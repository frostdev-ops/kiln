// SPDX-License-Identifier: MIT
// kiln.world's tracked writes on a dedicated server with no player. Boot 1: what the build says about every component
// type (a table in the mod's data folder), the refusals, a tracked write of an attribute (Fuel) and of a plain
// component, the change event, the check against the game's own writers, a can a spawned player character carries
// (CarriedPart.cs); baseline values left for a save.
// Boot 2: on entities a save has already written, a tracked write on one and a raw write on its twin.
// Boot 3: the tracked values are there, and the log says what the raw ones did. Stop and start the lab after each
// "READY restart".
using System.Text;
using System.Text.Json.Nodes;
using Kiln;

namespace WorldWritesTest;

public sealed partial class WorldWritesTestMod : Mod
{
    private const string Me = "world-writes-test";
    private const string Can = "Tool_T1_WateringCan";  // 100 charges = 6400 units of Fuel
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

    public override void OnLoad()
    {
        RegisterClientChecks();
        // The lab run starts by itself on a dedicated server; in a hosted game only the guided checks are offered.
        if (!Client.IsDedicatedServer) return;
        _ = Run().ContinueWith(t =>
        {
            if (t.Exception != null) Check(false, $"the test threw: {t.Exception.InnerException}");
            Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
        });
    }

    private async Task Run()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Items.All.Count > 0); i++) await Steps(25);
        await Steps(25);
        if (!Check(World.SupportsTrackedWrites, $"world.tracked_writes is available: {Capabilities.Why(World.TrackedWritesCapability)}")) return;
        if (File.Exists(StateFile)) await AfterRestart();
        else await Fresh();
    }

    private static int FuelUnits(EntityId e) => World.ReadBytes(e, "Fuel") is { Length: >= 16 } f ? BitConverter.ToInt32(f, 8) : -1;
    private static int NetFuel(EntityId e) => World.ReadBytes(e, "NetworkFuel") is { Length: >= 2 } n ? BitConverter.ToUInt16(n, 0) : -1;
    private static float Scale(EntityId e) => !e.IsNone && World.TryRead<float>(e, "ScaleComponent", out var s) ? s : float.NaN;

    // ---------------------------------------------------------------------------------------------- boot 1 ------

    private async Task Fresh()
    {
        Table();
        var fuel = World.WriteInfo<Ecs.Fuel>();
        Check(fuel is { CanWrite: true, Attribute: true, GameWriterCount: > 0 } && fuel.GameWriters.Contains("fuel_setup"),
            $"Fuel is listed (the build added it from World.Update<Ecs.Fuel>): {fuel.State}, attribute {fuel.Attribute}, " +
            $"{fuel.GameWriterCount} game writers ({string.Join(", ", fuel.GameWriters)}), tracked for the save {fuel.SaveTracked}, for replication {fuel.ReplicationTracked}");
        var scale = World.WriteInfo<Ecs.ScaleComponent>();
        Check(scale is { CanWrite: true, Attribute: false }, $"ScaleComponent is listed: {scale.State}, {scale.GameWriterCount} game writers, tracked for the save {scale.SaveTracked}, for replication {scale.ReplicationTracked}");
        var bogus = World.WriteInfo("NoSuchComponentKiln");
        Check(bogus.State == WorldWriteState.UnknownComponent && fuel.CanWrite,
            $"a listed name that is no component is refused alone, the write program runs without it: {bogus.State}: {bogus.Reason}");
        var stamina = World.WriteInfo("Stamina");
        Check(stamina is { State: WorldWriteState.NotDeclared, GameWriterCount: > 0 },
            $"an unlisted type can be asked about before listing it: Stamina {stamina.State}, {stamina.GameWriterCount} game writers, tracked for the save {stamina.SaveTracked}");

        var chest = await Chest();
        if (chest.IsNone) return;
        var canId = Items.Lookup(Can)!.Id;
        var can = await Give(chest, canId);
        var control = await Give(chest, canId);
        if (!Check(!can.Item.IsNone && !control.Item.IsNone, $"two watering cans in the chest {chest}: {can.Item} {control.Item}")) return;

        // refusals: every one an answer with Ok false and a sentence, none an exception
        var r = await World.WriteAt(can.Item, "Stamina", 8, 5);
        Check(r is { Ok: false, Status: WorldWriteStatus.NotDeclared } && r.Reason!.Contains("\"writes\""), $"a type that is not listed: {r}");
        r = await World.WriteAt(new EntityId(0x7ffffff0), "Fuel", 8, 5);
        Check(r is { Ok: false, Status: WorldWriteStatus.NoComponent }, $"an entity that does not exist: {r}");
        r = await World.WriteAt(chest, "Fuel", 8, 5);
        Check(r is { Ok: false, Status: WorldWriteStatus.NoComponent }, $"an entity without the component: {r}");
        r = await World.WriteAt(can.Item, "Fuel", 14, 5);
        Check(r is { Ok: false, Status: WorldWriteStatus.OutOfRange, ComponentSize: 16 }, $"bytes past the component's end: {r}");
        r = await World.Write(can.Item, new Ecs.Fuel());
        Check(r is { Ok: false, Status: WorldWriteStatus.Refused } && FuelUnits(can.Item) >= 0, $"a whole attribute built from nothing (its header would change): {r}");
        var threw = false;
        try { await World.WriteAt(EntityId.None, "Fuel", 8, 5); } catch (ArgumentException) { threw = true; }
        Check(threw, "EntityId.None is a bad argument: ArgumentException");

        // an attribute: Fuel, which nine of the game's programs write too
        var events = new List<ComponentWrittenEvent>();
        using (Events.On<ComponentWrittenEvent>(events.Add, EventFilter.ForEntity(can.Item)))
        {
            var before = FuelUnits(can.Item);
            r = await World.Update<Ecs.Fuel>(can.Item, (ref Ecs.Fuel f) => f.Values[0] = 1234);
            Check(r is { Ok: true, Changed: true, Attribute: true, GameWriterCount: > 0 } && FuelUnits(can.Item) == 1234 &&
                  BitConverter.ToInt32(r.Before.Span[8..]) == before && BitConverter.ToInt32(r.After.Span[8..]) == 1234,
                $"World.Update<Ecs.Fuel> {before} -> 1234: {r}; in {r.Steps} steps");
            Check(r.Kept == true, $"one step later, after the game's own Fuel writers ran again, the value was still there (Kept {r.Kept})");
            await Steps(3);
            Check(NetFuel(can.Item) == 1234, $"the game's own fuel_network program picked the write up: NetworkFuel {NetFuel(can.Item)}");
            r = await World.Update<Ecs.Fuel>(can.Item, (ref Ecs.Fuel f) => f.Values[0] = 999_999);
            Check(r.Ok && FuelUnits(can.Item) == 6400 && BitConverter.ToInt32(r.After.Span[8..]) == 6400,
                $"the game's formulas ran after the write: 999999 asked, {FuelUnits(can.Item)} held (the result says {BitConverter.ToInt32(r.After.Span[8..])})");
            r = await World.Update<Ecs.Fuel>(can.Item, (ref Ecs.Fuel f) => f.Values[0] = 6400);
            Check(r is { Ok: true, Changed: false, Status: WorldWriteStatus.Unchanged }, $"the same value again: {r}");
            r = await World.WriteField(can.Item, "Fuel", "dataStorage[0]", 4000);
            Check(r.Ok && FuelUnits(can.Item) == 4000, $"World.WriteField(Fuel, dataStorage[0], 4000): {r}");
            r = await World.WriteAt(can.Item, "Fuel", 8, 2000);
            Check(r.Ok && FuelUnits(can.Item) == 2000, $"World.WriteAt(Fuel, 8, 2000): {r}");
            r = await World.Update<Ecs.Fuel>(can.Item, (ref Ecs.Fuel f) => throw new InvalidOperationException("on purpose"));
            Check(r is { Ok: false, Status: WorldWriteStatus.Faulted } && r.Reason!.Contains("on purpose") && FuelUnits(can.Item) == 2000,
                $"a change that throws writes nothing: {r}");
            await Steps(2);
        }
        Check(events.Count == 4 && events.All(e => e.Entity == can.Item && e.Component == "Fuel" && e.ModId == Me) &&
              BitConverter.ToInt32(events[0].Result.After.Span[8..]) == 1234,
            $"ComponentWrittenEvent for each write that changed bytes, none for the unchanged or refused ones: {events.Count} event(s)");

        // a value the game derives every step: the check says so
        r = await World.WriteAt(can.Item, "NetworkFuel", 0, (ushort)77);
        await Steps(3);
        var net = NetFuel(can.Item);
        Info($"NetworkFuel (the game copies it from Fuel) written to 77: {r}; three steps later it reads {net}");
        Check(r.Ok && r.Kept == (net == 77), $"Kept ({r.Kept}) agrees with the read three steps later ({net})");

        // a plain component none of the game's programs writes: the whole value, typed
        var scaleWas = Scale(chest);
        if (!float.IsNaN(scaleWas))
        {
            r = await World.Write(chest, new Ecs.ScaleComponent { uniformScale = scaleWas });
            Check(r is { Ok: true, Changed: false, GameWriterCount: 0, Kept: null }, $"World.Write<Ecs.ScaleComponent> of the value it has: {r}");
            r = await World.Write(chest, new Ecs.ScaleComponent { uniformScale = scaleWas + 0.5f });
            Check(r is { Ok: true, Changed: true, Steps: 1 } && Scale(chest) == scaleWas + 0.5f, $"a type with no game writer answers one step later, no check needed: {r}; in {r.Steps} step(s)");
            r = await World.Update<Ecs.ScaleComponent>(chest, (ref Ecs.ScaleComponent s) => s.uniformScale -= 0.5f);
            Check(r.Ok && Scale(chest) == scaleWas, $"World.Update<Ecs.ScaleComponent> back: {r}");

            // The generation: an entity's SpawnTime. A chest loaded from the save has none, so a write to it is not kept
            // waiting behind a full step (512): the 513th of one step is answered at once.
            Info($"generations: chest {chest} has a SpawnTime: {World.Has(chest, "SpawnTime")}; can {can.Item}: {World.Has(can.Item, "SpawnTime")}");
            var full = Enumerable.Range(0, 512).Select(_ => World.Write(chest, new Ecs.ScaleComponent { uniformScale = scaleWas })).ToList();
            var behind = World.Write(chest, new Ecs.ScaleComponent { uniformScale = scaleWas });
            await Task.WhenAll(full.Append(behind));
            var lastStep = full.Max(t => t.Result.Steps);
            Check(World.Has(chest, "SpawnTime") || (behind.Result.Status == WorldWriteStatus.Dropped &&
                    behind.Result.Reason!.Contains("has no SpawnTime") && full.All(t => t.Result.Status == WorldWriteStatus.Unchanged)),
                $"513 writes in one step to an entity without a SpawnTime: 512 ran (Unchanged, in {lastStep} step(s)), the 513th was not kept waiting: {behind.Result}");

            // An entity the game spawned carries a SpawnTime: its write may wait behind a full step, and the generation read
            // at the call is compared with the one read right before the write (the same entity: they match).
            EntityId spawned = EntityId.None;
            string? listed = null;
            foreach (var name in (string[])["ScaleComponent", "ServerToggleState", "UsedItem", "Fuel"])
                if (World.Query(["SpawnTime", name], null).Entities.FirstOrDefault() is { IsNone: false } found) { spawned = found; listed = name; break; }
            if (spawned.IsNone) Info("generations: no entity with a SpawnTime carries a listed component in this world: the compare is not exercised here");
            else
            {
                var first = World.ReadBytes(spawned, listed!)![0];
                var ahead = Enumerable.Range(0, 512).Select(_ => World.Write(chest, new Ecs.ScaleComponent { uniformScale = scaleWas })).ToList();
                var waits = World.WriteAt(spawned, listed!, 0, first);
                await Task.WhenAll(ahead.Append(waits));
                Check(waits.Result is { Status: WorldWriteStatus.Unchanged } && waits.Result.Steps > ahead.Max(t => t.Result.Steps),
                    $"an entity with a SpawnTime ({spawned}, {listed}) waited behind a full step and its generation still matched right before the write: {waits.Result}; in {waits.Result.Steps} step(s)");
            }
        }

        // a can a player character carries, with and without a water refill on the character (CarriedPart.cs)
        try { await Carried(chest); }
        catch (Exception e) { Check(false, $"carried: the part threw: {e}"); }

        // baseline for the restart check: entities a save has written, values a save writes
        await World.Update<Ecs.Fuel>(can.Item, (ref Ecs.Fuel f) => f.Values[0] = 6400);
        await World.Update<Ecs.Fuel>(control.Item, (ref Ecs.Fuel f) => f.Values[0] = 1000);
        var invs = Items.Read(chest)!.Inventories.Select(i => i.Inventory).ToList();
        // other saved props, untouched by this test until boot 2: chests (UsedItem, ScaleComponent) and stations (ServerToggleState)
        var containers = Items.FindInventories(InventoryKinds.Containers).Where(e => e != chest).OrderBy(e => e.Value).ToList();
        var chests = containers.Where(e => World.Has(e, "UsedItem") && World.Has(e, "ScaleComponent") && !World.Has(e, "ServerToggleState")).Take(4).ToList();
        // two props of one kind with a toggle state (doors and lights before stations: a toggled station starts working)
        var toggles = World.Find("ServerToggleState").Where(e => World.Position(e) is not null && Spawn.Inspect(e)?.TemplateName is { Length: > 0 })
            .OrderBy(e => e.Value).ToList();
        Info($"props with a ServerToggleState: {string.Join(", ", toggles.GroupBy(e => Spawn.Inspect(e)!.TemplateName).Select(g => $"{g.Count()} {g.Key}"))}");
        static int Rank(string t) => t.Contains("Door") ? 0 : t.Contains("Torch") || t.Contains("Lamp") || t.Contains("Light") || t.Contains("Lantern") ? 1 : t.Contains("Factory") ? 3 : 2;
        var stations = toggles.GroupBy(e => Spawn.Inspect(e)!.TemplateName!).Where(g => g.Count() >= 2).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Take(2).ToList() ?? toggles.Take(2).ToList();
        Info($"saved props for the restart check: chests {string.Join(", ", chests.Select(Describe))}; toggles {string.Join(", ", stations.Select(Describe))}");
        // Entity ids are handed out again at every start, so a prop is found again by where it stands.
        File.WriteAllText(StateFile, new JsonObject
        {
            ["phase"] = "write", ["chest"] = Place(chest),
            ["can"] = Where(invs, can.Slot), ["control"] = Where(invs, control.Slot),
            ["chests"] = new JsonArray([.. chests.Select(Place)]),
            ["stations"] = new JsonArray([.. stations.Select(Place)]),
        }.ToJsonString());
        Info("READY restart (1 of 2): stop the lab (it saves) and start it again");
    }

    private static string Describe(EntityId e) => $"{e} {Spawn.Inspect(e)?.TemplateName}";

    private static JsonNode Place(EntityId e)
    {
        var p = World.Position(e) ?? default;
        return new JsonObject { ["template"] = Spawn.Inspect(e)?.TemplateName, ["x"] = p.X, ["y"] = p.Y, ["z"] = p.Z };
    }

    // The saved prop that stands where `place` says (within 5 cm) and has that template; none while it has not loaded.
    private static EntityId Found(JsonNode place, string component)
    {
        double x = place["x"]!.GetValue<double>(), y = place["y"]!.GetValue<double>(), z = place["z"]!.GetValue<double>();
        var template = place["template"]?.GetValue<string>();
        foreach (var e in World.Find(component))
            if (World.Position(e) is { } p && Math.Abs(p.X - x) < 0.05 && Math.Abs(p.Y - y) < 0.05 && Math.Abs(p.Z - z) < 0.05 &&
                Spawn.Inspect(e)?.TemplateName == template) return e;
        return EntityId.None;
    }
    private static uint UsedItem(EntityId e) => World.TryRead<uint>(e, "UsedItem", out var v) ? v : 0;
    // ServerToggleState { bool state @0; Time lastChange @8; bool isExtinguished @16 }
    private static int Toggle(EntityId e) => World.ReadBytes(e, "ServerToggleState") is { Length: >= 24 } b ? b[0] : -1;
    private static long ToggleTime(EntityId e) => World.ReadBytes(e, "ServerToggleState") is { Length: >= 24 } b ? BitConverter.ToInt64(b, 8) : -1;

    // ---------------------------------------------------------------------------------------- boots 2 and 3 ------

    private async Task AfterRestart()
    {
        var st = JsonNode.Parse(File.ReadAllText(StateFile))!.AsObject();
        EntityId chest = default;
        List<EntityId> chests = [], stations = [];
        InventoryContents? inv = null;
        for (var i = 0; i < 40 && !(inv is { Initialized: true } && chests.Concat(stations).All(e => !e.IsNone)); i++)
        {
            await Steps(25);
            chest = Found(st["chest"]!, "UsedItem");
            chests = [.. st["chests"]!.AsArray().Select(n => Found(n!, "UsedItem"))];
            stations = [.. st["stations"]!.AsArray().Select(n => Found(n!, "ServerToggleState"))];
            inv = chest.IsNone ? null : Items.Read(chest);
        }
        if (!Check(inv is { Initialized: true }, $"the chest is back where it stood: {Describe(chest)}")) return;
        if (!Check(chests.Concat(stations).All(e => !e.IsNone) && chests.Concat(stations).Append(chest).Distinct().Count() == chests.Count + stations.Count + 1,
                $"the other saved props are back where they stood: {string.Join(", ", chests.Concat(stations).Select(Describe))}")) return;
        var invs = inv!.Inventories.Select(i => i.Inventory).ToList();
        var canId = Items.Lookup(Can)!.Id;
        EntityId At(string key)
        {
            var w = st[key]!;
            return inv.Slots.FirstOrDefault(s => s.Item == canId && s.Slot.Index == w["index"]!.GetValue<int>() &&
                                                 invs.IndexOf(s.Slot.Inventory) == w["inventory"]!.GetValue<int>())?.ItemEntity ?? EntityId.None;
        }
        EntityId can = At("can"), control = At("control");
        if (!Check(!can.IsNone && !control.IsNone, $"the can and the control can are in their slots: {can} {control}")) return;
        // which prop gets which write: UsedItem tracked on chests[0] and raw on chests[1]; ScaleComponent tracked on
        // chests[2] and raw on chests[3] (props nothing else touches), and raw too on chests[0], which the tracked UsedItem
        // write marks for the save; ServerToggleState.lastChange tracked on stations[0], raw on stations[1].
        EntityId C(int i) => i < chests.Count ? chests[i] : EntityId.None;
        EntityId S(int i) => i < stations.Count ? stations[i] : EntityId.None;

        if (st["phase"]!.GetValue<string>() == "write")
        {
            Check(FuelUnits(can) == 6400 && FuelUnits(control) == 1000, $"boot 1's values came back: can {FuelUnits(can)}, control {FuelUnits(control)}");
            var a = await World.Update<Ecs.Fuel>(can, (ref Ecs.Fuel f) => f.Values[0] = 4321);
            var raw = World.WriteRaw(control, "Fuel", 8, 3000);
            await Steps(3);
            Check(a.Ok && raw && FuelUnits(can) == 4321 && FuelUnits(control) == 3000,
                $"Fuel on containers a save has written: can 4321 (tracked: {a}), control 3000 (raw)");
            if (!C(1).IsNone)
            {
                st["used0"] = UsedItem(C(0));
                st["used1"] = UsedItem(C(1));
                var u = await World.WriteAt(C(0), "UsedItem", 0, canId.Value);
                var rawU = World.WriteRaw(C(1), "UsedItem", 0, canId.Value);
                st["rider"] = (double)Scale(C(0));
                var rider = World.WriteRaw(C(0), "ScaleComponent", 0, 1.75f);
                await Steps(2);
                Check(u is { Ok: true, GameWriterCount: 0, SaveTracked: true } && rawU && rider && UsedItem(C(0)) == canId.Value && UsedItem(C(1)) == canId.Value,
                    $"UsedItem (no game writer, tracked for the save): {C(0)} tracked ({u}), {C(1)} raw; and a raw ScaleComponent 1.75 on {C(0)}");
            }
            else Info("fewer than two other saved chests: no UsedItem pair");
            if (!C(3).IsNone)
            {
                st["scale2"] = (double)Scale(C(2));
                st["scale3"] = (double)Scale(C(3));
                var s = await World.Write(C(2), new Ecs.ScaleComponent { uniformScale = 1.25f });
                var rawS = World.WriteRaw(C(3), "ScaleComponent", 0, 1.5f);
                await Steps(2);
                Check(s is { Ok: true, SaveTracked: false } && rawS && Scale(C(2)) == 1.25f && Scale(C(3)) == 1.5f,
                    $"ScaleComponent (no game writer, not tracked for the save): {C(2)} tracked 1.25 ({s}), {C(3)} raw 1.5");
            }
            else Info("fewer than four other saved chests: no ScaleComponent pair");
            if (!S(1).IsNone)
            {
                st["toggle0"] = Toggle(S(0));
                st["toggle1"] = Toggle(S(1));
                st["time0"] = ToggleTime(S(0));
                byte flip0 = (byte)(Toggle(S(0)) ^ 1), flip1 = (byte)(Toggle(S(1)) ^ 1);
                var t = await World.WriteAt(S(0), "ServerToggleState", 0, flip0);
                var rawT = World.WriteRaw(S(1), "ServerToggleState", 0, flip1);
                var time = await World.WriteAt(S(0), "ServerToggleState", 8, ToggleTime(S(0)) + 12345);
                await Steps(2);
                Check(t is { Ok: true, GameWriterCount: > 0, SaveTracked: true } && rawT && time.Ok && Toggle(S(0)) == flip0 && Toggle(S(1)) == flip1,
                    $"ServerToggleState.state (the game writes the type too, tracked for the save): {S(0)} tracked to {flip0} ({t}), {S(1)} raw to {flip1}; " +
                    $"and lastChange of {S(0)} tracked to {ToggleTime(S(0))}");
                Info($"ServerToggleState: Kept {t.Kept}; game writers {string.Join(", ", t.GameWriters)}");
            }
            else Info("fewer than two saved props with a ServerToggleState: no pair");
            st["phase"] = "check";
            File.WriteAllText(StateFile, st.ToJsonString());
            Info("READY restart (2 of 2): stop the lab (it saves) and start it again");
            return;
        }

        Check(FuelUnits(can) == 4321, $"Fuel kept the tracked write across the save and restart: {FuelUnits(can)} (written 4321)");
        var lost = FuelUnits(control) == 1000;
        Check(lost, $"the raw Fuel control was {(lost ? "lost: the save skipped the untracked write" : "NOT lost")}: {FuelUnits(control)} (raw 3000, saved before it 1000)");
        if (st["used0"] is { } u0)
        {
            uint was0 = u0.GetValue<uint>(), was1 = st["used1"]!.GetValue<uint>();
            Check(UsedItem(C(0)) == canId.Value, $"UsedItem kept the tracked write across the save and restart: {C(0)} 0x{UsedItem(C(0)):x8} (written 0x{canId.Value:x8}, before 0x{was0:x8})");
            var rawLost = UsedItem(C(1)) == was1;
            Check(rawLost, $"the raw UsedItem control was {(rawLost ? "lost" : "NOT lost")}: {C(1)} 0x{UsedItem(C(1)):x8} (raw 0x{canId.Value:x8}, saved before it 0x{was1:x8})");
            Info($"the raw ScaleComponent 1.75 on {C(0)}, the prop the tracked UsedItem write marked for the save, reads {Scale(C(0))} (before {st["rider"]})");
            await World.WriteAt(C(0), "UsedItem", 0, was0);
            await World.Write(C(0), new Ecs.ScaleComponent { uniformScale = (float)st["rider"]!.GetValue<double>() });
        }
        if (st["scale2"] is { } s2)
        {
            float tracked = Scale(C(2)), rawNow = Scale(C(3));
            Check(tracked != 1.25f && rawNow != 1.5f,
                $"ScaleComponent, which the save does not track, was not kept either way: tracked 1.25 reads {tracked} (before {s2}), raw 1.5 reads {rawNow} (before {st["scale3"]})");
        }
        if (st["toggle0"] is { } t0)
        {
            int was0 = t0.GetValue<int>(), was1 = st["toggle1"]!.GetValue<int>();
            long time0 = st["time0"]!.GetValue<long>();
            Check(Toggle(S(0)) == (was0 ^ 1), $"ServerToggleState.state kept the tracked write across the save and restart: {S(0)} {Toggle(S(0))} (written {was0 ^ 1}, before {was0})");
            var rawLost = Toggle(S(1)) == was1;
            Check(rawLost, $"the raw ServerToggleState control was {(rawLost ? "lost" : "NOT lost")}: {S(1)} {Toggle(S(1))} (raw {was1 ^ 1}, saved before it {was1})");
            Info($"ServerToggleState.lastChange, written tracked with the same component: reads {ToggleTime(S(0))} (written {time0 + 12345}, before {time0}): " +
                 (ToggleTime(S(0)) == time0 + 12345 ? "kept" : "the game's save record of the type does not carry this field"));
            await World.WriteAt(S(0), "ServerToggleState", 0, (byte)was0);
        }
        foreach (var s in inv.Slots.Where(s => s.ItemEntity == can || s.ItemEntity == control).ToList())
            await Items.TakeSlot(chest, s);
        File.Delete(StateFile);
        Info("cleaned up");
    }

    // ------------------------------------------------------------------------------------------- helpers ------

    private static JsonObject Where(List<EntityId> invs, SlotId s) => new() { ["inventory"] = invs.IndexOf(s.Inventory), ["index"] = s.Index };

    private async Task<(EntityId Item, SlotId Slot)> Give(EntityId chest, ItemId item)
    {
        var r = await Items.Give(chest, item, 1, options: GiveOptions.NoMerge);
        await Steps(2);
        var slot = r.Changes.Count > 0 ? Items.Read(chest)?.Slots.FirstOrDefault(s => s.Slot == r.Changes[0].Slot) : null;
        return slot is null ? default : (slot.ItemEntity, slot.Slot);
    }

    // a saved chest of the world (a lab made with --save from a world with a base), so a restart finds it again
    private async Task<EntityId> Chest()
    {
        for (var i = 0; i < 40; i++)
        {
            var found = Items.FindInventories(InventoryKinds.Containers)
                .Where(e => Items.Read(e) is { Initialized: true } c && c.Slots.Count(s => s.IsEmpty) >= 4 &&
                            Spawn.Inspect(e)?.TemplateName?.Contains("Storage") == true)
                .OrderBy(e => e.Value).ToList();
            if (found.Count > 0)
            {
                Check(true, $"a saved chest of the world: {Describe(found[0])}");
                return found[0];
            }
            await Steps(25);
        }
        Check(false, "a saved chest in this world (make the lab with --save from a world that has a base)");
        return default;
    }

    // What this game build says about every component type: who writes it, whether the save and replication carry it.
    // The evidence table of docs/re/world.md "Tracked writes" is made from this file.
    private void Table()
    {
        var text = new StringBuilder("component\tsize\tattribute\tgame_writers\tinit_writers\tsave_tracked\treplication_tracked\tstate\twriters\n");
        int n = 0, none = 0, saved = 0, sent = 0;
        foreach (var type in GameTypes.Components.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            var i = World.WriteInfo(type.Name);
            n++;
            if (i.GameWriterCount == 0) none++;
            if (i.SaveTracked == true) saved++;
            if (i.ReplicationTracked == true) sent++;
            text.Append(type.Name).Append('\t').Append(i.ComponentSize).Append('\t').Append(i.Attribute ? 1 : 0).Append('\t')
                .Append(i.GameWriterCount).Append('\t').Append(i.InitWriterCount).Append('\t').Append(i.SaveTracked).Append('\t')
                .Append(i.ReplicationTracked).Append('\t').Append(i.State).Append('\t').AppendJoin(", ", i.GameWriters).Append('\n');
        }
        File.WriteAllText(Path.Combine(Context.DataDirectory, "components.tsv"), text.ToString());
        Check(n > 600 && none > 0 && saved > 0 && sent > 0,
            $"the build's answer for every component type: {n} types, {none} that no game program writes, {saved} tracked for the save, {sent} tracked for replication (components.tsv)");
        // the components of a saved chest and of an item in it, for picking test targets
        var sb = new StringBuilder();
        foreach (var e in Items.FindInventories(InventoryKinds.Containers).Take(3))
        {
            sb.Append(e).Append('\t').Append(Spawn.Inspect(e)?.TemplateName).Append('\t');
            sb.AppendJoin(' ', GameTypes.Components.Where(t => World.Has(e, t.Name)).Select(t => t.Name)).Append('\n');
        }
        File.WriteAllText(Path.Combine(Context.DataDirectory, "entities.tsv"), sb.ToString());
    }
}
