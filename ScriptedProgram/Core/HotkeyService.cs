using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ScriptedProgram.Core;

public sealed class HotkeyService : IDisposable
{
    readonly nint _hwnd;
    readonly Dictionary<int, Action> _handlers = new();
    int _nextId = 100;

    public HotkeyService(nint hwnd) => _hwnd = hwnd;

    /// <summary>Registers a hotkey. Returns the assigned id (use to unregister later).</summary>
    public int Register(uint modifiers, uint vk, Action handler)
    {
        int id = _nextId++;
        if (WinApi.RegisterHotKey(_hwnd, id, modifiers, vk))
            _handlers[id] = handler;
        return id;
    }

    public void Unregister(int id)
    {
        WinApi.UnregisterHotKey(_hwnd, id);
        _handlers.Remove(id);
    }

    public void ProcessMessage(ref Message m)
    {
        if (m.Msg == WinApi.WM_HOTKEY && _handlers.TryGetValue(m.WParam.ToInt32(), out var h))
            h();
    }

    public void Dispose()
    {
        foreach (var id in _handlers.Keys)
            WinApi.UnregisterHotKey(_hwnd, id);
    }
}
