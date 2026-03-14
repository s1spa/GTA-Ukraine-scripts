using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ScriptedProgram.Core;

namespace ScriptedProgram.Cogs.Cda;

public sealed class CdaModule : IModule
{
    enum BotState { Idle, AutoPilot }

    volatile BotState  _state       = BotState.Idle;
    volatile bool      _userEnabled = false;
    Action<string>     _log         = Console.WriteLine;
    CdaConfig          _cfg         = CdaConfig.Load();
    Rectangle?         _codeZone;

    // ── IModule ───────────────────────────────────────────────────────────────
    public string Id         => "cda";
    public bool   IsRunning  => _state != BotState.Idle;

    public void Initialize(Action<string> log) => _log = log;

    public void Start()
    {
        _userEnabled = true;
        if (_state != BotState.Idle) return;
        // restore cached code zone so FindDialogRegion is skipped if we've seen it before
        if (_cfg.X > 0 && _codeZone == null)
            _codeZone = new System.Drawing.Rectangle(_cfg.X, _cfg.Y, _cfg.Width, _cfg.Height);
        _state = BotState.AutoPilot;
        new Thread(RunAutoPilot) { IsBackground = true }.Start();
    }

    public void Stop()
    {
        _userEnabled = false;
        if (_state == BotState.Idle) return;
        _state = BotState.Idle;
        _log("АВТОПІЛОТ ВИМКНЕНО.");
    }

    public void RegisterHotkeys(HotkeyService hotkeys)
    {
        hotkeys.Register(0, (uint)Keys.F9, () =>
        {
            if (!_userEnabled) return;
            if (!IsRunning)
            {
                _log("F9 → поновлення пошуку...");
                Start();
            }
        });
    }

    // ── Settings Control ──────────────────────────────────────────────────────
    public Control? GetSettingsControl() => BuildSettingsPanel();

    Control BuildSettingsPanel()
    {
        var panel = new Panel
        {
            BackColor    = Color.Transparent,
            Padding      = new Padding(0, 8, 0, 0),
            AutoSize     = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        int y = 0;
        y = AddRow(panel, y, "Монітор",    BuildMonitorSelector());
        y = AddRow(panel, y, "Мін. ціна /km",  BuildIntBox(_cfg.MinPrice,  v => { _cfg.MinPrice = v; _cfg.Save(); }));
        y = AddRow(panel, y, "Макс. тонн",     BuildDoubleBox(_cfg.MaxTon, v => { _cfg.MaxTon   = v; _cfg.Save(); }));
        y = AddRow(panel, y, "Рівень від",     BuildIntBox(_cfg.MinLvl,   v => { _cfg.MinLvl   = v; _cfg.Save(); }));
        y = AddRow(panel, y, "Рівень до",      BuildIntBox(_cfg.MaxLvl,   v => { _cfg.MaxLvl   = v; _cfg.Save(); }));

        var typesLabel = new Label
        {
            Text      = "ТИПИ ВАНТАЖІВ",
            Font      = UI.Theme.FontLabel,
            ForeColor = UI.Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Left      = 0,
            Top       = y,
            Width     = 300,
            Height    = 18,
        };
        panel.Controls.Add(typesLabel);
        y += 22;

        string[] allTypes = { "Одяг", "Нафта", "Фармацевтика", "Різне", "Продукти", "Автозапчастини", "Інше" };
        var checkFlow = new FlowLayoutPanel
        {
            Left          = 0,
            Top           = y,
            Width         = 400,
            Height        = 80,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = true,
            BackColor     = Color.Transparent,
        };
        foreach (var t in allTypes)
        {
            var cb = new CheckBox
            {
                Text      = t,
                Checked   = _cfg.Types.Contains(t),
                Font      = UI.Theme.FontSmall,
                ForeColor = UI.Theme.TextPrimary,
                BackColor = Color.Transparent,
                AutoSize  = true,
                Margin    = new Padding(0, 2, 12, 2),
            };
            string captured = t;
            cb.CheckedChanged += (_, _) =>
            {
                if (cb.Checked && !_cfg.Types.Contains(captured)) _cfg.Types.Add(captured);
                else _cfg.Types.Remove(captured);
                _cfg.Save();
            };
            checkFlow.Controls.Add(cb);
        }
        panel.Controls.Add(checkFlow);

        return panel;
    }

    static int AddRow(Panel parent, int y, string label, Control input)
    {
        var lbl = new Label
        {
            Text      = label.ToUpperInvariant(),
            Font      = UI.Theme.FontLabel,
            ForeColor = UI.Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Left      = 0, Top = y,
            Width     = 120, Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        input.Left = 128;
        input.Top  = y;
        parent.Controls.Add(lbl);
        parent.Controls.Add(input);
        return y + 30;
    }

    Control BuildMonitorSelector()
    {
        var cb = new ComboBox
        {
            FlatStyle     = FlatStyle.Flat,
            BackColor     = UI.Theme.BgCard,
            ForeColor     = UI.Theme.TextPrimary,
            Font          = UI.Theme.FontInput,
            Width         = 180,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        cb.Items.Add("Авто");
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
            cb.Items.Add($"Монітор {i + 1} ({screens[i].Bounds.Width}x{screens[i].Bounds.Height})");
        cb.SelectedIndex = _cfg.MonitorIndex + 1;
        cb.SelectedIndexChanged += (_, _) =>
        {
            _cfg.MonitorIndex = cb.SelectedIndex - 1;
            _cfg.X = _cfg.Y = _cfg.Width = _cfg.Height = 0;
            _codeZone = null;
            _cfg.Save();
        };
        return cb;
    }

    static Control BuildIntBox(int initial, Action<int> onChange)
    {
        var tb = StyledTextBox(initial.ToString());
        tb.Leave += (_, _) => { if (int.TryParse(tb.Text, out int v)) onChange(v); else tb.Text = initial.ToString(); };
        return tb;
    }

    static Control BuildDoubleBox(double initial, Action<double> onChange)
    {
        var tb = StyledTextBox(initial.ToString("F1"));
        tb.Leave += (_, _) => { if (double.TryParse(tb.Text.Replace(',','.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double v)) onChange(v); else tb.Text = initial.ToString("F1"); };
        return tb;
    }

    static TextBox StyledTextBox(string text) => new()
    {
        Text      = text,
        Width     = 80,
        Height    = 24,
        Font      = UI.Theme.FontInput,
        BackColor = UI.Theme.BgCard,
        ForeColor = UI.Theme.TextPrimary,
        BorderStyle = BorderStyle.FixedSingle,
    };

    // ── Autopilot loop ────────────────────────────────────────────────────────
    void RunAutoPilot()
    {
        try
        {
            _log($"АВТОПІЛОТ УВІМКНЕНО | Мін.ціна:{_cfg.MinPrice}$ | Макс.тонн:{_cfg.MaxTon} | LVL:{_cfg.MinLvl}-{_cfg.MaxLvl}");

            bool  waitingForMenu = false;
            long  clickTime      = 0;

            while (_state == BotState.AutoPilot)
            {
                if (!waitingForMenu)
                {
                    var cards = OrderScanner.FindCards(_cfg.MonitorIndex);
                    if (cards.Count > 0)
                    {
                        var valid = new List<OrderScanner.OrderCard>();
                        foreach (var c in cards)
                        {
                            bool ok = c.PricePerKm >= _cfg.MinPrice
                                   && c.Tonnage > 0 && c.Tonnage <= _cfg.MaxTon
                                   && c.Level >= _cfg.MinLvl && c.Level <= _cfg.MaxLvl
                                   && _cfg.Types.Contains(c.Type);

                            _log($"{(ok ? "✅" : "❌")} {c.Type} ({c.Tonnage}т, LVL{c.Level}) → {c.PricePerKm}$/km");
                            if (ok) valid.Add(c);
                        }

                        if (valid.Count > 0)
                        {
                            var best = valid.OrderByDescending(c => c.PricePerKm).First();
                            _log($"🏆 Найкращий: {best.Type} {best.PricePerKm}$/km — відкриваю...");
                            MouseInput.Click(best.ClickPoint.X, best.ClickPoint.Y);
                            Thread.Sleep(600);

                            var scr   = WinOcr.GetMonitorBounds(_cfg.MonitorIndex);
                            int btnX  = scr.X + scr.Width / 2 + (int)(scr.Width * 0.075);
                            int btnY  = scr.Y + scr.Height / 2 + (int)(scr.Height * 0.16);
                            _log("🖱 Натискаю 'Взяти замовлення'...");
                            MouseInput.Click(btnX, btnY);

                            waitingForMenu = true;
                            clickTime      = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            _log("Очікую код підтвердження...");
                        }
                        else
                        {
                            _log("Жодне замовлення не підійшло. Очікую...");
                        }
                    }
                    Thread.Sleep(1000);
                }
                else
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clickTime > 15000)
                    {
                        _log("Меню коду не з'явилося за 15с. Повертаюся до пошуку.");
                        _codeZone = null; // скидаємо кеш — можливо позиція змінилась
                        waitingForMenu = false;
                        continue;
                    }

                    if (_codeZone == null)
                    {
                        _codeZone = WinOcr.FindDialogRegion(_cfg.MonitorIndex);
                        if (_codeZone != null)
                        {
                            _cfg.X = _codeZone.Value.X; _cfg.Y = _codeZone.Value.Y;
                            _cfg.Width = _codeZone.Value.Width; _cfg.Height = _codeZone.Value.Height;
                            _cfg.Save();
                        }
                    }

                    if (_codeZone != null)
                    {
                        using var img = ScreenCapture.Capture(_codeZone.Value);
                        var code = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();
                        if (code != null)
                        {
                            _log($"Знайдено код: {code} — вводжу...");
                            Thread.Sleep(100);
                            KeyInput.TypeCode(code, turbo: true);
                            _log("Готово! Замовлення взято. 🚚");
                            _state = BotState.Idle;
                            return;
                        }
                    }
                    Thread.Sleep(50);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[ПОМИЛКА] {ex.Message}");
            _state = BotState.Idle;
        }
    }
}
