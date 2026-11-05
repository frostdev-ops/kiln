# Time of day and weather on the server (kiln.time)

Current build: dedicated server svn 1285601 (Steam 26404334, exe sha256 `c5c9fe50c6398d2a…`). Also resolves on
1273043. Sources: EMF 0.3.3 docs (MIT) `re_time_spawn_weather.md` and
`world_building_daytime.md` (client build 1269579), re-derived here with capstone on the server exe and checked live in
lab `chat`. Definitions: `sigdb/symbols/server.d/time.json`. Service: `native/services/time/time.cpp`.

## Functions (RVA, current / 1273043)
| Name | 1285601 | 1273043 | What |
|---|---|---|---|
| `time/SetTimeOfDay(daytime, tod, ecsNow)` | `0x959820` | `0x94f5a0` | `+0x10 = ecsNow; +0x18 = TodToGameTime(tod); +++0x24`. EMF sig 9. |
| `time/TodToGameTime(daytime, tod)` | `0x927560` | `0x91d590` | tod (ns, 0..24 h) -> game time inside the first cycle |
| `time/GameTimeToTod(daytime, game)` | `0x932380` | `0x9283a0` | game time -> tod |
| `time/GetGameTime(daytime, ecsNow)` | `0x937a30` | `0x92da40` | `anchorGame + (ecsNow - anchorEcs) * scale` (through float seconds) |
| `time/DaytimeUpdate(daytime, ecsNow)` | `0x969a80` | `0x95f650` | per step from `server_update_daytime`: `+0x48 = GameTimeToTod(GetGameTime(now))`, `+0x58 = tod in [+0x28, +0x30)` |
| `time/WeatherUpdate(ws, {time,valid}*, params*)` | `0xa23810` | `0xa18b40` | GameServer frame, after the sim step; calls `WeatherUpdateLayers` when `ws+8 == 0` (server) |
| `time/WeatherUpdateLayers` | `0xa233e0` | `0xa18720` | re-rolls a layer when `now >= layer.next` |
| `time/WeatherRemoveInstance(ws, handle)` | `0xa21540` | `0xa168b0` | frees the instance, queues the client remove; handle 0 = no-op |
| `time/SendServerInfo(GameServer)` | `0x7c2610` | `0x7b9060` | builds/sends ServerInfoMessage; EMF sig 50 |
| `time/ServerGameDaytimeSync(out, &last, serverGame)` | `0x23a310` | `0x234a70` | `jmp DaytimeSyncIfChanged(out, &last, serverGame+0x2D0)` |
| `time/DaytimeSyncIfChanged(out, &last, daytime)` | `0x96fd80` | `0x965910` | if `daytime+0x24 != *last`: copy `{+0x10, +0x18, +0x20}`, `*last = counter` |

Callers of `SetTimeOfDay` on 1285601 (three, as in EMF): `0x23498b` ServerGame_create (`serverGame+0x2D0`, 07:00, ecs 0),
`0x7c0f58` savegame load (`(ctx+0x20)`, the saved time, `ecsWorld_getTime(world)` = `0x659830`), `0xbd17d` `admin_player`.

## Daytime (`g38_daytime::Daytime`, 0x70 B; every offset captured from code, same on both builds)
`+0x10` anchor ECS time, `+0x18` anchor game time, `+0x20` f32 time scale, `+0x24` u32 change counter, `+0x28` day start
(tod ns), `+0x30` night start, `+0x38` day length (real ns), `+0x40` night length, `+0x48` time of day (written each
step), `+0x58` u8 is-day (fixed value 0x58, seen in DaytimeUpdate at `0x969c92`). Cycle = day length + night length.

ECS time = clock period (`storage+0x4C0`, 16,666,667 ns) x clock (`storage+0x4DC`), the core's `KilnTickInfo`
`clock_period` x `clock`. Live: at clock 1 `ecs 16666667 ns`.

## How a change reaches clients
`SendServerInfo` (GameServer frame, after the sim step) calls `ServerGameDaytimeSync(&msg.daytimeSync,
&GameServer+0x17C4 copy, GameServer+0x1D8)`; when the counter moved it puts the anchor into the ServerInfoMessage and
writes the counter back to `GameServer+0x17C4` (`0x7c2c75`) after sending. Clients apply it with `Daytime_applySync`
(EMF, client side). Live: `SendServerInfo` runs every frame even with no client connected, and
`GameServer+0x17C4` follows Kiln's counter within the same frame:
```
time: GameServer 00000000ACC7CEC8 syncs Daytime 0000000108BC6A20 (same as Kiln's), last sent counter 2
time: set 13.5000 h (forward): was 16.0981 h, now 13.5000 h; game time 2722117822288 -> 6337500000000 ns; counter 5 -> 6; ecs 11916666905
time: ServerInfo now carries daytime change 6 (sent to clients 0 ms after the set)
```
(`GameServer` 0xACC7CEC8 is the same object kiln.chat captures from ChatReceive.) Whether a client then shows the new
time needs a client: see the checklist in docs/kiln/services/time.md.

## Setting the time: two ways
- **Game** (`KILN_TIME_SET_GAME`): call `SetTimeOfDay(daytime, tod, ecsNow)` like the admin menu. Game time restarts in
  the first cycle (live: `game time 2137633333340 -> 3656250000000 ns`, inside the 4,200 s cycle).
- **Forward** (default): Kiln writes the anchor itself, keeping game time monotonic: `g = GetGameTime(now)`,
  `t = TodToGameTime(tod)`, `target = g - g mod cycle + t` (+ one cycle if behind), `+0x10 = now, +0x18 = target,
  ++counter`. Live: `game time 2722117822288 -> 6337500000000 ns` and the game's own field follows one step later:
  `PASS time.set.forward.game-field: game's time-of-day field 13.5004 h (13:30), day=True (day phase 06:00-22:00)`.
  Weather is timed on game time (`ServerGame_getWeatherTime` `0x238d20` = `GetGameTime(serverGame+0x2D0, ecs)`), so a
  forward jump only brings the next weather change closer; a game-reset can leave `nextPhenomenonTime` far ahead.

Both are done from Pre/Post on the tick thread; the Daytime readers run inside the step and the weather/ServerInfo after
it, all on that thread (GameServer frame `0x7bbeb0`: ChatReceive `0x7bc4d2` -> runSimStep `0x7bcb14` -> weather
`0x7bce29` -> ServerInfo `0x7bcfd1` -> chat send `0x7bd2ac`).

## Persistence
The server autosaves every 30 s while running and on shutdown (`world0.ksd` mtime 22:34:02, 22:34:32, stop 22:35:01).
The save keeps the time of day; loading restores it through `SetTimeOfDay` from `0x7c0f58`. Live, across a restart:
```
time: set 9.2500 h (forward): was 16.1013 h, now 9.2500 h; ...; counter 8 -> 9
[restart]
time: game setTimeOfDay(9.7601 h, ecs 9933333532) from rva 0x7c0f58; counter 3
PASS time.persist: set 09:15 before the restart (...), the loaded world shows 09:46 (+31.3 game minutes)
```
(The clock ran ~2 real minutes to the shutdown save; the lab's day phase is 16 h in 60 min.)
Caveat: the world steps for ~10 s while the save loads, and the load then sets the saved time. A set before the game
server reaches state Run (`[game_server] Switching state from LoadEcsScene to Run`) is overwritten (seen in the first
lab run: Kiln set 13:30 at clock 1, the load set 15:33 ten seconds later).

Day/night lengths: `server_update_daytime` calls `Daytime_setDurations` every step with
`GameSettings.dayTimeDuration/nightTimeDuration`; it re-anchors and bumps the counter when they change (live: counter
3 after the load, 5 once the lab's 60/10 min settings applied; 30/12 min defaults before). So lengths are read-only
here; change them through the game settings (kiln.settings).

## Weather reset
`WeatherSystem` (0x150 B): `+0x08` mode (0 = server), `+0xC8` layers, `+0xD0` count, stride 0x18:
`+0x00` nextPhenomenonTime (game time), `+0x10` current instance, `+0x14` previous instance (captured from
`WeatherUpdateLayers`; the old build needed register-agnostic patterns). Kiln captures `ws` in a `WeatherUpdate` hook
(mode 0) and resets in Pre/Post: `RemoveInstance(prev)`, `RemoveInstance(cur)`, clear both, `next = 0`. The game's
next weather update (same frame) re-rolls every layer:
```
time: weather reset: 9 layers re-roll, 3 instances removed
time: weather update after the reset: 9 of 9 layers rolled a new phenomenon (2 with a live instance), next change at game time 5766683349609 ns
```
Clients get the removes through `WeatherSyncRemoveInstanceMessage` and new instances through the normal add path (EMF;
not observed without a client).

## Open questions
- Client view of a set and of a weather reset (client checklist).
- `+0x59` (authoritative flag) is not checked; the server has only one Daytime (`ServerGame_create`).
- kiln.settings also hooks `SendServerInfo` (installed first: service DLLs load in file-name order). The two safetyhook
  inline hooks chain; run together in the lab, both fire: `settings: GameServer instance 00000000ACC7CEC8
  (SendServerInfo)` and `time: ServerInfo now carries daytime change 6`, 50/50 chat-test checks.
