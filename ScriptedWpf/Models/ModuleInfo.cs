using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScriptedWpf.Models;

public class ModuleInfo
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0.0";
    [JsonPropertyName("author")]      public string Author      { get; set; } = "";
    [JsonPropertyName("icon")]        public string Icon        { get; set; } = "";
    [JsonPropertyName("instruction")] public string Instruction { get; set; } = "";
    [JsonPropertyName("hints")]       public Dictionary<string, string> Hints { get; set; } = new();
    [JsonPropertyName("scripts")]     public List<ScriptInfo> Scripts { get; set; } = new();

    static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    public static ModuleInfo Load(string moduleId)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Cogs", moduleId, "module.json");
            return JsonSerializer.Deserialize<ModuleInfo>(File.ReadAllText(path), _opts) ?? new();
        }
        catch { return new(); }
    }

    public string Hint(string key) =>
        Hints.TryGetValue(key, out var v) ? v : "";
}

public class ScriptInfo
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}
