using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ScriptedWpf.Core;
using ScriptedWpf.Models;

namespace ScriptedWpf.Cogs.Cda;

public sealed class CdaModule : IModule
{
    enum BotState { Idle, AutoPilot, ManualScan }

    volatile BotState _state     = BotState.Idle;
    Action<string>    _log       = Console.WriteLine;
    CdaConfig         _cfg       = CdaConfig.Load();
    System.Drawing.Rectangle? _codeZone;
    int _frameCounter;

    public Action<System.Drawing.Bitmap>? OnFrame { get; set; }

    // ── IModule ───────────────────────────────────────────────────────────────
    public string Id        => "cda";
    public bool   IsRunning => _state != BotState.Idle;

    public event Action? StateChanged;

    public void Initialize(Action<string> log)
    {
        _log = log;
        TessOcr.EnsureInit(_log);
    }

    public void Start()
    {
        if (_state != BotState.Idle) return;
        if (_cfg.X > 0 && _codeZone == null)
            _codeZone = new System.Drawing.Rectangle(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height);

        if (_cfg.Mode == "Manual")
        {
            _state = BotState.ManualScan;
            new Thread(RunManualScan) { IsBackground = true }.Start();
            Notify("Ручний режим", "Запускаю...");
        }
        else
        {
            _state = BotState.AutoPilot;
            new Thread(RunAutoPilot) { IsBackground = true }.Start();
            Notify("Автопілот", "Запускаю...");
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_state == BotState.Idle) return;
        _state = BotState.Idle;
        Notify("CDA", "Вимкнено.");
        StateChanged?.Invoke();
    }

    void Notify(string title, string msg)
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show($"CDA: {title}", msg, ToastIcon.Info);
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F7, () =>
        {
            if (_state != BotState.Idle)
            {
                _log("[CDA] F7 → ВИМКНЕНО");
                Stop();
            }
            else
            {
                _log($"[CDA] F7 → {(_cfg.Mode == "Manual" ? "РУЧНИЙ РЕЖИМ" : "АВТОПІЛОТ")}");
                Start();
            }
        });
    }

    // ── Settings View ─────────────────────────────────────────────────────────
    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    FrameworkElement BuildSettingsPanel()
    {
        var info    = ModuleInfo.Load("cda");
        var textSec = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim = (Brush)Application.Current.Resources["TextDimBrush"];

        var root = new StackPanel { Orientation = Orientation.Vertical };

        // ── Вибір режиму ──────────────────────────────────────────────────────
        var modeLbl = new TextBlock
        {
            Text = "РЕЖИМ", Foreground = textDim, FontSize = 10,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6),
        };
        var modeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        var rbManual = new RadioButton { Content = "Ввід коду (ручний)",    IsChecked = _cfg.Mode == "Manual", Foreground = textSec, Margin = new Thickness(0, 0, 20, 0) };
        var rbAuto   = new RadioButton { Content = "Повністю автоматичний", IsChecked = _cfg.Mode != "Manual", Foreground = textSec };
        modeRow.Children.Add(rbManual);
        modeRow.Children.Add(rbAuto);
        root.Children.Add(modeLbl);
        root.Children.Add(modeRow);

        // ── Панель ручного режиму ─────────────────────────────────────────────
        var manualPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Visibility  = _cfg.Mode == "Manual" ? Visibility.Visible : Visibility.Collapsed,
        };

        manualPanel.Children.Add(new TextBlock
        {
            Text         = info.Hint("manualZone"),
            Foreground   = textDim,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        });

        var mZoneLbl = new TextBlock { Foreground = textDim, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) };
        void RefreshManualZone() =>
            mZoneLbl.Text = (_cfg.X > 0 && _cfg.Width > 0)
                ? $"Зона коду: ({_cfg.X}, {_cfg.Y}) → ({_cfg.X + _cfg.Width}, {_cfg.Y + _cfg.Height})"
                : "Зона коду: не задана";
        RefreshManualZone();

        var mZoneBtn = new Button
        {
            Content = "Виділити зону коду (3 с)", Padding = new Thickness(12, 5, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4),
        };
        var mCountLbl = new TextBlock { Foreground = textDim, FontSize = 13, Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
        mZoneBtn.Click += (_, _) =>
        {
            mZoneBtn.IsEnabled   = false;
            mCountLbl.Visibility = Visibility.Visible;
            new Thread(() =>
            {
                for (int sec = 3; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(() => mCountLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    mCountLbl.Visibility = Visibility.Collapsed;
                    mZoneBtn.IsEnabled   = true;
                    OpenZoneSelector(z =>
                    {
                        _codeZone = z; _cfg.X = z.X; _cfg.Y = z.Y; _cfg.Width = z.Width; _cfg.Height = z.Height; _cfg.Save();
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            RefreshManualZone();
                            mCountLbl.Text       = $"Збережено: ({z.X}, {z.Y}) {z.Width}×{z.Height}";
                            mCountLbl.Visibility = Visibility.Visible;
                        });
                    });
                });
            }) { IsBackground = true }.Start();
        };

        var turboCb = new CheckBox { Content = "Turbo-режим (без затримок між цифрами)", IsChecked = _cfg.Turbo, Foreground = textSec, Margin = new Thickness(0, 0, 0, 6) };
        turboCb.Checked   += (_, _) => { _cfg.Turbo = true;  _cfg.Save(); };
        turboCb.Unchecked += (_, _) => { _cfg.Turbo = false; _cfg.Save(); };

        var mNotifCb = new CheckBox { Content = "Показувати сповіщення", IsChecked = _cfg.ShowNotifications, Foreground = textSec };
        mNotifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        mNotifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };

        manualPanel.Children.Add(mZoneLbl);
        manualPanel.Children.Add(mZoneBtn);
        manualPanel.Children.Add(mCountLbl);
        manualPanel.Children.Add(turboCb);
        manualPanel.Children.Add(mNotifCb);
        root.Children.Add(manualPanel);

        // ── Панель автоматичного режиму ───────────────────────────────────────
        var autoPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Visibility  = _cfg.Mode != "Manual" ? Visibility.Visible : Visibility.Collapsed,
        };

        Grid LabelRow(string label, FrameworkElement ctrl)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock { Text = label, Foreground = textSec, VerticalAlignment = VerticalAlignment.Center };
            ctrl.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(lbl, 0); Grid.SetColumn(ctrl, 1);
            g.Children.Add(lbl); g.Children.Add(ctrl);
            return g;
        }

        // Монітор
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
        monCb.SelectionChanged += (_, _) =>
        {
            _cfg.MonitorIndex = monCb.SelectedIndex - 1;
            _cfg.X = _cfg.Y = _cfg.Width = _cfg.Height = 0;
            _codeZone = null; _cfg.Save();
        };
        autoPanel.Children.Add(LabelRow("Монітор:", monCb));

        // Мін. ціна
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal };
        var priceTb  = new TextBox { Text = _cfg.MinPrice.ToString(), Width = 90, Height = 26, VerticalAlignment = VerticalAlignment.Center };
        priceTb.LostFocus += (_, _) =>
        {
            if (int.TryParse(priceTb.Text, out int v)) { _cfg.MinPrice = v; _cfg.Save(); }
            else priceTb.Text = _cfg.MinPrice.ToString();
        };
        priceRow.Children.Add(priceTb);
        priceRow.Children.Add(new TextBlock { Text = " $", Foreground = textDim, VerticalAlignment = VerticalAlignment.Center });
        autoPanel.Children.Add(LabelRow("Мін. ціна /km:", priceRow));

        // Макс. тоннаж
        var tonCb = new ComboBox { Width = 120, Height = 28 };
        double[] tonOpts = { 0.5, 1.5, 3.0, 5.0 };
        foreach (var t in tonOpts) tonCb.Items.Add(t < 5.0 ? $"{t:F1} т" : $"{(int)t} т (всі)");
        int tonIdx = Array.FindIndex(tonOpts, t => Math.Abs(t - _cfg.MaxTon) < 0.01);
        tonCb.SelectedIndex = tonIdx >= 0 ? tonIdx : tonOpts.Length - 1;
        tonCb.SelectionChanged += (_, _) =>
        {
            if (tonCb.SelectedIndex >= 0) { _cfg.MaxTon = tonOpts[tonCb.SelectedIndex]; _cfg.Save(); }
        };
        autoPanel.Children.Add(LabelRow("Макс. тоннаж:", tonCb));

        // Типи вантажів
        autoPanel.Children.Add(new TextBlock { Text = "Типи вантажів:", Foreground = textSec, Margin = new Thickness(0, 4, 0, 4) });
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        string[] allTypes = { "Фармацевтика", "Одяг", "Продукти", "Різне", "Інше", "Автозапчастини", "Нафта", "Нелегальне" };
        foreach (var t in allTypes)
        {
            string cap = t;
            var cb = new CheckBox { Content = t, IsChecked = _cfg.Types.Contains(t), Foreground = textSec, Margin = new Thickness(0, 2, 12, 2) };
            cb.Checked   += (_, _) => { if (!_cfg.Types.Contains(cap)) { _cfg.Types.Add(cap); _cfg.Save(); } };
            cb.Unchecked += (_, _) => { _cfg.Types.Remove(cap); _cfg.Save(); };
            wrap.Children.Add(cb);
        }
        autoPanel.Children.Add(wrap);

        // Зона коду (резервна)
        autoPanel.Children.Add(new TextBlock
        {
            Text         = info.Hint("autoZone"),
            Foreground   = textDim,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 8, 0, 8),
        });

        var aZoneLbl = new TextBlock { Foreground = textDim, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
        void RefreshAutoZone() =>
            aZoneLbl.Text = (_cfg.X > 0 && _cfg.Width > 0)
                ? $"Зона коду: ({_cfg.X}, {_cfg.Y}) → ({_cfg.X + _cfg.Width}, {_cfg.Y + _cfg.Height})"
                : "Зона коду визначається автоматично. Вкажіть резервну зону якщо авто-пошук не спрацює.";
        RefreshAutoZone();

        var aZoneBtn = new Button
        {
            Content = "Виділити зону коду (резервна, 3 с)", Padding = new Thickness(12, 5, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Left, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 4),
        };
        var aCountLbl = new TextBlock { Foreground = textDim, FontSize = 13, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        aZoneBtn.Click += (_, _) =>
        {
            aZoneBtn.IsEnabled   = false;
            aCountLbl.Visibility = Visibility.Visible;
            new Thread(() =>
            {
                for (int sec = 3; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(() => aCountLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    aCountLbl.Visibility = Visibility.Collapsed;
                    aZoneBtn.IsEnabled   = true;
                    OpenZoneSelector(z =>
                    {
                        _codeZone = z; _cfg.X = z.X; _cfg.Y = z.Y; _cfg.Width = z.Width; _cfg.Height = z.Height; _cfg.Save();
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            RefreshAutoZone();
                            aCountLbl.Text       = $"Збережено: ({z.X}, {z.Y}) {z.Width}×{z.Height}";
                            aCountLbl.Visibility = Visibility.Visible;
                        });
                    });
                });
            }) { IsBackground = true }.Start();
        };

        var aNotifCb = new CheckBox { Content = "Показувати сповіщення", IsChecked = _cfg.ShowNotifications, Foreground = textSec };
        aNotifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        aNotifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };

        // ── Звукове сповіщення ────────────────────────────────────────────────
        var soundCb = new CheckBox { Content = "Звукове сповіщення при взятті замовлення", IsChecked = _cfg.SoundEnabled, Foreground = textSec, Margin = new Thickness(0, 8, 0, 4) };

        var soundPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4),
            Visibility = _cfg.SoundEnabled ? Visibility.Visible : Visibility.Collapsed };

        var builtIn = CdaSound.GetBuiltInFiles();
        var soundCmb = new ComboBox { Width = 200, Height = 28, Margin = new Thickness(0, 0, 8, 0) };

        // Заповнюємо вбудовані звуки
        int selectedIdx = 0;
        for (int i = 0; i < builtIn.Length; i++)
        {
            string fileName = System.IO.Path.GetFileNameWithoutExtension(builtIn[i]);
            soundCmb.Items.Add(new ComboBoxItem { Content = fileName, Tag = System.IO.Path.GetFileName(builtIn[i]) });
            if (System.IO.Path.GetFileName(builtIn[i]) == _cfg.SoundFile) selectedIdx = i;
        }

        // Якщо поточний файл — кастомний (абсолютний шлях)
        if (System.IO.Path.IsPathRooted(_cfg.SoundFile) && System.IO.File.Exists(_cfg.SoundFile))
        {
            soundCmb.Items.Add(new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(_cfg.SoundFile), Tag = _cfg.SoundFile });
            selectedIdx = soundCmb.Items.Count - 1;
        }
        soundCmb.SelectedIndex = selectedIdx;
        soundCmb.SelectionChanged += (_, _) =>
        {
            if (soundCmb.SelectedItem is ComboBoxItem item)
            { _cfg.SoundFile = (string)item.Tag; _cfg.Save(); }
        };

        var soundBrowseBtn = new Button { Content = "Свій файл...", Padding = new Thickness(10, 4, 10, 4), Cursor = Cursors.Hand };
        soundBrowseBtn.Click += (_, _) =>
        {
            var dlg = new System.Windows.Forms.OpenFileDialog
            {
                Title  = "Вибрати звук",
                Filter = "Аудіо файли|*.mp3;*.wav;*.ogg|Всі файли|*.*",
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string path = dlg.FileName;
                // Перевіряємо чи вже є в списку
                bool found = false;
                foreach (ComboBoxItem it in soundCmb.Items)
                    if ((string)it.Tag == path) { soundCmb.SelectedItem = it; found = true; break; }
                if (!found)
                {
                    var newItem = new ComboBoxItem { Content = System.IO.Path.GetFileNameWithoutExtension(path), Tag = path };
                    soundCmb.Items.Add(newItem);
                    soundCmb.SelectedItem = newItem;
                }
                _cfg.SoundFile = path; _cfg.Save();
            }
        };

        soundPanel.Children.Add(soundCmb);
        soundPanel.Children.Add(soundBrowseBtn);

        soundCb.Checked   += (_, _) => { _cfg.SoundEnabled = true;  _cfg.Save(); soundPanel.Visibility = Visibility.Visible; };
        soundCb.Unchecked += (_, _) => { _cfg.SoundEnabled = false; _cfg.Save(); soundPanel.Visibility = Visibility.Collapsed; };

        autoPanel.Children.Add(aZoneLbl);
        autoPanel.Children.Add(aZoneBtn);
        autoPanel.Children.Add(aCountLbl);
        autoPanel.Children.Add(aNotifCb);
        autoPanel.Children.Add(soundCb);
        autoPanel.Children.Add(soundPanel);
        root.Children.Add(autoPanel);

        // ── Перемикання режиму ────────────────────────────────────────────────
        rbManual.Checked += (_, _) =>
        {
            _cfg.Mode = "Manual"; _cfg.Save();
            manualPanel.Visibility = Visibility.Visible;
            autoPanel.Visibility   = Visibility.Collapsed;
        };
        rbAuto.Checked += (_, _) =>
        {
            _cfg.Mode = "Auto"; _cfg.Save();
            manualPanel.Visibility = Visibility.Collapsed;
            autoPanel.Visibility   = Visibility.Visible;
        };

        return root;
    }

    // ── Autopilot loop ────────────────────────────────────────────────────────
    void RunAutoPilot()
    {
        try
        {
            _log($"[CDA] АВТОПІЛОТ | Мін.ціна:{_cfg.MinPrice}$/km | Макс.тонн:{_cfg.MaxTon}т");

            bool waitingForMenu = false;
            long clickTime      = 0;

            while (_state == BotState.AutoPilot)
            {
                if (!TessOcr.IsReady)
                {
                    _log("[CDA] Очікую завантаження PaddleOCR...");
                    SleepChecked(1000, BotState.AutoPilot);
                    continue;
                }

                if (!waitingForMenu)
                {
                    var cards = OrderScanner.FindCards(_cfg.MonitorIndex,
                        isCancelled: () => _state != BotState.AutoPilot,
                        log: _log);

                    if (_state != BotState.AutoPilot) break;

                    if (cards.Count == 0)
                    {
                        _log("[CDA] Замовлень не знайдено. Чекаю...");
                        SleepChecked(500, BotState.AutoPilot);
                        continue;
                    }

                    var valid = new List<OrderScanner.OrderCard>();
                    foreach (var c in cards)
                    {
                        bool tonOk   = c.Tonnage > 0 && c.Tonnage <= _cfg.MaxTon;
                        bool typeOk  = _cfg.Types.Contains(c.Type);
                        bool priceOk = c.PricePerKm >= _cfg.MinPrice;
                        bool ok      = tonOk && typeOk && priceOk;

                        _log($"[CDA] {(ok ? "✅" : "❌")} {c.Type} {c.Tonnage}т → {c.PricePerKm}$/km" +
                             $" [тонаж:{(tonOk?"OK":"X")} тип:{(typeOk?"OK":"X")} ціна:{(priceOk?"OK":"X")}]");

                        if (ok) valid.Add(c);
                    }

                    if (valid.Count > 0)
                    {
                        var best = valid.OrderByDescending(c => c.PricePerKm).First();
                        _log($"[CDA] 🏆 Найкраще: {best.Type} {best.Tonnage}т → {best.PricePerKm}$/km");

                        MouseInput.Click(best.ClickPoint.X, best.ClickPoint.Y);
                        SleepChecked(400, BotState.AutoPilot);
                        if (_state != BotState.AutoPilot) break;

                        var scr  = WinOcr.GetMonitorBounds(_cfg.MonitorIndex);
                        int btnX = scr.X + scr.Width  / 2 + (int)(scr.Width  * 0.075);
                        int btnY = scr.Y + scr.Height / 2 + (int)(scr.Height * 0.16);
                        _log("[CDA] 🖱 Натискаю 'Взяти замовлення'...");
                        MouseInput.Click(btnX, btnY);

                        waitingForMenu = true;
                        clickTime      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        _log("[CDA] Очікую код підтвердження...");
                    }
                    else
                    {
                        _log("[CDA] Підходящих замовлень немає. Чекаю...");
                        SleepChecked(500, BotState.AutoPilot);
                    }
                }
                else
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clickTime > 15_000)
                    {
                        _log("[CDA] Меню коду не з'явилося за 15с. Повертаюся до пошуку.");
                        waitingForMenu = false;
                        continue;
                    }

                    var zone = GetOrFindCodeZone();
                    if (zone != null)
                    {
                        using var img = ScreenCapture.Capture(zone.Value);
                        if (_frameCounter++ % 5 == 0) OnFrame?.Invoke((System.Drawing.Bitmap)img.Clone());

                        var (code, rawText) = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();
                        _log($"[CDA] OCR: \"{rawText}\" → {code ?? "не знайдено"}");
                        if (code != null)
                        {
                            _log($"[CDA] ✅ Код: {code} — вводжу...");
                            Thread.Sleep(100);
                            KeyInput.TypeCode(code);
                            _log("[CDA] Готово! 🚚");
                            if (_cfg.SoundEnabled) CdaSound.Play(_cfg.SoundFile);
                            _state = BotState.Idle;
                            Notify("Готово!", $"Код {code} введено.");
                            StateChanged?.Invoke();
                            return;
                        }
                    }
                    Thread.Sleep(60);
                }
            }

            _log("[CDA] Автопілот зупинено.");
            _state = BotState.Idle;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"[CDA] ❌ {ex.GetType().Name}: {ex.Message}");
            _state = BotState.Idle;
            StateChanged?.Invoke();
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "CDA — Помилка", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    // ── Manual Scan loop ──────────────────────────────────────────────────────
    void RunManualScan()
    {
        try
        {
            _log("[CDA] РУЧНИЙ РЕЖИМ | Сканую зону коду...");
            string? lastCode       = null;
            long    cooldownUntil  = 0;
            int     noCodeStreak   = 0;

            while (_state == BotState.ManualScan)
            {
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < cooldownUntil)
                { Thread.Sleep(50); continue; }

                var zone = GetOrFindCodeZone();
                if (zone == null)
                {
                    _log("[CDA] Зона не задана. Виділіть зону коду в налаштуваннях.");
                    SleepChecked(1000, BotState.ManualScan);
                    continue;
                }

                using var img = ScreenCapture.Capture(zone.Value);
                var (code, rawText) = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();

                if (code != null)
                {
                    noCodeStreak = 0;
                    if (code != lastCode)
                    {
                        _log($"[CDA] Код: {code} — вводжу...");
                        Thread.Sleep(100);
                        KeyInput.TypeCode(code, _cfg.Turbo);
                        _log("[CDA] Готово!");
                        lastCode      = code;
                        cooldownUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
                        Notify("Код введено", code);
                    }
                }
                else
                {
                    if (++noCodeStreak >= 5) { lastCode = null; noCodeStreak = 0; }
                    Thread.Sleep(60);
                }
            }

            _log("[CDA] Ручний режим зупинено.");
            _state = BotState.Idle;
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log($"[CDA] ❌ {ex.GetType().Name}: {ex.Message}");
            _state = BotState.Idle;
            StateChanged?.Invoke();
            Application.Current.Dispatcher.Invoke(() =>
                MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "CDA — Помилка", MessageBoxButton.OK, MessageBoxImage.Error));
        }
    }

    // ── Zone Selector ─────────────────────────────────────────────────────────
    void OpenZoneSelector(Action<System.Drawing.Rectangle> onSelected)
    {
        var screen = System.Windows.Forms.Screen.AllScreens
            .ElementAtOrDefault(Math.Max(0, _cfg.MonitorIndex))
            ?? System.Windows.Forms.Screen.PrimaryScreen!;

        // Отримуємо DPI головного вікна для конвертації logical → physical px
        double dpiX = 1, dpiY = 1;
        var mainSrc = PresentationSource.FromVisual(Application.Current.MainWindow);
        if (mainSrc != null)
        {
            dpiX = mainSrc.CompositionTarget.TransformToDevice.M11;
            dpiY = mainSrc.CompositionTarget.TransformToDevice.M22;
        }

        // Розмір вікна в логічних пікселях WPF
        double logW = screen.Bounds.Width  / dpiX;
        double logH = screen.Bounds.Height / dpiY;

        var overlay = new Window
        {
            WindowStyle        = WindowStyle.None,
            AllowsTransparency = true,
            Background         = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)),
            Topmost            = true,
            Left               = screen.Bounds.Left / dpiX,
            Top                = screen.Bounds.Top  / dpiY,
            Width              = logW,
            Height             = logH,
            Cursor             = Cursors.Cross,
            ShowInTaskbar      = false,
            ResizeMode         = ResizeMode.NoResize,
        };

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(new TextBlock
        {
            Text                = "Виділіть зону де з'являється код  •  Esc — скасувати",
            Foreground          = Brushes.White,
            FontSize            = 15,
            Background          = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding             = new Thickness(14, 6, 14, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Top,
            Margin              = new Thickness(0, 20, 0, 0),
            IsHitTestVisible    = false,
        });

        var canvas = new Canvas { Width = logW, Height = logH, Background = Brushes.Transparent };
        root.Children.Add(canvas);
        overlay.Content = root;

        var selRect = new Rectangle
        {
            Stroke           = Brushes.LimeGreen,
            StrokeThickness  = 2,
            Fill             = new SolidColorBrush(Color.FromArgb(40, 0, 255, 0)),
            Visibility       = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        canvas.Children.Add(selRect);

        System.Windows.Point origin = default;
        bool dragging = false;

        canvas.MouseLeftButtonDown += (_, e) =>
        {
            origin   = e.GetPosition(canvas);
            dragging = true;
            selRect.Visibility = Visibility.Visible;
            Canvas.SetLeft(selRect, origin.X); Canvas.SetTop(selRect, origin.Y);
            selRect.Width = selRect.Height = 0;
            canvas.CaptureMouse();
        };

        canvas.MouseMove += (_, e) =>
        {
            if (!dragging) return;
            var p = e.GetPosition(canvas);
            Canvas.SetLeft(selRect, Math.Min(p.X, origin.X));
            Canvas.SetTop(selRect,  Math.Min(p.Y, origin.Y));
            selRect.Width  = Math.Abs(p.X - origin.X);
            selRect.Height = Math.Abs(p.Y - origin.Y);
        };

        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            canvas.ReleaseMouseCapture();

            var p = e.GetPosition(canvas);
            // Конвертуємо logical px → physical px
            int rx = (int)(Math.Min(p.X, origin.X) * dpiX) + screen.Bounds.Left;
            int ry = (int)(Math.Min(p.Y, origin.Y) * dpiY) + screen.Bounds.Top;
            int rw = (int)(Math.Abs(p.X - origin.X) * dpiX);
            int rh = (int)(Math.Abs(p.Y - origin.Y) * dpiY);

            overlay.Close();

            if (rw > 10 && rh > 10)
                onSelected(new System.Drawing.Rectangle(rx, ry, rw, rh));
        };

        overlay.KeyDown += (_, e) => { if (e.Key == Key.Escape) overlay.Close(); };
        overlay.ShowDialog();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    System.Drawing.Rectangle? GetOrFindCodeZone()
    {
        if (_codeZone != null) return _codeZone;

        // Спочатку — збережена зона з конфігу
        if (_cfg.X > 0 && _cfg.Width > 0)
        {
            _codeZone = new System.Drawing.Rectangle(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height);
            return _codeZone;
        }

        // Авто-пошук через PaddleOCR (якщо зону не виділено вручну)
        if (!TessOcr.IsReady) return null;
        var found = TessOcr.FindDialogRegion(_cfg.MonitorIndex);
        if (found == null) return null;

        var z = found.Value;
        _codeZone   = new System.Drawing.Rectangle(z.X + 180, z.Y + 5, z.Width - 360, z.Height - 30);
        _cfg.X      = _codeZone.Value.X;
        _cfg.Y      = _codeZone.Value.Y;
        _cfg.Width  = _codeZone.Value.Width;
        _cfg.Height = _codeZone.Value.Height;
        _cfg.Save();
        return _codeZone;
    }

    void SleepChecked(int ms, BotState expectedState)
    {
        int steps = Math.Max(1, ms / 50);
        for (int i = 0; i < steps && _state == expectedState; i++)
            Thread.Sleep(50);
    }
}
