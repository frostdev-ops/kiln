// SPDX-License-Identifier: MIT
// The samples of docs/kiln/modding/README.md that the kit can run with no game: what they keep, what they answer when a
// service is not there. A sample is what a mod author copies, so it must hold to the contract it shows.
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class SampleModsTests
{
    private const ulong AdaSteam = 76561198000000301, BoSteam = 76561198000000302;

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kiln-testkit-samples-{Guid.NewGuid():N}");

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    // what the source reads: the replies, and the refusal when the command threw a CommandException
    private static string Said(CommandResult r) => string.Join("\n", r.Output.Append(r.Error ?? ""));

    [Fact]
    public void SavedDataKeepsItsGuestbookAndVisitsThroughARestart()
    {
        using var folder = new Folder();
        Guid world;
        using (var kiln = new KilnHarness(new KilnHarnessOptions { ServerDirectory = folder.Path }))
        {
            kiln.LoadFolder(TestMods.Folder("SavedData"));
            world = kiln.World.Id;
            var ada = kiln.Players.Join("Ada", AdaSteam);
            kiln.Step();
            Assert.Contains("Ada: 1 visit(s)", Said(kiln.RunCommand(ada, "visits")));
            ada.Leave();
            kiln.Step();
            ada = kiln.Players.Join("Ada", AdaSteam);
            var bo = kiln.Players.Join("Bo", BoSteam);
            kiln.Step();
            Assert.Contains("Ada: 2 visit(s)", Said(kiln.RunCommand(ada, "visits")));
            Assert.Contains("3 join(s)", Said(kiln.RunCommand(bo, "guestbook")));
            // another player's visits are an admin's to look up; a label needs a chest next to the player (SampleFakesTests
            // labels one on the kit's fakes of kiln.spawn and kiln.items)
            Assert.Contains("only an admin", Said(kiln.RunCommand(bo, "visits Ada")));
            Assert.Contains("no chest within", Said(kiln.RunCommand(bo, "label mine")));
            kiln.World.Save();
        }
        using (var kiln = new KilnHarness(new KilnHarnessOptions { ServerDirectory = folder.Path, WorldId = world }))
        {
            kiln.LoadFolder(TestMods.Folder("SavedData"));
            kiln.Step();
            // nobody is online: the console looks the visitor up by name, from what the world's save held
            Assert.Contains("Ada: 2 visit(s)", Said(kiln.RunCommand("visits Ada")));
            Assert.Contains("3 join(s)", Said(kiln.RunCommand("guestbook")));
            Assert.Contains("Bo, Ada", Said(kiln.RunCommand("guestbook")));
        }
    }

    [Fact]
    public void AdminToolsCountsStrikesAndSaysWhatAServerCannotDo()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("AdminTools"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        var bo = kiln.Players.Join("Bo", BoSteam);
        kiln.Step();
        Assert.False(kiln.RunCommand(bo, "strike Ada griefing").Ok);  // a command for admins
        // a reason too long for the kick or ban it may lead to is refused before anything is counted
        Assert.Contains("at most 180 characters", Said(kiln.RunCommand(admin, "strike Bo " + new string('x', 181))));
        Assert.DoesNotContain("Bo", Said(kiln.RunCommand(admin, "strikes")));
        // the kit fakes kiln.chat since 0.2.0-dev.27: the warning reaches Bo's chat (SampleFakesTests reads it)
        Assert.Contains("Bo: first strike, warned", Said(kiln.RunCommand(admin, "strike Bo breaking the gate")));
        // with no game there is no kick and no ban list: the strikes are still counted, and the reply says what was not done
        Assert.Contains("this server cannot kick (players.kick is off)", Said(kiln.RunCommand(admin, "strike Bo again")));
        Assert.Contains("keeps no ban list (players.bans is off)", Said(kiln.RunCommand(admin, "strike Bo a third time")));
        Assert.Contains("Bo (76561198000000302): 3 strike(s), last: a third time", Said(kiln.RunCommand(admin, "strikes")));
        Assert.Contains("strikes forgotten", Said(kiln.RunCommand(admin, "pardon Bo")));
        Assert.DoesNotContain("Bo", Said(kiln.RunCommand(admin, "strikes")));
        // NoClip needs the game's hook: the sample says so instead of calling
        Assert.Contains("rules.noclip is off", Said(kiln.RunCommand(admin, "scout")));
    }

    [Fact]
    public void CalmBellAndQuietServerSayWhenTheirServiceIsNotThere()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("CalmBell"));
        kiln.LoadFolder(TestMods.Folder("QuietServer"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        kiln.Step();
        Assert.Contains("offers are not available", Said(kiln.RunCommand(admin, "bell")));
        Assert.Contains("not active", Said(kiln.RunCommand(admin, "quiet")));
    }

    [Fact]
    public void LoadoutsAndSkyControlSayWhatAServerCannotDo()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("Loadouts"));
        kiln.LoadFolder(TestMods.Folder("SkyControl"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        kiln.Step();
        // with no game there are no items to read and no weather to choose: each command says so and changes nothing
        Assert.Contains("only a player", Said(kiln.RunCommand("loadout save town")));
        Assert.Contains("items.instances is off", Said(kiln.RunCommand(admin, "loadout save town")));
        Assert.Contains("items.instances is off", Said(kiln.RunCommand(admin, "loadout wear town")));
        Assert.Contains("items.instances is off", Said(kiln.RunCommand(admin, "loadout off Ring0")));
        Assert.Contains("no slot Hat", Said(kiln.RunCommand(admin, "loadout off Hat")));
        Assert.Contains("no slot 99", Said(kiln.RunCommand(admin, "loadout off 99")));
        Assert.Contains("time.weather_choose is off", Said(kiln.RunCommand(admin, "sky")));
        Assert.Contains("time.weather_choose is off", Said(kiln.RunCommand(admin, "sky rain 10m")));
    }

    [Fact]
    public void EngineRoomSaysWhatIsNotThereWithNoGame()
    {
        using var kiln = new KilnHarness();
        kiln.LoadFolder(TestMods.Folder("EngineRoom"));
        var admin = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
        var bo = kiln.Players.Join("Bo", BoSteam);
        kiln.Step();
        Assert.False(kiln.RunCommand(bo, "engine claims").Ok);  // a command for admins
        // with no game there are no systems and no resources: each part says so
        Assert.Contains("are not known here", Said(kiln.RunCommand(admin, "engine systems")));
        Assert.Contains("nothing is watched", Said(kiln.RunCommand(admin, "engine watch")));
        Assert.Contains("not known on this build", Said(kiln.RunCommand(admin, "engine profile")));
        Assert.Contains("not known on this build", Said(kiln.RunCommand(admin, "engine watch stamina")));
        Assert.Contains("cannot be read here", Said(kiln.RunCommand(admin, "engine stack Weapon_T1_1H_SwordEpic_01 5")));
        Assert.Contains("systems [word]", Said(kiln.RunCommand(admin, "engine nonsense")));
        // the claim list works without the game: nothing is held, and giving back nothing is no error
        Assert.Contains("0 claim(s) in force", Said(kiln.RunCommand(admin, "engine claims")));
        Assert.Contains("0 claim(s) given back", Said(kiln.RunCommand(admin, "engine undo")));
    }
}
