# Other mod engines: detection, chaining, Library kinds

Kiln loads mods made for other Enshrouded engines, and never breaks a setup made with another loader. This page says
what Kiln recognises today and what it does with it. Code: `crates/kiln-manage/src/engines.rs` (table, markers,
Library detection, loader registry), `pe.rs` (proxy classification), `install.rs` (chaining), `native/proxy/dinput8.cpp`.

## Proxy classification

Every DLL under a proxy name next to the game (`dinput8`, `dbghelp`, `dbgcore`, `umpdc`, `cfgmgr32`, `winmm`,
`powrprof`, `iphlpapi`, `version`, `xinput1_4`, `xinput9_1_0`, `dxgi`, `steam_api64`) gets one `ProxyKind`. Checks in
this order; the first match wins. Texts match as ASCII or UTF-16LE, case ignored.

| Kind | Markers |
|---|---|
| `kiln` | export `KilnProxyInfo`, text `KilnProxy/<abi>`, UTF-16 `\kiln\kiln-core.dll` |
| `emf` | exports `emf_loader_identity` / `emf_loader_play_once`, text `EMF-LOADER-7d3c9f` (before ShroudForge: EMF 0.4+ also exports `ShroudforgeSetDinput8Provider`) |
| `shroudforge` | `shroudforge/shroudforge-runtime.dll`, `shroudforge/config/modloader-config.json`, export `ShroudforgeSetDinput8Provider`, text `shroudforge` |
| `eml` | `EML_LOG_FILE_PATH` |
| `set` | `shrouded_engine_tweaks` |
| `enshwick` | `enshwick` |
| `greeneye` | `greeneye` |
| `shroudtopia` | `shroudtopia` (also Shroudweave) |
| `flame` | `FlameStartRuntime`, `flame.json` |
| `shroudlink` | `shroudlink` |
| `ember` | `ember-loader`, `ember_loader` |
| `modloaderproxy` | `modloader-proxy.dll`, `chainloader.ini` |
| `ultimateasiloader` | export or text `IsUltimateASILoader` |
| `aldrich` | `aldrich` |
| `livemap` | `livemap.ini` |
| `sharedxp` | `xpshare.ini` |
| `reshade` | `reshade` |
| `chainloader` | `dinput8-chain.ini` (XHL-Core, Auto Loot Plus and similar) |
| `nativefolders` | `native-loader.log` (CharosDX's loader of `mods\<mod>\native\*.dll`: Sparkworks Enhanced, Cross-Base Storage, Craft From Stations) |
| `unknown` / `notdll` / `none` | any other 64-bit DLL / not a 64-bit DLL / no file |

The markers start from EMF's `known_tools.txt` (MIT). A marker that turns out wrong for a release is a one-line fix in
the `ENGINES` table.

## Installer

Kiln owns only `dinput8.dll`.

- `kiln`: replaced (backup). `notdll`: refused.
- `shroudforge`: refused unless "switch from ShroudForge". Then only ShroudForge's `dinput8.dll` goes to the backup.
  Its `dbghelp.dll`, `winmm.dll` and `shroudforge/` stay. If its own loader still starts, ShroudForge hosts itself;
  otherwise Kiln's ShroudForge plugin hosts the selected runtime. See ShroudForge self-hosting below.
- Every other kind: chained. The installer moves the DLL to `kiln/chain/<word>-dinput8.dll` and adds it to
  `chain.ini [chain]`. Occupied chain names always get a numbered suffix, even for identical bytes, directories or
  dangling links. Ultimate ASI Loader runs only under names it knows, so it is chained as `xinput9_1_0.dll` next
  to the game (`..\xinput9_1_0.dll` in chain.ini). If that name is taken (case ignored, even identical bytes), the install is refused.
  Ultimate ASI Loader that comes later, from the Library, goes to the same place at profile activation ([Files in the
  game folder](#files-in-the-game-folder)).
- DLLs under any other proxy name are never moved. EML's `dbghelp.dll`, Shroudtopia's `winmm.dll` and SET's
  `powrprof.dll` keep running next to Kiln.
- The receipt's `replaced[].engine` names the engine. Uninstall restores every original byte-identically, unless
  another loader's file the player put there since sits at its path: that one stays, with a warning naming the
  backup. A changed Kiln proxy there is moved to `kiln/backups/uninstall-*` and the original comes back.
- A full/oversized `chain.ini` is refused before taking the proxy slot. Symlinks on installation paths are refused;
  runtime bundles cannot own other proxy slots or installer recovery paths. Each install's backups have a unique
  suffix. An error, including a receipt-write error, rolls back files and restores the chain file's exact previous
  bytes. If the OS prevents rollback, the error names the retained recovery staging folder; later installs never
  delete originals held there.

## Proxy

The proxy reads `dinput8-chain.ini` `[Chain]` and `[EMF]` (the DLLs EMF 0.5's installer renamed) in file order. A
comment may follow a name after a space or tab (`mymod.dll ; my mod`, also `#`), and a name may stand in double quotes,
as EMF's shared spec for the file says (`docs/modding/dinput8-chain.md` in matenm/emf-enshrouded); Kiln's own
`kiln\chain.ini` keeps its stricter rules ([contracts](launcher/contracts.md) 5.3).
A line of `[Chain]`, `[EMF]` or before any section that names something other than a `.dll` is skipped and named in
`kiln\logs\proxy.log`; a `key = value` line whose value is no `.dll` is another tool's setting and is ignored without a
line, as other tools' sections are.
Both sections
use EMF's rules: relative paths inside the game folder only (including the final target of directory links), at most
256 entries across both sections, dedupe by canonical path. Files above 256 KiB are ignored. One overflow entry is
reported as refused; further entries are discarded. EMF's loader is accepted by its exports even without its marker,
and even though it carries ShroudForge's export name. ShroudForge's own `dinput8.dll` is still refused. PE headers,
sections and export names are checked against file-backed bounds, without loading the DLL for classification.
Containment compares the final path of the DLL with the final path of the game folder, so a game folder reached through
a link, a mapped drive (Proton's `S:`) or at a drive root is accepted. Under Wine/Proton, Unix symlinks are invisible to
Windows path APIs, so a Unix symlink inside the game folder counts as part of it (making one needs write access to the
game folder, which already allows placing a DLL there); absolute, drive and `..` entries are refused before resolving.

If Wine or Proton maps its own DLL of the same name in place of a chained one ([Ultimate ASI Loader next to Kiln on
Wine](#ultimate-asi-loader-next-to-kiln-on-wine)), `kiln\logs\proxy.log` says so in place of `loaded at ...` and gives
the override to set: `chain: chain.ini [chain]: requested <game>\xinput9_1_0.dll but another file was loaded in its
place: Wine's own xinput9_1_0; on Wine or Proton a DLL override is needed: WINEDLLOVERRIDES="xinput9_1_0=n,b"`. For a
`[late]` entry the same line, starting `late:`, goes to `kiln.log`. Wine's module reports the requested path all the
same, so the check compares the loaded image with the file: the PE headers' time stamp, image size, checksum, entry
point and section count, and Wine's builtin marker after the DOS header (`native/proxy/image_match.h`). A file or
module header that cannot be read counts as no mismatch.

### Loading a chained DLL late (`[late]`)

A DLL mod that hooks game functions (XHL-Core, Auto Loot Plus and similar closed-source mods) hooks them in its
`DllMain`, before Kiln has hooked anything. Kiln then chains over that hook and works, but a hosted engine that insists on
the game's original bytes can refuse (ShroudForge's engine hooks when ShroudForge hooks the game itself; with Kiln's
bundled runtime they go through kiln.hooks and chain over the DLL instead). Name the DLL in `kiln/chain.ini` `[late]` and Kiln
loads it **after** its own services, plugins and ShroudForge started: Kiln hooks first and the DLL hooks over Kiln.

```ini
[chain]
chain\xhl-core-dinput8.dll
[late]
chain\xhl-core-dinput8.dll
```

The entry stays in its own list (`[chain]`, `[profile]` or `dinput8-chain.ini`) and `[late]` repeats its path. The
proxy checks it as always (Kiln's and ShroudForge's proxies stay refused) and leaves loading to the core. Details:
[launcher/contracts.md](launcher/contracts.md#53-chainini-chainonceini-dinput8-chainini). The launcher's profile page has the
same switch ("Load after Kiln and ShroudForge"). What clashes and what Kiln does about it:
[engines/README.md](engines/README.md#closed-source-dll-mods).

A late DLL may patch the game's entry point, as a loader that holds the start does. Kiln's gate puts the entry's own
bytes back before the core starts, so the patch saves the right bytes and runs before the game's own entry.

What `[late]` cannot do:

- **EMF's own loader** is never deferred, even when it is named. It starts EMF itself, and Kiln's EMF support decides
  when Kiln's plugins start whether the real EMF is running; a loader that came later would run EMF next to it. The
  proxy loads it in its place and writes a line to `proxy.log`; the
  launcher shows it as "Always loads first" without a switch, and a `late` compat rule skips it.
- **A DLL something else already started.** With EMF's loader chained, it starts the DLLs of `dinput8-chain.ini` itself,
  so they are running before Kiln's late phase. kiln.log says `late: <path> was already loaded when the late phase ran`,
  `state/native.json` `chain.late` says `already-loaded`, and `state/boot.json` marks the entry `lateIgnored`. The
  overlap report suggests a compat rule for it, not `[late]`.
- **A disabled mod.** A native mod's DLL in `[late]` loads only when the mod does (see
  [native-mods.md](native-mods.md#switches-and-policy)).

### DLLs that must hook the game's imports first

Some DLL mods change the game's import table in their `DllMain`, and only where a slot still holds the real system
function. Two of them in one chain then depend on their order: Auto Loot Plus redirects the game's `GetProcAddress`
import (its F8 menu) but skips a slot someone redirected already, and Ultimate ASI Loader redirects that import when it
loads and puts it back only when the game first calls it, after Kiln's entry gate. The order is the phases': the proxy
loads `[chain]`, then `dinput8-chain.ini`, then `[profile]`, and the late phase loads its DLLs in the same list order
after all of them. A `[profile]` DLL (Auto Loot Plus from the Library) therefore loads before a `[chain]` one (Ultimate
ASI Loader, where the installer put it) only when the `[chain]` one is late and the `[profile]` one is not. A compat rule
with the action `before` says which DLL must come first, and the launcher writes that into chain.ini whatever the two
switches say; Kiln ships the rule for this pair. The proxy's own order is not changed for this: reordering lines inside
a phase would be new behaviour in the DLL every player's game loads first, and the phases already express it. Details,
matching and limits: [engines/README.md](engines/README.md#closed-source-dll-mods).

## Linux: Wine, Proton and other launchers

Wine has its own `dinput8.dll` and `xinput9_1_0.dll`. For a DLL of the same name in the game folder, Wine decides by
the file name, not by the path, which one runs: the DLL overrides, else its built-in default. Setting a name to
"native, builtin" (`n,b`) makes the game folder's file win.

### Starting the game from Bottles, Lutris or plain Wine
Kiln is `dinput8.dll` in the game folder. A start that does not go through Steam with Proton needs `dinput8` set to
native, builtin: `WINEDLLOVERRIDES="dinput8=n,b"`, or `dinput8` as "native, builtin" in the runner's DLL overrides
(`winecfg`, Libraries tab). Without it the game starts without Kiln, and `kiln/logs/proxy.log` is not written.
- Proton 10 and 11 build their `dinput8` with Wine's "prefer native" flag, so Steam starts need no launch option
  there (tested with GE-Proton11-11, [client.md](client.md); Valve's builds read from their source). Proton 9 and
  upstream Wine do not have that flag (read from their source). The override does no harm where it is not needed.
- Untested: Bottles' and Lutris' own runners. Keep the override.

### Ultimate ASI Loader next to Kiln on Wine
Kiln chains Ultimate ASI Loader as `xinput9_1_0.dll` in the game folder ([Installer](#installer)). Kiln loads it by
its full path, but Wine still applies its rules for the name `xinput9_1_0`. Set it to native, builtin as well:
`WINEDLLOVERRIDES="dinput8,xinput9_1_0=n,b"` (Steam: `WINEDLLOVERRIDES="dinput8,xinput9_1_0=n,b" %command%`).
The launcher names both overrides when Ultimate ASI Loader is chained (by the installer in `[chain]`, or since 0.2.0-dev.29.4 for
the profile's `.asi` packages in `[profile]`) and reports a missing override.
- Tested (2026-11-14, dedicated server lab under GE-Proton11-11, Kiln 0.2.0-dev.28's proxy, a test DLL of ours named
  `xinput9_1_0.dll`, not Ultimate ASI Loader itself): without the override, a DLL without version information did not
  run. Wine loaded its own `xinput9_1_0` instead, and dev.28's `proxy.log` still said `loaded at ...`. With
  `xinput9_1_0=n,b` it ran. Since dev.29 the proxy warns ([Proxy](#proxy)); `native/proxy/tests/test-chain.sh` shows
  the warning under GE-Proton11-11's wine without an override and under Wine 11.20 with `xinput9_1_0=b` (2026-11-15).
- Tested on the same lab: a DLL whose version information names a company other than Microsoft ran without the
  override. GE-Proton11-11 has the rule from Wine 11.6 that prefers such DLLs. Ultimate ASI Loader's current source
  names ThirteenAG as its company, so it should load there without the override (untested with the real loader).
- Read from the source, untested: Valve's Proton has neither the flag nor the rule for `xinput9_1_0`, so it needs the
  override. Upstream Wine has the rule since 11.6, and since 11.7 it also prefers a DLL without version information.
- Untested: the game client, Bottles' and Lutris' runners. The override is the safe setting everywhere.

## Library kinds

`library::detect` reports these kinds besides Kiln's own. Each has `engine` set and a reason a player can read.

| Kind | Detected by |
|---|---|
| `flame` | `flame.json`, or a DLL exporting `FlameStartRuntime` |
| `shroudforge` | SF-specific `extended.mod.json` or a ShroudForge schema; other SF-compatible Lua packages without the EML entry point |
| `eml` | `mod.json` + `src/mod.lua`; shared `capabilities` do not imply SF. An extension with `launcher: "EML"` remains EML |
| `enshwick` | `pack.toml` (a data pack, or a Luau mod with `main.luau`; its `id`, `version` and `name` are read), or a DLL exporting `enshwick_mod_abi` / `_query` / `_init` (the folder is the mod) |
| `set` | `init.lua` |
| `greeneye` | a DLL exporting `GreeneyeModStart` (the folder is the mod) |
| `shroudtopia` | `shroudtopia.json`, or a DLL exporting `CreateModInstance` |
| `shroudlink` | a DLL exporting `shroudlink_mod_v2` or `shroudlink_mod_v1` (the folder is the mod). A folder that also holds EML data mods (`<sub>/mod.json` + `src/mod.lua`) is a folder of mods: each DLL and each EML mod on its own. A release's `shroudlink_mods/` folder with one mod is named after its DLL |
| `ember` | a folder in `ember/mods/` holding a DLL that no other engine claims (Ember's mods have no manifest or known export yet, so the place names them; a lone DLL elsewhere stays Kiln's own bare-DLL rule) |
| `proxy` | another engine's loader or a tool DLL (classification above) |
| `save` | `*.ksd`, `characters`, a world save (`<8 hex>_info`, `-N` backups) |
| `cheat-table` | `.ct`, `.cetrainer` |
| `reshade` | a `.ini` with "reshade" in its name |
| `data` | `.kfc` (patched game files) |
| `asi` | an `.asi` file, Ultimate ASI Loader (export or text `IsUltimateASILoader`, any file name), or a folder with either (directly in it, or `.asi` in a folder of it, as Smart Workshop's `Scripts\`): imported as one native mod that activation places in the game folder ([Files in the game folder](#files-in-the-game-folder)) |

A kind without a loader is skipped on import with its reason. It is never imported as a plain DLL.
An unreadable/unknown `mod.json` or incomplete Lua package stays one skipped item: its helper DLLs are never
imported separately. Standalone DLLs without package metadata retain the existing bare-DLL import flow. These are
Library import rules; a runtime plugin may already run an engine's mods placed in its game-folder layout manually.

### ShroudForge self-hosting

ShroudForge can also host itself. If its own proxy loads, Kiln's adapter steps aside and ShroudForge reads its own
mods folder. Profile switches still update copies Kiln manages. Folders you installed yourself keep their own switches.

Engines shows **own loader installed: <file>** when disk detection finds a loader. Mods on desktop and Deck names the
detected loader when the loaded profile contains ShroudForge mods, including switched off entries. This says the file
is installed. It does not prove it loaded. Remove that file to let Kiln's adapter host ShroudForge.
For ShroudForge's `dinput8.dll`, **Let Kiln manage ShroudForge** opens Runtime's existing **Switch from ShroudForge**
confirmation. That operation backs up only `dinput8.dll`: other SF proxies must be removed separately.
Under Proton a proxy still needs its DLL override to actually load; disk detection is a warning before Play, not proof
of execution. The adapter confirms actual self-hosting in `kiln/state/shroudforge.json` (`mode: self-hosted`,
`loaderFile`) and in one INFO log line. Disk detection is refreshed while Mods is open. If the file was removed since
that record, the warning clears and Engines labels the record as the last start; it does not predict self-hosting.

ShroudForge installation detection classifies `dbghelp.dll` and `winmm.dll` before calling them SF's helpers.

## Adding support for an engine's mods

Write the importer in kiln-manage, then register it in one of two ways:

```rust
// compile time: engines.rs, builtin_loaders()
m.insert(DetectKind::Greeneye, crate::greeneye::LOADER);
// or at run time
kiln_manage::engines::register_loader(DetectKind::Greeneye, Loader { name: "kiln-greeneye", import: my_import });
```

`import` gets an `ImportCtx { dirs, detected, src, from, report }`. It prepares a folder in a `library::TempDir`
below `dirs.library()`, then calls `library::commit_folder(dirs, prepared, ImportRecord { kind, .. }, report)`, or
pushes a `Skipped` entry. With a loader registered, `DetectKind::importable()` is true, and the Deck, drops and Nexus
installs take the kind.

## Managing every engine's mods

Code: `crates/kiln-manage/src/engine_mods.rs`; formats: [launcher/contracts.md](launcher/contracts.md) 5.1 ("Other
engines' mods") and 5.8.

- **Package shapes.** Mods are dropped the way Nexus ships them: a zip, 7z or rar; an extracted folder; the game's own
  `mods` folder; one mod folder. A package shaped like the game folder (`dbghelp.dll` + `dinput8.dll` + `eml.json` +
  `mods/<Name>/mod.json`, or Shroudtopia's `winmm.dll` + `shroudtopia.dll` + `shroudtopia.json` + `mods/`) is a folder of
  mods: every folder and loose mod DLL inside `mods/` is imported (a loose Shroudtopia DLL gets the `mod.json` its plugin
  needs). The engine's own loader in it is never installed and is not an error: the result carries one line of
  information, `EML's own loader is in this package: not needed, Kiln runs EML mods` (the loaders of EMF, EML,
  ShroudForge, SET, Greeneye, Flame, Shroudtopia, Enshwick, shroudlink and Ember; DLL chain loaders get "not needed, Kiln
  loads DLL mods itself"). A tool that runs on its own next to Kiln (Aldrich's mod menu, ReShade, Live Map, Shared XP) is
  left out whole with what to do (copy its files into the Enshrouded folder yourself). Folders such as `fomod/`,
  `screenshots/`, `__MACOSX/` and the readme, licence and script files around a mod are ignored.
- **Ids.** An engine manifest has no id rule of Kiln's (`HoardersHelper`, `XHL-Max-Stack-65535`, `Less Dense Shroud Fog`,
  `antisocialian's tweaks`). A valid Kiln id is kept; any other becomes its slug (`library::id_from_name`:
  `hoardershelper`, `less-dense-shroud-fog`, `antisocialian-s-tweaks`) as the Library and profile id, and the manifest's
  own id goes into the import record (`manifestId`). The mod's files are not changed. Two manifest ids with one slug
  (`Ember` and `ember`) do not share an entry: the second gets a short hash (`ember-3fa9c2`). EML reads only a profile
  entry's `path` and `enabled` and reports each mod's own id and its `path`; the other engines' plugins take the
  profile entry's id as the mod's id, which is the Library id. Enshwick keeps the strict id (its loader copies a mod into
  `mods/<id>/`). A ShroudForge package needs only ShroudForge's own id rule (ASCII letters of both cases, digits, `.-_`,
  at most 80); Kiln places it in ShroudForge's mods folder as `<library id>/`, since ShroudForge finds a package by its
  mod.json id, not its folder name, and leaves it out when a folder there (the player's own copy) holds the same package
  id already: ShroudForge drops every package whose id it finds twice. A server's hand-placed engine mod folder is
  listed under the same slug (`engine_mods::identity`), and switched by its folder name whatever that is. Kiln's own kinds (`kiln`, `native`, `plugin`, `emf`) keep the strict id: their manifests are
  Kiln's contract, and the reason says the author has to change it.
- **A DLL mod with its files.** A plain DLL beside the files it reads (`dinput8.dll` + `.ini`/`.dat`/`.jpg`,
  `powrprof.dll` + a folder, a lone `winmm.dll`) is imported as one native mod: the whole package folder, with a
  generated `native.json` listing the DLL (phase `dllmain` for a `DirectInput8Create` proxy, chain-loaded by Kiln's proxy;
  `early` otherwise), named after the package. Kiln loads the DLL from that folder, so a mod that reads its files next
  to its own DLL finds them; the result says so in one line when the package has such files. A mod that looks for them
  in the game folder does not. One such shape is placed there at activation (since 0.2.0-dev.29.4): a loader under a proxy name
  with a folder of plugin DLLs beside it (`powrprof.dll` + `plugins\`), see [Files in the game folder](#files-in-the-game-folder).
  For any other DLL mod that reads its files from the game folder, Kiln still has no kind. A lone DLL under a proxy name
  gets a few characters of its hash in its id, so two different `winmm.dll` files never share an entry.
- **Mods for CharosDX's loader** (Sparkworks Enhanced, Cross-Base Storage, Craft From Stations; since 0.2.0-dev.29.10).
  The package is a `dinput8.dll` that loads every `<game>\mods\<mod>\native\*.dll` from its DllMain and calls nothing
  in them (source: github.com/CharosDX/sparkworks-enhanced, `src/dinput8_proxy.c`), plus `mods/<mod>/mod.json` (names
  the mod; EML runs nothing there) and `mods/<mod>/native/<mod>.dll`. The loader is left out with a note; each such
  `mods/<mod>` folder (a mod.json, no Lua, a `native` folder whose DLLs no other engine claims) becomes one native mod
  with a generated native.json: its DLLs in the loader's order (by name, case ignored), phase `early` (on the main
  thread before any game code and before Kiln hooks anything, as the loader's DllMain did), side `both` (the loader ran
  wherever it was put; the DLL decides what it does in `enshrouded_server.exe`). The mod.json id is the Library id when
  it is a Kiln id, else its slug. These DLLs keep their settings and logs beside their own DLL (`GetModuleFileNameA` of
  their module): `config.ini` and `debug.log` are written into the Library entry, which counts as the mod's output, not
  an edit (`library::own_output`).
- **Archives.** `.7z` is read in-process (`sevenz-rust2`, pure Rust, no password support, decoder memory capped at 256
  MiB). `.rar` needs a tool that is already on the PC (`unrar`, `7zz`/`7z`, `bsdtar`, Windows' `tar.exe`), found by
  absolute path, started with an empty private working folder, stopped when its output passes the import limits, and its
  output is checked like a folder; without one the reason says "Extract this .rar first, then drop the folder".
  Archives obey the zip limits (10,000 entries, 32 levels, 512 MiB per file, 1 GiB, no links, no `..`); a modpack must
  be a `.zip`.
- **Library.** `engines::builtin_loaders` registers `engine_mods::import_engine_mod` for every engine kind: `eml`,
  `shroudforge`, `set`, `greeneye`, `flame`, `shroudtopia`, `enshwick`, `shroudlink` and `ember`
  (`engine_mods::IMPORTED`). The item goes to `library/<kind>/<id>/<version>+<hash8>/` with its kdh1, like a Kiln
  mod. A lone Greeneye or Shroudtopia DLL gets the `mod.json` its loader needs, a lone Flame DLL a `flame.json`. A
  lone Enshwick or shroudlink DLL is wrapped into a folder with nothing generated: their manifests are compiled into
  the DLL. Each import checks what its plugin needs in the committed copy: an Enshwick `pack.toml` or mod DLL, a
  shroudlink mod DLL, exactly one library in an Ember folder (kiln-ember refuses more). An Enshwick mod may not take
  the id `enshwick` (the loader's own `mods/enshwick/`). Ember needs the folder: a lone DLL says nothing about Ember.
  Enshwick's identity and side come only from `pack.toml`, when present; an adjacent `mod.json` cannot override them.
  Pack metadata is limited to 1 MiB of UTF-8. The conservative reader supports top-level single-line and multiline
  strings with TOML escapes, plus inert booleans and decimal integers; duplicate keys, invalid metadata, containers
  (including author arrays), and unsupported table headers are refused. Extraction ends at the first supported
  table; its body is left to Enshwick. Explicit versions must be canonical SemVer, with numeric core parts at most
  2147483647. IDs must also be safe Windows folder names: no device names or trailing dots.
  For other engines, `side`/`clients` come from the mod's `mod.json` when it states them, else the
  kind's default: EML, ShroudForge, Flame and Enshwick `both`/`mod`, since both sides need the same data (Enshwick's
  join check refuses players without a `both` mod); Greeneye `client`/`none`; shroudlink and Ember `server`/`none`
  always (server frameworks: players never need their mods); others `both`/`none`.
  **Ember's mods don't run yet.** Ember has published no mod ABI, so kiln-ember only finds and reports them
  ([engines/ember.md](engines/ember.md)). The Library, profiles and server sync hold them like any kind so they are in
  place when Ember ships; `engine_mods::runs_yet` is false for `ember`, and the launcher says so on the Library,
  the profile's Engines tab and the Deck.
  Folder and archive imports refuse symlinks, including hidden links and Windows reparse points: Wine's plugins
  cannot read file symlinks. The same bounded import rules apply as for Kiln mods (512 MiB per file, 1 GiB total,
  10,000 entries, 32 levels; ZIP64 refused). An id that is no Kiln id gets a Kiln id and its own stays in the import
  record (above); an empty id, or one with control characters, is refused; missing ids use the normalized name. Versions are canonical semantic versions, `0.0.0` when
  absent, or a `0.0.0` prerelease for engine versions that are not semantic versions (`0.0.0-beta`, with an
  `import-` prefix when needed to make the prerelease valid, such as `0.0.0-import-001`).
- **Profiles.** `ModKind` has one kind per engine, and `Profile.mods.<kind>` and profile.json `mods.<kind>` have one
  list each. Activation writes each list for its plugin. ShroudForge and SET read only their own folder, so
  activation copies the profile's mods there (`engine_mods::place`, marker `.kiln-library.json`). It sets ShroudForge's
  `extended.mod.json` switch as the profile says, switches off Kiln's copies the profile dropped (their settings
  stay), and removes Kiln's copies of SET mods that are off. Ownership requires both the marker and the launcher's
  receipt in `kiln/state/library-placements.json`, with the recorded file inventory still matching (ShroudForge's
  enabled switch and setting values may change, and so may the default `targets` ShroudForge's own in-game menu adds
  to a file without one). A marker alone never grants deletion or replacement rights;
  older marker-only copies and folders changed by the player are left alone. Replacement is staged beside the
  destination and rolls the previous copy back if placement fails. Activation is refused while the game runs.
- **A player's own ShroudForge folder.** A folder in ShroudForge's mods folder that Kiln did not place and that serves a
  profile entry (it is called `<library id>`, or its mod.json holds the entry's package id under any folder name) is
  the player's: activation says so ("the game uses your own folder …"), places no second copy, and neither switches,
  updates nor removes it. **"Let Kiln manage this folder"** (dev.27, `engine_mods::adopt`; the launcher's Notes dialog
  and the mod's settings) makes such a folder Kiln's placement, on the player's request only, never by itself.
  - *When.* Only when its files are byte for byte the Library entry's: the same inventory activation records for a
    copy it places (`placement_files`: every file and folder, hidden ones too, by sha256). Two things in
    `extended.mod.json` are the player's and may differ: the switch and the value of each setting (and the default
    `targets` ShroudForge's own menu adds). Anything else is refused with the folder's path and the first thing that
    differs, in path order: a changed, missing or added file or folder, a link, a setting whose description, range or
    choices were edited, a setting the mod does not have, a file ShroudForge cannot read. Also refused: a second folder
    of the player's with the same package (ShroudForge drops both), a link on the way to the folder, a Library entry
    that changed on disk, and a copy of Kiln's own whose files changed (that one is not the player's folder).
  - *What changes.* Nothing in the folder is rewritten. It gets the marker file, and the name `<library id>` when it
    had another (Kiln's placements are found by that name; ShroudForge finds a package by its mod.json id). The receipt
    lists the Library entry's files, as for a placed copy, so the setting values in the folder stay the player's: they
    are carried to the next version, and a cleanup keeps a folder that holds them (its receipt's `extension` is the
    file Kiln would have written for the folder's switch, not the folder's own).
  - *Afterwards* it is a placement like any other: activation switches it as the profile says, replaces it when the
    profile names another version, switches it off when the mod leaves the profile, and a cleanup (`unplace`: a profile
    deactivated, Kiln uninstalled) removes it. A second request for a folder that is Kiln's already changes nothing.
  - *Order and failure.* Under the engine lock: the name, the marker, then the receipt, and only the receipt makes the
    folder Kiln's (`owned`). A stop before the receipt leaves a folder with a marker and no receipt, which Kiln reads as
    the player's own (a marker alone grants nothing), and the request can be made again. A receipt that cannot be
    written takes the marker and the name back. `engine_mods::own_folders` lists such folders with the refusal each
    would get, without writing anything.
  Only ShroudForge folders are offered. A player's own SET folder, and the folders the other engines' own loaders
  read, stay the player's as before.
  Enshwick's loader also reads only `<game>/mods/`, but kiln-enshwick copies the profile's enabled entries there
  itself (with its own receipts in `kiln/data/enshwick-managed.json`), so its list names the library folder like
  the other kinds; kiln-shroudlink and kiln-ember read the library folder in place.
- **Settings.** `engine_mods::settings` / `set_setting`. ShroudForge: the settings in `extended.mod.json` (the copy in
  ShroudForge's mods folder), as a JSON Schema with Kiln's `x-kiln-*` hints. Shroudtopia: `mods.<key>` in
  `<configDir>/shroudtopia/shroudtopia.json`, with the types taken from the values. EMF keeps its ini editor. EML,
  SET, Greeneye, Flame, shroudlink and Ember mods declare no settings Kiln edits; Enshwick keeps each mod's settings
  per world (`<save>-mod-settings/<id>/settings.toml`), edited in the game's Mods screen or with `enshwick config`. Settings calls require a valid mod id listed in the requested
  profile. ShroudForge writes require that profile active in the selected game and the folder ShroudForge reads for
  the entry: Kiln's unchanged copy, or the player's own folder that serves it (one Kiln did not place, also under
  another folder name with the same package id). A setting the player changes in the launcher is their request, so
  Kiln writes that one value into their folder's `extended.mod.json` too (atomically, links refused, game closed),
  and the settings dialog says it is the player's own folder; switching, replacing and removing such a folder stay
  refused until the player hands it to Kiln ("A player's own ShroudForge folder" above). A folder Kiln did not place is used only when its mod.json id is the entry's package id (a player's folder
  named like the entry that holds another package is not it); when several of the player's folders hold the package,
  nothing is written and the settings say which folders. The write goes through a temp file of a new, random name
  created exclusively, so no file of the mod folder is ever overwritten through it. A copy of Kiln's whose library files
  changed stays refused. Hand edits to a setting's description, range or other metadata in a Kiln copy's
  `extended.mod.json` are not carried to the mod's next version; only the values are (as before dev.26).
  Engine settings writes are refused while a game reading these files runs. Active copy-mode Shroudtopia profiles
  edit the game-side config copy, so activation's copy-back preserves the change. Without a matching selected game,
  a unique active copy is used; multiple active copies require selecting which game to edit.
- **Status.** `engine_mods::status`: `kiln/state/{emf,eml,shroudforge,greeneye,flame,shroudtopia,set,enshwick,shroudlink,ember}.json`; missing files
  mean the plugin has not run yet, while unreadable, invalid or symlinked files report an error.
- **Shared archive backups.** `engine_mods::eml_backup` reports whether `<exe>.kfc` differs from `<exe>.kfc.bak`,
  not which loader wrote it. Inspection reports read failures and refuses symlinks. The legacy `eml_restore_vanilla`
  command now refuses direct copies. Disable EML mods and start the game through Kiln: the separate archive writer
  restores only its own unchanged output, proven by exact output/backup receipts (output of dev.20 to dev.23 once by
  that release's change cache), with loader-state handoff ([eml.md](eml.md)). When it cannot prove ownership it leaves
  the data and writes why into `kiln/state/eml.json` (`reason`); the way back is then to enable any EML mod for one
  start and disable it again, or Steam's "Verify integrity of game files".
- **Servers** keep the legacy layout. The server mod sync (`server::control`) copies each kind into the folder its
  plugin reads without a profile (`engine_mods::server_root`):

  | Kind | Folder |
  |---|---|
  | `emf` | `emf/mods` |
  | `eml` | `kiln/eml/mods` |
  | `shroudforge` | ShroudForge's mods folder |
  | `set` | `shrouded_engine_tweaks/mods` |
  | `greeneye` | `greeneye` |
  | `flame`, `shroudtopia`, `enshwick` | `mods` (Enshwick's loader folder `mods/enshwick/` is never a mod) |
  | `shroudlink` | `shroudlink_mods` (one folder per mod, `shroudlink_mods/<id>/`; kiln-shroudlink reads the DLLs in its direct subfolders too) |
  | `ember` | `ember/mods` |

  Each copy carries the `.kiln-source.json` marker plus a receipt and payload inventory in
  `kiln/state/server-library.json`. A marker alone never permits replacement or removal; older marker-only copies
  and copies with player additions are left alone. Engine sync and switching are refused while the server runs.
  Engines have no common off switch, so a switched-off engine mod moves reversibly to
  `kiln/disabled/<kind>/<id>` (for Enshwick that is its own off rule: the folder leaves `mods/`). ShroudForge
  updates preserve player setting values. Modpacks (`modpack::client_mods`, `build`) carry the engine mods whose
  `clients` is `mod`, as `<kind>/<id>/` with their kdh1: Enshwick's `both` mods among the new kinds, never shroudlink
  or Ember mods. Kiln.Network adds the same mods to the advertised
  `required[]`, each with its `kind` (`AdvertiseEngines.cs`), so a joining player's launcher learns what the server
  runs. The HELLO check still covers Kiln mods only: clients don't announce other engines' mods.
- **Licences.** The launcher (MIT) only copies files. It never links or runs EML's GPL code or any other engine's code.

An enabled EML package declaring `runtime` or `runtime-register-dll` also requires hosted ShroudForge, even without
an enabled ShroudForge mod. The launcher uses the shared package reader and writes `emlRuntime` (package ids) into
`kiln/profile.json`; the adapter reads that additive field, defaulting to empty. The launcher places EML packages in
ShroudForge's mods folder with their `extended.mod.json` switches on. A compatibility entry in `mods.shroudforge`
lets older adapters make the same hosting decision. Runtime EML entries are disabled on the kiln-eml route even
when delegated: ShroudForge reads its placed copy and switch independently. This prevents older kiln-eml runners
from falling back to data-only if their adapter fails; new aside reporting recognizes `emlRuntime` delegation. Missing/off ShroudForge disables the runtime EML entry and
records its reason in `emlRuntimeBlocked`; kiln-eml reports `skipped`. Without a hosted or own ShroudForge runtime,
kiln-eml independently refuses the entire runtime package. A server-only/client-only package remains subject to
ShroudForge's existing targets check (`other-side`); patch/export-only EML packages are unchanged.

Activation and Play refuse a profile with enabled runtime EML packages while EML's standalone loader is detected in a
game-folder proxy slot or an active DLL chain. That loader prevents Kiln from starting the ShroudForge runtime those
packages need. The refusal names the packages and loader before any engine packages are placed. Switch those packages
off, or remove the standalone EML loader from the game folder or its DLL chain, then activate the profile again. This
also applies under Wine or Proton when the proxy has no DLL override. Patch-only profiles and disabled runtime
packages keep their existing behavior. The refusal does not remove packages already placed or repair game data another
loader changed.

### Restore the game's data

For a game folder another mod loader changed before Kiln. kiln-eml then finds game data it cannot prove it wrote
("Game data is not output Kiln's EML writer can prove it published"), leaves it and the shared backups alone, and every
EML mod fails. The launcher's Engines view (Logs, Engines) then offers **Restore the game's data**:

1. Verify the game files in Steam (the game's Properties, Installed Files, "Verify integrity of game files"). Steam
   puts the game's own files back; it does not remove the extra ones.
2. If Steam repaired files, start the game through Kiln once more. kiln-eml reads the game's index at a start and
   writes what it found into `kiln/state/eml.json` (`recovery`: the index's SHA-256, the number of archive containers
   the index refers to, and that the data is unproven); the launcher takes the list of leftover files from that and
   refuses to plan while the index on disk is not the one that was read. EML mods may fail once more in that start.
3. The dialog lists every file with its size. Tick "I verified the game files in Steam" and choose **Move files
   aside**.

What moves, by rename and never by copy: the containers `enshrouded_NNN.dat` numbered at or past the count the game's
own index refers to, the shared backup pair `enshrouded.kfc.bak` and `enshrouded.kfc_resources.bak`, the backup's seal
and Kiln's own change cache (`kiln/eml/cache/files.json`, `files.partial.json`). They go to
`kiln/eml/displaced/restore-<time>/` under their own names, beside a `manifest.json` that records each file's path,
size and SHA-256, the time and how far the move got. Nothing is deleted and nothing is overwritten: a name that already
exists stops the move. The next start through Kiln makes a fresh backup from the game data as it then is. Kiln never
copies a backup over the game's data here.

**Undo move** moves the files back and stays available after the launcher was closed. It is refused when one of the
names exists again or a moved file's content changed. A move or an undo that was interrupted (a crash, a power cut) is
finished the next time the launcher looks at this (the Engines view, or the move itself), with the game closed; when the
game's index changed in between, an unfinished move is undone instead.

Refused, each with its sentence: the game or its server is running; kiln-eml is writing; ShroudForge is the archive
writer of the loaded profile (its own loader is installed, or Kiln hosts it for the profile's mods: the files may then
be ShroudForge's own; a `shroudforge/` folder left from earlier does not count); Kiln has its own record of the data it
wrote (`kiln/eml/ownership/`: then **Restore vanilla game data** above is the way); a link anywhere on a path; nothing
left over. A game folder whose last start reported nothing unproven is not touched at all.

Not covered: a game whose data changed after Kiln had written it (Kiln's record exists but no longer matches: another
loader ran later). That case is as before: verify in Steam, then move the files by hand.

Never run on Windows or against a real game folder; the moves are tested on Linux with an interruption at every step
(`cargo test -p kiln-manage --features full restore_data`), the dialog with `launcher/tests/restore-data.spec.ts`.

Kiln no longer changes the game's five building functions merely by starting `kiln.gameplay`: a selected managed mod
which references `Kiln.Gameplay` (or ships a helper with that reference) requests them during the host's pre-world
preflight, including when it only uses crafting. Native plugins request on first use and should request at startup if
they will later add build/break protection. A first runtime request uses checked safe-Pre installation; build/break
zones are refused until installation, rather than accepted without protection. This lets ShroudForge inspect the
unmodified `PlaceProp` entry when the profile contains no such mod and no native plugin requests the group. Once those
hooks are installed (a typed link writes the same registry entry jump as a raw one), ShroudForge's entity spawning
still cannot find its prop-spawn helper through that entry unless it can read the game's original code. The
ShroudForge Kiln ships (frostdev-ops/shroudforge#2, merge 4a4e81eb) trusts the game's file as this process's image
when the only differences are function-entry detours (whose targets may be any executable memory), jumps inside
functions in the exact forms safetyhook and Kiln write, at original instruction boundaries and bounded, ShroudForge's
own recorded main-menu call sites, and an image moved by address randomisation once its relocations, checked against
the loaded image, are applied. A function is still read through the file only when its own difference is an entry
detour. kiln.log has one line either way: `executable file accepted (load delta ...) beside N entry detour(s), M
interior jump(s) and K own patch(es)` or `executable file refused: <reason> (RVA ...); live-only resolution`. Not seen
in a game yet. A mid-install failure can also leave pass-through jumps in some entries; see the gameplay service's
refusal status and installed-target mask.

### Files in the game folder

Some packages only work from the game folder itself (since 0.2.0-dev.29.4, `crates/kiln-manage/src/game_files.rs`). The Library
keeps them as native mods, and activation places their files there instead of having kiln-core load them: the
profile's `kiln/profile.json` does not list them, the launcher's native plan has them on with the reason "placed in
the game folder at activation" (the mod list says "Runs from the game folder", the load order lists them under "From
the game folder"; a package with two files that would be one on Windows is off, "not placed in the game folder"). Kiln
tells them by their files, so an entry an older launcher imported counts as well:

- **A plugin loader the game loads itself.** The native mod's one DLL sits at the top of its folder under a proxy name
  other than Kiln's `dinput8.dll`, the game's `steam_api64.dll` and `xinput9_1_0.dll` (`powrprof.dll`, `winmm.dll`,
  `version.dll` ...), it has no `init` export in native.json, it is no engine's own loader that Kiln runs the mods of
  (SET's `powrprof.dll` stays as [above](#managing-every-engines-mods)), and a folder beside it holds a DLL: its plugins.
  "Autoloot and coop loot sharing" (Nexus 266: `powrprof.dll`, `plugins\AutolootCoop.dll`, its ini and loot list) is
  this shape; the game imports `POWRPROF.dll`, so the copy next to the game is the one that runs. Every file keeps its
  place below the game folder. A DLL mod with its files but no folder of DLLs (InventoryLock's `winmm.dll` + ini) is
  still loaded from the Library.
- **Ultimate ASI Loader and `.asi` plugins** (Smart Workshop, Nexus 361: a `Scripts` folder with the `.asi`, which
  needs UAL). UAL goes next to the game as `xinput9_1_0.dll`, a name it runs under, and Kiln's proxy loads it from
  `kiln/chain.ini` `[profile]` (`..\xinput9_1_0.dll`). The plugins keep their folder (`Scripts\SmartWorkshop.asi`), in the
  spelling the game folder already has (`scripts\`); files at the top of the package go to `scripts\`, where UAL looks
  (UAL also reads `plugins\` and its own folder). A package of plugins alone uses the UAL that is there: the one Kiln's
  installer chained (`[chain]`, nothing more is written), one the player put there under that name, or the one another
  package of the profile brings (UAL's own download is a `dinput8.dll`: added to the Library it becomes the native mod
  `ultimate-asi-loader`). Without any, nothing of it is placed and activation says so. A file at `xinput9_1_0.dll` that
  is not UAL is left alone.

Documents and installer scripts (`readme*`, `license*`, `.md`, `.pdf`, `.bat` ...) stay in the Library; other `.txt`
files go along (a loot list). Ownership goes file by file, in `kiln/state/game-files.json`: Kiln writes a file only
where there is none or where it still holds the bytes Kiln wrote, and removes only such files when the mod is switched
off or leaves the profile, when the profile is deactivated and when Kiln is uninstalled; folders it made go when they
are empty. A code file (`.dll`, `.asi`) that is there already and that Kiln did not place (SET's `powrprof.dll`, the
player's own copy) keeps the whole package out, with a sentence naming the file: Kiln leaves a game-folder file it did
not place alone. The same bytes, put there by hand, serve and stay the player's: Kiln does not remove them. A settings
file the player or the mod changed stays where it is, also when the mod goes; a code file Kiln placed that changed
since is neither replaced nor removed and shows as a retained placement. Play once does not change these files: a
profile with other such mods than the active one is activated first.

Under Wine or Proton the game takes a game-folder DLL under a proxy name only with a DLL override for that name (a
loader: `powrprof=n,b`; the activation says it). For Ultimate ASI Loader chained from `[profile]` the launcher's Steam
launch option check asks for `xinput9_1_0` as it does for the installer's ([Ultimate ASI Loader next to Kiln on
Wine](#ultimate-asi-loader-next-to-kiln-on-wine)).

Not covered: a DLL mod that reads only its settings from the game folder (no folder of plugin DLLs), and dedicated
servers (they keep the legacy layout, no profile activation places these files).

### Shroudlink client addon

Shroudlink packages run in Kiln's `kiln-shroudlink` server core. The in-game MODS tab next to Skills is drawn by
Shroudlink's separate client addon. Import the addon from the Shroudlink download into the Library as a native mod and
switch it on in the client profile.

The addon loads Shroudlink's own core when `shroudlink_core.dll` is next to the game (its singleplayer mode; the
Library leaves the core of the singleplayer package out, kiln-shroudlink is that core). A core left there by an earlier
hand installation then runs, kiln-shroudlink steps aside, and that core reads only `shroudlink_mods\*.dll`: the
profile's Shroudlink mods do not load ("no mods on this server"). Since 0.2.0-dev.29.4 the Engines view and the activation name
that file ("Remove shroudlink_core.dll from the game folder: Kiln then runs them, and the addon keeps its MODS tab"),
also when the addon comes from the Library through `chain.ini` `[profile]`; kiln-shroudlink's own hint in `kiln.log`
and `kiln/state/shroudlink.json` names the core file it found, and so does a support report.

On desktop and Steam Deck, the mods list explains when a client profile enables Shroudlink mods without loading the
addon: the mods still run, but the tab needs the addon. If an addon is in the Library, **Switch addon on** enables it
in that profile. Otherwise the notice names the Shroudlink download. Support reports include an informational finding
naming the profile and enabled mods. Dedicated-server installs do not need the client addon and show no notice.
