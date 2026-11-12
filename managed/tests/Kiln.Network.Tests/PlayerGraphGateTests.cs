// SPDX-License-Identifier: MIT
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>What a refused player reads when the merged player animation graphs differ.</summary>
public class PlayerGraphGateTests
{
    private const string Game = PlayerGraphGate.GameHash;
    private static readonly List<string> Server = ["kiln-equipment/cinder-guard", "kiln-equipment/cinder-slash-1"];

    [Fact]
    public void NamesWhatTheClientLacks()
    {
        var text = PlayerGraphGate.Describe("1111111111111111", Server, "2222222222222222", "kiln-equipment/cinder-guard", "1");
        Assert.Contains("the server has 2 states from mods (kiln-equipment/cinder-guard, kiln-equipment/cinder-slash-1)", text);
        Assert.Contains("your game has 1 state (kiln-equipment/cinder-guard)", text);
        Assert.Contains("you don't have kiln-equipment/cinder-slash-1", text);
        Assert.EndsWith("Join with the same mods, and restart if you just changed them.", text);
    }

    [Fact]
    public void NamesWhatOnlyTheClientHas() =>
        Assert.Contains("this server doesn't have other/dash",
            PlayerGraphGate.Describe("1111111111111111", Server, "2222222222222222",
                "kiln-equipment/cinder-guard,kiln-equipment/cinder-slash-1,other/dash", "3"));

    [Fact]
    public void ANoKilnOrOlderClientHasTheGamesGraph()
    {
        var text = PlayerGraphGate.Describe("1111111111111111", Server, Game, "", "");
        Assert.StartsWith("This server's player animations don't match your game's: the server has 2 states from mods", text);
        Assert.Contains("your game plays the game's own.", text);
    }

    [Fact]
    public void AServerWithTheGamesGraph()
    {
        var text = PlayerGraphGate.Describe(Game, [], "2222222222222222", "other/dash", "1");
        Assert.Contains("the server plays the game's own player animations, your game has 1 state (other/dash)", text);
    }

    [Fact]
    public void SameNamesOtherBytes() =>
        Assert.Contains("the same states are merged differently",
            PlayerGraphGate.Describe("1111111111111111", Server, "2222222222222222",
                "kiln-equipment/cinder-guard,kiln-equipment/cinder-slash-1", "2"));

    [Fact]
    public void ACutListNamesNothingAsExtra()
    {
        var text = PlayerGraphGate.Describe("1111111111111111", Server, "2222222222222222", "z/a", "200");
        Assert.Contains("your game has 200 states (z/a, and 199 more)", text);
        Assert.DoesNotContain("this server doesn't have", text);
        Assert.DoesNotContain("you don't have", text);
    }

    [Fact]
    public void NamesSurviveTheList()
    {
        var names = new[] { "m/a,b", "m/100%", "m/plain" };
        Assert.Equal(names, PlayerGraphGate.Split(PlayerGraphGate.Join(names)));
    }

    [Fact]
    public void AMissingOrBadHashIsTheGamesGraph()
    {
        Assert.Equal(Game, PlayerGraphGate.Hash(null));
        Assert.Equal(Game, PlayerGraphGate.Hash("xyz"));
        Assert.Equal("00000000abcdef12", PlayerGraphGate.Hash("00000000ABCDEF12"));
    }

    [Fact]
    public void WithoutTheServiceBothSidesAgree()
    {
        // No native table in a test process: this side sends sixteen zeros, and so does a peer that sends no key.
        Assert.Equal(Game, PlayerGraphGate.Properties()[PlayerGraphGate.KeyHash]);
        Assert.Null(PlayerGraphGate.Difference(new Dictionary<string, string>()));
        Assert.Null(PlayerGraphGate.Difference(new Dictionary<string, string> { [PlayerGraphGate.KeyHash] = Game }));
        Assert.NotNull(PlayerGraphGate.Difference(new Dictionary<string, string> { [PlayerGraphGate.KeyHash] = "2222222222222222" }));
    }
}
