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
public readonly struct ContentHash : IEquatable<ContentHash>
{
    public uint Size { get; }
    public uint H0 { get; }
    public uint H1 { get; }
    public uint H2 { get; }
    internal ContentHash(uint size, uint h0, uint h1, uint h2) { Size = size; H0 = h0; H1 = h1; H2 = h2; }
    public override string ToString() => $"{Size}-{H0:x8}-{H1:x8}-{H2:x8}";
    public bool Equals(ContentHash other) => Size == other.Size && H0 == other.H0 && H1 == other.H1 && H2 == other.H2;
    public override bool Equals(object? obj) => obj is ContentHash other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Size, H0, H1, H2);
    public static bool operator ==(ContentHash a, ContentHash b) => a.Equals(b);
    public static bool operator !=(ContentHash a, ContentHash b) => !a.Equals(b);
}

/// <summary>How a mod supplied a content blob: new bytes under a new hash, or bytes served instead of the game's blob.</summary>
public enum BlobKind { Added = 0, Override = 1 }

/// <summary>A content blob a mod supplied through <see cref="Assets"/> (see <see cref="Assets.ListBlobs"/>).</summary>
/// <param name="Hash">The blob's content hash.</param>
/// <param name="Kind">New blob, or an override of the game's.</param>
/// <param name="Mod">The id of the mod that supplied it.</param>
public readonly record struct BlobEntry(ContentHash Hash, BlobKind Kind, string Mod);

/// <summary>The UI state of a registered icon (see <see cref="Assets.IconStateOf"/>).</summary>
public enum IconState { None = 0, Pending = 1, Served = 2, Failed = 3 }

/// <summary>Mod resources, overrides, icons and retextures (<c>kiln.assets</c>). Every member may be called from any
/// thread (the native functions copy what they are given); the ones that register something must be called from mod
/// code, which owns the registration.</summary>
/// <remarks>Capabilities (<see cref="Capabilities"/>): <c>kiln.assets</c> (the service is here), the sigdb features
/// <c>assets.resources</c> (resources), <c>assets.content</c> (content blobs) and <c>assets.ui_icons</c> (icons, game
/// client), and <c>assets.textures</c> (texture overrides, game client). A member that needs one that is off throws
/// <see cref="KilnUnavailableException"/> naming it (a <see cref="NotSupportedException"/>, as before).</remarks>
[AnyThread]
public static unsafe partial class Assets
{
    private static AssetsNative.Table* _table;
    private static AssetsNative.Table* T =>
        _table != null ? _table : (_table = Services.Get<AssetsNative.Table>(AssetsNative.Service, AssetsNative.Version));

    // The table with the slots appended in 0.2.0-dev.28 (blob_add_ex, blob_override_ex, the blob catalog), or null on an
    // older kiln.assets: the v1 table itself when its header.size covers them (no lookup of its own, so it never lowers
    // kiln.assets' row and follows whatever T is).
    internal static AssetsNative.TableEx* TEx
    {
        get
        {
            var t = T;
            return t != null && t->Header.Size >= (uint)sizeof(AssetsNative.TableEx) ? (AssetsNative.TableEx*)t : null;
        }
    }

    /// <summary>True when kiln.assets is loaded and its hooks are on (a verified build or policy "any"; capability
    /// <c>kiln.assets</c>).</summary>
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
            lock (Lock)
            {
                Watched.Remove(mod);
                Blobs.RemoveAll(b => b.Owner == mod.Owner);
            }
            var t = T;
            if (t != null) t->RevertOwner(mod.Owner);
        };
    }

    private static AssetsNative.Table* Require()
    {
        var t = T;
        if (t != null) return t;
        Rows.Ensure();
        throw ServiceCaps.Unavailable(Caps.Service, "kiln.assets is unavailable on this build");
    }

    // `capability`: what an "unavailable" answer of this call means (the feature is off, or this is the wrong side).
    private static void Check(int rc, string op, string capability)
    {
        switch (rc)
        {
            case AssetsNative.Ok: return;
            case AssetsNative.InvalidArgument: throw new ArgumentException($"{op}: invalid argument");
            case AssetsNative.NotFound: throw new KeyNotFoundException($"{op}: not found");
            case AssetsNative.Unavailable:
                Rows.Ensure();
                throw ServiceCaps.Unavailable(capability, $"{op}: unavailable on this build/side");
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
    [ClientOnly]
    [RequiresCapability(Caps.Icons)]
    public static void RegisterIcon(Guid itemGuid, byte[] png)
    {
        var t = Require();
        var owner = Owner();
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        fixed (byte* p = png) Check(t->IconRegisterPng(owner, g, p, (nuint)png.Length), "RegisterIcon", Caps.Icons);
    }

    /// <summary>Register an icon from a PNG file (absolute, or relative to &lt;game&gt;/kiln). Client only; see
    /// <see cref="RegisterIcon(Guid, byte[])"/>.</summary>
    [ClientOnly]
    [RequiresCapability(Caps.Icons)]
    public static void RegisterIcon(Guid itemGuid, string pngPath)
    {
        var t = Require();
        var owner = Owner();
        byte* g = stackalloc byte[16];
        WriteGuid(itemGuid, g);
        var u = new AssetsNative.Utf8(pngPath);
        int rc = AssetsNative.Unavailable;
        u.With(s => rc = t->IconRegisterFile(owner, g, s));
        Check(rc, "RegisterIcon", Caps.Icons);
    }

    /// <summary>Remove this mod's icon for a GUID.</summary>
    [RequiresCapability(Caps.Service)]
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
    // The player's animation graph: kiln.playeranim merges every mod's assets/player-states.json into it once and pins it, and
    // kiln.assets refuses any other put of the key (KILN_ASSET_PUT_PIN in native/core/abi/services/kiln_assets.h). The native refusal
    // would come back as "unavailable on this build/side", which is not the reason: Register and Override say it first.
    private static readonly Guid PlayerGraphGuid = new("21319e83-5087-4fff-8795-22997843649e");
    private const uint PlayerGraphType = 0xa345050eu;  // FNV-1a-32 of keen::anim_graph::runtime_graph::AnimationGraphResource2_0

    private static void RefusePlayerGraph(AssetKey key, string op)
    {
        if (key.Guid == PlayerGraphGuid && key.Type == PlayerGraphType && key.Part == 0)
            throw new ArgumentException(
                $"{op}: {key.Guid} (keen::anim_graph::runtime_graph::AnimationGraphResource2_0, part 0) is the player's animation graph. " +
                "kiln.playeranim owns it and kiln.assets refuses any other put of it: replacing a running player's graph can end the server. " +
                "Add player animation states in assets/player-states.json instead.", nameof(key));
    }

    /// <summary>Serve `bytes` for `key`. mode: null = either, true = must be new, false = must override a game resource.</summary>
    /// <exception cref="ArgumentException">`key` is the player's animation graph (see <see cref="Override"/>).</exception>
    [RequiresCapability(Caps.Resources)]
    public static void Register(AssetKey key, byte[] bytes, bool? mustBeNew = null)
    {
        RefusePlayerGraph(key, "Register");
        var t = Require();
        var owner = Owner();
        uint flags = mustBeNew == true ? AssetsNative.PutNew : mustBeNew == false ? AssetsNative.PutOverride : 0;
        var k = MakeKey(key);
        fixed (byte* p = bytes) Check(t->ResourcePut(owner, &k, p, (nuint)bytes.Length, flags), "Register", Caps.Resources);
    }

    /// <summary>Serves a new resource as a given owner (Kiln.Blocks: a generated Impact program).</summary>
    internal static void RegisterAs(ulong owner, AssetKey key, byte[] bytes)
    {
        var t = Require();
        var k = MakeKey(key);
        fixed (byte* p = bytes) Check(t->ResourcePut(owner, &k, p, (nuint)bytes.Length, 0), "Register", Caps.Resources);
    }

    /// <summary>Registers a PNG file as the icon texture with this GUID, as a given owner (Kiln.Blocks: a status's or a perk's icon). The native result: 0 = taken.</summary>
    internal static int RegisterIconAs(ulong owner, Guid guid, string pngPath)
    {
        var t = T;
        if (t == null) return AssetsNative.Unavailable;
        byte* g = stackalloc byte[16];
        WriteGuid(guid, g);
        var u = new AssetsNative.Utf8(pngPath);
        int rc = AssetsNative.Unavailable;
        u.With(s => rc = t->IconRegisterFile(owner, g, s));
        return rc;
    }

    /// <summary>Override a game resource's bytes for the session.</summary>
    /// <remarks>The player's animation graph (GUID 21319e83-5087-4fff-8795-22997843649e,
    /// type keen::anim_graph::runtime_graph::AnimationGraphResource2_0, part 0) is refused: kiln.playeranim owns that key,
    /// and replacing a running player's graph can end the server. Add player animation states in assets/player-states.json
    /// instead.</remarks>
    /// <exception cref="ArgumentException">`key` is the player's animation graph. Nothing is put.</exception>
    [RequiresCapability(Caps.Resources)]
    public static void Override(AssetKey key, byte[] bytes)
    {
        RefusePlayerGraph(key, "Override");
        Register(key, bytes, mustBeNew: false);
    }

    /// <summary>Remove this mod's resource / override for a key.</summary>
    [RequiresCapability(Caps.Service)]
    public static void RemoveResource(AssetKey key)
    {
        var t = Require();
        var k = MakeKey(key);
        t->ResourceRemove(Owner(), &k);
    }

    /// <summary>The bytes the game gets for `key` now (an override if any, else the game's own), or null if absent.</summary>
    [RequiresCapability(Caps.Resources)]
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
        Check(rc, "ReadResource", Caps.Resources);
        return buf;
    }

    /// <summary>The resource as a reflected struct: read any field by path, change one copy-on-write (see
    /// <see cref="ReflectedResource"/>). Null when the game has no such resource, and on a build where kiln.types or kiln.assets
    /// is not running (<see cref="ReflectedResources.Available"/>). Throws when kiln.types runs but the key's type is not a
    /// reflected type of this build. <see cref="ReflectedResources"/> finds resources by type and by the value of a field.</summary>
    public static ReflectedResource? Reflect(AssetKey key)
    {
        if (!ReflectedResources.Available) return null;
        return ReflectedResources.Get(key.Guid, GameTypes.ByHash(key.Type) ?? throw new ArgumentException($"no reflected type has the hash 0x{key.Type:x8}"), key.Part);
    }

    /// <summary>Every key of a type (the game's plus new ones).</summary>
    [RequiresCapability(Caps.Resources)]
    public static AssetKey[] ListResources(uint type)
    {
        var t = Require();
        nuint n = 0;
        t->ResourceList(type, null, 0, &n);
        var raw = new AssetsNative.Key[n];
        fixed (AssetsNative.Key* p = raw) Check(t->ResourceList(type, p, n, &n), "ListResources", Caps.Resources);
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
    // What each mod supplied through this class, for ListBlobs: kiln.assets has no blob listing of its own, so the managed side
    // keeps the record (and drops a mod's entries when it unloads, as kiln.assets drops its blobs).
    private sealed record BlobRecord(ulong Owner, string Mod, ContentHash Hash, BlobKind Kind);
    private static readonly List<BlobRecord> Blobs = new();

    private static void RecordBlob(ulong owner, string mod, ContentHash hash, BlobKind kind)
    {
        lock (Lock)
        {
            Blobs.RemoveAll(b => b.Owner == owner && b.Hash.Equals(hash));
            Blobs.Add(new BlobRecord(owner, mod, hash, kind));
        }
    }

    /// <summary>Add a new blob; the returned hash goes into a resource's content reference. kiln.assets records it as a
    /// managed registration (a kiln.assets older than 0.2.0-dev.28: as a native one).</summary>
    [RequiresCapability(Caps.Content)]
    public static ContentHash AddBlob(byte[] bytes)
    {
        var t = Require();
        var owner = Owner();
        AssetsNative.ContentHash h;
        var ex = TEx;
        fixed (byte* p = bytes)
            Check(ex != null && ex->BlobAddEx != null
                      ? ex->BlobAddEx(owner, p, (nuint)bytes.Length, &h, AssetsNative.OriginManaged)
                      : t->BlobAdd(owner, p, (nuint)bytes.Length, &h), "AddBlob", Caps.Content);
        var hash = new ContentHash(h.Size, h.H0, h.H1, h.H2);
        RecordBlob(owner, ModContext.CurrentMod!.Info.Id, hash, BlobKind.Added);
        return hash;
    }

    /// <summary>Serve `bytes` instead of the game's blob `hash` (bytes.Length must equal hash.Size). Recorded as a managed
    /// registration, as <see cref="AddBlob"/>.</summary>
    [RequiresCapability(Caps.Content)]
    public static void OverrideBlob(ContentHash hash, byte[] bytes)
    {
        var t = Require();
        var owner = Owner();
        var h = new AssetsNative.ContentHash { Size = hash.Size, H0 = hash.H0, H1 = hash.H1, H2 = hash.H2 };
        var ex = TEx;
        fixed (byte* p = bytes)
            Check(ex != null && ex->BlobOverrideEx != null
                      ? ex->BlobOverrideEx(owner, &h, p, (nuint)bytes.Length, AssetsNative.OriginManaged)
                      : t->BlobOverride(owner, &h, p, (nuint)bytes.Length), "OverrideBlob", Caps.Content);
        RecordBlob(owner, ModContext.CurrentMod!.Info.Id, hash, BlobKind.Override);
    }

    [RequiresCapability(Caps.Service)]
    public static void RemoveBlob(ContentHash hash)
    {
        var t = Require();
        var owner = Owner();
        var h = new AssetsNative.ContentHash { Size = hash.Size, H0 = hash.H0, H1 = hash.H1, H2 = hash.H2 };
        t->BlobRemove(owner, &h);
        lock (Lock) Blobs.RemoveAll(b => b.Owner == owner && b.Hash.Equals(hash));
    }

    /// <summary>
    /// The content blobs this mod added with <see cref="AddBlob"/> or overrode with <see cref="OverrideBlob"/> and has not
    /// removed, oldest first (EML's <c>get_all_contents</c>, for the mod's own blobs). Blobs a mod declares in assets.json
    /// are registered by the native side and are not listed; the game's own blobs (several hundred thousand) are not either.
    /// Call it from mod code.
    /// </summary>
    public static IReadOnlyList<BlobEntry> ListBlobs()
    {
        var owner = Owner();
        lock (Lock) return Blobs.Where(b => b.Owner == owner).Select(b => new BlobEntry(b.Hash, b.Kind, b.Mod)).ToList();
    }

    /// <summary>The blobs every mod has supplied through this class (see <see cref="ListBlobs"/>), oldest first. Any thread.</summary>
    public static IReadOnlyList<BlobEntry> ListAllBlobs()
    {
        lock (Lock) return Blobs.Select(b => new BlobEntry(b.Hash, b.Kind, b.Mod)).ToList();
    }

    // ---- textures (client) --------------------------------------------------------------------------------------
    /// <summary>Override a texture resource's pixels with a PNG, encoded to the texture's own format. Client only.</summary>
    [ClientOnly]
    [RequiresCapability(Caps.Textures)]
    public static void OverrideTexture(AssetKey key, byte[] png)
    {
        var t = Require();
        var owner = Owner();
        var k = MakeKey(key);
        fixed (byte* p = png) Check(t->TextureOverridePng(owner, &k, p, (nuint)png.Length), "OverrideTexture", Caps.Textures);
    }

    // ---- tools --------------------------------------------------------------------------------------------------
    /// <summary>Write resources / blobs to &lt;game&gt;/kiln/data/assets/dump/. See the guide for the spec grammar.</summary>
    [RequiresCapability(Caps.Service)]
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
    [RequiresCapability(Caps.Service)]
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
