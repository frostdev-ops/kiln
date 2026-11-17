// SPDX-License-Identifier: MIT
using Microsoft.CodeAnalysis;

namespace Kiln.Analyzers;

/// <summary>The KILN1xx rules, and KILN034/KILN035 (Mod.OnBoot). Docs: docs/kiln/modding/analyzers.md (one section per rule, anchored by its id).</summary>
public static class Descriptors
{
    /// <summary>Where the rules are documented. Repository-relative until the docs have a public home.</summary>
    public const string DocsBase = "docs/kiln/modding/analyzers.md";

    private const string Threading = "Kiln.Threading";
    private const string Reload = "Kiln.HotReload";
    private const string Manifest = "Kiln.Manifest";

    private static DiagnosticDescriptor Rule(string id, string title, string message, string category,
        DiagnosticSeverity severity, string description, params string[] tags) =>
        new(id, title, message, category, severity, isEnabledByDefault: true, description: description,
            helpLinkUri: $"{DocsBase}#{id.ToLowerInvariant()}", customTags: tags);

    public static readonly DiagnosticDescriptor TickThreadOffThread = Rule("KILN101",
        "Tick-thread API called off the tick thread",
        "'{0}' must be called on the tick thread, but this code runs {1}; post it with Scheduler.Post",
        Threading, DiagnosticSeverity.Warning,
        "Game state is single-threaded: this Kiln API throws InvalidOperationException when it is not called on the " +
        "server's tick thread. Code in Task.Run, threads, timers, thread-pool callbacks, file watchers and after " +
        "'await ...ConfigureAwait(false)' runs elsewhere. Use Context.Scheduler.Post(() => ...) or " +
        "'await Context.Scheduler.NextTick()' to get back.");

    public static readonly DiagnosticDescriptor ClientWorldOffMainThread = Rule("KILN109",
        "Client-world API called off the game's main thread",
        "'{0}' for the client world must be called on the game's main thread, but this code runs {1}; call it in OnLoad or post it with Client.Post",
        Threading, DiagnosticSeverity.Warning,
        "EcsPrograms.TryBind(..., WorldKind.Client) and (..., WorldKind.Prediction) bind a program of a client's worlds, " +
        "which kiln.world takes only on the game's main thread: in OnLoad (a client mod loads in the main menu, on that " +
        "thread) or in Client.Post work. Elsewhere it throws InvalidOperationException. Code in Task.Run, threads, timers, " +
        "thread-pool callbacks and after 'await ...ConfigureAwait(false)' runs elsewhere; on a host, the server's tick thread " +
        "(Scheduler.Post, a reload's OnLoad) is not the main thread either. Since 0.2.0-dev.29.");

    public static readonly DiagnosticDescriptor AsyncVoid = Rule("KILN102",
        "Avoid async void",
        "{0}",
        Threading, DiagnosticSeverity.Warning,
        "An async void method or lambda cannot be awaited: callers, Kiln's test runner and the unload cannot wait " +
        "for it or see its result. Kiln counts its exceptions as the mod's faults only while it continues on the " +
        "tick thread; one that throws after leaving it (ConfigureAwait(false), Task.Run) is an unhandled exception " +
        "that takes the whole server down.");

    public static readonly DiagnosticDescriptor ProcessWideEvent = Rule("KILN103",
        "Process-wide event handler is never removed",
        "'{0}' is a process-wide event: its handler keeps this mod's load context alive after unload and the old code keeps running after a hot reload; remove it ('-=') in OnUnload",
        Reload, DiagnosticSeverity.Warning,
        "Kiln removes what a mod registers through Kiln (ticks, game-log lines, scheduled work, IMC handlers, " +
        "event-bus subscriptions, service events and handles) when it unloads. Static events of other assemblies " +
        "(the BCL, libraries, other mods' exports) and events of process-wide objects such as " +
        "AppDomain.CurrentDomain are not Kiln's: a handler left there roots the mod's collectible load context.");

    public static readonly DiagnosticDescriptor ProcessWideCache = Rule("KILN104",
        "Mod object stored in process-wide state",
        "{0}",
        Reload, DiagnosticSeverity.Warning,
        "A static member of another assembly, a GCHandle or a process-wide registry outlives the mod. Whatever the " +
        "mod puts there (its objects, its types, delegates to its code) keeps its load context alive after unload, " +
        "so hot reload leaks a copy of the mod each time ('reload-test' reports it).");

    public static readonly DiagnosticDescriptor BlockingCall = Rule("KILN105",
        "Blocking call on the game thread",
        "'{0}' blocks {1}: the game stops stepping until it returns, and waiting for work that needs the tick thread (an await continuation, Scheduler.Post, a service task) deadlocks the server; use await",
        Threading, DiagnosticSeverity.Warning,
        "Tick handlers, event handlers, Scheduler.Post work, OnLoad/OnUnload and [KilnTest] methods run on the " +
        "game's tick thread (Kiln's UI and client callbacks on the game's other threads). A blocking wait there " +
        "freezes the server, and a test that blocks cannot be timed out.");

    // The task rows of KILN105 share its id: one severity setting covers every way of waiting on, or
    // escaping, a Kiln task. Their messages differ because the fixes differ.
    public static readonly DiagnosticDescriptor BlockingTaskCall = Rule("KILN105",
        "Blocking call in a Kiln task",
        "'{0}' blocks {1}: Kiln refuses a blocking wait on the tick and in a Kiln task (it can deadlock the tick or starve the task pool), and the Task that KilnTask.AsTask() hands out escapes that refusal; await the KilnTask instead",
        Threading, DiagnosticSeverity.Warning,
        "KilnTask.Wait is refused on the tick thread and on Kiln's task workers, and inside a task body a " +
        "blocking wait holds a pool worker (or a tick hop) that other tasks and the tick may need. AsTask() " +
        "gives a plain Task for composition (Task.WhenAll); Kiln cannot intercept Wait, Result or " +
        "GetAwaiter().GetResult() on it, so blocking on that Task in a task is the same mistake.");

    public static readonly DiagnosticDescriptor TickHopDiscarded = Rule("KILN105",
        "Tick hop result is never awaited",
        "'{0}' is never awaited: a tick hop does not happen until it is awaited, so the code after it still runs where it ran before; write 'await {0}'",
        Threading, DiagnosticSeverity.Warning,
        "KilnTaskContext.ToTick() only makes the hop: nothing is queued for the tick until the returned " +
        "TickHop is awaited (its awaiter is what hands the continuation to the tick). A ToTick() call whose " +
        "result is dropped, or kept in a local that nobody awaits, never hops, and the code after it runs on " +
        "a worker (or wherever it ran) with no warning from the tick-thread rules about that fact alone.");

    public static readonly DiagnosticDescriptor GatedWorldScope = Rule("KILN105",
        "Gated world call without a task scope",
        "'{0}' is a gated world call, but this code has no live task scope ({1}): gated calls check the task's execution scope, which code on a raw thread, in Task.Run or after 'await ...ConfigureAwait(false)' no longer has, so the call is refused; call it from a task body, or on the tick for a read",
        Threading, DiagnosticSeverity.Warning,
        "The gated world face (World.Concurrent / WorldConcurrent, T9/T10) reads and writes game state " +
        "through the engine's gates from a Kiln task: every call asks for the active task scope Kiln " +
        "installs around a task segment and around its tick hops. Work that left the task (ConfigureAwait(false), " +
        "Task.Run, a raw thread) carries at most the task's name for diagnostics, never its scope, and is refused.");

    public static readonly DiagnosticDescriptor GatedWorldWriteOnTick = Rule("KILN105",
        "Gated world write on the tick thread",
        "'{0}' is a gated world write, but this code runs {1}: the engine holds its scene gate for the whole update, so a gated write from tick code is always refused as busy; do the write from a Kiln task (and retry after 'await ctx.NextGateWindow()'), or use the tick API",
        Threading, DiagnosticSeverity.Warning,
        "A gated write (TryCompareExchange on the World.Concurrent / WorldConcurrent face, T10) takes the " +
        "engine's gates without waiting and is refused as busy while the engine holds them. The engine " +
        "holds its scene gate for the whole server update, Kiln's Pre/Post handlers and a task's tick hops " +
        "included, so a gated write attempted there can never land; between steps, from a task, it can.");

    public static readonly DiagnosticDescriptor Unreleased = Rule("KILN106",
        "Thread, timer or subscription is never released",
        "{0} is not released: Kiln does not stop threads, timers, watchers, sockets or external subscriptions a mod creates itself; dispose/stop it in OnUnload",
        Reload, DiagnosticSeverity.Warning,
        "docs/kiln/modding/lifecycle.md: what a mod must release itself are threads, timers, sockets, files and " +
        "static references held by other code. A running timer or thread keeps calling the unloaded mod's code and " +
        "keeps its load context alive.");

    public static readonly DiagnosticDescriptor AdvancedApi = Rule("KILN107",
        "Advanced API without KilnAdvanced",
        "'{0}' is an advanced API (hooks, symbols, raw memory): Kiln refuses it unless the mod opts in; set <KilnAdvanced>true</KilnAdvanced> (or \"advanced\": true in kiln.mod.json)",
        Manifest, DiagnosticSeverity.Error,
        "kiln.hooks checks the calling mod's manifest and throws InvalidOperationException ('mod ... is not " +
        "advanced') when it does not declare \"advanced\": true.");

    public static readonly DiagnosticDescriptor MissingService = Rule("KILN108",
        "Kiln service not listed in KilnServices",
        "'{0}' comes from Kiln.{1}, but <KilnServices> does not list {1}; add it to KilnServices in the project file",
        Manifest, DiagnosticSeverity.Warning,
        "KilnServices is how a Kiln.Sdk mod declares the Kiln services it uses: the SDK references exactly those " +
        "assemblies (compile only, never shipped) in package builds. A service that reaches the build some other " +
        "way works here and breaks the mod's build from the NuGet feed.");

    // Mod.OnBoot (chapter 08). The chapter names these two KILN034 and KILN035, outside the 1xx range of the other rules: the SDK's
    // MSBuild codes stop at KILN032, and the ids stay as the dossier and its guided checks say them.
    private const string Boot = "Kiln.Boot";

    public static readonly DiagnosticDescriptor BootPhase = Rule("KILN034",
        "OnBoot that never runs, or Blocks.Define in boot code",
        "{0}",
        Boot, DiagnosticSeverity.Error,
        "Kiln calls Mod.OnBoot only for a mod whose kiln.mod.json says \"boot\": true (or \"required\"); without it the override is never " +
        "called and its definitions are missing from every start. Inside OnBoot the run-time Blocks.Define throws " +
        "InvalidOperationException (PhaseClosed): it would register a block before the boot generation is checked. Boot code stages blocks " +
        "with BootDefinitions.For(boot).Define, which Kiln compiles and publishes with the mods' data files before the game is entered.");

    public static readonly DiagnosticDescriptor BootThreadApi = Rule("KILN035",
        "Tick, world or service API in boot code",
        "'{0}' {1}, but {2} runs on the boot thread before the game is entered, where no tick, world, player or live registry exists; read the game's data with boot.GameData.Read, stage blocks with BootDefinitions.For(boot), and do the rest in OnLoad",
        Boot, DiagnosticSeverity.Warning,
        "OnBoot and [BootThreadOnly] code run once per process on the boot thread, before the game's entry point: the game has loaded none of " +
        "its data and no world exists. A tick-thread API throws there, and a service's live API (World, Players, Content, Items, Chat, ...) " +
        "has nothing to act on. The game's data is read through the offline view (BootContext.GameData), definitions are staged with " +
        "BootDefinitions, and everything else waits for OnLoad. Members marked [BootThreadOnly] and capability checks are fine; [AnyThread] " +
        "says which threads may call a member, not that a world exists.");

    private const string Contract = "Kiln.Contract";

    public static readonly DiagnosticDescriptor ResultDiscarded = Rule("KILN120",
        "Kiln result thrown away",
        "The result of '{0}' {1}: a refusal by the game (Ok false, with a Reason) goes unnoticed; check its Ok, or drop it on purpose with '_ = ...'",
        Contract, DiagnosticSeverity.Warning,
        "docs/kiln/modding/contract.md, the error rule: when the game refuses (the player left, the slot is full, it is " +
        "already set) a Kiln call does not throw, it returns a result (IKilnResult) with Ok false and a Reason. A result " +
        "nobody reads is a refusal nobody notices. '_ = call' says the drop is meant.");

    public static readonly DiagnosticDescriptor CapabilityUnchecked = Rule("KILN121",
        "Capability not checked before the call",
        "'{0}' needs capability {1} and throws KilnUnavailableException when it is off; ask Capabilities.Is({2}) first, or catch KilnUnavailableException",
        Contract, DiagnosticSeverity.Warning,
        "docs/kiln/modding/contract.md: a member marked [RequiresCapability(\"row\")] throws KilnUnavailableException " +
        "when that row is off (a game build Kiln does not know, a client in its menu, a server without the service). " +
        "Ask Capabilities.Is(\"row\") or the service's own ...Available property before the call, catch the exception, " +
        "list the row in kiln.mod.json \"features\", or mark the calling method [RequiresCapability(\"row\")] to pass the " +
        "requirement on to its callers.");

    private const string Blocks = "Kiln.Blocks";

    public static readonly DiagnosticDescriptor BlockName = Rule("KILN110",
        "Bad or duplicate block name",
        "{0}",
        Blocks, DiagnosticSeverity.Error,
        "A block's ids come from its name, so a name takes letters, digits, '-', '_' and '.', and one mod defines it once. " +
        "A wrong name throws BlockError when the mod loads.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor BlockReference = Rule("KILN111",
        "A name that is no block",
        "{0}",
        Blocks, DiagnosticSeverity.Warning,
        "A field that takes a block takes its name. The name is not one this mod defines in code or in blocks/*.json, or it is another kind of " +
        "block. It would throw BlockError when the mod loads. Another mod's block is \"other-mod:name\".",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor BlockClients = Rule("KILN112",
        "clients is too low for what the mod defines",
        "{0}",
        Blocks, DiagnosticSeverity.Error,
        "Leave KilnClients out and the build works it out from the blocks. A new item, spell or status needs Kiln on every client (\"kiln\"); " +
        "a block that names the mod's own files needs the mod itself (\"mod\"). Kiln refuses the block when the mod loads.",
        WellKnownDiagnosticTags.CompilationEnd);
}
