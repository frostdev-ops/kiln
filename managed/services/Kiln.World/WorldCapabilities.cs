// SPDX-License-Identifier: MIT
// Kiln.World in the capability registry (Kiln.Capabilities): world.tracked_writes, the row World.Write, World.Update,
// World.WriteField and World.WriteAt throw KilnUnavailableException for. The sigdb features (world.programs,
// world.component_bits, items.fuel) are rows by themselves; this one follows the write program through the session.
namespace Kiln;

internal static unsafe class WorldCapabilities
{
    private static IDisposable? _tick;
    private static int _started;
    private static (CapabilityState State, CapabilityReason Reason, string Text) _reported = ((CapabilityState)(-1), 0, "");

    /// <summary>The row's state; the first call reports it and starts following the session.</summary>
    public static bool Is(string capability)
    {
        Ensure();
        return Capabilities.Is(capability);
    }

    public static string Why(string capability)
    {
        Ensure();
        return Capabilities.Why(capability);
    }

    private static void Ensure()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            Refresh();
            // the write program waits for a server world, and a hosted game closes and opens worlds
            if (WorldWrites.T != null) _tick = ServiceTick.Add("kiln.world capabilities", (in TickInfo t) => { if (t.Step % 25 == 0) Refresh(); });
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Backend.Current.Log(0, LogLevel.Warn, $"kiln.world: its capability row was not reported: {e.Message}");
        }
    }

    /// <summary>Tests and the test kit: report again from the table that is there now.</summary>
    internal static void Reset()
    {
        _tick?.Dispose();
        _tick = null;
        _reported = ((CapabilityState)(-1), 0, "");
        Volatile.Write(ref _started, 0);
    }

    internal static void Refresh()
    {
        var now = Read();
        if (now == _reported) return;
        _reported = now;
        Capabilities.Report(World.TrackedWritesCapability, now.State, now.Reason, now.Text);
    }

    private static (CapabilityState State, CapabilityReason Reason, string Text) Read()
    {
        if (WorldNative.T == null)
            return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "kiln.world is not loaded");
        var t = WorldWrites.T;
        if (t == null)
            return Decide(WorldNative.T->Header.Version, false, false, default, "");
        WriteStatusNative s;
        s.Size = (uint)sizeof(WriteStatusNative);
        var answered = t->WriteStatus(&s) == WorldNative.Ok;
        var capable = (t->Capabilities() & WorldWritesAbi.CapTrackedWrites) != 0 && answered;
        return Decide(t->V1.Header.Version, true, capable, s, capable ? "" : Needs());
    }

    // The first row tracked writes need that is off, in its own words.
    private static string Needs()
    {
        foreach (var name in (string[])["world.programs", "items.fuel", "world.read_other", "world.names"])
            if (Capabilities.Get(name) is { State: CapabilityState.Unavailable, Reason: not CapabilityReason.Unknown } row)
                return $"needs {name}: {row.ReasonText}";
        return "the game's tracked write path did not resolve on this game build (see kiln.log)";
    }

    /// <summary>What <c>world.tracked_writes</c> becomes from what kiln.world has. Pure, so every branch is tested
    /// without the native table.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) Decide(uint tableVersion, bool v3, bool capable,
        WriteStatusNative status, string needs)
    {
        if (!v3) return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, $"kiln.world v{tableVersion} has no tracked writes (update Kiln)");
        if (!capable) return (CapabilityState.Unavailable, CapabilityReason.SymbolMissing, needs);
        if (status.Program == WorldWritesAbi.ProgramFaulted)
            return (CapabilityState.Unavailable, CapabilityReason.Faulted, "the write program faulted and is off until the server restarts (see kiln.log)");
        if (status.Declared == 0 && status.Refused == 0)
            return (CapabilityState.Available, CapabilityReason.None, "no loaded mod lists a component under \"writes\": every write answers NotDeclared");
        if (status.Program == WorldWritesAbi.ProgramActive) return (CapabilityState.Available, CapabilityReason.None, "");
        if (status.Program == WorldWritesAbi.ProgramPending)
            return (CapabilityState.Pending, CapabilityReason.Session, "the write program waits for the server world");
        return (CapabilityState.Unavailable, CapabilityReason.Faulted, "the game did not take the write program (see kiln.log)");
    }
}
