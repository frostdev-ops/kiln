# kiln.hooks — detours, mid hooks, memory, patches (Forge's lowest tier)

`kiln.hooks` is Kiln's Mixin / coremod / access-transformer layer: it lets an **advanced** mod hook and patch
arbitrary game code safely. It is an in-core service (`native/core/src/services/hooks*.cpp`, header
`native/core/abi/services/kiln_hooks.h`), so it is always present when hooks are enabled (verified build, or policy
`any`); nothing is added to `KilnApi`. Managed mods use it through `Kiln.Hooks` (`managed/services/Kiln.Hooks/`).

## The gate
Hooking and patching game code is powerful and build-specific, so only a mod whose `kiln.mod.json` has
`"advanced": true` may use any of it. The first hooking call from a non-advanced mod throws
`InvalidOperationException`. (`Kiln.Hooks` reads the flag straight from `ModInfo.Directory/kiln.mod.json` until the
loader exposes it on `ModInfo`.) Every hook, patch and symbol load a mod installs is logged to `kiln.log` with the
mod id.

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
`r.XmmD(n)` / `r.Xmm(n)` for vector registers.

```csharp
Hooks.Mid("GetEntityTransform", (ref Registers r) => Log.Info($"entity arg r8 = {r.R8}"));
```

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
unpatch`, `detour/mid/call_original/revoke/revoke_owner`, `stats`; v2 adds `raw_detour/caller/detour2/call_original2/mid2/map_json`. Detour handler:
`uintptr_t(KilnHookCall*, void*)`; mid handler: `void(KilnRegisters*, void*)` (`KilnRegisters` mirrors safetyhook's
Context64). See the header for the full contract.
