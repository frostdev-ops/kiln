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
    public uint Reserved;
}
