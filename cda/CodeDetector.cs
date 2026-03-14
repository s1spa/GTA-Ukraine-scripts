using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct DISPLAY_DEVICE
    {
        public uint cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]  public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    public static string GetMonitorFriendlyName(string deviceName)
    {
        var dd = new DISPLAY_DEVICE { cb = (uint)Marshal.SizeOf<DISPLAY_DEVICE>() };
        if (EnumDisplayDevices(deviceName, 0, ref dd, 0))
            return dd.DeviceString;
        return deviceName;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN     = 0x0100;
    public const int VK_RETURN      = 0x0D;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    public const uint INPUT_KEYBOARD  = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const int  WM_HOTKEY       = 0x0312;
}

// ── Config ────────────────────────────────────────────────────────────────────

class Config
{
    public int X1, Y1, X2, Y2;
    public int MonitorIndex = -1; // -1 = не обрано (використовуємо Primary)

    static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

    // Повертає null якщо файл не існує (треба авто-калібровка)
    public static Config? Load()
    {
        if (!File.Exists(FilePath)) return null;

        var cfg = new Config();
        foreach (var line in File.ReadAllLines(FilePath))
        {
            var l = line.Trim();
            if (l.StartsWith('#') || !l.Contains('=')) continue;
            var parts = l.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1].Trim(), out int v)) continue;
            switch (parts[0].Trim())
            {
                case "x1": cfg.X1 = v; break; case "y1": cfg.Y1 = v; break;
                case "x2": cfg.X2 = v; break; case "y2": cfg.Y2 = v; break;
                case "monitor": cfg.MonitorIndex = v; break;
            }
        }
        return cfg;
    }

    public void Save() =>
        File.WriteAllText(FilePath,
            $"# Координати вікна підтвердження\nmonitor = {MonitorIndex}\nx1 = {X1}\ny1 = {Y1}\nx2 = {X2}\ny2 = {Y2}\n");
}

// ── Скрін + препроцесинг ──────────────────────────────────────────────────────

static class ScreenCapture
{
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        return bmp;
    }

    // Upscale ×2 — краща якість для OCR тексту при калібруванні
    public static Bitmap CaptureUpscaled(Rectangle r, int scale = 2)
    {
        using var raw = Capture(r);
        var dst = new Bitmap(raw.Width * scale, raw.Height * scale, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(raw, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // grayscale + contrast ×3 + binarize (як PIL Enhance + point)
    public static unsafe Bitmap Preprocess(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);

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
                dRow[x * 4 + 0] = bw;
                dRow[x * 4 + 1] = bw;
                dRow[x * 4 + 2] = bw;
                dRow[x * 4 + 3] = 255;
            }
        }

        src.UnlockBits(sData);
        dst.UnlockBits(dData);
        return dst;
    }
}

// ── Відстань Левенштейна ──────────────────────────────────────────────────────

static class Levenshtein
{
    public static int Distance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = a[i-1] == b[j-1]
                    ? d[i-1, j-1]
                    : 1 + Math.Min(d[i-1, j-1], Math.Min(d[i-1, j], d[i, j-1]));
        return d[a.Length, b.Length];
    }
}

// ── OCR через вбудований Windows.Media.Ocr ───────────────────────────────────

static class WinOcr
{
    // Lazy — пробуємо uk-UA, потім ru (теж кирилиця), потім системний
    static OcrEngine? _engine;
    static OcrEngine Engine => _engine ??= CreateEngine();
    static OcrEngine CreateEngine()
    {
        foreach (var tag in new[] { "uk-UA", "ru", "ru-RU" })
        {
            var e = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag));
            if (e != null) { Console.WriteLine($"[ocr] Використовую OCR: {tag}"); return e; }
        }
        var fallback = OcrEngine.TryCreateFromUserProfileLanguages()
                    ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                    ?? throw new Exception("Windows OCR недоступний");
        Console.WriteLine("[ocr] Використовую системний OCR (не кирилиця)");
        return fallback;
    }

    // Bitmap → SoftwareBitmap (через PNG в пам'яті)
    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
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
        using var processed = ScreenCapture.Preprocess(img);
        using var soft      = await ToSoftwareBitmapAsync(processed);

        var result = await Engine.RecognizeAsync(soft);
        var text   = result.Text;

        // "1 2 3 4 5 6" → "123456"
        var spaced = Regex.Match(text, @"(\d)\s+(\d)\s+(\d)\s+(\d)\s+(\d)\s+(\d)");
        if (spaced.Success)
            return string.Concat(
                spaced.Groups[1].Value, spaced.Groups[2].Value,
                spaced.Groups[3].Value, spaced.Groups[4].Value,
                spaced.Groups[5].Value, spaced.Groups[6].Value);

        var codes = Regex.Matches(text, @"\b\d{6}\b");
        return codes.Count > 0 ? codes[0].Value : null;
    }

    // Шукаємо унікальний рядок діалогу — може бути розбитий на 2-3 рядки OCR
    
    static string TessDataPath => Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath!) ?? AppDomain.CurrentDomain.BaseDirectory,
        "tessdata");

    public static Rectangle GetMonitorBounds(int monitorIndex)
    {
        var screens = Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length)
            return screens[monitorIndex].Bounds;
        return Screen.PrimaryScreen!.Bounds;
    }

    // Сканує екран (вказаний монітор або Primary) через Tesseract ukr, шукає діалог підтвердження.
    // Повертає координати зони з кодом (рядок цифр нижче заголовка) або null.
    // Нормалізація контрасту + бінаризація для низькоконтрастного тексту
    static unsafe Bitmap EnhanceContrast(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        var sData = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                        ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dData = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height),
                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        byte* s = (byte*)sData.Scan0;
        byte* d = (byte*)dData.Scan0;
        int total = src.Width * src.Height;

        for (int i = 0; i < total; i++)
        {
            int b = s[i * 4];
            int g = s[i * 4 + 1];
            int r = s[i * 4 + 2];

            // Беремо найяскравіший канал з трьох (RGB)
            int maxChannel = Math.Max(b, Math.Max(g, r));

            // СУПЕР-ФІЛЬТР: 
            // Фон (#0c0d0d) має канали ~12-13 -> робимо його повністю БІЛИМ (255)
            // Темний текст (#0c1b37, #555537) має канали 27-85 -> робимо повністю ЧОРНИМ (0)
            // Білі цифри (255) теж стануть чорними, що нам і треба.
            byte bw = maxChannel < 20 ? (byte)255 : (byte)0;

            d[i * 4 + 0] = bw;
            d[i * 4 + 1] = bw;
            d[i * 4 + 2] = bw;
            d[i * 4 + 3] = 255;
        }

        src.UnlockBits(sData);
        dst.UnlockBits(dData);
        return dst;
    }

    static List<(string raw, string norm, Rect bounds)> ScanLines(TesseractEngine engine, Bitmap bmp, bool enhance)
    {
        Bitmap processed = enhance ? EnhanceContrast(bmp) : bmp;

        try
        {
            using var ms = new MemoryStream();
            processed.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;
            using var pix  = Pix.LoadFromMemory(ms.ToArray());
            using var page = engine.Process(pix, PageSegMode.Auto);
            using var iter = page.GetIterator();

            var lines = new List<(string raw, string norm, Rect bounds)>();
            iter.Begin();
            do
            {
                if (!iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds)) continue;
                var text = iter.GetText(PageIteratorLevel.TextLine) ?? "";
                var norm = Regex.Replace(text.ToLowerInvariant(), @"[^\w\s]", " ").Trim();
                norm = Regex.Replace(norm, @"\s+", " ");
                if (norm.Length >= 5)
                    lines.Add((text.Trim(), norm, bounds));
            }
            while (iter.Next(PageIteratorLevel.TextLine));
            return lines;
        }
        finally
        {
            if (enhance) processed.Dispose();
        }
    }

public static Rectangle? FindDialogRegion(int monitorIndex = -1)
    {
        var screen = GetMonitorBounds(monitorIndex);
        using var bmp = ScreenCapture.Capture(screen);
        using var engine = new TesseractEngine(TessDataPath, "ukr", EngineMode.Default);

        Console.WriteLine("  [tess] Скан 1/2: оригінал...");
        var linesNormal = ScanLines(engine, bmp, enhance: false);
        Console.WriteLine("  [tess] Скан 2/2: контраст...");
        var linesInverted = ScanLines(engine, bmp, enhance: true);

        string target = "введіть код підтвердження щоб взяти замовлення";

        // Локальна функція: шукає найкращий збіг тільки в межах ОДНОГО скану
        (int dist, string text, Rect bounds) FindBestMatch(List<(string raw, string norm, Rect bounds)> lines)
        {
            int bestDist = int.MaxValue;
            Rect bestBounds = default;
            string bestText = "";

            lines.Sort((a, b) => a.bounds.Y1.CompareTo(b.bounds.Y1));

            for (int i = 0; i < lines.Count; i++)
            {
                for (int len = 1; len <= 2 && i + len - 1 < lines.Count; len++)
                {
                    var parts = new string[len];
                    for (int k = 0; k < len; k++) parts[k] = lines[i + k].norm;
                    string combined = string.Join(" ", parts);

                    if (combined.Length < 10) continue;

                    // ВАЖЛИВО: Жорсткий фільтр!
                    // Якщо в тексті немає унікальних слів з нашого меню, відкидаємо відразу.
                    // Це врятує нас від хибного спрацювання на кнопці "взяти замовлення".
                    if (!combined.Contains("код") && !combined.Contains("підтверд") && !combined.Contains("введіть"))
                        continue;

                    // Відкидаємо цифри, щоб порівнювати ТІЛЬКИ текст підпису
                    string textNoDigits = Regex.Replace(combined, @"\d+", "").Trim();
                    int dist = Levenshtein.Distance(textNoDigits, target);

                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestText = combined;
                        var b0 = lines[i].bounds;
                        var b1 = lines[i + len - 1].bounds;
                        bestBounds = new Rect(
                            Math.Min(b0.X1, b1.X1), b0.Y1,
                            Math.Max(b0.X2, b1.X2) - Math.Min(b0.X1, b1.X1), 
                            b1.Y2 - b0.Y1);
                    }
                }
            }
            return (bestDist, bestText, bestBounds);
        }

        // Шукаємо незалежно у двох варіантах картинки
        var match1 = FindBestMatch(linesNormal);
        var match2 = FindBestMatch(linesInverted);

        // Беремо той, який прочитався без помилок (або з мінімальними)
        var best = match1.dist < match2.dist ? match1 : match2;

        // Якщо нічого не знайшли (всі рядки відсіялися фільтром), dist буде int.MaxValue
        if (best.dist > 22 || best.text == "")
        {
            Console.WriteLine("  [auto-cal] Меню вводу коду не знайдено на екрані.");
            return null;
        }

        Console.WriteLine($"  [auto-cal] Знайдено меню (dist={best.dist}): \"{best.text}\"");

        // Задаємо ідеальну зону сканування (якраз там, де ти обвів білі цифри)
        int zoneW = 600; 
        int zoneX = (bmp.Width - zoneW) / 2; 
        int zoneY = best.bounds.Y1;
        int zoneH = 80;

        float scaleX = (float)bmp.Width / screen.Width;
        float scaleY = (float)bmp.Height / screen.Height;

        int x1 = screen.X + (int)(zoneX / scaleX);
        int y1 = screen.Y + (int)(zoneY / scaleY);
        int x2 = screen.X + (int)((zoneX + zoneW) / scaleX);
        int y2 = screen.Y + (int)((zoneY + zoneH) / scaleY);

        Console.WriteLine($"  [auto-cal] Зона коду: x={x1}-{x2}, y={y1}-{y2}");
        return new Rectangle(x1, y1, x2 - x1, y2 - y1);
    }




static class KeyInput
{
    static WinApi.INPUT Key(ushort vk, bool up) => new()
    {
        type = WinApi.INPUT_KEYBOARD,
        u = new WinApi.INPUTUNION
        {
            ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = up ? WinApi.KEYEVENTF_KEYUP : 0 }
        }
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
                inputs[i * 2]     = Key(vk, false);
                inputs[i * 2 + 1] = Key(vk, true);
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

// ── Форма (прихована, обробляє WM_HOTKEY) ────────────────────────────────────

class MainForm : Form
{
    const int HK_F7 = 1, HK_F8 = 2, HK_F9 = 3;
    const uint VK_F7 = 0x76, VK_F8 = 0x77, VK_F9 = 0x78;
    const int MARGIN = 20;

    Config? cfg;
    volatile bool scanning;
    string? lastCode;

    public MainForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        WindowState     = FormWindowState.Minimized;
        Opacity         = 0;
        Load += (_, _) => { Hide(); Init(); };
    }

    void Init()
    {
        WinApi.RegisterHotKey(Handle, HK_F7, 0, VK_F7);
        WinApi.RegisterHotKey(Handle, HK_F8, 0, VK_F8);
        WinApi.RegisterHotKey(Handle, HK_F9, 0, VK_F9);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; scanning = false; Application.Exit(); };

        cfg = Config.Load();
        if (cfg == null)
        {
            Console.WriteLine("[auto-cal] config.txt не знайдено — шукаю діалог на екрані...");
            new Thread(AutoCalibrate) { IsBackground = true }.Start();
        }
        else
        {
            PrintStatus();
        }
    }

    static int ChooseMonitor()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 1) return 0;

        Console.WriteLine("\n[монітор] Знайдено кілька моніторів:");
        for (int i = 0; i < screens.Length; i++)
        {
            string primary = screens[i].Primary ? " (основний)" : "";
            var b = screens[i].Bounds;
            string orientation = b.Height > b.Width ? "вертикальний" : "горизонтальний";
            string model = WinApi.GetMonitorFriendlyName(screens[i].DeviceName);
            Console.WriteLine($"  {i + 1}. {model} — {b.Width}x{b.Height} {orientation} @ ({b.X},{b.Y}){primary}");
        }
        Console.Write($"[монітор] Введіть номер монітора з грою (1-{screens.Length}): ");

        while (true)
        {
            var key = Console.ReadKey(true);
            if (int.TryParse(key.KeyChar.ToString(), out int n) && n >= 1 && n <= screens.Length)
            {
                Console.WriteLine(n.ToString());
                Console.WriteLine($"[монітор] Обрано монітор {n}: {WinApi.GetMonitorFriendlyName(screens[n - 1].DeviceName)}");
                return n - 1;
            }
        }
    }

    void AutoCalibrate()
    {
        int monitorIndex = ChooseMonitor();
        Console.WriteLine("[auto-cal] Очікую вікно замовлення на екрані...");
        Rectangle? region = null;
        while (region == null)
        {
            region = WinOcr.FindDialogRegion(monitorIndex);
            if (region == null) Thread.Sleep(500);
        }

        cfg = new Config
        {
            MonitorIndex = monitorIndex,
            X1 = region.Value.Left,
            Y1 = region.Value.Top,
            X2 = region.Value.Right,
            Y2 = region.Value.Bottom,
        };
        cfg.Save();
        Console.WriteLine($"[auto-cal] Збережено: x={cfg.X1}-{cfg.X2}, y={cfg.Y1}-{cfg.Y2}");
        PrintStatus();
    }

    void PrintStatus()
    {
        var screens = Screen.AllScreens;
        string monitorName = (cfg!.MonitorIndex >= 0 && cfg.MonitorIndex < screens.Length)
            ? $"Монітор {cfg.MonitorIndex + 1} ({WinApi.GetMonitorFriendlyName(screens[cfg.MonitorIndex].DeviceName)})"
            : "основний";
        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"  Монітор: {monitorName}");
        Console.WriteLine($"  Зона: x={cfg.X1}-{cfg.X2}, y={cfg.Y1}-{cfg.Y2}");
        Console.WriteLine("  F7  — ручне калібрування");
        Console.WriteLine("  F8  — змінити монітор");
        Console.WriteLine("  F9  — авто-скан (turbo)");
        Console.WriteLine("  F9 повторно — зупинити");
        Console.WriteLine("  Ctrl+C — вихід");
        Console.WriteLine(new string('=', 50));
    }

    Rectangle GetRegion() => new(
        cfg!.X1 - MARGIN, cfg.Y1 - MARGIN,
        (cfg.X2 - cfg.X1) + MARGIN * 2,
        (cfg.Y2 - cfg.Y1) + MARGIN * 2);

    void ScanLoop(bool turbo)
    {
        lastCode = null;
        long cooldownUntil = 0;
        int noCodeStreak = 0;

        while (scanning)
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < cooldownUntil)
            { Thread.Sleep(50); continue; }

            using var img = ScreenCapture.Capture(GetRegion());
            // WinRT async → блокуємо потік (ми і так в background thread)
            var code = WinOcr.FindCodeAsync(img).GetAwaiter().GetResult();

            if (code != null)
            {
                noCodeStreak = 0;
                if (code != lastCode)
                {
                    Console.WriteLine($"  >> Код: {code} — вводжу...");
                    Thread.Sleep(100);
                    KeyInput.TypeCode(code, turbo);
                    Console.WriteLine("  >> Готово.");
                    lastCode = code;
                    cooldownUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 2000;
                }
            }
            else
            {
                if (++noCodeStreak >= 5) { lastCode = null; noCodeStreak = 0; }
            }
        }
        Console.WriteLine("Авто-скан зупинено.");
    }

    void Toggle(bool turbo)
    {
        if (scanning) { scanning = false; Console.WriteLine("Зупиняю авто-скан..."); }
        else
        {
            scanning = true;
            Console.WriteLine($"Авто-скан [{(turbo ? "turbo" : "fast")}] запущено...");
            new Thread(() => ScanLoop(turbo)) { IsBackground = true }.Start();
        }
    }

    // Глобальний Enter — працює навіть коли консоль не у фокусі
    void WaitForGlobalEnter()
    {
        var evt  = new System.Threading.ManualResetEventSlim(false);
        IntPtr hook = IntPtr.Zero;
        WinApi.LowLevelKeyboardProc? proc = null;

        proc = (nCode, wParam, lParam) =>
        {
            if (nCode >= 0 && wParam == (IntPtr)WinApi.WM_KEYDOWN)
                if (Marshal.ReadInt32(lParam) == WinApi.VK_RETURN)
                    evt.Set();
            return WinApi.CallNextHookEx(hook, nCode, wParam, lParam);
        };

        // Хук треба ставити з UI-потоку (де крутиться message loop)
        Invoke(() =>
        {
            var mod = System.Diagnostics.Process.GetCurrentProcess().MainModule!;
            hook = WinApi.SetWindowsHookEx(WinApi.WH_KEYBOARD_LL, proc,
                       WinApi.GetModuleHandle(mod.ModuleName), 0);
        });

        evt.Wait();
        Invoke(() => WinApi.UnhookWindowsHookEx(hook));
        GC.KeepAlive(proc);
    }

    void ChangeMonitor()
    {
        int monitorIndex = ChooseMonitor();
        if (cfg == null) cfg = new Config();
        cfg.MonitorIndex = monitorIndex;
        // Скидаємо координати — потрібна повторна авто-калібровка
        cfg.X1 = cfg.Y1 = cfg.X2 = cfg.Y2 = 0; // скидаємо зону
        File.Delete(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt"));
        Console.WriteLine("[монітор] config.txt видалено — запускаю авто-калібровку...");
        AutoCalibrate();
    }

    void Calibrate()
    {
        Console.WriteLine("\n[Калібрування]");
        Console.WriteLine("  Наведи мишку на ЛІВИЙ ВЕРХНІЙ кут UI і натисни Enter");
        WaitForGlobalEnter();
        WinApi.GetCursorPos(out var p1);
        cfg!.X1 = p1.X; cfg.Y1 = p1.Y;
        Console.WriteLine($"  >> Лівий верхній: x={cfg.X1}, y={cfg.Y1}");

        Console.WriteLine("  Наведи мишку на ПРАВИЙ НИЖНІЙ кут UI і натисни Enter");
        WaitForGlobalEnter();
        WinApi.GetCursorPos(out var p2);
        cfg.X2 = p2.X; cfg.Y2 = p2.Y;
        Console.WriteLine($"  >> Правий нижній: x={cfg.X2}, y={cfg.Y2}");

        cfg.Save();
        Console.WriteLine("  Збережено. Перезапусти програму.\n");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WinApi.WM_HOTKEY)
            switch (m.WParam.ToInt32())
            {
                case HK_F7: new Thread(Calibrate)      { IsBackground = true }.Start(); break;
                case HK_F8: new Thread(ChangeMonitor)  { IsBackground = true }.Start(); break;
                case HK_F9: Toggle(true);  break;
            }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        scanning = false;
        WinApi.UnregisterHotKey(Handle, HK_F7);
        WinApi.UnregisterHotKey(Handle, HK_F8);
        WinApi.UnregisterHotKey(Handle, HK_F9);
        base.OnFormClosing(e);
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────

class Program
{
    const string OcrCapability = "Language.OCR~~~uk-UA~0.0.1.0";

    [STAThread]
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        EnsureUkrainianOcr();
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }

    static void EnsureUkrainianOcr()
    {
        // Діагностика — показуємо всі доступні OCR мови
        Console.Write("[ocr] Доступні мови:");
        foreach (var l in OcrEngine.AvailableRecognizerLanguages)
            Console.Write($" {l.LanguageTag}");
        Console.WriteLine();

        // Перевіряємо реально — чи вдається створити uk-UA engine
        var ukLang = new Windows.Globalization.Language("uk-UA");
        var testEngine = OcrEngine.TryCreateFromLanguage(ukLang);
        if (testEngine != null)
        {
            Console.WriteLine("[ocr] uk-UA OCR доступний.");
            return;
        }

        Console.WriteLine("[ocr] Встановлюю Ukrainian OCR пакет...");
        var install = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"Add-WindowsCapability -Online -Name '{OcrCapability}'\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        install.StandardOutput.ReadToEnd(); // drain stdout
        string installErr = install.StandardError.ReadToEnd().Trim();
        install.WaitForExit();
        Console.WriteLine($"[ocr] exitcode={install.ExitCode}");
        if (!string.IsNullOrEmpty(installErr)) Console.WriteLine($"[ocr] err={installErr}");

        // Перевіряємо знову після встановлення
        testEngine = OcrEngine.TryCreateFromLanguage(ukLang);
        if (testEngine != null)
        {
            Console.WriteLine("[ocr] Готово. Перезапускаю програму...");
            System.Diagnostics.Process.Start(Environment.ProcessPath!);
            Environment.Exit(0);
        }

        Console.WriteLine("[ocr] uk-UA OCR недоступний — використовую системний OCR.");
    }
}   }
