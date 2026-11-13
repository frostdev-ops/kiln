using Kiln;

namespace KilnNetwork1;

/// <summary>Client to server. A message writes and reads its own fields, in the same order, with a length limit on
/// everything of variable size: what arrives comes from another machine.</summary>
public sealed record Hello(string Text) : INetMessage<Hello>
{
    public void Encode(NetWriter w) => w.String(Text);
    public static Hello Decode(ref NetReader r) => new(r.String(256));
}

/// <summary>Server to client: the answer.</summary>
public sealed record Welcome(string Text, ulong ServerStep) : INetMessage<Welcome>
{
    public void Encode(NetWriter w) => w.String(Text).UInt64(ServerStep);
    public static Welcome Decode(ref NetReader r) => new(r.String(256), r.UInt64());
}

/// <summary>One mod on both sides (side "both" in the project file): the same OnLoad runs in the server and in each
/// player's game, and registers the same messages in the same order (their ids are that order).</summary>
public sealed class KilnNetwork1Mod : Mod
{
    private const string ChannelName = "kiln-mod-id:main";  // "<your mod id>:<name>": unique among all mods
    private NetChannel? _channel;
    private ulong _step;

    public override void OnLoad()
    {
        // The version is compared when a player joins: raise it when a message changes its fields.
        _channel = Network.Channel(ChannelName, "1")
            // handled on the server
            .Register<Hello>((message, context) =>
            {
                Log.Info($"hello from {context.Sender.Name}: {message.Text}");
                context.Reply(new Welcome($"Welcome, {context.Sender.Name}", _step));
            }, NetDirection.ToServer)
            // handled in the player's game
            .Register<Welcome>((message, context) =>
                Log.Info($"the server answered at its step {message.ServerStep}: {message.Text}"), NetDirection.ToClient);

        Context.Tick.Pre += (in TickInfo tick) => _step = tick.Step;

        // In a player's game: the server has Kiln and this channel, so say hello. On a server this never fires.
        Network.ServerReady += server =>
        {
            // False when the server does not have this mod: check instead of assuming.
            if (!_channel.SendToServer(new Hello("hello from a player's game")))
                Log.Info("the server has Kiln but not this mod's channel");
        };
        Network.ServerVanilla += _ => Log.Info("this server has no Kiln: the channel stays closed");
    }

    // On the server: a player's game finished Kiln's handshake. Not every player has this mod: ask the peer.
    [Subscribe]
    private void OnClientReady(NetPeerReadyEvent e)
    {
        if (!e.Peer.HasChannel(ChannelName)) return;
        var told = _channel!.SendToAll(new Welcome($"{e.Peer.Name} joined", _step), except: e.Peer);
        Log.Info($"{e.Peer.Name} has this mod; {told} other player(s) were told");
    }
}
