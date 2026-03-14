using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ScriptedProgram.Core;
using ScriptedProgram.Models;

namespace ScriptedProgram.UI.Controls;

public sealed class ScriptsPanel : Panel
{
    private readonly Panel _list;

    public ScriptsPanel()
    {
        BackColor = Theme.BgBase;
        Padding   = new Padding(24, 18, 24, 18);

        // Outer border panel (green, like in the mockup)
        var border = new Panel
        {
            Dock      = DockStyle.Fill,
            BackColor = Theme.BgBase,
            Padding   = new Padding(1),
        };

        _list = new Panel
        {
            Dock       = DockStyle.Fill,
            BackColor  = Theme.BgBase,
            AutoScroll = true,
            Padding    = new Padding(10, 8, 10, 8),
        };

        border.Controls.Add(_list);
        border.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Accent, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, border.Width - 1, border.Height - 1);
        };

        Controls.Add(border);
    }

    public void ShowScripts(List<(ModuleInfo info, IModule? module)> modules)
    {
        _list.Controls.Clear();
        int y = 4;

        foreach (var (info, module) in modules)
        {
            foreach (var script in info.Scripts)
            {
                var row = BuildScriptRow(script, module);
                row.Top  = y;
                row.Left = 0;
                row.Width  = _list.ClientSize.Width;
                row.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                _list.Controls.Add(row);
                y += row.Height + 6;
            }
        }
    }

    Panel BuildScriptRow(ScriptInfo script, IModule? module)
    {
        bool expanded = false;

        var row = new Panel
        {
            BackColor = Theme.BgCard,
            Height    = 44,
        };

        // Rounded border painting
        row.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(Theme.Border, 1f);
            using var path = RoundRect(new Rectangle(0, 0, row.Width - 1, row.Height - 1), 8);
            e.Graphics.DrawPath(pen, path);
        };

        // ▶ arrow
        var arrow = new Label
        {
            Text      = "▶",
            Font      = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Size      = new Size(26, 44),
            Left      = 8,
            Top       = 0,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor    = Cursors.Hand,
        };

        // Script name
        var name = new Label
        {
            Text      = script.Name,
            Font      = Theme.FontUi,
            ForeColor = Theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Left      = 36,
            Top       = 0,
            Width     = row.Width - 36 - 70,
            Height    = 44,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };

        // Toggle
        var toggle = new ToggleSwitch
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Top    = (44 - 26) / 2,
        };
        toggle.CheckedChanged += (_, _) =>
        {
            if (toggle.Checked) module?.Start();
            else                module?.Stop();
        };

        // Description (hidden by default)
        var desc = new Label
        {
            Text      = script.Description,
            Font      = Theme.FontSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Color.Transparent,
            AutoSize  = false,
            Left      = 36,
            Top       = 44,
            Height    = 36,
            TextAlign = ContentAlignment.TopLeft,
            Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Visible   = false,
        };

        row.Controls.Add(desc);
        row.Controls.Add(toggle);
        row.Controls.Add(name);
        row.Controls.Add(arrow);

        // Expand / collapse on arrow click
        arrow.Click += (_, _) =>
        {
            expanded = !expanded;
            arrow.Text   = expanded ? "▼" : "▶";
            arrow.ForeColor = expanded ? Theme.Accent : Theme.TextMuted;
            desc.Visible = expanded;
            row.Height   = expanded ? 84 : 44;
            // Reflow siblings
            ReflowList();
        };

        // Layout
        row.Layout += (_, _) =>
        {
            toggle.Left = row.Width - 60;
            name.Width  = row.Width - 36 - 70;
            desc.Width  = row.Width - 44;
        };

        return row;
    }

    void ReflowList()
    {
        int y = 4;
        foreach (Control c in _list.Controls)
        {
            c.Top = y;
            y += c.Height + 6;
        }
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X,                  r.Y,                   radius * 2, radius * 2, 180, 90);
        p.AddArc(r.Right - radius * 2, r.Y,                   radius * 2, radius * 2, 270, 90);
        p.AddArc(r.Right - radius * 2, r.Bottom - radius * 2, radius * 2, radius * 2,   0, 90);
        p.AddArc(r.X,                  r.Bottom - radius * 2, radius * 2, radius * 2,  90, 90);
        p.CloseFigure();
        return p;
    }
}
