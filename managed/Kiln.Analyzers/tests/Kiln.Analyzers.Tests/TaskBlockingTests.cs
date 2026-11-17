// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN105 around Kiln tasks (T6): <c>KilnTask.Wait</c>, blocking on the <c>Task</c> that
/// <c>AsTask()</c> hands out, a <c>ctx.ToTick()</c> that is never awaited, and gated world calls whose
/// task scope is gone (or that write from the tick). Kiln tasks compile against the real Kiln.Api;
/// the gated world face of T9/T10 (<c>Kiln.WorldConcurrent</c>: reads, and <c>TryCompareExchange</c>
/// for the gated write) does not exist yet, so a fixture library carries its documented shape.</summary>
public sealed class TaskBlockingTests
{
    private static readonly BlockingAnalyzer A = new();

    private static readonly MetadataReference Gated = Library("""
        namespace Kiln
        {
            public static class WorldConcurrent
            {
                public static bool IsAvailable => true;
                public static int Read(int entity) => entity;
                public static int Query(int entity) => entity;
                public static bool TryCompareExchange(int entity, int expected, int desired) => true;
            }
        }
        """, "Kiln.WorldFixture");

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
        // OnLoad, the Pre handler, the task bodies (before and after a hop, after ConfigureAwait(false),
        // and a method group handed to Run). Not: Task.Run (a thread of the mod's own, where Wait is
        // allowed), a look-alike Wait, unknown code.
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("KilnTask.Wait", x.GetMessage()));
        Assert.Contains("tick thread (OnLoad)", d[0].GetMessage());
        Assert.Contains("Kiln task worker", d[2].GetMessage());
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
                    });
                }
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

    // ---- gated world calls (T9/T10 shape) ----

    [Fact]
    public async Task Gated_reads_are_fine_in_a_task_and_on_the_tick_writes_are_not_on_the_tick()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var readOnLoad = WorldConcurrent.Read(1);
                    Context.Tick.Pre += (in TickInfo t) => WorldConcurrent.Read(1);
                    Context.Tick.Pre += (in TickInfo t) => WorldConcurrent.TryCompareExchange(1, 0, 1);
                    Context.Tick.Post += OnPost;
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        var a = WorldConcurrent.Read(1);
                        var wrote = WorldConcurrent.TryCompareExchange(1, 0, 1);
                        await ctx.ToTick();
                        var b = WorldConcurrent.Read(1);
                        var onTick = WorldConcurrent.TryCompareExchange(1, 0, 1);
                        await ctx.NextGateWindow();
                        var afterGate = WorldConcurrent.TryCompareExchange(1, 0, 1);
                        ctx.PostToTick(() => WorldConcurrent.TryCompareExchange(1, 0, 1));
                        var applied = ctx.OnTick(() => WorldConcurrent.Read(1));
                    });
                }
                private void OnPost(in TickInfo t) => WorldConcurrent.TryCompareExchange(1, 0, 1);
            }
            """, extra: [Gated]);
        // The Pre write, the Post handler's write, the write after a hop, the PostToTick write.
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("gated", x.GetMessage()));
        Assert.All(d, x => Assert.Contains("busy", x.GetMessage()));
    }

    [Fact]
    public async Task Gated_calls_after_ConfigureAwait_false_have_no_task_scope()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        var a = WorldConcurrent.Read(1);
                        await Task.Delay(1).ConfigureAwait(false);
                        var escapedRead = WorldConcurrent.Read(1);
                        var escapedWrite = WorldConcurrent.TryCompareExchange(1, 0, 1);
                        await ctx.NextGateWindow();
                        var back = WorldConcurrent.Read(1);
                        await ctx.ToTick();
                        await Task.Delay(1).ConfigureAwait(true);
                        var stillTick = WorldConcurrent.Read(1);
                    });
                    Task.Run(() => WorldConcurrent.Read(1));
                }
                private void Unknown() => WorldConcurrent.Read(1);
            }
            """, extra: [Gated]);
        // The two calls after ConfigureAwait(false), and the raw Task.Run call. Not: in-task calls,
        // a call re-scoped by a gate window, ConfigureAwait(true), or unknown code.
        Ids(d, "KILN105", "KILN105", "KILN105");
        Assert.All(d, x => Assert.Contains("task scope", x.GetMessage()));
    }

    [Fact]
    public async Task A_look_alike_gated_face_in_another_assembly_is_not_gated()
    {
        var fake = Library("""
            namespace Kiln
            {
                public static class WorldConcurrent
                {
                    public static int Read(int entity) => entity;
                }
            }
            """, "SharedLib");
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        var a = WorldConcurrent.Read(1);
                    });
                }
            }
            """, extra: [fake]);
        Ids(d);
    }

    [Fact]
    public async Task The_gated_faces_availability_check_is_not_a_gated_call()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tick.Pre += (in TickInfo t) => Use(WorldConcurrent.IsAvailable);
                    Context.Tasks.Run("scan", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        Use(WorldConcurrent.IsAvailable);
                    });
                }
                private void Use(bool b) { }
            }
            """, extra: [Gated]);
        Ids(d);
    }
}
