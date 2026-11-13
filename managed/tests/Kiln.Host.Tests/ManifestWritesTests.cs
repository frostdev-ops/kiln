// SPDX-License-Identifier: MIT
// kiln.mod.json "writes": the component types a mod writes through kiln.world's tracked path (docs/kiln/modding/manifest.md).
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.Host.Tests;

public class ManifestWritesTests
{
    private const string Head = "{ \"id\": \"a\", \"version\": \"1.0.0\", \"apiVersion\": \"0.2\", \"writes\": ";

    [Fact]
    public void Writes_is_a_list_of_component_names_read_once_each_in_order()
    {
        var m = Manifest.Parse(Head + "[\"Fuel\", \"CurrentTransform\", \"Fuel\"] }");
        Assert.Equal(["Fuel", "CurrentTransform"], m.Writes);
        Assert.Empty(m.Warnings);   // a known key
        Assert.Empty(Manifest.Parse("{ \"id\": \"a\", \"version\": \"1.0.0\", \"apiVersion\": \"0.2\" }").Writes);
    }

    [Theory]
    [InlineData("[\"keen::ecs::Fuel\"]", "short name")]
    [InlineData("[\"two words\"]", "short name")]
    [InlineData("\"Fuel\"", "list of at most 64")]
    [InlineData("[7]", "short name")]
    public void Anything_else_is_not_read_and_the_mod_still_loads_with_a_warning(string value, string says)
    {
        var m = Manifest.Parse(Head + value + " }");
        Assert.Empty(m.Writes);
        Assert.Contains(says, m.WritesError);
        var warning = Assert.Single(m.Warnings);
        Assert.Contains("\"writes\" is not read", warning);
        Assert.Contains("the mod loads", warning);
    }

    // The review's three manifests: each loaded on 0.2.0-dev.26, where "writes" was a key nobody read, and still loads.
    [Theory]
    [InlineData("null", "not null")]
    [InlineData("[\"Fuel\\n\"]", "'Fuel\\n' is not a component's short name")]
    [InlineData("65 copies", "it lists 65 entries")]
    public void A_manifest_that_loaded_before_tracked_writes_still_loads(string writes, string says)
    {
        var value = writes == "65 copies" ? "[" + string.Join(",", Enumerable.Repeat("\"Fuel\"", 65)) + "]" : writes;
        var m = Manifest.Parse(Head + value + " }");
        Assert.Equal("a", m.Id);
        Assert.Empty(m.Writes);
        Assert.Contains(says, m.WritesError);
        Assert.Contains(m.Warnings, w => w.Contains("tracked writes are refused (the mod loads)"));
    }

    // native/services/world/tests/parity.json: the cases the native reader (writes_test.cpp) is tested on too.
    public static TheoryData<string> ParityCases() => [.. Parity()["manifests"]!.AsArray().Select(c => (string)c!["case"]!)];

    [Theory]
    [MemberData(nameof(ParityCases))]
    public void The_managed_reader_agrees_with_the_native_one(string name)
    {
        var c = Parity()["manifests"]!.AsArray().Single(x => (string)x!["case"]! == name)!;
        var manifest = new JsonObject { ["id"] = "a", ["version"] = "1.0.0", ["apiVersion"] = "0.2" };
        var fields = c["manifest"]!.AsObject();
        if (fields.TryGetPropertyValue("writes", out var writes))
            manifest["writes"] = writes is JsonObject spec ? new JsonArray([.. Expand(spec).Select(n => (JsonNode?)n)]) : writes?.DeepClone();
        // valid false: the list is refused and the mod still loads (the native reader throws to its caller, which then
        // refuses only the mod's tracked writes)
        var m = Manifest.Parse(manifest.ToJsonString());
        Assert.Equal((bool)c["valid"]!, m.WritesError is null);
        if (m.WritesError is not null)
        {
            Assert.Empty(m.Writes);
            Assert.Contains(m.Warnings, w => w.Contains("\"writes\" is not read"));
            return;
        }
        if (c["writes"] is JsonArray list) Assert.Equal(list.Select(n => (string)n!), m.Writes);
        if (c["count"] is { } count) Assert.Equal((int)count, m.Writes.Count);
    }

    private static IEnumerable<string> Expand(JsonObject spec)
    {
        var pattern = (string)spec["pattern"]!;
        var from = spec["from"] is { } f ? (int)f : 0;
        for (var i = from; i < from + (int)spec["count"]!; i++) yield return pattern.Replace("{i}", i.ToString());
    }

    private static JsonNode Parity([System.Runtime.CompilerServices.CallerFilePath] string self = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(Path.Combine(dir, "native", "services", "world"))) { repo = dir; break; }
        return JsonNode.Parse(File.ReadAllText(Path.Combine(repo, "native", "services", "world", "tests", "parity.json")))!;
    }

    [Fact]
    public void A_mod_lists_64_types_at_most_as_the_native_reader_takes()
    {
        string List(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"T{i}\"")) + "]";
        Assert.Equal(Manifest.MaxWrites, Manifest.Parse(Head + List(Manifest.MaxWrites) + " }").Writes.Count);
        var over = Manifest.Parse(Head + List(Manifest.MaxWrites + 1) + " }");
        Assert.Empty(over.Writes);
        Assert.Contains("64 at most", over.WritesError);
    }
}
