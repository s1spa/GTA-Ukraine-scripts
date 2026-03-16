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
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(TemplatePath));
            return new Bitmap(ms);
        }
        catch { return null; }
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

    // ── Ball.png template ─────────────────────────────────────────────────────

    static string BallPath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "hlorka", "Ball.png");

    /// <summary>
    /// Завантажує Ball.png і масштабує до заданого розміру.
    /// </summary>
    public static Bitmap? LoadBallTemplate(int size = 24)
    {
        if (!File.Exists(BallPath)) return null;
        try
        {
            using var ms  = new MemoryStream(File.ReadAllBytes(BallPath));
            using var src = new Bitmap(ms);
            return Resize(src, size, size);
        }
        catch { return null; }
    }

    /// <summary>
    /// Шукає кульку (Ball.png) у кадрі через SAD по непрозорих пікселях.
    /// Повертає Y-центр найкращого збігу, або null якщо SAD/піксель >= threshold.
    /// threshold ≈ 40 (0 = ідеальний збіг, 255 = повна відмінність).
    /// </summary>
    public static unsafe int? FindBallByTemplate(Bitmap frame, Bitmap ball, int threshold = 40)
    {
        int fw = frame.Width, fh = frame.Height;
        int bw = ball.Width,  bh = ball.Height;
        if (bw > fw || bh > fh) return null;

        var fData = frame.LockBits(new Rectangle(0,0,fw,fh), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var bData = ball.LockBits( new Rectangle(0,0,bw,bh), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        byte* fp = (byte*)fData.Scan0, bp = (byte*)bData.Scan0;

        // Рахуємо непрозорі пікселі кульки (круг на прозорому фоні)
        int pixCount = 0;
        for (int by = 0; by < bh; by++) {
            byte* brow = bp + by * bData.Stride;
            for (int bx = 0; bx < bw; bx++)
                if (brow[bx*4+3] > 64) pixCount++;
        }

        int  bestY   = -1;
        long bestSad = long.MaxValue;

        if (pixCount > 0)
        {
            for (int fy = 0; fy <= fh - bh; fy += 2) // крок 2 для швидкості
            {
                for (int fx = 0; fx <= fw - bw; fx++)
                {
                    long sad = 0;
                    for (int by = 0; by < bh; by++)
                    {
                        byte* frow = fp + (fy+by) * fData.Stride;
                        byte* brow = bp + by     * bData.Stride;
                        for (int bx = 0; bx < bw; bx++)
                        {
                            if (brow[bx*4+3] <= 64) continue;
                            int fi = (fx+bx)*4;
                            sad += Math.Abs(frow[fi]   - brow[bx*4]);
                            sad += Math.Abs(frow[fi+1] - brow[bx*4+1]);
                            sad += Math.Abs(frow[fi+2] - brow[bx*4+2]);
                            if (sad >= bestSad) goto skipPos;
                        }
                    }
                    bestSad = sad; bestY = fy;
                    skipPos:;
                }
            }
        }

        frame.UnlockBits(fData);
        ball.UnlockBits(bData);

        if (bestY < 0) return null;
        return bestSad / pixCount / 3 < threshold ? bestY + bh / 2 : null;
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
