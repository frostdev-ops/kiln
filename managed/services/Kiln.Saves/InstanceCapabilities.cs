// SPDX-License-Identifier: MIT
// InstanceData in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The row
// saves.instance_data says what InstanceData.Use enforces: unavailable with the reason of the first thing it needs that
// is off (kiln.saves, then the item source: items.instances or kiln.spawn), pending while the world's data is not open
// (a hosted world before the game's load, and after a hosted world closed), available otherwise. Refreshed at Kiln.Saves'
// start, in its pump (about once a second) and at the state changes; reported only when it changed. Never in a getter,
// never in Use, which keeps throwing as it does. saves.instance_data.carried is unavailable, by policy.
using System.Runtime.CompilerServices;

namespace Kiln;

internal static class InstanceCapabilities
{
    internal const string Name = InstanceStore.Capability;
    internal const string Carried = InstanceData.CarriedCapability;

    /// <summary>What Use throws while the world's data is not open, and the pending row's text.</summary>
    internal const string WaitingText = "the hosted world's data opens when the game loads it (Saves.WorldDataReady)";

    private static readonly object Gate = new();
    private static (CapabilityState State, CapabilityReason Reason, string Text)? _shown;
    private static bool _carriedShown, _warned;

    /// <summary>The row as InstanceData.Use decides it now; null when it cannot be told on this thread right now (the
    /// row stays as it was).</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text)? Now()
    {
        if (!SavesRuntime.Ensure())
        {
            var saves = Capabilities.Get(SavesNative.Service);
            return saves is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown }
                ? (CapabilityState.Unavailable, saves.Reason, saves.ReasonText.Length > 0 ? saves.ReasonText : "kiln.saves is not loaded")
                : (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.saves is not loaded");
        }
        try
        {
            SavesRuntime.Store.Instances.RequireWorld();
        }
        catch (KilnUnavailableException e)
        {
            // RequireWorld wraps the item source's own exception; none inside: there is no item source at all
            var inner = e.InnerException as KilnUnavailableException;
            var needs = inner?.Capability ?? Items.Caps.Instances;
            var why = inner?.Reason ?? e.Reason ?? "";
            return (CapabilityState.Unavailable, ReasonOf(needs, inner is null), $"needs {needs}: {why}");
        }
        catch (InvalidOperationException)
        {
            return null;  // the item source cannot answer on this thread now
        }
        return SavesRuntime.WorldDataReady
            ? (CapabilityState.Available, CapabilityReason.None, "")
            : (CapabilityState.Pending, CapabilityReason.Session, WaitingText);
    }

    /// <summary>The reason code of what is off: its own row's when that row is unavailable, else one that fits.</summary>
    private static CapabilityReason ReasonOf(string capability, bool noSource)
    {
        if (capability == Items.Caps.Instances) RunItemsStart();  // Kiln.Items reports its rows at its start
        if (Capabilities.Get(capability) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } row) return row.Reason;
        if (noSource) return CapabilityReason.ServiceMissing;
        // kiln.<service>: its table is here but lacks what is needed; a feature row that says available: it stopped
        return capability.StartsWith("kiln.", StringComparison.Ordinal) ? CapabilityReason.ServiceOlder : CapabilityReason.Faulted;
    }

    private static void RunItemsStart()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(Items).TypeHandle);
        }
        catch (Exception e) when (e is TypeInitializationException or FileNotFoundException or FileLoadException)
        {
            // Kiln.Items cannot start: its row is not there; the fitting code stands in
        }
    }

    /// <summary>Reports the row when it changed. Any thread; a refused report is logged once and never thrown.</summary>
    internal static void Refresh()
    {
        try
        {
            var now = Now();
            if (now is not { } row) return;
            lock (Gate)
            {
                if (_shown == row) return;
                _shown = row;  // also when the report is refused: once, not every pump
                Report(Name, row.State, row.Reason, row.Text);
            }
        }
        catch (Exception e)
        {
            // never fail Kiln.Saves' start (a type load) or its pump for a row
            Warn($"{Name} could not be decided: {e.Message}");
        }
    }

    /// <summary>saves.instance_data.carried: unavailable by policy. Once.</summary>
    internal static void ReportCarried()
    {
        lock (Gate)
        {
            if (_carriedShown) return;
            _carriedShown = true;
            Report(Carried, CapabilityState.Unavailable, CapabilityReason.Policy, InstanceData.CarriedReason);
        }
    }

    /// <summary>The test kit's session end: the next session reports its row again, whatever it is.</summary>
    internal static void Forget()
    {
        lock (Gate) _shown = null;
    }

    private static void Report(string name, CapabilityState state, CapabilityReason reason, string text)
    {
        try
        {
            Capabilities.Report(name, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Warn($"{name} was not reported: {e.Message}");
        }
    }

    private static void Warn(string text)
    {
        if (_warned) return;
        _warned = true;
        SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: capabilities: {text}");
    }
}
