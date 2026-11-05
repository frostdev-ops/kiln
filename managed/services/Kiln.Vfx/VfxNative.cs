// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_vfx.h (kiln.vfx v1). Layouts are checked by size at run time.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class VfxNative
{
    public const string Name = "kiln.vfx";
    public const uint Version = 1;

    public const int Ok = 0;

    // KilnVfxPlay.flags
    public const uint At = 1, On = 2, Verify = 4, TestFx = 8, TestCreate = 16, WaitGuid = 32;

    // KilnVfxStatus.flags
    public const uint HasGuard = 1, HasPlay = 2, Controller = 4, SystemReady = 8, Sim = 16;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(WorldPosition p) => (X, Y, Z) = (p.X, p.Y, p.Z);
        public readonly WorldPosition ToPosition() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Play
    {
        public uint Size, Flags;
        public fixed byte Vfx[16];
        public Vec3 Position;
        public fixed float Rotation[4];
        public float Scale;
        public uint Entity, Slot;
        public fixed float Local[3];
        public fixed float World[3];
        public fixed float OffsetRotation[4];
        public float VerifyRadius;
        public long LifetimeNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Size, State, Reason, Flags, FxHandle, Instance, Thread, Entity;
        public ulong QueuedMs, StartedMs, EndedMs;
        public Vec3 Position;
        public long LifetimeNs;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size, Flags, Registered, MapCapacity, Thread, Live;
        public ulong Played, Failed, Refused, Updates, Controller;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<ulong, Play*, uint> Play;
        public delegate* unmanaged[Cdecl]<uint, int, int> Stop;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, void*, nuint, int> SetParam;
        public delegate* unmanaged[Cdecl]<uint, uint> State;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> GetInfo;
        public delegate* unmanaged[Cdecl]<ulong, int, uint> StopOwner;
        public delegate* unmanaged[Cdecl]<byte*, int> IsRegistered;
        public delegate* unmanaged[Cdecl]<byte*, int> Preload;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> StatusJson;
        public delegate* unmanaged[Cdecl]<ulong, GameDataMirror.Str, byte*, nuint, nuint*, int> DefineJson;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> BuildStatus;
        public delegate* unmanaged[Cdecl]<uint, byte*, int> BindGuid;
    }

    private static Table* _table;
    private static int _looked;  // 0 not yet, 1 looked up

    /// <summary>The kiln.vfx table, or null (dedicated server, unsupported build, kiln-vfx.dll missing or switched off).</summary>
    public static Table* T
    {
        get
        {
            if (_table != null) return _table;
            if (Volatile.Read(ref _looked) != 0) return null;
            _table = Services.Get<Table>(Name, Version);
            Volatile.Write(ref _looked, 1);
            return _table;
        }
    }

    static VfxNative()
    {
        // fail loudly in development when the mirror drifts from the C header
        global::System.Diagnostics.Debug.Assert(sizeof(Play) == 128 && sizeof(Info) == 88 && sizeof(Status) == 64 && sizeof(Table) == 112);
    }

    public static void WriteGuid(Guid g, byte* dst)
    {
        Span<byte> b = stackalloc byte[16];
        g.TryWriteBytes(b);  // .NET order = the game's resource-id order
        for (var i = 0; i < 16; i++) dst[i] = b[i];
    }

    /// <summary>Sizes this build was compiled with, for the test mod: must equal the C header's 128, 88, 64, 112.</summary>
    public static (int Play, int Info, int Status, int Table) Sizes => (sizeof(Play), sizeof(Info), sizeof(Status), sizeof(Table));
}

/// <summary>Reads templates and sequences through kiln.gamedata's table. Read-only, and only the fields it needs. kiln.gamedata answers on
/// its world's thread and refuses others (<see cref="WrongThread"/>): the hosted server world's on a host, the client world's when joined.</summary>
internal static unsafe class GameDataMirror
{
    public const uint SectionTemplates = 1, SectionSequences = 3;  // Kiln.Content's GameDataSection

    [StructLayout(LayoutKind.Sequential)]
    public struct Str
    {
        public byte* Ptr;
        public nuint Len;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Entry  // 56 bytes
    {
        public uint Size, Section, Id, Flags;
        public Str Name;
        public ulong Address;
        public fixed byte Guid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, uint> Count;
        public delegate* unmanaged[Cdecl]<uint, uint, Entry*, int> EntryAt;
        public delegate* unmanaged[Cdecl]<uint, Str, Entry*, int> Find;
        public delegate* unmanaged[Cdecl]<uint, uint, Str, void*, void*, int> Get;
        public delegate* unmanaged[Cdecl]<uint, uint, Str, byte*, nuint, nuint*, int> GetJson;
    }

    private static Table* _t;

    public const int WrongThread = 4;

    public static bool Available => (_t != null) || ((_t = Services.Get<Table>("kiln.gamedata", 1)) != null);

    /// <summary>The JSON of <paramref name="path"/> in entry <paramref name="key"/>, null when there is no such entry or path;
    /// <paramref name="rc"/> says which, or <see cref="WrongThread"/>.</summary>
    public static string? Read(uint section, string key, string path, out int rc)
    {
        rc = 0;
        if (!Available) return null;
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var pathBytes = System.Text.Encoding.UTF8.GetBytes(path);
        fixed (byte* k = keyBytes, p = pathBytes)
        {
            Entry e = default;
            e.Size = (uint)sizeof(Entry);
            rc = _t->Find(section, new Str { Ptr = k, Len = (nuint)keyBytes.Length }, &e);
            if (rc != 0) return null;
            var buf = new byte[16384];
            nuint n = 0;
            fixed (byte* b = buf)
            {
                rc = _t->GetJson(section, e.Id, new Str { Ptr = p, Len = (nuint)pathBytes.Length }, b, (nuint)buf.Length, &n);
                return rc == 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, (int)n) : null;
            }
        }
    }
}
