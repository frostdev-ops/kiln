// SPDX-License-Identifier: MIT
// Kiln.Quests/Native test mod (docs/kiln/services/npcs.md). Player-free.
//
// Two modes, told apart by the boot build's manifest:
//   Without its data (the lab's copy of quests/ renamed): the negative test. New ids are unknown to Kiln and dropped by
//   the game's own apply; after a run with the mod, the ids are gone from the world map (the save loader's sanitizing).
//   With its data: everything else.
//
// Runs are driven by <data>/state.txt, like quests-test: run 1 grants and places, run 2 (after a restart) checks that
// the save kept them; a run without the data checks they are gone; the next run with it checks what came back. Then the world is
// used up: copy the save back and delete <data>/state.txt to start over.
using Kiln;

namespace QuestNativeTest;

public sealed class QuestNativeTestMod : Mod
{
    private const string ModId = "quest-native-test";
    private static readonly Guid CloneGuid = new("5d2c0a1e-6b57-4c3f-9c11-7f1a2e3b4c5d"), FreeGuid = new("8e4f1b2a-3c6d-4e7f-a0b1-2c3d4e5f6a7b");
    private int _pass, _fail;
    private string _statePath = "";

    private void Pass(string what) { _pass++; Log.Info($"quest-native-test: PASS {what}"); }
    private void Fail(string what, string why) { _fail++; Log.Error($"quest-native-test: FAIL {what}: {why}"); }
    private void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why); }
    private void Note(string s) => Log.Info($"quest-native-test: {s}");

    public override void OnLoad()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        _statePath = Path.Combine(Context.DataDirectory, "state.txt");
        Events.On<KnowledgeChangedEvent>(e => { if (NativeQuests.Mod(ModId)?.Knowledge.Values.Contains(e.Id) == true) Note($"changed: {e}"); });
        Events.On<DialogueLineEvent>(e => Note($"dialogue: {e}"));
        Events.On<ServerStartedEvent>(ev => { _ = Run(); });
        Quests.RegisterAction("lab.off_tick", async _ =>
        {
            await Task.Delay(30).ConfigureAwait(false);  // what Items.Take does to whoever awaits it
            _offTick = !Game.IsTickThread;
        });
    }

    private bool _offTick;
    private static readonly QuestHolder Alice = QuestHolder.Synthetic("native-alice");

    private async Task WaitSteps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task WaitApplying()
    {
        for (var i = 0; i < 2000 && Knowledge.Status?.Applying != true; i++) await Context.Scheduler.NextTick();
    }

    private async Task Run()
    {
        try
        {
            await WaitApplying();
            await WaitSteps(5);
            var m = NativeQuests.Mod(ModId);
            Note($"manifest: {(m is null ? "no data for this mod" : $"ok {m.Ok}, {m.Knowledge.Count} knowledge, {m.Quests.Count} quests, {m.Lines.Count} lines, {m.Npcs.Count} NPCs, {m.Markers.Count} markers")}");
            Note($"native status: {NativeQuests.Counters}");
            if (m is null) await Negative();
            else if (!m.Ok) Fail("the boot build", string.Join("; ", m.Errors));
            else await Positive(m);
        }
        catch (Exception ex)
        {
            Fail("exception", ex.ToString());
        }
        Note($"done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- the negative test -----------------------------------------------------------------------------------------------

    private async Task Negative()
    {
        var world = NativeIds.Knowledge(ModId, "lab_world_bool");
        Note($"negative test: id 0x{world:x8} ({ModId}:lab_world_bool), no rebuilt resource");
        var st = Knowledge.Status!;
        Check(st.WorldObjects == 5825 && st.PlayerObjects == 10744, $"the game's maps are unchanged: {st.WorldObjects} + {st.PlayerObjects}", st.ToString());
        Check(Knowledge.Info(world) is null, "Knowledge.Info: the game's static maps do not have it");
        var refused = false;
        try { await Knowledge.World.Grant(world); }
        catch (ArgumentException ex) { refused = true; Note($"Kiln refuses: {ex.Message}"); }
        Check(refused, "Kiln's grant refuses an id the game does not know");
        var r = await Knowledge.GrantUnchecked(EntityId.None, world);
        Note($"unchecked grant through the game's apply: {r}");
        Check(r.Ok && r.After == 0 && !r.Changed, "the game's own apply drops an unknown id: value stays 0", r.ToString());
        Check(Knowledge.World.List().All(e => e.Id != world), "the id is not in the world map after the game's apply");
        // After a run with the mod: its ids in the save must be gone now.
        var saved = NativeIds.Knowledge(ModId, "lab_clone_unlock");
        if (File.Exists(_statePath))
        {
            Note($"state file says the mod ran before: {File.ReadAllText(_statePath).Trim()}");
            Check(Knowledge.World.List().All(e => e.Id != saved && e.Id != world),
                "a restart without the mod: its world ids are gone from the map (the world map only has static objects)");
            // the clone and the giver: what became of a saved entity whose template is gone
            var left = Spawn.Find(CloneGuid);
            Check(left.Count == 0, "a restart without the mod: no entity of the clone template", $"{left.Count}");
            var giver = Spawn.FindByName("Townsfolk_Male_villager_04_AntonGodwin");
            Note($"the vanilla giver stays (its template is the game's): {giver.Count} in the world");
            File.WriteAllText(_statePath, "removed 0");
        }
    }

    // ---- with the data -------------------------------------------------------------------------------------------------------

    private async Task Positive(NativeModInfo m)
    {
        if (File.Exists(_statePath) && File.ReadAllText(_statePath).StartsWith("removed"))
        {
            await AfterRemoval(m);
            return;
        }
        if (File.Exists(_statePath) && File.ReadAllText(_statePath).StartsWith("dirty"))
        {
            Note("this world has been through the whole sequence: copy the save back and delete state.txt to repeat it");
            return;
        }
        var run = File.Exists(_statePath) ? int.Parse(File.ReadAllText(_statePath).Trim().Split(' ')[1]) + 1 : 1;
        File.WriteAllText(_statePath, $"run {run}");
        Note($"run {run}");
        var st = Knowledge.Status!;
        Check(st.WorldObjects > 5825 && st.PlayerObjects > 10744, $"the rebuilt maps are live: {st.WorldObjects} + {st.PlayerObjects} objects", st.ToString());
        foreach (var (name, world, player) in new[] { ("lab_world_bool", true, false), ("lab_player_bool", false, true), ("lab_both_bool", true, true) })
        {
            var id = m.Knowledge[name];
            var i = Knowledge.Info(id);
            Check(i is { } info && info.IsWorld == world && info.IsPlayer == player && (info.Flags.HasFlag(KnowledgeObjectFlags.Twin) == (world && player)), $"{name} (0x{id:x8}) is where it belongs: {i}");
            Check(id == NativeIds.Knowledge(ModId, name), $"{name}: the managed id rule equals the native one");
        }
        var resolved = Knowledge.Resolve($"{ModId}:lab_world_bool");
        Check(resolved.Id == m.Knowledge["lab_world_bool"], $"Knowledge.Resolve(\"{ModId}:lab_world_bool\")");
        Check(Knowledge.Info(0xff6000e2)?.IsWorld == true, "the game's objects are still found (the Bard's unlock)");
        Check(Knowledge.Catalog(KnowledgeCatalog.Npcs).Count >= 35, $"the catalog reads the rebuilt roster: {Knowledge.Catalog(KnowledgeCatalog.Npcs).Count} NPCs");
        await Persist(m, run);
        CheckJournal(m);
        await CheckMirror(m, run);
        CheckDialogueData(m);
        await CheckNpcs(m, run);
        CheckSample();
        await CheckVillagers(run);
        if (run == 1)
        {
            await CheckOptionMirror(m);
            await CheckCounter(m);
            await CheckMomentOnScreen();
            await CheckHandIn(m);
            CheckConditions(m);
            await CheckPlaces(m);
            await CheckHunt(m);
            await CheckWarden();
        }
    }

    // ---- the game's UI as the default ---------------------------------------------------------------------------------------

    private NativeComputed ComputedOf(NativeModInfo m, string kind, string target, string node = "", int index = -1) =>
        m.Computed.First(c => c.Kind == kind && c.Target == target && (node.Length == 0 || c.Node == node) && (index < 0 || c.Index == index));

    private uint Want(string quest, QuestHolder h, string name) => NativeQuests.Wanted(quest, h).First(w => w.Name == name).Value;

    // Lab human, 00:02: completing through a line of the menu left the journal open and the line on offer. Complete through the
    // same path, assert the knowledge the mirror writes and the game's evaluator on the line's own query; reset; do it again.
    private async Task CheckOptionMirror(NativeModInfo m)
    {
        var full = $"{ModId}:lab_option_quest";
        var q = m.Quests.First(x => x.FullId == full);
        Check(Quests.Find(full)?.Journal == QuestJournalMode.Native && NativeQuests.ShownByGame(Quests.Find(full)!), "a quest with no journal key is in the game's journal");
        var clone = Content.Templates.Get("Kiln_Test_FreeClone");
        var requirement = clone?.GetJson("NpcDialog.dialogResource.dialogOptions[0].knowledgeRequirement.knowledgeOrQueryId");
        Check(Has(requirement, q.Steps[0].Active), $"the line's one query is the step's active knowledge 0x{q.Steps[0].Active:x8}", requirement?.ToJsonString() ?? "");
        var lineQuery = KnowledgeQuery.IsSet(q.Steps[0].Active);
        for (var round = 1; round <= 2; round++)
        {
            Check(Quests.Start(full, QuestHolder.World).Ok, $"round {round}: started");
            await WaitSteps(8);
            Check(Knowledge.World.Has(q.Active) && Knowledge.World.Has(q.Steps[0].Visible), $"round {round}: the mirror shows it");
            Check(Knowledge.Query(EntityId.None, lineQuery), $"round {round}: the game's evaluator: the line is on offer");
            _offTick = false;
            var r = await Dialogues.ApplyOption(new EntityId(4000000 + (uint)round), EntityId.None, $"{ModId}:lab_free", "hi", 0);
            Check(_offTick, $"round {round}: the action before the advance really ended off the tick thread");
            Check(Game.IsTickThread, $"round {round}: ApplyOption came back on the tick thread");
            Check(r.Ok, $"round {round}: the option ran", r.Reason);
            Check(Quests.StatusOf(full, QuestHolder.World) == QuestStatus.Done, $"round {round}: the engine finished it");
            await WaitSteps(8);
            Check(Knowledge.World.Has(q.Done) && !Knowledge.World.Has(q.Active) && !Knowledge.World.Has(q.Steps[0].Active) && Knowledge.World.Has(q.Steps[0].Done),
                $"round {round}: the mirror wrote done, and cleared active");
            Check(!Knowledge.Query(EntityId.None, lineQuery), $"round {round}: the game's evaluator: the line's query went false");
            var j = Journal.State(EntityId.None, Journal.Find(q.EntryId)!);
            Check(j.Steps[0].Done == true, $"round {round}: the game's evaluator: the journal step is done");
            var again = await Dialogues.ApplyOption(new EntityId(4000000 + (uint)round), EntityId.None, $"{ModId}:lab_free", "hi", 0);
            Check(!again.Ok, $"round {round}: a second pick is refused: {again.Reason}");
            var reset = Quests.Reset(full, QuestHolder.World);
            Check(reset.Ok, $"round {round}: reset");
            await WaitSteps(8);
            Check(!Knowledge.World.Has(q.Started) && !Knowledge.World.Has(q.Done) && !Knowledge.World.Has(q.Steps[0].Visible) && !Knowledge.World.Has(q.Steps[0].Done),
                $"round {round}: a reset clears the mirror");
        }
    }

    // The journal's counter: a Counter object the journal reads as value / total (journal_ui, docs/re/quests.md).
    private async Task CheckCounter(NativeModInfo m)
    {
        var full = $"{ModId}:lab_count_quest";
        var q = m.Quests.First(x => x.FullId == full);
        var s = q.Steps[0];
        Check(s.Count != 0 && s.Total == 3 && q.Steps[1].Count == 0, $"a step with a count of 3 has a counter of 3: {s.Total}; one thing to do has none");
        Quests.Start(full, QuestHolder.World);
        await WaitSteps(8);
        Check(Knowledge.World.Get(s.Count)?.Value == 0, "counter 0/3");
        Quests.Signal("lab_count", QuestHolder.World, 2);
        Check(Want(full, QuestHolder.World, "three.count") == 2, "the mirror wants 2/3");
        await WaitSteps(8);
        Check(Knowledge.World.Get(s.Count)?.Value == 2, $"counter 2/3: {Knowledge.World.Get(s.Count)}");
        Check(Knowledge.Info(s.Count)?.ToString().Contains("Counter", StringComparison.Ordinal) == true, $"it is a Counter object: {Knowledge.Info(s.Count)}");
        Quests.Signal("lab_count", QuestHolder.World, 1);
        await WaitSteps(8);
        Check(Knowledge.World.Get(s.Count)?.Value == 3 && Knowledge.World.Has(s.Done) && Knowledge.World.Has(q.Steps[1].Active), "3/3: the step is done, the next one active");
        Quests.Reset(full, QuestHolder.World);
        await WaitSteps(8);
        Check((Knowledge.World.Get(s.Count)?.Value ?? 0) == 0 && !Knowledge.World.Has(s.Done), "a reset takes the counter back to 0");
    }

    // Lab human, 23:57: Collect went 0 -> 3/3 and passed at once for a player already holding the twigs. A step already fulfilled stays open a moment.
    private async Task CheckMomentOnScreen()
    {
        var def = Quests.Define(new QuestDefinition
        {
            Id = "lab_hold", Name = "Hold", Scope = QuestScope.World,
            Steps =
            [
                new QuestStep { Id = "full", Text = "Already fulfilled", Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Custom, Count = 3, Progress = _ => 3 }] },
                new QuestStep { Id = "after", Text = "After", Objectives = [QuestObjective.Custom("lab_never")] },
            ],
        });
        Quests.Reset(def.FullId, QuestHolder.World);
        var started = Environment.TickCount64;
        Check(Quests.Start(def.FullId, QuestHolder.World).Ok, "a step that is already fulfilled: started");
        Check(await WaitFor(() => Quests.Progress(def.FullId, QuestHolder.World)?.Counter() == 3, 100), "its counter fills at the next poll");
        Check(Quests.Progress(def.FullId, QuestHolder.World)?.StepId == "full", $"and it is still on screen after {Environment.TickCount64 - started} ms");
        var moved = await WaitFor(() => Quests.Progress(def.FullId, QuestHolder.World)?.StepId == "after", 250);
        var took = Environment.TickCount64 - started;
        Check(moved && took >= 3000, $"it passes after the moment: {took} ms");
        Quests.Reset(def.FullId, QuestHolder.World);
    }

    // A hand-in is on offer only with the items, and refused without them. The player's mirror needs a player: what it should write is
    // asserted here (NativeQuests.Wanted, Holds), the writing itself by the world quests above, through the same code.
    private async Task CheckHandIn(NativeModInfo m)
    {
        const string logs = "Material_T1_Wood_Logs";
        var full = $"{ModId}:lab_handin";
        var q = m.Quests.First(x => x.FullId == full);
        var s = q.Steps[0];
        Check(s.Ready != 0 && s.Count == 0, "a hand-in step has a ready object and no counter");
        var ready = ComputedOf(m, "ready", $"{full}.give");
        Check(ready.Id == s.Ready, "the engine keeps it");
        var requirement = Content.Templates.Get("Kiln_Test_FreeClone")?.GetJson("NpcDialog.dialogResource.dialogOptions[1].knowledgeRequirement.knowledgeOrQueryId");
        Check(Has(requirement, s.Ready), $"the hand-in line's one query is that object 0x{s.Ready:x8}, not the step's active", requirement?.ToJsonString() ?? "");
        for (var round = 1; round <= 2; round++)
        {
            QuestInput.SetInventory(Alice, logs, 0);
            Check(Quests.Start(full, Alice).Ok, $"round {round}: started");
            Check(Want(full, Alice, "give.active") == 1, $"round {round}: the step is active");
            Check(!NativeQuests.Holds(ready, Alice), $"round {round}: no logs: the line is not on offer");
            var refused = await Quests.Advance(full, Alice, "give");
            Check(!refused.Ok && Quests.Progress(full, Alice)?.StepId == "give", $"round {round}: an old pick without the logs is refused and stays on the step: {refused.Reason}");
            var viaLine = await Dialogues.ApplyOption(new EntityId(4100000), EntityId.None, $"{ModId}:lab_free", "hi", 1);
            Check(!viaLine.Ok, $"round {round}: the menu's path refuses it too: {viaLine.Reason}");
            QuestInput.SetInventory(Alice, logs, 1);
            Check(!NativeQuests.Holds(ready, Alice), $"round {round}: one log of two: not on offer");
            QuestInput.SetInventory(Alice, logs, 2);
            Check(NativeQuests.Holds(ready, Alice), $"round {round}: two logs: on offer");
            var ok = await Quests.Advance(full, Alice, "give");
            Check(ok.Ok && Quests.StatusOf(full, Alice) == QuestStatus.Done, $"round {round}: handed in: {ok}");
            Check(QuestInput_Logs(Alice) == 0, $"round {round}: the logs are gone");
            Check(Want(full, Alice, "done") == 1 && Want(full, Alice, "active") == 0 && Want(full, Alice, "give.active") == 0 && Want(full, Alice, "give.done") == 1,
                $"round {round}: the mirror wants it done, the step's line off");
            Check(!NativeQuests.Holds(ready, Alice), $"round {round}: done: not on offer, whatever the player holds");
            Check(Quests.Reset(full, Alice).Ok, $"round {round}: reset");
            Check(NativeQuests.Wanted(full, Alice).All(w => w.Value == 0), $"round {round}: after a reset the mirror wants nothing set");
        }
    }

    private static int QuestInput_Logs(QuestHolder h) => Quests.Evaluate(QuestCondition.HasItem("Material_T1_Wood_Logs"), new QuestContext(h, null, EntityId.None, ModId)) ? 1 : 0;

    // What one knowledge check cannot say takes a knowledge object the engine keeps.
    private void CheckConditions(NativeModInfo m)
    {
        var dlg = $"{ModId}:lab_free";
        var both = ComputedOf(m, "if", dlg, "hi", 2);
        var requirement = Content.Templates.Get("Kiln_Test_FreeClone")?.GetJson("NpcDialog.dialogResource.dialogOptions[2].knowledgeRequirement.knowledgeOrQueryId");
        Check(Has(requirement, both.Id), $"a line with all/level/not/item has its own knowledge object 0x{both.Id:x8}", requirement?.ToJsonString() ?? "");
        QuestInput.SetLevel(Alice, 1);
        QuestInput.SetInventory(Alice, "Material_T1_Wood_Logs", 0);
        Check(!NativeQuests.Holds(both, Alice), "level 1: false");
        QuestInput.SetLevel(Alice, 5);
        Check(NativeQuests.Holds(both, Alice), "level 5, no logs: true");
        QuestInput.SetInventory(Alice, "Material_T1_Wood_Logs", 1);
        Check(!NativeQuests.Holds(both, Alice), "level 5, a log: false");
        QuestInput.SetInventory(Alice, "Material_T1_Wood_Logs", 0);
        var gated = ComputedOf(m, "if", dlg, "more", 0);
        Check(gated.Gate != 0, "a later node's option with an if has its gate and its if in one object");
        Check(!NativeQuests.Holds(gated, Alice), "without the gate: false");
        var after = ComputedOf(m, "if", dlg, "hi", 3);
        Check(!NativeQuests.Holds(after, Alice), "available with a requirement: false before it holds");
        var full = $"{ModId}:lab_handin";
        Quests.Complete(full, Alice);
        Check(NativeQuests.Holds(after, Alice), "available with a requirement: true once it holds");
        Quests.Reset(full, Alice);
    }

    private (bool Ok, string Why) OnGroundOutsideBases(WorldPosition p)
    {
        foreach (var b in Build.Bases)
            if (Math.Abs(p.X - b.Center.X) <= b.Half.X && Math.Abs(p.Z - b.Center.Z) <= b.Half.Z) return (false, $"{p} is inside the base at {b.Center}");
        var box = Build.BoxBetween(new WorldPosition(p.X, p.Y - 3, p.Z), new WorldPosition(p.X, p.Y + 3, p.Z));
        if (!Build.TryRead(box, out var cells)) return (false, "the tile is not loaded");
        var surface = -1;
        for (var y = 0; y < box.SizeY - 1; y++)
            if (cells[y * box.SizeX].Fill > 127 && cells[(y + 1) * box.SizeX].Fill <= 127) surface = y;
        if (surface < 0) return (false, $"no surface within 3 m of {p}");
        var top = Build.CornerOf(new CellPos(box.X, box.Y + surface + 1, box.Z)).Y;
        return (Math.Abs(top - p.Y) < 1.5, $"{p}: the voxel surface is at {top:F1}");
    }

    private async Task CheckPlaces(NativeModInfo m)
    {
        var bases = Build.Bases;
        Check(bases.Count > 0, $"the lab's world has bases: {string.Join(", ", bases.Select(b => $"{b.Center} half {b.Half.X:F0}"))}");
        var n = m.Npcs.First(x => x.Id == "lab_placed");
        Check(await WaitFor(() => Spawn.Find(n.Guid).Count == 1, 600), $"an NPC placed in the middle of the base is in the world: {Spawn.Find(n.Guid).Count}");
        var e = Spawn.Find(n.Guid).FirstOrDefault();
        if (Spawn.Inspect(e) is not { } info)
        {
            Fail("the placed NPC", "gone");
            return;
        }
        var g = OnGroundOutsideBases(info.Position);
        Check(g.Ok, $"it stands on the ground outside the base: {g.Why}", g.Why);
        await WaitSteps(150);
        Check(Spawn.Exists(e), "it is still there after 6 s");
    }

    private async Task CheckHunt(NativeModInfo m)
    {
        var full = $"{ModId}:lab_hunt";
        var q = m.Quests.First(x => x.FullId == full);
        var marker = m.Markers.First(k => k.Quest == "lab_hunt");
        Check(Quests.Start(full, QuestHolder.World).Ok, "the hunt started");
        Check(await WaitFor(() => Quests.SpawnedBy(full, "kill", QuestHolder.World).Count == 2, 900), $"two Vukah spawned: {Quests.SpawnedBy(full, "kill", QuestHolder.World).Count}");
        var site = Quests.PlaceOf(full, "kill");
        Check(site is not null, $"the step has a place: {site}");
        if (site is null) return;
        var g = OnGroundOutsideBases(site.Value);
        Check(g.Ok, $"not in the base, on the voxel surface: {g.Why}", g.Why);
        var margin = Build.Bases.Min(b => Math.Max(Math.Abs(site.Value.X - b.Center.X) - b.Half.X, Math.Abs(site.Value.Z - b.Center.Z) - b.Half.Z));
        Check(margin >= 20, $"at least 20 m from every base zone: {margin:F0} m");
        foreach (var v in Quests.SpawnedBy(full, "kill", QuestHolder.World))
        {
            var gv = Spawn.Inspect(v) is { } vi ? OnGroundOutsideBases(vi.Position) : (false, "gone");
            Check(gv.Item1, $"Vukah {v}: {gv.Item2}", gv.Item2);
        }
        await WaitSteps(100);
        var markers = Spawn.Find(marker.Guid);
        Check(markers.Count == 1 && Spawn.Inspect(markers[0])?.Position.DistanceTo(site.Value) < 2, $"the step's map marker stands there: {markers.Count} at {(markers.Count > 0 ? Spawn.Inspect(markers[0])?.Position : null)}");
        var step = Journal.Find(q.EntryId)?.Steps[0];
        var tag = Content.Templates.Get(marker.Template)?.GetJson("MapMarker.markerId")?.ToJsonString() ?? "";
        Note($"the journal step's marker tag 0x{step?.MapMarkerTag:x8}, the template's {tag}");
        Check(step?.MapMarkerTag == NativeIds.Fnv(NativeIds.Derive(ModId, "markertag", "lab_hunt.kill")), "the journal's step names that marker");
        Check(Knowledge.World.Has(q.Steps[0].Active), "and the marker's query holds while the step is active");
        await WaitSteps(100);
        var alive = Quests.SpawnedBy(full, "kill", QuestHolder.World);
        Check(alive.Count == 2 && alive.All(Spawn.Exists), "both are still there after 8 s: verified");

        // the game removes them: logged, and spawned again further out
        var gone = alive[0];
        Spawn.Destroy(gone);
        Check(await WaitFor(() => Quests.SpawnedBy(full, "kill", QuestHolder.World).Count == 2 && !Quests.SpawnedBy(full, "kill", QuestHolder.World).Contains(gone), 900),
            "one removed: a new one comes");
        var site2 = Quests.PlaceOf(full, "kill");
        Check(site2 is { } s2 && s2.DistanceTo(site.Value) > 1 && OnGroundOutsideBases(s2).Ok, $"somewhere else, on the ground: {site2}");
        await WaitSteps(100);
        var moved = Spawn.Find(marker.Guid);
        Check(moved.Count == 1 && site2 is { } s3 && Spawn.Inspect(moved[0])?.Position.DistanceTo(s3) < 2, "the marker went with them");

        // and when it keeps happening, it says so and stops (retries: 2)
        for (var i = 0; i < 2 && !Quests.SpawnGaveUp(full, "kill", QuestHolder.World); i++)
        {
            foreach (var v in Quests.SpawnedBy(full, "kill", QuestHolder.World).ToArray()) Spawn.Destroy(v);
            await WaitSteps(250);
        }
        Check(await WaitFor(() => Quests.SpawnGaveUp(full, "kill", QuestHolder.World), 600), "after its retries it gives up, as an error in the log");
        Quests.Reset(full, QuestHolder.World);
        await WaitSteps(8);
        Check(Quests.SpawnedBy(full, "kill", QuestHolder.World).Count == 0, "a reset ends it");
    }

    // The sample's Warden waits for a player; the lab has none. What can be checked: he is built, unlocked by his "place", and the game keeps him.
    private async Task CheckWarden()
    {
        if (NativeQuests.Mod("quests-sample") is not { Ok: true } s) return;
        var w = s.Npcs.First(n => n.Id == "warden");
        Check(w.IsClone && w.Roster && w.Place is { Once: true, Unlock: true, Spec.NearPlayer: true }, $"the Warden: a clone in the roster, placed near the first player: {w}");
        var npc = Knowledge.FindNpc("Kiln_QuestsSample_Warden");
        Check(await WaitFor(() => Knowledge.FindNpc("Kiln_QuestsSample_Warden")?.Unlocked == true, 200), $"his unlock was granted for this world: {npc}");
        var spot = await QuestPlacesFor(new WorldPosition(3800, 832, 1434));
        Check(spot is not null, $"a place for him near the base: {spot}");
        if (spot is null) return;
        var e = await Spawn.Entity(w.Guid, spot.Value, 180, new SpawnOptions { Flags = SpawnFlags.NoTrack });
        await WaitSteps(100);
        Check(Spawn.Exists(e), $"the game keeps him: entity {e} after 4 s");
        var tag = Creatures.Of(e).Ai.Get("NpcDialog.dialogResource.headline");
        Check(ParseU(tag) == NativeIds.Fnv(NativeIds.Derive("quests-sample", "npcname", "warden")), $"his entity reads his own dialogue: headline {tag}", tag);
        Spawn.Destroy(e);
        Check(Quests.Find("quests-sample:warden_tally") is { Journal: QuestJournalMode.Native } t && NativeQuests.ShownByGame(t) == File.Exists(Path.Combine(Mods.All.First(x => x.Id == "quests-sample").Directory, "quests", "generated", "quests-sample.json")),
            "the quest it defines in C# is in the game's journal when its file is there");
    }

    // the lab's own use of the rule: a step's "site" around a place (as the Warden's near a player standing in the base)
    private static async Task<WorldPosition?> QuestPlacesFor(WorldPosition near)
    {
        var def = Quests.Define(new QuestDefinition
        {
            Id = "lab_site_probe", Name = "Probe", Journal = QuestJournalMode.Chat,
            Steps = [new QuestStep { Id = "a", Text = "x", Site = new QuestPlace { At = near, MinDistance = 25, MaxDistance = 45 }, Objectives = [new QuestObjective { Kind = QuestObjectiveKind.Reach, Site = true }] }],
        });
        Quests.Start(def.FullId, QuestHolder.World);
        WorldPosition? p = null;
        for (var i = 0; i < 900 && p is null; i++)
        {
            p = Quests.PlaceOf(def.FullId, "a");
            await Task.Delay(40);
        }
        Quests.Reset(def.FullId, QuestHolder.World);
        return p;
    }

    private async Task<bool> WaitFor(Func<bool> f, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            if (f()) return true;
            await Context.Scheduler.NextTick();
        }
        return f();
    }

    // The mod came back after a run without it: what the game's save lost stays lost, what the engine's sidecar knows is mirrored again.
    private async Task AfterRemoval(NativeModInfo m)
    {
        Note("the mod is back after a run without it");
        File.WriteAllText(_statePath, "dirty 0");
        Check(!Knowledge.World.Has(m.Knowledge["lab_world_bool"]), "its own knowledge stayed lost (the save has dropped it)");
        Check(Knowledge.Info(m.Knowledge["lab_world_bool"]) is not null, "the id exists again");
        Check(Spawn.Find(CloneGuid).Count == 0, "its clone stayed lost");
        Quests.EnsureStarted();
        NativeQuests.Start();
        await WaitSteps(8);
        var q = m.Quests.First(x => x.Id == "lab_world_quest");
        Check(Knowledge.World.Has(q.Done), "the world quest's state came back from the engine's sidecar (mirrored at start)");
        var r = await Knowledge.World.Grant(m.Knowledge["lab_world_bool"]);
        Check(r.Changed, "the id can be granted again");
    }

    private async Task Persist(NativeModInfo m, int run)
    {
        var world = m.Knowledge["lab_world_bool"];
        var counter = m.Knowledge["lab_world_counter"];
        var flags = m.Knowledge["lab_world_flags"];
        if (run == 1)
        {
            Check(!Knowledge.World.Has(world), "run 1: the id starts unset");
            var r = await Knowledge.World.Grant(world);
            Check(r.Ok && r.Changed && r.After == 1, $"the server accepts a grant of a Kiln id: {r}", r.ToString());
            var c = await Knowledge.World.Grant(counter, 3);
            Check(c.After == 3, $"a Kiln counter: {c}", c.ToString());
            var f = await Knowledge.World.Grant(flags, 1);
            Check(f.After == 2, $"a Kiln flags object, flag 1: {f}", f.ToString());
            var bad = false;
            try { await Knowledge.World.Grant(flags, 5); } catch (ArgumentException) { bad = true; }
            Check(bad, "a flag outside expectedFlags is refused");
        }
        else
        {
            Check(Knowledge.World.Has(world), $"run {run}: the world save kept the Kiln id 0x{world:x8}");
            Check(Knowledge.World.Get(counter)?.Value == 3, $"run {run}: the counter kept 3", $"{Knowledge.World.Get(counter)}");
            Check(Knowledge.World.Get(flags)?.Value == 2, $"run {run}: the flags kept", $"{Knowledge.World.Get(flags)}");
        }
    }

    private void CheckJournal(NativeModInfo m)
    {
        var quests = Journal.Quests();
        var all = NativeQuests.Mods.Sum(x => x.Quests.Count);
        Check(quests.Count == 188 + all, $"the journal registry lists the game's 188 + {all}: {quests.Count}");
        foreach (var q in m.Quests)
        {
            var j = Journal.Find(q.EntryId);
            Check(j is not null && j.Steps.Count == q.Steps.Count, $"journal {q.FullId}: {j?.Steps.Count} steps, type {j?.Type}, source {j?.Source}");
            if (j is null) continue;
            for (var i = 0; i < j.Steps.Count; i++)
                Check(j.Steps[i].Visible.Id == q.Steps[i].Visible && j.Steps[i].Done.Id == q.Steps[i].Done, $"journal {q.Id}.{q.Steps[i].Id}: visibility and completion name its knowledge");
        }
        Check(Journal.Find(0x8755c335)?.Steps.Count == 1, "the game's quests still read (Alchemist Needs Shelter)");
    }

    private async Task CheckMirror(NativeModInfo m, int run)
    {
        Quests.EnsureStarted();
        NativeQuests.Start();
        Quests.Reload();
        var full = $"{ModId}:lab_world_quest";
        var q = m.Quests.First(x => x.FullId == full);
        var def = Quests.Find(full);
        Check(def?.Journal == QuestJournalMode.Native, $"the engine read {full} as native");
        if (run != 1)
        {
            Check(Knowledge.World.Has(q.Done), $"run {run}: the world quest's done knowledge survived (the game's save)");
            var js = Journal.State(EntityId.None, Journal.Find(q.EntryId)!);
            Check(js.Steps[0].Done == true, $"run {run}: the game's evaluator: step done");
            return;
        }
        Check(Quests.StatusOf(full, QuestHolder.World) != QuestStatus.Active, "the world quest is not active yet");
        var r = Quests.Start(full, QuestHolder.World);
        Check(r.Ok, $"engine: started {full}", r.Reason ?? "");
        await WaitSteps(8);
        Check(Knowledge.World.Has(q.Active), "mirror: active knowledge set");
        Check(Knowledge.World.Has(q.Steps[0].Visible), "mirror: first step visible");
        var j = Journal.State(EntityId.None, Journal.Find(q.EntryId)!);
        Check(j.Started && j.Steps[0].Visible == true && j.Steps[0].Done == false, $"the game's evaluator shows the step: visible {j.Steps[0].Visible}, done {j.Steps[0].Done}");
        var a = await Quests.Advance(full, QuestHolder.World, null, true);
        Check(a.Ok, $"engine: advanced {full}: {a.Reason}", a.Reason ?? "");
        await WaitSteps(8);
        Check(Knowledge.World.Has(q.Done) && !Knowledge.World.Has(q.Active), "mirror: done set, active cleared");
        j = Journal.State(EntityId.None, Journal.Find(q.EntryId)!);
        Check(j.Steps[0].Done == true, "the game's evaluator: the step is done");
    }

    private void CheckDialogueData(NativeModInfo m)
    {
        var alch = Content.Templates.Get("NPC_Workshop_Alchemist01");
        if (alch is null) { Fail("Balthazar", "no template entry"); return; }
        // appended in load order: another mod's may come first
        var line = NativeIds.Line(ModId, "balthazar_extra", "hi", 0);
        var added = alch.GetJson("NpcDialog.dialogResource.dialogOptions[33].optionId");
        if (!Has(added, line)) added = alch.GetJson("NpcDialog.dialogResource.dialogOptions[34].optionId");
        Check(Has(added, line), $"the game's template data: Balthazar has the appended option 0x{line:x8}", added?.ToJsonString() ?? "");
        var first = alch.GetJson("NpcDialog.dialogResource.dialogOptions[0].optionId");
        Check(Has(first, 1404811391), "his first option is still the game's", first?.ToJsonString() ?? "");
        var lines = m.Lines.Where(l => l.Npc == "lab_clone").ToList();
        Check(lines.Count == 5, $"the clone has its lines: {lines.Count}");
        // what the engine hands an entity: its template slot (the data createEntity took from the resource service)
        var balthazar = Spawn.FindByName("NPC_Workshop_Alchemist01");
        Check(balthazar.Count == 1, $"Balthazar is in this world: {balthazar.Count}");
        if (balthazar.Count == 1)
        {
            // the test data gives him a new headline: the tag his entity reads is the override's, so createEntity took the overridden resource
            var expected = NativeIds.Fnv(NativeIds.Derive(ModId, "npcname", "balthazar"));
            var view = Creatures.Of(balthazar[0]).Ai.Get("NpcDialog.dialogResource.headline");
            Check(ParseU(view) == expected, $"entity {balthazar[0]}'s template slot reads the overridden template: headline {view} = 0x{expected:x8}", view);
        }
    }

    private static uint ParseU(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var t = s.Trim();
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(t, 16) : uint.TryParse(t, out var v) ? v : 0;
    }

    private async Task CheckNpcs(NativeModInfo m, int run)
    {
        var unlock = m.Knowledge["lab_clone_unlock"];
        var npc = Knowledge.FindNpc("Kiln_Test_LabClone");
        Check(npc is not null && npc.NpcId == NativeIds.Fnv(CloneGuid), $"the roster has the clone: {npc}");
        Check(Spawn.Lookup("Kiln_Test_LabClone") is not null, "the engine has the clone template (served as new at boot, registered by the world's init)");
        var newEntries = NativeQuests.Mods.SelectMany(x => x.Npcs).Count(n => n.Roster);
        Check(Knowledge.Npcs.Count(n => n.DebugName.StartsWith("Kiln_")) == newEntries, $"the new roster entries: {newEntries}");
        var dialog = Content.Templates.Get("Kiln_Test_LabClone")?.GetJson("ActiveNpcState.npcId");
        Check(Has(dialog, NativeIds.Fnv(CloneGuid)), "the clone's ActiveNpcState.npcId = FNV(its GUID)", dialog?.ToJsonString() ?? "");

        if (run == 1)
        {
            // a roster NPC whose unlock does not hold: the game destroys it (unique_npc_spawns)
            Check(npc?.Unlocked == false, "the clone starts locked (NpcRegistry)", $"{npc?.Unlocked}");
            var at = new WorldPosition(3890, 834, 1490);
            var locked = await Spawn.Entity(CloneGuid, at, 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(!Spawn.Exists(locked), $"the game destroyed the locked roster NPC {locked} (uniqueness / unlock rule)");

            var g = await Knowledge.World.Grant(unlock);
            Check(g.Changed, "its unlock knowledge granted");
            await WaitSteps(10);
            npc = Knowledge.FindNpc("Kiln_Test_LabClone");
            Check(npc?.Unlocked == true, "NpcRegistry follows: unlocked", $"{npc}");

            var first = await Spawn.Entity(CloneGuid, at, 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(Spawn.Exists(first), $"the unlocked clone lives: {first}");
            Check(World.Has(first, "DynamicActiveNpcState"), "it has the villager state");
            var tag = Creatures.Of(first).Ai.Get("NpcDialog.dialogResource.headline");
            Check(ParseU(tag) == NativeIds.Fnv(NativeIds.Derive(ModId, "npcname", "lab_clone")), $"the clone entity's template slot reads the clone's data: headline {tag}", tag);
            var second = await Spawn.Entity(CloneGuid, new WorldPosition(at.X + 3, at.Y, at.Z), 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(!Spawn.Exists(second) && Spawn.Exists(first), $"one per world: the second clone {second} was destroyed, {first} stays");

            var free = await Spawn.Entity(FreeGuid, new WorldPosition(at.X + 6, at.Y, at.Z), 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(Spawn.Exists(free), $"a clone that is not in the roster lives without any unlock: {free}");
            var free2 = await Spawn.Entity(FreeGuid, new WorldPosition(at.X + 9, at.Y, at.Z), 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(!Spawn.Exists(free2) && Spawn.Exists(free), "its uniqueness holds too");
        }
        else
        {
            Check(npc?.Unlocked == true, $"run {run}: the clone is still unlocked", $"{npc}");
            Check(Spawn.Find(CloneGuid).Count == 1, $"run {run}: the saved clone came back: {Spawn.Find(CloneGuid).Count}");
        }
        var giver = m.Npcs.First(n => n.Id == "lab_giver");
        await WaitSteps(20);
        var givers = Spawn.Find(giver.Guid);
        Check(givers.Count == 1, $"the vanilla giver placed once: {givers.Count} entity in the world (run {run})");
        if (run == 1 && givers.Count == 1)
        {
            var dup = await Spawn.Entity(giver.Guid, new WorldPosition(3873, 834, 1470), 0, new SpawnOptions { Flags = SpawnFlags.NoTrack });
            await WaitSteps(30);
            Check(!Spawn.Exists(dup), $"a second giver {dup} was destroyed by the game (one entity per NpcId)");
        }
    }

    private void CheckSample()
    {
        if (NativeQuests.Mod("npcsample") is not { Ok: true } s) return;
        var tobin = s.Npcs.First(n => n.Id == "tobin");
        Check(Spawn.Lookup("Kiln_NpcSample_Tobin") is not null, "the sample's clone is a template");
        var npc = Knowledge.FindNpc("Kiln_NpcSample_Tobin");
        var tool = NewContent.Definitions().FirstOrDefault(d => d.Name == "Kiln_NpcSample_TobinTool");
        Check(tool is not null, $"the sample's summon tool is an item (kiln.content define at boot): {tool?.State}");
        if (tool is not null) Check(npc?.SummonItem == tool.Id, $"the roster names it: 0x{npc?.SummonItem:x8}");
        var placed = Content.ItemData.Get("Kiln_NpcSample_TobinTool")?.GetJson("equipment.placedEntity")?.ToJsonString() ?? "";
        Note($"its placedEntity: {placed}");
        Check(placed.Contains("299d68c5-10b9-4af9-91e0-194b308ee461"), "the tool places the clone (equipment.placedEntity)", placed);
        var trade = NewContent.Definitions().FirstOrDefault(d => d.Name == "Kiln_NpcSample_TobinTrade");
        Check(trade is not null, "the vendor recipe is defined");
        Check(tobin.NpcId == NativeIds.Fnv(tobin.Guid) && npc?.Workshop == 0xc4acc104, $"the vendor's workshop is the base's: 0x{npc?.Workshop:x8}");
    }

    private async Task CheckVillagers(int run)
    {
        Check(Villagers.Available, "Villagers: NpcCommandEvent is available");
        var v = Villagers.Find(CloneGuid);
        if (v is null) { Fail("Villagers", "the clone is not in the world"); return; }
        Note($"{v} at {v.Position}");
        var states = new List<string>();
        Events.On<VillagerStateEvent>(e => { if (e.Entity == v.Entity) { states.Add(e.ToString()); Note(e.ToString()); } });
        Villagers.Watch(v.Entity);
        var before = v.Position!.Value;
        var to = new WorldPosition(before.X + 6, before.Y, before.Z + 2);
        v.Teleport(to, 90);
        await WaitSteps(40);
        var after = v.Position!.Value;
        var d = Math.Sqrt(Math.Pow(after.X - to.X, 2) + Math.Pow(after.Z - to.Z, 2));
        Check(d < 2, $"Teleport (the game's Reset): moved to {after} ({d:F1} m from the target)");
        if (World.TryRead<SavedNpc>(v.Entity, "ActiveNpcStateSaved", out var saved))
            Check(Math.Abs(saved.X - to.X) < 0.5 && Math.Abs(saved.Z - to.Z) < 0.5, $"... and it is its home: ActiveNpcStateSaved.spawnPosition ({saved.X:F1}, {saved.Y:F1}, {saved.Z:F1})");
        else Fail("ActiveNpcStateSaved", "could not read it");
        v.GoTo(new WorldPosition(after.X + 4, after.Y, after.Z));
        await WaitSteps(40);
        // Not a check: the game ignores GoToPosition while an NPC is in state Start (0x1f3320), and without a player NPCs stay there.
        Note($"GoTo sent; state {v.State}; states seen: {string.Join(", ", states)} (walking and state events need a player: client checklist)");
        v.Stay();
        await WaitSteps(20);
        Note($"Stay sent; state {v.State}");
        Check(Villagers.List().Any(x => x.Entity == v.Entity), "Villagers.List has it");
    }

    private static bool Has(System.Text.Json.Nodes.JsonNode? n, uint id)
    {
        var s = n?.ToJsonString() ?? "";
        return s.Contains(id.ToString()) || s.Contains($"{id:x8}");
    }

    private readonly record struct SavedNpc(float X, float Y, float Z);
}
