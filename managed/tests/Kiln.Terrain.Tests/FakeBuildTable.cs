// SPDX-License-Identifier: MIT
// A fake kiln.build native table (native/core/abi/services/kiln_build.h) for this test process, so Kiln.Build's own
// managed code runs offline: Build.Read, Build.Write and Build.WriteIfUnchanged with their real BuildEditingEvent and
// BuildEditEvent. The test kit has no fake of kiln.build; this one models only ready, read and write_cells on flat ground
// (stone below y 100, air above) and is published under the kit's backend the first time a test asks for it.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;

namespace TerrainTests;

internal static unsafe class FakeBuildTable
{
    public const int GroundY = 100;

    private const int Ok = 0, Unavailable = 3;
    private const uint RChanged = 1u << 0, RVerified = 1u << 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public uint Size, Version;
        public delegate* unmanaged[Cdecl]<void*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<void*, int> GetGrid;
        public delegate* unmanaged[Cdecl]<int*, uint*, int> Ready;
        public delegate* unmanaged[Cdecl]<int*, uint*, ushort*, nuint, int> Read;
        public delegate* unmanaged[Cdecl]<WriteArgs*, ResultArgs*, int> WriteCells;
        // shape_box, shape, prop_place, prop_remove, props_in_box, prop_info, bases, item_material, item_template, keep,
        // release: never reached by these tests
        public fixed ulong Rest[11];
    }

    // KilnBuildWrite (72 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct WriteArgs
    {
        public uint Size, Flags;
        public fixed int Origin[3];
        public fixed uint Dims[3];
        public ushort* Cells;
        public byte* Mask;
        public uint Actor, Reserved;
        public byte* OriginMod;
        public nuint OriginModLength;
    }

    // KilnBuildResult (56 bytes)
    [StructLayout(LayoutKind.Sequential)]
    private struct ResultArgs
    {
        public uint Size, Flags;
        public ulong CellsChanged;
        public uint BlocksWritten, Zone;
        public fixed int BoxOrigin[3];
        public fixed uint BoxDims[3];
        public double Milliseconds;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<(int, int, int), ushort> Cells = new();
    private static bool _loaded = true;
    private static int _commits;
    private static Table* _table;

    /// <summary>Publishes the table for this process (once). Kiln.Build keeps the first table pointer it was given, so
    /// this must run before anything in the process asked for kiln.build: these tests are the only ones that do.</summary>
    public static void Use()
    {
        lock (Lock)
        {
            if (_table != null) return;
            Assert(sizeof(WriteArgs) == 72 && sizeof(ResultArgs) == 56, "the mirrors of KilnBuildWrite and KilnBuildResult");
            var t = (Table*)NativeMemory.AllocZeroed((nuint)sizeof(Table));
            t->Size = (uint)sizeof(Table);
            t->Version = 1;
            t->GetStatus = &Unavailable1;
            t->GetGrid = &Unavailable1;
            t->Ready = &ReadyFn;
            t->Read = &ReadFn;
            t->WriteCells = &WriteFn;
            var kit = typeof(Kiln.TestKit.KilnHarness).Assembly.GetType("Kiln.TestKit.KitBackend", throwOnError: true)!;
            var backend = kit.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            kit.GetMethod("RegisterTable", BindingFlags.Public | BindingFlags.Instance)!.Invoke(backend, ["kiln.build", (nint)t]);
            _table = t;
        }
    }

    /// <summary>Flat ground again, tiles loaded, no commits counted.</summary>
    public static void Reset()
    {
        lock (Lock)
        {
            Cells.Clear();
            _loaded = true;
            _commits = 0;
        }
    }

    /// <summary>Whether the tiles are there; false: ready and read answer "unavailable".</summary>
    public static bool Loaded
    {
        get { lock (Lock) return _loaded; }
        set { lock (Lock) _loaded = value; }
    }

    /// <summary>Calls of write_cells that changed a cell.</summary>
    public static int Commits { get { lock (Lock) return _commits; } }

    public static Voxel Get(CellPos at) { lock (Lock) return new Voxel(At(at.X, at.Y, at.Z)); }

    private static ushort At(int x, int y, int z) => Cells.TryGetValue((x, y, z), out var v) ? v : y < GroundY ? Voxel.Of(1).Raw : (ushort)0;

    private static void Assert(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException($"FakeBuildTable: {what} do not match kiln_build.h");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Unavailable1(void* _) => Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadyFn(int* origin, uint* dims)
    {
        lock (Lock) return _loaded ? Ok : Unavailable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadFn(int* origin, uint* dims, ushort* cells, nuint count)
    {
        lock (Lock)
        {
            if (!_loaded) return Unavailable;
            var box = new CellBox(origin[0], origin[1], origin[2], (int)dims[0], (int)dims[1], (int)dims[2]);
            for (var z = 0; z < box.SizeZ; z++)
            for (var y = 0; y < box.SizeY; y++)
            for (var x = 0; x < box.SizeX; x++)
                cells[box.Index(x, y, z)] = At(box.X + x, box.Y + y, box.Z + z);
            return Ok;
        }
    }

    // write_cells: the masked cells of the box (every cell without a mask); flags CHANGED and VERIFIED as the native
    // writer sets them. Bases, zones and the dry run are not modelled.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int WriteFn(WriteArgs* w, ResultArgs* r)
    {
        lock (Lock)
        {
            if (!_loaded)
            {
                r->Flags = 1u << 4; // KILN_BUILD_R_NOT_LOADED
                return Unavailable;
            }
            var box = new CellBox(w->Origin[0], w->Origin[1], w->Origin[2], (int)w->Dims[0], (int)w->Dims[1], (int)w->Dims[2]);
            ulong changed = 0;
            for (var z = 0; z < box.SizeZ; z++)
            for (var y = 0; y < box.SizeY; y++)
            for (var x = 0; x < box.SizeX; x++)
            {
                var i = box.Index(x, y, z);
                if (w->Mask != null && w->Mask[i] == 0) continue;
                var at = (box.X + x, box.Y + y, box.Z + z);
                if (At(at.Item1, at.Item2, at.Item3) == w->Cells[i]) continue;
                Cells[at] = w->Cells[i];
                changed++;
            }
            if (changed > 0) _commits++;
            r->Flags = changed > 0 ? RChanged | RVerified : RVerified;
            r->CellsChanged = changed;
            return Ok;
        }
    }
}
