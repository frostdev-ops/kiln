// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kiln.Api.Tests")]
[assembly: InternalsVisibleTo("Kiln.Ui.Tests")]
[assembly: InternalsVisibleTo("Kiln.Ui.Preview")]

namespace Kiln;

/// <summary>Base class of every event on Kiln's event bus (<see cref="Events"/>).</summary>
/// <remarks>Services define their events in their own assembly (e.g. <c>Kiln.Combat</c> defines its damage event);
/// mods may define their own events and post them for other mods. A handler subscribed to a base class also receives
/// every derived event. An event class's static constructor runs when the first handler subscribes to that exact
/// class: services use it to start producing the event only once someone listens.</remarks>
public abstract class KilnEvent
{
    // Services with a private dispatch thread must not also run on the tick bus.
    internal virtual bool TickDispatchAllowed => true;
}

/// <summary>An event whose default outcome a handler can stop by setting <see cref="Canceled"/>.</summary>
public abstract class CancelableEvent : KilnEvent
{
    /// <summary>Set by a handler to stop the default outcome. Later handlers only see a canceled event if they
    /// subscribed with <c>receiveCanceled</c> (and may set it back to false).</summary>
    public bool Canceled { get; set; }

    public void Cancel() => Canceled = true;

    /// <summary>Who canceled it: the id of the mod whose handler set <see cref="Canceled"/> ("kiln" for Kiln's own
    /// handlers); null while it is not canceled or when the poster canceled it before posting.</summary>
    public string? CanceledBy { get; internal set; }
}

/// <summary>Outcome of an event that asks for a decision (Forge's <c>Event.Result</c>).</summary>
public enum EventResult
{
    /// <summary>Nobody decided: the poster does what it would do without Kiln.</summary>
    Default = 0,
    /// <summary>Force the outcome even where the game would refuse it.</summary>
    Allow = 1,
    /// <summary>Refuse the outcome.</summary>
    Deny = 2,
}

/// <summary>An event with an <see cref="EventResult"/>; see <see cref="ResultEvent"/>.</summary>
public interface IResultEvent
{
    EventResult Result { get; set; }
}

/// <summary>An event that asks handlers for a decision: Allow, Deny or Default (Forge's <c>@HasResult</c>). The last
/// handler to set <see cref="Result"/> wins; <see cref="EventPriority.Monitor"/> handlers must only read it.</summary>
public abstract class ResultEvent : KilnEvent, IResultEvent
{
    public EventResult Result { get; set; }
    public void Allow() => Result = EventResult.Allow;
    public void Deny() => Result = EventResult.Deny;

    /// <summary>The decision, with <paramref name="vanilla"/> for <see cref="EventResult.Default"/>.</summary>
    public bool Decide(bool vanilla) => Result switch
    {
        EventResult.Allow => true,
        EventResult.Deny => false,
        _ => vanilla,
    };
}

/// <summary>A <see cref="CancelableEvent"/> that also carries an <see cref="EventResult"/>.</summary>
public abstract class CancelableResultEvent : CancelableEvent, IResultEvent
{
    public EventResult Result { get; set; }
    public void Allow() => Result = EventResult.Allow;
    public void Deny() => Result = EventResult.Deny;

    public bool Decide(bool vanilla) => Result switch
    {
        EventResult.Allow => true,
        EventResult.Deny => false,
        _ => vanilla,
    };
}

/// <summary>Order in which handlers run. <see cref="Monitor"/> runs last and must only observe the final result.</summary>
public enum EventPriority
{
    Highest = 0,
    High = 1,
    Normal = 2,
    Low = 3,
    Lowest = 4,
    Monitor = 5,
}

/// <summary>Marks a method of a <see cref="Mod"/> (or of an object passed to <see cref="Events.Register"/>) as an
/// event handler. The method takes exactly one parameter, the event type, and returns void.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SubscribeAttribute : Attribute
{
    public SubscribeAttribute(EventPriority priority = EventPriority.Normal) => Priority = priority;

    public EventPriority Priority { get; }

    /// <summary>Also run for events an earlier handler canceled.</summary>
    public bool ReceiveCanceled { get; init; }
}

/// <summary>Time spent in one handler since it subscribed (or since <see cref="Events.Diagnostics.ResetStats"/>).</summary>
public readonly record struct EventHandlerStats(long Calls, double TotalMs, double MaxMs, int Faults)
{
    public double AverageMs => Calls == 0 ? 0 : TotalMs / Calls;
}

/// <summary>One handler on the bus. Dispose to unsubscribe; a mod's subscriptions also end when it unloads.</summary>
public sealed class EventSubscription : IDisposable
{
    internal EventSubscription(Type type, Delegate handler, EventPriority priority, bool receiveCanceled, ModContext? mod,
        long order, EventFilter? filter = null)
    {
        Filter = filter;
        Type = type;
        Handler = handler;
        Priority = priority;
        ReceiveCanceled = receiveCanceled;
        Mod = mod;
        Order = order;
        Owner = mod?.Info.Id ?? "kiln";
        Name = $"{handler.Method.DeclaringType?.Name}.{handler.Method.Name}";
    }

    public Type Type { get; }
    public EventPriority Priority { get; }
    public bool ReceiveCanceled { get; }

    /// <summary>What this handler receives (<see cref="Events.On{T}(Action{T}, EventFilter, EventPriority, bool)"/>); null:
    /// every event of its type. Since 0.2.0-dev.27.</summary>
    public EventFilter? Filter { get; }

    /// <summary>Id of the mod that subscribed ("kiln" for Kiln's own code).</summary>
    public string Owner { get; }

    /// <summary>"Type.Method" of the handler (diagnostics).</summary>
    public string Name { get; }

    internal Delegate Handler { get; }
    internal ModContext? Mod { get; }
    internal long Order { get; }
    internal object? Target { get; init; }
    internal Action<object>? Invoke { get; set; }
    private bool _active = true;
    public bool Active { get => Volatile.Read(ref _active); internal set => Volatile.Write(ref _active, value); }

    // Also written by service dispatchers (e.g. the render thread).
    internal long Calls, TotalTicks, MaxTicks;
    internal int Faults;

    public EventHandlerStats Stats => new(Interlocked.Read(ref Calls), Interlocked.Read(ref TotalTicks) * 1000.0 / Stopwatch.Frequency,
        Interlocked.Read(ref MaxTicks) * 1000.0 / Stopwatch.Frequency, Volatile.Read(ref Faults));

    public void Dispose() => Events.Remove(this);

    public override string ToString() => $"{Type.Name} -> {Owner}:{Name} ({Priority})";
}

/// <summary>A row of <see cref="Events.Diagnostics.Handlers"/>.</summary>
public sealed record EventHandlerInfo(string Event, string Owner, string Handler, EventPriority Priority,
    bool ReceiveCanceled, EventHandlerStats Stats);

/// <summary>Kiln's event bus, shared by all mods and services (Forge's <c>EVENT_BUS</c>).</summary>
/// <remarks>
/// <para>Events are posted and handled on the tick thread (<see cref="Enqueue"/> posts from other threads at the next
/// step). Each handler runs as the mod that subscribed it, so work it starts (service calls, scheduled tasks) is
/// attributed to that mod; an exception in one handler is logged and the remaining handlers still run. A handler that
/// throws <see cref="MaxFaults"/> times is removed.</para>
/// <para>Dispatch works on a snapshot: a handler may post other events (up to <see cref="MaxDepth"/> levels deep),
/// subscribe (the new handler sees the next post, not the current one) or unsubscribe (takes effect at once: a removed
/// handler later in the current dispatch does not run). <see cref="On{T}"/>, <see cref="Register"/> and
/// <c>Dispose</c> work from any thread; <see cref="HasSubscribers{T}"/> is lock-free and costs two memory reads.</para>
/// </remarks>
public static class Events
{
    /// <summary>A handler is removed after this many exceptions.</summary>
    public const int MaxFaults = 5;

    /// <summary>How deep posts inside handlers may nest before <see cref="Post{T}"/> refuses (a loop between handlers).</summary>
    public const int MaxDepth = 32;

    private static readonly object Lock = new();
    private static readonly Dictionary<Type, List<EventSubscription>> ByType = new();
    // Written only under Lock (and cleared on every change); read without it.
    private static readonly ConcurrentDictionary<Type, EventSubscription[]> Resolved = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static readonly ConcurrentQueue<KilnEvent> Queued = new();
    private static long _order;
    private static int _version;
    private static int _depth;      // tick thread only
    private static long _posts, _dispatched;

    // Per-type answer of HasSubscribers<T>: (version << 1) | has, in one word so readers never see a torn pair. Holds
    // no references, so a mod's event type in here never keeps the mod's load context alive.
    private static class SubscriberCache<T>
    {
        public static long State = -1;
    }

    /// <summary>Subscribes <paramref name="handler"/> to <typeparamref name="T"/> and every event derived from it.</summary>
    public static EventSubscription On<T>(Action<T> handler, EventPriority priority = EventPriority.Normal,
        bool receiveCanceled = false) where T : KilnEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(typeof(T), handler, priority, receiveCanceled, e => handler((T)e), null);
    }

    /// <summary>Subscribes <paramref name="handler"/> to the events of <typeparamref name="T"/> that pass
    /// <paramref name="filter"/>: with <see cref="EventFilter.Entities"/>, only events about one of those entities
    /// (<see cref="IEntityEvent"/>). A service that holds something for its handlers (kiln.combat holds every hit while
    /// a handler wants hits) holds only what some handler's filter lets through, so watching one entity does not slow
    /// the rest of the world. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="handler"/> or <paramref name="filter"/> is null.</exception>
    /// <exception cref="ArgumentException">An entity filter on events that do not say which entities they are about
    /// (<typeparamref name="T"/> is not an <see cref="IEntityEvent"/>), or an entity filter with no entity.</exception>
    [AnyThread]
    public static EventSubscription On<T>(Action<T> handler, EventFilter filter, EventPriority priority = EventPriority.Normal,
        bool receiveCanceled = false) where T : KilnEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Entities is { } entities)
        {
            if (!typeof(IEntityEvent).IsAssignableFrom(typeof(T)))
                throw new ArgumentException($"{typeof(T).Name} does not say which entities it is about (it is no IEntityEvent): an entity filter cannot apply", nameof(filter));
            if (entities.Count == 0) throw new ArgumentException("an entity filter names no entity", nameof(filter));
        }
        return Add(typeof(T), handler, priority, receiveCanceled, e => handler((T)e), null, filter);
    }

    /// <summary>The entities the handlers of <paramref name="type"/> filter for, for a service that holds work for them
    /// (kiln.combat's hit hold): null when any handler takes every event of the type (no entity filter), else the union of
    /// their entity filters. Any thread; computed on demand (cache it by <see cref="Version"/>).</summary>
    internal static IReadOnlySet<EntityId>? EntityFilterUnion(Type type)
    {
        HashSet<EntityId>? union = null;
        foreach (var sub in HandlersFor(type))
        {
            if (!sub.Active) continue;
            if (sub.Filter?.Entities is not { } set) return null;
            (union ??= []).UnionWith(set);
        }
        return union ?? (IReadOnlySet<EntityId>)new HashSet<EntityId>();
    }

    /// <summary>Changes whenever a subscription is added or removed (wraps around). Any thread.</summary>
    internal static int Version => Volatile.Read(ref _version);

    /// <summary>Subscribes every <see cref="SubscribeAttribute"/> method of <paramref name="target"/> (instance and
    /// static methods of its type; pass a <see cref="Type"/> for its static methods only). Kiln calls this for each
    /// <see cref="Mod"/> before <c>OnLoad</c>, and then also registers the mod's <c>[Command]</c> methods (Kiln.Chat).
    /// Nothing is subscribed when one method has the wrong shape.</summary>
    public static IReadOnlyList<EventSubscription> Register(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var staticOnly = target is Type;
        var type = target as Type ?? target.GetType();
        var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly |
                    (staticOnly ? 0 : BindingFlags.Instance);
        var plan = new List<(Type Event, Delegate Handler, SubscribeAttribute Attr)>();
        for (var t = type; t != null && t != typeof(object) && t != typeof(Mod); t = t.BaseType)
        {
            foreach (var m in t.GetMethods(flags))
            {
                var attr = m.GetCustomAttribute<SubscribeAttribute>();
                if (attr == null) continue;
                var ps = m.GetParameters();
                if (ps.Length != 1 || !typeof(KilnEvent).IsAssignableFrom(ps[0].ParameterType) || m.ReturnType != typeof(void) ||
                    m.IsGenericMethodDefinition)
                    throw new ArgumentException($"[Subscribe] method {t.Name}.{m.Name} must be void and take one KilnEvent");
                var evType = ps[0].ParameterType;
                var delegateType = typeof(Action<>).MakeGenericType(evType);
                var del = m.IsStatic ? m.CreateDelegate(delegateType) : m.CreateDelegate(delegateType, target);
                plan.Add((evType, del, attr));
            }
        }
        var subs = new List<EventSubscription>(plan.Count);
        foreach (var (evType, del, attr) in plan)
            subs.Add(Add(evType, del, attr.Priority, attr.ReceiveCanceled, MakeInvoker(evType, del), target));
        if (target is Mod mod && mod.Context is { } context)
        {
            Watch(context);
            ServiceTick.Offer(context);
            ModScan.RegisterCommands(mod);
        }
        return subs;
    }

    /// <summary>Removes every subscription <see cref="Register"/> made for <paramref name="target"/>.</summary>
    public static int Unregister(object target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var n = 0;
        lock (Lock)
        {
            foreach (var list in ByType.Values)
                n += list.RemoveAll(s =>
                {
                    if (!ReferenceEquals(s.Target, target)) return false;
                    s.Active = false;
                    return true;
                });
            if (n > 0) ChangedLocked();
        }
        return n;
    }

    /// <summary>Runs every handler for <paramref name="ev"/> in priority order (then subscription order) and returns
    /// it, so the poster can read <see cref="CancelableEvent.Canceled"/>, a <see cref="ResultEvent.Result"/> and fields
    /// handlers changed. Tick thread.</summary>
    public static T Post<T>(T ev) where T : KilnEvent
    {
        ArgumentNullException.ThrowIfNull(ev);
        RequireTickDispatch(ev);
        Game.AssertTickThread();
        var handlers = HandlersFor(ev.GetType());
        if (handlers.Length == 0) return ev;
        if (_depth >= MaxDepth)
            throw new InvalidOperationException($"event {ev.GetType().Name} posted {MaxDepth} levels deep inside handlers (a loop?)");
        _depth++;
        _posts++;
        var cancelable = ev as CancelableEvent;
        var outer = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        try
        {
            foreach (var sub in handlers)
            {
                if (!sub.Active) continue;
                if (sub.Filter is { } filter && !filter.Passes(ev)) continue;
                var wasCanceled = cancelable is { Canceled: true };
                if (wasCanceled && !sub.ReceiveCanceled) continue;
                var mod = sub.Mod;
                if (mod is { Alive: false }) continue;
                ModContext.CurrentMod = mod;
                SynchronizationContext.SetSynchronizationContext(mod?.Scheduler.SynchronizationContext ?? outerSync);
                var start = Stopwatch.GetTimestamp();
                try
                {
                    sub.Invoke!(ev);
                }
                catch (Exception e)
                {
                    Fault(sub, ev, e);
                }
                var ticks = Stopwatch.GetTimestamp() - start;
                RecordCall(sub, ticks);
                if (cancelable != null && cancelable.Canceled != wasCanceled)
                    cancelable.CanceledBy = cancelable.Canceled ? sub.Owner : null;
            }
        }
        finally
        {
            _depth--;
            ModContext.CurrentMod = outer;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
        return ev;
    }

    /// <summary>Posts <paramref name="ev"/> on the tick thread at the start of the next step. Any thread. Use it from
    /// background work; the poster cannot read the outcome.</summary>
    public static void Enqueue(KilnEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        RequireTickDispatch(ev);
        if (Queued.Count >= 65536) throw new InvalidOperationException("more than 65536 events are waiting for the tick thread");
        Queued.Enqueue(ev);
    }

    /// <summary>Whether anything listens for <typeparamref name="T"/> (or a base class of it). Services check this
    /// before building an event, so unused events cost nothing. Lock-free; any thread.</summary>
    public static bool HasSubscribers<T>() where T : KilnEvent
    {
        var state = Volatile.Read(ref SubscriberCache<T>.State);
        return (state >> 1) == Volatile.Read(ref _version) ? (state & 1) != 0 : RefreshHas<T>();
    }

    /// <summary>Whether anything listens for events of <paramref name="type"/>.</summary>
    public static bool HasSubscribers(Type type) => HandlersFor(type).Length > 0;

    /// <summary>Raised (tick thread) when a handler throws; the loader counts faults per mod.</summary>
    internal static event Action<EventSubscription, Exception>? HandlerFaulted;

    /// <summary>All active subscriptions (diagnostics).</summary>
    public static IReadOnlyList<EventSubscription> Subscriptions
    {
        get
        {
            lock (Lock) return ByType.Values.SelectMany(l => l).ToArray();
        }
    }

    /// <summary>Handler timings and counters.</summary>
    public static class Diagnostics
    {
        /// <summary>Every active handler with its timing, slowest total first.</summary>
        public static IReadOnlyList<EventHandlerInfo> Handlers =>
            Subscriptions.Select(s => new EventHandlerInfo(s.Type.FullName ?? s.Type.Name, s.Owner, s.Name, s.Priority,
                    s.ReceiveCanceled, s.Stats))
                .OrderByDescending(h => h.Stats.TotalMs).ToArray();

        /// <summary>Posts that had at least one handler, since the start.</summary>
        public static long Posts => Interlocked.Read(ref _posts);

        /// <summary>Handler calls, since the start.</summary>
        public static long HandlerCalls => Interlocked.Read(ref _dispatched);

        /// <summary>Events waiting in <see cref="Enqueue"/>'s queue.</summary>
        public static int Queued => Events.Queued.Count;

        /// <summary>Zeroes every handler's timing (not the fault counts).</summary>
        public static void ResetStats()
        {
            foreach (var s in Subscriptions)
            {
                Interlocked.Exchange(ref s.Calls, 0);
                Interlocked.Exchange(ref s.TotalTicks, 0);
                Interlocked.Exchange(ref s.MaxTicks, 0);
            }
        }
    }

    private static void RequireTickDispatch(KilnEvent ev)
    {
        if (!ev.TickDispatchAllowed)
            throw new InvalidOperationException($"{ev.GetType().Name} is dispatched only by its service, not by Events.Post or Events.Enqueue");
    }

    internal static void RecordCall(EventSubscription sub, long ticks)
    {
        Interlocked.Increment(ref sub.Calls);
        Interlocked.Add(ref sub.TotalTicks, ticks);
        var max = Interlocked.Read(ref sub.MaxTicks);
        while (ticks > max)
        {
            var seen = Interlocked.CompareExchange(ref sub.MaxTicks, ticks, max);
            if (seen == max) break;
            max = seen;
        }
        Interlocked.Increment(ref _dispatched);
    }

    internal static void Remove(EventSubscription sub)
    {
        lock (Lock)
        {
            if (!sub.Active) return;
            sub.Active = false;
            if (ByType.TryGetValue(sub.Type, out var list))
            {
                list.Remove(sub);
                if (list.Count == 0) ByType.Remove(sub.Type);
            }
            ChangedLocked();
        }
    }

    /// <summary>Drops every subscription of <paramref name="mod"/> (also done automatically when it retires).</summary>
    internal static void RemoveAll(ModContext mod)
    {
        lock (Lock)
        {
            foreach (var list in ByType.Values)
                list.RemoveAll(s =>
                {
                    if (!ReferenceEquals(s.Mod, mod)) return false;
                    s.Active = false;
                    return true;
                });
            foreach (var key in ByType.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) ByType.Remove(key);
            ChangedLocked();  // also forgets cached handler arrays of the mod's own event types
            Watched.Remove(mod);
        }
    }

    /// <summary>Test hook: forget everything.</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            foreach (var s in ByType.Values.SelectMany(l => l)) s.Active = false;
            ByType.Clear();
            Watched.Clear();
            ChangedLocked();
        }
        while (Queued.TryDequeue(out _)) { }
        _depth = 0;
    }

    /// <summary>Posts what <see cref="Enqueue"/> queued before this call (the service tick runs it every step).</summary>
    internal static void DrainQueued()
    {
        for (var n = Queued.Count; n > 0 && Queued.TryDequeue(out var ev); n--)
        {
            try
            {
                Post(ev);
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Error, $"events: queued {ev.GetType().Name} failed: {e.Message}");
            }
        }
    }

    /// <summary>Makes <paramref name="mod"/>'s unload drop its subscriptions and the bus's caches of its types.</summary>
    internal static void Watch(ModContext mod)
    {
        lock (Lock)
        {
            if (!Watched.Add(mod)) return;
            mod.Retiring += () => RemoveAll(mod);
            // Retire may have already taken its event snapshot before we installed the cleanup hook.
            if (!mod.Alive) RemoveAll(mod);
        }
    }

    private static EventSubscription Add(Type type, Delegate handler, EventPriority priority, bool receiveCanceled,
        Action<object> invoke, object? target, EventFilter? filter = null)
    {
        if (!typeof(KilnEvent).IsAssignableFrom(type)) throw new ArgumentException($"{type} is not a KilnEvent");
        var mod = ModContext.CurrentMod;
        if (mod is { Alive: false }) throw new InvalidOperationException("the mod is unloading");
        EventSubscription sub;
        lock (Lock)
        {
            sub = new EventSubscription(type, handler, priority, receiveCanceled, mod, ++_order, filter)
            {
                Invoke = invoke,
                Target = target,
            };
            if (!ByType.TryGetValue(type, out var list)) ByType[type] = list = new List<EventSubscription>();
            list.Add(sub);
            ChangedLocked();
        }
        if (mod != null)
        {
            Watch(mod);
            // The mod may have retired after the initial Alive check, even if it was already watched.
            if (!mod.Alive) RemoveAll(mod);
        }
        StartProducers(type);
        return sub;
    }

    /// <summary>Runs the static constructors of the subscribed class and its bases, so a service can start producing
    /// an event lazily (e.g. Kiln.Players' join pump) the first time someone subscribes to it.</summary>
    private static void StartProducers(Type type)
    {
        for (var t = type; t != null && t != typeof(KilnEvent) && t != typeof(object); t = t.BaseType)
        {
            if (t.Assembly == typeof(KilnEvent).Assembly) break;
            try
            {
                RuntimeHelpers.RunClassConstructor(t.TypeHandle);
            }
            catch (Exception e)
            {
                Backend.Current.Log(0, LogLevel.Warn, $"events: static constructor of {t.Name} failed: {e.InnerException?.Message ?? e.Message}");
            }
        }
    }

    private static void ChangedLocked()
    {
        Resolved.Clear();
        Volatile.Write(ref _version, (_version + 1) & 0x3FFFFFFF);
    }

    private static EventSubscription[] HandlersFor(Type type)
    {
        if (Resolved.TryGetValue(type, out var cached)) return cached;
        lock (Lock) return HandlersForLocked(type);
    }

    /// <summary>The handlers <see cref="Post{T}"/> would run for <paramref name="type"/>, in order (a cached array: the
    /// same instance until a subscription changes). For services that dispatch an event on a thread of their own
    /// (Kiln.Ui's render events on the render thread). Any thread.</summary>
    internal static EventSubscription[] HandlersOf(Type type) => HandlersFor(type);

    private static EventSubscription[] HandlersForLocked(Type type)
    {
        if (Resolved.TryGetValue(type, out var cached)) return cached;
        List<EventSubscription>? all = null;
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            if (ByType.TryGetValue(t, out var list) && list.Count > 0)
                (all ??= new List<EventSubscription>()).AddRange(list);
        var arr = all is null ? Array.Empty<EventSubscription>() : all.OrderBy(s => s.Priority).ThenBy(s => s.Order).ToArray();
        Resolved[type] = arr;
        return arr;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool RefreshHas<T>() where T : KilnEvent
    {
        int version;
        bool has;
        lock (Lock)
        {
            version = _version;
            has = HandlersForLocked(typeof(T)).Length > 0;
        }
        Volatile.Write(ref SubscriberCache<T>.State, ((long)version << 1) | (has ? 1L : 0L));
        return has;
    }

    private static void Fault(EventSubscription sub, KilnEvent ev, Exception e)
    {
        Interlocked.Increment(ref sub.Faults);
        var what = $"event {ev.GetType().Name}: handler {sub.Name} threw ({sub.Faults}/{MaxFaults})";
        if (sub.Mod != null) sub.Mod.Log.Error(e, what);
        else Backend.Current.Log(0, LogLevel.Error, $"{what}: {e}");
        if (sub.Faults >= MaxFaults)
        {
            Remove(sub);
            var gone = $"event {ev.GetType().Name}: handler {sub.Name} removed after {MaxFaults} faults";
            if (sub.Mod != null) sub.Mod.Log.Error(gone);
            else Backend.Current.Log(0, LogLevel.Error, gone);
        }
        try
        {
            HandlerFaulted?.Invoke(sub, e);
        }
        catch
        {
            // a fault counter must not break dispatch
        }
    }

    private static Action<object> MakeInvoker(Type evType, Delegate del)
    {
        var m = typeof(Events).GetMethod(nameof(Typed), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(evType);
        return (Action<object>)m.Invoke(null, [del])!;
    }

    private static Action<object> Typed<T>(Delegate del)
    {
        var a = (Action<T>)del;
        return e => a((T)e);
    }
}
