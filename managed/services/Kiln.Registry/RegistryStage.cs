// SPDX-License-Identifier: MIT
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Kiln.Registry;

/// <summary>Typed registry access. Contexts are supplied by Mod.OnRegistry, never constructed by a mod.</summary>
[Experimental("KILNX031")]
public static partial class Registry
{
    /// <summary>The typed adapter of this callback. Retaining it or its entries does not extend the context's lifetime.</summary>
    [RegistryThreadOnly, RequiresCapability("kiln.registry")]
    public static RegistryStage For(RegistryContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Ensure();
        return (RegistryStage)(context.Adapter ??= new RegistryStage(context));
    }
}

/// <summary>A callback's typed data view and buffered edits. Nothing is submitted until the handler succeeds.
/// Ordinary assignments and Alter target the alter pass; Final is explicit. The host preserves all-mod pass ordering.</summary>
[Experimental("KILNX031"), RegistryThreadOnly]
public sealed class RegistryStage
{
    internal readonly RegistryContext Context;
    private readonly JsonArray _edits = [];
    private readonly Dictionary<(string Registry, string Id, string Field), JsonNode?> _view = new();
    private readonly Dictionary<(string Registry, string Id), (string Like, JsonObject Fields)> _added = new();
    private RegistryPhase _target = RegistryPhase.Alter;
    private static readonly IReadOnlyDictionary<int, RegistryFieldSpec> Specs = RegistrySchema.Fields.ToDictionary(x => x.Id);
    internal RegistryStage(RegistryContext context) { Context = context; context.Capture = Capture; }

    /// <summary>Items, by Kiln id.</summary>
    public RegistryEntries<ItemEntry> Items { get { Context.Ensure(); return new(this, "items", static (id, cells) => new(id, cells)); } }
    /// <summary>Recipes, by Kiln id.</summary>
    public RegistryEntries<RecipeEntry> Recipes { get { Context.Ensure(); return new(this, "recipes", static (id, cells) => new(id, cells)); } }
    /// <summary>Item loot rules, by Kiln id.</summary>
    public RegistryEntries<ItemLootEntry> Loot { get { Context.Ensure(); return new(this, "loot", static (id, cells) => new(id, cells)); } }
    /// <summary>Drop tables, by Kiln id.</summary>
    public RegistryEntries<DropTableEntry> Drops { get { Context.Ensure(); return new(this, "drops", static (id, cells) => new(id, cells)); } }
    /// <summary>Creatures (enemies, wildlife, farm animals, villagers), by Kiln id: the id of the creature's template. An edit is
    /// global to the creature kind; values copied at spawn reach creatures that spawn after it.</summary>
    public RegistryEntries<CreatureEntry> Creatures { get { Context.Ensure(); return new(this, "creatures", static (id, cells) => new(id, cells)); } }
    /// <summary>The enemies' arsenals (the attacks they choose from), by Kiln id. Enemies of one kind share an arsenal.</summary>
    public RegistryEntries<ArsenalEntry> Arsenals { get { Context.Ensure(); return new(this, "arsenals", static (id, cells) => new(id, cells)); } }
    /// <summary>Perks (what a perk on an item grants), by Kiln id: the ids <see cref="ItemEntry.Perks"/> holds.</summary>
    public RegistryEntries<PerkEntry> Perks { get { Context.Ensure(); return new(this, "perks", static (id, cells) => new(id, cells)); } }
    /// <summary>The skill tree's nodes, by Kiln id (game:skill_&lt;node id&gt;): their cost; levels, kind, class and the ability they
    /// unlock read-only. Nodes a mod adds through Kiln.Skills are not entries.</summary>
    public RegistryEntries<SkillNodeEntry> Skills { get { Context.Ensure(); return new(this, "skills", static (id, cells) => new(id, cells)); } }
    /// <summary>The crafting stations, by Kiln id (the id of the item that builds each one). Read-only; a recipe names its
    /// station (recipes.station).</summary>
    public RegistryEntries<StationEntry> Stations { get { Context.Ensure(); return new(this, "stations", static (id, cells) => new(id, cells)); } }
    /// <summary>Buffs and debuffs, by Kiln id (game:buff_&lt;32-bit id in hex&gt;): duration and what applying one again does.</summary>
    public RegistryEntries<BuffEntry> Buffs { get { Context.Ensure(); return new(this, "buffs", static (id, cells) => new(id, cells)); } }
    /// <summary>Abilities (the game's timed action sequences: casts, attacks, abilities), by Kiln id: their timing.</summary>
    public RegistryEntries<AbilityEntry> Abilities { get { Context.Ensure(); return new(this, "abilities", static (id, cells) => new(id, cells)); } }
    /// <summary>The singleton balance table. Its restart fields are editable only at game start.</summary>
    public BalanceEntry Balance { get { Context.Ensure(); return new(new("game:balance"), Cells("balance", "game:balance")); } }
    /// <summary>Authors new entries in the add pass. Entry creation is GameStart-only; later native points report RestartRequired.</summary>
    public void Add(Action<RegistryStage> entries) { ArgumentNullException.ThrowIfNull(entries); In(RegistryPhase.Add, () => entries(this)); }
    /// <summary>Authors ordinary field edits, once in the alter pass.</summary>
    public void Alter(Action<RegistryStage> edits) { ArgumentNullException.ThrowIfNull(edits); In(RegistryPhase.Alter, () => edits(this)); }
    /// <summary>Authors final edits, once in the final automatic pass. Other mods' final edits are not in its read view.
    /// Inside Registry.Change this is an ordinary Alter edit in that single transaction, not a global Final pass.</summary>
    public void Final(Action<RegistryStage> edits) { ArgumentNullException.ThrowIfNull(edits); In(RegistryPhase.Final, () => edits(this)); }

    internal void In(RegistryPhase target, Action action)
    {
        Context.Ensure();
        if (Context.EntryPoint != RegistryEntryPoint.Manual && Context.Phase != target) return;
        var before = _target; _target = target;
        try { action(); } finally { _target = before; }
    }
    internal IRegistryCells Cells(string registry, string id) => new EntryCells(this, registry, id, _target);
    private bool Writable(RegistryPhase phase)
    {
        Context.Ensure();
        if (Context.ReadOnly) throw new RegistryContextException(RegistryContextError.ReadOnly, "this is a resolved read-only registry view");
        return Context.EntryPoint == RegistryEntryPoint.Manual || Context.Phase == phase;
    }
    private string? Capture()
    {
        Context.Ensure();
        if (_edits.Count == 0) return null;
        return new JsonObject { ["ir"] = 1, ["mod"] = Context.ModId, ["stage"] = Context.Phase.ToString().ToLowerInvariant(),
            ["source"] = "managed:" + Context.Identity, ["edits"] = _edits.DeepClone() }.ToJsonString();
    }
    private JsonNode? Read(string registry, string id, string field)
    {
        Context.Ensure();
        var key = (registry, id, field);
        if (!_view.TryGetValue(key, out var value))
        {
            var source = _added.TryGetValue((registry, id), out var creation) ? creation.Like : id;
            _view[key] = value = source != id ? Read(registry, source, field)?.DeepClone() : JsonNode.Parse(Context.ReadValue(registry, source, field));
        }
        return value;
    }
    internal void AddEntry<T>(string registry, RegistryId id, RegistryId like, Func<RegistryId, IRegistryCells, T> create, Action<T> configure)
    {
        Context.Ensure(); ArgumentNullException.ThrowIfNull(configure);
        if (id.Value is null || id.Mod != Context.ModId || id.Name.Contains('~')) throw new ArgumentException("a new entry uses the current mod's namespace and a fresh lower-case name", nameof(id));
        if (like.Value is null) throw new ArgumentException("like is a stable Kiln id", nameof(like));
        if (!Writable(RegistryPhase.Add)) return;
        var ancestor = like.Value;
        while (true)
        {
            if (ancestor == id.Value) throw new ArgumentException("a new entry's local clone chain is cyclic", nameof(like));
            if (!_added.TryGetValue((registry, ancestor), out var parent)) break;
            ancestor = parent.Like;
        }
        var key = (registry, id.Value);
        if (_added.ContainsKey(key)) throw new ArgumentException("this callback already added that entry id", nameof(id));
        var fields = new JsonObject(); _added.Add(key, (like.Value, fields));
        try
        {
            configure(create(id, new EntryCells(this, registry, id.Value, RegistryPhase.Add)));
            Record(new JsonObject { ["registry"] = registry, ["entry"] = "add", ["id"] = id.Value, ["like"] = like.Value, ["fields"] = fields });
        }
        catch
        {
            _added.Remove(key);
            foreach (var cell in _view.Keys.Where(x => x.Registry == registry && x.Id == id.Value).ToArray()) _view.Remove(cell);
            throw;
        }
    }
    internal void RemoveEntry(string registry, string pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        if (!Writable(_target)) return;
        Record(new JsonObject { ["registry"] = registry, ["entry"] = "remove", ["select"] = pattern });
    }
    private void Record(JsonObject edit)
    {
        if (_edits.Count >= 4096) throw new ArgumentException("a registry callback has at most 4096 edits");
        _edits.Add(edit);
    }
    private static RegistryFieldSpec Spec(int id) => Specs.TryGetValue(id, out var field) ? field : throw new ArgumentException("unknown registry field id");
    private static JsonNode? Json(object? value) => value switch
    {
        null => null,
        RegistryId id => JsonValue.Create(id.Value),
        TimeSpan t => JsonValue.Create(t.TotalSeconds),
        IReadOnlyDictionary<string, object?> map => new JsonObject(map.Select(x => KeyValuePair.Create(x.Key, Json(x.Value)))),
        string text => JsonValue.Create(text),
        IEnumerable values => new JsonArray(values.Cast<object?>().Select(Json).ToArray()),
        _ => System.Text.Json.JsonSerializer.SerializeToNode(value),
    };
    private static JsonNode? Leaf(JsonNode? node, string path)
    {
        if (node is JsonObject o && o.TryGetPropertyValue(path, out var exact)) return exact;
        foreach (var part in path.Split('.')) node = node is JsonObject obj ? obj[part] : null;
        return node;
    }
    private sealed class EntryCells(RegistryStage stage, string registry, string id, RegistryPhase phase) : IRegistryCells
    {
        private RegistryFieldSpec Field(int field)
        {
            stage.Context.Ensure();
            var spec = Spec(field);
            if (spec.Registry != registry) throw new ArgumentException("the field belongs to another registry");
            return spec;
        }
        private JsonNode? Value(RegistryFieldSpec spec, int index)
        {
            var bracket = spec.Path.IndexOf("[]", StringComparison.Ordinal);
            if (bracket >= 0)
            {
                if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
                var array = stage.Read(registry, id, spec.Path[..bracket]) as JsonArray;
                if (array is null || index >= array.Count) throw new ArgumentOutOfRangeException(nameof(index));
                return Leaf(array[index], spec.Path[(bracket + 3)..]);
            }
            var value = stage.Read(registry, id, spec.Path);
            return index < 0 ? value : value is JsonArray list && index < list.Count ? list[index] : throw new ArgumentOutOfRangeException(nameof(index));
        }
        public bool TryGet(int field, int index, out object? value)
        {
            var spec = Field(field); var node = Value(spec, index);
            if (node is null) { value = null; return false; }
            var type = spec.Type == RegistryFieldType.List && index >= 0 ? spec.Element : spec.Type;
            value = type switch
            {
                RegistryFieldType.Int => checked((long)Math.Round(double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture), MidpointRounding.AwayFromZero)),
                RegistryFieldType.Float => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture),
                RegistryFieldType.Bool => node.GetValue<bool>(),
                RegistryFieldType.Time => TimeSpan.FromSeconds(double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture)),
                RegistryFieldType.Ref => new RegistryId(node.GetValue<string>()),
                RegistryFieldType.Enum => node.GetValue<string>(),
                RegistryFieldType.Flags => ((JsonArray)node).Select(x => x!.GetValue<string>()).ToArray(),
                _ => throw new InvalidOperationException("read a list through its typed elements"),
            };
            return true;
        }
        public void Set(int field, int index, object? value)
        {
            var spec = Field(field);
            if (spec.ReadOnly) throw new ArgumentException("the registry field is read-only");
            if (!stage.Writable(phase)) return;
            var node = Json(value);
            var path = spec.Path.Replace("[]", $"[{index}]", StringComparison.Ordinal);
            if (spec.Type == RegistryFieldType.List && index >= 0) path += $"[{index}]";
            // Validate the projected write before recording it: a caller may catch an invalid index and continue.
            var bracket = spec.Path.IndexOf("[]", StringComparison.Ordinal);
            JsonArray? list = null; JsonObject? obj = null;
            if (bracket >= 0)
            {
                list = stage.Read(registry, id, spec.Path[..bracket]) as JsonArray;
                if (list is null || index < 0 || index >= list.Count) throw new ArgumentOutOfRangeException(nameof(index));
                obj = list[index] as JsonObject ?? throw new ArgumentException("the list entry is not a struct");
            }
            else if (index >= 0)
            {
                list = stage.Read(registry, id, spec.Path) as JsonArray;
                if (list is null || index >= list.Count) throw new ArgumentOutOfRangeException(nameof(index));
            }
            var adding = stage._added.TryGetValue((registry, id), out var creation);
            if (!adding) stage.Record(new JsonObject { ["registry"] = registry, ["select"] = id, ["field"] = path, ["op"] = node?.DeepClone() });
            if (obj is not null) obj[spec.Path[(bracket + 3)..]] = node;
            else if (list is not null) list[index] = node;
            else stage._view[(registry, id, spec.Path)] = node;
            if (adding)
            {
                var root = bracket >= 0 ? spec.Path[..bracket] : spec.Path;
                creation.Fields[root] = stage._view[(registry, id, root)]?.DeepClone();
            }
        }
        public int Count(int listField) => stage.Read(registry, id, Field(listField).Path) is JsonArray array ? array.Count : 0;
        public void ListOp(int listField, RegistryListOp op, IReadOnlyList<object?> values)
        {
            var spec = Field(listField);
            if (spec.ReadOnly) throw new ArgumentException("the registry field is read-only");
            if (!stage.Writable(phase)) return;
            var json = values.Select(value => value is null && spec.Element == RegistryFieldType.Struct ? new JsonObject() : Json(value)).ToArray();
            var payload = new JsonObject { [op.ToString().ToLowerInvariant()] = op == RegistryListOp.Clear ? JsonValue.Create(true) : new JsonArray(json.Select(x => x?.DeepClone()).ToArray()) };
            var old = stage.Read(registry, id, spec.Path) as JsonArray ?? [];
            var next = op switch
            {
                RegistryListOp.Clear => new JsonArray(),
                RegistryListOp.Replace => new JsonArray(json),
                RegistryListOp.Append => new JsonArray(old.Select(x => x?.DeepClone()).Concat(json).ToArray()),
                RegistryListOp.Remove => new JsonArray(old.Where(x => !json.Any(v => JsonNode.DeepEquals(x, v))).Select(x => x?.DeepClone()).ToArray()),
                _ => throw new ArgumentOutOfRangeException(nameof(op)),
            };
            if (stage._added.TryGetValue((registry, id), out var creation)) creation.Fields[spec.Path] = next.DeepClone();
            else stage.Record(new JsonObject { ["registry"] = registry, ["select"] = id, ["field"] = spec.Path, ["list"] = payload });
            stage._view[(registry, id, spec.Path)] = next;
        }
    }
}

/// <summary>A registry's typed entries. Selections use Kiln id patterns, never game reflection names.</summary>
[Experimental("KILNX031"), RegistryThreadOnly]
public sealed class RegistryEntries<T> where T : IRegistryEntry
{
    private readonly RegistryStage _stage;
    private readonly string _registry;
    private readonly Func<RegistryId, IRegistryCells, T> _create;
    internal RegistryEntries(RegistryStage stage, string registry, Func<RegistryId, IRegistryCells, T> create)
        => (_stage, _registry, _create) = (stage, registry, create);
    internal void AddLikeCore(RegistryId id, RegistryId like, Action<T> configure) => _stage.AddEntry(_registry, id, like, _create, configure);
    internal void RemoveCore(string pattern) => _stage.RemoveEntry(_registry, pattern);
    /// <summary>One entry by its Kiln id.</summary>
    public T this[RegistryId id]
    {
        get
        {
            _stage.Context.Ensure();
            if (id.Value is null) throw new ArgumentException("an empty registry id", nameof(id));
            return _create(id, _stage.Cells(_registry, id.Value));
        }
    }
    /// <summary>One entry by its textual Kiln id.</summary>
    public T this[string id] => this[new RegistryId(id)];
    /// <summary>A copied selection, in native registry order.</summary>
    public IReadOnlyList<T> Select(string pattern = "*")
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern); _stage.Context.Ensure();
        return _stage.Context.SelectIds(_registry, pattern).Select(id => this[id]).ToArray();
    }
    /// <summary>Edits the selected entries in the alter pass.</summary>
    public void Alter(string pattern, Action<T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        _stage.In(RegistryPhase.Alter, () => { foreach (var item in Select(pattern)) edit(item); });
    }
    /// <summary>Edits the selected entries in the final pass.</summary>
    public void Final(string pattern, Action<T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        _stage.In(RegistryPhase.Final, () => { foreach (var item in Select(pattern)) edit(item); });
    }
}
