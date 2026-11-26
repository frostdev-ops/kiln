// SPDX-License-Identifier: MIT
// IPetMover in a game: the broker's authority from the pet's leases, input through the body's ActorInput (offsets from the
// game's reflection, as Kiln.Blocks' brains write it), routes from kiln.queries (walk-only accepted), catch-up through
// Kiln.World's teleport to a validated spot, the game's guard and threat for EngineGuard and attack orders.
using System.Numerics;

namespace Kiln;

internal sealed class GamePetMover : IPetMover
{
    private static int _move = -1, _look = -1, _speed = -1;
    private static bool _tried;

    private static PetControl? Control(PetBody b) => b.Tag is GamePetWorld.IHasControl h ? h.Control : null;

    public bool CanDrive(PetBody body) => Control(body)?.CanDrive ?? false;
    public bool Fighting(PetBody body) => Control(body)?.Fighting ?? false;

    public WorldPosition? Position(PetBody body) => World.TryGetPosition(body.Entity.Id, out var p) ? p : null;

    private static bool Resolve()
    {
        if (_tried) return _move >= 0;
        _tried = true;
        try
        {
            var t = Content.Templates.Get("Enemy_Vukah_Melee");
            if (t == null) return false;
            long Address(string path) => t.GetJson(path)?["address"]?.GetValue<long>() ?? -1;
            var at = Address("ActorInput");
            if (at <= 0) return false;
            int Off(string f) => Address($"ActorInput.{f}") is var a and > 0 ? (int)(a - at) : -1;
            _move = Off("desiredWorldMoveInput");
            _look = Off("desiredWorldLookDirection");
            _speed = Off("locomotionSpeed");
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            PetsRuntime.Log(LogLevel.Warn, $"Kiln.Pets: creature input is not known on this build, pets cannot walk: {e.Message}");
        }
        return _move >= 0;
    }

    public void Write(PetBody body, Vector3 move, Vector3 look, uint speed)
    {
        // the authority is checked right before the write, never cached
        if (!CanDrive(body) || !Resolve()) return;
        World.TryWrite(body.Entity.Id, "ActorInput", _move, move);
        if (_look >= 0 && look != Vector3.Zero) World.TryWrite(body.Entity.Id, "ActorInput", _look, look);
        // locomotionSpeed is one byte (the broker's layout guard checks it): a wider store would overwrite its neighbour
        if (_speed >= 0) World.TryWrite(body.Entity.Id, "ActorInput", _speed, (byte)speed);
    }

    public object? RequestPath(PetBody body, WorldPosition from, WorldPosition to, PetClearanceProfile clearance)
    {
        if (!Capabilities.Is("queries.path")) return null;
        try
        {
            var t = Queries.RequestPath(new QueryPathRequest(body.Entity.Id, from, to)
            {
                HorizontalSpace = clearance.HorizontalSpace, VerticalSpace = clearance.VerticalSpace, MaxIterations = 2000, ClosestFallback = false,
            });
            return t.Ok ? t : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    public (PetRoute? Route, string? Refusal)? PollPath(object request)
    {
        var t = (QueryPathTicket)request;
        if (!t.Completion.IsCompleted) return null;
        if (t.Completion.IsFaulted || t.Completion.IsCanceled) return (null, "NoRoute: the path request failed");
        return Accept(t.Completion.Result);
    }

    /// <summary>19d §4.3a: only Ok + Found + ReachesTarget + points, every one a walk; never code 0 with no points, never a
    /// partial route, never a fly, jump, swim or door segment.</summary>
    internal static (PetRoute? Route, string? Refusal) Accept(QueryPathResult r)
    {
        if (!r.Ok || r.Status != QueryPathStatus.Found || !r.ReachesTarget || r.Points.Count == 0) return (null, $"NoRoute: {r.Reason ?? r.Status.ToString()}");
        if (r.Points.Any(p => p.Move != QueryPathMove.Walk)) return (null, "NoRoute: the route has a segment that is not walking");
        return (new PetRoute(r.Points.Select(p => p.Position).ToList()), null);
    }

    public void CancelPath(object request)
    {
        var t = (QueryPathTicket)request;
        if (t.Ok && !t.Completion.IsCompleted)
        {
            try { Queries.CancelPath(t.Request); }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { }
        }
    }

    public bool Teleport(PetBody body, WorldPosition to)
    {
        if (!Kiln.Teleport.Available) return false;
        try
        {
            var t = Kiln.Teleport.To(body.Entity.Id, to, new TeleportOptions { FindGround = false });
            return !t.IsFaulted;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    public WorldPosition? SafeGround(WorldPosition near, double min, double max, PetDefinition def) => PetPlacement.Find(near, min, max, def, null);

    public void Guard(PetBody body, EntityId owner)
    {
        // the game's own guard event only: Kiln.Creatures' keeper (its own catch-up, defend and assist) is not run beside
        // the pet's follower, whose catch-up and target rule are the pet's
        try { Creatures.Of(body.Entity.Id).Command.Guard(owner, GuardOptions.EventOnly); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
    }

    public void Attack(PetBody body, EntityId target)
    {
        try
        {
            var c = Creatures.Of(body.Entity.Id);
            c.PreferTarget(target, 30);
            c.Command.Attack(target);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException) { }
    }

    public bool TargetAlive(EntityRef target)
    {
        try
        {
            if (target.IsNone || !target.IsAlive) return false;
            return Combat.GetHealth(target.Id) is not { } h || h.Current > 0;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { return false; }
    }
}
