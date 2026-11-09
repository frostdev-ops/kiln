# ECS world programs (dev.20+)

Use this only when ordinary tick events and service APIs cannot express the work. `EcsPrograms` is in `Kiln.Api`, backed
by `kiln.world` v2. `Available` checks boot declaration/binding support; `SupportsOtherWrites` checks tracked writes to
another entity. This is staged for dev.20, not a claim about released dev.19 or real-client replication.

Ship strict JSON at the mod root, `kiln.programs.json`; Kiln.Sdk copies it for build, deploy and pack:

```json
{ "format": 1, "programs": { "count": {} } }
```

The native service reserves this scheduler declaration before worlds finalize. Bind that key in `OnLoad`:

```csharp
private IDisposable? _program;
private int _updates;
public override void OnLoad()
{
    if (!EcsPrograms.Available) return;
    _program = EcsPrograms.Bind("count", _ => Interlocked.Increment(ref _updates));
}
public override void OnUnload() => _program?.Dispose();
```

Here the default is a host-only system program (one worker callback per step, no entity query). A tick handler can read
`Volatile.Read(ref _updates)` to report it. Do not log or call tick-thread game services from every worker callback.

Declarations support `tpl`, `tplOptional`, `read`, `readOptional`, `write`, `writeOptional`, `singletonsRead`,
`singletonsWrite`, `otherTpl`, `otherRead`, `otherWrite`, `with`, `without`, `anyOf`, `eventsIn`, `eventsOut`, and `flags`.
For an entity query, specify `flags: ["hostOnly"]` and a required query list; do not retain the default `system` flag with
an entity query. Match real component/event names and exact unmanaged layouts from the installed API/catalog; never guess
sizes or copy private engine offsets.

Inside the callback, `TryRead<T>` / `TryWrite<T>` use declared current-entity access; `TryReadOther<T>` /
`TryWriteOther<T>` use declared other-entity access. Writes mark the game's replication/save tracking. `ReadEvents(name)`
returns payload copies for each declared input; multiple event inputs work. `EmitEvent(name, bytes)` uses a declared
output with the exact reflected payload size (less than 256 bytes), stamps GameEvent timestamps, and returns false when full. `QueueAdd` / `QueueRemove` apply at the end of the step. A context/component span
cannot escape its callback. Send ordinary service work back with `Context.Scheduler.Post` and recheck lifetime/world state.

The file is strict JSON (no comments/trailing commas/duplicate keys), at most 64 KiB, 1..8 declarations per mod. Names,
access counts and system/query combinations are validated; an unknown declaration key or undeclared access fails.
Programs share a 32-slot world-service limit and the game's remaining scheduler capacity. Changing declarations needs a server restart. Dispose on the tick thread. Disposing/unloading drains bound callbacks and makes the reserved entry
inert; hot reload may bind the same declaration again, not add a new scheduler slot. The old load's callback must never
be reused.

Native program hooks and spawn-time components are separate APIs; this does not make ordinary service calls safe on
worker threads or add client-world EMF programs. Wiki: [world programs](https://frostdev.io/kiln/wiki/world-programs).

## Hooks around game systems

`GameSystems.Hook(name, before: callback, after: callback)` observes a real system update once per slice, on game worker
threads. Dispose on the tick thread; unload removes and drains the hook. A second hook for the same update function fails,
including differently named systems sharing that function. A hook has only the original system's access rights: it cannot
add scheduler permissions. `GameSystems.Profile` measures the existing runner separately. Missing symbols or a conflicting
runtime can make hooks unavailable. A callback exception disables the callback; check the log.
