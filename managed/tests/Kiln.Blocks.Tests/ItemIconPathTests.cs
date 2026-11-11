// SPDX-License-Identifier: MIT
// An item's PNG icon (a look's, a worn look's, a prop's, a material's block item's) reaches kiln.content as the file in the mod's folder, whichever way the
// block came. A data file's blocks compile on a tick of Kiln's own, with no mod code running: kiln.content's managed side then had no mod to read a relative
// path against, and kiln.assets opened it against <game>/kiln. The fake kiln.content table below keeps what Kiln.Content hands the native side.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

// The mod list, kiln.content's table and kiln.worldtables' answer are process-wide: nothing else runs meanwhile.
[CollectionDefinition("item icons", DisableParallelization = true)]
public sealed class ItemIconCollection;

[Collection("item icons")]
public sealed unsafe class ItemIconPathTests : IDisposable
{
    private const string Id = "icon-path-test", Other = "icon-path-other";
    private const string Model = "19796b32-33cb-507e-827a-b2c23f720126";
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAXpeqz8AAAAASUVORK5CYII=");

    // what kiln.content's native side was given: one item definition per call
    private static readonly List<JsonObject> Sent = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Define(ulong owner, uint kind, NativeStr json, byte* report, nuint cap, nuint* len)
    {
        if (kind == (uint)ContentKind.Items) Sent.Add(JsonNode.Parse(json.ToString())!.AsObject());
        if (cap > 0) report[0] = 0;
        *len = 0;
        return ContentNative.Ok;
    }

    private sealed class FakeMods(params ModEntry[] mods) : IModsBackend
    {
        public IReadOnlyList<ModEntry> All => mods;
        public ModSide Side => ModSide.Server;
        public ModEntry? Get(string id) => mods.FirstOrDefault(m => m.Id == id);
        public ModStats Stats(string id) => default;
        public bool Send(ModContext? sender, string targetId, string method, object? payload) => false;
        public Task<ModOperationResult> Request(ModOperation op, string arg) => throw new NotSupportedException();
        public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) => throw new NotSupportedException();
    }

    private readonly string _root, _dir, _other;
    private readonly IModsBackend? _mods;
    private readonly ModContext? _caller;
    private readonly FieldInfo _tableField = typeof(Content).GetField("_table", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _table;
    private readonly ContentNative.Table* _fake;

    public ItemIconPathTests()
    {
        _root = Directory.CreateTempSubdirectory("kiln-item-icon-").FullName;
        _dir = Path.Combine(_root, "mods", Id);
        _other = Path.Combine(_root, "mods", Other);
        Directory.CreateDirectory(Path.Combine(_dir, "icons"));
        Directory.CreateDirectory(Path.Combine(_dir, "blocks"));
        Directory.CreateDirectory(Path.Combine(_other, "icons"));
        foreach (var n in new[] { "cinderbrand", "gem", "helm", "hood", "crop", "trim", "stool", "brick", "loam" })
            File.WriteAllBytes(Path.Combine(_dir, "icons", n + ".png"), Png);
        File.WriteAllBytes(Path.Combine(_other, "icons", "cape.png"), Png);
        File.WriteAllBytes(Path.Combine(_root, "outside.png"), Png);
        _mods = ModsBackend.Current;
        ModsBackend.Current = new FakeMods(
            new ModEntry(Id, Id, "1.0.0", _dir, ModSide.Both, ModState.Loaded, null, [], 0, null),
            new ModEntry(Other, Other, "1.0.0", _other, ModSide.Both, ModState.Loaded, null, [], 1, null));
        _caller = HostAccess.Current;
        HostAccess.Current = null;   // a data file's blocks compile on Kiln's tick: no mod's code is running
        _table = _tableField.GetValue(null);
        _fake = (ContentNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(ContentNative.Table));
        _fake->Header.Size = (uint)sizeof(ContentNative.Table);
        _fake->Header.Version = 4;
        _fake->Define = &Define;
        _tableField.SetValue(null, Pointer.Box(_fake, typeof(ContentNative.Table*)));
        Sent.Clear();
    }

    public void Dispose()
    {
        _tableField.SetValue(null, _table);
        NativeMemory.Free(_fake);
        HostAccess.Current = _caller;
        ModsBackend.Current = _mods;
        WorldTablesNative.TestDefine = null;
        Directory.Delete(_root, true);
    }

    private string Full(string rel) => Path.GetFullPath(Path.Combine(_dir, rel));

    private static ModBlocks NewMod(string id = Id) => new(id, 7) { ManifestClients = "mod" };

    private static string? IconSent(string item) => (string?)Sent.SingleOrDefault(j => (string?)j["name"] == item)?["icon"];

    private ModBlocks Load(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "blocks", "icons.json"), json);
        var mod = NewMod();
        BlockJson.LoadMod(mod, _dir);
        return mod;
    }

    [Fact]
    public void A_data_files_png_icons_reach_kiln_content_as_the_files_in_the_mods_folder()
    {
        var mod = Load($$"""
            { "format": 1,
              "weapons": { "cinderbrand": { "name": "Cinderbrand", "class": "sword", "stats": { "level": 45, "power": 92, "rarity": "legendary" },
                                            "visual": { "model": "{{Model}}", "icon": "icons/cinderbrand.png" } } },
              "items": { "gem": { "name": "Gem", "visual": { "icon": "icons/gem.png" } } },
              "armor": { "helm": { "name": "Helm", "slot": "head", "stats": { "level": 5, "physical": 10, "magical": 5 },
                                   "look": { "like": "Vanity_TX_Hunter_Helmet", "icon": "icons/helm.png" } } },
              "cosmetics": { "hood": { "name": "Hood", "slot": "head", "look": { "like": "Vanity_TX_Hunter_Helmet", "icon": "icons/hood.png" } } },
              "hair": { "crop": { "name": "Crop", "look": { "like": "VC_Hair_04_02_frenchcrop", "icon": "icons/crop.png" } } },
              "beards": { "trim": { "name": "Trim", "look": { "like": "VC_Beard12_stubble", "icon": "icons/trim.png" } } } }
            """);
        Assert.Empty(mod.Errors);
        foreach (var (item, file) in new[] { ("cinderbrand", "cinderbrand"), ("gem", "gem"), ("helm", "helm"), ("hood", "hood"), ("crop", "crop"), ("trim", "trim") })
            Assert.Equal(Full($"icons/{file}.png"), IconSent($"{Id}.{item}"));
    }

    [Fact]
    public void A_prop_and_a_materials_block_item_name_the_file_in_the_mods_folder()
    {
        var mod = NewMod();
        var prop = Compiler.Lower(mod, "stool", BlockJson.Parse<PropBlock>("""{ "name": "Stool", "visual": { "model": "item:Prop_Decoration_Comfort1_T0_Table_Medium", "icon": "icons/stool.png" } }""")).Low.Json;
        Assert.Equal(Full("icons/stool.png"), (string?)prop["itemParts"]?["icon"]);
        static string Registered(string name, int id) => new JsonObject { ["blocks"] = new JsonArray(new JsonObject { ["name"] = name, ["status"] = "registered", ["id"] = id }) }.ToJsonString();
        WorldTablesNative.TestDefine = d => Registered(d.Contains("\"brick\"") ? "brick" : "loam", 40);
        var brick = Compiler.Lower(mod, "brick", BlockJson.Parse<BuildingMaterialBlock>("""{ "name": "Brick", "icon": "icons/brick.png" }""")).Low.Json;
        Assert.Equal(Full("icons/brick.png"), (string?)brick["itemParts"]?["icon"]);
        var loam = Compiler.Lower(mod, "loam", BlockJson.Parse<TerrainMaterialBlock>("""{ "name": "Loam", "placeable": true, "icon": "icons/loam.png" }""")).Low.Json;
        Assert.Equal(Full("icons/loam.png"), (string?)loam["itemParts"]?["icon"]);
        // A wrong optional picture is refused, not the material or its block item.
        var asked = 0;
        WorldTablesNative.TestDefine = d => { asked++; return Registered("clay", 41); };
        var building = Compiler.Lower(mod, "clay", BlockJson.Parse<BuildingMaterialBlock>("""{ "name": "Clay", "icon": "../clay.png" }""")).Low.Json;
        var terrain = Compiler.Lower(mod, "clay", BlockJson.Parse<TerrainMaterialBlock>("""{ "name": "Clay", "placeable": true, "icon": "icons/clay.png" }""")).Low.Json;
        Assert.Null(building["itemParts"]?["icon"]);
        Assert.Null(terrain["itemParts"]?["icon"]);
        Assert.Equal(2, asked);
        Assert.Equal(2, mod.Errors.Count);
    }

    [Fact]
    public void A_worn_look_of_another_mod_names_that_mods_file()
    {
        // Blocks.ModFor: in this assembly "Kiln.Blocks" names the test namespace
        var registry = typeof(ModBlocks).Assembly.GetType("Kiln.Blocks", throwOnError: true)!.GetMethod("ModFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var other = (ModBlocks)registry.Invoke(null, [Other, (Func<ulong>)(() => 8)])!;
        other.ManifestClients = "mod";
        Compiler.Define(other, "cape-look", new WornLook { Like = "Vanity_TX_Hunter_Helmet", Icon = "icons/cape.png" });
        Compiler.Define(NewMod(), "cape", new CosmeticBlock { Name = "Cape", Slot = CosmeticSlot.Head, Look = $"{Other}:cape-look" });
        Assert.Equal(Path.GetFullPath(Path.Combine(_other, "icons/cape.png")), IconSent($"{Id}.cape"));
    }

    [Fact]
    public void A_visual_look_owned_by_another_mod_uses_its_owners_icon_for_items_and_props()
    {
        var registry = typeof(ModBlocks).Assembly.GetType("Kiln.Blocks", throwOnError: true)!.GetMethod("ModFor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var other = (ModBlocks)registry.Invoke(null, [Other, (Func<ulong>)(() => 8)])!;
        other.ManifestClients = "mod";
        Compiler.Define(other, "shared-visual", new VisualBlock { Model = "item:Prop_Decoration_Comfort1_T0_Table_Medium", Icon = "icons/cape.png" });
        var mod = NewMod();
        Compiler.Define(mod, "shared-item", new ItemBlock { Name = "Shared", Visual = $"{Other}:shared-visual" });
        Assert.Equal(Path.Combine(_other, "icons", "cape.png"), IconSent($"{Id}.shared-item"));
        var prop = Compiler.Lower(mod, "shared-prop", new PropBlock { Name = "Shared prop", Visual = $"{Other}:shared-visual" }).Low.Json;
        Assert.Equal(Path.Combine(_other, "icons", "cape.png"), (string?)prop["itemParts"]?["icon"]);
        Assert.Empty(mod.Errors);
    }

    [Fact]
    public void Mod_code_gives_a_path_relative_to_its_folder_or_the_full_path_of_a_file_there()
    {
        var context = HostAccess.CreateContext(new ModInfo(Id, Id, "1.0.0", _dir), 7, _dir);
        try
        {
            HostAccess.Current = context;
            Compiler.Define(NewMod(), "relative", new ItemBlock { Name = "Relative", Visual = new VisualBlock { Icon = "icons/gem.png" } });
            Compiler.Define(NewMod(), "full", new ItemBlock { Name = "Full", Visual = new VisualBlock { Icon = Path.Combine(_dir, "icons", "gem.png") } });
        }
        finally
        {
            HostAccess.Current = null;
            context.Retire();
        }
        Assert.Equal(Full("icons/gem.png"), IconSent($"{Id}.relative"));
        Assert.Equal(Full("icons/gem.png"), IconSent($"{Id}.full"));
    }

    [Theory]
    [InlineData("icons/missing.png")]
    [InlineData("../../outside.png")]
    public void An_unusable_optional_icon_keeps_every_item_and_material_defined(string icon)
    {
        var json = $$"""
            { "format": 1,
              "items": { "gem": { "name": "Gem", "visual": { "icon": "{{icon}}" } } },
              "cosmetics": { "hood": { "name": "Hood", "slot": "head", "look": { "like": "Vanity_TX_Hunter_Helmet", "icon": "{{icon}}" } } },
              "hair": { "crop": { "name": "Crop", "look": { "like": "VC_Hair_04_02_frenchcrop", "icon": "{{icon}}" } } },
              "beards": { "trim": { "name": "Trim", "look": { "like": "VC_Beard12_stubble", "icon": "{{icon}}" } } },
              "buildingMaterials": { "brick": { "name": "Brick", "icon": "{{icon}}" } },
              "terrainMaterials": { "loam": { "name": "Loam", "placeable": true, "icon": "{{icon}}" } } }
            """;
        WorldTablesNative.TestDefine = d => new JsonObject { ["blocks"] = new JsonArray(new JsonObject
            { ["name"] = d.Contains("\"brick\"") ? "brick" : "loam", ["status"] = "registered", ["id"] = 40 }) }.ToJsonString();
        var mod = Load(json);
        foreach (var name in new[] { "gem", "hood", "crop", "trim", "brick", "loam" })
        {
            Assert.True(mod.Handles.ContainsKey(name), $"{name} disappeared: {string.Join("; ", mod.Errors)}");
            Assert.Null(IconSent($"{Id}.{name}"));
            var error = Assert.Single(mod.Errors, e => e.Contains($" {name}, ", StringComparison.Ordinal));
            Assert.Contains(name is "brick" or "loam" ? ", icon:" : name is "gem" or "stool" ? ", visual.icon:" : ", look.icon:", error);
            Assert.Contains(Full(icon), error);
            Assert.Contains("the base icon stays", error);
        }
        Assert.Equal(6, Sent.Count);
        // A prop also needs kiln.gamedata to commit its placed template, which this harness does not fake.
        var prop = Compiler.Lower(mod, "stool", BlockJson.Parse<PropBlock>($$"""{ "name": "Stool", "visual": { "model": "item:Prop_Decoration_Comfort1_T0_Table_Medium", "icon": "{{icon}}" } }"""));
        Assert.Equal($"{Id}.stool", (string?)prop.Low.Json["items"]?[0]?["name"]);
        Assert.Null(prop.Low.Json["itemParts"]?["icon"]);
        var propError = Assert.Single(mod.Errors, e => e.Contains(" stool, ", StringComparison.Ordinal));
        Assert.Contains(", visual.icon:", propError);
        Assert.Contains(Full(icon), propError);
        Assert.Contains("the base icon stays", propError);
        Assert.Equal(7, mod.Errors.Count);
    }

    [Fact]
    public void A_path_that_leaves_the_mods_folder_or_names_no_file_is_refused_with_the_field()
    {
        File.CreateSymbolicLink(Path.Combine(_dir, "icons", "link.png"), Path.Combine(_root, "outside.png"));
        var outside = Path.Combine(_root, "outside.png").Replace("\\", "\\\\");
        var mod = Load($$"""
            { "format": 1,
              "items": {
                "up": { "name": "Up", "visual": { "icon": "../../outside.png" } },
                "rooted": { "name": "Rooted", "visual": { "icon": "{{outside}}" } },
                "linked": { "name": "Linked", "visual": { "icon": "icons/link.png" } },
                "missing": { "name": "Missing", "visual": { "icon": "icons/missing.png" } } },
              "cosmetics": { "worn-up": { "name": "Worn", "slot": "head", "look": { "like": "Vanity_TX_Hunter_Helmet", "icon": "../../outside.png" } } } }
            """);
        Assert.Equal(5, Sent.Count);
        Assert.All(Sent, item => Assert.Null(item["icon"]));
        Assert.Equal(5, mod.Handles.Count);
        string Error(string block) => Assert.Single(mod.Errors, e => e.Contains($" {block}, ", StringComparison.Ordinal));
        Assert.Contains("\"../../outside.png\": a file in the mod's folder, with a relative path", Error("up"));
        Assert.Contains("visual.icon:", Error("rooted"));
        Assert.Contains("a file in the mod's folder", Error("rooted"));
        Assert.Contains("links cannot leave it", Error("linked"));
        Assert.Contains($"\"icons/missing.png\" is not in the mod's folder ({Full("icons/missing.png")})", Error("missing"));
        Assert.Contains("look.icon:", Error("worn-up"));
        Assert.Contains("\"../../outside.png\": a file in the mod's folder", Error("worn-up"));
        Assert.All(mod.Errors, error => Assert.Contains("the base icon stays", error));
        Assert.Equal(5, mod.Errors.Count);
    }
}
