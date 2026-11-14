// SPDX-License-Identifier: MIT
// Staging of Mod.OnBoot's definitions (chapter 08 §4.1, §5): BootDefinitions.For(boot).Define / DefineJson copy and normalise a block into
// canonical JSON (the block's own camelCase form, keys sorted) and hand it to kiln.boot; nothing compiles or registers here. Kiln.Host lowers
// what was staged into the boot generation (BootLowering.cs) once every author ran.
using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>A staged definition's name: what other definitions and data files name it by (<see cref="QualifiedName"/>). Not a live handle:
/// the definition is registered when the boot generation is published.</summary>
/// <typeparam name="T">The block type.</typeparam>
/// <param name="Mod">The defining mod.</param>
/// <param name="Kind">The kind (plural, as in blocks/*.json).</param>
/// <param name="Name">The block's name.</param>
public sealed record BootBlockId<T>(string Mod, string Kind, string Name) where T : class, IBlock
{
    /// <summary><c>mod:name</c>, as a reference from another mod.</summary>
    public string QualifiedName => $"{Mod}:{Name}";
}

/// <summary>What <see cref="BootDefinitions.Define{T}"/> did. <see cref="Ok"/> means staged, not committed.</summary>
/// <typeparam name="T">The block type.</typeparam>
/// <param name="Ok">True when staged (the same definition again is fine).</param>
/// <param name="Reason">Why not: <c>DuplicateDefinition</c>, <c>PhaseClosed</c>, <c>TooMany</c>, <c>NotInBootKinds</c>; null when staged.</param>
/// <param name="Reference">The staged definition's name.</param>
public sealed record BootDefinitionResult<T>(bool Ok, string? Reason, BootBlockId<T>? Reference) : IKilnResult where T : class, IBlock;

/// <summary>What <see cref="BootDefinitions.DefineJson"/> did. <see cref="Ok"/> means staged, not committed.</summary>
/// <param name="Ok">True when staged.</param>
/// <param name="Reason">Why not; null when staged.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Key">The block's name.</param>
public sealed record BootJsonDefinitionResult(bool Ok, string? Reason, string Kind, string Key) : IKilnResult;

/// <summary>A mod's boot definitions are in the published generation: once per runtime activation of the mod (a hosted world's new instance
/// gets it again, with the same <see cref="Generation"/>: nothing was committed again). Delivered on the first service dispatch after the
/// mod's runtime instance loaded; there may be no world yet.</summary>
[AnyThread]
public sealed class BootDefinitionsCommittedEvent : KilnEvent
{
    /// <summary>A new event.</summary>
    /// <param name="modId">The mod.</param>
    /// <param name="generation">The boot generation that holds the definitions.</param>
    /// <param name="definitions">Its committed definitions, as <c>kind/key</c>.</param>
    public BootDefinitionsCommittedEvent(string modId, ulong generation, IEnumerable<string> definitions)
    {
        ArgumentNullException.ThrowIfNull(modId);
        ArgumentNullException.ThrowIfNull(definitions);
        ModId = modId;
        Generation = generation;
        Definitions = Array.AsReadOnly(definitions.ToArray());
    }

    /// <summary>The mod.</summary>
    public string ModId { get; }

    /// <summary>The boot generation.</summary>
    public ulong Generation { get; }

    /// <summary>Its committed definitions, as <c>kind/key</c>.</summary>
    public IReadOnlyList<string> Definitions { get; }
}

/// <summary>Stages a mod's block definitions in its <see cref="Mod.OnBoot"/>: <c>BootDefinitions.For(boot).Define("cold", new StatusBlock { ... })</c>.
/// A staged definition is plain data: it is copied at once, compiled with the mod's data files when every boot mod ran, and registered with
/// them before the game is entered. A data file of the mod with the same kind and name wins (the issue says so).</summary>
public sealed class BootDefinitions
{
    /// <summary>The most definitions one mod stages (kiln.boot's limit).</summary>
    internal const int MaxDefinitions = 4096;

    private static readonly Regex KeyPattern = new("^[a-z][a-z0-9_.-]{0,95}$", RegexOptions.CultureInvariant);

    /// <summary>The kinds this build lowers into a boot generation, by their exact block type.</summary>
    internal static readonly IReadOnlyDictionary<Type, string> Kinds = new Dictionary<Type, string>
    {
        [typeof(StatusBlock)] = "statuses", [typeof(EffectBlock)] = "effects", [typeof(ItemBlock)] = "items", [typeof(SpellBlock)] = "spells",
        [typeof(TalentBlock)] = "talents",
    };

    /// <summary>The kinds of chapter 08 §4.1 that this build does not lower yet (they need id reservations or a pack the boot lowering does
    /// not build): staging them answers <c>NotInBootKinds</c>.</summary>
    internal static readonly HashSet<string> NotYet =
    [
        "talentTrees", "npcs", "creatures", "materialTweaks", "buildingMaterials", "terrainMaterials", "blueprints", "props", "stations", "vfx",
        "materials", "quests", "dialogues", "bodies", "animations", "ais", "behaviours", "perks", "abilities", "looks", "strengths", "surfaces",
    ];

    private readonly BootContext _boot;

    private BootDefinitions(BootContext boot) => _boot = boot;

    /// <summary>The staging of <paramref name="context"/>'s frame.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="KilnUnavailableException"><c>mods.boot</c> is off in this process.</exception>
    [BootThreadOnly, RequiresCapability("mods.boot")]
    public static BootDefinitions For(BootContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.BootOn) throw new KilnUnavailableException("mods.boot", "this process runs no boot phase for the mod");
        return new BootDefinitions(context);
    }

    /// <summary>Stages <paramref name="definition"/> as the mod's block <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">A bad name, a block type that is not a boot kind (a subclass of one included), or a value that is
    /// not plain data (a delegate, a stream).</exception>
    [BootThreadOnly, RequiresCapability("mods.boot")]
    public BootDefinitionResult<T> Define<T>(string name, T definition) where T : class, IBlock
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(definition);
        CheckKey(name);
        var kind = KindOf(definition.GetType());
        if (kind == null) return new BootDefinitionResult<T>(false, NotYetReason(BlockJson.SectionOf(definition.GetType())!), null);
        PlainData(definition, definition.GetType().Name, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        var json = Canonical(JsonSerializer.SerializeToNode(definition, definition.GetType(), BlockJson.SharedOptions));
        var (ok, reason) = Stage(kind, name, json);
        return new BootDefinitionResult<T>(ok, reason, ok ? new BootBlockId<T>(_boot.ModId, kind, name) : null);
    }

    /// <summary>Stages one block given as JSON (the value of <c>blocks/*.json</c>'s <c>&lt;kind&gt;.&lt;name&gt;</c>).</summary>
    /// <exception cref="ArgumentException">A kind that is not a boot kind, a bad name, or JSON that is not that kind's block.</exception>
    [BootThreadOnly, RequiresCapability("mods.boot")]
    public BootJsonDefinitionResult DefineJson(string kind, string name, string json)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(json);
        var type = Kinds.FirstOrDefault(k => k.Value == kind).Key;
        CheckKey(name);
        if (type == null)
        {
            if (NotYet.Contains(kind)) return new BootJsonDefinitionResult(false, NotYetReason(kind), kind, name);
            throw new ArgumentException($"\"{kind}\" is not a boot kind ({string.Join(", ", Kinds.Values)})", nameof(kind));
        }
        object? block;
        try { block = JsonSerializer.Deserialize(json, type, BlockJson.SharedOptions); }
        catch (JsonException e) { throw new ArgumentException($"{kind}/{name}: not a {type.Name}: {e.Message}", nameof(json), e); }
        if (block == null) throw new ArgumentException($"{kind}/{name}: null is not a {type.Name}", nameof(json));
        var canonical = Canonical(JsonSerializer.SerializeToNode(block, type, BlockJson.SharedOptions));
        var (ok, reason) = Stage(kind, name, canonical);
        return new BootJsonDefinitionResult(ok, reason, kind, name);
    }

    private (bool Ok, string? Reason) Stage(string kind, string name, string json)
    {
        if (!_boot.Open) return (false, $"PhaseClosed: {_boot.ModId}'s OnBoot has returned; definitions are staged inside OnBoot only");
        var known = _boot.Staged.FirstOrDefault(s => s.Kind == kind && s.Key == name);
        if (known != null)
            return known.Json == json ? (true, null) : (false, $"DuplicateDefinition: {_boot.ModId}:{name} ({kind}) is staged already with other content");
        if (_boot.Staged.Count >= MaxDefinitions) return (false, $"TooMany: one mod stages at most {MaxDefinitions} definitions");
        if (_boot.Stage is { } native)
        {
            var (ok, reason) = native(kind, name, json);
            if (!ok) return (false, reason);
        }
        _boot.Staged.Add(new BootStaged(kind, name, json));
        return (true, null);
    }

    // The kind of an exact boot type; null for a chapter-08 kind this build does not lower yet (a soft refusal).
    private static string? KindOf(Type type)
    {
        if (Kinds.TryGetValue(type, out var kind)) return kind;
        foreach (var (t, k) in Kinds)
            if (type.IsSubclassOf(t)) throw new ArgumentException($"{type.Name} is a subclass of {t.Name}: a boot definition is exactly a {t.Name}");
        if (BlockJson.SectionOf(type) is { } section && NotYet.Contains(section)) return null;
        throw new ArgumentException($"{type.Name} is not a boot kind ({string.Join(", ", Kinds.Values)})");
    }

    private static string NotYetReason(string kind) =>
        $"NotInBootKinds: \"{kind}\" is a boot kind of chapter 08 that this build does not lower yet; define it in OnLoad or a data file " +
        $"(this build stages {string.Join(", ", Kinds.Values)})";

    private static void CheckKey(string name)
    {
        if (!KeyPattern.IsMatch(name) || name.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException($"\"{name}\" is not a boot definition name: ^[a-z][a-z0-9_.-]{{0,95}}$, no \"..\"", nameof(name));
    }

    /// <summary>Plain data only: a delegate or a stream anywhere in the block is refused (it could not outlive the frame).</summary>
    private static void PlainData(object value, string path, int depth, HashSet<object> seen)
    {
        if (depth > 32) throw new ArgumentException($"{path}: nested deeper than 32");
        switch (value)
        {
            case Delegate: throw new ArgumentException($"{path}: a delegate is not plain data; a boot definition holds data only");
            case Stream: throw new ArgumentException($"{path}: a stream is not plain data; a boot definition holds data only");
            case string or Guid or Enum or JsonElement or JsonNode: return;
        }
        var type = value.GetType();
        if (type.IsPrimitive || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan)) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
                if (e.Value != null) PlainData(e.Value, $"{path}[{e.Key}]", depth + 1, seen);
            return;
        }
        if (value is IEnumerable list)
        {
            var i = 0;
            foreach (var x in list)
            {
                if (x != null) PlainData(x, $"{path}[{i}]", depth + 1, seen);
                i++;
            }
            return;
        }
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            if (typeof(Delegate).IsAssignableFrom(p.PropertyType) || typeof(Stream).IsAssignableFrom(p.PropertyType))
            {
                if (p.GetValue(value) != null) throw new ArgumentException($"{path}.{p.Name}: a {(typeof(Stream).IsAssignableFrom(p.PropertyType) ? "stream" : "delegate")} is not plain data; a boot definition holds data only");
                continue;
            }
            if (p.GetValue(value) is { } v) PlainData(v, $"{path}.{p.Name}", depth + 1, seen);
        }
    }

    /// <summary>Canonical JSON: object keys sorted (ordinal), no white space; the same block gives the same bytes however it was written.</summary>
    internal static string Canonical(JsonNode? node) => Sort(node)?.ToJsonString() ?? "null";

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray a => new JsonArray(a.Select(Sort).ToArray()),
        null => null,
        _ => JsonNode.Parse(node.ToJsonString()),
    };
}
