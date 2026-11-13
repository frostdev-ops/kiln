// SPDX-License-Identifier: MIT
// The face of affixes (dev.27): define affixes and pools, roll and edit the affixes of one item, read what Kiln applies
// to a player, and read the server's copies in a player's game. The engine is AffixRuntime.cs.
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Kiln;

/// <summary>What <see cref="Affixes.Load"/> read from a data file.</summary>
public sealed record AffixFile(IReadOnlyList<AffixHandle> Affixes, IReadOnlyList<AffixPoolHandle> Pools);

/// <summary>An item with affixes as a player's game knows it from the server (<see cref="Affixes.SyncedItems"/>).</summary>
/// <param name="Item">The item entity's id on the server.</param>
/// <param name="ItemType">The item type.</param>
/// <param name="Holder">The server's id of who holds it: a player, a chest.</param>
/// <param name="FlatSlot">The slot in the holder, as Kiln.Items numbers it.</param>
/// <param name="Own">True when this game's player holds it.</param>
/// <param name="Affixes">Its affixes.</param>
[ClientOnly]
public sealed record SyncedAffixes(EntityId Item, ItemId ItemType, EntityId Holder, int FlatSlot, bool Own, IReadOnlyList<RolledAffix> Affixes);

/// <summary>
/// Affixes: rolled modifiers on one item ("+5 Strength", "+5% Move Speed"), kept with that item and applied to whoever
/// wears or holds it.
/// <code>
/// var bear = Blocks.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1, Max = 5 }] });
/// var pool = Blocks.Define("blades", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "bear" }] });   // or Affixes.DefinePool: the same
/// AffixResult r = pool.Roll(Items.Held(player));        // this one sword now carries "+3 Strength (of the Bear)"
/// foreach (var a in Affixes.Of(sword)) Log.Info(a.Text);
/// </code>
/// </summary>
/// <remarks>
/// <para>Affix kinds and pools are blocks: <see cref="Blocks.Define(string, AffixBlock)"/>, the <c>affixes</c> and
/// <c>affixPools</c> sections of <c>blocks/*.json</c>, and <see cref="Define"/>, <see cref="DefinePool"/> and
/// <see cref="Load"/> here are one registration, with one handle and the id <c>"mod:name"</c> whichever route is used.</para>
/// <para>Where the rolls are kept. An item's affixes are item data (<c>InstanceData</c>, owner <see cref="DataMod"/>, key
/// <see cref="DataKey"/>): they stay with that one item through moves, saves and restarts, by the rules of
/// docs/kiln/services/saves.md, and they go to the games of players near the item (its holder, and players within 16 m
/// of a chest or another player that holds it), where <see cref="Synced"/> reads them.</para>
/// <para>How they act. About twice a second Kiln reads what every player wears (the equipment slots, without the vanity
/// slots) and holds in hand, and keeps one stat change per affix of those items on the player: a small Impact program
/// that adds the value to the game's attribute and takes it back when it ends. Everything follows from what is worn at
/// that moment, so taking an item off, losing it, giving it away, dying, leaving and a restart need no rule of their own,
/// and nothing stays on a player whose item is gone. Affixes of a mod that is not loaded stay on their items and do
/// nothing until it is back.</para>
/// <para>Two worn items with the same affix add up, unless the affix says <see cref="AffixBlock.Stacks"/> false: then
/// only the highest value counts.</para>
/// <para>Server side, tick thread, except <see cref="Synced"/> and <see cref="SyncedItems"/>. Capabilities:
/// <c>blocks.affixes</c> (the rolls: needs <c>saves.instance_data</c>) and <c>blocks.affixes.effects</c> (the stat
/// changes: needs kiln.abilities' programs and <c>items.instances</c>).</para>
/// </remarks>
public static class Affixes
{
    internal const string Capability = "blocks.affixes", EffectsCapability = "blocks.affixes.effects";

    /// <summary>The owner the rolls are kept under as item data, and the mod id of their <c>InstanceDataSyncedEvent</c>.</summary>
    public const string DataMod = "kiln-affixes";

    /// <summary>The item data key of the rolls.</summary>
    public const string DataKey = "affixes";

    /// <summary>The most affixes one item carries.</summary>
    public const int MaxPerItem = 8;

    // ------------------------------------------------------------------------------------------------ authoring ----

    /// <summary>Defines an affix of the calling mod: the same as <see cref="Blocks.Define(string, AffixBlock)"/> (one
    /// registration, one handle, the id <c>"mod:name"</c>). Call it in <c>OnLoad</c>; the same name again, by either
    /// route or in a <c>blocks/*.json</c> file, replaces it, as for every block.</summary>
    /// <param name="name">Unique among the mod's blocks: lower-case letters, digits, '-' and '_', at most 64.</param>
    /// <param name="block">The affix.</param>
    /// <exception cref="BlockError">The name or a field is wrong: which field and why; or the name is another kind of
    /// block of the mod.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or not from a mod's code.</exception>
    [TickThreadOnly]
    public static AffixHandle Define(string name, AffixBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        AffixRuntime.CallerMod("Affixes.Define");
        return AffixRuntime.DefineAffix(name, block);
    }

    /// <summary>Defines a pool of the calling mod, which items roll which affixes: the same as
    /// <see cref="Blocks.Define(string, AffixPoolBlock)"/>. Its affixes are defined before it in code, or in a
    /// <c>blocks/*.json</c> file of the mod (compiled when the pool names them).</summary>
    /// <param name="name">Unique among the mod's blocks, as an affix's name.</param>
    /// <param name="block">The pool.</param>
    /// <exception cref="BlockError">The name or a field is wrong, or it names an affix that is not defined.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or not from a mod's code.</exception>
    [TickThreadOnly]
    public static AffixPoolHandle DefinePool(string name, AffixPoolBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        AffixRuntime.CallerMod("Affixes.DefinePool");
        return AffixRuntime.DefinePool(name, block);
    }

    /// <summary>Defines the affixes and pools of a data file, as the calling mod's blocks: the same shapes as the C# records,
    /// in camel case. (A file in the mod's <c>blocks/</c> folder needs no call: its <c>affixes</c> and
    /// <c>affixPools</c> sections are read as every block file is.)
    /// <code>
    /// { "format": 1,
    ///   "affixes": { "bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 1, "max": 5 } ] } },
    ///   "pools": { "blades": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "bear" } ] } } }
    /// </code></summary>
    /// <param name="json">The file's text.</param>
    /// <exception cref="BlockError">The text is not such a file, or a block in it is wrong.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or not from a mod's code.</exception>
    [TickThreadOnly]
    public static AffixFile Load(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        AffixRuntime.CallerMod("Affixes.Load");
        return AffixRuntime.Load(json);
    }

    /// <summary>A defined affix by <c>"mod:name"</c> (or a name of the calling mod); null when no loaded mod defines it.</summary>
    public static AffixHandle? Find(string affix)
    {
        ArgumentNullException.ThrowIfNull(affix);
        return AffixRuntime.FindAffix(AffixRuntime.Qualify(affix))?.Handle;
    }

    /// <summary>A defined pool by <c>"mod:name"</c> (or a name of the calling mod); null when no loaded mod defines it.</summary>
    public static AffixPoolHandle? FindPool(string pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return AffixRuntime.FindPool(AffixRuntime.Qualify(pool))?.Handle;
    }

    // ------------------------------------------------------------------------------------------------ one item ----

    /// <summary>The affixes <paramref name="item"/> carries (empty: none, or the ref is stale).</summary>
    /// <exception cref="ArgumentException">A none ref, or one without a stamp.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static IReadOnlyList<RolledAffix> Of(ItemInstanceRef item) => AffixRuntime.Guarded("Affixes.Of", key => AffixRuntime.Read(item, key));

    /// <summary>Rolls <paramref name="pool"/> onto <paramref name="item"/>: between the pool's <c>Min</c> and <c>Max</c>
    /// different affixes, each with a tier its item level allows and a value in that tier's range.</summary>
    /// <param name="item">The item (from <c>Items.Held</c>, an inventory read, a give result).</param>
    /// <param name="pool">The pool.</param>
    /// <param name="random">A seeded one makes the roll repeat; null: the shared one.</param>
    /// <param name="replace">Roll again over affixes the item already has (false: such an item is refused).</param>
    /// <returns><see cref="AffixResult.Ok"/> false when the pool is not for this item type, the item already has
    /// affixes and <paramref name="replace"/> is false, or fewer than the pool's <c>Min</c> affixes can roll on this item
    /// now (an affix's mod is not loaded, no tier for the item's level, or the rest share a group); the item keeps what it
    /// had then.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is null.</exception>
    /// <exception cref="ArgumentException">A none, stamp-less or stale ref, or a pool whose mod has unloaded.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static AffixResult Roll(ItemInstanceRef item, AffixPoolHandle pool, Random? random = null, bool replace = false)
    {
        ArgumentNullException.ThrowIfNull(pool);
        return AffixRuntime.Guarded("Affixes.Roll", key => AffixRuntime.Roll(item, key, pool, random ?? Random.Shared, replace));
    }

    /// <summary>Adds one affix to <paramref name="item"/>.</summary>
    /// <param name="item">The item.</param>
    /// <param name="affix"><c>"mod:name"</c>, or a name of the calling mod.</param>
    /// <param name="tier">The tier, from 1.</param>
    /// <param name="value">The value; null: rolled in the tier's range.</param>
    /// <param name="random">For a rolled value; null: the shared one.</param>
    /// <returns><see cref="AffixResult.Ok"/> false when the item already has this affix or one of its group, or has
    /// <see cref="MaxPerItem"/> already.</returns>
    /// <exception cref="ArgumentException">A none, stamp-less or stale ref, an affix no loaded mod defines, a tier it does
    /// not have, or a value that is 0, not finite, or not whole for a whole-number stat.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static AffixResult Add(ItemInstanceRef item, string affix, int tier = 1, double? value = null, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(affix);
        var id = AffixRuntime.Qualify(affix);
        return AffixRuntime.Guarded("Affixes.Add", key => AffixRuntime.Add(item, key, id, tier, value, random ?? Random.Shared));
    }

    /// <summary>Gives <paramref name="item"/> exactly these affixes (an empty list takes them all off): for tools and
    /// commands that edit an item. The affixes need not be defined now.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="affixes"/> is null.</exception>
    /// <exception cref="ArgumentException">A none, stamp-less or stale ref, more than <see cref="MaxPerItem"/> affixes, the
    /// same affix twice, an affix id that is not <c>"mod:name"</c>, or a value that is 0 or not finite.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static AffixResult Set(ItemInstanceRef item, IReadOnlyList<RolledAffix> affixes)
    {
        ArgumentNullException.ThrowIfNull(affixes);
        return AffixRuntime.Guarded("Affixes.Set", key => AffixRuntime.Set(item, key, affixes));
    }

    /// <summary>Takes one affix off <paramref name="item"/>. False when it had none such, or the ref is stale.</summary>
    /// <exception cref="ArgumentException">A none or stamp-less ref.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static bool Remove(ItemInstanceRef item, string affix)
    {
        ArgumentNullException.ThrowIfNull(affix);
        var id = AffixRuntime.Qualify(affix);
        return AffixRuntime.Guarded("Affixes.Remove", key => AffixRuntime.Remove(item, key, id));
    }

    /// <summary>Takes every affix off <paramref name="item"/>. False when it had none, or the ref is stale.</summary>
    /// <exception cref="ArgumentException">A none or stamp-less ref.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(Capability)]
    public static bool Clear(ItemInstanceRef item) => AffixRuntime.Guarded("Affixes.Clear", key => AffixRuntime.Remove(item, key, null));

    // ------------------------------------------------------------------------------------------------ effects ----

    /// <summary>What Kiln holds on <paramref name="wearer"/> right now for the affixes of what is worn and in hand.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    public static IReadOnlyList<ActiveAffix> Active(EntityId wearer)
    {
        Services.RequireTickThread("Affixes.Active");
        return AffixRuntime.ActiveOf(wearer);
    }

    /// <summary>The value the game's attribute for <paramref name="stat"/> holds on <paramref name="entity"/> now, read
    /// by a small program through the game's own attribute read: what every modifier adds up to (points for a
    /// whole-number stat, a share for the others; for a resistance, the share of that damage still taken). The answer
    /// comes two or three steps later. Null when the game did not answer (no such entity, or the world closed).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="stat"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none or has no position.</exception>
    /// <exception cref="KilnUnavailableException"><c>blocks.affixes.effects</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(EffectsCapability)]
    public static Task<double?> StatOf(EntityId entity, Stat stat)
    {
        ArgumentNullException.ThrowIfNull(stat);
        Services.RequireTickThread("Affixes.StatOf");
        return AffixRuntime.Probe(entity, stat);
    }

    /// <summary>Lab only: treats <paramref name="wearer"/> (a player-template entity a lab spawned) as a player whose worn
    /// and held items count. False takes it out again.</summary>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    [ServerOnly]
    [TickThreadOnly]
    public static void LabTrack(EntityId wearer, bool on = true)
    {
        Services.RequireTickThread("Affixes.LabTrack");
        AffixRuntime.LabTrack(wearer, on);
    }

    /// <summary>Lab only: what the engine did since start (passes, stat changes started and ended, ones the game ended
    /// by itself and Kiln started again).</summary>
    [Experimental("KILNX001", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx001")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string LabStatus => AffixRuntime.Describe();

    // ------------------------------------------------------------------------------------------------- client ----

    /// <summary>Client: the affixes of the item with the server's entity id <paramref name="item"/>, as the server sent
    /// them to this game; null when it sent none. Any thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data.sync</c> is off.</exception>
    [ClientOnly]
    [AnyThread]
    [RequiresCapability("saves.instance_data.sync")]
    public static IReadOnlyList<RolledAffix>? Synced(EntityId item) =>
        InstanceData.Synced(item)?.Json(DataMod, DataKey) is { } json ? AffixRuntime.Parse(json) : null;

    /// <summary>Client: every item with affixes this game knows from the server: the player's own, and those of holders
    /// nearby. The first call opens the channel the server sends on, so call it once when the mod loads. Any thread.</summary>
    /// <exception cref="KilnUnavailableException"><c>saves.instance_data.sync</c> is off.</exception>
    [ClientOnly]
    [AnyThread]
    [RequiresCapability("saves.instance_data.sync")]
    public static IReadOnlyList<SyncedAffixes> SyncedItems()
    {
        var list = new List<SyncedAffixes>();
        foreach (var i in InstanceData.Synced())
            if (i.Json(DataMod, DataKey) is { } json && AffixRuntime.Parse(json) is { Count: > 0 } affixes)
                list.Add(new SyncedAffixes(i.Entity, i.Item, i.Holder, i.FlatSlot, i.Own, affixes));
        return list;
    }
}
