using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tesseract;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Cda;

// ── Screen Capture ────────────────────────────────────────────────────────────
static class ScreenCapture
{
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    public static unsafe Bitmap Preprocess(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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

// ── Windows OCR ───────────────────────────────────────────────────────────────
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

    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
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

    static Bitmap Upscale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    public static async Task<(string? code, string raw)> FindCodeAsync(Bitmap img)
    {
        using var processed = ScreenCapture.Preprocess(img);
        using var upscaled  = Upscale(processed, 3);
        using var soft      = await ToSoftwareBitmapAsync(upscaled);
        var result = await Engine.RecognizeAsync(soft);
        string raw = result.Text.Trim();
        // спочатку шукаємо 6 цифр з довільними пробілами між ними
        var spaced = Regex.Match(raw, @"(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)");
        if (spaced.Success)
            return (string.Concat(spaced.Groups[1].Value, spaced.Groups[2].Value,
                                  spaced.Groups[3].Value, spaced.Groups[4].Value,
                                  spaced.Groups[5].Value, spaced.Groups[6].Value), raw);
        // fallback: витягуємо всі цифри і беремо перші 6
        var digits = Regex.Replace(raw, @"[^\d]", "");
        return (digits.Length >= 6 ? digits[..6] : null, raw);
    }

    public static string TessDataPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath!) ?? AppDomain.CurrentDomain.BaseDirectory, "tessdata");

    static readonly string[] RequiredLangs = { "ukr", "eng" };
    const string TessdataBaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";

    public static void EnsureTessdata(Action<string> log)
    {
        Directory.CreateDirectory(TessDataPath);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        foreach (var lang in RequiredLangs)
        {
            var path = Path.Combine(TessDataPath, $"{lang}.traineddata");
            if (File.Exists(path)) continue;
            log($"Завантаження {lang}.traineddata...");
            try
            {
                var bytes = http.GetByteArrayAsync(TessdataBaseUrl + $"{lang}.traineddata").GetAwaiter().GetResult();
                File.WriteAllBytes(path, bytes);
                log($"{lang}.traineddata завантажено.");
            }
            catch (Exception ex)
            {
                log($"[ПОМИЛКА] Не вдалося завантажити {lang}.traineddata: {ex.Message}");
            }
        }
    }

    public static Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length) return screens[monitorIndex].Bounds;
        return Screen.PrimaryScreen!.Bounds;
    }

    static unsafe Bitmap EnhanceContrast(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++)
        {
            int max = Math.Max(s[i * 4], Math.Max(s[i * 4 + 1], s[i * 4 + 2]));
            byte bw = max < 20 ? (byte)255 : (byte)0;
            d[i * 4] = bw; d[i * 4 + 1] = bw; d[i * 4 + 2] = bw; d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }

    static List<(string norm, Tesseract.Rect bounds)> ScanLines(TesseractEngine engine, Bitmap bmp, bool enhance)
    {
        Bitmap processed = enhance ? EnhanceContrast(bmp) : bmp;
        try
        {
            using var ms   = new MemoryStream();
            processed.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position = 0;
            using var pix  = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix, PageSegMode.Auto);
            using var iter = page.GetIterator();
            var lines = new List<(string, Tesseract.Rect)>();
            iter.Begin();
            do
            {
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) continue;
                var norm = Regex.Replace((iter.GetText(PageIteratorLevel.TextLine) ?? "").ToLowerInvariant(), @"[^\w\s]", " ").Trim();
                if (norm.Length >= 5) lines.Add((Regex.Replace(norm, @"\s+", " "), bounds));
            } while (iter.Next(PageIteratorLevel.TextLine));
            return lines;
        }
        finally { if (enhance) processed.Dispose(); }
    }

    public static Rectangle? FindDialogRegion(int monitorIndex = -1)
    {
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp    = ScreenCapture.Capture(screen);
        using var engine = new TesseractEngine(TessDataPath, "ukr", EngineMode.Default);
        var linesNormal   = ScanLines(engine, bmp, enhance: false);
        var linesInverted = ScanLines(engine, bmp, enhance: true);
        string target = "введіть код підтвердження щоб взяти замовлення";

        (int dist, Tesseract.Rect bounds) FindBest(List<(string norm, Tesseract.Rect bounds)> lines)
        {
            int bestDist = int.MaxValue; Tesseract.Rect bestBounds = default;
            lines.Sort((a, b) => a.bounds.Y1.CompareTo(b.bounds.Y1));
            for (int i = 0; i < lines.Count; i++)
                for (int len = 1; len <= 2 && i + len - 1 < lines.Count; len++)
                {
                    var parts = new string[len];
                    for (int k = 0; k < len; k++) parts[k] = lines[i + k].norm;
                    string comb = string.Join(" ", parts);
                    if (comb.Length < 10 || (!comb.Contains("код") && !comb.Contains("підтверд") && !comb.Contains("введіть"))) continue;
                    int dist = Levenshtein.Distance(Regex.Replace(comb, @"\d+", "").Trim(), target);
                    if (dist < bestDist)
                    {
                        bestDist   = dist;
                        bestBounds = new Tesseract.Rect(
                            Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1),
                            lines[i].bounds.Y1,
                            Math.Max(lines[i].bounds.X2, lines[i + len - 1].bounds.X2) - Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1),
                            lines[i + len - 1].bounds.Y2 - lines[i].bounds.Y1);
                    }
                }
            return (bestDist, bestBounds);
        }

        var m1 = FindBest(linesNormal);
        var m2 = FindBest(linesInverted);
        var best = m1.dist < m2.dist ? m1 : m2;
        if (best.dist > 22) return null;

        int w = 600, x = (bmp.Width - w) / 2, y = best.bounds.Y1 + best.bounds.Height, h = 60;
        float sx = (float)bmp.Width / screen.Width, sy = (float)bmp.Height / screen.Height;
        return new Rectangle(screen.X + (int)(x / sx), screen.Y + (int)(y / sy), (int)(w / sx), (int)(h / sy));
    }
}

// ── Order Scanner ─────────────────────────────────────────────────────────────
static class OrderScanner
{
    public class OrderCard
    {
        public Point Anchor; public int PricePerKm; public double Tonnage;
        public int Level; public string Type = ""; public Point ClickPoint;
    }

    public static List<OrderCard> FindCards(int monitorIndex)
    {
        var screen  = WinOcr.GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var cards   = new List<OrderCard>();
        var anchors = FindGreenPriceAnchors(bmp);
        if (anchors.Count == 0) return cards;

        using var badgeEng = new TesseractEngine(WinOcr.TessDataPath, "eng", EngineMode.Default);
        badgeEng.SetVariable("tessedit_char_whitelist", "0123456789.tTlLvVsS");
        using var priceEng = new TesseractEngine(WinOcr.TessDataPath, "eng", EngineMode.Default);
        priceEng.SetVariable("tessedit_char_whitelist", "0123456789$/km.≈ ");
        using var ukrEng = new TesseractEngine(WinOcr.TessDataPath, "ukr", EngineMode.Default);

        foreach (var anchor in anchors)
        {
            int price = ReadPrice(bmp, anchor, priceEng);
            var (tonnage, level, orderType) = ReadBadge(bmp, anchor, badgeEng, ukrEng);
            int btnX = screen.X + anchor.X + 130, btnY = screen.Y + anchor.Y + 50;
            cards.Add(new OrderCard { Anchor = anchor, PricePerKm = price, Tonnage = tonnage, Level = level, Type = orderType, ClickPoint = new Point(btnX, btnY) });
        }
        return cards;
    }

    static int ReadPrice(Bitmap bmp, Point anchor, TesseractEngine eng)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y + 25, 240, 40);
        if (r.Right > bmp.Width || r.Bottom > bmp.Height || r.X < 0) return 0;
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var filt = EnhanceGreyText(crop);
        using var up   = Upscale(filt, 3);
        string txt = DoOcr(eng, up).Replace(" ", "").Replace("O", "0").Replace("S", "5").Replace("s", "5");
        int di = txt.IndexOf('$');
        if (di >= 0 && di < txt.Length - 1) txt = txt[(di + 1)..];
        var m = Regex.Match(txt, @"\d+");
        return m.Success && int.TryParse(m.Value, out int p) ? p : 0;
    }

    static (double ton, int lvl, string type) ReadBadge(Bitmap bmp, Point anchor, TesseractEngine badgeEng, TesseractEngine ukrEng)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y - 55, 320, 45);
        if (r.Right > bmp.Width || r.Y < 0 || r.X < 0) return (0, 1, "Невідомо");
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var filt = EnhanceBadgeText(crop);
        using var up   = Upscale(filt, 3);

        string eng = DoOcr(badgeEng, up).Replace("S","5").Replace("s","5").Replace("O","0").Replace("o","0").ToUpper();
        double ton = 0; int lvl = 1;
        var tm = Regex.Match(eng, @"(\d+[.,]?\d*)\s*[tT]");
        if (tm.Success) double.TryParse(tm.Groups[1].Value.Replace(',','.'), NumberStyles.Any, CultureInfo.InvariantCulture, out ton);
        var lm = Regex.Match(eng, @"(\d+)\s*[lL]");
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out lvl);

        string ukr = DoOcr(ukrEng, up).ToLower();
        string type =
            ukr.Contains("одяг")   || ukr.Contains("модн")                                        ? "Одяг"          :
            ukr.Contains("продукт")|| ukr.Contains("харч")                                        ? "Продукти"      :
            ukr.Contains("фарм")   || ukr.Contains("стерил") || ukr.Contains("аптек")             ? "Фармацевтика"  :
            ukr.Contains("нафт")   || ukr.Contains("палив")                                       ? "Нафта"         :
            ukr.Contains("авто")   || ukr.Contains("обслуг") || ukr.Contains("запчаст")           ? "Автозапчастини":
            ukr.Contains("різн")   || ukr.Contains("світ")   || ukr.Contains("мобіл")             ? "Різне"         :
            ukr.Contains("інш")    || ukr.Contains("спорядж")|| ukr.Contains("тактичн")           ? "Інше"          :
            "Невідомо";

        return (ton, lvl, type);
    }

    static string DoOcr(TesseractEngine engine, Bitmap bmp)
    {
        using var ms   = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position = 0;
        using var pix  = Pix.LoadFromMemory(ms.ToArray());
        using var page = engine.Process(pix, PageSegMode.SingleLine);
        return page.GetText() ?? "";
    }

    static List<Point> FindGreenPriceAnchors(Bitmap bmp)
    {
        var anchors = new List<Point>();
        var sData   = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* s = (byte*)sData.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = s + y * sData.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (r > 200 && g > 220 && b >= 100 && b <= 180 && r > b && g > b)
                    {
                        bool isNew = true;
                        foreach (var p in anchors) if (Math.Abs(p.X - x) < 200 && Math.Abs(p.Y - y) < 150) { isNew = false; break; }
                        if (isNew) anchors.Add(new Point(x, y));
                    }
                }
            }
        }
        bmp.UnlockBits(sData);
        return anchors;
    }

    static unsafe Bitmap EnhanceGreyText(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++)
        {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            bool isText = r > 150 && g > 150 && b > 150 && Math.Abs(r - g) < 25 && Math.Abs(g - b) < 25;
            d[i * 4] = isText ? (byte)0 : (byte)255; d[i * 4 + 1] = d[i * 4]; d[i * 4 + 2] = d[i * 4]; d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }

    static unsafe Bitmap EnhanceBadgeText(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++)
        {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            bool isText = (b > 160 && r < 140 && g < 180) || (r > 150 && g > 110 && b < 140) || (r > 180 && g > 180 && b > 180);
            d[i * 4] = isText ? (byte)0 : (byte)255; d[i * 4 + 1] = d[i * 4]; d[i * 4 + 2] = d[i * 4]; d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }

    static Bitmap Upscale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
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

    public static void TypeCode(string code, bool turbo)
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
