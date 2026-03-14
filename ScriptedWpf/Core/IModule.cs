using System;
using System.Windows;

namespace ScriptedWpf.Core;

public interface IModule
{
    string Id { get; }
    bool IsRunning { get; }

    /// <summary>Fires on UI thread whenever IsRunning changes (Start/Stop/hotkey).</summary>
    event Action? StateChanged;

    void Initialize(Action<string> log);
    void Start();
    void Stop();

    void RegisterHotkeys(HotkeyService hotkeys) { }

    /// <summary>Returns a WPF FrameworkElement with module settings UI, or null.</summary>
    FrameworkElement? GetSettingsView();
}
