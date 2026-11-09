# ECS programs and system hooks

An ECS program runs inside the game's scheduler on a worker thread. Declare its access in `kiln.programs.json`, next
to `kiln.mod.json`. The native world service reads this file before the server creates its scheduler. Bind the callback
in your normal `OnLoad`. `Kiln.Sdk` copies the file and includes it in `KilnPack` and `KilnDeploy`.

```json
{
  "format": 1,
  "programs": {
    "pulse": {
      "otherRead": ["Health"],
      "otherWrite": ["Health"],
      "eventsIn": ["TestEvent2", "TestEvent1"],
      "eventsOut": ["TestEvent1"]
    }
  }
}
```

```csharp
private IDisposable? program;
public override void OnLoad()
{
    program = EcsPrograms.Bind("pulse", context =>
    {
        foreach (var bytes in context.ReadEvents("TestEvent1"))
            Interlocked.Increment(ref eventsSeen);
    });
}
public override void OnUnload() => program?.Dispose();
```

See [WorldPrograms](../../../mods/samples/WorldPrograms/) for an event round trip. `EcsPrograms.Available` and
`SupportsOtherWrites` report native capabilities. An undeclared key, an unknown component/event, or a late declaration
fails explicitly. The declaration remains inert until its mod binds it. Disabled mods and client-only mods do not
reserve server programs; profile entries take precedence over drop-ins, including disabled profile entries.

A mod may declare eight programs in one file of at most 64 KiB. The file uses strict JSON: duplicate keys, unknown
fields and repeated access names are refused. The schema is [kiln-programs.schema.json](../../../schema/kiln-programs.schema.json).
`kiln lint` discovers it alongside the manifest. The native service also checks engine limits (template slots 8,
read/write slots 42, other-template slots 9, other accessors 92, total event inputs/outputs 24). Programs share the
world service's 32-slot limit and the game's remaining scheduler capacity; refusals appear in `kiln.log`.

Access lists are `tpl`, `tplOptional`, `read`, `readOptional`, `write`, `writeOptional`, `singletonsRead`,
`singletonsWrite`, `otherTpl`, `otherRead`, `otherWrite`, `with`, `without`, `anyOf`, `eventsIn`, `eventsOut`. Names
are engine short names; singleton names may contain `::`. Optional components return no data. Filters only select
entities. `flags` is an optional array of `system`, `init`, `hostOnly`, `exclusive`, `chunked`, `pinned`, `barrier`;
its default is `system, hostOnly`. A system program gets entity zero and cannot declare entity query lists. For a
per-entity query, supply flags such as `["hostOnly"]` and at least one required component/filter.

`context.TryRead<T>` and `TryWrite<T>` access the current entity's declared slots. `TryReadOther<T>` and
`TryWriteOther<T>` use declared accessors for another entity. Sizes must match the game's reflected component size.
Writes mark the game's replication/save tracker. `otherWrite` is passed to the engine's descriptor before it builds
the dependency plan. A component may appear in both other-read and other-write lists. Use `QueueAdd` and
`QueueRemove` for changes to the component set at the end of the step.

`ReadEvents` returns copies of every event of one declared input type. Multiple types work in one program, regardless
of declaration order. `EmitEvent` copies an exact event payload into a declared output stream and stamps `GameEvent`
timestamps. The payload must have the game's reflected size and be smaller than 256 bytes. A full event buffer
returns false. Layouts depend on the game build; use reflected types or structs whose sizes you have checked.

Callbacks cannot call ordinary tick-thread services. Use thread-safe counters or `Context.Scheduler.Post` to hand work
to the tick thread. The program context is a `ref struct`; component data is valid only during the callback. An
exception disables that callback. Dispose on the tick thread. Mod unload drains callbacks before freeing their roots.
The reserved scheduler entry remains, so the same declaration can bind after hot reload. Add/change declarations,
restart the server, then reload code. Editing the file while the server runs does not change its scheduler.

## Hooks around game systems

```csharp
hook = GameSystems.Hook("combat_experience_source",
    before: () => Interlocked.Increment(ref beforeCount),
    after: () => Interlocked.Increment(ref afterCount));
```

The callbacks run around the real update once per slice, on game worker threads. Dispose on the tick thread; mod
unload removes and drains the hook. A second hook for the same update function fails, including systems which share
that function. At most 64 distinct update functions can be hooked during one process. After disposal the native
trampoline remains passive until process exit; rebinding the same function reuses it. Retirement drains user
callbacks, and an old invocation cannot call a new owner's after callback. The original update remains valid while
another world retires the callbacks. Return promptly: unload waits for a callback currently running.
Hook callbacks observe the original system's access rights; they cannot add scheduler permissions.
`GameSystems.Profile` measures the existing runner independently. EMF's `hook_program` uses these same native hooks
and receives the real program context. Missing symbols or a conflicting runtime make hooks unavailable.

The dedicated-server probes cover scheduler registration, other-entity health readback, both input types, spawn-time
components, hook/profiler counts and callback removal. A joined client is still needed to confirm replicated visual
changes and client/prediction-world behaviour. Managed declarations currently target the server world only.
