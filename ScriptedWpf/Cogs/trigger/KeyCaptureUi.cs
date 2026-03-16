using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfBrush = System.Windows.Media.Brush;

namespace ScriptedWpf.Cogs.Trigger;

/// <summary>
/// Reusable key-capture widget: click the border, press a key or combo,
/// it records it as "CTRL+E", "F9", "SHIFT+F5", etc.
/// </summary>
static class KeyCaptureUi
{
    public static Border Make(string currentKey, WpfBrush textSec,
        WpfBrush borderB, WpfBrush bgCard, Action<string> onChanged)
    {
        bool listening = false;

        var display = new TextBlock
        {
            Text              = string.IsNullOrEmpty(currentKey) ? "— натисни клавішу —" : currentKey,
            Foreground        = textSec,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(8, 0, 8, 0),
            FontSize          = 12,
        };

        var border = new Border
        {
            BorderBrush     = borderB,
            BorderThickness = new Thickness(1),
            Background      = bgCard,
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(0, 5, 0, 5),
            MinWidth        = 140,
            Cursor          = Cursors.Hand,
            Child           = display,
            Focusable       = true,
        };

        void SetListening(bool on)
        {
            listening          = on;
            display.Text       = on
                ? "[ натискай... ]"
                : (string.IsNullOrEmpty(currentKey) ? "— натисни клавішу —" : currentKey);
            display.Foreground = on
                ? (WpfBrush)Application.Current.Resources["AccentBrush"]
                : textSec;
        }

        border.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetListening(true);
            border.Focus();
        };

        border.LostFocus += (_, _) =>
        {
            if (listening) SetListening(false);
        };

        border.KeyDown += (_, e) =>
        {
            if (!listening) return;
            e.Handled = true;

            // Ignore lone modifier presses — wait for a real key
            if (e.Key is Key.LeftCtrl or Key.RightCtrl
                      or Key.LeftShift or Key.RightShift
                      or Key.LeftAlt or Key.RightAlt
                      or Key.System or Key.LWin or Key.RWin)
                return;

            var mods  = Keyboard.Modifiers;
            var parts = new System.Collections.Generic.List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("CTRL");
            if (mods.HasFlag(ModifierKeys.Shift))   parts.Add("SHIFT");
            if (mods.HasFlag(ModifierKeys.Alt))     parts.Add("ALT");

            // Convert WPF Key enum to display name
            string keyName = e.Key.ToString().ToUpperInvariant();
            // "D0".."D9"  →  "0".."9"
            if (keyName.Length == 2 && keyName[0] == 'D' && char.IsDigit(keyName[1]))
                keyName = keyName[1].ToString();

            parts.Add(keyName);
            currentKey = string.Join("+", parts);

            display.Text       = currentKey;
            display.Foreground = textSec;
            listening          = false;
            onChanged(currentKey);
        };

        return border;
    }
}
