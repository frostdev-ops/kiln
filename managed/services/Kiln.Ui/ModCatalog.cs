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
    /// <summary>The engine kind: kiln, native, plugin, chain, emf, eml, shroudforge, set, greeneye, flame, shroudtopia,
    /// enshwick, shroudlink, ember.</summary>
    public required string Engine { get; init; }
    public required string Id { get; init; }
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

    public bool NeedsAttention => State == ModRunState.Failed || Missing;

    /// <summary>The state chip's text.</summary>
    public string Chip => Missing ? "Missing" : ModCatalog.ChipText(State);

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
}

internal static class ModCatalog
{
    /// <summary>Engines in the order the screen lists them (the launcher's MOD_KINDS, with chain DLLs after plugins).</summary>
    public static readonly string[] EngineOrder =
        ["kiln", "native", "plugin", "chain", "emf", "eml", "shroudforge", "set", "greeneye", "flame", "shroudtopia", "enshwick", "shroudlink", "ember"];

    /// <summary>The names players see (the launcher's KIND_SHORT, plain words for Kiln's own kinds).</summary>
    public static string EngineName(string kind) => kind switch
    {
        "kiln" => "Kiln", "native" => "Native DLL mods", "plugin" => "Kiln plugins", "chain" => "Chain DLLs", "emf" => "EMF",
        "eml" => "EML", "shroudforge" => "ShroudForge", "set" => "SET", "greeneye" => "Greeneye", "flame" => "Flame",
        "shroudtopia" => "Shroudtopia", "enshwick" => "Enshwick", "shroudlink" => "shroudlink", "ember" => "Ember", _ => kind,
    };

    /// <summary>One line about what the engine is, for the details.</summary>
    public static string EngineAbout(string kind) => kind switch
    {
        "kiln" => "Kiln's own mods (C#).",
        "native" => "DLL mods Kiln loads at start.",
        "plugin" => "Native plugins that extend Kiln.",
        "chain" => "Other DLLs Kiln starts next to the game.",
        "emf" => "Enshrouded Mod Framework mods, run by Kiln.",
        "eml" => "Enshrouded Mod Loader mods: they change the game's data before it starts.",
        "shroudforge" => "ShroudForge mods, run by the ShroudForge runtime.",
        "set" => "Shrouded Engine Tweaks Lua mods, run by Kiln.",
        "greeneye" => "Greeneye Loader mods, run by Kiln.",
        "flame" => "Flame mods, run by Kiln.",
        "shroudtopia" => "Shroudtopia mods, run by Kiln.",
        "enshwick" => "Enshwick mods, run by Enshwick's loader.",
        "shroudlink" => "shroudlink mods, run by Kiln.",
        "ember" => "Ember server mods.",
        _ => "",
    };

    public static string ChipText(ModRunState s) => s switch
    {
        ModRunState.Loaded => "Loaded", ModRunState.Waiting => "Waiting", ModRunState.Disabled => "Off",
        ModRunState.Failed => "Failed", _ => "Not loaded",
    };

    // Kiln's engine plugins (boot.json plugins[]): their own load result is the engine's, not a mod.
    private static readonly Dictionary<string, string> EnginePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kiln-emf"] = "emf", ["kiln-eml"] = "eml", ["shroudforge"] = "shroudforge", ["kiln-shroudforge"] = "shroudforge",
        ["kiln-set"] = "set", ["kiln-greeneye"] = "greeneye", ["kiln-flame"] = "flame", ["kiln-shroudtopia"] = "shroudtopia",
        ["kiln-enshwick"] = "enshwick", ["kiln-shroudlink"] = "shroudlink", ["kiln-ember"] = "ember",
    };

    // ------------------------------------------------------------------------------------------- normalisation ----
    // Ports of EngineStatus.tsx (keep in step): headline(), toneOf(), modLines().

    private static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
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
            w is "missing" or "conflict" or "unsupported") return ModRunState.Failed;
        if (w is "disabled" or "off" or "inactive" or "policy") return ModRunState.Disabled;
        if (w.Contains("pending") || w.Contains("waiting") || w is "candidate" or "registered" or "starting" or "deferred")
            return ModRunState.Waiting;
        if (w is "loaded" or "active" or "running" or "started" or "ok" or "done" or "hosted" or "script" or "copy" or "refresh" or
            "keep" or "run" or "already-loaded") return ModRunState.Loaded;
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
                error = "the file is too large";
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
            error = "it is cut off or not valid JSON";
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = "the file could not be read: " + e.Message;
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
        if (d.ValueKind != JsonValueKind.Object) return new StateFile(null, true, false, "the file is not a JSON object");
        return new StateFile(d, true, IsCurrent(src, d, path), null);
    }

    internal static bool IsCurrent(CatalogSources src, JsonElement d, string path)
    {
        if (d.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number && pid.TryGetInt64(out var p))
            return p == src.Pid;
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
    internal sealed record ProfileEntry(string Kind, string? Id, string? Version, string Path, bool Enabled);

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
                list.Add(new ProfileEntry(kind.Name, Str(e, "id"), Str(e, "version"), ResolvePath(src, path), Bool(e, "enabled") != false));
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

    private static bool DirExists(string? p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        try { return Directory.Exists(p) || File.Exists(p); }
        catch (Exception) { return false; }
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
                m = m with { Help = "Turn it on in the Kiln launcher (Profiles), then start the game again." };
            l.Add(m with { SearchText = $"{m.Name} {m.Id} {EngineName(m.Engine)}".ToLowerInvariant() });
        }

        public void Engine(string kind, string? word, EngineHealth health, string? note, bool reported) =>
            Engines[kind] = (word, health, note, reported);

        /// <summary>Profile entries of an engine that its report did not name: disabled, missing, or not loaded.</summary>
        public void ProfileRest(string kind, Func<ProfileEntry, bool> reported, string notReportedReason, ModRunState notReportedState = ModRunState.NotLoaded,
            string? notReportedHelp = null)
        {
            foreach (var e in Profile.Of(kind))
            {
                if (reported(e)) continue;
                var id = e.Id ?? LeafName(e.Path);
                var (name, version) = Manifest(e.Path);
                var missing = MissingPaths.Contains(e.Path) || !DirExists(e.Path);
                Add(new CatalogMod
                {
                    Engine = kind, Id = id, Name = name ?? id, Version = e.Version ?? version ?? "", Folder = e.Path, Source = "profile",
                    Missing = missing && e.Enabled,
                    State = !e.Enabled ? ModRunState.Disabled : missing ? ModRunState.Failed : notReportedState,
                    Word = !e.Enabled ? "disabled" : missing ? "missing" : "not reported",
                    Reason = !e.Enabled ? "Switched off in the profile." : missing ? "Its folder is missing: " + e.Path : notReportedReason,
                    Help = !e.Enabled ? "Turn it on in the Kiln launcher (Profiles), then start the game again."
                        : missing ? "Repair or remove it in the Kiln launcher (Library), then start the game again." : notReportedHelp,
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
                b.Engine(kind, "error", EngineHealth.Problem, "Kiln could not read this engine's report: " + e.Message, false);
            }
        }

        Guard("kiln", () => KilnMods(b));
        Guard("native", () => NativeMods(b));
        Guard("plugin", () => PluginMods(b, bootDoc));
        Guard("chain", () => ChainMods(b, bootDoc));
        foreach (var kind in new[] { "emf", "eml", "set", "greeneye", "flame", "shroudtopia", "shroudlink", "ember" })
            Guard(kind, () => EngineMods(b, kind, bootDoc));
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
        string? profileLine = profile.Mode switch
        {
            "no-mods" => "Play once without mods: no mod loads this start.",
            "invalid" => "The profile could not be read, so no mod loads" + (profile.Error is { } pe ? $" ({pe})." : "."),
            "legacy" => "No profile: Kiln loads the mods folders.",
            _ when profile.Name is not null || profile.Id is not null => $"Profile: {profile.Name ?? profile.Id}",
            _ => null,
        };
        return new CatalogSnapshot
        {
            Engines = engines, Mods = all, Profile = profileLine, ReadAtMs = Environment.TickCount64,
            Side = src.Side.Length > 0 ? src.Side : bootDoc is { } bd2 ? Str(bd2, "side") ?? "" : "",
            Version = Interlocked.Increment(ref _version),
        };
    }

    private static long _version;

    // ---- Kiln's managed mods: Mods.All, plus profile entries the host never saw (missing folders) ----
    private static void KilnMods(Builder b)
    {
        var mods = b.Src.KilnMods();
        foreach (var m in mods.OrderBy(m => m.LoadIndex < 0 ? int.MaxValue : m.LoadIndex).ThenBy(m => m.Id, StringComparer.Ordinal))
        {
            var (state, help) = m.State switch
            {
                ModState.Loaded => (ModRunState.Loaded, (string?)null),
                ModState.Pending => (ModRunState.Waiting, "It loads when its turn comes (for example when a world starts)."),
                ModState.Disabled => (ModRunState.Disabled, "Kiln switched it off after too many errors. Start the game again to retry, or update the mod."),
                ModState.Failed => (ModRunState.Failed, "Read the reason above. Update the mod or what it needs in the Kiln launcher, then start the game again. kiln/logs/kiln.log has the details."),
                ModState.Unloaded => (ModRunState.NotLoaded, "It was unloaded by a command. Start the game again to load it."),
                _ => (ModRunState.NotLoaded, (string?)null),
            };
            var side = m.Side switch { ModSide.Client => "client", ModSide.Both => "both", _ => "server" };
            b.Add(new CatalogMod
            {
                Engine = "kiln", Id = m.Id, Name = string.IsNullOrWhiteSpace(m.Name) ? m.Id : m.Name, Version = m.Version, State = state,
                Word = m.State.ToString().ToLowerInvariant(), Reason = m.Error, Help = help, Folder = m.Directory, Side = side,
                Source = "kiln", LoadIndex = m.LoadIndex, Dependencies = m.Dependencies,
            });
        }
        var known = new HashSet<string>(mods.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        b.ProfileRest("kiln", e => e.Id is not null && known.Contains(e.Id), "Listed in the profile, but Kiln did not load it this start.",
            notReportedHelp: "Start the game again. If it stays like this, check kiln/logs/kiln.log.");
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
                if (word == "policy") reason ??= "Kiln's native policy does not load DLL mods on this game build.";
                var (name, _) = Manifest(folder);
                b.Add(new CatalogMod
                {
                    Engine = "native", Id = id, Name = name ?? id, Version = Str(m, "version") ?? "", State = state, Word = word,
                    Reason = string.IsNullOrEmpty(reason) ? null : reason, Folder = folder, Source = Str(m, "source") ?? "",
                    Side = Str(m, "side") ?? "",
                    LoadIndex = m.TryGetProperty("loadIndex", out var li) && li.ValueKind == JsonValueKind.Number ? li.GetInt32() : -1,
                    Help = state == ModRunState.Failed ? "Read the reason above. Update the DLL mod in the Kiln launcher, then start the game again." : null,
                });
            }
        }
        var why = !f.Exists ? "Kiln's native loader wrote no report this start." :
            !f.Current ? "The native loader's report is from an earlier start." : "Listed in the profile, but the native loader did not report it.";
        b.ProfileRest("native", e => e.Id is not null && reported.Contains(e.Id), why);
        b.Engine("native", f.Doc is { } nd ? Str(nd, "phase") : null, EngineHealth.Ok, f.Error is { } er ? "Its report could not be read: " + er : null, f.Current);
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
                            $"Kiln's {EngineName(engine)} plugin did not start: {Str(p, "error") ?? word}. Its mods cannot run.", true);
                    continue;
                }
                if (id.StartsWith("kiln", StringComparison.OrdinalIgnoreCase) && path is not null &&
                    !b.Profile.Of("plugin").Any(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;  // Kiln's own service plugins
                var state = StateOf(word);
                b.Add(new CatalogMod
                {
                    Engine = "plugin", Id = id, Name = id, Version = Str(p, "version") ?? "", State = state, Word = word,
                    Reason = Str(p, "error"), Folder = path, Source = "plugins",
                    Help = state == ModRunState.Failed ? "Update or remove the plugin in the Kiln launcher, then start the game again." : null,
                });
            }
        }
        b.ProfileRest("plugin", e => reported.Contains(e.Path) || reported.Contains(LeafName(e.Path)),
            boot is null ? "Kiln's start report is missing, so it is not known whether it loaded." : "Listed in the profile, but Kiln did not load it.");
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
                Engine = "chain", Id = name, Name = name, State = state, Word = word, Reason = reason, Folder = path,
                Source = Str(c, "source") ?? "chain", Missing = word == "missing",
                Help = state == ModRunState.Failed ? "Check the DLL in chain.ini (or the Kiln launcher), then start the game again." : null,
            });
        }
        b.Engine("chain", null, EngineHealth.Ok, null, true);
    }

    // ---- engines with a per-mod list in their state file ----
    private static void EngineMods(Builder b, string kind, JsonElement? boot)
    {
        var f = ReadState(b.Src, kind + ".json");
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                if (line.Folder is { } lf) reportedFolders.Add(lf.TrimEnd('\\', '/'));
                var state = StateOf(line.State);
                var reason = line.Reason;
                string? help = null;
                if (kind == "ember")
                {
                    state = line.State == "skipped" ? ModRunState.NotLoaded : ModRunState.Waiting;
                    reason ??= "Ember mods cannot run yet: Ember has not published a way for mods to run.";
                    help = "Nothing to do now. The mod stays in your profile and runs once Kiln supports Ember mods.";
                }
                else if (aside && state != ModRunState.Failed)
                {
                    state = ModRunState.NotLoaded;
                    reason ??= Str(d, "steppedAsideReason");
                }
                if (state == ModRunState.Failed)
                    help = $"Read the reason above. Update the mod in the Kiln launcher, then start the game again. kiln/logs/kiln.log has the {EngineName(kind)} lines.";
                var (mname, mversion) = line.Name is null ? Manifest(line.Folder) : (line.Name, null);
                b.Add(new CatalogMod
                {
                    Engine = kind, Id = line.Id, Name = mname ?? line.Id, Version = line.Version ?? mversion ?? "", State = state,
                    Word = line.State ?? "", Reason = reason, Help = help, Folder = line.Folder, Side = line.Side ?? "",
                    Source = line.Source ?? "",
                });
            }
        }
        var why = f.Error is { } err ? $"{EngineName(kind)}'s report could not be read: {err}. Kiln reads it again in a moment."
            : !f.Exists ? $"{EngineName(kind)} did not run this start, so this mod did not load."
            : !f.Current ? $"{EngineName(kind)} did not report this start (its last report is from an earlier start)."
            : $"Listed in the profile, but {EngineName(kind)} did not report it this start.";
        b.ProfileRest(kind, e => (e.Id is not null && reported.Contains(e.Id)) || reportedFolders.Contains(e.Path.TrimEnd('\\', '/')), why,
            notReportedHelp: !f.Exists || !f.Current ? $"Check that Kiln's {EngineName(kind)} plugin is installed (kiln/plugins), then start the game again." : null);
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
                    State = ModRunState.NotLoaded, Word = "not reported", Reason = why.Replace("this mod", "the mod"),
                });
            }
        }
        if (f.Error is not null) (health, note) = (EngineHealth.Problem, why);
        else if (!f.Current && (b.Profile.Of(kind).Count > 0 || b.Mods.ContainsKey(kind)))
            (health, note) = (EngineHealth.Idle, f.Exists
                ? $"{EngineName(kind)} did not report this start; its last report is from an earlier start."
                : $"{EngineName(kind)} did not run this start.");
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
            var who = kind == "emf" ? "EMF: stepped aside for the real EMF" : $"Stepped aside: another {EngineName(kind)} loader runs these mods";
            return (EngineHealth.Idle, why is null ? who + "." : $"{who} ({why}).");
        }
        switch (kind)
        {
            case "emf":
                if (Str(d, "emfIdle") is { } idle) return (EngineHealth.Problem, "EMF went idle on this game build: " + idle);
                return (health, null);
            case "eml":
                return word switch
                {
                    "done" => (EngineHealth.Ok, d.TryGetProperty("patched", out var p) && p.ValueKind == JsonValueKind.Number
                        ? $"Game data patched before the game started ({p.GetRawText()} change{(p.GetRawText() == "1" ? "" : "s")})." : null),
                    "failed" => (EngineHealth.Problem, $"EML failed: {Str(d, "error") ?? "see kiln.log"}. The game data was put back to normal."),
                    "aside" => (EngineHealth.Idle, "EML: stepped aside for EML's own loader" + (Str(d, "aside") is { } a ? $" ({a})." : ".")),
                    "idle" => (EngineHealth.Idle, "No EML mods this start."),
                    _ => (health, null),
                };
            case "ember":
                return (EngineHealth.Idle, "Ember: mods are found but cannot run yet.");
            case "shroudlink":
                if (Bool(d, "claimedElsewhere") == true) return (EngineHealth.Idle, "Another shroudlink loader runs these mods.");
                return (health, null);
            case "set":
                return word switch
                {
                    "stepped-aside" => (EngineHealth.Idle, "SET: stepped aside for SET's own loader" + (Str(d, "reason") is { } r ? $" ({r})." : ".")),
                    "waiting" => (EngineHealth.Ok, "Mods load at the main menu."),
                    "off" => (EngineHealth.Idle, Str(d, "reason") ?? "Off this start."),
                    _ => (health, Str(d, "reason")),
                };
            default:
                return (health, tone == "err" ? Str(d, "reason") ?? Str(d, "error") : null);
        }
    }

    // ---- ShroudForge: the profile and its mods folder for the list, the state file for the runtime ----
    private static void ShroudForge(Builder b, JsonElement? boot)
    {
        var f = ReadState(b.Src, "shroudforge.json");
        string? mode = null, reason = null, modsDir = null;
        if (f.Doc is { } d && f.Current)
        {
            mode = Str(d, "mode");
            reason = Str(d, "reason");
            modsDir = Str(d, "modsDir");
        }
        var (health, note, state) = mode switch
        {
            "hosted" => (EngineHealth.Ok, "ShroudForge: runtime hosted by Kiln.", ModRunState.Loaded),
            "self-hosted" => (EngineHealth.Idle, "ShroudForge: its own loader runs the mods; Kiln stepped aside.", ModRunState.Loaded),
            "not-installed" => (EngineHealth.Problem, "ShroudForge is not installed, so its mods cannot run.", ModRunState.Failed),
            "off" => (EngineHealth.Idle, reason is null ? "ShroudForge is off this start." : $"ShroudForge is off: {reason}.", ModRunState.NotLoaded),
            "failed" => (EngineHealth.Problem, $"ShroudForge failed: {reason ?? "see kiln.log"}.", ModRunState.Failed),
            null => (f.Exists ? EngineHealth.Idle : EngineHealth.Idle, f.Error is { } e ? "Its report could not be read: " + e
                : f.Exists ? "ShroudForge did not report this start." : "ShroudForge did not run this start.", ModRunState.NotLoaded),
            _ => (EngineHealth.Ok, null, StateOf(mode)),
        };
        var why = mode switch
        {
            "hosted" or "self-hosted" => "ShroudForge runs it. ShroudForge reports no result per mod; its own log has the details.",
            null => note,
            _ => note,
        };
        var help = state == ModRunState.Failed ? "Fix ShroudForge first (see the engine line), then start the game again." : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b.Profile.Of("shroudforge"))
        {
            var id = e.Id ?? LeafName(e.Path);
            seen.Add(LeafName(e.Path));
            seen.Add(id);
        }
        b.ProfileRest("shroudforge", _ => false, why ?? "", state, help);
        // mods the player put into ShroudForge's folder by hand (ShroudForge reads the whole folder)
        var dir = modsDir ?? Path.Combine(b.Src.GameDir, "mods");
        foreach (var sub in SubDirs(dir))
        {
            var name = LeafName(sub);
            if (seen.Contains(name)) continue;
            var manifest = Path.Combine(sub, "extended.mod.json");
            JsonElement? m;
            try { m = File.Exists(manifest) ? ReadJson(manifest, out _) : null; }
            catch (Exception) { m = null; }
            if (m is not { ValueKind: JsonValueKind.Object } md) continue;
            var on = Bool(md, "enabled") != false;
            b.Add(new CatalogMod
            {
                Engine = "shroudforge", Id = Str(md, "id") ?? name, Name = Str(md, "name") ?? name, Version = Str(md, "version") ?? "",
                Folder = sub, Source = "folder", State = on ? state : ModRunState.Disabled, Word = on ? mode ?? "" : "disabled",
                Reason = on ? why : "Switched off in ShroudForge's settings.", Help = on ? help : null,
            });
        }
        b.Engine("shroudforge", mode, health, note, f.Current);
    }

    // ---- Enshwick: the profile and <game>/mods for the list, enshwick.json for the loader and copy notes ----
    private static void Enshwick(Builder b, JsonElement? boot)
    {
        var f = ReadState(b.Src, "enshwick.json");
        string? word = null;
        var notes = new Dictionary<string, ModLine>(StringComparer.OrdinalIgnoreCase);
        var health = EngineHealth.Idle;
        string? note = f.Error is { } err ? "Its report could not be read: " + err : f.Exists ? "Enshwick did not report this start." : "Enshwick did not run this start.";
        var state = ModRunState.NotLoaded;
        if (f.Doc is { } d && f.Current)
        {
            word = Headline(d);
            foreach (var l in ModLines(d)) notes[l.Id] = l;
            var reason = Str(d, "reason");
            (health, note, state) = word switch
            {
                "started" => (EngineHealth.Ok, "Enshwick's loader started.", ModRunState.Loaded),
                "starting" => (EngineHealth.Ok, "Enshwick's loader is starting.", ModRunState.Waiting),
                "failed" => (EngineHealth.Problem, $"Enshwick's loader failed: {reason ?? "see kiln.log"}.", ModRunState.Failed),
                "no-loader" => (EngineHealth.Problem, $"Enshwick's loader is missing: {reason ?? "install Enshwick"}.", ModRunState.Failed),
                "stepped-aside" => (EngineHealth.Idle, "Enshwick: stepped aside for Enshwick's own loader.", ModRunState.NotLoaded),
                "nothing" => (EngineHealth.Idle, null, ModRunState.NotLoaded),
                _ => (EngineHealth.Idle, reason, ModRunState.Waiting),
            };
        }
        var why = state == ModRunState.Loaded ? "Enshwick's loader runs it. Enshwick reports no result per mod." : note ?? "";
        var help = state == ModRunState.Failed ? "Fix Enshwick first (see the engine line), then start the game again." : null;
        CatalogMod Refine(CatalogMod m)
        {
            if (!notes.TryGetValue(LeafName(m.Folder), out var n) && !notes.TryGetValue(m.Id, out n)) return m;
            if (n.State == "failed" || n.State == "conflict")
                return m with { State = ModRunState.Failed, Word = n.State, Reason = n.Reason is { } nr ? Sentence(nr) : "Kiln could not copy it into the game's mods folder.", Help = "Close other tools that use the game's mods folder, then start the game again." };
            if (n.State == "remove") return m with { State = ModRunState.NotLoaded, Word = "remove", Reason = "Removed from the game's mods folder." };
            return n.Reason is { } r ? m with { Reason = Sentence($"{m.Reason} {r}".Trim()) } : m;
        }
        var start = b.Mods.TryGetValue("enshwick", out var before) ? before.Count : 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in b.Profile.Of("enshwick")) seen.Add(e.Id ?? LeafName(e.Path));
        b.ProfileRest("enshwick", _ => false, why, state, help);
        if (b.Profile.ReadsFolder("enshwick"))
            foreach (var sub in SubDirs(Path.Combine(b.Src.GameDir, "mods")))
            {
                var name = LeafName(sub);
                if (seen.Contains(name) || !LooksLikeEnshwick(sub)) continue;
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
            if (File.Exists(Path.Combine(dir, "pack.toml"))) return true;
            return Directory.EnumerateFiles(dir, "*.dll").Take(1).Any();
        }
        catch (Exception) { return false; }
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
        if (c is not null && Environment.TickCount64 - c.ReadAtMs < maxAge.TotalMilliseconds) return c;
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

    /// <summary>Waits for the running refresh (tests, the preview).</summary>
    internal static CatalogSnapshot WaitForSnapshot()
    {
        Snapshot(TimeSpan.Zero);
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
