using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using ScriptedWpf.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

using SysPoint     = System.Drawing.Point;
using SysRect      = System.Drawing.Rectangle;
using SysBitmap    = System.Drawing.Bitmap;
using SysGraphics  = System.Drawing.Graphics;

namespace ScriptedWpf.Cogs.Cda;

// ── Screen Capture ────────────────────────────────────────────────────────────
static class ScreenCapture
{
    public static SysBitmap Capture(SysRect r)
    {
        var bmp = new SysBitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = SysGraphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, SysPoint.Empty, r.Size);
        return bmp;
    }

    // For Windows OCR code detection
    public static unsafe SysBitmap Preprocess(SysBitmap src)
    {
        var dst   = new SysBitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new SysRect(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new SysRect(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int y = 0; y < src.Height; y++)
        {
            byte* sRow = s + y * sData.Stride, dRow = d + y * dData.Stride;
            for (int x = 0; x < src.Width; x++)
            {
                int b = sRow[x * 4], g = sRow[x * 4 + 1], r = sRow[x * 4 + 2];
                int gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                gray = Math.Clamp((int)((gray - 128) * 3.0 + 128), 0, 255);
                byte bw = gray < 128 ? (byte)0 : (byte)255;
                dRow[x * 4] = bw; dRow[x * 4 + 1] = bw; dRow[x * 4 + 2] = bw; dRow[x * 4 + 3] = 255;
            }
        }
        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }
}

// ── Levenshtein ───────────────────────────────────────────────────────────────
static class Levenshtein
{
    public static int Distance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i-1] == b[j-1] ? d[i-1, j-1] : 1 + Math.Min(d[i-1, j-1], Math.Min(d[i-1, j], d[i, j-1]));
        return d[a.Length, b.Length];
    }
}

// ── Windows OCR — лише для 6-значного коду ────────────────────────────────────
static class WinOcr
{
    static OcrEngine? _engine;
    static OcrEngine Engine => _engine ??= CreateEngine();

    static OcrEngine CreateEngine()
    {
        foreach (var tag in new[] { "uk-UA", "ru", "ru-RU" })
        {
            var e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));
            if (e != null) return e;
        }
        return OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))!;
    }

    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(SysBitmap bmp)
    {
        using var ms  = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        using var ras = new InMemoryRandomAccessStream();
        using var dw  = new DataWriter(ras.GetOutputStreamAt(0));
        dw.WriteBytes(ms.ToArray());
        await dw.StoreAsync();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    static SysBitmap Upscale(SysBitmap src, int scale)
    {
        var dst = new SysBitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = SysGraphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    public static async Task<(string? code, string raw)> FindCodeAsync(SysBitmap img)
    {
        using var processed = ScreenCapture.Preprocess(img);
        using var upscaled  = Upscale(processed, 3);
        using var soft      = await ToSoftwareBitmapAsync(upscaled);
        var result = await Engine.RecognizeAsync(soft);
        string raw = result.Text.Trim();
        var spaced = Regex.Match(raw, @"(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)");
        if (spaced.Success)
            return (string.Concat(spaced.Groups[1].Value, spaced.Groups[2].Value,
                                  spaced.Groups[3].Value, spaced.Groups[4].Value,
                                  spaced.Groups[5].Value, spaced.Groups[6].Value), raw);
        var digits = Regex.Replace(raw, @"[^\d]", "");
        return (digits.Length >= 6 ? digits[..6] : null, raw);
    }

    public static SysRect GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length) return screens[monitorIndex].Bounds;
        return Screen.PrimaryScreen!.Bounds;
    }
}

// ── PaddleOCR Helper ──────────────────────────────────────────────────────────
static class PaddleHelper
{
    // Both engines set atomically — або обидва готові, або жоден
    static (PaddleOcrAll eng, PaddleOcrAll cyr)? _engines;
    static readonly object _initLock  = new();
    static bool _initStarted;

    public static bool IsReady { get { lock (_initLock) return _engines.HasValue; } }

    public static void EnsureInit(Action<string> log)
    {
        lock (_initLock)
        {
            if (_initStarted) return;
            _initStarted = true;
        }
        Task.Run(() =>
        {
            try
            {
                log("[CDA] Завантаження PaddleOCR (~50 MB, кешується)...");
                var engModel = OnlineFullModels.EnglishV3.DownloadAsync().GetAwaiter().GetResult();
                var cyrModel = new OnlineFullModels(
                    OnlineDetectionModel.EnglishV3,
                    OnlineClassificationModel.ChineseMobileV2,
                    LocalDictOnlineRecognizationModel.CyrillicV3
                ).DownloadAsync().GetAwaiter().GetResult();

                var eng = new PaddleOcrAll(engModel) { AllowRotateDetection = false, Enable180Classification = false };
                var cyr = new PaddleOcrAll(cyrModel) { AllowRotateDetection = false, Enable180Classification = false };
                lock (_initLock) _engines = (eng, cyr);  // атомарно
                log("[CDA] PaddleOCR готовий.");
            }
            catch (Exception ex) { log($"[CDA] ❌ PaddleOCR init: {ex.Message}"); }
        });
    }

    public static Mat BitmapToMat(SysBitmap bmp)
    {
        var bd = bmp.LockBits(new SysRect(0, 0, bmp.Width, bmp.Height),
                     ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var bgra = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC4, bd.Scan0, bd.Stride);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Зчитує весь текст з bitmap. cyr=true → кириличний движок.</summary>
    public static string DetectText(SysBitmap bmp, bool cyr = false)
    {
        (PaddleOcrAll eng, PaddleOcrAll cyr)? engines;
        lock (_initLock) engines = _engines;
        if (!engines.HasValue) return "";
        using var mat = BitmapToMat(bmp);
        var engine = cyr ? engines.Value.cyr : engines.Value.eng;
        lock (engine)
        {
            var regions = engine.Run(mat).Regions;
            return string.Join(" ", regions.Select(r => r.Text));
        }
    }

    public static SysRect GetMonitorBounds(int monitorIndex)
        => WinOcr.GetMonitorBounds(monitorIndex);

    public static SysRect? FindDialogRegion(int monitorIndex = -1)
    {
        if (!IsReady) return null;

        // Stage 1: Fast color-based search for a candidate region
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var candidate = FindDialogRegionByColor(bmp);
        if (!candidate.HasValue) return null;

        // Stage 2: OCR confirmation on the small candidate region
        return FindDialogRegionWithOcr(bmp, candidate.Value);
    }
    
    static unsafe SysRect? FindDialogRegionByColor(SysBitmap bmp)
    {
        var data = bmp.LockBits(new SysRect(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rects = new List<SysRect>();
        var visited = new bool[bmp.Width, bmp.Height];
        byte* scan0 = (byte*)data.Scan0;

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                if (visited[x, y]) continue;
                byte* p = scan0 + y * data.Stride + x * 4;
                // Look for dark, semi-transparent pixels (typical for dialogs/overlays)
                if (p[3] > 100 && p[0] < 50 && p[1] < 50 && p[2] < 50)
                {
                    var newRect = FloodFill(scan0, data.Stride, visited, x, y, bmp.Width, bmp.Height);
                    if (newRect.Width * newRect.Height > 5000) // Filter small noise
                        rects.Add(newRect);
                }
            }
        }
        bmp.UnlockBits(data);

        if (rects.Count == 0) return null;

        // Return the largest found rectangle
        return rects.OrderByDescending(r => r.Width * r.Height).First();
    }
    
    static unsafe SysRect FloodFill(byte* scan0, int stride, bool[,] visited, int startX, int startY, int w, int h)
    {
        var q = new Queue<SysPoint>();
        q.Enqueue(new SysPoint(startX, startY));
        visited[startX, startY] = true;
        int minX = startX, maxX = startX, minY = startY, maxY = startY;

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);

            for (int i = -1; i <= 1; i++)
            {
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0) continue;
                    int nx = p.X + i, ny = p.Y + j;
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[nx, ny])
                    {
                        byte* ptr = scan0 + ny * stride + nx * 4;
                        if (ptr[3] > 100 && ptr[0] < 50 && ptr[1] < 50 && ptr[2] < 50)
                        {
                            visited[nx, ny] = true;
                            q.Enqueue(new SysPoint(nx, ny));
                        }
                    }
                }
            }
        }
        return new SysRect(minX, minY, maxX - minX, maxY - minY);
    }


    /// <summary>Шукає діалог "введіть код підтвердження" на екрані через PaddleOCR.</summary>
    static SysRect? FindDialogRegionWithOcr(SysBitmap bmp, SysRect? searchBounds = null)
    {
        if (!IsReady) return null;
        var screen = searchBounds ?? new SysRect(0, 0, bmp.Width, bmp.Height);

        (PaddleOcrAll eng, PaddleOcrAll cyr) engines;
        lock (_initLock) engines = _engines!.Value;
        using var mat = BitmapToMat(bmp.Clone(screen, bmp.PixelFormat));
        PaddleOcrResultRegion[] regions;
        lock (engines.cyr) regions = engines.cyr.Run(mat).Regions;

        foreach (var region in regions)
        {
            string text = region.Text.ToLowerInvariant();
            if ((text.Contains("код") && text.Contains("підтвердж")) ||
                (text.Contains("введіть") && text.Contains("код")))
            {
                var rect = region.Rect.BoundingRect();
                int w = 600;
                int x = Math.Max(screen.X, screen.X + rect.X + rect.Width / 2 - w / 2);
                int y = screen.Y + rect.Y + rect.Height + 5;
                return new SysRect(x, y, Math.Min(w, screen.Right - x), 60);
            }
        }
        return null;
    }
}

// ── Order Scanner ─────────────────────────────────────────────────────────────
static class OrderScanner
{
    public class OrderCard
    {
        public SysPoint Anchor;
        public int      PricePerKm;
        public double   Tonnage;
        public int      Level;
        public string   Type       = "";
        public SysPoint ClickPoint;
    }

    static readonly double[] ValidTons = { 0.5, 1.5, 3.0, 5.0 };

    /// <summary>Знаходить картки замовлень на екрані. isCancelled перевіряється між картками.</summary>
    public static List<OrderCard> FindCards(int monitorIndex, Func<bool>? isCancelled = null)
    {
        var screen  = WinOcr.GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var cards   = new List<OrderCard>();
        var anchors = FindGreenPriceAnchors(bmp);
        if (anchors.Count == 0) return cards;

        foreach (var anchor in anchors)
        {
            if (isCancelled?.Invoke() == true) break;

            int price = ReadPrice(bmp, anchor);
            if (isCancelled?.Invoke() == true) break;

            var (tonnage, level, orderType) = ReadBadge(bmp, anchor);

            // Кнопка "Натисніть, щоб переглянути" = anchor + ~130px вправо + ~50px вниз
            int btnX = screen.X + anchor.X + 130;
            int btnY = screen.Y + anchor.Y + 50;
            cards.Add(new OrderCard
            {
                Anchor     = anchor,
                PricePerKm = price,
                Tonnage    = tonnage,
                Level      = level,
                Type       = orderType,
                ClickPoint = new SysPoint(btnX, btnY)
            });
        }
        return cards;
    }

    static SysBitmap Upscale(SysBitmap src, int scale)
    {
        var dst = new SysBitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = SysGraphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // Anchor = перший зелений піксель основної ціни ($XX XXX)
    // per-km ціна (≈ $X/km.) знаходиться на ~25px нижче anchor
    static int ReadPrice(SysBitmap bmp, SysPoint anchor)
    {
        int x0 = Math.Max(0, anchor.X - 10);
        int y0 = anchor.Y + 25;
        if (y0 + 50 > bmp.Height) return 0;
        var r = new SysRect(x0, y0, Math.Min(280, bmp.Width - x0), 50);
        if (r.Width < 30) return 0;

        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var up   = Upscale(crop, 3);

        string txt = PaddleHelper.DetectText(up)
            .Replace(" ", "").Replace("O", "0").Replace("S", "5").Replace("s", "5");

        // Шукаємо цифри після знаку $
        int di = txt.IndexOf('$');
        if (di >= 0 && di < txt.Length - 1) txt = txt[(di + 1)..];
        var m = Regex.Match(txt, @"\d+");
        return m.Success && int.TryParse(m.Value, out int p) ? p : 0;
    }

    // Badges ([1.5 T] [1 LVL] [Нафта]) знаходяться вище anchor
    static (double ton, int lvl, string type) ReadBadge(SysBitmap bmp, SysPoint anchor)
    {
        // Anchor = основна ціна. Badges = приблизно на 55-90px вище
        int x0 = Math.Max(0, anchor.X - 10);
        int y0 = Math.Max(0, anchor.Y - 100);
        var r  = new SysRect(x0, y0, Math.Min(380, bmp.Width - x0), 85);
        if (r.Width < 50) return (0, 1, "Невідомо");

        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var up   = Upscale(crop, 3);

        // Англійський движок: тоннаж ("1.5 T") + рівень ("1 LVL")
        string eng = PaddleHelper.DetectText(up)
            .Replace("S", "5").Replace("s", "5").Replace("O", "0").Replace("o", "0")
            .ToUpperInvariant();

        double ton = 0;
        int    lvl = 1;

        // [TТ] — Latin T або Cyrillic Т (PaddleOCR може повернути будь-яке)
        var tm = Regex.Match(eng, @"(\d+[.,]?\d*)\s*[TТ]");
        if (tm.Success)
        {
            string raw = tm.Groups[1].Value.Replace(',', '.');
            double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out ton);
            // OCR міг загубити крапку: "15"→"1.5", "05"→"0.5"
            if (!ValidTons.Contains(ton) && raw.Length >= 2)
            {
                string cand = raw[..^1] + "." + raw[^1..];
                if (double.TryParse(cand, NumberStyles.Any, CultureInfo.InvariantCulture, out double c)
                    && ValidTons.Contains(c))
                    ton = c;
            }
        }

        var lm = Regex.Match(eng, @"(\d+)\s*LVL", RegexOptions.IgnoreCase);
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out lvl);

        // Кириличний движок: тип вантажу
        string ukr = PaddleHelper.DetectText(up, cyr: true).ToLowerInvariant();

        string type =
            ukr.Contains("одяг")    || ukr.Contains("модн")                                       ? "Одяг"          :
            ukr.Contains("продукт") || ukr.Contains("харч")                                       ? "Продукти"      :
            ukr.Contains("фарм")    || ukr.Contains("стерил")  || ukr.Contains("аптек")           ? "Фармацевтика"  :
            ukr.Contains("нафт")    || ukr.Contains("палив")                                      ? "Нафта"         :
            ukr.Contains("авто")    || ukr.Contains("обслуг")  || ukr.Contains("запчаст")         ? "Автозапчастини":
            ukr.Contains("різн")    || ukr.Contains("світ")    || ukr.Contains("мобіл")           ? "Різне"         :
            ukr.Contains("інш")     || ukr.Contains("спорядж") || ukr.Contains("тактичн")         ? "Інше"          :
            "Невідомо";

        return (ton, lvl, type);
    }

    // Колір основної ціни ($XX XXX) — yellow-green (222,237,131) area
    static List<SysPoint> FindGreenPriceAnchors(SysBitmap bmp)
    {
        var anchors = new List<SysPoint>();
        var sData   = bmp.LockBits(new SysRect(0, 0, bmp.Width, bmp.Height),
                                   ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* s = (byte*)sData.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = s + y * sData.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (r >= 205 && r <= 240 && g >= 220 && g <= 250 && b >= 115 && b <= 148)
                    {
                        bool isNew = true;
                        foreach (var p in anchors)
                            if (Math.Abs(p.X - x) < 200 && Math.Abs(p.Y - y) < 150) { isNew = false; break; }
                        if (isNew) anchors.Add(new SysPoint(x, y));
                    }
                }
            }
        }
        bmp.UnlockBits(sData);
        return anchors;
    }
}

// ── Keyboard Input ────────────────────────────────────────────────────────────
static class KeyInput
{
    static WinApi.INPUT Key(ushort vk, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = up ? WinApi.KEYEVENTF_KEYUP : 0 } }
    };

    public static void TypeCode(string code, bool turbo = true)
    {
        int size = Marshal.SizeOf<WinApi.INPUT>();
        if (turbo)
        {
            var inputs = new WinApi.INPUT[code.Length * 2];
            for (int i = 0; i < code.Length; i++)
            {
                ushort vk = (ushort)(0x30 + (code[i] - '0'));
                inputs[i * 2] = Key(vk, false); inputs[i * 2 + 1] = Key(vk, true);
            }
            WinApi.SendInput((uint)inputs.Length, inputs, size);
        }
        else
        {
            foreach (char c in code)
            {
                ushort vk = (ushort)(0x30 + (c - '0'));
                WinApi.SendInput(2, new[] { Key(vk, false), Key(vk, true) }, size);
                Thread.Sleep(20);
            }
        }
    }
}

// ── Mouse Input ───────────────────────────────────────────────────────────────
static class MouseInput
{
    public static void Click(int x, int y)
    {
        WinApi.SetCursorPos(x, y);
        Thread.Sleep(20);
        WinApi.mouse_event(0x0002, 0, 0, 0, 0);
        Thread.Sleep(20);
        WinApi.mouse_event(0x0004, 0, 0, 0, 0);
    }
}
