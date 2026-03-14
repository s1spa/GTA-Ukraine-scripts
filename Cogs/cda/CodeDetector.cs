using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Tesseract;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

// ── Windows API ───────────────────────────────────────────────────────────────
static class WinApi
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Ansi)] static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DISPLAY_DEVICE { public uint cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public uint StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey; }
    public static string GetMonitorFriendlyName(string deviceName) { var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() }; if (EnumDisplayDevices(deviceName, 0, ref dd, 0)) return dd.DeviceString; return deviceName; }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const int WM_HOTKEY = 0x0312;
}

// ── Config ────────────────────────────────────────────────────────────────────
// ── Config ────────────────────────────────────────────────────────────────────
class Config
{
    public int MonitorIndex = -1;
    public int X, Y, Width, Height; 
    
    // НАЛАШТУВАННЯ ФІЛЬТРІВ
    public int MinPrice = 1000;
    public double MaxTon = 5.0; // Тепер це просто максимальна місткість
    public int MinLvl = 1;
    public int MaxLvl = 5;
    public List<string> Types = new List<string> { "Одяг", "Нафта", "Фармацевтика", "Різне", "Продукти", "Автозапчастини", "Інше" };

    static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

    public static Config Load()
    {
        var cfg = new Config();
        if (!File.Exists(FilePath)) return cfg;

        foreach (var line in File.ReadAllLines(FilePath))
        {
            var l = line.Trim();
            if (l.StartsWith('#') || !l.Contains('=')) continue;
            var parts = l.Split('=', 2);
            string key = parts[0].Trim().ToLower();
            string val = parts[1].Trim();

            switch (key)
            {
                case "monitor": if (int.TryParse(val, out int m)) cfg.MonitorIndex = m; break;
                case "x": if (int.TryParse(val, out int x)) cfg.X = x; break;
                case "y": if (int.TryParse(val, out int y)) cfg.Y = y; break;
                case "w": if (int.TryParse(val, out int w)) cfg.Width = w; break;
                case "h": if (int.TryParse(val, out int h)) cfg.Height = h; break;
                case "minprice": if (int.TryParse(val, out int mp)) cfg.MinPrice = mp; break;
                case "maxton": if (double.TryParse(val.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out double mt)) cfg.MaxTon = mt; break;
                case "minlvl": if (int.TryParse(val, out int ml1)) cfg.MinLvl = ml1; break;
                case "maxlvl": if (int.TryParse(val, out int ml2)) cfg.MaxLvl = ml2; break;
                case "types": 
                    if (!string.IsNullOrEmpty(val)) 
                        cfg.Types = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList(); 
                    break;
            }
        }
        return cfg;
    }

    public void Save() =>
        File.WriteAllText(FilePath,
            $"monitor={MonitorIndex}\nx={X}\ny={Y}\nw={Width}\nh={Height}\n" +
            $"minprice={MinPrice}\n" +
            $"maxton={MaxTon.ToString(CultureInfo.InvariantCulture)}\n" +
            $"minlvl={MinLvl}\nmaxlvl={MaxLvl}\ntypes={string.Join(",", Types)}\n");
}
// ── Допоміжні класи ───────────────────────────────────────────────────────────
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
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int y = 0; y < src.Height; y++) {
            byte* sRow = s + y * sData.Stride, dRow = d + y * dData.Stride;
            for (int x = 0; x < src.Width; x++) {
                int b = sRow[x * 4], g = sRow[x * 4 + 1], r = sRow[x * 4 + 2];
                int gray = (int)(r * 0.299 + g * 0.587 + b * 0.114);
                gray = Math.Clamp((int)((gray - 128) * 3.0 + 128), 0, 255);
                byte bw = gray < 128 ? (byte)0 : (byte)255;
                dRow[x * 4 + 0] = bw; dRow[x * 4 + 1] = bw; dRow[x * 4 + 2] = bw; dRow[x * 4 + 3] = 255;
            }
        }
        src.UnlockBits(sData); dst.UnlockBits(dData); return dst;
    }
}

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
        return OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))!;
    }

    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        using var ras = new InMemoryRandomAccessStream();
        using var dw = new DataWriter(ras.GetOutputStreamAt(0));
        dw.WriteBytes(ms.ToArray());
        await dw.StoreAsync();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    public static async Task<string?> FindCodeAsync(Bitmap img)
    {
        using var processed = ScreenCapture.Preprocess(img);
        using var soft = await ToSoftwareBitmapAsync(processed);
        var result = await Engine.RecognizeAsync(soft);
        var spaced = Regex.Match(result.Text, @"(\d)\s+(\d)\s+(\d)\s+(\d)\s+(\d)\s+(\d)");
        if (spaced.Success) return string.Concat(spaced.Groups[1].Value, spaced.Groups[2].Value, spaced.Groups[3].Value, spaced.Groups[4].Value, spaced.Groups[5].Value, spaced.Groups[6].Value);
        var codes = Regex.Matches(result.Text, @"\b\d{6}\b");
        return codes.Count > 0 ? codes[0].Value : null;
    }

    public static string TessDataPath => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath!) ?? AppDomain.CurrentDomain.BaseDirectory, "tessdata");

    public static Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length) return screens[monitorIndex].Bounds;
        return Screen.PrimaryScreen!.Bounds;
    }

    static unsafe Bitmap EnhanceContrast(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++) {
            int max = Math.Max(s[i * 4], Math.Max(s[i * 4 + 1], s[i * 4 + 2]));
            byte bw = max < 20 ? (byte)255 : (byte)0;
            d[i * 4] = bw; d[i * 4 + 1] = bw; d[i * 4 + 2] = bw; d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData); return dst;
    }

    static List<(string norm, Tesseract.Rect bounds)> ScanLines(TesseractEngine engine, Bitmap bmp, bool enhance)
    {
        Bitmap processed = enhance ? EnhanceContrast(bmp) : bmp;
        try {
            using var ms = new MemoryStream();
            processed.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position = 0;
            using var pix = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix, PageSegMode.Auto);
            using var iter = page.GetIterator();
            var lines = new List<(string norm, Tesseract.Rect bounds)>();
            iter.Begin();
            do {
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) continue;
                var norm = Regex.Replace((iter.GetText(PageIteratorLevel.TextLine) ?? "").ToLowerInvariant(), @"[^\w\s]", " ").Trim();
                if (norm.Length >= 5) lines.Add((Regex.Replace(norm, @"\s+", " "), bounds));
            } while (iter.Next(PageIteratorLevel.TextLine));
            return lines;
        } finally { if (enhance) processed.Dispose(); }
    }

    public static Rectangle? FindDialogRegion(int monitorIndex = -1)
    {
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        using var engine = new TesseractEngine(TessDataPath, "ukr", EngineMode.Default);
        var linesNormal = ScanLines(engine, bmp, enhance: false);
        var linesInverted = ScanLines(engine, bmp, enhance: true);
        string target = "введіть код підтвердження щоб взяти замовлення";

        (int dist, Tesseract.Rect bounds) FindBestMatch(List<(string norm, Tesseract.Rect bounds)> lines)
        {
            int bestDist = int.MaxValue; Tesseract.Rect bestBounds = default;
            lines.Sort((a, b) => a.bounds.Y1.CompareTo(b.bounds.Y1));
            for (int i = 0; i < lines.Count; i++) {
                for (int len = 1; len <= 2 && i + len - 1 < lines.Count; len++) {
                    var parts = new string[len];
                    for (int k = 0; k < len; k++) parts[k] = lines[i + k].norm;
                    string comb = string.Join(" ", parts);
                    if (comb.Length < 10 || (!comb.Contains("код") && !comb.Contains("підтверд") && !comb.Contains("введіть"))) continue;
                    int dist = Levenshtein.Distance(Regex.Replace(comb, @"\d+", "").Trim(), target);
                    if (dist < bestDist) { bestDist = dist; bestBounds = new Tesseract.Rect(Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1), lines[i].bounds.Y1, Math.Max(lines[i].bounds.X2, lines[i + len - 1].bounds.X2) - Math.Min(lines[i].bounds.X1, lines[i + len - 1].bounds.X1), lines[i + len - 1].bounds.Y2 - lines[i].bounds.Y1); }
                }
            }
            return (bestDist, bestBounds);
        }

        var match1 = FindBestMatch(linesNormal);
        var match2 = FindBestMatch(linesInverted);
        var best = match1.dist < match2.dist ? match1 : match2;

        if (best.dist > 22) return null;
        int w = 600, x = (bmp.Width - w) / 2, y = best.bounds.Y1, h = 80;
        float scaleX = (float)bmp.Width / screen.Width, scaleY = (float)bmp.Height / screen.Height;
        return new Rectangle(screen.X + (int)(x / scaleX), screen.Y + (int)(y / scaleY), (int)(w / scaleX), (int)(h / scaleY));
    }
}

// ── Скан замовлень ────────────────────────────────────────────────────────────
static class OrderScanner
{
    public class OrderCard { public Point Anchor; public int PricePerKm; public double Tonnage; public int Level; public string Type = ""; public Point ClickPoint; }

    public static List<OrderCard> FindCards(int monitorIndex)
    {
        var screen = WinOcr.GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        var cards = new List<OrderCard>();

        var anchors = FindGreenPriceAnchors(bmp);
        if (anchors.Count == 0) return cards;

        using var badgeEngEngine = new TesseractEngine(WinOcr.TessDataPath, "eng", EngineMode.Default);
        badgeEngEngine.SetVariable("tessedit_char_whitelist", "0123456789.tTlLvVsS"); 
        
        using var priceEngEngine = new TesseractEngine(WinOcr.TessDataPath, "eng", EngineMode.Default);
        priceEngEngine.SetVariable("tessedit_char_whitelist", "0123456789$/km.≈ ");

        using var ukrEngine = new TesseractEngine(WinOcr.TessDataPath, "ukr", EngineMode.Default);

        foreach (var anchor in anchors)
        {
            var perKmRect = new Rectangle(anchor.X - 10, anchor.Y + 25, 240, 40); 
            int price = 0;
            if (perKmRect.Right <= bmp.Width && perKmRect.Bottom <= bmp.Height && perKmRect.X >= 0)
            {
                using var cropPrice = bmp.Clone(perKmRect, bmp.PixelFormat);
                using var filteredPrice = EnhanceGreyText(cropPrice);
                using var upscaledPrice = Upscale(filteredPrice, 3);
                
                string pText = DoOcr(priceEngEngine, upscaledPrice).Replace(" ", "").Replace("O", "0").Replace("S", "5").Replace("s", "5");
                int dollarIdx = pText.IndexOf('$');
                if (dollarIdx >= 0 && dollarIdx < pText.Length - 1) pText = pText.Substring(dollarIdx + 1);
                var matchPrice = Regex.Match(pText, @"\d+");
                if (matchPrice.Success) int.TryParse(matchPrice.Value, out price);
            }

            var badgeRect = new Rectangle(anchor.X - 10, anchor.Y - 55, 320, 45);
            double tonnage = 0; int level = 1; string orderType = "Невідомо";

            if (badgeRect.Right <= bmp.Width && badgeRect.Y >= 0 && badgeRect.X >= 0)
            {
                using var cropBadge = bmp.Clone(badgeRect, bmp.PixelFormat);
                using var filteredBadge = EnhanceBadgeText(cropBadge);
                using var upscaledBadge = Upscale(filteredBadge, 3);

                string engTxt = DoOcr(badgeEngEngine, upscaledBadge).Replace("S", "5").Replace("s", "5").Replace("O", "0").Replace("o", "0").ToUpper();
                var tonMatch = Regex.Match(engTxt, @"(\d+[.,]?\d*)\s*[tT]");
                if (tonMatch.Success) double.TryParse(tonMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out tonnage);
                
                var lvlMatch = Regex.Match(engTxt, @"(\d+)\s*[lL]");
                if (lvlMatch.Success) int.TryParse(lvlMatch.Groups[1].Value, out level);

                string ukrTxt = DoOcr(ukrEngine, upscaledBadge).ToLower();
                if (ukrTxt.Contains("одяг") || ukrTxt.Contains("модн")) orderType = "Одяг";
                else if (ukrTxt.Contains("продукт") || ukrTxt.Contains("харч")) orderType = "Продукти";
                else if (ukrTxt.Contains("фарм") || ukrTxt.Contains("стерил") || ukrTxt.Contains("аптек")) orderType = "Фармацевтика";
                else if (ukrTxt.Contains("нафт") || ukrTxt.Contains("палив")) orderType = "Нафта";
                else if (ukrTxt.Contains("авто") || ukrTxt.Contains("обслуг") || ukrTxt.Contains("запчаст")) orderType = "Автозапчастини";
                else if (ukrTxt.Contains("різн") || ukrTxt.Contains("світ") || ukrTxt.Contains("мобіл")) orderType = "Різне";
                else if (ukrTxt.Contains("інш") || ukrTxt.Contains("спорядж") || ukrTxt.Contains("тактичн")) orderType = "Інше";
            }

            int btnX = screen.X + anchor.X + 130, btnY = screen.Y + anchor.Y + 140;
            cards.Add(new OrderCard { Anchor = anchor, PricePerKm = price, Tonnage = tonnage, Level = level, Type = orderType, ClickPoint = new Point(btnX, btnY) });
        }
        return cards;
    }

    static string DoOcr(TesseractEngine engine, Bitmap bmp) { using var ms = new MemoryStream(); bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png); ms.Position = 0; using var pix = Pix.LoadFromMemory(ms.ToArray()); using var page = engine.Process(pix, PageSegMode.SingleLine); return page.GetText() ?? ""; }

    static List<Point> FindGreenPriceAnchors(Bitmap bmp)
    {
        var anchors = new List<Point>();
        var sData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe {
            byte* s = (byte*)sData.Scan0;
            for (int y = 0; y < bmp.Height; y++) {
                byte* row = s + y * sData.Stride;
                for (int x = 0; x < bmp.Width; x++) {
                    int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                    if (r > 200 && g > 220 && b >= 100 && b <= 180 && r > b && g > b) {
                        bool isNew = true;
                        foreach (var p in anchors) if (Math.Abs(p.X - x) < 200 && Math.Abs(p.Y - y) < 150) { isNew = false; break; }
                        if (isNew) anchors.Add(new Point(x, y));
                    }
                }
            }
        }
        bmp.UnlockBits(sData); return anchors;
    }

    static unsafe Bitmap EnhanceGreyText(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++) {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            if (r > 150 && g > 150 && b > 150 && Math.Abs(r - g) < 25 && Math.Abs(g - b) < 25) { d[i * 4] = 0; d[i * 4 + 1] = 0; d[i * 4 + 2] = 0; }
            else { d[i * 4] = 255; d[i * 4 + 1] = 255; d[i * 4 + 2] = 255; }
            d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData); return dst;
    }

    static unsafe Bitmap EnhanceBadgeText(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        byte* s = (byte*)sData.Scan0, d = (byte*)dData.Scan0;
        for (int i = 0; i < src.Width * src.Height; i++) {
            int b = s[i * 4], g = s[i * 4 + 1], r = s[i * 4 + 2];
            bool isText = (b > 160 && r < 140 && g < 180) || (r > 150 && g > 110 && b < 140) || (r > 180 && g > 180 && b > 180);
            if (isText) { d[i * 4] = 0; d[i * 4 + 1] = 0; d[i * 4 + 2] = 0; }
            else { d[i * 4] = 255; d[i * 4 + 1] = 255; d[i * 4 + 2] = 255; }
            d[i * 4 + 3] = 255;
        }
        src.UnlockBits(sData); dst.UnlockBits(dData); return dst;
    }

    static Bitmap Upscale(Bitmap src, int scale) { var dst = new Bitmap(src.Width * scale, src.Height * scale, PixelFormat.Format32bppArgb); using var g = Graphics.FromImage(dst); g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(src, 0, 0, dst.Width, dst.Height); return dst; }
}

// ── Ввід ──────────────────────────────────────────────────────────────────────
static class KeyInput
{
    static WinApi.INPUT Key(ushort vk, bool up) => new() { type = WinApi.INPUT_KEYBOARD, u = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = up ? WinApi.KEYEVENTF_KEYUP : 0 } } };
    public static void TypeCode(string code, bool turbo)
    {
        int size = Marshal.SizeOf<WinApi.INPUT>();
        if (turbo) {
            var inputs = new WinApi.INPUT[code.Length * 2];
            for (int i = 0; i < code.Length; i++) { ushort vk = (ushort)(0x30 + (code[i] - '0')); inputs[i * 2] = Key(vk, false); inputs[i * 2 + 1] = Key(vk, true); }
            WinApi.SendInput((uint)inputs.Length, inputs, size);
        } else {
            foreach (char c in code) { ushort vk = (ushort)(0x30 + (c - '0')); WinApi.SendInput(2, new[] { Key(vk, false), Key(vk, true) }, size); Thread.Sleep(20); }
        }
    }
}

static class MouseInput
{
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);
    public static void Click(int x, int y) { SetCursorPos(x, y); Thread.Sleep(50); mouse_event(0x0002, 0, 0, 0, 0); Thread.Sleep(50); mouse_event(0x0004, 0, 0, 0, 0); }
}

// ── Форма (гарячі клавіші та логіка) ──────────────────────────────────────────
class MainForm : Form
{
    const int HK_F8 = 2, HK_F9 = 3; const uint VK_F8 = 0x77, VK_F9 = 0x78;
    enum BotState { Idle, AutoPilot }
    volatile BotState currentState = BotState.Idle;
    Config cfg; Rectangle? codeZone = null;

    public MainForm(Config config)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Opacity = 0;
        cfg = config; if (cfg.Width > 0 && cfg.Height > 0) codeZone = new Rectangle(cfg.X, cfg.Y, cfg.Width, cfg.Height);
        Load += (_, _) => { Hide(); Init(); };
    }

    void Init()
    {
        WinApi.RegisterHotKey(Handle, HK_F8, 0, VK_F8); WinApi.RegisterHotKey(Handle, HK_F9, 0, VK_F9);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; currentState = BotState.Idle; Application.Exit(); };
        PrintStatus();
    }

    void PrintStatus()
    {
        Console.WriteLine("\n==================================================");
        Console.WriteLine($"  Бот готовий. Очікую команди.");
        Console.WriteLine("  F9  — УВІМКНУТИ ПОВНИЙ АВТОПІЛОТ");
        Console.WriteLine("  F9 (повторно) — Вимкнути");
        Console.WriteLine("  F8  — Змінити монітор");
        Console.WriteLine("  Ctrl+C — Вихід з програми");
        Console.WriteLine("==================================================\n");
    }

    void SmartToggle(bool turbo)
    {
        if (currentState != BotState.Idle) { currentState = BotState.Idle; Console.WriteLine("\n[система] >> АВТОПІЛОТ ВИМКНЕНО. Бот спить."); return; }
        currentState = BotState.AutoPilot;
        new Thread(() => RunAutoPilot(turbo)) { IsBackground = true }.Start();
    }

    void RunAutoPilot(bool turbo)
    {
        try
        {
            Console.WriteLine("\n  [система] >> АВТОПІЛОТ УВІМКНЕНО. Починаю пошук...");
            Console.WriteLine($"  [налаштування] Мін. ціна: {cfg.MinPrice}$ | Макс. тонни: {cfg.MaxTon} Т | LVL: {cfg.MinLvl}-{cfg.MaxLvl} | Типи: {string.Join(", ", cfg.Types)}");
            
            bool waitingForMenu = false; long clickTime = 0;

            while (currentState == BotState.AutoPilot)
            {
                if (!waitingForMenu)
                {
                    var cards = OrderScanner.FindCards(cfg.MonitorIndex);
                    if (cards.Count > 0)
                    {
                        var validCards = new List<OrderScanner.OrderCard>();
                        foreach (var c in cards)
                        {
                            bool okPrice = c.PricePerKm >= cfg.MinPrice;
                            // ОСЬ ТУТ НОВА ЛОГІКА: беремо все, що БІЛЬШЕ НУЛЯ, але МЕНШЕ АБО ДОРІВНЮЄ твоєму максимуму
                            bool okTon = c.Tonnage > 0 && c.Tonnage <= cfg.MaxTon; 
                            bool okLvl = c.Level >= cfg.MinLvl && c.Level <= cfg.MaxLvl;
                            bool okType = cfg.Types.Contains(c.Type);

                            if (okPrice && okTon && okLvl && okType) {
                                Console.WriteLine($"    ✅ {c.Type} ({c.Tonnage} т, {c.Level} LVL) -> {c.PricePerKm} $/km [ПІДХОДИТЬ]");
                                validCards.Add(c);
                            } else {
                                Console.WriteLine($"    ❌ {c.Type} ({c.Tonnage} т, {c.Level} LVL) -> {c.PricePerKm} $/km [Відхилено]");
                            }
                        }

                        if (validCards.Count > 0)
                        {
                            var bestCard = validCards.OrderByDescending(c => c.PricePerKm).First();
                            Console.WriteLine($"\n  🏆 НАЙКРАЩИЙ ВИБІР: {bestCard.Type} за {bestCard.PricePerKm} $/km!");
                            Console.WriteLine("  🖱️ Відкриваю замовлення...");
                            MouseInput.Click(bestCard.ClickPoint.X, bestCard.ClickPoint.Y);
                            
                            Thread.Sleep(600); 
                            var scr = WinOcr.GetMonitorBounds(cfg.MonitorIndex);
                            int takeBtnX = scr.X + scr.Width / 2 + (int)(scr.Width * 0.075);
                            int takeBtnY = scr.Y + scr.Height / 2 + (int)(scr.Height * 0.16);

                            Console.WriteLine("  🖱️ Натискаю кнопку 'Взяти замовлення'...");
                            MouseInput.Click(takeBtnX, takeBtnY);
                            
                            waitingForMenu = true; clickTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                            Console.WriteLine("  [система] Очікую на появу чорного меню з кодом...");
                        }
                        else
                        {
                             Console.WriteLine("  [пошук] Жодне замовлення не підійшло. Очікую на оновлення списку...");
                        }
                    }
                    Thread.Sleep(1000); 
                }
                else
                {
                    if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - clickTime > 5000) {
                        Console.WriteLine("  [помилка] Меню вводу коду не з'явилося за 5 секунд. Повертаюся до пошуку...");
                        waitingForMenu = false; continue;
                    }

                    if (codeZone == null) {
                        codeZone = WinOcr.FindDialogRegion(cfg.MonitorIndex);
                        if (codeZone != null) { cfg.X = codeZone.Value.X; cfg.Y = codeZone.Value.Y; cfg.Width = codeZone.Value.Width; cfg.Height = codeZone.Value.Height; cfg.Save(); }
                    }

                    if (codeZone != null) {
                        using var img = ScreenCapture.Capture(codeZone!.Value); 
                        var code = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();
                        if (code != null) {
                            Console.WriteLine($"  >> Знайдено код: {code} — Вводжу...");
                            Thread.Sleep(100); KeyInput.TypeCode(code, turbo);
                            Console.WriteLine("  >> Готово! Замовлення успішно взято.");
                            currentState = BotState.Idle;
                            Console.WriteLine("  [система] >> Автопілот перейшов у режим сну. Вдалої дороги! 🚚💨");
                            return; 
                        }
                    }
                    Thread.Sleep(50); 
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"\n[КРИТИЧНА ПОМИЛКА] Бот впав: {ex.Message}"); currentState = BotState.Idle; }
    }

    void ChangeMonitor() { cfg.MonitorIndex = Program.ChooseMonitor(); cfg.X = 0; cfg.Y = 0; cfg.Width = 0; cfg.Height = 0; cfg.Save(); codeZone = null; PrintStatus(); }
    protected override void WndProc(ref Message m) { if (m.Msg == WinApi.WM_HOTKEY) { if (m.WParam.ToInt32() == HK_F8) new Thread(ChangeMonitor) { IsBackground = true }.Start(); else if (m.WParam.ToInt32() == HK_F9) SmartToggle(true); } base.WndProc(ref m); }
    protected override void OnFormClosing(FormClosingEventArgs e) { currentState = BotState.Idle; WinApi.UnregisterHotKey(Handle, HK_F8); WinApi.UnregisterHotKey(Handle, HK_F9); base.OnFormClosing(e); }
}

// ── Точка входу та налаштування ───────────────────────────────────────────────
class Program
{
    const string OcrCapability = "Language.OCR~~~uk-UA~0.0.1.0";

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        EnsureUkrainianOcr();
        EnsureEngTessData();
        
        Config cfg = Config.Load();
        
        if (cfg.MonitorIndex == -1)
        {
            cfg.MonitorIndex = ChooseMonitor();
            cfg.Save();
        }
        else
        {
            Console.WriteLine($"\n[система] Автоматично обрано збережений монітор {cfg.MonitorIndex + 1}.");
        }

        // ЗАПУСКАЄМО МАЙСТЕР НАЛАШТУВАНЬ
        SetupFilters(cfg);

        Application.EnableVisualStyles();
        Application.Run(new MainForm(cfg));
    }

    static void SetupFilters(Config cfg)
    {
        Console.WriteLine("\n==================================================");
        Console.WriteLine("  НАЛАШТУВАННЯ ФІЛЬТРІВ");
        Console.WriteLine("  (Щоб залишити поточне значення або \"без різниці\" — просто натисніть Enter)");
        Console.WriteLine("==================================================");

        cfg.MinPrice = ReadInt($"Мінімальна ціна за км [Зараз: {cfg.MinPrice}]: ", cfg.MinPrice);
        
        // НОВЕ МЕНЮ ДЛЯ ТОННАЖУ
        Console.WriteLine("\n-- Вантажопідйомність (Тоннаж) --");
        Console.WriteLine("Оберіть максимальну вагу, яка влазить у вашу машину:");
        double[] allTons = { 0.5, 1.5, 3.0, 5.0 };
        for (int i = 0; i < allTons.Length; i++) Console.WriteLine($"  {i + 1}. до {allTons[i]} Т");
        
        Console.WriteLine($"\n[Зараз максимум: {cfg.MaxTon} Т]");
        Console.Write("Введіть номер (1-4) або Enter, щоб залишити як є: ");
        
        string tonInput = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(tonInput) && int.TryParse(tonInput, out int tIdx) && tIdx >= 1 && tIdx <= allTons.Length)
        {
            cfg.MaxTon = allTons[tIdx - 1];
        }

        Console.WriteLine("\n-- Рівень доступу --");
        cfg.MinLvl = ReadInt($"Мінімальний рівень [Зараз: {cfg.MinLvl}]: ", cfg.MinLvl);
        cfg.MaxLvl = ReadInt($"Максимальний рівень [Зараз: {cfg.MaxLvl}]: ", cfg.MaxLvl);

        Console.WriteLine("\n-- Типи вантажів --");
        string[] allTypes = { "Одяг", "Нафта", "Фармацевтика", "Різне", "Продукти", "Автозапчастини", "Інше" };
        for (int i = 0; i < allTypes.Length; i++) Console.WriteLine($"  {i + 1}. {allTypes[i]}");
        
        string currTypesStr = cfg.Types.Count == allTypes.Length ? "ВСІ" : string.Join(", ", cfg.Types);
        Console.WriteLine($"\n[Зараз обрано: {currTypesStr}]");
        Console.Write("Введіть номери через кому (наприклад 1,3,4) або Enter, щоб залишити як є: ");
        
        string input = Console.ReadLine()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(input))
        {
            var selected = new List<string>();
            foreach (var p in input.Split(','))
            {
                if (int.TryParse(p.Trim(), out int idx) && idx >= 1 && idx <= allTypes.Length)
                {
                    selected.Add(allTypes[idx - 1]);
                }
            }
            if (selected.Count > 0) cfg.Types = selected;
        }

        cfg.Save();
        Console.WriteLine("\n[система] Налаштування збережено!\n");
    }

    static int ReadInt(string prompt, int defaultVal)
    {
        Console.Write(prompt);
        string val = Console.ReadLine() ?? "";
        if (string.IsNullOrWhiteSpace(val)) return defaultVal;
        if (int.TryParse(val, out int res)) return res;
        return defaultVal;
    }

    public static int ChooseMonitor()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 1) return 0;
        Console.WriteLine("\n[монітор] Знайдено кілька моніторів:");
        for (int i = 0; i < screens.Length; i++)
        {
            var b = screens[i].Bounds;
            Console.WriteLine($"  {i + 1}. {WinApi.GetMonitorFriendlyName(screens[i].DeviceName)} — {b.Width}x{b.Height} @ ({b.X},{b.Y}){(screens[i].Primary ? " (основний)" : "")}");
        }
        Console.Write($"[монітор] Введіть номер монітора з грою (1-{screens.Length}): ");
        while (true)
        {
            var key = Console.ReadKey(true);
            if (int.TryParse(key.KeyChar.ToString(), out int n) && n >= 1 && n <= screens.Length)
            {
                Console.WriteLine(n); return n - 1;
            }
        }
    }

    static void EnsureEngTessData()
    {
        string tPath = WinOcr.TessDataPath; Directory.CreateDirectory(tPath);
        string ePath = Path.Combine(tPath, "eng.traineddata");
        if (!File.Exists(ePath))
        {
            Console.WriteLine("[tess] Завантажую англійський словник...");
            try { File.WriteAllBytes(ePath, new System.Net.Http.HttpClient().GetByteArrayAsync("https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/eng.traineddata").Result); }
            catch (Exception ex) { Console.WriteLine($"[tess] ПОМИЛКА: {ex.Message}"); }
        }
    }

    static void EnsureUkrainianOcr()
    {
        var ukLang = new Windows.Globalization.Language("uk-UA");
        if (OcrEngine.TryCreateFromLanguage(ukLang) != null) return;
        Console.WriteLine("[ocr] Встановлюю Ukrainian OCR пакет...");
        var install = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = "powershell", Arguments = $"-NoProfile -Command \"Add-WindowsCapability -Online -Name 'Language.OCR~~~uk-UA~0.0.1.0'\"", CreateNoWindow = true })!;
        install.WaitForExit();
        if (OcrEngine.TryCreateFromLanguage(ukLang) != null) { System.Diagnostics.Process.Start(Environment.ProcessPath!); Environment.Exit(0); }
    }
}