// SPDX-License-Identifier: MIT
// What an action speed applies to (Kiln.Combat's ActionSpeed; mod API 0.2.28). The numbers are kiln.combat.speed's
// (KILN_ACTION_*, native/core/abi/services/kiln_action_speed.h) and the kiln:speed wire's: append only. It lives in
// Kiln.Api so that Kiln.Client (GameInputSample.HeldKind) and Kiln.Combat (ActionSpeed) both see it without referencing
// each other; Kiln.Combat forwards it (ActionSpeed.cs), so a mod built against the preview SDK, where it sat in
// Kiln.Combat, still binds.
namespace Kiln;

/// <summary>What an action speed applies to (<c>Kiln.ActionSpeed</c>, in Kiln.Combat), and the kind of a held item
/// (<c>GameInputSample.HeldKind</c>, in Kiln.Client). The routes (the game's SequenceRuntimeIds,
/// graph states and overlays) come from Kiln's own table; an action outside every route keeps the game's timing.</summary>
public enum ActionKind
{
    /// <summary>Melee light and heavy attacks, their combos and recovery.</summary>
    Melee = 0,

    /// <summary>Bow draw (charge) and fire/recovery durations and the shot animation. Staffs and wands are not included.</summary>
    Bow = 1,

    /// <summary>Tool swings: axe, pickaxe and the like.</summary>
    Tool = 2,
}
