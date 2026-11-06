// SPDX-License-Identifier: MIT
// Water containers as item blocks, on a dedicated server with no player: a watering can that holds 1,000 charges and flows 4x, one at the 1,023
// limit, one that keeps the game's capacity, and a pouch from blocks/water.json. Checks the new cans' own water templates, that the game's can and its
// template are untouched, what a can holds on the server (charges x 64) and what players are sent (NetworkFuel, 16 bits), and a full can across a
// restart. Start the lab twice: the second start runs the restart phase. Observer/ (mod blocks-water-observer) logs what an unload puts back.
using System.Text.Json;
using System.Text.Json.Nodes;
using Kiln;

namespace BlocksWaterTest;

public sealed class BlocksWaterTestMod : Mod
{
    private const string Me = "blocks-water-test";
    private const string Blacksmith = "0x41bf48ce";
    private static readonly Guid GamePide = Guid.Parse("814530fb-dd12-48b9-8d8c-fab5d19b5a35");   // Pide_Fuel_Watering_Can
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

    private string StateFile => Path.Combine(Context.DataDirectory, "phase1.json");
    private static string G(string block) => $"{Me}.{block}";

    public override void OnLoad()
    {
        Blocks.Define("big-can", new ItemBlock
        {
            Name = "Big Watering Can", Description = "Holds ten times the water and fills four times as fast.",
            Kind = ItemKind.WateringCan, Holds = 1000, Flow = 4,
            Recipe = new RecipeBlock { Station = Blacksmith, Needs = { ["Material_T1_Metal_Scraps"] = 10, ["Material_T1_Charcoal"] = 6, ["Material_T1_Coal_Powder"] = 2 } },
        });
        Blocks.Define("edge-can", new ItemBlock { Name = "Edge Can", Kind = ItemKind.WateringCan, Holds = 1023 });
        Blocks.Define("plain-can", new ItemBlock { Name = "Plain Can", Kind = ItemKind.WateringCan });
        try
        {
            Blocks.Define("over-can", new ItemBlock { Name = "Over Can", Kind = ItemKind.WateringCan, Holds = 1024 });
            Check(false, "holds 1024 is refused");
        }
        catch (BlockError e)
        {
            Check(e.Field == "holds" && e.Problem.Contains("1..1023"), $"holds 1024 is refused: {e.Message}");
        }
        Content.OnReady(e => { _ = Run().ContinueWith(t => { if (t.Exception != null) Check(false, $"the test threw: {t.Exception.InnerException}"); }); });
    }

    private async Task Run()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        // blocks/*.json compile after OnLoad (on a hot load, after this event): wait for the pouch
        for (var i = 0; i < 200 && Content.Items.Get(G("big-pouch")) == null; i++) await Steps(1);
        await Steps(10);
        if (File.Exists(StateFile)) await AfterRestart();
        else await Fresh();
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    }

    private static GameDataEntry? T(string name) => Content.Templates.Get(name);
    private static GameDataEntry? D(string item) => Content.ItemData.Get(item);
    private static long Max(GameDataEntry t) => t.Get("FuelSetup.maxFuelCharges").Integer;
    private static int Volume(string item) => (int)D(item)!.Get("impactValues.Volume").AsDouble;

    private async Task Fresh()
    {
        // ---- what the blocks registered ----
        foreach (var b in new[] { "big-can", "edge-can", "plain-can", "big-pouch" })
            Check(Content.Items.Get(G(b)) != null, $"item {G(b)} is registered");
        var big = T(G("big-can.water"));
        var edge = T(G("edge-can.water"));
        var pouch = T(G("big-pouch.water"));
        var game = T("Pide_Fuel_Watering_Can")!;
        Check(big != null && Content.Templates.IsClone(big.Guid) && Max(big) == 1000, $"big-can's water template is a clone holding 1000 charges: {big?.Name} {(big == null ? "" : Max(big))}");
        Check(edge != null && Max(edge) == 1023, $"edge-can's holds 1023: {(edge == null ? "none" : Max(edge))}");
        Check(pouch != null && Max(pouch) == 60, $"big-pouch (blocks/water.json) holds 60: {(pouch == null ? "none" : Max(pouch))}");
        Check(T(G("plain-can.water")) == null, "plain-can (no holds) has no template of its own");
        Check(Max(game) == 100 && !game.Modified, $"the game's Pide_Fuel_Watering_Can is untouched: {Max(game)}, modified {game.Modified}");
        Check(D(G("big-can"))!.Get("pidEntity").Guid == big?.Guid, $"big-can's item entity is its clone: {D(G("big-can"))!.Get("pidEntity").Guid}");
        Check(D(G("plain-can"))!.Get("pidEntity").Guid == GamePide, "plain-can's item entity is the game's template");
        Check(D("Tool_T1_WateringCan")!.Get("pidEntity").Guid == GamePide, "the game's Watering Can still names its own template");
        Check(Volume(G("big-can")) == 128 && Volume(G("big-pouch")) == 32 && Volume(G("plain-can")) == 32 && Volume("Tool_T1_WateringCan") == 32,
            $"flow: big-can moves 128 a step (4 x 32), big-pouch 32 (2 x 16), plain-can and the game's can 32: {Volume(G("big-can"))}, {Volume(G("big-pouch"))}, {Volume(G("plain-can"))}, {Volume("Tool_T1_WateringCan")}");
        var recipe = Content.Recipes.Get(G("big-can.recipe"));
        Check(recipe != null && (uint)recipe.Get("workshopId").AsDouble == 0x41bf48ce, $"big-can's recipe is at the Blacksmith: {recipe?.Name} workshop 0x{(uint)(recipe?.Get("workshopId").AsDouble ?? 0):x8}");

        // ---- on the server: the items' own entities ----
        var chest = await Chest();
        if (chest.IsNone) return;
        (string Item, int Max)[] cans =
        [
            ("Tool_T1_WateringCan", 6400), (G("big-can"), 64000), (G("edge-can"), 65472), (G("plain-can"), 6400), (G("big-pouch"), 3840), ("Tool_Water_Pouch_Refillable", 1280),
        ];
        InventorySlot? keep = null;
        foreach (var (item, max) in cans)
        {
            var slot = await Give(chest, item);
            if (!Check(slot != null, $"gave {item}")) continue;
            var (cur, fmax, net, netMax) = Fuel(slot!.ItemEntity);
            Check(fmax == max && netMax == max, $"{item}: holds {fmax} on the server (charges x 64), players are sent {netMax}");
            if (item == G("big-can"))
            {
                Check(Spawn.Inspect(slot.ItemEntity)?.Template == big?.Guid, $"its entity is made from the clone: {Spawn.Inspect(slot.ItemEntity)?.TemplateName}");
                World.TryWrite(slot.ItemEntity, "Fuel", 8, max);   // filled, as a lake would
                await Steps(5);
                (cur, _, net, _) = Fuel(slot.ItemEntity);
                Check(cur == max && net == max, $"full: {cur} on the server, {net} sent to players");
                keep = slot;
                continue;
            }
            await Items.TakeSlot(chest, slot);
            await Steps(2);
        }
        if (keep == null || Spawn.Inspect(chest) is not { } at) return;
        File.WriteAllText(StateFile, new JsonObject { ["chest"] = chest.Value, ["slot"] = keep.Slot.ToString(), ["template"] = at.TemplateName }.ToJsonString());
        Info("a full big-can stays in the chest for the restart phase: stop the lab (it saves) and start it again");
    }

    private async Task AfterRestart()
    {
        var bigId = Content.Items.Get(G("big-can"))!.Id;
        for (var i = 0; i < 240 && !Spawn.WorldRunning; i++) await Steps(25);
        InventorySlot? slot = null;
        EntityId chest = default;
        for (var i = 0; i < 40 && slot == null; i++)
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
                if (Items.Read(e) is { Initialized: true } inv && inv.Slots.FirstOrDefault(s => s.Item.Value == bigId) is { } s) { slot = s; chest = e; break; }
            if (slot == null) await Steps(25);
        }
        if (!Check(slot != null, $"after the restart the chest {chest} still holds the big-can")) return;
        var (cur, max, net, netMax) = Fuel(slot!.ItemEntity);
        Check(cur == 64000 && max == 64000 && net == 64000 && netMax == 64000, $"and it is still full: {cur}/{max} on the server, {net}/{netMax} sent to players");
        Check(Spawn.Inspect(slot.ItemEntity)?.Template == T(G("big-can.water"))?.Guid, $"its entity is still the clone: {Spawn.Inspect(slot.ItemEntity)?.TemplateName}");
        await Items.TakeSlot(chest, slot);
        File.Delete(StateFile);
    }

    private static (int Cur, int Max, int Net, int NetMax) Fuel(EntityId pide)
    {
        var f = World.ReadBytes(pide, "Fuel");
        var n = World.ReadBytes(pide, "NetworkFuel");
        return (f is { Length: >= 16 } ? BitConverter.ToInt32(f, 8) : -1, f is { Length: >= 16 } ? BitConverter.ToInt32(f, 12) : -1,
                n is { Length: >= 4 } ? BitConverter.ToUInt16(n, 0) : -1, n is { Length: >= 4 } ? BitConverter.ToUInt16(n, 2) : -1);
    }

    private async Task<InventorySlot?> Give(EntityId chest, string item)
    {
        var id = Content.Items.Get(item)!.Id;
        await Items.Give(chest, new ItemId(id), 1);
        await Steps(3);
        return Items.Read(chest)?.Slots.LastOrDefault(s => s.Item.Value == id);
    }

    private async Task<EntityId> Chest()
    {
        // content is ready before the world runs: wait for it
        for (var i = 0; i < 240 && !Spawn.WorldRunning; i++) await Steps(25);
        // a saved container of the world's (a lab made with --save from a world with a base), so the restart phase finds the can again
        for (var i = 0; i < 40; i++)
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
                if (Items.Read(e) is { Initialized: true } c && c.Slots.Count(s => s.IsEmpty) >= 6 && Spawn.Inspect(e)?.TemplateName?.Contains("Storage") == true)
                {
                    Check(true, $"a saved chest of the world to put cans in: {e} {Spawn.Inspect(e)?.TemplateName}");
                    return e;
                }
            await Steps(25);
        }
        Info("no saved chest in this world (a lab without --save): the restart phase cannot find the can");
        // content is ready before the world runs: wait for it, and for the start's altar to be there
        WorldPosition? anchor = null;
        for (var i = 0; i < 120 && anchor == null; i++)
        {
            if (Spawn.WorldRunning)
                foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
                    if (anchor == null)
                        foreach (var id in Spawn.FindByName(filter, max: 4))
                            if (Spawn.Inspect(id) is { } info) { anchor = info.Position; break; }
            if (anchor == null) await Steps(25);
        }
        if (!Check(anchor != null, $"an anchor near the start: {anchor}")) return default;
        var chest = await Spawn.Entity("Prop_Decoration_T1_Storage_24", anchor!.Value with { X = anchor.Value.X - 5, Z = anchor.Value.Z - 5 }, 0, new SpawnOptions { Tag = Me });
        for (var i = 0; i < 60 && Items.Read(chest) is not { Initialized: true }; i++) await Steps(1);
        Check(Items.Read(chest) is { Initialized: true }, $"a chest to put cans in: {chest}");
        return chest;
    }
}
