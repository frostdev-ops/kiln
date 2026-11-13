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

    /// <summary>"Run these N" on a joined client: the server takes one start at a time, so the client sends each when
    /// the one before it ended (finished, refused or declined), in the list's order, and drops none. Before this the
    /// button pressed Run once per row, and every start after the first was dropped while the first was pending.</summary>
    [Fact]
    public void Run_these_sends_each_check_in_order_one_at_a_time()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var tick = new Queue<Action>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        ViewItem[] these = [Item(false) with { Id = "t.a" }, Item(false) with { Id = "t.b" }, Item(false) with { Id = "t.c" }];
        CheckUiHost.ActJoined(new UiAction(UiActionKind.RunThese, Items: these), net, tick.Enqueue);
        Assert.Empty(sent);
        tick.Dequeue()();
        string[] Started() => sent.OfType<NetStart>().Select(m => m.Id).ToArray();
        Assert.Equal(["t.a"], Started());
        Assert.Equal(2, net.View(h.Runner.View).Queued);
        net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.a" });
        Assert.Equal(["t.a", "t.b"], Started());
        net.OnState(new NetState { Kind = "refused", Mod = "cct", Id = "t.b", Message = "no" });
        Assert.Equal(["t.a", "t.b", "t.c"], Started());
        Assert.Equal(0, net.View(h.Runner.View).Queued);
        net.OnState(new NetState { Kind = "declined", Mod = "cct", Id = "t.c" });
        Assert.Equal(3, Started().Length);
        Assert.Equal(h.Guard!.Holds, h.Guard.Releases);
    }

    /// <summary>A list asked for while one start is still pending (sent, the server has not answered): nothing of it is
    /// dropped, it goes on when the pending one ends.</summary>
    [Fact]
    public void A_list_asked_for_while_a_start_is_pending_waits_for_it()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        net.Start(Item(false) with { Id = "t.first" });
        net.StartThese([Item(false) with { Id = "t.a" }, Item(false) with { Id = "t.b" }]);
        Assert.Equal(["t.first"], sent.OfType<NetStart>().Select(m => m.Id));
        Assert.Equal(2, net.View(h.Runner.View).Queued);
        net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.first" });
        net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.a" });
        Assert.Equal(["t.first", "t.a", "t.b"], sent.OfType<NetStart>().Select(m => m.Id));
    }

    /// <summary>A start that cannot be sent has no server answer that would call for the next queued check: the queue
    /// ends there and the message says how many were not started (before, they stayed queued for good).</summary>
    [Fact]
    public void A_start_that_cannot_be_sent_ends_the_queue_and_says_so()
    {
        using var h = new Harness();
        var tried = 0;
        var net = new CheckNet(h.Runner, h.Env, null!, _ => { tried++; return false; });
        net.StartThese([Item(false) with { Id = "t.a" }, Item(false) with { Id = "t.b" }, Item(false) with { Id = "t.c" }]);
        Assert.Equal(1, tried);
        var v = net.View(h.Runner.View);
        Assert.Equal(0, v.Queued);
        Assert.Contains("2 queued check(s) were not started", v.Message);
        Assert.Equal(h.Guard!.Holds, h.Guard.Releases);
        // and the next request starts at once: nothing is left pending
        net.Start(Item(false) with { Id = "t.d" });
        Assert.Equal(2, tried);
    }

    /// <summary>A second list asked for while the first still runs is added behind it, as the hosted runner queues its
    /// requests (before, it replaced the waiting checks, and its first check was sent twice in a row).</summary>
    [Fact]
    public void A_second_list_is_added_behind_the_first()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        ViewItem[] these = [Item(false) with { Id = "t.a" }, Item(false) with { Id = "t.b" }];
        net.StartThese(these);
        net.StartThese(these);
        Assert.Equal(3, net.View(h.Runner.View).Queued);
        foreach (var id in new[] { "t.a", "t.b", "t.a", "t.b" })
            net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = id });
        Assert.Equal(["t.a", "t.b", "t.a", "t.b"], sent.OfType<NetStart>().Select(m => m.Id));
        Assert.Equal(0, net.View(h.Runner.View).Queued);
    }

    /// <summary>A single Run pressed while a start is still on its way waits its turn (before, it was dropped without
    /// a word: the rows keep their Run buttons until the server says the first check has started).</summary>
    [Fact]
    public void A_single_run_while_a_start_is_pending_waits_its_turn()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        net.Start(Item(false) with { Id = "t.a" });
        net.Start(Item(false) with { Id = "t.d" });
        Assert.Equal(["t.a"], sent.OfType<NetStart>().Select(m => m.Id));
        Assert.Equal(1, net.View(h.Runner.View).Queued);
        net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.a" });
        Assert.Equal(["t.a", "t.d"], sent.OfType<NetStart>().Select(m => m.Id));
    }

    /// <summary>A terminal state the server sends about another check than the pending one does not take the next
    /// queued check out (it was dequeued, could not be sent while the first was pending, and was gone).</summary>
    [Fact]
    public void A_state_about_another_check_does_not_lose_the_queued_one()
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        net.Start(Item(false) with { Id = "t.a" });
        net.Start(Item(false) with { Id = "t.b" });
        net.OnState(new NetState { Kind = "refused", Mod = "cct", Id = "t.x", Message = "no" });
        Assert.Equal(["t.a"], sent.OfType<NetStart>().Select(m => m.Id));
        Assert.Equal(1, net.View(h.Runner.View).Queued);
        net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.a" });
        Assert.Equal(["t.a", "t.b"], sent.OfType<NetStart>().Select(m => m.Id));
    }

    /// <summary>A cheats check in a list: its throwaway question holds the queue; no goes on to the next check, yes
    /// sends it and the next follows when it ended.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_cheats_check_in_a_list_holds_the_queue_for_its_question(bool yes)
    {
        using var h = new Harness();
        var sent = new List<object>();
        var net = new CheckNet(h.Runner, h.Env, null!, m => { sent.Add(m); return true; });
        net.StartThese([Item(true), Item(false) with { Id = "t.b" }]);
        Assert.Empty(sent.OfType<NetStart>());
        Assert.Equal("confirming", net.View(h.Runner.View).Current?.Phase);
        Assert.Equal(1, net.View(h.Runner.View).Queued);
        net.Answer(yes ? AnswerKind.Yes : AnswerKind.No);
        if (yes)
        {
            Assert.Equal(["t.cheats"], sent.OfType<NetStart>().Select(m => m.Id));
            net.OnState(new NetState { Kind = "finished", Mod = "cct", Id = "t.cheats" });
        }
        Assert.Equal(yes ? ["t.cheats", "t.b"] : ["t.b"], sent.OfType<NetStart>().Select(m => m.Id));
    }

    internal static ViewItem Item(bool cheats) => new("cct", cheats ? "t.cheats" : "t.pass", "Check", "Basics",
        cheats ? [ClientCheckTags.Cheats] : [], "joined", null, null, null, null, cheats);
}
