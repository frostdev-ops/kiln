// SPDX-License-Identifier: MIT
// A kind's plan whose blocks have parts at content ready (its rules, programs that name the game's values, checks, its definitions) is
// Applied only once those are done (16d review R1): until then it is PendingContent and nothing of it is published; a part that fails
// refuses it and takes back what it registered, and a refusal that cannot be taken back says what stays. Through the fake ready host.
using Xunit;

namespace Kiln.BlockTests;

[Collection("block kinds")]
public sealed class KindSettleTests
{
    private const string Missing = "0xdeadbeef";

    private static KindApplication Submit(ModBlocks target, KindOwner consumer, BlockPlan plan) =>
        KindApply.Submit(target, consumer, plan, stage => KindDefine.HandleOf(target.Id, plan, stage));

    /// <summary>A consumable whose effect applies a game status by name: its program is finished at content ready (a late program).</summary>
    private static (BlockPlan Plan, KindOwner Consumer, ModBlocks Target) Potion(string mod)
    {
        var (entry, kind, _) = KindKit.Lantern();   // once per test: KindWorld cleared the registry
        kind.Body = (c, b) => c.Plan("p", [c.Item("p", new ItemBlock
        {
            Name = b.Name, Kind = ItemKind.Consumable,
            OnUse = [new EffectBlock { On = EffectOn.Caster, Applies = (StatusRef)Missing }],
        })]);
        var consumer = KindKit.Owner(mod);
        var plan = KindCompiler.Compile(consumer, entry, "potion", new LanternBlock { Name = "Potion" }, KindApply.Provisional(consumer));
        return (plan, consumer, KindKit.Mod(mod));
    }

    private static (BlockPlan Plan, KindOwner Consumer, ModBlocks Target) Camp(string mod, KindEntry? entry = null)
    {
        entry ??= KindKit.Lantern().Entry;   // once per test: KindWorld cleared the registry
        var consumer = KindKit.Owner(mod);
        var plan = KindCompiler.Compile(consumer, entry, "camp", new LanternBlock { Name = "Camp", Seconds = 600 }, KindApply.Provisional(consumer));
        return (plan, consumer, KindKit.Mod(mod));
    }

    [Fact]
    public void A_plan_is_applied_only_after_its_late_program_was_finished_and_its_definitions_checked()
    {
        using var w = new KindWorld();
        w.Ready.Deferred = true;
        var (plan, consumer, target) = Potion("settle-ok");
        var app = Submit(target, consumer, plan);
        // registered under its owner, and waiting: nothing published, no change yet
        var owner = Assert.Single(w.Created);
        Assert.Equal(BlockApplyState.PendingContent, app.State);
        Assert.False(app.Result.Ok);
        Assert.StartsWith("PendingContent: registered; waiting for its parts at content ready", app.Result.Reason);
        Assert.False(target.Handles.ContainsKey(plan.Name));
        Assert.Empty(target.Programs);
        Assert.NotEmpty(app.Stage.Ledger.LatePending);
        Assert.Empty(w.Changes);
        Assert.Equal(app.Result, KindApply.Of(plan));
        KindApply.PumpAll();   // the tick never commits it again
        Assert.Single(w.Created);

        // content is ready: the program is finished with the game's value and served under the plan's owner; still waiting for the check
        w.Ready.RunReady();
        var (programOwner, program) = Assert.Single(w.Ready.Programs);
        Assert.Equal(owner, programOwner);
        Assert.Empty(app.Stage.Ledger.LatePending);
        Assert.Equal(BlockApplyState.PendingContent, app.State);
        Assert.Contains(owner, w.Ready.RuleOwners);   // its item rules went in under its owner
        Assert.Empty(w.Changes);

        // the definitions are checked: applied, published with the finished program, one change
        w.Ready.RunLater();
        Assert.Equal(BlockApplyState.Applied, app.State);
        Assert.True(app.Result.Ok);
        Assert.Same(app.Handle, target.Handles[plan.Name]);
        Assert.True(target.Programs.ContainsKey(program));
        var change = Assert.Single(w.Changes);
        Assert.Equal((BlockApplyState.PendingContent, BlockApplyState.Applied), (change.Before.State, change.After.State));
        Assert.Empty(w.Revoked);
    }

    [Fact]
    public void A_late_program_that_names_a_missing_game_status_refuses_the_plan_and_takes_it_back()
    {
        using var w = new KindWorld();
        w.Ready.Missing.Add(Missing);
        var (plan, consumer, target) = Potion("settle-missing");
        var app = Submit(target, consumer, plan);
        var owner = Assert.Single(w.Created);
        Assert.Equal(BlockApplyState.Refused, app.State);
        Assert.StartsWith("ContentReadyFailed: block ", app.Result.Reason);
        Assert.Contains($"the game has no status \"{Missing}\"", app.Result.Reason);
        Assert.DoesNotContain("stay", app.Result.Reason);
        Assert.Equal([owner], w.Revoked);   // everything the plan registered comes off with its owner
        Assert.True(app.Stage.Ledger.Revoked);
        Assert.Empty(w.Ready.Programs);
        Assert.False(target.Handles.ContainsKey(plan.Name));
        Assert.Empty(target.Programs);
        var change = Assert.Single(w.Changes);
        Assert.Equal((BlockApplyState.PendingContent, BlockApplyState.Refused), (change.Before.State, change.After.State));
        Assert.Equal(change.After, KindApply.Of(plan));
    }

    [Fact]
    public void A_rule_the_game_refuses_at_content_ready_refuses_the_plan()
    {
        using var w = new KindWorld();
        w.Ready.RuleErrors = json => json.Contains("settle-rule.kind-camp.light", StringComparison.Ordinal)
            ? ["items settle-rule.kind-camp.light: the game has no field 'maxStack'"] : [];
        var (plan, consumer, target) = Camp("settle-rule");
        var app = Submit(target, consumer, plan);
        Assert.Equal(BlockApplyState.Refused, app.State);
        Assert.Equal("ContentReadyFailed: items settle-rule.kind-camp.light: the game has no field 'maxStack'", app.Result.Reason);
        Assert.Equal(w.Created, w.Revoked);
        Assert.False(target.Handles.ContainsKey(plan.Name));
        // the same plan again is the same refusal's request: it is compiled again to try once more, as any refused plan
        Assert.Equal(BlockApplyState.Refused, KindApply.Of(plan).State);
    }

    [Fact]
    public void A_definition_kiln_content_could_not_take_refuses_the_plan_and_a_warning_does_not()
    {
        using var w = new KindWorld();
        w.Ready.Deferred = true;
        var (plan, consumer, target) = Camp("settle-def");
        var app = Submit(target, consumer, plan);
        w.Ready.RunReady();
        var item = Assert.Single(app.Stage.Ledger.PlanDefinitions!);
        w.Ready.Items.Add(new ContentDefinition(ContentKind.Items, Guid.NewGuid(), 1, item, "failed", [], "owner", "the clone's base is not an item") { Mod = "settle-def" });
        w.Ready.Items.Add(new ContentDefinition(ContentKind.Items, Guid.NewGuid(), 2, "settle-def.authored", "failed", [], "owner", "not the plan's") { Mod = "settle-def" });
        w.Ready.RunLater();
        Assert.Equal(BlockApplyState.Refused, app.State);
        Assert.Equal($"ContentReadyFailed: {item}: the clone's base is not an item", app.Result.Reason);   // the mod's own definition is not the plan's

        var (plan2, consumer2, target2) = Camp("settle-warn", KindRegistry.FindId("provider:lantern"));
        var app2 = Submit(target2, consumer2, plan2);
        w.Ready.RunReady();
        var item2 = Assert.Single(app2.Stage.Ledger.PlanDefinitions!);
        w.Ready.Items.Add(new ContentDefinition(ContentKind.Items, Guid.NewGuid(), 3, item2, "registered", [], "owner", null) { Mod = "settle-warn", Warnings = ["a field was rounded"] });
        w.Ready.RunLater();
        Assert.Equal(BlockApplyState.Applied, app2.State);
        Assert.Contains($"{item2}: a field was rounded", app2.Stage.Ledger.Warnings);
    }

    [Fact]
    public void A_refusal_whose_owner_cannot_be_revoked_says_what_stays_and_the_unload_tries_again()
    {
        using var w = new KindWorld();
        var fails = true;
        KindApply.RevokeOwner = owner =>
        {
            if (fails) throw new InvalidOperationException("the registry is busy");
            w.Revoked.Add(owner);
        };
        w.Ready.Missing.Add(Missing);
        var (plan, consumer, target) = Potion("settle-stays");
        var app = Submit(target, consumer, plan);
        Assert.Equal(BlockApplyState.Refused, app.State);
        Assert.EndsWith("; its registrations could not be taken back (the registry is busy): they stay until the mod unloads", app.Result.Reason);
        Assert.True(app.LeftOver);
        fails = false;
        KindApply.RetireConsumer(consumer.Token);
        Assert.Equal(w.Created, w.Revoked);
    }

    [Fact]
    public void A_plan_replaced_or_unloaded_while_it_waits_is_taken_back_and_never_applied()
    {
        using var w = new KindWorld();
        w.Ready.Deferred = true;
        var (entry, _, _) = KindKit.Lantern();
        var consumer = KindKit.Owner("settle-gone");
        var target = KindKit.Mod("settle-gone");
        var first = KindCompiler.Compile(consumer, entry, "camp", new LanternBlock { Name = "Camp", Seconds = 600 }, KindApply.Provisional(consumer));
        var app = Submit(target, consumer, first);
        var owner = Assert.Single(w.Created);
        // another compile of the same block replaces the waiting one: its registrations come off, it is Removed
        var second = KindCompiler.Compile(consumer, entry, "camp", new LanternBlock { Name = "Camp", Seconds = 300 }, KindApply.Provisional(consumer));
        var next = Submit(target, consumer, second);
        Assert.Equal(BlockApplyState.Removed, app.State);
        Assert.Contains(owner, w.Revoked);
        Assert.True(app.Stage.Ledger.Revoked);
        Assert.Equal(BlockApplyState.PendingContent, next.State);
        // the unload takes the second one back too, and a pass that comes after it changes nothing
        KindApply.RetireConsumer(consumer.Token);
        Assert.Equal(w.Created, w.Revoked);
        Assert.Equal(BlockApplyState.Removed, next.State);
        w.Ready.RunReady();
        w.Ready.RunLater();
        Assert.Equal(BlockApplyState.Removed, next.State);
        Assert.False(target.Handles.ContainsKey("camp"));
    }

    [Fact]
    public void A_pass_that_throws_refuses_the_waiting_plan_instead_of_leaving_it_pending()
    {
        using var w = new KindWorld();
        w.Ready.RuleThrows = _ => new InvalidOperationException("the store is gone");
        var (plan, consumer, target) = Camp("settle-throws");
        var app = Submit(target, consumer, plan);   // the fake runs the pass inside Submit, and logs what it throws as the game does
        Assert.Single(w.Ready.Thrown);
        var r = app.Result;
        Assert.Equal(BlockApplyState.Refused, r.State);
        Assert.Equal("ContentReadyFailed: its pass at content ready failed: the store is gone", r.Reason);
        Assert.Equal(w.Created, w.Revoked);
    }
}
