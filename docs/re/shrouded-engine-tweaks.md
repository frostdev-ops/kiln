# Shrouded Engine Tweaks (SET) 0.1.1: inventory, techniques, and gaps against Kiln

SET (Nexus mod 303, by Cacaida, MIT, github.com/Cacaida/Shrouded-Engine-Tweaks) is a client-only Lua 5.4 mod framework
for Enshrouded with a Dear ImGui overlay. It ships as one `powrprof.dll` (the game imports `POWRPROF.dll`), with
MinHook, Lua 5.4.9 and ImGui inside. Sources for this note: the 0.1.1 release's docs (`README.txt`, `installation.md`,
`usage.md`, `modding-guide.md`, `api-reference.md`), SET's public `docs/architecture.md`, `docs/reflection-format.md`
and `runtime/game.{h,cpp}` (helpers in the Lua adapter are adapted with MIT notices), and offline checks of SET's techniques against the current game
binaries (server Steam build 26430253, client 26430625; both svn 1287369).

The safety review of commit `4116d90` fetched upstream `master` through GitHub and compared `runtime/game.cpp`
(blob `b7db32c4592e4d12bb08bb5a6bdcd88b9312ab63`), `lua_api.cpp`
(`dd63f09d7a205922684b1bf773b90ea2c269a6f0`), `hooks.cpp`, `main.cpp`, `lua_imgui.cpp`, and the architecture and
reflection-format docs. Upstream is the semantic reference, not a security guarantee: Kiln now adds stricter
file-handle containment, script budgets, component-size checks, event payload validation and system restrictions.
The lab observations below describe the original implementation. The review fixes are verified offline; they have
not been rerun in a live server or client. Current contracts are in [kiln.types](../kiln/services/types.md) and
[kiln-set](../kiln/set.md).

## API coverage
SET function → Kiln C# equivalent → kiln-set implementation → status. "lab" = checked on a dedicated-server lab
(svn 1287369, `mods/tests/set-test` and `mods/tests/types-test` PASS lines); "built" = compiles and is wired, not run;
"client" = needs the real game (manual checklist).

| SET | Kiln C# | kiln-set | Status |
|---|---|---|---|
| `Ecs.get(id, name)`, field reads | `Reflected.Component(e, name)[path]` | SET code on kiln.world `component` | lab (124 Health entities) |
| checked writes (range, enum by name) | `ReflectedValue` setter, `GameValueCodec` | SET code (`store`/`fill`) | lab (write 20→19→20; 2^40 refused) |
| `Ecs.each(n1..n8)` | `Reflected.All(name)` | kiln.world `find` + per-entity check | lab |
| `Ecs.player()` | `Players` (Kiln.Players) | first entity with `PlayerInput` | lab (nil without players) |
| `Ecs.fields`, `Ecs.types` | `GameTypes.Find(..).AllFields`, `GameTypes.Search`, `/type`, `/types` | kiln.types | lab |
| `Types.fields`, `Types.enum` | `GameType.AllFields`, `GameType.EnumValues` | kiln.types | lab |
| `Types.new`, `Types.copy` | `GameType.Defaults`, `ReflectedValue.Bytes` | SET code, defaults from kiln.types | built |
| `Observe(event, fn)` | `GameEvents.Each(type, fn)` | kiln.types `event_each` | lab |
| `Events.post` | `GameEvents.Post(type, fields)` | kiln.types `event_post` (hash checked) | lab (posted 42, observed 42) |
| `Hooks.observe`, `Hooks.remove` | `Hooks.Mid` (Kiln.Hooks) | kiln.hooks mid hook at entry, bounded queues | lab (74 captures, 0 dropped); remove built |
| `Systems.list/enable/enabled` | `GameSystems.All/Search/SetEnabled`, `/systems`, `/system` | kiln.types runner detour; disabling restricted to two leaf systems, owner scoped | original lab; restrictions host tested |
| `Systems.profile/stats` | `GameSystems.Profile/Stats`, `/systems top` | kiln.types | lab (236 systems timed) |
| `Res.get/find/each/types` | Kiln.Assets, Kiln.Content catalogs | kiln.assets `resource_list/resource_data` + reflection | lab (get, find); each/types built |
| `Res` edits | kiln.assets overrides (copy-on-write) | in-place write, page protection lifted for the bytes | built |
| `Res.text`, English names | Kiln.Content names (items, templates) | missing (nil) | missing |
| `GameOptions.machine/user/dump` | none | missing ("game options not found yet") | missing (client only) |
| `Tick.add`, `onUpdate` | `Context.Tick.Pre/Post` | Kiln Pre tick (server), UI tick (menu) | lab |
| `registerForEvent` onInit / onWorldEnter | `Mod.OnLoad`, world events | SET code, Kiln ticks | lab |
| onWorldLeave / onShutdown | world events, `OnUnload` | `KILN_WORLD_STOPPING`, `on_unload` | built |
| onOverlayOpen/Close, `Overlay.isOpen` | `Ui` | kiln.ui frame (`menu_open`) | client |
| `reload()` | `kiln/control reload <id>` | SET code | built |
| `Keys.on/hotkey/input` | `KeyBindings.Register` | SET code (key polling, bindings.ini) | client |
| `Draw.add`, `ImGui.*` (21 functions, constants) | `Ui.Window`, `UiDraw` | kiln.ui widget table (`lua_imgui.cpp`) | client |
| `Admin.setTime` | `Time` (Kiln.Time) | kiln.time | lab (19.7 h → 12.5 h) |
| `Admin.settings`, `changeSettings` | `Settings` (Kiln.Settings) | kiln.settings, ranges checked | lab (1 → 0.75 → 1; 99999 refused) |
| `Admin.teleport` | `Teleport` (Kiln.World) | kiln.world `teleport` | built (needs a player) |
| `Admin.spawn` | `Spawn` (Kiln.Spawn) | kiln.spawn | lab: refuses cleanly without a player |
| `Admin.give` | `Items.Give` (Kiln.Items) | kiln.items, by GUID or debug name | built (English names missing) |
| `Admin.setLevel`, `setFlameLevel`, `send` | Kiln.Players progression, world progress | missing (error says so) | missing |
| `GetMod`, `GetVersion`, `GetGameBuild`, `require`, `io`, `json`, `print`, sandbox | .NET | adapted SET code; mod-local file handles, isolated libraries and instruction/deadline limits | original lab; hardening host tested |
| Console window | `kiln/control/console/*.cmd`, `kilnctl cmd` | `kiln/control/set/*.lua` files | lab |
| Systems window | **Kiln: Systems** window | Kiln's window | client |
| Types browser | **Kiln: Types** window | - | client |
| Bindings, Resources, Hooks, Game Log, Settings/Status, ImGui Debug windows | Kiln menu Keys tab (bindings only) | missing | missing |

## 1. What SET is
- **Loader:** `powrprof.dll` proxy (forwards all 126 exports of the system DLL). At load it runs every `.dll` in
  `shrouded_engine_tweaks/plugins/` in name order, so other mods that also ship as `powrprof.dll` keep working.
- **Overlay:** a Vulkan layer inside the same DLL, enabled for this process only (environment variables + a manifest
  in `shrouded_engine_tweaks/layer/`, rewritten at each start). Opens on a key the player picks at first start.
- **Lua:** one Lua state for all mods; each mod has its own globals (`_ENV`), the shared API is read-only. Tick
  callbacks run on the server world's tick in a hosted world (on the main thread per frame in the menu), Draw callbacks
  on the render thread. One mutex serializes Lua; the render thread only try-locks and redraws the last frame.
- **Scope:** client only, and only in a world the player hosts (single player counts). In a world someone else hosts,
  mods run at "main-menu level". There is no dedicated-server support.

## 2. Feature inventory

### Overlay windows
| Window | What it does |
|---|---|
| Console | Lua REPL run at the next tick; expressions print their value; history kept in `console_history.txt`; 2000-line output with green/yellow/red levels, Ctrl+F filter (`a,b`, `-a`), Clear, Auto-Scroll |
| Bindings | every `Keys.hotkey`/`Keys.input` a mod registered, per mod; bind by press with Ctrl/Shift/Alt; exact-modifier matching; `bindings.ini`; "Remove dead bindings" |
| Resources | all 91,812 resources of 146 types; per-type list, word filter, "Group by" any enum field (`category`, `family`), field tree with in-place edit (numbers/bools/enums), Copy GUID, Go on references, English text for text refs |
| Systems | all 861 systems; untick = the game stops running it (every world, until re-ticked); word filter; Off Only; Turn All On; `exe+RVA`; Profile (CPU ms per second per system, calls/s, server/client/both), Log Top 30 |
| Hooks | `Hooks.observe` targets: address, calls/s, per-subscriber active/handled/overflow/discards/queue high-water/lock contention, optional capture timing, last 32 faults |
| Game Log | follows `enshrouded.log` live, warnings yellow, errors red, state-machine noise filtered, 5000 lines, Ctrl+F |
| Settings | `settings.ini` editor (overlay key, fps cap, antialiasing off, async compute, console window, history); **Dump Game Options**; **Status** (what was found at startup, green/yellow/red, runtime hook refusals) |
| ImGui Debug | style editor (save/load/default style), Metrics, ID Stack Tool |
| Reload Mods | asks, then restarts every mod from `init.lua` in a fresh Lua state |

Other UX: first-start **overlay key picker** (refuses keys that type text or close menus, and F10; ignores keys held
with Ctrl/Alt/Shift so another app's hotkey cannot become the overlay key; the game gets no input until a key is
picked; 5-s corner hint at every start); `(!)` in the toolbar at the worst status; frame-rate cap; antialiasing "off".

### Lua API
| Area | Functions |
|---|---|
| Ecs | `Ecs.player()`, `Ecs.get(id, "Component")` (short or full name; view re-resolves on every access), `Ecs.each(n1..n8)`, `Ecs.fields(type)`, `Ecs.types(search)`; checked writes (range, enum by name or number, whole structs from tables); wrapper structs (`{value}`, `{id}`, `{index}`) read as plain values |
| Events | `Observe("EventType", fn)` (all events of the type, after each server tick, read-only), `Events.post("EventType", {fields})` (game's own queue, checked) |
| Native hooks | `Hooks.observe{name, pattern, args, queue, fn}` (entry observation only, ≤ 8 Win64 args, typed captures incl. reflected structs ≤ 1 KiB, bounded queues, delivery next tick), `Hooks.remove(id)` |
| Res | `Res.get(guid [, type])`, `Res.each(type)`, `Res.find(type, name)` (debugName, name or English name), `Res.text(guid)`, `Res.types(search)`; checked in-place edits (numbers, bools, enums, references as GUID strings) |
| GameOptions | `GameOptions.machine()`, `GameOptions.user()` (live client options; changes apply like the menu, not saved), `GameOptions.dump([prefix])` (with defaults) |
| Systems | `Systems.list([search])`, `enable(name, on)`, `enabled(name)`, `profile(on)`, `stats()` |
| Types | `Types.fields(name)`, `Types.enum(name)`, `Types.new(name [, values])` (from the game's default value), `Types.copy(view)` |
| Tick/lifecycle | `Tick.add(fn(dt))`, `registerForEvent(onInit, onUpdate, onDraw, onOverlayOpen, onOverlayClose, onWorldEnter, onWorldLeave, onShutdown)`, `reload()` |
| Keys | `Keys.on(key, fn)`, `Keys.hotkey(id, label, fn)`, `Keys.input(id, label, fn(down))` (aliases `registerHotkey`, `registerInput`) |
| Draw/ImGui | `Draw.add(fn)`, `Overlay.isOpen()`, `ImGui.Begin/End/BeginChild/EndChild/CollapsingHeader/SetNextWindowPos/Size/Text/TextColored/Separator/SameLine/Spacing/Button/Checkbox/SliderFloat/SliderInt/InputFloat/InputInt/InputText/IsItemHovered/SetTooltip`, `ImGuiCond`, `ImGuiWindowFlags` |
| Admin | `Admin.setTime(h)`, `teleport(x,y,z[,exact])`, `setLevel(n)`, `setFlameLevel(n)`, `spawn(template[,count[,level]])`, `give(item[,count[,level[,rarity]]])`, `settings()`, `changeSettings{...}`, `send(command, fields)` (raw admin commands) |
| Mods/files | `GetMod(name)`, `GetVersion()`, `GetGameBuild()`, `require(name)` (mod folder, cycle-checked), `io.open`/`io.lines` (own folder only), `json.encode/decode`, `print`; sandbox: no `debug`, `os.execute`, `loadfile`, bytecode refused; `mod.json` `{requires, builds}` |

## 3. How SET finds game internals (and whether it holds on the current builds)
Checked offline with scratch scripts over the current exes (read-only).

| Internal | SET's technique | Server 26430253 | Client 26430625 |
|---|---|---|---|
| **Type registry** | A static table of `TypeMetadata*` in `.rdata`, found without code patterns: the descriptors of `"BlobString"` and `"uint32"` sit next to each other at the table start; the qword pointing at the start begins `{TypeMetadata** types, u64 count}`. Descriptor 0x90 B: name, impact name, qualified name (ptr+len at 0x00/0x10/0x20), base/inner type 0x38, size 0x40, align 0x44, field count 0x48, kind 0x4C, flags 0x4D, FNV-1a hash 0x50, fields 0x58 (stride 0x30: name, type 0x10, offset 0x18), enum values 0x60 (stride 0x28: name, value 0x10), variants 0x68, default value bytes 0x70, attributes 0x80 | 11,184 types | 12,658 types |
| ECS component / event lists | `{"ecs.component", list, 687}` and `{"ecs.event", list}` beside the table header | 687 / 132 | 687 / 132 |
| **System list** | `.rdata` descriptors `{char* name, u64 len, fn}`: name in `.rdata`, NUL after it, lower_snake_case, fn in `.text` | 652 systems | 861 systems |
| **System runner** | one function runs every program slice `(world, job, index, slot)`; the update is `*(*(job+0x10)+0x28)`; that fn identifies the system. Pattern `48 89 5C 24 08 48 89 74 24 10 48 89 7C 24 18 4C 89 64 24 20 55 41 56 41 57 48 8D AC 24 70 F8 FF FF 48 81 EC 90` | 1 match | 1 match |
| World tick | `40 55 53 56 41 55 48 8D AC 24 F8 FF` (Kiln's `SimStep`, already hooked by the core) | 1 match | 1 match |
| Components | entity map `world+0x148`, component map `*world+0x88`, archetype `rec+0x18`, offsets/strides at `arch+0x84`/`+0xa84` | Kiln's kiln.world already does this through `GetComponent` | same |
| Events | the event map and streams from a function that loads both: `4C 8B FB 0F 86 ?? ?? ?? ?? 48 8B 45 40 4C 8D B7 {map:d32} 49 8D BD {streams-0x28:d32}`; streams stride 0x48, 0x800-byte pages; posting appends `{u32 hash, u16 0, u8 size, u8 0}` + payload to poster 0 after checking the hash is known | 1 match | 1 match |
| Admin commands | detours the `admin_player` system (pattern `48 89 4C 24 08 55 41 54 48 8D AC 24 18 E9 FF FF B8 E8 17 00 00`) and writes queued commands into the host's command component right before it runs, bumping the version counter; `inventory_actions` the same way for give | 1 match | 1 match |
| Resources | KFC4 index read from disk; the resident resource block at fixed `0x20000000000`; writes lift page protection for their own bytes only, then restore it | kiln.assets uses the game's own resource service slots | same |
| Game options | app pointer `+0x128` → options; machine `+0x9c0`; 16 user slots found from the slot getter (slot stride moves between builds: 0x308 → 0x310) | n/a (client) | options-update pattern: 1 match |
| English names | the localization table, located through the KFC content map; text ids = FNV-1a of the 16 GUID bytes | | |

Everything except the two table scans uses code patterns that match exactly once. SET matches them against the exe
**file on disk with relocations applied**, so another mod's detour cannot hide a target (falls back to live `.text`).

## 4. Gap table: SET against Kiln
Kiln before this work; section "API coverage" above has what is done now. Size: S < 1 day, M 1-3 days, L > 3 days. Risk: what breaks on a game update.

| SET feature | Kiln equivalent | State | Size | Risk |
|---|---|---|---|---|
| Ecs.get by name, field reflection | kiln.world `component(entity, hash)` (raw bytes), Kiln.Game generated structs (9 types, one build), per-service TypeByHash readers (content, settings, players, creatures) | partial: no generic reflected access by name, no checked writes | M | low (static table, no patterns) |
| Ecs.each, Ecs.player | kiln.world `find`, Kiln.Players | has | - | - |
| Ecs.types / Types.fields / enum / defaults | none generic (TypeByHash needs a known name) | missing: no enumeration of the 11-12k types, no default values | S | low |
| Type/field browser window | none | missing | S | low |
| Systems list, enable/disable | kiln.world registers its own programs; no list of the game's | missing | M | medium (runner pattern) |
| Per-system profiling | Kiln's tick-stats (whole step only) | missing | S (with the above) | medium |
| Resources browser / Res.* | kiln.assets (by key, by type, data), kiln.gamedata/content catalogs (items, recipes, templates) | partial: no generic browser over every type, no reflected field view | M | low |
| Resource in-place edits | kiln.assets overrides (copy-on-write, safer), kiln.content field patches | has (different model: overrides, not pokes) | - | - |
| Res.text (English names) | kiln.gamedata names for items/templates | partial | S | low |
| GameOptions machine/user, dump | none (Kiln.Settings is the *world* settings) | missing, client only | M | medium (slot offsets moved between builds) |
| Events observe (any ECS event by name) | kiln.gameplay subscribes a fixed set of events; event bus for Kiln's own events | partial | M | medium (events pattern) |
| Events.post (any ECS event) | kiln.gameplay `inject` (some events), kiln.world programs with `events_out` | partial | M | medium |
| Hooks.observe from scripts | kiln.hooks (`scan`, `detour`, `mid`, `read`/`write`) from C# and C | has (more powerful: can change calls) | - | - |
| Hooks window | `kiln.hooks.stats` (no window) | partial | S | low |
| Tick / lifecycle | `Context.Tick.Pre/Post`, `Mod.OnLoad/OnUnload`, world events | has | - | - |
| Keys / Bindings window | `KeyBindings`, the Kiln menu's Keys tab (remap by press, keybinds.json) | has | - | - |
| Draw / ImGui | `Ui.Window`, `UiDraw`, `Hud`, kiln.ui C table | has | - | - |
| Console (REPL) | `kiln/control/console/*.cmd`, chat commands, `kilnctl cmd` | partial: no in-overlay console window | S | low |
| Game Log window | `Context.GameLog.Line` (managed), kiln.log mirror | partial: no window | S | low |
| Status window | `kiln/state/*.json`, the Kiln menu's Mods tab, sigdb report | partial | S | low |
| Admin setTime | Kiln.Time `set_time_of_day` | has | - | - |
| Admin teleport | kiln.world `teleport` | has | - | - |
| Admin spawn | Kiln.Spawn `spawn` | has | - | - |
| Admin give | Kiln.Items `give` | has | - | - |
| Admin setLevel | Kiln.Players progression | has | - | - |
| Admin setFlameLevel / settings / changeSettings | Kiln.Settings (world settings, flame level) | has | - | - |
| Admin.send (raw admin commands) | none | missing (low value; typed services cover it) | M | medium |
| Reload Mods | `kiln/control/reload <id>`, collectible contexts | has | - | - |
| mods/files/json/require | .NET mods (System.IO, System.Text.Json); per-mod config/data dirs | has (different language) | - | - |
| Lua mods | kiln-emf runs EMF's Lua mods through EMF's own `lua` mod | missing for SET's API | L | see section 6 |
| Proxy chain-load (`plugins/`) | proxy chain (`kiln/chain.ini`, `dinput8-chain.ini`) | has | - | - |
| Dedicated server | Kiln's main target | Kiln has, SET has not | - | - |

## 5. What SET does better, and what Kiln adopts
1. **Reflection from the static table.** SET enumerates every type (names, sizes, fields, base classes, enums,
   *default values*) from `.rdata` with two string anchors and no code pattern. It survives code moves and works
   before any game function ran. Kiln's readers call `TypeByHash` with a name they already know. Adopted: the new
   `kiln.types` service scans the same table (with its own sanity checks) and exposes it to every service and mod.
2. **System list from `.rdata` names** and one runner detour for enable/disable and profiling. Adopted in
   `kiln.types`; the idle path has two atomic checks. Unlike upstream, disabling is restricted to
   `stamina_depletion_sprint` and `fall_damage_infliction`, with owner-scoped unload cleanup.
3. **Checked writes:** every write is checked against the reflected type (range, enum names, fixed-size structs
   only), for components, resources, options, events and admin commands alike. Adopted for Kiln's reflected access.
4. **Clean-code scanning:** patterns are matched against the on-disk `.text` with relocations applied, so another
   mod's detour cannot hide a target. Kiln's sigdb scans the live image; worth adding to the core resolver (S, not
   done here).
5. **Overlay key picker:** refuses typing keys and F10, ignores modifier-held keys, blocks game input until picked.
   Kiln defaults to Insert with remap in the Keys tab; the modifier-held rule is worth copying into Kiln's remap (S).
6. **Plugins chain-load:** same idea as Kiln's `chain.ini`; SET's is zero-config (a folder). Kiln and SET use
   different proxy names (`dinput8.dll` vs `powrprof.dll`), so they never displace each other.
7. **Status rows** with green/yellow/red per capability and `[W]`/`[E]` tags in the log: Kiln has the data in
   `state/*.json`; a status page in the Kiln menu would be the equivalent (S).

### "Stability Fixes" (0.1.1): game pitfalls they reveal
- Posting an event with an **unknown type hash crashes the game's distributor**: check the hash against the world's
  event map first.
- Resource pages are **read-only after load**: lift protection for exactly the written bytes and restore it.
- Program updates must **iterate the whole slice** (Kiln already knows: "Not all entities were updated" is fatal).
- An unreadable pointer in a hook capture: disable the target, keep calling the original (never fault in a detour).
- Lua and the render thread must not block each other: the render thread only try-locks.
- Options user-slot stride moved between builds (0x308 → 0x310): never hard-code it; derive it from the getter.

## 6. Running SET's Lua mods in Kiln
Recommendation taken: build it (the loader's goal is to run every engine's mods). The cheapest faithful route was to
reuse SET's own Lua layer (MIT, notice kept) and give it a Kiln game layer. Safety hardening intentionally changes
unsafe script and system-control behavior; compatibility is subject to the restrictions in the service docs.
Risks: SET's ImGui windows and keys are untested in the game; `GameOptions`, English
names and three Admin commands remain. Built as the plugin `kiln-set` (`native/plugins/set/`, docs/kiln/set.md): a Lua 5.4 runtime with SET's API on
Kiln's services and `kiln.types`. Mods load from `<game>/shrouded_engine_tweaks/mods/<name>/init.lua`. When SET's
own runtime is detected, kiln-set steps aside; detection also conservatively refuses non-system power proxies and
checks modules loaded after startup. The API coverage table and what
remains are in docs/kiln/set.md.
