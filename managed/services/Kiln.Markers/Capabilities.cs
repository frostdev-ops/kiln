// SPDX-License-Identifier: MIT
// Kiln.Markers in the capability registry (docs/kiln/services/caps.md). kiln.markers is the core's row for the table; the
// service's own rows (markers.local, markers.map, ...) are reported by kiln-markers.dll itself, which knows what it built
// and what its game build resolved; markers.client_map, markers.anchors.server and markers.anchors.client are sigdb
// features. Kiln.Markers only fills in for a missing table: then every row is off with the reason that the service is not
// loaded, and the sigdb features it serves are lowered. Reported at the first service tick (MarkersStartup) and at the
// first use; again when a table appears late.
namespace Kiln;

internal static class MarkersCaps
{
    /// <summary>kiln.markers' table is here (the core's row).</summary>
    public const string Service = "kiln.markers";
    /// <summary>The local store (<see cref="Markers.Set"/>, <see cref="Markers.Remove"/>, <see cref="Markers.Clear"/>).</summary>
    public const string Local = "markers.local";
    /// <summary>The icon catalog (<c>Markers.Icons</c>); its warm-up is <c>Markers.Icons.IsReady</c>, not this row.</summary>
    public const string Catalog = "markers.catalog";
    /// <summary>Native map icons (the Map surface).</summary>
    public const string Map = "markers.map";
    /// <summary>Kiln's drawn compass strip (the Compass surface).</summary>
    public const string Compass = "markers.compass";
    /// <summary>Drawn world labels (the World surface).</summary>
    public const string World = "markers.world";
    /// <summary>A world label that follows an entity.</summary>
    public const string Nameplates = "markers.nameplates";
    /// <summary>Anchors on a client's world (sigdb feature, lowered by the service).</summary>
    public const string AnchorsClient = "markers.anchors.client";
    /// <summary>Anchors on the server world (sigdb feature, lowered by the service).</summary>
    public const string AnchorsServer = "markers.anchors.server";
    /// <summary>Server layers and the built-in receiver's link.</summary>
    public const string Send = "markers.send";
    /// <summary>The native map tooltip shows a marker's text (MarkerMapLabel.NativeTooltip): markers.map and the tooltip check
    /// (sigdb markers.map_tooltip_text) on a game client; WrongSide on a dedicated server, which sends such markers.</summary>
    public const string MapText = "markers.map_text";
    /// <summary>sigdb: the guards of the native map list.</summary>
    public const string ClientMap = "markers.client_map";

    private static readonly string[] ServiceRows = [Local, Catalog, Map, Compass, World, Nameplates, Send, MapText];
    private static readonly string[] Features = [ClientMap, AnchorsServer, AnchorsClient];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static unsafe void Report()
    {
        var here = MarkersNative.T != null;
        if (!here) ServiceCaps.LowerFeatures(Service, Features);
        foreach (var row in ServiceRows)
        {
            // the built-in receiver could not open its channel (a network limit below 16384, no kiln.net): it says why
            if (row == Send && here && MarkersBridge.Off is { } off) ServiceCaps.Report(row, CapabilityState.Unavailable, off.Reason, off.Text);
            else ServiceCaps.Static(row, here, CapabilityReason.ServiceMissing, "kiln.markers is not loaded", Service);
        }
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="capability"/> unless it is available
    /// and the table is here. Decided from what is true now: when the table is here but the row reads off, the rows are
    /// reported again first (a table found after the first report must not stay refused until the next refresh).</summary>
    public static unsafe void Require(string capability)
    {
        Rows.Ensure();
        if (MarkersNative.T == null) throw ServiceCaps.Unavailable(capability);
        if (Capabilities.Is(capability)) return;
        Rows.Refresh();
        if (Capabilities.Is(capability)) return;
        throw ServiceCaps.Unavailable(capability);
    }

    /// <summary>The exception for a native "unavailable" answer: <paramref name="capability"/> is the row the service
    /// named (its result's reason), or the member's own row when it named none.</summary>
    public static KilnUnavailableException Off(string capability)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, "kiln.markers answered that it is unavailable");
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick, so the rows are there before a mod asks, and
/// the built-in receiver (kiln:markers) runs without any author mod.</summary>
internal static class MarkersStartup
{
    static MarkersStartup()
    {
        MarkersCaps.Rows.Ensure();
        MarkersBridge.Start();
    }
}
