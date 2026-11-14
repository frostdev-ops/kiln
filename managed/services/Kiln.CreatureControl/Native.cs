// SPDX-License-Identifier: MIT
// The native table of kiln.creature_control v1 (native/core/abi/services/kiln_creature_control.h), mirrored field by
// field. Sizes: body key 24, request 48, state 248 bytes (pinned by Kiln.CreatureControl.Tests).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class CreatureControlNative
{
    public const string Service = "kiln.creature_control";
    public const uint Version = 1;

    // KilnResult
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;

    public const uint BaselineEnemy = 0;

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

    private static Table* _table;

    /// <summary>The table, or null when kiln.creature_control is not here (its row says why). A missing table is asked
    /// for again at the next use, so one published later is found.</summary>
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>(Service, Version));

    public static string ReasonOf(in State s)
    {
        fixed (byte* p = s.Reason)
        {
            var n = 0;
            while (n < 160 && p[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(p, n);
        }
    }
}
