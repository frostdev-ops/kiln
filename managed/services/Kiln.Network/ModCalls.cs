// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Per-mod ownership of what Kiln.Network holds for mods (channels, handlers): runs a mod's delegate as that
/// mod and drops everything a mod registered when it unloads. (Same pattern as Kiln.Chat's.)</summary>
internal static class NetModCalls
{
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly List<Action<ModContext>> RetireHandlers = new();
    private static readonly object Lock = new();

    public static ModContext? Caller => ModContext.CurrentMod;

    public static void OnRetire(Action<ModContext> cleanup)
    {
        lock (Lock) RetireHandlers.Add(cleanup);
    }

    public static void Watch(ModContext? mod)
    {
        if (mod is null) return;
        lock (Lock)
        {
            if (!Watched.Add(mod)) return;
        }
        mod.Retiring += () =>
        {
            Action<ModContext>[] handlers;
            lock (Lock)
            {
                Watched.Remove(mod);
                handlers = RetireHandlers.ToArray();
            }
            foreach (var h in handlers)
            {
                try { h(mod); }
                catch (Exception e) { Backend.Current.Log(0, LogLevel.Error, $"kiln.network: cleanup for {mod.Info.Id} threw: {e}"); }
            }
        };
    }

    /// <summary>Runs <paramref name="action"/> as code of <paramref name="mod"/> (null = Kiln). Exceptions are logged
    /// and reported as false.</summary>
    public static bool Invoke(ModContext? mod, Action action, string what)
    {
        if (mod is not null && !mod.Alive) return false;
        var previousContext = SynchronizationContext.Current;
        var previousMod = ModContext.CurrentMod;
        if (mod is not null)
        {
            SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
            ModContext.CurrentMod = mod;
        }
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            if (mod is not null) mod.Log.Error(e, $"{what} threw");
            else Backend.Current.Log(0, LogLevel.Error, $"kiln.network: {what} threw: {e}");
            return false;
        }
        finally
        {
            if (mod is not null)
            {
                ModContext.CurrentMod = previousMod;
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }
}

/// <summary>Handlers owned by mods (removed when their mod unloads).</summary>
internal sealed class OwnedHandlers<T> where T : Delegate
{
    private readonly object _lock = new();
    private (T Handler, ModContext? Owner)[] _items = [];

    public OwnedHandlers() => NetModCalls.OnRetire(mod =>
    {
        lock (_lock) _items = _items.Where(i => !ReferenceEquals(i.Owner, mod)).ToArray();
    });

    public void Add(T? handler)
    {
        if (handler is null) return;
        var owner = NetModCalls.Caller;
        NetModCalls.Watch(owner);
        lock (_lock) _items = [.. _items, (handler, owner)];
    }

    public void Remove(T? handler)
    {
        if (handler is null) return;
        lock (_lock)
        {
            var i = Array.FindLastIndex(_items, x => x.Handler == handler);
            if (i >= 0) _items = _items.Where((_, k) => k != i).ToArray();
        }
    }

    public void Raise(Action<T> call, string what)
    {
        (T, ModContext?)[] items;
        lock (_lock) items = _items;
        foreach (var (h, owner) in items) NetModCalls.Invoke(owner, () => call(h), what);
    }
}
