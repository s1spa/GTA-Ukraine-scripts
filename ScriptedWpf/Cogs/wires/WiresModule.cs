using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

            new Thread(() =>
            {
                for (int sec = 5; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(() =>
                        countdownLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }

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

    // ── Кольори WireColor для UI ───────────────────────────────────────────────
    static readonly (WireColor wc, string hex, string label)[] WireColorDefs =
    {
        (WireColor.Red,      "#DF3130", "Red"),
        (WireColor.Blue,     "#428FFF", "Blue"),
        (WireColor.Yellow,   "#D0852D", "Yellow"),
        (WireColor.Black,    "#514E49", "Black"),
        (WireColor.Green,    "#6C8222", "Green"),
        (WireColor.Purple,   "#6D149F", "Purple"),
        (WireColor.DarkBlue, "#2B1D68", "DarkBlue"),
        (WireColor.DarkGreen,"#2F572D", "DarkGreen"),
        (WireColor.Teal,     "#12917F", "Teal"),
        (WireColor.Olive,    "#625F3D", "Olive"),
    };

    static System.Windows.Media.Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        return System.Windows.Media.Color.FromRgb(r, g, b);
    }

    // ── Enum for drag state ────────────────────────────────────────────────────
    enum DragTarget { None, WireBody, RingBody,
        WireTL, WireTR, WireBL, WireBR, WireTC, WireBC, WireLC, WireRC,
        RingTL, RingTR, RingBL, RingBR, RingTC, RingBC, RingLC, RingRC }

    // ── Calibration phases ─────────────────────────────────────────────────────
    enum CalibPhase { DrawingWire, DrawingRing, Sampling }

    // ── Повноекранне вікно калібровки ─────────────────────────────────────────
    void OpenCalibrationWindow(System.Windows.Media.Imaging.BitmapImage screenshot, double imgW, double imgH)
    {
        var screen = GetScreen();
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

        // ── Toolbar ──────────────────────────────────────────────────────────
        var toolbarPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromArgb(220, 20, 20, 20)),
        };

        // Рядок 1: режим зразку + кнопки керування
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 2) };

        // Режим зразку: 3 toggle-кнопки
        var modeWire = new System.Windows.Controls.Button { Content = "🔵 Провід", Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0) };
        var modeRing = new System.Windows.Controls.Button { Content = "🔴 Кільце", Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 4, 0) };
        var modeTip  = new System.Windows.Controls.Button { Content = "🟡 Кінець проводу", Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 16, 0) };

        row1.Children.Add(modeWire);
        row1.Children.Add(modeRing);
        row1.Children.Add(modeTip);

        // Кнопки "Очистити" і "Зберегти"
        var clearBtn = new System.Windows.Controls.Button
        {
            Content = "🗑 Очистити все", Height = 28,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 4, 0)
        };
        var closeBtn = new System.Windows.Controls.Button
        {
            Content = "✓ Зберегти", Height = 28,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0)
        };
        var toggleZonesBtn = new System.Windows.Controls.Button
        {
            Content = "👁 Зони", Height = 28,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(4, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        row1.Children.Add(clearBtn);
        row1.Children.Add(closeBtn);
        row1.Children.Add(toggleZonesBtn);

        // Підказка
        var hintLbl = new TextBlock
        {
            Text = "Намалюй прямокутник міні-гри (тягни мишею) → Enter",
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 220, 80)),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0)
        };
        row1.Children.Add(hintLbl);

        toolbarPanel.Children.Add(row1);

        // Рядок 2: вибір кольору (10 кнопок)
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 2, 6, 4) };

        var colorBtns = new List<System.Windows.Controls.Button>();
        foreach (var (wc, hex, label) in WireColorDefs)
        {
            var col = ParseHexColor(hex);
            var btn = new System.Windows.Controls.Button
            {
                Width = 56, Height = 24,
                Content = label,
                FontSize = 9,
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(col),
                Foreground = WpfBrushes.White,
                BorderBrush = WpfBrushes.Transparent,
                BorderThickness = new Thickness(2),
                Tag = wc,
                ToolTip = label
            };
            colorBtns.Add(btn);
            row2.Children.Add(btn);
        }

        toolbarPanel.Children.Add(row2);

        Grid.SetRow(toolbarPanel, 0);
        grid.Children.Add(toolbarPanel);

        // ── Canvas ───────────────────────────────────────────────────────────
        var canvas = new System.Windows.Controls.Canvas { Background = WpfBrushes.Black, ClipToBounds = true };
        Grid.SetRow(canvas, 1);
        grid.Children.Add(canvas);

        var previewImg = new System.Windows.Controls.Image
        {
            Source  = screenshot,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        canvas.Children.Add(previewImg);
        System.Windows.Controls.Canvas.SetLeft(previewImg, 0);
        System.Windows.Controls.Canvas.SetTop (previewImg, 0);

        // ── Pre-read screenshot bytes into buffer (Bgr32) ────────────────────
        byte[] imgBuf;
        int imgStride;
        {
            var fmt = PixelFormats.Bgr32;
            var conv = new FormatConvertedBitmap(screenshot, fmt, null, 0);
            imgStride = (conv.PixelWidth * fmt.BitsPerPixel + 7) / 8;
            imgBuf = new byte[imgStride * conv.PixelHeight];
            conv.CopyPixels(imgBuf, imgStride, 0);
        }

        // ── Overlay WriteableBitmap ───────────────────────────────────────────
        var overlayWb = new WriteableBitmap((int)imgW, (int)imgH, 96, 96, PixelFormats.Bgra32, null);
        var overlayImg = new System.Windows.Controls.Image
        {
            Source = overlayWb,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        canvas.Children.Add(overlayImg);
        System.Windows.Controls.Canvas.SetLeft(overlayImg, 0);
        System.Windows.Controls.Canvas.SetTop(overlayImg, 0);

        // ── Фаза калібровки ───────────────────────────────────────────────────
        CalibPhase phase = CalibPhase.DrawingWire;

        // ── Стан вибору ──────────────────────────────────────────────────────
        // 0=Wire, 1=Ring, 2=Tip
        int sampleMode = 0;
        WireColor activeColor = WireColor.Red;

        // Список dot-елементів на canvas (для right-click видалення)
        var dotList = new List<(System.Windows.Shapes.Ellipse el, string colorName, int mode, int sampleIdx)>();

        // ── RebuildOverlay ────────────────────────────────────────────────────
        void RebuildOverlay()
        {
            var samples = new List<(int R, int G, int B)>();
            if (_cfg.ColorSamples.TryGetValue(activeColor.ToString(), out var csO))
            {
                foreach (var s in csO.Wire) samples.Add((s.R, s.G, s.B));
                foreach (var s in csO.Ring) samples.Add((s.R, s.G, s.B));
                // Tip зберігає тільки XY (без кольору) — не додаємо до overlay
            }

            int w = (int)imgW, h = (int)imgH;
            var pixels = new byte[w * h * 4];

            if (samples.Count > 0)
            {
                int gx1  = (int)(_cfg.ScanX1    * w);
                int gx2  = (int)(_cfg.ScanX2    * w);
                int gy1  = (int)(_cfg.TopWireY  * h);
                int gy2  = (int)(_cfg.BotWireY  * h);
                int rtx1 = (int)(_cfg.RingTopX1 * w);
                int rtx2 = (int)(_cfg.RingTopX2 * w);
                int rty  = (int)(_cfg.RingTopY  * h);
                int rbx1 = (int)(_cfg.RingBotX1 * w);
                int rbx2 = (int)(_cfg.RingBotX2 * w);
                int rby  = (int)(_cfg.RingBotY  * h);
                int ringHalfH = h / 20;

                for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool inZone = sampleMode == 1
                        ? (x >= rtx1 && x <= rtx2 && y >= rty && y <= rby)
                        : (x >= gx1 && x <= gx2 && y >= gy1 && y <= gy2); // Wire та Tip — зона міні-гри
                    if (!inZone) continue;

                    int srcIdx = y * imgStride + x * 4;
                    if (srcIdx + 2 >= imgBuf.Length) continue;
                    int B = imgBuf[srcIdx];
                    int G = imgBuf[srcIdx + 1];
                    int R = imgBuf[srcIdx + 2];

                    bool matched = false;
                    foreach (var (sr, sg, sb) in samples)
                    {
                        int d = (R - sr) * (R - sr) + (G - sg) * (G - sg) + (B - sb) * (B - sb);
                        if (d < 75) { matched = true; break; }
                    }

                    if (matched)
                    {
                        int dstIdx = (y * w + x) * 4;
                        pixels[dstIdx]     = 255;
                        pixels[dstIdx + 1] = 255;
                        pixels[dstIdx + 2] = 255;
                        pixels[dstIdx + 3] = 200;
                    }
                }
            }

            overlayWb.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 4, 0);
        }

        // ── Оновлення вигляду кнопок режиму ──
        void UpdateModeBtns()
        {
            var active = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 80, 140, 255));
            var normal = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 60, 60, 60));
            modeWire.Background = sampleMode == 0 ? active : normal;
            modeRing.Background = sampleMode == 1 ? active : normal;
            modeTip .Background = sampleMode == 2 ? active : normal;
        }

        void UpdateColorBtns()
        {
            foreach (var btn in colorBtns)
            {
                var wc = (WireColor)btn.Tag!;
                btn.BorderBrush = wc == activeColor
                    ? WpfBrushes.White
                    : WpfBrushes.Transparent;
            }
        }

        UpdateModeBtns();
        UpdateColorBtns();

        modeWire.Click += (_, _) => { sampleMode = 0; UpdateModeBtns(); };
        modeRing.Click += (_, _) => { sampleMode = 1; UpdateModeBtns(); };
        modeTip .Click += (_, _) => { sampleMode = 2; UpdateModeBtns(); };

        foreach (var btn in colorBtns)
        {
            btn.Click += (s, _) =>
            {
                activeColor = (WireColor)((System.Windows.Controls.Button)s!).Tag!;
                UpdateColorBtns();
                RebuildOverlay();
            };
        }

        clearBtn.Click += (_, _) =>
        {
            string colorName = activeColor.ToString();
            if (!_cfg.ColorSamples.TryGetValue(colorName, out var cs)) return;
            var listToClr = sampleMode == 0 ? cs.Wire : sampleMode == 1 ? cs.Ring : cs.Tip;
            listToClr.Clear();
            _cfg.Save();
            var toRemove = dotList.Where(d => d.colorName == colorName && d.mode == sampleMode).ToList();
            foreach (var d in toRemove) { canvas.Children.Remove(d.el); dotList.Remove(d); }
            RebuildOverlay();
        };

        closeBtn.Click += (_, _) => { _cfg.Save(); win.Close(); };

        // ── Zone rectangles ───────────────────────────────────────────────────
        // Wire zone rect (white dashed)
        var wireRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = WpfBrushes.White,
            StrokeThickness = 2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 8, 4 },
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        canvas.Children.Add(wireRect);

        // Wire zone label
        var wireLbl = new TextBlock
        {
            Text = "міні-гра",
            Foreground = WpfBrushes.White,
            FontSize = 13,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 0, 0))
        };
        canvas.Children.Add(wireLbl);

        // Ring zone rect (purple dashed)
        var ringRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = WpfBrushes.MediumPurple,
            StrokeThickness = 2,
            StrokeDashArray = new System.Windows.Media.DoubleCollection { 8, 4 },
            Fill = System.Windows.Media.Brushes.Transparent,
            IsHitTestVisible = false
        };
        canvas.Children.Add(ringRect);

        // Ring zone label
        var ringLbl = new TextBlock
        {
            Text = "кільця",
            Foreground = WpfBrushes.MediumPurple,
            FontSize = 13,
            IsHitTestVisible = false,
            Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(140, 0, 0, 0))
        };
        canvas.Children.Add(ringLbl);

        // ── Handles (8 per rect) ──────────────────────────────────────────────
        const double HandleSize = 10.0;
        const double HandleHit  = 8.0;  // hit radius

        System.Windows.Shapes.Rectangle MakeHandle(WpfBrush fillBrush)
        {
            var h = new System.Windows.Shapes.Rectangle
            {
                Width  = HandleSize,
                Height = HandleSize,
                Fill   = fillBrush,
                Stroke = WpfBrushes.Black,
                StrokeThickness = 0.5,
                IsHitTestVisible = false
            };
            canvas.Children.Add(h);
            return h;
        }

        // Wire handles: TL, TR, BL, BR, TC, BC, LC, RC
        var whTL = MakeHandle(WpfBrushes.White);
        var whTR = MakeHandle(WpfBrushes.White);
        var whBL = MakeHandle(WpfBrushes.White);
        var whBR = MakeHandle(WpfBrushes.White);
        var whTC = MakeHandle(WpfBrushes.White);
        var whBC = MakeHandle(WpfBrushes.White);
        var whLC = MakeHandle(WpfBrushes.White);
        var whRC = MakeHandle(WpfBrushes.White);

        // Ring handles
        var rhTL = MakeHandle(WpfBrushes.MediumPurple);
        var rhTR = MakeHandle(WpfBrushes.MediumPurple);
        var rhBL = MakeHandle(WpfBrushes.MediumPurple);
        var rhBR = MakeHandle(WpfBrushes.MediumPurple);
        var rhTC = MakeHandle(WpfBrushes.MediumPurple);
        var rhBC = MakeHandle(WpfBrushes.MediumPurple);
        var rhLC = MakeHandle(WpfBrushes.MediumPurple);
        var rhRC = MakeHandle(WpfBrushes.MediumPurple);

        void PlaceHandle(System.Windows.Shapes.Rectangle h, double cx, double cy)
        {
            System.Windows.Controls.Canvas.SetLeft(h, cx - HandleSize / 2);
            System.Windows.Controls.Canvas.SetTop (h, cy - HandleSize / 2);
        }

        // ── Refresh ──────────────────────────────────────────────────────────
        void Refresh(double cw, double ch)
        {
            previewImg.Width  = cw;
            previewImg.Height = ch;
            overlayImg.Width  = cw;
            overlayImg.Height = ch;
            double sx = cw / imgW, sy = ch / imgH;

            // Wire zone rect
            double wLeft   = _cfg.ScanX1   * imgW * sx;
            double wTop    = _cfg.TopWireY * imgH * sy;
            double wRight  = _cfg.ScanX2   * imgW * sx;
            double wBottom = _cfg.BotWireY * imgH * sy;
            double wW = wRight - wLeft;
            double wH = wBottom - wTop;

            System.Windows.Controls.Canvas.SetLeft(wireRect, wLeft);
            System.Windows.Controls.Canvas.SetTop (wireRect, wTop);
            wireRect.Width  = Math.Max(wW, 0);
            wireRect.Height = Math.Max(wH, 0);

            // мітка зовні — зліва-зверху від прямокутника
            System.Windows.Controls.Canvas.SetLeft(wireLbl, wLeft);
            System.Windows.Controls.Canvas.SetTop (wireLbl, Math.Max(0, wTop - 20));

            // Wire handles
            double wCX = (wLeft + wRight) / 2;
            double wCY = (wTop  + wBottom) / 2;
            PlaceHandle(whTL, wLeft,  wTop);
            PlaceHandle(whTR, wRight, wTop);
            PlaceHandle(whBL, wLeft,  wBottom);
            PlaceHandle(whBR, wRight, wBottom);
            PlaceHandle(whTC, wCX,    wTop);
            PlaceHandle(whBC, wCX,    wBottom);
            PlaceHandle(whLC, wLeft,  wCY);
            PlaceHandle(whRC, wRight, wCY);

            // Ring zone rect
            double rLeft   = _cfg.RingTopX1 * imgW * sx;
            double rTop    = _cfg.RingTopY  * imgH * sy;
            double rRight  = _cfg.RingTopX2 * imgW * sx;
            double rBottom = _cfg.RingBotY  * imgH * sy;
            double rW = rRight - rLeft;
            double rH = rBottom - rTop;

            System.Windows.Controls.Canvas.SetLeft(ringRect, rLeft);
            System.Windows.Controls.Canvas.SetTop (ringRect, rTop);
            ringRect.Width  = Math.Max(rW, 0);
            ringRect.Height = Math.Max(rH, 0);

            // мітка зовні — зліва-зверху від прямокутника
            System.Windows.Controls.Canvas.SetLeft(ringLbl, rLeft);
            System.Windows.Controls.Canvas.SetTop (ringLbl, Math.Max(0, rTop - 20));

            // Ring handles
            double rCX = (rLeft + rRight) / 2;
            double rCY = (rTop  + rBottom) / 2;
            PlaceHandle(rhTL, rLeft,  rTop);
            PlaceHandle(rhTR, rRight, rTop);
            PlaceHandle(rhBL, rLeft,  rBottom);
            PlaceHandle(rhBR, rRight, rBottom);
            PlaceHandle(rhTC, rCX,    rTop);
            PlaceHandle(rhBC, rCX,    rBottom);
            PlaceHandle(rhLC, rLeft,  rCY);
            PlaceHandle(rhRC, rRight, rCY);

            // Оновити позиції наявних dots
            foreach (var (el, _, _, _) in dotList)
            {
                var tag = ((double xp, double yp, string cn, int md, int si))el.Tag!;
                double cx = tag.xp * imgW * sx;
                double cy = tag.yp * imgH * sy;
                System.Windows.Controls.Canvas.SetLeft(el, cx - el.Width  / 2);
                System.Windows.Controls.Canvas.SetTop (el, cy - el.Height / 2);
            }
        }

        canvas.SizeChanged += (_, e) => Refresh(e.NewSize.Width, e.NewSize.Height);

        // ── Додати dot на canvas ──────────────────────────────────────────────
        System.Windows.Shapes.Ellipse AddDot(double xPct, double yPct, string colorName, int mode, int sampleIdx,
            System.Windows.Media.Color fillColor, System.Windows.Media.Color borderColor,
            double cw, double ch)
        {
            double sx = cw / imgW, sy = ch / imgH;
            const double sz = 8;
            var el = new System.Windows.Shapes.Ellipse
            {
                Width  = sz, Height = sz,
                Fill   = new SolidColorBrush(fillColor),
                Stroke = new SolidColorBrush(borderColor),
                StrokeThickness = 1.5,
                IsHitTestVisible = true,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            el.Tag = (xPct, yPct, colorName, mode, sampleIdx);
            double cx = xPct * imgW * sx;
            double cy = yPct * imgH * sy;
            System.Windows.Controls.Canvas.SetLeft(el, cx - sz / 2);
            System.Windows.Controls.Canvas.SetTop (el, cy - sz / 2);

            el.MouseRightButtonDown += (s, e) =>
            {
                var dot = (System.Windows.Shapes.Ellipse)s!;
                var tag = ((double xp, double yp, string cn, int md, int si))dot.Tag!;
                var found = dotList.FirstOrDefault(d => d.el == dot);
                if (found.el == null) return;
                dotList.Remove(found);
                canvas.Children.Remove(dot);
                if (_cfg.ColorSamples.TryGetValue(tag.cn, out var cs))
                {
                    var lst = tag.md == 0 ? cs.Wire : tag.md == 1 ? cs.Ring : cs.Tip;
                    if (tag.si >= 0 && tag.si < lst.Count)
                    {
                        lst.RemoveAt(tag.si);
                        RebuildDotIndices(tag.cn, tag.md);
                    }
                    _cfg.Save();
                }
                RebuildOverlay();
                e.Handled = true;
            };

            canvas.Children.Add(el);
            return el;
        }

        void RebuildDotIndices(string colorName, int mode)
        {
            int idx = 0;
            foreach (var i in Enumerable.Range(0, dotList.Count))
            {
                if (dotList[i].colorName == colorName && dotList[i].mode == mode)
                {
                    var d = dotList[i];
                    var oldTag = ((double xp, double yp, string cn, int md, int si))d.el.Tag!;
                    d.el.Tag = (oldTag.xp, oldTag.yp, oldTag.cn, oldTag.md, idx);
                    dotList[i] = (d.el, d.colorName, d.mode, idx);
                    idx++;
                }
            }
        }

        void LoadExistingDots()
        {
            double cw = canvas.ActualWidth;
            double ch = canvas.ActualHeight;
            if (cw <= 0 || ch <= 0) return;

            foreach (var (colorName, cs) in _cfg.ColorSamples)
            {
                if (!System.Enum.TryParse<WireColor>(colorName, out var wc)) continue;
                var defEntry = WireColorDefs.FirstOrDefault(d => d.wc == wc);
                var borderCol = ParseHexColor(defEntry.hex != null ? defEntry.hex : "#888888");

                void LoadList(List<PixelSample> lst, int mode)
                {
                    for (int i = 0; i < lst.Count; i++)
                    {
                        var ps = lst[i];
                        var fill = System.Windows.Media.Color.FromRgb((byte)ps.R, (byte)ps.G, (byte)ps.B);
                        var el = AddDot(ps.XPct, ps.YPct, colorName, mode, i, fill, borderCol, cw, ch);
                        dotList.Add((el, colorName, mode, i));
                    }
                }
                LoadList(cs.Wire, 0);
                LoadList(cs.Ring, 1);
                LoadList(cs.Tip,  2);
            }

            RebuildOverlay();
        }

        // dots завантажуємо лише після підтвердження обох прямокутників (Sampling)
        bool dotsLoaded = false;

        // ── Лупа (zoom) ───────────────────────────────────────────────────────
        const int ZoomPx = 11;   // пікселів навколо курсора (11×11)
        const int ZoomScale = 11; // збільшення кожного пікселя
        const int ZoomSize = ZoomPx * ZoomScale; // 121×121

        var zoomWb = new WriteableBitmap(ZoomSize, ZoomSize, 96, 96, PixelFormats.Bgra32, null);
        var zoomBorder = new System.Windows.Controls.Border
        {
            Width = ZoomSize + 4, Height = ZoomSize + 4,
            BorderBrush = WpfBrushes.White, BorderThickness = new Thickness(2),
            Background = WpfBrushes.Black,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        var zoomImgCtrl = new System.Windows.Controls.Image
        {
            Source = zoomWb, Width = ZoomSize, Height = ZoomSize,
            IsHitTestVisible = false
        };
        zoomBorder.Child = zoomImgCtrl;
        canvas.Children.Add(zoomBorder);

        void UpdateZoom(System.Windows.Point pt)
        {
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double sx = cw / imgW, sy = ch / imgH;
            int bmpX = (int)(pt.X / sx);
            int bmpY = (int)(pt.Y / sy);

            int half = ZoomPx / 2;
            var pixels = new byte[ZoomSize * ZoomSize * 4];

            for (int dy = 0; dy < ZoomPx; dy++)
            for (int dx = 0; dx < ZoomPx; dx++)
            {
                int srcX = Math.Clamp(bmpX - half + dx, 0, (int)imgW - 1);
                int srcY = Math.Clamp(bmpY - half + dy, 0, (int)imgH - 1);
                int srcIdx = srcY * imgStride + srcX * 4;
                byte B = imgBuf[srcIdx], G = imgBuf[srcIdx+1], R = imgBuf[srcIdx+2];

                for (int py = 0; py < ZoomScale; py++)
                for (int px = 0; px < ZoomScale; px++)
                {
                    int dstIdx = ((dy * ZoomScale + py) * ZoomSize + (dx * ZoomScale + px)) * 4;
                    pixels[dstIdx]   = B;
                    pixels[dstIdx+1] = G;
                    pixels[dstIdx+2] = R;
                    pixels[dstIdx+3] = 255;
                }
            }

            // Хрестик посередині (червоний)
            int mid = ZoomSize / 2;
            for (int i = 0; i < ZoomSize; i++)
            {
                // горизонталь
                int idx1 = (mid * ZoomSize + i) * 4;
                pixels[idx1] = 0; pixels[idx1+1] = 0; pixels[idx1+2] = 220; pixels[idx1+3] = 200;
                // вертикаль
                int idx2 = (i * ZoomSize + mid) * 4;
                pixels[idx2] = 0; pixels[idx2+1] = 0; pixels[idx2+2] = 220; pixels[idx2+3] = 200;
            }

            zoomWb.WritePixels(new Int32Rect(0, 0, ZoomSize, ZoomSize), pixels, ZoomSize * 4, 0);

            // Розміщуємо лупу справа-знизу від курсора (або зліва якщо не вміщується)
            double zx = pt.X + 16;
            double zy = pt.Y + 16;
            if (zx + ZoomSize + 4 > cw) zx = pt.X - ZoomSize - 20;
            if (zy + ZoomSize + 4 > ch) zy = pt.Y - ZoomSize - 20;
            System.Windows.Controls.Canvas.SetLeft(zoomBorder, zx);
            System.Windows.Controls.Canvas.SetTop (zoomBorder, zy);
            zoomBorder.Visibility = Visibility.Visible;
        }

        canvas.MouseLeave += (_, _) => zoomBorder.Visibility = Visibility.Collapsed;

        // ── Drag rectangles ───────────────────────────────────────────────────
        DragTarget dragTarget = DragTarget.None;
        System.Windows.Point dragStart = default;
        // cfg snapshot at drag start
        double dragScanX1 = 0, dragScanX2 = 0, dragTopWireY = 0, dragBotWireY = 0;
        double dragRingTopX1 = 0, dragRingTopX2 = 0, dragRingTopY = 0, dragRingBotY = 0;

        // Helper: get handle center in canvas coords given current cfg
        (double x, double y) WireHandlePos(DragTarget t, double cw, double ch)
        {
            double sx = cw / imgW, sy = ch / imgH;
            double L = _cfg.ScanX1   * imgW * sx;
            double R = _cfg.ScanX2   * imgW * sx;
            double T = _cfg.TopWireY * imgH * sy;
            double B = _cfg.BotWireY * imgH * sy;
            double CX = (L + R) / 2, CY = (T + B) / 2;
            return t switch
            {
                DragTarget.WireTL => (L, T),
                DragTarget.WireTR => (R, T),
                DragTarget.WireBL => (L, B),
                DragTarget.WireBR => (R, B),
                DragTarget.WireTC => (CX, T),
                DragTarget.WireBC => (CX, B),
                DragTarget.WireLC => (L, CY),
                DragTarget.WireRC => (R, CY),
                _ => (0, 0)
            };
        }

        (double x, double y) RingHandlePos(DragTarget t, double cw, double ch)
        {
            double sx = cw / imgW, sy = ch / imgH;
            double L = _cfg.RingTopX1 * imgW * sx;
            double R = _cfg.RingTopX2 * imgW * sx;
            double T = _cfg.RingTopY  * imgH * sy;
            double B = _cfg.RingBotY  * imgH * sy;
            double CX = (L + R) / 2, CY = (T + B) / 2;
            return t switch
            {
                DragTarget.RingTL => (L, T),
                DragTarget.RingTR => (R, T),
                DragTarget.RingBL => (L, B),
                DragTarget.RingBR => (R, B),
                DragTarget.RingTC => (CX, T),
                DragTarget.RingBC => (CX, B),
                DragTarget.RingLC => (L, CY),
                DragTarget.RingRC => (R, CY),
                _ => (0, 0)
            };
        }

        // ── Стан малювання прямокутника ───────────────────────────────────────
        bool isDrawingRect = false;
        System.Windows.Point rectDrawStart = default;

        // Показати/сховати handle-квадратики і прямокутники
        bool zonesVisible = false;
        void SetZonesVisible(bool visible)
        {
            zonesVisible = visible;
            var vis = visible ? Visibility.Visible : Visibility.Collapsed;
            wireRect.Visibility = wireLbl.Visibility = vis;
            ringRect.Visibility = ringLbl.Visibility = vis;
            whTL.Visibility = whTR.Visibility = whBL.Visibility = whBR.Visibility =
            whTC.Visibility = whBC.Visibility = whLC.Visibility = whRC.Visibility = vis;
            rhTL.Visibility = rhTR.Visibility = rhBL.Visibility = rhBR.Visibility =
            rhTC.Visibility = rhBC.Visibility = rhLC.Visibility = rhRC.Visibility = vis;
            toggleZonesBtn.Content = visible ? "🙈 Сховати зони" : "👁 Зони";
        }
        SetZonesVisible(false); // початково — нічого не видно

        toggleZonesBtn.Click += (_, _) => SetZonesVisible(!zonesVisible);

        void UpdateHint()
        {
            switch (phase)
            {
                case CalibPhase.DrawingWire:
                    hintLbl.Text = "Намалюй прямокутник міні-гри (тягни мишею) → Enter для підтвердження";
                    hintLbl.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 220, 80));
                    break;
                case CalibPhase.DrawingRing:
                    hintLbl.Text = "Намалюй прямокутник кілець (тягни мишею) → Enter для підтвердження";
                    hintLbl.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 130, 255));
                    break;
                case CalibPhase.Sampling:
                    hintLbl.Text = "Клікай на пікселі щоб додати зразок (ПКМ — видалити)";
                    hintLbl.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(180, 180, 180));
                    break;
            }
        }
        UpdateHint();

        canvas.MouseLeftButtonDown += (_, e) =>
        {
            var pt = e.GetPosition(canvas);
            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double sx = cw / imgW, sy = ch / imgH;

            if (phase == CalibPhase.Sampling)
            {
                // ── У режимі sampling: handle-перетягування або додавання dot ──

                // Check wire handles first
                var wireHandles = new[]
                {
                    (DragTarget.WireTL, WireHandlePos(DragTarget.WireTL, cw, ch)),
                    (DragTarget.WireTR, WireHandlePos(DragTarget.WireTR, cw, ch)),
                    (DragTarget.WireBL, WireHandlePos(DragTarget.WireBL, cw, ch)),
                    (DragTarget.WireBR, WireHandlePos(DragTarget.WireBR, cw, ch)),
                    (DragTarget.WireTC, WireHandlePos(DragTarget.WireTC, cw, ch)),
                    (DragTarget.WireBC, WireHandlePos(DragTarget.WireBC, cw, ch)),
                    (DragTarget.WireLC, WireHandlePos(DragTarget.WireLC, cw, ch)),
                    (DragTarget.WireRC, WireHandlePos(DragTarget.WireRC, cw, ch)),
                };
                foreach (var (tgt, (hx, hy)) in wireHandles)
                {
                    if (Math.Abs(pt.X - hx) <= HandleHit && Math.Abs(pt.Y - hy) <= HandleHit)
                    {
                        dragTarget = tgt;
                        dragStart  = pt;
                        dragScanX1 = _cfg.ScanX1; dragScanX2 = _cfg.ScanX2;
                        dragTopWireY = _cfg.TopWireY; dragBotWireY = _cfg.BotWireY;
                        canvas.CaptureMouse();
                        return;
                    }
                }

                // Check ring handles
                var ringHandles = new[]
                {
                    (DragTarget.RingTL, RingHandlePos(DragTarget.RingTL, cw, ch)),
                    (DragTarget.RingTR, RingHandlePos(DragTarget.RingTR, cw, ch)),
                    (DragTarget.RingBL, RingHandlePos(DragTarget.RingBL, cw, ch)),
                    (DragTarget.RingBR, RingHandlePos(DragTarget.RingBR, cw, ch)),
                    (DragTarget.RingTC, RingHandlePos(DragTarget.RingTC, cw, ch)),
                    (DragTarget.RingBC, RingHandlePos(DragTarget.RingBC, cw, ch)),
                    (DragTarget.RingLC, RingHandlePos(DragTarget.RingLC, cw, ch)),
                    (DragTarget.RingRC, RingHandlePos(DragTarget.RingRC, cw, ch)),
                };
                foreach (var (tgt, (hx, hy)) in ringHandles)
                {
                    if (Math.Abs(pt.X - hx) <= HandleHit && Math.Abs(pt.Y - hy) <= HandleHit)
                    {
                        dragTarget = tgt;
                        dragStart  = pt;
                        dragRingTopX1 = _cfg.RingTopX1; dragRingTopX2 = _cfg.RingTopX2;
                        dragRingTopY  = _cfg.RingTopY;  dragRingBotY  = _cfg.RingBotY;
                        canvas.CaptureMouse();
                        return;
                    }
                }

                // No rect/handle hit → add sample dot
                double xPct = Math.Clamp(pt.X / (imgW * sx), 0, 1);
                double yPct = Math.Clamp(pt.Y / (imgH * sy), 0, 1);

                // ── Перевірка зони: Wire/Tip → тільки wireRect, Ring → тільки ringRect ──
                if (sampleMode == 0 || sampleMode == 2) // Wire or Tip
                {
                    bool inWire = xPct >= _cfg.ScanX1 && xPct <= _cfg.ScanX2
                               && yPct >= _cfg.TopWireY && yPct <= _cfg.BotWireY;
                    if (!inWire) return;
                }
                else // Ring
                {
                    bool inRing = xPct >= _cfg.RingTopX1 && xPct <= _cfg.RingTopX2
                               && yPct >= _cfg.RingTopY  && yPct <= _cfg.RingBotY;
                    if (!inRing) return;
                }

                string colorName = activeColor.ToString();
                if (!_cfg.ColorSamples.ContainsKey(colorName))
                    _cfg.ColorSamples[colorName] = new ColorSamples();
                var cs = _cfg.ColorSamples[colorName];
                var lst = sampleMode == 0 ? cs.Wire : sampleMode == 1 ? cs.Ring : cs.Tip;

                PixelSample sample;
                System.Windows.Media.Color fill2;
                var defE = WireColorDefs.FirstOrDefault(d => d.wc == activeColor);
                var borderCol2 = ParseHexColor(defE.hex != null ? defE.hex : "#888888");

                if (sampleMode == 2) // Tip — тільки XY, без кольору
                {
                    sample = new PixelSample { R = 0, G = 0, B = 0, XPct = xPct, YPct = yPct };
                    fill2 = System.Windows.Media.Color.FromRgb(255, 220, 0); // жовтий маркер
                }
                else // Wire або Ring — читаємо точний піксель
                {
                    int px = Math.Clamp((int)(xPct * (imgW - 1)), 0, (int)imgW - 1);
                    int py = Math.Clamp((int)(yPct * (imgH - 1)), 0, (int)imgH - 1);
                    var pxColor = SampleBitmapPixel(screenshot, px, py);
                    sample = new PixelSample { R = pxColor.R, G = pxColor.G, B = pxColor.B, XPct = xPct, YPct = yPct };
                    fill2 = System.Windows.Media.Color.FromRgb(pxColor.R, pxColor.G, pxColor.B);
                }

                lst.Add(sample);
                int newIdx = lst.Count - 1;
                _cfg.Save();

                var el = AddDot(xPct, yPct, colorName, sampleMode, newIdx, fill2, borderCol2, cw, ch);
                dotList.Add((el, colorName, sampleMode, newIdx));

                RebuildOverlay();
            }
            else
            {
                // ── Фаза малювання прямокутника ──
                // Показуємо відповідний прямокутник під час малювання
                if (phase == CalibPhase.DrawingWire)
                {
                    wireRect.Visibility = wireLbl.Visibility = Visibility.Visible;
                }
                else
                {
                    ringRect.Visibility = ringLbl.Visibility = Visibility.Visible;
                }
                isDrawingRect = true;
                rectDrawStart = pt;
                canvas.CaptureMouse();
            }
        };

        canvas.MouseMove += (_, e) =>
        {
            var pt = e.GetPosition(canvas);
            UpdateZoom(pt);

            double cw = canvas.ActualWidth, ch = canvas.ActualHeight;
            double sx = cw / imgW, sy = ch / imgH;

            if (isDrawingRect && phase != CalibPhase.Sampling)
            {
                // Оновлюємо cfg в реальному часі під час малювання
                double x1 = Math.Min(rectDrawStart.X, pt.X) / (imgW * sx);
                double x2 = Math.Max(rectDrawStart.X, pt.X) / (imgW * sx);
                double y1 = Math.Min(rectDrawStart.Y, pt.Y) / (imgH * sy);
                double y2 = Math.Max(rectDrawStart.Y, pt.Y) / (imgH * sy);
                x1 = Math.Clamp(x1, 0, 1); x2 = Math.Clamp(x2, 0, 1);
                y1 = Math.Clamp(y1, 0, 1); y2 = Math.Clamp(y2, 0, 1);

                if (phase == CalibPhase.DrawingWire)
                {
                    _cfg.ScanX1 = x1; _cfg.ScanX2 = x2;
                    _cfg.TopWireY = y1; _cfg.BotWireY = y2;
                }
                else // DrawingRing
                {
                    _cfg.RingTopX1 = x1; _cfg.RingBotX1 = x1;
                    _cfg.RingTopX2 = x2; _cfg.RingBotX2 = x2;
                    _cfg.RingTopY = y1; _cfg.RingBotY = y2;
                }
                Refresh(cw, ch);
                return;
            }

            if (dragTarget == DragTarget.None) return;

            double ddx = (pt.X - dragStart.X) / (imgW * sx);
            double ddy = (pt.Y - dragStart.Y) / (imgH * sy);

            double wW = dragScanX2   - dragScanX1;
            double wH = dragBotWireY - dragTopWireY;
            double rW = dragRingTopX2 - dragRingTopX1;
            double rH = dragRingBotY  - dragRingTopY;

            const double minSize = 0.01;

            switch (dragTarget)
            {
                // ── Wire body move ────────────────────────────────────────────
                case DragTarget.WireBody:
                {
                    double newL = Math.Clamp(dragScanX1 + ddx, 0, 1 - wW);
                    double newT = Math.Clamp(dragTopWireY + ddy, 0, 1 - wH);
                    _cfg.ScanX1    = newL;
                    _cfg.ScanX2    = newL + wW;
                    _cfg.TopWireY  = newT;
                    _cfg.BotWireY  = newT + wH;
                    break;
                }
                // ── Wire corner/edge handles ──────────────────────────────────
                case DragTarget.WireTL:
                    _cfg.ScanX1   = Math.Clamp(dragScanX1   + ddx, 0,        dragScanX2   - minSize);
                    _cfg.TopWireY = Math.Clamp(dragTopWireY + ddy, 0,        dragBotWireY - minSize);
                    break;
                case DragTarget.WireTR:
                    _cfg.ScanX2   = Math.Clamp(dragScanX2   + ddx, dragScanX1   + minSize, 1);
                    _cfg.TopWireY = Math.Clamp(dragTopWireY + ddy, 0,            dragBotWireY - minSize);
                    break;
                case DragTarget.WireBL:
                    _cfg.ScanX1   = Math.Clamp(dragScanX1   + ddx, 0,        dragScanX2   - minSize);
                    _cfg.BotWireY = Math.Clamp(dragBotWireY + ddy, dragTopWireY + minSize, 1);
                    break;
                case DragTarget.WireBR:
                    _cfg.ScanX2   = Math.Clamp(dragScanX2   + ddx, dragScanX1   + minSize, 1);
                    _cfg.BotWireY = Math.Clamp(dragBotWireY + ddy, dragTopWireY + minSize, 1);
                    break;
                case DragTarget.WireTC:
                    _cfg.TopWireY = Math.Clamp(dragTopWireY + ddy, 0, dragBotWireY - minSize);
                    break;
                case DragTarget.WireBC:
                    _cfg.BotWireY = Math.Clamp(dragBotWireY + ddy, dragTopWireY + minSize, 1);
                    break;
                case DragTarget.WireLC:
                    _cfg.ScanX1 = Math.Clamp(dragScanX1 + ddx, 0, dragScanX2 - minSize);
                    break;
                case DragTarget.WireRC:
                    _cfg.ScanX2 = Math.Clamp(dragScanX2 + ddx, dragScanX1 + minSize, 1);
                    break;

                // ── Ring body move ────────────────────────────────────────────
                case DragTarget.RingBody:
                {
                    double newL = Math.Clamp(dragRingTopX1 + ddx, 0, 1 - rW);
                    double newT = Math.Clamp(dragRingTopY  + ddy, 0, 1 - rH);
                    _cfg.RingTopX1 = newL; _cfg.RingBotX1 = newL;
                    _cfg.RingTopX2 = newL + rW; _cfg.RingBotX2 = newL + rW;
                    _cfg.RingTopY  = newT;
                    _cfg.RingBotY  = newT + rH;
                    break;
                }
                // ── Ring corner/edge handles ──────────────────────────────────
                case DragTarget.RingTL:
                {
                    double newL = Math.Clamp(dragRingTopX1 + ddx, 0, dragRingTopX2 - minSize);
                    _cfg.RingTopX1 = newL; _cfg.RingBotX1 = newL;
                    _cfg.RingTopY  = Math.Clamp(dragRingTopY + ddy, 0, dragRingBotY - minSize);
                    break;
                }
                case DragTarget.RingTR:
                {
                    double newR = Math.Clamp(dragRingTopX2 + ddx, dragRingTopX1 + minSize, 1);
                    _cfg.RingTopX2 = newR; _cfg.RingBotX2 = newR;
                    _cfg.RingTopY  = Math.Clamp(dragRingTopY + ddy, 0, dragRingBotY - minSize);
                    break;
                }
                case DragTarget.RingBL:
                {
                    double newL = Math.Clamp(dragRingTopX1 + ddx, 0, dragRingTopX2 - minSize);
                    _cfg.RingTopX1 = newL; _cfg.RingBotX1 = newL;
                    _cfg.RingBotY  = Math.Clamp(dragRingBotY + ddy, dragRingTopY + minSize, 1);
                    break;
                }
                case DragTarget.RingBR:
                {
                    double newR = Math.Clamp(dragRingTopX2 + ddx, dragRingTopX1 + minSize, 1);
                    _cfg.RingTopX2 = newR; _cfg.RingBotX2 = newR;
                    _cfg.RingBotY  = Math.Clamp(dragRingBotY + ddy, dragRingTopY + minSize, 1);
                    break;
                }
                case DragTarget.RingTC:
                    _cfg.RingTopY = Math.Clamp(dragRingTopY + ddy, 0, dragRingBotY - minSize);
                    break;
                case DragTarget.RingBC:
                    _cfg.RingBotY = Math.Clamp(dragRingBotY + ddy, dragRingTopY + minSize, 1);
                    break;
                case DragTarget.RingLC:
                {
                    double newL = Math.Clamp(dragRingTopX1 + ddx, 0, dragRingTopX2 - minSize);
                    _cfg.RingTopX1 = newL; _cfg.RingBotX1 = newL;
                    break;
                }
                case DragTarget.RingRC:
                {
                    double newR = Math.Clamp(dragRingTopX2 + ddx, dragRingTopX1 + minSize, 1);
                    _cfg.RingTopX2 = newR; _cfg.RingBotX2 = newR;
                    break;
                }
            }

            Refresh(cw, ch);
        };

        canvas.MouseUp += (_, e) =>
        {
            if (isDrawingRect)
            {
                isDrawingRect = false;
                canvas.ReleaseMouseCapture();
                // Прямокутник намальований але ще не підтверджений
                return;
            }
            if (dragTarget != DragTarget.None)
            {
                _cfg.Save();
                dragTarget = DragTarget.None;
                canvas.ReleaseMouseCapture();
            }
        };

        win.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { _cfg.Save(); win.Close(); return; }

            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (phase == CalibPhase.DrawingWire)
                {
                    _cfg.Save();
                    // Сховати прямокутник міні-гри перед малюванням кілець
                    wireRect.Visibility = wireLbl.Visibility = Visibility.Collapsed;
                    phase = CalibPhase.DrawingRing;
                    UpdateHint();
                }
                else if (phase == CalibPhase.DrawingRing)
                {
                    _cfg.Save();
                    // Ховаємо обидва прямокутники — переходимо у режим семплування
                    SetZonesVisible(false);
                    toggleZonesBtn.Visibility = Visibility.Visible;
                    phase = CalibPhase.Sampling;
                    UpdateHint();
                    Refresh(canvas.ActualWidth, canvas.ActualHeight);
                    if (!dotsLoaded && canvas.ActualWidth > 0)
                    {
                        dotsLoaded = true;
                        LoadExistingDots();
                    }
                }
            }
        };
        win.Closing += (_, _) => _cfg.Save();
        win.Content  = grid;
        win.Show();
    }

    // Зчитати піксель із BitmapImage (frozen, в UI thread)
    static System.Drawing.Color SampleBitmapPixel(System.Windows.Media.Imaging.BitmapImage bi, int x, int y)
    {
        try
        {
            var fmt = System.Windows.Media.PixelFormats.Bgr32;
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(bi, fmt, null, 0);
            int stride = (conv.PixelWidth * fmt.BitsPerPixel + 7) / 8;
            byte[] buf = new byte[stride * conv.PixelHeight];
            conv.CopyPixels(buf, stride, 0);
            int idx = y * stride + x * 4;
            if (idx + 2 >= buf.Length) return System.Drawing.Color.Gray;
            return System.Drawing.Color.FromArgb(buf[idx + 2], buf[idx + 1], buf[idx]);
        }
        catch { return System.Drawing.Color.Gray; }
    }

    // DotXPctProperty / DotYPctProperty — зберігаємо xPct/yPct у Tag кортежем
    static readonly System.Windows.DependencyProperty DotXPctProperty =
        System.Windows.DependencyProperty.RegisterAttached("DotXPct", typeof(double),
            typeof(WiresModule), new System.Windows.PropertyMetadata(0.0));
    static readonly System.Windows.DependencyProperty DotYPctProperty =
        System.Windows.DependencyProperty.RegisterAttached("DotYPct", typeof(double),
            typeof(WiresModule), new System.Windows.PropertyMetadata(0.0));

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

                // Сканування проводів по кольору (рандомні позиції кожен раунд)
                var topWires    = WireScanner.FindTopWireTips(bmp, screen, _cfg);
                var bottomWires = WireScanner.FindBottomWireTips(bmp, screen, _cfg);
                var allWires    = topWires.Concat(bottomWires).ToList();

                // Сканування кілець
                var (topRings, bottomRings) = WireScanner.FindRings(bmp, _cfg);
                var allRings = topRings.Concat(bottomRings).ToList();

                _log($"Проводи: верх={topWires.Count} [{string.Join(",", topWires.Select(w => w.color))}] | низ={bottomWires.Count} [{string.Join(",", bottomWires.Select(w => w.color))}]");
                _log($"Кільця:  верх={topRings.Count} [{string.Join(",", topRings.Select(r => r.color))}] | низ={bottomRings.Count} [{string.Join(",", bottomRings.Select(r => r.color))}]");

                // Перетворюємо список проводів у словник колір→точка
                var tipPoints = allWires
                    .GroupBy(w => w.color)
                    .ToDictionary(g => g.Key, g => g.First().pos);

                SaveDebugImage(bmp, tipPoints, topRings, bottomRings, _debugFrame++, _cfg);

                // Готово якщо знайдено 10 проводів і 10 кілець без дублікатів
                bool wiresReady = tipPoints.Count == 10;
                bool ringsReady = allRings.Count >= 10 || tipPoints.Keys.All(wc => allRings.Any(r => r.color == wc));

                if (wiresReady && ringsReady)
                {
                    _log("Починаю з'єднання...");
                    ConnectWires(tipPoints, allRings, screen);
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
        Dictionary<WireColor, System.Drawing.Point> tipPoints,
        List<(System.Drawing.Point center, WireColor color)> rings,
        System.Drawing.Rectangle screen)
    {
        // Перебираємо кольори що треба з'єднати
        var remaining = tipPoints.Keys.ToList();

        foreach (var wireColor in remaining)
        {
            var ringMatch = rings.Find(r => r.color == wireColor);
            if (ringMatch.center == default) { _log($"⚠ Кільце для {wireColor} не знайдено"); continue; }

            // Перескановуємо поточний стан проводів перед кожним drag
            // (після попереднього drag провід зникає і решта зсувається)
            using var freshBmp = ScreenCapture.Capture(screen);
            var freshTop    = WireScanner.FindTopWireTips(freshBmp, screen, _cfg);
            var freshBot    = WireScanner.FindBottomWireTips(freshBmp, screen, _cfg);
            var freshWires  = freshTop.Concat(freshBot).ToDictionary(w => w.color, w => w.pos);

            System.Drawing.Point tipPt;
            if (!freshWires.TryGetValue(wireColor, out tipPt))
            {
                // Якщо не знайшли свіжу позицію — беремо стару
                tipPt = tipPoints[wireColor];
                _log($"⚠ Свіжу позицію {wireColor} не знайдено, використовую стару");
            }

            int fromX = screen.X + tipPt.X;
            int fromY = screen.Y + tipPt.Y;
            int toX   = screen.X + ringMatch.center.X;
            int toY   = screen.Y + ringMatch.center.Y;

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

    static void SaveDebugImage(
        Bitmap bmp,
        Dictionary<WireColor, System.Drawing.Point> tipPoints,
        List<(System.Drawing.Point center, WireColor color)> topRings,
        List<(System.Drawing.Point center, WireColor color)> bottomRings,
        int frame, WiresConfig cfg)
    {
        try
        {
            using var dbg = (Bitmap)bmp.Clone();
            using var g   = System.Drawing.Graphics.FromImage(dbg);

            int ringTopY = (int)(bmp.Height * cfg.RingTopY);
            int ringBotY = (int)(bmp.Height * cfg.RingBotY);

            var penRing = new System.Drawing.Pen(System.Drawing.Color.FromArgb(220, System.Drawing.Color.MediumPurple), 2);
            g.DrawLine(penRing, 0, ringTopY, bmp.Width, ringTopY);
            g.DrawLine(penRing, 0, ringBotY, bmp.Width, ringBotY);

            foreach (var (col, pos) in tipPoints)
            {
                DrawCross(g, pos, System.Drawing.Color.Orange, 10);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Orange, pos.X + 4, pos.Y - 14);
            }

            foreach (var (center, col) in topRings)
            {
                g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Yellow, 3), center.X - 12, center.Y - 12, 24, 24);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Yellow, center.X + 14, center.Y - 6);
            }

            foreach (var (center, col) in bottomRings)
            {
                g.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Lime, 3), center.X - 12, center.Y - 12, 24, 24);
                g.DrawString(col.ToString(), new System.Drawing.Font("Arial", 8), System.Drawing.Brushes.Lime, center.X + 14, center.Y - 6);
            }

            string dir  = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wires_debug");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"frame_{frame:D4}.png");
            dbg.Save(path, System.Drawing.Imaging.ImageFormat.Png);

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== frame {frame} ===");
            sb.AppendLine("TIP POINTS:");
            foreach (var (col, pos) in tipPoints)
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y})");
            sb.AppendLine("TOP RINGS:");
            foreach (var (pos, col) in topRings)
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y})");
            sb.AppendLine("BOTTOM RINGS:");
            foreach (var (pos, col) in bottomRings)
                sb.AppendLine($"  {col} @ ({pos.X},{pos.Y})");
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
