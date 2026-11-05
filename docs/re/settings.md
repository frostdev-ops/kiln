# Game settings (`keen::ecs::GameSettings`) on the dedicated server

Server build svn 1285601 (`enshrouded_server.exe` sha256 `c5c9fe50…`, image base `0x140000000`, RVAs below). Static
analysis with capstone over the PE image (`tools/sigport/peimg.py`), checked live in the `settings` lab. Sources used:
EMF 0.3.3 docs and `emf_core.dll` string/offset tables (MIT), ShroudForge's MIT runtime and its profile
`modules/game-runtime/profiles/enshrouded/server/26404334.json` (this build), the server's own
`enshrouded_server_readme.txt` (version 1.0.0.0). Nothing from `kfc-parser/`.

## 1. Where the live settings are

One 144-byte block, the values of `enshrouded_server.json`'s `gameSettings` (or of the preset). Every path below ends
at the same address (lab: `ready: 38 settings in the 144-byte block at 00000000000EDE00 (instance
00000000ACC7CEC8, world 00000000B2410080); world singleton ecs::GameSettings: same block`):

| Owner | Field | Evidence |
|---|---|---|
| GameServer instance | `+0x1758` pointer to the block, `+0x1760` preset byte (0 Default … 4 Custom) | copied from the parsed config (`cfg+0xA8`, `cfg+0xB0`) at `0x7ab199` / `0x7ab1ad`; read by the apply-settings state `0x7af7c0` (logs `[server] Game Settings '%k'` at `0x7b02a8`) and by `SendServerInfo` |
| GameServer instance | `+0x18E0` byte "send the settings to the players" | set to 1 at `0x7b0276` (world start), tested at `0x7c29bb`, cleared at `0x7c2ca5` (both in `SendServerInfo`) |
| GameServer instance | `+0x1D0` server world, `+0x1D8` game-systems object | `mov rcx,[r14+0x1d0]` before `call SendServerInfo` in the instance tick (`0x7bcfb1`); `mov [rdi+0x1d8],rax` after the setup call (`0x7b024b`) |
| game-systems object | `+0x488` the block (from `params+0xF0` = `instance+0x1758`) | setup `0x233ee0` stores it at `0x2340da` and registers it at `0x236459` as **`ecs::GameSettings`** through `RegisterSingleton` `0x67e720` |
| `BalancingRegistry` (`systems+0x1F0`, registered at `0x235952`) | `+0x90` the block | every small accessor: `0x94a060` `mov rax,[rcx+0x90]; movzx eax,byte [rax+0x14]; ret` (enableDurability) and ~30 more (section 4) |
| world singleton map | `ecs::GameSettings` (FNV-1a `0x943eebdd`) → the block | records `world+0x7462D0` (stride `0xE8`, hash `+0xA0`, object `+0xA8`), map bits `+0x746300`, capacity `+0x746308`, keys `+0x746320`, values (u16) `+0x746338`, count `+0x74634C` (`RegisterSingleton` `0x67e720`); kiln.settings asks the core (`api->singleton`) |

ShroudForge reaches the same block through its named-service records (`serviceLayout` in the 26404334 profile:
`recordsPointerOffset 0x7459A8` relative to its `registry_owner` = `world+0x928`, i.e. `world+0x7462D0`; stride 232,
hash `+160`, pointer `+168`). Its contract: size 144, qualified hash 2298714128 (`0x89039810`).

EMF 0.3.3 knows the two instance offsets as code offsets `InstSettingsPtr` (`0x1758`) and `InstSendSettingsFlag`
(`0x18E0`), scanned inside `SendServerInfo` (its signature index 50) with `80 BB ?? ?? ?? ?? 00 74 ?? 48 8B 83 ?? ?? ?? ??`
(string table at `emf_core.dll+0x2a0088`).

### How kiln.settings gets there
`settings/SendServerInfo` (`0x7c2610`, `48 8B C4 55 53 41 54 41 55 41 57 48 8D A8 …`) takes the GameServer instance in
`rcx`. kiln.settings hooks it only to learn the instance (and to count the sends it asked for); the hook calls the
original unchanged. `SendServerInfo` first runs at the end of the server's first world step, right after Kiln loaded
the mods, so mods see `Ready` one step later (lab: `GameServer instance 00000000ACC7CEC8 (SendServerInfo)` 17 ms after
`mod settings-test 0.1.0 loaded`). kiln.time hooks the same function (load order: settings, then time); both call
through.

## 2. Layout: the game's own reflection

`TypeByHash(hash)` `0x7e15d0` (a pure lookup in the type registry; also called with `mov ecx,0x89039810` at
`0x7a55a8`, `0x7b03bc`) returns the type metadata. The type hash is FNV-1a of the qualified name
(`fnv1a("keen::ecs::GameSettings") = 0x89039810`). Metadata (static, `keen::ecs::GameSettings` at RVA `0x11B7A60`):

| Offset | Meaning |
|---|---|
| `+0x00/+0x08` | short name ptr/len; `+0x10/+0x18` impact name; **`+0x20/+0x28` qualified name** |
| `+0x38` | inner type (typedef target, enum's underlying type) |
| `+0x40` u32 | size (144); `+0x44` u16 alignment |
| `+0x48` u32 | field count (38) or enum value count; **`+0x4C` u32 kind** |
| `+0x50` u32 | hash (FNV-1a of the qualified name) |
| `+0x58` | field records: stride `0x30`, `{name ptr, name len, type ptr (+0x10), offset u64 (+0x18), 0, 0}` |
| `+0x60` | enum values: stride `0x28`, `{name ptr, name len, value u64 (+0x10), …}` |

Kinds: 1 bool, 2 u8, 3 s8, 4 u16, 5 s16, 6 u32, 7 s32, 8 u64, 9 s64, 0xA float32, 0xC enum, 0x11 typedef (`keen::float`
→ `keen::float32`), 0x12 struct (`keen::Time` = struct `{sint64 value}`, nanoseconds). The hash offset `+0x50` is also
captured from `TypeByHash`'s fallback loop (`48 8B 11 39 5A 50`); the rest are fixed in `settings.json` and checked at
run time (qualified name, hash, kind, size, field bounds), so a different layout makes the service refuse instead of
writing.

The block on this build (38 fields; June 2026's `types.json` had 37, without `flameLevelFogRemoval`, and no
`Automatic` spawner amount):

| Off | Field | Type | Off | Field | Type |
|---|---|---|---|---|---|
| 0x00 | playerHealthFactor | f32 | 0x48 | experienceCombatFactor | f32 |
| 0x04 | playerManaFactor | f32 | 0x4C | experienceMiningFactor | f32 |
| 0x08 | playerStaminaFactor | f32 | 0x50 | experienceExplorationQuestsFactor | f32 |
| 0x0C | playerBodyHeatFactor | f32 | 0x54 | randomSpawnerAmount | enum u8 |
| 0x10 | playerDivingTimeFactor | f32 | 0x55 | aggroPoolAmount | enum u8 |
| 0x14 | enableDurability | bool | 0x58 | enemyDamageFactor | f32 |
| 0x15 | enableStarvingDebuff | bool | 0x5C | enemyHealthFactor | f32 |
| 0x18 | foodBuffDurationFactor | f32 | 0x60 | enemyStaminaFactor | f32 |
| 0x20 | fromHungerToStarving | Time (ns) | 0x64 | enemyPerceptionRangeFactor | f32 |
| 0x28 | shroudTimeFactor | f32 | 0x68 | bossDamageFactor | f32 |
| 0x2C | tombstoneMode | enum u8 | 0x6C | bossHealthFactor | f32 |
| 0x2D | enableGliderTurbulences | bool | 0x70 | threatBonus | f32 |
| 0x2E | weatherFrequency | enum u8 | 0x74 | pacifyAllEnemies | bool |
| 0x2F | fishingDifficulty | enum u8 | 0x75 | tamingStartleRepercussion | enum u8 |
| 0x30 | miningDamageFactor | f32 | 0x78 | dayTimeDuration | Time (ns) |
| 0x34 | plantGrowthSpeedFactor | f32 | 0x80 | nightTimeDuration | Time (ns) |
| 0x38 | resourceDropStackAmountFactor | f32 | 0x88 | curseModifier | enum u8 |
| 0x3C | factoryProductionSpeedFactor | f32 | 0x89 | flameLevelFogRemoval | u8 |
| 0x40 | perkUpgradeRecyclingFactor | f32 | | | |
| 0x44 | perkCostFactor | f32 | | | |

Enums (reflection of this build): TombstoneMode AddBackpackMaterials 0, Everything 1, NoTombstone 2 · WeatherFrequency
Disabled 0, Rare 1, Normal 2, Often 3 · GenericDifficulty VeryEasy 0 … VeryHard 4 · RandomSpawnerAmount Few 0, Normal 1,
Many 2, Extreme 3, Automatic 4 · AggroPoolAmount Few 0 … Extreme 3 · TamingStartleRepercussion KeepProgress 0,
LoseSomeProgress 1, LoseAllProgress 2 · CurseModifier Easy 0, Normal 1, Hard 2 · GameSettingsPreset Default 0,
Relaxed 1, Hard 2, Survival 3, Custom 4 (type hash `0x2df2c1ff`).

## 3. How the game itself changes it, and how players get it

- **In-game admin menu:** `admin_player` (`0xbc0b0`, program descriptor at `0x1379f40`; mutable singletons
  `ecs::GameSettings`, `g38_daytime::Daytime`) compares `PlayerInput.fromAdminClient.adminChangeGameSettingsAction`
  (`PlayerInput+0x4C8`, 152 bytes: the block + `VersionedData` at `+0x558`) with
  `ServerConsumedPlayerInput.consumedAdminChangeGameSettingsAction` (`+0xD0`). On a new version it copies the 144 bytes
  straight into the block (`0xbd679`–`0xbd6e0`, nine `movups`), acknowledges the version and emits
  `GameSettingsChangedEvent` (hash `0x7ea6585c`, size `0x98`, `newSettings` at `+8`; `0xbd713`–`0xbd7a4`). No ECS
  change tracking, no persistence, no `enshrouded_server.json` write. The event's only consumer is the client program
  `client_update_game_settings` (descriptor `0x1382870`, no server code; events-in `GameSettingsChangedEvent`, writes
  `ecs::GameSettings` in client worlds).
- **Server info:** `SendServerInfo(instance)` (`0x7c2610`, called from the instance tick at `0x7bcfd1` whenever the
  world stepped) builds the `ServerInfoMessage` (0x108 bytes). When `instance+0x18E0` is set it copies the block into
  the message (`0x7c29c4`–`0x7c2a22`, optional field flag `[rbp+0x40]=1`), sends the message to each of the 17 player
  slots (`instance+0x16A0`, stride 8) and clears the flag only if every send worked (`0x7c2c6a`–`0x7c2ca5`); with no
  player connected it clears it at once. EMF's world settings use this same flag ("the game's own flag that sends the
  settings to every player"). kiln.settings sets it after every change.
- So a kiln.settings change reaches players through the server-info resend, not through `GameSettingsChangedEvent`
  (which only a program could emit). Whether the client applies a settings block that arrives after login: see the
  client checklist (EMF reports it does, "within a second or two").

Lab (no player): every change was followed by `settings: the game sent the settings block to the players (send #N)`
in the same or next step (the flag went 1 → 0 inside `SendServerInfo`), e.g. `PASS resend.sent.enemyDamageFactor: the
game sent the block to the players and cleared its flag after 42 ms`.

## 4. Who reads each setting (does a live change take effect?)

Accessors are BalancingRegistry methods (`mov rax,[rcx+0x90]` + a read of the field); "every step" programs have
descriptor flags without INIT (`+0x180`: 0x8 = run on new entities only; 0x4 system; 0x10 host-only; 0x200 chunked).
Readers were attributed to programs by a call-graph walk (direct calls, 6–8 levels; heuristic, so a listed program
is a strong hint, a missing one proves nothing). "Players' games read it" is EMF 0.3.3's `world_settings.md` column.

Nothing in the server reads a setting once and caches it: every reader found reads the live block (through
BalancingRegistry, a program's singleton binding or the instance). **No setting needs a restart on the server.**
Some apply to what happens next (a buff, a death, a loot roll, a spawn) rather than retroactively.

| Setting | Range (readme) | Default | Server: when a change applies | Reader (evidence) | Players' games read it |
|---|---|---|---|---|---|
| playerHealthFactor | 0.25–4 | 1 | live, whenever max health is computed | `0x940540` (level table × factor), `0x91f4d0`; `health_max_scaling` runs every step | yes |
| playerManaFactor | 0.25–4 | 1 | live | `0x9405a0`, `0x91f5a0`; `mana_max_scaling` every step | yes |
| playerStaminaFactor | 0.25–4 | 1 | live | `0x940600`, `0x91f680`; `stamina_max_scaling` every step | yes |
| playerBodyHeatFactor | 0.5–2 | 1 | live | `0x933489` ← `bodyheat_depletion`; `body_heat_max_scaling` every step | yes |
| playerDivingTimeFactor | 0.5–2 | 1 | live | `0x9405e0` ← `oxygen_update`, `player_oxygen_max_scaling` | yes |
| shroudTimeFactor | 0.5–2 | 1 | live | `0x91f459` ← `fog_power_max_scaling` | yes |
| enableDurability | bool | true | live, every durability loss | `0x94a060` ← `durability_loss`, `actor_spawn_entity` (SF: `0x224e78`, `0x235330` on 25988036) | yes |
| enableStarvingDebuff | bool | false | live | `0x94c460` ← `thirst_and_hunger` | |
| foodBuffDurationFactor | 0.5–2 | 1 | next buff (duration set when a buff starts) | `0x937900` ← `actor_apply_buff`, `update_buffcontainer` | |
| fromHungerToStarving | 5–20 min | 10 min | live | `0x946550` ← `thirst_and_hunger`, instance tick `0x7a7be0` | |
| tombstoneMode | 3 options | AddBackpackMaterials | next death | `player_items_loot` reads `+0x2C` via its binding (`0x1aaa3c`) | |
| enableGliderTurbulences | bool | true | live | `0x91dde0` ← `locomotion_execution` | yes |
| weatherFrequency | 4 options | Normal | live: passed to the weather system every frame; new weather follows | instance tick `0x7bce13` → `0x2390c0` → `0x946ef0` → weather update | |
| fishingDifficulty | 5 options | Normal | next bite | `0x9375c0` ← `server_player_fishing` | yes |
| miningDamageFactor | 0.5–2 | 1 | next hit | `0x93df30`, `0xb28e0` ← `player_control_action_sequence` | |
| plantGrowthSpeedFactor | 0.25–2 | 1 | live | `0x93fda0` ← `growth` | |
| resourceDropStackAmountFactor | 0.25–2 | 1 | next loot roll | `0x941430` ← `random_loot_on_interaction`, `loot_pickup_*`, `default_inventory` | |
| factoryProductionSpeedFactor | 0.25–2 | 1 | live | `0x937520` ← `factory_station` | |
| perkUpgradeRecyclingFactor | 0–1 | 0.5 | next salvage | `0x2db2c0` | yes |
| perkCostFactor | 0.25–2 | 1 | next upgrade | `0x93f9b2` | yes |
| experienceCombatFactor | 0.25–2 | 1 | next XP gain | `0x937370` (switch over the three XP factors) ← quest/knowledge/dismantle rewards | |
| experienceMiningFactor | 0–2 | 1 | next XP gain | `0x937370` | |
| experienceExplorationQuestsFactor | 0.25–2 | 1 | next XP gain | `0x937370`, `0x5dc9d5` | |
| randomSpawnerAmount | 5 options (incl. Automatic) | Normal | new random spawns; enemies already out stay | `random_spawner` (binds GameSettings, host-only, every step) reads `+0x54` at `0x103bf9`, `0x104662`, `0x104b27` | |
| aggroPoolAmount | 4 options | Normal | live | `0x932d4c` (aggro pool limits) | |
| enemyDamageFactor | 0.25–5 | 1 | live | `0x936bb0` (enemy 0x58 / boss 0x68 by type) ← `enemy_scaling` (every step) | |
| enemyHealthFactor | 0.25–4 | 1 | live: max health recomputed every step | `0x9363c0` (enemy 0x5C / boss 0x6C) ← `enemy_scaling` | yes |
| enemyStaminaFactor | 0.5–2 | 1 | live (expected) | reader not pinned (enemy programs; `enemy01` binds GameSettings) | yes |
| enemyPerceptionRangeFactor | 0.5–2 | 1 | live | `0x936be0` ← `enemy01`, `enemy_combat_targets` | |
| bossDamageFactor | 0.2–5 | 1 | live | `0x936bb0` ← `enemy_scaling` | |
| bossHealthFactor | 0.2–5 | 1 | live | `0x9363c0` ← `enemy_scaling` | yes |
| threatBonus | 0.25–4 | 1 | live (expected) | reader not pinned | |
| pacifyAllEnemies | bool | false | live | `pacify` (binds GameSettings, every step) `cmp byte [rax+0x74],0` at `0x101386` | |
| tamingStartleRepercussion | 3 options | LoseSomeProgress | next startle | `0x945ef0` ← `wildlife` | |
| dayTimeDuration | 2–60 min | 30 min | **live, verified**: next step | `server_update_daytime` `0x10a770` (system, host-only, every step): accessors `0x936180`/`0x93ea20` → `Daytime_setDurations` `0x969de0` | yes |
| nightTimeDuration | 2–60 min | 12 min | **live, verified** | same | yes |
| curseModifier | 3 options | Normal | next curse roll | `0x935dd4` (table at BalancingRegistry `+0x214`) | yes |
| flameLevelFogRemoval | 0–20 | 11 | live (placement check) | `0x94a5e0` ← `server_cursor_no_prediction`; `0x966fa0` in the instance tick | yes |

### The day/night clock (lab-verified live effect)
`server_update_daytime` calls `Daytime_setDurations(daytime, day, night, ecsNow)` every step with the block's
durations. `setDurations` (`0x969de0`) compares with `Daytime+0x38/+0x40`; on a change it keeps the current time of
day, stores the durations and (authoritative `+0x59`) re-anchors and increments `+0x24`. `Daytime_update`
(`0x969a80`) then writes the time of day `+0x48`. kiln.settings reads these four fields every step for `status`
(Daytime = world singleton `g38_daytime::Daytime`, from the core's `api->singleton`). Lab:

```
PASS daytime.matches: clock uses day 01:00:00 / night 00:10:00, settings say 60 / 10 min
settings: dayTimeDuration 60 min -> 2 min (owner 13); no resend yet
settings: nightTimeDuration 10 min -> 2 min (owner 13); resend to players requested
PASS daytime.applied: clock now day 00:02:00 / night 00:02:00, re-anchored 1x (live, no restart)
PASS daytime.speed: day at 08:18: time of day ran 16.0 -> 480.4 game-s per s (x30.0, expected x30.0)
PASS daytime.restore: clock back to day 01:00:00 / night 00:10:00
```
(The day phase on this build spans 16 game hours: 57 600 s / 3 600 s = 16 game-s per s.)

**Programs start late:** Kiln loads mods at the first server-world step, during the scene's load. The game's
programs (server_update_daytime too) only run once the instance switched `LoadEcsScene → Run`; in the lab the clock
first moved 10 s after the settings were ready (`PASS daytime.running: … (12:00:00 -> 08:17:12 after 10 s)`). A change
made earlier is in the block and is used from then on.

## 5. enshrouded_server.json

- Parser `0x539040` (called from the startup config loader `0x536a36`): `gameSettingsPreset` into `cfg+0x168`
  (`0x539959`), `gameSettings` into `cfg+0xD8` (`0x5399bb`–`0x539a24`); with a preset other than Custom it logs
  `'gameSettings' will not be applied Preset is not set to '%k'` (`0x539ac7`). So a save must set `Custom`.
- Writer `0x53df40` (`gameSettingsPreset` `0x53e458`, `gameSettings` `0x53e4e5`, `bannedAccounts` `0x53e610`), called
  only from the startup loader (`0x536cc5`, `0x53819c`): the server rewrites the file in its own format at every
  start (observed: `4.000000`, empty arrays on two lines, no final newline). It does not write it while running in
  anything traced here (in-game bans may; not traced).
- kiln.settings `save()` parses the file, sets `gameSettingsPreset: "Custom"` and every live value under
  `gameSettings` (enums by name, durations in ns, whole factors as integers), keeps every other key and their order,
  writes `<file>.kiln-tmp`, keeps the old file as `<file>.kiln-backup` and renames over it. Lab:
  `PASS save.restart: after a restart the server started with the values Save() wrote` (enemyDamageFactor 1.5,
  weatherFrequency Rare, nightTimeDuration 7 min, read back from the block after a stop/start); the server log said
  `[server] Game Settings 'Custom'`.

## 6. sigdb (`sigdb/symbols/server.d/settings.json`)

Resolves on svn 1285601 and on build 1273043 without edits
(`python3 tools/sigport/sigdb_check.py <server.json merged with settings.json> <exe>`):

| Name | 1285601 | 1273043 | From |
|---|---|---|---|
| settings/SendServerInfo | `0x7c2610` | `0x7b9060` | function pattern + rel32 capture from the instance tick |
| settings/TypeByHash | `0x7e15d0` | `0x7d7ed0` | function pattern + rel32 capture from a `mov ecx,0x89039810; call` site |
| settings/DaytimeSetDurations | `0x969de0` | `0x95f9b0` | function pattern with the Daytime field holes |
| instance.settings / .resend / .world / .systems / .preset | 0x1758 / 0x18E0 (3 votes) / 0x1D0 / 0x1D8 / 0x1760 | same | SendServerInfo, instance tick, apply-settings |
| daytime.day / night / changes / authoritative / tod | 0x38 / 0x40 / 0x24 / 0x59 / 0x48 | same | setDurations, isDaytime |

Features: `settings.live` (required), `settings.resend` (players follow), `settings.world` (the instance's world, for
the singleton cross-check and the Daytime lookup through the core's `singletons` feature), `settings.daytime` (status
clock). A missing optional feature only switches that part off. (The singleton-map layout was captured here first,
identically on both builds; it now lives in the core file.)

## 7. Open questions
- Client side: does the client apply a GameSettings block that arrives in a later ServerInfoMessage (EMF says yes),
  and does it do so for every field (UI values such as the perk cost shown, the day/night prediction)?
- `enemyStaminaFactor` and `threatBonus`: the server readers were not pinned (expected in the enemy programs).
- The player max-attribute helpers (`0x91f4d0`, `0x940540` …) are reached indirectly (no direct call sites); that the
  `*_max_scaling` programs use them every step is inferred from their names and flags, not traced.
- Whether an in-game ban rewrites `enshrouded_server.json` while the server runs (it would write the startup
  `gameSettings`, undoing a `save()`).
- `GameSettingsPresetsResource` (min/max/preset blocks, a game resource) was not located in server memory; ranges come
  from the readme.
