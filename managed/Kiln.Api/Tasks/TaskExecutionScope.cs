// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// "This thread is running task X of owner Y right now": a thread-static scope Kiln installs around every real piece of a
/// task (a segment on a pool worker, or a tick hop on the tick) and restores in a <c>finally</c>. It is the evidence
/// Kiln's own checks use; work that left the task (raw <c>Task.Run</c>, <c>new Thread</c>, <c>ConfigureAwait(false)</c>)
/// has none. Inside a segment <see cref="ModContext.CurrentMod"/> is the task's mod and the synchronization context is
/// the task's own; outside one, both keep their tick meaning.
/// </summary>
/// <remarks>The task's identity also flows in an <see cref="AsyncLocal{T}"/> (<see cref="FlowedIdentity"/>) for
/// diagnostics only. It never grants anything: code that escaped the task still carries it.</remarks>
internal static class TaskExecutionScope
{
    [ThreadStatic] private static TaskRun? t_run;
    [ThreadStatic] private static bool t_onTick;
    [ThreadStatic] private static WorldKind t_world;

    private static readonly AsyncLocal<string?> Identity = new();

    /// <summary>The task this thread is running a piece of; null outside one.</summary>
    internal static TaskRun? Current => t_run;

    /// <summary>True while that piece is a tick hop (on a world's step), false on a pool worker.</summary>
    internal static bool OnTick => t_run is not null && t_onTick;

    /// <summary>The world whose step runs that tick hop (meaningful only while <see cref="OnTick"/>).</summary>
    internal static WorldKind TickWorld => t_world;

    /// <summary>"owner/task" of the task this code descends from, for diagnostics; null outside tasks. Not authority.</summary>
    internal static string? FlowedIdentity => Identity.Value;

    internal static void SetFlowedIdentity(string? identity) => Identity.Value = identity;

    /// <summary>Installs the scope of <paramref name="run"/>; dispose the frame (in a <c>finally</c>) to restore what was
    /// there before: the scope, <see cref="ModContext.CurrentMod"/> and the synchronization context.</summary>
    internal static Frame Enter(TaskRun run, bool onTick, SynchronizationContext context, WorldKind world = WorldKind.Server)
    {
        var frame = new Frame(t_run, t_onTick, t_world, ModContext.CurrentMod, SynchronizationContext.Current);
        t_run = run;
        t_onTick = onTick;
        t_world = world;
        ModContext.CurrentMod = run.Mod;
        SynchronizationContext.SetSynchronizationContext(context);
        return frame;
    }

    internal readonly struct Frame(TaskRun? run, bool onTick, WorldKind world, ModContext? mod, SynchronizationContext? context)
        : IDisposable
    {
        public void Dispose()
        {
            t_run = run;
            t_onTick = onTick;
            t_world = world;
            ModContext.CurrentMod = mod;
            SynchronizationContext.SetSynchronizationContext(context);
        }
    }
}
