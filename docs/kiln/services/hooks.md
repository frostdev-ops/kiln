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
[kiln] hooks: chat/ChatLogAdd (kiln-chat) chained over enshwick.dll+0x11ea80
```

Kiln's detour sits in front of the other module's jump and the call goes on into it. The map records it as
`foreignBefore`. These lines name Kiln's hooks on the function by their labels and owners (`<label> (<owner>)`, the
owner `service:kiln-chat.dll` as `kiln-chat`), not by the sigdb name of the address: several symbols can name one
function, and the map's `label` is the alphabetically first of them (dev.29, GitHub issue 39: `build/ItemFind` for
kiln-content's `content/ItemRegistryFind`).

After boot and at the first world, never per frame, Kiln checks that each target still begins with its own detour.
If another module hooked over it:

```
[kiln] hooks: xhl.dll+0x4410 hooked over Kiln's chat/ChatLogAdd (kiln-chat) (Kiln's detour stays armed; it is reached through their jump)
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
- `codePatches` (since 0.2.0-dev.29.8, sigdb's `code_patch_map`): every place the live code differs from the exe file's
  when the map is written, Kiln's own hooks included: `{available, total, rows: [{rva, size, by, called?,
  calledSymbol?, symbol?}]}` (at most 1,024 rows; `total` counts them all). `by` says who changed it: `call redirected
  to <module>+<offset>` for a `call rel32` another module points at its own code (`called` is the function it called
  in the file), `jmp to <module>+<offset>` for a hook, `bytes changed in place` otherwise. Some closed mods compare the
  game's code with the exe file before they patch it and refuse a call someone else redirected first (XHL-Core
  plugins: "could not prepare CALL 11 of 24 (invalid or changed since capture)"); this list names who did.
  `available: false` with a `reason` when Kiln has no file view of the code.

## Owner cleanup: `subscribe_owner_retiring` (dev.28)
A native service that keeps state for other owners (leases, queued work, callbacks into a mod) asks to be told when
one of them is revoked: `hooks->subscribe_owner_retiring(my_owner, fn, user, &handle)`; check `header.size` covers
the member, the capability row is `hooks.owner_cleanup`. `fn(retiring, user)` runs once per revoked owner (a mod
unloading, a plugin whose start failed), after the core marked the owner dead and removed its own subscriptions and
tasks, on the thread that revoked it, with no core lock held and under a fault guard (an observer that faults is
removed). It is not called for owner 0 nor for the observer's own owner. In the callback drop the retiring owner's
state and never call into it again; do not wait and do not call game code. A call into the retiring owner that is
already running on another thread is the service's own to guard. `unsubscribe_owner_retiring(handle)` may be called
from inside the callback.

## Typed hooks and calls: `detour_typed`, Kiln.Sigs (since 0.2.0-dev.29)
A typed link hooks or calls a game function as a function: integer, pointer, float and double arguments in their
Win64 positions (`rcx`/`xmm0` ... `r9`/`xmm3`, then the caller's stack), and the return in `rax`, `xmm0` or a hidden
return buffer (`sret`). The shape comes from a descriptor in sigdb (`sig`, [../sigport.md](../sigport.md#descriptors-of-typed-hooks-sig-blocks)),
which the core checks against the function's code when it loads: a descriptor that no longer fits turns
`sig/<symbol>` off and refuses every typed hook of it, with a reason that names the argument. Capability rows:
`hooks.typed` (the link kind) and one `sig/<symbol>` per described core symbol.

**Native** (`kiln_hooks.h`, the "typed links" block): `detour_typed(owner, target, order, sig, fn, user, out)` with a
`KilnTypedFn(KilnCallFrame*, void*)` handler that may change the frame's arguments, call `call_original_frame(frame)`
once (the rest of the chain and the function; a second call does nothing), set the return fields, and return
`KILN_TYPED_SKIP` (the function does not run) or `KILN_TYPED_CONTINUE` (when it did not call the original, the original
runs after it). `call_with_frame(fn, frame, nstack)` is a direct call. A native mod that relies on a described
signature checks `api->feature("sig/<symbol>")` first. A scalar argument's or return's `size` may be 0 (the kind's own
size); a by-value struct's is its size.

`detour_typed_checked` (since 0.2.0-dev.29, offset 288; check `header.size`) takes the same arguments and makes the same
link with `mid2_checked`'s patch policy: the descriptor is checked first, then the first detour on a target is written
only when every other thread of the process could be stopped and checked and none stands inside the bytes it replaces.
Otherwise nothing is written, there is no fallback to an unchecked patch, and the result is `KILN_HOOKS_ERR_UNCHECKED`
(102): a later attempt may succeed, and kiln.log says which threads (`checked installation refused, nothing written:
...`). A target that already has its detour needs no patch, so a further link goes in as with `detour_typed`. Managed
typed hooks use only this member.

**C#** (`KilnServices` `Hooks;Sigs`, advanced mods only): one generated class per descriptor,
`Kiln.Sigs.<Area>.<Name>` (the area is the symbol's part before `/` in Pascal case; `Core` for a symbol without one).
The core's ten are in Kiln.Sigs:

| Class | Function | `Invoke` | What a caller of `Invoke` provides |
|---|---|---|---|
| `Kiln.Sigs.Items.GetItemVolume` | `OptS32 (const ItemInfo* item)` (sret) | any thread | an item from the static registry; null gives `{18, 1}` |
| `Kiln.Sigs.Items.AttrFind` | `AttrRef (u32 attribute, ptr registry)` (sret) | tick thread | a live entity's attribute component; the pointer it returns lasts until the step ends |
| `Kiln.Sigs.Items.AttrChanged` | `void (ptr component, ptr registry)` | hook only | |
| `Kiln.Sigs.Time.SetTimeScale` | `void (Daytime*, f32 scale, s64 ecsNow)` | tick thread | the server world's Daytime and its now |
| `Kiln.Sigs.Time.GetGameTime` | `s64 (Daytime*, s64 ecsNow)` | tick thread | the same |
| `Kiln.Sigs.Abilities.RegisterNative` | `bool (ptr registry, u32 hash, ptr fn, ptr name, u8 argc)` | hook only | (world setup, before managed mods load: hook it from a native plugin) |
| `Kiln.Sigs.World.ReadOther` | `ptr (ptr out, ptr storage, u16* accessor, u32 entity)` | hook only | |
| `Kiln.Sigs.Core.GetComponent` | `void (ptr out, ptr world, u32 hash, u32 entity)` | tick thread | the live server world; the slice lasts until the step ends |
| `Kiln.Sigs.Rules.QuatRotate` | `ptr (ptr out, ptr rotation, ptr vector)` | any thread | the caller's own values |
| `Kiln.Sigs.Weather.SampleAt` | `WeatherSample (ptr weather, ptr at, s64 now)` (sret) | tick thread | the server's WeatherSystem |

```csharp
using Kiln.Sigs.Items;

if (Capabilities.Is("hooks.typed") && Capabilities.Is(GetItemVolume.Capability))
{
    HookResult r = GetItemVolume.Detour((ref GetItemVolume.Call c) =>
    {
        c.CallOriginal();                                   // the game answers first
        if (c.Item != 0 && c.Result.Has != 0) c.Result.Value *= 2;   // Result: a ref into the caller's buffer
    });
    if (!r.Ok) Log.Warn(r.Reason);                          // a game update changed the function
    OptS32 v = GetItemVolume.Invoke(0);                     // a struct return comes back by value
}
```

- **`Call`** (a `ref struct`, valid during the handler): one typed property per argument (a narrow integer is masked
  to its type: the upper bytes of a `u8` on the stack are the caller's garbage), `Result` (a scalar: get and set; a
  struct: a `ref`), `CallOriginal()`, `OriginalCalled`, `Skip()`. The result is zero when the first handler starts.
  Without `CallOriginal` and `Skip`, the function runs after the handler with the arguments as it left them. `Skip`
  ends the call with `Result` as the handler set it (fill an sret `Result` first); after `CallOriginal` it changes
  nothing.
- **`Detour(handler, priority)`** returns a `HookResult` (`Ok`, `Reason`, `Handle`): refused (`Ok` false, and a
  `HookRefusedEvent` for whoever listens) when the descriptor does not fit the function on this build, its symbol does
  not resolve, or no target slot is left. A null handler throws `ArgumentNullException`, a core without typed links
  `KilnUnavailableException("hooks.typed")`, a mod that is not advanced `InvalidOperationException`. A link is
  installed when the mod asks for it and removed with the handle or when the mod unloads; a re-install by the same mod
  on the same function takes its old link slot again.
- **`Invoke(...)`** calls the function through its hook chain, as the game's callers do. It never calls a function
  whose row is off (`KilnUnavailableException` naming `sig/<symbol>`); a `tick` descriptor's `Invoke` throws off the
  tick thread (also inside a hook handler on a worker). The caller owns every other precondition (the table above):
  a fault inside the game's function is the game's.
- **Faults**: a handler that throws is caught in Kiln's dispatcher (logged on the mod's log, three times per install):
  when it had called the original, the caller gets the original's result (whatever the handler wrote since), else the
  original runs with the arguments as the handler left them. That holds for a managed exception and for a write to the
  null page in the handler's own code (the runtime raises `NullReferenceException`; lab: `{18,1}`, the original once,
  the process went on). It does **not** hold for any other access violation in a managed handler: one in its own code
  above 64 KiB (`AccessViolationException`) or in native code it calls (a P/Invoke, an `Invoke` of a game function with
  a bad argument) ends the process. .NET 10 fails fast before Kiln's native guard sees it (lab, Wine, both before and
  after `CallOriginal`: `Fatal error. 0xC0000005` or `Fatal error. System.AccessViolationException`, exit code 5). This
  is the runtime's rule for all managed code, not only typed hooks (`Hooks.Detour` and `Hooks.Mid` handlers, a tick
  handler). With the runtime's `legacyCorruptedStateExceptionsPolicy` the dispatcher receives such a fault as an
  exception and the rule holds (lab, measured); Kiln does not switch it on. A native typed handler's fault is caught by
  the native guard, which disables the link and restores the original's complete result, its struct-return buffer
  included (Wine: 24- and 128-byte structs); snapshot storage is allocated before the handler runs. A fault while the
  original runs is the game's.
- **Threads**: the handler runs on the thread that calls the function, game worker threads too; it runs as its mod
  (gated calls work), and the mod's unload waits for handlers in flight (one second, then the unload goes on and the
  mod's fragment stays until the last one returns).
- **A mod's own descriptors**: a `sig` in an entry of the mod's fragment (`sigdb/server.json`, `sigdb/client.json`;
  names `<modid>/...`) becomes a class `Kiln.Sigs.<ModId>.<Name>`, internal to the mod (Kiln.Sdk's generator; the root
  namespace is `KilnSigsNamespace`). It may name a struct of its own fragment or one of Kiln.Sigs (`OptS32`,
  `AttrRef`, `WeatherSample`). Load the fragment with `Symbols.LoadShipped()` before the first `Invoke`: its report
  (kiln/logs/sigdb-&lt;modid&gt;.json) decides whether the descriptor fits; `"abi": "trust"` accepts a function whose code
  the check cannot follow. The generator's warnings: KILN180 (a descriptor it cannot use), KILN181 (descriptors but no
  Kiln.Hooks), KILN182 (an unreadable sigdb file).
- **What a matching fingerprint does not prove**: f32 versus f64, two types in one stack slot, integer versus
  pointer, aggregate layout, and the extent of a return buffer are not told apart. The compiled descriptor is also
  compared with the resolver's loaded descriptor before Invoke, including return class and sizes, but two equally
  wrong descriptions can still agree. Review the native function's meanings for each executable after an update.
- **Installation timing**: a request outside a safe Pre point while worlds run is accepted with `HookResult.Pending`
  true and a handle whose `State` is `Pending`. The host drains it in the next authoritative Pre (joined clients use
  client Pre). With no world, or in that Pre, it installs immediately. The installation is `detour_typed_checked`
  (above): the server world's Pre stops the simulation's workers, not a hosting client's render or network threads, so
  the first patch of a function is written only with every thread checked. When kiln.hooks could not check every
  thread (nothing written), the request stays `Pending` and is tried again at the next safe tick, three attempts in
  all; then it is refused. kiln.decisions, the other checked installer, makes one attempt and refuses its kind for the
  process; a typed hook gets two more because what makes the check fail (a thread caught inside the 5 to 14 bytes, a
  thread that is ending) is usually gone a step later, while a thread Kiln cannot open or stop stays, and every attempt
  stops the whole process for a moment. A late refusal (also a descriptor or symbol refusal at the pending attempt)
  changes `Handle.State` to `Refused`, sets its `Reason`, and posts `HookRefusedEvent` once; the attempts before the
  last are only logged on the mod's log. Removing a pending handle cancels it. A core without `detour_typed_checked`
  refuses every typed `Detour` with the reason (no unchecked fallback).
- **Cost per call** (lab, Wine, server f0d824be, 2026-11-15, the PC busy with other builds): `Invoke` of
  GetItemVolume takes 634 ns (best of three rounds of 10^6 calls; 158 ns on 2026-11-14, before `Invoke` checked its
  descriptor against the resolver's on every call), and a managed typed pass-through hook adds 10 ns to it (47 ns
  measured on 2026-11-14). A hot function hooked by several mods costs the sum of their handlers.
- **What it costs a player whose mods use none**: nothing is hooked and nothing runs per call. The core checks the
  ten descriptors once when sigdb loads (a decode of up to 300 instructions each, from the exe file's bytes).
- **What has run**: unit tests of both halves; the Wine suites (the thunk, the state machine, struct results restored
  after a native fault, `detour_typed_checked` refusing with a thread parked in the bytes); and
  `mods/tests/hooks-typed-test` on a dedicated server (lab, Wine, player-free, 0.2.0-dev.29 on 2026-11-15): typed links
  from a native plugin at boot (RegisterNative, 113 calls, argc from the caller's stack equal to the game's natives for
  all of them), managed hooks installed through the checked member (no refusal) and direct calls of GetItemVolume and
  SetTimeScale, a managed hook of AttrChanged on three worker threads with the lab parallel switch, a mod's own
  descriptor, and 20 hot reloads with nothing leaked. The other seven classes are generated and compiled and their
  descriptors fit both exes, but their hooks and calls have not run in a lab. The native typed link ran once on the
  client exe (a research session). A typed hook of a managed mod in a real game client has not run (guided check
  `06b-typed-hooks.client`): not its checked installation while the game renders, not with a second player joined, not
  across leaving and rejoining a world; and no lab row had a player, so the effect of a changed GetItemVolume on a real
  fuel transfer has not been seen.

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
  race; a thread it cannot open or stop keeps running and is counted in a warning. The checked members
  (`mid2_checked`, `detour_typed_checked`, every managed typed hook) never fall back: they write nothing and refuse
  instead. A mod that places its own safetyhook
  hook on game code has the old race too. Two hook libraries that stop threads (Kiln and a MinHook-based DLL mod, say)
  must not install at the same instant from two threads: each could stop the other.
- Stack arguments, rcx/rdx/r8/r9, xmm0-3 and the flags reach your handler as the game left them. Handlers are called with the
  standard stack alignment.

## Limits and failures

From C#: without kiln.hooks every call of `Hooks`, `Memory`, `Symbols` and `Constants` throws
`KilnUnavailableException` naming `kiln.hooks` (a `NotSupportedException`, as before); the members carry
`[RequiresCapability("kiln.hooks")]`. The advanced gate (`"advanced": true`) is checked first and still throws
`InvalidOperationException`.

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
- `native/core/tests/test-hooks-typed.sh` (Wine, no game): the typed link kind (the T3 set of sret, f64, eight stack
  arguments, narrow integers, mixed positions; the call state machine, faults before and after `CallOriginal`, a
  24- and a 128-byte struct return restored whole after a faulting handler, eight callers while a ninth installs and
  revokes) and `detour_typed_checked` (a thread parked inside the bytes: refused with `KILN_HOOKS_ERR_UNCHECKED`,
  nothing written; with the thread gone it installs; a second link writes no patch).
- `native/core/tests/test-hooks-checked-patch.sh`, `test-hooks-thread-stop.sh`, `test-hooks-typed-result.sh`,
  `test-hooks-fingerprint.sh` (host): the checked patch policy and the typed result code, the thread-stop loop, the
  complete-result snapshot, the fingerprint pass and the compiled-against-loaded descriptor comparison.
- `native/spikes/hookchain/run.sh`: how safetyhook chains with another engine's jump.
- `native/spikes/hooksv2/foreign_stub.cpp` builds `foreign-stub.dll`, a stand-in for another engine for lab servers:
  chain it from `kiln/chain.ini` and give it `foreign-stub.ini` (`before=<rva,...>` hooks at load, before Kiln looks;
  `over=<rva,...>` hooks over Kiln's detours once `state/hooks.json` exists). Used on the dev.21 lab to see the
  `chained over` and `hooked over` lines and the `foreign` list.
- `native/plugins/shroudforge/tests/test-host-chain.sh` (Wine, no game): ShroudForge's six hooks placed through this
  registry by the `kiln-shroudforge` adapter, with a foreign `jmp` on one of the places first.
