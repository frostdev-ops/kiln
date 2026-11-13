// SPDX-License-Identifier: MIT
// Kiln.Settings in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (the first service tick: SettingsStartup); settings.ready follows the session and is refreshed on the service tick, never in a getter.
namespace Kiln;

public static unsafe partial class Settings
{
    /// <summary>The capability names Kiln.Settings answers for.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row).</summary>
        public const string Service = Name;

        /// <summary>Kiln.Settings' session row: the live settings were found (<see cref="Settings.Ready"/>).</summary>
        public const string Ready = "settings.ready";

        /// <summary>The sigdb features kiln.settings serves.</summary>
        public static readonly string[] Features = ["settings.live", "settings.world", "settings.resend", "settings.daytime"];
    }

    internal static readonly ServiceCapRows Rows = new(Name, ReportCapabilities, everySteps: 25);

    static Settings() => Rows.Ensure();

    private static void ReportCapabilities()
    {
        if (T == null)
        {
            // sigdb may have resolved a feature that no service is here to serve
            var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing, "kiln.settings is not loaded", Caps.Service);
            foreach (var name in Caps.Features) ServiceCaps.Lower(name, reason, text);
        }
        ServiceCaps.Session(Caps.Ready, Ready,
            "the live settings are found at the end of the server's first world step", Caps.Service);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the capability rows are there before a
/// mod asks for them, not from Kiln.Settings' first use.</summary>
internal static class SettingsStartup
{
    static SettingsStartup() => Settings.Rows.Ensure();
}
