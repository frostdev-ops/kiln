// SPDX-License-Identifier: MIT
// One read-only view over every registry and every other resource type the game has (kiln.registry.view,
// native/core/abi/services/kiln_registry_view.h; docs/kiln/modding/registries/views.md). Stable views are the registries:
// they read what the registry resolved, mapped and proven per game build, and are written through Registries.Change and
// Mod.OnRegistry as before. Advanced views are every other resource type of the running build (quests, NPC dialogs,
// materials, UI data), with Kiln names derived from the game's reflection and kept by Kiln's alias table across game updates:
// verified per game build, read-only. A field a registry covers says so (CoveredBy), the stable way to the same data.
// Since 0.2.0-dev.32 (dev.32 J1).
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Kiln.Registry;

/// <summary>How far a view's names and fields are promised (the tiers of docs/kiln/modding/tiers.md).</summary>
[Experimental("KILNX031")]
public enum RegistryViewTier
{
    /// <summary>A registry: Kiln's schema, mapped and proven per game build; survives game updates, frozen with API 1.0.0.</summary>
    Stable = 0,
    /// <summary>Derived from the game's reflection: verified per game build, a field refused per entry when its member no
    /// longer resolves; no promise across game updates beyond Kiln's alias table.</summary>
    Advanced = 1,
}

/// <summary>A field's value kind in a view.</summary>
[Experimental("KILNX031")]
public enum RegistryViewFieldType
{
    /// <summary>A whole number.</summary>
    Int,
    /// <summary>A number.</summary>
    Float,
    /// <summary>True or false.</summary>
    Bool,
    /// <summary>One Kiln name of a fixed set.</summary>
    Enum,
    /// <summary>Any of a fixed set of Kiln names.</summary>
    Flags,
    /// <summary>Seconds.</summary>
    Time,
    /// <summary>An entry of another view, by its Kiln id (or the GUID text when no view has it).</summary>
    Id,
    /// <summary>Text.</summary>
    Text,
    /// <summary>A list (see <see cref="RegistryViewField.Element"/>).</summary>
    List,
    /// <summary>A group of fields.</summary>
    Struct,
    /// <summary>A member the view cannot read in this release (<see cref="RegistryViewField.Reason"/> says why).</summary>
    Unread,
}

/// <summary>Whether a field can be read on this game build.</summary>
[Experimental("KILNX031")]
public enum RegistryViewFieldState
{
    /// <summary>An advanced field whose member resolves on this build.</summary>
    Readable,
    /// <summary>An advanced field of a kind the view cannot read (a variant, an optional, a configuration list).</summary>
    Unread,
    /// <summary>A game build removed its member: refused, the name never given to another member.</summary>
    Removed,
    /// <summary>A registry field mapped and proven on this build.</summary>
    Mapped,
    /// <summary>A registry field Kiln has no mapping for on this build.</summary>
    Unmapped,
    /// <summary>A registry field whose mapping is not proven on this build.</summary>
    Unproven,
}

/// <summary>One field of a view.</summary>
/// <param name="Path">Its Kiln path (<c>"xpReward"</c>, <c>"rewardItems[].count"</c>); read an element with an index
/// (<c>"rewardItems[2].count"</c>).</param>
/// <param name="Type">Its value kind.</param>
/// <param name="Element">A list's element kind (<see cref="RegistryViewFieldType.Struct"/>: its fields are rows
/// <c>"&lt;list&gt;[].&lt;field&gt;"</c>); null for other fields.</param>
/// <param name="Values">An enum's or flags' Kiln names.</param>
/// <param name="Ref">An id field: the view whose entries it names; null when none.</param>
/// <param name="ReadOnly">No edit takes it here (every advanced field; a registry's read-only fields).</param>
/// <param name="State">Whether it reads on this build.</param>
/// <param name="Reason">Why it does not, in Kiln words; null when it does.</param>
/// <param name="CoveredBy">The registry field over the same member (<c>"items.maxStack"</c>): the stable way to read and
/// write it; of several, the one that maps the member one to one, else the one whose own member it is (never a formula that
/// only reads it); null when none.</param>
/// <param name="Exact">CoveredBy holds the same number or true/false, one to one: not a value computed through a formula,
/// an enum's or flags' own Kiln names, or another conversion.</param>
[Experimental("KILNX031")]
public sealed record RegistryViewField(string Path, RegistryViewFieldType Type, RegistryViewFieldType? Element,
    IReadOnlyList<string> Values, string? Ref, bool ReadOnly, RegistryViewFieldState State, string? Reason, string? CoveredBy, bool Exact)
{
    /// <summary>It reads on this build (<see cref="RegistryViewFieldState.Readable"/> or <see cref="RegistryViewFieldState.Mapped"/>).</summary>
    public bool Readable => State is RegistryViewFieldState.Readable or RegistryViewFieldState.Mapped;
}

/// <summary>Every field of one entry as copied values, and the fields it refused with their reasons.</summary>
/// <param name="View">The view's name.</param>
/// <param name="Id">The entry.</param>
/// <param name="Fields">Kiln path to value (a list's element fields inside the list's value).</param>
/// <param name="Refused">Kiln path to the reason that field did not read for this entry.</param>
[Experimental("KILNX031")]
public sealed record RegistryViewSnapshot(string View, RegistryId Id, IReadOnlyDictionary<string, JsonNode?> Fields,
    IReadOnlyDictionary<string, string> Refused);

/// <summary>The views of the running game: every registry (stable) and every other resource type (advanced), under Kiln
/// names. Read-only; any thread (advanced reads wait for a data-pack application in progress: keep them off hot paths). The
/// first calls are slow (a stable view's first read prepares the registry's values: seconds; an advanced view's first call lists
/// its entries; a Snapshot decodes every list): make them off the game's thread. A call holds back no other. Since
/// 0.2.0-dev.32.</summary>
/// <remarks>
/// <para>Two tiers (<see cref="RegistryViewTier"/>): a <i>stable</i> view is one of Kiln's registries, mapped and proven per game
/// build; an <i>advanced</i> view is any other resource type of the running game (quests, NPC dialogs, materials, UI data), under
/// Kiln names that follow the game's reflection and are verified per build. A field that a registry covers names the stable way
/// to the same data (<see cref="RegistryViewField.CoveredBy"/>). To change data, use <see cref="Registry"/>: views never write.</para>
/// <para>Availability: the views open once the game's resources are loaded. Before that, every call throws
/// <see cref="KilnUnavailableException"/> with the reason; ask <see cref="Available"/> first, or catch it and try again later.</para>
/// <para>Reads: a field that cannot be read for one entry says so for that entry only (<see cref="RegistryView.TryRead"/>,
/// <see cref="RegistryViewSnapshot.Refused"/>); the other entries and fields are not affected.</para>
/// </remarks>
/// <example><code>
/// if (RegistryViews.Available &amp;&amp; RegistryViews.Find("quest") is { } quests)
///     foreach (RegistryId id in quests.Select("game:quest_*"))
///         if (quests.TryRead(id, "xpReward", out var xp, out var why)) Log.Info($"{id}: {xp}");
///         else Log.Info($"{id}: {why}");
/// </code></example>
/// <seealso cref="RegistryView"/>
/// <seealso cref="Registry"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/registries/views.md">Registry views</seealso>
[Experimental("KILNX031")]
public static class RegistryViews
{
    /// <summary>The native service and capability row (<c>kiln.registry.view</c>).</summary>
    public const string Service = "kiln.registry.view";

    /// <summary>Kiln serves the views here (they open once the game's resources are loaded: before that, calls throw
    /// <see cref="KilnUnavailableException"/> with the reason).</summary>
    [AnyThread, CapabilityGuard(Service)]
    public static bool Available => Backend() is not null;

    /// <summary>Every view, stable ones first.</summary>
    /// <exception cref="KilnUnavailableException">There are none yet (the game's resources are not loaded), or Kiln serves no views here.</exception>
    /// <example><code>
    /// foreach (RegistryView v in RegistryViews.All) Log.Info($"{v.Name}: {v.Tier}");
    /// </code></example>
    [AnyThread, RequiresCapability(Service)]
    public static IReadOnlyList<RegistryView> All
    {
        get
        {
            var json = Call(b => b.Views(out var text) is var rc ? (rc, text) : default, "views");
            var views = new List<RegistryView>();
            foreach (var v in (JsonArray?)json["views"] ?? [])
                views.Add(new(v!["name"]!.GetValue<string>(), v["tier"]!.GetValue<string>() == "stable" ? RegistryViewTier.Stable : RegistryViewTier.Advanced));
            return views;
        }
    }

    /// <summary>A view by its name (<c>"items"</c>, <c>"quest"</c>); null when this game build has none.</summary>
    /// <param name="name">The view's Kiln name, as <see cref="RegistryView.Name"/>.</param>
    /// <returns>The view, or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or empty.</exception>
    /// <exception cref="KilnUnavailableException">Kiln serves no views here yet (see <see cref="All"/>).</exception>
    [AnyThread, RequiresCapability(Service)]
    public static RegistryView? Find(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return All.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.Ordinal));
    }

    // ---- the native table, or a test's

    internal interface IBackend
    {
        int Views(out string text);
        int Fields(string view, out string text);
        int Read(string view, string id, string field, out string text);
        int Select(string view, string selector, out string text);
        int Entry(string view, string id, out string text);
    }

    /// <summary>A test's backend instead of the native table (null: the native one).</summary>
    internal static IBackend? Override;

    internal static IBackend? Backend() => Override ?? NativeBackend.Instance;

    internal const int Ok = 0, InvalidArgument = 1, NotFound = 2, Unavailable = 3;

    internal static JsonNode Call(Func<IBackend, (int Rc, string Text)> call, string what)
    {
        var backend = Backend() ?? throw new KilnUnavailableException(Service, "Kiln serves no registry views here");
        var (rc, text) = call(backend);
        return rc switch
        {
            Ok => JsonNode.Parse(text) ?? throw new InvalidOperationException($"registry view: no answer to {what}"),
            NotFound => throw new KeyNotFoundException(text),
            InvalidArgument => throw new ArgumentException(text),
            _ => throw new KilnUnavailableException(Service, text),
        };
    }

    private sealed unsafe class NativeBackend : IBackend
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Table
        {
            public ServiceHeader Header;
            public delegate* unmanaged[Cdecl]<byte*, nuint, nuint*, int> Views;
            public delegate* unmanaged[Cdecl]<NativeStr, byte*, nuint, nuint*, int> Fields;
            public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, NativeStr, byte*, nuint, nuint*, int> Read;
            public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, byte*, nuint, nuint*, int> Select;
            public delegate* unmanaged[Cdecl]<NativeStr, NativeStr, byte*, nuint, nuint*, int> Entry;
        }

        private static readonly object Gate = new();
        private static NativeBackend? _instance;
        private readonly Table* _t;
        private NativeBackend(Table* t) => _t = t;

        internal static NativeBackend? Instance
        {
            get
            {
                if (_instance is not null) return _instance;
                lock (Gate)
                {
                    if (_instance is not null) return _instance;
                    var t = (Table*)Kiln.Backend.Current.Service(Service, 1);
                    if (t == null || t->Header.Size < (uint)sizeof(Table) || t->Entry == null) return null;
                    return _instance = new NativeBackend(t);
                }
            }
        }

        // The answer and, on a refusal, its reason: both are copied text that may need a second, larger call.
        private static int Text(Func<nint, nuint, nint, int> call, out string text)
        {
            var buf = new byte[4096];
            while (true)
            {
                nuint len = 0;
                int rc;
                fixed (byte* b = buf) rc = call((nint)b, (nuint)buf.Length, (nint)(&len));
                if ((int)len < buf.Length)
                {
                    text = Encoding.UTF8.GetString(buf, 0, (int)len);
                    return rc;
                }
                buf = new byte[(int)len + 1024];
            }
        }

        private static int With(string a, Func<NativeStr, int> call)
        {
            var bytes = Encoding.UTF8.GetBytes(a);
            fixed (byte* p = bytes) return call(new NativeStr { Ptr = p, Len = (nuint)bytes.Length });
        }

        public int Views(out string text) => Text((b, c, l) => _t->Views((byte*)b, c, (nuint*)l), out text);
        public int Fields(string view, out string text)
        {
            string result = "";
            var rc = With(view, v => Text((b, c, l) => _t->Fields(v, (byte*)b, c, (nuint*)l), out result));
            text = result;
            return rc;
        }
        public int Read(string view, string id, string field, out string text)
        {
            string result = "";
            var rc = With(view, v => With(id, i => With(field, f => Text((b, c, l) => _t->Read(v, i, f, (byte*)b, c, (nuint*)l), out result))));
            text = result;
            return rc;
        }
        public int Select(string view, string selector, out string text)
        {
            string result = "";
            var rc = With(view, v => With(selector, s => Text((b, c, l) => _t->Select(v, s, (byte*)b, c, (nuint*)l), out result)));
            text = result;
            return rc;
        }
        public int Entry(string view, string id, out string text)
        {
            string result = "";
            var rc = With(view, v => With(id, i => Text((b, c, l) => _t->Entry(v, i, (byte*)b, c, (nuint*)l), out result)));
            text = result;
            return rc;
        }
    }
}

/// <summary>One view: a registry (<see cref="RegistryViewTier.Stable"/>) or another resource type of the game
/// (<see cref="RegistryViewTier.Advanced"/>). Holds no native state: every read asks Kiln again.</summary>
[Experimental("KILNX031")]
public sealed class RegistryView
{
    private IReadOnlyList<RegistryViewField>? _fields;
    internal RegistryView(string name, RegistryViewTier tier) => (Name, Tier) = (name, tier);

    /// <summary>The view's Kiln name.</summary>
    public string Name { get; }

    /// <summary>Its tier.</summary>
    public RegistryViewTier Tier { get; }

    /// <summary>Its fields on this game build (asked once, then kept: a build's types do not change while it runs).</summary>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public IReadOnlyList<RegistryViewField> Fields => _fields ??= LoadFields();

    /// <summary>A field by its Kiln path (an index reads as the list's row); null when the view has none.</summary>
    /// <param name="path">The field's Kiln path (<c>"xpReward"</c>, <c>"rewardItems[2].count"</c>).</param>
    /// <returns>The field's description (type, element, values, whether it reads on this build), or null.</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    /// <exception cref="KilnUnavailableException">Kiln serves no views here yet.</exception>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public RegistryViewField? Field(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var generic = RegistrySchema.Normalise(path);
        return Fields.FirstOrDefault(f => f.Path == generic);
    }

    /// <summary>The ids a selector names, in the view's order: an id, a case-insensitive glob over ids
    /// (<c>"game:quest_*"</c>, <c>"*"</c>), or an entry's debug name (a stable view reads it as a registry edit does:
    /// <c>"Material_T1_Wood_Logs"</c> is <c>game:material_t1_wood_logs</c>). The first call on an advanced view lists its
    /// entries, a stable view's first call prepares the registry's values: make it off the game's thread.</summary>
    /// <param name="selector">An id, a glob (<c>"game:quest_*"</c>, <c>"*"</c> for every entry), or an entry's debug name.</param>
    /// <returns>The matching ids, in the view's order; empty when nothing matches.</returns>
    /// <exception cref="ArgumentException"><paramref name="selector"/> is null or empty, or not a valid selector.</exception>
    /// <exception cref="KilnUnavailableException">Kiln serves no views here yet.</exception>
    /// <example><code>
    /// IReadOnlyList&lt;RegistryId&gt; woods = RegistryViews.Find("items")!.Select("game:material_t1_wood_*");
    /// </code></example>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public IReadOnlyList<RegistryId> Select(string selector = "*")
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        var json = RegistryViews.Call(b => (b.Select(Name, selector, out var text), text), "select");
        var ids = new List<RegistryId>();
        foreach (var id in json.AsArray())
            if (id is not null && RegistryId.TryParse(id.GetValue<string>(), out var parsed)) ids.Add(parsed);
        return ids;
    }

    /// <summary>An entry of this view by its id (nothing is read until a field is).</summary>
    /// <param name="id">The entry's Kiln id.</param>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    public RegistryViewEntry this[RegistryId id] => id.Value is null ? throw new ArgumentException("an empty registry id", nameof(id)) : new(this, id);

    /// <summary>An entry of this view by its id's text.</summary>
    /// <param name="id">The entry's Kiln id, as text (<c>"game:quest_intro"</c>).</param>
    /// <exception cref="ArgumentException">The text is not a Kiln id.</exception>
    public RegistryViewEntry this[string id] => this[new RegistryId(id)];

    /// <summary>One field of one entry as JSON (numbers, true/false, Kiln names, ids, text, lists). false and the reason in
    /// Kiln words when it does not read for this entry: the view, entry or field is not on this build, the member is of a
    /// kind the view cannot read, or this entry has no value of it. Other entries are not affected.</summary>
    /// <param name="id">The entry.</param>
    /// <param name="field">A Kiln path.</param>
    /// <param name="value">The field as JSON when it read; null otherwise (and null when the field's value is null: an id that names no entry).</param>
    /// <param name="reason">Why it did not read, in Kiln words; null when it did.</param>
    /// <returns>True when the field read.</returns>
    /// <exception cref="ArgumentException">The id is empty, <paramref name="field"/> is empty, or Kiln calls the path malformed.</exception>
    /// <exception cref="KilnUnavailableException">Kiln serves no views here yet.</exception>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public bool TryRead(RegistryId id, string field, out JsonNode? value, [NotNullWhen(false)] out string? reason)
    {
        var rc = ReadCore(id, field, out value, out var why);
        reason = rc == RegistryViews.Ok ? null : why;
        return rc == RegistryViews.Ok;
    }

    internal int ReadCore(RegistryId id, string field, out JsonNode? value, out string reason)
    {
        ArgumentException.ThrowIfNullOrEmpty(field);
        if (id.Value is null) throw new ArgumentException("an empty registry id", nameof(id));
        var backend = RegistryViews.Backend() ?? throw new KilnUnavailableException(RegistryViews.Service, "Kiln serves no registry views here");
        var rc = backend.Read(Name, id.Value, field, out var text);
        value = null;
        reason = "";
        if (rc == RegistryViews.Ok)
        {
            value = JsonNode.Parse(text);
            return rc;
        }
        if (rc == RegistryViews.InvalidArgument) throw new ArgumentException(text, nameof(field));
        reason = text.Length != 0 ? text : $"{Name}[{id.Value}].{field} did not read";
        return rc;
    }

    /// <inheritdoc/>
    public override string ToString() => $"{Name} ({Tier})";

    private IReadOnlyList<RegistryViewField> LoadFields()
    {
        var json = RegistryViews.Call(b => (b.Fields(Name, out var text), text), "fields");
        var list = new List<RegistryViewField>();
        foreach (var f in json["fields"]?.AsArray() ?? [])
        {
            if (f is null) continue;
            list.Add(new(f["path"]!.GetValue<string>(), Type(f["type"]?.GetValue<string>()),
                f["element"] is { } el ? Type(el.GetValue<string>()) : null,
                f["values"]?.AsArray().Select(x => x!.GetValue<string>()).ToArray() ?? [], f["ref"]?.GetValue<string>(),
                f["readOnly"]?.GetValue<bool>() ?? true, State(f["state"]?.GetValue<string>()), f["reason"]?.GetValue<string>(),
                f["coveredBy"]?.GetValue<string>(), f["exact"]?.GetValue<bool>() ?? false));
        }
        return list;
    }

    private static RegistryViewFieldType Type(string? t) => t switch
    {
        "int" => RegistryViewFieldType.Int,
        "float" => RegistryViewFieldType.Float,
        "bool" => RegistryViewFieldType.Bool,
        "enum" => RegistryViewFieldType.Enum,
        "flags" => RegistryViewFieldType.Flags,
        "time" => RegistryViewFieldType.Time,
        "id" => RegistryViewFieldType.Id,
        "text" => RegistryViewFieldType.Text,
        "list" => RegistryViewFieldType.List,
        "struct" => RegistryViewFieldType.Struct,
        _ => RegistryViewFieldType.Unread,
    };

    private static RegistryViewFieldState State(string? s) => s switch
    {
        "readable" => RegistryViewFieldState.Readable,
        "removed" => RegistryViewFieldState.Removed,
        "mapped" => RegistryViewFieldState.Mapped,
        "unmapped" => RegistryViewFieldState.Unmapped,
        "unproven" => RegistryViewFieldState.Unproven,
        _ => RegistryViewFieldState.Unread,
    };
}

/// <summary>One entry of a view. Every read asks Kiln again (nothing is cached), so a value follows the game's data.</summary>
[Experimental("KILNX031")]
public sealed class RegistryViewEntry
{
    internal RegistryViewEntry(RegistryView view, RegistryId id) => (View, Id) = (view, id);

    /// <summary>The view.</summary>
    public RegistryView View { get; }

    /// <summary>The entry's Kiln id.</summary>
    public RegistryId Id { get; }

    /// <summary>One field, typed: <see cref="int"/>, <see cref="long"/>, <see cref="float"/>, <see cref="double"/>,
    /// <see cref="bool"/>, <see cref="string"/> (text, an enum's Kiln name, an id's text), <see cref="string"/>[] (flags, a list of
    /// names), <see cref="RegistryId"/>, <see cref="TimeSpan"/> (a time), <see cref="JsonNode"/> (anything, lists of structs).
    /// <see cref="KilnUnavailableException"/> with the reason when the field does not read for this entry;
    /// <see cref="KeyNotFoundException"/> when the view has no such entry or field on this build. An id that names no entry
    /// reads as null into a reference or nullable type (<c>RegistryId?</c>, <see cref="string"/>, <see cref="JsonNode"/>) and
    /// as <see cref="InvalidCastException"/> into a value type.</summary>
    /// <param name="field">A Kiln path (<c>"xpReward"</c>, <c>"rewardItems[0].count"</c>).</param>
    /// <typeparam name="T">The type to read the value as (see above).</typeparam>
    /// <returns>The field's value.</returns>
    /// <exception cref="KeyNotFoundException">The view has no such entry or field on this build.</exception>
    /// <exception cref="KilnUnavailableException">The field does not read for this entry (the message says why), or Kiln serves no views here.</exception>
    /// <exception cref="InvalidCastException">The value is not a <typeparamref name="T"/>.</exception>
    /// <example><code>
    /// int stack = RegistryViews.Find("items")!["game:material_t1_wood_logs"].Get&lt;int&gt;("maxStack");
    /// </code></example>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public T Get<T>(string field)
    {
        var rc = View.ReadCore(Id, field, out var node, out var reason);
        if (rc == RegistryViews.NotFound) throw new KeyNotFoundException(reason);
        if (rc != RegistryViews.Ok) throw new KilnUnavailableException(RegistryViews.Service, reason);
        try
        {
            return Convert<T>(node);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or OverflowException)
        {
            throw new InvalidCastException($"{View.Name}[{Id.Value}].{field} is not a {typeof(T).Name}: {node?.ToJsonString() ?? "null"}", e);
        }
    }

    /// <summary>One field, typed as <see cref="Get{T}"/>; false and the reason when it does not read for this entry or does
    /// not convert to <typeparamref name="T"/>.</summary>
    /// <param name="field">A Kiln path.</param>
    /// <param name="value">The value.</param>
    /// <param name="reason">Why it did not read, in Kiln words.</param>
    /// <typeparam name="T">The type to read the value as (see <see cref="Get{T}"/>).</typeparam>
    /// <returns>True when the field read and converted.</returns>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public bool TryGet<T>(string field, [MaybeNullWhen(false)] out T value, [NotNullWhen(false)] out string? reason)
    {
        value = default;
        if (!View.TryRead(Id, field, out var node, out reason)) return false;
        try
        {
            value = Convert<T>(node);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException or ArgumentException or OverflowException)
        {
            reason = $"{View.Name}[{Id.Value}].{field} is not a {typeof(T).Name}: {node?.ToJsonString() ?? "null"}";
            return false;
        }
    }

    /// <summary>Every field of the entry at once: the values that read and, per field, why the others did not.</summary>
    /// <returns>A copy: later changes in the game do not reach it.</returns>
    /// <exception cref="KeyNotFoundException">The view has no such entry on this build.</exception>
    /// <exception cref="KilnUnavailableException">Kiln serves no views here yet.</exception>
    /// <remarks>This decodes every field, lists included, so it is slow: keep it off the game's thread and off hot paths.</remarks>
    [AnyThread, RequiresCapability(RegistryViews.Service)]
    public RegistryViewSnapshot Snapshot()
    {
        var json = RegistryViews.Call(b => (b.Entry(View.Name, Id.Value, out var text), text), "entry");
        var fields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (k, v) in json["fields"]?.AsObject() ?? []) fields[k] = v?.DeepClone();
        var refused = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in json["refused"]?.AsObject() ?? []) refused[k] = v?.GetValue<string>() ?? "";
        return new(View.Name, Id, fields, refused);
    }

    /// <inheritdoc/>
    public override string ToString() => $"{View.Name}[{Id.Value}]";

    private static T Convert<T>(JsonNode? node)
    {
        // JSON null (an id that names no entry): null for a reference or nullable type, a refusal for a value type
        if (node is null)
            return default(T) is null ? default! : throw new InvalidOperationException("no value");
        object? v = typeof(T) switch
        {
            var t when t == typeof(JsonNode) => node,
            var t when t == typeof(string) => node?.GetValue<string>(),
            var t when t == typeof(int) => checked((int)Whole(node)),
            var t when t == typeof(long) => Whole(node),
            var t when t == typeof(double) => node!.GetValue<double>(),
            var t when t == typeof(float) => (float)node!.GetValue<double>(),
            var t when t == typeof(bool) => node!.GetValue<bool>(),
            var t when t == typeof(TimeSpan) => TimeSpan.FromSeconds(node!.GetValue<double>()),
            var t when t == typeof(RegistryId) => new RegistryId(node!.GetValue<string>()),
            var t when t == typeof(RegistryId?) => node is null ? null : new RegistryId(node.GetValue<string>()),
            var t when t == typeof(string[]) => node!.AsArray().Select(x => x!.GetValue<string>()).ToArray(),
            _ => throw new InvalidCastException($"a registry view gives no {typeof(T).Name}"),
        };
        return (T)v!;
    }

    private static long Whole(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<long>(out var whole)) return whole;
        var d = node!.GetValue<double>();
        if (d != Math.Floor(d) || double.IsInfinity(d)) throw new FormatException("not a whole number");
        return checked((long)d);
    }
}
