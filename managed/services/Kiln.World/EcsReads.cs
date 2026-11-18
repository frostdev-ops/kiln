// SPDX-License-Identifier: MIT
// Records of kiln.world's verified-copy reads (kiln_world.h). Layout only: no C# call uses them yet. Field order matches native/services/world/tests/abi.json, which the native host test checks
// against the header. A caller uses a member only when kiln.world's header.size covers it (the table is 584 bytes
// with these members, the first at offset 528).
using System.Runtime.InteropServices;

namespace Kiln;

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
