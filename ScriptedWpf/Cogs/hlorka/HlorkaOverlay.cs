using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ScriptedWpf.Cogs.Hlorka;

/// <summary>
/// Напівпрозоре повноекранне вікно для виділення зони сканування мишею.
/// Після успішного виділення заповнює RegionX/Y/W/H (частки 0–1) і закривається.
/// </summary>
sealed class HlorkaOverlay : Window
{
    readonly System.Windows.Forms.Screen _screen;

    Canvas    _canvas = null!;
    Rectangle _selRect = null!;
    TextBlock _hintLbl = null!;

    Point _dragStart;
    bool  _dragging;

    // ── Результат виділення ───────────────────────────────────────────────────
    public double RegionX { get; private set; }
    public double RegionY { get; private set; }
    public double RegionW { get; private set; }
    public double RegionH { get; private set; }

    public HlorkaOverlay(System.Windows.Forms.Screen screen)
    {
        _screen = screen;

        // ── Властивості вікна ─────────────────────────────────────────────────
        WindowStyle       = WindowStyle.None;
        ResizeMode        = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background        = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
        Topmost           = true;
        ShowInTaskbar     = false;
        Cursor            = Cursors.Cross;

        Left   = screen.Bounds.X;
        Top    = screen.Bounds.Y;
        Width  = screen.Bounds.Width;
        Height = screen.Bounds.Height;

        BuildContent();

        MouseLeftButtonDown += OnMouseDown;
        MouseMove           += OnMouseMove;
        MouseLeftButtonUp   += OnMouseUp;
        KeyDown             += (_, e) =>
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        };
    }

    void BuildContent()
    {
        _canvas = new Canvas();
        Content = _canvas;

        // Підказка вгорі зліва
        _hintLbl = new TextBlock
        {
            Text       = "Виділіть зону з міні-грою  (затисни мишу та тягни)\nESC — скасувати",
            Foreground = Brushes.White,
            FontSize   = 18,
            FontWeight = FontWeights.SemiBold,
            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
            Padding    = new Thickness(16, 8, 16, 8),
        };
        Canvas.SetLeft(_hintLbl, 40);
        Canvas.SetTop(_hintLbl, 40);
        _canvas.Children.Add(_hintLbl);

        // Прямокутник виділення
        _selRect = new Rectangle
        {
            Stroke          = Brushes.Lime,
            StrokeThickness = 2,
            Fill            = new SolidColorBrush(Color.FromArgb(50, 0, 255, 80)),
            Visibility      = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selRect);
    }

    void OnMouseDown(object s, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_canvas);
        _dragging  = true;
        _selRect.Visibility = Visibility.Visible;
        CaptureMouse();
    }

    void OnMouseMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateRect(e.GetPosition(_canvas));
    }

    void OnMouseUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        var cur = e.GetPosition(_canvas);
        double x = Math.Min(_dragStart.X, cur.X);
        double y = Math.Min(_dragStart.Y, cur.Y);
        double w = Math.Abs(cur.X - _dragStart.X);
        double h = Math.Abs(cur.Y - _dragStart.Y);

        // Ігноруємо випадковий клік без виділення
        if (w < 10 || h < 10) { _selRect.Visibility = Visibility.Collapsed; return; }

        double sw = _screen.Bounds.Width;
        double sh = _screen.Bounds.Height;

        RegionX = x / sw;
        RegionY = y / sh;
        RegionW = w / sw;
        RegionH = h / sh;

        DialogResult = true;
        Close();
    }

    void UpdateRect(Point cur)
    {
        double x = Math.Min(_dragStart.X, cur.X);
        double y = Math.Min(_dragStart.Y, cur.Y);
        double w = Math.Abs(cur.X - _dragStart.X);
        double h = Math.Abs(cur.Y - _dragStart.Y);

        Canvas.SetLeft(_selRect, x);
        Canvas.SetTop(_selRect, y);
        _selRect.Width  = w;
        _selRect.Height = h;

        // Оновлюємо підказку з поточними координатами
        double sw = _screen.Bounds.Width;
        double sh = _screen.Bounds.Height;
        _hintLbl.Text =
            $"X={x / sw:F3}  Y={y / sh:F3}  W={w / sw:F3}  H={h / sh:F3}\n" +
            "Відпусти щоб підтвердити  ·  ESC — скасувати";
    }
}
