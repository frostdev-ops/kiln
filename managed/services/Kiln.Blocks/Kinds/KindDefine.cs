// SPDX-License-Identifier: MIT
// A block of an external kind, defined: from a data file's "provider:plural" section, or from Blocks.Define in code.
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
        return Define(mod, consumer, kind, name, record);
    }

    /// <summary>Compiles the block to a plan and schedules it; the handle is provisional until the plan is applied.</summary>
    internal static BlockHandle Define(ModBlocks mod, KindOwner consumer, KindEntry kind, string name, IBlock record)
    {
        KindCapabilities.Require(KindCapabilities.Compile);
        var plan = KindCompiler.Compile(consumer, kind, name, record);
        throw new KilnUnavailableException("blocks.apply", $"block {plan.KindId} {plan.Name} compiled, and this build does not apply a kind's plan yet");
    }
}
