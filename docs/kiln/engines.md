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
| `unknown` / `notdll` / `none` | any other 64-bit DLL / not a 64-bit DLL / no file |

The markers start from EMF's `known_tools.txt` (MIT). A marker that turns out wrong for a release is a one-line fix in
the `ENGINES` table.

## Installer

Kiln owns only `dinput8.dll`.

- `kiln`: replaced (backup). `notdll`: refused.
- `shroudforge`: refused unless "switch from ShroudForge". Then only ShroudForge's `dinput8.dll` goes to the backup.
  Its `dbghelp.dll`, `winmm.dll` and `shroudforge/` stay, and Kiln's ShroudForge plugin drives the installed runtime.
- Every other kind: chained. The installer moves the DLL to `kiln/chain/<word>-dinput8.dll` and adds it to
  `chain.ini [chain]`. Occupied chain names always get a numbered suffix, even for identical bytes, directories or
  dangling links. Ultimate ASI Loader runs only under names it knows, so it is chained as `xinput9_1_0.dll` next
  to the game (`..\xinput9_1_0.dll` in chain.ini). If that name is taken (case ignored, even identical bytes), the install is refused.
- DLLs under any other proxy name are never moved. EML's `dbghelp.dll`, Shroudtopia's `winmm.dll` and SET's
  `powrprof.dll` keep running next to Kiln.
- The receipt's `replaced[].engine` names the engine. Uninstall restores every file byte-identically.
- A full/oversized `chain.ini` is refused before taking the proxy slot. Symlinks on installation paths are refused;
  runtime bundles cannot own other proxy slots or installer recovery paths. Each install's backups have a unique
  suffix. An error, including a receipt-write error, rolls back files and restores the chain file's exact previous
  bytes. If the OS prevents rollback, the error names the retained recovery staging folder; later installs never
  delete originals held there.

## Proxy

The proxy reads `dinput8-chain.ini` `[Chain]` and `[EMF]` (the DLLs EMF 0.5's installer renamed) in file order. Both
use EMF's rules: relative paths inside the game folder only (including the final target of directory links), at most
256 entries across both sections, dedupe by canonical path. Files above 256 KiB are ignored. One overflow entry is
reported as refused; further entries are discarded. EMF's loader is accepted by its exports even without its marker,
and even though it carries ShroudForge's export name. ShroudForge's own `dinput8.dll` is still refused. PE headers,
sections and export names are checked against file-backed bounds, without loading the DLL for classification.
Containment compares the final path of the DLL with the final path of the game folder, so a game folder reached through
a link, a mapped drive (Proton's `S:`) or at a drive root is accepted. Under Wine/Proton, Unix symlinks are invisible to
Windows path APIs, so a Unix symlink inside the game folder counts as part of it (making one needs write access to the
game folder, which already allows placing a DLL there); absolute, drive and `..` entries are refused before resolving.

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
| `asi` | `.asi` |

A kind without a loader is skipped on import with its reason. It is never imported as a plain DLL.
An unreadable/unknown `mod.json` or incomplete Lua package stays one skipped item: its helper DLLs are never
imported separately. Standalone DLLs without package metadata retain the existing bare-DLL import flow. These are
Library import rules; a runtime plugin may already run an engine's mods placed in its game-folder layout manually.
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
  to its own DLL finds them; the result says so in one line when the package has such files. A mod that looks for them in the game folder does not: that needs files placed into the game
  folder by profile activation, which the Library has no kind for yet. A lone DLL under a proxy name gets a few characters of its
  hash in its id, so two different `winmm.dll` files never share an entry.
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
  enabled switch and setting values may change). A marker alone never grants deletion or replacement rights;
  older marker-only copies and folders changed by the player are left alone. Replacement is staged beside the
  destination and rolls the previous copy back if placement fails. Activation is refused while the game runs.
  Enshwick's loader also reads only `<game>/mods/`, but kiln-enshwick copies the profile's enabled entries there
  itself (with its own receipts in `kiln/data/enshwick-managed.json`), so its list names the library folder like
  the other kinds; kiln-shroudlink and kiln-ember read the library folder in place.
- **Settings.** `engine_mods::settings` / `set_setting`. ShroudForge: the settings in `extended.mod.json` (the copy in
  ShroudForge's mods folder), as a JSON Schema with Kiln's `x-kiln-*` hints. Shroudtopia: `mods.<key>` in
  `<configDir>/shroudtopia/shroudtopia.json`, with the types taken from the values. EMF keeps its ini editor. EML,
  SET, Greeneye, Flame, shroudlink and Ember mods declare no settings Kiln edits; Enshwick keeps each mod's settings
  per world (`<save>-mod-settings/<id>/settings.toml`), edited in the game's Mods screen or with `enshwick config`. Settings calls require a valid mod id listed in the requested
  profile. ShroudForge writes require that profile active in the selected game and a Kiln-owned placed copy.
  Engine settings writes are refused while a game reading these files runs. Active copy-mode Shroudtopia profiles
  edit the game-side config copy, so activation's copy-back preserves the change. Without a matching selected game,
  a unique active copy is used; multiple active copies require selecting which game to edit.
- **Status.** `engine_mods::status`: `kiln/state/{emf,eml,shroudforge,greeneye,flame,shroudtopia,set,enshwick,shroudlink,ember}.json`; missing files
  mean the plugin has not run yet, while unreadable, invalid or symlinked files report an error.
- **Shared archive backups.** `engine_mods::eml_backup` reports whether `<exe>.kfc` differs from `<exe>.kfc.bak`,
  not which loader wrote it. Inspection reports read failures and refuses symlinks. The legacy `eml_restore_vanilla`
  command now refuses direct copies. Disable EML mods and launch through updated Kiln: the separate archive writer
  restores only its own unchanged output using exact output/backup receipts and loader-state handoff ([eml.md](eml.md)).
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
