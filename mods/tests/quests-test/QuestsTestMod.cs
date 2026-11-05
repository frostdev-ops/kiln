// SPDX-License-Identifier: MIT
// Kiln.Quests test mod, the knowledge part (kiln.knowledge, Kiln.Knowledge, Journal, events). Three parts:
//  0. Before the world runs (the game's knowledge update starts with it; maps are empty until the save's load): a grant
//     submitted now waits for the first update and reads the save's value.
//  1. Always, once the world runs (no player needed): the service and its parts; GameKnowledgeResource sizes; the catalog
//     (what the game's data holds plus what Kiln's mods built at boot, read from the game's registries and the boot build's manifest,
//     never counted here; 35 NPCs, 188 quests, 406 steps on svn 1287369): every name resolves and maps back; the
//     save's load fired no change events; the world map; the
//     NPC registry's unlock state against the world knowledge; journal states of the world through the game's
//     evaluator against direct reads; world query evaluation; dialogue-line names; refusals and argument checks;
//     settings round trip; a Counter and a Flags round trip (grant / set / revoke) on world objects.
//  2. Across restarts (the persistence check), driven by <data>/state.txt:
//     run 1 grants the unlock knowledge of a locked NPC (NPC_Workshop_Bard01 unless it is unlocked already) to the
//     world and checks the map, the change event and the NpcRegistry; run 2 (after a restart) checks it is still set,
//     then revokes it and checks map, event and registry; run 3 (after another restart) checks it stayed revoked.
//     kiln/control/quests-test.reset starts over.
//  3. Always: logs every DialogueLineEvent, KnowledgeRequestEvent and DialogueKnowledgeEvent (client checklist K3, K4).
//     kiln/control/quests-test.live with a player in game: grants and revokes the first-step knowledge of a player
//     quest for the first player (client checklist K1, K2; throwaway character only), see docs/kiln/services/knowledge.md.
// Output: "quests-test: PASS <what>" / "FAIL <what>: <why>" lines in kiln/logs/kiln.log, then a summary.
using Kiln;

namespace QuestsTest;

public sealed class QuestsTestMod : Mod
{
    private int _pass, _fail;
    private ulong _start;
    private bool _done, _running;
    private string _control = "", _statePath = "";
    private readonly List<KnowledgeChangedEvent> _changes = new();
    private Task<KnowledgeResult>? _early;
    private ulong _earlyStep;
    private bool _earlyTried;

    private void Pass(string what) { _pass++; Log.Info($"quests-test: PASS {what}"); }
    private void Fail(string what, string why) { _fail++; Log.Error($"quests-test: FAIL {what}: {why}"); }
    private void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why); }
    private void Note(string s) => Log.Info($"quests-test: {s}");

    public override void OnLoad()
    {
        Note($"loading (kiln.knowledge available: {Knowledge.Available})");
        _control = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Context.DataDirectory)!)!, "control");
        Directory.CreateDirectory(Context.DataDirectory);
        _statePath = Path.Combine(Context.DataDirectory, "state.txt");
        if (!Knowledge.Available) return;
        Events.On<KnowledgeChangedEvent>(e =>
        {
            lock (_changes) _changes.Add(e);
            Note($"changed: {e}");
        });
        Events.On<DialogueLineEvent>(e => Note($"dialogue: {e} (player {e.Player?.Name})"));
        Events.On<KnowledgeRequestEvent>(e => Note($"client knowledge: {e} name {e.Name ?? "-"}"), EventPriority.Monitor);
        Events.On<DialogueKnowledgeEvent>(e => Note($"dialogue knowledge: {e}; ops [{string.Join(", ", e.Operations)}], default " +
                                                     $"{(e.DefaultApplies ? "applies" : "dropped")}"), EventPriority.Monitor);
        Context.Tick.Pre += OnPre;
    }

    private void OnPre(in TickInfo tick)
    {
        if (_start == 0) _start = tick.Step;
        if (!_earlyTried && tick.Step - _start >= 40 && !Lifecycle.IsServerRunning)
        {
            _earlyTried = true;
            _earlyStep = tick.Step;
            Note($"before the world runs (step {tick.Step}): {Knowledge.Status}");
            _early = Knowledge.World.Grant(EarlyCounter, 1);
        }
        // The save is parsed and the game's knowledge update starts when the world runs (lab: step ~270)
        var ready = Lifecycle.IsServerRunning && tick.Step >= Lifecycle.StartedAtStep + 25 && Knowledge.Status is { Quests: > 0, Applying: true };
        if (!_done && !_running && (ready || tick.Step - _start > 12000))
        {
            _running = true;
            _ = RunAll().ContinueWith(t =>
            {
                if (t.Exception is { } e) Fail("run", e.GetBaseException().ToString());
                Note($"done: {_pass} PASS, {_fail} FAIL");
                _done = true;
                _running = false;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
        if (_done && !_running && tick.Step % 25 == 0)
        {
            var reset = Path.Combine(_control, "quests-test.reset");
            if (File.Exists(reset))
            {
                TryDelete(reset);
                TryDelete(_statePath);
                Note("state reset: the next start begins with the grant run");
            }
            var live = Path.Combine(_control, "quests-test.live");
            if (File.Exists(live))
            {
                var arg = SafeRead(live).Trim();
                TryDelete(live);
                _running = true;
                _ = RunLive(arg).ContinueWith(t =>
                {
                    if (t.Exception is { } e) Fail("live run", e.GetBaseException().ToString());
                    Note($"live done: {_pass} PASS, {_fail} FAIL");
                    _running = false;
                }, TaskContinuationOptions.ExecuteSynchronously);
            }
        }
    }

    // The world Counter of the editor's query "8k_test_counter" (see FindWorldObject): +1 from 0 fires nothing.
    private const uint EarlyCounter = 0xc07ee179;

    private async Task EarlyGrant()
    {
        if (_early is null)
        {
            Note("early grant: the world was already running at step 40 (not tested)");
            return;
        }
        var r = await _early;
        Check(r.Ok && r.Changed && r.After == r.Before + 1 && r.Steps > (int)KnowledgeApplyWaitSteps,
            $"a grant submitted while the world loads (step {_earlyStep}) waited {r.Steps} steps for the first knowledge update", r.ToString());
        var back = await Knowledge.World.Set(EarlyCounter, r.Before);
        Check(back.Ok && back.After == r.Before, $"early grant: Counter set back to {r.Before}", back.ToString());
    }

    private const ulong KnowledgeApplyWaitSteps = 50;

    private async Task RunAll()
    {
        try
        {
            lock (_changes)
            {
                var unasked = _changes.Count(c => !c.ByKiln);
                Check(unasked < 20, $"the save's load fired no change events ({unasked} game changes so far, {Knowledge.Status?.WorldEntries} entries in the map)");
            }
            await EarlyGrant();
            Offline();
        }
        catch (Exception e)
        {
            Fail("offline checks", e.ToString());
        }
        Note($"offline done: {_pass} PASS, {_fail} FAIL");
        await RecordLoad();
        await RoundTrips();
        await Persistence();
        if (Knowledge.Status is { } end)
            Note($"counters: {end.Submitted} submitted, {end.Applied} applied, {end.Failed} failed, {end.ChangesSeen} change events, queue max {end.QueueMax}");
    }

    // ------------------------------------------------------------------------------------------- no player ------
    private void Offline()
    {
        var st = Knowledge.Status;
        Check(st is not null, "status", "Knowledge.Status is null");
        if (st is null) return;
        Note($"status: {st}");
        Check(st.Knowledge && st.World, "knowledge reads + world", st.Reason);
        Check(st.Applying, "the game's knowledge update runs (Status.Applying)", st.Reason);
        Check(st.Changes, "in-step apply (revoke/set/ordered grants/change events)", st.Reason);
        Check(st.Journal, "journal + query evaluator", st.Reason);
        Check(st.Npc, "NPC registry", st.Reason);
        Check(st.Dialogue, "dialogue events (program events_in)", st.Reason);
        Check(st.ClientInput, "client knowledge input hook", st.Reason);
        Check(st.Program, "program kiln_knowledge active", st.Reason);
        // What the mods added at boot, from the boot build's own manifest: the rest of what the registries hold is the game's. A count
        // that is off here is a mod's object the game did not take, or one it has that nobody built.
        var built = NativeQuests.Mods.Where(m => m.Ok).ToList();
        var ownIds = built.SelectMany(m => m.Knowledge.Values).Distinct().ToList();
        var ownInfo = ownIds.Select(id => Knowledge.Info(id)).ToList();
        var ownWorld = ownInfo.Count(i => i?.IsWorld == true);
        var ownPlayer = ownInfo.Count(i => i?.IsPlayer == true);
        Check(ownInfo.All(i => i is not null), $"the game has every knowledge object the mods built ({ownIds.Count}: {ownWorld} world, {ownPlayer} player)",
            $"{ownInfo.Count(i => i is null)} missing");
        var ownQuests = built.Sum(m => m.Quests.Count);
        var ownSteps = built.Sum(m => m.Quests.Sum(q => q.Steps.Count));
        var ownNpcs = built.Sum(m => m.Npcs.Count(n => n.Roster));
        var ownUnlocks = built.Sum(m => m.Npcs.Count(n => n.Roster && n.Unlock != 0));
        Note($"the mods' native data: {ownWorld} world and {ownPlayer} player objects, {ownQuests} quests with {ownSteps} steps, {ownNpcs} roster NPCs ({ownUnlocks} with an unlock)");
        var gameWorld = st.WorldObjects - ownWorld;
        var gamePlayer = st.PlayerObjects - ownPlayer;
        Check(gameWorld > 0 && gamePlayer > 0, $"GameKnowledgeResource sizes: the game's {gameWorld} world and {gamePlayer} player objects + the mods'", $"{st.WorldObjects} world, {st.PlayerObjects} player");

        // catalog
        var npcs = Knowledge.Npcs;
        var gameNpcs = npcs.Count - ownNpcs;
        Check(st.Npcs == npcs.Count && gameNpcs > 0 && npcs.Count(n => built.Any(m => m.Npcs.Any(x => x.Roster && x.NpcId == n.NpcId))) == ownNpcs,
            $"the roster: the game's {gameNpcs} NPCs + the mods' {ownNpcs}", $"status {st.Npcs}, listed {npcs.Count}");
        var withUnlock = npcs.Where(n => !n.Unlock.IsEmpty).ToList();
        Check(withUnlock.Count > ownUnlocks && built.All(m => m.Npcs.Where(n => n.Roster && n.Unlock != 0).All(n => withUnlock.Any(x => x.NpcId == n.NpcId && x.Unlock.Id == n.Unlock))),
            $"NPCs with an unlock requirement: the game's {withUnlock.Count - ownUnlocks} + the mods' {ownUnlocks}", $"{withUnlock.Count}");
        var quests = Journal.Quests();
        var steps = quests.Sum(q => q.Steps.Count);
        var gameQuests = quests.Count - ownQuests;
        Check(st.Quests == quests.Count && gameQuests > 0 && built.All(m => m.Quests.All(q => quests.Any(x => x.EntryId == q.EntryId))),
            $"journal: the game's {gameQuests} quests and {steps - ownSteps} steps + the mods' {ownQuests} and {ownSteps}", $"status {st.Quests}, listed {quests.Count}, {steps} steps");
        var queries = Knowledge.Catalog(KnowledgeCatalog.Queries);
        Check(st.Queries == queries.Count && queries.Count > 0, $"{queries.Count} named queries", $"status {st.Queries}, listed {queries.Count}");
        // the numbers of the build this was written on: news, not a failure (a game update changes them)
        if (gameNpcs != 35 || gameQuests != 188 || steps - ownSteps != 406 || gameWorld != 5825 || gamePlayer != 10744 || queries.Count != 5296)
            Note($"not the counts of svn 1287369 (35 NPCs, 188 quests, 406 steps, 5825 world and 10744 player objects, 5296 queries): {gameNpcs}, {gameQuests}, {steps - ownSteps}, {gameWorld}, {gamePlayer}, {queries.Count}");

        // every name resolves and maps back
        int ok = 0, bad = 0, noUnlock = 0;
        string firstBad = "";
        foreach (var cat in new[] { KnowledgeCatalog.Npcs, KnowledgeCatalog.Quests, KnowledgeCatalog.Queries })
            foreach (var n in Knowledge.Catalog(cat))
            {
                if (cat == KnowledgeCatalog.Npcs && n.Id == 0)
                {
                    noUnlock++;  // listed, but no requirement: nothing to resolve to
                    if (Knowledge.TryResolve(n.Text, out _)) bad++;
                    continue;
                }
                if (Knowledge.TryResolve(n.Text, out var r) && r.Id == n.Id && r.IsQuery == n.IsQuery &&
                    Knowledge.NameOf(n.Id, n.IsQuery) is { } back && Knowledge.TryResolve(back, out var r2) && r2.Id == n.Id)
                    ok++;
                else
                {
                    bad++;
                    if (firstBad.Length == 0) firstBad = n.Text;
                }
            }
        Check(bad == 0 && ok == withUnlock.Count + quests.Count + queries.Count && noUnlock == npcs.Count - withUnlock.Count,
            $"names resolve and map back ({ok}; {noUnlock} NPCs without an unlock have no name)", $"{bad} bad, first '{firstBad}', {ok} of {withUnlock.Count + quests.Count + queries.Count}, {noUnlock} without an unlock");
        var bard = Knowledge.Resolve("npc:NPC_Workshop_Bard01.unlock");
        Check(bard.Id == 0xff6000e2 && !bard.IsQuery, "npc:NPC_Workshop_Bard01.unlock = 0xff6000e2", bard.ToString());
        Check(Knowledge.Resolve("NPC:npc_workshop_alchemist01").Id == 0x6da00be7, "names are case-insensitive (Alchemist unlock 0x6da00be7)");
        var shelter = Knowledge.Resolve("query:NPC_Alchemist_Hint01_HAS_Shelter_Journal");
        Check(shelter.IsQuery && shelter.Id == 0x4f5c5b09, "query name -> query id 0x4f5c5b09", shelter.ToString());
        var step = Knowledge.Resolve("quest:0x8755c335.step1.visible");
        Check(step.Id == 0x88b160c1 && !step.IsQuery, "quest:0x8755c335.step1.visible = 0x88b160c1 (Alchemist Needs Shelter)", step.ToString());
        Check(!Knowledge.TryResolve("npc:NoSuchNpc", out _), "unknown name refused");

        // info
        var bi = Knowledge.Info(0xff6000e2);
        Check(bi is { IsWorld: true, IsPlayer: true, WorldType: KnowledgeType.Bool } && (bi.Value.Flags & KnowledgeObjectFlags.Twin) != 0,
            "info: the Bard's unlock is a world Bool with a player twin", bi?.ToString() ?? "null");
        Check(Knowledge.Info(0x12345678) is null, "info: unknown id is null");

        // world map
        var world = Knowledge.World.List();
        Check(world.Count >= 500, $"world map lists {world.Count} set entries (the lab save: 566)");
        Check(world.All(s => Knowledge.Info(s.Id)?.IsWorld == true), "every world entry is a static world object");
        var g = Knowledge.World.Get(0x6da00be7);
        Check(g is { IsSet: true, Type: KnowledgeType.Bool, Present: true }, "Get: the Alchemist's unlock is set in this world", g?.ToString() ?? "null");

        // NPC registry vs knowledge
        int agree = 0, disagree = 0, unlocked = 0;
        foreach (var n in withUnlock)
        {
            var has = Knowledge.World.Has(n.Name);
            if (n.Unlocked == true) unlocked++;
            if (n.Unlocked == has) agree++;
            else
            {
                disagree++;
                Note($"npc {n.DebugName}: registry {n.Unlocked}, knowledge {has}");
            }
        }
        Check(disagree == 0, $"NpcRegistry unlock state agrees with the world knowledge for {agree} NPCs ({unlocked} unlocked)");

        // journal: the evaluator against direct reads for SimpleBool world steps
        int simple = 0, simpleOk = 0, visible = 0, extern_ = 0, externErr = 0;
        foreach (var q in quests)
        {
            var state = Journal.State(EntityId.None, q);
            foreach (var s in state.Steps)
            {
                if (s.Visible == true) visible++;
                var v = s.Step.Visible;
                if (v.Type == KnowledgeQueryType.SimpleBool && !v.PlayerOnly)
                {
                    simple++;
                    if (s.Visible == (Knowledge.World.Get(v.Id)?.IsSet ?? false)) simpleOk++;
                    else if (simple - simpleOk <= 5) Note($"journal: quest 0x{q.EntryId:x8} step 0x{v.Id:x8}: the evaluator says {s.Visible}, the world's map {Knowledge.World.Get(v.Id)}, info {Knowledge.Info(v.Id)}");
                }
                else if (v.Type == KnowledgeQueryType.Extern)
                {
                    extern_++;
                    if (s.Visible is null) externErr++;
                }
            }
        }
        Check(simple > 0 && simpleOk == simple, $"journal: the evaluator equals direct reads for {simple} SimpleBool steps", $"{simple - simpleOk} differ (the first are in the notes above)");
        Note($"journal (world): {visible} visible steps; {extern_} named-query steps, {externErr} not evaluable for the world (player queries)");
        Check(visible >= 29, $"journal: at least the 29 SimpleBool-visible steps of the lab world are visible ({visible})");

        // world query evaluation
        int qt = 0, qf = 0, qe = 0;
        foreach (var n in queries.Take(2000))
        {
            var r = Knowledge.TryQuery(EntityId.None, n.Requirement, KnowledgeMode.Shared);
            if (r is null) qe++;
            else if (r.Value) qt++;
            else qf++;
        }
        Check(qt + qf > 500, $"world queries evaluate ({qt} true, {qf} false, {qe} player/unevaluable of the first 2000)");

        // dialogue line names through kiln.gamedata
        if (Knowledge.TryResolve("line:NPC_Workshop_Alchemist01/option0", out var line))
        {
            var li = Knowledge.Info(line.Id);
            Check(li is { IsWorld: true }, $"line:NPC_Workshop_Alchemist01/option0 = 0x{line.Id:x8} is world knowledge (line heard)", li?.ToString() ?? "unknown id");
        }
        else Note("line names: kiln.gamedata did not resolve line:NPC_Workshop_Alchemist01/option0 (not a failure: optional)");

        // refusals and argument checks
        Throws<ArgumentException>(() => Knowledge.World.Grant(0x12345678), "grant of an unknown id throws");
        Throws<InvalidOperationException>(() => Knowledge.World.Grant("query:NPC_Alchemist_Hint01_HAS_Shelter_Journal"), "grant of a query name throws");
        var noPlayer = Knowledge.For(new EntityId(7)).Grant(0xff6000e2).Result;
        Check(!noPlayer.Ok && noPlayer.Status == "unavailable", "grant for a player who is not in game fails cleanly", noPlayer.ToString());
        Throws<NotSupportedException>(() => Knowledge.For(new EntityId(7)).Get(0xff6000e2), "Get for a player who is not in game throws NotSupported");
        var playerQuery = queries.FirstOrDefault(n => Knowledge.TryQuery(EntityId.None, n.Requirement) is null);
        if (playerQuery is not null)
            Throws<ArgumentException>(() => Knowledge.Query(EntityId.None, playerQuery.Requirement), $"a player query for the world throws ({playerQuery.Text})");

        // settings round trip (native configure; the file is not touched)
        var before = Knowledge.Security.Current;
        Knowledge.Security.Apply(before with { VetoClientKnowledge = true, CheckDialogue = true, DialogueRange = 7 });
        var on = Knowledge.Security.Current;
        Knowledge.Security.Apply(before);
        var off = Knowledge.Security.Current;
        Check(on.VetoClientKnowledge && on.CheckDialogue && Math.Abs(on.DialogueRange - 7) < 0.01 && !off.VetoClientKnowledge && !off.CheckDialogue,
            "security settings apply and restore", $"{before} / {on} / {off}");
        Check((Knowledge.Status?.QueueMax ?? 9999) < 2048, $"KnowledgeTriggerQueue never full (max seen {Knowledge.Status?.QueueMax})");
    }

    private void Throws<T>(Action a, string what) where T : Exception
    {
        try
        {
            a();
            Fail(what, "no exception");
        }
        catch (T)
        {
            Pass(what);
        }
        catch (AggregateException e) when (e.InnerException is T)
        {
            Pass(what);
        }
        catch (Exception e)
        {
            Fail(what, e.GetType().Name + ": " + e.Message);
        }
    }

    // ------------------------------------------------------------------------------------------- round trips ----
    private async Task RoundTrips()
    {
        // a world Counter: +1, then SET back
        var counter = FindWorldObject(KnowledgeType.Counter);
        if (counter != 0)
        {
            var v0 = Knowledge.World.Get(counter)?.Value ?? 0;
            var r1 = await Knowledge.World.Grant(counter, 1);
            Check(r1.Ok && r1.Before == v0 && r1.After == v0 + 1, $"Counter 0x{counter:x8}: grant +1 ({v0} -> {r1.After})", r1.ToString());
            var r2 = await Knowledge.World.Set(counter, v0);
            Check(r2.Ok && r2.After == v0 && (Knowledge.World.Get(counter)?.Value ?? 99) == v0, $"Counter 0x{counter:x8}: set back to {v0}", r2.ToString());
        }
        else Note("no world Counter object found to test (skipped)");

        // a world Flags object that is not set: grant its lowest flag, revoke it
        var flags = FindWorldObject(KnowledgeType.Flags, unset: true);
        if (flags != 0)
        {
            var info = Knowledge.Info(flags)!.Value;
            var bit = System.Numerics.BitOperations.TrailingZeroCount(info.WorldFlags);
            var r1 = await Knowledge.World.Grant(flags, (uint)bit);
            Check(r1.Ok && r1.After == 1u << bit, $"Flags 0x{flags:x8}: grant flag {bit} -> {r1.After}", r1.ToString());
            await Steps(2);
            var ev = TakeChange(flags);
            Check(ev is { ByKiln: true, NewValue: var nv } && nv == 1u << bit, "KnowledgeChangedEvent for the flag grant (by Kiln)", ev?.ToString() ?? "none");
            var r2 = await Knowledge.World.Revoke(flags, bit);
            Check(r2.Ok && r2.Before == 1u << bit && r2.After == 0, $"Flags 0x{flags:x8}: revoke flag {bit} -> 0", r2.ToString());
            await Steps(2);
            var ev2 = TakeChange(flags);
            Check(ev2 is { Lost: true, ByKiln: true }, "KnowledgeChangedEvent for the revoke (value 0)", ev2?.ToString() ?? "none");
            var s = Knowledge.World.Get(flags);
            Check(s is { IsSet: false }, $"Flags 0x{flags:x8} reads 0 after the revoke (entry present: {s?.Present})", s?.ToString() ?? "null");
            Throws<ArgumentException>(() => Knowledge.World.Grant(flags, 5), $"grant of a flag the object does not have (5) throws");
        }
        else Note("no unset world Flags object found to test (skipped)");
        var rr = await Knowledge.World.Grant(0x6da00be7);
        Check(rr.Ok && !rr.Changed && rr.Before == 1 && rr.After == 1, "grant of a set Bool: no change (the game only raises values)", rr.ToString());
    }

    // Test objects chosen offline from the decoded GameKnowledgeResource / query db / triggers / journal / NPC roster
    // (tools/kfc, client data 1287369): 0xc07ee179 is the world Counter of the editor's query "8k_test_counter" (a
    // trigger fires above 1: +1 from 0 fires nothing); the Flags ids are world Flags objects (expectedFlags 1) that no
    // query, trigger, journal step, NPC or scene trigger names. The first one that exists and is unset is used.
    private static uint FindWorldObject(KnowledgeType type, bool unset = false)
    {
        uint[] candidates = type == KnowledgeType.Counter ? [0xc07ee179u] : [0x003374adu, 0x00382e0eu, 0x006b0c01u, 0x0090fa03u];
        foreach (var id in candidates)
        {
            if (Knowledge.Info(id) is not { } i || i.WorldType != type) continue;
            if (unset && Knowledge.World.Get(id)?.IsSet != false) continue;
            return id;
        }
        return 0;
    }

    private KnowledgeChangedEvent? TakeChange(uint id)
    {
        lock (_changes)
        {
            var e = _changes.LastOrDefault(c => c.Id == id);
            _changes.RemoveAll(c => c.Id == id);
            return e;
        }
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    // ------------------------------------------------------------------------------------------- a player's record ----
    // A joining player's knowledge is not a change. The real path, on a free player block: the login activates the block,
    // 3.5 s later the client's record goes straight into the map and raises the game's record flag; the game's own
    // knowledge update consumes the flag in its step (0x9a48e0: list cleared, one marker entry, version + 1), so nothing of
    // this can be seen as a flag from Post. One join gave 978 "changed" lines without this.
    private async Task RecordLoad()
    {
        var block = new EntityId(16);  // the last block; a lab has no players
        var ids = Journal.Quests().Select(q => q.EntryId).Where(i => i != 0).Distinct().Take(6).ToArray();
        if (ids.Length < 6 || !Knowledge.LabRecord(block, 0))
        {
            Fail("record load", $"no free block 16 or too few quest ids ({ids.Length})");
            return;
        }
        await Steps(3);
        lock (_changes) _changes.RemoveAll(c => c.Target == block);
        var record = ids.Take(4).Select(i => (i, 1u)).ToArray();
        Check(Knowledge.LabRecord(block, 1, record), "a character's record loads into a free block (the game's way: map, record flag)");
        await Steps(5);
        int seen;
        lock (_changes) seen = _changes.Count(c => c.Target == block);
        Check(seen == 0, "a character's record is the baseline: no change events", $"{seen} events: {string.Join("; ", _changes.Where(c => c.Target == block).Take(5))}");
        Check(Knowledge.For(block).Get(ids[0])?.Value == 1, "the record's values read back", $"{Knowledge.For(block).Get(ids[0])}");
        lock (_changes) _changes.RemoveAll(c => c.Target == block);
        var rv = await Knowledge.For(block).Revoke(ids[0]);
        var ev = TakeChange(ids[0]);
        Check(rv.Ok && rv.Changed && ev is { Lost: true }, "after the load a revoke is one real change (1 -> 0)", $"{rv}; {ev}");
        var st = await Knowledge.For(block).Set(ids[5], 1);
        var ev2 = TakeChange(ids[5]);
        Check(st.Ok && st.Changed && ev2 is { Gained: true }, "after the load a new id is one real change (0 -> 1)", $"{st}; {ev2}");
        lock (_changes) seen = _changes.Count(c => c.Target == block);
        Check(seen == 0, "and nothing else changed", $"{seen} more events");
        // What decides a notification: the client's journal notifies for a query that holds unless its state has the save's mark
        // (bits 32..39; client 0xf04d90 and 0x2fc8e0). A direct write keeps the mark, the game's apply clears it. Done quests that
        // notified again after a world switch came back the second way (NativeQuests.Apply).
        Check(Knowledge.For(block).Get(ids[5])?.NewMask == 0xff, "a direct write of a new id keeps the save's mark: its notification stays quiet", $"{Knowledge.For(block).Get(ids[5])}");
        Check(Knowledge.For(block).Get(ids[1])?.NewMask == 0xff, "the record's values have it too", $"{Knowledge.For(block).Get(ids[1])}");
        var loud = await Knowledge.For(block).Grant(ids[4]);
        Check(loud.Ok && Knowledge.For(block).Get(ids[4])?.NewMask == 0, "the game's own apply clears it: its notification fires", $"{loud}; {Knowledge.For(block).Get(ids[4])}");
        Knowledge.LabRecord(block, 2);
        await Steps(2);
        lock (_changes) _changes.RemoveAll(c => c.Target == block);
    }

    // ------------------------------------------------------------------------------------------- persistence ----
    private async Task Persistence()
    {
        var state = SafeRead(_statePath).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var phase = state.Length > 0 ? state[0] : "";
        if (phase == "done")
        {
            Note("persistence: all three runs passed earlier (touch kiln/control/quests-test.reset to start over)");
            return;
        }
        if (phase == "")
        {
            var npc = Knowledge.FindNpc("NPC_Workshop_Bard01") is { Unlocked: false } b ? b
                : Knowledge.Npcs.FirstOrDefault(n => n.Unlocked == false && n.Unlock.Type == KnowledgeQueryType.SimpleBool && !n.Unlock.PlayerOnly);
            if (npc is null)
            {
                Fail("persistence run 1", "no locked NPC with a world unlock knowledge in this world");
                return;
            }
            var id = npc.Unlock.Id;
            var before = Knowledge.World.Get(id);
            Check(before is { IsSet: false }, $"run 1: {npc.DebugName}'s unlock 0x{id:x8} is not set in this world", before?.ToString() ?? "null");
            var r = await Knowledge.World.Grant(npc.Name + ".unlock");
            Check(r.Ok && r.Changed && r.Before == 0 && r.After == 1, $"run 1: world grant of {npc.Name}.unlock (0 -> 1)", r.ToString());
            await Steps(3);
            var ev = TakeChange(id);
            Check(ev is { Gained: true, IsWorld: true } && ev.Request == r.Request, "run 1: KnowledgeChangedEvent with the request id", ev?.ToString() ?? "none");
            var unlocked = await WaitUnlocked(npc.DebugName, true);
            Check(unlocked, $"run 1: NpcRegistry reports {npc.DebugName} unlocked (npc_unlock_state_server followed the knowledge)");
            File.WriteAllText(_statePath, $"granted 0x{id:x8} {npc.DebugName}\n");
            Note($"persistence: granted 0x{id:x8} ({npc.DebugName}); restart the server (lab.sh stop/start) for run 2");
            return;
        }
        if (state.Length < 3 || !Knowledge.TryParseId(state[1], out var kid))
        {
            Fail("persistence", $"bad state file '{string.Join(' ', state)}'");
            return;
        }
        var name = state[2];
        if (phase == "granted")
        {
            var s = Knowledge.World.Get(kid);
            Check(s is { IsSet: true }, $"run 2: 0x{kid:x8} ({name}) is still set after the restart (the world save kept it)", s?.ToString() ?? "null");
            Check(await WaitUnlocked(name, true), $"run 2: NpcRegistry reports {name} unlocked after the restart");
            var r = await Knowledge.World.Revoke(kid);
            Check(r.Ok && r.Changed && r.Before == 1 && r.After == 0, $"run 2: world revoke of 0x{kid:x8} (1 -> 0)", r.ToString());
            await Steps(3);
            var ev = TakeChange(kid);
            Check(ev is { Lost: true, IsWorld: true } && ev.Request == r.Request, "run 2: KnowledgeChangedEvent for the revoke", ev?.ToString() ?? "none");
            Check(await WaitUnlocked(name, false), $"run 2: NpcRegistry reports {name} locked again");
            File.WriteAllText(_statePath, $"revoked 0x{kid:x8} {name}\n");
            Note($"persistence: revoked 0x{kid:x8} ({name}); restart the server for run 3");
            return;
        }
        if (phase == "revoked")
        {
            var s = Knowledge.World.Get(kid);
            Check(s is { IsSet: false, Present: false }, $"run 3: 0x{kid:x8} ({name}) is absent after the restart (the save left it out)", s?.ToString() ?? "null");
            Check(await WaitUnlocked(name, false), $"run 3: NpcRegistry reports {name} locked after the restart");
            File.WriteAllText(_statePath, "done\n");
            Note("persistence: grant, restart, revoke, restart: done");
        }
    }

    private async Task<bool> WaitUnlocked(string npc, bool unlocked)
    {
        for (var i = 0; i < 40; i++)
        {
            if (Knowledge.FindNpc(npc)?.Unlocked == unlocked) return true;
            await Steps(1);
        }
        Note($"{npc}: registry says {Knowledge.FindNpc(npc)?.Unlocked}");
        return false;
    }

    // ------------------------------------------------------------------------------------------- live -----------
    // K1/K2: arg "" or "grant" grants the first-step visibility knowledge of the first quest whose first step is a
    // player-only SimpleBool the player does not have yet (lab: 51 of 188 quests qualify, the first is quest:0xe6f1d494),
    // or of the quest named in the file ("grant quest:0x…"). "revoke" takes it back from the quest of the last live
    // grant, or the named one.
    private JournalQuest? _liveQuest;

    private async Task RunLive(string arg)
    {
        var p = Players.All.FirstOrDefault(x => x.InGame);
        if (p is null)
        {
            Fail("live", "no player in game");
            return;
        }
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "grant";
        JournalQuest? quest = parts.Length > 1 ? Journal.Find(parts[1]) : null;
        if (verb == "revoke") quest ??= _liveQuest;
        quest ??= Journal.Quests().FirstOrDefault(q => q.Steps.Count > 0 && q.Steps[0].Visible is { Type: KnowledgeQueryType.SimpleBool, PlayerOnly: true } v &&
                                                      Knowledge.For(p).Get(v.Id)?.IsSet == false);
        if (quest is null)
        {
            Fail("live", "no quest to use");
            return;
        }
        _liveQuest = quest;
        var vis = quest.Steps[0].Visible;
        Note($"live: {p.Name} (entity {p.Entity}): {verb} {quest} step 1 visibility 0x{vis.Id:x8}; state before: {Journal.State(p, quest)}");
        var r = verb == "revoke" ? await Knowledge.For(p).Revoke(vis.Id) : await Knowledge.For(p).Grant(vis.Id);
        Check(r.Ok && r.Changed, $"live: {verb} 0x{vis.Id:x8} for {p.Name} ({r.Before} -> {r.After})", r.ToString());
        await Steps(4);
        var after = Journal.State(p, quest);
        Check(after.Steps[0].Visible == (verb != "revoke"), $"live: the server's journal state follows (step 1 visible: {after.Steps[0].Visible})", after.ToString());
        Note($"live: state after: {after}; look at the journal in the client");
    }

    private static string SafeRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
    }
}
