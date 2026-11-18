// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>Client-world hops (#82) in KILN101 and KILN105: a task body's parameterless hops land where its
/// <c>Context.Tasks.Run</c> was called (the server world's tick from tick code, the game's main thread from Client.Tick,
/// Client.Post and [ClientMainThread] callbacks), the overloads land where their constant world says, the game's main
/// thread is a game thread for the blocking rules, and <c>await Client.NextTick()</c> inside a task is reported as leaving
/// the task. Compiled against the real Kiln.Api and Kiln.Client.</summary>
public sealed class TaskWorldAnalyzerTests
{
    private static readonly BlockingAnalyzer Blocking = new();
    private static readonly TickThreadAnalyzer Tick = new();

    [Fact]
    public async Task A_task_started_on_the_tick_lands_on_the_tick()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("server", async ctx =>
                    {
                        Thread.Sleep(1);
                        await ctx.ToTick();
                        Thread.Sleep(1);
                    });
                }
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("the tick thread (a tick hop)", d[0].GetMessage());
    }

    [Fact]
    public async Task A_task_started_from_Client_Tick_lands_on_the_main_thread()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Client.Tick += OnClientTick;

                private void OnClientTick(in TickInfo t)
                {
                    Context.Tasks.Run("client", async ctx =>
                    {
                        Thread.Sleep(1);
                        await ctx.ToTick();
                        Thread.Sleep(1);
                        await Task.Delay(1);
                        Thread.Sleep(1);
                    });
                }
            }
            """);
        Ids(d, "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("the game's main thread", x.GetMessage()));
        Assert.Contains("client-world hop", d[0].GetMessage());
    }

    [Fact]
    public async Task Client_Post_work_and_its_tasks_are_main_thread_code()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Client.Post(() =>
                    {
                        Thread.Sleep(1);
                        Context.Tasks.Run("from-post", async ctx =>
                        {
                            await ctx.OnTick(() => { Thread.Sleep(1); return 1; });
                        });
                    });
                }
            }
            """);
        Ids(d, "KILN105", "KILN105");
        Assert.Contains("Client.Post", d[0].GetMessage());
        Assert.Contains("the game's main thread (the client world's step, ctx.OnTick)", d[1].GetMessage());
    }

    [Fact]
    public async Task The_overloads_land_where_their_world_says()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("cross", async ctx =>
                    {
                        await ctx.ToTick(WorldKind.Client);
                        Thread.Sleep(1);
                        await ctx.ToTick(WorldKind.Server);
                        Thread.Sleep(2);
                        ctx.PostToTick(WorldKind.Client, () => Thread.Sleep(3));
                        await ctx.NextGateWindow(WorldKind.Client);
                        Thread.Sleep(4);
                    });
                }
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105");
        var messages = d.Select(x => x.GetMessage()).ToList();
        Assert.Contains(messages, m => m.Contains("the game's main thread (the client world's step, a client-world hop)"));
        Assert.Contains(messages, m => m.Contains("the tick thread (a tick hop)"));
        Assert.Contains(messages, m => m.Contains("the game's main thread (the client world's step, ctx.PostToTick)"));
    }

    [Fact]
    public async Task A_task_started_in_a_client_hop_takes_the_client_world()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("parent", async ctx =>
                    {
                        await ctx.ToTick(WorldKind.Client);
                        Context.Tasks.Run("child", async c =>
                        {
                            await c.ToTick();
                            Thread.Sleep(1);
                        });
                    });
                }
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("the game's main thread", d[0].GetMessage());
    }

    [Fact]
    public async Task Awaiting_Client_NextTick_in_a_task_is_reported_and_outside_one_is_not()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("escape", async ctx =>
                    {
                        var n = 1;
                        await Client.NextTick();
                        n++;
                    });
                    Client.Post(async () =>
                    {
                        await Client.NextTick();  // not in a task: Kiln.Client's own way, fine
                    });
                }
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("'await Client.NextTick()' leaves the task", d[0].GetMessage());
        Assert.Contains("ctx.ToTick(WorldKind.Client)", d[0].GetMessage());
    }

    [Fact]
    public async Task Waiting_on_a_task_in_main_thread_callbacks_is_reported()
    {
        var d = await Run(Blocking, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Client.Tick += (in TickInfo t) => Context.Tasks.Run("a", _ => { }).Wait();
                    Ui.Values.SubscribeSlots(r => Context.Tasks.Run("b", _ => { }).Wait());
                }
            }
            """);
        Ids(d, "KILN105", "KILN105");
        Assert.Contains("Client.Tick", d[0].GetMessage());
        Assert.Contains("[ClientMainThread]", d[1].GetMessage());
    }

    [Fact]
    public async Task KILN101_stays_quiet_after_a_client_hop_and_reports_the_worker_as_before()
    {
        var d = await Run(Tick, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Client.Tick += (in TickInfo t) =>
                    Context.Tasks.Run("client", async ctx =>
                    {
                        Chat.Broadcast("on a worker");   // KILN101: a task worker
                        await ctx.ToTick();
                        Chat.Broadcast("on the main thread: a game thread, the runtime check decides");
                    });
            }
            """);
        Ids(d, "KILN101");
        Assert.Contains("Kiln task worker", d[0].GetMessage());
    }
    // ---- with KILN105's gated-write row and command/chat handlers as tick code (#86) ----

    [Fact]
    public async Task A_task_from_a_command_handler_hops_to_the_server_tick_where_a_compare_exchange_is_reported()
    {
        var d = await Run(Blocking, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private ConcurrentEntity _e;

                public override void OnLoad()
                {
                    Commands.Register("w", c => Context.Tasks.Run("from a command", async ctx =>
                    {
                        await ctx.ToTick();
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 1L);   // the server tick: always Busy
                    }));
                    Chat.Received += m => Context.Tasks.Run("from chat", async ctx =>
                    {
                        await ctx.ToTick();
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);
                    });
                }

                [Command("c")]
                private void Cmd(CommandContext c) => Context.Tasks.Run("from [Command]", async ctx =>
                {
                    await ctx.ToTick();
                    WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 3L);
                });
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("always answers Busy", x.GetMessage()));
        Assert.All(d, x => Assert.Contains("tick hop", x.GetMessage()));
    }

    [Fact]
    public async Task A_compare_exchange_after_a_client_hop_is_task_scope_and_not_the_server_tick()
    {
        var d = await Run(Blocking, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private ConcurrentEntity _e;

                public override void OnLoad() => Client.Tick += OnClientTick;

                private void OnClientTick(in TickInfo t)
                {
                    Context.Tasks.Run("client", async ctx =>
                    {
                        await ctx.ToTick();
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 1L);   // a client hop: meets the gate
                        await ctx.ToTick(WorldKind.Server);
                        WorldConcurrent.TryCompareExchange(_e, "Charge", 1L, 2L);   // the server tick: reported
                    });
                }
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("always answers Busy", d[0].GetMessage());
        Assert.Contains("2L", d[0].Location.SourceTree!.ToString().Substring(d[0].Location.SourceSpan.Start, d[0].Location.SourceSpan.Length));
    }
}
