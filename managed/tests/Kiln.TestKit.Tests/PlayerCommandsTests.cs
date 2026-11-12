// SPDX-License-Identifier: MIT
using TkPlayers;
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class PlayerCommandsTests
{
    private const ulong AdaSteam = 76561198000000101, BobSteam = 76561198000000102, CySteam = 76561198000000103;

    private static (KilnHarness Kiln, FakePlayer Ada, FakePlayer Bob) Server()
    {
        var kiln = new KilnHarness();
        try
        {
            kiln.Load<TkPlayersMod>();
            kiln.Step();
            var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan, position: new WorldPosition(100, 0, 0));
            var bob = kiln.Players.Join("Bob", BobSteam, position: new WorldPosition(0, 0, 0));
            kiln.Step();
            return (kiln, ada, bob);
        }
        catch
        {
            kiln.Dispose();
            throw;
        }
    }

    [Fact]
    public void AnAdminOnlyCommandIsDeniedToAPlainPlayerAndAllowedToAnAdmin()
    {
        var (kiln, ada, bob) = Server();
        using var _ = kiln;
        var denied = kiln.RunCommand(bob, "/tkp-admin");
        Assert.True(denied.Found);
        Assert.True(denied.Denied);
        Assert.False(denied.Ok);
        Assert.Equal("You may not use /tkp-admin.", denied.Error);

        var allowed = kiln.RunCommand(ada, "/tkp-admin");  // her user group may kick and ban: an admin by default
        Assert.True(allowed.Ok, allowed.Error);
        Assert.Equal(["admin ok for Ada"], allowed.Output);
        Assert.True(kiln.RunCommand(bob, "tkp-everyone").Ok);
        Assert.True(kiln.Run(() => CommandSource.ForPlayer(ada.Entity).IsAdmin));
        Assert.False(kiln.Run(() => CommandSource.ForPlayer(bob.Entity).IsAdmin));
        Assert.Equal(BobSteam, kiln.Run(() => CommandSource.ForPlayer(bob.Entity).SteamId));  // looked up through Kiln.Players
    }

    [Fact]
    public void ANodeGrantedToASteamIdInPermissionsJsonIsHonoured()
    {
        var (kiln, ada, bob) = Server();
        using var _ = kiln;
        Assert.Equal("no secret for you", kiln.RunCommand(bob, "tkp-secret").Error);
        Assert.True(kiln.RunCommand(ada, "tkp-secret").Ok);  // an admin node: admins have it

        kiln.WritePermissions($$"""
            {
              "players": {
                "{{BobSteam}}": { "name": "Bob", "nodes": ["tkplayers.secret", "tk-players.command.tkp-admin"] },
                "{{AdaSteam}}": { "nodes": ["-tkplayers.secret"] }
              }
            }
            """);
        var bobs = kiln.RunCommand(bob, "tkp-secret");
        Assert.True(bobs.Ok, bobs.Error);
        Assert.Equal(["the secret"], bobs.Output);
        Assert.True(kiln.RunCommand(bob, "tkp-admin").Ok);  // the command's own node, granted by name
        Assert.Equal("no secret for you", kiln.RunCommand(ada, "tkp-secret").Error);  // a deny beats the admin default
        var cy = kiln.Players.Join("Cy", CySteam);
        kiln.Step();
        Assert.False(kiln.RunCommand(cy, "tkp-secret").Ok);  // nobody else got it
    }

    [Fact]
    public void TheServersUserGroupsMapToKilnGroups()
    {
        var (kiln, _, bob) = Server();
        using var __ = kiln;
        var eve = kiln.Players.Join("Eve", 76561198000000105, PlayerPermissions.EditBase | PlayerPermissions.EditWorld);
        kiln.Step();
        Assert.False(kiln.RunCommand(eve, "tkp-admin").Ok);
        kiln.WriteServerConfig("""{ "userGroups": [ { "name": "Builder", "canEditBase": true, "canEditWorld": true }, { "name": "Guest" } ] }""");
        kiln.WritePermissions("""{ "groups": { "builders": { "admin": true } }, "userGroups": { "Builder": ["builders"] } }""");
        Assert.Equal("Builder", kiln.Run(() => Permissions.UserGroupOf(PermissionSubject.ForPlayer(eve.Entity))));
        Assert.True(kiln.RunCommand(eve, "tkp-admin").Ok);  // her rights are Builder's, and builders are admins
        Assert.False(kiln.RunCommand(bob, "tkp-admin").Ok);
    }

    [Fact]
    public void APlayerArgumentResolvesAgainstTheFakePlayers()
    {
        var (kiln, ada, bob) = Server();
        using var _ = kiln;
        Assert.Equal(["target Ada"], kiln.RunCommand(bob, "tkp-whois ada").Output);
        Assert.Equal(["target Bob"], kiln.RunCommand(bob, "tkp-whois @p").Output);  // nearest to Bob: Bob
        Assert.Equal(["target Ada"], kiln.RunCommand(bob, "tkp-whois @p[x=90,y=0,z=0]").Output);
        Assert.Equal(["target Bob"], kiln.RunCommand(ada, $"tkp-whois {bob.Entity.Value}").Output);
        bob.Position = new WorldPosition(99, 0, 0);  // the tick thread reads positions now
        Assert.Equal(["target Bob"], kiln.RunCommand(ada, "tkp-whois @p[x=98,y=0,z=0]").Output);
        var nobody = kiln.RunCommand(bob, "tkp-whois zed");
        Assert.False(nobody.Ok);
        Assert.True(ada.Leave());
        kiln.Step();
        Assert.False(kiln.RunCommand(bob, "tkp-whois ada").Ok);
    }

    [Fact]
    public void TheSecondHarnessDoesNotSeeTheFirstOnesGrants()
    {
        var (kiln, _, bob) = Server();
        using (kiln)
        {
            kiln.WritePermissions($$"""{ "players": { "{{BobSteam}}": { "nodes": ["tkplayers.secret"] } } }""");
            Assert.True(kiln.RunCommand(bob, "tkp-secret").Ok);
        }
        var (next, _, bob2) = Server();
        using (next)
        {
            Assert.Equal(BobSteam, bob2.SteamId);
            Assert.Equal("no secret for you", next.RunCommand(bob2, "tkp-secret").Error);
            Assert.DoesNotContain(BobSteam.ToString(), File.ReadAllText(Path.Combine(next.ConfigDirectory, "permissions.json")));
        }
    }
}
