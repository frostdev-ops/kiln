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
            // another player's visits are an admin's to look up; a label needs kiln.spawn, which the kit does not have
            Assert.Contains("only an admin", Said(kiln.RunCommand(bo, "visits Ada")));
            Assert.Contains("kiln.spawn", Said(kiln.RunCommand(bo, "label mine")));
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
        // no chat with no game: the strike counts, and the admin is told the warning did not go out
        Assert.Contains("first strike recorded (the warning could not be sent", Said(kiln.RunCommand(admin, "strike Bo breaking the gate")));
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
}
