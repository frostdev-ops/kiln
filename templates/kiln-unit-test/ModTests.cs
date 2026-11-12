using Kiln;
using Kiln.TestKit;
using Xunit;

namespace KilnUnitTest1;

/// <summary>The mod under a KilnHarness: a Kiln server with no game. kiln.Load loads a second, isolated copy of the mod's
/// assembly, as the game does, with its own kiln.mod.json; Step() runs one server tick (virtual time, 40.8 ms). Kiln API
/// is called on the harness's tick thread: kiln.Run(() => ...). Docs: docs/kiln/modding/unit-tests.md.</summary>
public sealed class ModTests
{
    [Fact]
    public void The_mod_loads_steps_and_sees_a_player()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<KILN_MOD_TYPE>();
        kiln.Step();                                    // the first step: the mods load and the server starts
        Assert.True(mod.IsLoaded, mod.Error ?? kiln.DumpLogs());
        Assert.True(kiln.HasLog("loaded", mod.Id), kiln.DumpLogs());   // a line of the mod's own OnLoad: use one yours writes

        Assert.True(kiln.RunCommand("help").Ok);       // a console command, through Kiln's command registry

        var ada = kiln.Players.Join("Ada");            // a fake player: in game at the next step
        kiln.Step();
        Assert.Equal(1, kiln.Run(() => Players.Count));
        ada.Leave();
        kiln.Step();
        Assert.Equal(0, kiln.Run(() => Players.Count));
    }                                                   // Dispose stops the server: ServerStopping, then OnUnload
}
