// SPDX-License-Identifier: MIT
// What a button or a key does, and how a toggle or a slider writes a config. A button is clicked on the render thread and a key pressed on the window thread; neither may touch
// the game's world or write a config file, so what needs the tick thread is queued for UiRuntime.Pump (the service tick: the world's step in a world, the UI frame in a menu).
//   command       Commands.Execute on the tick thread, as the console folder runs a line (the host's own character on a hosting game, else the console)
//   toggle, set   an edit: shown at once, written to the config on the tick thread (a slider waits until it rests)
//   castAbility   the request a key of an ability makes: a message to the server, from any thread
//   open, close   the window's surface, from any thread
//   toast         Hud.Toast, from any thread
using System.Collections.Concurrent;

namespace Kiln;

internal static class UiActions
{
    private const int MaxJobs = 64;
    private sealed record Job(Action Run, UiLifetime? Lifetime);
    private static readonly object Gate = new();
    private static readonly Queue<Job> Jobs = new();

    /// <summary>Runs an action. Never throws: what goes wrong is a toast and a log line.</summary>
    public static void Run(ActionPlan plan, string mod, string source, UiLifetime? lifetime = null)
    {
        if (lifetime != null) lifetime.Run(() => RunCore(plan, mod, source, lifetime));
        else RunCore(plan, mod, source, null);
    }

    private static void RunCore(ActionPlan plan, string mod, string source, UiLifetime? lifetime)
    {
        try
        {
            switch (plan)
            {
                case CommandPlan c:
                    Enqueue(() => RunCommand(c.Line, mod), lifetime);
                    break;
                case TogglePlan t:
                    // from what is waiting to be written, if anything: two quick clicks flip twice
                    var current = UiEdits.Pending(t.Config) ?? UiBindings.Read(t.Config);
                    UiEdits.Set(t.Config, current is not true, immediate: true, lifetime);
                    break;
                case SetPlan s:
                    UiEdits.Set(s.Config, s.Value, immediate: true, lifetime);
                    break;
                case CastPlan c:
                    UiRuntime.Backend.RequestCast(c.AbilityId);
                    break;
                case WindowActionPlan w:
                    if (UiRuntime.FindWindow(w.Mod, w.Window) is { } window)
                    {
                        if (w.Open) window.Open();
                        else window.Close();
                    }
                    else UiRuntime.Backend.Toast($"{w.Mod}:{w.Window} is not a window this game has", ToastKind.Warning);
                    break;
                case ToastPlan t:
                    UiRuntime.Backend.Toast(t.Text.For(UiRuntime.Backend.Language), ToastKind.Info);
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: {mod}: ui: {source}: the action failed: {e.Message}");
            UiRuntime.Backend.Toast($"{mod}: {e.Message}", ToastKind.Error);
        }
    }

    private static void Enqueue(Action job, UiLifetime? lifetime)
    {
        lock (Gate)
        {
            if (Jobs.Count < MaxJobs)
            {
                Jobs.Enqueue(new Job(job, lifetime));
                return;
            }
        }
        UiRuntime.Backend.Toast("too many commands waiting: slow down", ToastKind.Warning);
    }

    /// <summary>Runs what the render and window threads queued. Tick thread.</summary>
    public static void Drain()
    {
        while (true)
        {
            Job job;
            lock (Gate) if (!Jobs.TryDequeue(out job!)) return;
            try
            {
                if (job.Lifetime != null) job.Lifetime.Run(job.Run);
                else job.Run();
            }
            catch (Exception e) when (e is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Warn, $"blocks: ui: {e.Message}"); }
        }
    }

    private static void RunCommand(string line, string mod)
    {
        var (ok, message) = UiRuntime.Backend.RunCommand(line);
        BlocksBridge.Log(ok ? LogLevel.Info : LogLevel.Warn, $"blocks: {mod}: ui: command \"{line}\": {(ok ? "ok" : message)}");
        if (message.Length > 0 || !ok) UiRuntime.Backend.Toast(message.Length > 0 ? message : $"{line} failed", ok ? ToastKind.Info : ToastKind.Error);
    }

    internal static void Clear()
    {
        lock (Gate) Jobs.Clear();
    }

    internal static void Forget(UiLifetime lifetime)
    {
        lock (Gate)
        {
            var keep = Jobs.Where(j => j.Lifetime != lifetime).ToArray();
            Jobs.Clear();
            foreach (var job in keep) Jobs.Enqueue(job);
        }
    }
}

/// <summary>What a toggle, a slider or a button changed in a config and has not been written yet. The window shows it at once; the tick thread writes it: a toggle's change
/// now, a slider's once it has rested for a moment (a drag is a change every frame, and a write is a file).</summary>
internal static class UiEdits
{
    internal const long RestMs = 400;

    private sealed record Edit(UiBinding Binding, object Value, long At, bool Immediate, long Version, UiLifetime? Lifetime);

    private static readonly ConcurrentDictionary<string, Edit> Waiting = new(StringComparer.Ordinal);
    private static long _version;

    public static void Set(UiBinding b, object value, bool immediate, UiLifetime? lifetime = null) =>
        Waiting[b.Name] = new Edit(b, value, UiRuntime.Clock(), immediate, Interlocked.Increment(ref _version), lifetime);

    /// <summary>The value an edit is holding for this entry, or null.</summary>
    public static object? Pending(UiBinding b) => b.IsConfig && Waiting.TryGetValue(b.Name, out var e) ? e.Value : null;

    public static bool Any => !Waiting.IsEmpty;

    /// <summary>Whether this game cannot change the entry now: a server config while joined to a server, or the server's own value.</summary>
    public static bool ReadOnly(UiBinding b)
    {
        if (!b.IsConfig || UiConfigs.Find(b.ConfigId!) is not { } cfg) return true;
        return cfg.IsFromServer(b.Path!) || (cfg.ServerKind && UiRuntime.Backend.JoinedServer);
    }

    /// <summary>Writes the edits that are due. Tick thread.</summary>
    public static void Flush(long now)
    {
        if (Waiting.IsEmpty) return;
        foreach (var e in Waiting.Values)
        {
            if (!e.Immediate && now - e.At < RestMs) continue;
            try
            {
                if (e.Lifetime != null) e.Lifetime.Run(() => Write(e));
                else Write(e);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                BlocksBridge.Log(LogLevel.Warn, $"blocks: ui: config {e.Binding.Name}: {ex.Message}");
                UiRuntime.Backend.Toast($"{e.Binding.Name}: {ex.Message}", ToastKind.Error);
            }
            finally
            {
                // after the write, so a refresh sees the pending value; a newer edit stays
                Waiting.TryRemove(new KeyValuePair<string, Edit>(e.Binding.Name, e));
            }
        }
    }

    private static void Write(Edit e)
    {
        var b = e.Binding;
        if (UiConfigs.Find(b.ConfigId!) is not { } cfg) return;
        if (cfg.IsFromServer(b.Path!) || (cfg.ServerKind && UiRuntime.Backend.JoinedServer))
        {
            UiRuntime.Backend.Toast($"{cfg.Id}: {b.Path} is the server's: it can be changed after leaving", ToastKind.Warning);
            return;
        }
        object value = e.Value;
        if (b.EntryType == "int" && e.Value is double d)
        {
            // a whole number, as the entry's own type: an int where it fits
            var whole = Math.Round(d);
            value = whole is >= int.MinValue and <= int.MaxValue ? (object)(int)whole : (long)whole;
        }
        if (!cfg.TrySet(b.Path!, value, out var error))
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: ui: config {cfg.Id}: {b.Path}: {error}");
            UiRuntime.Backend.Toast($"{cfg.Id}: {error}", ToastKind.Error);
        }
    }

    internal static void Clear() => Waiting.Clear();

    internal static void Forget(UiLifetime lifetime)
    {
        foreach (var e in Waiting.Values)
            if (e.Lifetime == lifetime) Waiting.TryRemove(new KeyValuePair<string, Edit>(e.Binding.Name, e));
    }
}
