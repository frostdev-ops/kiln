// SPDX-License-Identifier: MIT
// Loadouts: Items.Equip and Items.Unequip, the game's own equip run for a mod (docs/kiln/services/items.md).
//   /loadout save <name>    remembers what you wear now, slot by slot (kept with the world, by Steam id)
//   /loadout wear <name>    puts those items on again from your backpack
//   /loadout check <name>   asks the game the same without changing anything (EquipOptions.DryRun)
//   /loadout off <slot>     takes off what one slot holds, into the backpack (/loadout off Ring0)
// What to take from it: ask the capability first and say what is missing; an equip is a task that ends when the game
// ran it, a step or two later, and a refusal is a result with a reason (the item does not fit the slot, the backpack is
// full, the player died or left in between), not an exception; read the backpack right before each call, because
// every equip moves items.
using Kiln;

namespace Loadouts;

public sealed class Wardrobe
{
    // loadout name -> equipment slot name -> the item's id
    public Dictionary<string, Dictionary<string, uint>> Sets { get; set; } = [];
}

public sealed class LoadoutsMod : Mod
{
    private const string WardrobeKey = "wardrobe";
    // The capability row of Kiln's equip (the /caps command lists them). It is off on a game build Kiln has no equip
    // functions for, on a server without kiln.world or kiln.players, and on a game that is in its menu.
    private const string EquipCapability = "items.equip";
    private const int MaxSets = 8, MaxName = 24;

    public override void OnLoad() =>
        Log.Info($"loadouts: equip {(Capabilities.Is(EquipCapability) ? "available" : $"not available ({Capabilities.Why(EquipCapability)})")}");

    [Command("loadout", Description = "save <name>, wear <name>, check <name>, or off <slot>: what you wear, kept and put on again")]
    private async Task Loadout(CommandContext c, string action, string? name = null)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player wears things");
        if (!Items.Available) throw new CommandException("items cannot be read on this server (kiln.items is missing)");
        if (string.IsNullOrWhiteSpace(name)) throw new CommandException("save <name>, wear <name>, check <name>, or off <slot>");
        switch (action.ToLowerInvariant())
        {
            case "save":
                c.Reply(Save(c, name));
                break;
            case "wear":
                await Wear(c, name, EquipOptions.None);
                break;
            case "check":
                await Wear(c, name, EquipOptions.DryRun);
                break;
            case "off":
                await Off(c, name);
                break;
            default:
                throw new CommandException("save <name>, wear <name>, check <name>, or off <slot>");
        }
    }

    private static string Save(CommandContext c, string name)
    {
        if (name.Length > MaxName) throw new CommandException($"a loadout's name may have at most {MaxName} characters");
        var wardrobe = Mine(c);
        if (!wardrobe.Sets.ContainsKey(name) && wardrobe.Sets.Count >= MaxSets)
            throw new CommandException($"you have {MaxSets} loadouts already: save over one of {string.Join(", ", wardrobe.Sets.Keys)}");
        var worn = new Dictionary<string, uint>();
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
            if (Items.Equipped(c.Source.Player, slot) is { IsNone: false } item)
                worn[slot.ToString()] = item.Item.Value;
        if (worn.Count == 0) throw new CommandException("you wear nothing a loadout could keep");
        wardrobe.Sets[name] = worn;  // the live object of PlayerData: written with the world's save, nothing to flush
        return $"loadout {name}: {worn.Count} item(s) kept";
    }

    private static async Task Wear(CommandContext c, string name, EquipOptions options)
    {
        if (!Mine(c).Sets.TryGetValue(name, out var set)) throw new CommandException($"you have no loadout {name}");
        NeedEquip();
        var player = c.Source.Player;
        var dry = options.HasFlag(EquipOptions.DryRun);
        int on = 0;
        foreach (var (slotName, itemId) in set)
        {
            // A name this Kiln does not know: the loadout was saved under a later Kiln that has more slots.
            if (!Enum.TryParse<EquipmentSlot>(slotName, out var slot)) continue;
            var item = new ItemId(itemId);
            if (Items.Equipped(player, slot) is { IsNone: false } worn && worn.Item == item)
            {
                on++;
                continue;
            }
            // Read the inventory here, not once before the loop: the equip before this one moved items, and what the
            // slot held came off into the backpack.
            var from = Items.Read(player)?.Slots.FirstOrDefault(s => s.Item == item && !s.IsEquipped);
            if (from is null)
            {
                c.Reply($"  {slot}: {Named(item)} is not in your backpack");
                continue;
            }
            // The task ends when the game's own equip ran for this player, a step or two later. The item entity makes
            // the request exact: if another stack lies in that slot by then, the game is not asked and the result says so.
            var result = await Items.Equip(player, from.Slot, slot, options, from.ItemEntity);
            if (result.Ok) on++;
            else c.Reply($"  {slot}: {Named(item)} {(dry ? "would not go on" : "was not put on")}: {result.Reason}");
        }
        c.Reply(dry ? $"loadout {name}: {on} of {set.Count} would be on" : $"loadout {name}: {on} of {set.Count} on");
    }

    private static async Task Off(CommandContext c, string slotName)
    {
        if (!Enum.TryParse<EquipmentSlot>(slotName, ignoreCase: true, out var slot) || !Enum.IsDefined(slot))
            throw new CommandException($"no slot {slotName}: one of {string.Join(", ", Enum.GetNames<EquipmentSlot>())}");
        NeedEquip();
        // An empty slot and a full backpack are refusals too: the reason is a sentence for the player.
        var result = await Items.Unequip(c.Source.Player, slot);
        c.Reply(result.Ok ? $"{slot}: {Named(result.Item)} is in your backpack" : $"{slot}: nothing taken off: {result.Reason}");
    }

    // Every equip Kiln ran for any mod, once it happened (never for a refusal or a dry run). What a player changes by
    // hand is Kiln.Players' PlayerEquipmentChangedEvent.
    [Subscribe]
    private void OnEquipChanged(EquipChangedEvent e) =>
        Log.Debug($"loadouts: {e.Player} {e.Slot}: {Named(e.BeforeItem)} -> {Named(e.AfterItem)}");

    private static void NeedEquip()
    {
        if (!Capabilities.Is(EquipCapability))
            throw new CommandException($"this server cannot change what you wear ({EquipCapability} is off: {Capabilities.Why(EquipCapability)})");
    }

    private static Wardrobe Mine(CommandContext c)
    {
        if (!Saves.Available) throw new CommandException("loadouts cannot be kept on this server (kiln.saves is missing)");
        if (c.Source.SteamId == 0) throw new CommandException("your Steam id is not known yet: try again in a moment");
        return PlayerData.Get<Wardrobe>(c.Source.SteamId, WardrobeKey);
    }

    private static string Named(ItemId item) =>
        item.Value == 0 ? "nothing" : Items.Get(item) is { } info ? info.DisplayName ?? info.Name : $"item {item.Value:x8}";
}
