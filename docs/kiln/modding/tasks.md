# Tasks: heavy work off the server's tick

A mod's tick handlers, event handlers and commands run on the server's tick thread, and every millisecond they take
comes out of the frame: the dedicated server steps its world about 24.5 times a second (≈41 ms a step). A Kiln task
moves the heavy part of a mod's work (a scan, path finding, a big calculation, file or network IO) onto Kiln's own
worker threads, and hands the result back to the tick in small pieces.

This page is the mod author's guide. The pool's contract (limits, states, configuration, exit) is in
[../services/tasks.md](../services/tasks.md); the world reads are in
[../services/world.md](../services/world.md#world-reads-from-task-threads). The example used throughout is
`mods/samples/FetchNearby`: `/fetch <item> [count]` brings items from the containers near a player into the player's
inventory, with the search for containers in a task.

## When to use a task

Use one for work that takes long and needs little of the game while it runs:
- a search over many entities (with the [world reads](#reading-the-world-from-a-task));
- path finding, planning, sorting, compressing, hashing;
- reading or writing files, talking to a web service.

Don't use one for:
- a few microseconds of work: the hop to a worker and back costs more;
- work made of tick-only calls (moving items, spawning, chat): it has to run on the tick anyway;
- something that must happen in this very step: a task's result comes back in a later step.

Tasks run on a dedicated server and on a client that hosts a game. On a client joined to someone else's server they
can still compute, but they cannot hop back to a tick (below).

## Starting a task

`Context.Tasks.Run` starts the work on one of Kiln's workers and returns a `KilnTask` at once:

```csharp
var scan = Context.Tasks.Run("chest-scan", ctx => RebuildIndex(data));                     // KilnTask
var path = Context.Tasks.Run<Route>("path", async ctx => await PlanAsync(from, to, ctx.Cancellation));  // KilnTask<Route>
```

- **The work** is a lambda or method that gets a `KilnTaskContext`. It can be synchronous (with no result) or `async`.
  `Run<T>` with an `async` lambda that returns a `T` gives a `KilnTask<T>`; a synchronous lambda's return value is
  discarded.
- **The name** is what `kiln.log`, the watchdog and the tick statistics show for it.
- **`KilnTaskOptions`:**
  - `new KilnTaskOptions(LongRunning: true)` gives work that runs for seconds or longer, or loops until it is
    cancelled (a queue the mod serves until it unloads), a dedicated thread, so it doesn't hold one of the shared
    workers.
  - `Priority` (`KilnTaskPriority.Low`, `Normal` or `High`) orders the mod's own queued tasks; it never gives a mod more
    than its share.
- **Limits.** A mod runs at most 2 tasks at once on the shared workers (a task waiting at an `await` doesn't count),
  holds at most 1024 tasks that have not ended (`KilnTaskQueueFullException` past that), and runs at most 2
  long-running ones (8 for all mods together by default).

A `KilnTask` is awaitable. It ends `Completed`, `Faulted` (`Fault` holds the exception) or `Cancelled`.

**Results.**
- `await task` gives the result, or throws its fault or `OperationCanceledException`.
- `KilnTask<T>.Result` throws until the task has ended.
- `Wait()` is refused on the tick and inside a task: the tick never waits for a task. Await it instead.

**Cancellation.**
- `task.Cancel()` asks a task to stop. It reaches the work through `ctx.Cancellation`, which a long loop should check
  (`ctx.Cancellation.ThrowIfCancellationRequested()`). An `OperationCanceledException` of that token ends the task
  `Cancelled`.
- Kiln cancels a mod's tasks itself when the mod unloads. When the server world stops, it cancels the tasks waiting
  for a hop, a post or `NextGateWindow` in that world; a task that is only computing runs on, and its next hop throws
  `KilnUnavailableException`.

**Faults.** An exception that ends a task faults only that task: `kiln.log` says `task '<name>' faulted: ...` once, under
the mod's name. It does not count toward the [fault policy](faults.md) that disables a mod.

**Starting and replacing.** In the example, the command handler starts one search per player, and a new request
replaces the old one:

```csharp
// one fetch per player: a new one replaces the last
if (_running.Remove(player, out var last)) last.Cancel();
var search = Context.Tasks.Run("fetch-search", ctx => SearchAsync(ctx, player, at, info.Id));
_running[player] = search;
_ = DeliverAsync(player, search, info.Id, info.Name, count);
```

## What a task may touch

A task's work runs on a worker, not on the tick. What it may call:
- **Its own data, .NET and IO.** Everything that is not the game's.
- **`[AnyThread]` APIs.** Kiln marks the members that work from any thread `[AnyThread]`, for example
  `Players.All`, `Context.Tasks.Run` and `KilnTaskContext.Cancellation`.
- **The task-thread world reads.** [`WorldConcurrent`](#reading-the-world-from-a-task) is made for this.

Everything else that touches game state is the tick's: members marked `[TickThreadOnly]`, and the APIs documented "tick
thread" (`World.Position`, `Items.Read`, `Chat.Tell`, ...). Off the tick they throw
`InvalidOperationException`. Take that work back to the tick ([below](#getting-back-to-the-tick)).

**What may cross between the tick and a task.**
- **Values do.** Pass `EntityId`, `ItemId`, `WorldPosition`, numbers and your own records.
- **Live views of the game don't.** Spans and pointers into component memory, objects the game reuses next step, the
  `ModContext` of an unloading mod: copy what you need on the tick, then start the task with the copy. The example reads
  the player's position on the tick and hands the task the `WorldPosition`.

The analyzers check this at build time:
- [KILN101](analyzers.md#kiln101) reports a tick-thread API used in a task's work.
- [KILN105](analyzers.md#kiln105) reports blocking on a task, a `ToTick()` nobody awaits, and a world read where the
  task's scope is gone (next section).

## Getting back to the tick

There are three ways back. They all land on the server world's tick, before the mods' `Tick.Pre` handlers, in a later
step:

| | What it does | Use it for |
|---|---|---|
| `await ctx.ToTick()` | the rest of the method continues on the tick, and stays there (later awaits come back to the tick) until `await ctx.NextGateWindow()` takes it back to a worker | a stretch of tick-only work in the middle of the task |
| `await ctx.OnTick(() => ...)` | runs one function on the tick and gives its result back; the method continues on its worker | one tick-only read or call |
| `ctx.PostToTick(() => ...)` | hands a function to the tick and goes on at once, without waiting for it | telling the tick something; the task still ends only once its posts were applied |

**The budget.** Per step, each mod's hops and posts run up to 64 items or 2 ms, whichever comes first, and all mods
together up to 4 ms. What doesn't fit waits for the next step; it is never dropped. So hand back small pieces, not one
long function.

**When a hop can't happen.**
- **No live server world** (the main menu, a client joined to someone else's server): `ToTick`, `OnTick`, `PostToTick`
  and `NextGateWindow` throw `KilnUnavailableException` (`tasks.pool`) at once.
- **The task was cancelled first** (the mod unloads, the world stops): the hop never runs and the await of `ToTick`,
  `OnTick` or `NextGateWindow` throws `OperationCanceledException`; a queued `PostToTick` is dropped and the task ends
  `Cancelled`. Your `finally` blocks still run, on a worker.
- **The mod is unloading:** a new `ToTick`, `OnTick`, `PostToTick` or `NextGateWindow` call throws
  `InvalidOperationException` ("the mod is unloading").

**The example's way back.**
- It tells the player how many containers it searches with `PostToTick`, and goes on at once.
- It hops with `ToTick` to read what the containers hold, because `Items.Read` is the tick's.
- It returns its plan, and the mod's own tick code moves the items:

```csharp
// a word for the player, on the tick when it comes; the task goes on at once
var found = near.Count;
ctx.PostToTick(() => Chat.Tell(player, found == 0 ? $"no container within {Reach:0} m" : $"searching {found} container(s) within {Reach:0} m"));
if (found == 0) return [];
// what a container holds is the tick's to read
await ctx.ToTick();
var sources = new List<Source>();
foreach (var container in near)
    if (Items.Read(container)?.Count(item) is > 0 and var has) sources.Add(new Source(container, has));
return sources;
```

**Why the move isn't in the task.** Item moves are two transactions (take, then give), each answered in a later step.
Inside the task, an unload or a world stop between the two would stop the rest of the method from running on the tick
and leave the items taken but not given. The mod's own tick code awaits the task's plan instead
(`sources = await search;`), so a fetch cancelled before the plan moves nothing, and a plan that exists is carried out
on the tick.

## Reading the world from a task

`WorldConcurrent` (Kiln.World) reads entities and components from a task's worker. Every read copies the bytes and
checks the entity again around the copy, so it returns a whole value or says why not, as a `ReadStatus`:
- `Ok`: the value is whole.
- `Absent`: no such entity or component.
- `Stale`: the id names another entity now, or the world, the entity's components or the mod's load changed.
- `Unreadable`: the copy failed or the value changed during the read; try again.
- `Unavailable`: the reads don't work here.
- `SizeMismatch`: the component is smaller than the type you read it as.
- `TooSmall` and `Changed`: see the buffer rules below.
- `Busy` and `Unsupported`: see the read modes below.

An ordinary miss never throws. The reads are experimental (`KILNX001`): opt in with
`<NoWarn>$(NoWarn);KILNX001</NoWarn>` in the mod's project. The full reference is
[world.md](../services/world.md#from-c-worldconcurrent-experimental).

**Where they work.**
- **Only on the game builds Kiln has an exact engine profile for.** `WorldConcurrent.IsAvailable` says whether this is
  one; everywhere else every read is `Unavailable`.
- **Fallback.** `WorldConcurrent.OrOnTick(ctx, offTick, onTick)` is the fallback in one line: `offTick` runs at once,
  where it is called, when the reads work; `onTick` runs on the tick otherwise.
- **Statuses and buffers.** A query is complete or nothing: a buffer that is too short says `TooSmall` with the size
  it needs, and a query the world changed under says `Changed`.

The example's search, with the tick search as the fallback:

```csharp
// The containers within reach, nearest first: from this worker where the reads work, else on the tick. A
// search from the task that finds the reads gone after all says null, and the tick does it.
var near = await WorldConcurrent.OrOnTick(ctx, () => NearFromTask(at, ctx.Cancellation), () => NearOnTick(at))
           ?? await ctx.OnTick(() => NearOnTick(at));
```

The search from the task: a bounded query, then where each container stands, nearest first.

```csharp
/// <summary>The containers within reach of <paramref name="at"/>, nearest first, read from a task's worker; null when
/// the reads are not available here.</summary>
private static IReadOnlyList<EntityId>? NearFromTask(WorldPosition at, CancellationToken cancellation)
{
    var refs = new ConcurrentEntity[64];
    var query = new EntityQuery(WorldKind.Server, "Inventory") { Center = at, Radius = Reach };
    for (var attempt = 0; attempt < 5; attempt++)
    {
        cancellation.ThrowIfCancellationRequested();
        var result = WorldConcurrent.Query(query, refs);
        switch (result.Status)
        {
            case ReadStatus.Ok:
                var near = new List<(EntityId Id, double Distance)>();
                for (var i = 0; i < result.Count; i++)
                {
                    // a container that went or changed since the query is left out (Stale, Absent)
                    var where = WorldConcurrent.Read<Position>(refs[i], "CurrentTransform");
                    if (where.IsOk) near.Add((refs[i].Id, where.Value.ToWorld().DistanceTo(at)));
                }
                return [.. near.OrderBy(n => n.Distance).Select(n => n.Id)];
            case ReadStatus.TooSmall:
                refs = new ConcurrentEntity[result.Required + 16];  // more containers than places: ask again with room
                continue;
            case ReadStatus.Changed:
                continue;  // a container came or went during the walk: walk again
            default:
                return null;  // Unavailable here: the tick searches
        }
    }
    return null;
}
```

**Two read modes.**
- **`ReadMode.Stable`** (the default) copies at any time and refuses a value that changes during the copy.
- **`ReadMode.FrameQuiet`** copies only while no server frame runs. Use it when a half-written value would hurt.
  - A frame that ran during the read gives `Busy`: retry after `await ctx.NextGateWindow()`, which resumes the task
    on a worker after a later step without holding a worker while it waits.
  - On the tick itself (a tick handler, or after `ToTick`) the frame is running, so a frame-quiet read there is `Busy`
    at once. Read `Stable` on the tick.
  - Frame-quiet reads are for the server world; on a client world they are `Unsupported`.

```csharp
ConcurrentRead<Position> where;
while ((where = WorldConcurrent.Read<Position>(entity, "CurrentTransform", ReadMode.FrameQuiet)).Status == ReadStatus.Busy)
    await ctx.NextGateWindow();   // a server frame ran during the read: try after the next step
```

**Refs.**
- A `ConcurrentEntity` is a value, not a handle: nothing is pinned, and every read checks it again.
- Refs taken before your mod reloads come back `Stale` after the reload: open the entities again.

**Tasks don't write to the world.** Change it on the tick (`World.Write`, `Items.Give`, ...), with `ToTick`,
`OnTick` or `PostToTick`, or in the mod's own tick code once the task has handed over its result.

## Lifecycle: unload, reload, exit

**Unload and reload.**
1. When a mod unloads (or reloads), before its `OnUnload`, Kiln stops its tasks:
   - it takes no new task;
   - queued tasks end `Cancelled` without running;
   - running and suspended ones are signalled through `ctx.Cancellation`.
2. Their pending hops and posts never run.
3. Their `catch` and `finally` blocks still run on a worker.
4. The unload never waits for them on the tick. The mod's code stays loaded until its tasks have ended. A task still
   running 2 s later is named once in `kiln.log` (`tasks: mod <id>: task '<name>' (<state>) did not stop within 2 s;
   ...`) and the old code unloads when it ends.
5. A reload doesn't wait for the old tasks: the new instance loads at once while the old one's tasks finish beside it.

`OnUnload` may cancel the tasks it knows; the example does, to keep it visible:

```csharp
public override void OnUnload()
{
    // Kiln cancels an unloading mod's tasks anyway; cancelling them here says so in the mod's own code
    foreach (var task in _running.Values) task.Cancel();
    _running.Clear();
}
```

**Process exit** guarantees less: queued tasks may never start, and neither a task's end nor its `finally` blocks are
promised. Don't rely on a task to save state at exit; save on the tick (in `OnUnload`, or as you go).

## Diagnostics

**`kiln.log`.**
- A watchdog writes one warning for a task that runs, or waits in the queue, for more than 30 s:
  `kiln.tasks: <mod>/<task> (task 0x...) has been running for 31.0 s (the watchdog reports it once; nothing is stopped)`.
- A worker running a task is named `<mod>/<task>`, which a debugger shows; crash reports list the running tasks by mod
  and task name.

**`kiln/logs/tick-stats.jsonl`.** Once a minute, the server world's line gets a `"tasks"` object:
- **`"pool"`:** the workers, the long-running workers and the deepest queue.
- **One row per mod load in `"owners"`:**
  - `queued` and `running` (now and the window's highest);
  - `completed`, `faulted` and `cancelled`;
  - `cpu_ms` and `wall_ms`;
  - the window's longest piece of work (`longest_ms`, `longest_name`);
  - the watchdog's counts (`queued_too_long`, `running_too_long`), `queue_full` and `unload_blocked`.
- **`"apply"`:** what came back to the tick: `applied`, `deferred` (waited for a later step), `cancelled`, the time it
  took (`ms`), the budget stops and the `backlog`.
- **`"hops"."refused"`:** hops refused for want of a server world.

Compare a mod's `cpu_ms` in tasks with the Pre time it no longer spends (`pre_parts`) to see what moving the work
bought. The field list is in [../services/tasks.md](../services/tasks.md#diagnostics).

## Check it locally with the TestKit

The [TestKit](unit-tests.md) runs a mod's tasks under `dotnet test`:
- **The workers** are real threads, as in a server.
- **Hops** run in the harness's steps, before `Tick.Pre`.
- **`kiln.Tasks.Settle()`** waits for the mod's tasks and steps as they need, until all of them have ended. A task that
  never ends fails the test with a message naming it.
- **The world reads** work on the kit's fake world. A container made with `kiln.Items.AddContainer` has the game's
  `Inventory` component.
- **The fallback.** `kiln.Capabilities.Override("world.ecs_read", false)` turns the reads off, so the tick path gets
  tested too.

The example's tests are in `managed/tests/Kiln.TestKit.Tests/FetchNearbyTests.cs`, with their helpers (`Start`,
`Finish`, `Said`, `Wood`, `ChatOf`). The first one, without its last check:

```csharp
[Theory]
[InlineData(true)]
[InlineData(false)]
public void A_fetch_takes_from_the_nearest_containers_first(bool reads)
{
    var w = Start(reads);   // Ada, and wood in containers 4 m, 8 m and 30 m away; the reads switched off when !reads
    using var _ = w.Kiln;
    Assert.Equal("looking for 12 x Wood within 12 m", Said(w.Kiln.RunCommand(w.Ada, "fetch Wood 12")));
    Finish(w.Kiln, w.Ada);  // kiln.Tasks.Settle(), then steps until the moves are done
    Assert.Equal(12, Wood(w));
    Assert.Equal((0, 3, 50), (w.Near.Count(w.Wood), w.Middle.Count(w.Wood), w.Far.Count(w.Wood)));
    // the command's reply, the task's word from the tick (PostToTick), the result of the moves
    Assert.Equal(["[Server] looking for 12 x Wood within 12 m", "[Server] searching 2 container(s) within 12 m", "[Server] fetched 12 of 12 Wood"], ChatOf(w));
    // the search ran in the task through the world reads, or on the tick where they are off
    Assert.Equal(reads, w.Kiln.Run(() => WorldConcurrent.IsAvailable));
    // ...
}
```

## Common mistakes

**Blocking on a task.**
- `KilnTask.Wait()` would freeze the server on the tick and hold a worker other tasks need in a task, so it is refused
  there.
- `.Result` and `GetAwaiter().GetResult()` on a `KilnTask` never wait: they throw until the task has ended.
- The plain `Task` from `AsTask()` escapes all of this: `Wait()`, `.Result` or `GetAwaiter().GetResult()` on it really
  block. KILN105 reports `KilnTask.Wait()` and blocking on the `AsTask()` task.
- **Fix:** `await`.

**Raw threads and `Task.Run`.**
- Kiln does not track them: an unload leaves them running against unloaded code. An exception in a raw thread ends
  the process; one in `Task.Run` is seen only when the garbage collector finds it unobserved, and then counts as a
  [fault](faults.md) of the mod.
- They have no task scope, so the world reads refuse them.
- **Fix:** use `Context.Tasks.Run`.

**`ConfigureAwait(false)` in a task.**
- Code after it has left the task: it runs on a .NET thread-pool thread with no task scope, so `WorldConcurrent`
  refuses it.
- **Fix:** drop it (a task's awaits already resume in the task), or `await ctx.NextGateWindow()` before the next read.
- **KILN105** reports reads after it on every path, round loops, in `catch` and `finally`, and in your own async
  helpers.
- **Not caught:** an awaitable configured in one place and awaited in another
  (`var later = io.ConfigureAwait(false); ... await later;`), whose `false` the analyzer does not follow. Write
  `ConfigureAwait` where you await.

**A `ToTick()` nobody awaits.**
- `ctx.ToTick();` on its own hops nothing: the code after it still runs on the worker. KILN105 reports it.
- **Fix:** `await ctx.ToTick();`.

**Holding game objects across threads.**
- A component's bytes, a list Kiln reuses, or a mod's `ModContext` captured on the tick and read later in a task can
  change or go under it.
- **Fix:** copy values on the tick. In a task, read through `WorldConcurrent`, which copies and checks.

**Long work in one hop.**
- A hop or post is tick time: a 30 ms `OnTick` costs the same frame the task was meant to save, and it blows the
  2 ms budget.
- **Fix:** do the work on the worker and hand back only the result.
