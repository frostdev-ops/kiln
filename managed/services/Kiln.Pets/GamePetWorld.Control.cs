// SPDX-License-Identifier: MIT
// Package 3: GamePetWorld's bodies under kiln.creature_control (PetControl). A new body is bound and prepared under a
// PetStay lease at its spawn; it is ready only once its final base lease is Active. Retiring a body quarantines it first
// (every lease, a rider's too, loses authority at once and its root is denied), lets a rider's emergency detach take two
// steps, then destroys it; the broker retires its rows when it sees the body gone.
namespace Kiln;

internal sealed partial class GamePetWorld
{
    internal const ulong RiderSteps = 2;

    private static BodyTag Tag(PetBody b) => (BodyTag)b.Tag!;

    private partial bool Attach(PetBody body, PetBodyRequest request, out string? reason)
    {
        var control = PetControl.Prepare(body.Entity, out reason);
        if (control is null) return false;
        Tag(body).Control = control;
        return true;
    }

    private partial PetBodyPoll ControlPoll(PetBody body, out string? reason)
    {
        reason = null;
        var tag = Tag(body);
        if (tag.Control is not { } c) return PetBodyPoll.Ready;
        try { return c.PollPreparation(PetControl.FollowRole(tag.Request.Definition), out reason); }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            reason = $"ControlUnavailable: {e.Message}";
            return PetBodyPoll.Failed;
        }
    }

    private partial string? ControlLost(PetBody body)
    {
        var tag = Tag(body);
        if (tag.Control is not { } c || tag.Quarantined) return null;
        try
        {
            foreach (var (before, after) in c.Changes())
                PetsRuntime.Enqueue(new PetControlChangedEvent(tag.Request.Pet, tag.Request.Owner, before, after));
            return c.Lost();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return $"ControlUnavailable: {e.Message}";
        }
    }

    private partial bool ActivateBody(PetBody body, out string? reason)
    {
        reason = null;
        return Tag(body).Control is not { } c || c.Prepared;
    }

    private partial bool SetOrder(PetBody body, PetOrder order, EntityRef target, out string? reason)
    {
        var tag = Tag(body);
        if (tag.Control is not { } c)
        {
            reason = null;
            return true;
        }
        return c.SetOrder(order, PetControl.FollowRole(tag.Request.Definition), out reason);
    }

    private partial bool Detach(PetBody body, PetChangeCause cause)
    {
        var tag = Tag(body);
        if (tag.Control is not { } c) return true;
        if (!tag.Quarantined)
        {
            ControlRole effective;
            try { effective = c.Effective; }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException) { effective = ControlRole.None; }
            tag.Quarantined = true;
            tag.QuarantinedAt = Step;
            if (!c.Quarantine(PetControl.CauseOf(cause)))
            {
                // the broker no longer has the body (its world ended, or it was never bound): nothing to hand over
                return true;
            }
            // a rider gets two steps to get off (19d §4.3 handshake: emergency detach), then the body goes regardless
            if (effective == ControlRole.Ridden) return false;
            return true;
        }
        return Step >= tag.QuarantinedAt + RiderSteps;
    }

    private partial void Retired(PetBody body) { }
}
