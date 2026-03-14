using System.Drawing;
using System.Windows.Forms;
using ScriptedProgram.Core;
using ScriptedProgram.Models;

namespace ScriptedProgram.UI.Controls;

public sealed class PluginDetailPanel : Panel
{
    private readonly Label        _name;
    private readonly ToggleSwitch _toggle;
    private readonly Label        _meta;
    private readonly Panel        _descBlock;
    private readonly Panel        _settingsHolder;

    private IModule? _module;

    public PluginDetailPanel()
    {
        BackColor = Theme.BgBase;
        Padding   = new Padding(28, 35, 28, 16);

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = 52, BackColor = Color.Transparent };

        _toggle = new ToggleSwitch();
        _toggle.CheckedChanged += OnToggleChanged;

        var toggleWrap = new Panel { Dock = DockStyle.Right, Width = 65, BackColor = Color.Transparent };
        toggleWrap.Controls.Add(_toggle);
        toggleWrap.Layout += (_, _) =>
            _toggle.Location = new Point(5, (toggleWrap.Height - _toggle.Height) / 2);

        _name = new Label
        {
            Dock      = DockStyle.Fill,
            Font      = Theme.FontTitle,
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(0),
        };

        header.Controls.Add(_name);
        header.Controls.Add(toggleWrap);

        _meta = new Label { Dock = DockStyle.Top, Height = 25, Font = Theme.FontSmall, ForeColor = Theme.TextMuted };

        _descBlock = new Panel { Dock = DockStyle.Top, Height = 0, BackColor = Color.Transparent };

        _settingsHolder = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        Controls.Add(_settingsHolder); // 0 (Fill)
        Controls.Add(_descBlock);      // 1 (Top)
        Controls.Add(_meta);           // 2 (Top)
        Controls.Add(header);          // 3 (Top — найвище)
    }

    public void ShowModule(ModuleInfo info, IModule? module)
    {
        _module = module;
        _name.Text      = info.Name;
        _meta.Text      = $"v{info.Version}  •  {info.Author}";
        _toggle.Checked = module?.IsRunning ?? false;

        _descBlock.Controls.Clear();
        int y = 5;
        y = AddSection(_descBlock, y, "ОПИС", info.Description);
        if (info.Scripts.Count > 0)
        {
            var names = string.Join(", ", info.Scripts.ConvertAll(s => s.Name));
            y = AddSection(_descBlock, y, "СКРИПТИ", names);
        }
        _descBlock.Height = y + 10;

        _settingsHolder.Controls.Clear();
        var ctrl = module?.GetSettingsControl();
        if (ctrl != null)
        {
            _settingsHolder.Controls.Add(ctrl);
        }
    }

    static int AddSection(Panel parent, int y, string heading, string body)
    {
        const int gap = 8;
        var h = new Label
        {
            Text = heading, Font = Theme.FontLabel, ForeColor = Theme.TextMuted,
            Top = y, Left = 0, Width = 600, Height = 18,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        parent.Controls.Add(h);

        y += 20;
        var b = new Label
        {
            Text = body, Font = Theme.FontUi, ForeColor = Theme.TextPrimary,
            Top = y, Left = 0, Width = 600, Height = 45,
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        };
        parent.Controls.Add(b);

        return y + 45 + gap;
    }

    private void OnToggleChanged(object? s, System.EventArgs e)
    {
        if (_module == null) return;
        if (_toggle.Checked) _module.Start(); else _module.Stop();
    }
}
