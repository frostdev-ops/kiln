// SPDX-License-Identifier: MIT
// What Kiln.Blocks makes of stations and props, on a dedicated server with no player: the exact cycle, running out of materials, burning fuel, a workshop nothing else knows, Build.PlaceProp on a
// template clone, what comes back after a restart inside a base, C# and JSON the same, and the errors a mistake gets. Start it twice: the second run checks what the first one saved.
using System.Numerics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Kiln;

namespace StationsTest;

public sealed class StationsTestMod : Mod
{
    private const string Sample = "station-blocks";
    private const string Twigs = "Material_T1_Twigs", Logs = "Material_T1_Wood_Logs", Charcoal = "Material_T1_Charcoal", Scraps = "Material_T1_Metal_Scraps", Sheets = "Material_T1_Metal_Sheets", Resin = "Material_T1_Resin";
    private const uint ForgeWorkshop = 0x8dca2c49, RackWorkshop = 0x6b95dc33;
    private static readonly BuildOptions InBases = new() { Flags = BuildFlags.InBases };
    private int _pass, _fail;
    private readonly List<EntityId> _placed = new();

    private void Info(string s) => Log.Info($"stations-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"stations-test: PASS {what}"); }
        else { _fail++; Log.Error($"stations-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static string G(string block) => $"{Sample}.{block}";
    private static ContentEntry I(string block) => Content.Items.Get(G(block)) ?? throw new InvalidOperationException($"no item {block}");
    private static GameDataEntry T(string template) => Content.Templates.Get(template) ?? throw new InvalidOperationException($"no template {template}");
    private static uint Workshop(GameDataEntry t) => (uint)t.Get("Crafting.workshopId").Number;
    private static ItemId Id(string item) => new(Content.Items.Get(item)!.Id);
    private static int Count(EntityId e, string item) => Items.Read(e)?.Count(Id(item)) ?? 0;

    private string Show(EntityId e)
    {
        var c = Items.Read(e);
        var inv = c == null ? "no inventory" : string.Join(", ", c.Slots.Where(s => s.Count > 0).Select(s => $"{Content.Items.Get(s.Item)?.Name ?? s.Item.ToString()} x{s.Count}"));
        return $"{Stations.Status(e)} [{inv}]";
    }

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        // from code: what blocks/*.json can't say, and a model cooked by tools/modelcook, if the sample that serves it is here
        Blocks.Define("plain-rack", new StationBlock { Name = "Plain Rack", Processing = new ProcessingBlock { Slots = 6 }, Recipes = [new RecipeBlock { Seconds = 1, Needs = { [Twigs] = 1 }, Output = Resin }] });
        Blocks.Define("big-rack", new StationBlock { Name = "Big Rack", Processing = new ProcessingBlock { Slots = 32 }, Recipes = [new RecipeBlock { Seconds = 1, Needs = { [Twigs] = 1 }, Output = Resin }] });
        Blocks.Define("roof-bench", new StationBlock { Name = "Roof Bench", Recipes = [new RecipeBlock { Sheltered = true, Needs = { [Twigs] = 1 }, Output = Resin }, new RecipeBlock { Needs = { [Twigs] = 2 }, Output = Logs }] });
        Blocks.Define("spa", new PropBlock { Name = "Spa", Visual = new VisualBlock { Like = "Prop_Decoration_Comfort1_T0_Table_Medium" }, Comfort = new ComfortBlock { Amount = 5, Category = ComfortCategory.Wellness } });
        Blocks.Define("shared-forge", new StationBlock { Name = "Shared Forge", Kind = StationKind.Forge, OwnWorkshop = false, Recipes = [new RecipeBlock { Seconds = 2, Needs = { [Twigs] = 1 }, Output = Resin }] });
        Blocks.Define("other-menu", new StationBlock { Name = "Other Menu", Kind = StationKind.Forge, Menu = new MenuBlock { Like = StationKind.DryingRack }, Processing = new ProcessingBlock(), Recipes = [new RecipeBlock { Seconds = 2, Needs = { [Twigs] = 1 }, Output = Resin }] });
        Blocks.Define("running-rack", new StationBlock
        {
            Name = "Running Rack", Kind = StationKind.Forge,
            Processing = new ProcessingBlock { GoesOutInRain = false, Running = new RunningBlock { Sound = Guid.Parse("633cc47d-51a7-446c-b980-63333e58231c"), Vfx = VfxRef.Game("T1_Forge_Factory") } },
        });
        Blocks.Define("own-look-rack", new StationBlock { Name = "Own Look", Kind = StationKind.Forge, Visual = new VisualBlock { Like = "Prop_Production_T1_Drying_Rack" } });
        Blocks.Define("lit-table", new PropBlock { Name = "Lit Table", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Light = VfxRef.Game("T1_Forge_Factory") });
        Blocks.Define("copied-table", new PropBlock { Name = "Copied", Visual = new VisualBlock { Like = $"{Sample}:old-table" } });
        foreach (var (name, on) in new[] { ("floor-model", Surface.Floor), ("wall-model", Surface.Wall), ("ceiling-model", Surface.Ceiling), ("two-surfaces", Surface.Wall | Surface.Ceiling) })
            Blocks.Define(name, new PropBlock { Name = name, Visual = new VisualBlock { Model = "item:Prop_Decoration_T2_Vase_Clay_02" }, Placement = new PlacementBlock { On = on, Water = true, Snap = true, Consumed = false, InBase = false } });
        if (Mods.IsLoaded("model-sample"))
            Blocks.Define("cooked-prop", new PropBlock { Name = "Cooked", Visual = new VisualBlock { Model = "08525f9a-f6f9-46f4-8c3e-003e481d740d" } });
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!(Spawn.WorldRunning && Build.Available && Build.Grid is not null) && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(150);
            var saved = Path.Combine(Context.DataDirectory, "saved.json");
            if (File.Exists(saved))
            {
                await Part("after the restart", () => AfterRestart(saved));
                File.Delete(saved);
            }
            else
            {
                await Part("mistakes", () => { Mistakes(); return Task.CompletedTask; });
                await Part("registered", () => { Registered(); return Task.CompletedTask; });
                await Part("placement data", () => { PlacementData(); return Task.CompletedTask; });
                await Part("places as the game's own", () => { PlacesAsTheGame(); return Task.CompletedTask; });
                await Part("twins", () => { Twins(); return Task.CompletedTask; });
                var at = BaseSpot();
                Info($"working next to a station of the world's base, at {at}");
                await Part("placing a clone", () => PlacingClones(at));
                await Part("the exact cycle", () => ExactCycle(at));
                await Part("burning fuel", () => BurningFuel(at));
                await Part("the workshop gate", () => WorkshopGate(at));
                await Part("slots", () => Slots(at));
                await Part("shelter", () => { Shelter(); return Task.CompletedTask; });
                await Part("comfort", () => Comfort(at));
                await Part("props", () => Props(at));
                await Part("leaving for a restart", () => LeaveRunning(at, saved));
            }
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        Log.Info($"stations-test done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Part(string name, Func<Task> run)
    {
        try { await run(); }
        catch (Exception e) { Check(false, $"{name}: exception: {e}"); }
    }

    // a spot inside the world's base: beside one of its stations
    private static WorldPosition BaseSpot()
    {
        foreach (var e in World.Find("FactoryStation"))
            if (World.Position(e) is { } p) return p;
        throw new InvalidOperationException("this world has no station to stand next to");
    }

    private static WorldPosition Near(WorldPosition at, int index) => new(at.X + 1.5 * index, at.Y, at.Z + 3);

    private async Task<EntityId> Place(string template, WorldPosition at, BuildOptions? options = null, uint item = 0)
    {
        var e = Build.PlaceProp(template, at, null, item, options ?? InBases);
        _placed.Add(e);
        await Steps(10);
        return e;
    }

    private void Remove()
    {
        foreach (var e in _placed)
            if (World.Exists(e)) Build.RemoveProp(e, InBases);
        _placed.Clear();
    }

    // ---- mistakes ----------------------------------------------------------------------------------------------

    private void Mistakes()
    {
        var mine = Blocks.Report().GetValueOrDefault("stations-test");
        foreach (var e in mine.Errors ?? []) Info($"error: {e}");
        void Named(string block, params string[] words)
        {
            var e = mine.Errors?.FirstOrDefault(x => Regex.IsMatch(x, $@"(?<![-\w]){Regex.Escape(block)}(?![-\w])"));
            Check(e != null && words.All(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)), $"{block}: \"{e}\"");
        }
        Named("no-name", "name");
        Named("typo-field", "kinds");
        Named("bad-kind", "kind", "anvil", "forge");
        Named("bench-processing", "processing", "workbench");
        Named("many-slots", "processing.slots", "500");
        Named("no-output", "recipes[0].output");
        Named("no-seconds", "recipes[0].seconds");
        Named("bad-seconds", "recipes[0].seconds", "-1");
        Named("bench-seconds", "recipes[0].seconds", "workbench");
        Named("recipe-station", "recipes[0].station");
        Named("no-inputs", "recipes[0].needs");
        Named("bad-burn", "recipes[0].burns", "less than 1");
        Named("effect-output", "recipes[0].output", "an-effect");
        Named("bad-sound", "processing.running.sound");
        Named("glb-model", "visual.model", "cook");
        Named("text-model", "visual.model", "a-model");
        Named("look-vfx", "visual.vfx");
        Named("rack-roof", "recipes[0].sheltered", "rain");
        Named("no-such-process", "processing", "gone");
        Named("wrong-process", "processing", "an-effect");
        Named("no-such-menu", "menu", "gone");
        Named("bad-comfort", "comfort.amount", "-1");
        Named("bad-range", "comfort.range");
        Named("bad-category", "category");
        Named("bad-on", ".on");
        Named("bad-size", "placement.size");
        Named("no-look", "visual", "look");
        Named("no-prop-name", "name");
        Named("part-size", "size");
        Named("part-range", "range");
        Named("part-slots", "slots");
        string[] broken = ["rack-roof", "no-name", "typo-field", "bad-kind", "bench-processing", "many-slots", "no-output", "no-seconds", "bad-seconds", "bench-seconds", "recipe-station", "no-inputs", "bad-burn", "effect-output", "bad-sound", "glb-model", "text-model", "look-vfx", "no-such-process", "wrong-process", "no-such-menu", "bad-comfort", "bad-range", "bad-category", "bad-on", "bad-size", "no-look", "no-prop-name", "part-size", "part-range", "part-slots"];
        var made = (mine.Blocks ?? []).Intersect(broken).ToList();
        Check(made.Count == 0 && Content.Items.Get("stations-test.many-slots") == null && Content.Templates.Get("stations-test.many-slots.station") == null, $"none of the broken blocks became anything ({string.Join(", ", made)})");
        var late = mine.Errors?.FirstOrDefault(x => x.Contains("not-placed", StringComparison.Ordinal));
        Check(late != null && late.Contains("places no template", StringComparison.Ordinal), $"a look that places nothing is said by name when the game's items are there: \"{late}\"");

        var thrown = "";
        try { Blocks.Define("bad", new StationBlock { Name = "Bad", Kind = StationKind.Workbench, Processing = new ProcessingBlock() }); }
        catch (BlockError e) { thrown = e.Message; }
        Check(thrown.Contains("station bad", StringComparison.Ordinal) && thrown.Contains("processing", StringComparison.Ordinal), $"C#: a wrong value throws with the block and the field: \"{thrown}\"");
        var output = "";
        try { Blocks.Define("bad-item", new ItemBlock { Name = "Bad", Recipe = new RecipeBlock { Output = Logs, Needs = { [Twigs] = 1 } } }); }
        catch (BlockError e) { output = e.Message; }
        Check(output.Contains("item bad-item", StringComparison.Ordinal) && output.Contains("recipe.output", StringComparison.Ordinal), $"C#: an item's recipe makes that item: \"{output}\"");
        var own = "";
        try { Blocks.Define("own-no-time", new ItemBlock { Name = "x", Recipe = new RecipeBlock { Station = Blocks.Find("cold-forge", Sample)!.ToString(), Needs = { [Twigs] = 1 } } }); }
        catch (BlockError e) { own = e.Message; }
        Check(own.Contains("recipe.seconds", StringComparison.Ordinal), $"C#: at a station of its own a recipe says its time: \"{own}\"");
    }

    // ---- it is all there ----------------------------------------------------------------------------------------

    private void Registered()
    {
        var report = Blocks.Report().GetValueOrDefault(Sample);
        Check(report.Errors is { Count: 0 }, $"the sample's blocks came through with no word from the services: [{string.Join(" | ", report.Errors ?? [])}]");
        var clients = File.ReadAllText(Path.Combine(Mods.Get(Sample)!.Directory, "kiln.mod.json"));
        Check(clients.Contains("\"both\"", StringComparison.Ordinal) && clients.Contains("\"kiln\"", StringComparison.Ordinal), "the SDK made the manifest side both, clients kiln, from KilnServices alone");

        foreach (var n in new[] { "herb-rack", "cold-forge" })
        {
            var h = Blocks.Find(n, Sample)!;
            var e = I(n);
            var t = T(G(n) + ".station");
            var own = BlockIds.Fnv(BlockIds.Derive(Sample, "workshop", n));
            Check(e.Guid == BlockIds.Derive(Sample, "item", n) && e.Id == h.Id, $"{n}: the item's GUID follows from the mod and the name");
            Check(t.Guid == BlockIds.Derive(Sample, "template", $"{n}.station") && e.GetRef("equipment.placedEntity") == t.Guid, $"{n}: the item places a template of its own: {t.Guid}");
            Check(Workshop(t) == own, $"{n}: a workshop id from the mod and the name: 0x{Workshop(t):x8}");
        }
        var rack = T(G("herb-rack") + ".station");
        var forge = T(G("cold-forge") + ".station");
        // a workshop of its own is also what its screens show: in a player's game a menu of its own, laid out as its kind's
        Check((uint)rack.Get("CraftingInteraction.workshopId").Number == Workshop(rack), $"herb-rack: its screens show its own workshop: 0x{(uint)rack.Get("CraftingInteraction.workshopId").Number:x8}");
        Check((uint)forge.Get("CraftingInteraction.workshopId").Number == Workshop(forge), $"cold-forge: the same: 0x{(uint)forge.Get("CraftingInteraction.workshopId").Number:x8}");
        Check(I("herb-rack").Get("flags").Name?.Contains("IsFactoryEntity") != false, "a station's item counts against the world's factory budget, as the game's do");

        var berry = Content.Recipes.Get(G("herb-rack.makes.Food_T1_processed_fruit_Dried_RedBerry"));
        Check(berry?.CraftingDuration == TimeSpan.FromSeconds(300) && (uint)(berry?.Get("workshopId").Integer ?? 0) == Workshop(rack), $"a recipe in the list: at the block's workshop, its time: {berry?.CraftingDuration}, 0x{(uint)(berry?.Get("workshopId").Integer ?? 0):x8}");
        var def = NewContent.Definitions().FirstOrDefault(d => d.Name == G("herb-rack.makes.Material_T1_Wood_Logs"));
        Check(def?.DefinitionJson is { } dj && dj.Contains("0x6b95dc33", StringComparison.Ordinal) && dj.Contains("workshop:", StringComparison.Ordinal), $"and its menu takes the rack's tabs: {def?.DefinitionJson}");
        Check(def?.DefinitionJson is { } dj2 && dj2.Contains($"\"stationItem\":\"{G("herb-rack")}\"", StringComparison.Ordinal), $"and is named after the item that places the station: {def?.DefinitionJson}");
        var ingotDef = NewContent.Definitions().FirstOrDefault(d => d.Name == G("frost-ingot.recipe"));
        Check(ingotDef?.DefinitionJson is { } dj3 && dj3.Contains($"\"stationItem\":\"{G("cold-forge")}\"", StringComparison.Ordinal), $"an item's recipe that names the station too: {ingotDef?.DefinitionJson}");
        var resinDef = NewContent.Definitions().FirstOrDefault(d => d.Name == G("fine-bench.makes.Material_T1_Resin"));
        Check(resinDef?.DefinitionJson is { } dj4 && !dj4.Contains("stationItem", StringComparison.Ordinal), $"a bench that shares its kind's workshop names none: {resinDef?.DefinitionJson}");
        var ingot = Content.Recipes.Get(G("frost-ingot.recipe"));
        Check(ingot != null && (uint)ingot.Get("workshopId").Integer == Workshop(forge) && ingot.CraftingDuration == TimeSpan.FromSeconds(2), $"an item that names the station is at its workshop: 0x{(uint)(ingot?.Get("workshopId").Integer ?? 0):x8}");
        Check(ingot?.Inputs.Any(i => i.Item.Value == Id(Charcoal).Value && i.Count == 1) == true && ingot.Inputs.Count == 2, $"and what it burns is an input: {string.Join(", ", ingot?.Inputs ?? [])}");

        var bench = T(G("fine-bench") + ".station");
        Check(Workshop(bench) == 0x4da88f73, $"a bench shares the game's workbench: 0x{Workshop(bench):x8}");
        var resin = Content.Recipes.Get(G("fine-bench.makes.Material_T1_Resin"));
        Check((uint)(resin?.Get("workshopId").Integer ?? 0) == 0x4da88f73, $"so its recipe is at the game's workbench: 0x{(uint)(resin?.Get("workshopId").Integer ?? 0):x8}");

        var shared = T("stations-test.shared-forge.station");
        Check(Workshop(shared) == ForgeWorkshop, $"OwnWorkshop = false: the kind's: 0x{Workshop(shared):x8}");
        var other = T("stations-test.other-menu.station");
        var otherDef = NewContent.Definitions().FirstOrDefault(d => d.Name == "stations-test.other-menu.makes.Material_T1_Resin");
        Check((uint)other.Get("CraftingInteraction.workshopId").Number == Workshop(other) && Workshop(other) != ForgeWorkshop && Workshop(other) != RackWorkshop
              && otherDef?.DefinitionJson?.Contains("\"menu\":\"0x6b95dc33\"", StringComparison.Ordinal) == true,
            $"a Menu of another station: runs its own, its menu takes that one's tabs: 0x{Workshop(other):x8}, 0x{(uint)other.Get("CraftingInteraction.workshopId").Number:x8}, {otherDef?.DefinitionJson}");
        var running = T("stations-test.running-rack.station");
        Check(Math.Abs(running.Get("Extinguish.extinguishAboveRainAmount").Number - 1e9) < 1 && forge.Get("Extinguish.extinguishAboveRainAmount").Number > 1e8, $"GoesOutInRain = false: above any rain: {running.Get("Extinguish.extinguishAboveRainAmount").Number}");
        Check(running.Get("FactoryStationClient.soundLoopRunning").Guid == Guid.Parse("633cc47d-51a7-446c-b980-63333e58231c"), $"Running.Sound: the template's loop, carried by a server with no sound: {running.Get("FactoryStationClient.soundLoopRunning").Guid}");

        // the look of a placed thing is on three things
        var look = Content.Items.Get("stations-test.own-look-rack")!;
        var like = Content.Items.Get("Prop_Production_T1_Drying_Rack")!;
        var lookT = T("stations-test.own-look-rack.station");
        Check(lookT.Get("ModelResource.model").Guid == like.GetRef("iconModel") && look.GetRef("equipment.cursorModel") == like.GetRef("iconModel") && look.GetRef("iconModel") == like.GetRef("iconModel"),
            $"a station with another's look: what stands, the ghost and the picture are that model: {lookT.Get("ModelResource.model").Guid}");
        var table = T(G("old-table") + ".prop");
        var game = Content.Templates.Get("Prop_Furniture_Comfort1_Table_A_Wood")!;
        Check(table.Get("ModelResource.model").Guid == game.Get("ModelResource.model").Guid, $"a prop with a game item's look starts from the template that item places: {table.Get("ModelResource.model").Guid}");
        Check(I("old-table").GetRef("equipment.cursorModel") == Content.Items.Get("Prop_Decoration_Comfort1_T0_Table_Medium")!.GetRef("equipment.cursorModel"), "and its item from that item");
        var copied = T("stations-test.copied-table.prop");
        Check(copied.Get("ModelResource.model").Guid == game.Get("ModelResource.model").Guid && copied.Get("ComfortProvider.comfort").Number == 3, $"a prop like another block's prop: its model, and its comfort {copied.Get("ComfortProvider.comfort").Number}");
        if (Content.Templates.Get("stations-test.cooked-prop.prop") is { } cooked)
        {
            var m = Guid.Parse("08525f9a-f6f9-46f4-8c3e-003e481d740d");
            var item = Content.Items.Get("stations-test.cooked-prop")!;
            Check(cooked.Get("ModelResource.model").Guid == m && item.GetRef("equipment.cursorModel") == m && item.GetRef("iconModel") == m, $"a model cooked by tools/modelcook stands, ghosts and pictures: {cooked.Get("ModelResource.model").Guid}");
        }
        else Info("model-sample is not here: a cooked model was not tried");
        var lit = T("stations-test.lit-table.prop");
        Check(lit.Get("VfxComponentResource.vfx").Guid == Content.Templates.Get("T1_Forge_Factory")!.Get("VfxComponentResource.vfx").Guid && lit.Get("VfxComponent.attachmentOffset.orientationOffset.w").Number == 1, $"Light on a prop that had no VFX: added, with a rotation: {lit.Get("VfxComponentResource.vfx").Guid}");
        var problems = (Blocks.Report().GetValueOrDefault("stations-test").Errors ?? []).Where(x => x.StartsWith("stations-test.", StringComparison.Ordinal) && !x.Contains("not-placed", StringComparison.Ordinal)).ToList();
        Check(problems.Count == 0, $"nothing else from the services about this mod's blocks: [{string.Join(" | ", problems)}]");
    }

    private void PlacementData()
    {
        static string F(string block, string path) => Content.Items.Get($"stations-test.{block}")!.Get($"equipment.{path}").Name ?? "";
        const string A = "cursorConfig.attachAngleConfig";
        Check(F("floor-model", $"{A}.floorAngle") == "Narrow" && F("floor-model", $"{A}.wallAngle") == "Disabled" && F("floor-model", $"{A}.defaultAttachDirection") == "Floor", "On = Floor");
        Check(F("wall-model", $"{A}.wallAngle") == "Narrow" && F("wall-model", $"{A}.floorAngle") == "Disabled" && F("wall-model", $"{A}.defaultAttachDirection") == "Wall", "On = Wall");
        Check(F("ceiling-model", $"{A}.ceilingAngle") == "Narrow" && F("ceiling-model", $"{A}.defaultAttachDirection") == "Ceiling", "On = Ceiling");
        Check(F("two-surfaces", $"{A}.wallAngle") == "Narrow" && F("two-surfaces", $"{A}.ceilingAngle") == "Narrow" && F("two-surfaces", $"{A}.floorAngle") == "Disabled" && F("two-surfaces", $"{A}.defaultAttachDirection") == "Wall" && F("two-surfaces", $"{A}.secondaryAttachDirection") == "Ceiling", "On = Wall | Ceiling: the first is tried first");
        var i = Content.Items.Get("stations-test.floor-model")!;
        Check(i.Get("equipment.buildZoneRequired").Integer == 0 && i.Get("equipment.placementConsume").Integer == 0, "InBase = false, Consumed = false");
        Check(i.Get("equipment.cursorConfig.allowWaterPlacement").Integer != 0 && i.Get("equipment.waterUsability.outsideBuildzone.allowInsideWater").Integer != 0 && i.Get("equipment.waterUsability.insideBuildzone.allowInsideWater").Integer != 0, "Water = true");
        Check(i.Get("equipment.cursorConfig.translationConfig.defaultSnapping").Integer != 0, "Snap = true");
        // starters follow the surface
        Check(T("stations-test.floor-model.prop").Get("ModelResource.model").Guid == Content.Items.Get("Prop_Decoration_T2_Vase_Clay_02")!.GetRef("iconModel"), "a model by item: \"item:<name>\"");
        var banner = I("lucky-banner");
        Check(banner.Get("equipment.allowPlacementBelowFog").Integer != 0, "Fog = true");
        Check(Math.Abs(banner.Get("equipment.placementAABBmax.y").Number - 3) < 1e-4 && Math.Abs(banner.Get("equipment.placementAABBmin.x").Number + 0.5) < 1e-4 && Math.Abs(banner.Get("equipment.placementAABBmax.z").Number - 0.125) < 1e-4, $"Size: a box standing on its floor, y up to {banner.Get("equipment.placementAABBmax.y").Number}");
        var rack = I("herb-rack");
        Check(rack.Get("equipment.buildZoneRequired").Integer != 0, "InBase = true");
    }

    // ---- what the game's placement check reads ---------------------------------------------------------------

    // Every placed thing of the block mods here (stations, props, furniture; C# and JSON): its item and its template are the game's starter's, apart from ids,
    // texts and what the block says. The game's placement check (client_cursor / server_cursor_no_prediction) reads only the item: its box and colliders,
    // the angles it attaches at, whether it needs a base, how it treats no-build zones, its category and flags. So a block places where its starter places.
    private static readonly string[] ItemOwn = ["itemId", "objectId", "name", "caption", "description", "lore", "acquisitionHint", "debugName", "equipment.placedEntity", "iconModel", "iconImage", "iconScene", "iconRenderOffset", "iconRenderCookingScale", "iconRenderGlobalScale",
        "equipment.cursorModel", "uiValues", "itemColorCombinationSetup", "rarity", "generateRarity", "itemLevelRange"];

    private void PlacesAsTheGame()
    {
        var dir = Path.Combine(Context.DataDirectory, "as-the-game");
        Directory.CreateDirectory(dir);
        var mods = new[] { Sample, "item-blocks", "furniture-blocks", "stations-test" }.Where(m => Blocks.Report().ContainsKey(m)).ToList();
        var checkedItems = 0;
        foreach (var mod in mods)
            foreach (var block in Blocks.Report()[mod].Blocks)
            {
                // a block that was refused on purpose (the mistakes part) places nothing to compare
                if (Blocks.Report()[mod].Errors.Any(e => e.Contains($" {block} ", StringComparison.Ordinal) || e.Contains($".{block}:", StringComparison.Ordinal) || e.Contains($".{block}.prop:", StringComparison.Ordinal))) continue;
                if (JsonNode.Parse(Blocks.Lowered(block, mod) ?? "null") is not JsonObject low || low["items"] is not JsonArray items) continue;
                foreach (var item in items.OfType<JsonObject>())
                {
                    if (item["refs"]?["equipment.placedEntity"] is not { } placed || item["base"] is not { } baseNode) continue;
                    var name = (string)item["name"]!;
                    try
                    {
                    var starter = (string)baseNode!;
                    var tmpl = low["templates"]!.AsArray().OfType<JsonObject>().First(t => (string)t["guid"]! == (string)placed!);
                    var own = ItemOwn.Concat(Keys(item["fields"])).Concat(Keys(item["refs"])).ToList();
                    var diff = Diff(Dump(Content.ItemData, name, dir), Dump(Content.ItemData, starter, dir), own);
                    Check(diff.Count == 0, $"{mod}:{block}: its item is {starter}'s in every field the game's placement reads{(diff.Count > 0 ? ": differs in " + string.Join(", ", diff.Take(8)) : "")}");
                    var reg = Content.Items.Get(name);
                    Check(reg?.GetRef("equipment.placedEntity") == Guid.Parse((string)placed!) && Content.Templates.Get(Guid.Parse((string)placed!)) != null,
                        $"{mod}:{block}: it places its own template, which the game has: {reg?.GetRef("equipment.placedEntity")}");
                    var baseName = (string)tmpl["base"]!;
                    var baseTemplate = baseName.StartsWith("item:", StringComparison.Ordinal) ? Content.Templates.Get(Content.Items.Get(baseName[5..])!.GetRef("equipment.placedEntity"))?.Name ?? baseName : baseName;
                    var tOwn = Keys(tmpl["fields"]).Concat(tmpl["addComponents"]?.AsArray().Select(c => (string)c! + ".") ?? []).Concat(Keys(low["templateRules"])).ToList();
                    var tDiff = Diff(Dump(Content.Templates, (string)tmpl["name"]!, dir), Dump(Content.Templates, baseTemplate, dir), tOwn);
                    Check(tDiff.Count == 0, $"{mod}:{block}: its template is {baseTemplate}'s, colliders and all, apart from what the block sets{(tDiff.Count > 0 ? ": differs in " + string.Join(", ", tDiff.Take(8)) : "")}");
                    checkedItems++;
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException)
                    {
                        Check(false, $"{mod}:{block}: {e.Message}");
                    }
                }
            }
        Check(checkedItems >= 10, $"{checkedItems} placed things of {string.Join(", ", mods)} compared with the game's");
    }

    private static IEnumerable<string> Keys(JsonNode? o) => o is JsonObject j ? j.Select(kv => kv.Key) : [];

    private static Dictionary<string, string> Dump(GameDataRegistry r, string key, string dir)
    {
        var file = Path.Combine(dir, Regex.Replace(key, "[^A-Za-z0-9_.-]", "_") + ".json");
        r.Dump(key, file);
        var entries = JsonNode.Parse(File.ReadAllText(file))!["entries"]!.AsArray();
        if (entries.Count == 0) throw new InvalidOperationException($"kiln.gamedata has no entry {key}");
        var e = entries[0]!;
        var flat = new Dictionary<string, string>();
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject o:
                    foreach (var (k, v) in o) Walk(v, path.Length == 0 ? k : $"{path}.{k}");
                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++) Walk(a[i], $"{path}[{i}]");
                    if (a.Count == 0) flat[path] = "[]";
                    break;
                default:
                    flat[path] = n?.ToJsonString() ?? "null";
                    break;
            }
        }
        Walk(e["value"] ?? e["components"], "");
        return flat;
    }

    // Paths that differ, leaving out a path the block owns (itself, or anything under it).
    private static List<string> Diff(Dictionary<string, string> a, Dictionary<string, string> b, IReadOnlyList<string> own) =>
        a.Keys.Union(b.Keys)
            .Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k))
            .Where(k => !own.Any(o => k == o.TrimEnd('.') || k.StartsWith(o.TrimEnd('.') + ".", StringComparison.Ordinal) || k.StartsWith(o.TrimEnd('.') + "[", StringComparison.Ordinal)))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();

    // JSON and C# are the same objects: what they lower to differs only in names and the ids that follow from names.
    private void Twins()
    {
        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"), "-json|0x[0-9a-f]{8}|(?<=\"(Crafting|CraftingInteraction)\\.workshopId\": )[0-9]+", m => m.Value == "-json" ? "" : m.Value is "0x6b95dc33" or "0x8dca2c49" or "0x4da88f73" ? m.Value : "0xID");
        foreach (var n in new[] { "warm-hearth", "herb-rack", "cold-forge", "frost-ingot", "fine-bench", "old-table", "lucky-banner" })
        {
            var cs = Normal(Blocks.Lowered(n, Sample));
            var json = Normal(Blocks.Lowered(n + "-json", Sample));
            Check(cs.Length > 10 && cs == json, $"{n}: blocks/twins.json lowers to what the C# does ({cs.Length} characters of definitions)");
            if (cs != json) Info($"C#: {cs}\nJSON: {json}");
        }
        // and they have their own ids, from their own names
        Check(Blocks.Find("herb-rack", Sample)!.Guid != Blocks.Find("herb-rack-json", Sample)!.Guid && Workshop(T(G("herb-rack") + ".station")) != Workshop(T(G("herb-rack-json") + ".station")), "and each has its own ids");
    }

    // ---- Build.PlaceProp knows clones ---------------------------------------------------------------------------

    private async Task PlacingClones(WorldPosition at)
    {
        var template = G("herb-rack") + ".station";
        var item = I("herb-rack");
        var byName = await Place(template, Near(at, 0));
        Check(!byName.IsNone && Spawn.Inspect(byName)?.Template == T(template).Guid, $"PlaceProp by a clone's name: {byName}, template {Spawn.Inspect(byName)?.Template}");
        var used = World.ReadBytes(byName, "UsedItem");
        Check(used is { Length: >= 4 } && BitConverter.ToUInt32(used, 0) == item.Id, $"and what dismantling gives back is the item that places it: 0x{(used is { Length: >= 4 } ? BitConverter.ToUInt32(used, 0) : 0):x8}");
        var byGuid = Build.PlaceProp(T(template).Guid, Near(at, 1), null, 0, InBases);
        _placed.Add(byGuid);
        Check(!byGuid.IsNone, $"PlaceProp by a clone's GUID: {byGuid}");
        var byItem = Build.PlaceProp(G("herb-rack"), Near(at, 2), null, 0, InBases);
        _placed.Add(byItem);
        Check(!byItem.IsNone && Spawn.Inspect(byItem)?.Template == T(template).Guid, $"PlaceProp by the item that places it: {byItem}");
        var spawned = await Spawn.Entity(template, Near(at, 3));
        _placed.Add(spawned);
        Check(!spawned.IsNone, $"Spawn.Entity finds the same: {spawned}");
        var nothing = "";
        try { Build.PlaceProp("no-such-template", Near(at, 4), null, 0, InBases); }
        catch (BuildException e) { nothing = e.Message; }
        Check(nothing.Contains("not found", StringComparison.Ordinal), $"a name that is nothing still says so: \"{nothing}\"");
        Check(Build.Prop(byName)?.TemplateName == template, $"Build.Prop reads it back: {Build.Prop(byName)?.TemplateName}");
        await Steps(4);
        Remove();
        await Steps(4);
    }

    // ---- processing ---------------------------------------------------------------------------------------------

    private async Task ExactCycle(WorldPosition at)
    {
        var rack = await Place(G("herb-rack") + ".station", Near(at, 0));
        var recipe = Content.Recipes.Get(G("herb-rack.makes.Material_T1_Wood_Logs"))!;
        Check(Stations.Status(rack) is { State: StationState.NoRecipe or StationState.MissingMaterials }, $"a placed rack waits: {Show(rack)}");
        var given = await Items.Give(rack, Twigs, 6);
        Check(given.Status == ItemsStatus.Done, $"its own inventory takes materials: {given}");
        Check(Stations.Start(rack, recipe.Id), "Stations.Start: as a player's choice does");
        var starts = new List<TimeSpan>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = TimeSpan.MinValue;
        StationStatus? s = null;
        while (clock.Elapsed.TotalSeconds < 30)
        {
            await Steps(1);
            s = Stations.Status(rack);
            if (s == null) break;
            if (s.State == StationState.Running)
            {
                if (s.Started != last) { last = s.Started; starts.Add(s.Started); Info($"cycle {starts.Count} at {s.Started.TotalSeconds:F3}: {Show(rack)}"); }
            }
            else if (s.State != StationState.NoRecipe) break;   // between two cycles it says no recipe, for a step
        }
        Check(starts.Count == 3, $"6 twigs, 2 a cycle: {starts.Count} cycles");
        for (var i = 1; i < starts.Count; i++)
        {
            var took = (starts[i] - starts[i - 1]).TotalSeconds;
            // the recipe's 4 s, and the next cycle starts within the game's next few steps: it finishes one, then its inventory transaction starts the next
            Check(took >= 4 - 1e-3 && took < 4.2, $"cycle {i}: its 4 s, and the next starts within a few of the game's steps: {took:F3} s");
        }
        Check(Count(rack, Logs) == 9 && Count(rack, Twigs) == 0, $"3 logs each: {Show(rack)}");
        Check(s is { State: StationState.MissingMaterials } && s.Needs.HasFlag(StationNeeds.InputMaterials) && s.Next == recipe.Id, $"out of materials it stops, and says why: {s}");
        Check(World.ReadBytes(rack, "ServerToggleState") is { } off && off[0] == 0, "and its toggle goes off");
        // more materials: it goes on by itself, as the game's stations do
        await Items.Give(rack, Twigs, 2);
        await Steps(48);
        Check(Stations.Status(rack) is { State: StationState.Running, Running: var r } && r == recipe.Id, $"given more, it goes on: {Show(rack)}");
        Check(World.ReadBytes(rack, "ServerToggleState") is { } on && on[0] == 1, "and its toggle follows");
        Remove();
        await Steps(4);
    }

    private async Task BurningFuel(WorldPosition at)
    {
        var forge = await Place(G("cold-forge") + ".station", Near(at, 0));
        var recipe = Content.Recipes.Get(G("cold-forge.makes.Material_T1_Metal_Sheets"))!;
        Check(recipe.Inputs.Any(i => i.Item.Value == Id(Charcoal).Value && i.Count == 2) && recipe.Inputs.Any(i => i.Item.Value == Id(Scraps).Value && i.Count == 1), $"Burns is an input: {string.Join(", ", recipe.Inputs)}");
        await Items.Give(forge, Scraps, 5);
        await Items.Give(forge, Charcoal, 4);
        Stations.Start(forge, recipe.Id);
        await Steps(12);
        Check(Stations.Status(forge) is { State: StationState.Running } && Count(forge, Charcoal) == 2, $"a cycle takes its fuel when it starts: {Show(forge)}");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (Stations.Status(forge) is not { State: StationState.MissingMaterials } && clock.Elapsed.TotalSeconds < 20) await Steps(2);
        Check(Stations.Status(forge) is { State: StationState.MissingMaterials } && Count(forge, Charcoal) == 0 && Count(forge, Scraps) == 3 && Count(forge, Sheets) == 2, $"no fuel left: it stops with metal left, 2 sheets made: {Show(forge)}");
        await Items.Give(forge, Charcoal, 2);
        await Steps(48);
        Check(Stations.Status(forge) is { State: StationState.Running } && Count(forge, Scraps) == 2, $"more fuel: on it goes: {Show(forge)}");
        Remove();
        await Steps(4);
    }

    private async Task WorkshopGate(WorldPosition at)
    {
        var forge = await Place(G("cold-forge") + ".station", Near(at, 0));
        var game = await Place("T1_Forge_Factory", Near(at, 2));
        var mine = Content.Recipes.Get(G("cold-forge.makes.Material_T1_Metal_Sheets"))!;
        var theirs = Content.Recipes.Get("Material_T1_Factory_Metal_Sheets") ?? Content.Recipes.First(r => (uint)r.Get("workshopId").Integer == ForgeWorkshop && r.Outputs.Any(o => o.Item.Value == Id(Sheets).Value));
        Info($"the game's recipe: {theirs.Name}: {string.Join(", ", theirs.Inputs)}");
        foreach (var e in new[] { forge, game })
        {
            foreach (var i in theirs.Inputs.Concat(mine.Inputs))
                if (i.Item.Value != 0) await Items.Give(e, new ItemId(i.Item.Value), i.Count * 3);
        }
        Stations.Start(forge, mine.Id);
        await Steps(24);
        Check(Stations.Status(forge) is { State: StationState.Running } s && s.Running == mine.Id, $"a forge with a workshop nothing else knows runs its recipe: {Show(forge)}");
        Stations.Start(game, mine.Id);
        await Steps(24);
        Check(Stations.Status(game) is { State: StationState.MissingMaterials, Running: 0 } && Count(game, Charcoal) >= 6, $"the game's forge doesn't run it, with everything there: {Show(game)}");
        Stations.Start(forge, theirs.Id);
        await Steps(96);
        Check(Stations.Status(forge) is { Running: 0, Next: var n } && n == theirs.Id, $"nor does it run the game's: {Show(forge)}");
        Stations.Start(game, theirs.Id);
        await Steps(24);
        Check(Stations.Status(game) is { State: StationState.Running } g && g.Running == theirs.Id, $"which the game's forge does: {Show(game)}");
        // sharing is the other way round
        var shared = await Place("stations-test.shared-forge.station", Near(at, 4));
        var added = Content.Recipes.Get("stations-test.shared-forge.makes.Material_T1_Resin")!;
        await Items.Give(shared, Twigs, 4);
        foreach (var i in theirs.Inputs) if (i.Item.Value != 0) await Items.Give(shared, new ItemId(i.Item.Value), i.Count);
        Stations.Start(shared, added.Id);
        await Steps(24);
        Check(Stations.Status(shared) is { State: StationState.Running } a && a.Running == added.Id, $"OwnWorkshop = false: it runs what it lists: {Show(shared)}");
        await Steps(72);
        Stations.Start(shared, theirs.Id);
        await Steps(72);
        Check(Stations.Status(shared) is { Next: var next } && next == theirs.Id && Stations.Status(shared)!.State is StationState.Running, $"and the game's: {Show(shared)}");
        var bench = Content.Recipes.Count(r => (uint)r.Get("workshopId").Integer == Workshop(T(G("cold-forge") + ".station")));
        Check(bench >= 2, $"the recipes of the new workshop are in the registry: {bench}");
        Remove();
        await Steps(4);
    }

    private async Task Slots(WorldPosition at)
    {
        foreach (var (block, slots) in new[] { ("plain-rack", 6), ("big-rack", 32) })
        {
            var rack = await Place($"stations-test.{block}.station", Near(at, 0));
            var c = Items.Read(rack);
            Check(c?.AvailableSlots == slots, $"{block}: Slots = {slots}: {c?.GenericSlots} generic, {c?.AvailableSlots} available");
            var given = await Items.Give(rack, Twigs, 250 * (slots + 2));
            await Steps(2);
            c = Items.Read(rack);
            // The inventory itself grows in eights, so a 6 has room for 8 by Items.Give: what a player's menu makes of it is a client check.
            Check(c != null && c.Slots.Count(s => s.Count > 0) == c.GenericSlots && c.GenericSlots >= slots, $"{block}: and they hold something: {c?.Slots.Count(s => s.Count > 0)} of {c?.GenericSlots} ({given.Status})");
            Remove();
            await Steps(4);
        }
    }

    // The game's flag on a recipe a player crafts. A station that works on its own doesn't check it: a rack in the open ran a recipe that had it (lab), and the game gives it to none of its own.
    private void Shelter()
    {
        var roofed = Content.Recipes.Get("stations-test.roof-bench.makes.Material_T1_Resin")!;
        var open = Content.Recipes.Get("stations-test.roof-bench.makes.Material_T1_Wood_Logs")!;
        Check(roofed.Get("requiresSheltered").Integer != 0 && open.Get("requiresSheltered").Integer == 0, "Sheltered is the recipe's own flag, on a bench");
        var factories = Content.Recipes.Where(r => (uint)r.Get("workshopId").Integer is ForgeWorkshop or RackWorkshop).ToList();
        Check(factories.Count > 0 && factories.All(r => r.Get("requiresSheltered").Integer == 0), $"and none of the game's {factories.Count} forge and rack recipes has it");
    }

    private async Task<WorldPosition> OpenGround()
    {
        double x = 3024, z = 1550;
        var box = Build.BoxBetween(new WorldPosition(x, 850, z), new WorldPosition(x, 960, z));
        var cells = await Build.ReadAsync(box);
        for (var y = box.SizeY - 1; y >= 0; y--)
        {
            var c = cells[box.Index(0, y, 0)];
            if (c.Fill > 0 && c.Material is > 0 and < 128) return new WorldPosition(x, Build.CornerOf(new CellPos(box.X, box.Y + y, box.Z)).Y + c.Fill / 255.0 * 0.5 + 0.05, z);
        }
        throw new InvalidOperationException("no ground at the open spot");
    }

    // ComfortProvider is a part of the template: an entity holds no copy, and the game's system reads it from the template of whatever stands near an actor.
    private async Task Comfort(WorldPosition at)
    {
        var t = T(G("cold-forge") + ".station");
        Check(t.Get("ComfortProvider.comfort").Number == 2 && t.Get("ComfortProvider.category").Name == "Hearth" && t.Get("ComfortProvider.range").Number == 5 && t.Get("ComfortProvider.freezingResistance").Number == 1,
            $"a forge has no comfort of its own, and this one does: {t.Get("ComfortProvider.comfort").Number}, {t.Get("ComfortProvider.category").Name}");
        var game = Content.Templates.Get("T1_Forge_Factory")!;
        var theirs = "";
        try { theirs = game.Get("ComfortProvider.comfort").Number.ToString(); }
        catch (Exception e) when (e is KeyNotFoundException or ArgumentException) { theirs = "none"; }
        Check(theirs == "none", $"the game's forge is what it was: {theirs}");
        Check(Workshop(T(G("cold-forge") + ".station")) != ForgeWorkshop && Workshop(game) == ForgeWorkshop, "and so is its workshop");
        var rows = Content.ItemData.Get(G("cold-forge"))!;
        Check((uint)rows.Get("uiValues[0].locaId").Number == 0xf2b8d8e0 && rows.Get("uiValues[0].value").Number == 2, $"the tooltip's Comfort row: {rows.Get("uiValues[0].value").Number}");
        Check(T(G("old-table") + ".prop").Get("ComfortProvider.comfort").Number == 3 && T(G("old-table") + ".prop").Get("ComfortProvider.category").Name == "Table", "a prop's comfort on a starter that has some: 3, Table");
        Check(T(G("lucky-banner") + ".prop").Get("ComfortProvider.comfort").Number == 2, "and on one that has none: 2");

        // The game's own comfort system, on an actor in the base. NetworkComfort holds the hearth it counts (+0) and the level (+4): furniture counts beside a lit hearth, and a station
        // is lit while it runs. A game oven (Hearth 3) is the control, our forge (Hearth 2, a component its starter lacks) the test, the spa (Wellness 5) on top of it.
        var spot = Near(at, 12);
        var actor = await Spawn.Entity("1_Player", new WorldPosition(spot.X, spot.Y + 0.5, spot.Z), 0, new SpawnOptions { Tag = "stations-test", Flags = SpawnFlags.Transient });
        _placed.Add(actor);
        async Task<(uint Hearth, int Level)> Measure()
        {
            await Steps(24 * 6);
            var nc = World.ReadBytes(actor, "NetworkComfort");
            return nc is { Length: 8 } ? (BitConverter.ToUInt32(nc, 0), BitConverter.ToUInt16(nc, 4)) : (0, -1);
        }
        async Task<EntityId> Lit(string template, ContentEntry recipe, double dx)
        {
            var e = await Place(template, new WorldPosition(spot.X + dx, spot.Y, spot.Z));
            foreach (var i in recipe.Inputs)
                if (i.Item.Value != 0) await Items.Give(e, new ItemId(i.Item.Value), i.Count * 20);
            Stations.Start(e, recipe.Id);
            return e;
        }
        var alone = await Measure();
        var ovenRecipe = Content.Recipes.First(r => (uint)r.Get("workshopId").Integer == 0x1ee9f0bf && r.Inputs.All(i => i.Item.Value != 0));
        var oven = await Lit("T3_Oven_Factory", ovenRecipe, 1.5);
        var withOven = await Measure();
        Info($"comfort: alone {alone}, a running game oven {oven} ({Stations.Status(oven)?.State}) {withOven}");
        Build.RemoveProp(oven, InBases);
        var forge = await Lit(G("cold-forge") + ".station", Content.Recipes.Get(G("cold-forge.makes.Material_T1_Metal_Sheets"))!, 1.5);
        var withForge = await Measure();
        var spa = await Place("stations-test.spa.prop", new WorldPosition(spot.X - 1.5, spot.Y, spot.Z));
        var withSpa = await Measure();
        Build.RemoveProp(spa, InBases);
        var table = Build.PlaceProp("Prop_Furniture_Comfort1_Table_A_Wood", new WorldPosition(spot.X - 1.5, spot.Y, spot.Z), null, 0, InBases);
        _placed.Add(table);
        var withTable = await Measure();
        Info($"comfort: our running forge {forge} ({Stations.Status(forge)?.State}) {withForge}, the spa {spa} {withSpa}, the game's table {table} {withTable}");
        if (withOven.Hearth == oven.Value)
        {
            Check(withForge.Hearth == forge.Value && withForge.Level == withOven.Level - 1, $"the game's comfort system takes our forge for its hearth, as it does the game's oven, at its comfort: hearth {withForge.Hearth}, level {withForge.Level} (the oven's {withOven.Level})");
            if (withTable.Level > withForge.Level) Check(withSpa.Level > withForge.Level, $"and counts the spa beside it, as it does the game's table: level {withForge.Level} -> {withSpa.Level} (the table: {withTable.Level})");
            else Info("furniture beside a hearth counted for neither table here: furniture is a manual check");
        }
        else Info("the game's oven was no hearth for this actor either: comfort is a manual check");
        Remove();
        await Steps(4);
    }

    private async Task Props(WorldPosition at)
    {
        var table = await Place(G("old-table") + ".prop", Near(at, 4));
        Check(Build.Prop(table)?.TemplateName == G("old-table") + ".prop" && Build.Prop(table)?.ItemId == I("old-table").Id, $"a prop places, and dismantling gives back its item: {Build.Prop(table)?.TemplateName}");
        foreach (var name in new[] { "wall-model", "ceiling-model", "floor-model" })
        {
            var e = await Place($"stations-test.{name}.prop", Near(at, 8));
            Check(!e.IsNone && Spawn.Inspect(e)?.Template == T($"stations-test.{name}.prop").Guid, $"{name}: places on its surface's starter: {Spawn.Inspect(e)?.TemplateName}");
        }
        if (Content.Templates.Get("stations-test.cooked-prop.prop") != null)
        {
            var e = await Place("stations-test.cooked-prop.prop", Near(at, 10));
            Check(World.Exists(e), $"a prop with a cooked model places: {e}");
        }
        Remove();
        await Steps(4);
    }

    // ---- restarts -----------------------------------------------------------------------------------------------

    private async Task LeaveRunning(WorldPosition at, string file)
    {
        var rack = await Place(G("herb-rack") + ".station", Near(at, 0));
        var rackRecipe = Content.Recipes.Get(G("herb-rack.makes.Material_T1_Wood_Logs"))!;
        await Items.Give(rack, Twigs, 100);
        Stations.Start(rack, rackRecipe.Id);
        var forge = await Place(G("cold-forge") + ".station", Near(at, 2));
        var forgeRecipe = Content.Recipes.Get(G("cold-forge.makes.Material_T1_Metal_Sheets"))!;
        await Items.Give(forge, Scraps, 20);
        await Items.Give(forge, Charcoal, 40);
        Stations.Start(forge, forgeRecipe.Id);
        var table = await Place(G("old-table") + ".prop", Near(at, 4));
        await Steps(48);
        Check(Stations.Status(rack) is { State: StationState.Running } && Stations.Status(forge) is { State: StationState.Running }, $"left running in the base: {Show(rack)}; {Show(forge)}");
        // what the world's save takes: in a base or not, with what dismantling gives back or not
        var open = await OpenGround();
        var cases = new JsonObject();
        foreach (var (name, spot, item, options) in new[]
        {
            ("base, with item", Near(at, 6), 0u, InBases),
            ("base, no item", Near(at, 8), uint.MaxValue, InBases),
            ("open, with item", open, 0u, new BuildOptions()),
            ("open, no item", new WorldPosition(open.X + 4, open.Y, open.Z), uint.MaxValue, new BuildOptions()),
        })
        {
            var e = Build.PlaceProp(G("herb-rack") + ".station", spot, null, item, options);
            cases[name] = new JsonArray(spot.X, spot.Y, spot.Z);
            Info($"placed for the save question, {name}: {e}, UsedItem {(World.ReadBytes(e, "UsedItem") is { } u ? BitConverter.ToUInt32(u, 0).ToString("x8") : "none")}");
        }
        await Steps(24);
        var state = new JsonObject
        {
            ["rack"] = rack.Value, ["forge"] = forge.Value, ["table"] = table.Value, ["cases"] = cases,
            ["rackAt"] = new JsonArray(World.Position(rack)!.Value.X, World.Position(rack)!.Value.Y, World.Position(rack)!.Value.Z),
            ["forgeAt"] = new JsonArray(World.Position(forge)!.Value.X, World.Position(forge)!.Value.Y, World.Position(forge)!.Value.Z),
            ["tableAt"] = new JsonArray(World.Position(table)!.Value.X, World.Position(table)!.Value.Y, World.Position(table)!.Value.Z),
            ["itemRack"] = I("herb-rack").Guid.ToString(), ["itemForge"] = I("cold-forge").Guid.ToString(), ["templateRack"] = T(G("herb-rack") + ".station").Guid.ToString(), ["templateForge"] = T(G("cold-forge") + ".station").Guid.ToString(),
            ["recipe"] = rackRecipe.Guid.ToString(), ["workshop"] = Workshop(T(G("herb-rack") + ".station")),
            ["twigs"] = Count(rack, Twigs),
        };
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(file, state.ToJsonString());
        var saved = await Saves.Request();
        Check(saved.Success, $"saved: {saved}");
        _placed.Clear();    // they stay for the second run
        Info("restart the lab for the second phase");
    }

    private static EntityId At(JsonNode? where, Guid template)
    {
        var (x, y, z) = ((double)where![0]!, (double)where[1]!, (double)where[2]!);
        foreach (var e in World.Find("CurrentTransform"))
            if (Spawn.Inspect(e) is { Template: { } t } info && t == template && Math.Abs(info.Position.X - x) < 0.25 && Math.Abs(info.Position.Y - y) < 0.25 && Math.Abs(info.Position.Z - z) < 0.25)
                return e;
        return EntityId.None;
    }

    private async Task AfterRestart(string file)
    {
        var s = JsonNode.Parse(File.ReadAllText(file))!;
        Check(I("herb-rack").Guid.ToString() == (string?)s["itemRack"] && I("cold-forge").Guid.ToString() == (string?)s["itemForge"] && T(G("herb-rack") + ".station").Guid.ToString() == (string?)s["templateRack"], "the ids are what they were");
        Check(Content.Recipes.Get(G("herb-rack.makes.Material_T1_Wood_Logs"))?.Guid.ToString() == (string?)s["recipe"] && Workshop(T(G("herb-rack") + ".station")) == (uint)s["workshop"]!, "the recipe and its workshop too");
        var template = T(G("herb-rack") + ".station").Guid;
        var rack = At(s["rackAt"], template);
        var forge = At(s["forgeAt"], T(G("cold-forge") + ".station").Guid);
        var table = At(s["tableAt"], T(G("old-table") + ".prop").Guid);
        Check(!rack.IsNone, $"the rack came back: {rack}");
        Check(!forge.IsNone, $"the forge came back: {forge}");
        Check(!table.IsNone, $"the table came back: {table}");
        var recipe = Content.Recipes.Get(G("herb-rack.makes.Material_T1_Wood_Logs"))!;
        var was = (int)s["twigs"]!;
        var before = Count(rack, Twigs);
        Check(Stations.Status(rack) is { State: StationState.Running, Running: var r } && r == recipe.Id, $"the rack still runs its recipe: {Show(rack)}");
        Check(before <= was && before > 0 && Count(rack, Logs) > 0, $"with what it had: twigs {was} -> {before}, logs {Count(rack, Logs)}");
        await Steps(24 * 9);
        var after = Count(rack, Twigs);
        Check(after < before, $"and goes on: twigs {before} -> {after}: {Show(rack)}");
        Check(Stations.Status(forge) is { State: StationState.Running } && Count(forge, Sheets) > 0, $"the forge too, burning what it had: {Show(forge)}");
        Check(Items.Read(rack)?.AvailableSlots == 12, $"and keeps its slots: {Items.Read(rack)?.AvailableSlots}");
        foreach (var (name, where) in s["cases"]!.AsObject())
            Info($"the save question: {name}: {(At(where, template).IsNone ? "gone" : "came back")}");
        // clean up after ourselves, so the test can run again in this world
        foreach (var e in World.Find("FactoryStation"))
            if (Spawn.Inspect(e) is { Template: { } t } && (t == template || t == T(G("cold-forge") + ".station").Guid)) Build.RemoveProp(e, InBases);
        if (!table.IsNone) Build.RemoveProp(table, InBases);
        await Steps(4);
        var saved = await Saves.Request();
        Check(saved.Success, $"saved without them: {saved}");
    }
}
