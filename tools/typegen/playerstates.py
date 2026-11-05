#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Generates Kiln.Players' state list from the game's reflection, read offline from the exe.

    python3 tools/typegen/playerstates.py --exe <enshrouded.exe> managed/services/Kiln.Players/PlayerStateIds.g.cs
    python3 tools/typegen/playerstates.py --exe <enshrouded.exe> --table          # the Markdown table of every state
    python3 tools/typegen/playerstates.py --exe <enshrouded.exe> --actions        # the table of client actions
    python3 tools/typegen/playerstates.py --exe <exe> --check PlayerStateIds.g.cs # what a game update added or moved

Every state Kiln can name comes from one of the enums below; nothing is hand-listed except friendly aliases and the
notes in the table. At run time kiln.playerstate reads the same enums from the running game, so a state a later build
adds is still reported, under its raw id and the game's own name.
"""
import argparse
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "kfc"))
os.environ.setdefault("KILN_OUT", str(Path(__file__).resolve().parents[2] / ".out" / "typegen"))
from kfc.refl import TypeDb  # noqa: E402

# id, C# name of the source, prefix of its states, game enum, field, one enum value at a time or a bitmask
SOURCES = [
    (1, "Actor", "", "keen::actor::StateFlag", "Actor.currentState", "mask"),
    (2, "Locomotion", "Locomotion", "keen::ecs::LocomotionState", "DynamicLocomotion.state", "enum"),
    (3, "Player", "", "keen::ecs::PlayerFlags", "PlayerState.playerStateMask", "mask"),
    (4, "Motion", "Motion", "keen::ecs::LocomotionFlags", "DynamicLocomotion.flags", "mask"),
    (5, "ClientUi", "ClientUi", "keen::ecs::ClientPlayerFlags", "PlayerInput.fromClient.mask", "mask"),
    (6, "Ability", "Ability", "keen::actor::AbilityFlag", "Actor.currentAbilityMask", "mask"),
    (7, "Input", "Input", "keen::ecs::PlayerInputType", "PlayerInput.fromClient.digitalInput", "mask"),
    (8, "Action", "Action", "keen::actor::ActionSequenceTrigger", "Actor.currentAction.triggerContext.triggerType", "enum"),
    (9, "Jump", "Jump", "keen::actor::JumpType", "DynamicLocomotion.jumpType", "enum"),
    (10, "Speed", "Speed", "keen::ecs::LocomotionSpeedSelector", "ActorInput.lastLocomotionSpeed", "enum"),
    (11, "Collider", "Collider", "keen::ecs::ColliderMode", "Actor.currentColliderMode", "enum"),
    (12, "Climb", "Climb", "keen::ecs::ClimbState", "Climb.climbState", "enum"),
    (13, "ClimbType", "ClimbType", "keen::ecs::ClimbType", "Climb.climbType", "enum"),
    (14, "Hook", "Hook", "keen::ecs::HookShotState", "HookShot.hookShotState", "enum"),
    (15, "HookType", "HookType", "keen::ecs::HookShotType", "HookShot.hookShotType", "enum"),
    (16, "Ranged", "Ranged", "keen::ranged_weapon::State", "NetworkRangedWeapon.currentState", "enum"),
    (17, "Fishing", "Fishing", "keen::fishing::State", "NetworkFishing.state", "enum"),
    (18, "CookingFlag", "CookingFlag", "keen::ecs::CookingFlags", "NetworkCookingData.mask", "mask"),
    (19, "Revive", "Revive", "keen::ecs::ReviveState", "NetworkPlayerAttributes.revivieData.state", "enum"),
    (20, "Fog", "Fog", "keen::FogType", "EnvironmentSensor.fogType", "enum"),
    (21, "Temperature", "Temperature", "keen::TemperatureVoxelType", "EnvironmentSensor.temperature", "enum"),
]
# Worked out by Kiln from the sources above (source 22); the game has no field for these.
DERIVED = [
    ("Airborne", "Not on the ground, and not climbing, swimming or hanging from a hook"),
    ("Attacking", "Running an action that swings or fires a weapon"),
    ("Casting", "Running an action that uses a spell, or a wand's own attack"),
    ("UsingItem", "Running an action with a used item that is not a weapon"),
    ("InBase", "Inside a flame altar's build zone (kiln.build's base list)"),
    ("Rested", "The Rested buff is on"),
    ("Fed", "At least one food buff is on"),
    ("HasDebuff", "At least one debuff is on"),
    ("Hungry", "The game's hunger marker is in a food slot: nothing eaten (program thirst_and_hunger)"),
]
ALIASES = {
    "Gliding": "HangGliding", "Underwater": "UnderWater", "Sneaking": "InSneakMode", "InShroud": "InFog",
    "InSafeZone": "InSaveZone", "Swimming": "LocomotionSwimming", "Diving": "LocomotionDiving",
    "Sliding": "LocomotionSliding", "ClimbingWall": "LocomotionClimbingWall", "ClimbingLadder": "LocomotionClimbingLadder",
    "Sprinting": "IsSprinting", "Grappling": "AttachedToHook",
}
# What a state means and what sets it, where it was read in the game's code or data. Left out = the game's name says it.
NOTES = {
    "Grounded": "On the ground. Program actor_grounded",
    "Jumping": "Rising from a jump. Program actor_jump, sequences",
    "Blocking": "Holding a block. Sequences; enemies: blocking_state_enemy",
    "Dodging": "In a dodge. Sequence Ability_Movement_Dodge and its variants",
    "ParryOportunity": "A parry window of the attacker's sequence (sequence data, the game's spelling)",
    "BlockOpportunity": "A block window (sequence data)",
    "Exhausted": "Out of stamina. Set in player_control_action_sequence",
    "Dead": "Program death; cleared when health is back",
    "Sheltered": "Under a roof. Program actor_shelter_state",
    "Sitting": "Sitting on a seat. player_control_action_sequence, sequences",
    "PlayingInstrument": "Sequences of the instruments",
    "Sleeping": "Sequence Interaction_Sleep",
    "Spawning": "Not ready yet after a spawn or respawn",
    "InFog": "Standing in the Shroud. Program fog_resistance_update",
    "InSaveZone": "Near the flame safe zone. Program player_update_insave_zone (host only)",
    "Mining": "Sequences of mining tools",
    "Warm": "Near warmth. Program warmth",
    "Wet": "Programs actor_wet_soaked_*",
    "Soaked": "Programs actor_wet_soaked_*",
    "Cold": "Program environmental_hazards",
    "Freezing": "Program environmental_hazards",
    "InSneakMode": "Crouching. actor_water_locomotion and sequences",
    "Unhappy": "Wildlife only (program wildlife); never set on players",
    "HasAggro": "Enemies only (post_enemy_combat_targets)",
    "InCombat": "Programs actor_in_combat, set_cluster_combat_state",
    "InRangeCombat": "Bow or staff out. Program server_player_ranged_weapon",
    "InRangeCombatPreparing": "Program server_player_ranged_weapon",
    "InRangeCombatShooting": "Program server_player_ranged_weapon",
    "HeavyAttack": "Sequences of heavy attacks",
    "Cooking": "At a cooking fire. Program player_cooking",
    "Falling": "Sequences; the locomotion state is the live one",
    "HeadDiving": "Sequence Ability_Movement_Jump_HeadDive",
    "Climbing": "On a wall or ladder",
    "HangGliding": "Gliding. Sequence Movement_Tool_HangGlider_Start and its variants",
    "AttachedToHook": "On a grappling hook line. Sequence Movement_Tool_HookPull_Self",
    "FloatingOnWater": "Swimming at the surface. Program actor_water_locomotion",
    "UnderWater": "Diving. Program actor_water_locomotion",
    "Suffocating": "Out of oxygen. Program oxygen_update",
    "Invisible": "Program actor_simulation_start, sequences",
    "IsNearBuildingZone": "The cursor's server flag: near a build zone. Program network_cursor",
    "IsReadyForGameplay": "Program actor_is_ready_for_gameplay",
    "Strafing": "Program strafing_state_player",
    "IsAnythingAbove": "Program check_line_above_sensor",
    "IsSequenceTriggerInputActive": "The input that started the sequence is still held. Program base_player_action",
    "IsChargingFishingRod": "Program server_player_fishing",
    "IsFishing": "Program server_player_fishing",
    "IsHoldingFish": "Program server_player_fishing",
    "IsSprinting": "Program player_sprint",
    "IsOutsideSafeZone": "The death-respawn safe zone timer runs",
    "InFullScreenUi": "A full screen menu is open (replicated from the client)",
    "IsSkyDiving": "Falling fast toward a head dive",
    "LocomotionHangGliding": "Derived by the game from HangGliding",
    "LocomotionFalling": "Derived by the game: not Grounded",
    "LocomotionSwimming": "Derived by the game from FloatingOnWater, and canSwim",
    "LocomotionDiving": "Derived by the game from UnderWater, and canDive",
}
SKIP_VALUES = {"Count"}
NONE_VALUES = {"Invalid", "None"}


def values(db, enum):
    t = db.get(enum)
    if t is None:
        raise SystemExit(f"{enum} is not in this exe's reflection")
    seen, out = set(), []
    for v in t["values"]:
        if v["name"] in SKIP_VALUES or v["value"] in seen:
            continue
        seen.add(v["value"])
        out.append((v["name"], v["value"]))
    return out


def states(db):
    """(C# name, id, game name, source row)"""
    rows = []
    taken = set(a for a in ALIASES)
    for src in SOURCES:
        sid, _, prefix, enum, _, kind = src
        for name, value in values(db, enum):
            if value > 0xFF:  # ActionSequenceTrigger.ClearAll is a command, never a state
                continue
            cs = prefix + re.sub(r"\W", "_", name)
            if cs in taken:
                cs = src[1] + cs
            taken.add(cs)
            rows.append((cs, (sid << 8) | value, name, src))
    return rows


def summary(cs, game, src):
    note = NOTES.get(cs)
    where = f"{src[3]}.{game} ({src[4]})"
    return f"{note}. {where}" if note else where


def generate(db, out):
    rows = states(db)
    p = ["// SPDX-License-Identifier: MIT",
         "// Generated by tools/typegen/playerstates.py from the game's reflection. Do not edit.",
         "// <auto-generated/>",
         "namespace Kiln;",
         "",
         "/// <summary>A state a player can be in. The value is <c>source &lt;&lt; 8 | the game's raw value</c>; a state a later",
         "/// game build adds has no name here but is still reported, as a value of this enum with its raw id",
         "/// (<see cref=\"PlayerStates.NameOf\"/> gives the game's name). Every value: docs/kiln/modding/player-states.md.</summary>",
         "public enum PlayerStateId : ushort",
         "{",
         "    /// <summary>No state.</summary>",
         "    None = 0,"]
    for cs, sid, game, src in rows:
        p.append(f"    /// <summary>{summary(cs, game, src)}</summary>")
        p.append(f"    {cs} = 0x{sid:04X},")
    p.append("")
    for i, (name, text) in enumerate(DERIVED):
        p.append(f"    /// <summary>{text}. Worked out by Kiln.</summary>")
        p.append(f"    {name} = 0x{(22 << 8) | i:04X},")
    p.append("")
    for alias, target in ALIASES.items():
        if target is None:
            continue
        p.append(f"    /// <summary>The same as <see cref=\"{target}\"/>.</summary>")
        p.append(f"    {alias} = {target},")
    p += ["}", "",
          "/// <summary>Where a state comes from. The game's own enum names its values.</summary>",
          "public enum PlayerStateSource : byte",
          "{",
          "    None = 0,"]
    for sid, name, _, enum, field, _ in SOURCES:
        p.append(f"    /// <summary><c>{enum}</c> in <c>{field}</c>.</summary>")
        p.append(f"    {name} = {sid},")
    p += ["    /// <summary>Worked out by Kiln from the other sources.</summary>", "    Derived = 22,", "}", "",
          "internal static class PlayerStateTable",
          "{",
          "    /// <summary>Each state this build of Kiln names, with the game's name of its value, so a game build that",
          "    /// renumbers an enum still maps to the same state.</summary>",
          "    internal static readonly (PlayerStateId Id, string GameName)[] Known =",
          "    ["]
    for cs, sid, game, src in rows:
        p.append(f"        (PlayerStateId.{cs}, \"{game}\"),")
    p += ["    ];", "", "    internal static readonly string[] DerivedNames =", "    ["]
    for name, _ in DERIVED:
        p.append(f"        \"{name}\",")
    p += ["    ];", "}", ""]
    Path(out).write_text("\n".join(p))
    print(f"{out}: {len(rows)} states from {len(SOURCES)} enums, {len(DERIVED)} derived")


def table(db):
    print("| State | Game name | Raw | Note |")
    print("|---|---|---|---|")
    last = None
    for cs, sid, game, src in states(db):
        if src[0] != last:
            print(f"| **{src[1]}** | `{src[3]}` in `{src[4]}` | {'bit' if src[5] == 'mask' else 'value'} | |")
            last = src[0]
        print(f"| `{cs}` | {game} | {sid & 0xFF} | {NOTES.get(cs, '')} |")
    print("| **Derived** | worked out by Kiln | | |")
    for i, (name, text) in enumerate(DERIVED):
        print(f"| `{name}` | | {i} | {text} |")


def client_actions(db):
    """The versioned actions a client sends: fields of the two input structs with a versionData field."""
    consumed = {f["name"] for f in db.all_fields(db.get("keen::ecs::ServerConsumedPlayerInput"))}
    special = {"adminSpawnCommand": "consumedAdminSpawnAction", "adminKillFaction": "consumedAdminKillFactionAction",
               "adminDebugEnemyAction": "consumedDebugEnemyAction"}
    rows = []
    for group in ("keen::ecs::ClientPlayerInputData", "keen::ecs::AdminClientPlayerInputData"):
        for f in db.all_fields(db.get(group)):
            t = db.resolve(db.get(f["type"]))
            if not t or "fields" not in t or not any(x["name"] == "versionData" for x in db.all_fields(t)):
                continue
            name = f["name"]
            rows.append((name, t["size"], special.get(name, "consumed" + name[0].upper() + name[1:]) in consumed))
    return rows


def action_table(db):
    print("| Action | Bytes | Can cancel |")
    print("|---|---|---|")
    for name, size, cancel in client_actions(db):
        print(f"| `{name}` | {size} | {'yes' if cancel else 'no'} |")


def check(db, generated):
    known = dict(re.findall(r"\(PlayerStateId\.(\w+), \"(\w+)\"\)", Path(generated).read_text()))
    now = {cs: game for cs, _, game, _ in states(db)}
    new = sorted(set(now) - set(known))
    gone = sorted(set(known) - set(now))
    print(f"{len(now)} states on this exe, {len(known)} in {generated}")
    print("new on this build (reported at run time by raw id):", new or "none")
    print("gone on this build (read as unknown at run time):", gone or "none")
    return 1 if gone else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--exe", required=True)
    ap.add_argument("--table", action="store_true")
    ap.add_argument("--actions", action="store_true")
    ap.add_argument("--check")
    ap.add_argument("out", nargs="?")
    a = ap.parse_args()
    db = TypeDb.from_exe(Path(a.exe))
    if a.table:
        table(db)
        return 0
    if a.actions:
        action_table(db)
        return 0
    if a.check:
        return check(db, a.check)
    if not a.out:
        ap.error("give an output file, --table or --check")
    generate(db, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main())
