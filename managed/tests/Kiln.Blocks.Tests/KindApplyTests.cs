// SPDX-License-Identifier: MIT
// Applying a kind's plan (16d S4, S5): a plan is bound to the mod instance that compiled it, applies whole or not at all,
// answers the same when it is applied again, and leaves nothing behind when a registration is refused.
using Xunit;

namespace Kiln.BlockTests;

/// <summary>The game as the apply engine sees it, faked: content readiness, owners, capability rows, the tick thread.</summary>
internal sealed class KindWorld : IDisposable
{
    public ulong? Generation = 1;
    public readonly List<ulong> Created = [], Revoked = [];
    public readonly HashSet<string> Off = [];
    public readonly CountingSink Sink = new();
    public readonly List<KilnEvent> Events;
    private readonly (Func<ulong?>, Func<string, ulong>, Action<ulong>?, Func<string, bool>, Func<bool>, Func<bool>) _was;
    private ulong _next = 100;

    public KindWorld()
    {
        _was = (KindApply.ContentGeneration, KindApply.CreateOwner, KindApply.RevokeOwner, KindApply.CapabilityIs, KindApply.OnTickThread, KindCapabilities.BlocksAvailable);
        Events = KindKit.Fresh();
        KindApply.Reset();
        KindApply.ContentGeneration = () => Generation;
        KindApply.CreateOwner = _ => { Created.Add(++_next); return _next; };
        KindApply.RevokeOwner = Revoked.Add;
        KindApply.CapabilityIs = name => !Off.Contains(name);
        KindApply.OnTickThread = () => true;
        KindCapabilities.BlocksAvailable = () => true;
        KindCapabilities.Rearm();
    }

    public void Dispose()
    {
        (KindApply.ContentGeneration, KindApply.CreateOwner, KindApply.RevokeOwner, KindApply.CapabilityIs, KindApply.OnTickThread, KindCapabilities.BlocksAvailable) = _was;
        KindApply.Reset();
        KindCapabilities.Rearm();
        Sink.Dispose();
    }

    public IEnumerable<BlockApplicationChangedEvent> Changes => Events.OfType<BlockApplicationChangedEvent>();
}

[Collection("block kinds")]
public sealed class KindApplyTests
{
    private static KindApplication Submit(ModBlocks target, KindOwner consumer, BlockPlan plan) =>
        KindApply.Submit(target, consumer, plan, stage => KindDefine.HandleOf(target.Id, plan, stage));

    private static (KindEntry Entry, LanternKind Kind, KindOwner Provider, KindOwner Consumer, ModBlocks Target) Setup(string mod)
    {
        var (entry, kind, provider) = KindKit.Lantern();
        return (entry, kind, provider, KindKit.Owner(mod), KindKit.Mod(mod));
    }

    private static BlockPlan Camp(KindEntry entry, KindOwner consumer, double seconds = 600) =>
        KindCompiler.Compile(consumer, entry, "camp", new LanternBlock { Name = "Camp", Seconds = seconds }, KindApply.Provisional(consumer));

    [Fact]
    public void A_plan_applies_whole_under_an_owner_of_its_own_and_the_same_plan_again_does_nothing()
    {
        using var w = new KindWorld();
        var (entry, kind, _, consumer, target) = Setup("apply-once");
        var plan = Camp(entry, consumer);
        var app = Submit(target, consumer, plan);
        Assert.Equal(BlockApplyState.Applied, app.State);
        var result = app.Result;
        Assert.True(result.Ok);
        Assert.Equal(("", 1ul, 1ul, plan.RequestId), (result.Reason, result.Generation, result.ContentGeneration, result.RequestId));
        // every registration is the plan owner's, none the mod's (owner 1)
        var owner = Assert.Single(w.Created);
        Assert.NotEmpty(w.Sink.Writes);
        Assert.All(w.Sink.Writes, x => Assert.Contains($" {owner} ", x));
        Assert.Contains(w.Sink.Writes, x => x.StartsWith($"item {owner} apply-once.kind-camp.light"));
        Assert.Contains(w.Sink.Writes, x => x.StartsWith($"recipe {owner} "));
        // the block is the mod's under its own name, with the primary's identity and the author's record
        var handle = Assert.IsType<BlockHandle<LanternBlock>>(target.Handles["camp"]);
        Assert.Same(app.Handle, handle);
        Assert.Equal(("apply-once", "camp", "apply-once.kind-camp.light", "Camp"), (handle.Mod, handle.Name, handle.GameName, handle.Block.Name));
        Assert.Equal((result.Guid, result.GameName), (handle.Guid, handle.GameName));
        Assert.Equal(["camp"], target.Handles.Keys);          // the products are not blocks of their own
        Assert.Equal("provider:lantern", (string)target.Lowered["camp"]!["kind"]!);
        var change = Assert.Single(w.Changes);
        Assert.Equal(("apply-once", "camp", BlockApplyState.PendingContent, BlockApplyState.Applied), (change.ModId, change.Name, change.Before.State, change.After.State));
        Assert.Equal(result, KindApply.Of(plan));
        Assert.Equal(result, KindApply.Of(handle));

        // again: the same plan, and a plan compiled again from the same record
        var writes = w.Sink.Writes.Count;
        Assert.Equal(result, Submit(target, consumer, plan).Result with { });
        var twin = Camp(entry, consumer);
        Assert.NotEqual(plan.RequestId, twin.RequestId);
        Assert.Equal(result.Generation, Submit(target, consumer, twin).Result.Generation);
        Assert.Equal(writes, w.Sink.Writes.Count);
        Assert.Single(w.Created);
        Assert.Single(w.Changes);
        Assert.Empty(w.Revoked);
        Assert.Equal(2, kind.Calls);   // one per compile, none for an apply
    }

    [Fact]
    public void Another_plan_for_an_applied_name_is_refused_and_changes_nothing()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-changed");
        var first = Submit(target, consumer, Camp(entry, consumer));
        var writes = w.Sink.Writes.Count;
        var other = Submit(target, consumer, Camp(entry, consumer, seconds: 900));
        Assert.Equal(BlockApplyState.Refused, other.State);
        Assert.StartsWith("NameAlreadyApplied", other.Result.Reason);
        Assert.False(other.Result.Ok);
        Assert.Equal(writes, w.Sink.Writes.Count);
        Assert.Same(first.Handle, target.Handles["camp"]);
        Assert.Equal(BlockApplyState.Applied, KindApply.Of(first.Plan).State);
        Assert.Single(w.Changes);
    }

    [Fact]
    public void Only_the_instance_that_compiled_a_plan_applies_it()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-binding");
        var plan = Camp(entry, consumer);
        // another mod; the same id loaded again; an instance that is unloading
        Assert.Throws<ArgumentException>(() => Submit(KindKit.Mod("thief"), KindKit.Owner("thief"), plan));
        Assert.Contains("earlier load", Assert.Throws<ArgumentException>(() => Submit(target, KindKit.Owner("apply-binding"), plan)).Message);
        var dying = new KindOwner("apply-dying", new object(), () => false);
        Assert.Throws<ArgumentException>(() => Submit(KindKit.Mod("apply-dying"), dying, KindCompiler.Compile(dying, entry, "camp", new LanternBlock { Name = "Camp" })));
        Assert.Empty(w.Sink.Writes);
        Assert.Empty(w.Created);
        Assert.Empty(target.Handles);
        Assert.Equal(BlockApplyState.Removed, KindApply.Of(plan).State);   // never submitted
    }

    [Fact]
    public void A_plan_waits_for_content_with_a_provisional_handle_and_is_applied_when_it_is_ready()
    {
        using var w = new KindWorld { Generation = null };
        var (entry, kind, _, consumer, target) = Setup("apply-pending");
        var app = Submit(target, consumer, Camp(entry, consumer));
        Assert.Equal(BlockApplyState.PendingContent, app.State);
        Assert.Equal((false, "PendingContent", 0ul), (app.Result.Ok, app.Result.Reason, app.Result.Generation));
        // the identity is known, and nothing is published or written
        Assert.Equal("apply-pending.kind-camp.light", app.Handle.GameName);
        Assert.Empty(target.Handles);
        Assert.Empty(w.Sink.Writes);
        Assert.Empty(w.Created);
        // a later compile of the same mod may name it
        ResolvedBlock<LanternBlock>? seen = null;
        kind.Body = (c, b) => { seen = c.Resolve(new BlockRef<LanternBlock>("camp")); return LanternKind.Default(c, b); };
        KindCompiler.Compile(consumer, entry, "other", new LanternBlock { Name = "Other" }, KindApply.Provisional(consumer));
        Assert.Equal((app.Handle.Guid, "provider:lantern"), (seen!.Guid, seen.KindId));
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.PendingContent, app.State);

        w.Generation = 7;
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.Applied, app.State);
        Assert.Equal(7ul, app.Result.ContentGeneration);
        Assert.Same(app.Handle, target.Handles["camp"]);
        Assert.Single(w.Changes);
        KindApply.PumpAll();
        Assert.Single(w.Created);
    }

    [Fact]
    public void A_retired_provider_refuses_what_is_not_applied_and_keeps_what_is()
    {
        using var w = new KindWorld();
        var (entry, kind, provider, consumer, target) = Setup("apply-provider");
        var applied = Submit(target, consumer, Camp(entry, consumer));
        w.Generation = null;
        var waiting = Submit(target, consumer, KindCompiler.Compile(consumer, entry, "late", new LanternBlock { Name = "Late", Seconds = 60 }));
        var calls = kind.Calls;
        KindRegistry.Dispose(entry, provider);
        w.Generation = 2;
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.Refused, waiting.State);
        Assert.StartsWith("ProviderRetired", waiting.Result.Reason);
        Assert.False(target.Handles.ContainsKey("late"));
        // what was applied stays the consumer's, and the provider is never asked again
        Assert.Equal(BlockApplyState.Applied, applied.State);
        Assert.Same(applied.Handle, target.Handles["camp"]);
        Assert.Empty(w.Revoked);
        Assert.Single(w.Created);
        Assert.Equal(calls, kind.Calls);
    }

    [Fact]
    public void Content_that_changes_under_an_attempt_takes_it_back_and_the_plan_is_tried_again()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-stale");
        var bumped = false;
        w.Sink.OnWrite = _ => { if (!bumped) { bumped = true; w.Generation = 2; } };
        var app = Submit(target, consumer, Camp(entry, consumer));
        // the first attempt registered under an owner, saw the content change, and was revoked whole
        Assert.Equal(BlockApplyState.PendingContent, app.State);
        Assert.Equal(w.Created, w.Revoked);
        Assert.Single(w.Revoked);
        Assert.Empty(target.Handles);
        Assert.Empty(w.Changes);
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.Applied, app.State);
        Assert.Equal(2ul, app.Result.ContentGeneration);
        Assert.Equal(2, w.Created.Count);
        Assert.Single(w.Revoked);
        Assert.NotEqual(w.Revoked[0], app.Owner);
    }

    [Fact]
    public void What_the_kind_requires_and_asks_of_clients_is_checked_before_anything_is_written()
    {
        using var w = new KindWorld();
        KindRegistry.Reset();
        var provider = KindKit.Owner("provider");
        Assert.True(KindRegistry.Register(provider, KindKit.Descriptor() with { Requires = ["items.give"], Clients = ClientsLevel.Mod }, new LanternKind()).Ok);
        var entry = KindRegistry.FindId("provider:lantern")!;
        var consumer = KindKit.Owner("apply-requires");
        w.Off.Add("items.give");
        var off = Submit(KindKit.Mod("apply-requires"), consumer, Camp(entry, consumer));
        Assert.StartsWith("RequiresOff", off.Result.Reason);
        w.Off.Clear();
        var low = new ModBlocks("apply-clients", 1) { ManifestClients = "kiln" };
        var lowOwner = KindKit.Owner("apply-clients");
        Assert.StartsWith("ClientsTooLow", Submit(low, lowOwner, Camp(entry, lowOwner)).Result.Reason);
        Assert.Empty(w.Sink.Writes);
        Assert.Empty(w.Created);
        var fine = KindKit.Owner("apply-fine");
        Assert.Equal(BlockApplyState.Applied, Submit(KindKit.Mod("apply-fine"), fine, Camp(entry, fine)).State);
    }

    [Fact]
    public void A_mod_that_unloads_takes_its_plans_with_it()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-retire");
        var app = Submit(target, consumer, Camp(entry, consumer));
        KindApply.RetireConsumer(consumer.Token);
        Assert.Equal(BlockApplyState.Removed, app.State);
        Assert.Equal([app.Owner], w.Revoked);
        Assert.Empty(target.Handles);
        Assert.True(app.Stage.Ledger.Revoked);
        Assert.Equal([BlockApplyState.Applied, BlockApplyState.Removed], w.Changes.Select(c => c.After.State));
        Assert.Equal(BlockApplyState.Removed, KindApply.Of(app.Plan).State);
        KindApply.RetireConsumer(consumer.Token);
        Assert.Single(w.Revoked);
    }

    [Fact]
    public void Products_name_each_other_in_any_order_and_a_circle_is_an_error()
    {
        using var w = new KindWorld();
        var (entry, kind, _, consumer, target) = Setup("apply-order");
        // the primary names "b", which names "a": listed primary first, staged a, b, primary
        kind.Body = (c, b) => c.Plan("light",
        [
            c.Effect("light", new EffectBlock { Heal = 1, Then = [(EffectRef)c.ProductName("b")] }),
            c.Effect("b", new EffectBlock { Heal = 2, Then = [(EffectRef)c.ProductName("a")] }),
            c.Effect("a", new EffectBlock { Heal = 3 }),
        ]);
        var app = Submit(target, consumer, Camp(entry, consumer));
        Assert.Equal(BlockApplyState.Applied, app.State);
        Assert.Equal(["kind-camp.a", "kind-camp.b", "kind-camp.light"], app.Stage.Blocks.Select(s => s.Name));

        kind.Body = (c, b) => c.Plan("x", [c.Effect("x", new EffectBlock { Then = [(EffectRef)c.ProductName("y")] }), c.Effect("y", new EffectBlock { Then = [(EffectRef)c.ProductName("x")] })]);
        var circle = Assert.Throws<BlockError>(() => Submit(target, consumer, KindCompiler.Compile(consumer, entry, "ring", new LanternBlock())));
        Assert.Contains("refers back to itself", circle.Message);
        Assert.StartsWith("provider:lantern ring", circle.Block);
        Assert.False(target.Handles.ContainsKey("ring"));
        Assert.Null(KindTransaction.Current);
    }

    [Fact]
    public void Without_a_way_to_revoke_nothing_is_applied()
    {
        using var w = new KindWorld();
        KindApply.RevokeOwner = null;
        KindCapabilities.Rearm();
        var (entry, _, _, consumer, target) = Setup("apply-norevoke");
        var plan = Camp(entry, consumer);
        Assert.Equal("blocks.apply", Assert.Throws<KilnUnavailableException>(() => KindCapabilities.RequireApply(plan)).Capability);
        KindCapabilities.Require(KindCapabilities.Compile);
        var app = Submit(target, consumer, plan);
        Assert.Equal(BlockApplyState.Refused, app.State);
        Assert.Empty(w.Sink.Writes);
        Assert.Empty(w.Created);
        var row = KindCapabilities.ApplyRowFor(true, false);
        Assert.Equal((CapabilityState.Unavailable, CapabilityReason.ServiceOlder), (row.State, row.Reason));
        Assert.Equal(CapabilityState.Available, KindCapabilities.ApplyRowFor(true, true).State);
        Assert.Equal(["blocks.apply", "blocks.apply.item", "blocks.apply.weapon", "blocks.apply.spell", "blocks.apply.status", "blocks.apply.effect"], KindCapabilities.ApplyRows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_resolved_pending_dependency_must_apply_before_its_consumer_and_a_refusal_propagates(bool foreign)
    {
        using var w = new KindWorld { Generation = null };
        var (entry, kind, _, consumer, target) = Setup("apply-dependency");
        var dependency = Submit(target, consumer, Camp(entry, consumer));
        var dependentOwner = foreign ? KindKit.Owner("apply-foreign-dependent") : consumer;
        var dependentTarget = foreign ? KindKit.Mod("apply-foreign-dependent") : target;
        kind.Body = (c, b) =>
        {
            c.Resolve(new BlockRef<LanternBlock>(foreign ? $"{target.Id}:camp" : "camp"));
            return LanternKind.Default(c, b);
        };
        // The ordinary Compile path must include provisional identities without a caller-supplied test view.
        var plan = KindCompiler.Compile(dependentOwner, entry, "dependent", new LanternBlock { Name = "Dependent", Seconds = 600 });
        var dependent = Submit(dependentTarget, dependentOwner, plan);
        w.Generation = 1;
        w.Sink.FailAt = 0;
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.Refused, dependency.State);
        Assert.Equal(BlockApplyState.Refused, dependent.State);
        Assert.StartsWith("DependencyRefused", dependent.Reason);
        Assert.Empty(target.Handles);
        Assert.Empty(w.Sink.Writes);
    }

    [Fact]
    public void Plans_with_colliding_generated_names_cannot_replace_each_other()
    {
        using var w = new KindWorld();
        var (entry, kind, _, consumer, target) = Setup("apply-product-collision");
        kind.Body = (c, _) => c.Plan("b.c", [c.Effect("b.c", new EffectBlock { Heal = 1 })]);
        var first = KindCompiler.Compile(consumer, entry, "a", new LanternBlock());
        Submit(target, consumer, first);
        kind.Body = (c, _) => c.Plan("c", [c.Effect("c", new EffectBlock { Heal = 2 })]);
        var other = KindCompiler.Compile(consumer, entry, "a.b", new LanternBlock());
        Assert.Throws<BlockError>(() => Submit(target, consumer, other));
        Assert.Single(w.Created);
        Assert.Equal(["a"], target.Handles.Keys);
    }

    [Fact]
    public void A_plan_cannot_take_a_generated_name_declared_in_the_consumers_data()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-pending-collision");
        target.Pending["kind-camp.light"] = () => throw new Exception("must not compile the authored block");
        Assert.Throws<BlockError>(() => Submit(target, consumer, Camp(entry, consumer)));
        Assert.Empty(w.Created);
    }

    [Fact]
    public void Apply_refuses_the_selected_primary_when_its_reported_row_is_off()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, _) = Setup("apply-capability-row");
        var plan = Camp(entry, consumer);
        Capabilities.Reset();
        try
        {
            KindCapabilities.RequireApply(plan);
            Capabilities.Report("blocks.apply.item", CapabilityState.Unavailable, CapabilityReason.Policy, "item plans disabled");
            Assert.False(Capabilities.Is("blocks.apply.item"));
            var error = Assert.Throws<KilnUnavailableException>(() => KindCapabilities.RequireApply(plan));
            Assert.Equal("blocks.apply.item", error.Capability);
        }
        finally { Capabilities.Reset(); KindCapabilities.Rearm(); }
    }

    [Fact]
    public void Named_product_reference_depth_is_bounded_separately_from_JSON_depth()
    {
        using var w = new KindWorld();
        var (entry, kind, _, consumer, target) = Setup("apply-reference-depth");
        BlockPlan Chain(BlockCompileContext c, int count)
        {
            var products = new List<BlockProduct>();
            for (var i = 0; i < count; i++)
                products.Add(c.Effect($"e{i:00}", i == count - 1 ? new EffectBlock { Heal = 1 }
                    : new EffectBlock { Then = [(EffectRef)c.ProductName($"e{i + 1:00}")] }));
            return c.Plan("e00", products);
        }
        kind.Body = (c, _) => Chain(c, 32);
        var good = KindCompiler.Compile(consumer, entry, "fits", new LanternBlock());
        Assert.Equal(BlockApplyState.Applied, Submit(target, consumer, good).State);
        kind.Body = (c, _) => Chain(c, 33);
        var tooDeep = KindCompiler.Compile(consumer, entry, "too-deep", new LanternBlock());
        Assert.Throws<BlockError>(() => Submit(target, consumer, tooDeep));
        Assert.Single(w.Created);
    }

    [Fact]
    public void An_authored_block_cannot_overwrite_an_applied_plans_private_product()
    {
        using var w = new KindWorld();
        var (entry, _, _, consumer, target) = Setup("apply-authored-collision");
        Submit(target, consumer, Camp(entry, consumer));
        var writes = w.Sink.Writes.Count;
        Assert.Throws<BlockError>(() => Compiler.Define(target, "kind-camp.light", new ItemBlock { Name = "Replacement" }));
        Assert.Equal(writes, w.Sink.Writes.Count);
    }

    [Fact]
    public void Replacing_a_pending_request_retires_its_provisional_handle_and_dependents()
    {
        using var w = new KindWorld { Generation = null };
        var (entry, kind, _, consumer, target) = Setup("apply-replace-pending");
        var old = Submit(target, consumer, Camp(entry, consumer));
        kind.Body = (c, b) => { c.Resolve(new BlockRef<LanternBlock>("camp")); return LanternKind.Default(c, b); };
        var dependent = Submit(target, consumer, KindCompiler.Compile(consumer, entry, "dependent", new LanternBlock { Name = "Dependent", Seconds = 600 }));
        kind.Body = null;
        Submit(target, consumer, Camp(entry, consumer, 900));
        Assert.Equal(BlockApplyState.Removed, KindApply.Of(old.Handle)!.State);
        w.Generation = 1;
        KindApply.PumpAll();
        Assert.Equal(BlockApplyState.Refused, dependent.State);
        Assert.StartsWith("DependencyRefused", dependent.Reason);
    }

    [Fact]
    public void Defining_the_same_external_record_through_different_generic_views_replays_one_application()
    {
        using var w = new KindWorld();
        KindKit.Lantern();
        var context = HostAccess.CreateContext(new ModInfo("generic-view", "Generic view", "1", AppContext.BaseDirectory), 1, AppContext.BaseDirectory);
        var previous = ModContext.CurrentMod;
        ModContext.CurrentMod = context;
        try
        {
            var target = KindKit.Mod("generic-view");
            var first = KindDefine.Define<IBlock>(target, "camp", new LanternBlock { Name = "Camp", Seconds = 600 });
            Assert.Equal(typeof(LanternBlock), first.BlockType);
            var second = KindDefine.Define(target, "camp", new LanternBlock { Name = "Camp", Seconds = 600 });
            Assert.Equal(first.Guid, second.Guid);
            Assert.Equal(KindApply.Of(first), KindApply.Of(second));
            Assert.Single(w.Created);
        }
        finally { context.Retire(); ModContext.CurrentMod = previous; }
    }

    // ---- a refusal at every registration, for each primary (16d S5) -------------------------------------------------

    private static StatusBlock Chilled => new() { Name = "Chilled", Seconds = 4, Kind = StatusKind.Debuff };

    private static BlockPlan Primary(string primary, BlockCompileContext c) => primary switch
    {
        "item" => c.Plan("p", [c.Item("p", new ItemBlock { Name = "Berry", Kind = ItemKind.Consumable, OnUse = [new EffectBlock { On = EffectOn.Caster, Applies = Chilled }], Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 } } }), c.Effect("aux", new EffectBlock { Heal = 1 })]),
        "weapon" => c.Plan("p", [c.Weapon("p", new WeaponBlock { Name = "Edge", Class = WeaponClass.Sword, OnHit = [new EffectBlock { Applies = Chilled }] })]),
        "spell" => c.Plan("p", [c.Spell("p", new SpellBlock { Name = "Bolt", Cast = new CastBlock { ManaCost = 5 }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Ice(5), Applies = Chilled }] } }] } })]),
        "status" => c.Plan("p", [c.Status("p", Chilled with { OnApply = [new EffectBlock { Heal = 2 }] })]),
        _ => c.Plan("p", [c.Effect("p", new EffectBlock { Damage = Damage.Ice(5), Applies = Chilled })]),
    };

    public static TheoryData<string> Primaries => new() { "item", "weapon", "spell", "status", "effect" };

    [Theory]
    [MemberData(nameof(Primaries))]
    public void A_refusal_at_any_registration_leaves_no_block_no_export_and_no_owner(string primary)
    {
        int steps;
        using (var clean = new KindWorld())
        {
            var (entry, kind, _, consumer, target) = Setup($"revoke-{primary}-clean");
            kind.Body = (c, _) => Primary(primary, c);
            Assert.Equal(BlockApplyState.Applied, Submit(target, consumer, Camp(entry, consumer)).State);
            steps = clean.Sink.Writes.Count;
            Assert.True(steps >= 1, "the plan registers something");
            Assert.Empty(clean.Revoked);
        }
        for (var failAt = 0; failAt < steps; failAt++)
        {
            using var w = new KindWorld();
            var (entry, kind, _, consumer, target) = Setup($"revoke-{primary}-{failAt}");
            kind.Body = (c, _) => Primary(primary, c);
            w.Sink.FailAt = failAt;
            var app = Submit(target, consumer, Camp(entry, consumer));
            Assert.Equal(BlockApplyState.Refused, app.State);
            Assert.StartsWith("Refused: refused:", app.Result.Reason);
            Assert.False(app.Result.Ok);
            // nothing is published: no handle, no lowered document for the export and reports, no program in the mod's ledger
            Assert.Empty(target.Handles);
            Assert.Empty(target.Lowered);
            Assert.Empty(target.Programs);
            // what was registered before the refusal went with the plan's owner, and the plan's ledger never runs
            Assert.Equal(failAt, w.Sink.Writes.Count);
            Assert.Equal(w.Created, w.Revoked);
            Assert.Single(w.Revoked);
            Assert.True(app.Stage.Ledger.Revoked);
            Assert.False(app.Stage.Ledger.Promoted);
            Assert.False(app.Stage.Ledger.ReadyHooked);
            Assert.Null(KindTransaction.Current);
            var change = Assert.Single(w.Changes);
            Assert.Equal(BlockApplyState.Refused, change.After.State);
            // a refused attempt may be tried again, and applies when nothing refuses
            w.Sink.FailAt = -1;
            var again = Submit(target, consumer, Camp(entry, consumer));
            Assert.Equal(BlockApplyState.Applied, again.State);
            Assert.Same(again.Handle, target.Handles["camp"]);
        }
    }
}
