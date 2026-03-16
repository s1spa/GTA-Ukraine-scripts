using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScriptedWpf.Core;

namespace ScriptedWpf.Cogs.AntiAfk;

public sealed class AntiAfkModule : IModule
{
    static readonly Random   _rng     = new();
    static readonly ushort[] _vkWasd  = { 0x57, 0x41, 0x53, 0x44 }; // W A S D
    static readonly string[] _nmWasd  = { "W", "A", "S", "D" };
    const ushort VK_A = 0x41;
    const ushort VK_D = 0x44;

    volatile bool  _running;
    Action<string> _log = Console.WriteLine;
    AntiAfkConfig  _cfg = AntiAfkConfig.Load();

    // ── IModule ───────────────────────────────────────────────────────────────
    public string Id        => "antiafk";
    public bool   IsRunning => _running;

    public event Action? StateChanged;

    public void Initialize(Action<string> log) => _log = log;

    public void Start()
    {
        if (_running) return;
        _running = true;
        new Thread(Loop) { IsBackground = true }.Start();
        ShowStartNotification();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _log("Anti-AFK вимкнено.");
        ShowStopNotification();
        StateChanged?.Invoke();
    }

    void ShowStartNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Anti-AFK", "Вмикаюсь...", ToastIcon.Success);
    }

    void ShowStopNotification()
    {
        if (_cfg.ShowNotifications) ToastNotifier.Show("Anti-AFK", "Вимикаюсь...", ToastIcon.Info);
    }

    // ── Settings View ─────────────────────────────────────────────────────────
    public FrameworkElement? GetSettingsView()
    {
        var textSec = (Brush)Application.Current.Resources["TextSecondaryBrush"];

        var panel = new StackPanel { Orientation = Orientation.Vertical };

        var carCb = new CheckBox
        {
            Content    = "Режим в машині  (тільки A → D)",
            IsChecked  = _cfg.CarMode,
            Foreground = textSec,
        };
        carCb.Checked   += (_, _) => { _cfg.CarMode = true;  _cfg.Save(); };
        carCb.Unchecked += (_, _) => { _cfg.CarMode = false; _cfg.Save(); };

        var notifCb = new CheckBox
        {
            Content    = "Показувати сповіщення",
            IsChecked  = _cfg.ShowNotifications,
            Foreground = textSec,
            Margin     = new Thickness(0, 8, 0, 0),
        };
        notifCb.Checked   += (_, _) => { _cfg.ShowNotifications = true;  _cfg.Save(); };
        notifCb.Unchecked += (_, _) => { _cfg.ShowNotifications = false; _cfg.Save(); };

        panel.Children.Add(carCb);
        panel.Children.Add(notifCb);
        return panel;
    }

    // ── Main loop ─────────────────────────────────────────────────────────────
    void Loop()
    {
        _log($"Anti-AFK увімкнено. Режим: {(_cfg.CarMode ? "в машині (A→D)" : "пішки (WASD)")}. Інтервал: 5–10 хв.");

        while (_running)
        {
            int waitMs  = _rng.Next(5 * 60_000, 10 * 60_000 + 1);
            int elapsed = 0;
            while (_running && elapsed < waitMs)
            {
                Thread.Sleep(500);
                elapsed += 500;
            }
            if (!_running) break;

            if (_cfg.CarMode)
            {
                // Car mode: press A then D
                int holdA = _rng.Next(50, 201);
                int holdD = _rng.Next(50, 201);
                int gap   = _rng.Next(80, 201);
                _log($"[Anti-AFK] Машина: [A] {holdA}мс → пауза {gap}мс → [D] {holdD}мс");
                PressKey(VK_A, holdA);
                Thread.Sleep(gap);
                PressKey(VK_D, holdD);
            }
            else
            {
                int idx    = _rng.Next(_vkWasd.Length);
                int holdMs = _rng.Next(50, 201);
                _log($"[Anti-AFK] Натискаю [{_nmWasd[idx]}] на {holdMs} мс");
                PressKey(_vkWasd[idx], holdMs);
            }
        }
    }

    static void PressKey(ushort vk, int holdMs)
    {
        int sz = Marshal.SizeOf<WinApi.INPUT>();

        var down = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk } }
        };
        var up = new WinApi.INPUT
        {
            type = WinApi.INPUT_KEYBOARD,
            u    = new WinApi.INPUTUNION { ki = new WinApi.KEYBDINPUT { wVk = vk, dwFlags = WinApi.KEYEVENTF_KEYUP } }
        };

        WinApi.SendInput(1, [down], sz);
        Thread.Sleep(holdMs);
        WinApi.SendInput(1, [up], sz);
    }
}
