# WeaponMoves (sample)

A sword with moves of its own and a held model with moving parts. Read in this order:

1. `WeaponMovesMod.cs` (or `blocks/weapon.json`, the same in data): the sword, its `Moves` (a chain of two swings, a heavy, a block) and its look's
   `Animated`.
2. `make_assets.py`: writes `assets/src/*.gltf`, the sample's own rig and motions and the sword; no game data. Run it again after a change.
3. `assets/cook.json`: retargets the motions onto the player's skeleton, makes the second swing from the first (`"from"`, `"mirror"`, `"speed"`),
   and cooks the sword's skeleton, clip, graph and models. `kiln cook` runs it on each player's machine and writes `assets/cooked/` (never committed).
4. `assets/player-states.json`: one state of the mod per clip, placed like the game's sword attacks and block (`slash-1` like
   `Attack_Sword_Shield_01`, `slash-2` like `Attack_Sword_Shield_02`, `cleave` like the sword machine's `Attack_Heavy`, `guard` like
   `Block_FullBody`). The cook checks it after its own steps and prints one line per state; `python3 tools/animcook/animcook.py player-states
   mods/samples/WeaponMoves/assets/player-states.json` checks it alone.

**What it does:** at boot kiln.playeranim adds the four states to the player's graph, on the server and on every player's game that has the
mod (`kiln.mod.json` says `"side": "both"` and `"clients": "mod"`, which player states need). The moves play the sample's own clips in them,
with the sample's timing (`hitAt`, `length`): the forehand, the mirrored backhand, the cleave, and the guard over the block's walk. A change to
the states applies from the next start. The held model turns and sways in the hand.

Not seen in a game client yet.

Docs: `docs/kiln/modding/item-blocks.md` ("Moves", "Parts"), `docs/kiln/modding/animations.md` ("`clips`", "Retargeting", "Player states").
