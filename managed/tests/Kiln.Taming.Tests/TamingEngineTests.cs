// SPDX-License-Identifier: MIT
// The attempt machine offline (dossier 19e section 6, the offline acceptance list): exact selections, the replay cache and
// challenges, before-event cancellation and event order, automatic finalization with a deferred commit and its cost-free
// retry, timeouts in simulation time with in-flight costs settled first, closed attempts and limits; and what this lane
// adds: the refund policies (non-refundable, refund on fail, escrow), a bind that settles at a later step, a world change.
using Kiln;
using Xunit;
using static Kiln.TamingTests.TamingFixture;

namespace Kiln.TamingTests;

public sealed class TamingEngineTests
{
    private static TamingDefinition FeedTwice() => Def(new FeedStep(Bait, 2), new FeedStep(Bait, 1));

    // ---------------------------------------------------------------------------------------- 1. selections ------

    [Fact]
    public void Only_the_selected_slot_pays_and_a_selection_of_another_owner_or_item_refuses_before_any_cost()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        Assert.StartsWith("StaleSelection", f.Advance(p, f.Stack(Other)).Reason);
        Assert.StartsWith("StaleSelection", f.Advance(p, f.Stack(Bait, owner: f.B)).Reason);
        Assert.StartsWith("StaleSelection", f.Advance(p, f.Stack(Bait, count: 1)).Reason);  // holds fewer than the step's 2
        Assert.Empty(f.Costs.Calls);
        var sel = f.Stack(Bait, slot: 6);
        var r = f.Advance(p, sel);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(("take", sel, 2), (f.Costs.Calls[0].Kind, f.Costs.Calls[0].Selection, f.Costs.Calls[0].Amount));
    }

    [Fact]
    public void A_selection_of_the_wrong_shape_throws()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1), new FeedStep(Bait), new UseToolStep(Whistle)));
        var p = f.Begin().Progress!;
        Assert.Throws<ArgumentException>(() => { _ = f.Engine.Advance(f.A, p.Id, Token(p), f.Stack(Bait)); });   // weaken: none
        p = f.Advance(p).Progress!;
        Assert.Throws<ArgumentException>(() => { _ = f.Engine.Advance(f.A, p.Id, Token(p), null); });            // feed: a stack
        Assert.Throws<ArgumentException>(() => { _ = f.Engine.Advance(f.A, p.Id, Token(p), f.Gear(Bait)); });
        p = f.Advance(p, f.Stack(Bait)).Progress!;
        Assert.Throws<ArgumentException>(() => { _ = f.Engine.Advance(f.A, p.Id, Token(p), f.Stack(Whistle)); });  // tool: gear
    }

    // ---------------------------------------------------------------------------------------- 2. replay ----------

    [Fact]
    public void The_same_request_is_the_same_task_or_result_and_one_debit()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        var held = f.Costs.Hold = new TaskCompletionSource<CostReceipt>();
        var token = Token(p);
        var sel = f.Stack(Bait);
        var first = f.Engine.Advance(f.A, p.Id, token, sel);
        Assert.Same(first, f.Engine.Advance(f.A, p.Id, token, sel));   // while it runs
        held.SetResult(new CostReceipt(Bait, 2, 2, true) { RequestId = token.RequestId });
        f.Tick();
        Assert.True(first.Result.Ok);
        Assert.Same(first, f.Engine.Advance(f.A, p.Id, token, sel));   // after
        Assert.Single(f.Costs.Calls);
        Assert.Single(f.Events.OfType<TamingStepChangedEvent>());
        Assert.StartsWith("RequestConflict", Done(f.Engine.Advance(f.A, p.Id, token, f.Stack(Bait, slot: 3))).Reason);
    }

    [Fact]
    public void A_completed_tool_token_never_advances_the_identical_next_tool_step()
    {
        var f = new TamingFixture().With(Def(new UseToolStep(Whistle), new UseToolStep(Whistle), new WeakenStep(1)));
        var p = f.Begin().Progress!;
        var tool = f.Gear(Whistle);
        var old = Token(p);
        var after = Done(f.Engine.Advance(f.A, p.Id, old, tool)).Progress!;
        Assert.Equal(1, after.CompletedSteps);
        // the old token with a new request id: StaleStep (the challenge rotated)
        Assert.StartsWith("StaleStep", Done(f.Engine.Advance(f.A, p.Id, old with { RequestId = Guid.NewGuid() }, tool)).Reason);
        Assert.Single(f.Costs.Calls);
        var next = f.Advance(after, tool);
        Assert.True(next.Ok, next.Reason);
        Assert.Equal(2, next.Progress!.CompletedSteps);
        Assert.Equal(2, f.Costs.Calls.Count);
        Assert.NotEqual(after.StepChallenge, next.Progress.StepChallenge);
    }

    [Fact]
    public void A_refused_cost_spends_nothing_and_leaves_the_token_valid()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        f.Costs.Answer = (s, n, id) => new CostReceipt(s.ExpectedItem, n, 0, false) { RequestId = id, Refusal = CostRefusal.StaleSelection, Reason = "moved" };
        var r = f.Advance(p, f.Stack(Bait));
        Assert.False(r.Ok);
        Assert.False(r.StepApplied);
        Assert.StartsWith("Refused: StaleSelection", r.Reason);
        Assert.Equal((p.Revision, p.StepChallenge, 0), (r.Progress!.Revision, r.Progress.StepChallenge, r.Progress.CompletedSteps));
        f.Costs.Answer = null;
        Assert.True(f.Advance(p, f.Stack(Bait)).Ok);   // the same token, a new request
    }

    [Fact]
    public void An_adapter_that_is_off_throws_its_row_and_leaves_the_attempt_as_it_was()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        f.Costs.Throw = new KilnUnavailableException("items.costs.exact", "off");
        var token = Token(p);
        Assert.Equal("items.costs.exact", Assert.Throws<KilnUnavailableException>(() => { _ = f.Engine.Advance(f.A, p.Id, token, f.Stack(Bait)); }).Capability);
        Assert.Equal(TamingState.Active, f.Engine.Inspect(p.Id)!.State);
        f.Costs.Throw = null;
        Assert.True(Done(f.Engine.Advance(f.A, p.Id, token, f.Stack(Bait))).Ok);   // the same request, once the row is back
    }

    // ---------------------------------------------------------------------------------------- 3. before-events ---

    [Fact]
    public void A_canceled_begin_reserves_nothing()
    {
        var f = new TamingFixture().With(FeedTwice());
        f.Refuse = e => e is TamingBeginningEvent;
        var r = f.Begin();
        Assert.StartsWith("RefusedByMod", r.Reason);   // a mod's veto, not a canceled attempt
        Assert.DoesNotContain("reserve", f.Binder.Calls);
        f.Refuse = _ => false;
        Assert.True(f.Begin(who: f.B).Ok);   // the target is not claimed
    }

    [Fact]
    public void A_canceled_advance_spends_nothing_and_keeps_the_revision_and_challenge()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        f.Refuse = e => e is TamingAdvancingEvent;
        var r = f.Advance(p, f.Stack(Bait));
        Assert.StartsWith("RefusedByMod", r.Reason);   // a mod's veto, not a canceled attempt
        Assert.Equal((p.Revision, p.StepChallenge, TamingState.Active), (r.Progress!.Revision, r.Progress.StepChallenge, r.Progress.State));
        Assert.Empty(f.Costs.Calls);
    }

    [Fact]
    public void A_canceled_final_tame_takes_no_last_cost_and_keeps_the_earlier_ones()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        f.Refuse = e => e is TamingCommittingEvent;
        var r = f.Advance(p, f.Stack(Bait));
        Assert.StartsWith("RefusedByMod", r.Reason);   // a mod's veto, not a canceled attempt
        Assert.Single(f.Costs.Calls);
        Assert.Single(r.Progress!.Spent);
        Assert.Equal(TamingState.Active, r.Progress.State);
    }

    [Fact]
    public void The_events_come_in_19e_order()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1), new FeedStep(Bait)));
        var p = f.Begin().Progress!;
        p = f.Advance(p).Progress!;
        var r = f.Advance(p, f.Stack(Bait));
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(TamingState.Committed, r.Progress!.State);
        Assert.Equal(["Beginning", "Started", "Advancing", "StepChanged", "Advancing", "Committing", "StepChanged", "Tamed", "Completed"], f.Names());
        Assert.Equal(["reserve", "preflight", "bind", "release", "committed"], f.Binder.Calls);
    }

    // ---------------------------------------------------------------------------------------- 4. finalization -----

    [Fact]
    public void A_deferred_tame_keeps_everything_and_its_retry_is_cost_free()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        f.Binder.Result = new TamingBindResult(false, null, "Capacity: the roster is full", true);
        var r = f.Advance(p, f.Stack(Bait));
        Assert.False(r.Ok);
        Assert.True(r.StepApplied);
        Assert.StartsWith("CommitDeferred", r.Reason);
        Assert.Equal((TamingState.AwaitingCommit, 2, 2), (r.Progress!.State, r.Progress.CompletedSteps, r.Progress.Spent.Count));
        Assert.StartsWith("Busy", f.Begin(who: f.B).Reason);   // still claimed
        Assert.StartsWith("WrongState", f.Advance(r.Progress, f.Stack(Bait)).Reason);
        f.Binder.Result = new TamingBindResult(true, null, null, false);
        var retry = Done(f.Engine.Retry(f.A, p.Id, Token(r.Progress)));
        Assert.True(retry.Ok, retry.Reason);
        Assert.False(retry.StepApplied);
        Assert.Equal(2, f.Costs.Calls.Count);                  // no new cost
        Assert.Single(f.Events.OfType<TamedEvent>());
        Assert.True(f.Events.OfType<TamingCommittingEvent>().Last().Context.CostFreeRetry);
    }

    [Fact]
    public void A_bind_that_settles_later_holds_the_attempt_busy_until_it_does()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1)));
        var held = f.Binder.Hold = new TaskCompletionSource<TamingBindResult>();
        var p = f.Begin().Progress!;
        var t = f.Engine.Advance(f.A, p.Id, Token(p), null);
        Assert.False(t.IsCompleted);
        Assert.Equal(TamingState.CostPending, f.Engine.Inspect(p.Id)!.State);
        var cancel = f.Engine.Cancel(f.A, p.Id, Token(f.Engine.Inspect(p.Id)!));
        f.Tick();
        Assert.False(t.IsCompleted);
        held.SetResult(new TamingBindResult(true, null, null, false));
        f.Tick();
        Assert.True(t.Result.Ok, t.Result.Reason);
        Assert.Equal(TamingState.Committed, t.Result.Progress!.State);
        Assert.StartsWith("AttemptClosed", cancel.Result.Reason);   // the tame came first: it is never undone
        Assert.Single(f.Events.OfType<TamedEvent>());
    }

    [Fact]
    public void A_bind_refused_for_good_fails_the_attempt_and_releases_it()
    {
        var f = new TamingFixture().With(Def(new FeedStep(Bait)));
        f.Binder.Result = new TamingBindResult(false, null, "DefinitionUnknown: no pet", false);
        var r = f.Advance(f.Begin().Progress!, f.Stack(Bait));
        Assert.Equal(TamingState.Failed, r.Progress!.State);
        Assert.True(r.StepApplied);
        Assert.Single(r.Receipts);
        Assert.Contains("release", f.Binder.Calls);
        Assert.True(f.Begin(who: f.B).Ok);   // the target is free again
    }

    [Fact]
    public void Cancel_and_failure_report_spent_receipts_and_invent_no_refund_by_default()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        var c = Done(f.Engine.Cancel(f.A, p.Id, Token(p)));
        Assert.True(c.Ok);
        Assert.Equal(TamingState.Canceled, c.Progress!.State);
        Assert.Single(c.Receipts);
        Assert.Empty(f.Costs.Refunds);
        Assert.Empty(c.Progress.Refunds);
        Assert.Single(f.Events.OfType<TamingCanceledEvent>());
    }

    // ---------------------------------------------------------------------------------------- 5. timeouts ---------

    [Fact]
    public void Only_an_applied_step_refreshes_the_inactivity_timeout()
    {
        var f = new TamingFixture().With(FeedTwice() with { InactivitySeconds = 10, MaxAttemptSeconds = 100 });
        var p = f.Begin().Progress!;
        f.Pass(8);
        Assert.StartsWith("StaleStep", f.Advance(p with { StepChallenge = Guid.NewGuid() }, f.Stack(Bait)).Reason);   // refused
        f.Engine.Inspect(p.Id);
        f.Pass(3);
        Assert.Equal(TamingState.Expired, f.Engine.Inspect(p.Id)!.State);
        Assert.Equal("InactivitySeconds", ((TamingExpiredEvent)f.Events.Last()).Cause);

        var g = new TamingFixture().With(FeedTwice() with { InactivitySeconds = 10, MaxAttemptSeconds = 100 });
        var q = g.Begin().Progress!;
        g.Pass(8);
        q = g.Advance(q, g.Stack(Bait)).Progress!;
        g.Pass(8);
        Assert.Equal(TamingState.Active, g.Engine.Inspect(q.Id)!.State);
    }

    [Fact]
    public void The_earlier_deadline_expires_an_awaiting_tame_too()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1)) with { InactivitySeconds = 30, MaxAttemptSeconds = 40 });
        f.Binder.Result = new TamingBindResult(false, null, "Capacity", true);
        var p = f.Begin().Progress!;
        f.Pass(20);
        var r = f.Advance(p);
        Assert.Equal(TamingState.AwaitingCommit, r.Progress!.State);
        f.Pass(21);   // 41 s since Begin, 21 since the step: the lifetime ends first
        Assert.Equal(TamingState.Expired, f.Engine.Inspect(p.Id)!.State);
        Assert.Equal("MaxAttemptSeconds", ((TamingExpiredEvent)f.Events.Last()).Cause);
        Assert.Contains("release", f.Binder.Calls);
    }

    [Fact]
    public void A_timeout_during_a_cost_waits_for_its_receipt()
    {
        var f = new TamingFixture().With(FeedTwice() with { InactivitySeconds = 5, MaxAttemptSeconds = 5 });
        var p = f.Begin().Progress!;
        var held = f.Costs.Hold = new TaskCompletionSource<CostReceipt>();
        var t = f.Engine.Advance(f.A, p.Id, Token(p), f.Stack(Bait));
        f.Pass(10);
        Assert.Equal(TamingState.CostPending, f.Engine.Inspect(p.Id)!.State);   // never assumed away
        held.SetResult(new CostReceipt(Bait, 2, 2, true));
        f.Tick();
        Assert.True(t.Result.StepApplied);
        Assert.StartsWith("Expired", t.Result.Reason);
        var after = f.Engine.Inspect(p.Id)!;
        Assert.Equal((TamingState.Expired, 1), (after.State, after.Spent.Count));
    }

    [Fact]
    public void An_uncertain_cost_fails_the_attempt_with_its_receipt_and_is_never_refunded()
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = TamingRefundPolicy.Escrow });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        var first = p.Spent.Single();
        f.Costs.Answer = (s, n, id) => new CostReceipt(s.ExpectedItem, n, n, false) { RequestId = id, Uncertain = true };
        var r = f.Advance(p, f.Stack(Bait));
        Assert.StartsWith("UncertainCommit", r.Reason);
        Assert.Equal(TamingState.Failed, r.Progress!.State);
        Assert.Equal(2, r.Progress.Spent.Count);
        // escrow gives back the step that was surely paid; the uncertain one is never given back automatically
        Assert.Same(first, Assert.Single(f.Costs.Refunds).Paid);
        f.Advance(r.Progress, f.Stack(Bait));
        Assert.Equal(2, f.Costs.Calls.Count);   // and never retried
    }

    [Fact]
    public void A_cancel_during_a_cost_settles_it_first()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        var held = f.Costs.Hold = new TaskCompletionSource<CostReceipt>();
        var step = f.Engine.Advance(f.A, p.Id, Token(p), f.Stack(Bait));
        var cancel = f.Engine.Cancel(f.A, p.Id, Token(p));
        Assert.False(cancel.IsCompleted);
        held.SetResult(new CostReceipt(Bait, 2, 2, true));
        f.Tick();
        Assert.Equal("Canceled: CostCommitted", cancel.Result.Reason);
        Assert.Single(cancel.Result.Receipts);
        Assert.True(step.Result.StepApplied);
    }

    // ---------------------------------------------------------------------------------------- 6. closed, limits ---

    [Fact]
    public void A_closed_attempt_answers_its_replays_then_stays_closed_forever()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1)));
        var p = f.Begin().Progress!;
        var token = Token(p);
        var done = Done(f.Engine.Advance(f.A, p.Id, token, null));
        Assert.True(done.Ok);
        f.Pass(300);
        Assert.Same(done, Done(f.Engine.Advance(f.A, p.Id, token, null)));
        f.Pass(301);
        Assert.Null(f.Engine.Inspect(p.Id));
        Assert.StartsWith("AttemptClosed", Done(f.Engine.Advance(f.A, p.Id, token, null)).Reason);
        Assert.StartsWith("AttemptClosed", Done(f.Engine.Advance(f.A, p.Id, Token(p), null)).Reason);
    }

    [Fact]
    public void Another_player_or_session_or_world_is_refused()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        Assert.StartsWith("NotOwner", f.Advance(p, f.Stack(Bait, owner: f.B), who: f.B).Reason);
        f.World.Owners[f.A] = f.World.Owners[f.A] with { Session = 99 };
        Assert.StartsWith("StaleSession", f.Advance(p, f.Stack(Bait)).Reason);
        Assert.Empty(f.Costs.Calls);
        f.Tick();
        Assert.Equal(TamingState.Failed, f.Engine.Inspect(p.Id)!.State);   // the owner's session ended: OwnerLeft
    }

    [Fact]
    public void Ten_requests_a_second_and_a_limited_begin_is_not_its_answer()
    {
        var f = new TamingFixture().With(FeedTwice());
        for (var i = 0; i < 10; i++) f.Begin(target: new EntityRef(new EntityId(7000 + (uint)i), 1));   // unknown targets: refused, counted
        var id = Guid.NewGuid();
        Assert.StartsWith("TooManyRequests", f.Begin(id: id).Reason);
        f.Pass(1.5);
        Assert.True(f.Begin(id: id).Ok);   // the same id, a second later
    }

    [Fact]
    public void One_claimant_per_target()
    {
        var f = new TamingFixture().With(FeedTwice());
        Assert.True(f.Begin().Ok);
        Assert.StartsWith("Busy", f.Begin(who: f.B).Reason);
    }

    // ---------------------------------------------------------------------------------------- refunds -------------

    [Theory]
    [InlineData(TamingRefundPolicy.NonRefundable, "cancel", 0)]
    [InlineData(TamingRefundPolicy.NonRefundable, "fail", 0)]
    [InlineData(TamingRefundPolicy.RefundOnFail, "cancel", 0)]
    [InlineData(TamingRefundPolicy.RefundOnFail, "fail", 1)]
    [InlineData(TamingRefundPolicy.RefundOnFail, "expire", 0)]
    [InlineData(TamingRefundPolicy.Escrow, "cancel", 1)]
    [InlineData(TamingRefundPolicy.Escrow, "fail", 1)]
    [InlineData(TamingRefundPolicy.Escrow, "expire", 1)]
    public void The_definition_s_refund_policy_decides_what_an_end_gives_back(TamingRefundPolicy policy, string end, int refunds)
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = policy, InactivitySeconds = 10 });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        var paid = p.Spent.Single();
        switch (end)
        {
            case "cancel": Done(f.Engine.Cancel(f.A, p.Id, Token(p))); break;
            case "fail": f.World.Targets.Remove(f.Wolf.Id); f.Tick(); break;
            default: f.Pass(11); break;
        }
        Assert.Equal(refunds, f.Costs.Refunds.Count);
        var closed = f.Engine.Inspect(p.Id)!;
        Assert.Equal(refunds, closed.Refunds.Count);
        if (refunds == 1)
        {
            Assert.Equal(TamingEngine.RefundId(paid.RequestId), f.Costs.Refunds[0].Request);
            Assert.Same(paid, closed.Refunds[0].Paid);
            Assert.True(closed.Refunds[0].Refund!.Committed);
        }
    }

    [Fact]
    public void A_tame_refunds_nothing_even_in_escrow()
    {
        var f = new TamingFixture().With(FeedTwice() with { Refund = TamingRefundPolicy.Escrow });
        var p = f.Advance(f.Begin().Progress!, f.Stack(Bait)).Progress!;
        Assert.True(f.Advance(p, f.Stack(Bait)).Ok);
        Assert.Empty(f.Costs.Refunds);
    }

    // ---------------------------------------------------------------------------------------- world, definitions --

    [Fact]
    public void A_new_world_fails_old_attempts_and_forgets_old_begins()
    {
        var f = new TamingFixture().With(FeedTwice());
        var id = Guid.NewGuid();
        var p = f.Begin(id: id).Progress!;
        f.World.WorldEpoch = 2;
        f.Tick();
        Assert.Equal(TamingState.Failed, f.Engine.Inspect(p.Id)!.State);
        Assert.Equal("WorldEnded", f.Events.OfType<TamingFailedEvent>().Single().Cause);
        var again = f.Begin(id: id);
        Assert.True(again.Ok);
        Assert.NotEqual(p.Id, again.Progress!.Id);
    }

    [Fact]
    public void Definitions_are_validated_and_a_running_one_is_not_replaced()
    {
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def()));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new FeedStep(Bait, 1000))));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(0))));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new UseToolStep(Whistle, -1))));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new NativeStep(0))));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(1)) with { RangeMeters = 9 }));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(1)) with { InactivitySeconds = 60, MaxAttemptSeconds = 30 }));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(1)) with { Creatures = [] }));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(1)) with { Pet = "NoPrefix" }));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(new WeakenStep(1)) with { Id = "Wolf" }));
        Assert.Throws<ArgumentException>(() => TamingEngine.Validate(Def(Enumerable.Repeat<TamingStep>(new WeakenStep(1), 65).ToArray())));

        var f = new TamingFixture().With(FeedTwice());
        f.Begin();
        f.Engine.Define(FeedTwice(), "mod-a");   // the same by value: fine
        Assert.Throws<InvalidOperationException>(() => f.Engine.Define(Def(new FeedStep(Bait, 3)), "mod-a"));
        Assert.Throws<InvalidOperationException>(() => f.Engine.Define(FeedTwice(), "mod-b"));
    }

    [Fact]
    public void An_unloaded_mod_s_attempts_fail_DefinitionGone()
    {
        var f = new TamingFixture().With(FeedTwice());
        var p = f.Begin().Progress!;
        f.Engine.RemoveMod("mod-a");
        Assert.Equal(TamingState.Failed, f.Engine.Inspect(p.Id)!.State);
        Assert.Null(f.Engine.Definition("mod-a:wolf"));
    }

    [Fact]
    public void Targets_match_by_registry_id_prefix_or_the_template_a_block_id_names()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(1)) with { Creatures = ["my-mod:pup", "game:animal_wildlife_dog_*"] });
        Assert.True(f.Begin(target: f.Dog).Ok);
        Assert.StartsWith("TemplateMismatch", f.Begin(who: f.B).Reason);   // the wolf
        var tpl = Guid.NewGuid();
        f.World.Targets[f.Wolf.Id] = f.World.Targets[f.Wolf.Id] with { Template = tpl };
        f.Engine.ResolveCreature = id => id == "my-mod:pup" ? tpl : null;
        Assert.True(f.Begin(who: f.B).Ok);
    }

    [Fact]
    public void A_native_step_waits_for_the_game_s_own_count()
    {
        var f = new TamingFixture().With(Def(new NativeStep(2)));
        Assert.StartsWith("TemplateMismatch", f.Begin().Reason);   // the wolf is not a tameable animal
        var p = f.Begin(target: f.Dog).Progress!;
        f.World.Targets[f.Dog.Id] = f.World.Targets[f.Dog.Id] with { NativeProgress = 1 };
        Assert.StartsWith("NativePending", f.Advance(p).Reason);
        f.World.Targets[f.Dog.Id] = f.World.Targets[f.Dog.Id] with { NativeProgress = 2 };
        var r = f.Advance(p);
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(TamingState.Committed, r.Progress!.State);
    }

    [Fact]
    public void Weaken_holds_its_threshold_until_the_tame()
    {
        var f = new TamingFixture().With(Def(new WeakenStep(0.5), new FeedStep(Bait)));
        var p = f.Begin().Progress!;
        Assert.StartsWith("Threshold", f.Advance(p).Reason);
        f.World.Targets[f.Wolf.Id] = f.World.Targets[f.Wolf.Id] with { HealthFraction = 0.4 };
        p = f.Advance(p).Progress!;
        f.World.Targets[f.Wolf.Id] = f.World.Targets[f.Wolf.Id] with { HealthFraction = 0.9 };   // it healed
        Assert.StartsWith("Threshold", f.Advance(p, f.Stack(Bait)).Reason);
        Assert.Empty(f.Costs.Calls);
    }

    [Fact]
    public void Choices_list_the_owner_s_matching_slots_only()
    {
        var f = new TamingFixture().With(FeedTwice());
        f.Costs.Inventory =
        [
            new InventorySlot(new SlotId(1), Bait, 1, EntityId.None, 0, null, default, 0, false, false, false, 0),   // too few
            new InventorySlot(new SlotId(2), Bait, 4, EntityId.None, 0, null, default, 0, false, false, false, 0),
            new InventorySlot(new SlotId(3), Other, 9, EntityId.None, 0, null, default, 0, false, false, false, 0),
            new InventorySlot(new SlotId(4), Bait, 2, EntityId.None, 0, null, default, 0, false, false, false, 0),
        ];
        var p = f.Begin().Progress!;
        var c = f.Engine.Choices(f.A, p.Id);
        Assert.True(c.Ok);
        Assert.Equal(new[] { new SlotId(2), new SlotId(4) }, c.Choices.Select(x => x.Selection.Slot).ToArray());
        Assert.Equal((p.Revision, p.StepChallenge), (c.Revision, c.StepChallenge));
        Assert.False(f.Engine.Choices(f.B, p.Id).Ok);
    }
}
