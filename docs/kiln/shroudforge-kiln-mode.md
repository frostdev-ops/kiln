# ShroudForge mods on Kiln: kiln mode

Kiln can run ShroudForge mods in two ways. By default it starts the ShroudForge runtime that comes with Kiln (or your
own installed copy) and lets it run your ShroudForge mods, as described in [shroudforge.md](shroudforge.md). **Kiln
mode** is the other way: Kiln runs ShroudForge-format mods itself, with its own interpreter of the ShroudForge mod API
(the API of ShroudForge 1.7.9), inside Kiln's runtime. No separate ShroudForge runtime is loaded in kiln mode.

Kiln mode is **opt-in**. Nothing changes until you choose it, and you can switch back at any time.

Adapted from ShroudForge by bonsaibauer, MIT.

## Choosing kiln mode

1. Close the game.
2. In the Kiln Launcher open **Settings** and find the **ShroudForge** card (see
   [launcher/shroudforge-backend.md](launcher/shroudforge-backend.md)). Pick **Kiln's interpreter**.
3. The launcher shows what the choice does and asks you to save it. It is stored per game in
   `<game>/kiln/kiln.json` as `"shroudforge": { "backend": "kiln" }` and applies from the next start.
4. Activate your profile and press **Play**.

Choosing **Kiln's interpreter** on the card is all it takes: no other setting is needed.

If the card shows **Kiln's interpreter** greyed out with "Not available in this Kiln build: ...", this Kiln build does
not offer kiln mode, for the reason it gives.

Kiln mode also stands aside (and no ShroudForge mod runs through Kiln) when:
- ShroudForge's own loader is in the game folder (its proxy DLL, `chain.ini` or `dinput8-chain.ini`, or
  `dbghelp=n,b` / `winmm=n,b` in the launch options): ShroudForge then runs your mods itself, and the card says so;
- `KILN_SHROUDFORGE=0` is set in the launch environment;
- `kiln/plugins/kiln-sfi.dll` is missing from the game: install Kiln again to add it.

## What happens at a kiln-mode start

- **Before Play**, the launcher checks every ShroudForge mod in your profile. A mod Kiln cannot run whole is **skipped
  for this start, with its reason**, and the launcher shows a warning before the game opens: what does not run, which
  part (startup data, Lua, DLLs), why, and which other mod it depends on. **Cancel** is the default; **Continue without
  these mods** starts the game without them. Continuing switches nothing off and deletes nothing; the next Play checks
  again. A start from Steam's own Play button skips this dialog: you get only the in-game notice.
- **Startup mods** (mods that change game data before the game starts) run in one pass before the game starts, as
  they do under ShroudForge.
- **In the game**, the Lua side of each mod runs on Kiln's interpreter. Kiln mode has:
  - each mod's lifecycle (`on_load`, updates, `on_unload`), settings, logging, clock, file access inside its own
    folder and `game.types`;
  - **entities and components**: the registry and component catalog reads, the entity queries and component reads
    (`runtime.ecs.query`, `query_bounds`, `resolve`, `read`, `read_bytes`, `read_fields`) and the plain component
    writes (`runtime.ecs.write`, `write_fields`) with the component claim and override target
    (`runtime.overrides.get_component_claim`, `runtime.overrides.component`);
  - **template components**: a view of one component's configuration in the template an entity was spawned from
    (`runtime.ecs.get_template_component`, its `read_fields` and `write_field`, one scalar field per call) and the
    template override target (`runtime.overrides.get_template_claim`, `runtime.overrides.template`);
  - **attributes**: the game's attribute catalog and an entity's attributes (`runtime.ecs.get_attributes`,
    `get_attribute`, `evaluate_attributes`, `read_attributes`, `read_attribute`) and the attribute writes
    (`runtime.ecs.update_attribute`, `write_attribute_storage`, a plain write to an attribute component, the attribute
    override target `runtime.overrides.attribute`);
  - **named world settings**: `runtime.services.get` (for example `ecs::GameSettings`, read and changed field by
    field) with the service claim and override target (`runtime.overrides.get_service_claim`,
    `runtime.overrides.service`);
  - **loaded resources**: `game.assets.get_loaded_resource`, `get_loaded_resources_by_type` and
    `read_loaded_field` (the resources the game has loaded now), the loaded index (`prepare_loaded_index`,
    `get_loaded_resource_by_field`), `runtime.ecs.get_template` (an entity's template as a loaded resource), and the loaded-resource writes (`game.assets.write_loaded_field` and the resource override
    target `runtime.overrides.resource`, `get_resource_claim`);
  - **the world**: the world context, the local player's building cursor (`runtime.world.cursor.get`), the props of
    an area with their transforms (`runtime.world.entity.query_props`, `query_props_in_bounds`,
    `register_prop_recipes`, `get_transform`, `set_scale`), and the voxel grid's geometry
    (`runtime.world.voxel.get_grid_spec`);
  - **building input** (`runtime.world.building.submit`, `status`, `cancel`), one mod's request at a time as under
    ShroudForge; see below for when it works;
  - `runtime.network`, `runtime.overrides`, the UI actions and notifications, `shroudforge.ui.toggle_modloader()`
    and the mods' keyboard and mouse shortcuts.
- **What the world parts need.** The entity, attribute and prop reads need Kiln's world reads, which work on the game
  builds Kiln has an exact engine profile for; on any other build they answer "not verified for game build ...". The
  writes also need Kiln's world writes on that build: they reach only components the game neither tracks nor saves by
  itself, in the world of a host (single player included) or a dedicated server; any other component answers
  "component write context or original command phase is unavailable". `set_scale` is such a write too: where the game
  tracks a prop's transform it answers "CurrentTransform has no supported native component write phase". An attribute
  write first runs the game's own attribute formulas (maxima, clamps) on the attribute's whole group, as ShroudForge
  does, and is refused if another writer changed that component meanwhile; on a game build whose engine profile
  publishes no attribute calculation model it answers "attribute calculations are not validated for this executable
  profile". The named world settings need Kiln's named services on that build (the rows `world.named_read` and
  `world.named_write`) and a world this game hosts. As in ShroudForge, a game client that hosts reads and writes the
  world it hosts; a client that joined another player's world reads its own copy of that world, and its writes answer
  "unavailable". The voxel grid's geometry answers in a world this game hosts or on a dedicated server.
- **What the loaded resources need.** The loaded-resource reads work in a world on the game builds Kiln has an exact
  engine profile for, and answer what the game uses, a Kiln mod's override or new resource included. In the main menu,
  or on another build, a lookup raises ShroudForge's "live ResourceService resource is unavailable".
  `read_loaded_field` reads a field stored inside the resource itself; a field inside a list or a variant answers why
  it cannot. A loaded-resource write changes one such field of a resource in the world a host or a dedicated server
  runs, between frames, only while the resource is still what the mod read; a change another mod or the game made
  meanwhile is kept and the write says so. A template component's `write_field` is such a write to the template
  resource, with the same rules. Each such write names its mod to Kiln (next release): where it changes a field a Kiln
  registry edit writes, the launcher's Conflicts page and `kiln/state/registry.json` name the mod on that field
  (`shroudforge:<mod id>`) instead of "another writer". A resource a Kiln mod overrides is not written
  ("unavailable"): change that mod's copy. The loaded index finds a loaded resource by one 32-bit integer, float or
  enum field (an item's id, for example): the index of a type and field builds in the background over a few frames, a
  lookup before it is complete answers that it is still scanning, and a match is read again before it is returned.
- **Building input** sends a mod's building action (select, place, remove, undo, dismantle) as your own input on game
  client 26430625 (not checked in a real game yet; [Testing kiln mode](shroudforge-kiln-mode-testing.md) says what to
  look for). On a game build without it a submit answers ShroudForge's "building input unavailable, busy, or request
  invalid". Kiln hooks the game's input path only at a mod's first request. A dispatched request is sent as your input;
  the server still decides what happens.
- **Mod DLLs** that a mod ships for ShroudForge's DLL loader are loaded by Kiln at the start, once. A DLL Kiln has no
  reviewed rule for is refused, and its mod is skipped with the reason.
- **ShroudForge's menu key** (F9 by default) and `shroudforge.ui.toggle_modloader()` open Kiln's page for ShroudForge mods
  (**Mods → ShroudForge** in the game). It lists what runs this start, each mod's settings, and its action buttons.
  Settings you change there are saved into the mod's own `extended.mod.json` and apply **at the next start**.
- **Mod shortcuts**: a mod's shortcut (a `keybind` setting, read through `shroudforge.input`) works from the keys the
  game window receives: held, pressed (once per press; holding a key is one press) and its name in your keyboard
  layout. It does not count while the game window is not in front, or while Kiln's menu, a Kiln screen or a Kiln text
  field has the keyboard. The game still sees the key too. To change a shortcut, type it in the setting on Kiln's
  ShroudForge page (for example `Ctrl+SDL:66` for Ctrl+F9); it applies at the next start. On a dedicated server
  shortcuts never count as held or pressed.
- **Live reload**: a reload request for a Lua-only mod reloads it in place. A mod whose files changed since the start, or
  a mod with DLLs, answers that it needs a restart, and the running mods are left alone.

### What answers "unavailable"

Some parts of the ShroudForge mod API are not in Kiln's interpreter yet. A call to one of them does not crash the mod
and never returns `nil` by surprise: it gives ShroudForge's own "unavailable" answer (`nil` or `false` plus a reason,
or an error where ShroudForge raises one), so a mod that checks it keeps running with that feature off. In this release
that covers:
- the world beyond the parts above: reading and writing voxel cells (`runtime.world.voxel.read`, `.write`),
  spawning, placing and destroying entities (`runtime.world.entity.spawn`, `place`, `destroy`, `finish_building`), and
  `runtime.patch`;
- in-game `game.assets` apart from the loaded-resource members above: the archive members and the item registry
  lookup `resolve_loaded_resource` (it answers that the game's item registry resolver is not available); and
  `runtime.assets`;
- the game's own functions (`runtime.functions.*`) and `runtime.impact.call`;
- runtime operations from the startup pass answer "not verified for game build ...";
- anything that needs ShroudForge 1.8 is never offered.

A mod that uses one of these keeps running with that feature off (for example, skeleton-commander's leash). A mod
that needs them for everything it does loads but may do nothing useful until a later Kiln release adds them; switch it
off or use the default backend for it.

## The do-not-save notice

Some mods keep data in your saves (Quiver & Spellbook's ammo, for example). If such a mod does not run this start, or
was running at an earlier start and is now off, **saving a world can lose that data**. Kiln warns you in three places:
- the warning before Play: "Do not save without them";
- in the game, a banner at the main menu ("Do not save: N mods are off this start"), a line above the main menu footer
  ("N mods off: do not save"), a **DO NOT SAVE** badge on the mod's row in **Mods**, and a marker on Kiln's Mods entry
  in the pause menu. The banner comes back when you enter a world, when the list changes and at each save;
- `kiln/state/sfi.json` and `kiln/logs/kiln.log`.

The notice says "Do not save while this mod is off; restore it and restart first." Do that: switch the mod back on,
restart, and only then save. Kiln never blocks or changes a save; the notice is the only protection. Mods that are off
but cannot lose save data are marked "off this start" instead.

Without Kiln's in-game menu (it is missing, or it has not started 10 seconds after the main menu shows), Kiln shows a
simpler banner of its own instead: the same list for 30 seconds at the main menu, again when you enter a world and when
the list changes. With the overlay switched off (`ui.overlay` in `kiln.json`) nothing can show in the game: rely on the
launcher's warning, which the next Play repeats, and on `kiln.log`.

## Switching back

Choose **Kiln's bundled ShroudForge** (or **Back to the default**) on the ShroudForge card, or set
`"backend": "hosted"` in `kiln.json`. Activate and press Play.
Your mods, their settings and your own mod folders stay as they are; the first start after kiln mode makes ShroudForge
prepare its startup data again, which can take a little longer once. Nothing switches back to the default on its own.

## Where to look

| What | Where |
|---|---|
| Kiln's log (this start, last start) | `<game>/kiln/logs/kiln.log`, `kiln.prev.log`; interpreter lines start with `kiln-sfi:` |
| What ran this start | `<game>/kiln/state/sfi.json`: phase (`starting`, `running`, `ended`, `aside`, `refused`), reason, one row per mod |
| The backend this start used | `<game>/kiln/state/launch.json`, and the card's "Last start" line |
| The launcher | **Mods → Engines** shows the interpreter's row; the support report includes the files above |

A skipped mod is logged as `kiln-sfi: WARNING: ShroudForge package '<id>' will not run: <reason>`. When kiln mode does
not run at all, the line is `kiln-sfi: WARNING: Kiln's interpreter does not run this start: <reason>`.

## Known differences

Kiln aims to behave as ShroudForge does. Where it deliberately does not, the difference is listed here.

**Errors and limits**
- Where ShroudForge's own code would crash the game, Kiln raises a Lua error in that mod with a stated cause.
- A single callback that runs more than 10 seconds of Lua time is stopped, and that mod ends for this start (the other
  mods go on).
- Compute-heavy Lua runs slower: Kiln watches every mod so it can stop a runaway one. A tight arithmetic loop is about
  2.5 times slower; ordinary table and string work about 8% slower.
- File access refuses links that lead out of the mod's folder before anything is written.
- An impossible array count in game data is an error, not a huge allocation.
- Some error texts differ slightly (typed-value errors, "failed to convert modified value"); messages for the same
  failures stay close to ShroudForge's.

**Game data and the world**
- A plain ECS write that meets a game frame's change to other fields of the component is compared again against the
  new bytes, up to three compares in all; when the third still meets such a change it answers "busy"
  (`ecs-scene-frame-busy`), and so does a write the frame holds off. A change to a field the write sets or expected is
  refused at once, as ShroudForge does, so an override is never restored over another writer's change. A write whose
  outcome cannot be proven stops the session's ECS writes with ShroudForge's
  `ecs-write-outcome-uncertain-rollback-unverified`. Engine calls refuse code that another mod hooked or patched, and
  stop for the session after a fault.
- ECS queries are complete when they answer: ShroudForge's "still scanning, retry on the next update" never comes.
- A Kiln mod holding the engine gate can, rarely, make a ShroudForge mod see "busy".
- Overrides are never restored into a different world session than the one they were applied in.
- `game.assets` writes in the startup pass refuse to create a resource the game already has, and refuse writes after
  the pass has closed (ShroudForge would drop them silently).
- `runtime.world` context changes follow Kiln's world lifecycle and can be a few frames off ShroudForge's.

**DLLs and starting**
- With `native.policy` set to `never` (or `verified-only` on an unverified build) in `kiln.json`, no mod DLL loads in
  kiln mode; the mods that need them are skipped with Kiln's reason. The default `always` behaves as ShroudForge.
- The 15-second limit for a mod DLL's start includes Kiln's own checks; on a timeout Kiln skips those mods and the game
  goes on.
- If a mod's DLL file is replaced on disk at the exact moment it loads, Kiln notices after it ran, then turns that mod
  off with a do-not-save notice.
- Whether a process is the client or the server comes from the game executable Kiln recognises, not from a file name.

**Settings, UI and reload**
- Settings changed in kiln mode apply at the next start.
- Shortcuts: a key Windows has no name for is shown as `SDL:<number>` in a shortcut's name. Media and browser keys
  (play/pause, volume, browser back and the like) do not work as shortcuts in kiln mode. Whether Kiln's menu has the
  keyboard is checked once per update of the mods, not per key.
- Kiln's package files are edited in place, keeping their formatting.
- UI limits: a 2 MiB message, 1,024 pending actions per mod, 4,096 notification keys. Past a limit the mod gets an
  error instead of losing data silently. An action file that another program holds locked waits for the next turn.
- Live reload uses Kiln's own request files (`<game>/kiln/sfi/runtime/mod-reload-request.json` and
  `mod-reload-result.json`), checked every 100 ms. Only Lua-only mods that were admitted at the start reload; an edited
  mod needs a restart.
- Log lines from the network and UI parts use Kiln's log layout; the texts are the same.

**Multiplayer**
- On a dedicated server running in kiln mode, the connected-player list comes from the server's log and starts empty: on a server with a long
  log it can take a few seconds to fill. At most 256 players are accepted automatically; the rest need a manual accept.

To help test kiln mode, see [Testing kiln mode](shroudforge-kiln-mode-testing.md).
