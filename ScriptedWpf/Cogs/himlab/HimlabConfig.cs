using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Himlab;

public class HimlabConfig
{
    [JsonPropertyName("monitorIndex")]       public int    MonitorIndex      { get; set; } = -1;
    [JsonPropertyName("showNotifications")] public bool   ShowNotifications { get; set; } = true;
    [JsonPropertyName("holdMs")]        public int    HoldMs        { get; set; } = 600;
    [JsonPropertyName("transitionMs")]  public int    TransitionMs  { get; set; } = 150;
    [JsonPropertyName("centerYPct")]    public double CenterYPct    { get; set; } = 0.876;
    [JsonPropertyName("captureSize")]   public int    CaptureSize   { get; set; } = 65;

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "himlab", "config.json");

    public static string TemplatesDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "himlab", "templates");

    public static HimlabConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<HimlabConfig>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _opts));
        }
        catch { }
    }
}
