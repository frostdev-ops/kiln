// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN101 inside Kiln tasks (issue #50, T5): a delegate handed to <c>Context.Tasks.Run</c> runs on a Kiln
/// worker until a genuine tick hop. The API here is the real one of <c>tasks/core</c> (Kiln.Api's
/// <c>KilnTasks</c>/<c>KilnTaskContext</c>), not a name look-alike.</summary>
public sealed class TaskThreadingTests
{
    private static readonly TickThreadAnalyzer A = new();

    [Fact]
    public async Task Task_body_runs_on_a_worker()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("sync", ctx => Chat.Broadcast("a"));
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        Chat.Broadcast("b");
                        await Task.Delay(1);
                        Chat.Broadcast("c");
                    });
                    _ = Context.Tasks.Run<int>("path", async ctx =>
                    {
                        Chat.Broadcast("d");
                        return 1;
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101", "KILN101", "KILN101");
        Assert.Contains("Kiln task worker", d[0].GetMessage());
        Assert.Contains("Context.Tasks.Run", d[0].GetMessage());
    }

    [Fact]
    public async Task After_ToTick_is_tick_legal_and_stays_there()
    {
        // tasks/core semantics: after the hop, later plain awaits come back to the tick through the task's own
        // tick context; only NextGateWindow (or ConfigureAwait(false)) takes the work back to a worker.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await ctx.ToTick();
                        Chat.Broadcast("on the tick");
                        await Task.Delay(1);
                        Chat.Broadcast("still on the tick");
                        var n = await ctx.OnTick(() => 1);
                        Chat.Broadcast("an OnTick awaited on the tick stays there");
                    });
                }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task An_unawaited_ToTick_hops_nothing()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        _ = ctx.ToTick();
                        Chat.Broadcast("the hop was never awaited");
                        await Task.Delay(1);
                    });
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task NextGateWindow_takes_the_work_back_to_a_worker()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await ctx.NextGateWindow();
                        Chat.Broadcast("after the gate: a worker");
                        await ctx.ToTick();
                        Chat.Broadcast("on the tick");
                        await ctx.NextGateWindow();
                        Chat.Broadcast("back on a worker");
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101");
    }

    [Fact]
    public async Task OnTick_result_resumes_in_the_task_not_on_the_tick()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        var n = await ctx.OnTick(() => 1);
                        Chat.Broadcast("resumed in the task");
                    });
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task OnTick_and_PostToTick_delegates_run_on_the_tick()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", ctx =>
                    {
                        ctx.PostToTick(() => Chat.Broadcast("posted"));
                        _ = ctx.OnTick(() => { Chat.Broadcast("applied"); return 1; });
                        ctx.PostToTick(Apply);
                        Chat.Broadcast("the task body itself is a worker");
                    });
                }
                private void Apply() => Chat.Broadcast("a method group handed to PostToTick");
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task Branch_where_only_one_side_hops_still_warns_after_the_join()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        if (ctx.Cancellation.IsCancellationRequested) await ctx.ToTick();
                        Chat.Broadcast("only one branch hopped");
                    });
                    Context.Tasks.Run("both", async ctx =>
                    {
                        if (ctx.Cancellation.IsCancellationRequested) await ctx.ToTick();
                        else await ctx.ToTick();
                        Chat.Broadcast("both branches hopped");
                    });
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task Loops_hop_before_inside_and_after()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("before", async ctx =>
                    {
                        await ctx.ToTick();
                        while (!ctx.Cancellation.IsCancellationRequested)
                        {
                            Chat.Broadcast("in the loop, on the tick");
                            await Task.Delay(1);
                            Chat.Broadcast("after a plain await in the loop");
                            break;
                        }
                        Chat.Broadcast("after the loop");
                    });
                    Context.Tasks.Run("inside", async ctx =>
                    {
                        while (!ctx.Cancellation.IsCancellationRequested)
                        {
                            Chat.Broadcast("before the hop in this iteration");
                            await ctx.ToTick();
                            Chat.Broadcast("after the hop in this iteration");
                            break;
                        }
                        Chat.Broadcast("after a loop whose body hopped");
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101");
        Assert.Equal("Broadcast", d[0].At());
    }

    [Fact]
    public async Task Early_return_try_catch_finally_and_using()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private sealed class Gate : IDisposable { public void Dispose() { } }
                public override void OnLoad()
                {
                    Context.Tasks.Run("early", async ctx =>
                    {
                        if (ctx.Cancellation.IsCancellationRequested)
                        {
                            await ctx.ToTick();
                            Chat.Broadcast("the hopped branch");
                            return;
                        }
                        Chat.Broadcast("the branch that never hopped");
                    });
                    Context.Tasks.Run("trycatch", async ctx =>
                    {
                        try
                        {
                            await ctx.ToTick();
                            Chat.Broadcast("in the try, on the tick");
                        }
                        catch
                        {
                            Chat.Broadcast("a failed hop unwinds on a worker");
                        }
                    });
                    Context.Tasks.Run("finally-first", async ctx =>
                    {
                        try
                        {
                            await ctx.ToTick();
                            Chat.Broadcast("in the try, on the tick");
                        }
                        finally
                        {
                            Chat.Broadcast("finally also runs when the hop was refused");
                        }
                    });
                    Context.Tasks.Run("finally-after", async ctx =>
                    {
                        await ctx.ToTick();
                        using (var gate = new Gate())
                        {
                            Chat.Broadcast("inside the using, on the tick");
                        }
                        Chat.Broadcast("after the using, still on the tick");
                        try { Chat.Broadcast("in a later try"); }
                        finally { Chat.Broadcast("in its finally"); }
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101", "KILN101");
    }

    [Fact]
    public async Task Awaits_in_a_finally_govern_what_follows_it()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("hop-in-finally", async ctx =>
                    {
                        try { }
                        finally { await ctx.ToTick(); }
                        Chat.Broadcast("after a finally that hopped");
                    });
                    Context.Tasks.Run("escape-in-finally", async ctx =>
                    {
                        await ctx.ToTick();
                        try { }
                        finally { await Task.Delay(1).ConfigureAwait(false); }
                        Chat.Broadcast("after a finally that escaped");
                    });
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task Blocking_after_a_hop_is_KILN105_not_before_it()
    {
        var d = await Run(new BlockingAnalyzer(), """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        Thread.Sleep(10);
                        await ctx.ToTick();
                        Thread.Sleep(10);
                    });
                }
            }
            """);
        Ids(d, "KILN105");
    }

    [Fact]
    public async Task Method_group_passed_to_Run()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("async", WorkAsync);
                    Context.Tasks.Run("sync", WorkSync);
                }
                private async Task WorkAsync(KilnTaskContext ctx)
                {
                    Chat.Broadcast("a method group body starts on a worker");
                    await ctx.ToTick();
                    Chat.Broadcast("after its own hop");
                }
                private void WorkSync(KilnTaskContext ctx) => Chat.Broadcast("a synchronous method group body");
            }
            """);
        Ids(d, "KILN101", "KILN101");
    }

    [Fact]
    public async Task Local_functions_called_from_the_body()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    void Local() => Chat.Broadcast("a local function called from the task");
                    void LocalOnTick() => Chat.Broadcast("a local function called after the hop");
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        void Inner() => Chat.Broadcast("a local function declared in the task");
                        Inner();
                        Local();
                        await ctx.ToTick();
                        LocalOnTick();
                    });
                    LocalOnTick();
                }
            }
            """);
        // Inner (worker), Local (worker), LocalOnTick called after the hop (tick) and from OnLoad (tick): no warning.
        Ids(d, "KILN101", "KILN101");
    }

    [Fact]
    public async Task Local_function_handed_to_Run_as_a_method_group()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Task LocalWork(KilnTaskContext ctx)
                    {
                        Chat.Broadcast("a local function handed to Run");
                        return Task.CompletedTask;
                    }
                    Context.Tasks.Run("scan", LocalWork);
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task Local_function_called_from_both_a_worker_and_the_tick_warns()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    void Local() => Chat.Broadcast("called from both threads");
                    Local();
                    Context.Tasks.Run("scan", ctx => Local());
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task Unknown_helpers_called_from_the_body_are_not_followed()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", ctx =>
                    {
                        Helper();
                        var stored = new Action(() => Chat.Broadcast("a stored delegate is not judged here"));
                    });
                }
                private void Helper() => Chat.Broadcast("inside an ordinary helper: not followed from its callers");
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task Look_alikes_do_not_hop()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private sealed class FakeContext
                {
                    public Task ToTick() => Task.CompletedTask;
                    public Task NextGateWindow() => Task.CompletedTask;
                }
                public override void OnLoad()
                {
                    var fake = new FakeContext();
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await fake.ToTick();
                        Chat.Broadcast("a user method named ToTick is no hop");
                        await fake.NextGateWindow();
                        Chat.Broadcast("a user method named NextGateWindow changes nothing either");
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101");
    }

    [Fact]
    public async Task Scheduler_NextTick_is_still_a_tick_hop_inside_a_task()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await Context.Scheduler.NextTick();
                        Chat.Broadcast("after the old hop");
                        await Task.Delay(1);
                        Chat.Broadcast("its later awaits come back to the tick too");
                    });
                }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task ConfigureAwait_false_after_ToTick_goes_back_to_a_worker()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await ctx.ToTick();
                        Chat.Broadcast("on the tick");
                        await Task.Delay(1).ConfigureAwait(false);
                        Chat.Broadcast("escaped to the thread pool");
                        await ctx.ToTick();
                        Chat.Broadcast("hopped again");
                    });
                    Context.Tasks.Run("escape-first", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        Chat.Broadcast("never hopped");
                    });
                }
            }
            """);
        Ids(d, "KILN101", "KILN101");
        Assert.Contains("ConfigureAwait(false)", d[0].GetMessage());
    }

    [Fact]
    public async Task A_lambda_a_library_runs_at_once_takes_the_thread_around_it()
    {
        // Kept from the old classifier: a LINQ/List.ForEach lambda runs synchronously where it is called, so a
        // ConfigureAwait(false) before the call in an otherwise unjudged method still settles its thread.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public async Task Work()
                {
                    await Task.Delay(1).ConfigureAwait(false);
                    new List<int> { 1 }.ForEach(i => Chat.Broadcast("run at once, off the tick"));
                }
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task A_Tasks_Run_reached_through_a_local_variable_still_counts()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var tasks = Context.Tasks;
                    tasks.Run("scan", ctx => Chat.Broadcast("by symbol, not by receiver text"));
                }
            }
            """);
        Ids(d, "KILN101");
    }
}
