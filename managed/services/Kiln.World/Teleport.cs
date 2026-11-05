// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Options for <see cref="Teleport"/>.</summary>
public sealed record TeleportOptions
{
    public static readonly TeleportOptions Default = new();

    /// <summary>The game's ground search: the nearest walkable spot within about 10 m (up or down) of the target.
    /// When none is found the entity lands exactly on the target (<see cref="TeleportFlags.RawTarget"/>).</summary>
    public bool FindGround { get; init; }

    /// <summary>Face this heading (degrees about +Y) on arrival; null keeps the current orientation.</summary>
    public double? YawDegrees { get; init; }

    /// <summary>Players: also set a replication checkpoint, as the game's fast travel does (for long moves).</summary>
    public bool Checkpoint { get; init; }
}

public enum TeleportStatus
{
    /// <summary>Not moved: refused when checked, or given up (see <see cref="TeleportResult.Reason"/>).</summary>
    Failed = 0,
    /// <summary>Moved: <see cref="TeleportResult.Position"/> is where the entity is now.</summary>
    Moved = 1,
}

[Flags]
public enum TeleportFlags
{
    None = 0,
    /// <summary>Ended more than 1 m from the target (25 m when aiming at a spawn point).</summary>
    OffTarget = 0x01,
    /// <summary>FindGround found no walkable spot: the entity stands exactly at the target.</summary>
    RawTarget = 0x02,
    /// <summary>Failed after 10 s without a move; the stuck Teleport component was removed.</summary>
    TimedOut = 0x04,
}

/// <summary>Outcome of one teleport.</summary>
public sealed record TeleportResult(TeleportStatus Status, EntityId Entity, WorldPosition Position, WorldPosition Target,
    double Distance, TeleportFlags Flags, int Steps, string Reason)
{
    public bool Moved => Status == TeleportStatus.Moved;
    public override string ToString() => Moved
        ? $"moved {Entity} to {Position} ({Distance:F2} m from {Target}, {Steps} steps{(Flags != 0 ? ", " + Flags : "")})"
        : $"{Entity} not moved: {Reason}";
}

/// <summary>
/// Moves entities the way the game's own fast travel does (the engine's Teleport component): players, villagers,
/// animals and creatures. The move happens 2-3 server steps after the call (longer while FindGround waits for the
/// destination to load); the task completes on the tick thread.
/// </summary>
/// <remarks>Refused (a Failed result, no exception): props/chests/altars (no TeleportCount), dead entities, an entity
/// with a teleport already in flight. Enemies moved far from every player are removed by the game shortly after.</remarks>
public static unsafe class Teleport
{
    private sealed class Pending
    {
        public required TaskCompletionSource<TeleportResult> Source;
        public required ModContext? Mod;
        public ulong NativeId;
        public EntityId Entity;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Pending> PendingById = new();
    private static readonly Dictionary<ModContext, HashSet<long>> ByMod = new();
    private static long _nextCookie;

    /// <summary>True when teleport works on this build and the server world is running.</summary>
    public static bool Available => WorldNative.T != null && World.Status.Teleport;

    /// <summary>Teleports <paramref name="entity"/> to <paramref name="target"/> (metres).</summary>
    public static Task<TeleportResult> To(EntityId entity, WorldPosition target, TeleportOptions? options = null) =>
        Start(entity, target, EntityId.None, options ?? TeleportOptions.Default);

    /// <summary>Teleports <paramref name="entity"/> onto a landing pad of a spawn point (an entity with
    /// PlayerSpawnPoint: flame altar, spire), as when respawning there.</summary>
    public static Task<TeleportResult> ToSpawnPoint(EntityId entity, EntityId spawnPoint, TeleportOptions? options = null) =>
        Start(entity, default, spawnPoint, options ?? TeleportOptions.Default);

    /// <summary>Teleports <paramref name="entity"/> next to <paramref name="target"/> (e.g. a player to another player):
    /// <paramref name="offset"/> metres beside it along +X, with the ground search on unless options say otherwise.</summary>
    public static Task<TeleportResult> ToEntity(EntityId entity, EntityId target, double offset = 1.5,
        TeleportOptions? options = null)
    {
        Services.RequireTickThread("Teleport.ToEntity");
        if (!World.TryGetPosition(target, out var p))
            return Task.FromResult(Fail(entity, default, $"target entity {target} has no position"));
        return Start(entity, p with { X = p.X + offset }, EntityId.None, options ?? new TeleportOptions { FindGround = true });
    }

    private static Task<TeleportResult> Start(EntityId entity, WorldPosition target, EntityId spawnPoint, TeleportOptions o)
    {
        var t = WorldNative.Require("Teleport");
        Services.RequireTickThread("Teleport");
        if (entity.IsNone) return Task.FromResult(Fail(entity, target, "no entity"));
        var mod = ModContext.CurrentMod;
        var cookie = Interlocked.Increment(ref _nextCookie);
        var pending = new Pending
        {
            Source = new TaskCompletionSource<TeleportResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            Mod = mod,
            Entity = entity,
        };
        WorldNative.TeleportRequest req;
        req.Size = (uint)sizeof(WorldNative.TeleportRequest);
        req.Entity = entity.Value;
        req.Target = new WorldNative.Vec3 { X = target.X, Y = target.Y, Z = target.Z };
        req.YawDeg = (float)(o.YawDegrees ?? 0);
        req.SpawnPoint = spawnPoint.Value;
        req.Flags = (o.FindGround ? 1u : 0) | (o.YawDegrees.HasValue ? 2u : 0) | (o.Checkpoint ? 4u : 0);
        req.Reserved = 0;
        req.Callback = &OnResult;
        req.User = (void*)cookie;
        lock (Gate)
        {
            PendingById[cookie] = pending;  // before the call: the callback never runs synchronously, but be safe
            if (mod is not null)
            {
                if (!ByMod.TryGetValue(mod, out var set))
                {
                    ByMod[mod] = set = new HashSet<long>();
                    mod.Retiring += () => Retire(mod);
                }
                set.Add(cookie);
            }
        }
        ulong id = 0;
        var rc = t->Teleport(Services.CallerOwner, &req, &id);
        if (rc != WorldNative.Ok)
        {
            Forget(cookie);
            return Task.FromResult(Fail(entity, target, WorldNative.Describe(rc)));
        }
        pending.NativeId = id;
        return pending.Source.Task;
    }

    private static TeleportResult Fail(EntityId e, WorldPosition target, string reason) =>
        new(TeleportStatus.Failed, e, default, target, 0, TeleportFlags.None, 0, reason);

    private static Pending? Forget(long cookie)
    {
        lock (Gate)
        {
            if (!PendingById.Remove(cookie, out var p)) return null;
            if (p.Mod is not null && ByMod.TryGetValue(p.Mod, out var set)) set.Remove(cookie);
            return p;
        }
    }

    // The mod unloads: drop its pending requests (and the continuations that point into its load context).
    private static void Retire(ModContext mod)
    {
        List<Pending> drop = [];
        lock (Gate)
        {
            if (!ByMod.Remove(mod, out var set)) return;
            foreach (var c in set)
                if (PendingById.Remove(c, out var p)) drop.Add(p);
        }
        var t = WorldNative.T;
        foreach (var p in drop)
        {
            if (t != null && p.NativeId != 0) t->TeleportCancel(p.NativeId);
            p.Source.TrySetCanceled();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(WorldNative.TeleportResult* r, void* user)
    {
        try
        {
            var p = Forget((long)user);
            if (p is null) return;
            var result = new TeleportResult((TeleportStatus)r->Status, new EntityId(r->Entity),
                new WorldPosition(r->Position.X, r->Position.Y, r->Position.Z),
                new WorldPosition(r->Target.X, r->Target.Y, r->Target.Z), r->Distance, (TeleportFlags)r->Flags,
                (int)r->Steps, r->Reason.ToString());
            p.Source.TrySetResult(result);
        }
        catch
        {
            // never let an exception cross back into native code
        }
    }
}
