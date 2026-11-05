// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// Server: a Kiln client sent its handshake. Fires on the tick thread before Kiln answers.
/// </summary>
/// <remarks>
/// <see cref="Refuse"/> starts as the policy's decision (<c>modSync: refuse</c> and something required is missing).
/// Handlers may change it either way and set <see cref="Reason"/>. <see cref="CancelableEvent.Canceled"/> also refuses.
/// A refused client gets Kiln's REJECT (its overlay shows the reason) and then the game's login failure.
/// </remarks>
public sealed class NetClientHandshakeEvent : CancelableEvent
{
    internal NetClientHandshakeEvent(NetPeer peer, IReadOnlyList<string> missing, IReadOnlyList<string> extra, bool refuse)
    {
        Peer = peer;
        Missing = missing;
        Extra = extra;
        Refuse = refuse;
    }

    public NetPeer Peer { get; }

    /// <summary>Required server mods/channels the client lacks or has in another version ("id 1.2.0 (client 1.1.0)").</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>Client mods with <c>side: both</c> the server does not have.</summary>
    public IReadOnlyList<string> Extra { get; }

    /// <summary>Whether Kiln will refuse the client. Handlers may change it.</summary>
    public bool Refuse { get; set; }

    /// <summary>Shown to a refused client.</summary>
    public string Reason { get; set; } = "";
}

/// <summary>Server: a client finished the Kiln handshake; channels it shares are usable. Tick thread.</summary>
public sealed class NetPeerReadyEvent(NetPeer peer) : KilnEvent
{
    public NetPeer Peer { get; } = peer;
}

/// <summary>
/// Server: a client finished loading and gave no proof of Kiln within <c>vanillaHoldSeconds</c> (on a build without the
/// gate: it was in game for <c>handshakeTimeoutSeconds</c> without a handshake): a vanilla client. Tick thread.
/// <see cref="Kick"/> starts as the policy's decision; handlers may change it. Letting in a client that the server has
/// new items for can crash that client (docs/kiln/network.md "Clients without Kiln").
/// </summary>
public sealed class NetVanillaClientEvent : KilnEvent
{
    internal NetVanillaClientEvent(NetPeer peer, bool kick)
    {
        Peer = peer;
        Kick = kick;
    }

    public NetPeer Peer { get; }
    public bool Kick { get; set; }
}

/// <summary>Server: a client's connection is gone (Kiln or vanilla). Tick thread.</summary>
public sealed class NetPeerLeftEvent(NetPeer peer) : KilnEvent
{
    public NetPeer Peer { get; } = peer;
}
