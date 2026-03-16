using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Cda;

public sealed class CdaModule : IModule
{
    enum BotState { Idle, AutoPilot }

    volatile BotState  _state = BotState.Idle;
    Action<string>     _log   = Console.WriteLine;
    CdaConfig          _cfg         = CdaConfig.Load();
    System.Drawing.Rectangle? _codeZone;
    int _frameCounter;

    public Action<System.Drawing.Bitmap>? OnFrame { get; set; }

    // ── IModule ───────────────────────────────────────────────────────────────
    public string Id        => "cda";
    public bool   IsRunning => _state != BotState.Idle;

    public event Action? StateChanged;

    public void Initialize(Action<string> log) => _log = log;

    public void Start()
    {
        if (_state != BotState.Idle) return;
        if (_cfg.X > 0 && _codeZone == null)
            _codeZone = new System.Drawing.Rectangle(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height);
        _state = BotState.AutoPilot;
        new Thread(RunAutoPilot) { IsBackground = true }.Start();
        ShowStartNotification();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_state == BotState.Idle) return;
        _state = BotState.Idle;
        _log("АВТОПІЛОТ ВИМКНЕНО.");
        ShowStopNotification();
        StateChanged?.Invoke();
    }

    void ShowStartNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Скрипт CDA", "Вмикаюсь...", ToastIcon.Success);
    }

    void ShowStopNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Скрипт CDA", "Вимикаюсь...", ToastIcon.Info);
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F7, () =>
        {
            if (IsRunning)
            {
                _log("F7 → ВИМКНЕНО");
                Stop();
            }
            else
            {
                _log("F7 → УВІМКНЕНО");
                Start();
            }
        });
    }

    // ── Settings View (WPF) ──────────────────────────────────────────────────
    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    FrameworkElement BuildSettingsPanel()
    {
        var accent   = (Brush)Application.Current.Resources["AccentBrush"];
        var textSec  = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim  = (Brush)Application.Current.Resources["TextDimBrush"];
        var bgCard   = (Brush)Application.Current.Resources["BgCardBrush"];
        var border   = (Brush)Application.Current.Resources["BorderBrush"];

        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;

        // ── Інструкція ────────────────────────────────────────────────────────
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instrBlock = new TextBlock
        {
            Text = "ІНСТРУКЦІЯ\n" +
                   "1. Запусти гру дальнобійника та відкрий список замовлень\n" +
                   "2. Встанови фільтри нижче (мін. ціна, тоннаж, типи вантажів)\n" +
                   "3. F7 — увімкнути / вимкнути детектор кодів\n" +
                   "4. Скануй зону коду: натисни «Скан зони» і обведи\n" +
                   "   область де з'являється 6-значний код підтвердження",
            Foreground = textDim, FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(instrBlock, row); Grid.SetColumnSpan(instrBlock, 2);
        grid.Children.Add(instrBlock);
        row++;

        void AddRow(string label, FrameworkElement input)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });

            var lbl = new TextBlock
            {
                Text = label,
                Foreground = textSec,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
            };
            Grid.SetRow(lbl, row);
            Grid.SetColumn(lbl, 0);

            input.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);

            grid.Children.Add(lbl);
            grid.Children.Add(input);
            row++;
        }

        // Monitor selector — з реальними назвами через DeviceName
        var monCb = new ComboBox { Width = 220, Height = 28 };
        monCb.Items.Add("Авто");
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            string friendly = ScriptedWpf.Core.WinApi.GetMonitorFriendlyName(screens[i].DeviceName);
            if (string.IsNullOrWhiteSpace(friendly) || friendly == screens[i].DeviceName)
                friendly = $"Дисплей {i + 1}";
            monCb.Items.Add($"{friendly}  {screens[i].Bounds.Width}×{screens[i].Bounds.Height}");
        }
        monCb.SelectedIndex = Math.Clamp(_cfg.MonitorIndex + 1, 0, monCb.Items.Count - 1);
        monCb.SelectionChanged += (_, _) =>
        {
            _cfg.MonitorIndex = monCb.SelectedIndex - 1;
            _cfg.X = _cfg.Y = _cfg.Width = _cfg.Height = 0;
            _codeZone = null;
            _cfg.Save();
        };
        AddRow("Монітор", monCb);

        AddRow("Мін. ціна /km", IntBox(_cfg.MinPrice, v => { _cfg.MinPrice = v; _cfg.Save(); }));

        // Макс. тонн — фіксований dropdown
        var tonCb = new ComboBox { Width = 100, Height = 28 };
        double[] tonOptions = { 0.5, 1.5, 3.0, 5.0 };
        foreach (var t in tonOptions)
            tonCb.Items.Add($"{t:F1} т");
        int tonIdx = Array.FindIndex(tonOptions, t => Math.Abs(t - _cfg.MaxTon) < 0.01);
        tonCb.SelectedIndex = tonIdx >= 0 ? tonIdx : tonOptions.Length - 1;
        tonCb.SelectionChanged += (_, _) =>
        {
            if (tonCb.SelectedIndex >= 0)
            {
                _cfg.MaxTon = tonOptions[tonCb.SelectedIndex];
                _cfg.Save();
            }
        };
        AddRow("Макс. тонн", tonCb);

        var notifCb = new CheckBox
        {
            Content   = "Показувати сповіщення",
            IsChecked = _cfg.ShowNotifications,
            Foreground = textSec,
        };
        notifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        notifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };
        AddRow("Сповіщення", notifCb);

        // Types section
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var typesLbl = new TextBlock
        {
            Text = "Типи вантажів",
            Foreground = textDim,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(typesLbl, row);
        Grid.SetColumnSpan(typesLbl, 2);
        grid.Children.Add(typesLbl);
        row++;

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        string[] allTypes = { "Одяг", "Нафта", "Фармацевтика", "Різне", "Продукти", "Автозапчастини", "Інше" };
        foreach (var t in allTypes)
        {
            string captured = t;
            var cb = new CheckBox
            {
                Content = t,
                IsChecked = _cfg.Types.Contains(t),
                Foreground = textSec,
                Margin = new Thickness(0, 2, 16, 2),
            };
            cb.Checked   += (_, _) => { if (!_cfg.Types.Contains(captured)) { _cfg.Types.Add(captured); _cfg.Save(); } };
            cb.Unchecked += (_, _) => { _cfg.Types.Remove(captured); _cfg.Save(); };
            wrap.Children.Add(cb);
        }
        Grid.SetRow(wrap, row);
        Grid.SetColumnSpan(wrap, 2);
        grid.Children.Add(wrap);

        return grid;
    }

    static TextBox IntBox(int initial, Action<int> onChange)
    {
        var tb = MakeTextBox(initial.ToString());
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) onChange(v);
            else tb.Text = initial.ToString();
        };
        return tb;
    }


    static TextBox MakeTextBox(string text) => new()
    {
        Text = text,
        Width = 90,
        Height = 26,
    };

    // ── Autopilot loop ────────────────────────────────────────────────────────
    void RunAutoPilot()
    {
        try
        {
            _log($"АВТОПІЛОТ УВІМКНЕНО | Мін.ціна:{_cfg.MinPrice}$ | Макс.тонн:{_cfg.MaxTon}");

            bool  waitingForMenu = false;
            long  clickTime      = 0;

            while (_state == BotState.AutoPilot)
            {
                if (!waitingForMenu)
                {
                    var cards = OrderScanner.FindCards(_cfg.MonitorIndex);
                    if (cards.Count > 0)
                    {
                        var valid = new List<OrderScanner.OrderCard>();
                        foreach (var c in cards)
                        {
                            bool ok = c.PricePerKm >= _cfg.MinPrice
                                   && c.Tonnage > 0 && c.Tonnage <= _cfg.MaxTon
                                   && _cfg.Types.Contains(c.Type);

                            _log($"{(ok ? "✅" : "❌")} {c.Type} ({c.Tonnage}т, LVL{c.Level}) → {c.PricePerKm}$/km");
                            if (ok) valid.Add(c);
                        }

                        if (valid.Count > 0)
                        {
                            var best = valid.OrderByDescending(c => c.PricePerKm).First();
                            _log($"🏆 Найкращий: {best.Type} {best.PricePerKm}$/km — відкриваю...");
                            MouseInput.Click(best.ClickPoint.X, best.ClickPoint.Y);
                            Thread.Sleep(300);

                            var scr  = WinOcr.GetMonitorBounds(_cfg.MonitorIndex);
                            int btnX = scr.X + scr.Width / 2 + (int)(scr.Width * 0.075);
                            int btnY = scr.Y + scr.Height / 2 + (int)(scr.Height * 0.16);
                            _log("🖱 Натискаю 'Взяти замовлення'...");
                            MouseInput.Click(btnX, btnY);

                            waitingForMenu = true;
                            clickTime      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            _log("Очікую код підтвердження...");
                        }
                        else
                        {
                            _log("Жодне замовлення не підійшло. Очікую...");
                        }
                    }
                    Thread.Sleep(300);
                }
                else
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clickTime > 15000)
                    {
                        _log("Меню коду не з'явилося за 15с. Повертаюся до пошуку.");
                        _codeZone = null;
                        waitingForMenu = false;
                        continue;
                    }

                    if (_codeZone == null)
                    {
                        _codeZone = WinOcr.FindDialogRegion(_cfg.MonitorIndex);
                        if (_codeZone != null)
                        {
                            var z = _codeZone.Value;
                            _codeZone = new System.Drawing.Rectangle(z.X + 180, z.Y + 5, z.Width - 360, z.Height - 30);
                            _cfg.X = _codeZone.Value.X; _cfg.Y = _codeZone.Value.Y;
                            _cfg.Width = _codeZone.Value.Width; _cfg.Height = _codeZone.Value.Height;
                            _cfg.Save();
                        }
                    }

                    if (_codeZone != null)
                    {
                        using var img = ScreenCapture.Capture(_codeZone.Value);
                        if (_frameCounter++ % 5 == 0)
                            OnFrame?.Invoke((System.Drawing.Bitmap)img.Clone());
                        var (code, rawText) = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();
                        _log($"OCR: \"{rawText}\" → {(code ?? "не знайдено")}");
                        if (code != null)
                        {
                            _log($"Знайдено код: {code} — вводжу...");
                            Thread.Sleep(100);
                            KeyInput.TypeCode(code, turbo: true);
                            _log("Готово! Замовлення взято. 🚚");
                            _state = BotState.Idle;
                            ShowStopNotification();
                            StateChanged?.Invoke();
                            return;
                        }
                    }
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[ПОМИЛКА] {ex.Message}");
            _state = BotState.Idle;
            ShowStopNotification();
            StateChanged?.Invoke();
        }
    }
}
