// SPDX-License-Identifier: MIT
// SteamGuardRelay: a server asks a player's own game client to turn its achievement guard on before the server
// changes that player's level, experience, skill points or knowledge. The guard that matters is the one in the
// process that talks to Steam, which is the player's client. On a hosting client the affected player may be the local
// one, and a local latch is enough; a joined player's client is reached over kiln.net and given a short time to latch
// and answer. A client with no Kiln, or no guard, cannot be protected, and the caller is told so.
//
// This lives in Kiln.Network (not Kiln.Client) because Kiln.Network references Kiln.Client, not the other way round.
//
// Both sides register the channel at Kiln.Network's start (NetworkStartup, Engine.cs), on a client in the main menu,
// before any connection: a client's hello lists its channels, and the server decides HasChannel from that list.
using System.Collections.Concurrent;

namespace Kiln;

/// <summary>What <see cref="SteamGuardRelay.LatchFor"/> could arrange for a player's Steam account.</summary>
/// <remarks>Experimental (KILNX002): the underlying guard has not yet been proven in a real game client.</remarks>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX002", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx002")]
[AnyThread]
public enum GuardOutcome
{
    /// <summary>The player's client latched its achievement guard and reports it protecting.</summary>
    Protected = 0,

    /// <summary>The player's client was asked but could not protect, left, or did not answer in time.</summary>
    UnprotectedFailed = 1,

    /// <summary>The player's client has no Kiln (a vanilla client) or no achievement guard: it cannot be protected.</summary>
    UnprotectedVanilla = 2,
}

/// <summary>
/// Turns on a player's own achievement guard from the server before a change that could unlock a Steam achievement.
/// </summary>
/// <remarks>
/// Call <see cref="LatchFor"/> and await it before an admin command changes a player's level, experience, skill points
/// or knowledge. The command may still run for an unprotected player (a server admin may level players); only the
/// guided-check runner refuses. Put the outcome into the command's reply when it is not <see cref="GuardOutcome.Protected"/>.
/// Awaiting it from the tick thread resumes the caller on its own context (the mod's scheduler).
/// Experimental (KILNX002): the underlying achievement guard has not yet run in a real game client. Do not rely on it
/// to protect a Steam account until the guard-proof check has passed; callers opt in with NoWarn for KILNX002.
/// Start latch requests on the server tick thread, including the host of a local game, never on the render thread.
/// </remarks>
[System.Diagnostics.CodeAnalysis.Experimental("KILNX002", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx002")]
[ServerOnly]
public static class SteamGuardRelay
{
    internal const string ChannelName = "kiln.steamguard";
    private const string ChannelVersion = "1";

    // ---- seams (tests replace these; the defaults are the real client) ----

    /// <summary>Latches this process's own guard and reports whether it protects. Game tick thread only.</summary>
    internal static Func<string, bool> LatchLocal = static reason => SteamGuard.Latch(reason) && SteamGuard.Protecting;

    /// <summary>True when <c>player</c> is this process's own player (single player, or the host's own character).</summary>
    internal static Func<EntityId, bool> IsLocalPlayer = static player => Client.IsClient && Client.LocalPlayer == player;

    /// <summary>How long the server waits for a player's client to answer.</summary>
    internal static TimeSpan ReplyTimeout = TimeSpan.FromSeconds(3);

    internal sealed record LatchRequest(ulong Corr, string Reason) : INetMessage<LatchRequest>
    {
        public void Encode(NetWriter w) => w.UInt64(Corr).String(Reason);
        public static LatchRequest Decode(ref NetReader r) => new(r.UInt64(), r.String(256));
    }

    internal sealed record LatchReply(ulong Corr, bool Protecting) : INetMessage<LatchReply>
    {
        public void Encode(NetWriter w) => w.UInt64(Corr).Bool(Protecting);
        public static LatchReply Decode(ref NetReader r) => new(r.UInt64(), r.Bool());
    }

    private sealed record Wait(uint Peer, TaskCompletionSource<bool> Answer);

    private static readonly ConcurrentDictionary<ulong, Wait> Pending = new();
    private static long _corr;
    private static NetChannel? _channel;
    private static int _started;

    /// <summary>Registers the channel, as Kiln's own (never a mod's, so no mod's unload closes it). Called at
    /// Kiln.Network's start on both sides; idempotent.</summary>
    internal static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0) return;
        var prev = ModContext.CurrentMod;
        ModContext.CurrentMod = null;
        try
        {
            _channel = Network.Channel(ChannelName, ChannelVersion, required: false)
                .Register<LatchRequest>(1, OnRequest, NetDirection.ToClient)
                .Register<LatchReply>(2, OnReply, NetDirection.ToServer);
            Events.On<NetPeerLeftEvent>(OnPeerLeft);
        }
        catch (Exception e)
        {
            Interlocked.Exchange(ref _started, 0);
            NetEngine.Log(LogLevel.Warn, $"{ChannelName}: channel not registered ({e.Message}): joined players cannot be protected");
        }
        finally
        {
            ModContext.CurrentMod = prev;
        }
    }

    /// <summary>Arranges that the player's own client has its achievement guard on. Latches locally when the player is
    /// this process's own (a hosting client); otherwise asks that player's client over kiln.net and waits a short
    /// bounded time (a player who leaves meanwhile answers <see cref="GuardOutcome.UnprotectedFailed"/> at once).
    /// Awaitable from the server tick thread.</summary>
    [TickThreadOnly]
    public static async Task<GuardOutcome> LatchFor(EntityId player, string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        if (IsLocalPlayer(player)) return LatchLocal(reason) ? GuardOutcome.Protected : GuardOutcome.UnprotectedFailed;

        Start();
        var channel = _channel;
        var peer = Network.PeerOf(player);
        if (peer is null || channel is null) return GuardOutcome.UnprotectedFailed;  // not reachable (left, no world yet)
        if (peer.KilnVersion is null || !peer.HasChannel(ChannelName))
            return GuardOutcome.UnprotectedVanilla;                                    // no Kiln, or a Kiln without the guard

        var corr = unchecked((ulong)Interlocked.Increment(ref _corr));
        var wait = new Wait(peer.Id, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        Pending[corr] = wait;
        try
        {
            if (!channel.SendTo(peer, new LatchRequest(corr, reason))) return GuardOutcome.UnprotectedFailed;
            var done = await Task.WhenAny(wait.Answer.Task, Task.Delay(ReplyTimeout)).ConfigureAwait(false);
            if (done != wait.Answer.Task) return GuardOutcome.UnprotectedFailed;       // no answer in time
            return await wait.Answer.Task.ConfigureAwait(false) ? GuardOutcome.Protected : GuardOutcome.UnprotectedFailed;
        }
        finally
        {
            Pending.TryRemove(corr, out _);
        }
    }

    /// <summary>For a change every player shares (world knowledge): latches this process's own guard (on a client) and
    /// asks every connected player's client, and returns the worst outcome. Players who join later are not covered:
    /// the changed world can report achievements in their game.</summary>
    [TickThreadOnly]
    public static async Task<GuardOutcome> LatchForAll(string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(reason);
        var worst = GuardOutcome.Protected;
        if (Client.IsClient && !LatchLocal(reason)) worst = GuardOutcome.UnprotectedFailed;
        var asks = new List<Task<GuardOutcome>>();
        foreach (var p in Network.Peers)
            if (!p.Player.IsNone && !IsLocalPlayer(p.Player)) asks.Add(LatchFor(p.Player, reason));
        foreach (var o in await Task.WhenAll(asks).ConfigureAwait(false))
            if (o > worst) worst = o;
        return worst;
    }

    /// <summary>A one-line note for a command reply when a player is not protected, or null when they are.</summary>
    [AnyThread]
    public static string? Note(GuardOutcome outcome) => outcome switch
    {
        GuardOutcome.Protected => null,
        GuardOutcome.UnprotectedVanilla => "this player's game has no Kiln achievement guard: Steam achievements may unlock",
        _ => "this player's achievement guard could not be turned on: Steam achievements may unlock",
    };

    // Client side, on the client's main tick thread (kiln.net delivers ToClient messages there): latch the local guard
    // and answer with whether it protects now. Never install its first hook on the render thread.
    private static void OnRequest(LatchRequest m, NetContext c)
    {
        bool protecting;
        try
        {
            protecting = LatchLocal(m.Reason);
        }
        catch (Exception)
        {
            protecting = false;  // a guard that threw does not protect
        }
        c.Reply(new LatchReply(m.Corr, protecting));
    }

    // Server side, on the tick thread: complete the waiting request, if it is still waiting and came from that peer.
    private static void OnReply(LatchReply m, NetContext c)
    {
        if (Pending.TryGetValue(m.Corr, out var wait) && wait.Peer == c.Sender.Id) wait.Answer.TrySetResult(m.Protecting);
    }

    // Server side: a player who leaves while asked gets no protection; answer the waiting requests at once.
    private static void OnPeerLeft(NetPeerLeftEvent e)
    {
        foreach (var (_, wait) in Pending)
            if (wait.Peer == e.Peer.Id) wait.Answer.TrySetResult(false);
    }
}
