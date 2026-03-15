using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WpfBrush = System.Windows.Media.Brush;
using ScriptedWpf.Core;
using ScriptedWpf.Cogs.Cda; // ScreenCapture

namespace ScriptedWpf.Cogs.Hlorka;

public sealed class HlorkaModule : IModule
{
    volatile bool  _running;
    Action<string> _log      = Console.WriteLine;
    HlorkaConfig   _cfg      = HlorkaConfig.Load();
    Bitmap?        _template = null;

    const ushort VK_SPACE = 0x20;

    // ── IModule ───────────────────────────────────────────────────────────────

    public string Id        => "hlorka";
    public bool   IsRunning => _running;

    public event Action? StateChanged;

    public void Initialize(Action<string> log)
    {
        _log      = log;
        _template = HlorkaScanner.LoadTemplate();
        if (_template != null) _log("Хлорка: еталон завантажено ✅");
        else                   _log("Хлорка: еталон не знайдено — збережи приклад у налаштуваннях");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        new Thread(Loop) { IsBackground = true }.Start();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _log("Хлорка вимкнена.");
        StateChanged?.Invoke();
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F10, () =>
        {
            if (IsRunning) { _log("F10 → ВИМКНЕНО"); Stop(); }
            else           { _log("F10 → УВІМКНЕНО"); Start(); }
        });
    }

    public FrameworkElement? GetSettingsView() => BuildSettingsPanel();

    // ── Основний цикл ─────────────────────────────────────────────────────────

    void Loop()
    {
        _log("✅ Хлорка в режимі очікування. F10 — зупинити.");
        int logTick = 0;

        while (_running)
        {
            var screen = GetScreen();
            var region = GetScanRegion(screen);
            using var bmp = ScreenCapture.Capture(region);

            // ── Фаза очікування ───────────────────────────────────────────────
            if (!CheckVisible(bmp))
            {
                // Логуємо MAD кожні ~3 секунди щоб можна було підібрати threshold
                if (_template != null && ++logTick % 10 == 0)
                {
                    double mad = HlorkaScanner.GetMatchScore(bmp, _template);
                    _log($"[очікування] MAD={mad:F1}  threshold={_cfg.MatchThreshold}");
                }
                Thread.Sleep(300);
                continue;
            }
            logTick = 0;

            // ── Активна фаза ──────────────────────────────────────────────────
            _log("🟢 Міні-гру знайдено! Починаю...");
            int missCount = 0;
            const int MaxMiss = 8;

            while (_running)
            {
                var s2 = GetScreen();
                var r2 = GetScanRegion(s2);
                using var frame = ScreenCapture.Capture(r2);

                if (!CheckVisible(frame))
                {
                    if (++missCount >= MaxMiss) break;
                    Thread.Sleep(50);
                    continue;
                }
                missCount = 0;

                int? ballY = HlorkaScanner.FindBallY(frame);
                int? lineY = HlorkaScanner.FindLineY(frame);

                if (!ballY.HasValue) { Thread.Sleep(16); continue; }

                bool needTap = !lineY.HasValue || (lineY.Value - ballY.Value) > _cfg.Deadband;
                if (needTap) TapSpace();
                else         Thread.Sleep(16);
            }

            if (_running)
                _log("⏸ Міні-гра завершена. Очікую наступну...");
        }

        _log("Хлорка вимкнена.");
    }

    bool CheckVisible(Bitmap bmp)
    {
        if (_template != null)
            return HlorkaScanner.MatchesTemplate(bmp, _template, _cfg.MatchThreshold);
        return HlorkaScanner.FindBallY(bmp).HasValue;
    }

    void TapSpace()
    {
        SendSpaceDown();
        Thread.Sleep(40);
        SendSpaceUp();
        Thread.Sleep(50);
    }

    // ── Допоміжні методи ──────────────────────────────────────────────────────

    System.Windows.Forms.Screen GetScreen()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (_cfg.MonitorIndex >= 0 && _cfg.MonitorIndex < screens.Length)
            return screens[_cfg.MonitorIndex];
        return System.Windows.Forms.Screen.PrimaryScreen ?? screens[0];
    }

    Rectangle GetScanRegion(System.Windows.Forms.Screen screen)
    {
        var b = screen.Bounds;
        int x = (int)(b.X + b.Width  * _cfg.ScanX);
        int y = (int)(b.Y + b.Height * _cfg.ScanY);
        int w = Math.Max((int)(b.Width  * _cfg.ScanW), 1);
        int h = Math.Max((int)(b.Height * _cfg.ScanH), 1);
        return new Rectangle(x, y, w, h);
    }

    static void SendSpaceDown()
    {
        int sz = Marshal.SizeOf<WinApi.INPUT>();
        var down = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = VK_SPACE } }
        };
        WinApi.SendInput(1, [down], sz);
    }

    static void SendSpaceUp()
    {
        int sz = Marshal.SizeOf<WinApi.INPUT>();
        var up = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = VK_SPACE, dwFlags = WinApi.KEYEVENTF_KEYUP } }
        };
        WinApi.SendInput(1, [up], sz);
    }

    // ── Панель налаштувань ────────────────────────────────────────────────────

    FrameworkElement BuildSettingsPanel()
    {
        var textSec = (WpfBrush)Application.Current.Resources["TextSecondaryBrush"];
        var stack   = new StackPanel { Orientation = Orientation.Vertical };

        // ── Монітор ──────────────────────────────────────────────────────────
        var monRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        monRow.Children.Add(new TextBlock
        {
            Text = "Монітор:", Foreground = textSec,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });

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

        // ── Область сканування ───────────────────────────────────────────────
        stack.Children.Add(new TextBlock
        {
            Text = "Область сканування (частки екрану):",
            Foreground = textSec, Margin = new Thickness(0, 0, 0, 4)
        });

        // TextBox-и для ручного редагування + посилання для оновлення з overlay
        var tbX = MakeValueBox(_cfg.ScanX, v => { _cfg.ScanX = v; _cfg.Save(); });
        var tbY = MakeValueBox(_cfg.ScanY, v => { _cfg.ScanY = v; _cfg.Save(); });
        var tbW = MakeValueBox(_cfg.ScanW, v => { _cfg.ScanW = v; _cfg.Save(); });
        var tbH = MakeValueBox(_cfg.ScanH, v => { _cfg.ScanH = v; _cfg.Save(); });

        stack.Children.Add(MakeLabeledRow(textSec, "X", tbX));
        stack.Children.Add(MakeLabeledRow(textSec, "Y", tbY));
        stack.Children.Add(MakeLabeledRow(textSec, "W", tbW));
        stack.Children.Add(MakeLabeledRow(textSec, "H", tbH));

        // ── Еталон міні-гри ───────────────────────────────────────────────────
        bool hasTemplate = HlorkaScanner.HasTemplate();
        var templateStatus = new TextBlock
        {
            Text       = hasTemplate ? "Еталон: ✅ є" : "Еталон: ❌ не збережено",
            Foreground = textSec, Margin = new Thickness(0, 8, 0, 4)
        };
        stack.Children.Add(templateStatus);

        var saveTemplateBtn = new Button
        {
            Content = "Зберегти приклад (3 с)",
            Height = 34,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 0, 16, 0),
            ToolTip = "Відкрий міні-гру в грі, натисни — через 3 с збережеться скріншот зони як еталон"
        };
        var templateLbl = new TextBlock
        {
            Foreground = textSec, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(saveTemplateBtn);
        stack.Children.Add(templateLbl);

        saveTemplateBtn.Click += (_, _) =>
        {
            saveTemplateBtn.IsEnabled = false;
            templateLbl.Visibility = Visibility.Visible;

            new Thread(() =>
            {
                for (int sec = 3; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(
                        () => templateLbl.Text = $"Повернись у гру (міні-гра має бути активна)... {s}");
                    Thread.Sleep(1000);
                }

                var screen = GetScreen();
                var region = GetScanRegion(screen);
                using var bmp = ScreenCapture.Capture(region);
                HlorkaScanner.SaveTemplate(bmp);

                // Перезавантажуємо еталон
                _template?.Dispose();
                _template = HlorkaScanner.LoadTemplate();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    templateLbl.Text       = "✅ Еталон збережено!";
                    templateStatus.Text    = "Еталон: ✅ є";
                    saveTemplateBtn.IsEnabled = true;
                });
            }) { IsBackground = true }.Start();
        };

        // ── Кнопка калібрування (overlay) ─────────────────────────────────────
        var calibBtn = new Button
        {
            Content = "Виділити зону (3 с)",
            Height = 34, Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 0, 16, 0),
            ToolTip = "Відкриє напівпрозорий екран — виділіть мишею зону з планкою міні-гри"
        };
        var calibLbl = new TextBlock
        {
            Foreground = textSec, FontSize = 12,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(calibBtn);
        stack.Children.Add(calibLbl);

        calibBtn.Click += (_, _) =>
        {
            calibBtn.IsEnabled = false;
            calibLbl.Visibility = Visibility.Visible;

            new Thread(() =>
            {
                for (int sec = 3; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(
                        () => calibLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    calibLbl.Visibility = Visibility.Collapsed;
                    calibBtn.IsEnabled  = true;

                    var screen  = GetScreen();
                    var overlay = new HlorkaOverlay(screen);
                    bool ok = overlay.ShowDialog() == true;

                    if (ok)
                    {
                        _cfg.ScanX = overlay.RegionX;
                        _cfg.ScanY = overlay.RegionY;
                        _cfg.ScanW = overlay.RegionW;
                        _cfg.ScanH = overlay.RegionH;
                        _cfg.Save();

                        // Оновлюємо TextBox-и
                        tbX.Text = _cfg.ScanX.ToString("F3", CultureInfo.InvariantCulture);
                        tbY.Text = _cfg.ScanY.ToString("F3", CultureInfo.InvariantCulture);
                        tbW.Text = _cfg.ScanW.ToString("F3", CultureInfo.InvariantCulture);
                        tbH.Text = _cfg.ScanH.ToString("F3", CultureInfo.InvariantCulture);

                        calibLbl.Text       = $"✅ Збережено: X={_cfg.ScanX:F3} Y={_cfg.ScanY:F3} W={_cfg.ScanW:F3} H={_cfg.ScanH:F3}";
                        calibLbl.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        calibLbl.Text       = "Скасовано.";
                        calibLbl.Visibility = Visibility.Visible;
                    }
                });
            }) { IsBackground = true }.Start();
        };

        // ── Мертва зона ──────────────────────────────────────────────────────
        stack.Children.Add(new TextBlock
        {
            Text = "Мертва зона (px):",
            Foreground = textSec, Margin = new Thickness(0, 10, 0, 4)
        });

        var deadbandBox = new TextBox
        {
            Width = 60, Height = 26,
            Text = _cfg.Deadband.ToString(),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        deadbandBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(deadbandBox.Text, out int v) && v >= 0)
            {
                _cfg.Deadband = v;
                _cfg.Save();
            }
        };
        stack.Children.Add(deadbandBox);

        // ── Поріг схожості з еталоном ────────────────────────────────────────
        stack.Children.Add(new TextBlock
        {
            Text = "Поріг MAD (менше = суворіше):",
            Foreground = textSec, Margin = new Thickness(0, 8, 0, 4)
        });
        var threshBox = new TextBox
        {
            Width = 60, Height = 26,
            Text  = _cfg.MatchThreshold.ToString(),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        threshBox.LostFocus += (_, _) =>
        {
            if (int.TryParse(threshBox.Text, out int v) && v > 0)
            {
                _cfg.MatchThreshold = v;
                _cfg.Save();
            }
        };
        stack.Children.Add(threshBox);
        stack.Children.Add(new TextBlock
        {
            Text = "← Дивись MAD в терміналі і виставь між\n   «без міні-гри» та «з міні-грою»",
            Foreground = textSec, FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });

        // ── Кнопка тесту ─────────────────────────────────────────────────────
        var testBtn = new Button
        {
            Content = "Тест (3 с) — перевірити детектор",
            Height = 34, Margin = new Thickness(0, 10, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(16, 0, 16, 0)
        };
        var statusLbl = new TextBlock
        {
            Foreground = textSec, FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(testBtn);
        stack.Children.Add(statusLbl);

        testBtn.Click += (_, _) =>
        {
            testBtn.IsEnabled = false;
            statusLbl.Visibility = Visibility.Visible;

            new Thread(() =>
            {
                for (int sec = 3; sec >= 1; sec--)
                {
                    int s = sec;
                    Application.Current.Dispatcher.Invoke(
                        () => statusLbl.Text = $"Повернись у гру... {s}");
                    Thread.Sleep(1000);
                }

                var screen = GetScreen();
                var region = GetScanRegion(screen);
                using var bmp = ScreenCapture.Capture(region);

                string debugInfo = HlorkaScanner.SaveDebug(bmp);

                int? ballY = HlorkaScanner.FindBallY(bmp);
                int? lineY = HlorkaScanner.FindLineY(bmp);

                string detection = ballY.HasValue && lineY.HasValue
                    ? $"✅ Кулька Y={ballY}  Лінія Y={lineY}  Різниця={lineY - ballY} px"
                    : ballY.HasValue
                        ? $"⚠️ Кулька Y={ballY}, лінія НЕ знайдена"
                        : lineY.HasValue
                            ? $"⚠️ Лінія Y={lineY}, кулька НЕ знайдена"
                            : "❌ Нічого не знайдено — перевір область сканування";

                string msg = $"{detection}\n{debugInfo}\n→ Збережено hlorka_raw.png і hlorka_debug.png на робочий стіл";

                Application.Current.Dispatcher.Invoke(() =>
                {
                    statusLbl.Text    = msg;
                    testBtn.IsEnabled = true;
                });
            }) { IsBackground = true }.Start();
        };

        return stack;
    }

    // ── Допоміжні методи для побудови UI ─────────────────────────────────────

    static TextBox MakeValueBox(double initial, Action<double> onChanged)
    {
        var tb = new TextBox
        {
            Width = 70, Height = 26,
            Text  = initial.ToString("F3", CultureInfo.InvariantCulture)
        };
        tb.LostFocus += (_, _) =>
        {
            if (double.TryParse(tb.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v) && v >= 0 && v <= 1)
            {
                onChanged(v);
                tb.Text = v.ToString("F3", CultureInfo.InvariantCulture);
            }
        };
        return tb;
    }

    static StackPanel MakeLabeledRow(WpfBrush textBrush, string label, TextBox tb)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 4)
        };
        row.Children.Add(new TextBlock
        {
            Text = $"{label}:", Foreground = textBrush,
            Width = 20, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        row.Children.Add(tb);
        return row;
    }
}
