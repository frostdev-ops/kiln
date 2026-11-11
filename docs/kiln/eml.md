# EML mods on Kiln (kiln-eml)

EML, the **Enshrouded Mod Loader** by Brabb3l ([kfc-parser](https://github.com/Brabb3l/kfc-parser), GPL-3.0, v0.2.0),
is the engine with the most Enshrouded data mods. Kiln runs EML mods unchanged through **kiln-eml**, a separate
GPL-3.0-or-later plugin built from EML's own source (`kiln/plugins/kiln-eml.dll`, source in
[gpl/kiln-eml](../../gpl/kiln-eml/README.md)). Kiln itself stays MIT. This is the same split EMF uses for its GPL
`emf_content.dll`.

- **Players:** drop EML mods into `<game>/mods/` as with EML, or add them to a Kiln profile. You don't need EML's
  `dbghelp.dll` and, on Linux, you don't need `WINEDLLOVERRIDES`: Kiln's own proxy starts kiln-eml.
- **Mod authors:** nothing changes. Same `mod.json`, same `src/mod.lua`, same Lua API, same game types.
- **Kiln mod authors:** the MIT ports (`Kiln.Eml` assembly) are listed under [MIT ports](#4-mit-ports-kilneml).

## 1. How EML works (mapped from its source, v0.2.0)

### Loading
- EML ships as a `dbghelp.dll` proxy (fallback `dinput8.dll`). The game imports dbghelp, so the proxy's `DllMain`
  runs at process start, before the game's entry point. It does all its work **inside `DllMain`**, under the loader
  lock:
  1. It forwards all 242 `dbghelp` exports to the system DLL (`GetSystemDirectory`).
  2. It reads `eml.json` from the working folder (and creates it if missing).
  3. It loads `<game>/mods/*` (folders and `.zip`), runs every mod, writes the patch, and returns.
- **Any error panics.** A bad mod, a parse error or a write failure shows a message box ("Fatal error") and the
  game stops (`panic = "abort"`).
- On Linux it needs `WINEDLLOVERRIDES="dbghelp=native,builtin"`, otherwise Wine's builtin dbghelp loads and EML never
  runs.
- The runtime step (`loader.runtime.register_dll`, capability `runtime`) is a stub in 0.2.0: it is declared and logged,
  and nothing is loaded.

### KFC patching
The game data is `<exe>.kfc` (header and tables), `<exe>.kfc_resources` (resident resources) and
`<exe>_NNN.dat` (content containers).
1. **Type registry.** EML extracts the game's reflection types from the exe, and caches them as `.cache/types.json`,
   keyed by the KFC version tag. A new game build gets re-extracted automatically: no EML release is needed while
   the reflection layout holds.
2. **Change cache.** `.cache/files.json` holds the mtimes of the game's `.kfc/.dat/.exe` files and of every mod file.
   When nothing changed, the patch is skipped.
3. **Backup.** On the first run it copies `<exe>.kfc` to `<exe>.kfc.bak`, and refreshes the copy when the game
   updates (the version tag changes). Every run reads the vanilla data from the backup, so patches never stack.
4. **Lua run.** Each mod gets its own Lua 5.4 environment. A mod runs when it declares `patch` (and patching is on) or
   `export` (and export is on). Resources are typed tables. Changes are tracked, and only changed or created
   resources are serialised.
5. **Write.**
   - An incremental KFC writer appends the new resources after the game's own in `<exe>.kfc_resources`.
   - New content goes into new `.dat` containers, padded to a power of two.
   - The header in `<exe>.kfc` is rewritten.
   - The game's own `.dat` files are never touched.

### Mod format
```
mods/<folder or .zip>/
  mod.json        { "id", "name", "version" (semver), "capabilities": ["patch"|"export"|"runtime"|"runtime-register-dll"],
                    "dependencies": [{ "id", "version" (semver req), "optional" }], "authors", "description", "license", "icon" }
  src/mod.lua     the main module (require "mods.<id>" = src/mod.lua)
  src/**.lua      more modules: require("a.b") = src/a/b.lua; another mod's: require("mods.<id>.a.b")
  ...             data files the mod reads with io.read (inside its own folder only)
```
- Mod ids must be unique.
- Folders whose name starts with `.` are skipped.
- `dependencies` is parsed but not enforced. Dependencies resolve through `require`.
- Mods run in hash-map order: two mods that write the same field have no defined winner.

### Settings and config
- `eml.json` in the game folder:
  - `enable_console` (bool): opens a console;
  - `use_export_flag` (bool): turns `export` on;
  - `export_directory` (string, default `export`).
- Environment variables:
  - `EML_CONSOLE`;
  - `EML_LOG_FILE_ENABLED`, `EML_LOG_FILE_PATH` (default `./logs`), `EML_LOG_FILE_MAX`, `EML_LOG_FILE_FILTER`;
  - `EML_LOG_STDOUT_ENABLED`, `EML_LOG_STDOUT_FILTER`.
- There are no per-mod settings. Mods that want options read their own files.

### ShroudForge and EMF
- **ShroudForge** vendors kfc-parser, so its `dbghelp.dll` carries EML: it runs EML packages plus its own runtime
  services. Its `extended.mod.json` adds settings. A "legacy EML package" is one ShroudForge stamped with
  `launcher: "EML"`. ShroudForge's runtime in Kiln is the `shroudforge` component (GPL-3, `third_party/shroudforge`,
  agent `sf`). See section 6.
- **EMF 0.5** builds EML content mods through a separate GPL-3.0 `emf_content.dll` that runs at start. Agent `emf26`.
  See section 6.

## 2. kiln-eml: how Kiln runs EML

### When
kiln-eml is an **early plugin**. kiln-core's early step ([early_plugins.cpp](../../native/core/src/early_plugins.cpp))
runs right after the native mods of phase `early`:
- on the game's main thread, at the entry gate, before any game code;
- before sigdb and hooks, and in safe idle too (EML needs no hooks and works on unverified builds);
- outside the loader lock, unlike EML.

The early step:
1. Loads every `kiln/plugins/kiln-*.dll` whose bounded PE export table defines `kiln_plugin_early` (inspected through a
   read-only file mapping; imports, arbitrary strings and forwarded exports do not qualify).
2. Initializes kiln-eml first so its profile roots and collection service are available, then calls the peer
   `kiln_plugin_early` exports in filename order.
3. Calls each `on_ready`.

kiln-eml publishes `kiln.eml` in its `kiln_plugin_early` and runs the mods in `on_ready`, so other early plugins can
hand it mods first (section 6). It does not run with kiln.json `"plugins": false`, play-once no-mods, or an invalid
profile. **Limit:** in those starts, and in a play-once vanilla start (no Kiln code at all), nothing puts the game data
back, so the archives keep the last EML patch: such a start runs vanilla code on EML-patched data. To get truly vanilla
data, switch the EML mods off in the active profile and start once normally through Kiln (that start restores what
kiln-eml wrote), or use Steam's "Verify integrity of game files" ([client.md](client.md#boot-and-side)). Later in the same boot the plugin step loads the DLL again: `kiln_plugin_main` returns OK and does nothing.
With an older kiln-core that has no early step, the run happens there instead, still before the game's entry point.

### Which mods
The candidates, in this order (the first mod with an id wins; later ones are `duplicate`):
1. **The Kiln profile's `mods.eml` list**: `[{ "id", "version", "path", "enabled" }]`, the same shape as `mods.emf`.
   kiln-core logs `profile: unknown mod kind "mods.eml" ignored` until detect or the coordinator add the kind to the
   schema; kiln-eml reads the list from the profile text (`kiln.profile` `json()`).
2. Folders other engine plugins add through `kiln.eml` `add_mod` (source = the plugin's name).
3. Drop-ins: `<game>/kiln/eml/mods/*`, then EML's own `<game>/mods/*`. These load without a profile (legacy), or when
   the profile has `"dropins": true` or lists `"eml"`. This is the same rule as every other Kiln kind. A drop-in
   folder holding the launcher's marker `.kiln-library.json` (kind `eml`) with the id of a profile entry is that
   entry's copy, placed for EML's own loader: it is skipped (logged at debug level in `eml.log`), so the mod runs once.

Each candidate is classified. Only EML mods run; everything else is logged and listed with a reason:

| Folder | State | Reason |
|---|---|---|
| `mod.json` (valid EML manifest) + `src/mod.lua`, or such a `.zip` | run | |
| no `mod.json` | skipped | not an EML mod (logged quietly) |
| `flame.json` | skipped | a Flame mod |
| `mod.json` with `dll`/`main`/`emf`/`emfMin`/`emf_api` | skipped | an EMF mod |
| `extended.mod.json` whose `launcher` is not `"EML"` | skipped | a ShroudForge package (ShroudForge's runtime runs it) |
| an unknown capability, a bad semver | skipped | not an EML manifest |
| `enabled: false` in the profile | disabled | |

### The run
This is EML's own runner (`mod_loader::lua::run`), on the listed mods:
- **Patch on.** Export is on only when `<game>/eml.json` says `use_export_flag` (kiln-eml reads that file and never
  creates it).
- **Its own cache** in `<game>/kiln/eml/cache/` (`<exe>-types.json`, files.json, Lua definitions for editors). The DLL
  targets its actual host executable even when client and server data share a folder; target identity is part of the
  patch cache key and reflection caches are separate. It fingerprints
  file contents (the mods' files) or what the OS says of them (the game's own files), size and full-resolution
  mtimes, including resources, backups and `eml.json`, plus runner code and options. Switching a mod off re-patches.
  Each file is read at most once per run, and not at all while the OS shows it unchanged, nor, where the OS can vouch
  for it, the first time ([Reading the game data](#reading-the-game-data)); export passes always run. Unreadable inputs
  and inputs changed during a run cannot produce a reusable cache entry.
- **The header backup is EML's own** `<exe>.kfc.bak`. New backups also save `<exe>.kfc_resources.bak` for exact
  resource-file restoration. `<exe>.kfc.bak.integrity` records both files' lengths and CRC64 checksums and is
  published durably before patching. Malformed or checksum-mismatched backups are refused. An interrupted seal
  update can be repaired only when both backups still match the untouched live files. Older unsealed resource
  backups must match the live vanilla prefix before adoption; legacy header-only backups cannot restore the
  resource file byte for byte. After a game update ShroudForge replaces the pair itself when its asset mods run: Kiln's
  bundled SF, from fork commit `2eb8f0b` on (the pin `112d939` includes it; the earlier pin `724a884` kept the old
  pair and failed every preparation after an update, as it needs its Updater for this step), moves the old pair and
  the seal to `shroudforge/backups/outdated-working-baseline/`; an installed SF (its Updater present) replaces both
  through its Updater and recycle bin and leaves kiln-eml's seal behind. Either way kiln-eml takes up the new pair by the unsealed rule (the live build, the live
  resource prefix) and seals it; a sealed pair counts as replaced, not damaged, only when the files' lengths differ
  from what the seal recorded, and a same-length mismatch stays refused. The old receipt then lists other backups, so
  nothing is taken for EML's output and the old build's bytes never go back. ShroudForge writes its backup copies to
  `<exe>.kfc.bak.partial` and `<exe>.kfc_resources.bak.partial` first (fork 112d939), and one can stay after a killed
  start: kiln-eml looks up backups only by their exact names, so it never reads, seals, compares or restores from a
  `.partial` file, and its tidy-up never removes one (it is SF's).
- **Publication:** the writer builds privately in the game filesystem, flushes every output, publishes the vanilla
  header from its validated private snapshot before replacing mod-owned containers/resources, and publishes the
  patched header last. Vanilla containers
  and the resource prefix stay immutable. Interrupted publication leaves either vanilla or the complete patch.
  Windows uses `FlushFileBuffers` and `MoveFileExW(MOVEFILE_WRITE_THROUGH)`. Wine additionally flushes a writable
  directory handle after each rename, since its move implementation ignores that flag; live reader handles are
  closed before publication so Wine can replace the resource file. Another process's open handle is a limit on native
  Windows: `MoveFileExW` without POSIX rename semantics does not replace a file another process holds open, even with
  delete sharing (documented behaviour, not tested on Windows here), and the launcher
  keeps `<exe>.kfc_resources` and the `.dat` files it reads open while it extracts game art (it starts that on its own
  after a mod change), so such a start can fail with `Access is denied (os error 5)`: that start runs vanilla (the
  vanilla header is in place first) and the next start retries. Wine replaces such files regardless.
  Detected changes to immutable game inputs during Lua execution refuse publication; changes during publication prevent
  cache reuse. The per-install OS lock is held until process exit; on Windows an already-running game from the same
  install is also refused. Failure to acquire the install lock stops this start before the game entry point, since
  continuing as a reader could race the lock holder's publication. Unmanaged loaders must still be kept from starting
  during patch preparation; a process scan cannot prevent a later unmanaged Steam start.
- **Paths:** profile entries resolve against `<game>/kiln` (including relative paths); each explicitly listed
  folder/zip authorizes its resulting mod root. `add_mod` still requires an absolute path under a game mod root or
  an explicit profile root. Check the root itself and everything below it for links/junctions, traversal and special
  files, and compare final resolved paths for containment. Ancestors above the root are the player's layout and
  are not inspected: Steam library symlinks and Wine drive links such as `Z:\` are allowed there. Under Wine,
  `OPEN_REPARSE_POINT` does not prevent following Unix links; a by-name attributes query catches directory links,
  but Unix file symlinks are invisible to Win32 and must be excluded by the installer. This follows the
  [engine path policy](engines/README.md#when-the-other-loader-is-installed-too).
  Zip classification uses the same SF/EMF/Flame exclusions as folders, with entry and uncompressed-size limits.

It fails soft:
- **A mod whose entry module cannot be read is switched off before the run.** EML needs `src/mod.lua` only after its
  long game-data phase, but reads `mod.json` when the mods load. kiln-eml opens each mod's `src/mod.lua` first, through
  the same file system EML reads with, and marks a mod that fails `failed` with the reason (see
  [When a mod cannot be read](#when-a-mod-cannot-be-read)). The other mods run. EML runs a mod only with the `patch`
  capability (or `export`, when export is on), so only those are checked; a library mod that others `require` is not.
- **A mod whose Lua fails is switched off, and the run repeats without it.** EML names each chunk `mods.<id>.<module>`,
  so the error names the mod. The other mods still patch. A module that cannot be read at that point (a `require` of a
  file that vanished or is unreadable) names its mod too: `failed to load module: mods.<id>.<module> (<file>): ...`;
  that mod is switched off, even when another mod required the module. A module its mod simply does not have (the mod
  folder and `src` are there, the file is not: a typo in a `require`, or a version of the mod without that module)
  says `no such module: mods.<id>.<module> (<file>): ...`; the mod whose chunk required it is switched off, not the
  intact one named.
- **A failure that names no mod** (a write error, a panic): restoration requires exact proof that the live output
  still belongs to kiln-eml and that its working backups are unchanged. Unowned or changed data is retained; the cache
  is dropped so the next start re-runs. Restore failures remain failures. An install-lock failure stops startup.
- **No EML mods (left):** kiln-eml restores only its own unchanged published output, using the matching baseline.
  A shared `.kfc.bak` or a change cache that merely exists never authorizes restoration. Other loaders' patches are kept.
  The restore copies back the header and the resource file, then removes the container files the output added (EML
  appends `<exe>_NNN.dat` after the game's own and pads their count to a power of two with empty files: on a client
  `_055` with the content and `_056` to `_063` empty): only those the receipt lists, each only while it is exactly the
  listed file, and never one the vanilla header references. A player's own file is never touched. A publication with
  fewer containers than the output it replaces removes the old ones the new header no longer references the same way.
  Before publication, any existing destination DAT also needs matching per-file receipt proof (including adopted
  legacy output). Otherwise the run refuses before changing live archives and names the container it retained; inspect
  it and move it aside manually if appropriate. Enabling a mod cannot establish ownership by overwriting unproven data.
- **Output of dev.20 to dev.23 (before receipts):** once, when there is no receipt, kiln-eml checks the change cache that
  release left (`kiln/eml/cache/files.json`, written only after a completed publication or a start that changed
  nothing). It is proof only when it is the record of a patch run of this stem, names the live `<exe>.kfc` and
  `<exe>.kfc_resources` by length and CRC-64 of their bytes (a file named only by its change stamp proves nothing),
  names the backups present exactly (by content, or by a change stamp on a volume that passes the scratch-file test),
  and the backup pair passes its seal. Then a normal receipt is written and the restore goes on as above. This reads
  the header, the resource file and the backups once (about 0.45 GiB on a client) and says so in the log.
- **A run killed half way** leaves its private stage folder (`.kiln-eml-stage-<uuid>` in the game folder: a full copy
  of the resource file and what it had written), temp copies (`<exe>.kiln-<uuid>`, `<exe>.kfc.kiln-<uuid>`) and, one per
  handoff, SF journals (`kiln/eml/ownership/sf-handoff-<uuid>.json`). The next run retains them and logs each possible
  recovery path: a UUID-shaped name is not proof that Kiln wrote the file or that its contents are unchanged. Inspect
  these copies before manually removing them; no automatic cleanup deletes them by name alone.
- **ShroudForge asset mods and EML mods together.** In a start where Kiln hosts ShroudForge (the active profile lists
  an enabled SF mod), only ShroudForge writes the game data: its early step reserves the writer before it prepares
  (`kiln.eml` `stand_aside`, section 6), and kiln-eml publishes `aside` and patches nothing. SF builds
  its startup asset mods and the EML packages in its mods folder (the launcher copies the profile's EML mods there)
  in one pass, so the game has both. `eml.json` says `aside` ("ShroudForge owns startup asset patching and runs EML
  packages in its mods folder") and lists the profile's EML mods as `own-loader`, or `not-loaded` when they are not in
  SF's folder. kiln-eml patches only in a start where SF does not run: its mods are off in the active profile,
  `KILN_SHROUDFORGE=0`, or SF could not start (its runtime is missing, or it failed before reserving the writer). If
  SF's state still says it prepared asset mods in an earlier start, that start's game data drops them: kiln-eml builds
  from the vanilla backup with the EML mods only, sets SF's state to `failed` and says which SF mods are missing (a
  warning in kiln.log, `kiln-eml: ShroudForge's asset mods <id> <version>, ... prepared by an earlier start, are not in
  this start's game data ...`, and `reason` plus `sfDisplaced` in `kiln/state/eml.json`, shown on the Engines row). The
  next start in which SF runs prepares them again, from the vanilla backup, with the EML mods in its folder. That
  happens once per change of who writes, not every start.
- **A backup of an earlier game build.** After a game update the backup (`<exe>.kfc.bak`) is of the old build while
  the game data is of the new one. A start without EML mods then compares nothing, restores nothing and claims nothing
  about the data: one info line in kiln.log (`... the vanilla backup is of another game build ...`), `staleBackup:
  true` in `kiln/state/eml.json`, and the launcher's archive banner says the backup is of an earlier build and nothing
  needs doing to that backup. Extra containers are still named in `reason` and the log for manual inspection. The next
  start with an EML mod makes a new backup of the current build, but refuses to replace any unproven container at an
  output path; inspect and move that file aside before retrying.
- **When ownership cannot be proven** and the data differs from the backup, the data is left as it is and the idle
  start says why: a `reason` in `kiln/state/eml.json`, a warning in kiln.log (`kiln-eml: no EML mods; game files
  untouched. The game data differs ...`), and the launcher's archive banner. If ShroudForge asset mods or another
  loader wrote it, nothing needs doing. If it still carries EML mods that were switched off: enable any EML mod for
  one start and disable it again only when no unproven DAT collision is reported (that start publishes with a receipt,
  the next one restores), or use Steam's "Verify integrity of game files". Neither route deletes unproven extra files:
  retained containers are named in `reason` and the log, including when the live archives already match vanilla or the
  backup is stale. Inspect them before manual cleanup; Steam verification can leave extra containers behind.
- **Writer handoff:** exact output/backup SHA-256 receipts live in `kiln/eml/ownership/`. Before changing archives,
  kiln-eml invalidates only a recognized ShroudForge prepared-asset cache under ShroudForge's installation lock.
  Settings and unrelated state survive; exact original/replacement state bytes remain in recovery journals. The same
  lock and state checks protect shared backup capture and resealing, before publication; checking or refreshing a
  baseline alone does not invalidate SF's applied state. Unknown schemas, busy locks and unsafe paths refuse the work.
  This does not automatically repair pre-existing stale SF state.
- **Launcher:** an archive differing from a shared backup does not prove EML wrote it. The old direct-restore command
  now refuses instead of copying a header without its resources, ownership checks or loader-state handoff. Disable EML
  mods and start the game through Kiln to let its archive writer restore proven EML output; when it cannot prove it,
  the banner shows the `reason` it gave and the two ways back above. No client rendering or the
  original reinstall reporter's exact installation was tested; the dedicated-server SF/EML/uninstall/SF round trip passed.

#### Reading the game data
EML decides whether to patch by fingerprinting the game's files: length, last-write time and a CRC-64 of every byte of
each `.dat`, `.kfc`, `.kfc_resources`, `.exe` and backup file, plus the mod files. EML 0.2.0 does that four times per
start (the change cache, the check before Lua, the check before publication, the snapshot it saves). A real client has
55 containers, about 48 GiB, so a patch run read about 190 GiB and even a start that changed nothing read 48 GiB
(GitHub issue [bisdat/kiln-mods#17](https://github.com/bisdat/kiln-mods/issues/17), whose analysis this follows).

Since dev.21 a file is read once per run, and not at all when the OS shows it unchanged since it was last read. Since
dev.22 the game's own files are not read at all the first time either, where the OS can vouch for them: a start with no
usable cache, or the first start after a Kiln update, reads about a gigabyte of what it writes and copies itself, not the
48 GiB (below). The integrity checks are the same checks; they just no longer re-read what cannot have changed.
- **What is compared.** `kiln/eml/cache/files.json` keeps, for each file, the length, the last-write time and a third
  number, and the OS's file identity and change time (the "stamp"): on Linux the device, inode and `ctime`, on Windows
  and Wine the volume serial number and file id (`FileIdInfo`) and the `ChangeTime` (`FileBasicInfo`). Any program can
  set a last-write time back; the change time moves with every write and with that reset, so a same-size edit with the
  old last-write time restored still shows. The third number is the CRC-64 of the bytes, or, for a file nobody needed
  the bytes of (dev.22), a digest of its stamp; the file's name is then in the list `unread`.
- **When the stamp counts.** Only when all of this holds: the stamp is complete, the file's volume passed a test in this
  run, and the change was older than three seconds. The test runs first, in the game folder: it writes a scratch file
  there, rewrites it with other bytes of the same length, puts the last-write time back and checks that the change time
  moved; it also checks that the file system keeps last-write times to 100 ns, which FAT and exFAT do not (their change
  time is not kept on disk; under Wine they are reported as NTFS). The scratch file is removed, and so is one a crashed
  start left behind. Mod folders are never written to. The three-second rule is for coarse file system clocks, where a
  later edit could get the same stamp as the one recorded.
- **What a start reads, by kind of file.**

  | File | At a start with no cache (or the first after an update) | Why |
  |---|---|---|
  | the 55 containers `<exe>_000.dat` ... (48.0 GiB on a real client) | **not read**: recorded by stamp | nothing in a run uses their CRC but the comparison with the saved one; the patch never rewrites them and reads only the resources a mod asks for |
  | `<exe>.kfc` (9 MiB), `<exe>.kfc_resources` (215 MiB), `<exe>.exe` (35 MiB), the backups and their seal, `eml.json` | **not read for the cache**; the run itself reads what it needs: the exe once for the type registry when `<exe>-types.json` is missing, the resource file once to copy it into the backup, the backups for the seal check (`.kfc.bak.integrity`, which stays a content check) and into the private stage | those reads are the patch's own and are not new |
  | what the patch has just written (`.kfc`, `.kfc_resources`, new containers), and files changed in the last three seconds (a backup made a moment ago) | read, each once more per step while it is that new | no stamp can vouch for a change that recent |
  | the mods' files (small, on another volume more often than not) | read | Lua reads them anyway |
  | every file, when the volume fails the test, the OS gives no stamp, or `KILN_EML_FULL_HASH=1` is set | read in full at every step | nothing can be assumed |

  On a real client that is (counted from the code path and the file sizes of a real install, not measured on one), at the
  first start, about 0.3 GiB from the drive (the exe, the live resource file) and about 1.6 GiB through the checksum and
  copy loops in all, most of it files written a moment before and still in the page cache, instead of 48 GiB. A start after that reads the 224 MiB the patch wrote once (their stamps were too new
  to record) and then nothing; a start after a mod change reads the 0.7 GiB of the backup seal and the stage copy and the
  224 MiB the patch writes.
- **The ownership receipt (dev.24)** is read outside the change cache and counted on its own (`ownershipBytes` in
  `fingerprint`, `checked ... against the ownership receipt` in the summary). A patch run hashes (SHA-256) what it
  publishes and the backups once, and the live files where their sizes equal the new ones, to write the receipt: about
  0.45 GiB on a client more. A patch that replaces an owned output also hashes that live previous set in full (to keep
  it as the receipt's previous generation): about 0.67 GiB in all, plus the containers EML added. An unchanged start reads none of it: the change cache records the receipt (`owner`, the
  SHA-256 of its bytes) right after the publication it lists, and while every game file is as it recorded them and the
  receipt is those bytes, the output is still that one; only the receipt's sizes are compared, from metadata. A start
  whose cache does not record it yet (a cache saved before dev.24, say) checks in full once and records it. A start
  without EML mods compares sizes first, so another writer's output (another length) is rejected unread; the full check
  is kept for the moment before a restore. Retained-container guidance also inventories DAT names and reads the small
  fixed header metadata (and same-build baseline version/count when idle), not archive entries or DAT contents.
- **When a saved fingerprint is reused:** the stamp is the one it was recorded under, and length and last-write time are
  unchanged. An unread record is reused the same way: it is its stamp.
- **A file whose stamp changed.** With a CRC on record, the file is read and the CRC decides, as in dev.21, until the
  start knows it must patch anyway: another build of kiln-eml than the one that saved the cache (every Kiln update that
  changes the plugin), no cache, or a file that already differs. From then on no file is read to be compared: it is
  recorded by its stamp, and counts as changed. An unread record has no CRC to compare, so a changed stamp is a changed
  file, and the patch runs again, even where the bytes turn out to be the same (a game folder copied to another drive,
  a program that touches every file).
- **Before publication** each input's stamp is read again. An input that changed since it was recorded stops the run,
  as before; a file that was read in this run is read again at every later step, so that its fingerprints stay
  comparable with each other.
- **After a patch** the files the patch wrote are too new to stamp. The next start reads them once, writes their
  stamps into `files.json` and reads nothing from then on.
- **Escape hatch.** `KILN_EML_FULL_HASH=1` in the game's environment turns every use of the stamp off: every file is
  read in full at every step, as in EML 0.2.0. `kiln-eml-run --skip-cache` ignores the saved cache but still uses stamps
  within the run.
- **The CRC-64 values did not change** (an old `files.json` still loads), but they are computed with sixteen-way tables,
  several times faster than the crate's default.

What this trusts: that the operating system updates the change time when a file's data changes, and, new in dev.22,
that a game file is what it was when kiln-eml first saw it: the first record of a file rests on its stamp alone, where
the CRC (dev.21) would have recorded whatever bytes were there and compared them with nothing, so damage or a foreign
patch that predates the first record is accepted as the baseline either way (Steam's "verify files" is the check for
that), while a change after it shows as a changed stamp. It catches tools, updates and accidents, not a program written
to hide an edit: on Windows a program can set the change time back (`SetFileInformationByHandle`), and anywhere a
process allowed to set the system clock could. A file system that does not keep a change time but passes the test above
would defeat it too. `KILN_EML_FULL_HASH=1` removes the assumption.

A power loss or a hard crash of the machine can also break it: a file's new data can reach the disk before its new
change time does, so the file comes back changed with its old size and times. After such a crash, start once with
`KILN_EML_FULL_HASH=1` (or let Steam verify the game files).

**Older and newer Kilns.** `files.json` gained two optional parts, `stamps` (dev.21) and `unread` (dev.22); a reader
that does not know one ignores it.
- dev.22 reading a dev.21 file: its CRCs and stamps are used as they are (the first start after the update reuses
  every stamp it can and records the rest by stamp). Reading a dev.20 file (CRCs, no stamps): the start patches anyway (the
  build id differs), and the containers are recorded by stamp instead of being read to compare CRCs that are going to
  be replaced.
- dev.21 or dev.20 reading a dev.22 file: the entries of unread files look like fingerprints no content has. With the
  stamp unchanged the old code reuses them, and still patches (its build id differs from the file's); with a changed stamp
  it reads the file, gets a CRC the entry does not have, and counts the file as changed. It never takes a changed file
  for an unchanged one, and the file it saves after its own patch has real CRCs again.

**An interrupted start.** While a start reads files, it keeps a second small file, `files.partial.json`, next to
`files.json`: the fingerprints of the files it has read so far (with their stamps), replaced whole every two seconds or so.
It has no build id, so it can never be mistaken for a finished cache, and it is used like the saved fingerprints: an entry
applies only to a file whose stamp, length and last-write time are the ones it was taken under. A start that is killed
half way through reading, which the player sees as closing the game on a long wait, continues with the files it had read;
a start that ends well removes the file. Nothing is written when a start reads nothing. A volume that fails the test
trusts no stamp, so on it a start has nothing to continue from: it reads everything every time (as EML 0.2.0 does),
and the log says so.

Each run logs what it read (eml.log, and kiln.log as `[eml] Fingerprints: ...`), and the summary line and
`kiln/state/eml.json` carry it:
```
[I 15:02:41.118] [early:kiln-eml.dll] [eml] Fingerprints: hashed 562 B in 4 file(s), recorded 0 file(s) (0 B unread), reused 22 file(s) (7.6 GiB not read), 19 ms
```

#### What you see while it runs
A start that takes more than a moment says so in four places, so that it never looks dead (dev.22; a player on a Steam
Deck saw nothing for minutes and gave up):
- **kiln.log**, before each step that reads, one line with what and why, then a line every five seconds while a step
  lasts with the share done, the rate and the file, and a warning when a read moves no byte for 15 s:
  ```
  [I 12:01:34.322] [early:kiln-eml.dll] [eml] Change stamps are reliable here; unchanged files are not read again folder=... volume=0x0
  [I 12:01:34.376] [early:kiln-eml.dll] [eml] Game data: 19 file(s), 7.5 GiB: 19 file(s) (7.5 GiB) recorded by their change stamp, not read
  [I 12:01:34.541] [early:kiln-eml.dll] [eml] Backing up the vanilla game data: enshrouded_server.kfc and enshrouded_server.kfc_resources (141.4 MiB) are copied, then read back for the seal
  [I 12:05:11.524] [early:kiln-eml.dll] [eml] Game data: 19 file(s), 7.5 GiB: reading 19 file(s) (7.5 GiB): KILN_EML_FULL_HASH is set (19)
  [I 12:05:16.623] [early:kiln-eml.dll] [eml] Checking the game data: 63% (4.7 GiB of 7.5 GiB), 944.8 MiB/s, now enshrouded_server_004.dat
  ```
  and, for example, after a start was killed: `Resuming: an earlier run that did not finish had read 12 file(s) (2.9 GiB)`; and when a read stops
  moving: `No data read for 15 s: still waiting on enshrouded_031.dat (29.9 GiB of 48.6 GiB read in this step, 31 file(s) done)`.
  A reader can tell "61% and moving" from a read the drive does not answer. Steps that are not reads (the mods' Lua, the
  write) say `Running the mods: still going, 35 s so far` the same way.
- **`kiln/state/eml.json`** gets `phase: "running"` and a `progress` object (stage, bytes done and to do, files, the file,
  `quietMs`), rewritten whole and atomically a few times a second and at least once a second, from the first moment of the
  run; the result replaces it ([`schema/state-eml.schema.json`](../../schema/state-eml.schema.json)).
- **A small window** from Kiln's core (`kiln.progress`, [`kiln_progress.h`](../../native/core/abi/services/kiln_progress.h)):
  "Preparing the game data for EML mods", a line of text and a bar. It appears when the work has lasted two seconds, never
  takes the keyboard focus, is gone before the game's own window can appear, and does not exist (no error) on a dedicated
  server, with `KILN_UI_DISABLE=1` or where no desktop is there. Under gamescope (the Steam Deck's game mode) it covers the
  screen with the panel in the middle, since gamescope scales the one window it shows. Plain Win32 on its own thread; any
  early plugin can use the same service (begin, update, end). `KILN_PROGRESS_DELAY_MS` changes the two seconds.
- **The launcher**, when it started the game: the Play page's launch status reads the state file
  (`kiln_manage::prestart`): "Preparing the game data for EML mods: 12.3 GiB of 48.6 GiB. Only the first start after a game
  update takes this long." It counts only while the game runs and the file is fresh, so a start that died leaves nothing behind.

#### When a mod cannot be read
`state/eml.json` and kiln.log give the reason: `entry src/mod.lua: <stage> failed: <OS error>; <what exists>`. Example:
`entry src/mod.lua: directory metadata (src) failed: Path not found (os error 3); root exists, src missing`.

| The reason says | What happened |
|---|---|
| `root metadata failed`, `root missing` | the mod folder is gone. Most often the launcher's Library version was removed while the game was starting. Re-add the mod and start again. |
| `directory metadata (src) failed` | the folder has no `src` (or `src` is a file). |
| `file metadata failed` | `src/mod.lua` is missing, or it is a folder. |
| `canonicalize failed` | the folder and file exist, but the OS cannot resolve the path. Under Wine/Proton, report the raw error text. |
| `containment check failed` | `src/mod.lua` resolves outside the mod folder (a link that leaves it). |
| `open failed` | the file exists but cannot be opened (permissions, or another program has it locked). |
| `read failed` | the file is not valid UTF-8 text, or reading it failed. `(it reads fine now: ...)` means it changed on disk in between. |
| `zip entry failed` | a zipped mod has no `src/mod.lua`. |

The last part of the line says which parts of the path exist right now (`root exists, src missing`).
If a read fails later, during the run, eml.log and kiln.log have a line
`module mods.<id>.<module> could not be read 184.2s after the mod was loaded: <stage> failed: ...; root exists, ...`
(`does not exist` instead of `could not be read` for a module the intact mod does not have):
the stage, whether the mod folder and `src` still exist, and how long after the mod loaded it happened. Only one mod
is switched off. A long gap means something changed the folder while the game was starting.

The launcher refuses to remove a Library version while the game runs ("Close Enshrouded first: the game may be
reading this mod"), and "Clean up" never lists a version the game's `kiln/profile.json` still points at, or its
`kiln/profile.once.json` while `kiln/play-once.ini` still asks the next start for it (the once file stays after its
start; nothing reads it again). Remove names the same users and removes past them once confirmed. A running dedicated
server blocks removal only when its own `kiln/profile.json` points into the Library (servers ignore play-once); servers
normally get copies of their mods (`kiln/mods/`), so an always-on server does not block it.

How it composes with Kiln's asset layer: EML changes the files before the game reads them, and `kiln.assets`
overrides resources and blobs in memory after the game reads them. An override in a Kiln asset pack wins over an EML
patch of the same resource.

### Real EML present: kiln-eml steps aside
Two engines must never write the KFC one after the other: the second write would drop the first one's mods.

When EML's own proxy is **loaded in this process**, kiln-eml stands aside: EML has already patched in its `DllMain`.
The same holds for ShroudForge's, which carries EML. kiln-eml enumerates the modules named `dbghelp.dll`/`dinput8.dll`
or under `kiln\chain\`, outside `\windows\`, whose file holds `EML_LOG_FILE_PATH`, and leaves out Kiln proxies. The
loaded image's PE header (time stamp, image size, entry point) must be the file's: under Wine without a DLL override
the process holds Wine's builtin `dbghelp.dll` under the game folder's path, and kiln-eml then runs the mods (lab,
dev.22; before, it stood aside there and nothing ran them).
- kiln.log says `kiln-eml: standing aside: <path> is loaded: EML's own loader patched the game files this start`.
- `state/eml.json` has `phase: "aside"`.

**The profile's EML mods** live in Kiln's Library, which EML's own loader never reads. Since dev.22 the launcher copies
them into the folder that loader reads while it is installed: `<game>/mods/<library id>/` for EML's own
`dbghelp.dll`/`dinput8.dll`, ShroudForge's mods folder for ShroudForge's loader or when Kiln hosts ShroudForge for the
profile ([engines/README.md](engines/README.md#the-profiles-mods-while-the-own-loader-runs)). Standing aside, kiln-eml
lists each profile entry in `state/eml.json`: `own-loader` (Kiln's copy is in that folder, so EML applied it) or
`not-loaded` (it is only in the Library), with the reason, and logs a warning per mod that does not load:
```
[W] kiln-eml: profile EML mod stacks does not load: EML's own loader runs this start and reads only Z:\...\mods; ...
kiln-eml: 2 of the profile's 2 EML mod(s) run under EML's own loader
```
Kiln never places a second copy of a mod the player already has in `mods/` (the same manifest id under another
folder name): EML stops the game on a duplicate id. The player's copy runs.

When EML's `dbghelp.dll` only sits in the folder but is **not loaded** (Wine without the override), kiln-eml runs the
mods, which is the Linux case EML can't handle by itself. The profile's entries then run from the Library and the
launcher's copies in `mods/` are skipped as drop-ins. The installer (agent `detect`) may chain EML's `dinput8.dll`
in `kiln/chain/`. It then loads, and kiln-eml stands aside, as verified on a lab server (section 5).

### Logs and state
- `kiln.log`, owner `early:kiln-eml.dll`: EML's info, warning and error lines as `[eml] ...`, one line per failed or
  skipped mod, and a summary such as
  `kiln-eml: 2 EML mod(s) loaded, 3 resource(s) patched in enshrouded_server (1263 ms; hashed 12.0 KiB, reused 23 file(s))`.
- `kiln/logs/eml.log`: everything, debug included.
- `kiln/state/eml.json` (format 1, for the launcher and `Kiln.Eml`):
  `{ pid, phase: done|failed|aside|idle|running, file, patched, cached, restored, ms, fingerprint, error, aside, reason, mods: [{ id, version, name, source, path, state, reason, capabilities }], progress }`
  ([schema](../../schema/state-eml.schema.json)). With phase `aside`, `mods` holds the profile's EML entries with state
  `own-loader`, `not-loaded` or `disabled` (dev.22; before, it was empty). `reason` (dev.24, absent otherwise), one
  sentence for the player: with phase `idle`, the game data differs from the vanilla backup and kiln-eml could not
  prove it wrote it, so it left it, and what to do (see "It fails soft"); with phase `done`, the ShroudForge asset
  mods an earlier start prepared that this start's game data does not have, because ShroudForge did not run this
  start; listed in `sfDisplaced` as `<id> <version>` (see "ShroudForge asset mods and EML mods together" below).
  `staleBackup` (dev.24, phase `idle`, `true` or absent): the backup is of another game build than the game data and
  was not compared with.
  `fingerprint` (dev.21, only when a run finished) is `{ hashedBytes, hashedFiles, reusedFiles, reusedBytes, ms }` plus,
  since dev.22, `recordedFiles` and `recordedBytes` (files recorded by their stamp, not read): what reading the game's
  files for the change cache cost this start, and since dev.24 `ownershipBytes` (what the ownership receipt's SHA-256
  checks read, apart). `phase: "running"` and `progress` (dev.22, only while the run goes on) are
  described under [What you see while it runs](#what-you-see-while-it-runs).

### Licence layout
| What | Licence | Where |
|---|---|---|
| EML's crates (8, v0.2.0 + changes listed in the GPL README) | GPL-3.0-or-later | `gpl/kiln-eml/eml/` |
| kiln-eml plugin + `kiln-eml-run` | GPL-3.0-or-later | `gpl/kiln-eml/kiln-eml/` |
| Licence, notice, build | GPL-3.0 text | `gpl/kiln-eml/{LICENSE,NOTICE.txt,README.md,build.sh,Cargo.toml,Cargo.lock}` |
| The early-plugin step | MIT | `native/core/src/early_plugins.{h,cpp}` |
| The progress window (`kiln.progress`) | MIT | `native/core/src/services/progress.cpp`, `native/core/src/progress*.h`, `native/core/abi/services/kiln_progress.h` |
| The `kiln.eml` table (C header) | MIT | `native/core/abi/services/kiln_eml.h` |
| MIT ports (from EML's docs and behaviour) | MIT | `managed/services/Kiln.Eml/`, tests `managed/tests/Kiln.Eml.Tests/` |
| This document, the test mods' descriptions | MIT | `docs/kiln/eml.md` |

GPL code exists only under `gpl/kiln-eml/`, which is its own Cargo workspace. No MIT file includes or links it. Kiln
talks to the DLL only through its C exports. Release artifacts:
- `kiln/plugins/kiln-eml.dll`, with `kiln-eml.LICENSE.txt` (GPL-3.0) and `kiln-eml.NOTICE.txt` next to it;
- runtime component `eml`;
- the source is `gpl/kiln-eml` in the source tarball.

## 3. Coverage: EML 0.2.0's Lua API

There are 89 functions (plus the generated `integer` helpers and the game types). kiln-eml runs EML's own code, so
every function is the original: **89/89 implemented**.
- The probe mod (section 5) calls each one: **89/89 passed** offline (`kiln-eml-run`, export on), and 88/89 in the lab
  server process. `io.export` is off there, as with EML, because the lab has no `eml.json`.
- The `integer` helpers (8 types; parse/truncate/clamp/is_valid/to_string plus checked/saturating/wrapping/overflowing
  arithmetic, 512 functions) are all EML's; 13 are sampled.
- Upstream quirk, kept: `integer.<T>.parse` ignores its documented `radix` argument.

The "Kiln equivalent" column is Kiln's own API (MIT). "port" means a new MIT port of the EML function (section 4).
The others name the Kiln API that does that job; Kiln changes data in memory at run time rather than in files.
The EML probe tests EML itself. A C# unit test checks the named helper, not its presentation or use in a running client.
Remaining C# differences include Impact-name/hash lookup, enumeration of the game's content blobs, non-PNG image
formats and a general block-compressed texture codec. These do not reduce kiln-eml's hosted coverage.

| EML function | kiln-eml | Kiln equivalent (MIT) | Tested |
|---|---|---|---|
| `typeof(v)` | EML | C# `GetType()` | probe |
| `require(path)` (local, `mods.<id>.<path>`) | EML | assembly references, `IModExports` | probe (both forms) |
| `builtin` table | EML | n/a | probe |
| `loader.is_client` / `is_server` (fields) | EML | `api->side()`, `Game` | probe |
| `loader.features` (field) | EML | n/a | probe |
| `loader.has_mod(id)` | EML | **port** `Eml.IsLoaded(id)` (+ `Mods.*` for Kiln mods) | probe; C# test |
| `loader.runtime.register_dll(path)` | EML (stub upstream) | native mods (`native.json`) | probe |
| `game.version` (field) | EML | `api->build_info` | probe |
| `game.types.get(name or hash)` | EML | `GameTypes.Find/ByHash` | probe |
| `game.types.get_by_qualified_name` | EML | `GameTypes.Find` | probe |
| `game.types.get_by_qualified_hash` | EML | `GameTypes.ByHash` | probe |
| `game.types.get_by_impact_name` | EML | no Impact-name lookup in Kiln C# | probe |
| `game.types.get_by_impact_hash` | EML | no Impact-hash lookup in Kiln C# | probe |
| `game.types.get_all` | EML | `GameTypes.All` | probe |
| `game.types.of(value)` | EML | `GameTypes.TypeOf(value)` for reflected values, resources, fields and types | probe; C# unit tests |
| `game.assets.get_resource(guid, type, part)` | EML | `Assets.ReadResource(key)` | probe |
| `game.assets.get_resource_parts(guid, type)` | EML | `Assets.ListResources(type)` | probe |
| `game.assets.get_resources_by_type(type)` | EML | `Assets.ListResources(type)` | probe |
| `game.assets.get_all_resources()` | EML | `Assets.Dump(spec)` | probe |
| `game.assets.get_resource_types()` | EML | `GameTypes.All` lists reflected types, without a resource-only filter | probe |
| `game.assets.create_resource(value, type[, guid, part])` | EML | `Assets.Register(key, bytes)`, typed blocks | probe (both forms) |
| `game.assets.get_content(guid or ContentHash)` | EML | advanced C ABI `kiln.assets.blob_read`; no managed blob-read helper | probe |
| `game.assets.get_all_contents()` | EML | partial: `Assets.ListBlobs/ListAllBlobs` list blobs supplied through the managed API, not game or manifest blobs | probe; C# unit tests |
| `game.assets.create_content(buffer)` | EML | `Assets.AddBlob(bytes)` | probe |
| `Content:read_data()` | EML | advanced C ABI `kiln.assets.blob_read`; no managed blob-read helper | probe |
| `game.guid.hash(guid)` | EML | `GuidHash.Of(guid)` (`Kiln.Eml`) | probe; C# unit tests |
| `game.guid.from_content_hash(h)` / `to_content_hash(guid)` | EML | `ContentHash` in Kiln.Assets | probe |
| `hasher.fnv1a32(v)` | EML | **port** `DataHash.Fnv1a32` | probe; C# test |
| `hasher.crc32(v)` | EML | **port** `DataHash.Crc32` | probe; C# test |
| `hasher.crc64(v)` | EML | **port** `DataHash.Crc64` | probe; C# test |
| `buffer.create(capacity)` | EML | **port** `new DataBuffer(capacity)` | probe; C# test |
| `buffer.wrap(string)` | EML | **port** `DataBuffer.Wrap` | probe; C# test |
| `Buffer:head()` / `head(pos)` | EML | **port** `DataBuffer.Head` | probe; C# test |
| `Buffer:tail()` / `tail(pos)` | EML | **port** `DataBuffer.Tail` | probe; C# test |
| `Buffer:remaining()` / `capacity()` | EML | **port** `Remaining` / `Capacity` | probe; C# test |
| `Buffer:reset()` / `reserve(n)` / `skip(n)` | EML | **port** `Reset` / `Reserve` / `Skip` | probe; C# test |
| `Buffer:order()` / `order(o)` | EML | **port** `Order` (`ByteOrder`) | probe; C# test |
| `Buffer:copy(src, offset, length)` | EML | **port** `Copy` | probe; C# test |
| `Buffer:close()` | EML | n/a (GC) | probe |
| `Buffer:to_string()` | EML | **port** `ToString()` | probe; C# test |
| `Buffer:read_bool/i8/u8/i16/u16/i32/u32/i64/u64/f16/f32/f64` (12) | EML | **port** `ReadBool` ... `ReadF64` | probe; C# test |
| `Buffer:read_string(n)` | EML | **port** `ReadString` | probe; C# test |
| `Buffer:write_bool ... write_f64` (12) | EML | **port** `WriteBool` ... `WriteF64` | probe; C# test |
| `Buffer:write_string(s)` | EML | **port** `WriteString` | probe; C# test |
| `Buffer:read_resource(type)` / `write_resource(type, v)` | EML | `Kiln.Game` typed structs | probe |
| `image.create(w, h)` | EML | `new DataImage(w, h)` (`Kiln.Eml`) | probe; C# unit tests |
| `image.decode(buf, fmt)` / `encode(img, fmt)` | EML | `DataImage.DecodePng/EncodePng` (PNG only) | probe (png); C# unit tests |
| `image.decode_texture` / `encode_texture` | EML | partial: `DataImage.FromRgba8/ToRgba8`; `Assets.OverrideTexture` encodes a PNG to an existing texture's format; no general texture decoder | probe (R8G8B8A8); C# raw-pixel unit tests |
| `Image:get_pixel/set_pixel/get_pixel_packed/set_pixel_packed` | EML | `DataImage.GetPixel/SetPixel/GetPixelPacked/SetPixelPacked` | probe; C# unit tests |
| `io.read` / `read_to_string` | EML | `System.IO`, `ModContext` folders | probe |
| `io.list_files` / `exists` / `is_file` / `is_directory` | EML | `System.IO` | probe |
| `io.name` / `name_without_extension` / `extension` / `parent` / `join` | EML | `System.IO.Path` | probe |
| `io.export(path, data)` | EML (needs `use_export_flag`) | `ModContext.DataDirectory` | probe (offline) |
| `integer.*` (512) | EML | C# fixed-size integers, `checked`/`unchecked` | probe (13 sampled) |

**Counts:**
- implemented: 89/89, all by EML's own code;
- probe: 89/89 offline, 88/89 in the lab (the 89th, `io.export`, is off there);
- C# helpers include `DataHash`, `DataBuffer`, `Eml.IsLoaded`, `GuidHash` and `DataImage`; `GameTypes.TypeOf`
  handles reflected values and `Assets.ListBlobs/ListAllBlobs` list managed additions/overrides;
- C# equivalence is qualified per row: Impact-name/hash lookup and complete game-blob enumeration are absent;
  the image helper covers PNG and RGBA8, with other formats outside that helper. `Buffer:close` needs no equivalent
  because managed memory is collected.

## 4. MIT ports (`Kiln.Eml`)
These are Kiln's own code (`managed/services/Kiln.Eml/`, deployed to `kiln/host/` like the other service assemblies),
written from EML's documentation and from what the probe observed, never from EML's code:
- `DataHash.Fnv1a32/Crc32/Crc64` (string or bytes): the algorithms EML's docs name (FNV-1a 32,
  CRC-32/ISO-HDLC, CRC-64/ECMA-182).
- `DataBuffer`: EML's buffer model. Writes append at `Tail` and reads consume from `Head`. It has `Order` (little or
  big), every fixed-size type including `Half`, strings, `Skip/Reset/Reserve/Copy`, `Wrap`, `ToString`. EML Lua code
  ports to C# line by line.
- `GuidHash.Of` hashes GUID bytes; `IdOf` also preserves the game's zero-id convention.
- `DataImage` creates RGBA8 images, reads/writes individual or packed pixels, and decodes/encodes PNGs.
  `FromRgba8/ToRgba8` copy raw pixels; this is not a general block-compressed texture codec.
- `Eml.Status` / `Eml.IsLoaded(id)`: the current process's `state/eml.json`, checked by PID and write time so a missing
  or disabled plugin cannot report an old run. `Eml.Load(path)` can inspect a historical result. These are the port of
  `loader.has_mod`, plus the run's result for Kiln mods that build on an EML mod.

```csharp
if (Eml.IsLoaded("better_stamina")) Log.Info("EML mod better_stamina is active; skipping our own stamina tweak");
var b = new DataBuffer(); b.WriteU32(DataHash.Fnv1a32("keen::ItemInfo")); // 0xb5ce8765
```

Not ported, on purpose:
- typed game data (`read_resource`, `create_resource`, `types`): Kiln has its own typed path (`Kiln.Game` generated
  types, typed blocks, `kiln.assets` overrides at run time);
- image formats beyond PNG/RGBA8 and Impact-name/hash lookup; the PNG/RGBA8 helpers above are implemented;
- a C#/Lua pre-game KFC writer: kiln-eml is the single writer of the game files (section 6), and a second writer
  would conflict.

## EMF creature portraits

The separate GPL component exports `kiln_eml_portraits`, an offline CPU renderer for EMF manifest portrait requests.
The reader/decoder stays in `gpl/kiln-eml`; kiln-emf's MIT adapter plans bounded requests and calls the export before
EMF DLL mods load on a client (drawing runs on a worker thread; nothing is drawn when every picture is current). It reads the local game archives and writes only the portrait cache. The renderer's
EMF 0.6.0 source is pinned at `bd6d90644785df80e4a61cb6ec55f9dab8e16b22`, with its MIT notice retained in the GPL
component's `NOTICE.txt`.

Manifest entries name a `key`, exactly one `template` or `model` GUID, `frames` (1..64) and `size` (8..512). The
renderer accepts at most 32 portraits and 32 million output pixels per request. A process lock protects cache
publication; a failed replacement preserves the prior frames. EMF's `image_load("portrait:<key>")` uses that cache.
A dedicated server does not automatically generate or draw portraits.

Offline Rust/cache and read-only archive checks passed. Actual Windows DLL ABI checks rendered textured wolf frames
from the client archive and reused the cache on Wine C: and mounted Z: paths. Synthetic nonredirecting Wine volume
ancestors are accepted; symbolic/junction redirects are refused without external mutation, and caches remain regular paths.
These results do not prove automatic client startup, custom client portraits, Beastmaster's pictures, taming, UI or multiplayer behaviour. The build and
focused test commands are in `gpl/kiln-eml/README.md`; the existing 89-function EML coverage count is unchanged.

## 5. Tests and evidence

### Host
- Rust tests: `gpl/kiln-eml/build.sh --test` runs the plugin, Lua runner and base-container tests before attempting
  the Windows build. Regressions cover interrupted publication, exact restoration, corrupt backups, preserved-time
  edits, process locks, ABI versions, path validation, zip classification and removed-content padding. The
  fingerprint tests (`cache/tests.rs`, `load.rs`) cover: a cache hit hashes 0 bytes of unchanged files; each file is
  hashed once across the four steps of a run; a same-size edit with the last-write time restored is read again (across
  runs and between steps); a replaced file is read again; a vanished, unreadable or symlinked input poisons the
  snapshot; a change newer than three seconds is not stamped; a change during the read is not stamped; a volume whose
  change time never moves, a stamp that cannot be read, a second volume, and a volume the OS does not name are never
  trusted; a cache file from before stamps loads, is read once and then is fast; the CRC-64 values equal the crate's
  reference implementation; an input edited in place after the last barrier is never cached. They also run as a
  Windows build under Wine (`cargo xwin test --no-run`, the test exe under GE-Proton's Wine).
- C# tests: `dotnet test managed/tests/Kiln.Eml.Tests` covers hash check values, buffer round trips, byte order,
  zero-filling after cursor changes, overflow and current-process state validation.
- `kiln-eml-run --game <dir> [--mod <path>]... [--profile <json>] [--no-patch] [--export --export-dir <d>] [--state <file>]`:
  the plugin's discovery and run on Linux, against a game folder (use a reflinked copy, never a real install). It
  prints the state JSON.

### Offline content check (2026-11-07, server build 1287369)
Test mods (in `/mnt/Dongus/kiln-wave/eml/testmods/`, not in the repo):
- `kiln_eml_slice` sets `keen::BalancingTable.playerBaseStamina` = 777;
- `kiln_eml_verify` (export) reads it back;
- `kiln_eml_probe` calls the whole API;
- `kiln_eml_broken` has a deliberate Lua error.

| Step | Result |
|---|---|
| slice on a reflinked copy | `playerBaseStamina before = 100` → `Applied 1 patches`, 1.8 s with the type extraction |
| second run | `No changes detected, skipping patching` (`cached: true`) |
| verify on the patched files | `playerBaseStamina = 777`; on the vanilla files `= 100` |
| probe | `kiln_eml_probe: 58/58 passed` (58 checks cover the 89 functions) |

### Lab server `wv-eml-1` (UDP 27100)
The deploy is minimal and native only (proxy, kiln-core with the early step, sigdb, kiln-eml; `clr: false`). These
are kiln.log lines:
```
[I 14:35:04.510] [early:kiln-eml.dll] [eml] kiln-eml 0.1.0 (EML 0.2.0) starting
[I 14:35:04.511] [kiln] service kiln.eml v1 published by early:kiln-eml.dll
[I 14:35:04.734] [early:kiln-eml.dll] [eml] kiln_eml_slice: playerBaseStamina set to 777
[I 14:35:04.769] [early:kiln-eml.dll] [eml] Applied 1 patches
[I 14:35:04.796] [early:kiln-eml.dll] kiln-eml: ...\mods\not-an-eml-mod skipped: an EMF mod (mod.json names a dll/main or an EMF API version)
[I 14:35:04.796] [early:kiln-eml.dll] kiln-eml: 1 EML mod(s) loaded, 1 resource(s) patched in enshrouded_server (283 ms)
[I 14:35:05.834] [kiln] boot complete; handing control to the game
```
- The server reached `Switching state from LoadEcsScene to Run`, and its `.kfc` read back 777.
- Restart: `1 EML mod(s) loaded; nothing changed since the last run, the game files keep their patches (24 ms)`.
- Broken mod next to the slice:
  `EML mod failed; running the others without it mod_id=kiln_eml_broken error=runtime error: [string "mods.kiln_eml_broken"]:2: attempt to index a nil value`.
  The slice stayed loaded.
- Probe in the server process: `kiln_eml_probe: 58/58 passed, 0 failed`, then
  `2 EML mod(s) loaded, 3 resource(s) patched (1263 ms)`. The server ran with resources and content the probe
  created.
- EML's own `dinput8.dll` (built from upstream v0.2.0 for the test) chained through `kiln/chain.ini [chain]`: EML
  patched in `DllMain` (its own log: `Applied 1 patches`), and kiln-eml logged
  `standing aside: ...\kiln\chain\eml\dinput8.dll is loaded: EML's own loader patched the game files this start`.
- All mods removed: `no EML mods; the game data was restored to vanilla (EML's .kfc.bak)`. `enshrouded_server.kfc`
  is byte-identical to the base install's again.
- A profile with `mods.eml: [{ path: Z:\...\library\kiln_eml_slice }]` and drop-ins off: the slice ran from the
  library (`source: profile`), and the EMF folder in `mods/` was not looked at.
- **Bug found and fixed** (change 3 in the README): after the probe's created content was removed, the server
  refused to start (`Container ..._010.dat has wrong size 4'096 (expected: 0)`). kiln-eml now truncates the padding
  containers. EML 0.2.0 has the same bug; it is worth reporting upstream.

### Fingerprint cost (2026-11-09, dev.21; GitHub issue bisdat/kiln-mods#17)
**Synthetic 55 containers of 1 GiB** (sparse files, 55.0 GiB; Linux host, the four fingerprint steps of a start
replayed with the same code `run` uses, `cargo test --release -p mod-loader-lua bench_ -- --ignored --nocapture`).
"Read" is the process's read counter. The page cache was warm for every row, so the times are CPU-bound CRC and mean
more for the ratios than as absolutes. Both columns use the new sixteen-way CRC tables; EML 0.2.0's own (byte-at-a-time)
CRC ran at about 0.2 to 0.4 GiB/s here, several times slower again.

| Start | EML 0.2.0 behaviour (`KILN_EML_FULL_HASH`) | dev.21 |
|---|---|---|
| cold, patch run (no cache) | 220 GiB read, 95.9 s | 55 GiB read, 22.3 s |
| nothing changed (cache hit) | 55 GiB read, 23.0 s | 0 B read, under 1 ms |
| a mod changed, warm cache (patch run) | 220 GiB read, 93.8 s | 0 B read, 1 ms |

The patch run row replays the steps without writing: a real patch run also reads the files it publishes, once (see
below). The warm rows are 0 B because every file passed its stamp check.

**Real server data through the whole pipeline** (7.5 GiB, 10 containers; the real `kiln-eml.dll` in a lab server under
Wine, `kiln.log` lines, one test mod; times are those of kiln-eml's own summary line):

| Start | `KILN_EML_FULL_HASH=1` | stamps |
|---|---|---|
| cold, patch run | 30.2 GiB in 78 files, 15.3 s (`kiln-eml-run`, native Linux, same data) | `hashed 7.8 GiB in 36 file(s), reused 42 file(s) (22.4 GiB not read)`, 6.6 s |
| first start after a patch run | as in a start that changed nothing | `hashed 141.5 MiB in 12 file(s), reused 14 file(s)`, 150 ms |
| nothing changed | `hashed 7.6 GiB, reused 0 file(s)`, 5.4 s | `hashed 562 B in 4 file(s), reused 22 file(s) (7.6 GiB not read)`, 64 ms |
| a mod changed, warm cache | `hashed 30.2 GiB in 78 file(s)`, 13.4 s | `hashed 141.5 MiB in 14 file(s), reused 64 file(s) (30.1 GiB not read)`, 1.0 s |

The 562 B are the mod's own files, which sit on a mount Wine gives no serial number, so they are read in full. The
141.5 MiB are the files the patch wrote, read once because they were too new to stamp. The cold times are for a warm
page cache (in the first lab start, with a cold page cache, reading the 7.5 GiB took 36 s).

### Start cost (2026-11-10, dev.22; the Steam Deck report that a second start "does not load")
The same synthetic client as above (sparse files: 55 containers of 1 GiB, and the exe, header, resource file and backups
at their sizes on a real client, 55.5 GiB in all), the fingerprint steps of each start replayed with the code `run` uses
(`cargo test --release -p mod-loader-lua bench_ -- --ignored --nocapture`; `KILN_EML_BENCH_SKIP_EML=1` and
`KILN_EML_BENCH_SKIP_MODES=1` leave out the rows that take minutes). "Read" is the process's read counter, warm page cache,
so the times mean more for the ratios than as absolutes. Only the fingerprint steps are replayed: what a real run reads and
copies besides (the exe for the types, the backup copy and seal, the stage copy, about 1.4 GiB on a real client, most of
it just written and still in the cache) is not in these rows.

| Start | EML 0.2.0 (`KILN_EML_FULL_HASH`) | dev.21 | dev.22 |
|---|---|---|---|
| first start ever (no cache), patch run | 221.5 GiB, 194 s | 55.7 GiB, 28 s | 0.22 GiB, 0.2 s |
| the start after a patch run (stamps of the files it wrote) | 55.5 GiB, 37 s | 0.22 GiB, 0.1 s | 0.22 GiB, 0.2 s |
| nothing changed | 55.5 GiB, 49 s | 0 B | 0 B (dev.24: also nothing for the ownership receipt, see above) |
| a mod changed (patch run, warm cache) | 221.5 GiB, 189 s | 0.22 GiB, 0.2 s | 0.22 GiB, 0.2 s |
| first start after a Kiln update, saved cache from dev.21 (stamps) | | 0 B | 0 B |
| first start after a Kiln update, saved cache from dev.20 (CRCs, no stamps) | | 55.5 GiB, 37 s | 0 B |
| restart after a first start that was killed half way (it had read 24.5 GiB in dev.21) | | 55.5 GiB, 40 s | 0 B |
| restart after a start killed half way through comparing CRCs (a cache without stamps and the same build: what the journal is for) | | 55.5 GiB, 28 s (the killed one read 24.5 GiB) | 32.0 GiB, 16 s |
| a volume that fails the stamp test, nothing changed | 55.5 GiB | 55.5 GiB, 28 s | 55.5 GiB, 28 s |

- The 0.22 GiB are the header (9 MiB) and the resource file (215 MiB) the patch wrote, too new to stamp; the next start reads
  them once, as in dev.21. Where dev.21 read 55.7 GiB at the first start, the containers are now recorded, not read.
- The Steam Deck player's start (a first start with no usable cache, on dev.21) is the first row: 55 GiB through a
  CRC loop, minutes on an SD card, with nothing in the log. dev.22 reads none of it.
- A killed start in dev.22 has nothing big to continue from (it reads a quarter of a gigabyte), so the journal only shows in
  the row that compares CRCs, which a normal start does not do: it needs a cache without stamps and a Kiln build the cache
  was made by. It is there for the cases that remain (a stamp-less cache after a rollback to dev.20 and forward again, files
  whose fingerprint was taken less than three seconds after a change). A volume that fails the test cannot resume anything: no
  record of a file says it is unchanged there, so every start reads everything, as EML 0.2.0 does. That row is the one
  that does not improve, and it is the same size as a start of dev.20.

**Lab server** (7.5 GiB, 10 containers, the real `kiln-eml.dll` under Wine, one tiny test mod, from a copy of the vanilla
install; kiln.log lines):
```
[I 12:01:34.376] [early:kiln-eml.dll] [eml] Game data: 19 file(s), 7.5 GiB: 19 file(s) (7.5 GiB) recorded by their change stamp, not read
[I 12:01:34.440] [early:kiln-eml.dll] [eml] Reading the game's reflection types from enshrouded_server.exe (21.7 MiB), once per game version
[I 12:01:34.541] [early:kiln-eml.dll] [eml] Backing up the vanilla game data: enshrouded_server.kfc and enshrouded_server.kfc_resources (141.4 MiB) are copied, then read back for the seal
[I 12:01:35.447] [early:kiln-eml.dll] [eml] Game inputs: 14 file(s), 7.5 GiB: reading 3 file(s) (141.4 MiB): changed in the last few seconds (3); 11 file(s) (7.3 GiB) already recorded, not read
[I 12:01:36.125] [early:kiln-eml.dll] [eml] Fingerprints: hashed 565.8 MiB in 23 file(s), recorded 19 file(s) (7.5 GiB unread), reused 33 file(s) (22.0 GiB not read), 465 ms
[I 12:01:36.126] [early:kiln-eml.dll] kiln-eml: 1 EML mod(s) loaded, 1 resource(s) patched in enshrouded_server (1858 ms; hashed 565.8 MiB, reused 33 file(s), recorded 19 file(s) unread)
```
The whole first start took 1.9 s (dev.21: the 7.8 GiB read of the table above, 6.6 s). The state file went `starting`, `checking`,
`prepare`, `checking`, `writing`, `saving`, `done` (seven writes), and the second start read the 141.5 MiB the patch wrote and
the third nothing (`hashed 930 B in 4 file(s), reused 22 file(s) (7.6 GiB not read)`, 107 ms in all).
With `KILN_EML_FULL_HASH=1` (everything read, four times: 30.1 GiB) the same start takes 21 s and says so while it goes
(`eml.json` was rewritten three to four times a second, with the bytes done and to do):
```
[I 12:05:11.483] [early:kiln-eml.dll] [eml] KILN_EML_FULL_HASH is set: every file is read in full at every step
[I 12:05:11.524] [early:kiln-eml.dll] [eml] Game data: 19 file(s), 7.5 GiB: reading 19 file(s) (7.5 GiB): KILN_EML_FULL_HASH is set (19)
[I 12:05:16.623] [early:kiln-eml.dll] [eml] Checking the game data: 63% (4.7 GiB of 7.5 GiB), 944.8 MiB/s, now enshrouded_server_004.dat
[I 12:05:32.340] [early:kiln-eml.dll] [eml] Fingerprints: hashed 30.1 GiB in 75 file(s), reused 0 file(s) (0 B not read), 19665 ms
```

**The progress window under Wine** (`native/core/tests/test-progress-window.sh`: `progress_probe.exe` is the core's
`progress.cpp` with stubs, on a virtual display of its own, nothing reaches the desktop; shots in `$KILN_OUT/progress-shots`):
the window appears 500 ms into a task (the test shortens the two seconds) at 520x112 in the middle of the screen, it is a
no-activate topmost tool window and the focus stays with the other window of the process; a task shorter than the delay never
creates one; `end()` returns with the window gone (0 to 2 ms); a task nobody ends is closed by `close_all`; with
`KILN_UI_DISABLE=1`, for a server, and in a process with no display (no `DISPLAY`, the Wayland driver off) `begin` returns 0 or
the window cannot be created and nothing fails. With `GAMESCOPE_WAYLAND_DISPLAY` set (or `KILN_PROGRESS_FULLSCREEN=1`) the
window covers the 1280x800 screen with the panel in the middle. Not verified: gamescope itself, a real desktop's window manager
(the virtual display has none), and a Steam Deck.

### Lab check for GitHub issue 4 (2026-11-09, lab server under Wine, server build 1287369)
Two good mods and one broken mod in `kiln/eml/mods/`. The state file marks the broken mod `failed` with the stage and
the others `loaded`; the run happens once.
- A `src/mod.lua` that is not UTF-8 passes discovery and fails the entry check 28 ms after kiln-eml starts:
  `entry src/mod.lua: read failed: stream did not contain valid UTF-8; root exists, src exists, src/mod.lua exists`.
- A mod folder removed 4 s into the run (after the entry check, as the Library does in the issue) fails when its
  module is read, 34 s after the mod was loaded. Only that mod is dropped and the run repeats once for the others:
  `module mods.g_vanishing could not be read 34.1s after the mod was loaded: root metadata failed: File not found. (os error 2); root missing`.
  The same with only `src` removed says `directory metadata (src) failed: ...; root exists, src missing`, and a
  `require` of a missing helper says `file metadata failed: ...; root exists, src exists, src/util.lua missing`.
- Not reachable in the lab: a failure at the open stage (Wine refuses an unreadable file at discovery, with
  `Access denied. (os error 5)`, so such a mod is `skipped`).

## 6. Shared startup data writer: ShroudForge and EMF content

There must be one KFC writer per start. The append-only `kiln.eml` table is now v2: v1's slots are unchanged;
`stand_aside(reason)` is added. Callers check header size/version and required slots before dereferencing them.
A missing DLL/export, incompatible table or a reservation after collection closes is unavailable. A v1 reader
requests `get(1)`, checks `version >= 1` and its own prefix size, then calls only `add_mod`, `phase` and `state_json`.
On x64 that prefix is 32 bytes; v2 is 40 bytes with `stand_aside` appended. C++ layout assertions and a Rust reader
that only knows the v1 prefix cover compatibility.

- kiln-eml normally owns patching. Peers can hand it EML-format folders through `add_mod` during early collection.
- ShroudForge retains its own startup writer. Its early export validates the selected runtime pair and reserves
  `kiln.eml` v2 before calling `shroudforge_prepare_startup`. kiln-eml publishes `aside` and does not rewrite SF's
  result. SF's Lua loop starts at early `on_ready`, after preparation, and continues independently of Kiln hooks.
- Without kiln-eml installed, the adapter acquires the same lifetime install lock itself and checks for another
  game from that install before preparing data. A busy or uninspectable install stops this start. The reservation
  remains closed after a preparation failure: a second writer cannot safely assume SF left the files untouched.
- A bundled SF DLL alone does not claim an EML-only legacy installation: hosting needs an enabled SF package or
  previous SF startup state, an active SF profile, or `KILN_SHROUDFORGE=1`.
- When SF owns startup, EML packages must live in SF's configured mods folder. Kiln-only EML profile paths and
  `kiln/eml/mods` are not forwarded into SF; the `aside` outcome explicitly identifies the selected writer. Since
  dev.22 the launcher copies the profile's EML mods into SF's mods folder when the profile makes Kiln host SF (it lists
  an enabled ShroudForge mod), so SF writes them; `eml.json` lists them as `own-loader`.
- An older kiln-eml table cannot coordinate SF's writer; the adapter refuses to run a second writer.
- A self-hosted EML/SF proxy still takes precedence. No second startup writer should be chained.

The core loads every early DLL before calling exports, so a peer can use
`GetModuleHandleW(L"kiln-eml.dll")` and `GetProcAddress("kiln_eml_service")` regardless of filename order.
Later it can request `api->service("kiln.eml", version)`.

## 7. Client checklist (for the user)
The client has its own data files, so a client check must run on a copy. These are steps for the user, who does all
client testing:
1. Deploy Kiln with the `eml` component (or `tools/client/client.sh deploy --real` with a build that has
   `kiln/plugins/kiln-eml.dll`).
2. Put one small EML mod into `<game>/mods/`, for example one that raises max stamina or stack sizes. A mod from
   Nexus is fine.
3. Start the game. kiln.log should show `early plugin kiln-eml.dll` and
   `kiln-eml: 1 EML mod(s) loaded, N resource(s) patched in enshrouded (...)`. On Linux no `WINEDLLOVERRIDES` should
   be needed.
4. In a LOCAL world, check the mod's effect (for example a higher max stamina shown in the character screen).
5. Start again: `nothing changed since the last run` and a fast start.
6. Remove the mod and start: `the game data was restored to vanilla`. Steam's "verify files" should find nothing
   (EML's `.kfc.bak` may remain; it is not a game file).
7. Optional: install EML's own `dbghelp.dll` with the Wine override. kiln.log should say `standing aside`, and the mod
   still works (EML applied it).
8. Library guard (GitHub issue 4): start the game with the EML mod in the active profile and, while it loads, try to
   remove that mod's version on the launcher's Library page. The launcher must refuse with "Close Enshrouded first: the
   game may be reading this mod", with or without "force". Close the game: removal works. "Clean up" must not list a
   version that the game's `kiln/profile.json` still points at.

9. Start cost on the real install (GitHub issue bisdat/kiln-mods#17; dev.22 changed it): the first start after this Kiln
   update should not read the game folder (about 48 GiB) any more. kiln.log shows `[eml] Game data: 66 file(s), 48.x GiB:
   ... recorded by their change stamp, not read`, and `[eml] Fingerprints: hashed ... GiB` of about 1 GiB, mostly files the
   run wrote or copied itself. The start after a patch run reads the 224 MiB the patch wrote, and every start after that
   should show `hashed` in KiB and `kiln-eml: ... nothing changed ...` within a second or two. If kiln.log says `Change
   stamps are not trusted here`, it names the reason, and the start reads everything every time, as before (the progress
   lines then show it going). Only the real install shows the real numbers (nothing above was measured on one).
10. What a long start shows (dev.22): while kiln-eml works for more than two seconds, a small window "Preparing the game
    data for EML mods" with a bar appears (on the Steam Deck in game mode it covers the screen with the panel in the
    middle), the launcher's Play page says `Preparing the game data for EML mods: ...`, and `kiln/state/eml.json` says
    `"phase": "running"`. To see it on purpose: set `KILN_EML_FULL_HASH=1` and start (it reads all 48 GiB, a minute or
    more, with a line in kiln.log every five seconds). The window must not take the keyboard focus, and be gone when the game's
    own window appears. Gamescope and a Steam Deck are not available to the agents.

Mods we need from the user (Nexus downloads, which agents can't fetch):
- 2-3 popular EML mods: one stat or balancing mod, one with `create_resource`/`create_content` (new items), one with
  several Lua modules. They are for the lab regression set.
- One mod that has both an EML package and EMF parts.

## 8. Open points
- The profile kind `eml` in `schema/profile.schema.json` and the launcher (detect / coordinator). kiln-eml already
  reads `mods.eml`.
- Library detection: `kiln-manage/src/engines.rs` treats a `mod.json` with `capabilities` as a ShroudForge package.
  Real EML mods have `"capabilities": ["patch"]`, so they show as ShroudForge (for detect).
- The launcher now reads `state/eml.json` on Logs > Engines. Its shared-backup notice is read-only: direct
  `.kfc.bak` copies are unsafe. Updated kiln-eml restores only output proven by exact receipts, or once by the change
  cache of dev.20 to dev.23, and otherwise says why it left the data (see "It fails soft").
- `kiln/eml/mods` drop-ins are still not forwarded into SF when SF owns startup (profile EML mods are, by placement
  since dev.22).
