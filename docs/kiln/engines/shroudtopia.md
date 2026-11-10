# Shroudtopia mods on Kiln (kiln-shroudtopia)

Kiln loads unmodified mods for **Shroudtopia** (MIT, [s0t7x/shroudtopia](https://github.com/s0t7x/shroudtopia) 0.1.1,
and its Enshrouded 1.0 compatibility release r1) through `kiln/plugins/kiln-shroudtopia.dll`: C++ DLLs that export
`CreateModInstance` and implement Shroudtopia's `Mod` class. You do not need Shroudtopia's `winmm.dll` or
`shroudtopia.dll`. Shroudweave (a fork with no public source) uses the same mod format as far as is known; its mods
are untested.

## Install a mod

Put the DLL where Shroudtopia would: `<game>/mods/<mod>.dll`, or a folder `<game>/mods/<name>/` with a `mod.json`
and the DLL (the 1.0 release's layout). Or list it in a Kiln profile (`mods.shroudtopia`, a folder or a DLL).

**Settings** live in `<configDir>/shroudtopia/shroudtopia.json` (`configDir` is `kiln/config` or the profile's). The
first start copies `<game>/shroudtopia.json` there when it exists. The format is Shroudtopia's:

```json
{
  "active": true, "bootDelay": 3000, "updateDelay": 500, "enableLogging": true, "logLevel": "INFO",
  "mods": { "Minimap and Weather Clock": { "active": true } }
}
```

A mod's key in `mods` is the name it reports (`GetMetaData().name`). `active` switches it on and off while the game
runs: the file is read again when it changes. A folder mod's `mod.json` block `"shroudtopia": {"default": {...}}` is
written as `mods.<id>` the first time that id is seen.

`kiln/logs/kiln.log` shows `[shroudtopia] Registered mod: ...`, `Loading mod: ...`, `Activating: ...`, and each mod's
own lines as `[shroudtopia:<mod name>] ...` (also in `kiln/logs/shroudtopia.log`). `kiln/state/shroudtopia.json`
lists the mods, their status, update counts, and the DLLs it refused.

## How mods run

As under Shroudtopia's loader: one loader thread waits `bootDelay` ms, then every `updateDelay` ms reloads the
settings when they changed and, while `active`, looks for new mod DLLs (a DLL dropped in while the game runs is
picked up), calls `Load` once for each mod that supports this side (`hasClientSupport` / `hasServerSupport`), then
`Activate` / `Update` / `Deactivate` by the mod's `active` setting.

Differences, all on the safe side:
- A DLL is loaded only when its export table (read from the file) has `CreateModInstance`. Shroudtopia 1.0 loads any
  DLL in a `mods/<name>/` folder, which in a shared `mods/` folder also runs other loaders' DLLs (Flame, EML packages);
  Kiln never does. Folders with `flame.json` or `src/mod.lua` are skipped.
- DLL initialization, the factory, metadata and loader-invoked lifecycle calls are fault-guarded: a mod that faults or throws is switched off until the next start, the game
  runs on. Mod-owned threads and direct detours are outside those guards.

## ABI

Shroudtopia's API is C++: `std::string`, `std::vector` and `std::function` cross between the loader and the mod. Kiln
builds the plugin against Shroudtopia's unchanged header with the MSVC standard library in release layout, the same
as Shroudtopia's own Visual Studio builds. Mods built with the DLL runtime (`/MD`, as Shroudtopia's projects are) work
with the plugin's static runtime, because every Universal CRT copy allocates from the process heap; the host test
covers exactly that pairing.

## API coverage

21 functions in `ModContext` (10 config, 10 memory, `Log`), its data fields, and the `Mod` lifecycle. **All are
implemented.**

| Shroudtopia API | Kiln implementation | Kiln C# equivalent | Status / tested |
|---|---|---|---|
| `shroudtopia.version` / `config_file` / `log_file` / `mod_folder` | `0.1.1`, Kiln's settings file, `kiln/logs/shroudtopia.log`, `<game>/mods` | - | full; host `version`, `config_file` |
| `game.version` / `isClient` / `isServer` | the exe's sha256 (Shroudtopia passes a placeholder), Kiln's side | `ModInfo.Side` | full; host `side` |
| `config.GetBool/Int/Float/String/Uint64` | `mods.<key>.<name>` with nlohmann::json conversions (Shroudtopia's library): a missing or wrong-typed value gives the default | Kiln mod config (`<configDir>/<id>.json`) | full; host `get_*` (incl. default and wrong type); lab: all 12 config checks |
| `config.Get*Array` (5) | same, for JSON arrays | same | full; host `get_*_array` |
| `memory.FindPattern(pattern, mask, start, length)` | committed readable regions from `start`, first match strictly within the requested range (including readable-region boundaries), `?` in mask = any byte | `Symbols.Find`, **`OriginalCode.FindUnique`** | full; host `find_pattern`; lab: it scans live memory, so a function whose first bytes Kiln's chat service had hooked was (correctly) not found by those bytes |
| `memory.Read` / `Write` / `WriteToReadOnlyMemory` | own bounded, guarded copies; writes restore each affected region's original protection even on failure | `Memory.Read/Write` | full; host `read`, `write`, `write_ro`; lab `read` |
| `memory.ApplyPatch` | own patch snapshot (restored after the loader worker stops on revoke; trampolines remain mapped) | `Memory.Patch` | full; host `apply_patch` |
| `memory.WriteJump(target, dest)` | `jmp rel32`; refused (logged) when out of reach | `Memory.Patch` | full; host `write_jump` |
| `memory.AllocateMemoryNearAddress` | a free block within rel32 reach | - | full (Shroudtopia's version may miss reach); host `alloc_near` |
| `memory.GetModuleBaseAddress(name)` | first module whose path contains `name`, case-insensitive | `Symbols.ImageBase` | full; host `module_base`; lab `lab_module_base` |
| `memory.InjectShellcode` | copies the code into a near block and keeps it | - | full (Shroudtopia's version overwrites the code with NOPs on return); host `inject_shellcode` |
| `memory.CreateDetour(target, detour, &trampoline)` | a [kiln.hooks v2](../services/hooks.md) raw link owned by `shroudtopia:<mod>` (16 readable bytes required at the target; it runs before Kiln's own links on that function, a later detour before an earlier one, also one placed by an EMF mod); the trampoline is the link's continuation (the rest of the chain, then the game's function), published before the link can run. A target outside the game exe, or any function once kiln.hooks has no free target slot (512 hooked functions; logged once), gets a plain safetyhook detour | `Hooks.Detour` | full (Shroudtopia's version is a stub that returns true); host `create_detour`, `detour_runs`, `hooks_raw_links` |
| `Log(msg)` | `[shroudtopia:<mod>]` + `shroudtopia.log` (the mod the loader is driving, as Shroudtopia attributes it) | `ModContext.Log` | full; host, lab |
| `registeredMods` / `loadedMods` | filled as Shroudtopia does (DLL path; loaded set) | - | full |
| `CreateModInstance`, `GetMetaData`, `Load`, `Activate`, `Update`, `Deactivate` | Shroudtopia's loop on Kiln's loader thread | `Mod.OnLoad`, tick events | full; host lifecycle (`activated`, `third update`, `deactivated` after a settings change), client-only mod skipped on a server |
| `Unload`, `~Mod` | never called, as in Shroudtopia | `Mod.OnUnload` | full (same behaviour) |

No Kiln C# addition was needed: Kiln's own config and hook APIs already cover these.

Settings reload compares file contents too, so a replacement preserving the timestamp still takes effect. Invalid
JSON or a non-object replacement retains the last valid settings. Side support is checked before `Load`; a skipped
instance is never activated or updated. The global `active` setting gates discovery and updates, as upstream does.

Renamed real proxies in the game folder and `kiln/chain/` are detected. Detection is repeated during boot delay and
before dispatch; see the [load-order limits](README.md#when-the-other-loader-is-installed-too). Plugin revoke stops
the worker, with a bounded wait for a blocked foreign call. Successful near allocations, shellcode blocks, inline
hooks, mod DLLs and the compatibility runtime remain mapped because foreign threads can retain them.

## Manual test

Shroudtopia mods are mostly client mods (minimap, first-person view); `basics` and `whitelist` run on servers too.
1. Put "Minimap and Weather Clock" (Shroudweave's or Shroudtopia's DLL) in `<game>/mods/`. Leave out `winmm.dll` and
   `shroudtopia.dll`. Start the game through Kiln, load a world.
2. `kiln.log`: `Registered mod: <name>`, `Loading mod: <name>`, `Activating: <name>` (if its `active` is true in
   `kiln/config/shroudtopia/shroudtopia.json`; set it and wait half a second).
3. The minimap and clock show in game. Set `active` to false: `Deactivating: <name>` and it disappears.

Mod files needed: Minimap and Weather Clock (Shroudweave, Nexus 216), the elxokker minimap, Shroudtopia's basics,
flight and first-person-view builds, and Shroudtopia 1.0 r1 itself to compare behaviour.
