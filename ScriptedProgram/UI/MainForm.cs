using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using ScriptedProgram.Core;
using ScriptedProgram.Models;
using ScriptedProgram.UI.Controls;

namespace ScriptedProgram.UI;

public class MainForm : Form
{
    // ── Services ──────────────────────────────────────────────────────────────
    private HotkeyService? _hotkeys;
    private readonly List<(ModuleInfo info, IModule? module)> _modules = new();

    // ── State ─────────────────────────────────────────────────────────────────
    private string? _activeModuleId;
    private bool    _scriptsTabActive;

    // ── Layout panels ─────────────────────────────────────────────────────────
    private Panel         _topBar       = null!;
    private Panel         _sidebarInner = null!;
    private Panel         _rightPanel   = null!;
    private TerminalPanel _terminal     = null!;

    // ── Nav labels ────────────────────────────────────────────────────────────
    private Label _tabSettings = null!;
    private Label _tabScripts  = null!;

    // ── Content panels ────────────────────────────────────────────────────────
    private PluginDetailPanel _detailPanel  = null!;
    private ScriptsPanel      _scriptsPanel = null!;

    public MainForm()
    {
        BuildUI();
        Load += (_, _) => AfterLoad();
    }

    // ── Form Init ─────────────────────────────────────────────────────────────
    void BuildUI()
    {
        SuspendLayout();

        FormBorderStyle = FormBorderStyle.None;
        Size            = new Size(960, 640);
        MinimumSize     = new Size(820, 520);
        BackColor       = Theme.BgBase;
        StartPosition   = FormStartPosition.CenterScreen;
        DoubleBuffered  = true;
        Text            = "GTA Ukraine Scripts";

        BuildTopBar();
        BuildTerminal();
        BuildBody();

        ResumeLayout(true);
    }

    void AfterLoad()
    {
        _hotkeys = new HotkeyService(Handle);
        // Defer until after first layout so ClientSize is correct
        Shown += (_, _) => LoadModules();
    }

    // ── Top Bar ───────────────────────────────────────────────────────────────
    void BuildTopBar()
    {
        _topBar = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 52, // трохи більше місця
            BackColor = Theme.BgPanel,
        };
        _topBar.MouseDown += DragForm;

        // Logo
        var logo = new Panel
        {
            Width     = 155,
            Dock      = DockStyle.Left,
            BackColor = Color.Transparent,
            Cursor    = Cursors.SizeAll,
        };
        logo.MouseDown += DragForm;
        logo.Paint     += PaintLogo;

        // Nav tabs
        _tabSettings = MakeTab("Налаштування");
        _tabScripts  = MakeTab("Скрипти");
        _tabSettings.Click += (_, _) => ActivateSettings();
        _tabScripts.Click  += (_, _) => ActivateScripts();

        // Close / Minimize
        var closeBtn = MakeWinBtn("×", Theme.AccentRed, Application.Exit); // Легший символ
        var minBtn   = MakeWinBtn("─", Theme.TextMuted, () => WindowState = FormWindowState.Minimized);

        var winBtns = new FlowLayoutPanel
        {
            Width           = 80,
            Dock            = DockStyle.Right,
            BackColor       = Color.Transparent,
            FlowDirection   = FlowDirection.RightToLeft,
            WrapContents    = false,
            Padding         = new Padding(0),
        };
        winBtns.Controls.Add(closeBtn);
        winBtns.Controls.Add(minBtn);

        var navFlow = new FlowLayoutPanel
        {
            Dock          = DockStyle.Fill,
            BackColor     = Color.Transparent,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            Padding       = new Padding(4, 10, 0, 0),
        };
        navFlow.MouseDown += DragForm;
        navFlow.Controls.Add(_tabSettings);
        navFlow.Controls.Add(_tabScripts);

        // Separator at bottom
        var sep = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };

        _topBar.Controls.Add(navFlow);
        _topBar.Controls.Add(winBtns);
        _topBar.Controls.Add(logo);
        _topBar.Controls.Add(sep);

        Controls.Add(_topBar);
    }

    void PaintLogo(object? s, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Diamond icon
        var pts = new Point[] { new(18, 25), new(26, 15), new(34, 25), new(26, 35) };
        using var pen   = new Pen(Theme.TextMuted, 1.5f);
        using var brush = new SolidBrush(Theme.TextMuted);
        g.DrawPolygon(pen, pts);

        var sf = new StringFormat { LineAlignment = StringAlignment.Center };
        // Використовуємо висоту батьківського елемента для малювання тексту
        g.DrawString("Логотип", Theme.FontUi, brush, new RectangleF(42, 0, 100, e.ClipRectangle.Height), sf);
    }

    Label MakeTab(string text)
    {
        var lbl = new Label
        {
            Text      = text,
            AutoSize  = false,
            Size      = new Size(130, 30),
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = Theme.FontUi,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Hand,
            Margin    = new Padding(2, 0, 2, 0),
        };
        lbl.MouseEnter += (_, _) => { if (lbl.ForeColor != Theme.Accent) lbl.ForeColor = Theme.TextPrimary; };
        lbl.MouseLeave += (_, _) => { if (lbl.ForeColor != Theme.Accent) lbl.ForeColor = Theme.TextMuted; };
        return lbl;
    }

    Label MakeWinBtn(string icon, Color hoverColor, Action onClick)
    {
        var btn = new Label
        {
            Text      = icon,
            AutoSize  = false,
            Size      = new Size(40, _topBar.Height), // Ширина 40, висота як у topBar
            TextAlign = ContentAlignment.MiddleCenter,
            Font      = new Font("Segoe UI", 11f), // Однаковий розмір для обох
            ForeColor = Theme.TextDim,
            BackColor = Color.Transparent,
            Cursor    = Cursors.Hand,
        };
        btn.MouseEnter += (_, _) => btn.ForeColor = hoverColor;
        btn.MouseLeave += (_, _) => btn.ForeColor = Theme.TextDim;
        btn.Click      += (_, _) => onClick();
        return btn;
    }

    // ── Terminal ──────────────────────────────────────────────────────────────
    void BuildTerminal()
    {
        _terminal = new TerminalPanel
        {
            Dock   = DockStyle.Bottom,
            Height = 155,
        };
        Controls.Add(_terminal);
    }

    // ── Body (sidebar + right panel) ──────────────────────────────────────────
    void BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBase };

        // Sidebar wrapper
        var sidebar = new Panel
        {
            Dock      = DockStyle.Left,
            Width     = 272,
            BackColor = Theme.BgPanel,
        };

        // Scrollable inner area for buttons
        _sidebarInner = new Panel
        {
            Dock       = DockStyle.Fill,
            BackColor  = Color.Transparent,
            AutoScroll = true,
        };
        sidebar.Controls.Add(_sidebarInner);

        // Vertical separator
        var sep = new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Theme.Border };

        // Right panel
        _rightPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.BgBase };

        _detailPanel  = new PluginDetailPanel { Dock = DockStyle.Fill, Visible = false };
        _scriptsPanel = new ScriptsPanel      { Dock = DockStyle.Fill, Visible = false };

        _rightPanel.Controls.Add(_detailPanel);
        _rightPanel.Controls.Add(_scriptsPanel);

        body.Controls.Add(_rightPanel);
        body.Controls.Add(sep);
        body.Controls.Add(sidebar);

        Controls.Add(body);
    }

    // ── Module Loading ────────────────────────────────────────────────────────
    void LoadModules()
    {
        string cogsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cogs");

        _modules.Clear();
        _modules.AddRange(ModuleLoader.LoadAll(cogsPath));

        foreach (var (info, module) in _modules)
            module?.Initialize(msg => _terminal.Log(msg));

        foreach (var (info, module) in _modules)
            module?.RegisterHotkeys(_hotkeys!);

        RebuildSidebar();

        // Auto-select first module
        if (_modules.Count > 0)
            SelectModule(_modules[0].info.Id);
    }

    void RebuildSidebar()
    {
        _sidebarInner.Controls.Clear();
        int y = _topBar.Height + 8;
        // The button width should be the full client width of the container panel.
        // The button's own painting logic will handle internal margins.
        int btnWidth = _sidebarInner.ClientSize.Width;

        foreach (var (info, _) in _modules)
        {
            var btn = new SidebarButton
            {
                Text     = info.Name,
                ModuleId = info.Id,
                Left     = 0,
                Top      = y,
                Width    = btnWidth > 0 ? btnWidth : 240,
                Height   = 56,
                Anchor   = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            btn.Click += (_, _) => SelectModule(info.Id);

            _sidebarInner.Controls.Add(btn);
            y += 64;
        }

        // Resize sidebar buttons when form is resized
        _sidebarInner.Resize += (_, _) =>
        {
            // Recalculate width to the full client width.
            int w = _sidebarInner.ClientSize.Width;
            foreach (Control c in _sidebarInner.Controls)
                if (c is SidebarButton sb) sb.Width = w;
        };
    }

    // ── Navigation ────────────────────────────────────────────────────────────
    void SelectModule(string id)
    {
        _activeModuleId   = id;
        _scriptsTabActive = false;
        RefreshTabs();
        RefreshSidebarSelection();

        var found = _modules.Find(m => m.info.Id == id);
        if (found.info == null) return;

        _scriptsPanel.Visible = false;
        _detailPanel.Visible  = true;
        _detailPanel.ShowModule(found.info, found.module);
    }

    void ActivateScripts()
    {
        _activeModuleId   = null;
        _scriptsTabActive = true;
        RefreshTabs();
        RefreshSidebarSelection();

        _detailPanel.Visible  = false;
        _scriptsPanel.Visible = true;
        _scriptsPanel.ShowScripts(_modules);
    }

    void ActivateSettings()
    {
        _activeModuleId   = null;
        _scriptsTabActive = false;
        RefreshTabs();
        RefreshSidebarSelection();

        _detailPanel.Visible  = false;
        _scriptsPanel.Visible = false;
        // TODO: global settings panel
    }

    void RefreshTabs()
    {
        _tabSettings.ForeColor = (!_scriptsTabActive && _activeModuleId == null)
            ? Theme.Accent : Theme.TextMuted;
        _tabScripts.ForeColor  = _scriptsTabActive ? Theme.Accent : Theme.TextMuted;
    }

    void RefreshSidebarSelection()
    {
        foreach (Control c in _sidebarInner.Controls)
            if (c is SidebarButton btn)
                btn.Selected = btn.ModuleId == _activeModuleId;
    }

    // ── Window Dragging ───────────────────────────────────────────────────────
    void DragForm(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            WinApi.ReleaseCapture();
            WinApi.SendMessage(Handle, WinApi.WM_NCLBUTTONDOWN, WinApi.HTCAPTION, 0);
        }
    }

    // ── Resize grip (bottom-right corner) ────────────────────────────────────
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Diamond grip indicator
        using var brush = new SolidBrush(Theme.TextDim);
        int gx = Width - 18, gy = Height - 18;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPolygon(brush, new Point[]
        {
            new(gx, gy + 8), new(gx + 8, gy), new(gx + 14, gy + 6), new(gx + 6, gy + 14),
        });
    }

    const int WM_NCHITTEST = 0x84, HTBOTTOMRIGHT = 17;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            var pt = PointToClient(new Point(m.LParam.ToInt32() & 0xFFFF, m.LParam.ToInt32() >> 16));
            if (pt.X >= Width - 20 && pt.Y >= Height - 20)
            {
                m.Result = (nint)HTBOTTOMRIGHT;
                return;
            }
        }
        _hotkeys?.ProcessMessage(ref m);
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        foreach (var (_, module) in _modules) module?.Stop();
        _hotkeys?.Dispose();
        base.OnFormClosing(e);
    }
}
