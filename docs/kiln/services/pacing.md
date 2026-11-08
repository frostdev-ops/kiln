# kiln.pacing: where the game waits, and its task pools

The places the game's loops wait in, and the limits its task system sizes its worker pools with. The same places Enshwick's `pacing`
API hooks (MIT; semantics from its docs and loader). How the places were found and what the registers hold:
[docs/re/pacing.md](../../re/pacing.md). What a mod writes: [lifecycle.md](../modding/lifecycle.md#pacing-pacing).

| Layer | File |
|---|---|
| C table | `native/core/abi/services/kiln_pacing.h` (`kiln.pacing` v1) |
| Native | `native/services/pacing/pacing.cpp` -> `kiln/services/kiln-pacing.dll` |
| Symbols | `sigdb/symbols/server.d/pacing.json`, `client.d/pacing.json` (features `pacing.loop_wait`, `pacing.server_wait`, `pacing.task_workers`; client: `pacing.message_wait`) |
| Managed | `managed/Kiln.Api/Pacing.cs` (the registry, `Kiln.Pacing`) and `managed/Kiln.Api/Services/PacingNative.cs` (the bridge Kiln.Host attaches before mods load) |
| Tests | `native/services/pacing/test-pacing.sh` (host), `managed/tests/Kiln.Api.Tests/PacingTests.cs`, lab mod `mods/tests/pacing-test` |

## How it works
- Five symbols, each a **mid-function address** (the instruction after the one that fills the register Kiln reads or writes): `pacing/LoopWait`
  (`r9d` ms, `rbx` ns left), `pacing/MessageWait` (client, `r8d`), `pacing/ServerWait` (`r9` the wait in ns, writable), `pacing/DefaultWorkers` and
  `pacing/BackgroundWorkers` (`rax` the group's limit, lowered).
- **No hook goes into the game until something asks.** `arm(sites)` puts in the hook of each place asked for (the managed half asks when the first handler
  registers); `cap_task_workers` puts in the worker hooks (the service itself asks at boot for `kiln-pacing.json`). A hook, once in, is never taken
  out: while nothing listens it is one relaxed load and a return, and it cannot remove another loader's hook that went on top of it (Enshwick's). Kiln's
  hook goes over a jump that is already there (kiln.log: `pacing/LoopWait is already hooked by someone else (jmp to ...); this hook chains with it`).
- A place whose hook the game's code refuses is dropped (`sites()` shrinks, `status().refused` names it); nothing retries it.
- The receiver (`set_receiver`) runs inside the game's loops on the thread that waits, concurrently from several threads; the service takes no lock there.
  What a server-wait receiver writes is clamped into `0..KILN_PACING_MAX_WAIT_NS` (1 s).

## Operator file
`kiln/config/kiln-pacing.json` (the profile's config folder when a profile is active), read when the service loads:
```json
{ "taskWorkers": { "default": 4, "background": 3 } }
```
`"taskWorkers": 4` caps both groups. The game's pools start before any mod loads, so this file (or native code that calls `cap_task_workers` during
boot) is the only way to cap them on a Kiln server. A cap is the most workers a group starts with; the default group adds its two main workers on
top (4 gives `Worker:6 Main:2`).

## Lab switch
`KILN_PACING_LAB=boot` in the server's environment puts the wait hooks in at boot instead of at the first handler, to test how another loader that starts
after Kiln (`kiln-enshwick` with `"start": "ready"`) copes. Not for play.
