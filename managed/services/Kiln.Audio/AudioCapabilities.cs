// SPDX-License-Identifier: MIT
// Kiln.Audio in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). In the game client the sigdb
// features and the kiln.audio row answer for playback. On the server side nothing is played here: calls are relayed to
// Kiln clients, and audio.relay is what Sound.Available says there. Reported at the service's start (the first service
// tick) and refreshed on the service tick, which runs on the server world's thread.
namespace Kiln;

internal static class AudioCaps
{
    /// <summary>Kiln.Audio's session row: a sound, track or voice line played on the server side reaches someone (a Kiln
    /// client with the relay channel, or this host's own client). What <see cref="Sound.Available"/> and
    /// <see cref="Voiceover.Available"/> say on the server side.</summary>
    public const string RelayRow = "audio.relay";

    private const string Net = "kiln.net";

    internal static readonly ServiceCapRows Rows = new("kiln.audio", Report, everySteps: 25);

    private static void Report()
    {
        Relay(RelayRow, AudioRelay.ChannelName, AudioRelay.Open, AudioRelay.HasListeners, "audio.play");
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
