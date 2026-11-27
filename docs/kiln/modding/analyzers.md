# Analyzers: KILN1xx

Kiln.Sdk runs Roslyn analyzers over every mod. They catch the mistakes that the server only shows at run time:
- a tick-thread API called from another thread;
- a mod that never unloads cleanly;
- a blocked game thread;
- an API the mod has not opted into;
- a refusal nobody reads, and a capability nobody asked for ([contract.md](contract.md)).
- an `OnBoot` that never runs, or that calls what does not exist before the game is entered.

The warnings appear in `dotnet build` output and live in the editor (C# Dev Kit, Rider, Visual Studio). Ten rules
come with code fixes.

| Id | Default | What | From |
|---|---|---|---|
| [KILN101](#kiln101) | warning | tick-thread API called off the tick thread | the services' `Services.RequireTickThread` checks (generated table) |
| [KILN102](#kiln102) | warning | `async void` method or lambda | faults.md, lifecycle.md, testing.md |
| [KILN103](#kiln103) | warning | process-wide event handler never removed | lifecycle.md "Hot reload" |
| [KILN104](#kiln104) | warning | mod object stored in process-wide state | lifecycle.md "Hot reload" |
| [KILN105](#kiln105) | warning | blocking call on the game thread | testing.md, lifecycle.md |
| [KILN106](#kiln106) | warning | thread, timer, watcher, socket or subscription never released | lifecycle.md "What a mod must still release itself" |
| [KILN107](#kiln107) | **error** | advanced API (hooks, symbols, memory) without `KilnAdvanced` | `HooksGate.RequireAdvanced` (generated table) |
| [KILN108](#kiln108) | warning | Kiln service used but not listed in `KilnServices` | mdk.md |
| [KILN109](#kiln109) | warning | client-world `EcsPrograms.TryBind` called off the game's main thread (since 0.2.0-dev.29) | services/world.md |
| [KILN120](#kiln120) | warning | a Kiln result (`IKilnResult`) thrown away | contract.md, "The error rule" |
| [KILN121](#kiln121) | warning | a `[RequiresCapability]` member used with nothing that asks for its row | contract.md; the attributes and the guard table (generated) |
| [KILN122](#kiln122) | warning | registry points declared without a real OnRegistry override (API 0.2.17) | registries/README.md |
| [KILN123](#kiln123) | warning | zero, Manual or unknown automatic registry flags (API 0.2.17) | registries/README.md |
| [KILN124](#kiln124) | info | an old data path (data pack, resource patch, `ContentEntry`/`GameDataEntry`) a registry field covers; a fix for one-to-one data pack values (since 0.2.0-dev.32) | the generated hint table (registrygen) |
| [KILN125](#kiln125) | info | Native-tier code: a hook or a memory write by address, a hooked game function a registry field covers (since 0.2.0-dev.32) | the generated hint table (registrygen) |
| [KILN199](#kiln199) | **error** | the value type of `Replication.Extension<T>` outside the rule (since 0.2.0-dev.31) | services/replication.md "Sidecars" |
| [KILN194](#kiln194) | **error** | a path string the game doesn't have | the local catalog |
| [KILN195](#kiln195) | warning | a config name no program of the game declares | the local catalog |
| [KILN196](#kiln196) | warning | writing an identity field or a name | the local catalog |
| [KILN197](#kiln197) | **error** | a typed field on an entry of another section | the code |
| [KILN110](#kiln110) | **error** | bad or duplicate block name | blocks.md |
| [KILN111](#kiln111) | warning (info for a game-style name the local catalog lacks) | a name that is no block, or the wrong kind of block | blocks.md |
| [KILN112](#kiln112) | **error** | `clients` too low for the mod's blocks | blocks.md |
| [KILN034](#kiln034) | **error** | an `OnBoot` that never runs (no `"boot"` in kiln.mod.json), or the run-time `Blocks.Define` in boot code | manifest.md, chapter 08 |
| [KILN035](#kiln035) | warning | a tick, world or service API in boot code (`OnBoot`, `[BootThreadOnly]`) | the tick, service and boot tables (generated) |

Other KILN codes:
- **KILN0xx** come from the SDK's MSBuild task (manifest checks, mdk.md), except KILN034 and KILN035 above: Roslyn rules
  of Mod.OnBoot, numbered as the design of the boot phase names them (the MSBuild codes are KILN001 to KILN032, and
  KILN036 since 0.2.0-dev.29: a `[Query]` struct kiln.world cannot reserve, from the `KilnPrograms` step,
  [services/systems.md](../services/systems.md#programs-of-your-own-systemsdefine)).
- **KILN190** to **KILN193**, **KILN198** are the game-id generator's notes (catalogs.md).
- **KILN170** to **KILN174** are the `[ImpactNative]` generator's (Kiln.ImpactNatives, [impact.md](impact.md), since
  0.2.0-dev.29): see [below](#kiln170). **KILN036** (error) and **KILN037** (warning) come from its build task, which
  writes the generated typed entries into the output's `impact/natives.json`: KILN036 when the assembly, kiln.mod.json or
  natives.json cannot be read or written, KILN037 when a hand-written entry of the same name is replaced.
- **KILN180** to **KILN182** are the typed-hook generator's warnings (Kiln.Sigs, [../services/hooks.md](../services/hooks.md#typed-hooks-and-calls-detour_typed-kilnsigs-since-020-dev29)): a descriptor of the mod's sigdb fragment it cannot use (KILN180, with the reason: an unknown type, a struct return over 8 bytes without `sret`, more than 20 positions, two descriptions of one symbol that differ, two symbols with one class name), descriptors in a project without Kiln.Hooks (KILN181), a sigdb file that is not JSON (KILN182). A descriptor it reports is not generated.
- **KL** codes are the data-file linter's (`kiln lint`).

## KILN122

`[RegistryEntryPoints(...)]` needs a real `Mod.OnRegistry(RegistryContext)` override (an inherited override counts).
A hidden `new` method does not count. Kiln reads this constant metadata without executing mod code and refuses a declaration
without an author. Remove the attribute or implement the override. Since API 0.2.17.

## KILN123

Automatic registry points are a nonzero combination of GameStart, ModLoad, WorldLoad and HotReload. Zero, unknown bits and
Manual are invalid; use explicit `Registry.Change` for Manual transactions. Without the attribute an override defaults to
GameStart. The Host repeats this validation when loading a mod. Since API 0.2.17.

## KILN124

A path that names the game's own member, where a registry field covers that member. Reported on a data pack's key
(`content/*.json`: `"items": {"Wood": {"maxStackSize": 500}}`; a blocks file's `items` or `recipes` are typed blocks under
Kiln's names, never reported), a resource patch's `set` key (`blocks/*.json`, or a constant
key of `ResourcePatchBlock.Set` in `OnBoot` code, in the initializer or as `block.Set["path"] = v` on a local made with a
constant `Type`), and a constant path given to `ContentEntry` / `GameDataEntry` (`Content.Items.Get(x)!.Set("maxStackSize", 500)`):

```
info KILN124: 'maxStackSize' of items is the registry field items.maxStack: edit items.maxStack instead (Kiln.Registry, or a data pack's "registry" section), and Kiln keeps it working across game updates
```

**Why.** The path is the Advanced tier: it works while the game's member names hold. The registry field is the Stable tier:
Kiln maps it onto every game build it proves ([tiers.md](tiers.md)). The old path keeps working; this is a hint, never an
error.

When several registry fields name the member, the hint names the one that maps it one to one, else the one whose own member it
is: `itemLevelRange.minLevel` is `items.levels.min` (the armor and power formulas only read it).

**Fix.** Where the field holds the same value one to one (a whole number, a number, a bool or a time, not a formula or an
enum whose values have other names), the value is plain or `{"set": v}`, and the entry is one entry named by its debug name
(not a glob, not an item number, no `@where` or other condition, only the letters, digits, `_`, `.` and `-` a Kiln id has),
the code fix moves it from the old section into the pack's `registry` section as one alter edit
(`"registry": {"alter": [{"registry": "items", "select": {"id": "Wood"}, "field": "maxStack", "op": 500}]}`, the section's
own form, schema/registry-section.schema.json; a registry selector reads an old debug name as `game:<name in lower case>`).
It joins an existing `alter` list, and offers nothing when the section already edits that field of that entry or is not a
registry section. A relative op stays where it is: an old section's op works on the game's value,
the registry's on the value below it. A registry edit is a layer above every old pack (D1), so another mod's old pack no longer
wins over this value. Code (`ContentEntry.Set`) has no automatic fix: a registry change is a transaction on the world thread
(`Registry.Change`) or an `OnRegistry` edit, not a code layer.

**Where the table comes from.** `RegistryHints.g.cs`, generated by `tools/registrygen/registrygen.py` from the schemas and
`regmap/common` (each registry's entry type and its rows' member paths), so a new registry is in the hints once its files
are. Nothing is kept by hand.

## KILN125

Native-tier code ([tiers.md](tiers.md)): `Hooks.Mid` / `Hooks.Detour` with an address, `Memory.Write` /
`Memory.Patch`, `Marshal.Write*` and `Marshal.Copy` into an address the code did not allocate itself (a local or field from
`Marshal.AllocHGlobal` is left alone). A hook by a symbol's name is not a hook by address: `Hooks.Detour("my-mod/AltarLimit", ...)`,
and an address `Symbols.Get("name")` or `Symbols.TryGet("name", out var a)` found, are reported only when the function is one a
hooks registry's field covers (the generated table reads the field's sigdb function from its regmap row), and then the message
names the field. An address the mod computes (`base + rva`, a pattern of its own) is.

**Why info.** These keep working until a game update moves the code or the memory; then only the mod can fix them. Kiln's
registries and services cover much of the game's data and rules and keep working across updates.

## How it ships
- **Package mode:** `Kiln.Sdk` carries the analyzers in `analyzers/dotnet/cs/`:
  - `Kiln.Analyzers.dll`: the analyzers, compiler-only references;
  - `Kiln.Analyzers.CodeFixes.dll`: the fixes, IDE only.

  `Sdk/Kiln.Analyzers.targets` adds them to every mod.
- **In the Kiln repository:** mods that import `managed/Kiln.Sdk/Sdk/*` get the analyzer projects as
  `OutputItemType="Analyzer"` references.
- The analyzers read these build properties: `KilnServicesList` and `KilnFeaturesList` (the SDK writes `KilnServices`
  and `KilnFeatures` comma-separated there, because `.editorconfig` cuts values at `;`), `KilnAdvanced`, `KilnSide`,
  `KilnClients`, `KilnModId`, `KilnApiVersion` and `KilnBoot` (KILN034: `true` or `required` writes `"boot"` into the
  generated kiln.mod.json). `Sdk/Kiln.Analyzers.targets` passes each of them to the compiler
  (`CompilerVisibleProperty`); a property it does not pass reads as unset, and a test of Kiln.Analyzers checks that
  every property a rule reads is passed.
- They also read the project's own `kiln.mod.json`, which the SDK passes as an AdditionalFile.
- Projects that do not use Kiln.Sdk get no analyzers and no diagnostics.
- Turn them off with `<KilnAnalyzers>false</KilnAnalyzers>`.

## Configuring and suppressing
```ini
# .editorconfig next to the project
[*.cs]
dotnet_diagnostic.KILN105.severity = error      # make a rule stricter
dotnet_diagnostic.KILN106.severity = none       # or switch it off
```
One place:
- `#pragma warning disable KILN101` / `#pragma warning restore KILN101`;
- or `[SuppressMessage("Kiln.Threading", "KILN101", Justification = "...")]`.

Categories:
- `Kiln.Threading`: 101, 102, 105;
- `Kiln.HotReload`: 103, 104, 106;
- `Kiln.Manifest`: 107, 108;
- `Kiln.Contract`: 120, 121;
- `Kiln.Boot`: 034, 035;
- `Kiln.GameData`: 194 to 197. These ship with the game-id generator, so `<KilnGameIds>None</KilnGameIds>` turns them off.
- `Kiln.Tiers`: 124, 125 (info: hints toward the stable tier).

## Where the API facts come from
KILN101 and KILN107 do not use a hand-kept list. `managed/Kiln.Analyzers/tools/gen-apis.sh` builds Kiln.Api,
Kiln.Testing and every service, then reads their sources with Roslyn (`tools/ApiScan`). It writes
`src/Kiln.Analyzers/KilnApis.g.cs`, which is committed:
- **Tick thread:** every public member that reaches one of these, directly or through calls inside the same
  assembly:
  - `Services.RequireTickThread`;
  - `Game.AssertTickThread`;
  - `if (!….IsTickThread) throw …`.

  A method that branches on `IsTickThread` any other way dispatches itself (for example, it posts when off the tick
  thread), so it does not inherit its callees' requirement. 328 members.
- **Advanced:** every public member that reaches `HooksGate.RequireAdvanced` (Kiln.Hooks: `Hooks.Detour/Mid`,
  `Symbols.*`, `Memory.*`, `Constants.*`). 17 members.
- **Services:** the folders under `managed/services/` (22).
- **Capability guards** (KILN121): the public static bool properties that answer what `Capabilities.Is("row")`
  answers, with their rows. See [KILN121](#kiln121), "Properties that ask the same question". 54 properties.
- **Boot** (KILN035): `BootWorks`, the public members with a boot path of their own (they return early while the
  boot phase is open: `Config.Register`), which KILN035 leaves alone; `BootRefused`, the public members that throw
  while the boot phase is open (directly or through calls inside the same assembly, not through a member with a boot
  path: a `ConfigFile.Changed` handler), which KILN035 reports.

Run the script after changing Kiln.Api, Kiln.Testing or a service:
`KILN_OUT=… managed/Kiln.Analyzers/tools/gen-apis.sh`.

The analyzers also honour attributes, matched by name in namespace `Kiln`, on a member or its type:
`TickThreadOnlyAttribute` (KILN101, KILN035), `AdvancedApiAttribute` (KILN107) and `BootThreadOnlyAttribute`
(KILN034, KILN035). Kiln.Api defines them in `ThreadingAttributes.cs`.

## Where code runs (KILN101, KILN105)
The analyzers decide which thread runs a piece of code.

**Tick thread:**
- `OnLoad` and `OnUnload` overrides;
- `[Subscribe]` and `[KilnTest]` methods;
- `Context.Tick.Pre` and `Post` handlers;
- `Context.GameLog.Line` handlers;
- `Scheduler.Post` work;
- `Events.On` and `Context.Messages.On` handlers;
- command and chat handlers: `[Command]` methods, handlers given to `Commands.Register` and to
  `LiteralNode.Executes` / `ArgumentNode.Executes`, and `Chat.Received` handlers (Kiln.Chat runs every command,
  from chat, the console folder or a remote tool, on the tick);
- the delegate handed to a task's `ctx.OnTick(...)` or `ctx.PostToTick(...)`, and task code after a tick hop (below);
- the `onTick` delegate of `WorldConcurrent.OrOnTick(ctx, offTick, onTick)`. Its `offTick` delegate runs at once,
  where `OrOnTick` is called, so it takes the thread of the code around it.

Method groups count too: `Context.Tick.Pre += OnPre` makes `OnPre` tick-thread code.

**The game's main thread** (a client's: it steps the client world and builds the game's UI):
- `Client.Tick` handlers and `Client.Post` work, and code after `await Client.NextTick()`;
- callbacks handed to a parameter or an event marked `[ClientMainThread]` (`Ui.Values.SubscribeSlots`, ...);
- a Kiln task's client-world hops (below).

**The render thread:** callbacks marked `[RenderThread]`, and a draw callback that takes a `UiDraw`.

**A game thread:**
- any other callback handed to a Kiln API or a Kiln event: on a server, the tick thread;
- on a client, the main, UI or render thread.

KILN105 reports blocking calls here. KILN101 does not report tick-thread APIs here, because the exact thread is not
known.

**Off the game threads:**

| Where | Code |
|---|---|
| thread pool | `Task.Run`, `Task.Factory.StartNew`, `Task.ContinueWith` (without a `TaskScheduler` or `ExecuteSynchronously`), `ThreadPool.*`, `Parallel.*` |
| own thread | `new Thread(…)` |
| timer thread | `System.Threading.Timer`, `System.Timers.Timer.Elapsed` |
| BCL events | `FileSystemWatcher`; `Process` output and exit; `AppDomain.ProcessExit` and `UnhandledException`; `TaskScheduler.UnobservedTaskException`; `Console.CancelKeyPress`; `SocketAsyncEventArgs.Completed`; `BackgroundWorker.DoWork` |
| after `ConfigureAwait(false)` | everything after `await x.ConfigureAwait(false)`, up to the next `await Context.Scheduler.NextTick()`. The same for the body of `await foreach (… in xs.ConfigureAwait(false))` and the code after it, and for the code after `await using (x.ConfigureAwait(false))` (or `await using var`). A plain `await` off the tick thread does not come back: there is no Kiln synchronization context there. |
| Kiln task worker | the delegate handed to `Context.Tasks.Run(...)` (a lambda, a method group, or a local function handed out that way), until a tick hop takes it to the tick |

**Inside a Kiln task** the thread is followed through the function's own control flow (branches and joins, loops,
early returns, `try`/`catch`/`finally`, `using`):
- `await ctx.ToTick()` lands where the task belongs: where its `Context.Tasks.Run` was called. Called from tick code, on
  the tick; from main-thread code (`Client.Tick`, `Client.Post`, a `[ClientMainThread]` callback), on the game's main
  thread; inside a task, in its parent's world (inside a hop, that hop's world); from anywhere else, on the tick, as Kiln
  does. `ctx.ToTick(WorldKind.Client)` and `ctx.ToTick(WorldKind.Server)` land where their constant says; a world that
  is not a constant is some game thread. The delegates of `ctx.OnTick(...)` and `ctx.PostToTick(...)` follow the same
  rule.
- `await ctx.ToTick()` and `await Context.Scheduler.NextTick()` land on every path that reaches the call after them. From there the code stays on the tick: its later plain `await`s come back to the tick too. It
  leaves the tick at `await ctx.NextGateWindow()` (back to a worker) or at an `await …ConfigureAwait(false)`
  (the thread pool), until it hops again. A call after an `if` where only one branch hopped is still off the tick.
- The delegate of `ctx.OnTick(...)` / `ctx.PostToTick(...)` runs on the tick. Awaiting the `OnTick` result does not
  hop the caller: the code after it resumes in the task, where it was.
- Hops are recognised by the type of what is awaited, the real `Kiln.KilnTaskContext` / `Kiln.Scheduler` awaitables:
  a user method that happens to be named `ToTick` or `NextGateWindow` hops nothing, `Scheduler.NextTick` still
  counts, and a hop kept in a variable (`var hop = ctx.ToTick(); await hop;`) counts too.
- Awaiting your own async helper does not move the caller, even when the helper hops inside: the caller's `await`
  resumes in the task where it was.
- A local function called from a task body runs where its call has got to, so it is judged by its calls. An
  ordinary helper method called from the body is not followed: when the rule cannot tell, it stays quiet.

**Not reported (unknown):**
- lambdas the mod stores or hands to its own helpers;
- calls through ordinary helper methods (above), and code whose thread no path settles;
- code under `if (Game.IsTickThread)` or `Client.IsClientThread`.

Lambdas passed to library calls that run them at once (LINQ, `List.ForEach`) take the thread of the code around
them.

---

<a id="kiln101"></a>
## KILN101: tick-thread API called off the tick thread
**What.** A Kiln API that throws `InvalidOperationException("… must be called on the tick thread (use
Scheduler.Post)")` is called from code that runs on another thread.

**Why.** Game state is single-threaded. Every world, combat, chat, spawn and content call checks its thread
(`Services.RequireTickThread` in `managed/Kiln.Api/Services/Services.cs`). Off the tick thread the call throws, or
it would race the game's own step. lifecycle.md, faults.md: such an exception outside the tick thread is not a mod
fault Kiln can count. It is a `managed-unhandled` crash of the whole server.

```csharp
public override void OnLoad()
{
    Task.Run(async () =>
    {
        var json = await Http.GetStringAsync(url);
        Chat.Broadcast(json);                          // KILN101: runs on a thread-pool thread (Task.Run)
    });
}
```
**Fix.** Post the call back. The code fix wraps the statement, or an expression-bodied lambda, as shown:
```csharp
Context.Scheduler.Post(() => Chat.Broadcast(json));
// or, in async code that is on the tick thread: just don't leave it
var json = await Http.GetStringAsync(url);             // no ConfigureAwait(false): continues on the tick thread
Chat.Broadcast(json);
// or come back explicitly
await Context.Scheduler.NextTick();
```
The fix uses `Context.Scheduler` in a `Mod`; otherwise it uses a `ModContext` or `Scheduler` field or property of
the type. No fix without one.

Inside a Kiln task the same rule reads the task's hops ([Where code runs](#where-code-runs-kiln101-kiln105)):
```csharp
Context.Tasks.Run("loot-scan", async ctx =>
{
    var found = ScanNearbyContainers(area);            // a worker: compute and IO are fine here
    Chat.Broadcast("scan done");                       // KILN101: runs on a Kiln task worker (Context.Tasks.Run)
    await ctx.ToTick();
    foreach (var c in found) await Items.MoveAll(c, player);   // on the tick after the hop
    await ctx.NextGateWindow();
    Chat.Broadcast("applied");                         // KILN101: NextGateWindow took the work back to a worker
});
```
**Fix in a task.** Hop first (`await ctx.ToTick()`), or hand the call to the tick (`ctx.PostToTick(() => ...)`,
`await ctx.OnTick(() => ...)`). Those keep the work tracked by the task; the `Scheduler.Post` code fix above
also works, as ordinary tick work that the task does not wait for.

<a id="kiln102"></a>
## KILN102: async void
**What.** An `async void` method, or an async lambda converted to a void-returning delegate (`Action`,
`TickHandler`, an event handler).

**Why.** faults.md lists `async void` among the things Kiln counts as faults. That is true only while the code
continues on the tick thread, through the mod's synchronization context.

What `async void` loses:
- Nothing can await it.
- `OnLoad` returns at its first `await`. Kiln then thinks the mod is loaded: dependents load and `ModsLoadedEvent`
  fires while the rest still runs. `OnUnload` lets the mod unload while it still runs.
- A `[Subscribe]` handler's event is finished, and its cancellation decided, at the first `await`.
- `[KilnTest] async void` passes before it ran (testing.md).
- After `ConfigureAwait(false)` or `Task.Run`, an exception has no context to go to: the process goes down
  (crash report `managed-unhandled`).

```csharp
public override async void OnLoad() { await LoadIndexAsync(); }    // KILN102
private async void Refresh() { … }                                 // KILN102, fix: async Task
Context.Scheduler.Post(async () => await SaveAsync());             // KILN102 (Post takes an Action)
```
**Fix.**
- For a plain method, the code fix makes it return `Task`. It is offered only when the method is not an override,
  does not implement an interface, and is not used as a method group anywhere in its type.
- For `OnLoad`: keep it synchronous and start the work as a stored Task, for example
  `_index = LoadIndexAsync();`. Or do the work in a `ServerStartedEvent` handler.
- For lambdas: use an overload that takes a `Func<Task>`, or call an `async Task` method from a synchronous lambda
  and keep or observe the Task.

<a id="kiln103"></a>
## KILN103: process-wide event handler never removed
**What.** `+=` on an event that outlives the mod, with no `-=` of the same event anywhere in the type. Two cases:
- a static event of another assembly;
- an event of a process-wide object reached through another assembly's static member, such as
  `AppDomain.CurrentDomain.ProcessExit` or `AssemblyLoadContext.Default.Unloading`.

**Why.** lifecycle.md, "Hot reload": each mod has its own collectible load context. Kiln removes what the mod
registered through Kiln:
- ticks;
- game-log handlers;
- scheduled work;
- IMC handlers;
- event-bus subscriptions;
- every service's static events and handles: Chat, Players, Network, Ui, Client, Gameplay, Commands, Hooks, …

Each of those is tracked per mod and dropped when the mod unloads. Kiln does not remove handlers on events that
belong to the BCL, to libraries, or to another mod's `exports`. Such a handler roots the mod's load context, so a
copy of the old mod survives each `reload`, and the old code keeps reacting. `reload-test <id> <n>` shows the leak.

Not reported:
- Kiln's own events (`Context.Tick.Pre`, `Chat.Received`, `Players.Joined`, `Network.ServerReady`, …);
- the mod's own static events.

```csharp
public override void OnLoad() => AppDomain.CurrentDomain.ProcessExit += OnExit;   // KILN103
```
**Fix.** Remove it in `OnUnload`. When the handler is a method group in a `Mod`, the code fix adds
`AppDomain.CurrentDomain.ProcessExit -= OnExit;` to `OnUnload`, and creates the override if needed. Lambda handlers
cannot be removed: store the delegate in a field first.

<a id="kiln104"></a>
## KILN104: mod object stored in process-wide state
**What.** Something the mod owns is put where the mod's unload does not reach. "Owned by the mod" means a lambda,
a delegate to its method, an instance of its type, `typeof` of its type, or a generic or array over one. The places:
- a static field or property of another (non-Kiln) assembly, or an indexer on one;
- a collection held in such a static member (`Add`, `TryAdd`, `GetOrAdd`, `Enqueue`, …);
- `AppContext.SetData` and `AppDomain.SetData`.

Also reported:
- `GCHandle.Alloc` with a strong or pinned handle, when the type never calls `Free`;
- `TypeDescriptor.AddAttributes` and `AddProvider`, when the type never calls `RemoveProvider`.

**Why.** Same as KILN103. After every unload Kiln clears the framework caches it knows about (lifecycle.md):
- System.Text.Json type metadata;
- reflection-emit accessors;
- the runtime's hot-reload `ClearCache` handlers.

"Static references held by *other* (non-mod) code" are the mod's own job.

Not reported:
- the mod's own statics: they die with its load context;
- Kiln's settable statics, such as `Chat.ServerPrefix`;
- plain values such as strings and numbers.

```csharp
Shared.Listeners.All.Add(this);           // KILN104: added to 'Listeners.All', static state of SharedLib
AppContext.SetData("my-mod", settings);   // KILN104
GCHandle.Alloc(this);                     // KILN104 (no Free in the type)
```
**Fix.** Undo it in `OnUnload` (`Remove`, set back to null, `handle.Free()`), or keep the state inside the mod.

<a id="kiln105"></a>
## KILN105: blocking call on the game thread
**What.** A wait in tick-thread or game-thread code ([Where code runs](#where-code-runs-kiln101-kiln105)):
- `Thread.Sleep`, `Thread.Join`;
- `Task.Wait`, `WaitAll`, `WaitAny`;
- `Task<T>.Result`, `ValueTask<T>.Result`;
- `GetAwaiter().GetResult()`;
- `WaitHandle.WaitOne`, `WaitAll`, `WaitAny`;
- `SemaphoreSlim.Wait`, `ManualResetEventSlim.Wait`, `CountdownEvent.Wait`;
- `Barrier.SignalAndWait`, `Monitor.Wait`, `SpinWait.SpinUntil`;
- `Process.WaitForExit`.

**Why.**
- The server steps its world about 24.5 times a second on that thread. While it waits, nothing steps.
- It deadlocks when the awaited work itself needs the tick thread: an `await` continuation of mod code, a
  `Scheduler.Post`, or a service task that completes "on the tick thread two steps later" (Spawn, Teleport,
  Combat.Damage). That work can never run.
- testing.md: "Only awaits are timed: a test that blocks the tick thread cannot be stopped."

Not reported:
- zero timeouts (`Wait(0)`, `Sleep(0)`), which are polls;
- tasks that are complete: `Task.FromResult(x).Result`, a task `await`ed earlier in the same function, a read under
  `if (t.IsCompleted…)`, or a Kiln API that always returns a completed task (`Spawn.Entity(…).Result`, also through a
  local that is never written again: `var t = Creatures.Spawn(…); … t.Result`).

```csharp
[KilnTest] public async Task Spawns(TestContext t)
{
    var id = Spawn.Entity("Enemy_Vukah_Melee", pos).Result;   // KILN105: deadlock, Spawn completes on the tick thread
}
```
**Fix.** `await`. Inside an async function, the code fix rewrites:
- `x.Result`, `x.Wait()` and `x.GetAwaiter().GetResult()` to `await x`;
- `Thread.Sleep(n)` to `await Task.Delay(n)`. The continuation comes back to the tick thread through the mod's
  synchronization context.

In a synchronous handler, start the work and react when it completes:
`Context.Scheduler.Post` or `ContinueWith(…, TaskScheduler.FromCurrentSynchronizationContext())`.

### Around Kiln tasks
The same id covers the ways of waiting on, or escaping, a Kiln task (`Context.Tasks`). The thread
classifier ([Where code runs](#where-code-runs-kiln101-kiln105)) also follows a task's scope: a task segment
and its tick hops run with the task's execution scope installed; code after
`await …ConfigureAwait(false)` still belongs to the task body, but its scope is gone until a hop
(`await ctx.ToTick()`, `await ctx.NextGateWindow()`) brings the work back into the task.

**Blocking on a task.** `KilnTask.Wait()` on the tick, on a game thread or inside a task body (a lambda,
method group or local function handed to `Context.Tasks.Run`, on a worker or on one of its tick hops,
also after `await …ConfigureAwait(true)`), and blocking on the plain `Task` that `AsTask()` hands out — `job.AsTask().Result`,
`job.AsTask().Wait()`, `job.AsTask().GetAwaiter().GetResult()`, also through a local whose only value is
that call (`var t = job.AsTask(); … t.Result`, until the local is written again). At run time
`KilnTask.Wait()` refuses on the tick and on a task worker; `AsTask()` escapes that refusal, so blocking on
its `Task` can deadlock the tick or starve the pool and only this rule sees it. **Fix:** `await` the
`KilnTask` (in an async function the code fix rewrites the `Wait` and `Result` forms as above).

Not reported:
- `KilnTask<T>.Result` and `job.GetAwaiter().GetResult()` themselves: they never block, they throw until
  the task has ended;
- a plain `Task` that is not from `AsTask()` inside a task (`Task.FromResult(…)` above, `Task.Run` work a
  task waits on), a look-alike `Wait()` or `AsTask()` on one of the mod's own types, and `KilnTask.Wait()`
  on a thread of the mod's own (`Task.Run`, a thread it started), where `Wait` is allowed;
- the same calls in a task after `await …ConfigureAwait(false)` on every path or only some, until a hop:
  the code usually runs on a .NET pool thread there, where `Wait` is allowed too, and the analyzer cannot
  tell when the await finished at once and stayed on the worker.

**A hop that never happens.** `ctx.ToTick()` whose result runs nothing: the call as a statement, under
`_ =`, as the body of a void task lambda, or in a local nobody reads again. A `TickHop` does nothing until
it is awaited — its awaiter is what hands the continuation to the tick — so the code after such a call
still runs where it ran before. This is reported anywhere, in a task or in a helper that takes a
`KilnTaskContext`. Not reported: a hop that is awaited, kept in a local and awaited later
(`var hop = ctx.ToTick(); await hop;`), returned, passed on, or driven by hand through `GetAwaiter()`; and
a look-alike `ToTick()` on one of the mod's own types.

```csharp
Context.Tasks.Run("loot-scan", async ctx =>
{
    var found = ScanNearbyContainers(area);
    ctx.ToTick();                                   // KILN105: never awaited, the hop never happens
    job.Wait();                                     // KILN105: refused in a task
    var path = other.AsTask().Result;               // KILN105: AsTask() escapes the refusal
    await ctx.ToTick();
    ApplyResults(found);                            // tick-only work, on the tick after the hop
});
```

**`await Client.NextTick()` in a task.** Kiln.Client's way to the main thread is a plain `Client.Post` of the
continuation: inside a Kiln task the rest of the method leaves the task (not tracked by it, not cancelled with it, and
left waiting for good if the mod unloads before that step). Reported at the await, where the task's scope is proven;
outside a task it is Kiln.Client's own way and fine. **Fix:** `await ctx.ToTick(WorldKind.Client)`, the task's own hop
to the client world's step.

```csharp
Client.Tick += (in TickInfo t) => Context.Tasks.Run("overlay", async ctx =>
{
    var marks = ComputeMarks();
    await Client.NextTick();                        // KILN105: leaves the task
    await ctx.ToTick();                             // the task's own hop: back to the client world it was started in
    Thread.Sleep(5);                                // KILN105: blocks the game's main thread
});
```

**A task-thread world call outside a task.** A `WorldConcurrent` read (`Open`, `Validate`, `Read`,
`ReadBytes`, `ReadMany`, `Query`, `Template`) or compare-exchange write (`TryCompareExchange`,
`TryCompareExchangeBytes`) where no task's scope is installed:
- in a task body after `await …ConfigureAwait(false)` (also `await foreach` and `await using` over
  `ConfigureAwait(false)`), on every path or only some, round a loop, or in a `catch` or `finally` whose `try`
  had one, until a hop (`await ctx.NextGateWindow()`, `await ctx.ToTick()`) brings the work back into the task;
- in work the mod starts itself: `Task.Run`, a `new Thread`, a timer or a thread-pool callback, also inside a
  task body;
- in the `offTick` delegate of `WorldConcurrent.OrOnTick` in such places: it runs at once, where `OrOnTick` is
  called;
- in an async helper of the mod's own after its own `await …ConfigureAwait(false)`.

Kiln checks the scope on every call and refuses one without it with `InvalidOperationException`, so the call
never works there. **Fix:** keep the awaits in the task or the helper (drop `ConfigureAwait(false)`), or call
`await ctx.NextGateWindow()` first; for work started with `Task.Run` or a thread, run it in a Kiln task
(`Context.Tasks.Run`) instead.

Not reported: the reads inside a task body (also after `ConfigureAwait(true)`, a hop, or in `ctx.OnTick` and
`OrOnTick`'s `onTick`), on the tick and on game threads; `WorldConcurrent.IsAvailable`, `IsPresent`, `CanWrite`,
`WritePolicy` and `OrOnTick` itself, which work on any thread; the values the reads return (`ConcurrentSnapshot.TryGet`,
`ConcurrentRead<T>.Value`); a look-alike `WorldConcurrent` of the mod's own; code whose thread the analyzer cannot
tell (a stored delegate, a method nothing in the mod calls); and an awaitable configured in one place and awaited
in another (`var a = x.ConfigureAwait(false); … await a;`), whose `false` the analyzer does not follow.

```csharp
Context.Tasks.Run("chest-scan", async ctx =>
{
    var data = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
    var chest = WorldConcurrent.Open(id);          // KILN105: the task's scope is gone after ConfigureAwait(false)
    await ctx.NextGateWindow();
    var again = WorldConcurrent.Open(id);          // back in the task: fine
});
```

**A compare-exchange write in tick code.** `WorldConcurrent.TryCompareExchange` or `TryCompareExchangeBytes` in
code the analyzer places on the tick (the list in [Where code runs](#where-code-runs-kiln101-kiln105): `OnLoad`, tick,
event, command and chat handlers, `Scheduler.Post` work, a task after `await ctx.ToTick()`, `ctx.OnTick` and
`ctx.PostToTick` delegates). The write takes the engine's write gate, which the server frame holds for its whole
update, so from tick code it answers `Busy` (`WriteReason.InFrame`) every time and never writes (a client mod's tick
gets `Unavailable`: the writes are for the server world only). **Fix:** call it from a Kiln task's worker (retry a
busy one after `await ctx.NextGateWindow()`), or write on the tick with `World.Write`. Not reported: the writes on a task's worker, and a look-alike `TryCompareExchange` of the mod's own.

```csharp
[Command("charge")]
private string Charge(CommandContext c)
{
    WorldConcurrent.TryCompareExchange(e, "Charge", now, next);   // KILN105: always Busy from a command handler
    Context.Tasks.Run("charge", async ctx =>
    {
        var r = WorldConcurrent.TryCompareExchange(e, "Charge", now, next);   // on a worker: fine
        while (r.Status == ExchangeStatus.Busy)
        {
            await ctx.NextGateWindow();
            r = WorldConcurrent.TryCompareExchange(e, "Charge", now, next);
        }
    });
    return "charging";
}
```

<a id="kiln106"></a>
## KILN106: thread, timer or subscription never released
**What.** Something the mod creates is never released. The tracked types and calls:
- `System.Threading.Timer`, `System.Timers.Timer`, `PeriodicTimer`;
- `Thread`;
- `FileSystemWatcher`;
- `FileStream`, `StreamWriter`, and the `File.Open/Create/OpenWrite/AppendText/CreateText` calls;
- `Socket`, `TcpListener`, `TcpClient`, `UdpClient`, `HttpListener`;
- `ThreadPool.RegisterWaitForSingleObject`;
- `IObservable<T>.Subscribe`.

When it counts as never released:
- stored in a field or property, and no `Dispose`, `DisposeAsync`, `Close`, `Stop`, `Join`, `Interrupt`,
  `Unregister` or `Abort` on that member anywhere in the type;
- in a local that the function never releases, returns, stores or hands on;
- created and dropped, such as `new Thread(…).Start()`.

`using` is fine. So is handing it to someone else: an argument, a return value, a collection element.

**Why.** lifecycle.md: "What a mod must still release itself: threads, timers, sockets, files, static references
held by other (non-mod) code." A timer keeps calling into the unloaded mod and keeps its load context alive. A
thread inside the mod's code blocks the unload. Kiln's own handles are released on unload and are not reported:
- `Commands.Register`, `Gameplay.Protect`, `Network` channels;
- `Hooks`, `Ui` windows, `KeyBinding`s;
- `CreatureVariants.Register`, `Natives.OnDamage`, …

```csharp
System.Threading.Timer? _poll;
public override void OnLoad() => _poll = new Timer(_ => Context.Scheduler.Post(Check), null, 0, 5000);   // KILN106
```
**Fix.** Dispose or stop it in `OnUnload`. For fields of a `Mod`, the code fix adds `_poll?.Dispose();` to
`OnUnload`. Threads get no fix: give the thread a stop signal (a `CancellationTokenSource`) and `Join` it in
`OnUnload`.

<a id="kiln107"></a>
## KILN107: advanced API without KilnAdvanced
**What.** A call into the advanced tier while the mod does not opt in. The advanced APIs are kiln.hooks':
- `Hooks.Detour`, `Hooks.Mid`;
- `Symbols.Get`, `TryGet`, `Find`, `FindAll`, `Hole`, `LoadShipped`;
- `Memory.Read`, `TryRead`, `ReadBytes`, `Write`, `Patch`;
- `Constants.Get`, `TryGet`.

The opt-in is `<KilnAdvanced>true</KilnAdvanced>`, or `"advanced": true` (or `"native": true`, its newer name since
0.2.0-dev.32) in the project's own `kiln.mod.json`, which wins, as in the manifest task.

**Why.** `managed/services/Kiln.Hooks/Gate.cs` reads the calling mod's manifest and throws:
`InvalidOperationException: Symbols.Get: mod 'x' is not advanced …`. The build error says so before the server does.

Not gated:
- `unsafe` code inside the mod;
- pointers the mod makes itself.

Kiln only gates kiln.hooks, and mods never see game pointers without it.

**Fix.** Add the property, or the manifest key. No code fix: the project file is not a source file.

<a id="kiln109"></a>
## KILN109: client-world API called off the game's main thread
`EcsPrograms.TryBind(key, callback, WorldKind.Client)` (and `WorldKind.Prediction`) binds a program of this player's client
world, which kiln.world takes only on the game's main thread: in `OnLoad` (a client mod loads in the main menu, on that
thread) or in `Client.Post` work. Elsewhere it throws `InvalidOperationException`. The rule reports the call in code that
runs off the game's threads (`Task.Run`, threads, timers, thread-pool callbacks, after `await ...ConfigureAwait(false)`),
when the world is a constant. Not reported: the tick thread (in `OnLoad` it is the main thread), though on a host the
server's tick thread (`Scheduler.Post`, a reload's `OnLoad`) is not the main thread: there, too, post it with
`Client.Post`. A world argument that is not a constant (a variable, a field, a parameter) is not seen: the rule cannot
tell a client bind from a server one there and does not report it; off the game's threads KILN101 reports such a call
as it does a server bind (whose thread is the tick thread), and at run time the call throws off its world's thread. [services/world.md](../services/world.md#client-world-programs-since-020-dev29).

<a id="kiln108"></a>
## KILN108: Kiln service not listed in KilnServices
**What.** The code uses a type or member of a Kiln service assembly, but the project's `KilnServices` does not
name that service. Examples: `Kiln.Players`, or `Kiln.Testing` for `[KilnTest]` (service name `Testing`). Reported
once per service per file.

**Why.**
- In package builds, `KilnServices` is what references a service: the SDK adds `kiln/lib/Kiln.<Service>.dll` from the
  Kiln.Api package, compile-only (mdk.md).
- When the build works without the entry, the reference came another way: a hand-written `ProjectReference` or
  `Reference`, or a library or exported API that drags it in. Such a project breaks when built from the NuGet feed.
- Tools such as `kiln lint` and the launcher read the list as the mod's declared service use.

**Fix.** Add the service: `<KilnServices>Chat;Players</KilnServices>`.

**Stations** (since 0.2.0-dev.29.15). The production stations and bases live in Kiln.Blocks' assembly, but `Stations`
in `KilnServices` covers them on their own: `Stations`, `Bases`, `BaseId`, `BaseIdChange`, `BaseInfo`, `BaseAltar`, the
`Station*` types, `IngredientCategory`, `IngredientOption`, `CategoryChoice`, `FillOptions`, `FillMode`, `FillPlan`,
`FillInput`, `FillLimit`, `StopWhen`, `CollectWhat` and `BasesChangedEvent`. A project that lists `Stations` (and not
`Blocks`) gets no KILN108 for them; the rest of Kiln.Blocks still asks for `Blocks`, and a project that lists `Blocks`
covers the stations types too ([mdk.md](mdk.md)). Before 0.2.0-dev.29.16 `BaseIdChange` was missing from that list, so
reading `BasesChangedEvent.Changed` (`c.Old`, `c.New`) with `Stations` alone raised a false KILN108; 0.2.0-dev.29.16
fixes it.

<a id="kiln120"></a>
## KILN120: Kiln result thrown away
**What.** The value of a call that returns an `IKilnResult` (the services' result records: `EquipResult`,
`NoClipResult`, `WeatherResult`, `KickResult`, ...), or a `Task<T>` / `ValueTask<T>` of one, is not looked at:
- the call is a statement: `NoClip.Set(p, true);`, `await Items.Equip(p, slot);`;
- it is the body of an expression lambda whose delegate returns nothing: `Context.Scheduler.Post(() => NoClip.Set(p, false))`;
- a task of a result is a statement and is not awaited: `Items.Equip(p, slot);` ("a task nobody awaits or reads");
- it goes into a local that is never read: `var r = await Items.Equip(p, slot);`, and nothing reads `r` (also through a
  cast or a `?:`: `var r = on ? NoClip.Set(p, true) : NoClip.Set(p, false);`).

The rule goes by the type (it implements `Kiln.IKilnResult`), not by method names: a result type a service takes the
contract up for later, and a mod's own result type, are covered without a list.

Not reported:
- `_ = NoClip.Set(p, true);`, and a local named `_`: the author's word that the drop is meant;
- the result is returned, passed on, stored in a field or property, or a member of it is read (`(await Items.Equip(...)).Ok`);
- a local that is read anywhere in its function: a value written over before it was read is not seen.

**Why.** contract.md, "The error rule": when the game refuses (the player left, the slot is full, it is already set),
a Kiln call does not throw, it returns a result with `Ok` false and a `Reason`. A result nobody reads is a refusal
nobody notices: the command says "done" and nothing happened.

```csharp
await Items.Equip(player, from.Slot, EquipmentSlot.Ring1);     // KILN120: the result of 'Items.Equip' is thrown away
var r = await Time.SetWeather("rain");                          // KILN120 when nothing reads r
```
**Fix.** Two code fixes:
- "Check the result": a local and `if (!result.Ok) { ... }`. Inside it, `Log.Warn("Items.Equip refused: " + result.Reason);`
  when a `Log` is in reach (a `Mod`'s own, or a `Logger` member of the type), else a comment. For a task it adds the
  `await`, so it is offered in async code only. For a local it adds the `if` after the declaration. It is not offered
  where `result.Ok` would not be sound: a result that can be null (reached through `?.`, or of a nullable type), or a
  task kept in a local (it is no result yet).
- "Drop it on purpose (_ =)": `_ = ...`. In an expression lambda this is the only fix: `() => _ = NoClip.Set(p, false)`.
  It is not offered where `_` names a parameter or a local (`_ => NoClip.Set(_, true)`): that would assign it.

<a id="kiln121"></a>
## KILN121: Capability not checked before the call
**What.** A member marked `[RequiresCapability("row")]` (on itself, on its property or event, or on a type around it)
is called, read, set or constructed, and nothing in reach asks for the row. In reach, within the enclosing member:
- a test that the use sits under (`if`, the `else` of a negated test, `?:`, `&&`, `while`) or that leaves before it
  (`if (!Capabilities.Is("row")) return;`, or throw, break, continue, `throw c.Usage(...)`, a call marked
  `[DoesNotReturn]`, or a block with such a statement: `{ Log.Warn(...); c.Fail(...); }`). A test is `Capabilities.Is("row")` (the same
  string: a literal, a `const`, or a `static readonly` string of the mod set to a constant),
  `Capabilities.Get("row").IsAvailable`, a property that asks the same question (below), a `bool` local set once from
  one, either way round (`bool off = !NoClip.Available; if (off) return;`), or a `bool` property of the mod that is
  one (`static bool CanFly => Capabilities.Is(Fly);`, also `get { return ...; }`); with `!`, `&&`, `||`,
  `is true/false` and `== true/false` around it. A test with another condition counts (`if (!on && !NoClip.Available)
  return;` in mods/samples/AdminTools: turning NoClip off never needs the row, and the author knows it);
- a `try` whose `catch` catches `KilnUnavailableException` or a base of it (`NotSupportedException`, `SystemException`,
  `Exception`, a bare `catch`). A `when` filter is not judged;
- the enclosing method, accessor, local function, lambda or type marked `[RequiresCapability("row")]`: the requirement
  is passed on, and its callers are asked instead;
- a `[KilnTest]` or `[ClientCheck]` method: the test runner reports what it throws as the test's failure;
- the row in the project's kiln.mod.json `features`, or in `<KilnFeatures>`: Kiln does not load the mod when it is off.

A local function is judged where it is called, never by what is written before its declaration (it runs at its calls,
not where it stands). A lambda counts the code around it (the checks ran before the delegate was made), and the call it is handed to: a
callback given to a member that needs the row (`Content.OnReady(_ => Content.Apply(...))`) is only kept when that
call went through. A use on an object that came from a use of the same row is not reported, the first use is
(`var wood = Content.Items.Get(...)`, then `wood.MaxStack`; also a `foreach` over it and a pattern on it). An object
of the member's own assembly that Kiln hands to a callback (the `npc` of `Act.Custom(npc => npc.Target)`) is Kiln's,
so its service runs. Setting an `init` property (a block record's `Animated = ...`) is not a use: the row matters
where the value is used.

One step beyond the member: a private method or local function that is only ever called, or handed out as a method
group (`Context.Tick.Pre += OnPre;`), at places in reach of the row is in reach itself, also through other such
methods. This is the shape of mods/samples/Loadouts: the command catches `KilnUnavailableException`, its private
helpers make the calls.

**Properties that ask the same question.** The generated table (`KilnApis.g.cs`, `CapabilityGuards`) lists the public
static bool properties, of any name, whose own code always asks the row: the getter is an `&&` of terms, and a term is
`Capabilities.Is("row")`, a helper around it (`Can(Caps.Fuel)`; `WorldCapabilities.Is(...)`, which calls something
first), `T != null` for the
native table `T` of service `kiln.x` (it comes from `Services.Get<...>("kiln.x", ...)` or `Backend.Current.Service`,
also through another table property, a method or a local: `T => BuildNative.T`; that table is there exactly when the
core's `kiln.x` row is available), or another such property; or the getter is a block that returns `false` when the
table is null before it returns anything else (`{ var t = AiNative.T; if (t == null) return false; ... }`). A summary is not code: what it names makes no guard. A
getter that asks one row on a server and another on a client (`ServerSide ? relay : local`, `Sound.Available`,
`Vfx.Available`) always asks neither, so it is no guard. For example `NoClip.Available` (rules.noclip),
`Time.WeatherChoiceAvailable` (time.weather_choose), `Items.Fuel.Available` (items.fuel), `Content.GameDataAvailable`
(kiln.gamedata), `Settings.Ready` (kiln.settings), `World.SupportsTrackedWrites` (world.tracked_writes). A service makes
a property a guard by writing it that way: there is no attribute for it. `Saves.Available` is not one: it is
`SavesRuntime.Ensure()`, which answers from a remembered state; ask `Capabilities.Is("kiln.saves")`.

**Why.** contract.md: such a member throws `KilnUnavailableException` when its row is off (a game build Kiln does not
know, a game client in its menu, a server without the service, a hook that faulted). Uncaught in `OnLoad`, a command
or a handler, that is a mod fault.

```csharp
[Command("fly")]
private string Fly(CommandContext c) => NoClip.Set(c.Source.Player, true).Message;   // KILN121: rules.noclip
// in reach:
if (!NoClip.Available) throw new CommandException("NoClip is not available on this game build");
```
**Fix.** Ask first, catch, or pass the requirement on. When the use is a whole statement (a call, an awaited call,
`_ = call`, a property set) and declares no variable (an `out var` would move into the new block), the code fix puts
the statement under `if (Capabilities.Is("row")) { ... }`.

**Limits.** The rule looks at one member at a time (and the private callees above), and when in doubt it says nothing:
- **A helper that checks is not followed.** `Need("rules.noclip"); NoClip.Set(...)` warns in the method that calls
  `NoClip.Set`. Two ways out: catch `KilnUnavailableException` at the command (or handler) and keep the helpers that
  make the calls private, as mods/samples/Loadouts does; or mark the method that calls the gated member
  `[RequiresCapability("rules.noclip")]`, which passes the requirement on to its callers.
- An event handler or a callback that only runs while its service runs is not known as such (a `[Subscribe]` handler
  of a creatures event that calls `Creatures.Of(...)`): ask anyway, it is one condition.
- A check kept in a field or a `static readonly bool`, a `bool` local written twice, a property of another class of the
  mod: not seen.
- A test with another condition, a `when` filter and a lambda's surroundings count as asked: a row that goes off
  between the check and a later callback is not seen.
- It trusts the attributes: a marked member that does not throw is still reported, an unmarked one that throws is not.
- A row the manifest requires is checked when the mod loads; a row that can go off in a session (items.equip when its
  program breaks) can still throw later.

<a id="kiln199"></a>

## KILN199: Replicated extension value type outside the rule
**What.** `Replication.Extension<T>(name, version)` is called with a `T` that Kiln refuses when the extension is
registered (`ArgumentException`): the value travels as its own bytes, so it must be an unmanaged struct with
`[StructLayout(LayoutKind.Sequential, Pack = 1)]` made only of `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`,
`long`, `ulong`, `float`, `double`, enums of those and nested structs under the same rule, at most 1,024 bytes. `bool`,
`char`, `nint`, `nuint`, pointers, fixed buffers and reference types are refused. Error by default: the mod would
throw in `OnLoad`.

**Fix.** Use a number where a `bool` or `char` was (`byte Flags`), a nested struct of fields where a fixed buffer was,
and put `[StructLayout(LayoutKind.Sequential, Pack = 1)]` on the value type and on every struct inside it.

**Limits.** The layout attribute is checked on the mod's own types only (an analyzer cannot see a referenced
assembly's layout; Kiln checks it when the extension is registered). A `T` that is a type parameter is checked where
it is closed, and at run time.

<a id="kilnx001"></a>
## KILNX001: lab and test member
**What.** The code calls a member Kiln keeps for its own lab and test mods: `Rules.Simulate`, `QuestInput`,
`Knowledge.LabRecord`, `GameTree.Simulate`, the `Lab*` members, `CreatureRef.ForceReady` and others. They are
`[Experimental("KILNX001")]`, hidden from completion, and may change or go without notice. The compiler reports the call as
an error.

**Since when.** 0.2.0-dev.26. About 120 of these members already existed in dev.24 without the mark, so a lab or test project
that called one builds no longer until it opts in. Mods built against dev.24 still load and run: the mark only matters when
you compile.

**Fix.** In a lab or test project, opt in once in the project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);KILNX001</NoWarn>
</PropertyGroup>
```

In a mod players install, use the documented API instead; `limits.md` lists what has none yet.

The task-thread world reads and writes (`WorldConcurrent`) are not lab members and need no experimental opt-in
([retired KILNX003](#kilnx003)).

<a id="kilnx002"></a>
## KILNX002: not yet proven in the game client
**What.** The member works in Kiln's labs, but no player has confirmed it in the real game client yet (the guided client
pass settles it). `[Experimental("KILNX002")]`; today the achievement guard (`SteamGuard`, `SteamGuardRelay`).

**Fix.** Opt in the same way with `KILNX002` when you accept that; the mark goes once the client pass confirms it.

<a id="kilnx003"></a>
## KILNX003: task-thread world reads and writes (retired in 0.2.0-dev.30.7)
**First availability.** The task-thread reads and compare-exchange writes (`WorldConcurrent` and its types,
Kiln.World) and TestKit's `FakeWorld.BusyReads` / `BusyWrites` first shipped in 0.2.0-dev.29.18 with
`[Experimental("KILNX003")]`. On those older SDKs, using them requires a `KILNX003` opt-in in `NoWarn`.

**Stabilization.** Since 0.2.0-dev.30.7, mod API 0.2.16 (no longer experimental). Mod and test projects no longer
need that opt-in. An existing `KILNX003` suppression is harmless and can go. `KILNX001` lab APIs and `KILNX002`
client APIs retain their own opt-ins. Threading, bounds, capability checks and refusal behavior have not changed:
KILN101 still reports tick-only API in task work, and KILN105 still reports task-unsafe uses, including escaped
world reads and compare-exchange writes on the tick. See the [task guide](tasks.md#project-setup).

## KILNX031: registries
**What.** The code uses `Kiln.Registry` (the registries' entry types such as `ItemEntry`, `RegistryId`, `RegistrySchema`)
or `Kiln.RegistryApplies`. They are `[Experimental("KILNX031")]`: generated from Kiln's registry schemas, documented, but
new; the registry stage that reads and edits through them is not wired yet. The compiler reports the use as an error.

**Since when.** 0.2.0-dev.31.

**Fix.** Opt in once in the mod's project file (and its test project):

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);KILNX031</NoWarn>
</PropertyGroup>
```

A registry leaves experimental when its schema is marked stable; until then field names and types can still change. The
guide: [registries/README.md](registries/README.md).

---

<a id="kiln194"></a>
## KILN180, KILN181, KILN182: Typed-hook descriptors
<a id="kiln180"></a><a id="kiln181"></a><a id="kiln182"></a>
The Kiln.Sigs generator turns each `sig` descriptor of a mod's `sigdb/server.json` and `sigdb/client.json` into a class (services/hooks.md). It warns and leaves a descriptor out when kiln.hooks would refuse it as malformed (KILN180), when the project describes functions but does not reference Kiln.Hooks (KILN181: add `Hooks` to `KilnServices`), and when a sigdb file does not parse (KILN182). Fix the descriptor; `tools/sigport/sigdb_check.py --fptool` checks it against the exe.

## KILN194: No such field in the game
**What.** A constant path string given to `GameDataEntry` or `ContentEntry` (`Get`, `Original`, `GetNumber`, `GetJson`, `GetRef`, `Set`,
`SetJson`, `Reset`, `Fields`), or a key of a definition's `Fields`, that the game doesn't have:
```csharp
Content.Items.Get(GameItems.Weapon_T1_1H_SwordEpic_01)!.Set("damageSetup.dmgMood", 1.5);
// error KILN194: 'damageSetup.dmgMood' is not a path of items: damageSetup has no field 'dmgMood'. Did you mean 'damageSetup.dmgMod'?
```
**Why.** It would be refused when the mod runs. The path is checked against the catalog made from your own game, the same way
kiln.gamedata walks it: `[i]`, `[*]`, `[Type#k]`, `[guid]`, `Component#k`, names exact and then ignoring case.

**When it stays silent.** A computed string. An entry whose section the code doesn't show, such as a parameter or a field; locals, loops
and `?.` are followed. Sections with no field list (arsenals, drops). Anything below a list of variants. No catalog on the machine. A
false alarm would stop your build, so anything it can't know is let through.

**Fix.** Use the suggestion (a code fix writes it), or the typed field ([typed-fields.md](typed-fields.md)). A test that wants the
refusal keeps its path with `#pragma warning disable KILN194`.

<a id="kiln195"></a>
## KILN195: Config name the game doesn't know
**What.** A config below `impactValues`, `configValues` or `ImpactConfiguration.configValues`, or in `Config("...")`, with a name no Impact
program of the game uses.

**Why it's only a warning.** Your own program can declare a name the game has never heard of. Names with dots are yours by nature: write
them in brackets, `impactValues[mymod.Radius]`, and they aren't checked. So are `0x` ids.

**Fix.** Use the suggestion, or suppress it for your own name.

<a id="kiln196"></a>
## KILN196: Field Kiln doesn't change
**What.** A write to an identity field or a name (`itemId`, `guid`, `debugName`).

**Why.** They identify the entry in registries and saves, and kiln.gamedata refuses them. The typed form doesn't compile.

<a id="kiln197"></a>
## KILN197: Typed field of another section
**What.** `Content.Templates.Get(x)!.Set(GameFields.Items.MaxStackSize, 5)`: a field of `items` on a template.

**Why.** The path would mean nothing there. A field stored in a variable isn't followed, and is refused with an `ArgumentException` when it
runs.

## Tests and development
- Tests: `dotnet test managed/Kiln.Analyzers/tests/Kiln.Analyzers.Tests --artifacts-path $KILN_OUT/dotnet`. Each test
  compiles a small mod against the real Kiln.Api and service assemblies. It runs one analyzer, and applies code
  fixes through an AdhocWorkspace.
- Sources:
  - `managed/Kiln.Analyzers/src/Kiln.Analyzers/` (netstandard2.0, Microsoft.CodeAnalysis.CSharp 4.14);
  - `CodeFixes/` (+ Workspaces);
  - `tools/ApiScan` and `managed/Kiln.Analyzers/tools/gen-apis.sh`.
- Limits:
  - The thread analysis follows a function's control-flow graph and one level of method groups inside a type,
    plus local functions at their call sites inside a function. A method that becomes off-thread code through
    another type, an ordinary helper call or a stored delegate is not seen.
  - KILN106 checks that a release exists somewhere in the type, not that it runs in `OnUnload`.
  - KILN120 and KILN121: their own sections above.

<a id="kiln110"></a>
## KILN110: Bad or duplicate block name
**What.** A `Blocks.Define` name with characters other than letters, digits, `-`, `_` and `.`, or the same name twice. Two calls in different
branches of an `if` or `switch` are fine: only one runs.

**Why.** A block's ids come from its name. A wrong name throws `BlockError` when the mod loads, and two blocks with one name are one block.

**Fix.** Rename one. To rename a block that players already have, say `Was` (blocks.md).

<a id="kiln111"></a>
## KILN111: A name that is no block
**What.** A string where a block is named (`Applies = "chilled"`, `Then = ["burst"]`) that this mod defines nowhere, in code or in `blocks/*.json`, or
that is another kind of block. Names with a `:` (another mod's) and the game's ids (`0x...`, GUIDs) are not checked. Nor is anything when a block's name
is built at run time, because it could be any name.

It checks every field that names a block: effects, statuses, VFX, sounds, items, perks, abilities, materials, stations, screen effects, the windows a
button or key of a [UI block](ui-blocks.md) opens or closes, and spawned templates. Some of these also take a game name:
- An item, perk or template may be the game's. The check uses the generated `GameItems`, `GamePerks` and `GameTemplates`; without them (no local
  game catalog, or the section left out of `KilnGameIds`) it skips those names. Names your data packs in `content/*.json` add count too, and so do
  items your code registers with the older items API (`new ItemDefinition { Name = "glacier_gem", ... }` for `Content.Items.Register`; when such a
  name is built at run time, item names are not checked).
- A name in the game's style (capitalised words joined by `_`, like `Weapon_T1_1H_SwordEpic_01`) that the local catalog lacks is reported as
  **info**, not a warning: your catalog may be of another game build, or the item may be another mod's. Other unknown names stay warnings.
  An item that code adds at run time some other way, or another mod's by its game name, can be suppressed there.
- A sound may be a `SoundBlock`, a cue in `assets/cook.json`, or a game cue's GUID (`GameSounds.Guids`). A game cue has no name a block can use.
- A perk may also be `"none"` or `"item:<name>"`. A station may be `"hand"` or a workshop's `0x` id.

```csharp
Blocks.Define("hit", new EffectBlock { Sound = "dnig" });   // KILN111: no sound "dnig"
```

**Why.** The block throws `BlockError` when the mod loads.

**Fix.** Define it, correct it, or use the handle `Blocks.Define` returned: the compiler checks those.

<a id="kiln112"></a>
## KILN112: clients too low for the mod's blocks
**What.** `KilnClients` (or `clients` in `kiln.mod.json`) says less than the mod's blocks need. A new item, spell or status needs `kiln`. A `VfxBlock`, a `SoundBlock`, or a field that
names the mod's own file, cue or VFX, needs `mod`. A game VFX by name asks for no more than its block.

**Why.** Kiln refuses the block when the mod loads, because players' games could not show it.

**Fix.** Raise it, or remove the setting: the build then works out the lowest level that works (blocks.md).

<a id="kiln034"></a>
## KILN034: OnBoot that never runs, or Blocks.Define in boot code
**What.** Two mistakes of the boot phase ([manifest.md](manifest.md), `"boot"`):
- A `Mod` subclass overrides `OnBoot` while the project's `kiln.mod.json` has no `"boot": true` (or `"required"`).
  Reported at the override. A Kiln.Sdk project without its own kiln.mod.json gets one written from its properties: with
  `<KilnBoot>true</KilnBoot>` (or `required`) it says `"boot"` and is not reported; without it, it is. A project's own
  kiln.mod.json wins over `KilnBoot`, as in the manifest task. A project that says nothing at all (no kiln.mod.json,
  not built with Kiln.Sdk) is not reported.
- The run-time `Blocks.Define` (any overload) called from boot code. Boot code is the body of an `OnBoot` override or of
  a method, accessor or type marked `[BootThreadOnly]`, with its local functions and the lambdas that run at once
  (LINQ, `List.ForEach`). A lambda handed to Kiln (`Context.Scheduler.Post`), to another thread, or kept by the mod is
  not boot code, and neither is a helper method that is not marked.

```csharp
public override void OnBoot(BootContext boot)            // KILN034 when kiln.mod.json has no "boot"
{
    Blocks.Define("cold", new StatusBlock { Name = "Cold", Seconds = 4 });   // KILN034: refused in OnBoot (PhaseClosed)
}
```

**Why.** Kiln calls `OnBoot` only for a mod that opts in: without the key the override is never called, and its
definitions are missing from every start, with no error. Inside `OnBoot`, `Blocks.Define` throws
`InvalidOperationException` ("PhaseClosed: Blocks.Define is refused inside …'s OnBoot"): it would register a block
before the boot generation is checked. The mod's `OnBoot` then fails, its boot content is dropped, and with
`"boot": "required"` the start stops.

**Fix.**
- Add `"boot": true` to kiln.mod.json (`"required"`: a failure of its boot content stops the start), or, in a Kiln.Sdk
  project that has no kiln.mod.json of its own, `<KilnBoot>true</KilnBoot>`; or move the code to `OnLoad`.
- Stage the block instead: `BootDefinitions.For(boot).Define("cold", new StatusBlock { ... })`. A block that names it
  uses its `Reference` (or the name).

No code fix: the manifest is not a source file, and a staged definition returns a result, not a handle.

<a id="kiln035"></a>
## KILN035: tick, world or service API in boot code
**What.** Boot code (as in [KILN034](#kiln034)) uses:
- a member that needs the tick thread: `[TickThreadOnly]`, or the generated tick table of [KILN101](#kiln101);
- a member of a service's own static class, or of a type nested in it: `World`, `Players`, `Content`, `Items`,
  `Chat`, `Spawn`, ... (the classes named after the services of the generated service table, in their own
  assemblies).

- a member Kiln refuses on the boot thread although it allows any thread otherwise: the generated `BootRefused` table
  (it throws while the boot phase is open, directly or through a call: adding a `ConfigFile.Changed` handler).

Not reported:
- members marked `[BootThreadOnly]` (`BootDefinitions`, `boot.GameData.Read`);
- members with a boot path of their own, the generated `BootWorks` table (they return early while the boot phase is open):
  `Config.Register<T>()` reads the file and its defaults without writing in `OnBoot`, and reading its `Value` is a plain
  property ([config.md](../config.md#in-modonboot)). Writing the config (`Set`, `TrySet`, `Save`, `Reload`), a `Changed`
  handler and `Config.RegisterWorld` are still reported. KILN101 still counts `Config.Register` as a tick-thread member
  outside boot code;
- the capability guard properties (`Players.Available`, ...) and `Capabilities.Is`;
- data helpers outside a service's own class (`Damage.Ice`, `Bodies`, block records);
- Kiln.Api's `[AnyThread]` members such as `Mods.BootState`.

`[AnyThread]` on a service's member is not enough: it says which threads may call it, not that a world exists.
`Players.All` answers an empty snapshot before the game is entered.

```csharp
public override void OnBoot(BootContext boot)
{
    Chat.Broadcast("booting");                     // KILN035: must be called on the tick thread
    var n = Players.All.Count;                     // KILN035: a Kiln.Players service API
    var wood = boot.GameData.Read("item:Material_T1_Wood_Logs");   // fine: the offline view
    var settings = Config.Register<Settings>();    // fine: reads the file, writes nothing
    if (settings.Value.Enabled) settings.Save();   // KILN035: must be called on the tick thread
}
```

**Why.** `OnBoot` runs once per process, on the boot thread, before the game's entry point. The game has loaded none
of its data, and no world, player or live registry exists. A tick-thread API throws there. A service's live API has
nothing to act on.

**Fix.**
- Read the game's data with `boot.GameData.Read` ([boot-gamedata.md](boot-gamedata.md)).
- Stage blocks with `BootDefinitions.For(boot)`.
- Do the rest in `OnLoad`.

KILN121 knows `OnBoot`: `Mod.OnBoot` is marked `[RequiresCapability("mods.boot")]`, and an override has the rows of what
it overrides, so `BootDefinitions.For(boot)` in `OnBoot` needs no check. `boot.GameData.Read` still asks for
`mods.boot.game_data`, which is off where no snapshot of this game build was made.

<a id="kiln170"></a><a id="kiln171"></a><a id="kiln172"></a><a id="kiln173"></a><a id="kiln174"></a>
## KILN170 to KILN174: an `[ImpactNative]` method the generator cannot make
**What.** Errors with the method's location: KILN170 for the method itself (not static, generic, async, private or in a
private or generic type, or a name that is not `[A-Za-z0-9_]` of 1 to 60 characters), KILN171 for a parameter of a type
no native takes (or a `NativeContext` by `ref`/`out`, or `[Raw]` on anything but `uint`), KILN172 for a return type other
than `bool`, `byte` or `void`, KILN173 for one name on two methods. KILN174 (warning): the project has `[ImpactNative]`
methods but no Kiln.Abilities with `ImpactNativeAccess` (0.2.0-dev.29 or newer, `Abilities` in `KilnServices`).

**Why.** The generator writes the native's thunk, registration and typed declaration from the method's signature. A
shape it cannot turn into the impact types of the call is not generated at all, so nothing half-works at run time.

**Fix.** Make the method `static` and at least `internal`, use the types of [impact.md](impact.md) ("`[ImpactNative]`
methods"), give other results back through `ref` or `out` parameters, and give each native its own name.
