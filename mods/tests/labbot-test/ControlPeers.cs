// SPDX-License-Identifier: MIT
using Kiln;

namespace LabBotTest;

/// <summary>Who may drive bots over labbot:control: a lab peer only, on kiln.net's lab transport (tools/labbot, a UDP
/// socket on the lab PC's 127.0.0.1). A player's game, with Kiln or not, is a peer of the game transport and never may:
/// the channel joins bots in the admin group and runs commands as them, so a player who sent it requests would hold an
/// admin's rights. Kiln.Network.Tests' LabBotControlPeersTests compile this file.</summary>
internal static class ControlPeers
{
    /// <summary>Null when <paramref name="sender"/> may send requests; otherwise why not.</summary>
    internal static string? Refusal(NetPeer sender) =>
        sender.IsLab
            ? null
            : $"labbot:control takes requests from a lab peer only (tools/labbot on kiln.net's lab transport), not from " +
              (sender.IsServer ? "the server" : $"the game connection 0x{sender.Id:x} ('{sender.Name}')");
}
