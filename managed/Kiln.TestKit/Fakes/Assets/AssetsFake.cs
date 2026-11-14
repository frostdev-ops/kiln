// SPDX-License-Identifier: MIT
// The kit's kiln.assets table (AssetsTable.cs, 0.2.0-dev.28): content blobs and the content blob catalog, for unit tests
// that publish it themselves (managed/tests/Kiln.Api.Tests/BlobCatalogTests.cs: a RecordingBackend table and
// AssetsFake.Unit). It is not an IKitFake yet, so a KilnHarness still has no kiln.assets: Kiln.Api's ReflectedResources
// takes "kiln.assets is here" for "resources can be read" (ResourceStore.Available), and this fake does not model
// resources. Once that check asks the assets.resources row, the class below can implement IKitFake (Register: the table,
// Features(["assets.content"]), Offline for assets.resources and assets.ui_icons; state per harness from KitFakes.State).
namespace Kiln.TestKit;

/// <summary>Where the kit's kiln.assets table keeps its state.</summary>
internal static class AssetsFake
{
    /// <summary>A unit test's state for the table it publishes (null: the table answers as kiln.assets with its content
    /// hooks off).</summary>
    internal static AssetsState? Unit;
}
