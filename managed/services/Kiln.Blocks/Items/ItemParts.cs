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
        if (ctx.Mod.Handles.TryGetValue(name, out var h)) return h;
        if (ctx.Mod.Pending.TryGetValue(name, out var pending))
        {
            if (!ctx.Mod.Compiling.Add(name)) throw new BlockError(ctx.Block, path, $"\"{name}\" refers back to itself");
            try { return pending(); }
            finally { ctx.Mod.Compiling.Remove(name); }
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
        if (h != null) throw new BlockError(ctx.Block, path, $"\"{r.Name}\" is a {KindOf(h.GetType().GenericTypeArguments[0])}; this field takes a {KindOf(typeof(T))}");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<T>).Select(x => x.Key);
        var keys = Premades<T>().OfType<IPremade>().Select(p => p.Key).Concat(own).Order();
        throw new BlockError(ctx.Block, path, $"no {KindOf(typeof(T))} \"{r.Name}\"; there are: {string.Join(", ", keys)}");
    }

    /// <summary>An item's name in the game: a block of this mod or another's, or a game item, as written.</summary>
    public static string ItemName(this BlockContext ctx, ItemRef r, string path)
    {
        var h = ctx.Find(r.Value, path);
        if (h == null) return r.Value;
        if (h is BlockHandle<EffectBlock> or BlockHandle<StatusBlock> or BlockHandle<VisualBlock> or BlockHandle<OnHitBlock> or BlockHandle<WeaponStats> or BlockHandle<ArmorStats> or BlockHandle<WeaponClass> or BlockHandle<ArmorSlot> or BlockHandle<ArmorSet>)
            throw new BlockError(ctx.Block, path, $"\"{r.Value}\" is a {KindOf(h.GetType().GenericTypeArguments[0])}, not an item");
        return h.GameName;
    }

    /// <summary>The effects a hit field means. A name is an on-hit block or a single effect.</summary>
    public static IReadOnlyList<EffectRef> Hits(this BlockContext ctx, HitsRef? r, string path)
    {
        if (r == null) return [];
        if (r.Part is not { } part) return r.ToList();
        if (part.Name is { } name && ctx.Find(name, path) is BlockHandle<EffectBlock>) return [(EffectRef)name];
        var block = ctx.Part(part, path);
        if (block.Vfx != null) throw Compiler.NoVfxYet(ctx, $"{path}.vfx");
        return block.Effects;
    }

    /// <summary>Where a recipe is crafted: what kiln.content takes for it.</summary>
    public static (uint Workshop, string Station) Station(this BlockContext ctx, StationRef r, string path)
    {
        var v = r.Value;
        if (v == Stations.Hand) return (0, v);
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return (Convert.ToUInt32(v, 16), v);
        var h = ctx.Find(v, path);
        if (h is BlockHandle<StationBlock> s) return (Convert.ToUInt32(s.Block.Kind.Workshop, 16), s.Block.Kind.Workshop);
        if (h != null) throw new BlockError(ctx.Block, path, $"\"{v}\" is a {KindOf(h.GetType().GenericTypeArguments[0])}, not a station");
        var own = ctx.Mod.Handles.Where(x => x.Value is BlockHandle<StationBlock>).Select(x => x.Key).Order();
        throw new BlockError(ctx.Block, path, $"\"{v}\" is not a station: say \"hand\", a workshop's 0x id (GameWorkshops), or a station block; this mod has: {string.Join(", ", own)}");
    }
}
