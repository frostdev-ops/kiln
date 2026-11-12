# kiln.playeranim / PlayerAnimStates: mods' states in the player's animation graph

The player's animation graph is one game resource for every player, so two mods cannot each override it.
kiln.playeranim reads it once at boot, adds every enabled mod's `assets/player-states.json` beside the game states
they name (`like`), and serves the result through kiln.assets before the game reads it, for the whole process. The
modder's guide is [modding/animations.md](../modding/animations.md), "Player states"; evidence:
[docs/re/models.md](../../re/models.md) 7.5.

**Status (dev.26):** works on a test server (a dedicated lab server; a fake client for the join check). Not run in a
real game client.

| Layer | Where |
|---|---|
| C table | `native/core/abi/services/kiln_playeranim.h` (`kiln.playeranim` v1) |
| Native | `native/services/playeranim/` → `kiln/services/kiln-playeranim.dll`. `merge.cpp`, `states.cpp`, `blob.cpp`, `typedb.cpp` are portable; host tests `tests/run.sh` |
| Reference | `tools/animcook/anicook/playergraph.py`, `playerstates.py`: the same bytes for the same inputs (the host tests compare them) |
| Managed | `managed/services/Kiln.PlayerAnim/` → `kiln/host/Kiln.PlayerAnim.dll` (`PlayerAnimStates`) |
| Capability | `anim.player_states`, reported by the native service before the mods load and never pending, so a manifest's `features` may require it ([caps.md](caps.md)) |
| Needs | kiln.assets (`when_resources_ready`, resource reads and puts) and kiln.types (the game's type table); without either the merge is off |
| Join | `Kiln.Network`'s player-graph check ([sync.md](../sync.md), "Limits and open points") |
| Test | `mods/tests/player-anim-test/` (`run.py` drives a lab server through its scenarios); sample `mods/samples/PlayerStates/` |

## When
- `on_ready`: read `assets/player-states.json` and `kiln.mod.json` of every enabled mod folder. No state anywhere:
  stop. Nothing is read from the game, nothing is put, the state is `untouched`.
- When the game's resource service comes up (kiln.assets' `when_resources_ready`, on the game's thread, before its
  first read): read the player's graph and the clips' facts through kiln.assets, the type layouts from the game's
  reflection table (kiln.types), merge, and put the result once, pinned for the process.
- Never again. A player graph put while a player's graph instance is live ends the game. A mod with player states that
  is unloaded or reloaded keeps its states and their clips until the next start; kiln.log says so.

## It fails closed
- A wrong definition refuses that state, with its reason; the others load.
- A merge that cannot be proven on the installed game (the game's graph does not re-encode to its own bytes, a field the
  merge writes is gone, the merged graph fails its checks, kiln.assets refuses it) serves nothing: the game's graph
  stays, the state is `off`, every state is unavailable with the reason, and `anim.player_states` is unavailable.
- A mod's player states load only when its `kiln.mod.json` states `"side": "both"` and `"clients": "mod"`.
- The player's graph is reserved: no other put of its key goes through ([assets.md](assets.md), "Limits").

## C#
```csharp
string hash = PlayerAnimStates.GraphHash;                         // 16 hex digits; zeros for the game's own graph
PlayerAnimGraphState g = PlayerAnimStates.Graph;                  // Off, Waiting, Untouched, Merged
PlayerAnimStateInfo? s = PlayerAnimStates.Find("my-mod", "cinder-slash-1");   // status Live, Refused, Pending, Unavailable
IReadOnlyList<PlayerAnimGameState> game = PlayerAnimStates.FindGameStates("Attack_Sword_Shield_01");
```
Any thread. `Available` says whether the service is here. `PlayerAnimStates.Define` (definitions from code) is not built
in this release: it returns `Refused` with a reason and keeps nothing; write the state in `assets/player-states.json`.

## kiln.log
```
playeranim: 3 state(s) of 1 mod(s) merged into the player's graph: 3 machine(s), 437260 -> 438620 bytes, 67 ms (types 4.7, clip facts 0.1, merge 62.4, put 0.1), hash b92020749983197c
playeranim: no player state could be merged (1 refused): the game's graph is untouched
playeranim: mod <id> was reloaded: its player states stay as they were at this start; a change to them applies from the next start
```
One warning line per refused state.

## Files
- `kiln/config/playeranim.json`: `"enabled"` (default true). `"testMissingField"` is a lab switch.
- `kiln/data/playeranim/status.json`: the state, the hash, the capability row, every state with its status and reason,
  the machines touched, the bytes before and after, the times in milliseconds.

## Limits
- No hot reload, on purpose (above).
- A state whose clip is not served (the mod's clips not cooked on this machine) is refused. A dedicated server that has
  not cooked a mod's clips therefore merges fewer states than a client that has, the graph hashes differ, and the join
  check refuses that client: a mod with player states needs its cook on the server too.
- Definitions from code are not built (above).
- The neighbours a state may be `like` are a fixed list (the modder's guide has it); anything else is refused by name.
- Cost: the merge takes 50 to 90 ms of a boot when a mod has states (lab, a loaded machine: mean 80 ms, worst 167 ms
  over ten boots), nothing when none has.
