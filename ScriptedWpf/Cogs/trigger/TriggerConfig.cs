using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Cogs.Trigger;

public class TriggerActionData
{
    [JsonPropertyName("id")]               public string Id              { get; set; } = "";
    [JsonPropertyName("name")]             public string Name            { get; set; } = "Новий тригер";
    [JsonPropertyName("imagePath")]        public string       ImagePath       { get; set; } = "";
    [JsonPropertyName("extraImagePaths")] public List<string> ExtraImagePaths { get; set; } = new();
    [JsonPropertyName("captureX")]         public int    CaptureX        { get; set; }
    [JsonPropertyName("captureY")]         public int    CaptureY        { get; set; }
    [JsonPropertyName("captureW")]         public int    CaptureW        { get; set; }
    [JsonPropertyName("captureH")]         public int    CaptureH        { get; set; }
    [JsonPropertyName("monitorIndex")]     public int    MonitorIndex    { get; set; }
    [JsonPropertyName("monitorW")]         public int    MonitorW        { get; set; }
    [JsonPropertyName("monitorH")]         public int    MonitorH        { get; set; }
    [JsonPropertyName("hotkey")]           public string Hotkey          { get; set; } = "";
    [JsonPropertyName("action")]           public string Action          { get; set; } = "key"; // "key" | "sound"
    [JsonPropertyName("actionKey")]        public string ActionKey       { get; set; } = "";
    [JsonPropertyName("soundFile")]        public string SoundFile       { get; set; } = "";
    [JsonPropertyName("soundIntervalSec")] public int    SoundIntervalSec { get; set; } = 3;
    [JsonPropertyName("matchThreshold")]   public double MatchThreshold   { get; set; } = 0.85;
    [JsonPropertyName("brightness")]       public int    Brightness       { get; set; } = 0;   // -100..100
    [JsonPropertyName("contrast")]         public int    Contrast         { get; set; } = 0;   // -100..100
    [JsonPropertyName("checkIntervalMs")]  public int    CheckIntervalMs  { get; set; } = 500; // ms between screen checks
}

public class TriggerConfig
{
    [JsonPropertyName("actions")] public List<TriggerActionData> Actions { get; set; } = new();

    static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public static string BaseDir => Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Cogs", "trigger");

    static string FilePath => Path.Combine(BaseDir, "config.json");

    public static TriggerConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<TriggerConfig>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, _opts));
        }
        catch { }
    }
}
