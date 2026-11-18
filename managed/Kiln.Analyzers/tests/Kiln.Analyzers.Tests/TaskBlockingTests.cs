// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN105 around Kiln tasks: <c>KilnTask.Wait</c>, blocking on the <c>Task</c> that
/// <c>AsTask()</c> hands out, and a <c>ctx.ToTick()</c> that is never awaited. Kiln tasks compile against
/// the real Kiln.Api.</summary>
public sealed class TaskBlockingTests
{
    private static readonly BlockingAnalyzer A = new();

    // ---- KilnTask.Wait ----

    [Fact]
    public async Task KilnTask_wait_on_the_tick_and_in_a_task()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private sealed class Fake { public void Wait() { } }
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run("scan", ctx => { });
                    job.Wait();
                    Context.Tick.Pre += (in TickInfo t) => job.Wait();
                    Context.Tasks.Run("outer", ctx => job.Wait());
                    Context.Tasks.Run("hopped", async ctx =>
                    {
                        await ctx.ToTick();
                        job.Wait();
                    });
                    Context.Tasks.Run("escaped", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        job.Wait();
                    });
                    Task.Run(() => job.Wait());
                    new Fake().Wait();
                    Context.Tasks.Run("group", GroupBody);
                }
                private KilnTask _job = null!;
                private void GroupBody(KilnTaskContext ctx) => _job.Wait();
                private void OwnCode(KilnTask job) => job.Wait();
            }
            """);
        // OnLoad, the Pre handler, the task bodies (before and after a hop, and a method group handed to
        // Run). Not: after ConfigureAwait(false) (usually a .NET pool thread, where Wait is allowed, unless
        // the await finished at once: unsure, so quiet), Task.Run (a thread of the mod's own, where Wait is
        // allowed), a look-alike Wait, unknown code.
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("KilnTask.Wait", x.GetMessage()));
        Assert.Contains("tick thread (OnLoad)", d[0].GetMessage());
        Assert.Contains("Kiln task worker", d[2].GetMessage());
    }

    [Fact]
    public async Task ConfigureAwait_true_keeps_the_task_and_false_leaves_it()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run<int>("path", async ctx => 1);
                    Context.Tasks.Run("kept", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(true);
                        job.Wait();
                        var a = job.AsTask().Result;
                    });
                    Context.Tasks.Run("left", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        job.Wait();
                        var b = job.AsTask().Result;
                        job.AsTask().GetAwaiter().GetResult();
                    });
                    Context.Tasks.Run("joined", async ctx =>
                    {
                        if (job.State == KilnTaskState.Queued) await Task.Delay(1).ConfigureAwait(false);
                        job.Wait();
                    });
                }
            }
            """);
        // ConfigureAwait(true) resumes in the task: both reported. After ConfigureAwait(false), on every
        // path or only some, the code may be on a .NET pool thread where blocking is the mod's own call.
        Ids(d, "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("Kiln task", x.GetMessage()));
    }

    [Fact]
    public async Task A_hop_after_ConfigureAwait_false_brings_the_task_back()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run<int>("path", async ctx => 1);
                    Context.Tasks.Run("gate", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        await ctx.NextGateWindow();
                        job.Wait();
                    });
                    Context.Tasks.Run("hop", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        await ctx.ToTick();
                        var r = job.AsTask().Result;
                    });
                }
            }
            """);
        Ids(d, "KILN105", "KILN105");
    }

    [Fact]
    public async Task Threads_and_pool_work_a_task_starts_are_its_own()
    {
        var d = await Run(A, """
            using System.Threading;
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run<int>("path", async ctx => 1);
                    Context.Tasks.Run("starts", async ctx =>
                    {
                        new Thread(() => job.Wait()).Start();
                        await Task.Run(() => job.AsTask().Result);
                        ThreadPool.QueueUserWorkItem(_ => job.Wait());
                        var plain = Task.Run(() => 5);
                        plain.Wait();
                    });
                }
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task KilnTask_wait_fix_awaits_the_task()
    {
        var s = await Fix(A, new BlockingCodeFix(), """
            using Kiln.Testing;
            public sealed class T
            {
                KilnTask _job = null!;
                [KilnTest] public async Task Waits() { _job.Wait(); await Task.Yield(); }
            }
            """);
        Assert.Contains("await _job;", s);
    }

    // ---- AsTask() escapes ----

    [Fact]
    public async Task Blocking_on_the_task_from_AsTask_on_the_tick_and_in_a_task()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private sealed class Fake
                {
                    public Task<int> AsTask() => Task.FromResult(1);
                }
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run<int>("path", async ctx => 1);
                    var onTick = job.AsTask().Result;
                    job.AsTask().Wait();
                    var alsoTick = job.AsTask().GetAwaiter().GetResult();
                    Context.Tasks.Run("outer", ctx =>
                    {
                        var a = job.AsTask().Result;
                        job.AsTask().Wait();
                        var b = job.AsTask().GetAwaiter().GetResult();
                        var alias = job.AsTask();
                        var c = alias.Result;
                        var plain = Task.FromResult(3);
                        var d = plain.Result;
                        var fake = new Fake().AsTask().Result;
                    });
                    var direct = job.Result;
                    var directAwaiter = job.GetAwaiter().GetResult();
                }
            }
            """);
        // Three on the tick, four in the task (the alias included). Not: a plain Task in a task,
        // a look-alike AsTask, or KilnTask's own Result/GetAwaiter (they throw instead of blocking).
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.Contains("AsTask()", d[0].GetMessage());
        Assert.Contains("AsTask()", d[3].GetMessage());
    }

    [Fact]
    public async Task An_AsTask_alias_written_again_is_not_proven()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var job = Context.Tasks.Run<int>("path", async ctx => 1);
                    Context.Tasks.Run("outer", ctx =>
                    {
                        var alias = job.AsTask();
                        alias = Task.FromResult(2);
                        var c = alias.Result;
                        var swapped = job.AsTask();
                        int n;
                        (swapped, n) = (Task.FromResult(2), 1);
                        var e = swapped.Result;
                        var refd = job.AsTask();
                        Replace(ref refd);
                        var f = refd.Result;
                    });
                }
                private static void Replace(ref Task<int> t) => t = Task.FromResult(4);
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task AsTask_result_fix_awaits()
    {
        var s = await Fix(A, new BlockingCodeFix(), """
            using Kiln.Testing;
            public sealed class T
            {
                KilnTask<int> _job = null!;
                [KilnTest] public async Task Reads() { var v = _job.AsTask().Result; }
            }
            """);
        Assert.Contains("var v = await _job.AsTask();", s);
    }

    // ---- a tick hop that never happens ----

    [Fact]
    public async Task A_ToTick_that_is_never_awaited_hops_nothing()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                private sealed class FakeContext { public Task ToTick() => Task.CompletedTask; }
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        ctx.ToTick();
                        _ = ctx.ToTick();
                        var never = ctx.ToTick();
                        var hop = ctx.ToTick();
                        await hop;
                        await ctx.ToTick();
                    });
                    Context.Tasks.Run("other", ctx => { ctx.ToTick(); });
                    Context.Tasks.Run("expr", ctx => ctx.ToTick());
                }
                private async Task Helper(KilnTaskContext ctx) { ctx.ToTick(); await Task.Yield(); }
                private KilnTaskContext.TickHop Kept(KilnTaskContext ctx) => ctx.ToTick();
                private void LookAlike() { new FakeContext().ToTick(); }
            }
            """);
        // The statement, the `_ =`, the local nobody awaits, the ones in the other tasks (a block and an
        // expression body), and Helper's (anywhere, not only in a task). Not: a hop kept and awaited,
        // a returned hop, a look-alike.
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("never awaited", x.GetMessage()));
    }
}
