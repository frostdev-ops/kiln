// SPDX-License-Identifier: MIT
// Kiln.Vfx in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). In the game client the sigdb
// features and the kiln.vfx row answer for playback. On the server side nothing is played here: calls are relayed to
// Kiln clients, and vfx.relay and screen.relay are what Vfx.Available says there. Reported at the service's start (the first service
// tick) and refreshed on the service tick, which runs on the server world's thread.
namespace Kiln;

internal static class VfxCaps
{
    /// <summary>Kiln.Vfx's session row: a VFX played on the server side reaches someone (a Kiln client with the relay
    /// channel, or this host's own client). What <see cref="Vfx.Available"/> says on the server side.</summary>
    public const string RelayRow = "vfx.relay";

    /// <summary>Kiln.Vfx's row for screen effects on the server side: the relay channel is open
    /// (<see cref="ScreenEffects.Available"/> there).</summary>
    public const string ScreenRelayRow = "screen.relay";

    private const string Net = "kiln.net";

    internal static readonly ServiceCapRows Rows = new("kiln.vfx", Report, everySteps: 25);

    private static void Report()
    {
        Relay(RelayRow, VfxRelay.ChannelName, VfxRelay.Open, VfxRelay.HasListeners, "vfx.play");
        // screen effects: an open channel is all ScreenEffects.Available asks on the server side
        Relay(ScreenRelayRow, ScreenRelay.ChannelName, ScreenRelay.Open, ScreenRelay.Open, "kiln.screen");
    }

    // One relay row: available while someone takes the relayed calls, pending while the channel is open and nobody
    // listens, unavailable without the channel (kiln.net) and in a game client that only joined a world.
    private static void Relay(string name, string channel, bool open, bool listeners, string clientRow)
    {
        if (!Client.IsDedicatedServer && !Client.IsHosting)
        {
            ServiceCaps.Report(name, CapabilityState.Unavailable, CapabilityReason.WrongSide,
                $"the relay is the server side's; a game client that joined a world plays for itself ({clientRow})");
            return;
        }
        if (listeners)
        {
            ServiceCaps.Report(name, CapabilityState.Available);
            return;
        }
        if (!open)
        {
            ServiceCaps.Probe(Net);  // kiln.net's wrapper asks for its table once: the row must say why under this binding
            ServiceCaps.Static(name, false, CapabilityReason.ServiceMissing, $"the relay channel {channel} is not open", Net);
            return;
        }
        ServiceCaps.Report(name, CapabilityState.Pending, CapabilityReason.Session, $"no Kiln client with the {channel} channel is connected");
    }
}
