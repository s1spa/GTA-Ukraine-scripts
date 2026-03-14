using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
    public int X1 = 704, Y1 = 229, X2 = 1213, Y2 = 841;

    static string FilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.txt");

    public static Config Load()
    {
        if (!File.Exists(FilePath))
        {
            File.WriteAllText(FilePath,
                "# Координати вікна підтвердження\nx1 = 704\ny1 = 229\nx2 = 1213\ny2 = 841\n");
            Console.WriteLine("[config] Створено config.txt з дефолтними координатами");
        }

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
            }
        }
        return cfg;
    }

    public void Save() =>
        File.WriteAllText(FilePath,
            $"# Координати вікна підтвердження\nx1 = {X1}\ny1 = {Y1}\nx2 = {X2}\ny2 = {Y2}\n");
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

// ── OCR через вбудований Windows.Media.Ocr ───────────────────────────────────

static class WinOcr
{
    // OcrEngine — вбудований в Windows 10/11, нічого качати не треба
    static readonly OcrEngine Engine =
        OcrEngine.TryCreateFromUserProfileLanguages()   // мова системи
        ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
        ?? throw new Exception("Windows OCR недоступний");

    // Bitmap → SoftwareBitmap (через PNG в пам'яті)
    static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
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
}

// ── Введення клавіатурою через SendInput ──────────────────────────────────────

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
    const int HK_F7 = 1, HK_F9 = 3;
    const uint VK_F7 = 0x76, VK_F9 = 0x78;
    const int MARGIN = 20;

    Config cfg = null!;
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
        cfg = Config.Load();
        WinApi.RegisterHotKey(Handle, HK_F7, 0, VK_F7);
        WinApi.RegisterHotKey(Handle, HK_F9, 0, VK_F9);

        Console.WriteLine(new string('=', 50));
        Console.WriteLine($"  Зона: x={cfg.X1}-{cfg.X2}, y={cfg.Y1}-{cfg.Y2}");
        Console.WriteLine("  F7  — калібрування координат");
        Console.WriteLine("  F9  — авто-скан (turbo)");
        Console.WriteLine("  F9 повторно — зупинити");
        Console.WriteLine("  Ctrl+C — вихід");
        Console.WriteLine(new string('=', 50));

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; scanning = false; Application.Exit(); };
    }

    Rectangle GetRegion() => new(
        cfg.X1 - MARGIN, cfg.Y1 - MARGIN,
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

    void Calibrate()
    {
        Console.WriteLine("\n[Калібрування]");
        Console.WriteLine("  Наведи мишку на ЛІВИЙ ВЕРХНІЙ кут UI і натисни Enter");
        WaitForGlobalEnter();
        WinApi.GetCursorPos(out var p1);
        cfg.X1 = p1.X; cfg.Y1 = p1.Y;
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
                case HK_F7: new Thread(Calibrate) { IsBackground = true }.Start(); break;
                case HK_F9: Toggle(true);  break;
            }
        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        scanning = false;
        WinApi.UnregisterHotKey(Handle, HK_F7);
        WinApi.UnregisterHotKey(Handle, HK_F9);
        base.OnFormClosing(e);
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────

class Program
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }
}
