// SPDX-License-Identifier: MIT
// Other engines' mods a server runs, for the advertised required[] (contracts.md 5.8, docs/kiln/engines.md): each
// engine's server folder (the legacy layout kiln-manage's server sync and engine_mods::server_root use), with the
// mod's kind ("eml", "flame" ...) so a joining player's launcher can tell what the server needs and the modpack carries
// it. The HELLO check stays on Kiln mods: clients don't announce other engines' mods.
// clients: the mod.json's stated "clients" wins; else EML, ShroudForge and Flame mods are needed (they change data both
// sides load), the others are not.
using System.Text.Json;

namespace Kiln;

internal static partial class NetAdvertise
{
    private static readonly (string Kind, string Folder)[] EngineFolders =
    [
        ("emf", "emf/mods"), ("eml", "kiln/eml/mods"), ("shroudforge", "mods"), ("set", "shrouded_engine_tweaks/mods"),
        ("greeneye", "greeneye"), ("flame", "mods"), ("shroudtopia", "mods"),
    ];

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
        }
        catch (Exception) { }
        static bool ValidId(string? value) => value is { Length: > 0 and <= 64 } &&
            (value[0] is >= 'a' and <= 'z' or >= '0' and <= '9') &&
            value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-');
        id = ValidId(id) ? id : folder;
        return (ValidId(id) ? id : null, version ?? "0.0.0", name);
    }
}
