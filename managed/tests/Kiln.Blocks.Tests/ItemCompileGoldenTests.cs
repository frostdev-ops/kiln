// SPDX-License-Identifier: MIT
// What item, weapon, wand and armor blocks compile to, pinned: each block below lowers (Compiler.Lower(...).Low.Json, nothing reaches a game) and the whole JSON is
// compared with its golden file in Goldens/items/. The goldens were written once from the compiler as it was before weapon moves, so a block that does not use a
// new field must keep compiling to exactly this. A difference is a change in what an existing block makes: fix the compiler, do not refresh the golden.
// Writing them (only for a block added here, or a change that is meant): KILN_GOLDEN_WRITE=1 dotnet test managed/tests/Kiln.Blocks.Tests --filter ItemCompileGoldenTests
// The goldens hold only what Kiln's compiler emits (names of the game's starters as the compiler writes them, GUIDs Kiln derives), no game data.
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class ItemCompileGoldenTests
{
    private const string Mod = "golden-items";

    /// <summary>The blocks, by golden name: (block type, the block as blocks/*.json writes it).</summary>
    internal static readonly (string Name, Type Type, string Json)[] Cases =
    [
        // every premade weapon class, plain
        ("weapon-sword", typeof(WeaponBlock), """{ "name": "Sword", "class": "sword" }"""),
        ("weapon-axe", typeof(WeaponBlock), """{ "name": "Axe", "class": "axe" }"""),
        ("weapon-mace", typeof(WeaponBlock), """{ "name": "Mace", "class": "mace" }"""),
        ("weapon-greatsword", typeof(WeaponBlock), """{ "name": "Greatsword", "class": "greatsword" }"""),
        ("weapon-greataxe", typeof(WeaponBlock), """{ "name": "Greataxe", "class": "greataxe" }"""),
        ("weapon-hammer", typeof(WeaponBlock), """{ "name": "Hammer", "class": "hammer" }"""),
        ("weapon-daggers", typeof(WeaponBlock), """{ "name": "Daggers", "class": "daggers" }"""),
        ("weapon-bow", typeof(WeaponBlock), """{ "name": "Bow", "class": "bow" }"""),
        ("weapon-staff", typeof(WeaponBlock), """{ "name": "Staff", "class": "staff" }"""),
        ("weapon-shield", typeof(WeaponBlock), """{ "name": "Shield", "class": "shield", "stats": { "block": 0.6 } }"""),
        // a weapon with everything a weapon block takes
        ("weapon-full", typeof(WeaponBlock), """
            { "name": "Cinderbrand", "description": "Hot.", "source": "The forge", "class": "sword",
              "stats": { "level": 12, "power": 48, "rarity": "rare", "staminaCost": 9 },
              "perks": [ "Perk_Damage_Ice", null, "item:Weapon_T1_1H_Sword_01" ],
              "visual": { "like": "Weapon_T1_1H_Sword_01", "tint": "#ff8800", "vfx": "85e60392-52e2-4488-8653-2d539c18c80c" },
              "onHit": [ { "damage": { "fire": 5 } }, { "heal": 2, "on": "caster" } ],
              "onKill": [ { "vfx": "85e60392-52e2-4488-8653-2d539c18c80c" } ],
              "recipe": { "needs": { "Material_T1_Metal_Scraps": 4, "Material_T1_Wood_Logs": 2 } },
              "loot": { "tables": [ { "dropsLike": "Weapon_T1_1H_Sword_01", "chance": 0.25 } ] } }
            """),
        ("weapon-damage", typeof(WeaponBlock), """
            { "name": "Frost Axe", "class": "greataxe", "stats": { "level": 20, "damage": { "cut": 30, "ice": 10 } }, "loot": { "like": "Weapon_T1_2H_GreatAxe_01" } }
            """),
        ("weapon-rolled", typeof(WeaponBlock), """{ "name": "Rolled", "class": "hammer", "loot": { "like": "Weapon_T1_2H_Hammer_01", "rolled": true } }"""),
        // an own class
        ("weapon-own-class", typeof(WeaponBlock), """
            { "name": "Own", "class": { "like": "daggers", "visual": { "like": "Weapon_T1_2H_Daggers_01_DPS" }, "stats": { "level": 5, "power": 20 } } }
            """),
        // wands
        ("wand-plain", typeof(WandBlock), """
            { "name": "Plain Wand", "primary": { "cast": { "manaCost": 4 }, "delivery": { "type": "projectile", "onHit": [ { "damage": { "ice": 12 } } ] } } }
            """),
        ("wand-full", typeof(WandBlock), """
            { "name": "Storm Wand", "stats": { "level": 8, "rarity": "uncommon" },
              "primary": { "cast": { "manaCost": 5, "releaseAt": 0.3, "vfx": "85e60392-52e2-4488-8653-2d539c18c80c" }, "moves": "bolt",
                           "delivery": { "type": "projectile", "speed": 45, "onHit": [ { "damage": { "lightning": 14 } } ] } },
              "heavy": { "cast": { "manaCost": 12, "releaseAt": 1.2, "staminaCost": 5 }, "moves": "heavy",
                         "delivery": { "type": "projectile", "look": "fireball", "onHit": [ { "damage": { "fire": 30 } } ] } },
              "special": { "cast": { "manaCost": 20, "releaseAt": 0.2 }, "moves": "special",
                           "delivery": { "type": "projectile", "onImpact": [ { "area": { "radius": 3, "effects": [ { "damage": { "ice": 8 } } ] } } ] } },
              "recipe": { "needs": { "Material_T1_Wood_Logs": 3 } } }
            """),
        // armor
        ("armor-chest", typeof(ArmorBlock), """
            { "name": "Chest", "slot": "chest", "set": "warrior", "stats": { "level": 10, "rarity": "uncommon", "physical": 40, "magical": 12,
              "resist": { "fire": 5 }, "bonus": { "health": 60, "melee-damage": 0.05 } }, "perks": [ "Perk_Damage_Ice" ], "recipe": { "needs": { "Material_T1_Fur": 3 } } }
            """),
        ("armor-ring", typeof(ArmorBlock), """{ "name": "Ring", "slot": "ring", "stats": { "bonus": { "mana": 25 } } }"""),
        // items, each kind the compiler branches on
        ("item-material", typeof(ItemBlock), """{ "name": "Ember", "stack": 50, "fuel": 30, "rarity": "rare", "level": 4, "visual": { "like": "Material_T1_Charcoal" } }"""),
        ("item-comfort", typeof(ItemBlock), """{ "name": "Rug", "comfort": 2 }"""),
        ("item-potion-heals", typeof(ItemBlock), """{ "name": "Red Potion", "heals": 80, "recipe": { "needs": { "Material_T1_Mushroom": 2 } } }"""),
        ("item-potion-heals-onuse", typeof(ItemBlock), """{ "name": "Red Plus", "heals": 40, "onUse": [ { "restores": { "mana": 10 } } ] }"""),
        ("item-potion-onuse", typeof(ItemBlock), """{ "name": "Blue Potion", "onUse": [ { "restores": { "mana": 30, "stamina": 20 } }, { "heal": 5, "chance": 0.5 } ] }"""),
        ("item-food", typeof(ItemBlock), """{ "name": "Sweet Bun", "feeds": "sweet", "heals": 10 }"""),
        ("item-ammo", typeof(ItemBlock), """
            { "name": "Fire Arrow", "level": 6, "ammo": { "power": 8, "damage": { "pierce": 6, "fire": 2 },
              "delivery": { "type": "projectile", "speed": 60, "onHit": [ { "damage": { "fire": 4 } } ] } } }
            """),
        ("item-ammo-plain", typeof(ItemBlock), """{ "name": "Plain Arrow", "ammo": { } }"""),
        ("item-tool-axe", typeof(ItemBlock), """{ "name": "Big Axe", "tool": { "harvests": "wood", "power": 40, "staminaCost": 3 }, "rarity": "uncommon", "level": 7 }"""),
        ("item-tool-pickaxe", typeof(ItemBlock), """{ "name": "Big Pick", "tool": { "harvests": "stone", "power": 24, "breaksInto": "Material_T1_Metal_Scraps" } }"""),
        ("item-tool-rake", typeof(ItemBlock), """{ "name": "Big Rake", "tool": { "harvests": "rake", "power": 900, "range": 5 } }"""),
        ("item-tool-hook", typeof(ItemBlock), """{ "name": "Long Hook", "tool": { "harvests": "grappling-hook", "range": 20 } }"""),
        ("item-tool-rod", typeof(ItemBlock), """{ "name": "Good Rod", "tool": { "harvests": "fishing-rod", "fishing": { "strength": 2, "endurance": 50 } } }"""),
        ("item-torch", typeof(ItemBlock), """{ "name": "Long Torch", "tool": { "harvests": "torch", "durability": 1200, "breaksInto": "Tool_Torch01" } }"""),
        ("item-watering-can", typeof(ItemBlock), """{ "name": "Big Can", "kind": "watering-can", "holds": 400, "flow": 2 }"""),
        ("item-water-pouch", typeof(ItemBlock), """{ "name": "Big Pouch", "kind": "water-pouch", "holds": 60 }"""),
    ];

    public static TheoryData<string> Names() => new(Cases.Select(c => c.Name));

    private static string Dir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "Goldens", "items");

    internal static ModBlocks NewMod()
    {
        var mod = new ModBlocks(Mod, 1) { ManifestClients = "mod" };
        // the status the food gives: a block of the mod, lowered (not committed) so the food can name it
        mod.Handles["sweet"] = Compiler.Lower(mod, "sweet", new StatusBlock { Name = "Sweet", Kind = StatusKind.Buff, Seconds = 300 }).Handle;
        return mod;
    }

    private static readonly System.Reflection.MethodInfo ParseOpen = typeof(BlockJson).GetMethod("Parse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
    private static readonly System.Reflection.MethodInfo LowerOpen = typeof(ItemCompileGoldenTests).GetMethod(nameof(LowerTyped), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    private static JsonObject LowerTyped<T>(ModBlocks mod, string name, T block) where T : class, IBlock => Compiler.Lower(mod, name, block).Low.Json;

    /// <summary>A case lowered: the whole JSON the compiler made for it.</summary>
    internal static JsonObject Lowered(string name)
    {
        var c = Cases.Single(x => x.Name == name);
        var block = ParseOpen.MakeGenericMethod(c.Type).Invoke(null, [c.Json])!;
        try
        {
            return (JsonObject)LowerOpen.MakeGenericMethod(c.Type).Invoke(null, [NewMod(), c.Name, block])!;
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    internal static string Text(JsonNode n) => n.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";

    internal static void Compare(string dir, string name, JsonObject made)
    {
        var file = Path.Combine(dir, name + ".json");
        var text = Text(made);
        if (Environment.GetEnvironmentVariable("KILN_GOLDEN_WRITE") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(file, text);
            return;
        }
        Assert.True(File.Exists(file), $"no golden {file}: write it with KILN_GOLDEN_WRITE=1, from a compiler that is known to be right");
        var want = File.ReadAllText(file).Replace("\r\n", "\n");
        if (want == text) return;
        // the first line that differs, to say where
        var a = want.Split('\n');
        var b = text.Split('\n');
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        Assert.Fail($"{name}: the compile differs from its golden at line {i + 1}:\n  golden: {(i < a.Length ? a[i].Trim() : "(end)")}\n  now:    {(i < b.Length ? b[i].Trim() : "(end)")}");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void An_existing_block_compiles_to_its_golden(string name) => Compare(Dir(), name, Lowered(name));

    [Fact]
    public void Every_golden_has_its_block()
    {
        if (!Directory.Exists(Dir())) return;
        var files = Directory.GetFiles(Dir(), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList();
        Assert.Equal(Cases.Select(c => c.Name).Order(), files);
    }
}
