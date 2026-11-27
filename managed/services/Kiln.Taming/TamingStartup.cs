// SPDX-License-Identifier: MIT
// Kiln.Taming's start (ServiceTick.AutoStart): its rows and its tick. taming.native is a sigdb feature (the core reports
// it); taming.native_observe (who tamed an animal) is Kiln.Taming's own row, off until a lab with a player proves where the
// game's petting player can be read (docs/re/taming.md 4, 7).
namespace Kiln;

internal static class TamingCaps
{
    /// <summary>Kiln.Taming is loaded.</summary>
    public const string Service = "kiln.taming";
    /// <summary>Who tamed a native animal: not proven on any build.</summary>
    public const string NativeObserve = "taming.native_observe";

    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25);

    /// <summary>Whether a capability is on (tests replace it).</summary>
    internal static Func<string, bool> Is = name => Capabilities.Is(name);

    internal static bool StepsOn() => Is(Taming.Capability);

    internal static void Require(string name)
    {
        Rows.Ensure();
        if (!Is(name)) throw ServiceCaps.Unavailable(name, Capabilities.Get(name).ReasonText);
    }

    /// <summary>What a definition's steps need: taming.steps, items.costs.exact (feed steps, and any refund of them),
    /// items.costs.wear (tool steps), taming.native (native steps), pets.records (a definition that names a pet).</summary>
    internal static void RequireFor(TamingDefinition d)
    {
        Require(Taming.Capability);
        if (d.Steps.Any(s => s is FeedStep)) Require(ItemCosts.StacksCapability);
        if (d.Steps.Any(s => s is UseToolStep)) Require(ItemCosts.WearCapability);
        if (d.Steps.Any(s => s is NativeStep)) Require(NativeTaming.Capability);
        if (d.Pet is not null) Require("pets.records");
    }

    private static void Report()
    {
        ServiceCaps.Report(Service, CapabilityState.Available);
        // attempts run on the server: on while a server world runs, waiting for one otherwise (a joined client never has one)
        ServiceCaps.Session(Taming.Capability, Lifecycle.IsServerRunning, "no server world runs here: taming runs on the server");
        // the game's taming is a sigdb feature the core reports; Kiln.Taming reads an animal's taming setup through
        // kiln.creatures (the Wildlife template by reflection path), so without that service it is lowered
        ServiceCaps.LowerFeatures("kiln.creatures", NativeTaming.Capability);
        ServiceCaps.Report(NativeTaming.BaitCapability, CapabilityState.Unavailable, CapabilityReason.Policy, NativeTaming.BaitUnproven);
        ServiceCaps.Report(NativeObserve, CapabilityState.Unavailable, CapabilityReason.Policy,
            "not proven: the game records no player for a taming step; a lab with a signed-in player must show where the petting player is read");
    }
}

/// <summary>Started by Kiln at its first service tick: the rows, and the native taming watch.</summary>
internal static class TamingStartup
{
    private static IDisposable? _tick;
    private static long _generation = -1;

    static TamingStartup()
    {
        TamingCaps.Rows.Ensure();
        Start();
    }

    internal static void Start()
    {
        if (_tick is not null && _generation == ServiceTick.Generation) return;
        _generation = ServiceTick.Generation;
        _tick = ServiceTick.Add("kiln.taming", (in TickInfo t) =>
        {
            GameTamingWorld.Advance(in t);
            TamingRuntime.Tick(t.Step);
        });
    }
}
