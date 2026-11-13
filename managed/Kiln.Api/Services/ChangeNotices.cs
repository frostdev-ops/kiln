// SPDX-License-Identifier: MIT
// The change events of Kiln's managed services (the contract: every write Kiln makes for a mod has one), posted so that
// a handler that writes again cannot loop. docs/kiln/modding/contract.md, "Change events".
namespace Kiln;

/// <summary>
/// The queues of <see cref="ChangeNotices{TEvent,TKey}"/> that have notices waiting for the next step. The service tick
/// delivers them first thing (<see cref="FlushAll"/>, before the queued events, the service handlers and every mod's
/// tick), and the mod manager once more before mods unload (<see cref="FlushFinal"/>).
/// </summary>
internal abstract class ChangeNoticeQueue
{
    private static readonly object Gate = new();
    private static ChangeNoticeQueue[] _queues = [];
    private int _listed;

    /// <summary>Puts this queue on the list the service tick flushes (once; any thread).</summary>
    protected void List()
    {
        if (Volatile.Read(ref _listed) != 0) return;
        lock (Gate)
        {
            if (_listed != 0) return;
            _queues = [.. _queues, this];
            Volatile.Write(ref _listed, 1);
        }
    }

    /// <summary>The start of a service tick (tick thread): every notice that waited goes out. All queues are taken first,
    /// then delivered, so a write made in any of their handlers waits for the next tick, whichever queue comes first.</summary>
    internal static void FlushAll()
    {
        var queues = Volatile.Read(ref _queues);
        var due = new object?[queues.Length];
        for (var i = 0; i < queues.Length; i++) due[i] = queues[i].TakeDue();
        for (var i = 0; i < queues.Length; i++)
            if (due[i] is { } batch)
                queues[i].Deliver(batch);
    }

    /// <summary>
    /// Before mods unload (the server stops, a client's hosted world closes; tick thread): the notices that wait for the
    /// next step go out now, since that step never comes for these mods. A write made by this last round's handlers would
    /// be announced at the step after; there is none, so it is not, and the count is logged. Returns that count.
    /// </summary>
    internal static int FlushFinal(string why)
    {
        if (!Game.IsTickThread) return 0;  // nothing is dispatched off the tick thread; they wait for the next tick
        var previousMod = ModContext.CurrentMod;
        var previousSync = SynchronizationContext.Current;
        ModContext.CurrentMod = null;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            FlushAll();
            var left = 0;
            foreach (var q in Volatile.Read(ref _queues)) left += q.Discard();
            if (left > 0)
            {
                try
                {
                    Backend.Current.Log(0, LogLevel.Info,
                        $"kiln: {left} change event(s) of writes made in the last round before mods unloaded ({why}) were not posted: no step follows");
                }
                catch
                {
                    // a backend that cannot log: nothing left to say
                }
            }
            return left;
        }
        finally
        {
            ModContext.CurrentMod = previousMod;
            SynchronizationContext.SetSynchronizationContext(previousSync);
        }
    }

    /// <summary>Test hook (<see cref="ServiceTick.Reset"/>): every waiting notice is dropped, the list is emptied.</summary>
    internal static void ResetAll()
    {
        lock (Gate)
        {
            foreach (var q in _queues)
            {
                q.Discard();
                Volatile.Write(ref q._listed, 0);
            }
            _queues = [];
        }
    }

    /// <summary>Takes what waits (null: nothing); the queue is empty after it.</summary>
    private protected abstract object? TakeDue();

    /// <summary>Delivers what <see cref="TakeDue"/> took.</summary>
    private protected abstract void Deliver(object batch);

    private protected abstract int Discard();
}

/// <summary>
/// Posts one kind of change event for the writes a service makes. Inline, on the tick thread, right after the write took,
/// except while an event of this kind is already being dispatched (a handler of it, or of a base type such as
/// <see cref="KilnEvent"/>, wrote again): then the event waits for the next step and goes out at its start, before
/// anything else of that step runs, one per key (the newest write of a key wins), under the same rule. So a handler that
/// writes in answer to the event sees its own write once a step at most, never recursively, and a mod built before the
/// event existed (a catch-all handler that writes) keeps working: no <see cref="Events.MaxDepth"/> exception, no faulted
/// handler.
/// </summary>
/// <remarks>
/// <para>What a waiting event is guaranteed: it is never dropped for want of room (the set holds one event per key
/// written during dispatch in one step and is emptied at the next). An event about an entity (<c>subject</c> of
/// <see cref="Post"/>) carries the entity's spawn serial from the moment of the write, and goes out only while that entity
/// is still there: when it is gone, or its id now names another entity, the event is dropped instead of naming the
/// wrong one. With no entity source to ask (no Kiln.Spawn, a unit test) it goes out as written. Before mods unload,
/// <see cref="ChangeNoticeQueue.FlushFinal"/> sends what waits; a write made in that last round is not announced.</para>
/// <para>The guard is released in a <c>finally</c>: a handler that throws (the bus catches it) or a post the bus refuses
/// (nested <see cref="Events.MaxDepth"/> levels deep in other events' handlers: the event then waits for the next step)
/// never leaves it set. A write on another thread waits for the next step too (no serial is read there). The caller checks
/// <see cref="Events.HasSubscribers{T}"/> first, so an event nobody listens for is never built.</para>
/// </remarks>
/// <typeparam name="TEvent">The service's change event.</typeparam>
/// <typeparam name="TKey">What one write changed (an entity and a field, an entry and a path): two waiting events of one
/// key are one.</typeparam>
internal sealed class ChangeNotices<TEvent, TKey>(string owner) : ChangeNoticeQueue where TEvent : KilnEvent where TKey : notnull
{
    // An event waiting for the next step, with what is known of its entity at the write.
    private readonly record struct Notice(TEvent Event, EntityId Subject, Identity Known, ulong Serial, Func<bool>? Check = null);

    private enum Identity : byte
    {
        None,       // no entity: content, a setting, the clock
        Unknown,    // an entity, but no source could say which (no entity source; a write off the tick thread)
        Captured,   // an entity, and Serial is the one it had at the write (0: it has no stamp)
        Checked,    // something else: Check says whether it is still the one written
    }

    private readonly object _gate = new();
    private int _dispatching;                           // tick thread only: an event of this kind is being dispatched
    private Dictionary<TKey, Notice> _waiting = new(); // under _gate

    /// <summary>Events waiting for the next step (tests).</summary>
    internal int Waiting
    {
        get
        {
            lock (_gate) return _waiting.Count;
        }
    }

    /// <summary>Posts <paramref name="ev"/> for a write that took (see the class remarks for when it waits). Never throws
    /// for the event: the write already happened.</summary>
    /// <param name="key">What the write changed: two waiting events of one key are one.</param>
    /// <param name="ev">The event.</param>
    /// <param name="subject">The entity written, when the event names one: a waiting event is dropped if that entity is
    /// gone by its delivery (its id may name another by then).</param>
    public void Post(TKey key, TEvent ev, EntityId subject = default)
    {
        if (!Game.IsTickThread)
        {
            Wait(key, new Notice(ev, subject, subject.IsNone ? Identity.None : Identity.Unknown, 0));
            return;
        }
        if (_dispatching > 0)
        {
            Wait(key, Capture(ev, subject));
            return;
        }
        Supersede(key);
        Dispatch(key, ev, subject);
    }

    // An inline write of a key: an older event of that key still waiting would announce an older value later, after this
    // newer one. It goes; this event says what the value is now.
    private void Supersede(TKey key)
    {
        lock (_gate) _waiting.Remove(key);
    }

    /// <summary>As <see cref="Post"/>, for an event about something that is not an entity and whose id can name another
    /// one later (an impact instance): <paramref name="stillThere"/> was made at the write and says, at a waiting event's
    /// delivery, whether it is still the one written (false or a throw: the event is dropped). Inline it is not called.</summary>
    public void PostChecked(TKey key, TEvent ev, Func<bool> stillThere)
    {
        if (!Game.IsTickThread || _dispatching > 0)
        {
            Wait(key, new Notice(ev, default, Identity.Checked, 0, stillThere));
            return;
        }
        Supersede(key);
        Dispatch(key, ev, default, stillThere);
    }

    private void Dispatch(TKey key, TEvent ev, EntityId subject, Func<bool>? check = null)
    {
        _dispatching++;
        try
        {
            Events.Post(ev);
        }
        catch (InvalidOperationException)
        {
            // posted MaxDepth levels deep inside other handlers: it goes out at the start of the next step instead
            Wait(key, check is null ? Capture(ev, subject) : new Notice(ev, default, Identity.Checked, 0, check));
        }
        finally
        {
            _dispatching--;
        }
    }

    // The write just took on the tick thread, so the entity there now is the one written.
    private static Notice Capture(TEvent ev, EntityId subject)
    {
        if (subject.IsNone) return new Notice(ev, subject, Identity.None, 0);
        try
        {
            return IdentitySources.TryGetSerial(subject, out var serial)
                ? new Notice(ev, subject, Identity.Captured, serial)
                : new Notice(ev, subject, Identity.Captured, ulong.MaxValue);  // gone already: never delivered
        }
        catch (Exception)
        {
            return new Notice(ev, subject, Identity.Unknown, 0);
        }
    }

    // Still the entity that was written? Asked on the tick thread at delivery.
    private static bool StillThere(in Notice w)
    {
        if (w.Known == Identity.Checked)
        {
            try
            {
                return w.Check!();
            }
            catch (Exception)
            {
                return false;
            }
        }
        if (w.Known != Identity.Captured) return true;
        if (w.Serial == ulong.MaxValue) return false;
        try
        {
            return IdentitySources.TryGetSerial(w.Subject, out var now) && now == w.Serial;
        }
        catch (Exception)
        {
            return false;  // the sources answered at the write and cannot now: the world it was in has gone
        }
    }

    private void Wait(TKey key, Notice w)
    {
        lock (_gate) _waiting[key] = w;
        List();
    }

    private protected override object? TakeDue()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0) return null;
            var due = _waiting;
            _waiting = new Dictionary<TKey, Notice>();
            return due;
        }
    }

    private protected override void Deliver(object batch)
    {
        // a write in their handlers waits for the next step again
        foreach (var (key, w) in (Dictionary<TKey, Notice>)batch)
            if (StillThere(in w)) Dispatch(key, w.Event, w.Subject, w.Check);
    }

    private protected override int Discard()
    {
        lock (_gate)
        {
            var n = _waiting.Count;
            if (n > 0) _waiting = new Dictionary<TKey, Notice>();
            return n;
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{owner} change events ({typeof(TEvent).Name})";
}
