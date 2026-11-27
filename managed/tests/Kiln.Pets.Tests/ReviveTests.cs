// SPDX-License-Identifier: MIT
// Package 6 (19d §4.5, §5.1): the revive channel. From a visible anchor only; the cost quoted before anything starts and
// again at completion, taken exactly once after the preview is ready; a mod may refuse the attempt; range, leaving and the
// owner going down always cancel, movement and damage when the definition says so; every cancel before the debit takes
// nothing; a failure after the debit keeps the receipt and the pet Downed; CancelRevive names its channel.
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

internal sealed class FakeCosts : IPetCosts
{
    public string? QuoteRefusal;
    public bool CommitFails;
    public int Quotes, Commits;
    public Func<string?>? SecondQuote;
    public TaskCompletionSource<CostReceipt>? HeldCommit;

    public Task<string?> Quote(PetOwner owner, PetCostDefinition cost, ItemSelection? selection)
    {
        Quotes++;
        if (Quotes > 1 && SecondQuote is { } second) return Task.FromResult(second());
        return Task.FromResult(QuoteRefusal);
    }

    public Task<CostReceipt> Commit(PetOwner owner, PetCostDefinition cost, ItemSelection? selection)
    {
        Commits++;
        if (HeldCommit is { } held) return held.Task;
        return Task.FromResult(new CostReceipt(cost.Item, cost.Count, CommitFails ? 0 : cost.Count, !CommitFails));
    }
}

public sealed class ReviveTests : IDisposable
{
    private readonly RosterTests _t = new();
    private readonly FakeCosts _costs = new();
    private bool _refuse;

    public ReviveTests()
    {
        Pets.DefineAs(new PetDefinition
        {
            Id = "mod-a:paid", Creature = "game:enemy_wildbeast_wolf",
            Revive = new PetRevivePolicy { ChannelSeconds = 1, HealthFraction = 0.25, Cost = new PetCostDefinition(new ItemId(77), 3) },
        }, "mod-a");
        Pets.DefineAs(new PetDefinition
        {
            Id = "mod-a:calm", Creature = "game:enemy_wildbeast_wolf",
            Revive = new PetRevivePolicy { ChannelSeconds = 1, CancelOnMovement = false, CancelOnObservedOwnerDamage = false },
        }, "mod-a");
        Pets.DefineAs(new PetDefinition { Id = "mod-a:mortal", Creature = "game:enemy_wildbeast_wolf", Revive = new PetRevivePolicy { Allowed = false } }, "mod-a");
        _t.Roster.Costs = _costs;
        _t.Roster.AskReviving = e => { if (_refuse) e.Cancel(); return e; };
    }

    public void Dispose() => _t.Dispose();

    /// <summary>A pet of <paramref name="def"/> that went down beside its owner, its anchor shown.</summary>
    private PetSnapshot Downed(string def = "mod-a:paid")
    {
        _t.World.Join(_t.A, new WorldPosition(10, 0, 10));
        var pet = _t.Grant(_t.A, def);
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.A, pet.Id).Ok);
        _t.BodyOf(pet.Id).Health = 0;
        _t.Step();
        var d = _t.Roster.Get(pet.Id)!;
        Assert.Equal((PetLife.Downed, PetPresence.RecoveryAnchor), (d.Life, d.Presence));
        _t.World.Move(_t.A, d.RecoveryAnchor!.Value);
        _t.Names();
        return d;
    }

    private Task<PetResult> Revive(PetSnapshot p, Guid? id = null) =>
        _t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.Revive, OperationId = id ?? Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, Payload = "revive" + id });

    private Task<PetResult> Cancel(PetSnapshot p, Guid target, Guid? id = null) =>
        _t.Roster.Submit(new PetRoster.Op { Kind = PetRoster.Kind.CancelRevive, OperationId = id ?? Guid.NewGuid(), OwnerKey = _t.A, Who = _t.Who(_t.A), Pet = p.Id, CancelTarget = target, Payload = "cancel" + target });

    [Fact]
    public void A_completed_channel_takes_the_cost_once_and_brings_a_new_body_at_the_health_fraction()
    {
        var d = Downed();
        var r = _t.Run(Revive(d), 200);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal((PetLife.Ready, PetPresence.Live, PetTransition.None), (r.Pet!.Life, r.Pet.Presence, r.Pet.Transition));
        Assert.Equal(25, r.Pet.Health);   // ceil(100 x 0.25)
        Assert.NotEqual(d.BodyGeneration, r.Pet.BodyGeneration);
        Assert.Equal((2, 1), (_costs.Quotes, _costs.Commits));
        var receipt = Assert.Single(r.Costs);
        Assert.Equal((new ItemId(77), 3, 3, true), (receipt.Item, receipt.Requested, receipt.Taken, receipt.Committed));
        Assert.Equal(["ReviveStarted", "Revived"], _t.Names());
        Assert.True(_t.World.Bodies.Values.Single().Request.Preview);
    }

    [Fact]
    public void Only_a_visible_anchor_is_revived_and_only_in_range()
    {
        var d = Downed();
        Assert.True(_t.Do(PetRoster.Kind.Dismiss, _t.A, d.Id).Ok);   // the anchor hidden
        Assert.StartsWith("RecoveryNotSummoned", _t.Run(Revive(d)).Reason);
        Assert.True(_t.Do(PetRoster.Kind.Summon, _t.A, d.Id).Ok);
        _t.World.Move(_t.A, new WorldPosition(_t.Roster.Get(d.Id)!.RecoveryAnchor!.Value.X + 10, 0, 10));
        Assert.StartsWith("OutOfRange", _t.Run(Revive(d)).Reason);
        Assert.Equal(0, _costs.Quotes);
    }

    [Fact]
    public void A_definition_may_turn_the_revive_off()
    {
        var d = Downed("mod-a:mortal");
        Assert.StartsWith("ReviveNotAllowed", _t.Run(Revive(d)).Reason);
    }

    [Fact]
    public void A_cost_the_owner_cannot_pay_refuses_before_anything_starts()
    {
        var d = Downed();
        _costs.QuoteRefusal = "CostUnavailable: no meat";
        var r = _t.Run(Revive(d));
        Assert.StartsWith("CostUnavailable", r.Reason);
        Assert.Equal(0, _costs.Commits);
        Assert.Empty(_t.Names());
        Assert.Equal(PetTransition.None, _t.Roster.Get(d.Id)!.Transition);
    }

    [Fact]
    public void A_mod_may_refuse_the_attempt_and_nothing_starts()
    {
        var d = Downed();
        _refuse = true;
        var r = _t.Run(Revive(d));
        Assert.StartsWith("ReviveRefused", r.Reason);
        Assert.Empty(_t.Names());
        Assert.Equal(0, _costs.Commits);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("hurt")]
    [InlineData("range")]
    [InlineData("down")]
    [InlineData("leave")]
    public void The_channel_is_canceled_and_takes_nothing(string what)
    {
        var d = Downed();
        var t = Revive(d);
        _t.Step(5);
        Assert.Equal(PetTransition.Reviving, _t.Roster.Get(d.Id)!.Transition);
        var at = _t.World.Online[_t.A].Position;
        switch (what)
        {
            case "move": _t.World.Move(_t.A, new WorldPosition(at.X + 1, at.Y, at.Z)); break;
            case "hurt": _t.World.OwnerHp[_t.A] = 60; break;
            case "range": _t.World.Move(_t.A, new WorldPosition(at.X + 20, at.Y, at.Z)); break;
            case "down": _t.World.OwnerHp[_t.A] = 0; break;
            case "leave": _t.World.Leave(_t.A); break;
        }
        _t.Step(2);
        Assert.True(t.IsCompleted);
        Assert.StartsWith("CanceledNoCost", t.Result.Reason);
        Assert.Equal(0, _costs.Commits);
        Assert.Equal(PetLife.Downed, _t.Roster.Get(d.Id)!.Life);
        Assert.Empty(_t.World.Bodies);
        Assert.Contains("ReviveCanceled", _t.Names());
    }

    [Fact]
    public void Movement_and_damage_cancel_only_when_the_definition_says_so()
    {
        var d = Downed("mod-a:calm");
        var t = Revive(d);
        _t.Step(5);
        var at = _t.World.Online[_t.A].Position;
        _t.World.Move(_t.A, new WorldPosition(at.X + 1, at.Y, at.Z));
        _t.World.OwnerHp[_t.A] = 50;
        var r = _t.Run(t, 200);
        Assert.True(r.Ok, r.Reason);
    }

    [Fact]
    public void A_cancel_names_its_channel_and_a_repeat_gives_its_first_answer()
    {
        var d = Downed();
        var id = Guid.NewGuid();
        var t = Revive(d, id);
        _t.Step(5);
        var stray = _t.Run(Cancel(d, Guid.NewGuid()));
        Assert.StartsWith("NoChannel", stray.Reason);
        Assert.Equal(PetTransition.Reviving, _t.Roster.Get(d.Id)!.Transition);   // a cancel of another id ends nothing
        var cancelId = Guid.NewGuid();
        var c = _t.Run(Cancel(d, id, cancelId));
        Assert.True(c.Ok, c.Reason);
        Assert.StartsWith("CanceledNoCost", t.Result.Reason);
        Assert.Same(c, _t.Run(Cancel(d, id, cancelId)));
        Assert.Equal(0, _costs.Commits);
    }

    [Fact]
    public void A_cancel_after_a_completed_revive_never_undoes_it()
    {
        var d = Downed();
        var id = Guid.NewGuid();
        Assert.True(_t.Run(Revive(d, id), 200).Ok);
        var c = _t.Run(Cancel(d, id));
        Assert.StartsWith("AlreadyCompleted", c.Reason);
        Assert.Equal(PetPresence.Live, _t.Roster.Get(d.Id)!.Presence);
    }

    [Fact]
    public void A_cost_gone_at_completion_takes_nothing_and_drops_the_preview()
    {
        var d = Downed();
        _costs.SecondQuote = () => "CostUnavailable: the meat was eaten";
        var r = _t.Run(Revive(d), 200);
        Assert.StartsWith("CostUnavailable", r.Reason);
        Assert.Equal(0, _costs.Commits);
        Assert.Empty(_t.World.Bodies);
        Assert.Equal(PetLife.Downed, _t.Roster.Get(d.Id)!.Life);
    }

    [Fact]
    public void A_debit_refused_at_the_last_moment_takes_nothing()
    {
        var d = Downed();
        _costs.CommitFails = true;
        var r = _t.Run(Revive(d), 200);
        Assert.StartsWith("CostUnavailable", r.Reason);
        Assert.Empty(r.Costs);
        Assert.Empty(_t.World.Bodies);
    }

    [Fact]
    public void A_body_lost_after_the_debit_reports_the_receipt_and_stays_downed()
    {
        var d = Downed();
        _costs.HeldCommit = new TaskCompletionSource<CostReceipt>();
        var t = Revive(d);
        for (var i = 0; i < 200 && _costs.Commits == 0; i++) _t.Step();
        Assert.Equal(1, _costs.Commits);
        _t.World.Bodies.Clear();   // the preview is lost while the debit settles
        _costs.HeldCommit.SetResult(new CostReceipt(new ItemId(77), 3, 3, true));
        var r = _t.Run(t);
        Assert.StartsWith("ReviveFailedCostCommitted", r.Reason);
        var receipt = Assert.Single(r.Costs);
        Assert.True(receipt.Committed);
        Assert.Equal(PetLife.Downed, _t.Roster.Get(d.Id)!.Life);
    }

    [Fact]
    public void A_cancel_while_the_debit_settles_reports_what_was_taken()
    {
        var d = Downed();
        _costs.HeldCommit = new TaskCompletionSource<CostReceipt>();
        var id = Guid.NewGuid();
        var t = Revive(d, id);
        for (var i = 0; i < 200 && _costs.Commits == 0; i++) _t.Step();
        var c = Cancel(d, id);
        _t.Step();
        Assert.False(c.IsCompleted);   // it waits for the debit to settle
        _costs.HeldCommit.SetResult(new CostReceipt(new ItemId(77), 3, 3, true));
        _t.Step();
        Assert.StartsWith("CanceledCostCommitted", t.Result.Reason);
        Assert.True(c.Result.Ok);
        Assert.Single(c.Result.Costs);
        Assert.Empty(_t.World.Bodies);
    }

    // review of dev.32 pets: a logout (or the definition's mod unloading) while the exact debit was in flight answered
    // CanceledNoCost at once, and the items the debit then took were in no receipt
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cut_while_the_debit_settles_reports_what_was_taken(bool logout)
    {
        var d = Downed();
        _costs.HeldCommit = new TaskCompletionSource<CostReceipt>();
        var t = Revive(d);
        for (var i = 0; i < 200 && _costs.Commits == 0; i++) _t.Step();
        Assert.Equal(1, _costs.Commits);
        if (logout) _t.World.Leave(_t.A);
        else _t.Roster.Unload(["mod-a:paid"]);
        _t.Step();
        Assert.Empty(_t.World.Bodies);   // the preview goes at once
        Assert.False(t.IsCompleted);     // the answer waits for the receipt
        _costs.HeldCommit.SetResult(new CostReceipt(new ItemId(77), 3, 3, true));
        _t.Step();
        Assert.True(t.IsCompleted);
        Assert.StartsWith("CanceledCostCommitted", t.Result.Reason);
        Assert.Equal(3, Assert.Single(t.Result.Costs).Taken);
        Assert.Equal(PetLife.Downed, _t.Roster.Get(d.Id)!.Life);
    }

    [Fact]
    public void A_cut_while_a_refused_debit_settles_takes_nothing()
    {
        var d = Downed();
        _costs.HeldCommit = new TaskCompletionSource<CostReceipt>();
        var t = Revive(d);
        for (var i = 0; i < 200 && _costs.Commits == 0; i++) _t.Step();
        _t.World.Leave(_t.A);
        _t.Step();
        _costs.HeldCommit.SetResult(new CostReceipt(new ItemId(77), 3, 0, false));
        _t.Step();
        Assert.StartsWith("CanceledNoCost", t.Result.Reason);
        Assert.Empty(t.Result.Costs);
    }

    [Fact]
    public void A_second_revive_during_a_channel_is_busy()
    {
        var d = Downed();
        var t = Revive(d);
        _t.Step(3);
        Assert.StartsWith("Busy", _t.Run(Revive(d)).Reason);
        Assert.True(_t.Run(t, 200).Ok);
    }
}
