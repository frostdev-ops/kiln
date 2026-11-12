# kiln.hooks v2: one registry for every hook

`kiln.hooks` hooks game functions for Kiln itself and for plugins and mods. Since dev.21 every hook Kiln places on the
game exe goes through it: the services, the core tick, kiln-emf, EMF mods' `hook` slot, Shroudtopia's `CreateDetour`
and ShroudForge's engine hooks when Kiln hosts it. Only hooks on system DLL exports (`LoadLibraryExW`, Vulkan, XInput
on the client) and EMF/Shroudtopia hooks on targets outside the game exe still use plain safetyhook (the registry places
its own detours with safetyhook too). For each game address the registry installs **one**
physical detour. Every handler on that address is a **link** in an ordered chain. Links of different kinds, from
different owners, share the detour instead of layering on top of each other.

Why this matters:

- Two Kiln components on one function used to stack two safetyhook detours. A handler that tells call sites apart
  with `_ReturnAddress()` then saw the other detour's address, not the game's. `caller()` fixes that.
- You choose the order. `before` and `after` name other owners.
- Kiln knows every hook. It writes `<game>/kiln/state/hooks.json` and logs when another module hooked first or hooked
  over Kiln.

Header: `native/core/abi/services/kiln_hooks.h` (C table, v1 functions unchanged). C++ client:
`native/core/abi/kiln_hook.hpp` (header only). Managed mods keep using `Kiln.Hooks`; see [../hooks.md](../hooks.md).

## Getting it

```cpp
#include "kiln_hook.hpp"

const KilnHooksApi* hooks = kiln::hooks_v2(api);   // api->service("kiln.hooks", 2); null if only v1 is present
```

A core without v2 is refused, not worked around: services and the core ship together. A plugin that must also run
on an older Kiln checks `hooks == nullptr` and turns its feature off.

### Early plugins

A plugin that exports `kiln_plugin_early` runs before sigdb and the services start (docs: [../engines/README.md](../engines/README.md)).
Since dev.21 the core makes `kiln.hooks` v2 answer `api->service()` for such a plugin already, when the build is verified or
the policy is `any` (the same condition as for hooks at all). The registry is ready; `scan` reads the exe file's code.
Links an early plugin places are ordinary links. If the start then turns out to be safe idle (no sigdb definitions), the
service goes away again for later callers and the early links stay. `kiln-shroudforge` uses this to hand ShroudForge's
native runtime its six engine hooks ([../shroudforge-compat.md](../shroudforge-compat.md#engine-hooks-through-kilnhooks)).

## A raw link: any signature

`raw_detour` takes a function with the **target's own signature**: any arguments, floats, stack arguments, any return
type. It gets `*original`, a function pointer with the same signature. Calling it runs the rest of the chain and then
the real function.

```cpp
static kiln::RawHook g_add;

static void hk_chat_add(void* chat, int type, const char* text) {
    if (g_add.caller() == g_receive_call_return) { /* a player line */ }
    g_add.call<void>(chat, type, text);
}

// in kiln_plugin_main:
if (!g_add.install(api, owner, target, reinterpret_cast<void*>(&hk_chat_add), "chat/ChatLogAdd"))
    return KILN_ERR_UNAVAILABLE;   // g_add.error() says why
```

Rules:

- `original` is written **before** the link can be reached, so the handler may call it on its first run.
- One handler, one live link per target: a raw handler reads one `original`, so it cannot tell two links apart.
  Installing the same function again on the same target **replaces** the earlier link in one step (its handle stops
  working). A call that is inside the old link at that moment goes on after the new link's place: the handler never
  runs twice for one call and never loops into itself, whatever the priorities.
- A continuation is never freed. After `revoke` it forwards to the next link, so a handler that still holds it is safe.
- Pass `call<R>(...)` the game's exact argument types (`uint64_t`, not `int`, for a 64-bit parameter). Or take
  `original<R (*)(A, B)>()` once and call that.
- Dropping a `RawHook` object does not remove its link. Call `revoke()`, or `revoke_owner(owner)`. Assigning to a hook
  that holds a link (`g = RawHook::create(...)`, `g = {}`) revokes the old link, like a safetyhook hook, and calling
  `install()` again replaces the link (see above). `MidHook2` behaves the same, except that a mid link is replaced
  after the new one is in: a call at that moment may run the handler twice (never in a loop).
- `install()` on a global is the safe way to hook after the game started. `create()` returns the hook by value and a
  game thread can run the handler before your global is assigned.

## Structured detours: `detour2` and the v1 `detour`

For integer and pointer signatures. Kiln collects the arguments for you:

```cpp
uintptr_t KILN_CALL on_call(KilnHookCall2* c, void*) {
    c->args[8] += 1;                       // up to 12 arguments; stack arguments included
    return hooks->call_original2(c) + 1;   // or skip it to replace the function
}
hooks->detour2(owner, target, &order, /*arg_count*/ 9, &on_call, nullptr, &h);
```

`c->caller` is the game's return address. `c->arg_count` is the count you declared; arguments past it reach the
original as the caller left them. Float arguments and float returns need a raw link.

The v1 `detour` is now a structured link too. It still shows four arguments, but the original now receives **all**
of them, not only the first four. A handler that faults is disabled (as before), and the chain goes on. A fault
inside the rest of the chain is not blamed on the handler: it goes on to the game's own handlers.

## Mid links

`mid2` runs a handler with the registers at `target`. The handler may change them (`KilnRegisters`, the same layout as
safetyhook's `Context64`; `kiln::MidHook2` accepts a handler taking `safetyhook::Context&` as is). The rules are
safetyhook's, for `mid2` and the v1 `mid`: `rsp` is read-only; `rip` is where execution goes on; `trampoline_rsp`
starts at `rsp - 8` and the stub resumes with `rsp = trampoline_rsp; ret`. A handler on a function's first instruction
can skip the function:

```cpp
void on_entry(KilnRegisters* r, void*) {
    r->rax = 0;                    // the return value
    r->trampoline_rsp = r->rsp;    // [rsp] is the game's return address: return to the caller
}
```

(An early dev.21 build started `trampoline_rsp` at `rsp - 16`, so that early return corrupted the caller's stack. The released dev.21 does not.)

A mid link may share its address with raw and structured links. They run in chain order; each sees the registers the
previous link left.

## Order

On one target: **constraints, then priority (higher first), then registration order.**

```cpp
KilnHookOrder o{sizeof o, /*priority*/ 0, KilnStr{"emf/ChatLogAdd", 14}, {}, KilnStr{"kiln.chat", 9}};  // after kiln.chat
```

- `before` and `after` are comma-separated owner names. Kiln compares them loosely: the kind prefix and a `.dll` suffix
  are optional and `-` equals `.`. A service loaded as `service:kiln-chat.dll` matches `kiln.chat` and `kiln-chat`;
  an EMF mod owner `emf:coolmod` matches `coolmod`.
- Links of the same owner are never ordered by constraints (use priority).
- `flags = KILN_HOOK_NEWEST_FIRST` (in `KilnHookOrder`, read only when its `size` covers it, so older plugins are
  unchanged): at one priority the flagged links run before the others, **newest first**; the others keep registration
  order. A (re)install counts as the newest: a revived link, or the same handler installed again in place. This is how
  mods of different engines stack on one function. kiln-emf and kiln-shroudtopia give every mod detour priority 1 and
  this flag, so the latest detour sees the game's call first whichever engine placed it, as layered inline hooks did
  before dev.21, and all of them run before Kiln's own links (priority 0).
- A constraint that contradicts an earlier one is dropped, with a warning in kiln.log. The earlier link keeps its
  constraint.
- Adding or revoking a link while threads call through the chain is safe. Moving links that are already installed
  (only when adding or revoking a link with `before`/`after` changes the order of two other links) is not atomic: a call in flight at that moment can skip a
  link or run one twice. It cannot crash and it always reaches the real function.

## `caller()`

`hooks->caller(handle)` is the return address of the game call that entered the target on this thread. It is valid
for every link, not only the first, **if you read it before the handler calls its original or another hooked
function**. The entry stub stores it in a per-thread table, one slot per target. A recursive call into the same target
on the same thread overwrites the slot; read it first.

## Other modules' hooks

Another engine (Enshwick, EMF, ShroudForge, a closed DLL) may have hooked a function before Kiln. Kiln compares the
target's live bytes with the exe file's before it hooks:

```
[kiln] hooks: chat/ChatLogAdd chained over enshwick.dll+0x11ea80
```

Kiln's detour sits in front of the other module's jump and the call goes on into it. The map records it as
`foreignBefore`.

After boot and at the first world, never per frame, Kiln checks that each target still begins with its own detour.
If another module hooked over it:

```
[kiln] hooks: xhl.dll+0x4410 hooked over Kiln's chat/ChatLogAdd (Kiln's detour stays armed; it is reached through their jump)
```

The map records `foreignOver`. Kiln never writes over that target again, and never removes the other module's jump.
Kiln does not unhook its own detours (an empty chain just calls the real function).

## The hook map: `state/hooks.json`

Written at boot end, at the first world, and when links change (at most once per second). `map_json(buf, cap, &len)`
returns the same text; `buf` may be null to ask for the length. Offsets and RVAs are hex strings.

```json
{
  "format": 1,
  "kiln": "0.2.0-dev.21",
  "phase": "boot",
  "worldChecked": false,
  "generated": "2026-11-09T14:20:33Z",
  "linkSlots": { "used": 214, "cap": 65536 },
  "targets": [
    { "rva": "0x9151f0", "symbol": "chat/ChatLogAdd", "label": "chat/ChatLogAdd",
      "links": [
        { "owner": "service:kiln-chat.dll", "kind": "raw", "priority": 0, "label": "chat/ChatLogAdd" },
        { "owner": "plugin:kiln-emf.dll", "kind": "raw", "priority": 0, "label": "emf/ChatLogAdd" } ],
      "foreignBefore": { "module": "enshwick.dll", "offset": "0x11ea80", "address": "0x7ffc...", "own": false },
      "foreignOver": null }
  ],
  "foreign": [
    { "symbol": "gameplay/InventoryTransfer", "rva": "0x19e860",
      "module": "enshwick.dll", "offset": "0x11ea80", "address": "0x...", "own": false } ]
}
```

- `phase` is what wrote the file last: `boot`, `world` (the first world) or `change`. `worldChecked` is true once the
  first-world check of other modules' hooks has run.
- `linkSlots`: link slots taken since the game started, and how many there are (see Limits).
- `kind` is `raw`, `structured`, `v1` or `mid`. `links` are in chain order: the first one runs first.
- `symbol` is the sigdb name of the address, or null. `label` is the symbol name, else the first link's label.
- `foreignBefore` and `foreignOver` are `{module, offset, address, own}` or null. `module` is empty when the jump goes
  to memory outside any module (a relay stub); `"inPlace": true` means bytes were changed but no jump was found.
  `own: true` means the module is one of Kiln's own (`kiln-*.dll`).
- `foreign` is sigdb's list of every symbol whose live bytes differed from the exe file's before Kiln hooked
  anything (the `code.live_differs_symbols` of `sigdb-report.json`), with the module each one jumps into.
- The writer is extensible: other parts of the core add sections with `add_map_extension`
  (`native/core/src/services/hooks_internal.h`). The closed-DLL report adds `pid`, `overlaps` (where another module's
  hook and a Kiln-hosted link meet: `foreign-before`, `foreign-over`, ShroudForge's `refused`), `rules` (the compat rules
  that fired) and `ruleFiles`; what they mean and how to fix an overlap:
  [../engines/README.md](../engines/README.md#closed-source-dll-mods). Schema: `schema/hooks-state.schema.json`.

## Threads

- A handler runs on whatever thread the game calls the target on.
- The **first** link on a target stops every other thread of the game for a moment, writes the jump over the function's
  first bytes, and lets the threads run again. A thread that was paused inside those bytes (right after the function's
  first instruction, say) is moved to the same instruction in the trampoline first, so it never runs half a jump. Wine
  needs about 70 microseconds per thread (5 ms for 66 threads); kiln.log at debug level says how long it took, and warns
  above 50 ms. Every later link on the target stops nothing: Kiln rewrites 8-byte cells that threads read with one load.
- Why Kiln stops the threads itself: safetyhook only makes the page non-executable while it writes, which catches a thread
  that runs into the bytes but not one the scheduler has paused there. That thread resumes in the middle of the new jump.
  On a loaded test machine under Wine this crashed 5 of 72 test runs. Windows can pause a thread the same way, so the race
  is not specific to Wine.
- What this does not cover: a thread that is created during those few milliseconds; a branch from elsewhere into the
  middle of the replaced bytes, or a call made from inside them that has not returned yet (the 5 to 14 bytes at the
  target: choose a hook address that nothing jumps or returns into). If Kiln cannot list the threads, or the
  trampoline has a shape it does not know, it logs a warning and falls back to safetyhook's own enable, with the old
  race; a thread it cannot open or stop keeps running and is counted in a warning. A mod that places its own safetyhook
  hook on game code has the old race too. Two hook libraries that stop threads (Kiln and a MinHook-based DLL mod, say)
  must not install at the same instant from two threads: each could stop the other.
- Stack arguments, rcx/rdx/r8/r9, xmm0-3 and the flags reach your handler as the game left them. Handlers are called with the
  standard stack alignment.

## Limits and failures

| What | Limit |
|---|---|
| Hooked addresses | 512 (`stats().targets_cap`) |
| Link slots | 65,536 per process (stubs are never freed) |
| `detour2` arguments | 12 |

`raw_detour`, `detour2` and `mid2` return `KILN_ERR_INVALID_ARGUMENT` for an address outside the game image or a null
handler, and `KILN_ERR_UNAVAILABLE` when safetyhook cannot place the detour (kiln.log says why) or a table is full.
Nothing crashes: the feature that wanted the hook just turns off.

`stats()` reports `targets` and `targets_cap` (512), so a client can tell "the target table is full" from "this address was
refused": `kiln::hooks_targets_full(hooks)` in `kiln_hook.hpp` does the comparison. A full table refuses every new address for
good (targets are never freed), so no Kiln link can ever join it. kiln-emf and kiln-shroudtopia use that: when
`raw_detour` answers UNAVAILABLE and the table is full, a mod's hook on that function becomes a plain safetyhook detour
(logged once, "kiln.hooks has no free target slot"), as for a function outside the game exe. Any other UNAVAILABLE (the
detour could not be placed, or the link slots are used) fails the mod's hook with that reason; the plugins do not
work around it.

A revoked link keeps its slot, because a call may still be inside it. It leaves the chain (the map and `stats()` show
live links only). Installing the same handler on the same target again (same kind, function, user data, owner and label)
takes the revoked link's slot back, with a new handle: a service that toggles its hook, or a mod that re-installs one
static handler, costs one slot. A different user pointer or label each time costs a new slot each time. Managed
`Hooks.Detour` / `Hooks.Mid` reuse the slot of the same mod id, kind and address, also across a hot reload: their
links sit under one hooks owner per mod id, named `mod:<id>`, and keep one user pointer per (mod id, kind, address)
for the life of the process (`managed/services/Kiln.Hooks/Cookies.cs`). kiln.log warns once when 75% of the slots are used and once when all are; after
that only revived links can be installed. `stats()` (`links_used`, `links_cap`) and hooks.json (`linkSlots`) show the
use.

A target whose first detour safetyhook refuses is remembered with its bytes: the same request is refused at once, with no
new log line and no memory, until the bytes there change.

`stats()` counts: targets, raw links, targets chained over a foreign hook, targets hooked over, link slots. Raw links
are not counted per call. Structured and mid dispatches are counted by each thread in its own table (`detour_calls`,
`mid_calls` are the sums when you ask), so no cache line is shared between threads on the call path.

## Tests

- `native/core/tests/test-hooks-v2.sh` (Wine, no game): chains of raw links with 9 arguments and `caller()` in each,
  floats, `detour2` with 9 arguments, ordering, newest-first links of several owners, mid links beside raw links, registers and flags, revoking the middle
  link while 4 threads call, first installs under load, threads parked inside the replaced bytes (they must come out in the
  trampoline, also past a relocated short jump), a first install with 64 other threads alive, a foreign jump before and
  over Kiln, the map, the C++ header, mid hooks that return early or redirect `rip` at a function entry, RawHook
  re-installed on a live hook while threads call it (raised and equal priority, `create()`, new labels: no nesting, one
  run per call), revival, an invalid UTF-8 label and a throwing map extension, a refused target. It runs twice, with
  the caller table in the TEB's TLS slots and in the expansion slots, then once in `slots` mode (every link slot used) and
  once in `targets` mode (every target slot used, a new address refused, a hooked one still takes links).
- `native/spikes/hookchain/run.sh`: how safetyhook chains with another engine's jump.
- `native/spikes/hooksv2/foreign_stub.cpp` builds `foreign-stub.dll`, a stand-in for another engine for lab servers:
  chain it from `kiln/chain.ini` and give it `foreign-stub.ini` (`before=<rva,...>` hooks at load, before Kiln looks;
  `over=<rva,...>` hooks over Kiln's detours once `state/hooks.json` exists). Used on the dev.21 lab to see the
  `chained over` and `hooked over` lines and the `foreign` list.
- `native/plugins/shroudforge/tests/test-host-chain.sh` (Wine, no game): ShroudForge's six hooks placed through this
  registry by the `kiln-shroudforge` adapter, with a foreign `jmp` on one of the places first.
