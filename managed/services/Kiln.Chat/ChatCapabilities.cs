// SPDX-License-Identifier: MIT
// Kiln.Chat in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's first
// use; the rows are fixed from then on.
namespace Kiln;

public static unsafe partial class Chat
{
    /// <summary>The capability names Kiln.Chat answers for.</summary>
    internal static class Caps
    {
        /// <summary>The native table is here (the core's row).</summary>
        public const string Service = "kiln.chat";

        /// <summary>The sigdb features kiln.chat serves.</summary>
        public static readonly string[] Features = ["chat.send", "chat.receive", "chat.players"];
    }

    internal static readonly ServiceCapRows Rows = new(Caps.Service, ReportCapabilities);

    private static void ReportCapabilities()
    {
        if (T != null) return;
        // sigdb may have resolved a feature that no service is here to serve
        var (reason, text) = ServiceCaps.Cause(CapabilityReason.ServiceMissing, "kiln.chat is not loaded", Caps.Service);
        foreach (var name in Caps.Features) ServiceCaps.Lower(name, reason, text);
    }
}
