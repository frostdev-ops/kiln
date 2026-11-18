# Other mod loaders on Kiln

Kiln loads mods written for other Enshrouded mod loaders through compatibility plugins in `kiln/plugins/`. Each
plugin reads the other loader's mod format and gives its mods the same API that loader gives them, either built on
Kiln or by hosting that loader's own code. You keep the mods as they are; you do not need the other loader's proxy DLL.
Players' guide: the frostdev.io wiki page "Mods for other frameworks" (`/kiln/wiki/other-mods`).

| Engine | Plugin | Mods it loads | Page |
|---|---|---|---|
| EML (Enshrouded Mod Loader) 0.2.0 | `kiln-eml.dll` (GPL-3, EML's own runner built from kfc-parser; component `eml`, default on) | `mods/<id>/` and `kiln/eml/mods/<id>/` with `mod.json` + `src/mod.lua` (or a zip), patched before the game starts; the single game-data writer unless ShroudForge owns startup | [../eml.md](../eml.md) |
| ShroudForge (bundled 1.7.9 from frostdev-ops/shroudforge, or the player's own copy) | `kiln-shroudforge.dll` (MIT) + `kiln/plugins/shroudforge/` (GPL-3 runtime; component `shroudforge`, default on) | ShroudForge's mods folder (`mods/<id>/` with `extended.mod.json`); with SF hosted it also writes the EML packages in that folder that its switch has on (Kiln's copies carry it) | [../shroudforge.md](../shroudforge.md), [../shroudforge-compat.md](../shroudforge-compat.md) |
| SET (Shrouded Engine Tweaks) 0.2.0 | `kiln-set.dll` | `shrouded_engine_tweaks/mods/<name>/init.lua` (a mod's `settings.json` is written next to it); also on dedicated servers | [../set.md](../set.md) |
| Greeneye Loader 1.2.2 (API 2) | `kiln-greeneye.dll` | `greeneye/<id>/` with `mod.json` and a DLL exporting `GreeneyeModStart` | [greeneye.md](greeneye.md) |
| Flame (Creative Mode's loader) | `kiln-flame.dll` | `mods/<id>/` with `flame.json` and a DLL exporting `FlameStartRuntime` | [flame.md](flame.md) |
| Shroudtopia 0.1.1 / 1.0 r1 | `kiln-shroudtopia.dll` | `mods/*.dll` and `mods/<name>/` exporting `CreateModInstance` | [shroudtopia.md](shroudtopia.md) |
| EMF 0.3.3 through 0.6.14 (EmfApi v46: 76 full, 17 partial, 49 not yet of 142 C slots; client checks remain) | `kiln-emf.dll` (component `emf`, default on) | `emf/mods/<id>/`, `emf/mods/<name>.dll`; Lua mods through EMF's `lua` mod. API coverage does not guarantee every mod pairing | [../emf.md](../emf.md), [../emf-parity.md](../emf-parity.md) |
| Enshwick 1.0.0-alpha.14 | `kiln-enshwick.dll`, hosting Enshwick's own loader: the game's `mods/enshwick/enshwick.dll` when present, else the copy Kiln ships in `kiln/plugins/enshwick/` (built from Enshwick's MIT source; `"loader": "auto"`), so Enshwick need not be installed | `mods/<author.name>/`: DLL mods, `main.luau`, `pack.toml` data packs | [enshwick.md](enshwick.md) |
| shroudlink 0.8.0 (SDK ABI 8), server | `kiln-shroudlink.dll` (shroudlink's own server core, built from source) | `shroudlink_mods/*.dll`; EML data mods go to kiln-eml | [shroudlink.md](shroudlink.md) |
| Ember (server SDK, no ABI published yet) | `kiln-ember.dll` (finds and reports; starts nothing yet) | `ember/mods/<mod>/` with one library | [ember.md](ember.md) |

Stand-alone `dinput8.dll` mods (and other loaders' `dinput8.dll`) are chained from `kiln/chain/` by Kiln's proxy, which
also reads `dinput8-chain.ini` `[Chain]` and `[EMF]` ([../engines.md](../engines.md)). Kiln's own DLL mods
(`native.json`) are not another engine.

The launcher's Library imports every kind above, Enshwick, shroudlink and Ember included; Ember mods are listed but not
run until Ember publishes its loader ABI.

Running such a mod runs that mod's own code in the game process, as with any native mod. Install mods only from
sources you trust.

## Where mods are found

The same rule for every engine:

1. **The profile's list.** A Kiln Launcher profile can list mods of an engine under `mods.<engine>` in
   `kiln/profile.json`, each entry `{ "id", "path", "version", "enabled" }` as for the other kinds (`path` is the mod
   folder; for Shroudtopia also a DLL). They load first, in the profile's order. `"enabled": false` keeps a mod off.
2. **The engine's own folder** (`greeneye/`, `mods/`) is read too when the profile has no `mods.<engine>` list (the
   launcher does not manage that engine, so what you installed by hand keeps working), when the profile has
   `"dropins": true`, or when there is no profile at all.
3. Play-once **no-mods** and an invalid profile load nothing.
4. A mod listed in the profile wins over a folder with the same id.

## When the other loader is installed too

Each plugin checks whether the engine's own loader runs in the same process, and **steps aside** when it sees it before starting mods. The log says `<plugin> steps aside: ...`. When the other loader's
files are in the game folder but did not start (for example under Wine or Proton, where those proxy names are not
loaded), the plugin says so and loads the mods itself.

| Engine | How Kiln knows its loader runs | Why stepping aside keeps the setup working |
|---|---|---|
| EML | a module named `dbghelp.dll`/`dinput8.dll` (or `dbghelp_*.dll`/`dinput8_*.dll`) or under `kiln\chain\`, outside `\windows\`, whose file holds `EML_LOG_FILE_PATH` (ShroudForge's carries it too) and whose loaded image is that file (not Wine's builtin) | EML patched the game files in its `DllMain`; a second writer would drop its changes ([../eml.md](../eml.md#real-eml-present-kiln-eml-steps-aside)) |
| ShroudForge | its own `dbghelp.dll` started it (state `self-hosted`) | SF already runs its mods and its startup writer ([../shroudforge.md](../shroudforge.md)) |
| EMF | `emf_core.dll` is loaded, or EMF's loader is chained (`kiln/chain.ini`, `dinput8-chain.ini`) | EMF loads the EMF mods; one EMF per process ([../emf.md](../emf.md#when-emf-itself-is-installed)) |
| SET | a module with SET's markers is loaded: a non-system `powrprof.dll` with SET's name or folder name, or a renamed module with SET's name (another tool's `powrprof.dll` is not SET) | SET's own runtime drives its mods ([../set.md](../set.md)) |
| Greeneye | Greeneye Loader's own per-process claim (`Local\Greeneye-Loader-<pid>`) exists | Creating the named claim is atomic. A loader honoring that same claim stays idle when Kiln took it first; Kiln yields when the claim already exists. |
| Flame | a module in the game folder or `kiln/chain/` carries Flame's entry-point name without exporting it (Flame's proxy, chained by Kiln) | Flame's loader starts the mods in `<game>/mods` from its own thread; starting them again would run the mod twice. Switch its line off in `kiln/chain.ini` to let Kiln start them (and from profiles). |
| Shroudtopia | `shroudtopia.dll` is loaded, or a module from the game folder or `kiln/chain/` names Shroudtopia or Shroudweave | Shroudtopia's loader thread already drives its mods. |
| Enshwick | its `powrprof.dll` stub (texts `enshwick-loader-stub`) is loaded, its claim `Local\enshwick-stub-<pid>` exists, or a module exporting `enshwick_start` is loaded | Kiln takes the same claim when it starts the loader, so a stub that starts later stays idle (the stub's own rule); when the stub is first, it runs Enshwick and Kiln starts nothing. |
| shroudlink | a loaded module exports both `CrashHandlerFactory` and `DirectInput8Create` (its core as `steam_api64.dll`, `crashhandler64.dll`, `dinput8.dll`, chained or not) or is named `shroudlink_core.dll`; backstop: its claim `chestsort_server_<pid>` | That core loads `shroudlink_mods/` itself; both copies honor the same claim, so only one hooks. |
| Ember | a DLL from the server folder or `kiln/chain/` carries `ember-loader` / `ember_loader` | Ember's loader runs its mods; kiln-ember starts none anyway until Ember publishes its ABI. |

Flame and Shroudtopia are checked again before mod dispatch, including after Shroudtopia's boot delay. Renamed
proxies in `kiln/chain/` are recognized. These engines have no cooperative process claim or shutdown contract:
a real loader arriving after a mod has entered can still start that mod again. Kiln stops further dispatch when it
observes such a loader, but cannot undo arbitrary hooks or threads already started. Disable one loader before
starting the process when both could arrive in that order.

### The profile's mods while the own loader runs

The engine's own loader reads only its own folder (`<game>/mods`, `emf/mods`, `greeneye` ...), never Kiln's Library.
So when a plugin steps aside, the profile's mods of that engine run only if they are in that folder. Since dev.22 the
launcher puts them there:

- **At activation** (and whenever the launcher refreshes the active profile before a start), when the engine's own
  loader is installed for this game (Greeneye: always, since dev.24, because its mods write their settings into their
  folder; [greeneye.md](greeneye.md#install-a-mod)), Kiln copies the profile's enabled mods of that engine into the folder the loader
  reads, as `<folder>/<library id>/` with Kiln's marker `.kiln-library.json` (`{format, kind, id, version, hash}`;
  the id is the Library id, which may differ from the id in the mod's own manifest). Current copies are kept. Kiln's
  copies of mods that were switched off or left the profile are removed. When the loader is no longer installed, Kiln
  removes its copies of that engine again. Ownership comes only from Kiln's receipt (`kiln/state/library-placements.json`),
  never from a marker in a folder, so folders the player put there are never touched: a player's folder with the same
  name, or with the same mod (the same manifest id: real EML stops the game on a duplicate id), is left alone and the
  player's copy runs. A copy that changed since Kiln placed it (a mod writing its settings into its folder) is left as
  it is.
- **"Installed"** is a static check of the game folder (`crates/kiln-manage/src/own_loaders.rs`): a loader file under a
  proxy name next to the game, an active `[chain]` line of `kiln/chain.ini`, an entry of `dinput8-chain.ini`, and for
  EML also a profile that makes Kiln host ShroudForge (ShroudForge then writes the EML packages of its mods folder
  itself, [../eml.md](../eml.md#6-shared-startup-data-writer-shroudforge-and-emf-content), but only those it has switched
  on: see "ShroudForge's switch" below). A file under a proxy name
  counts as "may run": on Windows the game imports those names, under Wine and Proton they load only with a DLL
  override (a Steam Deck player with ShroudForge's overrides set does run it), which the launcher cannot see.
- **ShroudForge's switch** (dev.24). ShroudForge, hosted or its own loader, runs a package of its mods folder only while
  the package's `extended.mod.json` says `enabled: true`, on the sides its `targets` name; a legacy EML package has
  no such file until ShroudForge writes one, so ShroudForge keeps it off. While ShroudForge may run the profile's EML
  mods (its own loader, or Kiln hosting it for the profile; also beside EML's own `dbghelp.dll`, which under Wine and
  Proton loads only with a DLL override: without one the adapter hosts ShroudForge and kiln-eml stands aside for it),
  Kiln's copies there (the game-folder ones in `<game>/mods/<folder>` too, when that is ShroudForge's folder) carry
  that switch from the profile; EML's own loader, when it runs instead, reads only `launcher` from the file: the file ShroudForge's own Modloader writes for a legacy
  EML package, or only the `enabled` value of a shipped one. The file is part of Kiln's copy; setting values changed
  in it later are the player's (carried to the next version; a copy holding them is kept, switched off, when the
  profile turns the mod off). A player's own folder is never written, and the launcher says when ShroudForge does not
  run a package and what helps (activation notes; for Kiln's copies `ownLoaders[].off`: switched off, the next
  activation switches it on; `otherSide`: its `targets` name the other side only, nothing to do; `unreadable`:
  ShroudForge refuses its files, no switch helps). Without ShroudForge nothing is written.
- **An EML mod copied into ShroudForge's folder by hand** (dev.28). ShroudForge keeps it switched off (a legacy EML
  package has no `extended.mod.json` until its switch is first turned on), and Kiln never writes into a folder it did
  not place. Every place that explains it uses the words promised to a player on Nexus (2026-11-14): "You put this
  mod into ShroudForge's folder yourself, and ShroudForge has it switched off. Switch it on in Kiln's in-game menu
  (Mods > ShroudForge), or remove the folder and add the mod with Add mods." The launcher's list of mods in the game
  folder (`found::scan`: Off, no "Add it" fix, since activation never places a copy beside the player's folder), the
  activation notes (`ownLoaders[].ownOff` and the per-mod note, which say "press Play" instead of "Add mods" because
  the mod is in the profile), kiln-eml's `off-in-shroudforge` reason and kiln.log line, and the in-game Mods page (on
  the folder's own row in Mods > ShroudForge: "Switch it on below"). On a dedicated server, which has no in-game menu,
  the second way out is the only one named. Text: `kiln_manage::shroudforge::hand_eml_off`; kiln-eml and `Kiln.Ui`
  keep their own copy of the words.
- **No double load.** When the loader does not start after all (Wine/Proton without the override), the plugin runs
  the profile's mods from the Library as usual (EML's: unless Kiln hosts ShroudForge for the profile, which then runs
  Kiln's copies in its folder). A folder in the engine's drop-in folder whose marker names this
  engine and a profile entry's id is that profile mod: the plugin skips it, so the mod runs once and is listed once.
  A marked folder that no profile entry names is an ordinary drop-in.
- **Saying it.** A plugin that steps aside lists the profile's mods of its engine in `kiln/state/<engine>.json`:
  `own-loader` (Kiln's copy is in the loader's folder, it runs there) or `not-loaded` (only in the Library, with a
  one-sentence reason), plus `disabled`; under ShroudForge also `off-in-shroudforge` (in its folder, switched off
  there) and `other-side` (its `targets` leave out this side). Every `not-loaded` and `off-in-shroudforge` mod is a
  warning in kiln.log. The launcher gets the same
  picture before the start: `profile_activate` / `profile_active` return `ownLoaders`, and `engine_status` returns
  `ownLoader` per engine, each with a `text` such as "EML's own loader (dbghelp.dll) is installed: it runs the EML
  mods in mods/; Kiln copied this profile's 3 EML mod(s) into mods/ for it".
- **Play-once** keeps the active profile's selection for an engine whose own loader is installed (as for ShroudForge
  and SET, and since dev.24 for Greeneye always): its folder is fixed, so a play-once profile with other mods of that
  engine is refused with the reason.
  Play-once **no-mods** cannot stop an own loader: it runs whatever is in its folder, Kiln's copies included.

Audit (dev.22):

| Engine | Own loader reads | How the plugin knows it runs | Profile mods while it runs, before dev.22 | Since dev.22 |
|---|---|---|---|---|
| EML | `<game>/mods/*` (working folder); ShroudForge's loader, which carries EML: ShroudForge's mods folder | module scan for `EML_LOG_FILE_PATH` (`gpl/kiln-eml/kiln-eml/src/plugin.rs` `eml_loader_loaded`); kiln-shroudforge's `stand_aside` | none ran; `standing aside` at info level, `eml.json` listed no mods | placed into `mods/<id>/` (or ShroudForge's folder, with ShroudForge's switch on since dev.24); `eml.json` lists them `own-loader`/`not-loaded` (under ShroudForge `off-in-shroudforge`/`other-side` too); marked copies skipped as drop-ins |
| ShroudForge | its mods folder (`paths.mods`, default `mods`) | state `self-hosted` (its bootstrap loaded) | already placed at activation | unchanged |
| SET | `shrouded_engine_tweaks/mods/<name>/` | a module with SET's markers (`powrprof.dll` or renamed) | already placed at activation | unchanged |
| EMF | `emf/mods/<id>/` (switch: `emf/config/<id>.ini`) | `emf_core.dll` loaded, or a module exporting `emf_loader_identity`/`emf_core_start` | none ran, none listed | placed into `emf/mods/<id>/`; `emf.json` lists them (`profileId`); the `own-loader` reason says when EMF's ini keeps the mod off |
| Greeneye | `greeneye/<id>/` | Greeneye Loader's claim `Local\Greeneye-Loader-<pid>` exists | none ran, none listed | placed into `greeneye/<id>/`; listed in `greeneye.json`. Since dev.24 placed with or without Greeneye's loader and run from there by kiln-greeneye too ([greeneye.md](greeneye.md#install-a-mod)); a copy the player put in `greeneye/` is listed `own-loader` |
| Flame | `<game>/mods/<id>/` with `flame.json` | a game-folder or `kiln/chain/` module carrying `FlameStartRuntime` without exporting it | none ran; listed `skipped` | placed into `mods/<id>/`; listed `own-loader`/`not-loaded`; `kiln.flame` `running(id)` also true for them |
| Shroudtopia | `<game>/mods/*.dll` and `mods/<name>/` | `shroudtopia.dll` loaded, or a module naming Shroudtopia/Shroudweave | none ran, none listed | placed into `mods/<id>/` (the 1.0 folder layout); listed in `shroudtopia.json` |
| Enshwick | `<game>/mods/<folder>/` | its `powrprof.dll` stub loaded, its claim, or a module exporting `enshwick_start` | none copied (kiln-enshwick returned before its sync), none listed | kiln-enshwick copies new entries while standing aside too (stage + one rename; the stub's loader may see a new copy only from the next start); never refreshes or removes a copy the running loader may hold; listed in `enshwick.json` (`action`) |
| shroudlink | `shroudlink_mods/*.dll` (flat only) | a module exporting `CrashHandlerFactory` + `DirectInput8Create`, or `shroudlink_core.dll`, with shroudlink's strings; or its claim | only disabled entries listed | not delivered (the real core reads only flat DLLs): `not-loaded` with the reason, `own-loader` for an entry that is a DLL directly in `shroudlink_mods/` |
| Ember | `ember/mods/<mod>/` | a DLL from the server folder or `kiln/chain/` carrying `ember-loader` | `skipped` | not delivered (no Ember loader exists yet): `not-loaded`, or `own-loader` for an entry that already lives in `ember/mods` |

Drop-ins with an active profile: every plugin reads its engine's folder when the profile has `"dropins": true` or a
`dropins` list naming the engine (the list form was ignored by kiln-greeneye, kiln-flame, kiln-shroudtopia,
kiln-enshwick, kiln-shroudlink and kiln-ember before dev.22; kiln-emf reads the core's bitmask, which has it).
kiln-shroudforge hosts ShroudForge with an active profile when the profile lists an enabled ShroudForge mod, and since
dev.22 also when ShroudForge drop-ins are on and its mods folder has a ShroudForge package. kiln-set reads only
`shrouded_engine_tweaks/mods`, where the launcher places the profile's SET mods. A profile made before dev.22 keeps
loading the folders it loaded: the launcher names those kinds in the `dropins` list it writes
([../profiles.md](../profiles.md#drop-in-folders)).

**Under Wine without a DLL override** the loader file is in the folder but Wine loads its own builtin DLL of that name
and reports the game folder's path for it. A check that reads the file at a loaded module's path would then see the
other loader's markers although it never ran. kiln-eml compares the loaded image's PE header with the file's and runs
the EML mods itself when they differ (lab, GE-Proton, no override):
```
[eml] Z:\...\dbghelp.dll: EML's loader is in the folder but this process loaded another dbghelp.dll (Wine's builtin, without a DLL override): kiln-eml runs the EML mods
```

**Path policy.** A mod DLL must be a regular file inside its authorized root: the mod folder, the engine folder for a
mod found there, or the parent of an explicitly listed flat DLL. From that root down (the root included, so a mod
folder or a profile's listed folder must not itself be a link), symlinks, junctions, `..`, alternate streams and
device names are refused, and the opened file's final path must still lie under the root. The root may sit anywhere,
such as a profile pointing at a library outside the game folder (Kiln's launcher library through `Z:\...`). Folders
above the root are the player's layout and are not judged: a Steam library behind a symlink, or Wine/Proton drive
links such as `Z:\` (itself a link to `/`), are fine. Under Wine, symlinks to directories are refused like on Windows,
but Wine follows a symlink to a *file* in every Win32 query, so the plugins cannot see that one case; the installer
that writes the mod folder must not create such links. Directory and file handles keep every path component and the
DLL contents stable from export validation through loading. A callable, file-backed export is required; forwarded and
data exports do not qualify.
The export precheck does not validate dependencies or make native mod code safe.

**Path encoding.** Paths cross the C ABI as UTF-8 (`KilnApi::path`, `kiln.profile`, `KilnNativeContext`), and a player's
game folder and Library often hold a Windows user name or Steam library folder in Cyrillic, CJK or accented Latin. A
narrow path (`char*`, a `std::string` handed to `fopen`, `std::ifstream`, `std::filesystem::path`, a `*A` Win32 call)
is read in the ANSI code page, which lacks those letters, and `std::filesystem::path::string()` throws for them. So a
plugin or service converts a UTF-8 path once (`to_wide`, `std::filesystem::path(std::u8string(...))`) and uses the
wide calls (`CreateFileW`, `GetFileAttributesW`, `FindFirstFileW`, `LoadLibraryExW`, `_wfopen`), `path::u8string()` for
log text, and never `path::string()`. A third-party API that takes only `char*` gets the bytes instead (read the
file yourself) or its UTF-8 entry point. `tools/lab/winpaths.py` builds a server lab under such a path with one mod of
every kind; `PROXYHOST_WINPATHS=1 native/spikes/proxyhost/run.py` runs the proxy under one.

Compatibility runtimes and entered mod DLLs stay mapped for the process lifetime. Revoking a plugin stops queued
work and cancellable watchers; it cannot terminate foreign threads or reclaim trampolines they may still call.
Fault guards cover synchronous loader-invoked initialization and lifecycle calls. Mod-owned threads and
arbitrary-signature detours execute directly and can still crash the process.

## When another engine hooks first

Several engines inline-hook game functions: Enshwick, EMF, ShroudForge, SET, a shroudlink core, and others that hook
through Kiln's API. A hook writes a `jmp` over the function's first bytes. Kiln shares the process with them in two
ways.

**Finding functions.** Kiln matches its signatures against the exe's code **as the file on disk has it**, not the
live code, and hooks the live address (details: [../sigport.md](../sigport.md#which-bytes-the-core-matches-the-exe-file-not-the-live-code)).
An engine that hooked first therefore hides nothing from Kiln. Before 2026-11-07, an Enshwick that started first
turned off `chat.send`, `chat.receive`, `gameplay.discard`, `gameplay.intercept`, `quests.client_input` and
`time.sync_probe`. These now resolve. kiln.log names every function whose live code differs from the file, and what
it jumps to:

```
[kiln] sigdb: symbol gameplay/InventoryTransfer matched on the file's code; its live bytes differ at rva 0x19e860 (jmp to enshwick.dll+0x11ea80): hooked or patched by someone else
```

`sigdb-report.json` lists the same symbols under `code.live_differs_symbols`, with a `"live"` entry on each. If the
file can't be used (unreadable, or a different build than the one loaded), Kiln scans the live code as before and
logs a warning.

**Hooking the same function.** Kiln's hooks use safetyhook, through one registry (`kiln.hooks` v2,
[../services/hooks.md](../services/hooks.md)) that places **one** detour per game address for every handler registered with it.
Since dev.21 Kiln's services, the core tick and the hosted engines' hook slots all register there.
safetyhook decodes at least 5 bytes at the target and moves them into its trampoline. A `jmp rel32` (E9, Enshwick's
5-byte hook) or a `jmp [rip+0]` (FF 25, a 14-byte absolute hook) there is IP-relative, so it is copied with its
displacement rewritten. The trampoline still reaches the other engine's hook. A call then runs:

1. the hook armed last (Kiln's, when the other engine started first);
2. its call to the original, which enters the other engine's hook;
3. the real function, once.

Kiln notices when it hooks a function that already starts with someone else's jump (the live bytes differ from the exe
file's) and says so, in kiln.log and in `state/hooks.json`:

```
[kiln] hooks: chat/ChatLogAdd (kiln-chat) chained over enshwick.dll+0x11ea80
```

The line names Kiln's hooks on that function with their owners (`<label> (<owner>)`).

Mid hooks behave the same way. When another engine hooks **over** Kiln's detour, its jump now starts the call and Kiln's
detour is reached through it. After boot and at the first world (never per frame) Kiln checks every target and logs
`hooks: <module> hooked over Kiln's <label> (<owner>)`; `hooks.json` records it as `foreignOver`. Kiln never removes the other
module's jump and never unhooks its own detours (an empty chain calls the real function), so the order in which engines
start does not make Kiln undo another engine's hook. Enshwick's decoder relocates Kiln's `jmp` when Kiln hooks first.
`native/spikes/hookchain/run.sh` and `native/core/tests/test-hooks-v2.sh` (Wine, no game) check these cases with a
stand-in hooker.

The one path that can still undo someone's hook is a safetyhook unhook outside the registry: it writes its saved bytes
back without checking them. If Kiln hooked first and another engine hooked over Kiln's `jmp`, such an unhook removes
the other engine's hook too, and the other engine's trampoline then leads into freed memory. Registry links are never
unhooked, only revoked from the chain. Since dev.21 no Kiln hook on the game exe is placed outside the registry. Only
hooks on system DLL exports (`LoadLibraryExW`, Vulkan, XInput on the client) and EMF/Shroudtopia mod hooks on targets
outside the game exe still use plain safetyhook; those stay installed for the life of the process.

One limit: safetyhook needs its trampoline within ±2 GB of both the target and the other engine's relay (a 5-byte
`jmp` already keeps the relay within ±2 GB of the target). If no such memory is free, safetyhook falls back to a
14-byte absolute hook, which refuses to move an IP-relative instruction. The hook then fails, and kiln.log says
`hooks: safetyhook could not detour rva <rva> (error <n>)` (or a service names the hook it could not arm). The other engine's
hook keeps working.

When a closed-source DLL is the other engine, the overlap report and `[late]` (see [Closed-source DLL
mods](#closed-source-dll-mods)) tell you which one hooked first and let you swap the order.

ShroudForge's native runtime (its six engine hooks, named by a legacy game profile) is hosted the same way: Kiln finds the
places in the exe file's bytes and places them as mid links, so a DLL that hooked one first (XHL, a closed mod) no longer
switches the runtime off ([../shroudforge-compat.md](../shroudforge-compat.md#engine-hooks-through-kilnhooks)).

What can still clash is **code patches**: two engines changing the same bytes in place, not a hook. Enshwick compares
the bytes before it patches and skips a patch when Kiln got there first ([enshwick.md](enshwick.md#hooks-next-to-kiln)).

## Closed-source DLL mods

Mods that ship only as a DLL (XHL-Core, Auto Loot Plus, trainers, overlays) are not an engine Kiln can host. Kiln loads
them from `kiln/chain.ini` and `dinput8-chain.ini` and lives next to them. Three things help when they hook the same
game functions as Kiln or a hosted engine.

**Load it late.** Put the DLL's path in `kiln/chain.ini` `[late]` (or switch it in the launcher). Kiln's proxy leaves it
alone; kiln-core loads it after the services, plugins, native boot mods and the managed host, in safe idle too. Kiln then
hooks first and the DLL hooks over it, which Kiln handles. A DLL that fails to load, or whose `DllMain` faults, is a
warning in kiln.log (`late: <path> failed to load (error 1114: its DllMain failed)`) and an entry in
`state/native.json` `chain.late`; the game goes on. Some DLLs cannot be moved (EMF's own loader, a DLL EMF's loader already
started): see the limits in
[../engines.md](../engines.md#loading-a-chained-dll-late-late).

**Overlaps.** Kiln knows every hook it places (the [hook map](../services/hooks.md)). An *overlap* is a function where a
foreign module's hook and a Kiln-hosted link (Kiln, ShroudForge, an EMF mod) meet:

| Kind | What happened | Usual fix |
|---|---|---|
| `foreign-before` | another module hooked the function before Kiln did; Kiln chained over it | load that DLL late (`fix: late`); if another loader starts it, add a rule or switch one side off (`fix: rule`) |
| `foreign-over` | another module hooked over Kiln's detour; Kiln's stays armed behind it | none unless a feature misbehaves |
| `refused` | a hosted engine could not hook (ShroudForge's engine hooks need the game's original bytes) | load the DLL that patched the code late |

`state/hooks.json` lists them under `overlaps` and kiln.log warns once for each, naming both sides and the fix:

```
[W] [kiln] compat: overlap: xhl-core.dll+0x11ea80 hooked the function before Kiln did; Kiln's chat/ChatLogAdd (kiln-chat), emf/ChatLogAdd (kiln-emf) chained over it. Load xhl-core.dll late (kiln/chain.ini [late], or the launcher's "Load after Kiln and ShroudForge"): Kiln then hooks first and xhl-core.dll hooks over it.
```

The launcher shows the last session's overlaps on the profile's native page with a one-click "load late". ShroudForge's
refusal is read from `state/shroudforge.json` (`game_thread=unavailable(<hook>:<reason>)`); Kiln writes it into
`hooks.json` a few seconds after boot, once ShroudForge has reported.

**Compat rules.** `compat-rules.json` tells Kiln about known clashes. Two files are read, Kiln's own (`kiln/compat-rules.json`,
shipped in the runtime's core component since dev.29 and replaced by every update; may be missing on an older install) and yours (`<configDir>/compat-rules.json`, in the launcher `profiles/<profile>/config/`). A rule of
yours with the same `id` replaces Kiln's. Plain JSON, no comments.

```json
{ "format": 1, "rules": [
  { "id": "xhl-and-shroudforge",
    "when": [ { "module": "xhl-*.dll" }, { "engine": "shroudforge" } ],
    "action": "late", "subject": { "module": "xhl-*.dll" },
    "message": "XHL hooks the same functions as ShroudForge. It loads after Kiln so both work." } ] }
```

- `when`: every matcher must be present. `{"module": "xhl-*.dll"}` (a DLL file name; `*` and `?`, case ignored),
  `{"engine": "shroudforge"}`, `{"mod": "emf:coolmod"}` (`kiln:` and `native:` both name Kiln's native mods),
  `{"symbol": "chat/ChatLogAdd"}` (an overlap on that function). No `when`: always. At most 8 matchers; a module
  pattern with wildcards has at most 64 characters and 4 `*`.
- `action`: `warn` (log the message), `late` (the `subject` module loads late), `disable` (the `subject` module, mod or
  engine does not load), `before` (dev.29: the `subject` loads before the `target`, below), `defer` (since
  0.2.0-dev.29.4: the `subject` native mod waits for the Shroudtopia host's first pass, below) and `original-code`
  (since 0.2.0-dev.29.6: the `subject` Shroudtopia mod compares against the game's original code, below). Every action
  but `warn` needs a `subject`; `before` and `defer` also need a `target`, and only they take one; only `defer` takes
  `max_wait_ms` (required for it).
- Who applies what: the launcher writes `chain.ini` and the profile, so it applies `late` and `disable` of chain entries
  (a line `; kiln rule <id>` marks what a rule wrote). kiln-core logs `warn` rules, switches off a **mod the profile lists**
  (`disable` on `{"mod": "emf:coolmod"}`, before the plugins read the profile; the rule's message is the reason in the
  logs), and reports the rest as not applied. A `late` rule cannot move EMF's loader, or a DLL another loader already
  started, so it is not applied for those either: `compat: rule <id> asks xhl-core.dll to load late; apply it in the
  launcher`. `hooks.json` `rules` lists every rule that fired with `applied` and a `note`.
- A late rule that was applied last session stays in force while its subject loads late, so a rule on a `symbol` does not
  undo itself when moving the DLL removes the overlap.
- A rule that does not validate (bad id, no `subject` for `late`/`disable`, an unknown key) is dropped with a warning in
  kiln.log; the other rules stay. To switch a shipped rule off, give your own rule its `id` and a `when` that never holds.

**Wait for Shroudtopia (`defer`, since 0.2.0-dev.29.4).** Some native mods must start only after a Shroudtopia mod has
placed its hooks. A `defer` rule keeps its `subject` (a native mod id such as `{"mod": "native:my-mod"}`, a DLL file
name the mod declares, or an exact path of a companion file in its package, with forward slashes) back until the
Shroudtopia host's first pass, while its `target` (a `{"mod": "shroudtopia:<id>"}` that is on in the profile) runs, and
for at most `max_wait_ms` (1 to 30000 ms after the host starts its worker, Shroudtopia's `bootDelay` included). kiln-core
applies it: the native mod's note and kiln.log say `waits for Shroudtopia's first pass (<id> is on)`, and a native mod
that loads after it in its declared order waits with it. A native mod with an `init` is never deferred (its `init` stays
on its documented phase thread; kiln.log says so and the normal phases are used), nor is one when the Shroudtopia host
is not there or Shroudtopia's own loader runs. Kiln ships no `defer` rule (since 0.2.0-dev.29.5); the action is there
for your own file.

```json
{ "id": "my-mod-after-shroudtopia", "action": "defer",
  "subject": { "mod": "native:my-mod" }, "target": { "mod": "shroudtopia:some-mod" }, "max_wait_ms": 5000,
  "message": "My Mod hooks the game after Some Mod has placed its hooks." }
```

**The game's original code for a Shroudtopia mod (`original-code`, since 0.2.0-dev.29.6).** Some Shroudtopia mods check
the game's code with `memcmp` before they patch it, and refuse when another mod's entry hook changed those bytes. An
`original-code` rule names exactly one such mod as its `subject` (`{"mod": "shroudtopia:<id>"}`, no `target`): Kiln
points that mod's imported `memcmp` at a version that compares against the game exe's original bytes where the live
bytes differ only by another mod's entry hook. It changes nothing in the chain. Kiln ships one:

```json
{ "id": "shroudtopia-item-skill-text-original-code", "action": "original-code",
  "subject": { "mod": "shroudtopia:item-skill-text" },
  "message": "Item Skill Descriptions can check the game's original code while Auto Doors keeps its hooks." }
```

**One DLL before another (`before`, dev.29).** Some DLL mods must hook the game before another one does, not only before
or after Kiln. Kiln ships one such rule in `kiln/compat-rules.json`:

```json
{ "id": "auto-loot-plus-before-ultimate-asi-loader",
  "when": [ { "mod": "native:auto-loot-plus" }, { "engine": "ultimate-asi-loader" } ],
  "action": "before", "subject": { "mod": "native:auto-loot-plus" }, "target": { "engine": "ultimate-asi-loader" },
  "message": "Ultimate ASI Loader loads after Kiln so that Auto Loot Plus can hook the game first; Auto Loot Plus's F8 menu needs that." }
```

Auto Loot Plus redirects the game's `GetProcAddress` import in its `DllMain` (for its F8 menu), but only while that slot
still holds kernel32's own function. Ultimate ASI Loader redirects the same import when it loads and puts it back only
when the game first calls it, after Kiln's entry gate. So whichever loads second finds the slot taken, and Auto Loot Plus
must load first. Kiln's proxy always loads `[chain]` (where the installer put Ultimate ASI Loader) before `[profile]`
(where Auto Loot Plus is, a Library native), and its late phase keeps that order, so the switches alone order them right
only as "Ultimate ASI Loader late, Auto Loot Plus not". The rule writes exactly that:

- The launcher loads every chain entry the `target` names late (a `; kiln rule <id>` line in `[late]`) and keeps every
  entry the `subject` names early, whatever their "Load after Kiln and ShroudForge" switches say: the profile's late
  switch for a `[profile]` entry is not written while the rule applies, and the player's own `[late]` line for a
  `[chain]` entry becomes `; kiln rule <id> off: <line>` (back at the next activation without the rule). It pins both
  even when the order is already right, so a switch flipped later cannot undo it. The player's own `[late]` line for the
  target stays the player's.
- Matching: `module` by file name, `engine` by what the launcher reads in a chain DLL (Ultimate ASI Loader by its
  `IsUltimateASILoader` export, whatever it is called: `xinput9_1_0.dll`, `dinput8.dll`, `version.dll` or another name), `mod`
  by the Library id of a native mod of the profile. Auto Loot Plus's id `auto-loot-plus` comes from its package's
  `mods/auto-loot-plus` folder, under any proxy name of its DLL. Imported without that folder (only its `dinput8.dll`), or
  with its DLL renamed to a name that is no proxy name, it gets another id and the shipped rule does not fire: the switches
  decide as before. Write your own rule with that id (`{"mod": "native:<id>"}`) in that case. Installed as the game's
  `dinput8.dll` before Kiln, the installer chains it as `kiln\chain\chain-loader-dinput8.dll` (a `[chain]` entry without
  a Library id): a rule of yours with `{"module": "chain-loader-dinput8.dll"}` as the subject orders that one.
- On the profile page both switches show the rule ("Loads late: rule <id>", "Loads first: rule <id>"), the rule's
  message stands under both entries, and neither switch can be moved against the rule. The activation's notes and the
  launcher's log (`profile: note: <message>`) say the same sentence; without a `message` Kiln writes one from the file
  names.
- An order that cannot be made moves nothing and is a warning on the profile page and in the activation's notes: the
  target is EMF's own loader or shroudlink's client addon (neither ever loads late), it starts outside Kiln's chain (next to
  the game under a proxy name Windows loads, or from another loader), the subject and the target name the same DLL, two
  `before` rules ask opposite orders, or a `late` rule loads the subject late (the `late` rule stays). A side a `disable`
  rule switches off leaves nothing to order (a note, no warning).
- kiln-core reads the rule and reports it in `hooks.json` `rules` (with its `target`): `applied: true` when every subject
  entry loaded before every target entry (a late entry after all early ones, list order within a phase), otherwise
  `applied: false` and a kiln.log warning naming both (`activate the profile again`). The core knows Ultimate ASI Loader's
  chain entry by the same export, and a `[profile]` entry's native mod by its Library folder.
- Older readers: a Kiln launcher or core from before dev.29 drops a `before` rule as one that does not validate (the
  launcher shows "A rule file did not read: ... unknown variant `before`"; the core logs `rule #1: unknown key "target";
  dropped`) and keeps the file's other rules. Nothing moves there.

Limits: the core knows an engine from the profile's lists, ShroudForge's state, a few DLL names (`shroudforge-runtime.dll`,
`emf_core.dll`, `enshwick.dll`) and, since dev.29, Ultimate ASI Loader's chain entry by its `IsUltimateASILoader` export; a mod folder placed by hand (not in a profile list) cannot be switched off by a rule in
the core, and the report says so. A `disable` on a native mod whose DLLs load in phase dllmain does not stop the proxy
from loading them (it ran before the core): the rule is reported as not applied (`its DLL was loaded by the proxy before
kiln-core started`) and the mod shows as loaded. Activate the profile again so the launcher's chain.ini keeps them out.
A DLL that `[late]` defers is held back by the rule. A file with more than 512 rules keeps the first 512 and logs one line
for the rest. Schema: `schema/compat-rules.schema.json`.

## Status files and logs

Each plugin writes `kiln/state/<engine>.json` (every mod, its status and reason) and logs under its own tag in
`kiln/logs/kiln.log`: `[greeneye] ...`, and a mod's own lines as `[greeneye:<id>] ...`. Greeneye and Shroudtopia also
keep the log file their mod authors ask for in bug reports: `kiln/logs/greeneye-loader.log`,
`kiln/logs/shroudtopia.log`.

## Tests

`native/plugins/compat/tests/run.py` runs each plugin in a stand-in game process under plain Wine with test mods that
exercise API, path, malformed-file, lifecycle and concurrency regressions, and `native/plugins/compat/tests/lab.sh` puts read-only probes into a lab dedicated server.
