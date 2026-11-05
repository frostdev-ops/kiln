// SPDX-License-Identifier: MIT
// Guard orders: the game's own guard (StartGuardingEvent -> enemy01's Guard state, which walks to the guarded entity
// with the path walker) plus what makes a companion out of it on a dedicated server:
//  - the order is kept: if the game drops guardEntityId (target teleported away, a behaviour action cleared it) it is
//    sent again;
//  - its home zone follows the target (roaming, leash and "return home" centre on whom it guards);
//  - catch-up: a guard that is far away (CatchUpDistance) or stands still while its target is out of guard range
//    (StuckSeconds; the path walker gave up: TargetNotReachable / Stuck) is teleported next to the target;
//  - defend / assist: who hurts the target, and what the target hits, get threat on the guard
//    (AddThreatTargetEvent, the game's own "attack that").
// Evidence: docs/re/creatures.md 12 (Guard state 0x1371e0 gives up after 0.5 s when the path walker reports
// TargetNotReachable or Stuck; NeedsToGuard re-checks every 0.5-2 s at guardSetup.startDistance).
namespace Kiln;

/// <summary>How a guard follows and defends (<see cref="CreatureCommands.Guard(EntityId, GuardOptions)"/>).</summary>
public sealed record GuardOptions
{
    /// <summary>Follow, catch up and defend (the defaults below).</summary>
    public static readonly GuardOptions Default = new();

    /// <summary>Only the game's StartGuardingEvent, nothing kept (the older behaviour).</summary>
    public static readonly GuardOptions EventOnly = new()
    {
        Keep = false, HomeFollows = false, CatchUpDistance = 0, StuckSeconds = 0, Defend = false, Assist = false,
        Focus = false,
    };

    /// <summary>Send the order again when the game dropped it (default true).</summary>
    public bool Keep { get; init; } = true;

    /// <summary>Its home zone follows the target (default true): idle roaming, the leash and "return home" centre on
    /// the target instead of where the creature spawned.</summary>
    public bool HomeFollows { get; init; } = true;

    /// <summary>Teleport the guard next to its target when it is farther away than this (metres; 0 = never; default
    /// 30). Skipped while it fights, unless it is twice as far.</summary>
    public double CatchUpDistance { get; init; } = 30;

    /// <summary>Teleport it when it stayed within 1.5 m of one spot this long (seconds) while the target is out of
    /// its guard range (the game's path walker found no way; 0 = never; default 4).</summary>
    public double StuckSeconds { get; init; } = 4;

    /// <summary>While it guards, the enemy AI features Awareness and Investigate are off for it (default true): the
    /// game's own player companions (necromancy skeletons) have no Awareness, and an ally Vukah with both kept walking
    /// off to investigate noises. Given back when the order ends.</summary>
    public bool Focus { get; init; } = true;

    /// <summary>Whoever hurts the target gets threat on the guard (default true).</summary>
    public bool Defend { get; init; } = true;

    /// <summary>What the target hits gets threat on the guard (default true).</summary>
    public bool Assist { get; init; } = true;

    /// <summary>Threat added per hit for <see cref="Defend"/> / <see cref="Assist"/> (default 2000; the Vukah's
    /// alarmed threshold is 500).</summary>
    public float Threat { get; init; } = 2000;
}

/// <summary>One guard order Kiln keeps (<see cref="Creatures.GuardOrders"/>).</summary>
public sealed record GuardOrder(EntityId Guard, EntityId Target, GuardOptions Options, string Owner)
{
    /// <summary>Times the order was sent again because the game had dropped it.</summary>
    public int Resent { get; internal set; }
    /// <summary>Catch-up teleports so far.</summary>
    public int CatchUps { get; internal set; }
    /// <summary>Threat events sent for Defend / Assist.</summary>
    public int ThreatsSent { get; internal set; }
}

public static partial class Creatures
{
    /// <summary>The guard orders Kiln keeps now (guards commanded with <see cref="CreatureCommands.Guard(EntityId,
    /// GuardOptions)"/>). Tick thread.</summary>
    public static IReadOnlyList<GuardOrder> GuardOrders => Guarding.Orders.Values.Select(o => o.Public).ToList();
}

internal static unsafe class Guarding
{
    internal sealed class Order
    {
        public required GuardOrder Public;
        public required Creatures.OwnerKey Owner;
        public double StartDistance = 10;
        public WorldPosition StillAnchor, LastHome;
        public bool HaveAnchor, HomeSet, FocusSet;
        public long StartMs, StillSinceMs = -1, LastResendMs = -1, LastCatchUpMs = -1;
        public bool TeleportInFlight;
        public EntityId Guard => Public.Guard;
        public EntityId Target => Public.Target;
        public GuardOptions Options => Public.Options;
    }

    internal static readonly Dictionary<EntityId, Order> Orders = [];
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static EventSubscription? _damaged;
    private static int _logs;

    private static void Log(string s)
    {
        if (_logs++ < 200) Backend.Current.Log(0, LogLevel.Info, "Kiln.Creatures: " + s);
    }

    /// <summary>A new order (replaces the guard's previous one). Tick thread.</summary>
    internal static void Start(EntityId guard, EntityId target, GuardOptions o, Creatures.OwnerKey owner)
    {
        if (Orders.Remove(guard, out var old)) Drop(old, sendStop: !old.Target.Equals(target));
        if (!o.Keep && !o.HomeFollows && o.CatchUpDistance <= 0 && o.StuckSeconds <= 0 && !o.Defend && !o.Assist && !o.Focus) return;
        var ord = new Order { Public = new GuardOrder(guard, target, o, owner.Name), Owner = owner, StartMs = Clock.ElapsedMilliseconds };
        try
        {
            if (double.TryParse(new CreatureRef(guard, owner).Ai.Get("guardSetup.startDistance"),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sd) && sd > 0)
                ord.StartDistance = sd;
        }
        catch (Exception) { /* no enemy AI copy readable: keep 10 m */ }
        Orders[guard] = ord;
        if (o.Focus)
            try
            {
                new CreatureRef(guard, FocusOwner).Features.Disable(FocusFeatures);
                ord.FocusSet = true;
            }
            catch (Exception ex) { Log($"guard {guard}: focus off ({ex.Message})"); }
        if ((o.Defend || o.Assist) && _damaged is null) HookDamage();
    }

    // StopGuardingEvent sent by Kiln: checked until the game cleared guardEntityId (the lab saw one in three runs
    // still set 25 steps later); sent again after 1 s, at most 3 times.
    private sealed record PendingStop(EntityId Guard, EntityId Target)
    {
        public long SentMs;
        public int Tries;
    }
    private static readonly Dictionary<EntityId, PendingStop> Stops = [];

    internal static void StopSent(EntityId guard, EntityId target)
    {
        if (!target.IsNone) Stops[guard] = new PendingStop(guard, target) { SentMs = Clock.ElapsedMilliseconds, Tries = 1 };
    }

    private static void PumpStops(long now)
    {
        var t = CreaturesNative.T;
        if (t == null) return;
        foreach (var p in Stops.Values.ToList())
        {
            if (Orders.TryGetValue(p.Guard, out var o) && o.Target.Equals(p.Target)) { Stops.Remove(p.Guard); continue; }  // guarding again
            var info = new CreaturesNative.Info { Size = (uint)sizeof(CreaturesNative.Info) };
            if (t->Inspect(p.Guard.Value, &info) != CreaturesNative.Ok || info.Guard != p.Target.Value) { Stops.Remove(p.Guard); continue; }
            if (now - p.SentMs < 1000) continue;
            if (p.Tries >= 4) { Stops.Remove(p.Guard); Log($"guard {p.Guard}: StopGuardingEvent sent {p.Tries}x, the game still guards {p.Target}"); continue; }
            Send(CreaturesNative.CmdStopGuard, p.Guard, p.Target);
            p.SentMs = now;
            p.Tries++;
        }
    }

    /// <summary>The order ends (StopGuarding, Stay, unload). sendStop: also tell the game (StopGuardingEvent).</summary>
    internal static void Stop(EntityId guard, EntityId target)
    {
        if (Orders.TryGetValue(guard, out var o) && (target.IsNone || o.Target.Equals(target)))
        {
            Orders.Remove(guard);
            Drop(o, sendStop: false);
        }
    }

    // The focus switches are a layer of their own owner (one per creature): the order's mod may have feature layers of
    // its own that a reset must not touch.
    private static readonly Creatures.OwnerKey FocusOwner = Creatures.OwnerKey.Named("kiln.creatures:guard-focus");
    private const EnemyFeatures FocusFeatures = EnemyFeatures.Awareness | EnemyFeatures.Investigate;

    private static void Unfocus(Order o)
    {
        if (!o.FocusSet) return;
        o.FocusSet = false;
        try { new CreatureRef(o.Guard, FocusOwner).Features.Reset(); } catch (Exception) { }
    }

    private static void Drop(Order o, bool sendStop)
    {
        Unfocus(o);
        if (o.HomeSet)
            try { new CreatureRef(o.Guard, o.Owner).ResetField(CreaturesNative.Home, 0); } catch (Exception) { }
        if (sendStop)
            try
            {
                Send(CreaturesNative.CmdStopGuard, o.Guard, o.Target);
                StopSent(o.Guard, o.Target);
            }
            catch (Exception) { }
    }

    /// <summary>The owner's mod unloads: its orders end and the game is told.</summary>
    internal static void Retire(ModContext mod)
    {
        foreach (var o in Orders.Values.Where(x => x.Owner.Mod == mod).ToList())
        {
            Orders.Remove(o.Guard);
            Unfocus(o);  // the mod's own layers (home) go with the mod's owner release
            try
            {
                Send(CreaturesNative.CmdStopGuard, o.Guard, o.Target);
                StopSent(o.Guard, o.Target);
            }
            catch (Exception) { }
        }
    }

    private static void Send(uint kind, EntityId e, EntityId target, float value = 0)
    {
        var t = CreaturesNative.T;
        if (t == null) return;
        var c = new CreaturesNative.Command
        {
            Size = (uint)sizeof(CreaturesNative.Command), Kind = kind, Entity = e.Value, Target = target.Value, Value = value,
        };
        t->SendCommand(&c);
    }

    // ---- the keeper (Pre, every ~10 steps, from the native listener poll) ----

    // /creature watch: a creature's AI, path walker and guard order in the log every poll (~0.4 s), for sessions.
    private static readonly Dictionary<EntityId, long> Watches = [];

    internal static void Watch(EntityId e, double seconds)
    {
        if (seconds <= 0) Watches.Remove(e);
        else Watches[e] = Clock.ElapsedMilliseconds + (long)(seconds * 1000);
    }

    private static void PumpWatches(long now)
    {
        foreach (var (e, until) in Watches.ToList())
        {
            if (now > until || !World.Exists(e)) { Watches.Remove(e); continue; }
            var t = CreaturesNative.T;
            if (t == null) return;
            var info = new CreaturesNative.Info { Size = (uint)sizeof(CreaturesNative.Info) };
            if (t->Inspect(e.Value, &info) != CreaturesNative.Ok) continue;
            var pos = World.Position(e);
            var guard = new EntityId(info.Guard);
            var gd = !guard.IsNone && pos is { } p0 && World.Position(guard) is { } q ? $" {p0.DistanceTo(q):0.0} m from it" : "";
            // DynamicPathWalker (reflection): state +0 (PathWalkerState), lastPathfindingResult +0x24 (diagnostic only)
            var pw = World.ReadBytes(e, "DynamicPathWalker");
            var walk = pw is { Length: > 0x28 } ? $", path walker {(PathWalkerState)pw[0]} ({(PathResult)pw[0x24]})" : "";
            var o = Orders.TryGetValue(e, out var ord) ? $", Kiln order -> {ord.Target} (catch-ups {ord.Public.CatchUps}, resent {ord.Public.Resent}, threats {ord.Public.ThreatsSent})" : "";
            Backend.Current.Log(0, LogLevel.Info,
                $"Kiln.Creatures: watch {e}: {(EnemyState)info.State} at {pos}, target {info.Target}, guard {guard}{gd}{walk}{o}");
        }
    }

    private enum PathWalkerState : byte
    {
        Idle, WaitingForQuery, Walking, TurnBeforeJump, WalkToJumpPoint, JumpUp, JumpDown, JumpHorizontal, TargetReached,
        TargetNotReachable, Stuck, Turn, Teleporting, WalkToDoor, OpenDoor, Unknown,
    }

    private enum PathResult : byte
    {
        None, PathFound, NoPathFound, NoFreePath, NoOccupiedPath, NoStartNodeFound, NoTargetNodeFound, MaxIterationsReached,
        OpenListOutOfMemory, NodesOutOfMemory,
    }

    internal static void Pump()
    {
        if (Watches.Count > 0) PumpWatches(Clock.ElapsedMilliseconds);
        if (Stops.Count > 0) PumpStops(Clock.ElapsedMilliseconds);
        if (Orders.Count == 0) return;
        var now = Clock.ElapsedMilliseconds;
        foreach (var o in Orders.Values.ToList())
        {
            try { Keep(o, now); }
            catch (Exception ex) { Log($"guard {o.Guard} -> {o.Target}: {ex.Message}"); }
        }
    }

    private static void Keep(Order o, long now)
    {
        var t = CreaturesNative.T;
        if (t == null) return;
        var info = new CreaturesNative.Info { Size = (uint)sizeof(CreaturesNative.Info) };
        if (t->Inspect(o.Guard.Value, &info) != CreaturesNative.Ok || (info.Flags & 2) != 0)  // gone or dead
        {
            Orders.Remove(o.Guard);
            Unfocus(o);
            Log($"guard order {o.Guard} -> {o.Target} ended: the guard is gone or dead");
            return;
        }
        if (!World.TryGetPosition(o.Target, out var tp) || !World.Exists(o.Target))
        {
            Orders.Remove(o.Guard);
            Drop(o, sendStop: true);
            Log($"guard order {o.Guard} -> {o.Target} ended: the target is gone");
            return;
        }
        if (!World.TryGetPosition(o.Guard, out var gp)) return;
        var state = (EnemyState)info.State;
        var op = o.Options;

        // 1. keep the order
        // (the game takes the event in the next sim step: give it a second before calling the order dropped)
        if (op.Keep && info.Guard != o.Target.Value && now - o.StartMs > 1000
            && state is not (EnemyState.Dead or EnemyState.Spawning or EnemyState.Start)
            && (o.LastResendMs < 0 || now - o.LastResendMs > 2000))
        {
            Send(CreaturesNative.CmdGuard, o.Guard, o.Target);
            o.LastResendMs = now;
            o.Public.Resent++;
            if (o.Public.Resent <= 3) Log($"guard {o.Guard}: the game dropped the order (guard {info.Guard}); sent again");
        }

        // 2. home follows
        if (op.HomeFollows && (!o.HomeSet || o.LastHome.DistanceTo(tp) > 3))
        {
            new CreatureRef(o.Guard, o.Owner).Home = tp;
            o.LastHome = tp;
            o.HomeSet = true;
        }

        // 3. catch up
        var dist = gp.DistanceTo(tp);
        var busy = state is EnemyState.Combat or EnemyState.Hit or EnemyState.ControlledByAttack or EnemyState.Stunned
            or EnemyState.Dead or EnemyState.Falling or EnemyState.Landing;
        // "stood still": within 1.5 m of one spot (the pump runs every 10 steps: 0.4 s without a player, 0.17 s at
        // 60 Hz with one, so a per-pump distance would call a walking guard still)
        if (busy || dist <= Math.Max(o.StartDistance, 4) + 1)
        {
            o.StillSinceMs = -1;
            o.HaveAnchor = false;
        }
        else if (!o.HaveAnchor || gp.DistanceTo(o.StillAnchor) >= 1.5)
        {
            o.StillAnchor = gp;
            o.HaveAnchor = true;
            o.StillSinceMs = now;
        }
        if (o.TeleportInFlight || (o.LastCatchUpMs >= 0 && now - o.LastCatchUpMs < 2000)) return;
        string? why = null;
        if (op.CatchUpDistance > 0 && dist > op.CatchUpDistance && (!busy || dist > 2 * op.CatchUpDistance))
            why = $"{dist:0.0} m away (catch-up distance {op.CatchUpDistance:0})";
        else if (op.StuckSeconds > 0 && o.StillSinceMs >= 0 && now - o.StillSinceMs >= op.StuckSeconds * 1000)
            why = $"stood still {(now - o.StillSinceMs) / 1000.0:0.0} s, {dist:0.0} m away (state {state})";
        if (why is null || !Teleport.Available) return;
        // 2.5 m from the target on the guard's side
        double dx = gp.X - tp.X, dz = gp.Z - tp.Z, len = Math.Sqrt(dx * dx + dz * dz);
        if (len < 0.1) (dx, dz, len) = (1, 0, 1);
        var dest = new WorldPosition(tp.X + dx / len * 2.5, tp.Y + 0.5, tp.Z + dz / len * 2.5);
        o.TeleportInFlight = true;
        o.LastCatchUpMs = now;
        o.StillSinceMs = -1;
        o.HaveAnchor = false;
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = o.Owner.Mod;
        Task<TeleportResult> task;
        try { task = Teleport.To(o.Guard, dest, new TeleportOptions { FindGround = true }); }
        finally { ModContext.CurrentMod = outer; }
        o.Public.CatchUps++;
        var n = o.Public.CatchUps;
        task.ContinueWith(r =>
        {
            o.TeleportInFlight = false;
            if (n <= 5 || n % 20 == 0)
                Log($"guard {o.Guard} -> {o.Target}: catch-up #{n} ({why}): {(r.IsCompletedSuccessfully ? r.Result.ToString() : r.Exception?.InnerException?.Message)}");
        }, TaskContinuationOptions.ExecuteSynchronously);
    }

    // ---- defend / assist ----

    private static void HookDamage()
    {
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // one global handler: it lives as long as Kiln.Creatures
        try { _damaged = Events.On<EntityDamagedEvent>(OnDamaged, EventPriority.Low); }
        catch (Exception ex) { Log("defend/assist off: " + ex.Message); }
        finally { ModContext.CurrentMod = outer; }
    }

    private static void OnDamaged(EntityDamagedEvent e)
    {
        if (Orders.Count == 0 || e.Amount <= 0 || e.Attacker.IsNone || e.Attacker.Equals(e.Target)) return;
        foreach (var o in Orders.Values)
        {
            EntityId foe;
            if (o.Options.Defend && e.Target.Equals(o.Target)) foe = e.Attacker;
            else if (o.Options.Assist && e.Attacker.Equals(o.Target)) foe = e.Target;
            else continue;
            if (foe.Equals(o.Guard) || foe.Equals(o.Target) || IsGuardOf(foe, o.Target)) continue;
            try
            {
                Send(CreaturesNative.CmdAttack, o.Guard, foe, o.Options.Threat);
                if (++o.Public.ThreatsSent <= 3) Log($"guard {o.Guard} ({(foe.Equals(e.Attacker) ? "defends" : "assists")} {o.Target}): threat on {foe}");
            }
            catch (Exception) { }
        }
    }

    private static bool IsGuardOf(EntityId e, EntityId target) => Orders.TryGetValue(e, out var o) && o.Target.Equals(target);
}
