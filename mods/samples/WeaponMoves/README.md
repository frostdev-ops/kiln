# WeaponMoves (sample)

A sword with moves of its own and a held model with moving parts. Read in this order:

1. `WeaponMovesMod.cs` (or `blocks/weapon.json`, the same in data): the sword, its `Moves` (a chain of two swings, a heavy, a block) and its look's
   `Animated`.
2. `make_assets.py`: writes `assets/src/*.gltf`, the sample's own rig and motions and the sword; no game data. Run it again after a change.
3. `assets/cook.json`: retargets the motions onto the player's skeleton, makes the second swing's arm from the first (`"from"`, `"mirror"`,
   `"speed"`), and makes each clip a state plays from the game's clip of the state it is placed like (`"from"`, with `"trim"` and `"speed"`
   to the move's timing) with the sample's sword arm on it (`"blend"` with `"bones"`). It also cooks the sword's skeleton, clip, graph and
   models. `kiln cook` runs it on each player's machine and writes `assets/cooked/` (never committed).
4. `assets/player-states.json`: one state of the mod per clip, placed like the game's sword attacks and block (`slash-1` like
   `Attack_Sword_Shield_01`, `slash-2` like `Attack_Sword_Shield_02`, `cleave` like the sword machine's `Attack_Heavy`, `guard` like
   `Block_FullBody`). The cook checks it after its own steps and prints one line per state; `python3 tools/animcook/animcook.py player-states
   mods/samples/WeaponMoves/assets/player-states.json` checks it alone.

**What it does:** at boot kiln.playeranim adds the four states to the player's graph, on the server and on every player's game that has the
mod (`kiln.mod.json` says `"side": "both"` and `"clients": "mod"`, which player states need). The moves play the sample's own clips in them,
with the sample's timing (`hitAt`, `length`). A change to the states applies from the next start. The held model turns and sways in the hand.

**What the moves look like:** each clip is the game's own clip of the state it is placed like (its body, shield arm, grip, step forward and
events), with the sword arm (upper arm, forearm, hand: joints j70 to j74) taken from the sample's motion. The game clip is played at the
speed that puts its blow on the move's `hitAt` and cut at the move's `length`, so the body's blow and the arm's land together:

- **slash-1** (on `Attack_Sword_Shield_01`, played at 0.52 of its speed, 1 s): the forehand, the sword from high on the right down across
  to the left, the blow at 0.42 s.
- **slash-2** (on `Attack_Sword_Shield_02`, at 0.59, 0.87 s): the backhand, the sword from across the body out to the right, the blow at
  0.37 s. Its arm is the first swing's mirrored: `Slash` writes it on the left arm.
- **cleave** (on the sword machine's `Attack_Heavy`, at 1.09, 1.6 s): the arm up over the head and down in front, the blow at 0.9 s.
- **guard** (on `Block_FullBody`, its 6.65 s loop): the sword held across in front, giving a little five times a loop, over the game's
  block (body, shield and walking legs).

The arm's poses are written relative to the chest, so the arm turns with the game's body. `mods/samples/check_player_clips.py` decodes the
cooked clips and checks that every joint outside the sword arm is the game clip's, that the arm is the sample's, and that no joint the game
clip moves is left at the skeleton's bind pose. The hand and the arm's twist joints (j71, j73) hold their bind pose: the rig moves the upper
arm and the forearm only.

**Cooking an imported copy:** the release zip holds `assets/src/` and the recipe; the rest is read from the installed game. With Python and
the Kiln source (`KILN_REPO`): `kiln cook <the mod's folder>` (the folder with `kiln.mod.json`), which runs
`python3 "$KILN_REPO/tools/modelcook/modelcook.py" recipe assets/cook.json --update-manifest` there. A dedicated server cooks the clips and
the states the same way with `--game <the server's folder>`; its model steps are skipped there, one line each: only a game client draws them.

An earlier form of this sample has been seen in a game client: the moves played after a join check that passed, with the body in the
skeleton's bind pose outside the arm. This form, on the game's clips, has not been seen in a client yet.

**Follow-ups:** the Parrying Blade (C# only) is the same blade with two routes on its block: a parried blow leads to the weapon's own parry,
and the light attack while blocking to its first swing. A weapon with follow-ups applies all its moves at once or none; the sample logs what
`WeaponMovesApplicationChangedEvent` says. The capability `blocks.move_followups` is switched off until its game test has passed, and the
sample leaves the Parrying Blade out while it is (`"preview": ["blocks.move_followups"]` in `kiln.json` switches it on to try it). Not seen
in a game client.

Docs: `docs/kiln/modding/item-blocks.md` ("Moves", "Follow-ups", "Parts"), `docs/kiln/modding/animations.md` ("`clips`", "Retargeting", "Player states").
