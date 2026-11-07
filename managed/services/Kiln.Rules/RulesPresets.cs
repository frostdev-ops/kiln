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

    public static string Folder => FolderOverride ?? Path.Combine(Config.Directory, "rules");

    /// <summary>Tests: the preset folder instead of <c>&lt;configDir&gt;/rules</c>.</summary>
    internal static string? FolderOverride;

    private static bool ValidName(string n) => n.Length is > 0 and <= 64 && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static PlayerRules? Get(string name)
    {
        if (!ValidName(name)) return null;
        var file = Path.Combine(Folder, name + ".json");
        if (File.Exists(file))
        {
            // An earlier Kiln's unedited file is this version's preset. Rules.Preset can run before Kiln.Rules has started
            // (and updated the files): /creative reads the preset before Rules.Apply starts the runtime, so the first
            // /creative after an update would otherwise apply the old one.
            if (BuiltIn.TryGetValue(name, out var current) && IsEarlierDefault(name, file))
            {
                WriteDefault(name, current, file);
                return current;
            }
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

    // What earlier Kiln versions wrote as a preset file. A file that still reads exactly so was never edited: it gets the
    // current defaults (new rules reach existing worlds); an edited file stays as the owner left it.
    private static readonly Dictionary<string, PlayerRules[]> Earlier = new(StringComparer.OrdinalIgnoreCase)
    {
        // 0.2.0-dev.14: before the Shroud timer and gliding rules
        ["creative"] = [PlayerRules.Creative with { FreezeShroudTimer = null, UnlimitedGliding = null, GlideSink = null, GlideBoost = null }],
    };

    /// <summary>Writes Kiln's presets as files when they are missing (or still an earlier Kiln's unedited defaults), so a
    /// server owner sees and can change them.</summary>
    public static void WriteDefaults()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            foreach (var (name, rules) in BuiltIn)
            {
                var file = Path.Combine(Folder, name + ".json");
                if (File.Exists(file) && !IsEarlierDefault(name, file)) continue;
                WriteDefault(name, rules, file);
            }
        }
        catch (Exception e)
        {
            RulesRuntime.Log(LogLevel.Warn, $"writing the default presets: {e.Message}");
        }
    }

    // Kiln's preset as a file (a missing one, or an earlier Kiln's unedited one). A failed write is logged; the caller's
    // rules stand either way.
    private static void WriteDefault(string name, PlayerRules rules, string file)
    {
        try
        {
            var upgrade = File.Exists(file);
            File.WriteAllText(file,
                $"// Kiln's \"{name}\" rules (Kiln.Rules: docs/kiln/services/rules.md). Edit to change what /{name} and Rules.Preset(\"{name}\") give;\n" +
                "// delete the file to get Kiln's defaults back. The server's limits (rules.json next to this folder) still apply.\n" +
                rules.ToJson() + "\n");
            if (upgrade) RulesRuntime.Log(LogLevel.Info, $"{file}: an earlier Kiln's unedited \"{name}\" preset; updated to this version's");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"writing the \"{name}\" preset {file}: {e.Message}");
        }
    }

    internal static bool IsEarlierDefault(string name, string file)
    {
        if (!Earlier.TryGetValue(name, out var earlier)) return false;
        try
        {
            var r = PlayerRules.FromJson(File.ReadAllText(file));
            return earlier.Any(e => e == r);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
