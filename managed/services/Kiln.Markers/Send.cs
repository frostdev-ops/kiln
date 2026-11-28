// SPDX-License-Identifier: MIT
// Markers sent by a server (dossier chapter 04 §4.5, §5): two layers per mod, the broadcast (every player, also later
// joins) and one per player connection (an override or a suppression). What a player sees is its override, nothing where
// it is suppressed, else the broadcast. Players whose client runs Kiln receive them through the built-in receiver, also
// without this mod; vanilla players get nothing and are counted as refused. Session only: a server world's markers end
// with it, a player's own layer with its connection.
using System.Text;

namespace Kiln;

public static unsafe partial class Markers
{
    /// <summary>Sends the calling mod's marker of <see cref="MarkerSpec.Id"/> to every player (<paramref name="player"/>
    /// <see cref="EntityId.None"/>: the broadcast, also for players who join later in this server session) or to one
    /// connected player (that player's own layer, over the broadcast; it ends with the player's connection). Sending an
    /// identical value again changes nothing and raises no event; it retries the players that have not taken it yet.
    /// <see cref="Changed"/> reports each stored change at the next server step.</summary>
    /// <param name="player">The recipient, or <see cref="EntityId.None"/> for everyone.</param>
    /// <param name="marker">The marker. A map marker needs the icon catalog (<c>Markers.Icons.IsReady</c>): a dedicated
    /// server takes it from the first verified Kiln client; compass and world markers need none. A followed marker
    /// (<see cref="MarkerSpec.Follow"/>) needs <c>markers.anchors.server</c>; the server then sends its moves. A
    /// <see cref="MarkerMapLabel.NativeTooltip"/> marker goes to the players whose Kiln shows the game's map tooltip (their
    /// client's <c>markers.map_text</c>); every other player is refused for it (<c>RequiresClient</c>): send them the same key
    /// with <see cref="MarkerMapLabel.None"/> for the marker without the tooltip.</param>
    /// <returns>Stored, and for each connected player whether the update could go: <c>Ok</c> when every recipient can
    /// take it (or there is none yet: it waits for later joins). <see cref="MarkerResult.Committed"/> with <c>Ok</c> false
    /// is a partial delivery, never a rollback: <see cref="MarkerResult.Queued"/> players get it,
    /// <see cref="MarkerResult.Refused"/> cannot (no Kiln, a surface or the catalog missing on their side, still in the
    /// handshake, or their marker link is full or stalled); the reason starts with <c>Partial</c>, <c>RequiresClient</c>,
    /// <c>NotReady</c> or <c>Full</c>. Not stored (<see cref="MarkerResult.Committed"/> false): <c>NotFound</c> (the player is
    /// not connected), <c>NotReady</c> (no server world), <c>Full</c> (128 sent markers per mod, 512 in the broadcast, 1024 on
    /// the server, 512 per player, 64 followed per player, 128 followed entities), <c>CatalogNotReady</c>,
    /// <c>InvalidAnchor</c>. Delivery counts are the transport's, not proof that a player saw it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="marker"/>, its id or its text is null.</exception>
    /// <exception cref="ArgumentException">A value outside the rules of <see cref="MarkerSpec"/>, an icon that is not in
    /// the catalog or not for the map, or a disposed <see cref="MarkerSpec.Follow"/> lease.</exception>
    /// <exception cref="KilnUnavailableException"><c>markers.send</c> or <c>markers.anchors.server</c> (a followed marker) is
    /// off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code, or not on the tick thread.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("markers.send")]
    public static MarkerResult Send(EntityId player, MarkerSpec marker)
    {
        ArgumentNullException.ThrowIfNull(marker);
        ArgumentNullException.ThrowIfNull(marker.Id, nameof(marker) + ".Id");
        ArgumentNullException.ThrowIfNull(marker.Text, nameof(marker) + ".Text");
        MarkersCaps.Require(MarkersCaps.Send);
        Services.RequireTickThread("Markers.Send");
        if (marker.Follow is { IsDisposed: true }) throw new ArgumentException("the marker's Follow lease was disposed", nameof(marker));
        var t = MarkersNative.T;
        if (!Bind(t, nameof(Send), out var owner, out var refusal)) return refusal!;
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
            rc = t->Send(owner, player.Value, &s, &r);
        }
        return Answer(rc, &r, MarkersCaps.Send, nameof(marker));
    }

    /// <summary>Removes the calling mod's sent marker <paramref name="id"/> from one layer: <paramref name="player"/>
    /// <see cref="EntityId.None"/> removes the broadcast one (players' own overrides stay); a player removes that player's
    /// override or suppression, so the broadcast one shows again for them.</summary>
    /// <param name="player">The layer: <see cref="EntityId.None"/> for the broadcast, else a connected player.</param>
    /// <param name="id">The marker's key.</param>
    /// <returns><c>Ok</c> (or a partial delivery as with <see cref="Send"/>), <c>NotFound</c> (no such marker in that layer,
    /// or the player is not connected), <c>NotReady</c>, <c>Full</c> (the step's change queue).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a key (1 to 64 of <c>A-Z a-z 0-9 _ . : -</c>).</exception>
    /// <exception cref="KilnUnavailableException"><c>markers.send</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code, or not on the tick thread.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("markers.send")]
    public static MarkerResult Unsend(EntityId player, string id) => Keyed(player, id, nameof(Unsend));

    /// <summary>Hides the calling mod's marker <paramref name="id"/> from one player without removing it for anyone else:
    /// the player's layer holds a suppression (the broadcast one stays stored). <see cref="Unsend"/> or
    /// <see cref="ClearSent"/> for that player shows it again.</summary>
    /// <param name="player">A connected player (not <see cref="EntityId.None"/>).</param>
    /// <param name="id">The marker's key; the player must see such a marker now.</param>
    /// <returns><c>Ok</c> (also when it is suppressed already: nothing changes), <c>NotFound</c> (the player sees no such
    /// marker, or is not connected), <c>NotReady</c>, <c>Full</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="player"/> is <see cref="EntityId.None"/>, or <paramref name="id"/>
    /// is not a key.</exception>
    /// <exception cref="KilnUnavailableException"><c>markers.send</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code, or not on the tick thread.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("markers.send")]
    public static MarkerResult SuppressSent(EntityId player, string id)
    {
        if (player.IsNone) throw new ArgumentException("SuppressSent hides a marker from one player; to remove a broadcast marker use Unsend(EntityId.None, id)", nameof(player));
        return Keyed(player, id, nameof(SuppressSent));
    }

    /// <summary>Removes the calling mod's sent markers: of one player's layer (the broadcast shows again for them), or with
    /// <see cref="EntityId.None"/> all of them, the broadcast and every player's layer. <see cref="Changed"/> reports one
    /// <see cref="MarkerChangeKind.Cleared"/> with the count; clearing nothing is <c>Ok</c> with
    /// <see cref="MarkerResult.Affected"/> 0.</summary>
    /// <param name="player">A connected player, or <see cref="EntityId.None"/> (the default) for everything.</param>
    /// <returns><c>Ok</c> (or a partial delivery as with <see cref="Send"/>), <c>NotFound</c> (the player is not connected),
    /// <c>NotReady</c>, <c>Full</c>.</returns>
    /// <exception cref="KilnUnavailableException"><c>markers.send</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not called from a mod's code, or not on the tick thread.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability("markers.send")]
    public static MarkerResult ClearSent(EntityId player = default)
    {
        MarkersCaps.Require(MarkersCaps.Send);
        Services.RequireTickThread("Markers.ClearSent");
        var t = MarkersNative.T;
        if (!Bind(t, nameof(ClearSent), out var owner, out var refusal)) return refusal!;
        MarkersNative.Result r = default;
        r.Size = (uint)sizeof(MarkersNative.Result);
        var rc = t->ClearSent(owner, player.Value, &r);
        return Answer(rc, &r, MarkersCaps.Send, nameof(player));
    }

    private static MarkerResult Keyed(EntityId player, string id, string api)
    {
        ArgumentNullException.ThrowIfNull(id);
        MarkersCaps.Require(MarkersCaps.Send);
        Services.RequireTickThread("Markers." + api);
        var t = MarkersNative.T;
        if (!Bind(t, api, out var owner, out var refusal)) return refusal!;
        var key = Encoding.UTF8.GetBytes(id);
        MarkersNative.Result r = default;
        r.Size = (uint)sizeof(MarkersNative.Result);
        int rc;
        fixed (byte* k = key)
        {
            var s = new NativeStr { Ptr = k, Len = (nuint)key.Length };
            rc = api == nameof(Unsend) ? t->Unsend(owner, player.Value, s, &r) : t->SuppressSent(owner, player.Value, s, &r);
        }
        return Answer(rc, &r, MarkersCaps.Send, nameof(id));
    }
}
