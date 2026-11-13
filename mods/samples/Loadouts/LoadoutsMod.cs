// SPDX-License-Identifier: MIT
// Loadouts: Items.Equip and Items.Unequip, the game's own equip run for a mod (docs/kiln/services/items.md).
//   /loadout save <name>    remembers which kind of gear you wear in each slot
//   /loadout wear <name>    puts gear of those kinds on again from your backpack
//   /loadout check <name>   asks the game about each slot without changing anything (EquipOptions.DryRun)
//   /loadout off <slot>     takes off what one slot holds, into the backpack (/loadout off Ring0)
// A loadout keeps item types, not the single pieces: a sword's level, or what a mod stored on that one sword, is not
// part of it, and any sword of the type will do. Stacks (arrows, bait) are left out.
// What to take from it:
//   - ask every capability a command needs before anything is changed, and say which one is off. A row can lag a
//     moment behind its service, and a service can refuse for a reason that has no row, so the command also turns
//     the "not available" exception into a sentence;
//   - an equip is a task that ends when the game ran it, a step or two later. A refusal is a result with a reason (the
//     item does not fit the slot, the backpack is full, the player died), not an exception;
//   - after every await the world may have moved on: the player can have left, and their entity id then names
//     whoever joins next. Keep a PlayerRef, which knows the session, and ask it before the next read, call or reply;
//   - read the backpack right before each equip, because every equip moves items.
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
    private const string Usage = "save <name>, wear <name>, check <name>, or off <slot>";
    private const int MaxSets = 8, MaxName = 24;
    // Capability rows (the /caps command lists them). Reading what a player wears needs the game's item instances,
    // changing it needs Kiln's equip, and telling one session of a player from the next needs the two identity rows.
    // The item rows are off on a game build Kiln does not know them for, and equip also on a game that is in its
    // menu; the identity rows say that a service answers for players and entities at all.
    private const string Gear = "items.instances", Backpack = "items.read", Equip = "items.equip";
    private const string Sessions = "identity.players", Entities = "identity.entities";
    private static readonly string[] ToRead = [Gear], ToChange = [Gear, Backpack, Equip, Sessions, Entities];

    // Nothing is asked in OnLoad. The rows of services that start with the server's first tick (the identity rows are
    // two of them) are still pending while mods load, so an answer taken here would be "not available" on a server
    // where everything works a moment later. Ask where you use a thing, as the commands below do.

    [Command("loadout", Description = "save <name>, wear <name>, check <name>, or off <slot>: the gear you wear, kept by kind and put on again")]
    private async Task Loadout(CommandContext c, string action, string? name = null)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player wears things");
        if (string.IsNullOrWhiteSpace(name)) throw new CommandException(Usage);
        try
        {
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
                    throw new CommandException(Usage);
            }
        }
        catch (Exception e) when (e is KilnUnavailableException or NotSupportedException)
        {
            // The rows were on when they were asked. A row is brought up to date about once a second, and a call can
            // need more than its row says (the game's type reflection, a world that can be read): the call then throws
            // what the contract names for "not available", and the player gets a sentence, not a stack trace. Wear
            // and Off come here only in a step in which they know the player is still the one who asked.
            throw new CommandException($"this server cannot do that right now: {e.Message}");
        }
    }

    private static string Save(CommandContext c, string name)
    {
        if (name.Length > MaxName) throw new CommandException($"a loadout's name may have at most {MaxName} characters");
        Need(ToRead);
        var wardrobe = Mine(c);
        if (!wardrobe.Sets.ContainsKey(name) && wardrobe.Sets.Count >= MaxSets)
            throw new CommandException($"you have {MaxSets} loadouts already: save over one of {string.Join(", ", wardrobe.Sets.Keys)}");
        var worn = new Dictionary<string, uint>();
        foreach (var slot in Enum.GetValues<EquipmentSlot>())
            if (Items.Equipped(c.Source.Player, slot) is { IsNone: false } item)
                worn[slot.ToString()] = item.Item.Value;
        if (worn.Count == 0) throw new CommandException("you wear no gear a loadout could keep");
        wardrobe.Sets[name] = worn;  // the live object of PlayerData: written with the world's save, nothing to flush
        return $"loadout {name}: {worn.Count} kind(s) of gear kept";
    }

    private static async Task Wear(CommandContext c, string name, EquipOptions options)
    {
        Need(ToChange);
        if (!Mine(c).Sets.TryGetValue(name, out var set)) throw new CommandException($"you have no loadout {name}");
        var player = c.Source.Player;
        // The session, not only the entity id: a player slot's id goes to the next player who joins.
        var who = Session(c);
        var dry = options.HasFlag(EquipOptions.DryRun);
        int on = 0;
        foreach (var (slotName, itemId) in set)
        {
            // A name or number that is no slot of this Kiln: saved under a later Kiln, or the saved data was edited.
            if (!Enum.TryParse<EquipmentSlot>(slotName, out var slot) || !Enum.IsDefined(slot)) continue;
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
            // Back from the await. First: is this still the player who asked? If not, stop without a word, because a
            // reply goes to the entity id, and so would the next equip.
            if (Left(who)) return;
            if (result.Ok) on++;
            else c.Reply($"  {slot}: {Named(item)} {(dry ? "would not go on" : "was not put on")}: {result.Reason}");
            // Second: a capability can go off while a request waits (the world closed, equip was switched off after
            // a fault). The next call would throw, so ask again and stop with a sentence.
            if (Missing(ToChange) is { } off)
            {
                c.Reply($"stopped at {slot}: {off} went off ({Capabilities.Why(off)})");
                return;
            }
        }
        // A dry run asks about each slot by itself and takes everything back, so it does not see what the equips
        // before it would have changed: two slots that want the same single ring both say yes.
        c.Reply(dry ? $"loadout {name}: {on} of {set.Count} slot(s) would take their item, each asked by itself" : $"loadout {name}: {on} of {set.Count} on");
    }

    private static async Task Off(CommandContext c, string slotName)
    {
        if (!Enum.TryParse<EquipmentSlot>(slotName, ignoreCase: true, out var slot) || !Enum.IsDefined(slot))
            throw new CommandException($"no slot {slotName}: one of {string.Join(", ", Enum.GetNames<EquipmentSlot>())}");
        Need(ToChange);
        var who = Session(c);
        // An empty slot and a full backpack are refusals too: the reason is a sentence for the player.
        var result = await Items.Unequip(c.Source.Player, slot);
        if (Left(who)) return;
        c.Reply(result.Ok ? $"{slot}: {Named(result.Item)} is in your backpack" : $"{slot}: nothing taken off: {result.Reason}");
    }

    // Every equip Kiln ran for any mod, once it happened (never for a refusal or a dry run). What a player changes by
    // hand is Kiln.Players' PlayerEquipmentChangedEvent.
    [Subscribe]
    private void OnEquipChanged(EquipChangedEvent e) =>
        Log.Debug($"loadouts: {e.Player} {e.Slot}: {Named(e.BeforeItem)} -> {Named(e.AfterItem)}");

    // The first of the capabilities that is off, or null.
    private static string? Missing(string[] capabilities) => capabilities.FirstOrDefault(capability => !Capabilities.Is(capability));

    private static void Need(string[] capabilities)
    {
        if (Missing(capabilities) is { } off)
            throw new CommandException($"this server cannot do that ({off} is off: {Capabilities.Why(off)})");
    }

    private static PlayerRef Session(CommandContext c)
    {
        var who = PlayerRef.Of(c.Source.Player);
        if (!who.IsOnline) throw new CommandException("your session is not known yet: try again in a moment");
        return who;
    }

    // True when the session a command came from has ended. When the server can no longer tell (the row went off),
    // that counts as ended: better to stop than to act on whoever has the id now.
    private static bool Left(PlayerRef who)
    {
        try
        {
            return !Capabilities.Is(Entities) || !who.IsOnline;
        }
        catch (KilnUnavailableException)
        {
            return true;
        }
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
