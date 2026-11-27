// SPDX-License-Identifier: MIT
// A taming, as a block: TamingBlock and its steps (TamingStepBlock), in C# and in blocks/*.json ("tamings" section). It
// compiles to the immutable Kiln.Taming TamingDefinition, the same record Taming.Define takes, so C# and JSON lower to one
// thing. Unlike Taming.DefineJson (the game's items by their registry ids) it names items as every block does: a game item
// or an item block of this mod or another, a creature block by its name, and a pet block. The standalone Kiln.Taming
// service stays as it is; this is a typed front-end over it. Every choice in it is the mod's. docs/kiln/modding/taming.md.
using System.Globalization;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>One step of a <see cref="TamingBlock"/>: <c>feed</c> (<see cref="Item"/>, <see cref="Count"/>), <c>weaken</c>
/// (<see cref="MaxHealthFraction"/>), <c>tool</c> (<see cref="Item"/>, <see cref="Wear"/>, <see cref="AllowBreak"/>) or
/// <c>native</c> (<see cref="Steps"/>, the game's own count). A field another kind has is refused by its path. In JSON:
/// <c>{ "kind": "feed", "item": "bait", "count": 2 }</c>. Since 0.2.0-dev.32.</summary>
/// <remarks>Make one with the factories (<see cref="Feed"/>, <see cref="Weaken"/>, <see cref="Tool"/>, <see cref="Native"/>); a block
/// is checked when the <see cref="TamingBlock"/> compiles, and a field that does not belong to the kind is refused by its path.
/// It lowers to the Kiln.Taming steps (<c>FeedStep</c>, <c>WeakenStep</c>, <c>UseToolStep</c>, <c>NativeStep</c>).</remarks>
/// <example><code>
/// TamingStepBlock[] steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed("Food_T1_Bait", 2), TamingStepBlock.Native()];
/// </code></example>
/// <seealso cref="TamingBlock.Steps"/>
public sealed record TamingStepBlock
{
    /// <summary><c>feed</c>, <c>weaken</c>, <c>tool</c> or <c>native</c>.</summary>
    public required string Kind { get; init; }
    /// <summary>feed: a plain stack item; tool: a gear item. A game item, or an item block of this mod (its name) or of
    /// another (<c>other-mod:name</c>).</summary>
    public ItemRef? Item { get; init; }
    /// <summary>feed: how many, 1..999 (default 1).</summary>
    public int? Count { get; init; }
    /// <summary>weaken: the target's health must be at most this share of its maximum, in (0, 1].</summary>
    public double? MaxHealthFraction { get; init; }
    /// <summary>tool: the durability one use takes, 0..32767 (default 1).</summary>
    public int? Wear { get; init; }
    /// <summary>tool: the use may break the tool (default false).</summary>
    public bool? AllowBreak { get; init; }
    /// <summary>native: how many of the game's own steps (a petting while it eats its bait), 1..255 (default 1).</summary>
    public int? Steps { get; init; }

    /// <summary>A feed step.</summary>
    /// <param name="item">A plain stack item (a game item, or an item block of this mod or another).</param>
    /// <param name="count">How many are taken from the slot the player selects: 1..999.</param>
    /// <returns>A <c>feed</c> step block.</returns>
    public static TamingStepBlock Feed(ItemRef item, int count = 1) => new() { Kind = "feed", Item = item, Count = count };
    /// <summary>A weaken step.</summary>
    /// <param name="maxHealthFraction">The most health the target may have as a share of its maximum, in (0, 1].</param>
    /// <returns>A <c>weaken</c> step block.</returns>
    public static TamingStepBlock Weaken(double maxHealthFraction) => new() { Kind = "weaken", MaxHealthFraction = maxHealthFraction };
    /// <summary>A tool step.</summary>
    /// <param name="item">A gear item (a game item, or an item block).</param>
    /// <param name="wear">The durability one use takes, 0..32767.</param>
    /// <param name="allowBreak">True: the use may break the tool; false: a use that would leave it below 1 durability is refused.</param>
    /// <returns>A <c>tool</c> step block.</returns>
    public static TamingStepBlock Tool(ItemRef item, int wear = 1, bool allowBreak = false) => new() { Kind = "tool", Item = item, Wear = wear, AllowBreak = allowBreak };
    /// <summary>A native step (the game's own count).</summary>
    /// <param name="steps">How many of the game's own steps (a petting while the animal eats its bait) it waits for: 1..255.</param>
    /// <returns>A <c>native</c> step block.</returns>
    public static TamingStepBlock Native(int steps = 1) => new() { Kind = "native", Steps = steps };
}

/// <summary>
/// A taming: which creatures it tames, its steps in order (the last one tames), the timeouts, the refund policy, and the pet
/// a tame makes. It compiles to a Kiln.Taming <see cref="TamingDefinition"/> with the id <c>&lt;mod id&gt;:&lt;name&gt;</c>;
/// <see cref="Taming.Begin"/> takes that id. Server only: it asks nothing of players' games. Since 0.2.0-dev.32.
/// </summary>
/// <remarks>Unlike <see cref="Taming.DefineJson"/> (which names the game's items by their items registry ids), a block names items as
/// every block does: a game item, or an item block of this mod or another; a creature block by its name; and a pet block. The
/// standalone Kiln.Taming service stays as it is; this is a typed front-end over it. In JSON (<c>blocks/*.json</c>, section
/// <c>tamings</c>) the fields are the same. Define tamings in <c>OnLoad</c>, through <c>Blocks.Define</c>; an item the game's
/// catalog does not have yet waits for it (about 2 minutes after content is ready) before it is called missing.</remarks>
/// <example><code>
/// var bait = Blocks.Define("wolf-bait", new FoodBlock { ... });
/// var pet = Blocks.Define("tame-wolf", new PetBlock { Creature = "game:enemy_wildbeast_wolf_earlygame" });
/// Blocks.Define("wolf", new TamingBlock
/// {
///     Creatures = ["game:enemy_wildbeast_wolf_earlygame"],
///     Steps = [TamingStepBlock.Weaken(0.4), TamingStepBlock.Feed(bait, 2)],
///     Pet = (BlockHandle&lt;PetBlock&gt;)pet,
///     Refund = TamingRefundPolicy.RefundOnFail,
/// });
/// </code></example>
/// <seealso cref="TamingDefinition"/>
/// <seealso cref="PetBlock"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/taming.md">Taming: the modder's guide</seealso>
[BlockClients(ClientsLevel.None)]
public sealed record TamingBlock : IBlock
{
    /// <summary>The creatures it tames: a game creature by its creatures registry id (<c>game:enemy_wildbeast_wolf_earlygame</c>;
    /// a trailing <c>*</c> matches a prefix of game ids), or a creature block of this mod (its name) or of another
    /// (<c>other-mod:name</c>). At least one.</summary>
    public required IReadOnlyList<string> Creatures { get; init; }
    /// <summary>The steps, 1..64, in order.</summary>
    public required IReadOnlyList<TamingStepBlock> Steps { get; init; }
    /// <summary>The pet a tame makes: a <see cref="PetBlock"/> (inline, a handle, its name, or <c>other-mod:name</c>, or the id
    /// of a pet a mod defined in code). Null: no pet; the tame is the attempt's own result.</summary>
    public PartRef<PetBlock>? Pet { get; init; }
    /// <summary>How close the player must be, 1..8 m (default 3).</summary>
    public double RangeMeters { get; init; } = 3;
    /// <summary>The latest weaken threshold holds until the tame (default true).</summary>
    public bool RequireThresholdUntilCommit { get; init; } = true;
    /// <summary>Seconds of simulation without an applied step before the attempt expires, 1..300 (default 30).</summary>
    public double InactivitySeconds { get; init; } = 30;
    /// <summary>The longest an attempt lasts, from <see cref="InactivitySeconds"/> to 3600 s (default 300).</summary>
    public double MaxAttemptSeconds { get; init; } = 300;
    /// <summary>What happens to paid steps when the attempt ends without a tame (default non-refundable).</summary>
    public TamingRefundPolicy Refund { get; init; } = TamingRefundPolicy.NonRefundable;
    /// <summary>With a pet: the tamed wild body is removed once the pet record is made (default true).</summary>
    public bool RemoveTarget { get; init; } = true;
    /// <summary>With a pet: the new pet is summoned at once (default true).</summary>
    public bool SummonPet { get; init; } = true;
    /// <summary>The name before a rename: the id comes from the oldest name, so a running server keeps naming it the same.</summary>
    public string? Was { get; init; }
}

internal static class TamingBlockJson
{
    public static readonly (string Section, Func<ModBlocks, string, System.Text.Json.JsonElement, BlockHandle> Make)[] Kinds =
    [
        ("tamings", BlockJson.Make<TamingBlock>),
    ];
}

internal static partial class Compiler
{
    internal static BlockHandle? DefineTamingBlock(BlockContext ctx, Lowered low, string name, IBlock block) =>
        block is TamingBlock t ? TamingCompiler.Define(ctx, low, name, t) : null;
}

internal static class TamingCompiler
{
    /// <summary>Registers a definition with Kiln.Taming as the mod's (tests replace it).</summary>
    internal static Action<TamingDefinition, string> Register { get; set; } = (d, mod) => TamingRuntime.Define(d, mod);

    /// <summary>Whether the game's item catalog is readable here now (tests replace it). On a dedicated server content is
    /// ready before Kiln.Items reads the catalog (lab 11-27: a game item was "not there" at content ready and readable 20 s
    /// later), so an item still unknown then waits for it instead of being called missing.</summary>
    internal static Func<bool> ItemsReadable { get; set; } = () => Items.Available && Items.All.Count > 0;

    /// <summary>How long a taming waits for the item catalog after content is ready, in service ticks (about 2 minutes).</summary>
    internal const int MaxWaitTicks = 3000;

    private static readonly object Gate = new();
    private static readonly List<(TamingDefinition Def, string Mod, string Block, List<(int Index, string Path, string Name, string Written, bool Gear)> Late, Action<string> Problem, int Ticks)> Waiting = new();
    private static IDisposable? _tick;

    /// <summary>The tamings waiting for the item catalog: resolved (or reported) once it is readable (service tick, every 25
    /// steps; tests call it).</summary>
    internal static void Pump()
    {
        var readable = ItemsReadable();
        var ready = new List<(TamingDefinition Def, string Mod, string Block, List<(int Index, string Path, string Name, string Written, bool Gear)> Late, Action<string> Problem, int Ticks)>();
        lock (Gate)
        {
            for (var i = 0; i < Waiting.Count; i++) Waiting[i] = Waiting[i] with { Ticks = Waiting[i].Ticks + 25 };
            ready.AddRange(Waiting.Where(w => readable || w.Ticks >= MaxWaitTicks));
            Waiting.RemoveAll(w => readable || w.Ticks >= MaxWaitTicks);
            if (Waiting.Count == 0) { _tick?.Dispose(); _tick = null; }
        }
        foreach (var (def, mod, block, late, problem, ticks) in ready)
        {
            if (ticks >= MaxWaitTicks && !ItemsReadable())
                problem($"block {block}: the game's items were not readable {MaxWaitTicks / 25} s after content was ready (the taming {def.Id} is not defined)");
            else Late(def, mod, block, late, problem);
        }
    }

    private static readonly string[] FeedFields = ["item", "count"], WeakenFields = ["maxHealthFraction"], ToolFields = ["item", "wear", "allowBreak"], NativeFields = ["steps"];

    public static BlockHandle Define(BlockContext ctx, Lowered low, string name, TamingBlock t)
    {
        var mod = ctx.Mod.Id;
        var key = t.Was ?? name;
        var id = $"{mod.ToLowerInvariant()}:{key.ToLowerInvariant()}";
        if (!PetDefinitionRules.ValidId(id)) throw new BlockError(ctx.Block, "name", $"\"{id}\" is not a taming id (lower case letters, digits, '-', '_', '.')");

        // the creatures: game ids pass as written; a creature block by its <mod>:<name>, registered with Kiln.Pets' resolver
        if (t.Creatures is not { Count: > 0 }) throw new BlockError(ctx.Block, "creatures", "a taming names the creatures it tames (a creatures registry id game:<name>, or a creature block)");
        var creatures = new List<string>();
        var blocks = new List<PetCreatureInfo>();
        for (var i = 0; i < t.Creatures.Count; i++)
        {
            var at = $"creatures[{i}]";
            var c = t.Creatures[i];
            if (string.IsNullOrWhiteSpace(c)) throw new BlockError(ctx.Block, at, "an empty creature");
            if (c.StartsWith("game:", StringComparison.Ordinal))
            {
                if (c != c.ToLowerInvariant()) throw new BlockError(ctx.Block, at, $"\"{c}\" is not a creatures registry id (lower case)");
                creatures.Add(c);
                continue;
            }
            if (c.EndsWith('*')) throw new BlockError(ctx.Block, at, $"\"{c}\": a prefix (*) names game creatures only (game:animal_wildlife_dog_*)");
            var (cid, info) = PetCompiler.CreatureOf(ctx, (PartRef<CreatureBlock>)c, $"{name}.creature{i}", at);
            creatures.Add(cid);
            if (info is not null) blocks.Add(info);
        }

        // the pet: a pet block (compiled now when written inline), or a pet id a mod defined in code
        string? pet = null;
        if (t.Pet is { } p)
        {
            if (p.Name is null) pet = Compiler.Define(ctx.Mod, $"{name}.pet", p.Inline!).GameName;
            else
            {
                var colon = p.Name.IndexOf(':');
                // another mod's name may be a pet block or a pet its code defined (Pets.Define): only a block is looked up
                var h = colon <= 0 ? ctx.Find(p.Name, "pet")
                    : p.Name[..colon] == ctx.Mod.Id && ctx.Mod.Handles.TryGetValue(p.Name[(colon + 1)..], out var own) ? own
                    : Blocks.Find(p.Name[(colon + 1)..], p.Name[..colon]);
                if (h is BlockHandle<PetBlock> ph) pet = ph.GameName;
                else if (h is not null) throw new BlockError(ctx.Block, "pet", $"\"{p.Name}\" is a {h.BlockType.Name.Replace("Block", "").ToLowerInvariant()}, not a pet");
                else if (colon > 0 && PetDefinitionRules.ValidId(p.Name)) pet = p.Name;   // a pet another mod defined in code
                else throw new BlockError(ctx.Block, "pet", $"no pet block \"{p.Name}\" (a pet of another mod is \"other-mod:name\")");
            }
        }

        // the steps: each kind takes its own fields; items by name, resolved to exact items now or once content is ready
        if (t.Steps is not { Count: > 0 and <= TamingEngine.MaxStepsPerDefinition }) throw new BlockError(ctx.Block, "steps", "a taming has 1 to 64 steps");
        var steps = new List<TamingStep>();
        var late = new List<(int Index, string Path, string Name, string Written, bool Gear)>();
        var lowered = new JsonArray();
        for (var i = 0; i < t.Steps.Count; i++)
        {
            var at = $"steps[{i}]";
            var s = t.Steps[i] ?? throw new BlockError(ctx.Block, at, "no step");
            var fields = s.Kind switch
            {
                "feed" => FeedFields,
                "weaken" => WeakenFields,
                "tool" => ToolFields,
                "native" => NativeFields,
                _ => throw new BlockError(ctx.Block, $"{at}.kind", $"\"{s.Kind}\" is not a step kind (feed, weaken, tool, native)"),
            };
            foreach (var (field, set) in new[] { ("item", s.Item is not null), ("count", s.Count is not null), ("maxHealthFraction", s.MaxHealthFraction is not null),
                         ("wear", s.Wear is not null), ("allowBreak", s.AllowBreak is not null), ("steps", s.Steps is not null) })
                if (set && Array.IndexOf(fields, field) < 0)
                    throw new BlockError(ctx.Block, $"{at}.{field}", $"not a field of a {s.Kind} step (fields: kind, {string.Join(", ", fields)})");
            var json = new JsonObject { ["kind"] = s.Kind };
            switch (s.Kind)
            {
                case "feed":
                {
                    var count = s.Count ?? 1;
                    if (count is < 1 or > 999) throw new BlockError(ctx.Block, $"{at}.count", $"{count} is outside 1..999");
                    var (item, written) = Item(ctx, s, at, gear: false, i, late);
                    steps.Add(new FeedStep(item, count));
                    json["item"] = written;
                    json["count"] = count;
                    break;
                }
                case "weaken":
                {
                    var f = s.MaxHealthFraction ?? throw new BlockError(ctx.Block, $"{at}.maxHealthFraction", "a weaken step needs maxHealthFraction in (0, 1]");
                    if (!(f > 0 && f <= 1)) throw new BlockError(ctx.Block, $"{at}.maxHealthFraction", $"{f.ToString(CultureInfo.InvariantCulture)} is outside (0, 1]");
                    steps.Add(new WeakenStep(f));
                    json["maxHealthFraction"] = f;
                    break;
                }
                case "tool":
                {
                    var wear = s.Wear ?? 1;
                    if (wear is < 0 or > ItemCosts.MaxWear) throw new BlockError(ctx.Block, $"{at}.wear", $"{wear} is outside 0..32767");
                    var (item, written) = Item(ctx, s, at, gear: true, i, late);
                    steps.Add(new UseToolStep(item, wear, s.AllowBreak ?? false));
                    json["item"] = written;
                    json["wear"] = wear;
                    json["allowBreak"] = s.AllowBreak ?? false;
                    break;
                }
                default:
                {
                    var n = s.Steps ?? 1;
                    if (n is < 1 or > 255) throw new BlockError(ctx.Block, $"{at}.steps", $"{n} is outside 1..255");
                    steps.Add(new NativeStep(n));
                    json["steps"] = n;
                    break;
                }
            }
            lowered.Add(json);
        }

        if (!(t.RangeMeters >= 1 && t.RangeMeters <= 8)) throw new BlockError(ctx.Block, "rangeMeters", "is 1 to 8");
        if (!(t.InactivitySeconds >= 1 && t.InactivitySeconds <= 300)) throw new BlockError(ctx.Block, "inactivitySeconds", "is 1 to 300");
        if (!(t.MaxAttemptSeconds >= t.InactivitySeconds && t.MaxAttemptSeconds <= 3600))
            throw new BlockError(ctx.Block, "maxAttemptSeconds", "is at least inactivitySeconds and at most 3600");
        if (!Enum.IsDefined(t.Refund)) throw new BlockError(ctx.Block, "refund", "not a refund policy (nonRefundable, refundOnFail, escrow)");

        var def = new TamingDefinition
        {
            Id = id, Steps = steps, Creatures = creatures, Pet = pet, RangeMeters = t.RangeMeters, RequireThresholdUntilCommit = t.RequireThresholdUntilCommit,
            InactivitySeconds = t.InactivitySeconds, MaxAttemptSeconds = t.MaxAttemptSeconds, Refund = t.Refund, RemoveTarget = t.RemoveTarget, SummonPet = t.SummonPet,
        };
        try { TamingEngine.Validate(def); }   // the engine's own rules, the same as Taming.Define's
        catch (ArgumentException e) { throw new BlockError(ctx.Block, null, e.Message.Replace(" (Parameter 'd')", "")); }

        low.Json["taming"] = new JsonObject
        {
            ["id"] = id, ["creatures"] = new JsonArray(creatures.Select(c => (JsonNode?)c).ToArray()), ["pet"] = pet, ["steps"] = lowered,
            ["rangeMeters"] = t.RangeMeters, ["requireThresholdUntilCommit"] = t.RequireThresholdUntilCommit, ["inactivitySeconds"] = t.InactivitySeconds,
            ["maxAttemptSeconds"] = t.MaxAttemptSeconds, ["refund"] = JsonNamingPolicyCamel(t.Refund), ["removeTarget"] = t.RemoveTarget, ["summonPet"] = t.SummonPet,
        };
        var owner = ctx.Mod;
        var blockName = ctx.Block;
        low.AfterCommit(() =>
        {
            foreach (var b in blocks) PetCreatures.RegisterBlock(b.Id, b.Variant!, b.Template, b.Id, b.LevelMax, b.DisplayName);
            if (late.Count == 0) Commit(def, mod, blockName);
            else BlockRuntime.AddReady(owner, problem => Late(def, mod, blockName, late, problem));
        });
        return new BlockHandle<TamingBlock>(mod, name, BlockIds.Derive(mod, "taming", key), id, t);
    }

    private static string JsonNamingPolicyCamel(TamingRefundPolicy p) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(p.ToString());

    // a step's item: its exact id now, or a placeholder resolved once content is ready (the SDK's bake and a game client's
    // OnLoad compile against a catalog without it, as a pet's revive cost)
    private static (ItemId Item, string Written) Item(BlockContext ctx, TamingStepBlock s, string at, bool gear, int index,
        List<(int Index, string Path, string Name, string Written, bool Gear)> late)
    {
        if (s.Item is not { } r || string.IsNullOrWhiteSpace(r.Value))
            throw new BlockError(ctx.Block, $"{at}.item", $"a {s.Kind} step names its item (a game item, or an item block)");
        var itemName = ctx.ItemName(r, $"{at}.item");
        var found = PetCompiler.FindItem(itemName);
        if (found is { } f && f.Gear != gear)
            throw new BlockError(ctx.Block, $"{at}.item", gear ? $"\"{r.Value}\" is a plain stack item; a tool step wears a gear item" : $"\"{r.Value}\" is gear; a feed step takes a plain stack item");
        if (found is null) late.Add((index, $"{at}.item", itemName, r.Value, gear));
        return (found?.Id ?? new ItemId(PetCompiler.PendingItem), itemName);
    }

    private static void Commit(TamingDefinition def, string mod, string block)
    {
        try { Register(def, mod); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            BlocksBridge.Log(LogLevel.Error, $"blocks: {mod}: {block}: Taming.Define refused it: {e.Message}");
        }
    }

    /// <summary>At content ready: the steps' items, or the mod's problem (the taming is then not defined: a Begin of it
    /// answers DefinitionUnknown, and nothing is ever taken for it).</summary>
    private static void Late(TamingDefinition def, string mod, string block, List<(int Index, string Path, string Name, string Written, bool Gear)> late, Action<string> problem)
    {
        if (!ItemsReadable())
        {
            // not readable yet: wait for the catalog rather than call the item missing
            lock (Gate)
            {
                Waiting.Add((def, mod, block, late, problem, 0));
                _tick ??= ServiceTick.Add("kiln.blocks tamings waiting for items", static (in TickInfo t) => { if (t.Step % 25 == 0) Pump(); });
            }
            return;
        }
        var steps = def.Steps.ToArray();
        foreach (var (index, path, itemName, written, gear) in late)
        {
            if (PetCompiler.FindItem(itemName) is not { } found)
            {
                problem($"block {block}, {path}: the game has no item \"{written}\" (the taming {def.Id} is not defined)");
                return;
            }
            if (found.Gear != gear)
            {
                problem($"block {block}, {path}: \"{written}\" is {(gear ? "a plain stack item; a tool step wears a gear item" : "gear; a feed step takes a plain stack item")} (the taming {def.Id} is not defined)");
                return;
            }
            steps[index] = steps[index] switch
            {
                FeedStep f => f with { Item = found.Id },
                UseToolStep u => u with { Item = found.Id },
                var other => other,
            };
        }
        Commit(def with { Steps = steps }, mod, block);
    }
}
