// SPDX-License-Identifier: MIT
// Other engines' mods a server runs, for the advertised required[] (contracts.md 5.8, docs/kiln/engines.md): each
// engine's server folder (the legacy layout kiln-manage's server sync and engine_mods::server_root use), with the
// mod's kind ("eml", "flame" ...) so a joining player's launcher can tell what the server needs and the modpack carries
// it. The HELLO check stays on Kiln mods: clients don't announce other engines' mods.
// clients: the mod.json's stated "clients" wins; else EML, ShroudForge, Flame and Enshwick mods are needed (they change
// data both sides load; an Enshwick pack.toml with side = "server" is not), the others are not. shroudlink
// (shroudlink_mods/) and Ember (ember/mods/) are server-only engines: players never need their mods, so they are not
// listed here (kiln-manage's engine_mods::default_side says the same).
using System.Text.Json;

namespace Kiln;

internal static partial class NetAdvertise
{
    private static readonly (string Kind, string Folder)[] EngineFolders =
    [
        ("emf", "emf/mods"), ("eml", "kiln/eml/mods"), ("shroudforge", "mods"), ("set", "shrouded_engine_tweaks/mods"),
        ("greeneye", "greeneye"), ("flame", "mods"), ("shroudtopia", "mods"), ("enshwick", "mods"),
    ];

    // Enshwick's mod DLL exports (kiln-manage engines.rs ENSHWICK_MOD_EXPORTS); export names are ASCII in the file.
    private static readonly byte[][] EnshwickExports =
        [.. new[] { "enshwick_mod_abi", "enshwick_mod_query", "enshwick_mod_init" }.Select(System.Text.Encoding.ASCII.GetBytes)];

    private static List<Candidate> EngineCandidates(string kilnRoot)
    {
        var list = new List<Candidate>();
        var game = Path.GetDirectoryName(Path.GetFullPath(kilnRoot));
        if (string.IsNullOrEmpty(game)) return list;
        foreach (var (kind, folder) in EngineFolders)
        {
            var root = kind == "shroudforge" ? ShroudForgeRoot(game) : Path.Combine(game, folder);
            if (!Directory.Exists(root)) continue;
            string[] dirs;
            try { dirs = Directory.GetDirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (var dir in dirs)
            {
                if (Path.GetFileName(dir).StartsWith('.') || (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                if (EngineKindOf(kind, dir) is not { } needed || !needed) continue;
                var (id, version, name) = EngineIdentity(dir);
                if (id != null) list.Add(new Candidate(kind, id, version, name, dir));
            }
        }
        return list;
    }

    // Match kiln-manage's shroudforge::mods_dir and the runtime's configured mods folder.
    internal static string ShroudForgeRoot(string game)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, "shroudforge/config/modloader-config.json")));
            if (doc.RootElement.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object &&
                paths.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.String &&
                mods.GetString() is { } configured && !string.IsNullOrWhiteSpace(configured))
            {
                if (!OperatingSystem.IsWindows() && configured.StartsWith("Z:", StringComparison.OrdinalIgnoreCase))
                    return configured[2..].Replace('\\', '/');
                return Path.IsPathRooted(configured) ? configured : Path.Combine(game, configured.Replace('\\', Path.DirectorySeparatorChar));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return Path.Combine(game, "mods");
    }

    /// <summary>null: the folder is not a mod of this kind (folders several engines share); else whether players need it.</summary>
    internal static bool? EngineKindOf(string kind, string dir)
    {
        bool Has(string f) => File.Exists(Path.Combine(dir, f));
        var manifest = Path.Combine(dir, "mod.json");
        var stated = Has("mod.json") ? DeclaredClients(manifest) : null;
        bool Need(bool byDefault) => !ServerOnly(manifest) && (stated is null ? byDefault : stated == "mod");
        switch (kind)
        {
            case "emf": return Has("mod.json") ? Need(false) : null;
            case "eml": return Has("mod.json") && Has(Path.Combine("src", "mod.lua")) ? Need(true) : null;
            case "set": return Has("init.lua") ? Need(false) : null;
            case "greeneye": return Has("mod.json") ? Need(false) : null;
            case "flame": return Has("flame.json") ? Need(true) : null;
            case "enshwick":
            {
                // <mods>/enshwick/ is the loader's own folder; Flame, ShroudForge, SET and Shroudtopia folders are theirs
                if (string.Equals(Path.GetFileName(dir), "enshwick", StringComparison.OrdinalIgnoreCase) || Has("flame.json") ||
                    Has("extended.mod.json") || Has("init.lua") || Has("shroudtopia.json")) return null;
                if (Has("pack.toml"))
                    return TomlStrings(Path.Combine(dir, "pack.toml")).TryGetValue("side", out var side) && side == "server" ? false : Need(true);
                return HasEnshwickDll(dir) ? Need(true) : null;
            }
            case "shroudtopia":
                return Has("mod.json") && !Has("flame.json") && !Has(Path.Combine("src", "mod.lua")) && Directory.EnumerateFiles(dir, "*.dll").Any()
                    ? Need(false) : null;
            case "shroudforge":
            {
                if (!Has("extended.mod.json")) return null;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "extended.mod.json")),
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    var r = doc.RootElement;
                    if (r.ValueKind != JsonValueKind.Object) return null;
                    if (r.TryGetProperty("launcher", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() == "EML") return null;
                    if (r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) return false;
                    if (r.TryGetProperty("targets", out var t) && t.ValueKind == JsonValueKind.Array &&
                        !t.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "client")) return false;
                    return Need(true);
                }
                catch (Exception) { return null; }
            }
            default: return null;
        }
    }

    private static bool HasEnshwickDll(string dir)
    {
        try
        {
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                var info = new FileInfo(dll);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 96L * 1024 * 1024) continue;
                var bytes = File.ReadAllBytes(dll);
                if (EnshwickExports.Any(e => bytes.AsSpan().IndexOf(e) >= 0)) return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>The top-level <c>key = "string"</c> pairs of a TOML file (Enshwick's pack.toml: id, version, name, side),
    /// as kiln-manage's engines::toml_strings reads them: up to the first table, multi-line strings and arrays skipped.</summary>
    internal static Dictionary<string, string> TomlStrings(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try { text = File.ReadAllText(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return map; }
        string? skipUntil = null;
        var depth = 0;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Trim();
            if (skipUntil != null) { if (line.Contains(skipUntil, StringComparison.Ordinal)) skipUntil = null; continue; }
            if (depth > 0) { depth += line.Count(c => c == '[') - line.Count(c => c == ']'); continue; }
            if (line.StartsWith('[')) break;
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim().Trim('"');
            var value = line[(eq + 1)..].Trim();
            if (value.StartsWith("\"\"\"", StringComparison.Ordinal) || value.StartsWith("'''", StringComparison.Ordinal))
            {
                if (!value[3..].Contains(value[..3], StringComparison.Ordinal)) skipUntil = value[..3];
                continue;
            }
            if (value.StartsWith('[')) { depth = value.Count(c => c == '[') - value.Count(c => c == ']'); continue; }
            if (key.Length == 0 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) continue;
            if (TomlString(value) is { } parsed && !map.ContainsKey(key)) map[key] = parsed;
        }
        return map;
    }

    private static string? TomlString(string v)
    {
        string? Rest(string rest, string result) { rest = rest.Trim(); return rest.Length == 0 || rest[0] == '#' ? result : null; }
        if (v.StartsWith('\''))
        {
            var end = v.IndexOf('\'', 1);
            return end < 0 ? null : Rest(v[(end + 1)..], v[1..end]);
        }
        if (!v.StartsWith('"')) return null;
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i < v.Length; i++)
        {
            var c = v[i];
            if (c == '"') return Rest(v[(i + 1)..], sb.ToString());
            if (c != '\\') { sb.Append(c); continue; }
            if (++i >= v.Length) return null;
            switch (v[i])
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'u' when i + 4 < v.Length && int.TryParse(v.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var u):
                    sb.Append((char)u); i += 4; break;
                default: return null;
            }
        }
        return null;
    }

    private static bool ServerOnly(string manifest)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("side", out var side)
                && side.ValueKind == JsonValueKind.String && side.GetString() == "server";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal static (string? Id, string Version, string? Name) EngineIdentity(string dir)
    {
        var folder = Path.GetFileName(dir);
        string? id = null, version = null, name = null;
        try
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var r = doc.RootElement;
                string? S(string k) => r.ValueKind == JsonValueKind.Object && r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                (id, version, name) = (S("id"), S("version"), S("name"));
            }
            else if (File.Exists(Path.Combine(dir, "pack.toml")))
            {
                // an Enshwick data pack or Luau mod names itself in pack.toml (kiln-manage engine_mods::identity)
                var pack = TomlStrings(Path.Combine(dir, "pack.toml"));
                id = pack.GetValueOrDefault("id");
                version = pack.GetValueOrDefault("version");
                name = pack.GetValueOrDefault("name");
            }
        }
        catch (Exception) { }
        static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } &&
            (value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
            value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
        id = ValidId(id) ? id : folder;
        return (ValidId(id) ? id : null, version ?? "0.0.0", name);
    }
}
