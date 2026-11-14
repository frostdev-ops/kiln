// SPDX-License-Identifier: MIT
// The kit's kiln.assets (AssetsTable.cs, 0.2.0-dev.28) in every KilnHarness: content blobs and the content blob catalog
// over the test's game index (KilnHarness.Assets). Resources, icons, textures, dumps and manifests are not modelled: their
// rows are offline, and Kiln.Api's ReflectedResources asks the assets.resources row (ResourceStore.Available), so it does
// not take "kiln.assets is here" for "resources can be read". Unit tests that publish the table on a backend of their own
// (managed/tests/Kiln.Api.Tests/BlobCatalogTests.cs) set Unit instead.
namespace Kiln.TestKit;

/// <summary>The kit's kiln.assets as a self-registering fake (<see cref="IKitFake"/>).</summary>
internal sealed class AssetsFake : IKitFake
{
    public const string ContentRow = "assets.content";
    public const string ResourcesRow = "assets.resources";
    public const string IconsRow = "assets.ui_icons";
    public const string CatalogRow = "assets.blob_catalog";

    /// <summary>A unit test's state for the table it publishes on its own backend (null: the running harness's state,
    /// else the table answers as kiln.assets with its content hooks off).</summary>
    internal static AssetsState? Unit;

    /// <summary>The state the table answers from now: a unit test's, else the running harness's while kiln.assets and
    /// its content row are on; null otherwise (kiln.assets with its content hooks off).</summary>
    internal static AssetsState? Current
    {
        get
        {
            if (Unit is { } unit) return unit;
            if (FakeTables.Active(AssetsAbi.Service) is not { } harness || FakeTables.Off(ContentRow)) return null;
            return KitFakes.State<AssetsState>(harness);
        }
    }

    public string Service => AssetsAbi.Service;

    public void Register()
    {
        KitBackend.Instance.RegisterTable(AssetsAbi.Service, AssetsTable.Create());
        FakeTables.Features([ContentRow]);
        FakeTables.Offline(ResourcesRow, "game resources are not modelled by the test kit's kiln.assets (content blobs and the blob catalog are)");
        FakeTables.Offline(IconsRow, "UI icons are not modelled by the test kit's kiln.assets");
    }

    public void Start(KilnHarness harness) { }
    public void BeforeStep(KilnHarness harness, ulong step) { }

    // what mods registered goes with the harness (a native kiln.assets reverts each owner when its mod unloads)
    public void End(KilnHarness harness)
    {
        var s = KitFakes.State<AssetsState>(harness);
        lock (s.Gate)
        {
            s.Ledger.Clear();
            s.Handles.Clear();
        }
    }

    public void AddCoverage(List<CoverageRow> rows) =>
        rows.Add(new CoverageRow("Assets", CoverageKind.RecordingFake, [AssetsAbi.Service, ContentRow, CatalogRow],
            "Kiln.Assets' own code on the kit's kiln.assets: content blobs (AddBlob, OverrideBlob, ReadBlob, RemoveBlob) and the blob catalog " +
            "(OpenBlobCatalog) over the game index a test scripts (KilnHarness.Assets.AddGameBlob, IndexReady), with kiln.assets' rules: one entry per " +
            "hash, the later registration served, sorted snapshots that never change, 64 handles, NotReady while the index cannot be read. The hashes " +
            "are the kit's own (size and SHA-256), not the game's. Resources (assets.resources), UI icons (assets.ui_icons), textures, dumps and " +
            "manifests are offline: ReflectedResources.Available is false and Assets' resource calls throw."));
}
