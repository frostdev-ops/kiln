// SPDX-License-Identifier: MIT
// GameLayoutStamps.Check: a mod's stamp (kiln.gametypes.json) against a layout table. Hand-made tables and stamps,
// plus the table embedded in the Kiln.Game under test.
using System.Reflection;
using Xunit;

namespace Kiln.Game.Tests;

public class GameLayoutStampsTests
{
    // A hand-made table in the format of docs/kiln/typegen.md §3 (made-up types and hashes).
    const string Table =
        "// kiln-typegen: generated at build time from a game layout dump. Never commit this file.\n" +
        "kiln.game.layouts\t1\tside=server\texe_sha256=00000000000000000000000000000000000000000000000000000000000000aa\troot=Ecs\ttypes=3\tset=1111111111111111\n" +
        "S\tKiln.Ecs+Lamp\t00000000000000a1\tkeen::ecs::Lamp\t24\tcomponent\tglow@0:float\twick@4:Ecs.LampId\tstore@8:Ecs.StaticArray4<float>\n" +
        "E\tKiln.Ecs+Glimmer_Mode\t00000000000000b2\tkeen::glimmer::Mode\t1\tbyte\tOff=0\tDim=1\tBright=2\n" +
        "F\tKiln.Ecs+Glimmer_ModeMask\t00000000000000c3\tkeen::Bitmask8<keen::glimmer::Mode>\t1\tbyte\tOff=1\tDim=2\tBright=4\n";

    static bool Check(string stamp, out string? reason) => GameLayoutStamps.Check(stamp, Table, out reason);

    static string Stamp(string types, string set = "2222222222222222") =>
        $$"""{ "schema": 1, "assembly": "Lanterns.dll", "mvid": "8b1f4cbe-7c41-4a52-9a3b-0d1c3f0e5a11", "layoutSet": "{{set}}", "types": { {{types}} } }""";

    [Fact]
    public void The_same_layout_passes()
    {
        Assert.True(Check(Stamp("""
            "Kiln.Ecs+Lamp": { "layout": "00000000000000a1", "fields": { "glow": "float" } },
            "Kiln.Ecs+Glimmer_Mode": { "layout": "00000000000000b2", "values": { "Off": 0, "Dim": 1, "Bright": 2 } }
            """), out var reason), reason);
        Assert.Null(reason);
    }

    [Fact]
    public void The_same_layout_set_passes_at_once()
    {
        Assert.True(Check(Stamp("""
            "Kiln.Ecs+Lamp": { "layout": "ffffffffffffffff", "fields": { "glow": "double" } }
            """, set: "1111111111111111"), out var reason), reason);
    }

    [Fact]
    public void A_changed_layout_passes_while_the_stamped_fields_are_the_same()
    {
        Assert.True(Check(Stamp("""
            "Kiln.Ecs+Lamp": { "layout": "0000000000000999", "fields": { "glow": "float", "store": "Ecs.StaticArray4<float>" } }
            """), out var reason), reason);
    }

    [Fact]
    public void A_retyped_field_is_refused_naming_type_and_field()
    {
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Lamp": { "layout": "0000000000000999", "fields": { "glow": "double" } }
            """), out var reason));
        Assert.Equal("Lanterns.dll: Kiln.Ecs+Lamp.glow changed from double to float in this Kiln.Game (server 00000000)", reason);
    }

    [Fact]
    public void A_field_that_is_gone_is_refused()
    {
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Lamp": { "layout": "0000000000000999", "fields": { "glow": "float", "flicker": "uint" } }
            """), out var reason));
        Assert.Equal("Lanterns.dll: Kiln.Ecs+Lamp.flicker (uint) is gone in this Kiln.Game (server 00000000)", reason);
    }

    [Fact]
    public void A_type_that_is_gone_is_refused()
    {
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Torch": { "layout": "0000000000000001", "fields": {} }
            """), out var reason));
        Assert.Equal("Lanterns.dll: Kiln.Ecs+Torch is not in this Kiln.Game (server 00000000)", reason);
    }

    [Fact]
    public void A_renumbered_enum_value_is_refused()
    {
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Glimmer_Mode": { "layout": "0000000000000999", "values": { "Off": 0, "Dim": 2 } }
            """), out var reason));
        Assert.Equal("Lanterns.dll: Kiln.Ecs+Glimmer_Mode.Dim changed from 2 to 1 in this Kiln.Game (server 00000000)", reason);
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Glimmer_ModeMask": { "layout": "0000000000000999", "values": { "Bright": 8 } }
            """), out reason));
        Assert.Contains("Kiln.Ecs+Glimmer_ModeMask.Bright changed from 8 to 4", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_added_enum_value_passes()
    {
        // The mod saw Off and Dim; Bright came later and changed the layout hash.
        Assert.True(Check(Stamp("""
            "Kiln.Ecs+Glimmer_Mode": { "layout": "0000000000000777", "values": { "Off": 0, "Dim": 1 } }
            """), out var reason), reason);
    }

    [Fact]
    public void A_removed_enum_value_is_refused()
    {
        Assert.False(Check(Stamp("""
            "Kiln.Ecs+Glimmer_Mode": { "layout": "0000000000000777", "values": { "Flicker": 3 } }
            """), out var reason));
        Assert.Equal("Lanterns.dll: Kiln.Ecs+Glimmer_Mode.Flicker (= 3) is gone in this Kiln.Game (server 00000000)", reason);
    }

    [Fact]
    public void Bad_json_is_refused()
    {
        Assert.False(Check("{ \"schema\": 1, ", out var reason));
        Assert.StartsWith("the layout stamp is not readable JSON: ", reason, StringComparison.Ordinal);
        Assert.False(Check("[1, 2]", out reason));
        Assert.Equal("the layout stamp is not a JSON object", reason);
    }

    [Fact]
    public void An_unknown_schema_is_refused()
    {
        Assert.False(Check("""{ "schema": 2, "assembly": "Lanterns.dll", "types": {} }""", out var reason));
        Assert.Equal("Lanterns.dll: unknown layout stamp schema 2; this Kiln.Game reads schema 1", reason);
        Assert.False(Check("""{ "types": {} }""", out reason));
        Assert.Equal("unknown layout stamp schema (none); this Kiln.Game reads schema 1", reason);
    }

    [Fact]
    public void A_null_stamp_is_a_bad_argument()
    {
        Assert.Throws<ArgumentNullException>(() => GameLayoutStamps.Check(null!, out _));
    }

    // --- the table embedded in the Kiln.Game under test --------------------------------------------------------------

    internal static Dictionary<string, string> Header(string table)
    {
        var line = table.Split('\n').First(l => l.StartsWith("kiln.game.layouts\t", StringComparison.Ordinal));
        return line.Split('\t').Skip(2).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    }

    [Fact]
    public void The_embedded_table_matches_the_generated_types()
    {
        var table = GameLayoutStamps.Table;
        Assert.StartsWith("// kiln-typegen: ", table, StringComparison.Ordinal);
        var h = Header(table);
        Assert.Equal("Ecs", h["root"]);
        var lines = table.Split('\n').Where(l => l.Length > 2 && l[1] == '\t').ToList();
        Assert.Equal(int.Parse(h["types"]), lines.Count);
        Assert.Equal(RoundTrip.Fnv64(string.Concat(lines.Select(l => l + "\n"))).ToString("x16"), h["set"]);
        var asm = GenRepo.GameAssembly;
        foreach (var l in lines)
        {
            var p = l.Split('\t');
            var t = asm.GetType(p[1]);
            Assert.True(t != null, "no type " + p[1]);
            if (p[0] != "S") continue;
            Assert.Equal(Convert.ToUInt64(p[2], 16), (ulong)t.GetField("Layout", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);
            Assert.Equal(p[3], (string)t.GetField("QualifiedName", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!);
            var fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(p.Skip(6).Select(m => m[..m.IndexOf('@')]).OrderBy(n => n, StringComparer.Ordinal), fields);
        }
    }

    [Fact]
    public void A_stamp_of_this_assembly_passes_and_a_retyped_field_of_it_is_refused()
    {
        var line = GameLayoutStamps.Table.Split('\n').FirstOrDefault(l => l.StartsWith("S\tKiln.Ecs+NetworkHealth\t", StringComparison.Ordinal));
        if (line is null) Assert.Skip("skipped: the Kiln.Game under test has no Ecs.NetworkHealth");
        var p = line.Split('\t');
        Assert.Contains("health@0:uint", p);
        Assert.True(GameLayoutStamps.Check(Stamp($$"""
            "Kiln.Ecs+NetworkHealth": { "layout": "{{p[2]}}", "fields": { "health": "uint", "healthMax": "uint" } }
            """), out var reason), reason);
        Assert.True(GameLayoutStamps.Check(Stamp("""
            "Kiln.Ecs+NetworkHealth": { "layout": "0000000000000000", "fields": { "health": "uint" } }
            """), out reason), reason);
        Assert.False(GameLayoutStamps.Check(Stamp("""
            "Kiln.Ecs+NetworkHealth": { "layout": "0000000000000000", "fields": { "health": "int" } }
            """), out reason));
        Assert.StartsWith("Lanterns.dll: Kiln.Ecs+NetworkHealth.health changed from int to uint", reason, StringComparison.Ordinal);
    }
}
