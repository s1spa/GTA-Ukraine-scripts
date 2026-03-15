using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Hlorka;

public class HlorkaConfig
{
    // Індекс монітора (-1 = основний)
    [JsonPropertyName("monitorIndex")] public int MonitorIndex { get; set; } = -1;

    // Область сканування (частки від розміру екрану, 0.0–1.0)
    // Має охоплювати лише вертикальну планку (циліндр) міні-гри
    [JsonPropertyName("scanX")] public double ScanX { get; set; } = 0.62;
    [JsonPropertyName("scanY")] public double ScanY { get; set; } = 0.15;
    [JsonPropertyName("scanW")] public double ScanW { get; set; } = 0.06;
    [JsonPropertyName("scanH")] public double ScanH { get; set; } = 0.65;

    // Мертва зона в пікселях: в межах цього діапазону нічого не робимо
    [JsonPropertyName("deadband")] public int Deadband { get; set; } = 4;

    // Поріг схожості з еталоном (MAD, 0–255): менше = суворіше
    // Логуй MAD в терміналі і підбери під своє значення
    [JsonPropertyName("matchThreshold")] public int MatchThreshold { get; set; } = 30;

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    static string FilePath => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "hlorka", "config.json");

    public static HlorkaConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<HlorkaConfig>(File.ReadAllText(FilePath)) ?? new();
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
