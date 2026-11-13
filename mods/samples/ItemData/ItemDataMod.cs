// SPDX-License-Identifier: MIT
// Data on one item (docs/kiln/modding/inventory.md, "Data on one item"): /notch cuts a notch into the weapon or tool in
// your hand. The count belongs to that one item, not to its slot or to its kind: move it to a chest, swap it with an
// identical sword, save, restart, and it is still on it. /notches lists every notched item.
//
// The mod runs on both sides (kiln.mod.json "side": "both"). The server keeps the counts; the key says the player who
// holds the item may see its count (Sync = InstanceSync.Holder), so Kiln sends it to that player's game, where this same
// mod shows it as a notice. A player without the mod, or without Kiln, plays as before and sees nothing of it.
using Kiln;

namespace ItemData;

/// <summary>The value kept on an item: any class with a parameterless constructor that System.Text.Json can write.</summary>
public sealed class Notches
{
    public int Count { get; set; }
    public string? LastBy { get; set; }
}

public sealed class ItemDataMod : Mod
{
    // Capability rows (the /caps command lists them): the data is Kiln.Saves' item data, which needs a world with items,
    // and "the item in your hand" is Kiln.Items' item instances. A game client that only joins a server has neither.
    private const string Data = "saves.instance_data", InHand = "items.instances";

    private InstanceKey<Notches> _notches = null!;

    public override void OnLoad()
    {
        // One typed key per kind of value, declared the same way on the server and in a player's game. Persist.World (the
        // default) keeps it in this world's save, in a chest or in a player's backpack alike. Sync.Holder sends it to the
        // game of the player who holds the item.
        _notches = InstanceData.Key<Notches>("notches", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        // The server's calls need a world with items: a game client that only joins a server has none.
        Log.Info(Capabilities.Is("saves.instance_data")
            ? $"item-data: key {_notches} declared; {InstanceData.All(_notches).Count} notched item(s) in this session so far"
            : $"item-data: key {_notches} declared; no item data of its own here ({Capabilities.Why("saves.instance_data")})");
    }

    // In the player's game: the server's copy of a count arrived or changed. The copy is read-only; e.Item says which item
    // (its slot in the holder, its type) and whether this game's player holds it.
    [Subscribe]
    private void OnSynced(InstanceDataSyncedEvent e)
    {
        if (e.Mod != Context.Info.Id || e.Change != InstanceSyncChange.Set || !e.Item.TryGet(_notches, out var n)) return;
        Log.Info($"item-data: the server says {n.Count} notch(es) on {e.Item}");
        if (Ui.Available) Hud.Toast($"{n.Count} notch(es) on the item in slot {e.Item.FlatSlot}");
    }

    [Command("notch", Description = "cut a notch into the weapon or tool in your hand")]
    private string Notch(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a hand");
        if (!Capabilities.Is(Data) || !Capabilities.Is(InHand)) throw new CommandException(NotHere(Data, InHand));
        // The item in hand as one particular item. None: an empty hand, or a plain stack (wood, arrows), which has no
        // item of its own to keep data on.
        var held = Items.Held(c.Source.Player);
        if (held.IsNone) throw new CommandException("hold a weapon or a tool");
        var notches = held.Get(_notches);   // the live object, created on first use; saved with the world
        notches.Count++;
        notches.LastBy = c.Source.Name;
        return $"{Items.Get(held.Item)?.Label ?? held.Item.ToString()}: {notches.Count} notch(es)";
    }

    [Command("notches", Description = "every notched item, and the records waiting for theirs", Permission = CommandPermission.Admin)]
    private void List(CommandContext c)
    {
        if (!Capabilities.Is(Data) || !Capabilities.Is(InHand)) throw new CommandException(NotHere(Data, InHand));
        var all = InstanceData.All(_notches);
        c.Reply($"{all.Count} notched item(s) in this session");
        foreach (var (item, n) in all)
            c.Reply($"  {Items.Get(item.Item)?.Label ?? item.Item.ToString()} at {item.Location()?.ToString() ?? "?"}: {n.Count} (last by {n.LastBy})");
        // Records not on an item now: waiting for their holder (a chest not loaded yet, a player who left) or set aside
        // because Kiln could not be sure which item they belong to.
        foreach (var p in InstanceData.Parked(_notches))
            c.Reply($"  {p.State} {p.RecordId}: {p.Item} in {p.Holder} slot {p.FlatSlot}: {p.Value?.Count} ({p.Reason})");
    }

    // A set-aside record is never attached by itself: Kiln could not tell which of two identical items it belongs to. The
    // player who knows takes the item in hand and says which record is its own.
    [Command("notch-claim", Description = "puts a set-aside notch record (its id from /notches) on the item in your hand")]
    private string Claim(CommandContext c, string record)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a hand");
        if (!Capabilities.Is(Data) || !Capabilities.Is(InHand)) throw new CommandException(NotHere(Data, InHand));
        var held = Items.Held(c.Source.Player);
        if (held.IsNone) throw new CommandException("hold the weapon or tool the notches belong to");
        var parked = InstanceData.Parked(_notches).FirstOrDefault(p => p.RecordId == record)
                     ?? throw new CommandException($"no parked notch record {record} (see /notches)");
        var r = InstanceData.Claim(parked, held);
        return r.Ok ? $"the {parked.Value?.Count} notch(es) are on this item now" : $"not claimed: {r.Reason}";
    }

    [Command("notch-drop", Description = "drops a parked notch record (its id from /notches)", Permission = CommandPermission.Admin)]
    private string Drop(CommandContext c, string record)
    {
        if (!Capabilities.Is(Data)) throw new CommandException(NotHere(Data));
        var parked = InstanceData.Parked(_notches).FirstOrDefault(p => p.RecordId == record)
                     ?? throw new CommandException($"no parked notch record {record} (see /notches)");
        return InstanceData.Discard(parked) ? "dropped" : "it was no longer parked";
    }

    // The three things that happen to stored data, as events.

    [Subscribe]
    private void OnRestored(InstanceDataRestoredEvent e)
    {
        if (e.Mod == Context.Info.Id) Log.Info($"item-data: back on {e.Item} ({e.Match}): {e.Detail}");
    }

    [Subscribe]
    private void OnParked(InstanceDataParkedEvent e)
    {
        if (e.Mod == Context.Info.Id) Log.Info($"item-data: waiting: {e.Item} of {e.Holder} slot {e.FlatSlot} ({e.Reason})");
    }

    [Subscribe]
    private void OnLost(InstanceDataLostEvent e)
    {
        if (e.Mod == Context.Info.Id) Log.Info($"item-data: lost on {e.Item} ({e.Reason}): {e.Detail}");
    }

    // A command's answer when a row it needs is off: the first such row, and why.
    private static string NotHere(params string[] rows) =>
        rows.FirstOrDefault(row => !Capabilities.Is(row)) is { } off ? $"not on this server ({off} is off: {Capabilities.Why(off)})" : "not on this server right now";
}
