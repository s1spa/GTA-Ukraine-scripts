using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScriptedProgram.UI.Controls;

public sealed class ToggleSwitch : Control
{
    private bool _checked;

    public bool Checked
    {
        get => _checked;
        set { if (_checked == value) return; _checked = value; Invalidate(); }
    }

    public event EventHandler? CheckedChanged;

    public ToggleSwitch()
    {
        Size = new Size(50, 26);
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
        CheckedChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bgColor = _checked
            ? Theme.Accent
            : Color.FromArgb(0x3A, 0x3A, 0x3A);

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundRect(rect, Height / 2);
        using var bgBrush = new SolidBrush(bgColor);
        g.FillPath(bgBrush, path);

        // Knob
        int d = Height - 6;
        float travel = Width - d - 6f;
        float kx = _checked ? 3f + travel : 3f;
        using var knob = new SolidBrush(Color.White);
        g.FillEllipse(knob, kx, 3f, d, d);
    }

    static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        p.AddArc(r.X,                   r.Y,                    radius * 2, radius * 2, 180, 90);
        p.AddArc(r.Right - radius * 2,  r.Y,                    radius * 2, radius * 2, 270, 90);
        p.AddArc(r.Right - radius * 2,  r.Bottom - radius * 2,  radius * 2, radius * 2,   0, 90);
        p.AddArc(r.X,                   r.Bottom - radius * 2,  radius * 2, radius * 2,  90, 90);
        p.CloseFigure();
        return p;
    }
}
