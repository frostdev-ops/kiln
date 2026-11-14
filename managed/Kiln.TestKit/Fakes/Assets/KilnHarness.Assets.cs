// SPDX-License-Identifier: MIT
namespace Kiln.TestKit;

public sealed partial class KilnHarness
{
    private HarnessAssets? _assets;

    /// <summary>The kit's kiln.assets of this harness: the game's content index the blob catalog reads, and the content
    /// blobs mods registered.</summary>
    public HarnessAssets Assets => _assets ??= new HarnessAssets(this);
}

/// <summary>A content blob a mod registered with the kit's kiln.assets (<c>Assets.AddBlob</c>, <c>OverrideBlob</c>).</summary>
/// <param name="Hash">Its content hash as <c>ContentHash.ToString()</c> writes it (<c>size-h0-h1-h2</c>, hex).</param>
/// <param name="Owner">The native owner it was registered under (the mod's).</param>
/// <param name="Override">True for an override of a hash, false for an added blob.</param>
public readonly record struct FakeBlobRegistration(string Hash, ulong Owner, bool Override);

/// <summary>
/// The kit's kiln.assets of a harness (<see cref="KilnHarness.Assets"/>): Kiln.Assets' own code runs on it.
/// </summary>
/// <remarks>
/// <para><b>Modelled.</b> Content blobs (add, override, read, remove, revert when a mod unloads) and the blob catalog over
/// the game index a test scripts here (<see cref="AddGameBlob"/>, <see cref="IndexReady"/>), with kiln.assets' rules.
/// The hashes are the kit's own (the size and the first 12 bytes of the SHA-256), not the game's.</para>
/// <para><b>Not modelled.</b> Game resources (<c>assets.resources</c>), UI icons (<c>assets.ui_icons</c>), textures, dumps
/// and manifests: their rows are offline and <c>ReflectedResources.Available</c> is false.</para>
/// </remarks>
public sealed class HarnessAssets
{
    private readonly KilnHarness _harness;

    internal HarnessAssets(KilnHarness harness) => _harness = harness;

    private AssetsState State => KitFakes.State<AssetsState>(_harness);

    private static string Text(AssetsAbi.Hash h) => $"{h.Size}-{h.H0:x8}-{h.H1:x8}-{h.H2:x8}";

    /// <summary>Adds <paramref name="bytes"/> to the game's content index (in order; a hash already there is not listed
    /// twice by the catalog). Returns its hash as <c>ContentHash.ToString()</c> writes it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    public string AddGameBlob(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var h = AssetsTable.HashOf(bytes);
        var s = State;
        lock (s.Gate) s.Game.Add((h, (byte[])bytes.Clone()));
        return Text(h);
    }

    /// <summary>Whether the game's content index can be read. False: the catalog's default mode refuses with
    /// <c>NotReady</c>, the registrations-only mode still answers. Default true.</summary>
    public bool IndexReady
    {
        get { lock (State.Gate) return State.IndexReady; }
        set { lock (State.Gate) State.IndexReady = value; }
    }

    /// <summary>The live registrations, in the order that decides which one is served (the later one).</summary>
    public IReadOnlyList<FakeBlobRegistration> Registrations
    {
        get { lock (State.Gate) return [.. State.Ledger.Select(r => new FakeBlobRegistration(Text(r.Hash), r.Owner, r.Override))]; }
    }

    /// <summary>The catalog's generation: it grows with every change of the registrations.</summary>
    public ulong CatalogGeneration { get { lock (State.Gate) return State.Generation; } }
}
