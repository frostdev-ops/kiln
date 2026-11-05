// SPDX-License-Identifier: MIT
// Kiln.Quests runs on the tick thread, and nothing else may touch its state. Service code runs without a synchronization
// context (ServiceTick.Run clears it), and tasks of the other services complete with RunContinuationsAsynchronously, so a plain
// await, or ConfigureAwait(false), continues on the thread pool. There every later step fails in Game.AssertTickThread, half
// way through: a quest completed by a native dialogue option paid its rewards and never mirrored its state. So every await in this service is `await task.OnTick()`: it comes back on the tick thread, always,
// whoever called. Drained at the start of the service tick.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Kiln;

internal static class QuestTick
{
    private static readonly ConcurrentQueue<Action> Queue = new(), Later = new();

    /// <summary>Runs an action at the next service tick. Any thread.</summary>
    public static void Post(Action action) => Queue.Enqueue(action);

    /// <summary><c>await QuestTick.NextStep()</c>: continues on the tick thread at the next service tick.</summary>
    public static NextStepAwaitable NextStep() => default;

    /// <summary>From the service tick. An await chain finishes in one step (an outer await is queued while the inner one runs).</summary>
    public static void Drain()
    {
        for (var m = Later.Count; m > 0 && Later.TryDequeue(out var l); m--) Queue.Enqueue(l);
        for (var n = 0; n < 4096 && Queue.TryDequeue(out var a); n++)
        {
            try
            {
                a();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                QuestsInternals.Error($"a continuation on the tick thread threw: {e}");
            }
        }
    }

    /// <summary><c>await t.OnTick()</c>: continues on the tick thread.</summary>
    public static Awaitable OnTick(this Task task) => new(task);

    /// <summary><c>await t.OnTick()</c>: continues on the tick thread.</summary>
    public static Awaitable<T> OnTick<T>(this Task<T> task) => new(task);

    public readonly struct NextStepAwaitable
    {
        public NextStepAwaiter GetAwaiter() => default;
    }

    public readonly struct NextStepAwaiter : ICriticalNotifyCompletion
    {
        public bool IsCompleted => false;
        public void OnCompleted(Action continuation) => Later.Enqueue(continuation);
        public void UnsafeOnCompleted(Action continuation) => Later.Enqueue(continuation);
        public void GetResult() { }
    }

    public readonly struct Awaitable(Task task)
    {
        public Awaiter GetAwaiter() => new(task);
    }

    public readonly struct Awaitable<T>(Task<T> task)
    {
        public Awaiter<T> GetAwaiter() => new(task);
    }

    public readonly struct Awaiter(Task task) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => task.IsCompleted && Game.IsTickThread;
        public void OnCompleted(Action continuation) => task.ConfigureAwait(false).GetAwaiter().OnCompleted(() => Queue.Enqueue(continuation));
        public void UnsafeOnCompleted(Action continuation) => task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Queue.Enqueue(continuation));
        public void GetResult() => task.GetAwaiter().GetResult();
    }

    public readonly struct Awaiter<T>(Task<T> task) : ICriticalNotifyCompletion
    {
        public bool IsCompleted => task.IsCompleted && Game.IsTickThread;
        public void OnCompleted(Action continuation) => task.ConfigureAwait(false).GetAwaiter().OnCompleted(() => Queue.Enqueue(continuation));
        public void UnsafeOnCompleted(Action continuation) => task.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(() => Queue.Enqueue(continuation));
        public T GetResult() => task.GetAwaiter().GetResult();
    }
}
