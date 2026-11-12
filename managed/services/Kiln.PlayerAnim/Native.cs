// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_playeranim.h (kiln.playeranim v1). Blittable only; the field order is the ABI.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class PlayerAnimNative
{
    public const string Service = "kiln.playeranim";
    public const uint Version = 1;

    // KilnResult
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;

    /// <summary>KilnPaGameState: 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct GameState
    {
        public uint Size;      // set by the caller
        public uint Kind;      // KILN_PA_KIND_*
        public uint Id;        // the first of IdCount; 0 when IdCount is 0
        public uint IdCount;
        public NativeStr Machine, Name;
    }

    /// <summary>KilnPaState: 112 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct State
    {
        public uint Size;      // set by the caller
        public uint Status;    // KILN_PA_LIVE ...
        public uint Kind;
        public uint Id;
        public fixed byte Guid[16];  // game byte order (= Guid.ToByteArray())
        public NativeStr Mod, Name, Like, Machine, Reason;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<int> State;
        public delegate* unmanaged[Cdecl]<NativeStr> GraphHash;
        public delegate* unmanaged[Cdecl]<NativeStr, GameState*, uint*, int> GameState;
        public delegate* unmanaged[Cdecl]<NativeStr, uint*, nuint, nuint*, int> GameStateIds;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, State*, int> ModState;
        public delegate* unmanaged[Cdecl]<uint> StateCount;
        public delegate* unmanaged[Cdecl]<uint, State*, int> StateAt;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> DefineJson;
        public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, int> PrunePending;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> StatusJson;
        // appended after v1's first table: an older table's header.size stops before these (HasAt)
        public delegate* unmanaged[Cdecl]<NativeStr, uint, GameState*, int> GameStateAt;
        public delegate* unmanaged[Cdecl]<NativeStr, uint, uint*, nuint, nuint*, int> GameStateIdsAt;
    }

    /// <summary>The table up to status_json: what every v1 table has.</summary>
    public static readonly uint FirstSize = (uint)(sizeof(ServiceHeader) + 10 * sizeof(nint));

    /// <summary>The table, or null when the service is missing or its table is shorter than the first v1 table.</summary>
    public static Table* Get()
    {
        var h = Services.Get<ServiceHeader>(Service, Version);
        return h != null && h->Size >= FirstSize ? (Table*)h : null;
    }

    /// <summary>Whether the table has game_state_at and game_state_ids_at.</summary>
    public static bool HasAt(Table* t) => t->Header.Size >= (uint)sizeof(Table);
}
