// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// Runs a mod's heavy work off the game's tick, on Kiln's own worker threads: scans, path finding, planning, file and
/// network IO, big calculations. <see cref="ModContext.Tasks"/>; every task is the mod's own (its limits, its
/// cancellation, its unload).
/// </summary>
/// <remarks>
/// <para>The work runs on a Kiln worker, never on the tick. It may compute, do IO and call members marked
/// <see cref="AnyThreadAttribute"/>; tick-only APIs it reaches through <see cref="KilnTaskContext.ToTick()"/>,
/// <see cref="KilnTaskContext.OnTick{T}(Func{T})"/> or <see cref="KilnTaskContext.PostToTick(Action)"/>. The tick never waits for a
/// task.</para>
/// <para>A mod runs at most 2 tasks at once (plus 2 <see cref="KilnTaskOptions.LongRunning"/> ones) and holds at most 1024
/// that have not ended; its tasks queue in order (by <see cref="KilnTaskOptions.Priority"/>) and mods take turns. A task
/// suspended at an <c>await</c> holds no worker.</para>
/// <para>An exception that ends the work faults only that task: Kiln writes it to kiln.log with the mod and the task, and
/// it does not count toward the mod's handler faults.</para>
/// </remarks>
public sealed class KilnTasks
{
    private readonly ModContext _mod;

    internal KilnTasks(ModContext mod)
    {
        _mod = mod;
        // The mod's unload seals and signals its tasks from BeginUnload (Mod.cs), before any OnUnload: the tick
        // never waits for them, and a task that ignores its token is joined off the tick by the host.
        mod.UnloadTaskSeal = () => TaskRun.SealTasks(mod);
        mod.UnloadTaskBlocker = () => TaskRun.LiveTasks(mod);
    }

    /// <summary>Starts <paramref name="work"/> on a Kiln worker and returns at once. Any thread.</summary>
    /// <param name="name">What logs and diagnostics call the task: 1 to 128 UTF-8 bytes, no control characters.</param>
    /// <param name="work">The work; it gets the task's <see cref="KilnTaskContext"/>.</param>
    /// <param name="options">How to run it; null for the defaults.</param>
    /// <exception cref="ArgumentException">A bad name, null work or an unknown priority; nothing was started.</exception>
    /// <exception cref="KilnUnavailableException">Kiln's task pool is not available here (tasks.pool).</exception>
    /// <exception cref="KilnTaskQueueFullException">The mod already holds 1024 tasks that have not ended.</exception>
    /// <exception cref="InvalidOperationException">The mod is unloading.</exception>
    [AnyThread, RequiresCapability(TaskRun.Capability)]
    public KilnTask Run(string name, Action<KilnTaskContext> work, KilnTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TaskRun.Start(TaskRun.ForMod(_mod), name, work, options, new KilnTask(name));
    }

    /// <summary>Starts async <paramref name="work"/> on a Kiln worker and returns at once. Its awaits resume in the task,
    /// holding no worker while they wait. Any thread.</summary>
    /// <inheritdoc cref="Run(string, Action{KilnTaskContext}, KilnTaskOptions?)" path="/param"/>
    /// <inheritdoc cref="Run(string, Action{KilnTaskContext}, KilnTaskOptions?)" path="/exception"/>
    [AnyThread, RequiresCapability(TaskRun.Capability)]
    public KilnTask Run(string name, Func<KilnTaskContext, Task> work, KilnTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return TaskRun.Start(TaskRun.ForMod(_mod), name, work, options, new KilnTask(name));
    }

    /// <summary>Starts async <paramref name="work"/> with a result on a Kiln worker and returns at once: <c>T value = await
    /// Context.Tasks.Run("path", async ctx =&gt; ...);</c>. Any thread.</summary>
    /// <inheritdoc cref="Run(string, Action{KilnTaskContext}, KilnTaskOptions?)" path="/param"/>
    /// <inheritdoc cref="Run(string, Action{KilnTaskContext}, KilnTaskOptions?)" path="/exception"/>
    [AnyThread, RequiresCapability(TaskRun.Capability)]
    public KilnTask<T> Run<T>(string name, Func<KilnTaskContext, Task<T>> work, KilnTaskOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        return (KilnTask<T>)TaskRun.Start(TaskRun.ForMod(_mod), name, work, options, new KilnTask<T>(name));
    }
}
