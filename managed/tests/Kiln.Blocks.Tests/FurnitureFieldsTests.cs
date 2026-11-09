// SPDX-License-Identifier: MIT
// What a container's default loot and lid, a door's swing and sides, several colliders and crafting props lower to, with no game: the clone's fields and what it adds or does without,
// what is refused by block and field, and how the runtime names a container. The placed things, the loot given and the restart are the lab's (mods/tests/furniture-test).
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class FurnitureFieldsTests : IDisposable
{
    private static ModBlocks Mod(string id) => new(id, 1) { ManifestClients = "mod" };

    private static JsonObject Lower<T>(T b, string name = "thing", ModBlocks? mod = null) where T : class, IBlock => Compiler.Lower(mod ?? Mod("furniture-fields"), name, b).Low.Json;

    private static JsonObject Template(JsonObject j) => Assert.Single(j["templates"]!.AsArray())!.AsObject();
    private static JsonObject Fields(JsonObject j) => Template(j)["fields"]!.AsObject();
    private static double D(JsonNode? n) => double.Parse(n!.ToJsonString(), CultureInfo.InvariantCulture);

    // a handle for a block of the mod, made as Define would, without handing anything to the game
    private static BlockHandle Add<T>(ModBlocks mod, string name, T block) where T : class, IBlock
    {
        var handle = Compiler.Lower(mod, name, block).Handle;
        mod.Handles[name] = handle;
        return handle;
    }

    private static string?[] Names(JsonNode? list) => list is JsonArray a ? [.. a.Select(x => (string?)x)] : [];

    public void Dispose()
    {
        WorldTablesNative.TestDefine = null;
        WorldTablesNative.TestCraftingPropIndex = null;
    }

    // ---- default loot ----

    private static readonly LootTableBlock Scrap = new() { Entries = [new LootEntry { Item = "Material_T1_Metal_Scraps", CountMin = 2, CountMax = 5 }] };

    [Fact]
    public void A_default_loot_written_in_place_is_in_what_the_container_became()
    {
        var f = Lower(new ContainerBlock { Name = "Chest", DefaultLoot = Scrap })["furniture"]!;
        Assert.Equal("Material_T1_Metal_Scraps", (string?)f["defaultLoot"]!["entries"]![0]!["item"]);
        Assert.Equal(5, (int)f["defaultLoot"]!["entries"]![0]!["countMax"]!);
    }

    [Fact]
    public void A_default_loot_names_a_loot_table_block_of_the_mod()
    {
        var mod = Mod("furniture-loot-name");
        Add(mod, "scrap-pile", Scrap);
        var f = Lower(new ContainerBlock { Name = "Chest", DefaultLoot = "scrap-pile" }, mod: mod)["furniture"]!;
        Assert.Equal("Material_T1_Metal_Scraps", (string?)f["defaultLoot"]!["entries"]![0]!["item"]);
    }

    [Fact]
    public void A_default_loot_that_is_no_table_is_refused_by_field()
    {
        var mod = Mod("furniture-loot-bad");
        var e = Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "Chest", DefaultLoot = new LootTableBlock { Entries = [] } }, "bad", mod));
        Assert.Equal("defaultLoot.entries", e.Field);
        Assert.Contains("bad", e.Message, StringComparison.Ordinal);
        var gone = Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "Chest", DefaultLoot = "no-such-table" }, "gone", mod));
        Assert.Equal("defaultLoot", gone.Field);
    }

    [Fact]
    public void Containers_are_named_by_template_and_spot_in_sixteenths_of_a_metre()
    {
        var t = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var a = ContainerRuntime.Spot(t, new WorldPosition(10.0, 20.0, 30.0));
        Assert.Equal(a, ContainerRuntime.Spot(t, new WorldPosition(10.01, 20.0, 29.99)));     // a position read back from a save is the same spot
        Assert.NotEqual(a, ContainerRuntime.Spot(t, new WorldPosition(10.5, 20.0, 30.0)));
        Assert.NotEqual(a, ContainerRuntime.Spot(Guid.Parse("11111111-2222-3333-4444-555555555556"), new WorldPosition(10.0, 20.0, 30.0)));
    }

    private static ItemsResult Given(ItemsStatus status, int requested = 2, int done = 2) =>
        new(status, EntityId.None, ItemId.None, requested, done, [], "", 0, 1, false);

    [Fact]
    public async Task A_delayed_inventory_result_never_submits_the_next_stack_from_a_continuation()
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2), new("twigs", 2)] };
        book.Pending["spot"] = progress;
        var first = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ItemsResult>? transaction = null;
        var thread = Environment.CurrentManagedThreadId;
        var submitted = new List<string>();
        Task<ItemsResult> Give(LootStack stack)
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            submitted.Add(stack.Item);
            return first.Task;
        }
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, Give, true, out _));
        await Task.Run(() => first.SetResult(Given(ItemsStatus.Done)), TestContext.Current.CancellationToken);
        // Completing a transaction on another thread changes neither the journal nor the next native request.
        Assert.Equal(0, progress.Next);
        Assert.Equal(["scraps"], submitted);
        // The next call stands for the next game tick; its own thread is the one native calls must use.
        thread = Environment.CurrentManagedThreadId;
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, Give, true, out _));
        Assert.Equal(["scraps", "twigs"], submitted);
        Assert.Equal(1, progress.Next);
    }

    [Fact]
    public void A_save_records_completed_stacks_without_submitting_and_a_restart_resumes_only_the_remainder()
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2), new("twigs", 2)] };
        book.Pending["spot"] = progress;
        var completion = new TaskCompletionSource<ItemsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ItemsResult>? transaction = completion.Task;
        // The final shutdown snapshot can come after the host stopped service ticks. WorldData's serialization callback must harvest that result itself.
        book.BeforeSnapshot = () => Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("a snapshot must submit no request"), false, out _));
        Assert.Equal(0, JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!.Pending["spot"].Next);
        completion.SetResult(Given(ItemsStatus.Done)); // arrives after the earlier snapshot/announcement, with no intervening service tick
        var restored = JsonSerializer.Deserialize<FilledContainers>(JsonSerializer.Serialize(book))!;
        var roll = restored.Pending["spot"];
        Assert.Equal(1, roll.Next);
        var given = new List<string>();
        Assert.False(ContainerRuntime.Advance(restored, "spot", roll, ref transaction, stack => { given.Add(stack.Item); return Task.FromResult(Given(ItemsStatus.Done)); }, true, out _));
        Assert.True(ContainerRuntime.Advance(restored, "spot", roll, ref transaction, _ => throw new Exception("all stacks finished"), false, out _));
        Assert.Equal(["twigs"], given);
        Assert.Contains("spot", restored.Filled.Keys);
        Assert.Empty(restored.Pending);
    }

    [Theory]
    [InlineData(ItemsStatus.Unavailable)]
    [InlineData(ItemsStatus.NoInventory)]
    [InlineData(ItemsStatus.Refused)]
    public void A_temporary_failure_keeps_the_same_rolled_stack_for_retry(ItemsStatus status)
    {
        var book = new FilledContainers();
        var progress = new FilledContainers.FillProgress { Stacks = [new("scraps", 2)] };
        book.Pending["spot"] = progress;
        Task<ItemsResult>? transaction = Task.FromResult(Given(status, done: 0));
        Assert.False(ContainerRuntime.Advance(book, "spot", progress, ref transaction, _ => throw new Exception("retry is later"), true, out var retry));
        Assert.NotNull(retry);
        Assert.Equal(0, progress.Next);
        Assert.Empty(book.Filled);
        Assert.Same(progress, book.Pending["spot"]);
    }

    // ---- the lid ----

    [Fact]
    public void A_lid_that_does_not_move_takes_the_toggle_animation_off()
    {
        var j = Lower(new ContainerBlock { Name = "Barrel", Lid = new LidBlock { Moves = false } });
        Assert.Collection(Names(Template(j)["removeComponents"]), n => Assert.Equal("ToggleAnimation", n));
        Assert.False((bool)j["furniture"]!["lid"]!["moves"]!);
    }

    [Fact]
    public void A_lid_with_a_graph_and_states_sets_the_toggle_animation_on_the_clone()
    {
        var graph = Guid.Parse("be434836-4c77-4c86-bee0-a49bc5992800");
        var open = Guid.Parse("11111111-0000-4000-8000-000000000001");
        var j = Lower(new ContainerBlock { Name = "Chest", Lid = new LidBlock { Graph = graph, OpenState = open } });
        var f = Fields(j);
        Assert.Equal(graph.ToString(), (string?)f["AnimationGraphResourceComponent2.animationGraph"]);
        Assert.Equal(open.ToString(), (string?)f["ToggleAnimation.animationStateOn"]);
        Assert.Equal(LidBlock.Shut.ToString(), (string?)f["ToggleAnimation.animationStateOff"]);       // a state left out is the game's
        Assert.Contains("ToggleAnimation", Names(Template(j)["addComponents"]));
    }

    [Fact]
    public void A_lid_that_says_nothing_leaves_the_starters_alone()
    {
        var j = Lower(new ContainerBlock { Name = "Chest", Lid = new LidBlock() });
        Assert.DoesNotContain("ToggleAnimation.animationStateOn", Fields(j).Select(x => x.Key));
        Assert.Null(Template(j)["removeComponents"]);
    }

    [Fact]
    public void A_lid_that_makes_no_sense_is_refused_by_field()
    {
        Assert.Equal("lid", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Lid = new LidBlock { Moves = false, Graph = Guid.NewGuid() } })).Field);
        Assert.Equal("lid.openState", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Lid = new LidBlock { OpenState = Guid.Empty } })).Field);
    }

    // ---- the door ----

    [Theory]
    [InlineData(DoorSide.Both, "bcf39dc8-d81e-450a-a77d-3b626d4106c4", "03deda2f-3d10-4127-96bb-12d438533e4f")]
    [InlineData(DoorSide.Front, "bcf39dc8-d81e-450a-a77d-3b626d4106c4", "bcf39dc8-d81e-450a-a77d-3b626d4106c4")]
    [InlineData(DoorSide.Back, "03deda2f-3d10-4127-96bb-12d438533e4f", "03deda2f-3d10-4127-96bb-12d438533e4f")]
    public void A_door_swings_the_way_it_says(DoorSide opens, string front, string back)
    {
        var f = Fields(Lower(new DoorBlock { Name = "Door", Opens = opens }));
        Assert.Equal(front, (string?)f["DirectionalInteraction.frontInteraction.animationState"]);
        Assert.Equal(back, (string?)f["DirectionalInteraction.backInteraction.animationState"]);
    }

    [Fact]
    public void A_door_that_says_nothing_of_its_swing_keeps_the_starters()
    {
        var f = Fields(Lower(new DoorBlock { Name = "Door", OnInteract = new InteractBlock { Verb = InteractVerb.Use } }));
        Assert.DoesNotContain("DirectionalInteraction.frontInteraction.animationState", f.Select(x => x.Key));
    }

    [Fact]
    public void A_doors_sides_are_in_what_it_became_and_wrong_values_are_refused()
    {
        var j = Lower(new DoorBlock { Name = "Door", Sides = DoorSide.Front, Opens = DoorSide.Back })["furniture"]!;
        Assert.Equal("front", (string?)j["sides"]);
        Assert.Equal("back", (string?)j["opens"]);
        Assert.Equal("sides", Assert.Throws<BlockError>(() => Lower(new DoorBlock { Name = "x", Sides = (DoorSide)9 })).Field);
        Assert.Equal("opens", Assert.Throws<BlockError>(() => Lower(new DoorBlock { Name = "x", Opens = (DoorSide)9 })).Field);
    }

    [Fact]
    public void The_side_of_a_door_follows_the_doors_turn()
    {
        // the side a guest is on is decided by the door's yaw: a door turned half a circle swaps front and back (Spawn.Inspect needs a game, so the geometry is checked here)
        static DoorSide Side(double yawDeg, double gx, double gz)
        {
            var yaw = yawDeg * Math.PI / 180;
            return gx * Math.Sin(yaw) + gz * Math.Cos(yaw) >= 0 ? DoorSide.Front : DoorSide.Back;
        }
        Assert.Equal(DoorSide.Front, Side(0, 0, 1));
        Assert.Equal(DoorSide.Back, Side(0, 0, -1));
        Assert.Equal(DoorSide.Back, Side(180, 0, 1));
        Assert.Equal(DoorSide.Front, Side(90, 1, 0));
    }

    // ---- several colliders ----

    private static PropBlock Table(params BoxCollider[] boxes) => new() { Name = "Table", Visual = new VisualBlock { Model = Guid.Empty.ToString() }, Colliders = boxes };

    [Fact]
    public void Two_boxes_are_two_shapes_of_the_templates_collider_and_the_template_grows_its_list()
    {
        var j = Lower(Table(new BoxCollider { Size = new Vector3(2, 0.2f, 1), Center = new Vector3(0, 1, 0) }, new BoxCollider { Size = new Vector3(0.2f, 1, 0.2f), Center = new Vector3(0.8f, 0.5f, 0.6f) }));
        var f = Fields(j);
        const string g = "ColliderResourceComponent.colliders[0].dataArray";
        Assert.Equal(1.0, (double)f[$"{g}[0].shape.halfSize.x"]!, 5);
        Assert.Equal(0.1, (double)f[$"{g}[1].shape.halfSize.x"]!, 5);
        Assert.Equal(0.8, (double)f[$"{g}[1].offset.x"]!, 5);
        Assert.Equal(2, (int)Template(j)["growArrays"]![g]!);
        Assert.Equal(2, (int)Template(j)["growArrays"]!["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList"]!);
        Assert.Equal(0, (int)f["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList[0]"]!);
        Assert.Equal(1, (int)f["ColliderResourceComponent.colliders[0].joltObjects[0].shapeIndexList[1]"]!);
        // one hash for the compound shape, and a different one for the cursor's bounding box
        var hash = (double)f["ColliderResourceComponent.colliders[0].joltObjects[0].hash"]!;
        Assert.NotEqual(hash, (double)j["itemDataRules"]!["equipment.placementColliders[0].joltObjects[0].hash"]!);
        Assert.Equal(2, j["collider"]!["boxes"]!.AsArray().Count);
        Assert.Equal(1.0, D(j["collider"]!["half"]![0]), 5);
        Assert.Equal(0.6, D(j["collider"]!["half"]![2]), 5);       // z from -0.5 to 0.7
    }

    [Fact]
    public void One_box_in_a_list_makes_no_longer_list_and_the_hash_follows_the_geometry()
    {
        var one = Lower(Table(new BoxCollider { Size = Vector3.One }));
        Assert.Null(Template(one)["growArrays"]);
        var a = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(2, 1, 1) }));
        var b = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(3, 1, 1) }));
        const string h = "ColliderResourceComponent.colliders[0].joltObjects[0].hash";
        Assert.NotEqual((double)Fields(a)[h]!, (double)Fields(b)[h]!);
        var twin = Lower(Table(new BoxCollider { Size = Vector3.One }, new BoxCollider { Size = new Vector3(2, 1, 1) }), "twin");
        Assert.NotEqual((double)Fields(a)[h]!, (double)Fields(twin)[h]!); // identical geometry still belongs to a different template
    }

    [Theory]
    [InlineData("both", "colliders")]
    [InlineData("none", "colliders")]
    [InlineData("many", "colliders")]
    [InlineData("size", "colliders[1].size")]
    [InlineData("far", "colliders[0].size")]
    [InlineData("center", "colliders[0].center")]
    [InlineData("ceiling", "colliders")]
    [InlineData("together", "colliders")]
    public void Wrong_colliders_are_refused_by_field(string what, string field)
    {
        var box = new BoxCollider { Size = Vector3.One };
        PropBlock b = what switch
        {
            "both" => Table(box) with { Collider = box },
            "none" => Table(),
            "many" => Table([.. Enumerable.Repeat(box, 17)]),
            "size" => Table(box, new BoxCollider { Size = new Vector3(1, 0, 1) }),
            "far" => Table(new BoxCollider { Size = new Vector3(40, 1, 1) }),
            "center" => Table(new BoxCollider { Size = Vector3.One, Center = new Vector3(float.NaN, 0, 0) }),
            "ceiling" => Table(box, box) with { Placement = new PlacementBlock { On = Surface.Ceiling } },
            "together" => Table(new BoxCollider { Size = new Vector3(30, 1, 1), Center = new Vector3(-10, 0, 0) }, new BoxCollider { Size = new Vector3(30, 1, 1), Center = new Vector3(10, 0, 0) }),
            _ => throw new ArgumentException(what),
        };
        var e = Assert.Throws<BlockError>(() => Lower(b, "bad"));
        Assert.Equal(field, e.Field);
    }

    [Fact]
    public void Colliders_of_a_station_or_a_game_items_look_are_refused_as_a_collider_is()
    {
        var box = new BoxCollider { Size = Vector3.One };
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "x", Colliders = [box] })).Field);
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new ContainerBlock { Name = "x", Colliders = [box] })).Field);
        Assert.Equal("colliders", Assert.Throws<BlockError>(() => Lower(new PropBlock { Name = "x", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Colliders = [box] })).Field);
    }

    // ---- crafting props ----

    private static string Report(string name, string status, int index = -1) =>
        new JsonObject { ["blocks"] = new JsonArray(new JsonObject { ["name"] = name, ["kind"] = "craftingProp", ["status"] = status, ["index"] = index }) }.ToJsonString();

    [Fact]
    public void A_prop_in_the_table_is_a_crafting_prop_by_the_id_of_its_guid()
    {
        var mod = Mod("furniture-provides");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        var j = Lower(new StationBlock { Name = "Cold Forge", Provides = true }, "cold-forge", mod);
        var guid = Compiler.CraftingPropGuid("furniture-provides", "cold-forge");
        Assert.Equal(BlockIds.Fnv(guid), (uint)Fields(j)["RequiredCraftingProp.craftingPropId"]!);
        Assert.Contains("RequiredCraftingProp", Names(Template(j)["addComponents"]));
        Assert.Equal(guid.ToString(), (string?)j["provides"]!["guid"]);
        Assert.Equal(51, (int)j["provides"]!["index"]!);
    }

    [Fact]
    public void A_prop_the_table_does_not_have_yet_is_made_from_the_next_start()
    {
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "pending");
        var j = Lower(new StationBlock { Name = "Cold Forge", Provides = true }, "cold-forge", Mod("furniture-pending"));
        Assert.Null(j["templates"]);       // no template, no item: nothing the game could place with an id it can't read
        Assert.Equal("pending", (string?)j["provides"]!["status"]);
    }

    [Fact]
    public void A_service_that_keeps_no_crafting_props_makes_nothing_and_says_so()
    {
        var j = Lower(new PropBlock { Name = "Lamp", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" }, Provides = true }, "lamp", Mod("furniture-off"));
        Assert.Null(j["templates"]);
        Assert.Equal("off", (string?)j["provides"]!["status"]);
    }

    [Fact]
    public void A_crafting_prop_asks_for_clients_with_the_mod()
    {
        var mod = Mod("furniture-clients");
        mod.ManifestClients = "kiln";
        var e = Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "x", Provides = true }, "x", mod));
        Assert.Equal("provides", e.Field);
    }

    [Fact]
    public void The_table_is_told_the_blocks_key_item_and_name()
    {
        string? sent = null;
        WorldTablesNative.TestDefine = d => { sent = d; return Report("cold-forge", "registered", 51); };
        Lower(new StationBlock { Name = "Cold Forge", Provides = true, Was = "old-forge" }, "cold-forge", Mod("furniture-sent"));
        var doc = JsonNode.Parse(sent!)!;
        var block = doc["craftingProps"]!["cold-forge"]!;
        Assert.Equal("old-forge", (string?)block["key"]);                                         // ids follow from the key, so a rename keeps them
        Assert.Equal(BlockIds.Derive("furniture-sent", "item", "old-forge").ToString(), (string?)block["item"]);
        Assert.Equal("Cold Forge", (string?)block["name"]);
    }

    [Fact]
    public void A_recipe_that_needs_a_prop_carries_its_id_and_place()
    {
        var mod = Mod("furniture-needs");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = g => g == Compiler.CraftingPropGuid("furniture-needs", "cold-forge") ? 51 : -1;
        var forge = Add(mod, "cold-forge", new StationBlock { Name = "Cold Forge", Provides = true });
        var j = Lower(new ItemBlock { Name = "Frost Ingot", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 2 }, NeedsProps = [forge] } }, "frost-ingot", mod);
        var need = j["needsProps"]![0]!;
        Assert.Equal($"0x{BlockIds.Fnv(Compiler.CraftingPropGuid("furniture-needs", "cold-forge")):x8}", (string?)need["id"]);
        Assert.Equal(51, (int)need["index"]!);
        Assert.NotNull(j["recipes"]);
    }

    [Fact]
    public void An_automated_station_refuses_nearby_props_before_a_recipe_can_be_made()
    {
        var recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 }, Seconds = 3, Output = "Material_T1_Wood_Logs", NeedsProps = ["table"] };
        var error = Assert.Throws<BlockError>(() => Lower(new StationBlock { Name = "Forge", Kind = StationKind.Forge, Recipes = [recipe] }));
        Assert.Equal("recipes[0].needsProps", error.Field);
        Assert.Contains("manual station", error.Message, StringComparison.Ordinal);
        var item = Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Logs", Recipe = recipe with { Output = null, Station = StationKind.Forge.Workshop } }));
        Assert.Equal("recipe.needsProps", item.Field);
        var mod = Mod("factory-prop-recipe");
        var forge = Add(mod, "forge", new StationBlock { Name = "Forge", Kind = StationKind.Forge });
        Assert.Equal("recipe.needsProps", Assert.Throws<BlockError>(() => Lower(new ItemBlock { Name = "Logs", Recipe = recipe with { Output = null, Station = forge.ToString() } }, mod: mod)).Field);
    }

    [Fact]
    public void A_manual_workbench_recipe_accepts_the_props_mask()
    {
        var mod = Mod("manual-prop-recipe");
        WorldTablesNative.TestDefine = _ => Report("table", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = _ => 51;
        var table = Add(mod, "table", new PropBlock { Name = "Table", Visual = new VisualBlock { Like = "Prop_Decoration_Comfort1_T0_Table_Medium" }, Provides = true });
        var j = Lower(new StationBlock { Name = "Bench", Kind = StationKind.Workbench, Recipes = [new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 }, Output = "Material_T1_Wood_Logs", NeedsProps = [table] }] }, mod: mod);
        Assert.Equal(51, (int)j["needsProps"]![0]!["index"]!);
        Assert.Single(j["recipes"]!.AsArray());
        Assert.Equal(51, (int)j["recipes"]![0]!["props"]![0]!["index"]!);
    }

    [Fact]
    public void A_recipe_that_needs_a_prop_the_table_does_not_have_waits_for_it()
    {
        var mod = Mod("furniture-needs-wait");
        WorldTablesNative.TestDefine = _ => Report("cold-forge", "registered", 51);
        WorldTablesNative.TestCraftingPropIndex = _ => -1;
        Add(mod, "cold-forge", new StationBlock { Name = "Cold Forge", Provides = true });
        var j = Lower(new ItemBlock { Name = "Frost Ingot", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 2 }, NeedsProps = ["cold-forge"] } }, "frost-ingot", mod);
        Assert.Null(j["recipes"]);
        Assert.Contains("no place", (string?)j["recipeWaits"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_recipe_that_needs_what_is_no_crafting_prop_is_refused_by_field()
    {
        var mod = Mod("furniture-needs-bad");
        Add(mod, "plain-lamp", new PropBlock { Name = "Lamp", Visual = new VisualBlock { Like = "Prop_Decoration_T2_Vase_Clay_01" } });
        Add(mod, "scrap-pile", Scrap);
        ItemBlock Item(PropRef p) => new() { Name = "Thing", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 1 }, NeedsProps = [p] } };
        var plain = Assert.Throws<BlockError>(() => Lower(Item("plain-lamp"), "a", mod));
        Assert.Equal("recipe.needsProps[0]", plain.Field);
        Assert.Contains("provides", plain.Message, StringComparison.Ordinal);
        Assert.Equal("recipe.needsProps[0]", Assert.Throws<BlockError>(() => Lower(Item("scrap-pile"), "b", mod)).Field);
        Assert.Equal("recipe.needsProps[0]", Assert.Throws<BlockError>(() => Lower(Item("nothing-here"), "c", mod)).Field);
        var many = new ItemBlock { Name = "Thing", Recipe = new RecipeBlock { Needs = { ["Material_T1_Metal_Scraps"] = 1 }, NeedsProps = [.. Enumerable.Repeat((PropRef)"plain-lamp", 9)] } };
        Assert.Equal("recipe.needsProps", Assert.Throws<BlockError>(() => Lower(many, "d", mod)).Field);
    }
}
