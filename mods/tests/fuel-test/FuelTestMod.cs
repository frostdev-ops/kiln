// SPDX-License-Identifier: MIT
// Items.Fuel on a dedicated server with no player. Fresh boot: read, set, add, fill, dry run, every container of a chest,
// keep-full against a drain; containers left in a chest. Boot 2: a tracked write on two of them, a raw one on the third.
// Boot 3: the tracked writes are still there, the raw one is not. Stop and start the lab after each "READY restart".
using System.Text.Json.Nodes;
using Kiln;

namespace FuelTest;

public sealed class FuelTestMod : Mod
{
    private const string Me = "fuel-test";
    private const string Can = "Tool_T1_WateringCan";        // 100 charges = 6400 units
    private const string Pouch = "Tool_Water_Pouch_Refillable";  // 20 charges = 1280 units
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"{Me}: PASS {what}"); }
        else { _fail++; Log.Error($"{Me}: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private string StateFile => Path.Combine(Context.DataDirectory, "restart.json");

    public override void OnLoad() => _ = Run().ContinueWith(t =>
    {
        if (t.Exception != null) Check(false, $"the test threw: {t.Exception.InnerException}");
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    });

    private async Task Run()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Items.All.Count > 0); i++) await Steps(25);
        await Steps(25);
        if (!Check(Items.Fuel.Available, $"Items.Fuel is available: {Items.Status()?.Detail}")) return;
        if (File.Exists(StateFile)) await AfterRestart();
        else await Fresh();
    }

    // raw bytes of the Fuel component: {u32 group; u16 offset; u8 flags; u8 count; s32 current; s32 max}
    private static (int Cur, int Max) Raw(EntityId e)
    {
        var f = World.ReadBytes(e, "Fuel");
        return f is { Length: >= 16 } ? (BitConverter.ToInt32(f, 8), BitConverter.ToInt32(f, 12)) : (-1, -1);
    }

    private static int Net(EntityId e) => World.ReadBytes(e, "NetworkFuel") is { Length: >= 4 } n ? BitConverter.ToUInt16(n, 0) : -1;

    private async Task<InventorySlot?> Give(EntityId chest, string item)
    {
        var id = Items.Lookup(item)!.Id;
        var r = await Items.Give(chest, id, 1, options: GiveOptions.NoMerge);
        await Steps(2);
        return r.Changes.Count > 0 ? Items.Read(chest)?.Slots.FirstOrDefault(s => s.Slot == r.Changes[0].Slot) : null;
    }

    private async Task Fresh()
    {
        var chest = await Chest();
        if (chest.IsNone) return;
        var canSlot = await Give(chest, Can);
        var pouchSlot = await Give(chest, Pouch);
        if (!Check(canSlot is { ItemEntity.IsNone: false } && pouchSlot is { ItemEntity.IsNone: false },
                $"a watering can and a water pouch in the chest: {canSlot?.ItemEntity} {pouchSlot?.ItemEntity}")) return;
        EntityId can = canSlot!.ItemEntity, pouch = pouchSlot!.ItemEntity;
        var canId = Items.Lookup(Can)!.Id;

        // read
        var lv = Items.Fuel.Read(can);
        var raw = Raw(can);
        Check(lv is { Max: 6400 } && lv.Value.Units == raw.Cur, $"Read(can) = {lv} matches the component ({raw.Cur}/{raw.Max})");
        Check(Items.Fuel.Read(pouch) is { Max: 1280 }, $"Read(pouch) = {Items.Fuel.Read(pouch)}");
        Check(Items.Fuel.Read(chest) == null, "Read(chest) = null (a chest holds no water)");

        // set, add, fill, clamps
        var r = await Items.Fuel.Set(can, 1234);
        Check(r.Ok && r.Changed == 1 && r.Changes[0].After == 1234 && Items.Fuel.Read(can)?.Units == 1234 && Raw(can).Cur == 1234,
            $"Set(can, 1234): {r}, now {Items.Fuel.Read(can)}");
        await Steps(3);
        Check(Net(can) == 1234, $"players are sent it (NetworkFuel {Net(can)})");
        r = await Items.Fuel.Add(can, -234);
        Check(r.Ok && Items.Fuel.Read(can)?.Units == 1000, $"Add(can, -234): {Items.Fuel.Read(can)}");
        r = await Items.Fuel.Add(can, 1_000_000);
        Check(r.Ok && Items.Fuel.Read(can)?.Units == 6400, $"Add(can, 1000000) stops at max: {Items.Fuel.Read(can)}");
        r = await Items.Fuel.Set(can, -5);
        Check(r.Ok && Items.Fuel.Read(can)?.Units == 0, $"Set(can, -5) stops at 0: {Items.Fuel.Read(can)}");
        r = await Items.Fuel.Fill(can);
        Check(r.Ok && r.Changes.Count == 1 && r.Changes[0] is { Before: 0, After: 6400, Max: 6400 } && Items.Fuel.Read(can)!.Value.IsFull,
            $"Fill(can): {r} {(r.Changes.Count > 0 ? r.Changes[0] : default)}");
        r = await Items.Fuel.Fill(can);
        Check(r.Ok && r.Matched == 1 && r.Changed == 0, $"Fill(can) again changes nothing: {r}");
        r = await Items.Fuel.Set(can, 1, dryRun: true);
        Check(r.Ok && r.DryRun && r.Changes.Count == 1 && r.Changes[0].After == 1 && Items.Fuel.Read(can)?.Units == 6400,
            $"a dry run says what would change and changes nothing: {r}, still {Items.Fuel.Read(can)}");

        // holders
        r = await Items.Fuel.SetAll(chest, 0);
        Check(r.Ok && r.Matched >= 2 && Items.Fuel.Read(can)?.Units == 0 && Items.Fuel.Read(pouch)?.Units == 0, $"SetAll(chest, 0): {r}");
        r = await Items.Fuel.FillAll(chest, canId);
        Check(r.Ok && Items.Fuel.Read(can)?.Units == 6400 && Items.Fuel.Read(pouch)?.Units == 0,
            $"FillAll(chest, can) fills only cans: {r}; can {Items.Fuel.Read(can)}, pouch {Items.Fuel.Read(pouch)}");
        Check(r.Changes.Count > 0 && r.Changes.All(c => c.Item == canId && !c.Slot.IsNone),
            $"the changes name the can and its slot: {string.Join(", ", r.Changes)}");
        r = await Items.Fuel.FillAll(chest);
        Check(r.Ok && Items.Fuel.Read(pouch)?.Units == 1280, $"FillAll(chest) fills the pouch too: {r}");
        r = await Items.Fuel.Fill(chest);
        Check(r.Status == ItemsStatus.SlotChanged, $"Fill(chest) itself: {r}");
        r = await Items.Fuel.FillAll(can);
        Check(r.Status == ItemsStatus.NoInventory, $"FillAll(can) (no inventory): {r}");

        // keep-full against a drain (a raw write each step, as a pour would take water)
        await Items.Fuel.SetAll(chest, 0);
        using (var keep = Items.Fuel.KeepFull(chest, canId))
        {
            await Steps(2);
            Check(Items.Fuel.Read(can)?.IsFull == true && Items.Fuel.Read(pouch)?.Units == 0,
                $"KeepFull(chest, can) filled the can, not the pouch: can {Items.Fuel.Read(can)}, pouch {Items.Fuel.Read(pouch)}");
            int seenFull = 0, drained = 0;
            for (var i = 0; i < 20; i++)
            {
                World.TryWrite(can, "Fuel", 8, 6400 - 32 * (i % 3 + 1));
                drained++;
                await Steps(1);
                if (Items.Fuel.Read(can)?.IsFull == true) seenFull++;
            }
            Check(seenFull == drained, $"drained {drained} times, full again at the next step {seenFull} times");
            await Steps(3);
            Check(Net(can) == 6400, $"players are sent a full can: NetworkFuel {Net(can)}");
        }
        World.TryWrite(can, "Fuel", 8, 100);
        await Steps(3);
        Check(Items.Fuel.Read(can)?.Units == 100, $"after the keep is disposed a drain stays: {Items.Fuel.Read(can)}");

        // a rate up to a level, and the refill event
        var events = new List<WaterRefilledEvent>();
        using (Events.On<WaterRefilledEvent>(e => events.Add(e)))
        {
            await Items.Fuel.SetAll(chest, 0);
            var reported = 0L;
            using (var rate = Items.Fuel.Refill(chest, FuelRefill.ChargesPerSecond(10, limitUnits: 1000), canId))
            {
                rate.Refilled += ch => reported += ch.Sum(c => c.After - c.Before);
                await Steps(25);   // about one second of game time
                var mid = Items.Fuel.Read(can)!.Value.Units;
                Check(mid is > 200 and < 1000, $"10 charges a second for ~1 s: {mid} units (640 expected, give or take the step rhythm)");
                await Steps(50);
                Check(Items.Fuel.Read(can)?.Units == 1000 && Items.Fuel.Read(pouch)?.Units == 0,
                    $"it stops at its limit (1000) and fills only cans: can {Items.Fuel.Read(can)}, pouch {Items.Fuel.Read(pouch)}");
                await Steps(1);
                Check(rate.UnitsAdded == 1000 && reported == 1000, $"the policy counted what it gave: {rate.UnitsAdded} units, Refilled reported {reported}");
            }
            using (var cap = Items.Fuel.Refill(chest, FuelRefill.Full with { Limit = 3200 }))
            {
                await Steps(3);
                Check(Items.Fuel.Read(can)?.Units == 3200 && Items.Fuel.Read(pouch)?.Units == 1280,
                    $"keep full up to 3200: can {Items.Fuel.Read(can)} (capped), pouch {Items.Fuel.Read(pouch)} (its max is lower)");
            }
            await Steps(1);
        }
        Check(events.Count > 0 && events.Sum(e => e.Units) >= 1000 + 2200 + 1280,
            $"WaterRefilledEvent: {events.Count} event(s), {events.Sum(e => e.Units)} units");
        r = await Items.Fuel.Set(can, 100);

        // across restarts. A new entity is saved whole at the next save, raw writes included, so the writes under test
        // hit containers a save has already written: boot 1 leaves can 6400, pouch 0, control 1000; boot 2 writes can 4321
        // and pouch 777 (tracked) and control 3000 (raw); boot 3 reads them back.
        await Items.Fuel.Fill(can);
        await Items.Fuel.Set(pouch, 0);
        var control = await Give(chest, Can);
        if (!Check(control is { ItemEntity.IsNone: false }, $"a second can (control) {control?.ItemEntity}")) return;
        await Items.Fuel.Set(control!.ItemEntity, 1000);
        var invs = Items.Read(chest)!.Inventories.Select(i => i.Inventory).ToList();
        Save(new JsonObject
        {
            ["phase"] = "write", ["chest"] = chest.Value,
            ["can"] = Where(invs, canSlot.Slot), ["pouch"] = Where(invs, pouchSlot.Slot), ["control"] = Where(invs, control.Slot),
        });
        Info("READY restart (1 of 2): stop the lab (it saves) and start it again");
    }

    private static JsonObject Where(List<EntityId> invs, SlotId s) => new() { ["inventory"] = invs.IndexOf(s.Inventory), ["index"] = s.Index };
    private void Save(JsonObject st) => File.WriteAllText(StateFile, st.ToJsonString());

    private async Task AfterRestart()
    {
        var st = JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();
        var chest = new EntityId(st["chest"]!.GetValue<uint>());
        InventoryContents? inv = null;
        for (var i = 0; i < 40 && inv is not { Initialized: true }; i++)
        {
            inv = Items.Read(chest);
            if (inv is not { Initialized: true }) await Steps(25);
        }
        if (!Check(inv is { Initialized: true }, $"the chest {chest} is back")) return;
        // Entity ids change at a restart (the chest's 8-slot inventories too): find each container by its inventory's place
        // in the chest's link order, the slot index and the item.
        var invs = inv!.Inventories.Select(i => i.Inventory).ToList();
        EntityId At(string key)
        {
            var w = st[key]!;
            var item = Items.Lookup(key == "pouch" ? Pouch : Can)!.Id;
            var at = inv.Slots.FirstOrDefault(s => s.Item == item && s.Slot.Index == w["index"]!.GetValue<int>() &&
                                                     invs.IndexOf(s.Slot.Inventory) == w["inventory"]!.GetValue<int>());
            return at?.ItemEntity ?? EntityId.None;
        }
        EntityId can = At("can"), pouch = At("pouch"), control = At("control");
        if (!Check(!can.IsNone && !pouch.IsNone && !control.IsNone, $"the can, the pouch and the control can are in their slots: {can} {pouch} {control}"))
            return;
        if (st["phase"]!.GetValue<string>() == "write")
        {
            Check(Items.Fuel.Read(can)?.Units == 6400 && Items.Fuel.Read(pouch)?.Units == 0 && Items.Fuel.Read(control)?.Units == 1000,
                $"boot 1's values came back: can {Items.Fuel.Read(can)}, pouch {Items.Fuel.Read(pouch)}, control {Items.Fuel.Read(control)}");
            var a = await Items.Fuel.Set(can, 4321);
            var b = await Items.Fuel.Set(pouch, 777);
            World.TryWrite(control, "Fuel", 8, 3000);
            await Steps(3);
            Check(a.Ok && b.Ok && Items.Fuel.Read(can)?.Units == 4321 && Items.Fuel.Read(pouch)?.Units == 777 && Items.Fuel.Read(control)?.Units == 3000,
                $"written on loaded containers: can 4321 (tracked), pouch 777 (tracked), control 3000 (raw): {Items.Fuel.Read(can)}, {Items.Fuel.Read(pouch)}, {Items.Fuel.Read(control)}");
            st["phase"] = "check";
            Save(st);
            Info("READY restart (2 of 2): stop the lab (it saves) and start it again");
            return;
        }
        Check(Items.Fuel.Read(can)?.Units == 4321, $"the can kept the tracked write: {Items.Fuel.Read(can)}");
        Check(Items.Fuel.Read(pouch)?.Units == 777, $"the pouch kept the tracked write: {Items.Fuel.Read(pouch)}");
        Check(Items.Fuel.Read(control)?.Units == 1000,
            $"the control can lost the raw write, as the game's save skips untracked writes: {Items.Fuel.Read(control)} (raw 3000, saved 1000)");
        foreach (var s in inv.Slots.Where(s => s.ItemEntity == can || s.ItemEntity == pouch || s.ItemEntity == control).ToList())
            await Items.TakeSlot(chest, s);
        File.Delete(StateFile);
        Info("cleaned up");
    }

    private async Task<EntityId> Chest()
    {
        // a saved container of the world's (a lab made with --save from a world with a base), so the restart phase finds it
        for (var i = 0; i < 40; i++)
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
                if (Items.Read(e) is { Initialized: true } c && c.Slots.Count(s => s.IsEmpty) >= 4 && Spawn.Inspect(e)?.TemplateName?.Contains("Storage") == true)
                {
                    Check(true, $"a saved chest of the world: {e} {Spawn.Inspect(e)?.TemplateName}");
                    return e;
                }
            await Steps(25);
        }
        Check(false, "a saved chest in this world (make the lab with --save from a world that has a base)");
        return default;
    }
}
