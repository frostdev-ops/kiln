// SPDX-License-Identifier: MIT
// Villagers: the game's friendly NPCs (npc_active_behavior) commanded through their own event, NpcCommandEvent. It
// addresses an NPC by NpcId, FNV-1a-32 of its template GUID (ActiveNpcState.npcId). Evidence: docs/re/creatures.md 7.6.
//   GoToPosition: state Roam, decision WantsToRoam, NpcCommon.currentTargetPosition = the point.
//   Reset: teleports it, ActiveNpcStateSaved.spawnPosition = the point, state Start (what the game does when a player
//   moves a placed NPC with its summon tool).
// Docs: docs/kiln/services/npcs.md.

namespace Kiln;

/// <summary>keen::active_npc::State.</summary>
public enum VillagerState : byte
{
    Invalid = 0, Start, FindBed, Idle, DecideTask, FindTarget, GoToTarget, Roam, Sleep, Sitting, Interacting, NpcSocializing,
    Eating, WaitingCloseToPlayer, AvoidBuildingCursor, AvoidWater, InsideWater, FinishTask,
}

/// <summary>A villager's state machine changed (<see cref="Villager.State"/>, the game's <c>DynamicActiveNpcState</c>). Polled about four times a second for villagers
/// Kiln commands or <see cref="Villagers.Watch"/>es, and once a second for every villager while someone subscribes. Tick
/// thread. Observe only.</summary>
public sealed class VillagerStateEvent : KilnEvent
{
    internal VillagerStateEvent(EntityId entity, uint npcId, VillagerState from, VillagerState to)
    {
        Entity = entity;
        NpcId = npcId;
        From = from;
        To = to;
    }

    public EntityId Entity { get; }
    public uint NpcId { get; }
    public VillagerState From { get; }
    public VillagerState To { get; }
    public override string ToString() => $"villager {Entity} (0x{NpcId:x8}): {From} -> {To}";
}

/// <summary>One friendly NPC. Commands are the game's NpcCommandEvent, sent in the next sim step.</summary>
public sealed class Villager
{
    internal Villager(EntityId entity, uint npcId)
    {
        Entity = entity;
        NpcId = npcId;
    }

    public EntityId Entity { get; }
    /// <summary>FNV-1a-32 of its template GUID, the id of the game's roster.</summary>
    public uint NpcId { get; }
    /// <summary>Its state machine now (Invalid when it is gone). Tick thread.</summary>
    public VillagerState State => World.TryRead<byte>(Entity, "DynamicActiveNpcState", out var s) ? (VillagerState)s : VillagerState.Invalid;
    public WorldPosition? Position => World.Position(Entity);
    public bool IsFollowing => Villagers.IsFollowing(Entity);

    /// <summary>Walks to <paramref name="target"/> (the game's GoToPosition). It finds its way with the path grid, which
    /// exists near players; then its normal behaviour goes on, and its home stays where it was. The game ignores this while the
    /// NPC is in state <see cref="VillagerState.Start"/>, where NPCs stay without a player. Ends a <see cref="Follow"/>.</summary>
    public void GoTo(WorldPosition target)
    {
        Villagers.StopFollow(Entity);
        Villagers.Send(1, NpcId, target, 0);
    }

    /// <summary>Teleports it to <paramref name="at"/> and makes that its home (the game's Reset). Ends a <see cref="Follow"/>.</summary>
    public void Teleport(WorldPosition at, float yawDegrees = 0)
    {
        Villagers.StopFollow(Entity);
        Villagers.Send(0, NpcId, at, yawDegrees);
    }

    /// <summary>Stays where it stands: that becomes its home. Ends a <see cref="Follow"/>.</summary>
    public void Stay()
    {
        Villagers.StopFollow(Entity);
        if (World.TryGetPosition(Entity, out var p)) Villagers.Send(0, NpcId, p, 0);
    }

    /// <summary>Keeps walking after <paramref name="target"/> (a GoTo when it is farther than <paramref name="distance"/>),
    /// and is teleported to it beyond <paramref name="catchUp"/> metres. Ends with <see cref="Stay"/>, <see cref="GoTo"/>,
    /// the target's or the villager's despawn or the calling mod's unload.</summary>
    public void Follow(EntityId target, double distance = 4, double catchUp = 40) => Villagers.StartFollow(this, target, distance, catchUp);

    public void StopFollowing() => Villagers.StopFollow(Entity);
    public override string ToString() => $"villager {Entity} (0x{NpcId:x8}) {State}";
}

/// <summary>Friendly NPCs (villagers, crafters, traders, recruited NPCs, clones): find them and command them.</summary>
public static unsafe class Villagers
{
    private const long PollMs = 250, AllPollMs = 1000, FollowMs = 1000;

    private sealed class Follower
    {
        public required Villager V;
        public required EntityId Target;
        public double Distance, CatchUp;
        public long NextMs;
        public ModContext? Mod;
    }

    private static readonly Dictionary<EntityId, Follower> Follows = [];
    private static readonly Dictionary<EntityId, (uint Npc, VillagerState State)> Watched = [];
    private static readonly Dictionary<EntityId, (uint Npc, VillagerState State)> All = [];
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static long _nextPoll, _nextAll;

    /// <summary>True when kiln.creatures can send the game's NpcCommandEvent on this build.</summary>
    public static bool Available
    {
        get
        {
            var t = CreaturesNative.T;
            return t != null && t->Header.Size >= (uint)sizeof(CreaturesNative.Table);
        }
    }

    /// <summary>The roster id of a template: FNV-1a-32 of its GUID bytes.</summary>
    public static uint NpcIdOf(Guid template)
    {
        Span<byte> b = stackalloc byte[16];
        template.TryWriteBytes(b);
        var h = 0x811c9dc5u;
        foreach (var x in b) h = (h ^ x) * 0x01000193u;
        return h;
    }

    /// <summary>The villager behind an entity; null when it is not one (no DynamicActiveNpcState). Tick thread.</summary>
    public static Villager? Of(EntityId entity)
    {
        if (entity.IsNone || !World.Has(entity, "DynamicActiveNpcState")) return null;
        var info = Spawn.Inspect(entity);
        return info?.Template is { } g ? new Villager(entity, NpcIdOf(g)) : null;
    }

    /// <summary>The world's villager of a template (the game keeps one per NpcId); null when it has none. Tick thread.</summary>
    public static Villager? Find(Guid template)
    {
        foreach (var e in Spawn.Find(template)) return new Villager(e, NpcIdOf(template));
        return null;
    }

    /// <summary>Every villager in the world. One pass over the entity map. Tick thread.</summary>
    public static IReadOnlyList<Villager> List()
    {
        var r = new List<Villager>();
        foreach (var e in World.Find("DynamicActiveNpcState"))
            if (Of(e) is { } v) r.Add(v);
        return r;
    }

    /// <summary>Posts <see cref="VillagerStateEvent"/> about four times a second for this villager. Tick thread.</summary>
    public static void Watch(EntityId entity)
    {
        if (Of(entity) is { } v) Watched[entity] = (v.NpcId, v.State);
    }

    internal static void Send(byte type, uint npcId, WorldPosition at, float yawDegrees)
    {
        var t = CreaturesNative.Require("Villagers");
        if (t->Header.Size < (uint)sizeof(CreaturesNative.Table)) throw new NotSupportedException("kiln.creatures has no NPC commands");
        var half = yawDegrees * MathF.PI / 360f;
        var c = new CreaturesNative.NpcCommand
        {
            Size = (uint)sizeof(CreaturesNative.NpcCommand), Type = type, NpcId = npcId, X = at.X, Y = at.Y, Z = at.Z,
            Ry = MathF.Sin(half), Rw = MathF.Cos(half),
        };
        Creatures.Check(t->SendNpcCommand(&c), $"npc 0x{npcId:x8}: command {type}");
    }

    internal static bool IsFollowing(EntityId e) => Follows.ContainsKey(e);

    internal static void StartFollow(Villager v, EntityId target, double distance, double catchUp)
    {
        var mod = ModContext.CurrentMod;
        Follows[v.Entity] = new Follower { V = v, Target = target, Distance = Math.Max(1, distance), CatchUp = catchUp, Mod = mod };
        if (mod != null) mod.Retiring += () => { foreach (var f in Follows.Values.Where(f => f.Mod == mod).ToList()) Follows.Remove(f.V.Entity); };
        Watched[v.Entity] = (v.NpcId, v.State);
    }

    internal static void StopFollow(EntityId e) => Follows.Remove(e);

    private static void Check(EntityId e, (uint Npc, VillagerState State) was, Dictionary<EntityId, (uint Npc, VillagerState State)> into)
    {
        var state = World.TryRead<byte>(e, "DynamicActiveNpcState", out var s) ? (VillagerState)s : VillagerState.Invalid;
        if (state == VillagerState.Invalid)
        {
            into.Remove(e);
            return;
        }
        if (state == was.State) return;
        into[e] = (was.Npc, state);
        if (Events.HasSubscribers<VillagerStateEvent>()) Events.Post(new VillagerStateEvent(e, was.Npc, was.State, state));
    }

    // Called from the creature pump (tick thread, Pre).
    internal static void Pump()
    {
        var now = Clock.ElapsedMilliseconds;
        if (Follows.Count > 0) PumpFollows(now);
        if (now >= _nextPoll && Watched.Count > 0)
        {
            _nextPoll = now + PollMs;
            foreach (var (e, w) in Watched.ToList()) Check(e, w, Watched);
        }
        if (now >= _nextAll && Events.HasSubscribers<VillagerStateEvent>())
        {
            _nextAll = now + AllPollMs;
            foreach (var e in World.Find("DynamicActiveNpcState"))
            {
                if (Watched.ContainsKey(e)) continue;
                if (All.TryGetValue(e, out var w)) Check(e, w, All);
                else if (Of(e) is { } v) All[e] = (v.NpcId, v.State);
            }
        }
    }

    private static void PumpFollows(long now)
    {
        foreach (var f in Follows.Values.ToList())
        {
            if (now < f.NextMs) continue;
            f.NextMs = now + FollowMs;
            if (!World.TryGetPosition(f.V.Entity, out var me) || !World.TryGetPosition(f.Target, out var to))
            {
                Follows.Remove(f.V.Entity);
                continue;
            }
            double dx = to.X - me.X, dy = to.Y - me.Y, dz = to.Z - me.Z;
            var d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (f.CatchUp > 0 && d > f.CatchUp) Send(0, f.V.NpcId, new WorldPosition(to.X, to.Y, to.Z), 0);
            else if (d > f.Distance + 2) Send(1, f.V.NpcId, to, 0);
        }
    }
}
