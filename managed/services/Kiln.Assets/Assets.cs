// SPDX-License-Identifier: MIT
// Kiln.Assets: the public managed API over kiln.assets. Mods supply their own resources and override vanilla ones at
// run time; no game file is changed. Icons and textures are client-only; resources and blobs work on both sides.
// Guide: docs/kiln/services/assets.md.
using System.Runtime.InteropServices;

namespace Kiln;

/// <summary>A game content key: a resource GUID, a type and a part index.</summary>
public readonly struct AssetKey
{
    public Guid Guid { get; }
    public uint Type { get; }
    public uint Part { get; }
    public AssetKey(Guid guid, uint type, uint part = 0) { Guid = guid; Type = type; Part = part; }
    /// <summary>A key by a reflection type name (e.g. "keen::RenderModel"); the hash is FNV-1a-32.</summary>
    public AssetKey(Guid guid, string typeName, uint part = 0) : this(guid, Assets.TypeHash(typeName), part) { }
    public const uint UiTextureType = AssetsNative.TypeUiTexture;
    public const uint ItemInfoType = AssetsNative.TypeItemInfo;
}

/// <summary>A content blob hash (keen::ContentHash): the identity of a streamed blob (texture pixels, mesh data).</summary>
public readonly struct ContentHash
{
    public uint Size { get; }
    public uint H0 { get; }
    public uint H1 { get; }
    public uint H2 { get; }
    internal ContentHash(uint size, uint h0, uint h1, uint h2) { Size = size; H0 = h0; H1 = h1; H2 = h2; }
    public override string ToString() => $"{Size}-{H0:x8}-{H1:x8}-{H2:x8}";
}

/// <summary>The UI state of a registered icon (see <see cref="Assets.IconStateOf"/>).</summary>
public enum IconState { None = 0, Pending = 1, Served = 2, Failed = 3 }

public static unsafe class Assets
{
    private static AssetsNative.Table* _table;
    private static AssetsNative.Table* T =>
        _table != null ? _table : (_table = Services.Get<AssetsNative.Table>(AssetsNative.Service, AssetsNative.Version));

    /// <summary>True when kiln.assets is loaded and its hooks are on (a verified build or policy "any").</summary>
    public static bool Available => T != null;

    /// <summary>FNV-1a-32 of a reflection type name, e.g. "keen::UiTextureResource".</summary>
    public static uint TypeHash(string name)
    {
        uint h = 0x811c9dc5u;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(name)) h = (h ^ b) * 0x01000193u;
        return h;
    }

    private static ulong Owner()
    {
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException("Assets calls must be made from mod code");
        Track(mod);
        return mod.Owner;
    }

    private static readonly HashSet<ModContext> Watched = new();
    private static readonly object Lock = new();
    private static void Track(ModContext mod)
    {
        lock (Lock)
            if (!Watched.Add(mod)) return;
        mod.Retiring += () =>
        {
            lock (Lock) Watched.Remove(mod);
            var t = T;
            if (t != null) t->RevertOwner(mod.Owner);
        };
    }

    private static AssetsNative.Table* Require()
        => T != null ? T : throw new NotSupportedException("kiln.assets is unavailable on this build");

    private static void Check(int rc, string op)
    {
        switch (rc)
        {
            case AssetsNative.Ok: return;
            case AssetsNative.InvalidArgument: throw new ArgumentException($"{op}: invalid argument");
            case AssetsNative.NotFound: throw new KeyNotFoundException($"{op}: not found");
            case AssetsNative.Unavailable: throw new NotSupportedException($"{op}: unavailable on this build/side");
            default: throw new InvalidOperationException($"{op}: error {rc}");
        }
    }

    private static void WriteGuid(Guid g, byte* dst)
    {
        var b = g.ToByteArray();  // .NET order == the game's resource-id byte order
        for (int i = 0; i < 16; i++) dst[i] = b[i];
    }

    // ---- icons (client) -----------------------------------------------------------------------------------------
    /// <summary>
    /// Register a PNG as the inventory / crafting icon of the item with this GUID. Client only. A new item gets a new
    /// icon texture; a game item (a model override that needs a matching icon) has its own icon texture retextured in
    /// that texture's format and size. Icons in assets/assets.json apply before the UI builds; a game item's icon
    /// registered later shows once the UI loads that texture again (the next start at the latest).
    /// </summary>
    public static void RegisterIcon(Guid itemGuid, byte[] png)
    {
        var t = Require();
        var owner = Owner();
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        fixed (byte* p = png) Check(t->IconRegisterPng(owner, g, p, (nuint)png.Length), "RegisterIcon");
    }

    /// <summary>Register an icon from a PNG file (absolute, or relative to &lt;game&gt;/kiln). Client only; see
    /// <see cref="RegisterIcon(Guid, byte[])"/>.</summary>
    public static void RegisterIcon(Guid itemGuid, string pngPath)
    {
        var t = Require();
        var owner = Owner();
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        var u = new AssetsNative.Utf8(pngPath);
        int rc = AssetsNative.Unavailable;
        u.With(s => rc = t->IconRegisterFile(owner, g, s));
        Check(rc, "RegisterIcon");
    }

    /// <summary>Remove this mod's icon for a GUID.</summary>
    public static void RemoveIcon(Guid itemGuid)
    {
        var t = Require();
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        t->IconRemove(Owner(), g);
    }

    /// <summary>The UI state of an icon: used by content code to decide whether to alias to a base item.</summary>
    public static IconState IconStateOf(Guid itemGuid)
    {
        var t = T;
        if (t == null) return IconState.None;
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        return (IconState)t->IconState(g);
    }

    // ---- resources ----------------------------------------------------------------------------------------------
    /// <summary>Serve `bytes` for `key`. mode: null = either, true = must be new, false = must override a game resource.</summary>
    public static void Register(AssetKey key, byte[] bytes, bool? mustBeNew = null)
    {
        var t = Require();
        var owner = Owner();
        uint flags = mustBeNew == true ? AssetsNative.PutNew : mustBeNew == false ? AssetsNative.PutOverride : 0;
        var k = MakeKey(key);
        fixed (byte* p = bytes) Check(t->ResourcePut(owner, &k, p, (nuint)bytes.Length, flags), "Register");
    }

    /// <summary>Serves a new resource as a given owner (Kiln.Blocks: a generated Impact program).</summary>
    internal static void RegisterAs(ulong owner, AssetKey key, byte[] bytes)
    {
        var t = Require();
        var k = MakeKey(key);
        fixed (byte* p = bytes) Check(t->ResourcePut(owner, &k, p, (nuint)bytes.Length, 0), "Register");
    }

    /// <summary>Override a game resource's bytes for the session.</summary>
    public static void Override(AssetKey key, byte[] bytes) => Register(key, bytes, mustBeNew: false);

    /// <summary>Remove this mod's resource / override for a key.</summary>
    public static void RemoveResource(AssetKey key)
    {
        var t = Require();
        var k = MakeKey(key);
        t->ResourceRemove(Owner(), &k);
    }

    /// <summary>The bytes the game gets for `key` now (an override if any, else the game's own), or null if absent.</summary>
    public static byte[]? ReadResource(AssetKey key)
    {
        var t = Require();
        var k = MakeKey(key);
        nuint n = 0;
        int rc = t->ResourceRead(&k, null, 0, &n);
        if (rc == AssetsNative.NotFound) return null;
        var buf = new byte[n];
        fixed (byte* p = buf) rc = t->ResourceRead(&k, p, n, &n);
        if (rc == AssetsNative.NotFound) return null;
        Check(rc, "ReadResource");
        return buf;
    }

    /// <summary>Every key of a type (the game's plus new ones).</summary>
    public static AssetKey[] ListResources(uint type)
    {
        var t = Require();
        nuint n = 0;
        t->ResourceList(type, null, 0, &n);
        var raw = new AssetsNative.Key[n];
        fixed (AssetsNative.Key* p = raw) Check(t->ResourceList(type, p, n, &n), "ListResources");
        var outv = new AssetKey[n];
        for (nuint i = 0; i < n; i++)
        {
            var b = new byte[16];
            fixed (byte* src = raw[i].Guid) Marshal.Copy((nint)src, b, 0, 16);
            outv[i] = new AssetKey(new Guid(b), raw[i].Type, raw[i].Part);
        }
        return outv;
    }

    // ---- content blobs ------------------------------------------------------------------------------------------
    /// <summary>Add a new blob; the returned hash goes into a resource's content reference.</summary>
    public static ContentHash AddBlob(byte[] bytes)
    {
        var t = Require();
        AssetsNative.ContentHash h;
        fixed (byte* p = bytes) Check(t->BlobAdd(Owner(), p, (nuint)bytes.Length, &h), "AddBlob");
        return new ContentHash(h.Size, h.H0, h.H1, h.H2);
    }

    /// <summary>Serve `bytes` instead of the game's blob `hash` (bytes.Length must equal hash.Size).</summary>
    public static void OverrideBlob(ContentHash hash, byte[] bytes)
    {
        var t = Require();
        var h = new AssetsNative.ContentHash { Size = hash.Size, H0 = hash.H0, H1 = hash.H1, H2 = hash.H2 };
        fixed (byte* p = bytes) Check(t->BlobOverride(Owner(), &h, p, (nuint)bytes.Length), "OverrideBlob");
    }

    public static void RemoveBlob(ContentHash hash)
    {
        var t = Require();
        var h = new AssetsNative.ContentHash { Size = hash.Size, H0 = hash.H0, H1 = hash.H1, H2 = hash.H2 };
        t->BlobRemove(Owner(), &h);
    }

    // ---- textures (client) --------------------------------------------------------------------------------------
    /// <summary>Override a texture resource's pixels with a PNG, encoded to the texture's own format. Client only.</summary>
    public static void OverrideTexture(AssetKey key, byte[] png)
    {
        var t = Require();
        var owner = Owner();
        var k = MakeKey(key);
        fixed (byte* p = png) Check(t->TextureOverridePng(owner, &k, p, (nuint)png.Length), "OverrideTexture");
    }

    // ---- tools --------------------------------------------------------------------------------------------------
    /// <summary>Write resources / blobs to &lt;game&gt;/kiln/data/assets/dump/. See the guide for the spec grammar.</summary>
    public static string Dump(string spec)
    {
        var t = Require();
        var u = new AssetsNative.Utf8(spec);
        string result = "";
        u.With(s =>
        {
            nuint n = 0;
            t->Dump(s, null, 0, &n);
            var buf = new byte[n + 1];
            fixed (byte* p = buf) { t->Dump(s, p, (nuint)buf.Length, &n); result = System.Text.Encoding.UTF8.GetString(p, (int)n); }
        });
        return result;
    }

    /// <summary>Register every entry of a mod folder's assets/assets.json under this mod.</summary>
    public static string LoadManifest(string modDirectory)
    {
        var t = Require();
        var owner = Owner();
        var u = new AssetsNative.Utf8(modDirectory);
        string result = "";
        u.With(s =>
        {
            nuint n = 0;
            byte* rep = stackalloc byte[512];
            t->LoadManifest(owner, s, rep, 512, &n);
            result = System.Text.Encoding.UTF8.GetString(rep, (int)Math.Min(n, 511));
        });
        return result;
    }

    /// <summary>A JSON status string (hooks, counts, container, reads).</summary>
    public static string Status()
    {
        var t = T;
        if (t == null) return "{\"available\":false}";
        nuint n = 0;
        t->Status(null, 0, &n);
        var buf = new byte[n + 1];
        fixed (byte* p = buf) { t->Status(p, (nuint)buf.Length, &n); return System.Text.Encoding.UTF8.GetString(p, (int)n); }
    }

    private static AssetsNative.Key MakeKey(AssetKey key)
    {
        var k = new AssetsNative.Key { Type = key.Type, Part = key.Part };
        WriteGuid(key.Guid, k.Guid);
        return k;
    }
}
