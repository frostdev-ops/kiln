// SPDX-License-Identifier: MIT
// The game's own type registry, ECS systems, ECS events, and components by name, over kiln.types and kiln.world
// (native/core/abi/services/kiln_types.h, kiln_world.h). Guide: docs/kiln/services/types.md.
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Kiln.DevTools")]
[assembly: InternalsVisibleTo("Kiln.DevTools.Tests")]

namespace Kiln;

/// <summary>A field of a reflected struct.</summary>
public readonly record struct GameField(string Name, GameType Type, int Offset);

/// <summary>One of the game's reflected types (about 11,000 on the server, 12,600 in the game).</summary>
public sealed unsafe class GameType
{
    internal readonly void* Handle;
    private GameField[]? _fields;
    private (string Name, long Value)[]? _enum;

    internal GameType(void* handle, in TypesNative.TypeInfo info)
    {
        Handle = handle;
        Index = (int)info.Index;
        Name = info.Name.ToString();
        QualifiedName = info.QualifiedName.ToString();
        Size = (int)info.TypeSize;
        Alignment = info.Alignment;
        Kind = (GameTypeKind)info.Kind;
        Flags = info.Flags;
        Hash = info.Hash;
        FieldCount = (int)info.FieldCount;
        _inner = info.Inner;
        _defaults = info.Defaults == null ? null : new ReadOnlySpan<byte>(info.Defaults, Size).ToArray();
    }

    private readonly void* _inner;
    private readonly byte[]? _defaults;

    public int Index { get; }
    /// <summary>"Health".</summary>
    public string Name { get; }
    /// <summary>"keen::ecs::Health".</summary>
    public string QualifiedName { get; }
    public int Size { get; }
    public int Alignment { get; }
    public GameTypeKind Kind { get; }
    public byte Flags { get; }
    /// <summary>FNV-1a-32 of <see cref="QualifiedName"/>: the key of the game's component and event lookups.</summary>
    public uint Hash { get; }
    /// <summary>Own fields (struct) or values (enum, bitmask).</summary>
    public int FieldCount { get; }
    /// <summary>A struct's base class; an array's, optional's or typedef's element or target; else null.</summary>
    public GameType? Inner => _inner == null ? null : GameTypes.Of(_inner);
    /// <summary>A struct's base class, or null.</summary>
    public GameType? Base => Kind == GameTypeKind.Struct ? Inner : null;
    /// <summary>The type a typedef names (followed to the end), else this type.</summary>
    public GameType Resolved
    {
        get
        {
            var t = this;
            var seen = new HashSet<GameType>();
            while (t.Kind == GameTypeKind.Typedef)
            {
                if (!seen.Add(t) || seen.Count > 64 || t.Inner is not { } n || n.Size != t.Size)
                    throw new NotSupportedException($"invalid typedef chain in {QualifiedName}");
                t = n;
            }
            return t;
        }
    }
    /// <summary>The game's default value (<see cref="Size"/> bytes), or null when the type has none.</summary>
    public byte[]? Defaults => _defaults == null ? null : (byte[])_defaults.Clone();

    /// <summary>Own fields, in the game's order.</summary>
    public IReadOnlyList<GameField> Fields => _fields ??= LoadFields();

    /// <summary>Every field, base classes first.</summary>
    public IEnumerable<GameField> AllFields
    {
        get
        {
            var chain = new List<GameType>();
            var seen = new HashSet<GameType>();
            for (GameType? t = this; t is { Kind: GameTypeKind.Struct }; t = t.Base)
            {
                if (!seen.Add(t) || chain.Count == 64 || t.Size > Size)
                    throw new NotSupportedException($"invalid base class chain in {QualifiedName}");
                chain.Add(t);
            }
            return chain.AsEnumerable().Reverse().SelectMany(t => t.Fields)
                .Where(f => f.Offset >= 0 && f.Offset <= Size && f.Type.Size <= Size - f.Offset).ToArray();
        }
    }

    /// <summary>An enum's or bitmask's values (name, value).</summary>
    public IReadOnlyList<(string Name, long Value)> EnumValues => _enum ??= LoadEnum();

    /// <summary>A static array's element count (0 for other kinds).</summary>
    public int ArrayLength => Kind == GameTypeKind.StaticArray && Inner is { Size: > 0 } e ? Size / e.Size : 0;

    /// <summary>A field by name, base classes included.</summary>
    public GameField? Field(string name)
    {
        return AllFields.Reverse().Cast<GameField?>().FirstOrDefault(f => f!.Value.Name == name);
    }

    public override string ToString() => QualifiedName;

    private GameField[] LoadFields()
    {
        var t = GameTypes.Table;
        if (t == null || Kind != GameTypeKind.Struct) return [];
        var list = new List<GameField>(FieldCount);
        for (uint i = 0; i < FieldCount; i++)
        {
            TypesNative.TypeField f;
            if (t->TypeField(Handle, i, &f) != 0 || f.Type == null) continue;
            if (GameTypes.Of(f.Type) is { } ft && f.Offset <= (ulong)Size && (ulong)ft.Size <= (ulong)Size - f.Offset)
                list.Add(new GameField(f.Name.ToString(), ft, (int)f.Offset));
        }
        return list.ToArray();
    }

    private (string, long)[] LoadEnum()
    {
        var t = GameTypes.Table;
        if (t == null || Kind is not (GameTypeKind.Enum or >= GameTypeKind.Bitmask8 and <= GameTypeKind.Bitmask64)) return [];
        var list = new List<(string, long)>(FieldCount);
        for (uint i = 0; i < FieldCount; i++)
        {
            NativeStr n;
            long v;
            if (t->TypeEnum(Handle, i, &n, &v) != 0) break;
            list.Add((n.ToString(), v));
        }
        return list.ToArray();
    }
}

/// <summary>The game's reflected types: every name, size, field, base class, enum value and default value of the
/// running build, read from the game itself (kiln.types). Any thread.</summary>
public static unsafe class GameTypes
{
    private static readonly Dictionary<nint, GameType> Cache = new();

    internal static TypesNative.Table* Table => Services.Get<TypesNative.Table>(TypesNative.Name, TypesNative.Version);

    /// <summary>Whether the type table was found on this build.</summary>
    public static bool Available { get { var table = Table; return table != null && table->TypeCount() > 0; } }

    public static int Count { get { var table = Table; return table == null ? 0 : (int)table->TypeCount(); } }

    internal static GameType? Of(void* handle)
    {
        var table = Table;
        if (handle == null || table == null) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue((nint)handle, out var t)) return t;
            var info = new TypesNative.TypeInfo { Size = (uint)sizeof(TypesNative.TypeInfo) };
            if (table->TypeInfo(handle, &info) != 0 || info.TypeSize > int.MaxValue || info.FieldCount > 65536 ||
                info.Kind is < (byte)GameTypeKind.Bool or > (byte)GameTypeKind.Guid) return null;
            return Cache[(nint)handle] = new GameType(handle, info);
        }
    }

    /// <summary>By name: "keen::ecs::Health" exactly, or a short name ("Health", "ItemInfo") that is unique.</summary>
    public static GameType? Find(string name)
    {
        var t = Table;
        if (t == null || string.IsNullOrEmpty(name)) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        fixed (byte* p = bytes) return Of(t->TypeByName(new NativeStr { Ptr = p, Len = (nuint)bytes.Length }));
    }

    public static GameType? ByHash(uint hash)
    {
        var table = Table;
        return table == null ? null : Of(table->TypeByHash(hash));
    }

    /// <summary>The reflected type of a value (EML's <c>types.of</c>): a <see cref="ReflectedValue"/> (a component or an event
    /// copy), a <see cref="ReflectedResource"/>, a <see cref="GameField"/> (the field's type), or a <see cref="GameType"/>
    /// itself. Null for anything else.</summary>
    public static GameType? TypeOf(object? value) => value switch
    {
        ReflectedValue v => v.Type,
        ReflectedResource r => r.Type,
        GameField f => f.Type,
        GameType t => t,
        _ => null,
    };

    /// <summary>Every type, in the game's order.</summary>
    public static IEnumerable<GameType> All
    {
        get
        {
            var tbl = Table;
            var list = new List<GameType>(Count);
            if (tbl == null) return list;
            for (uint i = 0, n = tbl->TypeCount(); i < n; i++)
                if (Of(tbl->TypeAt(i)) is { } t) list.Add(t);
            return list;
        }
    }

    /// <summary>Types whose qualified name holds every word (any order, any case): <c>Search("player input")</c>.</summary>
    public static IEnumerable<GameType> Search(string words)
    {
        var w = (words ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(t => w.All(x => t.QualifiedName.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The game's ECS component types.</summary>
    public static IReadOnlyList<GameType> Components
    {
        get
        {
            var t = Table;
            var list = new List<GameType>();
            if (t == null) return list;
            for (uint i = 0, n = t->ComponentTypeCount(); i < n; i++)
                if (Of(t->ComponentTypeAt(i)) is { } g) list.Add(g);
            return list;
        }
    }

    /// <summary>The game's ECS event types.</summary>
    public static IReadOnlyList<GameType> Events
    {
        get
        {
            var t = Table;
            var list = new List<GameType>();
            if (t == null) return list;
            for (uint i = 0, n = t->EventTypeCount(); i < n; i++)
                if (Of(t->EventTypeAt(i)) is { } g) list.Add(g);
            return list;
        }
    }

    /// <summary>What kiln.types found, in words ("" when everything works).</summary>
    public static string Status
    {
        get
        {
            var t = Table;
            if (t == null) return "kiln.types is not running";
            var s = new TypesNative.Status { Size = (uint)sizeof(TypesNative.Status) };
            return t->GetStatus(&s) == 0 ? s.Detail.ToString() : "kiln.types did not answer";
        }
    }
}

/// <summary>One of the game's ECS systems, with its profiler numbers.</summary>
public sealed record GameSystem(int Index, string Name, ulong Rva, bool Enabled, ulong Calls, double Milliseconds, string World);

/// <summary>The game's ECS systems (the code it runs every step: stamina drain, durability loss, fall damage,
/// spawners...) and their profiling data (kiln.types). Only explicitly allowed systems can be disabled; switches
/// belong to the calling mod and end when it unloads. Reads work on any thread; mutations require the tick thread.</summary>
public static unsafe class GameSystems
{
    private static TypesNative.Table* Table => GameTypes.Table;
    private static readonly ConditionalWeakTable<ModContext, object> Owners = new();
    private sealed record NativeOwner(ulong Value);
    private static readonly ConditionalWeakTable<IKilnBackend, NativeOwner> ToolOwners = new();
    private static ulong CallerOwner => Services.CallerOwner is var owner && owner != 0 ? owner
        : ToolOwners.GetValue(Backend.Current, b => new NativeOwner(b.OwnerCreate("kiln.systems"))).Value;

    private static void TrackOwner()
    {
        if (ModContext.CurrentMod is not { } mod) return;
        Owners.GetValue(mod, m =>
        {
            var owner = m.Owner;
            m.Retiring += () =>
            {
                var table = Table;
                if (table != null && table->SystemReleaseOwner != null) table->SystemReleaseOwner(owner);
            };
            return new object();
        });
    }

    /// <summary>Whether switching and profiling work on this build.</summary>
    public static bool Available
    {
        get
        {
            var t = Table;
            var s = new TypesNative.Status { Size = (uint)sizeof(TypesNative.Status) };
            return t != null && t->GetStatus(&s) == 0 && s.Systems != 0;
        }
    }

    /// <summary>Every system, sorted by name.</summary>
    public static IReadOnlyList<GameSystem> All
    {
        get
        {
            var t = Table;
            if (t == null) return [];
            var n = t->SystemCount();
            var list = new List<GameSystem>((int)n);
            for (uint i = 0; i < n; i++)
                if (Get(i) is { } s) list.Add(s);
            return list;
        }
    }

    /// <summary>Systems whose name holds every word: <c>Search("fall damage")</c> finds fall_damage_infliction.</summary>
    public static IEnumerable<GameSystem> Search(string words)
    {
        var w = (words ?? "").Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return All.Where(s => w.All(x => s.Name.Contains(x, StringComparison.OrdinalIgnoreCase)));
    }

    public static GameSystem? Find(string name)
    {
        var t = Table;
        if (t == null || string.IsNullOrEmpty(name)) return null;
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        int i;
        fixed (byte* p = bytes) i = t->SystemFind(new NativeStr { Ptr = p, Len = (nuint)bytes.Length });
        return i < 0 ? null : Get((uint)i);
    }

    /// <summary>Claims or releases this caller's disabling switch. Other owners may still hold it off. Throws when
    /// the name is unknown, the system is protected, or switching is unavailable.</summary>
    [TickThreadOnly]
    public static void SetEnabled(string name, bool enabled)
    {
        Services.RequireTickThread("GameSystems.SetEnabled");
        var s = Find(name) ?? throw new ArgumentException($"no system named '{name}'");
        var table = Table;
        if (table == null) throw new NotSupportedException("kiln.types is not running");
        TrackOwner();
        var r = table->SystemSetEnabled(CallerOwner, (uint)s.Index, enabled);
        if (r != 0) throw new NotSupportedException($"systems cannot be switched on this build ({r})");
    }

    /// <summary>Releases this caller's switches. Returns the number of systems that actually turned on; another
    /// owner can still keep a system off.</summary>
    [TickThreadOnly]
    public static int TurnAllOn()
    {
        Services.RequireTickThread("GameSystems.TurnAllOn");
        var n = 0;
        foreach (var s in All.Where(s => !s.Enabled))
        {
            SetEnabled(s.Name, true);
            if (Find(s.Name)?.Enabled == true) n++;
        }
        return n;
    }

    /// <summary>On: starts the profiler from zero. Off: stops it, keeping the numbers.</summary>
    [TickThreadOnly]
    public static void Profile(bool on)
    {
        Services.RequireTickThread("GameSystems.Profile");
        var t = Table;
        if (t == null || t->Profile(on) != 0) throw new NotSupportedException("the system profiler is unavailable on this build");
    }

    public static bool Profiling => StatusOf().Profiling != 0;

    /// <summary>How long the profiler has run (or ran), seconds.</summary>
    public static double ProfileSeconds => StatusOf().ProfileSeconds;

    /// <summary>The systems that ran while profiling, costliest first. CPU time summed over every thread.</summary>
    public static IReadOnlyList<GameSystem> Stats() => All.Where(s => s.Calls > 0).OrderByDescending(s => s.Milliseconds).ToList();

    private static TypesNative.Status StatusOf()
    {
        var s = new TypesNative.Status { Size = (uint)sizeof(TypesNative.Status) };
        var table = Table;
        if (table != null) table->GetStatus(&s);
        return s;
    }

    private static GameSystem? Get(uint i)
    {
        var s = new TypesNative.SystemInfo { Size = (uint)sizeof(TypesNative.SystemInfo) };
        var table = Table;
        if (table == null || table->SystemInfo(i, &s) != 0) return null;
        var world = (s.WorldKinds & 4) == 0 ? "server" : (s.WorldKinds & 3) != 0 ? "both" : "client";
        return new GameSystem((int)s.Index, s.Name.ToString(), s.Rva, s.Enabled != 0, s.Calls, s.Ms, s.Calls == 0 ? "" : world);
    }
}

/// <summary>The game's ECS events of one step, and posting events into its queue (kiln.types). Tick thread.</summary>
[TickThreadOnly]
public static unsafe class GameEvents
{
    /// <summary>Every event of <paramref name="type"/> the server world produced in the step that just ended (call it
    /// in <c>Context.Tick.Post</c>). The handler gets a copy it may keep. Returns how many there were; -1 when the
    /// world has no stream for the type or events are unavailable.</summary>
    public static int Each(string type, Action<ReflectedValue> handler)
    {
        Services.RequireTickThread("GameEvents.Each");
        ArgumentNullException.ThrowIfNull(handler);
        var tbl = GameTypes.Table;
        if (tbl == null) return -1;
        var t = EventType(type);
        var world = ReflectedValue.World is var w && w != null ? w->ServerWorld() : 0;
        if (tbl == null || world == 0) return -1;
        var capture = new EventCopies(t.Size);
        var h = GCHandle.Alloc(capture);
        try
        {
            if (tbl->EventEach((nuint)world, t.Hash, &Collect, (void*)GCHandle.ToIntPtr(h)) != 0) return -1;
        }
        finally
        {
            h.Free();
        }
        if (capture.Error is { } error) throw new InvalidOperationException("invalid event data", error);
        foreach (var c in capture.Copies) handler(ReflectedValue.Owned(t, c));
        return capture.Copies.Count;
    }

    private sealed class EventCopies(int size)
    {
        public readonly int Size = size;
        public readonly List<byte[]> Copies = [];
        public Exception? Error;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Collect(void* data, uint size, void* user)
    {
        var capture = (EventCopies)GCHandle.FromIntPtr((nint)user).Target!;
        if (capture.Error != null) return;
        try
        {
            if (data == null || size < capture.Size || size > 0x10000 || capture.Copies.Count >= 1000000)
                throw new InvalidOperationException("invalid event size or count");
            capture.Copies.Add(new ReadOnlySpan<byte>(data, capture.Size).ToArray());
        }
        catch (Exception e) { capture.Error = e; } // Never propagate managed exceptions across the native callback.
    }

    /// <summary>Posts an event with the named fields set (checked; unnamed fields, including timeStamp, are 0). The game
    /// handles it at the start of the next step. False when its queue is full this step. Throws for an unknown type,
    /// a type the world has no stream for, or a value that does not fit.</summary>
    public static bool Post(string type, IReadOnlyDictionary<string, object?> fields)
    {
        Services.RequireTickThread("GameEvents.Post");
        ArgumentNullException.ThrowIfNull(fields);
        var tbl = GameTypes.Table;
        if (tbl == null) throw new NotSupportedException("kiln.types is not running");
        var t = EventType(type);
        if (t.Size <= 0 || t.Size >= 0x100) throw new ArgumentException($"{t} is too big to post");
        var v = ReflectedValue.Owned(t, new byte[t.Size]);
        foreach (var (k, val) in fields) v[k] = val;
        var world = ReflectedValue.World is var w && w != null ? w->ServerWorld() : 0;
        if (world == 0) throw new InvalidOperationException("no server world");
        var bytes = v.Bytes;
        int r;
        fixed (byte* p = bytes) r = tbl->EventPost((nuint)world, t.Hash, p, (uint)bytes.Length);
        return r switch
        {
            0 => true,
            5 => false,
            2 => throw new ArgumentException($"this world has no {t} stream"),
            1 => throw new InvalidOperationException($"kiln.types refused {t}: not the server world, or its stream does not match the reflected layout"),
            4 => throw new InvalidOperationException("GameEvents.Post: not on the world thread"),
            _ => throw new NotSupportedException($"events are unavailable on this build ({r})"),
        };
    }

    private static GameType EventType(string name)
    {
        var t = GameTypes.Find(name) ?? throw new ArgumentException($"unknown type '{name}'");
        if (GameTypes.Events.Any(e => e.Handle == t.Handle)) return t;
        throw new ArgumentException($"'{name}' is not an event type");
    }
}

/// <summary>Components by name with the game's own field layout, and checked writes (kiln.world + kiln.types).</summary>
[TickThreadOnly]
public static unsafe class Reflected
{
    /// <summary>The entity's component, or null when it has none. The view holds no pointer: every access looks the
    /// component up again, so it never goes stale (a gone entity throws). Tick thread.</summary>
    public static ReflectedValue? Component(EntityId entity, string component)
    {
        Services.RequireTickThread("Reflected.Component");
        if (GameTypes.Table == null || ReflectedValue.World == null) return null;
        var t = GameTypes.Find(component);
        if (t is not { Kind: GameTypeKind.Struct }) throw new ArgumentException($"unknown component type '{component}'");
        return ReflectedValue.ComponentPtr(entity.Value, t.Hash, t.Size) == null ? null : ReflectedValue.OfComponent(entity.Value, t);
    }

    /// <summary>Every entity that has the component (loaded entities only). Tick thread.</summary>
    public static IReadOnlyList<EntityId> All(string component)
    {
        Services.RequireTickThread("Reflected.All");
        var w = ReflectedValue.World;
        if (GameTypes.Table == null || w == null) return [];
        var t = GameTypes.Find(component) ?? throw new ArgumentException($"unknown component type '{component}'");
        var buf = new uint[4096];
        uint n;
        int r;
        fixed (uint* p = buf) r = w->Find(t.Hash, p, (uint)buf.Length, &n);
        if (r == 5)
        {
            if (n > 16777216) throw new InvalidOperationException("invalid entity count");
            buf = new uint[checked(n + 256)];
            fixed (uint* p = buf) r = w->Find(t.Hash, p, (uint)buf.Length, &n);
        }
        return r != 0 || n > buf.Length ? [] : buf.Take((int)n).Select(e => new EntityId(e)).ToList();
    }
}

/// <summary>A reflected struct (a component, an event copy): read and write fields by path, checked against the
/// game's types. Paths: <c>"dataStorage[0]"</c>, <c>"transform.position.x"</c>. Writes skip the game's dirty-marking:
/// some fields only reach players when the server next touches them.</summary>
public sealed unsafe class ReflectedValue
{
    private readonly uint _entity, _hash;
    private readonly byte[]? _owned;

    private ReflectedValue(GameType type, uint entity, uint hash, byte[]? owned)
    {
        Type = type;
        _entity = entity;
        _hash = hash;
        _owned = owned;
    }

    internal static ReflectedValue OfComponent(uint entity, GameType t) => new(t, entity, t.Hash, null);
    internal static ReflectedValue Owned(GameType t, byte[] bytes) => new(t, 0, 0, bytes);

    public GameType Type { get; }
    /// <summary>The entity of a component view (0 for copies).</summary>
    public EntityId Entity => new(_entity);

    /// <summary>A copy of the current bytes.</summary>
    [TickThreadOnly]
    public byte[] Bytes => _owned is { } o ? (byte[])o.Clone() : Span().ToArray();

    internal static WorldPrefix* World => Services.Get<WorldPrefix>("kiln.world", 1);

    internal static byte* ComponentPtr(uint entity, uint hash, int minimumSize)
    {
        Services.RequireTickThread("ReflectedValue");
        var w = World;
        if (w == null) return null;
        uint size;
        var result = (byte*)w->Component(entity, hash, &size);
        if (result != null && (minimumSize < 0 || size < minimumSize))
            throw new InvalidOperationException("the live component is smaller than its reflected type");
        return result;
    }

    private Span<byte> Span()
    {
        if (_owned != null) return _owned;
        Services.RequireTickThread("ReflectedValue");
        var p = ComponentPtr(_entity, _hash, Type.Size);
        if (p == null) throw new InvalidOperationException($"entity {_entity} no longer has {Type.Name}");
        return new Span<byte>(p, Type.Size);
    }

    /// <summary>A scalar field by path: bool, long, ulong or double; a nested struct as a <see cref="ReflectedValue"/>
    /// copy. Set: checked (range, enum by name or number); throws and writes nothing when the value does not fit.</summary>
    [TickThreadOnly]
    public object? this[string path]
    {
        get
        {
            var (t, off) = Resolve(path);
            var data = Span().Slice(off, t.Size);
            if (GameValueCodec.IsScalar(t.Kind)) return GameValueCodec.Read(data, t.Kind, t.Size, t.EnumValues);
            return Owned(t, data.ToArray());
        }
        set
        {
            var (t, off) = Resolve(path);
            if (!GameValueCodec.IsScalar(t.Kind)) throw new ArgumentException($"{path} is a {t.Kind}; set its fields one by one");
            Span<byte> tmp = stackalloc byte[8];
            GameValueCodec.Write(tmp, t.Kind, t.Size, value, t.EnumValues, path);
            tmp[..t.Size].CopyTo(Span().Slice(off, t.Size));
        }
    }

    [TickThreadOnly]
    public T Get<T>(string path) => (T)Convert.ChangeType(this[path]!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Every scalar field with its value, one per line (<c>dataStorage[0] = 20</c>).</summary>
    [TickThreadOnly]
    public string Describe(int maxArray = 16)
    {
        var sb = new System.Text.StringBuilder();
        var data = Bytes;
        void Walk(GameType t, int off, string prefix, int depth)
        {
            t = t.Resolved;
            if (depth > 8 || off < 0 || off > data.Length || t.Size > data.Length - off) return;
            if (GameValueCodec.IsScalar(t.Kind))
            {
                sb.Append(prefix).Append(" = ").AppendLine(GameValueCodec.Format(GameValueCodec.Read(data.AsSpan(off, t.Size), t.Kind, t.Size, t.EnumValues), t.Kind, t.EnumValues));
            }
            else if (t.Kind == GameTypeKind.Struct)
            {
                foreach (var f in t.AllFields) Walk(f.Type, off + f.Offset, prefix.Length == 0 ? f.Name : prefix + "." + f.Name, depth + 1);
            }
            else if (t.Kind == GameTypeKind.StaticArray && t.Inner is { } e)
            {
                for (var i = 0; i < Math.Min(t.ArrayLength, maxArray); i++) Walk(e, off + i * e.Size, $"{prefix}[{i}]", depth + 1);
            }
            else
            {
                sb.Append(prefix).Append(" = <").Append(t.Name).AppendLine(">");
            }
        }
        Walk(Type, 0, "", 0);
        return sb.ToString();
    }

    private (GameType Type, int Offset) Resolve(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 4096) throw new ArgumentException("a field path is required", nameof(path));
        var t = Type.Resolved;
        var off = 0;
        foreach (var part in path.Split('.'))
        {
            var name = part;
            var idx = new List<int>();
            while (name.EndsWith(']') && name.LastIndexOf('[') is var b and > 0)
            {
                if (!int.TryParse(name[(b + 1)..^1], System.Globalization.NumberStyles.None,
                                  System.Globalization.CultureInfo.InvariantCulture, out var index))
                    throw new ArgumentException($"invalid array index in {path}");
                idx.Insert(0, index);
                name = name[..b];
            }
            var f = t.Field(name) ?? throw new ArgumentException($"{t.Name} has no field '{name}'");
            if (f.Offset < 0 || f.Offset > t.Size || f.Type.Size > t.Size - f.Offset)
                throw new ArgumentException($"{path} lies outside {t.Name}");
            t = f.Type.Resolved;
            off = checked(off + f.Offset);
            foreach (var i in idx)
            {
                if (t.Kind != GameTypeKind.StaticArray || t.Inner is not { } e) throw new ArgumentException($"{name} is not an array");
                if (i < 0 || i >= t.ArrayLength) throw new ArgumentOutOfRangeException(path, $"{name} has {t.ArrayLength} elements");
                off = checked(off + checked(i * e.Size));
                t = e.Resolved;
            }
        }
        if (off < 0 || off > Type.Size || t.Size < 0 || t.Size > Type.Size - off) throw new ArgumentException($"{path} lies outside {Type.Name}");
        return (t, off);
    }
}

internal static unsafe class TypesNative
{
    public const string Name = "kiln.types";
    public const uint Version = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct TypeInfo
    {
        public uint Size, Index;
        public NativeStr Name, QualifiedName;
        public uint TypeSize;
        public ushort Alignment;
        public byte Kind, Flags;
        public uint Hash, FieldCount;
        public void* Inner, Defaults;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TypeField
    {
        public NativeStr Name;
        public void* Type;
        public ulong Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SystemInfo
    {
        public uint Size, Index;
        public NativeStr Name;
        public ulong Rva;
        public byte Enabled, WorldKinds;
        public ushort Reserved;
        public ulong Calls;
        public double Ms;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Status
    {
        public uint Size;
        public byte Types, Systems, Events, Profiling;
        public uint TypeCount, ComponentCount, EventTypeCount, SystemCount, SystemsOff;
        public double ProfileSeconds;
        public NativeStr Detail;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<Status*, int> GetStatus;
        public delegate* unmanaged[Cdecl]<uint> TypeCount;
        public delegate* unmanaged[Cdecl]<uint, void*> TypeAt;
        public delegate* unmanaged[Cdecl]<uint, void*> TypeByHash;
        public delegate* unmanaged[Cdecl]<NativeStr, void*> TypeByName;
        public delegate* unmanaged[Cdecl]<void*, TypeInfo*, int> TypeInfo;
        public delegate* unmanaged[Cdecl]<void*, uint, TypeField*, int> TypeField;
        public delegate* unmanaged[Cdecl]<void*, uint, NativeStr*, long*, int> TypeEnum;
        public delegate* unmanaged[Cdecl]<uint> ComponentTypeCount;
        public delegate* unmanaged[Cdecl]<uint, void*> ComponentTypeAt;
        public delegate* unmanaged[Cdecl]<uint> EventTypeCount;
        public delegate* unmanaged[Cdecl]<uint, void*> EventTypeAt;
        public delegate* unmanaged[Cdecl]<uint> SystemCount;
        public delegate* unmanaged[Cdecl]<uint, SystemInfo*, int> SystemInfo;
        public delegate* unmanaged[Cdecl]<NativeStr, int> SystemFind;
        public delegate* unmanaged[Cdecl]<ulong, uint, bool, int> SystemSetEnabled;
        public delegate* unmanaged[Cdecl]<bool, int> Profile;
        public delegate* unmanaged[Cdecl]<nuint, uint, delegate* unmanaged[Cdecl]<void*, uint, void*, void>, void*, int> EventEach;
        public delegate* unmanaged[Cdecl]<nuint, uint, void*, uint, int> EventPost;
        public delegate* unmanaged[Cdecl]<ulong, int> SystemReleaseOwner;
    }
}

/// <summary>The front of kiln.world's table (kiln_world.h) up to the entity functions this file uses.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WorldPrefix
{
    public ServiceHeader Header;
    public void* RegisterProgram, ProgramSlot, ProgramInfo, IterBegin, IterNext, CurrentEntity, CommandBuffer, QueuePush,
        QueueAdd, QueueRemove, MarkWritten, ReadOther, ComponentHash;
    public delegate* unmanaged[Cdecl]<uint, uint, uint*, void*> Component;
    public void* Exists, Transform, Position;
    public delegate* unmanaged[Cdecl]<uint, uint*, uint, uint*, int> Find;
    public delegate* unmanaged[Cdecl]<uint> EntityCount;
    public delegate* unmanaged[Cdecl]<nuint> ServerWorld;
}

/// <summary>kiln.log lines written as Kiln itself (no mod name), for Kiln's own tool assemblies. Mods use their
/// <c>Log</c>.</summary>
public static class KilnLog
{
    public static void Info(string text) => Backend.Current.Log(0, LogLevel.Info, text);
    public static void Warn(string text) => Backend.Current.Log(0, LogLevel.Warn, text);
}
