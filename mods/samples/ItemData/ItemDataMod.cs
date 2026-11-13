// SPDX-License-Identifier: MIT
// Data on one item (docs/kiln/modding/inventory.md, "Data on one item"): /notch cuts a notch into the weapon or tool in
// your hand. The count belongs to that one item, not to its slot or to its kind: move it to a chest, swap it with an
// identical sword, save, restart, and it is still on it. /notches lists every notched item.
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
    private InstanceKey<Notches> _notches = null!;

    public override void OnLoad()
    {
        // One typed key per kind of value. Persist.World (the default) keeps it in this world's save, in a chest or in a
        // player's backpack alike.
        _notches = InstanceData.Key<Notches>("notches");
        Log.Info($"item-data: key {_notches} declared; {InstanceData.All(_notches).Count} notched item(s) in this session so far");
    }

    [Command("notch", Description = "cut a notch into the weapon or tool in your hand")]
    private string Notch(CommandContext c)
    {
        if (!c.Source.IsPlayer) throw new CommandException("only a player has a hand");
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
}
