using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace KilnMod1;

/// <summary>In-server tests (Kiln.Testing). They run on the tick thread when the server has started (KilnTestsAutoRun),
/// or on demand: echo "test kiln-mod-id" > kiln/control/test.cmd. Results: kiln/logs/tests/kiln-mod-id.json.</summary>
public sealed class KilnMod1Tests
{
    [KilnTest]
    public void Mod_is_loaded() => Assert.True(Mods.IsLoaded("kiln-mod-id"));

    [KilnTest]
    public async Task Ticks_advance(TestContext t)
    {
        await t.Ticks(5);
        Assert.True(Game.IsTickThread);
    }
}
