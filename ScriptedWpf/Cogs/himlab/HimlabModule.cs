using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptedWpf.Core;
using ScriptedWpf.Models;

namespace ScriptedWpf.Cogs.Himlab;

public sealed class HimlabModule : IModule
{
    static readonly Dictionary<char, ushort> _vkMap = new()
    {
        ['W'] = 0x57, ['A'] = 0x41, ['S'] = 0x53, ['D'] = 0x44,
    };

    // SSD above this = low confidence warning (~30% pixels different)
    static readonly double BadSsdThreshold = HimlabLogic.MaxSSD * 0.30;

    volatile bool               _running;
    Action<string>              _log       = Console.WriteLine;
    HimlabConfig                _cfg       = HimlabConfig.Load();
    Dictionary<char, System.Drawing.Bitmap> _templates = new();

    // ── IModule ───────────────────────────────────────────────────────────────
    public string Id        => "himlab";
    public bool   IsRunning => _running;

    public event Action? StateChanged;

    public void Initialize(Action<string> log)
    {
        _log      = log;
        _templates = HimlabLogic.LoadTemplates();
    }

    public void Start()
    {
        if (_running) return;

        if (_templates.Count < 4)
        {
            _log($"[Хімлаб] ❌ Не всі шаблони готові ({_templates.Count}/4). Налаштуй шаблони в налаштуваннях.");
            ShowNotConfiguredNotification();
            return;
        }

        _running = true;
        new Thread(Run) { IsBackground = true }.Start();
        ShowStartNotification();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _log("[Хімлаб] Зупинено.");
        ShowStopNotification();
        StateChanged?.Invoke();
    }

    void ShowStartNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Скрипт Himlab", "Вмикаюсь...", ToastIcon.Success);
    }

    void ShowStopNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Скрипт Himlab", "Вимикаюсь...", ToastIcon.Info);
    }

    void ShowNotConfiguredNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Скрипт Himlab", "Скрипт не налаштований", ToastIcon.Warning);
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F9, () =>
        {
            if (IsRunning) Stop();
            else Start();
        });
    }

    public FrameworkElement? GetSettingsView() => BuildSettings();

    // ── Background loop ───────────────────────────────────────────────────────
    void Run()
    {
        _log("[Хімлаб] Фоновий режим увімкнено. Очікую капчу...");

        while (_running)
        {
            try
            {
                var region = HimlabLogic.GetCaptureRegion(_cfg.MonitorIndex, _cfg);
                // ── Waiting phase: poll until captcha appears ──────────────────
                if (!HimlabLogic.IsCaptchaVisible(region))
                {
                    Thread.Sleep(300);
                    continue;
                }

                // ── Solving phase ──────────────────────────────────────────────
                _log("[Хімлаб] Капча виявлена! Починаю прохід...");
                int step    = 0;
                int maxStep = 15;

                while (_running && step < maxStep)
                {
                    // Single capture reused for both visibility check and letter matching
                    using var raw = HimlabLogic.Capture(region);
                    if (!HimlabLogic.IsCaptchaVisible(raw)) break;

                    step++;

                    // Detect letter — up to 3 attempts
                    char   letter = '?';
                    double ssd    = double.MaxValue;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        using var processed = HimlabLogic.Preprocess(raw);
                        (letter, ssd) = HimlabLogic.MatchLetter(processed, _templates);
                        if (letter != '?') break;
                        if (attempt < 2) Thread.Sleep(50);
                    }

                    if (letter == '?')
                    {
                        _log($"[{step}] ❌ Не вдалося розпізнати літеру — пропускаю");
                        Thread.Sleep(300);
                        continue;
                    }

                    double confidence = (1.0 - ssd / HimlabLogic.MaxSSD) * 100.0;
                    string conf = confidence < 70.0 ? $" ⚠ довіра {confidence:F0}%" : "";
                    _log($"[{step}] ✅ [{letter}] — тримаю {_cfg.HoldMs}мс{conf}");

                    HoldKey(_vkMap[letter], _cfg.HoldMs);

                    // Wait for transition animation
                    if (_running) Thread.Sleep(_cfg.TransitionMs);
                }

                if (step >= maxStep)
                    _log($"[Хімлаб] ⚠ Досягнуто ліміт {maxStep} кроків — перевір гру");
                else
                    _log($"[Хімлаб] Капча пройдена за {step} кроків ✅ Очікую наступну...");
            }
            catch (Exception ex)
            {
                _log($"[Хімлаб] ⚠ Помилка захоплення екрану: {ex.Message} — повторюю через 1с");
                Thread.Sleep(1000);
            }
        }

        _log("[Хімлаб] Фоновий режим вимкнено.");
        _running = false;
        StateChanged?.Invoke();
    }

    static void HoldKey(ushort vk, int holdMs)
    {
        int sz = Marshal.SizeOf<WinApi.INPUT>();

        var down = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk } }
        };
        var up = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = WinApi.KEYEVENTF_KEYUP } }
        };

        WinApi.SendInput(1, [down], sz);
        Thread.Sleep(holdMs);
        WinApi.SendInput(1, [up], sz);
    }

    // ── Settings View ─────────────────────────────────────────────────────────
    FrameworkElement BuildSettings()
    {
        var info     = ModuleInfo.Load("himlab");
        var accent   = (Brush)Application.Current.Resources["AccentBrush"];
        var textSec  = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim  = (Brush)Application.Current.Resources["TextDimBrush"];
        var borderB  = (Brush)Application.Current.Resources["BorderBrush"];

        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;

        // ── Інструкція ────────────────────────────────────────────────────────
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instrBlock = new TextBlock
        {
            Text = "ІНСТРУКЦІЯ\n" + info.Instruction,
            Foreground = textDim, FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instrBlock, row); Grid.SetColumnSpan(instrBlock, 2);
        grid.Children.Add(instrBlock);
        row++;

        void AddRow(string label, FrameworkElement input, string? hint = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            var lbl = new TextBlock { Text = label, Foreground = textSec, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            input.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            grid.Children.Add(lbl); grid.Children.Add(input);
            row++;

            if (hint != null)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var hintBlock = new TextBlock
                {
                    Text = hint, Foreground = textDim, FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 6),
                };
                Grid.SetRow(hintBlock, row); Grid.SetColumnSpan(hintBlock, 2);
                grid.Children.Add(hintBlock);
                row++;
            }
        }

        // Monitor
        var monCb = new ComboBox { Width = 220, Height = 28 };
        monCb.Items.Add("Авто");
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            string friendly = WinApi.GetMonitorFriendlyName(screens[i].DeviceName);
            if (string.IsNullOrWhiteSpace(friendly) || friendly == screens[i].DeviceName)
                friendly = $"Дисплей {i + 1}";
            monCb.Items.Add($"{friendly}  {screens[i].Bounds.Width}×{screens[i].Bounds.Height}");
        }
        monCb.SelectedIndex = Math.Clamp(_cfg.MonitorIndex + 1, 0, monCb.Items.Count - 1);
        monCb.SelectionChanged += (_, _) => { _cfg.MonitorIndex = monCb.SelectedIndex - 1; _cfg.Save(); };
        AddRow("Монітор", monCb, "На якому моніторі шукати капчу");

        // Hold duration
        AddRow("Утримання клавіші (мс)", IntBox(_cfg.HoldMs, v => { _cfg.HoldMs = v; _cfg.Save(); }),
            "Скільки мс тримати клавішу. Збільш якщо капча не спрацьовує (за замовч. 600)");

        // Transition delay
        AddRow("Пауза між кроками (мс)", IntBox(_cfg.TransitionMs, v => { _cfg.TransitionMs = v; _cfg.Save(); }),
            "Затримка після кожної клавіші — час на анімацію переходу (за замовч. 150)");

        var notifCb = new CheckBox
        {
            Content    = "Показувати сповіщення",
            IsChecked  = _cfg.ShowNotifications,
            Foreground = textSec,
        };
        notifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        notifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };
        AddRow("Сповіщення", notifCb);

        // ── Templates section ────────────────────────────────────────────────
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) }); row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tmplLbl = new TextBlock
        {
            Text = "ШАБЛОНИ",
            FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = textDim,
            Margin = new Thickness(0, 4, 0, 8),
        };
        Grid.SetRow(tmplLbl, row); Grid.SetColumnSpan(tmplLbl, 2);
        grid.Children.Add(tmplLbl);
        row++;

        // Hint text
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var hint = new TextBlock
        {
            Text = info.Hint("templatesHint"),
            Foreground = textDim, FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        Grid.SetRow(hint, row); Grid.SetColumnSpan(hint, 2);
        grid.Children.Add(hint);
        row++;

        // One row per letter
        foreach (char letter in HimlabLogic.Letters)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            bool hasTemplate = _templates.ContainsKey(letter);

            var statusText = new TextBlock
            {
                Text       = hasTemplate ? "✅ Готово" : "❌ Немає",
                Foreground = hasTemplate ? accent : textDim,
                FontSize   = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var captureBtn = new Button
            {
                Content = $"[{letter}]  Захопити",
                Height  = 26,
                Padding = new Thickness(10, 0, 10, 0),
                Cursor  = System.Windows.Input.Cursors.Hand,
                FontSize = 11,
            };

            char captured = letter; // capture for closure
            captureBtn.Click += async (_, _) =>
            {
                captureBtn.IsEnabled = false;
                for (int i = 3; i > 0; i--)
                {
                    captureBtn.Content = $"[{captured}]  {i}...";
                    await Task.Delay(1000);
                }

                captureBtn.Content = $"[{captured}]  Захоплення...";

                // Minimize app so game is visible
                var win = Application.Current.MainWindow;
                win.WindowState = WindowState.Minimized;
                await Task.Delay(300);

                try
                {
                    var region = HimlabLogic.GetCaptureRegion(_cfg.MonitorIndex, _cfg);
                    using var raw       = HimlabLogic.Capture(region);
                    using var processed = HimlabLogic.Preprocess(raw);
                    HimlabLogic.SaveTemplate(captured, processed);

                    // Reload all templates
                    foreach (var bmp in _templates.Values) bmp.Dispose();
                    _templates = HimlabLogic.LoadTemplates();

                    statusText.Text       = "✅ Готово";
                    statusText.Foreground = accent;
                    _log($"[Хімлаб] Шаблон [{captured}] збережено");
                }
                catch (Exception ex)
                {
                    _log($"[Хімлаб] Помилка захоплення [{captured}]: {ex.Message}");
                }

                win.WindowState = WindowState.Normal;
                win.Activate();
                captureBtn.Content   = $"[{captured}]  Захопити";
                captureBtn.IsEnabled = true;
            };

            // Layout: [status] ............. [button]
            var letterRow = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(statusText,  Dock.Left);
            DockPanel.SetDock(captureBtn,  Dock.Right);
            letterRow.Children.Add(statusText);
            letterRow.Children.Add(captureBtn);

            Grid.SetRow(letterRow, row); Grid.SetColumnSpan(letterRow, 2);
            grid.Children.Add(letterRow);
            row++;
        }

        return grid;
    }

    static TextBox IntBox(int initial, Action<int> onChange)
    {
        var tb = new TextBox { Text = initial.ToString(), Width = 90, Height = 26 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) onChange(v);
            else tb.Text = initial.ToString();
        };
        return tb;
    }
}
