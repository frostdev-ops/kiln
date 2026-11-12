// SPDX-License-Identifier: MIT
// Lab test of kiln.creatures' rules, arsenal writer and attack overrides. No player: creatures are forced ready and fight each
// other. PASS and FAIL lines in kiln.log, then a summary. A build where the hooks' targets did not resolve runs only Unresolved().
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace NpcRulesTest;

public sealed class NpcRulesTestMod : Mod
{
    private const string Vukah = "Enemy_Vukah_Melee";
    private const string Melee = "Enemy_Scavenger_Melee01";
    private const string Companion = "npc-rules-companion";
    private static readonly WorldPosition Pos = new(3855.8, 833.0, 1393.8);
    private static readonly Guid Heal = Guid.Parse("be0e7626-e95f-4a14-844a-2af52501698e");
    // Another Vukah's attack: not in the plain Vukah's arsenal.
    private static readonly Guid ThreePunch = Guid.Parse("6b05974c-1e8b-4709-b081-ed43d0dcbcb6");

    private readonly List<EntityId> _mine = [];
    private readonly List<IDisposable> _handles = [];
    private readonly ConcurrentDictionary<(uint Entity, uint Hash, ulong Caller), long> _starts = new();
    private HookHandle? _trace;
    private string? _companion;
    private int _pass, _fail;

    private static unsafe (uint Entity, ulong Caller) Started(nuint context, nuint stack) =>
        (*(uint*)context, *(ulong*)stack - (ulong)Symbols.ImageBase);

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"npc-rules-test: {(ok ? "PASS" : "FAIL")} {what}");
    }

    private void Info(string s) => Log.Info("npc-rules-test: " + s);

    public override void OnLoad() => _ = Guarded();

    public override void OnUnload() => Cleanup();

    private void Cleanup()
    {
        foreach (var h in _handles) try { h.Dispose(); } catch (Exception) { }
        _handles.Clear();
        foreach (var e in _mine) try { Spawn.Destroy(e); } catch (Exception) { }
        _mine.Clear();
        try { _trace?.Remove(); } catch (Exception) { }
        _trace = null;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private async Task Guarded()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(100);
            var st = Arsenals.RulesState;
            Check(st != null, "kiln.creatures answers");
            if (st == null) return;
            Info($"resolved: writer {st.Writer}, dispatcher {st.Dispatcher}, attacks {st.Attacks}, AI switch {st.AiSwitch}, hooks now {st.RuleHooks}/{st.AttackHooks}, map {st.MapCount}/{st.MapCapacity}");
            // The companion holds rules from its OnLoad; the unload check at the end brings it back.
            _companion = Mods.All.FirstOrDefault(m => m.Id == Companion)?.Directory;
            if (Mods.IsLoaded(Companion))
            {
                await Mods.UnloadAsync(Companion);
                await Steps(5);
                st = Arsenals.RulesState!;
            }
            Check(!st.RuleHooks && !st.AttackHooks, "no hook is installed while no mod asks for one");
            if (!st.Writer || !st.Dispatcher || !st.Attacks)
            {
                await Unresolved(st);
                return;
            }
            Symbols.LoadShipped();
            _trace = Hooks.Mid("npc-rules-test/TriggerSequence", (ref Registers r) =>
            {
                // (out, actor, {entity, time}*, sequence hash, trigger, input): who started which sequence on whom
                var (entity, caller) = Started(r.R8, r.Rsp);
                _starts.AddOrUpdate((entity, (uint)r.R9, caller), 1, (_, n) => n + 1);
            });
            await Writer();
            await RulesFire();
            await JobHandlers();
            await CustomAttack();
            await Replicated();
            await StartPaths();
            await FollowUps();
            await AttackAction();
            await Picks();
            await AiSwitch();
            await Extend();
            await Shrink();
            await Pack();
            _trace?.Remove();
            _trace = null;
            await Unload();
            await Cost();
        }
        catch (Exception e) { Check(false, "threw: " + e); }
        finally
        {
            Cleanup();
            Info($"summary: {_pass} PASS, {_fail} FAIL");
        }
    }

    // ---------------------------------------------------------------------------------------------- helpers ----
    private static readonly SpawnOptions Opts = new() { Tag = "npc-rules", Flags = SpawnFlags.Transient };

    private async Task<EntityId> One(string template, double dx, double dz)
    {
        var e = await Spawn.Entity(template, new WorldPosition(Pos.X + dx, Pos.Y, Pos.Z + dz), 0, Opts);
        _mine.Add(e);
        return e;
    }

    // Two creatures that will fight, spawned where fights happen in this save. `first` runs before either is ready.
    private async Task<(EntityId, EntityId)> Pair(string template, Action<CreatureRef, CreatureRef>? first = null, double apart = 2)
    {
        var x = await One(template, 0, 6);
        var y = await One(template, apart, 6);
        var cx = Creatures.Of(x);
        var cy = Creatures.Of(y);
        first?.Invoke(cx, cy);
        cx.Level = 60;
        cy.Level = 60;
        cx.Faction = Faction.PlayerAlly;
        cx.ForceReady();
        cy.ForceReady();
        return (x, y);
    }

    private void Gone(params EntityId[] es)
    {
        foreach (var e in es)
        {
            try { Spawn.Destroy(e); } catch (Exception) { }
            _mine.Remove(e);
        }
    }

    private sealed record Fought(Dictionary<uint, int> Steps, ulong Selects, ulong Starts, ulong Off, double LowestHealth);

    // Lets a pair fight, telling them to every so often. Counts the steps each sequence was seen playing.
    private async Task<Fought> Fight(EntityId x, EntityId y, int steps = 400)
    {
        var cx = Creatures.Of(x);
        var cy = Creatures.Of(y);
        var s0 = Arsenals.RulesState!;
        var lowest = 1.0;
        var seen = new Dictionary<uint, int>();
        for (var i = 0; i < steps; i++)
        {
            if (i % 40 == 0)
            {
                if (World.Exists(x) && World.Exists(y))
                {
                    cx.Command.Attack(y, 5000);
                    cy.Command.Attack(x, 5000);
                }
            }
            await Steps(1);
            foreach (var e in new[] { x, y })
            {
                if (!World.Exists(e)) continue;
                var q = Abilities.SequenceOf(e);
                if (q is { Sequence: not 0 } v) seen[v.Sequence] = seen.GetValueOrDefault(v.Sequence) + 1;
                if (Creatures.Of(e).Inspect() is { HealthMax: > 0 } hp) lowest = Math.Min(lowest, (double)hp.Health / hp.HealthMax);
            }
        }
        var s1 = Arsenals.RulesState!;
        return new Fought(seen, s1.Selects - s0.Selects, s1.Starts - s0.Starts, s1.StartsOffOverride - s0.StartsOffOverride, lowest);
    }

    private static string Show(Dictionary<uint, int> d) => string.Join(", ", d.OrderByDescending(k => k.Value).Select(k => $"{k.Key:x8}:{k.Value}"));

    private string Started(IEnumerable<uint> hashes, params EntityId[] on)
    {
        var want = hashes.ToHashSet();
        var ids = on.Select(e => e.Value).ToHashSet();
        return string.Join(", ", _starts.Where(kv => ids.Contains(kv.Key.Entity) && want.Contains(kv.Key.Hash))
            .GroupBy(kv => (kv.Key.Hash, kv.Key.Caller))
            .Select(g => $"{g.Key.Hash:x8} x{g.Sum(k => k.Value)} from +0x{g.Key.Caller:x}"));
    }

    private long StartCount(uint hash, params EntityId[] on)
    {
        var ids = on.Select(e => e.Value).ToHashSet();
        return _starts.Where(kv => ids.Contains(kv.Key.Entity) && kv.Key.Hash == hash).Sum(kv => kv.Value);
    }

    // ---------------------------------------------------------------------------------------------- what didn't resolve ----
    private async Task Unresolved(ArsenalRulesStatus st)
    {
        Info("some targets did not resolve here: checking that everything stays calm");
        if (!st.Writer || !st.Dispatcher)
        {
            var ok = false;
            try
            {
                Arsenals.Define("unresolved", new ArsenalSpec { Rules = [new ArsenalRule { Name = "r", If = [Is.Custom(_ => true)], Do = [] }] });
            }
            catch (NotSupportedException e)
            {
                ok = true;
                Info("a rule with Kiln's kinds is refused: " + e.Message);
            }
            Check(ok, "a rule with Kiln's kinds is refused with a reason, not a crash");
        }
        if (st.Writer)
        {
            var from = Arsenals.OfTemplate(Vukah)!.Value;
            using var h = Arsenals.Define("unresolved-data", new ArsenalSpec
            {
                From = from,
                Rules = [new ArsenalRule { Name = "data", If = [Is.HealthBelow(1)], Do = [Act.Game("SetCombatStateAction", ("state", "Prowl"), ("minTime", 1), ("maxTime", 2))] }],
            });
            var a = await One(Vukah, 0, 0);
            Creatures.Of(a).Arsenal = h.Id;
            Creatures.Of(a).ForceReady();
            await Steps(100);
            Check(World.Exists(a) && Arsenals.Info(h.Id) is { IsKiln: true, Rules.Count: 2 }, "a data-only arsenal still works, needing no hook");
        }
        var e2 = await One(Vukah, 3, 0);
        Creatures.Of(e2).ForceReady();
        await Steps(100);
        Check(Creatures.Of(e2).Inspect() != null, $"creatures run on: {Creatures.Of(e2).State}");
        if (!st.Attacks)
        {
            var threw = false;
            try { Creatures.Of(e2).Attacks.Force(0); }
            catch (NotSupportedException) { threw = true; }
            Check(threw, "an attack override says it is unavailable");
        }
        Creatures.Of(e2).AiEnabled = false;
        await Steps(20);
        Check(!Creatures.Of(e2).AiEnabled, "the AI switch needs no hook and still works");
        Creatures.Of(e2).AiEnabled = true;
        Info($"status: writer {st.Writer}, dispatcher {st.Dispatcher}, attacks {st.Attacks}, hooks {Arsenals.RulesState!.RuleHooks}/{Arsenals.RulesState!.AttackHooks}");
    }

    // ---------------------------------------------------------------------------------------------- the writer ----
    private async Task Writer()
    {
        var (n, bad) = Arsenals.SelfTest();
        Check(n >= 200 && bad == 0, $"every arsenal of the game goes through the writer and reads back the same: {n} checked, {bad} differ");
        void Refused(string what, string expect, Func<ArsenalSpec> spec)
        {
            try
            {
                Arsenals.Define("refused", spec()).Dispose();
                Check(false, what + " is refused");
            }
            catch (Exception e) when (e is ArgumentException)
            {
                Check(e.Message.Contains(expect), $"{what} is refused: {e.Message}");
            }
        }
        Refused("an unknown constraint type", "not in the game's reflection", () => new ArsenalSpec { Rules = [new ArsenalRule { Name = "x", If = [Is.Game("NoSuchConstrain")] }] });
        Refused("an unknown field", "no field", () => new ArsenalSpec { Rules = [new ArsenalRule { Name = "x", If = [Is.Game("DistanceConstrain", ("nope", 1))] }] });
        Refused("an unknown enum name", "unknown", () => new ArsenalSpec { Rules = [new ArsenalRule { Name = "x", Do = [Act.Game("SetCombatStateAction", ("state", "Dancing"))] }] });
        Refused("an attack without a sequence", "sequence", () => new ArsenalSpec { Attacks = [new ArsenalAttack { Name = "x" }] });
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var baseInfo = Arsenals.Info(from)!;
        Info($"the Vukah's arsenal: {baseInfo.Attacks.Count} attacks, {baseInfo.Rules.Count} rules");
        var h = Arsenals.Define("rich", new ArsenalSpec
        {
            From = from,
            Attacks =
            [
                new ArsenalAttack { Name = "punches", Sequence = ThreePunch, Style = ["Special"], Threat = 3, MaxRange = 3, Selection = [Is.TargetWithin(4)] },
                new ArsenalAttack { ModifyIndex = 0, MaxRange = 2.75, Chance = 0.5 },
            ],
            Rules = [new ArsenalRule { Name = "any", Every = 2, Cooldown = 1, Chance = 0.5, If = [Is.HealthBelow(1)], Do = [Act.Game("SetCombatStateAction", ("state", "Prowl"), ("minTime", 1.0), ("maxTime", 2.0))] }],
        });
        _handles.Add(h);
        await Steps(2);
        var i = Arsenals.Info(h.Id);
        Check(i is { IsKiln: true, Attacks.Count: 4, Rules.Count: 2, Problems.Count: 0 } && i.Attacks[3].Sequence == ThreePunch && i.Attacks[3].Id == RuleIds.Attack("punches"),
            $"a new arsenal from an existing one: {i?.Attacks.Count} attacks, {i?.Rules.Count} rules, attack 3 plays {i?.Attacks[3].Sequence}, problems {string.Join(";", i?.Problems ?? [])}");
        Check(i != null && i.Attacks[0].Id == baseInfo.Attacks[0].Id && i.Attacks[0].Sequence == baseInfo.Attacks[0].Sequence, "the base's attacks keep their indexes");
        Check(Arsenals.Info(from)!.Address == baseInfo.Address, "the arsenal it started from is untouched");
        // A limit that depends on the base: those additions are left out with the reason; the arsenal is the base.
        var tooMany = Arsenals.Define("too-many", new ArsenalSpec
        {
            From = from,
            Rules = Enumerable.Range(0, 40).Select(n => new ArsenalRule { Name = "many" + n, If = [Is.HealthBelow(1)] }).ToList(),
        });
        await Steps(2);
        var over = Arsenals.Info(tooMany.Id)!;
        Check(over.Problems.Any(p => p.Contains("32")) && over.Rules.Count == baseInfo.Rules.Count && over.Attacks.Count == baseInfo.Attacks.Count,
            $"past the limit: refused with \"{string.Join("; ", over.Problems)}\", the arsenal is its base ({over.Rules.Count} rules)");
        tooMany.Dispose();
        await Steps(2);
        var map0 = Arsenals.RulesState!.MapCount;
        h.Dispose();
        _handles.Remove(h);
        await Steps(2);
        Check(Arsenals.Info(h.Id) == null && Arsenals.RulesState!.MapCount == map0 - 1, $"disposing a new arsenal takes its map entry out ({map0} -> {Arsenals.RulesState!.MapCount}), and the map still finds the others: {Arsenals.Info(from) != null}");
    }

    // ---------------------------------------------------------------------------------------------- rules fire ----
    private async Task RulesFire()
    {
        var flag = false;
        var phaseFlag = false;
        var offThread = 0;
        long customRuns = 0, hpTrue = 0, hpFalse = 0, timeTrue = 0, timeFalse = 0, buffRuns = 0, notRuns = 0, phaseRuns = 0;
        void Count(ref long c)
        {
            if (!Game.IsTickThread) offThread++;
            c++;
        }
        var hour = Kiln.Time.OfDay;
        var cond = Is.Custom(npc =>
        {
            if (!Game.IsTickThread) offThread++;
            return flag && npc.HealthMax > 0;   // the tick API works here
        }, every: 0.1);
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var h = Arsenals.Define("rules", new ArsenalSpec
        {
            From = from,
            Rules =
            [
                new ArsenalRule { Name = "custom", Every = 0.2, If = [cond], Do = [Act.Custom(npc => { Count(ref customRuns); _ = npc.Creature.Inspect(); })] },
                new ArsenalRule { Name = "not", Every = 0.2, If = [cond.Not()], Do = [Act.Custom(_ => Count(ref notRuns))] },
                new ArsenalRule { Name = "hp1", Every = 0.2, If = [Is.HealthBelow(1)], Do = [Act.Custom(_ => Count(ref hpTrue))] },
                new ArsenalRule { Name = "hp0", Every = 0.2, If = [Is.HealthBelow(0.0001)], Do = [Act.Custom(_ => Count(ref hpFalse))] },
                new ArsenalRule { Name = "t1", Every = 0.2, If = [Is.TimeBetween((hour + 23) % 24, (hour + 2) % 24)], Do = [Act.Custom(_ => Count(ref timeTrue))] },
                new ArsenalRule { Name = "t0", Every = 0.2, If = [Is.TimeBetween((hour + 6) % 24, (hour + 7) % 24)], Do = [Act.Custom(_ => Count(ref timeFalse))] },
                new ArsenalRule { Name = "buff", Every = 0.2, If = [Is.HasBuff(Heal)], Do = [Act.Custom(_ => Count(ref buffRuns))] },
                new ArsenalRule { Name = "setphase", Every = 0.2, If = [Is.Custom(_ => phaseFlag, every: 0.1)], Do = [Act.SetPhase("second")] },
                new ArsenalRule { Name = "inphase", Every = 0.2, If = [Is.InPhase("second")], Do = [Act.Custom(_ => Count(ref phaseRuns))] },
            ],
        });
        _handles.Add(h);
        Arsenals.Timing = true;
        await Steps(3);
        var a = await One(Vukah, 0, 0);
        var ca = Creatures.Of(a);
        ca.Arsenal = h.Id;
        ca.ForceReady();
        await Steps(60);
        Check(ca.Arsenal == h.Id, "a creature reads the new arsenal through its private reference");
        var (e0, t0) = Arsenals.ConditionStats(cond);
        var r0 = customRuns;
        await Steps(120);
        var (e1, t1) = Arsenals.ConditionStats(cond);
        Check(e1 > e0 && t1 == t0 && customRuns == r0 && notRuns > 0,
            $"constraint false: evaluated {e1 - e0} times, true {t1 - t0}, action ran {customRuns - r0}; its negation ran {notRuns}");
        flag = true;
        await Steps(20);
        var (e2, t2) = Arsenals.ConditionStats(cond);
        var r2 = customRuns;
        await Steps(120);
        var (e3, t3) = Arsenals.ConditionStats(cond);
        Check(t3 > t2 && e3 > e2 && customRuns > r2, $"constraint true: evaluated {e3 - e2} times, true {t3 - t2}, action ran {customRuns - r2}");
        Check(hpTrue > 0 && hpFalse == 0, $"the game's own kinds beside Kiln's: health below 100 % ran {hpTrue}, below 0.01 % ran {hpFalse}");
        Check(timeTrue > 0 && timeFalse == 0, $"time of day ({hour:F2} h): the window around now ran {timeTrue}, one 6 h away ran {timeFalse}");
        var bb = buffRuns;
        await Steps(40);
        Check(buffRuns == 0, $"no buff: the rule did not run ({bb})");
        await Abilities.ApplyBuff(a, Heal, TimeSpan.FromSeconds(10));
        await Steps(60);
        Check(buffRuns > 0, $"with the buff on: it ran {buffRuns} times");
        Check(phaseRuns == 0, "not in the phase: its rule did not run");
        phaseFlag = true;
        await Steps(60);
        Check(ca.Phase == RuleIds.Fnv(RuleIds.Phase("second")) && phaseRuns > 0, $"a rule set the phase on a body the game has no phases for ({ca.Phase:x8}), and a rule on that phase ran {phaseRuns}");
        Check(offThread == 0, "every condition and action of the mod ran on the tick thread");
        var st = Arsenals.RulesState!;
        Info($"so far: constraints {st.Constraints} ({st.ConstraintsTrue} true), actions {st.Actions}, dropped {st.ActionsDropped}, table {st.Tracked} creature(s)");
        Check(st.ActionsDropped == 0, "no action was dropped");
        Gone(a);
        h.Dispose();
        _handles.Remove(h);
        Arsenals.Timing = false;
        await Steps(5);
    }

    // ---------------------------------------------------------------------------------------------- job-thread handlers ----
    private static int _jobCalls;

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static uint JobCondition(uint entity, uint id, uint arg, nint user)
    {
        Interlocked.Increment(ref _jobCalls);
        return arg;
    }

    private async Task JobHandlers()
    {
        unsafe
        {
            Arsenals.JobHandler(7, (nint)(delegate* unmanaged[Cdecl]<uint, uint, uint, nint, uint>)&JobCondition);
        }
        long yes = 0, no = 0;
        var h = Arsenals.Define("job", new ArsenalSpec
        {
            From = Arsenals.OfTemplate(Vukah),
            Rules =
            [
                new ArsenalRule { Name = "yes", Every = 0.2, If = [Is.Job(7, 1)], Do = [Act.Custom(_ => yes++), Act.Job(7, 0)] },
                new ArsenalRule { Name = "no", Every = 0.2, If = [Is.Job(7, 0)], Do = [Act.Custom(_ => no++)] },
            ],
        });
        _handles.Add(h);
        await Steps(3);
        var a = await One(Vukah, 0, 0);
        Creatures.Of(a).Arsenal = h.Id;
        Creatures.Of(a).ForceReady();
        await Steps(150);
        Check(_jobCalls > 0 && yes > 0 && no == 0, $"a job-thread handler answered {_jobCalls} times: its true rule ran {yes}, its false rule {no}");
        Gone(a);
        h.Dispose();
        _handles.Remove(h);
        unsafe { Arsenals.JobHandler(7, 0); }
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- what clients get ----
    private const uint Strike1 = 0x947bf0f6, Strike1State = 0x2f9a458c;

    // A vanilla client gets a creature through these components and nothing else of the rule system. A forced attack and a rule
    // must come out in them exactly as the game's own choice does.
    private async Task Replicated()
    {
        var (x, y) = await Pair(Vukah, (cx, cy) => { cx.Attacks.Force(0); cy.Attacks.Force(0); });
        uint seqs = 0, states = 0, mismatch = 0;
        var c = Creatures.Of(x);
        for (var i = 0; i < 300; i++)
        {
            if (i % 40 == 0)
            {
                c.Command.Attack(y, 5000);
                Creatures.Of(y).Command.Attack(x, 5000);
            }
            await Steps(1);
            foreach (var e in new[] { x, y })
            {
                var na = World.ReadBytes(e, "NetworkActor");
                var input = World.ReadBytes(e, "NetworkAnimationGraphInput");
                var q = Abilities.SequenceOf(e);
                if (na == null || input == null || q is not { } v) continue;
                var wire = BitConverter.ToUInt32(na, 0);
                if (wire == Strike1) seqs++;
                if (BitConverter.ToUInt32(input, 0x22c) == Strike1State) states++;
                if (v.Sequence != 0 && wire != v.Sequence && wire != 0) mismatch++;
            }
        }
        Info($"on the wire: NetworkActor names the forced attack's sequence in {seqs} entity-steps, NetworkAnimationGraphInput its state in {states}, {mismatch} steps differ from the server's sequence (the replica is written once a step)");
        Check(seqs > 0 && states > 0, "a forced attack reaches the components clients read: the sequence's id and its animation state");
        c.AiEnabled = false;
        await Steps(5);
        var ne0 = World.ReadBytes(x, "NetworkEnemy");
        await Steps(100);
        var ne1 = World.ReadBytes(x, "NetworkEnemy");
        Info($"AI off: NetworkEnemy.state {ne0?[0]} -> {ne1?[0]} over 100 steps");
        Check(ne0 != null && ne1 != null && ne0[0] == ne1[0], "AI off: NetworkEnemy.state holds its last value for clients");
        c.AiEnabled = true;
        Gone(x, y);
        await Steps(5);
    }

    // ---------------------------------------------------------------------------------------------- a custom attack ----
    private async Task CustomAttack()
    {
        // An attack record of our own that plays a sequence this body's graph has a state for: it must land.
        var from2 = Arsenals.OfTemplate(Vukah)!.Value;
        var own = Arsenals.Define("own-strike", new ArsenalSpec
        {
            From = from2,
            Attacks = [new ArsenalAttack { Name = "own-strike", Sequence = Guid.Parse("1470b836-f769-49df-9403-43237d96dfc6"), Style = ["Special"], Threat = 5, MaxRange = 2.5 }],
        });
        _handles.Add(own);
        await Steps(2);
        var (ox, oy) = await Pair(Vukah, (cx, cy) => { cx.Arsenal = own.Id; cy.Arsenal = own.Id; cx.Attacks.Force("own-strike"); cy.Attacks.Force("own-strike"); });
        var of = await Fight(ox, oy);
        Info($"an own attack record, the body's own sequence: starts {of.Starts}, lowest health {of.LowestHealth:P1}; {Show(of.Steps)}");
        Check(StartCount(Strike1, ox, oy) > 0, $"an attack of our own record plays ({StartCount(Strike1, ox, oy)} starts), selected by index {Arsenals.Info(own.Id)!.Attacks.Count - 1}");
        Check(of.LowestHealth < 1, $"and it lands: lowest health {of.LowestHealth:P1}");
        Gone(ox, oy);
        own.Dispose();
        _handles.Remove(own);
        await Steps(3);

        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var h = Arsenals.Define("punch", new ArsenalSpec
        {
            From = from,
            Attacks = [new ArsenalAttack { Name = "punches", Sequence = ThreePunch, Style = ["Normal"], Threat = 3, MaxRange = 3.5, Selection = [Is.TargetWithin(4)] }],
        });
        _handles.Add(h);
        await Steps(2);
        var hash = RuleIds.Fnv(ThreePunch);
        var (x, y) = await Pair(Vukah, (cx, cy) => { cx.Arsenal = h.Id; cy.Arsenal = h.Id; cx.Attacks.Force("punches"); cy.Attacks.Force("punches"); });
        var f = await Fight(x, y);
        Info($"custom attack: selects {f.Selects}, starts {f.Starts}; sequences (steps): {Show(f.Steps)}");
        Info($"who started what: {Started([hash, 0x947bf0f6, 0xb49eeb00, 0xee78bde2], x, y)}");
        Check(f.Steps.GetValueOrDefault(hash) > 0 && StartCount(hash, x, y) > 0, $"the attack written into the arsenal plays: {hash:x8} seen for {f.Steps.GetValueOrDefault(hash)} steps, started {StartCount(hash, x, y)} times");
        Info($"custom attack: lowest health in the fight {f.LowestHealth:P1}, without the attack's own animation state in this body's graph");
        Gone(x, y);
        h.Dispose();
        _handles.Remove(h);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- start paths ----
    private async Task StartPaths()
    {
        const uint A0 = 0x947bf0f6, A1 = 0xb49eeb00, A2 = 0xee78bde2;
        uint[] attacks = [A0, A1, A2];

        // 1. Nothing forced: what the game does by itself here.
        var (x, y) = await Pair(Vukah);
        var natural = await Fight(x, y);
        Info($"natural: selects {natural.Selects}, starts {natural.Starts}; sequences (steps): {Show(natural.Steps)}");
        Info($"natural: who started what: {Started(attacks, x, y)}");
        Check(StartCount(A0, x, y) > 0, $"a natural fight starts attack 0 ({StartCount(A0, x, y)} times)");
        Gone(x, y);
        await Steps(10);

        // 2. Forced from before the creatures are ready, in three fights: nothing but the forced attack may start.
        for (var run = 0; run < 3; run++)
        {
            (x, y) = await Pair(Vukah, (cx, cy) => { cx.Attacks.Force(1); cy.Attacks.Force(1); });
            var f = await Fight(x, y);
            Info($"forced 1, run {run}: selects {f.Selects}, starts {f.Starts}, off the override {f.Off}; sequences (steps): {Show(f.Steps)}");
            Info($"forced 1, run {run}: who started what: {Started(attacks, x, y)}");
            Check(StartCount(A1, x, y) > 0 && f.Steps.GetValueOrDefault(A1) > 0, $"run {run}: the forced attack played ({StartCount(A1, x, y)} starts, {f.Steps.GetValueOrDefault(A1)} steps)");
            Check(StartCount(A0, x, y) == 0 && StartCount(A2, x, y) == 0 && f.Steps.GetValueOrDefault(A0) == 0 && f.Steps.GetValueOrDefault(A2) == 0,
                $"run {run}: no other attack started ({StartCount(A0, x, y)}, {StartCount(A2, x, y)}) or was seen playing ({f.Steps.GetValueOrDefault(A0)}, {f.Steps.GetValueOrDefault(A2)} steps)");
            Check(f.Off == 0, $"run {run}: nothing reached SetCurrentAttack against the override");
            Gone(x, y);
            await Steps(10);
        }

        // 3. Hostile and ready first, forced only after 60 steps. An attack begun before the force plays on, which from outside
        // looks like the override failing: it must not start again.
        (x, y) = await Pair(Vukah);
        await Steps(60);
        var cxr = Creatures.Of(x);
        var cyr = Creatures.Of(y);
        var before = StartCount(A0, x, y);
        var running = new[] { x, y }.Count(e => Abilities.SequenceOf(e) is { Sequence: A0 });
        cxr.Attacks.Force(1);
        cyr.Attacks.Force(1);
        await Steps(1);
        var at = StartCount(A0, x, y);
        var late = await Fight(x, y);
        var after = StartCount(A0, x, y) - at;
        Info($"forced late: attack 0 had started {before} times before the force, {running} of the pair were playing it then; started {after} times after it; seen playing {late.Steps.GetValueOrDefault(A0)} steps after it");
        Check(after == 0, "forced late: attack 0 does not start again after the force");
        Gone(x, y);
        await Steps(10);

        // 4. An allow mask: the game's own choice, among these.
        (x, y) = await Pair(Vukah, (cx, cy) => { cx.Attacks.Allow(0, 1); cy.Attacks.Allow(0, 1); });
        var s0 = Arsenals.RulesState!;
        var m = await Fight(x, y);
        var s1 = Arsenals.RulesState!;
        Info($"allow 0,1: selects {m.Selects}, refused {s1.SelectsFiltered - s0.SelectsFiltered}; {Started(attacks, x, y)}");
        Check(StartCount(A2, x, y) == 0 && (StartCount(A0, x, y) + StartCount(A1, x, y)) > 0, "an allow mask keeps the game choosing among the attacks it names");
        // Clearing brings the game's choice back.
        Creatures.Of(x).Attacks.Clear();
        Creatures.Of(y).Attacks.Clear();
        await Steps(2);
        Check(Arsenals.RulesState!.Overrides == 0, $"clearing an override: {Arsenals.RulesState!.Overrides} left");
        Gone(x, y);
        await Steps(10);
    }

    // ---------------------------------------------------------------------------------------------- combos ----
    private async Task FollowUps()
    {
        // The Scavenger: attack 1 (Chain_Strike01) declares attack 0 (Chain_Strike02) as a follow-up, attack 0 declares attack 2
        // (Chain_Strike03). Attacks 0 and 2 have no style, so SelectAttack can never pick them: only the combo path starts them.
        const uint A0 = 0x0af145e9, A1 = 0x2770f8fd, A2 = 0x28408ed0, A3 = 0xc925972e;
        uint[] all = [A0, A1, A2, A3];
        // Its strikes reach 1.9 m, and nothing walks in the lab: they stand within reach.
        const double Apart = 1.3;
        var (x, y) = await Pair(Melee, null, Apart);
        var n = await Fight(x, y, 600);
        Info($"scavenger, natural: {Show(n.Steps)}; {Started(all, x, y)}; {Creatures.Of(y).State}, target {Creatures.Of(y).Inspect()?.Target}");
        var natural = StartCount(A0, x, y) + StartCount(A2, x, y);
        Check(natural > 0, $"the combo path is real: attacks of no style started {natural} times with no selection to pick them");
        Gone(x, y);
        await Steps(10);

        var s0 = Arsenals.RulesState!;
        (x, y) = await Pair(Melee, (cx, cy) => { cx.Attacks.Force(1); cy.Attacks.Force(1); }, Apart);
        var f = await Fight(x, y, 600);
        var s1 = Arsenals.RulesState!;
        Info($"scavenger, forced 1: selects {f.Selects}, starts {f.Starts}, follow-ups refused {s1.FollowupsRefused - s0.FollowupsRefused}, off {f.Off}; {Started(all, x, y)}");
        Check(StartCount(A1, x, y) > 0, $"forced: the forced attack played ({StartCount(A1, x, y)} starts)");
        Check(StartCount(A0, x, y) == 0 && StartCount(A2, x, y) == 0 && StartCount(A3, x, y) == 0, "forced: its combo did not continue, and nothing else started");
        Check(s1.FollowupsRefused > s0.FollowupsRefused, $"forced: the override refused the follow-ups the game would have run ({s1.FollowupsRefused - s0.FollowupsRefused})");
        Check(f.Off == 0, "forced: nothing reached SetCurrentAttack against the override");
        Gone(x, y);
        await Steps(10);

        // An allow mask stops the combo the same way, and lets the rest be.
        s0 = Arsenals.RulesState!;
        (x, y) = await Pair(Melee, (cx, cy) => { cx.Attacks.Allow(1, 3); cy.Attacks.Allow(1, 3); }, Apart);
        var m = await Fight(x, y, 400);
        s1 = Arsenals.RulesState!;
        Info($"scavenger, allow 1,3: follow-ups refused {s1.FollowupsRefused - s0.FollowupsRefused}; {Started(all, x, y)}");
        Check(StartCount(A0, x, y) == 0 && StartCount(A2, x, y) == 0 && StartCount(A1, x, y) + StartCount(A3, x, y) > 0, "allow: only the named attacks started");
        Gone(x, y);
        await Steps(10);
    }

    // ---------------------------------------------------------------------------------------------- a rule starts an attack ----
    private async Task AttackAction()
    {
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var hash = RuleIds.Fnv(ThreePunch);
        var h = Arsenals.Define("byrule", new ArsenalSpec
        {
            From = from,
            Attacks = [new ArsenalAttack { Name = "punches", Sequence = ThreePunch, Style = ["Special"], Threat = 3, MaxRange = 3.5 }],
            Rules =
            [
                new ArsenalRule
                {
                    Name = "slam",
                    Every = 1,
                    Cooldown = 3,
                    If = [Is.Game("StateConstrain", ("states", new[] { "Combat" })), Is.Game("IsAnyAttackSelectedConstraint", ("negated", true))],
                    Do = [Act.Attack("punches")],
                },
            ],
        });
        _handles.Add(h);
        await Steps(2);
        var (x, y) = await Pair(Vukah, (cx, cy) => { cx.Arsenal = h.Id; cy.Arsenal = h.Id; });
        var f = await Fight(x, y);
        Info($"attack by rule: {Started([hash, 0x947bf0f6, 0xb49eeb00], x, y)}; steps {f.Steps.GetValueOrDefault(hash)}");
        Check(StartCount(hash, x, y) > 0, $"a rule started an attack of style Special, which the game would not pick by itself here ({StartCount(hash, x, y)} starts)");
        Gone(x, y);
        h.Dispose();
        _handles.Remove(h);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- picks ----
    private async Task Picks()
    {
        const uint A0 = 0x947bf0f6;
        var hash = RuleIds.Fnv(ThreePunch);
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var offThread = 0;
        var asked = 0;
        var h = Arsenals.Define("picked", new ArsenalSpec
        {
            From = from,
            Attacks = [new ArsenalAttack { Name = "punches", Sequence = ThreePunch, Style = ["Normal"], Threat = 3, MaxRange = 3.5 }],
            PickAttack = npc =>
            {
                if (!Game.IsTickThread) offThread++;
                asked++;
                return npc.HealthMax > 0 ? "punches" : null;
            },
        });
        _handles.Add(h);
        await Steps(2);
        var s0 = Arsenals.RulesState!;
        var (x, y) = await Pair(Vukah, (cx, cy) => { cx.Arsenal = h.Id; cy.Arsenal = h.Id; });
        var f = await Fight(x, y);
        var s1 = Arsenals.RulesState!;
        Info($"PickAttack: asked {asked} times, first selections waited {s1.PicksWaiting - s0.PicksWaiting}; {Started([hash, A0], x, y)}");
        Check(StartCount(hash, x, y) > 0 && StartCount(A0, x, y) == 0, "PickAttack: every attack is the picked one, the first included");
        Check(offThread == 0 && asked > 0, "PickAttack ran on the tick thread");
        Gone(x, y);
        h.Dispose();
        _handles.Remove(h);
        await Steps(5);

        // A target of our choosing, with no hook.
        var a = await One(Vukah, 0, 6);
        var b = await One(Vukah, 2, 6);
        var c = await One(Vukah, -2, 6);
        var ca = Creatures.Of(a);
        ca.Faction = Faction.PlayerAlly;
        foreach (var e in new[] { a, b, c }) { Creatures.Of(e).Level = 60; Creatures.Of(e).ForceReady(); }
        await Steps(50);
        ca.Command.Attack(b, 5000);
        await Steps(60);
        Info($"target before: {ca.Inspect()?.Target} (b {b}, c {c})");
        foreach (var want in new[] { c, b, c })
        {
            ca.PreferTarget(want, 30);
            var n = 0;
            while (ca.Inspect()?.Target != want && n < 50)
            {
                await Steps(1);
                n++;
            }
            var heldFor = 0;
            for (var i = 0; i < 100 && ca.Inspect()?.Target == want; i++)
            {
                await Steps(1);
                heldFor++;
            }
            Check(n < 50 && heldFor == 100, $"PreferTarget: the target is {want} after {n} steps, and held for {heldFor}");
        }
        ca.ReleaseTarget();
        await Steps(100);
        Info($"PreferTarget released: target {ca.Inspect()?.Target}, state {ca.State}");
        Gone(a, b, c);
        await Steps(5);
        // The same from an arsenal: a rule finds the creatures, the pick runs on the tick. A fresh trio, the last one has fought.
        a = await One(Vukah, 0, 6);
        b = await One(Vukah, 2, 6);
        c = await One(Vukah, -2, 6);
        ca = Creatures.Of(a);
        ca.Faction = Faction.PlayerAlly;
        foreach (var e in new[] { a, b, c }) { Creatures.Of(e).Level = 60; Creatures.Of(e).ForceReady(); }
        var pickedTarget = c;
        var picks = 0;
        var pt = Arsenals.Define("pick-target", new ArsenalSpec
        {
            From = Arsenals.OfTemplate(Vukah),
            PickEvery = 0.5,
            PickTarget = _ =>
            {
                picks++;
                return pickedTarget;
            },
        });
        _handles.Add(pt);
        await Steps(3);
        ca.Arsenal = pt.Id;
        await Steps(50);
        ca.Command.Attack(b, 5000);
        await Steps(60);
        Check(picks > 0 && ca.Inspect()?.Target == c, $"PickTarget: asked {picks} times, the target is {ca.Inspect()?.Target} (wanted {c})");
        pickedTarget = b;
        await Steps(60);
        Check(ca.Inspect()?.Target == b, $"PickTarget follows the answer: now {ca.Inspect()?.Target} (wanted {b})");
        ca.Arsenal = Arsenals.OfTemplate(Vukah)!.Value;
        pt.Dispose();
        _handles.Remove(pt);
        Gone(a, b, c);
        await Steps(5);
    }

    // ---------------------------------------------------------------------------------------------- AI off ----
    private async Task AiSwitch()
    {
        var (x, y) = await Pair(Vukah);
        var c = Creatures.Of(x);
        var d = Creatures.Of(y);
        c.Command.Attack(y, 5000);
        d.Command.Attack(x, 5000);
        await Steps(100);
        Info($"AI on: {c.State}, target {c.Inspect()?.Target}, enabled {c.AiEnabled}");
        Check(c.AiEnabled, "the AI switch reads on");
        var target0 = c.Inspect()?.Target;
        Check(target0?.Value == y.Value, $"it has its target before the switch ({target0}, the other is {y})");
        c.AiEnabled = false;
        await Steps(5);
        var st = c.State;
        var attackStarts = StartCount(0x947bf0f6, x);
        await Steps(150);
        Check(!c.AiEnabled && c.State == st, $"AI off: the state holds at {st} for 150 steps, with the switch off");
        Info($"AI off: target was {target0}, is now {c.Inspect()?.Target}");
        Check(c.Inspect()?.Target.Value == 0, "AI off: the game clears the combat target too: perception does not run on for free");
        var playing = Abilities.PlaySequence(x, Guid.Parse("1470b836-f769-49df-9403-43237d96dfc6"));
        await Steps(10);
        var q = Abilities.SequenceOf(x);
        Check(playing && q is { Sequence: 0x947bf0f6 }, $"AI off: a sequence still plays ({playing}, now {q?.Sequence:x8})");
        Check(Arsenals.RulesState!.AiOff == 1, "the service counts it");
        c.AiEnabled = true;
        await Steps(50);
        Check(c.AiEnabled && Arsenals.RulesState!.AiOff == 0, $"AI on again: {c.State}");
        Gone(x, y);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- the game's arsenal ----
    private async Task Extend()
    {
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var game = Arsenals.Info(from)!;
        long runs = 0;
        var h = Arsenals.Extend(Vukah, new ArsenalSpec
        {
            Rules = [new ArsenalRule { Name = "every-vukah", Every = 0.2, If = [Is.Custom(_ => true, every: 0.2)], Do = [Act.Custom(_ => runs++)] }],
        });
        _handles.Add(h);
        await Steps(3);
        var mid = Arsenals.Info(from)!;
        Check(mid.IsKiln && mid.Address != game.Address && mid.Rules.Count == game.Rules.Count + 1 && mid.Attacks.Count == game.Attacks.Count,
            $"extending the game's arsenal: {game.Rules.Count} -> {mid.Rules.Count} rules, same attacks, its record now Kiln's");
        var a = await One(Vukah, 0, 0);
        Creatures.Of(a).ForceReady();
        await Steps(120);
        Check(runs > 0, $"a plain Vukah with no private anything runs the rule ({runs} times)");
        h.Dispose();
        _handles.Remove(h);
        await Steps(3);
        var back = Arsenals.Info(from)!;
        Check(!back.IsKiln && back.Address == game.Address && back.Rules.Count == game.Rules.Count, "letting go restores the game's own record");
        var r0 = runs;
        await Steps(60);
        Check(runs == r0 && World.Exists(a), $"and the creature goes on with the game's rules, in {Creatures.Of(a).State}");
        Gone(a);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- a rule goes away mid-run ----
    private async Task Shrink()
    {
        // A creature inside a rule of ours holds that rule's index, and the game does not check it against the list.
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var game = Arsenals.Info(from)!;
        var h = Arsenals.Extend(from, new ArsenalSpec
        {
            Rules = [new ArsenalRule { Name = "long-pause", Every = 0.2, If = [Is.HealthBelow(1)], Do = [Act.Game("PauseAction", ("minDuration", 600.0), ("maxDuration", 600.0))] }],
        });
        await Steps(3);
        var a = await One(Vukah, 0, 0);
        Creatures.Of(a).ForceReady();
        int index = 255;
        for (var i = 0; i < 200 && index == 255; i++)
        {
            await Steps(1);
            index = World.ReadBytes(a, "DynamicEnemy01BehaviorState")?[0x410] ?? 255;
        }
        Check(index == game.Rules.Count, $"a creature is inside the rule we add: current rule {index}");
        h.Dispose();
        await Steps(10);
        var after = World.ReadBytes(a, "DynamicEnemy01BehaviorState")?[0x410] ?? 0;
        Check(after == 255 && !Arsenals.Info(from)!.IsKiln, $"the rule goes away under it: its index is reset ({index} -> {after}), the game's own record is back");
        await Steps(60);
        Check(World.Exists(a) && Creatures.Of(a).Inspect() != null, $"and it runs on: {Creatures.Of(a).State}");
        Gone(a);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- data packs ----
    private async Task Pack()
    {
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var game = Arsenals.Info(from)!;
        long named = 0;
        Arsenals.Action("test.count", _ => named++);
        Arsenals.Condition("test.always", _ => true, every: 0.2);
        const string json = """
        { "arsenals": [
            { "extends": "Enemy_Vukah_Melee",
              "rules": [ { "name": "packed", "every": 0.2, "when": [ { "custom": "test.always" }, { "healthBelow": 1 }, { "not": { "healthBelow": 0.0001 } } ], "do": [ { "custom": "test.count" } ] } ] },
            { "id": "from-pack", "from": "Enemy_Vukah_Melee",
              "attacks": [ { "name": "punches", "sequence": "6b05974c-1e8b-4709-b081-ed43d0dcbcb6", "style": ["Special"], "maxRange": 3.5 } ] }
        ] }
        """;
        var report = Creatures.ApplyJson(json, "npc-rules-pack", "test.json");
        Check(report.Ok && report.Applied == 2, $"a data pack: {report.Applied} arsenals, errors [{string.Join("; ", report.Errors)}]");
        // a mod on the same arsenal merges with the pack
        long mine = 0;
        var h = Arsenals.Extend(Vukah, new ArsenalSpec { Rules = [new ArsenalRule { Name = "mine", Every = 0.2, If = [Is.HealthBelow(1)], Do = [Act.Custom(_ => mine++)] }] });
        _handles.Add(h);
        await Steps(3);
        var m = Arsenals.Info(from)!;
        Check(m.Rules.Count == game.Rules.Count + 2, $"two owners merge on one arsenal: {game.Rules.Count} -> {m.Rules.Count} rules");
        var other = Arsenals.Info(RuleIds.Derive("npc-rules-pack", "arsenal", "from-pack"));
        Check(other is { Attacks.Count: 4 }, $"a pack's new arsenal: {other?.Attacks.Count} attacks");
        var a = await One(Vukah, 0, 0);
        Creatures.Of(a).ForceReady();
        await Steps(120);
        Check(named > 0 && mine > 0, $"both run: the pack's {named} times, the mod's {mine}");
        Creatures.RevertJson("npc-rules-pack");
        await Steps(3);
        var n = Arsenals.Info(from)!;
        Check(n.Rules.Count == game.Rules.Count + 1 && n.IsKiln, "reverting the pack leaves the mod's rule");
        h.Dispose();
        _handles.Remove(h);
        await Steps(3);
        var z = Arsenals.Info(from)!;
        Check(!z.IsKiln && z.Address == game.Address, "and with the mod's gone too, the game's own record is back");
        Gone(a);
        await Steps(3);
    }

    // ---------------------------------------------------------------------------------------------- unload ----
    private async Task Unload()
    {
        const string id = Companion;
        if (_companion == null)
        {
            Check(false, "the companion mod is not there: the unload check did not run");
            return;
        }
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        for (var round = 0; round < 3; round++)
        {
            Info($"round {round}: load: {await Mods.LoadAsync(_companion)}");
            await Steps(10);
            var a = await One(Vukah, 0, 0);
            var b = await One(Vukah, 3, 0);
            Creatures.Of(a).ForceReady();
            Creatures.Of(b).ForceReady();
            await Steps(30);
            Mods.Send(id, "own", $"{a.Value} {b.Value}");
            await Steps(60);
            var s0 = Arsenals.RulesState!;
            var infoLoaded = Arsenals.Info(from)!;
            Check(infoLoaded.IsKiln && s0.Overrides >= 1 && s0.AiOff == 1 && s0.RuleHooks && s0.AttackHooks && !Creatures.Of(b).AiEnabled,
                $"round {round}: the companion holds an arsenal, an override, an AI switch and the hooks ({s0.Overrides} override, {s0.AiOff} off, constraints {s0.Constraints})");
            var status = await Mods.UnloadAsync(id);
            Info($"round {round}: unload: {status}");
            await Steps(5);
            var s1 = Arsenals.RulesState!;
            var info = Arsenals.Info(from)!;
            Check(!info.IsKiln && s1.Arsenals == 0 && s1.Overrides == 0 && s1.AiOff == 0, $"round {round}: after the unload: the game's arsenal, {s1.Arsenals} Kiln arsenals, {s1.Overrides} overrides, {s1.AiOff} AI switches");
            Check(Creatures.Of(b).AiEnabled, $"round {round}: the creature's AI is on again");
            Check(!s1.RuleHooks && !s1.AttackHooks, $"round {round}: no hook left (rule hooks {s1.RuleHooks}, attack hooks {s1.AttackHooks})");
            var c0 = s1.Constraints;
            await Steps(60);
            var s2 = Arsenals.RulesState!;
            Check(s2.Constraints == c0 && s2.Actions == s1.Actions && World.Exists(a), $"round {round}: nothing of it runs any more; creatures live on ({Creatures.Of(a).State})");
            Gone(a, b);
            await Steps(10);
        }
    }

    // ---------------------------------------------------------------------------------------------- cost ----
    private async Task Cost()
    {
        var from = Arsenals.OfTemplate(Vukah)!.Value;
        var pairs = new List<(EntityId, EntityId)>();
        for (var i = 0; i < 50; i++)
        {
            var dx = (i % 10) * 6.0;
            var dz = (i / 10) * 6.0;
            var a = await One(Vukah, dx, dz + 12);
            var b = await One(Vukah, dx + 1.5, dz + 12);
            var ca = Creatures.Of(a);
            var cb = Creatures.Of(b);
            ca.Level = 60;
            cb.Level = 60;
            ca.Faction = Faction.PlayerAlly;
            ca.ForceReady();
            cb.ForceReady();
            pairs.Add((a, b));
        }
        void Goad()
        {
            foreach (var (a, b) in pairs)
            {
                if (World.Exists(a) && World.Exists(b))
                {
                    Creatures.Of(a).Command.Attack(b, 5000);
                    Creatures.Of(b).Command.Attack(a, 5000);
                }
            }
        }
        // the sim step's wall time, Pre to Post
        var sw = new Stopwatch();
        double total = 0;
        long samples = 0;
        TickHandler pre = (in TickInfo _) => sw.Restart();
        TickHandler post = (in TickInfo _) =>
        {
            total += sw.Elapsed.TotalMilliseconds;
            samples++;
        };
        Context.Tick.Pre += pre;
        Context.Tick.Post += post;
        async Task<double> Measure(int steps)
        {
            total = 0;
            samples = 0;
            for (var i = 0; i < steps; i++)
            {
                if (i % 40 == 0) Goad();
                await Steps(1);
            }
            return samples > 0 ? total / samples : 0;
        }
        Goad();
        await Steps(100);
        var noHooks0 = await Measure(400);
        Info($"100 creatures, no hooks: {noHooks0:F2} ms per step over {samples} steps");
        long fired = 0;
        var cond = Is.Custom(npc => npc.HealthMax > 0, every: 0.5);
        var h = Arsenals.Define("costly", new ArsenalSpec
        {
            From = from,
            Rules =
            [
                new ArsenalRule { Name = "c1", Every = 0.25, If = [cond], Do = [Act.Custom(_ => fired++)] },
                new ArsenalRule { Name = "c2", Every = 0.25, If = [Is.HasBuff(Heal), cond.Not()], Do = [Act.Custom(_ => fired++)] },
                new ArsenalRule { Name = "c3", Every = 0.25, If = [Is.Night, Is.InPhase("never")], Do = [Act.Custom(_ => fired++)] },
            ],
        });
        _handles.Add(h);
        await Steps(2);
        foreach (var (a, b) in pairs)
        {
            if (World.Exists(a)) Creatures.Of(a).Arsenal = h.Id;
            if (World.Exists(b)) Creatures.Of(b).Arsenal = h.Id;
            if (World.Exists(a)) Creatures.Of(a).Attacks.Allow(0, 1, 2);
            if (World.Exists(b)) Creatures.Of(b).Attacks.Allow(0, 1, 2);
        }
        await Steps(100);
        Arsenals.Timing = true;
        var s0 = Arsenals.RulesState!;
        var p0 = Arsenals.PumpCost;
        var hooks = await Measure(400);
        var steps = samples;
        var s1 = Arsenals.RulesState!;
        var p1 = Arsenals.PumpCost;
        Arsenals.Timing = false;
        var alive = pairs.Sum(p => (World.Exists(p.Item1) ? 1 : 0) + (World.Exists(p.Item2) ? 1 : 0));
        var jobNs = s1.JobTickHz > 0 ? (s1.JobTicks - s0.JobTicks) * 1e9 / s1.JobTickHz : 0;
        Info($"100 creatures ({alive} alive), rules and overrides on: {hooks:F2} ms per step over {steps} steps");
        Info($"per step: tick side {(s1.PreNs - s0.PreNs) / 1000.0 / Math.Max(1, s1.PreCalls - s0.PreCalls):F1} us (table {s1.Tracked} creatures); job side {jobNs / 1000.0 / Math.Max(1, steps):F1} us over {(double)(s1.JobCalls - s0.JobCalls) / Math.Max(1, steps):F1} Kiln kinds at {jobNs / Math.Max(1, s1.JobCalls - s0.JobCalls):F0} ns each");
        Info($"per step: managed pump {(p1.Milliseconds - p0.Milliseconds) * 1000 / Math.Max(1, steps):F1} us over {p1.Pumps - p0.Pumps} pumps");
        Info($"per step: constraints {(double)(s1.Constraints - s0.Constraints) / Math.Max(1, steps):F2}, actions {(double)(s1.Actions - s0.Actions) / Math.Max(1, steps):F2}, selects {(double)(s1.Selects - s0.Selects) / Math.Max(1, steps):F2}, starts {(double)(s1.Starts - s0.Starts) / Math.Max(1, steps):F2}; actions run {fired}");
        h.Dispose();
        _handles.Remove(h);
        foreach (var (a, b) in pairs)
        {
            if (World.Exists(a)) Creatures.Of(a).Attacks.Clear();
            if (World.Exists(b)) Creatures.Of(b).Attacks.Clear();
        }
        await Steps(5);
        var noHooks1 = await Measure(400);
        Info($"100 creatures, hooks out again: {noHooks1:F2} ms per step over {samples} steps ({Arsenals.RulesState!.RuleHooks}/{Arsenals.RulesState!.AttackHooks})");
        Check(hooks < Math.Max(noHooks0, noHooks1) * 1.25 + 1, $"the rules cost about {hooks - (noHooks0 + noHooks1) / 2:F2} ms per step against {(noHooks0 + noHooks1) / 2:F2} ms without them");
        Context.Tick.Pre -= pre;
        Context.Tick.Post -= post;
        foreach (var (a, b) in pairs) Gone(a, b);
        await Steps(5);
    }
}
