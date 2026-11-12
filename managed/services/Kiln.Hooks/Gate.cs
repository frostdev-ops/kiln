// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>The capability gate for kiln.hooks: only a mod whose <c>kiln.mod.json</c> has <c>"advanced": true</c>
/// (<see cref="ModInfo.Advanced"/>, filled by the loader from the manifest) may hook, patch or read game memory. Also
/// tracks each advanced mod so everything it installs is revoked when it unloads.</summary>
internal static class HooksGate
{
    private static readonly ConcurrentDictionary<ModContext, ModHooks> Tracked = new();

    internal sealed class ModHooks
    {
        public bool SymbolsLoaded;
        // Threads currently inside one of this mod's handlers (reverse P/Invoke from game threads). Drained before
        // the mod's collectible load context is torn down, so the runtime never has to suspend a game thread that is
        // executing code in the context being unloaded.
        public readonly StrongBox<long> InFlight = new(0);
    }

    /// <summary>Returns the calling mod if it is advanced; throws otherwise.</summary>
    public static ModContext RequireAdvanced(string api)
        => RequireAdvanced(api, "Hooking and patching game code is powerful and build-specific");

    /// <summary>Returns the calling mod if it is advanced; throws otherwise. <paramref name="why"/> is the middle
    /// sentence of the refusal.</summary>
    public static ModContext RequireAdvanced(string api, string why)
    {
        var mod = ModContext.CurrentMod
            ?? throw new InvalidOperationException($"{api} must be called from a mod (on the tick thread or in OnLoad)");
        if (!mod.Info.Advanced)
            throw new InvalidOperationException(
                $"{api}: mod '{mod.Info.Id}' is not advanced. {why}; add \"advanced\": true to its kiln.mod.json " +
                "to allow it.");
        return mod;
    }

    /// <summary>Ensures the mod's hooks are torn down on unload; returns its tracker.</summary>
    public static ModHooks Track(ModContext mod)
        => Tracked.GetOrAdd(mod, m =>
        {
            m.Retiring += () => Retire(m);
            return new ModHooks();
        });

    private static void Retire(ModContext mod)
    {
        unsafe
        {
            var t = HooksNative.T;
            if (t != null) t->RevokeOwner(mod.Owner);  // native: restore all patches (and drop anything else under it)
        }
        // Clear the mod's registrations so no new dispatch enters its handlers, and revoke their links (they are under
        // the mod id's own hooks owner, HookCookies.Owner, so RevokeOwner above does not take them).
        Hooks.Forget(mod);
        Tracked.TryRemove(mod, out var mh);
        // Wait for any game thread still inside this mod's handlers to leave before its context is unloaded. New calls
        // can no longer reach them (chains emptied, dictionary cleared), so this drains in microseconds.
        if (mh is not null)
        {
            var sw = Stopwatch.StartNew();
            while (Interlocked.Read(ref mh.InFlight.Value) > 0 && sw.ElapsedMilliseconds < 1000)
                Thread.SpinWait(64);
        }
        if (mh is { SymbolsLoaded: true }) Symbols.UnloadShipped(mod);
    }

    internal static ModHooks Current(ModContext mod) => Tracked.TryGetValue(mod, out var mh) ? mh : Track(mod);
}
