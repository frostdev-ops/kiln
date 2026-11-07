// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kiln.Sdk;
using Xunit;
using Xunit.Sdk;

namespace Kiln.Analyzers.Tests;

/// <summary>What the build works out for <c>clients</c> from a mod's data files (Kiln.Sdk's DataClients, which the KilnManifest task runs):
/// in step with Kiln.Blocks' block types, one case per kind, and the other data folders.</summary>
public sealed class DataClientsTests
{
    private static readonly Assembly BlocksAssembly = typeof(SpellBlock).Assembly;

    // Every section of blocks/*.json and the block type it makes: the table Kiln.Blocks reads data files with.
    private static List<(string Section, Type Type)> RuntimeSections()
    {
        var kinds = (Array)BlocksAssembly.GetType("Kiln.BlockJson", throwOnError: true)!.GetField("Kinds", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var list = new List<(string, Type)>();
        foreach (ITuple k in kinds)
        {
            var make = ((Delegate)k[1]!).Method;
            var type = make.IsGenericMethod ? make.GetGenericArguments()[0]
                : make.Name switch { "MakeVfx" => typeof(VfxBlock), "MakeMaterial" => typeof(MaterialBlock), _ => throw new InvalidOperationException($"{k[0]}: {make.Name} makes a type this test does not know") };
            list.Add(((string)k[0]!, type));
        }
        return list;
    }

    // What a block type asks on its own, as Kiln.Blocks' Compiler.Define does: its [BlockClients], else kiln. An ability is the one type without
    // the attribute that asks nothing itself: what it places, its item and its key ask (DataClients.OfBlock, the cases below).
    private static string Own(Type t) =>
        t.GetCustomAttribute<BlockClientsAttribute>()?.Level.ToString().ToLowerInvariant() ?? (t == typeof(AbilityBlock) ? DataClients.None : DataClients.Kiln);

    [Fact]
    public void Every_section_is_known_with_its_own_level()
    {
        var runtime = RuntimeSections();
        Assert.Equal(runtime.Select(s => s.Section).Order(StringComparer.Ordinal), DataClients.Sections.Keys.Order(StringComparer.Ordinal));
        Assert.All(runtime, s => Assert.True(DataClients.Sections[s.Section] == Own(s.Type), $"{s.Section} ({s.Type.Name}): the build says {DataClients.Sections[s.Section]}, the block type {Own(s.Type)}"));
    }

    private static string JsonName(PropertyInfo p) =>
        p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name);

    [Fact]
    public void Every_field_that_can_name_the_mods_own_files_is_read()
    {
        var types = BlocksAssembly.GetTypes().Where(t => t.Namespace == "Kiln" && !t.IsGenericTypeDefinition);
        var fields = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttribute<ClientsModWhenAttribute>() != null
                        || (Nullable.GetUnderlyingType(x.Property.PropertyType) ?? x.Property.PropertyType) is var pt && (pt == typeof(VfxRef) || pt == typeof(SoundRef)))
            .ToList();
        Assert.NotEmpty(fields);
        Assert.All(fields, x => Assert.True(DataClients.ModFields.Contains(JsonName(x.Property)), $"{x.Type.Name}.{x.Property.Name} (\"{JsonName(x.Property)}\") can name the mod's own file, and the build does not read it"));
    }

    private const string Guid1 = "3d1f2a40-5b6c-4d7e-8f90-a1b2c3d4e5f6";

    // (section, one block, what it asks: null = nothing more than "none")
    public static TheoryData<string, string, string?> Kinds() => new()
    {
        // kiln: a definition Kiln clients get from the server
        { "statuses", """{ "name": "Burn", "seconds": 4 }""", "kiln" },
        { "spells", """{ "name": "Bolt", "cast": { "manaCost": 5 }, "delivery": { "type": "projectile" } }""", "kiln" },
        { "spells", """{ "name": "Bolt", "delivery": { "type": "projectile", "look": "fireball", "vfx": "game:Fx_Fireball_Trail" } }""", "kiln" },
        { "spells", """{ "name": "Bolt", "delivery": { "type": "projectile", "onHit": [ { "vfx": "my-burst" } ] } }""", "mod" },
        { "weapons", """{ "name": "Blade", "class": "sword" }""", "kiln" },
        { "perks", """{ "name": "Edge", "value": "5%" }""", "kiln" },
        { "perks", """{ "name": "Edge", "onHit": [ { "sound": "clang" } ] }""", "mod" },
        { "items", """{ "name": "Gem" }""", "kiln" },
        { "items", """{ "name": "Gem", "visual": { "icon": "gem.png" } }""", "mod" },
        { "items", $$"""{ "name": "Gem", "visual": { "model": "{{Guid1}}" } }""", "mod" },
        { "items", """{ "name": "Gem", "visual": { "model": "item:Ember_Sword", "icon": "item:Ember_Sword" } }""", "kiln" },
        { "wands", """{ "name": "Wand", "primary": { "cast": { "manaCost": 5 } } }""", "kiln" },
        { "armor", """{ "name": "Cap", "slot": "head" }""", "kiln" },
        { "stations", """{ "name": "Kiln Bench" }""", "kiln" },
        { "stations", """{ "name": "Kiln Bench", "visual": { "icon": "bench.png" } }""", "mod" },
        { "cosmetics", """{ "name": "Hat", "look": { "model": "item:Armor_Hat" } }""", "kiln" },
        { "cosmetics", """{ "name": "Hat", "look": { "model": "hat.glb" } }""", "mod" },
        { "cosmetics", """{ "name": "Hat", "look": { "model": "item:Armor_Hat", "female": "hat_f.gltf" } }""", "mod" },
        { "cosmetics", """{ "name": "Hat", "look": { "model": "item:Armor_Hat" }, "icon": "hat.png" }""", "mod" },
        { "wornLooks", """{ "model": "item:Armor_Hat" }""", "kiln" },
        { "looks", """{ "model": "item:Ember_Sword" }""", "kiln" },
        { "weaponStats", """{ "level": 5 }""", "kiln" },
        { "armorStats", """{ "level": 5 }""", "kiln" },
        { "weaponClasses", """{ "starter": "Sword" }""", "kiln" },
        { "armorSlots", """{ "starter": "Head" }""", "kiln" },
        { "armorSets", """{ "pieces": [] }""", "kiln" },
        { "onHits", """{ "effects": [ { "damage": { "fire": 3 } } ] }""", "kiln" },
        { "props", """{ "name": "Lamp" }""", "kiln" },
        { "props", $$"""{ "name": "Lamp", "visual": { "model": "{{Guid1}}" } }""", "mod" },
        { "props", """{ "name": "Lamp", "light": "lamp-glow" }""", "mod" },
        { "props", """{ "name": "Lamp", "light": "game:Fx_Torch_Light" }""", "kiln" },
        { "placements", """{ "snap": true }""", "kiln" },
        { "comforts", """{ "amount": 2 }""", "kiln" },
        { "processing", """{ "seconds": 10 }""", "kiln" },
        { "processing", """{ "seconds": 10, "running": { "sound": "hum" } }""", "mod" },
        { "menus", """{ "title": "Kiln" }""", "kiln" },
        { "containers", """{ "name": "Crate", "slots": 8 }""", "kiln" },
        { "doors", """{ "name": "Door" }""", "kiln" },
        { "beds", """{ "name": "Bed" }""", "kiln" },
        { "seats", """{ "name": "Stool" }""", "kiln" },
        { "interactions", """{ "name": "Lever" }""", "kiln" },
        { "hair", """{ "name": "Braid", "look": { "model": "item:Hair_Braid" } }""", "kiln" },
        { "hair", """{ "name": "Braid", "look": { "icon": "braid.png" } }""", "mod" },
        { "beards", """{ "name": "Full", "look": { "model": "item:Beard_Full" } }""", "kiln" },
        // nothing more than none, unless a field asks
        { "effects", """{ "damage": { "fire": 5 } }""", null },
        { "effects", """{ "damage": { "fire": 5 }, "vfx": "game:Fx_Burst" }""", "kiln" },
        { "effects", """{ "vfx": { "name": "my-burst", "slot": "chest" } }""", "mod" },
        { "effects", $$"""{ "sound": "{{Guid1}}" }""", null },
        { "effects", """{ "sound": "zap" }""", "mod" },
        { "effects", """{ "sound": "other-mod:zap" }""", "mod" },
        { "abilities", """{ "onCast": [ { "heal": 5 } ] }""", null },
        { "abilities", """{ "delivery": { "type": "self", "effects": [ { "heal": 5 } ] } }""", null },
        { "abilities", """{ "delivery": { "type": "self", "vfx": "game:Fx_Heal", "effects": [ { "heal": 5 } ] } }""", "kiln" },
        { "abilities", """{ "delivery": { "type": "cone", "range": 8, "angle": 60, "effects": [ { "damage": { "fire": 5 } } ] } }""", "kiln" },
        { "abilities", """{ "item": { "name": "Charm" }, "delivery": { "type": "self", "effects": [ { "heal": 5 } ] } }""", "kiln" },
        { "abilities", """{ "cast": { "animation": "fastBolt" }, "onCast": [ { "heal": 5 } ] }""", "kiln" },
        { "abilities", """{ "key": "G", "onCast": [ { "heal": 5 } ] }""", "mod" },
        { "triggers", """{ "on": "Gliding", "do": [ { "heal": 1 } ] }""", null },
        { "screenEffects", """{ "tint": [1, 0, 0], "seconds": 2 }""", null },
        { "sounds", """{ "clips": ["audio/hum.wav"], "range": [3, 40] }""", "mod" },
        { "materialTweaks", """{ "material": "Stone", "hardness": 2 }""", null },
        { "strengths", """{ "material": "Stone", "hits": 3 }""", null },
        { "surfaces", """{ "material": "Stone", "noiseRange": [0, 1] }""", null },
        { "creatures", """{ "body": "Enemy_Wolf", "behaviour": { "attacks": [ { "sequence": "Wolf_Bite" } ] } }""", null },
        { "creatures", """{ "body": { "model": "wolf.glb" } }""", "mod" },
        { "creatures", """{ "body": "Enemy_Wolf", "animation": { "clips": "wolf_anims.glb" } }""", "mod" },
        { "creatures", """{ "body": "Enemy_Wolf", "behaviour": { "attacks": [ { "damage": { "cut": 9 } } ] } }""", "kiln" },
        { "bodies", """{ "template": "Enemy_Wolf" }""", null },
        { "bodies", """{ "model": "wolf.glb" }""", "mod" },
        { "animations", """{ "template": "Enemy_Wolf" }""", null },
        { "animations", """{ "clips": "wolf_anims.glb" }""", "mod" },
        { "ais", """{ "template": "Enemy_Wolf" }""", null },
        { "behaviours", """{ "attacks": [ "bite" ] }""", null },
        { "attacks", """{ "sequence": "Wolf_Bite" }""", null },
        { "attacks", """{ "damage": { "cut": 9 } }""", "kiln" },
        { "rules", """{ "where": { "name": "Enemy_Wolf*" }, "health": 2 }""", null },
        // mod: every player's game builds it from the mod's own files
        { "vfx", """{ "particles": [] }""", "mod" },
        { "materials", """{ "texture": "glow.png" }""", "mod" },
        { "talents", """{ "name": "Kindle", "modifies": { "intelligence": 1 } }""", "mod" },
        { "talentTrees", """{ "name": "Ember Path", "nodes": {} }""", "mod" },
        { "buildingMaterials", """{ "name": "Glass" }""", "mod" },
        { "blueprints", """{ "name": "Hut" }""", "mod" },
        { "npcs", """{ "name": "Smith" }""", "mod" },
    };

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Each_kind(string section, string block, string? expected)
    {
        using var doc = JsonDocument.Parse(block);
        Assert.Equal(expected, DataClients.OfBlock(section, doc.RootElement));
        using var file = JsonDocument.Parse($$"""{ "{{section}}": { "x": {{block}} } }""");
        Assert.Equal(expected, DataClients.OfBlocks(file.RootElement));
    }

    [Fact]
    public void Every_section_has_a_case()
    {
        var covered = Kinds().Select(row => (string)((ITheoryDataRow)row).GetData()[0]!).ToHashSet();
        Assert.All(DataClients.Sections.Keys, s => Assert.Contains(s, covered));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("none", null, "none")]
    [InlineData(null, "kiln", "kiln")]
    [InlineData("none", "kiln", "kiln")]
    [InlineData("mod", "kiln", "mod")]
    [InlineData("kiln", "mod", "mod")]
    public void The_higher_of_code_and_data(string? code, string? data, string? expected) => Assert.Equal(expected, DataClients.Max(code, data));

    // A mod folder with only data files (no code that defines blocks): what the build writes as "clients".
    private static string? Folder(params (string Path, string Text)[] files)
    {
        var dir = Path.Combine(Path.GetTempPath(), "kiln-dataclients-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            return DataClients.OfFolder(dir);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_data_only_mod_with_talents_asks_mod() =>
        Assert.Equal("mod", Folder(("blocks/perks.json", """{ "perks": { "edge": { "name": "Edge", "value": "5%" } } }"""),
            ("blocks/talents.json", """{ "talents": { "kindle": { "name": "Kindle", "game": { "from": "0x8efcc472" } } } }""")));

    [Fact]
    public void A_data_only_mod_with_items_asks_kiln_and_with_effects_only_nothing()
    {
        Assert.Equal("kiln", Folder(("blocks/items.json", """{ "items": { "gem": { "name": "Gem" } }, "effects": { "zap": { "damage": { "fire": 1 } } } }""")));
        // comments and trailing commas, as the loader reads them
        Assert.Null(Folder(("blocks/fx.json", "// effects only\n" + """{ "effects": { "zap": { "damage": { "fire": 1 }, }, }, "triggers": { "on-glide": { "on": "Gliding", "do": [] } } }""")));
        Assert.Null(Folder(("blocks/broken.json", """{ "talents": """)));   // a file that does not parse is the loader's and lint's to report
    }

    [Theory]
    [InlineData("skills/tree.json", """{ "gameTree": { "nodes": [ { "id": "0x1" } ] } }""", "mod")]
    [InlineData("skills/tree.json", """{ "gameTree": [ { "id": "0x1" } ] }""", "mod")]
    [InlineData("skills/tree.json", """{ "trees": [ { "id": "path" } ] }""", "mod")]
    [InlineData("skills/tree.json", """{ "trees": [] }""", null)]
    [InlineData("quests/q.json", """{ "quests": { "q1": { "title": "Find it" } } }""", "mod")]
    [InlineData("quests/q.json", """{ "quests": { "q1": { "journal": "chat" } } }""", null)]
    [InlineData("quests/generated/q.json", """{ "quests": { "q1": { "title": "Find it" } } }""", "mod")]
    [InlineData("quests/q.json", """{ "dialogues": { "d1": { "native": false, "nodes": { "a": { "voice": "line.wav" } } } } }""", "mod")]
    [InlineData("quests/q.json", """{ "dialogues": { "d1": { "native": false } }, "npcs": { "n1": { "clone": false } } }""", null)]
    [InlineData("quests/q.json", """{ "npcs": { "n1": { "clone": "Npc_Smith" } } }""", "mod")]
    [InlineData("content/items.json", """{ "items": { "add": [ { "name": "Gem" } ] } }""", "kiln")]
    [InlineData("content/items.json", """{ "items": { "add": [ { "name": "Gem", "icon": "gem.png" } ] } }""", "mod")]
    [InlineData("content/tweaks.json", """{ "items": { "Gem": { "maxStackSize": 5 } } }""", null)]
    [InlineData("assets/assets.json", """{ "entries": [ { "kind": "icon", "file": "gem.png" } ] }""", "mod")]
    [InlineData("assets/assets.json", """{ "entries": [ { "kind": "resource", "type": "keen::RenderModelResource" } ] }""", "mod")]
    [InlineData("assets/assets.json", """{ "entries": [ { "kind": "resource", "type": "keen::ImpactProgram" } ] }""", null)]
    [InlineData("assets/cook.json", """{ "steps": [ { "op": "model", "in": "hat.glb" } ] }""", "mod")]
    [InlineData("assets/cook.json", """{ "tool": "impact", "steps": [ { "op": "model" } ] }""", null)]
    public void Other_data_folders(string path, string text, string? expected) => Assert.Equal(expected, Folder((path, text)));
}
