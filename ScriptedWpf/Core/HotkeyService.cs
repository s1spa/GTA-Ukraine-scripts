using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScriptedWpf.Core;

public sealed class HotkeyService : IDisposable
{
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    const int WM_HOTKEY = 0x0312;

    private readonly IntPtr _hwnd;
    private HwndSource?     _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 100;

    public HotkeyService(Window window)
    {
        _source = (HwndSource)PresentationSource.FromVisual(window);
        _hwnd   = _source.Handle;
        _source.AddHook(WndProc);
    }

    public int Register(uint modifiers, uint vk, Action handler)
    {
        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, modifiers, vk))
            _handlers[id] = handler;
        return id;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var cb))
        {
            cb();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source?.RemoveHook(WndProc);
        foreach (var id in _handlers.Keys)
            UnregisterHotKey(_hwnd, id);
        _handlers.Clear();
    }
}
