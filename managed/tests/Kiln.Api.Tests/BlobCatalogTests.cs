// SPDX-License-Identifier: MIT
// Assets.OpenBlobCatalog (0.2.0-dev.28) over the test kit's kiln.assets (managed/Kiln.TestKit/Fakes/Assets): the snapshot's
// entries and origins, paging, immutability, registrations-only, not ready, owner lifetimes, a closed handle, the budget
// refusal, the capability off with an older table, the JSON shape (schema/blob-catalog.schema.json) and the change event.
using System.Runtime.InteropServices;
using System.Text.Json;
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace Kiln.Api.Tests;

public sealed unsafe class BlobCatalogTests : KilnTest
{
    private const string Cap = "assets.blob_catalog";
    private readonly AssetsState _state = new();
    private FakeServiceRegistration _service;

    public BlobCatalogTests()
    {
        AssetsFake.Unit = _state;
        _service = new(Backend.Tables, "kiln.assets", AssetsTable.Create());
        ResetAssets();
    }

    public override void Dispose()
    {
        try { base.Dispose(); }
        finally
        {
            ResetAssets();
            _service.Dispose();
            AssetsFake.Unit = null;
        }
    }

    // Kiln.Assets remembers its table: each test starts with the one it publishes
    private static void ResetAssets() =>
        typeof(Assets).GetField("_table", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, null);

    private static byte[] Bytes(int n, byte seed) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + seed)).ToArray();

    private static ContentHash Hash(string text) => BlobCatalogJson.ParseHash(text);

    private static List<BlobCatalogEntry> All(BlobCatalogSnapshot s)
    {
        var all = new List<BlobCatalogEntry>();
        for (ulong at = 0; at < s.Total;)
        {
            var page = s.ReadPage(at, 2);
            Assert.NotEmpty(page);
            all.AddRange(page);
            at += (ulong)page.Count;
        }
        return all;
    }

    [Fact]
    public void The_kits_table_mirrors_Kiln_Assets_ABI()
    {
        Assert.Equal(sizeof(AssetsNative.TableEx), sizeof(AssetsAbi.Table));
        Assert.Equal(Marshal.OffsetOf<AssetsNative.TableEx>("BlobCatalogOpen") - Marshal.OffsetOf<AssetsNative.TableEx>("BlobAddEx"),
            Marshal.OffsetOf<AssetsAbi.Table>("BlobCatalogOpen") - Marshal.OffsetOf<AssetsAbi.Table>("BlobAddEx"));
        Assert.Equal(32, sizeof(AssetsNative.CatalogInfo));
        Assert.Equal(48, sizeof(AssetsNative.CatalogEntry));
        Assert.Equal(sizeof(AssetsNative.CatalogEntry), sizeof(AssetsAbi.CatalogEntry));
        Assert.Equal(24, (int)Marshal.OffsetOf<AssetsNative.CatalogEntry>("Origins"));
        Assert.Equal(40, (int)Marshal.OffsetOf<AssetsNative.CatalogEntry>("SelectedSequence"));
    }

    [Fact]
    public void A_complete_snapshot_lists_the_games_blobs_and_every_registration_with_its_origins()
    {
        var gameOnly = Hash(AddGame(Bytes(40, 1)));
        var overridden = Hash(AddGame(Bytes(24, 2)));
        var mod = NewMod("catalog-mod");
        var added = As(mod, () => Assets.AddBlob(Bytes(16, 3)));
        As(mod, () => Assets.OverrideBlob(overridden, Bytes(24, 9)));
        var r = As(mod, () => Assets.OpenBlobCatalog());
        Assert.True(r.Ok);
        Assert.Null(r.Reason);
        using var snap = r.Snapshot!;
        Assert.Equal(new BlobCatalogInfo(snap.Info.Generation, 3, BlobCatalogStatus.Complete, true, true, BlobCatalogReason.None), snap.Info);
        Assert.Same(snap.Info, r.Info);
        var all = All(snap);
        Assert.Equal(all.OrderBy(e => e.ContentHash.Size).ThenBy(e => e.ContentHash.H0).ThenBy(e => e.ContentHash.H1).ThenBy(e => e.ContentHash.H2), all);
        Assert.Equal(new BlobCatalogEntry(gameOnly, BlobOrigins.Game, BlobOrigins.Game, 0, 0, false), all.Single(e => e.ContentHash == gameOnly));
        var over = all.Single(e => e.ContentHash == overridden);
        Assert.Equal((BlobOrigins.Game | BlobOrigins.Managed, BlobOrigins.Managed, mod.Owner, true), (over.Origins, over.SelectedOrigin, over.Owner, over.Overridden));
        var add = all.Single(e => e.ContentHash == added);
        Assert.Equal((BlobOrigins.Managed, BlobOrigins.Managed, mod.Owner, false), (add.Origins, add.SelectedOrigin, add.Owner, add.Overridden));
        Assert.True(add.Sequence > 0 && over.Sequence > add.Sequence);
        Assert.Equal(Bytes(24, 9), Assets.ReadBlob(overridden));    // the served bytes are the winner's
        Assert.Equal(Bytes(40, 1), Assets.ReadBlob(gameOnly));
        Assert.Null(Assets.ReadBlob(Hash("5-00000001-00000002-00000003")));
    }

    [Fact]
    public void Paging_is_bounded_and_checked()
    {
        AddGame(Bytes(8, 1));
        AddGame(Bytes(9, 2));
        AddGame(Bytes(10, 3));
        var mod = NewMod("pager");
        using var snap = As(mod, () => Assets.OpenBlobCatalog()).Snapshot!;
        Assert.Equal(3ul, snap.Total);
        Assert.Equal(2, snap.ReadPage(1).Count);
        Assert.Empty(snap.ReadPage(3));                                       // first == total: nothing, no error
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.ReadPage(4));   // past the end
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.ReadPage(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => snap.ReadPage(0, BlobCatalogSnapshot.MaxPage + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => As(mod, () => Assets.OpenBlobCatalog((BlobCatalogMode)7)));
    }

    [Fact]
    public void An_open_snapshot_never_changes_and_a_new_one_has_a_newer_generation()
    {
        AddGame(Bytes(30, 1));
        var mod = NewMod("watcher");
        using var first = As(mod, () => Assets.OpenBlobCatalog()).Snapshot!;
        var before = All(first);
        var added = As(mod, () => Assets.AddBlob(Bytes(12, 5)));
        Assert.Equal(before, All(first));
        Assert.Equal(1ul, first.Total);
        using var second = As(mod, () => Assets.OpenBlobCatalog()).Snapshot!;
        Assert.True(second.Info.Generation > first.Info.Generation);
        Assert.Contains(All(second), e => e.ContentHash == added);
    }

    [Fact]
    public void Registrations_only_is_visibly_incomplete_and_not_ready_throws_naming_the_row()
    {
        AddGame(Bytes(30, 1));
        _state.IndexReady = false;
        var mod = NewMod("early");
        var added = As(mod, () => Assets.AddBlob(Bytes(12, 5)));
        var e = Assert.Throws<KilnUnavailableException>(() => As(mod, () => Assets.OpenBlobCatalog()));
        Assert.Equal(Cap, e.Capability);
        Assert.Contains("not opened its content index yet", e.Message, StringComparison.Ordinal);
        var r = As(mod, () => Assets.OpenBlobCatalog(BlobCatalogMode.RegistrationsOnly));
        using var snap = r.Snapshot!;
        Assert.Equal((BlobCatalogStatus.Incomplete, false, true), (snap.Info.Status, snap.Info.GameComplete, snap.Info.RegistrationsComplete));
        Assert.Equal([new BlobCatalogEntry(added, BlobOrigins.Managed, BlobOrigins.Managed, mod.Owner, All(snap)[0].Sequence, false)], All(snap));
    }

    [Fact]
    public void A_snapshot_closes_with_dispose_or_its_mods_unload()
    {
        AddGame(Bytes(30, 1));
        var mod = NewMod("leaver");
        var a = As(mod, () => Assets.OpenBlobCatalog()).Snapshot!;
        var b = As(mod, () => Assets.OpenBlobCatalog()).Snapshot!;
        Assert.Equal(2, _state.Handles.Count);
        a.Dispose();
        a.Dispose();                                                          // a second dispose does nothing
        Assert.Throws<ObjectDisposedException>(() => a.ReadPage(0));
        mod.Retire();                                                         // kiln.assets reverts the owner: its handles close
        Assert.Empty(_state.Handles);
        Assert.Throws<ObjectDisposedException>(() => b.ReadPage(0));
        b.Dispose();
    }

    [Fact]
    public void A_budget_refusal_is_a_result_not_an_exception()
    {
        var mod = NewMod("greedy");
        var open = new List<BlobCatalogSnapshot>();
        BlobCatalogOpenResult r;
        while ((r = As(mod, () => Assets.OpenBlobCatalog())).Ok) open.Add(r.Snapshot!);
        Assert.Equal(64, open.Count);
        Assert.Null(r.Snapshot);
        Assert.Equal((BlobCatalogStatus.LimitExceeded, BlobCatalogReason.MemoryBudget), (r.Info.Status, r.Info.Reason));
        Assert.Contains("64 open", r.Reason, StringComparison.Ordinal);
        Assert.False(((IKilnResult)r).Ok);
        foreach (var s in open) s.Dispose();
    }

    [Fact]
    public void An_older_kiln_assets_has_no_catalog_and_blobs_still_register()
    {
        _service.Dispose();
        var size = Marshal.SizeOf<AssetsNative.Table>();  // the v1 table without the 0.2.0-dev.28 slots
        var older = (AssetsNative.Table*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            Buffer.MemoryCopy((void*)AssetsTable.Create(), older, size, size);
            older->Header.Size = (uint)size;
            _service = new(Backend.Tables, "kiln.assets", (nint)older);
            ResetAssets();
            var mod = NewMod("old-table");
            var e = Assert.Throws<KilnUnavailableException>(() => As(mod, () => Assets.OpenBlobCatalog()));
            Assert.Equal(Cap, e.Capability);
            Assert.Contains("older than 0.2.0-dev.28", e.Message, StringComparison.Ordinal);
            Assert.Equal(CapabilityReason.ServiceOlder, Capabilities.Get(Cap).Reason);
            var h = As(mod, () => Assets.AddBlob(Bytes(12, 5)));               // the legacy slot: a native registration
            Assert.Equal(AssetsAbi.OriginNativeApi, _state.Ledger.Single(x => x.Hash.Size == h.Size).Origin);
            mod.Retire();
        }
        finally
        {
            _service.Dispose();
            ResetAssets();
            NativeMemory.Free(older);
            _service = new(Backend.Tables, "kiln.assets", AssetsTable.Create());
        }
    }

    [Fact]
    public void The_catalog_row_is_off_when_the_content_hooks_are_off()
    {
        AssetsFake.Unit = null;  // the table answers as kiln.assets with its content hooks off
        ResetAssets();
        Assets.Rows.Refresh();
        Assert.False(Capabilities.Is(Cap));
        Assert.Equal(CapabilityReason.Faulted, Capabilities.Get(Cap).Reason);
        var e = Assert.Throws<KilnUnavailableException>(() => As(NewMod("off"), () => Assets.OpenBlobCatalog()));
        Assert.Equal(Cap, e.Capability);
        AssetsFake.Unit = _state;
    }

    [Fact]
    public void Managed_registrations_are_tagged_managed()
    {
        var mod = NewMod("tagger");
        var h = As(mod, () => Assets.AddBlob(Bytes(12, 5)));
        As(mod, () => Assets.OverrideBlob(Hash(AddGame(Bytes(20, 1))), Bytes(20, 2)));
        Assert.All(_state.Ledger, x => Assert.Equal(AssetsAbi.OriginManaged, x.Origin));
        Assert.Equal(2, _state.Ledger.Count);
        Assert.Equal(h.Size, _state.Ledger[0].Hash.Size);
    }

    [Fact]
    public void The_records_JSON_is_the_schemas_shape_and_reads_back()
    {
        var info = new BlobCatalogInfo(18446744073709551615, 40123, BlobCatalogStatus.Incomplete, false, true, BlobCatalogReason.None);
        var entry = new BlobCatalogEntry(Hash("4096-0000abcd-12345678-ffffffff"), BlobOrigins.Game | BlobOrigins.Manifest, BlobOrigins.Manifest,
            9007199254740993, 42, true);
        var infoJson = JsonSerializer.Serialize(info);
        var entryJson = JsonSerializer.Serialize(entry);
        Assert.Equal("""{"generation":"18446744073709551615","total":"40123","status":"incomplete","gameComplete":false,"registrationsComplete":true,"reason":"none"}""", infoJson);
        Assert.Equal("""{"contentHash":"4096-0000abcd-12345678-ffffffff","origins":["game","manifest"],"selectedOrigin":"manifest","owner":"9007199254740993","sequence":"42","overridden":true}""", entryJson);
        Assert.Equal(info, JsonSerializer.Deserialize<BlobCatalogInfo>(infoJson));
        Assert.Equal(entry, JsonSerializer.Deserialize<BlobCatalogEntry>(entryJson));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BlobCatalogEntry>(entryJson.Replace("\"42\"", "42")));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<BlobCatalogInfo>(infoJson.Replace("incomplete", "partial")));
        // the schema names the same fields
        var schema = File.ReadAllText(Path.Combine(Repo(), "schema", "blob-catalog.schema.json"));
        foreach (var name in new[] { "generation", "total", "status", "gameComplete", "registrationsComplete", "reason", "contentHash", "origins",
                     "selectedOrigin", "owner", "sequence", "overridden", "nativeApi", "limitExceeded", "unsupportedTable" })
            Assert.Contains($"\"{name}\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registration_posts_one_change_event_at_the_next_check()
    {
        var mod = NewMod("eventful");
        var seen = new List<AssetsCatalogChangedEvent>();
        using var sub = Events.On<AssetsCatalogChangedEvent>(e => seen.Add(e));
        Assets.PollCatalog();                                                 // the first check notes the generation only
        Assert.Empty(seen);
        As(mod, () => Assets.AddBlob(Bytes(12, 5)));
        As(mod, () => Assets.AddBlob(Bytes(13, 6)));
        Assets.PollCatalog();
        var one = Assert.Single(seen);
        Assert.Equal((_state.Generation, AssetsCatalogChange.Registration), (one.Generation, one.Reason));
        Assets.PollCatalog();                                                 // nothing moved: no event
        Assert.Single(seen);
        mod.Retire();
        Assets.PollCatalog();
        Assert.Equal(AssetsCatalogChange.Revoke, seen[^1].Reason);
    }

    private string AddGame(byte[] bytes)
    {
        var h = AssetsTable.HashOf(bytes);
        lock (_state.Gate) _state.Game.Add((h, bytes));
        return $"{h.Size}-{h.H0:x8}-{h.H1:x8}-{h.H2:x8}";
    }

    // the repository: three folders above this file (managed/tests/Kiln.Api.Tests)
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", "..", ".."));
}
