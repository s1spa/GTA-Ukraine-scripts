//Ось приклад того, як динамічно підтягувати ці «коги» з папки:

public class ModuleInfo
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
}

// У головній формі:
void LoadModules()
{
    string cogsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cogs");
    if (!Directory.Exists(cogsPath)) return;

    foreach (var dir in Directory.GetDirectories(cogsPath))
    {
        string jsonPath = Path.Combine(dir, "module.json");
        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            var info = JsonSerializer.Deserialize<ModuleInfo>(json);
            AddModuleToSidebar(info);
        }
    }
}

void AddModuleToSidebar(ModuleInfo info)
{
    Button btn = new Button {
        Text = info.Name,
        FlatStyle = FlatStyle.Flat,
        Height = 50,
        Dock = DockStyle.Top,
        ForeColor = Color.White
    };
    btn.Click += (s, e) => ShowModuleDetails(info);
    sidebarPanel.Controls.Add(btn);
}