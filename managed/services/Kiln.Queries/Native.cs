// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_queries.h (kiln.queries v1).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class QueriesNative
{
    public const string Service = "kiln.queries";
    public const uint Version = 1;
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4;
    public const uint Sphere = 0, Box = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(WorldPosition p) { X = p.X; Y = p.Y; Z = p.Z; }
        public readonly WorldPosition Position => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RayRequest
    {
        public uint Size, Realm;
        public Vec3 From, To;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RayResult
    {
        public uint Size, Ok;
        public NativeStr Reason;
        public uint Hit, Material;
        public ushort Voxel, Reserved;
        public Vec3 Position;
        public double Distance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EntityRequest
    {
        public uint Size, Shape;
        public Vec3 Center, HalfExtents;
        public double Radius;
        public uint Exclude, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EntityResult
    {
        public uint Size, Ok;
        public NativeStr Reason;
        public uint Written, Total;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathRequest
    {
        public uint Size, Anchor;
        public Vec3 From, To;
        public double HorizontalSpace, VerticalSpace;
        public uint MaxIterations, ClosestFallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathAdmission
    {
        public uint Size, Ok;
        public ulong Request;
        public uint Status, Reserved;
        public NativeStr Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathUpdate
    {
        public uint Size, Before, After, Status;
        public ulong Request, Sequence;
        public uint Generation, Anchor;
        public uint Terminal, Ok, GameResult, ReachesTarget;
        public uint PointCount, Steps;
        public double Length;
        public NativeStr Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PathPoint
    {
        public Vec3 Position;
        public uint RawMove, Reserved;
    }

    public const int BufferTooSmall = 5, Revoked = 6;
    public const uint NoGameResult = 0xffffffff;
    public const int MaxPathPoints = 4096;

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<RayRequest*, RayResult*, int> RayVoxels;
        public delegate* unmanaged[Cdecl]<EntityRequest*, uint*, uint, EntityResult*, int> Entities;
        public delegate* unmanaged[Cdecl]<ulong, PathRequest*, PathAdmission*, int> PathSubmit;
        public delegate* unmanaged[Cdecl]<ulong, ulong, int> PathCancel;
        public delegate* unmanaged[Cdecl]<ulong, PathUpdate*, PathPoint*, uint, uint*, int> PathPoll;
        public delegate* unmanaged[Cdecl]<ulong, int> PathRetire;
    }

    private static Table* _table;

    /// <summary>The service's table, or null. Looked up again while it is missing.</summary>
    public static Table* T => _table != null ? _table : (_table = Services.Get<Table>(Service, Version));

    /// <summary>For the test kit's next harness: look the table up again.</summary>
    internal static void Forget() => _table = null;
}
