# kiln.players: who is on the server

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_players.h` (`kiln.players` v1) |
| Native | `native/services/players/` -> `kiln/services/kiln-players.dll` |
| Symbols | `sigdb/symbols/server.d/players.json` (features `players.records`, `players.admin`, `players.singleton_map`) |
| Managed | `managed/services/Kiln.Players/` -> `kiln/host/Kiln.Players.dll` (static class `Kiln.Players`) |
| Test mod | `mods/tests/players-test/` |
| Evidence | `docs/re/players.md` |
| Progression | `kiln.progression` (level, experience, skill tree, skill points) is published by the same DLL: `docs/kiln/services/progression.md`; `Player.Progression` |
| State and actions | `kiln.playerstate` (gliding, swimming, health, buffs, eating, every action) is published by the same DLL: `docs/kiln/services/player-state.md`; `player.Is(...)`, `player.Snapshot()`, `PlayerStateChangedEvent`, `PlayerConsumeEvent` |

## What you get

`Player` (a record, as of the start of the current server-world step):

| Property | Meaning |
|---|---|
| `Entity` | the player's entity: **1..16** on this build (= slot + 1); stable while they stay, reused after they leave |
| `Name` | the platform name, as the chat and the game log show it |
| `SteamId` | SteamID64 (from the game log); 0 when unknown |
| `IsAdmin` | the game's own admin bit (it gates the admin client's actions). `IsAdminKnown` says whether it was readable |
| `Position` | `WorldPosition?` in metres (x east, y up, z north) |
| `Permissions` | the user group's rights: `KickBan`, `AccessInventories`, `EditBase`, `EditWorld`, `ExtendBase`, `ReceiveExp` (`Can(...)`) |
| `State` | `Joining`, `InGame`, `Leaving` |
| `Slot`, `UserGroupId`, `AccountId`, `Handle`, `JoinedStep` | raw game data (see `docs/re/players.md`) |
| `CharacterId` | the game's id of the character this player plays; per-character data: [character-data.md](character-data.md) |

For "is this player a server admin" use `Can(PlayerPermissions.KickBan)` (the `Admin` group of
`enshrouded_server.json` has it); `IsAdmin` is the game's separate admin-client bit.

## C#

Reference the assembly like any service (never ship it):
```xml
<ProjectReference Include="../../../managed/services/Kiln.Players/Kiln.Players.csproj" Private="false" ExcludeAssets="runtime" />
```

```csharp
using Kiln;

public sealed class Greeter : Mod
{
    public override void OnLoad()
    {
        if (!Players.Available) { Log.Warn("kiln.players is unavailable on this build"); return; }
        foreach (var p in Players.All) Log.Info($"already here: {p} at {p.Position}");
        Players.Joined += p => Log.Info($"{p.Name} joined (steam {p.SteamId}, admin rights: {p.Can(PlayerPermissions.KickBan)})");
        Players.Left += p => Log.Info($"{p.Name} left from {p.Position}");
    }
}
```

| Member | Threads | Notes |
|---|---|---|
| `Players.Available`, `Source` | any | `Source`: `Records` (full data), `GameLog` (fallback on an unknown build: names, Steam ids, join/leave, positions; no account, group or admin bit), `None` |
| `Players.All` / `Connected` / `Count` | any | in-game players / every connection (joining, leaving too) / in-game count |
| `Players.Find(string)` | any | exact name, then case-insensitive (ASCII); an in-game player wins |
| `Players.Find(EntityId)`, `FindBySteamId(ulong)`, `IsPlayer(EntityId)` | any | null / false when absent |
| `Players.PositionOf(EntityId)` | tick thread: live; other threads: last step's | null if not a player in game |
| `Players.Joined`, `Players.Left` (`Action<Player>`) | subscribe on the tick thread from your mod's code | raised on the tick thread at the start of the step after the change, inside your mod's tick: an exception counts as a fault of your mod. Dropped when your mod unloads. No `Joined` for players already in game when you subscribe |
| `Players.Diagnostics.Status`, `RawSlot`, `ProbePosition`, `ReplayLog` | see XML docs | for tests and dashboards |

Unavailable service: `All`, `Find`, ... and subscribing throw `NotSupportedException`; check `Players.Available`.

## Identity: `PlayerRef` from a player
Kiln.Players supplies the players for the identity types of [modding/contract.md](../modding/contract.md)
(`managed/services/Kiln.Players/PlayerIdentity.cs`). It is started by the first service tick, also when no loaded mod
references Kiln.Players, so these work **from the first tick, not in `OnLoad`**:
```csharp
PlayerRef me = (PlayerRef)player;                  // any thread; the snapshot's session (offline while joining or leaving)
PlayerRef who = PlayerRef.Of(entity);              // tick thread; None when the entity is not a player in game
PlayerRef now = who.Resolve();                     // tick thread; the same person with their current session
bool on = who.IsOnline;                            // tick thread; false for an earlier session
PlayerRef p2 = (PlayerRef)talentHolder;            // also from a TalentHolder (Kiln.Skills) and a QuestHolder (Kiln.Quests)
```
- The session entity carries a stamp made from the connection handle's generation and the step the player joined. A
  ref of an earlier session is not `IsOnline` after the slot is reused, also when the same person comes back, and
  `Resolve()` gives the new session. Use `IsSamePerson` to compare people across sessions.
- The holder conversions check the person: a holder whose entity now holds someone else gives the holder's offline
  ref, not the newcomer.
- With kiln.players off, `Of` and `Resolve` throw `KilnUnavailableException("identity.players")`, and `IsOnline`
  `identity.entities`. kiln.log at start: `kiln.players: identity.players and the players' entity source supplied`.
- Entities that are not players are answered by Kiln.Items' and Kiln.Spawn's sources ([modding/contract.md](../modding/contract.md)).
- Works on a test server, with simulated players (game-log replay, where the handle is 0 and the join step alone
  separates sessions). Not run with a real player: the handle generations of real joins are unchecked.

## Kick and ban (dev.27)
```csharp
KickResult r = await Players.Kick((PlayerRef)player, "Be nice.");     // server, tick thread
BanEntry b = Bans.Add((PlayerRef)player, "Griefing", TimeSpan.FromDays(7));  // by Steam id; kicks within a second if in game
Bans.Add(76561190000000000, "Cheating");                              // someone who is not here
Bans.Remove(76561190000000000);                                       // unban (by Steam id or account id)
foreach (var e in Bans.All) Log.Info($"{e.SteamId} {e.Name}: {e.Reason} until {e.Until}");
[Subscribe] void OnLogin(PlayerLoginEvent e) { if (!Allowed(e.SteamId)) { e.Reason = "Whitelist only."; e.Cancel(); } }
```
| Member | What |
|---|---|
| `Players.Kick(PlayerRef, reason)` | `Task<KickResult>` (`IKilnResult`): a Kiln client gets the reason (its overlay shows it) and 1.5 s later the game's login failure; a vanilla client the login failure at once (the game's Generic error, 5). `Ok` false: the player is not in game, or the game's kick call failed. `PlayerKickedEvent` follows. A kick is not a ban |
| `Network.Kick(NetPeer, reason)` (Kiln.Network) | the same for a connection, also before its player exists (from `NetClientHandshakeEvent`) |
| `Bans.Add(PlayerRef / steamId, reason, duration?)`, `Bans.Remove(id)`, `Bans.All`, `Bans.Find(id)` | Kiln's ban list, apart from the game's own: `kiln/data/kiln-players/bans.json` (the server's Kiln data, for every world it runs). An entry names a Steam id and, for a player banned while online, the game's account id (`Player.AccountId`); a connection that names either is refused. `PlayerBannedEvent`, `PlayerUnbannedEvent` |
| `PlayerLoginEvent` (cancelable) | once per connection, when it first names its player (account id or Steam id): `Name`, `SteamId`, `AccountId`, `Entity`, `HasKiln`, `Reason`. Cancel refuses it the way a ban does. Not posted for a banned player |
| `/kick <player> [reason]`, `/ban <player\|steamid> [30m\|12h\|7d] [reason]`, `/unban <id>`, `/bans` | Kiln's admin commands (level Admin, nodes `kiln.command.kick`, `.ban`, `.unban`, `.bans`) |

How it works: Kiln.Network (which references Kiln.Players) fills the kick at its start and checks every connection on
its server tick (about once a second): once a connection names an account id (the player record's, `kiln.net`) or a
Steam id (Kiln.Players), a ban or a `PlayerLoginEvent` refusal sends Kiln's REJECT and the game's login failure through
the same path as the refusal of clients without Kiln ([../network.md](../network.md)). A vanilla client that proves
nothing is held at the gate while that happens; a Kiln client is not held and may stand in the world for a second or
two before it is refused. Capabilities `players.kick` and `players.bans` (reported by Kiln.Network: they need kiln.net,
the sigdb feature `net.kick` and a process that serves clients). Errors: a none player, an empty reason or one over
200 characters, a ban without a Steam id or with a duration that is not positive throw `ArgumentException`; off throws
`KilnUnavailableException`; a broken `bans.json` is left alone, logged, and refuses new entries until it is fixed.

Tested: `managed/tests/Kiln.Players.Tests/PlayerAdminTests.cs` (the list on disk, the error rule, the kick seam),
`managed/tests/Kiln.Network.Tests/PlayerGateTests.cs` (a banned account refused once it is named, a login handler's
refusal, REJECT then the login failure for a Kiln client, the Generic login error, the event); a lab with
`mods/tests/kick-test` and `tools/nettest/kicktest.py` (fake clients: a Kiln client gets the REJECT with the reason and
is dropped after 1.5 s, one without Kiln at once; `/ban`, `/bans`, `/unban` through the console folder and
`bans.json`). Lab peers have no player record, so a ban and the login event have not refused a real connection yet.
Not run with a real player: whether the login failure takes a player who is already in the world out of it (the game
was seen to obey it only during the join, [../../re/network.md](../../re/network.md)), and what the game's dialog says
for the Generic error. Guided checks `kick-test/players.kick`, `players.kick.after`, `players.ban`, `players.ban.after`.

## C (native plugins)

```c
#include "services/kiln_players.h"
static const KilnPlayersApi* players;
static void KILN_CALL on_player(const KilnPlayerEvent* e, void* user) {
    /* tick thread, Pre */
    if (e->kind == KILN_PLAYER_JOINED) { /* e->player.name, .entity, .steam_id, .position ... */ }
}
static void KILN_CALL on_ready(void* user) {
    players = (const KilnPlayersApi*)api->service((KilnStr){KILN_PLAYERS_SERVICE, 12}, 1);
    if (players) players->subscribe(owner, &on_player, NULL, &handle);   /* unsubscribe(handle) before unloading */
}
/* anywhere: */
KilnPlayer list[16]; uint32_t n = 0;
players->list(KILN_PLAYERS_LIST_IN_GAME, list, 16, &n);
```
`list`, `get`, `find_name`, `find_steam_id`, `events`, `status` read the last step's snapshot from any thread;
`position` reads live on the tick thread; `raw_slot` and `probe_position` are tick-thread diagnostics; `events(after,
...)` replays the ring of the last 64 events (for pollers). `KilnPlayer` is frozen in v1 (arrays are packed at
`sizeof(KilnPlayer)` = 224).

## How it works

- At startup the service hooks the engine's `RegisterSingleton` (runs only while a world is set up) and keeps the
  server world, `ServerPlayers` (the game server's 16 player records) and `BasicPlayerInfos`. At the first step it
  checks them against the world's singleton map and the game server's world pointer; any mismatch turns the records
  off (log fallback), never a guess.
- Every step (Pre, ~2 us on an empty server) it copies the 16 records under an SEH guard, reads names, permissions,
  account ids, the admin bit and positions (`GetEntityTransform`), adds Steam ids from the log tracker, diffs against
  the last step (joined: the record is in game and the entity exists; left: it no longer is, or the slot changed
  hands) and publishes the snapshot.
- The log tracker follows the game's join/leave lines from the core's game-log mirror (wording in
  `docs/re/players.md`). It is the only source of Steam ids, and the whole player list when the records are not found.
- Fail-soft: an unknown build loses the records feature (log fallback), a missing mirror loses Steam ids, a missing
  `GetEntityTransform` loses positions; with neither records nor the log the service does not register. Every read of
  game memory is guarded; a faulting native subscriber is removed.

## Kiln.Admin

`ops.players` -> `Players.All` (`Entity`, `Position`; `health`/`healthMax` are the live Health attribute through
kiln.combat, the same read as the `health` op; a player character has no `NetworkHealth` component, its replicated
vitals are `NetworkPlayerAttributes`, the fallback without kiln.combat),
`is_player` -> `Players.IsPlayer`, name/Steam lookups for any op that takes a player. Kick is not in v1 (see the RE
notes).

## Testing

```sh
export KILN_OUT=<build dir>
dotnet build mods/tests/players-test -c Release -o $KILN_OUT/mods/players-test --artifacts-path $KILN_OUT/dotnet
tools/deploy-server.sh ~/srv/enshrouded/labs/<lab> --policy any
tools/lab/lab.sh start <lab>; grep players-test ~/srv/enshrouded/labs/<lab>/kiln/logs/kiln.log
```
The test mod checks everything that needs no client (empty lists, every raw record free, lookups, threads, the
position path against kiln.world, the log tracker on real join/leave lines in file and mirror form) and prints
`players-test: N PASS, M FAIL`. Leak check: `echo "reload-test players-test 3" > kiln/control/rt.cmd`.

**Simulation (labs only).** `KILN_PLAYERS_SIMULATE='kiln\mods\players-test\fixtures\join-leave.log' tools/lab/lab.sh
start <lab>` makes the service ignore the records and replay that file (game-log lines, relative to the server folder
or a `Z:\...` path) into its live log tracker, one line per step after the 50th: two players join and leave through
the real event path, and the test mod checks what its handlers got. The service logs a `SIMULATION` warning and
`Players.Diagnostics.Status.Simulated` is true. Never set it on a real server.

**Identity.** `players-test` checks `PlayerRef` and its stamps in every run. With
`KILN_PLAYERS_SIMULATE='kiln\mods\players-test\fixtures\slot-reuse.log'`, Alder joins slot 0 and leaves, Birch takes
slot 0, Alder rejoins into slot 1: the first session's ref is not online after the reuse, the two players are not the
same person, and `Resolve` gives Alder's new session. `mods/tests/players-test/autostart/` is a separate mod
(`players-autostart-test`) that references only Kiln.Api: `PlayerRef.Of` throws `identity.players` in its `OnLoad`
and answers from its first tick, so Kiln.Players was started without a mod asking for it. Unit tests, no game:
`managed/tests/Kiln.Players.Tests`.

## Client checklist (needs a joined player)

1. `Players.All` lists them with the right name, entity 1, `Permissions` of their group, `State InGame`, a position
   that matches the game's `Player EntityId 1 teleported From (...)` line; `Joined` fires once, at the
   `logged in with Permissions` line; `Left` once at `Remove Entity for Player`.
2. `SteamId` equals the `[online] Added peer ... (steamid:...)` id; compare `AccountId` with it (open question: id64 or
   a hash).
3. Join with each user-group password (Admin, Friend, Guest, Visitor): `UserGroupId` and `Permissions` per group;
   `IsAdmin` for each (does anything set the game's admin bit on a dedicated server?).
4. Two players, one leaves and rejoins: the slot and entity are reused, `Joined`/`Left` pair up, `Handle` changes
   generation. The first session's `PlayerRef` is not `IsOnline`, `Resolve()` gives the new session, and the stamp's
   generation bits changed.
5. Shutdown with a player on: `Left` fires.

## In a game client (hosted worlds)
- Only the server world's `ServerPlayers` / `BasicPlayerInfos` are used: a client also registers `BasicPlayerInfos` in
  its own client world, and the RegisterSingleton hook ignores worlds whose kind is not 0.
- Each hosted game is a server-world session. When the game closes the world (back to the main menu), everyone still
  in game gets a `LEFT` event at that moment, and the singletons and validation are dropped. The next world's first
  scan validates its own (`players: world ... (server world: yes)` once per session). On a dedicated server nothing
  of this runs: one world for the whole run.
