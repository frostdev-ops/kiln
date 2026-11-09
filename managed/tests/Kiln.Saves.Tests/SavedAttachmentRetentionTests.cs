// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln;

[Collection("entity identity")]
public sealed class SavedAttachmentRetentionTests : IDisposable
{
    private static readonly Guid Chest = Guid.Parse("00000002-0000-0000-0000-000000000002");
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "saved-attachment-tests", Guid.NewGuid().ToString("N"));
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    public SavedAttachmentRetentionTests()
    {
        TestWorld.Reset();
        RestoreMetadata();
        Directory.CreateDirectory(Path.Combine(_root, "manifests"));
        Directory.CreateDirectory(Path.Combine(_root, "blobs"));
    }

    public void Dispose()
    {
        TestWorld.Reset();
        Directory.Delete(_root, recursive: true);
    }

    private static EntityId Place(uint id)
    {
        var entity = new EntityId(id);
        TestWorld.World[id] = new SpawnLink.Ent(entity, Chest, null, new WorldPosition(42, 0, 0), 0);
        return entity;
    }

    private WorldStore Store()
    {
        var store = new WorldStore();
        // Supply an isolated sidecar directory, never a native save path. All stage/commit/load logic is real.
        typeof(WorldStore).GetProperty("Root")!.SetValue(store, _root);
        typeof(WorldStore).GetProperty("WorldId")!.SetValue(store, "retention-unit");
        typeof(WorldStore).GetField("_opened", PrivateInstance)!.SetValue(store, true);
        return store;
    }

    private WorldStore Restart(uint revision)
    {
        var store = Store();
        typeof(WorldStore).GetMethod("Load", PrivateInstance)!.Invoke(store, [revision, 1000u + revision]);
        Assert.Equal(DataMatch.Exact, store.Match);
        Assert.Equal(revision, store.Generation);
        store.Entities.StartMatching();
        return store;
    }

    private void Save(WorldStore store, uint revision)
    {
        store.Stage(revision, SaveReason.Request, late: false);
        store.Commit(revision, 1000u + revision, SaveReason.Request);
    }

    private JsonNode Manifest(uint revision) => JsonNode.Parse(File.ReadAllText(
        Path.Combine(_root, "manifests", $"{revision:D10}-{1000 + revision}.json")))!;

    private JsonNode SavedEntity(uint revision)
    {
        var entry = Manifest(revision)["bundles"]!["entity/test"];
        Assert.True(entry is not null, "The newest committed manifest must retain entity/test while eligibility is unknown");
        var store = Store();
        var sha = entry!["sha256"]!.GetValue<string>();
        return Assert.Single(JsonNode.Parse(File.ReadAllBytes(store.BlobPath(sha)))!["entities"]!.AsArray())!;
    }

    private void Seed()
    {
        var store = Store();
        store.Entities.Attach(Place(2001)).ByMod["test"] = new()
        {
            ["engraving"] = new WorldStore.Slot { Json = "\"saved-chest\"" },
        };
        Save(store, 1);
        Assert.Equal("saved-chest", SavedEntity(1)["keys"]!["engraving"]!.GetValue<string>());
        TestWorld.World.Clear();
    }

    private static void Unknown(string mode)
    {
        TestWorld.ClearMetadataCache();
        switch (mode)
        {
            case "missing": TestWorld.Templates.Remove(Chest); break;
            case "unready": TestWorld.RegistryReady = false; break;
            case "unavailable": TestWorld.RegistryResult = 3; break;
            case "absent": TestWorld.Available = false; break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static void RestoreMetadata()
    {
        TestWorld.ClearMetadataCache();
        TestWorld.Available = true;
        TestWorld.RegistryReady = true;
        TestWorld.RegistryResult = 0;
        TestWorld.Templates[Chest] = new(3, 9, "PropChest"); // KNOWN | TRANSFORM
    }

    private static void EndMatchWindow(EntityTable entities)
    {
        typeof(EntityTable).GetField("_matchUntil", PrivateInstance)!.SetValue(entities, DateTime.UtcNow.AddSeconds(-1));
        entities.Tick();
        entities.Tick(); // at most one missed-session increment, even with repeated pumps
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unready")]
    [InlineData("unavailable")]
    [InlineData("absent")]
    public void UnknownSavedAttachmentSurvivesSaveRestartAndRecoversWhenMetadataReturns(string mode)
    {
        Seed();
        Unknown(mode);
        var current = Place(3001);
        var missing = Restart(1);
        Assert.False(SpawnLink.Template(Chest).canPersist);
        missing.Entities.Tick();
        Assert.Null(missing.Entities.FindOrMatch(current));
        Assert.Equal(0, missing.Entities.LiveCount);
        Assert.Equal(1, missing.Entities.UnresolvedCount);
        Assert.Equal(0, TestWorld.FindCalls); // no positional candidate search without authoritative flags
        Save(missing, 2);
        var reloaded = Restart(2);
        Assert.Equal(1, reloaded.Entities.UnresolvedCount);
        Assert.Equal("saved-chest", SavedEntity(2)["keys"]!["engraving"]!.GetValue<string>());
        Assert.Null(reloaded.Entities.FindOrMatch(current));
        RestoreMetadata();
        reloaded.Entities.Tick();
        Assert.Equal("\"saved-chest\"", reloaded.Entities.FindOrMatch(current)!.ByMod["test"]["engraving"].Json);
        Assert.Equal(1, reloaded.Entities.Matched);
        Save(reloaded, 3);
        Assert.Null(SavedEntity(3)["missed"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unready")]
    [InlineData("absent")]
    public void UnknownSavedAttachmentsStillExpireAfterThreeMissedSessions(string mode)
    {
        Seed();
        Unknown(mode);
        for (uint session = 1; session <= 3; session++)
        {
            var store = Restart(session);
            EndMatchWindow(store.Entities);
            Assert.Equal(session < 3 ? 1 : 0, store.Entities.UnresolvedCount);
            var unresolved = (List<EntityTable.Rec>)typeof(EntityTable).GetField("_unresolved", PrivateInstance)!.GetValue(store.Entities)!;
            Assert.All(unresolved, record => Assert.Equal((int)session, record.Missed));
            Save(store, session + 1);
            if (session < 3) Assert.Equal((int)session, SavedEntity(session + 1)["missed"]!.GetValue<int>());
            else Assert.Null(Manifest(session + 1)["bundles"]!["entity/test"]);
        }
        RestoreMetadata();
        var current = Place(4001);
        var expired = Restart(4);
        expired.Entities.Tick();
        Assert.Null(expired.Entities.FindOrMatch(current));
    }

    [Theory]
    [InlineData(1u)] // known item without CurrentTransform
    [InlineData(3u)] // KNOWN | DO_NOT_SAVE
    [InlineData(11u)] // KNOWN | DO_NOT_SAVE | TRANSFORM
    public void KnownNonpersistentTemplatesCannotReattachOrBeStaged(uint flags)
    {
        Seed();
        TestWorld.ClearMetadataCache();
        TestWorld.Templates[Chest] = new(3, flags, "NoIdentity");
        var current = Place(3001);
        var loaded = Restart(1);
        loaded.Entities.Tick();
        Assert.False(SpawnLink.Template(Chest).canPersist);
        Assert.Null(loaded.Entities.FindOrMatch(current));
        var fresh = loaded.Entities.Attach(current);
        Assert.True(fresh.Transient);
        Assert.Empty(fresh.ByMod);
        fresh.ByMod["test"] = new() { ["engraving"] = new WorldStore.Slot { Json = "\"session-only\"" } };
        Save(loaded, 2);
        Assert.Null(Manifest(2)["bundles"]!["entity/test"]);
    }

    [Fact]
    public void UnknownLiveAttachmentDoesNotClaimTheSavedRecordOrGainAPositionalIdentity()
    {
        Seed();
        Unknown("unready");
        var loaded = Restart(1);
        var fresh = loaded.Entities.Attach(Place(3001));
        Assert.True(fresh.Transient);
        Assert.Empty(fresh.ByMod);
        fresh.ByMod["test"] = new() { ["engraving"] = new WorldStore.Slot { Json = "\"session-only\"" } };
        Assert.Equal(1, loaded.Entities.UnresolvedCount);
        Save(loaded, 2);
        Assert.Equal("saved-chest", SavedEntity(2)["keys"]!["engraving"]!.GetValue<string>());
    }
}
