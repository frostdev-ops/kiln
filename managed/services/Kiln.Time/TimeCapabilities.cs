// SPDX-License-Identifier: MIT
// Kiln.Time in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's start
// (TimeStartup, the first service tick); what kiln.time can do is fixed once it is loaded.
namespace Kiln;

internal static unsafe class TimeCaps
{
    /// <summary>kiln.time's table is here (the core's row).</summary>
    public const string Service = "kiln.time";

    /// <summary>Kiln.Time's own row: the clock's speed can be held (<see cref="Time.SetSpeed"/>, <see cref="Time.SetCycle"/>).</summary>
    public const string Speed = "time.speed";

    /// <summary>The sigdb features kiln.time serves.</summary>
    public static readonly string[] Features =
        ["time.read", "time.scale", "time.set", "time.sync_probe", "time.weather", "time.weather_choose"];

    internal static readonly ServiceCapRows Rows = new(Service, Report);

    private static void Report()
    {
        if (!Time.Available) ServiceCaps.LowerFeatures(Service, Features);
        ServiceCaps.Static(Speed, Time.CanSetSpeed, Time.SpeedOffReason(out var text), text, Service, "time.scale");
    }

    /// <summary>The exception a member throws when <paramref name="capability"/> is off; the rows are reported first.</summary>
    public static KilnUnavailableException Off(string capability, string detail)
    {
        Rows.Ensure();
        return ServiceCaps.Unavailable(capability, detail);
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: time.speed is there before a mod asks for
/// it, not from Kiln.Time's first use.</summary>
internal static class TimeStartup
{
    static TimeStartup() => TimeCaps.Rows.Ensure();
}
