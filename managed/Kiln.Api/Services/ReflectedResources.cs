// SPDX-License-Identifier: MIT
// The game's resources (ItemInfo, templates, buffs, balancing tables: every reflected struct it keeps resident) as typed,
// reflected values: read any field by path, find resources by a field's value, and change a field copy-on-write through
// kiln.assets. The types come from kiln.types (GameTypes), the bytes from kiln.assets. Writes are Overrides claims, so
// two mods that change different fields of one resource both keep their change, and a field two mods want gets a clear
// answer. Guide: docs/kiln/modding/typed-fields.md ("Any resource").
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>
/// Reflected resources: <see cref="Get(Guid, string, uint)"/> a resource by GUID and type, list a type's resources, and find
/// resources by the value of one of their fields. Any thread. Without kiln.assets and kiln.types (an unknown game build, a
/// client without Kiln) every lookup finds nothing.
/// </summary>
/// <remarks>Threads: a resource you get remembers the mod whose code got it, and its writes (<see cref="ReflectedResource.Set"/>,
/// the indexer, <see cref="ReflectedResource.Restore(string)"/>) are that mod's claims on any thread, given back when the mod
/// unloads. The mod running on the calling thread wins when there is one. A handle got on a background thread that no mod's
/// code runs on has no mod: writing through it there throws <see cref="InvalidOperationException"/> (it would otherwise be a
/// claim nobody gives back). Get the handle in OnLoad or a tick-thread callback, or post the write with Scheduler.Post.</remarks>
/// <example>
/// <code>
/// var sword = ReflectedResources.Get(Guid.Parse("1d622bd9-8170-41b1-822e-d3984b386d95"), "keen::ItemInfo")!;
/// Log.Info($"{sword["debugName"]} stacks to {sword["maxStackSize"]}");
/// sword["maxStackSize"] = 5;                                   // a copy-on-write override, undone when your mod unloads
/// foreach (var item in ReflectedResources.FindByField("keen::ItemInfo", "maxStackSize", 1)) { /* every unstackable item */ }
/// </code>
/// </example>
public static class ReflectedResources
{
    /// <summary>Whether resources can be read: kiln.assets (with its resource hooks on, row <c>assets.resources</c>) and
    /// kiln.types are both running on this build.</summary>
    public static bool Available => ResourceStore.Available && GameTypes.Available;

    /// <summary>The resource with this GUID and type (part 0 unless the type has parts), or null when the game has none.</summary>
    /// <param name="guid">The resource's GUID.</param>
    /// <param name="type">Its reflected type: "keen::ItemInfo", or a short name that is unique.</param>
    /// <param name="part">The part index; 0 for nearly every type.</param>
    /// <exception cref="ArgumentException">The type is unknown, or not a struct.</exception>
    public static ReflectedResource? Get(Guid guid, string type, uint part = 0) =>
        Available ? Get(guid, TypeNamed(type), part) : null;

    /// <summary><see cref="Get(Guid, string, uint)"/> with the type as a <see cref="GameType"/>.</summary>
    public static ReflectedResource? Get(Guid guid, GameType type, uint part = 0)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!Available) return null;
        var root = RootOf(type);
        var id = new ResourceId(guid, type.Hash, part);
        return ResourceStore.Exists(id) ? new ReflectedResource(id, root) : null;
    }

    /// <summary>Every resource of a type, the game's and the mods' new ones, in the order kiln.assets lists them. Lazy: the
    /// list is read when you start enumerating, and each resource is read when you look at it.</summary>
    public static IEnumerable<ReflectedResource> All(string type) => Available ? All(TypeNamed(type)) : [];

    /// <summary><see cref="All(string)"/> with the type as a <see cref="GameType"/>.</summary>
    public static IEnumerable<ReflectedResource> All(GameType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var root = RootOf(type);
        return Enumerate(root, type.Hash, ModContext.CurrentMod);   // the mod that asked, not whoever enumerates later
    }

    private static IEnumerable<ReflectedResource> Enumerate(GameType root, uint hash, ModContext? mod)
    {
        if (!Available) yield break;
        foreach (var id in ResourceStore.List(hash)) yield return new ReflectedResource(id, root, mod);
    }

    /// <summary>
    /// The resources of a type whose field <paramref name="path"/> equals <paramref name="value"/>. The value is checked like a
    /// write: an enum by name or number, a whole number for an integer, text for a text field, a GUID or its text for a
    /// reference. A resource where the path leads nowhere (an unset optional, an array too short) does not match.
    /// </summary>
    /// <exception cref="ArgumentException">The path is not a field of the type, or the value does not fit that field.</exception>
    public static IReadOnlyList<ReflectedResource> FindByField(string type, string path, object? value) =>
        Available ? FindByField(TypeNamed(type), path, value) : [];

    /// <summary><see cref="FindByField(string, string, object)"/> with the type as a <see cref="GameType"/>.</summary>
    public static IReadOnlyList<ReflectedResource> FindByField(GameType type, string path, object? value)
    {
        ArgumentNullException.ThrowIfNull(type);
        var root = RootOf(type);
        var found = new List<ReflectedResource>();
        if (!Available) return found;
        var buffer = new byte[4096];
        foreach (var id in ResourceStore.List(type.Hash))
        {
            if (!ResourceStore.TryRead(id, ref buffer, out var length)) continue;
            var data = buffer.AsSpan(0, length);
            try
            {
                var spot = ResourceLayout.Locate(data, root, path);
                if (ResourceLayout.Matches(data, spot, value, path)) found.Add(new ReflectedResource(id, root));
            }
            catch (Exception e) when (e is ResourcePathMiss or InvalidDataException)
            {
                // this resource has no such element (or damaged data): it does not match; the others are still searched
            }
        }
        return found;
    }

    private static GameType TypeNamed(string type)
    {
        ArgumentException.ThrowIfNullOrEmpty(type);
        return GameTypes.Find(type) ?? throw new ArgumentException($"unknown type '{type}'", nameof(type));
    }

    private static GameType RootOf(GameType type)
    {
        var root = type.Resolved;
        return root.Kind == GameTypeKind.Struct ? root : throw new ArgumentException($"{type.QualifiedName} is a {root.Kind}, not a struct: it is not a resource type");
    }
}

/// <summary>
/// One of the game's resources as a reflected struct: read fields by path, change them, put them back. A path is
/// <c>"maxStackSize"</c>, <c>"damageSetup.dmgMod"</c>, <c>"tags[2]"</c>; it goes through lists the game stores with the
/// resource (BlobArray), optional parts and variants as well as fixed arrays. The view holds no bytes: every read looks
/// at what the game gets now (your override, or another mod's, or the game's own).
/// </summary>
/// <remarks>
/// <para>What a read gives: <c>bool</c>, <c>long</c>, <c>ulong</c> (u64) and <c>double</c> for numbers, enums, flags and bools,
/// <c>string</c> for text, <see cref="Guid"/> for GUIDs and references. A struct or a list is read through its fields and
/// elements (<see cref="Count"/>, <see cref="Describe"/>).</para>
/// <para>What a write can change: numbers, enums, flags, bools, GUIDs and references, anywhere in the resource. The resource
/// keeps its size: text and lists are not made longer or shorter. A write is a claim (see <see cref="Overrides"/>): it
/// ends with <see cref="Restore(string)"/> or when your mod unloads. The game uses a changed resource when it next loads
/// it: for data it keeps resident, write in <c>OnLoad</c> (docs/kiln/services/assets.md).</para>
/// </remarks>
public sealed class ReflectedResource
{
    private readonly ResourceId _id;
    private readonly ModContext? _mod;   // the mod whose code got this handle: background-thread writes are its claims

    internal ReflectedResource(ResourceId id, GameType type, ModContext? mod = null)
    {
        _id = id;
        Type = type;
        _mod = mod ?? ModContext.CurrentMod;
    }

    internal ResourceId Id => _id;

    /// <summary>The resource's GUID.</summary>
    public Guid Guid => _id.Guid;

    /// <summary>The resource's part index (0 for nearly every type).</summary>
    public uint Part => _id.Part;

    /// <summary>The resource's reflected type.</summary>
    public GameType Type { get; }

    public override string ToString() => _id.Describe(Type);

    /// <summary>Whether the game (or a mod) still serves this resource.</summary>
    public bool Exists => ResourceStore.Exists(_id);

    /// <summary>A copy of the bytes the game gets for the resource now.</summary>
    /// <exception cref="KeyNotFoundException">The resource is gone.</exception>
    public byte[] Bytes => Snapshot();

    private byte[] Snapshot() =>
        ResourceStore.Read(_id) ?? throw new KeyNotFoundException($"{this} is not served any more");

    /// <summary>The value at <paramref name="path"/>, as <see cref="ReflectedResource"/> describes. Set: a checked write
    /// that goes through <see cref="Set"/>; throws <see cref="InvalidOperationException"/> when another mod holds the field.</summary>
    public object? this[string path]
    {
        get
        {
            var data = Snapshot();
            return ResourceLayout.Read(data, ResourceLayout.Locate(data, Type, path), path);
        }
        set
        {
            var result = Set(path, value);
            if (!result.Ok) throw new InvalidOperationException(result.Message);
        }
    }

    /// <summary>The value at <paramref name="path"/> converted to <typeparamref name="T"/>.</summary>
    public T Get<T>(string path) => (T)Convert.ChangeType(this[path]!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The value at <paramref name="path"/> before any change made through <see cref="Overrides"/> (what the game
    /// has, or had when the first change was made).</summary>
    public object? Original(string path)
    {
        var data = Overrides.OriginalBytes(_id) ?? Snapshot();
        return ResourceLayout.Read(data, ResourceLayout.Locate(data, Type, path), path);
    }

    /// <summary>The number of elements of the list or array at <paramref name="path"/>, or the length in bytes of a text.</summary>
    public int Count(string path)
    {
        var data = Snapshot();
        return ResourceLayout.Count(data, ResourceLayout.Locate(data, Type, path), path);
    }

    /// <summary>Whether the optional part at <paramref name="path"/> is present, or the variant holds a value.</summary>
    public bool IsSet(string path)
    {
        var data = Snapshot();
        return ResourceLayout.IsSet(data, ResourceLayout.Locate(data, Type, path), path);
    }

    /// <summary>The type a variant at <paramref name="path"/> holds (for example one component of a template), or null when empty.</summary>
    public GameType? VariantType(string path)
    {
        var data = Snapshot();
        return ResourceLayout.VariantOf(data, ResourceLayout.Locate(data, Type, path), path);
    }

    /// <summary>Every field with its value, one per line (<c>damageSetup.dmgMod = 1.5</c>), lists cut after
    /// <paramref name="maxArray"/> elements. The paths are the ones <see cref="this[string]"/> takes.</summary>
    public string Describe(int maxArray = 8) => ResourceLayout.Describe(Snapshot(), Type, Math.Max(0, maxArray));

    /// <summary>
    /// Changes one field (copy-on-write: the game gets the resource with your value in it). The value is checked like a
    /// <see cref="ReflectedValue"/> write; it throws <see cref="ArgumentException"/> and changes nothing when it does not
    /// fit. When another mod already changed this field the result is <see cref="OverrideStatus.Conflict"/>: nothing changes
    /// and a warning names both mods in the log.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called on a background thread for a handle that no mod's code got (see
    /// <see cref="ReflectedResources"/>).</exception>
    public OverrideResult Set(string path, object? value) => Overrides.SetField(_id, Type, path, value, _mod);

    /// <summary>Gives the field back what it had before this mod changed it. False when this mod holds no change there.</summary>
    public bool Restore(string path) => Overrides.RestoreField(_id, Type, path, _mod);

    /// <summary>Undoes every change this mod made to this resource. Returns how many fields were given back.</summary>
    public int RestoreAll() => Overrides.RestoreResource(_id, _mod);

    /// <summary>The id of the mod that changed the field at <paramref name="path"/> through <see cref="Overrides"/>, or null.</summary>
    public string? HeldBy(string path) => Overrides.FieldHolder(_id, Type, path);
}

/// <summary>A resource's identity in kiln.assets: GUID, type hash, part.</summary>
internal readonly record struct ResourceId(Guid Guid, uint Type, uint Part)
{
    public string Describe(GameType? type) => $"{type?.QualifiedName ?? $"type 0x{Type:x8}"} {Guid}" + (Part != 0 ? $" part {Part}" : "");
}

/// <summary>kiln.assets as Kiln.Api needs it (the resource calls only; the table's layout is the ABI,
/// native/core/abi/services/kiln_assets.h). Kiln.Assets has its own full mirror.</summary>
internal static unsafe class ResourceStore
{
    public const string Name = "kiln.assets";
    private const int Ok = 0, NotFound = 2, BufferTooSmall = 5;
    private const uint PutOverride = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Key
    {
        public fixed byte Guid[16];
        public uint Type;
        public uint Part;
    }

    /// <summary>The front of the kiln.assets table, up to resource_list.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Table
    {
        public ServiceHeader Header;
        public void* IconRegisterPng, IconRegisterFile, IconRemove, IconState;
        public delegate* unmanaged[Cdecl]<ulong, Key*, void*, nuint, uint, int> ResourcePut;
        public delegate* unmanaged[Cdecl]<ulong, Key*, int> ResourceRemove;
        public delegate* unmanaged[Cdecl]<Key*, void*, nuint, nuint*, int> ResourceRead;
        public delegate* unmanaged[Cdecl]<uint, Key*, nuint, nuint*, int> ResourceList;
    }

    private static Table* T => Services.Get<Table>(Name, 1);

    /// <summary>The row of kiln.assets' resource hooks (a sigdb feature, lowered by Kiln.Assets when they are off).</summary>
    public const string ResourcesRow = "assets.resources";

    /// <summary>Whether resources can be read and claimed: kiln.assets is here and its resource hooks are on (the table
    /// alone is not enough: kiln.assets publishes it with its hooks off too, and the test kit's table models content
    /// blobs only).</summary>
    public static bool Available => T != null && Capabilities.Is(ResourcesRow);

    private static Key MakeKey(ResourceId id)
    {
        Key k = default;
        id.Guid.TryWriteBytes(new Span<byte>(k.Guid, 16), bigEndian: false, out _);   // the game's byte order
        k.Type = id.Type;
        k.Part = id.Part;
        return k;
    }

    public static bool Exists(ResourceId id)
    {
        var t = T;
        if (t == null) return false;
        var k = MakeKey(id);
        nuint n = 0;
        return t->ResourceRead(&k, null, 0, &n) != NotFound;
    }

    /// <summary>Reads what the game gets for the resource into <paramref name="buffer"/> (grown when it is too small).</summary>
    public static bool TryRead(ResourceId id, ref byte[] buffer, out int length)
    {
        length = 0;
        var t = T;
        if (t == null) return false;
        var k = MakeKey(id);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            nuint n = 0;
            int rc;
            fixed (byte* p = buffer) rc = t->ResourceRead(&k, p, (nuint)buffer.Length, &n);
            if (rc == NotFound) return false;
            if (rc == Ok)
            {
                length = checked((int)n);
                return true;
            }
            if (rc != BufferTooSmall || n > int.MaxValue / 2) throw new InvalidOperationException($"kiln.assets: reading {id.Guid}: error {rc}");
            buffer = new byte[Math.Max((int)n, 16)];
        }
        throw new InvalidOperationException($"kiln.assets: {id.Guid} changed size while it was read");
    }

    public static byte[]? Read(ResourceId id)
    {
        var buffer = new byte[256];
        return TryRead(id, ref buffer, out var length) ? buffer.AsSpan(0, length).ToArray() : null;
    }

    public static ResourceId[] List(uint type)
    {
        var t = T;
        if (t == null) return [];
        for (var attempt = 0; attempt < 3; attempt++)
        {
            nuint n = 0;
            t->ResourceList(type, null, 0, &n);
            if (n > 8_000_000) throw new InvalidOperationException($"kiln.assets lists {n} resources of one type");
            var raw = new Key[n];
            int rc;
            fixed (Key* p = raw) rc = t->ResourceList(type, p, n, &n);
            if (rc == BufferTooSmall) continue;
            if (rc != Ok) throw new InvalidOperationException($"kiln.assets: listing type 0x{type:x8}: error {rc}");
            var list = new ResourceId[n];
            for (nuint i = 0; i < n; i++)
            {
                var bytes = new byte[16];
                fixed (byte* src = raw[i].Guid) Marshal.Copy((nint)src, bytes, 0, 16);
                list[i] = new ResourceId(new Guid(bytes), raw[i].Type, raw[i].Part);
            }
            return list;
        }
        return [];
    }

    /// <summary>Serves <paramref name="bytes"/> instead of the game's resource, for <paramref name="owner"/> (the call
    /// <c>Assets.Override</c> makes). The kiln.assets result code; 0 is taken.</summary>
    public static int Put(ulong owner, ResourceId id, byte[] bytes)
    {
        var t = T;
        if (t == null) return 3;
        var k = MakeKey(id);
        fixed (byte* p = bytes) return t->ResourcePut(owner, &k, p, (nuint)bytes.Length, PutOverride);
    }

    public static void Remove(ulong owner, ResourceId id)
    {
        var t = T;
        if (t == null) return;
        var k = MakeKey(id);
        t->ResourceRemove(owner, &k);
    }
}

/// <summary>A path that leads nowhere in this resource only because of its data (an unset optional, an array that is too
/// short, an empty variant): the type has the field, this resource just has no value there.</summary>
internal sealed class ResourcePathMiss(string message) : ArgumentException(message);

/// <summary>Walks a resource's bytes with the game's reflected layout: struct fields, fixed arrays and the blob kinds
/// (offsets relative to the field itself, docs/re/models.md). Pure functions over bytes and kiln.types data.</summary>
internal static class ResourceLayout
{
    internal readonly record struct Spot(GameType Type, int Offset);

    private static int I32(ReadOnlySpan<byte> d, long at, string path)
    {
        if (at < 0 || at > d.Length - 4) throw new InvalidDataException($"{path}: a reference points outside the resource");
        return BinaryPrimitives.ReadInt32LittleEndian(d[(int)at..]);
    }

    private static uint U32(ReadOnlySpan<byte> d, long at, string path)
    {
        if (at < 0 || at > d.Length - 4) throw new InvalidDataException($"{path}: a reference points outside the resource");
        return BinaryPrimitives.ReadUInt32LittleEndian(d[(int)at..]);
    }

    /// <summary>Finds a field by path. The result is the field's own type and offset (a variant or an optional is not entered).</summary>
    internal static Spot Locate(ReadOnlySpan<byte> d, GameType root, string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096) throw new ArgumentException("a field path is required", nameof(path));
        var t = root.Resolved;
        long off = 0;
        var throughVariant = false;   // below a variant the struct depends on the data: a missing field is a miss, not an error
        foreach (var part in path.Split('.'))
        {
            var name = part;
            var indexes = new List<int>();
            while (name.EndsWith(']') && name.LastIndexOf('[') is var b and > 0)
            {
                if (!int.TryParse(name[(b + 1)..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index))
                    throw new ArgumentException($"invalid array index in {path}");
                indexes.Insert(0, index);
                name = name[..b];
            }
            if (name.Length == 0) throw new ArgumentException($"{path} has an empty field name");
            (t, off) = Enter(d, t, off, path, ref throughVariant);
            var f = t.Field(name) ?? throw (throughVariant
                ? new ResourcePathMiss($"{path}: the {t.Name} this resource holds has no field '{name}'")
                : new ArgumentException($"{t.Name} has no field '{name}'"));
            if (f.Offset < 0 || f.Offset > t.Size || f.Type.Size > t.Size - f.Offset) throw new ArgumentException($"{path} lies outside {t.Name}");
            off += f.Offset;
            t = f.Type.Resolved;
            foreach (var i in indexes) (t, off) = Element(d, t, off, i, name, path);
        }
        // A struct of one field that holds a plain value (HashKey32 {value}, ProgramId) reads and writes as that value.
        for (var depth = 0; depth < 8 && t.Kind == GameTypeKind.Struct && SoleValueField(t) is { } sole; depth++)
        {
            off += sole.Offset;
            t = sole.Type.Resolved;
        }
        if (off < 0 || off > d.Length || t.Size < 0 || t.Size > d.Length - off)
            throw new InvalidDataException($"{path} lies outside the resource's {d.Length} bytes");
        return new Spot(t, (int)off);
    }

    private static GameField? SoleValueField(GameType t)
    {
        var fields = t.AllFields.Take(2).ToArray();
        if (fields.Length != 1) return null;
        var inner = fields[0].Type.Resolved;
        var plain = GameValueCodec.IsScalar(inner.Kind) || inner.Kind is GameTypeKind.Guid or GameTypeKind.ObjectReference or GameTypeKind.BlobString
            || (inner.Kind == GameTypeKind.Struct && SoleValueField(inner) != null);
        return plain ? fields[0] : null;
    }

    // Goes into what holds fields: an optional's value, a variant's concrete struct.
    private static (GameType, long) Enter(ReadOnlySpan<byte> d, GameType t, long off, string path, ref bool throughVariant)
    {
        for (var depth = 0; depth < 8; depth++)
        {
            switch (t.Kind)
            {
                case GameTypeKind.Struct:
                    return (t, off);
                case GameTypeKind.BlobOptional:
                {
                    var inner = t.Inner?.Resolved ?? throw new ArgumentException($"{t.Name} has no element type");
                    var rel = I32(d, off, path);
                    if (rel == 0) throw new ResourcePathMiss($"{path}: the optional is not set in this resource");
                    t = inner;
                    off += rel;
                    break;
                }
                case GameTypeKind.BlobVariant:
                {
                    var hash = U32(d, off, path);
                    if (hash == 0) throw new ResourcePathMiss($"{path}: the variant is empty in this resource");
                    var rel = I32(d, off + 4, path);
                    t = GameTypes.ByHash(hash)?.Resolved ?? throw new InvalidDataException($"{path}: the variant holds type 0x{hash:x8}, which this build does not know");
                    throughVariant = true;
                    off = off + 4 + rel;
                    break;
                }
                default:
                    throw throughVariant ? new ResourcePathMiss($"{path}: the variant holds a {t.Name} ({t.Kind}), which has no fields")
                        : new ArgumentException($"{t.Name} ({t.Kind}) has no fields");
            }
        }
        throw new ArgumentException($"{path} is nested too deeply");
    }

    private static (GameType, long) Element(ReadOnlySpan<byte> d, GameType t, long off, int index, string name, string path)
    {
        switch (t.Kind)
        {
            case GameTypeKind.StaticArray when t.Inner is { } e:
                if (index < 0 || index >= t.ArrayLength) throw new ArgumentOutOfRangeException(path, $"{name} has {t.ArrayLength} elements");
                return (e.Resolved, off + (long)index * e.Size);
            case GameTypeKind.BlobArray when t.Inner is { } e:
            {
                var rel = I32(d, off, path);
                var count = U32(d, off + 4, path);
                if (index < 0 || index >= count) throw new ResourcePathMiss($"{path}: {name} has {count} elements in this resource, not {index + 1}");
                var start = off + rel;
                if (e.Size <= 0 || start < 0 || start + (long)count * e.Size > d.Length) throw new InvalidDataException($"{path}: {name}'s elements lie outside the resource");
                return (e.Resolved, start + (long)index * e.Size);
            }
            default:
                throw new ArgumentException($"{name} is not an array");
        }
    }

    internal static object Read(ReadOnlySpan<byte> d, Spot s, string path)
    {
        var t = s.Type;
        if (GameValueCodec.IsScalar(t.Kind)) return GameValueCodec.Read(d.Slice(s.Offset, t.Size), t.Kind, t.Size, t.EnumValues);
        switch (t.Kind)
        {
            case GameTypeKind.BlobString:
                return TextAt(d, s.Offset, path);
            case GameTypeKind.Guid or GameTypeKind.ObjectReference when t.Size == 16:
                return new Guid(d.Slice(s.Offset, 16));
            case GameTypeKind.Struct:
                throw new ArgumentException($"{path} is a {t.Name}: read its fields (Describe lists them)");
            case GameTypeKind.StaticArray or GameTypeKind.BlobArray:
                throw new ArgumentException($"{path} is a list: read its elements as {path}[0] (Count gives how many)");
            case GameTypeKind.BlobOptional or GameTypeKind.BlobVariant:
                throw new ArgumentException($"{path} is an optional or variant part: read its fields as {path}.field (IsSet and VariantType say what it holds)");
            default:
                throw new NotSupportedException($"{path} is a {t.Kind}, which is not read as a value");
        }
    }

    private static string TextAt(ReadOnlySpan<byte> d, int at, string path)
    {
        var rel = I32(d, at, path);
        var len = U32(d, at + 4L, path);
        if (len == 0) return "";
        var start = at + (long)rel;
        if (start < 0 || len > d.Length || start > d.Length - len) throw new InvalidDataException($"{path}: the text lies outside the resource");
        return Encoding.UTF8.GetString(d.Slice((int)start, (int)len));
    }

    internal static int Count(ReadOnlySpan<byte> d, Spot s, string path)
    {
        switch (s.Type.Kind)
        {
            case GameTypeKind.StaticArray:
                return s.Type.ArrayLength;
            case GameTypeKind.BlobArray or GameTypeKind.BlobString:
            {
                var n = U32(d, s.Offset + 4L, path);
                return n > int.MaxValue ? throw new InvalidDataException($"{path}: its count is not valid") : (int)n;
            }
            default:
                throw new ArgumentException($"{path} is a {s.Type.Kind}, not a list or text");
        }
    }

    internal static bool IsSet(ReadOnlySpan<byte> d, Spot s, string path) => s.Type.Kind switch
    {
        GameTypeKind.BlobOptional => I32(d, s.Offset, path) != 0,
        GameTypeKind.BlobVariant => U32(d, s.Offset, path) != 0,
        _ => throw new ArgumentException($"{path} is a {s.Type.Kind}, not an optional or variant part"),
    };

    internal static GameType? VariantOf(ReadOnlySpan<byte> d, Spot s, string path)
    {
        if (s.Type.Kind != GameTypeKind.BlobVariant) throw new ArgumentException($"{path} is a {s.Type.Kind}, not a variant");
        var hash = U32(d, s.Offset, path);
        return hash == 0 ? null : GameTypes.ByHash(hash);
    }

    /// <summary>The bytes a write of <paramref name="value"/> makes at the spot. Throws and writes nothing when it does not fit.</summary>
    internal static byte[] Encode(Spot s, object? value, string path)
    {
        var t = s.Type;
        var bytes = new byte[t.Size];
        if (GameValueCodec.IsScalar(t.Kind))
        {
            GameValueCodec.Write(bytes, t.Kind, t.Size, value, t.EnumValues, path);
            return bytes;
        }
        if (t.Kind is GameTypeKind.Guid or GameTypeKind.ObjectReference && t.Size == 16)
        {
            var guid = value switch
            {
                Guid g => g,
                string text when Guid.TryParse(text, out var parsed) => parsed,
                byte[] { Length: 16 } raw => new Guid(raw),
                _ => throw new ArgumentException($"{path} takes a GUID (or its text), not {value ?? "null"}"),
            };
            guid.TryWriteBytes(bytes, bigEndian: false, out _);
            return bytes;
        }
        throw new NotSupportedException(t.Kind switch
        {
            GameTypeKind.BlobString => $"{path} is text: a resource keeps its size, so text is not rewritten in place",
            GameTypeKind.Struct => $"{path} is a {t.Name}: set its fields one by one",
            GameTypeKind.StaticArray or GameTypeKind.BlobArray => $"{path} is a list: set its elements one by one (a list keeps its length)",
            _ => $"{path} is a {t.Kind}, which is not written as a value",
        });
    }

    /// <summary>Whether the field at the spot equals <paramref name="value"/> (checked like a write: enums by name, GUIDs as text).</summary>
    internal static bool Matches(ReadOnlySpan<byte> d, Spot s, object? value, string path)
    {
        var t = s.Type;
        if (t.Kind == GameTypeKind.BlobString)
            return value is string wanted && string.Equals(TextAt(d, s.Offset, path), wanted, StringComparison.Ordinal);
        if (!GameValueCodec.IsScalar(t.Kind) && !(t.Kind is GameTypeKind.Guid or GameTypeKind.ObjectReference && t.Size == 16))
            throw new ArgumentException($"{path} is a {t.Kind}: find by one of its fields (numbers, enums, flags, text and GUIDs can be compared)");
        return d.Slice(s.Offset, t.Size).SequenceEqual(Encode(s, value, path));
    }

    internal static string Describe(ReadOnlySpan<byte> d, GameType root, int maxArray)
    {
        var sb = new StringBuilder();
        var lines = 0;
        var data = d.ToArray();
        void Line(string prefix, string text)
        {
            if (lines++ < 4000) sb.Append(prefix).Append(" = ").AppendLine(text);
        }
        void Walk(GameType type, long off, string prefix, int depth, bool unwrap = true)
        {
            if (lines >= 4000 || depth > 10) return;
            var t = type.Resolved;
            try
            {
                if (off < 0 || off > data.Length || t.Size > data.Length - off)
                {
                    Line(prefix, "<outside the resource>");
                    return;
                }
                var span = new ReadOnlySpan<byte>(data);
                switch (t.Kind)
                {
                    case var k when GameValueCodec.IsScalar(k):
                        Line(prefix, GameValueCodec.Format(GameValueCodec.Read(span.Slice((int)off, t.Size), k, t.Size, t.EnumValues), k, t.EnumValues));
                        break;
                    case GameTypeKind.BlobString:
                        Line(prefix, "\"" + TextAt(span, (int)off, prefix) + "\"");
                        break;
                    case GameTypeKind.Guid or GameTypeKind.ObjectReference when t.Size == 16:
                        Line(prefix, new Guid(span.Slice((int)off, 16)).ToString());
                        break;
                    case GameTypeKind.Struct when unwrap && prefix.Length > 0 && SoleValueField(t) is { } sole:
                        Walk(sole.Type, off + sole.Offset, prefix, depth + 1);        // a one-field struct shows as its value, as Locate reads it
                        break;
                    case GameTypeKind.Struct:
                        foreach (var f in t.AllFields) Walk(f.Type, off + f.Offset, prefix.Length == 0 ? f.Name : prefix + "." + f.Name, depth + 1);
                        break;
                    case GameTypeKind.StaticArray when t.Inner is { } e:
                        for (var i = 0; i < Math.Min(t.ArrayLength, maxArray); i++) Walk(e, off + (long)i * e.Size, $"{prefix}[{i}]", depth + 1);
                        break;
                    case GameTypeKind.BlobArray when t.Inner is { } e:
                    {
                        var count = U32(span, off + 4, prefix);
                        var rel = I32(span, off, prefix);
                        Line(prefix, $"[{count} elements]");
                        var start = off + rel;
                        if (count > 0 && (e.Size <= 0 || start < 0 || start + (long)count * e.Size > data.Length)) { Line(prefix, "<elements outside the resource>"); break; }
                        for (var i = 0; i < Math.Min(count, (uint)maxArray); i++) Walk(e, start + (long)i * e.Size, $"{prefix}[{i}]", depth + 1);
                        break;
                    }
                    case GameTypeKind.BlobOptional when t.Inner is { } inner:
                    {
                        var rel = I32(span, off, prefix);
                        if (rel == 0) Line(prefix, "<not set>");
                        else Walk(inner, off + rel, prefix, depth + 1, unwrap: false);   // the path to an optional ends at the optional
                        break;
                    }
                    case GameTypeKind.BlobVariant:
                    {
                        var hash = U32(span, off, prefix);
                        if (hash == 0) { Line(prefix, "<empty>"); break; }
                        var concrete = GameTypes.ByHash(hash);
                        Line(prefix, $"<{concrete?.Name ?? $"0x{hash:x8}"}>");
                        if (concrete != null) Walk(concrete, off + 4 + I32(span, off + 4, prefix), prefix, depth + 1, unwrap: false);
                        break;
                    }
                    default:
                        Line(prefix, $"<{t.Name}>");
                        break;
                }
            }
            catch (InvalidDataException e)
            {
                Line(prefix, $"<{e.Message}>");
            }
        }
        Walk(root, 0, "", 0);
        if (lines > 4000) sb.AppendLine($"... ({lines - 4000} more lines)");
        return sb.ToString();
    }
}
