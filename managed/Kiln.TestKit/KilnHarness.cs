// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kiln.Host;

namespace Kiln.TestKit;

/// <summary>
/// A Kiln server with no game, for mod tests under <c>dotnet test</c>: a temp server folder with its <c>kiln/</c> tree,
/// mods loaded the way the game loads them (Kiln.Host's own loader, a collectible load context each), ticks driven by
/// hand, console commands, and every log line captured.
/// </summary>
/// <remarks>
/// <para><b>Threads.</b> The harness has its own tick thread: <see cref="Game.IsTickThread"/> is true there and nowhere
/// else, so a mod that touches tick-thread API from a worker fails as it does in the game. Every public call runs on that
/// thread and returns when done (an exception comes back with its stack); called from the tick thread it runs inline. Test
/// code reaches the tick thread with <see cref="Run(Action)"/>, and runs as a mod with <see cref="As(ModHandle, Action)"/>.</para>
/// <para><b>One at a time.</b> Kiln's state is process-wide, so one harness lives per process: a second constructor waits
/// until the first is disposed (parallel test classes take turns), then throws after
/// <see cref="KilnHarnessOptions.WaitForOtherHarness"/>.</para>
/// <para><b>Nothing pretends.</b> A Kiln service the kit has no fake for gets no native table, so the service's own
/// <c>Available</c> is false and its capability row says <c>offline</c> (<see cref="Capabilities"/>); <see cref="Coverage"/>
/// lists what is real, what is a recording fake and what is unavailable.</para>
/// <para><b>Faults.</b> A harness call during which a mod faulted (an exception Kiln's fault policy counts) throws
/// <see cref="ModFaultException"/> when the call is done; <see cref="KilnHarnessOptions.ThrowOnModFault"/> turns that off.</para>
/// </remarks>
public sealed partial class KilnHarness : IDisposable
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static string _aliveSince = "";
    private static long _serial;

    static KilnHarness() => KitBackend.Install();

    private readonly KilnHarnessOptions _options;
    private readonly TickThread _tick;
    private readonly HarnessHost _host;
    private readonly ModManager _mods;
    private readonly HashSet<EventSubscription> _subscriptionsBefore;
    private readonly EventHandler<UnobservedTaskExceptionEventArgs> _unobserved;
    private readonly bool _keep;
    private int _disposed;

    /// <summary>Creates the temp server folder, points Kiln at it and starts the tick thread. No mod is loaded and no step
    /// has run: <see cref="Load{TMod}(ModManifest?)"/> the mods, then <see cref="Step"/>.</summary>
    public KilnHarness(KilnHarnessOptions? options = null)
    {
        _options = options ?? new KilnHarnessOptions();
        if (KitBackend.Instance.Harness is { IsTickThread: true })
            throw new InvalidOperationException("a KilnHarness cannot be created on another harness's tick thread: harnesses run one at a time, and this one would wait forever for that one to end");
        if (!Gate.Wait(_options.WaitForOtherHarness))
            throw new TimeoutException($"another KilnHarness is still alive in this process ({_aliveSince}) after {_options.WaitForOtherHarness.TotalSeconds:0} s. " +
                                       "Harnesses run one at a time because Kiln's state is process-wide: dispose the other one (a `using` declaration) first.");
        try
        {
            _aliveSince = $"created {DateTime.Now:HH:mm:ss.fff} on thread {Environment.CurrentManagedThreadId} '{Thread.CurrentThread.Name}'";
            _keep = _options.KeepFolder || _options.ServerDirectory is not null || Environment.GetEnvironmentVariable("KILN_TESTKIT_KEEP") == "1";
            var temp = _options.TempDirectory ?? Path.GetTempPath();
            Interlocked.Increment(ref _serial);
            ServerDirectory = _options.ServerDirectory is { } reuse ? Path.GetFullPath(reuse) : Path.Combine(temp, $"kiln-testkit-{Guid.NewGuid():N}");
            KilnDirectory = Path.Combine(ServerDirectory, "kiln");
            if (_options.ServerDirectory is not null && Directory.Exists(ModsDirectory)) Directory.Delete(ModsDirectory, recursive: true);  // the mods load again
            foreach (var d in new[] { ConfigDirectory, DataDirectory, ModsDirectory, LogsDirectory, ControlDirectory, StateDirectory })
                Directory.CreateDirectory(d);
            OpenLogFile();

            KitBackend.Install();
            KitBackend.Instance.Attach(this);
            Capabilities = new HarnessCapabilities();
            KitBackend.Instance.Capabilities.Report(HarnessCapabilities.HarnessOwner, "tick", CapState.Available, CapReason.None,
                "the harness steps the server world by hand", KitBackend.CapabilityOwner);
            ResetStatics();
            _host = new HarnessHost(this);
            StartFakes();
            _subscriptionsBefore = new HashSet<EventSubscription>(Events.Subscriptions, ReferenceEqualityComparer.Instance);
            _mods = new ModManager(_host, ModsDirectory, DataDirectory, new ModManagerOptions
            {
                Side = ManifestSide.Server,
                KilnVersion = KitBackend.KilnVersion,
                KilnBuild = KilnBuild.Of(typeof(Mod).Assembly),
                HasFeature = Game.HasFeature,
                Faults = new FaultPolicy
                {
                    Limit = Math.Max(1, _options.FaultLimit),
                    WindowSeconds = Math.Max(0, _options.FaultWindowSeconds),
                    Clock = () => Volatile.Read(ref _virtualMs) / 1000.0,
                },
                CrashReports = _options.CrashReports
                    ? new CrashReports(Path.Combine(KilnDirectory, "crash-reports"), Path.Combine(LogsDirectory, "kiln.log"), SystemReport,
                        () => _mods!.Describe(), _host.Log)
                    : null,
                StateFile = Path.Combine(LogsDirectory, "mods.txt"),
                ModsJsonFile = Path.Combine(StateDirectory, "mods.json"),
                TestResultsDirectory = Path.Combine(LogsDirectory, "tests"),
                TestTimeout = _options.TestTimeout,
            });
            ModsBackend.Current = _mods;
            _unobserved = (_, e) =>
            {
                _mods.ReportUnobserved(e.Exception);
                e.SetObserved();
            };
            TaskScheduler.UnobservedTaskException += _unobserved;
            _tick = new TickThread($"kiln-testkit tick {_serial}", _options.CallTimeout);
            _tick.Invoke(() =>
            {
                BootServiceTick();
                return 0;
            });
            _host.Log(LogLevel.Info, $"testkit: server folder {ServerDirectory}; Kiln {KitBackend.KilnVersion} (mod API {Mods.RuntimeApiVersion}), " +
                                     $"no game: {ServiceNames.All.Count(n => !KitBackend.Instance.HasTable(n))} of {ServiceNames.All.Count} services unavailable (offline)");
        }
        catch
        {
            if (_unobserved is not null) TaskScheduler.UnobservedTaskException -= _unobserved;
            if (_mods is not null && ReferenceEquals(ModsBackend.Current, _mods)) ModsBackend.Current = null;
            _mods?.Dispose();
            _tick?.Close(TimeSpan.FromSeconds(5));
            KitBackend.Instance.Detach(this);
            CloseLogFile();
            Gate.Release();
            throw;
        }
    }

    // ---- paths ----

    /// <summary>The temp server folder (the game's install folder in a real server).</summary>
    public string ServerDirectory { get; }

    /// <summary><c>&lt;server&gt;/kiln</c>: what <c>KilnPaths.Root</c> points at while the harness lives.</summary>
    public string KilnDirectory { get; }

    /// <summary><c>kiln/config</c>: mod configs (<see cref="Config.Directory"/>).</summary>
    public string ConfigDirectory => Path.Combine(KilnDirectory, "config");

    /// <summary><c>kiln/data</c>: each mod's <see cref="ModContext.DataDirectory"/> is <c>data/&lt;id&gt;</c>.</summary>
    public string DataDirectory => Path.Combine(KilnDirectory, "data");

    /// <summary><c>kiln/mods</c>: where the harness puts each mod's folder.</summary>
    public string ModsDirectory => Path.Combine(KilnDirectory, "mods");

    /// <summary><c>kiln/logs</c>: <c>kiln.log</c> (every captured line), <c>mods.txt</c>, <c>tests/</c>.</summary>
    public string LogsDirectory => Path.Combine(KilnDirectory, "logs");

    /// <summary><c>kiln/control</c>: the console folder is <c>control/console</c> (Kiln.Chat polls it).</summary>
    public string ControlDirectory => Path.Combine(KilnDirectory, "control");

    /// <summary><c>kiln/state</c>: <c>mods.json</c> for the launcher.</summary>
    public string StateDirectory => Path.Combine(KilnDirectory, "state");

    /// <summary>Capability rows as mods see them offline, and the test's overrides.</summary>
    public HarnessCapabilities Capabilities { get; }

    internal bool IsTickThread => _tick?.IsCurrent ?? false;

    /// <summary>The harness's tick thread (the watchdog's handle on it).</summary>
    internal TickThread TickThread => _tick;

    /// <summary>The real loader the harness drives.</summary>
    internal ModManager Manager => _mods;

    // ---- calls ----

    private int _depth;
    private List<string>? _callFaults;
    private Dictionary<string, int>? _faultsBefore;

    /// <summary>Every public call: on the tick thread, with the fault check around the outermost one.</summary>
    internal T Call<T>(Func<T> work, [CallerMemberName] string subject = "")
    {
        ThrowIfDisposed();
        return _tick.Invoke(() => Outer(work), subject);
    }

    internal void Call(Action work, [CallerMemberName] string subject = "") => Call(() =>
    {
        work();
        return 0;
    }, subject);

    private T Outer<T>(Func<T> work)
    {
        var outer = _depth++ == 0;
        if (outer) BeginFaultWatch();
        T result;
        try
        {
            result = work();
        }
        catch
        {
            _depth--;
            if (outer) EndFaultWatch();
            throw;
        }
        _depth--;
        if (outer && EndFaultWatch() is { } faults && _options.ThrowOnModFault) throw new ModFaultException(faults);
        return result;
    }

    private void BeginFaultWatch()
    {
        lock (_faultLock) _callFaults = [];
        _faultsBefore = _mods.Mods.ToDictionary(m => m.Id, m => m.Faults, StringComparer.Ordinal);
    }

    // The faults Kiln logged during the call; a fault count that grew with no line seen still counts (a changed wording).
    private List<string>? EndFaultWatch()
    {
        List<string> seen;
        lock (_faultLock)
        {
            seen = _callFaults ?? [];
            _callFaults = null;
        }
        var before = _faultsBefore ?? [];
        _faultsBefore = null;
        foreach (var m in _mods.Mods)
            if (m.Faults > before.GetValueOrDefault(m.Id) && !seen.Exists(f => f.StartsWith($"mod {m.Id} ", StringComparison.Ordinal)))
                seen.Add($"mod {m.Id} faulted ({m.Faults} fault(s) since load): {m.LastFaultMessage}");
        return seen.Count > 0 ? seen : null;
    }

    private readonly object _faultLock = new();

    // ModManager.Fault's two lines: "mod <id> threw in <where> (n/limit): <exception>" and "mod <id> fault in <where> ...".
    private static readonly Regex FaultLine = new(@"^mod [a-z0-9][a-z0-9._-]* (threw|fault) in .+ \(\d+/\d+\): ", RegexOptions.CultureInvariant);

    private void NoteHostLine(LogLevel level, string text)
    {
        if (level != LogLevel.Error || !FaultLine.IsMatch(text)) return;
        lock (_faultLock) _callFaults?.Add(text);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(KilnHarness));
        if (_tick.AbandonReason is { } why) throw new HarnessStoppedException(why);
    }

    /// <summary>Runs <paramref name="action"/> on the tick thread, as Kiln (no mod).</summary>
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Call(action);
    }

    /// <summary>Runs <paramref name="func"/> on the tick thread, as Kiln (no mod), and returns its result.</summary>
    public T Run<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Call(func);
    }

    /// <summary>Runs <paramref name="action"/> on the tick thread as code of <paramref name="mod"/>: its
    /// <c>HostAccess.Current</c> and synchronization context are set as Kiln sets them around a mod's handlers, so what it
    /// registers belongs to the mod and an <c>await</c> comes back to the tick thread. An exception goes to the caller (it
    /// is the test's, not counted as the mod's fault).</summary>
    public void As(ModHandle mod, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        As(mod, () =>
        {
            action();
            return 0;
        });
    }

    /// <inheritdoc cref="As(ModHandle, Action)"/>
    public T As<T>(ModHandle mod, Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(func);
        return Call(() => AsCore(mod.Id, func));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private T AsCore<T>(string id, Func<T> func)
    {
        var mod = _mods.Find(id) ?? throw new InvalidOperationException($"mod {id} is not loaded ({EntryOf(id)?.State.ToString().ToLowerInvariant() ?? "unknown"})");
        if (mod.Disabled) throw new InvalidOperationException($"mod {id} is disabled by the fault policy");
        var previous = SynchronizationContext.Current;
        var previousMod = HostAccess.Current;
        SynchronizationContext.SetSynchronizationContext(mod.ModContext.Scheduler.SynchronizationContext);
        HostAccess.Current = mod.ModContext;
        try
        {
            return func();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            HostAccess.Current = previousMod;
        }
    }

    // ---- statics ----

    /// <summary>The fake native tables' state for this harness (KilnHarness.World.cs), after the statics are reset.</summary>
    partial void StartFakes();

    /// <summary>The fakes end with the harness: players gone, the world closed.</summary>
    partial void EndFakes();

    private static bool _configUsed;

    static void OnConfigRegistry() => _configUsed = true;

    /// <summary>Kiln's process-wide state, as a fresh server has it. Only what is safe across harness instances: services
    /// register their tick handlers and event subscriptions once, in static constructors, so neither
    /// <c>ServiceTick.Reset</c> nor <c>Events.Reset</c> is called.</summary>
    private void ResetStatics()
    {
        KilnPaths.Root = KilnDirectory;
        KilnPaths.Config = null!;  // <root>/config
        ResetConfig();
        Config.RegistryChanged -= OnConfigRegistry;
        Config.RegistryChanged += OnConfigRegistry;
        Lifecycle.Phase = ServerPhase.Booting;
        Lifecycle.ModsLoaded = false;
        Lifecycle.StartedAtStep = 0;
        GameLanguageSource.ResetForTests();
        ServiceTick.HostDriven = true;  // the harness runs Kiln's service tick itself, before the mods' Pre
        ReloadPermissionsIfLoaded();
    }

    /// <summary>Config's watcher and every registered file belong to the folder they were made for: after a harness that
    /// registered a config they are dropped (Config.Reset), keeping Kiln.Saves' per-world provider. Mods' configs are
    /// gone with their mods by then; a service's own config (Kiln.Rules, Kiln.Network, Kiln.Quests) is dropped too.</summary>
    private static void ResetConfig()
    {
        if (!_configUsed) return;
        _configUsed = false;
        if (WorldConfigs.DirectoryProvider is null)
        {
            // Config.Reset marks Kiln.Saves as tried: start it first, as the first per-world config would have.
            try
            {
                if (Type.GetType("Kiln.SavesRuntime, Kiln.Saves") is { } t) RuntimeHelpers.RunModuleConstructor(t.Module.ModuleHandle);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException)
            {
                // no Kiln.Saves next to the kit: per-world configs stay in memory, as in Kiln's own unit tests
            }
        }
        var provider = WorldConfigs.DirectoryProvider;
        Config.Reset();
        WorldConfigs.DirectoryProvider = provider;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadPermissionsIfLoaded()
    {
        // permissions.json follows KilnPaths.Config; a store that was read for an earlier harness reads the new one
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Kiln.Permissions")) ReloadPermissions();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReloadPermissions() => Permissions.Reload();

    private string SystemReport() =>
        $"Kiln.TestKit (no game): Kiln {KitBackend.KilnVersion}, mod API {Mods.RuntimeApiVersion}\n.NET: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}\n" +
        $"Process: pid {Environment.ProcessId}, step {CurrentStep}, phase {Lifecycle.Phase}\n";

    // ---- dispose ----

    /// <summary>Stops the server (<see cref="ServerStoppingEvent"/>, every mod's <c>OnUnload</c> in reverse load order),
    /// logs any mod whose load context outlived the stop, ends the tick thread, and removes the temp folder (unless
    /// <see cref="KilnHarnessOptions.KeepFolder"/> or <c>KILN_TESTKIT_KEEP=1</c>). Leaves Kiln's static state ready for
    /// the next harness. A stop blocked past <see cref="KilnHarnessOptions.StopTimeout"/> is stopped by the watchdog,
    /// the remaining mods are retired without callbacks, and an exception reports where it blocked.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Exception? failure = null;
        try
        {
            if (_tick.AbandonReason is null)
            {
                var previousWatchdog = _tick.Watchdog;
                _tick.Watchdog = new Watchdog { Budget = _options.StopTimeout, Subject = "the server stop" };
                try
                {
                    _tick.Invoke(() =>
                    {
                        ShutDown();
                        return 0;
                    });
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    _tick.Watchdog = previousWatchdog;
                }
                if (_tick.AbandonReason is null) _tick.Close(TimeSpan.FromSeconds(10));
            }
            if (_tick.AbandonReason is not null) RetireAbandoned();  // the watchdog stopped the tick thread: retire the mods without it (KilnHarness.Tests.cs)
            TaskScheduler.UnobservedTaskException -= _unobserved;
            _mods.Dispose();
            EndFakes();
            RemoveTestSubscriptions();
            if (ReferenceEquals(ModsBackend.Current, _mods)) ModsBackend.Current = null;
            Lifecycle.Phase = ServerPhase.Booting;
            Lifecycle.ModsLoaded = false;
            Lifecycle.StartedAtStep = 0;
            GameLanguageSource.ResetForTests();
            Capabilities.Clear();
            KitBackend.Instance.Capabilities.Withdraw(HarnessCapabilities.HarnessOwner, "tick");
            ResetConfig();
            KilnPaths.Root = null!;
            KilnPaths.Config = null!;
            KitBackend.Instance.Detach(this);
            CloseLogFile();
            if (!_keep)
            {
                try
                {
                    Directory.Delete(ServerDirectory, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a file a mod still holds open: the folder stays in the temp folder
                }
            }
        }
        finally
        {
            Gate.Release();
        }
        if (failure is not null) throw new InvalidOperationException($"the harness did not stop cleanly: {failure.Message}", failure);
    }

    // Test code's own subscriptions (no mod) made while the harness lived: they would run in the next harness. Kiln's
    // services subscribe without a mod too (in their static constructors, once per process) and stay.
    private void RemoveTestSubscriptions()
    {
        foreach (var s in Events.Subscriptions)
        {
            if (s.Mod is not null || _subscriptionsBefore.Contains(s)) continue;
            var asm = s.Handler.Method.Module.Assembly.GetName().Name ?? "";
            var kilnOwn = asm.StartsWith("Kiln.", StringComparison.Ordinal) && !asm.EndsWith(".Tests", StringComparison.Ordinal) && !asm.EndsWith(".Test", StringComparison.Ordinal);
            if (!kilnOwn) Events.Remove(s);
        }
    }

    /// <summary>The <see cref="IHostServices"/> the loader runs on: owners from the kit's backend, lines as "host".</summary>
    private sealed class HarnessHost(KilnHarness harness) : IHostServices
    {
        public readonly HashSet<ulong> Revoked = [];

        public ulong OwnerCreate(string id) => KitBackend.Instance.OwnerCreate(id);

        public void OwnerRevoke(ulong owner)
        {
            lock (Revoked) Revoked.Add(owner);
        }

        public void Log(LogLevel level, string text)
        {
            harness.NoteHostLine(level, text);
            harness.Capture(level, "host", text);
        }
    }
}
