// SPDX-License-Identifier: MIT
// Package 4 (19d §4.3 modes, §4.3a): a live pet's movement. Follow walks the body along walk-only routes kiln.queries
// finds (QueryInput) or lets the game's own guard keep it near (EngineGuard); Stay holds it with zero input; Attack hands
// the body to the game's AI under the PetCombat lease with the target preferred. Every write is checked against the
// broker's authority right before it (Pending, Preempted: nothing is written). A far or stuck pet is put next to its owner
// on validated ground when its definition allows catch-up, else it stops. Never a fly, jump, swim or door segment.
using System.Numerics;

namespace Kiln;

/// <summary>A route the follower may walk: walk-only points from the body to the owner.</summary>
internal sealed record PetRoute(IReadOnlyList<WorldPosition> Points);

/// <summary>What the follower asks of the engine (GamePetMover in a game; a fake in tests).</summary>
internal interface IPetMover
{
    /// <summary>The broker's authority for the body's input now (checked right before each write).</summary>
    bool CanDrive(PetBody body);
    /// <summary>The PetCombat lease is the body's Active winner.</summary>
    bool Fighting(PetBody body);
    WorldPosition? Position(PetBody body);
    /// <summary>Writes the body's input (move direction, look direction, speed 0 stop / 2 walk / 3 run).</summary>
    void Write(PetBody body, Vector3 move, Vector3 look, uint speed);
    /// <summary>Asks for a walk-only route; null when no request could be made now (the budget, the service).</summary>
    object? RequestPath(PetBody body, WorldPosition from, WorldPosition to, PetClearanceProfile clearance);
    /// <summary>A request's state: null while pending; a route or a refusal once it ended.</summary>
    (PetRoute? Route, string? Refusal)? PollPath(object request);
    void CancelPath(object request);
    /// <summary>Puts the body at <paramref name="to"/> (exactly, no ground search: the spot was validated).</summary>
    bool Teleport(PetBody body, WorldPosition to);
    /// <summary>A validated spot near <paramref name="near"/>; the owner and the pet itself never count as in the way.</summary>
    WorldPosition? SafeGround(WorldPosition near, double min, double max, PetDefinition def, EntityId owner, EntityId pet);
    /// <summary>The game's guard keeps the body near the entity (EngineGuard).</summary>
    void Guard(PetBody body, EntityId owner);
    /// <summary>The game's AI attacks the target (PetCombat: the target preferred, threat added).</summary>
    void Attack(PetBody body, EntityId target);
    /// <summary>The target is still there and alive.</summary>
    bool TargetAlive(EntityRef target);
}

/// <summary>A measured path clearance for a creature at a scale (19d §4.3a, gate C6): the HorizontalSpace and VerticalSpace
/// a route request asks for. Kiln never derives it from the body's radius; without one, QueryInput follow is catch-up only.</summary>
internal sealed record PetClearanceProfile(double HorizontalSpace, double VerticalSpace);

internal static class PetClearance
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string Creature, double Scale), PetClearanceProfile> Profiles = [];
    private static bool _loaded;

    /// <summary>The accepted profile of a creature at a scale; null when none was measured (ClearanceMappingUnverified).</summary>
    public static PetClearanceProfile? Of(string creature, double scale)
    {
        lock (Gate)
        {
            if (!_loaded) Load();
            return Profiles.GetValueOrDefault((creature, Math.Round(scale, 3)));
        }
    }

    internal static void SetForTests(string creature, double scale, PetClearanceProfile? p)
    {
        lock (Gate)
        {
            _loaded = true;
            if (p is null) Profiles.Remove((creature, Math.Round(scale, 3)));
            else Profiles[(creature, Math.Round(scale, 3))] = p;
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Profiles.Clear();
            _loaded = true;
        }
    }

    // kiln/data/kiln.pets/clearance.json: [{"creature": "game:...", "scale": 1, "horizontalSpace": 1, "verticalSpace": 2}],
    // the profiles a C6 calibration accepted (re-measured after a game update)
    private static void Load()
    {
        _loaded = true;
        try
        {
            var dir = Path.Combine(KilnPaths.Root, "data", "kiln.pets");
            var file = Path.Combine(dir, "clearance.json");
            if (!File.Exists(file)) return;
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) as System.Text.Json.Nodes.JsonArray;
            foreach (var n in root ?? [])
            {
                var c = n?["creature"]?.GetValue<string>();
                var s = n?["scale"]?.GetValue<double>() ?? 1;
                var h = n?["horizontalSpace"]?.GetValue<double>();
                var v = n?["verticalSpace"]?.GetValue<double>();
                if (c is null || h is not (>= 0 and <= 16) || v is not (>= 0 and <= 16)) continue;
                Profiles[(c, Math.Round(s, 3))] = new PetClearanceProfile(h.Value, v.Value);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or NotSupportedException)
        {
            PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: clearance profiles not read: {e.Message}");
        }
    }
}

internal sealed class PetFollower
{
    internal const uint Stop = 0, Walk = 2, Run = 3;
    internal const int MaxInFlight = 4;
    internal const double ReplanMeters = 2, MaxSeparation = 160, SettleSeconds = 0.5, SettleMeters = 1, CatchUpGiveUpSeconds = 5;
    private static int _inFlight;

    private object? _ticket;
    private PetRoute? _route;
    private int _wp;
    private WorldPosition _routeTarget;
    private double _routeAt = double.NegativeInfinity;
    private double _progressAt;
    private WorldPosition? _progressFrom;
    private double _noRouteSince = double.NaN;
    private bool _guardSent;
    private EntityRef _attacking;
    private (WorldPosition To, double At)? _catchUp;
    private double _catchUpBlockedUntil = double.NegativeInfinity;
    internal const double CatchUpBackoffSeconds = 10;
    private bool _stopped;

    /// <summary>Why following is limited now (ClearanceMappingUnverified, no route, ...); null when it walks.</summary>
    public string? Limited { get; private set; }
    /// <summary>Catch-ups done (tests and logs).</summary>
    public int CatchUps { get; private set; }
    /// <summary>The attack target ended (gone or dead): the roster puts the pet back on Follow.</summary>
    public bool TargetEnded { get; private set; }

    internal static void ResetForTests() => _inFlight = 0;

    public void Forget(IPetMover m)
    {
        if (_ticket is { } t)
        {
            m.CancelPath(t);
            _ticket = null;
            _inFlight = Math.Max(0, _inFlight - 1);
        }
    }

    public void Step(PetState p, PetDefinition def, PetOwner owner, IPetMover m, double now)
    {
        if (p.Body is not { } body) return;
        TargetEnded = false;
        if (p.Order == PetOrder.Attack)
        {
            Forget(m);
            if (!m.Fighting(body)) return;   // admitted, not yet the body's winner: the AI takes it once Active
            if (!m.TargetAlive(p.Target))
            {
                TargetEnded = true;
                _attacking = EntityRef.None;
                return;
            }
            if (_attacking != p.Target)
            {
                m.Attack(body, p.Target.Id);
                _attacking = p.Target;
            }
            return;
        }
        _attacking = EntityRef.None;
        var at = m.Position(body);
        if (at is null) return;
        if (_catchUp is { } c)
        {
            Settle(body, at.Value, c, m, now);
            return;
        }
        var distance = at.Value.DistanceTo(owner.Position);
        if (p.Order == PetOrder.Stay)
        {
            Forget(m);
            Halt(body, m);
            return;
        }
        if (def.Movement.Mode == PetMovementMode.EngineGuard)
        {
            // the game's guard walks it (AI on, SteeringAgent back): Kiln writes no input, only catches up when it is far
            if (!_guardSent && m.CanDrive(body))
            {
                m.Guard(body, owner.Entity.Id);
                _guardSent = true;
            }
            if (def.Movement.AllowCatchUp && distance > def.Movement.CatchUpDistance) CatchUp(body, owner, def, m, now);
            return;
        }
        if (!m.CanDrive(body)) return;   // Pending, or Preempted by a rider: never a write without authority
        if (def.Movement.AllowCatchUp && distance > def.Movement.CatchUpDistance)
        {
            CatchUp(body, owner, def, m, now);
            return;
        }
        if (distance <= def.Movement.FollowDistance)
        {
            Arrived(body, m, now, at.Value);
            return;
        }
        var clearance = PetClearance.Of(def.Creature, def.Level.Scale);
        if (clearance is null)
        {
            // 19d §4.3a: no measured clearance for this creature at this scale: no route is walked (catch-up only)
            Limited = "ClearanceMappingUnverified: no measured path clearance for this creature at this scale; the pet catches up instead of walking";
            Halt(body, m);
            return;
        }
        Limited = null;
        Plan(body, at.Value, owner.Position, clearance, def, m, now);
        if (_route is null)
        {
            Halt(body, m);
            if (double.IsNaN(_noRouteSince)) _noRouteSince = now;
            if (def.Movement.AllowCatchUp && now - _noRouteSince >= def.Movement.StuckSeconds) CatchUp(body, owner, def, m, now);
            return;
        }
        _noRouteSince = double.NaN;
        Advance(body, at.Value, distance, def, m, now, owner);
    }

    private void Arrived(PetBody body, IPetMover m, double now, WorldPosition at)
    {
        Forget(m);
        _route = null;
        _noRouteSince = double.NaN;
        _progressFrom = at;
        _progressAt = now;
        Halt(body, m);
    }

    private void Halt(PetBody body, IPetMover m)
    {
        if (!m.CanDrive(body)) return;
        m.Write(body, Vector3.Zero, Vector3.Zero, Stop);
        _stopped = true;
    }

    private void Plan(PetBody body, WorldPosition from, WorldPosition owner, PetClearanceProfile clearance, PetDefinition def, IPetMover m, double now)
    {
        if (_ticket is { } t)
        {
            if (m.PollPath(t) is not { } done) return;
            _ticket = null;
            _inFlight = Math.Max(0, _inFlight - 1);
            _route = done.Route is { Points.Count: > 0 } r ? r : null;
            Limited = done.Refusal;
            _wp = 0;
            return;
        }
        var stale = _route is null || owner.DistanceTo(_routeTarget) > ReplanMeters || now - _routeAt >= def.Movement.RepathSeconds;
        if (!stale || _inFlight >= MaxInFlight) return;
        if (from.DistanceTo(owner) > MaxSeparation) return;   // farther than a request's cap: catch-up decides
        var ticket = m.RequestPath(body, from, owner, clearance);
        if (ticket is null) return;
        _ticket = ticket;
        _inFlight++;
        _routeTarget = owner;
        _routeAt = now;
    }

    private void Advance(PetBody body, WorldPosition at, double distance, PetDefinition def, IPetMover m, double now, PetOwner owner)
    {
        var points = _route!.Points;
        while (_wp < points.Count && Flat(points[_wp], at) < 0.5) _wp++;
        if (_wp >= points.Count)
        {
            Halt(body, m);
            _route = null;
            return;
        }
        var target = points[_wp];
        var dir = new Vector3((float)(target.X - at.X), 0, (float)(target.Z - at.Z));
        if (dir.LengthSquared() < 1e-6f)
        {
            Halt(body, m);
            return;
        }
        dir = Vector3.Normalize(dir);
        if (!m.CanDrive(body)) return;
        m.Write(body, dir, dir, distance > def.Movement.RunDistance ? Run : Walk);
        if (_stopped || _progressFrom is null)
        {
            _progressFrom = at;
            _progressAt = now;
            _stopped = false;
        }
        else if (at.DistanceTo(_progressFrom.Value) >= 0.25)
        {
            _progressFrom = at;
            _progressAt = now;
        }
        else if (now - _progressAt >= def.Movement.StuckSeconds)
        {
            // no progress for StuckSeconds while walking: catch up when allowed, else stop and wait
            _progressFrom = null;
            if (def.Movement.AllowCatchUp) CatchUp(body, owner, def, m, now);
            else
            {
                Halt(body, m);
                _route = null;
                Limited = "Stuck: no progress; catch-up is off for this pet";
            }
        }
    }

    private void CatchUp(PetBody body, PetOwner owner, PetDefinition def, IPetMover m, double now)
    {
        // a teleport moves the body: only with the broker's authority (never while a rider's lease preempts the pet's, nor
        // while its base lease is Pending), checked right before it like every input write
        if (!m.CanDrive(body)) return;
        Forget(m);
        _route = null;
        Halt(body, m);
        if (now < _catchUpBlockedUntil) return;   // the last one did not settle: wait before another checked try
        var spot = m.SafeGround(owner.Position, 2, 4, def, owner.Entity.Id, body.Entity.Id);
        if (spot is null)
        {
            Limited = "NoSafeGround: no validated spot near the owner to catch up to";
            return;
        }
        if (!m.Teleport(body, spot.Value))
        {
            Limited = "CatchUpRefused: the teleport was refused";
            return;
        }
        _catchUp = (spot.Value, now);
        CatchUps++;
    }

    private void Settle(PetBody body, WorldPosition at, (WorldPosition To, double At) c, IPetMover m, double now)
    {
        Halt(body, m);
        if (now - c.At < SettleSeconds) return;
        if (at.DistanceTo(c.To) <= SettleMeters)
        {
            _catchUp = null;
            Limited = null;
            _progressFrom = at;
            _progressAt = now;
            return;
        }
        if (now - c.At >= CatchUpGiveUpSeconds)
        {
            // it did not settle where it was put: stop and wait (never an unchecked second jump)
            _catchUp = null;
            _catchUpBlockedUntil = now + CatchUpBackoffSeconds;
            Limited = "CatchUpUnsettled: the body did not settle at the catch-up spot";
        }
    }

    private static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
