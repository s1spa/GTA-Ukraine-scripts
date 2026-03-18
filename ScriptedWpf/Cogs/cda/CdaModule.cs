using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using WpfBrush = System.Windows.Media.Brush;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Cda;

public sealed class CdaModule : IModule
{
    volatile bool  _running;
    Action<string> _log = Console.WriteLine;
    CdaConfig      _cfg = CdaConfig.Load();

    public event Action<Bitmap>? OnFrame;

    // ── IModule ───────────────────────────────────────────────────────────────

    public string Id        => "cda";
    public bool   IsRunning => _running;

    public event Action? StateChanged;

    public void Initialize(Action<string> log)
    {
        _log = log;
        // Download tessdata in background on first run
        new Thread(() =>
        {
            try { TessOcr.EnsureTessdata(_log); }
            catch { }
        })
        { IsBackground = true }.Start();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        if (_cfg.AutoMode)
            new Thread(RunAutoPilot) { IsBackground = true }.Start();
        else
            new Thread(ScanLoop) { IsBackground = true }.Start();

        if (_cfg.ShowNotifications)
        {
            string mode = _cfg.AutoMode ? "Автопілот" : "Ввід коду";
            ToastNotifier.Show("CDA", $"{mode} запущено", ToastIcon.Success);
        }
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _log("CDA: зупинено.");
        if (_cfg.ShowNotifications)
            ToastNotifier.Show("CDA", "Зупинено", ToastIcon.Info);
        StateChanged?.Invoke();
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)System.Windows.Forms.Keys.F7, () =>
        {
            if (_running) Stop(); else Start();
        });
    }

    // ── Manual code scan loop ─────────────────────────────────────────────────

    void ScanLoop()
    {
        string? lastCode      = null;
        long    cooldownUntil = 0;
        int     noCodeStreak  = 0;

        _log($"CDA: запущено [{(_cfg.Turbo ? "turbo" : "fast")}]");

        while (_running)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < cooldownUntil) { Thread.Sleep(50); continue; }

            try
            {
                var region = CdaLogic.GetRegion(_cfg);
                using var img = ScreenCapture.Capture(region);

                OnFrame?.Invoke((Bitmap)img.Clone());

                var code = CdaLogic.FindCodeAsync(img).GetAwaiter().GetResult();

                if (code != null)
                {
                    noCodeStreak = 0;
                    if (code != lastCode)
                    {
                        _log($"CDA: >> Код: {code} — вводжу...");
                        Thread.Sleep(100);
                        CdaLogic.TypeCode(code, _cfg.Turbo);
                        _log("CDA: >> Готово.");
                        lastCode      = code;
                        cooldownUntil = now + 2000;
                    }
                }
                else
                {
                    if (++noCodeStreak >= 5) { lastCode = null; noCodeStreak = 0; }
                }
            }
            catch (Exception ex)
            {
                _log($"CDA: ❌ {ex.Message}");
                Thread.Sleep(500);
            }
        }
    }

    // ── Auto pilot loop ───────────────────────────────────────────────────────

    void RunAutoPilot()
    {
        _log($"CDA: АВТОПІЛОТ | Мін.ціна:{_cfg.MinPrice}$/km | Макс.тоннаж:{_cfg.MaxTon}т");

        bool waitingForCode   = false;
        long clickTime        = 0;
        long nextDialogSearch = 0;
        Rectangle? codeZone   = null;

        while (_running)
        {
            try
            {
                if (!waitingForCode)
                {
                    // ── Scan order list ───────────────────────────────────────
                    Rectangle? scanZone = _cfg.HasScanZone
                        ? new Rectangle(_cfg.ScanX1, _cfg.ScanY1,
                                        _cfg.ScanX2 - _cfg.ScanX1,
                                        _cfg.ScanY2 - _cfg.ScanY1)
                        : null;
                    var cards = OrderScanner.FindCards(_cfg.MonitorIndex, scanZone, _cfg.MaxTon);

                    if (cards.Count == 0)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }

                    // Filter by tonnage, type, price
                    var valid = new List<OrderScanner.OrderCard>();
                    foreach (var c in cards)
                    {
                        bool tonOk   = c.Tonnage > 0 && c.Tonnage <= _cfg.MaxTon;
                        bool typeOk  = _cfg.Types.Contains(c.Type);
                        bool priceOk = c.PricePerKm >= _cfg.MinPrice;
                        bool ok      = tonOk && typeOk && priceOk;

                        _log($"CDA: {(ok ? "✅" : "❌")} {c.Type} {c.Tonnage}т " +
                             $"→ {c.PricePerKm}$/km " +
                             $"[тонаж:{(tonOk?"OK":"X")} тип:{(typeOk?"OK":"X")} ціна:{(priceOk?"OK":"X")}]");

                        if (ok) valid.Add(c);
                    }

                    if (valid.Count == 0)
                    {
                        _log("CDA: Підходящих замовлень немає. Чекаю...");
                        Thread.Sleep(2000);
                        continue;
                    }

                    // Pick best (highest $/km)
                    var best = valid.OrderByDescending(c => c.PricePerKm).First();
                    _log($"CDA: 🏆 {best.Type} {best.Tonnage}т → {best.PricePerKm}$/km — відкриваю...");

                    // Click on order card
                    MouseInput.Click(best.ClickPoint.X, best.ClickPoint.Y);
                    Thread.Sleep(500);

                    // Click "Взяти замовлення" button (center-right of screen)
                    var scr  = TessOcr.GetMonitorBounds(_cfg.MonitorIndex);
                    int btnX = scr.X + scr.Width  / 2 + (int)(scr.Width  * 0.075);
                    int btnY = scr.Y + scr.Height / 2 + (int)(scr.Height * 0.16);
                    _log("CDA: 🖱 'Взяти замовлення'...");
                    MouseInput.Click(btnX, btnY);

                    waitingForCode   = true;
                    clickTime        = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    nextDialogSearch = clickTime + 1000; // wait 1s before first Tesseract search
                    // Use saved zone immediately if available — skip slow FindDialogRegion
                    codeZone = GetManualCodeZone();
                    if (codeZone.HasValue)
                        _log($"CDA: 📍 Зона коду збережена, Tesseract не потрібен.");
                    else
                        _log("CDA: Очікую код підтвердження...");
                }
                else
                {
                    // ── Waiting for confirmation code dialog ──────────────────
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    if (now - clickTime > 15000)
                    {
                        _log("CDA: ⏱ Діалог коду не з'явився за 15с. Повертаюся до пошуку.");
                        waitingForCode = false;
                        codeZone       = null;
                        Thread.Sleep(500);
                        continue;
                    }

                    // Try to auto-detect dialog zone (throttled to every 500ms)
                    if (codeZone == null && now >= nextDialogSearch)
                    {
                        _log("CDA: 🔍 Шукаю діалог коду...");
                        var detected = TessOcr.FindDialogRegion(_cfg.MonitorIndex);
                        if (detected != null)
                        {
                            var z = detected.Value;
                            // Narrow to the input field area (trim sides)
                            codeZone = new Rectangle(
                                z.X + 180, z.Y + 5,
                                Math.Max(1, z.Width - 360),
                                Math.Max(1, z.Height - 30));
                            _log($"CDA: 📍 Зону коду знайдено: {codeZone.Value}");
                            // Save for manual fallback
                            _cfg.X1 = codeZone.Value.Left;
                            _cfg.Y1 = codeZone.Value.Top;
                            _cfg.X2 = codeZone.Value.Right;
                            _cfg.Y2 = codeZone.Value.Bottom;
                            _cfg.Save();
                        }
                        else
                        {
                            nextDialogSearch = now + 500; // retry in 500ms
                        }
                    }

                    // Use fallback manual zone if auto-detect hasn't found it yet
                    var zone = codeZone ?? GetManualCodeZone();

                    if (zone.HasValue)
                    {
                        using var img = ScreenCapture.Capture(zone.Value);
                        OnFrame?.Invoke((Bitmap)img.Clone());
                        var code = CdaLogic.FindCodeAsync(img).GetAwaiter().GetResult();
                        if (code != null)
                        {
                            _log($"CDA: ✅ Код: {code} — вводжу...");
                            Thread.Sleep(100);
                            CdaLogic.TypeCode(code, turbo: true);
                            _log("CDA: 🚚 Замовлення взято! Автопілот вимкнено.");
                            Stop();
                            return;
                        }
                    }

                    Thread.Sleep(50);
                }
            }
            catch (Exception ex)
            {
                _log($"CDA: ❌ {ex.Message}");
                waitingForCode = false;
                codeZone       = null;
                Thread.Sleep(1000);
            }
        }
    }

    Rectangle? GetManualCodeZone()
    {
        if (_cfg.X2 <= _cfg.X1 || _cfg.Y2 <= _cfg.Y1) return null;
        return new Rectangle(_cfg.X1, _cfg.Y1, _cfg.X2 - _cfg.X1, _cfg.Y2 - _cfg.Y1);
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public FrameworkElement? GetSettingsView()
    {
        var textSec = (WpfBrush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim = (WpfBrush)Application.Current.Resources["TextDimBrush"];
        var accent  = (WpfBrush)Application.Current.Resources["AccentBrush"];

        var root = new StackPanel { Orientation = Orientation.Vertical };

        // ── Mode selection ────────────────────────────────────────────────────
        var modeLbl = new TextBlock
        {
            Text         = "РЕЖИМ",
            Foreground   = textDim,
            FontSize     = 10,
            FontWeight   = System.Windows.FontWeights.SemiBold,
            Margin       = new Thickness(0, 0, 0, 6),
        };

        var modePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };

        var rbManual = new RadioButton
        {
            Content    = "Ввід коду (ручний)",
            Foreground = textSec,
            IsChecked  = !_cfg.AutoMode,
            Margin     = new Thickness(0, 0, 20, 0),
            GroupName  = "CdaMode",
        };
        var rbAuto = new RadioButton
        {
            Content   = "Повністю автоматичний",
            Foreground = textSec,
            IsChecked  = _cfg.AutoMode,
            GroupName  = "CdaMode",
        };

        // Sections that toggle visibility
        var manualSection = new StackPanel { Visibility = _cfg.AutoMode ? Visibility.Collapsed : Visibility.Visible };
        var autoSection   = new StackPanel { Visibility = _cfg.AutoMode ? Visibility.Visible   : Visibility.Collapsed };

        rbManual.Checked += (_, _) =>
        {
            _cfg.AutoMode = false;
            _cfg.Save();
            manualSection.Visibility = Visibility.Visible;
            autoSection.Visibility   = Visibility.Collapsed;
        };
        rbAuto.Checked += (_, _) =>
        {
            _cfg.AutoMode = true;
            _cfg.Save();
            manualSection.Visibility = Visibility.Collapsed;
            autoSection.Visibility   = Visibility.Visible;
        };

        modePanel.Children.Add(rbManual);
        modePanel.Children.Add(rbAuto);

        root.Children.Add(modeLbl);
        root.Children.Add(modePanel);
        root.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 10), Opacity = 0.2 });

        // ── Manual section ────────────────────────────────────────────────────
        var codeZoneLbl = new TextBlock
        {
            Foreground   = textDim,
            FontSize     = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        };
        UpdateCodeZoneLabel(codeZoneLbl);

        var codeZoneBtn = AccentButton("Виділити зону коду", accent);
        codeZoneBtn.Click += (_, _) => OpenOverlay(r =>
        {
            if (r.Width < 4 || r.Height < 4) return;
            _cfg.X1 = r.Left; _cfg.Y1 = r.Top; _cfg.X2 = r.Right; _cfg.Y2 = r.Bottom;
            _cfg.Save();
            Application.Current.Dispatcher.Invoke(() => UpdateCodeZoneLabel(codeZoneLbl));
            _log("CDA: зону коду збережено");
            ToastNotifier.Show("CDA", "Зону коду збережено", ToastIcon.Success);
        });

        var turboCb = MakeCheckBox("Turbo-режим (без затримок між цифрами)", _cfg.Turbo, textSec);
        turboCb.Checked   += (_, _) => { _cfg.Turbo = true;  _cfg.Save(); };
        turboCb.Unchecked += (_, _) => { _cfg.Turbo = false; _cfg.Save(); };

        manualSection.Children.Add(codeZoneLbl);
        manualSection.Children.Add(codeZoneBtn);
        manualSection.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8), Opacity = 0.2 });
        manualSection.Children.Add(turboCb);

        // ── Auto section ──────────────────────────────────────────────────────

        // Monitor selector
        var monitorRow = MakeLabeledRow("Монітор:", textSec);
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
        monitorRow.Children.Add(monCb);

        // Min price
        var priceRow = MakeLabeledRow("Мін. ціна /km:", textSec);
        var priceTb  = MakeIntBox(_cfg.MinPrice, v => { _cfg.MinPrice = v; _cfg.Save(); });
        priceRow.Children.Add(priceTb);
        var priceHint = new TextBlock
        {
            Text       = "$",
            Foreground = textDim,
            Margin     = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        priceRow.Children.Add(priceHint);

        // Max tonnage
        var tonRow  = MakeLabeledRow("Макс. тоннаж:", textSec);
        var tonCb   = new ComboBox { Width = 100, Height = 28 };
        double[] tonOptions = { 0.5, 1.5, 3.0, 5.0 };
        string[] tonLabels  = { "0.5 т (лише 0.5)", "1.5 т (0.5–1.5)", "3 т (0.5–3)", "5 т (всі)" };
        for (int i = 0; i < tonOptions.Length; i++) tonCb.Items.Add(tonLabels[i]);
        int tonIdx = Array.FindIndex(tonOptions, t => Math.Abs(t - _cfg.MaxTon) < 0.01);
        tonCb.SelectedIndex = tonIdx >= 0 ? tonIdx : tonOptions.Length - 1;
        tonCb.SelectionChanged += (_, _) =>
        {
            if (tonCb.SelectedIndex >= 0) { _cfg.MaxTon = tonOptions[tonCb.SelectedIndex]; _cfg.Save(); }
        };
        tonRow.Children.Add(tonCb);

        // Cargo types
        var typesLbl = new TextBlock
        {
            Text       = "Типи вантажів:",
            Foreground = textSec,
            Margin     = new Thickness(0, 10, 0, 4),
        };
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        string[] allTypes = { "Фармацевтика", "Одяг", "Продукти", "Різне", "Інше", "Автозапчастини", "Нафта" };
        foreach (var t in allTypes)
        {
            string captured = t;
            var cb = new CheckBox
            {
                Content   = t,
                IsChecked = _cfg.Types.Contains(t),
                Foreground = textSec,
                Margin    = new Thickness(0, 2, 14, 2),
            };
            cb.Checked   += (_, _) => { if (!_cfg.Types.Contains(captured)) { _cfg.Types.Add(captured); _cfg.Save(); } };
            cb.Unchecked += (_, _) => { _cfg.Types.Remove(captured); _cfg.Save(); };
            wrap.Children.Add(cb);
        }

        // ── Scan zone ─────────────────────────────────────────────────────────
        var scanZoneLbl = new TextBlock
        {
            Foreground   = textDim,
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 10, 0, 4),
        };
        UpdateScanZoneLabel(scanZoneLbl);

        var scanZoneBtn = AccentButton("Виділити зону замовлень", accent);
        scanZoneBtn.Click += (_, _) => OpenOverlay(r =>
        {
            if (r.Width < 4 || r.Height < 4) return;
            _cfg.ScanX1 = r.Left; _cfg.ScanY1 = r.Top; _cfg.ScanX2 = r.Right; _cfg.ScanY2 = r.Bottom;
            _cfg.Save();
            Application.Current.Dispatcher.Invoke(() => UpdateScanZoneLabel(scanZoneLbl));
            _log("CDA: зону сканування замовлень збережено");
            ToastNotifier.Show("CDA", "Зону замовлень збережено", ToastIcon.Success);
        });

        var scanZoneClearBtn = new Button
        {
            Content             = "Скинути зону",
            Margin              = new Thickness(0, 0, 0, 10),
            Padding             = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = System.Windows.Input.Cursors.Hand,
        };
        scanZoneClearBtn.Click += (_, _) =>
        {
            _cfg.ScanX1 = _cfg.ScanY1 = _cfg.ScanX2 = _cfg.ScanY2 = 0;
            _cfg.Save();
            Application.Current.Dispatcher.Invoke(() => UpdateScanZoneLabel(scanZoneLbl));
            _log("CDA: зону сканування скинуто (весь екран)");
        };

        // Code zone hint for auto mode
        var autoZoneBtn = AccentButton("Виділити зону коду (резервна)", accent);
        autoZoneBtn.Click += (_, _) => OpenOverlay(r =>
        {
            if (r.Width < 4 || r.Height < 4) return;
            _cfg.X1 = r.Left; _cfg.Y1 = r.Top; _cfg.X2 = r.Right; _cfg.Y2 = r.Bottom;
            _cfg.Save();
            Application.Current.Dispatcher.Invoke(() => UpdateCodeZoneLabel(codeZoneLbl));
            _log("CDA: резервну зону коду збережено");
        });
        var autoZoneHint = new TextBlock
        {
            Text         = "Зона коду визначається автоматично. Вкажіть резервну зону якщо авто-пошук не спрацьовує.",
            Foreground   = textDim,
            FontSize     = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 6),
        };

        autoSection.Children.Add(monitorRow);
        autoSection.Children.Add(priceRow);
        autoSection.Children.Add(tonRow);
        autoSection.Children.Add(typesLbl);
        autoSection.Children.Add(wrap);
        autoSection.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 8), Opacity = 0.2 });
        autoSection.Children.Add(scanZoneLbl);
        autoSection.Children.Add(scanZoneBtn);
        autoSection.Children.Add(scanZoneClearBtn);
        autoSection.Children.Add(new Separator { Margin = new Thickness(0, 2, 0, 8), Opacity = 0.2 });
        autoSection.Children.Add(autoZoneHint);
        autoSection.Children.Add(autoZoneBtn);

        // ── Shared: notifications ─────────────────────────────────────────────
        var notifCb = MakeCheckBox("Показувати сповіщення", _cfg.ShowNotifications, textSec);
        notifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        notifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };

        root.Children.Add(manualSection);
        root.Children.Add(autoSection);
        root.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 8), Opacity = 0.2 });
        root.Children.Add(notifCb);

        return root;
    }

    // ── Overlay ───────────────────────────────────────────────────────────────

    void OpenOverlay(Action<Rectangle> onSelected)
    {
        new Thread(() =>
        {
            Thread.Sleep(3000);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            var bounds = screen.Bounds;

            var fullBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(fullBmp))
                g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var win = new Cogs.Trigger.SelectionOverlayWindow(fullBmp, screen);
                win.Selected += r => { fullBmp.Dispose(); onSelected(r); };
                win.ShowDialog();
            });
        })
        { IsBackground = true }.Start();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    static StackPanel MakeLabeledRow(string label, WpfBrush fg)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 6),
        };
        row.Children.Add(new TextBlock
        {
            Text              = label,
            Foreground        = fg,
            Width             = 130,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    static Button AccentButton(string text, WpfBrush accent) => new()
    {
        Content             = text,
        Margin              = new Thickness(0, 0, 0, 10),
        Padding             = new Thickness(10, 6, 10, 6),
        Foreground          = System.Windows.Media.Brushes.Black,
        Background          = accent,
        BorderBrush         = accent,
        BorderThickness     = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left,
        Cursor              = System.Windows.Input.Cursors.Hand,
    };

    static CheckBox MakeCheckBox(string text, bool isChecked, WpfBrush fg) => new()
    {
        Content   = text,
        IsChecked = isChecked,
        Foreground = fg,
        Margin    = new Thickness(0, 0, 0, 4),
    };

    static TextBox MakeIntBox(int initial, Action<int> onChange)
    {
        var tb = new TextBox { Text = initial.ToString(), Width = 90, Height = 26 };
        tb.LostFocus += (_, _) =>
        {
            if (int.TryParse(tb.Text, out int v)) onChange(v);
            else tb.Text = initial.ToString();
        };
        return tb;
    }

    void UpdateCodeZoneLabel(TextBlock lbl) =>
        lbl.Text = $"Зона коду: ({_cfg.X1}, {_cfg.Y1}) → ({_cfg.X2}, {_cfg.Y2})";

    void UpdateScanZoneLabel(TextBlock lbl) =>
        lbl.Text = _cfg.HasScanZone
            ? $"Зона замовлень: ({_cfg.ScanX1}, {_cfg.ScanY1}) → ({_cfg.ScanX2}, {_cfg.ScanY2})"
            : "Зона замовлень: весь екран (не задана)";
}
