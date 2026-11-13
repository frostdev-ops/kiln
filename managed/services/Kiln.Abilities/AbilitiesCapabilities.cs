// SPDX-License-Identifier: MIT
// Kiln.Abilities in the capability registry (Kiln.Capabilities; native/core/abi/services/kiln_caps.h). The rows are
// decided once, when the service starts (the first use of Abilities): what kiln.abilities resolved at its init and
// whether kiln.spawn and kiln.players are here does not change in a process.
namespace Kiln;

internal static class AbilitiesCaps
{
    /// <summary>Managed, session: buffs removed (and applied) through the game's natives (<see cref="Abilities.ApplyBuff"/>,
    /// <see cref="Abilities.RemoveBuff(EntityId, Guid)"/>). Needs kiln.abilities with its natives, programs and instance
    /// hooks, and kiln.spawn with spawn components; pending until a server world has set up the game's registries.</summary>
    public const string Buffs = "abilities.buffs";

    /// <summary>Managed, static: an entity's buffs read (<see cref="Abilities.BuffsOf"/>). Needs kiln.playerstate and its
    /// reflection reads (<c>players.reflection</c>).</summary>
    public const string BuffList = "abilities.buff_list";

    private static int _listReported, _started;
    private static (CapabilityState State, CapabilityReason Reason, string Text)? _buffs;
    private static IDisposable? _tick;

    /// <summary>Reports the rows: buff_list once, buffs now and again on the service tick (about once a second) when it
    /// changes, because its registries come with the server world (a client hosts after its mods loaded). Never throws.</summary>
    internal static void Ensure()
    {
        RefreshBuffs();
        if (Interlocked.Exchange(ref _started, 1) == 0)
        {
            try
            {
                _tick = ServiceTick.Add("kiln.abilities capabilities", static (in TickInfo t) =>
                {
                    if (t.Step % 25 == 0) RefreshBuffs();
                });
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                ModLog.Warn($"kiln.abilities: no service tick for {Buffs}: {e.Message}");
            }
        }
        if (Interlocked.Exchange(ref _listReported, 1) == 1) return;
        try
        {
            var (state, reason, text) = DecideList(PlayerStates.Status is { } st ? st.Reflection : null, Row("kiln.playerstate"), Row("players.reflection"));
            Capabilities.Report(BuffList, state, reason, text);
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
            if (_buffs == now) return;
            _buffs = now;
            Capabilities.Report(Buffs, now.State, now.Reason, now.Text);
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
