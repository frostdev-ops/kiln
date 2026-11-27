// SPDX-License-Identifier: MIT
// The names 0.2.0-dev.31 shipped that dev.32's API review renamed (owner 2026-11-27: the old surface keeps working, the new
// one beside it), so a mod built against dev.31 keeps building and its compiled calls keep binding:
//   Kiln.Registry.Registry         was also its namespace's name, so code in a Kiln.* namespace needed an alias
//                                  (using DataRegistry = Kiln.Registry.Registry): Registries holds the API, Registry forwards
//   ItemLootEntry.FixedRarity      an ItemLootRarity (None for none), as the generated code had it; ItemLootEntry.Rarity is
//   ItemLootRarity                 the same field as a RegistryRarity? (null for none), items.rarity's type. Both hidden and
//                                  [Obsolete] with the name to use; never removed before 1.0
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace Kiln.Registry;

/// <summary>The name of <see cref="Registries"/> before 0.2.0-dev.32 (it is also its namespace's name): the same members,
/// kept for mods built against it, [Obsolete] (a warning) and removed no earlier than 1.0. Use <see cref="Registries"/>.</summary>
[Experimental("KILNX031"), EditorBrowsable(EditorBrowsableState.Never), Obsolete(Message)]
public static class Registry
{
    internal const string Message = "Kiln.Registry.Registry is the 0.2.0-dev.31 name of Kiln.Registry.Registries (it is also its " +
        "namespace's name): use Registries, which has the same members. The old name keeps working and goes no earlier than 1.0.";
    /// <summary><see cref="Registries.Available"/>.</summary>
    [AnyThread]
    public static bool Available => Registries.Available;
    /// <summary><see cref="Registries.For"/>.</summary>
    [RegistryThreadOnly, RequiresCapability("kiln.registry")]
    public static RegistryStage For(RegistryContext context) => Registries.For(context);
    /// <summary><see cref="Registries.Change"/>.</summary>
    [TickThreadOnly, RequiresCapability("kiln.registry.live")]
    public static RegistryChange Change(Action<RegistryStage> edits) => Registries.Change(edits);
    /// <summary><see cref="Registries.Read"/>.</summary>
    [TickThreadOnly, RequiresCapability("kiln.registry")]
    public static void Read(Action<RegistryStage> read) => Registries.Read(read);
    /// <summary><see cref="Registries.Ready"/>.</summary>
    [TickThreadOnly]
    public static event Action<RegistryReady> Ready
    {
        add => Registries.Ready += value;
        remove => Registries.Ready -= value;
    }
}

public sealed partial class ItemLootEntry
{
    internal const string FixedRarityMessage = "ItemLootEntry.FixedRarity (an ItemLootRarity, None for none) is the 0.2.0-dev.31 " +
        "shape of loot.fixedRarity: use ItemLootEntry.Rarity, the same field as a RegistryRarity? (null for none). The old one " +
        "keeps working and goes no earlier than 1.0.";

    /// <summary>A rarity every drop gets (<see cref="ItemLootRarity.None"/>: rolled from rarities): loot.fixedRarity as
    /// 0.2.0-dev.31 shipped it, the same field as <see cref="Rarity"/>. Use <see cref="Rarity"/>.</summary>
    [EditorBrowsable(EditorBrowsableState.Never), Obsolete(FixedRarityMessage)]
    public ItemLootRarity FixedRarity
    {
        get => (ItemLootRarity)RegistryCells.Enum(_c, ItemLootFields.FixedRarity, _i, ItemLootRarityNames.All);
        set => RegistryCells.SetEnum(_c, ItemLootFields.FixedRarity, _i, ItemLootRarityNames.All, (int)value);
    }
}

/// <summary>A rarity every drop gets (none: rolled from rarities): the type of <see cref="ItemLootEntry.FixedRarity"/> as
/// 0.2.0-dev.31 shipped it. Use <see cref="RegistryRarity"/> (<see cref="ItemLootEntry.Rarity"/>, null for none).</summary>
[Experimental("KILNX031", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/loot.md")]
[EditorBrowsable(EditorBrowsableState.Never), Obsolete(ItemLootEntry.FixedRarityMessage)]
public enum ItemLootRarity
{
    /// <summary>none</summary>
    None = 0,
    /// <summary>common</summary>
    Common = 1,
    /// <summary>uncommon</summary>
    Uncommon = 2,
    /// <summary>rare</summary>
    Rare = 3,
    /// <summary>epic</summary>
    Epic = 4,
    /// <summary>legendary</summary>
    Legendary = 5,
}

internal static class ItemLootRarityNames
{
    internal static readonly string[] All = { "none", "common", "uncommon", "rare", "epic", "legendary" };
}
