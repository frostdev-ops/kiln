// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln;
using Kiln.Testing;
using static Kiln.Testing.ClientCheckTags;

namespace ClientPass;

// 08-onboot (chapter 08): Mod.OnBoot on a game client. mods.boot.client and mods.boot.vfx are held on a client until these checks pass
// (native/core/gates/dev28-08-onboot.json). The fixture is the sample boot-gallery (chapter 08 §6, built with S8): without it in the
// profile both checks end at Need and cannot pass. Each check runs over several starts; the part reached is kept in the mod's data
// folder. Orders 3000-3099 (port block 28100).
public sealed partial class ClientPassMod
{
    private const string BootArea = "OnBoot";
    private const string BootGallery = "boot-gallery";
    private const string BootFailMod = "boot-callback-fail";

    // boot-gallery's definitions (chapter 08 §6) as Mods.BootState lists them (kind/key). The first five are boot kinds of this build and
    // are looked up in the registries; npcs, buildingMaterials and props are not boot kinds yet (boot-definitions-test: NOT BUILT).
    private static readonly (string Kind, string Key)[] BootGalleryProbed =
        [("statuses", "cold"), ("effects", "cold-hit"), ("items", "wood-token"), ("spells", "cold-bolt"), ("talents", "cold-talent")];

    private static readonly (string Kind, string Key)[] BootGalleryNotProbed =
        [("npcs", "cold-keeper"), ("buildingMaterials", "cold-stone"), ("props", "cold-prop")];

    [ClientCheck("08-boot.first-start-definitions", "First-start content and rehosting", Area = BootArea, Since = Dev28,
        Tags = [Visual], Needs = ClientCheckNeeds.Hosted, Order = 3000)]
    public async Task BootFirstStartDefinitions(CheckContext c)
    {
        if (!await Need(c, BootGallery) || !await NeedOpen(c, "mods.boot.client", "mods.boot.vfx")) return;
        var stageFile = Path.Combine(c.Mod.DataDirectory, "boot-first-start.stage");
        var stage = BootStage.Read(stageFile);
        var pid = Environment.ProcessId;
        var started = BootStage.ThisStart;

        if (stage is { Part: 2 } && stage.SameStart)
        {
            // part 2: the same start, after leaving to the main menu and hosting again
            var generation = await BootGalleryCommitted(c);
            c.Expect(generation == stage.Generation, "hosting again kept the boot generation (nothing was committed again)",
                $"part 1: {stage.Generation}, now: {generation}");
            BootAuthorRanOnce(c);
            new BootStage(3, pid, generation, started).Write(stageFile);
            c.Instruct($"Part 2 of 3. You left the world and hosted it again in the same start. Are the Boot Token, the Boot Bolt and " +
                       $"the talent still there and as before? If not, fail now. If they are, press skip (the pass is given in part 3 " +
                       $"only) and make two more starts: (A) quit, enable boot-gallery's " +
                       $"server-only variant before the start (its README names it), start the game and join a dedicated server whose " +
                       $"profile has boot-gallery: the Boot items must be there without a second start; remember what you saw. (B) quit, " +
                       $"add {BootFailMod} to the profile (a required boot mod whose OnBoot throws) and start: the start must end before the " +
                       $"game shows any window or menu. Remove " +
                       $"{BootFailMod}, start again, host a world and run this check again: part 3 reads what the stopped start wrote.");
            await c.Verdict();
            return;
        }

        if (stage is { Part: 3 } && !stage.SameStart)
        {
            // part 3: the start after the one the failure mod stopped
            BootStage.Clear(stageFile);
            var prev = BootLog(c, "kiln.prev.log");
            if (prev is not null)
            {
                var stop = prev.LastOrDefault(l => l.Contains("boot: the start stops before the game's entry point (exit code 0x4B01)", StringComparison.Ordinal));
                c.Expect(stop is not null, "the previous start stopped before the game's entry point, exit code 0x4B01 (kiln.prev.log)", stop);
                c.Expect(stop?.Contains(BootFailMod, StringComparison.Ordinal) == true, $"the stop names {BootFailMod}", stop);
                var handed = prev.FirstOrDefault(l => l.Contains("boot complete; handing control to the game", StringComparison.Ordinal));
                c.Expect(handed is null, "the stopped start never handed control to the game (no world, no save loaded)", handed);
            }
            c.Expect(!Mods.IsLoaded(BootFailMod), $"{BootFailMod} is out of the profile again");
            await BootGalleryCommitted(c);
            c.Instruct($"Part 3 of 3. Did the start with {BootFailMod} end before any game window or menu appeared (the launcher may " +
                       "show its exit code: 0x4B01 on Windows, 1 under a Unix wrapper)? And in (A), the joined start with the server-only " +
                       "variant: were the Boot items there at once? Pass only if both were right; on a fail, say which in the note.");
            await c.Verdict();
            return;
        }

        if (stage is { Part: 3 })
        {
            await BootCannotJudge(c, "part 3 reads the start that boot-callback-fail stopped, and this is still the start of part 2",
                $"Part 3 runs in the start after the one {BootFailMod} stopped: do (A) and (B) of part 2 first, then run this check again.");
            return;
        }

        if (stage is { Part: 2 })
        {
            c.Log.Info($"client-pass: {c.Id}: part 2 was left in another start (it needs the start of part 1: leave and host again, do not quit); part 1 again");
            BootStage.Clear(stageFile);
        }

        // part 1: the first start with boot-gallery enabled, no baked or pending files of it
        c.Instruct("Part 1 of 3. This must be the first start after boot-gallery was added to the profile (a fresh copy of the sample: " +
                   "no blocks/boot-gallery.json next to it and nothing of it under kiln/data). Wait while the check reads the boot " +
                   "state (at most two minutes).");
        var first = await BootGalleryCommitted(c);
        BootAuthorRanOnce(c);
        var data = Path.GetFullPath(Path.Combine(c.Mod.DataDirectory, ".."));
        var pending = new[]
            {
                Path.Combine(data, "skills", "defined", BootGallery + ".json"), Path.Combine(data, "worldtables", "pending", BootGallery),
                Path.Combine(data, "vfx", "pending", BootGallery),
            }
            .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        c.Expect(pending.Count == 0, "no pending cache of boot-gallery: its content came from the boot generation of this start",
            pending.Count == 0 ? null : string.Join(", ", pending));
        if (!c.Player.IsNone)
        {
            await GiveBlock(c, BootGallery, "wood-token", 5);
            await GiveBlock(c, BootGallery, "cold-bolt");
        }
        if (first != 0) new BootStage(2, pid, first, started).Write(stageFile);
        c.Instruct("Part 1 of 3. You got 5 Boot Token and the Boot Bolt spell. Open the inventory and look at them: names, icons and " +
                   "tooltips. Cast the Boot Bolt at a creature as any spell: the hit gives it Boot Cold for 4 s. Open the skill tree: a new " +
                   "node \"Boot Cold Talent\" sits next to an existing node and is linked to it. Is all of it there, drawn and working, " +
                   "in this first start? If not, fail now. If it is, press skip (the pass is given in part 3 only), leave the world to the main menu, host it again (do not quit the game) and " +
                   "run this check again.");
        await c.Verdict();
    }

    [ClientCheck("08-loader-order", "Other-loader consumer ordering", Area = BootArea, Since = Dev28,
        Tags = [Frameworks, Visual], Needs = ClientCheckNeeds.Hosted, Order = 3001)]
    public async Task BootLoaderOrder(CheckContext c)
    {
        if (!await Need(c, BootGallery) || !await NeedOpen(c, "mods.boot.client", "mods.boot.vfx")) return;
        var order = BootLoaderOrderOf(c, out var detail, out var published);
        if (order is null)
        {
            await BootCannotJudge(c, $"no other loader started with this game ({detail})",
                "This check needs another loader next to Kiln in this start: the frameworks profile (docs/kiln/client-pass.md), with a " +
                $"loader that starts its own DLL (EMF, a ShroudForge loader file, a chain DLL in dinput8-chain.ini). This start: {detail}.");
            return;
        }
        c.Log.Info($"client-pass: {c.Id}: {order}: {detail}");
        c.Expect(published, "kiln/state/boot.json: the boot generation was published before the game's entry point", detail);

        var stageFile = Path.Combine(c.Mod.DataDirectory, "boot-loader-order.stage");
        var stage = BootStage.Read(stageFile);
        var part = stage is { Part: 2 } && !stage.SameStart ? 2 : 1;
        var orders = new[] { "kiln-first", "other-first" };
        if (part == 2)
            c.Expect(stage!.Generation != (ulong)Array.IndexOf(orders, order), "part 2 runs with the other loader order",
                $"part 1: {orders[Math.Min(1, (int)stage.Generation)]}, now: {order}");
        await BootGalleryCommitted(c);
        if (!c.Player.IsNone) await GiveBlock(c, BootGallery, "cold-bolt");
        if (part == 1)
        {
            new BootStage(2, Environment.ProcessId, (ulong)Array.IndexOf(orders, order), BootStage.ThisStart).Write(stageFile);
            c.Instruct($"Part 1 of 2, loader order: {(order == "kiln-first" ? "Kiln started first and loaded the other loader" : "the other loader started first")} " +
                       $"({detail}). Cast the Boot Bolt you just got and open the skill tree: are the Boot Bolt and the \"Boot Cold " +
                       "Talent\" node there and working in this first world of the start? If not, fail now. If they are, press skip (the pass is given in part 2 only), quit, swap which loader " +
                       "starts first (docs/kiln/universal-loader.md: the other loader's own dinput8.dll in front with Kiln chained from it, " +
                       "or the other way round), start again, host a world and run this check again.");
        }
        else
        {
            BootStage.Clear(stageFile);
            c.Instruct($"Part 2 of 2, loader order: {(order == "kiln-first" ? "Kiln first" : "the other loader first")} ({detail}). Cast " +
                       "the Boot Bolt you just got and open the skill tree: are the Boot Bolt and the \"Boot Cold Talent\" node there " +
                       "and working, as in part 1? Pass only if both orders had them in the first world of the start.");
        }
        await c.Verdict();
    }

    // ------------------------------------------------------------------------------------------------ helpers ---

    /// <summary>Waits up to 120 s for boot-gallery's committed state and asserts its definitions in the boot report and in the registries.
    /// Returns the boot generation (0: not committed).</summary>
    private static async Task<ulong> BootGalleryCommitted(CheckContext c)
    {
        await c.Until(() => Mods.BootState(BootGallery).State == BootModState.Committed, TimeSpan.FromSeconds(120),
            "boot-gallery's boot definitions are committed (Mods.BootState)");
        var report = Mods.BootState(BootGallery);
        c.Expect(report.State == BootModState.Committed && report.Generation != 0,
            $"Mods.BootState(\"{BootGallery}\"): Committed with a generation",
            $"{report.State}, generation {report.Generation}, {report.Definitions.Count} definition(s)" +
            (report.Issues.Count == 0 ? "" : "; " + string.Join("; ", report.Issues.Select(i => $"{i.Code} {i.Kind}/{i.Key}: {i.Reason}"))));
        if (report.State != BootModState.Committed) return 0;
        foreach (var (kind, key) in BootGalleryProbed)
            c.Expect(report.Definitions.Contains($"{kind}/{key}"), $"the boot report lists {kind}/{key}");
        foreach (var (kind, key) in BootGalleryNotProbed)
            c.Log.Info($"client-pass: {c.Id}: {kind}/{key} is {(report.Definitions.Contains($"{kind}/{key}") ? "in" : "not in")} the boot report; " +
                       "no registry lookup in this check (not a boot kind in this build)");

        var items = NewContent.Definitions();
        string Item(string key) => items.FirstOrDefault(x => x.Name == $"{BootGallery}.{key}") is { } x ? $"{x.State} (owner {x.Mod})" : "missing";
        c.Expect(items.Any(x => x.Name == $"{BootGallery}.wood-token" && x.State == "registered"), "the item boot-gallery.wood-token is registered",
            Item("wood-token"));
        c.Expect(items.Any(x => x.Name == $"{BootGallery}.cold-bolt" && x.State == "registered"), "the spell item boot-gallery.cold-bolt is registered",
            Item("cold-bolt"));
        var cold = Content.Buffs.Definitions().FirstOrDefault(b => b.Name == $"{BootGallery}.cold");
        c.Expect(cold is { Error: null } && cold.State is "registered" or "defined", "the status boot-gallery.cold is a buff",
            $"{cold?.State ?? "missing"} {cold?.Error}");
        var templates = Content.Templates.Definitions().Where(t => t.Name.StartsWith($"{BootGallery}.cold-bolt", StringComparison.Ordinal)).ToList();
        c.Expect(templates.Count > 0 && templates.All(t => t.Error == null), "cold-bolt's templates are there (its impact runs cold-hit)",
            string.Join(", ", templates.Select(t => $"{t.Name} {t.State}")));
        var node = GameTree.Definitions.FirstOrDefault(n => n.Name == $"{BootGallery}.cold-talent");
        c.Expect(node != null && node.Error == null && node.Index >= 0, "the talent boot-gallery.cold-talent is a node of the game's tree",
            $"{node?.State ?? "missing"}, index {node?.Index}");
        return report.Generation;
    }

    /// <summary>Asserts that this start ran boot-gallery's OnBoot exactly once (Kiln.Host's "boot: &lt;id&gt;: OnBoot staged" line in
    /// kiln.log, which starts empty with each start).</summary>
    private static void BootAuthorRanOnce(CheckContext c)
    {
        if (BootLog(c, "kiln.log") is not { } log) return;
        var runs = log.Count(l => l.Contains($"boot: {BootGallery}: OnBoot staged", StringComparison.Ordinal));
        c.Expect(runs == 1, "boot-gallery's OnBoot ran once in this start (no new author invocation)", $"{runs} \"OnBoot staged\" line(s) in kiln.log");
    }

    /// <summary>The lines of kiln/logs/<paramref name="file"/> (kiln.log: this start, kiln.prev.log: the one before), or null after a failed
    /// assertion. The game keeps kiln.log open: read with sharing.</summary>
    private static string[]? BootLog(CheckContext c, string file)
    {
        var path = Path.Combine(KilnFolder(), "logs", file);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            c.Expect(false, $"kiln/logs/{file} can be read", ex.Message);
            return null;
        }
    }

    /// <summary>Which loader started first in this start, from kiln/state/boot.json: "other-first" when Kiln waited for another loader
    /// before it hooked ("waitedFor"), "kiln-first" when Kiln's proxy loaded a chain DLL ("chain", status loaded); null when neither.</summary>
    private static string? BootLoaderOrderOf(CheckContext c, out string detail, out bool published)
    {
        detail = "";
        published = false;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(KilnFolder(), "state", "boot.json")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            c.Expect(false, "kiln/state/boot.json can be read", ex.Message);
            detail = $"kiln/state/boot.json could not be read: {ex.Message}";
            return null;
        }
        using (doc)
        {
            var root = doc.RootElement;
            string[] Names(string array, string member, Func<JsonElement, bool> keep) =>
                root.TryGetProperty(array, out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Where(keep).Select(e => e.TryGetProperty(member, out var v) ? v.ToString() : "?").ToArray()
                    : [];
            var waited = Names("waitedFor", "module", _ => true);
            var chained = Names("chain", "path", e => e.TryGetProperty("status", out var s) && s.GetString() == "loaded");
            published = root.TryGetProperty("boot", out var boot) && boot.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.True;
            detail = $"waited for: {(waited.Length == 0 ? "none" : string.Join(", ", waited))}; chain DLLs loaded: " +
                     $"{(chained.Length == 0 ? "none" : string.Join(", ", chained.Select(Path.GetFileName)))}";
            return waited.Length > 0 ? "other-first" : chained.Length > 0 ? "kiln-first" : null;
        }
    }

    /// <summary>Ends a check that cannot judge in this start: it says what to do, and the check ends skipped (CheckContext.Skip), so it
    /// cannot be passed from here.</summary>
    private static async Task BootCannotJudge(CheckContext c, string why, string instruction)
    {
        c.Log.Info($"client-pass: {c.Id} cannot judge in this start: {why}");
        c.Instruct(instruction);
        await c.Skip(why);
    }

    /// <summary>The part of a check that runs over several starts, kept in the mod's data folder: "&lt;part&gt; &lt;pid&gt; &lt;value&gt;
    /// &lt;UTC start time of the game process&gt;". <see cref="Generation"/> carries the check's own value (a boot generation, a loader order).</summary>
    private sealed record BootStage(int Part, int Pid, ulong Generation, DateTime Since)
    {
        /// <summary>When this game process started (UTC). With the pid it names one start: a pid alone repeats between starts under
        /// Wine. If the start time cannot be read, the default: the pid then decides alone.</summary>
        public static readonly DateTime ThisStart = ProcessStart();

        /// <summary>The stage was written in this start of the game.</summary>
        public bool SameStart => Pid == Environment.ProcessId && Since == ThisStart;

        private static DateTime ProcessStart()
        {
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                return p.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
            {
                return default;
            }
        }

        public static BootStage? Read(string file)
        {
            try
            {
                var p = File.ReadAllText(file).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (p.Length == 4 && int.TryParse(p[0], out var part) && int.TryParse(p[1], out var pid) && ulong.TryParse(p[2], out var value) &&
                    DateTime.TryParse(p[3], null, System.Globalization.DateTimeStyles.RoundtripKind, out var since))
                    return new BootStage(part, pid, value, since);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return null;
        }

        public void Write(string file)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"{Part} {Pid} {Generation} {Since:O}");
        }

        public static void Clear(string file)
        {
            try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
