// SPDX-License-Identifier: MIT
// Host-only tests: compile the actual never-shipped producer against its real managed API dependencies.
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Kiln;
using RegistryProofTest;
using Xunit;

public class ProducerTests
{
    // Coordinator ruling (2026-11-23): evidence only for the config kinds the build has, so the producer counts them; an
    // entry that holds the value but whose kind cannot be read counts as "unknown", never as absent. review3 S1: an entry
    // of the id table the lab's world does not hold (nothing reads it) is "notInWorld", never "no config".
    [Theory]
    [InlineData(ContentValueType.Int, true, true, "sint32")]
    [InlineData(ContentValueType.Float, true, true, "float")]
    [InlineData(ContentValueType.Int, false, true, "sint32")]
    [InlineData(ContentValueType.Bool, true, true, "unknown")]
    [InlineData(null, true, true, "unknown")]
    [InlineData(null, false, true, null)]
    [InlineData(null, false, false, "notInWorld")]
    [InlineData(ContentValueType.Int, false, false, "sint32")]
    [InlineData(null, true, false, "unknown")]
    public void Config_kinds_are_counted_and_unreadable_ones_are_unknown(ContentValueType? type, bool holds, bool inWorld, string? expected)
    {
        var method = typeof(RegistryProofTestMod).GetMethod("ConfigKind", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (string?)method.Invoke(null, new object?[] { type, holds, inWorld }));
    }

    // review2 S6: refusals that show a mapping disagreeing with the build are an error sample (changed); guard, range and
    // entry-specific refusals are missing evidence.
    [Theory]
    [InlineData("not a field of the schema", true)]
    [InlineData("the game member impactValues is missing on this game build", true)]
    [InlineData("the member raw:x is not a writable member on this game build", true)]
    [InlineData("\"epic\" is not a value of rarity on this game build", true)]
    [InlineData("Unproven: items.comfort is not proven for game build 1 (x); this edit was not applied", false)]
    [InlineData("Unmapped: items.power is not mapped for game build 1 yet (a Kiln update is needed: not a field of the schema)", false)]
    [InlineData("not_exact: items.manaCost of game:x is a whole number on this build; 2.5 was not written", false)]
    [InlineData("the result 70000 over the value below is outside the field's range", false)]
    [InlineData("the config value is not readable", false)]
    [InlineData("", false)]
    public void Structural_refusals_are_disagreements(string reason, bool expected)
    {
        var method = typeof(RegistryProofTestMod).GetMethod("IsDisagreement", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (bool)method.Invoke(null, new object[] { reason })!);
    }

    // review3 N1/N2: the producer's unsampled reasons become the record's, shown inside kiln.content's parentheses: no
    // nested parentheses, and an all-empty field says so (not "no lab round trip").
    [Theory]
    [InlineData("NoDropTables")]
    [InlineData("NoValue")]
    public void Unsampled_reasons_read_inside_parentheses(string name)
    {
        var text = (string)typeof(RegistryProofTestMod).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.DoesNotContain("(", text);
        Assert.DoesNotContain("no lab round trip", text);
    }

    // review2 N5: an impact config is compared raw; a raw read that fails stays null, never the resource digest
    [Fact]
    public void Impact_configs_are_compared_raw()
    {
        var field = typeof(RegistryProofTestMod).GetField("Compared", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Contains("impactConfig", (HashSet<string>)field.GetValue(null)!);
    }

    // review2 N4: an impactConfig row naming a config Kiln has no id for gets no probe, never StaminaCost's id.
    [Theory]
    [InlineData("ManaCost", "0xaf236ece")]
    [InlineData("StaminaCost", "0x727d7f78")]
    [InlineData("HealthCost", null)]
    [InlineData(null, null)]
    public void Config_ids_refuse_unknown_names(string? config, string? expected)
    {
        var method = typeof(RegistryProofTestMod).GetMethod("ConfigId", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, (string?)method.Invoke(null, new object?[] { config }));
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(2.5)]
    [InlineData(0.0)]
    public void Float_config_probe_is_fractional_and_changes_the_value(double before)
    {
        var probe = typeof(RegistryProofTestMod).GetMethod("Probe", BindingFlags.Static | BindingFlags.NonPublic)!;
        var value = (JsonNode)probe.Invoke(null, new object[] { JsonValue.Create(before)!,
            new JsonObject { ["conv"] = "impactConfig" }, Array.Empty<string>() })!;
        var number = value.GetValue<double>();
        Assert.NotEqual(Math.Floor(number), number);
        Assert.NotEqual(before, number);
    }

    // Reflection exposes a struct with one scalar (GemSlotOverride {isSet}) at the struct's own path, so the raw read of
    // items.socket's "gemSlotOverride.isSet" falls back to "gemSlotOverride" (only for a Boolean, see Raw).
    [Theory]
    [InlineData("gemSlotOverride.isSet", "gemSlotOverride.isSet|gemSlotOverride")]
    [InlineData("armorSetup.isSet", "armorSetup.isSet|armorSetup")]
    [InlineData("impactValues", "impactValues")]
    [InlineData("isSet", "isSet")]
    public void Raw_paths_include_the_flattened_one_flag_struct(string path, string expected)
    {
        var method = typeof(RegistryProofTestMod).GetMethod("RawPaths", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(expected, string.Join("|", (IEnumerable<string>)method.Invoke(null, new object[] { path })!));
    }

    [Theory]
    [InlineData(2, "not_exact: value cannot be represented", true)]
    [InlineData(0, "not_exact: value cannot be represented", false)]
    [InlineData(2, "not_exact:value cannot be represented", false)]
    [InlineData(2, "unproven: no mapping", false)]
    [InlineData(2, "value cannot be represented exactly", false)]
    public void Exact_refusal_needs_both_the_outcome_and_the_stable_token(int outcome, string reason, bool expected)
    {
        var type = typeof(RegistryProofTestMod);
        var reply = type.GetNestedType("Reply", BindingFlags.NonPublic)!;
        var method = type.GetMethod("IsExactRefusal", BindingFlags.NonPublic | BindingFlags.Static)!;
        var memory = Marshal.AllocHGlobal(232);
        try
        {
            Marshal.Copy(new byte[232], 0, memory, 232);
            Marshal.WriteInt32(memory, 4, outcome);
            var bytes = Encoding.UTF8.GetBytes(reason);
            Marshal.Copy(bytes, 0, memory + 40, bytes.Length);
            Assert.Equal(expected, (bool)method.Invoke(null, new[] { Marshal.PtrToStructure(memory, reply) })!);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static object? Call(string name, params object?[] args) =>
        typeof(RegistryProofTestMod).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);

    // A drop entry's leaves are flat keys with dots ({"count.min": 1}, kiln.registry.live's struct), nested ones are read too.
    [Fact]
    public void Element_leaves_read_and_write_flat_dotted_keys()
    {
        var el = JsonNode.Parse("""{"chance":0.5,"count.max":3,"count.min":1,"item":"game:x","weight":1}""")!.AsObject();
        Assert.Equal(1, ((JsonNode)Call("Leaf", el, "count.min")!).GetValue<int>());
        Call("SetLeaf", el, "count.min", JsonValue.Create(2));
        Assert.Equal(2, el["count.min"]!.GetValue<int>());
        Assert.Null(el["count"]);
        var nested = JsonNode.Parse("""{"count":{"min":4}}""")!.AsObject();
        Assert.Equal(4, ((JsonNode)Call("Leaf", nested, "count.min")!).GetValue<int>());
    }

    // A bitmask's raw value is a list of the game's names (regmap/README.md), also with one flag or none set: kiln.content's
    // name text is "Common,Rare", "Epic" or "".
    [Theory]
    [InlineData("Common,Rare", """["Common","Rare"]""")]
    [InlineData("Epic", """["Epic"]""")]
    [InlineData("", "[]")]
    public void Flags_raw_values_are_lists(string names, string expected) =>
        Assert.Equal(expected, ((JsonNode)Call("FlagNames", names)!).ToJsonString());

    // A drop entry probe changes a leaf within its range: chance 1.0 + 1 would leave 0..1, a count's + 1 never does.
    [Fact]
    public void Drop_entry_probe_stays_in_range()
    {
        var list = JsonNode.Parse("""[{"chance":1.0,"count.max":3,"count.min":1,"item":"game:x","weight":1}]""")!;
        var probe = (JsonArray)Call("Probe", list, new JsonObject { ["conv"] = "inventoryWalk" }, Array.Empty<string>())!;
        var el = probe[0]!.AsObject();
        Assert.Equal(4, el["count.max"]!.GetValue<long>());
        Assert.Equal(1.0, el["chance"]!.GetValue<double>());
    }

    // After a range refusal of a float probe (1.25 x), the producer tries 0.75 x on the same entry; whole-number and other
    // kinds have no second probe.
    [Theory]
    [InlineData("float", 1.0, 0.75)]
    [InlineData("float", 0.25, 0.1875)]
    [InlineData("int", 3.0, null)]
    public void A_float_probe_refused_for_its_range_is_tried_lower(string conv, double before, double? expected)
    {
        var lower = (JsonNode?)Call("LowerProbe", JsonValue.Create(before), new JsonObject { ["conv"] = conv });
        Assert.Equal(expected, lower?.GetValue<double>());
    }

    // review registry-proof: an optional enum's probe from "none" turns the switch on with a rarity whose game enum differs
    // from the enum below it (the raw [switch, enum]), so the round trip writes both members: "common" over an entry whose
    // enum below is already Common wrote the switch alone (the 31.x lab sample). From a rarity it probes "none".
    [Theory]
    [InlineData("\"none\"", "[false,\"Common\"]", "\"uncommon\"")]
    [InlineData("\"none\"", "[false,\"Rare\"]", "\"common\"")]
    [InlineData("\"none\"", "[false,\"common\"]", "\"uncommon\"")]
    [InlineData("\"none\"", null, "\"common\"")]
    [InlineData("\"epic\"", "[true,\"Epic\"]", "\"none\"")]
    public void An_optional_enum_probe_changes_the_enum_below_too(string read, string? raw, string expected)
    {
        var row = JsonNode.Parse("""{"conv":"optionalEnum","values":{"none":"","common":"Common","uncommon":"Uncommon","rare":"Rare","epic":"Epic","legendary":"Legendary"}}""")!.AsObject();
        var probe = (JsonNode?)Call("ProbeOf", JsonNode.Parse(read), raw is null ? null : JsonNode.Parse(raw)!.AsArray(), row, Array.Empty<string>());
        Assert.Equal(expected, probe?.ToJsonString());
    }

    // Every other conversion probes as Probe does: ProbeOf only adds what the raw value tells.
    [Fact]
    public void Other_probes_are_unchanged()
    {
        var row = new JsonObject { ["conv"] = "int" };
        Assert.Equal("4", ((JsonNode)Call("ProbeOf", JsonNode.Parse("3"), JsonNode.Parse("[3]")!.AsArray(), row, Array.Empty<string>())!).ToJsonString());
    }

    // The roll's scaled stack (0x16db30): n x the world's loot amount, at least 1, at most maxScaled.
    [Theory]
    [InlineData(3, 2.0, 1000, 6)]
    [InlineData(3, 2.0, 4, 4)]
    [InlineData(3, 1.0, 1000, 3)]
    [InlineData(0, 2.0, 10, 1)]
    public void Scaled_stack_is_clamped(int n, double amount, int maxScaled, int expected) =>
        Assert.Equal(expected, (int)Call("ScaledCount", n, amount, maxScaled)!);

    // dev.32 skills: the producer takes the skill nodes from the id table's number keys (the nodes have no GUID), in table order
    [Fact]
    public void Skill_nodes_come_from_the_id_tables_number_keys()
    {
        var table = new JsonObject { ["#1148578013"] = "game:skill_4475e8dd", ["0002f740-86a7-4764-ad8e-06423588a054"] = "game:x", ["#abc"] = "game:bad",
            ["#2398930034"] = "game:skill_8efcc472" };
        var method = typeof(RegistryProofTestMod).GetMethod("SkillNodes", BindingFlags.Static | BindingFlags.NonPublic)!;
        var nodes = (List<(uint Node, string Id)>)method.Invoke(null, new object[] { table })!;
        Assert.Equal(new List<(uint, string)> { (0x4475e8ddu, "game:skill_4475e8dd"), (0x8efcc472u, "game:skill_8efcc472") }, nodes);
    }

    // dev.32 skills: the read-time effect reads a node's cost from kiln.skills' dump of the game's tree (its NodeById view)
    [Fact]
    public void A_nodes_cost_is_read_from_the_game_trees_dump()
    {
        const string dump = """{"world":"server","nodes":[{"index":0,"id":"0x8efcc472","cost":0},{"index":1,"id":"0x4475E8DD","cost":3},{"index":2,"id":"0x00000001","missing":true}]}""";
        var method = typeof(RegistryProofTestMod).GetMethod("TreeCostOf", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(3L, (long?)method.Invoke(null, new object[] { dump, 0x4475e8ddu }));
        Assert.Equal(0L, (long?)method.Invoke(null, new object[] { dump, 0x8efcc472u }));
        Assert.Null((long?)method.Invoke(null, new object[] { dump, 1u }));
        Assert.Null((long?)method.Invoke(null, new object[] { dump, 2u }));
    }
}
