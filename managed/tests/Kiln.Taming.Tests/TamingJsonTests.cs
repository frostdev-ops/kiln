// SPDX-License-Identifier: MIT
// Tamings in JSON: the same TamingDefinition as C#, every field checked by name and type with the exact path in the error,
// item ids resolved when read (items registry ids, item ids), and the engine's own validation; the capabilities a
// definition's steps need.
using Kiln;
using Xunit;

namespace Kiln.TamingTests;

public sealed class TamingJsonTests
{
    private static readonly ItemInfo Bait = new(new ItemId(0xb59dfc2c), "Food_T2_Dog_Bait", "Dog Bait", 50, default, default, false, 1, 1, 0, 0,
        Guid.Parse("8a5c1d00-0000-0000-0000-000000000001"));
    private static readonly ItemInfo Whistle = new(new ItemId(0x1234), "Tool_Whistle", "Whistle", 1, default, default, true, 1, 1, 0, 0,
        Guid.Parse("33aa0000-0000-0000-0000-000000000002"));

    private static ItemInfo? Lookup(string s) => s.ToLowerInvariant() switch
    {
        "food_t2_dog_bait" or "0xb59dfc2c" => Bait,
        "tool_whistle" or "0x00001234" or "0x1234" => Whistle,
        "dog bait" => Bait,   // a display name: never what a registry id means
        _ => null,
    };

    private static ItemId Items(string id, string path) => TamingJson.ResolveItem(id, path, Lookup);

    private const string Full = """
        {
          "id": "my-mod:wolf", "creatures": ["game:enemy_wildbeast_wolf", "game:animal_wildlife_dog_*"], "pet": "my-mod:wolf-pet",
          "steps": [
            { "kind": "weaken", "maxHealthFraction": 0.3 },
            { "kind": "feed", "item": "game:food_t2_dog_bait", "count": 2 },
            { "kind": "tool", "item": "game:tool_whistle~33aa0000", "wear": 3, "allowBreak": true },
            { "kind": "native", "steps": 2 }
          ],
          "rangeMeters": 4, "requireThresholdUntilCommit": false, "inactivitySeconds": 20, "maxAttemptSeconds": 100,
          "refund": "escrow", "removeTarget": false, "summonPet": false
        }
        """;

    [Fact]
    public void A_full_taming_reads_as_the_same_definition_csharp_makes()
    {
        var d = Assert.Single(TamingJson.Read(Full, Items));
        var want = new TamingDefinition
        {
            Id = "my-mod:wolf", Creatures = ["game:enemy_wildbeast_wolf", "game:animal_wildlife_dog_*"], Pet = "my-mod:wolf-pet",
            Steps = [new WeakenStep(0.3), new FeedStep(Bait.Id, 2), new UseToolStep(Whistle.Id, 3, true), new NativeStep(2)],
            RangeMeters = 4, RequireThresholdUntilCommit = false, InactivitySeconds = 20, MaxAttemptSeconds = 100,
            Refund = TamingRefundPolicy.Escrow, RemoveTarget = false, SummonPet = false,
        };
        Assert.True(TamingEngine.Same(want, d), $"{d}");
    }

    [Fact]
    public void Defaults_and_arrays()
    {
        var two = TamingJson.Read("""
            [ { "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "0xb59dfc2c" } ] },
              { "id": "m:b", "creatures": ["game:x"], "steps": [ { "kind": "tool", "item": "game:item_00001234" } ] } ]
            """, Items);
        Assert.Equal(2, two.Count);
        var a = two[0];
        Assert.Equal((3.0, true, 30.0, 300.0, TamingRefundPolicy.NonRefundable, (string?)null, true, true),
            (a.RangeMeters, a.RequireThresholdUntilCommit, a.InactivitySeconds, a.MaxAttemptSeconds, a.Refund, a.Pet, a.RemoveTarget, a.SummonPet));
        Assert.Equal(new FeedStep(Bait.Id, 1), a.Steps.Single());
        Assert.Equal(new UseToolStep(Whistle.Id, 1, false), two[1].Steps.Single());
    }

    [Theory]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [], "colour": 1 }""", "$.colour")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "pat" } ] }""", "$.steps[0].kind")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "game:food_t2_dog_bait", "wear": 1 } ] }""", "$.steps[0].wear")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "game:food_t2_dog_bait", "count": "two" } ] }""", "$.steps[0].count")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "game:nothing" } ] }""", "$.steps[0].item")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "game:dog bait" } ] }""", "$.steps[0].item")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "game:food_t2_dog_bait~ffffffff" } ] }""", "$.steps[0].item")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "feed", "item": "my-mod:bait" } ] }""", "$.steps[0].item")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "weaken" } ] }""", "$.steps[0].maxHealthFraction")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ], "refund": "sometimes" }""", "$.refund")]
    [InlineData("""{ "creatures": ["game:x"], "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ] }""", "$.id")]
    [InlineData("""{ "id": "m:a", "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ] }""", "$.creatures")]
    [InlineData("""{ "id": "m:a", "creatures": [1], "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ] }""", "$.creatures[0]")]
    [InlineData("""[ { "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ] }, 3 ]""", "$[1]")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "weaken", "maxHealthFraction": 2 } ] }""", "$: steps[0]")]
    [InlineData("""{ "id": "m:a", "creatures": ["game:x"], "steps": [ { "kind": "weaken", "maxHealthFraction": 0.5 } ], "rangeMeters": 20 }""", "$: rangeMeters")]
    [InlineData("""{ not json""", "$: not JSON")]
    public void A_wrong_field_names_its_path(string json, string path)
    {
        var e = Assert.Throws<ArgumentException>(() => TamingJson.Read(json, Items));
        Assert.StartsWith(path, e.Message);
    }

    [Fact]
    public void A_definition_needs_the_capabilities_of_its_steps()
    {
        var off = new HashSet<string>();
        var was = TamingCaps.Is;
        TamingCaps.Is = name => !off.Contains(name);
        try
        {
            var d = Assert.Single(TamingJson.Read(Full, Items));
            TamingCaps.RequireFor(d);
            foreach (var row in new[] { "taming.steps", "items.costs.exact", "items.costs.wear", "taming.native", "pets.records" })
            {
                off.Clear();
                off.Add(row);
                Assert.Equal(row, Assert.Throws<KilnUnavailableException>(() => TamingCaps.RequireFor(d)).Capability);
            }
            off.Clear();
            off.Add("items.costs.wear");
            TamingCaps.RequireFor(d with { Steps = [new WeakenStep(1)], Pet = null });   // a weaken-only taming needs none of them
        }
        finally
        {
            TamingCaps.Is = was;
        }
    }
}
