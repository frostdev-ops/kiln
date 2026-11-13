// SPDX-License-Identifier: MIT
using Kiln;

namespace NetSample;

/// <summary>Client to server: a ping with a timestamp and a text.</summary>
public sealed record Ping(long SentMs, string Text) : INetMessage<Ping>
{
    public void Encode(NetWriter w) => w.Int64(SentMs).String(Text);
    public static Ping Decode(ref NetReader r) => new(r.Int64(), r.String(512));
}

/// <summary>Server to client: the ping echoed with the player's name and the server's step.</summary>
public sealed record Pong(long SentMs, string Text, string Player, ulong ServerStep) : INetMessage<Pong>
{
    public void Encode(NetWriter w) => w.Int64(SentMs).String(Text).String(Player).UInt64(ServerStep);
    public static Pong Decode(ref NetReader r) => new(r.Int64(), r.String(512), r.String(128), r.UInt64());
}

/// <summary>Server to every client: a notice.</summary>
public sealed record Announce(string Text) : INetMessage<Announce>
{
    public void Encode(NetWriter w) => w.String(Text);
    public static Announce Decode(ref NetReader r) => new(r.String(1024));
}

/// <summary>
/// Kiln.Network in one mod that runs on both sides (side "both"):
///  - server: answers Ping with Pong and tells every Kiln client when another one joins;
///  - client: F9 sends a Ping; the Pong's round trip is shown as a toast (Kiln.Ui); announcements too;
///  - server: a whitelist when kiln/data/netsample/whitelist.txt exists (one SteamID64 per line): PlayerLoginEvent
///    refuses everyone else (Kiln's ban list and Players.Kick work the same way; /ban and /kick are Kiln's).
/// The channel's messages are registered in the same order on both sides (ids 0, 1, 2).
/// </summary>
public sealed class NetSampleMod : Mod
{
    private NetChannel? _ch;
    private ulong _step;
    private int _pings;

    public override void OnLoad()
    {
        _ch = Network.Channel("netsample:main", "1")
            .Register<Ping>((m, ctx) =>
            {
                Log.Info($"ping '{m.Text}' from {ctx.Sender.Name}");
                ctx.Reply(new Pong(m.SentMs, m.Text, ctx.Sender.Name, _step));
            }, NetDirection.ToServer)
            .Register<Pong>((m, ctx) =>
            {
                var rtt = Environment.TickCount64 - m.SentMs;
                Log.Info($"pong '{m.Text}' for {m.Player} at server step {m.ServerStep}: {rtt} ms");
                Hud.Toast($"pong from the server in {rtt} ms (you are {m.Player})", ToastKind.Success);
            }, NetDirection.ToClient)
            .Register<Announce>((m, ctx) => Hud.Toast(m.Text), NetDirection.ToClient);

        Context.Tick.Pre += (in TickInfo t) => _step = t.Step;
        Network.ServerReady += server => Hud.Toast($"Kiln server {server.KilnVersion}: press F9 to ping it");
        Network.ServerVanilla += _ => Hud.Toast("This server has no Kiln: netsample does nothing here", ToastKind.Warning);
        Network.Rejected += (reason, _) => Log.Warn("refused: " + reason);  // Kiln itself shows the refusal on screen
        if (Ui.Available)
        {
            KeyBindings.Register("netsample.ping", "F9", () =>
            {
                var ok = _ch.SendToServer(new Ping(Environment.TickCount64, $"ping #{++_pings}"));
                if (!ok) Hud.Toast(Network.ServerHasKiln ? "the server lacks netsample" : "not connected to a Kiln server", ToastKind.Warning);
            }, "Ping the server");
        }
        Log.Info($"netsample: channel {_ch} open ({(Ui.Available ? "client: F9 pings" : "server")})");
    }

    // The whitelist, read at each login so an edit counts at once. No file: everyone may join.
    [Subscribe]
    private void OnLogin(PlayerLoginEvent e)
    {
        var path = Path.Combine(Context.DataDirectory, "whitelist.txt");
        if (!File.Exists(path)) return;
        var allowed = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet();
        if (e.SteamId != 0 && allowed.Contains(e.SteamId.ToString())) return;
        e.Reason = "This server is whitelisted.";
        e.Cancel();
        Log.Info($"whitelist: refused {e.Name} (Steam {e.SteamId}, account {e.AccountId})");
    }

    [Subscribe]
    private void OnClientReady(NetPeerReadyEvent e)
    {
        if (!e.Peer.HasChannel("netsample:main")) return;
        var n = _ch!.SendToAll(new Announce($"{e.Peer.Name} joined with Kiln {e.Peer.KilnVersion}"), except: e.Peer);
        Log.Info($"{e.Peer.Name} has netsample; announced to {n} other client(s)");
    }
}
