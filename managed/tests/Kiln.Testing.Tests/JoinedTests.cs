// SPDX-License-Identifier: MIT
using Kiln.Testing;
using Xunit;
using Assert = Xunit.Assert;

namespace Kiln.CheckRunnerTests;

public sealed class JoinedTests
{
    [Theory]
    [InlineData("finished", false)]
    [InlineData("refused", false)]
    [InlineData("declined", false)]
    [InlineData("finished", true)]
    [InlineData("refused", true)]
    [InlineData("declined", true)]
    public void Joined_start_holds_the_clients_guard_until_its_terminal_state(string terminal, bool cheats)
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        var item = Item(cheats);
        net.Start(item);
        if (cheats) net.Answer(AnswerKind.Yes);
        Assert.Single(sent.OfType<NetStart>());
        Assert.Equal(1, h.Guard!.Holds);
        Assert.True(h.Guard.Status()!.Value.Active);
        net.OnState(new NetState { Kind = terminal, Mod = item.Mod, Id = item.Id });
        Assert.Equal(1, h.Guard.Releases);
        net.OnState(new NetState { Kind = terminal, Mod = item.Mod, Id = item.Id });
        Assert.Equal(1, h.Guard.Releases);
    }

    [Fact]
    public void A_disconnected_join_releases_the_guard_even_before_any_server_was_seen()
    {
        using var h = new Harness();
        var net = new CheckNet(h.Runner, h.Env, null!, _ => true, () => null);
        net.Start(Item(false));
        net.Step();
        Assert.Equal(1, h.Guard!.Holds);
        Assert.Equal(1, h.Guard.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_send_releases_the_joined_guard(bool throws)
    {
        using var h = new Harness();
        var net = new CheckNet(h.Runner, h.Env, null!, _ => throws ? throw new InvalidOperationException("send failed") : false);
        try { net.Start(Item(false)); } catch (InvalidOperationException) { }
        Assert.Equal(1, h.Guard!.Holds);
        Assert.Equal(1, h.Guard.Releases);
    }

    [Fact]
    public void Joined_ui_start_and_yes_do_no_guard_disk_or_send_work_until_client_tick()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var tick = new Queue<Action>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        CheckUiHost.ActJoined(new UiAction(UiActionKind.Run, Item(true)), net, tick.Enqueue);
        Assert.Null(net.View(h.Runner.View).Current);
        tick.Dequeue()();
        Assert.NotNull(net.View(h.Runner.View).Current);
        CheckUiHost.ActJoined(new UiAction(UiActionKind.Answer, Answer: AnswerKind.Yes), net, tick.Enqueue);
        Assert.Equal(0, h.Guard!.Latches);
        Assert.Equal(0, h.Guard.Holds);
        Assert.Empty(sent);
        tick.Dequeue()();
        Assert.Equal(1, h.Guard.Latches);
        Assert.Equal(1, h.Guard.Holds);
        Assert.Single(sent.OfType<NetStart>());
    }

    internal static ViewItem Item(bool cheats) => new("cct", cheats ? "t.cheats" : "t.pass", "Check", "Basics",
        cheats ? [ClientCheckTags.Cheats] : [], "joined", null, null, null, null, cheats);
}
