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
                var (id, version, name) = EngineIdentity(dir, kind);
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
                {
                    if (!TryTomlStrings(Path.Combine(dir, "pack.toml"), out var pack)) return null;
                    // Enshwick consumes pack.toml, not another engine's mod.json beside it.
                    return pack.GetValueOrDefault("side") != "server";
                }
                return HasEnshwickDll(dir) ? true : null;
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

    // A deliberately conservative TOML metadata reader shared in policy with kiln-manage. Unsupported
    // containers fail closed; a line scanner must never interpret multiline text as identity or side.
    private const int MaxPackBytes = 1024 * 1024;

    internal static Dictionary<string, string> TomlStrings(string file) =>
        TryTomlStrings(file, out var result) ? result : new(StringComparer.Ordinal);

    private static bool TryTomlStrings(string file, out Dictionary<string, string> result)
    {
        result = new(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(file);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxPackBytes) return false;
            // Bound the read itself, including a file which grows after the metadata check.
            using var stream = File.OpenRead(file);
            var bytes = new byte[MaxPackBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = stream.Read(bytes, count, bytes.Length - count);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxPackBytes) return false;
            var text = new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, count);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\u007f' || (char.IsControl(text[i]) && text[i] is not ('\t' or '\n' or '\r')) ||
                    (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))) return false;
            var reader = new PackMetadataReader(text);
            if (!reader.Read(out var parsed)) return false;
            result = parsed;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException) { return false; }
    }

    private static bool PackId(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, @"^[a-z0-9][a-z0-9._-]{0,63}$") ||
            value.EndsWith('.') || value == "enshwick") return false;
        var stem = value.Split('.')[0];
        return stem is not ("con" or "prn" or "aux" or "nul") &&
            !(stem.Length == 4 && (stem.StartsWith("com", StringComparison.Ordinal) || stem.StartsWith("lpt", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    private static bool PackVersion(string value)
    {
        // Match kiln-manage's canonical SemVersion text: three i32 components, no v prefix or whitespace.
        var match = System.Text.RegularExpressions.Regex.Match(value,
            @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$");
        if (!match.Success) return false;
        for (var i = 1; i <= 3; i++)
            if (!int.TryParse(match.Groups[i].Value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _)) return false;
        return !match.Groups[4].Success || match.Groups[4].Value.Split('.').All(part =>
            part.Length == 1 || part[0] != '0' || !part.All(char.IsAsciiDigit));
    }

    private sealed class PackMetadataReader(string text)
    {
        private int _at;
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
        private void Space() { while (_at < text.Length && text[_at] is ' ' or '\t') _at++; }
        private void Comment() { if (_at < text.Length && text[_at] == '#') while (_at < text.Length && text[_at] != '\n') _at++; }
        private bool EndLine()
        {
            Space(); Comment();
            if (_at == text.Length) return true;
            if (text[_at] == '\r') _at++;
            return _at < text.Length && text[_at++] == '\n';
        }
        private static bool Bare(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
        private static bool Metadata(string key) => key is "id" or "version" or "name" or "side";
        internal bool Read(out Dictionary<string, string> result)
        {
            result = _strings;
            while (_at < text.Length)
            {
                Space(); Comment();
                if (_at == text.Length) break;
                if (text[_at] is '\r' or '\n') { if (!EndLine()) return false; continue; }
                // A table switches out of the root. Do not interpret any table values as metadata.
                if (text[_at] == '[') return Table();
                string key;
                if (text[_at] is '\'' or '"')
                {
                    if (!String(out key, false)) return false;
                }
                else
                {
                    var start = _at;
                    while (_at < text.Length && Bare(text[_at])) _at++;
                    key = text[start.._at];
                    if (key.Length == 0) return false;
                }
                if (!_keys.Add(key)) return false;
                Space();
                if (_at == text.Length || text[_at++] != '=') return false;
                Space();
                if (_at == text.Length) return false;
                if (text[_at] is '\'' or '"')
                {
                    if (!String(out var value, true)) return false;
                    if (Metadata(key))
                    {
                        var limit = key == "id" ? 64 : key == "side" ? 16 : 128;
                        if (System.Text.Encoding.UTF8.GetByteCount(value) > limit || value.Any(char.IsControl)) return false;
                        if (key == "id" && !PackId(value) || key == "version" && !PackVersion(value)) return false;
                    }
                    _strings.Add(key, value);
                }
                else
                {
                    if (Metadata(key)) return false;
                    var start = _at;
                    while (_at < text.Length && text[_at] is not ('#' or '\r' or '\n')) _at++;
                    var value = text[start.._at].TrimEnd(' ', '\t');
                    // Unsupported values (including containers) must never leak their inner lines.
                    if (value is not ("true" or "false") &&
                        !System.Text.RegularExpressions.Regex.IsMatch(value, @"^[+-]?(0|[1-9][0-9]*)$")) return false;
                }
                if (!EndLine()) return false;
            }
            return true;
        }
        private bool Table()
        {
            // Only simple dotted bare table names are supported by this conservative reader.
            _at++;
            if (_at < text.Length && text[_at] == '[') return false;
            Space();
            while (true)
            {
                var start = _at;
                while (_at < text.Length && Bare(text[_at])) _at++;
                if (start == _at) return false;
                Space();
                if (_at < text.Length && text[_at] == '.') { _at++; Space(); continue; }
                break;
            }
            if (_at == text.Length || text[_at++] != ']') return false;
            return EndLine();
        }
        private bool String(out string value, bool multilineAllowed)
        {
            value = "";
            var quote = text[_at++];
            var multiline = _at + 1 < text.Length && text[_at] == quote && text[_at + 1] == quote;
            if (multiline)
            {
                if (!multilineAllowed) return false;
                _at += 2;
                if (_at < text.Length && text[_at] == '\r')
                {
                    _at++;
                    if (_at == text.Length || text[_at] != '\n') return false;
                    _at++;
                }
                else if (_at < text.Length && text[_at] == '\n') _at++;
            }
            var output = new System.Text.StringBuilder();
            while (_at < text.Length)
            {
                var c = text[_at++];
                if (c == quote)
                {
                    if (!multiline) { value = output.ToString(); return true; }
                    var count = 1;
                    while (_at < text.Length && text[_at] == quote) { count++; _at++; }
                    if (count >= 3)
                    {
                        if (count > 5) return false;
                        output.Append(quote, count - 3);
                        value = output.ToString(); return true;
                    }
                    output.Append(quote, count); continue;
                }
                if (c == '\r')
                {
                    if (!multiline || _at == text.Length || text[_at++] != '\n') return false;
                    output.Append('\n'); continue;
                }
                if (c == '\n') { if (!multiline) return false; output.Append(c); continue; }
                if (char.IsControl(c) && c != '\t' || c == '\u007f') return false;
                if (c != '\\' || quote == '\'') { output.Append(c); continue; }
                if (_at == text.Length) return false;
                c = text[_at++];
                if (multiline && c is ' ' or '\t' or '\r' or '\n')
                {
                    while (c is ' ' or '\t') { if (_at == text.Length) return false; c = text[_at++]; }
                    if (c == '\r') { if (_at == text.Length || text[_at++] != '\n') return false; }
                    else if (c != '\n') return false;
                    while (_at < text.Length && text[_at] is ' ' or '\t' or '\r' or '\n') _at++;
                    continue;
                }
                switch (c)
                {
                    case 'b': output.Append('\b'); break;
                    case 't': output.Append('\t'); break;
                    case 'n': output.Append('\n'); break;
                    case 'f': output.Append('\f'); break;
                    case 'r': output.Append('\r'); break;
                    case '"': output.Append('"'); break;
                    case '\\': output.Append('\\'); break;
                    case 'u': case 'U':
                        var digits = c == 'u' ? 4 : 8;
                        if (_at + digits > text.Length || !uint.TryParse(text.AsSpan(_at, digits),
                            System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var scalar) ||
                            scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff) return false;
                        output.Append(char.ConvertFromUtf32((int)scalar)); _at += digits; break;
                    default: return false;
                }
            }
            return false;
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

    internal static (string? Id, string Version, string? Name) EngineIdentity(string dir, string? kind = null)
    {
        var folder = Path.GetFileName(dir);
        string? id = null, version = null, name = null;
        try
        {
            var manifest = Path.Combine(dir, "mod.json");
            if (File.Exists(manifest) && kind != "enshwick")
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
                if (!TryTomlStrings(Path.Combine(dir, "pack.toml"), out var pack)) return (null, "0.0.0", null);
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
        return (id is not null && ValidId(id) && (kind != "enshwick" || PackId(id)) ? id : null, SafeText(version) ?? "0.0.0", SafeText(name));
        static string? SafeText(string? value) => value is { Length: <= 128 } && !value.Any(char.IsControl) ? value : null;
    }
}
