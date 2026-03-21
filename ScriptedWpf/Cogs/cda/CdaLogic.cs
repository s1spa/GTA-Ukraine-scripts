using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Net.Http;
using ScriptedWpf.Core;
using Tesseract;
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

    // Пряма копія пікселів — без PNG encode/decode (~5-10x швидше)
    static SoftwareBitmap ToSoftwareBitmap(SysBitmap bmp)
    {
        var bd = bmp.LockBits(new SysRect(0, 0, bmp.Width, bmp.Height),
                     ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int bytes = Math.Abs(bd.Stride) * bmp.Height;
            byte[] pixels = new byte[bytes];
            Marshal.Copy(bd.Scan0, pixels, 0, bytes);
            using var dw = new DataWriter();
            dw.WriteBytes(pixels);
            return SoftwareBitmap.CreateCopyFromBuffer(
                dw.DetachBuffer(),
                BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height,
                BitmapAlphaMode.Premultiplied);
        }
        finally { bmp.UnlockBits(bd); }
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
        using var soft      = ToSoftwareBitmap(upscaled);
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

// ── Tesseract OCR Helper ───────────────────────────────────────────────────────
static class TessOcr
{
    static TesseractEngine? _engine;
    static readonly object  _lock        = new();
    static bool             _initStarted;

    static string TessDataDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tessdata");

    public static bool IsReady { get { lock (_lock) return _engine != null; } }

    public static void EnsureInit(Action<string> log)
    {
        lock (_lock) { if (_initStarted) return; _initStarted = true; }
        Task.Run(() =>
        {
            try
            {
                EnsureTessdata(log);
                var eng = new TesseractEngine(TessDataDir, "ukr+eng", EngineMode.Default);
                lock (_lock) _engine = eng;
                log("[CDA] Tesseract готовий.");
            }
            catch (Exception ex) { log($"[CDA] ❌ Tesseract init: {ex.Message}"); }
        });
    }

    static void EnsureTessdata(Action<string> log)
    {
        Directory.CreateDirectory(TessDataDir);
        var files = new[]
        {
            ("eng", "https://github.com/tesseract-ocr/tessdata_fast/raw/main/eng.traineddata"),
            ("ukr", "https://github.com/tesseract-ocr/tessdata_fast/raw/main/ukr.traineddata"),
        };
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        foreach (var (lang, url) in files)
        {
            var path = Path.Combine(TessDataDir, $"{lang}.traineddata");
            if (File.Exists(path)) continue;
            log($"[CDA] Завантаження tessdata/{lang}...");
            var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(path, bytes);
            log($"[CDA] tessdata/{lang} готово.");
        }
    }

    public static string DetectText(SysBitmap bmp)
    {
        TesseractEngine? engine;
        lock (_lock) engine = _engine;
        if (engine == null) return "";

        using var ms  = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        using var pix = Pix.LoadFromMemory(ms.ToArray());
        lock (engine)
        {
            using var page = engine.Process(pix);
            return page.GetText().Trim();
        }
    }

    public static SysRect GetMonitorBounds(int monitorIndex)
        => WinOcr.GetMonitorBounds(monitorIndex);

    public static SysRect? FindDialogRegion(int monitorIndex = -1)
    {
        if (!IsReady) return null;
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var candidate = FindDialogCandidateByColor(bmp);
        if (!candidate.HasValue) return null;
        return ConfirmDialogWithOcr(bmp, candidate.Value);
    }

    static unsafe SysRect? FindDialogCandidateByColor(SysBitmap bmp)
    {
        var data    = bmp.LockBits(new SysRect(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rects   = new List<SysRect>();
        var visited = new bool[bmp.Width * bmp.Height];
        byte* scan0 = (byte*)data.Scan0;

        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (visited[y * bmp.Width + x]) continue;
                byte* p = scan0 + y * data.Stride + x * 4;
                if (p[3] > 100 && p[0] < 50 && p[1] < 50 && p[2] < 50)
                {
                    var r = FloodFill(scan0, data.Stride, visited, x, y, bmp.Width, bmp.Height);
                    if (r.Width * r.Height > 5000) rects.Add(r);
                }
            }
        bmp.UnlockBits(data);
        if (rects.Count == 0) return null;
        return rects.OrderByDescending(r => r.Width * r.Height).First();
    }

    static unsafe SysRect FloodFill(byte* scan0, int stride, bool[] visited, int startX, int startY, int w, int h)
    {
        var stack = new Stack<SysPoint>();
        stack.Push(new SysPoint(startX, startY));
        visited[startY * w + startX] = true;
        int minX = startX, maxX = startX, minY = startY, maxY = startY;

        while (stack.Count > 0)
        {
            var p = stack.Pop();
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);

            for (int i = -1; i <= 1; i++)
                for (int j = -1; j <= 1; j++)
                {
                    if (i == 0 && j == 0) continue;
                    int nx = p.X + i, ny = p.Y + j;
                    if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[ny * w + nx])
                    {
                        byte* ptr = scan0 + ny * stride + nx * 4;
                        if (ptr[3] > 100 && ptr[0] < 50 && ptr[1] < 50 && ptr[2] < 50)
                        {
                            visited[ny * w + nx] = true;
                            stack.Push(new SysPoint(nx, ny));
                        }
                    }
                }
        }
        return new SysRect(minX, minY, maxX - minX, maxY - minY);
    }

    static SysRect? ConfirmDialogWithOcr(SysBitmap bmp, SysRect candidate)
    {
        using var crop = bmp.Clone(candidate, bmp.PixelFormat);
        string text = DetectText(crop).ToLowerInvariant();
        if ((text.Contains("код") && text.Contains("підтвердж")) ||
            (text.Contains("введіть") && text.Contains("код")))
        {
            int w  = 600;
            int cx = candidate.X + candidate.Width / 2;
            int x  = Math.Max(0, cx - w / 2);
            int y  = candidate.Y + candidate.Height / 2 + 10;
            return new SysRect(x, y, Math.Min(w, bmp.Width - x), 60);
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

            var (price, tonnage, level, orderType) = ReadCard(bmp, anchor);

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

    // Оригінальні зони (badge окремо від price) + 3 OCR паралельно
    // eng_badge || cyr_badge → справжній паралелізм (різні lock-об'єкти)
    // eng_price конкурує з eng_badge за lock(eng), але cyr вже йде паралельно
    // Результат: 3T послідовно → ~2T з overlap = ~33% швидше на картку
    static (int price, double ton, int lvl, string type) ReadCard(SysBitmap bmp, SysPoint anchor)
    {
        int x0 = Math.Max(0, anchor.X - 10);

        // Badge zone — оригінальні межі ReadBadge
        int by0 = Math.Max(0, anchor.Y - 100);
        var br  = new SysRect(x0, by0, Math.Min(380, bmp.Width - x0), 85);

        // Price zone — оригінальні межі ReadPrice
        int py0 = anchor.Y + 25;
        var pr  = new SysRect(x0, py0, Math.Min(280, bmp.Width - x0), 50);

        if (br.Width < 50 || py0 + 50 > bmp.Height || pr.Width < 30)
            return (0, 0, 1, "Невідомо");

        using var badgeCrop = bmp.Clone(br, bmp.PixelFormat);
        using var badgeUp   = Upscale(badgeCrop, 3);
        using var priceCrop = bmp.Clone(pr, bmp.PixelFormat);
        using var priceUp   = Upscale(priceCrop, 3);

        // TessOcr з ukr+eng читає і кирилицю і латиницю в одному проході
        string badge    = TessOcr.DetectText(badgeUp);
        string engPrice = TessOcr.DetectText(priceUp);
        string ukr      = badge;

        // ── Badge: тоннаж + рівень ────────────────────────────────────────────
        string eng = badge
            .Replace("S", "5").Replace("s", "5").Replace("O", "0").Replace("o", "0")
            .ToUpperInvariant();

        double ton = 0;
        int    lvl = 1;

        var tm = Regex.Match(eng, @"(\d+[.,]?\d*)\s*[TТ]");
        if (tm.Success)
        {
            string raw = tm.Groups[1].Value.Replace(',', '.');
            double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out ton);
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

        // ── Price: ціна/km ────────────────────────────────────────────────────
        int price = 0;
        // "$3 728/km" — Tesseract reads "$" as "5", giving "53 728/km".
        // Pattern "(\d) (\d{3})(?=/km)" anchors to the thousands-separator format and
        // naturally skips the misread prefix digit: in "53 728" it matches "3 728".
        var kmMatch = Regex.Match(engPrice, @"(\d) (\d{3})(?=/km)", RegexOptions.IgnoreCase);
        if (kmMatch.Success)
        {
            int.TryParse(kmMatch.Groups[1].Value + kmMatch.Groups[2].Value, out price);
        }
        else
        {
            // Fallback for prices without thousands separator (e.g. "3728/km")
            var kmFallback = Regex.Match(engPrice, @"(\d{3,5})/km", RegexOptions.IgnoreCase);
            if (kmFallback.Success) int.TryParse(kmFallback.Groups[1].Value, out price);
        }

        // ── Cyrillic: тип вантажу ─────────────────────────────────────────────
        ukr = ukr.ToLowerInvariant();
        string type =
            ukr.Contains("одяг")    || ukr.Contains("модн")                                       ? "Одяг"          :
            ukr.Contains("продукт") || ukr.Contains("харч")                                       ? "Продукти"      :
            ukr.Contains("фарм")    || ukr.Contains("стерил")  || ukr.Contains("аптек")           ? "Фармацевтика"  :
            ukr.Contains("нафт")    || ukr.Contains("палив")                                      ? "Нафта"         :
            ukr.Contains("авто")    || ukr.Contains("обслуг")  || ukr.Contains("запчаст")         ? "Автозапчастини":
            ukr.Contains("різн")    || ukr.Contains("світ")    || ukr.Contains("мобіл")           ? "Різне"         :
            ukr.Contains("інш")     || ukr.Contains("спорядж") || ukr.Contains("тактичн")         ? "Інше"          :
            ukr.Contains("нелег")   || ukr.Contains("заборон")  || ukr.Contains("контраб")         ? "Нелегальне"    :
            "Невідомо";

        return (price, ton, lvl, type);
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
                        // Зворотна ітерація + early break по Y (анкори відсортовані зверху-вниз)
                        bool isNew = true;
                        for (int ai = anchors.Count - 1; ai >= 0; ai--)
                        {
                            var ap = anchors[ai];
                            if (y - ap.Y > 150) break; // всі попередні ще далі по Y
                            if (Math.Abs(ap.X - x) < 200) { isNew = false; break; }
                        }
                        if (isNew) anchors.Add(new SysPoint(x, y));
                    }
                }
            }
        }
        bmp.UnlockBits(sData);
        return anchors;
    }
}

// ── Notification Sound ────────────────────────────────────────────────────────
static class CdaSound
{
    static string SoundDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "cda", "notification_sound");

    public static string[] GetBuiltInFiles()
        => Directory.Exists(SoundDir)
            ? Directory.GetFiles(SoundDir, "*.mp3")
            : Array.Empty<string>();

    public static void Play(string? fileNameOrPath)
    {
        if (string.IsNullOrEmpty(fileNameOrPath)) return;
        string path = Path.IsPathRooted(fileNameOrPath)
            ? fileNameOrPath
            : Path.Combine(SoundDir, fileNameOrPath);
        if (!File.Exists(path)) return;

        string uriPath = path;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var player = new System.Windows.Media.MediaPlayer();
            player.Open(new Uri(uriPath));
            player.MediaEnded += (_, _) => player.Close();
            player.Play();
        });
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
