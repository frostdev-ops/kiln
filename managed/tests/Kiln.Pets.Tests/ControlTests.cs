// SPDX-License-Identifier: MIT
// Package 3 (19d §4.3, §5.2, gate 2/3): a pet's leases through the real Kiln.CreatureControl face on the kit's broker. A
// new body is the pet's only once its final base lease is Active (admission is never permission); orders move the base
// lease by the same owner's atomic replacement or add PetCombat above it; a rider (another owner) preempts without a loss
// and the base lease resumes after the ride with a new transition sequence; a body gone is lost control; quarantine
// revokes every lease at once and denies the root.
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace Kiln.PetsTests;

public sealed class ControlTests
{
    public sealed class RiderMod : Mod { }

    private static (KilnHarness Kiln, EntityRef Body) Start()
    {
        var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var e = kiln.Entities.Create(new WorldPosition(10, 0, 10));
        return (kiln, new EntityRef(e, 0));
    }

    private static PetControl Prepared(KilnHarness kiln, EntityRef body, ControlRole final = ControlRole.PetFollow)
    {
        var c = kiln.Run(() => PetControl.Prepare(body, out _))!;
        for (var i = 0; i < 20 && kiln.Run(() => c.PollPreparation(final, out _)) != PetBodyPoll.Ready; i++) kiln.Step();
        Assert.True(c.Prepared);
        return c;
    }

    [Fact]
    public void A_new_body_is_the_pets_only_once_its_final_base_lease_is_active()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var c = kiln.Run(() => PetControl.Prepare(body, out var why))!;
        Assert.NotNull(c);
        Assert.Equal(ControlRole.PetStay, c.BaseRole);
        Assert.Equal(PetBodyPoll.Pending, kiln.Run(() => c.PollPreparation(ControlRole.PetFollow, out _)));
        Assert.False(kiln.Run(() => c.CanDrive));
        var stay = c.Base;
        kiln.Step(2);
        // the preparation lease is Active: the same owner's replacement to the final role is asked, and that one is Pending
        Assert.Equal(PetBodyPoll.Pending, kiln.Run(() => c.PollPreparation(ControlRole.PetFollow, out _)));
        Assert.Equal(ControlRole.PetFollow, c.BaseRole);
        var replaced = kiln.Run(() => CreatureControl.Inspect(stay))!;
        Assert.Equal(ControlLeaseState.Released, replaced.State);
        Assert.Contains("Replaced", replaced.Reason, StringComparison.Ordinal);
        Assert.False(kiln.Run(() => c.CanDrive));
        kiln.Step(2);
        Assert.Equal(PetBodyPoll.Ready, kiln.Run(() => c.PollPreparation(ControlRole.PetFollow, out _)));
        Assert.True(kiln.Run(() => c.CanDrive));
        Assert.Null(kiln.Run(() => c.Lost()));   // the replaced lease's end is ours, never a loss
    }

    [Fact]
    public void The_final_base_lease_is_reported_first_seen_once_the_body_is_the_pets()
    {
        // PetControlChangedEvent's "Before null: first seen": the base lease a summoned or revived body ends its preparation on
        // (PetFollow Active) is the first change the pet's leases report (PETS-E2E: the lab's bot owner saw a hound's leases only
        // from its first order on, never the PetFollow lease it was summoned under)
        var (kiln, body) = Start();
        using var harness = kiln;
        var c = Prepared(kiln, body);
        var change = Assert.Single(kiln.Run(() => c.Changes().ToList()));
        Assert.Null(change.Before);
        Assert.Equal(ControlRole.PetFollow, change.After.EffectiveRole);
        Assert.Equal(ControlLeaseState.Active, change.After.State);
        Assert.True(change.After.CanDrive);
        Assert.Empty(kiln.Run(() => c.Changes().ToList()));   // nothing changed since
    }

    [Fact]
    public void An_engine_guard_pet_ends_on_the_AI_on_role()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var c = Prepared(kiln, body, ControlRole.EngineGuard);
        Assert.Equal(ControlRole.EngineGuard, c.BaseRole);
        Assert.Equal(ControlRole.EngineGuard, kiln.Run(() => c.Effective));
    }

    [Fact]
    public void Stay_replaces_follow_atomically_and_attack_adds_combat_above_it()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var c = Prepared(kiln, body);
        var follow = c.Base;
        Assert.True(kiln.Run(() => c.SetOrder(PetOrder.Stay, ControlRole.PetFollow, out _)));
        Assert.Equal(ControlRole.PetStay, c.BaseRole);
        Assert.Equal(ControlLeaseState.Released, kiln.Run(() => CreatureControl.Inspect(follow))!.State);
        kiln.Step(2);
        Assert.True(kiln.Run(() => c.CanDrive));
        Assert.True(kiln.Run(() => c.SetOrder(PetOrder.Attack, ControlRole.PetFollow, out _)));
        kiln.Step(6);
        Assert.True(kiln.Run(() => c.Fighting));
        Assert.False(kiln.Run(() => c.CanDrive));   // the base lease waits Preempted while combat holds the body
        Assert.Equal(ControlLeaseState.Preempted, kiln.Run(() => CreatureControl.Inspect(c.Base))!.State);
        var seq = kiln.Run(() => CreatureControl.Inspect(c.Base))!.TransitionSequence;
        Assert.True(kiln.Run(() => c.SetOrder(PetOrder.Stay, ControlRole.PetFollow, out _)));
        kiln.Step(8);
        Assert.False(kiln.Run(() => c.Fighting));
        Assert.True(kiln.Run(() => c.CanDrive));
        Assert.True(kiln.Run(() => CreatureControl.Inspect(c.Base))!.TransitionSequence > seq);
        Assert.Null(kiln.Run(() => c.Lost()));
    }

    [Fact]
    public void A_rider_preempts_without_a_loss_and_the_base_lease_resumes_after_the_ride()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var rider = kiln.Load<RiderMod>(new ModManifest("mounts-test"));
        var c = Prepared(kiln, body);
        var before = kiln.Run(() => CreatureControl.Inspect(c.Base))!.TransitionSequence;
        var ride = kiln.As(rider, () => CreatureControl.Acquire(body, ControlRole.Ridden, CreatureControl.NewRequestId()));
        Assert.True(ride.Ok, ride.Reason);
        kiln.Step(3);
        Assert.True(kiln.Run(() => CreatureControl.Inspect(ride.Lease))!.CanDrive);
        Assert.False(kiln.Run(() => c.CanDrive));
        Assert.Equal(ControlRole.Ridden, kiln.Run(() => c.Effective));
        Assert.Null(kiln.Run(() => c.Lost()));
        // the owner's new order during the ride commits with a Preempted lease; it does not wait for the dismount
        Assert.True(kiln.Run(() => c.SetOrder(PetOrder.Stay, ControlRole.PetFollow, out _)));
        Assert.Equal(ControlLeaseState.Preempted, kiln.Run(() => CreatureControl.Inspect(c.Base))!.State);
        kiln.As(rider, () => CreatureControl.Release(ride.Lease));
        kiln.Step(8);
        Assert.True(kiln.Run(() => c.CanDrive));
        Assert.Equal(ControlRole.PetStay, kiln.Run(() => c.Effective));
        Assert.True(kiln.Run(() => CreatureControl.Inspect(c.Base))!.TransitionSequence > before);
    }

    [Fact]
    public void A_body_gone_is_lost_control()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var c = Prepared(kiln, body);
        kiln.Entities.Destroy(body.Id);
        kiln.Step(2);
        var lost = kiln.Run(() => c.Lost());
        Assert.NotNull(lost);
        Assert.Contains("BodyGone", lost, StringComparison.Ordinal);
    }

    [Fact]
    public void Quarantine_revokes_every_lease_at_once_and_denies_the_root()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        var rider = kiln.Load<RiderMod>(new ModManifest("mounts-test-2"));
        var c = Prepared(kiln, body);
        var ride = kiln.As(rider, () => CreatureControl.Acquire(body, ControlRole.Ridden, CreatureControl.NewRequestId()));
        kiln.Step(3);
        Assert.True(kiln.Run(() => CreatureControl.Inspect(ride.Lease))!.CanDrive);
        Assert.True(kiln.Run(() => c.Quarantine(PetControl.CauseOf(PetChangeCause.NativeLethal))));
        Assert.False(kiln.Run(() => CreatureControl.Inspect(ride.Lease))!.CanDrive);   // the rider's too, at once
        Assert.False(kiln.Run(() => c.CanDrive));
        Assert.True(kiln.Run(() => CreatureControl.RootDenied(c.Key.WorldEpoch, body.Id)));
        Assert.Null(kiln.Run(() => c.Lost()));   // quarantined by the pet: its leases' end is ours
        kiln.Entities.Destroy(body.Id);
        kiln.Step(2);
        Assert.Equal(ControlResultCode.Retired, kiln.Run(() => CreatureControl.Inspect(c.Base))!.Result);
        Assert.True(kiln.Run(() => CreatureControl.RootDenied(c.Key.WorldEpoch, body.Id)));   // delayed hits of it stay denied
    }

    [Fact]
    public void A_body_whose_action_never_ends_fails_its_preparation()
    {
        var (kiln, body) = Start();
        using var harness = kiln;
        kiln.CreatureControl.SetBusy(body.Id);
        var c = kiln.Run(() => PetControl.Prepare(body, out _))!;
        string? why = null;
        var poll = PetBodyPoll.Pending;
        for (var i = 0; i < 200 && poll == PetBodyPoll.Pending; i++)
        {
            kiln.Step();
            poll = kiln.Run(() => c.PollPreparation(ControlRole.PetFollow, out why));
        }
        Assert.Equal(PetBodyPoll.Failed, poll);
        Assert.Contains("Quiescence", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PetChangeCause.NativeLethal, 1u)]
    [InlineData(PetChangeCause.ProtectionLost, 2u)]
    [InlineData(PetChangeCause.Control, 3u)]
    [InlineData(PetChangeCause.BodyLost, 4u)]
    [InlineData(PetChangeCause.Request, 5u)]
    [InlineData(PetChangeCause.Logout, 7u)]
    [InlineData(PetChangeCause.WorldEnd, 9u)]
    public void Retirement_causes_are_the_brokers_codes(PetChangeCause cause, uint code) => Assert.Equal(code, PetControl.CauseOf(cause));
}

public sealed class RosterControlTests : IDisposable
{
    private readonly RosterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void An_order_the_broker_refuses_leaves_the_pet_as_it_was()
    {
        var p = _t.Summoned(_t.A);
        _t.World.OrderRefusal = "ControlRefused: Busy";
        var r = _t.Run(_t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Command, OperationId = Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, Order = PetOrder.Stay, Payload = "stay" }));
        Assert.StartsWith("ControlRefused", r.Reason);
        Assert.Equal(PetOrder.Follow, _t.Roster.Get(p.Id)!.RequestedOrder);
        _t.World.OrderRefusal = null;
        var ok = _t.Run(_t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Command, OperationId = Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, Order = PetOrder.Stay, Payload = "stay2" }));
        Assert.True(ok.Ok, ok.Reason);
        Assert.Equal(PetOrder.Stay, ok.Pet!.RequestedOrder);
        Assert.Contains(_t.World.Orders, o => o.Order == PetOrder.Stay);
        Assert.Equal(["CommandChanged"], _t.Names());
    }

    [Fact]
    public void Lost_control_retires_the_body_and_downs_the_pet()
    {
        var p = _t.Summoned(_t.A);
        _t.BodyOf(p.Id).ControlLost = "ControlFailed: RestoreFailed";
        _t.Step();
        var d = _t.Roster.Get(p.Id)!;
        Assert.Equal(PetLife.Downed, d.Life);
        Assert.Empty(_t.World.Bodies);
        Assert.Equal(["TransitionChanged", "BodyRetired", "Downed"], _t.Names());
    }
}
