// SPDX-License-Identifier: MIT
// Guided checks of the sync for a real player (Kiln.Testing; docs/kiln/client-pass.md). They work in a world the player
// hosts and on a server the player joined: this mod must be loaded on both sides (in a hosted game that is one copy).
// What code can assert, it asserts: the player's game reports every change of its copies back over the mod's channel
// (OnSynced -> ClientCopy), and in a hosted game the check reads the game's copies directly. The player confirms the
// notices on screen.
using Kiln;
using Kiln.Testing;

namespace InstanceSyncTest;

public sealed partial class InstanceSyncTestMod
{
    private const string Since = "0.2.0-dev.27";
    private const string Area = "Item data";
    private readonly List<(EntityId Player, ClientCopy Copy, DateTime At)> _copies = [];
    private static ItemInstanceRef _sword;
    private static int _swordCount;

    private void OnClientCopy(ClientCopy m, NetContext ctx)
    {
        if (ctx.Sender.IsLab) return;
        _copies.Add((ctx.Player, m, DateTime.UtcNow));
        if (_copies.Count > 512) _copies.RemoveRange(0, 256);
        Log.Info($"instance-sync-test: {ctx.Sender.Name}'s game: {m.Key} {m.Change} {m.Count} on entity {m.Entity} (holder {m.Holder}, slot {m.Slot}, own {m.Own})");
    }

    /// <summary>Waits up to <paramref name="seconds"/> for a report of <paramref name="player"/>'s game that fits.</summary>
    private async Task<ClientCopy?> Reported(CheckContext c, EntityId player, DateTime since, Func<ClientCopy, bool> fits, double seconds = 5)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var hit = _copies.LastOrDefault(x => x.Player == player && x.At >= since && fits(x.Copy));
            if (hit.Copy is not null) return hit.Copy;
            if (DateTime.UtcNow >= until) return null;
            await Context.Scheduler.NextTick();
            c.Cancellation.ThrowIfCancellationRequested();
        }
    }

    private static bool Hosted => Client.IsClient && Client.IsHosting;

    private async Task<ItemInstanceRef> GiveSword(CheckContext c)
    {
        var sword = Items.Lookup("Weapon_T1_1H_SwordEpic_01")
                    ?? Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0);
        if (sword is null)
        {
            c.Expect(false, "the item catalog has a gear weapon");
            return ItemInstanceRef.None;
        }
        var g = await Items.Give(c.Player, sword.Id, 1, new Gear(Math.Max(1, sword.LevelMin), ItemRarity.Rare), GiveOptions.Notify);
        var r = g.Changes.FirstOrDefault(x => !x.Ref.IsNone).Ref;
        c.Expect(g.Succeeded && !r.IsNone, $"a {sword.Label} was put into your backpack", g.ToString());
        return r;
    }

    [ClientCheck("instances.synced", "A value on your item reaches your game", Area = Area, Since = Since, Needs = ClientCheckNeeds.Any, Order = 700)]
    public async Task Synced(CheckContext c)
    {
        c.Expect(Capabilities.Is("saves.instance_data") && Capabilities.Is("saves.instance_data.sync"), "saves.instance_data and its sync are available",
            $"{Capabilities.Why("saves.instance_data")} / {Capabilities.Why("saves.instance_data.sync")}");
        var peer = Network.PeerOf(c.Player);
        c.Expect(peer is { State: NetPeerState.Ready } && peer.HasChannel("kiln:instance"), "your game has the channel kiln:instance open",
            peer is null ? "no connection of yours is known" : $"{peer}, channel: {peer.HasChannel("kiln:instance")}");
        _sword = await GiveSword(c);
        if (_sword.IsNone)
        {
            await c.Verdict();
            return;
        }
        var t0 = DateTime.UtcNow;
        _sword.Set(Held, new Tally { Count = 7 });
        _sword.Set(Secret, new Tally { Count = 99 });
        var first = await Reported(c, c.Player, t0, x => x.Key == "held" && x.Change == "Set" && x.Count == 7);
        c.Expect(first is { Own: true } && first.Entity == _sword.Entity.Id.Value, "your game reported its copy: held = 7, on the sword, yours",
            first?.ToString() ?? "no report within 5 s");
        if (first is not null) c.Log.Info($"instance-sync-test: the copy arrived {(_copies.Last(x => x.Copy == first).At - t0).TotalMilliseconds:0} ms after Set");
        var t1 = DateTime.UtcNow;
        _sword.Get(Held).Count = 8;  // in place, no Set
        var second = await Reported(c, c.Player, t1, x => x.Key == "held" && x.Change == "Set" && x.Count == 8);
        c.Expect(second is not null, "a change of the live object reached your game too: held = 8", second?.ToString() ?? "no report within 5 s");
        c.Expect(!_copies.Any(x => x.Player == c.Player && x.Copy.Key == "secret"), "the key without Sync never reached your game");
        // a game whose acknowledgements do not reach the server is started over ten seconds after an update: its copy would
        // go and come again (Gone, then Set). Twelve quiet seconds say the server hears your game's acknowledgements.
        var t2 = DateTime.UtcNow;
        var redo = await Reported(c, c.Player, t2, x => x.Entity == _sword.Entity.Id.Value && x.Change == "Gone", 12);
        c.Expect(redo is null, "your game acknowledged the updates: no start over in the next 12 seconds (the copy did not go and come again)",
            redo?.ToString() ?? "");
        if (Hosted)
        {
            // one process: the game's own copies can be read here
            var copy = InstanceData.Synced(_sword.Entity.Id, Held);
            c.Expect(copy is { Count: 8 }, "InstanceData.Synced reads 8 from this game's copies", copy is null ? "no copy" : $"count {copy.Count}");
            c.Expect(InstanceData.Synced(_sword.Entity.Id) is { } item && !item.Has(Secret) && item.Own, "the copy is yours and has no 'secret'");
        }
        _swordCount = 8;
        c.Instruct("A sword was put into your backpack. Two green notices appeared in the corner: \"sync: held = 7 (your item, slot N)\" and then " +
                   "\"sync: held = 8 ...\". Pass if you saw both (the slot number is the sword's: backpack slots count from 0, row by row). " +
                   "Fail with what you saw instead. Keep the sword for the next check.");
        await c.Verdict();
    }

    [ClientCheck("instances.synced-moves", "The copy follows the item, and leaves with it", Area = Area, Since = Since, Needs = ClientCheckNeeds.Any, Order = 701)]
    public async Task SyncedMoves(CheckContext c)
    {
        if (!c.Expect(!_sword.IsNone && _sword.Has(Held), "instances.synced ran first and its sword still carries the value"))
        {
            c.Instruct("Run \"instances.synced\" first (and keep its sword), then this check. Skip now.");
            await c.Verdict();
            return;
        }
        var t0 = DateTime.UtcNow;
        c.Instruct("Open your backpack and drag the sword of the last check to another backpack slot. A notice says \"sync: held = 8, moved (your item, slot M)\".");
        var moved = await Reported(c, c.Player, t0, x => x.Key == "held" && x.Change == "Moved", 90);
        c.Expect(moved is { Count: 8 }, "your game reported the move with the same value", moved?.ToString() ?? "no report within 90 s");
        if (moved is not null)
            c.Expect(_sword.Location() is { } at && at.FlatSlot == moved.Slot, "the slot your game holds is the slot the server reads", $"{moved.Slot} vs {_sword.Location()}");
        var t1 = DateTime.UtcNow;
        c.Instruct("Now put the sword into a chest (place one if none is near). A yellow notice says \"sync: held gone\": the value is the holder's, and a chest is no player.");
        var gone = await Reported(c, c.Player, t1, x => x.Key == "held" && x.Change == "Gone", 180);
        c.Expect(gone is not null, "your game reported that its copy is gone", gone?.ToString() ?? "no report within 180 s");
        c.Expect(_sword.TryGet(Held, out var kept) && kept.Count == 8, "the server still has the value on the sword (in the chest)");
        var t2 = DateTime.UtcNow;
        c.Instruct("Take the sword back out of the chest. A green notice says \"sync: held = 8\" again.");
        var back = await Reported(c, c.Player, t2, x => x.Key == "held" && x.Change == "Set" && x.Count == 8, 180);
        c.Expect(back is { Own: true }, "your game got the value again with the item", back?.ToString() ?? "no report within 180 s");
        c.Instruct("Pass if you saw the three notices in that order: moved, gone, held = 8 again. Fail with what you saw instead.");
        await c.Verdict();
    }

    [ClientCheck("instances.synced-rejoin", "The copies come again after a rejoin", Area = Area, Since = Since, Needs = ClientCheckNeeds.Joined, Order = 702)]
    public async Task SyncedRejoin(CheckContext c)
    {
        // The sword of "instances.synced" has a new item entity after a rejoin; the value finds it by its slot (InstanceData).
        var mine = InstanceData.All(Held).Where(x => x.Item.Location() is { } at && at.Holder == c.Player).ToList();
        if (!c.Expect(mine.Count > 0, "an item of yours carries 'held' (run instances.synced, leave through the menu, join again, then this check)"))
        {
            c.Instruct("Nothing of yours carries the value: run \"instances.synced\", keep its sword in your backpack, leave to the main menu, join " +
                       "this server again and run this check. Skip now.");
            await c.Verdict();
            return;
        }
        var want = mine[0];
        var got = _copies.LastOrDefault(x => x.Player == c.Player && x.Copy.Key == "held" && x.Copy.Entity == want.Item.Entity.Id.Value).Copy;
        c.Expect(got is { Change: "Set" or "Moved" } && got.Count == want.Value.Count,
            $"after your join your game reported held = {want.Value.Count} for the item entity it has now, without anything being set",
            got?.ToString() ?? "no report for that item entity");
        c.Instruct($"When you joined, a green notice said \"sync: held = {want.Value.Count} (your item, slot N)\" within a second or two, without anyone " +
                   "changing anything. Pass if you saw it. Fail if no notice came, or it showed another number.");
        await c.Verdict();
    }

    [ClientCheck("instances.synced-nearby", "A value for players nearby reaches the other player (two players)", Area = Area, Since = Since,
        Needs = ClientCheckNeeds.Joined, Order = 703)]
    public async Task SyncedNearby(CheckContext c)
    {
        var other = Players.All.FirstOrDefault(p => p.InGame && p.Entity != c.Player);
        if (!c.Expect(other is not null, "a second player is in game (both with Kiln and this mod)") ||
            !c.Expect(!_sword.IsNone && _sword.Has(Held) && _sword.Location() is { } at && at.Holder == c.Player, "you hold the sword of instances.synced"))
        {
            c.Instruct("This check needs a second player with Kiln and this mod, and the sword of \"instances.synced\" in your backpack. Skip if you are alone.");
            await c.Verdict();
            return;
        }
        c.Instruct($"Stand within a few steps of {other!.Name}, then wait.");
        var near = false;
        for (var i = 0; i < 25 * 120 && !near; i++)
        {
            near = Players.PositionOf(c.Player) is { } a && Players.PositionOf(other.Entity) is { } b && a.DistanceTo(b) < 6;
            await Context.Scheduler.NextTick();
            c.Cancellation.ThrowIfCancellationRequested();
        }
        c.Expect(near, "the two of you stood within 6 m");
        var t0 = DateTime.UtcNow;
        _sword.Set(Seen, new Tally { Count = 21 });
        var theirs = await Reported(c, other.Entity, t0, x => x.Key == "seen" && x.Change == "Set" && x.Count == 21);
        c.Expect(theirs is { Own: false } && theirs.Holder == c.Player.Value, $"{other.Name}'s game reported seen = 21 on your sword, as another holder's item",
            theirs?.ToString() ?? "no report within 5 s");
        c.Expect(!_copies.Any(x => x.Player == other.Entity && x.Copy.Key == "held" && x.Copy.Entity == _sword.Entity.Id.Value),
            $"{other.Name}'s game never got 'held': that key is the holder's only");
        var t1 = DateTime.UtcNow;
        c.Instruct($"{other.Name} saw \"sync: seen = 21 (another holder's item, slot N)\". Now one of you walks more than 10 m away: {other.Name} sees \"sync: seen gone\".");
        var gone = await Reported(c, other.Entity, t1, x => x.Key == "seen" && x.Change == "Gone", 120);
        c.Expect(gone is not null, $"{other.Name}'s game reported the copy gone once out of range", gone?.ToString() ?? "no report within 120 s");
        c.Instruct($"Pass if {other.Name} saw both notices (seen = 21, then gone after walking away) and you saw \"sync: seen = 21 (your item ...)\" yourself.");
        await c.Verdict();
    }
}
