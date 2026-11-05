// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_content.h (kiln.content v1..v4). Blittable only; field order is the ABI (the
// native side static_asserts the same sizes and offsets).
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class ContentNative
{
    public const string Service = "kiln.content";
    public const uint Version = 1;

    public const uint Inputs = 0, Outputs = 1, DropStacks = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Value  // 40 bytes
    {
        public uint Type;
        public uint Reserved;
        public long I;
        public double F;
        public fixed byte Guid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Field  // 56 bytes
    {
        public uint Size;
        public uint Index;
        public NativeStr Path;
        public NativeStr TypeName;
        public uint Offset;
        public byte Bytes;
        public byte ValueType;
        public byte IsSigned;
        public byte Writable;
        public byte EnumKind;
        public byte FieldFlags;  // v4: KILN_CONTENT_FIELD_* (reserved[0])
        public byte R1, R2;
        public uint EnumCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Entry  // 56 bytes
    {
        public uint Size;
        public uint Kind;
        public uint Id;
        public uint Flags;
        public NativeStr Name;
        public ulong Address;
        public fixed byte Guid[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Stack  // 32 bytes
    {
        public uint Item;
        public uint Category;
        public uint Count;
        public uint CountMax;
        public float Chance;
        public float Weight;
        public uint Group;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status  // 208 bytes
    {
        public uint Size;
        public byte Ready;
        public byte Registries;
        public byte StackGuard;
        public byte ReloadPending;
        public uint Items, Recipes, Loot, Drops;
        public uint Packs, PackErrors, Conflicts, Changed;
        public ulong Generation;
        public fixed byte Detail[160];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint, uint> Count;
        public delegate* unmanaged[Cdecl]<uint, uint, Entry*, int> EntryAt;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, Entry*, int> Find;
        public delegate* unmanaged[Cdecl]<uint, uint> FieldCount;
        public delegate* unmanaged[Cdecl]<uint, uint, Field*, int> FieldAt;
        public delegate* unmanaged[Cdecl]<uint, NativeStr, Field*, int> FieldFind;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, NativeStr*, long*, int> EnumName;
        public delegate* unmanaged[Cdecl]<uint, uint, NativeStr, Value*, Value*, int> Get;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, Value*, int> Set;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, int> Reset;
        public delegate* unmanaged[Cdecl]<uint, uint, uint, Stack*, uint, uint*, int> Stacks;
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, uint, Stack*, uint, int> SetStacks;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr, byte*, nuint, nuint*, int> ApplyJson;
        public delegate* unmanaged[Cdecl]<ulong, uint> RevertOwner;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> Dump;
        public delegate* unmanaged[Cdecl]<int> ReloadPacks;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Report;
        public delegate* unmanaged[Cdecl]<ulong, delegate* unmanaged[Cdecl]<ulong, uint, void*, void>, void*, ulong*, int> SubscribeReady;
        public delegate* unmanaged[Cdecl]<ulong, int> UnsubscribeReady;
        public delegate* unmanaged[Cdecl]<int, byte*, int> StackGuard;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, int> LabelOwner;
        public delegate* unmanaged[Cdecl]<NativeStr, int> PackMods;

        // ---- v2 (check Header.Version >= 2) ----
        public delegate* unmanaged[Cdecl]<uint, byte*, nuint, nuint*, int> ExportChanges;

        // ---- v3 (check Header.Version >= 3): new items and recipes (NewContent.cs) ----
        public delegate* unmanaged[Cdecl]<ulong, uint, NativeStr, byte*, nuint, nuint*, int> Define;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Definitions;

        // ---- v4 (check Header.Version >= 4): kitbash references, info ----
        public delegate* unmanaged[Cdecl]<ulong, uint, uint, NativeStr, NativeStr, uint, byte*, nuint, nuint*, int> SetRef;
        public delegate* unmanaged[Cdecl]<ulong, uint> UndefineOwner;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Info;
    }

    public const byte FieldRef = 1;
    public const uint RefAllowGame = 1;

    /// <summary>The table is at least <paramref name="v"/> (and long enough for its functions).</summary>
    public static bool Has(Table* t, uint v) => t != null && t->Header.Version >= v;

    // KilnResult
    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, WrongThread = 4, BufferTooSmall = 5,
        Internal = 7;

    public static string Text(byte* p, int max)
    {
        var n = 0;
        while (n < max && p[n] != 0) n++;
        return System.Text.Encoding.UTF8.GetString(p, n);
    }

    /// <summary>A UTF-8 copy of a string in native memory, valid until disposed: <c>using var k = new Utf8(key);</c>.</summary>
    public struct Utf8 : IDisposable
    {
        private byte* _p;
        public NativeStr Str;

        public Utf8(string? s)
        {
            s ??= "";
            var n = System.Text.Encoding.UTF8.GetByteCount(s);
            _p = (byte*)NativeMemory.Alloc((nuint)n + 1);
            fixed (char* c = s) System.Text.Encoding.UTF8.GetBytes(c, s.Length, _p, n);
            _p[n] = 0;
            Str = new NativeStr { Ptr = _p, Len = (nuint)n };
        }

        public void Dispose()
        {
            if (_p != null) NativeMemory.Free(_p);
            _p = null;
        }
    }

    public static string Message(int rc) => rc switch
    {
        InvalidArgument => "invalid argument or value out of range",
        NotFound => "not found",
        Unavailable => "unavailable (not ready, or not on this game build)",
        WrongThread => "not on the tick thread",
        BufferTooSmall => "buffer too small",
        Internal => "internal error (see kiln.log)",
        _ => $"error {rc}",
    };
}
