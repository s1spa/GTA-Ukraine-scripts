using System.Drawing;

namespace ScriptedProgram.UI;

public static class Theme
{
    // ── Backgrounds ──────────────────────────────────────────────────────────
    public static readonly Color BgBase    = Color.FromArgb(0x11, 0x11, 0x11);
    public static readonly Color BgPanel   = Color.FromArgb(0x1A, 0x1A, 0x1A);
    public static readonly Color BgCard    = Color.FromArgb(0x22, 0x22, 0x22);

    // ── Borders ───────────────────────────────────────────────────────────────
    public static readonly Color Border    = Color.FromArgb(0x2E, 0x2E, 0x2E);

    // ── Accent ────────────────────────────────────────────────────────────────
    public static readonly Color Accent    = Color.FromArgb(0x4C, 0xFF, 0x72);  // green
    public static readonly Color AccentDim = Color.FromArgb(0x16, 0x2E, 0x18);
    public static readonly Color AccentRed = Color.FromArgb(0xFF, 0x44, 0x44);

    // ── Text ──────────────────────────────────────────────────────────────────
    public static readonly Color TextPrimary = Color.FromArgb(0xE0, 0xE0, 0xE0);
    public static readonly Color TextMuted   = Color.FromArgb(0x66, 0x66, 0x66);
    public static readonly Color TextDim     = Color.FromArgb(0x44, 0x44, 0x44);

    // ── Fonts ─────────────────────────────────────────────────────────────────
    public static readonly Font FontUi      = new("Segoe UI",  10f);
    public static readonly Font FontTitle   = new("Segoe UI",  13f, FontStyle.Bold);
    public static readonly Font FontSmall   = new("Segoe UI",   8.5f);
    public static readonly Font FontLabel   = new("Segoe UI",   8f,  FontStyle.Bold);
    public static readonly Font FontSidebar = new("Segoe UI",  10.5f);
    public static readonly Font FontMono    = new("Consolas",   9f);
    public static readonly Font FontInput   = new("Segoe UI",   9.5f);
}
