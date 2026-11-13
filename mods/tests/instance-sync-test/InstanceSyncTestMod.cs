// SPDX-License-Identifier: MIT
// Instance data on players' games (dev.27, channel kiln:instance), tested two ways:
//  - in a lab with no player: syncnet.py's fake clients (tools/nettest) stand for the holders of two chests and for a
//    player near them, and ask this mod over the control channel to change the items' values. The fake clients read what
//    the server sends them and print PASS/FAIL; this mod logs "instance-sync-test: ..." lines of what it did.
//  - with a player (ClientChecks.cs): the mod runs on both sides. The server side sets values on the player's sword, the
//    client side shows every change of its copy as a notice on screen and reports it back, so a check asserts in code
//    what the player's game holds.
using System.Text.Json;
using Kiln;

namespace InstanceSyncTest;

/// <summary>The value the test keeps on a sword.</summary>
public sealed class Tally
{
    public int Count { get; set; }
    public string? Text { get; set; }
}

/// <summary>Lab peer to server: do one thing. <see cref="Item"/> is the index into the swords of "setup".</summary>
public sealed record CtlRequest(string Op, int Item, string? Key, int Count, int Size);

public sealed record CtlReply(string Op, bool Ok, string Data);

/// <summary>A player's game to the server: a copy it holds changed (the guided checks assert on these).</summary>
public sealed record ClientCopy(uint Entity, uint Item, uint Holder, int Slot, string Key, string Change, int Count, bool Own);

public sealed partial class InstanceSyncTestMod : Mod
{
    internal InstanceKey<Tally> Held = null!, Seen = null!, Secret = null!;
    private NetChannel _ctl = null!;
    private EntityId _a, _b;
    private readonly List<ItemInstanceRef> _items = [];

    public override void OnLoad()
    {
        // The same declarations on both sides: the server sends by them, the client reads with them.
        Held = InstanceData.Key<Tally>("held", new InstanceKeyOptions { Sync = InstanceSync.Holder });
        Seen = InstanceData.Key<Tally>("seen", new InstanceKeyOptions { Sync = InstanceSync.Nearby, NearbyRadius = 10 });
        Secret = InstanceData.Key<Tally>("secret");
        _ctl = Network.Channel("instance-sync-test:ctl", "1", required: false, maxMessageBytes: 256 * 1024)
            .RegisterJson<CtlRequest>(OnCtl, NetDirection.ToServer)
            .RegisterJson<CtlReply>((_, _) => { }, NetDirection.ToClient)
            .RegisterJson<ClientCopy>(OnClientCopy, NetDirection.ToServer);
        var n = Kiln.Testing.ClientChecks.Register(this);
        Log.Info($"instance-sync-test: loaded on a {(Client.IsClient ? "game client" : "dedicated server")}; keys {Held} ({Held.Sync}), {Seen} ({Seen.Sync}), " +
                 $"{Secret} ({Secret.Sync}); {Capabilities.Why("saves.instance_data.sync")}; {n} guided check(s)");
    }

    // ---------------------------------------------------------------------------------------- the client side ----

    /// <summary>A copy this game holds changed: say so on screen, in the log, and to the server.</summary>
    [Subscribe]
    private void OnSynced(InstanceDataSyncedEvent e)
    {
        if (e.Mod != Context.Info.Id) return;
        var count = e.Change is InstanceSyncChange.Set or InstanceSyncChange.Moved && e.Item.Json(e.Mod, e.Key) is { } json
            ? JsonSerializer.Deserialize<Tally>(json)?.Count ?? -1
            : -1;
        var what = e.Change switch
        {
            InstanceSyncChange.Set => $"{e.Key} = {count}",
            InstanceSyncChange.Removed => $"{e.Key} removed",
            InstanceSyncChange.Moved => $"{e.Key} = {count}, moved",
            _ => $"{e.Key} gone",
        };
        var text = $"sync: {what} ({(e.Item.Own ? "your" : "another holder's")} item, slot {e.Item.FlatSlot})";
        Log.Info($"instance-sync-test: client: {text}; entity {e.Item.Entity}, item {e.Item.Item}, holder {e.Item.Holder}");
        if (Ui.Available) Hud.Toast(text, e.Change == InstanceSyncChange.Gone ? ToastKind.Warning : ToastKind.Success, 6);
        if (Network.ServerHasKiln)
            _ctl.SendToServer(new ClientCopy(e.Item.Entity.Value, e.Item.Item.Value, e.Item.Holder.Value, e.Item.FlatSlot, e.Key, e.Change.ToString(), count, e.Item.Own));
    }

    // ------------------------------------------------------------------------------- the lab's control channel ----

    private void OnCtl(CtlRequest m, NetContext ctx)
    {
        if (!ctx.Sender.IsLab) return;  // a real player never drives the lab's part
        _ = Answer(m, ctx);
    }

    private async Task Answer(CtlRequest m, NetContext ctx)
    {
        string data;
        var ok = true;
        try
        {
            data = await Run(m);
        }
        catch (Exception e)
        {
            ok = false;
            data = $"{e.GetType().Name}: {e.Message}";
        }
        Log.Info($"instance-sync-test: {m.Op} item {m.Item} key {m.Key} count {m.Count}: {(ok ? "ok" : "FAILED")} {(data.Length > 300 ? data[..300] + "..." : data)}");
        ctx.Reply(new CtlReply(m.Op, ok, data));
    }

    private InstanceKey<Tally> KeyOf(string? name) => name switch
    {
        "held" => Held,
        "seen" => Seen,
        "secret" => Secret,
        _ => throw new ArgumentException($"no key '{name}'"),
    };

    private async Task<string> Run(CtlRequest m)
    {
        switch (m.Op)
        {
            case "caps":
                return JsonSerializer.Serialize(new
                {
                    Sync = Capabilities.Is("saves.instance_data.sync"),
                    Why = Capabilities.Why("saves.instance_data.sync"),
                    Data = Capabilities.Is("saves.instance_data"),
                    WhyData = Capabilities.Why("saves.instance_data"),
                });
            case "setup":
                return await Setup();
            case "list":
                return List();
            case "cleanup":
                return await Cleanup();
            case "set":
                _items[m.Item].Set(KeyOf(m.Key), new Tally { Count = m.Count, Text = m.Size > 0 ? new string((char)('a' + m.Item), m.Size) : null });
                return "";
            case "edit":
                _items[m.Item].Get(KeyOf(m.Key)).Count = m.Count;  // the live object, in place: no Set, no event
                return "";
            case "remove":
                return _items[m.Item].Remove(KeyOf(m.Key)) ? "removed" : "had none";
            case "where":
                return Where(m.Item);
            case "move":
            {
                var item = _items[m.Item];
                var at = item.Location() ?? throw new InvalidOperationException("the item is gone");
                var to = at.Holder == _a ? _b : _a;
                var r = await Items.Move(at.Holder, at.Slot, to);
                if (!r.Succeeded) throw new InvalidOperationException($"move: {r.Status} {r.Message}");
                return Where(m.Item);
            }
            case "destroy":
            {
                var item = _items[m.Item];
                var at = item.Location() ?? throw new InvalidOperationException("the item is gone already");
                var r = await Items.Take(at.Holder, item.Item, 1, at.Slot, true, item.Entity.Id);
                if (!r.Succeeded) throw new InvalidOperationException($"take: {r.Status} {r.Message}");
                return "destroyed";
            }
            default:
                throw new ArgumentException($"no op '{m.Op}'");
        }
    }

    /// <summary>Every item of this session that carries one of the test's values, with where it is and what it carries:
    /// after a restart, what the store put back on the items.</summary>
    private string List()
    {
        var rows = new List<object>();
        foreach (var item in InstanceData.All(Held).Select(x => x.Item).Concat(InstanceData.All(Seen).Select(x => x.Item)).Concat(InstanceData.All(Secret).Select(x => x.Item)).Distinct())
        {
            var at = item.Location();
            item.TryGet(Held, out var held);
            item.TryGet(Seen, out var seen);
            rows.Add(new
            {
                Entity = item.Entity.Id.Value, Item = item.Item.Value, Holder = at?.Holder.Value ?? 0, Flat = at?.FlatSlot ?? -1,
                Held = held?.Count, HeldText = held?.Text?.Length ?? 0, Seen = seen?.Count, SeenText = seen?.Text?.Length ?? 0, Secret = item.Has(Secret),
            });
        }
        return JsonSerializer.Serialize(rows);
    }

    /// <summary>Takes every sword an earlier run left with a value out of the world, so a run starts from nothing.</summary>
    private async Task<string> Cleanup()
    {
        var n = 0;
        foreach (var item in InstanceData.All(Held).Select(x => x.Item).Concat(InstanceData.All(Seen).Select(x => x.Item)).Concat(InstanceData.All(Secret).Select(x => x.Item)).Distinct().ToList())
        {
            if (item.Location() is not { IsPlaced: true } at) continue;
            var r = await Items.Take(at.Holder, item.Item, 1, at.Slot, true, item.Entity.Id);
            if (r.Succeeded) n++;
        }
        _items.Clear();
        return $"{n} item(s) of an earlier run taken";
    }

    private string Where(int index)
    {
        var item = _items[index];
        var at = item.Location();
        return JsonSerializer.Serialize(new { Entity = item.Entity.Id.Value, Item = item.Item.Value, Holder = at?.Holder.Value ?? 0, Flat = at?.FlatSlot ?? -1 });
    }

    /// <summary>Two chests of the lab's world, two swords into the first and one into the second.</summary>
    private async Task<string> Setup()
    {
        if (_items.Count == 0)
        {
            var sword = Items.Lookup("Weapon_T1_1H_SwordEpic_01")
                        ?? Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0)
                        ?? throw new InvalidOperationException("no gear weapon in the item catalog");
            var chests = new List<(EntityId Entity, WorldPosition At)>();
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
                if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 6 && Spawn.Inspect(e) is { TemplateName: not null } info)
                    chests.Add((e, info.Position));
            if (chests.Count < 2) throw new InvalidOperationException($"the world has {chests.Count} container(s) with 6 free slots; the test needs two");
            // the two farthest apart (a base's chests stand close together: the fake clients say where they stand themselves)
            var pair = (from x in chests from y in chests where x.Entity.Value < y.Entity.Value orderby x.At.DistanceTo(y.At) descending select (x, y)).First();
            (_a, _b) = (pair.x.Entity, pair.y.Entity);
            foreach (var holder in new[] { _a, _a, _b })
            {
                var free = Items.Read(holder)!.Slots.First(s => s.IsEmpty && s.Category == InventoryCategory.Generic);
                var g = await Items.Give(holder, sword.Id, 1, new Gear(Math.Max(1, sword.LevelMin), ItemRarity.Rare), slot: free.Slot);
                var change = g.Changes.FirstOrDefault(c => !c.Ref.IsNone);
                if (!g.Succeeded || change.Ref.IsNone) throw new InvalidOperationException($"give into {holder}: {g}");
                _items.Add(change.Ref);
            }
        }
        var pa = Spawn.Inspect(_a)!.Position;
        var pb = Spawn.Inspect(_b)!.Position;
        return JsonSerializer.Serialize(new
        {
            A = _a.Value,
            B = _b.Value,
            PosA = new[] { pa.X, pa.Y, pa.Z },
            PosB = new[] { pb.X, pb.Y, pb.Z },
            Items = Enumerable.Range(0, _items.Count).Select(i => JsonSerializer.Deserialize<JsonElement>(Where(i))).ToArray(),
        });
    }
}
