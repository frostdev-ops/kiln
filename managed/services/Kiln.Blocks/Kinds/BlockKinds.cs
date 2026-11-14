// SPDX-License-Identifier: MIT
// Block kinds a mod adds (docs/kiln/modding/block-kinds.md): a provider mod registers a typed record and a pure compiler
// that turns it into one built-in primary block (item, weapon, spell, status or effect) and up to 32 effects. Kiln freezes
// what the compiler returns and lowers it with its own compilers; provider code never touches a service.
namespace Kiln;

/// <summary>A block kind's compiler. <see cref="Compile"/> is pure: it reads the block and the context, and returns
/// <see cref="BlockCompileContext.Plan"/>. It runs once per compile, never again for a plan that was made, and must not
/// call <see cref="Blocks.Compile{T}"/>, <see cref="Blocks.RegisterKind{T}"/> or <see cref="Blocks.Apply"/>.
/// This interface never gets another member: later facilities are other interfaces a compiler may also implement.</summary>
public interface IBlockKind<T> where T : class, IBlock
{
    /// <summary>What <paramref name="block"/> becomes. <paramref name="context"/> is valid only until this returns.</summary>
    BlockPlan Compile(BlockCompileContext context, T block);
}

/// <summary>What a block kind is called and asks for. Kiln copies it at registration.</summary>
public sealed record BlockKindDescriptor
{
    /// <summary><c>provider-mod:kind</c>. The part before the colon is the registering mod's id.</summary>
    public required string Id { get; init; }
    /// <summary>The section of blocks/*.json this kind reads: <c>provider-mod:plural</c>.</summary>
    public required string JsonSection { get; init; }
    /// <summary>The format of the kind's blocks. Only 1.</summary>
    public int Format { get; init; } = 1;
    /// <summary>JSON Schema (2020-12) of one block's payload, without <c>format</c>. At most 64 KiB; only local <c>#</c> references.</summary>
    public required string SchemaJson { get; init; }
    /// <summary>Capability names a plan of this kind needs when it is applied.</summary>
    public IReadOnlyList<string> Requires { get; init; } = [];
    /// <summary>The least this kind asks of players' games. What a plan's products ask can only raise it.</summary>
    public ClientsLevel Clients { get; init; }
}

/// <summary>The built-in blocks a kind may make.</summary>
public enum BuiltInBlockProductKind { Item, Weapon, Spell, Status, Effect }

/// <summary>One frozen built-in block of a plan. Made by <see cref="BlockCompileContext"/>'s factories.</summary>
public sealed class BlockProduct
{
    internal BlockProduct(object context, string key, BuiltInBlockProductKind kind, string canonicalJson, int nodes)
    {
        Context = context;
        Key = key;
        Kind = kind;
        CanonicalJson = canonicalJson;
        Nodes = nodes;
    }

    internal object Context { get; }
    internal int Nodes { get; }
    /// <summary>The product's name inside its plan.</summary>
    public string Key { get; }
    public BuiltInBlockProductKind Kind { get; }
    /// <summary>The built-in block, as canonical JSON (the shape blocks/*.json takes, keys in ordinal order).</summary>
    public string CanonicalJson { get; }
}

/// <summary>What one block of an external kind becomes: frozen. Made by <see cref="BlockCompileContext.Plan"/>; nothing of
/// it changes afterwards, whatever the provider or the author does with the objects they passed.</summary>
public sealed class BlockPlan
{
    internal BlockPlan(object context, KindEntry kind, KindOwner consumer, ulong requestId, string name, string primaryKey, string inputHash, BlockProduct[] products, string recordJson)
    {
        Context = context;
        Kind = kind;
        Consumer = consumer;
        RequestId = requestId;
        KindId = kind.Id;
        ConsumerModId = consumer.ModId;
        Name = name;
        PrimaryKey = primaryKey;
        InputHash = inputHash;
        _products = products;
        RecordJson = recordJson;
        Requires = kind.Descriptor.Requires;
        Clients = kind.Descriptor.Clients;
    }

    private readonly BlockProduct[] _products;
    internal object Context { get; }
    internal KindEntry Kind { get; }
    internal KindOwner Consumer { get; }
    internal ulong RequestId { get; }
    /// <summary>The author's record as canonical JSON: what the provider compiled.</summary>
    internal string RecordJson { get; }
    internal IReadOnlyList<string> Requires { get; }
    internal ClientsLevel Clients { get; }
    /// <summary>A copy of the author's record, for the handle's <c>Block</c>. Informational: the plan does not read it.</summary>
    internal IBlock? Projection { get; set; }
    internal BlockProduct Primary => _products.First(p => p.Key == PrimaryKey);

    public string KindId { get; }
    public string ConsumerModId { get; }
    /// <summary>The block's name in the consumer mod.</summary>
    public string Name { get; }
    public string PrimaryKey { get; }
    /// <summary>SHA-256 (hex) of the canonical record, the descriptor and the frozen products.</summary>
    public string InputHash { get; }
    /// <summary>The products, in key order. A copy.</summary>
    public IReadOnlyList<BlockProduct> Products => Array.AsReadOnly((BlockProduct[])_products.Clone());
}

/// <summary>What <see cref="Blocks.RegisterKind{T}"/> gives the provider: dispose it to take the kind away. Plans already
/// applied stay; nothing compiles with the kind afterwards.</summary>
public sealed class BlockKindRegistration : IDisposable
{
    private readonly KindEntry _entry;
    internal BlockKindRegistration(KindEntry entry) => _entry = entry;

    public string Id => _entry.Id;

    /// <summary>Takes the kind away. Only the mod instance that registered it may; again by it does nothing.</summary>
    [AnyThread]
    public void Dispose() => KindRegistry.Dispose(_entry, KindOwner.Caller());
}

/// <summary>What <see cref="Blocks.RegisterKind{T}"/> answers.</summary>
public sealed record BlockKindResult : IKilnResult
{
    public bool Ok { get; init; }
    public string Reason { get; init; } = "";
    public BlockKindRegistration? Registration { get; init; }
}

/// <summary>A kind was registered, or (<see cref="Removed"/>) taken away. Posted on the tick thread.</summary>
public sealed class BlockKindRegisteredEvent : KilnEvent
{
    internal BlockKindRegisteredEvent(string kindId, string providerModId, bool removed)
    {
        KindId = kindId;
        ProviderModId = providerModId;
        Removed = removed;
    }

    public string KindId { get; }
    public string ProviderModId { get; }
    public bool Removed { get; }
}
