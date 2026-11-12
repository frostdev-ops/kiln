# WeaponMoves (sample)

A sword with moves of its own and a held model with moving parts. Read in this order:

1. `WeaponMovesMod.cs` (or `blocks/weapon.json`, the same in data): the sword, its `Moves` (a chain of two swings, a heavy, a block) and its look's
   `Animated`.
2. `make_assets.py`: writes `assets/src/*.gltf`, the sample's own rig and motions and the sword; no game data. Run it again after a change.
3. `assets/cook.json`: retargets the motions onto the player's skeleton, makes the second swing from the first (`"from"`, `"mirror"`, `"speed"`),
   and cooks the sword's skeleton, clip, graph and models. `kiln cook` runs it on each player's machine and writes `assets/cooked/` (never committed).
4. `assets/player-states.json`: one state of the mod per clip, placed like the game's sword attacks and block.

**Today:** nothing reads `assets/player-states.json` in this build. When content is ready each move's state name is reported ("player states are
not available in this build") and each move plays its `like` sequence's own animation, with the sample's timing (`hitAt`, `length`). The held
model turns and sways in the hand either way. **Once the `player-anim` package is in the build**, the moves play the sample's own clips.

Docs: `docs/kiln/modding/item-blocks.md` ("Moves", "Parts"), `docs/kiln/modding/animations.md` ("`clips`", "Retargeting").
