// SPDX-License-Identifier: MIT
// The block kinds mods registered (Blocks.RegisterKind): by id, by blocks/*.json section and by record type. A registration
// belongs to the mod instance that made it; when it goes (Dispose, or the mod retires) the entry drops its provider's type,
// compiler and serializer caches, so an unloaded provider is not kept alive by this table.
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>A mod instance as an authority: the id is for messages, <see cref="Token"/> is what is compared.</summary>
internal sealed record KindOwner(string ModId, object Token, Func<bool> Alive)
{
    public static KindOwner? Caller() => ModContext.CurrentMod is { } m ? new KindOwner(m.Info.Id, m, () => m.Alive) : null;

    public bool Same(KindOwner? other) => other != null && ReferenceEquals(Token, other.Token);
}

internal sealed class KindEntry(string id, string section, KindOwner provider, BlockKindDescriptor descriptor, string descriptorJson)
{
    public string Id { get; } = id;
    public string Section { get; } = section;
    public KindOwner Provider { get; } = provider;
    /// <summary>Kiln's own copy.</summary>
    public BlockKindDescriptor Descriptor { get; } = descriptor;
    /// <summary>The descriptor as canonical JSON, for a plan's hash.</summary>
    public string DescriptorJson { get; } = descriptorJson;
    /// <summary>One provider callback at a time.</summary>
    public readonly object CallbackLock = new();
    public volatile bool Retired;
    // null once retired
    public Type? RecordType;
    public JsonSerializerOptions? Options;
    public Func<BlockCompileContext, IBlock, BlockPlan>? Compile;
    public BlockKindRegistration? Registration;
}

internal static partial class KindRegistry
{
    internal const int MaxKinds = 128, MaxSchemaBytes = 64 << 10, MaxRequires = 64, MaxText = 256;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, KindEntry> ById = new(StringComparer.Ordinal), BySection = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, KindEntry> ByType = new();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Local();

    internal static int Count { get { lock (Gate) return ById.Count; } }

    internal static KindEntry? Find(Type record) { lock (Gate) return ByType.GetValueOrDefault(record); }
    internal static KindEntry? FindSection(string section) { lock (Gate) return BySection.GetValueOrDefault(section); }
    internal static KindEntry? FindId(string id) { lock (Gate) return ById.GetValueOrDefault(id); }

    /// <summary>A section of blocks/*.json written as an external kind's: <c>mod:plural</c>.</summary>
    internal static bool IsExternalSection(string section) => section.Contains(':');

    /// <summary>The record types Kiln compiles itself: every section of blocks/*.json, and every block type of this assembly.
    /// None of them can be registered as an external kind.</summary>
    internal static bool IsBuiltIn(Type t) => t.Assembly == typeof(IBlock).Assembly;

    internal static BlockKindResult Register<T>(KindOwner provider, BlockKindDescriptor descriptor, IBlockKind<T> compiler) where T : class, IBlock
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(compiler);
        KindCompiler.RefuseInsideCompile("Blocks.RegisterKind");
        var id = Name(descriptor.Id, nameof(descriptor.Id), provider.ModId);
        var section = Name(descriptor.JsonSection, nameof(descriptor.JsonSection), provider.ModId);
        if (descriptor.Format != 1) throw new ArgumentException($"block kind {id}: format {descriptor.Format} is not supported; only 1", nameof(descriptor));
        var record = typeof(T);
        if (IsBuiltIn(record)) throw new ArgumentException($"block kind {id}: {record.Name} is a block Kiln compiles itself; a kind takes a record of its provider's own", nameof(compiler));
        if (record.IsAbstract || record.IsInterface) throw new ArgumentException($"block kind {id}: {record.Name} must be a concrete record", nameof(compiler));
        var schema = Schema(id, descriptor.SchemaJson);
        var requires = (descriptor.Requires ?? []).ToArray();
        if (requires.Length > MaxRequires) throw new ArgumentException($"block kind {id}: {requires.Length} required capabilities; at most {MaxRequires}", nameof(descriptor));
        foreach (var r in requires)
            if (string.IsNullOrWhiteSpace(r) || r.Length > MaxText) throw new ArgumentException($"block kind {id}: a required capability name is empty or longer than {MaxText}", nameof(descriptor));
        if (!Enum.IsDefined(descriptor.Clients)) throw new ArgumentException($"block kind {id}: clients {(int)descriptor.Clients} is no ClientsLevel", nameof(descriptor));
        // Kiln's own copy: the provider's object, its list and its strings' owner are not read again.
        var copy = new BlockKindDescriptor { Id = id, JsonSection = section, Format = 1, SchemaJson = schema, Requires = Array.AsReadOnly(requires), Clients = descriptor.Clients };
        var descriptorJson = KindJson.Canonical(new System.Text.Json.Nodes.JsonObject
        {
            ["id"] = id, ["jsonSection"] = section, ["format"] = 1, ["schema"] = System.Text.Json.Nodes.JsonNode.Parse(schema),
            ["requires"] = new System.Text.Json.Nodes.JsonArray(requires.Select(r => (System.Text.Json.Nodes.JsonNode?)r).ToArray()), ["clients"] = descriptor.Clients.ToString(),
        });
        var entry = new KindEntry(id, section, provider, copy, descriptorJson)
        {
            RecordType = record,
            Options = KindJson.NewOptions(),
            Compile = (context, block) => compiler.Compile(context, (T)block),
        };
        lock (Gate)
        {
            if (ById.ContainsKey(id)) return Refused($"AlreadyRegistered: kind {id}");
            if (BySection.ContainsKey(section)) return Refused($"AlreadyRegistered: section {section}");
            if (ByType.ContainsKey(record)) return Refused($"AlreadyRegistered: record {record.Name} (kind {ByType[record].Id})");
            if (ById.Count >= MaxKinds) return Refused($"TooManyKinds: {MaxKinds} kinds are registered in this process");
            ById[id] = entry;
            BySection[section] = entry;
            ByType[record] = entry;
            entry.Registration = new BlockKindRegistration(entry);
        }
        if (provider.Token is ModContext mod)
        {
            mod.Retiring += () => Retire(entry);
            if (!mod.Alive) Retire(entry);   // it retired between the caller check and the subscription
        }
        BlocksBridge.Log(LogLevel.Info, $"blocks: {provider.ModId}: block kind {id} registered (section \"{section}\", record {record.Name})");
        KindEvents.Queue(new BlockKindRegisteredEvent(id, provider.ModId, removed: false));
        return new BlockKindResult { Ok = true, Registration = entry.Registration };

        static BlockKindResult Refused(string reason) => new() { Ok = false, Reason = reason };
    }

    /// <summary>The provider takes its kind away. Another mod, or another instance of the same mod id, may not.</summary>
    internal static void Dispose(KindEntry entry, KindOwner? caller)
    {
        if (!entry.Provider.Same(caller))
            throw new ArgumentException($"block kind {entry.Id} was registered by another mod instance ({entry.Provider.ModId}); only it disposes the registration");
        Retire(entry);
    }

    /// <summary>The kind is gone: now, without waiting for a compile that is running and without calling the provider.</summary>
    internal static void Retire(KindEntry entry)
    {
        lock (Gate)
        {
            if (entry.Retired) return;
            entry.Retired = true;
            if (ById.GetValueOrDefault(entry.Id) == entry) ById.Remove(entry.Id);
            if (BySection.GetValueOrDefault(entry.Section) == entry) BySection.Remove(entry.Section);
            if (entry.RecordType is { } t && ByType.GetValueOrDefault(t) == entry) ByType.Remove(t);
            entry.RecordType = null;
            entry.Options = null;
            entry.Compile = null;
        }
        BlocksBridge.Log(LogLevel.Info, $"blocks: {entry.Provider.ModId}: block kind {entry.Id} removed");
        KindEvents.Queue(new BlockKindRegisteredEvent(entry.Id, entry.Provider.ModId, removed: true));
    }

    /// <summary>For tests: forget every kind, with no event.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            foreach (var e in ById.Values) { e.Retired = true; e.RecordType = null; e.Options = null; e.Compile = null; }
            ById.Clear();
            BySection.Clear();
            ByType.Clear();
        }
    }

    private static string Name(string? value, string what, string mod)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"a block kind's {what} is empty", "descriptor");
        var colon = value.IndexOf(':');
        if (colon <= 0 || !Local().IsMatch(value[(colon + 1)..]))
            throw new ArgumentException($"a block kind's {what} \"{Clip(value)}\" is not \"<mod id>:<name>\" (the name takes letters, digits and '-', at most 64)", "descriptor");
        if (!string.Equals(value[..colon], mod, StringComparison.Ordinal))
            throw new ArgumentException($"a block kind's {what} \"{Clip(value)}\" must start with the registering mod's id, \"{mod}:\"", "descriptor");
        return string.Concat(value);   // a copy
    }

    private static string Clip(string s) => s.Length <= 96 ? s : s[..96] + "...";

    // The schema is data Kiln keeps and hands to tools: valid JSON, an object, bounded, and with no reference that would make a reader fetch something.
    private static string Schema(string id, string? schema)
    {
        if (string.IsNullOrWhiteSpace(schema)) throw new ArgumentException($"block kind {id}: SchemaJson is empty", "descriptor");
        if (System.Text.Encoding.UTF8.GetByteCount(schema) > MaxSchemaBytes) throw new ArgumentException($"block kind {id}: SchemaJson is larger than {MaxSchemaBytes >> 10} KiB", "descriptor");
        try
        {
            using var doc = JsonDocument.Parse(schema, new JsonDocumentOptions { MaxDepth = 64 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException($"block kind {id}: SchemaJson is not a JSON object", "descriptor");
            Refs(id, doc.RootElement);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"block kind {id}: SchemaJson is not JSON: {e.Message}", "descriptor");
        }
        return string.Concat(schema);
    }

    private static void Refs(string id, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name is "$ref" or "$dynamicRef" && (p.Value.ValueKind != JsonValueKind.String || !p.Value.GetString()!.StartsWith('#')))
                        throw new ArgumentException($"block kind {id}: SchemaJson has the reference {Clip(p.Value.GetRawText())}; only local \"#...\" references are taken", "descriptor");
                    Refs(id, p.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Refs(id, x);
                break;
        }
    }
}

/// <summary>Kind events are delivered by the tick pump, whichever thread the change happened on.</summary>
internal static class KindEvents
{
    /// <summary>Where they go (tests set it).</summary>
    internal static Action<KilnEvent> Sink = ev => Events.Enqueue(ev);

    public static void Queue(KilnEvent ev)
    {
        try { Sink(ev); }
        catch (Exception e) when (e is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Error, $"blocks: posting {ev.GetType().Name} failed: {e.Message}"); }
    }
}
