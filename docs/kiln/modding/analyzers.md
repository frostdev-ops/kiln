# Analyzers: KILN1xx

Kiln.Sdk runs Roslyn analyzers over every mod. They catch the mistakes that the server only shows at run time:
- a tick-thread API called from another thread;
- a mod that never unloads cleanly;
- a blocked game thread;
- an API the mod has not opted into.

The warnings appear in `dotnet build` output and live in the editor (C# Dev Kit, Rider, Visual Studio). Eight rules
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
| [KILN194](#kiln194) | **error** | a path string the game doesn't have | the local catalog |
| [KILN195](#kiln195) | warning | a config name no program of the game declares | the local catalog |
| [KILN196](#kiln196) | warning | writing an identity field or a name | the local catalog |
| [KILN197](#kiln197) | **error** | a typed field on an entry of another section | the code |
| [KILN110](#kiln110) | **error** | bad or duplicate block name | blocks.md |
| [KILN111](#kiln111) | warning | a name that is no block, or the wrong kind of block | blocks.md |
| [KILN112](#kiln112) | **error** | `clients` too low for the mod's blocks | blocks.md |

Other KILN codes:
- **KILN0xx** come from the SDK's MSBuild task (manifest checks, mdk.md).
- **KILN190** to **KILN193**, **KILN198** are the game-id generator's notes (catalogs.md).
- **KL** codes are the data-file linter's (`kiln lint`).

## How it ships
- **Package mode:** `Kiln.Sdk` carries the analyzers in `analyzers/dotnet/cs/`:
  - `Kiln.Analyzers.dll`: the analyzers, compiler-only references;
  - `Kiln.Analyzers.CodeFixes.dll`: the fixes, IDE only.

  `Sdk/Kiln.Analyzers.targets` adds them to every mod.
- **In the Kiln repository:** mods that import `managed/Kiln.Sdk/Sdk/*` get the analyzer projects as
  `OutputItemType="Analyzer"` references.
- The analyzers read these build properties: `KilnServicesList` (the SDK writes `KilnServices` comma-separated
  there, because `.editorconfig` cuts values at `;`), `KilnAdvanced`, `KilnSide`, `KilnModId`, `KilnApiVersion`.
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
- `Kiln.GameData`: 194 to 197. These ship with the game-id generator, so `<KilnGameIds>None</KilnGameIds>` turns them off.

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

Run the script after changing Kiln.Api, Kiln.Testing or a service:
`KILN_OUT=… managed/Kiln.Analyzers/tools/gen-apis.sh`.

The analyzers also honour two attributes, matched by name in namespace `Kiln`: `TickThreadOnlyAttribute` and
`AdvancedApiAttribute`, on a member or its type. Kiln.Api does not define them yet; they are proposed.

## Where code runs (KILN101, KILN105)
The analyzers decide which thread runs a piece of code.

**Tick thread:**
- `OnLoad` and `OnUnload` overrides;
- `[Subscribe]` and `[KilnTest]` methods;
- `Context.Tick.Pre` and `Post` handlers;
- `Context.GameLog.Line` handlers;
- `Scheduler.Post` work;
- `Events.On` and `Context.Messages.On` handlers.

Method groups count too: `Context.Tick.Pre += OnPre` makes `OnPre` tick-thread code.

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
| after `ConfigureAwait(false)` | everything after `await x.ConfigureAwait(false)`, up to the next `await Context.Scheduler.NextTick()`. A plain `await` off the tick thread does not come back: there is no Kiln synchronization context there. |

**Not reported (unknown):**
- lambdas the mod stores or hands to its own helpers;
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

Each of those is tracked per mod and dropped in `ModContext.Retiring`. Kiln does not remove handlers on events that
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
Shared.Registry.Items.Add(this);          // KILN104: added to 'Registry.Items', static state of SharedLib
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
- `Creatures.Register`, `Natives.OnDamage`, …

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

The opt-in is `<KilnAdvanced>true</KilnAdvanced>`, or `"advanced": true` in the project's own `kiln.mod.json`,
which wins, as in the manifest task.

**Why.** `managed/services/Kiln.Hooks/Gate.cs` reads the calling mod's manifest and throws:
`InvalidOperationException: Symbols.Get: mod 'x' is not advanced …`. The build error says so before the server does.

Not gated:
- `unsafe` code inside the mod;
- pointers the mod makes itself.

Kiln only gates kiln.hooks, and mods never see game pointers without it.

**Fix.** Add the property, or the manifest key. No code fix: the project file is not a source file.

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

---

<a id="kiln194"></a>
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
  - The thread analysis follows syntax and one level of method groups inside a type. A method that becomes
    off-thread code through another type or a stored delegate is not seen.
  - KILN106 checks that a release exists somewhere in the type, not that it runs in `OnUnload`.

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

It checks every field that names a block: effects, statuses, VFX, sounds, items, perks, abilities, materials, stations, screen effects and spawned
templates. Some of these also take a game name:
- An item, perk or template may be the game's. The check uses the generated `GameItems`, `GamePerks` and `GameTemplates`; without them (no local
  game catalog, or the section left out of `KilnGameIds`) it skips those names. Names your data packs in `content/*.json` add count too. An item
  that code adds at run time, or another mod's by its game name, gets the warning: suppress it there.
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
