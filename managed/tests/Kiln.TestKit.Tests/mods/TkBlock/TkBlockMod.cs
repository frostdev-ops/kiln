// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Kiln;
using Kiln.Testing;
using Assert = Kiln.Testing.Assert;

namespace TkBlock;

/// <summary>Tests that block the tick thread three ways (a Task.Wait that needs the tick thread, a lock another thread
/// holds, an endless loop), each followed by one that passes; and tests that are slow but never block for long.
/// The mod also registers a command and an event handler, to show they are gone after a stopped harness.</summary>
public sealed class TkBlockMod : Mod
{
    private static volatile int _spin;
    private readonly object _gate = new();

    public override void OnLoad()
    {
        Commands.Register("tk-block", ctx => ctx.Reply("tk-block is here"));
        Events.On<ServerStartedEvent>(_ => { });
    }

    [KilnTest(Order = 1, TimeoutSeconds = 1)]
    public async Task Blocks_in_task_wait(TestContext t)
    {
        await t.Ticks(1);
        // the continuation needs the tick thread, which this wait holds: a deadlock, as in a server
        var next = Task.Delay(10).ContinueWith(_ => { }, TaskScheduler.FromCurrentSynchronizationContext());
        WaitFor(next);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WaitFor(Task task) => task.Wait();

    [KilnTest(Order = 2)]
    public void Passes_after_the_wait() => Assert.True(Game.IsTickThread);

    [KilnTest(Order = 3, TimeoutSeconds = 1)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization | System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // the test asserts this frame; a Release build would make its last call a tail call
    public void Blocks_on_a_lock()
    {
        using var held = new ManualResetEventSlim(false);
        var holder = new Thread(() =>
        {
            lock (_gate)
            {
                held.Set();
                Thread.Sleep(TimeSpan.FromSeconds(8));  // ends by itself: the test process keeps no thread of it
            }
        }) { IsBackground = true, Name = "tk-block lock holder" };
        holder.Start();
        held.Wait();
        lock (_gate) { }
    }

    [KilnTest(Order = 4)]
    public void Passes_after_the_lock() => Assert.True(Game.IsTickThread);

    [KilnTest(Order = 5, TimeoutSeconds = 1)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization | System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // the test asserts this frame; a Release build would make its last call a tail call
    public void Spins_forever()
    {
        while (true) _spin++;
    }

    [KilnTest(Order = 6)]
    public void Passes_after_the_loop() => Assert.True(Game.IsTickThread);

    // Slow but alive: longer than the watchdog's grace, every step short.
    [KilnTest(Order = 7, TimeoutSeconds = 5)]
    public async Task Awaits_ticks_for_a_while(TestContext t)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(1.5)) await t.Ticks();
    }

    // One step that takes 0.8 s: under its timeout and the grace.
    [KilnTest(Order = 8, TimeoutSeconds = 1)]
    public void Blocks_briefly() => Thread.Sleep(800);

    [KilnTest(Order = 9, TimeoutSeconds = 1)]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoOptimization | System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // the test asserts this frame; a Release build would make its last call a tail call
    public void Sleeps_forever() => Thread.Sleep(Timeout.Infinite);
}
