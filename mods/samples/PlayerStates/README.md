# Player States sample

A mod that adds states of its own to the player's animation graph (`docs/kiln/modding/animations.md`, "Player states"): a
sword, the **Kiln Sample Riposte Sword** (crafted by hand from one twig), whose first light attack, heavy attack and block
play animations of this mod, and a staff cast of its own that the guided checks play.

## The files

| File | What it is |
|---|---|
| `kiln.mod.json` | The manifest. `"side": "both"` and `"clients": "mod"` are required: the server and every player's game merge the same states, and a player whose graph differs is refused at join. |
| `assets/player-states.json` | The four states. Each is placed **like** a state of the game's (`swing` like `Attack_Sword_Shield_01`, `heavy` like the sword machine's `Attack_Heavy`, `guard` like `Block_FullBody`, `cast` like the staff overlay's `Staff_Cast_F_Shoot`) and plays a clip: three of this mod's, one of the game's by GUID. |
| `make_clips.py` | Writes the three clips as glTF on the player's skeleton, into `assets/local/riposte.gltf`. The motions are written in the script; the skeleton and its rest pose come from your installed game, so the file is made on your machine and never committed or shipped. A real mod animates in Blender on the same skeleton (`kiln skeleton player player.gltf`). |
| `assets/cook.json` | The recipe: one `clips` step of `tools/animcook` on the player's skeleton (an input of the recipe, read from the game). |
| `assets/assets.json` | Written by the cook (`--update-manifest`): the cooked clips, served by kiln.assets. |
| `content/riposte.json` | The sword (a copy of the game's Bronze Sword) and its hand recipe. |
| `PlayerStatesMod.cs` | Makes copies of the game's sequences that name this mod's states (`SetAnimationEvent.animationName`, or `SetAnimationOverlayEvent.overlayName` for the cast) and puts them into the sword's slots once the game's data is ready. Holds the guided client checks. |

When the `weapon-moves` block is merged, a weapon's moves are written in its block instead of in code: this sample can then
move to it, and `PlayerStatesMod.cs` keeps only the checks.

## Cook, build, run

```sh
python3 mods/samples/PlayerStates/make_clips.py                     # assets/local/riposte.gltf, from your installed game
python3 tools/animcook/animcook.py recipe mods/samples/PlayerStates/assets/cook.json --update-manifest
python3 tools/animcook/animcook.py player-states mods/samples/PlayerStates/assets/player-states.json
dotnet build mods/samples/PlayerStates/PlayerStates.csproj -c Release -o <folder>/player-states-sample
```

The cook prints one line per state (`live, state in SM_Main (like Block_FullBody)`, ...) and fails if one is refused. Put the
built folder (with `assets/cooked/`) into `kiln/mods/` of the server and of every player's game.

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

Nothing of this sample has been seen in a game client. On a dedicated server, the lab test
`mods/tests/player-anim-test/run.py` shows the states entered by sequences on a spawned player character and their poses
following their clips; how they look, their blends and the hits landing in time are for the guided checks. The clips hold
the skeleton's rest pose outside the arms they move, which a real animation would not. On this build, kiln.gamedata may
refuse an `overlayName` in a sequence copy: the cast check then says so, and the copy plays the game's own shoot.
