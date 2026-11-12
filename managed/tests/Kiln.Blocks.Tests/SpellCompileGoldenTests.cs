// SPDX-License-Identifier: MIT
// What spell blocks compile to, pinned, as ItemCompileGoldenTests does for items: each spell lowers and the whole JSON is compared with its golden in Goldens/spells/.
// Written once from the compiler as it was before cast moves (animation, like, hitAt on a cast). A difference is a change in what an existing spell makes.
// Writing them (only for a spell added here, or a change that is meant): KILN_GOLDEN_WRITE=1 dotnet test managed/tests/Kiln.Blocks.Tests --filter SpellCompileGoldenTests
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class SpellCompileGoldenTests
{
    internal static readonly (string Name, string Json)[] Cases =
    [
        ("spell-default", """{ "name": "Bolt", "cast": { "manaCost": 10 }, "delivery": { "type": "projectile", "onHit": [ { "damage": { "ice": 20 } } ] } }"""),
        ("spell-lob", """
            { "name": "Lob", "level": 6, "maxCharges": 200, "cast": { "manaCost": 25, "staminaCost": 4, "animation": "lob" },
              "delivery": { "type": "projectile", "look": "fireball", "gravity": 0.5, "onImpact": [ { "area": { "radius": 4, "effects": [ { "damage": { "fire": 30 } } ] } } ] },
              "recipe": { "makes": 20, "needs": { "Material_T1_Wood_Logs": 2 } } }
            """),
        ("spell-release", """{ "name": "Quick", "cast": { "manaCost": 8, "releaseAt": 0.3 }, "delivery": { "type": "projectile", "onHit": [ { "damage": { "lightning": 12 } } ] } }"""),
        ("spell-release-lob", """
            { "name": "Slow Lob", "cast": { "manaCost": 8, "animation": "lob", "releaseAt": 1.1 }, "delivery": { "type": "projectile", "look": "fireball", "onHit": [ { "damage": { "fire": 12 } } ] } }
            """),
        ("spell-vfx", """
            { "name": "Glow", "cast": { "manaCost": 8, "vfx": "85e60392-52e2-4488-8653-2d539c18c80c" }, "delivery": { "type": "projectile", "onHit": [ { "damage": { "ice": 9 } } ] } }
            """),
        ("spell-free", """{ "name": "Free", "cast": { "manaCost": 3, "charges": 0 }, "delivery": { "type": "projectile", "onHit": [ { "damage": { "ice": 5 } } ] } }"""),
        ("spell-charges", """
            { "name": "Greedy", "cast": { "manaCost": 3, "charges": 3 }, "onCast": [ { "heal": 4, "on": "caster" } ], "delivery": { "type": "projectile", "onHit": [ { "damage": { "ice": 5 } } ] } }
            """),
        ("spell-self", """{ "name": "Mend", "cast": { "manaCost": 15, "animation": "lob", "releaseAt": 0.8, "charges": 0 }, "delivery": { "type": "self", "effects": [ { "heal": 30 } ] } }"""),
    ];

    public static TheoryData<string> Names() => new(Cases.Select(c => c.Name));

    private static string Dir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "Goldens", "spells");

    internal static JsonObject Lowered(string name)
    {
        var c = Cases.Single(x => x.Name == name);
        return Compiler.Lower(ItemCompileGoldenTests.NewMod(), c.Name, BlockJson.Parse<SpellBlock>(c.Json)).Low.Json;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void An_existing_spell_compiles_to_its_golden(string name) => ItemCompileGoldenTests.Compare(Dir(), name, Lowered(name));

    [Fact]
    public void Every_golden_has_its_spell()
    {
        if (!Directory.Exists(Dir())) return;
        Assert.Equal(Cases.Select(c => c.Name).Order(), Directory.GetFiles(Dir(), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order());
    }
}
