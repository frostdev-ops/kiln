// SPDX-License-Identifier: MIT
// Mirror of native/core/abi/services/kiln_assets.h (kiln.assets v1, with the slots appended since). Blittable only; the
// field order is the ABI.
using System.Runtime.InteropServices;

namespace Kiln;

internal static unsafe class AssetsNative
{
    public const string Service = "kiln.assets";
    public const uint Version = 1;

    // put() flags
    public const uint PutNew = 1, PutOverride = 2;
    // icon_state()
    public const int IconNone = 0, IconPending = 1, IconServed = 2, IconFailed = 3;
    // common type hashes
    public const uint TypeUiTexture = 0xf9e86997;
    public const uint TypeItemInfo = 0xb5ce8765;

    [StructLayout(LayoutKind.Sequential)]
    public struct Key  // KilnAssetKey: 24 bytes
    {
        public fixed byte Guid[16];
        public uint Type;
        public uint Part;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ContentHash  // keen::ContentHash: 16 bytes
    {
        public uint Size, H0, H1, H2;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<ulong, byte*, void*, nuint, int> IconRegisterPng;
        public delegate* unmanaged[Cdecl]<ulong, byte*, NativeStr, int> IconRegisterFile;
        public delegate* unmanaged[Cdecl]<ulong, byte*, int> IconRemove;
        public delegate* unmanaged[Cdecl]<byte*, int> IconState;
        public delegate* unmanaged[Cdecl]<ulong, Key*, void*, nuint, uint, int> ResourcePut;
        public delegate* unmanaged[Cdecl]<ulong, Key*, int> ResourceRemove;
        public delegate* unmanaged[Cdecl]<Key*, void*, nuint, nuint*, int> ResourceRead;
        public delegate* unmanaged[Cdecl]<uint, Key*, nuint, nuint*, int> ResourceList;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, ContentHash*, int> BlobAdd;
        public delegate* unmanaged[Cdecl]<ulong, ContentHash*, void*, nuint, int> BlobOverride;
        public delegate* unmanaged[Cdecl]<ulong, ContentHash*, int> BlobRemove;
        public delegate* unmanaged[Cdecl]<ContentHash*, void*, nuint, nuint*, int> BlobRead;
        public delegate* unmanaged[Cdecl]<ulong, Key*, void*, nuint, int> TextureOverridePng;
        public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> Dump;
        public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Status;
        public delegate* unmanaged[Cdecl]<ulong, void> RevertOwner;
        public delegate* unmanaged[Cdecl]<ulong, NativeStr, byte*, nuint, nuint*, int> LoadManifest;
        public delegate* unmanaged[Cdecl]<Key*, void**, nuint*, int> ResourceData;  // zero-copy live view (advanced)
    }

    /// <summary>The table since 0.2.0-dev.28: v1 as far as <see cref="Table"/> mirrors it, the three v1 slots after it this
    /// wrapper does not call, then blob_add_ex, blob_override_ex and the blob catalog. Read through the v1 pointer when
    /// header.size covers it (an older kiln.assets: never).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TableEx
    {
        public Table Base;
        public void* ResourceIsNew, ResourceNewCount, WhenResourcesReady;
        public delegate* unmanaged[Cdecl]<ulong, void*, nuint, ContentHash*, uint, int> BlobAddEx;
        public delegate* unmanaged[Cdecl]<ulong, ContentHash*, void*, nuint, uint, int> BlobOverrideEx;
        public delegate* unmanaged[Cdecl]<ulong, uint, ulong*, CatalogInfo*, int> BlobCatalogOpen;
        public delegate* unmanaged[Cdecl]<ulong, ulong, CatalogEntry*, uint, nuint, nuint*, int> BlobCatalogRead;
        public delegate* unmanaged[Cdecl]<ulong, int> BlobCatalogClose;
    }

    /// <summary>KilnBlobCatalogInfo: 32 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CatalogInfo
    {
        public uint Size, Status;
        public ulong Generation, Total;
        public uint Flags, Reason;
    }

    /// <summary>KilnBlobCatalogEntry: 48 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CatalogEntry
    {
        public uint Size, Flags;
        public ContentHash Hash;
        public uint Origins, SelectedOrigin;
        public ulong SelectedOwner, SelectedSequence;
    }

    // blob catalog modes, statuses, flags
    public const uint CatalogRequireComplete = 0, CatalogRegistrationsOnly = 1;
    public const uint CatalogComplete = 0, CatalogIncomplete = 1, CatalogNotReady = 2, CatalogLimitExceeded = 3, CatalogUnavailable = 4;
    public const uint CatalogGameComplete = 1, CatalogRegistrationsComplete = 2, CatalogOverridden = 1;

    // KILN_BLOB_ORIGIN_* (kiln_assets.h): the provenance a registration is tagged with
    public const uint OriginGame = 1, OriginManifest = 2, OriginManaged = 4, OriginNativeApi = 8;

    public const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3, BufferTooSmall = 5;

    /// <summary>Scoped UTF-8 buffer for a NativeStr argument.</summary>
    public ref struct Utf8
    {
        private byte[] _buf;
        public NativeStr Str;
        public Utf8(string s)
        {
            _buf = System.Text.Encoding.UTF8.GetBytes(s);
            Str = default;
        }
        public unsafe void With(System.Action<NativeStr> use)
        {
            fixed (byte* p = _buf) use(new NativeStr { Ptr = p, Len = (nuint)_buf.Length });
        }
    }
}
