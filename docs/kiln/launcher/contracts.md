# Contracts

The formats, flows, names and module boundaries the launcher, kilnctl and the runtime share. Admin transport:
[admin-protocol.md](admin-protocol.md).

Adding optional fields, new commands, new error codes or new enum values at the end is fine; every reader must
ignore unknown keys. Anything else is a format change: update every reader with it.

Machine-readable copies:
- JSON Schemas (draft 2020-12) in `schema/*.schema.json`, examples in `schema/examples/`, fixtures in
  `schema/fixtures/`.
- Rust types in `crates/kiln-manage`. `cargo test -p kiln-manage`
  deserializes every example and checks the kdh1 fixtures and the KAP test vector.
- C ABI in `native/core/abi/kiln_api.h`, `kiln_native.h`, `services/kiln_profile.h`.
- Tauri commands in `launcher/src-tauri/src/commands/*.rs` and `launcher/src/api/commands.ts`.

## 1. Conventions (all formats)
- JSON files are UTF-8 without BOM, keys in camelCase, and carry `"format": 1` (the format's major version). Readers
  refuse a larger `format`, ignore unknown keys, and treat a missing optional key as its default.
- Files Kiln or the launcher write are replaced atomically: write `<name>.tmp` in the same folder, flush, rename over
  the target. Readers never see half a file.
- Times are RFC 3339 UTC strings (`2026-11-04T12:00:00Z`). Exception: `play-once.ini` `created` (unix seconds, or
  RFC 3339 with a zone).
- Hashes are lower-case hex. `sha256` = 64 hex chars; directory hashes are `kdh1:<64 hex>` (section 5.11); mod-set
  fingerprints are `kms1:<64 hex>`.
- Mod ids follow the manifest rule `^[a-z0-9][a-z0-9._-]{0,63}$` for every kind (Kiln, native, EMF).
- "Game-side path": a path as the game process sees it (section 6). "Host path": as the launcher sees it.
- Relative paths inside formats use `/` and never contain `..`, unless the format says otherwise.
- Kiln's own JSON (manifests, configs) may contain `//` comments and trailing commas; the launcher's formats
  (profile.json, receipts, state files) are plain JSON.

## 2. Files at a glance
| File | Written by | Read by | Schema / example |
|---|---|---|---|
| `<game>/kiln/profile.json` | launcher (activation) | kiln-core, Kiln.Host, native loader, kiln-emf | `profile.schema.json`, `examples/profile.json`, `profile-copy.json` |
| `<game>/kiln/profile.once.json` | launcher (launch mode once) | kiln-core | same schema |
| `<game>/kiln/play-once.ini` | launcher | proxy (deletes it) | section 5.2, `fixtures/ini/play-once/`, `examples/play-once.ini` |
| `<game>/kiln/chain.ini` | installer ([chain]), activation ([profile]) | proxy | section 5.3, `fixtures/ini/chain/`, `examples/chain.ini` |
| `<game>/kiln/chain/once.ini` | launcher (launch mode once) | proxy | section 5.3, `examples/once.ini` |
| `<game>/dinput8-chain.ini` | EMF's installer | proxy (EMF rules) | EMF `docs/modding/other_mods.md` |
| `native.json` (mod folder) | mod author | native loader, launcher | `native.schema.json`, `examples/native.json`, `native-plain.json` |
| `<configDir>/native.json` | launcher | native loader | `native-state.schema.json`, `examples/native-state.json` |
| `<configDir>/.schema/<file>.json` | Kiln.Api `Config.Register`, kiln-emf | launcher | `config-schema.schema.json`, `examples/config-schema*.json` |
| `kiln-runtime.json` (bundle root) | `kilnctl bundle create` | installer | `kiln-runtime.schema.json`, `examples/kiln-runtime.json` |
| `<game>/kiln/.install.json` | installer | installer, launcher | `install-receipt.schema.json`, `examples/install-receipt.json` |
| `kiln-modpack.json` (modpack root) | modpack build | launcher | `kiln-modpack.schema.json`, `examples/kiln-modpack.json` |
| `<game>/kiln/state/boot.json` | kiln-core | launcher | `state-boot.schema.json`, `examples/state-boot.json` |
| `<game>/kiln/state/mods.json` | Kiln.Host | launcher | `state-mods.schema.json`, `examples/state-mods.json` |
| `<game>/kiln/state/native.json` | native loader (core) | launcher | `state-native.schema.json`, `examples/state-native.json` |
| `<game>/kiln/state/emf.json` | kiln-emf | launcher | format in `docs/re/emf-compat.md` |
| `<game>/kiln/state/last-server.json` | Kiln.Network (client) | launcher | `state-last-server.schema.json`, `examples/state-last-server.json` |
| `<server>/kiln/data/kiln-admin/endpoint.json` | Kiln.Admin | launcher, kilnctl | `admin-endpoint.schema.json`, `examples/admin-endpoint.json` |
| `<server>/kiln/data/kiln-admin/admin-token` | Kiln.Admin (first start) | launcher, kilnctl | 64 hex chars ([admin-protocol.md](admin-protocol.md)) |
| `<game>/kiln/logs/proxy.log` | proxy | humans, launcher | one line per decision, rewritten at each start |
| `kiln.mod.json` | mod author | Kiln.Host, Kiln.Sdk, launcher | `kiln.mod.schema.json` (+ `clients`) |
| `content/*.json` (mod folder) | mod author | kiln.content, `kiln lint` | `content-pack.schema.json` |
| `skills/*.json` (mod folder) | mod author | kiln.skills, Kiln.Skills, `kiln lint` | `skills.schema.json` |
| `assets/assets.json` (mod folder) | mod author, cook tools | kiln.assets, `kiln lint` | `assets.schema.json` |
| `assets/cook.json`, `impact/cook.json` (mod folder) | mod author | modelcook, soundcook, tools/impact, `kiln lint` | `cook.schema.json` |
| `impact/natives.json` (mod folder) | mod author | tools/impact, kiln.abilities, `kiln lint` | `impact-natives.schema.json` |
| `*.kschem` header | Kiln.Build | Kiln.Build, `kiln lint` | `kschem-header.schema.json` |
| `schema/schemas.json`, `schema/idrefs.json` | `kiln lint` (`idrefs.json` is generated) | `kiln lint`, tools/vscode | which schema and JSON dialect each file has; where files name game entries |
| `kiln lint --json`, `library_lint` result | `kiln lint` | tools/vscode, launcher | `lint-report.schema.json`, `fixtures/lint/output.json` |

Shared definitions (ids, semver, kdh1, game paths): `schema/common.schema.json`. Validate every example with
`bun run check:schemas` in `launcher/`.

## 3. Launcher data
Root: `$KILN_DATA_DIR`, else `$XDG_DATA_HOME/kiln` or `~/.local/share/kiln` (Linux), `%LOCALAPPDATA%\Kiln` (Windows).
`kiln_manage::paths::DataDirs`.

| Path | What |
|---|---|
| `settings.json` | launcher settings (`LauncherSettings`, section 9) |
| `library/<kind>/<id>/<version>+<hash8>/` | immutable library entries; kind `kiln`, `native`, `plugin`, `emf`, or another engine's (`eml`, `shroudforge`, `set`, `greeneye`, `flame`, `shroudtopia`, `enshwick`, `shroudlink`, `ember`), see 5.1; hash8 = the first 8 hex chars of the folder's kdh1 (of the DLL's sha256 for a plugin, whose folder holds the one DLL) |
| `library/<kind>/<id>/<version>+<hash8>.json` | import record next to the folder: `{format, kind, id, version, hash, imported, source:{type,from}, size}` |
| `profiles/<pid>/meta.json` | the profile definition (`kiln_manage::profile::Profile`) |
| `profiles/<pid>/config/` | the profile's mod configs (its `configDir`), with `.schema/` and `native.json` |
| `profiles/<pid>/data/` | per-mod data when the profile chooses `dataDir: profile` |
| `profiles/<pid>/save-backups/<yyyymmddThhmmssZ>/` | automatic save backups, 10 kept (+ `backup.json`) |
| `installs/<installId>.json` | the launcher's record of a game install: the `GameInstall` + a copy of its receipt |
| `runtime/kiln-runtime-<version>.zip` | runtime bundles (import, rollback) |
| `schema-cache/<modid>/<version>+<hash8>/<file>.json` | config schema snapshots cached per mod version |
| `backups/` | `saves/<gameId>/` (save backups without a profile), `install/<installId>/` (mirror of install backups), `profiles/` (deleted profiles) |
| `logs/launcher.log` | the launcher's own log |
| `update.json` | the self-update's state: `{format, skipped, checked, updated: {from, to, at, source, notes}}` (releasing.md) |
| `servers/<id>.json` | launcher records of servers that are not kilnctl instances (steam, windows, remote) and renames (`ServerInstance`) |
| `modpacks/downloads/<sha256>.zip`, `modpacks/built/<serverId>/`, `modpacks/staging/`, `modpacks/dismissed.json` | modpack downloads (reused when the sha256 matches), built server modpacks, unpack staging, offers and refusals the user dismissed (`{gameId: {serverId: modpack sha256 | "refused <written>"}}`) |
| `nexus/` | the catalog on Nexus Mods ([catalog.md](catalog.md)): `oauth.json` (OAuth token pair and expiry, 0600, only without an OS keyring backend; existing backend failures are surfaced; retired personal-key entries are removed), `handler.json` (the nxm opt-in and the handler before Kiln; a build with Nexus off gives the links back at start and deletes it: `{format, registered, previous: {type: "desktop", id} \| {type: "command", command, icon, scope: "hkcu"\|"hklm"} \| null, exe, registeredAt, snapshot}`; format 2 snapshots preserve overwritten OS state before mutation), `verdicts.json` (what each mod's files hold, per mod and its `updated_at`), `installs.json` (mods installed from the catalog: `{format, mods: InstallRecord[]}`), `latest.json` (newest version per mod file, checked at most every 30 min), `inbox/*.json` (nxm links handed over by a second launcher process; private owner-only directory/files, bounded reads by the instance-lock owner), `downloads/<mod>-<file>/download-<random>/`, state transaction locks, `instance.lock` (held while a launcher runs) |

Profile ids: `^[a-z0-9][a-z0-9-]{0,47}$`, derived from the name (`Server: Ember Isles` -> `server-ember-isles`,
a `-2` suffix on clashes).

## 4. Game folder (`<game>/kiln/`)
Today's layout stays (`tools/deploy-server.sh`): `kiln-core.dll`, `kiln.json`, `sigdb/`, `services/`, `plugins/`,
`host/`, `dotnet/`, `mods/`, `config/`, `data/`, `logs/`, `control/`, `crash-reports/`. The launcher adds:

| Path | What |
|---|---|
| `profile.json`, `profile.once.json` | section 5.1 |
| `play-once.ini` | section 5.2 |
| `chain.ini`, `chain/` (displaced proxies, `once.ini`) | section 5.3 |
| `native/<id>/` | native mod drop-in folder (a folder with `native.json`) |
| `profiles/<pid>/<kind>/<id>/`, `profiles/<pid>/config/` | copy mode (section 6) |
| `../mods/<id>/` (ShroudForge's mods folder, `shroudforge::mods_dir`), `../shrouded_engine_tweaks/mods/<id>/` | ShroudForge and SET mods of the active profile, placed by activation with a `.kiln-library.json` marker (`{format, kind, id, version, hash}`; 5.1). Folders without the marker are the player's and are never touched |
| `state/` | status files for the launcher (section 5.9); created by kiln-core at boot (`KILN_PATH_STATE`) |
| `.install.json` | install receipt (section 5.7) |
| `backups/install-<yyyymmddThhmmssZ>/` | what the installer replaced, byte-identical |
| `.staging-<random>/` | installer staging; deleted at the end, stale ones deleted by the next install |
| `logs/proxy.log` | the proxy's decisions |

Servers keep their layout: the launcher syncs library mods into `<server>/kiln/mods/<id>/` with
`.kiln-source.json` (`{format, library:{kind,id,version,hash}, synced, by}`) and switches them off with an empty
`.disabled` file; native mods go to `<server>/kiln/native/<id>/`. Other engines' mods go to the folder their plugin
reads on a server (`engine_mods::server_root`: `emf/mods`, `kiln/eml/mods`, ShroudForge's mods folder,
`shrouded_engine_tweaks/mods`, `greeneye`, `mods`) with the same marker; switched off, they move to
`<server>/kiln/disabled/<kind>/<id>/`. Profiles are a client feature.

World data (`kiln/data/worlds`) is never per profile and never moves with `dataDir`.

## 5. Formats

### 5.1 `profile.json`
```json
{
  "format": 1, "id": "ember-run", "name": "Ember run", "generation": 7,
  "written": "2026-11-04T12:00:00Z", "writer": "kiln-launcher 0.2.0-dev", "pathMode": "z",
  "configDir": "Z:\\home\\user\\.local\\share\\kiln\\profiles\\ember-run\\config",
  "dataDir": null, "dropins": false, "fingerprint": "kms1:…", "server": null,
  "mods": {
    "kiln":   [{ "id": "new-items", "version": "1.0.0", "path": "Z:\\…\\library\\kiln\\new-items\\1.0.0+3f2a9c1d", "hash": "kdh1:…", "enabled": true }],
    "native": [{ "id": "creative-mode", "version": "1.3.0", "path": "Z:\\…\\library\\native\\creative-mode\\1.3.0+0badc0de" }],
    "emf":    [{ "id": "beastmaster", "version": "0.3.3", "path": "Z:\\…\\library\\emf\\beastmaster\\0.3.3+c0ffee00" }],
    "plugin": [{ "path": "Z:\\…\\library\\plugin\\my-plugin\\1.0.0+12345678\\my-plugin.dll" }],
    "eml":    [{ "id": "bigger-stacks", "version": "0.1.0", "path": "Z:\\…\\library\\eml\\bigger-stacks\\0.1.0+0a1b2c3d" }],
    "shroudforge": [{ "id": "sfpkg", "version": "1.0.0", "path": "Z:\\…\\Enshrouded\\mods\\sfpkg", "enabled": false }]
  }
}
```
Rules:
- Required: `format`, `id`, `generation`, `mods`. `generation` grows with every write; kiln-core echoes `id` and
  `generation` in `state/boot.json`, so the launcher knows which profile the running game booted.
- `mods.<kind>[]` keep the launcher's order; each loader still sorts with its resolver (managed: Resolver.cs rules;
  native: the same rules over `after`/`before`/`requires`/`incompatible`; EMF: EMF's own order). `enabled: false`
  entries are reported as disabled and skipped.
- `path`: absolute game-side path (Z: or a Windows path), or relative to `<game>/kiln/` (copy mode). Kiln mods, native
  mods and EMF mods name a folder; plugins name the DLL.
- `configDir` (default `<game>/kiln/config`) becomes `KILN_PATH_CONFIG`, `KilnPaths.Config` and
  `kiln.profile.config_dir()`. Mod config files (`<id>.json`), `.schema/`, `native.json` and EMF settings
  (`emf/<id>.ini`) live there.
- `dataDir` (default `<game>/kiln/data`): per-mod data (`<dataDir>/<id>/`); `kiln/data/worlds` stays put.
- `dropins`: `false` (default) loads only the listed mods; `true` also loads every kind's drop-in folder
  (`kiln/mods`, `kiln/native`, `kiln/plugins`, `emf/mods`) after them; a list (`["plugin"]`) does it for those kinds.
  A drop-in mod whose id is also listed in the profile is skipped (the profile wins).
- `server`: the server a modpack profile was made for (`id`, `name`, `address`, `modpack{url,sha256,name,version}`).
- Missing file: the legacy layout (drop-in folders only, `kiln.profile` mode LEGACY). Unreadable or invalid file: no
  mods of any kind, mode INVALID, the error in `kiln.log`, `state/boot.json` `profile.error` and the
  `kiln.profile.json()` text. Never fall back to the drop-ins silently.
- `profile.once.json` has the same format; kiln-core reads it instead when play-once `mode=profile` (section 5.2).
- Safety limits: 16 MiB file, JSON nesting 128, at most 4,096 entries per known kind; paths are at most 32,767
  bytes and contain no control characters. Optional versions are semantic versions of at most 256 bytes; folder
  hashes use kdh1 and plugin hashes SHA-256. Generations are in `1..9223372036854775807`. Limit violations make
  the profile INVALID with a specific error.

**Other engines' mods** (`mods.<kind>`, same entry shape as `emf`). kiln-core validates these lists like its own (a
broken entry makes the profile INVALID), counts them in `kiln.log` and `boot.json` `profile.counts.<kind>` and lists
missing folders in `profile.missing[]`, but `kiln.profile` `count()/get()` cover only the four Kiln kinds (no ABI
change): each engine's plugin reads its own list from `kiln.profile.json()`. Older cores warn
`unknown mod kind "mods.<kind>" ignored` and run on. A list is written only when it has entries, so a Kiln-only
profile is byte-identical to older launchers' files. `dropins` may name engine kinds; `true` is written when the
profile turns on all four Kiln kinds (as before), and the engine plugins then read their own folders too.
Older cores can ignore the new `mods.<kind>` lists, but their `dropins` arrays reject unknown names: engine-specific
drop-in selections require an updated core. `dropins: true` keeps the older all-Kiln-kinds representation.

| Kind | Plugin | Entry `path` | How the plugin takes the list |
|---|---|---|---|
| `emf` | kiln-emf | library folder | `kiln.profile` `get(KILN_PROFILE_KIND_EMF)` |
| `eml` | kiln-eml (GPL) | library folder | `discover::profile_candidates` over `json()`; `enabled: false` = disabled |
| `shroudforge` | kiln-shroudforge | the placed copy in ShroudForge's mods folder | hosts ShroudForge when an entry is enabled; ShroudForge reads its mods folder. Activation places each listed package there and writes its `extended.mod.json` `enabled` (`shroudforge::apply_switches`); Kiln's copies the profile no longer lists are switched off (settings kept); a new version keeps the player's setting values |
| `set` | kiln-set | the placed copy in `shrouded_engine_tweaks/mods/` | kiln-set reads that folder. Activation places the enabled mods and removes Kiln's copies of disabled or unlisted ones (SET has no switch). kiln-set loads nothing under play-once no-mods or an invalid profile |
| `greeneye` | kiln-greeneye | library folder (`mod.json` + DLL) | `compat.h` `plan_engine("greeneye")` |
| `flame` | kiln-flame | library folder (`flame.json`) | `plan_engine("flame")` |
| `shroudtopia` | kiln-shroudtopia | library folder (mod DLL) | `plan_engine("shroudtopia")`; settings in `<configDir>/shroudtopia/shroudtopia.json` |
| `enshwick` | kiln-enshwick | library folder (`pack.toml`, or a folder with the mod DLL) | `plan_engine("enshwick")`; the plugin copies each enabled entry into `<game>/mods/<id>/` (Enshwick's loader reads only that folder), with its own receipts, and starts Enshwick's loader |
| `shroudlink` | kiln-shroudlink | library folder (mod DLLs directly in it) | its own `plan_from` (the same rule as `compat.h`); roots are the enabled entries, then `shroudlink_mods/` and its direct subfolders |
| `ember` | kiln-ember | library folder (one library) | `plan_engine("ember")`; finds and reports, starts nothing: Ember has published no mod ABI (the launcher says the mods don't run yet) |

Placement ownership requires `.kiln-library.json` plus a matching launcher receipt in
`kiln/state/library-placements.json` (`{path, marker, files}` entries, recording relative paths, file hashes and
directory entries).
The marker alone is advisory: marker-only older copies and player-modified folders are left untouched. The
inventory excludes only ShroudForge's mutable enabled switch and setting values. Copies are staged beside the
destination and the previous folder is restored if replacement fails. Activation refuses while the game runs.
In copy mode, the ShroudForge and SET profile entry paths name the verified source copies under
`kiln/profiles/<profile>/<kind>/<id>`; their plugins still read the separately placed fixed engine folders.
Play once cannot select different ShroudForge or SET mods, versions or switches from the active profile: activate
that profile first to change their fixed folders. A refused play-once request leaves those folders unchanged.

`compat.h`'s rule for Greeneye, Flame, Shroudtopia, Enshwick, shroudlink and Ember: with a `mods.<kind>` list the profile's entries load and the
engine's own folder is read only when `dropins` is `true`; without the list the folder is read (mods the player put
there by hand keep working).

### 5.2 `play-once.ini`
```ini
; Kiln play-once flag. The proxy reads it, deletes it at once, and applies it to this one start only.
mode = no-mods
created = 1762257600
nonce = 4f1c0e3a9b2d4e6f
```
INI dialect (shared with chain.ini): UTF-8 with or without BOM, UTF-16LE with BOM, or ANSI (the system code page);
LF or CRLF; lines are trimmed (spaces, tabs); blank lines and lines starting with `;`, `#` or `//` are skipped;
`[section]` headers (case-insensitive). Reference parser: `schema/fixtures/ini/kini.py`; fixtures:
`schema/fixtures/ini/play-once/*.ini` with the expected decision in `<case>.json` (`python3 kini.py check`).

Grammar of play-once.ini:
- `key = value` lines; sections are ignored and so are lines without `=`. Keys are case-insensitive; a repeated key:
  the last one counts. Unknown keys are ignored.
- `mode` (required): `vanilla` | `no-mods` | `profile`.
- `created` (required): unix seconds (`1762257600`) or RFC 3339 with a zone (`2026-11-04T12:00:00Z`,
  `…+01:00`).
- `nonce` (required, non-empty): echoed in `state/boot.json` `playOnce.nonce`. The launcher writes 32 hex chars.
- `profile` (optional, mode profile only): the profile file, absolute or relative to `<game>/kiln/`; default
  `profile.once.json`.

What the proxy does, in this order:
1. `KILN_DISABLE=1`: vanilla, no chain, no gate; play-once.ini is left untouched.
2. Read the file, then delete it, before validating. Delete failed: `IGNORED_UNDELETABLE` (a flag that cannot be
   removed must not stick).
3. Dedicated server exe: `IGNORED_SERVER` (the file is still deleted).
4. Invalid (bad mode, missing created/nonce, bad created): `IGNORED_INVALID`.
5. `now - created > 600 s` or `created - now > 60 s`: `IGNORED_EXPIRED`.
6. Apply: `vanilla` loads nothing (no chain, no core); `no-mods` loads no chain entry and boots the core with
   `KILN_PLAY_ONCE_NO_MODS` (Kiln and its own services run; no managed, native, EMF, plugin or chained mod);
   `profile` boots with `KILN_PLAY_ONCE_PROFILE` and `play_once_profile` = the absolute profile path, and reads
   `kiln/chain/once.ini` instead of chain.ini `[profile]`.
An ignored flag boots as `NONE` and is reported (`state/boot.json` `playOnce.mode` = `ignored-…`, proxy.log).

### 5.3 `chain.ini`, `chain/once.ini`, `dinput8-chain.ini`
```ini
[chain]
; proxies the installer moved aside; a leading ; switches one off
chain\emf-dinput8.dll
[profile]
; native mods with phase "dllmain" of the active profile
Z:\home\user\.local\share\kiln\library\native\fps-unlock\2.0.0+00000000\fpsunlock.dll
```
Grammar (same dialect as 5.2):
- Sections `[chain]` and `[profile]`, case-insensitive; lines before any section count as `[chain]` (in `once.ini`:
  as `[profile]`). Other sections are ignored (they may belong to other tools).
- One DLL per line. A `key = value` line counts by its value. Only values ending in `.dll` (case-insensitive) count;
  other lines are ignored.
- Paths: absolute (`Z:\…` under Proton, real paths on Windows, UNC) or relative to `<game>/kiln/`; `/` is read as `\`.
- At most 256 entries per section: the first entry past the limit is reported as REFUSED with detail `too-many`, and
  any further entries are discarded (not reported).
- Writers: the installer owns `[chain]`, profile activation owns `[profile]`; each rewrites the whole file atomically
  and keeps the other section's lines byte-identical. The launcher writes `once.ini` (its `[profile]`) together with
  play-once.ini for launch mode once and deletes it at the next activation.

Load order, all in the proxy's `DllMain` before it installs the gate (so entry patches unwind last-in, first-out):
1. `kiln/chain.ini [chain]`;
2. `<game>/dinput8-chain.ini` with EMF's rules (`[Chain]`, lines before any section, and `[EMF]` (the DLLs EMF 0.5's
   installer renamed), in file order; `;`/`#`/`//` comments, a `key=value` counts by its value, only DLLs inside the
   game folder: an entry outside is REFUSED `outside`; at most 256 entries across both sections, with the same
   overflow rule as above);
3. `kiln/chain.ini [profile]`, or `kiln/chain/once.ini` for play-once mode=profile.

Decisions per entry (`KilnChainEntry`, `state/boot.json` `chain[]`): dedupe by canonical path (`duplicate`, loaded
once); refuse (`refused` + detail): `self` (the proxy itself), `system` (the system dinput8 or Wine's builtin), `kiln`
(a Kiln proxy or kiln-core.dll), `shroudforge`, `not-dll` (not a 64-bit PE DLL), `outside`, `too-many`; `missing`;
`failed` (LoadLibrary error, `error` = Win32 code); `skipped` (play-once no-mods); else `loaded`. The proxy never
calls FreeLibrary on a chained DLL. One line per decision goes to `kiln/logs/proxy.log` (rewritten at each start).

`KilnChainEntry` (kiln_api.h) = `{KilnStr path; u32 status; i32 error; void* module; u32 source; u32 detail}`
(`detail` = `KILN_CHAIN_REFUSED_SELF=1, _SYSTEM=2, _KILN=3, _SHROUDFORGE=4, _NOT_DLL=5, _OUTSIDE=6, _TOO_MANY=7`;
the native loader renames an older `reserved` field, layout unchanged). `source`: `chain=0, profile=1, emf=2`.

Proxy exports: `DirectInput8Create` (forwarded), `GetdfDIJoystick` (forwarded to the system DLL if it exports it,
else the proxy's own `c_dfDIJoystick`), and `const KilnProxyInfoData* __cdecl KilnProxyInfo(void)` returning
`{u32 size; u32 abi /* 2 */; KilnStr version; KilnStr marker; u32 boot_info_size; u32 reserved}`. Older proxies
have no export and count as abi 1.

Proxy classification (`kiln_manage::pe::classify_proxy` -> `ProxyKind`, the installer's table, and the proxy's
refusals use the same markers). Every DLL under a proxy name next to the game (`dinput8`, `dbghelp`, `dbgcore`,
`umpdc`, `cfgmgr32`, `winmm`, `powrprof`, `iphlpapi`, `version`, `xinput1_4`, `xinput9_1_0`, `dxgi`, `steam_api64`;
`engines::scan_proxies`) gets one kind; checks in this order, the first match wins; texts match as ASCII or UTF-16LE,
case ignored. Table and markers: `engines::ENGINES` ([engines.md](../engines.md)).

| Kind | Markers | Installer (`dinput8.dll`) | Proxy |
|---|---|---|---|
| `kiln` | export `KilnProxyInfo`, ASCII `KilnProxy/<abi>`, UTF-16LE `\kiln\kiln-core.dll` | replace | refuse (`kiln`) |
| `emf` | exports `emf_loader_identity` / `emf_loader_play_once`, text `EMF-LOADER-7d3c9f` (checked before ShroudForge: EMF 0.4+ also exports `ShroudforgeSetDinput8Provider`) | chain | load |
| `shroudforge` | `shroudforge/shroudforge-runtime.dll`, `shroudforge/config/modloader-config.json`, export `ShroudforgeSetDinput8Provider`, text `shroudforge` | refuse unless "switch from ShroudForge" (then backed up, never chained; `dbghelp.dll`, `winmm.dll`, `shroudforge/` stay) | refuse (`shroudforge`) |
| `eml` | `EML_LOG_FILE_PATH` | chain | load |
| `set` | `shrouded_engine_tweaks` | chain | load |
| `enshwick` | `enshwick` | chain | load |
| `greeneye` | `greeneye` | chain | load |
| `shroudtopia` | `shroudtopia` (Shroudweave too) | chain | load |
| `flame` | `FlameStartRuntime`, `flame.json` | chain | load |
| `shroudlink` | `shroudlink` | chain | load |
| `ember` | `ember-loader`, `ember_loader` | chain | load |
| `modloaderproxy` | `modloader-proxy.dll`, `chainloader.ini` | chain | load |
| `ultimateasiloader` | export or text `IsUltimateASILoader` | chained as `..\xinput9_1_0.dll` next to the game (refused when that name is taken) | load |
| `aldrich`, `livemap`, `sharedxp`, `reshade` | `aldrich` / `livemap.ini` / `xpshare.ini` / `reshade` | chain | load |
| `chainloader` | `dinput8-chain.ini` | chain | load |
| `unknown` | any other 64-bit PE DLL | chain | load |
| `notdll` / `none` | not a 64-bit PE DLL / no file | refuse / nothing to do | refuse (`not-dll`) |

Kiln owns only `dinput8.dll`; DLLs under the other proxy names are never moved (EML's `dbghelp.dll`, Shroudtopia's
`winmm.dll`, SET's `powrprof.dll` keep running next to Kiln). "chain" = moved to `kiln/chain/<word>-dinput8.dll` +
`[chain]` (an occupied name gets a numbered suffix).

**Library detection** (`library::detect` -> `Detected {kind, path, id, version, name, reason, items, engine}`;
`engine` = the `Engine` it was made for, kebab-case like `ProxyKind`). `DetectKind` besides Kiln's own (`kiln`,
`native`, `plugin`, `emf`, `modpack`, `runtime`, `bare-dll`, `unknown`): `eml`, `shroudforge`, `set`, `enshwick`,
`greeneye`, `shroudtopia`, `flame`, `shroudlink`, `ember` (mods for those engines), `proxy` (another engine's loader or
a tool: never imported), `data`, `save`, `reshade`, `cheat-table`, `asi`. EML vs ShroudForge: both use `mod.json` +
Lua and the same `capabilities`; a package is ShroudForge's only with SF-specific `extended.mod.json` metadata or an
SF schema (`shroudforge::is_sf_own_mod`); `launcher: "EML"` stays EML. A kind is imported only through its registered
loader (`engines::builtin_loaders`: `engine_mods::import_engine_mod` for `eml`, `shroudforge`, `set`, `greeneye`,
`flame`, `shroudtopia`, `enshwick`, `shroudlink`, `ember`), into `library/<kind>/`; the others are skipped with their
`reason`. Enshwick packs take `id`/`version`/`name` from `pack.toml`; an Ember mod is a folder in `ember/mods/` (no
manifest or known export exists yet) and its reason says it doesn't run yet. The launcher (MIT) only
copies files: it never links or runs an engine's code.

### 5.4 `native.json` and `<configDir>/native.json`
```jsonc
{
  "format": 1, "id": "creative-mode", "name": "Creative Mode", "version": "1.3.0",
  "side": "client", "dlls": ["creative.dll", "lib/creative-ui.dll"], "phase": "early",
  "init": "kiln_native_init", "after": ["fps-unlock"], "before": [], "requires": [],
  "incompatible": ["creative-legacy"], "enabled": true, "kiln": ">=0.2.0-dev", "builds": []
}
```
- Required: `id`, `version`, `dlls`. Defaults: `side: "client"`, `phase: "early"`, `init: null`, `enabled: true`,
  `clients` from side (server -> none, else mod).
- `dlls`: relative paths in the folder (subfolders allowed, no `..`), loaded in order with LoadLibrary (each under an
  SEH guard). `init`: an export of `dlls[0]`, called once right after it loads as
  `int __cdecl init(const KilnNativeContext*)` (`native/core/abi/kiln_native.h`); `null` = a plain DLL mod, only
  loaded. Return 0 ok, 1 failed, 2 unsupported. `init` must be null for phase `dllmain`.
- Phases: `dllmain` (proxy, chain.ini `[profile]`, no init), `early` (start of kiln_boot, also in safe idle), `boot`
  (after Kiln's services and plugins), `world` (first world step, needs hooks; skipped in safe idle).
- Ordering: the managed resolver's rules: `requires` = required dependency (any version), `after` = loadAfter,
  `before` = loadBefore, `incompatible`; ties by id; cycles fail their members.
- `kiln` (range): checked like a `kiln` dependency. `builds` (exe sha256s): when given and the exe is not listed, the
  mod still loads with the note "untested build".
- Policy `kiln.json` `"native": {"policy": "always" | "verified-only" | "never"}` (default `always`):
  `verified-only` loads native mods only on a verified game build, `never` loads none (state `policy`).
- Enabled precedence: a profile entry with `enabled: false` wins, then `<configDir>/native.json`
  `mods.<id>.enabled`, then native.json `enabled`.
- `<configDir>/native.json` = `{"format": 1, "mods": {"<id>": {"enabled": false}}}`, written by the launcher.
- Results: `state/native.json` (5.9).

### 5.5 Config schema snapshots `<configDir>/.schema/<file>.json`
Written whenever a mod registers a config (Kiln.Api `Config.Register`; kiln-emf for EMF settings): the config's
schema without values. Path: `.schema/` + the values file's path relative to configDir with its extension replaced by
`.json` (`my-mod.json` -> `.schema/my-mod.json`, `emf/beastmaster.ini` -> `.schema/emf/beastmaster.json`;
`kiln_manage::config::snapshot_path`). Shape = the admin op `config_get` result minus `value`/`fileValue`/`fromServer`:
```json
{ "format": 1, "source": "kiln", "file": "big-stacks.json", "valueFormat": "jsonc", "id": "big-stacks",
  "mod": "big-stacks", "modVersion": "0.3.1", "kind": "server", "written": "…", "kiln": "0.2.0-dev",
  "sections": [{ "path": "limits", "comment": "Stack limits", "synced": true }],
  "entries": [{ "path": "limits.multiplier", "type": "float", "comment": "…", "default": 2.0,
                "requiresRestart": false, "synced": true, "min": 1, "max": 100 }] }
```
`type`: `bool | int | float | string | enum | duration | list<…>` (ConfigValueKind); `options` for enums; durations
are strings (`"90s"`, `"5m"`). The launcher copies snapshots into `schema-cache/` per mod version, edits values
through a JSONC CST (comments kept) and validates like Kiln.Api. Running servers use the admin ops
`config_get`/`config_set` instead.

### 5.6 `kiln-runtime.json` (runtime bundle)
Bundle file: `kiln-runtime-<VERSION>+<yyyymmdd>.<gitsha>.zip`, deterministic (entries sorted bytewise, times = the
commit time, no extra attributes), its files laid out relative to the game folder. `kiln-runtime.json` at the root:
`format`, `version` (= the zip's), `kiln` (VERSION), `git`, `created` (commit time), `abi`, `api`, `dotnet`,
`components` (`core`, `admin`, `emf`, `samples`: `{sides, default, requires, description}`), `files[]`
(`{path, size, sha256, component, config}`, sorted by path), `builds.{server,client}[]` (`{sha256, steamBuild, svn,
verified}`). `config: true` files (`kiln/kiln.json`) are installed only when missing and never replaced or deleted.
A parity test checks the bundle's tree against `tools/deploy-server.sh`'s output.

### 5.7 `.install.json` (receipt) and the install rules
Fields: `format`, `installId` (uuid, stable across upgrades), `installed`, `updated`, `installer`,
`runtime{version, sha256, components, deselected}` (`deselected`: the defaults the player turned off; updates keep
them off unless required and add every other default for the side; automatic requirements preserve
opt-outs, and explicit choices preserve unselected earlier opt-outs even if now optional or absent;
absent in older receipts = nothing turned off), `side`, `platform` (`proton|wine|windows`), `game{exe, sha256, steamBuild}`,
`files[]` (`{path, sha256, size, component, config}`), `replaced[]` (`{path, sha256, size, class, action, chainPath,
backup, engine}`; `class`: `kiln|shroudforge|emf|unknown|file`; `engine`: the 5.3 `ProxyKind` when Kiln knows the
file's engine (`eml`, `set`, `flame` ...), absent otherwise, so older readers keep their five classes; `action`:
`chained|backed-up|replaced|adopted`; every path that existed before the install is recorded: `adopted` = the
bundle's exact bytes (or a config file), kept by uninstall).
Install:
1. Refuse while the game runs (`game_running`) or Steam updates it (`steam_updating`).
2. Stage into `kiln/.staging-<random>/` and verify every sha256.
3. Classify `dinput8.dll` (5.3 table).
4. Back up everything it replaces to `kiln/backups/install-<ts>/` (mirrored to `<data>/backups/install/<installId>/`),
   move displaced proxies to `kiln/chain/<kind>-dinput8.dll` + `[chain]`, put the files in place, `dinput8.dll` last.
5. Write the receipt last.
Upgrade: delete files the new bundle no longer has only when their sha256 still equals the receipt's. Uninstall:
restore every `replaced` file byte-identically (a displaced proxy moves back to `dinput8.dll`), remove the receipt's
unchanged non-config files and `chain.ini [chain]` lines it added, keep `kiln/`. Rollback: install an older bundle
from `<data>/runtime/`.

### 5.8 Modpacks: `kiln-modpack.json`, network.json, handshake properties
Zip layout: `kiln-modpack.json` + `kiln/<id>/…`, `native/<id>/…`, `emf/<id>/…`, `plugin/<file>.dll`; deterministic.
`kiln-modpack.json`: `format`, `name`, `version` (free text), `created`, `server{id,name}`, `kiln` (range players
need), `mods[]` (`{kind, id, version, name, path, hash, clients, side}`; `hash` = the folder's kdh1, a plugin's sha256).
Contents: the server's mods with `clients: "mod"`. Other engines' mods ride along as `<kind>/<id>/…` (`eml/`,
`shroudforge/`, `flame/` …) with their kdh1, taken from each engine's server folder (`engine_mods::server_root`) when
their `clients` is `mod` (stated in their `mod.json`, else the kind's default: `eml`, `shroudforge`, `flame`). A player's
install imports them into the library under their kind and lists them in the profile "Server: <name>".
Install revalidates the current archive against its manifest, validates paths for Windows and verifies every listed
payload hash before importing. Server engine copies require both `.kiln-source.json` and a matching receipt and
payload inventory in `kiln/state/server-library.json` before sync update or removal. Marker-only older copies and
folders with player additions are not auto-adopted. An explicit enable/disable action can move a hand-installed
engine mod between its own folder and `kiln/disabled/<kind>/`; ambiguous folder names across kinds are refused.
These operations refuse while the server runs.

Server (`kiln/config/network.json`, Kiln.Network) adds:
`serverId` (default: generated once into `kiln/data/kiln-network/server-id`), `serverName` (default: the
enshrouded_server.json name), `modpack{url, sha256, name, version}`, `advertise` (default true). REJECT and WELCOME
gain trailing properties: server id/name, Kiln version, modpack, `required[]` (`{id, version, hash, kind}` of the
`clients: "mod"` mods; `kind` is absent for `kiln`, else `native` or an engine kind such as `eml` or `flame`: other
engines' mods are advertised so the launcher can tell the player, while the HELLO check covers Kiln mods only), truncated so the frame stays <= 7 KB (`kiln.required.more` = how many were left out); old
clients still decode them. Property keys (`kiln.server.*`, `kiln.modpack.*`, `kiln.required[.n]`, `.count`, `.more`,
`.pending`): docs/kiln/network.md "Server modpacks".

Client: Kiln.Network writes `state/last-server.json` (5.9) on every REJECT, on the first WELCOME of a connection and
when the server never answers (outcome `vanilla`).

### 5.9 State files (`<game>/kiln/state/`)
All written atomically; a file whose `pid` is not a live process is stale.
- `boot.json` (kiln-core, end of `kiln_boot`, before the game's entry point): `kiln`, `abi`, `pid`, `written`,
  `bootMs`, `side`, `exe{name,sha256,verified}`, `policy`, `hooks`, `safeIdle`, `playOnce{mode, nonce}` (mode `none`,
  `no-mods`, `profile`, `ignored-expired`, `ignored-undeletable`, `ignored-server`, `ignored-invalid`),
  `profile{mode, id, name, generation, file, error, configDir, dataDir, counts}` (mode `legacy|active|once|no-mods|
  invalid`), `paths` (every KILN_PATH_*), `chain[]` (`{path, source: chain|emf|profile, status, detail, error}`),
  `services[]` (`name@version`), `plugins[]` (`{file, id, version, status, error}`), `native{loaded, failed, pending}`,
  `clr{started, error}`, `errors[]`.
- `mods.json` (Kiln.Host, after every resolve/load/unload/reload; `generation` grows): `pid`, `written`, `generation`,
  `side`, `api`, `profile{id, generation, mode}`, `mods[]` (`{id, version, name, folder, source: profile|dropin, state:
  pending|loaded|disabled|failed|skipped|unloaded, reason, loadIndex, side, clients, faults}`).
- `native.json` (native loader, after each phase): `pid`, `written`, `policy`, `phase`, `mods[]` (`{id, version,
  folder, source, phase, state: pending|loaded|failed|disabled|skipped|policy|unsupported, reason, notes[], loadIndex,
  dlls[]{file, status, error, ms}, init{export, result, fault, ms}}`).
- `last-server.json` (Kiln.Network on a client): `written`, `outcome` (`rejected|welcome|vanilla`), `reason`,
  `server{id, name, address, kiln}`, `modpack`, `required[]`, `truncated`, `missing[]`, `history[]` (<= 20 earlier
  entries, newest first, without their own history).

### 5.10 Admin endpoint
`<server>/kiln/data/kiln-admin/endpoint.json`: `{format, protocol: "kap/1", host: "127.0.0.1", port, pid, started,
kiln, admin, server{id,name}, token: "admin-token", bridge}`; written when the listener is up, deleted on unload.
`admin-token`: 64 lower-case hex chars (+ optional newline) = 32 random bytes, created on first start (0600 on Linux).
Port: `kiln/data/kiln-admin/config.json` `"tcp": {"enabled": true, "port": 0}` (0 = the query port number on TCP,
then + 1000 ... + 9000, then any free port). `pid` is the Windows pid (Wine): a stale file is told by a refused
connection. Protocol, ops and the SSH recipe: [admin-protocol.md](admin-protocol.md).

### 5.11 kdh1 (directory hash) and kms1 (mod-set fingerprint)
kdh1 of a folder:
1. Walk it recursively. Skip every entry (file or folder, any depth) whose name starts with `.`, with everything below
   it (`.kiln-source.json`, `.disabled`, `.schema/`, `.git/`).
2. Symlinks are **refused**: the root itself may be a symlink, but any symlink or Windows reparse point below it is an
   error, as is anything that is neither a regular file nor a folder. Empty folders contribute nothing.
3. Each regular file's relative path: components joined with `/`, each component NFC-normalized, UTF-8. A name that is
   not valid Unicode, or two paths equal after NFC, is an error. No case folding.
4. Sort the paths by their UTF-8 bytes.
5. `h = sha256( for each file: path "\0" size "\0" sha256hex(content) "\n" )`, size in decimal without leading zeros,
   sha256hex lower-case.
6. Result `"kdh1:" + lower-case hex(h)`. An empty folder: `kdh1:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
`hash8` = the first 8 hex chars after `kdh1:`. For a zip (modpack), hash each folder's entries with the same rules
(directory entries ignored, symlink entries refused; `kiln_manage::hash::dirhash_records`).
Reference: `schema/fixtures/dirhash/dirhash.py` (`--check`); cases and expected hashes in `schema/fixtures/dirhash/`;
Rust: `kiln_manage::hash::dirhash` (tested against the same fixtures).

kms1 of a profile: `sha256( for each enabled entry of every kind, sorted by (kind, id) bytewise:
kind "\0" id "\0" version "\0" hash "\n" )` as `"kms1:" + hex` (`kiln_manage::hash::fingerprint`). A launch whose
kms1 differs from the game's last launched kms1 backs up the saves first.

### 5.12 Manifest `clients`
`kiln.mod.json` `"clients": "none" | "kiln" | "mod"`: what joining players need. Default from `side`: `server` ->
`none`, `client`/`both` -> `mod`. C#: `Manifest.Clients` (`ManifestClients`), schema `kiln.mod.schema.json`, docs
`docs/kiln/modding/manifest.md`. native.json has the same key and default rule.

### 5.13 ABI additions
- `KILN_PATH_CONFIG = 6` (mod configs: `<kiln>/config`, the profile's `configDir` once native-loader wires it) and
  `KILN_PATH_STATE = 7` (`<kiln>/state`, created at boot). An older core answers `KILN_ERR_INVALID_ARGUMENT`.
- `KilnBootInfo` v2 = v1 `{size, reserved, proxy_module}` + `{u32 play_once; u32 chain_count; const KilnChainEntry*
  chain; KilnStr play_once_nonce; KilnStr play_once_profile}` (64 bytes). The core copies fields as far as `size`
  covers them (`KILN_BOOT_INFO_HAS`); a v1 proxy (16 bytes) reads as play-once NONE, empty chain
  (`kiln::boot_info()`). `KILN_PLAY_ONCE_*`: NONE 0, VANILLA 1, NO_MODS 2, PROFILE 3, IGNORED_EXPIRED 16,
  IGNORED_UNDELETABLE 17, IGNORED_SERVER 18, IGNORED_INVALID 19. `KILN_CHAIN_*`: LOADED 0, FAILED 1, MISSING 2,
  REFUSED 3, DUPLICATE 4, SKIPPED 5.
- `kiln_native.h`: `KilnNativeContext {size, abi_version, phase, side, api, owner, module, id, version, directory,
  config_dir (<configDir>/native/<id>), data_dir (<dataDir>/<id>), hooks_enabled, play_once}`,
  `typedef int (KILN_CALL* KilnNativeInitFn)(const KilnNativeContext*)`, phases, `KILN_NATIVE_INIT_{OK,FAILED,UNSUPPORTED}`.
- `services/kiln_profile.h`: `kiln.profile` v1 `{header, mode, id, name, generation, count(kind), get(kind, i,
  KilnProfileMod*), dropins (bitmask 1<<kind), config_dir, data_dir, json}`; modes LEGACY 0, ACTIVE 1, ONCE 2,
  NO_MODS 3, INVALID 4; kinds KILN 0, NATIVE 1, PLUGIN 2, EMF 3. Implemented by native-loader.
- `kiln_version.h` (generated from VERSION): `KILN_VERSION`, `_MAJOR`, `_MINOR`, `_PATCH`, `_PRERELEASE`.
- Managed: `KilnPaths.Config` (internal setter), `KilnPaths.State`; `KilnAbi.PathConfig/PathState`.

### 5.14 ABI additions (clients without Kiln)
All additive, appended to v1 tables (check `header.size`). Docs: `docs/kiln/network.md`
"Clients without Kiln".
- `kiln.assets`: `resource_is_new(key)` (1 once a key was served as a resource the game doesn't have, for the rest of the
  process; lock-free) and `resource_new_count(type)`.
- `kiln.net`: `gate_set(mode)`, `gate_info(KilnNetGateInfo*)`, `gate_release(peer)`, `entity_withheld(entity)`,
  `template_kiln_only(guid)`, `withhold_set(peer, on)`, `lab_selftest(op, a, b, out)` (lab transport only);
  `KilnNetPeerInfo.flags` bits `PROVED` 0x04, `HELD` 0x08, `WITHHELD` 0x10; `KILN_NET_GATE_CAN_GUARD`;
  `KILN_NET_KNOCK_INIT`, the four-byte proof frame `'K','N',1,10`.
- Kiln.Network wire: frame type 10 `Knock` (client to server, no body, swallowed by kiln.net). Old servers ignore it, a
  vanilla server logs one warning per frame.
- `kiln/config/network.json`: `vanillaHoldSeconds` (default 5, 1..20), `definitionsWaitSeconds` (default 5, 1..30). `modSync` left out now differs from
  `"warn"`: a Kiln client without a mod stating `clients: "mod"` is refused (REJECT with the usual properties, so `last-server.json` says `rejected` and the modpack offer fires).
- sigdb features `net.gate`, `net.withhold`, `net.client_guard` (server and client exes; on a server the guard exists only for lab
  tests), `net.client_ready` (client exe). The build records' symbol lists don't have the new symbols yet: the core resolves them from the patterns at boot and reads only
  `verified` from a record. Regenerate them at the next `build_record.py` run.

## 6. Path translation
`kiln_manage::winpath`, `kiln_manage::game::PathMode`:
- **Z mode** (Proton): `/home/u/x` <-> `Z:\home\u\x`, used only when `<pfx>/dosdevices/z:` resolves to `/` and the
  launcher data dir is a normal host path. Every `/` becomes `\`. A component Windows cannot name (`<>:"|?*\`,
  control characters, a trailing dot or space) does not translate: copy mode for the whole profile.
- **Native mode** (Windows): real paths as they are (no `\\?\` prefix).
- **Copy mode**: Flatpak or Snap Steam (the sandbox cannot see `~/.local/share/kiln`), a missing or redirected `Z:`,
  or an untranslatable path. Activation copies each enabled mod folder to `<game>/kiln/profiles/<pid>/<kind>/<id>/`
  (verified by kdh1 after the copy; unchanged copies are kept), the profile's config dir to
  `<game>/kiln/profiles/<pid>/config/` (copied back to `<data>/profiles/<pid>/config/` before the next activation and
  after the game exits), and writes relative paths (`profiles/<pid>/kiln/<id>`), resolved against `<game>/kiln/`.
- Reverse (reading state files): `Z:\a\b` -> `/a/b`; `C:\…` -> `<pfx>/drive_c/…`; other letters through
  `<pfx>/dosdevices/<l>:`; relative -> `<game>/kiln/…`.

## 7. Flows
### Activation (`profile_activate`)
1. Refuse while the game runs. Resolve the profile (preview errors are shown, not fatal: the game reports the same).
2. Path mode (6). Copy mode: sync folders.
3. Write `<configDir>/native.json` from the profile's native switches; write `chain.ini [profile]` (dllmain natives).
4. Write `kiln/profile.json` (generation + 1, fingerprint, writer, written) atomically; delete a stale
   `kiln/chain/once.ini` and `kiln/profile.once.json`.
5. Record the activation in `<data>/installs/<installId>.json`; emit `kiln://game` `{kind: "status"}`.
Deactivate: move `profile.json` to `kiln/backups/profile-<ts>.json`, clear `[profile]`: the legacy layout.

### Add a mod (`library_add`: a drop anywhere on the launcher window)
1. Refuse at once (no task) when the game has no `kiln/` folder (`unsupported`: install Kiln first).
2. Where the mods go is decided before anything changes: the profile the game's `profile.json` names (when this data
   folder has it); else, with no profile at all, "My mods" (made below); else nowhere (profiles exist but none is active
   in this game, or another data folder's profile is: those players are left alone, the files are only imported).
   "My mods" while the game runs is refused (`game_running`) before the import.
3. Modpack zips (`kiln-modpack.json` at the root) are not imported: the result lists them (`modpacks`) for the consent
   flow (`modpack_inspect`/`modpack_install` with `{path}`). A modpack folder is skipped ("drop its zip").
4. `library::import` the rest; every entry imported or already there is added to the profile, enabled: new, or
   replacing another version of the same id, or switching a disabled entry on (`changes`, with what each replaced).
5. "My mods": `profile::create`, `dropins` every kind (what the legacy layout loaded still loads), activated
   (activation flow). A failed activation removes it.
6. Save the profile, then `profile::refresh_active` writes `profile.json` again (not while the game runs: Play does it
   before the next start). When that write fails, the profile change is undone (a "My mods" made for it is removed)
   and the task fails; the library keeps what it imported.
7. `missing`: what the added Kiln mods (non-optional `dependencies`, not `kiln`) and native mods (`requires`) need and
   the profile lacks (enabled, any version).
Undo is `profile_revert(profileId, changes)`: each changed entry goes back (removed when it was new), newest first; an
entry changed since is left alone; then `profile.json` is written again where the profile is active.
`profile_ensure_default(gameId)` is step 2 and 5 alone (Play runs it after it installed Kiln).

### Launch (`launch_play`)
1. Refuse while the game runs (`game_running`).
2. Mode `profile`: nothing to write (Steam's Play button does the same). Modes `vanilla`/`no-mods`: write
   play-once.ini. Mode `once`: render the profile into `kiln/profile.once.json` (+ `kiln/chain/once.ini`), then
   play-once.ini `mode=profile`.
3. Save backup when the kms1 of what will load differs from the game's last launched kms1 (unless `skipBackup`);
   vanilla and no-mods count as the empty set.
4. Open `steam://rungameid/1203620` (xdg-open / ShellExecute). Never write Steam's config.
5. Watch: the process appears -> `kiln://game {kind: "started"}`; `state/boot.json` with a live pid and the nonce ->
   `{kind: "boot"}`; `mods.json`/`native.json` updates -> `{kind: "mods"|"native"}`; exit -> `{kind: "stopped"}`.
   play-once.ini still there after 120 s: the proxy did not run; under Proton show the `WINEDLLOVERRIDES="dinput8=n,b"
   %command%` advice (read-only check of the launch options). While the game runs: play-once.ini still there 30 s after
   the process started (`GameStatus.started`; the proxy deletes it in its DllMain), or a non-vanilla start of this
   launcher without a boot.json of this run 60 s in, means Kiln did not load: the state is "No Kiln" (warn), never
   "Starting", and the advice (or, without one, a "send proxy.log" banner) shows (`shell/gameState.ts` `playOnceMissed`,
   `shell/play.ts` `kilnMissing`).

### Install / upgrade / uninstall / rollback
Section 5.7. Long-running: `runtime_install` etc. return a task id; progress through `kiln://task`.

### Modpack join (client)
1. `state/last-server.json` changes (outcome `rejected` with `missing[]`, or `welcome` with a different modpack) ->
   `kiln://last-server`.
2. The consent modal: server name/id, missing mods, URL, size, "runs code on your PC". https only unless
   the settings allow http. Nothing downloads before the user says yes.
3. `modpack_install` (task): download -> zip sha256 (from last-server.json) -> `kiln-modpack.json` -> each mod's kdh1 ->
   library import -> profile `Server: <name>` (bound server) -> save backup (reason `modpack`) -> activate -> the UI
   offers Launch.

### Server mods
Sync library mods into `<server>/kiln/mods/<id>/` (+ `.kiln-source.json`), natives into `kiln/native/<id>/`;
switch with `.disabled`; never touch hand-placed folders without a marker. Running servers reload through the admin ops
`mod_load`/`mod_reload`/`mod_unload`.

## 8. Parity fixtures (C# <-> Rust)
`schema/fixtures/{resolver,semver,manifest}/*.json`. The expected values come from the real C# code
(`managed/tests/Kiln.Host.Tests/FixtureTests.cs` checks them; `KILN_FIXTURES_WRITE=1 dotnet test … --filter
FixtureTests` regenerates them: review the diff, it is a contract change). the Rust ports run the same
files.
- resolver: `{description, env: {kiln, api, side, unavailableFeatures[], loaded[<raw manifest>]}, mods: [{dir,
  manifest: <object> | raw: <text>}], expect: {order: [ids], rejected: [{id, dir, state, reason}]}}`. A `raw` that does
  not parse becomes a candidate whose error is the manifest error text. `rejected` is compared as a set (stored sorted
  by dir, then id); `reason` must match exactly.
- manifest: `{raw, syntax?, expect: {ok: true, manifest: {id, name, version, parsedVersion, apiVersion,
  entryAssembly, entryType, side, clients, authors, description, license, url, dependencies[{id, range, optional}],
  loadBefore, loadAfter, incompatible[{id, range}], features, exports, advanced, tests, warnings}} | {ok: false,
  error}}`. With `syntax: true` (invalid JSON) only the prefix `kiln.mod.json: not valid JSON` must match; other
  errors match exactly.
- semver: `parse.json` (`cases[{text, expect{ok, major, minor, patch, pre, build, text}}]`), `ranges.json`
  (`cases[{range, version, satisfies}]`, `invalid[{range, ok:false}]`), `order.json` (`ordered[]`, `equal[[a,b]]`),
  `api.json` (`cases[{api, current, satisfies}]`).
- INI and kdh1 fixtures: sections 5.2, 5.3, 5.11 (Python references).
- KAP: `schema/fixtures/kap/handshake.json`.

## 9. Tauri commands
One file per area in `launcher/src-tauri/src/commands/`; the TypeScript wrappers in `launcher/src/api/commands.ts`
have the same names in camelCase (`profile_get` -> `profileGet`). Arguments are passed by name in camelCase (Tauri maps
them to the Rust snake_case parameters). Every command rejects with `KilnError` (section 11). `TaskId` commands start
a background task and return its id at once; the task's `result` (in `kiln://task`) has the type given after `->`.

Shared types (TypeScript names, defined in `commands.ts`; Rust in kiln-manage unless noted):
`GameId`, `ProfileId`, `ServerId`, `TaskId`, `ModKind = 'kiln'|'native'|'plugin'|'emf'|'eml'|'shroudforge'|'set'|'greeneye'|'flame'|'shroudtopia'|'enshwick'|'shroudlink'|'ember'` (section 5.1), `RunSide = 'server'|'client'`,
`LogTarget = {game: GameId} | {server: ServerId}`, `ConfigTarget = {profile: ProfileId} | {server: ServerId} |
{game: GameId}`.

| File | Command | Arguments | Returns |
|---|---|---|---|
| app | `app_info` | - | `AppInfo` {version, kiln, platform, arch, dataDir, commit, mock, noWebgl, deck: `DeckInfo`, distribution: "direct" \| "nexus"} |
| app | `app_settings_get` | - | `LauncherSettings` |
| app | `app_settings_set` | settings: LauncherSettings | `LauncherSettings` |
| app | `app_open_path` | path: string | null |
| app | `app_open_url` | url: string (https) | null |
| app | `task_list` | - | `Task[]` |
| app | `task_get` | taskId | `Task` |
| app | `task_cancel` | taskId | null |
| app | `app_assets_status` | - | `AssetsStatus {root, editor: {present, bytes, source?, build?, gameId?, gameBuild?, current?, canExtract?, task?, counts?, extracted?, modded?: {items, icons, current, version, generated, mods}}, admin: {map, catalog, bytes, source?, build?, gameBuild?, current?, canExtract?, markers?, task?, counts?, extracted?}}` (gameart additions: `source` "game" \| "copy", `build`/`gameBuild` the data builds of the art and of the installed client, `current` false = the game was updated since: offer to re-extract; [gameart.md](gameart.md)) |
| app | `app_assets_import` | kind: "editor" \| "admin", from: string \| null | `TaskId` (result `AssetsStatus`; `from` null: extracts the art from the installed client, read-only (`kiln_manage::gameart`; `editor`: task "Prepare game art", the art when missing or outdated, then the overlay of mod items `<data>/editor-assets/modded/` when the mods changed; `admin`: relief, tiles, markers (map/game/, every game language), marker icons, fonts, plus the local markers (fallback), finder data and catalogs copied from `$KILN_ADMIN_EXTRAS` when set). The launcher runs it on its own (`<GameArtAuto/>`), there is no button; `from` a folder: copies from that checkout into `<data>/{editor,admin}-assets` (dev fallback); served by the `kiln-asset` protocol; never downloads; while one runs, a second call for the same kind returns the running task) |
| game | `game_list` | - | `GameInstall[]` |
| game | `game_detect` | - | `GameInstall[]` (scans Steam, adds new ones) |
| game | `game_add` | path: string | `GameInstall` |
| game | `game_remove` | gameId | null |
| game | `game_status` | gameId | `GameStatus` (+ `started`: RFC 3339 start of the running game's process, null when stopped) |
| runtime | `runtime_list` | - | `RuntimeBundle[]` |
| runtime | `runtime_import` | path: string | `RuntimeBundle` |
| runtime | `runtime_status` | gameId | `InstallState` |
| runtime | `runtime_install` | gameId, version: string, options: InstallOptions | `TaskId` -> `InstallReceipt` + `changedBackedUp: string[]` (Kiln files changed by hand that the install replaced, kept in the backup) + `backup: string \| null` (`<data>/backups/install/<installId>/install-<ts>`) |
| runtime | `runtime_uninstall` | gameId | `TaskId` -> null |
| runtime | `runtime_rollback` | gameId, version: string | `TaskId` -> `InstallReceipt` + `changedBackedUp`, `backup` (as `runtime_install`) |
| runtime | `runtime_classify` | path: string | `ProxyClass` |
| profile | `profile_list` | - | `ProfileSummary[]` |
| profile | `profile_get` | profileId | `Profile` |
| profile | `profile_create` | name: string, copyFrom: ProfileId \| null | `Profile` |
| profile | `profile_save` | profile: Profile | `Profile` (conflict on a stale generation) |
| profile | `profile_delete` | profileId | null |
| profile | `profile_resolve` | profileId, side: RunSide | `ResolvePreview` |
| profile | `profile_activate` | profileId, gameId | `Activation` |
| profile | `profile_deactivate` | gameId | null |
| profile | `profile_active` | gameId | `Activation \| null` |
| library | `library_list` | kind: ModKind \| null | `LibraryEntry[]` |
| library | `library_detect` | path: string | `Detected` |
| library | `library_import` | paths: string[] | `TaskId` -> `ImportReport` |
| library | `library_remove` | entry: LibraryRef, force: boolean | null (`game_running` while a game runs, or a server whose `profile.json` / `profile.once.json` points into the library, even with `force`) |
| library | `library_unused` | | `LibraryRef[]` (in no profile and no known game's `profile.json` / `profile.once.json`) |
| library | `library_lint` | entry: LibraryRef \| null, profileId: ProfileId \| null | `LintReport` (`schema/lint-report.schema.json`) |
| config | `config_list` | target: ConfigTarget | `ConfigFileInfo[]` |
| config | `config_read` | target, file: string | `ConfigDocument` |
| config | `config_set` | target, file, path: string, value: unknown | `ConfigDocument` |
| config | `config_write` | target, file, text: string | `ConfigDocument` |
| config | `config_reset` | target, file, path: string \| null | `ConfigDocument` |
| config | `config_kiln_read` | gameId | `ConfigDocument` (kiln/kiln.json with the built-in schema snapshot `launcher/src/data/kiln-json.snapshot.json`) |
| config | `config_kiln_set` | gameId, path, value | `ConfigDocument` |
| config | `config_kiln_reset` | gameId, path | `ConfigDocument` |
| config | `config_native_state` | target | `NativeUserState` |
| config | `config_native_set` | target, modId: string, enabled: boolean \| null | `NativeUserState` |
| launch | `launch_play` | gameId, request: LaunchRequest {mode: 'profile'\|'vanilla'\|'no-mods'\|'once', profileId?, skipBackup?} | `LaunchResult` |
| launch | `launch_state` | gameId | `BootState` {boot, mods, native, stale} (launcher type) |
| logs | `logs_list` | target: LogTarget | `LogFile[]` |
| logs | `logs_read` | target, file: string, offset: number \| null, maxBytes: number \| null | `LogChunk` |
| logs | `logs_follow` | target, file: string | string (subscription; lines in `kiln://log`) |
| logs | `logs_unfollow` | subscription: string | null |
| logs | `logs_crash_reports` | target | `CrashReport[]` |
| saves | `saves_list` | gameId | `SaveSlot[]` |
| saves | `saves_backups` | gameId | `SaveBackup[]` |
| saves | `saves_backup` | gameId, note: string \| null | `SaveBackup` |
| saves | `saves_restore` | gameId, backupId: string | null (game_running while it runs) |
| saves | `saves_delete` | gameId, backupId: string | null |
| editor | `editor_list_saves` | - | `EditorSaveFile[]` |
| editor | `editor_file_info` | path: string | `EditorFileInfo` |
| editor | `editor_read_file` | path: string | ArrayBuffer |
| editor | `editor_zstd_decompress` | data: number[] | ArrayBuffer |
| editor | `editor_zstd_compress` | data: number[] | ArrayBuffer |
| editor | `editor_game_running` | - | boolean |
| editor | `editor_backup_file` | path: string | string |
| editor | `editor_write_save` | path: string, data: number[] | null |
| editor | `editor_list_backups` | target: string | `EditorBackup[]` |
| editor | `editor_restore_backup` | src: string, target: string | null |
| editor | `editor_open_backups` | - | null |
| server | `server_list` | - | `ServerInstance[]` |
| server | `server_get` | serverId | `ServerInstance` |
| server | `server_create` | spec: ServerSpec | `TaskId` -> `ServerInstance` |
| server | `server_import` | dir: string, name: string | `ServerInstance` |
| server | `server_remove` | serverId, deleteFiles: boolean | null |
| server | `server_status` | serverId | `ServerStatus` (+ `installed`: the Kiln runtime in the server folder from its receipt, else a hand-placed Kiln proxy's version or `"unknown"`; null without Kiln and for remote servers) |
| server | `server_start` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_stop` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_restart` | serverId | `TaskId` -> `ServerStatus` |
| server | `server_update` | serverId | `TaskId` -> string \| null (new build id) |
| server | `server_deploy` | serverId, version: string | `TaskId` -> `InstallReceipt` + `changedBackedUp`, `backup` (as `runtime_install`) |
| server | `server_settings_get` | serverId | `ServerSettings` |
| server | `server_settings_set` | serverId, values: unknown | `ServerSettings` |
| server | `server_mods` | serverId | `ServerMod[]` |
| server | `server_mods_sync` | serverId, mods: LibraryRef[] | `TaskId` -> `ServerMod[]` |
| server | `server_mod_enable` | serverId, folder: string, enabled: boolean | null |
| server | `server_backups` | serverId | `ServerBackup[]` |
| server | `server_backup_create` | serverId | `TaskId` -> `ServerBackup` |
| server | `server_backup_restore` | serverId, name: string | `TaskId` -> null |
| server | `server_query` | serverId | `ServerQuery` |
| server | `server_modpack_status` | serverId | `ModpackPublishState` |
| server | `server_modpack_build` | serverId, name: string, version: string | `TaskId` -> `BuiltModpack` |
| server | `server_modpack_publish` | serverId, url: string | `ModpackPublishState` |
| server | `server_console_open` | serverId, maxLines: number \| null (default 5000, at most 50000) | `ConsoleOpen` {subscription, serverId, source: 'capture'\|'gamelog'\|'none', path: string \| null, lines: `ConsoleLine` {text, level: 'info'\|'warn'\|'error'\|'debug'\|'meta'}[], truncated} (the last run of the server's output; new lines in `kiln://console`; remote: unsupported; [servers.md](servers.md) "Console") |
| server | `server_console_close` | subscription: string | null |
| admin | `admin_connect` | serverId, transport: 'auto'\|'tcp'\|'file'\|'ssh' | `AdminSession` |
| admin | `admin_disconnect` | session: string | null |
| admin | `admin_sessions` | - | `AdminSession[]` |
| admin | `admin_call` | session: string, op: string, args: object | unknown (the op's `data`; op_failed with its error) |
| admin | `admin_subscribe` | session: string, topic: string | null (pushes in `kiln://admin`) |
| admin | `admin_unsubscribe` | session: string, topic: string | null |
| modpack | `modpack_pending` | gameId | `LastServer \| null` |
| modpack | `modpack_inspect` | gameId, source: ModpackSource \| null (null: from last-server.json) | `ModpackPreview` |
| modpack | `modpack_install` | gameId, request: ModpackInstallRequest {source, consent: true, allowHttp?, activate?} | `TaskId` -> `ModpackInstallResult` |
| modpack | `modpack_dismiss` | gameId, serverId | null |
| update | `update_status` | - | `UpdateStatus` |
| update | `update_check` | - | `UpdateStatus` (asks the sources now) |
| update | `update_install` | - | `TaskId` -> `UpdateStatus` (download, verify; the AppImage is put in place; `unsupported` for a .deb, .rpm or development build) |
| update | `update_restart` | - | null (restarts into the update that is ready; Windows: runs its installer; returns only on failure) |
| update | `update_skip` | version: string \| null | `UpdateStatus` (null: un-skips) |
| update | `update_ack` | - | `UpdateStatus` (clears `justUpdated`) |

The Nexus Mods build (`AppInfo.distribution` "nexus", built without the `self-update` feature; [releasing.md](releasing.md)
"The two builds") has the same six commands with no self-update behind them: `update_status`, `update_check`,
`update_skip` and `update_ack` return the same `UpdateStatus` with `phase: "disabled"`, `distribution: "nexus"`,
`auto: false`, `install.inPlace: false` and `page` = the Kiln page on Nexus Mods, and change nothing; `update_install`
and `update_restart` reject with `unsupported` (detail: that page). Nothing in that build reads the network for updates
or emits `kiln://update`; its UI calls none of these commands.
| library | `library_add` | gameId, paths: string[] | `TaskId` -> `AddReport` (import + add to the game's active profile + write profile.json, section 7 "Add a mod"; rejects at once with `unsupported` when the game has no `kiln/`) |
| profile | `profile_revert` | profileId, changes: ProfileChange[] | `Profile` (undoes a `library_add`; profile.json follows where the profile is active, as after `profile_save`) |
| profile | `profile_ensure_default` | gameId | `Activation \| null` (the active profile's; with no profile at all: "My mods" made and activated; null when profiles exist but none is active in the game) |
| deck | `deck_browse` | dir: string \| null | `DeckFolder` (one folder for the Deck's Add mods, null: Downloads (XDG download dir, else `~/Downloads`, else home): `{dir, name, parent, folders: {name, path}[] (not mods; hidden ones left out), mods: DeckFoundMod {path, file, folder, size, modified, detected: Detected}[] (what `library_detect` recognises: kiln, native, plugin, emf, bare-dll, modpack, or a zip/folder holding mods; newest first; zips listing no manifest or DLL, zips over 512 MiB and DLLs over 96 MiB are not opened), other (files left out), truncated (only the newest 400 entries looked at), hasGame (enshrouded.exe here), places: {name, path, kind: 'downloads'\|'home'\|'desktop'\|'card'\|'games'}[]}`; read-only; [deck.md](deck.md)) |
| deck | `deck_gamepad` | on: boolean | `GamepadStatus` {native (Linux: the evdev reader exists), running, devices: {device, name, vendor, product}[], denied (gamepads in /dev/input that could not be opened)} (starts or stops the reader; events in `kiln://gamepad`) |
| deck | `deck_fullscreen` | on: boolean | boolean (the main window's fullscreen state after the change) |
| deck | `deck_quit` | - | null (exits the launcher: Game Mode has no window buttons) |
| nexus | `nexus_account` | - | `NexusAccount` (backend-held OAuth session metadata; account validation cached for 60 s; never token material) |
| nexus | `nexus_sign_in` | - | `TaskId` (opens browser authorization with PKCE; result `NexusAccount`; cancellation/timeout supported; unavailable until Kiln has its own approved public registration) |
| nexus | `nexus_sign_out` | - | `NexusAccount` (invalidates pending sign-in, clears memory and stored OAuth credentials) |
| nexus | `nexus_search` | query: CatalogQuery | `CatalogPage` (the Kiln filter looks at up to 12 mods per call: `unchecked` > 0, ask again) |
| nexus | `nexus_mod` | modId: string (game-scoped) | `CatalogDetails` |
| nexus | `nexus_installed` | check: boolean | `InstalledCatalogMod[]` (`check`: look up the newest version of each installed file, cached 30 min) |
| nexus | `nexus_download` | gameId, modId, fileId (game-scoped version id) | `TaskId` -> `InstallReport` (premium accounts only; others reject with `refused`, `detail.url` = the file's page with its Mod Manager Download; Kiln's own page (301) is refused) |
| nexus | `nexus_links` | - | `PendingLink[]` (Enshrouded nxm links that arrived: `{id, modId, fileId, expired, received}`) |
| nexus | `nexus_link_install` | gameId, linkId | `TaskId` -> `InstallReport` (the link's key and expiry; an expired link rejects with `refused`; game/Kiln/key prerequisites are checked before claiming the link) |
| nexus | `nexus_link_dismiss` | linkId | null |
| nexus | `nexus_handler_status` | - | `HandlerStatus {supported, registered, active (the system's handler is Kiln now), previous: string \| null (its name)}` |
| nexus | `nexus_handler_set` | on: boolean | `HandlerStatus` (on: remember the current nxm handler, register the launcher; off: give it back; only on the user's click) |
| engines | `engine_settings_get` | kind: ModKind, id, profileId, gameId: GameId \| null | `EngineSettings {kind, id, format: "extended.mod.json" \| "shroudtopia.json" \| "none", file, schema (JSON Schema; ShroudForge's with x-kiln-* hints), values, note?}` (valid mod id listed in the profile required; ShroudForge reads the selected game's placed copy only when this profile is active, otherwise library defaults; Shroudtopia reads the profile's effective config, using the game copy for active copy mode; other engine kinds `format: "none"` with a note) |
| engines | `engine_settings_set` | kind, id, profileId, gameId \| null, key, value: Json | `EngineSettings` (one declared top-level key with schema validation; profile membership required; `game_running` while a game reading these files runs; ShroudForge requires the profile active in the selected game and a Kiln-owned placed copy; `unsupported` for kinds without settings) |
| engines | `engine_status` | gameId | `EngineStatus[] {kind, plugin, state: Json \| null, file, error?}` (the engine plugins' `kiln/state/<engine>.json`, null when not written; invalid, unreadable or symlinked state files report `error`) |
| engines | `eml_backup` | gameId | `EmlBackup[] {file, backup, patched}` (EML's `<exe>.kfc.bak` next to each `.kfc`; `patched`: they differ; read/hash errors and symlinks are refused) |
| engines | `eml_restore` | gameId | `string[]` (the `.kfc` files copied back from their backup; refused with `game_running` while the game runs) |

`AddReport {import: ImportReport, profileId, profileName, created (My mods made now), added: AddedMod[], already:
AddedMod[], changes: ProfileChange[], applied (profile.json written now), running (the game runs: applies at the next
start), activation, missing: {id, range, by}[], modpacks: string[]}`, `AddedMod {kind, id, version, hash, name}`,
`ProfileChange {kind, before: ProfileMod | null, after: ProfileMod}` (`kiln_manage::quick`).

Launcher-only types (Rust in `launcher/src-tauri/src/commands/app.rs` and `launch.rs`): `AppInfo` (`noWebgl`:
KILN_LAUNCHER_NO_WEBGL=1 for this start; `distribution`: "direct" for the frostdev.io/GitHub build that updates itself,
"nexus" for the Nexus Mods build, which has no self-update and whose UI shows only a link to the Kiln page there
(`src-tauri/src/distribution.rs`; absent from an older backend: direct)), `LauncherSettings {steamRoots: string[], defaultGameId: string|null,
allowHttpModpacks: boolean, savesKeep: number, updateCheck: boolean (default true), updateChannel?: "stable"|"dev"
(absent: the running build's channel), animatedFire: boolean (default true), addToActiveProfile: boolean (default true;
a dropped mod goes through `library_add`; off: `library_import` only), uiMode?: "auto"|"deck"|"desktop" (the UI to start
in; absent or auto: the Deck UI when `AppInfo.deck.detected`; [deck.md](deck.md))}` (`checkUpdates`, never used, is gone;
readers ignore it), `UpdateStatus` (`launcher/src-tauri/src/selfupdate/mod.rs`; [releasing.md](releasing.md); `distribution`
"direct", or in the Nexus build "nexus" with `phase: "disabled"`, `commands/update.rs`),
`Task {id, kind (the command), title, state: 'queued'|'running'|'done'|'failed'|'canceled', progress: number|null,
message: string|null, started, finished: string|null, result?, error?: KilnError}`, `BootState {boot: BootJson|null,
mods: ModsJson|null, native: NativeJson|null, stale: boolean, log: {errors: number, first: string[]}|null}` (`log`:
kiln.log's `[E` lines of the run that wrote boot.json).

`--selftest`: the binary runs headless, exercises the Rust side (data dir, crate calls with temp
folders) and exits 0/1; used under Wine for the NSIS build. Its report has `distribution` ("direct" or "nexus"; also
the line `distribution: <d>` on stdout), and its `self_update` check names it.

`DeckInfo` (`launcher/src-tauri/src/deck/detect.rs`): `{detected: boolean, signals: string[] ("SteamDeck=1", "SteamOS=1",
"XDG_CURRENT_DESKTOP=gamescope", "GAMESCOPE_WAYLAND_DISPLAY", "dmi:Jupiter" | "dmi:Galileo", "os-release:steamos"), gamescope:
boolean, device: "jupiter"|"galileo"|null, forced: "deck"|"desktop"|null}` (`forced`: `--deck` / `--desktop` on the command
line, the last one wins, else `KILN_LAUNCHER_UI=deck|desktop`). The UI: `forced`, else `uiMode` when not auto, else
`detected`, else the desktop UI ([deck.md](deck.md)).

Official launcher builds (`KILN_RELEASE_BUILD` set) require valid public Nexus OAuth registration when Nexus is enabled.
`KILN_NEXUS_ENABLED=0` disables the entire integration; absent/`1` enables it, other values fail the build.
`app_info.nexusEnabled` is the backend capability for both UIs. Disabled `nexus_*` commands return `unsupported`
before storage/provider/handler access; direct nxm callbacks exit without side effects.
The headless `--selftest` report adds `releaseBuild` and `nexusOAuth {enabled, configured, clientId, redirectUri}` from the
actual compiled configuration. Unconfigured development builds pass selftest with `configured: false`; release
report checks reject enabled unconfigured variants and accept explicit disabled official variants. This metadata
proves configuration, not Nexus approval or a working provider session.

Catalog types (`kiln_manage::nexus`, `launcher/src/api/nexus.ts`; [catalog.md](catalog.md)): `CatalogQuery {text, filter:
'kiln'|'all'|'installed' (default kiln), sort: 'endorsements'|'updated'|'new'|'downloads'|'name', page (1-based),
pageSize (24, at most 100), adult, refresh}`, `CatalogPage {mods: CatalogMod[], total, page, pageSize, unchecked, needsSignIn,
fetched}`, `CatalogMod {id (game-scoped), uid, name, summary, version, author, category, thumbnail, endorsements,
downloads, created, updated, adult, kind: 'kiln'|'native'|'emf'|'dll'|'modpack'|'other'|null (null: not looked at),
url, installed: InstalledInfo | null}`, `InstalledInfo {version, versionGsid, fileName, inLibrary, latestVersion,
latestGsid, updateAvailable}`, `CatalogDetails = CatalogMod + {description (plain text), credit, tags, uniqueDownloads,
images: {url, thumb, title}[], files: CatalogFile[], bestFile, requirements: Requirement[], filesUrl, needsSignIn}`,
`CatalogFile {fileId, fileName, versionId, versionGsid, version, category, uploaded, primary, kind, archive}`,
`Requirement {modId, name, fileName, minVersion, installed, kiln (Kiln itself), otherGame, url}`, `InstallReport {add:
AddReport (Undo: profile_revert with its profileId and changes), modId, name, version, fileName, check: {files,
matched, hashed, noManifest, sha256, size}, requirements, download}`, `InstalledCatalogMod = InstallRecord {modId, name,
fileId, fileName, versionId, versionGsid, version, refs: LibraryRef[], installed, source: 'nxm'|'catalog'} + {info:
InstalledInfo}`, `NexusAccount {configured, signedIn, tokenPlace: 'keyring'|'file'|null, name, premium, error: KilnError|null, rate:
{hourlyRemaining, dailyRemaining, blockedFor, calls}}`.

Command line: `kiln-launcher --nxm <link>` (what Kiln registers) or a bare `nxm://` argument: an Enshrouded link is
queued in `<data>/nexus/inbox/` and handed to the launcher that runs (this process exits), or this process starts with
it; another game's well-formed link goes to the handler before Kiln and the process exits. URL invocations accept
exactly `--nxm <link>` or one bare link, without additional launcher options; malformed invocations exit 1.

## 10. Events
Emitted with `app.emit(name, payload)`; `launcher/src/api/commands.ts` exports `KilnEvents` with the payload types.

| Event | Payload |
|---|---|
| `kiln://task` | `Task` (the whole task at every change) |
| `kiln://game` | `{gameId, kind: 'started'\|'stopped'\|'boot'\|'mods'\|'native'\|'status', pid: number\|null, status: GameStatus\|null}` |
| `kiln://log` | `{subscription, target: LogTarget, file, lines: string[], next: number}` |
| `kiln://last-server` | `{gameId, lastServer: LastServer}` |
| `kiln://server` | `{serverId, status: ServerStatus}` |
| `kiln://admin` | `{session, serverId, event: {kind: 'push', topic, seq, data} \| {kind: 'closed', reason, retry} \| {kind: 'error', message} \| {kind: 'ready', transport, ops, topics}}` (`ready` after every (re)connect; `closed.retry`: the hub reconnects) |
| `kiln://update` | `UpdateStatus` (at every change of the self-update: check, download progress, ready, skip; never in the Nexus Mods build) |
| `kiln://console` | `{subscription, serverId, reset: boolean, source, path: string \| null, truncated, lines: ConsoleLine[]}` (`reset`: `lines` replace everything shown: a start, a stop, a start outside the launcher) |
| `kiln://gamepad` | `{kind: 'button', button: 'a'\|'b'\|'x'\|'y'\|'lb'\|'rb'\|'lt'\|'rt'\|'select'\|'start'\|'guide'\|'ls'\|'rs'\|'up'\|'down'\|'left'\|'right', pressed, repeat, device} \| {kind: 'axis', axis: 'rx'\|'ry', value: -1..1, device} \| {kind: 'connected', device, name, vendor, product} \| {kind: 'disconnected', device, name}` (the Deck UI's native reader while `deck_gamepad(true)`: directions merge the D-pad and the left stick and repeat while held (`repeat: true`); the right stick is sent for scrolling, at most ~25 a second, then one 0; [deck.md](deck.md)) |
| `kiln://nxm` | `{count}` (an Enshrouded Mod Manager Download link arrived; `count` links wait in `nexus_links`; the window comes to the front) |

## 11. Errors
Every command rejects with `KilnError = {code, message, detail?}` (`kiln_manage::Error`, serialized as is). `message`
is one sentence for the user; `detail` is anything that helps (paths, OS error text, a list).

| Code | When |
|---|---|
| `not_implemented` | a stub |
| `invalid_argument` | bad input (id format, empty path) |
| `not_found` | no such game, profile, mod, file, server, session |
| `already_exists` | create over an existing thing |
| `io` | file system error |
| `parse` | a file could not be parsed |
| `schema` | parsed but broke the format's rules |
| `conflict` | stale generation, concurrent change, library entry still in use |
| `game_running` | the game runs (install, activation, restore, launch) |
| `steam_updating` | Steam updates or validates the game |
| `permission` | the OS refused access |
| `unsupported` | not possible here (platform, setup, untranslatable path) |
| `network` | download / TCP / SSH failure |
| `hash_mismatch` | a sha256 or kdh1 did not match |
| `consent_required` | remote code without the user's yes |
| `refused` | refused on purpose (ShroudForge proxy, http URL, a symlink in a mod folder) |
| `timeout` | waited too long |
| `canceled` | the user canceled the task |
| `busy` | too many tasks/requests in flight |
| `auth_failed` | KAP/1 handshake refused |
| `admin_unavailable` | no admin transport reaches the server |
| `op_failed` | an admin op answered ok=false (message = its error) |
| `internal` | a bug |

## 12. Crate `kiln-manage` modules
Features: default = none (serde, serde_json, sha2, unicode-normalization); `zip`, `http` (ureq, rustls), `admin`
(hmac, getrandom), `jsonc` (jsonc-parser CST), `saves` (zstd), `server` (toml), `gameart` (zstd, bcdec_rs, image-webp,
png, flate2), `full` = all. kilnctl uses the
default; the launcher uses `full`. Builds for Linux and `x86_64-pc-windows-msvc` (`cargo xwin check -p kiln-manage
--target x86_64-pc-windows-msvc [--all-features]`).

| Module | Public items (signatures in the source) |
|---|---|
| `error` | `Error {code, message, detail}`, `ErrorCode`, `Result<T>`, `Error::{new, not_implemented, with_detail, invalid, not_found}` |
| `task` | `trait Progress { report(Option<f32>, &str); canceled() }`, `NoProgress` |
| `paths` | `DataDirs::{at, locate, library, profiles, installs, runtime, schema_cache, backups, logs, settings, library_entry, profile}`, `paths::game::*` constants |
| `hash` | `dirhash(&Path) -> Result<String>`, `dirhash_records(impl IntoIterator<Item = Record>)`, `sha256_file`, `sha256_hex`, `hex`, `hash8`, `fingerprint(entries) -> String`, `Record`, `FingerprintEntry` |
| `vdf` | `parse(&str) -> Result<Vdf>`, `Vdf::{get, path, as_str, entries}` |
| `steam` | `roots()`, `libraries(&SteamRoot)`, `app_manifest(&Path, u32)`, `compat_tool`, `launch_options`, `compatdata`; `SteamRoot`, `SteamFlavor`, `Library`, `AppManifest`, `LaunchOptions` |
| `game` | `detect()`, `inspect(&Path)`, `known(&DataDirs)`, `add`, `remove`, `status`, `path_mode`; `GameInstall`, `GameKind`, `Platform`, `PathMode`, `GameStatus`, `LaunchOptionsInfo` |
| `pe` | `exports(&Path)`, `is_pe64_dll(&[u8])`, `classify_proxy(&Path) -> Result<ProxyClass>`; `ProxyKind {None, Kiln, ShroudForge, Emf, Unknown, NotDll}`, `ProxyClass`, marker constants |
| `proc` | `list()`, `find_game(&GameInstall)`, `alive(u32)`; `ProcessInfo` |
| `launch` | `steam_uri(u32)`, `open_uri(&str)`, `play(&DataDirs, &GameInstall, &LaunchRequest) -> Result<LaunchResult>`; `LaunchMode`, `LaunchRequest`, `LaunchResult` |
| `winpath` | `unix_to_z(&Path)`, `z_to_unix(&str)`, `z_drive_ok(&Path)`, `to_game(&Path, &GameInstall)`, `to_host(&str, &GameInstall)` |
| `bundle` | `read(&Path)`, `verify(&Path, &mut dyn Progress)`, `create(&Path, &Path, &CreateOptions)`, `list(&DataDirs)`, `import(&DataDirs, &Path)`; `RuntimeManifest`, `RuntimeComponent`, `RuntimeFile`, `RuntimeBuilds`, `RuntimeBuild`, `RuntimeBundle`, `CreateOptions` |
| `install` | `read_receipt(&Path)`, `status`, `install(&DataDirs, &GameInstall, &Path, &InstallOptions, &mut dyn Progress)`, `uninstall`, `rollback`, `plan`; `InstallReceipt`, `ReceiptRuntime`, `ReceiptGame`, `ReceiptFile`, `ReplacedFile`, `ReplacedClass`, `ReplacedAction`, `InstallOptions`, `InstallState` |
| `chain` | `parse(&[u8]) -> ChainIni`, `parse_once(&[u8]) -> OnceIni`, `read(&Path)`, `write_chain_section`, `write_profile_section`, `write_once`; `ChainEntry`, `EntryStatus` |
| `manifest` | `parse_kiln(&str) -> Result<KilnManifest>`, `read_kiln`, `parse_native(&str) -> Result<NativeManifest>`, `read_native`; `Side`, `RunSide`, `Clients::default_for`, `ModKind`, `KilnManifest`, `Dependency`, `Incompatibility`, `TestsSpec`, `NativeManifest`, `NativePhase`, `IncompatibleSpec`, `OneOrMany` |
| `semver` | `SemVersion::{parse, compare, release}`, `VersionRange::{parse, at_least_same_major, satisfies, is_any}` |
| `resolver` | `resolve(&[Candidate], &Environment) -> Result<Resolution>`; `Candidate`, `Environment`, `ModState`, `Rejection`, `Resolution` |
| `library` | `list(&DataDirs, Option<ModKind>)`, `detect(&Path)`, `import(&DataDirs, &[PathBuf], &mut dyn Progress)`, `remove(&DataDirs, &LibraryRef, bool)` (GameRunning while a game runs, or a server whose profile file points into the library, whatever the bool), `unused(&DataDirs)`, `users`, `path_of`; `LibraryRef`, `LibraryEntry`, `LibraryVersion`, `ImportSource`, `DetectKind`, `Detected`, `ImportReport`, `Skipped` |
| `profile` | `list`, `get`, `create(&DataDirs, &str, Option<&str>)`, `save`, `delete`, `resolve_preview`, `activate(&DataDirs, &str, &GameInstall) -> Result<Activation>`, `deactivate`, `active`, `render`, `read_game_profile`, `fingerprint(&Profile)`; `ProfileFile`, `ProfileFileMods`, `FolderMod`, `PluginMod`, `Dropins`, `BoundServer`, `ModpackRef`, `Profile`, `ProfileMods`, `ProfileMod`, `DataDirMode`, `ProfileSummary`, `ProfileCounts`, `Activation`, `ResolvePreview`, `NativePlanEntry` |
| `quick` | `add(&DataDirs, &GameInstall, &[PathBuf], &mut dyn Progress) -> Result<AddReport>`, `add_with` (running given), `ensure_default`, `ensure_default_with`, `revert(&DataDirs, &str, &[ProfileChange]) -> Result<Profile>`, `is_modpack`, `DEFAULT_PROFILE`; `AddReport`, `AddedMod`, `MissingMod`, `ProfileChange`, `Target` |
| `playonce` | `render(&PlayOnce) -> String`, `decide(&[u8], i64, bool, bool)`, `write(&Path, &PlayOnce)`, `pending(&Path)`, `new_nonce()`; `PlayOnce`, `PlayOnceMode`, `PlayOnceStatus` |
| `config` | `snapshot_path`, `list`, `read`, `set`, `write`, `reset`, `validate`, `read_native_state`, `write_native_state`; `ConfigTarget`, `SchemaSnapshot`, `SnapshotSection`, `SnapshotEntry`, `SnapshotSource`, `ValueFormat`, `NativeUserState`, `NativeModSwitch`, `ConfigFileInfo`, `ConfigDocument`, `ValidationError` |
| `jsonc` | `split_path(&str) -> Vec<PathSeg>`, `parse`, `set_value`, `remove_value` |
| `saves` | `list`, `backups`, `backup`, `restore`, `delete`, `KEEP`; `SaveSlot`, `SlotKind`, `SaveBackup`, `BackupReason`; `saves::editor::{list_saves, file_info, read_file, zstd_decompress, zstd_compress, game_running, backup_file, write_save, list_backups, restore_backup, backups_dir}` |
| `logs` | `list(&Path)`, `read(&Path, &str, Option<u64>, Option<u64>)`, `crash_reports`, `Follower::{open, poll}`; `LogFile`, `LogKind`, `LogChunk`, `CrashReport` |
| `control` | `send(&Path, &str, Duration) -> Result<Value>` |
| `state` | `read_boot`, `read_mods`, `read_native`, `read_last_server` (implemented); `BootJson` (+ `BootProfile`, `ProfileMode`, `BootChainEntry`, `ChainSource`, `ChainStatus`, `RefusalDetail`, ...), `ModsJson`, `ManagedMod`, `NativeJson`, `NativeModStatus`, `LastServer`, `RequiredMod`, `JoinOutcome`, `ServerRef` |
| `server` (`instance settings steamcmd run control query backup remote game procs console proton`) | `instance::{list, get, create, import, remove, record}`, `settings::{read, write, validate, schema}`, `steamcmd::{install, update}`, `run::{status, start, stop, restart, pid, headless, ctrl_c}`, `console::{capture_path, open_capture, pick, snapshot, ConsoleFollower::{open, poll, source}, ConsoleLine, ConsoleLevel, ConsoleSource, ConsoleSnapshot, ConsoleUpdate}`, `proton::{plan, SteamLaunch, parse_launch_options}`, `control::{mods, sync, set_enabled}`, `query::query`, `backup::{list, create, restore}`, `remote::{run, read_file}`; `ServerInstance` (+ `runner`, `prefix`, `backupDir`, `kilnctl`, `stopTimeoutS`), `ServerRunner {Kilnctl, Steam, Windows, Remote}`, `ServerSpec` (+ `wine`, `skipSteamcmd`), `ServerStatus`, `ServerState`, `ServerMod` (+ `kind`), `SourceMarker`, `NetworkModpackConfig`, `RemoteSpec`, `ServerQuery`, `ServerBackup`, `ServerSettings` (+ `schema`, `running`); kilnctl's moved pieces (instance files, game, procs, the foreground runner) |
| `admin` (`protocol client bridge tunnel discover hub`) | `protocol::{encode, decode, decode_known, write_frame, read_payload, parse_token, mac_message, mac, op_budget, error_code, Frame, KapErrorCode, Endpoint, Limits, ServerIdent}` + constants (`MAX_SERVER_FRAME` = 16 MiB), `client::{AdminClient::{connect, connect_dir, connect_ssh, connect_tcp, connect_file, session, call, subscribe, unsubscribe, next_event, is_open, shutdown, close, send_frame}, Transport, AdminSession, AdminEvent, TcpTarget}`, `bridge::FileBridge::{new, locate, alive, call, call_raw, state}`, `tunnel::SshTunnel::{open, close, alive}`, `discover::{endpoint, token, probe, remote_endpoint, remote_token}`, `hub::{AdminHub::{new, connect, call, subscribe, unsubscribe, disconnect, sessions, session_of, shutdown}, HubEvent, Connector}` |
| `gameart` (feature `gameart`) | `extract_editor`, `extract_map`, `status`, `game_build`, `build_of`, `read_source`, `write_source`, `recipe_of`, `GameData::open`; `SourceInfo`, `GameSource`, `ArtStatus`, `GameBuild`, `EditorOptions`, `MapOptions`, `LayoutSource`; `kfc::Kfc`, `refl::{PeFile, TypeDb}`, `res`, `texture`, `image`, `work` ([gameart.md](gameart.md)) |
| `modpack` | `inspect`, `download`, `verify`, `extract`, `check_against`, `install`, `build`, `publish`, `publish_state`, `client_mods`, `required`, `server_identity`; `ModpackManifest`, `ModpackMod`, `ModpackServer`, `ModpackSource`, `ModpackPreview`, `ModpackInstallRequest`, `ModpackInstallResult`, `BuiltModpack`, `ModpackPublishState`, `ClientMod` |
| `lint` | `lint_paths`, `lint_text`, `lint_profile`, `lint_library_entry`, `rules`, `schema_for`, `docs::{idrefs, schema_markdown}`; `LintOptions`, `LintReport`, `Diagnostic`, `Severity`, `Span`, `Fix`, `TextEdit`, `Related` |
| `nexus` (`api catalog install nxm handler keystore oauth version`; network: feature `http`) | `client(token, app_version)`; `api::{Client::{new, with_credentials, has_credentials, search, mod_details, mod_files, file_versions, file_version_by_gsid, dependencies, download_links, validate, manifest, open_download, rate}, Credentials, Transport, UreqTransport, AppId, Account, ManifestEntry, RateState, parse_manifest}`, `catalog::{Catalog::{new, search, details, verify, files, requirements, installed, check_updates, record_install, cached_verdict}, classify, newest, bbcode_to_text, files_url, mod_url, root_of, refuse_kiln, GAME, KILN_MOD, KILN_EPOCH, CHECK_BATCH, Query, Filter, Sort, Page, CatalogMod, Details, CatalogFile, Requirement, InstallRecord, InstalledMod, InstalledInfo, ModType, Verdict}`, `install::{install, download, check_zip, InstallRequest, InstallReport, CheckReport}`, `nxm::{parse, game_of, NxmLink}`, `handler::{register, unregister, status, state, forward, platform, acquire_instance, queue_link, take_links, exec_argv, exec_quote, fill_command, split_command, LinuxMime, HandlerPlatform, HandlerState, HandlerStatus, Previous}`, `keystore::{TokenStore::{new, with_backend, get, set, clear, file_path}, TokenPlace, SecretBackend, retire_legacy, write_private}`, `oauth::{Config::{registered, new}, Login::{bind, authorize_url, wait}, Session::{new, save, clear, place}, Tokens}`, `version::{compare, is_newer}` ([catalog.md](catalog.md)) |

kilnctl is built on the crate; it also has `bundle`, `client`, `admin` and `deploy --bundle`.

## 13. Frontend contracts (`launcher/`)
- React 18 + TypeScript + Vite, bun. Router: `HashRouter`. Data: TanStack Query; state: zustand.
- `src/api/commands.ts`: one typed function per command (section 9) + the shared types + `KilnEvents`. Names and
  argument shapes are the contract; pages import from here, never call `invoke` directly.
- `src/api/kinds.ts`: the 13 `ModKind`s for the UI: `MOD_KINDS`, `CORE_KINDS`, `IMPORTED_KINDS` (every engine kind,
  = `engine_mods::IMPORTED`), `ENGINE_KINDS`, `NOT_RUNNING_KINDS` / `runsYet` / `NOT_RUNNING_NOTE` (=
  `engine_mods::runs_yet`: Ember), `KIND_NAME` (= `ModKind::label`), `KIND_SHORT` (badges), `KIND_TONE`,
  `KIND_PLUGIN`, `emptyMods()`, `totalCount(counts)`.
- `src/api/mock.ts`: used automatically when `window.__TAURI_INTERNALS__` is absent (plain browser, Playwright mock
  mode): every command answers from in-memory fixtures (built on `schema/examples/`), unknown ones reject with
  `not_implemented`.
- `src/api/admin.ts`: KAP/admin types, `<AdminProvider serverId>` and the hooks
  `useAdminState([serverId])`, `useHasOp([serverId,] op)`, `useAdminCaller([serverId])` (op runner),
  `useAdminCall(op, options?)` (TanStack mutation), `useAdminQuery(op, args?, options?)` over one AdminHub session per
  server; without a server id they use the nearest provider. The admin routes are mounted by `<AdminRoutes/>` at
  `servers/:id/admin/*`. Pages: [admin-pages.md](admin-pages.md).
- `src/theme/tokens.css`: generated from `branding/palette.json`: `--k-<name>` for every colour (with `--k-well` and
  `--k-line-hot`), `--k-radius-{s,m,l}`, `--k-font-{display,ui,label,mono}` (Grenze Gotisch, Alegreya Sans, Alegreya
  Sans SC, Inconsolata; bundled locally, no web-font URLs), `--k-text-on-ember: #170803` (text on ember or gold fills
  is never white), `--k-ember-text-on-light`.
- Routes: Deck UI `/deck`, `/deck/{mods,add,catalog,kiln,help,settings}` (its own frame outside the shell, [deck.md](deck.md)); Play `/`; Client `/profiles/:id/{mods,native,emf,engines,configs,saves}`, `/library`, `/catalog`, `/runtime`,
  `/logs`, `/saves`, `/editor`, `/settings`, `/about`; Servers `/servers/:id/{overview,logs,ticks,settings,configs,mods,
  backups,modpack}`; Admin `/servers/:id/admin/{players,map,zones,items,spawn,world,combat,creatures,gamedata,permissions,
  content,saves,crashes}` (`admin/configs`, `admin/mods` and `admin/console` redirect to the merged server pages).
