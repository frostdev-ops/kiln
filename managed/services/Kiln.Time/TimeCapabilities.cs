// SPDX-License-Identifier: MIT
// Kiln.Time in the capability registry (Kiln.Capabilities; docs/kiln/services/caps.md). Reported at the service's start
// (TimeStartup, the first service tick); what kiln.time can do is fixed once it is loaded, and time.speed follows the
// session (the speed is held on the server world's clock), refreshed on the service tick, never in a getter.
namespace Kiln;

internal static unsafe class TimeCaps
{
    /// <summary>kiln.time's table is here (the core's row).</summary>
    public const string Service = "kiln.time";

    /// <summary>Kiln.Time's session row: the clock's speed can be held now (<see cref="Time.SetSpeed"/>,
    /// <see cref="Time.SetCycle"/>): kiln.time can hold it and the server world's clock is there; pending without one.</summary>
    public const string Speed = "time.speed";

    /// <summary>The sigdb features kiln.time serves.</summary>
    public static readonly string[] Features =
        ["time.read", "time.scale", "time.set", "time.sync_probe", "time.weather", "time.weather_choose"];

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25, refreshOnlyWithService: true);

    private static void Report()
    {
        if (!Time.Available) ServiceCaps.LowerFeatures(Service, Features);
        if (Time.CanSetSpeed)
            ServiceCaps.Session(Speed, Time.ServerClock,
                "the speed is held on the server world's clock, there once a server world runs", Service, "time.scale");
        else
            ServiceCaps.Static(Speed, false, Time.SpeedOffReason(out var text), text, Service, "time.scale");
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
