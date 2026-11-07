# Enshwick mods on Kiln (kiln-enshwick)

Kiln runs unmodified mods for **Enshwick** (MIT, [enshwick/enshwick](https://github.com/enshwick/enshwick)) through
`kiln/plugins/kiln-enshwick.dll`. That covers Rust, C, C++ and Zig DLL mods, Luau mods (`main.luau`) and TOML data
packs. You don't need Enshwick's `powrprof.dll`, and under Proton you don't need the `WINEDLLOVERRIDES=powrprof=n,b`
launch option.

The plugin does not re-implement Enshwick. It **hosts Enshwick's own loader**, `enshwick.dll`, built from Enshwick's
source at a pinned tag (`native/plugins/enshwick/tools/build-runtime.sh`, now `v1.0.0-alpha.13`). The plugin starts
the loader the way Enshwick's stub does. So every API a mod calls (the 193-entry host table, Luau, packs, settings,
the join check) is Enshwick's own code, and a new Enshwick release is a rebuild, not a port.

## Install a mod

Put the mod where Enshwick puts it, `<game>/mods/<author.name>/`, or add it to a Kiln profile (`mods.enshwick`,
see [README.md](README.md)).

- **Profile entries.** Enshwick's loader reads only `<game>/mods/`. For each enabled entry, Kiln copies the library
  folder into `<game>/mods/<id>/` before the loader starts and puts a `.kiln-managed` marker in the copy. When the
  source changes, Kiln copies it again. When the profile no longer enables an entry, Kiln removes its copy. Folders
  without the marker belong to you, and Kiln never changes them.
- **Folders you installed by hand** always run, because Enshwick's loader runs every folder in `mods/`. If the profile
  manages Enshwick and `dropins` is off, `kiln.log` names such folders. To switch one off, move it out of `mods/`
  (Enshwick's own rule).
- **Play-once no-mods** and an invalid profile: the loader does not start.

## Which loader runs

| `kiln/config/kiln-enshwick.json` `"loader"` | Runs |
|---|---|
| `"auto"` (default) | `<game>/mods/enshwick/enshwick.dll`, the loader your mods were installed with, if it is there; otherwise the copy Kiln ships in `kiln/plugins/enshwick/enshwick.dll` |
| `"game"` | only the game's copy |
| `"bundled"` | only Kiln's copy |

A file without the `enshwick_start` export is never loaded. The config file sits in the profile's config folder when
a profile is active.

## How it starts

Enshwick's `powrprof.dll` stub forwards the system DLL's exports, loads `mods/enshwick/enshwick.dll` on a thread
outside the loader lock, and calls:

```c
void enshwick_start(uint32_t stub_abi /* 2 */, uint32_t missing_forwards,
                    uint32_t (*release)(void));  /* 1 held until now, 2 hold ran out, 0 nothing held */
```

On the dedicated server the stub holds the exe's entry point until the loader calls `release` (after its hooks are
armed), for at most 10 s.

kiln-enshwick makes the same call from `on_ready`, which runs on the main thread inside Kiln's own entry gate, before
the game's entry point:

1. **Step aside** when Enshwick's own stub runs in this process. That covers three cases: a `powrprof.dll` from the
   game folder carrying the stub's texts is loaded, the stub's claim `Local\enshwick-stub-<pid>` already exists, or a
   module exporting `enshwick_start` is loaded. In those cases Enshwick runs its mods itself and Kiln starts nothing.
2. Otherwise **take the stub's claim**, so a stub that starts later (for example one chained by another tool) stays
   idle. That is the stub's own rule for two copies.
3. Sync the profile into `mods/` (above).
4. Load the loader with the stub's search flags and call `enshwick_start(2, 0, release)` on a new thread. On the
   server the main thread waits for `release`, for at most 10 s, as the stub's hold does. On the client it does not
   wait, because the loader arms its hooks once the game window shows.

Enshwick's log stays `<game>/logs/enshwick_<date>.log`. For a minute after the start, Kiln copies its key lines into
`kiln/logs/kiln.log` as `[enshwick:log] ...`. `kiln/state/enshwick.json` holds the plugin's status, the loader path and
version, the `release` answer and one line per mod folder (form, origin, action).

A `powrprof.dll` of another tool (SET, Shared XP, Creative Mode's server build, Ember) keeps running. Kiln never takes
or moves that name. If the Enshwick stub is there but did not load (Proton without the override), Kiln logs that and
runs Enshwick itself.

## Settings, worlds and servers

All of these work exactly as under Enshwick, because they are Enshwick's code:

- per-world settings `<save>-mod-settings/<id>/settings.toml`, written from each mod's defaults at world load;
- the Mods screen's Settings button and `enshwick config <mod> <key> <value>` on the server console;
- `require_enshwick`, `recipes.toml`;
- the join check: a server records `<save>-mod-data/enshwick/mods.lock` with each DLL's SHA-256, and a player with a
  different version of a join-check mod does not join;
- `enshwick.toml` in the game folder (log levels, release check).

## Hooks next to Kiln

Enshwick arms its own inline hooks (a 5-byte `jmp` to a relay, all written in one batch with the other threads
suspended). Kiln's hooks (safetyhook) are armed before `on_ready`, so Enshwick hooks on top of them. Its decoder moves
a prologue that already begins with a 5-byte `E9` or a `FF 25` jump into its trampoline, and both chains run.

| Overlap | What happens |
|---|---|
| Both hook one game function | Chained. Enshwick's detour runs first, then Kiln's (the order they were armed). Hooks Kiln installs later (managed `Kiln.Hooks`) chain over Enshwick's in the same way. |
| An Enshwick **code patch** (`patch_code`, bytes compared before writing) on bytes Kiln already hooked | Enshwick skips the patch and turns the owning mod (or the loader feature) off, with the address in its log. Watch for `code patch of ... skipped`. |
| Enshwick's code watch | It compares the game's code to the file on disk. Kiln's hooks show up as changes "before the loader started" in its conflict report. That is only a report. |
| Server console | Enshwick reads the server's stdin. Kiln doesn't (Kiln's console is `kilnctl`), so they don't collide. |
| Main menu "Mods" button, its own UI, its own net channel | Enshwick's own; Kiln's UI and network services don't use the same widgets or message ids. |
| Gameplay rules on one system (both change building cost, loot, rates) | Both apply. Kiln's conflict registry (universal-loader 3.6) is planned to name such pairs. |

## Coverage

Enshwick API 1.0.0-alpha.13: 59 public modules, about 1,270 public functions, 193 host-table entries, 37 Luau
namespaces and 7 data-pack file kinds. Every item is **hosted**: the call goes to Enshwick's own loader inside Kiln,
so coverage is complete by construction.

The "Kiln C#" column names Kiln's own API for the same capability, for Kiln mod authors. **Tested** means:

- **host**: the plugin's Wine host test starts the real loader under Kiln;
- **lab**: an Enshwick mod ran on a dedicated lab server under Kiln;
- **client**: needs the manual client test.

| Enshwick area (host-table entries) | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| Loading: DLL mods (`enshwick_mod_abi/_query/_init/_ready`), resolve, duplicates, `mods.toml` | hosted (kiln-enshwick) | Kiln mod loader (`kiln.mod.json`, dependencies) | hosted; host + lab |
| Luau mods (`main.luau`, sandbox, 37 namespaces) | hosted | C# mods | hosted; lab |
| Data packs (`pack.toml`, items, recipes, workshops, loot, rewards, sounds, texts) | hosted | `Kiln.Blocks` (typed blocks, JSON) | hosted; lab |
| Settings (`config_schema`, `config_check`, `settings`): per-world `settings.toml` | hosted | `Kiln.Config` (per profile; per-world: not yet) | hosted; client |
| Join check (`join_missing`, `hosting`, `mods.lock`) | hosted | `Kiln.Network` mod-set check | hosted; client |
| Hooks and raw access (`hook`, `hook_at`, `patch_code`, `code_range`, `symbol`, `find_call`, `field_offset`, `constant`, `caller_frame`, `fault`) | hosted, chained with Kiln's hooks (see above) | `Kiln.Hooks` | hosted; lab |
| ECS and types (`component`, `component_write`, `system`, `types`, `type_size`, `type_hash`, `resource_read`, `resource_keys`, `rebuild_resource`, `serve_resource`) | hosted | `Kiln.Game` typed components, `kiln.types` | hosted; lab |
| Events and ticks (`subscribe*`, `define_event`, `send_event`, `emit`, `event_types`, `set_tick_every`, `loop_wait`, `task_workers`) | hosted | `Events`, `ModContext.Tick`, `Scheduler` | hosted; lab |
| Services and calls between mods (`publish`, `service`, `serve_call`, `call`) | hosted | `Mods.Send`, `Kiln.Network` ModCalls | hosted; host (no game needed) |
| Extension points (`define_point`, `attach_point`, `run_point`) | hosted | **`Kiln.Points`** (new, ported from Enshwick's model) | hosted; C# unit-tested |
| World, players, entities (`world_*`, `player`, `players`, `entities`, `entity_template`, `spawn`, `destroy`, `teleport`, `server_world`, `server_wait`) | hosted | `Kiln.World`, `Kiln.Players`, `Kiln.Spawn` | hosted; lab |
| Entity and item data (`entity_data_open`, `item_data_*`) | hosted | `EntityData` (`Kiln.Saves`); per-item data: none | hosted; client |
| Saves and state (`save_world`, `save_info`, `hold_saves`, `data_dir`, `set_saved`) | hosted | `WorldData`, `PlayerData`, `ModContext.DataDirectory` | hosted; lab |
| Items, inventory, transactions (`item_*`, `inventory_*`, `transaction_*`, `summon_*`, `local_held`) | hosted | `Kiln.Items` | hosted; client |
| Content (`declare_content`, `declare_change`, `use_content`, `content_*`, `rename_*`, `add_text`, `texts`, `icons_ready`, `workshop_recipes`, `recipe_*`, `crafting_prop`) | hosted | `Kiln.Content`, `Kiln.Blocks` | hosted; client |
| Loot and rewards | hosted | `Kiln.Content` loot, `Kiln.Quests` rewards | hosted; client |
| Stats, buffs, affixes, damage (`stat_*`, `buff*`, `apply_buff`, `remove_buff`, `affix_*`, `damage_filter`, `stats_changed`) | hosted | `Kiln.Combat`, `Kiln.Abilities`; affixes: none | hosted; client |
| Progress, knowledge, skills, experience, comfort (`knowledge*`, `grant/revoke_knowledge`, `skill_override`, `experience_filter`, `comfort_filter`, `quests`) | hosted | `Kiln.Skills`, `Kiln.Players`, `Kiln.Quests` | hosted; client |
| Rates and game settings (`rate`, `set_rate`, `game_settings`, `set_game_settings`, `keep/forget_game_settings`) | hosted | `Kiln.Rules`, `Kiln.Settings` | hosted; lab |
| Vetoes and filters (`craft_filter`, `build_filter`, `build_rights`, `interaction_filter`, `building_zone_check_filter`, `chat_filter`) | hosted | cancelable `Events`, `Kiln.Rules` | hosted; client |
| Altars, bases, survivors (`altar*`, `base_*`, `raise_base_limit`, `request_base_limits`, `survivor_*`) | hosted | `Kiln.Build` (partial); survivors: none | hosted; client |
| Daytime, weather (`time_of_day`, `set_time_of_day`, `time_speed*`, `weather_at`, `weather_fronts`, `set_weather`) | hosted | `Kiln.Time` | hosted; lab |
| Terrain, voxels (`terrain_*`, `voxel_read`) | hosted | `Kiln.Build`, `Kiln.Blocks` | hosted; client |
| Map, fog, markers (`map_*`, `fog_*`, `ui_markers`) | hosted | `Kiln.Ui` map markers | hosted; client |
| UI (`ui_frame`, `ui_widget`, `ui_dialog`, `ui_hud`, `ui_extension`, `notify`) | hosted | `Kiln.Ui` | hosted; client |
| Controls, input (`control`, `actions`, `run_action*`) | hosted | `Kiln.Client` input | hosted; client |
| Chat and console (`chat`, `chat_mute_*`, `run_console`, `account_*`) | hosted | `Kiln.Chat`, `kilnctl` commands | hosted; lab |
| Net between a mod's server and client (`net_channel`, `net_send`, `net_ready`) | hosted | `Kiln.Network` | hosted; client |
| Sound (`sound`) | hosted | `Kiln.Audio` | hosted; client |
| Pacing (`pacing`, `set_change`, `change_data`) | hosted | none | hosted; lab |
| Status lines (`status`), mods list (`mods`, `mod_dir`, `host_version`, `game_version`, `language`, `feature`) | hosted | `Mods.All`; status lines: none | hosted; host |
| Logging (`log`, `log_level`) | hosted; key lines mirrored into kiln.log | `ModContext.Log` | hosted; host + lab |

Counts: 33 areas, 33 hosted. Of the 193 entries, 193 are served by Enshwick's own code. On Kiln's side,
`Kiln.Points` is new. Per-world settings, per-item data, affixes, survivors, status lines and pacing have no Kiln C#
equivalent yet.

## Updating to a new Enshwick release

Enshwick publishes an alpha every few days. To move to one:

1. Rebuild: `native/plugins/enshwick/tools/build-runtime.sh --tag <tag>`.
2. Check that the start contract hasn't changed: `STUB_ABI` in `crates/internal/stub/src/lib.rs` and the
   `enshwick_start` signature in `crates/internal/loader/src/lib.rs`. A new stub contract means a new `kStubAbi` in
   `src/logic.h`. The loader logs `stub contract N, the loader speaks M` when they differ.
3. Run the host tests and a lab start.

Enshwick finds its game entries per build by patterns. It needs no file per game build, so a game update doesn't
need a Kiln change for Enshwick.

## Tests

- `c++ -std=c++20 -o logic_test native/plugins/enshwick/tests/logic_test.cpp && ./logic_test`: the decisions (folder
  names, step aside, loader choice, profile sync plan, markers, log mirroring).
- `KILN_OUT=<out> native/build.sh kiln-enshwick compathost`, then `KILN_OUT=<out> python3
  native/plugins/enshwick/tests/run.py`: 13 Wine scenarios with a stand-in loader. Set `ENSHWICK_REAL=<enshwick.dll>`
  to add one that starts the real loader.
- Lab: deploy Kiln with `tools/deploy-server.sh` (it ships `kiln/plugins/enshwick/` when the runtime was built), put
  a data pack or Luau mod into `<server>/mods/`, start, and read `kiln/logs/kiln.log` and `logs/enshwick_*.log`.
