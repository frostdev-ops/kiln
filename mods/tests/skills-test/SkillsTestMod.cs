// SPDX-License-Identifier: MIT
// kiln.skills lab test, player-free. Prints "skills-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
// skills/test.json expands the game's tree (18 nodes: 13 native, 5 shadow nodes past the game's caps, one refused; next to a smaller
// mod it is the one that gives up native slots) and
// defines a Kiln tree; this code checks them through the game's own functions, a player-template entity (1_Player,
// no client) spawned 40 m from the world's flame altar, and Kiln.Saves. A second boot checks what stayed.
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kiln;

namespace SkillsTest;

public sealed class SkillsTestMod : Mod
{
    private int _pass, _fail;
    private const uint Root = 0x8efcc472, IronParent = 0x0b61f08f;
    private const ulong FakeSteam = 76561190000004242;  // a player who is not online: Kiln-tree state round trip
    private readonly List<EntityId> _spawned = new();

    public override void OnLoad()
    {
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        if (!Client.IsDedicatedServer)
        {
            Info("client: the automatic suite runs on a dedicated (lab) server");
            return;
        }
        // the data-only path (Kiln.SkillsStartup) loads skills/test.json's "trees"; load it here too when it has not
        if (Skills.Find("skills-test:arts") is null) Skills.LoadTrees();
        RegisterCodeTree();
        OpenControl();
        _ = Run();
    }

    private bool _applied, _removed;

    private void RegisterCodeTree() => Skills.Register(new TalentTree
    {
        Id = "skills-test:code",
        Name = "Code tree",
        Points = TalentPoints.Custom(_ => 3),
        Nodes =
        [
            new TalentNode { Id = "x", Name = "Callback", Column = 0, Row = 0, Effects = [TalentEffect.Callback(_ => _applied = true, _ => _removed = true)] },
            new TalentNode { Id = "y", Name = "Needs x", Column = 1, Row = 0, Requires = ["x"] },
        ],
    });

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task Run()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(40);
            var second = File.Exists(Path.Combine(Context.DataDirectory, "first-boot-done"));
            TestGameTree();
            TestResource();
            TestSimulate();
            TestForcedTree();
            var player = await MakePlayer();
            if (!player.IsNone)
            {
                await TestShadowGamePath(player);
                await TestProgressionShadow(player);
                await TestEmberWard(player);
                await TestShadowClientSync(player);
                await TestTreeChanged(player);
                await TestKilnTreeEffects(player);
            }
            TestKilnTreeState(second);
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(Path.Combine(Context.DataDirectory, "first-boot-done"), DateTime.UtcNow.ToString("O"));
            foreach (var e in _spawned) Spawn.Destroy(e);
        }
        catch (Exception e)
        {
            Fail($"exception: {e}");
        }
        Log.Info($"skills-test done: {_pass} PASS, {_fail} FAIL");
    }

    // ---------------------------------------------------------------------------------------------- the game's tree --
    private List<GameNodeInfo> _defs = new();
    private GameNodeInfo? Def(string name) => _defs.FirstOrDefault(d => d.Name == name);

    private void TestGameTree()
    {
        var st = GameTree.Status;
        if (st is null)
        {
            Fail("kiln.skills is unavailable");
            return;
        }
        Info(st.ToString());
        Check(st.TreeExpansion && st.LoginGuard && st.ShadowLevel >= 1, "tree expansion, login guard and the shadow table are on");
        Check(st.GameNodes == 243 && st.GameLinks == 312, $"the game's resource: {st.GameNodes} nodes, {st.GameLinks} links, levelIndex up to {st.GameLevelMax}");
        _defs = GameTree.Definitions.Where(d => d.Name.StartsWith("skilltest_", StringComparison.Ordinal)).ToList();
        var inTree = _defs.Where(d => d.State == "in-tree").ToList();
        // the smaller mod keeps its slots: adding this big one to a game with the sample must not move the sample's nodes
        var sampleNodes = GameTree.Definitions.Where(d => d.Name.StartsWith("skilltreesample_", StringComparison.Ordinal)).ToList();
        if (sampleNodes.Count > 0)
            Check(sampleNodes.All(d => d.State == "in-tree" && !GameTree.ShadowNodes.Contains(d.Id)),
                $"the sample's nodes stay native next to this bigger mod: {string.Join(", ", sampleNodes.Select(d => $"{d.Name} index {d.Index}"))}");
        Check(st.FreeNodes == 0 || st.ShadowNodes == 0, $"no native slot stays free while nodes wait for one ({st.FreeNodes} free, {st.ShadowNodes} shadow)");
        Check(inTree.Count == 18 && Def("skilltest_bad")?.State == "error",
            $"18 definitions in the tree, the broken one refused ({Def("skilltest_bad")?.Error})");
        // other mods' nodes (the sample's) may be in the tree too
        var extra = GameTree.Definitions.Count(d => d.State == "in-tree" && !d.Name.StartsWith("skilltest_", StringComparison.Ordinal));
        Check(st.Nodes == 243 + 18 + extra && st.AddedNodes == 18 + extra && st.ShadowNodes >= 1 && st.Nodes - st.ShadowNodes == 256,
            $"the served tree: {st.Nodes} nodes = 243 + {st.AddedNodes} ({st.ShadowNodes} shadow nodes past the 256/100 caps), {st.Links} links");
        var dump = JsonNode.Parse(GameTree.Dump(addedOnly: true))!;
        var nodes = dump["nodes"]!.AsArray();
        var missing = nodes.Count(n => n?["missing"]?.GetValue<bool>() == true);
        var maps = dump["maps"]!;
        Check(nodes.Count == 18 + extra && missing == 0,
            $"the server world's tree has every Kiln node through the game's own NodeById (indices {nodes.Min(n => n!["index"]!.GetValue<int>())}.." +
            $"{nodes.Max(n => n!["index"]!.GetValue<int>())}); maps: {maps.ToJsonString()}");
        Check(maps["nodes"]!.GetValue<int>() == st.Nodes && maps["index"]!.GetValue<int>() == st.Nodes && maps["links"]!.GetValue<int>() == st.Links,
            $"the game built its node, index and link maps from Kiln's resource ({st.Nodes} / {st.Nodes} / links)");
        var shadowIdx = nodes.Where(n => n!["shadow"]!.GetValue<bool>()).Select(n => n!["index"]!.GetValue<int>()).ToList();
        Check(shadowIdx.Count == st.ShadowNodes && shadowIdx.Min() >= 243 + 18 + extra - st.ShadowNodes, $"shadow nodes sit behind the native ones (indices {string.Join(",", shadowIdx)})");
        var iw = nodes.FirstOrDefault(n => n!["id"]!.GetValue<string>() == $"0x{Def("skilltest_iron_will")!.Id:x8}");
        Info($"skilltest_iron_will in the game's tree: {iw?.ToJsonString()}");
    }

    private void TestResource()
    {
        var expanded = GameTree.Resource(0, out var check);
        var vanilla = GameTree.Resource(1, out _);
        var copy = GameTree.Resource(2, out var checkCopy);
        var c = JsonNode.Parse(check)!;
        Check(c["ok"]!.GetValue<bool>() && c["served"]!.GetValue<bool>(),
            $"round trip: the served resource ({expanded.Length} bytes) parses back, every game node/link/sub-array equals the game's: {check}");
        Check(vanilla.Length == 78424, $"the game's own bytes are kept ({vanilla.Length} bytes)");
        Check(JsonNode.Parse(checkCopy)!["ok"]!.GetValue<bool>(), $"the copy path without additions round-trips: {checkCopy}");
    }

    private void TestSimulate()
    {
        uint Id(string n) => Def(n)?.Id ?? 0;
        var native = _defs.First(d => d.State == "in-tree" && d.Index < 255 && d.Name.StartsWith("skilltest_bulk_", StringComparison.Ordinal));
        var shadow = Def("skilltest_deep_a")!;
        var r = GameTree.Simulate(
            (GameTreeSimOp.Unlock, native.Id),          // 0: from a root (the game: roots are always reachable)
            (GameTreeSimOp.UnlockForce, IronParent),    // 1
            (GameTreeSimOp.UnlockForce, shadow.Id),     // 2: a shadow node never enters UnlockedSkillNodes
            (GameTreeSimOp.AddRaw, 0xdeadbeef),         // 3: a node of a mod that is gone, as a save would have it
            (GameTreeSimOp.Check, 0),                   // 4
            (GameTreeSimOp.Strip, 0),                   // 5: the login guard
            (GameTreeSimOp.Lock, native.Id));           // 6
        foreach (var x in r) Info(x.ToString());
        Check(r[0].Ok && r[0].Reachable && r[0].Known && r[0].Index == native.Index && r[0].Rebuild && r[0].Connected,
            $"the game's Reachable/NodeSet/SkillUiRebuild/connectivity check take the Kiln node {native.Name} (index {r[0].Index})");
        Check(r[1].Spent == 1 && r[0].Spent == 0, $"the game's PointsSpent counts the Kiln node's cost (0) and its parent's (1): {r[1].Spent}");
        Check(!r[2].Ok, "the game's NodeSet refuses a shadow node for a list no player owns (it never goes into UnlockedSkillNodes)");
        Check(r[3].Ok && !r[4].Rebuild, "an unknown id in the list makes the game's SkillUiRebuild fail (the game would reset the whole tree at login)");
        Check(r[5].Stripped == 1 && r[5].Rebuild && r[5].LevelsValid && r[5].Connected == r[1].Connected && r[5].Spent == r[1].Spent,
            "the login guard's strip removes only it: the rebuild and level checks pass again, the rest is as before");
        Check(r[6].Ok && r[6].UnlockedNodes == 1, "the game's lock takes the Kiln node back");
    }

    // ---------------------------------------------------------------------------------------------- a player entity --
    private async Task<EntityId> MakePlayer()
    {
        WorldPosition? anchor = null;
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf)
            {
                anchor = inf.Position;
                break;
            }
        }
        if (anchor is not { } a)
        {
            Fail("no anchor entity in the world: player checks skipped");
            return default;
        }
        try
        {
            var p = await Spawn.Entity("1_Player", new WorldPosition(a.X + 40, a.Y + 0.5, a.Z + 40), 0,
                new SpawnOptions { Tag = "skills-test", Flags = SpawnFlags.Transient });
            _spawned.Add(p);
            await Steps(6);
            Info("components of the player-template entity: " + string.Join(", ", new[] { "CurrentTransform", "Level", "PlayerInput",
                "PlayerSkillsUi", "ServerConsumedPlayerInput", "UnlockedSkillNodes", "InventorySetup", "PlayerState", "Experience" }
                .Select(c => $"{c} {World.ComponentSize(p, c)}")));
            Check(World.ComponentSize(p, "UnlockedSkillNodes") == 2316 && World.ComponentSize(p, "PlayerSkillsUi") == 136,
                $"a player-template entity {p} has UnlockedSkillNodes and PlayerSkillsUi");
            return p;
        }
        catch (Exception e)
        {
            Fail($"no player-template entity: {e.Message}");
            return default;
        }
    }

    private static string InputState(EntityId p)
    {
        var inp = World.ReadBytes(p, "PlayerInput");
        var con = World.ReadBytes(p, "ServerConsumedPlayerInput");
        var u = World.ReadBytes(p, "UnlockedSkillNodes");
        return inp is null || con is null || u is null ? "no input components" :
            $"input skill 0x{BitConverter.ToUInt32(inp, 0x180):x8} unlearn {inp[0x184]} reset {inp[0x185]} version {BitConverter.ToUInt32(inp, 0x188)}, " +
            $"consumed version {BitConverter.ToUInt32(con, 0x58)}, UnlockedSkillNodes initialized {u[0]} changeCount {u[0x908]}";
    }

    private bool InSave(EntityId p, uint id)
    {
        var b = World.ReadBytes(p, "UnlockedSkillNodes");
        if (b is null) return false;
        for (var i = 0; i < 256; i++)
            if (BitConverter.ToUInt32(b, 4 + i * 4) == id) return true;
        return false;
    }

    private async Task TestShadowGamePath(EntityId p)
    {
        var a = Def("skilltest_deep_a")!;
        var b = Def("skilltest_deep_b")!;
        var q0 = GameTree.Query(p, a.Id);
        var qb0 = GameTree.Query(p, b.Id);
        Check(q0 is { Unlocked: false, Reachable: true } && qb0 is { Unlocked: false, Reachable: false },
            $"the game's IsUnlocked / Reachable answer for shadow nodes: {a.Name} locked + reachable from its root ({q0}), {b.Name} not reachable yet ({qb0})");
        var spent0 = GameTree.PointsSpent(p);
        // the game's own input handler, as the player's client would ask (PlayerInput.unlockSkillAction)
        // the game's join check waits for skill-point knowledge, which a player-template entity never has: skip it
        GameTree.GameRequest(p, 3);
        await Steps(1);
        Info($"before the request: {InputState(p)}");
        Check(GameTree.GameRequest(p, 0, a.Id), $"asked the game (player input) to unlock {a.Name}");
        for (var i = 0; i < 3; i++)
        {
            await Steps(1);
            Info($"step +{i + 1}: {InputState(p)}");
        }
        var lv = GameTree.ShadowLevels(p);
        Check(lv.GetValueOrDefault(a.Id) == 1 && !InSave(p, a.Id),
            $"the game's unlock handler put the shadow node in Kiln's table (level {lv.GetValueOrDefault(a.Id)}), not into UnlockedSkillNodes (the character save)");
        var q1 = GameTree.Query(p, a.Id);
        var qb1 = GameTree.Query(p, b.Id);
        Check(q1?.Unlocked == true && qb1?.Reachable == true, $"IsUnlocked({a.Name}) is true now, and {b.Name} (linked from it) is reachable: {qb1}");
        // a node with a cost: the player-template entity has no skill points, so the game refuses it (its own cost check)
        GameTree.GameRequest(p, 0, b.Id);
        await Steps(3);
        Check(GameTree.ShadowLevels(p).GetValueOrDefault(b.Id) == 0, $"the game's cost check refuses {b.Name} (3 points, the entity has none)");
        // PointsSpent: the shadow table adds its nodes' costs inside the game's own sum
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [a.Id] = 1, [b.Id] = 2 });
        await Steps(2);
        var spent1 = GameTree.PointsSpent(p);
        Check(spent0 is { } s0 && spent1 == s0 + 2 * 3, $"the game's PointsSpent adds the shadow table: {spent0} -> {spent1} (deep_b level 2 x cost 3)");
        var fx = GameTree.ShadowEffects(p);
        Info($"shadow effects of {p}: {string.Join(", ", fx.Select(f => $"0x{f.Node:x8} L{f.Level} entity {f.Impact}"))}");
        // unlearn through the game's path, then the respec
        // the game's unlearn charges a node's cost in respec items: with none, it undoes (as for its own nodes)
        GameTree.GameRequest(p, 1, b.Id);
        await Steps(3);
        Check(GameTree.ShadowLevels(p).GetValueOrDefault(b.Id) == 2,
            "the game's unlearn of a shadow node asks for its unlearn cost like for its own nodes: no respec item, so it put the level back (2 -> 1 -> 2)");
        // the game's rule for its own nodes holds for shadow nodes: an unlearn may not cut another node off
        GameTree.GameRequest(p, 1, a.Id);
        await Steps(3);
        Check(GameTree.ShadowLevels(p).GetValueOrDefault(a.Id) == 1, $"the game's unlearn of {a.Name} is refused while {b.Name} hangs on it");
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [a.Id] = 1 });
        await Steps(2);
        GameTree.GameRequest(p, 1, a.Id);
        await Steps(3);
        Check(GameTree.ShadowLevels(p).GetValueOrDefault(a.Id) == 0, "the game's unlearn of a free shadow node (cost 0) takes it (1 -> 0)");
        // the respec costs what is spent (the shadow table counts in the game's PointsSpent): with only a free node it is free
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [a.Id] = 1 });
        await Steps(2);
        GameTree.GameRequest(p, 2);
        await Steps(3);
        Check(GameTree.ShadowLevels(p).Count == 0, "the game's respec (reset action) clears the shadow table too");
        // the same respec with spent points: the game's paid respec asks for its respec cost (the entity has nothing)
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [a.Id] = 1, [b.Id] = 1 });
        await Steps(2);
        GameTree.GameRequest(p, 2);
        await Steps(3);
        Info($"a paid respec with 3 points spent in shadow nodes and no respec item: shadow levels now {string.Join(",", GameTree.ShadowLevels(p).Select(kv => $"0x{kv.Key:x8}={kv.Value}"))}");
        GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
        // a shadow node's id in the save (it never should be): the login guard moves it into the table
        Info($"strip records so far: {GameTree.Status?.Refunded}");
    }

    // ---------------------------------------------------------------------------- kiln.progression and shadow nodes --
    // /skillpoints, /skill list | lock | unlock | respec go through kiln.progression: its sums run on scratch masks, and
    // shadow nodes are not in UnlockedSkillNodes. (Seen: 8 unspent while the screen said 4, "not unlocked", a respec that
    // left them, "the game's unlock refused it".)
    private async Task TestProgressionShadow(EntityId p)
    {
        var a = Def("skilltest_deep_a")!;
        var b = Def("skilltest_deep_b")!;
        Progression.Diagnostics.LabPlayer(p);
        GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
        await Steps(2);
        var s0 = Progression.Get(p);
        var n0 = s0?.UnlockedNodes ?? -1;
        Check(s0 is { HasSkills: true }, $"kiln.progression reads the lab player: {s0}");
        // force: a player-template entity's total points don't read (no skill-point knowledge), so the cost check can't run here
        var u = await Progression.Unlock(p, a.Id, force: true);
        var ub = await Progression.Unlock(p, b.Id, force: true);
        Check(u.Ok && ub.Ok && GameTree.ShadowLevels(p).GetValueOrDefault(a.Id) == 1 && GameTree.ShadowLevels(p).GetValueOrDefault(b.Id) == 1,
            $"Progression.Unlock takes shadow nodes ({a.Name}: {u.Status}, {b.Name}: {ub.Status}), into Kiln's table");
        var s1 = Progression.Get(p);
        Check(s1 is not null && s0 is not null && s1.SkillPointsSpent == s0.SkillPointsSpent + 3 && s1.UnlockedNodes == n0 + 2,
            $"its points and node count include them: spent {s0?.SkillPointsSpent} -> {s1?.SkillPointsSpent} (deep_b costs 3), nodes {n0} -> {s1?.UnlockedNodes}");
        var list = Progression.UnlockedNodes(p);
        Check(list.Any(n => n.Id == a.Id && n.Level == 1) && list.Any(n => n.Id == b.Id && n.Level == 1 && n.MaxLevel == 2 && n.Cost == 3),
            $"Progression.UnlockedNodes (/skill list) lists them: {string.Join(", ", list.Select(n => $"0x{n.Id:x8} {n.Level}/{n.MaxLevel} cost {n.Cost}"))}");
        var again = await Progression.Unlock(p, a.Id, force: true);
        Check(!again.Ok && again.Reason.Contains("already"), $"unlocking a taken shadow node says so, not \"the game's unlock refused it\": {again.Reason}");
        var nl = await Progression.Lock(p, a.Id);
        Check(!nl.Ok && nl.Reason.Contains("depend"), $"/skill lock of a node another hangs on is refused: {nl.Reason}");
        var lb = await Progression.Lock(p, b.Id);
        var la = await Progression.Lock(p, a.Id);
        Check(lb.Ok && la.Ok && GameTree.ShadowLevels(p).Count == 0, $"/skill lock sees shadow nodes ({lb.Status}, {la.Status}): the table is empty");
        var missing = await Progression.Lock(p, a.Id);
        Check(!missing.Ok && missing.Reason == "not unlocked", $"a node that isn't taken: {missing.Reason}");
        await Progression.Unlock(p, a.Id, force: true);
        await Progression.Unlock(p, b.Id, force: true);
        var r = await Progression.Respec(p);
        Check(r.Ok && GameTree.ShadowLevels(p).Count == 0 && r.Applied >= 2, $"/skill respec clears the shadow table ({r.Applied} applied, kiln.log: \"(respec)\")");
        var after = await Progression.Unlock(p, a.Id, force: true);
        Check(after.Ok && GameTree.ShadowLevels(p).GetValueOrDefault(a.Id) == 1, $"and the unlock works again afterwards: {after.Status}");
        await Progression.Respec(p);
        // a force that breaks the game's rules says what will happen at the next join
        var forced = await Progression.Unlock(p, 0x4475e8dd, force: true);
        Check(forced.Ok && forced.Reason.Contains("next join"), $"a force unlock that leaves the tree unlinked warns: {forced.Reason}");
        await Progression.Respec(p);
        Progression.Diagnostics.LabPlayer(default);
        await Steps(2);
    }

    // The sample's Ember Ward says what it does. Its anchor node is an Intelligence node; only with the sample deployed.
    private async Task TestEmberWard(EntityId p)
    {
        var ward = GameTree.Definitions.FirstOrDefault(d => d.Name == "skilltreesample_ember_ward");
        var file = Mods.All.FirstOrDefault(m => m.Id == "skilltreesample")?.Directory is { } dir ? Path.Combine(dir, "skills", "sample.json") : null;
        if (ward is null || file is null || !File.Exists(file))
        {
            Info("Ember Ward: the sample isn't deployed (skipped)");
            return;
        }
        var texts = JsonNode.Parse(File.ReadAllText(file))!["gameTree"]!["nodes"]!.AsArray().First(n => n!["name"]!.GetValue<string>() == ward.Name)!["texts"]!;
        var en = texts["en"]!["description"]!.GetValue<string>();
        var de = texts["de"]!["description"]!.GetValue<string>();
        Check(en.Contains("Intelligence") && de.Contains("Intelligenz"), $"Ember Ward's texts name Intelligence: \"{en}\" / \"{de}\"");
        if (!GameTree.ShadowNodes.Contains(ward.Id))
        {
            Info("Ember Ward is a native node in this build of the tree: its effect runs through the game (client only)");
            return;
        }
        int? Attr(string c, Guid g) => Abilities.Attributes.Get(p, c, Fnv(g.ToByteArray()));
        var int0 = Attr("Intelligence", TalentAttributes.Intelligence);
        var spi0 = Attr("Spirit", TalentAttributes.Spirit);
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [ward.Id] = 1 });
        await Steps(6);
        var int1 = Attr("Intelligence", TalentAttributes.Intelligence);
        var spi1 = Attr("Spirit", TalentAttributes.Spirit);
        Check(int1 == int0 + 2 && spi1 == spi0, $"and that is what it does: Intelligence {int0} -> {int1}, Spirit {spi0} -> {spi1}");
        GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
        await Steps(4);
    }

    // The test tree's path a force-unlocking admin took: the game's connectivity check is what reset it at the next
    // join. relearn.py left out the double jump node 0xffd5f940, and 0x4475e8dd hangs on it: with it, the same list is valid.
    private static readonly uint[] ForcedPath =
    [
        0xbcc2af54, 0x6a03aac2, 0x01e263aa, 0x9a35d01f, 0xc14d4ff4, 0x3c5488aa, 0x1c3a25dc, 0x4826aafc, 0x20ea4235, 0x8dcc1e4a, 0x81d8b64f,
        0x8569bab7, 0xe887d6dc, 0x92f1b848, 0xfbcb2152, 0x35f3fd54, 0xcfa5985b, 0x7f33b22e, 0xe2b07e6b, 0x4475e8dd, 0x4b50fa5c,
    ];

    private void TestForcedTree()
    {
        var steps = ForcedPath.Select(n => (GameTreeSimOp.UnlockForce, n)).ToArray();
        var r = GameTree.Simulate(steps);
        Check(r.All(x => x.Ok) && !r[^1].Connected, $"the forced tree that characters lost at login is not connected in the game's own check ({r.Count} nodes, spent {r[^1].Spent}): the game's join check resets it");
        var fixedSteps = ForcedPath.Select(n => (GameTreeSimOp.UnlockForce, n)).Append((GameTreeSimOp.UnlockForce, 0xffd5f940u)).ToArray();
        var r2 = GameTree.Simulate(fixedSteps);
        Check(r2.All(x => x.Ok) && r2[^1].Connected, "with the double jump node it left out the same tree is connected: a valid tree passes");
    }

    // ------------------------------------------------------------------------- a tree that changed under a character --
    // Native or shadow follows from the mod list, so a character can arrive with an unlock on the other side of the line. Seen:
    // a character took Ember Ward and Emberheart 2/3 as native nodes; with skills-test added they were shadow nodes, and on the join both
    // showed Locked with 3 points back and no line in the log. The login guard had moved them to the table; the character's carried
    // copy, coming in a moment later, knew nothing of them and replaced it.
    private async Task TestTreeChanged(EntityId p)
    {
        var a = Def("skilltest_deep_a")!;
        var b = Def("skilltest_deep_b")!;
        var holder = Skills.HolderOf(new CharacterKey(0x1122334455667788, Guid.Parse("c0ffee00-0000-4000-8000-000000000001")));
        Progression.Diagnostics.LabPlayer(p);
        GameTree.LabPlayer(default);
        GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
        GameTree.GameRequest(p, 3);
        await Steps(3);

        // native -> shadow: the save still has them as native nodes
        Check(GameTree.LabPutInSave(p, a.Id, 0) && GameTree.LabPutInSave(p, b.Id, 2) && InSave(p, a.Id) && InSave(p, b.Id), $"a save from another mod list holds {a.Name} and {b.Name} 2/2 as native nodes");
        Check(GameTree.LabLoginGuard(p), "the join: the login guard handles the ids it finds");
        await Steps(3);
        var lv = GameTree.ShadowLevels(p);
        Check(lv.GetValueOrDefault(a.Id) == 1 && lv.GetValueOrDefault(b.Id) == 2 && !InSave(p, a.Id) && !InSave(p, b.Id), $"they moved to the table with their levels ({lv.GetValueOrDefault(a.Id)}, {lv.GetValueOrDefault(b.Id)}) and left the list");
        // the character's data, loaded after that, doesn't have them yet
        GameTree.LabPlayer(p, holder, forceNative: true);
        await Steps(3);
        lv = GameTree.ShadowLevels(p);
        var carried = GameTree.LabCarried(holder);
        Check(lv.GetValueOrDefault(a.Id) == 1 && lv.GetValueOrDefault(b.Id) == 2, $"the table's first load keeps them ({lv.GetValueOrDefault(a.Id)}, {lv.GetValueOrDefault(b.Id)})");
        Check(carried.GetValueOrDefault($"0x{a.Id:x8}") == 1 && carried.GetValueOrDefault($"0x{b.Id:x8}") == 2, $"and they are in the carried data, their only record now: {string.Join(",", carried.Select(kv => $"{kv.Key}={kv.Value}"))}");
        // the failure this guards against: the client's copy, taken after the guard ran, has none of it
        GameTree.LabSetCarried(holder, new Dictionary<string, int>());
        GameTree.LabReload(p);
        await Steps(3);
        lv = GameTree.ShadowLevels(p);
        carried = GameTree.LabCarried(holder);
        Check(lv.GetValueOrDefault(a.Id) == 1 && lv.GetValueOrDefault(b.Id) == 2 && carried.GetValueOrDefault($"0x{b.Id:x8}") == 2,
            $"the client's older copy coming in doesn't undo them: table {lv.GetValueOrDefault(a.Id)}, {lv.GetValueOrDefault(b.Id)}, carried {carried.Count} entries");
        // what the player does afterwards stands: a refund isn't undone by the next load
        var locked = await Progression.Lock(p, b.Id);
        await Steps(3);
        GameTree.LabReload(p);
        await Steps(3);
        Check(locked.Ok && GameTree.ShadowLevels(p).GetValueOrDefault(b.Id) == 1, $"a level the player gives back stays gone after a load: {locked.Status}, level {GameTree.ShadowLevels(p).GetValueOrDefault(b.Id)}");
        var respec = await Progression.Respec(p);
        await Steps(3);
        GameTree.LabSetCarried(holder, new Dictionary<string, int>());
        GameTree.LabReload(p);
        await Steps(3);
        Check(respec.Ok && GameTree.ShadowLevels(p).Count == 0, $"and a respec clears what came from the save: {GameTree.ShadowLevels(p).Count} left");

        // shadow -> native: the carried data has a node that this tree gives a native slot
        var native = GameTree.Definitions.Where(d => d.State == "in-tree" && !GameTree.ShadowNodes.Contains(d.Id) && d.Name.StartsWith("skilltest_bulk_", StringComparison.Ordinal)).ToList();
        var n0 = native[0];
        var n1 = native[1];
        Check(!InSave(p, n0.Id), $"{n0.Name} is a native node here, and not unlocked");
        GameTree.LabSetCarried(holder, new Dictionary<string, int> { [$"0x{n0.Id:x8}"] = 1, ["0x0badf00d"] = 1 });
        GameTree.LabReload(p);
        for (var i = 0; i < 40 && !InSave(p, n0.Id); i++) await Steps(1);
        await Steps(5);
        carried = GameTree.LabCarried(holder);
        Check(InSave(p, n0.Id) && !carried.ContainsKey($"0x{n0.Id:x8}") && GameTree.ShadowLevels(p).Count == 0,
            $"carried as a shadow node, it goes into the game's own list: in the list {InSave(p, n0.Id)}, carried: {string.Join(",", carried.Keys)}");
        Check(carried.ContainsKey("0x0badf00d"), "a node of a mod this world doesn't have stays with the character");
        // what can't be kept says so: without force the lab's entity has no points to read, so the game's rules refuse it
        GameTree.LabPlayer(p, holder, forceNative: false);
        await Steps(2);
        GameTree.LabSetCarried(holder, new Dictionary<string, int> { [$"0x{n1.Id:x8}"] = 1 });
        GameTree.LabReload(p);
        for (var i = 0; i < 60 && GameTree.LabCarried(holder).Count > 0; i++) await Steps(1);
        Check(!InSave(p, n1.Id) && GameTree.LabCarried(holder).Count == 0, $"refused by the game's rules: no unlock, the entry goes, and kiln.log says so (\"can't be taken back\" for 0x{n1.Id:x8})");
        GameTree.LabPlayer(default);
        Progression.Diagnostics.LabPlayer(default);
        await Progression.Respec(p);
        await Steps(4);
    }

    // ------------------------------------------------------------------------------- the client's copy of the table --
    // The real order at a join: the table loads first, the client's Kiln handshake finishes later. shadownet.py plays the client on
    // kiln.net's lab transport; this side plays the join (control file kiln/control/skills-test.net: "wait" while it runs).
    private async Task TestShadowClientSync(EntityId p)
    {
        var a = Def("skilltest_deep_a")!;
        var b = Def("skilltest_deep_b")!;
        GameTree.LabPlayer(p);
        GameTree.SetShadowLevels(p, new Dictionary<uint, int> { [a.Id] = 1, [b.Id] = 2 });
        await Steps(3);
        Info($"shadow client sync: table of {p}: {a.Name} 1, {b.Name} 2, no peer yet; lab transport {Network.Status.LabTransport}");
        if (!Network.Status.LabTransport)
        {
            Info("shadow client sync: skipped (no labPort in kiln/config/network.json; run mods/tests/skills-test/shadownet.py)");
            GameTree.LabPlayer(default);
            GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
            await Steps(4);
            return;
        }
        _shadowNetPlayer = p;
        _shadowNetA = a.Id;
        _shadowNetB = b.Id;
        Info("shadow client sync: waiting for shadownet.py (up to 120 s)");
        var sw = Stopwatch.StartNew();
        while (_shadowNetPlayer != default && sw.Elapsed.TotalSeconds < 120) await Steps(5);
        if (_shadowNetPlayer != default) Fail("shadow client sync: shadownet.py did not finish in 120 s");
        _shadowNetPlayer = default;
        GameTree.LabPlayer(default);
        GameTree.SetShadowLevels(p, new Dictionary<uint, int>());
        await Steps(4);
    }

    private EntityId _shadowNetPlayer;
    private uint _shadowNetA, _shadowNetB;
    private NetChannel? _ctl;

    /// <summary>Control channel for shadownet.py: Op "bind" makes the sending peer the lab player's connection, as a real peer's
    /// handshake does; "change" changes the table; "result" reports what the script saw; "done" ends the wait.</summary>
    private sealed record Ctl(string Op, string Text);
    private sealed record CtlReply(string Op, string Text);

    private void OpenControl()
    {
        if (!Network.Available) return;
        _ctl = Network.Channel("skills-test:ctl", "1", required: false).RegisterJson<Ctl>((m, ctx) =>
        {
            var reply = "ok";
            switch (m.Op)
            {
                case "bind":
                    typeof(NetPeer).GetProperty(nameof(NetPeer.Player))!.GetSetMethod(true)!.Invoke(ctx.Sender, [_shadowNetPlayer]);
                    reply = $"{_shadowNetPlayer.Value}";
                    break;
                case "change":
                    GameTree.SetShadowLevels(_shadowNetPlayer, new Dictionary<uint, int> { [_shadowNetA] = 1 });
                    break;
                case "check":
                    Check(m.Text.StartsWith("PASS", StringComparison.Ordinal), $"shadow client sync: {m.Text}");
                    break;
                case "done":
                    typeof(NetPeer).GetProperty(nameof(NetPeer.Player))!.GetSetMethod(true)!.Invoke(ctx.Sender, [EntityId.None]);
                    _shadowNetPlayer = default;
                    break;
            }
            ctx.Reply(new CtlReply(m.Op, reply));
        }, NetDirection.ToServer).RegisterJson<CtlReply>((_, _) => { }, NetDirection.ToClient);
    }

    // ---------------------------------------------------------------------------------------------- Kiln trees -------
    private async Task TestKilnTreeEffects(EntityId p)
    {
        var h = new TalentHolder(0, p);  // no Steam id: session state
        var str = new Dictionary<string, int?>();
        foreach (var c in new[] { "Strength", "Constitution", "Dexterity", "Intelligence", "Spirit", "Endurance" })
            foreach (var (name, g) in new[] { ("Strength", TalentAttributes.Strength), ("Constitution", TalentAttributes.Constitution),
                         ("Dexterity", TalentAttributes.Dexterity), ("Intelligence", TalentAttributes.Intelligence),
                         ("Spirit", TalentAttributes.Spirit), ("Endurance", TalentAttributes.Endurance) })
                if (Abilities.Attributes.Get(p, c, Fnv(g.ToByteArray())) is { } v) str[$"{c}/{name}"] = v;
        Info("attribute components x AttributeReference ids that resolve: " + string.Join(", ", str.Select(kv => $"{kv.Key}={kv.Value}")));
        var before = Abilities.Attributes.Get(p, "Strength", Fnv(TalentAttributes.Strength.ToByteArray()));
        var r = Skills.Unlock("skills-test:arts", "a", h);
        Check(r.Ok, $"Kiln tree: unlock a (cost 1, 1 point at start): {r}");
        Skills.Refresh("skills-test:arts", h);
        await Steps(4);
        var after = Abilities.Attributes.Get(p, "Strength", Fnv(TalentAttributes.Strength.ToByteArray()));
        var fx = Skills.ActiveEffects(p);
        Check(fx.Any(f => f.Node == "a" && f.Entities.Count == 1), $"the attribute effect runs the game's attribute-node program on the player: {string.Join(", ", fx.Select(f => $"{f.Tree}/{f.Node} L{f.Level} {string.Join("/", f.Entities)}"))}");
        Check(before is { } b0 && after == b0 + 2, $"Strength {before} -> {after} (+2 from the talent)");
        var lk = Skills.Lock("skills-test:arts", "a", h);
        await Steps(4);
        var back = Abilities.Attributes.Get(p, "Strength", Fnv(TalentAttributes.Strength.ToByteArray()));
        Check(lk.Ok && back == before && Skills.ActiveEffects(p).All(f => f.Node != "a"), $"lock: the effect entity is gone, Strength {after} -> {back}");
        // callbacks (code tree)
        var x = Skills.Unlock("skills-test:code", "x", h);
        await Steps(1);
        var y = Skills.Unlock("skills-test:code", "y", h);
        Skills.Respec("skills-test:code", h);
        Check(x.Ok && y.Ok && _applied && _removed, $"code tree: callbacks ran (apply {_applied}, remove at respec {_removed}), custom points 3: {y}");
    }

    private void TestKilnTreeState(bool second)
    {
        const string T = "skills-test:arts";
        var h = new TalentHolder(FakeSteam, default);
        var s = Skills.State(T, h);
        if (second)
        {
            Check(s.Levels.GetValueOrDefault("a") == 1 && s.Levels.GetValueOrDefault("b") == 1 && s.Granted == 2,
                $"after the restart: the offline character's Kiln tree state came back from Kiln.Saves (a {s.Levels.GetValueOrDefault("a")}, b {s.Levels.GetValueOrDefault("b")}, granted {s.Granted}, revision {s.Revision})");
            return;
        }
        Skills.Respec(T, h, force: true);
        s.Granted = 0;
        var p0 = Skills.Points(T, h);
        Check(p0.Total == 1 && p0.Available == 1, $"points: start 1, nothing spent: {p0}");
        var b0 = Skills.Unlock(T, "b", h);
        Check(!b0.Ok, $"b needs a: {b0.Reason}");
        var a = Skills.Unlock(T, "a", h);
        var b1 = Skills.Unlock(T, "b", h);
        Check(a.Ok && !b1.Ok, $"a takes the only point, b has no point left: {b1.Reason}");
        Skills.Grant(T, h, 2);
        var b2 = Skills.Unlock(T, "b", h);
        var la = Skills.Lock(T, "a", h);
        Check(b2.Ok && !la.Ok, $"after a grant of 2: b ok ({b2}); a cannot be given back while b needs it ({la.Reason})");
        var c = Skills.Unlock(T, "c", h);
        Check(c.Ok, $"c (needs 2 points spent in the tree): {c}");
        var lc = Skills.Lock(T, "c", h);
        Check(lc.Ok, $"c given back: {lc}");
        var st = Skills.State(T, h);
        Check(st.Levels.GetValueOrDefault("a") == 1 && st.Levels.GetValueOrDefault("b") == 1 && st.Revision > 0,
            $"state kept in Kiln.Saves player data (a 1, b 1, revision {st.Revision}); a restart checks it");
        Saves.Request();
    }

    private static uint Fnv(byte[] b)
    {
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x1000193u;
        return h;
    }

    private void Check(bool ok, string what)
    {
        if (ok) Pass(what);
        else Fail(what);
    }
    private void Pass(string s) { _pass++; Log.Info($"skills-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Warn($"skills-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"skills-test: INFO {s}");
}
