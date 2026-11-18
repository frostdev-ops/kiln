// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

/// <summary>KILN105's gated world row: a task-thread world read (<c>WorldConcurrent</c>) where no Kiln task's
/// scope is installed, so Kiln refuses it at run time: after <c>await …ConfigureAwait(false)</c> in a task (on
/// every path or some), in <c>Task.Run</c> and on a thread of the mod's own. The reads compile against the real
/// Kiln.World; the test sources opt in to its experimental API as a mod does.</summary>
public sealed class TaskGatedTests
{
    private static readonly BlockingAnalyzer A = new();

    [Fact]
    public async Task Every_read_after_ConfigureAwait_false_is_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("escaped", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        var open = WorldConcurrent.Open(new EntityId(12));
                        var e = open.Value;
                        WorldConcurrent.Validate(e);
                        WorldConcurrent.Read<int>(e, "Health");
                        WorldConcurrent.ReadBytes(e, "Health", new byte[8]);
                        WorldConcurrent.ReadMany(e, new ComponentSet("Health"));
                        WorldConcurrent.Query(new EntityQuery(WorldKind.Server, "Health"), new ConcurrentEntity[4]);
                        WorldConcurrent.Template(e);
                    });
                }
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.Equal(["WorldConcurrent.Open", "WorldConcurrent.Validate", "WorldConcurrent.Read", "WorldConcurrent.ReadBytes",
            "WorldConcurrent.ReadMany", "WorldConcurrent.Query", "WorldConcurrent.Template"],
            d.Select(x => x.GetMessage().Split('\'')[1]));
        Assert.All(d, x => Assert.Contains("ConfigureAwait(false)", x.GetMessage()));
        Assert.All(d, x => Assert.Contains("NextGateWindow", x.GetMessage()));
    }

    [Fact]
    public async Task ConfigureAwait_false_on_some_paths_is_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("joined", async ctx =>
                    {
                        if (DateTime.Now.Second == 0) await Task.Delay(1).ConfigureAwait(false);
                        WorldConcurrent.Open(new EntityId(12));
                    });
                }
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("ConfigureAwait(false)", d[0].GetMessage());
        Assert.Contains("some path", d[0].GetMessage());
    }

    [Fact]
    public async Task Task_Run_and_threads_of_the_mods_own_are_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("inner", async ctx =>
                    {
                        await Task.Run(() => WorldConcurrent.Open(new EntityId(1)));
                    });
                    Task.Run(() => WorldConcurrent.Open(new EntityId(2)));
                    new Thread(() => WorldConcurrent.Open(new EntityId(3))).Start();
                    Task.Run(Helper);
                }
                private void Helper() => WorldConcurrent.Open(new EntityId(4));
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.Contains("Task.Run", d[0].GetMessage());
        Assert.Contains("Context.Tasks.Run", d[0].GetMessage());
        Assert.Contains("new Thread", d[2].GetMessage());
    }

    [Fact]
    public async Task Inside_the_task_on_the_tick_and_after_a_hop_back_are_not_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    WorldConcurrent.Open(new EntityId(1));                          // the tick (OnLoad)
                    Context.Tick.Pre += (in TickInfo t) => WorldConcurrent.Open(new EntityId(2));
                    Context.Tasks.Run("inside", async ctx =>
                    {
                        WorldConcurrent.Open(new EntityId(3));
                        await Task.Yield();
                        WorldConcurrent.Open(new EntityId(4));
                        await Task.Delay(1).ConfigureAwait(true);
                        WorldConcurrent.Open(new EntityId(5));
                        await ctx.ToTick();
                        WorldConcurrent.Open(new EntityId(6));
                        var n = await ctx.OnTick(() => WorldConcurrent.Open(new EntityId(7)).Status);
                    });
                    Context.Tasks.Run("back", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        await ctx.NextGateWindow();
                        WorldConcurrent.Open(new EntityId(8));
                    });
                    Context.Tasks.Run("back to the tick", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        await ctx.ToTick();
                        WorldConcurrent.Open(new EntityId(9));
                    });
                    Context.Tasks.Run("group", Body);
                }
                private void Body(KilnTaskContext ctx) => WorldConcurrent.Open(new EntityId(10));
            }
            """);
        Ids(d);
    }

    /// <summary>The entity ids of the reported reads, in source order: each test numbers its reads.</summary>
    private static int[] Reported(System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic> d) =>
        d.Where(x => x.Id == "KILN105").Select(x => int.Parse(System.Text.RegularExpressions.Regex.Match(x.At(), @"EntityId\((\d+)\)").Groups[1].Value)).ToArray();

    [Fact]
    public async Task ConfigureAwait_false_coming_round_a_loop_is_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("for", async ctx =>
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            WorldConcurrent.Open(new EntityId(1));       // escaped from the second round on
                            await Task.Delay(1).ConfigureAwait(false);
                        }
                    });
                    Context.Tasks.Run("foreach", async ctx =>
                    {
                        foreach (var x in new[] { 1, 2 }) await Task.Delay(x).ConfigureAwait(false);
                        WorldConcurrent.Open(new EntityId(2));           // after the loop: escaped when it ran once
                    });
                    Context.Tasks.Run("while", async ctx =>
                    {
                        while (true)
                        {
                            try { if (DateTime.Now.Second == 0) break; await Task.Delay(1).ConfigureAwait(false); break; }
                            finally { }
                        }
                        WorldConcurrent.Open(new EntityId(3));
                    });
                    Context.Tasks.Run("hop each round", async ctx =>
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            await Task.Delay(1).ConfigureAwait(false);
                            await ctx.NextGateWindow();
                            WorldConcurrent.Open(new EntityId(4));       // back in the task every round: fine
                        }
                    });
                    Context.Tasks.Run("return in the loop", async ctx =>
                    {
                        foreach (var x in new[] { 1, 2 })
                        {
                            WorldConcurrent.Open(new EntityId(5));       // runs once, in the task: fine
                            await Task.Delay(x).ConfigureAwait(false);
                            return;
                        }
                    });
                }
            }
            """);
        Assert.Equal([1, 2, 3], Reported(d));
    }

    [Fact]
    public async Task Catch_and_finally_after_ConfigureAwait_false_in_the_try_are_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("finally", async ctx =>
                    {
                        try { await Task.Delay(1).ConfigureAwait(false); }
                        finally { WorldConcurrent.Open(new EntityId(1)); }
                    });
                    Context.Tasks.Run("catch", async ctx =>
                    {
                        try { await Task.Delay(1).ConfigureAwait(false); throw new InvalidOperationException(); }
                        catch (InvalidOperationException) { WorldConcurrent.Open(new EntityId(2)); }
                    });
                    Context.Tasks.Run("in the task", async ctx =>
                    {
                        try { await Task.Delay(1); }
                        catch (InvalidOperationException) { WorldConcurrent.Open(new EntityId(3)); }
                        finally { WorldConcurrent.Open(new EntityId(4)); }
                    });
                }
            }
            """);
        Assert.Equal([1, 2], Reported(d));
    }

    [Fact]
    public async Task OrOnTick_off_tick_delegate_runs_where_it_is_called()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Context.Tasks.Run("escaped", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        var s = await WorldConcurrent.OrOnTick(ctx,
                            () => WorldConcurrent.Open(new EntityId(1)).Status,
                            () => WorldConcurrent.Open(new EntityId(2)).Status);   // through ctx.OnTick: the tick
                    });
                    Context.Tasks.Run("Task.Run", async ctx =>
                    {
                        await Task.Run(() => WorldConcurrent.OrOnTick(ctx, () => WorldConcurrent.Open(new EntityId(3)).Status, () => default(ReadStatus)));
                    });
                    Context.Tasks.Run("in the task", async ctx =>
                    {
                        var s = await WorldConcurrent.OrOnTick(ctx,
                            () => WorldConcurrent.Open(new EntityId(4)).Status,
                            () => WorldConcurrent.Open(new EntityId(5)).Status);
                    });
                }
            }
            """);
        Assert.Equal([1, 3], Reported(d));
    }

    [Fact]
    public async Task Await_foreach_and_await_using_with_ConfigureAwait_false_are_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            public sealed class M : Mod
            {
                private static async IAsyncEnumerable<int> Seq() { await Task.Yield(); yield return 1; }
                public override void OnLoad()
                {
                    Context.Tasks.Run("foreach", async ctx =>
                    {
                        WorldConcurrent.Open(new EntityId(1));
                        await foreach (var x in Seq().ConfigureAwait(false)) WorldConcurrent.Open(new EntityId(2));
                        WorldConcurrent.Open(new EntityId(3));
                    });
                    Context.Tasks.Run("using", async ctx =>
                    {
                        await using (new MemoryStream().ConfigureAwait(false)) WorldConcurrent.Open(new EntityId(4));
                        WorldConcurrent.Open(new EntityId(5));
                    });
                    Context.Tasks.Run("using declaration", async ctx =>
                    {
                        {
                            await using var s = new MemoryStream().ConfigureAwait(false);
                            WorldConcurrent.Open(new EntityId(6));
                        }
                        WorldConcurrent.Open(new EntityId(7));
                    });
                    Context.Tasks.Run("plain", async ctx =>
                    {
                        await foreach (var x in Seq()) WorldConcurrent.Open(new EntityId(8));
                        await using (var s = new MemoryStream()) WorldConcurrent.Open(new EntityId(9));
                        WorldConcurrent.Open(new EntityId(10));
                    });
                }
            }
            """);
        Assert.Equal([2, 3, 5, 7], Reported(d));
    }

    [Fact]
    public async Task Any_thread_members_lookalikes_and_unknown_code_are_not_reported()
    {
        var d = await Run(A, """
            #pragma warning disable KILNX001
            namespace Mine { public static class WorldConcurrent { public static int Open(int id) => id; } }
            public sealed class M : Mod
            {
                private Func<object>? _stored;
                public override void OnLoad()
                {
                    Context.Tasks.Run("escaped", async ctx =>
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                        var available = Kiln.WorldConcurrent.IsAvailable && Kiln.WorldConcurrent.IsPresent(WorldKind.Server);
                        var n = await Kiln.WorldConcurrent.OrOnTick(ctx, () => 1, () => 2);
                        Mine.WorldConcurrent.Open(3);
                        var set = new ComponentSet("Health");
                    });
                    _stored = () => Kiln.WorldConcurrent.Open(new EntityId(1));
                    Task.Run(() => Mine.WorldConcurrent.Open(4));
                }
                private object Unknown() => Kiln.WorldConcurrent.Open(new EntityId(2));
            }
            """);
        Ids(d);
    }
}
