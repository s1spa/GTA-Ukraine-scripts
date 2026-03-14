using System.Runtime.InteropServices;

namespace ScriptedProgram.Core;

public static class WinApi
{
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(nint hWnd, int id, uint mod, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(nint hWnd, int id);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern int SendMessage(nint hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, uint dwExtraInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    public static extern nint GetModuleHandle(string? lpModuleName);

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
        return EnumDisplayDevices(deviceName, 0, ref dd, 0) ? dd.DeviceString : deviceName;
    }

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)]   public struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public nint dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy, mouseData; public uint dwFlags, time; public nint dwExtraInfo; }

    public const uint INPUT_KEYBOARD   = 1;
    public const uint KEYEVENTF_KEYUP  = 0x0002;
    public const int  WM_HOTKEY        = 0x0312;
    public const int  WM_NCLBUTTONDOWN = 0xA1;
    public const int  HTCAPTION        = 0x2;
}
