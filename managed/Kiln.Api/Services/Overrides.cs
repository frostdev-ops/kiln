// SPDX-License-Identifier: MIT
// Reversible overrides with claims: a change a mod makes to something shared (a field of a game resource, or anything it
// can undo with a delegate) is a claim. Claims are per mod, a thing two mods want gets a clear logged answer, and
// every claim of a mod is given back when it unloads or asks. The model is ShroudForge's `runtime.overrides` (claims,
// apply, restore all), written for Kiln's own services. Guide: docs/kiln/modding/typed-fields.md ("Any resource").
using System.Runtime.CompilerServices;

namespace Kiln;

/// <summary>What happened to a claim.</summary>
public enum OverrideStatus
{
    /// <summary>The change is in effect (a claim you already held was updated).</summary>
    Applied,
    /// <summary>Another mod holds the thing: nothing changed.</summary>
    Conflict,
    /// <summary>There is no such resource: nothing changed.</summary>
    NotFound,
    /// <summary>The game side is not there (an unknown game build, a client without Kiln) or refused the change: nothing changed.</summary>
    Unavailable,
}

/// <summary>The answer to a claim. <see cref="Message"/> says it in a sentence and is also in the log.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Message">A sentence for the log or the player.</param>
/// <param name="HeldBy">For <see cref="OverrideStatus.Conflict"/>: the id of the mod that holds the thing.</param>
public sealed record OverrideResult(OverrideStatus Status, string Message, string? HeldBy = null)
{
    public bool Ok => Status == OverrideStatus.Applied;
    public override string ToString() => $"{Status}: {Message}";
}

/// <summary>One claim in force (<see cref="Overrides.Active"/>).</summary>
/// <param name="Mod">The mod that holds it ("kiln" for Kiln's own tools).</param>
/// <param name="Target">What it holds: the name given to <see cref="Overrides.Claim"/>, or a resource.</param>
/// <param name="Detail">For a resource field: its path and value. Empty for a claim made with <see cref="Overrides.Claim"/>.</param>
public sealed record OverrideInfo(string Mod, string Target, string Detail);

/// <summary>
/// Changes a mod makes to shared things, each with a way back. Two kinds of claim:
/// <list type="bullet">
/// <item>a field of a game resource: <see cref="ReflectedResource.Set"/> (and the indexer) claim it, <see cref="ReflectedResource.Restore(string)"/> gives it back;</item>
/// <item>anything else you can undo: <see cref="Claim"/> takes a name, runs your <c>apply</c> and remembers your <c>restore</c>.</item>
/// </list>
/// Claims belong to the calling mod. When a mod unloads, every claim it holds is given back, in reverse order. When two mods
/// want the same thing the first keeps it: the second gets <see cref="OverrideStatus.Conflict"/> and a warning in its
/// log that names both. Several mods can change different fields of one resource: the game gets all of them, and a mod that
/// restores or unloads takes out only its own. Thread-safe. Call it from mod code: a call from a background thread counts as
/// Kiln's own and is not given back when a mod unloads.
/// </summary>
/// <example>
/// <code>
/// var r = Overrides.Claim("mymod:damage-scale", () =&gt; Scale = 2f, () =&gt; Scale = 1f);
/// if (!r.Ok) Log.Warn(r.Message);                  // another mod has it
/// Overrides.RestoreAll();                          // or let the unload do it
/// </code>
/// </example>
public static class Overrides
{
    private static readonly object Gate = new();
    private static long _seq;
    private static readonly Dictionary<string, GenericClaim> Generic = new(StringComparer.Ordinal);
    private static readonly Dictionary<ResourceId, ResourceState> Resources = new();
    private static readonly HashSet<ModContext> Hooked = new();

    private sealed record Holder(string Mod, ModContext? Context);
    private sealed record GenericClaim(Holder Holder, string Target, Action Restore, long Seq);
    private sealed record FieldClaim(Holder Holder, string Path, int Offset, int Size, byte[] Bytes, string Value, long Seq);

    private sealed class ResourceState(byte[] original, GameType type)
    {
        public readonly byte[] Original = original;   // what the game served before the first claim: never changed
        public readonly GameType Type = type;
        public readonly List<FieldClaim> Claims = [];
    }

    private static Holder Who() => ModContext.CurrentMod is { } mod ? new Holder(mod.Info.Id, mod) : new Holder("kiln", null);

    private static void Say(Holder who, LogLevel level, string text)
    {
        if (who.Context is { } mod) mod.Log.Write(level, text);
        else Backend.Current.Log(0, level, text);
    }

    // A mod's claims are given back when it unloads; hooked once, under Gate.
    private static void Hook(Holder who)
    {
        if (who.Context is not { } mod || !Hooked.Add(mod)) return;
        mod.Retiring += () =>
        {
            lock (Gate) Hooked.Remove(mod);
            ReleaseHeldBy(who, unloading: true);
        };
    }

    // ---- claims of any kind ---------------------------------------------------------------------------------------

    /// <summary>
    /// Claims <paramref name="target"/> (any name that is the same for everyone who wants that thing, such as
    /// <c>"mymod:max-players"</c> or <c>"setting:pvp"</c>), runs <paramref name="apply"/> and remembers
    /// <paramref name="restore"/>. When another mod holds the target the result is <see cref="OverrideStatus.Conflict"/> and
    /// <paramref name="apply"/> does not run. Claiming a target you hold runs <paramref name="apply"/> again and keeps the
    /// first <paramref name="restore"/> (it knows the original). If <paramref name="apply"/> throws, the claim is dropped and
    /// <paramref name="restore"/> is not called, so make <paramref name="apply"/> all or nothing.
    /// </summary>
    /// <param name="target">What is claimed.</param>
    /// <param name="apply">Makes the change.</param>
    /// <param name="restore">Undoes it. Runs when <see cref="Release"/> or <see cref="RestoreAll"/> is called, or when the mod unloads (on the thread that unloads it).</param>
    public static OverrideResult Claim(string target, Action apply, Action restore)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(restore);
        var who = Who();
        var again = false;
        OverrideResult? refused = null;
        lock (Gate)
        {
            if (Generic.TryGetValue(target, out var held))
            {
                if (held.Holder != who) refused = Conflict(who, held.Holder.Mod, $"'{target}'", "");
                else again = true;
            }
            else
            {
                Generic[target] = new GenericClaim(who, target, restore, ++_seq);
                Hook(who);
            }
        }
        if (refused != null)
        {
            Say(who, LogLevel.Warn, "overrides: " + refused.Message);
            return refused;
        }
        try
        {
            apply();
        }
        catch
        {
            if (!again) lock (Gate) Generic.Remove(target);
            throw;
        }
        Say(who, LogLevel.Debug, $"overrides: {who.Mod} {(again ? "updated" : "claimed")} '{target}'");
        return new OverrideResult(OverrideStatus.Applied, $"'{target}' is {(again ? "updated" : "claimed")}");
    }

    /// <summary>Gives back this mod's claim on <paramref name="target"/> (runs its restore). False when this mod does not hold it.</summary>
    public static bool Release(string target)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        var who = Who();
        GenericClaim? claim;
        lock (Gate)
        {
            if (!Generic.TryGetValue(target, out claim) || claim.Holder != who) return false;
            Generic.Remove(target);
        }
        try
        {
            claim.Restore();
        }
        catch (Exception e)
        {
            Say(who, LogLevel.Error, $"overrides: restoring '{target}' failed: {e.Message}");
        }
        return true;
    }

    /// <summary>Gives back every claim this mod holds, newest first: resource fields get what the game had, and each
    /// <see cref="Claim"/>'s restore runs. Returns how many claims were given back. The unload does this by itself.</summary>
    public static int RestoreAll() => ReleaseHeldBy(Who(), unloading: false);

    /// <summary>The id of the mod that holds <paramref name="target"/> (a <see cref="Claim"/> name), or null.</summary>
    public static string? HolderOf(string target)
    {
        lock (Gate) return Generic.TryGetValue(target, out var c) ? c.Holder.Mod : null;
    }

    /// <summary>Every claim in force, by every mod, oldest first.</summary>
    public static IReadOnlyList<OverrideInfo> Active
    {
        get
        {
            lock (Gate)
            {
                var all = new List<(long Seq, OverrideInfo Info)>();
                foreach (var c in Generic.Values) all.Add((c.Seq, new OverrideInfo(c.Holder.Mod, c.Target, "")));
                foreach (var (id, st) in Resources)
                    foreach (var c in st.Claims) all.Add((c.Seq, new OverrideInfo(c.Holder.Mod, id.Describe(st.Type), $"{c.Path} = {c.Value}")));
                return all.OrderBy(x => x.Seq).Select(x => x.Info).ToList();
            }
        }
    }

    private static int ReleaseHeldBy(Holder who, bool unloading)
    {
        var generics = new List<GenericClaim>();
        var fields = 0;
        var problems = new List<string>();
        lock (Gate)
        {
            foreach (var c in Generic.Values.Where(c => c.Holder == who).ToList())
            {
                Generic.Remove(c.Target);
                generics.Add(c);
            }
            foreach (var (id, st) in Resources.ToList())
            {
                var n = st.Claims.RemoveAll(c => c.Holder == who);
                if (n == 0) continue;
                fields += n;
                try
                {
                    if (Recompose(id, st) is { } problem) problems.Add($"{id.Describe(st.Type)}: {problem}");
                }
                catch (Exception e)
                {
                    // one resource that cannot be put back must not keep the others (or, at unload, other services) from theirs
                    problems.Add($"{id.Describe(st.Type)}: putting it back failed: {e.Message}");
                }
                if (st.Claims.Count == 0) Resources.Remove(id);
            }
        }
        foreach (var c in generics.OrderByDescending(c => c.Seq))
        {
            try
            {
                c.Restore();
            }
            catch (Exception e)
            {
                Say(who, LogLevel.Error, $"overrides: restoring '{c.Target}' failed: {e.Message}");
            }
        }
        var count = fields + generics.Count;
        if (count > 0) Say(who, LogLevel.Info, $"overrides: gave back {count} change{(count == 1 ? "" : "s")} of {who.Mod}{(unloading ? " (unloading)" : "")}");
        foreach (var p in problems) Say(who, LogLevel.Warn, "overrides: " + p);
        return count;
    }

    // ---- fields of resources --------------------------------------------------------------------------------------

    private sealed record NativeOwner(ulong Value);
    private static readonly ConditionalWeakTable<IKilnBackend, NativeOwner> Owners = new();

    // One native owner for every composed resource: the layers of all mods are put together here, so no mod's override
    // hides another's. (A put per mod would each hold the whole resource, and the last put would win every field.)
    private static ulong SharedOwner => Owners.GetValue(Backend.Current, b => new NativeOwner(b.OwnerCreate("kiln.overrides"))).Value;

    private static bool Overlaps(FieldClaim c, int offset, int size) => c.Offset < offset + size && offset < c.Offset + c.Size;

    // Puts the resource together from the original bytes and every claim, oldest claim first; no claims: the game's own again.
    // Null when it worked, else what went wrong. Under Gate.
    private static string? Recompose(ResourceId id, ResourceState st)
    {
        var owner = SharedOwner;
        if (st.Claims.Count == 0)
        {
            ResourceStore.Remove(owner, id);
            return null;
        }
        var bytes = (byte[])st.Original.Clone();
        foreach (var c in st.Claims.OrderBy(c => c.Seq)) Buffer.BlockCopy(c.Bytes, 0, bytes, c.Offset, c.Size);
        var rc = ResourceStore.Put(owner, id, bytes);
        if (rc != 0) return $"kiln.assets did not take the resource (error {rc})";
        var served = ResourceStore.Read(id);
        return served != null && served.AsSpan().SequenceEqual(bytes) ? null
            : "the game does not get the changed bytes: the resource is not one of the game's own, or another override of it takes precedence";
    }

    private static OverrideResult Conflict(Holder who, string holder, string what, string detail) =>
        new(OverrideStatus.Conflict,
            $"refused: {what} is already changed by mod '{holder}'{detail}; mod '{who.Mod}' asked for it too. The first change keeps it until that mod restores it or unloads.",
            holder);

    private static string Show(GameType t, ReadOnlySpan<byte> bytes) =>
        GameValueCodec.IsScalar(t.Kind)
            ? GameValueCodec.Format(GameValueCodec.Read(bytes, t.Kind, t.Size, t.EnumValues), t.Kind, t.EnumValues)
            : new Guid(bytes).ToString();

    internal static OverrideResult SetField(ResourceId id, GameType type, string path, object? value)
    {
        if (!ResourceStore.Available || SharedOwner == 0)
            return new(OverrideStatus.Unavailable, "kiln.assets is not running here: nothing was changed");
        var who = Who();
        OverrideResult result;
        lock (Gate) result = SetFieldLocked(who, id, type, path, value);
        Say(who, result.Ok ? LogLevel.Debug : LogLevel.Warn, "overrides: " + result.Message);
        return result;
    }

    private static OverrideResult SetFieldLocked(Holder who, ResourceId id, GameType type, string path, object? value)
    {
        var what = $"{path} of {id.Describe(type)}";
        Resources.TryGetValue(id, out var existing);
        var original = existing?.Original ?? ResourceStore.Read(id);
        if (original == null) return new(OverrideStatus.NotFound, $"{id.Describe(type)} is not served by the game: nothing was changed");
        var spot = ResourceLayout.Locate(original, type, path);       // a bad path or value throws here, before anything changes
        var bytes = ResourceLayout.Encode(spot, value, path);
        if (existing?.Claims.FirstOrDefault(c => c.Holder != who && Overlaps(c, spot.Offset, bytes.Length)) is { } other)
            return Conflict(who, other.Holder.Mod, what, $" (set to {other.Value})");
        var state = existing ?? new ResourceState(original, type);
        var before = state.Claims.ToList();
        state.Claims.RemoveAll(c => c.Holder == who && Overlaps(c, spot.Offset, bytes.Length));
        var shown = Show(spot.Type, bytes);
        state.Claims.Add(new FieldClaim(who, path, spot.Offset, bytes.Length, bytes, shown, ++_seq));
        if (Recompose(id, state) is { } problem)
        {
            state.Claims.Clear();
            state.Claims.AddRange(before);
            Recompose(id, state);                                    // back to what was served before this call
            if (state.Claims.Count == 0) Resources.Remove(id);
            return new(OverrideStatus.Unavailable, $"{what} was not changed: {problem}");
        }
        Resources[id] = state;
        Hook(who);
        return new(OverrideStatus.Applied, $"{what} = {shown} (was {Show(spot.Type, original.AsSpan(spot.Offset, bytes.Length))})");
    }

    internal static bool RestoreField(ResourceId id, GameType type, string path)
    {
        var who = Who();
        var removed = 0;
        lock (Gate)
        {
            if (!Resources.TryGetValue(id, out var st)) return false;
            var spot = ResourceLayout.Locate(st.Original, type, path);
            removed = st.Claims.RemoveAll(c => c.Holder == who && Overlaps(c, spot.Offset, spot.Type.Size));
            if (removed == 0) return false;
            if (Recompose(id, st) is { } problem) Say(who, LogLevel.Warn, $"overrides: {path} of {id.Describe(type)}: {problem}");
            if (st.Claims.Count == 0) Resources.Remove(id);
        }
        Say(who, LogLevel.Debug, $"overrides: {who.Mod} gave back {path} of {id.Describe(type)}");
        return true;
    }

    internal static int RestoreResource(ResourceId id)
    {
        var who = Who();
        int removed;
        lock (Gate)
        {
            if (!Resources.TryGetValue(id, out var st)) return 0;
            removed = st.Claims.RemoveAll(c => c.Holder == who);
            if (removed == 0) return 0;
            if (Recompose(id, st) is { } problem) Say(who, LogLevel.Warn, $"overrides: {id.Describe(st.Type)}: {problem}");
            if (st.Claims.Count == 0) Resources.Remove(id);
        }
        Say(who, LogLevel.Debug, $"overrides: {who.Mod} gave back {removed} field(s) of {id.Describe(null)}");
        return removed;
    }

    internal static string? FieldHolder(ResourceId id, GameType type, string path)
    {
        lock (Gate)
        {
            if (!Resources.TryGetValue(id, out var st)) return null;
            var spot = ResourceLayout.Locate(st.Original, type, path);
            return st.Claims.FirstOrDefault(c => Overlaps(c, spot.Offset, spot.Type.Size))?.Holder.Mod;
        }
    }

    /// <summary>What the game served before the first claim on the resource, or null when nobody holds a field of it.</summary>
    internal static byte[]? OriginalBytes(ResourceId id)
    {
        lock (Gate) return Resources.TryGetValue(id, out var st) ? st.Original : null;
    }

    /// <summary>Drops every claim (tests).</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Generic.Clear();
            Resources.Clear();
            Hooked.Clear();
        }
    }
}
