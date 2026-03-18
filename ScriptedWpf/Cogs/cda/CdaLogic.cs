using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using SysImgFmt = System.Drawing.Imaging.ImageFormat;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tesseract;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.Cda;

// ── Screen Capture (shared — used by other modules: hlorka, wires) ────────────
public static class ScreenCapture
{
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }
}

// ── Code detection (Windows OCR, no extra dependencies) ──────────────────────
static class CdaLogic
{
    const int MARGIN = 20;

    static readonly OcrEngine _ocr =
        OcrEngine.TryCreateFromUserProfileLanguages()
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
        ?? throw new Exception("Windows OCR недоступний");

    public static Rectangle GetRegion(CdaConfig cfg) => new(
        cfg.X1 - MARGIN, cfg.Y1 - MARGIN,
        (cfg.X2 - cfg.X1) + MARGIN * 2,
        (cfg.Y2 - cfg.Y1) + MARGIN * 2);

    public static unsafe Bitmap Preprocess(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                        ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte* s = (byte*)sData.Scan0;
        byte* d = (byte*)dData.Scan0;

        for (int y = 0; y < src.Height; y++)
        {
            byte* sRow = s + y * sData.Stride;
            byte* dRow = d + y * dData.Stride;
            for (int x = 0; x < src.Width; x++)
            {
                int b = sRow[x * 4], g = sRow[x * 4 + 1], r = sRow[x * 4 + 2];
                int gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                gray = Math.Clamp((int)((gray - 128) * 3.0 + 128), 0, 255);
                byte bw = gray < 128 ? (byte)0 : (byte)255;
                dRow[x * 4]     = bw;
                dRow[x * 4 + 1] = bw;
                dRow[x * 4 + 2] = bw;
                dRow[x * 4 + 3] = 255;
            }
        }

        src.UnlockBits(sData);
        dst.UnlockBits(dData);
        return dst;
    }

    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms  = new MemoryStream();
        bmp.Save(ms, SysImgFmt.Png);
        ms.Position = 0;

        using var ras = new InMemoryRandomAccessStream();
        using var dw  = new DataWriter(ras.GetOutputStreamAt(0));
        dw.WriteBytes(ms.ToArray());
        await dw.StoreAsync();

        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public static async Task<string?> FindCodeAsync(Bitmap img)
    {
        using var processed = Preprocess(img);
        using var upscaled  = Upscale(processed, 3);
        using var soft      = await ToSoftwareBitmapAsync(upscaled);

        var result = await _ocr.RecognizeAsync(soft);
        var text   = result.Text;

        var spaced = Regex.Match(text, @"(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)\s*(\d)");
        if (spaced.Success)
            return string.Concat(
                spaced.Groups[1].Value, spaced.Groups[2].Value,
                spaced.Groups[3].Value, spaced.Groups[4].Value,
                spaced.Groups[5].Value, spaced.Groups[6].Value);

        var digits = Regex.Replace(text, @"[^\d]", "");
        return digits.Length >= 6 ? digits[..6] : null;
    }

    static Bitmap Upscale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // ── Keyboard input ────────────────────────────────────────────────────────

    public static void TypeCode(string code, bool turbo)
    {
        int sz = Marshal.SizeOf<WinApi.INPUT>();

        if (turbo)
        {
            var inputs = new WinApi.INPUT[code.Length * 2];
            for (int i = 0; i < code.Length; i++)
            {
                ushort vk = (ushort)(0x30 + (code[i] - '0'));
                inputs[i * 2]     = MakeKey(vk, false);
                inputs[i * 2 + 1] = MakeKey(vk, true);
            }
            WinApi.SendInput((uint)inputs.Length, inputs, sz);
        }
        else
        {
            foreach (char c in code)
            {
                ushort vk = (ushort)(0x30 + (c - '0'));
                WinApi.SendInput(2, [MakeKey(vk, false), MakeKey(vk, true)], sz);
                Thread.Sleep(20);
            }
        }
    }

    static WinApi.INPUT MakeKey(ushort vk, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = up ? WinApi.KEYEVENTF_KEYUP : 0 } }
    };
}

// ── Levenshtein distance ──────────────────────────────────────────────────────
static class Levenshtein
{
    public static int Distance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i - 1] == b[j - 1]
                    ? d[i - 1, j - 1]
                    : 1 + Math.Min(d[i - 1, j - 1], Math.Min(d[i - 1, j], d[i, j - 1]));
        return d[a.Length, b.Length];
    }
}

// ── Tesseract utilities ───────────────────────────────────────────────────────
static class TessOcr
{
    public static string TessDataPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath!) ?? AppDomain.CurrentDomain.BaseDirectory,
        "tessdata");

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
            log($"[CDA] Завантаження {lang}.traineddata...");
            try
            {
                var bytes = http.GetByteArrayAsync(TessdataBaseUrl + $"{lang}.traineddata")
                                .GetAwaiter().GetResult();
                File.WriteAllBytes(path, bytes);
                log($"[CDA] {lang}.traineddata завантажено.");
            }
            catch (Exception ex)
            {
                log($"[CDA] ❌ Не вдалося завантажити {lang}.traineddata: {ex.Message}");
            }
        }
    }

    public static Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length)
            return screens[monitorIndex].Bounds;
        return Screen.PrimaryScreen!.Bounds;
    }

    // Inverts dark-on-bright: keeps only near-black pixels (dialog text on dark bg)
    static unsafe Bitmap InvertContrast(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                        ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
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

    static List<(string norm, Tesseract.Rect bounds)> ScanLines(
        TesseractEngine engine, Bitmap bmp, bool invert)
    {
        Bitmap processed = invert ? InvertContrast(bmp) : bmp;
        try
        {
            using var ms   = new MemoryStream();
            processed.Save(ms, SysImgFmt.Png);
            ms.Position = 0;
            using var pix  = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix, PageSegMode.Auto);
            using var iter = page.GetIterator();
            var lines = new List<(string, Tesseract.Rect)>();
            iter.Begin();
            do
            {
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) continue;
                var norm = Regex.Replace(
                    (iter.GetText(PageIteratorLevel.TextLine) ?? "").ToLowerInvariant(),
                    @"[^\w\s]", " ").Trim();
                if (norm.Length >= 5) lines.Add((Regex.Replace(norm, @"\s+", " "), bounds));
            }
            while (iter.Next(PageIteratorLevel.TextLine));
            return lines;
        }
        finally { if (invert) processed.Dispose(); }
    }

    /// <summary>
    /// Scans the full monitor to find the "Введіть код підтвердження" dialog.
    /// Returns the rectangle of the code input field, or null if not found.
    /// </summary>
    static TesseractEngine? _dialogEng;

    public static Rectangle? FindDialogRegion(int monitorIndex = -1)
    {
        if (!File.Exists(Path.Combine(TessDataPath, "ukr.traineddata"))) return null;

        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        _dialogEng ??= new TesseractEngine(TessDataPath, "ukr", EngineMode.Default);
        var engine = _dialogEng;

        var linesNormal   = ScanLines(engine, bmp, invert: false);
        var linesInverted = ScanLines(engine, bmp, invert: true);

        const string target = "введіть код підтвердження щоб взяти замовлення";

        (int dist, Tesseract.Rect bounds) FindBest(List<(string norm, Tesseract.Rect bounds)> lines)
        {
            int bestDist = int.MaxValue;
            Tesseract.Rect bestBounds = default;
            lines.Sort((a, b) => a.bounds.Y1.CompareTo(b.bounds.Y1));
            for (int i = 0; i < lines.Count; i++)
                for (int len = 1; len <= 2 && i + len - 1 < lines.Count; len++)
                {
                    var parts = new string[len];
                    for (int k = 0; k < len; k++) parts[k] = lines[i + k].norm;
                    string comb = string.Join(" ", parts);
                    if (comb.Length < 10 ||
                        (!comb.Contains("код") && !comb.Contains("підтверд") && !comb.Contains("введіть")))
                        continue;
                    int dist = Levenshtein.Distance(
                        Regex.Replace(comb, @"\d+", "").Trim(), target);
                    if (dist < bestDist)
                    {
                        bestDist   = dist;
                        bestBounds = new Tesseract.Rect(
                            Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1),
                            lines[i].bounds.Y1,
                            Math.Max(lines[i].bounds.X2, lines[i + len - 1].bounds.X2)
                                - Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1),
                            lines[i + len - 1].bounds.Y2 - lines[i].bounds.Y1);
                    }
                }
            return (bestDist, bestBounds);
        }

        var m1   = FindBest(linesNormal);
        var m2   = FindBest(linesInverted);
        var best = m1.dist < m2.dist ? m1 : m2;
        if (best.dist > 22) return null;

        int w = 600, x = (bmp.Width - w) / 2;
        int y = best.bounds.Y1 + best.bounds.Height;
        int h = 60;
        float sx = (float)bmp.Width / screen.Width, sy = (float)bmp.Height / screen.Height;
        return new Rectangle(
            screen.X + (int)(x / sx),
            screen.Y + (int)(y / sy),
            (int)(w / sx),
            (int)(h / sy));
    }
}

// ── Order scanner ─────────────────────────────────────────────────────────────
static class OrderScanner
{
    public class OrderCard
    {
        public Point  Anchor;
        public int    PricePerKm;
        public double Tonnage;
        public int    Level;
        public string Type       = "";
        public Point  ClickPoint;
    }

    static readonly double[] ValidTons = { 0.5, 1.5, 3.0, 5.0 };
    static Dictionary<double, Bitmap>? _tonTemplates;

    // ── Cached Tesseract engines (created once, reused every scan) ───────────
    static TesseractEngine? _badgeEng, _priceEng, _ukrEng;
    static readonly object  _engLock = new();

    static (TesseractEngine badge, TesseractEngine price, TesseractEngine ukr) GetEngines()
    {
        lock (_engLock)
        {
            if (_badgeEng == null)
            {
                _badgeEng = new TesseractEngine(TessOcr.TessDataPath, "eng", EngineMode.Default);
                _badgeEng.SetVariable("tessedit_char_whitelist", "0123456789.tTlLvVsS");
            }
            if (_priceEng == null)
            {
                _priceEng = new TesseractEngine(TessOcr.TessDataPath, "eng", EngineMode.Default);
                _priceEng.SetVariable("tessedit_char_whitelist", "0123456789$/km.≈ ");
            }
            _ukrEng ??= new TesseractEngine(TessOcr.TessDataPath, "ukr", EngineMode.Default);
            return (_badgeEng, _priceEng, _ukrEng);
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the given zone (or full monitor) for order cards.
    /// Uses Windows OCR (parallel async) — much faster than Tesseract.
    /// Template matching pre-filters by tonnage before any OCR runs.
    /// </summary>
    public static List<OrderCard> FindCards(int monitorIndex,
                                            Rectangle? scanZone = null,
                                            double     maxTon   = 5.0)
    {
        var screen = scanZone ?? TessOcr.GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);

        var anchors = FindGreenPriceAnchors(bmp);
        if (anchors.Count == 0) return new();

        // Step 1 – tonnage pre-filter via template matching (no OCR, ~1ms/card)
        var candidates = new List<(Point anchor, double ton)>();
        foreach (var anchor in anchors)
        {
            var r = new Rectangle(anchor.X - 10, anchor.Y - 55, 320, 45);
            if (r.X < 0 || r.Y < 0 || r.Right > bmp.Width || r.Bottom > bmp.Height) continue;
            using var crop = bmp.Clone(r, bmp.PixelFormat);
            double ton = MatchTonnageByTemplate(crop);
            if (ton > 0 && ton > maxTon) continue; // skip before any OCR
            candidates.Add((anchor, ton));
        }
        if (candidates.Count == 0) return new();

        // Step 2 – Tesseract OCR for remaining candidates (engines cached, created once)
        if (!File.Exists(Path.Combine(TessOcr.TessDataPath, "eng.traineddata"))) return new();
        var (badgeEng, priceEng, ukrEng) = GetEngines();

        var results = new List<OrderCard>();
        foreach (var (anchor, ton) in candidates)
        {
            int    price     = ReadPrice(bmp, anchor, priceEng);
            var    (ton2, level, orderType) = ReadBadge(bmp, anchor, badgeEng, ukrEng);
            if (ton2 > 0 && Math.Abs(ton2 - ton) > 0.01) { } // template already gave us ton
            results.Add(new OrderCard
            {
                Anchor     = anchor,
                PricePerKm = price,
                Tonnage    = ton,
                Level      = level,
                Type       = orderType,
                ClickPoint = new Point(screen.X + anchor.X + 130, screen.Y + anchor.Y + 50),
            });
        }
        return results;
    }

    // ── Price reading (Tesseract, English digits) ─────────────────────────────

    static int ReadPrice(Bitmap bmp, Point anchor, TesseractEngine eng)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y + 25, 240, 40);
        if (r.Right > bmp.Width || r.Bottom > bmp.Height || r.X < 0) return 0;
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var filt = EnhanceGreyText(crop);
        using var up   = Upscale(filt, 3);
        string txt = DoOcr(eng, up)
            .Replace(" ", "").Replace("O", "0").Replace("S", "5").Replace("s", "5");
        int di = txt.IndexOf('$');
        if (di >= 0 && di < txt.Length - 1) txt = txt[(di + 1)..];
        var m = Regex.Match(txt, @"\d+");
        return m.Success && int.TryParse(m.Value, out int p) ? p : 0;
    }

    // ── Badge reading: tonnage fallback + level + type (Tesseract Ukrainian) ──

    static (double ton, int lvl, string type) ReadBadge(
        Bitmap bmp, Point anchor, TesseractEngine badgeEng, TesseractEngine ukrEng)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y - 55, 320, 45);
        if (r.Right > bmp.Width || r.Y < 0 || r.X < 0) return (0, 1, "Невідомо");
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var filt = EnhanceBadgeText(crop);
        using var up   = Upscale(filt, 3);

        // Level from English OCR
        string eng = DoOcr(badgeEng, up)
            .Replace("S","5").Replace("s","5").Replace("O","0").Replace("o","0").ToUpper();
        int lvl = 1;
        var lm = Regex.Match(eng, @"(\d+)\s*[lL]");
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out lvl);

        // Type from Ukrainian OCR
        string ukr = DoOcr(ukrEng, up).ToLower();
        string type =
            ukr.Contains("одяг")    || ukr.Contains("модн")                                       ? "Одяг"          :
            ukr.Contains("продукт") || ukr.Contains("харч")                                       ? "Продукти"      :
            ukr.Contains("фарм")    || ukr.Contains("стерил") || ukr.Contains("аптек")            ? "Фармацевтика"  :
            ukr.Contains("нафт")    || ukr.Contains("палив")                                       ? "Нафта"         :
            ukr.Contains("авто")    || ukr.Contains("обслуг") || ukr.Contains("запчаст")          ? "Автозапчастини":
            ukr.Contains("різн")    || ukr.Contains("світ")   || ukr.Contains("мобіл")            ? "Різне"         :
            ukr.Contains("інш")     || ukr.Contains("спорядж")|| ukr.Contains("тактичн")          ? "Інше"          :
            "Невідомо";

        return (0, lvl, type); // tonnage already set by template matching
    }

    // ── Template matching for tonnage ─────────────────────────────────────────

    static Dictionary<double, Bitmap> LoadTonTemplates()
    {
        var d   = new Dictionary<double, Bitmap>();
        string dir = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath!)
                ?? AppDomain.CurrentDomain.BaseDirectory,
            "Cogs", "cda", "ton_templates");
        if (!Directory.Exists(dir)) return d;
        foreach (double t in ValidTons)
        {
            string path = Path.Combine(dir, $"{t}.png");
            if (File.Exists(path)) d[t] = new Bitmap(path);
        }
        return d;
    }

    static double MatchTonnageByTemplate(Bitmap crop)
    {
        _tonTemplates ??= LoadTonTemplates();
        if (_tonTemplates.Count == 0) return 0;
        double bestTon = 0, minSSD = double.MaxValue;
        foreach (var (t, tmpl) in _tonTemplates)
        {
            double ssd = TemplateSSD(crop, tmpl);
            if (ssd < minSSD) { minSSD = ssd; bestTon = t; }
        }
        return bestTon;
    }

    static unsafe double TemplateSSD(Bitmap source, Bitmap tmpl)
    {
        if (tmpl.Width > source.Width || tmpl.Height > source.Height) return double.MaxValue;
        var srcData = source.LockBits(new Rectangle(0, 0, source.Width, source.Height),
                          ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var tplData = tmpl.LockBits(new Rectangle(0, 0, tmpl.Width, tmpl.Height),
                          ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        double minSSD = double.MaxValue;
        try
        {
            byte* s = (byte*)srcData.Scan0, t = (byte*)tplData.Scan0;
            for (int y = 0; y <= source.Height - tmpl.Height; y++)
            for (int x = 0; x <= source.Width  - tmpl.Width;  x++)
            {
                double ssd = 0;
                for (int ty = 0; ty < tmpl.Height; ty++)
                {
                    byte* sRow = s + (y + ty) * srcData.Stride + x * 4;
                    byte* tRow = t + ty * tplData.Stride;
                    for (int tx = 0; tx < tmpl.Width; tx++, sRow += 4, tRow += 4)
                    {
                        int dr = sRow[2] - tRow[2], dg = sRow[1] - tRow[1], db = sRow[0] - tRow[0];
                        ssd += dr*dr + dg*dg + db*db;
                    }
                }
                if (ssd < minSSD) minSSD = ssd;
            }
        }
        finally { source.UnlockBits(srcData); tmpl.UnlockBits(tplData); }
        return minSSD;
    }

    // ── Anchor detection ──────────────────────────────────────────────────────

    static List<Point> FindGreenPriceAnchors(Bitmap bmp)
    {
        var anchors = new List<Point>();
        var sData   = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
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
                            if (Math.Abs(p.X - x) < 200 && Math.Abs(p.Y - y) < 150)
                            { isNew = false; break; }
                        if (isNew) anchors.Add(new Point(x, y));
                    }
                }
            }
        }
        bmp.UnlockBits(sData);
        return anchors;
    }

    // ── Image preprocessing ───────────────────────────────────────────────────

    static unsafe Bitmap EnhanceGreyText(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                        ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++)
        {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            bool isText = r > 150 && g > 150 && b > 150
                       && Math.Abs(r - g) < 25 && Math.Abs(g - b) < 25;
            byte bw = isText ? (byte)0 : (byte)255;
            d[i * 4] = bw; d[i * 4 + 1] = bw; d[i * 4 + 2] = bw; d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData);
        return dst;
    }

    static unsafe Bitmap EnhanceBadgeText(Bitmap src)
    {
        var dst   = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                        ImageLockMode.ReadOnly,  PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++)
        {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            bool isText = (b > 160 && r < 140 && g < 180)
                       || (r > 150 && g > 110 && b < 140)
                       || (r > 180 && g > 180 && b > 180);
            byte bw = isText ? (byte)0 : (byte)255;
            d[i * 4] = bw; d[i * 4 + 1] = bw; d[i * 4 + 2] = bw; d[i * 4 + 3] = 255;
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

    static string DoOcr(TesseractEngine engine, Bitmap bmp)
    {
        using var ms   = new MemoryStream();
        bmp.Save(ms, SysImgFmt.Png);
        ms.Position = 0;
        using var pix  = Pix.LoadFromMemory(ms.ToArray());
        using var page = engine.Process(pix, PageSegMode.SingleLine);
        return page.GetText() ?? "";
    }
}

// ── Mouse input ───────────────────────────────────────────────────────────────
static class MouseInput
{
    public static void Click(int x, int y)
    {
        WinApi.SetCursorPos(x, y);
        Thread.Sleep(20);
        WinApi.mouse_event(0x0002, 0, 0, 0, 0); // MOUSEEVENTF_LEFTDOWN
        Thread.Sleep(20);
        WinApi.mouse_event(0x0004, 0, 0, 0, 0); // MOUSEEVENTF_LEFTUP
    }
}
