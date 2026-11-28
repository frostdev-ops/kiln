// SPDX-License-Identifier: MIT
//
// The ShroudForge group of the Kiln menu's mod list in a kiln-backend start (Kiln's interpreter runs the ShroudForge mods):
// ModCatalog.Read decides once per snapshot, before any ShroudForge folder, status, assets or actions path, which branch
// this start takes (Decide), and in every branch but Legacy builds the group here instead of ModCatalog.ShroudForge.
//
//   Legacy    hosted or installed ShroudForge, or a start with no launch plan and no kiln sfi.json: ModCatalog's own
//             code, byte for byte as before.
//   Kiln      the frozen backend is kiln: the rows are the profile's ShroudForge mods, the packages in <game>/mods and
//             the packages sfi.json names, with sfi.json's part truth (data, Lua, DLLs, skip and its chain, warning,
//             requested versus current). Before kiln-sfi has written this start's record: pending, never Active.
//   External  the frozen backend is kiln, but ShroudForge's own loader runs in the process (kiln-shroudforge says
//             self-hosted): only that state; the interpreter did not start and nothing counts as its acceptance.
//   Unknown   this start's sfi.json cannot be read or breaks its schema, or the two reports disagree: every row pending.
//
// In every branch but Legacy, nothing reads or writes <game>/shroudforge (its config, state.json, ui/actions): the mods
// root is <game>/mods (KILN_PATH_GAME + mods, as the interpreter's plan producer), and a package's settings stay in its
// own extended.mod.json there (below). The row is still the copy its engine runs (ModCatalog.EngineRunFolder with
// that root), and a package with a native-plugin.ini is listed once (ModCatalog.Xhl leaves ShroudForge packages of the
// mods root to this group).
//
// Where a kiln-backend start's package settings live: in the package's own extended.mod.json under <game>/mods,
// the file ShroudForge 1.7.9 writes them to as well (modloader update_mod_configuration.rs: write_package_json(package,
// "extended.mod.json"); its config/extended-mod/extended-mod.json is only the schema document's place). The page keeps
// using ShroudForgePackages' byte-preserving writer on that file. Until the interpreter observes those files while the
// game runs, every switch and setting change in kiln mode applies at the next start, and the page says so.
//
// Threads: Decide and Build run inside ModCatalog.Read (a worker); the render thread only reads the rows they made.
// Texts are English for now (runtime/locales takes no private keys).
using System.Text.Json;

namespace Kiln;

internal enum SfiBranchKind
{
    Legacy,
    Kiln,
    External,
    Unknown,
}

/// <summary>Which branch the ShroudForge group takes this start, decided once per snapshot (<see cref="SfiCatalog.Decide"/>).</summary>
internal sealed record SfiBranch(SfiBranchKind Kind, SfiStateRead State, string? Why = null, string? SfReason = null)
{
    public static readonly SfiBranch Legacy = new(SfiBranchKind.Legacy, default);
}

/// <summary>What the page draws of a kiln-mode row beyond its state: the part truth and requested-versus-current lines.</summary>
internal sealed record SfiRow(SfiBranchKind Branch, bool NextStartOnly, IReadOnlyList<(string Text, bool Warn)> Lines);

internal static class SfiCatalog
{
    internal const string Interpreter = "Kiln's interpreter";

    /// <summary>Worker only: <paramref name="src"/> with this start's branch (decided once; a source that has one keeps it).</summary>
    public static CatalogSources Decide(CatalogSources src)
    {
        if (src.Sfi is not null) return src;
        SfiBranch branch;
        try { branch = Branch(src); }
        catch (Exception e)  // whatever the files hold: the ShroudForge rows say so, and no ShroudForge folder is read
        {
            branch = new SfiBranch(SfiBranchKind.Unknown, default, "the interpreter's state could not be read: " + e.Message);
        }
        return src with { Sfi = branch };
    }

    private static SfiBranch Branch(CatalogSources src)
    {
        var state = SfiState.Read(src);
        // kiln-shroudforge's status of this start (kiln/state, not ShroudForge's folder): the frozen backend it copied from
        // kiln.launch, and whether ShroudForge's own loader runs in the process
        var sf = ModCatalog.ReadState(src, "shroudforge.json");
        string? planned = null, plannedId = null, mode = null, reason = null;
        if (sf.Current && sf.Doc is { } d)
        {
            mode = Str(d, "mode");
            reason = Str(d, "reason");
            if (d.TryGetProperty("launch", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                planned = Str(l, "backend") ?? "";
                plannedId = Str(l, "launchId");
            }
        }
        static SfiBranch Unknown(SfiStateRead s, string why) => new(SfiBranchKind.Unknown, s, why);
        switch (planned)
        {
            case "hosted" or "installed" or "none":
                // kiln.launch froze another backend; a kiln record of this start would contradict it
                return state.Start == SfiStart.Kiln ? Unknown(state, $"kiln-shroudforge names backend {planned} for this start, the interpreter's state names kiln") : SfiBranch.Legacy;
            case "kiln":
                if (mode == "self-hosted") return new SfiBranch(SfiBranchKind.External, state, null, reason);
                return state.Start switch
                {
                    SfiStart.Kiln when plannedId is not null && state.LaunchId != plannedId =>
                        Unknown(state, $"the interpreter's state is of launch {state.LaunchId}, kiln-shroudforge's of launch {plannedId}"),
                    SfiStart.Kiln => new SfiBranch(SfiBranchKind.Kiln, state),
                    SfiStart.Unknown => Unknown(state, state.Why ?? "the interpreter's state could not be read"),
                    SfiStart.Aside => Unknown(state, $"the interpreter's state names backend {state.Backend} for this start, kiln-shroudforge names kiln"),
                    // None, Other: kiln-sfi has not written this start's record yet
                    _ => new SfiBranch(SfiBranchKind.Kiln, state),
                };
            case null:
                // no launch plan (an older core, a legacy start) or no status of this start: sfi.json alone decides
                return state.Start switch
                {
                    SfiStart.Kiln => new SfiBranch(SfiBranchKind.Kiln, state),
                    SfiStart.Unknown => Unknown(state, state.Why ?? "the interpreter's state could not be read"),
                    _ => SfiBranch.Legacy,
                };
            default:
                return Unknown(state, $"kiln-shroudforge names an unknown backend for this start: {Clip(planned, 64)}");
        }
    }

    /// <summary>The mods root of a kiln-backend start (&lt;game&gt;/mods, never ShroudForge's config), or null in Legacy.</summary>
    public static string? ModsRoot(CatalogSources src) =>
        src.Sfi is { Kind: not SfiBranchKind.Legacy } ? Path.Combine(src.GameDir, "mods") : null;

    // ------------------------------------------------------------------------------------- the group ----

    /// <summary>Worker only: the ShroudForge group of a non-Legacy start. <paramref name="emlRows"/> is the EML group's list:
    /// an EML package the interpreter runs stays an EML row (with the interpreter's truth), never a second ShroudForge row;
    /// a ShroudForge package kiln-eml or the profile calls EML (ModCatalog.IsSfOwned) moves from it to this group.</summary>
    public static void Build(SfiBranch branch, CatalogSources src, IReadOnlyList<ModCatalog.ProfileEntry> entries,
        Func<ModCatalog.ProfileEntry, string> folderOf, Func<ModCatalog.ProfileEntry, bool> missing, List<CatalogMod> emlRows,
        Action<CatalogMod> add, Action<string?, EngineHealth, string?, bool> engine)
    {
        var record = branch.Kind == SfiBranchKind.Kiln ? branch.State.Record : null;
        var root = Path.Combine(src.GameDir, "mods");
        var nextStart = true;  // until the interpreter observes them: every change applies at the next start
        var used = new HashSet<string>(StringComparer.Ordinal);  // sfi.json packages a row shows

        CatalogMod Row(CatalogMod m, string? pkgId, bool? requested)
        {
            var p = record?.Find(pkgId);
            if (p is not null) used.Add(p.Id);
            var (state, word, reason, help) = p is not null ? Truth(record!, p) : Default(branch);
            var lines = Lines(branch, record, p, requested);
            if (requested is { } on && p is not null && RestartLine(p, on) is { } restart)
            {
                // what a restart changes is what helps; a save warning stays first
                help = p.Warning is not null && help is not null ? $"{help} {restart}" : restart;
                lines = [.. lines, (restart, true)];
            }
            return m with
            {
                State = state, Word = word, Reason = reason, Help = help,
                Active = p is not null && record!.Active(p.Id),
                Name = m.Name.Length > 0 && m.Name != m.Id ? m.Name : p?.Name ?? m.Name,
                Version = m.Version.Length > 0 ? m.Version : p?.Version ?? "",
                Sfi = new SfiRow(branch.Kind, nextStart, lines),
            };
        }

        // 1. EML rows of packages in the mods root that the interpreter lists: refined in place. A ShroudForge package
        // among them (ModCatalog.IsSfOwned: its own settings in an extension that says EML, Nexus 347) is this group's row,
        // with its switch and settings: where the interpreter lists it, before it has reported, and while ShroudForge's
        // own loader or an unreadable state runs the folder (Default says which)
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // (one row per mod: an EML row for the folder of a profile ShroudForge entry, the launcher's compatibility entry for
        // an EML package that needs ShroudForge's runtime, goes; step 2's row stands for it. kiln-eml names the entry's
        // Library folder as the plan's data writer, Kiln's copy in the mods root otherwise: both are the entry's. A failed or
        // missing EML row stays: kiln-eml's failure shows nowhere else)
        var profileFolders = entries.SelectMany(e => new[] { ModCatalog.NormPath(e.Path), ModCatalog.NormPath(folderOf(e)) }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        emlRows.RemoveAll(m => !m.NeedsAttention && m.Folder is { } ef && profileFolders.Contains(ModCatalog.NormPath(ef)));
        foreach (var m in ModCatalog.TakeSfPackages(emlRows, root, m => record is null || record.Find(SfPackageId(m.Folder!)) is not null))
        {
            var facts = ModCatalog.SfFacts(m.Folder!);
            var pkgId = SfPackageId(m.Folder!);
            var row = Row(m with { Engine = "shroudforge" }, pkgId, facts?.On);
            // nothing of the interpreter's about it yet (it has not reported, or ShroudForge's own loader runs the folder):
            // kiln-eml's words where they say the profile has it off, or that it runs although off
            if (record?.Find(pkgId) is null && ModCatalog.KeepsEmlWords(m)) row = row with { State = m.State, Word = m.Word, Reason = m.Reason, Help = m.Help };
            add(row);
            covered.Add(ModCatalog.NormPath(m.Folder!));
        }
        for (var i = 0; i < emlRows.Count; i++)
        {
            var m = emlRows[i];
            if (m.Folder is not { } f || !InRoot(f, root) || record is null) continue;
            var facts = ModCatalog.SfFacts(f);
            var pkgId = facts?.Id ?? ManifestId(f);
            if (record.Find(pkgId) is null) continue;
            emlRows[i] = Row(m, pkgId, facts?.On);
            covered.Add(ModCatalog.NormPath(f));
        }

        // 2. the profile's ShroudForge mods: the copy the interpreter runs (ModCatalog.EngineRunFolder under the mods root)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            var id = e.Id ?? Leaf(e.Path);
            var folder = folderOf(e);
            seen.Add(Leaf(e.Path));
            seen.Add(Leaf(folder));
            seen.Add(id);
            var (name, version) = ModCatalog.Manifest(folder);
            var facts = ModCatalog.SfFacts(folder);
            var pkgId = facts?.Id ?? ManifestId(folder) ?? id;
            var gone = missing(e);
            var row = new CatalogMod
            {
                Engine = "shroudforge", Id = id, Name = name ?? id, Version = e.Version ?? version ?? "", Folder = folder, Source = "profile",
                Missing = gone && e.Enabled,
            };
            if (record?.Find(pkgId) is null && (!e.Enabled || gone))
            {
                used.Add(pkgId);  // nothing of it in this start's record to show
                add(row with
                {
                    State = !e.Enabled ? ModRunState.Disabled : ModRunState.Failed, Word = !e.Enabled ? "disabled" : "missing",
                    Reason = !e.Enabled ? KilnText.T("catalog.profile.off") : KilnText.T("catalog.profile.missing", ("path", e.Path)),
                    Help = !e.Enabled ? KilnText.T("catalog.help.turn_on") : KilnText.T("catalog.help.repair"),
                    Sfi = new SfiRow(branch.Kind, nextStart, Lines(branch, record, null, facts?.On)),
                });
                continue;
            }
            add(Row(row, pkgId, facts?.On));
        }

        // 3. packages in the mods root the profile does not list (the player put them there)
        var emlFolders = new HashSet<string>(emlRows.Where(m => m.Folder is not null).Select(m => ModCatalog.NormPath(m.Folder!)), StringComparer.OrdinalIgnoreCase);
        foreach (var sub in SubDirs(root))
        {
            var leaf = Leaf(sub);
            var norm = ModCatalog.NormPath(sub);
            if (seen.Contains(leaf) || covered.Contains(norm)) continue;
            if (emlFolders.Contains(norm) && string.Equals(ModCatalog.PlacedMarker(sub)?.Kind, "eml", StringComparison.OrdinalIgnoreCase)) continue;
            // a ShroudForge package the interpreter does not run while kiln-eml lists it: the EML row only (it says why its
            // settings do nothing)
            if (emlFolders.Contains(norm) && ModCatalog.IsSfOwned(sub)) continue;
            if (ModCatalog.SfFacts(sub) is not { } facts) continue;
            var (sid, sname, sversion, on) = facts;
            var pkgId = sid ?? leaf;
            var row = new CatalogMod { Engine = "shroudforge", Id = pkgId, Name = sname ?? leaf, Version = sversion ?? "", Folder = sub, Source = "folder" };
            if (record?.Find(pkgId) is null && !on)
            {
                add(row with
                {
                    State = ModRunState.Disabled, Word = "disabled", Reason = KilnText.T("catalog.sf.off_in_settings"),
                    Sfi = new SfiRow(branch.Kind, nextStart, Lines(branch, record, null, on)),
                });
                continue;
            }
            add(Row(row, pkgId, on));
        }

        // 4. what sfi.json names that no folder showed (gone from the profile with a warning, a folder not found)
        if (record is not null)
            foreach (var p in record.Packages)
                if (!used.Contains(p.Id) && (p.Intent == "selected" || p.Warning is not null))
                    add(Row(new CatalogMod { Engine = "shroudforge", Id = p.Id, Name = p.Name ?? p.Id, Version = p.Version ?? "", Source = "sfi" }, p.Id, null));

        var (word, health, note) = Engine(branch);
        engine(word, health, note, record is not null);
    }

    private static (string? Word, EngineHealth Health, string Note) Engine(SfiBranch b)
    {
        var record = b.Kind == SfiBranchKind.Kiln ? b.State.Record : null;
        return b.Kind switch
        {
            SfiBranchKind.External => ("external", EngineHealth.Idle,
                "ShroudForge's own loader runs the ShroudForge mods this start, so Kiln's interpreter did not start" +
                (b.SfReason is { Length: > 0 } r ? $" ({Clip(r, 300)})." : ".")),
            SfiBranchKind.Unknown => ("unknown", EngineHealth.Problem, $"{Interpreter}'s state cannot be read ({Clip(b.Why ?? "", 300)}): nothing is shown as running until it can."),
            _ when record is null => ("pending", EngineHealth.Idle, $"{Interpreter} runs the ShroudForge mods this start; it has not reported yet."),
            _ => record.Phase switch
            {
                "running" => ("running", EngineHealth.Ok, $"{Interpreter} runs the ShroudForge mods this start. Pick a mod to see what runs and change its settings (changes apply at the next start)."),
                "starting" => ("starting", EngineHealth.Ok, $"{Interpreter} is starting ShroudForge's mods."),
                "ended" => ("ended", EngineHealth.Idle, $"{Interpreter} has stopped for this start."),
                _ => ("refused", EngineHealth.Problem, $"{Interpreter} did not start this start: {Clip(record.ReasonText ?? "", 500)}"),
            },
        };
    }

    /// <summary>A row the interpreter's record does not name.</summary>
    private static (ModRunState, string, string, string?) Default(SfiBranch b) => b.Kind switch
    {
        SfiBranchKind.External => (ModRunState.NotLoaded, "external",
            "ShroudForge's own loader runs the ShroudForge mods this start, not Kiln's interpreter: Kiln cannot confirm this mod.", null),
        SfiBranchKind.Unknown => (ModRunState.Waiting, "unknown", $"{Interpreter}'s state cannot be read, so whether it runs this mod is not known.", null),
        _ when b.State.Record is null => (ModRunState.Waiting, "unconfirmed", $"{Interpreter} has not reported this start yet.", null),
        _ when b.State.Record.Phase == "refused" => (ModRunState.NotLoaded, "refused", $"{Interpreter} did not start this start: {Clip(b.State.Record.ReasonText ?? "", 500)}", null),
        _ => (ModRunState.NotLoaded, "not in plan", "Not part of this start's launch plan: Kiln's interpreter does not run it this start.", null),
    };

    /// <summary>A package's state in the words a player sees, from the interpreter's own record of it.</summary>
    internal static (ModRunState State, string Word, string Reason, string? Help) Truth(SfiRecord r, SfiPackageState p)
    {
        string WithWarning(string text) => p.Warning is { } w ? $"{text} {Clip(w.Text, 2000)}" : text;
        if (r.Phase == "refused")
            return (ModRunState.NotLoaded, "refused", $"{Interpreter} did not start this start: {Clip(r.ReasonText ?? "", 500)}", null);
        switch (p.Intent)
        {
            case "disabled":
                return (ModRunState.Disabled, "disabled", WithWarning("Switched off for this start."), p.Warning is not null ? Presence(p) : KilnText.T("catalog.help.turn_on"));
            case "other-side":
                return (ModRunState.NotLoaded, "other side", "For the other side (client or server): not run here.", null);
            case "absent":
                return (ModRunState.Disabled, "removed", WithWarning("No longer in the profile."), p.Warning is not null ? Presence(p) : null);
        }
        if (p.Skip is { } s)
        {
            var text = $"{(s.Late ? "Stopped during this start" : "Skipped this start")} ({PartName(s.Part)}): {Clip(s.Text, 2000)}";
            if (s.Chain.Count > 0) text += $" Because of: {string.Join(" > ", s.Chain)}.";
            if (s.Loader is { } other) text += $" {other} still runs part of it.";
            return (ModRunState.Failed, s.Late ? "stopped" : "skipped", text, p.Warning is { } w ? Clip(w.Text, 2000) : "Fix what the reason names, then restart the game.");
        }
        if (p.Lua == "failed" || p.Data == "failed" || p.Dlls.Any(d => d.State is "failed" or "refused"))
            return (ModRunState.Failed, "failed", $"{Interpreter} could not run it: {Parts(p)}.", "See kiln.log for the reason, then restart the game.");
        switch (p.Lua)
        {
            case "active":
                return (ModRunState.Loaded, "active", $"{Interpreter} runs it. {Parts(p)}.", null);
            case "stopped":
                return (ModRunState.NotLoaded, "stopped", p.ArchivePresent switch
                {
                    true => "Its runtime stopped; its game data stays applied until the game restarts.",
                    null => "Its runtime stopped; whether its game data is still applied is not known.",
                    false => "Its runtime stopped.",
                }, null);
        }
        if (p.Data == "uncertain" || p.RuntimeActive is null && p.Lua is not ("not-needed" or "excluded"))
            return (ModRunState.Waiting, "unknown", $"{Interpreter} has not confirmed it yet: {Parts(p)}.", null);
        if (p.Lua is "admitted" or "ready")
            return (ModRunState.Waiting, "starting", $"Admitted: its runtime starts when {Interpreter} reaches it. {Parts(p)}.", null);
        return p.Data switch
        {
            "committed" or "cached-verified" or "verified-restored" => (ModRunState.Loaded, "applied", $"Its game data is applied this start. {Parts(p)}.", null),
            "staged" or "prepared" => (ModRunState.Waiting, "preparing", $"Its game data is being prepared. {Parts(p)}.", null),
            "excluded" => (ModRunState.NotLoaded, "excluded", $"Its game data is not part of this start. {Parts(p)}.", null),
            _ when p.Dlls.Any(d => d.State is "loaded" or "retained") => (ModRunState.Loaded, "loaded", $"Its DLLs are loaded. {Parts(p)}.", null),
            _ => (ModRunState.Waiting, "unconfirmed", $"{Interpreter} has not confirmed it yet: {Parts(p)}.", null),
        };
    }

    private static string Presence(SfiPackageState p) => p.Warning?.Presence switch
    {
        "not-applied" => "Its game data is not applied. Restore the mod and restart before you save.",
        "data-applied-runtime-stopped" => "Its game data is still applied but its runtime stopped. Restore the mod and restart before you save.",
        _ => "Whether its game data is applied is not known. Restore the mod and restart before you save.",
    };

    private static string PartName(string part) => part switch
    {
        "data" => "game data", "lua" => "Lua", "dll" => "DLL", _ => "whole package",
    };

    /// <summary>"Game data: applied. Lua: active. DLLs: x.dll loaded."</summary>
    internal static string Parts(SfiPackageState p)
    {
        var data = p.Data switch
        {
            "not-needed" => "none",
            "committed" or "cached-verified" or "verified-restored" => "applied",
            "uncertain" => "not known",
            _ => p.Data,
        };
        var text = $"Game data: {data}. Lua: {(p.Lua == "not-needed" ? "none" : p.Lua)}";
        if (p.Dlls.Count > 0) text += ". DLLs: " + string.Join(", ", p.Dlls.Take(8).Select(d => $"{Clip(d.Name, 80)} {d.State}")) + (p.Dlls.Count > 8 ? ", ..." : "");
        return text;
    }

    /// <summary>The switch in the package's file (requested for the next start) against what runs now, or null when they agree.</summary>
    internal static string? RestartLine(SfiPackageState p, bool requestedOn)
    {
        var selected = p.Intent == "selected";
        if (requestedOn && !selected)
            return "Switched on for the next start: Kiln's interpreter takes it when the game starts again. Restart to apply.";
        if (!requestedOn && selected)
        {
            if (p.RuntimeActive == true || p.ArchivePresent == true)
                return "Switched off for the next start, but it still runs until the game restarts" +
                       (p.ArchivePresent == true ? " (its game data stays applied)." : ".");
            if (p.ArchivePresent is null && p.Data is not "not-needed")
                return "Switched off for the next start; whether its game data is still applied is not known until the game restarts.";
            return "Switched off for the next start.";
        }
        return null;
    }

    private static IReadOnlyList<(string, bool)> Lines(SfiBranch b, SfiRecord? r, SfiPackageState? p, bool? requested)
    {
        var lines = new List<(string, bool)>();
        if (p is null)
        {
            lines.Add((b.Kind switch
            {
                SfiBranchKind.External => "ShroudForge's own loader runs this start; Kiln's interpreter does not.",
                SfiBranchKind.Unknown => $"{Interpreter}'s state for this start cannot be read.",
                _ when r is null => $"{Interpreter} has not reported this start yet.",
                _ => $"{Interpreter} does not run it this start.",
            }, b.Kind == SfiBranchKind.Unknown));
        }
        else
        {
            lines.Add(($"This start: {Parts(p)}.", false));
            lines.Add(($"Runtime: {(p.RuntimeActive switch { true => "running", false => "not running", null => "not known" })}." +
                       (p.ArchivePresent switch { true => " Game data in the archive: yes.", false => " Game data in the archive: no.", null => p.Data == "not-needed" ? "" : " Game data in the archive: not known." }), false));
            if (p.Skip is { } s) lines.Add(($"Skipped ({PartName(s.Part)}): {Clip(s.Text, 2000)}" + (s.Chain.Count > 0 ? $" Because of: {string.Join(" > ", s.Chain)}." : ""), true));
            if (p.Warning is { } w) lines.Add((Clip(w.Text, 2000), true));
        }
        if (requested is { } on) lines.Add(($"Requested for the next start: {(on ? "on" : "off")}.", false));
        return lines;
    }

    // ------------------------------------------------------------------------------------- helpers ----

    private static bool InRoot(string folder, string root) =>
        Path.GetDirectoryName(folder.TrimEnd('/', '\\')) is { } parent && ModCatalog.SamePath(parent, root);

    /// <summary>The package id a folder's row is looked up by in the interpreter's record (as step 1 below does).</summary>
    private static string? SfPackageId(string folder) => ModCatalog.SfFacts(folder)?.Id ?? ManifestId(folder);

    private static string? ManifestId(string folder)
    {
        try
        {
            var path = Path.Combine(folder, "mod.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1 << 20) return null;
            return ModCatalog.ReadJson(path, out _) is { ValueKind: JsonValueKind.Object } d ? Str(d, "id") : null;
        }
        catch (Exception) { return null; }
    }

    private static IEnumerable<string> SubDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateDirectories(dir).Where(d => !Leaf(d).StartsWith('.')).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
        }
        catch (Exception) { return []; }
    }

    private static string Leaf(string? p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        var t = p.TrimEnd('\\', '/');
        var i = t.LastIndexOfAny(['\\', '/']);
        return i >= 0 ? t[(i + 1)..] : t;
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string s, int max) => ShroudForgeUi.Clip(s, max);
}
