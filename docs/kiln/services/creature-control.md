# kiln.creature_control: who drives a creature body

Since 0.2.0-dev.28. Native service `kiln.creature_control` v1 (`kiln-creature-control.dll`,
`native/core/abi/services/kiln_creature_control.h`), managed face `Kiln.CreatureControl`.

A creature's body moves by its `ActorInput`. Two things of the game write that input every step: the creature's AI
(while it is on), and the game's steering program, which copies the body's `SteeringAgent` velocity into the input of
every entity that has one, after any other writer (a mod's write is overwritten and the body does not move; a lab
measured 0.00 m for every writer, and 11.24 m in 40 steps once the `SteeringAgent` was gone; docs/re of riding).
So code that wants to drive a body (a pet that follows, a mount, a vehicle, a `Kiln.Blocks` NPC brain) needs the AI
off and the `SteeringAgent` removed first, and both back when it is done.

The broker is the one code that does this. A driver asks it for a **lease**; the broker turns the body's AI off with a
layer of its own, waits until the body's current action has ended, saves the `SteeringAgent` (80 bytes) at a world-job
barrier, removes it through its own world program, checks that it is gone, and only then grants the lease. When the
last lease ends it zeroes the body's movement input for 3 steps, puts the saved `SteeringAgent` back with its range
query id and both velocity vectors zeroed (the game makes a new query within 20 steps), and releases its AI layer.
Pets, mounts and vehicles are its clients; none of them touches the AI switch or the `SteeringAgent` itself.

## A lease's life

| State | Means | Drive? |
|---|---|---|
| Pending | admitted; the body is changing hands | no |
| Active | granted: the transition is verified; `transition_sequence` is new | yes, while `can_drive` says so |
| Preempted | kept while a higher lease holds the body; resumed through verification with a new sequence | no |
| Released | released by its holder, after the body was restored or handed over (terminal) | no |
| Failed | a failure, the body's or the world's end (terminal; `result` and `reason` say which) | no |

- **Admission is not permission.** `acquire_control` / `CreatureControl.Acquire` answer Pending at best.
- **`can_drive(lease, key, transition_sequence)` is the only authority**, checked right before every write; a cached
  answer grants nothing, a notice is history. In C#: `CreatureControl.Inspect(lease)?.CanDrive`.
- **Release revokes at once**: from the call on nothing may drive; the lease reports Released after the restore
  (about 4 steps), Canceled when it was never Active.
- **Bounds** (simulation time): the body's current action must end within 5 s (else Failed, TimedOut, `Quiescence` or
  `BodyNotReady`); a component change must verify within 0.5 s (else Failed, TimedOut, `Transition`; a failed restore is
  `RestoreFailed`). Terminal leases stay queryable for 10 minutes; after that a handle answers `ExpiredHandle`.
- A body that is not ready for gameplay (no player near it, a dead body) never becomes Active: `BodyNotReady` after 5 s.

## Bodies and keys
A body is bound before it is leased: `bind_body(owner, entity, baseline_kind, retire_on_fatal = 1)` gives the key
`(world_epoch, body_generation, entity)`. `world_epoch` is the core's server-world session number (a hosting client's
own worlds never change it); `body_generation` is issued at the first binding and never reused. One binding reference
per owner; other owners share the key. Only non-player entities of the engine's counter range `[1024, 0x7fffffff]` of
the running server world bind; a body without `Actor`/`ActorInput` does not; a game creature (`baseline_kind` Enemy 0)
whose `SteeringAgent` is already gone is refused as **ExternalController** (another mod or loader moves it, and Kiln
does not claim a removal it did not make). `ExplicitNoAi` (1) is for bodies defined without AI (vehicles). The managed
face binds Enemy bodies itself and gives the binding back after its last lease on the body ended.

## Roles
`None 0, PetFollow 1, PetCombat 2, Ridden 3, Quarantine 4, Vehicle 5, PetStay 6, EngineGuard 7`. Priority:
Quarantine > Ridden > Vehicle > PetCombat > PetFollow = PetStay = EngineGuard; on a tie the earlier lease wins.

| Role | The body | Rules |
|---|---|---|
| PetFollow, PetStay | input: SteeringAgent removed, the broker's AI-off layer held | the **base group** with EngineGuard: one base lease per body. The same owner's next base role replaces its old one at once (old: Released, result Canceled, reason `Replaced`; no restore in between); another owner's is `Busy` |
| EngineGuard | AI on: the game's own guard drives | base group; AI-on (below) |
| PetCombat | AI on: the game's AI fights | AI-on (below); preempts the base group |
| Ridden | input | one per body (the mount service shares it among seats); preempts every role below it and keeps them Preempted; they resume after the ride through verification, with a new `transition_sequence` and no second snapshot |
| Vehicle | input, and the body's `DynamicEnemy01BehaviorState` removed for good | a permanent no-AI base: a Vehicle lease that was Active is not released on a live body (`release_control` answers `KILN_ERR_INVALID_ARGUMENT`, RetireRequired); the owner destroys the body and calls `body_retired` |
| Quarantine | the broker's own policy, never requested | see below |

A lease admitted under a higher one starts Preempted. An **AI-on** lease takes the body by zeroing its input for 3
steps and putting the `SteeringAgent` back (when the broker had removed it; the round's one snapshot is kept for a later
input lease), then releasing the broker's AI-off layer; it is Active only when the AI then runs. When another owner
still holds the AI off, it fails `AiInhibited`: the broker never overrides another owner's layer. A body without
enemy AI cannot take an AI-on role (`AiInhibited`).

## Quarantine and retirement
- `quarantine(key, cause)` (any thread, no game call): every lease of the body loses authority at once (`can_drive`
  checks the latch), and the body's root is denied from then on (`root_policy` answers 1, and kiln.combat drops every hit
  of that root, of every kind, before any other policy: no health change, HitEvent, reaction or aggro). A query shows the
  leases Preempted with effective role Quarantine and no winner. Causes 1-4 (a semantic down) win over 5-9 (a voluntary
  teardown); every cause stays in the record. A quarantined body is never restored and never unbound early
  (`release_body` answers `KILN_ERR_INVALID_ARGUMENT`); its leases end `Retired` once it is gone.
- `body_retired(owner, key)`: the owner destroyed the body; it must be gone (`KILN_ERR_INVALID_ARGUMENT` while it
  lives). Its leases end `Retired`, its root stays denied for the world epoch, its rows go.
- A restore that does not verify within 0.5 s is irrecoverable: the body is quarantined and retired at once through
  kiln.spawn (the `retire_on_fatal` consent of `bind_body`); the releasing lease ends `RestoreFailed`, the others
  `Retired`.
- Every binding reserves its deny entry (65,536 per world epoch); a retired or quarantined body keeps it until the world
  ends, an unbound live body gives it back. When the table is full, `bind_body` refuses (`Capacity`).
- The engine's entity counter going down within a world (an identity fault) quarantines every bound body (never
  destroying one: its id may name another entity now) and refuses new bindings until the world ends. The counter is
  read through sigdb's `items/storage.next_id`; without it the check is off.
- **Two-phase withdrawal:** when the broker's world program stops running in a server world, `creature_control` goes off
  (new admissions are refused), every bound body is quarantined at once and retired at the next Pre.

## Native table (v1)
`bind_body`, `acquire_control`, `query_control` (any thread), `can_drive` (any thread), `release_control`, `notices`,
`quarantine` (any thread), `body_retired`, `root_policy` (any thread), `next_request`, `release_body`. The rest run on
the server tick thread (`KILN_ERR_WRONG_THREAD` otherwise). Records: body key 24 bytes, request 48, state 248 (pack 8);
the header documents every field and error code. A request id is unique and increasing per owner: take it from
`next_request` right before `acquire_control`; the same id and request returns the same lease (also a terminal one), the
same id with another request is `RequestConflict`, an id at or below one already admitted is `ExpiredRequest`.
`notices` gives the owner's lease changes in order (1024 per owner; a full queue drops and sets `needs_resync`: query
every lease you hold). Ceilings: 256 bodies, 1024 open leases, a request queue of 1024 per step, refused before
anything is allocated.

A native client (the mount service) feeds the body from its own world program: register it after the broker's
(`kiln_creature_control`, which declares other-write `ActorInput` and `SteeringAgent`) and declare a write of
`ActorInput` too, so the two never run side by side; call `can_drive` in the update before each write.

## Managed face
```csharp
[ServerOnly] public static class CreatureControl {
    [AnyThread] public static bool Available { get; }
    [TickThreadOnly, RequiresCapability("kiln.creature_control")] public static ControlRequestId NewRequestId();
    [TickThreadOnly, RequiresCapability("creature_control")] public static ControlRequestResult Acquire(EntityRef body, ControlRole role, ControlRequestId requestId);
    [TickThreadOnly, RequiresCapability("kiln.creature_control")] public static Task<ControlLeaseResult> WaitActive(ControlLeaseId lease, double timeoutSimSeconds = 0.5);
    [AnyThread, RequiresCapability("kiln.creature_control")] public static ControlLeaseSnapshot? Inspect(ControlLeaseId lease);
    [TickThreadOnly, RequiresCapability("kiln.creature_control")] public static Task<ControlLeaseResult> Release(ControlLeaseId lease);
}
```
- The calling mod owns its leases; they are released when it unloads (the broker restores the bodies).
- `WaitActive` (0.01 to 10 s of simulation time) answers Ok when the lease is Active; a timeout answers Ok false and
  **keeps the lease** (release it yourself if you give up); an ended lease answers Ok false with its reason.
- `Release` is Ok when the lease ends Released, also again for a lease that already did (its reason kept); a lease that
  ended by a failure answers Ok false with that reason.
- `Inspect` reads any lease (also a native client's); null when the broker has none of that handle.
- The one error rule: a bad argument (no body, a player id, role None, Quarantine or Vehicle, request id 0, a request
  id reused for another request, a wait outside 0.01..10 s) throws `ArgumentException`; the broker's refusal is
  `Ok = false` with its reason; `creature_control` off throws `KilnUnavailableException("creature_control")` from
  `Acquire`, the table missing throws it naming `kiln.creature_control` from every member.
- `ControlChangedEvent` (tick thread, the start of the step after the broker moved) carries the lease's snapshot before
  and after, for every lease acquired through this face or named to `WaitActive`. Observe only.
- Sample: `mods/samples/CreatureControlSample`. `/hold` is a standalone lease on a spawned Vukah (Acquire, WaitActive,
  Release: it stands while held and is itself again after); `/pacer` is a creature block whose brain walks its body
  under the lease Kiln.Blocks takes for it ([npc-ai.md](../modding/npc-ai.md#how-a-brain-moves-its-body-since-020-dev28)).
  The guided check `19d-brain-walks` uses the pacer.

## Capabilities
| Row | Says |
|---|---|
| `kiln.creature_control` | the table is here (the core's row): queries, waits and releases work |
| `creature_control` | the broker takes new leases: kiln.types matched every field path it reads or writes (below), kiln.world registered its world program and has `write_other`, kiln.creatures has the AI switch; lowered (`faulted`) when the program does not run in a server world |

The layout guard resolves every path through the game's own reflection (`kiln.types`, base classes walked) and compares
it with the layout the contract was written for: `SteeringAgent` (80 bytes; `queryId` +0, `desiredVelocity` +8,
`velocity` +0x1c), `ActorInput` (456; `triggerContext.sequenceRuntimeId.sequenceId` +8, the move vectors +0x18c and
+0x198, the look direction +0x1b8, `locomotionSpeed` +0x1c4), `Actor` (`currentAction.playbackContext.state` +0x1b1,
`currentState` +0xbf8), the flags `ActionSequenceFlags.IsActive` 0, `StateFlag.IsReadyForGameplay` 50 and `Dead` 7. A
game update that moves one of them turns `creature_control` off (the log line `guard:` lists every path); the
footprint paths of `MovementColliderConfig` and `PathWalker` are reported and do not. Offline:
`native/services/creature_control/tests/guard_dump_test.py` checks the table against the type dumps in `sigdb/types/`.

## Threads and safety
- The broker's state changes on the server tick thread only (the table's tick functions and its Pre). `query_control`
  and `can_drive` read published copies under a per-lease lock held for the copy alone, never across a game call.
- Its world program runs inside the step and does only what the Pre decided: zero a body's input, queue the removal or
  the return of its `SteeringAgent`. The engine applies those at the end of the step; the next Pre checks them.
- A server world that stops ends every lease (WorldEnded) and drops every binding: nothing is restored into another
  world. The service DLL stays loaded for the life of the process.

## Failure injection (labs only)
`kiln-creature-control.dll` exports `KilnResult kiln_creature_control_inject(uint32_t what, uint64_t value)`, which
answers only when the file `kiln/data/kiln.creature_control/inject` existed when the service started (else
`KILN_ERR_UNAVAILABLE`); tick thread. `what`: 1 request queue capacity, 2 notice queue capacity, 3 the component
transition bound in nanoseconds (0: 0.5 s), 4 restores that never verify (0/1), 5 two-phase withdrawal now. It is not
part of the table and no mod API reaches it.

## Tests
- Host, no game: `native/services/creature_control/tests/run.sh` (records, registry, a lease's life, timeouts, body
  loss, the roles, preemption and resume, replacement, Vehicle, AI-on roles, quarantine, retirement, the deny table and
  its ceiling, the identity fault, injection, withdrawal, world end, notices, capacities; the guard table against the
  type dumps).
- Offline: `managed/tests/Kiln.CreatureControl.Tests` on the kit's fake (`KilnHarness.CreatureControl`).
- Lab: `mods/tests/creature-control-test` (`creature-control-test: PASS` lines in kiln.log; needs
  `kiln/data/kiln.creatures/force-ready`, as the creatures tests do; with `kiln/control/systems.parallel` it also
  counts the AnyThread reads made from worker threads, while the broker's own SYSTEM program stays on the world's
  thread). `mods/tests/blocks-npc-test`, section "brain moves": a brain's body walks under its lease, and a lease that
  fails before it drives leaves the game's AI off.
- Guided client check: `19d-brain-walks` (the sample's pacer walks to the player and stops).
