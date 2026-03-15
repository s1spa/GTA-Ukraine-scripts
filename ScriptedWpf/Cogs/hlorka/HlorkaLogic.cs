using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ScriptedWpf.Cogs.Hlorka;

/// <summary>
/// Аналізує знімок планки міні-гри та повертає Y-координати
/// зеленої кульки і білої лінії всередині захопленого регіону.
/// </summary>
static class HlorkaScanner
{
    /// <summary>
    /// Знаходить Y-центр зеленої кульки.
    /// Критерій: G домінує над R та B з абсолютним відступом (стійко до glow).
    /// </summary>
    public static int? FindBallY(Bitmap bmp)
    {
        long sumY = 0, count = 0;

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* s = (byte*)data.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = s + y * data.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 4];
                    int g = row[x * 4 + 1];
                    int r = row[x * 4 + 2];

                    // G достатньо яскравий і виразно перевищує R та B
                    if (g >= 90 && (g - r) >= 40 && (g - b) >= 40)
                    {
                        sumY += y;
                        count++;
                    }
                }
            }
        }

        bmp.UnlockBits(data);

        return count >= 5 ? (int)(sumY / count) : null;
    }

    /// <summary>
    /// Знаходить Y-центр білої горизонтальної лінії.
    /// Шукає рядок з НАЙДОВШОЮ безперервною горизонтальною смугою білих пікселів
    /// (відрізняє справжню лінію від тексту "SPACE" або "41%").
    /// Лінія має займати мінімум 30% ширини зони сканування.
    /// </summary>
    public static int? FindLineY(Bitmap bmp)
    {
        int bestRow = -1, bestRun = 0;
        int minRun  = Math.Max(3, bmp.Width * 30 / 100); // 30% ширини

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* s = (byte*)data.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = s + y * data.Stride;
                int curRun = 0, maxRun = 0;

                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 4];
                    int g = row[x * 4 + 1];
                    int r = row[x * 4 + 2];

                    bool isWhite = r > 185 && g > 185 && b > 185
                                   && Math.Abs(r - g) < 35
                                   && Math.Abs(r - b) < 35;

                    if (isWhite) { curRun++; if (curRun > maxRun) maxRun = curRun; }
                    else curRun = 0;
                }

                if (maxRun > bestRun) { bestRun = maxRun; bestRow = y; }
            }
        }

        bmp.UnlockBits(data);

        return bestRun >= minRun ? bestRow : null;
    }

    /// <summary>
    /// Зберігає debug-зображення на робочий стіл:
    ///   hlorka_raw.png   — що саме захоплено
    ///   hlorka_debug.png — зелені пікселі підсвічені червоним, білі — синім
    /// Повертає рядок з найяскравішим зеленим пікселем для діагностики.
    /// </summary>
    public static string SaveDebug(Bitmap bmp)
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        // Зберігаємо raw
        bmp.Save(Path.Combine(desktop, "hlorka_raw.png"), ImageFormat.Png);

        // Збираємо діагностику і малюємо debug
        var debug   = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format32bppArgb);
        int maxG    = 0, maxGr = 0, maxGb = 0;
        int greenPx = 0, whitePx = 0;

        var src  = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dst  = debug.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        unsafe
        {
            byte* s = (byte*)src.Scan0;
            byte* d = (byte*)dst.Scan0;

            for (int y = 0; y < bmp.Height; y++)
            {
                byte* sRow = s + y * src.Stride;
                byte* dRow = d + y * dst.Stride;

                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = sRow[x * 4];
                    int g = sRow[x * 4 + 1];
                    int r = sRow[x * 4 + 2];

                    // Відстежуємо найяскравіший G
                    if (g > maxG) { maxG = g; maxGr = r; maxGb = b; }

                    bool isGreen = g >= 90 && (g - r) >= 40 && (g - b) >= 40;
                    bool isWhite = r > 185 && g > 185 && b > 185
                                   && Math.Abs(r - g) < 35 && Math.Abs(r - b) < 35;

                    if (isGreen) { greenPx++; dRow[x * 4] = 0; dRow[x * 4 + 1] = 0; dRow[x * 4 + 2] = 255; dRow[x * 4 + 3] = 255; } // червоний
                    else if (isWhite) { whitePx++; dRow[x * 4] = 255; dRow[x * 4 + 1] = 0; dRow[x * 4 + 2] = 0; dRow[x * 4 + 3] = 255; } // синій
                    else { dRow[x * 4] = sRow[x * 4]; dRow[x * 4 + 1] = sRow[x * 4 + 1]; dRow[x * 4 + 2] = sRow[x * 4 + 2]; dRow[x * 4 + 3] = 255; }
                }
            }
        }

        bmp.UnlockBits(src);
        debug.UnlockBits(dst);
        debug.Save(Path.Combine(desktop, "hlorka_debug.png"), ImageFormat.Png);
        debug.Dispose();

        return $"Розмір зони: {bmp.Width}×{bmp.Height}px | " +
               $"Найяскравіший G-піксель: R={maxGr} G={maxG} B={maxGb} | " +
               $"Зелених пікселів: {greenPx} | Білих: {whitePx}";
    }
}
