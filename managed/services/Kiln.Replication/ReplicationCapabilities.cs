// SPDX-License-Identifier: MIT
// Kiln.Replication in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's
// start (ReplicationStartup, the first service tick); what kiln.repl can do is fixed once it is loaded.
namespace Kiln;

internal static class ReplicationCaps
{
    /// <summary>kiln.repl's table is here (the core's row).</summary>
    public const string Service = "kiln.repl";

    /// <summary>sigdb: what the server sends each peer (server side, also a hosted world).</summary>
    public const string Observe = "repl.observe";

    /// <summary>sigdb (client exe): what a Kiln client receives. Held by the client gate until the guided check
    /// 11-repl.receive-watch has passed; kiln.repl has no receive side yet in this version.</summary>
    public const string Client = "repl.client";

    /// <summary>sigdb: the loopback harness (<see cref="ReplicationLab"/>); lab servers only: kiln.json's policy "any"
    /// and a kiln.net lab port.</summary>
    public const string Loopback = "repl.loopback";

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        var client = Mods.Side == ModSide.Client;
        string[] features = client ? [Observe, Client] : [Observe, Loopback];
        if (!ReplNative.Available)
        {
            ServiceCaps.LowerFeatures(Service, features);
        }
        else
        {
            var caps = ReplNative.Capabilities();
            if ((caps & ReplNative.CapObserve) == 0)
                ServiceCaps.Lower(Observe, CapabilityReason.Faulted, "kiln.repl turned it off (see kiln.log)");
            if (client && (caps & ReplNative.CapClient) == 0)
                ServiceCaps.Lower(Client, CapabilityReason.ServiceOlder, "kiln.repl has no client receive side in this version");
            if (!client && (caps & ReplNative.CapLoopback) == 0)
                ServiceCaps.Lower(Loopback, CapabilityReason.Policy,
                    "lab servers only: needs kiln.json policy \"any\" and kiln.net's lab port (labPort)");
        }
        // the side a row is not for (sigdb declares it on the other side only)
        if (client) ServiceCaps.Report(Loopback, CapabilityState.Unavailable, CapabilityReason.WrongSide, "the loopback harness runs on lab servers");
        else ServiceCaps.Report(Client, CapabilityState.Unavailable, CapabilityReason.WrongSide, "a game client's receive side");
    }

    /// <summary>The exception a member throws when <paramref name="capability"/> is off; the rows are reported first.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: the repl.* rows are there before a mod
/// asks for them, not from Kiln.Replication's first use.</summary>
internal static class ReplicationStartup
{
    static ReplicationStartup() => ReplicationCaps.Rows.Ensure();
}
