// SPDX-License-Identifier: MIT
// Named rule sets: <configDir>/rules/<name>.json (the server's), those mods define, and Kiln's own.
using System.Text.Json;

namespace Kiln;

internal static class RulesPresets
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, (PlayerRules Rules, ModContext? Mod)> Defined = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, PlayerRules> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        ["creative"] = PlayerRules.Creative,
    };

    public static string Folder => Path.Combine(Config.Directory, "rules");

    private static bool ValidName(string n) => n.Length is > 0 and <= 64 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static PlayerRules? Get(string name)
    {
        if (!ValidName(name)) return null;
        var file = Path.Combine(Folder, name + ".json");
        if (File.Exists(file))
        {
            try
            {
                var r = PlayerRules.FromJson(File.ReadAllText(file));
                var problems = r.Problems();
                if (problems.Count > 0) throw new JsonException(string.Join("; ", problems));
                return r;
            }
            catch (JsonException e)
            {
                throw new JsonException($"{file}: {e.Message}", e);
            }
        }
        lock (Lock)
            if (Defined.TryGetValue(name, out var d)) return d.Rules;
        return BuiltIn.GetValueOrDefault(name);
    }

    public static IReadOnlyList<string> Names()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(BuiltIn.Keys);
        lock (Lock) names.UnionWith(Defined.Keys);
        if (Directory.Exists(Folder))
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json"))
                names.Add(Path.GetFileNameWithoutExtension(f));
        return names.ToArray();
    }

    public static IDisposable Define(string name, PlayerRules rules, ModContext? mod)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (!ValidName(name)) throw new ArgumentException($"preset name '{name}' must be [A-Za-z0-9._-], at most 64", nameof(name));
        var problems = rules.Problems();
        if (problems.Count > 0) throw new ArgumentException(string.Join("; ", problems), nameof(rules));
        lock (Lock) Defined[name] = (rules, mod);
        var handle = new Handle(name, rules);
        if (mod != null) mod.Retiring += handle.Dispose;
        return handle;
    }

    private sealed class Handle(string name, PlayerRules rules) : IDisposable
    {
        public void Dispose()
        {
            lock (Lock)
                if (Defined.TryGetValue(name, out var d) && ReferenceEquals(d.Rules, rules)) Defined.Remove(name);
        }
    }

    /// <summary>Writes Kiln's presets as files when they are missing, so a server owner sees and can change them.</summary>
    public static void WriteDefaults()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var (name, rules) in BuiltIn)
            {
                var file = Path.Combine(Folder, name + ".json");
                if (File.Exists(file)) continue;
                File.WriteAllText(file,
                    $"// Kiln's \"{name}\" rules (Kiln.Rules: docs/kiln/services/rules.md). Edit to change what /{name} and Rules.Preset(\"{name}\") give;\n" +
                    "// delete the file to get Kiln's defaults back. The server's limits (rules.json next to this folder) still apply.\n" +
                    rules.ToJson() + "\n");
            }
        }
        catch (Exception e)
        {
            RulesRuntime.Log(LogLevel.Warn, $"writing the default presets: {e.Message}");
        }
    }
}
