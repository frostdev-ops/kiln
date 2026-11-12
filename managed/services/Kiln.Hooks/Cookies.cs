// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The user pointers ("cookies") managed hooks hand to the native registry. The registry gives a revoked link's
/// slot back to an install whose kind, function, user pointer, owner and label are equal
/// (native/core/src/services/hooks_chain.cpp, install() and the comment above it); the function is always the fixed
/// dispatcher of <see cref="Hooks"/> and the label the target's symbol (or owner name and RVA), so the cookie decides.
/// A fresh cookie per install took a new slot of the 65,536 every time. Here a cookie belongs to one (mod id, kind,
/// address) for the life of the process: removing an install returns it to that key's free list and the next install
/// on the key takes it again, also after the mod reloaded (its id is the same; see <see cref="Owner"/> for the owner).
/// Two installs on one key that are live at the same time get two cookies. Only strings and numbers are kept, so nothing
/// here holds an unloaded mod.</summary>
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

    // The registry also compares the owner, and the host gives every load of a mod a new one (Kiln.Host Mods.LoadCore:
    // OwnerCreate("mod:" + id)). Links go under an owner of Kiln.Hooks' own per mod id instead, with the same name, made
    // once and never revoked as a whole: so a reloaded mod's install matches its previous load's revoked link too. Its
    // links are revoked one by one when a context of the mod retires (Hooks.Forget); its patches stay under the mod's
    // own owner (Memory.Patch, RevokeOwner on retire).
    private static readonly Dictionary<string, ulong> Owners = new(StringComparer.Ordinal);

    /// <summary>Makes a native owner (the core's owner_create; 0 when there is no core). Tests replace it.</summary>
    internal static Func<string, ulong> CreateOwner = name => Backend.Current.OwnerCreate(name);

    /// <summary>The native owner the mod's links are installed under: one per mod id for the process, else (no core
    /// to make one) the mod's own.</summary>
    public static ulong Owner(ModContext mod)
    {
        ulong owner;
        lock (Gate)
            if (!Owners.TryGetValue(mod.Info.Id, out owner))
                Owners[mod.Info.Id] = owner = CreateOwner("mod:" + mod.Info.Id);
        return owner != 0 ? owner : mod.Owner;
    }

    /// <summary>Cookies ever made for a key, free or taken (tests).</summary>
    internal static int MadeCount(string modId, byte kind, nuint address)
    {
        var key = new Key(modId, kind, address);
        lock (Gate) return KeyOf.Values.Count(k => k == key);
    }

    /// <summary>Free cookies of a key (tests).</summary>
    internal static int FreeCount(string modId, byte kind, nuint address)
    {
        lock (Gate) return Free.TryGetValue(new Key(modId, kind, address), out var f) ? f.Count : 0;
    }
}
