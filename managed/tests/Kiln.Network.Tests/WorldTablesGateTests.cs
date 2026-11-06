// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>What a refused player reads when the building-material ids differ.</summary>
public class WorldTablesGateTests
{
    private static readonly List<(int Id, string Guid, string Label)> Server =
    [
        (213, "041e7974e9d05097", "brick-first (mod first)"),
        (214, "0bc0dd6153c25d22", "brick-a (mod a)"),
    ];

    [Fact]
    public void NamesAMovedId() =>
        Assert.Contains("brick-a (mod a) is id 214 here and 213 on your game", WorldTablesGate.Describe(Server, "213=0bc0dd6153c25d22"));

    [Fact]
    public void NamesWhatTheClientLacks()
    {
        var text = WorldTablesGate.Describe(Server, "214=0bc0dd6153c25d22");
        Assert.Contains("you don't have brick-first (mod first)", text);
        Assert.DoesNotContain("brick-a", text);
    }

    [Fact]
    public void NamesWhatOnlyTheClientHas() =>
        Assert.Contains("your game has material ffff (id 215), and this server doesn't",
            WorldTablesGate.Describe(Server, "213=041e7974e9d05097,214=0bc0dd6153c25d22,215=ffff"));

    [Fact]
    public void ANoKilnClientLacksAll()
    {
        var text = WorldTablesGate.Describe(Server, "");
        Assert.StartsWith("This server's building materials don't match your game's:", text);
        Assert.Contains("you don't have brick-first", text);
        Assert.Contains("you don't have brick-a", text);
    }
}
