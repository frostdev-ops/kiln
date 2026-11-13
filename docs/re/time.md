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

## The clock's speed (svn 1287369)
`g38_daytime` speed setter `(daytime, float scale /*xmm1*/, ecsNow /*r8*/)`: `GetGameTime(daytime, ecsNow)`, `++counter`
(+0x24), `scale` -> +0x20, `+0x10 = ecsNow`, `+0x18 = game time` (server RVA `0x95b640`, client `0xeb39d0`; symbol
`time/SetTimeScale`, feature `time.scale`). It re-anchors before changing the speed, so the clock never jumps. It
has **one caller** in each exe (server `0x108f75`, inside `0x108de0`): a server program that walks the 16 player slots
(stride 0xA0), counts the players present and those asleep, and computes the wanted speed (1, or faster while every
present player sleeps); it calls the setter only when the current speed differs from the wanted one by more than a
relative tolerance. A remote client has no other writer: its Daytime copy takes `{anchor ECS, anchor game, scale}` from
the server's sync (`DaytimeSyncIfChanged` copies +0x10, +0x18, +0x20), so a speed set on the server reaches every
client with the next ServerInfo.

Kiln holds a speed by hooking the setter: while held, a call for the server world's Daytime whose current speed already
equals the held one returns at once (the sleep program then asks every step, and nothing changes: no counter bump, no
re-sync); any other call is made with the held speed. Kiln's Pre applies the held speed when the field differs (after a
load, or Kiln's own change), through the original setter.

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

## Choosing the weather (since 0.2.0-dev.27; server 26430253, exe `f0d824be…`; client 26430625) **[refl] [data] [dis] [rt]**
**The table is game data.** The server's weather is one `keen::WeatherSystemResource` (`2dcf7ad7-3fd8-41e4-870e-bf713f324119`,
read offline with `tools/kfc decode`): `layers` (BlobArray of `WeatherSystemLayerResource`, 200 B: guid, `restrictMaskIndex`
+0x10, `excludeMaskIndex` +0x11, `phenomenonIndexRange` {start +0x12, count +0x13}, `nothingPhenomenon` +0x18),
`phenomenons` (BlobArray of `WeatherSystemPhenomenonResource`, 176 B: guid, `debugName` BlobString +0x10, `probability` +0x28,
`flags` +0x2c, `durationMin` +0x30, `durationMax` +0x38, spawn and despawn fade +0x58/+0x60, `effect` +0xa8 None/Rain/Blizzard),
mask tables, and state configurations (`stateConfigurations`, `layerStateConfigurations`: Ignore / Reset / SetPhenomenon with
`layerPhenomenonIndex` and an optional `overrideDuration`; `newWorldStateConfiguration` sets clouds_few on layer 0 for 15
minutes and resets the others). On this build: layer 0 holds clouds_few, clouds_medium, clouds_overcast, clouds_fluffy (its
nothing has weight 0: there are always clouds); layers 1, 3, 5, 6, 7 a `rain` each (different region masks), layer 2
`rain_snow`, layers 4 and 8 nothing at all; `debug_rain`, `debug_blizzard`, `debug_dragon` lie in no layer's range, so the
roll never picks them. Kiln reads the table from the running resource, never from a list of its own:
```
INFO weather table: clouds_few (#0, layer 0), clouds_medium (#1, layer 0), clouds_overcast (#2, layer 0), clouds_fluffy (#3, layer 0), rain (#4, layer 1), rain_snow (#5, layer 2), debug_rain (#6, layer -1), debug_blizzard (#7, layer -1), debug_dragon (#8, layer -1), rain (#9, layer 3), rain (#10, layer 5), rain (#11, layer 6), rain (#12, layer 7)
```

**The roll** (`WeatherUpdateLayers` `0xa233d0`, called from `WeatherUpdate` `0xa23800` with `(ws, resource, now, params)`,
`now` = the weather clock = game time): per layer, a previous instance whose despawn time passed is removed; when
`next <= now`, the previous instance is removed, the current one becomes the previous (it fades out by its own despawn
time), and the next phenomenon is drawn from the layer's range plus its nothing by `probability` (rain-effect entries
times the weather-frequency factor `params`), skipping the one that ran; the RNG is two 16-bit multiply-with-carry
generators at `ws+0xb4`/`+0xb8`. The draw ends in **the start of a phenomenon** `0xa22830` (client `0xaab220`)
`(ws, resource, now, layer, u8 choice, Optional<Time>* overrideDuration)`: choice is the offset in the range, the range's
count means nothing; it writes the choice to the layer's `+0x08`, rolls the duration between min and max unless the
override is set, sets `next = now + duration - despawnFadeTime` (the next roll starts while this one fades), and creates
the instance (a random place inside the layer's region masks, radius, rotation, drift speed: more RNG) whose handle goes to
`+0x10`; the game's instance add path tells the clients.

**The game's own way to force one**: `0xa22f50(ws, now, Guid* stateConfiguration)` applies a state configuration; for each
layer, `SetPhenomenon` removes both instances, sets `next = INT64_MIN`, the choice field to -1, clears the instances, then
calls the start above with the configuration's choice and override duration; `Reset` removes both instances and sets
`next` (min, or now + override). `WeatherUpdate` calls it once for `newWorldStateConfiguration` when `ws+0xb0` is set.

**What Kiln does** (`native/services/time/time.cpp`, feature `time.weather_choose`): it hooks `WeatherUpdateLayers` (it already
hooked `WeatherUpdate` for the reset) and, before the game's roll, runs the requests queued since the last update: a choice
does exactly what `SetPhenomenon` does for one layer, with the mod's choice and duration (the override); a hold makes
Kiln do the game's own expiry step for that layer with the held choice whenever `next <= now`, so the game's roll then
finds nothing due there; a release ends the hold and, with NOW, does `Reset` (the game rolls in the same update). No
game function is patched; the only writes are the ones the game's own SetPhenomenon and expiry make. Symbols:
`time/WeatherStartPhenomenon` (captured from the roll's call), `@time.weather_table` (the BlobArray offsets, both strides,
the range and nothing offsets and the layer's choice field, captured from the start function; the client allocates other
registers, `client.d/time.json`). The record fields no captured code reads (name, weight, durations, effect, masks) are the
reflected offsets above, used only while the captured strides equal the reflected sizes.

Lab (`dev27-reach2`, server 26430253, no client; `mods/tests/chat-test` `WeatherTest.cs`, clock held at 20x) **[rt]**:
```
time: weather layer 0: started clouds_fluffy (table 3) for the given time; now runs 3 (instance 196612), next roll at game time 3012259960948 ns (clock 3002259960948 ns)
PASS weather.timed.back: after 30 s of game time the game rolled layer 0 itself: clouds_fluffy -> clouds_few (#0, layer 0) (layer 0: clouds_few, next roll in 142 s)
time: weather layer 1: started rain (table 4) for its own time, held until released; now runs 4 (instance 327681), next roll at game time 3296842012224 ns (clock 3013593293963 ns)
time: 1 held weather layer(s) rolled with Kiln's choice at game time 3297593304453 ns
PASS weather.hold.kept: the game's roll came (283 s left before) and Kiln kept rain: layer 1: rain, next roll in 326 s, held rain (#4, layer 1)
PASS weather.release: released with now: the game rolled at once and picked none (layer 1) (layer 1: nothing, next roll in 689 s)
```
The first run passed the mod's 30 s as the override and `next` came 10 s ahead (clouds_fluffy's despawn fade is 20 s), and
for clouds_overcast `next == now` (its fade is 30 s): the game rolled again in the same update. Kiln now passes the
duration plus the chosen phenomenon's `despawnFadeTime` (+0x60), so the layer is rolled `duration` after the start.

**Restart: the chosen weather does not come back.** Kiln left a two-hour `rain_snow` on layer 2 and a hold on layer 1,
the server autosaved and stopped; after the load layer 2 ran nothing with 373 s to its next roll and layer 1 was not held
(`weather.persist.timed`, `weather.persist.hold`). The world save has a `weather` chunk (docs/re/characters.md) with
`save_v0::WeatherSavedLayer` {nextPhenomenon, current and previous instance}; whether the game restores it and then rolls
anew because the load restarts the game clock in its first cycle (`setTimeOfDay` from `0x7c0f58`, above), or never restores
it, was not traced **[hyp]**. Either way a mod that wants its weather after a restart sets it again.

## Open questions
- Client view of a set, of a weather reset and of a chosen phenomenon (guided check `time.weather`).
- Whether the save's `weather` chunk is restored at a load (a chosen phenomenon does not survive a restart, above).
- `+0x59` (authoritative flag) is not checked; the server has only one Daytime (`ServerGame_create`).
- kiln.settings also hooks `SendServerInfo` (installed first: service DLLs load in file-name order). The two safetyhook
  inline hooks chain; run together in the lab, both fire: `settings: GameServer instance 00000000ACC7CEC8
  (SendServerInfo)` and `time: ServerInfo now carries daytime change 6`, 50/50 chat-test checks.
