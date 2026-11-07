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

## Library kinds

`library::detect` reports these kinds besides Kiln's own. Each has `engine` set and a reason a player can read.

| Kind | Detected by |
|---|---|
| `flame` | `flame.json`, or a DLL exporting `FlameStartRuntime` |
| `shroudforge` | SF-specific `extended.mod.json` or a ShroudForge schema; other SF-compatible Lua packages without the EML entry point |
| `eml` | `mod.json` + `src/mod.lua`; shared `capabilities` do not imply SF. An extension with `launcher: "EML"` remains EML |
| `enshwick` | `pack.toml`, or a DLL exporting `enshwick_mod_abi` / `_query` / `_init` (the folder is the mod) |
| `set` | `init.lua` |
| `greeneye` | a DLL exporting `GreeneyeModStart` (the folder is the mod) |
| `shroudtopia` | `shroudtopia.json`, or a DLL exporting `CreateModInstance` |
| `proxy` | another engine's loader or a tool DLL (classification above) |
| `save` | `*.ksd`, `characters`, a world save (`<8 hex>_info`, `-N` backups) |
| `cheat-table` | `.ct`, `.cetrainer` |
| `reshade` | a `.ini` with "reshade" in its name |
| `data` | `.kfc` (patched game files) |
| `asi` | `.asi` |
| `shroudlink`, `ember` | reserved for their mod formats |

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

- **Library.** `engines::builtin_loaders` registers `engine_mods::import_engine_mod` for `eml`, `shroudforge`, `set`,
  `greeneye`, `flame` and `shroudtopia`. The item goes to `library/<kind>/<id>/<version>+<hash8>/` with its kdh1,
  like a Kiln mod. A lone Greeneye or Shroudtopia DLL gets the `mod.json` its loader needs, a lone Flame DLL a
  `flame.json`. `side`/`clients` come from the mod's `mod.json` when it states them, else the kind's default (EML,
  ShroudForge, Flame: `both`/`mod`, since both sides need the same data; Greeneye: `client`/`none`; others
  `both`/`none`). `enshwick`, `shroudlink` and `ember` are reserved kinds: still skipped with their reason.
  Folder and archive imports refuse symlinks, including hidden links and Windows reparse points: Wine's plugins
  cannot read file symlinks. The same bounded import rules apply as for Kiln mods (512 MiB per file, 1 GiB total,
  10,000 entries, 32 levels; ZIP64 refused). Explicit invalid ids are refused rather than renamed away from the
  engine manifest; missing ids use the normalized name. Versions are canonical semantic versions, `0.0.0` when
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
- **Settings.** `engine_mods::settings` / `set_setting`. ShroudForge: the settings in `extended.mod.json` (the copy in
  ShroudForge's mods folder), as a JSON Schema with Kiln's `x-kiln-*` hints. Shroudtopia: `mods.<key>` in
  `<configDir>/shroudtopia/shroudtopia.json`, with the types taken from the values. EMF keeps its ini editor. EML,
  SET, Greeneye and Flame mods declare no settings. Settings calls require a valid mod id listed in the requested
  profile. ShroudForge writes require that profile active in the selected game and a Kiln-owned placed copy.
  Engine settings writes are refused while a game reading these files runs. Active copy-mode Shroudtopia profiles
  edit the game-side config copy, so activation's copy-back preserves the change. Without a matching selected game,
  a unique active copy is used; multiple active copies require selecting which game to edit.
- **Status.** `engine_mods::status`: `kiln/state/{emf,eml,shroudforge,greeneye,flame,shroudtopia,set}.json`; missing files
  mean the plugin has not run yet, while unreadable, invalid or symlinked files report an error.
- **Restore vanilla game data.** `engine_mods::eml_backup` / `eml_restore_vanilla`: copies `<exe>.kfc.bak` over a
  patched `<exe>.kfc`, which is what kiln-eml does itself when no EML mod is left. Inspection reports read failures;
  symlinks are refused and restore is refused while the game runs.
- **Servers** keep the legacy layout. The server mod sync (`server::control`) copies each kind into the folder its
  plugin reads without a profile (`engine_mods::server_root`):

  | Kind | Folder |
  |---|---|
  | `emf` | `emf/mods` |
  | `eml` | `kiln/eml/mods` |
  | `shroudforge` | ShroudForge's mods folder |
  | `set` | `shrouded_engine_tweaks/mods` |
  | `greeneye` | `greeneye` |
  | `flame`, `shroudtopia` | `mods` |

  Each copy carries the `.kiln-source.json` marker plus a receipt and payload inventory in
  `kiln/state/server-library.json`. A marker alone never permits replacement or removal; older marker-only copies
  and copies with player additions are left alone. Engine sync and switching are refused while the server runs.
  Engines have no common off switch, so a switched-off engine mod moves reversibly to
  `kiln/disabled/<kind>/<id>`. ShroudForge updates preserve player setting values. Modpacks (`modpack::client_mods`, `build`) carry the engine mods whose
  `clients` is `mod`, as `<kind>/<id>/` with their kdh1. Kiln.Network adds the same mods to the advertised
  `required[]`, each with its `kind` (`AdvertiseEngines.cs`), so a joining player's launcher learns what the server
  runs. The HELLO check still covers Kiln mods only: clients don't announce other engines' mods.
- **Licences.** The launcher (MIT) only copies files. It never links or runs EML's GPL code or any other engine's code.
