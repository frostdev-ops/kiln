// SPDX-License-Identifier: MIT
// The fake kiln.types (native/core/abi/services/kiln_types.h) under Kiln.Api's GameTypes: the game's type table as the
// Kiln.Game beside the tests was generated with it (its embedded layout data), so typed access (Kiln.Ecs), reflected
// components and World.WriteField find the layouts a mod was compiled against. Systems, the profiler and the game's
// event streams are not modelled.
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln.TestKit;

/// <summary>The types of the Kiln.Game assembly beside the tests: every struct, enum and array of the game's component
/// and event closure (about 1,500 of the game's 11,000 types), with the layout the mod was compiled against. Built
/// once per process; there is none when no Kiln.Game can be loaded (then kiln.types stays unavailable).</summary>
internal static unsafe class TypesFake
{
    public const string Service = "kiln.types";
    private const int KindStruct = 18, KindEnum = 12;

    internal sealed class Row
    {
        public required string QualifiedName, Name;
        public int Kind, Size, Inner = -1;
        public uint Hash;
        public string? InnerName;
        public List<(string Name, string Type, int Offset)> RawFields = [];
        public (nint Name, nuint NameLen, int Type, int Offset)[] Fields = [];
        public (nint Name, nuint NameLen, long Value)[] Values = [];
        public List<(string Name, long Value)> RawValues = [];
        public nint NamePtr, QualifiedPtr;
        public bool Component, Event;
    }

    private static Row[] _rows = [];
    private static readonly Dictionary<string, int> ByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, int> ByHash = new();
    private static readonly Dictionary<string, List<int>> ByShort = new(StringComparer.Ordinal);
    private static int[] _components = [], _events = [];
    private static nint _detail;
    private static nuint _detailLen;
    private static TypesNative.Table* _table;

    /// <summary>Whether a Kiln.Game with layout data was found.</summary>
    public static bool Available => _rows.Length > 0;

    public static string Source { get; private set; } = "";

#pragma warning disable CA2255  // a test kit: the table must be published before any Kiln service asks for one
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void ModuleInit()
    {
        try
        {
            if (Load() && Available) KitBackend.Instance.RegisterTable(Service, Create());
        }
        catch (Exception e) when (e is IOException or FormatException or BadImageFormatException or InvalidOperationException)
        {
            // no usable Kiln.Game: kiln.types stays unavailable, as before
            Source = $"Kiln.Game's layout data could not be read: {e.Message}";
        }
    }

    public static Row? Find(uint hash) => ByHash.TryGetValue(hash, out var i) ? _rows[i] : null;

    /// <summary>The short names of the game's components (the dump's component list).</summary>
    public static IEnumerable<string> ComponentNames() => _components.Select(i => _rows[i].Name);

    /// <summary>The type is a component of the game (the dump's component list).</summary>
    public static bool IsComponent(uint hash) => Find(hash) is { Component: true };

    /// <summary>An attribute: a component whose base chain runs through <c>keen::ecs::AttributeComponent</c>.</summary>
    public static bool IsAttribute(uint hash)
    {
        var row = Find(hash);
        for (var depth = 0; row is not null && depth < 16; depth++)
        {
            if (depth > 0 && row.QualifiedName == "keen::ecs::AttributeComponent") return true;
            row = row.Inner >= 0 ? _rows[row.Inner] : null;
        }
        return false;
    }

    private static bool Load()
    {
        var game = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Kiln.Game");
        if (game is null)
        {
            try
            {
                game = Assembly.Load(new AssemblyName("Kiln.Game"));
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                Source = "no Kiln.Game assembly beside the tests";
                return false;
            }
        }
        var types = Read(game, "Kiln.Game.types");
        if (types is null)
        {
            Source = "this Kiln.Game has no layout data (resource Kiln.Game.types)";
            return false;
        }
        Parse(types, Read(game, "Kiln.Game.layouts"));
        Source = $"the layouts of {game.GetName().Name} {game.GetName().Version} ({_rows.Length} types)";
        return true;
    }

    private static string? Read(Assembly assembly, string resource)
    {
        using var s = assembly.GetManifestResourceStream(resource);
        if (s is null) return null;
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // Kiln.Game.types: "qualified name \t kind \t size \t hash \t inner|- \t members", a struct's members
    // "name@offset:type", an enum's "name=value" (Kiln.Game's EcsLayoutCheck reads the same text).
    private static void Parse(string types, string? layouts)
    {
        var rows = new List<Row>();
        var header = false;
        foreach (var raw in types.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var p = line.Split('\t');
            if (!header)
            {
                if (p[0] != "kiln.game.types") throw new FormatException("no kiln.game.types header");
                header = true;
                continue;
            }
            if (p.Length < 5) throw new FormatException("bad line: " + line);
            var row = new Row
            {
                QualifiedName = p[0], Name = ShortName(p[0]), Kind = int.Parse(p[1], CultureInfo.InvariantCulture),
                Size = int.Parse(p[2], CultureInfo.InvariantCulture), Hash = Convert.ToUInt32(p[3], 16), InnerName = p[4] == "-" ? null : p[4],
            };
            foreach (var m in p.Skip(5))
            {
                int at = m.IndexOf('@'), colon = m.IndexOf(':'), eq = m.IndexOf('=');
                if (row.Kind == KindStruct && at > 0 && colon > at)
                    row.RawFields.Add((m[..at], m[(colon + 1)..], int.Parse(m[(at + 1)..colon], CultureInfo.InvariantCulture)));
                else if (row.Kind == KindEnum && eq > 0)
                    row.RawValues.Add((m[..eq], unchecked((long)decimal.Parse(m[(eq + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))));
            }
            ByName[row.QualifiedName] = rows.Count;
            ByHash.TryAdd(row.Hash, rows.Count);
            if (!ByShort.TryGetValue(row.Name, out var same)) ByShort[row.Name] = same = [];
            same.Add(rows.Count);
            rows.Add(row);
        }
        foreach (var row in rows)
        {
            row.NamePtr = Native(row.Name, out _);
            row.QualifiedPtr = Native(row.QualifiedName, out _);
            if (row.InnerName is { } inner && ByName.TryGetValue(inner, out var i)) row.Inner = i;
            row.Fields = [.. row.RawFields.Where(f => ByName.ContainsKey(f.Type)).Select(f => (Native(f.Name, out var n), n, ByName[f.Type], f.Offset))];
            row.Values = [.. row.RawValues.Select(v => (Native(v.Name, out var n), n, v.Value))];
        }
        _rows = [.. rows];
        // Kiln.Game.layouts: "S \t C# name \t layout \t game name \t size \t role \t ..." names the components and events.
        List<int> components = [], events = [];
        foreach (var raw in (layouts ?? "").Split('\n'))
        {
            var p = raw.TrimEnd('\r').Split('\t');
            if (p.Length < 6 || p[0] != "S" || !ByName.TryGetValue(p[3], out var i)) continue;
            if (p[5] == "component")
            {
                _rows[i].Component = true;
                components.Add(i);
            }
            else if (p[5] == "event")
            {
                _rows[i].Event = true;
                events.Add(i);
            }
        }
        _components = [.. components];
        _events = [.. events];
        _detail = Native("the test kit's type table: the layouts of the Kiln.Game beside the tests; no systems, no event streams", out _detailLen);
    }

    // "keen::ecs::Health" -> "Health"; a "::" inside <...> does not count.
    private static string ShortName(string qualified)
    {
        var depth = 0;
        for (var i = qualified.Length - 1; i > 0; i--)
        {
            if (qualified[i] == '>') depth++;
            else if (qualified[i] == '<') depth--;
            else if (depth == 0 && qualified[i] == ':' && qualified[i - 1] == ':') return qualified[(i + 1)..];
        }
        return qualified;
    }

    private static nint Native(string text, out nuint length)  // process lifetime, never freed
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var p = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        p[bytes.Length] = 0;
        length = (nuint)bytes.Length;
        return (nint)p;
    }

    // ---- the table ----

    private static nint Create()
    {
        if (_table != null) return (nint)_table;
        var t = (TypesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(TypesNative.Table));  // process lifetime, never freed
        t->Header = new ServiceHeader { Size = (uint)sizeof(TypesNative.Table), Version = TypesNative.Version };
        t->GetStatus = &GetStatus;
        t->TypeCount = &TypeCount;
        t->TypeAt = &TypeAt;
        t->TypeByHash = &TypeByHash;
        t->TypeByName = &TypeByName;
        t->TypeInfo = &TypeInfo;
        t->TypeField = &TypeField;
        t->TypeEnum = &TypeEnum;
        t->ComponentTypeCount = &ComponentTypeCount;
        t->ComponentTypeAt = &ComponentTypeAt;
        t->EventTypeCount = &EventTypeCount;
        t->EventTypeAt = &EventTypeAt;
        t->SystemCount = &SystemCount;
        t->SystemInfo = &SystemInfo;
        t->SystemFind = &SystemFind;
        // bool is not blittable in an UnmanagedCallersOnly signature: the fakes take the byte the C ABI passes
        t->SystemSetEnabled = (delegate* unmanaged[Cdecl]<ulong, uint, bool, int>)(delegate* unmanaged[Cdecl]<ulong, uint, byte, int>)&SystemSetEnabled;
        t->Profile = (delegate* unmanaged[Cdecl]<bool, int>)(delegate* unmanaged[Cdecl]<byte, int>)&Profile;
        t->EventEach = &EventEach;
        t->EventPost = &EventPost;
        t->SystemReleaseOwner = &SystemReleaseOwner;
        t->SystemHook = &SystemHook;
        t->SystemUnhook = &SystemUnhook;
        _table = t;
        return (nint)t;
    }

    // Switched off by the test, or no harness: the table answers as a build without a type table.
    private static bool On => FakeTables.Active(Service) is not null;

    private static void* Handle(int index) => index < 0 ? null : (void*)(nint)(index + 1);

    private static Row? RowOf(void* handle)
    {
        var i = (nint)handle - 1;
        return On && i >= 0 && i < _rows.Length ? _rows[i] : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetStatus(TypesNative.Status* s)
    {
        if (s == null || s->Size < (uint)sizeof(TypesNative.Status)) return FakeTables.InvalidArgument;
        var size = s->Size;
        *s = default;
        s->Size = size;
        if (!On) return FakeTables.Ok;
        s->Types = 1;
        s->TypeCount = (uint)_rows.Length;
        s->ComponentCount = (uint)_components.Length;
        s->EventTypeCount = (uint)_events.Length;
        s->Detail = new NativeStr { Ptr = (byte*)_detail, Len = _detailLen };
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint TypeCount() => On ? (uint)_rows.Length : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* TypeAt(uint index) => On && index < _rows.Length ? Handle((int)index) : null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* TypeByHash(uint hash) => On && ByHash.TryGetValue(hash, out var i) ? Handle(i) : null;

    // "keen::ecs::Health" exactly; a name without "::" as "keen::ecs::<name>", then as the only type of that short name.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* TypeByName(NativeStr name)
    {
        if (!On || name.Ptr == null) return null;
        var text = name.ToString();
        if (ByName.TryGetValue(text, out var i)) return Handle(i);
        if (text.Contains("::", StringComparison.Ordinal)) return null;
        if (ByName.TryGetValue("keen::ecs::" + text, out i)) return Handle(i);
        return ByShort.TryGetValue(text, out var same) && same.Count == 1 ? Handle(same[0]) : null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TypeInfo(void* type, TypesNative.TypeInfo* info)
    {
        if (info == null || info->Size < (uint)sizeof(TypesNative.TypeInfo)) return FakeTables.InvalidArgument;
        if (RowOf(type) is not { } row) return FakeTables.NotFound;
        var size = info->Size;
        *info = default;
        info->Size = size;
        info->Index = (uint)((nint)type - 1);
        info->Name = new NativeStr { Ptr = (byte*)row.NamePtr, Len = (nuint)Encoding.UTF8.GetByteCount(row.Name) };
        info->QualifiedName = new NativeStr { Ptr = (byte*)row.QualifiedPtr, Len = (nuint)Encoding.UTF8.GetByteCount(row.QualifiedName) };
        info->TypeSize = (uint)row.Size;
        info->Alignment = 1;  // the layout data does not carry it
        info->Kind = (byte)row.Kind;
        info->Hash = row.Hash;
        info->FieldCount = (uint)(row.Kind == KindStruct ? row.Fields.Length : row.Values.Length);
        info->Inner = Handle(row.Inner);
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TypeField(void* type, uint index, TypesNative.TypeField* field)
    {
        if (field == null) return FakeTables.InvalidArgument;
        if (RowOf(type) is not { } row || index >= row.Fields.Length) return FakeTables.NotFound;
        var f = row.Fields[index];
        field->Name = new NativeStr { Ptr = (byte*)f.Name, Len = f.NameLen };
        field->Type = Handle(f.Type);
        field->Offset = (ulong)f.Offset;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int TypeEnum(void* type, uint index, NativeStr* name, long* value)
    {
        if (name == null || value == null) return FakeTables.InvalidArgument;
        if (RowOf(type) is not { } row || index >= row.Values.Length) return FakeTables.NotFound;
        var v = row.Values[index];
        *name = new NativeStr { Ptr = (byte*)v.Name, Len = v.NameLen };
        *value = v.Value;
        return FakeTables.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint ComponentTypeCount() => On ? (uint)_components.Length : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* ComponentTypeAt(uint index) => On && index < _components.Length ? Handle(_components[index]) : null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint EventTypeCount() => On ? (uint)_events.Length : 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* EventTypeAt(uint index) => On && index < _events.Length ? Handle(_events[index]) : null;

    // ---- systems, the profiler, event streams: not modelled ----
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint SystemCount() => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemInfo(uint index, TypesNative.SystemInfo* info) => FakeTables.NotFound;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemFind(NativeStr name) => -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemSetEnabled(ulong owner, uint index, byte enabled) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Profile(byte on) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EventEach(nuint world, uint hash, delegate* unmanaged[Cdecl]<void*, uint, void*, void> callback, void* user) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int EventPost(nuint world, uint hash, void* data, uint size) => FakeTables.Unavailable;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemReleaseOwner(ulong owner) => FakeTables.Ok;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemHook(ulong owner, uint index, delegate* unmanaged[Cdecl]<void*, void*, void> before,
        delegate* unmanaged[Cdecl]<void*, void*, void> after, void* user, ulong* handle)
    {
        if (handle != null) *handle = 0;
        return FakeTables.Unavailable;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int SystemUnhook(ulong handle) => FakeTables.NotFound;
}
