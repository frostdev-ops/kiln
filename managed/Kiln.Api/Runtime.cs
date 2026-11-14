// SPDX-License-Identifier: MIT
namespace Kiln;

public enum LogLevel
{
    Error = 1,
    Warn = 2,
    Info = 3,
    Debug = 4,
    Trace = 5,
}

/// <summary>Writes to <c>kiln/logs/kiln.log</c>, tagged with the mod's id. Thread-safe.</summary>
public sealed class Logger
{
    private readonly ulong _owner;

    internal Logger(ulong owner) => _owner = owner;

    public void Write(LogLevel level, string message) => Backend.Current.Log(_owner, level, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Warn(string message) => Write(LogLevel.Warn, message);
    public void Info(string message) => Write(LogLevel.Info, message);
    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Error(Exception e, string message) => Write(LogLevel.Error, $"{message}: {e}");
}

/// <summary>Gets work onto the tick thread.</summary>
public sealed class Scheduler
{
    private readonly ModContext _context;

    internal Scheduler(ModContext context)
    {
        _context = context;
        SynchronizationContext = new ModSynchronizationContext(this);
    }

    /// <summary>Runs <paramref name="action"/> on the tick thread before the next step's Pre handlers. Thread-safe.
    /// Work still queued when the mod unloads is dropped.</summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_context.PendingLock)
        {
            if (!_context.Alive) return;
            _context.Pending.Enqueue(action);
        }
    }

    /// <summary><c>await Scheduler.NextTick()</c> continues on the tick thread at the next step.</summary>
    public NextTickAwaitable NextTick() => new(this);

    /// <summary>The context Kiln installs while the mod's code runs on the tick thread: <c>await</c> without
    /// <c>ConfigureAwait(false)</c> comes back to the tick thread.</summary>
    public SynchronizationContext SynchronizationContext { get; }

    public readonly struct NextTickAwaitable(Scheduler scheduler)
    {
        public Awaiter GetAwaiter() => new(scheduler);

        public readonly struct Awaiter(Scheduler scheduler) : System.Runtime.CompilerServices.ICriticalNotifyCompletion
        {
            public bool IsCompleted => false;
            public void OnCompleted(Action continuation) => scheduler.Post(continuation);
            public void UnsafeOnCompleted(Action continuation) => scheduler.Post(continuation);
            public void GetResult() { }
        }
    }

    private sealed class ModSynchronizationContext(Scheduler scheduler) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => scheduler.Post(() => d(state));

        public override void Send(SendOrPostCallback d, object? state)
        {
            if (!Game.IsTickThread) throw new InvalidOperationException("Send is only allowed on the tick thread");
            d(state);
        }

        public override SynchronizationContext CreateCopy() => this;
    }
}

/// <summary>Process-wide facts about the running server and Kiln.</summary>
public static class Game
{
    public static string KilnVersion => Backend.Current.Version;

    /// <summary>True on the server-world tick thread, where game state may be touched.</summary>
    public static bool IsTickThread => Backend.Current.IsTickThread;

    /// <summary>The language mod texts are shown in, as a lower-case tag: <c>"en-us"</c>, <c>"de-de"</c>, <c>"pt-br"</c>
    /// (the game's own language keys). A dedicated server always says <c>"en-us"</c>. A game client says the language of the
    /// game's texts, and <c>"en-us"</c> before the game's UI has built a frame or when this build cannot tell. Any thread.
    /// <see cref="ModLocale.LookupOrder"/> turns it into the files that are tried.</summary>
    public static string Language => GameLanguageSource.Current();

    /// <summary>Whether a Kiln capability (e.g. <c>"tick"</c>, <c>"log.mirror"</c>, <c>"items.fuel"</c>) works here:
    /// <see cref="Capabilities.Is"/>, which also knows services (<c>"kiln.items"</c>) and the rows they register.
    /// <see cref="Capabilities.Why"/> says why one is off. Any thread.</summary>
    [AnyThread]
    public static bool HasFeature(string name) => Capabilities.Is(name);

    /// <summary>Throws unless this is the tick thread. In <see cref="Mod.OnBoot"/> (before the game's first tick, where
    /// Scheduler.Post cannot help) the message names the member and says to call it in OnLoad.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]  // frame 1 is the caller
    public static void AssertTickThread()
    {
        if (IsTickThread) return;
        throw new InvalidOperationException(InBoot ? NotInBoot(CallerName()) : "This must run on the tick thread; use Scheduler.Post.");
    }

    /// <summary>As <see cref="AssertTickThread()"/>, naming <paramref name="member"/> in OnBoot.</summary>
    internal static void AssertTickThread(string member)
    {
        if (IsTickThread) return;
        throw new InvalidOperationException(InBoot ? NotInBoot(member) : "This must run on the tick thread; use Scheduler.Post.");
    }

    // The member that called AssertTickThread() (two frames up from here), as "Type.Member".
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static string CallerName()
    {
        var method = new System.Diagnostics.StackFrame(2, false).GetMethod();
        if (method is null || method.Name.Contains('<')) return "This call";
        var type = method.DeclaringType;
        while (type is { IsNested: true } && type.Name.Contains('<')) type = type.DeclaringType;  // a lambda or iterator of a member
        var typeName = type is null ? "" : type.Name.Split('`')[0];
        var name = method.Name switch
        {
            ".ctor" => "new " + typeName,
            var n when n.StartsWith("get_", StringComparison.Ordinal) || n.StartsWith("set_", StringComparison.Ordinal) => n[4..],
            var n when n.StartsWith("add_", StringComparison.Ordinal) => n[4..],
            var n when n.StartsWith("remove_", StringComparison.Ordinal) => n[7..],
            var n => n,
        };
        return method.Name == ".ctor" || typeName.Length == 0 ? name : typeName + "." + name;
    }

    /// <summary>A mod's <see cref="Mod.OnBoot"/> is running on this thread: its boot-author frame is open (Kiln.Host's boot phase,
    /// <see cref="BootFrame"/>), before the game's first tick.</summary>
    internal static bool InBoot => BootFrame.Current is { Open: true };

    /// <summary>What a member says when it cannot run in OnBoot.</summary>
    internal static string NotInBoot(string member) =>
        $"{member} is not available in OnBoot (it runs before the game's first tick); call it in OnLoad";
}

/// <summary>Seam between the public API and whatever hosts it (the native core, or a test fake).</summary>
internal interface IKilnBackend
{
    string Version { get; }
    bool IsTickThread { get; }
    bool HasFeature(string name);
    void Log(ulong owner, LogLevel level, string text);

    /// <summary>Native service table (see native/core/abi/services), or 0 when unavailable.</summary>
    nint Service(string name, uint minVersion);

    /// <summary>A new native owner (everything registered with it can be revoked together); 0 when there is no core.</summary>
    ulong OwnerCreate(string id) => 0;

    /// <summary>True when <see cref="OwnerRevoke"/> takes back what an owner registered (there is a core, or a test kit that plays one).</summary>
    bool CanRevokeOwners => false;

    /// <summary>Revokes an owner made by <see cref="OwnerCreate"/>: everything registered with it comes off. The owner is dead afterwards.</summary>
    void OwnerRevoke(ulong owner) { }
}

internal static class Backend
{
    public static IKilnBackend Current { get; set; } = new NullBackend();

    private sealed class NullBackend : IKilnBackend
    {
        public string Version => "0.0.0";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) => Console.Error.WriteLine($"[{level}] {text}");
        public nint Service(string name, uint minVersion) => 0;
    }
}

/// <summary>Constructors the host needs; not part of the mod API.</summary>
internal static class HostAccess
{
    public static ModContext CreateContext(ModInfo info, ulong owner, string dataDirectory)
    {
        var mod = new ModContext(info, new Logger(owner), dataDirectory) { Owner = owner };
        ModContext.TrackLive(mod);
        return mod;
    }

    /// <summary>The mod whose code is running on this thread (set by the host around every call into a mod).</summary>
    public static ModContext? Current
    {
        get => ModContext.CurrentMod;
        set => ModContext.CurrentMod = value;
    }

    public static void Bind(Mod mod, ModContext context)
    {
        mod.Context = context;
        ModContext.Remember(mod.GetType().Assembly, context);
    }

    public static TickInfo Tick(ulong step, uint clock, uint delta, long period, double wallMs) =>
        new(step, clock, delta, period, wallMs);
}
