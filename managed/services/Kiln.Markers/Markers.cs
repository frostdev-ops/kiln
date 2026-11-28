// SPDX-License-Identifier: MIT
// Markers: session-only markers by key (chapter 04 §4.1, §4.5, §5). The native kiln.markers holds every record and checks
// every rule; this wrapper binds each mod to its id as the namespace on first use, marshals the spec and maps the answer by
// the error rule. A mod's records and bindings go when it unloads (release_owner on ModContext.Retiring).
using System.Text;

namespace Kiln;

/// <summary>Markers by key on the game's map, Kiln's drawn compass strip and world labels. Session only: nothing is saved;
/// a client's markers clear when its world ends, and a mod's own when it unloads.</summary>
/// <remarks>
/// <para>Each mod has its own keys (its mod id is its namespace): two mods never overwrite each other's markers.</para>
/// <para>Limits (Kiln's policy, not the game's): 128 local markers per mod, 512 in all, 1024 with received ones; at most 64
/// accepted changes of one mod (256 of all) wait for the next client step, the next is refused with <c>Full</c>.
/// Text is kept until the game exits: 4 MiB in all and 256 KiB of new text per mod; the same text again costs nothing.
/// Nothing is evicted to make room.</para>
/// <para>Results follow Kiln's error rule (<see cref="MarkerResult"/>).</para>
/// </remarks>
public static unsafe partial class Markers
{
    /// <summary>Sets the calling mod's local marker of <see cref="MarkerSpec.Id"/> (adds it or replaces its value).
    /// Setting an identical value again is <c>Ok</c> with <see cref="MarkerResult.Affected"/> 0 and raises no event. A
    /// change raises <see cref="Changed"/> at the next client step.</summary>
    /// <param name="marker">The marker. Every surface it asks for must be available: a missing one throws, nothing is
    /// drawn partly.</param>
    /// <returns><c>Ok</c>, or a refusal: <c>NotReady</c> (no client world: nothing is kept for later), <c>Full</c>,
    /// <c>CatalogNotReady</c> (a map marker before <c>Markers.Icons.IsReady</c>).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/>, its id or its text is null.</exception>
    /// <exception cref="ArgumentException">A value outside the rules of <see cref="MarkerSpec"/>, an icon that is not in
    /// the catalog or not for the map, or a disposed <see cref="MarkerSpec.Follow"/> lease.</exception>
    /// <exception cref="KilnUnavailableException"><c>markers.local</c>, or a surface's capability (<c>markers.map</c>,
    /// <c>markers.compass</c>, <c>markers.world</c>, <c>markers.anchors.client</c> for a followed marker,
    /// <c>markers.map_text</c> for <see cref="MarkerMapLabel.NativeTooltip"/>: off on a game build whose map tooltip Kiln
    /// has not checked), is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code.</exception>
    [ClientOnly, AnyThread, RequiresCapability("markers.local")]
    public static MarkerResult Set(MarkerSpec marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(marker.Id, nameof(marker) + ".Id");
        ArgumentNullException.ThrowIfNull(marker.Text, nameof(marker) + ".Text");
        MarkersCaps.Require(MarkersCaps.Local);
        if (marker.Follow is { IsDisposed: true }) throw new ArgumentException("the marker's Follow lease was disposed", nameof(marker));
        var t = MarkersNative.T;
        if (!Bind(t, nameof(Set), out var owner, out var refusal)) return refusal!;
        var key = Encoding.UTF8.GetBytes(marker.Id);
        var text = Encoding.UTF8.GetBytes(marker.Text);
        MarkersNative.Result r = default;
        r.Size = (uint)sizeof(MarkersNative.Result);
        int rc;
        fixed (byte* k = key, x = text)
        {
            var s = new MarkersNative.Spec
            {
                Size = (uint)sizeof(MarkersNative.Spec),
                Surfaces = (uint)marker.Surfaces,
                Key = new NativeStr { Ptr = k, Len = (nuint)key.Length },
                Position = new MarkersNative.Vec3 { X = marker.Position.X, Y = marker.Position.Y, Z = marker.Position.Z },
                Follow = marker.Follow?.Handle ?? 0,
                Height = marker.Height,
                MaxDistance = marker.MaxDistance,
                IconType = marker.Icon.Id,
                Rgba = marker.Rgba,
                Priority = marker.Priority,
                MapLabelMode = (uint)marker.MapLabel,
                Text = new NativeStr { Ptr = x, Len = (nuint)text.Length },
            };
            rc = t->SetLocal(owner, &s, &r);
        }
        return Answer(rc, &r, MarkersCaps.Local, nameof(marker));
    }

    /// <summary>Removes the calling mod's local marker of <paramref name="id"/>; <see cref="Changed"/> reports it at the
    /// next client step.</summary>
    /// <param name="id">The marker's key.</param>
    /// <returns><c>Ok</c>, or <c>NotFound</c> (no such marker), <c>NotReady</c>, <c>Full</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a key (1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>).</exception>
    /// <exception cref="KilnUnavailableException"><c>markers.local</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code.</exception>
    [ClientOnly, AnyThread, RequiresCapability("markers.local")]
    public static MarkerResult Remove(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        MarkersCaps.Require(MarkersCaps.Local);
        var t = MarkersNative.T;
        if (!Bind(t, nameof(Remove), out var owner, out var refusal)) return refusal!;
        var key = Encoding.UTF8.GetBytes(id);
        MarkersNative.Result r = default;
        r.Size = (uint)sizeof(MarkersNative.Result);
        int rc;
        fixed (byte* k = key) rc = t->RemoveLocal(owner, new NativeStr { Ptr = k, Len = (nuint)key.Length }, &r);
        return Answer(rc, &r, MarkersCaps.Local, nameof(id));
    }

    /// <summary>Removes every local marker of the calling mod; <see cref="Changed"/> reports one
    /// <see cref="MarkerChangeKind.Cleared"/> with their count at the next client step. Clearing nothing is <c>Ok</c>
    /// with <see cref="MarkerResult.Affected"/> 0.</summary>
    /// <returns><c>Ok</c>, or <c>NotReady</c>, <c>Full</c>.</returns>
    /// <exception cref="KilnUnavailableException"><c>markers.local</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code.</exception>
    [ClientOnly, AnyThread, RequiresCapability("markers.local")]
    public static MarkerResult Clear()
    {
        MarkersCaps.Require(MarkersCaps.Local);
        var t = MarkersNative.T;
        if (!Bind(t, nameof(Clear), out var owner, out var refusal)) return refusal!;
        MarkersNative.Result r = default;
        r.Size = (uint)sizeof(MarkersNative.Result);
        var rc = t->ClearLocal(owner, &r);
        return Answer(rc, &r, MarkersCaps.Local, null);
    }

    // ---- the answer by the error rule ----

    internal static MarkerResult Answer(int rc, MarkersNative.Result* r, string capability, string? parameter)
    {
        var reason = MarkersNative.Text(r->Reason, 256);
        switch (rc)
        {
            case MarkersNative.Ok:
                var ok = r->Status == MarkersNative.Accepted;
                return new MarkerResult(ok, ok ? null : reason.Length > 0 ? reason : $"status {r->Status}", r->Committed != 0,
                    (int)r->Queued, (int)r->Refused, (int)r->Affected);
            case MarkersNative.InvalidArgument:
                throw new ArgumentException(reason.Length > 0 ? reason : "kiln.markers refused an argument", parameter);
            case MarkersNative.Unavailable:
                throw MarkersCaps.Off(reason.StartsWith("markers.", StringComparison.Ordinal) ? reason : capability);
            case MarkersNative.WrongThread:
                throw new InvalidOperationException($"kiln.markers: this call is not allowed on this thread ({reason})");
            default:
                throw new InvalidOperationException($"kiln.markers answered {rc}{(reason.Length > 0 ? ": " + reason : "")}");
        }
    }

    // ---- each mod's binding: its id is its namespace ----

    private static readonly object BindLock = new();
    private static readonly HashSet<ModContext> Bound = new(ReferenceEqualityComparer.Instance);

    private static bool Bind(MarkersNative.Table* t, string api, out ulong owner, out MarkerResult? refusal)
    {
        refusal = null;
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException($"Markers.{api} must be called from a mod's code");
        if (!mod.Alive) throw new InvalidOperationException($"Markers.{api}: the mod {mod.Info.Id} is unloading");
        owner = mod.Owner;
        if (owner == 0) throw new InvalidOperationException($"Markers.{api}: the mod {mod.Info.Id} has no native owner (no Kiln core in this process)");
        lock (BindLock)
        {
            if (Bound.Contains(mod)) return true;
            var ns = Encoding.UTF8.GetBytes(mod.Info.Id);
            int rc;
            fixed (byte* p = ns) rc = t->BindOwner(owner, new NativeStr { Ptr = p, Len = (nuint)ns.Length });
            if (rc == MarkersNative.BufferTooSmall)
            {
                refusal = new MarkerResult(false, $"Full: 64 mods have used markers in this game; '{mod.Info.Id}' cannot be bound until the game restarts",
                    false, 0, 0, 0);
                return false;
            }
            if (rc != MarkersNative.Ok)
            {
                refusal = new MarkerResult(false, $"NamespaceInUse: kiln.markers refused the namespace '{mod.Info.Id}' (another publisher holds it)",
                    false, 0, 0, 0);
                return false;
            }
            Bound.Add(mod);
        }
        mod.Retiring += () =>
        {
            lock (BindLock) Bound.Remove(mod);
            var tt = MarkersNative.T;
            if (tt != null) tt->ReleaseOwner(mod.Owner);
        };
        return true;
    }
}
