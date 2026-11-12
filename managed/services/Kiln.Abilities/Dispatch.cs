// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>Kiln.Abilities' native listener (Pre, tick thread) and job-thread wrap dispatcher. Lives in the default
/// load context: the function pointers handed to native stay valid for the life of the process; mods' delegates are
/// reached only through these tables and dropped when a mod retires.</summary>
internal static unsafe class AbilitiesDispatch
{
    private static int _installed;
    private static readonly HashSet<ModContext> WatchedMods = new(ReferenceEqualityComparer.Instance);
    private static readonly object Gate = new();

    /// <summary>Per-step work of the service (cast timers, sequence polling); run in the listener.</summary>
    internal static event Action<ulong>? Tick;

    internal static ulong Step;

    public static void Ensure()
    {
        if (Volatile.Read(ref _installed) == 1) return;
        AbilitiesCaps.Ensure();
        var t = AbilitiesNative.T;
        if (t == null) return;
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        t->SetListener(&Listener, null);
        t->SetWrapDispatcher(&WrapDispatch, null);
        ModLog.Info("abilities: managed listener installed");
    }

    public static void Watch(ModContext mod)
    {
        lock (Gate)
            if (!WatchedMods.Add(mod)) return;
        mod.Retiring += () => Retire(mod);
    }

    private static void Retire(ModContext mod)
    {
        Abilities.Natives.Unregister(mod);
        RemoveWraps(mod);
        RemoveHoldFilters(mod);
        AbilityRegistry.Retire(mod);
        lock (Gate) WatchedMods.Remove(mod);
        if (AbilitiesNative.T is var t && t != null && mod.Owner != 0 && Game.IsTickThread) t->ReleaseOwner(mod.Owner);
    }

    // ----------------------------------------------------------------------------------------------- interest -----

    private static uint Interest()
    {
        uint m = 0;
        if (Events.HasSubscribers<ImpactStartedEvent>()) m |= AbilitiesNative.Bit(AbilitiesNative.EvImpactStarted);
        if (Events.HasSubscribers<ImpactStartingEvent>()) m |= AbilitiesNative.InterestHold;
        if (Events.HasSubscribers<ImpactEndedEvent>()) m |= AbilitiesNative.Bit(AbilitiesNative.EvImpactEnded);
        if (Events.HasSubscribers<ImpactNativeEvent>()) m |= AbilitiesNative.Bit(AbilitiesNative.EvNative);
        if (Events.HasSubscribers<ImpactSignalEvent>() || Abilities.RemovalsPending) m |= AbilitiesNative.Bit(AbilitiesNative.EvSignal);
        if (!Abilities.Natives.ManagedNatives.IsEmpty) m |= AbilitiesNative.Bit(AbilitiesNative.EvManagedCall);
        return m;
    }

    // ----------------------------------------------------------------------------------------------- listener -----

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Listener(AbilitiesNative.Event* evs, uint n, void* user)
    {
        try
        {
            if (n > 0) Deliver(evs, (int)n);
        }
        catch (Exception e)
        {
            ModLog.Error($"abilities: listener threw: {e}");
        }
        try
        {
            Step++;
            Abilities.ExpireRemovals(Step);
            Tick?.Invoke(Step);
        }
        catch (Exception e)
        {
            ModLog.Error($"abilities: service tick threw: {e}");
        }
        try
        {
            return Interest();
        }
        catch
        {
            return 0;
        }
    }

    private static void Deliver(AbilitiesNative.Event* evs, int n)
    {
        for (var i = 0; i < n; i++)
        {
            ref var ev = ref evs[i];
            var impact = new ImpactRef(ev.ImpactId, ev.ProgramId, new EntityId(ev.Self), new EntityId(ev.Root));
            switch (ev.Kind)
            {
                case AbilitiesNative.EvImpactStarted:
                    if (ev.Held != 0 && Events.HasSubscribers<ImpactStartingEvent>())
                    {
                        var se = Events.Post(new ImpactStartingEvent(impact, ev.Step));
                        if (se.Canceled) ev.Canceled = 1;
                    }
                    if (Events.HasSubscribers<ImpactStartedEvent>()) Events.Post(new ImpactStartedEvent(impact, ev.Step));
                    break;
                case AbilitiesNative.EvImpactEnded:
                    if (Events.HasSubscribers<ImpactEndedEvent>()) Events.Post(new ImpactEndedEvent(impact, ev.Step));
                    break;
                case AbilitiesNative.EvNative:
                    if (!Events.HasSubscribers<ImpactNativeEvent>()) break;
                    var snap = Snapshot(ref ev, impact);
                    Events.Post<ImpactNativeEvent>(ev.NativeHash switch
                    {
                        0xb04d0e7a => new ImpactDamageEvent(snap),
                        0x91f30b26 => new ImpactBuffEvent(snap),
                        0x61b11b03 => new ImpactSpawnEvent(snap),
                        _ => new ImpactNativeEvent(snap),
                    });
                    break;
                case AbilitiesNative.EvSignal:
                    // Kiln's remove-buff program answers here; mods never see its signal
                    if (ev.Tag == Abilities.RemoveSignalTag && Abilities.CompleteRemoval(ev.Self, ev.ValueSize > 0 && ev.Value[0] != 0)) break;
                    if (!Events.HasSubscribers<ImpactSignalEvent>()) break;
                    var val = new byte[Math.Min(ev.ValueSize, 64)];
                    for (var k = 0; k < val.Length; k++) val[k] = ev.Value[k];
                    Events.Post(new ImpactSignalEvent(impact, ev.Tag, ev.Site, ev.ValueType, val, ev.Step));
                    break;
                case AbilitiesNative.EvManagedCall:
                    Answer(ref ev, impact);
                    break;
            }
        }
    }

    private static NativeSnapshot Snapshot(ref AbilitiesNative.Event ev, ImpactRef impact)
    {
        var argc = (int)Math.Min(ev.Argc, 31);
        var args = new uint[argc];
        var values = new uint[argc];
        var offs = new byte[argc];
        var sizes = new byte[argc];
        for (var k = 0; k < argc; k++)
        {
            args[k] = ev.Args[k];
            values[k] = ev.ArgValues[k];
            offs[k] = ev.ArgOffsets[k];
            sizes[k] = ev.ArgSizes[k];
        }
        var payload = new byte[136];
        for (var k = 0; k < payload.Length; k++) payload[k] = ev.Value[k];
        return new NativeSnapshot
        {
            Hash = ev.NativeHash, Impact = impact, Site = ev.Site, Step = ev.Step, Result = ev.Result, Error = ev.Error,
            Args = args, Values = values, Offsets = offs, Sizes = sizes, Payload = payload,
        };
    }

    private static void Answer(ref AbilitiesNative.Event ev, ImpactRef impact)
    {
        var t = AbilitiesNative.T;
        if (!Abilities.Natives.ManagedNatives.TryGetValue(ev.NativeHash, out var m) || m.Mod is { Alive: false })
        {
            t->NativeComplete(ev.CallId, 0, 0);
            return;
        }
        var args = new uint[Math.Min(ev.Argc, 31)];
        for (var k = 0; k < args.Length; k++) args[k] = ev.Args[k];
        var call = new NativeInvocation(m.Name, impact, ev.Site, args);
        byte value = 0, error = 0;
        var outerMod = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        try
        {
            ModContext.CurrentMod = m.Mod;
            if (m.Mod is not null) SynchronizationContext.SetSynchronizationContext(m.Mod.Scheduler.SynchronizationContext);
            value = m.Handler(call);
        }
        catch (Exception e)
        {
            error = 0x11;  // keen::ErrorId Internal: the instance stops with Error
            (m.Mod?.Log ?? null)?.Error(e, $"native {m.Name} threw");
        }
        finally
        {
            ModContext.CurrentMod = outerMod;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
        t->NativeComplete(ev.CallId, error, value);
    }

    // ----------------------------------------------------------------------------------------------- wraps (job) ---

    internal sealed class WrapEntry
    {
        public required uint Hash;
        public required NativeWrapHandler Handler;
        public required int Priority;
        public required ModContext? Mod;
        // NativeCall.Bytes for this handler: the gate's answer for Mod, decided at registration (where the mod is known)
        // and read by every call of the handler on the job threads.
        public required bool RawAllowed;
        public long Order;
        public int Faults;
        public bool Active = true;
    }

    private sealed class WrapHandle(WrapEntry e) : IDisposable
    {
        public void Dispose() => RemoveWrap(e);
    }

    // hash -> handlers, sorted (copy on write; job threads read without locks)
    private static readonly ConcurrentDictionary<uint, WrapEntry[]> Wraps = new();
    private static long _order;
    private static readonly StrongBox<long> InFlight = new(0);

    internal static IDisposable AddWrap(uint hash, NativeWrapHandler handler, int priority)
    {
        Game.AssertTickThread();
        Ensure();
        var t = AbilitiesNative.Require("Abilities.Natives.Wrap");
        var mod = ModContext.CurrentMod;
        var e = Insert(hash, handler, priority, mod);
        var rc = t->NativeWrap(hash, 1);
        if (rc != AbilitiesNative.Ok)
        {
            RemoveWrap(e);
            throw new InvalidOperationException($"Natives.Wrap(0x{hash:x8}): {AbilitiesNative.Describe(rc)} (not a vanilla native?)");
        }
        if (mod is not null) Watch(mod);
        mod?.Log.Info($"abilities: wraps native {Abilities.Natives.NameOf(hash)} (job thread)");
        return new WrapHandle(e);
    }

    // Puts the handler in the job-thread table (no native call: AddWrap asks the core to route the native here). The
    // registering mod is known here, so NativeCall.Bytes' answer for it is decided here (tests call this directly).
    internal static WrapEntry Insert(uint hash, NativeWrapHandler handler, int priority, ModContext? mod)
    {
        var e = new WrapEntry
        {
            Hash = hash, Handler = handler, Priority = priority, Mod = mod, RawAllowed = mod is { Info.Advanced: true },
            Order = Interlocked.Increment(ref _order),
        };
        lock (Gate)
        {
            var cur = Wraps.TryGetValue(hash, out var a) ? a : [];
            Wraps[hash] = cur.Append(e).OrderByDescending(x => x.Priority).ThenBy(x => x.Order).ToArray();
        }
        return e;
    }

    internal static void RemoveWrap(WrapEntry e)
    {
        bool empty;
        lock (Gate)
        {
            e.Active = false;
            if (!Wraps.TryGetValue(e.Hash, out var a)) return;
            var next = a.Where(x => !ReferenceEquals(x, e)).ToArray();
            empty = next.Length == 0;
            if (empty) Wraps.TryRemove(e.Hash, out _);
            else Wraps[e.Hash] = next;
        }
        if (empty && AbilitiesNative.T is var t && t != null) t->NativeWrap(e.Hash, 0);
        Drain();
    }

    private static void RemoveWraps(ModContext mod)
    {
        foreach (var kv in Wraps)
            foreach (var e in kv.Value)
                if (ReferenceEquals(e.Mod, mod)) RemoveWrap(e);
    }

    // Waits for job threads still inside a handler (they cannot enter removed ones any more).
    private static void Drain()
    {
        var sw = Stopwatch.StartNew();
        while (Interlocked.Read(ref InFlight.Value) > 0 && sw.ElapsedMilliseconds < 1000) Thread.SpinWait(64);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void WrapDispatch(AbilitiesNative.NativeCall* c, void* user)
    {
        Interlocked.Increment(ref InFlight.Value);
        var outer = ModContext.CurrentMod;
        try
        {
            if (!Wraps.TryGetValue(c->Hash, out var handlers)) return;  // OutError stays 0xFF: the game's native runs
            foreach (var h in handlers)
            {
                if (!h.Active || h.Mod is { Alive: false }) continue;
                ModContext.CurrentMod = h.Mod;
                try
                {
                    var call = new NativeCall(c, h.Mod, h.RawAllowed);
                    h.Handler(ref call);
                }
                catch (Exception e)
                {
                    if (Interlocked.Increment(ref h.Faults) <= 3)
                        h.Mod?.Log.Error($"abilities: wrap of {Abilities.Natives.NameOf(h.Hash)} threw: {e.GetType().Name}: {e.Message}");
                }
            }
        }
        catch
        {
            // never throw into native code
        }
        finally
        {
            ModContext.CurrentMod = outer;
            Interlocked.Decrement(ref InFlight.Value);
        }
    }

    // ----------------------------------------------------------------------------------------------- hold filter --

    private sealed class HoldFilter(ModContext? mod, uint[] ids) : IDisposable
    {
        public readonly ModContext? Mod = mod;
        public readonly uint[] Ids = ids;
        public void Dispose() => RemoveHold(this);
    }

    private static readonly List<HoldFilter> Holds = new();

    internal static IDisposable AddHoldFilter(uint[] ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var f = new HoldFilter(ModContext.CurrentMod, ids.ToArray());
        lock (Gate) Holds.Add(f);
        if (f.Mod is not null) Watch(f.Mod);
        PushHolds();
        return f;
    }

    private static void RemoveHold(HoldFilter f)
    {
        lock (Gate) Holds.Remove(f);
        PushHolds();
    }

    private static void RemoveHoldFilters(ModContext mod)
    {
        lock (Gate) Holds.RemoveAll(h => ReferenceEquals(h.Mod, mod));
        PushHolds();
    }

    private static void PushHolds()
    {
        var t = AbilitiesNative.T;
        if (t == null) return;
        uint[] all;
        lock (Gate) all = Holds.SelectMany(h => h.Ids).Distinct().ToArray();
        fixed (uint* p = all) t->HoldFilter(p, (uint)all.Length);
    }
}
