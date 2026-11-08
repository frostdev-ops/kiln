// SPDX-License-Identifier: MIT
// Assets.ListBlobs / ListAllBlobs: what each mod added or overrode through Assets, over a fake kiln.assets.
using System.Runtime.InteropServices;
using Kiln;
using Xunit;

namespace Kiln.Api.Tests;

public class AssetsBlobTests : KilnTest
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AddFn(ulong owner, nint data, nuint length, nint outHash);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OverrideFn(ulong owner, nint hash, nint data, nuint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RemoveFn(ulong owner, nint hash);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void RevertFn(ulong owner);

    private readonly nint _table;
    private readonly List<Delegate> _keep = [];
    private readonly List<ulong> _reverted = [];
    private readonly List<(ulong Owner, uint Size)> _native = [];

    public AssetsBlobTests()
    {
        var size = Marshal.SizeOf<AssetsNative.Table>();
        _table = Marshal.AllocHGlobal(size);
        Marshal.Copy(new byte[size], 0, _table, size);
        Marshal.WriteInt32(_table, size);
        Bind("BlobAdd", new AddFn((owner, data, length, outHash) =>
        {
            var bytes = new byte[(int)length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            Marshal.WriteInt32(outHash, 0, bytes.Length);
            Marshal.WriteInt32(outHash, 4, (int)Fnv(bytes, 1));
            Marshal.WriteInt32(outHash, 8, (int)Fnv(bytes, 2));
            Marshal.WriteInt32(outHash, 12, (int)Fnv(bytes, 3));
            _native.Add((owner, (uint)bytes.Length));
            return 0;
        }));
        Bind("BlobOverride", new OverrideFn((owner, hash, data, length) =>
        {
            if ((uint)Marshal.ReadInt32(hash) != (uint)length) return 1;     // the length must be the hash's size
            _native.Add((owner, (uint)length));
            return 0;
        }));
        Bind("BlobRemove", new RemoveFn((owner, hash) => { _native.RemoveAll(n => n.Owner == owner && n.Size == (uint)Marshal.ReadInt32(hash)); return 0; }));
        Bind("RevertOwner", new RevertFn(owner => { _reverted.Add(owner); _native.RemoveAll(n => n.Owner == owner); }));
        Backend.Tables["kiln.assets"] = _table;
        typeof(Assets).GetField("_table", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, null);
    }

    private static uint Fnv(byte[] bytes, uint seed)
    {
        var h = 0x811c9dc5u * seed;
        foreach (var b in bytes) h = (h ^ b) * 0x01000193u;
        return h;
    }

    private void Bind(string member, Delegate callback)
    {
        _keep.Add(callback);
        Marshal.WriteIntPtr(_table, (int)Marshal.OffsetOf<AssetsNative.Table>(member), Marshal.GetFunctionPointerForDelegate(callback));
    }

    public override void Dispose()
    {
        typeof(Assets).GetField("_table", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, null);
        Backend.Tables.Clear();
        Marshal.FreeHGlobal(_table);
        base.Dispose();
    }

    private static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i + seed)).ToArray();

    [Fact]
    public void ListsWhatTheModAddedAndOverrode()
    {
        var mod = NewMod("blobby");
        As(mod, () =>
        {
            Assert.Empty(Assets.ListBlobs());
            var a = Assets.AddBlob(Bytes(64, 1));
            var b = Assets.AddBlob(Bytes(128, 2));
            var list = Assets.ListBlobs();
            Assert.Equal([new BlobEntry(a, BlobKind.Added, "blobby"), new BlobEntry(b, BlobKind.Added, "blobby")], list);
            Assert.Equal(64u, list[0].Hash.Size);

            Assets.AddBlob(Bytes(64, 1));                                // the same bytes again: the same blob, listed once
            Assert.Equal(2, Assets.ListBlobs().Count);

            Assets.OverrideBlob(a, Bytes(64, 9));                        // the blob's hash is now an override
            Assert.Equal(BlobKind.Override, Assets.ListBlobs().Single(e => e.Hash == a).Kind);
            Assert.Throws<ArgumentException>(() => Assets.OverrideBlob(b, new byte[3]));   // refused: not listed as an override
            Assert.Equal(BlobKind.Added, Assets.ListBlobs().Single(e => e.Hash == b).Kind);

            Assets.RemoveBlob(a);
            Assert.Equal([b], Assets.ListBlobs().Select(e => e.Hash).ToArray());
        });
    }

    [Fact]
    public void ModsSeeTheirOwnAndAllModsSeeEveryones()
    {
        var a = NewMod("mod-a");
        var b = NewMod("mod-b");
        var ha = As(a, () => Assets.AddBlob(Bytes(32, 1)));
        var hb = As(b, () => Assets.AddBlob(Bytes(48, 2)));
        As(a, () => Assert.Equal([ha], Assets.ListBlobs().Select(e => e.Hash).ToArray()));
        As(b, () => Assert.Equal([hb], Assets.ListBlobs().Select(e => e.Hash).ToArray()));
        var all = Assets.ListAllBlobs();
        Assert.Equal(new[] { ("mod-a", ha), ("mod-b", hb) }, all.Select(e => (e.Mod, e.Hash)).ToArray());
    }

    [Fact]
    public void AModsBlobsLeaveTheListWhenItUnloads()
    {
        var a = NewMod("mod-a");
        var b = NewMod("mod-b");
        As(a, () => Assets.AddBlob(Bytes(32, 1)));
        var hb = As(b, () => Assets.AddBlob(Bytes(48, 2)));
        a.Retire();
        Assert.Equal([hb], Assets.ListAllBlobs().Select(e => e.Hash).ToArray());
        Assert.Single(_reverted);                                          // and the native side drops them too
    }

    [Fact]
    public void ListBlobsNeedsAMod()
    {
        Assert.Throws<InvalidOperationException>(() => Assets.ListBlobs());
        Assert.Empty(Assets.ListAllBlobs());
    }

    [Fact]
    public void ContentHashesCompareByValue()
    {
        var mod = NewMod("hashes");
        var (x, y) = As(mod, () => (Assets.AddBlob(Bytes(8, 1)), Assets.AddBlob(Bytes(8, 1))));
        Assert.Equal(x, y);
        Assert.True(x == y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        var z = As(mod, () => Assets.AddBlob(Bytes(8, 2)));
        Assert.NotEqual(x, z);
        Assert.True(x != z);
    }
}
