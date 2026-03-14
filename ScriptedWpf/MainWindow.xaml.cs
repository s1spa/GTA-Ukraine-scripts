using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScriptedWpf.Core;
using ScriptedWpf.Models;
using ScriptedWpf.Cogs.Cda;

namespace ScriptedWpf;

public partial class MainWindow : Window
{
    record ModuleEntry(ModuleInfo Info, IModule? Module, Button SidebarBtn, TextBlock StarText);

    readonly List<ModuleEntry>  _entries   = new();
    readonly HashSet<string>    _favorites = LoadFavorites();
    HotkeyService?              _hotkeys;
    ModuleEntry?                _selected;

    static string FavPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "favorites.json");

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

    // ── Favorites persistence ─────────────────────────────────────────────────
    static HashSet<string> LoadFavorites()
    {
        try
        {
            if (File.Exists(FavPath))
                return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FavPath)) ?? []);
        }
        catch { }
        return new();
    }

    void SaveFavorites()
    {
        try { File.WriteAllText(FavPath, JsonSerializer.Serialize(_favorites.ToList())); }
        catch { }
    }

    void RefreshSidebarOrder()
    {
        SidebarPanel.Children.Clear();
        foreach (var e in _entries.OrderByDescending(e => _favorites.Contains(e.Info.Id)))
            SidebarPanel.Children.Add(e.SidebarBtn);
    }

    // ── Module loading ────────────────────────────────────────────────────────
    void LoadModules()
    {
        string cogsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cogs");
        var loaded = ModuleLoader.LoadAll(cogsPath);

        var accent  = (Brush)Application.Current.Resources["AccentBrush"];
        var textDim = (Brush)Application.Current.Resources["TextDimBrush"];

        foreach (var (info, module) in loaded)
        {
            bool isFav = _favorites.Contains(info.Id);

            var btn = new Button
            {
                Style = (Style)Resources["SidebarButtonStyle"],
                Tag   = info,
            };

            // Status dot
            var statusDot = new System.Windows.Shapes.Ellipse
            {
                Width             = 6,
                Height            = 6,
                Margin            = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill              = textDim,
            };

            // Star text (right side)
            var starText = new TextBlock
            {
                Text              = isFav ? "★" : "☆",
                Foreground        = isFav ? accent : textDim,
                FontSize          = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(0, 0, 6, 0),
                Cursor            = Cursors.Hand,
                ToolTip           = "Додати в обране",
            };

            // Button content: dock star to right, dot+name to left
            var dock = new DockPanel { LastChildFill = true, HorizontalAlignment = HorizontalAlignment.Stretch };
            DockPanel.SetDock(starText, Dock.Right);
            dock.Children.Add(starText);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(statusDot);
            left.Children.Add(new TextBlock
            {
                Text              = info.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize          = 13,
            });
            dock.Children.Add(left);
            btn.Content = dock;

            var entry = new ModuleEntry(info, module, btn, starText);

            // Star click (stop propagation so sidebar btn doesn't fire)
            starText.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                if (_favorites.Contains(info.Id))
                {
                    _favorites.Remove(info.Id);
                    starText.Text       = "☆";
                    starText.Foreground = textDim;
                }
                else
                {
                    _favorites.Add(info.Id);
                    starText.Text       = "★";
                    starText.Foreground = accent;
                }
                SaveFavorites();
                RefreshSidebarOrder();

                // Restore selected highlight after re-order
                if (_selected != null)
                    _selected.SidebarBtn.Background = new SolidColorBrush(Color.FromArgb(28, 76, 255, 114));
            };

            btn.Click += (_, _) => SelectModule(entry);

            if (module != null)
            {
                module.Initialize(msg => Dispatcher.Invoke(() => AppendLog(msg)));

                module.StateChanged += () => Dispatcher.Invoke(() =>
                    statusDot.Fill = module.IsRunning
                        ? (Brush)Application.Current.Resources["AccentBrush"]
                        : (Brush)Application.Current.Resources["TextDimBrush"]);

                if (module is CdaModule cda)
                {
                    cda.OnFrame += bmp => Dispatcher.Invoke(() =>
                    {
                        OcrPreviewBorder.Visibility = Visibility.Visible;
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
                        ms.Seek(0, System.IO.SeekOrigin.Begin);
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        OcrPreviewImage.Source = bi;
                        bmp.Dispose();
                    });
                    module.StateChanged += () => Dispatcher.Invoke(() =>
                    {
                        if (!module.IsRunning) { OcrPreviewBorder.Visibility = Visibility.Collapsed; OcrPreviewImage.Source = null; }
                    });
                }
            }

            _entries.Add(entry);
        }

        // Register hotkeys
        if (_hotkeys != null)
            foreach (var e in _entries)
                e.Module?.RegisterHotkeys(_hotkeys);

        // Add to sidebar sorted (favorites first)
        RefreshSidebarOrder();
    }

    void SelectModule(ModuleEntry entry)
    {
        if (_selected != null)
            _selected.SidebarBtn.Background = Brushes.Transparent;

        _selected = entry;
        entry.SidebarBtn.Background = new SolidColorBrush(Color.FromArgb(28, 76, 255, 114));

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
            Tag             = false,
        };

        if (entry.Module == null)
        {
            toggleBtn.IsEnabled = false;
            statusText.Text     = "НЕ ПІДТРИМУЄТЬСЯ";
        }

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

        if (entry.Module != null)
            entry.Module.StateChanged += () => Dispatcher.Invoke(SyncToggleUi);

        toggleBtn.Click += (_, _) =>
        {
            if (entry.Module == null) return;
            if (entry.Module.IsRunning)
                entry.Module.Stop();
            else
                entry.Module.Start();
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

        if (entry.Module != null)
        {
            var sep = new Border
            {
                Height     = 1,
                Background = borderB,
                Margin     = new Thickness(0, 0, 0, 16),
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
        var doc  = TerminalBox.Document;
        var para = new Paragraph { Margin = new Thickness(0) };

        var ts = new Run($"[{DateTime.Now:HH:mm:ss}] ")
        {
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
        };
        para.Inlines.Add(ts);

        Color msgColor = ((SolidColorBrush)Application.Current.Resources["TextSecondaryBrush"]).Color;
        foreach (var (prefix, color) in _logRules)
        {
            if (message.Contains(prefix)) { msgColor = color; break; }
        }

        var msg = new Run(message) { Foreground = new SolidColorBrush(msgColor) };
        para.Inlines.Add(msg);

        doc.Blocks.Add(para);
        TerminalBox.ScrollToEnd();

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
