# Greeneye Loader mods on Kiln (kiln-greeneye)

Kiln loads unmodified mods for **Greeneye Loader** 1.2.2 (MIT, [Greeneye0/greeneye-loader-enshrouded](https://github.com/Greeneye0/greeneye-loader-enshrouded))
through `kiln/plugins/kiln-greeneye.dll`: The Worst Map Mod, Find, Quick Deposit, Town Portals, Embervale Teleport,
Hollow Halls Progression. You do not need Greeneye's `dbgcore.dll`. Greeneye Loader does not start under Wine or
Proton; on Kiln its mods do.

## Install a mod

Put the mod where Greeneye Loader would put it, `<game>/greeneye/<id>/` with its `mod.json` and DLL, or add it to a
Kiln profile (`mods.greeneye`, see [README.md](README.md)). Greeneye mods keep their settings in their own folder (an
ini next to the DLL), as under Greeneye Loader.

`kiln/logs/kiln.log` has one line per mod, for example
`[greeneye] Find 1.4 (folder): started (active generation 1 (0 ms))`. Each mod's own log lines appear as
`[greeneye:<id>] ...`, and in `kiln/logs/greeneye-loader.log` (the file Greeneye's bug reports ask for).
`kiln/state/greeneye.json` lists every mod with its status, generation and hook count.

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
without running it (Greeneye Loader loads it, then finds no entry).

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
starts first runs the mods, the other stays idle.

## API coverage

GreeneyeHost API 2 has 9 functions, 8 data fields and 2 entry points a mod exports. **All 19 are implemented.** The
hook owner (hooks, leases, generations, reloads) is Greeneye Loader's own code, ported (`native/plugins/greeneye/src/hot.cpp`),
so Greeneye mods chain onto each other, onto Creative Mode's hooks and onto Kiln's own detours as they do under
Greeneye Loader. "Tested" names the check in `native/plugins/compat/tests/run.py` (host) or the lab run.

| Greeneye API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `size`, `apiVersion` (2) | filled as Greeneye Loader 1.2.2 | - | full; host `api_version` |
| `side` (1 client, 2 server) | from Kiln's side | `ModInfo.Side` | full; host `side=1`, `side=2`; lab `side=2` |
| `modDir`, `gameDir` (trailing `\`) | the mod's folder (profile or `greeneye/`), the game folder | `ModInfo.Directory` | full; host `mod_dir_slash`, `game_dir_slash` |
| `log(line)` | `[greeneye:<id>]` in kiln.log + `greeneye-loader.log` (one log function per mod, so lines carry the mod's id) | `ModContext.Log` | full; host, lab |
| `generation`, `hotReload`, `token` | ported hook owner | Kiln's own mod reload | full; host `generation=2`, `hot_reload_flag` |
| `enter(token)` / `leave()` | ported: per-mod shared gate, nestable per thread; active only for the active generation | `HookHandle` removal waits for running handlers | full; host `lease_inactive_while_starting`, detour +1000 runs under an active lease |
| `find(pattern, module)` | ported: unique match in the exe's `.text` as it was before the loader's hooks, also behind another mod's hook jump; `module` must be null | **new** `OriginalCode.FindUnique` / `TryFindUnique` (reads the exe file: before every hook) | full; host `find`, `find_wildcard`, `find_ambiguous`, `find_other_module_null`, `find_after_hook`; lab: found the game's chat-log function behind Kiln's own detour |
| `read(p, out, n)` | ported: live bytes with every Greeneye slot's saved bytes laid back (another loader's jump stays visible, as upstream) | **new** `OriginalCode.Read` (the file: before every hook) | full; host `read_before_hooks`, `read_original`; lab `lab_read` |
| `hook(target, detour, len, prologue, n)` | ported: 12..32 bytes, `mov rax; jmp rax` to a thunk, trampoline runs the original or the given prologue; chains onto an existing `mov rax`, `jmp rel32` or `jmp [rip]` | `Hooks.Detour` (Kiln's detour chain) | full; host `hook` (+1000), `short_hook_refused` declines the start |
| `redirectCall(call, detour)` | ported: points one `call rel32` at a thunk near it | `Hooks.Mid` at the call site | full; host `redirect_call` (+100) |
| `poll()` | ported safe point | - | full; host `poll_returns` |
| `keep(key, size)` | ported: zeroed block per key for the process life (shared across mods, as upstream) | **new** `Keep.Block` / `Keep.Span<T>` | full; host `keep_same_block` (zeroed, then the same block after a reload) |
| `GreeneyeModStart(host)` export | called once per generation under a fault guard | `Mod.OnLoad` | full; host, lab |
| `GreeneyeModHot()` export: `prepare`, `ready`, `resume` | ported reload protocol | - | full; reload path tested with a mod that does not export it |

## Kiln C# additions

In `Kiln.Hooks` (`managed/services/Kiln.Hooks/OriginalCode.cs`), for any Kiln mod:
- `OriginalCode.FindUnique(pattern)` / `TryFindUnique`: the single match of a byte pattern in the game's code as it is
  in the exe file, so a function another mod has hooked is still found by its own bytes.
- `OriginalCode.Read(address, count)`: the bytes a hook replaced.
- `BytePattern.Parse`, `PeCode`: the parts underneath, usable on any PE file.
- `Keep.Block(key, size)` / `Keep.Span<T>`: native memory that survives a mod's reload.

## Manual test (real client)

Greeneye mods are client mods. With Kiln installed in the client and no Greeneye `dbgcore.dll`:
1. Put The Worst Map Mod in `<game>/greeneye/<its id>/` (its `mod.json` and DLL, from its download). Start the game
   through Kiln and load a world.
2. `kiln.log` shows `[greeneye] kiln-greeneye 1.0.0 (GreeneyeHost API 2, as Greeneye Loader 1.2.2) on the client` and
   `... (folder): started (active generation 1 ...)`. No `CHECK`/`fail` lines, no fault.
3. In the game, the mod's own feature works (its map behaviour), exactly as under Greeneye Loader on Windows.
4. Repeat with Find (open its search) and Quick Deposit (deposit into a nearby chest).

Mod files needed for this (not downloadable by Kiln's tests): the Nexus downloads of The Worst Map Mod, Find, Quick
Deposit, Town Portals, Embervale Teleport and Hollow Halls Progression.
