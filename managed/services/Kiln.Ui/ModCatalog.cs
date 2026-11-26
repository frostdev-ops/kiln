// SPDX-License-Identifier: MIT
// Every mod of this game session, of every engine Kiln runs: Kiln's managed mods (Mods.All), native DLL mods, Kiln
// native plugins, chain DLLs, and the mods of EMF, EML, ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick,
// shroudlink and Ember. Sources: Mods.All, the state files under <game>/kiln/state/ (docs/kiln/launcher/contracts.md
// 5.9 and each engine's page), and the active profile for mods that are listed but not reported. The state words are
// normalised as the launcher does (launcher/src/components/EngineStatus.tsx headline / modLines), so both show the same.
//
// Nothing here draws: a dedicated server can read it too (Read). The mod manager screen calls Snapshot from the render
// thread; that never touches a file: it answers with the last snapshot and starts a refresh on a worker when it is old.
// While nobody asks (the menu closed) nothing runs.
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Kiln;

/// <summary>What happened to a mod this start, in the words a player sees.</summary>
internal enum ModRunState
{
    Loaded = 0,
    /// <summary>Found and accepted, not started yet (it waits for its turn, a world, or its engine).</summary>
    Waiting = 1,
    /// <summary>Switched off (in the profile, the engine's own switch, or after too many errors).</summary>
    Disabled = 2,
    /// <summary>Did not load because something is wrong (an error, a missing folder or dependency).</summary>
    Failed = 3,
    /// <summary>Known but not loaded this start, and nothing is wrong (another side, the engine stepped aside...).</summary>
    NotLoaded = 4,
}

/// <summary>The engine's overall state as a mark next to its name.</summary>
internal enum EngineHealth
{
    Ok = 0,
    Idle = 1,
    Problem = 2,
}

/// <summary>One mod.</summary>
internal sealed record CatalogMod
{
    /// <summary>The engine kind: kiln, native, plugin, chain, emf, eml, xhl, shroudforge, set, greeneye, flame, shroudtopia,
    /// enshwick, shroudlink, ember.</summary>
    public required string Engine { get; init; }
    public required string Id { get; init; }
    /// <summary>What tells this row apart from its engine's other rows where <see cref="Id"/> repeats (every proxy DLL
    /// mod is a dinput8.dll): its path. Null: the id. The mod manager makes its ImGui ids and its selection from it
    /// (<see cref="ModManagerLogic.KeyOf"/>); a player never sees it.</summary>
    public string? Key { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public ModRunState State { get; init; }
    /// <summary>The engine's own word (loaded, failed, waiting-for-abi...), for the details.</summary>
    public string Word { get; init; } = "";
    /// <summary>Why it is in this state (the engine's reason or error), or null.</summary>
    public string? Reason { get; init; }
    /// <summary>What the player can do about it, or null.</summary>
    public string? Help { get; init; }
    public string? Folder { get; init; }
    /// <summary>client, server, both, or "" when the engine does not say.</summary>
    public string Side { get; init; } = "";
    /// <summary>Where Kiln found it: profile, folder (the engine's own folder), dropin, chain...</summary>
    public string Source { get; init; } = "";
    /// <summary>The profile lists it but its folder is not there.</summary>
    public bool Missing { get; init; }
    /// <summary>Kiln mods: position in the load order, -1 when not loaded.</summary>
    public int LoadIndex { get; init; } = -1;
    /// <summary>Kiln mods: the dependencies its manifest declares.</summary>
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];
    /// <summary>ShroudForge mods: its runtime runs it in this process now (its current state.json lists it active).</summary>
    public bool Active { get; init; }
    // a kiln-backend start's ShroudForge row: the interpreter's part truth for the page
    /// <summary>ShroudForge mods of a start Kiln's interpreter has (SfiCatalog): what the page draws beyond the state.</summary>
    public SfiRow? Sfi { get; init; }

    /// <summary>Loaded and running, with a problem the mod reported and handled itself (a ShroudForge mod's
    /// <c>write-failed</c>: its last change did not apply). Drawn in the attention colour, never as failed.</summary>
    public bool Warn { get; init; }

    public bool NeedsAttention => State == ModRunState.Failed || Missing;

    /// <summary>The state chip's text.</summary>
    public string Chip => Missing ? KilnText.T("catalog.chip.missing") : ModCatalog.ChipText(State);

    /// <summary>Lower-case name, id and engine, for the search box.</summary>
    public string SearchText { get; init; } = "";
}

/// <summary>One engine (a group of mods).</summary>
internal sealed record CatalogEngine
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    /// <summary>Its overall state word (EngineStatus.tsx headline), or null.</summary>
    public string? Word { get; init; }
    public EngineHealth Health { get; init; } = EngineHealth.Ok;
    /// <summary>A short plain sentence about its overall state when it matters ("Runtime hosted by Kiln."), or null.</summary>
    public string? Note { get; init; }
    public int Count { get; init; }
    public int Problems { get; init; }
    /// <summary>Its state file wrote this start (false: missing, stale or unreadable; true for Kiln's own kinds).</summary>
    public bool Reported { get; init; }
}

/// <summary>Every mod of this session at one moment.</summary>
internal sealed record CatalogSnapshot
{
    public IReadOnlyList<CatalogEngine> Engines { get; init; } = [];
    /// <summary>Every mod, engines in <see cref="ModCatalog.EngineOrder"/>, then load order / profile order / name.</summary>
    public IReadOnlyList<CatalogMod> Mods { get; init; } = [];
    public long ReadAtMs { get; init; }
    /// <summary>One line about the profile ("Profile 'Ember run'", "No profile: the mods folders"), or null.</summary>
    public string? Profile { get; init; }
    /// <summary>client or server.</summary>
    public string Side { get; init; } = "";
    /// <summary>Bumped for every new snapshot (the screen re-filters only when it changes).</summary>
    public long Version { get; init; }
    /// <summary>The <see cref="KilnText.Generation"/> its sentences were made in (null: any; a test's or the preview's).</summary>
    public int? Generation { get; init; }

    public int Problems => Mods.Count(m => m.NeedsAttention);
    public static readonly CatalogSnapshot Empty = new();
}

/// <summary>A Kiln mod as <see cref="Mods.All"/> reports it (the seam tests and the preview fill).</summary>
internal sealed record KilnModInput(string Id, string Name, string Version, string Directory, ModSide Side, ModState State,
    string? Error, IReadOnlyList<ModDependency> Dependencies, int LoadIndex);

/// <summary>Where <see cref="ModCatalog.Read"/> looks.</summary>
internal sealed record CatalogSources
{
    /// <summary>The kiln folder (<c>&lt;game&gt;/kiln</c>).</summary>
    public required string KilnRoot { get; init; }
    public string StateDir => Path.Combine(KilnRoot, "state");
    /// <summary>The game folder (state paths win when boot.json names them).</summary>
    public string GameDir => Path.GetDirectoryName(Path.GetFullPath(KilnRoot)) ?? KilnRoot;
    public Func<IReadOnlyList<KilnModInput>> KilnMods { get; init; } = () => [];
    /// <summary>This process: a state file with another pid, or written before <see cref="ProcessStartUtc"/>, is from an
    /// earlier start.</summary>
    public int Pid { get; init; } = Environment.ProcessId;
    public DateTime ProcessStartUtc { get; init; } = DateTime.MinValue;
    public string Side { get; init; } = "";
    // which branch the ShroudForge group takes this start, decided once per read
    /// <summary>Set by <see cref="ModCatalog.Read"/> (SfiCatalog.Decide) before any ShroudForge path; null until then.</summary>
    public SfiBranch? Sfi { get; init; }
}

internal static class ModCatalog
{
    /// <summary>Engines in the order the screen lists them (the launcher's MOD_KINDS, with chain DLLs after plugins).</summary>
    public static readonly string[] EngineOrder =
        ["kiln", "native", "plugin", "chain", "emf", "eml", "xhl", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember", "dllmod"];

    /// <summary>The names players see (the launcher's KIND_SHORT, plain words for Kiln's own kinds).</summary>
    public static string EngineName(string kind) => kind switch
    {
        "kiln" => "Kiln", "native" => KilnText.T("catalog.engine.native"), "plugin" => KilnText.T("catalog.engine.plugin"),
        "chain" => KilnText.T("catalog.engine.chain"), "emf" => "EMF",
        "eml" => "EML", "xhl" => "XHL", "shroudforge" => "ShroudForge", "set" => "SET", "greeneye" => "Greeneye", "flame" => "Flame",
        "shroudtopia" => "Shroudtopia", "enshwick" => "Enshwick", "shroudlink" => "shroudlink", "ember" => "Ember", "dllmod" => "Enshrouded ModLoader", _ => kind,
    };

    /// <summary>One line about what the engine is, for the details.</summary>
    public static string EngineAbout(string kind) => EngineOrder.Contains(kind) ? KilnText.T("catalog.about." + kind) : "";

    public static string ChipText(ModRunState s) => s switch
    {
        ModRunState.Loaded => KilnText.T("catalog.chip.loaded"), ModRunState.Waiting => KilnText.T("catalog.chip.waiting"),
        ModRunState.Disabled => KilnText.T("catalog.chip.off"), ModRunState.Failed => KilnText.T("catalog.chip.failed"),
        _ => KilnText.T("catalog.chip.not_loaded"),
    };

    // Kiln's engine plugins (boot.json plugins[]): their own load result is the engine's, not a mod.
    private static readonly Dictionary<string, string> EnginePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kiln-emf"] = "emf", ["kiln-eml"] = "eml", ["shroudforge"] = "shroudforge", ["kiln-shroudforge"] = "shroudforge",
        ["kiln-set"] = "set", ["kiln-greeneye"] = "greeneye", ["kiln-flame"] = "flame", ["kiln-shroudtopia"] = "shroudtopia",
        ["kiln-enshwick"] = "enshwick", ["kiln-shroudlink"] = "shroudlink", ["kiln-ember"] = "ember", ["kiln-dllmod"] = "dllmod",
    };

    // ------------------------------------------------------------------------------------------- normalisation ----
    // Ports of EngineStatus.tsx (keep in step): headline(), toneOf(), modLines().

    private static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        try
        {
            return v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            };
        }
        catch (InvalidOperationException) { return null; }  // a lone surrogate escape ("\ud83d", a cut emoji): no string to show
    }

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    /// <summary>EngineStatus.tsx headline: status, phase, mode, state; then steppedAside, Ember's abi null, started.</summary>
    public static string? Headline(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) return null;
        var word = Str(state, "status") ?? Str(state, "phase") ?? Str(state, "mode") ?? Str(state, "state");
        if (word is not null) return word;
        if (Bool(state, "steppedAside") == true) return "stepped aside";
        if (state.TryGetProperty("abi", out var abi) && abi.ValueKind == JsonValueKind.Null) return "waiting for Ember's ABI";
        if (Bool(state, "started") is { } started) return started ? "running" : "idle";
        return null;
    }

    /// <summary>EngineStatus.tsx toneOf: err / idle / ok / info.</summary>
    public static string ToneOf(string? word)
    {
        if (word is null) return "idle";
        var w = word.ToLowerInvariant();
        if (w.Contains("fail") || w.Contains("error") || w.Contains("refus")) return "err";
        if (w.Contains("aside") || w.Contains("off") || w.Contains("not-installed") || w.Contains("skip") || w.Contains("disabled") ||
            w.Contains("idle") || w.Contains("waiting")) return "idle";
        if (w.Contains("done") || w.Contains("hosted") || w.Contains("loaded") || w.Contains("running") || w.Contains("active") ||
            w.Contains("started") || w.Contains("ok")) return "ok";
        return "info";
    }

    /// <summary>One <c>mods[]</c> entry as EngineStatus.tsx modLines reads it, plus the fields the screen shows.</summary>
    public sealed record ModLine(string Id, string? State, string? Reason, string? Name, string? Version, string? Folder, string? Side, string? Source);

    /// <summary>EngineStatus.tsx modLines: id ?? name ?? folder; state ?? status ?? action ?? active; reason ?? detail ??
    /// error ?? note.</summary>
    public static IReadOnlyList<ModLine> ModLines(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return [];
        var list = new List<ModLine>();
        foreach (var m in mods.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object) continue;
            var active = Bool(m, "active");
            list.Add(new ModLine(
                Str(m, "id") ?? Str(m, "name") ?? Str(m, "folder") ?? "?",
                Str(m, "state") ?? Str(m, "status") ?? Str(m, "action") ?? (active is { } a ? (a ? "active" : "off") : null),
                Str(m, "reason") ?? Str(m, "detail") ?? Str(m, "error") ?? Str(m, "note"),
                Str(m, "name"), Str(m, "version"), Str(m, "folder") ?? Str(m, "path") ?? Str(m, "dll"), Str(m, "side"),
                Str(m, "source") ?? Str(m, "origin")));
        }
        return list;
    }

    /// <summary>An engine's word for one mod, as a <see cref="ModRunState"/>.</summary>
    public static ModRunState StateOf(string? word)
    {
        if (word is null) return ModRunState.NotLoaded;
        var w = word.ToLowerInvariant();
        if (w.Contains("fail") || w.Contains("error") || w.Contains("refus") || w.Contains("reject") || w.Contains("fault") ||
            w is "missing" or "conflict" or "unsupported" or "not-loaded") return ModRunState.Failed;
        if (w is "disabled" or "off" or "inactive" or "policy" or "off-in-shroudforge") return ModRunState.Disabled;
        if (w.Contains("pending") || w.Contains("waiting") || w is "candidate" or "registered" or "starting" or "deferred" or "activating")
            return ModRunState.Waiting;
        if (w is "loaded" or "active" or "running" or "started" or "ok" or "done" or "hosted" or "script" or "copy" or "refresh" or
            "keep" or "run" or "already-loaded" or "own-loader") return ModRunState.Loaded;
        return ModRunState.NotLoaded;  // skipped, unloaded, duplicate, remove, idle, and words we do not know
    }

    // ----------------------------------------------------------------------------------------------- the files ----

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64,
    };

    private const long MaxFile = 8 << 20;

    /// <summary>A state file: its JSON (null when missing or unreadable), and whether it is from this start.</summary>
    internal readonly record struct StateFile(JsonElement? Doc, bool Exists, bool Current, string? Error);

    /// <summary>Reads a JSON file that another process may be writing: shared access, a size limit, no exceptions.</summary>
    internal static JsonElement? ReadJson(string path, out string? error)
    {
        error = null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxFile)
            {
                error = KilnText.T("catalog.file.too_large");
                return null;
            }
            using var doc = JsonDocument.Parse(fs, JsonOptions);
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            error = KilnText.T("catalog.file.invalid");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // ArgumentException / NotSupportedException: a path no file call takes (a NUL in a folder another config names)
            error = KilnText.T("catalog.file.unreadable", ("error", e.Message));
            return null;
        }
    }

    /// <summary>A state file of this start: a <c>pid</c> must be ours; else <c>updatedUnixMs</c>, <c>updated</c> (seconds)
    /// or <c>written</c> (ISO time), else the file's time, must not be older than the process (as Kiln.Eml does).</summary>
    internal static StateFile ReadState(CatalogSources src, string file)
    {
        var path = Path.Combine(src.StateDir, file);
        var doc = ReadJson(path, out var error);
        if (doc is not { } d) return new StateFile(null, error is not null || File.Exists(path), false, error);
        if (d.ValueKind != JsonValueKind.Object) return new StateFile(null, true, false, KilnText.T("catalog.file.not_object"));
        return new StateFile(d, true, IsCurrent(src, d, path), null);
    }

    internal static bool IsCurrent(CatalogSources src, JsonElement d, string path)
    {
        if (d.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt64(out var p) && p != src.Pid)
            return false;  // matching PIDs can be reused: the report must also be from this start
        if (src.ProcessStartUtc == DateTime.MinValue) return true;
        var start = src.ProcessStartUtc.AddSeconds(-2);  // clocks: a file written in the process's first moment
        DateTime? at = null;
        if (d.TryGetProperty("updatedUnixMs", out var ms) && ms.ValueKind == JsonValueKind.Number && ms.TryGetInt64(out var m))
            at = DateTime.UnixEpoch.AddMilliseconds(m);
        else if (d.TryGetProperty("updated", out var sec) && sec.ValueKind == JsonValueKind.Number && sec.TryGetInt64(out var s))
            at = DateTime.UnixEpoch.AddSeconds(s);
        else if (d.TryGetProperty("written", out var w) && w.ValueKind == JsonValueKind.String &&
                 DateTime.TryParse(w.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t))
            at = t;
        if (at is null)
        {
            try { at = File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return true; }
        }
        return at >= start;
    }

    // A profile entry (mods.<kind>[]).
    internal sealed record ProfileEntry(string Kind, string? Id, string? Version, string Path, bool Enabled, string? Hash = null);

    internal sealed class Profile
    {
        public string? Id, Name, Mode, Error;
        public bool DropinsAll;
        public HashSet<string> DropinKinds = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, List<ProfileEntry>> Mods = new(StringComparer.OrdinalIgnoreCase);
        public bool Lists(string kind) => Mods.ContainsKey(kind);
        public List<ProfileEntry> Of(string kind) => Mods.TryGetValue(kind, out var l) ? l : [];
        public bool ReadsFolder(string kind) => !Lists(kind) || DropinsAll || DropinKinds.Contains(kind);
    }

    private static Profile ReadProfile(CatalogSources src, JsonElement? boot)
    {
        var prof = new Profile();
        string? file = null;
        if (boot is { } b && b.TryGetProperty("profile", out var bp) && bp.ValueKind == JsonValueKind.Object)
        {
            prof.Mode = Str(bp, "mode");
            prof.Error = Str(bp, "error");
            file = Str(bp, "file");
        }
        if (prof.Mode is "no-mods" or "invalid") return prof;
        var candidates = new List<string>();
        if (file is not null) candidates.Add(file);
        candidates.Add(Path.Combine(src.KilnRoot, "profile.json"));
        JsonElement? doc = null;
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
            }
            catch (Exception) { continue; }
            doc = ReadJson(c, out _);
            if (doc is not null) break;
        }
        if (doc is not { ValueKind: JsonValueKind.Object } d) return prof;
        prof.Id ??= Str(d, "id");
        prof.Name = Str(d, "name");
        prof.Mode ??= "active";
        if (d.TryGetProperty("dropins", out var dr))
        {
            if (dr.ValueKind == JsonValueKind.True) prof.DropinsAll = true;
            else if (dr.ValueKind == JsonValueKind.Array)
                foreach (var k in dr.EnumerateArray())
                    if (k.ValueKind == JsonValueKind.String && k.GetString() is { } ks) prof.DropinKinds.Add(ks);
        }
        if (!d.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Object) return prof;
        foreach (var kind in mods.EnumerateObject())
        {
            if (kind.Value.ValueKind != JsonValueKind.Array) continue;
            var list = new List<ProfileEntry>();
            foreach (var e in kind.Value.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || Str(e, "path") is not { } path) continue;
                list.Add(new ProfileEntry(kind.Name, Str(e, "id"), Str(e, "version"), ResolvePath(src, path), Bool(e, "enabled") != false, Str(e, "hash")));
            }
            if (list.Count > 0) prof.Mods[kind.Name] = list;  // an empty list is written by no launcher: like no list
        }
        return prof;
    }

    /// <summary>A profile path as kiln-core resolves it: absolute, or relative to the kiln folder.</summary>
    internal static string ResolvePath(CatalogSources src, string p)
    {
        if (Path.IsPathRooted(p) || (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':')) return p;
        try { return Path.GetFullPath(Path.Combine(src.KilnRoot, p.Replace('\\', Path.DirectorySeparatorChar))); }
        catch (Exception) { return p; }
    }

    /// <summary>The Kiln id the launcher's Library gives an engine mod from the id its manifest gives (kiln-manage
    /// <c>library::id_from_name</c>, keep in step): lower case, anything but letters, digits and <c>._-</c> as a dash,
    /// dashes collapsed and trimmed, at most 64, "mod" when nothing is left. A profile entry carries that id; the engine's
    /// state rows carry the manifest's own (<c>Hoarders Helper</c>, <c>HoardersHelper</c>).</summary>
    internal static string IdFromName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
            sb.Append(c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' ? c : '-');
        var s = sb.ToString();
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-', '.', '_');
        if (s.Length > 64) s = s[..64];
        return s.Length == 0 ? "mod" : s;
    }

    /// <summary>A folder path as compared between a profile entry and an engine's row: one separator, no trailing one
    /// (the plugin writes the path it resolved, `Z:/home/...`; the profile has `Z:\home\...`), and no verbatim prefix
    /// (ShroudForge writes its mods folder from a canonical path on Windows, `\\?\C:\...\mods`, Kiln's game folder has
    /// none): `\\?\C:\x` is `C:\x`, `\\?\UNC\server\share` is `\\server\share`. Callers compare without regard to case,
    /// as Windows does (<see cref="SamePath"/>): the game runs on Windows or under Wine or Proton, with Windows paths.</summary>
    internal static string NormPath(string p)
    {
        var s = p.Replace('\\', '/');
        if (s.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase)) s = "//" + s[8..];
        else if (s.StartsWith("//?/", StringComparison.Ordinal)) s = s[4..];
        return s.TrimEnd('/');
    }

    /// <summary>Whether two paths name one folder as Windows compares them (<see cref="NormPath"/>, case ignored).</summary>
    internal static bool SamePath(string a, string b) => string.Equals(NormPath(a), NormPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool DirExists(string? p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        try { return Directory.Exists(p) || File.Exists(p); }
        catch (Exception) { return false; }
    }

    /// <summary>The folder a path lies in (either slash), null at a root.</summary>
    private static string? ParentOf(string p)
    {
        var t = p.TrimEnd('\\', '/');
        var i = t.LastIndexOfAny(['\\', '/']);
        return i > 0 ? t[..i] : null;
    }

    private static string LeafName(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var t = p.TrimEnd('\\', '/');
        var i = t.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? t[(i + 1)..] : t;
    }

    // A mod folder's display name from its manifest (cached per folder for the session: names do not change while it runs).
    private static readonly Dictionary<string, (string? Name, string? Version)> ManifestCache = new(StringComparer.OrdinalIgnoreCase);

    internal static (string? Name, string? Version) Manifest(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return (null, null);
        lock (ManifestCache)
            if (ManifestCache.TryGetValue(folder, out var hit)) return hit;
        (string?, string?) found = (null, null);
        foreach (var f in new[] { "kiln.mod.json", "native.json", "mod.json", "extended.mod.json", "flame.json", "shroudtopia.json" })
        {
            string path;
            try
            {
                path = Path.Combine(folder, f);
                if (!File.Exists(path) || new FileInfo(path).Length > 256 << 10) continue;
            }
            catch (Exception) { continue; }
            if (ReadJson(path, out _) is not { ValueKind: JsonValueKind.Object } d) continue;
            var name = Str(d, "name") ?? Str(d, "displayName") ?? Str(d, "title");
            var version = Str(d, "version");
            if (name is null && version is null) continue;
            found = (name, version);
            break;
        }
        lock (ManifestCache)
            if (ManifestCache.Count < 4096) ManifestCache[folder] = found;
        return found;
    }

    // ------------------------------------------------------------------------------------------------ building ----

    private sealed class Builder(CatalogSources src, Profile profile)
    {
        public readonly Dictionary<string, List<CatalogMod>> Mods = new();
        public readonly Dictionary<string, (string? Word, EngineHealth Health, string? Note, bool Reported)> Engines = new();
        public readonly CatalogSources Src = src;
        public readonly Profile Profile = profile;
        public readonly HashSet<string> MissingPaths = new(StringComparer.OrdinalIgnoreCase);

        public void Add(CatalogMod m)
        {
            if (!Mods.TryGetValue(m.Engine, out var l)) Mods[m.Engine] = l = [];
            // engines write their reasons in lower case ("switched off in the profile"): shown as a sentence
            if (m.Reason is { } r) m = m with { Reason = Sentence(r) };
            if (m.Help is null && m.State == ModRunState.Disabled && m.Engine != "kiln")
                m = m with { Help = KilnText.T("catalog.help.turn_on") };
            l.Add(m with { SearchText = $"{m.Name} {m.Id} {EngineName(m.Engine)}".ToLowerInvariant() });
        }

        /// <summary>The folder in <c>&lt;game&gt;/mods</c> that still holds a profile entry's copy: the entry's own folder
        /// (<paramref name="folder"/>, <see cref="FolderOf"/>) when it is placed there (XHL companions, EML mods a DLL reads
        /// there, ShroudForge's copies), Kiln's copy for Enshwick's loader.</summary>
        private string? KeptInGameMods(string kind, string folder, string id)
        {
            if (ParentOf(folder) is { } parent && SamePath(parent, Path.Combine(Src.GameDir, "mods")))
                return DirExists(folder) ? folder : null;
            if (kind != "enshwick") return null;
            string copy;
            try { copy = Path.Combine(Src.GameDir, "mods", id); }
            catch (Exception) { return null; }
            return DirExists(copy) ? copy : null;
        }

        public void Engine(string kind, string? word, EngineHealth health, string? note, bool reported) =>
            Engines[kind] = (word, health, note, reported);

        private readonly Dictionary<ProfileEntry, string?> _runFolders = new();

        /// <summary>The folder the engine runs a profile entry from when that is not the entry's path, else null
        /// (<see cref="EngineRunFolder"/>; read once per snapshot).</summary>
        public string? RunFolder(ProfileEntry e)
        {
            if (!_runFolders.TryGetValue(e, out var f)) _runFolders[e] = f = EngineRunFolder(Src, e);
            return f;
        }

        /// <summary>The folder a profile entry's row stands for: where its engine runs it (<see cref="RunFolder"/>), else
        /// the entry's path.</summary>
        public string FolderOf(ProfileEntry e) => RunFolder(e) ?? e.Path;

        /// <summary>Profile entries of an engine that its report did not name: disabled, missing, or not loaded.</summary>
        public void ProfileRest(string kind, Func<ProfileEntry, bool> reported, string notReportedReason, ModRunState notReportedState = ModRunState.NotLoaded,
            string? notReportedHelp = null)
        {
            foreach (var e in Profile.Of(kind))
            {
                if (reported(e)) continue;
                var id = e.Id ?? LeafName(e.Path);
                // the folder its engine runs (ShroudForge's and SET's own folder in the game, in every path mode), else the
                // entry's path: what the row shows and where the page reads and writes its settings
                var folder = FolderOf(e);
                var (name, version) = Manifest(folder);
                var missing = RunFolder(e) is null && (MissingPaths.Contains(e.Path) || !DirExists(e.Path));
                // switched off, but a copy is still in <game>/mods (kept because it was changed there, say), where a loader
                // that reads the folder may still run it
                var kept = e.Enabled ? null : KeptInGameMods(kind, folder, id);
                Add(new CatalogMod
                {
                    Engine = kind, Id = id, Name = name ?? id, Version = e.Version ?? version ?? "", Folder = folder, Source = "profile",
                    Missing = missing && e.Enabled,
                    State = !e.Enabled ? ModRunState.Disabled : missing ? ModRunState.Failed : notReportedState,
                    Word = !e.Enabled ? "disabled" : missing ? "missing" : "not reported",
                    Reason = kept is { } k
                        ? KilnText.T(kind switch { "xhl" => "catalog.profile.kept_xhl", "enshwick" => "catalog.profile.kept_enshwick", _ => "catalog.profile.kept" },
                            ("folder", LeafName(k)))
                        : !e.Enabled ? KilnText.T("catalog.profile.off") : missing ? KilnText.T("catalog.profile.missing", ("path", e.Path)) : notReportedReason,
                    Help = kept is not null ? KilnText.T("catalog.help.kept")
                        : !e.Enabled ? KilnText.T("catalog.help.turn_on")
                        : missing ? KilnText.T("catalog.help.repair") : notReportedHelp,
                });
            }
        }
    }

    /// <summary>"switched off in the profile" -> "Switched off in the profile"; a first word that is a file or a code
    /// ("c.dll:", "init.lua:1:") stays as it is.</summary>
    internal static string Sentence(string r)
    {
        if (r.Length == 0 || !char.IsLower(r[0])) return r;
        var end = r.IndexOf(' ');
        var word = end < 0 ? r : r[..end];
        foreach (var ch in word)
            if (!char.IsLetter(ch) && ch != '\'' && ch != ',') return r;
        return char.ToUpperInvariant(r[0]) + r[1..];
    }

    /// <summary>Reads every source now, on the calling thread (a worker, a test, a server command). Never throws: a source
    /// that cannot be read leaves its engine with an explaining note.</summary>
    public static CatalogSnapshot Read(CatalogSources src)
    {
        var generation = KilnText.Generation;  // a change while it reads: the next Snapshot reads again
        // (the frozen ShroudForge backend of this start, before any ShroudForge folder, status,
        // assets or actions path; in a kiln-backend start nothing below reads or writes <game>/shroudforge)
        src = SfiCatalog.Decide(src);
        var boot = ReadState(src, "boot.json");
        var bootDoc = boot.Current ? boot.Doc : null;
        Profile profile;
        try { profile = ReadProfile(src, bootDoc); }
        catch (Exception) { profile = new Profile(); }
        var b = new Builder(src, profile);
        if (bootDoc is { } bd && bd.TryGetProperty("profile", out var bp) && bp.TryGetProperty("missing", out var miss) &&
            miss.ValueKind == JsonValueKind.Array)
            foreach (var m in miss.EnumerateArray())
                if (Str(m, "path") is { } mp) b.MissingPaths.Add(ResolvePath(src, mp));

        void Guard(string kind, Action a)
        {
            try { a(); }
            catch (Exception e)
            {
                b.Engine(kind, "error", EngineHealth.Problem, KilnText.T("catalog.engine_unreadable", ("error", e.Message)), false);
            }
        }

        Guard("kiln", () => KilnMods(b));
        Guard("native", () => NativeMods(b));
        Guard("plugin", () => PluginMods(b, bootDoc));
        Guard("chain", () => ChainMods(b, bootDoc));
        foreach (var kind in new[] { "emf", "eml", "set", "greeneye", "flame", "shroudtopia", "shroudlink", "ember", "dllmod" })
            Guard(kind, () => EngineMods(b, kind, bootDoc));
        Guard("xhl", () => Xhl(b));
        Guard("shroudforge", () => ShroudForge(b, bootDoc));
        Guard("enshwick", () => Enshwick(b, bootDoc));

        var engines = new List<CatalogEngine>();
        var all = new List<CatalogMod>();
        foreach (var kind in EngineOrder)
        {
            b.Mods.TryGetValue(kind, out var mods);
            mods ??= [];
            b.Engines.TryGetValue(kind, out var e);
            var problems = mods.Count(m => m.NeedsAttention);
            if (mods.Count == 0 && e.Health != EngineHealth.Problem) continue;  // engines without mods are not listed
            var health = problems > 0 ? EngineHealth.Problem : e.Health;
            engines.Add(new CatalogEngine
            {
                Kind = kind, Name = EngineName(kind), Word = e.Word, Health = health, Note = e.Note, Count = mods.Count,
                Problems = problems, Reported = e.Reported,
            });
            all.AddRange(mods);
        }
        UniqueKeys(all);
        string? profileLine = profile.Mode switch
        {
            "no-mods" => KilnText.T("catalog.profile.no_mods"),
            "invalid" => profile.Error is { } pe ? KilnText.T("catalog.profile.invalid_why", ("error", pe)) : KilnText.T("catalog.profile.invalid"),
            "legacy" => KilnText.T("catalog.profile.legacy"),
            _ when profile.Name is not null || profile.Id is not null => KilnText.T("catalog.profile.name", ("name", profile.Name ?? profile.Id)),
            _ => null,
        };
        return new CatalogSnapshot
        {
            Engines = engines, Mods = all, Profile = profileLine, ReadAtMs = Environment.TickCount64,
            Side = src.Side.Length > 0 ? src.Side : bootDoc is { } bd2 ? Str(bd2, "side") ?? "" : "",
            Version = Interlocked.Increment(ref _version), Generation = generation,
        };
    }

    private static long _version;

    /// <summary>Gives each row its own <see cref="ModManagerLogic.KeyOf"/>: the mod manager's ImGui ids and its selection
    /// are made from it, and two rows with one ImGui id raise ImGui's "conflicting ID" popup and cannot be chosen apart.
    /// KeyOf includes folder and source, so distinguishable rows retain their keys when the list changes. Only repeated
    /// reports of the same identity need "#2", "#3"; reserve real keys first so a generated suffix cannot displace one.</summary>
    internal static void UniqueKeys(List<CatalogMod> mods)
    {
        var reserved = new HashSet<string>(mods.Select(ModManagerLogic.KeyOf), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);  // ImGui hashes ids as they are spelled
        for (var i = 0; i < mods.Count; i++)
        {
            var m = mods[i];
            if (seen.Add(ModManagerLogic.KeyOf(m))) continue;
            var key = m.Key ?? m.Id;
            for (var n = 2; ; n++)
            {
                var next = m with { Key = $"{key}#{n}" };
                var nextKey = ModManagerLogic.KeyOf(next);
                if (reserved.Contains(nextKey) || !seen.Add(nextKey)) continue;
                mods[i] = next;
                break;
            }
        }
    }

    /// <summary>A client's <c>side: "server"</c> Kiln mod before the player's own world starts (the launcher says the same:
    /// kiln_manage::profile::WITH_WORLD).</summary>
    internal static string StartsWithWorld => KilnText.T("catalog.kiln.with_world");

    /// <summary>The same with kiln.json <c>"mods": {"hosted": false}</c> (kiln_manage::profile::HOSTED_OFF).</summary>
    internal static string HostedOff => KilnText.T("catalog.kiln.hosted_off");

    // ---- Kiln's managed mods: Mods.All, plus profile entries the host never saw (missing folders) ----
    private static void KilnMods(Builder b)
    {
        var mods = b.Src.KilnMods();
        foreach (var m in mods.OrderBy(m => m.LoadIndex < 0 ? int.MaxValue : m.LoadIndex).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            var (state, help) = m.State switch
            {
                ModState.Loaded => (ModRunState.Loaded, (string?)null),
                ModState.Pending => (ModRunState.Waiting, KilnText.T("catalog.kiln.pending")),
                ModState.Disabled => (ModRunState.Disabled, KilnText.T("catalog.kiln.disabled")),
                ModState.Failed => (ModRunState.Failed, KilnText.T("catalog.kiln.failed")),
                ModState.Unloaded => (ModRunState.NotLoaded, KilnText.T("catalog.kiln.unloaded")),
                _ => (ModRunState.NotLoaded, (string?)null),
            };
            var reason = m.Error;
            // a client's server mod in the menu (or in someone else's world) waits for the player's own world: Kiln.Host
            // loads it when that world starts (ModManager.StartHosting). Not a failure, and not "server only".
            if (m.State == ModState.Skipped && m.Side == ModSide.Server && m.Error == HostedReasons.Waiting)
                (state, reason, help) = (ModRunState.Waiting, StartsWithWorld, null);
            else if (m.State == ModState.Skipped && m.Side == ModSide.Server && m.Error == HostedReasons.Off)
                (reason, help) = (HostedOff, KilnText.T("catalog.kiln.hosted_off_help"));
            var side = m.Side switch { ModSide.Client => "client", ModSide.Both => "both", _ => "server" };
            b.Add(new CatalogMod
            {
                Engine = "kiln", Id = m.Id, Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, Version = m.Version, State = state,
                Word = m.State.ToString().ToLowerInvariant(), Reason = reason, Help = help, Folder = m.Directory, Side = side,
                Source = "kiln", LoadIndex = m.LoadIndex, Dependencies = m.Dependencies,
            });
        }
        var known = new HashSet<string>(mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        b.ProfileRest("kiln", e => e.Id is not null && known.Contains(e.Id), KilnText.T("catalog.kiln.not_reported"),
            notReportedHelp: KilnText.T("catalog.kiln.not_reported_help"));
        b.Engine("kiln", null, EngineHealth.Ok, null, true);
    }

    // ---- native DLL mods: state/native.json ----
    private static void NativeMods(Builder b)
    {
        var f = ReadState(b.Src, "native.json");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (f.Doc is { } d && f.Current && d.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mods.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Object || Str(m, "id") is not { } id) continue;
                reported.Add(id);
                var word = Str(m, "state") ?? "unknown";
                var folder = Str(m, "folder");
                var reason = Str(m, "reason");
                if (reason is null && m.TryGetProperty("dlls", out var dlls) && dlls.ValueKind == JsonValueKind.Array)
                    foreach (var dll in dlls.EnumerateArray())
                        if (Str(dll, "error") is { } err) { reason = $"{Str(dll, "file") ?? "DLL"}: {err}"; break; }
                if (m.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.Array && reason is null)
                    reason = string.Join(" ", notes.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()));
                var state = StateOf(word);
                if (word == "policy") reason ??= KilnText.T("catalog.native.policy");
                var (name, _) = Manifest(folder);
                b.Add(new CatalogMod
                {
                    Engine = "native", Id = id, Name = name ?? id, Version = Str(m, "version") ?? "", State = state, Word = word,
                    Reason = string.IsNullOrEmpty(reason) ? null : reason, Folder = folder, Source = Str(m, "source") ?? "",
                    Side = Str(m, "side") ?? "",
                    LoadIndex = m.TryGetProperty("loadIndex", out var li) && li.ValueKind == JsonValueKind.Number ? li.GetInt32() : -1,
                    Help = state == ModRunState.Failed ? KilnText.T("catalog.native.failed_help") : null,
                });
            }
        }
        var why = !f.Exists ? KilnText.T("catalog.native.no_report") :
            !f.Current ? KilnText.T("catalog.native.old_report") : KilnText.T("catalog.native.not_reported");
        b.ProfileRest("native", e => e.Id is not null && reported.Contains(e.Id), why);
        b.Engine("native", f.Doc is { } nd ? Str(nd, "phase") : null, EngineHealth.Ok, f.Error is { } er ? KilnText.T("catalog.report_unreadable", ("error", er)) : null, f.Current);
    }

    // ---- Kiln native plugins: boot.json plugins[] that are not Kiln's own engine plugins, and the profile's plugin entries ----
    private static void PluginMods(Builder b, JsonElement? boot)
    {
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (boot is { } d && d.TryGetProperty("plugins", out var plugins) && plugins.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in plugins.EnumerateArray())
            {
                var id = Str(p, "id") ?? LeafName(Str(p, "file") ?? Str(p, "path"));
                if (id.Length == 0) continue;
                var word = Str(p, "status") ?? "unknown";
                var path = Str(p, "path");
                if (path is not null) reported.Add(path);
                if (Str(p, "file") is { } file) reported.Add(file);
                if (EnginePlugins.TryGetValue(id, out var engine))
                {
                    // Kiln's own engine plugin: its load result belongs to its engine
                    if (StateOf(word) == ModRunState.Failed)
                        b.Engine(engine, word, EngineHealth.Problem,
                            KilnText.T("catalog.plugin.engine_failed", ("engine", EngineName(engine)), ("error", Str(p, "error") ?? word)), true);
                    continue;
                }
                if (id.StartsWith("kiln", StringComparison.OrdinalIgnoreCase) && path is not null &&
                    !b.Profile.Of("plugin").Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;  // Kiln's own service plugins
                var state = StateOf(word);
                b.Add(new CatalogMod
                {
                    // a plugin that failed before it named itself is named by its file, which another folder may also hold
                    Engine = "plugin", Id = id, Key = path, Name = id, Version = Str(p, "version") ?? "", State = state, Word = word,
                    Reason = Str(p, "error"), Folder = path, Source = "plugins",
                    Help = state == ModRunState.Failed ? KilnText.T("catalog.plugin.failed_help") : null,
                });
            }
        }
        b.ProfileRest("plugin", e => reported.Contains(e.Path) || reported.Contains(LeafName(e.Path)),
            boot is null ? KilnText.T("catalog.plugin.no_report") : KilnText.T("catalog.plugin.not_reported"));
        b.Engine("plugin", null, EngineHealth.Ok, null, boot is not null);
    }

    // ---- chain DLLs: boot.json chain[], native.json chain.late[] ----
    private static void ChainMods(Builder b, JsonElement? boot)
    {
        var late = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var native = ReadState(b.Src, "native.json");
        if (native.Current && native.Doc is { } nd && nd.TryGetProperty("chain", out var ch) && ch.ValueKind == JsonValueKind.Object &&
            ch.TryGetProperty("late", out var l) && l.ValueKind == JsonValueKind.Array)
            foreach (var e in l.EnumerateArray())
                if (Str(e, "path") is { } p) late[p] = e;
        if (boot is not { } d || !d.TryGetProperty("chain", out var chain) || chain.ValueKind != JsonValueKind.Array) return;
        foreach (var c in chain.EnumerateArray())
        {
            if (Str(c, "path") is not { } path) continue;
            var word = Str(c, "status") ?? "unknown";
            var reason = Str(c, "error") ?? Str(c, "detail");
            if (late.TryGetValue(path, out var lt))
            {
                word = Str(lt, "status") ?? word;
                reason = Str(lt, "error") ?? Str(lt, "note") ?? reason;
            }
            var state = StateOf(word);
            var name = LeafName(path);
            b.Add(new CatalogMod
            {
                // keyed by the path: every proxy DLL mod (Spawn the World, Auto Loot Plus) is a dinput8.dll
                Engine = "chain", Id = name, Key = path, Name = name, State = state, Word = word, Reason = reason, Folder = path,
                Source = Str(c, "source") ?? "chain", Missing = word == "missing",
                Help = state == ModRunState.Failed ? KilnText.T("catalog.chain.failed_help") : null,
            });
        }
        b.Engine("chain", null, EngineHealth.Ok, null, true);
    }

    // ---- engines with a per-mod list in their state file ----
    private static void EngineMods(Builder b, string kind, JsonElement? boot)
    {
        var f = ReadState(b.Src, kind + ".json");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // an engine mod whose manifest id is no Kiln id (EML: `Hoarders Helper`) is in the profile under its Kiln id: the
        // row matches it by its folder, else by the id's slug
        var reportedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reportedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? word = null;
        var health = EngineHealth.Ok;
        string? note = null;
        if (f.Doc is { } d && f.Current)
        {
            word = Headline(d);
            (health, note) = EngineNote(kind, d, word);
            var aside = Bool(d, "steppedAside") == true;
            foreach (var line in ModLines(d))
            {
                reported.Add(line.Id);
                reportedSlugs.Add(IdFromName(line.Id));
                if (line.Folder is { } lf)
                {
                    reportedFolders.Add(NormPath(lf));
                    // An engine that reports a DLL (shroudlink: one line per mod DLL) covers the profile entry whose
                    // folder holds it. A bundle of several DLLs under one Library id (`assistants.dll` with five mods,
                    // as 0.2.0-dev.22 imported shroudlink's zip) would otherwise show as one more row, "not reported".
                    if (lf.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && ParentOf(lf) is { } parent) reportedFolders.Add(NormPath(parent));
                }
                var state = StateOf(line.State);
                var reason = line.Reason;
                string? help = null;
                if (kind == "ember")
                {
                    state = line.State == "skipped" ? ModRunState.NotLoaded : ModRunState.Waiting;
                    reason ??= KilnText.T("catalog.ember.reason");
                    help = KilnText.T("catalog.ember.help");
                }
                // kiln-dllmod starts its packages when a world of your own starts: until then they wait for it
                else if (kind == "dllmod" && state == ModRunState.Waiting && string.IsNullOrEmpty(reason))
                    reason = KilnText.T("catalog.dllmod.waiting");
                // own-loader: the engine's own loader runs it (Kiln copied it into that loader's folder)
                else if (aside && state != ModRunState.Failed && line.State != "own-loader")
                {
                    state = ModRunState.NotLoaded;
                    reason ??= Str(d, "steppedAsideReason");
                }
                if (line.State == "not-loaded")
                    help = KilnText.T("catalog.engine_mods.own_loader", ("engine", EngineName(kind)));
                else if (line.State == "off-in-shroudforge")
                    help = OffInShroudForgeHelp;
                else if (kind == "shroudtopia" && !aside && line.State == "inactive")
                {
                    reason = KilnText.T("catalog.shroudtopia.off");
                    help = KilnText.T("catalog.shroudtopia.off_help", ("name", line.Name ?? line.Id));
                }
                // kiln-shroudtopia: the setting is on and Activate is called, but the mod's own flag stays off
                else if (kind == "shroudtopia" && !aside && line.State == "activating")
                {
                    reason = string.IsNullOrEmpty(reason) ? KilnText.T("catalog.shroudtopia.not_active") : char.ToUpperInvariant(reason[0]) + reason[1..] + ".";
                    help = KilnText.T("catalog.shroudtopia.not_active_help", ("name", line.Name ?? line.Id));
                }
                else if (state == ModRunState.Failed)
                    help = KilnText.T("catalog.engine_mods.failed_help", ("engine", EngineName(kind)));
                var (mname, mversion) = line.Name is null ? Manifest(line.Folder) : (line.Name, null);
                b.Add(new CatalogMod
                {
                    Engine = kind, Id = line.Id, Name = mname ?? line.Id, Version = line.Version ?? mversion ?? "", State = state,
                    Word = line.State ?? "", Reason = reason, Help = help, Folder = line.Folder, Side = line.Side ?? "",
                    Source = line.Source ?? "",
                });
            }
        }
        // folder: a mod of the engine's folder that the profile does not list ("the mod" in place of "this mod")
        string Why(bool folder) => f.Error is { } err ? KilnText.T("catalog.engine_mods.unreadable", ("engine", EngineName(kind)), ("error", err))
            : !f.Exists ? KilnText.T(folder ? "catalog.engine_mods.not_run_folder" : "catalog.engine_mods.not_run", ("engine", EngineName(kind)))
            : !f.Current ? KilnText.T("catalog.engine_mods.old_report", ("engine", EngineName(kind)))
            : KilnText.T("catalog.engine_mods.not_reported", ("engine", EngineName(kind)));
        var why = Why(false);
        b.ProfileRest(kind, e => (e.Id is not null && (reported.Contains(e.Id) || reportedSlugs.Contains(e.Id))) || reportedFolders.Contains(NormPath(e.Path)) ||
                                 reportedFolders.Contains(NormPath(b.FolderOf(e))), why,
            notReportedHelp: !f.Exists || !f.Current ? KilnText.T("catalog.engine_mods.plugin_help", ("engine", EngineName(kind))) : null);
        if (kind == "set" && !f.Current && b.Profile.ReadsFolder("set"))
        {
            // an older kiln-set writes no set.json: the folder SET reads, without a result
            var listed = new HashSet<string>(b.Profile.Of("set").Select(e => e.Id ?? LeafName(e.Path)), StringComparer.OrdinalIgnoreCase);
            foreach (var sub in SubDirs(Path.Combine(b.Src.GameDir, "shrouded_engine_tweaks", "mods")))
            {
                var name = LeafName(sub);
                bool lua;
                try { lua = File.Exists(Path.Combine(sub, "init.lua")); }
                catch (Exception) { lua = false; }
                if (!lua || listed.Contains(name) || reported.Contains(name)) continue;
                var (mname, mversion) = Manifest(sub);
                b.Add(new CatalogMod
                {
                    Engine = "set", Id = name, Name = mname ?? name, Version = mversion ?? "", Folder = sub, Source = "folder",
                    State = ModRunState.NotLoaded, Word = "not reported", Reason = Why(true),
                });
            }
        }
        if (f.Error is not null) (health, note) = (EngineHealth.Problem, why);
        else if (!f.Current && (b.Profile.Of(kind).Count > 0 || b.Mods.ContainsKey(kind)))
            (health, note) = (EngineHealth.Idle, f.Exists
                ? KilnText.T("catalog.engine_mods.note_old", ("engine", EngineName(kind)))
                : KilnText.T("catalog.engine_mods.note_not_run", ("engine", EngineName(kind))));
        if (!b.Engines.ContainsKey(kind)) b.Engine(kind, word, health, note, f.Current);
    }

    /// <summary>The engine's overall state as a short sentence, when it matters.</summary>
    internal static (EngineHealth, string?) EngineNote(string kind, JsonElement d, string? word)
    {
        var tone = ToneOf(word);
        // no word at all (EMF, Greeneye, Flame write none while they run): the engine ran this start, which is fine
        var health = word is null ? EngineHealth.Ok : tone == "err" ? EngineHealth.Problem : tone == "idle" ? EngineHealth.Idle : EngineHealth.Ok;
        if (Bool(d, "steppedAside") == true)
        {
            var why = Str(d, "steppedAsideReason");
            return (EngineHealth.Idle, kind == "emf"
                ? why is null ? KilnText.T("catalog.note.aside_emf") : KilnText.T("catalog.note.aside_emf_why", ("reason", why))
                : why is null ? KilnText.T("catalog.note.aside", ("engine", EngineName(kind)))
                : KilnText.T("catalog.note.aside_why", ("engine", EngineName(kind)), ("reason", why)));
        }
        switch (kind)
        {
            case "emf":
                if (Str(d, "emfIdle") is { } idle) return (EngineHealth.Problem, KilnText.T("catalog.note.emf_idle", ("reason", idle)));
                return (health, null);
            case "eml":
                return word switch
                {
                    "done" => (EngineHealth.Ok, d.TryGetProperty("patched", out var p) && p.ValueKind == JsonValueKind.Number
                        ? p.TryGetInt64(out var changes) ? KilnText.Plural("catalog.note.eml_patched", changes)
                            : KilnText.T("catalog.note.eml_patched.other", ("count", p.GetRawText())) : null),
                    "failed" => (EngineHealth.Problem, KilnText.T("catalog.note.eml_failed", ("error", Str(d, "error") ?? KilnText.T("catalog.see_log")))),
                    "aside" => (EngineHealth.Idle, Str(d, "aside") is { } a && a.Contains("ShroudForge", StringComparison.OrdinalIgnoreCase)
                        ? KilnText.T("catalog.note.eml_aside_sf")
                        : Str(d, "aside") is { } why ? KilnText.T("catalog.note.eml_aside_why", ("reason", why)) : KilnText.T("catalog.note.eml_aside")),
                    "idle" => (EngineHealth.Idle, KilnText.T("catalog.note.eml_idle")),
                    _ => (health, null),
                };
            case "ember":
                return (EngineHealth.Idle, KilnText.T("catalog.note.ember"));
            // kiln-dllmod (packages for Enshrouded ModLoader): one client build only, never on a dedicated server
            case "dllmod":
                return word switch
                {
                    "unsupported" => (EngineHealth.Problem, KilnText.T("catalog.note.dllmod_unsupported")),
                    "client only" => (EngineHealth.Idle, KilnText.T("catalog.note.dllmod_server")),
                    _ => (health, null),
                };
            case "shroudlink":
                if (Bool(d, "claimedElsewhere") == true) return (EngineHealth.Idle, KilnText.T("catalog.note.shroudlink_elsewhere"));
                return (health, null);
            case "set":
                return word switch
                {
                    "stepped-aside" => (EngineHealth.Idle, Str(d, "reason") is { } r ? KilnText.T("catalog.note.set_aside_why", ("reason", r)) : KilnText.T("catalog.note.set_aside")),
                    "waiting" => (EngineHealth.Ok, KilnText.T("catalog.note.set_waiting")),
                    "off" => (EngineHealth.Idle, Str(d, "reason") ?? KilnText.T("catalog.note.set_off")),
                    _ => (health, Str(d, "reason")),
                };
            default:
                return (health, tone == "err" ? Str(d, "reason") ?? Str(d, "error") : null);
        }
    }

    // ---- ShroudForge: the profile and its mods folder for the list, the state file for the runtime ----
    private static void ShroudForge(Builder b, JsonElement? boot)
    {
        // a start Kiln's interpreter has, or one whose state is not known: SfiCatalog's rows
        if (b.Src.Sfi is { Kind: not SfiBranchKind.Legacy } sfi)
        {
            if (!b.Mods.TryGetValue("eml", out var eml)) eml = [];
            SfiCatalog.Build(sfi, b.Src, b.Profile.Of("shroudforge"), b.FolderOf,
                e => b.RunFolder(e) is null && (b.MissingPaths.Contains(e.Path) || !DirExists(e.Path)), eml, b.Add,
                (word, health, note, reported) => b.Engine("shroudforge", word, health, note, reported));
            return;
        }
        var f = ReadState(b.Src, "shroudforge.json");
        string? mode = null, reason = null, modsDir = null, menu = null;
        if (f.Doc is { } d && f.Current)
        {
            mode = Str(d, "mode");
            reason = Str(d, "reason");
            modsDir = Str(d, "modsDir");
            menu = Str(d, "menu");
        }
        var (health, note, state) = mode switch
        {
            "hosted" => (EngineHealth.Ok, menu == "kiln" ? KilnText.T("catalog.sf.hosted_menu") : KilnText.T("catalog.sf.hosted"), ModRunState.Loaded),
            "self-hosted" => (EngineHealth.Idle, KilnText.T("catalog.sf.self_hosted"), ModRunState.Loaded),
            "not-installed" => (EngineHealth.Problem, KilnText.T("catalog.sf.not_installed"), ModRunState.Failed),
            "off" => (EngineHealth.Idle, reason is null ? KilnText.T("catalog.sf.off") : KilnText.T("catalog.sf.off_why", ("reason", reason)), ModRunState.NotLoaded),
            "failed" => (EngineHealth.Problem, KilnText.T("catalog.sf.failed", ("reason", reason ?? KilnText.T("catalog.see_log"))), ModRunState.Failed),
            null => (f.Exists ? EngineHealth.Idle : EngineHealth.Idle, f.Error is { } e ? KilnText.T("catalog.report_unreadable", ("error", e))
                : f.Exists ? KilnText.T("catalog.sf.no_report") : KilnText.T("catalog.sf.not_run"), ModRunState.NotLoaded),
            _ => (EngineHealth.Ok, null, StateOf(mode)),
        };
        var why = mode switch
        {
            "hosted" or "self-hosted" => KilnText.T("catalog.sf.runs_it"),
            null => note,
            _ => note,
        };
        var help = state == ModRunState.Failed ? KilnText.T("catalog.sf.fix_first") : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b.Profile.Of("shroudforge"))
        {
            var id = e.Id ?? LeafName(e.Path);
            seen.Add(LeafName(e.Path));
            seen.Add(id);
        }
        var start = b.Mods.TryGetValue("shroudforge", out var before) ? before.Count : 0;
        // while ShroudForge runs, a mod is only as far as its own report says: unconfirmed until SfRefine reads it, so a
        // failure before that (the engine line names it) never leaves a row looking Loaded
        var runs = mode is "hosted" or "self-hosted";
        if (runs)
        {
            state = ModRunState.Waiting;
            why = KilnText.T("catalog.sf.unconfirmed");
        }
        b.ProfileRest("shroudforge", _ => false, why ?? "", state, help);
        // mods the player put into ShroudForge's folder by hand (ShroudForge reads the whole folder)
        var dir = modsDir ?? SfFolder(b.Src.GameDir, "mods");
        // Kiln's copies of the profile's EML mods are rows of the EML group, which say whether ShroudForge's switch runs
        // them (the profile is their switch: the launcher sets it)
        var emlRows = b.Mods.TryGetValue("eml", out var emlList) ? emlList : [];
        var emlFolders = new HashSet<string>(emlRows.Where(m => m.Folder is not null).Select(m => NormPath(m.Folder!)), StringComparer.OrdinalIgnoreCase);
        foreach (var sub in SubDirs(dir))
        {
            var name = LeafName(sub);
            if (seen.Contains(name)) continue;
            if (emlFolders.Contains(NormPath(sub)) && string.Equals(PlacedKind(sub), "eml", StringComparison.OrdinalIgnoreCase)) continue;
            // a legacy EML package (no extended.mod.json): ShroudForge's own Modloader lists it, off until its switch is
            // first turned on (it writes the file then), and so does this page while ShroudForge runs
            var facts = SfFacts(sub) ?? (runs ? LegacyEmlFacts(sub) : null);
            if (facts is null) continue;
            var (sid, sname, sversion, on) = facts.Value;
            b.Add(new CatalogMod
            {
                Engine = "shroudforge", Id = sid ?? name, Name = sname ?? name, Version = sversion ?? "",
                Folder = sub, Source = "folder", State = on ? state : ModRunState.Disabled, Word = on ? runs ? "unconfirmed" : mode ?? "" : "disabled",
                // an EML package without ShroudForge's file was never switched on there: say so, it is not a choice the
                // player made; the player's own folder says the promised words, a copy Kiln's launcher placed does not
                Reason = on ? why : SfFacts(sub) is not null ? KilnText.T("catalog.sf.off_in_settings") : KilnsEml(sub) ? NoSwitchFile : HandEmlOff,
                Help = on ? help : null,
            });
        }
        // kiln-eml reported its handoff before ShroudForge prepared. A later failure supersedes that prediction.
        if (mode == "failed")
        {
            var affected = false;
            for (var i = 0; i < emlRows.Count; i++)
                if (emlRows[i].Word == "own-loader" && emlRows[i].Reason?.Contains("ShroudForge", StringComparison.OrdinalIgnoreCase) == true)
                {
                    emlRows[i] = emlRows[i] with { State = ModRunState.Failed, Word = "failed", Reason = note,
                        Help = KilnText.T("catalog.sf.eml_failed_help") };
                    affected = true;
                }
            if (affected && b.Engines.TryGetValue("eml", out var eml))
                b.Engines["eml"] = (eml.Word, EngineHealth.Problem, KilnText.T("catalog.sf.eml_aside_but", ("note", note)), eml.Reported);
        }
        // what ShroudForge's runtime says per mod (its state.json, this process), and its own switch in the mod's files
        if (runs)
        {
            // kiln-shroudforge's status of this process: ShroudForge's startup preparation succeeded in this start
            var prepared = f.Doc is { } status && f.Current && Bool(status, "startupPrepared") == true;
            var assets = SfAssets(b.Src, prepared);
            if (b.Mods.TryGetValue("shroudforge", out var list))
            {
                var runtime = SfRuntime(b.Src);
                for (var i = start; i < list.Count; i++) list[i] = SfRefine(list[i], runtime, assets, b.Src.Side);
            }
            // the EML mods ShroudForge runs from its folder: only while its switch is on (kiln-eml decided before
            // ShroudForge prepared, and an older kiln-eml said "runs" for every one of them)
            var off = 0;
            for (var i = 0; i < emlRows.Count; i++)
            {
                emlRows[i] = EmlUnderSf(emlRows[i], dir, assets, b.Src.Side);
                if (emlRows[i].Word == OffInShroudForge) off++;
            }
            if (off > 0 && b.Engines.TryGetValue("eml", out var en))
                b.Engines["eml"] = (en.Word, EngineHealth.Problem, KilnText.Plural("catalog.sf.eml_off", off), en.Reported);
        }
        b.Engine("shroudforge", mode, health, note, f.Current);
    }

    /// <summary>The word of an EML row whose package ShroudForge has switched off (kiln-eml's state too).</summary>
    internal const string OffInShroudForge = "off-in-shroudforge";

    /// <summary>An EML mod the player put into ShroudForge's mods folder by hand, which ShroudForge keeps switched off (a
    /// legacy EML package has no extended.mod.json until its switch is first turned on). The words promised to a player on
    /// Nexus (2026-11-14): what happened, nothing about ShroudForge's files (that read as "Kiln wants a ShroudForge file
    /// from an EML mod"). kiln-manage (the launcher) and kiln-eml (kiln.log) say the same.</summary>
    internal static string HandEmlOff => KilnText.T("catalog.sf.hand_off");

    /// <summary>What helps <see cref="HandEmlOff"/> on the EML row of a profile mod whose folder is the player's.</summary>
    internal static string HandEmlOffProfileHelp => KilnText.T("catalog.sf.hand_off_profile_help");

    internal static string OffInShroudForgeHelp => KilnText.T("catalog.sf.off_in_sf_help");

    /// <summary>A package in ShroudForge's folder that has no extended.mod.json and is not the player's own folder (a
    /// copy Kiln's launcher placed there, <see cref="KilnsEml"/>): <see cref="HandEmlOff"/> would tell the player they
    /// put it there.</summary>
    internal static string NoSwitchFile => KilnText.T("catalog.sf.no_switch_file");

    /// <summary>The folder is a copy of an EML mod Kiln's launcher placed (its marker says eml), not the player's own.</summary>
    private static bool KilnsEml(string folder) => string.Equals(PlacedKind(folder), "eml", StringComparison.OrdinalIgnoreCase);

    /// <summary>An EML row while ShroudForge runs the EML mods: a package in ShroudForge's mods folder runs only while its
    /// extended.mod.json says enabled: true, on the sides its targets name (ShroudForgePackages.Read reads both as
    /// ShroudForge does), and a package with a startup part is applied when ShroudForge's record of this start's pass lists
    /// it. Rows of other folders, and the profile's switched-off or not-loaded ones, stay as kiln-eml said.</summary>
    private static CatalogMod EmlUnderSf(CatalogMod m, string sfMods, SfAssetState? assets, string side)
    {
        if (m.Folder is not { } folder || m.Word is not ("own-loader" or OffInShroudForge or "other-side") ||
            ParentOf(folder) is not { } parent || !SamePath(parent, sfMods)) return m;
        SfPackage p;
        try { p = ShroudForgePackages.Read(folder); }
        catch (Exception e)
        {
            return m with
            {
                State = ModRunState.Waiting, Word = "unconfirmed",
                Reason = KilnText.T("catalog.sf.package_unreadable", ("error", ShroudForgeUi.Clip(e.Message, 300))),
                Help = null,
            };
        }
        var kilns = string.Equals(PlacedKind(folder), "eml", StringComparison.OrdinalIgnoreCase);
        if (!p.Enabled)
            return m with
            {
                State = ModRunState.Disabled, Word = OffInShroudForge,
                // Kiln's copy: the launcher switches it on; the player's own folder: the promised words
                Reason = !kilns ? HandEmlOff : p.Extended ? KilnText.T("catalog.sf.eml_switch_off") : KilnText.T("catalog.sf.eml_no_switch"),
                Help = kilns ? KilnText.T("catalog.sf.eml_press_play") : HandEmlOffProfileHelp,
            };
        if (side.Length > 0 && p.Targets.Count > 0 && !(side == "server" ? p.Targets.Contains("server") : p.Targets.Contains("client") || !p.Targets.Contains("server")))
            return m with { State = ModRunState.NotLoaded, Word = "other side", Reason = KilnText.T("catalog.sf.eml_other_side", ("sides", string.Join(KilnText.T("catalog.sf.and"), p.Targets))), Help = null };
        if (!p.Capabilities.Any(c => c is "patch" or "export")) return m;
        if (assets is not null && SfStartup(m, p, assets, side) is { } startup) return startup;
        return m with
        {
            State = ModRunState.Waiting, Word = "unconfirmed",
            Reason = KilnText.T("catalog.sf.eml_unconfirmed"),
            Help = null,
        };
    }

    /// <summary>A legacy EML package in ShroudForge's folder (mod.json and src/mod.lua, no extended.mod.json, no other
    /// engine's manifest, not a copy Kiln placed for another kind): its mod.json's id, name and version, switched off.</summary>
    private static (string? Id, string? Name, string? Version, bool On)? LegacyEmlFacts(string folder)
    {
        try
        {
            if (!File.Exists(Path.Combine(folder, "src", "mod.lua")) || File.Exists(Path.Combine(folder, "flame.json")) ||
                PlacedKind(folder) is { } kind && !kind.Equals("eml", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch (Exception) { return null; }
        if (ReadJson(Path.Combine(folder, "mod.json"), out _) is not { ValueKind: JsonValueKind.Object } mod || Str(mod, "id") is null) return null;
        if (new[] { "dll", "main", "emf", "emfMin", "emf_api" }.Any(k => mod.TryGetProperty(k, out _))) return null;  // an EMF mod
        return (Str(mod, "id"), Str(mod, "name"), Str(mod, "version"), false);
    }

    /// <summary><see cref="ShroudForgePackages.Folder"/> for mods or state. Folder catches what it knows of (the config
    /// unreadable, cut off, a repeated key); this catch is kept for anything it may let through later, so the list is still
    /// read from ShroudForge's default folder.</summary>
    private static string SfFolder(string gameDir, string key)
    {
        try { return ShroudForgePackages.Folder(gameDir, key); }
        catch (Exception) { return key == "mods" ? Path.Combine(gameDir, "mods") : Path.Combine(gameDir, "shroudforge"); }
    }

    /// <summary>mod.json's id, name and version (else extended.mod.json's), and ShroudForge's switch (extended.mod.json
    /// <c>enabled</c>, off when missing); null when the folder has no extended.mod.json.</summary>
    internal static (string? Id, string? Name, string? Version, bool On)? SfFacts(string folder)
    {
        JsonElement? ext, mod;
        try
        {
            var extPath = Path.Combine(folder, "extended.mod.json");
            ext = File.Exists(extPath) ? ReadJson(extPath, out _) : null;
            var modPath = Path.Combine(folder, "mod.json");
            mod = File.Exists(modPath) ? ReadJson(modPath, out _) : null;
        }
        catch (Exception) { return null; }
        if (ext is not { ValueKind: JsonValueKind.Object } e) return null;
        var m = mod is { ValueKind: JsonValueKind.Object } mm ? mm : default;
        return (Str(m, "id") ?? Str(e, "id"), Str(m, "name") ?? Str(e, "name"), Str(m, "version") ?? Str(e, "version"), Bool(e, "enabled") == true);
    }

    /// <summary>ShroudForge's runtime section of its state.json when this process wrote it: the mods it runs, the errors
    /// it holds per mod, and whether each mod has a runtime part.</summary>
    internal sealed record SfRuntimeState(HashSet<string> Active, Dictionary<string, string> Errors, Dictionary<string, (string State, string Detail)> Effects);

    private static SfRuntimeState? SfRuntime(CatalogSources src)
    {
        var path = Path.Combine(SfFolder(src.GameDir, "state"), "state.json");
        if (ReadJson(path, out _) is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("runtime", out var r) ||
            r.ValueKind != JsonValueKind.Object) return null;
        if (!r.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !pid.TryGetInt32(out var p) || p != src.Pid ||
            Bool(r, "running") != true || !r.TryGetProperty("updatedAt", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var seconds) ||
            Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - seconds) > 30 ||
            (src.ProcessStartUtc != DateTime.MinValue && seconds < new DateTimeOffset(src.ProcessStartUtc).ToUnixTimeSeconds() - 2)) return null;
        var active = new HashSet<string>(StringComparer.Ordinal);
        if (r.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var x in a.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && x.GetString() is { } id) active.Add(id);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (r.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Object)
            foreach (var x in e.EnumerateObject())
                if (x.Value.ValueKind == JsonValueKind.String && x.Value.GetString() is { Length: > 0 } t) errors[x.Name] = t;
        var effects = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (r.TryGetProperty("effects", out var ef) && ef.ValueKind == JsonValueKind.Object)
            foreach (var x in ef.EnumerateObject())
                if (x.Value.ValueKind == JsonValueKind.Object && Str(x.Value, "state") is { } state)
                    effects[x.Name] = (state, Str(x.Value, "detail") ?? "");
        return new SfRuntimeState(active, errors, effects);
    }

    /// <summary>ShroudForge's record of its startup game-data pass (state.json <c>assets</c>, written by its loader:
    /// <c>status</c> preparing, applied or failed, <c>target</c> enshrouded or enshrouded_server, <c>completedAt</c>, and
    /// for an applied pass the mods it patched with their versions), when it describes this game start. The record
    /// carries no pid. It is this start's in two cases: the pass ended inside this process's lifetime (<c>Fresh</c>), or
    /// kiln-shroudforge's status of this process says ShroudForge's startup preparation succeeded
    /// (<c>prepared</c> in <c>SfAssets</c>): ShroudForge then either wrote the record just now or found the pass of an earlier
    /// start still current for this start's mods and left the record as it was, which is the usual start. A failed pass
    /// counts only when fresh. Anything else (an older record without that word, a time ahead of the clock, a process
    /// whose start is unknown and no word from the adapter) proves nothing about this start.</summary>
    internal sealed record SfAssetState(string Status, string Target, Dictionary<string, string> Mods, string? Error, bool Fresh);

    private static SfAssetState? SfAssets(CatalogSources src, bool prepared)
    {
        var path = Path.Combine(SfFolder(src.GameDir, "state"), "state.json");
        if (ReadJson(path, out _) is not { ValueKind: JsonValueKind.Object } d || !d.TryGetProperty("assets", out var a) ||
            a.ValueKind != JsonValueKind.Object || Str(a, "status") is not { } status || Str(a, "target") is not { } target) return null;
        if (!a.TryGetProperty("completedAt", out var at) || at.ValueKind != JsonValueKind.Number || !at.TryGetInt64(out var seconds) ||
            seconds > DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30) return null;
        var fresh = src.ProcessStartUtc != DateTime.MinValue && seconds >= new DateTimeOffset(src.ProcessStartUtc).ToUnixTimeSeconds() - 2;
        if (!fresh && !(prepared && status == "applied")) return null;
        var mods = new Dictionary<string, string>(StringComparer.Ordinal);
        if (a.TryGetProperty("mods", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var x in list.EnumerateArray())
                if (x.ValueKind == JsonValueKind.Object && Str(x, "id") is { Length: > 0 } id) mods[id] = Str(x, "version") ?? "";
        return new SfAssetState(status, target, mods, Str(a, "error"), fresh);
    }

    /// <summary>What this start's startup pass proves for a mod with no Lua part: applied when the pass patched this
    /// side's game files and lists the mod at the version in its folder; failed when the pass itself failed. Anything
    /// else (the other side's files, a pass still running, a mod the pass does not list, another version) is no proof
    /// either way and returns null.</summary>
    private static CatalogMod? SfStartup(CatalogMod m, SfPackage p, SfAssetState assets, string side)
    {
        if (side.Length == 0 || assets.Target != (side == "server" ? "enshrouded_server" : "enshrouded")) return null;
        if (assets.Status == "failed")
            return m with
            {
                State = ModRunState.Failed, Word = "failed",
                Reason = assets.Error is { Length: > 0 } e ? KilnText.T("catalog.sf.startup_failed_why", ("error", e)) : KilnText.T("catalog.sf.startup_failed"),
                Help = KilnText.T("catalog.sf.startup_failed_help"),
            };
        if (assets.Status != "applied" || !assets.Mods.TryGetValue(p.Id, out var version) || version != p.Version) return null;
        return m with
        {
            State = ModRunState.Loaded, Word = "applied",
            Reason = assets.Fresh ? KilnText.T("catalog.sf.startup_applied") : KilnText.T("catalog.sf.startup_current"),
            Help = null,
        };
    }

    private static CatalogMod SfRefine(CatalogMod m, SfRuntimeState? runtime, SfAssetState? assets, string side)
    {
        if (m.Missing || m.Folder is not { } folder) return m;
        SfPackage p;
        try { p = ShroudForgePackages.Read(folder); }
        catch (Exception) { return m with { State = m.State == ModRunState.Disabled ? m.State : ModRunState.Waiting, Word = "unconfirmed", Reason = KilnText.T("catalog.sf.settings_unreadable") }; }
        var repair = ShroudForgeUi.RepairNote(p);
        // a legacy EML package the player put into ShroudForge's folder: the promised words (the switch below writes the
        // file); a copy Kiln's launcher placed there is not theirs, and says what is missing
        if (!p.Enabled && !p.Extended && m.Source == "folder" && !KilnsEml(folder))
            return m with
            {
                State = ModRunState.Disabled, Word = "disabled", Reason = HandEmlOff,
                // what helps on its own row in Mods > ShroudForge (the switch is drawn below it)
                Help = p.SwitchLive ? KilnText.T("catalog.sf.hand_off_help_live") : KilnText.T("catalog.sf.hand_off_help_next"),
            };
        if (!p.Enabled && !p.Extended)
            return m with
            {
                // a legacy EML package: ShroudForge's own Modloader writes the file at the first change of its switch
                State = ModRunState.Disabled, Word = "disabled", Reason = NoSwitchFile,
                Help = p.SwitchLive ? KilnText.T("catalog.sf.no_switch_help_live") : KilnText.T("catalog.sf.no_switch_help_next"),
            };
        if (!p.Enabled)
            return m with
            {
                State = ModRunState.Disabled, Word = "disabled",
                Reason = repair is null ? KilnText.T("catalog.sf.off_ext") : KilnText.T("catalog.sf.off_ext_repair", ("repair", repair)),
                Help = KilnText.T("catalog.sf.switch_on_below"),
            };
        if (repair is not null)
            return m with
            {
                State = ModRunState.Failed, Word = "needs repair", Reason = repair,
                Help = KilnText.T("catalog.sf.repair_help"),
            };
        if (side.Length > 0 && p.Targets.Count > 0 && !p.Targets.Contains(side))
            return m with { State = ModRunState.NotLoaded, Word = "other side", Reason = KilnText.T("catalog.sf.other_side", ("sides", string.Join(KilnText.T("catalog.sf.and"), p.Targets))), Help = null };
        var hasRuntime = p.Capabilities.Any(c => c is "runtime" or "runtime-register-dll");
        if (!hasRuntime && assets is not null && SfStartup(m, p, assets, side) is { } startup) return startup;
        if (runtime is null) return m with { State = ModRunState.Waiting, Word = "unconfirmed", Reason = KilnText.T("catalog.sf.no_runtime"), Help = null };
        if (runtime.Errors.TryGetValue(p.Id, out var err))
        {
            var waits = err.StartsWith("restart-required", StringComparison.Ordinal) || err.StartsWith("dependency-blocked", StringComparison.Ordinal);
            return m with
            {
                State = waits ? ModRunState.NotLoaded : ModRunState.Failed, Word = waits ? "waiting" : "failed", Reason = KilnText.T("catalog.sf.error", ("error", err)),
                Help = waits ? KilnText.T("catalog.sf.restart") : KilnText.T("catalog.sf.error_help"),
            };
        }
        if (!hasRuntime) return m with { State = ModRunState.Waiting, Word = "startup", Reason = KilnText.T("catalog.sf.startup_unconfirmed"), Help = null };
        if (!runtime.Active.Contains(p.Id))
            return m with { State = ModRunState.NotLoaded, Word = "inactive", Reason = KilnText.T("catalog.sf.inactive"), Help = null };
        if (runtime.Effects.TryGetValue(p.Id, out var effect))
        {
            // the mod's own report that its last change did not apply: it still runs (ShroudForge lists it active; the mod
            // itself made the report), so it is loaded, with that report as a warning; a fault or unload takes it off the list
            if (effect.State == "write-failed")
                return m with
                {
                    State = ModRunState.Loaded, Word = effect.State, Warn = true, Active = true,
                    Reason = KilnText.T("catalog.sf.effect_last_failed", ("detail", effect.Detail)),
                    Help = KilnText.T("catalog.sf.effect_last_failed_help"),
                };
            var effectState = EffectState(effect.State);
            return m with
            {
                State = effectState, Word = effect.State, Reason = KilnText.T("catalog.sf.effect", ("effect", effect.State), ("detail", effect.Detail)), Active = true,
                Help = effectState == ModRunState.Failed ? KilnText.T("catalog.sf.effect_failed_help") : null,
            };
        }
        return m with { State = ModRunState.Loaded, Word = "active", Reason = KilnText.T("catalog.sf.active"), Help = null, Active = true };
    }

    /// <summary>A mod's reported effect state as the launcher shows it: a mod reports waiting, no-target, no-change,
    /// write-confirmed or write-failed; ShroudForge's runtime also sets loading, loaded, error and restart-required.
    /// write-failed and error are Failed (SfRefine shows a running mod's write-failed as loaded with a warning); write-confirmed, loaded and no-change are Loaded; every other state (waiting,
    /// no-target, loading, restart-required, a word Kiln does not know) is Waiting, never a healthy green.</summary>
    internal static ModRunState EffectState(string state) => state switch
    {
        "write-failed" or "error" => ModRunState.Failed,
        "write-confirmed" or "loaded" or "no-change" => ModRunState.Loaded,
        _ => ModRunState.Waiting,
    };

    // ---- Enshwick: the profile and <game>/mods for the list, enshwick.json for the loader and copy notes ----
    private static void Enshwick(Builder b, JsonElement? boot)
    {
        var f = ReadState(b.Src, "enshwick.json");
        string? word = null;
        var notes = new Dictionary<string, ModLine>(StringComparer.OrdinalIgnoreCase);
        var health = EngineHealth.Idle;
        string? note = f.Error is { } err ? KilnText.T("catalog.report_unreadable", ("error", err)) : f.Exists ? KilnText.T("catalog.enshwick.no_report") : KilnText.T("catalog.enshwick.not_run");
        var state = ModRunState.NotLoaded;
        if (f.Doc is { } d && f.Current)
        {
            word = Headline(d);
            foreach (var l in ModLines(d)) notes[l.Id] = l;
            var reason = Str(d, "reason");
            (health, note, state) = word switch
            {
                "started" => (EngineHealth.Ok, KilnText.T("catalog.enshwick.started"), ModRunState.Loaded),
                "starting" => (EngineHealth.Ok, KilnText.T("catalog.enshwick.starting"), ModRunState.Waiting),
                "failed" => (EngineHealth.Problem, KilnText.T("catalog.enshwick.failed", ("reason", reason ?? KilnText.T("catalog.see_log"))), ModRunState.Failed),
                "no-loader" => (EngineHealth.Problem, KilnText.T("catalog.enshwick.no_loader", ("reason", reason ?? KilnText.T("catalog.enshwick.install"))), ModRunState.Failed),
                "stepped-aside" => (EngineHealth.Idle, KilnText.T("catalog.enshwick.aside"), ModRunState.NotLoaded),
                "nothing" => (EngineHealth.Idle, null, ModRunState.NotLoaded),
                _ => (EngineHealth.Idle, reason, ModRunState.Waiting),
            };
        }
        var why = state == ModRunState.Loaded ? KilnText.T("catalog.enshwick.runs_it") : note ?? "";
        var help = state == ModRunState.Failed ? KilnText.T("catalog.enshwick.fix_first") : null;
        CatalogMod Refine(CatalogMod m)
        {
            if (!notes.TryGetValue(LeafName(m.Folder), out var n) && !notes.TryGetValue(m.Id, out n)) return m;
            if (n.State == "failed" || n.State == "conflict")
                return m with { State = ModRunState.Failed, Word = n.State, Reason = n.Reason is { } nr ? Sentence(nr) : KilnText.T("catalog.enshwick.copy_failed"), Help = KilnText.T("catalog.enshwick.copy_failed_help") };
            if (n.State == "remove") return m with { State = ModRunState.NotLoaded, Word = "remove", Reason = KilnText.T("catalog.enshwick.removed") };
            return n.Reason is { } r ? m with { Reason = Sentence($"{m.Reason} {r}".Trim()) } : m;
        }
        var start = b.Mods.TryGetValue("enshwick", out var before) ? before.Count : 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b.Profile.Of("enshwick")) seen.Add(e.Id ?? LeafName(e.Path));
        // the game's mods folder also holds what Kiln places there for other hosts (XHL companions, EML mods a DLL reads
        // there): those are listed under their own kind, never as Enshwick's
        var taken = TakenFolders(b, "enshwick");
        b.ProfileRest("enshwick", _ => false, why, state, help);
        if (b.Profile.ReadsFolder("enshwick"))
            foreach (var sub in SubDirs(Path.Combine(b.Src.GameDir, "mods")))
            {
                var name = LeafName(sub);
                if (seen.Contains(name) || taken.Contains(NormPath(sub)) || !LooksLikeEnshwick(sub)) continue;
                b.Add(new CatalogMod
                {
                    Engine = "enshwick", Id = name, Name = name, Folder = sub, Source = "folder", State = state, Word = word ?? "",
                    Reason = why, Help = help,
                });
            }
        if (b.Mods.TryGetValue("enshwick", out var list))
            for (var i = start; i < list.Count; i++) list[i] = Refine(list[i]);
        b.Engine("enshwick", word, health, note, f.Current);
    }

    private static bool LooksLikeEnshwick(string dir)
    {
        try
        {
            if (File.Exists(Path.Combine(dir, "extended.mod.json"))) return false;  // ShroudForge's
            // an XHL-Core plugin, an EML (or ShroudForge) package, a copy Kiln placed for another kind
            if (File.Exists(Path.Combine(dir, XhlPluginIni)) || File.Exists(Path.Combine(dir, "mod.json"))) return false;
            if (PlacedKind(dir) is { } kind && !kind.Equals("enshwick", StringComparison.OrdinalIgnoreCase)) return false;
            if (File.Exists(Path.Combine(dir, "pack.toml"))) return true;
            return Directory.EnumerateFiles(dir, "*.dll").Take(1).Any();
        }
        catch (Exception) { return false; }
    }

    /// <summary>The kind in Kiln's placement marker of a folder (kiln-manage <c>engine_mods::MARKER</c>), or null.</summary>
    private static string? PlacedKind(string dir) => PlacedMarker(dir)?.Kind;

    /// <summary>Kiln's placement marker of a folder (kiln-manage <c>engine_mods::Marker</c>: kind, id, version, hash), or
    /// null when the folder has none (not a copy Kiln's launcher placed).</summary>
    internal static (string? Kind, string? Id, string? Version, string? Hash)? PlacedMarker(string dir)
    {
        var path = Path.Combine(dir, ".kiln-library.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 << 10) return null;
        }
        catch (Exception) { return null; }
        return ReadJson(path, out _) is { ValueKind: JsonValueKind.Object } d ? (Str(d, "kind"), Str(d, "id"), Str(d, "version"), Str(d, "hash")) : null;
    }

    /// <summary>The folder an engine reads its mods from when it never reads the profile's path for them (kiln-manage
    /// <c>engine_mods::placed_folder</c>, keep in step): ShroudForge's mods folder (its <c>paths.mods</c>, else
    /// <c>&lt;game&gt;/mods</c>: <c>shroudforge::mods_dir</c>) and SET's <c>shrouded_engine_tweaks/mods</c>. Null for every
    /// other kind: its plugin loads the mod from the path the profile names.</summary>
    internal static string? PlacedRoot(CatalogSources src, string kind) => kind switch
    {
        // a kiln-backend start's mods root is <game>/mods; ShroudForge's config is never read
        "shroudforge" when SfiCatalog.ModsRoot(src) is { } kilnRoot => kilnRoot,
        "shroudforge" => PlainPath(SfFolder(src.GameDir, "mods")),
        "set" => Path.Combine(src.GameDir, "shrouded_engine_tweaks", "mods"),
        _ => null,
    };

    /// <summary>A Windows path without the verbatim prefix ShroudForge writes (<c>\\?\D:\x</c> is <c>D:\x</c>,
    /// <c>\\?\UNC\server\share</c> is <c>\\server\share</c>): the same folder, as the profile and the page name it.</summary>
    internal static string PlainPath(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) || p.StartsWith("//?/UNC/", StringComparison.OrdinalIgnoreCase)) return @"\\" + p[8..];
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith("//?/", StringComparison.Ordinal)) return p[4..];
        return p;
    }

    /// <summary>The folder the engine of a profile entry runs it from, when that is not the entry's path; else null.
    ///
    /// ShroudForge and SET read their mods only from their own folder in the game (<see cref="PlacedRoot"/>), never from
    /// the path the profile names. Activation places Kiln's copy of each such mod there as <c>&lt;folder&gt;/&lt;id&gt;</c>,
    /// with a <c>.kiln-library.json</c> marker naming the mod. In the normal path modes the profile names that copy. In Copy
    /// mode (the launcher's data folder has no Windows path in the game's prefix: a Steam Deck with the game on an SD card)
    /// the profile names a second copy under <c>kiln/profiles/&lt;profile&gt;/&lt;kind&gt;/</c>, which the engine never
    /// reads: settings changed there never applied, and the next activation replaced them from the Library. So the row is
    /// Kiln's copy in the engine's folder whenever its marker names this entry's kind and id, as the launcher's own settings
    /// editor finds it (kiln-manage <c>engine_mods::sf_folder</c>: the receipt's kind and id; a copy of another version that
    /// activation kept because it changed is still the one the engine runs). The settings a player changes there are the
    /// ones the launcher's next profile update keeps (it compares <c>extended.mod.json</c> without them). A folder of that
    /// name without Kiln's marker is the player's own: it serves the entry in the normal path modes too, so it is the row
    /// as well when it holds the same package (the same <c>mod.json</c> id as the entry's copy).</summary>
    internal static string? EngineRunFolder(CatalogSources src, ProfileEntry e)
    {
        if (e.Id is not { Length: > 0 } id || PlacedRoot(src, e.Kind) is not { } root) return null;
        string placed;
        try
        {
            if (id.IndexOfAny(['/', '\\', ':']) >= 0 || id is "." or "..") return null;
            placed = Path.Combine(root, id);
        }
        catch (Exception) { return null; }
        if (SamePath(placed, e.Path) || !DirExists(placed)) return null;  // the profile names it already (the normal path modes)
        if (PlacedMarker(placed) is { } marker)
            return string.Equals(marker.Kind, e.Kind, StringComparison.OrdinalIgnoreCase) && string.Equals(marker.Id, id, StringComparison.Ordinal)
                ? placed : null;
        // the player's own folder of the same package (the launcher's sf_folder looks for the package id too)
        return ManifestId(placed) is { } own && own == ManifestId(e.Path) ? placed : null;
    }

    /// <summary>A mod folder's <c>mod.json</c> id, or null.</summary>
    private static string? ManifestId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "mod.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1 << 20) return null;
            return ReadJson(path, out _) is { ValueKind: JsonValueKind.Object } d ? Str(d, "id") : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The folders (as <see cref="NormPath"/>) the profile's entries of every kind but <paramref name="except"/>
    /// name, and those other engines' rows already list.</summary>
    private static HashSet<string> TakenFolders(Builder b, string except)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, entries) in b.Profile.Mods)
            if (!kind.Equals(except, StringComparison.OrdinalIgnoreCase))
                foreach (var e in entries)
                {
                    taken.Add(NormPath(e.Path));
                    taken.Add(NormPath(b.FolderOf(e)));  // the folder its engine runs (ShroudForge's copy in <game>/mods)
                }
        foreach (var (kind, rows) in b.Mods)
            if (!kind.Equals(except, StringComparison.OrdinalIgnoreCase))
                foreach (var m in rows)
                    if (m.Folder is { } f) taken.Add(NormPath(f));
        return taken;
    }

    // ---- XHL companions: placed in <game>/mods/<name>/ for XHL-Core, a separately installed host. Kiln never loads them
    // and nothing reports whether XHL-Core did, so no row says Loaded (kiln-manage engine_mods: xhl_host) ----
    private const string XhlPluginIni = "native-plugin.ini";

    internal static string XhlPrerequisite => KilnText.T("catalog.xhl.prerequisite");

    /// <summary>The XHL row's reason when XHL-Core started with the game: plain, no warning (inbox 39).</summary>
    internal static string XhlCoreStarted => KilnText.T("catalog.xhl.core_started");

    /// <summary>XHL-Core's DLL as Kiln chain-loads it (kiln-manage engine_mods XHL_CORE_DLL) and its Library id.</summary>
    private const string XhlCoreDll = "xhl-core.dll", XhlCoreId = "xhl-core";

    /// <summary>XHL-Core started this time: the native mod Kiln imported it as (its id, or its DLL renamed for the chain)
    /// loaded, or a chain DLL of that name did. A hand-installed XHL-Core under another name is not recognised here.</summary>
    private static bool XhlCoreLoaded(Builder b)
    {
        bool Is(string? s, string name) => s is not null && s.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (b.Mods.TryGetValue("native", out var native) &&
            native.Any(m => m.State == ModRunState.Loaded && (Is(m.Id, XhlCoreId) || Is(LeafName(m.Folder ?? ""), XhlCoreId))))
            return true;
        return b.Mods.TryGetValue("chain", out var chain) &&
               chain.Any(m => m.State == ModRunState.Loaded && (Is(m.Name, XhlCoreDll) || Is(LeafName(m.Folder ?? ""), XhlCoreDll)));
    }

    private static void Xhl(Builder b)
    {
        var start = b.Mods.TryGetValue("xhl", out var before) ? before.Count : 0;
        var core = XhlCoreLoaded(b);
        b.ProfileRest("xhl", _ => false, core ? XhlCoreStarted : KilnText.T("catalog.xhl.placed"),
            ModRunState.NotLoaded, core ? null : XhlPrerequisite);
        if (b.Mods.TryGetValue("xhl", out var placed))
            for (var i = start; i < placed.Count; i++)
                if (placed[i].Word == "not reported") placed[i] = placed[i] with { Word = "placed" };
        // XHL-Core plugins put into the game's mods folder by hand
        var taken = TakenFolders(b, "xhl");
        var listed = new HashSet<string>(b.Profile.Of("xhl").Select(e => NormPath(e.Path)), StringComparer.OrdinalIgnoreCase);
        var mods = Path.Combine(b.Src.GameDir, "mods");
        // ShroudForge reads this folder too when it is its mods folder: its packages are ShroudForge's rows
        var sfReads = PlacedRoot(b.Src, "shroudforge") is { } sfRoot && SamePath(sfRoot, mods);
        foreach (var sub in SubDirs(mods))
        {
            bool plugin;
            try { plugin = IsXhlPlugin(sub); }
            catch (Exception) { plugin = false; }
            if (!plugin || listed.Contains(NormPath(sub)) || taken.Contains(NormPath(sub))) continue;
            if (sfReads && IsSfPackage(sub)) continue;
            var name = LeafName(sub);
            b.Add(new CatalogMod
            {
                Engine = "xhl", Id = name, Name = name, Folder = sub, Source = "folder", State = ModRunState.NotLoaded, Word = "found",
                Reason = core ? KilnText.T("catalog.xhl.found_core") : KilnText.T("catalog.xhl.found"),
                Help = core ? null : XhlPrerequisite,
            });
        }
        if (b.Mods.ContainsKey("xhl") || b.Profile.Of("xhl").Count > 0)
            b.Engine("xhl", null, EngineHealth.Idle, core ? KilnText.T("catalog.xhl.note_core") : KilnText.T("catalog.xhl.note"), true);
    }

    /// <summary>Is <paramref name="dir"/> an XHL-Core plugin, and not only a folder with a <c>native-plugin.ini</c>? That file is
    /// ShroudForge's too: its DLL Manager loads the DLL <c>[Plugin] Dll=</c> names inside the package (<c>Dll=bin/x.dll</c>,
    /// Unified Tweaks). XHL-Core 2.1.0 takes <c>Dll</c> as a file name in the plugin's folder and refuses one with a path in
    /// it, and refuses a DLL without its plugin exports (<c>XhlNativePluginQuery</c>, <c>XhlNativePluginStart</c>,
    /// <c>XhlNativePluginRequestStop</c>). A folder is XHL-Core's when it has XHL's <c>xhl-plugin.json</c>, or when nothing
    /// says otherwise: a plain <c>Dll</c> file name, and the DLL, when it can be read, exports something of XHL's
    /// (<c>Xhl…</c>). The rule of kiln-manage <c>engine_mods::is_xhl_plugin</c>.</summary>
    internal static bool IsXhlPlugin(string dir)
    {
        try
        {
            var ini = Path.Combine(dir, XhlPluginIni);
            if (!File.Exists(ini)) return false;
            if (File.Exists(Path.Combine(dir, XhlPluginJson))) return true;
            if (new FileInfo(ini).Length > 64 << 10 || IniDll(File.ReadAllText(ini)) is not { } dll) return true;
            if (dll.Contains('/')) return false;
            var path = Path.Combine(dir, dll);
            if (!File.Exists(path) || PeExports(path) is not { } exports) return true;
            return exports.Any(e => e.StartsWith("Xhl", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return File.Exists(Path.Combine(dir, XhlPluginIni)); }
    }

    private const string XhlPluginJson = "xhl-plugin.json";

    /// <summary>The DLL the <c>[Plugin]</c> section of a <c>native-plugin.ini</c> names (<c>Dll=</c>, section and key in any
    /// case), '/'-separated; null when there is none or it leaves the folder (kiln-manage <c>engine_mods::xhl_ini_dll</c>).</summary>
    internal static string? IniDll(string text)
    {
        var plugin = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith('[') && line.EndsWith(']')) { plugin = line[1..^1].Trim().Equals("Plugin", StringComparison.OrdinalIgnoreCase); continue; }
            var eq = line.IndexOf('=');
            if (!plugin || line.StartsWith(';') || line.StartsWith('#') || eq <= 0) continue;
            if (!line[..eq].Trim().Equals("Dll", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line[(eq + 1)..].Trim().Trim('"').Replace('\\', '/');
            while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
            var leaves = value.Length == 0 || value.StartsWith('/') || value.Contains(':') || value.Split('/').Any(p => p == "..");
            return leaves ? null : value;
        }
        return null;
    }

    /// <summary>The export names of the PE file at <paramref name="path"/>; null when it is not one Kiln can read.</summary>
    internal static List<string>? PeExports(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 512L << 20) return null;
            var file = File.ReadAllBytes(path);
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(file));
            var h = pe.PEHeaders;
            if (h.PEHeader is not { } opt) return null;
            var dir = opt.ExportTableDirectory;
            if (dir.Size == 0 || dir.RelativeVirtualAddress == 0) return [];
            int Off(int rva)
            {
                foreach (var s in h.SectionHeaders)
                    if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                        return rva - s.VirtualAddress + s.PointerToRawData;
                return -1;
            }
            uint U32(int at) => at >= 0 && at + 4 <= file.Length ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)) : throw new InvalidDataException();
            var ed = Off(dir.RelativeVirtualAddress);
            var count = U32(ed + 24);
            var names = Off((int)U32(ed + 32));
            if (count > 65536 || names < 0) return null;
            var list = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var at = Off((int)U32(names + 4 * i));
                if (at < 0) return null;
                var end = Array.IndexOf(file, (byte)0, at, Math.Min(512, file.Length - at));
                if (end < 0) return null;
                list.Add(Encoding.ASCII.GetString(file, at, end - at));
            }
            return list;
        }
        catch (Exception) { return null; }
    }

    /// <summary>A ShroudForge package with a <c>native-plugin.ini</c> is not an XHL-Core plugin of its own: ShroudForge's own
    /// DLL loader reads that file and loads the DLL it names (its dll_manager), and the folder is ShroudForge's row. Told
    /// apart from an XHL-Core plugin (an EML mod with that file) by ShroudForge's own file: a package is ShroudForge's when
    /// Kiln's marker says so, or, with no marker of another kind, when its <c>extended.mod.json</c> is ShroudForge's own
    /// and not the switch of an EML package (<c>"launcher": "EML"</c>, which ShroudForge and Kiln write into an EML
    /// mod's folder so ShroudForge runs it; that mod stays an XHL-Core plugin too). The test kiln-shroudforge and the
    /// server's advertised list use (plugin.cpp legacy_wanted, AdvertiseEngines EngineKindOf).</summary>
    internal static bool IsSfPackage(string dir)
    {
        if (PlacedKind(dir) is { } kind) return kind.Equals("shroudforge", StringComparison.OrdinalIgnoreCase);
        JsonElement? ext;
        try
        {
            var path = Path.Combine(dir, "extended.mod.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4 << 20) return false;
            ext = ReadJson(path, out _);
        }
        catch (Exception) { return false; }
        return ext is { ValueKind: JsonValueKind.Object } e && Str(e, "launcher") != "EML";
    }

    private static IEnumerable<string> SubDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateDirectories(dir).Where(d => !LeafName(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
        }
        catch (Exception) { return []; }
    }

    // ------------------------------------------------------------------------------------- the render thread ----

    private static volatile CatalogSnapshot? _cache;
    private static volatile Task? _refresh;
    private static readonly object RefreshLock = new();
    private static Func<CatalogSources>? _sources;

    /// <summary>Where the default snapshot reads (tests and the preview set it).</summary>
    internal static Func<CatalogSources> Sources
    {
        get => _sources ?? DefaultSources;
        set
        {
            _sources = value;
            _cache = null;
        }
    }

    private static DateTime _processStart = DateTime.MinValue;

    internal static CatalogSources DefaultSources()
    {
        if (_processStart == DateTime.MinValue)
        {
            try { _processStart = Process.GetCurrentProcess().StartTime.ToUniversalTime(); }
            catch (Exception) { _processStart = DateTime.UtcNow; }
        }
        return new CatalogSources
        {
            KilnRoot = KilnPaths.Root,
            ProcessStartUtc = _processStart,
            Side = Mods.Side == ModSide.Client ? "client" : "server",
            KilnMods = () => Kiln.Mods.All.Select(m => new KilnModInput(m.Id, m.Name, m.Version, m.Directory, m.Side, m.State, m.Error,
                m.Dependencies, m.LoadIndex)).ToArray(),
        };
    }

    /// <summary>For the render thread (the mod manager screen, every frame while open): the last snapshot at once, never a
    /// file read. When it is older than <paramref name="maxAge"/> a refresh starts on a worker (one at a time); the screen
    /// shows the new one a moment later. Empty until the first refresh lands.</summary>
    public static CatalogSnapshot Snapshot(TimeSpan maxAge)
    {
        var c = _cache;
        // another language: read again (the sentences are made in the language of the read); the old list shows meanwhile
        if (c is not null && Environment.TickCount64 - c.ReadAtMs < maxAge.TotalMilliseconds && (c.Generation is not { } g || g == KilnText.Generation)) return c;
        var r = _refresh;
        if (r is null || r.IsCompleted)
        {
            lock (RefreshLock)
            {
                if (_refresh is null || _refresh.IsCompleted)
                {
                    var sources = Sources;
                    _refresh = Task.Run(() =>
                    {
                        try { _cache = Read(sources()); }
                        catch (Exception e) { Backend.Current.Log(0, LogLevel.Warn, "ui: the mod list could not be read: " + e.Message); }
                    });
                }
            }
        }
        return c ?? CatalogSnapshot.Empty;
    }

    /// <summary>A refresh is running or none has landed yet.</summary>
    public static bool Loading => _cache is null;

    /// <summary>Waits for the running refresh (tests, the preview); starts one only when no snapshot has landed (setting
    /// <see cref="Sources"/> drops it). A second read after one that already landed would be a read nobody asked for.</summary>
    internal static CatalogSnapshot WaitForSnapshot()
    {
        if (_cache is null) Snapshot(TimeSpan.Zero);
        _refresh?.Wait();
        return _cache ?? CatalogSnapshot.Empty;
    }

    /// <summary>Sets the snapshot directly (the preview's scenes, tests).</summary>
    internal static void SetForTests(CatalogSnapshot? s) => _cache = s;

    /// <summary>The catalog as text, one line per mod (a server console, a log, a test): <c>[EMF] Beastmaster 0.3.3: loaded</c>.</summary>
    public static string Summary(CatalogSnapshot s)
    {
        var sb = new StringBuilder();
        foreach (var e in s.Engines)
        {
            sb.Append(e.Name).Append(": ").Append(e.Count).Append(e.Count == 1 ? " mod" : " mods");
            if (e.Note is { } n) sb.Append(". ").Append(n);
            sb.Append('\n');
            foreach (var m in s.Mods.Where(m => m.Engine == e.Kind))
            {
                sb.Append("  ").Append(m.Name);
                if (m.Version.Length > 0) sb.Append(' ').Append(m.Version);
                sb.Append(": ").Append(m.Chip.ToLowerInvariant());
                if (m.Reason is { } r) sb.Append(" (").Append(r).Append(')');
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}
