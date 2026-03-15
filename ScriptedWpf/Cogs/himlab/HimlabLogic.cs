using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ScriptedWpf.Cogs.Himlab;

public static class HimlabLogic
{
    public static readonly char[] Letters = { 'W', 'A', 'S', 'D' };

    // ── Capture ───────────────────────────────────────────────────────────────

    // ── Captcha presence detection ────────────────────────────────────────────
    // Dual criterion:
    //   1. brightRatio > 0.50 — card background is mostly white (gray > 180)
    //   2. darkRatio   > 0.05 — letter strokes present (gray < 70)
    // A bright game surface fails #2; a dark background fails #1.

    public static unsafe bool IsCaptchaVisible(Rectangle region)
    {
        using var bmp  = Capture(region);
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte* p      = (byte*)data.Scan0;
        int   n      = bmp.Width * bmp.Height;
        int   bright = 0;
        int   dark   = 0;

        for (int y = 0; y < bmp.Height; y++)
        for (int x = 0; x < bmp.Width;  x++)
        {
            byte* px   = p + y * data.Stride + x * 4;
            byte  gray = (byte)(px[2] * 0.299 + px[1] * 0.587 + px[0] * 0.114);
            if      (gray > 180) bright++;
            else if (gray < 70 ) dark++;
        }

        bmp.UnlockBits(data);

        double brightRatio = (double)bright / n;
        double darkRatio   = (double)dark   / n;

        return brightRatio > 0.50 && darkRatio > 0.05;
    }

    public static Rectangle GetCaptureRegion(int monitorIndex, HimlabConfig cfg)
    {
        var b    = GetMonitorBounds(monitorIndex);
        int cx   = b.X + b.Width / 2;
        int cy   = b.Y + (int)(b.Height * cfg.CenterYPct);
        int half = cfg.CaptureSize / 2;
        return new Rectangle(cx - half, cy - half, cfg.CaptureSize, cfg.CaptureSize);
    }

    public static Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        var s = (monitorIndex >= 0 && monitorIndex < screens.Length)
            ? screens[monitorIndex]
            : System.Windows.Forms.Screen.PrimaryScreen!;
        return new Rectangle(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
    }

    public static Bitmap Capture(Rectangle region)
    {
        var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(region.X, region.Y, 0, 0, new Size(region.Width, region.Height));
        return bmp;
    }

    // ── Preprocessing ─────────────────────────────────────────────────────────
    // Grayscale → threshold (keep dark letter strokes) → resize to 64×64

    public static unsafe Bitmap Preprocess(Bitmap src)
    {
        const int OutSize   = 64;
        const int Threshold = 140; // darker than this → black (letter), rest → white

        var thresholded = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(
            new Rectangle(0, 0, src.Width, src.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var tData = thresholded.LockBits(
            new Rectangle(0, 0, thresholded.Width, thresholded.Height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte* sp = (byte*)sData.Scan0;
        byte* tp = (byte*)tData.Scan0;

        for (int y = 0; y < src.Height; y++)
        for (int x = 0; x < src.Width;  x++)
        {
            byte* px  = sp + y * sData.Stride + x * 4;
            byte  gray = (byte)(px[2] * 0.299 + px[1] * 0.587 + px[0] * 0.114);
            byte  val  = gray < Threshold ? (byte)0 : (byte)255;
            byte* dp  = tp + y * tData.Stride + x * 4;
            dp[0] = val; dp[1] = val; dp[2] = val; dp[3] = 255;
        }

        src.UnlockBits(sData);
        thresholded.UnlockBits(tData);

        // Resize to 64×64
        var out64 = new Bitmap(OutSize, OutSize, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(out64);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(thresholded, 0, 0, OutSize, OutSize);
        thresholded.Dispose();

        return out64;
    }

    // ── Template management ───────────────────────────────────────────────────

    public static Dictionary<char, Bitmap> LoadTemplates()
    {
        var result = new Dictionary<char, Bitmap>();
        string dir = HimlabConfig.TemplatesDir;

        foreach (char c in Letters)
        {
            string path = Path.Combine(dir, $"{c}.png");
            if (!File.Exists(path)) continue;

            // Load and normalize to 64×64 Format32bppArgb
            using var raw = new Bitmap(path);
            var normalized = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(normalized);
            g.DrawImage(raw, 0, 0, 64, 64);
            result[c] = normalized;
        }

        return result;
    }

    public static void SaveTemplate(char letter, Bitmap processed64)
    {
        Directory.CreateDirectory(HimlabConfig.TemplatesDir);
        string path = Path.Combine(HimlabConfig.TemplatesDir, $"{letter}.png");
        processed64.Save(path, ImageFormat.Png);
    }

    // ── Matching ──────────────────────────────────────────────────────────────

    // Returns best matching letter and SSD score (lower = better).
    // If no templates loaded → returns ('?', double.MaxValue).
    public static (char letter, double ssd) MatchLetter(
        Bitmap processed64, Dictionary<char, Bitmap> templates)
    {
        char   best    = '?';
        double bestSsd = double.MaxValue;

        foreach (var (c, tmpl) in templates)
        {
            double ssd = CalcSSD(processed64, tmpl);
            if (ssd < bestSsd) { bestSsd = ssd; best = c; }
        }

        return (best, bestSsd);
    }

    // Sum of squared differences on the blue channel (all channels identical after preprocess)
    static unsafe double CalcSSD(Bitmap a, Bitmap b)
    {
        var aData = a.LockBits(
            new Rectangle(0, 0, 64, 64), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bData = b.LockBits(
            new Rectangle(0, 0, 64, 64), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte* ap = (byte*)aData.Scan0;
        byte* bp = (byte*)bData.Scan0;
        double sum = 0;

        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            double diff = ap[y * aData.Stride + x * 4] - bp[y * bData.Stride + x * 4];
            sum += diff * diff;
        }

        a.UnlockBits(aData);
        b.UnlockBits(bData);
        return sum;
    }

    // Max possible SSD for a 64×64 binary image (all pixels differ) — used for confidence %
    public static double MaxSSD => 64.0 * 64.0 * 255.0 * 255.0;
}
