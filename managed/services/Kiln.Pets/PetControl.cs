// SPDX-License-Identifier: MIT
// A pet's leases on its body from kiln.creature_control (19d §4.3, §5.2, §5.3), under Kiln.Pets' own service owner. Pets
// is the broker's client, never a second owner of the body's AI or SteeringAgent: a new body is prepared under a PetStay
// lease (its AI held off, its current action finished, the body ready) and is the pet's only once the same owner's
// atomic replacement to the final base role (PetFollow, or EngineGuard for that movement mode) is Active. Orders move the
// base lease (Follow <-> Stay) or add a PetCombat lease above it; a rider's Ridden lease (another owner) preempts them and
// the broker resumes the base lease after the ride; a lease of ours that ends without our asking is lost control.
namespace Kiln;

internal sealed class PetControl
{
    internal const string Service = "kiln.pets";

    private readonly HashSet<ulong> _ours = [];   // leases this pet ended itself (released or replaced): their end is no loss
    private ControlLeaseSnapshot? _lastBase, _lastCombat;

    private PetControl(EntityRef body, ControlRequestResult first)
    {
        Body = body;
        Base = first.Lease;
        BaseRole = ControlRole.PetStay;
        Key = first.Initial!.Body;
    }

    public EntityRef Body { get; }
    public ControlBodyKey Key { get; }
    /// <summary>The base lease: PetStay, PetFollow or EngineGuard.</summary>
    public ControlLeaseId Base { get; private set; }
    public ControlRole BaseRole { get; private set; }
    /// <summary>The PetCombat lease while the pet attacks; none otherwise.</summary>
    public ControlLeaseId Combat { get; private set; }
    /// <summary>The final base role was granted: the body is the pet's.</summary>
    public bool Prepared { get; private set; }

    /// <summary>Takes the preparation lease (PetStay) on a new body. Tick thread; the caller checked creature_control.</summary>
    public static PetControl? Prepare(EntityRef body, out string? reason)
    {
        ControlRequestResult r;
        try { r = CreatureControl.AcquireAs(Service, null, body, ControlRole.PetStay); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            reason = $"ControlRefused: {e.Message}";
            return null;
        }
        if (!r.Ok || r.Initial is null)
        {
            reason = $"ControlRefused: {r.Reason}";
            return null;
        }
        reason = null;
        return new PetControl(body, r);
    }

    /// <summary>The base role a Follow order takes for a definition.</summary>
    public static ControlRole FollowRole(PetDefinition d) => d.Movement.Mode == PetMovementMode.EngineGuard ? ControlRole.EngineGuard : ControlRole.PetFollow;

    /// <summary>The preparation: Pending until the PetStay lease is Active, then the replacement to <paramref name="final"/>
    /// is asked once, then Pending until that lease is Active (Ready). A lease that ends meanwhile fails it.</summary>
    public PetBodyPoll PollPreparation(ControlRole final, out string? reason)
    {
        reason = null;
        if (Prepared) return PetBodyPoll.Ready;
        var s = CreatureControl.Inspect(Base);
        if (s is null)
        {
            reason = "ControlLost: the preparation lease expired";
            return PetBodyPoll.Failed;
        }
        if (s.IsTerminal)
        {
            reason = $"ControlFailed: {s.Reason}";
            return PetBodyPoll.Failed;
        }
        if (!CreatureControl.CanDriveNow(Base)) return PetBodyPoll.Pending;
        if (BaseRole != final)
        {
            if (!Replace(final, out reason)) return PetBodyPoll.Failed;
            return PetBodyPoll.Pending;
        }
        // the body is the pet's now; its base lease is reported by the next Changes() as first seen (PetControlChangedEvent
        // with Before null), not recorded here unreported
        Prepared = true;
        return PetBodyPoll.Ready;
    }

    private bool Replace(ControlRole role, out string? reason)
    {
        var old = Base;
        ControlRequestResult r;
        try { r = CreatureControl.AcquireAs(Service, null, Body, role); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            reason = $"ControlRefused: {e.Message}";
            return false;
        }
        if (!r.Ok)
        {
            reason = $"ControlRefused: {r.Reason}";
            return false;
        }
        // the same owner's next base role replaced the old one atomically (Released, Replaced): its end is ours
        _ours.Add(old.Value);
        Base = r.Lease;
        BaseRole = role;
        reason = null;
        return true;
    }

    /// <summary>An order's lease: Follow and Stay move the base lease (a PetCombat lease is released), Attack adds a
    /// PetCombat lease above it. Admission only; during a ride the new lease waits Preempted.</summary>
    public bool SetOrder(PetOrder order, ControlRole follow, out string? reason)
    {
        reason = null;
        if (order == PetOrder.Attack)
        {
            if (Combat.Value != 0 && CreatureControl.Inspect(Combat) is { IsTerminal: false }) return true;
            ControlRequestResult r;
            try { r = CreatureControl.AcquireAs(Service, null, Body, ControlRole.PetCombat); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                reason = $"ControlRefused: {e.Message}";
                return false;
            }
            if (!r.Ok)
            {
                reason = $"ControlRefused: {r.Reason}";
                return false;
            }
            Combat = r.Lease;
            return true;
        }
        EndCombat();
        var want = order == PetOrder.Stay ? ControlRole.PetStay : follow;
        if (want == BaseRole && CreatureControl.Inspect(Base) is { IsTerminal: false }) return true;
        return Replace(want, out reason);
    }

    public void EndCombat()
    {
        if (Combat.Value == 0) return;
        _ours.Add(Combat.Value);
        CreatureControl.ReleaseNow(Combat);
        Combat = default;
    }

    /// <summary>The follower's authority, checked right before each write (never cached).</summary>
    public bool CanDrive => Prepared && CreatureControl.CanDriveNow(Base);

    /// <summary>The PetCombat lease is the body's Active winner (the game's AI fights).</summary>
    public bool Fighting => Combat.Value != 0 && CreatureControl.CanDriveNow(Combat);

    /// <summary>The role that holds the body now (Ridden during a ride; Quarantine once quarantined).</summary>
    public ControlRole Effective => CreatureControl.Inspect(Base)?.EffectiveRole ?? ControlRole.None;

    /// <summary>A lease of ours that ended without our asking (the broker failed it, the world or the body ended): null when none.</summary>
    public string? Lost()
    {
        foreach (var lease in new[] { Base, Combat })
        {
            if (lease.Value == 0 || _ours.Contains(lease.Value)) continue;
            var s = CreatureControl.Inspect(lease);
            if (s is null) return "ControlLost: the lease expired";
            if (s.IsTerminal) return $"ControlFailed: {s.Result}: {s.Reason}";
        }
        return null;
    }

    /// <summary>The leases' changes since the last call (PetControlChangedEvent).</summary>
    public IEnumerable<(ControlLeaseSnapshot? Before, ControlLeaseSnapshot After)> Changes()
    {
        var b = Base.Value != 0 ? CreatureControl.Inspect(Base) : null;
        if (b is not null && !Same(_lastBase, b))
        {
            yield return (_lastBase, b);
            _lastBase = b;
        }
        var c = Combat.Value != 0 ? CreatureControl.Inspect(Combat) : null;
        if (c is not null && !Same(_lastCombat, c))
        {
            yield return (_lastCombat, c);
            _lastCombat = c;
        }
    }

    private static bool Same(ControlLeaseSnapshot? a, ControlLeaseSnapshot b) =>
        a is not null && a.Lease == b.Lease && a.State == b.State && a.Result == b.Result && a.EffectiveRole == b.EffectiveRole &&
        a.WinningLease == b.WinningLease && a.TransitionSequence == b.TransitionSequence;

    /// <summary>Latches the body's quarantine: every lease loses authority at once (also a rider's), its root is denied, and
    /// the broker retires its rows once the body is gone. Our leases end there: their end is ours.</summary>
    public bool Quarantine(uint cause)
    {
        _ours.Add(Base.Value);
        if (Combat.Value != 0) _ours.Add(Combat.Value);
        return CreatureControl.QuarantineBody(Key, cause);
    }

    /// <summary>Releases every lease of the pet (a staging body that never became the pet).</summary>
    public void Release()
    {
        EndCombat();
        if (Base.Value == 0) return;
        _ours.Add(Base.Value);
        CreatureControl.ReleaseNow(Base);
    }

    /// <summary>The broker's quarantine cause for a pet's reason to retire its body (19d §4.3): 1-4 a semantic down, 5-9 a
    /// voluntary teardown.</summary>
    public static uint CauseOf(PetChangeCause cause, bool release = false) => cause switch
    {
        PetChangeCause.NativeLethal => 1,
        PetChangeCause.ProtectionLost => 2,
        PetChangeCause.Control => 3,
        PetChangeCause.BodyLost => 4,
        PetChangeCause.Logout or PetChangeCause.Unload => 7,
        PetChangeCause.WorldEnd => 9,
        _ => release ? 6u : 5u,
    };
}
