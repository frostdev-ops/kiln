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

    public string Description { get; init; } = "";
    public IReadOnlyList<string> Authors { get; init; } = [];
    public string? License { get; init; }
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
    }

    public ModInfo Info { get; }
    public Logger Log { get; }

    /// <summary>Server-world tick events (24.5 Hz on a dedicated server).</summary>
    public TickEvents Tick { get; }

    /// <summary>Lines of the game's own log, delivered on the tick thread.</summary>
    public GameLogEvents GameLog { get; }

    /// <summary>Run work on the tick thread from anywhere; await the next tick.</summary>
    public Scheduler Scheduler { get; }

    /// <summary>Inter-mod messages sent to this mod with <see cref="Mods.Send"/> (Forge's InterModComms).</summary>
    public ModMessages Messages { get; }

    /// <summary>Per-mod persistent directory (<c>kiln/data/&lt;id&gt;</c>), created on first use.</summary>
    public string DataDirectory { get; }

    private ModLocale? _locale;

    /// <summary>The mod's strings from <c>locales/&lt;language&gt;.json</c> (see <see cref="Mod.Text"/>).</summary>
    public ModLocale Locale => _locale ??= ModLocale.Of(this);

    internal bool Alive { get; private set; } = true;

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

    /// <summary>Runs when the mod unloads, after OnUnload: services drop per-mod state (pending requests,
    /// callbacks) here so the mod's load context can be collected.</summary>
    internal event Action? Retiring;

    internal readonly Queue<Action> Pending = new();
    internal readonly object PendingLock = new();

    internal void Retire()
    {
        if (!Alive) return;
        Alive = false;
        try { Retiring?.Invoke(); } catch { /* a service's cleanup must not stop the unload */ }
        Retiring = null;
        Tick.Clear();
        GameLog.Clear();
        Messages.Clear();
        lock (PendingLock) Pending.Clear();
    }
}
