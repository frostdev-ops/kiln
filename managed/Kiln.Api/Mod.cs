// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// Base class of a Kiln mod. Kiln creates one instance per load, in the mod's own collectible load context; a hot
/// reload unloads that context and creates a fresh instance from the files on disk.
/// </summary>
/// <remarks>
/// <see cref="OnLoad"/> and <see cref="OnUnload"/> run on the server's tick thread. Everything the mod registers
/// through its <see cref="Context"/> (tick handlers, game-log handlers, scheduled work, IMC handlers, event
/// subscriptions) is removed automatically on unload; a mod only has to release what it created itself (threads,
/// files, sockets).
/// Mods load in dependency order (see <c>docs/kiln/modding/manifest.md</c>): when <see cref="OnLoad"/> runs, every
/// required dependency has finished its own <c>OnLoad</c>. <see cref="ModsLoadedEvent"/> follows once all mods are in.
/// </remarks>
public abstract class Mod
{
    /// <summary>The mod's view of Kiln. Set before <see cref="OnLoad"/>.</summary>
    public ModContext Context { get; internal set; } = null!;

    /// <summary>Shortcut for <c>Context.Log</c>.</summary>
    protected Logger Log => Context.Log;

    /// <summary>A text of the mod, in the game's language (<see cref="Game.Language"/>): <c>Text("shop.buy")</c>,
    /// <c>Text("shop.price", 12)</c> for <c>{0}</c>, <c>Text("welcome", new { name = "Ada" })</c> for <c>{name}</c>. The
    /// strings are the mod's <c>locales/&lt;language&gt;.json</c> files; a language without the key uses its main variety and
    /// then English, and a key no file has comes back as the key. Shortcut for <c>Context.Locale.Get</c>. Any thread.</summary>
    protected string Text(string key, params object?[] args) => Context.Locale.Get(key, args);

    /// <summary>Like <see cref="Text"/> in another language (e.g. one a player chose in your own settings).</summary>
    protected string TextIn(string language, string key, params object?[] args) => Context.Locale.GetIn(language, key, args);

    private Dictionary<(Type, string?), ConfigFile>? _worldConfigs;

    /// <summary>The mod's settings for the world that is open: <c>WorldConfig&lt;MySettings&gt;().Value.Difficulty</c>.
    /// Registers them on the first call (see <see cref="Config.RegisterWorld{T}"/>) and gives the same object afterwards. Tick
    /// thread.</summary>
    /// <param name="name">For a second per-world config of the mod.</param>
    [TickThreadOnly]
    protected WorldConfig<T> WorldConfig<T>(string? name = null) where T : class, new()
    {
        if (RegistryContext.Current is not null) return Config.RegisterWorld<T>(name);
        Game.AssertTickThread();
        if (!Context.Alive) throw new InvalidOperationException("the mod is unloading");
        _worldConfigs ??= new();
        if (_worldConfigs.TryGetValue((typeof(T), name), out var have)) return (WorldConfig<T>)have;
        var previous = ModContext.CurrentMod;
        ModContext.CurrentMod = Context;
        try
        {
            var cfg = Config.RegisterWorld<T>(name);
            _worldConfigs[(typeof(T), name)] = cfg;
            return cfg;
        }
        finally
        {
            ModContext.CurrentMod = previous;
        }
    }

    /// <summary>Called once after the mod is loaded.</summary>
    public virtual void OnLoad() { }

    /// <summary>The mod's boot phase (its kiln.mod.json says <c>"boot": true</c> or <c>"required"</c>): called once per process, on the boot
    /// thread before the game is entered, on a separate instance that is unloaded right after; stage block definitions with Kiln.Blocks'
    /// <c>BootDefinitions.For(boot)</c>. No tick, world, player or live registry exists here; later instances get <see cref="OnLoad"/> only.</summary>
    [BootThreadOnly, RequiresCapability("mods.boot")]
    public virtual void OnBoot(BootContext boot) { }

    /// <summary>Authors registry edits at the declared points (default: GameStart). Called once per add/alter/final pass;
    /// use Kiln.Registry.Registry.For(context). GameStart uses a retained author instance, not the transient OnBoot owner.
    /// Only world-load or later frames may read per-world settings. The context expires when this call returns. A WorldLoad pass
    /// re-authored on request (Registry.Reauthor) runs on a worker thread of Kiln's, not the world's (0.2.0-dev.32).</summary>
    [RegistryThreadOnly, System.Diagnostics.CodeAnalysis.Experimental("KILNX031")]
    public virtual void OnRegistry(RegistryContext context) { }

    /// <summary>Called once before the mod is unloaded (server shutdown, reload, unload of a dependency, or disable).</summary>
    public virtual void OnUnload() { }
}

/// <summary>Identity and metadata of a loaded mod, from its <c>kiln.mod.json</c>.</summary>
/// <param name="Id">Unique id (<c>[a-z0-9][a-z0-9._-]*</c>, at most 64 characters).</param>
/// <param name="Name">Display name.</param>
/// <param name="Version">The mod's version (semantic versioning).</param>
/// <param name="Directory">The mod's folder (<c>kiln/mods/&lt;folder&gt;</c>).</param>
public sealed record ModInfo(string Id, string Name, string Version, string Directory)
{
    /// <summary>Where the mod runs: <c>server</c> (default), <c>client</c> or <c>both</c>.</summary>
    public ModSide Side { get; init; } = ModSide.Server;

    /// <summary>The mod's description from <c>kiln.mod.json</c>; empty when it has none.</summary>
    public string Description { get; init; } = "";
    /// <summary>The mod's authors from <c>kiln.mod.json</c>.</summary>
    public IReadOnlyList<string> Authors { get; init; } = [];
    /// <summary>The license the manifest names; null when it names none.</summary>
    public string? License { get; init; }
    /// <summary>The web address the manifest names; null when it names none.</summary>
    public string? Url { get; init; }

    /// <summary>The Kiln API range the mod declared (<c>apiVersion</c>).</summary>
    public string ApiVersion { get; init; } = "";

    /// <summary>Declared dependencies (required and optional), in manifest order.</summary>
    public IReadOnlyList<ModDependency> Dependencies { get; init; } = [];

    /// <summary>Assemblies (file names) that dependent mods compile against and share at run time.</summary>
    public IReadOnlyList<string> Exports { get; init; } = [];

    /// <summary>Kiln features the mod requires (<see cref="Game.HasFeature"/>).</summary>
    public IReadOnlyList<string> Features { get; init; } = [];

    /// <summary>The mod opted into the advanced tier (raw hooks, symbols, coremod-style patches). Kiln may refuse or
    /// sandbox advanced mods by configuration; services gate their unstable entry points on it.</summary>
    public bool Advanced { get; init; }

    /// <summary>The mod talks to Steam users directly: its kiln.mod.json says <c>"steamMessages": true</c>, so it may open
    /// its own Steam peer message leases (<c>SteamMessages.Open</c> in Kiln.Network refuses every other mod). Since
    /// 0.2.0-dev.30.3.</summary>
    public bool SteamMessages { get; init; }
}

/// <summary>Everything a mod gets from Kiln. One per loaded mod instance.</summary>
public sealed class ModContext
{
    internal ModContext(ModInfo info, Logger log, string dataDirectory)
    {
        Info = info;
        Log = log;
        DataDirectory = dataDirectory;
        Tick = new TickEvents();
        GameLog = new GameLogEvents();
        Scheduler = new Scheduler(this);
        Messages = new ModMessages(this);
        Tasks = new KilnTasks(this);
    }

    /// <summary>The mod's identity and metadata (its <c>kiln.mod.json</c>).</summary>
    public ModInfo Info { get; }
    /// <summary>The mod's logger: lines go to <c>kiln.log</c> tagged with the mod's id.</summary>
    public Logger Log { get; }

    /// <summary>Server-world tick events (24.5 Hz on a dedicated server).</summary>
    public TickEvents Tick { get; }

    /// <summary>Lines of the game's own log, delivered on the tick thread.</summary>
    public GameLogEvents GameLog { get; }

    /// <summary>Run work on the tick thread from anywhere; await the next tick.</summary>
    public Scheduler Scheduler { get; }

    /// <summary>Inter-mod messages sent to this mod with <see cref="Mods.Send"/> (Forge's InterModComms).</summary>
    public ModMessages Messages { get; }

    /// <summary>Run heavy work off the tick, on Kiln's own worker threads, attributed to this mod
    /// (<see cref="KilnTasks"/>).</summary>
    public KilnTasks Tasks { get; }

    /// <summary>Per-mod persistent directory (<c>kiln/data/&lt;id&gt;</c>), created on first use.</summary>
    public string DataDirectory { get; }

    private ModLocale? _locale;

    /// <summary>The mod's strings from <c>locales/&lt;language&gt;.json</c> (see <see cref="Mod.Text"/>).</summary>
    public ModLocale Locale => _locale ??= ModLocale.Of(this);

    private volatile bool _alive = true;
    internal bool Alive => _alive;

    // A snapshot's exact metadata object identifies its loaded instance. Weak values avoid retaining a retired
    // context, and reference keys keep equal mod ids (or client/server instances) from sharing a lifetime.
    private static readonly object LiveGate = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ModInfo, WeakReference<ModContext>> LiveByInfo = new();

    internal static void TrackLive(ModContext mod)
    {
        lock (LiveGate) LiveByInfo.AddOrUpdate(mod.Info, new WeakReference<ModContext>(mod));
    }

    internal static ModContext? FindLive(ModInfo info)
    {
        lock (LiveGate)
            return LiveByInfo.TryGetValue(info, out var weak) && weak.TryGetTarget(out var mod) && mod.Alive ? mod : null;
    }

    /// <summary>Native owner id: everything this mod registers in the core is revoked with it.</summary>
    internal ulong Owner { get; init; }

    /// <summary>Services use this to attribute work (and callbacks) to the calling mod. Tick thread only.</summary>
    [ThreadStatic] internal static ModContext? CurrentMod;

    // Which mod a load context belongs to. Weak, so it never holds an unloaded mod's context.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Runtime.Loader.AssemblyLoadContext, ModContext> ByLoadContext = new();

    internal static void Remember(System.Reflection.Assembly assembly, ModContext context)
    {
        if (System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly) is { IsCollectible: true } alc)
            ByLoadContext.AddOrUpdate(alc, context);
    }

    /// <summary>The mod whose code <paramref name="work"/> runs. For work handed over from a background thread, where
    /// <see cref="CurrentMod"/> is null. Null when it is not a mod's delegate.</summary>
    internal static ModContext? OwnerOf(Delegate work)
    {
        var assembly = work.Method.Module.Assembly;
        // an await continuation is the runtime's box around the compiler's state machine, a type of the mod
        if (work.Target?.GetType() is { IsGenericType: true } box && box.Assembly == typeof(object).Assembly)
            assembly = box.GetGenericArguments()[^1].Assembly;
        return System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly) is { } alc && ByLoadContext.TryGetValue(alc, out var mod) ? mod : null;
    }

    /// <summary>Runs when the mod's unload begins, before its OnUnload (and before <see cref="Retiring"/>): services
    /// that call into the mod's code from other threads stop letting new calls in here and wait a bounded time for the
    /// calls inside (kiln.abilities' sync natives and attachments). A step returns null when no call it let in is still
    /// inside, else what is (the host then defers OnUnload and the disposal until it is null: <see cref="UnloadWaitsFor"/>
    /// asks the same steps again, which only look). A throwing step is logged and counts as nothing inside; it stops
    /// neither the unload nor the other steps. Tick thread.</summary>
    internal event Func<string?>? UnloadStarting;

    private int _unloadStarted;
    internal bool Unloading => Volatile.Read(ref _unloadStarted) != 0;
    private Func<string?>[] _unloadSteps = [];

    /// <summary>Kiln's task system seals this mod's tasks here (set by <see cref="KilnTasks"/> for its mod): run
    /// once when the unload begins, in <see cref="BeginUnload"/> before the <see cref="UnloadStarting"/> steps. It
    /// seals the mod's task admission at the pool and signals every live task through its token; a queued task ends
    /// cancelled (its continuations still run). Never waits: a task that ignores its token keeps running on its
    /// worker, and the host joins it off the tick (its load context stays pinned meanwhile).</summary>
    internal Action? UnloadTaskSeal;

    /// <summary>The mod's live tasks as <c>'name' (State)</c> joined by ", ", or null when none is live. The host
    /// polls this off the tick while it joins the mod's tasks after the unload; nothing on the tick ever waits on
    /// it, and <see cref="Mod.OnUnload"/> never does.</summary>
    internal Func<string?>? UnloadTaskBlocker;

    /// <summary>What keeps this mod's tasks from being quiescent after its unload began, or null when they are.
    /// Off-tick callers only (the host's task join); a throwing probe counts as quiescent, since the pool's own
    /// lease pins still hold the mod's load context until each lease ends.</summary>
    internal string? TaskBlocker()
    {
        try
        {
            return UnloadTaskBlocker?.Invoke();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Begins the unload (once per context): runs the <see cref="UnloadStarting"/> steps. Null when nothing is
    /// inside the mod's code from another thread any more, else what is. The host calls it before OnUnload; Retire
    /// calls it too, for an unload without OnUnload (a harness that gave up its tick thread).</summary>
    internal string? BeginUnload()
    {
        if (Interlocked.Exchange(ref _unloadStarted, 1) != 0) return UnloadWaitsFor();
        // The mod's tasks stop first: admission sealed and every live task signalled before anything can start new
        // work against the unloading mod. This never waits for a task to actually stop.
        try
        {
            UnloadTaskSeal?.Invoke();
        }
        catch (Exception e)
        {
            Log.Warn($"stopping this mod's tasks at the start of its unload threw: {e.GetType().Name}: {e.Message}");
        }
        _unloadSteps = UnloadStarting is { } starting ? Array.ConvertAll(starting.GetInvocationList(), d => (Func<string?>)d) : [];
        UnloadStarting = null;
        return AskUnloadSteps();
    }

    /// <summary>What the unload still waits for (null: nothing): the steps again, after <see cref="BeginUnload"/>.</summary>
    internal string? UnloadWaitsFor() => Volatile.Read(ref _unloadStarted) == 0 ? null : AskUnloadSteps();

    private string? AskUnloadSteps()
    {
        var waiting = Ask(_unloadSteps);
        // A service may subscribe from another service's start step. Run those additions too,
        // before the host can decide that disposal is safe, and retain them for later polls.
        while (UnloadStarting is { } added)
        {
            UnloadStarting = null;
            var steps = Array.ConvertAll(added.GetInvocationList(), d => (Func<string?>)d);
            _unloadSteps = [.. _unloadSteps, .. steps];
            if (Ask(steps) is { } more) waiting = waiting is null ? more : waiting + "; " + more;
        }
        return waiting;
    }

    private string? Ask(Func<string?>[] steps)
    {
        List<string>? inside = null;
        foreach (var step in steps)
        {
            string? what;
            try { what = step(); }
            catch (Exception e)
            {
                Log.Warn($"a service's step at the start of this mod's unload threw: {e.GetType().Name}: {e.Message}");
                what = null;
            }
            if (what is not null) (inside ??= []).Add(what);
        }
        return inside is null ? null : string.Join("; ", inside);
    }

    /// <summary>Runs when the mod unloads, after OnUnload: services drop per-mod state (pending requests,
    /// callbacks) here so the mod's load context can be collected.</summary>
    internal event Action? Retiring;

    internal readonly Queue<Action> Pending = new();
    internal readonly object PendingLock = new();

    internal void Retire()
    {
        if (Volatile.Read(ref _unloadStarted) == 0) BeginUnload();  // the host began it before OnUnload, as a rule
        lock (LiveGate)
        {
            if (!Alive) return;
            _alive = false;
            if (LiveByInfo.TryGetValue(Info, out var weak) && weak.TryGetTarget(out var current) && ReferenceEquals(current, this)) LiveByInfo.Remove(Info);
        }
        if (Retiring is { } retiring)
            foreach (Action cleanup in retiring.GetInvocationList())
                try { cleanup(); } catch { /* one service's cleanup must not stop the others or the unload */ }
        Retiring = null;
        Tick.Clear();
        GameLog.Clear();
        Messages.Clear();
        lock (PendingLock) Pending.Clear();
    }
}
