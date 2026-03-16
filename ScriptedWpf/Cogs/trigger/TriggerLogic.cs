using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Trigger;

static class TriggerLogic
{
    // ── Screen capture ────────────────────────────────────────────────────────

    public static Screen GetMonitor(int index)
    {
        var screens = Screen.AllScreens;
        if (index < 0 || index >= screens.Length) return Screen.PrimaryScreen ?? screens[0];
        return screens[index];
    }

    /// <summary>
    /// Returns scaled capture rect: adjusts saved coords (recorded at monitorW×monitorH)
    /// to current monitor size, then expands by ±80px, clamped to screen bounds.
    /// </summary>
    public static Rectangle GetCaptureRect(TriggerActionData d, Screen screen)
    {
        var b = screen.Bounds;
        double scaleX = b.Width  / (double)Math.Max(1, d.MonitorW);
        double scaleY = b.Height / (double)Math.Max(1, d.MonitorH);

        int x = (int)(d.CaptureX * scaleX);
        int y = (int)(d.CaptureY * scaleY);
        int w = (int)(d.CaptureW * scaleX);
        int h = (int)(d.CaptureH * scaleY);

        const int pad = 80;
        int rx = Math.Max(b.Left,               b.Left + x - pad);
        int ry = Math.Max(b.Top,                b.Top  + y - pad);
        int rr = Math.Min(b.Left + b.Width,     b.Left + x + w + pad);
        int rb = Math.Min(b.Top  + b.Height,    b.Top  + y + h + pad);

        return new Rectangle(rx, ry, rr - rx, rb - ry);
    }

    public static Bitmap CaptureRect(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    // ── Image adjustment ──────────────────────────────────────────────────────

    /// <summary>
    /// Applies brightness (-100..100) and contrast (-100..100) to a bitmap in-place.
    /// </summary>
    public static Bitmap AdjustBitmap(Bitmap src, int brightness, int contrast)
    {
        if (brightness == 0 && contrast == 0) return new Bitmap(src);

        var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        float b = brightness / 100f;        // -1..1
        float c = (contrast + 100f) / 100f; // 0..2

        var data    = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var srcData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);

        int bytes = Math.Abs(data.Stride) * bmp.Height;
        byte[] px = new byte[bytes];
        Marshal.Copy(srcData.Scan0, px, 0, bytes);

        for (int i = 0; i < bytes; i += 4)
        {
            for (int ch = 0; ch < 3; ch++)
            {
                float v = px[i + ch] / 255f;
                v = (v - 0.5f) * c + 0.5f + b;
                px[i + ch] = (byte)Math.Clamp((int)(v * 255), 0, 255);
            }
        }

        Marshal.Copy(px, 0, data.Scan0, bytes);
        bmp.UnlockBits(data);
        src.UnlockBits(srcData);
        return bmp;
    }

    // ── Template matching (NCC) ────────────────────────────────────────────────

    /// <summary>
    /// Returns best normalized cross-correlation (0..1) between template and screen capture.
    /// Slides template over capture with step 4px for speed; returns max found score.
    /// </summary>
    public static double MatchTemplate(Bitmap templ, Bitmap capture)
    {
        // resize capture if wildly different — use center crop of ±80px expanded region
        if (templ.Width > capture.Width || templ.Height > capture.Height)
            return 0;

        var td = templ.LockBits(new Rectangle(0, 0, templ.Width, templ.Height),
                                 ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var cd = capture.LockBits(new Rectangle(0, 0, capture.Width, capture.Height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int tw = templ.Width, th = templ.Height;
        int cw = capture.Width, ch = capture.Height;

        byte[] tp = new byte[Math.Abs(td.Stride) * th];
        byte[] cp = new byte[Math.Abs(cd.Stride) * ch];
        Marshal.Copy(td.Scan0, tp, 0, tp.Length);
        Marshal.Copy(cd.Scan0, cp, 0, cp.Length);

        templ.UnlockBits(td);
        capture.UnlockBits(cd);

        int ts = Math.Abs(td.Stride);
        int cs = Math.Abs(cd.Stride);

        // Pre-compute template mean
        double tSum = 0;
        int    tN   = tw * th;
        for (int ty = 0; ty < th; ty++)
        for (int tx = 0; tx < tw; tx++)
        {
            int ti = ty * ts + tx * 4;
            tSum += (tp[ti] + tp[ti+1] + tp[ti+2]) / 3.0;
        }
        double tMean = tSum / tN;

        double tVar = 0;
        for (int ty = 0; ty < th; ty++)
        for (int tx = 0; tx < tw; tx++)
        {
            int ti = ty * ts + tx * 4;
            double v = (tp[ti] + tp[ti+1] + tp[ti+2]) / 3.0 - tMean;
            tVar += v * v;
        }
        double tStd = Math.Sqrt(tVar);
        if (tStd < 1e-6) return 1.0; // blank template matches everything

        const int step = 3;
        double bestScore = 0;

        for (int oy = 0; oy <= ch - th; oy += step)
        for (int ox = 0; ox <= cw - tw; ox += step)
        {
            double cSum = 0;
            for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                int ci = (oy + ty) * cs + (ox + tx) * 4;
                cSum += (cp[ci] + cp[ci+1] + cp[ci+2]) / 3.0;
            }
            double cMean = cSum / tN;

            double num  = 0, cVar = 0;
            for (int ty = 0; ty < th; ty++)
            for (int tx = 0; tx < tw; tx++)
            {
                int ti = ty * ts + tx * 4;
                int ci = (oy + ty) * cs + (ox + tx) * 4;
                double tv = (tp[ti] + tp[ti+1] + tp[ti+2]) / 3.0 - tMean;
                double cv = (cp[ci] + cp[ci+1] + cp[ci+2]) / 3.0 - cMean;
                num  += tv * cv;
                cVar += cv * cv;
            }
            double denom = tStd * Math.Sqrt(cVar);
            double score = denom < 1e-6 ? 0 : num / denom;
            if (score > bestScore) bestScore = score;
            if (bestScore > 0.999) break;
        }

        return bestScore;
    }

    // ── WinAPI input (uses WinApi structs to avoid marshal layout bugs) ──────

    static WinApi.INPUT MakeKey(ushort vk, uint flags = 0) => new WinApi.INPUT
    {
        type = WinApi.INPUT_KEYBOARD,
        u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = flags } },
    };

    public static ushort KeyNameToVk(string name) => name.ToUpperInvariant() switch
    {
        "A"=>(ushort)0x41,"B"=>(ushort)0x42,"C"=>(ushort)0x43,"D"=>(ushort)0x44,
        "E"=>(ushort)0x45,"F"=>(ushort)0x46,"G"=>(ushort)0x47,"H"=>(ushort)0x48,
        "I"=>(ushort)0x49,"J"=>(ushort)0x4A,"K"=>(ushort)0x4B,"L"=>(ushort)0x4C,
        "M"=>(ushort)0x4D,"N"=>(ushort)0x4E,"O"=>(ushort)0x4F,"P"=>(ushort)0x50,
        "Q"=>(ushort)0x51,"R"=>(ushort)0x52,"S"=>(ushort)0x53,"T"=>(ushort)0x54,
        "U"=>(ushort)0x55,"V"=>(ushort)0x56,"W"=>(ushort)0x57,"X"=>(ushort)0x58,
        "Y"=>(ushort)0x59,"Z"=>(ushort)0x5A,
        "0"=>(ushort)0x30,"1"=>(ushort)0x31,"2"=>(ushort)0x32,"3"=>(ushort)0x33,
        "4"=>(ushort)0x34,"5"=>(ushort)0x35,"6"=>(ushort)0x36,"7"=>(ushort)0x37,
        "8"=>(ushort)0x38,"9"=>(ushort)0x39,
        "F1"=>(ushort)0x70,"F2"=>(ushort)0x71,"F3"=>(ushort)0x72,"F4"=>(ushort)0x73,
        "F5"=>(ushort)0x74,"F6"=>(ushort)0x75,"F7"=>(ushort)0x76,"F8"=>(ushort)0x77,
        "F9"=>(ushort)0x78,"F10"=>(ushort)0x79,"F11"=>(ushort)0x7A,"F12"=>(ushort)0x7B,
        "SPACE"=>(ushort)0x20,"ENTER"=>(ushort)0x0D,"TAB"=>(ushort)0x09,
        "ESCAPE"=>(ushort)0x1B,"ESC"=>(ushort)0x1B,
        "LEFT"=>(ushort)0x25,"UP"=>(ushort)0x26,"RIGHT"=>(ushort)0x27,"DOWN"=>(ushort)0x28,
        "SHIFT"=>(ushort)0x10,"CTRL"=>(ushort)0x11,"ALT"=>(ushort)0x12,
        "LSHIFT"=>(ushort)0xA0,"RSHIFT"=>(ushort)0xA1,
        "LCTRL"=>(ushort)0xA2, "RCTRL"=>(ushort)0xA3,
        _ => 0
    };

    /// <summary>
    /// Parses a hotkey string like "F9", "CTRL+F9", "ALT+E" into (modifiers, vk).
    /// modifiers: Win32 MOD_* flags (1=ALT, 2=CTRL, 4=SHIFT).
    /// Returns vk=0 if the main key is unknown.
    /// </summary>
    public static (uint modifiers, ushort vk) ParseHotkey(string combo)
    {
        uint   mods   = 0;
        ushort mainVk = 0;

        foreach (var part in combo.ToUpperInvariant().Split('+'))
        {
            var p = part.Trim();
            if      (p is "CTRL" or "CONTROL") mods |= 2;
            else if (p is "SHIFT")             mods |= 4;
            else if (p is "ALT")               mods |= 1;
            else    mainVk = KeyNameToVk(p);
        }

        return (mods, mainVk);
    }

    /// <summary>
    /// Presses a key combination like "CTRL+E", "SHIFT+F5", or plain "E".
    /// Holds modifiers down, presses the main key, then releases all.
    /// </summary>
    public static void PressCombo(string combo, Action<string> log, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(combo)) return;

        var parts  = combo.ToUpperInvariant().Split('+');
        var modVks = new System.Collections.Generic.List<ushort>();
        ushort mainVk = 0;

        foreach (var part in parts)
        {
            var p = part.Trim();
            if      (p is "CTRL" or "CONTROL") modVks.Add(0x11);
            else if (p is "SHIFT")             modVks.Add(0x10);
            else if (p is "ALT")               modVks.Add(0x12);
            else
            {
                mainVk = KeyNameToVk(p);
                if (mainVk == 0) { log($"[{moduleName}] ❌ Невідома клавіша: {p}"); return; }
            }
        }

        if (mainVk == 0) return;

        var inputs = new System.Collections.Generic.List<WinApi.INPUT>();
        foreach (var vk in modVks)            inputs.Add(MakeKey(vk));
        inputs.Add(MakeKey(mainVk));
        inputs.Add(MakeKey(mainVk, WinApi.KEYEVENTF_KEYUP));
        for (int m = modVks.Count - 1; m >= 0; m--)
            inputs.Add(MakeKey(modVks[m], WinApi.KEYEVENTF_KEYUP));

        var arr = inputs.ToArray();
        WinApi.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<WinApi.INPUT>());
    }
}
