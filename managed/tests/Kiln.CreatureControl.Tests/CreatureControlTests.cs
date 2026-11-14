// SPDX-License-Identifier: MIT
// Kiln.CreatureControl offline, on the kit's fake kiln.creature_control: admission is never permission, WaitActive's
// timeout keeps the lease, Release of a retained terminal lease is idempotent, an off row throws naming it, the change
// event, and the mirrors of the native records. The broker itself is native/services/creature_control/tests (host) and
// mods/tests/creature-control-test (lab).
using System.Runtime.InteropServices;
using Kiln;
using Kiln.TestKit;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Kiln.CreatureControlTests;

public sealed unsafe class CreatureControlTests
{
    private static (KilnHarness Kiln, EntityRef Body) Start()
    {
        var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var e = kiln.Entities.Create(new WorldPosition(10, 0, 10));
        return (kiln, new EntityRef(e, 0));
    }

    private static ControlRequestResult Acquire(KilnHarness kiln, EntityRef body, ControlRole role = ControlRole.PetFollow) =>
        kiln.Run(() => CreatureControl.Acquire(body, role, CreatureControl.NewRequestId()));

    [Fact]
    public void TheMirrorsKeepTheNativeLayouts()
    {
        Assert.Equal(24, sizeof(CreatureControlNative.BodyKey));
        Assert.Equal(48, sizeof(CreatureControlNative.Request));
        Assert.Equal(248, sizeof(CreatureControlNative.State));
        Assert.Equal(24, (int)Marshal.OffsetOf<CreatureControlNative.State>(nameof(CreatureControlNative.State.Body)));
        Assert.Equal(48, (int)Marshal.OffsetOf<CreatureControlNative.State>(nameof(CreatureControlNative.State.Lease)));
        Assert.Equal(64, (int)Marshal.OffsetOf<CreatureControlNative.State>(nameof(CreatureControlNative.State.RequestId)));
        Assert.Equal(72, (int)Marshal.OffsetOf<CreatureControlNative.State>(nameof(CreatureControlNative.State.TransitionSequence)));
        Assert.Equal(80, (int)Marshal.OffsetOf<CreatureControlNative.State>(nameof(CreatureControlNative.State.NoticeSequence)));
        Assert.Equal(88, (int)Marshal.OffsetOf<CreatureControlNative.State>("Reason"));
        Assert.Equal(8 + 11 * IntPtr.Size, sizeof(CreatureControlNative.Table));
        // the numbers riding and pets code against
        Assert.Equal(3u, (uint)ControlRole.Ridden);
        Assert.Equal(5u, (uint)ControlRole.Vehicle);
        Assert.Equal(7u, (uint)ControlRole.EngineGuard);
        Assert.Equal(4u, (uint)ControlLeaseState.Failed);
        Assert.Equal(14u, (uint)ControlResultCode.AiInhibited);
    }

    [Fact]
    public void AdmissionIsNeverPermissionAndWaitActiveSeesTheGrant()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        var r = Acquire(kiln, body);
        Assert.True(r.Ok, r.Reason);
        Assert.NotEqual(0ul, r.Lease.Value);
        Assert.Equal(ControlLeaseState.Pending, r.Initial!.State);
        Assert.False(r.Initial.CanDrive);
        var wait = kiln.Run(() => CreatureControl.WaitActive(r.Lease, 2));
        Assert.False(wait.IsCompleted);
        kiln.Step(3);
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.True(wait.Result.Ok, wait.Result.Reason);
        var s = kiln.Run(() => CreatureControl.Inspect(r.Lease))!;
        Assert.True(s.CanDrive);
        Assert.Equal(1ul, s.TransitionSequence);
        Assert.Equal(ControlRole.PetFollow, s.EffectiveRole);
        Assert.Equal(body.Id, s.Body.Entity);
        Assert.True(kiln.CreatureControl.IsControlled(body.Id));
    }

    [Fact]
    public void AWaitThatTimesOutKeepsTheLease()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        kiln.CreatureControl.SetBusy(body.Id);  // an attack is playing: the broker waits for it
        var r = Acquire(kiln, body);
        var wait = kiln.Run(() => CreatureControl.WaitActive(r.Lease, 0.2));
        kiln.Step(10);  // 0.4 s of simulation time
        Assert.True(wait.IsCompletedSuccessfully);
        Assert.False(wait.Result.Ok);
        Assert.Contains("lease is kept", wait.Result.Reason);
        Assert.Equal(ControlLeaseState.Pending, kiln.Run(() => CreatureControl.Inspect(r.Lease))!.State);  // not canceled
        kiln.CreatureControl.SetBusy(body.Id, false);
        kiln.Step(3);
        Assert.True(kiln.Run(() => CreatureControl.Inspect(r.Lease))!.CanDrive);
    }

    [Fact]
    public void ReleaseOfARetainedTerminalLeaseIsIdempotent()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        var r = Acquire(kiln, body);
        kiln.Step(3);
        var first = kiln.Run(() => CreatureControl.Release(r.Lease));
        Assert.False(kiln.Run(() => CreatureControl.Inspect(r.Lease))!.CanDrive);  // permission ends at the call
        Assert.False(first.IsCompleted);  // the body is restored first
        kiln.Step(6);
        Assert.True(first.IsCompletedSuccessfully);
        Assert.True(first.Result.Ok, first.Result.Reason);
        Assert.Equal((ControlLeaseState.Released, ControlResultCode.Released), (first.Result.State!.State, first.Result.State.Result));
        var again = kiln.Run(() => CreatureControl.Release(r.Lease));
        Assert.True(again.IsCompletedSuccessfully);
        Assert.True(again.Result.Ok);
        Assert.Equal(first.Result.State.Reason, again.Result.State!.Reason);
        Assert.False(kiln.CreatureControl.IsControlled(body.Id));
        // the binding this face took for the lease is given back once it ended
        Assert.Contains(kiln.CreatureControl.Calls, c => c.Function == "release_body" && c.Entity == body.Id && c.Result == 0);
    }

    [Fact]
    public void AnOffRowThrowsNamingIt()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        kiln.Capabilities.Override("creature_control", false);
        var e = Assert.Throws<KilnUnavailableException>(() => kiln.Run(() => CreatureControl.Acquire(body, ControlRole.PetFollow, new ControlRequestId(5))));
        Assert.Equal("creature_control", e.Capability);
        // the table row still serves the rest: a lease admitted before can be inspected and released
        kiln.Capabilities.Override("creature_control", true);
        var r = Acquire(kiln, body);
        kiln.Capabilities.Override("creature_control", false);
        Assert.NotNull(kiln.Run(() => CreatureControl.Inspect(r.Lease)));
        var release = kiln.Run(() => CreatureControl.Release(r.Lease));
        kiln.Step(3);
        Assert.True(release.IsCompletedSuccessfully);
    }

    [Fact]
    public void BadArgumentsThrowAndRefusalsAreResults()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(EntityRef.None, ControlRole.PetFollow, new ControlRequestId(1))));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(body, ControlRole.Vehicle, new ControlRequestId(1))));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(body, ControlRole.Quarantine, new ControlRequestId(1))));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(body, ControlRole.PetFollow, default)));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(new EntityRef(new EntityId(3), 0), ControlRole.PetFollow, new ControlRequestId(1))));
        Assert.IsType<ArgumentOutOfRangeException>(Record.Exception(() => { _ = kiln.Run(() => CreatureControl.WaitActive(new ControlLeaseId(1), 0)); }));
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Inspect(default)));
        // ids increase; one below an admitted id is refused, not thrown
        var a = kiln.Run(() => CreatureControl.NewRequestId());
        var b = kiln.Run(() => CreatureControl.NewRequestId());
        Assert.True(b.Value > a.Value);
        Assert.True(kiln.Run(() => CreatureControl.Acquire(body, ControlRole.PetFollow, b)).Ok);
        var other = kiln.Entities.Create(new WorldPosition(20, 0, 20));
        var old = kiln.Run(() => CreatureControl.Acquire(new EntityRef(other, 0), ControlRole.PetFollow, a));
        Assert.False(old.Ok);
        Assert.Equal(ControlResultCode.ExpiredHandle, old.Initial!.Result);
        // one lease per role: a second ride on the body is Busy; a body that is gone: BodyGone
        Assert.True(Acquire(kiln, body, ControlRole.Ridden).Ok);
        var busy = Acquire(kiln, body, ControlRole.Ridden);
        Assert.False(busy.Ok);
        Assert.StartsWith("Busy", busy.Reason);
        kiln.Entities.Destroy(other);
        var gone = Acquire(kiln, new EntityRef(other, 0));
        Assert.False(gone.Ok);
        Assert.StartsWith("BodyGone", gone.Reason);
        // a same id for another request is the caller's error
        Assert.Throws<ArgumentException>(() => kiln.Run(() => CreatureControl.Acquire(body, ControlRole.Ridden, b)));
    }

    [Fact]
    public void EveryChangeIsOneEventAndALostBodyFailsItsLease()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        var seen = new List<ControlChangedEvent>();
        using var sub = kiln.Run(() => Events.On<ControlChangedEvent>(seen.Add));
        var r = Acquire(kiln, body);
        kiln.Step(3);
        Assert.Contains(seen, e => e.Lease == r.Lease && e.Before!.State == ControlLeaseState.Pending && e.After.State == ControlLeaseState.Active &&
                                   e.Cause == ControlResultCode.Granted);
        kiln.Entities.Destroy(body.Id);
        kiln.Step(2);
        var lost = seen[^1];
        Assert.Equal((ControlLeaseState.Failed, ControlResultCode.BodyGone), (lost.After.State, lost.Cause));
        var release = kiln.Run(() => CreatureControl.Release(r.Lease));
        Assert.True(release.IsCompletedSuccessfully);
        Assert.False(release.Result.Ok);  // a terminal loss is not a release
        Assert.StartsWith("BodyGone", release.Result.Reason);
        Assert.Equal(seen.Count, seen.Select(e => (e.Lease, e.After.State, e.After.Result)).Distinct().Count());  // once per change
    }

    [Fact]
    public void ARiddenLeasePreemptsTheBaseLeaseWhichResumesWithANewSequence()
    {
        var (kiln, body) = Start();
        using var _k = kiln;
        var follow = Acquire(kiln, body);
        kiln.Step(3);
        Assert.True(kiln.Run(() => CreatureControl.Inspect(follow.Lease))!.CanDrive);
        var ride = Acquire(kiln, body, ControlRole.Ridden);
        Assert.True(ride.Ok, ride.Reason);
        kiln.Step(2);
        var f = kiln.Run(() => CreatureControl.Inspect(follow.Lease))!;
        var r = kiln.Run(() => CreatureControl.Inspect(ride.Lease))!;
        Assert.Equal((ControlLeaseState.Preempted, false), (f.State, f.CanDrive));
        Assert.True(r.CanDrive);
        Assert.Equal(2ul, r.TransitionSequence);
        Assert.Equal(ControlRole.Ridden, f.EffectiveRole);
        // the same mod's other base role replaces its Follow at once, kept Preempted under the ride
        var stay = Acquire(kiln, body, ControlRole.PetStay);
        Assert.True(stay.Ok, stay.Reason);
        Assert.Equal(ControlLeaseState.Preempted, stay.Initial!.State);
        var replaced = kiln.Run(() => CreatureControl.Inspect(follow.Lease))!;
        Assert.Equal((ControlLeaseState.Released, ControlResultCode.Canceled), (replaced.State, replaced.Result));
        Assert.StartsWith("Replaced", replaced.Reason);
        var end = kiln.Run(() => CreatureControl.Release(ride.Lease));
        kiln.Step(2);
        Assert.True(end.IsCompletedSuccessfully);
        var s = kiln.Run(() => CreatureControl.Inspect(stay.Lease))!;
        Assert.True(s.CanDrive);
        Assert.Equal(3ul, s.TransitionSequence);
    }

    [Fact]
    public void AServiceLeaseThatEndsIsReportedOnceToTheService()
    {
        // Kiln.Blocks watches its brains' leases this way: a lease that fails while the brain lives falls back to the AI off
        var (kiln, body) = Start();
        using var _k = kiln;
        var ended = new List<ControlLeaseSnapshot>();
        void Seen(ControlLeaseSnapshot s) => ended.Add(s);
        CreatureControl.LeaseEnded += Seen;
        try
        {
            var r = kiln.Run(() => CreatureControl.AcquireAs("kiln.blocks", null, body, ControlRole.PetFollow));
            Assert.True(r.Ok, r.Reason);
            kiln.Step(3);
            Assert.Empty(ended);
            kiln.Entities.Destroy(body.Id);
            kiln.Step(4);
            var one = Assert.Single(ended);
            Assert.Equal((r.Lease, ControlLeaseState.Failed, ControlResultCode.BodyGone), (one.Lease, one.State, one.Result));

            // a release is an end too, reported with its own result
            var other = new EntityRef(kiln.Entities.Create(new WorldPosition(20, 0, 20)), 0);
            var r2 = kiln.Run(() => CreatureControl.AcquireAs("kiln.blocks", null, other, ControlRole.PetFollow));
            kiln.Step(3);
            kiln.Run(() => CreatureControl.ReleaseNow(r2.Lease));
            kiln.Step(6);
            Assert.Equal(2, ended.Count);
            Assert.Equal((r2.Lease, ControlLeaseState.Released), (ended[1].Lease, ended[1].State));
        }
        finally { CreatureControl.LeaseEnded -= Seen; }
    }
}
