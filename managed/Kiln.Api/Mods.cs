// SPDX-License-Identifier: MIT
// The mod list, per-mod state and stats, and inter-mod messages (Forge: ModList + InterModComms).
namespace Kiln;

/// <summary>Where a mod runs.</summary>
public enum ModSide
{
    /// <summary>Dedicated server only (the default).</summary>
    Server = 0,
    /// <summary>Game client only.</summary>
    Client = 1,
    /// <summary>Server and client.</summary>
    Both = 2,
}

/// <summary>What happened to a mod Kiln found in <c>kiln/mods</c>.</summary>
public enum ModState
{
    /// <summary>Found and accepted; waiting for its turn in the load order.</summary>
    Pending = 0,
    /// <summary>Running.</summary>
    Loaded = 1,
    /// <summary>Loaded, then switched off by the fault policy (too many exceptions). <c>reload</c> retries it.</summary>
    Disabled = 2,
    /// <summary>Not loaded: bad manifest, missing or mismatched dependency, cycle, incompatibility, missing feature,
    /// or an exception while loading. <see cref="ModEntry.Error"/> says which.</summary>
    Failed = 3,
    /// <summary>Not for this side (e.g. a client mod on a server), or switched off with a <c>.disabled</c> file.</summary>
    Skipped = 4,
    /// <summary>Unloaded by an operator or by an unload of one of its dependencies.</summary>
    Unloaded = 5,
}

/// <summary>A dependency declared in <c>kiln.mod.json</c>.</summary>
/// <param name="Id">The mod id depended on (<c>kiln</c> means Kiln itself).</param>
/// <param name="VersionRange">Accepted versions, e.g. <c>^1.2</c>, <c>&gt;=1.0 &lt;2</c>, <c>[1.0,2.0)</c>; <c>*</c> = any.</param>
/// <param name="Optional">Load after it when present; load anyway when absent.</param>
public sealed record ModDependency(string Id, string VersionRange, bool Optional);

/// <summary>Time and faults of one mod since it was loaded.</summary>
public readonly record struct ModStats(
    long Steps, double TotalMs, double PreMsPerStep, double PostMsPerStep, double MaxMs, int Faults,
    int RecentFaults, DateTime? LastFault, string? LastFaultMessage);

/// <summary>One mod Kiln knows about (loaded or not). A snapshot: read <see cref="Mods.Get"/> again for fresh state.</summary>
public sealed class ModEntry
{
    internal ModEntry(string id, string name, string version, string directory, ModSide side, ModState state,
        string? error, IReadOnlyList<ModDependency> dependencies, int loadIndex, ModInfo? info)
    {
        Id = id;
        Name = name;
        Version = version;
        Directory = directory;
        Side = side;
        State = state;
        Error = error;
        Dependencies = dependencies;
        LoadIndex = loadIndex;
        Info = info;
    }

    public string Id { get; }
    public string Name { get; }
    public string Version { get; }
    public string Directory { get; }
    public ModSide Side { get; }
    public ModState State { get; }

    /// <summary>Why the mod is not loaded (or was disabled); null otherwise.</summary>
    public string? Error { get; }

    public IReadOnlyList<ModDependency> Dependencies { get; }

    /// <summary>Position in the load order (0 = first), -1 when not loaded.</summary>
    public int LoadIndex { get; }

    /// <summary>The full manifest metadata, when the manifest could be read.</summary>
    public ModInfo? Info { get; }

    public bool IsLoaded => State == ModState.Loaded;

    /// <summary>Live stats (tick time, faults) of the running instance; zero when not loaded.</summary>
    public ModStats Stats => ModsBackend.Current?.Stats(Id) ?? default;

    public override string ToString() => $"{Id} {Version} ({State.ToString().ToLowerInvariant()})";
}

/// <summary>Result of a mod operation requested through <see cref="Mods"/>.</summary>
/// <param name="Ok">Whether the operation fully succeeded.</param>
/// <param name="Message">What happened, for logs and chat.</param>
/// <param name="Affected">Every mod id the operation unloaded or loaded (a reload cascades to dependents).</param>
public sealed record ModOperationResult(bool Ok, string Message, IReadOnlyList<string> Affected);

/// <summary>The mod list, per-mod state and inter-mod messages.</summary>
public static class Mods
{
    /// <summary>The Kiln mod API version this Kiln implements; a manifest's <c>apiVersion</c> is checked against it.
    /// Kiln keeps the API additive within a major version (including 0.x): an older minor keeps working.</summary>
    public const string ApiVersion = "0.2.0";

    /// <summary>Every mod Kiln found, in load order (loaded first), then the ones that did not load.</summary>
    public static IReadOnlyList<ModEntry> All => ModsBackend.Current?.All ?? [];

    /// <summary>The side this process runs as (from the core: dedicated server or client).</summary>
    public static ModSide Side => ModsBackend.Current?.Side ?? ModSide.Server;

    public static bool IsLoaded(string id) => Get(id)?.State == ModState.Loaded;

    /// <summary>The mod with <paramref name="id"/>, loaded or not; null when Kiln never saw it.</summary>
    public static ModEntry? Get(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return ModsBackend.Current?.Get(id);
    }

    /// <summary>The mod whose code is running on this thread (null outside mod code).</summary>
    public static ModEntry? Current => ModContext.CurrentMod is { } c ? Get(c.Info.Id) : null;

    /// <summary>
    /// Sends an inter-mod message (Forge's <c>InterModComms.sendTo</c>). Thread-safe. The target receives it on the
    /// tick thread, before its next Pre handlers, in its own context, through a handler registered with
    /// <see cref="ModMessages.On"/>. Messages wait (at most 256 per target) until the target registers a handler for
    /// <paramref name="method"/>; they are dropped when the target or the sender unloads.
    /// </summary>
    /// <param name="targetId">The receiving mod.</param>
    /// <param name="method">A name the receiver understands, e.g. <c>"register-recipe"</c>.</param>
    /// <param name="payload">Any object. Use types both mods can see: BCL types, Kiln types, or types from an
    /// assembly the receiver exports (<c>exports</c> in its manifest) — not types private to the sender.</param>
    /// <returns>False when the target is not loaded.</returns>
    public static bool Send(string targetId, string method, object? payload = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetId);
        ArgumentException.ThrowIfNullOrEmpty(method);
        return ModsBackend.Current?.Send(ModContext.CurrentMod, targetId, method, payload) ?? false;
    }

    /// <summary>Reloads a mod from disk at the start of the next step, together with every mod that depends on it
    /// (dependents unload first and load again after it). Completes on the tick thread; if the caller itself is part
    /// of the reload, its continuation is dropped with its old instance.</summary>
    public static Task<ModOperationResult> ReloadAsync(string id) => Request(ModOperation.Reload, id);

    /// <summary>Unloads a mod (and its dependents) at the start of the next step.</summary>
    public static Task<ModOperationResult> UnloadAsync(string id) => Request(ModOperation.Unload, id);

    /// <summary>Loads the mod folder <c>kiln/mods/&lt;folder&gt;</c> at the start of the next step.</summary>
    public static Task<ModOperationResult> LoadAsync(string folder) => Request(ModOperation.Load, folder);

    /// <summary>Callback forms for code without a synchronization context (services, chat commands): the operation
    /// runs at the start of the next step and <paramref name="done"/> runs right after it on the tick thread, in the
    /// caller's mod context; it is dropped when the caller's own mod was unloaded by the operation.</summary>
    public static void Reload(string id, Action<ModOperationResult> done) => Request(ModOperation.Reload, id, done);

    /// <inheritdoc cref="Reload(string, Action{ModOperationResult})"/>
    public static void Unload(string id, Action<ModOperationResult> done) => Request(ModOperation.Unload, id, done);

    /// <inheritdoc cref="Reload(string, Action{ModOperationResult})"/>
    public static void Load(string folder, Action<ModOperationResult> done) => Request(ModOperation.Load, folder, done);

    private static void Request(ModOperation op, string arg, Action<ModOperationResult> done)
    {
        ArgumentException.ThrowIfNullOrEmpty(arg);
        ArgumentNullException.ThrowIfNull(done);
        if (ModsBackend.Current is { } backend) backend.Request(op, arg, done, ModContext.CurrentMod);
        else done(new ModOperationResult(false, "Kiln's mod manager is not running", []));
    }

    private static Task<ModOperationResult> Request(ModOperation op, string arg)
    {
        ArgumentException.ThrowIfNullOrEmpty(arg);
        return ModsBackend.Current?.Request(op, arg)
               ?? Task.FromResult(new ModOperationResult(false, "Kiln's mod manager is not running", []));
    }
}

/// <summary>One inter-mod message.</summary>
public sealed class ImcMessage
{
    internal ImcMessage(string sender, string method, object? payload, ModContext? senderContext)
    {
        Sender = sender;
        Method = method;
        Payload = payload;
        SenderContext = senderContext;
    }

    /// <summary>The sending mod's id (<c>kiln</c> when Kiln or a service sent it).</summary>
    public string Sender { get; }
    public string Method { get; }
    public object? Payload { get; }
    internal ModContext? SenderContext { get; }

    /// <summary>The payload as <typeparamref name="T"/>, or default when it is something else.</summary>
    public T? PayloadAs<T>() => Payload is T t ? t : default;

    public override string ToString() => $"{Sender} -> {Method}";
}

/// <summary>A mod's inbox for <see cref="Mods.Send"/>. Handlers run on the tick thread in the receiving mod's context.</summary>
public sealed class ModMessages
{
    private const int MaxQueued = 256;
    private readonly ModContext _owner;
    private readonly object _lock = new();
    private readonly List<ImcMessage> _queue = [];
    private readonly Dictionary<string, List<Action<ImcMessage>>> _handlers = new(StringComparer.Ordinal);
    private long _dropped;

    internal ModMessages(ModContext owner) => _owner = owner;

    /// <summary>Handles messages for <paramref name="method"/>; dispose the result to stop. Ends on unload.</summary>
    public IDisposable On(string method, Action<ImcMessage> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock)
        {
            if (!_owner.Alive) throw new InvalidOperationException("the mod is unloading");
            if (!_handlers.TryGetValue(method, out var list)) _handlers[method] = list = [];
            list.Add(handler);
        }
        return new Registration(this, method, handler);
    }

    /// <summary>Takes (removes and returns) queued messages for <paramref name="method"/> that no handler took, for
    /// mods that prefer to poll (Forge's <c>InterModComms.getMessages</c>).</summary>
    public IReadOnlyList<ImcMessage> Take(string method)
    {
        lock (_lock)
        {
            var taken = _queue.Where(m => m.Method == method).ToArray();
            _queue.RemoveAll(m => m.Method == method);
            return taken;
        }
    }

    /// <summary>Messages waiting for a handler.</summary>
    public int Queued
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    /// <summary>Messages dropped because the inbox was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    internal bool Enqueue(ImcMessage message)
    {
        lock (_lock)
        {
            if (!_owner.Alive) return false;
            if (_queue.Count >= MaxQueued)
            {
                _queue.RemoveAt(0);
                _dropped++;
            }
            _queue.Add(message);
            return true;
        }
    }

    internal bool HasDeliverable
    {
        get
        {
            lock (_lock) return _queue.Count > 0 && _handlers.Count > 0;
        }
    }

    /// <summary>Host, tick thread, inside the owner's context: runs handlers for queued messages that have one.
    /// <paramref name="fault"/> receives each handler exception.</summary>
    internal void Deliver(Action<ImcMessage, Exception> fault)
    {
        List<(ImcMessage, Action<ImcMessage>[])> batch = [];
        lock (_lock)
        {
            if (_queue.Count == 0 || _handlers.Count == 0) return;
            for (var i = 0; i < _queue.Count;)
            {
                if (_handlers.TryGetValue(_queue[i].Method, out var hs) && hs.Count > 0)
                {
                    batch.Add((_queue[i], hs.ToArray()));
                    _queue.RemoveAt(i);
                }
                else i++;
            }
        }
        foreach (var (message, handlers) in batch)
            foreach (var h in handlers)
            {
                if (!_owner.Alive) return;
                try
                {
                    h(message);
                }
                catch (Exception e)
                {
                    fault(message, e);
                }
            }
    }

    /// <summary>Drops queued messages from <paramref name="sender"/> (it unloaded; its payloads may pin its context).</summary>
    internal void DropFrom(ModContext sender)
    {
        lock (_lock) _queue.RemoveAll(m => ReferenceEquals(m.SenderContext, sender));
    }

    internal void Clear()
    {
        lock (_lock)
        {
            _queue.Clear();
            _handlers.Clear();
        }
    }

    private sealed class Registration(ModMessages owner, string method, Action<ImcMessage> handler) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock)
                if (owner._handlers.TryGetValue(method, out var list))
                {
                    list.Remove(handler);
                    if (list.Count == 0) owner._handlers.Remove(method);
                }
        }
    }
}

internal enum ModOperation
{
    Load,
    Unload,
    Reload,
}

/// <summary>Seam between <see cref="Mods"/> and the mod manager in Kiln.Host (or a test fake).</summary>
internal interface IModsBackend
{
    IReadOnlyList<ModEntry> All { get; }
    ModSide Side { get; }
    ModEntry? Get(string id);
    ModStats Stats(string id);
    bool Send(ModContext? sender, string targetId, string method, object? payload);
    Task<ModOperationResult> Request(ModOperation op, string arg);
    void Request(ModOperation op, string arg, Action<ModOperationResult> done, ModContext? caller);
}

internal static class ModsBackend
{
    public static IModsBackend? Current { get; set; }
}
