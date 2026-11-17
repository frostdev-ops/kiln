// SPDX-License-Identifier: MIT
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

/// <summary>A host whose tick thread is the fake pool's tick thread (FakeHost says "always").</summary>
internal sealed class TaskTestHost : IHostServices, IKilnBackend
{
    public FakeNativeTasks? Native;
    public string Version => "0.1.0-dev";
    public bool IsTickThread => Native?.IsTickThread ?? false;
    public bool HasFeature(string name) => true;
    public void Log(ulong owner, LogLevel level, string text) => Log(level, text);
    public nint Service(string name, uint minVersion) => Native?.Service(name, minVersion) ?? 0;
    public readonly List<string> Lines = [];
    public readonly List<ulong> Revoked = [];
    private ulong _next = 1;
    public ulong OwnerCreate(string id) => Interlocked.Increment(ref _next);

    /// <summary>As Kiln.Host's own: the owner's tasks are told first, then the core retires it.</summary>
    public void OwnerRevoke(ulong owner)
    {
        lock (Revoked) Revoked.Add(owner);
        HostTasks.Bound?.RetireOwner(owner);
        Native?.RetireOwner(owner);
    }

    public void Log(LogLevel level, string text)
    {
        lock (Lines) Lines.Add($"{level}: {text}");
    }

    public bool Has(string fragment) { lock (Lines) return Lines.Exists(l => l.Contains(fragment, StringComparison.Ordinal)); }
    public int Count(string fragment) { lock (Lines) return Lines.Count(l => l.Contains(fragment, StringComparison.Ordinal)); }
    public string Dump() { lock (Lines) return string.Join("\n", Lines); }
}

/// <summary>The real backend (<see cref="HostTasks"/>) bound to a fake of the core's task tables, with a live server
/// session. Every test must leave no pin, no misuse of the table and no leaked scope behind.</summary>
public abstract class HostTaskTest : ModsFixture
{
    internal static readonly TimeSpan Long = TimeSpan.FromSeconds(20);
    internal FakeNativeTasks Native { get; private set; } = null!;
    internal TaskTestHost Host { get; } = new();
    internal HostTasks Tasks { get; private set; } = null!;
    internal readonly List<(ulong Owner, bool OnTick)> Hops = [];
    private readonly List<FakeNativeTasks> _old = [];
    private readonly List<ModContext> _mods = [];

    /// <summary>What a tick hop runs in; null (the default) records it and runs it bare.</summary>
    internal Action<ulong, Action>? HopFrame;

    protected HostTaskTest()
    {
        Kiln.Backend.Current = Host;
        Use(2);
    }

    /// <summary>A fresh fake core (prepared by <paramref name="prepare"/> before the host looks at it) and a fresh
    /// backend on it.</summary>
    internal void Use(int workers, Action<FakeNativeTasks>? prepare = null, string? ownStamp = FakeNativeTasks.Stamp,
        string coreStamp = FakeNativeTasks.Stamp, bool session = true, bool offer = true)
    {
        if (Native is not null) _old.Add(Native);
        Native = new FakeNativeTasks(workers, coreStamp);
        prepare?.Invoke(Native);
        if (session) Native.StartSession();
        Host.Native = Native;
        var native = Native;
        Tasks = HostTasks.Start(new HostTasksEnvironment
        {
            Service = (name, min) => offer ? native.Service(name, min) : 0,
            OnServerTick = () => native.IsTickThread,
            Log = Host.Log,
            BuildStamp = ownStamp,
            TickHop = (owner, hop) =>
            {
                lock (Hops) Hops.Add((owner, native.IsTickThread));
                if (HopFrame is { } frame) frame(owner, hop);
                else hop();
            },
        });
    }

    internal ModContext NewMod(string id)
    {
        var mod = HostAccess.CreateContext(new ModInfo(id, id, "1.0.0", Path.Combine(ModsDir, id)), Host.OwnerCreate(id), Path.Combine(DataDir, id));
        _mods.Add(mod);
        return mod;
    }

    /// <summary>Waits until <paramref name="task"/> has ended (this is the test's thread, never a game thread).</summary>
    internal static void Ended(KilnTask task) =>
        Assert.True(task.WaitEnded(Long), $"the task '{task.Name}' did not end ({task.State})");

    /// <summary>Waits for the core's end notification of every lease, then for the pins to be gone.</summary>
    internal void Quiesce()
    {
        Native.WaitIdle();
        Assert.True(SpinWait.SpinUntil(() => Tasks.LivePins == 0 && Native.Records == 0, Long),
            $"{Tasks.LivePins} pin(s) and {Native.Records} record(s) left");
    }

    /// <summary><see cref="Quiesce"/> after a mod's unload: a tick hop of its tasks still pending at the core (a
    /// sample's scan that reached <c>ToTick</c> after the test's last step) is only flushed, cancelled, by a later step,
    /// so the tick steps until every pin is gone.</summary>
    internal void QuiesceAfterUnload()
    {
        Native.WaitIdle();
        Assert.True(Until(() =>
        {
            Native.Step();
            return Tasks.LivePins == 0 && Native.Records == 0;
        }), $"{Tasks.LivePins} pin(s), {Native.Records} record(s) and {Native.PendingHops} hop(s) left");
        Quiesce();
    }

    internal static bool Until(Func<bool> done) => SpinWait.SpinUntil(done, Long);

    public override void Dispose()
    {
        try
        {
            TaskRun.TestHook = null;
            TaskBackend.Install(null);
            ComponentTask.Owners = null;
            foreach (var mod in _mods) mod.Retire();
            Native.Dispose();
            foreach (var old in _old) old.Dispose();
            Assert.Empty(Native.Errors);  // the host never misuses the table
            Assert.Empty(Native.Leaks);   // every dispatch restored its thread's scope, mod and context
        }
        finally
        {
            base.Dispose();
        }
    }
}
