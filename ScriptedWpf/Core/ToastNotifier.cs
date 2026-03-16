using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ScriptedWpf.Core;

public enum ToastIcon { Success, Info, Warning }

public static class ToastNotifier
{
    public static void Show(string title, string message, ToastIcon icon = ToastIcon.Info)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            new ToastWindow(title, message, icon).Show();
        });
    }
}

internal sealed class ToastWindow : Window
{
    internal ToastWindow(string title, string message, ToastIcon icon)
    {
        WindowStyle       = WindowStyle.None;
        AllowsTransparency = true;
        Background        = Brushes.Transparent;
        ShowInTaskbar     = false;
        Topmost           = true;
        SizeToContent     = SizeToContent.WidthAndHeight;
        ResizeMode        = ResizeMode.NoResize;

        var area = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        Left = area.Right - 320;
        Top  = area.Top  + 24;

        Content = BuildContent(title, message, icon);
        Loaded += (_, _) => Animate();
    }

    FrameworkElement BuildContent(string title, string message, ToastIcon icon)
    {
        var border = new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x1e, 0x1e, 0x1e)),
            CornerRadius  = new CornerRadius(12),
            Padding       = new Thickness(14, 12, 18, 12),
            Width         = 290,
            Effect        = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color       = Colors.Black,
                BlurRadius  = 24,
                Opacity     = 0.55,
                ShadowDepth = 4
            }
        };

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Icon ────────────────────────────────────────────────────────────
        var iconColor = icon switch
        {
            ToastIcon.Success => Color.FromRgb(0x30, 0xd1, 0x58),
            ToastIcon.Warning => Color.FromRgb(0xff, 0x9f, 0x0a),
            _                 => Color.FromRgb(0x00, 0x8a, 0xff),
        };

        var circle = new System.Windows.Shapes.Ellipse
        {
            Width  = 28, Height = 28,
            Fill   = new SolidColorBrush(iconColor)
        };
        var glyph = new TextBlock
        {
            Text                = icon == ToastIcon.Success ? "✓" : icon == ToastIcon.Warning ? "!" : "i",
            Foreground          = Brushes.White,
            FontSize            = 13,
            FontWeight          = FontWeights.Bold,
            Width               = 28, Height = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            TextAlignment       = TextAlignment.Center,
            Padding             = new Thickness(0, icon == ToastIcon.Success ? 3 : 2, 0, 0)
        };
        var iconPanel = new Grid
        {
            Margin              = new Thickness(0, 0, 12, 0),
            VerticalAlignment   = VerticalAlignment.Center
        };
        iconPanel.Children.Add(circle);
        iconPanel.Children.Add(glyph);
        Grid.SetColumn(iconPanel, 0);

        // ── Text ─────────────────────────────────────────────────────────────
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text       = title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize   = 13
        });
        textStack.Children.Add(new TextBlock
        {
            Text        = message,
            Foreground  = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x9f)),
            FontSize    = 12,
            Margin      = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(textStack, 1);

        root.Children.Add(iconPanel);
        root.Children.Add(textStack);
        border.Child = root;
        return border;
    }

    void Animate()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(350));
            fadeOut.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fadeOut);
        };
        timer.Start();
    }
}
