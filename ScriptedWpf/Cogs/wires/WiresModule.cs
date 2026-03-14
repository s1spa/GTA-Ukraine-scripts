using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfBrush  = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using ScriptedWpf.Core;
using ScriptedWpf.Cogs.Cda;

namespace ScriptedWpf.Cogs.Wires;

public sealed class WiresModule : IModule
{
    enum BotState { Idle, Running }

    volatile BotState _state = BotState.Idle;
    Action<string>    _log   = Console.WriteLine;
    WiresConfig       _cfg   = WiresConfig.Load();
    int _debugFrame = 0;

    public string Id        => "wires";
    public bool   IsRunning => _state != BotState.Idle;

    public event Action? StateChanged;

    public void Initialize(Action<string> log) => _log = log;

    public void Start()
    {
        if (_state != BotState.Idle) return;
        _state = BotState.Running;
        new Thread(Run) { IsBackground = true }.Start();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (_state == BotState.Idle) return;
        _state = BotState.Idle;
        _log("Wire Connector вимкнено.");
        StateChanged?.Invoke();
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F8, () =>
        {
            if (IsRunning) { _log("F8 → ВИМКНЕНО"); Stop(); }
            else           { _log("F8 → УВІМКНЕНО"); Start(); }
        });
    }

    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    // ── Панель налаштувань (компактна, у sidebar) ─────────────────────────────
    FrameworkElement BuildSettingsPanel()
    {
        var textSec = (WpfBrush)Application.Current.Resources["TextSecondaryBrush"];
        var stack = new StackPanel { Orientation = Orientation.Vertical };

        // Монітор
        var monRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        monRow.Children.Add(new TextBlock { Text = "Монітор:", Foreground = textSec, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
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
        monRow.Children.Add(monCb);
        stack.Children.Add(monRow);

        // Кнопка калібрування з відліком
        var calibBtn = new System.Windows.Controls.Button
        {
            Content = "🎯 Калібрувати (5с)", Height = 36,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 0, 16, 0)
        };
        stack.Children.Add(calibBtn);

        var countdownLbl = new TextBlock
        {
            Foreground = textSec, FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed
        };
        stack.Children.Add(countdownLbl);

        calibBtn.Click += (_, _) =>
        {
            calibBtn.IsEnabled = false;
            countdownLbl.Visibility = Visibility.Visible;

            // Відлік у фоновому потоці, оновлення UI через Dispatcher
            new Thread(() =>
            {
                for (int sec = 5; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(() =>
                        countdownLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }

                // Скрін
                var screen = GetScreen();
                using var bmp = ScreenCapture.Capture(screen);
                double iW = bmp.Width, iH = bmp.Height;

                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                var bi = new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = ms; bi.EndInit(); bi.Freeze();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    countdownLbl.Visibility = Visibility.Collapsed;
                    calibBtn.IsEnabled = true;
                    OpenCalibrationWindow(bi, iW, iH);
                });
            }) { IsBackground = true }.Start();
        };

        return stack;
    }

    // ── Повноекранне вікно калібровки ─────────────────────────────────────────
    void OpenCalibrationWindow(System.Windows.Media.Imaging.BitmapImage screenshot, double imgW, double imgH)
    {
        // Визначаємо монітор і його реальний розмір у device pixels
        var screen   = GetScreen();
        var dpiScale = 1.0; // WPF logical units; скрін робиться у device px
        // Щоб canvas 1:1 відповідав скріншоту — розміщуємо вікно точно по межах монітора
        var win = new System.Windows.Window
        {
            Title       = "Wire Connector — Калібровка  |  ESC або закрий щоб зберегти",
            WindowStyle = System.Windows.WindowStyle.None,
            ResizeMode  = System.Windows.ResizeMode.NoResize,
            Topmost     = true,
            Left        = screen.X,
            Top         = screen.Y,
            Width       = screen.Width,
            Height      = screen.Height,
            Background  = WpfBrushes.Black
        };

        // Головний Grid: рядок з панеллю зверху + canvas знизу
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Панель інструментів зверху
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
            Height = 36
        };

        toolbar.Children.Add(new TextBlock
        {
            Text = "🔴 проводи верх   🔵 проводи низ   ⬜⬜ межі X   🟡 кільця верх   🟢 кільця низ",
            Foreground = WpfBrushes.White, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 20, 0)
        });

        toolbar.Children.Add(new TextBlock
        {
            Text = "Розмір кілець:", Foreground = WpfBrushes.White, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });

        var sizeLabel = new TextBlock
        {
            Foreground = WpfBrushes.Yellow, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0)
        };
        var sizeSlider = new Slider
        {
            Minimum = 10, Maximum = 80, Value = _cfg.RingRadius * 2,
            Width = 130, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0)
        };
        sizeLabel.Text = $"{(int)sizeSlider.Value}px";
        sizeSlider.ValueChanged += (_, e) =>
        {
            sizeLabel.Text = $"{(int)e.NewValue}px";
            _cfg.RingRadius = (int)(e.NewValue / 2);
            _cfg.Save();
        };
        toolbar.Children.Add(sizeLabel);
        toolbar.Children.Add(sizeSlider);

        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✓ Зберегти і закрити", Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        toolbar.Children.Add(closeBtn);

        Grid.SetRow(toolbar, 0);
        grid.Children.Add(toolbar);

        // Canvas з скріном
        var canvas = new System.Windows.Controls.Canvas { Background = WpfBrushes.Black, ClipToBounds = true };
        Grid.SetRow(canvas, 1);
        grid.Children.Add(canvas);

        var previewImg = new System.Windows.Controls.Image
        {
            Source  = screenshot,
            Stretch = Stretch.Fill,  // Fill — скрін точно на весь canvas без відступів
            IsHitTestVisible = false
        };
        canvas.Children.Add(previewImg);
        System.Windows.Controls.Canvas.SetLeft(previewImg, 0);
        System.Windows.Controls.Canvas.SetTop (previewImg, 0);

        // ── Допоміжні функції ──
        System.Windows.Shapes.Line MakeHLine(System.Windows.Media.Color color)
        {
            var l = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 8, 4 },
                IsHitTestVisible = true, Cursor = System.Windows.Input.Cursors.SizeNS
            };
            canvas.Children.Add(l); return l;
        }
        System.Windows.Shapes.Line MakeVLine(System.Windows.Media.Color color)
        {
            var l = new System.Windows.Shapes.Line
            {
                Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                StrokeDashArray = new System.Windows.Media.DoubleCollection { 8, 4 },
                IsHitTestVisible = true, Cursor = System.Windows.Input.Cursors.SizeWE
            };
            canvas.Children.Add(l); return l;
        }
        TextBlock MakeLbl(string t, WpfBrush b)
        {
            var tb = new TextBlock
            {
                Text = t, Foreground = b, FontSize = 11, IsHitTestVisible = false,
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 0, 0))
            };
            canvas.Children.Add(tb); return tb;
        }
        System.Windows.Shapes.Ellipse MakeNode(System.Windows.Media.Color color)
        {
            double sz = sizeSlider.Value;
            var e = new System.Windows.Shapes.Ellipse
            {
                Width = sz, Height = sz,
                Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, color.R, color.G, color.B)),
                IsHitTestVisible = true, Cursor = System.Windows.Input.Cursors.SizeAll,
                Tag = color  // зберігаємо колір для оновлення розміру
            };
            canvas.Children.Add(e); return e;
        }

        var lineTopWire = MakeHLine(System.Windows.Media.Colors.Red);
        var lineBotWire = MakeHLine(System.Windows.Media.Colors.DodgerBlue);
        var lblTopWire  = MakeLbl("верхні проводи", WpfBrushes.Red);
        var lblBotWire  = MakeLbl("нижні проводи",  WpfBrushes.DodgerBlue);
        var lineX1      = MakeVLine(System.Windows.Media.Colors.White);
        var lineX2      = MakeVLine(System.Windows.Media.Colors.White);
        var lblX1       = MakeLbl("X1", WpfBrushes.White);
        var lblX2       = MakeLbl("X2", WpfBrushes.White);

        const int N = 5;
        var ringTopNodes = Enumerable.Range(0, N).Select(_ => MakeNode(System.Windows.Media.Colors.Yellow)).ToArray();
        var ringBotNodes = Enumerable.Range(0, N).Select(_ => MakeNode(System.Windows.Media.Colors.LimeGreen)).ToArray();

        // ── Refresh ──
        void Refresh(double cw, double ch)
        {
            previewImg.Width  = cw;
            previewImg.Height = ch;
            double sx = cw / imgW, sy = ch / imgH;

            void SetH(System.Windows.Shapes.Line l, TextBlock lb, double yPct)
            {
                double y = yPct * imgH * sy;
                l.X1 = 0; l.X2 = cw; l.Y1 = y; l.Y2 = y;
                System.Windows.Controls.Canvas.SetLeft(lb, 6);
                System.Windows.Controls.Canvas.SetTop (lb, y + 2);
            }
            void SetV(System.Windows.Shapes.Line l, TextBlock lb, double xPct)
            {
                double x = xPct * imgW * sx;
                l.X1 = x; l.X2 = x; l.Y1 = 0; l.Y2 = ch;
                System.Windows.Controls.Canvas.SetLeft(lb, x + 3);
                System.Windows.Controls.Canvas.SetTop (lb, 22);
            }
            void SetRow(System.Windows.Shapes.Ellipse[] nodes, double yPct, double x1Pct, double stepPct)
            {
                double cy = yPct * imgH * sy;
                double cx1 = x1Pct * imgW * sx;
                double cstep = stepPct * imgW * sx;
                // Розмір кружка = діаметр bitmap * масштаб canvas
                double sz = sizeSlider.Value * sx;
                for (int i = 0; i < N; i++)
                {
                    nodes[i].Width  = sz;
                    nodes[i].Height = sz;
                    double cx = cx1 + i * cstep;
                    System.Windows.Controls.Canvas.SetLeft(nodes[i], cx - sz / 2);
                    System.Windows.Controls.Canvas.SetTop (nodes[i], cy - sz / 2);
                }
            }

            SetH(lineTopWire, lblTopWire, _cfg.TopWireY);
            SetH(lineBotWire, lblBotWire, _cfg.BotWireY);
            SetV(lineX1, lblX1, _cfg.ScanX1);
            SetV(lineX2, lblX2, _cfg.ScanX2);
            SetRow(ringTopNodes, _cfg.RingTopY, _cfg.RingTopX1, _cfg.RingTopStep);
            SetRow(ringBotNodes, _cfg.RingBotY, _cfg.RingBotX1, _cfg.RingBotStep);
        }

        canvas.SizeChanged += (_, e) => Refresh(e.NewSize.Width, e.NewSize.Height);
        sizeSlider.ValueChanged += (_, _) => Refresh(canvas.ActualWidth, canvas.ActualHeight);

        // ── Drag ──
        object? drag = null;
        System.Windows.Point dragOff;

        canvas.MouseDown += (_, e) =>
        {
            var pt = e.GetPosition(canvas);
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double sx = cw / imgW, sy = ch / imgH;

            double best = 14;
            System.Windows.Shapes.Line? bestLine = null;
            foreach (var l in new[] { lineTopWire, lineBotWire })
            { double d = Math.Abs(pt.Y - l.Y1); if (d < best) { best = d; bestLine = l; } }
            if (bestLine != null) { drag = bestLine; canvas.CaptureMouse(); return; }

            foreach (var l in new[] { lineX1, lineX2 })
            { double d = Math.Abs(pt.X - l.X1); if (d < best) { best = d; bestLine = l; } }
            if (bestLine != null) { drag = bestLine; canvas.CaptureMouse(); return; }

            for (int i = 0; i < N; i++)
            {
                foreach (var (nodes, isTop) in new[] { (ringTopNodes, true), (ringBotNodes, false) })
                {
                    var nd = nodes[i];
                    double nx = System.Windows.Controls.Canvas.GetLeft(nd) + nd.Width  / 2;
                    double ny = System.Windows.Controls.Canvas.GetTop (nd) + nd.Height / 2;
                    double hit = sizeSlider.Value / 2 + 4;
                    if (Math.Abs(pt.X - nx) < hit && Math.Abs(pt.Y - ny) < hit)
                    {
                        drag = (nd, isTop, i);
                        dragOff = new System.Windows.Point(pt.X - nx, pt.Y - ny);
                        canvas.CaptureMouse(); return;
                    }
                }
            }
        };

        canvas.MouseMove += (_, e) =>
        {
            if (drag == null) return;
            var pt = e.GetPosition(canvas);
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double sx = cw / imgW, sy = ch / imgH;

            if (drag is System.Windows.Shapes.Line dl)
            {
                if      (dl == lineTopWire) _cfg.TopWireY = Math.Clamp(pt.Y / (imgH * sy), 0.01, 0.99);
                else if (dl == lineBotWire) _cfg.BotWireY = Math.Clamp(pt.Y / (imgH * sy), 0.01, 0.99);
                else if (dl == lineX1)      _cfg.ScanX1   = Math.Clamp(pt.X / (imgW * sx), 0.01, _cfg.ScanX2 - 0.01);
                else if (dl == lineX2)      _cfg.ScanX2   = Math.Clamp(pt.X / (imgW * sx), _cfg.ScanX1 + 0.01, 0.99);
            }
            else if (drag is (System.Windows.Shapes.Ellipse, bool isTop, int idx))
            {
                double xPct = Math.Clamp((pt.X - dragOff.X) / (imgW * sx), 0.01, 0.99);
                double yPct = Math.Clamp((pt.Y - dragOff.Y) / (imgH * sy), 0.01, 0.99);
                if (isTop)
                {
                    _cfg.RingTopY = yPct;
                    if (idx == 0) _cfg.RingTopX1   = xPct;
                    else          _cfg.RingTopStep  = (xPct - _cfg.RingTopX1) / idx;
                }
                else
                {
                    _cfg.RingBotY = yPct;
                    if (idx == 0) _cfg.RingBotX1   = xPct;
                    else          _cfg.RingBotStep  = (xPct - _cfg.RingBotX1) / idx;
                }
            }
            Refresh(cw, ch);
        };

        canvas.MouseUp += (_, _) =>
        {
            if (drag != null) { _cfg.Save(); drag = null; canvas.ReleaseMouseCapture(); }
        };

        closeBtn.Click += (_, _) => { _cfg.Save(); win.Close(); };
        win.KeyDown    += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { _cfg.Save(); win.Close(); } };
        win.Closing    += (_, _) => _cfg.Save();
        win.Content     = grid;
        win.Show();
    }

    // ── Main loop ─────────────────────────────────────────────────────────────
    void Run()
    {
        try
        {
            _log("Wire Connector запущено. Очікую міні-гру...");

            while (_state == BotState.Running)
            {
                var screen = GetScreen();
                using var bmp = ScreenCapture.Capture(screen);

                var topWires    = WireScanner.FindTopWireTips(bmp, screen, _cfg);
                var bottomWires = WireScanner.FindBottomWireTips(bmp, screen, _cfg);
                var (topRings, bottomRings) = WireScanner.FindRings(bmp, _cfg);

                _log($"Знайдено: верх={topWires.Count} [{string.Join(",", topWires.Select(w => w.color))}] | низ={bottomWires.Count} [{string.Join(",", bottomWires.Select(w => w.color))}]");
                _log($"Кільця: верх={topRings.Count} [{string.Join(",", topRings.Select(r => r.color))}] | низ={bottomRings.Count} [{string.Join(",", bottomRings.Select(r => r.color))}]");

                // Debug: зберегти скріншот з підсвіченими точками і зонами
                SaveDebugImage(bmp, topWires, bottomWires, topRings, bottomRings, _debugFrame++, _cfg);

                if (topWires.Count == 5 && topRings.Count >= 5 && bottomWires.Count == 5 && bottomRings.Count >= 5)
                {
                    _log("Починаю з'єднання...");
                    ConnectWires(topWires, topRings, screen, isTop: true);
                    Thread.Sleep(200);
                    ConnectWires(bottomWires, bottomRings, screen, isTop: false);
                    _log("Готово! 🔌");
                    _state = BotState.Idle;
                    StateChanged?.Invoke();
                    return;
                }

                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            _log($"[ПОМИЛКА] {ex.Message}");
            _state = BotState.Idle;
            StateChanged?.Invoke();
        }
    }

    void ConnectWires(
        List<(System.Drawing.Point pos, WireColor color)> wires,
        List<(System.Drawing.Point center, WireColor color)> rings,
        System.Drawing.Rectangle screen,
        bool isTop)
    {
        foreach (var (wirePos, wireColor) in wires)
        {
            if (wireColor == WireColor.Unknown) { _log($"⚠ Провід невідомого кольору на X={wirePos.X}"); continue; }

            var ringMatch = rings.Find(r => r.color == wireColor);
            if (ringMatch.center == default) { _log($"⚠ Кільце для {wireColor} не знайдено"); continue; }

            // Конвертуємо з координат bitmap в екранні координати
            float sx = (float)screen.Width  / GetBmpWidth(screen);
            float sy = (float)screen.Height / GetBmpHeight(screen);

            int fromX = screen.X + (int)(wirePos.X    * sx);
            int fromY = screen.Y + (int)(wirePos.Y    * sy);
            int toX   = screen.X + (int)(ringMatch.center.X * sx);
            int toY   = screen.Y + (int)(ringMatch.center.Y * sy);

            _log($"🔌 {wireColor}: ({fromX},{fromY}) → ({toX},{toY})");
            DragInput.Drag(fromX, fromY, toX, toY, _log);
        }
    }

    System.Drawing.Rectangle GetScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (_cfg.MonitorIndex >= 0 && _cfg.MonitorIndex < screens.Length)
            return screens[_cfg.MonitorIndex].Bounds;
        return System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
    }

    // Bitmap розмір = розмір екрану (ScreenCapture.Capture захоплює 1:1)
    static int GetBmpWidth(System.Drawing.Rectangle screen)  => screen.Width;
    static int GetBmpHeight(System.Drawing.Rectangle screen) => screen.Height;

    static void SaveDebugImage(
        Bitmap bmp,
        List<(System.Drawing.Point pos, WireColor color)> topWires,
        List<(System.Drawing.Point pos, WireColor color)> bottomWires,
        List<(System.Drawing.Point center, WireColor color)> topRings,
        List<(System.Drawing.Point center, WireColor color)> bottomRings,
        int frame, WiresConfig cfg)
    {
        try
        {
            using var dbg = (Bitmap)bmp.Clone();
            using var g   = System.Drawing.Graphics.FromImage(dbg);

            // Лінії зон з конфігу
            int scanTopY  = (int)(bmp.Height * cfg.TopWireY);
            int scanBotY  = (int)(bmp.Height * cfg.BotWireY);
            int scanX1    = (int)(bmp.Width  * cfg.ScanX1);
            int scanX2    = (int)(bmp.Width  * cfg.ScanX2);
            int ringTopY  = (int)(bmp.Height * cfg.RingTopY);
            int ringBotY  = (int)(bmp.Height * cfg.RingBotY);

            var penTop  = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, System.Drawing.Color.OrangeRed), 2);
            var penBot  = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, System.Drawing.Color.DodgerBlue), 2);
            var penX    = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, System.Drawing.Color.White), 1);
            var penRing = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, System.Drawing.Color.MediumPurple), 2);

            g.DrawLine(penTop,  0, scanTopY, bmp.Width, scanTopY);
            g.DrawLine(penBot,  0, scanBotY, bmp.Width, scanBotY);
            g.DrawLine(penX,    scanX1, 0, scanX1, bmp.Height);
            g.DrawLine(penX,    scanX2, 0, scanX2, bmp.Height);
            g.DrawLine(penRing, 0, ringTopY, bmp.Width, ringTopY);
            g.DrawLine(penRing, 0, ringBotY, bmp.Width, ringBotY);

            // Верхні проводи — червоний хрест
            foreach (var (pos, col) in topWires)
            {
                DrawCross(g, pos, System.Drawing.Color.Red, 10);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Red, pos.X + 4, pos.Y - 14);
            }

            // Нижні проводи — синій хрест
            foreach (var (pos, col) in bottomWires)
            {
                DrawCross(g, pos, System.Drawing.Color.Blue, 10);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Cyan, pos.X + 4, pos.Y + 4);
            }

            // Верхні кільця — жовте коло
            foreach (var (center, col) in topRings)
            {
                g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Yellow, 3), center.X - 12, center.Y - 12, 24, 24);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Yellow, center.X + 14, center.Y - 6);
            }

            // Нижні кільця — зелене коло
            foreach (var (center, col) in bottomRings)
            {
                g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Lime, 3), center.X - 12, center.Y - 12, 24, 24);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Lime, center.X + 14, center.Y - 6);
            }

            string dir  = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wires_debug");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"frame_{frame:D4}.png");
            dbg.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            // Зберегти реальні RGB пікселів для налаштування ColorMatcher
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== frame {frame} ===");
            sb.AppendLine("TOP WIRES:");
            foreach (var (pos, col) in topWires)
            {
                var px = bmp.GetPixel(pos.X, pos.Y);
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y}) RGB=({px.R},{px.G},{px.B}) #{px.R:X2}{px.G:X2}{px.B:X2}");
            }
            sb.AppendLine("BOTTOM WIRES:");
            foreach (var (pos, col) in bottomWires)
            {
                var px = bmp.GetPixel(pos.X, pos.Y);
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y}) RGB=({px.R},{px.G},{px.B}) #{px.R:X2}{px.G:X2}{px.B:X2}");
            }
            sb.AppendLine("TOP RINGS:");
            foreach (var (pos, col) in topRings)
            {
                var px = bmp.GetPixel(Math.Clamp(pos.X, 0, bmp.Width-1), Math.Clamp(pos.Y, 0, bmp.Height-1));
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y}) RGB=({px.R},{px.G},{px.B}) #{px.R:X2}{px.G:X2}{px.B:X2}");
            }
            sb.AppendLine("BOTTOM RINGS:");
            foreach (var (pos, col) in bottomRings)
            {
                var px = bmp.GetPixel(Math.Clamp(pos.X, 0, bmp.Width-1), Math.Clamp(pos.Y, 0, bmp.Height-1));
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y}) RGB=({px.R},{px.G},{px.B}) #{px.R:X2}{px.G:X2}{px.B:X2}");
            }
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "colors.txt"), sb.ToString());
        }
        catch { }
    }

    static void DrawCross(System.Drawing.Graphics g, System.Drawing.Point p, System.Drawing.Color c, int size)
    {
        var pen = new System.Drawing.Pen(c, 2);
        g.DrawLine(pen, p.X - size, p.Y, p.X + size, p.Y);
        g.DrawLine(pen, p.X, p.Y - size, p.X, p.Y + size);
    }
}
