// SPDX-License-Identifier: MIT
// The kit's kiln.assets (native/core/abi/services/kiln_assets.h, the table up to blob_catalog_close): content blobs (add,
// override, remove, read, the 0.2.0-dev.28 ex slots), owner revert, status, and the content blob catalog over the test's
// game index (AssetsState.Game). Resources, icons, textures, dumps and manifests are not modelled: they answer
// KILN_ERR_UNAVAILABLE, as the native service does with those hooks off. Registered in every KilnHarness (AssetsFake.cs). The catalog keeps the native rules
// (catalog.cpp): one entry per hash, GAME plus every live registration's origin, the last registration served, sorted by
// (size, hash0, hash1, hash2), 64 handles, NotReady while the index is not readable, snapshots that never change.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Kiln.TestKit;

/// <summary>Mirror of <c>kiln_assets.h</c>'s table, its content hash and the blob catalog's records. The sizes are pinned
/// by a test to Kiln.Assets' own mirror (<c>managed/tests/Kiln.Api.Tests/BlobCatalogTests.cs</c>).</summary>
internal static unsafe class AssetsAbi
{
    public const string Service = "kiln.assets";
    public const uint Version = 1;
    public const uint OriginGame = 1, OriginManifest = 2, OriginManaged = 4, OriginNativeApi = 8;
    public const uint StatusComplete = 0, StatusIncomplete = 1, StatusNotReady = 2, StatusLimitExceeded = 3;
    public const uint FlagGameComplete = 1, FlagRegistrationsComplete = 2, EntryOverridden = 1;
    public const uint ReasonNone = 0, ReasonGameNotReady = 1, ReasonMemoryBudget = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct Hash { public uint Size, H0, H1, H2; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CatalogInfo { public uint Size, Status; public ulong Generation, Total; public uint Flags, Reason; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CatalogEntry
    {
        public uint Size, Flags;
        public Hash Hash;
        public uint Origins, SelectedOrigin;
        public ulong SelectedOwner, SelectedSequence;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, byte*, void*, nuint, int> IconRegisterPng;
        public delegate* unmanaged[Cdecl]<ulong, byte*, NativeStr, int> IconRegisterFile;
        public delegate* unmanaged[Cdecl]<ulong, byte*, int> IconRemove;
        public delegate* unmanaged[Cdecl]<byte*, int> IconState;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, nuint, uint, int> ResourcePut;
        public delegate* unmanaged[Cdecl]<ulong, void*, int> ResourceRemove;
        public delegate* unmanaged[Cdecl]<void*, void*, nuint, nuint*, int> ResourceRead;
        public delegate* unmanaged[Cdecl]<uint, void*, nuint, nuint*, int> ResourceList;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, Hash*, int> BlobAdd;
        public delegate* unmanaged[Cdecl]<ulong, Hash*, void*, nuint, int> BlobOverride;
        public delegate* unmanaged[Cdecl]<ulong, Hash*, int> BlobRemove;
        public delegate* unmanaged[Cdecl]<Hash*, void*, nuint, nuint*, int> BlobRead;
        public delegate* unmanaged[Cdecl]<ulong, void*, void*, nuint, int> TextureOverridePng;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> Dump;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Status;
        public delegate* unmanaged[Cdecl]<ulong, void> RevertOwner;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, byte*, nuint, nuint*, int> LoadManifest;
        public delegate* unmanaged[Cdecl]<void*, void**, nuint*, int> ResourceData;
        public delegate* unmanaged[Cdecl]<void*, int> ResourceIsNew;
        public delegate* unmanaged[Cdecl]<uint, nuint> ResourceNewCount;
        public delegate* unmanaged[Cdecl]<void*, void*, int> WhenResourcesReady;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, Hash*, uint, int> BlobAddEx;
        public delegate* unmanaged[Cdecl]<ulong, Hash*, void*, nuint, uint, int> BlobOverrideEx;
        public delegate* unmanaged[Cdecl]<ulong, uint, ulong*, CatalogInfo*, int> BlobCatalogOpen;
        public delegate* unmanaged[Cdecl]<ulong, ulong, CatalogEntry*, uint, nuint, nuint*, int> BlobCatalogRead;
        public delegate* unmanaged[Cdecl]<ulong, int> BlobCatalogClose;
    }
}

/// <summary>What the kit's kiln.assets holds for one harness (or for a unit test's own table, <see cref="AssetsFake.Unit"/>).</summary>
internal sealed class AssetsState
{
    public sealed record Registration(ulong Owner, AssetsAbi.Hash Hash, byte[] Bytes, uint Origin, ulong Sequence, bool Override);

    public readonly object Gate = new();
    public readonly List<(AssetsAbi.Hash Hash, byte[] Bytes)> Game = [];  // the test's game index, in its order
    public readonly List<Registration> Ledger = [];  // live registrations, in the order that decides the winner
    public readonly Dictionary<ulong, (ulong Owner, AssetsAbi.CatalogEntry[] Entries)> Handles = [];
    public bool IndexReady = true;
    public ulong Generation = 1, NextSequence, NextHandle = 1;
    public string Change = "none";

    public void Changed(string why)
    {
        Generation++;
        Change = why;
    }
}

/// <summary>The C# functions behind the kit's <c>kiln.assets</c> table.</summary>
internal static unsafe class AssetsTable
{
    private const int Ok = FakeTables.Ok, Invalid = FakeTables.InvalidArgument, NotFound = FakeTables.NotFound,
        Unavailable = FakeTables.Unavailable, TooSmall = FakeTables.BufferTooSmall;
    private const int MaxHandles = 64;

    private static AssetsAbi.Table* _table;

    public static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (AssetsAbi.Table*)NativeMemory.AllocZeroed((nuint)sizeof(AssetsAbi.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(AssetsAbi.Table), Version = AssetsAbi.Version };
        t->IconRegisterPng = &IconPng;
        t->IconRegisterFile = &IconFile;
        t->IconRemove = &IconRemove;
        t->IconState = &IconState;
        t->ResourcePut = &ResourcePut;
        t->ResourceRemove = &ResourceRemove;
        t->ResourceRead = &ResourceRead;
        t->ResourceList = &ResourceList;
        t->BlobAdd = &BlobAdd;
        t->BlobOverride = &BlobOverride;
        t->BlobRemove = &BlobRemove;
        t->BlobRead = &BlobRead;
        t->TextureOverridePng = &TexturePng;
        t->Dump = &Dump;
        t->Status = &Status;
        t->RevertOwner = &RevertOwner;
        t->LoadManifest = &LoadManifest;
        t->ResourceData = &ResourceData;
        t->ResourceIsNew = &ResourceIsNew;
        t->ResourceNewCount = &ResourceNewCount;
        t->WhenResourcesReady = &WhenReady;
        t->BlobAddEx = &BlobAddEx;
        t->BlobOverrideEx = &BlobOverrideEx;
        t->BlobCatalogOpen = &CatalogOpen;
        t->BlobCatalogRead = &CatalogRead;
        t->BlobCatalogClose = &CatalogClose;
        _table = t;
        return (nint)t;
    }

    // a unit test's state, else the running harness's; null: answered as kiln.assets with its content hooks off
    private static AssetsState? S => AssetsFake.Current;

    /// <summary>The fake's content hash of some bytes: their size and the first 12 bytes of their SHA-256.</summary>
    public static AssetsAbi.Hash HashOf(ReadOnlySpan<byte> bytes)
    {
        Span<byte> d = stackalloc byte[32];
        SHA256.HashData(bytes, d);
        return new AssetsAbi.Hash
        {
            Size = (uint)bytes.Length, H0 = BitConverter.ToUInt32(d[..4]), H1 = BitConverter.ToUInt32(d[4..8]), H2 = BitConverter.ToUInt32(d[8..12]),
        };
    }

    private static bool Same(in AssetsAbi.Hash a, in AssetsAbi.Hash b) => a.Size == b.Size && a.H0 == b.H0 && a.H1 == b.H1 && a.H2 == b.H2;

    private static int Compare(in AssetsAbi.Hash a, in AssetsAbi.Hash b) =>
        a.Size != b.Size ? a.Size.CompareTo(b.Size) : a.H0 != b.H0 ? a.H0.CompareTo(b.H0) : a.H1 != b.H1 ? a.H1.CompareTo(b.H1) : a.H2.CompareTo(b.H2);

    // ---- what is not modelled ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int IconPng(ulong o, byte* g, void* p, nuint n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int IconFile(ulong o, byte* g, NativeStr p) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int IconRemove(ulong o, byte* g) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int IconState(byte* g) => 0;  // KILN_ICON_NONE
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourcePut(ulong o, void* k, void* d, nuint n, uint f) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourceRemove(ulong o, void* k) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourceRead(void* k, void* b, nuint c, nuint* n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourceList(uint t, void* o, nuint c, nuint* n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int TexturePng(ulong o, void* k, void* p, nuint n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int Dump(NativeStr s, byte* r, nuint c, nuint* n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int LoadManifest(ulong o, NativeStr d, byte* r, nuint c, nuint* n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourceData(void* k, void** p, nuint* n) => Unavailable;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int ResourceIsNew(void* k) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static nuint ResourceNewCount(uint t) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])] private static int WhenReady(void* fn, void* user) => Unavailable;

    // ---- content blobs ----
    private static int Register(ulong owner, AssetsAbi.Hash h, byte* data, nuint len, uint origin, bool isOverride)
    {
        if (data == null && len != 0) return Invalid;
        if (isOverride && h.Size != (uint)len) return Invalid;
        var s = S;
        if (s is null) return Unavailable;
        var bytes = new ReadOnlySpan<byte>(data, (int)len).ToArray();
        lock (s.Gate)
        {
            var reg = new AssetsState.Registration(owner, h, bytes, origin, ++s.NextSequence, isOverride);
            var at = s.Ledger.FindIndex(r => r.Owner == owner && Same(r.Hash, h));
            if (at >= 0) s.Ledger[at] = reg;  // in place, as kiln.assets does
            else s.Ledger.Add(reg);
            s.Changed("registration");
        }
        return Ok;
    }

    private static bool ApiOrigin(uint origin) => origin is AssetsAbi.OriginManaged or AssetsAbi.OriginNativeApi;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobAdd(ulong owner, void* data, nuint len, AssetsAbi.Hash* outHash) => Add(owner, data, len, outHash, AssetsAbi.OriginNativeApi);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobAddEx(ulong owner, void* data, nuint len, AssetsAbi.Hash* outHash, uint origin) =>
        ApiOrigin(origin) ? Add(owner, data, len, outHash, origin) : Invalid;

    private static int Add(ulong owner, void* data, nuint len, AssetsAbi.Hash* outHash, uint origin)
    {
        if (data == null && len != 0) return Invalid;
        var h = HashOf(new ReadOnlySpan<byte>(data, (int)len));
        var rc = Register(owner, h, (byte*)data, len, origin, false);
        if (rc == Ok && outHash != null) *outHash = h;
        return rc;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobOverride(ulong owner, AssetsAbi.Hash* hash, void* data, nuint len) =>
        hash == null ? Invalid : Register(owner, *hash, (byte*)data, len, AssetsAbi.OriginNativeApi, true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobOverrideEx(ulong owner, AssetsAbi.Hash* hash, void* data, nuint len, uint origin) =>
        hash == null || !ApiOrigin(origin) ? Invalid : Register(owner, *hash, (byte*)data, len, origin, true);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobRemove(ulong owner, AssetsAbi.Hash* hash)
    {
        if (hash == null) return Invalid;
        var s = S;
        if (s is null) return Unavailable;
        var h = *hash;
        lock (s.Gate)
            if (s.Ledger.RemoveAll(r => r.Owner == owner && Same(r.Hash, h)) > 0) s.Changed("remove");
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int BlobRead(AssetsAbi.Hash* hash, void* buffer, nuint cap, nuint* outLen)
    {
        if (hash == null) return Invalid;
        var s = S;
        if (s is null) return Unavailable;
        var h = *hash;
        byte[]? bytes;
        lock (s.Gate)
            bytes = s.Ledger.LastOrDefault(r => Same(r.Hash, h))?.Bytes ?? s.Game.FirstOrDefault(g => Same(g.Hash, h)).Bytes;
        if (bytes is null) return NotFound;
        if (outLen != null) *outLen = (nuint)bytes.Length;
        if (cap < (nuint)bytes.Length) return TooSmall;
        bytes.CopyTo(new Span<byte>(buffer, bytes.Length));
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void RevertOwner(ulong owner)
    {
        var s = S;
        if (s is null) return;
        lock (s.Gate)
        {
            foreach (var id in s.Handles.Where(kv => kv.Value.Owner == owner).Select(kv => kv.Key).ToList()) s.Handles.Remove(id);
            if (s.Ledger.RemoveAll(r => r.Owner == owner) > 0) s.Changed("revoke");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Status(byte* json, nuint cap, nuint* outLen)
    {
        var s = S;
        string text;
        if (s is null) text = "{\"hooks\":{\"resources\":false,\"content\":false,\"contentWhy\":\"kiln.assets is switched off in this test\",\"uiIcons\":false}}";
        else
            lock (s.Gate)
                text = $"{{\"blobs\":{s.Ledger.Count},\"catalog\":{{\"generation\":{s.Generation},\"change\":\"{s.Change}\"}}," +
                       "\"hooks\":{\"resources\":false,\"resourcesWhy\":\"not modelled by the test kit\",\"content\":true,\"uiIcons\":false}}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        if (outLen != null) *outLen = (nuint)bytes.Length;
        if (json == null || cap <= (nuint)bytes.Length) return TooSmall;
        bytes.CopyTo(new Span<byte>(json, bytes.Length));
        json[bytes.Length] = 0;
        return Ok;
    }

    // ---- the blob catalog ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CatalogOpen(ulong owner, uint mode, ulong* handle, AssetsAbi.CatalogInfo* info)
    {
        if (handle != null) *handle = 0;
        if (handle == null || info == null || info->Size < (uint)sizeof(AssetsAbi.CatalogInfo) || owner == 0 || mode > 1) return Invalid;
        var s = S;
        if (s is null) return Unavailable;
        lock (s.Gate)
        {
            var size = info->Size;
            *info = new AssetsAbi.CatalogInfo { Size = size, Generation = s.Generation };
            if (mode == 0 && !s.IndexReady)
            {
                info->Status = AssetsAbi.StatusNotReady;
                info->Reason = AssetsAbi.ReasonGameNotReady;
                return Unavailable;
            }
            if (s.Handles.Count >= MaxHandles)
            {
                info->Status = AssetsAbi.StatusLimitExceeded;
                info->Reason = AssetsAbi.ReasonMemoryBudget;
                return TooSmall;
            }
            var entries = new List<AssetsAbi.CatalogEntry>();
            if (mode == 0)
                foreach (var (h, _) in s.Game)
                    if (!entries.Exists(e => Same(e.Hash, h)))
                        entries.Add(new AssetsAbi.CatalogEntry
                        {
                            Size = (uint)sizeof(AssetsAbi.CatalogEntry), Hash = h, Origins = AssetsAbi.OriginGame, SelectedOrigin = AssetsAbi.OriginGame,
                        });
            foreach (var r in s.Ledger)
            {
                var at = entries.FindIndex(e => Same(e.Hash, r.Hash));
                var e = at >= 0 ? entries[at] : new AssetsAbi.CatalogEntry { Size = (uint)sizeof(AssetsAbi.CatalogEntry), Hash = r.Hash };
                if ((e.Origins & AssetsAbi.OriginGame) != 0) e.Flags = AssetsAbi.EntryOverridden;
                e.Origins |= r.Origin;
                e.SelectedOrigin = r.Origin;  // the later registration of a hash is served
                e.SelectedOwner = r.Owner;
                e.SelectedSequence = r.Sequence;
                if (at >= 0) entries[at] = e;
                else entries.Add(e);
            }
            entries.Sort((a, b) => Compare(a.Hash, b.Hash));
            var id = s.NextHandle++;
            s.Handles[id] = (owner, [.. entries]);
            *handle = id;
            info->Status = mode == 0 ? AssetsAbi.StatusComplete : AssetsAbi.StatusIncomplete;
            info->Flags = mode == 0 ? AssetsAbi.FlagGameComplete | AssetsAbi.FlagRegistrationsComplete : AssetsAbi.FlagRegistrationsComplete;
            info->Total = (ulong)entries.Count;
            return Ok;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CatalogRead(ulong handle, ulong first, AssetsAbi.CatalogEntry* output, uint stride, nuint cap, nuint* written)
    {
        if (written != null) *written = 0;
        if (written == null || stride < (uint)sizeof(AssetsAbi.CatalogEntry) || (cap > 0 && output == null)) return Invalid;
        var s = S;
        if (s is null) return Unavailable;
        AssetsAbi.CatalogEntry[] entries;
        lock (s.Gate)
        {
            if (!s.Handles.TryGetValue(handle, out var h)) return NotFound;
            entries = h.Entries;
        }
        if (first > (ulong)entries.Length) return Invalid;
        var n = (int)Math.Min((ulong)cap, (ulong)entries.Length - first);
        var dst = (byte*)output;
        for (var i = 0; i < n; i++, dst += stride)
        {
            *(AssetsAbi.CatalogEntry*)dst = entries[(int)first + i];
            new Span<byte>(dst + sizeof(AssetsAbi.CatalogEntry), (int)stride - sizeof(AssetsAbi.CatalogEntry)).Clear();
        }
        *written = (nuint)n;
        return Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int CatalogClose(ulong handle)
    {
        var s = S;
        if (s is null) return Unavailable;
        lock (s.Gate) return s.Handles.Remove(handle) ? Ok : NotFound;
    }
}
