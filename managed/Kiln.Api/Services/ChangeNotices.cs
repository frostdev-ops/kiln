// SPDX-License-Identifier: MIT
// The change events of Kiln's managed services (the contract: every write Kiln makes for a mod has one), posted so that
// a handler that writes again cannot loop. docs/kiln/modding/contract.md, "Change events".
namespace Kiln;

/// <summary>
/// Posts one kind of change event for the writes a service makes. Inline, on the tick thread, right after the write took,
/// except while an event of this kind is already being dispatched (a handler of it, or of a base type such as
/// <see cref="KilnEvent"/>, wrote again): then the event waits for the next service tick, one per key (the newest write
/// of a key wins), and is posted there under the same rule. So a handler that writes in answer to the event sees its own
/// write once a step at most, never recursively, and a mod built before the event existed (a catch-all handler that
/// writes) keeps working: no <see cref="Events.MaxDepth"/> exception, no faulted handler.
/// </summary>
/// <remarks>The guard is released in a <c>finally</c>: a handler that throws (the bus catches it) or a post the bus
/// refuses (nested <see cref="Events.MaxDepth"/> levels deep in other events' handlers: the event then waits for the next
/// step) never leaves it set. A write on another thread queues its event for the next step
/// (<see cref="Events.Enqueue"/>). The caller checks <see cref="Events.HasSubscribers{T}"/> first, so an event nobody
/// listens for is never built.</remarks>
/// <typeparam name="TEvent">The service's change event.</typeparam>
/// <typeparam name="TKey">What one write changed (an entity and a field, an entry and a path): two waiting events of one
/// key are one.</typeparam>
internal sealed class ChangeNotices<TEvent, TKey>(string owner) where TEvent : KilnEvent where TKey : notnull
{
    /// <summary>At most this many events wait for the next step; one more is dropped (and logged once).</summary>
    public const int MaxWaiting = 4096;

    private int _dispatching;                         // tick thread only: an event of this kind is being dispatched
    private Dictionary<TKey, TEvent> _waiting = new(); // tick thread only
    private IDisposable? _flush;
    private int _flushGeneration = -1;
    private bool _warned;

    /// <summary>Events waiting for the next service tick (tests).</summary>
    internal int Waiting => _waiting.Count;

    /// <summary>Posts <paramref name="ev"/> for a write that took (see the class remarks for when it waits). Never throws
    /// for the event: the write already happened.</summary>
    public void Post(TKey key, TEvent ev)
    {
        if (!Game.IsTickThread)
        {
            try
            {
                Events.Enqueue(ev);
            }
            catch (InvalidOperationException)
            {
                Dropped();
            }
            return;
        }
        if (_dispatching > 0)
        {
            Wait(key, ev);
            return;
        }
        Dispatch(key, ev);
    }

    private void Dispatch(TKey key, TEvent ev)
    {
        _dispatching++;
        try
        {
            Events.Post(ev);
        }
        catch (InvalidOperationException)
        {
            // posted MaxDepth levels deep inside other handlers: it goes out at the start of the next step instead
            Wait(key, ev);
        }
        finally
        {
            _dispatching--;
        }
    }

    private void Wait(TKey key, TEvent ev)
    {
        if (_waiting.Count >= MaxWaiting && !_waiting.ContainsKey(key))
        {
            Dropped();
            return;
        }
        _waiting[key] = ev;
        // registered again after a test reset the service tick (its entries are gone then)
        if (_flush is null || _flushGeneration != ServiceTick.Generation)
        {
            _flush?.Dispose();
            _flush = ServiceTick.Add(owner + " change events", Flush);
            _flushGeneration = ServiceTick.Generation;
        }
    }

    private void Flush(in TickInfo tick)
    {
        if (_waiting.Count == 0) return;
        var due = _waiting;
        _waiting = new Dictionary<TKey, TEvent>();
        foreach (var (key, ev) in due) Dispatch(key, ev);  // a write in their handlers waits for the next step again
    }

    private void Dropped()
    {
        if (_warned) return;
        _warned = true;
        try
        {
            Backend.Current.Log(0, LogLevel.Warn,
                $"{owner}: more than {MaxWaiting} {typeof(TEvent).Name}s waited for the next step; the rest were dropped");
        }
        catch
        {
            // a backend that cannot log: nothing left to say
        }
    }
}
