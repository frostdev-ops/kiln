// SPDX-License-Identifier: MIT
// A block of an external kind, defined: from a data file's "provider:plural" section, or from Blocks.Define in code.
using System.Reflection;

namespace Kiln;

internal static class KindDefine
{
    /// <summary>The live instance of a mod, as the authority its plans are bound to: the calling mod when it is that mod, or
    /// (a data file compiling on Kiln's tick) the loaded instance of that id.</summary>
    internal static KindOwner? OwnerOf(string modId)
    {
        if (KindOwner.Caller() is { } caller && caller.ModId == modId) return caller;
        var info = Mods.Get(modId)?.Info;
        return info != null && ModContext.FindLive(info) is { } live ? new KindOwner(modId, live, () => live.Alive) : null;
    }

    /// <summary>One block of an external section of blocks/*.json.</summary>
    internal static BlockHandle FromJson(ModBlocks mod, KindEntry kind, string name, System.Text.Json.JsonElement value)
    {
        var record = KindJson.ReadRecord(kind, name, value);
        var consumer = OwnerOf(mod.Id) ?? throw new BlockError($"{kind.Section}.{name}", null, $"mod {mod.Id} is not loaded: a block of kind {kind.Id} belongs to a loaded mod");
        return Define(mod, consumer, kind, name, record, null);
    }

    /// <summary>Blocks.Define with a block of an external kind: the handle is typed as the caller wrote it.</summary>
    internal static BlockHandle<T> Define<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        var consumer = KindOwner.Caller() ?? throw new InvalidOperationException("Blocks.Define must be called from mod code");
        return KindApply.HandleAs<T>(Define(mod, consumer, Blocks.KindOf(block), name, block,
            (plan, stage) => new BlockHandle<T>(mod.Id, name, stage.Primary.Guid, stage.Primary.GameName, (T)plan.Projection!)));
    }

    /// <summary>Compiles the block to a plan and submits it. The handle is provisional until the plan is applied:
    /// <see cref="Blocks.Find"/> does not give it before, and <see cref="Blocks.Application(BlockHandle)"/> says where it stands.</summary>
    internal static BlockHandle Define(ModBlocks mod, KindOwner consumer, KindEntry kind, string name, IBlock record, Func<BlockPlan, KindStage, BlockHandle>? handle)
    {
        KindCapabilities.Require(KindCapabilities.Compile);
        var plan = KindCompiler.Compile(consumer, kind, name, record, KindApply.Provisional(consumer));
        KindCapabilities.RequireApply(plan);
        var app = KindApply.Submit(mod, consumer, plan, stage => handle?.Invoke(plan, stage) ?? HandleOf(mod.Id, plan, stage));
        if (app.State == BlockApplyState.Refused && !ReferenceEquals(app.Plan, plan)) throw new BlockError($"{kind.Id} {name}", null, app.Reason);
        return app.Handle;
    }

    /// <summary>A handle typed by the record's own type, for a plan that was not defined through a generic call.</summary>
    internal static BlockHandle HandleOf(string mod, BlockPlan plan, KindStage stage)
    {
        var record = plan.Projection ?? throw new InvalidOperationException("the plan has no record");
        var type = typeof(BlockHandle<>).MakeGenericType(record.GetType());
        return (BlockHandle)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([mod, plan.Name, stage.Primary.Guid, stage.Primary.GameName, record]);
    }
}
