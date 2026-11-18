// SPDX-License-Identifier: MIT
// Records of kiln.world's compare-exchange writes (kiln_world.h "Compare-exchange writes"), used by WorldConcurrent. Field
// order matches native/services/world/tests/abi.json (KilnWorldApiEcsWrites, KilnEcsWritePolicy, KilnMutationReply,
// KilnEcsWriteEvent, KilnEcsWriteConstants), which the native host test checks against the header and Kiln.World.Tests
// against these mirrors. A caller uses the members only when kiln.world's header.size covers them (616 bytes).
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>kiln.world's table as far as the compare-exchange writes (kiln_world.h v4, 616 bytes): the reads' part, then
/// the four write members at 584..608.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EcsWritesTable
{
    public EcsReadsTable Reads;  // 0..584
    public delegate* unmanaged[Cdecl]<uint, uint, EcsWritePolicyNative*, int> WritePolicy;
    public delegate* unmanaged[Cdecl]<ulong, EcsObjectRefNative*, uint, void*, void*, void*, uint, void*, MutationReplyNative*, int> CompareExchange;
    public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<EcsWriteEventNative*, void*, void>, void*, ulong*, int> WriteSubscribe;
    public delegate* unmanaged[Cdecl]<ulong, int> WriteUnsubscribe;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct EcsWritePolicyNative
{
    public uint Size;
    public uint Status;
    public uint WorldKind;
    public uint Component;
    public uint ComponentSize;
    public uint Budget;
    public uint Flags;
    public uint WhyCode;
    public fixed byte Why[96];
}

[StructLayout(LayoutKind.Sequential)]
internal struct MutationReplyNative
{
    public uint Size;
    public uint Status;
    public uint Flags;
    public uint WhyCode;
    public uint EngineReason;
    public uint Generation;
    public ulong Session;
    public ulong Step;
    public ulong HoldNs;
}

[StructLayout(LayoutKind.Sequential)]
internal struct EcsWriteEventNative
{
    public uint Size;
    public uint WorldKind;
    public uint Entity;
    public uint Generation;
    public uint Component;
    public uint Reserved;
    public ulong Owner;
    public ulong Session;
    public ulong Step;
}

// kiln_world.h's compare-exchange statuses, result codes and flags (abi.json "KilnEcsWriteConstants", checked by
// Kiln.World.Tests). The why codes are WriteReason's values.
internal static class EcsWriteConstants
{
    public const uint Conflict = 7, NotWritable = 8, Uncertain = 9;
    public const int ErrConflict = 105, ErrNotWritable = 106, ErrUncertain = 107;
    public const uint CapEcsWrite = 1u << 5;  // KILN_WORLD_CAP_ECS_WRITE
    public const uint PolicyWritable = 1u << 0, PolicyTrackingKnown = 1u << 1, PolicyTrackedA = 1u << 2, PolicyTrackedB = 1u << 3,
        PolicyPlain = 1u << 4, PolicyOverBudget = 1u << 5;
    public const uint MutationWritten = 1u << 0, MutationNoOp = 1u << 1, MutationConfirmed = 1u << 2, MutationRolledBack = 1u << 3,
        MutationTargetLatched = 1u << 4, MutationEventQueued = 1u << 5;
}
