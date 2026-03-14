using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ScriptedWpf.Core;
using ScriptedWpf.Models;

namespace ScriptedWpf;

public partial class MainWindow : Window
{
    record ModuleEntry(ModuleInfo Info, IModule? Module, Button SidebarBtn);

    readonly List<ModuleEntry>  _entries = new();
    HotkeyService?              _hotkeys;
    ModuleEntry?                _selected;

    // Log color rules: prefix → color
    static readonly (string Prefix, Color Color)[] _logRules =
    {
        ("✅", Color.FromRgb(0x4C, 0xFF, 0x72)),
        ("❌", Color.FromRgb(0xFF, 0x55, 0x55)),
        ("🏆", Color.FromRgb(0xFF, 0xCC, 0x44)),
        ("🖱",  Color.FromRgb(0x55, 0xAA, 0xFF)),
        ("🚚", Color.FromRgb(0x4C, 0xFF, 0x72)),
    };

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys = new HotkeyService(this);
        LoadModules();
        if (_entries.Count > 0)
            SelectModule(_entries[0]);
    }

    void LoadModules()
    {
        string cogsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cogs");
        var loaded = ModuleLoader.LoadAll(cogsPath);

        foreach (var (info, module) in loaded)
        {
            var btn = new Button
            {
                Style = (Style)Resources["SidebarButtonStyle"],
                Tag   = info,
            };

            var btnContent = new StackPanel { Orientation = Orientation.Horizontal };
            var statusDot  = new System.Windows.Shapes.Ellipse
            {
                Width             = 6,
                Height            = 6,
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill              = (Brush)Application.Current.Resources["TextDimBrush"],
            };
            btnContent.Children.Add(statusDot);
            btnContent.Children.Add(new TextBlock
            {
                Text              = info.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize          = 13,
            });
            btn.Content = btnContent;

            var entry = new ModuleEntry(info, module, btn);
            btn.Click += (_, _) => SelectModule(entry);

            if (module != null)
            {
                module.Initialize(msg => Dispatcher.Invoke(() => AppendLog(msg)));

                // Dot reflects running state (works for F9 too via StateChanged)
                module.StateChanged += () => Dispatcher.Invoke(() =>
                    statusDot.Fill = module.IsRunning
                        ? (Brush)Application.Current.Resources["AccentBrush"]
                        : (Brush)Application.Current.Resources["TextDimBrush"]);
            }

            SidebarPanel.Children.Add(btn);
            _entries.Add(entry);
        }

        // Register hotkeys after all modules loaded
        if (_hotkeys != null)
            foreach (var e in _entries)
                e.Module?.RegisterHotkeys(_hotkeys);
    }

    void SelectModule(ModuleEntry entry)
    {
        // Deselect previous
        if (_selected != null)
            _selected.SidebarBtn.Background = Brushes.Transparent;

        _selected = entry;
        entry.SidebarBtn.Background = new SolidColorBrush(Color.FromArgb(28, 76, 255, 114));

        // Build detail view
        DetailContent.Content = BuildDetailView(entry);
    }

    UIElement BuildDetailView(ModuleEntry entry)
    {
        var accent  = (Brush)Application.Current.Resources["AccentBrush"];
        var textPri = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var textSec = (Brush)Application.Current.Resources["TextSecondaryBrush"];
        var textDim = (Brush)Application.Current.Resources["TextDimBrush"];
        var bgCard  = (Brush)Application.Current.Resources["BgCardBrush"];
        var borderB = (Brush)Application.Current.Resources["BorderBrush"];

        var root = new DockPanel();

        // ── Header bar ──────────────────────────────────────────────────────
        var header = new Border
        {
            Height          = 56,
            Background      = (Brush)Application.Current.Resources["BgPanelBrush"],
            BorderBrush     = borderB,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(header, Dock.Top);

        var headerGrid = new Grid { Margin = new Thickness(20, 0, 20, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameBlock = new TextBlock
        {
            Text              = entry.Info.Name,
            FontSize          = 16,
            FontWeight        = FontWeights.SemiBold,
            Foreground        = textPri,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(nameBlock, 0);

        var togglePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var statusText  = new TextBlock
        {
            Text       = "ВИМКНЕНО",
            Foreground = textDim,
            FontSize   = 10,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin     = new Thickness(0, 0, 10, 0),
        };

        var toggleBtn = new Button
        {
            Width           = 52,
            Height          = 26,
            Background      = borderB,
            Foreground      = textDim,
            BorderThickness = new Thickness(0),
            Cursor          = Cursors.Hand,
            Content         = "OFF",
            FontSize        = 10,
            FontWeight      = FontWeights.Bold,
            Tag             = false, // isOn state
        };

        if (entry.Module == null)
        {
            toggleBtn.IsEnabled = false;
            statusText.Text     = "НЕ ПІДТРИМУЄТЬСЯ";
        }

        // Sync toggle visual state from IsRunning
        void SyncToggleUi()
        {
            bool on = entry.Module?.IsRunning == true;
            toggleBtn.Tag        = on;
            toggleBtn.Background = on ? accent : borderB;
            toggleBtn.Foreground = on ? new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0F)) : textDim;
            toggleBtn.Content    = on ? "ON"   : "OFF";
            statusText.Text      = on ? "ПРАЦЮЄ" : "ВИМКНЕНО";
            statusText.Foreground = on ? accent : textDim;
        }

        // F9 → оновити UI через StateChanged
        if (entry.Module != null)
            entry.Module.StateChanged += () => Dispatcher.Invoke(SyncToggleUi);

        toggleBtn.Click += (_, _) =>
        {
            if (entry.Module == null) return;
            if (entry.Module.IsRunning)
                entry.Module.Stop();
            else
                entry.Module.Start();
            // SyncToggleUi буде викликано через StateChanged
        };

        togglePanel.Children.Add(statusText);
        togglePanel.Children.Add(toggleBtn);
        Grid.SetColumn(togglePanel, 1);

        headerGrid.Children.Add(nameBlock);
        headerGrid.Children.Add(togglePanel);
        header.Child = headerGrid;
        root.Children.Add(header);

        // ── Content scroll area ──────────────────────────────────────────────
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(24, 20, 24, 20),
        };
        DockPanel.SetDock(scroll, Dock.Top);

        var content = new StackPanel();

        // Description
        if (!string.IsNullOrWhiteSpace(entry.Info.Description))
        {
            var desc = new TextBlock
            {
                Text         = entry.Info.Description,
                Foreground   = textSec,
                FontSize     = 13,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 20),
            };
            content.Children.Add(desc);
        }

        // Separator + Settings label
        if (entry.Module != null)
        {
            var sep = new Border
            {
                Height          = 1,
                Background      = borderB,
                Margin          = new Thickness(0, 0, 0, 16),
            };
            content.Children.Add(sep);

            var settLabel = new TextBlock
            {
                Text       = "НАЛАШТУВАННЯ",
                FontSize   = 9,
                FontWeight = FontWeights.Bold,
                Foreground = textDim,
                Margin     = new Thickness(0, 0, 0, 12),
            };
            content.Children.Add(settLabel);

            var settingsView = entry.Module.GetSettingsView();
            if (settingsView != null)
                content.Children.Add(settingsView);
        }

        scroll.Content = content;
        root.Children.Add(scroll);
        return root;
    }

    // ── Terminal ─────────────────────────────────────────────────────────────

    void AppendLog(string message)
    {
        var doc = TerminalBox.Document;
        var para = new Paragraph { Margin = new Thickness(0) };

        // Timestamp
        var ts = new Run($"[{DateTime.Now:HH:mm:ss}] ")
        {
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        };
        para.Inlines.Add(ts);

        // Message with color rules
        Color msgColor = ((SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"]).Color;
        foreach (var (prefix, color) in _logRules)
        {
            if (message.Contains(prefix)) { msgColor = color; break; }
        }

        var msg = new Run(message) { Foreground = new SolidColorBrush(msgColor) };
        para.Inlines.Add(msg);

        doc.Blocks.Add(para);
        TerminalBox.ScrollToEnd();

        // Keep last 500 lines
        while (doc.Blocks.Count > 500)
            doc.Blocks.Remove(doc.Blocks.FirstBlock);
    }

    void ClearTerminal_Click(object sender, RoutedEventArgs e)
        => TerminalBox.Document.Blocks.Clear();

    // ── Window chrome ─────────────────────────────────────────────────────────

    void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    void MinBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        _hotkeys?.Dispose();
        foreach (var entry in _entries)
            entry.Module?.Stop();
        Close();
    }
}
