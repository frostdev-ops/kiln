// SPDX-License-Identifier: MIT
// kiln.progression test mod. Two parts:
//  1. Always (no player needed), ~4 s after load: the service and its program are up; the game's experience table
//     (the game's own function) equals the BalancingTable decoded offline (tools/kfc) for levels 1..5 and the formula
//     read from the function for 6..cap; the skill tree has the 243 nodes of the SkillTreeResource and agrees node by
//     node with kiln.gamedata's independent reflection reader (costs, max levels); the game's skill functions on a
//     scratch node list (link check, unlock, levels, unlearn, connectivity, points spent, respec); the skill-point
//     knowledge list; requests for a player who is not there fail cleanly; argument checks throw.
//  2. Live (a player in game; hosted client or joined lab): touch kiln/control/progression-test.live. The mod runs the
//     whole path on the first player in game and puts everything back: add experience, set level, set experience,
//     unlock / lock a node, respec, events. With "give-point" in the file it also gives one skill point (knowledge: that
//     one stays).
//     Use it only with a throwaway character (a LOCAL save copy).
// Output: "progression-test: PASS <what>" / "FAIL <what>: <why>" lines in kiln/logs/kiln.log, then a summary.
using Kiln;

namespace ProgressionTest;

public sealed class ProgressionTestMod : Mod
{
    private int _pass, _fail;
    private ulong _start;
    private bool _done, _liveRunning;
    private readonly List<string> _events = new();
    private string _trigger = "";
    private bool _givePoint;

    private void Pass(string what) { _pass++; Log.Info($"progression-test: PASS {what}"); }
    private void Fail(string what, string why) { _fail++; Log.Error($"progression-test: FAIL {what}: {why}"); }
    private void Check(bool ok, string what, string why = "") { if (ok) Pass(what); else Fail(what, why); }

    public override void OnLoad()
    {
        Log.Info($"progression-test: loading (kiln.progression available: {Progression.Available})");
        _trigger = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Context.DataDirectory)!)!, "control", "progression-test.live");
        if (Progression.Available)
        {
            Events.On<PlayerLevelChangedEvent>(e => _events.Add($"level {e.Entity} {e.OldLevel}->{e.NewLevel} req {e.Request}"));
            Events.On<PlayerSkillsChangedEvent>(e => _events.Add($"skills {e.Entity} {e.OldPointsSpent}->{e.NewPointsSpent} req {e.Request}"));
        }
        Context.Tick.Pre += OnPre;
    }

    private void OnPre(in TickInfo tick)
    {
        if (_start == 0) _start = tick.Step;
        if (!_done && tick.Step - _start >= 100 && Content.GameDataAvailable is var gd && (!gd || Content.Ready || tick.Step - _start > 2500))
        {
            _done = true;
            try
            {
                RunOffline();
            }
            catch (Exception e)
            {
                Fail("offline run", e.ToString());
            }
            Log.Info($"progression-test: offline done: {_pass} PASS, {_fail} FAIL");
        }
        if (_done && !_liveRunning && tick.Step % 25 == 0 && File.Exists(_trigger))
        {
            try
            {
                _givePoint = File.ReadAllText(_trigger).Contains("give-point", StringComparison.Ordinal);
                File.Delete(_trigger);
            }
            catch (IOException) { }
            _liveRunning = true;
            _ = RunLive().ContinueWith(t =>
            {
                if (t.Exception is { } e) Fail("live run", e.GetBaseException().ToString());
                Log.Info($"progression-test: live done: {_pass} PASS, {_fail} FAIL (events seen: {string.Join("; ", _events)})");
                _liveRunning = false;
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    // ------------------------------------------------------------------------------------------- no player ------
    private void RunOffline()
    {
        Check(Progression.Available, "kiln.progression is published");
        if (!Progression.Available) return;
        var st = Progression.Status!;
        Log.Info($"progression-test: status {st}");
        Check(st.Levels && st.Skills && st.SkillKnowledge && st.ExperienceEvents, "every part resolved on this build",
            $"levels {st.Levels} skills {st.Skills} knowledge {st.SkillKnowledge} events {st.ExperienceEvents}: {st.Reason}");
        Check(st.Program, "program kiln_players_progression is active", st.Reason);
        Check(st.World, "the server world's singletons were found", st.Reason);
        Check(st.Reason.Length == 0, "no reason given for anything off", st.Reason);

        // The game's experience table vs the BalancingTable resource (82706b40-…, decoded with tools/kfc):
        // experienceNeedPerLevel [1000, 2023, 3046, 4069, 5092]; levels >= 6: base = min(start, end) + |end - start| *
        // (level / playerLevelMax), v = base * xpSeedEnemy, v = v * 1.5 + xpNeededFromKnowledge, rounded half away from 0
        // (killToLevelUPStart 50, killToLevelUPEnd 200, playerLevelMax 100, xpSeedEnemy 50, xpNeededFromKnowledge 2562).
        uint[] table = [1000, 2023, 3046, 4069, 5092];
        var (_, cap) = Progression.Diagnostics.ExperienceForLevel(1);
        Check(cap == 50, "level cap 50 (BalancingTable.playerLevelCap)", $"cap {cap}");
        Check(st.LevelCap == cap && st.SkillPointsPerLevel == 2, "status: cap and 2 skill points per level",
            $"cap {st.LevelCap} per level {st.SkillPointsPerLevel}");
        var bad = new List<string>();
        var line = new List<string>();
        for (var level = 1; level <= Math.Max(cap, 5); level++)
        {
            var (need, _) = Progression.Diagnostics.ExperienceForLevel(level);
            uint want;
            if (level <= 5) want = table[level - 1];
            else
            {
                var v = (50f + 150f * ((float)level / 100f)) * 50f;
                v = v * 0.5f + v + 2562f;
                want = (uint)(int)(v >= 0 ? v + 0.5f : v - 0.5f);
            }
            if (need != want) bad.Add($"{level}: {need} (want {want})");
            if (level <= 6 || level % 10 == 0) line.Add($"{level}:{need}");
        }
        Log.Info($"progression-test: experience per level {string.Join(" ", line)}");
        Check(bad.Count == 0, $"experience for levels 1..{cap} = the BalancingTable values", string.Join(", ", bad.Take(5)));

        Attributes();

        // The tree.
        var tree = Progression.Tree;
        var types = tree.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count());
        Log.Info($"progression-test: tree {tree.Count} nodes: {string.Join(", ", types.Select(kv => $"{kv.Key} {kv.Value}"))}, " +
                 $"{tree.Count(n => n.MaxLevel > 0)} with levels, {tree.Count(n => n.ActiveSkillItem != 0)} unlock an ability");
        Check(tree.Count == 243 && st.TreeNodes == 243, "the skill tree has 243 nodes", $"{tree.Count} / status {st.TreeNodes}");
        Check(types.GetValueOrDefault(SkillNodeType.Root) == 12 && types.GetValueOrDefault(SkillNodeType.Attribute) == 65 &&
              types.GetValueOrDefault(SkillNodeType.ActiveSkill) == 43 && types.GetValueOrDefault(SkillNodeType.PassiveSkill) == 123,
            "node types Root 12, Attribute 65, ActiveSkill 43, PassiveSkill 123 (docs/re/abilities.md)");
        var dj = Progression.FindNode(0xffd5f940);
        Check(dj is { Type: SkillNodeType.ActiveSkill, Cost: 2 } && dj.Value.ActiveSkillItem != 0,
            "node 0xffd5f940 (double jump): ActiveSkill, cost 2, unlocks an ability item", $"{dj}");
        Check(Progression.ParseNode("0xFFD5F940")?.Id == 0xffd5f940 && Progression.ParseNode("ffd5f940")?.Id == 0xffd5f940 &&
              Progression.ParseNode(0xffd5f940u.ToString())?.Id == 0xffd5f940 && Progression.ParseNode("0x12345678") is null,
            "ParseNode: 0x hex, bare hex, decimal, unknown");
        CrossCheckGameData(tree);

        Simulation(tree);

        // Knowledge.
        Check(st.KnowledgeEntries == 78 && st.KnowledgePlusOne == 60,
            "skill-point knowledge: 78 entries, 60 of them +1 (GameKnowledgeResource.skillPoints)",
            $"{st.KnowledgeEntries} / {st.KnowledgePlusOne}");

        // Requests for nobody (an entity id no player has), and argument checks.
        var nobody = Enumerable.Range(1, 64).Select(i => new EntityId((uint)i)).First(e => Players.Find(e) is null);
        Check(Progression.Get(nobody) is null, "Get(nobody): null");
        Check(Progression.UnlockedNodes(nobody).Count == 0, "UnlockedNodes(nobody): empty");
        var r = Progression.SetLevel(nobody, 10);
        Check(r.IsCompleted && !r.Result.Ok && r.Result.Status == "not_found", "SetLevel for nobody: not_found at once",
            $"{r.Status} {(r.IsCompleted ? r.Result : null)}");
        var r2 = Progression.AddExperience(nobody, 100);
        Check(r2.IsCompleted && !r2.Result.Ok && r2.Result.Status == "not_found", "AddExperience for nobody: not_found");
        var r3 = Progression.Unlock(nobody, 0xffd5f940);
        Check(r3.IsCompleted && !r3.Result.Ok, "Unlock for nobody: not ok", $"{r3.Result}");
        Throws<ArgumentException>(() => Progression.SetLevel(nobody, 0), "SetLevel(0) throws ArgumentException");
        Throws<ArgumentException>(() => Progression.AddExperience(nobody, 0), "AddExperience(0) throws ArgumentException");
        Throws<ArgumentException>(() => Progression.Unlock(nobody, 0), "Unlock(node 0) throws ArgumentException");
        Throws<ArgumentException>(() => Progression.GiveSkillPoints(nobody, 0), "GiveSkillPoints(0) throws ArgumentException");
        Throws<InvalidOperationException>(() => Task.Run(() => Progression.Diagnostics.ExperienceForLevel(1)).GetAwaiter().GetResult(),
            "ExperienceForLevel off the tick thread throws");
        var off = Task.Run(() => Progression.Get(nobody)).GetAwaiter().GetResult();
        Check(off is null, "Get off the tick thread (snapshot): null for nobody");
        Check(new Player { Entity = nobody }.Progression.Read() is null, "Player.Progression.Read() for an absent player: null");
        if (Players.All.Count == 0) Check(_events.Count == 0, "no progression events on an empty server", string.Join("; ", _events));
    }

    // The attribute lookup and formula re-run on components laid out as the game initialises them (no player needed).
    private void Attributes()
    {
        uint L = Progression.Diagnostics.AttrLevel, X = Progression.Diagnostics.AttrExperience,
             G = Progression.Diagnostics.AttrGranted, N = Progression.Diagnostics.AttrNeeded;
        var e = Progression.Diagnostics.ProbeAttributes(false, [100, 0, 1000]);
        Log.Info($"progression-test: Experience root 0x{e.Root:x8}, slots level {e.Slots[0]} experience {e.Slots[1]} granted {e.Slots[2]} needed {e.Slots[3]}");
        Check(e.Root != 0 && e.Slots[0] == -1 && new[] { e.Slots[1], e.Slots[2], e.Slots[3] }.Order().SequenceEqual([0, 1, 2]),
            "experience, granted and needed are the three values of the Experience attribute root; level is not");
        var l = Progression.Diagnostics.ProbeAttributes(true, [1, 100]);
        Log.Info($"progression-test: Level root 0x{l.Root:x8}, slots level {l.Slots[0]} experience {l.Slots[1]} granted {l.Slots[2]} needed {l.Slots[3]}");
        Check(l.Root != 0 && l.Root != e.Root && l.Slots[0] is 0 or 1 && l.Slots[1] == -1 && l.Slots[2] == -1 && l.Slots[3] == -1,
            "level is one value of the Level attribute root");
        var start = new uint[3];
        start[e.Slots[1]] = 100;
        start[e.Slots[3]] = 1000;
        var g = Progression.Diagnostics.ProbeAttributes(false, start, G, 500);
        Check(g.Storage[e.Slots[2]] == 500 && g.Storage[e.Slots[1]] == 100 && g.Storage[e.Slots[3]] == 1000,
            "granted 500 survives the game's formula re-run (experience and needed untouched)", string.Join(",", g.Storage));
        var n = Progression.Diagnostics.ProbeAttributes(false, start, N, 7437);
        Check(n.Storage[e.Slots[3]] == 7437, "needed 7437 survives the needed re-run", string.Join(",", n.Storage));
        var lv = new uint[2];
        lv[l.Slots[0]] = 1;
        lv[1 - l.Slots[0]] = 100;
        var l60 = Progression.Diagnostics.ProbeAttributes(true, lv, L, 30);
        var l200 = Progression.Diagnostics.ProbeAttributes(true, lv, L, 200);
        Log.Info($"progression-test: Level after writing 30: {string.Join(",", l60.Storage)}; after writing 200: {string.Join(",", l200.Storage)}");
        Check(l60.Storage[l.Slots[0]] == 30, "level 30 survives the formula re-run", string.Join(",", l60.Storage));
        Check(l200.Storage[l.Slots[0]] <= 200, "level 200: kept or clamped by the game's formula", string.Join(",", l200.Storage));
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
        catch (Exception e)
        {
            Fail(what, e.GetType().Name + ": " + e.Message);
        }
    }

    private void CrossCheckGameData(IReadOnlyList<SkillNode> tree)
    {
        if (!Content.GameDataAvailable || !Content.Ready)
        {
            Log.Info("progression-test: kiln.gamedata is not ready: the tree cross-check is skipped");
            return;
        }
        var skills = Content.Skills;
        var byId = tree.ToDictionary(n => n.Id);
        var diff = new List<string>();
        foreach (var e in skills)
        {
            if (!byId.TryGetValue(e.Id, out var n))
            {
                diff.Add($"{e.Name} 0x{e.Id:x8} missing");
                continue;
            }
            var cost = (int)e.GetNumber("costs");
            var max = (int)e.GetNumber("maxLevel");
            if (cost != n.Cost || max != n.MaxLevel) diff.Add($"0x{e.Id:x8}: gamedata cost {cost} max {max}, progression {n.Cost} {n.MaxLevel}");
        }
        Check(skills.Count == tree.Count && diff.Count == 0,
            $"the tree agrees with kiln.gamedata's reflection reader ({skills.Count} nodes: ids, costs, max levels)",
            $"{skills.Count} vs {tree.Count}: {string.Join("; ", diff.Take(5))}");
    }

    private void Simulation(IReadOnlyList<SkillNode> tree)
    {
        var sim = Progression.Diagnostics.Simulate;
        var check = Progression.Diagnostics.SimOp.Check;
        var unlock = Progression.Diagnostics.SimOp.Unlock;
        var lockOp = Progression.Diagnostics.SimOp.Lock;
        var nonRoot = tree.Where(n => n.Type != SkillNodeType.Root).ToList();
        var first = sim([.. nonRoot.Select(n => (check, n.Id))]);
        var reachable = nonRoot.Where((n, i) => first[i].Reachable).ToList();
        Log.Info($"progression-test: {reachable.Count} of {nonRoot.Count} nodes are next to a root on an empty tree");
        Check(reachable.Count > 0 && reachable.Count < nonRoot.Count, "the game's link check: some nodes next to the roots, not all");
        Check(first.All(r => r.Ok && r.Spent == 0 && r.Nodes == 0 && r.Connected), "empty tree: nothing spent, connected");

        // a plain node next to a root, and a plain node only reachable through it
        var a = reachable.FirstOrDefault(n => n.MaxLevel == 0);
        if (a.Id == 0)
        {
            Fail("simulation", "no plain node next to a root");
            return;
        }
        var afterA = sim([(unlock, a.Id), .. nonRoot.Select(n => (check, n.Id))]);
        Check(afterA[0].Ok && afterA[0].Spent == a.Cost && afterA[0].Nodes == 1 && afterA[0].Connected,
            $"unlock 0x{a.Id:x8}: spent = its cost {a.Cost}, 1 node, connected", $"{afterA[0]}");
        SkillNode b = default;
        for (var i = 0; i < nonRoot.Count; i++)
            if (afterA[i + 1].Reachable && !first[i].Reachable && nonRoot[i].MaxLevel == 0 && nonRoot[i].Id != a.Id)
            {
                b = nonRoot[i];
                break;
            }
        if (b.Id == 0)
        {
            Fail("simulation", $"no node reachable only through 0x{a.Id:x8}");
            return;
        }
        var chain = sim([(unlock, a.Id), (unlock, b.Id), (lockOp, a.Id), (unlock, a.Id), (lockOp, b.Id), (lockOp, b.Id)]);
        Check(chain[1].Ok && chain[1].Reachable && chain[1].Spent == a.Cost + b.Cost && chain[1].Nodes == 2 && chain[1].Connected,
            $"unlock 0x{b.Id:x8} through it: spent {a.Cost + b.Cost}, connected", $"{chain[1]}");
        Check(chain[2].Ok && !chain[2].Connected, "locking the first node disconnects the second (Kiln's lock refuses that)", $"{chain[2]}");
        Check(chain[3].Ok && chain[3].Connected && chain[4].Ok && chain[4].Spent == a.Cost && chain[4].Connected,
            "re-unlock, then locking the second node: back to the first's cost, connected", $"{chain[3]} / {chain[4]}");
        Check(!chain[5].Ok, "locking a node that is not unlocked: the game refuses", $"{chain[5]}");
        var unreachable = nonRoot.Where((n, i) => !first[i].Reachable && !afterA[i + 1].Reachable).FirstOrDefault();
        if (unreachable.Id != 0)
        {
            var u = sim([(check, unreachable.Id)]);
            Check(!u[0].Reachable, $"a far node (0x{unreachable.Id:x8}) is not reachable on an empty tree");
        }

        // a node with levels
        var lv = reachable.FirstOrDefault(n => n.MaxLevel >= 2);
        if (lv.Id != 0)
        {
            var steps = Enumerable.Repeat((unlock, lv.Id), lv.MaxLevel + 1).Append((lockOp, lv.Id)).ToList();
            var r = sim(steps);
            var top = r[lv.MaxLevel - 1];
            Check(r[0].Ok && r[0].Level == 1 && top.Ok && top.Level == lv.MaxLevel && top.Spent == lv.Cost * lv.MaxLevel,
                $"node 0x{lv.Id:x8} with {lv.MaxLevel} levels: level 1..{lv.MaxLevel}, spent {lv.Cost} per level", $"{r[0]} / {top}");
            Check(!r[lv.MaxLevel].Ok && !r[lv.MaxLevel].Reachable, "one level past the last: refused (the link check says so too)", $"{r[lv.MaxLevel]}");
            var last = r[^1];
            Check(last.Ok && last.Level == lv.MaxLevel - 1 && last.Spent == lv.Cost * (lv.MaxLevel - 1), "lock: one level less", $"{last}");
        }
        else Fail("simulation", "no node with levels next to a root");
        var resp = sim([(unlock, a.Id), (unlock, b.Id), (Progression.Diagnostics.SimOp.Respec, 0)]);
        Check(resp[2].Ok && resp[2].Spent == 0 && resp[2].Nodes == 0, "respec: nothing unlocked, nothing spent", $"{resp[2]}");
    }

    // ------------------------------------------------------------------------------------------- live -----------
    private async Task RunLive()
    {
        var p = Players.All.FirstOrDefault();
        if (p is null)
        {
            Fail("live", "no player in game");
            return;
        }
        var prog = p.Progression;
        var before = prog.Read();
        Log.Info($"progression-test: live on {p}: {before}");
        if (before is null || !before.HasLevel)
        {
            Fail("live read", $"{before}");
            return;
        }
        Pass($"live read: {before}");
        var nodesBefore = prog.UnlockedNodes;
        Log.Info($"progression-test: unlocked nodes: {string.Join(", ", nodesBefore.Select(n => $"0x{n.Id:x8}:{n.Level}"))}");
        Check(nodesBefore.Count == before.UnlockedNodes, "UnlockedNodes count = the snapshot's", $"{nodesBefore.Count} vs {before.UnlockedNodes}");
        Check(before.SkillPointsTotal == before.SkillPointsFromLevel + before.SkillPointsFromKnowledge &&
              before.SkillPointsFromLevel == 2 * (before.Level - 1), "points: total = 2 x (level - 1) + knowledge");
        Check(before.SkillPointsSpent == nodesBefore.Sum(n => n.Cost), "points spent = the sum of the unlocked nodes' costs",
            $"{before.SkillPointsSpent} vs {nodesBefore.Sum(n => n.Cost)}");

        // experience: a quest-like grant; the game's player_level counts it
        var level0 = before.Level;
        if (!before.AtLevelCap)
        {
            var add = await prog.AddExperience(100, ExperienceSource.Quest);
            Check(add.Ok && add.Applied == 100, $"AddExperience 100: {add}");
            var a = add.After!;
            Check(a.ExperiencePending == 0 && (a.Experience == before.Experience + 100 || a.Level > level0),
                "the game counted it (pending 0, experience +100 or a level up)", $"{before} -> {a}");
        }
        // set the level one up, then back
        var target = Math.Min(level0 + 1, before.LevelCap);
        var set = await prog.SetLevel(target);
        Check(set.Ok && set.After!.Level == target && set.After.Experience == 0 && set.After.ExperienceNeeded == Progression.Diagnostics.ExperienceForLevel(target).Needed,
            $"SetLevel {target}: level, experience 0, needed = the table's", $"{set} {set.After}");
        Check(set.After!.SkillPointsFromLevel == 2 * (target - 1), "skill points follow the level", $"{set.After}");
        var exp = await prog.SetExperience(before.Experience);
        Check(exp.Ok && exp.After!.Experience == before.Experience, $"SetExperience {before.Experience}", $"{exp.After}");
        // a node: the first reachable affordable plain node that is not unlocked
        var now = prog.Read()!;
        var owned = prog.UnlockedNodes.Select(n => n.Id).ToHashSet();
        SkillNode? pick = null;
        foreach (var n in Progression.Tree.Where(n => n.Type != SkillNodeType.Root && n.MaxLevel == 0 && !owned.Contains(n.Id) && n.Cost <= Math.Max(now.SkillPoints, 0)))
        {
            var r = await prog.Unlock(n.Id);
            if (r.Ok)
            {
                pick = n;
                Check(r.After!.SkillPointsSpent == now.SkillPointsSpent + n.Cost && r.After.UnlockedNodes == now.UnlockedNodes + 1,
                    $"Unlock 0x{n.Id:x8}: spent +{n.Cost}, one node more", $"{r.After}");
                break;
            }
        }
        if (pick is null) Log.Info($"progression-test: no affordable linked node to unlock ({now.SkillPoints} points)");
        else
        {
            var l = await prog.Lock(pick.Value.Id);
            Check(l.Ok && l.After!.SkillPointsSpent == now.SkillPointsSpent, $"Lock 0x{pick.Value.Id:x8}: spent back", $"{l}");
        }
        // a node not linked to anything the player has: the game's link check refuses it without force
        var linked = new HashSet<uint>();
        foreach (var n in Progression.Tree.Where(n => n.Type != SkillNodeType.Root && !owned.Contains(n.Id) && n.MaxLevel == 0))
            if (Progression.Diagnostics.Simulate([(Progression.Diagnostics.SimOp.Check, n.Id)])[0].Reachable) linked.Add(n.Id);
        var far = Progression.Tree.FirstOrDefault(n => n.Type != SkillNodeType.Root && !owned.Contains(n.Id) && n.MaxLevel == 0 &&
                                                         !linked.Contains(n.Id) && n.Incoming + n.Outgoing > 0 &&
                                                         Progression.Tree.Count(m => owned.Contains(m.Id)) > 0 && n.Cost <= 1);
        if (far.Id != 0)
        {
            var refused = await prog.Unlock(far.Id);
            if (refused.Ok)
            {
                Log.Info($"progression-test: 0x{far.Id:x8} was linked for this player after all; locked again");
                await prog.Lock(far.Id, force: true);
            }
            else Check(refused.Reason.Contains("not linked") || refused.Reason.Contains("needs"), $"Unlock of a far node without force: refused ({refused.Reason})");
        }
        // one skill point as knowledge (it cannot be taken back: only when the trigger file asks for it)
        if (now.GivableSkillPoints > 0 && _givePoint)
        {
            var g = await prog.GiveSkillPoints(1);
            Check(g.Ok && g.Applied == 1 && g.After!.SkillPointsFromKnowledge == now.SkillPointsFromKnowledge + 1,
                "GiveSkillPoints 1: one more point from knowledge", $"{g} {g.After}");
        }
        // respec and put the nodes back
        var resp = await prog.Respec();
        Check(resp.Ok && resp.After!.SkillPointsSpent == 0, $"Respec: nothing spent ({resp})", $"{resp.After}");
        foreach (var n in nodesBefore)
            for (var i = 0; i < Math.Max(n.Level, 1); i++)
                await prog.Unlock(n.Id, force: true);
        var back = await prog.SetLevel(level0);
        await prog.SetExperience(before.Experience);
        var end = prog.Read()!;
        Check(end.Level == level0 && end.Experience == before.Experience && end.SkillPointsSpent == before.SkillPointsSpent,
            $"put back: level {level0}, experience {before.Experience}, spent {before.SkillPointsSpent}", $"{end}");
        Check(_events.Any(e => e.StartsWith("level")), "PlayerLevelChangedEvent seen", string.Join("; ", _events));
        _ = back;
    }
}
