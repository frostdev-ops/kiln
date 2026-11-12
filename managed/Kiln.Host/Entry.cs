// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Kiln.Testing;

namespace Kiln.Host;

/// <summary>Entry points the native core calls. Each one catches everything: an exception must never unwind into
/// game frames.</summary>
public static unsafe class Entry
{
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    public static int Init(void* api)
    {
        try
        {
            return HostRuntime.Start((KilnApi*)api);
        }
        catch (Exception e)
        {
            try
            {
                HostRuntime.EmergencyLog((KilnApi*)api, $"Kiln.Host failed to start: {e}");
            }
            catch
            {
                // nothing left to report to
            }
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnPre(KilnTickInfo* tick, void* user)
    {
        try
        {
            HostRuntime.Instance?.Pre(tick);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("Pre", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnPost(KilnTickInfo* tick, void* user)
    {
        try
        {
            HostRuntime.Instance?.Post(tick);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("Post", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnClientPre(KilnTickInfo* tick, void* user)
    {
        try
        {
            HostRuntime.Instance?.ClientPre(tick);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("client Pre", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnClientPost(KilnTickInfo* tick, void* user)
    {
        try
        {
            HostRuntime.Instance?.ClientPost(tick);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("client Post", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnUiTick(ulong frame, void* user)
    {
        try
        {
            HostRuntime.Instance?.UiTick(frame);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("UI tick", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnWorldEvent(KilnWorldEvent* ev, void* user)
    {
        try
        {
            HostRuntime.Instance?.WorldEvent(ev);
        }
        catch (Exception e)
        {
            HostRuntime.Instance?.HostFault("world event", e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void OnGameLogLine(int level, KilnStr text, void* user)
    {
        try
        {
            HostRuntime.Instance?.EnqueueGameLine(level, text.ToString());
        }
        catch
        {
            // runs on the core's log thread; dropping a line is the only safe failure
        }
    }
}

/// <summary>kiln.json <c>"mods"</c> section (all optional).</summary>
internal sealed record HostConfig(int FaultLimit, double FaultWindowSeconds, int StopTimeoutMs, double TestTimeoutSeconds, bool CrashReports,
    bool Hosted = true)
{
    public static readonly HostConfig Default = new(ModManager.MaxFaults, 60, 5000, 30, true);

    public static HostConfig Read(string kilnJson, Action<LogLevel, string> log)
    {
        if (!File.Exists(kilnJson)) return Default;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(kilnJson), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (!doc.RootElement.TryGetProperty("mods", out var m) || m.ValueKind != JsonValueKind.Object) return Default;
            double Num(string key, double fallback) => m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
            var crash = !m.TryGetProperty("crash_reports", out var c) || c.ValueKind != JsonValueKind.False;
            // client: side "server" mods load while it hosts a world unless this is false
            var hosted = !m.TryGetProperty("hosted", out var h) || h.ValueKind != JsonValueKind.False;
            return new HostConfig(Math.Max(1, (int)Num("fault_limit", Default.FaultLimit)), Math.Max(0, Num("fault_window_s", Default.FaultWindowSeconds)),
                Math.Clamp((int)Num("stop_timeout_ms", Default.StopTimeoutMs), 0, 60000), Math.Max(1, Num("test_timeout_s", Default.TestTimeoutSeconds)), crash,
                hosted);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            log(LogLevel.Warn, $"kiln.json: mods section unreadable ({e.Message}); defaults");
            return Default;
        }
    }
}

/// <summary>The managed side of Kiln: the backend behind <see cref="Kiln.Game"/>, and the tick-thread driver of
/// the mod manager, the lifecycle events, the control folder and the GC pacer.</summary>
internal sealed unsafe class HostRuntime : IHostServices, IKilnBackend
{
    private const long ControlEveryMs = 1000;   // by the clock: a 60 Hz world listed the folder 2.4x a second at 25 steps
    private long _nextControl;
    private const int StatsEverySteps = 1470;   // ~60 s
    private const int StartedFallbackSteps = 2450;  // ~100 s without "LoadEcsScene to Run": start anyway
    private const int MaxQueuedGameLines = 4096;
    private const long GcBudgetBytes = 8L << 20;

    // Game log lines Kiln's lifecycle watches for (svn 1285601): the world goes live / the game begins its shutdown.
    private const string RunLine = "Switching state from LoadEcsScene to Run";
    private const string ShutdownLine = "Trigger gameflow shutdown";

    public static HostRuntime? Instance { get; private set; }

    private readonly KilnApi* _api;
    private readonly ulong _owner;
    private readonly string _version;
    private readonly string _control;
    private readonly HostConfig _config;
    private readonly ModManager _mods;
    private readonly CrashReports? _crash;
    private readonly KilnBuildInfo _build;
    private readonly HostProfile _profile;
    private readonly string _wine;
    private readonly ConcurrentQueue<(LogLevel, string)> _gameLines = new();
    private readonly ConcurrentQueue<(string Id, string? Filter, TaskCompletionSource<TestReport> Done)> _testRequests = new();
    private readonly List<(LogLevel, string)> _lineBatch = [];
    private readonly List<PosixSignalRegistration> _signals = [];
    private readonly ManualResetEventSlim _stopped = new(false);
    // Client side: mods run on whichever world drives them (the hosted server world, else the client world), one at a
    // time under _drive; _driver is the managed thread running them right now (Game.IsTickThread there).
    private readonly bool _client;
    private readonly object _drive = new();
    private int _driver;
    private volatile bool _hostedStartPending;
    private ulong _clientPostHandle;
    // Client: kiln.ui.tick (the game's UI frame, menus included) loads the mods in the main menu and runs Kiln's
    // service tick there while no world steps (no mod Pre/Post there). Menu steps count from MenuStepBase so they never
    // equal a world's step.
    private const ulong MenuStepBase = 1UL << 40;
    private ulong _uiTickHandle, _menuStep;
    private long _lastMenuServiceTick;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private int _queuedLines;
    private long _droppedLines;
    private ulong _postHandle, _logHandle;
    private bool _modsLoaded, _started, _runSeen, _stopping;
    private ulong _loadStep, _step;
    private string? _stopRequest;
    private long _lastAllocated;
    private int _gc0, _gc1, _gc2;
    private int _hostFaults;
    private TimeSpan _gcPause;
    private DateTime _statsSince = DateTime.UtcNow;
    private ReloadTest? _reloadTest;

    private sealed class ReloadTest
    {
        public required string Id;
        public required string ResultFile;
        public required int Total;
        public int Done, Failures;
        public double TotalMs, MaxMs;
    }

    private HostRuntime(KilnApi* api)
    {
        _api = api;
        _client = KilnAbi.SideName(api) == "client";
        _version = api->Version().ToString();
        _owner = OwnerCreate("host");
        _control = NativeCalls.Path(api, KilnAbi.PathControl);
        var kiln = NativeCalls.Path(api, KilnAbi.PathKiln);
        var logs = NativeCalls.Path(api, KilnAbi.PathLogs);
        // The profile (kiln.profile): which folders load, where configs and per-mod data go. Configs follow the
        // profile's configDir before any mod (or Kiln service) registers one.
        _profile = HostProfile.Read(api);
        var config = _profile.ConfigDirectory ?? NativeCalls.TryPath(api, KilnAbi.PathConfig);
        if (!string.IsNullOrEmpty(config)) KilnPaths.Config = config;
        var state = NativeCalls.TryPath(api, KilnAbi.PathState) ?? Path.Combine(kiln, "state");
        _config = HostConfig.Read(Path.Combine(kiln, "kiln.json"), Log);
        KilnBuildInfo build = default;
        build.Size = (uint)sizeof(KilnBuildInfo);
        if (api->BuildInfo(&build) != KilnAbi.Ok) build = default;
        _build = build;
        _wine = WineVersion();
        var data = _profile.DataDirectory ?? NativeCalls.Path(api, KilnAbi.PathData);
        _mods = new ModManager(this, NativeCalls.Path(api, KilnAbi.PathMods), data, new ModManagerOptions
        {
            Profile = _profile,
            ModsJsonFile = Path.Combine(state, "mods.json"),
            Side = CurrentSide(),
            HostedServerMods = _config.Hosted,
            KilnVersion = _version,
            KilnBuild = KilnBuild.Of(typeof(Kiln.Mod).Assembly),
            HasFeature = HasFeature,
            // The registry's reasons in refusals; a core without kiln.caps keeps the old text.
            Capability = Service(Kiln.Capabilities.ServiceName, Kiln.Capabilities.ServiceVersion) != 0 ? Kiln.Capabilities.Get : null,
            Faults = new FaultPolicy { Limit = _config.FaultLimit, WindowSeconds = _config.FaultWindowSeconds },
            CrashReports = _config.CrashReports
                ? _crash = new CrashReports(Path.Combine(kiln, "crash-reports"), Path.Combine(logs, "kiln.log"), SystemReport, () => _mods!.Describe(), Log)
                : null,
            StateFile = Path.Combine(logs, "mods.txt"),
            TestResultsDirectory = Path.Combine(logs, "tests"),
            TestTimeout = TimeSpan.FromSeconds(_config.TestTimeoutSeconds),
        });
    }

    /// <summary>The side this process runs as, from the core (KilnApi.side; KilnBuildInfo.side on the same core).</summary>
    private ManifestSide CurrentSide() => _client ? ManifestSide.Client : ManifestSide.Server;

    public static int Start(KilnApi* api)
    {
        if (api->AbiVersion != KilnAbi.Version || api->Size < (uint)sizeof(KilnApi))
        {
            EmergencyLog(api, $"Kiln.Host needs ABI {KilnAbi.Version}, core offers {api->AbiVersion}");
            return 2;
        }
        var hostDir = Path.GetDirectoryName(typeof(HostRuntime).Assembly.Location)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name == GameTypes.AssemblyName) return GameTypes.Load();  // this build's Kiln.Game, else kiln/host's
            var candidate = Path.Combine(hostDir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        var host = new HostRuntime(api);
        string? exeSha;
        fixed (byte* p = host._build.ExeSha256) exeSha = Marshal.PtrToStringAnsi((nint)p);
        GameTypes.Configure(KilnAbi.SideName(api), exeSha, host.Log);
        Instance = host;
        Backend.Current = host;
        ModsBackend.Current = host._mods;
        Kiln.Testing.Tests.Starter = host.RequestTests;
        ServiceTick.HostDriven = true;  // Kiln's service tick runs from the host's Pre, not from a mod's
        try
        {
            Kiln.Capabilities.Start();  // the registry's snapshot, and the identity rows that follow their sources
        }
        catch (Exception e)
        {
            host.Log(LogLevel.Warn, $"capabilities: not started: {e.Message}");
        }
        try
        {
            PacingNative.Attach();  // kiln.pacing: which waits this build lets mods pace; hooks go in when a mod asks
        }
        catch (Exception e)
        {
            host.Log(LogLevel.Warn, $"kiln.pacing is not available: {e.Message}");
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => host.Unhandled(e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            host._mods.ReportUnobserved(e.Exception);
            e.SetObserved();
        };
        ulong handle = 0;
        var rc = api->SubscribeTick(host._owner, KilnAbi.TickPre, &Entry.OnPre, null, &handle);
        if (host._client)
        {
            // A client steps no server world unless it hosts: mods also run from the client world (in game), and the
            // hosted server world's start/stop drive the server lifecycle events. Mods stay loaded across sessions.
            ulong ch = 0, wh = 0;
            var crc = api->SubscribeWorldTick(host._owner, KilnAbi.WorldClient, KilnAbi.TickPre, &Entry.OnClientPre, null, &ch);
            var wrc = api->SubscribeWorld(host._owner, (delegate* unmanaged[Cdecl]<void*, void*, void>)(delegate* unmanaged[Cdecl]<KilnWorldEvent*, void*, void>)&Entry.OnWorldEvent, null, &wh);
            host.Log(LogLevel.Info, $"client: mods run from the hosted server world, else the client world (client tick rc {crc}, world events rc {wrc})");
            if (crc == KilnAbi.Ok) rc = KilnAbi.Ok;
            host.SubscribeUiTick();
        }
        if (rc != KilnAbi.Ok)
        {
            host.Log(LogLevel.Warn, $"no server tick on this build (rc {rc}): managed mods stay unloaded");
            return 0;
        }
        // The lifecycle watches the game's log from boot on (world live, shutdown), so the mirror stays subscribed.
        ulong log = 0;
        var lrc = api->SubscribeLog(host._owner, &Entry.OnGameLogLine, null, &log);
        host._logHandle = lrc == KilnAbi.Ok ? log : ulong.MaxValue;  // MaxValue: unavailable
        if (lrc != KilnAbi.Ok) host.Log(LogLevel.Warn, $"game log mirror unavailable (rc {lrc}): ServerStarted fires at the first step");
        host.Log(LogLevel.Info, $"Kiln.Host {typeof(HostRuntime).Assembly.GetName().Version} (mod API {Kiln.Mods.RuntimeApiVersion}" +
                                $"{(KilnBuild.Of(typeof(Kiln.Mod).Assembly) is { } build ? ", build " + build.Version : "")}) on .NET {Environment.Version}" +
                                $"{(host._wine.Length > 0 ? ", Wine " + host._wine : "")}; side {host.CurrentSide().ToString().ToLowerInvariant()}; " +
                                $"{host._profile}; {host._mods.Discover().Count()} mod folder(s), loading at the first server step; " +
                                $"configs in {KilnPaths.Config}; fault policy {host._config.FaultLimit} in {host._config.FaultWindowSeconds:0.#} s");
        return 0;
    }

    public static void EmergencyLog(KilnApi* api, string text)
    {
        NativeCalls.WithUtf8(text, s =>
        {
            api->Log(0, (int)LogLevel.Error, s);
            return 0;
        });
    }

    // ---- IKilnBackend / IHostServices ----
    public string Version => _version;
    public bool IsTickThread => _client ? Volatile.Read(ref _driver) == Environment.CurrentManagedThreadId : _api->OnTickThread() != 0;
    public bool HasFeature(string name) => NativeCalls.WithUtf8(name, s => _api->Feature(s) != 0);

    public void Log(ulong owner, LogLevel level, string text) =>
        NativeCalls.WithUtf8(text, s =>
        {
            _api->Log(owner, (int)level, s);
            return 0;
        });

    public void Log(LogLevel level, string text) => Log(_owner, level, text);
    public nint Service(string name, uint minVersion) => NativeCalls.WithUtf8(name, s => (nint)_api->Service(s, minVersion));
    public ulong OwnerCreate(string id) => NativeCalls.WithUtf8(id, s => _api->OwnerCreate(s));
    public void OwnerRevoke(ulong owner) => _api->OwnerRevoke(owner);

    // ---- game log mirror (core log thread) ----
    public void EnqueueGameLine(int level, string text)
    {
        if (Interlocked.Increment(ref _queuedLines) > MaxQueuedGameLines)
        {
            Interlocked.Decrement(ref _queuedLines);
            Interlocked.Increment(ref _droppedLines);
            return;
        }
        _gameLines.Enqueue(((LogLevel)level, text));
    }

    // ---- tick thread ----
    public void Pre(KilnTickInfo* t)
    {
        if (_client)
        {
            Drive(t, server: true);
            return;
        }
        var tick = HostAccess.Tick(t->Step, t->Clock, t->ClockDelta, t->ClockPeriod, t->WallDeltaMs);
        _step = tick.Step;
        _lineBatch.Clear();
        while (_lineBatch.Count < 1024 && _gameLines.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queuedLines);
            _lineBatch.Add(line);
            WatchLine(line.Item2);
        }
        if (!_modsLoaded)
        {
            _modsLoaded = true;
            _loadStep = tick.Step;
            _lineBatch.Clear();  // boot lines are for the lifecycle only (mods did not exist when they were written)
            LoadMods(tick.Step);
        }
        if (_stopRequest is { } reason && !_stopping) BeginStop(reason, tick.Step);
        if (_stopping) return;
        if (!_started && (_runSeen || _logHandle == ulong.MaxValue || tick.Step - _loadStep >= StartedFallbackSteps))
            ServerStarted(tick.Step, _runSeen ? "game-log" : _logHandle == ulong.MaxValue ? "first-step" : "timeout");
        _mods.ProcessRequests();
        RunServiceTick(in tick);
        while (_testRequests.TryDequeue(out var tr)) StartTestRequest(tr.Id, tr.Filter, tr.Done);
        _mods.Pre(in tick, _lineBatch);
        SyncSubscriptions();
        if (_reloadTest is not null) StepReloadTest();
        if (Environment.TickCount64 >= _nextControl) PollControl();
        if (tick.Step % StatsEverySteps == 0) WriteStats();
        if (_postHandle == 0) PaceGc();
    }

    public void Post(KilnTickInfo* t)
    {
        if (_stopping) return;
        if (_client)
        {
            lock (_drive) RunPost(t);
            return;
        }
        var tick = HostAccess.Tick(t->Step, t->Clock, t->ClockDelta, t->ClockPeriod, t->WallDeltaMs);
        _mods.Post(in tick);
        PaceGc();
    }

    // ---- client side ----

    public void ClientPre(KilnTickInfo* t)
    {
        if (ServerWorldLive()) return;  // hosting: the server world drives the mods
        if (_mods.Hosting)
        {
            // The hosted server world went away without a stop event (no WorldDestroy hook on this build): its
            // server-side mods go now, on the main thread (no ServerStopping: the world is gone).
            lock (_drive)
            {
                var previous = Volatile.Read(ref _driver);
                Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
                try
                {
                    _mods.StopHosting("the server world stopped stepping");
                    Lifecycle.Phase = ServerPhase.Idle;
                    _started = false;
                    Log(LogLevel.Warn, "lifecycle: the hosted server world stopped without a stop event; its server-side mods were unloaded");
                }
                finally
                {
                    Volatile.Write(ref _driver, previous);
                }
            }
        }
        Drive(t, server: false);
    }

    public void ClientPost(KilnTickInfo* t)
    {
        if (ServerWorldLive()) return;
        lock (_drive) RunPost(t);
    }

    /// <summary>Client: a hosted server world runs in this process (KilnApi.session state HOST: without the WorldDestroy
    /// hook a world that stopped stepping 3 s ago counts as gone).</summary>
    private bool ServerWorldLive()
    {
        KilnSessionInfo s = default;
        s.Size = (uint)sizeof(KilnSessionInfo);
        if (_api->Session != null && _api->Session(&s) == KilnAbi.Ok) return s.State == KilnSessionInfo.Host;
        return _api->World(KilnAbi.WorldServer) != 0;
    }

    private void RunPost(KilnTickInfo* t)
    {
        var previous = Volatile.Read(ref _driver);
        Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
        try
        {
            var tick = HostAccess.Tick(t->Step, t->Clock, t->ClockDelta, t->ClockPeriod, t->WallDeltaMs);
            _mods.Post(in tick);
        }
        finally
        {
            Volatile.Write(ref _driver, previous);
        }
    }

    /// <summary>One step of the mods on a client, from the hosted server world or the client world (never both at once).</summary>
    private void Drive(KilnTickInfo* t, bool server)
    {
        lock (_drive)
        {
            Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
            var tick = HostAccess.Tick(t->Step, t->Clock, t->ClockDelta, t->ClockPeriod, t->WallDeltaMs);
            _step = tick.Step;
            _lineBatch.Clear();
            while (_lineBatch.Count < 1024 && _gameLines.TryDequeue(out var line))
            {
                Interlocked.Decrement(ref _queuedLines);
                _lineBatch.Add(line);
            }
            if (!_modsLoaded)
            {
                _modsLoaded = true;
                _loadStep = tick.Step;
                _lineBatch.Clear();
                LoadMods(tick.Step);
                if (server) _hostedStartPending = true;
            }
            if (server && _hostedStartPending)
            {
                _hostedStartPending = false;
                HostedStarted(tick.Step);
            }
            _mods.ProcessRequests();
            RunServiceTick(in tick);
            while (_testRequests.TryDequeue(out var tr)) StartTestRequest(tr.Id, tr.Filter, tr.Done);
            _mods.Pre(in tick, _lineBatch);
            SyncSubscriptions();
            if (_reloadTest is not null) StepReloadTest();
            if (Environment.TickCount64 >= _nextControl) PollControl();
            if (tick.Step % StatsEverySteps == 0) WriteStats();
            if (_postHandle == 0 && _clientPostHandle == 0) PaceGc();
        }
    }

    /// <summary>A hosted session's server world is live (its first step, on its thread): the server-side mods load, as
    /// they would at a dedicated server's first step, then ServerStarting + ServerStarted (no LoadEcsScene wait).</summary>
    private void HostedStarted(ulong step)
    {
        if (_mods.Hosting) _mods.StopHosting("a new hosted world started without a stop event");  // no WorldDestroy hook
        _started = true;
        Lifecycle.Phase = ServerPhase.Booting;
        var hosted = _mods.StartHosting();
        Lifecycle.Phase = ServerPhase.Starting;
        _mods.PostEvent(new ServerStartingEvent(step));
        Lifecycle.Phase = ServerPhase.Running;
        Lifecycle.StartedAtStep = step;
        _mods.PostEvent(new ServerStartedEvent(step, "world-started"));
        Log(LogLevel.Info, $"lifecycle: hosted server world started (step {step}); {hosted.Count} server-side mod(s) loaded for it");
        _mods.QueueAutoTests();
    }

    /// <summary>Client side, any thread: a hosted server world starts (handled at its first Pre) or is being destroyed
    /// (on its own thread, world intact: ServerStopping right here, under the drive lock, then the server-side mods
    /// unload; client and both mods stay loaded).</summary>
    public void WorldEvent(KilnWorldEvent* ev)
    {
        if (!_client || ev->Kind != KilnAbi.WorldServer || !_modsLoaded) return;
        if (ev->Event == KilnWorldEvent.Started)
        {
            _hostedStartPending = true;
            return;
        }
        if (ev->Event != KilnWorldEvent.Stopping) return;
        lock (_drive)
        {
            if (Lifecycle.Phase != ServerPhase.Running && !_mods.Hosting) return;
            var previous = Volatile.Read(ref _driver);
            Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
            try
            {
                Lifecycle.Phase = ServerPhase.Stopping;
                _mods.PostEvent(new ServerStoppingEvent(_step, "world-stopping"));
                var unloaded = _mods.StopHosting("the game is closing it");
                Lifecycle.Phase = ServerPhase.Idle;
                _started = false;
                Log(LogLevel.Info, $"lifecycle: hosted server world {ev->Session} stopping; {unloaded.Count} server-side mod(s) unloaded, " +
                                   $"{_mods.Mods.Count} mod(s) stay loaded");
            }
            finally
            {
                Volatile.Write(ref _driver, previous);
            }
        }
    }

    /// <summary>kiln.ui.tick's table (native/core/abi/services/kiln_ui.h, KilnUiTickApi).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UiTickApi
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<ulong, void*, void>, void*, ulong*, int> Subscribe;
        public delegate* unmanaged[Cdecl]<ulong, int> Unsubscribe;
        public delegate* unmanaged[Cdecl]<byte> InMenu;
    }

    /// <summary>Client: load the mods in the main menu, driven by the game's UI frame (kiln.ui.tick, from kiln-ui.dll).
    /// Without the service (an older kiln-ui, KILN_UI_DISABLE, safe idle) or with KILN_NO_MENU_LOAD=1 the mods load at
    /// the first world step, as before.</summary>
    private void SubscribeUiTick()
    {
        if (Environment.GetEnvironmentVariable("KILN_NO_MENU_LOAD") == "1")
        {
            Log(LogLevel.Info, "client: KILN_NO_MENU_LOAD=1: mods load at the first world step");
            return;
        }
        var t = (UiTickApi*)Service("kiln.ui.tick", 1);
        if (t == null || t->Size < (uint)sizeof(UiTickApi))
        {
            Log(LogLevel.Info, "client: no kiln.ui.tick: mods load at the first world step");
            return;
        }
        ulong h = 0;
        var rc = t->Subscribe(_owner, &Entry.OnUiTick, null, &h);
        if (rc == KilnAbi.Ok) _uiTickHandle = h;
        Log(LogLevel.Info, $"client: the game's UI frame loads the mods in the main menu (kiln.ui.tick rc {rc})");
    }

    /// <summary>The game's UI thread, after each UI frame: while no world steps, load the mods once (first call), then
    /// about 30 times a second run mod requests and Kiln's service tick. A world's step (Drive) takes over as before.</summary>
    public void UiTick(ulong frame)
    {
        if (_stopping || InWorld()) return;
        var now = Environment.TickCount64;
        if (_modsLoaded && now - _lastMenuServiceTick < 33) return;
        lock (_drive)
        {
            if (InWorld()) return;  // a hosted server world may have started on its own thread meanwhile
            Volatile.Write(ref _driver, Environment.CurrentManagedThreadId);
            if (!_modsLoaded)
            {
                _modsLoaded = true;
                _loadStep = 0;
                _lineBatch.Clear();
                Log(LogLevel.Info, $"lifecycle: loading the mods in the game's menu (UI frame {frame})");
                LoadMods(0);
            }
            _lastMenuServiceTick = now;
            _mods.ProcessRequests();
            var tick = HostAccess.Tick(MenuStepBase + ++_menuStep, 0, 0, 0, 0);
            RunServiceTick(in tick);
        }
    }

    private bool InWorld() => _api->World(KilnAbi.WorldServer) != 0 || _api->World(KilnAbi.WorldClient) != 0;

    /// <summary>Kiln.Api's service tick (event pumps, config watchers, queued events) runs here, as Kiln, before the
    /// mods' Pre; without the host it would ride on one mod's tick.</summary>
    private void RunServiceTick(in TickInfo tick)
    {
        try
        {
            ServiceTick.Run(in tick);
        }
        catch (Exception e)
        {
            HostFault("service tick", e);
        }
    }

    private Task<TestReport> RequestTests(string id, string? filter)
    {
        var done = new TaskCompletionSource<TestReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        _testRequests.Enqueue((id, filter, done));
        return done.Task;
    }

    private void StartTestRequest(string id, string? filter, TaskCompletionSource<TestReport> done)
    {
        if (!_mods.StartTests(id, filter, (report, error) =>
            {
                if (report is not null) done.TrySetResult(report);
                else done.TrySetException(new InvalidOperationException(error ?? "the test run did not finish"));
            }, out var startError))
            done.TrySetException(new InvalidOperationException(startError));
    }

    private void WatchLine(string text)
    {
        if (!_runSeen && text.Contains(RunLine, StringComparison.Ordinal))
        {
            _runSeen = true;
            Log(LogLevel.Info, $"lifecycle: the game reports the world running (step {_step})");
        }
        else if (text.Contains(ShutdownLine, StringComparison.Ordinal))
        {
            Log(LogLevel.Info, $"lifecycle: the game began its shutdown ('{text.Trim()}')");
            Interlocked.CompareExchange(ref _stopRequest, "game-log", null);
        }
    }

    private void LoadMods(ulong step)
    {
        _lastAllocated = GC.GetTotalAllocatedBytes();
        (_gc0, _gc1, _gc2, _gcPause) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration());
        Lifecycle.Phase = ServerPhase.Booting;
        _mods.LoadAll();
        Lifecycle.ModsLoaded = true;
        var all = Kiln.Mods.All;
        _mods.PostEvent(new ModsLoadedEvent(all.Where(e => e.IsLoaded).ToArray(), all.Where(e => !e.IsLoaded).ToArray()));
        if (_client)
        {
            Lifecycle.Phase = ServerPhase.Idle;  // ServerStarting/Started come with a hosted session's server world
            Log(LogLevel.Info, $"lifecycle: mods loaded on the client (step {step})");
            return;
        }
        Lifecycle.Phase = ServerPhase.Starting;
        _mods.PostEvent(new ServerStartingEvent(step));
        Log(LogLevel.Info, $"lifecycle: mods loaded, server starting (step {step})");
        RegisterSignals();
    }

    private void ServerStarted(ulong step, string source)
    {
        _started = true;
        Lifecycle.Phase = ServerPhase.Running;
        Lifecycle.StartedAtStep = step;
        if (source == "timeout") Log(LogLevel.Warn, $"lifecycle: '{RunLine}' not seen in the game log; ServerStarted fires anyway");
        _mods.PostEvent(new ServerStartedEvent(step, source));
        Log(LogLevel.Info, $"lifecycle: server started (step {step}, {source})");
        _mods.QueueAutoTests();
    }

    /// <summary>ServerStopping, then every mod unloads (reverse load order); releases a waiting stop signal.</summary>
    private void BeginStop(string reason, ulong step)
    {
        _stopping = true;
        Lifecycle.Phase = ServerPhase.Stopping;
        var sw = Stopwatch.StartNew();
        var count = _mods.Mods.Count;
        _mods.PostEvent(new ServerStoppingEvent(step, reason));
        _mods.UnloadAll("server stopping");
        Log(LogLevel.Info, $"lifecycle: server stopping ({reason}, step {step}): {count} mod(s) notified and unloaded in {sw.Elapsed.TotalMilliseconds:F1} ms");
        _stopped.Set();
    }

    /// <summary>Console stop signals (Ctrl-C = SIGINT under Wine). Registered at the first step, after the game's own
    /// console handler, so Kiln's runs first: it holds the signal until the tick thread has stopped the mods, then
    /// lets it through to the game. Registration order is what makes this work; see docs/kiln/modding/lifecycle.md.</summary>
    private void RegisterSignals()
    {
        foreach (var signal in new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(signal, OnSignal));
            }
            catch (Exception e) when (e is PlatformNotSupportedException or IOException or InvalidOperationException)
            {
                Log(LogLevel.Debug, $"lifecycle: {signal} not available ({e.Message})");
            }
        }
    }

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = false;  // never swallow the stop: the game must still shut down
        var name = context.Signal.ToString();
        if (_stopping || Interlocked.CompareExchange(ref _stopRequest, name, null) is not null) return;
        Log(LogLevel.Info, $"lifecycle: {name} received; stopping mods on the tick thread (up to {_config.StopTimeoutMs} ms) before the game shuts down");
        var sw = Stopwatch.StartNew();
        if (_stopped.Wait(_config.StopTimeoutMs))
            Log(LogLevel.Info, $"lifecycle: mods stopped {sw.Elapsed.TotalMilliseconds:F0} ms after {name}; the game continues its shutdown");
        else
            Log(LogLevel.Warn, $"lifecycle: mods did not stop within {_config.StopTimeoutMs} ms of {name}; the game shuts down anyway");
    }

    private void SyncSubscriptions()
    {
        var wantPost = _mods.AnyPost;
        if (wantPost && _postHandle == 0)
        {
            ulong h = 0;
            if (_api->SubscribeTick(_owner, KilnAbi.TickPost, &Entry.OnPost, null, &h) == KilnAbi.Ok) _postHandle = h;
        }
        else if (!wantPost && _postHandle != 0)
        {
            _api->Unsubscribe(_postHandle);
            _postHandle = 0;
        }
        if (!_client) return;
        if (wantPost && _clientPostHandle == 0)
        {
            ulong h = 0;
            if (_api->SubscribeWorldTick(_owner, KilnAbi.WorldClient, KilnAbi.TickPost, &Entry.OnClientPost, null, &h) == KilnAbi.Ok) _clientPostHandle = h;
        }
        else if (!wantPost && _clientPostHandle != 0)
        {
            _api->Unsubscribe(_clientPostHandle);
            _clientPostHandle = 0;
        }
    }

    /// <summary>Kiln.Host itself threw on the tick thread: logged, and the first few get a crash report.</summary>
    public void HostFault(string where, Exception e)
    {
        Log(LogLevel.Error, $"host {where} failed: {e}");
        if (Interlocked.Increment(ref _hostFaults) <= 3) _crash?.Write("host-fault", $"Kiln.Host threw in {where}", e.ToString());
    }

    private void Unhandled(Exception? e, bool terminating)
    {
        var who = e is null ? null : ModOf(e);
        Log(LogLevel.Error, $"unhandled exception{(who is null ? "" : $" from mod {who}")} on thread '{Thread.CurrentThread.Name}'" +
                            $"{(terminating ? "; the process is going down" : "")}: {e}");
        _crash?.Write("managed-unhandled", $"unhandled {e?.GetType().Name ?? "exception"}{(who is null ? "" : $" thrown by mod {who}")}" +
                                           $"{(terminating ? " (process terminating)" : "")}",
            $"Thread: {Thread.CurrentThread.ManagedThreadId} '{Thread.CurrentThread.Name}'\n{e}");
    }

    private static string? ModOf(Exception e) =>
        new StackTrace(e, false).GetFrames()
            .Select(f => f.GetMethod()?.DeclaringType?.Assembly)
            .Select(a => a is null ? null : AssemblyLoadContext.GetLoadContext(a) as ModLoadContext)
            .FirstOrDefault(c => c is not null)?.ModId;

    private string SystemReport()
    {
        string sha;
        fixed (byte* p = _build.ExeSha256) sha = Marshal.PtrToStringAnsi((nint)p) ?? "";
        var up = DateTime.UtcNow - _startedAt;
        return $"Kiln: {_version} (core), Kiln.Host {typeof(HostRuntime).Assembly.GetName().Version}, mod API {Kiln.Mods.RuntimeApiVersion}\n" +
               $".NET: {RuntimeInformation.FrameworkDescription}; OS: {RuntimeInformation.OSDescription}{(_wine.Length > 0 ? $" (Wine {_wine})" : "")}\n" +
               $"Game: {Path.GetFileName(Environment.ProcessPath)} sha256 {sha} ({(_build.Verified != 0 ? "verified" : "unverified")} build, " +
               $"hooks {(_build.HooksEnabled != 0 ? "on" : "off")}), side {CurrentSide().ToString().ToLowerInvariant()}\n" +
               $"Process: pid {Environment.ProcessId}, Kiln.Host up {(int)up.TotalHours}h{up.Minutes:00}m{up.Seconds:00}s, step {_step}, " +
               $"phase {Lifecycle.Phase}, managed heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB\n";
    }

    private static string WineVersion()
    {
        try
        {
            if (!NativeLibrary.TryLoad("ntdll.dll", out var ntdll) || !NativeLibrary.TryGetExport(ntdll, "wine_get_version", out var f)) return "";
            return Marshal.PtrToStringAnsi(((delegate* unmanaged[Cdecl]<nint>)f)()) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Moves gen0 collections to the end of a step, where the server has ~40 ms of slack.</summary>
    private void PaceGc()
    {
        var allocated = GC.GetTotalAllocatedBytes();
        if (allocated - _lastAllocated < GcBudgetBytes) return;
        GC.Collect(0, GCCollectionMode.Forced, blocking: true, compacting: false);
        _lastAllocated = GC.GetTotalAllocatedBytes();
    }

    private void WriteStats()
    {
        var now = DateTime.UtcNow;
        var seconds = Math.Max(1, (now - _statsSince).TotalSeconds);
        _statsSince = now;
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        var pause = GC.GetTotalPauseDuration();
        Log(LogLevel.Info, $"stats: gc +{g0 - _gc0}/+{g1 - _gc1}/+{g2 - _gc2} pause +{(pause - _gcPause).TotalMilliseconds:F2} ms, " +
                           $"heap {GC.GetTotalMemory(false) / 1048576.0:F1} MB, game lines dropped {Interlocked.Read(ref _droppedLines)}");
        (_gc0, _gc1, _gc2, _gcPause) = (g0, g1, g2, pause);
        foreach (var line in _mods.TakeStats(seconds)) Log(LogLevel.Info, "stats: " + line);
    }

    // ---- control folder: kiln/control/<name>.cmd -> <name>.result.json ----
    private void PollControl()
    {
        _nextControl = Environment.TickCount64 + ControlEveryMs;
        if (!Directory.Exists(_control)) return;
        foreach (var file in Directory.EnumerateFiles(_control, "*.cmd").Order(StringComparer.Ordinal).Take(8))
        {
            string text;
            try
            {
                text = File.ReadAllText(file).Trim();
                File.Delete(file);
            }
            catch (IOException)
            {
                continue;  // still being written
            }
            var result = Path.ChangeExtension(file, ".result.json");
            try
            {
                Execute(text, result);
            }
            catch (Exception e)
            {
                WriteResult(result, text, false, e.Message);
            }
        }
    }

    private static object EntryJson(ModEntry e)
    {
        var s = e.Stats;
        return new
        {
            id = e.Id, name = e.Name, version = e.Version, state = e.State.ToString().ToLowerInvariant(), error = e.Error,
            side = e.Side.ToString().ToLowerInvariant(), loadIndex = e.LoadIndex, folder = Path.GetFileName(e.Directory),
            dependencies = e.Dependencies.Select(d => new { id = d.Id, version = d.VersionRange, optional = d.Optional }),
            exports = e.Info?.Exports ?? [], advanced = e.Info?.Advanced ?? false,
            faults = s.Faults, recentFaults = s.RecentFaults, disabled = e.State == ModState.Disabled,
            steps = s.Steps, msPerStep = Math.Round(s.PreMsPerStep + s.PostMsPerStep, 4), maxMs = Math.Round(s.MaxMs, 3),
            lastFault = s.LastFaultMessage,
        };
    }

    private void Execute(string command, string resultFile)
    {
        var args = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.Length == 0) throw new ArgumentException("empty command");
        Log(LogLevel.Info, $"control: {command}");
        switch (args[0])
        {
            case "mods":
            {
                var all = Kiln.Mods.All;
                WriteResult(resultFile, command, true, $"{all.Count(e => e.IsLoaded)} loaded, {all.Count(e => !e.IsLoaded)} not loaded",
                    all.Select(EntryJson));
                break;
            }
            case "load" when args.Length == 2:
            case "unload" when args.Length == 2:
            case "reload" when args.Length == 2:
            {
                var op = args[0] switch { "load" => ModOperation.Load, "unload" => ModOperation.Unload, _ => ModOperation.Reload };
                var r = _mods.Execute(op, args[1]);
                WriteResult(resultFile, command, r.Ok, r.Message, new { affected = r.Affected });
                break;
            }
            case "reload-test" when args.Length == 3 && int.TryParse(args[2], out var n) && n > 0:
                if (_reloadTest is not null) throw new InvalidOperationException("a reload test is already running");
                if (_mods.Find(args[1]) is null) throw new ArgumentException($"mod {args[1]} is not loaded");
                _reloadTest = new ReloadTest { Id = args[1], Total = n, ResultFile = resultFile };
                _mods.SuppressAutoTests = true;
                break;
            case "test" when args.Length is 2 or 3:
                if (!_mods.StartTests(args[1], args.Length == 3 ? args[2] : null, (report, error) =>
                        WriteResult(resultFile, command, report is { Ok: true }, report?.ToString() ?? error ?? "?",
                            report is null ? null : new
                            {
                                passed = report.Passed, failed = report.Failed, skipped = report.Skipped, file = report.File,
                                results = report.Results.Select(r => new { name = r.Name, outcome = r.Outcome, ms = Math.Round(r.Ms, 3), message = r.Message }),
                            }),
                        out var testError))
                    throw new ArgumentException(testError);
                break;
            case "gc":
            {
                var leaks = _mods.CollectAndCountLeaks(out var leaked);
                WriteResult(resultFile, command, leaks == 0, $"{leaks} unloaded mod context(s) still alive",
                    new { leaked, heapMB = GC.GetTotalMemory(false) / 1048576.0 });
                break;
            }
            default:
                throw new ArgumentException($"unknown command '{command}' (mods | load <folder> | unload <id> | reload <id> | " +
                                            "reload-test <id> <n> | test <id> [filter] | gc)");
        }
    }

    /// <summary>One reload per step, so the test never stalls the server for more than one load (with its cascade).</summary>
    private void StepReloadTest()
    {
        var test = _reloadTest!;
        var start = Stopwatch.GetTimestamp();
        if (!_mods.Reload(test.Id, out var error))
        {
            test.Failures++;
            Log(LogLevel.Error, $"reload-test {test.Id}: {error}");
        }
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        test.TotalMs += ms;
        test.MaxMs = Math.Max(test.MaxMs, ms);
        if (++test.Done < test.Total && test.Failures == 0) return;
        _reloadTest = null;
        _mods.SuppressAutoTests = false;
        var leaks = _mods.CollectAndCountLeaks(out var leaked);
        var ok = test.Failures == 0 && leaks == 0;
        var message = $"{test.Done} reloads, {test.Failures} failures, avg {test.TotalMs / test.Done:F2} ms, " +
                      $"max {test.MaxMs:F2} ms, {leaks} leaked context(s)";
        Log(ok ? LogLevel.Info : LogLevel.Error, $"reload-test {test.Id}: {message}");
        WriteResult(test.ResultFile, $"reload-test {test.Id} {test.Total}", ok, message, new { leaked });
    }

    private static readonly JsonSerializerOptions ResultJson = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static void WriteResult(string file, string command, bool ok, string message, object? data = null)
    {
        var json = JsonSerializer.Serialize(new { command, ok, message, data, time = DateTime.UtcNow }, ResultJson);
        File.WriteAllText(file + ".tmp", json);
        File.Move(file + ".tmp", file, overwrite: true);
    }
}
