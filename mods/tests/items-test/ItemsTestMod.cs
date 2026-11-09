// SPDX-License-Identifier: MIT
using System.Text.Json;
using Kiln;

namespace ItemsTest;

/// <summary>
/// kiln.items lab test (no player connected): finds a container in the copied world, reads it, gives a stack, reads
/// back, takes it back out slot by slot, reads again (net zero); gives one piece of gear with a level and rarity and
/// removes it again; checks the refusals (unknown item, not enough, no inventory, dry run, wrong slot). Every check
/// logs one PASS or FAIL line; the container ends exactly as it started.
/// </summary>
public sealed partial class ItemsTestMod : Mod
{
    private int _pass, _fail;
    private bool _stop;

    public override void OnLoad() => _ = RunAsync();

    public override void OnUnload() => _stop = true;

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunAsync()
    {
        try
        {
            await Run();
        }
        catch (Exception e)
        {
            Check(false, $"test aborted: {e}");
        }
        Log.Info($"items-test done: {_pass} PASS, {_fail} FAIL");
    }

    private static string Snapshot(InventoryContents c) =>
        string.Join(" ", c.Slots.Select(s => s.IsEmpty ? "-" : $"{s.Item.Value:x}x{s.Count}{(s.ItemEntity.IsNone ? "" : "#" + s.ItemEntity)}"));

    private static string Describe(InventoryContents c, int max = 12) =>
        string.Join(", ", c.Slots.Where(s => !s.IsEmpty).Take(max).Select(s =>
            $"{s.Slot}={Items.Get(s.Item)?.Label ?? s.Item.ToString()} x{s.Count}" +
            (s.ItemEntity.IsNone ? "" : $" (gear #{s.ItemEntity} lvl {s.Level} {s.Rarity})")));

    private async Task Run()
    {
        // ---- 1. the service comes up: program verified, catalog loaded ----
        for (var i = 0; i < 25 * 120 && !_stop; i++)
        {
            if (Items.Available && Items.Status() is { Transactions: true, Catalog: true }) break;
            await Steps(1);
        }
        var st = Items.Status();
        Check(st is { Transactions: true, Catalog: true },
            $"service ready: {(st == null ? "kiln.items not loaded" : $"program state {st.Program}, {st.Items} items, updates {st.ProgramUpdates}, detail '{st.Detail}'")}");
        if (st is not { Transactions: true, Catalog: true }) return;

        // ---- 2. catalog ----
        var all = Items.All;
        var wood = Items.Lookup("Material_T1_Wood_Logs");
        Check(wood is { Id.Value: 0x2b0cf1d5, MaxStack: 250, IsGear: false },
            $"catalog: Material_T1_Wood_Logs = {wood?.Id} stack {wood?.MaxStack} category {wood?.Category} ('{wood?.DisplayName}')");
        Check(Items.Lookup("Wood Logs")?.Id == wood?.Id && Items.Lookup("0x2b0cf1d5")?.Id == wood?.Id &&
              Items.Lookup("material_t1_wood_logs")?.Id == wood?.Id,
            "catalog: Lookup by display name, hex id and case-insensitive debug name");
        var guidOk = all.Count(i => ItemId.FromGuidBytes(i.Guid.ToByteArray()) == i.Id);
        Check(guidOk == all.Count, $"catalog: item id = FNV-1a(GUID bytes) for {guidOk}/{all.Count} items");
        CompareWithDashboard(all);
        if (wood == null) return;
        // A request in flight from the first step on: under `reload-test items-test N` the mod unloads while it waits
        // (Kiln.Items drops its callback on Retiring). A dry run, so it never changes anything.
        var anyInventory = Items.FindInventories(InventoryKinds.Containers).FirstOrDefault();
        if (!anyInventory.IsNone) _ = Items.Give(anyInventory, wood.Id, 1, options: GiveOptions.DryRun);
        var persist = Path.Combine(Context.DataDirectory, "persist.json");
        if (File.Exists(persist))
        {
            await VerifyPersisted(persist);
            return;
        }

        // ---- 3. a container from the copied world ----
        var found = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty);
        var players = Items.FindInventories(InventoryKinds.Players);
        var npcs = Items.FindInventories(InventoryKinds.Npcs);
        var every = Items.FindInventories(InventoryKinds.All);
        Log.Info($"inventories: {every.Count} with an initialized InventorySetup; {found.Count} non-empty containers, " +
                 $"{players.Count} players, {npcs.Count} npcs");
        Check(found.Count > 0, $"find: {found.Count} non-empty containers in the world");
        var candidates = new List<(EntityId E, string Snap)>();
        foreach (var e in found)
        {
            var c = Items.Read(e);
            if (c is { Initialized: true } && c.FreeSlots() >= 3)
                candidates.Add((e, Snapshot(c)));
        }
        await Steps(50);  // skip containers something else is using right now (villager crafting stock, stations)
        EntityId chest = default;
        foreach (var (e, snap) in candidates)
        {
            if (Items.Read(e) is { } c && Snapshot(c) == snap) { chest = e; break; }
        }
        Check(!chest.IsNone, $"chose container {chest} ({candidates.Count} candidates with >= 3 free slots)");
        if (chest.IsNone) return;
        var second = candidates.Where(c => c.E != chest).Select(c => c.E).FirstOrDefault();
        if (ProbePresent())
        {
            // shroudlink's probe mod keeps wood out of every quick stack, which would change the checks below: this run is
            // the hook-chain check only (the rest runs without shroudlink mods)
            await ChainChecks(chest, second, wood);
            return;
        }
        var before = Items.Read(chest)!;
        var original = Snapshot(before);
        Log.Info($"container {chest}: {before.Inventories.Count} inventories, {before.Slots.Count} slots, {before.UsedSlots} used, " +
                 $"generic {before.GenericSlots}, flags 0x{before.SetupFlags:x}: {Describe(before)}");

        // ---- 4. give a stack, read back, take it back out, read again ----
        var woodBefore = before.Count(wood.Id);
        var give = await Items.Give(chest, wood.Id, 7);
        Check(give.Status == ItemsStatus.Done && give.Done == 7 && give.Changes.Sum(c => c.Amount) == 7,
            $"give 7 x Wood Logs: {give} slots [{string.Join(", ", give.Changes.Select(c => $"{c.Slot}+{c.Amount}"))}] after {give.Steps} step(s)");
        var mid = Items.Read(chest)!;
        Check(mid.Count(wood.Id) == woodBefore + 7, $"read back: wood logs {woodBefore} -> {mid.Count(wood.Id)}");
        foreach (var c in give.Changes)
        {
            var take = await Items.Take(chest, wood.Id, c.Amount, c.Slot, exactSlot: true);
            Check(take.Status == ItemsStatus.Done && take.Done == c.Amount, $"take {c.Amount} back from {c.Slot}: {take}");
        }
        var after = Items.Read(chest)!;
        Check(Snapshot(after) == original, $"net zero after the stack round trip (wood logs {after.Count(wood.Id)})");

        // ---- 4b. a burst: 20 requests submitted in one step (the program runs 16 per step) ----
        var burst = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Items.Give(chest, wood.Id, 1)));
        Check(burst.All(b => b.Status == ItemsStatus.Done && b.Done == 1),
            $"burst of 20 gives: {burst.Count(b => b.Status == ItemsStatus.Done)} done, steps {string.Join(",", burst.Select(b => b.Steps).Distinct())}");
        foreach (var g in burst.SelectMany(b => b.Changes).GroupBy(c => c.Slot))
        {
            var t = await Items.Take(chest, wood.Id, g.Sum(c => c.Amount), g.Key, exactSlot: true);
            Check(t.Status == ItemsStatus.Done, $"take the burst back from {g.Key}: {t}");
        }
        Check(Snapshot(Items.Read(chest)!) == original, "net zero after the burst");

        // ---- 5. gear: one piece with a level and rarity, then removed ----
        var sword = Items.Lookup("Weapon_T1_1H_SwordEpic_01") ??
                    all.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMax >= i.LevelMin && i.LevelMin > 0);
        Check(sword is { IsGear: true }, $"gear item: {sword} levels {sword?.LevelMin}-{sword?.LevelMax}");
        if (sword is { IsGear: true })
        {
            var level = Math.Clamp(sword.LevelMin + 1, Math.Max(1, sword.LevelMin), Math.Max(sword.LevelMin, sword.LevelMax));
            var g = await Items.Give(chest, sword.Id, 1, new Gear(level, ItemRarity.Rare));
            var ch = g.Changes.FirstOrDefault();
            Check(g.Status == ItemsStatus.Done && g.Done == 1 && g.Changes.Count == 1 && !ch.ItemEntity.IsNone,
                $"give gear {sword.Label} level {level} Rare: {g} -> {ch.Slot} item entity {ch.ItemEntity}");
            await Steps(3);  // the item entity is spawned at the end of the step; item_state_init fills ItemState
            var withGear = Items.Read(chest)!;
            var slot = withGear.Slots.FirstOrDefault(s => s.Slot == ch.Slot);
            Check(slot != null && slot.Item == sword.Id && slot.ItemEntity == ch.ItemEntity && slot.Count == 1,
                $"read back gear: {slot?.Slot} item {slot?.Item} count {slot?.Count} entity {slot?.ItemEntity} state 0x{slot?.ItemState:x}");
            Check(slot != null && slot.Level == level && slot.Rarity == ItemRarity.Rare,
                $"gear level / rarity: level {slot?.Level} (asked {level}), rarity {slot?.Rarity} (asked Rare)");
            CrossCheckGear(ch.ItemEntity, ch.Slot, level);
            if (slot != null && !slot.IsEmpty)
            {
                var dry = await Items.TakeSlot(chest, slot, dryRun: true);
                Check(dry.Status == ItemsStatus.Done && dry.DryRun && Snapshot(Items.Read(chest)!) == Snapshot(withGear),
                    $"dry-run take of the gear changes nothing: {dry}");
                var t = await Items.TakeSlot(chest, slot);
                Check(t.Status == ItemsStatus.Done && t.Changes.Count == 1 && t.Changes[0].ItemEntity == ch.ItemEntity,
                    $"take the gear out of {slot.Slot}: {t} (item entity {t.Changes.FirstOrDefault().ItemEntity} destroyed)");
            }
            await Steps(3);
            var end = Items.Read(chest)!;
            Check(Snapshot(end) == original, "net zero after the gear round trip");
            CrossCheckGone(ch.ItemEntity);
        }

        // ---- 6. refusals change nothing ----
        var r1 = await Items.Give(chest, new ItemId(0x12345678), 1);
        Check(r1.Status == ItemsStatus.UnknownItem, $"unknown item refused: {r1}");
        var have = Items.Read(chest)!.Count(wood.Id);
        var r2 = await Items.Take(chest, wood.Id, have + 1000);
        Check(r2.Status == ItemsStatus.NotEnough && Snapshot(Items.Read(chest)!) == original, $"take more than there is: {r2}");
        var r3 = await Items.Give(new EntityId(0x7FFFFFF0), wood.Id, 1);
        Check(r3.Status == ItemsStatus.NoInventory, $"entity without inventory: {r3}");
        var r4 = await Items.Give(chest, wood.Id, 5, options: GiveOptions.DryRun);
        Check(r4.Status == ItemsStatus.Done && r4.DryRun && r4.Done == 5 && Snapshot(Items.Read(chest)!) == original,
            $"dry-run give changes nothing: {r4}");
        var r5 = await Items.Give(chest, wood.Id, 100000, options: GiveOptions.AllOrNothing | GiveOptions.DryRun);
        Check(r5.Status == ItemsStatus.NoRoom && Snapshot(Items.Read(chest)!) == original,
            $"all-or-nothing give that cannot fit: {r5}");
        var empty = Items.Read(chest)!.Slots.First(s => s.IsEmpty);
        var r6 = await Items.Take(chest, wood.Id, 1, empty.Slot, exactSlot: true);
        Check(r6.Status == ItemsStatus.SlotChanged, $"exact-slot take from an empty slot: {r6}");
        var r7 = await Items.Take(chest, wood.Id, 1, SlotId.Of(new EntityId(0x7FFFFFF0), 0), exactSlot: true);
        Check(r7.Status == ItemsStatus.SlotChanged, $"slot of another entity: {r7}");
        var final = Items.Read(chest)!;
        Check(Snapshot(final) == original, $"container {chest} ends as it started: {Describe(final)}");

        // ---- 6b. kiln.items v3: moves that keep item state, container names, transfer rules and events ----
        await RunV3(chest, second, wood, sword);
        final = Items.Read(chest)!;
        Check(Snapshot(final) == original, $"container {chest} still ends as it started after the v3 checks: {Describe(final)}");

        // ---- 7. opt-in: leave items in the container for a save + restart (kiln/data/items-test/persist.request) ----
        if (File.Exists(Path.Combine(Context.DataDirectory, "persist.request")) && sword is { IsGear: true })
        {
            var a = await Items.Give(chest, wood.Id, 3);
            var b = await Items.Give(chest, sword.Id, 1, new Gear(3, ItemRarity.Epic));
            await Steps(3);
            var gearSlot = b.Changes.FirstOrDefault();
            // the gear is then moved to a second container: the save has to carry the moved item entity
            var other = second;
            var otherTotals = other.IsNone ? "" : Totals(Items.Read(other)!);
            var movedToB = other.IsNone ? null : await Items.Move(chest, gearSlot.Slot, other, 0);
            await Steps(3);
            // item data left on two identical swords (same template, same container) for the restart: does it survive, and
            // does each keep its own?
            ItemDataSetTag(gearSlot.ItemEntity, "level3");
            ItemDataSetTag(chest, "chest-persistence-control");
            var g2 = await Items.Give(chest, sword.Id, 1, new Gear(5, ItemRarity.Epic));
            await Steps(3);
            var g2Slot = g2.Changes.FirstOrDefault();
            if (g2Slot.ItemEntity.Value != 0)
            {
                ItemDataSetTag(g2Slot.ItemEntity, "level5");
                if (!other.IsNone) await Items.Move(chest, g2Slot.Slot, other, 0);
                await Steps(3);
            }
            var named = Items.ContainerNamesAvailable ? Items.FindInventories(InventoryKinds.Containers).Select(e => (e, n: Items.ContainerName(e))).FirstOrDefault(x => x.n != null) : default;
            var rec = new PersistRecord(chest.Value, original, wood.Id.Value, final.Count(wood.Id), 3, sword.Id.Value,
                final.Count(sword.Id), gearSlot.ItemEntity.Value, 3, (int)ItemRarity.Epic,
                movedToB is { Status: ItemsStatus.Done } ? other.Value : 0, otherTotals, named.n == null ? 0 : named.e.Value, named.n ?? "");
            File.WriteAllText(persist, JsonSerializer.Serialize(rec, new JsonSerializerOptions()));
            File.Delete(Path.Combine(Context.DataDirectory, "persist.request"));
            Check(a.Status == ItemsStatus.Done && b.Status == ItemsStatus.Done,
                $"persistence phase 1: left 3 x Wood Logs and a level 3 Epic {sword.Label} (entity {gearSlot.ItemEntity}) in {chest}" +
                (rec.ChestB != 0 ? $", the sword moved on to {other} ({movedToB})" : "") + (rec.Named != 0 ? $"; chest {rec.Named} is named \"{rec.NamedText}\"" : "") + "; restart the server to check them");
        }
        var s2 = Items.Status()!;
        Log.Info($"kiln.items: {s2.Committed} committed, {s2.RolledBack} rolled back, {s2.ProgramUpdates} program updates");
    }

    // Serialized with a fresh JsonSerializerOptions each time: the shared default options cache type metadata and would
    // keep this collectible mod context alive after an unload.
    private sealed record PersistRecord(uint Chest, string Original, uint Wood, int WoodBefore, int WoodCount, uint Gear,
        int GearBefore, uint GearEntity, int GearLevel, int GearRarity, uint ChestB = 0, string BTotals = "", uint Named = 0,
        string NamedText = "");

    // Phase 2 after a save and restart: the items are still there (stack count, gear with its level and rarity), then
    // they are taken out again and the container is as it was before phase 1. Matched by item, not by slot: entities
    // created at run time (the gear's item entity, a chest's linked inventories) get new ids when the save loads.
    private async Task VerifyPersisted(string path)
    {
        var rec = JsonSerializer.Deserialize<PersistRecord>(File.ReadAllText(path), new JsonSerializerOptions())!;
        var chest = new EntityId(rec.Chest);
        await Steps(25);
        var c = Items.Read(chest);
        Check(c != null, $"persistence: container {chest} is back after the restart");
        if (c == null) return;
        var wood = new ItemId(rec.Wood);
        var gearId = new ItemId(rec.Gear);
        Check(c.Count(wood) == rec.WoodBefore + rec.WoodCount,
            $"persistence: the {rec.WoodCount} wood logs survived the save ({string.Join(", ", c.Slots.Where(s => s.Item == wood).Select(s => $"{s.Slot} x{s.Count}"))})");
        // the gear was moved to a second container before the save: the move is in the save
        var holder = rec.ChestB != 0 ? Items.Read(new EntityId(rec.ChestB)) : c;
        var holderId = rec.ChestB != 0 ? new EntityId(rec.ChestB) : chest;
        var gear = holder?.Slots.FirstOrDefault(s => s.Item == gearId && s.Level == rec.GearLevel && s.Rarity == (ItemRarity)rec.GearRarity);
        Check(gear != null && !gear.ItemEntity.IsNone && (rec.ChestB != 0 ? holder!.Count(gearId) >= 1 && c.Count(gearId) == rec.GearBefore : c.Count(gearId) == rec.GearBefore + 1),
            $"persistence: the gear survived{(rec.ChestB != 0 ? $" its move to {holderId}" : "")}: {gear?.Slot} item {gear?.Item} entity {gear?.ItemEntity} (was {rec.GearEntity} before the restart) level {gear?.Level} rarity {gear?.Rarity}");
        var gear2 = holder?.Slots.FirstOrDefault(s => s.Item == gearId && s.Level == 5 && s.Rarity == ItemRarity.Epic);
        if (gear != null) ItemDataAfterRestart(gear.ItemEntity, "level3", gear2?.ItemEntity ?? default, "level5", chest);
        if (rec.Named != 0)
        {
            var name = Items.ContainerNamesAvailable ? Items.ContainerName(new EntityId(rec.Named)) : null;
            Check(name == rec.NamedText, $"persistence: the chest named \"{rec.NamedText}\" before the restart is read back from the save: entity {rec.Named} is \"{name}\"");
        }
        var left = rec.WoodCount;
        foreach (var s in c.Slots.Where(s => s.Item == wood).Reverse())
        {
            if (left <= 0) break;
            var n = Math.Min(left, s.Count);
            var take = await Items.Take(chest, wood, n, s.Slot, exactSlot: true);
            Check(take.Status == ItemsStatus.Done, $"persistence: {n} wood taken back from {s.Slot}: {take}");
            left -= n;
        }
        if (gear is { IsEmpty: false })
        {
            var t = await Items.TakeSlot(holderId, gear);
            Check(t.Status == ItemsStatus.Done, $"persistence: gear taken back: {t}");
        }
        if (gear2 is { IsEmpty: false })
        {
            var t = await Items.TakeSlot(holderId, gear2);
            Check(t.Status == ItemsStatus.Done, $"persistence: second gear taken back: {t}");
        }
        await Steps(3);
        if (rec.ChestB != 0)
            Check(Totals(Items.Read(new EntityId(rec.ChestB))!) == rec.BTotals, $"persistence: container {holderId} is as before phase 1: {Totals(Items.Read(new EntityId(rec.ChestB))!)}");
        var end = Items.Read(chest)!;
        Check(end.Count(wood) == rec.WoodBefore && end.Count(gearId) == rec.GearBefore &&
              (rec.Original.Length == 0 || Snapshot(end) == rec.Original),
            $"persistence: container {chest} is as before phase 1: {Describe(end)}");
        File.Delete(path);
    }

    // Independent look at the gear's item entity through kiln.world (raw components; layouts in docs/re/items.md).
    private void CrossCheckGear(EntityId pide, SlotId slot, int level)
    {
        try
        {
            CrossCheckGearWorld(pide, slot, level);
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException)
        {
            Log.Info($"kiln.world not usable ({e.GetType().Name}); gear cross-check skipped");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CrossCheckGearWorld(EntityId pide, SlotId slot, int level)
    {
        if (!World.Available) { Log.Info("kiln.world unavailable; gear cross-check skipped"); return; }
        var lv = World.ReadBytes(pide, "Level");
        var perks = World.ReadBytes(pide, "PerkContainerNew");
        var owner = World.ReadBytes(pide, "OwnerRelationship");
        var state = World.ReadBytes(pide, "ItemState");
        var levelValue = lv is { Length: >= 12 } ? BitConverter.ToUInt32(lv, BitConverter.ToUInt16(lv, 4)) : 0u;
        var rarity = perks is { Length: >= 1 } ? perks[0] - 1 : -1;
        var ownerId = owner is { Length: >= 4 } ? BitConverter.ToUInt32(owner, 0) : 0u;
        var contained = state is { Length: >= 8 } ? BitConverter.ToUInt64(state, 0) : 0ul;
        Check(levelValue == (uint)level && rarity == (int)ItemRarity.Rare && ownerId == slot.Inventory.Value && contained == slot.Value,
            $"gear item entity {pide} (kiln.world): Level {levelValue}, PerkContainerNew.availablePerks-1 = {rarity}, " +
            $"OwnerRelationship {ownerId}, ItemState.containedInSlotId {new SlotId(contained)}, components " +
            $"Level {lv?.Length} B, PerkContainerNew {perks?.Length} B, Durability {World.ComponentSize(pide, "Durability")} B");
    }

    private void CrossCheckGone(EntityId pide)
    {
        try
        {
            CrossCheckGoneWorld(pide);
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException)
        {
            Log.Info($"kiln.world not usable ({e.GetType().Name}); pide check skipped");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void CrossCheckGoneWorld(EntityId pide)
    {
        if (!World.Available) return;
        Check(!World.Has(pide, "ItemState") && !World.Has(pide, "OwnerRelationship"),
            $"the taken gear's item entity {pide} is gone (no ItemState / OwnerRelationship)");
    }

    // The game's registry against a JSON item list ($KILN_EXPECTED_ITEMS at build time, copied next to the mod).
    private void CompareWithDashboard(IReadOnlyList<ItemInfo> all)
    {
        var path = Path.Combine(Context.Info.Directory, "expected-items.json");
        if (!File.Exists(path))
        {
            Log.Info("catalog: expected-items.json not shipped; dashboard comparison skipped");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        int known = 0, sameName = 0, sameStack = 0, sameCat = 0, missing = 0;
        var categories = new Dictionary<string, Dictionary<int, int>>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            var id = new ItemId(e.GetProperty("id").GetUInt32());
            var it = Items.Get(id);
            if (it == null) { missing++; continue; }
            known++;
            if (it.Name == e.GetProperty("debug").GetString()) sameName++;
            if (it.MaxStack == e.GetProperty("maxStack").GetInt32()) sameStack++;
            var cat = e.GetProperty("category").GetString() ?? "";
            if (!categories.TryGetValue(cat, out var m)) categories[cat] = m = new Dictionary<int, int>();
            m[(int)it.Category] = m.GetValueOrDefault((int)it.Category) + 1;
            if (Enum.GetName(it.Category) == cat) sameCat++;
        }
        Check(known > 0 && sameName == known && sameStack == known,
            $"catalog vs dashboard: {known} known ({missing} not in this build), debug name equal {sameName}, max stack equal {sameStack}, category name equal {sameCat}");
        Log.Info("catalog categories (dashboard name -> game value x count): " + string.Join("; ",
            categories.OrderBy(k => k.Key).Select(k => $"{k.Key} -> " + string.Join("/", k.Value.Select(v => $"{v.Key}x{v.Value}")))));
    }
}
