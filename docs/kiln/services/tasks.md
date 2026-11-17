# Tasks: Kiln-owned worker threads for off-tick work

`kiln.tasks` (since 0.2.0-dev.30) runs a mod's heavy work on Kiln's own worker threads instead of in a tick handler.
A task's body runs on a pool worker, never on the tick, a world thread, a window or render thread or one of the
game's job workers; then its `done` callback runs, exactly once, also on a pool worker. A body touches game state only
through members documented as callable from any thread; anything phase-bound is posted back with `KilnApi.post` /
`post_world`. The public ABI is `native/core/abi/services/kiln_tasks.h`, version 1, 48 bytes on
Windows x64.

## Capability

`tasks.pool`: available on a booted runtime on both sides, the dedicated server and the client. Unavailable in safe
idle, or while no pool worker can run the work (a worker or the pool's maintenance thread could not start; the pool
retries a failed start every second), with the reason. It does not depend on the world-read capabilities.

## Members and threads

| Member | Threads | Blocks |
|---|---|---|
| `submit(owner, info, body, done, user, task*, refusal*)` | any thread, the tick and world threads included | never (a short internal lock) |
| `cancel(owner, task, state*)` | any thread | never |
| `status(owner, task, state*)` | any thread | never |
| `release(owner, task)` | any thread, also inside this task's own `done` | never |
| `drain_owner(owner, 0, running*)` | any thread except a pool worker | never: seal, cancel queued, poll |
| `drain_owner(owner, timeout_ms > 0, running*)` | only off the tick, off every world thread and off the pool | up to `timeout_ms` |

A body may call every member except a timed `drain_owner` (`KILN_ERR_WRONG_THREAD`). `run->cancelled(run)` is valid
only during that body's run, on its own thread. Every member is scoped to the calling owner's loaded instance: another
owner's handle, or one from an earlier instance of the same owner id, is `NOT_FOUND`. Owner 0 is refused on every
member (`INVALID_ARGUMENT`). Pass your own owner; Kiln trusts native plugins as everywhere else.

`submit` writes `*refusal` on every non-OK return and leaves it untouched on OK: `QUEUE_FULL`, `OWNER_SEALED`,
`OWNER_QUARANTINED`, `CAPABILITY`, `ARGUMENT`, `SHUTDOWN`, `NO_SERVER_WORLD` (header for each result code).

## States and outcomes

`QUEUED` (also an `AFTER_STEP` task waiting for its edge) -> `RUNNING` -> one immutable terminal state:

- `COMPLETED`: the body returned. For a native body always, also after a cancel request: a native body returns
  `void` and cannot say why it returned. A body that stops early because `cancelled()` returned 1 records that in its
  own state.
- `FAULTED`: the body faulted. A native structured fault also quarantines the owner (its queued tasks are cancelled,
  new submissions refuse `OWNER_QUARANTINED`) and retires that worker thread; a fresh one replaces it.
- `CANCELLED`: cancelled before it started (cancel, drain, revoke, its session ending); for a managed task also when
  its own token's cancellation ended it.

Every accepted task gets exactly one `done`, a queued-then-cancelled one included, never inline on the cancelling
thread.

## Records, capacity and release

An owner holds at most 1024 task records; a record counts until `release` (terminal records included: they are never
evicted) or the owner's drain or revoke. At the cap `submit` returns `UNAVAILABLE` + `QUEUE_FULL` and changes nothing.
`release` is for terminal tasks only (a queued or running one refuses `UNAVAILABLE` and is not cancelled), is legal
inside `done`, and is idempotent for a handle this owner instance already released.

## Lifetime and quiescence

`user`, and the code of `body` and `done`, must stay valid until `done` has returned, or until `drain_owner` reported
0 for the owner. Kiln holds a module reference (`GetModuleHandleExW` FROM_ADDRESS, not a pin) on both callbacks'
modules from before acceptance until `done` returns; a refused submit holds nothing.

Quiescence (`drain_owner` reporting `*running == 0`) means every body and every `done` of the owner has returned.
Kiln drops its module references just after that point, so a module stays mapped slightly longer than quiescence, never
shorter. Revoking an owner starts its retirement (no new tasks, queued ones cancelled, running ones signalled); it is
not quiescence.

`drain_owner` seals the owner for good, cancels its queued tasks (each still gets its `done`), signals the running
ones, releases its terminal records and then waits up to `timeout_ms`. A timeout returns `KILN_OK` with
`*running > 0`: nothing is freed or forced, and code and `user` stay referenced. Use `timeout_ms = 0` from the tick
(unload) and join off the tick.

## Done deliveries and the running cap

An owner runs at most 2 normal-slot items at once, and its `done` deliveries count toward that cap like its bodies.
An owner whose two normal bodies never return therefore also holds back its queued cancellations' `done`s, and its
`drain_owner` reports `*running > 0` until its timeout. That is bounded and reported (the watchdog names the
long-running bodies), not lost work. Other owners keep running: one owner's stuck bodies hold only its own slots.

## Configuration

`kiln.json` `"tasks"` sets the pool's global knobs; a `"server"` or `"client"` section overrides them key by key. An
absent key keeps the value before it; a wrong type or a value out of its bound gives the default and one `kiln.log`
warning naming the key and the bound.

| Key | Bound | Default |
|---|---|---|
| `workers` | 1..8 | `clamp(cores / 4, 1, 4)` |
| `long_running_global` | 1..16 | 8 |
| `watchdog_running_s` | 5..600 | 30 |
| `watchdog_queued_s` | 5..600 | 30 |

The per-owner limits (2 running, 2 LongRunning, 1024 records) are part of the contract above and are not configurable.

## LongRunning and priority

`KILN_TASK_LONG_RUNNING` gives the task a dedicated below-normal thread: at most 2 per owner and 8 in all, beyond that
it queues. `priority` (Normal, Low, High) orders work inside one owner only; it never raises an owner's share or an OS
priority. Owners are served round-robin.

## AFTER_STEP and sessions

`KILN_TASK_AFTER_STEP` captures the live server world's session and that session's last Post edge at acceptance. The
task becomes runnable at the first later Post edge of the same session; an edge seen between acceptance and queueing is
not lost. A wake means "Kiln's Post ran" (a scheduling hint), not that the body may touch game state. When that session ends (its `KILN_WORLD_STOPPING`) every task still
waiting on it is cancelled, with its `done` delivered as `CANCELLED`; it never wakes in the next world. With no live
authoritative server world (the main menu, a client joined to a remote server) `submit` refuses `UNAVAILABLE` +
`NO_SERVER_WORLD`.

## Managed tasks

The managed host runs C# tasks (`Context.Tasks`) on the same pool, through its Kiln-internal lease table
(`kiln.internal.tasks.host`, `native/core/src/tasks_host.h`; Kiln.Host's side is `managed/Kiln.Host/Tasks.cs`): one
pool, one set of limits and one unload rule for native and managed tasks. A managed task counts once
against its owner's 1024 records; between its awaits it holds no worker, and its continuations are never refused by the
cap or a drain, so a cancelled task can always finish. Its hops back to the tick run on the server world's tick before
the Pre subscribers, in order per mod and mods in turn, at most 64 hops and 2 ms per mod and 4 ms in all per server
step; the rest wait for the next step, never dropped. In this version hops land only on a server world's tick (a
dedicated server, or a hosting client's server world): with no live server world (the main menu, a client joined to a
remote server) a hop is refused at once, and the hops still pending when that world stops complete as cancelled.

Kiln.Host uses the table only when it belongs to the same build: it checks the table's size and version and compares
the core's build stamp with the one Kiln.Api was built with. On a mismatch `tasks.pool` is unavailable to C# mods, and
`kiln.log` says why (`tasks: C# tasks are off: ...`).

The core tells the host once when a managed task is quiescent: after its last segment returned and its last tick hop
was dispatched, also for a task that was cancelled before it ever ran. Until then the host keeps the task's state and
the load context of the mod's code alive, so a mod's assemblies are never unloaded under a running task. The core
releases a managed task's record right after that notification, so ended C# tasks do not count against the owner's
1024 records. A `NextGateWindow` wait belongs to the server session it was asked in: the core arms it only while that
session is still the live one, and it never wakes in another world.

A C# exception that ends a task faults only that task (`kiln.log` names the mod and the task once). It is not a handler
fault of the mod and does not count toward the fault policy that disables a mod. When a mod unloads or is disabled, its
tasks' cancellation is signalled first; their cancellation and `finally` continuations still run on the pool afterwards.

## Exit

Once the process's exit begins, queued tasks may never start and a `done` is not guaranteed: only the exit cleanup
budget (`KilnPluginInfo.on_unload` in `kiln_api.h`) applies, and nothing waits for tasks beyond it. A mod that frees
resources in `done` must not rely on `done` at exit; free them in `on_unload`, or leave them to the process exit.

Kiln's exit steps run at the process's exit call (`ntdll!RtlExitUserProcess`), before the loader lock is taken and
while every other thread still runs:

1. Every exit, a crash's non-zero one too: new submissions are refused (`SHUTDOWN`). One lock-free store, nothing else.
2. A normal exit (code 0): every owner is sealed, queued tasks are cancelled and running bodies are signalled
   (`cancelled()` returns 1).
3. Kiln's own services' exit handlers run: `kiln.engine` closes its write gate for good, so no task can take it any
   more.
4. The pool parks: from here no worker starts another body or `done`. The exiting thread waits up to 500 ms for the
   bodies already running. A body still running after that is named in `kiln.log` (`kiln.tasks: parked at exit; ...`);
   it can no longer take the engine's gate and ends with the process.
5. Then the plugins' `on_unload` handlers, where they are on.

The order is the same on every platform. On native Windows the plugins' handlers run only with `kiln.json`
`"unloadAtExit"`: `"on"`; with `"auto"` (the default) the link runs Kiln's own steps 1 to 4 there. Under Wine and Proton
(Linux servers, the Steam Deck) every handler runs, after the park: tasks no longer run while the plugins' handlers do,
which is new in this version (before, every handler ran last loaded first with the pool sealed but not parked).
`"off"` places no exit link at all, anywhere; the process exit then simply ends the pool's threads.

An exit called from a `DllMain` (the loader lock is held) does steps 1 and 2 only: nothing waits under the loader
lock. A body that ends the process itself is never waited for. Once the pool is parked, `drain_owner` does not wait:
it reports what is left (bodies still running, `done`s that will not be delivered). So don't wait for your own tasks in
`on_unload`.

When the exit call is never reached (a killed process, a crash that does not go through `ExitProcess`,
`"unloadAtExit": "off"`, or a link that could not be placed), the OS ends every thread at once. What still holds:

- the engine's gate is already closed if the server world stopped first (`KILN_WORLD_STOPPING`);
- engine reads are guarded copies, so a read of freed memory fails instead of crashing;
- nothing in Kiln's `DLL_PROCESS_DETACH` takes a pool lock or waits on a thread, so Kiln does not hang the exit;
- no `done` is promised, as above.

Tests: `native/core/tests/test-unload.sh` (where the link is placed), `test-unload-sequence.sh` (the order, over host
stubs), `test-tasks.sh` (the park, under ASan/UBSan and TSan) and `test-exit-wine.sh` (the exit block and the pool on
the Windows code path under headless Wine, ending in a real `ExitProcess` with tasks in flight).

The production pool lives for the whole process: it is never destroyed by a static destructor, and its shutdown comes
from Kiln's own exit path. A worker that will not stop within the exit bound is detached, never terminated.

## Faults the pool cannot contain

On native Windows a stack overflow in a native body is caught by the same structured-exception boundary as any other
fault. Under Wine (Linux servers, Proton) that boundary does not fire for a stack overflow: the worker stays stuck, the
watchdog names the task, only its owner's slot is held, and exit detaches that worker within its bound. The native
Windows behaviour is still to be confirmed on a real Windows machine.

## Diagnostics

A watchdog sweeps once a second and writes one `kiln.log` warning for a task running, or queued, for more than 30 s,
naming the owner, the task, the state and the age. It reports each task once per state (never every sweep), also a
body that outlives its revoked owner; it only reports and never stops a task. When a worker kind loses its last
worker (a fault, then its replacement could not start) `kiln.log` says so once, with the number of queued tasks that
wait, and once again when a retried start brings a worker back. While a task runs its worker's thread description is
`<mod>/<task>`.

Since issue #49 the pool is also visible where Kiln already reports performance and crashes:

- **`tick-stats.jsonl`** (server world's line only): an optional `"tasks"` object appended after the existing
  fields — the window's id, span and age; the pool's workers, LongRunning workers and deepest queue; one row per
  owner *instance* (a reload starts a new row, never mixed with the old one; at most 64 rows, then
  `"owners_truncated":true`) with queued/running current and window max, completed/faulted/cancelled in the
  window, `cpu_ms` and `wall_ms`, the window's longest segment and its name, and the watchdog, queue-full and
  blocked-unload counts; the tick hops' apply account (applied, deferred, cancelled, its time, the budget stops,
  the owners served and the backlog); and hops refused. The object is absent when the pool does not exist, so a
  line without a pool keeps its older shape byte for byte.
- **Crash reports**, native and managed alike: a `-- Tasks --` section with up to 16 running tasks (thread, mod,
  task name, handle, start, elapsed, running / suspended / on-tick) and the last 16 fault summaries. A managed
  report reads the same store through five members appended to the Kiln-internal host table (owner count, owner
  row, running row, fault row, totals); a supplier that throws still leaves a written report.

Both read an immortal, fixed-size, lock-free store (64 owner slots, 64 running entries, 16 faults;
`native/core/src/tasks_stats.{h,cpp}`): every field is atomic, compound updates publish through a per-slot
seqlock, and readers copy with a bounded retry and then write `(task snapshot unavailable)`. Collecting never
blocks the tick, never takes the pool lock and allocates nothing on a crash path. A segment's cost is two clock
reads (wall, and the executing thread's CPU) and a few relaxed atomic updates.

CPU time on Windows comes from `GetThreadTimes` (kernel + user): the units are exact but the values advance only
at the scheduler quantum (~15.6 ms), so a segment's `cpu_ms` is quantum-granular — usually 0 for a short segment,
a whole quantum for one that crosses a boundary. (`QueryThreadCycleTime` advances continuously but has no
documented frequency to convert its cycles to time.) Wall time excludes nothing; CPU excludes the time the
thread spent awaiting IO, because the thread was not executing then.
