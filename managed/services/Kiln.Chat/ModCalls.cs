// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>Per-mod ownership of callbacks held by Kiln.Chat: runs a mod's delegate in that mod's context (so
/// <c>await</c>, <c>Scheduler</c> and service ownership work as in its own handlers) and drops everything a mod
/// registered when it unloads.</summary>
internal static class ModCalls
{
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly List<Action<ModContext>> RetireHandlers = new();
    private static readonly object Lock = new();

    /// <summary>The mod whose code is calling right now (null outside mod code).</summary>
    public static ModContext? Caller => ModContext.CurrentMod;

    /// <summary>Registers a cleanup that runs for every mod that retires after <see cref="Watch"/>.</summary>
    public static void OnRetire(Action<ModContext> cleanup)
    {
        lock (Lock) RetireHandlers.Add(cleanup);
    }

    /// <summary>Makes sure <paramref name="mod"/>'s unload runs the cleanups.</summary>
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
                catch (Exception e) { Backend.Current.Log(0, LogLevel.Error, $"kiln.chat: cleanup for {mod.Info.Id} threw: {e}"); }
            }
        };
    }

    /// <summary>Runs <paramref name="action"/> as code of <paramref name="mod"/> (null = Kiln itself). Exceptions are
    /// logged to the mod's log and reported as false.</summary>
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
            else Backend.Current.Log(0, LogLevel.Error, $"kiln.chat: {what} threw: {e}");
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

    public static string OwnerName(ModContext? mod) => mod?.Info.Id ?? "kiln";
}
