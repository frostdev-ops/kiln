// SPDX-License-Identifier: MIT
// Lab test of creature and NPC blocks. No player: creatures are forced ready and fight each other; a brain's body walks under its
// kiln.creature_control lease ("brain moves"). Start it twice: the second run checks that the ids
// stayed and that the NPCs written for the game's start were built. PASS and FAIL lines in kiln.log, then "blocks-npc-test done: N PASS, M FAIL".
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace BlocksNpcTest;

public sealed class BlocksNpcTestMod : Mod
{
    private const string Me = "blocks-npc-test";
    // where forced-ready creatures fight in the lab's save (outside the base, which pacifies them)
    private static readonly WorldPosition Pos = new(3855.8, 833.0, 1399.8);
    private readonly List<EntityId> _mine = [];
    private int _pass, _fail;
    private CreatureHandle? _stoneling, _vukahMind, _brainy, _walker;
    internal static long CsCount, JsonCount, BrainTicks, BrainAttacks, BrainSaw;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{Me}: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Info(string s) => Log.Info($"{Me}: {s}");

    public override void OnLoad()
    {
        // what the data files name by name
        Blocks.Condition("always-json", _ => true, 0.5);
        Blocks.Action("count-json", _ => Interlocked.Increment(ref JsonCount));

        // the twins of blocks/creatures.json
        var blow = Blocks.Define("stone-blow", new EffectBlock { Damage = Damage.Blunt(30), Knockback = Knockback.Small });
        Blocks.Define("stone-fighter", new BehaviourBlock
        {
            Phases = ["angry"],
            Attacks = [new AttackBlock { Name = "slam", Plays = "slam", Range = 3, HitAt = 0.6, OnHit = [blow] }],
            Rules =
            [
                new RuleBlock { Name = "angry", Every = 0.5, Cooldown = 5, When = [Is.HealthBelow(0.95)], Do = [Act.SetPhase("angry"), Act.Effect(new EffectBlock { Heal = 40 }, NpcEffectOn.Self)] },
                new RuleBlock { Name = "count", Every = 0.5, When = [Is.Custom(_ => true, 0.5)], Do = [Act.Custom(_ => Interlocked.Increment(ref CsCount))] },
            ],
        });
        _stoneling = (CreatureHandle)Blocks.Define("stoneling", new CreatureBlock
        {
            Name = "Stoneling", Body = "stone-body", Animation = "stone-moves",
            AI = AIs.Vukah with { Sight = 25, SightAngle = 80, Aggro = 6, Faction = Faction.Vukah },
            Behaviour = "stone-fighter", Stats = new CreatureStats { Level = 10 },
        });
        _vukahMind = (CreatureHandle)Blocks.Define("vukah-mind", new CreatureBlock
        {
            Body = Bodies.Vukah,
            Behaviour = Behaviours.VukahMelee with { Rules = [new RuleBlock { Name = "count", Every = 0.5, When = [Is.Custom(_ => true, 0.5)], Do = [Act.Custom(_ => Interlocked.Increment(ref CsCount))] }] },
            Stats = new CreatureStats { Level = 12 },
        });
        Blocks.Define("tobin", new NpcBlock
        {
            Name = "Tobin", Body = Bodies.Fisher, TalkName = "tobin-talk",
            Vendor = new VendorBlock { Sells = { [GameItemName("Material_T1_Plant_Fibre")] = Price.Of(GameItemName("Material_T1_Twigs"), 2) } },
            Roster = new RosterBlock { SummonTool = "Tobin's Whistle", Description = "A wanderer who trades a little." },
        });

        // a brain: the game's AI off, this code on
        _brainy = (CreatureHandle)Blocks.Define("stone-brain", new CreatureBlock
        {
            Name = "Stone brain", Body = "stone-body", Animation = "stone-moves", AI = AIs.Vukah with { Sight = 20, Faction = Faction.Vukah },
            Behaviour = new BehaviourBlock { Attacks = [new AttackBlock { Name = "slam", Plays = "slam", Range = 3, HitAt = 0.6, OnHit = [blow] }], Brain = () => new StoneMind() },
        });

        // a brain on a game body (the Vukah's model and graph), for the walking checks
        _walker = (CreatureHandle)Blocks.Define("walker-brain", new CreatureBlock
        {
            Name = "Walker brain", Body = Bodies.Vukah, AI = AIs.Vukah with { Sight = 20, Faction = Faction.Vukah },
            Behaviour = new BehaviourBlock { Brain = () => new StoneMind() },
        });

        // an NPC whose dialogue is defined here, as quests define them in code
        Blocks.Define("mira", new NpcBlock
        {
            Name = "Mira", Body = Bodies.Villager,
            Talk = new DialogueDefinition { Id = "mira-talk", Start = "hi", Nodes = { ["hi"] = new DialogueNode { Text = "The stones walk at night." } } },
        });
        _ = Run();
    }

    private static ItemRef GameItemName(string name) => new(name);

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
            await Section("mistakes", Mistakes);
            await Section("twins", Twins);
            await Section("data", Data);
            await Section("ids", Ids);
            await Section("fight", Fight);
            await Section("reactions", Reactions);
            await Section("game body", GameBody);
            await Section("brain", Brain);
            await Section("brain moves", BrainMoves);
            await Section("npc", Npc);
            await Section("clones", Clones);
        }
        catch (Exception e) { Check(false, $"exception: {e}"); }
        OnUnload();
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Section(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name} threw: {e}"); }
    }

    private static string Refused(Action define)
    {
        try { define(); return ""; }
        catch (BlockError e) { return e.Message; }
    }

    // ---- wrong blocks ----

    private Task Mistakes()
    {
        var (blocks, errors) = Blocks.Report()[Me];
        foreach (var e in errors) Info($"refused: {e}");
        void Named(string block, params string[] words)
        {
            var e = errors.FirstOrDefault(x => x.Contains($" {block},", StringComparison.Ordinal) || x.Contains($" {block}:", StringComparison.Ordinal));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block} is refused by block and field: \"{e}\"");
        }
        Named("bad-plays", "behaviour.attacks[0].plays", "slam2", "slam", "idle");
        Named("bad-mixed", "animation", "skeleton");
        Named("bad-moves", "animation.walk", "moves");
        Named("bad-keeps", "behaviour", "Enemy_Vukah_Melee");
        Named("bad-skeleton", "animation", "ghost", "stoneling");
        Named("bad-uncooked", "animation", "not cooked");
        Named("bad-phase", "behaviour.rules[0].do[0]", "calm");
        Named("bad-empty-attack", "behaviour.attacks[0].onHit");
        Named("bad-spawn", "spawns[0].replaces", "Enemy_No_Such_Creature");
        Named("bad-level", "stats.level", "99");
        Named("no-idle", "idle");
        Named("bad-npc-body", "body.model");
        string[] wrong = ["bad-plays", "bad-mixed", "bad-moves", "bad-keeps", "bad-skeleton", "bad-uncooked", "bad-phase", "bad-empty-attack", "bad-spawn", "bad-level", "no-idle", "bad-npc-body"];
        var made = wrong.Where(blocks.Contains).ToList();
        Check(made.Count == 0, $"none of the {wrong.Length} wrong blocks became anything: {string.Join(", ", made)}");
        Check(CreatureVariants.Get($"{Me}:bad-plays") == null && CreatureVariants.Get($"{Me}:bad-level") == null, "and no variant of theirs exists");

        var cs = Refused(() => Blocks.Define("cs-bad-plays", new CreatureBlock
        {
            Body = "stone-body", Animation = "stone-moves",
            Behaviour = new BehaviourBlock { Attacks = [new AttackBlock { Name = "x", Plays = "slam2", OnHit = [new EffectBlock { Heal = 1 }] }] },
        }));
        Check(cs.Contains("creature cs-bad-plays", StringComparison.Ordinal) && cs.Contains("behaviour.attacks[0].plays", StringComparison.Ordinal) && cs.Contains("slam2", StringComparison.Ordinal),
            $"C#: an attack playing a state the graph lacks throws with block and field: \"{cs}\"");
        cs = Refused(() => Blocks.Define("cs-run-plays", new CreatureBlock
        {
            Body = "stone-body", Animation = "stone-moves",
            Behaviour = new BehaviourBlock { Attacks = [new AttackBlock { Plays = AnimState.Run, OnHit = [new EffectBlock { Heal = 1 }] }] },
        }));
        Check(cs.Contains("plays", StringComparison.Ordinal) && cs.Contains("Run", StringComparison.Ordinal), $"C#: the engine's Run can't be played by a sequence (no GUID): \"{cs}\"");
        cs = Refused(() => Blocks.Define("cs-inline", new CreatureBlock
        {
            Body = "stone-body",
            Animation = new AnimationBlock { Clips = "models/stoneling.glb", Idle = "Idle", Walk = "Walk", Run = "Run" },
        }));
        Check(cs.Contains("animation", StringComparison.Ordinal) && cs.Contains("not cooked", StringComparison.Ordinal) && cs.Contains("blocks/*.json", StringComparison.Ordinal),
            $"C#: an animation with files written in code is refused, saying where it belongs: \"{cs}\"");
        cs = Refused(() => Blocks.Define("cs-bad-npc", new NpcBlock { Name = "x", Body = Bodies.Fisher, Vendor = new VendorBlock() }));
        Check(cs.Contains("vendor.sells", StringComparison.Ordinal), $"C#: a vendor that sells nothing: \"{cs}\"");
        return Task.CompletedTask;
    }

    // ---- C# and JSON are the same objects ----

    private Task Twins()
    {
        static string Normal(string? json) => Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID").Replace("-json", "").Replace("\r", "");
        foreach (var name in new[] { "stoneling", "vukah-mind", "tobin", "stone-blow" })
        {
            var cs = Normal(Blocks.Lowered(name, Me));
            var json = Normal(Blocks.Lowered($"{name}-json", Me));
            Check(cs.Length > 10 && cs == json, $"blocks/creatures.json lowers to what the C# {name} does ({cs.Length} characters)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
        Info($"stoneling lowers to: {Blocks.Lowered("stoneling", Me)}");
        return Task.CompletedTask;
    }

    // ---- what they became ----

    private Task Data()
    {
        var low = JsonNode.Parse(Blocks.Lowered("stoneling", Me)!)!;
        var graph = Guid.Parse(low["creature"]!["graph"]!.GetValue<string>());
        var model = Guid.Parse(low["creature"]!["model"]!.GetValue<string>());
        Check(graph == BlockIds.Derive(Me, "graph", "stone-moves.stone-body"), $"its graph is the cooked one with the body's slots: {graph}");
        Check(model == BlockIds.Derive(Me, "model", "models/stoneling"), $"its model is the cooked one: {model}");
        Check(low["creature"]!["clients"]!.GetValue<string>() == "mod", "a body of its own asks clients: mod");
        var t = Content.Templates.Get($"{Me}.stoneling");
        Check(t != null, $"its template is a template of its own: {t?.Guid}");
        if (t != null)
        {
            Check(t.Get("AnimationGraphResourceComponent2.animationGraph").ToString() == graph.ToString(), $"the template plays the cooked graph: {t.Get("AnimationGraphResourceComponent2.animationGraph")}");
            Check(t.Get("ModelResource.model").ToString() == model.ToString(), $"and draws the cooked model: {t.Get("ModelResource.model")}");
            Check(t.Get("Enemy01BehaviorState.patrolSetup.patrolAnimation").ToString() == "a26eaa64-1902-496c-bc67-070c87a32303", $"a state the AI names by field is the animation's Walk: {t.Get("Enemy01BehaviorState.patrolSetup.patrolAnimation")}");
            Info($"template: scale {t.Get("ScaleComponent.uniformScale")}, faction {t.Get("Faction.faction")}, sight {t.Get("Enemy01BehaviorState.combatCommitmentSetup.view.range")} (candidates within {t.Get("Enemy01BehaviorState.aggroSetup.viewRange")}), path radius {t.Get("PathWalker.horizontalRadius")}, " +
                 $"capsule {t.GetJson("ColliderResourceComponent.colliders[0].dataArray[CapsuleColliderData#0].shape")?["value"]?.ToJsonString()}, bounds max y {t.Get("Enemy01BehaviorState.defaultLocalBoundingBoxMax.y")}");
            Check(Math.Abs(t.GetNumber("Enemy01BehaviorState.combatCommitmentSetup.view.range") - 25) < 0.01 && Math.Abs(t.GetNumber("ScaleComponent.uniformScale") - 1.1) < 0.01,
                "the AI's sight (the view range the game's sight check reads) and the body's scale are the template's");
            Check(Math.Abs(t.GetNumber("Enemy01BehaviorState.combatCommitmentSetup.view.coneHalfAngle") - 80) < 0.01 && Math.Abs(t.GetNumber("Enemy01BehaviorState.combatCommitmentSetup.gain.inRange.range") - 6) < 0.01,
                $"its view cone and the distance it engages at are the fields the game reads: {t.Get("Enemy01BehaviorState.combatCommitmentSetup.view.coneHalfAngle")}, {t.Get("Enemy01BehaviorState.combatCommitmentSetup.gain.inRange.range")}");
            var vukah = Content.Templates.Get("Enemy_Vukah_Melee");
            Check(vukah != null && t.Get("Enemy01BehaviorState.aggroSetup.viewRange").ToString() == vukah.Get("Enemy01BehaviorState.aggroSetup.viewRange").ToString(),
                $"a sight within 64 m leaves the search for candidates as it is: {t.Get("Enemy01BehaviorState.aggroSetup.viewRange")}");
        }
        var arsenal = _stoneling!.Arsenal!.Value;
        var info = Arsenals.Info(arsenal);
        var seq = BlockIds.Derive(Me, "sequence", "stoneling.slam");
        Check(info is { Attacks.Count: 1 } && info.Attacks[0].Sequence == seq && info.Rules.Count == 2, $"its arsenal has its attack, playing its own sequence, and its 2 rules: {info?.Attacks.Count} attack(s), {info?.Rules.Count} rule(s), problems {string.Join("; ", info?.Problems ?? [])}");
        var s = Content.Sequences.Get($"{Me}.stoneling.slam");
        Check(s != null && s.Get("events[SetAnimationEvent#0].animationName").ToString() == BlockIds.Derive(Me, "state", "slam").ToString(), $"the attack's sequence sets the attack's state: {s?.Get("events[SetAnimationEvent#0].animationName")}");
        Info($"slam sequence: length {s?.Get("length")}, spawn at {s?.Get("events[SpawnEntityEvent#0].time")}, places {s?.Get("events[SpawnEntityEvent#0].templateReference")}");
        var mind = JsonNode.Parse(Blocks.Lowered("vukah-mind", Me)!)!;
        Check(mind["creature"]!["clients"]!.GetValue<string>() == "none" && mind["creature"]!["template"]!.GetValue<string>() == "Enemy_Vukah_Melee",
            "a game body with a new mind stays the game's creature and asks clients for nothing");
        return Task.CompletedTask;
    }

    private Task Ids()
    {
        var file = Path.Combine(Context.DataDirectory, "ids.txt");
        var now = string.Join("\n", new[] { "stoneling", "vukah-mind", "stone-brain", "tobin", "stone-moves", "stone-body" }
            .Select(n => $"{n} {Blocks.Find(n, Me)?.Guid}") .Append($"arsenal {_stoneling?.Arsenal}").Append($"variant {_stoneling?.Variant}"));
        if (File.Exists(file))
        {
            var before = File.ReadAllText(file);
            Check(before == now, $"after the restart the ids are the same: {now.Replace('\n', ',')}");
        }
        else
        {
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(file, now);
            Info($"first run: ids written to {file}; start again for the restart check");
        }
        return Task.CompletedTask;
    }

    // ---- a creature of our own files ----

    private async Task<EntityId> Opponent(double dx, double dz)
    {
        var e = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(Pos.X + dx, Pos.Y, Pos.Z + dz), 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(e);
        var c = Creatures.Of(e);
        c.Faction = Faction.PlayerAlly;
        c.Level = 60;
        c.ForceReady();
        return e;
    }

    private static uint StateOf(EntityId e) => World.ReadBytes(e, "ServerAnimationGraphInput") is { Length: > 0x230 } b ? BitConverter.ToUInt32(b, 0x22c) : 0;

    private async Task Fight()
    {
        var s = await _stoneling!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(s);
        await Steps(30);
        var info = Spawn.Inspect(s);
        Check(info?.Template == Content.Templates.Get($"{Me}.stoneling")?.Guid, $"a stoneling spawns as its own template: {info?.Template}");
        var g = World.ReadBytes(s, "ServerAnimationGraphComponent2");
        Check(g != null && BitConverter.ToInt32(g, 0) != 0, $"with a running graph: handle {(g == null ? 0 : BitConverter.ToInt32(g, 0))}");
        Check(StateOf(s) == _stoneling.States["Idle"], $"standing in the engine's Idle: 0x{StateOf(s):x8}");
        var c = Creatures.Of(s);
        Check(c.Arsenal == _stoneling.Arsenal, $"its arsenal is the block's: {c.Arsenal}");
        Check(CreatureVariants.Of(s)?.Id == _stoneling.Variant && c.Level == 10, $"it is the block's variant, level {c.Level}");
        Check(c.Features.Disabled.HasFlag(EnemyFeatures.UseTaunt), $"the base creature's own motions (taunt, block, spawn sequence) are off: {c.Features.Disabled}");

        var o = await Opponent(2, 0);
        c.Level = 60;
        c.ForceReady();
        var slamSeq = BlockIds.Fnv(BlockIds.Derive(Me, "sequence", "stoneling.slam"));
        var slamState = _stoneling.States["slam"];
        var angry = RuleIds.Fnv(RuleIds.Derive(Me, "phase", "angry"));
        long seqSteps = 0, stateSteps = 0, staggerSteps = 0, cs0 = Interlocked.Read(ref CsCount);
        var staggerState = _stoneling.States["Stagger"];
        var lowest = 1.0;
        int healed = 0, last = c.Inspect()?.Health ?? 0;
        var phase = false;
        for (var i = 0; i < 900 && World.Exists(s); i++)
        {
            if (i % 40 == 0 && World.Exists(o))
            {
                c.Command.Attack(o, 5000);
                Creatures.Of(o).Command.Attack(s, 5000);
            }
            await Steps(1);
            if (Abilities.SequenceOf(s) is { Sequence: var q } && q == slamSeq) seqSteps++;
            if (StateOf(s) == slamState) stateSteps++;
            if (StateOf(s) == staggerState) staggerSteps++;
            if (World.Exists(o) && Creatures.Of(o).Inspect() is { HealthMax: > 0 } oh) lowest = Math.Min(lowest, (double)oh.Health / oh.HealthMax);
            if (c.Inspect() is { } me)
            {
                if (me.Health > last + 20) healed++;
                last = me.Health;
            }
            phase |= World.Exists(s) && c.Phase == angry;
            if (i % 100 == 0)
            {
                var a = c.Inspect();
                var b = World.Exists(o) ? Creatures.Of(o).Inspect() : null;
                var d = World.TryGetPosition(s, out var ps) && World.TryGetPosition(o, out var po) ? ps.DistanceTo(po) : -1;
                Info($"fight step {i}: stoneling {a?.State} target {a?.Target} hp {a?.Health}/{a?.HealthMax} seq {Abilities.SequenceOf(s)?.Sequence:x8} state 0x{StateOf(s):x8}; " +
                     $"opponent {b?.State} target {b?.Target} hp {b?.Health}/{b?.HealthMax} seq {(World.Exists(o) ? Abilities.SequenceOf(o)?.Sequence : 0):x8}; {d:F1} m apart");
            }
        }
        Info($"fight: its Stagger seen {staggerSteps} steps (the opponent's blows are light: they play the hit layer, not a stagger)");
        Info($"fight: slam sequence seen {seqSteps} steps, its state {stateSteps} steps, opponent down to {lowest:P0}, healed {healed} time(s), angry {phase}, rule count {Interlocked.Read(ref CsCount) - cs0}");
        Check(seqSteps > 0, $"the stoneling attacks with its own sequence ({seqSteps} steps)");
        Check(stateSteps > 0, $"and its graph plays the attack's state ({stateSteps} steps)");
        Check(lowest < 1, $"its hit lands: the opponent is down to {lowest:P0}");
        Check(Interlocked.Read(ref CsCount) > cs0, "a rule with code of its own fires (Is.Custom, Act.Custom)");
        Check(phase && healed > 0, $"below 95% health its rule sets the phase \"angry\" and its effect heals it ({healed})");
        Spawn.Destroy(s);
        if (World.Exists(o)) Spawn.Destroy(o);
    }

    // ---- reactions and death on a body of its own ----

    private async Task<uint> Plays(EntityId e, Guid sequence, int steps)
    {
        Check(Abilities.PlaySequence(e, sequence), $"{sequence} plays on it");
        var seen = new Dictionary<uint, int>();
        for (var i = 0; i < steps; i++)
        {
            await Steps(1);
            var st = StateOf(e);
            seen[st] = seen.GetValueOrDefault(st) + 1;
        }
        Info($"{sequence}: states {string.Join(", ", seen.Select(x => $"0x{x.Key:x8} x{x.Value}"))}");
        return seen.Where(x => x.Key != _stoneling!.States["Idle"]).OrderByDescending(x => x.Value).Select(x => x.Key).FirstOrDefault();
    }

    private async Task Reactions()
    {
        var t = Content.Templates.Get($"{Me}.stoneling")!;
        var stagger = BlockIds.Derive(Me, "state", "Stagger");
        var medium = Guid.Parse(t.Get("HitReactionBehavior.hitReactionMedium.hitSequence").ToString()!);
        var vukah = Content.Templates.Get("Enemy_Vukah_Melee")!;
        var theirs = Guid.Parse(vukah.Get("HitReactionBehavior.hitReactionMedium.hitSequence").ToString()!);
        var made = Content.Sequences.Get(medium.ToString());
        Check(medium != theirs && made?.Get("events[SetAnimationEvent#0].animationName").ToString() == stagger.ToString(),
            $"its hit reaction is a sequence of its own that plays its Stagger: {medium} (the Vukah's {theirs}) sets {made?.Get("events[SetAnimationEvent#0].animationName")}");
        var longStun = Guid.Parse(t.Get("HitReactionBehavior.hitReactionLongStun.hitSequence").ToString()!);
        var ls = Content.Sequences.Get(longStun.ToString());
        Check(ls?.Get("events[SetAnimationEvent#0].animationName").ToString() == stagger.ToString() && ls.Get("events[SetAnimationEvent#1].animationName").ToString() == "1477d689-8d8c-424c-bb5e-6f4cf42b2ac8",
            "a long stun: the base's own state becomes its Stagger, the engine's Stun stays (its graph has one)");
        var death = Guid.Parse(t.Get("Actor.deathSequence").ToString()!);
        Check(death == Guid.Parse(vukah.Get("Actor.deathSequence").ToString()!), $"its death keeps the Vukah's sequence, which plays the engine's Dead its graph has: {death}");

        var s = await _stoneling!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(s);
        await Steps(30);
        CreatureRef c_;
        var st = _stoneling.States;
        // a hit that makes it stagger, through the game's own hit reaction: it picks the sequence the template names
        c_ = Creatures.Of(s);
        c_.ForceReady();
        await Steps(20);
        var hit = await Combat.Damage(s, 5, default, new DamageOptions { Reaction = HitReaction.Medium, ForceInterrupt = true });
        var seenStagger = 0;
        for (var i = 0; i < 20; i++)
        {
            await Steps(1);
            if (StateOf(s) == st["Stagger"]) seenStagger++;
        }
        Check(hit.Applied && seenStagger > 0, $"a hit with a medium reaction plays its Stagger through the game's hit reaction: {hit}, {seenStagger} steps");
        await Steps(30);
        var got = await Plays(s, medium, 12);
        Check(got == st["Stagger"], $"its hit reaction plays its Stagger state: 0x{got:x8} (Stagger 0x{st["Stagger"]:x8})");
        await Steps(40);
        got = await Plays(s, longStun, 40);
        Check(got == st["Stagger"] || got == st["Stun"], $"its long stun plays its Stagger, then the Stun: 0x{got:x8}");
        await Steps(130);
        got = await Plays(s, death, 30);
        Check(got == st["Dead"], $"its death sequence lays it in its Dead state: 0x{got:x8} (Dead 0x{st["Dead"]:x8})");
        Spawn.Destroy(s);
    }

    // ---- a game creature with a new mind ----

    private async Task GameBody()
    {
        var v = await _vukahMind!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(v);
        var o = await Opponent(2, 0);
        var c = Creatures.Of(v);
        c.ForceReady();
        var j0 = Interlocked.Read(ref JsonCount);
        var c0 = Interlocked.Read(ref CsCount);
        for (var i = 0; i < 200; i++)
        {
            if (i % 40 == 0 && World.Exists(o)) c.Command.Attack(o, 5000);
            await Steps(1);
        }
        Check(Spawn.Inspect(v)?.Template == Creatures.Template("Enemy_Vukah_Melee")?.Id, "a Vukah with a new mind is the game's Vukah");
        var info = Arsenals.Info(c.Arsenal);
        Check(c.Arsenal == _vukahMind.Arsenal && info != null && info.Attacks.Count > 1, $"its arsenal keeps the Vukah's attacks and adds its rule: {info?.Attacks.Count} attacks, {info?.Rules.Count} rules");
        Check(Interlocked.Read(ref CsCount) > c0, $"its rule fires ({Interlocked.Read(ref CsCount) - c0} times)");
        Spawn.Destroy(v);
        if (World.Exists(o)) Spawn.Destroy(o);
    }

    // ---- a brain ----

    private async Task Brain()
    {
        var b = await _brainy!.Spawn(Pos, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(b);
        var o = await Opponent(2, 0);
        var c = Creatures.Of(b);
        c.ForceReady();
        var t0 = Interlocked.Read(ref BrainTicks);
        var slamSeq = BlockIds.Fnv(BlockIds.Derive(Me, "sequence", "stone-brain.slam"));
        long seen = 0;
        for (var i = 0; i < 400; i++)
        {
            await Steps(1);
            if (Abilities.SequenceOf(b) is { Sequence: var q } && q == slamSeq) seen++;
        }
        Check(!c.AiEnabled, "a creature with a brain has the game's AI off");
        Check(Interlocked.Read(ref BrainTicks) - t0 > 300, $"its brain runs every step ({Interlocked.Read(ref BrainTicks) - t0} ticks in 400 steps)");
        Check(Interlocked.Read(ref BrainSaw) > 0, "its own perception sees the opponent, with the game's off");
        Check(Interlocked.Read(ref BrainAttacks) > 0 && seen > 0, $"and the brain attacks with the creature's attack: {Interlocked.Read(ref BrainAttacks)} started, its sequence seen {seen} steps");
        Spawn.Destroy(b);
        if (World.Exists(o)) Spawn.Destroy(o);
    }

    // ---- a brain's body walks (19d-creature-control S4) ----
    // The body is leased from kiln.creature_control: the broker holds the game's AI off and removes the SteeringAgent whose
    // program overwrote every input write, so MoveTo moves it. Lab only: without a player the game does not simulate a body
    // where it thinks the terrain is not streamed in; this section detours the two terrain checks of
    // actor_is_ready_for_gameplay (noclip-test's pattern) so the forced-ready body grounds and walks.
    internal static int WalkMode;  // 0: StoneMind fights; 1: it walks to WalkGoal; 2: it stands
    internal static WorldPosition WalkGoal;

    private async Task BrainMoves()
    {
        var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
        foreach (var r in ready) Hooks.Detour(r, _ => 1);  // goes with the mod
        Info($"brain moves: terrain readiness checks detoured: {ready.Length}");
        var last = new Dictionary<uint, ControlChangedEvent>();
        using var sub = Events.On<ControlChangedEvent>(e => last[e.After.Body.Entity.Value] = e);
        var bases = Build.Bases;
        var anchor = bases.Count > 0 ? bases[0].Center : new WorldPosition(3685, 835, 1480);
        var at = anchor with { X = Math.Floor(anchor.X) + 140.25, Z = Math.Floor(anchor.Z) + 20.25 };
        try { await Build.Load(Build.BoxAround(at, 24, 60, 24)); } catch (Exception e) { Info($"brain moves: loading the terrain: {e.Message}"); }
        at = at with { Y = (GroundY(at) ?? at.Y) + 1 };  // just above the ground: a long fall would kill the body
        WalkMode = 2;
        async Task<EntityId> Body(WorldPosition where, bool outlivesTheMod = false)
        {
            var e = await _walker!.Spawn(where, 0, outlivesTheMod
                ? new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient | SpawnFlags.NoTrack, DespawnOnUnload = false }
                : new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
            Creatures.Of(e).ForceReady();
            for (var i = 0; i < 50 && Combat.GetHealth(e) is null; i++) await Steps(1);
            try { Combat.SetInvulnerable(e); } catch (Exception x) { Info($"brain moves: invulnerable: {x.Message}"); }
            for (var i = 0; i < 250 && World.Has(e, "SteeringAgent"); i++) await Steps(1);
            for (var i = 0; i < 250 && (Grounded(e) is false); i++) await Steps(1);
            return e;
        }
        var b = await Body(at);
        _mine.Add(b);
        var lease = last.TryGetValue(b.Value, out var ev) ? ev.After : null;
        Check(!World.Has(b, "SteeringAgent") && lease is { CanDrive: true, RequestedRole: ControlRole.PetFollow } && !Creatures.Of(b).AiEnabled,
            $"brain: the broker took the body (SteeringAgent removed, lease {lease?.Lease.Value} {lease?.State} {lease?.RequestedRole}, AI off, grounded {Grounded(b)})");
        await Compare(at with { X = at.X + 8 });
        WalkGoal = PosOf(b) with { X = PosOf(b).X + 30 };
        WalkMode = 1;
        var p0 = PosOf(b);
        await Steps(10);
        var input = World.ReadBytes(b, "ActorInput");
        var loco = World.ReadBytes(b, "DynamicLocomotion");
        Info($"brain moves: after 10 steps: input move ({(input is null ? "-" : $"{BitConverter.ToSingle(input, 0x198):0.00},{BitConverter.ToSingle(input, 0x1a0):0.00}")}) speed {(input is null ? -1 : input[0x1c4])}, " +
             $"inputVelocity ({(loco is null ? "-" : $"{BitConverter.ToSingle(loco, 0):0.00},{BitConverter.ToSingle(loco, 8):0.00}")}), moved {Flat(p0, PosOf(b)):0.00} m; {Diag(b)}");
        await Steps(38);  // 2 s at 24 steps a second in all
        var moved = Flat(p0, PosOf(b));
        Check(moved >= 3, $"brain: MoveTo moved {moved:0.00} m in 2 s");
        WalkMode = 2;
        await Steps(15);
        var p1 = PosOf(b);
        await Steps(24);
        var held = Flat(p1, PosOf(b));
        Check(held < 0.1, $"brain: Stop holds {held:0.000} m (< 0.1 m) in 1 s");
        Spawn.Destroy(b);
        _mine.Remove(b);
        await Steps(4);
        var lost = last.TryGetValue(b.Value, out var le) ? le.After : null;
        var now = lost is null ? null : CreatureControl.Inspect(lost.Lease);
        Check(lost is { State: ControlLeaseState.Failed, Result: ControlResultCode.BodyGone, CanDrive: false } && now is { CanDrive: false },
            $"brain: body destroyed -> lease {lost?.Result}, no write ({lost?.Reason})");
        // the unload check: one more body, standing, that outlives the mod (not despawned with it): unload blocks-npc-test through
        // kiln/control, then read the broker's lines for this body: Releasing, restored, Released
        var stay = await Body(at with { Z = at.Z + 6 }, outlivesTheMod: true);
        var sl = last.TryGetValue(stay.Value, out var se) ? se.After : null;
        Info($"brain moves: unload check: body {stay} lease {sl?.Lease.Value} {sl?.State}; SteeringAgent {World.Has(stay, "SteeringAgent")}, AI {Creatures.Of(stay).AiEnabled}");
    }

    // the body's movement state, for the log: Actor state and ability mask, locomotion state, the AI's state
    private static string Diag(EntityId e)
    {
        var a = World.ReadBytes(e, "Actor");
        var l = World.ReadBytes(e, "DynamicLocomotion");
        var state = a is { Length: > 0xc04 } ? BitConverter.ToUInt64(a, 0xbf8) : 0;
        var mask = a is { Length: > 0xc04 } ? BitConverter.ToUInt32(a, 0xc00) : 0;
        string ai;
        try { ai = Creatures.Of(e).State.ToString(); } catch (Exception x) { ai = x.GetType().Name; }
        return $"actor state 0x{state:x} abilities 0x{mask:x} (CanMove {(mask >> 26) & 1}), loco state {(l is { Length: > 0x59 } ? l[0x59] : -1)}, AI state {ai}";
    }

    // the same input on a game Vukah whose AI ran before its lease: does that body walk?
    private async Task Compare(WorldPosition where)
    {
        var e = await Spawn.Entity("Enemy_Vukah_Melee", where, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _mine.Add(e);
        Creatures.Of(e).ForceReady();
        for (var i = 0; i < 50 && Combat.GetHealth(e) is null; i++) await Steps(1);
        try { Combat.SetInvulnerable(e); } catch (Exception x) { Info($"brain moves: invulnerable: {x.Message}"); }
        await Steps(120);
        Info($"brain moves: compare: game Vukah {e} before its lease: {Diag(e)}");
        var r = CreatureControl.Acquire(new EntityRef(e, 0), ControlRole.PetFollow, CreatureControl.NewRequestId());
        var w = r.Ok ? await CreatureControl.WaitActive(r.Lease, 5) : null;
        var p0 = PosOf(e);
        for (var i = 0; i < 48 && w is { Ok: true }; i++)
        {
            if (CreatureControl.Inspect(r.Lease) is { CanDrive: true })
            {
                World.WriteRaw(e, "ActorInput", 0x198, new System.Numerics.Vector3(1, 0, 0));
                World.WriteRaw(e, "ActorInput", 0x1c4, (byte)2);
            }
            await Steps(1);
        }
        Info($"brain moves: compare: game Vukah leased after its AI ran, Walk input from the mod: moved {Flat(p0, PosOf(e)):0.00} m in 2 s ({w?.Reason ?? "granted"}); {Diag(e)}");
        if (r.Ok) await CreatureControl.Release(r.Lease);
    }

    private static double? GroundY(WorldPosition at)
    {
        try
        {
            var box = Build.BoxAround(at with { Y = at.Y + 100 - 128 }, 0.1, 128, 0.1);
            if (!Build.TryRead(box, out var cells) || cells is null) return null;
            var layer = Math.Max(1, box.SizeX * box.SizeZ);
            for (var i = cells.Length - 1; i >= 0; i--)
                if (!cells[i].IsAir) return Build.CornerOf(new CellPos(box.X, box.Y + i / layer, box.Z)).Y + (Build.Grid?.CellSize ?? 0.5);
        }
        catch (Exception) { }
        return null;
    }

    private static WorldPosition PosOf(EntityId e) => World.Position(e) ?? new WorldPosition(double.NaN, double.NaN, double.NaN);

    private static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static bool? Grounded(EntityId e) => World.ReadBytes(e, "Actor") is { Length: > 0xbf8 + 8 } a ? (BitConverter.ToUInt64(a, 0xbf8) & 1) != 0 : null;

    // ---- a friendly NPC ----

    private async Task Npc()
    {
        var def = Quests.NpcDefinitions.FirstOrDefault(d => d.FullId == $"{Me}:tobin");
        Check(def is { Clone: true, Template: "NPC_Workshop_Fisher_Trader", Dialogue: "tobin-talk" }, $"the NPC is the quests' NPC, a clone of the fisher with its dialogue: {def?.FullId} {def?.CloneName}");
        Check(Quests.NpcDefinitions.Any(d => d.FullId == $"{Me}:mira") && Dialogues.Definitions.Any(d => d.FullId == $"{Me}:mira-talk"), "an NPC with a dialogue from code defines both");
        var recipe = Content.Recipes.Get($"{Me}.tobin.sells0");
        Check(recipe != null, $"its wares are a recipe at the fisher's workshop: {recipe?.Name}");
        await Steps(60);
        // beside what the quests a mod defines in code become, in the data folder: a redeploy of the mod keeps it
        var file = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "kiln-quests", "native", $"{Me}.blocks.json"));
        var doc = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null;
        Check(doc?["npcs"]?["tobin"]?["roster"]?["summonItem"] != null && doc["npcs"]!["tobin-json"] == null,
            $"the roster entry of an NPC from code is written for the next start, in the data folder; one from a data file is not: {file}");

        // an NPC block of a data file: the game's start-up build reads it from the mod's folder, so it is there at the first start
        var first = Spawn.Lookup($"Kiln_{Me}_tobin-json");
        Check(first != null && first.Id == BlockIds.Derive(Me, "npc", "tobin-json"), $"an NPC of blocks/*.json is built at the first start: {first?.Name} {first?.Id}");
        if (first != null)
        {
            var j = await Spawn.Entity(first.Name, new WorldPosition(Pos.X, Pos.Y, Pos.Z + 16), 90, new SpawnOptions { Tag = Me });
            _mine.Add(j);
            await Steps(80);
            Check(World.Exists(j), $"and spawns and stays: {j}");
        }

        await Places();

        // the quests' engine knows it: a talk objective to it is done by talking to it
        var q = Quests.Define(new QuestDefinition
        {
            Id = "meet-tobin", Name = "Meet Tobin", Giver = "tobin", Journal = QuestJournalMode.Chat,
            Steps = [new QuestStep { Id = "talk", Objectives = [QuestObjective.Talk("tobin")] }],
        });
        var who = QuestHolder.Synthetic("blocks-npc-test");
        Quests.Reset(q.FullId, who);
        var r = Quests.Start(q.FullId, who, force: true);
        QuestInput.Talk(who, "tobin", modId: Me);
        await Steps(10);
        Check(r.Ok && Quests.StatusOf(q.FullId, who) == QuestStatus.Done, $"a quest whose step is to talk to it is done by talking to it: {r.Reason} {Quests.StatusOf(q.FullId, who)}");

        // what the game built at this start from what the last one wrote
        var lookup = Spawn.Lookup($"Kiln_{Me}_tobin");
        if (lookup == null)
        {
            Info("the NPC's template is not built yet: it is from the next start (start the lab again)");
            return;
        }
        Check(lookup.Id == BlockIds.Derive(Me, "npc", "tobin"), $"the game built the NPC from the written file: {lookup.Name} {lookup.Id}");
        var e = await Spawn.Entity(lookup.Name, new WorldPosition(Pos.X, Pos.Y, Pos.Z + 12), 90, new SpawnOptions { Tag = Me });
        _mine.Add(e);
        await Steps(80);
        Check(World.Exists(e), $"it spawns and stays (its roster entry is unlocked): {e}");
    }
    // An NPC block's place: a spot in the lab's base, 6..10 m from it. A base is fine for a friendly NPC unless the block says no; then it keeps
    // 20 m from every base zone, as a quest's places do.
    private async Task Places()
    {
        var home = NativeQuests.Npc($"{Me}:pip-home");
        var outside = NativeQuests.Npc($"{Me}:pip-out");
        Check(home?.Place?.Spec is { InBase: true, MinDistance: 6, MaxDistance: 10 } && outside?.Place?.Spec is { InBase: false },
            $"the start-up build takes an NPC block's place, a base fine unless it says no: {home?.Place?.Spec.InBase} {outside?.Place?.Spec.InBase}");
        var spot = new WorldPosition(3800, 833, 1434);
        WorldPosition? Where(NativeNpcInfo? n) =>
            n != null && Spawn.Find(n.Guid).FirstOrDefault() is { IsNone: false } e && World.TryGetPosition(e, out var p) ? p : null;
        for (var i = 0; i < 1200 && (Where(home) == null || Where(outside) == null); i++) await Steps(1);
        var bases = Build.Bases;
        double FromBase(WorldPosition p) => bases.Count == 0 ? double.NaN : bases.Min(b => Math.Max(Math.Abs(p.X - b.Center.X) - b.Half.X, Math.Abs(p.Z - b.Center.Z) - b.Half.Z));
        static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
        Check(bases.Count > 0 && FromBase(spot) < 0, $"the spot is in a base: {FromBase(spot):F0} m from the nearest zone's edge");
        var h = Where(home);
        Check(h is { } hp && Flat(hp, spot) is >= 5.5 and <= 10.5 && FromBase(hp) < 0, $"in a base: 6..10 m from the spot, as the block asks: {h} {(h is { } a ? Flat(a, spot) : double.NaN):F1} m");
        var o = Where(outside);
        Check(o is { } op && FromBase(op) >= 20, $"inBase false: 20 m outside the base zone: {o} {(o is { } b2 ? FromBase(b2) : double.NaN):F0} m from it");
    }

    // ---- what the template clones take ----

    private static bool Absent(GameDataEntry t, string component)
    {
        try { return t.GetJson(component) is not { } j || j["absent"] is not null; }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or InvalidOperationException) { return true; }
    }

    private (string State, string? Error) Clone(string name, JsonArray? remove)
    {
        var def = new JsonObject { ["guid"] = BlockIds.Derive(Me, "template", name).ToString(), ["name"] = $"{Me}.{name}", ["base"] = "Enemy_Vukah_Melee", ["fields"] = new JsonObject() };
        if (remove != null) def["removeComponents"] = remove;
        try
        {
            var d = Content.Templates.Define(def);
            return (d.State, d.Error);
        }
        catch (ArgumentException e) { return ("refused", e.Message); }
    }

    private Task Clones()
    {
        var t = Content.Templates.Get($"{Me}.stoneling")!;
        var graph = BlockIds.Derive(Me, "graph", "stone-moves.stone-body").ToString();
        string Refusal(string value)
        {
            try { t.Set("AnimationGraphResourceComponent2.animationGraph", value); return ""; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return e.Message; }
        }
        var wrong = Refusal(Guid.NewGuid().ToString());
        Check(wrong.Contains("not a loaded", StringComparison.Ordinal), $"a template's graph that is no loaded graph is still refused: \"{wrong}\"");
        var right = Refusal(graph);
        Check(right.Length == 0 && t.Get("AnimationGraphResourceComponent2.animationGraph").ToString() == graph, $"a mod's cooked graph is taken: \"{right}\" {t.Get("AnimationGraphResourceComponent2.animationGraph")}");

        Check(Absent(t, "SubEntities") && Absent(t, "HookShotAnchorSubEntity"), "a body of its own leaves the base creature's gear out (SubEntities, HookShotAnchorSubEntity)");
        var (state, error) = Clone("no-gear", new JsonArray("SubEntities", "HookShotAnchorSubEntity"));
        var ng = Content.Templates.Get($"{Me}.no-gear");
        Check(state == "registered" && ng != null && Absent(ng, "SubEntities") && !Absent(ng, "Enemy01BehaviorState"), $"removeComponents takes those two out and keeps the rest: {state} {error}");
        (state, error) = Clone("no-such", new JsonArray("NoSuchComponent"));
        Check(state != "registered" && error?.Contains("NoSuchComponent", StringComparison.Ordinal) == true, $"a component the base hasn't is refused by name: {state} \"{error}\"");
        (state, error) = Clone("no-actor", new JsonArray("Actor"));
        Check(state != "registered" && error?.Contains("Actor", StringComparison.Ordinal) == true, $"an actor's own is refused: {state} \"{error}\"");
        return Task.CompletedTask;
    }
}

/// <summary>Walks to what it sees and hits it: the game's AI is off for it.</summary>
internal sealed class StoneMind : NpcBrain
{
    public override void Tick(NpcControl npc)
    {
        Interlocked.Increment(ref BlocksNpcTestMod.BrainTicks);
        if (BlocksNpcTestMod.WalkMode == 1)
        {
            npc.MoveTo(BlocksNpcTestMod.WalkGoal);
            return;
        }
        if (BlocksNpcTestMod.WalkMode == 2)
        {
            npc.Stop();
            return;
        }
        if (npc.Target.IsNone)
        {
            npc.Stop();
            return;
        }
        Interlocked.Increment(ref BlocksNpcTestMod.BrainSaw);
        if (npc.TargetDistance is < 3 && World.Position(npc.Target) is { } at)
        {
            npc.Face(at);
            if (npc.Attack("slam")) Interlocked.Increment(ref BlocksNpcTestMod.BrainAttacks);
        }
        else if (World.Position(npc.Target) is { } to) npc.MoveTo(to, run: true);
    }
}
