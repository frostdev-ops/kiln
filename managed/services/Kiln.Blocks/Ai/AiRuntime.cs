// SPDX-License-Identifier: MIT
// What an AIBlock needs while the game runs: each creature of the block gets its own relations (kiln.npcai's row for that entity) and its
// SenseFilter's answers (pairs, refreshed every SenseEvery seconds for what is within its sight); summons are tracked for Is.SummonsBelow and
// their lifetime; casts and heals run here, queued by rules. Tick thread of a server world only (a dedicated server's, or one a client hosts).
namespace Kiln;

/// <summary>A creature block's AI as the runtime knows it.</summary>
internal sealed class AiCreature
{
    public required NpcCreature Info;
    public byte[]? Row;                                   // 256 relations, null: none of its own
    public Func<NpcView, EntityId, bool>? Filter;
    public double Every = 0.5;
    public double Range = 30;
    public ModContext? Context;
}

internal static class AiRuntime
{
    internal static readonly Dictionary<string, Func<NpcView, EntityId, bool>> Filters = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AiCreature> ByVariant = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, Tracked> Live = new();
    private sealed record Summoned(EntityId E, long Until, ulong Lifetime, int Depth, AiLimits.Lease Lease);
    private static readonly Dictionary<(uint Entity, ulong Life), List<Summoned>> Summons = new();
    private static readonly AiLimits Limits = new();
    private static readonly Dictionary<string, ModContext?> FilterContexts = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> FilterMods = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ModInfo?> Instances = new(StringComparer.Ordinal);
    private static ulong _step;
    private static readonly HashSet<string> LifetimeWarnings = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Watched = new(StringComparer.Ordinal);
    private static IDisposable? _tick;
    private static long _now;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Counters for tests and <c>/npcai</c>: filter calls, pairs set neutral, rows written, summons made, casts run.</summary>
    internal static long FilterCalls, FilterRefused, RowsWritten, SummonsMade, CastsRun;

    private sealed class Tracked
    {
        public required AiCreature Creature;
        public required EntityId Entity;
        public long NextLook;
        public ulong Lifetime;
        public bool FilterFailed, LookPending;
        public readonly HashSet<uint> Refused = new();
    }

    // ---- registration ----

    /// <summary>A creature block compiled: what of its AI needs the runtime. Nothing when it has no relations, filter or flee of its own.</summary>
    internal static void Attach(NpcCreature info, AIBlock ai)
    {
        Watch(info.Mod);
        var filter = ai.SenseFilter ?? (ai.SenseFilterName is { } n ? FindFilter(info.Mod, n) : null);
        byte[]? row = null;
        if (ai.Relations is { Count: > 0 } rel)
        {
            row = new byte[256];
            foreach (var (f, r) in rel) row[(byte)f] = (byte)r;
        }
        lock (Gate)
        {
            if (row == null && filter == null)
            {
                ByVariant.Remove(info.Variant);
                return;
            }
            ByVariant[info.Variant] = new AiCreature { Info = info, Row = row, Filter = filter, Context = ai.SenseFilterName is { } fn ? FilterContext(info.Mod, fn) ?? info.Owner.Mod : info.Owner.Mod, Every = ai.SenseEvery ?? 0.5, Range = Math.Max(ai.Sight ?? info.Sight, 8) * 1.25 };
        }
        if (filter != null) ObserveUnload(info.Mod, filter);
        EnsureTick();
    }

    /// <summary>A creature of a block appeared (spawned by the block's handle).</summary>
    internal static void Spawned(NpcCreature info, EntityId e)
    {
        AiCreature? c;
        lock (Gate) ByVariant.TryGetValue(info.Variant, out c);
        if (c != null && NpcServerSide.Now) Track(c, e);
    }

    internal static void Watch(string mod)
    {
        var instance = Mods.Get(mod)?.Info;
        lock (Gate)
        {
            if (Watched.Contains(mod) && !ReferenceEquals(Instances.GetValueOrDefault(mod), instance)) Retire(mod);
            Watched.Add(mod);
            Instances[mod] = instance;
        }
        EnsureTick();
    }

    internal static void Released(string mod)
    {
        // Reset clears native relations, but registered filters still need unload cleanup.
        lock (Gate)
            foreach (var tr in Live.Values.Where(x => x.Creature.Info.Mod == mod)) tr.Refused.Clear();
    }

    private static void EnsureTick()
    {
        lock (Gate) _tick ??= BlocksBridge.AddTick("kiln creature ai", OnTick);
    }

    private static unsafe void Track(AiCreature c, EntityId e)
    {
        var lifetime = AiNative.Lifetime(e);
        if (lifetime == 0) return;
        lock (Gate)
        {
            if (Live.TryGetValue(e.Value, out var old))
            {
                if (old.Lifetime == lifetime && ReferenceEquals(old.Creature, c)) return;
                Forget(old);
            }
        }
        var t = AiNative.T;
        if (c.Row != null && t != null)
        {
            fixed (byte* p = c.Row)
            {
                var rc = t->EntityRow(AiNative.Owner(c.Info.Mod), e.Value, p);
                if (rc == AiNative.Ok) Interlocked.Increment(ref RowsWritten);
                else BlocksBridge.Log(LogLevel.Warn, $"blocks: creature {c.Info.Name}: its relations could not be set for {e}: {AiNative.Describe(rc)}");
            }
        }
        lock (Gate) Live[e.Value] = new Tracked { Creature = c, Entity = e, Lifetime = lifetime };
    }

    // ---- the tick ----

    internal static void OnTick(ulong step)
    {
        if (!NpcServerSide.Now) return;
        _step = step;
        BeginBudget();
        _now = Clock.ElapsedMilliseconds;
        CheckMods();
        if (step % 24 == 11) Poll();
        List<Tracked> live;
        lock (Gate) live = Live.Values.ToList();
        foreach (var tr in live)
        {
            if (!World.Exists(tr.Entity) || Dead(tr.Entity) || tr.Lifetime != AiNative.Lifetime(tr.Entity))
            {
                Forget(tr);
                continue;
            }
            if (!tr.FilterFailed && !tr.LookPending && tr.Creature.Filter is { } filter && _now >= tr.NextLook)
            {
                tr.NextLook = _now + (long)(tr.Creature.Every * 1000);
                // Scheduler establishes the owning mod's context as well as the tick thread. Unload drops queued work.
                if (tr.Creature.Context is { } context)
                {
                    tr.LookPending = true;
                    context.Scheduler.Post(() =>
                    {
                        tr.LookPending = false;
                        if (NpcServerSide.Now && World.Exists(tr.Entity) && tr.Lifetime == AiNative.Lifetime(tr.Entity) &&
                            Live.TryGetValue(tr.Entity.Value, out var current) && ReferenceEquals(current, tr)) Look(tr, filter);
                    });
                }
            }
        }
        ExpireSummons();
    }

    private static bool Dead(EntityId e) => Creatures.Of(e).Inspect() is { HealthMax: > 0, Health: <= 0 };

    private static unsafe void Forget(Tracked tr)
    {
        lock (Gate) Live.Remove(tr.Entity.Value);
        var t = AiNative.T;
        if (t == null) return;
        var o = AiNative.Owner(tr.Creature.Info.Mod);
        t->EntityRow(o, tr.Entity.Value, null);
        t->PairClear(o, tr.Entity.Value);
    }

    /// <summary>Creatures of a block that the game spawned (spawn rules, saves), which no block call saw.</summary>
    private static void Poll()
    {
        Dictionary<string, AiCreature> byVariant;
        lock (Gate)
        {
            if (ByVariant.Count == 0) return;
            byVariant = new(ByVariant, StringComparer.Ordinal);
        }
        foreach (var e in World.Find("Enemy01BehaviorState"))
        {
            lock (Gate)
                if (Live.ContainsKey(e.Value))
                    continue;
            if (CreatureVariants.Of(e) is { } v && byVariant.TryGetValue(v.Id, out var c)) Track(c, e);
        }
    }

    internal static void RegisterFilter(string name, Func<NpcView, EntityId, bool> filter)
    {
        var context = Creatures.OwnerKey.Caller.Mod;
        var mod = context?.Info.Id ?? BlocksBridge.CallerModId ?? "kiln";
        Watch(mod);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(filter);
        var key = context == null || name.Contains(':') ? name : $"{mod}:{name}";
        lock (Gate)
        {
            Filters[key] = filter;
            FilterContexts[key] = context;
            FilterMods[key] = mod;
        }
        ObserveUnload(mod, filter);
    }

    private static string FilterKey(string mod, string name) => !name.Contains(':') && Filters.ContainsKey($"{mod}:{name}") ? $"{mod}:{name}" : name;

    internal static Func<NpcView, EntityId, bool>? FindFilter(string mod, string name)
    {
        lock (Gate) return Filters.GetValueOrDefault(FilterKey(mod, name));
    }

    private static ModContext? FilterContext(string mod, string name)
    {
        lock (Gate) return FilterContexts.GetValueOrDefault(FilterKey(mod, name));
    }

    private static void ObserveUnload(string mod, Delegate callback)
    {
        if (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(callback.Method.Module.Assembly) is { IsCollectible: true } alc)
            alc.Unloading += _ => Retire(mod);
    }

    private static void CheckMods()
    {
        List<string> gone;
        lock (Gate) gone = Watched.Where(m => !Mods.IsLoaded(m) || !ReferenceEquals(Instances.GetValueOrDefault(m), Mods.Get(m)?.Info)).ToList();
        foreach (var mod in gone) Retire(mod);
    }

    internal static unsafe void Retire(string mod)
    {
        var t = AiNative.T;
        if (t != null) t->OwnerRelease(AiNative.Owner(mod));
        lock (Gate)
        {
            Watched.Remove(mod);
            Instances.Remove(mod);
            foreach (var v in ByVariant.Where(x => x.Value.Info.Mod == mod).Select(x => x.Key).ToList()) ByVariant.Remove(v);
            foreach (var k in Live.Where(x => x.Value.Creature.Info.Mod == mod).Select(x => x.Key).ToList()) Live.Remove(k);
            foreach (var name in FilterMods.Where(x => x.Value == mod).Select(x => x.Key).ToList())
            {
                Filters.Remove(name);
                FilterContexts.Remove(name);
                FilterMods.Remove(name);
            }
            Limits.Release(mod);
            LifetimeWarnings.Remove(mod);
        }
        AiNative.Forget(mod);
    }

    /// <summary>The SenseFilter's look: everything it could notice within its range, asked once each; what it refuses it ignores until asked again.</summary>
    private static unsafe void Look(Tracked tr, Func<NpcView, EntityId, bool> filter)
    {
        if (!NpcServerSide.Now) return;
        var t = AiNative.T;
        if (t == null || !World.TryGetPosition(tr.Entity, out var at)) return;
        var owner = AiNative.Owner(tr.Creature.Info.Mod);
        var view = new NpcView(tr.Entity);
        var seen = new HashSet<uint>();
        var hold = (float)Math.Max(tr.Creature.Every * 4, 2);
        void Ask(EntityId other)
        {
            if (tr.FilterFailed || seen.Count >= 256 || other == tr.Entity || !seen.Add(other.Value)) return;
            bool ok;
            Interlocked.Increment(ref FilterCalls);
            try { ok = filter(view, other); }
            catch (Exception x)
            {
                BlocksBridge.Log(LogLevel.Error, $"blocks: creature {tr.Creature.Info.Name}: its sense filter threw, and is off for {tr.Entity}: {x.Message}");
                tr.FilterFailed = true;
                tr.Refused.Clear();
                t->PairClear(owner, tr.Entity.Value);
                return;
            }
            if (!ok)
            {
                Interlocked.Increment(ref FilterRefused);
                t->PairSet(owner, tr.Entity.Value, other.Value, (uint)Relation.Neutral, hold);
                tr.Refused.Add(other.Value);
            }
            else if (tr.Refused.Remove(other.Value))
            {
                t->PairSet(owner, tr.Entity.Value, other.Value, (uint)Relation.Game, 0);
            }
        }
        foreach (var p in Players.All)
            if (!p.Entity.IsNone && World.TryGetPosition(p.Entity, out var pp) && pp.DistanceTo(at) <= tr.Creature.Range)
                Ask(p.Entity);
        foreach (var e in Creatures.Near(at, tr.Creature.Range)) Ask(e);
        // out of range now: what it refused before is forgotten (the pair runs out by itself)
        tr.Refused.RemoveWhere(x => !seen.Contains(x));
    }

    // ---- conditions that read the world ----

    /// <summary>Creatures (and players) within metres that it would fight (enemies) or that are on its side, by the game's checks with Kiln's in them.</summary>
    internal static int CountNear(EntityId me, double metres, bool enemies)
    {
        if (!World.TryGetPosition(me, out var at)) return 0;
        var n = 0;
        var useHooks = AiNative.Loaded && Factions.Available;
        var mine = SafeFaction(me);
        bool Counts(EntityId other)
        {
            if (useHooks)
            {
                try
                {
                    var (fights, friends) = Factions.Between(me, other);
                    return enemies ? fights : friends;
                }
                catch (Exception x) when (x is KeyNotFoundException or NotSupportedException) { return false; }
            }
            var hostile = NpcRuntime.Hostile(mine, SafeFaction(other));
            return enemies ? hostile : !hostile && mine == SafeFaction(other);
        }
        foreach (var e in Creatures.Near(at, metres))
            if (e != me && !Dead(e) && Counts(e)) n++;
        if (enemies)
            foreach (var p in Players.All)
                if (!p.Entity.IsNone && World.TryGetPosition(p.Entity, out var pp) && pp.DistanceTo(at) <= metres && Counts(p.Entity))
                    n++;
        return n;
    }

    private static Faction SafeFaction(EntityId e)
    {
        try { return Creatures.Of(e).Faction; }
        catch (Exception x) when (x is NotSupportedException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return Faction.None; }
    }

    internal static IReadOnlyList<EntityId> SummonList(EntityId summoner)
    {
        lock (Gate)
            return Summons.TryGetValue((summoner.Value, AiNative.Lifetime(summoner)), out var l) ? l.Where(Alive).Select(x => x.E).ToList() : [];
    }

    internal static int SummonsOf(EntityId summoner)
    {
        lock (Gate)
            return Summons.TryGetValue((summoner.Value, AiNative.Lifetime(summoner)), out var l) ? l.Count(Alive) : 0;
    }

    // ---- actions ----

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RuleAction, object> BoundedActions = new();

    internal static RuleAction? Bounded(RuleAction action)
    {
        if (BoundedActions.TryGetValue(action, out _)) return action;
        if (action.Use?.Fn is not { } run) return null;
        EnsureTick();
        var bounded = Act.Custom(npc =>
        {
            BeginBudget();
            if (NpcServerSide.Now && World.Exists(npc.Entity) && !Dead(npc.Entity) && Limits.Action(npc.Entity.Value)) run(npc);
        });
        BoundedActions.Add(bounded, new object());
        return bounded;
    }

    private static void BeginBudget()
    {
        var step = AiNative.Step;
        Limits.Begin(step == 0 ? _step : step);
    }

    internal static void Heal(NpcView npc, double fraction)
    {
        if (fraction <= 0 || npc.HealthMax <= 0 || npc.Health <= 0) return;
        Combat.Heal(npc.Entity, (int)Math.Clamp(Math.Round(npc.HealthMax * fraction), 0, int.MaxValue));
    }

    internal static RuleAction SummonAction(SummonSpec spec) => Bounded(Act.Custom(npc => _ = SummonGuarded(spec, npc)))!;

    private static async Task SummonGuarded(SummonSpec spec, NpcView npc)
    {
        try { await Summon(spec, npc); }
        catch (Exception x) { BlocksBridge.Log(LogLevel.Warn, $"blocks: {spec.Mod}: a summon failed: {x.Message}"); }
    }

    private static async Task Summon(SummonSpec s, NpcView npc)
    {
        if (!NpcServerSide.Now || !World.TryGetPosition(npc.Entity, out var at)) return;
        Watch(s.Mod);
        var parentLife = AiNative.Lifetime(npc.Entity);
        if (parentLife == 0)
        {
            if (LifetimeWarnings.Add(s.Mod)) BlocksBridge.Log(LogLevel.Warn, $"blocks: {s.Mod}: summon refused: entity lifetime tracking is unavailable on this build");
            return;
        }
        var depth = Summons.Values.SelectMany(x => x).FirstOrDefault(x => x.E == npc.Entity && x.Lifetime == parentLife)?.Depth ?? 0;
        var (variant, handle) = Resolve(s);
        if (variant == null)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {s.Mod}: a rule summons \"{s.Creature}\", which is no creature block, variant or game creature");
            return;
        }
        var side = s.Side == SummonSide.Summoner ? SafeFaction(npc.Entity) : Faction.None;
        var target = npc.Target;
        var targetLife = target.IsNone ? 0 : AiNative.Lifetime(target);
        var rng = Random.Shared;
        for (var i = 0; i < s.Count; i++)
        {
            if (!NpcServerSide.Now || parentLife != AiNative.Lifetime(npc.Entity) || Dead(npc.Entity)) return;
            BeginBudget();
            var lease = Limits.Reserve(s.Mod, npc.Entity.Value, depth, parentLife);
            if (lease == null) return;
            var claimed = false;
            EntityId made = default;
            ulong madeLife = 0;
            try
            {
                var a = rng.NextDouble() * Math.PI * 2;
                var d = s.Radius * (0.5 + 0.5 * rng.NextDouble());
                var pos = new WorldPosition(at.X + Math.Cos(a) * d, at.Y, at.Z + Math.Sin(a) * d);
                EntityId e;
                try { e = await Creatures.Spawn(variant, pos, (float)(rng.NextDouble() * 360), new CreatureSpawnOptions { Tag = $"blocks:{s.Mod}:summon", Flags = SpawnFlags.Transient }); }
                catch (Exception x) when (x is ArgumentException or KeyNotFoundException or InvalidOperationException or NotSupportedException)
                {
                    BlocksBridge.Log(LogLevel.Warn, $"blocks: {s.Mod}: a summon of \"{s.Creature}\" failed: {x.Message}");
                    return;
                }
                made = e;
                madeLife = AiNative.Lifetime(e);
                if (!NpcServerSide.Now || !lease.Active || parentLife != AiNative.Lifetime(npc.Entity))
                {
                    if (NpcServerSide.Now) Spawn.Destroy(e);
                    return;
                }
                var childLife = madeLife;
                if (childLife == 0) { Spawn.Destroy(e); return; }
                Interlocked.Increment(ref SummonsMade);
                if (handle != null)
                {
                    NpcRuntime.Spawned(handle.Info, e);
                    Spawned(handle.Info, e);
                }
                lock (Gate)
                {
                    if (!Summons.TryGetValue((npc.Entity.Value, parentLife), out var list)) Summons[(npc.Entity.Value, parentLife)] = list = new();
                    list.Add(new Summoned(e, s.Seconds > 0 ? Clock.ElapsedMilliseconds + (long)(s.Seconds * 1000) : 0, childLife, depth + 1, lease));
                }
                claimed = true;
                try
                {
                    var c = Creatures.Of(e);
                    if (side != Faction.None) c.Faction = side;
                    if (!target.IsNone && targetLife != 0 && targetLife == AiNative.Lifetime(target)) c.Command.Attack(target, 5000);
                }
                catch (Exception x) when (x is NotSupportedException or InvalidOperationException or KeyNotFoundException or ArgumentException) { }
            }
            finally
            {
                if (!claimed)
                {
                    lease.Dispose();
                    if (NpcServerSide.Now && madeLife != 0 && madeLife == AiNative.Lifetime(made)) Spawn.Destroy(made);
                }
            }
        }
        Watch(s.Mod);
        EnsureTick();
    }

    // A creature block of this mod or another ("mod:name"), a variant, or a game template, in that order.
    private static (string? Variant, CreatureHandle? Handle) Resolve(SummonSpec s)
    {
        var name = s.Creature;
        var mod = s.Mod;
        if (name.Contains(':'))
        {
            mod = name[..name.IndexOf(':')];
            name = name[(name.IndexOf(':') + 1)..];
        }
        if (Blocks.Find(name, mod) is CreatureHandle h) return (h.Variant, h);
        if (CreatureVariants.Get(s.Creature) != null) return (s.Creature, null);
        return (Creatures.Template(s.Creature) != null ? s.Creature : null, null);
    }

    private static bool Alive(Summoned x) => x.Lease.Active && World.Exists(x.E) && x.Lifetime == AiNative.Lifetime(x.E) && !Dead(x.E);

    private static void ExpireSummons()
    {
        List<Summoned> drop = [];
        lock (Gate)
        {
            if (Summons.Count == 0) return;
            foreach (var (k, list) in Summons.ToList())
            {
                foreach (var x in list.ToList())
                {
                    var alive = World.Exists(x.E) && x.Lifetime == AiNative.Lifetime(x.E);
                    if (alive && (!x.Lease.Active || x.Until > 0 && x.Until <= _now)) drop.Add(x);
                    if (!Alive(x) || x.Until > 0 && x.Until <= _now)
                    {
                        x.Lease.Dispose();
                        list.Remove(x);
                    }
                }
                if (list.Count == 0) Summons.Remove(k);
            }
        }
        foreach (var x in drop)
            try
            {
                if (World.Exists(x.E) && x.Lifetime == AiNative.Lifetime(x.E)) Spawn.Destroy(x.E);
            }
            catch (Exception) { }
    }

    internal static void Cast(NpcCreature c, Guid program, NpcView npc)
    {
        if (!World.TryGetPosition(npc.Entity, out var at)) return;
        var target = npc.Target;
        var yaw = 0f;
        if (!target.IsNone && World.TryGetPosition(target, out var tp))
            yaw = (float)(Math.Atan2(tp.X - at.X, tp.Z - at.Z) * 180 / Math.PI);
        var options = new ImpactOptions
        {
            Root = npc.Entity, YawDegrees = yaw, Tag = $"blocks:{c.Mod}:{c.Name}:cast",
            Configs = target.IsNone ? null : new ImpactConfigs { [$"0x{EffectEmitter.CfgTarget:x8}"] = target },
        };
        _ = RunCast(c, program, at, options);
    }

    private static async Task RunCast(NpcCreature c, Guid program, WorldPosition at, ImpactOptions options)
    {
        try
        {
            await Abilities.RunProgram(program, at, options);
            Interlocked.Increment(ref CastsRun);
        }
        catch (Exception x) { BlocksBridge.Log(LogLevel.Warn, $"blocks: creature {c.Name}: a cast did not start: {x.Message}"); }
    }
}

/// <summary>What the AI blocks did since the server started, for tests and admin commands. Tick thread.</summary>
public static class AiStats
{
    /// <summary>Sense filter calls, and how many of them refused.</summary>
    public static long FilterCalls => Interlocked.Read(ref AiRuntime.FilterCalls);
    public static long FilterRefused => Interlocked.Read(ref AiRuntime.FilterRefused);
    /// <summary>Creatures given their own relations.</summary>
    public static long Rows => Interlocked.Read(ref AiRuntime.RowsWritten);
    /// <summary>Creatures summoned by rules.</summary>
    public static long Summons => Interlocked.Read(ref AiRuntime.SummonsMade);
    /// <summary>Casts started by rules.</summary>
    public static long Casts => Interlocked.Read(ref AiRuntime.CastsRun);
    /// <summary>The creatures this one summoned that still live. Tick thread.</summary>
    public static IReadOnlyList<EntityId> SummonsOf(EntityId summoner)
    {
        Game.AssertTickThread();
        return AiRuntime.SummonList(summoner);
    }
}
