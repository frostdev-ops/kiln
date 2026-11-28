// SPDX-License-Identifier: MIT
// Kiln.Markers' records and enums (chapter 04 §5). Session only: nothing here is saved.
namespace Kiln;

/// <summary>Where a marker is shown. A marker asks for one or more; it is shown on every surface it asks for or, when one
/// of them is off in this process, refused as a whole (nothing is drawn partly).</summary>
[Flags]
public enum MarkerSurface
{
    /// <summary>A native icon on the game's map (capability <c>markers.map</c>); needs an icon of the catalog.</summary>
    Map = 1,
    /// <summary>A glyph and its text on Kiln's drawn compass strip (capability <c>markers.compass</c>). Not the game's
    /// own compass.</summary>
    Compass = 2,
    /// <summary>A drawn label at the marker's position in the world (capability <c>markers.world</c>).</summary>
    World = 4,
}

/// <summary>What the native map shows for a marker's text.</summary>
public enum MarkerMapLabel
{
    /// <summary>The icon only (the default): the map's tooltip is the game's own for that icon type (the custom-pin types
    /// say "Custom marker"); the text is drawn on the compass and world surfaces.</summary>
    None = 0,
    /// <summary>The text is also the title of the game's own map tooltip, shown when the map's cursor is on the icon
    /// (capability <c>markers.map_text</c>: <c>markers.map</c> on, on a game build whose tooltip Kiln has checked). The
    /// game shows it as a name a player typed: its text filter may apply, and markup shows as typed. Two icon types name
    /// themselves: on <c>tower_locked</c> (Ancient Spire) the text is the line under the type's name, and on
    /// <c>playerwatersource</c> (Water Source) the tooltip keeps the type's name.</summary>
    NativeTooltip = 1,
}

/// <summary>Whose record a marker is.</summary>
public enum MarkerSource
{
    /// <summary>A client mod's own marker (<see cref="Markers.Set"/>).</summary>
    Local,
    /// <summary>A server's marker for every player (the broadcast layer).</summary>
    ServerBroadcast,
    /// <summary>A server's marker for one player (the per-player layer).</summary>
    ServerTarget,
    /// <summary>A marker this client received from the server.</summary>
    Received,
}

/// <summary>What a <see cref="MarkerChangedEvent"/> reports.</summary>
public enum MarkerChangeKind
{
    /// <summary>A new key.</summary>
    Added,
    /// <summary>A key's value changed.</summary>
    Updated,
    /// <summary>A key was removed.</summary>
    Removed,
    /// <summary>Every key of one publisher and source went at once; <see cref="MarkerChangedEvent.Affected"/> says how many.</summary>
    Cleared,
}

/// <summary>A marker type of the game's icon catalog (<see cref="Markers.Icons"/>), by its id. Icons are drawn on the map
/// only.</summary>
/// <param name="Id">The game's marker type id; 0 is <see cref="None"/>.</param>
public readonly record struct MarkerIcon(uint Id)
{
    /// <summary>No icon: for markers without the <see cref="MarkerSurface.Map"/> surface (a map marker needs one).</summary>
    public static MarkerIcon None { get; } = default;
}

/// <summary>One marker, by key. Defaults and units are the same in C#, the native ABI and JSON
/// (<see cref="FromJson(string)"/>).</summary>
/// <param name="Id">The key: 1 to 64 characters of <c>A-Z a-z 0-9 _ . : -</c>. One record per key and mod; setting a key
/// again replaces it.</param>
/// <param name="Position">Where it is, in metres: x and z in [-1000, 12000], y in [-1000, 5000]. Ignored (but finite) when
/// <paramref name="Follow"/> is set.</param>
/// <param name="Text">The text drawn on the compass and world surfaces, and with <see cref="MarkerMapLabel.NativeTooltip"/>
/// in the map's tooltip: at most 128 bytes of UTF-8 on one line, no control characters; may be empty.</param>
/// <param name="Icon">The map icon, from <see cref="Markers.Icons"/>: required with <see cref="MarkerSurface.Map"/>, and
/// <see cref="MarkerIcon.None"/> without it.</param>
/// <param name="Surfaces">Where it is shown (default map and compass).</param>
/// <param name="Follow">An entity to follow instead of a fixed position (a lease from <c>CaptureAnchorAsync</c>); null
/// for a fixed marker.</param>
/// <param name="Height">Metres above the point for the world label, in [-128, 128].</param>
/// <param name="MaxDistance">Metres from the camera beyond which it is not shown, in [0, 100000]; 0 = unlimited.</param>
/// <param name="Priority">Higher first when a surface has no room, in [-32768, 32767].</param>
/// <param name="Rgba">Colour of the compass glyph and the world label, 0xRRGGBBAA (map icons keep the game's colours).</param>
/// <param name="MapLabel">What the map shows for the text: <see cref="MarkerMapLabel.NativeTooltip"/> makes it the title of
/// the game's own map tooltip. <see cref="MarkerMapLabel.None"/> by default.</param>
public sealed partial record MarkerSpec(string Id, WorldPosition Position, string Text, MarkerIcon Icon,
    MarkerSurface Surfaces = MarkerSurface.Map | MarkerSurface.Compass, MarkerAnchor? Follow = null, float Height = 0,
    float MaxDistance = 0, int Priority = 0, uint Rgba = 0xffffffff, MarkerMapLabel MapLabel = MarkerMapLabel.None);

/// <summary>What became of a marker request, by Kiln's error rule: a bad argument throws <see cref="ArgumentException"/>, a
/// capability that is off throws <see cref="KilnUnavailableException"/> naming it, and a refusal by the current state comes
/// back here with <see cref="Ok"/> false and a <see cref="Reason"/>.</summary>
/// <param name="Ok">True when the request was stored and, for a server send, every connected recipient took it.</param>
/// <param name="Reason">Why not, starting with the refusal's name (<c>NotFound</c>, <c>Full</c>, <c>NotReady</c>,
/// <c>CatalogNotReady</c>, <c>RequiresClient</c>, <c>Partial</c>, ...); null when <see cref="Ok"/>.</param>
/// <param name="Committed">The store holds the request (also when it was identical to what it held). A server send can be
/// committed and still not <see cref="Ok"/> (<c>Partial</c>): do not treat that as a rollback.</param>
/// <param name="Queued">Server sends: connected recipients whose update was admitted to their link (0 for local markers).</param>
/// <param name="Refused">Server sends: connected recipients that cannot take it (a vanilla client, a pending handshake).</param>
/// <param name="Affected">Records changed: 0 for a value identical to the stored one or an empty clear.</param>
public sealed record MarkerResult(bool Ok, string? Reason, bool Committed, int Queued, int Refused, int Affected) : IKilnResult;
