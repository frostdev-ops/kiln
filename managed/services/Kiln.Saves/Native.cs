// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_saves.h (kiln.saves v1). Sizes are checked by static_asserts in saves.cpp.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class SavesNative
{
    public const string Service = "kiln.saves";
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;

    public const uint EvSaving = 1, EvWritten = 2, EvFailed = 3, EvLoaded = 4;
    public const uint FlagSync = 1, FlagHasGuid = 2, FlagNewWorld = 4, FlagTimeout = 8;
    public const uint CanRequest = 1, CanWrite = 2, CanLoad = 4, CanReason = 8;
    public const uint ClientHosted = 16;  // KILN_SAVES_CLIENT_HOSTED: a game client's own world (Steam cloud / local slot)
    public const int PathSaveDir = 0, PathWorldFile = 1, PathData = 2, PathBackups = 3;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public uint Size, Kind;
        public ulong Seq;
        public uint Reason, Flags;
        public ulong Step;
        public long UnixMs;
        public uint Revision, Result;
        public ushort Slot, Reserved16;
        public uint WorldUnix;
        public fixed byte Guid[16];
        public double DurationMs;
        public ulong SaveSeq;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Features, State, LastReason;
        public fixed byte Guid[16];
        public uint HasGuid, Revision;
        public ushort Slot, PendingRequest;
        public uint WorldUnix;
        public long LastWrittenUnixMs, LastAcceptedUnixMs;
        public double LastDurationMs;
        public long AutosaveIntervalNs;
        public ulong Accepted, Refused, Written, Failed, LastSeq, Step;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint, int> Request;
        public delegate* unmanaged[Cdecl]<ulong, Event*, int> NextEvent;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<uint, void*, void>, void*, ulong*, int> SetPump;
        public delegate* unmanaged[Cdecl]<int, byte*, nuint, nuint*, int> Path;
        public delegate* unmanaged[Cdecl]<uint, int> SetPumpInterval;
    }

    private static Table* _table;
    private static bool _looked;

    public static Table* T
    {
        get
        {
            if (_looked) return _table;
            _table = Services.Get<Table>(Service, 1);
            _looked = true;
            return _table;
        }
    }

    public static Guid ToGuid(byte* b) => new(new ReadOnlySpan<byte>(b, 16));

    public static Status? ReadStatus()
    {
        var t = T;
        if (t == null) return null;
        var s = new Status { Size = (uint)sizeof(Status) };
        return t->GetStatus(&s) == Ok ? s : null;
    }

    public static string? GetPath(int kind)
    {
        var t = T;
        if (t == null) return null;
        nuint len = 0;
        var rc = t->Path(kind, null, 0, &len);
        if (rc != BufferTooSmall && rc != Ok) return null;
        var buf = new byte[(int)len];
        fixed (byte* p = buf) rc = t->Path(kind, p, len, &len);
        return rc == Ok ? System.Text.Encoding.UTF8.GetString(buf, 0, (int)len) : null;
    }
}
