// SPDX-License-Identifier: MIT
// Kiln.Abilities in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The rows are
// decided once, when the service starts (AbilitiesStartup at the first service tick, or the first use of Abilities): what kiln.abilities resolved at its init and
// whether kiln.spawn and kiln.players are here does not change in a process.
namespace Kiln;

internal static class AbilitiesCaps
{
    /// <summary>kiln.abilities' table is here (the core's row).</summary>
    public const string Service = "kiln.abilities";

    /// <summary>sigdb: action sequences played on an actor (<see cref="Abilities.PlaySequence(EntityId, Guid, SequenceOptions)"/>).</summary>
    public const string Sequences = "abilities.sequences";

    /// <summary>The sigdb features kiln.abilities serves.</summary>
    public static readonly string[] Features =
        ["abilities.attributes", "abilities.instances", "abilities.natives", "abilities.programs", Sequences];

    /// <summary>Managed, session: buffs removed (and applied) through the game's natives (<see cref="Abilities.ApplyBuff"/>,
    /// <see cref="Abilities.RemoveBuff(EntityId, Guid)"/>). Needs kiln.abilities with its natives, programs and instance
    /// hooks, and kiln.spawn with spawn components; pending until a server world has set up the game's registries.</summary>
    public const string Buffs = "abilities.buffs";

    /// <summary>Managed, static: an entity's buffs read (<see cref="Abilities.BuffsOf"/>). Needs kiln.playerstate and its
    /// reflection reads (<c>players.reflection</c>).</summary>
    public const string BuffList = "abilities.buff_list";

    // Reported at the service's start, again under a new registry binding (the test kit's), and every 25 steps (about
    // once a second): abilities.buffs follows the server world (its registries come with it; a client hosts after its
    // mods loaded). ServiceCaps writes a row only when it changed.
    internal static readonly ServiceCapRows Rows = new(Service, Report, everySteps: 25);

    /// <summary>Reports the rows when they were not reported under the current registry binding yet. Never throws.</summary>
    internal static void Ensure() => Rows.Ensure();

    private static void Report()
    {
        try
        {
            // sigdb may have resolved a feature that no service is here to serve
            if (Status() is null) ServiceCaps.LowerFeatures(Service, Features);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            ModLog.Warn($"kiln.abilities: its sigdb features were not lowered: {e.Message}");
        }
        RefreshBuffs();
        try
        {
            var (state, reason, text) = DecideList(PlayerStates.Status is { } st ? st.Reflection : null, Row("kiln.playerstate"), Row("players.reflection"));
            ServiceCaps.Report(BuffList, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeInitializationException
                                      or FileNotFoundException or FileLoadException)
        {
            ModLog.Warn($"kiln.abilities: {BuffList} was not reported: {e.Message}");
        }
    }

    private static void RefreshBuffs()
    {
        try
        {
            var now = DecideBuffs(Status(), Row("kiln.abilities"), Row("abilities.natives"), Row("abilities.programs"),
                Row("abilities.instances"), Spawn.Available, Spawn.Available && Spawn.SupportsComponents, Row("kiln.spawn"));
            ServiceCaps.Report(Buffs, now.State, now.Reason, now.Text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeInitializationException
                                      or FileNotFoundException or FileLoadException)
        {
            ModLog.Warn($"kiln.abilities: {Buffs} was not reported: {e.Message}");
        }
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="name"/> when its row is not available
    /// now (unavailable, or pending: no server world yet).</summary>
    internal static void Require(string name)
    {
        Ensure();
        if (name == Buffs) RefreshBuffs();  // the world may have come or gone since the last refresh
        var row = Capabilities.Get(name);
        if (row.State == CapabilityState.Available) return;
        throw new KilnUnavailableException(name, row.ReasonText.Length > 0 ? row.ReasonText : null);
    }

    private static Capability? Row(string name) => Capabilities.Get(name) is { Reason: not CapabilityReason.Unknown } r ? r : null;

    private static unsafe (bool Natives, bool Programs, bool Instances)? Status()
    {
        var t = AbilitiesNative.T;
        if (t == null) return null;
        AbilitiesNative.Status s = default;
        s.Size = (uint)sizeof(AbilitiesNative.Status);
        return t->GetStatus(&s) == AbilitiesNative.Ok ? (s.Natives != 0, s.Programs != 0, s.Instances != 0) : null;
    }

    internal const string WaitingText = "the game's native and program registries come with a server world (none yet)";

    /// <summary>The <c>abilities.buffs</c> row from what kiln.abilities says now (null: no table), the sigdb rows its
    /// features come from, and kiln.spawn. kiln.abilities' natives and programs are only "on" once a server world has set
    /// up the game's registries: with their sigdb features resolved, that is a pending row, not an unavailable one. Pure,
    /// so every branch is tested without the native table.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideBuffs(
        (bool Natives, bool Programs, bool Instances)? status, Capability? service, Capability? natives, Capability? programs,
        Capability? instances, bool spawn, bool spawnComponents, Capability? spawnRow)
    {
        if (status is not { } s)
            return Off(service, CapabilityReason.ServiceMissing, "kiln.abilities is not loaded");
        foreach (var (row, name) in new[] { (natives, "abilities.natives"), (programs, "abilities.programs"), (instances, "abilities.instances") })
            if (row is { State: CapabilityState.Unavailable })
                return Off(row, CapabilityReason.SymbolMissing, "", name);
        if (!s.Instances) return Off(instances, CapabilityReason.Faulted, "kiln.abilities has no instance hooks on this build", "abilities.instances");
        if (!spawn) return Off(spawnRow, CapabilityReason.ServiceMissing, "kiln.spawn is not loaded", "kiln.spawn");
        if (!spawnComponents)
            return (CapabilityState.Unavailable, CapabilityReason.ServiceOlder, "kiln.spawn takes no extra components (an older kiln-spawn.dll)");
        if (!s.Natives || !s.Programs) return (CapabilityState.Pending, CapabilityReason.Session, WaitingText);
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    /// <summary>The <c>abilities.buff_list</c> row: kiln.playerstate's reflection flag (null: no kiln.playerstate). Pure.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideList(bool? reflection, Capability? service,
        Capability? reflectionRow)
    {
        if (reflection is null) return Off(service, CapabilityReason.ServiceMissing, "kiln.playerstate is not loaded", "kiln.playerstate");
        if (reflection == false || reflectionRow is { State: CapabilityState.Unavailable })
            return Off(reflectionRow, CapabilityReason.Faulted, "kiln.playerstate read nothing from the game's reflection", "players.reflection");
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    // Off with the reason of the row it needs when that row is off, else the fallback.
    private static (CapabilityState, CapabilityReason, string) Off(Capability? needed, CapabilityReason fallback, string text,
        string? name = null)
    {
        if (needed is { State: CapabilityState.Unavailable } row)
            return (CapabilityState.Unavailable, row.Reason,
                name is null ? $"kiln.abilities is unavailable: {row.ReasonText}" : $"needs {name}: {row.ReasonText}");
        return (CapabilityState.Unavailable, fallback, name is null || text.Length > 0 ? text : $"needs {name}");
    }
}

/// <summary>Started by Kiln (ServiceTick.AutoStart) at its first service tick: abilities.buffs and abilities.buff_list
/// are there before a mod asks for them, not from Kiln.Abilities' first use.</summary>
internal static class AbilitiesStartup
{
    static AbilitiesStartup() => AbilitiesCaps.Ensure();
}
