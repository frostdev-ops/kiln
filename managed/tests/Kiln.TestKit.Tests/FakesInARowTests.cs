// SPDX-License-Identifier: MIT
using TkPlayers;
using TkSaves;
using Xunit;

namespace Kiln.TestKit.Tests;

/// <summary>The faked areas over harnesses one after the other in one process (the normal case under xunit): each starts as
/// fresh as the first, whatever the one before did.</summary>
public sealed class FakesInARowTests
{
    private const ulong AdaSteam = 76561198000000301, BobSteam = 76561198000000302;

    [Fact]
    public void TheThirdHarnessInARowIsAsFreshAsTheFirst()
    {
        var worlds = new HashSet<Guid>();
        ulong lastSeq = 0;
        for (var i = 0; i < 3; i++)
        {
            using var kiln = new KilnHarness();
            Assert.True(worlds.Add(kiln.World.Id));
            Assert.Equal(0u, kiln.World.Revision);
            kiln.Load<TkPlayersMod>();
            kiln.Load<TkSavesMod>();
            var ada = kiln.Players.Join("Ada", AdaSteam, PlayerPermissions.KickBan);
            kiln.Step(2);
            var bob = kiln.Players.Join("Bob", BobSteam);
            kiln.Step();

            // players: only this harness's, joined at its own steps, sequence numbers going on
            Assert.Equal(["present: Ada"], kiln.Lines("tk-players", "present:"));  // in game at step 1's scan, before the mods load
            Assert.Equal(["joined: Bob"], kiln.Lines("tk-players", "joined:").Select(l => l[..11]).ToList());
            Assert.Equal(["Ada", "Bob"], kiln.Run(() => Players.All).Select(p => p.Name));
            var seq = kiln.Run(() => Players.Diagnostics.Status!.LastSequence);
            Assert.True(seq > lastSeq);
            lastSeq = seq;

            // permissions: no grant of an earlier harness
            Assert.False(kiln.RunCommand(bob, "tkp-secret").Ok);
            Assert.False(kiln.RunCommand(bob, "tkp-admin").Ok);
            Assert.True(kiln.RunCommand(ada, "tkp-admin").Ok);
            kiln.WritePermissions($$"""{ "players": { "{{BobSteam}}": { "nodes": ["tkplayers.secret"] } } }""");
            Assert.True(kiln.RunCommand(bob, "tkp-secret").Ok);

            // saved data: a new world with nothing in it, its own folder, the save cycle from revision 1
            Assert.Equal([$"load: world {kiln.World.Id} loaded None box 0 keys box"], kiln.Lines("tk-saves", "load:"));
            Assert.Equal(["none"], kiln.RunCommand("tk-saves-get kept").Output);
            Assert.Equal(["none"], kiln.RunCommand($"tk-saves-pget {AdaSteam}").Output);
            Assert.Equal(["default at " + Path.Combine(kiln.World.Directory, "config")], kiln.RunCommand("tk-saves-config").Output.Select(l => l[..l.LastIndexOf(Path.DirectorySeparatorChar)]));
            kiln.RunCommand("tk-saves-set kept 1");
            kiln.RunCommand($"tk-saves-pset {AdaSteam} 1");
            Assert.Equal(1u, kiln.World.Save());
            Assert.StartsWith(kiln.ServerDirectory, kiln.Run(() => Saves.SaveDirectory));
            Assert.Equal(kiln.World.Id.ToString(), kiln.Run(() => Saves.WorldId));
            // the next harness gets the shutdown save announcement too
            kiln.Stop();
            Assert.Single(kiln.Lines("tk-saves", "saving: Shutdown revision 2"));
        }
    }
}
