using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ScriptedWpf.Models;

namespace ScriptedWpf.Core;

public static class ModuleLoader
{
    static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Scans <paramref name="cogsBasePath"/> for subdirectories that contain module.json.
    /// Returns a list of (metadata, module_instance_or_null) pairs.
    /// </summary>
    public static List<(ModuleInfo info, IModule? module)> LoadAll(string cogsBasePath)
    {
        var result = new List<(ModuleInfo, IModule?)>();
        if (!Directory.Exists(cogsBasePath)) return result;

        foreach (var dir in Directory.GetDirectories(cogsBasePath))
        {
            string jsonPath = Path.Combine(dir, "module.json");
            if (!File.Exists(jsonPath)) continue;
            try
            {
                var info = JsonSerializer.Deserialize<ModuleInfo>(File.ReadAllText(jsonPath), _json);
                if (info == null) continue;
                result.Add((info, CreateModule(info.Id)));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ModuleLoader] Помилка завантаження {dir}: {ex.Message}");
            }
        }
        return result;
    }

    // Registry: add new modules here as the project grows
    static IModule? CreateModule(string id) => id switch
    {
        "cda"     => new Cogs.Cda.CdaModule(),
        "antiafk" => new Cogs.AntiAfk.AntiAfkModule(),
        _         => null,
    };
}
