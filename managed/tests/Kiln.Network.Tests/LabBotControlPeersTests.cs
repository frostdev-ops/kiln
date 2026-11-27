// SPDX-License-Identifier: MIT
using LabBotTest;
using Xunit;

namespace Kiln.Network.Tests;

/// <summary>mods/tests/labbot-test's control channel (labbot:control v1) drives bot players: joins in the admin group,
/// chat lines and commands as a bot. Only a lab peer on kiln.net's lab transport (tools/labbot, on the lab PC) may send
/// it requests; a player's game, with Kiln or not, never does (its machine is a game-transport peer).</summary>
public sealed class LabBotControlPeersTests
{
    [Fact]
    public void ALabPeerMayDriveBots() => Assert.Null(ControlPeers.Refusal(new NetPeer(NetNative.PeerLab | 2)));

    [Fact]
    public void APlayersGameNeverDrivesBots()
    {
        foreach (var machine in new uint[] { 0x2a90, 0x81, 0x10 })  // a bot's or a client's machine handle, slot 0's
        {
            var why = ControlPeers.Refusal(new NetPeer(machine));
            Assert.NotNull(why);
            Assert.Contains("lab peer", why);
            Assert.Contains($"0x{machine:x}", why);
        }
    }

    [Fact]
    public void TheServerItselfIsNoLabPeer() => Assert.NotNull(ControlPeers.Refusal(new NetPeer(NetNative.PeerServer)));
}
