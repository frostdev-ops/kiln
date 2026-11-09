// SPDX-License-Identifier: MIT
// The managed half's bridge to kiln.pacing (native/services/pacing; the C table is native/core/abi/services/kiln_pacing.h).
// Kiln.Host calls Attach once before any mod loads. After that the registry in Pacing.cs drives the native half: a hook
// goes into the game only for a place some mod registered a handler for, and the native hooks call OnLoopWait and
// OnServerWait below from the game's own loops, on whichever thread waits.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class PacingNative
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint> Sites;
        public delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<NativeLoopWait*, void*, void>,
            delegate* unmanaged[Cdecl]<ulong*, void*, void>, void*, int> SetReceiver;
        public delegate* unmanaged[Cdecl]<uint, uint> Arm;
        public delegate* unmanaged[Cdecl]<uint, uint, int> CapTaskWorkers;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLoopWait
    {
        public uint Size, Kind, Ms, Reserved;
        public long LeftNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        public uint Size, Found, Armed, Hooked, Refused, DefaultCap, BackgroundCap;
        public byte TaskSystemStarted;
        public fixed byte Reserved[3];
        public ulong LoopCalls, ServerCalls;
    }

    /// <summary>The mirrors have the sizes native/services/pacing checks (kiln_pacing.h).</summary>
    internal static bool LayoutMatchesNative => sizeof(NativeLoopWait) == 24 && sizeof(NativeStatus) == 48 && sizeof(Table) == 48;

    private const uint KindMessages = 1;
    private const PacingSites Armable = PacingSites.LoopWait | PacingSites.MessageWait | PacingSites.ServerWait;

    private static Table* _t;
    private static readonly object SyncLock = new();

    /// <summary>Finds the native half and hands it the receivers. Quiet when the build has none (no Sites, nothing runs).</summary>
    internal static void Attach()
    {
        var t = Services.Get<Table>("kiln.pacing", 1);
        if (t == null) return;
        var found = (PacingSites)t->Sites();
        if (found == PacingSites.None) return;
        if (t->SetReceiver(&OnLoopWait, &OnServerWait, null) != 0)
        {
            Backend.Current.Log(0, LogLevel.Warn, "kiln.pacing: the native receiver could not attach; pacing is unavailable");
            return;
        }
        _t = t;
        Pacing.TasksStartedProbe = TasksStarted;
        Pacing.Attach(found);
        Pacing.Changed += Sync;
        Backend.Current.Log(0, LogLevel.Info, $"kiln.pacing: places this build lets mods pace: {found}");
    }

    /// <summary>The native half's counters (diagnostics and the lab test): waits delivered to handlers, and what is armed.</summary>
    internal static (ulong LoopCalls, ulong ServerCalls, PacingSites Armed, PacingSites Hooked, bool TasksStarted, uint DefaultCap, uint BackgroundCap) Status()
    {
        var t = _t;
        if (t == null) return default;
        NativeStatus s = default;
        s.Size = (uint)sizeof(NativeStatus);
        if (t->Status(&s) != 0) return default;
        return (s.LoopCalls, s.ServerCalls, (PacingSites)s.Armed, (PacingSites)s.Hooked, s.TaskSystemStarted != 0, s.DefaultCap, s.BackgroundCap);
    }

    private static bool TasksStarted() => Status().TasksStarted;

    // The registry changed: arm what the handlers are for, nothing else. The first request for a place puts its hook in the
    // game (the tick thread: that is where handlers are added); a handler that fails and is removed on a game thread only
    // lowers the request, which flips a flag. Under one lock so the last call always reflects the latest registry.
    private static void Sync()
    {
        var t = _t;
        if (t == null) return;
        try
        {
            lock (SyncLock)
            {
                var wanted = Pacing.Wanted & Pacing.Sites & Armable;
                var armed = (PacingSites)t->Arm((uint)wanted);
                var lost = wanted & ~armed;
                if (lost != PacingSites.None)
                {
                    Pacing.Narrow(lost);
                    Backend.Current.Log(0, LogLevel.Warn, $"kiln.pacing: the game's code would not take the hook for {lost}; handlers for it are never called");
                }
                var caps = Pacing.EffectiveTaskWorkerCaps;
                if (!caps.IsEmpty && (Pacing.Sites & PacingSites.TaskWorkers) != 0)
                    t->CapTaskWorkers((uint)Math.Max(caps.Default, 0), (uint)Math.Max(caps.Background, 0));
            }
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"kiln.pacing: could not arm the game's waits: {e}");
        }
    }

    // Called from the game's loops (native hooks), on the thread that waits. Nothing may unwind into them.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLoopWait(NativeLoopWait* w, void* user)
    {
        try
        {
            var wait = new LoopWait(w->Ms > int.MaxValue ? int.MaxValue : (int)w->Ms, w->LeftNs,
                w->Kind == KindMessages ? LoopWaitKind.Messages : LoopWaitKind.Handles);
            Pacing.DispatchLoopWait(in wait);
        }
        catch (Exception)
        {
            // Pacing.Call already logs and counts a handler's failure; this is for what it cannot foresee.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServerWait(ulong* waitNs, void* user)
    {
        try
        {
            var wait = (long)Math.Min(*waitNs, (ulong)long.MaxValue);
            Pacing.DispatchServerWait(ref wait);
            *waitNs = (ulong)wait;
        }
        catch (Exception)
        {
            // as above
        }
    }
}
