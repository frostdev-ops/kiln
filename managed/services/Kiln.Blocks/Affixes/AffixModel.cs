// SPDX-License-Identifier: MIT
// Affixes as blocks (dev.27): what an author writes. An affix is one kind of rolled modifier (its stat, its tiers and
// ranges); a pool says which items roll which affixes and how many. The rolls of one item are item data (Kiln.Saves'
// InstanceData), and Kiln applies them while the item is worn or in hand (AffixRuntime.cs). docs/kiln/modding/affixes.md.
using System.Globalization;

namespace Kiln;

/// <summary>
/// One kind of rolled modifier on an item: "of the Bear, +3 to +8 Strength". Define it once; a roll picks a tier and a
/// value in the tier's range, and that value is what the one item carries.
/// <code>
/// var bear = Affixes.Define("bear", new AffixBlock
/// {
///     Name = "of the Bear", Stat = Stat.Strength,
///     Tiers = [new AffixTier { Min = 1, Max = 3 }, new AffixTier { Min = 4, Max = 8, Weight = 0.25 }],
/// });
/// </code>
/// </summary>
public sealed record AffixBlock
{
    /// <summary>What players read: "of the Bear", "Fleet". Kept with every roll, so a game without the mod can still show it.</summary>
    public required string Name { get; init; }

    /// <summary>The stat it changes while its item is worn or in hand: a <see cref="Kiln.Stat"/> constant, or
    /// <c>new Stat("my-key", guid, Whole: true)</c> for another attribute of the game.</summary>
    public required Stat Stat { get; init; }

    /// <summary>Its tiers, the weakest first: tier 1 is <c>Tiers[0]</c>. At least one, at most 16.</summary>
    public required IReadOnlyList<AffixTier> Tiers { get; init; }

    /// <summary>What two worn items with this affix do together: their values add up (true, the default), or only the
    /// highest one counts (false).</summary>
    public bool Stacks { get; init; } = true;

    /// <summary>Affixes with the same group never roll onto one item together ("prefix", "element"). Null: no group.</summary>
    public string? Group { get; init; }
}

/// <summary>One tier of an <see cref="AffixBlock"/>: the range a roll of this tier takes its value from.</summary>
public sealed record AffixTier
{
    /// <summary>The lowest value: whole points for a whole-number stat (<c>Stat.Strength</c>: 3), a share for the others
    /// (<c>Stat.MoveSpeed</c>: 0.05 is 5 %). Negative values make a curse. Never 0.</summary>
    public required double Min { get; init; }

    /// <summary>The highest value; at least <see cref="Min"/>, and of the same sign.</summary>
    public required double Max { get; init; }

    /// <summary>The grid a rolled value lies on. Null: 1 for a whole-number stat, 0.01 (one percent) for a share.</summary>
    public double? Step { get; init; }

    /// <summary>How likely a roll of the affix takes this tier, among the tiers the item's level allows.</summary>
    public double Weight { get; init; } = 1;

    /// <summary>The item level from which this tier can roll (0: any).</summary>
    public int MinItemLevel { get; init; }
}

/// <summary>
/// Which items roll which affixes, and how many.
/// <code>
/// var blades = Affixes.DefinePool("blades", new AffixPoolBlock
/// {
///     Items = [GameItems.Weapon_T1_1H_SwordEpic_01],
///     Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "fleet", Weight = 2 }, new AffixEntry { Affix = "ember-ward" }],
/// });
/// AffixResult rolled = blades.Roll(sword);   // one of the three, on this one sword
/// </code>
/// </summary>
public sealed record AffixPoolBlock
{
    /// <summary>The item types that roll from this pool: <c>GameItems</c> constants, or items of this mod by their block
    /// name. Null: any item with an item entity of its own (gear, tools).</summary>
    public IReadOnlyList<ItemRef>? Items { get; init; }

    /// <summary>What can come up. At least one.</summary>
    public required IReadOnlyList<AffixEntry> Affixes { get; init; }

    /// <summary>How many different affixes a roll gives at least (0 to 8).</summary>
    public int Min { get; init; } = 1;

    /// <summary>How many at most; at least <see cref="Min"/>, at most 8.</summary>
    public int Max { get; init; } = 1;
}

/// <summary>One affix a pool can give.</summary>
public sealed record AffixEntry
{
    /// <summary>An affix of this mod by its name, or <c>"other-mod:name"</c>.</summary>
    public required string Affix { get; init; }

    /// <summary>How likely it is to come up, among the pool's.</summary>
    public double Weight { get; init; } = 1;
}

/// <summary>One affix as an item carries it: which affix, which tier, and the rolled value.</summary>
/// <param name="Affix">The affix: <c>"mod:name"</c>.</param>
/// <param name="Name">Its display name, as it was when rolled.</param>
/// <param name="Stat">The stat's key (<c>"strength"</c>, <c>"move-speed"</c>, or a GUID for an attribute of the mod's own).</param>
/// <param name="Tier">The tier, from 1.</param>
/// <param name="Value">The value: whole points, or a share (0.05 is 5 %).</param>
public sealed record RolledAffix(string Affix, string Name, string Stat, int Tier, double Value)
{
    /// <summary>The line a player reads: "+5 Strength (of the Bear)", "+5% Move Speed (Fleet)".</summary>
    public string Text => $"{ValueText} {StatLabel(Stat)} ({Name})";

    /// <summary>The value alone: "+5", "-3", "+5%".</summary>
    public string ValueText
    {
        get
        {
            var whole = Kiln.Stat.All.FirstOrDefault(s => s.Key == Stat)?.Whole ?? Value == Math.Floor(Value) && Math.Abs(Value) >= 1;
            var sign = Value < 0 ? "-" : "+";
            return whole
                ? sign + Math.Abs(Value).ToString("0", CultureInfo.InvariantCulture)
                : sign + (Math.Abs(Value) * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%";
        }
    }

    /// <summary>"move-speed" as "Move Speed".</summary>
    internal static string StatLabel(string key) => Guid.TryParse(key, out _)
        ? key
        : string.Join(' ', key.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>The answer of a call that changes an item's affixes. <see cref="Ok"/> false, with a <see cref="Reason"/>, when
/// the rules said no (the pool is not for this item, the item already has affixes, it would carry more than 8); nothing
/// changed then.</summary>
[ServerOnly]
public sealed record AffixResult : IKilnResult
{
    internal AffixResult(ItemInstanceRef item, IReadOnlyList<RolledAffix> affixes, string? reason)
    {
        Item = item;
        Affixes = affixes;
        Reason = reason;
    }

    /// <summary>The item.</summary>
    public ItemInstanceRef Item { get; }

    /// <summary>The item's affixes after the call (when not <see cref="Ok"/>: as they were).</summary>
    public IReadOnlyList<RolledAffix> Affixes { get; }

    /// <summary>True when the change was made.</summary>
    public bool Ok => Reason is null;

    /// <summary>Why not; null when <see cref="Ok"/>.</summary>
    public string? Reason { get; }

    /// <summary>The result in one line, for a log.</summary>
    public override string ToString() => Ok
        ? $"{Item}: {(Affixes.Count == 0 ? "no affixes" : string.Join(", ", Affixes.Select(a => a.Text)))}"
        : $"{Item}: not changed: {Reason}";
}

/// <summary>An item's affixes changed through <see cref="Kiln.Affixes"/> (a roll, an add, a set, a removal). Server, tick
/// thread, one event per successful call.</summary>
[ServerOnly]
public sealed class AffixesChangedEvent : KilnEvent
{
    internal AffixesChangedEvent(ItemInstanceRef item, IReadOnlyList<RolledAffix> before, IReadOnlyList<RolledAffix> now)
    {
        Item = item;
        Before = before;
        Now = now;
    }

    /// <summary>The item.</summary>
    public ItemInstanceRef Item { get; }

    /// <summary>Its affixes before the call.</summary>
    public IReadOnlyList<RolledAffix> Before { get; }

    /// <summary>Its affixes now.</summary>
    public IReadOnlyList<RolledAffix> Now { get; }
}

/// <summary>One stat change Kiln holds on a player (or a lab's stand-in) for the affixes of what is worn or in hand.</summary>
/// <param name="Affix">The affix: <c>"mod:name"</c>.</param>
/// <param name="Stat">The stat's key.</param>
/// <param name="Value">The value applied: one item's, or for an affix that does not stack the highest of the worn items'.</param>
/// <param name="Item">The item it comes from; <see cref="EntityId.None"/> for an affix that does not stack.</param>
public sealed record ActiveAffix(string Affix, string Stat, double Value, EntityId Item);

/// <summary>What Kiln applies to a player for worn and held affixes changed: an item was put on or taken off, its
/// affixes changed, or the player left (then <see cref="Now"/> is empty). Server, tick thread.</summary>
[ServerOnly]
public sealed class AffixesAppliedEvent : KilnEvent
{
    internal AffixesAppliedEvent(EntityId wearer, IReadOnlyList<ActiveAffix> before, IReadOnlyList<ActiveAffix> now)
    {
        Wearer = wearer;
        Before = before;
        Now = now;
    }

    /// <summary>The player.</summary>
    public EntityId Wearer { get; }

    /// <summary>What was applied before.</summary>
    public IReadOnlyList<ActiveAffix> Before { get; }

    /// <summary>What is applied now.</summary>
    public IReadOnlyList<ActiveAffix> Now { get; }
}

/// <summary>What <see cref="Affixes.Define"/> gives back.</summary>
public sealed class AffixHandle
{
    internal AffixHandle(string mod, string name, AffixBlock block)
    {
        Mod = mod;
        Name = name;
        Block = block;
    }

    /// <summary>The mod that defined it.</summary>
    public string Mod { get; }

    /// <summary>Its name within the mod.</summary>
    public string Name { get; }

    /// <summary><c>"mod:name"</c>: what <see cref="RolledAffix.Affix"/> holds.</summary>
    public string Id => $"{Mod}:{Name}";

    /// <summary>The definition.</summary>
    public AffixBlock Block { get; }

    /// <inheritdoc cref="Id"/>
    public override string ToString() => Id;
}

/// <summary>What <see cref="Affixes.DefinePool"/> gives back: roll it onto an item.</summary>
public sealed class AffixPoolHandle
{
    internal AffixPoolHandle(string mod, string name, AffixPoolBlock block)
    {
        Mod = mod;
        Name = name;
        Block = block;
    }

    /// <summary>The mod that defined it.</summary>
    public string Mod { get; }

    /// <summary>Its name within the mod.</summary>
    public string Name { get; }

    /// <summary><c>"mod:name"</c>.</summary>
    public string Id => $"{Mod}:{Name}";

    /// <summary>The definition.</summary>
    public AffixPoolBlock Block { get; }

    /// <summary>Rolls the pool onto <paramref name="item"/>: <see cref="Affixes.Roll(ItemInstanceRef, AffixPoolHandle, Random?, bool)"/>.</summary>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Affixes.Capability)]
    public AffixResult Roll(ItemInstanceRef item, Random? random = null, bool replace = false) => Affixes.Roll(item, this, random, replace);

    /// <inheritdoc cref="Id"/>
    public override string ToString() => Id;
}
