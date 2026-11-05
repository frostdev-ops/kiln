// SPDX-License-Identifier: MIT
// What the server does about a client without Kiln (docs/kiln/network.md "Clients without Kiln"). Plain decisions, no
// game state: the transport half is native (kiln_net.h), the engine feeds these and acts on them.
namespace Kiln;

internal static class VanillaGate
{
    /// <summary>Whether a client without Kiln is refused: always with <c>requireKiln</c>; with <c>modSync: refuse</c>
    /// when anything is required; unless <c>modSync</c> is off, when a loaded mod declares <c>clients: "kiln"</c> or
    /// <c>"mod"</c> (what it adds can't be shown by a vanilla client); and, whatever the policy says, when the server
    /// has templates only Kiln has and this build can't keep them from that client: letting it in would crash it.</summary>
    internal static bool ShouldRefuse(NetworkConfig config, bool requiredExist, int kilnClientMods, uint kilnTemplates, bool canWithhold) =>
        config.RequireKiln ||
        (config.ModSync == ModSyncPolicy.Refuse && requiredExist) ||
        (config.ModSync != ModSyncPolicy.Off && kilnClientMods > 0) ||
        (kilnTemplates > 0 && !canWithhold);

    /// <summary>Whether an unproven client's Gameplay Ready waits: only when there is something to protect it from, and
    /// while the mod list isn't known yet (the first seconds after boot). Otherwise a vanilla client joins as fast as on
    /// a server without Kiln.</summary>
    internal static uint GateMode(bool modListKnown, bool refuse, uint kilnTemplates) =>
        !modListKnown || refuse || kilnTemplates > 0 ? NetNative.GateHold : NetNative.GateOff;

    /// <summary>Whether the wait for a peer's Kiln handshake is over. A peer held in the gate waits <c>vanillaHoldSeconds</c>
    /// (it has finished loading, and it gets nothing until this is decided). A peer that got past the gate, which only a
    /// build without the gate allows, waits <c>handshakeTimeoutSeconds</c> in game. Lab peers are Kiln clients unless a
    /// test puts them in the gate.</summary>
    internal static bool TimeIsUp(NetPeerState state, bool isLab, DateTime? heldSince, DateTime? inGameSince, DateTime now, NetworkConfig config)
    {
        if (state != NetPeerState.Connecting) return false;
        if (heldSince is { } held) return (now - held).TotalSeconds >= config.VanillaHoldSeconds;
        return !isLab && inGameSince is { } since && (now - since).TotalSeconds >= config.HandshakeTimeoutSeconds;
    }

    /// <summary>The mods a Kiln client is refused for under the default policy: those that state <c>clients: "mod"</c> and
    /// that it doesn't have. A <c>modSync</c> set in <c>network.json</c> decides for itself: <c>warn</c> and <c>off</c> let it
    /// in, <c>refuse</c> has its own wider rule. A value that only comes from <c>side</c> isn't in the list, nor is an
    /// optional mod.</summary>
    internal static IReadOnlyList<string> MissingStatedMods(NetworkConfig config, IEnumerable<string> statedMods, IReadOnlySet<string> clientMods) =>
        config.ModSyncStated ? [] : statedMods.Where(id => !clientMods.Contains(id) && !config.OptionalMods.Contains(id, StringComparer.Ordinal)).ToList();

    /// <summary>Whether Kiln-only entities must wait for a client's content ack: only when some exist and its content sync
    /// can bring definitions. They're sent when it acks, or when the wait runs out.</summary>
    internal static bool AwaitsDefinitions(uint kilnTemplates, bool hasContentChannel) => kilnTemplates > 0 && hasContentChannel;

    /// <summary>Whether the wait for that ack is over.</summary>
    internal static bool DefinitionsWaitOver(DateTime? since, DateTime now, NetworkConfig config) =>
        since is { } s && (now - s).TotalSeconds >= config.DefinitionsWaitSeconds;

    /// <summary>What a refused client is told. A vanilla client's game discards this frame (no code reads the channel)
    /// and shows its own login error; a Kiln build too old to prove itself shows this text.</summary>
    internal static string RefusalReason(int kilnClientMods) =>
        kilnClientMods > 0
            ? "This server's mods need Kiln on your game. Join with the Kiln launcher, with an up-to-date Kiln."
            : "This server needs Kiln on your game. Join with the Kiln launcher, with an up-to-date Kiln.";
}
