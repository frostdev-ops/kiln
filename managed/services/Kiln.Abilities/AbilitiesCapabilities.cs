// SPDX-License-Identifier: MIT
// Kiln.Abilities in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The rows are
// decided once, when the service starts (the first use of Abilities): what kiln.abilities resolved at its init and
// whether kiln.spawn and kiln.players are here does not change in a process.
namespace Kiln;

internal static class AbilitiesCaps
{
    /// <summary>Managed, static: buffs applied and removed through the game's natives (<see cref="Abilities.ApplyBuff"/>,
    /// <see cref="Abilities.RemoveBuff(EntityId, Guid)"/>). Needs kiln.abilities with its natives, programs and instance
    /// hooks, and kiln.spawn.</summary>
    public const string Buffs = "abilities.buffs";

    /// <summary>Managed, static: an entity's buffs read (<see cref="Abilities.BuffsOf"/>). Needs kiln.players and its
    /// reflection reads (<c>players.reflection</c>).</summary>
    public const string BuffList = "abilities.buff_list";

    private static int _reported;

    /// <summary>Reports the rows once. Never throws: a refused report is logged.</summary>
    internal static void Ensure()
    {
        if (Interlocked.Exchange(ref _reported, 1) == 1) return;
        try
        {
            var (state, reason, text) = DecideBuffs(Status(), Row("kiln.abilities"), Row("abilities.natives"),
                Row("abilities.programs"), Row("abilities.instances"), Spawn.Available, Row("kiln.spawn"));
            Capabilities.Report(Buffs, state, reason, text);
            (state, reason, text) = DecideList(PlayerStates.Available, Row("kiln.players"), Row("players.reflection"));
            Capabilities.Report(BuffList, state, reason, text);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or TypeInitializationException
                                      or FileNotFoundException or FileLoadException)
        {
            ModLog.Warn($"kiln.abilities: its capability rows were not reported: {e.Message}");
        }
    }

    /// <summary>Throws <see cref="KilnUnavailableException"/> naming <paramref name="name"/> when its row is off.</summary>
    internal static void Require(string name)
    {
        Ensure();
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

    /// <summary>The <c>abilities.buffs</c> row from what kiln.abilities resolved (null: no table) and the rows it needs.
    /// Pure, so every branch is tested without the native table.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideBuffs(
        (bool Natives, bool Programs, bool Instances)? status, Capability? service, Capability? natives, Capability? programs,
        Capability? instances, bool spawn, Capability? spawnRow)
    {
        if (status is not { } s)
            return Off(service, CapabilityReason.ServiceMissing, "kiln.abilities is not loaded");
        if (!s.Natives) return Off(natives, CapabilityReason.Faulted, "kiln.abilities has no natives on this build", "abilities.natives");
        if (!s.Programs) return Off(programs, CapabilityReason.Faulted, "kiln.abilities has no programs on this build", "abilities.programs");
        if (!s.Instances) return Off(instances, CapabilityReason.Faulted, "kiln.abilities has no instance hooks on this build", "abilities.instances");
        if (!spawn) return Off(spawnRow, CapabilityReason.ServiceMissing, "kiln.spawn is not loaded", "kiln.spawn");
        return (CapabilityState.Available, CapabilityReason.None, "");
    }

    /// <summary>The <c>abilities.buff_list</c> row. Pure.</summary>
    internal static (CapabilityState State, CapabilityReason Reason, string Text) DecideList(bool players, Capability? service,
        Capability? reflection)
    {
        if (!players) return Off(service, CapabilityReason.ServiceMissing, "kiln.players is not loaded", "kiln.players");
        if (reflection is { State: CapabilityState.Unavailable })
            return Off(reflection, CapabilityReason.SymbolMissing, "", "players.reflection");
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
