# Kiln runtime bundles and the installer

The runtime bundle is how Kiln reaches a game folder outside the repository: the launcher, `kilnctl deploy --bundle`
and `kilnctl client install` all install the same zip with `kiln_manage::install`. Format contract: contracts.md 5.6
and 5.7 (`schema/kiln-runtime.schema.json`, `schema/install-receipt.schema.json`).

## Building one
```sh
tools/package/package.sh [--out dist] [--no-build]      # = stage.sh + kilnctl bundle create (+ verify)
tools/package/stage.sh <stage dir> [--no-build] [--allow-missing]  # build (like deploy-server.sh) and lay the components out
kilnctl bundle create <stage dir> --out dist [--git SHA] [--epoch S]
kilnctl bundle verify dist/kiln-runtime-*.zip
tools/package/parity.sh dist/kiln-runtime-*.zip [--samples]   # bundle install == deploy-server.sh tree (diff -r)
tools/package/fetch-dotnet.sh [--check]                 # the pinned .NET runtime in .cache/dotnet-win-x64/
```
`KILN_OUT` (default `.out/package`) holds the build. The zip is
`kiln-runtime-<VERSION>+<yyyymmdd>.<git>.zip`; `<yyyymmdd>` and every entry time come from the commit time
(`SOURCE_DATE_EPOCH`, default `git log -1 --format=%ct`), `<git>` from `KILN_GIT` / `git rev-parse --short=7 HEAD`.
Entries are sorted bytewise, deflate level 6, mode 0644, Unix "made by", no extra fields: the same staging folder
gives a byte-identical zip (checked: two runs, same sha256). A dirty tree still says the HEAD commit (package.sh warns).

### The staging folder
```
<stage>/kiln-runtime.json          the manifest without files[] (+ "config": ["kiln/kiln.json"])
<stage>/<component>/<game-relative path>
```
`kilnctl bundle create` walks every component folder, hashes the files (sha256, size), marks the `config` ones and
writes `files[]` sorted by path. A path in two components, an unknown component folder, a symlink or an unsafe name
is an error.

### Components (what stage.sh writes)
| Component | Sides | Default | Files |
|---|---|---|---|
| `core` | server, client | yes | `dinput8.dll`, `kiln/kiln-core.{dll,pdb}`, `kiln/services/*.{dll,pdb}`, `kiln/host/Kiln.*`, `kiln/dotnet/**` (pinned .NET 10.0.14 win-x64), `kiln/kiln.json` (config), Kiln's plugins other than kiln-emf |
| `sigdb-server` | server | yes | `kiln/sigdb/server.json`, `server.d/*.json`, `builds/server/*.json` |
| `sigdb-client` | client | yes | `kiln/sigdb/client.json`, `client.d/*.json` (`tools/sigport/sides.py compose client`), `builds/client/*.json` |
| `admin` | server | yes | `kiln/mods/kiln-admin/{Kiln.Admin.dll,.pdb,kiln.mod.json}` |
| `emf` | server, client | yes | `kiln/plugins/kiln-emf.{dll,pdb}` |
| `eml` | server, client | yes | `kiln/plugins/kiln-eml.dll` (GPL-3.0, `gpl/kiln-eml`) + `kiln-eml.LICENSE.txt`, `kiln-eml.NOTICE.txt` |
| `shroudforge` | server, client | yes | `kiln/plugins/shroudforge/**`: ShroudForge's runtime (GPL-3.0, `third_party/shroudforge`), its notice, source pointer and licences |
| `enshwick` | server, client | yes | `kiln/plugins/enshwick/{enshwick.dll,enshwick-version.txt,LICENSE.txt}`: Enshwick's own loader (MIT), built from the pinned tag and commit by `native/plugins/enshwick/tools/build-runtime.sh`; kiln-enshwick runs it when the game has no `mods/enshwick/enshwick.dll` |
| `samples` | server | no | `kiln/mods/hello/`, `kiln/plugins/kiln-native-hello.dll` |

Kiln runs every engine's mods, so every compatibility plugin is installed by default on both sides: the
`native/plugins/*` plugins (in `core`, kiln-emf in `emf`), kiln-eml, the ShroudForge runtime and Enshwick's loader.
stage.sh stops, naming the part, when any of them (or a service, Kiln.Admin, Kiln.Game, the samples) fails to build;
`--allow-missing` (local dev staging only, refused when `KILN_RELEASE_BUILD=1`) warns and leaves it out instead.
`tools/release/lib/plugins.py check` checks that a stage or a runtime zip holds every plugin file in a default
component for both sides; stage.sh runs it, release.sh runs it right after `bundle create` and again in its checks.

The rsync filters are deploy-server.sh's (services `*.dll|*.pdb`, host `Kiln.*`, mod folders without the shared
`Kiln.*`, `*.deps.json`, `*.runtimeconfig.json` except the entry assembly and exports). Other mod folders in
`$KILN_OUT/mods` are not bundled (stage.sh warns). `abi` comes from `KILN_ABI_VERSION`, `api` from
`Kiln.Mods.ApiVersion`, `dotnet` from the runtime folder, `builds` from the verified records in `sigdb/builds/`.

### The .NET runtime
`fetch-dotnet.sh` pins `dotnet-runtime-10.0.14-win-x64.zip` by the sha512 Microsoft publishes in
`builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json`. A cached `runtime.zip` with that hash is used
as it is; otherwise it downloads from `builds.dotnet.microsoft.com` and checks the hash. `root/` is the unpacked zip
(what deploy-server.sh, client.sh and stage.sh copy).

## Installing (kiln_manage::install)
`install(dirs, game, bundle, options, progress)`; the CLI: `kilnctl bundle install`, `kilnctl client install`,
`kilnctl deploy <instance> --bundle`.
1. The folder has the side's exe (`enshrouded.exe` client, `enshrouded_server.exe` server).
2. Refuse while the game runs (`proc::find_game`: Wine comm + argv[0]/cwd, Windows image path) with `game_running`,
   and while Steam updates or validates it (`appmanifest_<app>.acf` StateFlags in the recorded library, in
   `<lib>/steamapps/common/<dir>/../../`, or in the folder for SteamCMD installs) with `steam_updating`.
3. `bundle::verify`: every entry's size and sha256, no entry outside `files[]`, no `..`/absolute/backslash/drive
   names, no symlink entries.
4. Components (`install::resolve_components`), plus what they `require`; the selection must include `dinput8.dll`:
   - `options.components` (the player's explicit choice, `--components`): exactly those. The bundle's defaults for
     the side that it leaves out are recorded as turned off in the receipt's `runtime.deselected`.
   - none, no receipt: the defaults of the side.
   - none, a receipt (update, reinstall, rollback, `deploy --bundle`): the installed components the bundle still has
     for the side, plus every default of the bundle not in `runtime.deselected`. So a component that is new, or
     became a default, goes in on the next update unless the player turned it off; a component the bundle lacks
     (rollback) is left out, not an error.
   - `runtime.deselected` carries over (opt-outs of components a bundle lacks too, so a rollback and the next
     update keep them off). An explicit choice keeps earlier opt-outs of unselected parts even when they are now
     optional or on the other side. Requirements take precedence for the current install; an automatic update
     does not erase the opt-out, so the part stays off again when no longer required. `deploy --bundle --samples`
     adds samples to the automatic selection under the install lock; it does not replace the player's selection.
     A receipt without `deselected` (written before 0.2.0-dev.19) holds a plain list that records no
     choice: nothing counts as turned off, so the defaults it lacks go in. The 0.2.0-dev.18 update froze every
     player's earlier list this way (the launcher and the installer passed the receipt's list back as the choice)
     and skipped eml, shroudforge and enshwick.
5. Stage the planned files into `kiln/.staging-<random>/new/` (hash checked while writing). Stale `.staging-*`
   folders of an interrupted install are deleted first.
6. `dinput8.dll` by `pe::classify_proxy`:

   | Found | Action | `replaced[]` |
   |---|---|---|
   | nothing | - | - |
   | our previous install (receipt sha256) | replaced like every receipt file | - |
   | another Kiln proxy | backed up, replaced | `kiln`, `replaced` |
   | ShroudForge | refused (`refused`) unless `switch_from_shroudforge`; then `dbghelp.dll`, `dinput8.dll`, `winmm.dll` are backed up and moved away, never chained | `shroudforge`/`file`, `backed-up` |
   | EMF's loader / an unknown proxy | backed up, moved to `kiln/chain/<emf\|unknown>-dinput8.dll` (`-2`, `-3` ... when taken by other bytes), `chain.ini [chain]` line added | `emf`/`unknown`, `chained`, `chainPath` |
   | a Wine builtin copied into the folder | backed up and removed, not chained | `file`, `backed-up` |
   | not a 64-bit DLL | refused | - |
7. Every other file. A path the previous receipt lists is ours (a hand-edited one is copied to the backup before it
   is overwritten, but it is no original). Every other path that already exists is recorded: the bundle's exact bytes
   or a config file (`kiln/kiln.json`, installed only when missing) as `adopted` (left in place, no backup), other
   bytes backed up as `replaced`. Backups go to `kiln/backups/install-<yyyymmddThhmmssZ>/<path>`, mirrored to
   `<data>/backups/install/<installId>/`. A Kiln folder deployed by deploy-server.sh or client.sh is thus adopted
   file by file, and uninstall gives it back unchanged.
8. Files move into place (rename from staging; the previous file waits in `staging/previous/`), `dinput8.dll` last.
   Any failure undoes every step in reverse (files, moved proxies, chain lines); the backups stay.
9. Upgrade: an `adopted` file the new bundle changes is backed up first and its record becomes `replaced`. Files of the
   previous receipt that the new bundle no longer has are deleted only when unchanged (empty folders below `kiln/` go
   too); an adopted one stays, a replaced one gets its original back; changed ones stay and are reported.
10. The standard folders (`kiln/{host,mods,plugins,services,logs,control,data}`, `kiln/sigdb/builds/<side>`,
    `kiln/sigdb/<side>.d`) exist, as deploy-server.sh creates them.
11. The receipt `kiln/.install.json`, last and atomic. `installId` = the previous receipt's, else the install's
    path-derived UUID (`game::install_id_for`), so a folder's id does not change; `installed` stays, `updated` is set
    on upgrades, `replaced[]` accumulates (the oldest record per path is the original). Then
    `<data>/installs/<installId>.json` (`game::write_record`).

`status` reports the receipt, the current proxy class, the files changed since (size mismatch, else sha256 cached by
size+mtime per process) and a newer bundle in `<data>/runtime/` for the side.

## Uninstall and rollback
`uninstall` (refused while running/updating): checks first that every original (the oldest `replaced[]` record per
path, `adopted` ones excepted) can come back from its chain file or backup with the recorded sha256; then deletes the
receipt's unchanged non-config files that were not there before (adopted files stay), removes the `[chain]` lines it added, puts the originals back (a displaced proxy is moved back,
others copied from the backup) and verifies each sha256, removes the receipt. `kiln/` stays (mods, configs, data,
logs, backups). `rollback(version)` installs a bundle from `<data>/runtime/` with the player's parts (step 4, no
explicit choice). `status` lists `missingParts`: parts absent from the receipt that reinstalling the installed bundle
(when it is in `<data>/runtime/`) adds: defaults not turned off and transitive requirements, including optional or
turned-off parts required by another selected part. Runtime shows them and offers Reinstall.

## Verified
- Two `bundle create` runs: byte-identical zip (365 files, 205 MB unpacked).
- parity.sh: 344 files equal to deploy-server.sh's tree (348 with samples, admin and emf present).
- `kilnctl deploy mylab --bundle`: a lab boots (`boot complete`, verified build, SimStep hooked,
  kiln-admin loaded, world Run).
- Client copy with EMF's real loader: chain entry, backup, receipt; upgrade (obsolete file gone, edited kiln.json
  kept, same installId); uninstall: byte-identical folder outside `kiln/`. The receipts, `kiln-runtime.json`,
  `profile.json`, native state and generated `native.json` validate against `schema/*.schema.json` (ajv).
- Tests: `crates/kiln-manage/tests/install.rs` (determinism, tamper, zip-slip, EMF/unknown/Kiln/ShroudForge/not-DLL,
  upgrade, uninstall, rollback, steam_updating, game_running, a pre-deployed identical kiln/ tree adopted and given
  back byte-identically, also across an upgrade), Linux and Windows (cargo xwin + Wine).

## The bundle a launcher ships
`tools/launcher/build-{linux,windows}.sh` put one bundle in the app's resources as `runtime/kiln-runtime.zip`: `--runtime ZIP`, or one
built from the tree with `tools/package/package.sh`; `--runtime none` ships none. At start the launcher copies it into
`<data>/runtime/` unless that version is already there (`bundle::import_shipped`), so Play's **Install Kiln** works with no download.
A launcher update with a newer bundle shows **Update** on Runtime; nothing installs by itself. A dropped zip still works, for rollback or a test build.
