# kiln.hooks — detours, mid hooks, memory, patches (Forge's lowest tier)

`kiln.hooks` is Kiln's Mixin / coremod / access-transformer layer: it lets an **advanced** mod hook and patch
arbitrary game code safely. It is an in-core service (`native/core/src/services/hooks*.cpp`, header
`native/core/abi/services/kiln_hooks.h`), so it is always present when hooks are enabled (verified build, or policy
`any`); nothing is added to `KilnApi`. Managed mods use it through `Kiln.Hooks` (`managed/services/Kiln.Hooks/`).

## The gate
Hooking and patching game code is powerful and build-specific, so only a mod whose `kiln.mod.json` has
`"advanced": true` (or `"native": true`, its newer name since 0.2.0-dev.32: the Native tier of
[modding/tiers.md](modding/tiers.md)) may use any of it. The flag is `ModInfo.Advanced`, which the loader fills from the manifest. A call
from a non-advanced mod throws `InvalidOperationException`, and so does a call from a thread with no current mod (a
background thread, a raw native job handler), even for an advanced mod: read what such a thread needs in `OnLoad`. A
mod that has unloaded cannot install hooks. Every hook, patch and symbol load a mod installs is logged to `kiln.log`
with the mod id.

Behind the same gate: `Hooks.Detour`/`Mid`, `Hooks.Module`/`TryModule`/`FindInModule`, `Memory.*`, `Symbols.*`, `Constants.Get`, `OriginalCode.FindUnique`,
`TryFindUnique` and `Read`, `Keep.Block` and `Keep.Span<T>` (Kiln.Hooks); `Abilities.Programs.Register` and
`NativeCall.Bytes` (Kiln.Abilities); `Arsenals.JobHandler`, `Is.Job` and `Act.Job` (Kiln.Creatures). Kiln's own code
calls internal paths behind them, so an ordinary mod that uses buffs or typed natives is never refused. An
`[UnmanagedCallersOnly]` callback must catch the gate's exception itself.

```json
{ "id": "mycoremod", "name": "...", "version": "1.0.0", "apiVersion": "0.1", "entryAssembly": "MyCoremod.dll",
  "side": "server", "advanced": true }
```

## Detours (inline hooks with a callable original and a priority chain)
`Hooks.Detour(symbolOrAddress, handler, priority = 0)` adds a link to the target's one inline detour. Several handlers on one
target form a **priority chain** (highest first; a Mixin `@Inject` head/tail with cancellation). A handler sees the
first four integer/pointer arguments (rcx, rdx, r8, r9), may change them, then either:

- `return call.CallOriginal();` — run the rest of the chain and the real function (optionally after changing args), or
- `return someValue;` — **replace** the function and cancel the rest of the chain and the original.

```csharp
// Count calls and forward to the original.
var h = Hooks.Detour("GetComponent", call => { Interlocked.Increment(ref _n); return call.CallOriginal(); });
...
h.Remove();   // or it is revoked automatically when the mod unloads
```

A managed handler sees the first four integer/pointer arguments. Since dev.21 the original receives **every**
argument the game passed (the rest unchanged), so a target with more than four integer arguments works; you just
cannot read or change the fifth and later. Floating-point arguments, or reading all of them, need a **mid hook** or a
native plugin (see [services/hooks.md](services/hooks.md): raw links take any signature, `detour2` up to 12 integers).

## Mid hooks (full register context)
`Hooks.Mid(symbolOrAddress, (ref Registers r) => …, priority = 0)` adds a mid link (it may share the address with detours). The handler reads
and writes every register in place (writes take effect when the game resumes); `r.Rax`, `r.Rcx`, …, `r.Rip`, and
`r.XmmD(n)` / `r.Xmm(n)` for vector registers. `r.Rsp` is read-only. `r.TrampolineRsp` follows safetyhook's rule: it
starts at `Rsp - 8` and the game resumes at the address on top of that stack, so a mid hook on a function's first
instruction returns early with `r.Rax = value; r.TrampolineRsp = r.Rsp;`.

```csharp
Hooks.Mid("GetEntityTransform", (ref Registers r) => Log.Info($"entity arg r8 = {r.R8}"));
```

## Hooks inside another mod's DLL (since 0.2.0-dev.29.19)
Every hook goes into the game's own code unless you say otherwise: an address anywhere else (another mod's DLL, a
loader's DLL) is refused with "invalid argument". To hook inside another loaded module, open it first:

```csharp
// Creative Mode's creative.dll (a Flame mod that kiln-flame loads): open it, then hook by offset or by pattern
var creative = Hooks.Module("creative.dll");             // KeyNotFoundException while it is not loaded
Hooks.Mid(creative.At(0x15F5F7), (ref Registers r) => { /* ... */ });
var at = creative.Find("48 8B ?? ?? 41 B8");            // survives an update of the DLL that moves its code
```

- `Hooks.Module(name)` finds the loaded module by its file name (any case) and never loads it: when the DLL loads
  later than your mod, use `Hooks.TryModule(name, out var m)` and try again from a later tick. `Hooks.FindInModule(name,
  pattern)` is `Hooks.Module(name).Find(pattern)`.
- After that, addresses inside the module's **executable code** are hook targets for `Hooks.Detour` and `Hooks.Mid`
  (and the native kinds: `detour`, `mid`, `raw_detour`, `detour2`, `mid2`, `mid2_checked`), exactly like the game's
  code. Its headers, data and anything past its image stay refused. `HookModule.At(offset)` turns an offset from the
  module's base (the RVA a disassembler shows for the DLL) into an address and checks it is in the module's code.
- `HookModule.Find(pattern)` / `FindAll` search the module's code with sigdb pattern syntax. They read the live bytes
  (unlike `Symbols.Find`, which reads the exe file), so another engine's hook in that DLL can hide a match. A pattern
  keeps working when the DLL is updated and its code moves; a fixed offset does not.
- Kiln **pins** the module: it stays loaded until the game exits, even if its loader frees it, because a detour's
  bytes and trampoline must never outlive the code they were written into (Kiln never removes a physical detour).
  kiln-flame never unloads a Flame mod, so for Creative Mode nothing changes. Kiln's own modules (`kiln-core.dll`,
  `kiln-*.dll`) are not opened: use their APIs.
- Typed hooks (`Kiln.Sigs`) stay the game's: their descriptor is checked against the game's code, so one inside
  another module is refused with a reason.
- kiln.log names the module once ("hooks: creative.dll opened for hooks by mod:..., pinned") and every link in it as
  `creative.dll+0x15f5f7 [mods/creative/creative.dll]` (the module's file in brackets, as in the overlap lines); `state/hooks.json` lists such a target with `"module"`, `"offset"` and `"rva": null`.
- The capability row is `hooks.foreign_modules` (off on a Kiln runtime older than 0.2.0-dev.29.19, where
  `Hooks.Module` throws `KilnUnavailableException`). Check it, or catch that exception and `MissingMethodException`,
  when your mod should also load on an older Kiln.

**For XHL-Creative-Mode-Fix.** Its search patch called `Hooks.Mid(creativeBase + 0x15F5F7, ...)` with an absolute
address inside `creative.dll`, which Kiln refused. Use `Hooks.Mid(Hooks.Module("creative.dll").At(0x15F5F7), ...)`
instead, or better a pattern: `Hooks.Mid(Hooks.FindInModule("creative.dll", "<bytes at that instruction>"), ...)`, so
a Creative Mode update that moves the code does not break the search patch.

## Memory and patches (access transformers)
- `Memory.Read<T>(addr)` / `TryRead` / `ReadBytes(addr, n)` — SEH-guarded reads (a bad address throws, never crashes).
- `Memory.Write<T>(addr, value)` — SEH-guarded write (not reversible).
- `Memory.Patch(addr, bytes…)` — overwrites bytes, remembering the originals; **restored automatically** when the
  handle is removed or the mod unloads. The canonical access transformer.

## Symbols and scanning
- `Symbols.Get(name)` / `TryGet` — a sigdb symbol's absolute address (core names, or `<modid>/name` from a shipped
  fragment). `Symbols.Find(pattern)` / `FindAll` — scan the image with sigdb pattern syntax (holes act as wildcards).
  `Symbols.Hole(pattern, match, hole)` — read a captured hole (rel32 gives the absolute target). `Symbols.ImageBase`.
  Like sigdb, `Find` and `Hole` match the exe file's code and return live addresses, so another engine's hooks don't
  hide a match ([sigport.md](sigport.md#which-bytes-the-core-matches-the-exe-file-not-the-live-code)).
- `Constants.Get(name)` — a sigdb layout constant.

## Mod-shipped symbols (coremods that survive game updates)
A coremod ships `sigdb/<side>.json` (`server.json` on a dedicated server, `client.json` on the client) with every
name prefixed `<modid>/`. `Symbols.LoadShipped()` (call once in `OnLoad`) resolves it against the running exe with
Kiln's own rules (typed holes, votes, uniqueness), so the coremod's addresses are found by pattern on each build
exactly as Kiln's services are — no hard-coded offsets.

```csharp
public override void OnLoad()
{
    Symbols.LoadShipped();                       // resolves mycoremod/* against this build
    Hooks.Detour("mycoremod/DoThing", OnDoThing);
}
```

## Typed hooks (since 0.2.0-dev.29)
`Hooks.Detour` sees the first four integer arguments. For floats, stack arguments and struct returns, use the typed
classes of `Kiln.Sigs` (`KilnServices` `Hooks;Sigs`): `Kiln.Sigs.Time.SetTimeScale.Detour((ref SetTimeScale.Call c) =>
...)` and `...Invoke(...)`, generated from sigdb descriptors that the core checks against the function's code at load.
A mod's own fragment can describe its own functions. `Detour` returns a `HookResult`: asked for during a step, the hook
goes in at the next safe tick (`Pending`, then the handle's `State` says `Installed` or `Refused` with its `Reason`);
the first detour on a function is written only when Kiln could check every thread of the game, and it is tried at
three safe ticks before it is refused. See [services/hooks.md](services/hooks.md#typed-hooks-and-calls-detour_typed-kilnsigs-since-020-dev29).

## Threads and the open original
- A detour/mid handler runs on **whatever thread the target runs on** — often the tick thread, but the game's job
  threads too. Keep handlers short and thread-safe; do not call tick-thread-only APIs (most of `World`, `Items`, …)
  from them.
- The first hook on a target stops the other threads for a few milliseconds while Kiln writes the jump
  ([details](services/hooks.md#threads)); do it on the tick thread or in `OnLoad`. Later hooks on the same target stop
  nothing.
- The inline detour itself is **never uninstalled** while the process runs; revoking a mod removes its links from the
  chain atomically (a dead mod is never called), and when none remain the call goes straight to the original.

## Per-call overhead (measured, lab, Wine)
`GetComponent` is called about 400k times per step. With the v2 registry (dev.21 lab, `hooks-test` mod): about **6 ns**
per call through a target whose chain is empty (the entry stub, one jump, the trampoline) and about **59 ns** through a
managed handler, so roughly **53 ns** of managed dispatch (reverse P/Invoke into `Kiln.Hooks`, a dictionary lookup, the
delegate). dev.20's v1 path measured about 65 ns and 100 ns. Fine for functions called a few hundred thousand times a
step; a hot handler should still do as little as possible.

## Safety model (collectible contexts)
Native dispatch routes through a fixed `[UnmanagedCallersOnly]` dispatcher in `Kiln.Hooks` (the default, non-
collectible load context — so the function pointer handed to native lives for the whole process) and looks the mod's
handler up by id. A mod's own delegates live in its collectible context and are reached only by that lookup; on
unload Kiln clears the lookup, tells the core to drop the mod's handlers, and **waits for any in-flight handler to
return** before the context is torn down, so the runtime never has to suspend a game thread that is running code in
the context being unloaded.

**Known limitation (Wine).** When two mods both hook the *same* very hot function (e.g. `GetComponent`, ~400k
calls/step across job threads) and one of them hot-reloads, the collectible-context unload can stall under Wine while
the other mod's handler keeps the game threads bouncing through reverse P/Invoke. Reloading a mod whose hooks are the
only ones on the hot path is fine (verified: 100 reloads, 0 failures). Prefer not to pile several mods' hot-path
detours on one function, or quiesce before a hot reload.

## v2 (dev.21): one registry
Hooks placed through `kiln.hooks` (managed `Hooks.*`, Kiln's services and core tick, kiln-emf, kiln-set, EMF mods'
`hook` slot, Shroudtopia and a hosted ShroudForge's engine hooks) go through one registry that owns a single physical detour per game address, with ordering (`before` /
`after` / priority), the game's call site (`caller()`), detection of other modules' hooks and a map of all hooks in
`state/hooks.json`. Native code uses it through `kiln_hook.hpp`. The full description is
[services/hooks.md](services/hooks.md). Managed `Hooks.Detour` / `Hooks.Mid` are links in that chain at their
priority and need no change.

## C table (`kiln_hooks.h`, v1 + v2)
`image_base/image_size`, `symbol/constant`, `scan/scan_hole`, `load_symbols/unload_symbols`, `read/write/patch/
unpatch`, `detour/mid/call_original/revoke/revoke_owner`, `stats`; v2 adds `raw_detour/caller/detour2/call_original2/mid2/map_json`;
0.2.0-dev.29.19 adds `open_module/module_scan` (hooks inside another module). Detour handler:
`uintptr_t(KilnHookCall*, void*)`; mid handler: `void(KilnRegisters*, void*)` (`KilnRegisters` mirrors safetyhook's
Context64). See the header for the full contract.
