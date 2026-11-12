# Kiln player states (assets/player-states.json), format 1

<!-- Generated from schema/player-states.schema.json by kiln_manage::lint::docs (cargo test -p kiln-manage --test lint with KILN_LINT_WRITE=1). Do not edit. -->

States a mod adds to the player's animation graph, each placed like a state of the game's (its neighbour). Kiln merges every loaded mod's states into one override of the player's graph (tools/animcook/anicook/playergraph.py, the reference of the native merge). The mod id comes from the mod's kiln.mod.json, which must state "side": "both" and "clients": "mod". Strict JSON: no comments, no trailing commas, a key twice and an unknown key are errors. A wrong state is left out with its reason; the others load. Readers: tools/animcook/anicook/playerstates.py (Python json, duplicate keys refused).

- Files: `**/assets/player-states.json`
- JSON: comments **not allowed**, trailing commas **not allowed**
- Schema: [`schema/player-states.schema.json`](../../../../schema/player-states.schema.json); rules: [lint.md](../lint.md)

## Top level

| Key | Type | Required | Notes |
|---|---|---|---|
| `format` | `1` | yes |  |
| `states` | object | yes | The mod's states by name. A state's GUID is derive(mod, "state", name) (UUIDv5 of "<mod>:state:<name>" in Kiln.Blocks' namespace); a sequence enters it with SetAnimationEvent.animationName = that GUID. |

## state

| Key | Type | Required | Notes |
|---|---|---|---|
| `blendIn` | number |  | Seconds to blend into the state (its global transition and retriggerBlendDuration). Default: the neighbour's. min 0 max 5 |
| `clip` | string | yes | A clip name of this mod's cook (GUID derive(mod, "clip", name)), or a GUID written out (xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx: a clip the game or another mod serves). It has to be served, and made for the player's skeleton. min length 1 |
| `like` | string | yes | The neighbour: a state of the player's graph by its debug name, or Machine/State when several machines have the name. Entered by state id: any state of SM_Attack_Unarmed_FullBody, SM_Attack_Sword_Shield_FullBody, SM_Attack_Hammer_FullBody, SM_Attack_Greatsword_FullBody, SM_Attack_Dual_Daggers_FullBody, SM_Jump_Attack_FullBody, SM_Attack_Staff_FullBody, SM_Attack_Wand_FullBody; in SM_Main Block_FullBody, Parry_FullBody, DodgeRoll_FullBody, Dodge_Attack_FullBody, Sneak_Attack_FullBody, Attack_Skill_Kick_FullBody (a sequence enters such a state with SetAnimationEvent). Entered by overlay: a state of SM_Staff_Overlay, SM_Wand_Overlay, SM_Ground_Bow_Overlay, SM_Air_Bow_Overlay, SM_Ranged_Attack_Additive that an overlay of its own enters, not their DoNothing (a sequence enters such a state with SetAnimationOverlayEvent, overlayName = the state's GUID). A block pose of your own is like Block_FullBody. |
| `loop` | boolean |  | Default: the clip's own loop flag. |
| `speed` | number |  | Play rate of the clip (the clip node's raw speed multiplier). Default: the neighbour's (1 in the game's nodes). |

