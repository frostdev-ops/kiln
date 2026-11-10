# Mod profiles, play-once, chain loading

A **profile** says which mods a game start loads and where their configs live. The Kiln Launcher writes it when you
activate a profile; Kiln reads it at every start, so Steam's own Play button starts the last activated profile too.
Formats: [launcher/contracts.md](launcher/contracts.md) (sections 5.1-5.3, 5.9), `schema/profile.schema.json`.
Native (DLL) mods: [native-mods.md](native-mods.md).

## Where Kiln looks, in this order
| What is there | Mode (`kiln.profile`, boot.json `profile.mode`) | What loads |
|---|---|---|
| `kiln/play-once.ini` with `mode=no-mods` | `no-mods` | Kiln and its services only: no managed, native, plugin, EMF or chained mod |
| `kiln/play-once.ini` with `mode=profile` | `once` | `kiln/profile.once.json` (or the ini's `profile=`) for this start |
| `kiln/profile.json` | `active` | the profile's mods (+ drop-in folders when `dropins` says so) |
| neither | `legacy` | today's drop-in folders: `kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods` |
| a profile file that is not valid | `invalid` | **no mods at all**, an error in `kiln.log` and `state/boot.json` (never the drop-ins instead) |

Dedicated servers keep the legacy layout (profiles are a client feature); a server ignores and deletes a
play-once.ini.

## profile.json in short
```json
{ "format": 1, "id": "ember-run", "name": "Ember run", "generation": 7,
  "configDir": "Z:\\home\\me\\.local\\share\\kiln\\profiles\\ember-run\\config", "dataDir": null, "dropins": false,
  "mods": { "kiln":   [{ "id": "new-items", "version": "1.0.0", "path": "Z:\\home\\me\\...\\new-items\\1.0.0+3f2a9c1d" }],
            "native": [{ "id": "fps-unlock", "path": "Z:\\...\\fps-unlock\\2.0.0+00000000", "enabled": false }],
            "plugin": [{ "path": "Z:\\...\\my-plugin.dll" }], "emf": [] } }
```
- **Paths** are game-side: absolute (`Z:\…` under Proton, real paths on Windows, UNC), or relative to
  `<game>/kiln/` (copy mode: `profiles/<id>/kiln/<mod>`). `/` and `\` both work; `..` is refused.
- An entry whose folder (or plugin DLL) is **missing** is skipped: `kiln.log` warns and boot.json lists it in
  `profile.missing[]`.
- **Other engines' mods** have their own lists (`eml`, `shroudforge`, `set`, `greeneye`, `flame`, `shroudtopia`;
  `enshwick`, `shroudlink` and `ember` are reserved). Each engine's plugin reads its list. ShroudForge and SET mods
  are copied into those engines' own folders at activation. Details: [engines.md](engines.md) "Managing every engine's
  mods" and contracts.md 5.1.
- `enabled: false`: listed but off. Managed mods show as `skipped` ("disabled in profile"), native mods as `disabled`.
  A mod that requires it fails with the reason.
- The profile entry's `id` must be the mod's own id (`kiln.mod.json` / `native.json`); otherwise that mod fails
  ("the profile lists it as 'x' but its kiln.mod.json says id 'y'").
- `configDir` (default `kiln/config`) holds every mod config (`<id>.json`), the offline schemas (`.schema/`), the
  native switches (`native.json`), key bindings, permissions and EMF settings. It is `KILN_PATH_CONFIG` in C and
  `Config.Directory` in C#.
- `dataDir` (default `kiln/data`) is the per-mod data root (`Context.DataDirectory` = `<dataDir>/<id>`; a native
  mod's `data_dir`). World data (`kiln/data/worlds`, `KILN_PATH_DATA`) never moves.
- `dropins`: `false` loads only the listed mods; `true` also loads every drop-in folder after them; a list
  (`["plugin"]`) does that for those kinds. A drop-in whose id the profile lists too is skipped (the profile wins).
  Each engine plugin reads it its own way: see [Drop-in folders](#drop-in-folders).
- Kiln's own plugins (`kiln/plugins/kiln-*.dll`, e.g. `kiln-emf.dll`) always load when `kiln.json` `plugins` is on,
  profile or not (not under play-once no-mods). Other DLLs in `kiln/plugins` are drop-ins.
- Order: the profile's order is not the load order; each loader sorts with its rules (managed: dependencies,
  `loadAfter`/`loadBefore`, ties by id; native: the same over `requires`/`after`/`before`).

## Drop-in folders

The launcher's profile settings have one switch per kind whose plugin reads `dropins`, grouped as Kiln's own folders
and other engines' folders:

| Switch | Folder | Read by, with an active profile |
|---|---|---|
| Kiln, Native, Plugins, EMF | `kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods` | kiln-core, kiln-emf: `true` or a list naming the kind |
| EML | `mods`, `kiln/eml/mods` | kiln-eml: `true` or a list naming `eml` |
| ShroudForge | ShroudForge's mods folder (`mods` unless its config says otherwise) | kiln-shroudforge hosts ShroudForge (which runs that whole folder) when the profile has an enabled ShroudForge mod, or with ShroudForge drop-ins on (`true` or `shroudforge` in the list; the drop-in half from dev.22) |
| Greeneye, Flame, Shroudtopia, shroudlink | `greeneye`, `mods`, `mods`, `shroudlink_mods` | compat.h / kiln-shroudlink: the folder is read when the profile has no mods of that engine, or with `true`. A list does not reach them, so with mods of that engine in the profile the folder needs every switch on |

There is no switch for SET (`shrouded_engine_tweaks/mods` always loads), Enshwick (its loader runs every folder in
`mods`) or Ember (its mods do not run yet).

The launcher's `meta.json` keeps the switches in `dropins`. Since dev.22 it also writes `"dropinsEngines": true`: the
list then names the engine kinds too, and profile.json gets `true` only when every switch is on, `false` when none is,
otherwise the list of the switched kinds (`kiln_manage::profile::Profile::dropins_file`). A profile without
`dropinsEngines` (made before dev.22) loads as before: all four Kiln kinds on means `dropins: true`, every kind's
folder; any other list is written as it is. The settings dialog shows such a profile with every switch on.

## Mods in the game folder

Mod pages tell players to extract a mod into the game folder. The launcher shows those mods on the profile's Mods tab
(every kind) and Engines tab (other engines' kinds) under **In the game folder** (`found_scan`,
`kiln_manage::found::scan`), for the profile the page shows, active or not. Listed: the entries of every folder above,
except copies Kiln placed (`.kiln-library.json`), synced (`.kiln-source.json`) or that kiln-enshwick manages
(`.kiln-managed`), Kiln's own plugins (`kiln-*.dll` and the install receipt's files), `mods/enshwick/` and hidden
entries. The Library's detection says what each is. Each mod gets one state:

| State | When | What the player can do |
|---|---|---|
| Loads | the kind's plugin reads the folder with this profile, or the engine's own loader is in the game folder (`dbghelp.dll`, `powrprof.dll` and the other proxy names, or a loader `kiln/chain.ini [chain]` chains): it runs the folder itself and Kiln's plugin steps aside | nothing |
| Not loaded | drop-ins of that kind are off; a ShroudForge mod without ShroudForge drop-ins or ShroudForge mods; a Greeneye (Flame, Shroudtopia, shroudlink) mod while the profile has mods of that engine and not every switch is on; a mod in a folder its loader does not read (an EMF mod in `mods`); a folder of several mods; Kiln not installed | **Load the game-folder <engine> mods** (that kind's switch on) when that is enough, else **Add to this profile** (`found_add`: the Library imports the folder and the profile lists it, enabled; the player's folder stays) |
| In this profile | the profile lists the same mod (same id, case ignored, or the Library copy was imported from that folder) | nothing: the profile's copy wins |
| Can't run | not a mod Kiln knows, a link, an Ember mod, a DLL over 64 MiB | nothing; the reason is the detection's |

Play shows one line when the active profile leaves game-folder mods out ("3 mods in the game folder do not load with
this profile") and links to the section. The scan is cheap: one listing per folder (at most 512 entries), the
detection's manifests and the DLLs directly in a mod folder, no hashing, no zip opened. A missing folder is empty; an
unreadable one is named under the list, never an error.

## play-once.ini (one start without the profile)
The launcher writes `<game>/kiln/play-once.ini` right before it starts the game:
```ini
mode = vanilla          ; vanilla | no-mods | profile
created = 1762257600    ; unix seconds, or RFC 3339 with a zone
nonce = 4f1c0e3a9b2d4e6f
; profile = profile.once.json   (mode profile; relative to kiln/)
```
The proxy reads it in `DllMain`, **deletes it at once**, then decides:
- `vanilla`: nothing at all (no chained DLL, no Kiln). The game runs as if Kiln were not installed.
- `no-mods`: the chained DLLs are listed but not loaded (`skipped`); Kiln boots without any mod.
- `profile`: Kiln boots from `kiln/profile.once.json`; `kiln/chain/once.ini` replaces chain.ini `[profile]`. It also
  carries the play-once profile's own `[chain]` and `[late]` (dev.21): chain.ini as that profile's compat rules and late
  natives would leave it, so this one start follows them and not the active profile's. A `once.ini` without those
  sections (an older launcher's) leaves chain.ini's in effect.
- Ignored (a normal start), and reported in boot.json `playOnce.mode` and `kiln/logs/proxy.log`: the file could not
  be deleted (`ignored-undeletable`: a flag that cannot be removed must not stick), a dedicated server
  (`ignored-server`), invalid (`ignored-invalid`: unknown mode, missing `created` or `nonce`), older than 600 s or more
  than 60 s in the future (`ignored-expired`).
- `KILN_DISABLE=1` (environment) still turns everything off, play-once.ini included (left in place).

The next start (no play-once.ini) boots the profile again.

## chain.ini: other dinput8 mods
Only one DLL can be `dinput8.dll`. When the installer finds another tool's `dinput8.dll` (EMF's loader, an unknown
proxy), it moves it to `kiln/chain/` and lists it in `kiln/chain.ini`. The proxy loads, in its `DllMain` and before
it installs Kiln's entry gate:
1. `kiln/chain.ini [chain]` (lines before any section count too): the displaced proxies;
2. `<game>/dinput8-chain.ini` (EMF's list, with EMF's rules: only paths inside the game folder);
3. `kiln/chain.ini [profile]` (native mods with phase `dllmain`; the launcher writes it), or `kiln/chain/once.ini`.

```ini
[chain]
chain\emf-dinput8.dll
; chain\old-tool.dll          ; a leading ; switches a line off
[profile]
Z:\home\me\.local\share\kiln\library\native\fps-unlock\2.0.0+00000000\fpsunlock.dll
```
Each DLL is loaded once (same canonical path = `duplicate`). Never loaded, only inspected through a read-only file
mapping: this proxy itself, the system dinput8 / Wine builtins, other Kiln proxies (and kiln-core.dll),
ShroudForge's dinput8 (ShroudForge runs through its dbghelp.dll), files that are not 64-bit PE DLLs, EMF entries
outside the game folder, more than 256 per list. Kiln never unloads a chained DLL. A corrupt profile.json also
skips its `[profile]` DLLs.

Because the chain loads before Kiln's gate, a chained loader that patches the game's entry point (EMF does on a
server) keeps working: Kiln's gate saves that patch as the "original" bytes, runs first, puts the patch back and
jumps to it, so the patches unwind last in, first out.

## What Kiln reports (`<game>/kiln/state/`, `kiln/logs/proxy.log`)
- `logs/proxy.log`: one line per proxy decision (play-once, each chain entry, the gate), rewritten every start.
- `state/boot.json` (end of `kiln_boot`): pid, start time, version, side, exe sha256 + verified, policy, hooks and the
  safe-idle reason, play-once mode + nonce, the profile (mode, id, generation, file, error, configDir, dataDir,
  counts, missing), every `KILN_PATH_*`, the chain results, services, plugins, native summary, .NET, errors.
- `state/mods.json` (Kiln.Host, after every load/unload/reload): each managed mod with state, reason, source
  (`profile`/`dropin`), load index, side, clients, faults.
- `state/native.json` (after each native phase): see [native-mods.md](native-mods.md).
Schemas: `schema/state-{boot,mods,native}.schema.json`. A file whose pid is not running is from an earlier start.

## For mod and service authors
- **Never build config paths yourself.** Use `Config.Register` (C#), `KilnPaths.Config` (Kiln's own assemblies),
  `KILN_PATH_CONFIG` (C). Paths derived from `kiln/config`, from `Config.Directory`'s parent or from the assembly's
  location break under a profile.
- Per-mod data: `Context.DataDirectory` (C#), `KilnNativeContext.data_dir` (native). Kiln-wide data that must not move
  with a profile (worlds, caches): `KILN_PATH_DATA`.
- Finding mods: read the `kiln.profile` service (`native/core/abi/services/kiln_profile.h`): `mode()`, `count(kind)`,
  `get(kind, i, &mod)`, `dropins()` (bit `1 << kind`), `config_dir()`, `data_dir()`. Answered in safe idle too. Then,
  when `dropins()` has your kind's bit, the drop-in folder.
- Tools: `tools/client/client.sh status` shows the active profile, a pending play-once.ini and the last boot;
  `client.sh deploy --mod <folder>` turns on kiln drop-ins in an active profile (undeploy puts it back).
- Tests: `native/spikes/proxyhost/run.py` (proxy, under plain Wine: chain order, refusals, dedupe, LIFO entry
  patches, play-once, GetdfDIJoystick, KilnProxyInfo, every `schema/fixtures/ini` case, the core's semver port),
  `managed/tests/Kiln.Host.Tests/ProfileTests.cs`, `mods/tests/native-test/stage.py` (lab profiles with Z:\ paths).
  Test-only: `KILN_PROXY_TEST_NOW=<unix seconds>` pins the clock the proxy judges play-once.ini by (the fixture
  parity run uses it); it changes nothing else.
