using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WpfColor = System.Windows.Media.Color;
using WpfSCB   = System.Windows.Media.SolidColorBrush;
using WpfBrush = System.Windows.Media.Brush;
using ScriptedWpf.Core;
using ScriptedWpf.Models;

namespace ScriptedWpf.Cogs.Trigger;

/// <summary>
/// Manager module: shows wizard to create new triggers.
/// Each created trigger becomes an independent TriggerActionModule registered dynamically.
/// </summary>
public sealed class TriggerModule : IModule
{
    public string Id        => "trigger";
    public bool   IsRunning => false;

    public event Action?                           StateChanged;
    public event Action<TriggerActionModule>?      ModuleCreated;
    public event Action<TriggerActionModule>?      ModuleDeleted;

    Action<string> _log = Console.WriteLine;

    public void Initialize(Action<string> log) => _log = log;
    public void Start()  { }
    public void Stop()   { }

    public List<TriggerActionModule> LoadExisting()
    {
        var list = new List<TriggerActionModule>();
        var cfg  = TriggerConfig.Load();
        foreach (var d in cfg.Actions)
        {
            var m = new TriggerActionModule(d);
            m.DeleteRequested += OnDeleteRequested;
            list.Add(m);
        }
        return list;
    }

    public FrameworkElement? GetSettingsView() => BuildManagerPanel();

    // ── Wizard ────────────────────────────────────────────────────────────────

    FrameworkElement BuildManagerPanel()
    {
        var textDim = (WpfBrush)Application.Current.Resources["TextDimBrush"];
        var textSec = (WpfBrush)Application.Current.Resources["TextSecondaryBrush"];
        var accent  = (WpfBrush)Application.Current.Resources["AccentBrush"];
        var bgCard  = (WpfBrush)Application.Current.Resources["BgCardBrush"];
        var borderB = (WpfBrush)Application.Current.Resources["BorderBrush"];

        var info  = ModuleInfo.Load("trigger");
        var panel = new StackPanel();

        // ── Instructions ──────────────────────────────────────────────────────
        var instr = new TextBlock
        {
            Text = "ІНСТРУКЦІЯ\n" + info.Instruction,
            Foreground   = textDim,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16),
        };
        panel.Children.Add(instr);

        // ── Wizard form ───────────────────────────────────────────────────────
        var card = new Border
        {
            Background      = bgCard,
            BorderBrush     = borderB,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(6),
            Padding         = new Thickness(14),
            Margin          = new Thickness(0, 0, 0, 8),
        };
        var form = new StackPanel();
        card.Child = form;

        // Name
        form.Children.Add(MakeLabel("Назва тригера:", textDim));
        var nameBox = new TextBox
        {
            Text    = "Новий тригер",
            Margin  = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(6, 4, 6, 4),
        };
        form.Children.Add(nameBox);

        // Monitor selection
        form.Children.Add(MakeLabel("Монітор:", textDim));
        var monitorCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        var screens = System.Windows.Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            monitorCombo.Items.Add($"{i}: {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (основний)" : "")}");
        }
        monitorCombo.SelectedIndex = 0;
        form.Children.Add(monitorCombo);

        // Action type
        form.Children.Add(MakeLabel("Тип дії:", textDim));
        var actionCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        actionCombo.Items.Add("Натиснути клавішу");
        actionCombo.Items.Add("Звуковий сигнал");
        actionCombo.SelectedIndex = 0;
        form.Children.Add(actionCombo);

        // Key row
        string capturedActionKey = "E";
        var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        keyRow.Children.Add(MakeLabel("Клавіша:", textDim, 80));
        keyRow.Children.Add(KeyCaptureUi.Make(capturedActionKey, textSec, borderB, bgCard,
            v => capturedActionKey = v));
        form.Children.Add(keyRow);

        // Sound row
        var soundRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        soundRow.Visibility = Visibility.Collapsed;
        soundRow.Children.Add(MakeLabel("Файл .wav:", textDim, 80));
        var soundBox = new TextBox { Text = "", Width = 180, Padding = new Thickness(6, 4, 6, 4) };
        var browseBtn = new Button
        {
            Content = "...",
            Width   = 28,
            Margin  = new Thickness(4, 0, 0, 0),
            Padding = new Thickness(2),
            Cursor  = Cursors.Hand,
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "WAV files|*.wav",
                Title  = "Вибери звуковий файл",
            };
            if (dlg.ShowDialog() == true) soundBox.Text = dlg.FileName;
        };
        soundRow.Children.Add(soundBox);
        soundRow.Children.Add(browseBtn);
        form.Children.Add(soundRow);

        // Sound interval row
        var intervalRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        intervalRow.Visibility = Visibility.Collapsed;
        intervalRow.Children.Add(MakeLabel("Інтервал (сек):", textDim, 120));
        var intervalBox = new TextBox { Text = "3", Width = 50, Padding = new Thickness(6, 4, 6, 4) };
        intervalRow.Children.Add(intervalBox);
        form.Children.Add(intervalRow);

        actionCombo.SelectionChanged += (_, _) =>
        {
            bool isSound = actionCombo.SelectedIndex == 1;
            keyRow.Visibility      = isSound ? Visibility.Collapsed : Visibility.Visible;
            soundRow.Visibility    = isSound ? Visibility.Visible   : Visibility.Collapsed;
            intervalRow.Visibility = isSound ? Visibility.Visible   : Visibility.Collapsed;
        };

        // Hotkey
        string capturedHotkey = "F9";
        form.Children.Add(MakeLabel("Хоткей вмк/вимк:", textDim));
        var hotkeyCapture = KeyCaptureUi.Make(capturedHotkey, textSec, borderB, bgCard,
            v => capturedHotkey = v);
        hotkeyCapture.Margin = new Thickness(0, 0, 0, 10);
        form.Children.Add(hotkeyCapture);

        // Match threshold
        var thrRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        thrRow.Children.Add(MakeLabel("Поріг збігу %:", textDim, 120));
        var thrSlider = new Slider { Minimum = 50, Maximum = 100, Value = 85, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        var thrLbl    = new TextBlock { Text = "85", Foreground = textSec, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), Width = 30 };
        thrSlider.ValueChanged += (_, _) => thrLbl.Text = ((int)thrSlider.Value).ToString();
        thrRow.Children.Add(thrSlider);
        thrRow.Children.Add(thrLbl);
        form.Children.Add(thrRow);

        // Status label
        var statusLbl = new TextBlock
        {
            Text       = "",
            Foreground = textDim,
            FontSize   = 11,
            Margin     = new Thickness(0, 4, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        form.Children.Add(statusLbl);

        // Add button
        var addBtn = new Button
        {
            Content             = "➕ Додати дію",
            Background          = accent,
            Foreground          = new WpfSCB(WpfColor.FromRgb(0x0D, 0x0D, 0x0F)),
            BorderThickness     = new Thickness(0),
            FontWeight          = FontWeights.SemiBold,
            Padding             = new Thickness(14, 7, 14, 7),
            Cursor              = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        addBtn.Click += (_, _) =>
        {
            addBtn.IsEnabled = false;
            statusLbl.Foreground = textDim;
            statusLbl.Text = "⏳ Через 5 секунд зробиться скріншот...";

            new Thread(() =>
            {
                Thread.Sleep(5000);

                int monIdx = 0;
                Application.Current.Dispatcher.Invoke(() => monIdx = monitorCombo.SelectedIndex);
                var screen = TriggerLogic.GetMonitor(monIdx);

                // Full screen capture
                Bitmap fullBmp;
                try { fullBmp = TriggerLogic.CaptureRect(screen.Bounds); }
                catch
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        statusLbl.Text = "❌ Помилка скріншоту.";
                        addBtn.IsEnabled = true;
                    });
                    return;
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    statusLbl.Text = "✅ Скріншот готовий. Виділи зону...";
                    ShowSelectionOverlay(fullBmp, screen, (selRect) =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (selRect.Width < 4 || selRect.Height < 4)
                            {
                                statusLbl.Text = "❌ Зона занадто мала. Спробуй ще раз.";
                                addBtn.IsEnabled = true;
                                fullBmp.Dispose();
                                return;
                            }

                            // Crop selection from fullBmp
                            Bitmap cropped;
                            try
                            {
                                cropped = fullBmp.Clone(selRect, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                            }
                            catch
                            {
                                statusLbl.Text = "❌ Помилка обрізки.";
                                addBtn.IsEnabled = true;
                                fullBmp.Dispose();
                                return;
                            }
                            fullBmp.Dispose();

                            string name   = nameBox.Text.Trim();
                            if (string.IsNullOrEmpty(name)) name = "Тригер";

                            string id     = "trigger_" + Guid.NewGuid().ToString("N")[..8];
                            string imgDir = Path.Combine(TriggerConfig.BaseDir, "images");
                            Directory.CreateDirectory(imgDir);
                            string imgPath = Path.Combine(imgDir, id + ".png");
                            cropped.Save(imgPath, ImageFormat.Png);
                            cropped.Dispose();

                            bool isSound = actionCombo.SelectedIndex == 1;
                            int  interval = int.TryParse(intervalBox.Text, out int iv) ? Math.Max(1, iv) : 3;

                            var data = new TriggerActionData
                            {
                                Id              = id,
                                Name            = name,
                                ImagePath       = imgPath,
                                CaptureX        = selRect.X,
                                CaptureY        = selRect.Y,
                                CaptureW        = selRect.Width,
                                CaptureH        = selRect.Height,
                                MonitorIndex    = monIdx,
                                MonitorW        = screen.Bounds.Width,
                                MonitorH        = screen.Bounds.Height,
                                Hotkey          = capturedHotkey,
                                Action          = isSound ? "sound" : "key",
                                ActionKey       = capturedActionKey,
                                SoundFile       = soundBox.Text.Trim(),
                                SoundIntervalSec = interval,
                                MatchThreshold  = thrSlider.Value / 100.0,
                            };

                            var cfg = TriggerConfig.Load();
                            cfg.Actions.Add(data);
                            cfg.Save();

                            var module = new TriggerActionModule(data);
                            module.DeleteRequested += OnDeleteRequested;
                            ModuleCreated?.Invoke(module);

                            statusLbl.Foreground = (WpfBrush)Application.Current.Resources["AccentBrush"];
                            statusLbl.Text = $"✅ Тригер «{name}» створено! З'явився у меню ліворуч.";
                            addBtn.IsEnabled = true;
                            nameBox.Text = "Новий тригер";
                        });
                    });
                });
            }) { IsBackground = true }.Start();
        };

        form.Children.Add(addBtn);
        panel.Children.Add(card);
        return panel;
    }

    // ── Selection overlay ─────────────────────────────────────────────────────

    void ShowSelectionOverlay(Bitmap fullBmp, System.Windows.Forms.Screen screen, Action<Rectangle> onSelected)
    {
        var win = new SelectionOverlayWindow(fullBmp, screen);
        win.Selected += onSelected;
        win.ShowDialog();
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    void OnDeleteRequested(TriggerActionModule module)
    {
        var cfg = TriggerConfig.Load();
        cfg.Actions.RemoveAll(a => a.Id == module.Data.Id);
        cfg.Save();

        try { if (File.Exists(module.Data.ImagePath)) File.Delete(module.Data.ImagePath); }
        catch { }

        ModuleDeleted?.Invoke(module);
        _log($"[Auto Trigger] Тригер «{module.Data.Name}» видалено.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static TextBlock MakeLabel(string text, WpfBrush brush, double width = double.NaN) => new()
    {
        Text       = text,
        Foreground = brush,
        FontSize   = 12,
        Width      = double.IsNaN(width) ? double.NaN : width,
        VerticalAlignment = VerticalAlignment.Center,
        Margin     = new Thickness(0, 0, 0, 4),
    };
}
