// SPDX-License-Identifier: MIT
// Named rule sets: <configDir>/rules/<name>.json (the server's), those mods define, and Kiln's own.
using System.Text.Json;
using System.Text;

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
            try
            {
                var text = BuiltIn.TryGetValue(name, out var current) && IsEarlierDefault(name, file)
                    ? WriteDefault(name, current, file) ?? File.ReadAllText(file)
                    : File.ReadAllText(file);
                var r = PlayerRules.FromJson(text);
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

    // Only exact files emitted by earlier versions may be upgraded. Semantic equality loses intentional nulls,
    // omitted default-valued members, formatting and comments. Keep these snapshots independent of future defaults.
    private static readonly Dictionary<string, string[]> Earlier = new(StringComparer.OrdinalIgnoreCase)
    {
        ["creative"] =
        [
            DefaultText("creative", """
                {
                  "flyAnywhere": true,
                  "flightCarriesBody": true,
                  "buildOutsideBases": true,
                  "ignoreNoBuildZones": true,
                  "ignoreEnemiesNearby": true,
                  "buildInShroud": true,
                  "keepBuildsOutsideBases": true,
                  "buildCost": 0,
                  "buildRefund": 0,
                  "digDamage": 1000,
                  "invulnerable": true,
                  "minHealth": 1,
                  "minStamina": 1,
                  "minMana": 1,
                  "water": {
                    "chargesPerSecond": "Infinity"
                  }
                }
                """), // dev.14: before the Shroud timer and gliding rules
            DefaultText("creative", """
                {
                  "flyAnywhere": true,
                  "flightCarriesBody": true,
                  "buildOutsideBases": true,
                  "ignoreNoBuildZones": true,
                  "ignoreEnemiesNearby": true,
                  "buildInShroud": true,
                  "keepBuildsOutsideBases": true,
                  "buildCost": 0,
                  "buildRefund": 0,
                  "digDamage": 1000,
                  "invulnerable": true,
                  "minHealth": 1,
                  "minStamina": 1,
                  "minMana": 1,
                  "freezeShroudTimer": true,
                  "unlimitedGliding": true,
                  "glideSink": 0,
                  "glideBoost": {
                    "thrust": 15,
                    "maxSpeed": 35,
                    "lift": 0,
                    "coast": 8
                  },
                  "water": {
                    "chargesPerSecond": "Infinity"
                  }
                }
                """), // early dev.15: before glideBoost.climb
            DefaultText("creative", """
                {
                  "flyAnywhere": true,
                  "flightCarriesBody": true,
                  "buildOutsideBases": true,
                  "ignoreNoBuildZones": true,
                  "ignoreEnemiesNearby": true,
                  "buildInShroud": true,
                  "keepBuildsOutsideBases": true,
                  "buildCost": 0,
                  "buildRefund": 0,
                  "digDamage": 1000,
                  "invulnerable": true,
                  "minHealth": 1,
                  "minStamina": 1,
                  "minMana": 1,
                  "freezeShroudTimer": true,
                  "unlimitedGliding": true,
                  "glideSink": 0,
                  "glideBoost": {
                    "thrust": 15,
                    "maxSpeed": 35,
                    "lift": 0,
                    "coast": 8,
                    "climb": 15
                  },
                  "water": {
                    "chargesPerSecond": "Infinity"
                  }
                }
                """), // dev.15 to dev.23: before glideSteer
        ],
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

    private static string DefaultText(string name, string json) =>
        $"// Kiln's \"{name}\" rules (Kiln.Rules: docs/kiln/services/rules.md). Edit to change what /{name} and Rules.Preset(\"{name}\") give;\n" +
        "// delete the file to get Kiln's defaults back. The server's limits (rules.json next to this folder) still apply.\n" +
        json + "\n";

    // Recheck an existing file while holding the same stream used to write it, without sharing write access.
    // Return the text actually observed/applied so Get does not return new defaults after a raced-in owner edit.
    internal static string? WriteDefault(string name, PlayerRules rules, string file)
    {
        try
        {
            var upgrade = false;
            FileStream stream;
            try
            {
                stream = new FileStream(file, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException) when (File.Exists(file))
            {
                stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                upgrade = true;
            }
            var text = DefaultText(name, rules.ToJson());
            using (stream)
            {
                if (upgrade)
                {
                    using var contents = new MemoryStream();
                    stream.CopyTo(contents);
                    if (!IsEarlierText(name, contents.ToArray()))
                    {
                        stream.Position = 0;
                        using var reader = new StreamReader(stream, leaveOpen: true);
                        return reader.ReadToEnd();
                    }
                }
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Position = 0;
                stream.Write(bytes);
                stream.SetLength(bytes.Length);
            }
            if (upgrade) RulesRuntime.Log(LogLevel.Info, $"{file}: an earlier Kiln's unedited \"{name}\" preset; updated to this version's");
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RulesRuntime.Log(LogLevel.Warn, $"writing the \"{name}\" preset {file}: {e.Message}");
            return null;
        }
    }

    internal static bool IsEarlierDefault(string name, string file) => IsEarlierText(name, File.ReadAllBytes(file));

    // Each snapshot as written on Linux (LF) and as a Windows game or server wrote it: System.Text.Json ends indented
    // lines with Environment.NewLine (CRLF there), while the header lines and the final newline are "\n" on both.
    private static bool IsEarlierText(string name, ReadOnlySpan<byte> bytes)
    {
        if (!Earlier.TryGetValue(name, out var earlier)) return false;
        foreach (var text in earlier)
            if (bytes.SequenceEqual(Encoding.UTF8.GetBytes(text)) || bytes.SequenceEqual(Encoding.UTF8.GetBytes(WindowsText(text))))
                return true;
        return false;
    }

    // DefaultText's two header lines, then the JSON with CRLF line ends, then the final "\n"
    private static string WindowsText(string text)
    {
        var start = text.IndexOf('\n', text.IndexOf('\n') + 1) + 1;
        return text[..start] + text[start..^1].Replace("\n", "\r\n") + "\n";
    }
}
