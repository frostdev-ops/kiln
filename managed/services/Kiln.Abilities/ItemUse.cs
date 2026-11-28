// SPDX-License-Identifier: MIT
// Abilities.UseItem (since 0.2.0-dev.30.3, mod API 0.2.13; GitHub issue #110, thanks Fuzen777): a player uses a
// consumable from a slot of their own inventories the way the game does for a hotbar use. Kiln.Abilities picks the item's
// own sequence from the game's data (ItemInfo.sequences[] with triggeredBy Primary) and checks that it consumes the item;
// kiln.abilities' item_use (native/services/abilities/item_use.h) checks the slot, the item and the player, then starts
// the sequence with the slot and the item as the used item. The game's own code does the rest: it takes the item from that
// slot when the consume event runs and applies the buff with the item's values. Since 0.2.0-dev.31.5 (GitHub #118, thanks
// Fuzen777) the item is also the trigger context's cost item, as the game's hand use makes it (0x36b60): the sequence's
// PayUsageCost events pay with it (a scroll's mana) and, with consume, take it (the repair kit, which has no other consume).
// Evidence: docs/re/player-state.md "Eating, drinking and other consumables", docs/re/abilities.md "The cost item of an
// item use"; usage: docs/kiln/services/abilities.md "Using an item".
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>What <see cref="Abilities.UseItem"/> did. Since 0.2.0-dev.30.3 (mod API 0.2.13).</summary>
public enum ItemUseResult
{
    /// <summary>The item's own sequence started on the player, as a hotbar use starts it: the animation, the effects, and
    /// at the sequence's consume event (0.05 to 0.6 s later for the game's food, drinks and potions) one item taken from
    /// the slot and the buff applied with the item's own values and time; a repair kit is taken by the sequence's
    /// PayUsageCost (0.07 s) and repairs over 4.3 s; a sequence's costs (a scroll's mana) are paid as for a hand use. A
    /// <c>PlayerConsumeEvent</c> handler that cancels,
    /// a rule, or the player's own action in the same step can still stop it, as they stop a hand use; the
    /// <c>PlayerConsumedEvent</c> says when the item went.</summary>
    Started = 0,

    /// <summary>The item has no sequence its use starts (<c>triggeredBy</c> Primary), or that sequence uses up nothing:
    /// a weapon, a tool, a material, a staff's spell (ammunition: cast with the staff, not used; the few spells and arrows
    /// with a consume event of their own start). Nothing happened.</summary>
    NotConsumable = 1,

    /// <summary>The slot is not in the player's own inventories (backpack, hotbar, equipment), or the player has none.
    /// Move the item into one first (<c>Items.Move</c>).</summary>
    NotOwnSlot = 2,

    /// <summary>The slot holds nothing.</summary>
    EmptySlot = 3,

    /// <summary>The slot held another item when kiln.abilities checked it than when Kiln.Abilities read it (it changed
    /// during the call). Nothing happened; read the slot again.</summary>
    SlotChanged = 4,

    /// <summary>The entity is not a player character (its inventories are not a player's, or it has no actor).</summary>
    NotAPlayer = 5,

    /// <summary>The player is dead.</summary>
    Dead = 6,

    /// <summary>The player is not ready for gameplay yet (joining, loading the terrain, respawning).</summary>
    NotReady = 7,

    /// <summary>The player is in another action (an attack, another item's use, an emote, a dodge), already has one
    /// waiting for this step (a second <c>UseItem</c> or a <c>PlaySequence</c> on the same player in the same step), or is
    /// in a state a use does not start from (sleeping, sitting, climbing, gliding, fishing, an instrument). The game has no
    /// cooldown for consumables: what plays as one is the use's own sequence, so a second use right after the first is
    /// <see cref="Busy"/> until it ends.</summary>
    Busy = 8,

    /// <summary>This process has no server world (a joined client, or a server whose world has not started): the game
    /// runs item uses on the server only.</summary>
    ServerOnly = 9,
}

public static unsafe partial class Abilities
{
    /// <summary>The player uses the consumable in <paramref name="slot"/> the way the game does when they use it from the
    /// hotbar: the item's own sequence plays (eating, drinking, a potion), and the game's own code takes one item from
    /// that slot and applies the buff with the item's values (148 foods share 16 buff types: the numbers are the item's),
    /// so the client shows the buff's effect lines and its time as for a hand use. The item is also the use's cost item, as
    /// in a hand use (since 0.2.0-dev.31.5): a scroll pays its mana, a repair kit is taken by its PayUsageCost and repairs
    /// the player's gear; docs/kiln/services/abilities.md "Using an item" lists the uses covered. <paramref name="slot"/> must be in the
    /// player's own inventories; move an item from a chest first. Nothing is taken at the call: the item goes when the
    /// sequence's consume event runs, a fraction of a second later (<see cref="ItemUseResult.Started"/>). Server, tick
    /// thread, between steps (a tick handler, an event handler, a scheduled task); not in a native handler or attachment,
    /// which runs inside the world's step. Since 0.2.0-dev.30.3 (mod API 0.2.13).</summary>
    /// <param name="player">The player character (<c>Players.Find(..)!.Entity</c>).</param>
    /// <param name="slot">A slot of the player's inventories (<see cref="InventorySlot.Slot"/> of <see cref="Items.Read"/>).</param>
    /// <returns>What happened; every outcome but <see cref="ItemUseResult.Started"/> changes nothing.</returns>
    /// <exception cref="ArgumentException"><paramref name="player"/> or <paramref name="slot"/> is none.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a native handler or attachment (the
    /// world's step, where the game's own programs run beside it on other threads): post it to the next tick.</exception>
    /// <exception cref="KilnUnavailableException">kiln.abilities, kiln.items or kiln.gamedata is not here, or this build
    /// cannot start item uses (sequences, or the trigger context's used item in the game's reflection: kiln.log says
    /// which), or, for an item only its PayUsageCost uses up (the repair kit), not its cost item.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(AbilitiesCaps.Service)]
    [RequiresCapability(AbilitiesCaps.Sequences)]
    [RequiresCapability("kiln.items")]
    [RequiresCapability("kiln.gamedata")]
    public static ItemUseResult UseItem(EntityId player, SlotId slot)
    {
        if (player.IsNone) throw new ArgumentException("no player", nameof(player));
        if (slot.IsNone) throw new ArgumentException("no slot", nameof(slot));
        // a sync native's or an attachment's handler is on the world's thread, so the tick-thread check passes there; but
        // it runs inside the step, where the game could start the sequence before its used item is in (kiln.abilities
        // refuses it natively too)
        if (AbilitiesDispatch.InsideHandler)
            throw new InvalidOperationException("Abilities.UseItem cannot run in a native handler or attachment (it runs inside the world's step); use Scheduler.Post.");
        Game.AssertTickThread();
        var t = ItemUseTable("Abilities.UseItem");
        var inv = Items.Read(player);
        if (inv == null) return ItemUseResult.NotOwnSlot;
        var s = inv.Slots.FirstOrDefault(x => x.Slot == slot);
        if (s == null) return ItemUseResult.NotOwnSlot;
        if (s.IsEmpty) return ItemUseResult.EmptySlot;
        if (!Content.GameDataAvailable) throw ServiceCaps.Unavailable("kiln.gamedata", "Abilities.UseItem reads the item's sequence from kiln.gamedata, which is not here");
        if (Content.Status() is not { Ready: true })
            throw ServiceCaps.Unavailable("kiln.gamedata", "Abilities.UseItem: the game's data is not loaded yet (no server world)");
        if (Content.ItemData.Get(s.Item.Value) is not { } data) return ItemUseResult.NotConsumable;
        if (PrimaryUse(data.GetJson("sequences")) is not { } use) return ItemUseResult.NotConsumable;
        // the game's hand use makes the item its own cost item unless the entry names a costOverride (GitHub #118); a
        // spell (ammunition) is cast with a staff, which passes it as ammunition: not a use Kiln makes
        var payWithItem = !use.CostOverride && IsUsable(Leaf(data, "category"), Leaf(data, "ammunitionType"));
        var consumes = SequenceGuard.ConsumingEvent(use.Sequence) != null;
        if (!consumes && !(payWithItem && SequenceGuard.PayingEvent(use.Sequence) != null)) return ItemUseResult.NotConsumable;
        return UseResolved(t, player, slot, s.Item, use.Sequence, payWithItem, consumes);
    }

    /// <summary>An item field's value (kiln.gamedata's node <c>{path, type, value}</c>), or null when it cannot be read.</summary>
    private static JsonNode? Leaf(GameDataEntry data, string path)
    {
        try
        {
            return data.GetJson(path) is JsonObject o && o.ContainsKey("path") ? o["value"] : null;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Whether a use of the item may make it its own cost item: its category and ammunition type read, and it
    /// is no ammunition (the game's 102 spells with a PayUsageCost that consumes are staff casts). Unreadable: no, so
    /// the use stays as in 0.2.0-dev.30.3 (only the used item).</summary>
    internal static bool IsUsable(JsonNode? category, JsonNode? ammunitionType) =>
        category is JsonValue c && c.TryGetValue<string>(out var cat) && cat != "Ammunition"
        && ammunitionType is JsonValue a && a.TryGetValue<string>(out var ammo) && ammo == "Invalid";

    /// <summary>kiln.abilities with item_use, or an exception that says what is missing.</summary>
    internal static AbilitiesNative.TableV2* ItemUseTable(string api)
    {
        var t = AbilitiesNative.Require(api);
        if (!AbilitiesNative.Has(t, nameof(AbilitiesNative.TableV2.ItemUseFn)))
            throw new KilnUnavailableException(AbilitiesCaps.Sequences, $"{api} needs kiln.abilities 0.2.0-dev.30.3 or newer");
        return (AbilitiesNative.TableV2*)t;
    }

    internal static int _oldNativeWarned;  // the cost item's fallback said so in kiln.log (once a process)

    /// <summary>item_use with what <see cref="UseItem"/> read: the slot's item and the item's own sequence;
    /// <paramref name="payWithItem"/>: the item is the cost item too (KILN_ITEM_USE_COST_ITEM), <paramref name="consumes"/>:
    /// the sequence has a consume event of its own. A kiln.abilities that cannot set the cost item (older than
    /// 0.2.0-dev.31.5, or no costItem in this build's reflection) gets the use without it when the sequence consumes by
    /// itself (as in 0.2.0-dev.30.3: its costs unpaid); otherwise the use is never started without it, since a repair kit
    /// started with no cost item would repair and never be taken: <see cref="ItemUseResult.NotConsumable"/> from an older
    /// kiln.abilities, <see cref="KilnUnavailableException"/> from a build without costItem.</summary>
    internal static ItemUseResult UseResolved(AbilitiesNative.TableV2* t, EntityId player, SlotId slot, ItemId item, Guid sequence,
        bool payWithItem = false, bool consumes = true)
    {
        AbilitiesNative.ItemUseRequest u = default;
        u.Size = (uint)sizeof(AbilitiesNative.ItemUseRequest);
        u.Entity = player.Value;
        u.Slot = slot.Value;
        u.Item = item.Value;
        u.Sequence = IdOf(sequence);
        u.Flags = payWithItem ? AbilitiesNative.UseCostItem : 0;
        var rc = t->ItemUseFn(&u);
        if (payWithItem && rc is AbilitiesNative.InvalidArgument or AbilitiesNative.Unavailable)
        {
            // InvalidArgument: a kiln.abilities older than the flag. Unavailable: no costItem in this build's reflection, or
            // item_use off altogether (the retry without the flag tells them apart; it is only made when the sequence
            // consumes by itself)
            var first = rc;
            if (consumes)
            {
                u.Flags = 0;
                u.Status = 0;
                rc = t->ItemUseFn(&u);
            }
            if ((first == AbilitiesNative.InvalidArgument || (consumes && rc != AbilitiesNative.Unavailable))
                && Interlocked.Exchange(ref _oldNativeWarned, 1) == 0)
                ModLog.Warn($"Abilities.UseItem: this kiln.abilities cannot make the used item the cost item ({AbilitiesNative.Describe(first)}: " +
                            "older than 0.2.0-dev.31.5, or no triggerContext.costItem in this build's reflection): items used up only by " +
                            "their PayUsageCost (the repair kit) are not used, and other uses do not pay the item's own costs");
            if (!consumes && first == AbilitiesNative.InvalidArgument) return ItemUseResult.NotConsumable;
            if (!consumes && first == AbilitiesNative.Unavailable)
            {
                AbilitiesCaps.Ensure();
                throw ServiceCaps.Unavailable(AbilitiesCaps.Sequences,
                    "Abilities.UseItem cannot use this item on this build: only its PayUsageCost uses it up, which needs the trigger " +
                    "context's cost item (or item_use is off altogether: kiln.log says which)");
            }
        }
        if (rc == AbilitiesNative.Unavailable)
        {
            AbilitiesCaps.Ensure();
            throw ServiceCaps.Unavailable(AbilitiesCaps.Sequences,
                "Abilities.UseItem is unavailable on this build (sequences, the trigger context's used item or the state flags did not resolve: kiln.log says which)");
        }
        if (rc == AbilitiesNative.NotFound)
        {
            ModLog.Warn($"Abilities.UseItem: item {item} names sequence {sequence}, which the game does not have: not used");
            return ItemUseResult.NotConsumable;
        }
        return Outcome(rc, u.Status) ?? throw new InvalidOperationException($"Abilities.UseItem: kiln.abilities answered {AbilitiesNative.Describe(rc)}");
    }

    /// <summary>The result for item_use's answer, or null for an error the caller turns into an exception.</summary>
    internal static ItemUseResult? Outcome(int rc, uint status) => rc switch
    {
        AbilitiesNative.WrongThread => ItemUseResult.ServerOnly,  // the tick thread is asserted before: no server world
        AbilitiesNative.Ok => status switch
        {
            AbilitiesNative.UseStarted => ItemUseResult.Started,
            AbilitiesNative.UseNotOwnSlot => ItemUseResult.NotOwnSlot,
            AbilitiesNative.UseEmptySlot => ItemUseResult.EmptySlot,
            AbilitiesNative.UseOtherItem => ItemUseResult.SlotChanged,
            AbilitiesNative.UseNotAPlayer => ItemUseResult.NotAPlayer,
            AbilitiesNative.UseDead => ItemUseResult.Dead,
            AbilitiesNative.UseNotReady => ItemUseResult.NotReady,
            AbilitiesNative.UseBusy => ItemUseResult.Busy,
            _ => null,
        },
        _ => null,
    };

    /// <summary>The sequence a use of the item starts: of the item's <c>sequences</c> (kiln.gamedata's JSON, as the game
    /// has them now), the first triggered by Primary with no requirements, else the first triggered by Primary.</summary>
    internal static Guid? PrimarySequence(JsonNode? sequences) => PrimaryUse(sequences)?.Sequence;

    /// <summary>The entry <see cref="PrimarySequence"/> picks: its sequence, and whether it names a <c>costOverride</c>
    /// (then the game's hand use pays with that item, not the used one; no vanilla Primary entry has one).</summary>
    internal readonly record struct ItemUseEntry(Guid Sequence, bool CostOverride);

    /// <summary>As <see cref="PrimarySequence"/>, with the entry's <c>costOverride</c>.</summary>
    internal static ItemUseEntry? PrimaryUse(JsonNode? sequences)
    {
        if ((sequences is JsonObject o && o.ContainsKey("path") ? o["value"] : sequences) is not JsonArray list) return null;
        ItemUseEntry? any = null;
        foreach (var x in list)
        {
            if (x is not JsonObject e || e["triggeredBy"]?.ToString() != "Primary") continue;
            if (e["sequence"] is not JsonValue v || !v.TryGetValue<string>(out var text) || !Guid.TryParse(text, out var g) || g == Guid.Empty)
                continue;
            var plain = e["requirements"] switch
            {
                JsonArray a => a.Count == 0,
                JsonValue r when r.TryGetValue<string>(out var rs) => string.IsNullOrWhiteSpace(rs) || rs == "0",
                null => true,
                _ => false,
            };
            var entry = new ItemUseEntry(g, HasCostOverride(e["costOverride"]));
            if (plain) return entry;
            any ??= entry;
        }
        return any;
    }

    /// <summary>A <c>costOverride</c> that names an item: a GUID other than zero, or a name; kiln.gamedata's <c>"none"</c>
    /// (how it writes a zero reference), empty or zero is none.</summary>
    internal static bool HasCostOverride(JsonNode? value)
    {
        if (value is null) return false;
        if (value is not JsonValue v || !v.TryGetValue<string>(out var text)) return true;  // not a plain reference: assume one
        if (string.IsNullOrWhiteSpace(text) || text is "0" or "none") return false;
        return !Guid.TryParse(text, out var g) || g != Guid.Empty;
    }
}
