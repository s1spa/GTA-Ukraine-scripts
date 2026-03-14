using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ScriptedProgram.Models;

public class ModuleInfo
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("version")]     public string Version     { get; set; } = "1.0.0";
    [JsonPropertyName("author")]      public string Author      { get; set; } = "";
    [JsonPropertyName("icon")]        public string Icon        { get; set; } = "";
    [JsonPropertyName("scripts")]     public List<ScriptInfo> Scripts { get; set; } = new();
}

public class ScriptInfo
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = "";
    [JsonPropertyName("name")]        public string Name        { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}
