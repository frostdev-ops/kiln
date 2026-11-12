// SPDX-License-Identifier: MIT
// Task t4, part 1: how the game's loader numbers a holder's item entities. The matcher's rule 1 (LoadOrder.AsMade in
// Kiln.Saves' InstanceMatcher.cs) takes an exact-slot match among look-alikes on trust only when a holder's item entities
// are "as the loader made them": ids strictly ascending in flat-slot order, one gapless run, right after the holder's own
// id. This measures it over three boots (run-t4.sh order):
//   boot 1: gear given into several chests of two or more Generic links, into scattered slots, in an order that is not the
//           slot order; a save; READY stop
//   boot 2, boot 3: every holder of the world (containers and NPCs) with item entities: its id, its inventory entities'
//           ids, each item entity's id with its flat slot; the four conditions per holder; READY stop / end
// Each boot writes kiln/data/instances-test/t4/order-boot<N>.json (copied out by the run script).
using System.Text.Json;
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    private string T4Dir => Path.Combine(Context.DataDirectory, "t4");

    internal void Ready(string what) => Log.Info($"t4: READY {what}");

    private sealed record OrderHolder(string Template, double X, double Y, double Z, List<int> Slots);

    private sealed class OrderState
    {
        public int Boot { get; set; }
        public List<OrderHolder> Holders { get; set; } = [];
    }

    [LabCheck(200, "t4.order")]
    private async Task T4Order()
    {
        Directory.CreateDirectory(T4Dir);
        var file = Path.Combine(T4Dir, "order-state.json");
        var st = File.Exists(file) ? JsonSerializer.Deserialize<OrderState>(File.ReadAllText(file))! : new OrderState();
        st.Boot++;
        Info($"t4 order: boot {st.Boot}");
        if (st.Boot == 1)
        {
            // the table before any restart: ids in give order (the control: here they do not ascend with the slots)
            await OrderSetup(st);
            OrderTable(st, "before the restart");
            File.WriteAllText(file, JsonSerializer.Serialize(st));
            var r = await Saves.Request();
            Info($"t4 order: {r}");
            Ready("stop");
            return;
        }
        OrderTable(st, $"after restart {st.Boot - 1}");
        File.WriteAllText(file, JsonSerializer.Serialize(st));
        Ready(st.Boot >= 3 ? "end" : "stop");
    }

    /// <summary>Gear into chests of two or more Generic links: up to 7 pieces each, into slots spread over the links, given
    /// highest slot first, then lowest, and so on (never in slot order).</summary>
    private async Task OrderSetup(OrderState st)
    {
        var sword = Sword()!;
        var others = Items.All.Where(i => i.IsGear && i.LevelMin > 0 && i.Id != sword.Id &&
                                          i.Category is ItemCategory.Weapons or ItemCategory.Equipment).Take(3).ToList();
        var kinds = new[] { sword }.Concat(others).ToList();
        var quiet = await QuietContainers(6, 400);
        var picked = 0;
        foreach (var h in quiet)
        {
            var inv = Items.Read(h)!;
            var generic = inv.Inventories.Count(i => i.Category == InventoryCategory.Generic);
            if (generic < 2 || Spawn.Inspect(h) is not { TemplateName: { } tn } info) continue;
            var free = inv.Slots.Where(s => s.IsEmpty && s.Category == InventoryCategory.Generic).ToList();
            // spread: every other free slot, so items of different links and gaps between them
            var spread = free.Where((_, i) => i % 2 == 0).Take(7).ToList();
            if (spread.Select(s => s.Link).Distinct().Count() < 2) spread = free.Where((_, i) => i % 3 == 0).Take(7).ToList();
            // order: last, first, second last, second, ...
            var order = new List<InventorySlot>();
            for (int lo = 0, hi = spread.Count - 1; lo <= hi; lo++, hi--)
            {
                order.Add(spread[hi]);
                if (lo != hi) order.Add(spread[lo]);
            }
            var slots = new List<int>();
            var k = 0;
            foreach (var s in order)
            {
                var item = kinds[k++ % kinds.Count];
                var g = await Items.Give(h, item.Id, 1, new Gear(Math.Max(1, item.LevelMin), ItemRarity.Rare), slot: s.Slot);
                var ch = g.Changes.FirstOrDefault(c => !c.Ref.IsNone);
                var loc = ch.Ref.IsNone ? null : ch.Ref.Location();
                Check(g.Succeeded && loc is { IsSavedSlot: true }, $"t4 order setup: {item.Name} into {h} slot {s.Slot} (link {s.Link}): {g}, entity {ch.ItemEntity}, location {loc}");
                if (loc is { } l) slots.Add(l.FlatSlot);
            }
            st.Holders.Add(new OrderHolder(tn, info.Position.X, info.Position.Y, info.Position.Z, slots));
            Info($"t4 order setup: holder {h} {tn} {info.Position} ({generic} Generic links): gave into flat slots {string.Join(",", slots)} in that order");
            if (++picked >= 4) break;
        }
        Check(picked >= 2, $"t4 order setup: {picked} chest(s) of two or more Generic links filled");
    }

    private sealed record OrderItem(uint Id, int Flat, int Link, uint Inventory, string Item);

    private sealed record OrderRow(uint Holder, string? Template, string Pos, bool Setup, List<(uint Id, string Category)> Inventories,
        List<OrderItem> Items, bool Ascending, bool OneRun, long FromHolder, long Span, string Ids);

    /// <summary>Every holder with item entities: the measurement, logged and written to order-boot&lt;N&gt;.json.</summary>
    private void OrderTable(OrderState st, string when)
    {
        var rows = new List<OrderRow>();
        foreach (var h in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.Npcs).OrderBy(e => e.Value))
        {
            var inv = Items.Read(h);
            if (inv is not { Initialized: true }) continue;
            var items = new List<OrderItem>();
            foreach (var s in inv.Slots)
            {
                if (s.IsEmpty || s.ItemEntity.IsNone) continue;
                var loc = s.Ref.IsNone ? null : s.Ref.Location();
                items.Add(new OrderItem(s.ItemEntity.Value, loc?.FlatSlot ?? -1, s.Link, s.Slot.Inventory.Value, Items.Get(s.Item)?.Name ?? s.Item.ToString()));
            }
            if (items.Count == 0) continue;
            var info = Spawn.Inspect(h);
            var setup = info != null && st.Holders.Any(o => o.Template == info.TemplateName && Math.Abs(o.X - info.Position.X) < 0.5 &&
                                                            Math.Abs(o.Y - info.Position.Y) < 0.5 && Math.Abs(o.Z - info.Position.Z) < 0.5);
            var sorted = items.OrderBy(i => i.Flat).ToList();
            var asc = sorted.Zip(sorted.Skip(1)).All(p => p.Second.Id > p.First.Id);
            var span = (long)sorted.Max(i => i.Id) - sorted.Min(i => i.Id);
            var row = new OrderRow(h.Value, info?.TemplateName, info?.Position.ToString() ?? "?", setup,
                inv.Inventories.Select(i => (i.Inventory.Value, i.Category.ToString())).ToList(), sorted, asc, span == items.Count - 1,
                (long)sorted.Min(i => i.Id) - h.Value, span,
                string.Join(" ", sorted.Select(i => $"{i.Flat}:{i.Id}")));
            rows.Add(row);
            Info($"t4 order {when}: holder {h} {info?.TemplateName}{(setup ? " [setup]" : "")} inventories " +
                 $"[{string.Join(" ", row.Inventories.Select(i => $"{i.Id}/{i.Category}"))}] items (flat:id) [{row.Ids}] -> " +
                 $"ascending {asc}, one run {row.OneRun} (span {span} for {items.Count}), first item - holder = {row.FromHolder}");
        }
        var n = rows.Count;
        Info($"t4 order {when}: {n} holders with item entities; ids ascending in flat-slot order: {rows.Count(r => r.Ascending)}; " +
             $"one gapless run: {rows.Count(r => r.OneRun)}; run starts right after the holder (first - holder = 1): {rows.Count(r => r.FromHolder == 1)}; " +
             $"all three: {rows.Count(r => r.Ascending && r.OneRun && r.FromHolder == 1)}; setup holders {rows.Count(r => r.Setup)}");
        var json = JsonSerializer.Serialize(rows.Select(r => new
        {
            r.Holder, r.Template, r.Pos, r.Setup, inventories = r.Inventories.Select(i => new { id = i.Id, category = i.Category }),
            items = r.Items.Select(i => new { id = i.Id, flat = i.Flat, link = i.Link, inventory = i.Inventory, item = i.Item }),
            r.Ascending, r.OneRun, r.FromHolder, r.Span,
        }), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(T4Dir, $"order-boot{st.Boot}.json"), json);
    }
}
