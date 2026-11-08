// SPDX-License-Identifier: MIT
// Loot tables as blocks: a table of what a creature, a container or your own code gives out, rolled by Kiln.
//
// The game's own drop tables (kiln.content "drops") are tables it already has: a mod can add a stack to one (an item's Loot.Tables) or scale its counts,
// and cannot make a new table. And the game rolls a creature's loot from its death sequence, which only runs near a player. A table of this kind is
// Kiln's: it is rolled on the server when a creature of the block dies (CreatureBlock.Drops), by code (LootTableHandle.Roll/Give/Drop), and by whatever
// later names it (a container's default loot). It needs no native code of its own: the stacks are dropped as loose pickups (kiln.spawn) or given into
// an inventory (kiln.items).
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// A table of what drops. Name it from a creature (<see cref="CreatureBlock.Drops"/>), or roll it yourself (<see cref="LootTableHandle.Roll"/>,
/// <see cref="LootTableHandle.Drop"/>, <see cref="LootTableHandle.Give"/>).
/// <code>
/// Blocks.Define("scrap-pile", new LootTableBlock
/// {
///     Entries = [new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 2, CountMax = 5 }, new LootEntry { Item = "ember-shard", Chance = 0.25 }],
/// });
/// Blocks.Define("scrapper", new CreatureBlock { Body = Bodies.Vukah, Drops = "scrap-pile" });
/// </code>
/// One roll goes through the entries by <see cref="Pick"/>; the table rolls <see cref="Rolls"/> times, and what comes up is added together per item.
/// Kiln rolls it, apart from the game's own loot: a creature that dies near a player also drops what the game gives it. The world's Loot Amount
/// setting does not scale it.
/// </summary>
[BlockClients(ClientsLevel.None)]
public sealed record LootTableBlock : IBlock
{
    /// <summary>What can come up. At least one.</summary>
    public required IReadOnlyList<LootEntry> Entries { get; init; }

    /// <summary>How many times the table rolls, 1..64.</summary>
    public int Rolls { get; init; } = 1;

    /// <summary>What one roll does: every entry tries its own chance, or one entry is picked by weight.</summary>
    public LootPick Pick { get; init; } = LootPick.Each;
}

/// <summary>One stack a <see cref="LootTableBlock"/> can give.</summary>
public sealed record LootEntry
{
    /// <summary>A <c>GameItems</c> constant, or an item of this mod by its block name (or <c>"other-mod:name"</c>).</summary>
    public required ItemRef Item { get; init; }

    /// <summary>How many, at least. 1..65535.</summary>
    public int CountMin { get; init; } = 1;

    /// <summary>How many, at most. Null: the same as <see cref="CountMin"/>.</summary>
    public int? CountMax { get; init; }

    /// <summary>0..1: how often this entry gives anything when it comes up. With <see cref="LootPick.One"/>, after it was picked.</summary>
    public double Chance { get; init; } = 1;

    /// <summary>With <see cref="LootPick.One"/>: how likely this entry is to be the one picked, among the table's. Not used by <see cref="LootPick.Each"/>.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>What one roll of a <see cref="LootTableBlock"/> does.</summary>
public enum LootPick
{
    /// <summary>Every entry rolls its own <see cref="LootEntry.Chance"/>: any number of them can give a stack.</summary>
    Each,
    /// <summary>One entry is picked by <see cref="LootEntry.Weight"/>, and its <see cref="LootEntry.Chance"/> says whether it gives anything: the game's selector group.</summary>
    One,
}

/// <summary>What a roll gave: an item, by the name the game and <c>Items.Give</c> know it by, and how many.</summary>
public readonly record struct LootStack(string Item, int Count);

/// <summary>What <see cref="Blocks.Define{T}"/> gives back for a loot table: roll it, drop it, give it.</summary>
public sealed class LootTableHandle : BlockHandle<LootTableBlock>
{
    internal LootTableHandle(string mod, string name, Guid guid, string gameName, LootTableBlock block, LootPlan plan) : base(mod, name, guid, gameName, block) => Plan = plan;

    internal LootPlan Plan { get; }

    /// <summary>One go at the table: what comes up, one stack per item. Pure: nothing is spawned or given. <paramref name="random"/>: a seeded one makes it repeat.</summary>
    public IReadOnlyList<LootStack> Roll(Random? random = null) => Plan.Roll(random ?? Random.Shared);

    /// <summary>Rolls it and drops what comes up as loose pickups around <paramref name="at"/> (the game's generic pickup: collected like loot). Tick thread. Not saved by the game.</summary>
    public Task<IReadOnlyList<EntityId>> Drop(WorldPosition at, Random? random = null) => LootRuntime.Drop(Plan, Plan.Roll(random ?? Random.Shared), at, $"blocks:{Mod}:{Name}");

    /// <summary>Rolls it and gives what comes up into an inventory (a chest, a player, a villager), as <see cref="Items.Give(EntityId, string, int, Gear?, GiveOptions)"/> does.
    /// What did not fit is in the results.</summary>
    public Task<IReadOnlyList<ItemsResult>> Give(EntityId inventory, Random? random = null, GiveOptions options = GiveOptions.None) =>
        LootRuntime.Give(Plan.Roll(random ?? Random.Shared), inventory, options);
}

/// <summary>A loot table as the runtime knows it: its items by the game's names, ready to roll.</summary>
internal sealed class LootPlan
{
    internal sealed record Entry(string Item, int Min, int Max, double Chance, double Weight);

    public required string Label { get; init; }
    public required IReadOnlyList<Entry> Entries { get; init; }
    public int Rolls { get; init; } = 1;
    public LootPick Pick { get; init; }

    /// <summary>Rolls the table, one stack per item (an item over 65535 is more than one stack: no pickup holds more).</summary>
    public IReadOnlyList<LootStack> Roll(Random rng)
    {
        var got = new List<(string Item, long Count)>();
        void Add(Entry e)
        {
            var n = e.Min == e.Max ? e.Min : rng.Next(e.Min, e.Max + 1);
            var i = got.FindIndex(x => x.Item == e.Item);
            if (i >= 0) got[i] = (e.Item, got[i].Count + n);
            else got.Add((e.Item, n));
        }
        bool Hit(Entry e) => e.Chance >= 1 || (e.Chance > 0 && rng.NextDouble() < e.Chance);
        for (var r = 0; r < Rolls; r++)
        {
            if (Pick == LootPick.Each)
            {
                foreach (var e in Entries)
                    if (Hit(e)) Add(e);
                continue;
            }
            var total = Entries.Sum(e => e.Weight);
            if (total <= 0) continue;
            var x = rng.NextDouble() * total;
            var picked = Entries[^1];
            foreach (var e in Entries)
            {
                if (x < e.Weight)
                {
                    picked = e;
                    break;
                }
                x -= e.Weight;
            }
            if (Hit(picked)) Add(picked);
        }
        var stacks = new List<LootStack>();
        foreach (var (item, count) in got)
            for (var left = count; left > 0; left -= 65535) stacks.Add(new LootStack(item, (int)Math.Min(left, 65535)));
        return stacks;
    }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["rolls"] = Rolls,
            ["pick"] = Pick == LootPick.One ? "one" : "each",
            ["entries"] = new JsonArray(Entries.Select(e => (JsonNode)new JsonObject { ["item"] = e.Item, ["countMin"] = e.Min, ["countMax"] = e.Max, ["chance"] = e.Chance, ["weight"] = e.Weight }).ToArray()),
        };
        return o;
    }
}

internal static partial class Compiler
{
    // The items it names are the game's, or this mod's; a name no block of the mod and no game item has is looked for once content is ready (below).
    internal static LootPlan LootPlanOf(BlockContext ctx, LootTableBlock t, string path)
    {
        if (t.Entries is not { Count: > 0 } entries) throw new BlockError(ctx.Block, $"{path}entries", "a loot table with no entries drops nothing: give it at least one");
        if (t.Rolls is < 1 or > 64) throw new BlockError(ctx.Block, $"{path}rolls", $"{t.Rolls} is outside 1..64");
        var list = new List<LootPlan.Entry>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i] ?? throw new BlockError(ctx.Block, $"{path}entries[{i}]", "an entry that is empty");
            var p = $"{path}entries[{i}]";
            if (string.IsNullOrWhiteSpace(e.Item.Value)) throw new BlockError(ctx.Block, $"{p}.item", "an entry needs an item: a GameItems constant, or an item of this mod");
            var max = e.CountMax ?? e.CountMin;
            if (e.CountMin is < 1 or > 65535) throw new BlockError(ctx.Block, $"{p}.countMin", $"{e.CountMin} is outside 1..65535");
            if (max < e.CountMin) throw new BlockError(ctx.Block, $"{p}.countMax", $"{max} is less than countMin {e.CountMin}");
            if (max > 65535) throw new BlockError(ctx.Block, $"{p}.countMax", $"{max} is outside 1..65535");
            if (!double.IsFinite(e.Chance) || e.Chance is < 0 or > 1) throw new BlockError(ctx.Block, $"{p}.chance", $"{e.Chance} is outside 0..1");
            if (!double.IsFinite(e.Weight) || e.Weight < 0) throw new BlockError(ctx.Block, $"{p}.weight", $"{e.Weight} is less than 0");
            list.Add(new LootPlan.Entry(ctx.ItemName(e.Item, $"{p}.item"), e.CountMin, max, e.Chance, e.Weight));
        }
        if (t.Pick == LootPick.One && list.All(e => e.Weight <= 0)) throw new BlockError(ctx.Block, $"{path}entries", "pick \"one\" chooses by weight, and every weight is 0");
        return new LootPlan { Label = ctx.Block, Entries = list, Rolls = t.Rolls, Pick = t.Pick };
    }

    /// <summary>What a field that takes a loot table means: a block of a mod (its own plan, whichever mod wrote it), or one written inline. Every item it names is looked for once content is ready.</summary>
    internal static LootPlan LootPlanOf(BlockContext ctx, Lowered low, PartRef<LootTableBlock> r, string path)
    {
        if (r.Name != null && ctx.Find(r.Name, path) is LootTableHandle h) return h.Plan;
        var table = ctx.Part(r, path);
        var plan = LootPlanOf(ctx, table, $"{path}.");
        LootChecks(ctx, low, plan);
        return plan;
    }

    private static BlockHandle DefineLootTable(BlockContext ctx, Lowered low, string name, LootTableBlock t)
    {
        var plan = LootPlanOf(ctx, t, "");
        LootChecks(ctx, low, plan);
        low.Json["lootTable"] = plan.ToJson();
        return new LootTableHandle(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "loot", name), $"{ctx.Mod.Id}.{name}", t, plan);
    }

    // The game's items are known when content is ready: an entry naming one that is not there is said once, by block and entry.
    private static void LootChecks(BlockContext ctx, Lowered low, LootPlan plan)
    {
        var mod = ctx.Mod;
        var block = ctx.Block;
        low.AfterCommit(() => BlockRuntime.AddReady(mod, problem =>
        {
            for (var i = 0; i < plan.Entries.Count; i++)
                if (LootRuntime.IdOf(plan.Entries[i].Item) == null)
                    problem($"block {block}, entries[{i}].item: the game has no item \"{plan.Entries[i].Item}\"");
        }));
    }

    // ---- a recipe of its own ------------------------------------------------------------------------------------

    /// <summary>A recipe that is not on an item: <c>output</c> says what it makes, <c>station</c> where (the game's workbench, a mod's station).</summary>
    private static BlockHandle DefineRecipeBlock(BlockContext ctx, Lowered low, string name, RecipeBlock r)
    {
        var output = r.Output ?? throw new BlockError(ctx.Block, "output", "a recipe block says what it makes: output is a GameItems constant, or an item of this mod");
        var item = ctx.ItemName(output, "output");
        var key = $"recipes/{name}";
        try
        {
            // the lowering names fields as an item's recipe has them: recipe.needs
            LowerRecipe(ctx, low, key, $"{ctx.Mod.Id}.{name}", item, r with { Output = null }, "recipe", null, false, r.Sheltered);
        }
        catch (BlockError e) when (e.Field is { } f && f.StartsWith("recipe.", StringComparison.Ordinal))
        {
            throw new BlockError(ctx.Block, f["recipe.".Length..], e.Problem);
        }
        low.Json["recipeOf"] = item;
        return new BlockHandle<RecipeBlock>(ctx.Mod.Id, name, BlockIds.Derive(ctx.Mod.Id, "recipe", key), $"{ctx.Mod.Id}.{name}", r);
    }
}

/// <summary>What loot tables need while the game runs: ids of items, a creature's death, and the stacks going out.</summary>
internal static class LootRuntime
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (string Name, LootPlan Plan)> ByVariant = new(StringComparer.Ordinal);
    private static IDisposable? _tick, _deaths;
    private static readonly Dictionary<string, uint> Ids = new(StringComparer.Ordinal);

    /// <summary>Stacks dropped and creatures that dropped, since start (for tests and the lab).</summary>
    internal static long Dropped, Deaths;

    /// <summary>The game's id of an item by the name it has there, once the item exists (the game's, or a mod's that is registered). Null: not now. Tick thread.</summary>
    internal static Func<string, uint?> Resolver = name =>
    {
        try
        {
            if (Content.Items.Get(name) is { } entry) return entry.Id;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException) { }
        return Items.Available && Items.Lookup(name) is { } info ? info.Id.Value : null;
    };

    internal static uint? IdOf(string name)
    {
        lock (Gate)
            if (Ids.TryGetValue(name, out var known))
                return known;
        var id = Resolver(name);
        if (id is { } found)
            lock (Gate) Ids[name] = found;
        return id;
    }

    // ---- creatures ----

    /// <summary>A creature block's drops: rolled where one of its creatures dies. Replaces what an earlier definition of the variant had.</summary>
    internal static void Register(string variant, string name, LootPlan? plan)
    {
        lock (Gate)
        {
            if (plan == null) ByVariant.Remove(variant);
            else ByVariant[variant] = (name, plan);
            if (plan != null) _tick ??= BlocksBridge.AddTick("kiln blocks loot", OnTick);
        }
    }

    // The subscription is made here, from the service's own tick: one made while a mod's code runs would end with the mod.
    private static void OnTick(ulong step)
    {
        if (_deaths != null || step % 10 != 7) return;
        lock (Gate)
        {
            if (_deaths != null) return;
            _deaths = Events.On<EntityDeathEvent>(OnDeath);
        }
    }

    private static void OnDeath(EntityDeathEvent e)
    {
        if (e.IsPlayer) return;
        (string Name, LootPlan Plan) drops;
        lock (Gate)
            if (ByVariant.Count == 0 || CreatureVariants.Of(e.Victim) is not { } v || !ByVariant.TryGetValue(v.Id, out drops))
                return;
        Interlocked.Increment(ref Deaths);
        var stacks = drops.Plan.Roll(Random.Shared);
        var text = stacks.Count == 0 ? "nothing" : string.Join(", ", stacks.Select(s => $"{s.Item} x{s.Count}"));
        BlocksBridge.Log(LogLevel.Info, $"blocks: {drops.Name} died at {e.Position}: drops {text}");
        if (stacks.Count == 0) return;
        _ = Drop(drops.Plan, stacks, e.Position, $"blocks:{drops.Name}");
    }

    // ---- going out ----

    internal static async Task<IReadOnlyList<EntityId>> Drop(LootPlan plan, IReadOnlyList<LootStack> stacks, WorldPosition at, string tag)
    {
        var made = new List<EntityId>();
        for (var i = 0; i < stacks.Count; i++)
        {
            var s = stacks[i];
            if (IdOf(s.Item) is not { } id)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {plan.Label}: the game has no item \"{s.Item}\": nothing dropped for it");
                continue;
            }
            // around the place, not on one spot: they would stand inside each other
            var angle = i * 2.399963;
            var spot = stacks.Count == 1 ? at : new WorldPosition(at.X + Math.Cos(angle) * 0.6, at.Y + 0.3, at.Z + Math.Sin(angle) * 0.6);
            try
            {
                made.Add(await Spawn.Item(id, s.Count, spot, options: new SpawnOptions { Tag = tag }));
                Interlocked.Increment(ref Dropped);
            }
            catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: {plan.Label}: {s.Item} x{s.Count} did not drop: {e.Message}");
            }
        }
        return made;
    }

    internal static async Task<IReadOnlyList<ItemsResult>> Give(IReadOnlyList<LootStack> stacks, EntityId inventory, GiveOptions options)
    {
        var results = new List<ItemsResult>();
        foreach (var s in stacks) results.Add(await Items.Give(inventory, s.Item, s.Count, options: options));
        return results;
    }
}
