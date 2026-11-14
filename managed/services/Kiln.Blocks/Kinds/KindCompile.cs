// SPDX-License-Identifier: MIT
// Compiling a block of an external kind: the provider's pure callback runs once, under a context that is valid only for
// that call, and what it returns is frozen (canonical JSON of built-in blocks, hashed). Nothing here reaches a service.
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kiln;

/// <summary>What a kind's compiler works with, for one block. Valid only during <see cref="IBlockKind{T}.Compile"/>:
/// used afterwards, every member throws <see cref="ArgumentException"/>. Nothing here reads the game or registers anything.</summary>
public sealed class BlockCompileContext
{
    private readonly KindEntry _kind;
    private readonly KindOwner _consumer;
    private readonly ulong _request;
    private readonly string _recordJson;
    private readonly IReadOnlyDictionary<(string Mod, string Name), KindCompiler.Identity> _identities;
    private readonly object _token = new();
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    private int _nodes;
    private long _bytes;
    private bool _closed, _planned;

    internal BlockCompileContext(KindEntry kind, KindOwner consumer, ulong request, string name, string recordJson, IReadOnlyDictionary<(string, string), KindCompiler.Identity> identities)
    {
        _kind = kind;
        _consumer = consumer;
        _request = request;
        _recordJson = recordJson;
        _identities = identities;
        ModId = consumer.ModId;
        Name = name;
        KindId = kind.Id;
    }

    /// <summary>The consumer mod: whose block this is.</summary>
    public string ModId { get; }
    /// <summary>The block's name in the consumer mod.</summary>
    public string Name { get; }
    public string KindId { get; }

    internal void Close() => _closed = true;

    private void Live()
    {
        if (_closed) throw new ArgumentException($"block kind {KindId}: the compile context of \"{Name}\" was used after Compile returned; it is valid only during that call");
    }

    /// <summary>A stable GUID for something this block makes: the same for ever for this consumer mod, block name,
    /// <paramref name="domain"/> and <paramref name="localName"/>. Registers nothing.</summary>
    [AnyThread]
    public Guid GuidOf(string domain, string localName)
    {
        Live();
        KindCompiler.Key(domain, nameof(domain), Label);
        KindCompiler.Key(localName, nameof(localName), Label);
        return BlockIds.Derive(ModId, domain, $"kind-{Name}.{localName}");
    }

    /// <summary>The identity of a block declared before this compile began: one of the consumer's (<c>name</c>) or another
    /// mod's (<c>mod:name</c>). A copy of its ids; never the block. Missing or of another kind: <see cref="BlockError"/>.</summary>
    [AnyThread]
    public ResolvedBlock<T> Resolve<T>(BlockRef<T> reference) where T : class, IBlock
    {
        Live();
        var value = reference.Value ?? "";
        var colon = value.IndexOf(':');
        var (mod, name) = colon > 0 ? (value[..colon], value[(colon + 1)..]) : (ModId, value);
        if (colon == 0 || name.Length == 0 || name.Length > 128 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
            throw new BlockError(Label, value, "a block reference is \"name\" or \"mod:name\"");
        if (!_identities.TryGetValue((mod, name), out var found))
            throw new BlockError(Label, value, colon > 0 ? $"no block \"{name}\" in mod {mod} (is it loaded before this one?)" : $"no block \"{name}\" in mod {mod}; define it before this one");
        if (found.BlockType != typeof(T))
            throw new BlockError(Label, value, $"\"{value}\" is a {found.BlockType.Name}, not a {typeof(T).Name}");
        return new ResolvedBlock<T> { ModId = mod, Name = name, Guid = found.Guid, Id = BlockIds.Fnv(found.Guid), GameName = found.GameName, KindId = found.KindId };
    }

    /// <summary>The name by which a block of this plan names another product of it (an effect, in a field that takes an
    /// effect by name): <c>consumer-mod:kind-&lt;block name&gt;.&lt;key&gt;</c>.</summary>
    [AnyThread]
    public string ProductName(string key)
    {
        Live();
        KindCompiler.Key(key, nameof(key), Label);
        return $"{ModId}:{KindCompiler.StagedName(Name, key)}";
    }

    /// <summary>An item as a product of the plan. The block is frozen now: changing it afterwards changes nothing.</summary>
    [AnyThread] public BlockProduct Item(string key, ItemBlock block) => Product(key, BuiltInBlockProductKind.Item, block);
    /// <summary>A weapon as a product of the plan.</summary>
    [AnyThread] public BlockProduct Weapon(string key, WeaponBlock block) => Product(key, BuiltInBlockProductKind.Weapon, block);
    /// <summary>A spell as a product of the plan.</summary>
    [AnyThread] public BlockProduct Spell(string key, SpellBlock block) => Product(key, BuiltInBlockProductKind.Spell, block);
    /// <summary>A status as a product of the plan.</summary>
    [AnyThread] public BlockProduct Status(string key, StatusBlock block) => Product(key, BuiltInBlockProductKind.Status, block);
    /// <summary>An effect as a product of the plan: the primary, or one of up to 32 the primary names by <see cref="ProductName"/>.</summary>
    [AnyThread] public BlockProduct Effect(string key, EffectBlock block) => Product(key, BuiltInBlockProductKind.Effect, block);

    private string Label => $"{KindId} {Name}";

    private BlockProduct Product(string key, BuiltInBlockProductKind kind, IBlock block)
    {
        Live();
        ArgumentNullException.ThrowIfNull(block);
        KindCompiler.Key(key, nameof(key), Label);
        if (_keys.Contains(key)) throw new BlockError(Label, key, $"the plan has a product \"{key}\" already");
        if (_keys.Count >= KindCompiler.MaxProducts) throw new BlockError(Label, key, $"a plan has at most {KindCompiler.MaxProducts} products (one primary and {KindCompiler.MaxProducts - 1} effects)");
        var (json, nodes, bytes) = KindCompiler.Freeze(Label, key, block);
        if (_nodes + nodes > KindCompiler.MaxNodes) throw new BlockError(Label, key, $"the plan's products have more than {KindCompiler.MaxNodes} objects and lists together");
        if (_bytes + bytes > KindCompiler.MaxPlanBytes) throw new BlockError(Label, key, $"the plan's products are larger than {KindCompiler.MaxPlanBytes >> 20} MiB together");
        _nodes += nodes;
        _bytes += bytes;
        _keys.Add(key);
        return new BlockProduct(_token, key, kind, json, nodes);
    }

    /// <summary>The plan: <paramref name="primaryKey"/> names the one primary product; every other product is an effect.
    /// The list is copied. Call once, and return what it gives.</summary>
    [AnyThread]
    public BlockPlan Plan(string primaryKey, IReadOnlyList<BlockProduct> products)
    {
        Live();
        ArgumentNullException.ThrowIfNull(products);
        KindCompiler.Key(primaryKey, nameof(primaryKey), Label);
        if (_planned) throw new BlockError(Label, null, "Plan was called already for this block: a compile makes one plan");
        var copy = products.ToArray();
        if (copy.Length == 0) throw new BlockError(Label, null, "a plan needs its primary product");
        if (copy.Length > KindCompiler.MaxProducts) throw new BlockError(Label, null, $"{copy.Length} products; a plan has one primary and at most {KindCompiler.MaxProducts - 1} effects");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in copy)
        {
            if (p == null) throw new ArgumentException($"block kind {KindId}: a product of \"{Name}\" is null", nameof(products));
            if (!ReferenceEquals(p.Context, _token)) throw new ArgumentException($"block kind {KindId}: product \"{p.Key}\" of \"{Name}\" was made by another compile; a plan takes only its own context's products", nameof(products));
            if (!seen.Add(p.Key)) throw new BlockError(Label, p.Key, "the product is in the plan twice");
        }
        var primary = copy.FirstOrDefault(p => p.Key == primaryKey) ?? throw new BlockError(Label, primaryKey, $"no product \"{primaryKey}\" in the plan; there are: {string.Join(", ", copy.Select(p => p.Key))}");
        foreach (var p in copy)
            if (!ReferenceEquals(p, primary) && p.Kind != BuiltInBlockProductKind.Effect)
                throw new BlockError(Label, p.Key, $"a plan has one primary (\"{primaryKey}\"); every other product is an effect, and this is a {p.Kind.ToString().ToLowerInvariant()}");
        Array.Sort(copy, (a, b) => string.CompareOrdinal(a.Key, b.Key));
        _planned = true;
        return new BlockPlan(_token, _kind, _consumer, _request, Name, primaryKey, KindCompiler.Hash(_recordJson, _kind.DescriptorJson, copy), copy, _recordJson);
    }

    internal object Token => _token;
}

internal static partial class KindCompiler
{
    // chapter 16d section 5
    internal const int MaxProducts = 33, MaxDepth = 32, MaxNodes = 4096, MaxProductBytes = 64 << 10, MaxPlanBytes = 1 << 20;

    [ThreadStatic] private static BlockCompileContext? _active;
    private static long _requests;

    internal sealed record Identity(Guid Guid, string GameName, Type BlockType, string KindId);

    [GeneratedRegex("^[A-Za-z0-9._-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    internal static void Key(string? value, string what, string label)
    {
        if (value == null || !KeyPattern().IsMatch(value)) throw new BlockError(label, what, $"\"{(value is { Length: > 48 } ? value[..48] + "..." : value)}\" takes letters, digits, '.', '_' and '-', 1 to 32 of them");
    }

    /// <summary>The name a product is staged under in the consumer mod.</summary>
    internal static string StagedName(string outer, string key) => $"kind-{outer}.{key}";

    /// <summary>A provider's Compile may not come back into the kinds API: said, instead of a deadlock on its own lock.</summary>
    internal static void RefuseInsideCompile(string what)
    {
        if (_active is { } c) throw new BlockError($"{c.KindId} {c.Name}", null, $"{what} was called from inside a block kind's Compile: a compiler is pure, and gets what it needs from its context");
    }

    internal static string BuiltInKindId(Type t) => "kiln:" + t.Name.Replace("Block", "").ToLowerInvariant();

    /// <summary>The identities a compile may resolve: every published block, as of now.</summary>
    internal static Dictionary<(string, string), Identity> Identities(IEnumerable<(string Mod, string Name, Identity Identity)>? provisional = null)
    {
        var view = new Dictionary<(string, string), Identity>();
        foreach (var mod in Blocks.AllMods)
            foreach (var (name, h) in mod.Handles)
                view[(mod.Id, name)] = new Identity(h.Guid, h.GameName, h.BlockType, KindRegistry.IsBuiltIn(h.BlockType) ? BuiltInKindId(h.BlockType) : KindRegistry.Find(h.BlockType)?.Id ?? "");
        foreach (var (mod, name, identity) in provisional ?? []) view[(mod, name)] = identity;
        return view;
    }

    /// <summary>Runs the provider's compiler for one block and returns the frozen plan.</summary>
    internal static BlockPlan Compile(KindOwner consumer, KindEntry kind, string name, IBlock block, IEnumerable<(string, string, Identity)>? provisional = null)
    {
        RefuseInsideCompile("Blocks.Compile");
        var label = $"{kind.Id} {name}";
        if (name == null || !NamePattern().IsMatch(name)) throw new BlockError(label, "name", "a block name takes letters, digits, '.', '_' and '-', 1 to 64 of them");
        var (compile, options, type) = (kind.Compile, kind.Options, kind.RecordType);
        if (kind.Retired || compile == null || options == null || type == null)
            throw new BlockError(label, null, $"block kind {kind.Id} is no longer registered: its provider mod was unloaded");
        if (block.GetType() != type) throw new ArgumentException($"block {label}: a {block.GetType().Name} is not kind {kind.Id}'s record, {type.Name}");
        // The provider compiles a copy and the handle shows another: neither is the author's object, and neither is read again.
        var recordJson = KindJson.RecordJson(kind, label, block, options);
        if (Encoding.UTF8.GetByteCount(recordJson) > MaxPlanBytes) throw new BlockError(label, null, $"the block is larger than {MaxPlanBytes >> 20} MiB");
        IBlock input, projection;
        try
        {
            input = (IBlock)(JsonSerializer.Deserialize(recordJson, type, options) ?? throw new JsonException("null"));
            projection = (IBlock)(JsonSerializer.Deserialize(recordJson, type, options) ?? throw new JsonException("null"));
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new BlockError(label, null, $"a block of kind {kind.Id} must read back from its own JSON: {e.Message}");
        }
        var request = (ulong)Interlocked.Increment(ref _requests);
        var context = new BlockCompileContext(kind, consumer, request, name, recordJson, Identities(provisional));
        BlockPlan? plan;
        lock (kind.CallbackLock)
        {
            _active = context;
            try { plan = compile(context, input); }
            finally
            {
                context.Close();
                _active = null;
            }
        }
        if (plan == null) throw new ArgumentException($"block kind {kind.Id}: Compile returned null for \"{name}\"; it returns context.Plan(...)");
        if (!ReferenceEquals(plan.Context, context.Token)) throw new ArgumentException($"block kind {kind.Id}: Compile returned the plan of another compile for \"{name}\"");
        plan.Projection = projection;
        return plan;
    }

    internal static string Hash(string recordJson, string descriptorJson, BlockProduct[] sorted)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            h.AppendData(BitConverter.GetBytes((long)bytes.Length));
            h.AppendData(bytes);
        }
        Add(recordJson);
        Add(descriptorJson);
        foreach (var p in sorted)
        {
            Add(p.Key);
            Add(p.Kind.ToString());
            Add(p.CanonicalJson);
        }
        return Convert.ToHexStringLower(h.GetHashAndReset());
    }

    // ---- freezing a built-in block ------------------------------------------------------------------------------

    /// <summary>The five a kind may make. A block of any other kind written inline in one of them is refused.</summary>
    internal static readonly Type[] Admitted = [typeof(ItemBlock), typeof(WeaponBlock), typeof(SpellBlock), typeof(StatusBlock), typeof(EffectBlock)];

    /// <summary>Kinds of blocks/*.json that an admitted block holds inline as a part of itself: they add no product of
    /// their own kind (a recipe is the item's, a look names game models). KindFreezeTests pins this list against the model.</summary>
    internal static readonly Type[] InlineParts = [typeof(RecipeBlock), typeof(VisualBlock), typeof(WeaponStats), typeof(WeaponClass), typeof(OnHitBlock)];

    private static readonly Lazy<HashSet<Type>> Definable = new(() => [.. BlockJson.KindTypes()]);

    /// <summary>Whether an inline block of this type may sit inside a product.</summary>
    internal static bool InlineAllowed(Type t) =>
        Array.IndexOf(Admitted, t) >= 0 || Array.IndexOf(InlineParts, t) >= 0
        || (!Definable.Value.Contains(t) && !typeof(INpcBlock).IsAssignableFrom(t) && !typeof(IQuestBlock).IsAssignableFrom(t) && !typeof(IUiBlock).IsAssignableFrom(t));

    /// <summary>A built-in block as canonical JSON, checked: plain data only, no inline block of a kind outside the five, within the bounds.</summary>
    internal static (string Json, int Nodes, int Bytes) Freeze(string label, string key, IBlock block)
    {
        Walk(label, key, block, key, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        JsonNode? node;
        try
        {
            node = JsonSerializer.SerializeToNode(block, block.GetType(), BlockJson.SharedOptions);
        }
        catch (Exception e) when (e is NotSupportedException or JsonException or InvalidOperationException)
        {
            throw new BlockError(label, key, $"a product must be plain data, as blocks/*.json writes it: {e.Message}");
        }
        var (depth, nodes) = KindJson.Measure(node);
        if (depth > MaxDepth) throw new BlockError(label, key, $"the product nests {depth} deep; at most {MaxDepth}");
        if (nodes > MaxNodes) throw new BlockError(label, key, $"the product has {nodes} objects and lists; a plan has at most {MaxNodes}");
        var json = KindJson.Canonical(node);
        var bytes = Encoding.UTF8.GetByteCount(json);
        if (bytes > MaxProductBytes) throw new BlockError(label, key, $"the product is {bytes} bytes of JSON; at most {MaxProductBytes >> 10} KiB");
        return (json, nodes, bytes);
    }

    // What JSON would not show: a delegate ([JsonIgnore]d or not) and an inline block of a kind a plan cannot make.
    private static void Walk(string label, string key, object value, string path, int depth, HashSet<object> seen)
    {
        if (depth > MaxDepth * 2) throw new BlockError(label, path, $"the product nests more than {MaxDepth} deep");
        var type = value.GetType();
        if (value is Delegate) throw new BlockError(label, path, "code (a delegate) cannot be part of a kind's product: a product is plain data");
        if (type.IsPrimitive || type.IsEnum || value is string or decimal or Guid or DateTime or TimeSpan or JsonNode or JsonElement or Type) return;
        if (!type.IsValueType && !seen.Add(value)) return;
        if (value is IBlock && depth > 0 && !InlineAllowed(type))
            throw new BlockError(label, path, $"an inline {type.Name} is outside what a block kind makes (an item, a weapon, a spell, a status or an effect, with their own parts); name a block that is defined already instead");
        if (value is IDictionary map)
        {
            foreach (DictionaryEntry e in map)
                if (e.Value != null) Walk(label, key, e.Value, $"{path}[{e.Key}]", depth + 1, seen);
            return;
        }
        if (value is IEnumerable list)
        {
            var i = 0;
            foreach (var x in list)
            {
                if (x != null) Walk(label, key, x, $"{path}[{i}]", depth + 1, seen);
                i++;
            }
            return;
        }
        if (type.Assembly != typeof(IBlock).Assembly && type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true && !type.IsGenericType) return;
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead) continue;
            object? v;
            try { v = p.GetValue(value); }
            catch (TargetInvocationException) { continue; }   // a computed property that cannot be computed is not data
            if (v != null) Walk(label, key, v, $"{path}.{char.ToLowerInvariant(p.Name[0])}{p.Name[1..]}", depth + 1, seen);
        }
        foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (f.GetValue(value) is { } v) Walk(label, key, v, $"{path}.{f.Name}", depth + 1, seen);
    }
}
