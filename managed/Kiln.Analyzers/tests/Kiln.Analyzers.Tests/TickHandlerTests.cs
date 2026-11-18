// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>Command and chat handlers run on the server tick (Kiln.Chat's <c>Commands.Execute</c> is tick-thread only:
/// chat lines in Pre, the console folder and remote tools from a service tick), so the analyzers treat them as tick code:
/// <c>[Command]</c> methods, handlers given to <c>Commands.Register</c> and to <c>LiteralNode</c>/<c>ArgumentNode.Executes</c>,
/// and <c>Chat.Received</c> handlers. KILN105's blocking rows and its gated-write row apply there; KILN101 follows them
/// past <c>ConfigureAwait(false)</c>. And KILN105's gated-write row: a compare-exchange (<c>WorldConcurrent.TryCompareExchange</c>)
/// in tick code always answers busy, because the server frame holds the engine's write gate for its whole update.</summary>
public sealed class TickHandlerTests
{
    private static readonly BlockingAnalyzer B = new();
    private static readonly TickThreadAnalyzer T = new();

    // ---- command and chat handlers are tick code ----

    [Fact]
    public async Task Blocking_in_a_Command_method_is_reported()
    {
        var d = await Run(B, """
            public sealed class M : Mod
            {
                [Command("fetch")]
                private string Fetch(CommandContext c)
                {
                    var job = Context.Tasks.Run<int>("work", ctx => Task.FromResult(1));
                    job.Wait();
                    var n = job.AsTask().Result;
                    Task.Delay(10).Wait();
                    return n.ToString();
                }
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("the tick thread (a [Command] handler)", x.GetMessage()));
        Assert.Contains("KilnTask.Wait", d[0].GetMessage());
        Assert.Contains("KilnTask.AsTask()", d[1].GetMessage());
    }

    [Fact]
    public async Task Blocking_in_registered_command_handlers_and_chat_handlers_is_reported_as_tick_code()
    {
        var d = await Run(B, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Commands.Register("a", c => Task.Delay(1).Wait());
                    Commands.Register(Commands.Literal("b").Executes(OnB));
                    Commands.Register(Commands.Literal("c").Then(Commands.Argument("n", Arg.Int()).Executes(c => Thread.Sleep(5))));
                    Chat.Received += m => Task.Delay(1).Wait();
                }

                private static void OnB(CommandContext c) => Task.Delay(1).Wait();
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.Contains(d, x => x.GetMessage().Contains("the tick thread (a Commands.Register handler)"));
        Assert.Contains(d, x => x.GetMessage().Contains("the tick thread (a LiteralNode.Executes handler)"));
        Assert.Contains(d, x => x.GetMessage().Contains("the tick thread (an ArgumentNode.Executes handler)"));
        Assert.Contains(d, x => x.GetMessage().Contains("the tick thread (a Chat.Received handler)"));
    }

    [Fact]
    public async Task A_command_that_awaits_or_hands_work_to_a_task_is_not_reported()
    {
        var d = await Run(B, """
            public sealed class M : Mod
            {
                [Command("fine")]
                private async Task Fine(CommandContext c)
                {
                    var job = Context.Tasks.Run<int>("work", ctx => Task.FromResult(1));
                    var n = await job;
                    await Task.Delay(1);
                    c.Reply(n.ToString());
                }
            }
            """);
        Assert.Empty(d);
    }

    [Fact]
    public async Task KILN101_follows_a_command_past_ConfigureAwait_false()
    {
        var d = await Run(T, """
            public sealed class M : Mod
            {
                [Command("later")]
                private async Task Later(CommandContext c)
                {
                    Chat.Broadcast("on the tick: fine");
                    await Task.Delay(1).ConfigureAwait(false);
                    Chat.Broadcast("off the tick");
                }
            }
            """);
        Ids(d, "KILN101");
        Assert.Contains("ConfigureAwait(false)", d[0].GetMessage());
    }

    // ---- the gated-write row: a compare-exchange in tick code ----

    [Fact]
    public async Task A_compare_exchange_in_tick_code_is_reported()
    {
        var d = await Run(B, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private ConcurrentEntity _e;

                public override void OnLoad()
                {
                    WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                    Context.Tick.Pre += (in TickInfo t) => WorldConcurrent.TryCompareExchangeBytes(_e, "Charge", new byte[8], new byte[8]);
                    Context.Tasks.Run("hops", async ctx =>
                    {
                        await ctx.ToTick();
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                    });
                    Context.Tasks.Run("posts", ctx => ctx.PostToTick(() => WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L)));
                }

                [Command("charge")]
                private void Charge(CommandContext c) => WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("always answers Busy", x.GetMessage()));
        Assert.Contains("WorldConcurrent.TryCompareExchangeBytes", d[1].GetMessage());
        Assert.Contains("the tick thread (a [Command] handler)", d[4].GetMessage());
        Assert.All(d, x => Assert.Contains("NextGateWindow", x.GetMessage()));
    }

    [Fact]
    public async Task A_compare_exchange_on_a_worker_is_not_reported()
    {
        var d = await Run(B, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private ConcurrentEntity _e;

                [Command("charge")]
                private string Charge(CommandContext c)
                {
                    Context.Tasks.Run("write", async ctx =>
                    {
                        var r = WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                        while (r.Status == ExchangeStatus.Busy)
                        {
                            await ctx.NextGateWindow();
                            r = WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                        }
                    });
                    return "started";
                }
            }
            """);
        Assert.Empty(d);
    }

    [Fact]
    public async Task A_look_alike_TryCompareExchange_is_not_reported()
    {
        var d = await Run(B, """
            public static class WorldConcurrentLike
            {
                public static bool TryCompareExchange(object e, string c, long a, long b) => true;
            }
            public sealed class M : Mod
            {
                public override void OnLoad() => WorldConcurrentLike.TryCompareExchange(null!, "Charge", 1L, 2L);
            }
            """);
        Assert.Empty(d);
    }

    [Fact]
    public async Task A_compare_exchange_after_ConfigureAwait_false_is_the_out_of_scope_row()
    {
        var d = await Run(B, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private ConcurrentEntity _e;

                public override void OnLoad()
                {
                    Context.Tasks.Run("escaped", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                        WorldConcurrent.TryCompareExchangeBytes(_e, "Charge", new byte[8], new byte[8]);
                    });
                    Task.Run(() => WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L));
                }
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105");
        Assert.Equal(["WorldConcurrent.TryCompareExchange", "WorldConcurrent.TryCompareExchangeBytes", "WorldConcurrent.TryCompareExchange"],
            d.Select(x => x.GetMessage().Split('\'')[1]));
        Assert.All(d, x => Assert.Contains("is refused", x.GetMessage()));
    }
}
