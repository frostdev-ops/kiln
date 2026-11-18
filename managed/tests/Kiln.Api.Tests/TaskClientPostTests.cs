// SPDX-License-Identifier: MIT
// A client mod's way back from a task (docs/kiln/modding/tasks.md, "Client mods"). On a client joined to someone else's
// server there is no server world: the hops refuse naming tasks.pool, and Client.Post hands the task's result to the
// game's main thread, before the client world's next step. It is not a hop of the task: untracked (the task can end
// first), not cancelled with it, dropped once the mod has unloaded, its exceptions the mod's. Awaiting Client.NextTick()
// in a task leaves the task, and an unload that drops its post leaves the task waiting.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

// The task bodies stand in for mod code (their own waits, no test token).
#pragma warning disable xUnit1031, xUnit1051

namespace Kiln.Api.Tests;

public sealed class TaskClientPostTests : TaskTest
{
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(20);

    public TaskClientPostTests()
    {
        FakeKilnClient.Install(Backend);
        Pool.StopSession();  // joined to someone else's server: no server world here
    }

    public override void Dispose()
    {
        try
        {
            FakeKilnClient.Active = false;
        }
        finally
        {
            base.Dispose();
        }
    }

    // The guide's example, with the test's stand-ins for the mod's files and its overlay.
    private static int[] BuildIndex(int[] files, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return [.. files.Order()];
    }

    [Fact]
    public void A_task_on_a_joined_client_hands_its_result_to_the_main_thread_with_Client_Post()
    {
        var mod = NewMod("map");
        int[] files = [3, 1, 2];
        int[]? shown = null;
        var shownOnMain = false;
        ModContext? shownFor = null;
        KilnUnavailableException? hop = null;
        var task = mod.Tasks.Run("map-index", ctx =>
        {
            try
            {
                ctx.PostToTick(() => { });
            }
            catch (KilnUnavailableException e)
            {
                hop = e;  // no server world to hop to
            }
            var index = BuildIndex(files, ctx.Cancellation);  // off every game thread
            Client.Post(() =>
            {
                shown = index;  // the main thread, before the client world's next step
                shownOnMain = Client.IsClientThread;
                shownFor = ModContext.CurrentMod;
            });
        });
        Ended(task);
        Assert.Equal(KilnTaskState.Completed, task.State);  // it ended before its post ran: Client.Post is not tracked
        Assert.Null(shown);
        Assert.Equal("tasks.pool", hop?.Capability);
        FakeKilnClient.Step();
        Assert.Equal(new[] { 1, 2, 3 }, shown);
        Assert.True(shownOnMain);
        Assert.Same(mod, shownFor);
    }

    [Fact]
    public void Client_Post_from_a_task_is_not_cancelled_with_it_but_is_dropped_once_the_mod_unloaded()
    {
        var mod = NewMod("map");
        var ran = new List<string>();
        using var posted = new ManualResetEventSlim();
        using var go = new ManualResetEventSlim();
        var task = mod.Tasks.Run("posts", ctx =>
        {
            Client.Post(() => ran.Add("handed over before the cancel"));
            posted.Set();
            go.Wait(Long);
        });
        Assert.True(posted.Wait(Long));
        task.Cancel();
        go.Set();
        Ended(task);
        FakeKilnClient.Step();
        Assert.Equal(["handed over before the cancel"], ran);

        var gone = NewMod("gone");
        Ended(gone.Tasks.Run("posts", ctx => Client.Post(() => ran.Add("of a mod that unloaded"))));
        gone.Retire();
        FakeKilnClient.Step();
        Assert.Single(ran);
    }

    [Fact]
    public void A_Client_Post_that_throws_is_the_mods_fault_not_the_tasks()
    {
        var mod = NewMod("map");
        var task = mod.Tasks.Run("posts", ctx => Client.Post(() => throw new FormatException("overlay")));
        Ended(task);
        FakeKilnClient.Step();
        Assert.Equal(KilnTaskState.Completed, task.State);
        Assert.True(Backend.Logged("Client.Post work threw"));
        Assert.False(Backend.Logged("task 'posts' faulted"));
    }

    [Fact]
    public void Awaiting_Client_NextTick_in_a_task_leaves_the_task_and_an_unload_strands_it()
    {
        var mod = NewMod("map");
        TaskRun? before = null, after = null;
        var afterOnMain = false;
        var leaves = mod.Tasks.Run("leaves", async ctx =>
        {
            before = TaskExecutionScope.Current;
            await Client.NextTick();
            after = TaskExecutionScope.Current;
            afterOnMain = Client.IsClientThread;
        });
        Pool.WaitIdle();
        FakeKilnClient.Step();
        Ended(leaves);
        Assert.NotNull(before);
        Assert.Null(after);  // the rest of the method ran on the main thread, outside the task
        Assert.True(afterOnMain);

        var stranded = mod.Tasks.Run("stranded", async ctx => await Client.NextTick());
        Pool.WaitIdle();
        mod.Retire();  // the mod unloads before the client world's next step: its post is dropped
        FakeKilnClient.Step();
        Assert.False(stranded.WaitEnded(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(KilnTaskState.Running, stranded.State);
    }
}

/// <summary>A kiln.client table for <see cref="Client"/> (Kiln.Client), for the whole test process: Kiln.Client keeps
/// the table it found and subscribes to its Pre once. While no test uses it (<see cref="Active"/> false) it answers as
/// no client does: the zero state a server reads.</summary>
internal static unsafe class FakeKilnClient
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<uint*, int> State;
        public delegate* unmanaged[Cdecl]<ulong, int, delegate* unmanaged[Cdecl]<void*, void*, void>, void*, ulong*, int> SubscribeTick;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public void* Post;
        public delegate* unmanaged[Cdecl]<byte> OnClientThread;
        public void* ClientWorld;
    }

    private static readonly Table* s_table = Create();
    private static readonly void* s_tick = NativeMemory.AllocZeroed(64);  // the client world's KilnTickInfo for Pre
    private static delegate* unmanaged[Cdecl]<void*, void*, void> s_pre;
    private static Thread? s_main;

    /// <summary>A joined client in game (state Remote) while true.</summary>
    internal static volatile bool Active;

    private static Table* Create()
    {
        var t = (Table*)NativeMemory.AllocZeroed((nuint)sizeof(Table));  // never freed: Kiln.Client keeps it
        t->Size = (uint)sizeof(Table);
        t->Version = 1;
        t->State = &State;
        t->SubscribeTick = &Subscribe;
        t->Unsubscribe = &Unsubscribe;
        t->OnClientThread = &OnClientThread;
        return t;
    }

    /// <summary>Serves the table to <paramref name="backend"/> and makes Kiln.Client use it, whatever it found before.</summary>
    internal static void Install(Kiln.TestKit.RecordingBackend backend)
    {
        backend.Tables["kiln.client"] = (nint)s_table;
        var table = Field("_table");
        table.SetValue(null, Pointer.Box(s_table, table.FieldType));
        Field("_subscribed").SetValue(null, 0);
        Active = true;

        static FieldInfo Field(string name) =>
            typeof(Client).GetField(name, BindingFlags.NonPublic | BindingFlags.Static) ?? throw new MissingFieldException(nameof(Client), name);
    }

    /// <summary>One client-world step: Kiln.Client's Pre on the game's main thread (a thread of this fake's own).</summary>
    internal static void Step()
    {
        Assert.True(s_pre != null, "Kiln.Client never subscribed to the client world's Pre");
        var main = new Thread(() => s_pre(s_tick, null)) { Name = "fake-client-main" };
        s_main = main;
        main.Start();
        Assert.True(main.Join(TimeSpan.FromSeconds(20)));
        s_main = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int State(uint* s)
    {
        // NativeState: Size, Side, Session, Generation, LocalPlayer, Flags, ClientSession, ServerSession, then the steps
        if (!Active) return 0;
        s[1] = 1;  // the game client
        s[2] = 3;  // Remote: joined to someone else's world
        s[6] = 1;  // one client world so far
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Subscribe(ulong owner, int phase, delegate* unmanaged[Cdecl]<void*, void*, void> fn, void* user, ulong* handle)
    {
        s_pre = fn;
        *handle = 1;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unsubscribe(ulong handle) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnClientThread() => (byte)(Thread.CurrentThread == s_main ? 1 : 0);
}
