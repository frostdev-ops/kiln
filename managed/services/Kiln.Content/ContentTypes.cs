// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The game registries kiln.content exposes (Forge: <c>ForgeRegistries.ITEMS</c>, recipe manager, loot tables).</summary>
public enum ContentKind
{
    /// <summary>Every item definition (<c>keen::ItemInfo</c>): max stack, rarity, damage, armor, fuel, ...</summary>
    Items = 1,
    /// <summary>Every crafting recipe (<c>keen::RecipeInfo</c>): ingredients, results, station, duration, unlock.</summary>
    Recipes = 2,
    /// <summary>Random loot per item (<c>keen::LootableItemInfo</c>, containers and world pickups): stack sizes.</summary>
    Loot = 3,
    /// <summary>Default-inventory drop tables (enemy drops, fixed chests); filled as the game first uses each table.</summary>
    Drops = 4,
}

/// <summary>How a field's value is stored.</summary>
public enum ContentValueType
{
    None = 0,
    Bool = 1,
    /// <summary>Integers, enums and flag sets.</summary>
    Int = 2,
    Float = 3,
    /// <summary><c>keen::Time</c>, read and written in seconds.</summary>
    Time = 4,
    /// <summary>A GUID or a reference to another resource (read-only).</summary>
    Guid = 5,
}

/// <summary>Plain, enum or flag-set integer.</summary>
public enum ContentEnumKind
{
    Plain = 0,
    Enum = 1,
    Flags = 2,
}

/// <summary>One value read from a registry.</summary>
/// <param name="Name">The enum value's name (enums), the flag names joined by "," (flag sets), else null.</param>
public readonly record struct ContentValue(ContentValueType Type, long Integer, double Number, Guid Guid, string? Name)
{
    public bool Bool => Integer != 0;

    /// <summary>The value as a double (integers, floats, seconds for times).</summary>
    public double AsDouble => Type is ContentValueType.Int or ContentValueType.Bool ? Integer : Number;

    public override string ToString() => Type switch
    {
        ContentValueType.Bool => Bool ? "true" : "false",
        ContentValueType.Int => Name ?? Integer.ToString(),
        ContentValueType.Float => Number.ToString("G7", System.Globalization.CultureInfo.InvariantCulture),
        ContentValueType.Time => Number.ToString("G7", System.Globalization.CultureInfo.InvariantCulture) + "s",
        ContentValueType.Guid => Guid.ToString(),
        _ => "?",
    };
}

/// <summary>A field of a registry's struct, from the game's own reflection, as a dotted path
/// (<c>maxStackSize</c>, <c>damageSetup.dmgMod</c>, <c>perkIds[2]</c>).</summary>
public sealed record ContentField(int Index, string Path, string TypeName, int Offset, int Bytes, ContentValueType Type,
    bool Signed, bool Writable, ContentEnumKind EnumKind, IReadOnlyList<(string Name, long Value)> Values)
{
    /// <summary>A reference to another resource (a 16-byte GUID typed by the game: a model, a template, a perk, ...):
    /// not writable as a value, but settable with <see cref="ContentEntry.SetRef(string, string, bool)"/> (kitbash).</summary>
    public bool IsReference { get; init; }

    public override string ToString() => $"{Path}: {TypeName}{(Writable ? "" : IsReference ? " (reference)" : " (read-only)")}";
}

/// <summary>One ingredient or result of a recipe. <see cref="Category"/> != 0: "any item of that input category".</summary>
public readonly record struct RecipeStack(ItemId Item, int Count, uint Category = 0)
{
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static RecipeStack Of(string item, int count) =>
        new(Content.Items.Get(item) is { } e ? new ItemId(e.Id) : throw new ArgumentException($"unknown item {item}"), count);

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public override string ToString() =>
        Category != 0 ? $"category 0x{Category:x8} x{Count}" : $"{Content.Items.Get(Item.Value)?.Name ?? Item.ToString()} x{Count}";
}

/// <summary>One stack of a drop table (depth-first over the table's groups).</summary>
/// <param name="Chance">spawnProbability, 0..1.</param>
/// <param name="Weight">Weight inside a selector group (one of the group's stacks is picked by weight).</param>
/// <param name="Group">Index of the group the stack belongs to (read-only).</param>
public readonly record struct DropStack(ItemId Item, int CountMin, int CountMax, float Chance, float Weight, int Group);

/// <summary>A conflict: two owners (mods / data packs) set the same field to different values; the later one wins.</summary>
public sealed record ContentConflict(string Field, string Winner, string WinnerValue, string Loser, string LoserValue)
{
    public override string ToString() => $"{Field}: {Loser} sets {LoserValue}, {Winner} sets {WinnerValue} ({Winner} wins)";
}

/// <summary>What applying a data-pack document did.</summary>
public sealed record ContentReport(int Applied, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    IReadOnlyList<ContentConflict> Conflicts)
{
    public bool Ok => Errors.Count == 0;
}

/// <summary>State of kiln.content.</summary>
public sealed record ContentStatus(bool Ready, bool HasItems, bool HasRecipes, bool HasLoot, bool HasDrops, bool StackGuard,
    int Items, int Recipes, int Loot, int Drops, int Packs, int PackErrors, int Conflicts, int Changed, ulong Generation,
    string Detail)
{
    // Counted when first read: that lists every definition, and Content.Ready and the ready checks ask for the status often.
    private readonly Lazy<(int Definitions, int Placeholders)>? _counts;
    private readonly int? _definitions, _placeholders;

    /// <summary>New items and recipes defined by mods and data packs (all states).</summary>
    public int Definitions { get => _definitions ?? _counts?.Value.Definitions ?? 0; init => _definitions = value; }

    /// <summary>Placeholders "[Missing: mod:item]" registered for items of mods that are gone (missing content).</summary>
    public int Placeholders { get => _placeholders ?? _counts?.Value.Placeholders ?? 0; init => _placeholders = value; }

    internal Func<(int Definitions, int Placeholders)> CountedBy { init => _counts = new(value); }
}

/// <summary>
/// The content registries are loaded and every data pack is applied (Forge: the moment after data packs load,
/// <c>TagsUpdatedEvent</c> / <c>ServerStartedEvent</c> for registries). Fires on the tick thread at world load
/// (before the save is read and before players can join) and again after a data-pack reload. Apply code changes in a
/// handler: they are layered over the data packs. Not cancelable; no fields to change.
/// </summary>
public sealed class ContentReadyEvent : KilnEvent
{
    internal ContentReadyEvent(ulong generation, bool reload)
    {
        Generation = generation;
        Reload = reload;
    }

    /// <summary>+1 per application (1 = world load).</summary>
    public ulong Generation { get; }

    /// <summary>True after a data-pack reload (a pack file changed, or a mod with packs was loaded / unloaded).</summary>
    public bool Reload { get; }
}
