// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>How severe a log line is: from <see cref="Error"/> (1, the most severe) to <see cref="Trace"/> (5, the
/// finest detail).</summary>
public enum LogLevel
{
    /// <summary>Something failed.</summary>
    Error = 1,
    /// <summary>Something unexpected happened; the mod went on.</summary>
    Warn = 2,
    /// <summary>A normal event worth a line in the log.</summary>
    Info = 3,
    /// <summary>Detail for finding a problem.</summary>
    Debug = 4,
    /// <summary>The finest detail.</summary>
    Trace = 5,
}

/// <summary>Writes to <c>kiln/logs/kiln.log</c>, tagged with the mod's id. Thread-safe.</summary>
public sealed class Logger
{
    private readonly ulong _owner;

    internal Logger(ulong owner) => _owner = owner;

    /// <summary>Writes a line at <paramref name="level"/>.</summary>
    public void Write(LogLevel level, string message) => Backend.Current.Log(_owner, level, message);
    /// <summary>Writes an <see cref="LogLevel.Error"/> line.</summary>
    public void Error(string message) => Write(LogLevel.Error, message);
    /// <summary>Writes a <see cref="LogLevel.Warn"/> line.</summary>
    public void Warn(string message) => Write(LogLevel.Warn, message);
    /// <summary>Writes an <see cref="LogLevel.Info"/> line.</summary>
    public void Info(string message) => Write(LogLevel.Info, message);
    /// <summary>Writes a <see cref="LogLevel.Debug"/> line.</summary>
    public void Debug(string message) => Write(LogLevel.Debug, message);
    /// <summary>Writes an <see cref="LogLevel.Error"/> line: the message, then the exception (its type, message and
    /// stack).</summary>
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

    /// <summary>What <see cref="Scheduler.NextTick"/> returns: <c>await</c> it to continue on the tick thread at the
    /// next step.</summary>
    public readonly struct NextTickAwaitable(Scheduler scheduler)
    {
        /// <summary>The awaiter the <c>await</c> uses.</summary>
        public Awaiter GetAwaiter() => new(scheduler);

        /// <summary>The awaiter of <see cref="NextTickAwaitable"/>.</summary>
        public readonly struct Awaiter(Scheduler scheduler) : System.Runtime.CompilerServices.ICriticalNotifyCompletion
        {
            /// <summary>Always false: the continuation is posted to the tick thread
            /// (<see cref="Scheduler.Post"/>).</summary>
            public bool IsCompleted => false;
            /// <summary>Posts <paramref name="continuation"/> to the tick thread.</summary>
            public void OnCompleted(Action continuation) => scheduler.Post(continuation);
            /// <summary>Posts <paramref name="continuation"/> to the tick thread.</summary>
            public void UnsafeOnCompleted(Action continuation) => scheduler.Post(continuation);
            /// <summary>Returns nothing: the await has no value.</summary>
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
    /// <summary>The version of the Kiln that is running.</summary>
    public static string KilnVersion => Backend.Current.Version;

    /// <summary>True on the server-world tick thread, where game state may be touched. False in a Kiln task's menu hop
    /// (<see cref="WorldKind.Menu"/>), where no world runs.</summary>
    public static bool IsTickThread => !TaskExecutionScope.InMenuHop && Backend.Current.IsTickThread;

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
        if (SliceGuard.Depth > 0)
            throw new InvalidOperationException("This cannot run in a program wrap's handler (it runs inside the world's step); use slice.Post.");
        if (TaskExecutionScope.InMenuHop) throw new InvalidOperationException(TaskExecutionScope.MenuHopRefusal(CallerName()));
        if (IsTickThread) return;
        if (OffTickPass) throw OffTick(CallerName());
        throw new InvalidOperationException(InBoot ? NotInBoot(CallerName()) : "This must run on the tick thread; use Scheduler.Post.");
    }

    /// <summary>As <see cref="AssertTickThread()"/>, naming <paramref name="member"/> in OnBoot.</summary>
    internal static void AssertTickThread(string member)
    {
        if (SliceGuard.Depth > 0)
            throw new InvalidOperationException($"{member} cannot run in a program wrap's handler (it runs inside the world's step); use slice.Post.");
        if (TaskExecutionScope.InMenuHop) throw new InvalidOperationException(TaskExecutionScope.MenuHopRefusal(member));
        if (IsTickThread) return;
        if (OffTickPass) throw OffTick(member);
        throw new InvalidOperationException(InBoot ? NotInBoot(member) : "This must run on the tick thread; use Scheduler.Post.");
    }

    /// <summary>A re-authored registry pass (Registries.Reauthor, 0.2.0-dev.32) runs on a worker thread of Kiln.Host's, off the
    /// world tick. A tick-thread call in it is refused and noted (<see cref="OffTickRefused"/>): Kiln.Host then runs that mod's
    /// passes on the tick, so a mod that caught the refusal never lands a result built around it. Every tick-thread check
    /// notes it: <see cref="AssertTickThread()"/>, Services.RequireTickThread (most services' members) and a service's own
    /// <c>if (!Game.IsTickThread) throw</c> (<see cref="TickRefusal"/>), the checks the analyzers' KILN101 table is made of.</summary>
    [ThreadStatic] internal static bool OffTickPass;
    [ThreadStatic] internal static string? OffTickRefused;
    /// <summary>A tick-thread member's refusal off the tick: in a re-authored registry pass noted and named as such, else
    /// <paramref name="otherwise"/>.</summary>
    internal static Exception TickRefusal(string member, Exception otherwise) => OffTickPass ? OffTick(member) : otherwise;
    internal static InvalidOperationException OffTick(string member)
    {
        OffTickRefused ??= member;
        return new InvalidOperationException($"{member} must run on the tick thread, and a re-authored registry pass (Registries.Reauthor) runs off it: " +
            "read game state before asking for the pass and keep it in the mod, or Kiln runs this mod's passes on the tick");
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
