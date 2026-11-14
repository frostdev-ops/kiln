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

    /// <summary>The registered kind of a block's own type. A built-in block has none: <see cref="Define{T}"/> compiles those.</summary>
    internal static KindEntry KindOf(IBlock block)
    {
        var type = block.GetType();
        if (KindRegistry.IsBuiltIn(type)) throw new ArgumentException($"{type.Name} is a block Kiln compiles itself: Blocks.Define takes it. Blocks.Compile takes a block of a kind a mod registered");
        return KindRegistry.Find(type) ?? throw new BlockError(type.Name, null, $"no block kind is registered for {type.Name}: is its provider mod a dependency of this one, and loaded?");
    }
}
