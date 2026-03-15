using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace ScriptedWpf.Cogs.Hlorka;

static class HlorkaScanner
{
    // ── Шлях до еталонного зображення ────────────────────────────────────────

    static string TemplatePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "hlorka", "template.png");

    // ── Еталон: збереження / завантаження ────────────────────────────────────

    /// <summary>
    /// Зберігає поточний кадр як еталон міні-гри (grayscale 32×32).
    /// </summary>
    public static void SaveTemplate(Bitmap bmp)
    {
        using var small = Resize(bmp, 32, 32);
        using var gray  = ToGrayscale(small);
        Directory.CreateDirectory(Path.GetDirectoryName(TemplatePath)!);
        gray.Save(TemplatePath, ImageFormat.Png);
    }

    /// <summary>
    /// Завантажує збережений еталон або null якщо його немає.
    /// </summary>
    public static Bitmap? LoadTemplate()
    {
        if (!File.Exists(TemplatePath)) return null;
        try { return new Bitmap(TemplatePath); } catch { return null; }
    }

    public static bool HasTemplate() => File.Exists(TemplatePath);

    // ── Перевірка присутності міні-гри ────────────────────────────────────────

    /// <summary>
    /// Порівнює поточний кадр з еталоном через MAD (mean absolute difference)
    /// на зменшеному grayscale зображенні.
    /// Чим менше MAD — тим схожіший кадр на еталон.
    /// Threshold ~50: якщо MAD < threshold → міні-гра видна.
    /// </summary>
    public static double GetMatchScore(Bitmap current, Bitmap template)
    {
        using var small = Resize(current, 32, 32);
        using var gray  = ToGrayscale(small);
        return ComputeMAD(gray, template);
    }

    public static bool MatchesTemplate(Bitmap current, Bitmap template, int threshold)
    {
        return GetMatchScore(current, template) < threshold;
    }

    // ── Детектори кульки та лінії ─────────────────────────────────────────────

    /// <summary>
    /// Знаходить Y-центр зеленої кульки.
    /// Критерій: G домінує над R та B з абсолютним відступом (стійко до glow).
    /// </summary>
    public static unsafe int? FindBallY(Bitmap bmp)
    {
        long sumY = 0, count = 0;

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte* s = (byte*)data.Scan0;
        for (int y = 0; y < bmp.Height; y++)
        {
            byte* row = s + y * data.Stride;
            for (int x = 0; x < bmp.Width; x++)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                if (g >= 90 && (g - r) >= 40 && (g - b) >= 40)
                { sumY += y; count++; }
            }
        }

        bmp.UnlockBits(data);
        return count >= 5 ? (int)(sumY / count) : null;
    }

    /// <summary>
    /// Знаходить Y-центр білої горизонтальної лінії за найдовшою
    /// безперервною смугою білих пікселів (≥30% ширини зони).
    /// </summary>
    public static unsafe int? FindLineY(Bitmap bmp)
    {
        int bestRow = -1, bestRun = 0;
        int minRun  = Math.Max(3, bmp.Width * 30 / 100);

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte* s = (byte*)data.Scan0;
        for (int y = 0; y < bmp.Height; y++)
        {
            byte* row = s + y * data.Stride;
            int curRun = 0, maxRun = 0;
            for (int x = 0; x < bmp.Width; x++)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                bool isWhite = r > 185 && g > 185 && b > 185
                               && Math.Abs(r - g) < 35 && Math.Abs(r - b) < 35;
                if (isWhite) { curRun++; if (curRun > maxRun) maxRun = curRun; }
                else curRun = 0;
            }
            if (maxRun > bestRun) { bestRun = maxRun; bestRow = y; }
        }

        bmp.UnlockBits(data);
        return bestRun >= minRun ? bestRow : null;
    }

    // ── Debug ─────────────────────────────────────────────────────────────────

    public static string SaveDebug(Bitmap bmp)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        bmp.Save(Path.Combine(desktop, "hlorka_raw.png"), ImageFormat.Png);

        var debug = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        int maxG = 0, maxGr = 0, maxGb = 0, greenPx = 0, whitePx = 0;

        var src = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dst = debug.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* sp = (byte*)src.Scan0, dp = (byte*)dst.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* sr = sp + y * src.Stride, dr = dp + y * dst.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = sr[x*4], g = sr[x*4+1], r = sr[x*4+2];
                    if (g > maxG) { maxG = g; maxGr = r; maxGb = b; }
                    bool isG = g >= 90 && (g - r) >= 40 && (g - b) >= 40;
                    bool isW = r > 185 && g > 185 && b > 185 && Math.Abs(r-g)<35 && Math.Abs(r-b)<35;
                    if      (isG) { dr[x*4]=0;   dr[x*4+1]=0;   dr[x*4+2]=255; dr[x*4+3]=255; greenPx++; }
                    else if (isW) { dr[x*4]=255; dr[x*4+1]=0;   dr[x*4+2]=0;   dr[x*4+3]=255; whitePx++; }
                    else          { dr[x*4]=sr[x*4]; dr[x*4+1]=sr[x*4+1]; dr[x*4+2]=sr[x*4+2]; dr[x*4+3]=255; }
                }
            }
        }

        bmp.UnlockBits(src); debug.UnlockBits(dst);
        debug.Save(Path.Combine(desktop, "hlorka_debug.png"), ImageFormat.Png);
        debug.Dispose();

        return $"Розмір: {bmp.Width}×{bmp.Height}px | G-макс: R={maxGr} G={maxG} B={maxGb} | Зел: {greenPx} | Білих: {whitePx}";
    }

    // ── Допоміжні методи ─────────────────────────────────────────────────────

    static Bitmap Resize(Bitmap src, int w, int h)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    static unsafe Bitmap ToGrayscale(Bitmap src)
    {
        var dst  = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte* sp = (byte*)sData.Scan0, dp = (byte*)dData.Scan0;
        for (int y = 0; y < src.Height; y++)
        {
            byte* sr = sp + y * sData.Stride, dr = dp + y * dData.Stride;
            for (int x = 0; x < src.Width; x++)
            {
                byte gray = (byte)(sr[x*4+2] * 0.299 + sr[x*4+1] * 0.587 + sr[x*4] * 0.114);
                dr[x*4] = gray; dr[x*4+1] = gray; dr[x*4+2] = gray; dr[x*4+3] = 255;
            }
        }

        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }

    static unsafe double ComputeMAD(Bitmap a, Bitmap b)
    {
        var aData = a.LockBits(new Rectangle(0, 0, a.Width, a.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bData = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        byte* ap = (byte*)aData.Scan0, bp = (byte*)bData.Scan0;
        long sum = 0;
        int  n   = a.Width * a.Height;

        for (int y = 0; y < a.Height; y++)
        {
            byte* ar = ap + y * aData.Stride, br = bp + y * bData.Stride;
            for (int x = 0; x < a.Width; x++)
                sum += Math.Abs(ar[x*4] - br[x*4]); // grayscale: всі канали однакові
        }

        a.UnlockBits(aData); b.UnlockBits(bData);
        return (double)sum / n;
    }
}
