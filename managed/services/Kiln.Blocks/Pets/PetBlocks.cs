// SPDX-License-Identifier: MIT
// A pet, as a block (19d §5.1): PetBlock and ReviveBlock, in C# and in blocks/*.json ("pets" section). It compiles to the
// immutable Kiln.Pets PetDefinition, the same record Pets.Define takes, so C# and JSON lower to one thing. The policy
// records (level, movement, targets, downed, caps, HUD) are Kiln.Pets' own, reused here as they are. Every choice in it is
// the mod's; Kiln's defaults are only what a block that says nothing gets (players protected, a bodyless recovery anchor).
// docs/kiln/modding/pets.md.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>What a revive costs: so many of a plain stack item.</summary>
/// <param name="Item">A plain stack item of the game or of a mod (not gear).</param>
/// <param name="Count">1..999.</param>
public sealed record ReviveCostBlock(ItemRef Item, int Count = 1);

/// <summary>How a downed pet is revived: the owner channels next to its recovery anchor. All of it is the mod's choice.</summary>
public sealed record ReviveBlock
{
    /// <summary>False: Kiln's revive is off for this pet (the mod releases it, or revives it its own way).</summary>
    public bool Allowed { get; init; } = true;
    /// <summary>Seconds the owner channels, 0.1..60.</summary>
    public double ChannelSeconds { get; init; } = 3;
    /// <summary>Metres between owner and anchor while channelling, 1..8.</summary>
    public double RangeMeters { get; init; } = 3;
    /// <summary>The share of its maximum health the pet comes back with, more than 0 and at most 1.</summary>
    public double HealthFraction { get; init; } = 0.5;
    /// <summary>What it costs, taken when the channel completes; null: free.</summary>
    public ReviveCostBlock? Cost { get; init; }
    /// <summary>The owner being hurt during the channel cancels it.</summary>
    public bool CancelOnObservedOwnerDamage { get; init; } = true;
    /// <summary>The owner moving more than <see cref="MaxOwnerDisplacement"/> cancels it.</summary>
    public bool CancelOnMovement { get; init; } = true;
    /// <summary>Metres, 0.1..2.</summary>
    public double MaxOwnerDisplacement { get; init; } = 0.75;
}

/// <summary>
/// A pet: a creature an owner keeps, summons, orders and revives. Kiln keeps its record, makes and retires its body, holds
/// its movement and protects it; this block says what it is and every choice about it.
/// <code>
/// var wolf = Blocks.Define("wolf", new CreatureBlock { Body = Bodies.Vukah });
/// Blocks.Define("wolf-pet", new PetBlock { Creature = wolf, Name = "Companion", Revive = new ReviveBlock { ChannelSeconds = 3 } });
/// Blocks.Define("hound", new PetBlock { Creature = "game:enemy_wildbeast_wolf" });   // a game creature, by its creatures-registry id
/// </code>
/// Its id is <c>&lt;mod id&gt;:&lt;name&gt;</c>; <see cref="Pets"/> takes it. A pet on a game creature asks nothing of players' games.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record PetBlock : IBlock
{
    /// <summary>The creature its body is: a <see cref="CreatureBlock"/> (inline, a handle, or a block's name) or a game
    /// creature by its <c>creatures</c> registry id (<c>"game:enemy_wildbeast_wolf"</c>).</summary>
    public required PartRef<CreatureBlock> Creature { get; init; }
    /// <summary>The name owners read; default: the creature's own.</summary>
    public string? Name { get; init; }
    /// <summary>The name before a rename: the id comes from the oldest name, so records keep their pets.</summary>
    public string? Was { get; init; }
    /// <summary>Its level and size.</summary>
    public PetLevelPolicy Level { get; init; } = new();
    /// <summary>How it follows: distances, catch-up.</summary>
    public PetMovementPolicy Movement { get; init; } = new();
    /// <summary>How it is revived.</summary>
    public ReviveBlock Revive { get; init; } = new();
    /// <summary>Who it may harm and who may harm it. Default: every player protected.</summary>
    public PetTargetPolicy Targets { get; init; } = new();
    /// <summary>What it becomes when it goes down. Default: a bodyless recovery anchor.</summary>
    public PetDownedPolicy Downed { get; init; } = new();
    /// <summary>How many there may be.</summary>
    public PetCaps Caps { get; init; } = new();
    /// <summary>What the owner's HUD shows of it.</summary>
    public PetHudPolicy Hud { get; init; } = new();
}

internal static class PetJson
{
    public static readonly (string Section, Func<ModBlocks, string, System.Text.Json.JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("pets", BlockJson.Make<PetBlock>),
    ];
}

internal static partial class Compiler
{
    internal static BlockHandle? DefinePetBlock(BlockContext ctx, Lowered low, string name, IBlock block) =>
        block is PetBlock p ? PetCompiler.Define(ctx, low, name, p) : null;
}

internal static class PetCompiler
{
    public static BlockHandle Define(BlockContext ctx, Lowered low, string name, PetBlock p)
    {
        var mod = ctx.Mod.Id;
        var key = p.Was ?? name;
        var id = $"{mod.ToLowerInvariant()}:{key.ToLowerInvariant()}";
        if (!PetDefinitionRules.ValidId(id)) throw new BlockError(ctx.Block, "name", $"\"{id}\" is not a pet id (lower case letters, digits, '-', '_', '.')");
        if (p.Creature is null) throw new BlockError(ctx.Block, "creature", "a pet needs a creature");

        // the creature: a game creature by its registry id, or a creature block (compiled now when written inline)
        string creatureId;
        PetCreatureInfo? block = null;
        if (p.Creature.Name is { } named && named.StartsWith("game:", StringComparison.Ordinal))
        {
            creatureId = named;
            if (!PetDefinitionRules.ValidId(creatureId)) throw new BlockError(ctx.Block, "creature", $"\"{named}\" is not a creatures registry id (game:<name>, lower case)");
        }
        else
        {
            var handle = CreatureHandleOf(ctx, p.Creature, name);
            creatureId = $"{handle.Mod.ToLowerInvariant()}:{handle.Name.ToLowerInvariant()}";
            var info = handle.Info;
            var template = info.TemplateGuid != Guid.Empty ? info.TemplateGuid : Creatures.Available && Creatures.Template(info.Template) is { } t ? t.Id : Guid.Empty;
            var gameMax = Creatures.Available && Creatures.Template(info.Template) is { } gt ? gt.LevelMax : 0;
            block = new PetCreatureInfo(creatureId, template, info.Variant, gameMax, handle.Block.Name ?? handle.Name);
        }

        // the revive cost: an item of the game or of a mod, as a plain stack
        PetCostDefinition? cost = null;
        if (p.Revive.Cost is { } c)
        {
            if (c.Count is < 1 or > 999) throw new BlockError(ctx.Block, "revive.cost.count", $"{c.Count} is outside 1..999");
            var itemName = ctx.ItemName(c.Item, "revive.cost.item");
            var itemId = ItemIdOf(itemName);
            if (itemId is null && Items.Available) throw new BlockError(ctx.Block, "revive.cost.item", $"the game has no item \"{c.Item.Value}\"");
            if (itemId is not null && Items.Lookup(itemName) is { IsGear: true }) throw new BlockError(ctx.Block, "revive.cost.item", $"\"{c.Item.Value}\" is gear; a revive costs a plain stack item");
            cost = new PetCostDefinition(itemId ?? new ItemId(PendingItem), c.Count);
            if (itemId is null) low.AfterCommit(() => LateCost(ctx.Mod, ctx.Block, id, itemName));
        }

        var def = new PetDefinition
        {
            Id = id, Creature = creatureId, DisplayName = p.Name, Level = p.Level ?? new(), Movement = p.Movement ?? new(),
            Revive = new PetRevivePolicy
            {
                Allowed = p.Revive.Allowed, ChannelSeconds = p.Revive.ChannelSeconds, RangeMeters = p.Revive.RangeMeters, HealthFraction = p.Revive.HealthFraction,
                Cost = cost, CancelOnObservedOwnerDamage = p.Revive.CancelOnObservedOwnerDamage, CancelOnMovement = p.Revive.CancelOnMovement,
                MaxOwnerDisplacement = p.Revive.MaxOwnerDisplacement,
            },
            Targets = p.Targets ?? new(), Downed = p.Downed ?? new(), Caps = p.Caps ?? new(), Hud = p.Hud ?? new(),
        };
        if (PetDefinitionRules.Check(def, block?.LevelMax is > 0 ? block.LevelMax : null) is { } bad)
            throw new BlockError(ctx.Block, bad.Field == "id" ? "name" : bad.Field, bad.Problem);

        low.Json["pet"] = JsonNode.Parse(PetsRuntime.CanonicalJson(def));
        low.AfterCommit(() =>
        {
            if (block is not null) PetCreatures.RegisterBlock(block.Id, block.Variant!, block.Template, block.Id, block.LevelMax, block.DisplayName);
            var result = Kiln.Pets.DefineAs(def, mod);
            if (!result.Ok) BlocksBridge.Log(LogLevel.Error, $"blocks: {mod}: {ctx.Block}: Pets.Define refused it: {result.Reason}");
        });
        return new BlockHandle<PetBlock>(mod, name, BlockIds.Derive(mod, "pet", key), id, p);
    }

    /// <summary>The handle of the creature block a pet names (an inline one is compiled now as <c>&lt;pet&gt;.creature</c>).</summary>
    private static CreatureHandle CreatureHandleOf(BlockContext ctx, PartRef<CreatureBlock> r, string petName)
    {
        if (r.Name is null)
        {
            var inner = $"{petName}.creature";
            if (ctx.Mod.Handles.TryGetValue(inner, out var known) && known is CreatureHandle same && ReferenceEquals(same.Block, r.Inline)) return same;
            return (CreatureHandle)Compiler.Define(ctx.Mod, inner, r.Inline!);
        }
        var colon = r.Name.IndexOf(':');
        if (colon > 0 && r.Name[..colon] == ctx.Mod.Id && ctx.Mod.Handles.TryGetValue(r.Name[(colon + 1)..], out var own) && own is CreatureHandle oc) return oc;
        var h = ctx.Find(r.Name, "creature");
        if (h is CreatureHandle ch) return ch;
        if (h is not null) throw new BlockError(ctx.Block, "creature", $"\"{r.Name}\" is a {h.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not a creature");
        throw new BlockError(ctx.Block, "creature", $"no creature block \"{r.Name}\" (a game creature is named by its creatures registry id: \"game:<name>\")");
    }

    /// <summary>A cost item that is not readable yet (a game client's OnLoad): resolved when the block is committed.</summary>
    internal const uint PendingItem = uint.MaxValue;

    private static ItemId? ItemIdOf(string name)
    {
        if (!Items.Available) return null;
        return Items.Lookup(name) is { } i ? i.Id : null;
    }

    private static void LateCost(ModBlocks mod, string block, string id, string itemName)
    {
        if (ItemIdOf(itemName) is not { } item || Kiln.Pets.Definition(id) is not { } d) return;
        Kiln.Pets.DefineAs(d with { Revive = d.Revive with { Cost = d.Revive.Cost! with { Item = item } } }, mod.Id);
    }
}
