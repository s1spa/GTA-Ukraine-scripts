using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Cda;

public class CdaConfig
{
    [JsonPropertyName("mode")]              public string Mode              { get; set; } = "Auto";
    [JsonPropertyName("turbo")]             public bool   Turbo             { get; set; } = true;
    [JsonPropertyName("monitorIndex")]      public int    MonitorIndex      { get; set; } = -1;
    [JsonPropertyName("x")]                 public int    X                 { get; set; }
    [JsonPropertyName("y")]                 public int    Y                 { get; set; }
    [JsonPropertyName("width")]             public int    Width             { get; set; }
    [JsonPropertyName("height")]            public int    Height            { get; set; }
    [JsonPropertyName("minPrice")]          public int    MinPrice          { get; set; } = 1000;
    [JsonPropertyName("maxTon")]            public double MaxTon            { get; set; } = 5.0;
    [JsonPropertyName("showNotifications")] public bool   ShowNotifications { get; set; } = true;
    [JsonPropertyName("types")]
    public List<string> Types { get; set; } = new()
    {
        "Одяг", "Нафта", "Фармацевтика", "Різне", "Продукти", "Автозапчастини", "Інше"
    };

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "cda", "config.json");

    public static CdaConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<CdaConfig>(File.ReadAllText(FilePath)) ?? new();
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
