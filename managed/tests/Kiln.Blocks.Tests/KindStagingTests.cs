// SPDX-License-Identifier: MIT
// The StageOnly transaction (16d S3): while a kind's plan is staged, every Compiler.Define on the thread lowers into the
// plan's own ledger and commits nothing, however it was reached: directly, for an inline status nested in an item, a spell or
// an effect, or through a data file's pending closure over the mod's real ledger.
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("block kinds")]
public sealed class KindStagingTests
{
    private static StatusBlock Chilled => new() { Name = "Chilled", Seconds = 4, Kind = StatusKind.Debuff };

    private static ItemBlock Item => new() { Name = "Berry", Kind = ItemKind.Consumable, OnUse = [new EffectBlock { On = EffectOn.Caster, Applies = Chilled }] };
    private static SpellBlock Spell => new() { Name = "Bolt", Cast = new CastBlock { ManaCost = 5 }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Ice(5), Applies = Chilled }] } }] } };
    private static EffectBlock Effect => new() { Damage = Damage.Ice(5), Applies = Chilled };

    private static void Untouched(ModBlocks target)
    {
        Assert.Empty(target.Handles);
        Assert.Empty(target.Lowered);
        Assert.Empty(target.Programs);
        Assert.Empty(target.LateBuilders);
        Assert.Empty(target.LatePending);
        Assert.Empty(target.ReadyRules);
        Assert.Empty(target.ReadyChecks);
        Assert.Empty(target.Carriers);
        Assert.False(target.ReadyHooked);
    }

    public static TheoryData<string> Kinds => new() { "item", "spell", "effect" };

    private static BlockHandle Define(ModBlocks mod, string kind) => kind switch
    {
        "item" => Compiler.Define(mod, "outer", Item),
        "spell" => Compiler.Define(mod, "outer", Spell),
        _ => Compiler.Define(mod, "outer", Effect),
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void A_block_with_an_inline_status_stages_both_and_writes_nothing(string kind)
    {
        using var sink = new CountingSink();
        var target = KindKit.Mod($"staging-{kind}");
        using (var tx = KindTransaction.Begin(target))
        {
            var handle = Define(target, kind);
            Assert.Empty(sink.Writes);
            Untouched(target);
            // the nested status joined the transaction, before the block that holds it
            Assert.Equal(2, tx.Staged.Count);
            Assert.IsType<BlockHandle<StatusBlock>>(tx.Staged[0].Handle);
            Assert.Equal("outer", tx.Staged[1].Name);
            Assert.Same(handle, tx.Staged[1].Handle);
            Assert.NotEmpty(tx.Stage.Programs);             // the status's program is in the plan's ledger, not the mod's
            // nothing staged can be committed before the transaction is
            foreach (var staged in tx.Staged) Assert.Throws<InvalidOperationException>(staged.Low.Commit);
            Assert.Empty(sink.Writes);
        }
        Assert.Null(KindTransaction.Current);
        Untouched(target);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void The_same_block_without_a_transaction_registers_as_before(string kind)
    {
        // the control of the test above: the sink does see a commit, and the mod's ledger does change
        using var sink = new CountingSink();
        var target = KindKit.Mod($"unstaged-{kind}");
        Define(target, kind);
        Assert.NotEmpty(sink.Writes);
        Assert.Contains(sink.Writes, w => w.StartsWith("clone 1 buffs"));
        Assert.Equal(2, target.Handles.Count);
        Assert.NotEmpty(target.Programs);
    }

    [Fact]
    public void A_data_files_block_named_inside_the_transaction_joins_it()
    {
        using var sink = new CountingSink();
        var target = KindKit.Mod("staging-pending");
        var compiled = 0;
        // as BlockJson.Read leaves it: a closure over the mod's real ledger that ends in Compiler.Define
        target.Pending["chilled"] = () => { compiled++; return Compiler.Define(target, "chilled", Chilled); };
        using (var tx = KindTransaction.Begin(target))
        {
            Compiler.Define(target, "outer", new EffectBlock { Applies = (StatusRef)"chilled" });
            Assert.Equal(1, compiled);
            Assert.Equal(["chilled", "outer"], tx.Staged.Select(s => s.Name));
            Assert.Empty(sink.Writes);
            Untouched(target);
            Assert.Empty(target.Compiling);
            // named again: the staged one, not a second compile
            Compiler.Define(target, "again", new EffectBlock { Applies = (StatusRef)"chilled" });
            Assert.Equal(1, compiled);
        }
        Untouched(target);
    }

    [Fact]
    public void A_staged_block_names_the_mods_published_blocks_and_the_plans_own_products()
    {
        using var sink = new CountingSink();
        var target = KindKit.Mod("staging-names");
        var published = Compiler.Define(target, "chilled", Chilled);
        var before = sink.Writes.Count;
        using var tx = KindTransaction.Begin(target);
        Compiler.Define(target, "kind-camp.glow", new EffectBlock { Applies = (StatusRef)"chilled" });
        // a product names another by "consumer:kind-<name>.<key>": staged, not published, and found
        Compiler.Define(target, "kind-camp.light", new EffectBlock { Then = [(EffectRef)"staging-names:kind-camp.glow"] });
        Assert.Equal(["kind-camp.glow", "kind-camp.light"], tx.Staged.Select(s => s.Name));
        Assert.Equal(before, sink.Writes.Count);
        Assert.Same(published, Assert.Single(target.Handles).Value);
    }

    [Fact]
    public void A_plan_never_replaces_a_block_and_never_compiles_another_mods()
    {
        using var sink = new CountingSink();
        var target = KindKit.Mod("staging-collide");
        var foreign = KindKit.Mod("staging-foreign");
        Compiler.Define(target, "taken", Chilled);
        var before = sink.Writes.Count;
        using var tx = KindTransaction.Begin(target);
        Assert.Contains("exists already", Assert.Throws<BlockError>(() => Compiler.Define(target, "taken", Chilled)).Message);
        Compiler.Define(target, "fresh", Chilled);
        Assert.Throws<BlockError>(() => Compiler.Define(target, "fresh", Chilled));
        var pending = Assert.Throws<KindDependencyPending>(() => Compiler.Define(foreign, "theirs", Chilled));
        Assert.Equal(("staging-foreign", "theirs"), (pending.Mod, pending.Name));
        Untouched(foreign);
        Assert.Equal(before, sink.Writes.Count);
    }

    [Fact]
    public void One_transaction_per_thread_and_committing_opens_the_staged_blocks()
    {
        using var sink = new CountingSink();
        var target = KindKit.Mod("staging-commit");
        var tx = KindTransaction.Begin(target);
        try
        {
            Assert.Throws<InvalidOperationException>(() => KindTransaction.Begin(target));
            Assert.Throws<InvalidOperationException>(() => KindTransaction.Begin(tx.Stage));
            Compiler.Define(target, "chilled", Chilled);
            // another thread has no transaction: its define is an ordinary one
            var elsewhere = Task.Run(() => KindTransaction.Current).GetAwaiter().GetResult();
            Assert.Null(elsewhere);
            tx.BeginCommit();
            Assert.Throws<InvalidOperationException>(() => Compiler.Define(target, "late", Chilled));
            tx.Staged[0].Low.Commit();
            Assert.Contains(sink.Writes, w => w.StartsWith("clone 0 buffs"));   // under the staging ledger's owner, which the plan's owner replaces
        }
        finally
        {
            tx.Dispose();
        }
        Assert.Null(KindTransaction.Current);
        using var next = KindTransaction.Begin(target);
        Assert.Empty(next.Staged);
    }
}

/// <summary>Counts what a commit would register, and can refuse the n-th registration.</summary>
internal sealed class CountingSink : Lowered.ILoweredSink, IDisposable
{
    public readonly List<string> Writes = new();
    public int FailAt = -1;

    public CountingSink() => Lowered._sinkOverride = this;
    public void Dispose() => Lowered._sinkOverride = null;

    private void Add(string what)
    {
        if (Writes.Count == FailAt) throw new InvalidOperationException($"refused: {what}");
        Writes.Add(what);
    }

    public void Program(ulong owner, Guid guid, byte[] blob) => Add($"program {owner} {guid}");
    public void Clone(ulong owner, string section, JsonObject def) => Add($"clone {owner} {section} {def["name"]}");
    public void Item(ulong owner, ItemDefinition d) => Add($"item {owner} {d.Name}");
    public void Recipe(ulong owner, RecipeDefinition d) => Add($"recipe {owner} {d.Name}");
}
