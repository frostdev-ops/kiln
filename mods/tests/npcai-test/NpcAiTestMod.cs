// SPDX-License-Identifier: MIT
// Lab test of kiln.npcai and the AI and behaviour blocks. No player: creatures are forced ready, and find (or ignore) each other by the game's own
// targeting. PASS and FAIL lines in kiln.log, then "npcai-test done: N PASS, M FAIL".
using Kiln;

namespace NpcAiTest;

public sealed class NpcAiTestMod : Mod
{
    private const string Me = "npcai-test";
    // outside the save's base (a base pacifies creatures); the blocks-npc test's spot
    private static readonly WorldPosition Pos = new(3855.8, 833.0, 1399.8);
    private readonly List<EntityId> _mine = [];
    private int _pass, _fail;
    private CreatureHandle? _peacekeeper, _hunter, _picky, _wary, _waryJson, _runner;
    private readonly List<string> _transitions = [];
    private EntityId _watched;
    private static EntityId _refused;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Info(string s) => Log.Info($"{Me}: {s}");

    public override void OnLoad()
    {
        // its own view: Scavengers are none of its business, players its friends
        _peacekeeper = (CreatureHandle)Blocks.Define("peacekeeper", new CreatureBlock
        {
            Body = Bodies.Vukah,
            AI = AIs.Vukah with { Relations = new Dictionary<Faction, Relation> { [Faction.Scavenger] = Relation.Neutral, [Faction.Player] = Relation.Friendly } },
            Stats = new CreatureStats { Level = 20 },
        });
        // a Vukah that hunts Vukahs (the game's equal factions are friends)
        _hunter = (CreatureHandle)Blocks.Define("hunter", new CreatureBlock
        {
            Body = Bodies.Vukah,
            AI = AIs.Vukah with { Sight = 30, Aggro = 12, Relations = new Dictionary<Faction, Relation> { [Faction.Vukah] = Relation.Hostile } },
            Stats = new CreatureStats { Level = 30 },
        });
        // the same, with a filter that leaves one marked Vukah alone
        _picky = (CreatureHandle)Blocks.Define("picky", new CreatureBlock
        {
            Body = Bodies.Vukah,
            AI = AIs.Vukah with
            {
                Sight = 30, Aggro = 12, SenseEvery = 0.25,
                Relations = new Dictionary<Faction, Relation> { [Faction.Vukah] = Relation.Hostile },
                SenseFilter = (npc, other) => other != _refused,
            },
            Stats = new CreatureStats { Level = 30 },
        });
        // rules: below half health it heals and calls two Vukahs; in a fight it throws ice; near death it goes immune; below a quarter it flees
        var bolt = new ProjectileBlock { Speed = 25, Range = 30, OnHit = [new EffectBlock { Damage = Damage.Ice(25) }] };
        _wary = (CreatureHandle)Blocks.Define("wary", new CreatureBlock
        {
            Body = Bodies.Vukah,
            AI = AIs.Vukah with
            {
                Sight = 30, NightSight = 0.5, Threat = 1.5, Flee = new FleeBlock { Below = 0.25, Min = 12, Max = 24, Seconds = 6 },
                Relations = new Dictionary<Faction, Relation> { [Faction.Player] = Relation.Neutral, [Faction.Scavenger] = Relation.Hostile },
            },
            Behaviour = Behaviours.VukahMelee with
            {
                Rules =
                [
                    new RuleBlock { Name = "rally", Every = 1, Cooldown = 3, When = [Is.HealthBelow(0.5), Is.SummonsBelow(2)], Do = [Act.Heal(0.1), Act.Summon("Enemy_Vukah_Melee", 2, 4, seconds: 20)] },
                    new RuleBlock { Name = "volley", Every = 1, Cooldown = 2, When = [Is.InCombat, Is.HasTarget], Do = [Act.Cast(bolt)] },
                    new RuleBlock { Name = "last-stand", Every = 0.5, When = [Is.HealthBelow(0.15)], Do = [Act.Immune()] },
                ],
            },
            Stats = new CreatureStats { Level = 20 },
        });
        // only the AI block's flee: below 30 % it runs
        _runner = (CreatureHandle)Blocks.Define("runner", new CreatureBlock
        {
            Body = Bodies.Vukah,
            AI = AIs.Vukah with { Flee = new FleeBlock { Below = 0.3, Min = 10, Max = 20, Seconds = 5, Cooldown = 5 } },
            Stats = new CreatureStats { Level = 20 },
        });
        Events.On<CreatureStateEvent>(ev =>
        {
            if (ev.Entity == _watched && _transitions.Count < 40) _transitions.Add($"{ev.From}->{ev.To} ({ev.Condition})");
        });
        _ = Run();
    }

    public override void OnUnload()
    {
        foreach (var e in _mine) try { Spawn.Destroy(e); } catch (Exception) { }
        _mine.Clear();
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(120);
            await Section("service", Service);
            await Section("data", Data);
            await Section("table", Table);
            await Section("entities", Entities);
            await Section("natural", Natural);
            await Section("rows", Rows);
            await Section("filter", Filter);
            await Section("rules", Rules);
            await Section("unload", UnloadPath);
        }
        catch (Exception e) { Check(false, $"exception: {e}"); }
        OnUnload();
        Info($"status at the end: {Factions.Status}");
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Section(string name, Func<Task> run)
    {
        Info($"---- {name}");
        try { await run(); }
        catch (Exception e) { Check(false, $"{name} threw: {e}"); }
    }

    private async Task<EntityId> Creature(string templateOrVariant, double dx, double dz, Faction? faction = null)
    {
        var e = await Creatures.Spawn(templateOrVariant, new WorldPosition(Pos.X + dx, Pos.Y, Pos.Z + dz), 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(e);
        var c = Creatures.Of(e);
        if (faction is { } f) c.Faction = f;
        c.Level = 30;
        return e;
    }

    private void Gone(params EntityId[] es)
    {
        foreach (var e in es)
            if (World.Exists(e)) Spawn.Destroy(e);
    }

    // ---- kiln.npcai is here ----

    private Task Service()
    {
        var s = Factions.Status;
        Info($"status: {s}");
        Check(s is { Available: true }, "kiln.npcai found the game's faction checks on this build");
        Check(Factions.Available, "Factions.Available");
        return Task.CompletedTask;
    }

    // ---- the data file's twin, and wrong blocks ----

    private Task Data()
    {
        var (blocks, errors) = Blocks.Report()[Me];
        foreach (var e in errors) Info($"refused: {e}");
        _waryJson = Blocks.Find("wary-json") as CreatureHandle;
        Check(_waryJson != null && !errors.Any(e => e.Contains("wary-json", StringComparison.Ordinal)), $"blocks/creatures.json's wary-json compiles ({blocks.Count} block(s))");
        void Named(string block, string field)
        {
            var e = errors.FirstOrDefault(x => x.Contains($" {block},", StringComparison.Ordinal) || x.Contains($" {block}:", StringComparison.Ordinal) || x.Contains($".{block}.", StringComparison.Ordinal));
            Check(e != null && e.Contains(field, StringComparison.Ordinal), $"{block} is refused by block and field: \"{e}\"");
        }
        Named("bad-flee", "flee.below");
        Named("bad-relation", "relations");
        Named("bad-filter", "senseFilter");
        Named("bad-summon", "summon");
        Named("bad-chance", "chance");
        if (_waryJson != null && Blocks.Lowered("wary-json", Me) is { } sj && Blocks.Lowered("wary", Me) is { } sc
            && System.Text.Json.Nodes.JsonNode.Parse(sj) is { } lj && System.Text.Json.Nodes.JsonNode.Parse(sc) is { } lc)
        {
            var a = lj["arsenal"]?["rules"]?.ToJsonString().Replace("wary-json", "X", StringComparison.Ordinal);
            var b = lc["arsenal"]?["rules"]?.ToJsonString().Replace("wary", "X", StringComparison.Ordinal);
            Check(a != null && a == b, $"blocks/creatures.json's rules lower to what the C# wary's do ({a?.Length} characters)");
            // the arsenal's GUID follows from the block's name; everything else is the same
            static string? Values(System.Text.Json.Nodes.JsonNode n) { var v = n["creature"]?["variantValues"]?.DeepClone(); v?["Ai"]?.AsObject().Remove("EnemyArsenalReference.arsenal"); return v?.ToJsonString(); }
            var va = Values(lj);
            var vb = Values(lc);
            Check(va != null && va == vb, $"and its AI values and switches too: {va}");
        }
        return Task.CompletedTask;
    }

    // ---- the world's table, through the game's own functions ----

    private async Task Table()
    {
        Check(Factions.Fights(Faction.Vukah, Faction.Scavenger) && !Factions.Friends(Faction.Vukah, Faction.Scavenger), "the game's own answer: Vukahs fight Scavengers");
        Check(!Factions.Fights(Faction.Vukah, Faction.WildBeast), "the game's species rule: Vukahs leave wild beasts alone");
        Check(!Factions.Fights(Faction.Vukah, Faction.Vukah) && Factions.Friends(Faction.Vukah, Faction.Vukah), "equal factions are friends");
        Factions.Relate(Faction.Vukah, Faction.Scavenger, Relation.Friendly);
        Check(!Factions.Fights(Faction.Vukah, Faction.Scavenger) && Factions.Friends(Faction.Vukah, Faction.Vukah) && Factions.Friends(Faction.Scavenger, Faction.Vukah),
            "Relate(Vukah, Scavenger, Friendly): the game's checks say friends, both ways");
        Factions.Relate(Faction.Vukah, Faction.Vukah, Relation.Hostile);
        Check(Factions.Fights(Faction.Vukah, Faction.Vukah), "Relate(Vukah, Vukah, Hostile): a Vukah would fight a Vukah");
        Factions.Relate(Faction.Fogger, Faction.Player, Relation.Neutral, mutual: false);
        Check(!Factions.Fights(Faction.Fogger, Faction.Player) && Factions.Fights(Faction.Player, Faction.Fogger), "a one-way relation stays one way");
        Check(Factions.Get(Faction.Fogger, Faction.Player) == Relation.Neutral && Factions.Get(Faction.Player, Faction.Fogger) == Relation.Game, "Factions.Get reads the table");
        await Steps(2);
        Info($"status: {Factions.Status}");
        Check(Factions.Status is { Installed: true, Relations: 4 }, "the hooks went in with the first relation; 4 cells set");
        Factions.Reset();
        Check(Factions.Fights(Faction.Vukah, Faction.Scavenger) && !Factions.Fights(Faction.Vukah, Faction.Vukah), "Reset gives every pair back to the game");
        await Steps(2);
        if (Factions.Status is { Relations: 0, CreatureRows: 0, Pairs: 0 } st)
            Check(!st.Installed, $"and the hooks come out when nothing is set: {st}");
        else
            Info($"another mod holds relations, so the hooks stay in: {Factions.Status}");
    }

    // ---- a creature's own relations, through the game's checks on the two entities ----

    private async Task Entities()
    {
        var v = await Creature("Enemy_Vukah_Melee", 0, 0);
        var s = await Creature("Enemy_Scavenger_Melee01", 3, 0);
        var p = await _peacekeeper!.Spawn(new WorldPosition(Pos.X, Pos.Y, Pos.Z + 3), 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(p);
        await Steps(3);
        var (vs, _) = Factions.Between(v, s);
        var (ps, pf) = Factions.Between(p, s);
        var (sp, _) = Factions.Between(s, p);
        Info($"status: {Factions.Status}");
        Check(vs, "a game Vukah would fight the Scavenger");
        Check(!ps && !pf, "the peacekeeper (its own relations: Scavenger neutral) would not, and does not count it a friend");
        Check(sp, "the Scavenger's own view of the peacekeeper is still the game's: relations are one creature's view");
        Check(Factions.Status is { CreatureRows: >= 1, Mapped: >= 1 }, $"the peacekeeper's row is mapped onto its Faction component: {Factions.Status}");
        Gone(v, s, p);
        await Steps(3);
        Check(Factions.Status is { CreatureRows: 0 }, "a creature that is gone takes its relations with it");
    }

    // ---- the game's own targeting, with nobody telling them to fight ----

    private async Task<(int Steps, EntityId Target)> Watch(EntityId a, EntityId b, int steps, string label)
    {
        var ca = Creatures.Of(a);
        var cb = Creatures.Of(b);
        ca.ForceReady();
        cb.ForceReady();
        int first = -1;
        EntityId seen = default;
        for (var i = 0; i < steps; i++)
        {
            await Steps(1);
            var t = ca.Inspect()?.Target ?? default;
            if (first < 0 && t == b)
            {
                first = i;
                seen = t;
            }
            if (i % 100 == 0)
            {
                var x = ca.Inspect();
                var y = cb.Inspect();
                Info($"{label} step {i}: a {x?.State} target {x?.Target} hp {x?.Health}/{x?.HealthMax}; b {y?.State} target {y?.Target} hp {y?.Health}/{y?.HealthMax}");
            }
        }
        return (first, seen);
    }

    private async Task Natural()
    {
        // without Kiln: a Vukah and a Scavenger side by side
        var v = await Creature("Enemy_Vukah_Melee", 0, 0);
        var s = await Creature("Enemy_Scavenger_Melee01", 4, 0);
        var (n0, _) = await Watch(v, s, 300, "game");
        Check(n0 >= 0, $"forced ready, a Vukah finds a Scavenger by itself (step {n0}): the lab's targeting runs");
        Gone(v, s);
        await Steps(5);

        // the world's table: Vukahs and Scavengers ignore each other
        Factions.Relate(Faction.Vukah, Faction.Scavenger, Relation.Neutral);
        var before = Factions.Status!;
        v = await Creature("Enemy_Vukah_Melee", 0, 0);
        s = await Creature("Enemy_Scavenger_Melee01", 4, 0);
        var (n1, _) = await Watch(v, s, 300, "neutral");
        var after = Factions.Status!;
        Info($"table answers during it: {after.ByTable - before.ByTable}, calls {after.Calls - before.Calls}");
        Check(n1 < 0 && Creatures.Of(s).Inspect()?.Target != v, "with Relate(Vukah, Scavenger, Neutral) neither takes the other as its target in 300 steps");
        Check(after.ByTable > before.ByTable, $"the game's targeting asked, and Kiln's table answered ({after.ByTable - before.ByTable} times)");
        Factions.Reset();
        Gone(v, s);
        await Steps(5);
    }

    private async Task Rows()
    {
        // two game Vukahs: friends, nobody fights
        var v1 = await Creature("Enemy_Vukah_Melee", 0, 0);
        var v2 = await Creature("Enemy_Vukah_Melee", 4, 0);
        var (n0, _) = await Watch(v1, v2, 200, "vukahs");
        Check(n0 < 0, "two game Vukahs leave each other alone");
        Gone(v1, v2);
        await Steps(5);
        // the hunter's own relations: Vukahs are its enemies
        var before = Factions.Status!;
        var h = await _hunter!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(h);
        Creatures.Of(h).Level = 40;
        var v = await Creature("Enemy_Vukah_Melee", 4, 0);
        var (n1, _) = await Watch(h, v, 300, "hunter");
        var after = Factions.Status!;
        Check(n1 >= 0, $"the hunter (Relations: Vukah hostile) takes a game Vukah as its target by itself (step {n1})");
        Check(after.ByRow > before.ByRow, $"the game's targeting asked about the hunter, and its row answered ({after.ByRow - before.ByRow} times): the game passes the components Kiln mapped");
        Gone(h, v);
        await Steps(5);
    }

    private async Task Filter()
    {
        var f0 = AiStats.FilterCalls;
        var p = await _picky!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(p);
        var v = await Creature("Enemy_Vukah_Melee", 4, 0);
        _refused = v;
        var before = Factions.Status!;
        var (n1, _) = await Watch(p, v, 300, "picky");
        var after = Factions.Status!;
        var (fights, _) = Factions.Between(p, v);
        Check(n1 < 0 && !fights, "the picky one's SenseFilter refuses this Vukah: never its target, the game's check says no fight");
        Check(after.ByPair > before.ByPair, $"the game's targeting asked, and the filter's pair answered ({after.ByPair - before.ByPair} times)");
        Check(AiStats.FilterCalls > f0, $"the filter ran on the tick thread ({AiStats.FilterCalls - f0} calls)");
        _refused = default;   // now it may
        var (n2, _) = await Watch(p, v, 300, "picky-allowed");
        Check(n2 >= 0, $"once the filter allows it, the picky one takes it as its target (step {n2})");
        Gone(p, v);
        await Steps(5);
    }

    // ---- rules: heal, summon, cast, immune, flee ----

    private async Task Rules()
    {
        var w = await _wary!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(w);
        var o = await Creature("Enemy_Scavenger_Melee01", 2, 0);
        var cw = Creatures.Of(w);
        var co = Creatures.Of(o);
        co.Level = 60;
        cw.ForceReady();
        co.ForceReady();
        await Steps(10);
        var hpMax = cw.Inspect()?.HealthMax ?? 0;
        var s0 = AiStats.Summons;
        var c0 = AiStats.Casts;
        var oh0 = co.Inspect()?.Health ?? 0;
        var lowestO = oh0;
        Combat.SetHealth(w, (int)(hpMax * 0.45));
        int healed = 0, last = cw.Inspect()?.Health ?? 0, inCombat = 0;
        for (var i = 0; i < 400; i++)
        {
            if (i % 40 == 0)
            {
                cw.Command.Attack(o, 5000);
                co.Command.Attack(w, 5000);
            }
            await Steps(1);
            var me = cw.Inspect();
            if (me != null)
            {
                if (me.Health > last + hpMax * 0.05) healed++;
                last = me.Health;
                if (me.State == EnemyState.Combat) inCombat++;
            }
            if (co.Inspect() is { } oi) lowestO = Math.Min(lowestO, oi.Health);
            if (i == 60)
                foreach (var e in AiStats.SummonsOf(w)) Creatures.Of(e).ForceReady();   // no player near: the lab's summons wait to be ready like any creature
            if (i % 100 == 0) Info($"rules step {i}: wary {me?.State} hp {me?.Health}/{hpMax} target {me?.Target}; scavenger hp {co.Inspect()?.Health}; summons {AiStats.Summons - s0}, casts {AiStats.Casts - c0}");
        }
        var summons = AiStats.Summons - s0;
        Check(healed >= 1, $"below half health its rule heals it by a tenth ({healed} time(s))");
        Check(summons is 2, $"and calls exactly two Vukahs while two live (Is.SummonsBelow(2)): {summons}");
        var mine = AiStats.SummonsOf(w).ToList();
        Check(mine.Count == 2 && mine.All(e => Creatures.Of(e).Faction == Faction.Vukah), $"the summons stand around it, on its side: {mine.Count}");
        Check(mine.Any(e => Creatures.Of(e).Inspect()?.Target == o), "and go after its target");
        Check(AiStats.Casts - c0 > 0, $"in a fight it casts its ice ({AiStats.Casts - c0} casts)");
        Check(lowestO < oh0, $"its casts and blows land: the Scavenger fell from {oh0} to {lowestO}");
        _mine.AddRange(mine);

        // near death: immune
        // near death: immune (the game's CombatFlags.immune, which the game's impacts read; kiln.combat's direct hits do not)
        var immune = ImmuneOffset();
        var before = immune >= 0 && World.ReadBytes(w, "CombatFlags") is { } b0 && b0.Length > immune ? b0[immune] : -1;
        Combat.SetHealth(w, Math.Max(1, (int)(hpMax * 0.1)));
        await Steps(40);
        var after = immune >= 0 && World.ReadBytes(w, "CombatFlags") is { } b1 && b1.Length > immune ? b1[immune] : -1;
        Check(before == 0 && after == 1, $"below 15% its rule makes it immune: CombatFlags.immune {before} -> {after} (+0x{immune:x})");
        Gone(w, o);
        foreach (var e in mine) Gone(e);
        await Steps(5);

        // the AI block's flee, on a creature with no other rules
        var r = await _runner!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(r);
        o = await Creature("Enemy_Scavenger_Melee01", 2, 0);
        var cr = Creatures.Of(r);
        cr.ForceReady();
        Creatures.Of(o).ForceReady();
        _watched = r;
        await Steps(60);
        var rmax = cr.Inspect()?.HealthMax ?? 0;
        Combat.SetHealth(r, (int)(rmax * 0.2));
        var states = new Dictionary<EnemyState, int>();
        for (var i = 0; i < 240; i++)
        {
            if (i % 40 == 0) cr.Command.Attack(o, 5000);
            await Steps(1);
            if (cr.Inspect() is { } me) states[me.State] = states.GetValueOrDefault(me.State) + 1;
        }
        Info($"runner states below 30 %: {string.Join(", ", states.Select(x => $"{x.Key} x{x.Value}"))}; transitions {string.Join(", ", _transitions)}");
        Check(states.ContainsKey(EnemyState.Flee) || states.ContainsKey(EnemyState.FleeHome) || states.ContainsKey(EnemyState.WaitAfterFlee),
            "below 30 % its AI block's flee rule sends it into the game's Flee");
        _watched = default;
        Gone(r, o);
        await Steps(5);
    }

    private static int ImmuneOffset()
    {
        var t = Content.Templates.Get("Enemy_Vukah_Melee");
        long A(string path) => t?.GetJson(path)?["address"]?.GetValue<long>() ?? -1;
        var a = A("CombatFlags");
        var f = A("CombatFlags.immune");
        return a > 0 && f >= a ? (int)(f - a) : -1;
    }

    // ---- what a mod set goes with it ----

    private async Task UnloadPath()
    {
        Factions.Relate(Faction.Cyclops, Faction.Drak, Relation.Hostile);
        Check(Factions.Get(Faction.Cyclops, Faction.Drak) == Relation.Hostile, "a relation set before the end");
        Factions.Reset();
        await Steps(2);
        Check(Factions.Get(Faction.Cyclops, Faction.Drak) == Relation.Game, "Reset takes it back");
    }
}
