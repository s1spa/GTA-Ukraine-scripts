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

namespace ScriptedWpf.Cogs.Cda;

public sealed class CdaModule : IModule
{
    enum BotState { Idle, AutoPilot }

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
        // Починаємо завантаження PaddleOCR у фоні одразу при ініціалізації
        PaddleHelper.EnsureInit(_log);
    }

    public void Start()
    {
        if (_state != BotState.Idle) return;
        if (_cfg.X > 0 && _codeZone == null)
            _codeZone = new System.Drawing.Rectangle(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height);
        _state = BotState.AutoPilot;
        new Thread(RunAutoPilot) { IsBackground = true }.Start();
        Notify("Автопілот", "Запускаю...");
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
        // F7 — автопілот
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F7, () =>
        {
            if (_state == BotState.AutoPilot)
            {
                _log("[CDA] F7 → ВИМКНЕНО");
                Stop();
            }
            else if (_state == BotState.Idle)
            {
                _log("[CDA] F7 → АВТОПІЛОТ");
                Start();
            }
        });

    }

    // ── Settings View ─────────────────────────────────────────────────────────
    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    FrameworkElement BuildSettingsPanel()
    {
        var textSec = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim = (Brush)Application.Current.Resources["TextDimBrush"];

        var grid = new Grid { Margin = new Thickness(0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        int row = 0;

        // ── Інструкція ────────────────────────────────────────────────────────
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var instrBlock = new TextBlock
        {
            Text = "ІНСТРУКЦІЯ\n" +
                   "• F7 — вмикає / вимикає автопілот\n" +
                   "• Автопілот сам шукає найкраще замовлення, клікає та вводить код\n" +
                   "• При першому запуску завантажується PaddleOCR (~50 MB, кешується)",
            Foreground   = textDim,
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12),
        };
        Grid.SetRow(instrBlock, row); Grid.SetColumnSpan(instrBlock, 2);
        grid.Children.Add(instrBlock);
        row++;

        void AddRow(string label, FrameworkElement input)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            var lbl = new TextBlock { Text = label, Foreground = textSec, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            input.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(lbl, row);   Grid.SetColumn(lbl, 0);
            Grid.SetRow(input, row); Grid.SetColumn(input, 1);
            grid.Children.Add(lbl);
            grid.Children.Add(input);
            row++;
        }

        // Зона коду
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var zonePanel = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 0, 8) };

        var zoneLbl = new TextBlock { Foreground = textDim, FontSize = 11, Margin = new Thickness(0, 0, 0, 4) };
        void RefreshZoneLabel()
        {
            zoneLbl.Text = (_cfg.X > 0 && _cfg.Width > 0)
                ? $"Зона коду: ({_cfg.X}, {_cfg.Y}) → ({_cfg.X + _cfg.Width}, {_cfg.Y + _cfg.Height})"
                : "Зона коду: не задана";
        }
        RefreshZoneLabel();

        var zoneBtn = new Button
        {
            Content             = "Виділити зону коду",
            Padding             = new Thickness(12, 5, 12, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = Cursors.Hand,
        };
        zoneBtn.Click += (_, _) => OpenZoneSelector(zone =>
        {
            _codeZone   = zone;
            _cfg.X      = zone.X; _cfg.Y = zone.Y;
            _cfg.Width  = zone.Width; _cfg.Height = zone.Height;
            _cfg.Save();
            Application.Current.Dispatcher.Invoke(RefreshZoneLabel);
        });

        zonePanel.Children.Add(zoneLbl);
        zonePanel.Children.Add(zoneBtn);
        Grid.SetRow(zonePanel, row); Grid.SetColumnSpan(zonePanel, 2);
        grid.Children.Add(zonePanel);
        row++;

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
            _codeZone = null;
            _cfg.Save();
        };
        AddRow("Монітор", monCb);

        // Мін. ціна
        AddRow("Мін. ціна /km", IntBox(_cfg.MinPrice, v => { _cfg.MinPrice = v; _cfg.Save(); }));

        // Макс. тоннаж
        var tonCb = new ComboBox { Width = 100, Height = 28 };
        double[] tonOpts = { 0.5, 1.5, 3.0, 5.0 };
        foreach (var t in tonOpts) tonCb.Items.Add($"{t:F1} т");
        int tonIdx = Array.FindIndex(tonOpts, t => Math.Abs(t - _cfg.MaxTon) < 0.01);
        tonCb.SelectedIndex = tonIdx >= 0 ? tonIdx : tonOpts.Length - 1;
        tonCb.SelectionChanged += (_, _) =>
        {
            if (tonCb.SelectedIndex >= 0) { _cfg.MaxTon = tonOpts[tonCb.SelectedIndex]; _cfg.Save(); }
        };
        AddRow("Макс. тонн", tonCb);

        // Сповіщення
        var notifCb = new CheckBox { Content = "Показувати сповіщення", IsChecked = _cfg.ShowNotifications, Foreground = textSec };
        notifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        notifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };
        AddRow("Сповіщення", notifCb);

        // Типи вантажів
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); row++;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var typesLbl = new TextBlock
        {
            Text = "Типи вантажів", Foreground = textDim, FontSize = 10,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 4),
        };
        Grid.SetRow(typesLbl, row); Grid.SetColumnSpan(typesLbl, 2);
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
                Content   = t,
                IsChecked = _cfg.Types.Contains(t),
                Foreground = textSec,
                Margin    = new Thickness(0, 2, 16, 2),
            };
            cb.Checked   += (_, _) => { if (!_cfg.Types.Contains(captured)) { _cfg.Types.Add(captured); _cfg.Save(); } };
            cb.Unchecked += (_, _) => { _cfg.Types.Remove(captured); _cfg.Save(); };
            wrap.Children.Add(cb);
        }
        Grid.SetRow(wrap, row); Grid.SetColumnSpan(wrap, 2);
        grid.Children.Add(wrap);

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
                if (!PaddleHelper.IsReady)
                {
                    _log("[CDA] Очікую завантаження PaddleOCR...");
                    SleepChecked(1000, BotState.AutoPilot);
                    continue;
                }

                if (!waitingForMenu)
                {
                    var cards = OrderScanner.FindCards(_cfg.MonitorIndex,
                        isCancelled: () => _state != BotState.AutoPilot);

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
            _log($"[CDA] ❌ {ex.Message}");
            _state = BotState.Idle;
            StateChanged?.Invoke();
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
        if (!PaddleHelper.IsReady) return null;
        var found = PaddleHelper.FindDialogRegion(_cfg.MonitorIndex);
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
