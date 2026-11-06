// SPDX-License-Identifier: MIT
// Hair and beards: items of the game's own kind (category Customization, slot BodyPart_Hair or BodyPart_Beard), drawn on the head by every player's game from
// CharacterSetup.hair and .beard, as any worn item's look is (docs/re/building.md 3.2, 3.3). A look is a WornLook, cooked for the player's skeleton like a cosmetic's.
// The client's barber and character screens list the game's own (CharacterCustomizationResource in its UI data); Barber.Wear puts one on a character from the server.
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>A hair style: a <see cref="WornLook"/> on the head. A morph target named <c>Female</c> is its shape on a female head, and <c>Hat</c> and <c>Hood</c> the cut the game
/// raises under a hat or a hood. It is worn through <see cref="Barber.Wear(EntityId, BlockHandle)"/>.</summary>
public sealed record HairBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required PartRef<WornLook> Look { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }
    /// <summary>Game fields this block does not cover, set on the item it makes.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

/// <summary>A beard: a <see cref="WornLook"/> on the jaw. A morph target named <c>Female</c> is its shape on a female head, and <c>Jaw01</c>..<c>Jaw16</c> its shape on each of the
/// game's jaw shapes, which the character's head raises; <see cref="WornLook.Variants"/> take the same flags for whole models.</summary>
public sealed record BeardBlock : IBlock, ITweakable
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required PartRef<WornLook> Look { get; init; }
    /// <inheritdoc cref="WeaponBlock.Was"/>
    public string? Was { get; init; }
    /// <summary>Game fields this block does not cover, set on the item it makes.</summary>
    [JsonConverter(typeof(ItemTweakConverter))]
    public FieldValues Tweak { get; init; } = new();
}

internal enum BodyLook { Hair, Beard }

/// <summary>Hair and beards on characters, on the server world. A character's choice is <c>CharacterSetup</c> on its entity, saved with the character and sent to every player's game.</summary>
public static class Barber
{
    // keen::ecs::CharacterSetup on the builds Kiln knows. The size stands in for a layout check: a game update that moves these changes it, and these calls then decline.
    private const string Component = "CharacterSetup";
    private const int Size = 44, Hair = 0x0c, Beard = 0x10;

    /// <summary>Puts a hair or beard block on a character: a player, or a villager. False: it has no character setup, or this build's differs.</summary>
    public static bool Wear(EntityId character, BlockHandle look) => look switch
    {
        BlockHandle<HairBlock> => Write(character, Hair, look.Id),
        BlockHandle<BeardBlock> => Write(character, Beard, look.Id),
        _ => throw new ArgumentException($"{look} is no hair or beard", nameof(look)),
    };

    /// <summary>The same with an item id: a game hair or beard (<c>VC_Hair_*</c>, <c>VC_Beard_*</c>), or 0 for none.</summary>
    public static bool Wear(EntityId character, uint item, bool beard) => Write(character, beard ? Beard : Hair, item);

    /// <summary>The item ids a character wears as hair and beard. Null: it has no character setup.</summary>
    public static (uint Hair, uint Beard)? Of(EntityId character) =>
        World.ReadBytes(character, Component) is { Length: Size } b ? (BitConverter.ToUInt32(b, Hair), BitConverter.ToUInt32(b, Beard)) : null;

    private static bool Write(EntityId character, int offset, uint item) =>
        World.ComponentSize(character, Component) == Size && World.TryWrite(character, Component, offset, item);
}

internal static partial class Compiler
{
    // Items of the game's that are hair and beards, with no flags of their own.
    private const string HairStarter = "VC_Hair_04_01_pixie", BeardStarter = "VC_Beard01_full";

    private const Fit Jaws = Fit.Jaw01 | Fit.Jaw02 | Fit.Jaw03 | Fit.Jaw04 | Fit.Jaw05 | Fit.Jaw06 | Fit.Jaw07 | Fit.Jaw08 | Fit.Jaw09 | Fit.Jaw10 | Fit.Jaw11 | Fit.Jaw12 | Fit.Jaw13 | Fit.Jaw14 | Fit.Jaw15 | Fit.Jaw16;

    // The flags a hair or a beard model is chosen by (client 0xe8fe00): hair Female|Hat|Hood, a beard Female|Jaw01..16.
    private static Fit FitsOf(BodyLook kind) => kind == BodyLook.Hair ? Fit.Female | Fit.Hat | Fit.Hood : Fit.Female | Jaws;

    private static BlockHandle DefineBodyLook(BlockContext ctx, Lowered low, string name, string display, string? description, PartRef<WornLook> part, string? was, IBlock block, BodyLook kind)
    {
        RequireName(ctx, display, kind == BodyLook.Hair ? "a hair style" : "a beard");
        var key = was ?? name;
        var d = new Draft(ctx, low, key, name, kind == BodyLook.Hair ? HairStarter : BeardStarter);
        d.Rows = new JsonArray();
        var look = ctx.Part(part, "look");
        var owner = WornOwner(ctx, part);
        CheckWorn(ctx, look);
        if (look.Attach != null) throw new BlockError(ctx.Block, "look.attach", "hair and beards are skinned on the player's skeleton: weight the mesh to the head joint instead");
        if (look.Fits is { } raises && raises != Fit.None) throw new BlockError(ctx.Block, "look.fits", "hair and beards raise no flags: hats and hoods raise them for hair");
        var worn = new JsonObject();
        if (look.Like is { } like)
        {
            var n = ctx.ItemName(like, "look.like");
            d.Model = "item:" + n;
            d.Icon = "item:" + n;
            worn["like"] = n;
        }
        if (look.Model is { } model)
        {
            var m = WornModel(ctx, low, look, owner, model, "look.model", null, true);
            worn["model"] = m;
            if (m.StartsWith("item:", StringComparison.Ordinal))
            {
                d.Model = m;
                d.Icon ??= m;
            }
            else
            {
                d.Refs["equipment.visualModel"] = m;
                d.Refs["iconModel"] = m;
                d.VisualModels = [];
            }
        }
        var heard = FitsOf(kind);
        var variants = new List<(uint Mask, string Model)>();
        var files = new Dictionary<Fit, string>();
        if (look.Female is { } female) files[Fit.Female] = WornModel(ctx, low, look, owner, female, "look.female", null, false);
        foreach (var (fit, file) in look.Variants)
        {
            var p = $"look.variants[{FitConverter.Text(fit)}]";
            if (fit == Fit.None) throw new BlockError(ctx.Block, "look.variants", "a variant says which fit it is for: the plain case is model");
            if ((fit & ~heard) != 0) throw new BlockError(ctx.Block, p, $"{(kind == BodyLook.Hair ? "hair" : "a beard")} is never drawn for {string.Join(", ", FitNames(fit & ~heard))}, only for {string.Join(", ", FitNames(heard))}");
            if (files.ContainsKey(fit)) throw new BlockError(ctx.Block, p, "says the same as female");
            files[fit] = WornModel(ctx, low, look, owner, file, p, null, false);
        }
        if (files.Count > 0)
        {
            if (look.Model == null || look.Model.StartsWith("item:", StringComparison.Ordinal))
                throw new BlockError(ctx.Block, "look.model", "models for other fits need a model of the mod's own to be the plain case");
            // the client draws the first entry whose flags are exactly the raised ones: every set gets the most specific file that applies
            var bits = Enum.GetValues<Fit>().Where(f => f != Fit.None && (heard & f) != 0).ToArray();
            for (var set = 1; set < 1 << bits.Length; set++)
            {
                var mask = Fit.None;
                for (var b = 0; b < bits.Length; b++)
                    if ((set >> b & 1) != 0) mask |= bits[b];
                // a head has one jaw shape: sets of two or more never happen
                if (System.Numerics.BitOperations.PopCount((uint)(mask & Jaws)) > 1) continue;
                var best = files.Where(v => (v.Key & ~mask) == 0).OrderByDescending(v => System.Numerics.BitOperations.PopCount((uint)v.Key)).ThenByDescending(v => (v.Key & Fit.Female) != 0).ThenBy(v => (uint)v.Key).Select(v => v.Value).FirstOrDefault();
                if (best != null) variants.Add(((uint)mask, best));
            }
            d.VisualModels = variants;
            worn["visualModels"] = new JsonArray(variants.Select(v => (JsonNode)new JsonObject { ["mask"] = new JsonArray(FitNames((Fit)v.Mask).Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()), ["model"] = v.Model }).ToArray());
        }
        if (look.Hides is { Count: > 0 } hides)
        {
            var names = hides.Select(HideName).Distinct().ToArray();
            worn["hides"] = new JsonArray(names.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            SetFlags(d, "equipment.hideMask", names, d.Model?.StartsWith("item:", StringComparison.Ordinal) == true);
        }
        if (look.Icon is { } icon)
        {
            d.Icon = icon;
            worn["icon"] = icon;
            if (icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) ctx.Need(ClientsLevel.Mod, "look.icon", "a PNG icon has to be on every player's game: nothing can send it (an item's icon, \"item:<name>\", asks no more than kiln)");
        }
        low.Json["worn"] = worn;
        low.Json["bodyLook"] = kind == BodyLook.Hair ? "hair" : "beard";
        d.Commit(display, description);
        return block switch
        {
            HairBlock h => new BlockHandle<HairBlock>(ctx.Mod.Id, name, d.Guid, d.Game, h),
            _ => new BlockHandle<BeardBlock>(ctx.Mod.Id, name, d.Guid, d.Game, (BeardBlock)block),
        };
    }
}
