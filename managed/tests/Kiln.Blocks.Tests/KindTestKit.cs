// SPDX-License-Identifier: MIT
// What the block-kind tests share: a provider's record and compiler (the chapter's LanternKind), owners, a counting sink.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

/// <summary>Kind tests change process-wide tables (the kind registry, where events go): one at a time.</summary>
[CollectionDefinition("block kinds", DisableParallelization = true)]
public sealed class KindCollection;

public sealed record LanternBlock : IBlock
{
    public string Name { get; init; } = "";
    public double Seconds { get; init; }
    /// <summary>Mutable on purpose: tests change it after a compile.</summary>
    public List<string> Tags { get; set; } = new();
}

public sealed record OtherBlock : IBlock
{
    public string Name { get; init; } = "";
}

public sealed class LanternKind : IBlockKind<LanternBlock>
{
    public int Calls;
    public Func<BlockCompileContext, LanternBlock, BlockPlan>? Body;
    public BlockCompileContext? LastContext;
    public LanternBlock? LastInput;

    public BlockPlan Compile(BlockCompileContext context, LanternBlock b)
    {
        Calls++;
        LastContext = context;
        LastInput = b;
        return Body != null ? Body(context, b) : Default(context, b);
    }

    public static ItemBlock Torch(LanternBlock b) => new()
    {
        Name = b.Name,
        Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = b.Seconds },
        Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 } },
    };

    public static BlockPlan Default(BlockCompileContext context, LanternBlock b) => context.Plan("light", [context.Item("light", Torch(b))]);
}

public sealed class OtherKind : IBlockKind<OtherBlock>
{
    public BlockPlan Compile(BlockCompileContext context, OtherBlock b) => context.Plan("e", [context.Effect("e", new EffectBlock { Heal = 1 })]);
}

public sealed class BuiltInKind : IBlockKind<ItemBlock>
{
    public BlockPlan Compile(BlockCompileContext context, ItemBlock b) => context.Plan("i", [context.Item("i", b)]);
}

internal static class KindKit
{
    public const string Provider = "provider", Consumer = "consumer";

    public static KindOwner Owner(string mod, Func<bool>? alive = null) => new(mod, new object(), alive ?? (() => true));

    public static BlockKindDescriptor Descriptor(string id = "provider:lantern", string section = "provider:lanterns") => new()
    {
        Id = id, JsonSection = section,
        SchemaJson = """{"type":"object","properties":{"name":{"type":"string"},"seconds":{"type":"number"}}}""",
        Clients = ClientsLevel.Kiln,
    };

    /// <summary>Clears the registry and catches the events of the test.</summary>
    public static List<KilnEvent> Fresh()
    {
        KindRegistry.Reset();
        var events = new List<KilnEvent>();
        KindEvents.Sink = events.Add;
        return events;
    }

    public static (KindEntry Entry, LanternKind Kind, KindOwner Provider) Lantern()
    {
        var provider = Owner(Provider);
        var kind = new LanternKind();
        var r = KindRegistry.Register(provider, Descriptor(), kind);
        Assert.True(r.Ok, r.Reason);
        return (KindRegistry.FindId("provider:lantern")!, kind, provider);
    }

    /// <summary>A mod's real ledger in Blocks' table (the class Blocks is hidden by this assembly's namespace Kiln.Blocks).</summary>
    public static ModBlocks ModFor(string id) =>
        (ModBlocks)typeof(IBlock).Assembly.GetType("Kiln.Blocks", true)!.GetMethod("ModFor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, [id, (Func<ulong>)(() => 1)])!;

    public static ModBlocks Mod(string id) => new(id, 1) { ManifestClients = "mod" };
}
