// SPDX-License-Identifier: MIT
// Staging for a kind's plan. Kiln's compilers register as they go: Compiler.Define lowers and commits, and a block with an
// inline status defines that status on the way (BlockContext.Status), as does a data file's block named for the first time
// (ModBlocks.Pending). A plan must reach the game whole or not at all, so while a transaction is open on this thread every
// Compiler.Define, however it was reached, lowers into the transaction's own ledger and commits nothing.
namespace Kiln;

internal sealed class KindTransaction : IDisposable
{
    [ThreadStatic] private static KindTransaction? _current;

    /// <summary>The transaction open on this thread, or null.</summary>
    public static KindTransaction? Current => _current;

    /// <summary>The consumer's real ledger. Nothing here writes it.</summary>
    public ModBlocks Target { get; }
    /// <summary>The isolated ledger blocks lower into: programs, late builders, handles and lowered documents of this plan only.</summary>
    public ModBlocks Stage { get; }
    /// <summary>What was staged, in the order it was defined (a nested block before the block that holds it).</summary>
    public List<StagedBlock> Staged { get; } = new();
    /// <summary>Set while the owner of the transaction commits it: only then may a staged <see cref="Lowered"/> commit.</summary>
    public bool Committing => Stage.CommitOpen;

    internal sealed record StagedBlock(string Name, BlockHandle Handle, Lowered Low, string Label);

    private KindTransaction(ModBlocks target, ModBlocks? stage = null)
    {
        Target = target;
        Stage = stage ?? new ModBlocks(target.Id, 0) { StagesFor = target, ManifestClients = target.ManifestClients };
    }

    public static KindTransaction Begin(ModBlocks target)
    {
        if (_current != null) throw new InvalidOperationException("a block kind transaction is already open on this thread");
        if (target.StagesFor != null) throw new InvalidOperationException("a staging ledger cannot be staged for");
        return _current = new KindTransaction(target);
    }

    /// <summary>Opens the transaction of a ledger that was staged before, to commit it.</summary>
    public static KindTransaction Resume(ModBlocks target, ModBlocks stage)
    {
        if (_current != null) throw new InvalidOperationException("a block kind transaction is already open on this thread");
        if (!ReferenceEquals(stage.StagesFor, target)) throw new InvalidOperationException("the ledger was not staged for this mod");
        return _current = new KindTransaction(target, stage);
    }

    /// <summary>Compiler.Define inside the transaction: lowered and kept, nothing committed.</summary>
    public BlockHandle<T> Define<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        if (Committing) throw new InvalidOperationException($"block {name}: a block cannot be defined while a kind's plan commits");
        // Another mod's block does not join this plan, and is not committed from here either: the plan waits for it.
        if (!ReferenceEquals(mod, Target) && !ReferenceEquals(mod, Stage)) throw new KindDependencyPending(mod.Id, name);
        if (Target.Handles.ContainsKey(name) || Stage.Handles.ContainsKey(name))
            throw new BlockError(name, null, $"a block \"{name}\" of mod {Target.Id} exists already: a kind's plan never replaces one");
        var (handle, low, label) = Compiler.Lower(Stage, name, block);
        Stage.Handles[name] = handle;
        Stage.Lowered[name] = low.Json;
        Staged.Add(new StagedBlock(name, handle, low, label));
        return (BlockHandle<T>)handle;
    }

    /// <summary>The owner of the transaction commits what was staged: from here to <see cref="Dispose"/> a staged <see cref="Lowered.Commit"/> is allowed.</summary>
    public void BeginCommit() => Stage.CommitOpen = true;

    public void Dispose()
    {
        if (ReferenceEquals(_current, this)) _current = null;
    }
}

/// <summary>A plan named a block of another mod that is not compiled yet. That mod compiles its own blocks; the plan is tried again.</summary>
internal sealed class KindDependencyPending(string mod, string name) : Exception($"block \"{name}\" of mod {mod} is not compiled yet")
{
    public string Mod { get; } = mod;
    public string Name { get; } = name;
}
