// SPDX-License-Identifier: MIT
// items-test, kiln.items v3: item moves that keep item state, container names, inventory-transfer rules and events. Lab,
// no player: the player-initiated quick stack and a player's equipment are on the client checklist (docs/kiln/modding/inventory.md).
// Reproduces shroudlink's own server checks ("world move_stack: 4 iron bars moved, chest 7 -> 11", "world move_items: 3 of 9
// iron bars moved", "world chest_name") on the game's real inventories.
using Kiln;

namespace ItemsTest;

public sealed partial class ItemsTestMod
{
    private static string Totals(InventoryContents c) =>
        string.Join(" ", c.Slots.Where(s => !s.IsEmpty).GroupBy(s => s.Item).OrderBy(g => g.Key.Value)
            .Select(g => $"{g.Key.Value:x}x{g.Sum(s => s.Count)}"));

    private static int Count(EntityId e, ItemId item) => Items.Read(e)?.Count(item) ?? -1;

    // The components a move must not change (the game rewrites ItemState and OwnerRelationship by design).
    private static string GearState(EntityId pide)
    {
        string Hex(string c) => World.ReadBytes(pide, c) is { } b ? Convert.ToHexString(b) : "-";
        return $"Level {Hex("Level")} Perks {Hex("PerkContainerNew")} Durability {Hex("Durability")} Gem {Hex("WeaponGemSlot")}";
    }

    private async Task RunV3(EntityId a, EntityId b, ItemInfo wood, ItemInfo? sword)
    {
        var st3 = Items.Status3();
        Log.Info($"v3 status: moves {st3?.Moves}, names {st3?.Names}, transfer hooks {st3?.TransferHooks}, installed {st3?.HooksInstalled}, detail '{st3?.Detail}'");
        Check(st3 != null, "kiln.items v3 table is there");
        if (st3 == null) return;

        // ---- container names (shroudlink "world chest_name"): a chest the game's own rename path named ----
        await NameChecks();

        // ---- cheap when nothing is registered: no hook yet ----
        Check(!st3.HooksInstalled && st3.Rules == 0 && st3.Reporters == 0,
            $"nothing hooked until something registers: installed {st3.HooksInstalled}, rules {st3.Rules}, handlers {st3.Handlers}, reporters {st3.Reporters}");

        if (!st3.Moves || b.IsNone)
        {
            Check(false, $"moves need items.move and a second container (moves {st3.Moves}, second {b})");
            return;
        }
        var snapA = Snapshot(Items.Read(a)!);
        var snapB = Snapshot(Items.Read(b)!);
        var totalA = Totals(Items.Read(a)!);
        var totalB = Totals(Items.Read(b)!);
        Log.Info($"moves between {a} and {b}: A {Describe(Items.Read(a)!, 6)} | B {Describe(Items.Read(b)!, 6)}");

        // ---- move_stack: a whole stack, then part of one (shroudlink "move_stack" and "move_items") ----
        var woodA0 = Count(a, wood.Id);
        var woodB0 = Count(b, wood.Id);
        var give = await Items.Give(a, wood.Id, 9);
        var slotA = give.Changes.FirstOrDefault().Slot;
        Check(give.Status == ItemsStatus.Done && !slotA.IsNone, $"setup: 9 x Wood Logs given to {a} in {slotA}");
        var mv = await Items.Move(a, slotA, b);
        Check(mv.Status == ItemsStatus.Done && mv.Done == 9 && mv.Kind == MoveKind.Stack && mv.Changes.Count >= 1 &&
              mv.Changes.Sum(c => c.Amount) == 9 && mv.Changes.All(c => c.From == slotA),
              $"move_stack: {mv.Done} wood logs moved, {a} {woodA0 + 9} -> {Count(a, wood.Id)}, {b} {woodB0} -> {Count(b, wood.Id)} ({mv}; ledger {string.Join(", ", mv.Changes.Select(c => $"{c.From}->{c.To} x{c.Amount}"))})");
        Check(Count(a, wood.Id) == woodA0 && Count(b, wood.Id) == woodB0 + 9, "move_stack: the source lost 9 and the target gained 9, nothing else changed");
        var slotB = mv.Changes.FirstOrDefault().To;
        var part = await Items.Move(b, slotB, a, 3);
        Check(part.Status == ItemsStatus.Done && part.Done == 3 && Count(a, wood.Id) == woodA0 + 3 && Count(b, wood.Id) == woodB0 + 6,
            $"move_items: 3 of 9 wood logs moved back, {b} {woodB0 + 9} -> {Count(b, wood.Id)}, {a} -> {Count(a, wood.Id)} ({part})");
        var dryStack = await Items.Move(b, slotB, a, 0, MoveOptions.DryRun);
        Check(dryStack.DryRun && dryStack.Status == ItemsStatus.Done && dryStack.Done == 6 && Count(b, wood.Id) == woodB0 + 6 && Count(a, wood.Id) == woodA0 + 3,
            $"a dry-run move changes nothing: {dryStack}");
        var restoreB = await Items.Move(b, slotB, a);
        Check(restoreB.Status == ItemsStatus.Done && Count(a, wood.Id) == woodA0 + 9 && Count(b, wood.Id) == woodB0, $"the rest moved back: {restoreB}");
        var tookBack = await Items.Take(a, wood.Id, 9);
        Check(tookBack.Status == ItemsStatus.Done, $"cleanup: the 9 wood logs taken out again: {tookBack}");

        // ---- refusals change nothing ----
        var e1 = await Items.Move(a, SlotId.Of(Items.Read(a)!.Slots.First(s => s.IsEmpty).Slot.Inventory, Items.Read(a)!.Slots.First(s => s.IsEmpty).Slot.Index), b);
        Check(e1.Status == ItemsStatus.SlotChanged, $"an empty slot is refused: {e1}");
        var any = Items.Read(a)!.Slots.FirstOrDefault(s => !s.IsEmpty);
        if (any != null)
        {
            var e2 = await Items.Move(a, any.Slot, b, any.Count + 1);
            Check(e2.Status == ItemsStatus.NotEnough, $"more than the stack holds is refused: {e2}");
        }
        var e3 = await Items.Move(a, any?.Slot ?? default, new EntityId(0x7FFFFFF0));
        Check(e3.Status == ItemsStatus.NoInventory, $"a target without an inventory is refused: {e3}");
        var e4 = await Items.Move(a, any?.Slot ?? default, a);
        Check(e4.Status == ItemsStatus.Invalid, $"the same entity is refused before it is queued: {e4}");
        var e5 = await Items.Move(a, SlotId.Of(new EntityId(0x7FFFFFF0), 0), b);
        Check(e5.Status == ItemsStatus.SlotChanged, $"a slot of another entity is refused: {e5}");
        Check(Snapshot(Items.Read(a)!) == snapA && Snapshot(Items.Read(b)!) == snapB, "the refusals changed nothing");

        // ---- gear keeps its state: the same item entity, its components, replication and the save follow ----
        if (sword is { IsGear: true })
        {
            var level = Math.Clamp(sword.LevelMin + 1, Math.Max(1, sword.LevelMin), Math.Max(sword.LevelMin, sword.LevelMax));
            var gear = new Gear(level, ItemRarity.Epic) { Unlocked = Items.GearOptionsAvailable ? 2 : null, GemSlot = Items.GearOptionsAvailable };
            var g = await Items.Give(a, sword.Id, 1, gear);
            var gc = g.Changes.FirstOrDefault();
            await Steps(3);
            var before = Items.Read(a)!.Slots.FirstOrDefault(s => s.Slot == gc.Slot);
            Check(g.Status == ItemsStatus.Done && before is { IsEmpty: false } && !before.ItemEntity.IsNone,
                $"setup: {sword.Label} level {level} Epic in {gc.Slot}, item entity {before?.ItemEntity}, level {before?.Level}, rarity {before?.Rarity}");
            if (before is { IsEmpty: false })
            {
                var pide = before.ItemEntity;
                var stateBefore = GearState(pide);
                var moved = await Items.Move(a, before.Slot, b);
                await Steps(3);
                var inB = Items.Read(b)!.Slots.FirstOrDefault(s => s.Item == sword.Id && s.ItemEntity == pide);
                var change = moved.Changes.FirstOrDefault();
                Check(moved.Status == ItemsStatus.Done && moved.Done == 1 && change.ItemEntity == pide && inB != null && inB.Slot == change.To,
                    $"move gear: {moved}; item entity {pide} -> {inB?.ItemEntity} in {inB?.Slot} (a take and a give would have made a new entity)");
                Check(inB != null && inB.Level == before.Level && inB.Rarity == before.Rarity && Items.Read(a)!.Slots.All(s => s.ItemEntity != pide),
                    $"the gear arrived with level {inB?.Level} (was {before.Level}) and rarity {inB?.Rarity} (was {before.Rarity}); the source no longer holds it");
                var stateAfter = GearState(pide);
                Check(stateBefore == stateAfter, $"every component the move must keep is byte for byte the same: {stateAfter}");
                var itemState = World.ReadBytes(pide, "ItemState");
                var owner = World.ReadBytes(pide, "OwnerRelationship");
                var contained = itemState is { Length: >= 8 } ? BitConverter.ToUInt64(itemState, 0) : 0ul;
                var ownerId = owner is { Length: >= 4 } ? BitConverter.ToUInt32(owner, 0) : 0u;
                Check(inB != null && contained == inB.Slot.Value && ownerId == inB.Slot.Inventory.Value,
                    $"the game's own fix-ups ran at commit: ItemState.containedInSlotId {new SlotId(contained)} = {inB?.Slot}, OwnerRelationship {ownerId} = {inB?.Slot.Inventory.Value}");
                ItemDataFinding(pide, "set");
                var dry = await Items.Move(b, inB!.Slot, a, 0, MoveOptions.DryRun);
                Check(dry.Status == ItemsStatus.Done && dry.DryRun && Items.Read(b)!.Slots.Any(s => s.ItemEntity == pide) && GearState(pide) == stateBefore,
                    $"a dry-run gear move changes nothing: {dry}");
                var many = await Items.Move(b, inB.Slot, a, 2);
                Check(many.Status == ItemsStatus.NotEnough, $"two of a piece of gear is refused: {many}");
                var back = await Items.Move(b, inB.Slot, a);
                await Steps(3);
                var inA = Items.Read(a)!.Slots.FirstOrDefault(s => s.ItemEntity == pide);
                Check(back.Status == ItemsStatus.Done && inA != null && inA.Slot == before.Slot && GearState(pide) == stateBefore,
                    $"and back: the same entity {pide} in {inA?.Slot} (was {before.Slot}), state unchanged: {back}");
                ItemDataFinding(pide, "after the move there and back");
                if (inA != null) await Items.TakeSlot(a, inA);
                await Steps(3);
            }
        }
        else Log.Info("no gear item in the catalog: gear move checks skipped");

        // ---- the game's quick stack and move all, run by a mod, with the ledger ----
        var seed = await Items.Give(b, wood.Id, 1);  // B holds a stack of wood, so a quick stack of A's wood goes there
        var wA = await Items.Give(a, wood.Id, 5);
        var seedSlot = seed.Changes.FirstOrDefault().Slot;
        Check(seed.Status == ItemsStatus.Done && wA.Status == ItemsStatus.Done, $"setup: wood in both containers ({seedSlot}, {wA.Changes.FirstOrDefault().Slot})");
        var beforeA = Items.Read(a)!;
        var beforeB = Items.Read(b)!;
        var qsDry = await Items.QuickStack(a, b, MoveOptions.DryRun);
        Check(qsDry.DryRun && qsDry.Status == ItemsStatus.Done && qsDry.Done == 5 && Snapshot(Items.Read(a)!) == Snapshot(beforeA) && Snapshot(Items.Read(b)!) == Snapshot(beforeB),
            $"a dry-run quick stack says what would move and changes nothing: {qsDry}");
        var qs = await Items.QuickStack(a, b);
        await Steps(2);
        var afterA = Items.Read(a)!;
        var afterB = Items.Read(b)!;
        var lostA = beforeA.Slots.Sum(s => s.IsEmpty ? 0 : s.Count) - afterA.Slots.Sum(s => s.IsEmpty ? 0 : s.Count);
        var gainB = afterB.Slots.Sum(s => s.IsEmpty ? 0 : s.Count) - beforeB.Slots.Sum(s => s.IsEmpty ? 0 : s.Count);
        Check(qs.Status == ItemsStatus.Done && qs.Done == 5 && qs.Done == lostA && lostA == gainB && qs.Changes.Sum(c => c.Amount) == qs.Done,
            $"quick stack by the game's function: {qs}; A lost {lostA}, B gained {gainB}, ledger {string.Join(", ", qs.Changes.Select(c => $"{c.Item.Value:x} {c.From}->{c.To} x{c.Amount}"))}");
        Check(afterB.Count(wood.Id) == beforeB.Count(wood.Id) + 5 && afterA.Count(wood.Id) == beforeA.Count(wood.Id) - 5,
            $"the 5 wood logs went into B's stack, other stacks stayed: A {beforeA.Count(wood.Id)} -> {afterA.Count(wood.Id)}, B {beforeB.Count(wood.Id)} -> {afterB.Count(wood.Id)}");
        // put it back, stack by stack, to the slots it came from (the ledger says which)
        foreach (var c in qs.Changes)
        {
            var r = await Items.Move(b, c.To, a, c.Amount, toSlot: c.From);
            if (r.Status != ItemsStatus.Done) Check(false, $"restore {c.From}<-{c.To} x{c.Amount}: {r}");
        }
        await Steps(2);
        Check(Snapshot(Items.Read(a)!) == Snapshot(beforeA) && Snapshot(Items.Read(b)!) == Snapshot(beforeB),
            $"the ledger of {qs.Changes.Count} piece(s) put everything back, slot for slot");
        var maDry = await Items.MoveAll(a, b, MoveOptions.DryRun);
        Check(maDry.DryRun && maDry.Done == beforeA.Slots.Sum(s => s.IsEmpty ? 0 : s.Count) && Snapshot(Items.Read(a)!) == Snapshot(beforeA),
            $"a dry-run move all says what would move ({maDry.Done} item(s) in {maDry.Stacks} stack(s)) and changes nothing");
        var ma = await Items.MoveAll(a, b);
        await Steps(2);
        var afterMa = Items.Read(a)!;
        var afterMaB = Items.Read(b)!;
        Check(ma.Status == ItemsStatus.Done && ma.Done == maDry.Done && afterMa.UsedSlots == 0 &&
              afterMaB.Slots.Sum(s => s.IsEmpty ? 0 : s.Count) == beforeB.Slots.Sum(s => s.IsEmpty ? 0 : s.Count) + ma.Done,
              $"move all by the game's function: {ma}; A now holds {afterMa.UsedSlots} stack(s), B gained {ma.Done}");
        var moveAllLedger = ma.Changes.Count;
        foreach (var c in ma.Changes)
        {
            var r = await Items.Move(b, c.To, a, c.Amount, toSlot: c.From);
            if (r.Status != ItemsStatus.Done) Check(false, $"restore {c.From}<-{c.To} x{c.Amount}: {r}");
        }
        await Steps(2);
        Check(Snapshot(Items.Read(a)!) == Snapshot(beforeA) && Snapshot(Items.Read(b)!) == Snapshot(beforeB),
            $"the move-all ledger ({moveAllLedger} piece(s), {ma.Stacks} stacks) put everything back, slot for slot");
        var seedBack = await Items.Take(b, wood.Id, 1, seedSlot, exactSlot: true);
        var wBack = await Items.Take(a, wood.Id, 5);
        Check(seedBack.Status == ItemsStatus.Done && wBack.Status == ItemsStatus.Done, $"cleanup of the quick-stack setup: {seedBack.Status}, {wBack.Status}");
        await Steps(2);
        Check(Snapshot(Items.Read(a)!) == snapA && Snapshot(Items.Read(b)!) == snapB,
            $"both containers end as they started ({a}: {Totals(Items.Read(a)!)}; {b}: {Totals(Items.Read(b)!)})");

        // ---- transfer rules and events: nothing hooked before this point ----
        await TransferChecks(a, b, wood);

        await NativeHandlerChecks(a, b);
        Check(Snapshot(Items.Read(a)!) == snapA && Snapshot(Items.Read(b)!) == snapB,
            $"after the transfer checks both containers are as they started ({a}: {Totals(Items.Read(a)!)}; {b}: {Totals(Items.Read(b)!)})");
    }

    // A native handler (transfer_listen) inside the game's own move all: kiln.items' lab self-test runs it when asked
    // (kiln/data/kiln.items/selftest exists), on these two containers, and reports in selftest-run.done.
    private async Task NativeHandlerChecks(EntityId a, EntityId b)
    {
        var dir = Path.Combine(ServerDir, "kiln", "data", "kiln.items");
        if (!File.Exists(Path.Combine(dir, "selftest")))
        {
            Log.Info("native handler checks skipped (no kiln/data/kiln.items/selftest marker)");
            return;
        }
        var done = Path.Combine(dir, "selftest-run.done");
        File.Delete(done);
        File.WriteAllText(Path.Combine(dir, "selftest-run"), $"{a.Value} {b.Value}\n");
        for (var i = 0; i < 25 * 40 && !File.Exists(done) && !_stop; i++) await Steps(1);
        if (!File.Exists(done))
        {
            Check(false, "native handler: kiln.items' self-test did not answer in 40 s");
            return;
        }
        foreach (var line in File.ReadAllLines(done))
        {
            if (line.StartsWith("PASS ")) Check(true, "native " + line[5..]);
            else if (line.StartsWith("FAIL ")) Check(false, "native " + line[5..]);
            else if (line.StartsWith("RESULT ")) Check(line == "RESULT ok", $"native handler scenario: {line}");
        }
        await Steps(2);
    }

    // With kiln-shroudlink and a shroudlink mod installed (shroudlink_mods/chain_probe.dll, built from the vendored SDK; it
    // keeps every Wood Logs stack in the source of a quick stack / move all), both loaders hook the game's two transfer
    // functions. Either order: each runs on every call, and neither undoes the other.
    private string ServerDir => Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", ".."));

    private bool ProbePresent() => File.Exists(Path.Combine(ServerDir, "shroudlink_mods", "chain_probe.dll"));

    private async Task ChainChecks(EntityId a, EntityId b, ItemInfo wood)
    {
        var server = ServerDir;
        if (b.IsNone)
        {
            Check(false, "chain: needs two containers");
            return;
        }
        int ProbeCalls()
        {
            try
            {
                using var f = new FileStream(Path.Combine(server, "kiln", "logs", "kiln.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f);
                var text = r.ReadToEnd();
                return System.Text.RegularExpressions.Regex.Matches(text, "chain_probe: keep #").Count;
            }
            catch { return -1; }
        }
        var logText = "";
        try
        {
            using var f = new FileStream(Path.Combine(server, "kiln", "logs", "kiln.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            logText = r.ReadToEnd();
        }
        catch { /* the log is only evidence */ }
        var order = logText.Contains("hooks: installed early") ? "Kiln first (KILN_ITEMS_HOOKS=early)" : "shroudlink first, Kiln on top";
        Check(logText.Contains("shroudlink core running") || logText.Contains("chain_probe: loaded"), $"chain: kiln-shroudlink is running with the probe mod ({order})");
        var events = new List<InventoryTransferEvent>();
        using var sub = Events.On<InventoryTransferEvent>(events.Add);
        await Steps(3);
        var seed = await Items.Give(b, wood.Id, 1);
        var g = await Items.Give(a, wood.Id, 4);
        var calls0 = ProbeCalls();
        var r1 = await Items.QuickStack(a, b);
        await Steps(3);
        var calls1 = ProbeCalls();
        var ev = events.LastOrDefault();
        Check(calls1 == calls0 + 1 && r1.Done == 0 && Count(a, wood.Id) == 4 && ev is { Kind: TransferKind.QuickStack, Moved: 0, ByMod: true },
            $"chain: one quick stack ran shroudlink's keep once (probe calls {calls0} -> {calls1}) and Kiln's report once ({ev}); the wood stayed: {r1.Status}");
        events.Clear();
        using (var keep = Items.Transfers.Keep(SlotId.Of(new EntityId(1), 0)))  // a rule of ours on the same functions (no effect on wood)
        {
            var r2 = await Items.QuickStack(a, b);
            await Steps(3);
            Check(ProbeCalls() == calls1 + 1 && r2.Done == 0 && events.Count == 1 && Items.Status3()!.Rules == 1,
                $"chain: with a Kiln rule too, both still run once per call (probe {ProbeCalls()}, events {events.Count})");
        }
        // shroudlink's mod keeps the wood out of a move all as well; ours reports it
        events.Clear();
        var r3 = await Items.MoveAll(a, b);
        await Steps(3);
        var woodMoved = r3.Changes.Any(c => c.Item == wood.Id);
        Check(!woodMoved && ProbeCalls() == calls1 + 2 && events.LastOrDefault() is { Kind: TransferKind.MoveAll } mev && mev.Moved == r3.Done && r3.Done > 0,
            $"chain: move all runs both too: the other stacks moved ({r3.Done} item(s)), the wood did not, probe calls {ProbeCalls()}, event {events.LastOrDefault()}");
        foreach (var c in r3.Changes) await Items.Move(b, c.To, a, c.Amount, toSlot: c.From);
        await Steps(2);
        // no rule, no subscriber (ours removed): shroudlink's hook is still there and ours passes through
        sub.Dispose();
        await Steps(3);
        var r4 = await Items.QuickStack(a, b);
        await Steps(3);
        Check(ProbeCalls() == calls1 + 3 && r4.Done == 0, $"chain: with nothing of Kiln's registered the shroudlink hook still keeps the wood (probe {ProbeCalls()}); Kiln's hook stays installed: {Items.Status3()!.HooksInstalled}");
        var tb = await Items.Take(b, wood.Id, 1, seed.Changes.First().Slot, exactSlot: true);
        var ta = await Items.Take(a, wood.Id, 4);
        Check(tb.Status == ItemsStatus.Done && ta.Status == ItemsStatus.Done, $"chain: cleanup {tb.Status}, {ta.Status}");
    }

    private sealed class Engraving
    {
        public string By { get; set; } = "";
    }

    // The stretch finding: instance data on a piece of gear. The game keeps no free-form data on an item, but the item entity
    // is an entity, and a move keeps it. Does Kiln.Saves' EntityData take it, and does it follow the item? Logged either way.
    private void ItemDataFinding(EntityId pide, string step)
    {
        try
        {
            ItemDataFindingSaves(pide, step);
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException)
        {
            Log.Info($"item data: Kiln.Saves not usable ({e.GetType().Name}); finding skipped");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ItemDataFindingSaves(EntityId pide, string step)
    {
        if (!EntityData.Available)
        {
            Log.Info("item data: EntityData is not available (needs kiln.spawn); finding skipped");
            return;
        }
        try
        {
            if (step == "set")
            {
                EntityData.Set(pide, "items-test.engraving", new Engraving { By = "Balthazar" });
                var back = EntityData.TryGet<Engraving>(pide, "items-test.engraving", out var v) ? v.By : null;
                Check(back == "Balthazar" && !EntityData.IsPersistent(pide),
                    $"item data: EntityData.Set on item entity {pide} reads back \"{back}\"; explicitly session-only: {!EntityData.IsPersistent(pide)}");
            }
            else
            {
                var has = EntityData.TryGet<Engraving>(pide, "items-test.engraving", out var v);
                Check(has && v!.By == "Balthazar", $"item data: the engraving is still on item entity {pide} {step}: {(has ? v!.By : "gone")} (a move keeps the item entity, so data keyed to it follows)");
                EntityData.Remove(pide, "items-test.engraving");
            }
        }
        catch (Exception e)
        {
            Log.Info($"item data finding ({step}): EntityData refused item entity {pide}: {e.GetType().Name}: {e.Message}");
        }
    }

    private void ItemDataSetTag(EntityId pide, string text)
    {
        try
        {
            ItemDataSetTagSaves(pide, text);
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException)
        {
            Log.Info($"item data: Kiln.Saves not usable ({e.GetType().Name})");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ItemDataSetTagSaves(EntityId pide, string text)
    {
        if (!EntityData.Available) return;
        EntityData.Set(pide, "items-test.engraving", new Engraving { By = text });
        Log.Info($"item data: set \"{text}\" on item entity {pide} for the restart");
    }

    // A safety regression, not an item persistence feature: item entities have no world position. Two same-template
    // swords must never claim positional EntityData, including legacy records. A real chest is the positive control.
    private void ItemDataAfterRestart(EntityId first, string firstText, EntityId second, string secondText, EntityId chest)
    {
        try
        {
            ItemDataAfterRestartSaves(first, firstText, second, secondText, chest);
        }
        catch (Exception e) when (e is FileNotFoundException or TypeLoadException or NotSupportedException)
        {
            Log.Info($"item data: Kiln.Saves not usable after the restart ({e.GetType().Name})");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void ItemDataAfterRestartSaves(EntityId first, string firstText, EntityId second, string secondText, EntityId chest)
    {
        if (!EntityData.Available) { Log.Info("item data: EntityData is not available after the restart"); return; }
        string? Read(EntityId e) => !e.IsNone && EntityData.TryGet<Engraving>(e, "items-test.engraving", out var v) ? v.By : null;
        var a = Read(first);
        var b = Read(second);
        Log.Info($"FINDING item data after the restart: sword {first} (was tagged \"{firstText}\") reads \"{a}\"; sword {second} (was \"{secondText}\") reads \"{b}\"");
        Check(!second.IsNone && !EntityData.IsPersistent(first) && !EntityData.IsPersistent(second) && a == null && b == null,
            $"item data safety: both same-template swords are session-only after a restart; no positional tag crosses to another item ({firstText} -> \"{a}\", {secondText} -> \"{b}\")");
        var control = Read(chest);
        Check(EntityData.IsPersistent(chest) && control == "chest-persistence-control",
            $"item data safety: a saved world's chest still restores its own EntityData ({chest} -> \"{control}\")");
        EntityData.Remove(chest, "items-test.engraving");
    }

    private async Task NameChecks()
    {
        if (!Items.ContainerNamesAvailable)
        {
            Check(false, $"container names are not available: {Items.Status3()?.Detail}");
            return;
        }
        // The lab's native self-test (kiln/data/kiln.items/selftest) names a container through the game's own rename path,
        // as the server does for a client; wait for it, then read it back with the public API.
        EntityId named = default;
        string? text = null;
        for (var i = 0; i < 25 * 60 && !_stop && named.IsNone; i++)
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
            {
                var n = Items.ContainerName(e);
                if (n != null) { named = e; text = n; break; }
            }
            if (named.IsNone) await Steps(5);
        }
        Check(!named.IsNone && text == "Kiln Lab Ores", $"chest_name: the placed chest {named} is \"{text}\" (a name the game's rename path stored; shroudlink's \"world chest_name\")");
        if (named.IsNone) return;
        var raw = World.ReadBytes(named, "CustomPlayerString");
        var key = raw is { Length: >= 8 } ? BitConverter.ToUInt64(raw, 0) : 0ul;
        ulong h = 0xcbf29ce484222325;
        foreach (var by in System.Text.Encoding.UTF8.GetBytes(text!)) h = (h ^ by) * 0x100000001b3;
        Check(key == h, $"chest_name: the component holds {key:x16}, the FNV-1a 64 of the text ({h:x16})");
        var unnamed = Items.FindInventories(InventoryKinds.Containers).Where(e => e != named).Select(e => (e, Items.ContainerName(e))).ToList();
        Check(unnamed.All(u => u.Item2 == null) || unnamed.Any(u => u.Item2 == null),
            $"chest_name: {unnamed.Count(u => u.Item2 == null)} other containers have no name ({string.Join(", ", unnamed.Where(u => u.Item2 != null).Select(u => $"{u.e} '{u.Item2}'"))})");
        Check(Items.ContainerName(new EntityId(0x7FFFFFF0)) == null, "chest_name: an entity that does not exist has no name");
    }

    private async Task TransferChecks(EntityId a, EntityId b, ItemInfo wood)
    {
        if (!Items.Transfers.Available)
        {
            Check(false, $"the transfer hooks are not available: {Items.Status3()?.Detail}");
            return;
        }
        var events = new List<InventoryTransferEvent>();
        using var sub = Events.On<InventoryTransferEvent>(events.Add);
        await Steps(3);  // the service tick sees the subscriber and registers the report listener
        var s0 = Items.Status3()!;
        Check(s0.HooksInstalled && s0.Reporters == 1, $"a subscriber to InventoryTransferEvent installs the hooks and one report listener: installed {s0.HooksInstalled}, reporters {s0.Reporters}");

        var seed = await Items.Give(b, wood.Id, 1);
        var g = await Items.Give(a, wood.Id, 4);
        var woodSlot = g.Changes.FirstOrDefault().Slot;
        var wood0A = Count(a, wood.Id);
        var wood0B = Count(b, wood.Id);
        Check(wood0A == 4 && wood0B == 1, $"setup: 4 wood logs in {a} ({woodSlot}), 1 in {b}");

        var dryBeforeA = Snapshot(Items.Read(a)!);
        var dryBeforeB = Snapshot(Items.Read(b)!);
        var dryQuick = await Items.QuickStack(a, b, MoveOptions.DryRun);
        var dryAll = await Items.MoveAll(a, b, MoveOptions.DryRun);
        await Steps(3);
        Check(dryQuick.Done == 4 && dryAll.Done >= 4 && events.Count == 0 &&
              Snapshot(Items.Read(a)!) == dryBeforeA && Snapshot(Items.Read(b)!) == dryBeforeB,
            $"dry-run reports: quick stack would move {dryQuick.Done}, move all {dryAll.Done}; inventories unchanged and {events.Count} committed transfer events");

        // with the hooks in and no rule: the game's quick stack runs as before, and is reported
        var plain = await Items.QuickStack(a, b);
        await Steps(3);
        var plainEvent = events.LastOrDefault();
        Check(plain.Status == ItemsStatus.Done && plain.Done == 4 && Count(a, wood.Id) == 0 && Count(b, wood.Id) == 5 &&
              plainEvent is { Kind: TransferKind.QuickStack, ByMod: true, Canceled: false, Moved: 4, Kept: 0 } && plainEvent.From == a && plainEvent.To == b,
            $"report: quick stack {a} -> {b} moved {plain.Done}; event {plainEvent}");
        var back = await Items.Move(b, plain.Changes.First().To, a, 4, toSlot: woodSlot);
        Check(back.Status == ItemsStatus.Done && Count(a, wood.Id) == 4 && Count(b, wood.Id) == 1, $"put the 4 wood logs back: {back}");

        // keep a slot: its stack stays while the game's quick stack runs (shroudlink `keep`)
        events.Clear();
        using (var keep = Items.Transfers.Keep(woodSlot))
        {
            Check(Items.Status3()!.Rules == 1 && keep.IsActive, $"the rule is registered: {keep}");
            var kept = await Items.QuickStack(a, b);
            await Steps(3);
            var ev = events.LastOrDefault();
            Check(kept.Status == ItemsStatus.NoRoom && kept.Done == 0 && Count(a, wood.Id) == 4 && Count(b, wood.Id) == 1 && ev is { Kept: 1, Moved: 0, Canceled: false },
                $"keep: the locked slot {woodSlot} stayed, its 4 wood logs were hidden from the game's quick stack ({kept.Status}); event {ev}");
            var other = await Items.MoveAll(a, b, MoveOptions.DryRun);
            Check(other.Done > 0 && other.Changes.All(c => c.From != woodSlot), $"a keep rule for any kind also holds the slot out of a move all (dry run): {other.Done} item(s) would move, none of them the locked wood");
        }
        Check(Items.Status3()!.Rules == 0, "disposing the rule ends it");
        events.Clear();
        using (var keepItem = Items.Transfers.KeepItem(wood.Id, a, TransferKinds.QuickStack))
        {
            var kept = await Items.QuickStack(a, b);
            await Steps(3);
            Check(kept.Done == 0 && Count(a, wood.Id) == 4 && events.LastOrDefault() is { Kept: >= 1 }, $"keep item: stacks of Wood Logs stayed in {a} ({kept.Status}); event {events.LastOrDefault()}");
            var ma = await Items.MoveAll(a, b, MoveOptions.DryRun);
            Check(ma.Changes.Any(c => c.Item == wood.Id), $"a rule for quick stack only does not touch a move all: it would take the wood ({ma.Done} item(s))");
        }
        events.Clear();
        using (var deny = Items.Transfers.Deny(b, TransferKinds.QuickStack))
        {
            var denied = await Items.QuickStack(a, b);
            await Steps(3);
            var ev = events.LastOrDefault();
            Check(denied.Status == ItemsStatus.Refused && denied.Canceled && denied.Done == 0 && Count(a, wood.Id) == 4 && ev is { Canceled: true, Moved: 0 },
                $"cancel: a rule denying {b} stopped the game's quick stack: {denied}; event {ev}");
            var other = await Items.MoveAll(a, b, MoveOptions.DryRun);
            Check(other.DryRun && !other.Canceled && other.Done > 0, $"the rule names quick stack only: a move all is not canceled ({other.Done} item(s) would move)");
            var silent = await Items.QuickStack(a, b, MoveOptions.Silent | MoveOptions.DryRun);
            Check(silent.Status == ItemsStatus.Done && silent.Done == 4, $"a silent move sees no rules: {silent}");
        }
        // all rules ended: the quick stack works again, the hook only reports
        events.Clear();
        var again = await Items.QuickStack(a, b);
        await Steps(3);
        Check(again.Status == ItemsStatus.Done && again.Done == 4 && events.Count == 1, $"after the rules ended a quick stack runs as the game's: {again}; {events.Count} event(s)");
        var st = Items.Status3()!;
        Check(st.TransfersSeen >= 8 && st.TransfersCanceled == 1 && st.Rules == 0, $"the hook counted the game's calls: {st.TransfersSeen} seen, {st.TransfersCanceled} canceled, {st.Rules} rules");
        foreach (var c in again.Changes)
            await Items.Move(b, c.To, a, c.Amount, toSlot: c.From);
        await Steps(2);
        var tb = await Items.Take(b, wood.Id, 1, seed.Changes.First().Slot, exactSlot: true);
        var ta = await Items.Take(a, wood.Id, 4);
        await Steps(2);
        Check(tb.Status == ItemsStatus.Done && ta.Status == ItemsStatus.Done, $"cleanup of the transfer setup: {tb.Status}, {ta.Status}");
    }
}
