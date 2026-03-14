using System.Drawing;
using System.Windows.Forms;

namespace ScriptedProgram.UI.Controls;

public sealed class TerminalPanel : Panel
{
    private readonly Label      _header;
    private readonly RichTextBox _output;

    public TerminalPanel()
    {
        BackColor = Theme.BgPanel;

        // ── Separator line on top ───────────────────────────────────────────
        var topLine = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 1,
            BackColor = Theme.Border,
        };

        // ── Header ─────────────────────────────────────────────────────────
        _header = new Label
        {
            Dock        = DockStyle.Top,
            Height      = 30,
            Text        = "ТЕРМІНАЛ  [active]",
            Font        = Theme.FontMono,
            ForeColor   = Theme.AccentRed,
            BackColor   = Color.FromArgb(0x16, 0x16, 0x16),
            TextAlign   = ContentAlignment.MiddleLeft,
            Padding     = new Padding(14, 0, 0, 0),
        };

        // ── Output ─────────────────────────────────────────────────────────
        _output = new RichTextBox
        {
            Dock        = DockStyle.Fill,
            BackColor   = Theme.BgBase,
            ForeColor   = Color.FromArgb(0x77, 0xBB, 0x77),
            Font        = Theme.FontMono,
            ReadOnly    = true,
            BorderStyle = BorderStyle.None,
            ScrollBars  = RichTextBoxScrollBars.Vertical,
            WordWrap    = true,
            Padding     = new Padding(10, 6, 0, 0),
        };

        Controls.Add(_output);
        Controls.Add(_header);
        Controls.Add(topLine);
    }

    public void Log(string message)
    {
        if (InvokeRequired) { Invoke(() => Log(message)); return; }
        _output.AppendText($"... {message}\n");
        _output.ScrollToCaret();
    }

    public void Clear() => _output.Clear();
}
