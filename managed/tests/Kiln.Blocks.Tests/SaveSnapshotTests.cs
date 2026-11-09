// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

public sealed class SaveSnapshotTests
{
    public sealed class Counter { public int Value { get; set; } }

    [Fact]
    public void The_first_snapshot_post_restages_next_sim_commits_without_repeating_save_handlers_or_later_writes()
    {
        var store = SavesRuntime.Store;
        var root = Path.Combine(AppContext.BaseDirectory, "snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "manifests"));
        Directory.CreateDirectory(Path.Combine(root, "blobs"));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var rootProperty = typeof(WorldStore).GetProperty("Root")!;
        var worldProperty = typeof(WorldStore).GetProperty("WorldId")!;
        var opened = typeof(WorldStore).GetField("_opened", flags)!;
        var closed = typeof(WorldStore).GetField("_closed", flags)!;
        var prior = (rootProperty.GetValue(store), worldProperty.GetValue(store), opened.GetValue(store), closed.GetValue(store));
        var bundles = (Dictionary<string, WorldStore.Bundle>)typeof(WorldStore).GetField("_bundles", flags)!.GetValue(store)!;
        var oldBundles = bundles.ToArray();
        var generation = store.Generation;
        var runtimeFields = new[] { "_savingSeq", "_snapshotSeq", "_savingRevision" }.Select(name => typeof(SavesRuntime).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!).ToArray();
        var oldRuntime = runtimeFields.Select(field => field.GetValue(null)).ToArray();
        var dispatch = typeof(SavesRuntime).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        using var saving = Events.On<WorldSavingEvent>(_ => count++);
        try
        {
            bundles.Clear();
            rootProperty.SetValue(store, root);
            worldProperty.SetValue(store, "snapshot-unit");
            opened.SetValue(store, true);
            closed.SetValue(store, false);
            var book = new FilledContainers();
            var progress = new FilledContainers.FillProgress { Stacks = [new("twigs", 5)] };
            book.Pending["cache"] = progress;
            var completion = new TaskCompletionSource<ItemsResult>();
            Task<ItemsResult>? transaction = completion.Task;
            book.BeforeSnapshot = () => ContainerRuntime.Advance(book, "cache", progress, ref transaction, _ => throw new Exception("snapshot submitted loot"), false, out _);
            store.GetBundleRaw("world/kiln.blocks", "kiln.blocks").Keys["containers-filled"] = new() { Value = book };
            var counter = new Counter();
            store.GetBundleRaw("world/other-mod", "other-mod").Keys["counter"] = new() { Value = counter };
            void Dispatch(uint kind, ulong seq, uint revision, ulong saveSeq = 0) =>
                dispatch.Invoke(null, [new SavesNative.Event { Kind = kind, Seq = seq, Revision = revision, SaveSeq = saveSeq, Reason = (uint)SaveReason.Request }]);
            JsonNode Read(string scope, string key, uint revision = 1)
            {
                var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifests", $"{revision:D10}.pending.json")))!;
                var sha = manifest["bundles"]![scope]!["sha256"]!.GetValue<string>();
                return JsonNode.Parse(File.ReadAllText(store.BlobPath(sha)))!["keys"]![key]!;
            }
            Dispatch(1, 101, 1); // initial request-time Stage, before the next simulation
            Assert.Equal(0, (int)Read("world/kiln.blocks", "containers-filled")["Pending"]!["cache"]!["Next"]!);
            completion.SetResult(new ItemsResult(ItemsStatus.Done, new EntityId(42), new ItemId(24), 5, 5, [], "", 0, 1, false));
            counter.Value = 1; // all WorldData, not just the container journal, follows the real snapshot boundary
            Dispatch(5, 102, 1, 101); // native first-following-Post SNAPSHOT
            Assert.NotNull(Read("world/kiln.blocks", "containers-filled")["Filled"]?["cache"]);
            Assert.Equal(1, (int)Read("world/other-mod", "counter")["Value"]!);
            Assert.Equal(1, count);
            counter.Value = 2; // after the world snapshot, while the background write is still running
            Dispatch(5, 103, 1, 101);
            Assert.Equal(1, (int)Read("world/other-mod", "counter")["Value"]!);
            Dispatch(1, 104, 2);
            counter.Value = 3;
            Dispatch(5, 105, 1, 101); // an old target cannot restage the new save or overwrite the old generation
            Assert.Equal(2, (int)Read("world/other-mod", "counter", 2)["Value"]!);
            Dispatch(5, 106, 2, 104);
            Assert.Equal(3, (int)Read("world/other-mod", "counter", 2)["Value"]!);
            Assert.Equal(2, count);
        }
        finally
        {
            bundles.Clear(); foreach (var pair in oldBundles) bundles.Add(pair.Key, pair.Value);
            rootProperty.SetValue(store, prior.Item1); worldProperty.SetValue(store, prior.Item2);
            opened.SetValue(store, prior.Item3); closed.SetValue(store, prior.Item4);
            typeof(WorldStore).GetProperty("Generation")!.SetValue(store, generation);
            for (var i = 0; i < runtimeFields.Length; i++) runtimeFields[i].SetValue(null, oldRuntime[i]);
            Directory.Delete(root, recursive: true);
        }
    }
}
