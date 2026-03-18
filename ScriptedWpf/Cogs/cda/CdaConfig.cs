using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Cda;

public class CdaConfig
{
    // ── Code zone (manual mode / auto-detected fallback) ──────────────────────
    [JsonPropertyName("x1")]                public int    X1                { get; set; } = 704;
    [JsonPropertyName("y1")]                public int    Y1                { get; set; } = 229;
    [JsonPropertyName("x2")]                public int    X2                { get; set; } = 1213;
    [JsonPropertyName("y2")]                public int    Y2                { get; set; } = 841;
    [JsonPropertyName("turbo")]             public bool   Turbo             { get; set; } = true;
    [JsonPropertyName("showNotifications")] public bool   ShowNotifications { get; set; } = true;

    // ── Auto mode ─────────────────────────────────────────────────────────────
    [JsonPropertyName("autoMode")]     public bool   AutoMode     { get; set; } = false;
    [JsonPropertyName("monitorIndex")] public int    MonitorIndex { get; set; } = -1;
    [JsonPropertyName("minPrice")]     public int    MinPrice     { get; set; } = 1500;
    [JsonPropertyName("maxTon")]       public double MaxTon       { get; set; } = 5.0;

    // ── Scan zone for orders (0,0,0,0 = full monitor) ─────────────────────────
    [JsonPropertyName("scanX1")] public int ScanX1 { get; set; } = 0;
    [JsonPropertyName("scanY1")] public int ScanY1 { get; set; } = 0;
    [JsonPropertyName("scanX2")] public int ScanX2 { get; set; } = 0;
    [JsonPropertyName("scanY2")] public int ScanY2 { get; set; } = 0;

    public bool HasScanZone => ScanX2 > ScanX1 && ScanY2 > ScanY1;

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
