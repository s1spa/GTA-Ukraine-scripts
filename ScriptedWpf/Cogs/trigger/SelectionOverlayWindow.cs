using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WpfColor   = System.Windows.Media.Color;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfSCB     = System.Windows.Media.SolidColorBrush;
namespace ScriptedWpf.Cogs.Trigger;

/// <summary>
/// Full-screen overlay window showing the captured screenshot.
/// User drags to select a region. On mouse release fires Selected event.
/// </summary>
sealed class SelectionOverlayWindow : Window
{
    readonly Bitmap _fullBmp;
    readonly System.Windows.Forms.Screen _screen;

    System.Windows.Point _start;
    bool _dragging;

    readonly Canvas                        _canvas;
    readonly System.Windows.Shapes.Rectangle _selRect;
    readonly System.Windows.Controls.Image _bgImage;

    public event Action<System.Drawing.Rectangle>? Selected;

    public SelectionOverlayWindow(Bitmap fullBmp, System.Windows.Forms.Screen screen)
    {
        _fullBmp = fullBmp;
        _screen  = screen;

        WindowStyle     = WindowStyle.None;
        AllowsTransparency = true;
        Background      = WpfBrushes.Transparent;
        Topmost         = true;
        ShowInTaskbar   = false;
        Cursor          = Cursors.Cross;

        var bounds = screen.Bounds;
        Left   = bounds.Left;
        Top    = bounds.Top;
        Width  = bounds.Width;
        Height = bounds.Height;

        _canvas  = new Canvas();
        Content  = _canvas;

        // Background screenshot
        _bgImage = new System.Windows.Controls.Image
        {
            Width  = bounds.Width,
            Height = bounds.Height,
            Source = BitmapToSource(fullBmp),
            Opacity = 0.85,
        };
        Canvas.SetLeft(_bgImage, 0);
        Canvas.SetTop(_bgImage, 0);
        _canvas.Children.Add(_bgImage);

        // Dim overlay
        var dim = new System.Windows.Shapes.Rectangle  // WPF shape
        {
            Width  = bounds.Width,
            Height = bounds.Height,
            Fill   = new WpfSCB(WpfColor.FromArgb(100, 0, 0, 0)),
        };
        Canvas.SetLeft(dim, 0);
        Canvas.SetTop(dim, 0);
        _canvas.Children.Add(dim);

        // Instruction text
        var lbl = new TextBlock
        {
            Text       = "Виділи зону мишею, відпусти — зона буде збережена. ESC — скасувати.",
            Foreground = WpfBrushes.White,
            FontSize   = 14,
            Background = new WpfSCB(WpfColor.FromArgb(160, 0, 0, 0)),
            Padding    = new Thickness(10, 5, 10, 5),
        };
        Canvas.SetLeft(lbl, bounds.Width / 2.0 - 280);
        Canvas.SetTop(lbl, 16);
        _canvas.Children.Add(lbl);

        // Selection rectangle
        _selRect = new System.Windows.Shapes.Rectangle  // WPF shape
        {
            Stroke          = new WpfSCB(WpfColor.FromRgb(0x4C, 0xFF, 0x72)),
            StrokeThickness = 2,
            Fill            = new WpfSCB(WpfColor.FromArgb(40, 76, 255, 114)),
            Visibility      = Visibility.Collapsed,
        };
        _canvas.Children.Add(_selRect);

        _canvas.MouseLeftButtonDown += OnMouseDown;
        _canvas.MouseMove           += OnMouseMove;
        _canvas.MouseLeftButtonUp   += OnMouseUp;
        KeyDown                     += OnKeyDown;
    }

    void OnMouseDown(object s, MouseButtonEventArgs e)
    {
        _start    = e.GetPosition(_canvas);
        _dragging = true;
        _canvas.CaptureMouse();
        _selRect.Visibility = Visibility.Visible;
        UpdateRect(_start, _start);
    }

    void OnMouseMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        UpdateRect(_start, e.GetPosition(_canvas));
    }

    void OnMouseUp(object s, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _canvas.ReleaseMouseCapture();

        var end = e.GetPosition(_canvas);
        var r   = MakeRect(_start, end);
        Close();

        // Convert WPF coords (device-independent) to screen pixels
        // DPI scaling: WPF uses 96 dpi baseline
        var source = PresentationSource.FromVisual(this);
        double dpiX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double dpiY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        var dr = new System.Drawing.Rectangle(
            (int)(r.X      * dpiX),
            (int)(r.Y      * dpiY),
            (int)(r.Width  * dpiX),
            (int)(r.Height * dpiY));

        Selected?.Invoke(dr);
    }

    void OnKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            Selected?.Invoke(System.Drawing.Rectangle.Empty);
        }
    }

    void UpdateRect(System.Windows.Point a, System.Windows.Point b)
    {
        var r = MakeRect(a, b);
        Canvas.SetLeft(_selRect, r.X);
        Canvas.SetTop(_selRect,  r.Y);
        _selRect.Width  = r.Width;
        _selRect.Height = r.Height;
    }

    static Rect MakeRect(System.Windows.Point a, System.Windows.Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    static BitmapSource BitmapToSource(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Bmp);
        ms.Seek(0, SeekOrigin.Begin);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption  = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}
