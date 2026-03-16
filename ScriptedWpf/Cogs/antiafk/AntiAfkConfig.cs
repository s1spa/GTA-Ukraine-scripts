using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.AntiAfk;

public class AntiAfkConfig
{
    [JsonPropertyName("carMode")]            public bool CarMode            { get; set; } = false;
    [JsonPropertyName("showNotifications")] public bool ShowNotifications { get; set; } = true;

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "antiafk", "config.json");

    public static AntiAfkConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AntiAfkConfig>(File.ReadAllText(FilePath)) ?? new();
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
