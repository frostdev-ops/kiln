// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TkParity;

/// <summary>Parity tests: each means the same in a lab server and under Kiln.TestKit. Nothing in a test asks where it
/// runs. Offline, the network tests are skipped by the kit's rule (they ask for kiln.net, which the kit does not fake);
/// in a server they run.</summary>
public sealed class TkParityMod : Mod
{
    public override void OnLoad() => Log.Info("tk-parity loaded: its tests run once the server has started");
}

/// <summary>A mod's own event, posted on Kiln's bus.</summary>
public sealed class ParityEvent : KilnEvent
{
    public List<string> Seen { get; } = [];
}

/// <summary>A mod's own cancelable event.</summary>
public sealed class ParityCancelable : CancelableEvent
{
    public List<string> Seen { get; } = [];
}

/// <summary>The event bus: post, priority, cancel, a subscription gone after dispose.</summary>
public sealed class EventsParity
{
    [KilnTest]
    public void Post_runs_every_handler_in_priority_order()
    {
        using var low = Events.On<ParityEvent>(e => e.Seen.Add("low"), EventPriority.Low);
        using var highest = Events.On<ParityEvent>(e => e.Seen.Add("highest"), EventPriority.Highest);
        using var normal = Events.On<ParityEvent>(e => e.Seen.Add("normal"));
        using var monitor = Events.On<ParityEvent>(e => e.Seen.Add("monitor"), EventPriority.Monitor);
        var posted = Events.Post(new ParityEvent());
        Assert.Equal("highest,normal,low,monitor", string.Join(",", posted.Seen));
    }

    [KilnTest]
    public void Cancel_stops_later_handlers_unless_they_receive_canceled()
    {
        using var first = Events.On<ParityCancelable>(e => { e.Seen.Add("high"); e.Cancel(); }, EventPriority.High);
        using var skipped = Events.On<ParityCancelable>(e => e.Seen.Add("normal"));
        using var late = Events.On<ParityCancelable>(e => e.Seen.Add($"low canceled={e.Canceled}"), EventPriority.Low, receiveCanceled: true);
        var posted = Events.Post(new ParityCancelable());
        Assert.True(posted.Canceled, "canceled");
        Assert.Equal("tk-parity", posted.CanceledBy);
        Assert.Equal("high,low canceled=True", string.Join(",", posted.Seen));
    }

    [KilnTest]
    public void A_disposed_subscription_is_gone()
    {
        var calls = 0;
        var sub = Events.On<ParityEvent>(_ => calls++);
        Events.Post(new ParityEvent());
        Assert.Equal(1, calls);
        Assert.True(Events.Subscriptions.Contains(sub), "listed while subscribed");
        sub.Dispose();
        Events.Post(new ParityEvent());
        Assert.Equal(1, calls, "no call after dispose");
        Assert.False(Events.Subscriptions.Contains(sub), "not listed after dispose");
    }
}

/// <summary>Commands with permissions: register, run as the console and as a remote non-admin source, a denied command,
/// a failing handler. Each test removes what it registered.</summary>
public sealed class CommandsParity
{
    private static readonly CommandSource Dashboard = CommandSource.Remote("tk-parity-dashboard", admin: false);

    [KilnTest]
    public void A_registered_command_runs_as_the_console()
    {
        using var reg = Commands.Register("tk-parity-echo", ctx => ctx.Reply("echo " + string.Join(" ", ctx.Args)), description: "parity echo");
        var r = Commands.Execute(CommandSource.Console, "tk-parity-echo a b");
        Assert.True(r.Ok, r.Text);
        Assert.Equal("echo a b", string.Join("|", r.Output));
    }

    [KilnTest]
    public void A_remote_non_admin_runs_a_command_for_everyone()
    {
        using var reg = Commands.Register("tk-parity-everyone", ctx => ctx.Reply($"hi {ctx.Source.Name} admin={ctx.Source.IsAdmin}"));
        var r = Commands.Execute(Dashboard, "/tk-parity-everyone");
        Assert.True(r.Ok, r.Text);
        Assert.Equal("hi tk-parity-dashboard admin=False", string.Join("|", r.Output));
    }

    [KilnTest]
    public void An_admin_command_is_denied_to_a_remote_non_admin()
    {
        var ran = 0;
        using var reg = Commands.Register("tk-parity-admin", _ => ran++, CommandPermission.Admin);
        var denied = Commands.Execute(Dashboard, "tk-parity-admin");
        Assert.True(denied.Found, "found");
        Assert.True(denied.Denied, denied.Text);
        Assert.False(denied.Ok, "not ok");
        Assert.Equal(0, ran);
        var console = Commands.Execute(CommandSource.Console, "tk-parity-admin");
        Assert.True(console.Ok, console.Text);
        Assert.Equal(1, ran);
    }

    [KilnTest]
    public void A_failing_handler_is_a_result()
    {
        using var reg = Commands.Register("tk-parity-fail", _ => throw new InvalidOperationException("parity handler broke"));
        var r = Commands.Execute(CommandSource.Console, "tk-parity-fail");
        Assert.True(r.Found, "found");
        Assert.False(r.Ok, "not ok");
        Assert.False(r.Denied, "not denied");
        Assert.Equal("/tk-parity-fail failed (see the server log)", r.Error);   // the exception goes to the log, not to the source
        var unknown = Commands.Execute(CommandSource.Console, "tk-parity-nowhere");
        Assert.False(unknown.Found, "an unknown command is not found");
    }
}

/// <summary>A message of the parity channel.</summary>
public sealed record ParityPing(string Text, long Count, WorldPosition At) : INetMessage<ParityPing>
{
    public void Encode(NetWriter w) => w.String(Text).VarInt(Count).Position(At);
    public static ParityPing Decode(ref NetReader r) => new(r.String(256), r.VarInt(), r.Position3());
}

/// <summary>Network channels. Each test starts from what it needs, the transport (kiln.net): in a server it is there,
/// offline the kit does not fake it, and the kit's skip rule reports the test skipped with kiln.net named.</summary>
public sealed class NetworkParity
{
    [KilnTest]
    public void A_channel_opens_and_closes()
    {
        Assert.True(Network.Available, "kiln.net is up");
        var ch = Network.Channel("tk-parity:open", "1", required: false).Register<ParityPing>();
        Assert.True(Network.Channels.Any(c => c.Name == "tk-parity:open" && c.Version == "1"), "listed");
        ch.Dispose();
        Assert.False(Network.Channels.Any(c => c.Name == "tk-parity:open"), "gone after dispose");
    }

    [KilnTest]
    public void A_message_round_trips_through_the_codec()
    {
        Assert.True(Network.Available, "kiln.net is up");
        using var ch = Network.Channel("tk-parity:codec", "1", required: false).Register<ParityPing>();
        var sent = new ParityPing("héllo ✓", -300, new WorldPosition(1.5, -2, 3e6));
        var w = new NetWriter();
        sent.Encode(w);
        var r = new NetReader(w.WrittenSpan);
        var back = ParityPing.Decode(ref r);
        Assert.True(r.End, "every byte read");
        Assert.Equal(sent, back);
    }

    [KilnTest]
    public void A_second_version_of_a_channel_is_refused()
    {
        Assert.True(Network.Available, "kiln.net is up");
        using var ch = Network.Channel("tk-parity:dup", "1", required: false);
        Assert.Throws<InvalidOperationException>(() => Network.Channel("tk-parity:dup", "2", required: false));
        Assert.Same(ch, Network.Channel("tk-parity:dup", "1", required: false));
    }
}
