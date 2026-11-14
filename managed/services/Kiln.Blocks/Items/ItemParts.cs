// SPDX-License-Identifier: MIT
// Resolving what a block names, whichever way it was written: a part inline or by handle, name or a premade's key; an item of this mod, another mod's, or the game's.
using System.Reflection;

namespace Kiln;

internal static class ItemParts
{
    private static IEnumerable<T> Premades<T>() where T : class =>
        typeof(T).GetField("All", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as T[] ?? [];

    private static string KindOf(Type t) => t.Name.Replace("Block", "").ToLowerInvariant();

    /// <summary>A block by name, compiling it first when a data file defines it later. As BlockContext's own, for every kind of block.</summary>
    public static BlockHandle? Find(this BlockContext ctx, string name, string path)
    {
        var colon = name.IndexOf(':');
        if (colon > 0)
            return Blocks.Find(name[(colon + 1)..], name[..colon])
                   ?? throw new BlockError(ctx.Block, path, $"no block \"{name[(colon + 1)..]}\" in mod {name[..colon]} (is it loaded before this one?)");
        if (ctx.Mod.TryHandle(name, out var h)) return h;
        var authored = ctx.Mod.Authored;
        if (authored.Pending.TryGetValue(name, out var pending))
        {
            if (!authored.Compiling.Add(name)) throw new BlockError(ctx.Block, path, $"\"{name}\" refers back to itself");
            try { return pending(); }
            finally { authored.Compiling.Remove(name); }
        }
        return null;
    }

    /// <summary>What a part-typed field means.</summary>
    public static T Part<T>(this BlockContext ctx, PartRef<T> r, string path) where T : class, IBlock
    {
        if (r.Name == null) return r.Inline!;
        foreach (var p in Premades<T>())
            if (p is IPremade m && m.Key == r.Name) return p;
        var h = ctx.Find(r.Name, path);
        if (h is BlockHandle<T> typed) return typed.Block;
        if (h != null) throw new BlockError(ctx.Block, path, $"\"{r.Name}\" is a {KindOf(h.BlockType)}; this field takes a {KindOf(typeof(T))}");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<T>).Select(x => x.Key);
        var keys = Premades<T>().OfType<IPremade>().Select(p => p.Key).Concat(own).Order();
        throw new BlockError(ctx.Block, path, $"no {KindOf(typeof(T))} \"{r.Name}\"; there are: {string.Join(", ", keys)}");
    }

    /// <summary>An item's name in the game: a block of this mod or another's, or a game item, as written.</summary>
    public static string ItemName(this BlockContext ctx, ItemRef r, string path)
    {
        var h = ctx.Find(r.Value, path);
        if (h == null) return r.Value;
        if (h is BlockHandle<EffectBlock> or BlockHandle<StatusBlock> or BlockHandle<VfxBlock> or BlockHandle<VisualBlock> or BlockHandle<OnHitBlock> or BlockHandle<WeaponStats> or BlockHandle<ArmorStats> or BlockHandle<WeaponClass> or BlockHandle<ArmorSlot> or BlockHandle<ArmorSet> or BlockHandle<WornLook> or BlockHandle<PlacementBlock> or BlockHandle<ComfortBlock> or BlockHandle<ProcessingBlock> or BlockHandle<MenuBlock> or BlockHandle<PerkBlock> or BlockHandle<AbilityBlock> or BlockHandle<TalentBlock> or BlockHandle<TalentTreeBlock> or BlockHandle<TriggerBlock> or BlockHandle<LootTableBlock> or BlockHandle<RecipeBlock> or BlockHandle<QuestBlock> or BlockHandle<DialogueBlock> or BlockHandle<WindowBlock> or BlockHandle<HudLineBlock> or BlockHandle<KeyBindingBlock> or BlockHandle<AffixBlock> or BlockHandle<AffixPoolBlock>)
            throw new BlockError(ctx.Block, path, $"\"{r.Value}\" is a {KindOf(h.BlockType)}, not an item");
        return h.GameName;
    }

    /// <summary>The effects a hit field means. A name is an on-hit block or a single effect.</summary>
    public static IReadOnlyList<EffectRef> Hits(this BlockContext ctx, HitsRef? r, string path)
    {
        if (r == null) return [];
        if (r.Part is not { } part) return r.ToList();
        if (part.Name is { } name && ctx.Find(name, path) is BlockHandle<EffectBlock>) return [(EffectRef)name];
        var block = ctx.Part(part, path);
        // shown where a hit lands: the same as an effect that only shows it
        return block.Vfx == null ? block.Effects : [.. block.Effects, new EffectBlock { Vfx = block.Vfx }];
    }

    /// <summary>Where a recipe is crafted: what kiln.content takes for it, the menu that lists it when that isn't the workshop's own, and with a workshop of a station's own, the item
    /// that places the station.</summary>
    public static (string Station, string? Menu, string? Item) StationOf(this BlockContext ctx, StationRef r, string path)
    {
        var v = r.Value;
        if (v == Stations.Hand || v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (v, null, null);
        var h = ctx.Find(v, path);
        if (h is StationHandle s) return (s.Station, s.Menu, s.Item);
        if (h != null) throw new BlockError(ctx.Block, path, $"\"{v}\" is a {KindOf(h.BlockType)}, not a station");
        var own = ctx.Mod.Handles.Where(x => x.Value is StationHandle).Select(x => x.Key).Order();
        throw new BlockError(ctx.Block, path, $"\"{v}\" is not a station: say \"hand\", a workshop's 0x id (GameWorkshops), or a station block; this mod has: {string.Join(", ", own)}");
    }
}
