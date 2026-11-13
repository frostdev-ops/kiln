// SPDX-License-Identifier: MIT
// Players.Kick, Kiln's ban list and PlayerLoginEvent on the server (since 0.2.0-dev.27). Kiln.Players owns the API and
// the list; this file fills its kick seam (PlayerAdmin.Kicker) and, from the server tick, refuses a connection whose
// account id or Steam id is banned, or that a PlayerLoginEvent handler refused. The refusal is the one the vanilla gate
// uses: a REJECT frame with the reason (a Kiln client shows it), then the game's login failure (kiln.net kick).
namespace Kiln;

internal static class PlayerGate
{
    /// <summary>The game's login error a kick or ban sends (keen::GameMessageLoginErrors 5, Generic): neither "Version"
    /// (the vanilla refusal's, which tells the player to update) nor "Full" fits.</summary>
    internal const uint KickReasonCode = 5;

    /// <summary>How long a Kiln client gets to read the REJECT before the login failure (as the vanilla refusal).</summary>
    internal static readonly TimeSpan KilnClientGrace = TimeSpan.FromSeconds(1.5);

    private static int _started;

    internal static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        PlayerAdmin.Kicker = Kick;
        PlayerAdmin.KickOff = WhyOff;
        ReportCapabilities();
    }

    /// <summary>Why kicks do not work in this process right now; null when they do.</summary>
    internal static string? WhyOff()
    {
        if (NetEngine.TestServerRole is { } test) return test ? null : "this process serves no clients";  // unit tests
        if (!NetNative.Available) return "kiln.net is not loaded";
        if (!Capabilities.Is("net.kick")) return Capabilities.Why("net.kick");
        if (!Capabilities.Is("net.players")) return Capabilities.Why("net.players");
        if (((NetNative.ReadStatus()?.Roles ?? 0) & NetNative.RoleServer) == 0) return "this process serves no clients";
        return null;
    }

    private static void ReportCapabilities()
    {
        try
        {
            var (state, reason, text) = Decide(NetNative.Available, Capabilities.Get("kiln.net"), Capabilities.Get("net.kick"),
                Capabilities.Get("net.players"), (NetNative.ReadStatus()?.Roles ?? 0) & NetNative.RoleServer);
            Capabilities.Report(PlayerAdmin.KickCapability, state, reason, text);
            Capabilities.Report(PlayerAdmin.BansCapability, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            NetEngine.Log(LogLevel.Warn, $"players.kick / players.bans were not reported: {e.Message}");
        }
    }

    /// <summary>The <c>players.kick</c> (and <c>players.bans</c>) row from what kiln.net has: the game's kick call, and
    /// the connection records that say which player and account a connection is. Without the records no connection is
    /// ever matched to a player or to a ban, so neither row may say available. Pure.</summary>
    internal static (CapabilityState, CapabilityReason, string) Decide(bool net, Capability netRow, Capability kick,
        Capability players, uint serverRole)
    {
        if (!net)
            return netRow is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }
                ? (CapabilityState.Unavailable, netRow.Reason, $"needs kiln.net: {netRow.ReasonText}")
                : (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.net is not loaded");
        if (kick.State != CapabilityState.Available)
            return (CapabilityState.Unavailable, kick.Reason is CapabilityReason.Unknown or CapabilityReason.None ? CapabilityReason.SymbolMissing : kick.Reason,
                $"needs net.kick: {(kick.ReasonText.Length > 0 ? kick.ReasonText : "the game's kick call did not resolve")}");
        if (players.State != CapabilityState.Available)
            return (CapabilityState.Unavailable, players.Reason is CapabilityReason.Unknown or CapabilityReason.None ? CapabilityReason.SymbolMissing : players.Reason,
                $"needs net.players: {(players.ReasonText.Length > 0 ? players.ReasonText : "the game's connection records did not resolve")}");
        if (serverRole == 0) return (CapabilityState.Unavailable, CapabilityReason.WrongSide, "this process serves no clients");
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    // ------------------------------------------------------------------------------------------------ kick ------

    /// <summary>PlayerAdmin.Kicker: refuses the player's connection (tick thread).</summary>
    internal static Task<KickResult> Kick(Player player, PlayerRef pref, string reason, string? by, bool banned)
    {
        var p = NetEngine.PeerOfPlayer(player.Entity);
        if (p is null) return Task.FromResult(new KickResult(pref, player.Name, false, "the player has no connection Kiln knows"));
        if (!p.IsLab && (p.Id & 0x7F) == 0)
            return Task.FromResult(new KickResult(pref, player.Name, false, "the hosting player's own game cannot be kicked"));
        return Refuse(p, pref, player.Name, reason, by, banned);
    }

    /// <summary>Sends the REJECT (Kiln clients) and schedules the login failure; the task completes when it went out.
    /// A peer that is already being refused keeps its first reason and gives the same task.</summary>
    internal static Task<KickResult> Refuse(NetPeer p, PlayerRef pref, string name, string reason, string? by, bool banned)
    {
        if (p.Kick is { } pending) return pending.Done.Task;
        var now = DateTime.UtcNow;
        var hasKiln = p.State == NetPeerState.Ready || p.KilnVersion is not null;
        var k = new PendingKick(pref, name, reason, by, banned,
            new TaskCompletionSource<KickResult>(TaskCreationOptions.RunContinuationsAsynchronously));
        p.Kick = k;
        if (hasKiln) NetEngine.SendReject(p, reason);
        p.KickAt = hasKiln ? now + KilnClientGrace : now;
        p.State = NetPeerState.Rejected;
        NetEngine.KicksPending = true;
        NetEngine.Log(LogLevel.Info, $"{(banned ? "banned" : "kicking")} {p.Name} (0x{p.Id:x}){(by is null ? "" : $" by {by}")}: {reason}");
        return k.Done.Task;
    }

    /// <summary>The kick of <paramref name="p"/> went out (rc from kiln.net), or its connection left first: completes its
    /// task and posts the event, once. The connection of a kick that went out leaves a moment later and comes here
    /// again; the refusal itself stays on the peer (<see cref="NetPeer.Kick"/>), so it is not refused a second time.</summary>
    internal static void Kicked(NetPeer p, int rc)
    {
        if (p.Kick is not { } k || k.Finished) return;
        k.Finished = true;
        var ok = rc == NetNative.Ok;
        var result = new KickResult(k.Player, k.Name, ok, ok ? null : $"the game's kick call failed (rc {rc})");
        if (ok && Events.HasSubscribers<PlayerKickedEvent>())
        {
            try
            {
                Events.Post(new PlayerKickedEvent(k.Player, k.Name, k.Reason, k.By, k.Banned));
            }
            catch (Exception e)
            {
                NetEngine.Log(LogLevel.Warn, $"PlayerKickedEvent: {e.Message}");
            }
        }
        k.Done.TrySetResult(result);
    }

    // ------------------------------------------------------------------------------------------------ gate ------

    /// <summary>The server tick's check of one connection: once its player is named, PlayerLoginEvent (once), and the ban
    /// list (every tick, so a ban made while the player is in game kicks too). Tick thread.</summary>
    internal static void Check(NetPeer p)
    {
        if (p.Kick is not null || p.IsServer) return;
        var steam = SteamIdOf(p);
        var account = p.AccountId;
        if (steam == 0 && account == 0) return;  // not named yet
        var ban = PlayerAdmin.Store.Find(steam, account, DateTimeOffset.UtcNow);
        var pref = new PlayerRef(steam, Guid.Empty, EntityRef.None);
        if (ban is not null)
        {
            p.LoginSeen = true;
            _ = Refuse(p, pref, p.Name, BanText(ban), null, true);
            return;
        }
        if (p.LoginSeen) return;
        p.LoginSeen = true;
        if (!Events.HasSubscribers<PlayerLoginEvent>()) return;
        var ev = Events.Post(new PlayerLoginEvent(p.Name, steam, account, p.Player, p.State == NetPeerState.Ready || p.KilnVersion is not null));
        if (ev.Canceled)
            _ = Refuse(p, pref, p.Name, string.IsNullOrWhiteSpace(ev.Reason) ? "You may not join this server." : ev.Reason, null, false);
    }

    /// <summary>What a banned player is told.</summary>
    internal static string BanText(BanEntry b) =>
        $"You are banned from this server{(b.Until is { } u ? $" until {u.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : "")}: {b.Reason}";

    private static ulong SteamIdOf(NetPeer p)
    {
        if (p.Player.IsNone) return 0;
        try
        {
            return Players.Find(p.Player)?.SteamId ?? 0;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            return 0;  // kiln.players cannot say: the account id still counts
        }
    }
}

/// <summary>A refusal on its way: what the kick says and who waits for it.</summary>
internal sealed record PendingKick(PlayerRef Player, string Name, string Reason, string? By, bool Banned,
    TaskCompletionSource<KickResult> Done)
{
    /// <summary>Its task is completed and its event posted (tick thread).</summary>
    public bool Finished { get; set; }
}
