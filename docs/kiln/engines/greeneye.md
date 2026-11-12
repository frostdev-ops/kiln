# Greeneye Loader mods on Kiln (kiln-greeneye)

Kiln loads unmodified mods for **Greeneye Loader** 1.2.2 (MIT, [Greeneye0/greeneye-loader-enshrouded](https://github.com/Greeneye0/greeneye-loader-enshrouded))
through `kiln/plugins/kiln-greeneye.dll`: The Worst Map Mod, Find, Quick Deposit, Town Portals, Embervale Teleport,
Hollow Halls Progression. You do not need Greeneye's `dbgcore.dll`. Greeneye Loader does not start under Wine or
Proton; on Kiln its mods do.

## Install a mod

Add the mod's zip to the Kiln Launcher's Library (a release that ships Greeneye's `dbgcore.dll` too is fine: the loader
is left out, Kiln does not need it) and switch it on in a profile (`mods.greeneye`, see [README.md](README.md)). Or put
the mod where Greeneye Loader would put it, `<game>/greeneye/<id>/` with its `mod.json` and DLL.

**Where a profile's Greeneye mod runs from (since dev.24).** Greeneye's API gives a mod its folder as
`...\greeneye\<id>\` (`modDir`), and mods keep their settings, logs and per-world data there: The Worst Map Mod writes
`minimap_config.ini`, `looted_chests.ini`, `defeated_bosses.ini`, `unlock_map_markers.log` and
`UnlockMapMarkers.status` next to its DLL, and its page tells players to edit `greeneye\unlock-map-markers\minimap_config.ini`.
So activation copies each enabled Greeneye mod of the profile to `<game>/greeneye/<library id>/` (Kiln's copy: marker
`.kiln-library.json`, receipt `kiln/state/library-placements.json`) and `profile.json` names that copy, with or without
Greeneye's own `dbgcore.dll`. kiln-greeneye and Greeneye Loader then run the same folder. What a mod writes there is the
player's: it stays when a new version replaces the copy, and waits in `kiln/kept-mod-files/greeneye/<id>/` while the
mod is switched off, after "No profile" and after uninstalling Kiln (Greeneye Loader, and kiln-greeneye without a
profile, would run anything left in `greeneye/`); a copy whose own files the player changed stays and is reported. A folder the player put in `greeneye/`
under that name, or holding the same mod (the same `mod.json` id), is never touched: the profile then names the Library
entry, as for a play-once start with another version. So does a mod Kiln could not place (an id that is no safe
Windows folder name, a linked `greeneye` folder, a changed or unreadable Library entry, a copy that could not be
published): the launcher shows a warning with the reason (a copy that stays in `greeneye/` is listed as kept), and the
other mods, the profile switch and Play go on. When publishing the copy fails after the files were moved out of the
Library entry (below), they go back into the entry the mod then runs from. The forms `profile.json` gets for a
placed copy: `Z:\...\greeneye\<id>` (Proton with the Z: drive), `C:\...\greeneye\<id>` (Windows), and
`../greeneye/<id>` relative to `<game>/kiln/` (copy mode); otherwise the forms of a Library entry as before (the
Library path, or `profiles/<profile>/greeneye/<id>` in copy mode). Up to dev.23 the profile named the Library entry
(`library/greeneye/<id>/<version>+<hash>/`), so the mod wrote into the Library: settings were lost with every new
version, and a later placement for Greeneye Loader refused the changed entry ("library hash mismatch"). The first
placement moves those files out of the entry into the copy (only when the entry without them is exactly the import,
and never over a file already kept for that mod). In copy mode, with a core that reads `../greeneye/<id>`, no second
copy is kept under `kiln/profiles/`.

`kiln/logs/kiln.log` has one line per mod, for example
`[greeneye] Find 1.4 (folder): started (active generation 1 (0 ms))`. The lines a mod sends through `host->log` appear
as `[greeneye:<id>] ...`, and in `kiln/logs/greeneye-loader.log` (the file Greeneye's bug reports ask for). Many mods
write their own log in their folder instead (The Worst Map Mod's `unlock_map_markers.log` says which code sites it
found and patched): the launcher's support report (Logs > Save report) carries each Greeneye mod's `*.log` and
`*.status` files and Greeneye Loader's `greeneye/loader.log`. `kiln/state/greeneye.json` has the engine's `status`
(`running`, `stepped aside`, `failed`, `idle`), the counts `started` and `failed`, and every mod with its status,
reason, folder, generation and hook count. "started" means `GreeneyeModStart` returned true; what the mod then patched
is in its own log.

## Which mods load

Exactly Greeneye Loader's rules, read from the same `mod.json` fields with the same lenient reader:

| Field | Meaning | Default |
|---|---|---|
| `id` | the mod's id | the folder's name |
| `name`, `version` | shown in the log | the id, `?` |
| `dll` | the DLL to load | `<id>.dll` |
| `side` | `client`, `server` or `both`: a mod for the other side is skipped | `client` |
| `api` | the API the mod needs: above 2 is refused | `1` |
| `enabled` | `false` skips the mod | `true` |

One difference: Kiln reads the DLL's export table from the file first and refuses a DLL without `GreeneyeModStart`
without running it (Greeneye Loader loads it, then finds no entry). The DLL must remain inside its mod folder,
without symlinks or junctions; file and directory handles stabilize validation and loading.

The mods start in `on_ready`, before the game's entry point, the moment Greeneye Loader starts them when it loads as
`dbgcore.dll`.

## Hot reload (mod developers)

As in Greeneye Loader: create `<game>/greeneye/hotreload.enable`, copy a new build over the mod's DLL, then create
`<mod folder>/reload`. Each generation loads from a shadow copy in `<mod folder>/.hot/`; `<mod folder>/hotload.status`
says `active generation N` when the swap happened. Safe points: the top of the game's UI frame (found by Greeneye's
pattern), a mod's `host->poll`, the loader's watcher thread on a dedicated server, and on Kiln also each client-world
step.

## When Greeneye Loader is installed too

See [README.md](README.md#when-the-other-loader-is-installed-too). Greeneye's own claim decides: whichever loader
atomically creates the expected named claim first runs the mods; the other stays idle if it honors the same claim.

## API coverage

GreeneyeHost API 2 has 9 functions, 8 data fields and 2 entry points a mod exports. **All 19 are implemented.** The
hook owner (hooks, leases, generations, reloads) is Greeneye Loader's own code, ported (`native/plugins/greeneye/src/hot.cpp`),
so Greeneye mods chain onto each other, onto Creative Mode's hooks and onto Kiln's own detours as they do under
Greeneye Loader. "Tested" names the check in `native/plugins/compat/tests/run.py` (host) or the lab run.

| Greeneye API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `size`, `apiVersion` (2) | filled as Greeneye Loader 1.2.2 | - | full; host `api_version` |
| `side` (1 client, 2 server) | from Kiln's side | `ModInfo.Side` | full; host `side=1`, `side=2`; lab `side=2` |
| `modDir`, `gameDir` (trailing `\`) | the mod's folder (Kiln's copy in `greeneye\<id>\`, a folder in `greeneye\`, or a Library entry: see Install), the game folder | `ModInfo.Directory` | full; host `mod_dir_slash`, `game_dir_slash` |
| `log(line)` | `[greeneye:<id>]` in kiln.log + `greeneye-loader.log` (one log function per mod, so lines carry the mod's id) | `ModContext.Log` | full; host, lab |
| `generation`, `hotReload`, `token` | ported hook owner | Kiln's own mod reload | full; host `generation=2`, `hot_reload_flag` |
| `enter(token)` / `leave()` | ported: per-mod shared gate, nestable per thread; active only for the active generation | `HookHandle` removal waits for running handlers | full; host `lease_inactive_while_starting`, detour +1000 runs under an active lease |
| `find(pattern, module)` | ported: unique match in the exe's `.text` as it was before the loader's hooks, also behind another mod's hook jump; `module` may be null or the main executable handle | **new** `OriginalCode.FindUnique` / `TryFindUnique` (reads the exe file: before every hook) | full; host `find`, `find_wildcard`, `find_ambiguous`, `find_other_module_null`, `find_after_hook`; lab: found the game's chat-log function behind Kiln's own detour |
| `read(p, out, n)` | ported: live bytes with every Greeneye slot's saved bytes laid back (another loader's jump stays visible, as upstream) | **new** `OriginalCode.Read` (the file: before every hook) | full; host `read_before_hooks`, `read_original`; lab `lab_read` |
| `hook(target, detour, len, prologue, n)` | ported: 12..32 bytes, `mov rax; jmp rax` to a thunk, trampoline runs the original or the given prologue; chains onto an existing `mov rax`, `jmp rel32` or `jmp [rip]` | `Hooks.Detour` (Kiln's detour chain) | full; host `hook` (+1000), `short_hook_refused` declines the start |
| `redirectCall(call, detour)` | ported: points one `call rel32` at a thunk near it | `Hooks.Mid` at the call site | full; host `redirect_call` (+100) |
| `poll()` | ported safe point | - | full; host `poll_returns` |
| `keep(key, size)` | ported: zeroed block per key for the process life (shared across mods, as upstream) | **new** `Keep.Block` / `Keep.Span<T>` | full; host `keep_same_block` (zeroed, then the same block after a reload) |
| `GreeneyeModStart(host)` export | called once per generation under a fault guard | `Mod.OnLoad` | full; host, lab |
| `GreeneyeModHot()` export: `prepare`, `ready`, `resume` | ported reload protocol | - | full; host tests prepare/ready/resume, reentrant own-hook calls, and faulting/invalid hot factories |

## Kiln C# additions

In `Kiln.Hooks` (`managed/services/Kiln.Hooks/OriginalCode.cs`), for advanced Kiln mods only (`"advanced": true` in
`kiln.mod.json`; an ordinary mod gets `InvalidOperationException`). These calls need the calling mod's context: from
`OnLoad` or a Kiln callback. A background thread or a raw job handler is refused even in an advanced mod, so cache the
addresses, bytes or blocks it needs during `OnLoad`, and catch every exception inside a raw unmanaged callback:
- `OriginalCode.FindUnique(pattern)` / `TryFindUnique`: the single match of a byte pattern in the game's code as it is
  in the exe file, so a function another mod has hooked is still found by its own bytes.
- `OriginalCode.Read(address, count)`: the bytes a hook replaced.
- `BytePattern.Parse`, `PeCode`: the parts underneath, with bounded x64 PE32+ parsing, relocation checks and a
  512 MiB code snapshot cap. `OriginalCode` checks the file headers against the mapped executable before returning
  live addresses; this detects different build metadata and does not authenticate the file contents.
- `TryFindUnique` returns false with a reason for unavailable images and malformed patterns.
- `Keep.Block(key, size)` / `Keep.Span<T>`: native memory that survives a mod's reload. Allocation and publication
  are serialized; the first requested logical size owns the key, including zero. Span sizes use checked arithmetic.

Hook publication waits until a generation has finished starting and stored its original trampolines. Swaps wait for
leases and suspend other threads before replacing live instruction bytes. Loader-driven callbacks under a swap
see inactive leases, including callbacks that reenter their own detours. Failed generations retain any host tokens,
modules and trampolines their own threads may still use. The compatibility runtime stays mapped on plugin revoke.
Direct native detours and mod-owned threads cannot be generically fault-wrapped; lifecycle and hot callbacks are guarded.

## Manual test (real client)

Greeneye mods are client mods. The Worst Map Mod (Nexus mod 220) is the first one to check, on native Windows:
1. With Kiln installed and no Greeneye `dbgcore.dll` next to `enshrouded.exe`, add the mod's zip in the launcher
   (Library > Add mods), switch it on in the active profile and press Play.
2. `<game>/greeneye/unlock-map-markers/` holds the mod (and `.kiln-library.json`); `kiln/profile.json` names that
   folder. `kiln.log` shows `[greeneye] kiln-greeneye 1.0.0 (GreeneyeHost API 2, as Greeneye Loader 1.2.2) on the client`
   and `The Worst Map Mod 1.3.x (profile): started (active generation 1 ...)`; `greeneye.json` says `running`.
3. `greeneye/unlock-map-markers/unlock_map_markers.log` has `Resolved ...` for its four patch sites and
   `All 4 map marker unlock patches + MapKeyPanel hooks are ACTIVE.`; `UnlockMapMarkers.status` says `state=active`.
   In a world, the map (M) shows the Map Key & Filters panel and every marker; the HUD minimap shows.
4. Change a setting in the mod's cog menu, quit, update the mod to its next version in the launcher, play: the setting
   is still there (`minimap_config.ini` kept).
5. Put Greeneye's `dbgcore.dll` next to the game and play: `greeneye.json` says `stepped aside`, the mod is
   `own-loader`, `greeneye/loader.log` shows Greeneye Loader starting it, and the map works the same.
6. Repeat 1 to 3 with Find (Shift+F in a base) and Quick Deposit (G G in the inventory).

Mod files needed for this (not downloadable by Kiln's tests): the Nexus downloads of The Worst Map Mod, Find, Quick
Deposit, Town Portals, Embervale Teleport and Hollow Halls Progression.
