// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using Kiln.Testing;

namespace Kiln.Host;

/// <summary>What the mod manager needs from its host (the native core, or a test fake).</summary>
internal interface IHostServices
{
    nint Service(string name, uint minVersion) => 0;
    ulong OwnerCreate(string id);
    void OwnerRevoke(ulong owner);
    void Log(LogLevel level, string text);
}

/// <summary>Disable a mod after <see cref="Limit"/> faults within <see cref="WindowSeconds"/> (0 = ever).
/// kiln.json: <c>"mods": {"fault_limit": 5, "fault_window_s": 60}</c>.</summary>
internal sealed class FaultPolicy
{
    public int Limit { get; init; } = ModManager.MaxFaults;
    public double WindowSeconds { get; init; } = 60;
    public Func<double> Clock { get; init; } = static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

internal sealed class ModManagerOptions
{
    public ManifestSide Side { get; init; } = ManifestSide.Server;

    /// <summary>Client: load <c>side: "server"</c> mods while this client hosts a world (kiln.json
    /// <c>"mods": {"hosted": false}</c> turns it off).</summary>
    public bool HostedServerMods { get; init; } = true;
    public string KilnVersion { get; init; } = "0.0.0";

    /// <summary>This runtime's build (Kiln.Api's stamp): a mod built against a newer one is named before it loads. Null: not known (a
    /// build without git), nothing is compared.</summary>
    public KilnBuild? KilnBuild { get; init; }
    public Func<string, bool> HasFeature { get; init; } = static _ => true;

    /// <summary>The capability registry the resolver checks a manifest's <c>features</c> against (ResolveEnvironment.Capability);
    /// null: <see cref="HasFeature"/> only.</summary>
    public Func<string, Capability>? Capability { get; init; }
    public FaultPolicy Faults { get; init; } = new();
    public CrashReports? CrashReports { get; init; }

    /// <summary><c>kiln/logs/mods.txt</c>: the mod list, rewritten on every change; native crash reports include it.</summary>
    public string? StateFile { get; init; }

    /// <summary><c>kiln/logs/tests</c>: Kiln.Testing results.</summary>
    public string? TestResultsDirectory { get; init; }

    /// <summary>Which folders load (kiln.profile); default: no profile, kiln/mods only.</summary>
    public HostProfile Profile { get; init; } = HostProfile.Legacy;

    /// <summary><c>kiln/state/mods.json</c> for the launcher (schema/state-mods.schema.json), rewritten on every change.</summary>
    public string? ModsJsonFile { get; init; }

    public TimeSpan TestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>One collectible context per loaded mod instance. Assemblies load from bytes, so the files on disk stay
/// free to be replaced while the mod runs. Resolution order: Kiln.Api, Kiln's shared assemblies (kiln/host/Kiln.*),
/// assemblies exported by the mod's dependencies (loaded in their contexts, so types are shared), the mod's folder.</summary>
internal sealed class ModLoadContext(string id, string directory, Func<AssemblyName, Assembly?>? exports = null, Action<Assembly>? loaded = null)
    : AssemblyLoadContext("kiln-mod:" + id, isCollectible: true)
{
    private static readonly Assembly ApiAssembly = typeof(Kiln.Mod).Assembly;
    private static readonly string HostDirectory = System.IO.Path.GetDirectoryName(typeof(ModLoadContext).Assembly.Location) ?? "";
    // No cache of our own assemblies here: while the context unloads the runtime holds it strongly, so a reference
    // from it to its assemblies would keep its LoaderAllocator (and itself) alive forever. Assemblies is the cache.
    private readonly object _lock = new();

    public string ModId => id;

    public Assembly LoadFile(string path)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        lock (_lock)
        {
            if (Find(name) is { } existing) return existing;
            using var image = new MemoryStream(File.ReadAllBytes(path));
            var pdb = System.IO.Path.ChangeExtension(path, ".pdb");
            Assembly assembly;
            if (!File.Exists(pdb)) assembly = LoadFromStream(image);
            else { using var symbols = new MemoryStream(File.ReadAllBytes(pdb)); assembly = LoadFromStream(image, symbols); }
            loaded?.Invoke(assembly);
            return assembly;
        }
    }

    private Assembly? Find(string simpleName)
    {
        foreach (var a in Assemblies)
            if (string.Equals(a.GetName().Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }

    /// <summary>An assembly from the mod's own folder (null when it has none of that name).</summary>
    public Assembly? LoadOwn(string simpleName)
    {
        var candidate = System.IO.Path.Combine(directory, simpleName + ".dll");
        return File.Exists(candidate) ? LoadFile(candidate) : null;
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is not { } n) return null;
        if (n == ApiAssembly.GetName().Name) return ApiAssembly;  // one shared API for every mod
        // Kiln.Game: the build's own kiln/host/game/<side>-<sha12>/Kiln.Game.dll, else kiln/host's (GameTypes).
        if (n == GameTypes.AssemblyName && GameTypes.Load() is { } game) return game;
        // Kiln's service assemblies (kiln/host/Kiln.*.dll) are shared too: one copy in the host's context.
        if (n.StartsWith("Kiln.", StringComparison.Ordinal))
        {
            var shared = System.IO.Path.Combine(HostDirectory, n + ".dll");
            if (File.Exists(shared))
            {
                var host = GetLoadContext(typeof(ModLoadContext).Assembly) ?? Default;
                return host.Assemblies.FirstOrDefault(a => a.GetName().Name == n) ?? host.LoadFromAssemblyPath(shared);
            }
        }
        if (exports?.Invoke(name) is { } exported) return exported;
        return LoadOwn(n);  // null: framework from the default context
    }
}

/// <summary>What a mod without an <c>entryAssembly</c> runs: nothing. It is a mod like any other for the load order, the
/// mod list, dependencies and unload; its folder's data (kiln.content packs, kiln.assets) belongs to it.</summary>
internal sealed class DataOnlyMod : Kiln.Mod;

internal sealed class LoadedMod
{
    public required Manifest Manifest { get; init; }
    public required string Directory { get; init; }
    public required ModLoadContext Context { get; init; }
    public required Kiln.Mod Instance { get; init; }
    public required ModContext ModContext { get; init; }
    public required ulong Owner { get; init; }

    /// <summary>Loaded mods this one resolved against (required and present optional dependencies, transitively):
    /// unloading any of them unloads this one first.</summary>
    public required HashSet<string> ResolvedDependencies { get; init; }

    public int Faults;
    public bool Disabled;
    public bool Loaded;  // OnLoad returned
    public long PreTicks, PostTicks, MaxTicks, Calls;  // since the last stats line
    public long PreMaxTicks, PostMaxTicks;              // since the last stats line: the longest Pre and the longest Post apart
    public long TotalPreTicks, TotalPostTicks, TotalMaxTicks, TotalCalls;  // since load
    public readonly Queue<double> FaultTimes = new();
    public DateTime? LastFault;
    public string? LastFaultMessage;

    /// <summary>Set when a fault showed that this runtime lacks a Kiln member the mod uses (<see cref="NewerRuntime"/>): what to say.</summary>
    public string? NeedsNewerRuntime;
    public string Id => Manifest.Id;
}

/// <summary>Kiln's record of one mod folder: what the manifest says and what happened to it.</summary>
internal sealed class ModRecord
{
    public required string Key { get; set; }  // mod id; "folder:<name>" when the manifest is unreadable
    public required string Directory { get; set; }
    public string Source { get; set; } = "dropin";  // "profile" | "dropin" (state/mods.json)
    public Manifest? Manifest { get; set; }
    public ModState State { get; set; }
    public string? Error { get; set; }
    public LoadedMod? Mod { get; set; }
}

/// <summary>Discovers, resolves, loads, runs, reloads and unloads managed mods. Tick-thread only, except the
/// <see cref="IModsBackend"/> reads and <see cref="Send"/>, which are thread-safe.</summary>
internal sealed partial class ModManager : IModsBackend, IDisposable
{
    public const int MaxFaults = 5;
    private readonly IHostServices _host;
    private readonly string _modsDirectory, _dataDirectory;
    private readonly ModManagerOptions _options;
    private readonly List<LoadedMod> _mods = [];
    private readonly ConcurrentDictionary<string, LoadedMod> _byId = new(StringComparer.Ordinal);
    private readonly List<ModRecord> _records = [];
    private readonly List<(string Id, WeakReference Context)> _unloaded = [];
    private readonly ConcurrentQueue<(ModOperation Op, string Arg, TaskCompletionSource<ModOperationResult>? Done,
        Action<ModOperationResult>? Callback, ModContext? Caller)> _requests = new();
    private readonly ConcurrentQueue<Exception> _unobserved = new();
    private readonly List<KilnEvent> _deferred = [];
    private readonly List<TestRun> _tests = [];
    private readonly Queue<string> _autoTests = new();
    private readonly SemVersion _kilnVersion, _apiVersion;
    private readonly Dictionary<string, string> _sources = new(StringComparer.Ordinal);  // folder -> profile | dropin
    private volatile ModEntry[] _snapshot = [];
    private long _modsJsonGeneration;

    private sealed record TestRun(LoadedMod Mod, Task<TestReport> Task, Action<TestReport?, string?>? Done);

    public ModManager(IHostServices host, string modsDirectory, string dataDirectory, ModManagerOptions? options = null)
    {
        _host = host;
        _modsDirectory = modsDirectory;
        _dataDirectory = dataDirectory;
        _options = options ?? new ModManagerOptions();
        _kilnVersion = SemVersion.TryParse(_options.KilnVersion, out var kv) ? kv : new SemVersion(0, 0, 0);
        _apiVersion = SemVersion.Parse(Kiln.Mods.RuntimeApiVersion);
        Events.HandlerFaulted += OnHandlerFaulted;
    }

    public void Dispose()
    {
        Events.HandlerFaulted -= OnHandlerFaulted;
        StopTeardowns();
    }

    /// <summary>Loaded mods (including disabled ones), in load order.</summary>
    public IReadOnlyList<LoadedMod> Mods => _mods;

    public IReadOnlyList<ModRecord> Records => _records;

    /// <summary>This runtime, as a player's launcher names it: the build (<c>0.2.0-dev+20261106.0f0a3f6</c>), else the version.</summary>
    private string RuntimeName => _options.KilnBuild?.Version ?? _options.KilnVersion;
    public bool AnyPost => _mods.Exists(m => !m.Disabled && m.ModContext.Tick.HasPost);
    public bool AnyGameLog => _mods.Exists(m => !m.Disabled && m.ModContext.GameLog.HasListeners);
    public bool TestsRunning => _tests.Count > 0;

    /// <summary>No test auto-run on (re)load while set (a reload stress test would start a run per reload).</summary>
    public bool SuppressAutoTests { get; set; }

    /// <summary>The drop-in folders (kiln/mods/&lt;folder&gt; with a kiln.mod.json), in name order; none when the
    /// profile turns drop-ins off.</summary>
    public IEnumerable<string> DropinFolders()
    {
        if (!_options.Profile.Dropins || _options.Profile.LoadsNothing || !System.IO.Directory.Exists(_modsDirectory)) yield break;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(_modsDirectory).Order(StringComparer.Ordinal))
            if (File.Exists(System.IO.Path.Combine(dir, Manifest.FileName)))
                yield return dir;
    }

    /// <summary>Every folder the start-up load looks at: the profile's kiln mods, then the drop-ins.</summary>
    public IEnumerable<string> Discover() =>
        _options.Profile.LoadsNothing ? [] : _options.Profile.Folders.Select(f => f.Directory).Concat(DropinFolders());

    private string SourceOf(string directory) => _sources.TryGetValue(directory, out var s) ? s : "dropin";

    /// <summary>Start-up: resolves every mod folder (profile entries, then drop-ins) and loads the accepted ones in
    /// dependency order. Profile entries with enabled false are skipped ("disabled in profile"); an entry whose
    /// manifest id is not the profile's id fails; a drop-in whose id the profile lists is skipped (the profile wins).</summary>
    public void LoadAll()
    {
        var candidates = new List<ModCandidate>();
        var profile = _options.Profile;
        if (profile.LoadsNothing)
            _host.Log(profile.Mode == ProfileMode.Invalid ? LogLevel.Error : LogLevel.Info, $"mods: none load this start ({profile})");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var clashes = new List<ModCandidate>();
        foreach (var f in profile.LoadsNothing ? [] : profile.Folders)
        {
            _sources[f.Directory] = "profile";
            listed.Add(f.Id);
            var c = ModCandidate.Read(f.Directory);
            if (!f.Enabled)
            {
                disabled.Add(f.Id);
                SetRecord(c, ModState.Skipped, "disabled in profile");  // Kiln.Network's NetAdvertise.DisabledInProfile: keep in step
                _host.Log(LogLevel.Info, $"mod {f.Id}{(f.Version is null ? "" : " " + f.Version)} skipped: disabled in profile");
                continue;
            }
            if (c.Manifest is { } m && m.Id != f.Id)
            {
                var reason = $"the profile lists it as '{f.Id}' but its {Manifest.FileName} says id '{m.Id}'";
                SetRecord(c, ModState.Failed, reason);
                _host.Log(LogLevel.Error, $"mod {c} not loaded: {reason} ({f.Directory})");
                continue;
            }
            candidates.Add(c);
        }
        foreach (var dir in DropinFolders())
        {
            _sources[dir] = "dropin";
            if (File.Exists(System.IO.Path.Combine(dir, ".disabled")))
            {
                var c = ModCandidate.Read(dir);
                SetRecord(c, ModState.Skipped, "disabled by a .disabled file");
                _host.Log(LogLevel.Info, $"mod folder {System.IO.Path.GetFileName(dir)} is disabled (.disabled file)");
                continue;
            }
            var d = ModCandidate.Read(dir);
            if (d.Manifest is { } dm && listed.Contains(dm.Id))
            {
                clashes.Add(d);  // recorded after the load, so the profile's mod keeps the id's record
                _host.Log(LogLevel.Info, $"mod folder {d.Folder} skipped: the profile lists {dm.Id} too");
                continue;
            }
            candidates.Add(d);
        }
        _reportStart = true;
        List<string> loaded;
        try { loaded = LoadSet(candidates); }
        finally { _reportStart = false; }
        foreach (var d in clashes) SetRecord(d, ModState.Skipped, $"the profile lists {d.Manifest!.Id} too (the profile wins over kiln/mods)");
        foreach (var r in _records)  // say why a dependency is "missing" when the profile switched it off
            if (r.State == ModState.Failed && r.Error is { } err)
                foreach (var id in disabled)
                    if (err.StartsWith($"missing dependency {id} ", StringComparison.Ordinal) || err == $"missing dependency {id}")
                        r.Error = err + " (it is disabled in profile)";
        if (clashes.Count > 0 || disabled.Count > 0) PublishSnapshot();
        var failed = _records.Count(r => r.State == ModState.Failed);
        var skipped = _records.Count(r => r.State == ModState.Skipped);
        _host.Log(failed > 0 ? LogLevel.Warn : LogLevel.Info,
            $"mods: {loaded.Count} loaded, {failed} failed, {skipped} skipped; load order: {(loaded.Count > 0 ? string.Join(", ", loaded) : "-")}");
        ReportContent(loaded);
    }

    // progress.json while LoadAll runs (StartProgress: kiln.progress v2): step "mods" counts the mods up to its total.
    private bool _reportStart;

    /// <summary>After the first load: step "content" while the mods' data files (blocks/) are compiled. Kiln.Blocks counts
    /// them and ends the step; with no loaded mod that has a blocks/ folder there is nothing to apply, and step "game"
    /// follows at once.</summary>
    private void ReportContent(List<string> loaded)
    {
        var withBlocks = loaded.Select(Find).Where(m => m is not null && Directory.Exists(System.IO.Path.Combine(m.Directory, "blocks")))
            .Select(m => m!.Id).ToList();
        StartProgress.Step("content", "", withBlocks.Count > 0 ? $"the blocks of {string.Join(", ", withBlocks)}" : "");
        if (withBlocks.Count == 0) StartProgress.Step("game", StartProgress.GameLabel(_options.Side == ManifestSide.Client));
    }

    public LoadedMod? Find(string id) => _byId.GetValueOrDefault(id);

    private ModRecord? RecordById(string id) => _records.Find(r => r.Key == id);

    /// <summary>Runtime load of one folder (control <c>load</c>), resolved against the running mods. Mods that
    /// failed earlier because this one was missing are retried.</summary>
    public bool TryLoad(string directory, out string error) => TryLoad(directory, out error, out _);

    public bool TryLoad(string directory, out string error, out List<string> affected)
    {
        error = "";
        affected = [];
        var c = ModCandidate.Read(directory);
        if (c.Manifest is { } m && Find(m.Id) is not null)
        {
            error = $"mod {m.Id} is already loaded";
            _host.Log(LogLevel.Warn, error);
            return false;
        }
        var candidates = new List<ModCandidate> { c };
        foreach (var r in FailedDependents([c.Id]))
            if (candidates.TrueForAll(x => x.Directory != r.Directory))
                candidates.Add(ModCandidate.Read(r.Directory));
        affected = LoadSet(candidates);
        if (affected.Contains(c.Id)) return true;
        error = _records.Find(r => r.Directory == directory)?.Error ?? $"mod folder {c.Folder} did not load";
        return false;
    }

    /// <summary>Resolves <paramref name="candidates"/> against the running mods and loads them in order.</summary>
    private List<string> LoadSet(IReadOnlyList<ModCandidate> candidates)
    {
        var env = new ResolveEnvironment(_kilnVersion, _apiVersion, _options.Side, _options.HasFeature,
            _mods.ToDictionary(m => m.Id, m => m.Manifest, StringComparer.Ordinal))
        {
            Hosting = Hosting && _options.HostedServerMods,
            Capability = _options.Capability,
        };
        var result = Resolver.Resolve(candidates, env);
        foreach (var c in candidates)
            if (c.Manifest is { } m)
                foreach (var w in m.Warnings)
                    _host.Log(LogLevel.Warn, $"mod {m.Id}: {w}");
        foreach (var r in result.Rejected)
        {
            var reason = r.Reason;
            // A client says when a server mod would load (the resolver's text is the menu's; the launcher words it the
            // same way: kiln_manage::profile::resolve_preview_for, ResolvePreview.with_world).
            if (_options.Side == ManifestSide.Client && r.State == ModState.Skipped && r.Candidate.Manifest?.Side == ManifestSide.Server)
                reason = _options.HostedServerMods ? HostedReason : HostedOffReason;
            SetRecord(r.Candidate, r.State, reason);
            PublishSnapshot(writeFile: false);
            if (r.State == ModState.Skipped) _host.Log(LogLevel.Info, $"mod {r.Candidate} skipped: {reason}");
            else _host.Log(LogLevel.Error, $"mod {r.Candidate} not loaded: {reason}");
        }
        var loaded = new List<string>();
        var position = 0UL;
        var count = (ulong)result.Order.Count;
        if (_reportStart) StartProgress.Step("mods", "Loading the mods", "", 0, count);
        foreach (var c in result.Order)
        {
            var m = c.Manifest!;
            if (_reportStart) StartProgress.Step("mods", "", m.Id, position, count);
            position++;
            var failedDep = m.Dependencies.FirstOrDefault(d => !d.Optional && d.Id != Manifest.KilnId && !_byId.ContainsKey(d.Id));
            if (failedDep is not null)
            {
                var reason = $"dependency {failedDep.Id} failed to load";
                SetRecord(c, ModState.Failed, reason);
                _host.Log(LogLevel.Error, $"mod {c} not loaded: {reason}");
                continue;
            }
            if (m.BuiltWith is { } built && _options.KilnBuild is { } runtime && built.IsNewerThan(runtime))
                _host.Log(LogLevel.Warn, $"mod {m.Id} {m.Version} was built against Kiln {built.Version}, newer than this runtime ({runtime.Version}): " +
                                         "if it fails, update Kiln on the launcher's Runtime page");
            SetRecord(c, ModState.Pending, null);
            PublishSnapshot(writeFile: false);  // a mod's OnLoad sees the mods loaded before it in Mods.All
            var sw = Stopwatch.StartNew();
            // A retained dependency is alive for its old callers, not a provider for a new load. Otherwise that new
            // dependent would escape the cascade already waiting and keep using the dependency after disposal.
            var retiringDependency = DependencyClosure(m).FirstOrDefault(UnloadWaiting);
            if (retiringDependency is not null)
            {
                var why = $"dependency {retiringDependency} is unloading; wait for its retirement before loading {m.Id}";
                SetRecord(c, ModState.Failed, why);
                _host.Log(LogLevel.Warn, $"mod {m.Id} not loaded: {why}");
                continue;
            }
            if (!LoadCore(c.Directory, m, out var error))
            {
                SetRecord(c, ModState.Failed, error);
                continue;
            }
            loaded.Add(m.Id);
            var shared = Find(m.Id)!.ResolvedDependencies;
            _host.Log(LogLevel.Info, $"mod {m.Id} {m.Version} loaded in {sw.Elapsed.TotalMilliseconds:F1} ms" +
                                     (m.HasCode ? "" : " (data only: no entryAssembly)") +
                                     (shared.Count > 0 ? $" (after {string.Join(", ", shared.Order(StringComparer.Ordinal))})" : ""));
        }
        if (_reportStart) StartProgress.Step("mods", "", "", count, count);
        PublishSnapshot();
        return loaded;
    }

    private void SetRecord(ModCandidate c, ModState state, string? error, LoadedMod? mod = null)
    {
        var key = c.Manifest?.Id ?? "folder:" + c.Folder;
        var existing = _records.Find(r => r.Key == key);
        if (existing is not null && existing.Directory != c.Directory && existing.State is ModState.Loaded or ModState.Disabled or ModState.Pending)
        {
            key = $"{key}@{c.Folder}";  // a duplicate id: keep the running one under its id
            existing = _records.Find(r => r.Key == key);
        }
        _records.RemoveAll(r => r.Directory == c.Directory && r.Key != key);
        if (existing is null)
        {
            existing = new ModRecord { Key = key, Directory = c.Directory };
            _records.Add(existing);
        }
        existing.Directory = c.Directory;
        existing.Source = SourceOf(c.Directory);
        existing.Manifest = c.Manifest;
        existing.State = state;
        existing.Error = error;
        existing.Mod = mod;
    }

    private HashSet<string> DependencyClosure(Manifest manifest)
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<Manifest>([manifest]);
        while (queue.Count > 0)
            foreach (var d in queue.Dequeue().Dependencies)
                if (_byId.TryGetValue(d.Id, out var dep) && closure.Add(d.Id))
                    queue.Enqueue(dep.Manifest);
        return closure;
    }

    private Assembly? ResolveExport(HashSet<string> dependencies, AssemblyName name)
    {
        var file = name.Name + ".dll";
        foreach (var id in dependencies)
            if (_byId.TryGetValue(id, out var dep) && dep.Manifest.Exports.Any(x => x.Equals(file, StringComparison.OrdinalIgnoreCase)))
                return dep.Context.LoadOwn(name.Name!);
        return null;
    }

    private static ModInfo Info(Manifest m, string directory) => new(m.Id, m.Name, m.Version, directory)
    {
        Side = (ModSide)(int)m.Side,
        Description = m.Description,
        Authors = m.Authors,
        License = m.License,
        Url = m.Url,
        ApiVersion = m.ApiVersion,
        Dependencies = m.Dependencies.Select(d => new ModDependency(d.Id, d.RangeText, d.Optional)).ToArray(),
        Exports = m.Exports,
        Features = m.Features,
        Advanced = m.Advanced,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool LoadCore(string directory, Manifest manifest, out string error)
    {
        error = "";
        // a boot mod loads only with its boot content in this process (chapter 08 §4.4): RestartRequiredBoot otherwise
        if (BootLoadRefusal(manifest, directory) is { } boot)
        {
            error = boot;
            _host.Log(LogLevel.Warn, $"mod {manifest.Id} not loaded: {boot}");
            return false;
        }
        // its layout stamp against this game build's Kiln.Game, before any of its code loads
        if (manifest.HasCode && !GameTypes.CheckMod(manifest.Id, directory, manifest.EntryAssembly, _host.Log, out var refusal))
        {
            error = $"mod {manifest.Id}: {refusal}";
            _host.Log(LogLevel.Error, error);
            return false;
        }
        var dependencies = DependencyClosure(manifest);
        ulong owner = 0;
        uint gameplayRequested = 0;
        var alc = new ModLoadContext(manifest.Id, directory, dependencies.Count > 0 ? name => ResolveExport(dependencies, name) : null,
            loaded: assembly => {
                var demand=GameplayBuildDemand.GroupsFor(assembly) & ~gameplayRequested;
                if (demand!=0) {
                    gameplayRequested |= demand; GameplayBuildDemand.Request(_host, owner,demand);
                }
            });
        try
        {
            owner = _host.OwnerCreate("mod:" + manifest.Id);
            Kiln.Mod instance;
            if (!manifest.HasCode)
                instance = new DataOnlyMod();  // no entryAssembly: a mod of data (content packs, assets), with nothing to run
            else
            {
                var assembly = alc.LoadFile(System.IO.Path.Combine(directory, manifest.EntryAssembly));
                var type = manifest.EntryType is { } name
                    ? assembly.GetType(name, throwOnError: true)!
                    : assembly.GetExportedTypes().SingleOrDefault(t => t.IsSubclassOf(typeof(Kiln.Mod)) && !t.IsAbstract)
                      ?? throw new InvalidDataException($"{manifest.EntryAssembly} has no public Kiln.Mod subclass (or set entryType)");
                if (!type.IsSubclassOf(typeof(Kiln.Mod))) throw new InvalidDataException($"{type} does not derive from Kiln.Mod");
                instance = (Kiln.Mod)Activator.CreateInstance(type)!;
            }
            var context = HostAccess.CreateContext(Info(manifest, directory), owner, System.IO.Path.Combine(_dataDirectory, manifest.Id));
            HostAccess.Bind(instance, context);
            var mod = new LoadedMod
            {
                Manifest = manifest, Directory = directory, Context = alc, Instance = instance, ModContext = context,
                Owner = owner, ResolvedDependencies = dependencies,
            };
            _mods.Add(mod);
            _byId[mod.Id] = mod;
            var record = RecordById(manifest.Id);
            if (record is not null)
            {
                record.Mod = mod;
                record.State = ModState.Loaded;
                record.Error = null;
            }
            if (Invoke(mod, static m => { Kiln.Events.Register(m.Instance); m.Instance.OnLoad(); }, "OnLoad"))
            {
                mod.Loaded = true;
                // a committed boot mod's runtime activation: its definitions' runtime parts and BootDefinitionsCommittedEvent (Kiln.Blocks)
                if (manifest.Boot != ManifestBoot.None && BootGeneration.Report(manifest.Id) is { State: BootModState.Committed })
                    ActivateBoot(manifest.Id);
            }
            else
            {
                var why = mod.LastFaultMessage;
                var newer = mod.NeedsNewerRuntime;
                Unload(mod.Id, out _);
                error = newer ?? $"mod {manifest.Id}: OnLoad failed ({why})";
                return false;
            }
            if (manifest.Tests is { AutoRun: true } && Lifecycle.IsServerRunning && !SuppressAutoTests) _autoTests.Enqueue(manifest.Id);
            return true;
        }
        catch (Exception e)
        {
            // its types reach for a Kiln member, type or assembly this runtime lacks: built against a newer Kiln
            if (NewerRuntime.MissingKilnMember(e) is { } member)
            {
                error = NewerRuntime.Explain(manifest.Id, member, RuntimeName, manifest.BuiltWith);
                _host.Log(LogLevel.Error, $"{error} ({e.GetType().Name}: {e.Message})");
            }
            else
            {
                error = $"mod {manifest.Id}: {e.GetType().Name}: {e.Message}";
                _host.Log(LogLevel.Error, error);
            }
            if (owner != 0) _host.OwnerRevoke(owner);
            alc.Unload();
            return false;
        }
    }

    /// <summary>Unloads a mod and, first, every loaded mod that depends on it; <paramref name="context"/> tracks the
    /// mod's load context until the GC frees it.</summary>
    public bool Unload(string id, out WeakReference? context) => Unload(id, ModState.Unloaded, null, out context, out _);

    public bool Unload(string id, ModState state, string? reason, out WeakReference? context, out List<string> affected)
    {
        context = null;
        affected = [];
        var mod = Find(id);
        if (mod is null) return false;
        var dependents = Dependents(id);
        for (var i = dependents.Count - 1; i >= 0; i--)
        {
            UnloadOne(dependents[i], state, $"its dependency {id} unloaded");
            affected.Add(dependents[i].Id);
        }
        context = UnloadOne(mod, state, reason);
        affected.Add(id);
        UnloadCaches.Clear(_host.Log);
        PublishSnapshot();
        return true;
    }

    /// <summary>Loaded mods that resolved against <paramref name="id"/>, directly or through another, in load order.</summary>
    public List<LoadedMod> Dependents(string id)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { id };
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var m in _mods)
                if (!set.Contains(m.Id) && m.ResolvedDependencies.Overlaps(set))
                    changed |= set.Add(m.Id);
        }
        return _mods.Where(m => m.Id != id && set.Contains(m.Id)).ToList();
    }

    /// <summary>Failed mods whose manifest requires one of <paramref name="ids"/> (directly or transitively).</summary>
    private List<ModRecord> FailedDependents(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids, StringComparer.Ordinal);
        var found = new List<ModRecord>();
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var r in _records)
                if (r.State == ModState.Failed && r.Manifest is { } m && !found.Contains(r) && m.Dependencies.Any(d => set.Contains(d.Id)))
                {
                    found.Add(r);
                    set.Add(m.Id);
                    changed = true;
                }
        }
        return found;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference UnloadOne(LoadedMod mod, ModState state, string? reason)
    {
        // services close what calls into the mod from other threads first (its natives), so OnUnload runs alone; while
        // a call they let in is still inside, OnUnload and the disposal wait for it (a later Pre)
        if (WaitsForQuiet(mod, () => FinishUnload(mod, state, reason), "unload")) return new WeakReference(mod.Context);
        return FinishUnload(mod, state, reason);
    }

    // ---- unloads that wait for a call into the mod's code from another thread ----
    // ModContext.UnloadStarting: a service whose calls into the mod run on the game's threads (kiln.abilities' natives)
    // closes them at the start of the unload and says when one is still inside after its bounded wait. OnUnload and the
    // disposal then run in a later Pre, once nothing is inside: never forced (kiln.abilities' watchdog names a call
    // inside for over a second). The mod keeps its place meanwhile (the same id cannot load twice); a reload's load waits
    // with it. A mod whose dependent still waits waits too (the call inside the dependent may use the dependency's code
    // or state), so the deferred unloads keep the order of ordinary ones: dependents first. At shutdown what still waits
    // after ShutdownWait is skipped (its OnUnload and disposal), in that order, never forced.

    private sealed record WaitingUnload(LoadedMod Mod, Action Finish, long Since, string What);

    private readonly List<WaitingUnload> _waitingUnloads = [];
    private readonly List<(HashSet<string> Ids, Action Then)> _afterUnloads = [];

    /// <summary>How long the shutdown waits for a call still inside a mod's code before it skips that mod's OnUnload.</summary>
    internal static TimeSpan ShutdownWait = TimeSpan.FromSeconds(5);

    /// <summary>Whether mod <paramref name="id"/>'s unload waits for a call inside its code.</summary>
    internal bool UnloadWaiting(string id) => _waitingUnloads.Exists(w => w.Mod.Id == id);

    private bool WaitsForQuiet(LoadedMod mod, Action finish, string what)
    {
        var waiting = _waitingUnloads.FindIndex(w => ReferenceEquals(w.Mod, mod));
        if (waiting >= 0)
        {
            // An unload/reload requested during a deferred disable must still remove the old context.
            if (what == "unload" && _waitingUnloads[waiting].What == "disable")
                _waitingUnloads[waiting] = _waitingUnloads[waiting] with { Finish = finish, What = what };
            return true;
        }
        var inside = mod.ModContext.BeginUnload();  // its own admission closes now in any case
        var waits = WaitsFor(mod, inside);
        if (waits is null) return false;
        _waitingUnloads.Add(new WaitingUnload(mod, finish, Stopwatch.GetTimestamp(), what));
        _host.Log(LogLevel.Warn, $"mod {mod.Id}: its {what} waits: {waits}; OnUnload and the disposal run when that is over");
        return true;
    }

    // What mod's unload still waits for, or null: a call inside its own code (`inside`, the unload steps' answer), and
    // the waiting unloads of mods that depend on it, directly or through another.
    private string? WaitsFor(LoadedMod mod, string? inside)
    {
        var dependents = Dependents(mod.Id).Where(d => _waitingUnloads.Exists(w => ReferenceEquals(w.Mod, d))).Select(d => d.Id).ToList();
        var deps = dependents.Count switch
        {
            0 => null,
            1 => $"its dependent {dependents[0]} still waits",
            _ => $"its dependents {string.Join(", ", dependents)} still wait",
        };
        return inside is null ? deps : deps is null ? inside : $"{inside}; {deps}";
    }

    // Pre: the waiting unloads whose calls left go on, then what waited for them (a reload's load).
    private void FinishWaitingUnloads()
    {
        if (_waitingUnloads.Count == 0) return;
        var done = false;
        // dependents were added first: one pass in order finishes a dependent before its dependency; passes repeat
        // while one goes on
        for (var progress = true; progress;)
        {
            progress = false;
            for (var i = 0; i < _waitingUnloads.Count; i++)
            {
                var w = _waitingUnloads[i];
                if (WaitsFor(w.Mod, w.Mod.ModContext.UnloadWaitsFor()) is not null) continue;
                _waitingUnloads.RemoveAt(i--);
                _host.Log(LogLevel.Info, $"mod {w.Mod.Id}: what its {w.What} waited for is over after {Stopwatch.GetElapsedTime(w.Since).TotalSeconds:0.0} s: it goes on");
                w.Finish();
                done = progress = true;
            }
        }
        if (!done) return;
        UnloadCaches.Clear(_host.Log);
        PublishSnapshot();
        for (var i = 0; i < _afterUnloads.Count; i++)
        {
            var (ids, then) = _afterUnloads[i];
            if (ids.Any(UnloadWaiting)) continue;
            _afterUnloads.RemoveAt(i--);
            then();
        }
    }

    // ---- load contexts whose teardown joins the mod's tasks off the tick ----
    // A mod's tasks were sealed and signalled when its unload began (ModContext.BeginUnload), and OnUnload ran on
    // the tick without waiting for any of them. What is left is the load context: it is only torn down once no task
    // of the mod (or of a dependent that unloaded with it) can still run. That join happens here, on a maintenance
    // thread, with a bounded budget; a task that will not stop leaves its context pinned (held by its entry below,
    // logged once) until it finishes, and other mods keep ticking meanwhile. Process exit joins nothing: a pinned
    // context simply dies with the process, and UnloadAll never waits for this thread.

    /// <summary>How long the off-tick task join waits before an unstopped task's context is left pinned.</summary>
    internal static TimeSpan TaskJoinBudget = TimeSpan.FromSeconds(2);

    /// <summary>One drain call of the join: short, so the join visits every pending context in turn and sees a stop.</summary>
    private static readonly TimeSpan TaskJoinSlice = TimeSpan.FromMilliseconds(50);

    private sealed class PendingTeardown(LoadedMod mod, HostTasks? pool)
    {
        public readonly string Id = mod.Id;
        public readonly ModLoadContext Context = mod.Context;
        public readonly ModContext ModContext = mod.ModContext;
        public readonly ulong Owner = mod.Owner;
        public readonly HashSet<string> Dependencies = mod.ResolvedDependencies;
        public readonly HostTasks? Pool = pool;  // the pool the mod's tasks ran on (the one bound when it unloaded)
        public readonly long Since = Stopwatch.GetTimestamp();
        public bool PinLogged;
    }

    private readonly List<PendingTeardown> _pendingTeardowns = [];
    private readonly object _teardownGate = new();
    private Thread? _teardownThread;
    private bool _teardownStopped;  // Dispose: the join ends and takes no new context

    /// <summary>FinishUnload's last step, on the tick: tears the context down at once when nothing of the mod can
    /// still run (the rule for mods without tasks, unchanged), else hands it to the maintenance thread. The check
    /// only looks; the tick never waits here.</summary>
    private void UnloadContext(LoadedMod mod)
    {
        var entry = new PendingTeardown(mod, HostTasks.Bound);
        lock (_teardownGate)
        {
            if (_teardownStopped || TeardownBlocker(entry) is null)
            {
                mod.Context.Unload();
                return;
            }
            _pendingTeardowns.Add(entry);
            if (_teardownThread is null)
            {
                _teardownThread = new Thread(TeardownLoop) { IsBackground = true, Name = "kiln-task-join" };
                _teardownThread.Start();
            }
        }
    }

    /// <summary>What still keeps <paramref name="entry"/>'s context from being torn down, or null: its own live
    /// tasks (or their leases still ending at the pool), and the pending teardowns of dependents of it, whose tasks
    /// may still run its code. Polled by the maintenance thread and, once, on the tick in <see cref="UnloadContext"/>
    /// (which never waits on the answer).</summary>
    private string? TeardownBlocker(PendingTeardown entry)
    {
        var parts = new List<string>();
        if (entry.ModContext.TaskBlocker() is { } tasks)
            parts.Add($"task {tasks}");
        else if (entry.Pool is { } pool && pool.OwnerPins(entry.Owner) > 0)
            parts.Add("its task leases are ending");
        lock (_teardownGate)
            foreach (var other in _pendingTeardowns)
                if (!ReferenceEquals(other, entry) && other.Dependencies.Contains(entry.Id))
                    parts.Add($"its dependent {other.Id} still has tasks" +
                              (other.ModContext.TaskBlocker() is { } dependentTasks ? $": task {dependentTasks}" : ""));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>The maintenance thread: joins each pending context's tasks with the pool's own
    /// <c>drain_owner</c>, off every tick (a timed drain is refused on the tick, a world thread and pool workers,
    /// never here), in short slices. Entries are visited in unload order, so a dependent's join comes before its
    /// dependency's, and a dependency is not joined while a dependent's entry still waits. A context whose tasks
    /// stop unloads as it always did, just from here; one still blocked after <see cref="TaskJoinBudget"/> is
    /// logged once (also a dependency held by its dependent) and stays pinned (its entry holds the context) until
    /// its tasks finish, however long that takes. The thread ends when the queue drains or the manager is
    /// disposed.</summary>
    private void TeardownLoop()
    {
        while (true)
        {
            List<PendingTeardown> round;
            lock (_teardownGate)
            {
                if (_teardownStopped || _pendingTeardowns.Count == 0)
                {
                    _teardownThread = null;
                    return;
                }
                round = [.. _pendingTeardowns];
            }
            var progressed = false;
            foreach (var entry in round)
            {
                lock (_teardownGate)
                    if (_teardownStopped || !_pendingTeardowns.Contains(entry)) continue;
                // a dependent's tasks may still run this mod's code: its own join waits until theirs is over
                var running = DependentPending(entry) ? 1 : Drain(entry);
                if (running == 0)
                {
                    lock (_teardownGate)
                    {
                        if (_teardownStopped) continue;
                        _pendingTeardowns.Remove(entry);
                    }
                    entry.Context.Unload();
                    _host.Log(LogLevel.Info, $"tasks: mod {entry.Id}'s load context unloaded after its tasks stopped");
                    progressed = true;
                }
                else if (!entry.PinLogged && Stopwatch.GetElapsedTime(entry.Since) >= TaskJoinBudget)
                {
                    entry.PinLogged = true;
                    progressed = true;
                    _host.Log(LogLevel.Warn, $"tasks: mod {entry.Id}: {TeardownBlocker(entry) ?? $"{running} task(s)"} did not stop within " +
                                             $"{TaskJoinBudget.TotalSeconds:0.#} s; its load context stays pinned until its tasks finish");
                }
            }
            if (!progressed) Thread.Sleep(5);
        }
    }

    /// <summary>One slice of <paramref name="entry"/>'s join: how many of its leases are not quiescent yet.</summary>
    private static int Drain(PendingTeardown entry)
    {
        var running = entry.Pool?.DrainOwner(entry.Owner, TaskJoinSlice) ?? -1;
        if (running >= 0) return running;
        // No pool to drain against (tasks off, or the core refused): look at the leases directly.
        if (entry.ModContext.TaskBlocker() is null && (entry.Pool?.OwnerPins(entry.Owner) ?? 0) == 0) return 0;
        Thread.Sleep(10);
        return 1;
    }

    /// <summary>A dependent of <paramref name="entry"/>'s mod still has a pending teardown: its tasks may still
    /// run the dependency's code, so the dependency's context is joined only after theirs.</summary>
    private bool DependentPending(PendingTeardown entry)
    {
        lock (_teardownGate)
            return _pendingTeardowns.Exists(other => !ReferenceEquals(other, entry) && other.Dependencies.Contains(entry.Id));
    }

    /// <summary>Stops the task join (tests: one manager per test, and the core under it goes with the test). What
    /// it still held is dropped, not torn down; it waits for the join's current slice.</summary>
    private void StopTeardowns()
    {
        Thread? join;
        lock (_teardownGate)
        {
            _teardownStopped = true;
            _pendingTeardowns.Clear();
            join = _teardownThread;
        }
        if (join is not null && join != Thread.CurrentThread) join.Join(TimeSpan.FromSeconds(5));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference FinishUnload(LoadedMod mod, ModState state, string? reason)
    {
        if (mod.Loaded && !mod.Disabled) Invoke(mod, static m => m.Instance.OnUnload(), "OnUnload");
        mod.ModContext.Retire();
        foreach (var other in _mods)
            if (!ReferenceEquals(other, mod))
                other.ModContext.Messages.DropFrom(mod.ModContext);
        _host.OwnerRevoke(mod.Owner);
        _mods.Remove(mod);
        _byId.TryRemove(mod.Id, out _);
        var weak = new WeakReference(mod.Context);
        _unloaded.Add((mod.Id, weak));
        UnloadContext(mod);
        if (RecordById(mod.Id) is { } record && ReferenceEquals(record.Mod, mod))
        {
            record.Mod = null;
            record.State = state;
            record.Error = reason;
        }
        _host.Log(LogLevel.Info, $"mod {mod.Id} unloaded" + (reason is null ? "" : $" ({reason})"));
        return weak;
    }

    /// <summary>Unloads everything, dependents first (server shutdown). The change events that wait for the next step go
    /// out first: that step never comes for these mods (docs/kiln/modding/contract.md, "Change events").</summary>
    public void UnloadAll(string reason)
    {
        ChangeNoticeQueue.FlushFinal(reason);
        _afterUnloads.Clear();  // shutdown must never resume a previously queued reload
        for (var i = _mods.Count - 1; i >= 0; i--)
            UnloadOne(_mods[i], ModState.Unloaded, reason);  // also upgrade a waiting disable to a full unload
        // no Pre follows: wait here for the calls still inside, then skip what still waits (never forced)
        var sw = Stopwatch.StartNew();
        while (_waitingUnloads.Count > 0 && sw.Elapsed < ShutdownWait)
        {
            FinishWaitingUnloads();
            if (_waitingUnloads.Count > 0) Thread.Sleep(1);
        }
        // dependents first (the order they began to wait in), each with what it still waits for
        foreach (var w in _waitingUnloads)
            _host.Log(LogLevel.Error, $"mod {w.Mod.Id}: its OnUnload and disposal are skipped at {reason}: " +
                                      $"{WaitsFor(w.Mod, w.Mod.ModContext.UnloadWaitsFor()) ?? "a call"} after {sw.Elapsed.TotalSeconds:0.0} s");
        _waitingUnloads.Clear();
        _afterUnloads.Clear();
        ChangeNoticeQueue.FlushFinal(reason);  // what the unloads' writes left waiting: no step follows for it either
        UnloadCaches.Clear(_host.Log);
        PublishSnapshot();
    }

    public bool Reload(string id, out string error)
    {
        var r = ReloadCore(id);
        error = r.Ok ? "" : r.Message;
        return r.Ok;
    }

    /// <summary>Reloads <paramref name="id"/> from disk with its dependents: they unload first (reverse order), then
    /// everything resolves and loads again. Mods that failed for want of it are retried.</summary>
    public ModOperationResult ReloadCore(string id)
    {
        var record = RecordById(id);
        var mod = Find(id);
        // a committed boot mod's boot content stays as committed until the process ends: reloading it needs a restart (before OnUnload)
        if (mod is { Manifest.Boot: not ManifestBoot.None } && BootGeneration.Report(id) is { State: BootModState.Committed })
            return new ModOperationResult(false, $"RestartRequiredBoot: mod {id} has boot content in this process's boot generation; restart the game (or the server) to load a new version", [id]);
        if (record is null && mod is null)
        {
            // first deploy (KilnDeploy -p:KilnReload=true): nothing to reload, but the profile or kiln/mods/<id> may
            // hold it now
            var folder = _options.Profile.Folders.FirstOrDefault(f => f.Id == id && f.Enabled)?.Directory ?? System.IO.Path.Combine(_modsDirectory, id);
            if (!Manifest.IdPattern.IsMatch(id) || !File.Exists(System.IO.Path.Combine(folder, Manifest.FileName)))
                return new ModOperationResult(false, $"mod {id} is not loaded", []);
            var first = TryLoad(folder, out var loadError, out var loadedNow);
            return new ModOperationResult(first, first ? $"loaded {string.Join(", ", loadedNow)} (it was not loaded)" : loadError, loadedNow);
        }
        var directories = new List<string>();
        var cascade = new List<string>();
        if (mod is not null)
        {
            var dependents = Dependents(id);
            directories.Add(mod.Directory);
            directories.AddRange(dependents.Select(d => d.Directory));
            cascade.AddRange(dependents.Select(d => d.Id));
            mod = null;
            Unload(id, ModState.Unloaded, "reloading", out _, out _);
            var waiting = new HashSet<string>(cascade.Prepend(id).Where(UnloadWaiting), StringComparer.Ordinal);
            if (waiting.Count > 0)
            {
                // the new load waits for the old one's unload: same id, same folder
                var dirs = directories.ToList();
                _afterUnloads.Add((waiting, () => FinishReload(id, dirs, cascade)));
                return new ModOperationResult(true, $"reloading {id}: its unload waits for a call still inside " +
                    $"{string.Join(", ", waiting)}; it loads again when that call leaves", [id, .. cascade]);
            }
        }
        else directories.Add(record!.Directory);
        return FinishReload(id, directories, cascade);
    }

    private ModOperationResult FinishReload(string id, List<string> directories, List<string> cascade)
    {
        foreach (var f in FailedDependents([id, .. cascade]))
            if (!directories.Contains(f.Directory))
                directories.Add(f.Directory);
        var loaded = LoadSet(directories.Select(ModCandidate.Read).ToList());
        var ok = loaded.Contains(id) && cascade.TrueForAll(loaded.Contains);
        var version = RecordById(id)?.Manifest?.Version ?? "?";
        var others = loaded.Where(x => x != id).ToList();
        var message = ok
            ? $"reloaded {id} {version}" + (others.Count > 0 ? $" with {string.Join(", ", others)}" : "")
            : RecordById(id)?.Error is { } e && !loaded.Contains(id) ? e
            : $"reloaded {id}, but not all dependents came back: {string.Join(", ", cascade.Where(c => !loaded.Contains(c)))}";
        if (cascade.Count > 0 || !ok) _host.Log(ok ? LogLevel.Info : LogLevel.Error, $"reload {id}: {message}");
        PostEvent(new ModReloadedEvent(id, version, ok, others));
        return new ModOperationResult(ok, message, [id, .. cascade.Union(others)]);
    }

    /// <summary>Full blocking collection; returns how many unloaded mod contexts are still alive.</summary>
    /// <remarks>A context still referenced by a short timer (an unloaded mod's pending <c>Task.Delay</c>) is not a
    /// leak: after the first 10 collections it is retried for up to 0.5 s.</remarks>
    public int CollectAndCountLeaks(out List<string> leaked)
    {
        for (var i = 0; i < 15 && _unloaded.Exists(u => u.Context.IsAlive); i++)
        {
            if (i >= 10) Thread.Sleep(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        _unloaded.RemoveAll(u => !u.Context.IsAlive);
        leaked = _unloaded.Select(u => u.Id).ToList();
        return leaked.Count;
    }

    // ---- a client hosting a world (docs/kiln/client.md, "Mods and sides") ----

    /// <summary>Why a server mod is not loaded on a client right now.</summary>
    public const string HostedReason = HostedReasons.Waiting;
    public const string HostedOffReason = HostedReasons.Off;

    /// <summary>Client: a hosted world's server world runs in this process (between <see cref="StartHosting"/> and
    /// <see cref="StopHosting"/>). Loads and reloads then accept <c>side: "server"</c> mods.</summary>
    public bool Hosting { get; private set; }

    /// <summary>Client, the hosted server world's first step (its thread): loads the <c>side: "server"</c> mods, then
    /// the mods that failed for want of one of them, against the mods already running (client and both mods stay as
    /// they are). Returns the ids loaded, in load order.</summary>
    public List<string> StartHosting()
    {
        if (_options.Side != ManifestSide.Client || Hosting) return [];
        Hosting = true;
        if (!_options.HostedServerMods) return [];
        var dirs = _records.Where(r => r.Mod is null && r.State == ModState.Skipped && r.Error == HostedReason)
            .Select(r => r.Directory).Distinct().ToList();
        if (dirs.Count == 0) return [];
        var sw = Stopwatch.StartNew();
        var loaded = LoadSet(dirs.Select(ModCandidate.Read).ToList());
        // Client and both mods that require a server mod failed in the menu ("dependency ... did not load"): now.
        var dependents = FailedDependents(loaded).Select(r => r.Directory).Where(d => !dirs.Contains(d)).Distinct().ToList();
        if (dependents.Count > 0) loaded.AddRange(LoadSet(dependents.Select(ModCandidate.Read).ToList()));
        _host.Log(LogLevel.Info, $"hosting: {loaded.Count} of {dirs.Count} server-side mod folder(s) loaded for the hosted world " +
                                 $"in {sw.Elapsed.TotalMilliseconds:F1} ms{(loaded.Count > 0 ? ": " + string.Join(", ", loaded) : "")}" +
                                 (dependents.Count > 0 ? $" (with {dependents.Count} mod(s) that need them)" : ""));
        return loaded;
    }

    /// <summary>Client, the hosted server world is going away (its destroy, on its thread, world intact; or a new one
    /// started unseen): every loaded server mod unloads, dependents first, in reverse load order. Server mods start
    /// afresh in the next hosted world (a failure or an operator's unload in this one included); mods that came with
    /// them go back to failed until then. Returns the ids unloaded.</summary>
    public List<string> StopHosting(string reason)
    {
        if (!Hosting) return [];
        ChangeNoticeQueue.FlushFinal(reason);  // what waits for the next step: the server mods see no next step
        Hosting = false;
        var sw = Stopwatch.StartNew();
        var unloaded = new List<string>();
        foreach (var id in _mods.Where(m => m.Manifest.Side == ManifestSide.Server).Select(m => m.Id).Reverse().ToList())
            if (Find(id) is not null && Unload(id, ModState.Skipped, "the hosted world ended", out _, out var affected))
                unloaded.AddRange(affected);
        var gone = unloaded.ToHashSet(StringComparer.Ordinal);
        foreach (var r in _records)
        {
            if (r.Mod is not null || r.Manifest is not { } m) continue;
            if (m.Side == ManifestSide.Server)
            {
                // folders skipped for another reason (disabled in profile, a .disabled file, the profile lists the id)
                // and duplicates stay as they are
                if (r.State == ModState.Skipped && r.Error != HostedReason && !gone.Contains(m.Id)) continue;
                if (r.Error?.StartsWith("duplicate mod id", StringComparison.Ordinal) == true) continue;
                r.State = ModState.Skipped;
                r.Error = _options.HostedServerMods ? HostedReason : HostedOffReason;
            }
            else if (gone.Contains(m.Id))
            {
                r.State = ModState.Failed;
                r.Error = "a server-side dependency unloaded with the hosted world; it loads again with the next one";
            }
        }
        PublishSnapshot();
        ChangeNoticeQueue.FlushFinal(reason);  // what the unloads' writes left waiting: the hosted world has no next step
        if (unloaded.Count > 0 || _options.HostedServerMods)
            _host.Log(LogLevel.Info, $"hosting: the hosted world ended ({reason}): {unloaded.Count} mod(s) unloaded in " +
                                     $"{sw.Elapsed.TotalMilliseconds:F1} ms{(unloaded.Count > 0 ? ": " + string.Join(", ", unloaded) : "")}");
        return unloaded;
    }

    // ---- tick ----

    public void Pre(in TickInfo tick, IReadOnlyList<(LogLevel Level, string Text)> gameLines)
    {
        while (_unobserved.TryDequeue(out var lost)) AttributeUnobserved(lost);
        FinishWaitingUnloads();
        foreach (var mod in _mods)
        {
            if (mod.Disabled) continue;
            var start = Stopwatch.GetTimestamp();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(mod.ModContext.Scheduler.SynchronizationContext);
            HostAccess.Current = mod.ModContext;
            try
            {
                DrainPending(mod);
                if (!mod.Disabled && mod.ModContext.Messages.HasDeliverable)
                    mod.ModContext.Messages.Deliver((m, e) => Fault(mod, $"message handler '{m.Method}' (from {m.Sender})", e));
                if (!mod.Disabled && gameLines.Count > 0 && mod.ModContext.GameLog.HasListeners)
                    foreach (var (level, text) in gameLines)
                    {
                        try
                        {
                            mod.ModContext.GameLog.Raise(level, text);
                        }
                        catch (Exception e)
                        {
                            Fault(mod, "GameLog", e);
                            if (mod.Disabled) break;
                        }
                    }
                if (!mod.Disabled)
                {
                    try
                    {
                        mod.ModContext.Tick.RaisePre(in tick);
                    }
                    catch (Exception e)
                    {
                        Fault(mod, "Pre", e);
                    }
                }
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                HostAccess.Current = null;
            }
            Account(mod, Stopwatch.GetTimestamp() - start, pre: true);
        }
        StartQueuedTests();
        PollTests();
        FlushDeferred();
    }

    public void Post(in TickInfo tick)
    {
        foreach (var mod in _mods)
        {
            if (mod.Disabled || !mod.ModContext.Tick.HasPost) continue;
            var start = Stopwatch.GetTimestamp();
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(mod.ModContext.Scheduler.SynchronizationContext);
            HostAccess.Current = mod.ModContext;
            try
            {
                mod.ModContext.Tick.RaisePost(in tick);
            }
            catch (Exception e)
            {
                Fault(mod, "Post", e);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                HostAccess.Current = null;
            }
            Account(mod, Stopwatch.GetTimestamp() - start, pre: false);
        }
        FlushDeferred();
    }

    /// <summary>Per-mod time spent since the last call, then resets the counters.</summary>
    public IEnumerable<string> TakeStats(double seconds)
    {
        foreach (var m in _mods)
        {
            if (m.Calls == 0) continue;
            double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
            // "max Z ms (pre A / post B)": one field for readers that know only "max Z ms" (no ", " inside)
            yield return $"{m.Id}: pre {ms(m.PreTicks) / m.Calls:F4} ms/step, post {ms(m.PostTicks) / m.Calls:F4} ms/step, " +
                         $"max {ms(m.MaxTicks):F3} ms (pre {ms(m.PreMaxTicks):F3} / post {ms(m.PostMaxTicks):F3}), " +
                         $"{ms(m.PreTicks + m.PostTicks) / seconds:F3} ms/s, faults {m.Faults}" +
                         (m.Disabled ? " (disabled)" : "");
            m.PreTicks = m.PostTicks = m.MaxTicks = m.PreMaxTicks = m.PostMaxTicks = m.Calls = 0;
        }
    }

    // ---- task tick hops (TaskTickDispatcher.cs) ----

    /// <summary>The tick dispatcher of the mods' tasks: their tick hops run through it before the Pre handlers, apart
    /// from <see cref="DrainPending"/>.</summary>
    internal TaskTickDispatcher TaskTick => _taskTick ??= new TaskTickDispatcher(this);
    private TaskTickDispatcher? _taskTick;

    /// <summary>Books the time of one task tick hop to its mod's Pre cost (the hop runs before the mod's Pre handlers).
    /// An owner that is no loaded mod (a Kiln component, a mod that just unloaded) books nothing.</summary>
    internal void AccountTaskHop(ulong owner, long ticks)
    {
        foreach (var mod in _mods)
        {
            if (mod.Owner != owner) continue;
            mod.PreTicks += ticks;
            mod.TotalPreTicks += ticks;
            return;
        }
    }

    /// <summary>Runs what was queued before this step began; work queued while draining (e.g. the next
    /// <c>await NextTick()</c>) waits for the next step. Each item faults on its own.</summary>
    private void DrainPending(LoadedMod mod)
    {
        var ctx = mod.ModContext;
        int count;
        lock (ctx.PendingLock) count = Math.Min(ctx.Pending.Count, 4096);
        for (; count > 0 && !mod.Disabled; count--)
        {
            Action? next;
            lock (ctx.PendingLock)
                if (!ctx.Pending.TryDequeue(out next)) return;
            try
            {
                next();
            }
            catch (Exception e)
            {
                Fault(mod, "scheduled work", e);
            }
        }
    }

    private static void Account(LoadedMod mod, long ticks, bool pre)
    {
        if (pre)
        {
            mod.PreTicks += ticks;
            mod.TotalPreTicks += ticks;
            mod.Calls++;
            mod.TotalCalls++;
            if (ticks > mod.PreMaxTicks) mod.PreMaxTicks = ticks;
        }
        else
        {
            mod.PostTicks += ticks;
            mod.TotalPostTicks += ticks;
            if (ticks > mod.PostMaxTicks) mod.PostMaxTicks = ticks;
        }
        if (ticks > mod.MaxTicks) mod.MaxTicks = ticks;
        if (ticks > mod.TotalMaxTicks) mod.TotalMaxTicks = ticks;
    }

    private bool Invoke(LoadedMod mod, Action<LoadedMod> action, string what)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(mod.ModContext.Scheduler.SynchronizationContext);
        HostAccess.Current = mod.ModContext;
        try
        {
            action(mod);
            return true;
        }
        catch (Exception e)
        {
            Fault(mod, what, e);
            return false;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            HostAccess.Current = null;
        }
    }

    // ---- fault policy ----

    private void Fault(LoadedMod mod, string where, Exception e, bool alreadyLogged = false)
    {
        var policy = _options.Faults;
        mod.Faults++;
        var now = policy.Clock();
        mod.FaultTimes.Enqueue(now);
        if (policy.WindowSeconds > 0)
            while (mod.FaultTimes.Count > 0 && now - mod.FaultTimes.Peek() > policy.WindowSeconds)
                mod.FaultTimes.Dequeue();
        mod.LastFault = DateTime.UtcNow;
        mod.LastFaultMessage = $"{e.GetType().Name}: {e.Message} (in {where})";
        var recent = mod.FaultTimes.Count;
        _host.Log(LogLevel.Error, alreadyLogged
            ? $"mod {mod.Id} fault in {where} ({recent}/{policy.Limit}): {e.GetType().Name}: {e.Message}"
            : $"mod {mod.Id} threw in {where} ({recent}/{policy.Limit}): {e}");
        // A Kiln member this runtime lacks (the runtime throws when it compiles the method that uses it): said once, plainly.
        if (mod.NeedsNewerRuntime == null && NewerRuntime.MissingKilnMember(e) is { } member)
        {
            mod.NeedsNewerRuntime = NewerRuntime.Explain(mod.Id, member, RuntimeName, mod.Manifest.BuiltWith);
            _host.Log(LogLevel.Error, mod.NeedsNewerRuntime);
        }
        if (recent >= policy.Limit && !mod.Disabled) Disable(mod, where, e);
    }

    private void Disable(LoadedMod mod, string where, Exception e)
    {
        var policy = _options.Faults;
        mod.Disabled = true;
        if (!WaitsForQuiet(mod, () => { mod.ModContext.Retire(); _host.OwnerRevoke(mod.Owner); }, "disable"))
        {
            mod.ModContext.Retire();
            _host.OwnerRevoke(mod.Owner);
        }
        var window = policy.WindowSeconds > 0 ? $" within {policy.WindowSeconds:0.#} s" : "";
        if (RecordById(mod.Id) is { } r)
        {
            r.State = ModState.Disabled;
            r.Error = $"disabled after {mod.FaultTimes.Count} faults{window}: {mod.NeedsNewerRuntime ?? mod.LastFaultMessage}";
        }
        PublishSnapshot();  // the crash report's mod list shows it disabled
        var report = _options.CrashReports?.Write("mod-disabled",
            $"mod {mod.Id} {mod.Manifest.Version} disabled after {mod.FaultTimes.Count} faults{window}",
            $"Last fault, in {where}:\n{e}\n\nTotal faults since load: {mod.Faults}\nFolder: {mod.Directory}");
        _host.Log(LogLevel.Error, $"mod {mod.Id} disabled after {mod.FaultTimes.Count} faults{window}; `reload {mod.Id}` to try again" +
                                  (report is null ? "" : $"; crash report {report}"));
        var dependents = Dependents(mod.Id);
        if (dependents.Count > 0)
            _host.Log(LogLevel.Warn, $"mods depending on {mod.Id} keep running: {string.Join(", ", dependents.Select(d => d.Id))}");
        _deferred.Add(new ModDisabledEvent(mod.Id, mod.Faults, mod.LastFaultMessage ?? e.Message, report));
    }

    private void OnHandlerFaulted(EventSubscription sub, Exception e)
    {
        if (sub.Mod is not { } ctx) return;
        var mod = _mods.Find(m => ReferenceEquals(m.ModContext, ctx));
        if (mod is { Disabled: false }) Fault(mod, $"event handler for {sub.Type.Name}", e, alreadyLogged: true);
    }

    /// <summary>An unobserved task exception (any thread, usually the finalizer): attributed on the tick thread to the
    /// mod whose code threw it, by the load context of the throwing frames.</summary>
    public void ReportUnobserved(Exception e) => _unobserved.Enqueue(e);

    private void AttributeUnobserved(Exception e)
    {
        var inner = e is AggregateException { InnerExceptions.Count: > 0 } a ? a.InnerExceptions : [e];
        foreach (var x in inner)
        {
            var ctx = new StackTrace(x, false).GetFrames()
                .Select(f => f.GetMethod()?.DeclaringType?.Assembly)
                .Select(asm => asm is null ? null : AssemblyLoadContext.GetLoadContext(asm) as ModLoadContext)
                .FirstOrDefault(c => c is not null);
            var mod = ctx is null ? null : Find(ctx.ModId);
            if (mod is { Disabled: false } && ReferenceEquals(mod.Context, ctx))  // not an old instance's leftovers
                Fault(mod, "an unobserved Task", x);
            else _host.Log(LogLevel.Warn, $"unobserved task exception: {x}");
        }
    }

    private void FlushDeferred()
    {
        if (_deferred.Count == 0) return;
        var events = _deferred.ToArray();
        _deferred.Clear();
        foreach (var ev in events) PostEvent(ev);
    }

    /// <summary>Posts an event as Kiln itself (no mod context).</summary>
    public void PostEvent(KilnEvent ev)
    {
        var mod = HostAccess.Current;
        var sync = SynchronizationContext.Current;
        HostAccess.Current = null;
        try
        {
            Events.Post(ev);
        }
        catch (Exception e)
        {
            _host.Log(LogLevel.Error, $"posting {ev.GetType().Name} failed: {e}");
        }
        finally
        {
            HostAccess.Current = mod;
            SynchronizationContext.SetSynchronizationContext(sync);
        }
    }

    // ---- tests (Kiln.Testing) ----

    /// <summary>Starts the <c>[KilnTest]</c> methods of a loaded mod; <paramref name="done"/> runs on the tick thread
    /// with the report, or with an error when the run could not finish.</summary>
    public bool StartTests(string id, string? filter, Action<TestReport?, string?>? done, out string error)
    {
        error = "";
        var mod = Find(id);
        if (mod is null || mod.Disabled)
        {
            error = mod is null ? $"mod {id} is not loaded" : $"mod {id} is disabled";
            return false;
        }
        if (_tests.Exists(t => ReferenceEquals(t.Mod, mod)))
        {
            error = $"tests of {id} are already running";
            return false;
        }
        var options = new TestRunOptions
        {
            Filter = filter ?? mod.Manifest.Tests?.Filter,
            Timeout = mod.Manifest.Tests is { TimeoutSeconds: > 0 } t ? TimeSpan.FromSeconds(t.TimeoutSeconds) : _options.TestTimeout,
            ResultsDirectory = _options.TestResultsDirectory,
            Version = mod.Manifest.Version,
        };
        Task<TestReport>? task = null;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(mod.ModContext.Scheduler.SynchronizationContext);
        HostAccess.Current = mod.ModContext;
        try
        {
            task = TestRunner.RunAsync(mod.Instance, mod.Context.Assemblies.ToArray(), options);
        }
        catch (Exception e)
        {
            error = $"tests of {id} could not start: {e.Message}";
            return false;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
            HostAccess.Current = null;
        }
        _tests.Add(new TestRun(mod, task, done));
        return true;
    }

    /// <summary>Queues the auto-run tests (manifest <c>tests.autoRun</c>) of every loaded mod (server started).</summary>
    public void QueueAutoTests()
    {
        foreach (var m in _mods)
            if (m.Manifest.Tests is { AutoRun: true })
                _autoTests.Enqueue(m.Id);
    }

    private void StartQueuedTests()
    {
        while (_autoTests.TryDequeue(out var id))
            if (Find(id) is { Disabled: false } && !StartTests(id, null, null, out var error))
                _host.Log(LogLevel.Warn, $"tests: {error}");
    }

    private void PollTests()
    {
        for (var i = _tests.Count - 1; i >= 0; i--)
        {
            var run = _tests[i];
            string? error = null;
            TestReport? report = null;
            if (run.Task.IsCompleted)
            {
                if (run.Task.IsCompletedSuccessfully) report = run.Task.Result;
                else error = $"test runner failed: {run.Task.Exception?.GetBaseException().Message}";
            }
            else if (!run.Mod.ModContext.Alive) error = $"mod {run.Mod.Id} unloaded during its test run";
            else continue;
            _tests.RemoveAt(i);
            if (error is not null) _host.Log(run.Mod.ModContext.Alive ? LogLevel.Error : LogLevel.Warn, $"tests {run.Mod.Id}: {error}");
            try
            {
                run.Done?.Invoke(report, error);
            }
            catch (Exception e)
            {
                _host.Log(LogLevel.Error, $"tests {run.Mod.Id}: completion failed: {e}");
            }
        }
    }

    // ---- requests from Kiln.Api (Mods.ReloadAsync & co.) ----

    /// <summary>Runs queued mod operations; call on the tick thread outside any mod's code.</summary>
    public void ProcessRequests()
    {
        while (_requests.TryDequeue(out var r))
        {
            ModOperationResult result;
            try
            {
                result = Execute(r.Op, r.Arg);
            }
            catch (Exception e)
            {
                result = new ModOperationResult(false, $"{r.Op.ToString().ToLowerInvariant()} {r.Arg}: {e.Message}", []);
            }
            r.Done?.TrySetResult(result);
            if (r.Callback is not null) Callback(r.Callback, r.Caller, result);
        }
        FlushDeferred();
    }

    public ModOperationResult Execute(ModOperation op, string arg)
    {
        switch (op)
        {
            case ModOperation.Load:
            {
                var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(_modsDirectory, arg));
                if (!dir.StartsWith(System.IO.Path.GetFullPath(_modsDirectory), StringComparison.Ordinal))
                    return new ModOperationResult(false, "load takes a folder name inside kiln/mods", []);
                var ok = TryLoad(dir, out var error, out var affected);
                return new ModOperationResult(ok, ok ? $"loaded {string.Join(", ", affected)}" : error, affected);
            }
            case ModOperation.Unload:
            {
                var ok = Unload(arg, ModState.Unloaded, "unloaded by request", out _, out var affected);
                return new ModOperationResult(ok, ok ? $"unloaded {string.Join(", ", affected)}" : $"mod {arg} is not loaded", affected);
            }
            default:
                return ReloadCore(arg);
        }
    }

    // ---- IModsBackend ----

    IReadOnlyList<ModEntry> IModsBackend.All => _snapshot;
    public ModSide Side => (ModSide)(int)_options.Side;
    public ModEntry? Get(string id) => Array.Find(_snapshot, e => e.Id == id);

    public ModStats Stats(string id)
    {
        if (!_byId.TryGetValue(id, out var m)) return default;
        double ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        var calls = Math.Max(1, m.TotalCalls);
        return new ModStats(m.TotalCalls, ms(m.TotalPreTicks + m.TotalPostTicks), ms(m.TotalPreTicks) / calls,
            ms(m.TotalPostTicks) / calls, ms(m.TotalMaxTicks), m.Faults, m.FaultTimes.Count, m.LastFault, m.LastFaultMessage);
    }

    public bool Send(ModContext? sender, string targetId, string method, object? payload)
    {
        if (!_byId.TryGetValue(targetId, out var target) || target.Disabled) return false;
        return target.ModContext.Messages.Enqueue(new ImcMessage(sender?.Info.Id ?? "kiln", method, payload, sender));
    }

    public Task<ModOperationResult> Request(ModOperation op, string arg)
    {
        var done = new TaskCompletionSource<ModOperationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests.Enqueue((op, arg, done, null, null));
        return done.Task;
    }

    public void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller) =>
        _requests.Enqueue((op, arg, null, done, caller));

    /// <summary>Runs a request's callback as its caller (or as Kiln); dropped when the caller's mod is gone.</summary>
    private void Callback(Action<ModOperationResult> done, ModContext? caller, ModOperationResult result)
    {
        if (caller is not null && !caller.Alive) return;
        var mod = caller is null ? null : _mods.Find(m => ReferenceEquals(m.ModContext, caller));
        if (mod is not null)
        {
            Invoke(mod, _ => done(result), "a mod operation callback");
            return;
        }
        try
        {
            done(result);
        }
        catch (Exception e)
        {
            _host.Log(LogLevel.Error, $"mod operation callback threw: {e}");
        }
    }

    private void PublishSnapshot(bool writeFile = true)
    {
        var list = new List<ModEntry>();
        var index = 0;
        foreach (var m in _mods)
            if (RecordById(m.Id) is { } r)
                list.Add(Entry(r, index++));
        foreach (var r in _records)
            if (r.Mod is null)
                list.Add(Entry(r, -1));
        _snapshot = list.ToArray();
        if (writeFile) WriteStateFile();
    }

    private static ModEntry Entry(ModRecord r, int index)
    {
        var m = r.Manifest;
        var state = r.Mod is { Disabled: true } ? ModState.Disabled : r.State;
        var deps = m?.Dependencies.Select(d => new ModDependency(d.Id, d.RangeText, d.Optional)).ToArray() ?? [];
        return new ModEntry(m?.Id ?? r.Key, m?.Name ?? r.Key, m?.Version ?? "?", r.Directory, (ModSide)(int)(m?.Side ?? ManifestSide.Server),
            state, r.Error, deps, index, r.Mod?.ModContext.Info ?? (m is null ? null : Info(m, r.Directory)));
    }

    /// <summary>The mod list as text (crash reports, mods.txt).</summary>
    /// <summary>state/mods.json (schema/state-mods.schema.json): the same records as mods.txt, for the launcher.</summary>
    private void WriteModsJson()
    {
        if (_options.ModsJsonFile is not { } file) return;
        try
        {
            var profile = _options.Profile;
            var list = new System.Text.Json.Nodes.JsonArray();
            foreach (var e in _snapshot)
            {
                var r = _records.Find(x => x.Directory == e.Directory);
                var m = r?.Manifest;
                var stats = e.IsLoaded || e.State == ModState.Disabled ? Stats(e.Id) : default;
                list.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = e.Id,
                    ["version"] = m?.Version,
                    ["name"] = m?.Name,
                    ["folder"] = e.Directory,
                    ["source"] = r?.Source ?? SourceOf(e.Directory),
                    ["state"] = e.State.ToString().ToLowerInvariant(),
                    ["reason"] = e.Error,
                    ["loadIndex"] = e.LoadIndex >= 0 ? e.LoadIndex : null,
                    ["side"] = m is null ? null : m.Side.ToString().ToLowerInvariant(),
                    ["clients"] = m is null ? null : Manifest.ClientsName(m.Clients),
                    ["faults"] = stats.Faults,
                });
            }
            var doc = new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = 1,
                ["pid"] = Environment.ProcessId,
                ["written"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                ["generation"] = Interlocked.Increment(ref _modsJsonGeneration),
                ["side"] = _options.Side == ManifestSide.Client ? "client" : "server",
                ["api"] = Kiln.Mods.RuntimeApiVersion,
                ["profile"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = profile.Id.Length > 0 ? profile.Id : null,
                    ["generation"] = profile.Generation,
                    ["mode"] = profile.ModeName,
                },
                ["mods"] = list,
            };
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", doc.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _host.Log(LogLevel.Warn, $"mods: cannot write {file}: {e.Message}");
        }
    }

    public string Describe()
    {
        var sb = new StringBuilder();
        foreach (var e in _snapshot)
        {
            sb.Append(e.LoadIndex >= 0 ? $"{e.LoadIndex + 1,3}. " : "   - ");
            sb.Append($"{e.Id} {e.Version} [{e.State.ToString().ToLowerInvariant()}]");
            if (e.IsLoaded || e.State == ModState.Disabled)
            {
                var s = Stats(e.Id);
                sb.Append($" faults {s.Faults}, {s.PreMsPerStep + s.PostMsPerStep:F3} ms/step");
            }
            if (e.Error is { } err) sb.Append($": {err}");
            sb.Append($"  ({System.IO.Path.GetFileName(e.Directory)})\n");
        }
        return sb.Length == 0 ? "   (none)\n" : sb.ToString();
    }

    private void WriteStateFile()
    {
        WriteModsJson();
        if (_options.StateFile is not { } file) return;
        try
        {
            File.WriteAllText(file + ".tmp", $"# Kiln mods ({DateTime.Now:yyyy-MM-dd HH:mm:ss}), load order; written by Kiln.Host\n" + Describe());
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (IOException)
        {
            // best effort: only crash reports read it
        }
    }
}
