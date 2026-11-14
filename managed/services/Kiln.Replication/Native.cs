// SPDX-License-Identifier: MIT
// The native service kiln.repl (native/core/abi/services/kiln_repl.h).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class ReplNative
{
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5;
    public const uint CapObserve = 1, CapClient = 2, CapLoopback = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Fake
    {
        public uint Machine, Slot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LoopbackStats
    {
        public uint Size, Fakes;
        public ulong Messages, Decoded, ComponentsEqual, ComponentsDiffer, CustomEqual, CustomDiffer, DecodeFaults, NoBaseline,
            Acked, Dropped, DestroyLists;
        public uint ActiveObjects, SlotMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<uint> Capabilities;
        public delegate* unmanaged[Cdecl]<ulong, byte, int> StatsEnable;
        public delegate* unmanaged[Cdecl]<void*, uint, uint*, int> PeerStats;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, void*, void*, ulong*, int> Watch;
        public delegate* unmanaged[Cdecl]<ulong, int> Remove;
        public delegate* unmanaged[Cdecl]<uint, void*, int> ComponentInfo;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, int> Capture;
        // lab (loopback harness)
        public delegate* unmanaged[Cdecl]<uint, Fake*, uint, uint*, int> LabAddFakes;
        public delegate* unmanaged[Cdecl]<uint, uint, int> LabForceRelevant;
        public delegate* unmanaged[Cdecl]<int> LabRemoveFakes;
        public delegate* unmanaged[Cdecl]<LoopbackStats*, int> LabStats;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, byte*, uint, uint*, uint*, int> LabDecoded;
    }

    private static Table* _table;
    private static bool _looked;

    private static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _table = Services.Get<Table>("kiln.repl", 1);
            _looked = true;
            return _table;
        }
    }

    /// <summary>The table is here (also in the test kit, when a fake publishes one).</summary>
    public static bool Available => T != null;

    /// <summary>KILN_REPL_CAP_* of this process; 0 without the table.</summary>
    public static uint Capabilities() => T != null ? T->Capabilities() : 0;

    public static int AddFakes(uint count, Span<Fake> fakes, out uint added)
    {
        added = 0;
        if (T == null) return Unavailable;
        uint n = 0;
        int rc;
        fixed (Fake* p = fakes) rc = T->LabAddFakes(count, p, (uint)fakes.Length, &n);
        added = n;
        return rc;
    }

    public static int ForceRelevant(uint machine, uint entity) => T == null ? Unavailable : T->LabForceRelevant(machine, entity);

    public static int RemoveFakes() => T == null ? Unavailable : T->LabRemoveFakes();

    public static int Stats(out LoopbackStats stats)
    {
        stats = default;
        stats.Size = (uint)sizeof(LoopbackStats);
        if (T == null) return Unavailable;
        fixed (LoopbackStats* p = &stats) return T->LabStats(p);
    }

    public static int Decoded(uint machine, uint entity, uint hash, out byte[] bytes, out uint tick)
    {
        bytes = [];
        tick = 0;
        if (T == null) return Unavailable;
        uint len = 0, t = 0;
        var rc = T->LabDecoded(machine, entity, hash, null, 0, &len, &t);
        if (rc != Ok) return rc;
        var buf = new byte[len];
        fixed (byte* p = buf) rc = T->LabDecoded(machine, entity, hash, p, len, &len, &t);
        if (rc != Ok) return rc;
        bytes = buf;
        tick = t;
        return Ok;
    }

    /// <summary>The engine's component hash: FNV-1a-32 of "keen::ecs::" + the short name (a name with "::" as it is),
    /// as kiln.world's component_hash.</summary>
    public static uint ComponentHash(string name)
    {
        var h = 2166136261u;
        if (!name.Contains("::", StringComparison.Ordinal))
            foreach (var c in "keen::ecs::") h = (h ^ (byte)c) * 16777619u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(name)) h = (h ^ b) * 16777619u;
        return h;
    }
}
