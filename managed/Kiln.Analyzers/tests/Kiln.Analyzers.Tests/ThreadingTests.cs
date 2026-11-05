// SPDX-License-Identifier: MIT
using Xunit;
using static Kiln.Analyzers.Tests.Harness;

namespace Kiln.Analyzers.Tests;

public sealed class TickThreadTests
{
    private static readonly TickThreadAnalyzer A = new();

    [Fact]
    public async Task Task_run_thread_and_timers_are_off_thread()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                System.Threading.Timer? _t;
                System.Timers.Timer _tt = new(1000);
                public override void OnLoad()
                {
                    Task.Run(() => Chat.Broadcast("a"));
                    new Thread(() => Combat.SetHealth(new EntityId(5), 10)).Start();
                    _t = new System.Threading.Timer(_ => Chat.Broadcast("b"), null, 0, 1000);
                    _tt.Elapsed += (s, e) => Chat.Broadcast("c");
                    ThreadPool.QueueUserWorkItem(_ => Chat.Broadcast("d"));
                    Parallel.For(0, 2, i => Chat.Broadcast("e"));
                }
            }
            """);
        Ids(d, "KILN101", "KILN101", "KILN101", "KILN101", "KILN101", "KILN101");
        Assert.Contains("Task.Run", d[0].GetMessage());
        Assert.Contains("new Thread", d[1].GetMessage());
        Assert.Contains("System.Threading.Timer", d[2].GetMessage());
        Assert.Contains("Elapsed", d[3].GetMessage());
        Assert.Equal("Broadcast", d[0].At());
    }

    [Fact]
    public async Task On_the_tick_thread_or_posted_back_is_fine()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Chat.Broadcast("in OnLoad");
                    Context.Tick.Pre += (in TickInfo t) => Chat.Broadcast("tick");
                    Task.Run(() => Context.Scheduler.Post(() => Chat.Broadcast("posted")));
                    Task.Run(() => { if (Game.IsTickThread) Chat.Broadcast("guarded"); });
                    Task.Run(() => Log.Info("logging is thread-safe"));
                    Task.Run(() => Mods.Send("other", "m"));
                }
                [Subscribe] private void OnStarted(ServerStartedEvent e) => Chat.Broadcast("started");
            }
            """);
        Ids(d);
    }

    [Fact]
    public async Task After_ConfigureAwait_false_until_NextTick()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public async Task Work()
                {
                    Chat.Broadcast("before");
                    await Task.Delay(10);
                    Chat.Broadcast("still on the tick thread");
                    await Task.Delay(10).ConfigureAwait(false);
                    Chat.Broadcast("thread pool");
                    await Context.Scheduler.NextTick();
                    Chat.Broadcast("back");
                }
            }
            """);
        Ids(d, "KILN101");
        Assert.Contains("ConfigureAwait(false)", d[0].GetMessage());
    }

    [Fact]
    public async Task Method_group_handed_to_Task_Run()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Task.Run(Work);
                private void Work() => Chat.Broadcast("x");
                private void NotStarted() => Chat.Broadcast("y");
            }
            """);
        Ids(d, "KILN101");
    }

    [Fact]
    public async Task TickThreadOnly_attribute_is_honoured()
    {
        // A future Kiln.Api may mark members itself (proposed Kiln.TickThreadOnlyAttribute).
        var lib = Library("""
            namespace Kiln
            {
                [AttributeUsage(AttributeTargets.All)] public sealed class TickThreadOnlyAttribute : Attribute { }
                public static class Marked
                {
                    [TickThreadOnly] public static void Touch() { }
                    [TickThreadOnly] public static int Value { get; set; }
                    public static void Free() { }
                }
            }
            """, "Kiln.Marked");
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Task.Run(() => { Marked.Touch(); Marked.Value = 3; Marked.Free(); });
            }
            """, extra: [lib]);
        Ids(d, "KILN101", "KILN101");
    }

    [Fact]
    public async Task Fix_wraps_the_statement_in_Scheduler_Post()
    {
        var fixedSource = await Fix(A, new TickThreadCodeFix(), """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    Task.Run(() =>
                    {
                        Chat.Broadcast("a");
                    });
                }
            }
            """);
        Assert.Contains("Context.Scheduler.Post(() => Chat.Broadcast(\"a\"));", fixedSource);
    }

    [Fact]
    public async Task Fix_wraps_an_expression_body()
    {
        var fixedSource = await Fix(A, new TickThreadCodeFix(), """
            public sealed class M : Mod
            {
                public override void OnLoad() => Task.Run(() => Chat.Broadcast("a"));
            }
            """);
        Assert.Contains("Task.Run(() => Context.Scheduler.Post(() => Chat.Broadcast(\"a\")))", fixedSource);
    }
}

public sealed class AsyncVoidTests
{
    private static readonly AsyncVoidAnalyzer A = new();

    [Fact]
    public async Task Async_void_methods_and_lambdas()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override async void OnLoad() => await Task.Delay(1);
                private async void Helper() => await Task.Delay(1);
                [Subscribe] private async void OnStarted(ServerStartedEvent e) => await Task.Delay(1);
                private async Task Fine() => await Task.Delay(1);
                public void Lambdas()
                {
                    Context.Scheduler.Post(async () => await Task.Delay(1));
                    Task.Run(async () => await Task.Delay(1));
                    Func<Task> f = async () => await Task.Delay(1);
                }
            }
            """);
        Ids(d, "KILN102", "KILN102", "KILN102", "KILN102");
        Assert.Contains("ModsLoaded", d[0].GetMessage());
        Assert.False(d[0].Properties.ContainsKey(AsyncVoidAnalyzer.FixKey));
        Assert.Equal("task", d[1].Properties[AsyncVoidAnalyzer.FixKey]);
        Assert.Contains("[Subscribe]", d[2].GetMessage());
        Assert.Contains("Action", d[3].GetMessage());
    }

    [Fact]
    public async Task No_fix_when_used_as_a_delegate()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad() => Context.Scheduler.Post(Later);
                private async void Later() => await Task.Delay(1);
            }
            """);
        Ids(d, "KILN102");
        Assert.False(d[0].Properties.ContainsKey(AsyncVoidAnalyzer.FixKey));
    }

    [Fact]
    public async Task Fix_returns_Task()
    {
        var s = await Fix(A, new AsyncVoidCodeFix(), """
            public sealed class M : Mod
            {
                private async void Helper() => await Task.Delay(1);
            }
            """);
        Assert.Contains("private async Task Helper()", s);
    }
}

public sealed class BlockingTests
{
    private static readonly BlockingAnalyzer A = new();

    [Fact]
    public async Task Blocking_in_tick_code()
    {
        var d = await Run(A, """
            public sealed class M : Mod
            {
                Task<int> _job = Task.FromResult(1);
                public override void OnLoad()
                {
                    Thread.Sleep(100);
                    Thread.Sleep(0);
                    Context.Tick.Pre += (in TickInfo t) => _job.Wait();
                    Context.Tick.Post += OnPost;
                    Task.Run(() => Thread.Sleep(100));
                }
                private void OnPost(in TickInfo t) => Thread.Sleep(5);
                [Subscribe] private void OnStarted(ServerStartedEvent e) { var r = _job.Result; }
                private void NotATickHandler() => Thread.Sleep(5);
                public override void OnUnload() => Task.FromResult(1).Wait();
            }
            """);
        Ids(d, "KILN105", "KILN105", "KILN105", "KILN105");
        Assert.Contains("OnLoad", d[0].GetMessage());
        Assert.Contains("Tick.Pre handler", d[1].GetMessage());
        Assert.Contains("deadlocks", d[1].GetMessage());
    }

    [Fact]
    public async Task Kiln_tasks_that_are_always_complete()
    {
        // Spawn.Entity returns Task.FromResult (KilnApis.CompletedTasks): its Result never blocks.
        var d = await Run(A, """
            public sealed class M : Mod
            {
                public override void OnLoad()
                {
                    var id = Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(0, 0, 0)).Result;
                    var t = Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(0, 0, 0));
                    var same = t.Result;    // a local that only ever holds that task
                    var u = Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(0, 0, 0));
                    u = Later();
                    var later = u.Result;   // written again: not proven complete
                    var v = Later();
                    var other = v.Result;   // not one of those APIs
                }
                private static async Task<EntityId> Later() { await Task.Yield(); return EntityId.None; }
            }
            """);
        Ids(d, "KILN105", "KILN105");
        Assert.Equal("u.Result", d[0].At());
        Assert.Equal("v.Result", d[1].At());
    }

    [Fact]
    public async Task Awaited_task_result_is_not_blocking()
    {
        var d = await Run(A, """
            using Kiln.Testing;
            public sealed class T
            {
                [KilnTest] public async Task Reads(TestContext c)
                {
                    var t = Task.FromResult(2);
                    var u = Compute();
                    await u;
                    Assert.Equal(2, u.Result);
                    var v = Compute().Result;
                }
                private static Task<int> Compute() => Task.FromResult(2);
            }
            """);
        Ids(d, "KILN105");
        Assert.Contains("[KilnTest]", d[0].GetMessage());
        Assert.Equal("awaitResult", d[0].Properties[BlockingAnalyzer.FixKey]);
    }

    [Fact]
    public async Task Fix_result_to_await()
    {
        var s = await Fix(A, new BlockingCodeFix(), """
            using Kiln.Testing;
            public sealed class T
            {
                [KilnTest] public async Task Reads() { var v = Compute().Result; }
                private static Task<int> Compute() => Task.Delay(1).ContinueWith(_ => 2);
            }
            """);
        Assert.Contains("var v = await Compute();", s);
    }

    [Fact]
    public async Task Fix_sleep_to_delay()
    {
        var s = await Fix(A, new BlockingCodeFix(), """
            using Kiln.Testing;
            public sealed class T
            {
                [KilnTest] public async Task Waits() { Thread.Sleep(50); await Task.Yield(); }
            }
            """);
        Assert.Contains("await Task.Delay(50);", s);
    }
}
