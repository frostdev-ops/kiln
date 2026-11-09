// SPDX-License-Identifier: MIT
// World progress test mod (Kiln.Quests' WorldProgress, Quests.Of and their events), for a lab server with no player.
//  1. Reads, nothing changed: the index of the game's data (counts, ids, scopes against the game's knowledge objects),
//     the flame (and the progress entity's Level attribute next to it), the difficulty against kiln.settings, survivors
//     against the NPC registry, spires and bosses against direct knowledge reads, the loaded chests.
//  2. Simulated progress, put back afterwards: world knowledge through Knowledge.World (a spire's unlock, a boss's kill
//     record); a character's knowledge on a free player block (Knowledge.LabRecord, direct writes: a shrine, a root, a
//     spire top, a vanilla quest's first step); the flame level through the progress entity's Level attribute; a Kiln
//     quest on a synthetic holder; a spawned chest written as looted; a spawned boss killed. Each change must show in the
//     reads and raise its event.
//  3. The flame write (WorldProgress.SetFlameLevel, docs/re/world-status.md section 11): the level is written the way the
//     game's admin command writes it, read back through Flame (the stored Level attribute, the replicated copy, the
//     game's own ServerProgressSystem) and LimitsAt, then put back. Restart check: `touch kiln/data/worldstatus-test/arm-flame`
//     (or `arm-flame-raw`: the same with a raw unmarked write), run once, restart the server: the second run finds the level
//     in the world's save, checks what the game derives from it and puts it back.
//  4. Texts.Get (docs/re/world-status.md section 12): item names through the catalog's loca table against the item
//     catalog's DisplayName.
// A lab world only: part 2 writes the world's knowledge and its flame level for a few seconds and restores them.
// Output: "worldstatus-test: PASS <what>" / "FAIL <what>: <why>" lines in kiln/logs/kiln.log, then a summary.
using System.Runtime.InteropServices;
using Kiln;

namespace WorldStatusTest;

public sealed class WorldStatusTestMod : Mod
{
    private int _pass, _fail;
    private ulong _start;
    private bool _running, _done;
    private readonly List<KilnEvent> _events = new();
    private static readonly EntityId Block = new(16);  // the last player block: a lab has no players

    private void Pass(string what) { _pass++; Log.Info($"worldstatus-test: PASS {what}"); }
    private void Fail(string what, string why) { _fail++; Log.Error($"worldstatus-test: FAIL {what}: {why}"); }
    private void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why); }
    private void Note(string s) => Log.Info($"worldstatus-test: {s}");

    public override void OnLoad()
    {
        Note($"loading (knowledge {Knowledge.Available}, world {World.Available}, settings {Settings.Available})");
        void Record(KilnEvent e)
        {
            lock (_events) _events.Add(e);
            Note($"event: {e}");
        }
        Events.On<FlameLevelChangedEvent>(Record);
        Events.On<SpireUnlockedEvent>(Record);
        Events.On<ShrineFoundEvent>(Record);
        Events.On<ShroudRootDestroyedEvent>(Record);
        Events.On<BossKilledEvent>(Record);
        Events.On<ChestLootedEvent>(Record);
        Events.On<QuestStatusChangedEvent>(Record);
        Context.Tick.Pre += OnPre;
    }

    private void OnPre(in TickInfo tick)
    {
        if (_start == 0) _start = tick.Step;
        var ready = Lifecycle.IsServerRunning && tick.Step >= Lifecycle.StartedAtStep + 25 && Knowledge.Status is { Quests: > 0, Applying: true }
                    && Settings.Ready;
        if (_done || _running || !(ready || tick.Step - _start > 12000)) return;
        _running = true;
        _ = RunAll().ContinueWith(t =>
        {
            if (t.Exception is { } e) Fail("run", e.GetBaseException().ToString());
            Note($"done: {_pass} PASS, {_fail} FAIL");
            _done = true;
        }, TaskScheduler.Current);
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task<T?> WaitEvent<T>(Func<T, bool> match, int steps) where T : KilnEvent
    {
        for (var i = 0; i < steps; i++)
        {
            lock (_events)
                if (_events.OfType<T>().FirstOrDefault(match) is { } hit)
                {
                    _events.Remove(hit);
                    return hit;
                }
            await Context.Scheduler.NextTick();
        }
        return null;
    }

    private async Task RunAll()
    {
        Note($"world progress: {WorldProgress.Status}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ready = await WorldProgress.WhenReady(bosses: true, TimeSpan.FromMinutes(3));
        Check(ready, $"the game's data is indexed ({sw.Elapsed.TotalSeconds:0.0} s to wait here)", WorldProgress.Status.ToString());
        if (!ready) return;
        Note($"world progress: {WorldProgress.Status}");
        Reads();
        await Simulated();
    }

    // ---------------------------------------------------------------------------------------------------- reads ----

    [StructLayout(LayoutKind.Sequential)]
    private struct LevelAttribute
    {
        public uint RootId;
        public ushort StorageOffset;
        public byte Flags, StorageCount;
        public uint Value0, Value1;
    }

    private void Reads()
    {
        var s = WorldProgress.Status;
        Check(s.Problems.Count == 0, "the index had no problems", string.Join("; ", s.Problems));
        Check(s.Spires >= 8 && s.Shrines >= 40 && s.Roots >= 40 && s.Obelisks >= 7 && s.BossCount >= 20 && s.TrackedBosses >= 8,
            $"the lists: {s.Spires} spires, {s.Shrines} shrines, {s.Roots} roots, {s.Obelisks} obelisks, {s.BossCount} bosses ({s.TrackedBosses} with a kill record)");
        foreach (var x in WorldProgress.Spires) Note($"  {x}: unlock {x.Unlock}, top {x.TopReached}, beacon {x.Beacon}");
        foreach (var x in WorldProgress.Bosses.Where(b => b.Tracked)) Note($"  {x}: kill {x.Kill}");

        // scopes against the game's own objects
        var badScope = new List<string>();
        void Scope(ProgressFact f, string what)
        {
            if (f.Query.Type == KnowledgeQueryType.Extern) return;
            var info = Knowledge.Info(f.Id);
            var expect = info is null ? (ProgressScope?)null : info.Value.IsWorld
                ? (info.Value.Flags & KnowledgeObjectFlags.Twin) != 0 ? ProgressScope.Both : ProgressScope.World
                : ProgressScope.Character;
            if (expect != f.Scope) badScope.Add($"{what} 0x{f.Id:x8}: {f.Scope}, the game says {info}");
        }
        foreach (var x in WorldProgress.Spires)
        {
            Scope(x.Unlock, x.Id);
            if (x.TopReached is { } t) Scope(t, x.Id + ".top");
            if (x.Beacon is { } b) Scope(b, x.Id + ".beacon");
        }
        foreach (var x in WorldProgress.Shrines) Scope(x.Discovery, x.Id);
        foreach (var x in WorldProgress.ShroudRoots) Scope(x.Destruction, x.Id);
        foreach (var x in WorldProgress.Bosses) if (x.Kill is { } k) Scope(k, x.Id);
        Check(badScope.Count == 0, "every fact's scope matches the game's knowledge object", string.Join("; ", badScope.Take(5)));
        Check(WorldProgress.Shrines.All(x => x.Discovery.Scope == ProgressScope.Character), "flame shrines and sanctums are a character's");
        Check(WorldProgress.ShroudRoots.All(x => x.Destruction.Scope == ProgressScope.Character && x.SkillPoints > 0)
              && WorldProgress.ElixirWells.Count(x => x.SkillPoints == 3) * 10 >= WorldProgress.ElixirWells.Count * 8,
            $"roots are a character's, with skill points ({WorldProgress.ElixirWells.Count} well roots, {WorldProgress.ElixirWells.Count(x => x.SkillPoints == 3)} " +
            $"worth 3; others: {string.Join(", ", WorldProgress.ElixirWells.Where(x => x.SkillPoints != 3).Select(x => $"{x.Id} {x.SkillPoints}"))})");
        Check(WorldProgress.Spires.Where(x => x.Unlock.Query.Type != KnowledgeQueryType.Extern).All(x => x.Unlock.Scope == ProgressScope.Both),
            "a spire's unlock is the world's (with a twin for personal progression)");
        Check(WorldProgress.Shrines.All(x => x.Position is not null) && WorldProgress.ShroudRoots.All(x => x.Position is not null),
            "every shrine and root has a position");
        Check(WorldProgress.Shrines.All(x => x.Discovery.Holds(EntityId.None) is null), "a character's fact has no world value");
        var dragon = WorldProgress.Bosses.FirstOrDefault(b => b.Template.Contains("Fog_Dragon", StringComparison.OrdinalIgnoreCase));
        Check(dragon?.Kill is { Scope: not ProgressScope.Character }, $"a world kill record is preferred ({dragon?.Id}: {dragon?.Kill})");
        Check(WorldProgress.Spire(WorldProgress.Spires[0].Id.ToUpperInvariant()) is not null && WorldProgress.Boss(WorldProgress.Bosses[0].Template) is not null,
            "lookups by id and template, any case");

        // live reads equal direct knowledge reads
        var mism = new List<string>();
        foreach (var x in WorldProgress.Spires.Where(x => x.Unlock.Query.Type == KnowledgeQueryType.SimpleBool))
            if (x.Unlocked != (Knowledge.World.Get(x.Unlock.Id)?.Value is > 0)) mism.Add($"{x.Id}: {x.Unlocked}");
        foreach (var x in WorldProgress.Bosses.Where(b => b.Kill is { Scope: not ProgressScope.Character }))
            if (x.Killed != (Knowledge.World.Get(x.Kill!.Id)?.Value is > 0)) mism.Add($"{x.Id}: {x.Killed}");
        Check(mism.Count == 0, $"spire and boss reads equal the world's knowledge ({WorldProgress.Spires.Count(x => x.Unlocked == true)} unlocked, " +
                               $"{WorldProgress.Bosses.Count(x => x.Killed == true)} killed in this world)", string.Join("; ", mism));

        // the flame
        var f = WorldProgress.Flame;
        Check(f is not null && World.Has(f.Entity, "NetworkServerProgress"), $"the flame: {f}");
        if (f is not null && World.TryRead<LevelAttribute>(f.Entity, "Level", out var lv))
        {
            Note($"progress entity {f.Entity}: Level attribute root 0x{lv.RootId:x8} offset {lv.StorageOffset} flags {lv.Flags} count {lv.StorageCount} values {lv.Value0} {lv.Value1}");
            Check(lv.Value0 == f.Level || lv.Value1 == f.Level, "the flame level equals the progress entity's Level attribute", $"{lv.Value0}/{lv.Value1} vs {f.Level}");
        }
        Check(WorldProgress.FlameLevel == f?.Level, "FlameLevel is the flame's level");

        // difficulty
        var d = WorldProgress.Difficulty;
        Check(d is not null && d.Preset == Settings.Status.Preset && d.Settings.Count == Settings.All.Count
              && Equals(d.Settings["enemyDamageFactor"], Settings.Get("enemyDamageFactor")),
            $"the difficulty is kiln.settings': {d}, enemy damage {d?.Number("enemyDamageFactor")}");

        // survivors against the NPC registry
        var sv = WorldProgress.Survivors;
        var npcs = Knowledge.Npcs.Where(n => n.Unlock.Id != 0).ToDictionary(n => n.DebugName);
        var diff = sv.Where(x => npcs[x.Id].Unlocked is { } u && u != x.Rescued).Select(x => x.Id).ToList();
        Check(sv.Count == npcs.Count && diff.Count == 0, $"survivors: {sv.Count(x => x.Rescued == true)} of {sv.Count} rescued, as the NPC registry says",
            string.Join(", ", diff));

        // chests (a lab without players has few or none loaded: see Chests() for a spawned one)
        var loaded = World.Find("RandomLootContainer");
        var states = loaded.Select(WorldProgress.ChestOf).Where(c => c is not null).Select(c => c!).ToList();
        var placed = states.Count(c => c.LootId != 0);
        Note($"{loaded.Count} loaded loot containers; {states.Count} read as chests, {placed} matched to a placement; " +
             $"{states.Count(c => c.Opened == true)} opened, {states.Count(c => c.Looted == true)} looted");
        foreach (var c in states.Take(5)) Note($"  {c}");
        if (states.Count == 0) Note("no placed chest is loaded in this lab (no player): the live state of a placed chest is a manual check");
        else Check(placed * 10 >= states.Count * 8, "loaded chests match their placements (loot container ids)", $"{placed} of {states.Count}");
        if (states.FirstOrDefault(c => c.LootId != 0) is { } one)
        {
            var again = WorldProgress.Chest(one.LootId, one.Position);
            Check(again is { Loaded: true } && again.Entity == one.Entity, "a chest by its loot id finds the same entity", $"{again}");
            var at = WorldProgress.Chest(one.Position, 1);
            Check(at?.LootId == one.LootId, "a chest by position finds it", $"{at}");
        }
        var far = WorldProgress.Chests(new WorldPosition(7000, 900, 7000), 200);
        Check(far.Count > 0 && far.All(c => !c.Loaded && c.Looted is null), $"chests far from everything are placed but not loaded ({far.Count} within 200 m of (7000, 7000))",
            $"{far.FirstOrDefault()}");
    }

    // ------------------------------------------------------------------------------------------------ simulated ----

    private async Task Simulated()
    {
        lock (_events) _events.Clear();
        await WorldFacts();
        await CharacterFacts();
        await Flame();
        await FlameWrite();
        TextsCheck();
        await Chests();
        await BossDeath();
        await KilnQuest();
    }

    private async Task WorldFacts()
    {
        var spire = WorldProgress.Spires.FirstOrDefault(s => s.Unlock.Query.Type == KnowledgeQueryType.SimpleBool && s.Unlocked == false);
        if (spire is null)
        {
            Note("every spire is unlocked in this world: the spire event is not tested");
        }
        else
        {
            var r = await Knowledge.World.Grant(spire.Unlock.Id);
            var ev = await WaitEvent<SpireUnlockedEvent>(e => e.Spire.Id == spire.Id && e.IsWorld && !e.TopReached, 60);
            Check(r.Changed && spire.Unlocked == true && ev is not null, $"a spire's unlock reads true and raises SpireUnlockedEvent ({spire.Id})", $"{r}; {ev}");
            var back = await Knowledge.World.Revoke(spire.Unlock.Id);
            Check(back.Changed && spire.Unlocked == false, "revoked: it reads false again", $"{back}");
        }
        var boss = WorldProgress.Bosses.FirstOrDefault(b => b.Kill is { Scope: not ProgressScope.Character } && b.Killed == false);
        if (boss is null)
        {
            Note("no unkilled boss with a world record: the boss record event is not tested");
            return;
        }
        var g = await Knowledge.World.Grant(boss.Kill!.Id);
        var be = await WaitEvent<BossKilledEvent>(e => e.Boss?.Id == boss.Id, 400);
        Check(g.Changed && boss.Killed == true && be is { Recorded: true, Entity.IsNone: true },
            $"a boss's kill record reads true and raises one BossKilledEvent from the record ({boss.Id})", $"{g}; {be}");
        await Steps(200);
        Check(await WaitEvent<BossKilledEvent>(e => e.Boss?.Id == boss.Id, 1) is null, "no second event for the same kill (world and twin)");
        var gb = await Knowledge.World.Revoke(boss.Kill.Id);
        Check(gb.Changed && boss.Killed == false, "revoked: the boss reads alive again", $"{gb}");
    }

    private async Task CharacterFacts()
    {
        if (!Knowledge.LabRecord(Block, 0))
        {
            Fail("player block", "block 16 is not free");
            return;
        }
        await Steps(3);
        Check(Knowledge.LabRecord(Block, 1), "a free player block takes an empty record (the character for these checks)");
        await Steps(5);
        var who = Block;
        var shrine = WorldProgress.Shrines[0];
        var root = WorldProgress.ElixirWells.FirstOrDefault() ?? WorldProgress.ShroudRoots[0];
        var spire = WorldProgress.Spires.First(s => s.TopReached is not null);
        Check(shrine.Discovery.Holds(who) == false && root.Destruction.Holds(who) == false && spire.TopReached!.Holds(who) == false,
            "a new character has found nothing");
        var log0 = Quests.Of(who);
        // world quests show what the world's knowledge says, for every character in shared progression
        Check(log0.Vanilla.Count() >= 150, $"a new character's journal: {log0.Vanilla.Count()} vanilla quests, {log0.Vanilla.Count(q => q.Active)} active " +
                                           $"and {log0.Vanilla.Count(q => q.Done)} done by the world's knowledge");
        // a vanilla quest not started, whose first step shows by a character's own knowledge
        var quest = Journal.Quests().FirstOrDefault(q => q.Steps.Count > 1 && q.Steps[0].Visible.Type == KnowledgeQueryType.SimpleBool
                                                         && Knowledge.Info(q.Steps[0].Visible.Id) is { IsWorld: false, IsPlayer: true }
                                                         && log0.Find(q.Name) is { Status: QuestStatus.Locked });
        Check(log0.Vanilla.Count(q => q.Name is not null) >= 100, $"vanilla quests have readable names ({log0.Vanilla.Count(q => q.Name is not null)})");

        // direct writes (no XP for a block without a player); each is a knowledge change
        await Knowledge.For(who).Set(shrine.Discovery.Id, 1);
        var se = await WaitEvent<ShrineFoundEvent>(e => e.Shrine.Id == shrine.Id && e.Target == who, 60);
        Check(se is not null && shrine.Discovery.Holds(who) == true, $"a shrine found by a character reads true and raises ShrineFoundEvent ({shrine.Id})", $"{se}");
        await Knowledge.For(who).Set(root.Destruction.Id, 1);
        var re = await WaitEvent<ShroudRootDestroyedEvent>(e => e.Root.Id == root.Id && e.Target == who, 60);
        Check(re is not null && root.Destruction.Holds(who) == true, $"a root destroyed reads true and raises ShroudRootDestroyedEvent ({root.Id})", $"{re}");
        await Knowledge.For(who).Set(spire.TopReached!.Id, 1);
        var te = await WaitEvent<SpireUnlockedEvent>(e => e.Spire.Id == spire.Id && e.Target == who && e.TopReached, 60);
        Check(te is not null && spire.TopReached.Holds(who) == true, $"a spire's top reached raises SpireUnlockedEvent with TopReached ({spire.Id})", $"{te}");
        var cp = WorldProgress.For(who);
        Check(cp.ShrinesFound.Count == 1 && cp.RootsDestroyed.Count == 1 && cp.SpireTopsReached.Count == 1, "the character's progress lists them",
            $"{cp.ShrinesFound.Count} {cp.RootsDestroyed.Count} {cp.SpireTopsReached.Count}");

        if (quest is null) Fail("vanilla quest", "no quest whose first step shows by a character's own knowledge");
        else
        {
            // the first change after a character appears is its baseline: wait it out, then start the quest
            await Steps(WorldProgressPumpBaseline + 30);
            await Knowledge.For(who).Set(quest.Steps[0].Visible.Id, 1);
            var qe = await WaitEvent<QuestStatusChangedEvent>(e => e.Quest.Id == quest.Name && e.PlayerEntity == who, 120);
            var entry = Quests.Of(who).Find(quest.Name);
            Check(qe is { OldStatus: QuestStatus.Locked, NewStatus: QuestStatus.Active, NewStep: 0 } && entry is { Status: QuestStatus.Active, Step: 0 },
                $"a vanilla quest started for a character reads Active and raises QuestStatusChangedEvent ({entry})", $"{qe}");
            Check(entry is not null && (entry.Name is null || Quests.Of(who).Find(entry.Name) == entry), "the quest is found by its name too", $"{entry?.Name}");
        }
        Knowledge.LabRecord(Block, 2);
        await Steps(2);
    }

    private const int WorldProgressPumpBaseline = 600;

    private async Task Flame()
    {
        if (WorldProgress.Flame is not { } f || !World.TryRead<LevelAttribute>(f.Entity, "Level", out var lv))
        {
            Fail("flame", "no progress entity or Level attribute");
            return;
        }
        // the progress entity's Level attribute is what the game's set-flame-level writes; the game mirrors it into
        // NetworkServerProgress. Raised by one for a few seconds, then put back.
        var offset = lv.Value0 == f.Level ? 8 : 12;
        var target = (uint)f.Level + 1;
        Check(World.TryWrite(f.Entity, "Level", offset, target), $"the Level attribute takes {target} (offset {offset})");
        var fe = await WaitEvent<FlameLevelChangedEvent>(e => e.NewLevel == (int)target, 240);
        Check(fe is { OldLevel: var o } && o == f.Level && WorldProgress.FlameLevel == (int)target,
            "the game mirrors it into NetworkServerProgress and FlameLevelChangedEvent fires", $"{fe}; now {WorldProgress.Flame}");
        lock (_events) _events.RemoveAll(e => e is FlameLevelChangedEvent);
        World.TryWrite(f.Entity, "Level", offset, (uint)f.Level);
        var back = await WaitEvent<FlameLevelChangedEvent>(e => e.NewLevel == f.Level, 240);
        Check(back is not null && WorldProgress.FlameLevel == f.Level, "put back", $"{WorldProgress.Flame}");
    }

    // ------------------------------------------------------------------------------------------- the flame write ----

    private string StateFile => Path.Combine(Context.DataDirectory, "flame-restart.json");

    private async Task<bool> WaitFlame(Func<FlameStatus, bool> done, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            if (WorldProgress.Flame is { } f && done(f)) return true;
            await Context.Scheduler.NextTick();
        }
        return false;
    }

    private void Limits(string what, int level)
    {
        var l = WorldProgress.LimitsAt(level);
        Note($"{what}: {(l is null ? $"no limits for level {level}" : l.ToString())}");
    }

    private async Task FlameWrite()
    {
        if (WorldProgress.Flame is not { } f0 || f0.Stored is null)
        {
            Fail("flame write", $"the flame part of kiln.knowledge is not answering: {WorldProgress.Flame}");
            return;
        }
        var max = f0.Max ?? 0;
        Note($"flame before: {f0}; stored {f0.Stored}, max {f0.Max}, system level {f0.SystemLevel}");
        Check(f0.Stored == f0.Level, "the stored Level attribute equals the replicated copy", $"{f0.Stored} vs {f0.Level}");
        Check(max >= 2, $"the Level attribute's second value is the game's maximum ({max})", $"{max}");
        Check(WorldProgress.LimitsAt(max) is not null && WorldProgress.LimitsAt(max + 1) is null,
            $"the balancing table's flameLevelMax equals it (limits exist for 0..{max} and not above)");
        Check(f0.SystemLevel == f0.Level, "the game's own ServerProgressSystem.progressLevel equals it (what altar and fog rules read)",
            $"{f0.SystemLevel}");
        Limits("limits at the current level", f0.Level);
        if (Settings.Available && Settings.Values.TryGetValue("flameLevelFogRemoval", out var fog)) Note($"setting flameLevelFogRemoval = {fog}");

        // arguments
        var threw0 = false;
        try { _ = WorldProgress.SetFlameLevel(0); } catch (ArgumentOutOfRangeException) { threw0 = true; }
        var threwMax = false;
        try { _ = WorldProgress.SetFlameLevel(max + 1); } catch (ArgumentOutOfRangeException) { threwMax = true; }
        Check(threw0 && threwMax, "levels 0 and above the maximum are refused with ArgumentOutOfRangeException");

        // a state left by an armed run before a restart: this is the second run
        if (File.Exists(StateFile))
        {
            await FlameAfterRestart(f0);
            return;
        }

        var armed = File.Exists(Path.Combine(Context.DataDirectory, "arm-flame")) ? "marked" :
                    File.Exists(Path.Combine(Context.DataDirectory, "arm-flame-raw")) ? "raw" : null;

        // up one (down one at the maximum), read back everywhere, put back
        var target = f0.Level < max ? f0.Level + 1 : f0.Level - 1;
        var up = await WorldProgress.SetFlameLevel(target);
        Note($"SetFlameLevel({target}): {up}");
        Check(up.Ok && up.Before == f0.Level && up.After == target && up.Marked && !up.Clamped,
            $"SetFlameLevel({target}) wrote it: {up.Before} -> {up.After}, marked written {up.Marked}");
        Check(up.Mirrored, $"the game mirrored it into NetworkServerProgress after {up.Steps} step(s)", $"{up}");
        Check(await WaitFlame(f => f.Level == target && f.Stored == target && f.SystemLevel == target, 120),
            "Level, the stored attribute and ServerProgressSystem.progressLevel all read the new level", $"{WorldProgress.Flame}");
        var changed = await WaitEvent<FlameLevelChangedEvent>(e => e.NewLevel == target, 120);
        Check(changed is not null && changed.OldLevel == f0.Level, "FlameLevelChangedEvent fires", $"{changed}");
        Limits($"limits at the new level {target}", target);
        var a = WorldProgress.LimitsAt(f0.Level);
        var b = WorldProgress.LimitsAt(target);
        Note($"derived: altars {a?.Altars} -> {b?.Altars}, water sources {a?.WaterSources} -> {b?.WaterSources}, fog resistance {a?.FogResistance} -> {b?.FogResistance}, " +
             $"fog removed {a?.FogRemoved} -> {b?.FogRemoved}");

        if (armed is not null)
        {
            // keep a level that differs from the saved one by 3 or more, for the restart
            var keep = f0.Level + 4 <= max ? f0.Level + 4 : Math.Max(1, f0.Level - 4);
            if (armed == "marked")
            {
                var k = await WorldProgress.SetFlameLevel(keep);
                Check(k.Ok && k.Mirrored, $"armed ({armed}): level {keep} is set and mirrored", $"{k}");
            }
            else
            {
                var e = f0.Entity;
                var slotOffset = World.TryRead<LevelAttribute>(e, "Level", out var lv) && lv.Value0 == (uint)target ? 8 : 12;
                Check(World.TryWrite(e, "Level", slotOffset, (uint)keep), $"armed ({armed}): level {keep} written with a raw World.TryWrite (offset {slotOffset})");
                Check(await WaitFlame(f => f.Level == keep, 240), "armed (raw): the game mirrored it", $"{WorldProgress.Flame}");
            }
            File.WriteAllText(StateFile, System.Text.Json.JsonSerializer.Serialize(new { original = f0.Level, expected = keep, mode = armed }));
            Note($"armed ({armed}): the flame stays at {keep} (saved level was {f0.Level}). Wait for an autosave or stop the server, then start it again: the second run reads it back and restores {f0.Level}");
            return;
        }
        var back = await WorldProgress.SetFlameLevel(f0.Level);
        Check(back.Ok && back.After == f0.Level && back.Mirrored, $"put back: {back.Before} -> {back.After}", $"{back}");
        Check(await WaitFlame(f => f.Level == f0.Level && f.Stored == f0.Level && f.SystemLevel == f0.Level, 120), "all three read the original level again", $"{WorldProgress.Flame}");
    }

    private async Task FlameAfterRestart(FlameStatus f)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StateFile));
        var original = doc.RootElement.GetProperty("original").GetInt32();
        var expected = doc.RootElement.GetProperty("expected").GetInt32();
        var mode = doc.RootElement.GetProperty("mode").GetString();
        Note($"after the restart ({mode} write): flame {f}; stored {f.Stored}, system level {f.SystemLevel}; the armed run left {expected} (the level before was {original})");
        Check(f.Level == expected && f.Stored == expected,
            $"the level written before the restart ({mode}) is in the world's save: {f.Level} (stored {f.Stored}), expected {expected}", $"{f}");
        Check(f.SystemLevel == expected, "the game's own limits read it too (ServerProgressSystem.progressLevel)", $"{f.SystemLevel}");
        Limits("derived limits after the restart", f.Level);
        if (Settings.Available && Settings.Values.TryGetValue("flameLevelFogRemoval", out var fog)) Note($"setting flameLevelFogRemoval = {fog} (unchanged by the level)");
        var back = await WorldProgress.SetFlameLevel(original);
        Check(back.Ok && back.Mirrored, $"put back to {original}", $"{back}");
        File.Delete(StateFile);
        File.Delete(Path.Combine(Context.DataDirectory, "arm-flame"));
        File.Delete(Path.Combine(Context.DataDirectory, "arm-flame-raw"));
    }

    // ----------------------------------------------------------------------------------------------------- texts ----

    private void TextsCheck()
    {
        var st = Texts.Status;
        Note($"texts: {st}");
        if (!st.Loaded)
        {
            Fail("texts", st.ToString());
            return;
        }
        Check(st.Count > 10000, $"the loca table has {st.Count} texts");
        // Three known items, required individually. Walk the item registry, whose GUIDs have already been resolved by
        // kiln.items, rather than assuming every asset-directory key names an item in that registry.
        var known = 0;
        foreach (var n in new[] { "Material_T1_Wood_Logs", "Consumable_Potion_Mana_T5", "Consumable_Potion_Health_T5" })
        {
            var item = Items.Lookup(n);
            if (item?.DisplayName is not { } shown || Assets.ReadResource(new AssetKey(item.Guid, AssetKey.ItemInfoType)) is not { Length: >= 0x58 } bytes)
            {
                Fail($"Texts.Get comparison for {n}", $"item {item}, display name {item?.DisplayName ?? "missing"}, GUID {item?.Guid}");
                continue;
            }
            var tag = new Guid(bytes.AsSpan(0x48, 16));
            var text = Texts.GetPlain(tag);
            Note($"item {item.Name}: DisplayName \"{shown}\", Texts.Get(name tag {tag}) = \"{text}\"");
            known++;
            Check(text == shown, $"Texts.Get of {item.Name}'s name tag equals its DisplayName ({shown})", $"{text}");
        }
        Check(known >= 3, $"three known items were compared ({known} found)");
        var same = 0;
        var total = 0;
        var diff = new List<string>();
        foreach (var item in Items.All)
        {
            if (item.DisplayName is not { } shown ||
                Assets.ReadResource(new AssetKey(item.Guid, AssetKey.ItemInfoType)) is not { Length: >= 0x58 } bytes) continue;
            if (Texts.GetPlain(new Guid(bytes.AsSpan(0x48, 16))) is not { } text) continue;
            total++;
            if (text == shown) same++;
            else if (diff.Count < 5) diff.Add($"{item.Name}: '{shown}' vs '{text}'");
        }
        Note($"every item with both names: {same} of {total} equal; first differences: {string.Join(" | ", diff)}");
        Check(total > 3000 && same * 100 >= total * 97, $"{same} of {total} item names agree (the shipped catalog swaps a few names)");
        Check(Texts.Get(0x12345678u) is null && Texts.Get(Guid.Empty) is null && Texts.Get("not a tag") is null, "an unknown tag, the zero GUID and a bad string read null");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct EmptyState
    {
        public byte ToggleVfx, IsEmpty;
    }

    /// <summary>A chest spawned next to the world's base (the area is loaded): its state reads untouched; written as looted
    /// (loot generated, inventory empty: what a player's looting leaves) it must raise ChestLootedEvent. Destroyed afterwards.</summary>
    private async Task Chests()
    {
        if (!Spawn.Available || WorldProgress.Flame is null)
        {
            Fail("chest", "kiln.spawn or the progress entity is missing");
            return;
        }
        var bases = World.Find("BaseIdComponent");
        var at = bases.Count > 0 && World.Position(bases[0]) is { } b ? b with { X = b.X + 6 } : new WorldPosition(3806, 834, 1434);
        EntityId chest;
        try
        {
            chest = await Spawn.Entity("LootChest_Random_Cairn_Sarcophagus_Wood", at);
        }
        catch (Exception e)
        {
            Fail("chest spawn", e.Message);
            return;
        }
        await Steps(10);
        World.TryRead<uint>(chest, "RandomLootContainer", out var version);
        World.TryRead<EmptyState>(chest, "InventoryEmptyState", out var empty);
        Note($"spawned chest {chest}: spawnedLootVersion {version}, isEmpty {empty.IsEmpty}, toggleVFX {empty.ToggleVfx}");
        var s0 = WorldProgress.ChestOf(chest);
        Check(s0 is { Loaded: true, Opened: false, Looted: false, LootId: 0 }, "a chest entity reads loaded and untouched (not a placed one: no loot id)", $"{s0}");
        await Steps(70);  // the sampler has seen it untouched
        lock (_events) _events.RemoveAll(e => e is ChestLootedEvent);
        World.TryWrite(chest, "RandomLootContainer", 0, 1u);
        World.TryWrite(chest, "InventoryEmptyState", 1, (byte)1);
        var ce = await WaitEvent<ChestLootedEvent>(e => e.Chest.Entity == chest, 120);
        Check(ce is { Chest.Looted: true } && WorldProgress.ChestOf(chest) is { Looted: true }, "written as looted, it reads looted and ChestLootedEvent fires", $"{ce}");
        Spawn.Destroy(chest);
        await Steps(2);
    }

    /// <summary>A boss (a template with a boss health bar) spawned next to the base and killed: the game's death event must
    /// become a BossKilledEvent with the entity. The game reports deaths only near a player, so without one this may not
    /// happen: then it is a note, and the manual check covers it.</summary>
    private async Task BossDeath()
    {
        var at = World.Find("BaseIdComponent") is { Count: > 0 } bs && World.Position(bs[0]) is { } b ? b with { Z = b.Z + 12 } : new WorldPosition(3800, 834, 1446);
        EntityId boss;
        try
        {
            boss = await Spawn.Entity(BossTemplate, at, 0, new SpawnOptions { Tag = "worldstatus-test", Flags = SpawnFlags.Transient });
        }
        catch (Exception e)
        {
            Fail("boss spawn", e.Message);
            return;
        }
        var deaths = 0;
        using var sub = Events.On<EntityDeathEvent>(e => { if (e.Victim == boss) deaths++; });
        await Steps(20);
        lock (_events) _events.RemoveAll(e => e is BossKilledEvent);
        var hit = await Combat.Kill(boss);
        var ev = await WaitEvent<BossKilledEvent>(e => e.Entity == boss, 180);
        if (deaths == 0 && ev is null) Note($"the game reported no death for the boss without a player near ({hit}): BossKilledEvent from a death is a manual check");
        else Check(ev is { Recorded: false, Template: BossTemplate }, "a boss's death raises BossKilledEvent with its entity and template", $"{hit}; deaths {deaths}; {ev}");
        if (Spawn.Exists(boss)) Spawn.Destroy(boss);
    }

    private const string BossTemplate = "Enemy_Fogger_Heavy_unarmored_BossHealthBar";

    private async Task KilnQuest()
    {
        var q = Quests.Define(new QuestDefinition
        {
            Id = "test_quest",
            Name = "World progress test quest",
            Journal = QuestJournalMode.Chat,  // nothing for the game's journal
            Steps =
            [
                new QuestStep { Id = "one", Text = "One", Objectives = [QuestObjective.Custom("worldstatus.never")] },
                new QuestStep { Id = "two", Text = "Two", Objectives = [QuestObjective.Custom("worldstatus.never")] },
            ],
        });
        var holder = QuestHolder.Synthetic("worldstatus-test");
        Quests.Reset(q.FullId, holder);
        await Steps(2);
        lock (_events) _events.RemoveAll(e => e is QuestStatusChangedEvent);
        Check(Quests.Start(q.FullId, holder).Ok, "a Kiln quest starts on a synthetic holder");
        var a = await WaitEvent<QuestStatusChangedEvent>(e => e.Quest.Id == q.FullId && e.NewStatus == QuestStatus.Active, 30);
        Check(a is { Quest.Kind: QuestKind.Kiln, NewStep: 0 }, "QuestStatusChangedEvent: the Kiln quest is Active at step 1", $"{a}");
        var adv = await Quests.Advance(q.FullId, holder, force: true);
        var b = await WaitEvent<QuestStatusChangedEvent>(e => e.Quest.Id == q.FullId && e.NewStep == 1, 30);
        Check(adv.Ok && b is { OldStep: 0, NewStatus: QuestStatus.Active }, "an advance raises it with the step", $"{adv}; {b}");
        Check(Quests.Complete(q.FullId, holder).Ok, "completes");
        var c = await WaitEvent<QuestStatusChangedEvent>(e => e.Quest.Id == q.FullId && e.NewStatus == QuestStatus.Done, 30);
        Check(c is { OldStatus: QuestStatus.Active }, "and Done", $"{c}");
        Quests.Reset(q.FullId, holder);
    }
}
