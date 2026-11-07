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
}

internal static class AiRuntime
{
    internal static readonly Dictionary<string, Func<NpcView, EntityId, bool>> Filters = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, AiCreature> ByVariant = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, Tracked> Live = new();
    private static readonly Dictionary<uint, List<(EntityId E, long Until)>> Summons = new();
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
        public readonly HashSet<uint> Refused = new();
    }

    // ---- registration ----

    /// <summary>A creature block compiled: what of its AI needs the runtime. Nothing when it has no relations, filter or flee of its own.</summary>
    internal static void Attach(NpcCreature info, AIBlock ai)
    {
        var filter = ai.SenseFilter ?? (ai.SenseFilterName is { } n ? NpcRuntime.Lookup(Filters, n) : null);
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
            ByVariant[info.Variant] = new AiCreature { Info = info, Row = row, Filter = filter, Every = ai.SenseEvery ?? 0.5, Range = Math.Max(ai.Sight ?? info.Sight, 8) * 1.25 };
        }
        Watch(info.Mod);
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
        lock (Gate) Watched.Add(mod);
        EnsureTick();
    }

    internal static void Released(string mod)
    {
        lock (Gate) Watched.Remove(mod);
        AiNative.Forget(mod);
    }

    private static void EnsureTick()
    {
        lock (Gate) _tick ??= BlocksBridge.AddTick("kiln creature ai", OnTick);
    }

    private static unsafe void Track(AiCreature c, EntityId e)
    {
        lock (Gate)
            if (Live.ContainsKey(e.Value))
                return;
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
        lock (Gate) Live[e.Value] = new Tracked { Creature = c, Entity = e };
    }

    // ---- the tick ----

    internal static void OnTick(ulong step)
    {
        if (!NpcServerSide.Now) return;
        _now = Clock.ElapsedMilliseconds;
        if (step % 24 == 11) Poll();
        if (step % 48 == 23) CheckMods();
        List<Tracked> live;
        lock (Gate) live = Live.Values.ToList();
        foreach (var tr in live)
        {
            if (!World.Exists(tr.Entity) || Dead(tr.Entity))
            {
                Forget(tr);
                continue;
            }
            if (tr.Creature.Filter is { } filter && _now >= tr.NextLook)
            {
                tr.NextLook = _now + (long)(tr.Creature.Every * 1000);
                Look(tr, filter);
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

    /// <summary>A mod that set relations unloaded: what it set goes.</summary>
    private static unsafe void CheckMods()
    {
        List<string> gone;
        lock (Gate) gone = Watched.Where(m => !Mods.IsLoaded(m)).ToList();
        foreach (var m in gone)
        {
            var t = AiNative.T;
            if (t != null) t->OwnerRelease(AiNative.Owner(m));
            lock (Gate)
            {
                Watched.Remove(m);
                foreach (var v in ByVariant.Where(x => x.Value.Info.Mod == m).Select(x => x.Key).ToList()) ByVariant.Remove(v);
                foreach (var k in Live.Where(x => x.Value.Creature.Info.Mod == m).Select(x => x.Key).ToList()) Live.Remove(k);
            }
            AiNative.Forget(m);
            BlocksBridge.Log(LogLevel.Info, $"blocks: {m} unloaded: its creature relations went with it");
        }
    }

    /// <summary>The SenseFilter's look: everything it could notice within its range, asked once each; what it refuses it ignores until asked again.</summary>
    private static unsafe void Look(Tracked tr, Func<NpcView, EntityId, bool> filter)
    {
        var t = AiNative.T;
        if (t == null || !World.TryGetPosition(tr.Entity, out var at)) return;
        var owner = AiNative.Owner(tr.Creature.Info.Mod);
        var view = new NpcView(tr.Entity);
        var seen = new HashSet<uint>();
        var hold = (float)Math.Max(tr.Creature.Every * 4, 2);
        void Ask(EntityId other)
        {
            if (other == tr.Entity || !seen.Add(other.Value)) return;
            bool ok;
            Interlocked.Increment(ref FilterCalls);
            try { ok = filter(view, other); }
            catch (Exception x)
            {
                BlocksBridge.Log(LogLevel.Error, $"blocks: creature {tr.Creature.Info.Name}: its sense filter threw, and is off for {tr.Entity}: {x.Message}");
                tr.Creature.Filter = null;
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
            return Summons.TryGetValue(summoner.Value, out var l) ? l.Select(x => x.E).Where(World.Exists).ToList() : [];
    }

    internal static int SummonsOf(EntityId summoner)
    {
        lock (Gate)
            return Summons.TryGetValue(summoner.Value, out var l) ? l.Count(x => World.Exists(x.E) && !Dead(x.E)) : 0;
    }

    // ---- actions ----

    internal static void Heal(NpcView npc, double fraction)
    {
        if (npc.HealthMax <= 0 || npc.Health <= 0) return;
        Combat.Heal(npc.Entity, Math.Max(1, (int)Math.Round(npc.HealthMax * fraction)));
    }

    internal static RuleAction SummonAction(SummonSpec spec) => Act.Custom(npc => _ = Summon(spec, npc));

    private static async Task Summon(SummonSpec s, NpcView npc)
    {
        if (!World.TryGetPosition(npc.Entity, out var at)) return;
        var (variant, handle) = Resolve(s);
        if (variant == null)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {s.Mod}: a rule summons \"{s.Creature}\", which is no creature block, variant or game creature");
            return;
        }
        var side = s.Side == SummonSide.Summoner ? SafeFaction(npc.Entity) : Faction.None;
        var target = npc.Target;
        var rng = Random.Shared;
        for (var i = 0; i < s.Count; i++)
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
            Interlocked.Increment(ref SummonsMade);
            if (handle != null)
            {
                NpcRuntime.Spawned(handle.Info, e);
                Spawned(handle.Info, e);
            }
            lock (Gate)
            {
                if (!Summons.TryGetValue(npc.Entity.Value, out var list)) Summons[npc.Entity.Value] = list = new();
                list.Add((e, s.Seconds > 0 ? Clock.ElapsedMilliseconds + (long)(s.Seconds * 1000) : 0));
            }
            try
            {
                var c = Creatures.Of(e);
                if (side != Faction.None) c.Faction = side;
                if (!target.IsNone && World.Exists(target)) c.Command.Attack(target, 5000);
            }
            catch (Exception x) when (x is NotSupportedException or InvalidOperationException or KeyNotFoundException or ArgumentException) { }
        }
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

    private static void ExpireSummons()
    {
        List<EntityId> drop = [];
        lock (Gate)
        {
            if (Summons.Count == 0) return;
            foreach (var (k, list) in Summons.ToList())
            {
                foreach (var x in list.Where(x => x.Until > 0 && x.Until <= _now)) drop.Add(x.E);
                list.RemoveAll(x => (x.Until > 0 && x.Until <= _now) || !World.Exists(x.E));
                if (list.Count == 0) Summons.Remove(k);
            }
        }
        foreach (var e in drop)
            try { Spawn.Destroy(e); } catch (Exception) { }
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
        var t = Abilities.RunProgram(program, at, options);
        if (t.IsFaulted) BlocksBridge.Log(LogLevel.Warn, $"blocks: creature {c.Name}: a cast did not start: {t.Exception?.GetBaseException().Message}");
        else Interlocked.Increment(ref CastsRun);
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
    /// <summary>The creatures this one summoned that still live.</summary>
    public static IReadOnlyList<EntityId> SummonsOf(EntityId summoner) => AiRuntime.SummonList(summoner);
}
