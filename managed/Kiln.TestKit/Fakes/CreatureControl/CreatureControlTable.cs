// SPDX-License-Identifier: MIT
// The fake kiln.creature_control v1 table (native/core/abi/services/kiln_creature_control.h) under the real
// Kiln.CreatureControl. Its functions answer from the running harness's CreatureControlModel, and as the native service
// does when it is unavailable otherwise.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_creature_control.h</c> (v1). Sizes are pinned by the kit's tests to the header's.</summary>
internal static unsafe class CreatureControlAbi
{
    public const string Service = "kiln.creature_control";
    public const string Operational = "creature_control";
    public const uint Version = 1;

    public const uint Pending = 0, Active = 1, Preempted = 2, Released = 3, Failed = 4;
    public const uint ResultNone = 0, Granted = 1, ResultReleased = 2, Canceled = 4, Busy = 5, TimedOut = 6, BodyGone = 7,
        WorldEnded = 8, CapabilityOff = 9, ExpiredHandle = 13;
    public const uint RoleNone = 0, PetFollow = 1, Ridden = 3, Quarantine = 4, PetStay = 6, EngineGuard = 7;
    public const uint ChangedSteering = 1, ChangedAiLayer = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct BodyKey
    {
        public ulong WorldEpoch, BodyGeneration;
        public uint Entity, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Request
    {
        public uint Size, Role;
        public BodyKey Body;
        public ulong RequestId;
        public uint Flags, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint Size, LeaseState, Result, RequestedRole, EffectiveRole, ChangedMask;
        public BodyKey Body;
        public ulong Lease, WinningLease, RequestId, TransitionSequence, NoticeSequence;
        public fixed byte Reason[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, BodyKey*, int> BindBody;
        public delegate* unmanaged[Cdecl]<ulong, Request*, State*, int> AcquireControl;
        public delegate* unmanaged[Cdecl]<ulong, ulong, State*, int> QueryControl;
        public delegate* unmanaged[Cdecl]<ulong, ulong, BodyKey, ulong, uint*, int> CanDrive;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> ReleaseControl;
        public delegate* unmanaged[Cdecl]<ulong, State*, uint, uint*, uint*, int> Notices;
        public delegate* unmanaged[Cdecl]<BodyKey, uint, int> Quarantine;
        public delegate* unmanaged[Cdecl]<ulong, BodyKey, int> BodyRetired;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint> RootPolicy;
        public delegate* unmanaged[Cdecl]<ulong, ulong*, int> NextRequest;
        public delegate* unmanaged[Cdecl]<ulong, BodyKey, int> ReleaseBody;
    }

    public static void SetReason(State* s, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var n = Math.Min(bytes.Length, 159);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80) n--;
        for (var i = 0; i < 160; i++) s->Reason[i] = i < n ? bytes[i] : (byte)0;
    }
}

/// <summary>The C# functions behind the fake <c>kiln.creature_control</c> table. Modelled: every function of v1 but
/// quarantine and body_retired (unavailable, as in this build's native service) and root_policy (always 0).</summary>
internal static unsafe class CreatureControlTable
{
    private static CreatureControlAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (CreatureControlAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(CreatureControlAbi.Table));  // process lifetime
        t->Header = new ServiceHeader { Size = (uint)sizeof(CreatureControlAbi.Table), Version = CreatureControlAbi.Version };
        t->BindBody = &BindBody;
        t->AcquireControl = &AcquireControl;
        t->QueryControl = &QueryControl;
        t->CanDrive = &CanDrive;
        t->ReleaseControl = &ReleaseControl;
        t->Notices = &Notices;
        t->Quarantine = &Quarantine;
        t->BodyRetired = &BodyRetired;
        t->RootPolicy = &RootPolicy;
        t->NextRequest = &NextRequest;
        t->ReleaseBody = &ReleaseBody;
        _table = t;
        return (nint)t;
    }

    // the model of the harness that runs now; null: answer as the unavailable service
    private static CreatureControlModel? Model => FakeTables.Active(CreatureControlAbi.Service) is { } h ? CreatureControlFake.ModelOf(h) : null;

    private static int Guard(Func<CreatureControlModel, int> body, bool tick = true)
    {
        try
        {
            if (Model is not { } m) return FakeTables.Unavailable;
            if (tick && !FakeTables.OnTickThread) return FakeTables.WrongThread;
            return body(m);
        }
        catch
        {
            return FakeTables.Internal;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BindBody(ulong owner, uint entity, uint kind, uint retire, CreatureControlAbi.BodyKey* out_) =>
        Guard(m => m.Bind(owner, entity, kind, retire, out_));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int AcquireControl(ulong owner, CreatureControlAbi.Request* request, CreatureControlAbi.State* out_) =>
        Guard(m => m.Acquire(owner, request, out_));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int QueryControl(ulong owner, ulong lease, CreatureControlAbi.State* out_) =>
        Guard(m => m.Query(lease, out_), tick: false);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CanDrive(ulong owner, ulong lease, CreatureControlAbi.BodyKey key, ulong seq, uint* out_)
    {
        if (out_ == null) return FakeTables.InvalidArgument;
        *out_ = 0;
        return Guard(m =>
        {
            *out_ = m.CanDrive(owner, lease, key, seq) ? 1u : 0u;
            return FakeTables.Ok;
        }, tick: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReleaseControl(ulong owner, ulong lease) => Guard(m => m.Release(owner, lease));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Notices(ulong owner, CreatureControlAbi.State* out_, uint capacity, uint* written, uint* resync) =>
        Guard(m => m.Notices(owner, out_, capacity, written, resync));

    // S6 (quarantine, retirement, the deny table) is not modelled: the native service of this build does not serve it
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Quarantine(CreatureControlAbi.BodyKey key, uint cause) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BodyRetired(ulong owner, CreatureControlAbi.BodyKey key) =>
        FakeTables.OnTickThread ? FakeTables.Unavailable : FakeTables.WrongThread;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint RootPolicy(ulong epoch, uint root) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NextRequest(ulong owner, ulong* out_) => Guard(m => m.NextRequest(owner, out_));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReleaseBody(ulong owner, CreatureControlAbi.BodyKey key) => Guard(m => m.ReleaseBody(owner, key));
}
