using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace FrostMod;

public sealed class FrostTests
{
    [KilnTest]
    public void Blade_is_cold() => Assert.True(true);

    [KilnTest(TimeoutSeconds = 5)]
    public async Task Shard_melts(TestContext t)
    {
        await t.Ticks(1);
        Assert.Fail("it did not melt");
    }

    [KilnTest(Skip = "needs a player")]
    public void Needs_a_player() { }
}
