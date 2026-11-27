// SPDX-License-Identifier: MIT
#pragma warning disable KILNX001  // LabBotInfo is lab-only API (Kiln.Network.Tests compiles this file too)
using Kiln;

namespace LabBotTest;

/// <summary>Whether a connection kiln.net judges as a client without Kiln is one of the lab's bots. A bot sends no Kiln
/// frame, so on a lab whose mods state <c>clients: "kiln"</c> or <c>"mod"</c> (or with <c>requireKiln</c>, or
/// <c>modSync: refuse</c> and required mods) kiln.net would hold its Gameplay Ready and refuse it, and the join would fail
/// a minute later with a record-state reason. A bot has no client a Kiln-only template could crash, so the mod lets a
/// live bot's machine in (<see cref="NetVanillaClientEvent.Kick"/> false) and no other connection. Kiln.Network.Tests'
/// LabBotPeersTests compile this file.</summary>
internal static class BotPeers
{
    /// <summary>True when <paramref name="peer"/> is the machine of a live bot of <paramref name="bots"/>.</summary>
    internal static bool IsBot(uint peer, IEnumerable<LabBotInfo> bots) => bots.Any(b => !b.Ended && b.Machine == peer);

    /// <summary>True when <paramref name="now"/> (LabBots.Info of the bot's id now) is still the bot <paramref name="was"/>
    /// in game in the same player entity. An op that waits (use, give) checks it before it acts: a bot that left frees its
    /// player slot, and the next player in that slot, another bot or a player's game, gets the same entity id.</summary>
    internal static bool Still(LabBotInfo was, LabBotInfo? now) =>
        now is { InGame: true } && now.Id == was.Id && now.Entity == was.Entity;
}
