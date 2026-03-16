using System;
using System.Drawing;
using System.IO;
using System.Media;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WpfBrush  = System.Windows.Media.Brush;
using WpfColor  = System.Windows.Media.Color;
using WpfSCB    = System.Windows.Media.SolidColorBrush;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Trigger;

/// <summary>
/// One auto-trigger action — shows as a separate module in the sidebar.
/// Matches a saved screenshot zone against the live screen;
/// fires a key-press or plays a sound every N seconds while match holds.
/// </summary>
public sealed class TriggerActionModule : IModule
{
    volatile bool  _running;
    Action<string> _log = Console.WriteLine;

    public TriggerActionData Data { get; }

    public string Id        => Data.Id;
    public string Name      => Data.Name;
    public bool   IsRunning => _running;

    public event Action? StateChanged;
    /// <summary>Raised (on bg thread) when user deletes this trigger from its own settings panel.</summary>
    public event Action<TriggerActionModule>? DeleteRequested;

    public TriggerActionModule(TriggerActionData data) => Data = data;

    public void Initialize(Action<string> log) => _log = log;

    public void Start()
    {
        if (_running) return;
        if (!File.Exists(Data.ImagePath))
        {
            _log($"[{Data.Name}] ❌ Зображення не знайдено: {Data.ImagePath}");
            return;
        }
        _running = true;
        new Thread(Loop) { IsBackground = true }.Start();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _log($"[{Data.Name}] Вимкнено.");
        StateChanged?.Invoke();
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        if (string.IsNullOrWhiteSpace(Data.Hotkey)) return;
        var (mods, vk) = TriggerLogic.ParseHotkey(Data.Hotkey);
        if (vk == 0) return;
        hotkeys.Register(mods, vk, () =>
        {
            if (IsRunning) { _log($"[{Data.Name}] {Data.Hotkey} → ВИМКНЕНО"); Stop(); }
            else           { _log($"[{Data.Name}] {Data.Hotkey} → УВІМКНЕНО"); Start(); }
        });
    }

    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    // ── Main loop ─────────────────────────────────────────────────────────────

    void Loop()
    {
        _log($"✅ [{Data.Name}] Запущено. Очікую збіг...");

        // Load all templates (main + extras)
        var templates = new System.Collections.Generic.List<Bitmap>();
        try
        {
            void LoadTemplate(string path)
            {
                if (!File.Exists(path)) return;
                using var raw = new Bitmap(path);
                templates.Add(TriggerLogic.AdjustBitmap(new Bitmap(raw), Data.Brightness, Data.Contrast));
            }
            LoadTemplate(Data.ImagePath);
            foreach (var p in Data.ExtraImagePaths) LoadTemplate(p);
        }
        catch { }

        if (templates.Count == 0)
        {
            _log($"[{Data.Name}] ❌ Не вдалося завантажити жодного зображення.");
            _running = false;
            StateChanged?.Invoke();
            return;
        }

        var screen      = TriggerLogic.GetMonitor(Data.MonitorIndex);
        var soundPlayer = TryBuildPlayer(Data.SoundFile);

        bool wasMatching  = false;
        long lastActionMs = 0;

        while (_running)
        {
            try
            {
                screen = TriggerLogic.GetMonitor(Data.MonitorIndex);
                var rect = TriggerLogic.GetCaptureRect(Data, screen);

                using var capture = TriggerLogic.CaptureRect(rect);

                // Match against all templates — any hit counts
                double bestScore = 0;
                foreach (var t in templates)
                {
                    double s = TriggerLogic.MatchTemplate(t, capture);
                    if (s > bestScore) bestScore = s;
                    if (bestScore >= Data.MatchThreshold) break;
                }
                double score    = bestScore;
                bool   matching = score >= Data.MatchThreshold;

                if (matching && !wasMatching)
                    _log($"🟢 [{Data.Name}] Збіг! ({score:P0})");
                else if (!matching && wasMatching)
                    _log($"[{Data.Name}] Збіг зник ({score:P0})");

                wasMatching = matching;

                if (matching)
                {
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    long intervalMs = Data.Action == "sound"
                        ? (long)(Data.SoundIntervalSec * 1000)
                        : 0; // key: press once per match cycle (debounced 500ms)

                    if (nowMs - lastActionMs >= Math.Max(500, intervalMs))
                    {
                        lastActionMs = nowMs;
                        DoAction(soundPlayer);
                    }
                }
            }
            catch { /* screen may not be ready */ }

            Thread.Sleep(Math.Max(50, Data.CheckIntervalMs));
        }

        foreach (var t in templates) t.Dispose();
        soundPlayer?.Dispose();
    }

    void DoAction(SoundPlayer? player)
    {
        if (Data.Action == "sound")
        {
            try { player?.Play(); }
            catch { _log($"[{Data.Name}] ❌ Не вдалося відтворити звук."); }
        }
        else
        {
            TriggerLogic.PressCombo(Data.ActionKey, s => _log(s), Data.Name);
        }
    }

    static SoundPlayer? TryBuildPlayer(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try { return new SoundPlayer(path); }
            catch { }
        }
        return null;
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    FrameworkElement BuildSettingsPanel()
    {
        var textDim = (WpfBrush)Application.Current.Resources["TextDimBrush"];
        var textSec = (WpfBrush)Application.Current.Resources["TextSecondaryBrush"];
        var accent  = (WpfBrush)Application.Current.Resources["AccentBrush"];
        var bgCard  = (WpfBrush)Application.Current.Resources["BgCardBrush"];
        var borderB = (WpfBrush)Application.Current.Resources["BorderBrush"];

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        // ── Instructions ──────────────────────────────────────────────────────
        var info = new TextBlock
        {
            Text = $"Зона: ({Data.CaptureX},{Data.CaptureY}) {Data.CaptureW}×{Data.CaptureH}  |  " +
                   $"Монітор: {Data.MonitorIndex} ({Data.MonitorW}×{Data.MonitorH})\n" +
                   $"Дія: {(Data.Action == "sound" ? $"Звук кожні {Data.SoundIntervalSec}с" : $"Клавіша [{Data.ActionKey}]")}  |  " +
                   $"Поріг: {Data.MatchThreshold:P0}  |  Хоткей: {Data.Hotkey}",
            Foreground   = textDim,
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(info);

        // ── Preview image (live — оновлюється при зміні яскравості/контрасту) ──
        System.Windows.Controls.Image? previewImg = null;
        if (File.Exists(Data.ImagePath))
        {
            var imgBorder = new Border
            {
                Background      = bgCard,
                BorderBrush     = borderB,
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(4),
                Padding         = new Thickness(4),
                Margin          = new Thickness(0, 0, 0, 12),
                MaxHeight       = 140,
            };

            previewImg = new System.Windows.Controls.Image
            {
                Source  = RenderPreview(Data.ImagePath, Data.Brightness, Data.Contrast),
                Stretch = System.Windows.Media.Stretch.Uniform,
            };
            imgBorder.Child = previewImg;
            panel.Children.Add(imgBorder);
        }

        void RefreshPreview()
        {
            if (previewImg == null) return;
            previewImg.Source = RenderPreview(Data.ImagePath, Data.Brightness, Data.Contrast);
        }

        // ── Extra templates ────────────────────────────────────────────────────
        var extrasPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(extrasPanel);

        void RebuildExtrasUi()
        {
            extrasPanel.Children.Clear();

            if (Data.ExtraImagePaths.Count > 0)
            {
                extrasPanel.Children.Add(new TextBlock
                {
                    Text = $"Додаткові фото ({Data.ExtraImagePaths.Count}):",
                    Foreground = textDim, FontSize = 11,
                    Margin = new Thickness(0, 0, 0, 4),
                });

                var thumbsRow = new System.Windows.Controls.WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
                for (int ei = 0; ei < Data.ExtraImagePaths.Count; ei++)
                {
                    int idx  = ei;
                    string p = Data.ExtraImagePaths[ei];
                    if (!File.Exists(p)) continue;

                    var thumb = new Border
                    {
                        Background = bgCard, BorderBrush = borderB,
                        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3),
                        Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(2),
                        Width = 60, Height = 48,
                    };
                    var imgEl = new System.Windows.Controls.Image
                    {
                        Source  = RenderPreview(p, Data.Brightness, Data.Contrast),
                        Stretch = System.Windows.Media.Stretch.Uniform,
                    };
                    thumb.Child = imgEl;
                    thumb.ToolTip = System.IO.Path.GetFileName(p);

                    // Right-click to remove
                    thumb.MouseRightButtonDown += (_, _) =>
                    {
                        try { if (File.Exists(p)) File.Delete(p); } catch { }
                        Data.ExtraImagePaths.RemoveAt(idx);
                        SaveData();
                        RebuildExtrasUi();
                    };
                    thumbsRow.Children.Add(thumb);
                }
                extrasPanel.Children.Add(thumbsRow);

                extrasPanel.Children.Add(new TextBlock
                {
                    Text = "ПКМ по фото — видалити",
                    Foreground = textDim, FontSize = 10,
                    Margin = new Thickness(0, 0, 0, 4),
                });
            }

            // Add extra template button
            var addExtraBtn = new Button
            {
                Content = "➕ Додати фото (через 5 сек)",
                Background = bgCard, BorderBrush = borderB,
                BorderThickness = new Thickness(1),
                Foreground = textSec,
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontSize = 11,
            };
            var addExtraStatus = new TextBlock { Text = "", Foreground = textDim, FontSize = 11, Margin = new Thickness(0, 2, 0, 0) };

            addExtraBtn.Click += (_, _) =>
            {
                addExtraBtn.IsEnabled = false;
                addExtraStatus.Text   = "⏳ Через 5 сек...";
                new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(5000);
                    var sc = TriggerLogic.GetMonitor(Data.MonitorIndex);
                    try
                    {
                        var rect    = TriggerLogic.GetCaptureRect(Data, sc);
                        using var bmp = TriggerLogic.CaptureRect(rect);
                        string imgDir = System.IO.Path.Combine(TriggerConfig.BaseDir, "images");
                        System.IO.Directory.CreateDirectory(imgDir);
                        string path = System.IO.Path.Combine(imgDir, Data.Id + "_extra" + Data.ExtraImagePaths.Count + ".png");
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                        Data.ExtraImagePaths.Add(path);
                        SaveData();
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            addExtraBtn.IsEnabled = true;
                            addExtraStatus.Text   = "✅ Додано!";
                            RebuildExtrasUi();
                        });
                    }
                    catch
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            addExtraBtn.IsEnabled = true;
                            addExtraStatus.Text   = "❌ Помилка";
                        });
                    }
                }) { IsBackground = true }.Start();
            };

            extrasPanel.Children.Add(addExtraBtn);
            extrasPanel.Children.Add(addExtraStatus);
        }

        RebuildExtrasUi();

        // ── Action key / hotkey capture fields ─────────────────────────────────
        if (Data.Action == "key")
            panel.Children.Add(MakeKeyRow("Клавіша дії:", Data.ActionKey, textDim, textSec, borderB, bgCard, v =>
            {
                Data.ActionKey = v;
                SaveData();
            }));

        panel.Children.Add(MakeKeyRow("Хоткей вмк/вимк:", Data.Hotkey, textDim, textSec, borderB, bgCard, v =>
        {
            Data.Hotkey = v;
            SaveData();
        }));

        // ── Check interval ──────────────────────────────────────────────────────
        panel.Children.Add(MakeTextRow("Інтервал перевірки мс:", Data.CheckIntervalMs.ToString(), "500", textDim, textSec, v =>
        {
            if (int.TryParse(v.Trim(), out int ms) && ms >= 50)
            {
                Data.CheckIntervalMs = ms;
                SaveData();
            }
        }));

        // ── Brightness / Contrast ──────────────────────────────────────────────
        panel.Children.Add(MakeSliderRow("Яскравість", Data.Brightness, -100, 100, textDim, textSec, v =>
        {
            Data.Brightness = (int)v;
            SaveData();
            RefreshPreview();
        }));
        panel.Children.Add(MakeSliderRow("Контраст", Data.Contrast, -100, 100, textDim, textSec, v =>
        {
            Data.Contrast = (int)v;
            SaveData();
            RefreshPreview();
        }));

        // ── Match threshold ────────────────────────────────────────────────────
        panel.Children.Add(MakeSliderRow("Поріг збігу %", (int)(Data.MatchThreshold * 100), 50, 100, textDim, textSec, v =>
        {
            Data.MatchThreshold = v / 100.0;
            SaveData();
        }));

        // ── Delete button ──────────────────────────────────────────────────────
        var delBtn = new Button
        {
            Content         = "Видалити тригер",
            Foreground      = new WpfSCB(WpfColor.FromRgb(0xFF, 0x55, 0x55)),
            Background      = bgCard,
            BorderBrush     = new WpfSCB(WpfColor.FromRgb(0xFF, 0x55, 0x55)),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10, 5, 10, 5),
            Cursor          = System.Windows.Input.Cursors.Hand,
            Margin          = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        delBtn.Click += (_, _) =>
        {
            Stop();
            DeleteRequested?.Invoke(this);
        };
        panel.Children.Add(delBtn);

        return panel;
    }

    StackPanel MakeSliderRow(string label, int value, int min, int max,
        WpfBrush textDim, WpfBrush textSec, Action<double> onChange)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = textDim,
            Width             = 120,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 12,
        };

        var slider = new Slider
        {
            Minimum           = min,
            Maximum           = max,
            Value             = value,
            Width             = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var valLbl = new TextBlock
        {
            Text              = value.ToString(),
            Foreground        = textSec,
            Width             = 36,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize          = 12,
            Margin            = new Thickness(6, 0, 0, 0),
            TextAlignment     = TextAlignment.Right,
        };

        slider.ValueChanged += (_, e) =>
        {
            valLbl.Text = ((int)slider.Value).ToString();
            onChange(slider.Value);
        };

        row.Children.Add(lbl);
        row.Children.Add(slider);
        row.Children.Add(valLbl);
        return row;
    }

    /// <summary>Renders preview with brightness/contrast applied — no file URI caching.</summary>
    static System.Windows.Media.Imaging.BitmapSource? RenderPreview(string path, int brightness, int contrast)
    {
        try
        {
            using var raw     = new Bitmap(path);
            using var adjusted = TriggerLogic.AdjustBitmap(new Bitmap(raw), brightness, contrast);
            using var ms = new MemoryStream();
            adjusted.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
            ms.Seek(0, SeekOrigin.Begin);
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    StackPanel MakeKeyRow(string label, string currentKey, WpfBrush textDim, WpfBrush textSec,
        WpfBrush borderB, WpfBrush bgCard, Action<string> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = textDim, Width = 160,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        });
        row.Children.Add(KeyCaptureUi.Make(currentKey, textSec, borderB, bgCard, onChanged));
        return row;
    }

    StackPanel MakeTextRow(string label, string value, string placeholder,
        WpfBrush textDim, WpfBrush textSec, Action<string> onChanged)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock
        {
            Text = label, Foreground = textDim, Width = 160,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        });
        var box = new TextBox { Text = value, Width = 130, Padding = new Thickness(6, 3, 6, 3), ToolTip = placeholder };
        box.LostFocus += (_, _) => onChanged(box.Text);
        box.KeyDown   += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) onChanged(box.Text); };
        row.Children.Add(box);
        return row;
    }

    void SaveData()
    {
        var cfg = TriggerConfig.Load();
        var idx = cfg.Actions.FindIndex(a => a.Id == Data.Id);
        if (idx >= 0) cfg.Actions[idx] = Data;
        cfg.Save();
    }
}
