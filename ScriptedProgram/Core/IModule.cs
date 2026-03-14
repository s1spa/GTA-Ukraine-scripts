using System;
using System.Windows.Forms;

namespace ScriptedProgram.Core;

public interface IModule
{
    string Id { get; }
    bool IsRunning { get; }

    /// <summary>Called once after loading. Provides a log callback for terminal output.</summary>
    void Initialize(Action<string> log);

    void Start();
    void Stop();

    /// <summary>Called once the window handle is ready. Register global hotkeys here.</summary>
    void RegisterHotkeys(HotkeyService hotkeys) { }

    /// <summary>Returns a WinForms Control with module-specific settings UI, or null.</summary>
    Control? GetSettingsControl();
}
