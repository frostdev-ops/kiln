// SPDX-License-Identifier: MIT
// Run the production managed request/result pump against a synthetic ABI table with serialized slow completions.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace Kiln.QuestTests;

[Collection("Flame service")]
public sealed unsafe class FlameQueueTests
{
    private static ulong _next;
    private static ulong _finished;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(KnowledgeNative.FlameState* state) { state->Cap = 20; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Submit(ulong owner, uint level, ulong* request) { *request = ++_next; return 0; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Result(ulong request, KnowledgeNative.FlameOutcome* result)
    {
        if (request > _finished) return KnowledgeNative.NotFound;
        *result = new() { Size = (uint)sizeof(KnowledgeNative.FlameOutcome), Request = request, Status = 0, Before = 3, After = 4, Cap = 20, Marked = 1, Mirrored = 1 };
        return 0;
    }

    [Fact]
    public void Eight_serialized_mirror_waits_do_not_report_lost_before_the_native_write_runs()
    {
        const BindingFlags hidden = BindingFlags.NonPublic | BindingFlags.Static;
        var nativeField = typeof(KnowledgeNative).GetField("_table", hidden)!;
        var saved = nativeField.GetValue(null);
        var table = (KnowledgeNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(KnowledgeNative.Table));
        table->FlameStatus = &Status;
        table->FlameSet = &Submit;
        table->FlameResult = &Result;
        nativeField.SetValue(null, Pointer.Box(table, typeof(KnowledgeNative.Table*)));
        _next = _finished = 0;
        var pump = typeof(WorldProgress).GetMethod("FlamePump", hidden)!;
        var waiting = typeof(WorldProgress).GetField("FlameWaiting", hidden)!.GetValue(null)!;
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => WorldProgress.SetFlameLevel(4)).ToArray();
            // Each earlier request can spend 181 updates waiting for the mirror, plus the next request's apply update.
            // With one active and eight queued, the service's worst non-stalled batch is 9 * 182 = 1638 updates.
            for (ulong step = 1; step <= 9 * 182; step++)
            {
                _finished = step / 182;
                var tick = (TickInfo)Activator.CreateInstance(typeof(TickInfo), BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [step, 0u, 0u, 0L, 0.0], null)!;
                pump.Invoke(null, [tick]);
                for (var i = 0; i < tasks.Length; i++)
                    Assert.Equal((ulong)(i + 1) <= _finished, tasks[i].IsCompleted);
            }
            Assert.All(tasks, task => { Assert.True(task.Result.Ok); Assert.True(task.Result.Mirrored); });
        }
        finally
        {
            waiting.GetType().GetMethod("Clear")!.Invoke(waiting, null);
            nativeField.SetValue(null, saved);
            NativeMemory.Free(table);
        }
    }
}

[CollectionDefinition("Flame service", DisableParallelization = true)]
public sealed class FlameServiceCollection { }
