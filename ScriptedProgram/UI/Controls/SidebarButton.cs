using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ScriptedProgram.UI.Controls;

public sealed class SidebarButton : Control
{
    private bool _selected;
    private bool _hovered;

    public string ModuleId { get; set; } = "";

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    public SidebarButton()
    {
        Height = 56;
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint            |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true;  Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = new Rectangle(1, 1, Width - 3, Height - 3);

        // Background
        var bg = _selected  ? Theme.AccentDim
               : _hovered   ? Color.FromArgb(0x26, 0x26, 0x26)
               :               Theme.BgCard;
        using var bgBrush = new SolidBrush(bg);
        using var path    = RoundRect(r, 10);
        g.FillPath(bgBrush, path);

        // Border
        var borderColor = _selected ? Theme.Accent : Theme.Border;
        using var pen = new Pen(borderColor, _selected ? 1.5f : 1f);
        g.DrawPath(pen, path);

        // Text
        var fg = _selected ? Theme.Accent : Theme.TextPrimary;
        using var textBrush = new SolidBrush(fg);
        var sf = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming      = StringTrimming.EllipsisCharacter,
        };
        g.DrawString(Text, Theme.FontSidebar, textBrush, new RectangleF(8, 0, Width - 16, Height), sf);
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
