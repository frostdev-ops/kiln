// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The user pointers ("cookies") managed hooks hand to the native registry. The registry gives a revoked link's
/// slot back to an install whose kind, function, user pointer, owner and label are equal
/// (native/core/src/services/hooks_chain.cpp, install() and the comment above it); the function is always the fixed
/// dispatcher of <see cref="Hooks"/> and the label the target's symbol (or owner name and RVA), so the cookie decides.
/// A fresh cookie per install took a new slot of the 65,536 every time. Here a cookie belongs to one (mod id, kind,
/// address) for the life of the process: removing an install returns it to that key's free list and the next install
/// on the key takes it again, also after the mod reloaded (its id is the same). Two installs on one key that are live
/// at the same time get two cookies. Only strings and numbers are kept, so nothing here holds an unloaded mod.</summary>
internal static class HookCookies
{
    public const byte Detour = 0, Mid = 1;

    private readonly record struct Key(string ModId, byte Kind, nuint Address);

    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Stack<long>> Free = new();
    private static readonly Dictionary<long, Key> KeyOf = new();
    private static long _next;

    /// <summary>A cookie for a new install on (mod, kind, address): a freed one of that key, else a new one.</summary>
    public static long Take(string modId, byte kind, nuint address)
    {
        var key = new Key(modId, kind, address);
        lock (Gate)
        {
            if (Free.TryGetValue(key, out var free) && free.Count > 0) return free.Pop();
            var cookie = ++_next;
            KeyOf[cookie] = key;
            return cookie;
        }
    }

    /// <summary>Frees <paramref name="cookie"/> for its key. Call only once no live native link carries it.</summary>
    public static void Return(long cookie)
    {
        lock (Gate)
        {
            if (!KeyOf.TryGetValue(cookie, out var key)) return;
            if (!Free.TryGetValue(key, out var free)) Free[key] = free = new Stack<long>();
            if (!free.Contains(cookie)) free.Push(cookie);  // a double return must never hand one cookie out twice
        }
    }

    /// <summary>The native owner the mod's links are installed under.</summary>
    public static ulong Owner(ModContext mod) => mod.Owner;

    /// <summary>Free cookies of a key (tests).</summary>
    internal static int FreeCount(string modId, byte kind, nuint address)
    {
        lock (Gate) return Free.TryGetValue(new Key(modId, kind, address), out var f) ? f.Count : 0;
    }
}
