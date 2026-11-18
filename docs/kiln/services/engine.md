# kiln.engine: exact-build engine profiles and the engine's own gates

`kiln.engine` (`native/services/engine/`, `kiln-engine.dll`) knows which exact game build runs and holds Kiln's per-build
engine profiles. Kiln services whose members read engine memory from the caller's thread, or write it under the engine's
own gates, build on it. The per-build profiles, the exact-image selection and the engine-context read and write-gate
code are adapted from ShroudForge (MIT, Copyright (c) 2026 bonsaibauer; see `CREDITS.md`).

It has two tables:

| Table | Header | Who | What |
|---|---|---|---|
| `kiln.engine` v1 | `native/core/abi/services/kiln_engine.h` (public, append-only, in `kiln-abi`) | anyone | `identity`, `gate_stats`; data only, any thread |
| `kiln.internal.engine` v1 | `native/services/engine/include/kiln_engine_native.h` (Kiln-internal) | Kiln's own services only | the selected profile and its validated layout, guarded copies, the owner/ECS views and write support, the engine's own Scene write gate |

## Exact builds only

A profile applies only when all of these hold (exact executable identity, no structural fallback):

1. the exe file name is the profile's target (`enshrouded.exe`, `enshrouded_server.exe`);
2. the sha256 of the exe file (the core's, `KilnBuildInfo.exe_sha256`) is the profile's;
3. the loaded image's PE `TimeDateStamp` and `SizeOfImage` are the profile's (`SizeOfImage`, not the file length);
4. `<exe>.kfc` starts with `KFC4`;
5. exactly one ported profile matches.

Otherwise `identity().status` is `KILN_ENGINE_UNKNOWN_BUILD` (or `UNIDENTIFIED`/`AMBIGUOUS`), the rows below are
unavailable, and every engine-reading member of every service returns its own unavailable shape. A new game build needs a
new profile; nothing structural stands in.

Capability rows: `engine.profile` (an exact profile matched; text: its id), `engine.ecs_read` (owner and ECS read layouts
validated), `engine.ecs_write` (the original native-thread Scene write gate enabled). Unavailable rows carry
`symbol-missing` and the reason in their text. Per-call readiness (a world loading, the server thread stopping) stays
dynamic: each call says why it refused.

## Profiles

`sigdb/engine/<side>/<steam build>.json` are game-build profiles adapted from ShroudForge (MIT), copied verbatim
(`sigdb/engine/README.md`). `tools/engine/generate.py` turns them into
`native/services/engine/profiles.g.h` (committed; `--check` fails on drift). It applies the profile rules at build time, on
that immutable data: eligibility (`select_resource_profile.cpp:5-13`), the resource layout
(`parse_resource_layout.cpp`) and the engine layout (`configure_engine_layout.cpp`); a profile that breaks a rule fails the
build. `tools/engine/test_generate.py` mutates the real profiles and expects the refusal for each rule. `tools/engine/validate.py <profile> <copy of the exe>` checks a profile against a read-only copy of its
executable (never the installed game, never run): hash, PE stamps, file name, the change tracker's guard bytes at its RVA,
every code RVA a `.pdata` function start, the thread registry in `.data`, vtables in `.rdata`, the thread names. For
`ecsReadLayout` it reads each field out of the code that uses it, finding every function by its own code, never by an
address: the entity manager inside the world (`GetComponent`, `GetEntityTransform`), the entity map's count, slots,
occupancy bits, keys and id hash (the map lookup `GetComponentByType` calls), the record's layout, chunk and row and the
layout's bits, offsets and strides (`GetComponentByType`), the record's id (the change tracker), its definition pointer
and the definition's 16-byte fields (the engine's entity-to-template readers), and the world inside the GameServer.
Both exact builds pass with 15 of the 29 fields shown by code. The rest are listed with what confirms them at run time
(`RUNTIME_ONLY`): the generation (no function reads it alone), the registry owner offset (every view checks the entity
manager's owner word against it), the Scene list fields (every view resolves them twice), the two bounds, and which of
the definition's 16-byte fields is the template UUID. `tools/engine/test_validate.py` builds those functions from a
profile's values and expects each wrong field to fail the line that names it; with `KILN_ENGINE_BINARIES` set to a
folder of read-only copies it checks the real ones too. kiln.world's task-thread reads build on this table: its
`copy` and `ecs_view` only ([world.md](world.md#world-reads-from-task-threads)).

## Reading engine memory (kiln.internal.engine)

Members a Kiln service calls from ANY thread, world threads included (no allocation, no host lock, no wait), adapted
from ShroudForge's engine context (`engine-context/resolve_engine_owners.cpp`, `resolve_registered_scene.cpp`,
`lock_scene_access.cpp:68-82`):

| Member | What |
|---|---|
| `layout()`, `profile()` | the selected exact profile (identity, sections, verbatim leaves, the published `attribute_calculation_model`) and its validated layout; NULL on any other build |
| `copy(address, out, bytes)` | copy engine memory under SEH: a freed, guard, no-access or reserved page fails the call (`KILN_ERR_UNAVAILABLE`), never the process |
| `ecs_view(kind, view, reason)` | the owner/Scene/ECS view: kind 0 = `ResolveEcs` (the local authority when this process hosts one, else the client replica), kind 1 = `ResolveClientEcs` (the client's main world); refusals carry a reason string (`kiln_engine_view.h`, `reason_name()`) |
| `ecs_write_supported(view, component)` | `EcsComponentWriteSupported`: the view is a current local authority and the component's registration carries neither change-tracking category |

A view is an optimistic copy: every owner is resolved twice and published only when both passes agree; any failure of
the second pass is `ecs-engine-owner-changed-during-read`. On the dedicated server the traversal takes
the engine's thread-registry word Shared with a nonblocking try (`ecs-thread-registry-busy` while a thread registers);
the CAS is retried only while the word stays Shared (at most 128 attempts, never against a writer), so several Kiln
callers taking it at once do not refuse each other. A view is evidence of
one instant, not a lock: writers re-validate under the Scene gate. `gate_stats` counts views and view failures.

`attribute_calculation_model` follows `publish_attribute_model` (`initialize_resource_access.cpp:30-38`):
`"attribute-command-v1"` when the profile names it and `provenance.attributeCalculationEvidence` is a non-empty string,
else `""` (the generator computes it; a non-string model or a missing `provenance` refuses the profile).

## The engine's write gate (kiln.internal.engine)

`EcsWriteGuard` (adapted from ShroudForge, `engine-context/lock_scene_access.cpp:84-195`) on the engine's own two words: the thread
registry word taken Shared (it keeps the Scene allocated), then the GameServer Scene word taken exclusively with one CAS
(`0 -> 0x80000000`), never a wait. Members: `ecs_gate_acquire`, `ecs_gate_validate`, `ecs_gate_component_supported`,
`ecs_gate_track_change` (the engine's own change recorder, after its guard bytes match), `ecs_gate_write`
(`guarded_copy`) and `ecs_gate_release`; C++ consumers hold it through `kiln::engine::EcsGateScope`, which
releases on every path. The gate record (`EcsGate`) is the caller's: nothing is allocated. Before acquiring, the caller
sets `owner` (its `KilnOwner`) and `step` (the server step it observed, `KilnTickInfo.step`), both nonzero.

- **When it wins.** The engine holds the Scene word Shared for the whole GameServer update, Kiln's Pre/Post included
  so the gate only wins between frames. Busy (`ecs-scene-frame-busy`, `ecs-thread-registry-busy`) is the normal
  result: retry at a later step or apply in-phase; never wait.
- **Threads.** Refused before any word is touched on the server world thread (`kiln-in-frame`: the update already holds
  the word, an exclusive try could never win), on the client or prediction world thread (`kiln-world-thread`), and when
  the core cannot name the thread (`kiln-caller-thread-unknown`, from `KilnApi.on_world_thread`). Engine job workers
  running a world program are none of these and get `ecs-scene-frame-busy` like every caller during the update.
- **Kiln's admission**, also before any engine word and
  never a wait, so a flood of off-tick tasks cannot contend on the engine's own words or strand one at exit:
  - one Kiln holder at a time: a second Kiln caller gets `kiln-gate-busy`;
  - a per-owner busy latch: after any busy refusal (registry, Scene or `kiln-gate-busy`) that owner's further attempts
    in the same server step get `kiln-gate-busy-this-step`. The step is the live server step kiln.engine follows from
    its own server Pre subscription (the caller's `step` only where no tick subscription exists), so a stale or bumped
    caller step neither keeps an owner latched nor bypasses the latch. Successful holds never latch, so several writes
    per step stay possible;
  - `kiln-gate-closed` while the server world is stopping (closed at `KILN_WORLD_STOPPING`, reopened at the next
    `STARTED`) and from process exit (the `on_unload` handler closes it). Both wait a few microseconds for a Kiln
    holder in flight and log one that is left; at STOPPING none is expected (the engine holds the Scene word Shared
    through teardown).
  - **Admission modes** (`EcsGate.admission`): `GateAdmission::KilnMod` (the default) keeps the busy latch.
    `GateAdmission::RetryKilnHolder` is for callers that schedule their own retries: never latched, and a refusal
    caused only by another Kiln holder is retried up to 128 times without a pause before any engine word (never
    against an engine exclusive holder).
- **Priority.** While a thread may hold an engine word it runs at `THREAD_PRIORITY_ABOVE_NORMAL` at least (raised before
  the first word, restored after the last, both outside the hold), above the engine's normal-priority threads (the
  GameServer thread is created at `NORMAL`), so a holder is never preempted while the
  GameServer thread spins on the word.
- **Inside the hold** the GameServer thread spins at its next update (pause + `SwitchToThread`), so the gate path does
  only this there: guarded engine reads, the CASes (on the dedicated server `validate` re-resolves the
  view, a nested registry share), the engine's tracker call and the member's guarded writes. No allocation, no lock, no
  wait, no KilnApi call, no log: prepare buffers and take caller mutexes before acquiring. `tests/lint_gate.py` keeps
  these out of the gate's sources; the tests count allocations, CASes, writes and KilnApi calls inside the hold.
- **Faults and budget.** A fault in a read, a write or the tracker call is contained (SEH) and reported; the gate stays
  held and must still be released. `ecs_gate_write` refuses a component larger than the profile's budget
  (`EcsGate.maximum_bytes`, 64 KiB) with `KILN_ERR_BUFFER_TOO_SMALL` before any mutation
  (`write_entity_components.cpp:143-146`, maps to `ecs-write-size-exceeds-gate-budget`). After a `KILN_ERR_UNAVAILABLE`
  (the copy faulted) a partial write may have happened: restore the original, read it back and latch "uncertain" when
  the readback does not prove it (`write_entity_components.cpp:87-98`).
- `gate_stats` counts attempts, holds, busy refusals by engine word (`kiln-gate-busy` and the latch count in
  `refused_other`, never as Scene contention), in-frame and world-thread refusals, and the total and
  longest hold; the hold time covers the whole Scene exclusive interval (from the Scene CAS to the release), including
  a hold that then refuses.

## Kiln-internal tables

`kiln.internal.engine` follows the Kiln-internal table rule ([README.md](README.md#kiln-internal-tables)): only
kiln/services DLLs of this very build publish or resolve it. Consumers check `header.version` for an exact match,
`header.size` for each member they call and the table's own record sizes (`kiln_engine_native()` does all three). This
keeps a table that hands out engine addresses and the engine's own lock primitives away from plugins: a plugin holding
the engine's Scene word would freeze the server.

## Tests

```sh
KILN_OUT=/path/to/build native/services/engine/tests/run.sh          # host: generator, C layout, gate lint, identity, views and the gate over a fake engine, DLL entry (ASan/UBSan and TSan)
KILN_OUT=/path/to/build native/core/tests/test-service-access.sh     # the core's kiln.internal.* rule (real api.cpp block, stamped and without build metadata)
KILN_OUT=/path/to/build native/core/tests/test-build-stamp.sh        # the build metadata script (git, overrides, no git, nested export; also run from a nested export)
native/build.sh && native/build.sh kiln-engine-test engine-host engine-core-lookup
KILN_OUT=/path/to/build WINEPREFIX=/abs/prefix native/services/engine/tests/wine.sh   # the real DLL under headless Wine
```

The host suite lays a fake engine out from both real profiles' offsets (`tests/fake_engine.h`) and reaches every
refusal on the view paths: static ones by memory or layout edits, the "changed during read" ones by applying
one engine edit at every read index of a view in turn, plus concurrent registrar and owner-flip threads under TSan. The
gate test runs a simulated GameServer update (try-shared with 128 spins, then yield, as the engine) against two gate
writers under TSan: the update never sees a half-written component. The Wine host proves the Windows facts (PE
headers, exe name, `.kfc` header) and the SEH copy (guard, no-access and reserved pages) on a stand-in exe, which is
always an unknown build; `kiln-engine-test.dll` (the same code with test facts, built into `bin/tests/`, never shipped)
runs the gate over a fake engine with the real Interlocked words, a faulting write and a tracker call on a
non-executable page. `engine-core-lookup.exe` compiles the core's own lookup/registration and build-stamp blocks
(`native/core/src/api.cpp`, between their markers) and checks the Release build itself: every DLL in `bin/services` and
`bin/plugins` carries this build's stamp, a copy with another build's stamp is not trusted, and the Release
`kiln-engine.dll`, booted the way the core boots a trusted service, publishes `kiln.internal.engine` for services only. Matching the real game builds is proven statically (`validate.py` on the copies); the game itself is the final check.
