// SPDX-License-Identifier: MIT
// Records of kiln.world's verified-copy reads (kiln_world.h), used by WorldConcurrent. Field order matches
// native/services/world/tests/abi.json, which the native host test checks against the header. A caller uses a member
// only when kiln.world's header.size covers it (the table is 584 bytes with these members, the first at offset 528).
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>kiln.world's table as far as the ECS reads (kiln_world.h v4, 584 bytes): the v3 part, which holds
/// <c>capabilities</c>, the rest of v4 up to offset 528, then the seven read members.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EcsReadsTable
{
    public WorldWritesTable V3;    // 0..280
    public fixed long V4[31];       // 280..528: game programs, foreign views, programs in a client's worlds
    public delegate* unmanaged[Cdecl]<ulong, ulong, uint, ulong, uint, EcsObjectRefNative*, int> EntityOpen;
    public delegate* unmanaged[Cdecl]<ulong, EcsObjectRefNative*, EcsObjectInfoNative*, int> EntityValidate;
    public delegate* unmanaged[Cdecl]<EcsContextInfoNative*, int> ContextSnapshot;
    public delegate* unmanaged[Cdecl]<ulong, EcsQueryRequestNative*, EcsObjectRefNative*, uint, uint*, uint*, int> Query;
    public delegate* unmanaged[Cdecl]<ulong, EcsObjectRefNative*, uint, void*, uint, uint*, int> Snapshot;
    public delegate* unmanaged[Cdecl]<ulong, EcsObjectRefNative*, EcsSnapshotSpecNative*, uint, void*, uint, uint*, uint*, int> SnapshotMany;
    public delegate* unmanaged[Cdecl]<ulong, EcsObjectRefNative*, byte*, int> TemplateUuid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EcsObjectRefNative
{
    public uint Size;
    public uint Entity;
    public uint Generation;
    public uint WorldKind;
    public ulong Session;
    public ulong LayoutStamp;
    public ulong ReloadEpoch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EcsObjectInfoNative
{
    public uint Size;
    public uint Status;
    public uint Entity;
    public uint Generation;
    public uint WorldKind;
    public uint Flags;
    public ulong Session;
    public ulong LayoutStamp;
    public ulong ReloadEpoch;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EcsContextInfoNative
{
    public uint Size;
    public uint Status;
    public uint PresentMask;
    public uint ExactProfile;
    public fixed ulong Sessions[3];
    public ulong LayoutStamp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EcsQueryRequestNative
{
    public uint Size;
    public uint WorldKind;
    public uint Flags;
    public uint ComponentCount;
    public nint Components;
    public WorldNative.Vec3 Center;
    public double Radius;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EcsSnapshotSpecNative
{
    public uint Component;
    public uint Flags; // EcsReadConstants.ReadFrameQuiet
}

// kiln_world.h's ECS-read statuses, result codes and flags (abi.json "KilnEcsReadConstants", checked by Kiln.World.Tests).
internal static class EcsReadConstants
{
    public const uint Ok = 0, Absent = 1, Stale = 2, Unreadable = 3, Unavailable = 4, Busy = 5, Unsupported = 6;
    public const int ErrChanged = 100, ErrStale = 101, ErrUnreadable = 102, ErrBusy = 103, ErrUnsupported = 104;
    public const uint QueryBounds = 1u << 0, QueryFrameQuiet = 1u << 1; // KilnEcsQueryRequest.flags
    public const uint ReadFrameQuiet = 1u << 0;                         // KilnEcsSnapshotSpec.flags
    public const uint ObjectAlive = 1u << 0, ObjectHasTemplate = 1u << 1;
    public const uint CapEcsRead = 1u << 4;                             // KILN_WORLD_CAP_ECS_READ
}
