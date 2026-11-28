// SPDX-License-Identifier: MIT
// Kiln.ActionSpeed (Kiln 0.2.0-dev.31.30, mod API 0.2.28): per-player speed of melee, bow and tool actions. The public
// surface is the one settled for the Combat And Attack Tweaks author (docs/kiln/services/combat.md, "Action speed");
// the state, the native table and the kiln:speed relay are ActionSpeedState.cs, ActionSpeedNative.cs and
// ActionSpeedRelay.cs.
using System;

namespace Kiln;

/// <summary>An effective action speed changed (<see cref="ActionSpeed.Changed"/>).</summary>
/// <param name="Player">The player whose value changed.</param>
/// <param name="Kind">The action kind.</param>
/// <param name="Multiplier">The new effective multiplier (every mod's value multiplied, clamped).</param>
public readonly record struct ActionSpeedChanged(EntityId Player, ActionKind Kind, double Multiplier);

/// <summary>Which parts of <see cref="ActionSpeed"/> work in this game. Each false part has its reason in
/// <see cref="Reason"/> (capabilities <c>combat.action_speed</c>, <c>combat.bow_timing</c>, <c>combat.melee_sampling</c>,
/// <c>combat.action_speed.prediction</c>).</summary>
/// <param name="Sequence">The action sequence clock is scaled (hit events, combos, recovery).</param>
/// <param name="Graph">The animation graph delta is scaled (visible and collision poses).</param>
/// <param name="BowTiming">Bow charge and fire durations are scaled.</param>
/// <param name="MeleeSampling">Melee contacts are sampled between frames above <see cref="ActionSpeed.MeleeSamplingAbove"/>.</param>
/// <param name="Prediction">A joined client applies the rates to its own actor's prediction and presentation.</param>
/// <param name="Reason">Why a part is off, or null.</param>
public sealed record ActionSpeedStatus(bool Sequence, bool Graph, bool BowTiming, bool MeleeSampling, bool Prediction,
                                       string? Reason);

/// <summary>Per-player speed multipliers for melee, bow and tool actions. Kiln scales the action sequence clock and the
/// animation graph delta together, divides a bow's fresh, skill-adjusted charge and fire durations, samples melee contacts
/// between frames above <see cref="MeleeSamplingAbove"/> (the game's own box colliders; contacts go into the original impact
/// before damage, so block and repeat-hit rules apply), and sends the effective rates to joined clients itself.
/// Each mod's values are its own; across mods they multiply, clamped to [<see cref="Min"/>, <see cref="Max"/>]; a mod's
/// values go when it unloads. At 1.0 everywhere nothing is scaled. Players only in this version.</summary>
/// <remarks>
/// <para>Since Kiln 0.2.0-dev.31.30 (mod API 0.2.28). Kiln provides the mechanism; which multipliers to use is the mod's
/// choice, and nothing changes in a game until a mod sets a value.</para>
/// <para>What is classified: a player's light and heavy melee attacks (a melee weapon's Primary and Heavy triggers),
/// tool swings, and bow shots (a ranged weapon's Primary trigger while the player's ranged weapon is a bow; staffs and
/// wands keep their timing). Blocks, dodges, spells, eating and everything else keep the game's timing. In this release
/// <see cref="ActionSpeedStatus.MeleeSampling"/> is false: Kiln samples nothing between frames, so at high melee rates a
/// hit lands where the game's own frames find it.</para>
/// <para>Where it applies: the server world (a dedicated server or a host) for every player; a joined client with Kiln
/// applies the server's rates to its own player's prediction and presentation (other players look as the server sends
/// them). A client on an older Kiln sees server-timed results.</para>
/// </remarks>
/// <example><code>
/// public override void OnLoad()
/// {
///     if (!ActionSpeed.Available) { Log.Warn($"action speed: {ActionSpeed.Status.Reason}"); return; }
///     if (Client.IsRemote) return;                 // the host or the server decides
///     ActionSpeed.SetDefault(ActionKind.Melee, 2.0); // every player, also later joins
///     ActionSpeed.SetDefault(ActionKind.Bow, 1.5);
/// }
/// </code></example>
public static class ActionSpeed
{
    /// <summary>The smallest effective multiplier.</summary>
    public const double Min = 0.5;

    /// <summary>The largest effective multiplier.</summary>
    public const double Max = 10.0;

    /// <summary>Kiln samples melee contacts between frames while a player's effective melee multiplier is above this.</summary>
    public const double MeleeSamplingAbove = 2.0;

    /// <summary>The sequence and graph scaling work in this game (server, host or joined client).</summary>
    [AnyThread]
    public static bool Available => ActionSpeedState.Available;

    /// <summary>Which parts work here, with the reason for any that don't.</summary>
    [AnyThread]
    public static ActionSpeedStatus Status => ActionSpeedState.ReadStatus();

    /// <summary>Sets this mod's multiplier for <paramref name="kind"/> for every player, now and later joins.
    /// Server or host only, tick thread.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is outside [<see cref="Min"/>,
    /// <see cref="Max"/>], NaN or infinite.</exception>
    /// <exception cref="InvalidOperationException">Called on a joined client.</exception>
    [TickThreadOnly]
    public static void SetDefault(ActionKind kind, double multiplier) =>
        ActionSpeedState.Write(nameof(SetDefault), kind, null, multiplier);

    /// <summary>Removes this mod's default for <paramref name="kind"/>. Server or host only, tick thread.</summary>
    /// <exception cref="InvalidOperationException">Called on a joined client.</exception>
    [TickThreadOnly]
    public static void ClearDefault(ActionKind kind) => ActionSpeedState.Write(nameof(ClearDefault), kind, null, null);

    /// <summary>Sets this mod's multiplier for one player; it overrides this mod's default for that player.
    /// Server or host only, tick thread.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="multiplier"/> is outside [<see cref="Min"/>,
    /// <see cref="Max"/>], NaN or infinite.</exception>
    /// <exception cref="InvalidOperationException">Called on a joined client.</exception>
    /// <exception cref="ArgumentException"><paramref name="player"/> is not a player entity (1 to 1023).</exception>
    [TickThreadOnly]
    public static void Set(EntityId player, ActionKind kind, double multiplier) =>
        ActionSpeedState.Write(nameof(Set), kind, player, multiplier);

    /// <summary>Removes this mod's value for one player and kind (its default applies again).</summary>
    /// <exception cref="InvalidOperationException">Called on a joined client.</exception>
    [TickThreadOnly]
    public static void Clear(EntityId player, ActionKind kind) => ActionSpeedState.Write(nameof(Clear), kind, player, null);

    /// <summary>Removes all of this mod's values for one player.</summary>
    /// <exception cref="InvalidOperationException">Called on a joined client.</exception>
    [TickThreadOnly]
    public static void Clear(EntityId player) => ActionSpeedState.ClearPlayer(player);

    /// <summary>The effective multiplier: every mod's value multiplied, clamped to [<see cref="Min"/>, <see cref="Max"/>].
    /// Readable on any side.</summary>
    /// <remarks>On a joined client it is the value the server sent (1.0 before it did, or from a server without it).
    /// Any thread.</remarks>
    [AnyThread]
    public static double Get(EntityId player, ActionKind kind) => ActionSpeedState.Get(player, kind);

    /// <summary>An effective value changed (any side).</summary>
    /// <remarks>Raised on the thread that changed it: the server's tick thread, or a joined client's main thread when the
    /// server's rates arrive. A handler goes when its mod unloads.</remarks>
    public static event Action<ActionSpeedChanged>? Changed
    {
        add => ActionSpeedState.AddHandler(value);
        remove => ActionSpeedState.RemoveHandler(value);
    }
}
