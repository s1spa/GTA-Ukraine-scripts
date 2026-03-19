using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using SysImgFmt = System.Drawing.Imaging.ImageFormat;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using OpenCvSharp;
using Point     = System.Drawing.Point;
using Rectangle = System.Drawing.Rectangle;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using Sdcb.PaddleOCR.Models.Online;
using Sdcb.PaddleInference;
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

// ── PaddleOCR helper (замінює Tesseract) ─────────────────────────────────────
static class PaddleHelper
{
    // Два движки: English (для тоннажу/рівня/ціни) та Cyrillic (для типу вантажу і діалогу)
    static PaddleOcrAll? _eng, _cyr;
    static readonly object _engLock = new();
    static readonly object _cyrLock = new();

    // Завантажує модель з інтернету (~50 MB за раз, кешується локально)
    static FullOcrModel DownloadModel(bool cyr)
    {
        // OnlineFullModels.EnglishV3 = det(EnglishV3) + cls(ChineseMobileV2) + rec(EnglishV3)
        // Для кирилиці замінюємо лише recognizer на CyrillicV3
        var onlineModels = cyr
            ? new OnlineFullModels(
                OnlineDetectionModel.EnglishV3,
                OnlineClassificationModel.ChineseMobileV2,
                LocalDictOnlineRecognizationModel.CyrillicV3)
            : OnlineFullModels.EnglishV3;
        return onlineModels.DownloadAsync().GetAwaiter().GetResult();
    }

    static PaddleOcrAll MakeEngine(FullOcrModel model) => new(model)
    {
        AllowRotateDetection    = false,
        Enable180Classification = false,
    };

    /// <summary>
    /// Ініціалізує двигуни PaddleOCR. Моделі (~50 MB) завантажуються автоматично
    /// до %USERPROFILE%\.paddlesharp\models при першому запуску.
    /// </summary>
    public static void EnsureInit(Action<string> log)
    {
        if (_eng != null && _cyr != null) return;
        log("[CDA] Завантаження PaddleOCR моделей (~50 MB, кешується)...");
        try
        {
            var engModel = DownloadModel(cyr: false);
            var cyrModel = DownloadModel(cyr: true);
            lock (_engLock) _eng ??= MakeEngine(engModel);
            lock (_cyrLock) _cyr ??= MakeEngine(cyrModel);
            log("[CDA] PaddleOCR готовий.");
        }
        catch (Exception ex) { log($"[CDA] ❌ PaddleOCR: {ex.Message}"); }
    }

    /// <summary>Конвертує System.Drawing.Bitmap у OpenCV Mat (BGR).</summary>
    public static Mat BitmapToMat(Bitmap bmp)
    {
        var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                     ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Format32bppArgb у пам'яті = BGRA на little-endian
            using var bgra = new Mat(bmp.Height, bmp.Width, MatType.CV_8UC4, bd.Scan0, bd.Stride);
            return bgra.CvtColor(ColorConversionCodes.BGRA2BGR);
        }
        finally { bmp.UnlockBits(bd); }
    }

    /// <summary>Детекція + розпізнавання тексту на Bitmap.</summary>
    public static PaddleOcrResultRegion[] Detect(Bitmap bmp, bool cyr = false)
    {
        if (cyr ? _cyr == null : _eng == null) return Array.Empty<PaddleOcrResultRegion>();
        using var mat = BitmapToMat(bmp);
        if (cyr) { lock (_cyrLock) return _cyr!.Run(mat).Regions; }
        else     { lock (_engLock) return _eng!.Run(mat).Regions; }
    }

    public static System.Drawing.Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        return monitorIndex >= 0 && monitorIndex < screens.Length
            ? screens[monitorIndex].Bounds
            : Screen.PrimaryScreen!.Bounds;
    }

    /// <summary>
    /// Сканує монітор на наявність діалогу "Введіть код підтвердження".
    /// Повертає координати поля вводу або null.
    /// </summary>
    public static System.Drawing.Rectangle? FindDialogRegion(int monitorIndex = -1)
    {
        if (_cyr == null) return null;
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var regions = Detect(bmp, cyr: true);

        const string target = "введіть код підтвердження щоб взяти замовлення";
        PaddleOcrResultRegion best = default;
        int bestDist = int.MaxValue;

        foreach (var region in regions)
        {
            var text = region.Text.ToLower();
            if (!text.Contains("код") && !text.Contains("підтверд") && !text.Contains("введіть"))
                continue;
            string norm = Regex.Replace(text, @"[^\w\s]", " ").Trim();
            int d = Levenshtein.Distance(norm, target);
            if (d < bestDist) { bestDist = d; best = region; }
        }

        if (bestDist > 30 || bestDist == int.MaxValue) return null;

        var b  = best.Rect.BoundingRect();
        float sx = (float)bmp.Width / screen.Width, sy = (float)bmp.Height / screen.Height;
        int w = 600, x = (bmp.Width - w) / 2;
        return new System.Drawing.Rectangle(
            screen.X + (int)(x  / sx),
            screen.Y + (int)((b.Y + b.Height) / sy),
            (int)(w  / sx),
            (int)(60 / sy));
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

    public static List<OrderCard> FindCards(int monitorIndex,
                                            Rectangle? scanZone = null,
                                            double     maxTon   = 5.0)
    {
        var cards  = new List<OrderCard>();
        var screen = scanZone ?? PaddleHelper.GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);

        var anchors = FindGreenPriceAnchors(bmp);
        if (anchors.Count == 0) return cards;

        foreach (var anchor in anchors)
        {
            var (ton, lvl, type) = ReadBadge(bmp, anchor);
            if (ton > 0 && ton > maxTon) continue;
            int price = ReadPrice(bmp, anchor);
            cards.Add(new OrderCard
            {
                Anchor     = anchor,
                PricePerKm = price,
                Tonnage    = ton,
                Level      = lvl,
                Type       = type,
                ClickPoint = new Point(screen.X + anchor.X + 130, screen.Y + anchor.Y + 50),
            });
        }
        return cards;
    }

    // ── Price reading (English PaddleOCR) ─────────────────────────────────────

    static int ReadPrice(Bitmap bmp, Point anchor)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y + 25, 240, 40);
        if (r.Right > bmp.Width || r.Bottom > bmp.Height || r.X < 0) return 0;
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var up   = Upscale(crop, 3);
        string txt = string.Join("", PaddleHelper.Detect(up).Select(rg => rg.Text))
            .Replace(" ", "").Replace("O", "0").Replace("S", "5").Replace("s", "5");
        int di = txt.IndexOf('$');
        if (di >= 0 && di < txt.Length - 1) txt = txt[(di + 1)..];
        var m = Regex.Match(txt, @"\d+");
        return m.Success && int.TryParse(m.Value, out int p) ? p : 0;
    }

    // ── Badge reading (English для тоннажу/рівня, Cyrillic для типу) ──────────

    static (double ton, int lvl, string type) ReadBadge(Bitmap bmp, Point anchor)
    {
        var r = new Rectangle(anchor.X - 10, anchor.Y - 55, 320, 45);
        if (r.Right > bmp.Width || r.Y < 0 || r.X < 0) return (0, 1, "Невідомо");
        using var crop = bmp.Clone(r, bmp.PixelFormat);
        using var up   = Upscale(crop, 3);   // PaddleOCR працює на кольорових зображеннях

        // English: тоннаж + рівень
        string eng = string.Join(" ", PaddleHelper.Detect(up).Select(rg => rg.Text))
            .Replace("S","5").Replace("s","5").Replace("O","0").Replace("o","0").ToUpper();

        double ton = 0;
        var tm = Regex.Match(eng, @"(\d+[.,]?\d*)\s*[tT]");
        if (tm.Success)
        {
            string raw = tm.Groups[1].Value.Replace(',', '.');
            double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out ton);
            // Виправлення помилки парсингу: "15" → "1.5"
            if (!Array.Exists(ValidTons, t => Math.Abs(t - ton) < 0.01) && raw.Length >= 2)
            {
                string candidate = raw[..^1] + "." + raw[^1..];
                if (double.TryParse(candidate, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out double c)
                    && Array.Exists(ValidTons, t => Math.Abs(t - c) < 0.01))
                    ton = c;
            }
        }

        int lvl = 1;
        var lm = Regex.Match(eng, @"(\d+)\s*[lL]");
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out lvl);

        // Cyrillic: тип вантажу
        string ukr = string.Join(" ", PaddleHelper.Detect(up, cyr: true).Select(rg => rg.Text))
            .ToLower();
        string type =
            ukr.Contains("одяг")    || ukr.Contains("модн")                                     ? "Одяг"          :
            ukr.Contains("продукт") || ukr.Contains("харч")                                     ? "Продукти"      :
            ukr.Contains("фарм")    || ukr.Contains("стерил") || ukr.Contains("аптек")          ? "Фармацевтика"  :
            ukr.Contains("нафт")    || ukr.Contains("палив")                                     ? "Нафта"         :
            ukr.Contains("авто")    || ukr.Contains("обслуг") || ukr.Contains("запчаст")        ? "Автозапчастини":
            ukr.Contains("різн")    || ukr.Contains("світ")   || ukr.Contains("мобіл")          ? "Різне"         :
            ukr.Contains("інш")     || ukr.Contains("спорядж")|| ukr.Contains("тактичн")        ? "Інше"          :
            "Невідомо";

        return (ton, lvl, type);
    }

    // ── Anchor detection (green $/km badge color) ─────────────────────────────

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
                    // rgba(222,237,131) — green-yellow price badge color
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

    // ── Image helpers ─────────────────────────────────────────────────────────

    static Bitmap Upscale(Bitmap src, int scale)
    {
        var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
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
