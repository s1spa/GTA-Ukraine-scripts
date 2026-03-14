using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Wires;

public class PixelSample
{
    public int R { get; set; }
    public int G { get; set; }
    public int B { get; set; }
    public double XPct { get; set; } // position in image (0..1)
    public double YPct { get; set; }
}

public class ColorSamples
{
    public List<PixelSample> Wire { get; set; } = new();
    public List<PixelSample> Ring { get; set; } = new();
    public List<PixelSample> Tip  { get; set; } = new();
}

public class WiresConfig
{
    [JsonPropertyName("monitorIndex")] public int MonitorIndex { get; set; } = -1;

    // Горизонтальні лінії проводів (Y як відсоток висоти екрану)
    [JsonPropertyName("topWireY")] public double TopWireY { get; set; } = 0.277;
    [JsonPropertyName("botWireY")] public double BotWireY { get; set; } = 0.741;

    // X-межі зони сканування (щоб не виходити за межі міні-гри)
    [JsonPropertyName("scanX1")] public double ScanX1 { get; set; } = 0.40;
    [JsonPropertyName("scanX2")] public double ScanX2 { get; set; } = 0.95;

    // Y-лінії зони сканування кілець
    [JsonPropertyName("ringTopY")] public double RingTopY { get; set; } = 0.319;
    [JsonPropertyName("ringBotY")] public double RingBotY { get; set; } = 0.746;

    // X-межі зони сканування кілець
    [JsonPropertyName("ringTopX1")] public double RingTopX1 { get; set; } = 0.525;
    [JsonPropertyName("ringTopX2")] public double RingTopX2 { get; set; } = 0.815;
    [JsonPropertyName("ringBotX1")] public double RingBotX1 { get; set; } = 0.525;
    [JsonPropertyName("ringBotX2")] public double RingBotX2 { get; set; } = 0.815;

    // Зразки кольорів (ключ = назва WireColor: "Red", "Blue", тощо)
    [JsonPropertyName("colorSamples")]
    public Dictionary<string, ColorSamples> ColorSamples { get; set; } = new();

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "wires", "config.json");

    public static WiresConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WiresConfig>(File.ReadAllText(FilePath)) ?? new();
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
