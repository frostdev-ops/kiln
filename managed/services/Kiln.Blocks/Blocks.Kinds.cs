// SPDX-License-Identifier: MIT
// Blocks' face for block kinds mods add: Kinds/*.cs. docs/kiln/modding/block-kinds.md.
namespace Kiln;

public static partial class Blocks
{
    /// <summary>Registers a block kind as the calling mod's: a record of the mod's own (<typeparamref name="T"/>) and the
    /// pure compiler that turns it into built-in blocks. Call in <c>OnLoad</c>: mods that depend on this one then write the
    /// kind in C# (<see cref="Define{T}"/>) and in blocks/*.json (<see cref="BlockKindDescriptor.JsonSection"/>).
    /// A kind, section or record that is registered already answers <c>Ok = false</c>; a descriptor that is wrong throws.</summary>
    [AnyThread, RequiresCapability("blocks.kinds")]
    public static BlockKindResult RegisterKind<T>(BlockKindDescriptor descriptor, IBlockKind<T> compiler) where T : class, IBlock
    {
        var caller = KindOwner.Caller() ?? throw new InvalidOperationException("Blocks.RegisterKind must be called from mod code");
        KindCapabilities.Require(KindCapabilities.Kinds);
        return KindRegistry.Register(caller, descriptor, compiler);
    }

    /// <summary>Compiles a block of an external kind as the calling mod's, and registers nothing: the provider's compiler
    /// runs once and the result is frozen. <typeparamref name="T"/> may be a base type; the block's own type chooses the kind.
    /// What is wrong with the block throws <see cref="BlockError"/>.</summary>
    [AnyThread, RequiresCapability("blocks.compile")]
    public static BlockPlan Compile<T>(string name, T block) where T : class, IBlock
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(block);
        var caller = KindOwner.Caller() ?? throw new InvalidOperationException("Blocks.Compile must be called from mod code");
        KindCapabilities.Require(KindCapabilities.Compile);
        return KindCompiler.Compile(caller, KindOf(block), name, block);
    }

    /// <summary>Applies a plan the calling mod compiled: Kiln lowers its frozen products with its own compilers and
    /// registers them under an owner of the plan's own. All of it or nothing: a registration that is refused takes the
    /// others back. The same plan again answers the same and does nothing; another plan for a name that is applied is
    /// refused (reload the mod to change it). A plan that waits for the game's content is applied when it is ready.</summary>
    /// <exception cref="ArgumentException">The plan was compiled by another mod, or by an earlier load of this one.</exception>
    /// <exception cref="BlockError">A product of the plan is not a block Kiln can compile here.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.apply</c> or the row of the plan's primary is off.</exception>
    [TickThreadOnly, RequiresCapability("blocks.apply")]
    public static BlockApplyResult Apply(BlockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Services.RequireTickThread("Blocks.Apply");
        var caller = KindOwner.Caller() ?? throw new InvalidOperationException("Blocks.Apply must be called from mod code");
        if (!plan.Consumer.Same(caller))
            throw new ArgumentException($"Blocks.Apply: the plan of \"{plan.Name}\" was compiled by {(plan.ConsumerModId == caller.ModId ? "an earlier load of this mod" : $"mod {plan.ConsumerModId}")}; a plan is applied by the mod instance that compiled it", nameof(plan));
        KindCapabilities.RequireApply(plan);
        return KindApply.Submit(CallerMod(), caller, plan, stage => KindDefine.HandleOf(caller.ModId, plan, stage)).Result;
    }

    /// <summary>Where a plan stands for the mod that compiled it. A snapshot; it applies nothing.</summary>
    [AnyThread, RequiresCapability("blocks.compile")]
    public static BlockApplyResult Application(BlockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        KindCapabilities.Require(KindCapabilities.Compile);
        return KindApply.Of(plan);
    }

    /// <summary>Where a block of an external kind stands: the handle <see cref="Define{T}"/> gave for it.</summary>
    /// <exception cref="ArgumentException">The handle is a built-in block's: those are applied when they are defined.</exception>
    [AnyThread, RequiresCapability("blocks.compile")]
    public static BlockApplyResult Application(BlockHandle block)
    {
        ArgumentNullException.ThrowIfNull(block);
        KindCapabilities.Require(KindCapabilities.Compile);
        return KindApply.Of(block) ?? throw new ArgumentException($"{block} is not a block of an external kind: a built-in block is applied when it is defined", nameof(block));
    }

    /// <summary>The registered kind of a block's own type. A built-in block has none: <see cref="Define{T}"/> compiles those.</summary>
    internal static KindEntry KindOf(IBlock block)
    {
        var type = block.GetType();
        if (KindRegistry.IsBuiltIn(type)) throw new ArgumentException($"{type.Name} is a block Kiln compiles itself: Blocks.Define takes it. Blocks.Compile takes a block of a kind a mod registered");
        return KindRegistry.Find(type) ?? throw new BlockError(type.Name, null, $"no block kind is registered for {type.Name}: is its provider mod a dependency of this one, and loaded?");
    }
}
