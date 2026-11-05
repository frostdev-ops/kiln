// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_build.h (kiln.build v1). Layouts are checked by size at first use.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class BuildNative
{
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5,
        Revoked = 6, Internal = 7;

    // KILN_BUILD_F_*
    public const uint FRespectZones = 1u << 0, FInBases = 1u << 1, FAllowAltars = 1u << 2, FUnloaded = 1u << 3,
        FDryRun = 1u << 4, FTransient = 1u << 5;

    // KILN_BUILD_R_*
    public const uint RChanged = 1u << 0, RVerified = 1u << 1, RZone = 1u << 2, RBase = 1u << 3, RNotLoaded = 1u << 4,
        RDryRun = 1u << 5, RKept = 1u << 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(WorldPosition p) { X = p.X; Y = p.Y; Z = p.Z; }
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
        public WorldPosition ToPosition() => new(X, Y, Z);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GuidBytes
    {
        public fixed byte B[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Grid
    {
        public uint Size, Level;
        public double CellSize;
        public Vec3 Origin;
        public fixed int CellsMin[3];
        public fixed int CellsMax[3];
        public fixed uint TileCells[2];
        public fixed uint Tiles[2];
        public uint Block, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Result
    {
        public uint Size, Flags;
        public ulong CellsChanged;
        public uint BlocksWritten, Zone;
        public fixed int BoxOrigin[3];
        public fixed uint BoxDims[3];
        public double Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Write
    {
        public uint Size, Flags;
        public fixed int Origin[3];
        public fixed uint Dims[3];
        public ushort* Cells;
        public byte* Mask;
        public uint Actor, Reserved;
        public NativeStr OriginMod;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Shape
    {
        public uint Size, Flags, Op, Kind;
        public Vec3 Center, Half;
        public double LevelY;
        public float Strength;
        public uint Passes;
        public byte Material, FromMaterial, Reserved0, Reserved1;
        public uint Actor;
        public NativeStr OriginMod;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Prop
    {
        public uint Size, Flags;
        public GuidBytes Template;
        public NativeStr TemplateName;
        public Vec3 Position;
        public fixed float Rotation[4];
        public uint ItemId, Actor;
        public NativeStr OriginMod;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropInfo
    {
        public uint Size, Entity;
        public GuidBytes Template;
        public Vec3 Position;
        public fixed float Rotation[4];
        public fixed float Scale[3];
        public uint ItemId, Kind, Flags;
        public fixed byte Name[128];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Base
    {
        public uint Size, Altar;
        public Vec3 Center, Half;
        public uint Abandoned, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Read, Write, Props, World;
        public ulong Reads, Writes, CellsRead, CellsWritten, BlocksWritten, PropsPlaced, PropsRemoved, Refused;
        public fixed byte Detail[160];
        public byte Persist;
        public fixed byte Reserved[7];
        public ulong KeptChunks, SavesSeen, BoxesAdded;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<Grid*, int> GetGrid;
        public delegate* unmanaged[Cdecl]<int*, uint*, int> Ready;
        public delegate* unmanaged[Cdecl]<int*, uint*, ushort*, nuint, int> Read;
        public delegate* unmanaged[Cdecl]<Write*, Result*, int> WriteCells;
        public delegate* unmanaged[Cdecl]<Shape*, int*, uint*, int> ShapeBox;
        public delegate* unmanaged[Cdecl]<Shape*, Result*, int> RunShape;
        public delegate* unmanaged[Cdecl]<Prop*, uint*, int> PropPlace;
        public delegate* unmanaged[Cdecl]<uint, int> PropRemove;
        public delegate* unmanaged[Cdecl]<Vec3, Vec3, uint, PropInfo*, nuint, nuint*, int> PropsInBox;
        public delegate* unmanaged[Cdecl]<uint, PropInfo*, int> PropInfoOf;
        public delegate* unmanaged[Cdecl]<Base*, nuint, nuint*, int> Bases;
        public delegate* unmanaged[Cdecl]<uint, uint> ItemMaterial;
        public delegate* unmanaged[Cdecl]<uint, GuidBytes*, int> ItemTemplate;
        public delegate* unmanaged[Cdecl]<int*, uint*, int> Keep;
        public delegate* unmanaged[Cdecl]<int*, uint*, ulong*, int> Release;
    }

    private static Table* _table;
    private static bool _checked;

    public static Table* T
    {
        get
        {
            if (_table != null) return _table;
            if (_checked) return null;
            _checked = true;
            if (sizeof(Grid) != 88 || sizeof(Result) != 56 || sizeof(Write) != 72 || sizeof(Shape) != 104 ||
                sizeof(Prop) != 104 || sizeof(PropInfo) != 216 || sizeof(Base) != 64 || sizeof(Status) != 264)
                throw new InvalidOperationException("Kiln.Build: native layout mismatch (kiln_build.h changed)");
            _table = Services.Get<Table>("kiln.build", 1);
            return _table;
        }
    }

    public static string Utf8Z(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    public static Guid ReadGuid(GuidBytes* g)
    {
        Span<byte> b = stackalloc byte[16];
        for (var i = 0; i < 16; i++) b[i] = g->B[i];
        return new Guid(b);
    }

    public static void WriteGuid(Guid id, GuidBytes* g)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        for (var i = 0; i < 16; i++) g->B[i] = b[i];
    }
}
