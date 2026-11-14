// SPDX-License-Identifier: MIT
// kiln.mod.json, manifest v2 (backward compatible with v1: id, name, version, apiVersion, entryAssembly, entryType,
// side; "clients" and "kilnBuild" added later; entryAssembly is optional since dev.22: a mod of data only has none).
// Self-contained (System.Text.Json + SemVer.cs + KilnBuild.cs): Kiln.Sdk's build
// task compiles this file too.
// Schema: schema/kiln.mod.schema.json. Docs: docs/kiln/modding/manifest.md.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kiln.Host;

internal enum ManifestSide
{
    Server = 0,
    Client = 1,
    Both = 2,
}

/// <summary>What a player's game needs to join a server that runs the mod (<c>"clients"</c>).</summary>
internal enum ManifestClients
{
    /// <summary>Nothing: vanilla clients join (server-side logic, replicated through the game's own paths).</summary>
    None = 0,
    /// <summary>Kiln on the client (it receives the mod's definitions, e.g. new items, from the server), not the mod.</summary>
    Kiln = 1,
    /// <summary>The mod itself on the client (assets, client code): delivered by the server's modpack.</summary>
    Mod = 2,
}

/// <summary><c>"boot"</c> of kiln.mod.json: none, <c>true</c> (a failure drops the mod's boot content) or <c>"required"</c> (a failure stops the start).</summary>
internal enum ManifestBoot
{
    None = 0,
    Optional = 1,
    Required = 2,
}

internal sealed record ManifestDependency(string Id, string RangeText, VersionRange Range, bool Optional);

internal sealed record ManifestIncompatibility(string Id, string RangeText, VersionRange Range);

internal sealed record ManifestTests(bool AutoRun, int TimeoutSeconds, string? Filter);

internal sealed class Manifest
{
    public const string FileName = "kiln.mod.json";

    /// <summary>Ids a mod may depend on that are not mods: <c>kiln</c> (the native core's version).</summary>
    public const string KilnId = "kiln";

    public static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);
    // "native" and "permissions" would collide with <configDir>/native.json and permissions.json.
    private static readonly HashSet<string> Reserved = ["kiln", "kiln.api", "game", "enshrouded", "native", "permissions"];

    private static readonly HashSet<string> Known =
    [
        "$schema", "id", "name", "version", "authors", "author", "description", "license", "url", "apiVersion", "side",
        "clients", "dependencies", "loadBefore", "loadAfter", "incompatible", "features", "entryAssembly", "entryType", "exports",
        "advanced", "tests", "kilnBuild", "writes", "boot",
    ];

    /// <summary>A component's short name, as kiln.world takes it in <c>"writes"</c>.</summary>
    private static readonly Regex ComponentName = new(@"\A[A-Za-z0-9_]{1,120}\z", RegexOptions.CultureInvariant);

    /// <summary>The most entries <c>"writes"</c> may hold, a repeated name counted each time (kiln.world: boot_programs.h,
    /// kBootWrites; the schema's maxItems). The same rule in the same order as the native reader
    /// (native/services/world/tests/parity.json holds the cases both readers are tested on).</summary>
    public const int MaxWrites = 64;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required SemVersion ParsedVersion { get; init; }
    public required string ApiVersion { get; init; }
    public required VersionRange ApiRange { get; init; }
    /// <summary>The mod's assembly (<c>"entryAssembly"</c>); empty for a mod of data only (content packs, assets), which
    /// has no code: Kiln.Host loads it as a mod with nothing to run, so it takes its place in the load order and its
    /// data packs apply.</summary>
    public string EntryAssembly { get; init; } = "";

    public bool HasCode => EntryAssembly.Length > 0;
    public string? EntryType { get; init; }
    public ManifestSide Side { get; init; } = ManifestSide.Server;

    /// <summary>What joining players need; default from <see cref="Side"/>: server → none, client/both → mod.</summary>
    public ManifestClients Clients { get; init; } = ManifestClients.None;
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string Description { get; init; } = "";
    public string? License { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<ManifestDependency> Dependencies { get; init; } = [];
    public IReadOnlyList<string> LoadBefore { get; init; } = [];
    public IReadOnlyList<string> LoadAfter { get; init; } = [];
    public IReadOnlyList<ManifestIncompatibility> Incompatible { get; init; } = [];
    public IReadOnlyList<string> Features { get; init; } = [];
    public IReadOnlyList<string> Exports { get; init; } = [];
    public bool Advanced { get; init; }

    /// <summary><c>"boot"</c>: the mod's Mod.OnBoot runs before the game is entered (chapter 08).</summary>
    public ManifestBoot Boot { get; init; }
    public ManifestTests? Tests { get; init; }

    /// <summary>The Kiln build the mod was compiled against (<c>"kilnBuild"</c>, written by Kiln.Sdk); null when unknown.</summary>
    public KilnBuild? BuiltWith { get; init; }

    /// <summary>The component types the mod writes through kiln.world's tracked path (<c>"writes"</c>: short names such as
    /// <c>Fuel</c>; Kiln.Sdk adds the types the mod's code passes to <c>World.Write</c> and <c>World.Update</c>). The
    /// native world service reads the list when the server starts and gives its write program access to them.</summary>
    public IReadOnlyList<string> Writes { get; init; } = [];

    /// <summary>Why <c>"writes"</c> was not read (it is then empty and the mod's tracked writes are refused); null when it was
    /// read or absent. The mod loads either way, with a warning.</summary>
    public string? WritesError { get; init; }

    /// <summary>Non-fatal findings (unknown keys, odd values).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public static Manifest Read(string directory) => Parse(File.ReadAllText(Path.Combine(directory, FileName)));

    /// <summary>Parses and validates a manifest; throws <see cref="InvalidDataException"/> naming the bad field.</summary>
    public static Manifest Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{FileName}: not valid JSON ({e.Message})");
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad("the file must contain one JSON object");
            var warnings = new List<string>();
            foreach (var p in root.EnumerateObject())
                if (!Known.Contains(p.Name))
                    warnings.Add($"unknown key '{p.Name}' (ignored)");

            var id = Str(root, "id") ?? throw Bad("'id' is required");
            if (!IdPattern.IsMatch(id)) throw Bad($"id '{id}' must match {IdPattern}");
            if (Reserved.Contains(id)) throw Bad($"id '{id}' is reserved");

            var version = Str(root, "version") ?? throw Bad("'version' is required");
            if (!SemVersion.TryParse(version, out var parsed))
                throw Bad($"version '{version}' is not a semantic version (e.g. 1.2.0)");

            var api = Str(root, "apiVersion") ?? throw Bad("'apiVersion' is required (the Kiln API version the mod was built for, e.g. \"0.2\")");
            VersionRange apiRange;
            if (SemVersion.TryParse(api, out var apiMin)) apiRange = VersionRange.AtLeastSameMajor(apiMin);
            else if (!VersionRange.TryParse(api, out apiRange, out var apiError)) throw Bad($"apiVersion: {apiError}");

            var entry = Str(root, "entryAssembly");  // none: a mod of data only
            if (entry is not null && !IsDllName(entry)) throw Bad($"entryAssembly '{entry}' must be a .dll file name in the mod folder");

            var side = (Str(root, "side") ?? "server") switch
            {
                "server" => ManifestSide.Server,
                "client" => ManifestSide.Client,
                "both" => ManifestSide.Both,
                var s => throw Bad($"side '{s}' must be server, client or both"),
            };

            var clients = Str(root, "clients") switch
            {
                null => DefaultClients(side),
                "none" => ManifestClients.None,
                "kiln" => ManifestClients.Kiln,
                "mod" => ManifestClients.Mod,
                var c => throw Bad($"clients '{c}' must be none, kiln or mod"),
            };

            var authors = Strings(root, "authors", allowSingle: true);
            if (authors.Count == 0 && Str(root, "author") is { } author) authors = [author];

            var deps = ParseDependencies(root, id);
            var exports = Strings(root, "exports");
            foreach (var x in exports)
                if (!IsDllName(x))
                    throw Bad($"exports: '{x}' must be a .dll file name in the mod folder");

            // As kiln.world reads it at boot (boot_programs.h parse_boot_writes). A list it cannot take never costs the
            // mod its loading: before tracked writes "writes" was a key nobody read, and a mod that loaded then loads now.
            var (writes, writesError) = ParseWrites(root);
            if (writesError is not null)
                warnings.Add($"\"writes\" is not read, so this mod's tracked writes are refused (the mod loads): {writesError}");

            var loadBefore = Ids(root, "loadBefore");
            var loadAfter = Ids(root, "loadAfter");
            if (loadBefore.Contains(id) || loadAfter.Contains(id)) throw Bad("loadBefore/loadAfter must not name the mod itself");
            foreach (var x in loadBefore.Intersect(loadAfter))
                throw Bad($"'{x}' is in both loadBefore and loadAfter");

            // never a reason to refuse a mod: a stamp Kiln can't read is ignored
            KilnBuild? builtWith = null;
            if (root.TryGetProperty("kilnBuild", out var kb) && kb.ValueKind != JsonValueKind.Null)
            {
                if (kb.ValueKind == JsonValueKind.Object && kb.TryGetProperty("version", out var kv) && kv.ValueKind == JsonValueKind.String)
                    builtWith = KilnBuild.From(kv.GetString(), kb.TryGetProperty("created", out var kc) && kc.ValueKind == JsonValueKind.String ? kc.GetString() : null);
                if (builtWith == null) warnings.Add("kilnBuild must be {\"version\": \"<semver>+<build>\", \"created\": \"<UTC time>\"} (ignored)");
            }

            var advanced = false;
            if (root.TryGetProperty("advanced", out var adv))
                advanced = adv.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw Bad("advanced must be true or false"),
                };

            // "boot": the mod's Mod.OnBoot runs before the game is entered (chapter 08); "required": a failure of its boot content stops the
            // start, true: it drops only that mod's boot contribution
            var boot = ManifestBoot.None;
            if (root.TryGetProperty("boot", out var bt))
                boot = bt.ValueKind switch
                {
                    JsonValueKind.True => ManifestBoot.Optional,
                    JsonValueKind.False => ManifestBoot.None,
                    JsonValueKind.String when bt.GetString() == "required" => ManifestBoot.Required,
                    _ => throw Bad("boot must be true, false or \"required\""),
                };

            return new Manifest
            {
                Id = id,
                Boot = boot,
                Name = Str(root, "name") ?? id,
                Version = version,
                ParsedVersion = parsed,
                ApiVersion = api,
                ApiRange = apiRange,
                EntryAssembly = entry ?? "",
                EntryType = Str(root, "entryType"),
                Side = side,
                Clients = clients,
                Authors = authors,
                Description = Str(root, "description") ?? "",
                License = Str(root, "license"),
                Url = Str(root, "url"),
                Dependencies = deps,
                LoadBefore = loadBefore,
                LoadAfter = loadAfter,
                Incompatible = ParseIncompatible(root, id),
                Features = Strings(root, "features"),
                Exports = exports,
                Advanced = advanced,
                Tests = ParseTests(root),
                BuiltWith = builtWith,
                Writes = writes,
                WritesError = writesError,
                Warnings = warnings,
            };
        }
    }

    private static InvalidDataException Bad(string message) => new($"{FileName}: {message}");

    /// <summary>A server mod works with vanilla clients; a mod that runs on clients is needed there.</summary>
    public static ManifestClients DefaultClients(ManifestSide side) => side == ManifestSide.Server ? ManifestClients.None : ManifestClients.Mod;

    /// <summary>The manifest spelling of <paramref name="clients"/>: none, kiln or mod.</summary>
    public static string ClientsName(ManifestClients clients) => clients switch
    {
        ManifestClients.Kiln => "kiln",
        ManifestClients.Mod => "mod",
        _ => "none",
    };

    private static bool IsDllName(string s) =>
        s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && s.Length > 4 && s.IndexOfAny(['/', '\\', ':']) < 0 && s != "..";

    private static string? Str(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw Bad($"'{key}' must be a string");
        return v.GetString();
    }

    /// <summary>"writes": a list (null is none), at most 64 entries as written, each a component's short name; a repeated
    /// name is then read once. Anything else: no names and the reason (the same rule and order as boot_programs.h
    /// parse_boot_writes; native/services/world/tests/parity.json holds the cases both are tested on).</summary>
    internal static (IReadOnlyList<string> Writes, string? Error) ParseWrites(JsonElement root)
    {
        if (!root.TryGetProperty("writes", out var v)) return ([], null);
        if (v.ValueKind != JsonValueKind.Array) return ([], $"it must be a list of at most {MaxWrites} component names, not {v.ValueKind.ToString().ToLowerInvariant()}");
        if (v.GetArrayLength() > MaxWrites) return ([], $"it lists {v.GetArrayLength()} entries; {MaxWrites} at most (a repeated name counts each time)");
        var names = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            string? name = null;
            if (e.ValueKind == JsonValueKind.String)
            {
                try
                {
                    name = e.GetString();
                }
                catch (InvalidOperationException)
                {
                    // an escape that is no text (half a surrogate pair, "\uD800"): the parser took it, the decoder does not
                }
            }
            if (name is null || !ComponentName.IsMatch(name))
                return ([], $"'{(name ?? e.GetRawText()).ReplaceLineEndings("\\n")}' is not a component's short name (letters, digits and _, such as Fuel or CurrentTransform)");
            names.Add(name);
        }
        return ([.. names.Distinct(StringComparer.Ordinal)], null);
    }

    private static List<string> Strings(JsonElement root, string key, bool allowSingle = false)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return [];
        if (allowSingle && v.ValueKind == JsonValueKind.String) return [v.GetString()!];
        if (v.ValueKind != JsonValueKind.Array) throw Bad($"'{key}' must be an array of strings");
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(e.GetString()))
                throw Bad($"'{key}' must be an array of non-empty strings");
            list.Add(e.GetString()!);
        }
        return list;
    }

    private static List<string> Ids(JsonElement root, string key)
    {
        var ids = Strings(root, key);
        foreach (var x in ids)
            if (!IdPattern.IsMatch(x))
                throw Bad($"{key}: '{x}' is not a valid mod id");
        return ids;
    }

    private static VersionRange Range(string owner, string text)
    {
        if (!VersionRange.TryParse(text, out var range, out var error)) throw Bad($"{owner}: {error}");
        return range;
    }

    /// <summary><c>{"id": "range"}</c>, <c>{"id": {"version": "range", "optional": true}}</c>, or
    /// <c>[{"id": "...", "version": "...", "optional": false}]</c>.</summary>
    private static List<ManifestDependency> ParseDependencies(JsonElement root, string self)
    {
        var list = new List<ManifestDependency>();
        if (!root.TryGetProperty("dependencies", out var v) || v.ValueKind == JsonValueKind.Null) return list;
        void Add(string id, JsonElement spec)
        {
            if (id != KilnId && !IdPattern.IsMatch(id)) throw Bad($"dependencies: '{id}' is not a valid mod id");
            if (id == self) throw Bad("a mod cannot depend on itself");
            if (list.Exists(d => d.Id == id)) throw Bad($"dependencies: '{id}' is listed twice");
            string text;
            var optional = false;
            switch (spec.ValueKind)
            {
                case JsonValueKind.String:
                    text = spec.GetString()!;
                    break;
                case JsonValueKind.Object:
                    text = spec.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.String ? ver.GetString()! : "*";
                    if (spec.TryGetProperty("optional", out var opt))
                        optional = opt.ValueKind == JsonValueKind.True ||
                                   (opt.ValueKind != JsonValueKind.False ? throw Bad($"dependencies.{id}.optional must be true or false") : false);
                    break;
                default:
                    throw Bad($"dependencies.{id} must be a version range string or an object");
            }
            list.Add(new ManifestDependency(id, text.Trim().Length == 0 ? "*" : text.Trim(), Range($"dependency '{id}'", text), optional));
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in v.EnumerateObject()) Add(p.Name, p.Value);
                break;
            case JsonValueKind.Array:
                foreach (var e in v.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                        throw Bad("dependencies: each entry needs a string 'id'");
                    Add(idEl.GetString()!, e);
                }
                break;
            default:
                throw Bad("dependencies must be an object (id -> range) or an array");
        }
        return list;
    }

    /// <summary><c>["id", ...]</c> (any version) or <c>{"id": "range"}</c>.</summary>
    private static List<ManifestIncompatibility> ParseIncompatible(JsonElement root, string self)
    {
        var list = new List<ManifestIncompatibility>();
        if (!root.TryGetProperty("incompatible", out var v) || v.ValueKind == JsonValueKind.Null) return list;
        void Add(string id, string text)
        {
            if (!IdPattern.IsMatch(id)) throw Bad($"incompatible: '{id}' is not a valid mod id");
            if (id == self) throw Bad("a mod cannot be incompatible with itself");
            list.Add(new ManifestIncompatibility(id, text, Range($"incompatible '{id}'", text)));
        }
        switch (v.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var e in v.EnumerateArray())
                    Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : throw Bad("incompatible: entries must be mod ids"), "*");
                break;
            case JsonValueKind.Object:
                foreach (var p in v.EnumerateObject())
                    Add(p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : throw Bad($"incompatible.{p.Name} must be a range"));
                break;
            default:
                throw Bad("incompatible must be an array of ids or an object (id -> range)");
        }
        return list;
    }

    private static ManifestTests? ParseTests(JsonElement root)
    {
        if (!root.TryGetProperty("tests", out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return new ManifestTests(v.ValueKind == JsonValueKind.True, 0, null);
        if (v.ValueKind != JsonValueKind.Object) throw Bad("tests must be true/false or an object");
        var auto = v.TryGetProperty("autoRun", out var a) && a.ValueKind == JsonValueKind.True;
        var timeout = v.TryGetProperty("timeoutSeconds", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
        if (timeout < 0) throw Bad("tests.timeoutSeconds must be >= 0");
        var filter = v.TryGetProperty("filter", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        return new ManifestTests(auto, timeout, filter);
    }
}
