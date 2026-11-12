# Player States sample

A mod that adds states of its own to the player's animation graph (`docs/kiln/modding/animations.md`, "Player states"): a
sword, the **Kiln Sample Riposte Sword** (crafted by hand from one twig), whose first light attack, heavy attack and block
play animations of this mod, and a staff cast of its own that the guided checks play.

## The files

| File | What it is |
|---|---|
| `kiln.mod.json` | The manifest. `"side": "both"` and `"clients": "mod"` are required: the server and every player's game merge the same states, and a player whose graph differs is refused at join. |
| `assets/player-states.json` | The four states. Each is placed **like** a state of the game's (`swing` like `Attack_Sword_Shield_01`, `heavy` like the sword machine's `Attack_Heavy`, `guard` like `Block_FullBody`, `cast` like the staff overlay's `Staff_Cast_F_Shoot`) and plays a clip: three of this mod's, one of the game's by GUID. |
| `assets/src/riposte.gltf` | The sample's own motion: a small rig of its own (20 joints, an A-pose like the player's skeleton at rest, no mesh) and three animations of its sword arm, `swing`, `heavy` and `guard`. No game data: it is committed and ships in the mod's zip. |
| `make_assets.py` | Writes `assets/src/riposte.gltf` (a dev tool, not shipped). The arm's poses are written in it as directions of the upper arm and forearm and a bend of the wrist. A real mod animates in Blender, on a rig like this one or on the player's skeleton itself (`kiln skeleton player player.gltf`, made from your installed game, so not shipped). |
| `assets/cook.json` | The recipe. Its first step retargets the three animations onto the player's skeleton; its second makes each clip a state plays from the game's clip of the state it is placed like (`"from"`) and puts the sample's sword arm on it (`"blend"` with `"bones"`: j70 to j74, the upper arm, the forearm and the hand). The skeleton and the three game clips are inputs of the recipe, read from the installed game. |
| `assets/assets.json` | Written by the cook (`--update-manifest`): the cooked clips with their root motion and events, served by kiln.assets (the three arm clips of the first step are in it too: small, and played by no state). |
| `content/riposte.json` | The sword (a copy of the game's Bronze Sword) and its hand recipe. |
| `PlayerStatesMod.cs` | Makes copies of the game's sequences that name this mod's states (`SetAnimationEvent.animationName`, or `SetAnimationOverlayEvent.overlayName` for the cast) and puts them into the sword's slots once the game's data is ready. Holds the guided client checks. |

When the `weapon-moves` block is merged, a weapon's moves are written in its block instead of in code: this sample can then
move to it, and `PlayerStatesMod.cs` keeps only the checks.

## What the moves look like

Each clip is the game's own clip of the state it is placed like, with the sword arm (upper arm, forearm, hand) replaced by the
sample's. The rest of the body, the shield arm, the fingers' grip, the root motion (the step forward) and the clip's events are the
game's, so the body moves as in the game's own move, and the clip keeps the game clip's length and timing: the blow lands when the
game's sequence lands it and the hand-back is where the game's is.

- **swing** (the first light attack, on the game's `Attack_Sword_Shield_01`, 1.667 s): the sword arm draws back low behind the
  right hip, sweeps across the front at the waist at the blow (0.217 s) and rises over the left shoulder, then comes back to the
  sword held low in front.
- **heavy** (the heavy attack, on the sword machine's `Attack_Heavy`, 2.567 s): the arm rises past the shoulder and up behind the
  head, chops down in front at the blow (0.983 s) and follows through low.
- **guard** (the block, on `Block_FullBody`, a 6.65 s loop): the sword arm raised, the forearm across above the head, the blade
  held flat, swaying a little five times a loop, over the game's block (body, shield and walking legs).

The arm's poses are written relative to the chest, so the arm turns with the game's body: in the game's stance the chest is turned
and leans forward, and so does the raised sword. Only a game client shows how it looks (the guided checks below).

## Cook, build, run

Someone who imports the release zip has everything the cook needs: `assets/src/riposte.gltf` ships in it, and the rest is read
from the installed game. The cook needs Python and the Kiln source (`KILN_REPO`), as every cooked mod does today. On the mod's
folder (the one with `kiln.mod.json`):

```sh
KILN_REPO=<a Kiln source checkout> kiln cook <the mod's folder>                             # a player's game
KILN_REPO=<a Kiln source checkout> kiln cook <the mod's folder> --game <the server's folder> # a dedicated server
```

`kiln cook` runs the recipe's tool; the same by hand, in the mod's folder:
`python3 "$KILN_REPO/tools/animcook/animcook.py" recipe assets/cook.json --update-manifest`.

From the repository:

```sh
python3 mods/samples/PlayerStates/make_assets.py                  # only after a change to the motion: assets/src/riposte.gltf
python3 tools/animcook/animcook.py recipe mods/samples/PlayerStates/assets/cook.json --update-manifest
dotnet build mods/samples/PlayerStates/PlayerStates.csproj -c Release -o <folder>/player-states-sample
python3 mods/samples/check_player_clips.py mods/samples/PlayerStates   # the cooked clips against the game's (below)
```

The cook prints one line per clip and one per state (`live, state in SM_Main (like Block_FullBody)`, ...) and fails if one is
refused. Put the built folder (with `assets/cooked/`) into `kiln/mods/` of the server and of every player's game. The server and
every player must cook from the same game version: the merged graph's hash is compared at join.

`mods/samples/check_player_clips.py` decodes the cooked clips and checks, per state, that every joint outside the sword arm is the
game clip's, that the arm is the sample's, and that no joint the game clip moves is left at the skeleton's bind pose.

## What the log says

kiln.log, at boot, from kiln.playeranim:

```
playeranim: 4 player state(s) of 1 mod(s) to merge when the game's resources come up
playeranim: 4 state(s) of 1 mod(s) merged into the player's graph: 3 machine(s), ... bytes, ... ms (...), hash <16 hex>
```

and from this mod, one line per state (`state swing: Live`), the sequences it copied, and per move how many of the sword's
slots now play it (`Kiln_Sample_RiposteSword: 1 slot(s) of Ability_Block play the state guard`). A refused state is a
warning with its reason; the others still load.

## Guided client checks

The mod registers seven checks (Kiln menu, guided checks; area "Player animations"): a state plays, the block pose, a
second player sees it, the game's own moves unchanged, a cast overlay, a player without Kiln on the server, and a client
with other player states refused. They need a player and have not been run.

## What is not proven

An earlier form of this sample has been seen in a game client: its states played on the Riposte Sword after a join check that
passed, with the body in the skeleton's bind pose outside the arm. This form, on the game's clips, has not been seen in a client yet. Checked without a client: the cooked clips against the
game's (above), the same bytes cooked from a dedicated server's files and from a client's. On a dedicated server, the lab test
`mods/tests/player-anim-test/run.py` (its own fixture mods, not this sample) shows states entered by sequences on a spawned player
character and their poses following their clips. How the arm looks on the game's body, the blends in and out, and the hits landing
in time are for the guided checks. The twist joints of the sword arm (j71, j73) hold their bind pose: the sample's rig has none.
On this build, kiln.gamedata may refuse an `overlayName` in a sequence copy: the cast check then says so, and the copy plays the
game's own shoot.
