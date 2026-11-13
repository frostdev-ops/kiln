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
    [InlineData("\"Fuel\"", "array of strings")]
    [InlineData("[7]", "non-empty strings")]
    public void Anything_else_is_refused_with_the_field_named(string value, string says)
    {
        var e = Assert.Throws<InvalidDataException>(() => Manifest.Parse(Head + value + " }"));
        Assert.Contains("writes", e.Message);
        Assert.Contains(says, e.Message);
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
        Manifest? m = null;
        try { m = Manifest.Parse(manifest.ToJsonString()); }
        catch (InvalidDataException) { }
        Assert.Equal((bool)c["valid"]!, m is not null);
        if (m is null) return;
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
        Assert.Contains("64 at most", Assert.Throws<InvalidDataException>(() => Manifest.Parse(Head + List(Manifest.MaxWrites + 1) + " }")).Message);
    }
}
