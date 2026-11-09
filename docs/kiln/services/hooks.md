# kiln.hooks v2: one registry for every hook

`kiln.hooks` hooks game functions for Kiln itself and for plugins and mods. Since dev.21 every hook Kiln places on the
game exe goes through it: the services, the core tick, kiln-emf, EMF mods' `hook` slot, Shroudtopia's `CreateDetour`
and ShroudForge's engine hooks when Kiln hosts it. Only hooks on system DLL exports (`LoadLibraryExW`, Vulkan, XInput
on the client) and EMF/Shroudtopia hooks on targets outside the game exe still use plain safetyhook, plus the
registry's own detour. For each game address the registry installs **one**
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

- `original` is written **before** the link can be reached, so the handler may call it on its first run. It must be
  storage that belongs to this one link: two links that share a variable would overwrite each other's continuation.
- A continuation is never freed. After `revoke` it forwards to the next link, so a handler that still holds it is safe.
- Pass `call<R>(...)` the game's exact argument types (`uint64_t`, not `int`, for a 64-bit parameter). Or take
  `original<R (*)(A, B)>()` once and call that.
- Dropping a `RawHook` object does not remove its link. Call `revoke()`, or `revoke_owner(owner)`. Assigning to a hook
  that holds a link (`g = RawHook::create(...)`, `g = {}`) revokes the old link, like a safetyhook hook, and calling
  `install()` again replaces the link. `MidHook2` behaves the same.
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
safetyhook's `Context64`; `kiln::MidHook2` accepts a handler taking `safetyhook::Context&` as is).

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
- The **first** link on a target freezes the other threads briefly (safetyhook). Every later link on it freezes nothing:
  Kiln rewrites 8-byte cells that threads read with one load.
- Stack arguments, rcx/rdx/r8/r9, xmm0-3 and the flags reach your handler as the game left them. Handlers are called with the
  standard stack alignment.

## Limits and failures

| What | Limit |
|---|---|
| Hooked addresses | 512 |
| Links ever created | 65,536 (stubs are never freed) |
| `detour2` arguments | 12 |

`raw_detour`, `detour2` and `mid2` return `KILN_ERR_INVALID_ARGUMENT` for an address outside the game image or a null
handler, and `KILN_ERR_UNAVAILABLE` when safetyhook cannot place the detour (kiln.log says why) or a table is full.
Nothing crashes: the feature that wanted the hook just turns off.

`stats()` counts: targets, raw links, targets chained over a foreign hook, targets hooked over. Raw links are not
counted per call (that would put an atomic on every game call).

## Tests

- `native/core/tests/test-hooks-v2.sh` (Wine, no game): chains of raw links with 9 arguments and `caller()` in each,
  floats, `detour2` with 9 arguments, ordering, mid links beside raw links, registers and flags, revoking the middle
  link while 4 threads call, first installs under load, a foreign jump before and over Kiln, the map, the C++ header.
  It runs twice, with the caller table in the TEB's TLS slots and in the expansion slots.
- `native/spikes/hookchain/run.sh`: how safetyhook chains with another engine's jump.
- `native/spikes/hooksv2/foreign_stub.cpp` builds `foreign-stub.dll`, a stand-in for another engine for lab servers:
  chain it from `kiln/chain.ini` and give it `foreign-stub.ini` (`before=<rva,...>` hooks at load, before Kiln looks;
  `over=<rva,...>` hooks over Kiln's detours once `state/hooks.json` exists). Used on the dev.21 lab to see the
  `chained over` and `hooked over` lines and the `foreign` list.
- `native/plugins/shroudforge/tests/test-host-chain.sh` (Wine, no game): ShroudForge's six hooks placed through this
  registry by the `kiln-shroudforge` adapter, with a foreign `jmp` on one of the places first.
