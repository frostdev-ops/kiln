// SPDX-License-Identifier: MIT
// kiln.mod.json "writes": the component types a mod writes through kiln.world's tracked path (docs/kiln/modding/manifest.md).
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

    [Fact]
    public void A_mod_lists_64_types_at_most_as_the_native_reader_takes()
    {
        string List(int n) => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"T{i}\"")) + "]";
        Assert.Equal(Manifest.MaxWrites, Manifest.Parse(Head + List(Manifest.MaxWrites) + " }").Writes.Count);
        Assert.Contains("64 at most", Assert.Throws<InvalidDataException>(() => Manifest.Parse(Head + List(Manifest.MaxWrites + 1) + " }")).Message);
    }
}
