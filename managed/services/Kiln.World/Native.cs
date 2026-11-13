// SPDX-License-Identifier: MIT
// The native kiln.world table (native/core/abi/services/kiln_world.h). Field order and types must match the C header.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class WorldNative
{
    public const string Name = "kiln.world";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Transform
    {
        public Vec3 Position;
        public fixed float Rotation[4];
        public fixed float Scale[3];
        public float Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TeleportRequest
    {
        public uint Size;
        public uint Entity;
        public Vec3 Target;
        public float YawDeg;
        public uint SpawnPoint;
        public uint Flags;
        public uint Reserved;
        public delegate* unmanaged[Cdecl]<TeleportResult*, void*, void> Callback;
        public void* User;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TeleportResult
    {
        public uint Size;
        public uint Entity;
        public ulong Request;
        public uint Status;
        public uint Flags;
        public Vec3 Position;
        public Vec3 Target;
        public double Distance;
        public uint Steps;
        public uint Reserved;
        public NativeStr Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Programs, World, Teleport, Find;
        public uint KilnPrograms, EnginePrograms, EngineMax, TeleportsPending;
        public ulong WorldSteps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        // programs (native services only; kept as raw pointers here)
        public nint RegisterProgram, ProgramSlot, ProgramInfo;
        public nint IterBegin, IterNext, CurrentEntity, CommandBuffer, QueuePush, QueueAdd, QueueRemove, MarkWritten,
            ReadOther;
        // entities
        public delegate* unmanaged[Cdecl]<NativeStr, uint> ComponentHash;
        public delegate* unmanaged[Cdecl]<uint, uint, uint*, void*> Component;
        public delegate* unmanaged[Cdecl]<uint, byte> Exists;
        public delegate* unmanaged[Cdecl]<uint, Transform*, int> GetTransform;
        public delegate* unmanaged[Cdecl]<uint, Vec3*, int> GetPosition;
        public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> Find;
        public delegate* unmanaged[Cdecl]<uint> EntityCount;
        public delegate* unmanaged[Cdecl]<nuint> ServerWorld;
        // teleport
        public delegate* unmanaged[Cdecl]<ulong, TeleportRequest*, ulong*, int> Teleport;
        public delegate* unmanaged[Cdecl]<ulong, int> TeleportCancel;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
    }

    // KilnResult codes (kiln_api.h)
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;

    private static Table* _table;
    private static bool _looked;

    /// <summary>Forgets a remembered miss, so the next use looks for the table again (ServiceCaps.WhenFound: the refresh
    /// found it after all).</summary>
    internal static void LookAgain()
    {
        if (_table == null) _looked = false;
    }

    /// <summary>The table, or null when kiln.world is unavailable on this build.</summary>
    public static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _table = Services.Get<Table>(Name, Version);  // published at boot, before .NET starts: one look is enough
            _looked = true;
            return _table;
        }
    }

    /// <summary>Tests and the test kit: ask for the table again (a first ask can come before a fake table is published).</summary>
    internal static void Reset()
    {
        _table = null;
        _looked = false;
    }

    public static Table* Require(string api)
    {
        var t = T;
        if (t == null) throw new NotSupportedException($"{api}: kiln.world is unavailable on this server build");
        return t;
    }

    public static string Describe(int rc) => rc switch
    {
        Ok => "ok",
        InvalidArgument => "invalid argument",
        NotFound => "not found",
        Unavailable => "unavailable (feature off on this build, or no server world yet)",
        WrongThread => "wrong thread (game state is tick-thread only)",
        BufferTooSmall => "buffer too small",
        _ => $"error {rc}",
    };
}
