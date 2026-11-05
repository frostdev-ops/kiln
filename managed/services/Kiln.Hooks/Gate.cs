// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Kiln;

/// <summary>The capability gate for kiln.hooks: only a mod whose <c>kiln.mod.json</c> has <c>"advanced": true</c>
/// may hook, patch or read game memory. (Read straight from the manifest in <see cref="ModInfo.Directory"/> until the
/// loader exposes the flag on <see cref="ModInfo"/>.) Also tracks each advanced mod so everything it installs is
/// revoked when it unloads.</summary>
internal static class HooksGate
{
    private static readonly ConcurrentDictionary<ModContext, bool> Advanced = new();
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
    {
        var mod = ModContext.CurrentMod
            ?? throw new InvalidOperationException($"{api} must be called from a mod (on the tick thread or in OnLoad)");
        if (!Advanced.GetOrAdd(mod, m => ReadAdvanced(m.Info.Directory)))
            throw new InvalidOperationException(
                $"{api}: mod '{mod.Info.Id}' is not advanced. Hooking and patching game code is powerful and " +
                "build-specific; add \"advanced\": true to its kiln.mod.json to allow it.");
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
            if (t != null) t->RevokeOwner(mod.Owner);  // native: drop all hooks (chains emptied), restore all patches
        }
        Hooks.Forget(mod);  // clear the cookie dictionary so no new dispatch enters this mod's handlers
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
        Advanced.TryRemove(mod, out _);
    }

    internal static ModHooks Current(ModContext mod) => Tracked.TryGetValue(mod, out var mh) ? mh : Track(mod);

    private static bool ReadAdvanced(string directory)
    {
        try
        {
            var file = Path.Combine(directory, "kiln.mod.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return doc.RootElement.TryGetProperty("advanced", out var v)
                && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }
}
