// SPDX-License-Identifier: MIT
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Kiln;

/// <summary>
/// The game's content registries (Forge: registries + data packs): read and change items, recipes, random loot and
/// drop tables at run time. Managed face of the native service kiln.content
/// (native/core/abi/services/kiln_content.h; usage: docs/kiln/services/content.md).
/// </summary>
/// <remarks>
/// <para>Every change belongs to the mod that made it and is a layer over the game's original value: the newest layer
/// wins (data packs in mod load order, then code changes in call order), and when a mod unloads (or hot-reloads) its
/// layers come off again. Nothing is saved; after a restart the data packs apply again at world load, and code
/// changes made in <see cref="OnReady"/> / <see cref="ContentReadyEvent"/> run again.</para>
/// <para>Data packs: a mod ships <c>content/*.json</c> in its folder; kiln.content applies them when the world's
/// registries have loaded (before the save is read and players can join), in mod load order, reports conflicts, and
/// re-applies them when a file changes or the mod is loaded / unloaded.</para>
/// <para>Call everything on the tick thread (mod code runs there). Before <see cref="Ready"/> the registries are
/// empty.</para>
/// </remarks>
public static unsafe partial class Content
{
    private static ContentNative.Table* _table;
    private static readonly object Lock = new();
    private static readonly List<(ModContext Mod, Action<ContentReadyEvent> Handler)> ReadyHandlers = new();
    private static readonly HashSet<ModContext> Watched = new(ReferenceEqualityComparer.Instance);
    private static bool _initialized;
    private static string? _lastModList;

    internal static ContentNative.Table* T
    {
        get
        {
            if (_table == null)
            {
                _table = ContentNative.FromService((nint)Backend.Current.Service(ContentNative.Service, ContentNative.Version));
                if (_table != null) Init();
            }
            return _table;
        }
    }

    // The service's start (ServiceTick.AutoStart runs this at the first service tick): the table, then the rows.
    static Content()
    {
        _ = T;
        ContentCaps.Rows.Ensure();
    }

    /// <summary>The native service is loaded (capability <c>kiln.content</c>).</summary>
    public static bool Available => T != null;

    /// <summary>The registries are loaded and the data packs applied: the question for the row <c>content.ready</c>, true
    /// only when <c>kiln.content</c> is here.</summary>
    [AnyThread]
    public static bool Ready => Status()?.Ready == true;

    // One key per change of one field (or document) by one mod: a handler that writes again waits for the next step.
    internal static readonly ChangeNotices<ContentChangedEvent, (ContentChange, ContentKind?, uint, string, string)> Notices =
        new("kiln.content");

    /// <summary>Posts <see cref="ContentChangedEvent"/> for a change made from code that took. Built only while someone
    /// subscribes; a write made while one is being dispatched posts at the next step (<see cref="ChangeNotices{TEvent,TKey}"/>).</summary>
    internal static void Changed(ContentChange change, ContentEntry? entry, string path)
    {
        if (!Events.HasSubscribers<ContentChangedEvent>()) return;
        var origin = ModContext.CurrentMod?.Info.Id ?? "";
        Notices.Post((change, entry?.Kind, entry?.Id ?? 0, path, origin),
            new ContentChangedEvent(change, entry?.Kind, entry?.Id ?? 0, entry?.Name ?? "", path, origin));
    }

    /// <summary>Items (<c>keen::ItemInfo</c>): id = ItemId, name = debug name (<c>Material_T1_Wood_Logs</c>).</summary>
    public static ContentRegistry Items { get; } = new(ContentKind.Items);

    /// <summary>Recipes (<c>keen::RecipeInfo</c>): id = RecipeId, name = the recipe's debug name.</summary>
    public static ContentRegistry Recipes { get; } = new(ContentKind.Recipes);

    /// <summary>Random loot per item (<c>keen::LootableItemInfo</c>): id = the item's ItemId.</summary>
    public static ContentRegistry Loot { get; } = new(ContentKind.Loot);

    /// <summary>Drop tables (default inventories of enemies and fixed chests), as the game has used them so far.</summary>
    public static ContentRegistry Drops { get; } = new(ContentKind.Drops);

    public static ContentRegistry Registry(ContentKind kind) => kind switch
    {
        ContentKind.Items => Items,
        ContentKind.Recipes => Recipes,
        ContentKind.Loot => Loot,
        ContentKind.Drops => Drops,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The service's state, or null when it is not loaded.</summary>
    public static ContentStatus? Status()
    {
        var t = T;
        if (t == null || t->GetStatus == null) return null;  // no status function: a test's partial table
        ContentNative.Status s = default;
        s.Size = (uint)sizeof(ContentNative.Status);
        if (t->GetStatus(&s) != ContentNative.Ok) return null;
        return new ContentStatus(s.Ready != 0, (s.Registries & 1) != 0, (s.Registries & 2) != 0, (s.Registries & 4) != 0,
            (s.Registries & 8) != 0, s.StackGuard != 0, (int)s.Items, (int)s.Recipes, (int)s.Loot, (int)s.Drops,
            (int)s.Packs, (int)s.PackErrors, (int)s.Conflicts, (int)s.Changed, s.Generation,
            ContentNative.Text(s.Detail, 160)) { CountedBy = NewContent.Counts };
    }

    /// <summary>
    /// Runs <paramref name="handler"/> as the calling mod every time the registries are (re)applied: at world load
    /// after the data packs, and after each data-pack reload. If the world has already loaded, it runs at the next
    /// tick. Code changes made there are owned by the mod and come off when it unloads. Dropped when the mod unloads.
    /// </summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static void OnReady(Action<ContentReadyEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException("Content.OnReady must be called from mod code");
        var t = Require("Content.OnReady");
        Track(mod);
        lock (Lock) ReadyHandlers.Add((mod, handler));
        if (Ready)
        {
            // Loaded after the world: first make sure this mod's own data packs are in (the mod list changed), then run
            // the handler. When a pack re-application is queued, Dispatch calls the handler right after it.
            mod.Scheduler.Post(() =>
            {
                SyncMods();
                var st = Native();
                if (st.ReloadPending != 0) return;
                lock (Lock)
                    if (!ReadyHandlers.Contains((mod, handler))) return;
                handler(new ContentReadyEvent(st.Generation, false));
            });
        }
    }

    // ------------------------------------------------------------------------------------------- data packs ----

    /// <summary>Applies a data-pack document (the <c>content/*.json</c> format) as the calling mod's layers.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static ContentReport Apply(string json, string? source = null) =>
        ApplyAs(Owner(), json, source ?? $"mod {ModContext.CurrentMod!.Info.Id}");

    /// <summary>Applies a data-pack document as a given owner's layers (Kiln.Blocks: a data-only mod's blocks).</summary>
    internal static ContentReport ApplyAs(ulong owner, string json, string src)
    {
        var t = Require("Content.Apply");
        // Applying twice is not the same as applying once, so the report gets one chance: room for a thousand errors. Most reports are short, and the buffer is shared.
        const int cap = 1 << 21;
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(cap);
        nuint len = 0;
        int rc;
        string text;
        try
        {
            using (var j = new ContentNative.Utf8(json))
            using (var s = new ContentNative.Utf8(src))
                fixed (byte* b = buf)
                    rc = t->ApplyJson(owner, j.Str, s.Str, b, cap, &len);
            text = System.Text.Encoding.UTF8.GetString(buf, 0, (int)Math.Min(len, (nuint)(cap - 1)));
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
        if (rc != ContentNative.Ok && rc != ContentNative.InvalidArgument) throw Failure("Content.Apply", rc);
        var report = ParseReport(text, rc == ContentNative.InvalidArgument ? "not a data-pack document (JSON object)" : null,
            cut: len >= cap);
        // native says Ok also for a document whose every section was refused: an event only when something changed
        if (rc == ContentNative.Ok && report.Applied > 0) Changed(ContentChange.Applied, null, src);
        return report;
    }

    /// <summary>Applies a JSON file (relative paths: from the calling mod's folder).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static ContentReport ApplyFile(string path)
    {
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException("Content.ApplyFile must be called from mod code");
        var full = Path.IsPathRooted(path) ? path : Path.Combine(mod.Info.Directory, path);
        return Apply(File.ReadAllText(full), $"mod {mod.Info.Id}: {Path.GetFileName(full)}");
    }

    /// <summary>Takes every layer of the calling mod off (data packs of its folder stay; they follow the mod's load
    /// state). Returns how many fields were put back. Also happens automatically when the mod unloads.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static int Revert()
    {
        var t = Require("Content.Revert");
        var n = (int)t->RevertOwner(Owner());
        if (n > 0) Changed(ContentChange.Reverted, null, "");
        return n;
    }

    /// <summary>Re-reads every mod's <c>content/*.json</c> now.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static void ReloadPacks()
    {
        var t = Require("Content.ReloadPacks");
        var rc = t->ReloadPacks();
        if (rc != ContentNative.Ok) throw Failure("Content.ReloadPacks", rc);
    }

    /// <summary>The last data-pack application as JSON: <c>{generation, packs:[{file, applied, errors, warnings}],
    /// skipped, conflicts:[...]}</c>.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static string PackReport()
    {
        var t = Require("Content.PackReport");
        return ContentNative.ReadText(t->Report);
    }

    /// <summary>Writes every registry (schema, entries with their non-zero fields, recipe lists, drop stacks, what
    /// changed and its original) as JSON. Default: <c>kiln/data/content/dump.json</c>. Returns the path. Takes about a
    /// second for the whole item registry (the server step waits).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public static string Dump(string? path = null)
    {
        var t = Require("Content.Dump");
        var buf = stackalloc byte[1024];
        nuint len = 0;
        using var p = new ContentNative.Utf8(path ?? "");
        var rc = t->Dump(p.Str, buf, 1024, &len);
        if (rc != ContentNative.Ok) throw Failure("Content.Dump", rc);
        return ContentNative.Text(buf, 1024);
    }

    /// <summary>
    /// The stack guard: while on, the game no longer cuts stacks down to their item's max stack size (its
    /// "Deleted N items from stack" check). kiln.content switches it on by itself as soon as a max stack size is raised,
    /// so taking the raise back (unload, reload) does not delete items; it stays on until the server restarts.
    /// </summary>
    [RequiresCapability("kiln.content")]
    public static bool StackGuard
    {
        get
        {
            var t = T;
            if (t == null) return false;
            byte on = 0;
            t->StackGuard(-1, &on);
            return on != 0;
        }
        [TickThreadOnly] set
        {
            var t = Require("Content.StackGuard");
            byte on = 0;
            var rc = t->StackGuard(value ? 1 : 0, &on);
            if (rc != ContentNative.Ok) throw Failure("Content.StackGuard", rc);
        }
    }

    // ------------------------------------------------------------------------------------------- internals -----

    private static ContentNative.Status Native()
    {
        ContentNative.Status s = default;
        s.Size = (uint)sizeof(ContentNative.Status);
        if (T != null) T->GetStatus(&s);
        return s;
    }

    internal static ContentNative.Table* Require(string api)
    {
        Services.RequireTickThread(api);
        var t = T;
        if (t == null) throw ContentCaps.Off(ContentCaps.Service, "kiln.content is not available on this server");
        return t;
    }

    internal static Exception Failure(string api, int rc) =>
        rc == ContentNative.InvalidArgument ? new ArgumentException($"{api}: {ContentNative.Message(rc)}")
        : rc == ContentNative.NotFound ? new KeyNotFoundException($"{api}: {ContentNative.Message(rc)}")
        : new InvalidOperationException($"{api}: {ContentNative.Message(rc)}");

    /// <summary>The calling mod's owner (changes are attributed to it and reverted when it unloads).</summary>
    internal static ulong Owner()
    {
        var mod = ModContext.CurrentMod ?? throw new InvalidOperationException("content changes must be made from mod code");
        Track(mod);
        return mod.Owner;
    }

    private static void Track(ModContext mod)
    {
        lock (Lock)
        {
            if (!Watched.Add(mod)) return;
        }
        var t = T;
        if (t != null)
        {
            var label = $"mod {mod.Info.Id}";
            using var s = new ContentNative.Utf8(label);
            t->LabelOwner(mod.Owner, s.Str);
        }
        mod.Retiring += () =>
        {
            lock (Lock)
            {
                Watched.Remove(mod);
                ReadyHandlers.RemoveAll(h => ReferenceEquals(h.Mod, mod));
            }
            var tt = T;
            if (tt != null) tt->RevertOwner(mod.Owner);  // Retire runs on the tick thread
        };
    }

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        ulong h = 0;
        _table->SubscribeReady(0, &OnNativeReady, null, &h);
        ServiceTick.Add("kiln content packs", static (in TickInfo tick) =>
        {
            if (tick.Step % 25 == 0 || _lastModList == null) SyncMods();
            MissingContent.Tick(tick.Step);
            if (tick.Step % 25 == 0) DefinitionsExport.Tick();  // kiln/state/content-definitions.json (launcher)
        });
        ContentSync.Start();  // server -> client content sync (ContentSync.cs, docs/kiln/sync.md)
    }

    /// <summary>Tells the native service which mods are loaded, in load order: their data packs apply, others not.</summary>
    private static void SyncMods()
    {
        if (ModsBackend.Current is null || _table == null) return;
        var ids = Kiln.Mods.All.Where(m => m.State == ModState.Loaded).OrderBy(m => m.LoadIndex).Select(m => m.Id).ToArray();
        var json = JsonSerializer.Serialize(ids);
        if (json == _lastModList) return;
        _lastModList = json;
        using var s = new ContentNative.Utf8(json);
        _table->PackMods(s.Str);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeReady(ulong generation, uint reason, void* user)
    {
        try
        {
            Dispatch(generation, reason == 1);
        }
        catch (Exception e)
        {
            try { Backend.Current.Log(0, LogLevel.Error, $"Kiln.Content: ready dispatch failed: {e}"); } catch { }
        }
    }

    private static void Dispatch(ulong generation, bool reload)
    {
        MissingContent.OnReady(reload);  // placeholders for recorded items nobody defines, before the save is read
        (ModContext Mod, Action<ContentReadyEvent> Handler)[] handlers;
        lock (Lock) handlers = ReadyHandlers.ToArray();
        var ev = new ContentReadyEvent(generation, reload);
        var outer = ModContext.CurrentMod;
        var outerSync = SynchronizationContext.Current;
        try
        {
            foreach (var (mod, handler) in handlers)
            {
                if (!mod.Alive) continue;
                ModContext.CurrentMod = mod;
                SynchronizationContext.SetSynchronizationContext(mod.Scheduler.SynchronizationContext);
                try
                {
                    handler(ev);
                }
                catch (Exception e)
                {
                    mod.Log.Error(e, "Content.OnReady handler threw");
                }
            }
        }
        finally
        {
            ModContext.CurrentMod = outer;
            SynchronizationContext.SetSynchronizationContext(outerSync);
        }
        if (Events.HasSubscribers<ContentReadyEvent>()) Events.Post(ev);
    }

    // cut: native wrote only the first cap - 1 bytes of a longer report. The document was applied all the same, so its
    // count is read from the start of the text ("applied" is the report's first key), and the report says it was cut.
    private static ContentReport ParseReport(string text, string? fallbackError, bool cut = false)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var conflicts = new List<ContentConflict>();
        var applied = 0;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
            var r = doc.RootElement;
            if (r.TryGetProperty("applied", out var a)) applied = a.GetInt32();
            if (r.TryGetProperty("errors", out var e)) errors.AddRange(e.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (r.TryGetProperty("warnings", out var w)) warnings.AddRange(w.EnumerateArray().Select(x => x.GetString() ?? ""));
            if (r.TryGetProperty("conflicts", out var c))
                foreach (var x in c.EnumerateArray())
                    conflicts.Add(new ContentConflict(x.GetProperty("field").GetString() ?? "", x.GetProperty("winner").GetString() ?? "",
                        x.GetProperty("winnerValue").GetString() ?? "", x.GetProperty("loser").GetString() ?? "",
                        x.GetProperty("loserValue").GetString() ?? ""));
        }
        catch (JsonException)
        {
            if (cut) applied = LeadingApplied(text);
            errors.Add(cut ? "the report could not be read (longer than 2 MiB, cut)" : "the report could not be read");
        }
        if (fallbackError != null && errors.Count == 0) errors.Add(fallbackError);
        return new ContentReport(applied, errors, warnings, conflicts);
    }

    // The top-level "applied" of a report cut short, read as far as the text goes; 0 when it is not there.
    private static int LeadingApplied(string text)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text), isFinalBlock: false, state: default);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) return 0;
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
                var name = reader.ValueTextEquals("applied"u8);
                if (!reader.Read()) return 0;
                if (name) return reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n) ? n : 0;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray && !reader.TrySkip()) return 0;
            }
        }
        catch (JsonException)
        {
            // not a report at all
        }
        return 0;
    }
}

/// <summary>One registry (items, recipes, loot, drops). Entries are looked up by debug name (exact, then ignoring
/// case), <c>"0x1234abcd"</c> / decimal id, or (drops) the table GUID.</summary>
public sealed unsafe class ContentRegistry : IReadOnlyList<ContentEntry>
{
    private IReadOnlyList<ContentField>? _fields;
    private Dictionary<string, ContentField>? _byPath;

    internal ContentRegistry(ContentKind kind) => Kind = kind;

    public ContentKind Kind { get; }

    /// <summary>Entries in the registry (0 until <see cref="Content.Ready"/>).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public int Count
    {
        get
        {
            var t = Content.Require("ContentRegistry.Count");
            return (int)t->Count((uint)Kind);
        }
    }

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentEntry this[int index]
    {
        get
        {
            var t = Content.Require("ContentRegistry[]");
            ContentNative.Entry e = default;
            e.Size = (uint)sizeof(ContentNative.Entry);
            var rc = t->EntryAt((uint)Kind, (uint)index, &e);
            if (rc != ContentNative.Ok) throw new ArgumentOutOfRangeException(nameof(index), ContentNative.Message(rc));
            return new ContentEntry(this, e);
        }
    }

    /// <summary>The entry for a name, id or GUID, or null.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentEntry? Get(string key)
    {
        var t = Content.Require("ContentRegistry.Get");
        ContentNative.Entry e = default;
        e.Size = (uint)sizeof(ContentNative.Entry);
        using var k = new ContentNative.Utf8(key);
        var rc = t->Find((uint)Kind, k.Str, &e);
        return rc == ContentNative.Ok ? new ContentEntry(this, e) : null;
    }

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentEntry? Get(uint id) => Get("0x" + id.ToString("x8"));

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentEntry? Get(ItemId item) => Get(item.Value);

    /// <summary>Every field of the registry's struct, from the game's reflection.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public IReadOnlyList<ContentField> Fields
    {
        get
        {
            if (_fields != null) return _fields;
            var t = Content.Require("ContentRegistry.Fields");
            var n = t->FieldCount((uint)Kind);
            if (n == 0) return [];
            var list = new List<ContentField>((int)n);
            for (uint i = 0; i < n; i++)
            {
                ContentNative.Field f = default;
                f.Size = (uint)sizeof(ContentNative.Field);
                if (t->FieldAt((uint)Kind, i, &f) != ContentNative.Ok) continue;
                var values = new List<(string, long)>((int)f.EnumCount);
                for (uint k = 0; k < f.EnumCount; k++)
                {
                    NativeStr name;
                    long v = 0;
                    if (t->EnumName((uint)Kind, i, k, &name, &v) == ContentNative.Ok) values.Add((name.ToString(), v));
                }
                list.Add(new ContentField((int)i, f.Path.ToString(), f.TypeName.ToString(), (int)f.Offset, f.Bytes,
                    (ContentValueType)f.ValueType, f.IsSigned != 0, f.Writable != 0, (ContentEnumKind)f.EnumKind, values)
                {
                    IsReference = (f.FieldFlags & ContentNative.FieldRef) != 0 ||
                                  (t->Header.Version < 4 && f.ValueType == (byte)ContentValueType.Guid && f.TypeName.ToString() != "keen::Guid"),
                });
            }
            _byPath = list.GroupBy(x => x.Path).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _fields = list;
        }
    }

    /// <summary>The field with this path (ignoring case), or null.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentField? Field(string path)
    {
        _ = Fields;
        return _byPath != null && _byPath.TryGetValue(path, out var f) ? f : null;
    }

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public IEnumerator<ContentEntry> GetEnumerator()
    {
        var n = Count;
        for (var i = 0; i < n; i++) yield return this[i];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>One entry of a registry: an item, a recipe, an item's random-loot data, or a drop table. A handle: values
/// are read from the game each time.</summary>
public sealed unsafe class ContentEntry
{
    private readonly ContentRegistry _registry;

    internal ContentEntry(ContentRegistry registry, in ContentNative.Entry e)
    {
        _registry = registry;
        Id = e.Id;
        Name = e.Name.ToString();
        Modified = (e.Flags & 1) != 0;
        fixed (byte* g = e.Guid) Guid = new Guid(new ReadOnlySpan<byte>(g, 16));
    }

    public ContentKind Kind => _registry.Kind;
    public uint Id { get; }
    public string Name { get; }
    public Guid Guid { get; }

    /// <summary>At least one field differed from the game's original when this handle was made.</summary>
    public bool Modified { get; }

    public override string ToString() => $"{Kind} {Name} (0x{Id:x8})";

    // ---- values ----

    /// <summary>The current value of a field (drops: <c>"[3].countMin"</c> addresses stack 3).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentValue Get(string path) => Read(path, original: false);

    /// <summary>The game's original value of a field (before any mod changed it).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public ContentValue Original(string path) => Read(path, original: true);

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public double GetNumber(string path) => Get(path).AsDouble;

    /// <summary>Sets a field as the calling mod (a layer over the original; reverted when the mod unloads). Integers
    /// are rounded and range-checked against the field's type; times are seconds.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, double value) =>
        Write(path, new ContentNative.Value { Type = (uint)ContentValueType.Float, F = value, I = (long)value });

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, long value) =>
        Write(path, new ContentNative.Value { Type = (uint)ContentValueType.Int, I = value, F = value });

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, int value) => Set(path, (long)value);

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, bool value) =>
        Write(path, new ContentNative.Value { Type = (uint)ContentValueType.Bool, I = value ? 1 : 0, F = value ? 1 : 0 });

    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, TimeSpan value) =>
        Write(path, new ContentNative.Value { Type = (uint)ContentValueType.Time, F = value.TotalSeconds, I = value.Ticks * 100 });

    /// <summary>Sets an enum field by name (<c>"Epic"</c>), a flag set by names (<c>"A,B"</c>), or a number in text.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void Set(string path, string value)
    {
        var f = _registry.Field(PathOf(path)) ?? throw new KeyNotFoundException($"{Kind} has no field {path}");
        if (f.EnumKind != ContentEnumKind.Plain)
        {
            long bits = 0;
            foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var hit = f.Values.FirstOrDefault(v => string.Equals(v.Name, part, StringComparison.OrdinalIgnoreCase));
                if (hit.Name == null) throw new ArgumentException($"{part} is not a value of {f.TypeName}");
                bits = f.EnumKind == ContentEnumKind.Flags ? bits | (1L << (int)hit.Value) : hit.Value;
            }
            Set(path, bits);
            return;
        }
        Set(path, double.Parse(value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Takes the calling mod's layer of a field off. False when the mod had none.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public bool Reset(string path)
    {
        var t = Content.Require("ContentEntry.Reset");
        var owner = Content.Owner();
        using var p = new ContentNative.Utf8(path);
        var rc = t->Reset(owner, (uint)Kind, Id, p.Str);
        if (rc == ContentNative.Ok) Content.Changed(ContentChange.Reset, this, path);
        return rc == ContentNative.Ok;
    }

    // ---- references (kitbash) ----

    /// <summary>
    /// Points a reference of this item at a resource the game already loaded, as the calling mod's layer (reverted
    /// with the mod, or by <see cref="Reset"/> of the field). <paramref name="key"/>: <c>model</c> (a bundle: visual
    /// entity, models, model setup, hide mask, collection model), <c>visual</c>, <c>weaponType</c>, <c>impact</c>,
    /// <c>program</c>, <c>animation</c>, <c>collectionModel</c>, <c>iconModel</c>, or the path of a reference field
    /// (<c>equipment.visualEntity</c>). <paramref name="value"/>: <c>item:&lt;name&gt;</c> (that item's value),
    /// <c>item:&lt;name&gt;.&lt;path&gt;</c>, a GUID some loaded item uses in a field of the same type (or a loaded
    /// template), or <c>none</c>. New items (definitions) take it as is; a game item needs
    /// <paramref name="allowGameItem"/> (the opt-in).
    /// </summary>
    /// <exception cref="ArgumentException">Nothing was applied (the message lists the problems).</exception>
    [TickThreadOnly]
    [RequiresCapability("content.references")]
    public IReadOnlyList<string> SetRef(string key, string value, bool allowGameItem = false) =>
        SetRefJson(key, System.Text.Json.JsonSerializer.Serialize(value), allowGameItem);

    /// <summary>Items: sets perk slots (up to 5: a perk GUID, <c>item:&lt;name&gt;</c> = that item's perk in the same
    /// slot, <c>none</c>, null = keep the slot); the perk ids follow the game data.</summary>
    [TickThreadOnly]
    [RequiresCapability("content.references")]
    public IReadOnlyList<string> SetPerks(IReadOnlyList<string?> perks, bool allowGameItem = false) =>
        SetRefJson("perks", System.Text.Json.JsonSerializer.Serialize(perks), allowGameItem);

    /// <summary><see cref="SetRef(string, string, bool)"/> with any JSON value (the data-pack form). Returns the problems of
    /// parts that were skipped.</summary>
    [TickThreadOnly]
    [RequiresCapability("content.references")]
    public IReadOnlyList<string> SetRefJson(string key, string valueJson, bool allowGameItem = false)
    {
        var t = Content.Require("ContentEntry.SetRef");
        if (!ContentNative.Has(t, 4) || t->SetRef == null) throw ContentCaps.Off(ContentCaps.References, "kiln.content has no reference support (v4)");
        var owner = Content.Owner();
        const int cap = 8192;
        var buf = stackalloc byte[cap];
        nuint len = 0;
        int rc;
        using (var k = new ContentNative.Utf8(key))
        using (var v = new ContentNative.Utf8(valueJson))
            rc = t->SetRef(owner, (uint)Kind, Id, k.Str, v.Str, allowGameItem ? ContentNative.RefAllowGame : 0, buf, cap, &len);
        var problems = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(ContentNative.Text(buf, cap) is { Length: > 0 } x ? x : "{}");
            if (doc.RootElement.TryGetProperty("problems", out var p)) problems.AddRange(p.EnumerateArray().Select(e => e.GetString() ?? ""));
        }
        catch (JsonException) { }
        if (rc == ContentNative.InvalidArgument) throw new ArgumentException($"{Name}.{key}: {string.Join("; ", problems)}");
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{key}", rc);
        Content.Changed(ContentChange.Set, this, key);
        return problems;
    }

    /// <summary>The GUID a reference field holds now (<see cref="Guid.Empty"/> = none).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public Guid GetRef(string path) => Get(path).Guid;

    // ---- typed shortcuts ----

    /// <summary>Items: the max stack size (<c>maxStackSize</c>, 1..65535).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public int MaxStack
    {
        get => (int)Get("maxStackSize").Integer;
        set => Set("maxStackSize", (long)value);
    }

    /// <summary>Recipes: the crafting duration (<c>craftingDuration</c>; used by timed stations such as kilns).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public TimeSpan CraftingDuration
    {
        get => TimeSpan.FromSeconds(Get("craftingDuration").Number);
        set => Set("craftingDuration", value);
    }

    // ---- recipe lists, drop stacks ----

    /// <summary>Recipes: the ingredients as the game sees them now.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public IReadOnlyList<RecipeStack> Inputs => ReadStacks(ContentNative.Inputs).Select(s => new RecipeStack(new ItemId(s.Item), (int)s.Count, s.Category)).ToList();

    /// <summary>Recipes: the results.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public IReadOnlyList<RecipeStack> Outputs => ReadStacks(ContentNative.Outputs).Select(s => new RecipeStack(new ItemId(s.Item), (int)s.Count, s.Category)).ToList();

    /// <summary>Recipes: replaces the ingredients (1..32; a category input keeps its category id).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void SetInputs(IEnumerable<RecipeStack> stacks) => WriteRecipe(ContentNative.Inputs, stacks);

    /// <summary>Recipes: replaces the results (1..32).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void SetOutputs(IEnumerable<RecipeStack> stacks) => WriteRecipe(ContentNative.Outputs, stacks);

    /// <summary>Drops: every stack of the table, depth first over its groups.</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public IReadOnlyList<DropStack> Stacks => ReadStacks(ContentNative.DropStacks)
        .Select(s => new DropStack(new ItemId(s.Item), (int)s.Count, (int)s.CountMax, s.Chance, s.Weight, (int)s.Group)).ToList();

    /// <summary>Drops: changes counts / chances / weights of the table's stacks (same stacks, same order, same items).</summary>
    [TickThreadOnly]
    [RequiresCapability("kiln.content")]
    public void SetStacks(IEnumerable<DropStack> stacks)
    {
        var t = Content.Require("ContentEntry.SetStacks");
        var list = stacks.Select(s => new ContentNative.Stack
        {
            Item = s.Item.Value, Count = (uint)s.CountMin, CountMax = (uint)s.CountMax, Chance = s.Chance, Weight = s.Weight,
            Group = (uint)s.Group,
        }).ToArray();
        var owner = Content.Owner();
        int rc;
        fixed (ContentNative.Stack* p = list) rc = t->SetStacks(owner, (uint)Kind, Id, ContentNative.DropStacks, p, (uint)list.Length);
        if (rc != ContentNative.Ok) throw Content.Failure("ContentEntry.SetStacks", rc);
        Content.Changed(ContentChange.Set, this, "stacks");
    }

    // ---- internals ----

    private static string PathOf(string path)
    {
        // drops: "[3].countMin" -> "countMin"
        if (path.StartsWith('[') && path.IndexOf("].", StringComparison.Ordinal) is var i and > 0) return path[(i + 2)..];
        return path;
    }

    private ContentValue Read(string path, bool original)
    {
        var t = Content.Require("ContentEntry.Get");
        ContentNative.Value cur = default, orig = default;
        using var p = new ContentNative.Utf8(path);
        var rc = t->Get((uint)Kind, Id, p.Str, &cur, &orig);
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{path}", rc);
        var v = original ? orig : cur;
        var iv = v.I;
        var f = _registry.Field(PathOf(path));
        string? name = null;
        if (f is { EnumKind: ContentEnumKind.Enum }) name = f.Values.FirstOrDefault(x => x.Value == iv).Name;
        else if (f is { EnumKind: ContentEnumKind.Flags })
            name = string.Join(",", f.Values.Where(x => x.Value is >= 0 and < 64 && ((iv >> (int)x.Value) & 1) != 0).Select(x => x.Name));
        var g = new Guid(new ReadOnlySpan<byte>(v.Guid, 16));
        return new ContentValue((ContentValueType)v.Type, iv, v.F, g, name);
    }

    private void Write(string path, ContentNative.Value v)
    {
        var t = Content.Require("ContentEntry.Set");
        var owner = Content.Owner();
        using var p = new ContentNative.Utf8(path);
        var rc = t->Set(owner, (uint)Kind, Id, p.Str, &v);
        if (rc != ContentNative.Ok)
        {
            var f = _registry.Field(PathOf(path));
            var hint = f == null ? "no such field" : !f.Writable ? "read-only" : $"{f.TypeName}, {f.Bytes * 8}-bit";
            throw Content.Failure($"{Name}.{path} ({hint})", rc);
        }
        Content.Changed(ContentChange.Set, this, path);
    }

    private ContentNative.Stack[] ReadStacks(uint which)
    {
        var t = Content.Require("ContentEntry.Stacks");
        const int cap = 256;
        var buf = stackalloc ContentNative.Stack[cap];
        uint n = 0;
        var rc = t->Stacks((uint)Kind, Id, which, buf, cap, &n);
        if (rc != ContentNative.Ok && rc != ContentNative.BufferTooSmall) throw Content.Failure($"{Name}.stacks", rc);
        var arr = new ContentNative.Stack[Math.Min(n, cap)];
        for (var i = 0; i < arr.Length; i++) arr[i] = buf[i];
        return arr;
    }

    private void WriteRecipe(uint which, IEnumerable<RecipeStack> stacks)
    {
        var t = Content.Require("ContentEntry.SetInputs");
        var list = stacks.Select(s => new ContentNative.Stack
        {
            Item = s.Category != 0 ? 0 : s.Item.Value, Category = s.Category, Count = (uint)s.Count, CountMax = (uint)s.Count,
            Chance = 1,
        }).ToArray();
        var owner = Content.Owner();
        int rc;
        fixed (ContentNative.Stack* p = list) rc = t->SetStacks(owner, (uint)Kind, Id, which, p, (uint)list.Length);
        if (rc != ContentNative.Ok) throw Content.Failure($"{Name}.{(which == 0 ? "inputs" : "outputs")}", rc);
        Content.Changed(ContentChange.Set, this, which == 0 ? "inputs" : "outputs");
    }
}
