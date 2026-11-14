// SPDX-License-Identifier: MIT
// The kit's kiln.assets (Fakes/Assets/) in a KilnHarness (0.2.0-dev.28): registered as an IKitFake, content blobs and the
// blob catalog served through Kiln.Assets' own code, resources not claimed (ReflectedResources asks assets.resources).
using Xunit;

namespace Kiln.TestKit.Tests;

public sealed class AssetsFakeTests
{
    private sealed class BlobMod : Mod;

    private static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 5 + seed)).ToArray();

    [Fact]
    public void Every_harness_has_kiln_assets_with_content_on_and_resources_offline()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        Assert.True(KitBackend.Instance.HasTable("kiln.assets"));
        Assert.True(kiln.Run(() => Capabilities.Is("kiln.assets")));
        Assert.True(kiln.Run(() => Capabilities.Is("assets.content")), kiln.Run(() => Capabilities.Why("assets.content")));
        Assert.True(kiln.Run(() => Capabilities.Is("assets.blob_catalog")), kiln.Run(() => Capabilities.Why("assets.blob_catalog")));
        Assert.False(kiln.Run(() => Capabilities.Is("assets.resources")));  // what ResourceStore.Available asks
        Assert.Equal(CapReason.Offline, KitBackend.Instance.Capabilities.Get("assets.resources").Reason);
        Assert.Contains("unavailable (offline)", kiln.Capabilities.Why("assets.resources"));
        Assert.Equal(CapReason.Offline, KitBackend.Instance.Capabilities.Get("assets.ui_icons").Reason);
        Assert.Contains(Coverage.All, r => r.Area == "Assets" && r.Kind == CoverageKind.RecordingFake && r.Capabilities.Contains("assets.blob_catalog"));
    }

    [Fact]
    public void Reflected_resources_claim_nothing_the_fake_cannot_serve()
    {
        using var kiln = new KilnHarness();
        kiln.Step();
        // kiln.assets is here, but its resources are not modelled: nothing says they can be read
        Assert.True(KitBackend.Instance.HasTable("kiln.assets"));
        Assert.False(kiln.Run(() => ReflectedResources.Available));
        Assert.Null(kiln.Run(() => ReflectedResources.Get(Guid.NewGuid(), "keen::ItemInfo")));
        Assert.Empty(kiln.Run(() => ReflectedResources.All("keen::ItemInfo").ToList()));
        Assert.Null(kiln.Run(() => Assets.Reflect(new AssetKey(Guid.NewGuid(), AssetKey.UiTextureType, 0))));
    }

    [Fact]
    public void The_blob_catalog_opens_through_a_harness_over_the_scripted_game_index()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<BlobMod>(new ModManifest("blob-kit"));
        var game = kiln.Assets.AddGameBlob(Bytes(40, 1));
        kiln.Assets.AddGameBlob(Bytes(40, 1));  // the same bytes twice: one entry
        kiln.Step(2);
        var before = kiln.Assets.CatalogGeneration;
        var added = kiln.As(mod, () => Assets.AddBlob(Bytes(12, 9)));
        Assert.True(kiln.Assets.CatalogGeneration > before);
        Assert.Equal(added.ToString(), Assert.Single(kiln.Assets.Registrations).Hash);

        var opened = kiln.As(mod, () => Assets.OpenBlobCatalog());
        Assert.True(opened.Ok, opened.Reason);
        using (var snapshot = opened.Snapshot!)
        {
            Assert.True(snapshot.Info.GameComplete);
            var entries = snapshot.ReadPage(0);
            Assert.Equal(2, entries.Count);
            Assert.Equal(added.ToString(), entries[0].ContentHash.ToString());  // sorted by size: 12 bytes first
            Assert.Equal(BlobOrigins.Managed, entries[0].SelectedOrigin);
            Assert.Equal(game, entries[1].ContentHash.ToString());
            Assert.Equal(BlobOrigins.Game, entries[1].Origins);
        }
        Assert.Equal(Bytes(12, 9), kiln.As(mod, () => Assets.ReadBlob(added)));

        // an index that cannot be read: the default mode is refused with the capability, registrations-only answers
        kiln.Assets.IndexReady = false;
        Assert.Throws<KilnUnavailableException>(() => kiln.As(mod, () => Assets.OpenBlobCatalog()));
        var partial = kiln.As(mod, () => Assets.OpenBlobCatalog(BlobCatalogMode.RegistrationsOnly));
        Assert.True(partial.Ok, partial.Reason);
        using (var snapshot = partial.Snapshot!)
            Assert.Equal(1UL, snapshot.Total);
    }

    [Fact]
    public void Switching_assets_content_off_takes_the_blobs_away()
    {
        using var kiln = new KilnHarness();
        var mod = kiln.Load<BlobMod>(new ModManifest("blob-off-kit"));
        kiln.Capabilities.Override("assets.content", false);
        kiln.Step(2);
        Assert.Throws<KilnUnavailableException>(() => kiln.As(mod, () => Assets.AddBlob(Bytes(8, 3))));
        Assert.Empty(kiln.Assets.Registrations);
    }
}
