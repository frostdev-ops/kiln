// SPDX-License-Identifier: MIT
// The review of dev.32 TAME (Opus review + fix pass): what the attempt machine did wrong before each fix. Each case is the
// red test of one finding (docs/kiln/services/taming.md says what the engine promises; dossier 19e 4.3-4.5 the contract).
using Kiln;
using Xunit;
using static Kiln.TamingTests.TamingFixture;

namespace Kiln.TamingTests;

public sealed class TamingReviewTests
{
    private static TamingDefinition FeedTwice() => Def(new FeedStep(Bait, 2), new FeedStep(Bait, 1));

    // ---- finding: a mod's handler that calls Taming.Cancel (or Begin) from inside a before-event ----------------------
    // 19e 4.4: "There is a transient reentrancy guard while its event runs; another operation queues ... rather than
    // stealing the target." The engine ended the attempt at once, then the Advance (or Retry) that posted the event went on:
    // it took the step's cost from a Canceled attempt and made it Active again, or bound the pet after an escrow refund.

    [Fact]
    public void A_cancel_from_an_advancing_handler_ends_the_attempt_and_takes_no_cost()
    {
        var f = new TamingFixture().With(Def(new FeedStep(Bait, 2), new FeedStep(Bait, 1), new WeakenStep(1)) with { Refund = TamingRefundPolicy.Escrow });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;   // one step paid (escrowed)
        Task<TamingResult>? cancel = null;
        f.Refuse = e =>
        {
            if (e is TamingAdvancingEvent a) cancel = f.Engine.Cancel(f.A, p.Id, Token(a.Context.Before));
            return false;
        };
        var r = f.Advance(p, f.Stack(Bait));
        Assert.False(r.Ok);
        Assert.False(r.StepApplied);
        Assert.StartsWith("Canceled", r.Reason);
        Assert.Single(f.Costs.Calls);                                      // the first step's only: no cost for the canceled one
        var closed = f.Engine.Inspect(p.Id)!;
        Assert.Equal((TamingState.Canceled, 1), (closed.State, closed.CompletedSteps));
        Assert.Single(f.Costs.Refunds);                                    // the escrowed step, once
        Assert.NotNull(cancel);
        Assert.True(cancel!.IsCompleted);
        Assert.True(cancel.Result.Ok, cancel.Result.Reason);
        Assert.Single(f.Events.OfType<TamingCanceledEvent>());
        f.Tick(3);
        Assert.Equal(TamingState.Canceled, f.Engine.Inspect(p.Id)!.State);   // never made Active again
        Assert.DoesNotContain("bind", f.Binder.Calls);
    }

    [Fact]
    public void A_cancel_from_a_committing_handler_of_the_last_step_takes_no_last_cost_and_binds_nothing()
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = TamingRefundPolicy.Escrow });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        Task<TamingResult>? cancel = null;
        f.Refuse = e =>
        {
            if (e is TamingCommittingEvent) cancel = f.Engine.Cancel(f.A, p.Id, Token(f.Engine.Inspect(p.Id)!));
            return false;
        };
        var r = f.Advance(p, f.Stack(Bait));
        Assert.False(r.Ok);
        Assert.StartsWith("Canceled", r.Reason);
        Assert.Single(f.Costs.Calls);
        Assert.DoesNotContain("bind", f.Binder.Calls);
        Assert.Equal(TamingState.Canceled, f.Engine.Inspect(p.Id)!.State);
        Assert.Single(f.Costs.Refunds);
        Assert.True(cancel!.Result.Ok, cancel.Result.Reason);
        Assert.Empty(f.Events.OfType<TamedEvent>());
    }

    [Fact]
    public void A_cancel_from_the_committing_handler_of_a_retry_never_binds_a_pet_after_the_refund()
    {
        var f = new TamingFixture().With(Def(new FeedStep(Bait)) with { Refund = TamingRefundPolicy.Escrow });
        f.Binder.Result = new TamingBindResult(false, null, "Cap: the roster is full", true);
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        Assert.Equal(TamingState.AwaitingCommit, p.State);
        f.Binder.Result = new TamingBindResult(true, null, null, false);
        Task<TamingResult>? cancel = null;
        f.Refuse = e =>
        {
            if (e is TamingCommittingEvent) cancel = f.Engine.Cancel(f.A, p.Id, Token(f.Engine.Inspect(p.Id)!));
            return false;
        };
        var retry = Done(f.Engine.Retry(f.A, p.Id, Token(p)));
        Assert.False(retry.Ok);
        Assert.StartsWith("Canceled", retry.Reason);
        Assert.Equal(1, f.Binder.Calls.Count(c => c == "bind"));           // the deferred one only
        Assert.Equal(TamingState.Canceled, f.Engine.Inspect(p.Id)!.State);
        Assert.Single(f.Costs.Refunds);                                    // escrow: given back once, and no pet
        Assert.Empty(f.Events.OfType<TamedEvent>());
        Assert.True(cancel!.Result.Ok, cancel.Result.Reason);
    }

    [Fact]
    public void A_begin_from_a_beginning_handler_leaves_one_claimant_on_the_target()
    {
        var f = new TamingFixture().With(FeedTwice());
        TamingResult? inner = null;
        var nested = false;
        f.Refuse = e =>
        {
            if (e is TamingBeginningEvent && !nested)
            {
                nested = true;
                inner = f.Begin(who: f.B);
            }
            return false;
        };
        var outer = f.Begin();
        Assert.NotNull(inner);
        Assert.True(inner!.Ok, inner.Reason);
        Assert.False(outer.Ok);
        Assert.StartsWith("Busy", outer.Reason);
        Assert.Equal(1, f.Engine.Active);
        Assert.Single(f.Events.OfType<TamingStartedEvent>());
    }

    // ---- finding: a refund the adapter refuses by throwing (its row went off) escaped the settlement --------------------
    // Settlement called the adapter's Refund without a guard: its KilnUnavailableException left Cancel (the caller saw an
    // exception for a cancel that had happened), skipped the end event and the cancel's answer, and in the tick it stopped
    // every later attempt's expiry for that step.

    [Fact]
    public void A_refund_the_adapter_refuses_by_throwing_still_ends_the_attempt_and_says_so()
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = TamingRefundPolicy.Escrow });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        f.Costs.RefundThrow = new KilnUnavailableException("items.costs.exact", "stack costs are off");
        var cancel = Done(f.Engine.Cancel(f.A, p.Id, Token(p)));
        Assert.True(cancel.Ok, cancel.Reason);
        var closed = f.Engine.Inspect(p.Id)!;
        Assert.Equal(TamingState.Canceled, closed.State);
        var refund = Assert.Single(closed.Refunds);
        Assert.Same(p.Spent.Single(), refund.Paid);
        Assert.NotNull(refund.Refund);
        Assert.False(refund.Refund!.Committed);
        Assert.Equal(CostRefusal.NotRun, refund.Refund.Refusal);
        Assert.Contains("stack costs are off", refund.Refund.Reason);
        Assert.Single(f.Events.OfType<TamingCanceledEvent>());
    }

    [Fact]
    public void A_refund_that_throws_in_the_tick_does_not_stop_the_other_attempts_expiring()
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = TamingRefundPolicy.Escrow, InactivitySeconds = 10 });
        var a = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        var b = f.Begin(target: f.Dog, who: f.B).Progress!;
        f.Costs.RefundThrow = new InvalidOperationException("not on the tick thread");
        f.Pass(11);
        Assert.Equal(TamingState.Expired, f.Engine.Inspect(a.Id)!.State);
        Assert.Equal(TamingState.Expired, f.Engine.Inspect(b.Id)!.State);
        Assert.Equal(2, f.Events.OfType<TamingExpiredEvent>().Count());
    }
}
