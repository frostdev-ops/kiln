// SPDX-License-Identifier: MIT
#pragma warning disable KILNX031
using System.Text.Json.Nodes;
using Kiln.Host;
using Xunit;

namespace Kiln.Host.Tests;

public sealed class RegistryDispatchTests : ModsFixture
{
    private ModContext Owner(FakeRegistry native, string id)
        => HostAccess.CreateContext(new ModInfo(id, id, "1.0.0", Root), native.Host.OwnerCreate(id), DataDir);
    private static void Edit(RegistryContext context, int value, string registry = "items")
    {
        context.Capture = () => new JsonObject { ["ir"] = 1, ["mod"] = context.ModId,
            ["stage"] = context.Phase.ToString().ToLowerInvariant(), ["edits"] = new JsonArray(new JsonObject {
                ["registry"] = registry, ["select"] = registry == "items" ? "game:wood" : "game:balance",
                ["field"] = registry == "items" ? "maxStack" : "levelCap", ["op"] = value }) }.ToJsonString();
    }
    [RegistryEntryPoints(RegistryEntryPoint.ModLoad | RegistryEntryPoint.HotReload)]
    private sealed class Author(Action<RegistryContext> callback) : Mod
    {
        public override void OnRegistry(RegistryContext context) => callback(context);
    }
    [Fact]
    public void ForeignReadyWaitsForTheNextHostPreBeforeRunningRuntimeAuthors()
    {
        using var native = new FakeRegistry { Phase = 0 };
        using var manager = NewManager(native.Host);
        manager.PrepareRegistry(100);
        var dispatch = Assert.IsType<RegistryDispatch>(RegistryRuntime.Host);
        var tick = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        var owner = Owner(native, "pending");
        dispatch.Loaded(new Author(_ => threads.Add(Environment.CurrentManagedThreadId)), owner, Root, false);
        dispatch.FlushLoads(); Assert.Empty(threads);
        var loader = new Thread(() => { native.Phase = 4; native.FireReady(); });
        loader.Start(); loader.Join();
        Assert.Empty(threads);
        manager.Pre(Tick(1), []);
        Assert.Equal(new[] { tick, tick, tick }, threads);
        owner.Retire();
    }
    [Fact]
    public void RuntimeDrainsAndManualChangesRefuseAForeignThread()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "threaded");
        var tick = Environment.CurrentManagedThreadId;
        native.Host.TickThread = () => Environment.CurrentManagedThreadId == tick;
        Exception? drain = null, manual = null;
        var worker = new Thread(() => {
            drain = Record.Exception(dispatch.FlushLoads);
            manual = Record.Exception(() => RegistryRuntime.Manual!(owner, c => Edit(c, 90)));
        });
        worker.Start(); worker.Join();
        Assert.IsType<InvalidOperationException>(drain);
        Assert.IsType<InvalidOperationException>(manual);
        Assert.Equal(0, native.Submits);
        owner.Retire();
    }
    // review4 NS5 (ruling Nov 23): every callback of a runtime pass reads the same pre-pass values, whatever the order; the
    // pass commits at its end, all Alters then all Finals, in load order.
    [Fact(Timeout = 30_000)]
    public async Task RuntimePassesReadPrePassValuesAndCommitAtTheEndInLoadOrder()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var seen = new List<string>();
        void Invoke(RegistryContext context, int value)
        {
            seen.Add($"{context.ModId}:{context.Phase}:{context.ReadValue("items", "game:wood", "maxStack")}");
            if (context.Phase == RegistryPhase.Alter) Edit(context, value);
            if (context.Phase == RegistryPhase.Final) Edit(context, value + 100);
        }
        var a = Owner(native, "a"); var b = Owner(native, "b");
        dispatch.Loaded(new Author(c => Invoke(c, 20)), a, Root, false);
        dispatch.Loaded(new Author(c => Invoke(c, 30)), b, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(new[] { "a:Add:10", "b:Add:10", "a:Alter:10", "b:Alter:10", "a:Final:10", "b:Final:10" }, seen);
        Assert.Equal(130, native.Value);
        Assert.True(native.PassReads >= 6, "automatic passes read through the pass view");
        a.Retire(); b.Retire();
        Assert.Equal(10, native.Value);
    }
    [Fact]
    public void ReplacingAModThatDropsAnEditRestoresBaseBeforeHotReload()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var old = Owner(native, "test");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 50); }), old, Root, false);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value);
        old.Retire(); Assert.Equal(10, native.Value);
        var replacement = Owner(native, "test"); var points = new List<RegistryEntryPoint>();
        dispatch.Loaded(new Author(c => { points.Add(c.EntryPoint); Assert.Equal("10", c.ReadValue("items", "game:wood", "maxStack")); }), replacement, Root, true);
        dispatch.FlushLoads();
        Assert.Equal(new[] { RegistryEntryPoint.HotReload, RegistryEntryPoint.HotReload, RegistryEntryPoint.HotReload }, points);
        Assert.Equal(10, native.Value); replacement.Retire();
    }
    [Fact]
    public void ManualBuffersAtomicallyExpiresItsContextAndRevertsOnlyItsLayer()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "manual"); RegistryContext? retained = null;
        var result = RegistryRuntime.Manual!(owner, c => { retained = c; Edit(c, 40); });
        Assert.Equal(0u, result.Outcome); Assert.Equal(40, native.Value);
        Assert.Equal(RegistryContextError.Closed, Assert.Throws<RegistryContextException>(() => retained!.Ensure()).Code);
        var submits = native.Submits;
        Assert.Throws<InvalidOperationException>(() => RegistryRuntime.Manual!(owner, c => { Edit(c, 90); throw new InvalidOperationException("fail"); }));
        Assert.Equal(submits, native.Submits); Assert.Equal(40, native.Value);
        Assert.Equal(5u, RegistryRuntime.Revert!(owner, result.Handle).Outcome); Assert.Equal(10, native.Value);
        var refused = RegistryRuntime.Manual!(owner, c => Edit(c, 50, "balance"));
        Assert.Equal(3u, refused.Outcome); Assert.Equal(1u, refused.Refused); Assert.Equal(10, native.Value);
        native.World = 0;
        Assert.Equal(4u, RegistryRuntime.Manual!(owner, _ => throw new Exception("must not run")).Outcome);
    }
    [RegistryEntryPoints(RegistryEntryPoint.ModLoad | RegistryEntryPoint.WorldLoad)]
    private sealed class WorldAuthor(Action<RegistryContext> callback) : Mod
    {
        public override void OnRegistry(RegistryContext context) => callback(context);
    }
    private static int Read(RegistryContext context) =>
        int.Parse(context.ReadValue("items", "game:wood", "maxStack"), System.Globalization.CultureInfo.InvariantCulture);
    [RegistryEntryPoints(RegistryEntryPoint.WorldLoad)]
    private sealed class WorldOnlyAuthor(Action<RegistryContext> callback) : Mod
    {
        public override void OnRegistry(RegistryContext context) => callback(context);
    }
    // Registry.Reauthor (Loot Rarity Leveler's live settings): the WorldLoad point again in the same world, derived from what
    // lies below the mod's own layers, replacing them; never stacking, never a moment without the mod's value.
    [Fact(Timeout = 30_000)]
    public async Task ReauthorRunsTheWorldPointAgainFromBelowAndReplacesItsLayers()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "leveler"); var factor = 2; var passes = 0;
        dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { ++passes; Edit(c, Read(c) * factor); } }), owner, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(20, native.Value); Assert.Equal(1, native.LayerCount); Assert.Equal(1, passes);
        factor = 3;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        Assert.Equal(20, native.Value); // queued: nothing runs before the next host tick
        dispatch.FlushLoads();
        Assert.Equal(30, native.Value);        // 10 x 3: read below its own layer, not 20 x 3
        Assert.Equal(1, native.LayerCount);    // the earlier layer was taken back
        Assert.Equal(2, passes);               // two requests before the tick are one pass
        dispatch.FlushLoads(); Assert.Equal(2, passes); // a request runs once
        owner.Retire(); Assert.Equal(10, native.Value); Assert.Equal(0, native.LayerCount);
    }
    [Fact(Timeout = 30_000)]
    public async Task AReauthoredPassThatThrowsKeepsTheEarlierLayers()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "leveler"); var fail = false;
        dispatch.Loaded(new WorldOnlyAuthor(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            if (fail) throw new InvalidOperationException("bad settings");
            Edit(c, 40);
        }), owner, Root, false);
        dispatch.FlushLoads(); Assert.Equal(40, native.Value);
        fail = true;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
        dispatch.FlushLoads();
        Assert.Equal(40, native.Value); Assert.Equal(1, native.LayerCount);
        fail = false;
        Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad)); // the author is asked again, not closed for good
        dispatch.FlushLoads(); Assert.Equal(40, native.Value); Assert.Equal(1, native.LayerCount);
        owner.Retire();
    }
    [Fact]
    public void ReauthorRefusesOtherPointsAndModsWithoutAWorldPoint()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var modload = Owner(native, "modload-only"); var world = Owner(native, "world");
        dispatch.Loaded(new Author(_ => { }), modload, Root, false);
        dispatch.Loaded(new WorldOnlyAuthor(_ => { }), world, Root, false);
        Assert.Contains("declares no WorldLoad point", RegistryRuntime.Reauthor!(modload, RegistryEntryPoint.WorldLoad));
        Assert.Contains("only the WorldLoad point", RegistryRuntime.Reauthor!(world, RegistryEntryPoint.ModLoad));
        var plain = Owner(native, "no-author");
        Assert.Contains("declares no WorldLoad point", RegistryRuntime.Reauthor!(plain, RegistryEntryPoint.WorldLoad));
        modload.Retire(); world.Retire();
    }
    [Fact]
    public void AReauthorWithoutAWorldWaitsForTheNextWorldsLoad()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry { World = 0 }; using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            var owner = Owner(native, "leveler"); var passes = 0;
            dispatch.Loaded(new WorldOnlyAuthor(c => { if (c.Phase == RegistryPhase.Alter) { ++passes; Edit(c, 50); } }), owner, Root, false);
            dispatch.FlushLoads(); Assert.Equal(0, passes);
            Assert.Null(RegistryRuntime.Reauthor!(owner, RegistryEntryPoint.WorldLoad));
            dispatch.FlushLoads(); Assert.Equal(0, passes); Assert.Equal(10, native.Value);
            native.World = 1; WorldConfigs.Announce(Path.Combine(DataDir, "world-a"));
            dispatch.FlushLoads();
            Assert.Equal(1, passes); Assert.Equal(50, native.Value); // the world's own pass, not a second one for the request
            dispatch.FlushLoads(); Assert.Equal(1, passes);
            owner.Retire();
        }
        finally { WorldConfigs.Reset(); }
    }
    // review4 NS5: a read-derived assignment restates the mod's intent at every point; it never stacks on its own layers.
    [Fact(Timeout = 30_000)]
    public async Task ARuntimeReadExcludesTheReadersOwnLayers()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "doubler");
        dispatch.Loaded(new WorldAuthor(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, Read(c) * 2); }), owner, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(20, native.Value); Assert.Equal(2, native.LayerCount); // ModLoad 20, then WorldLoad 20 (not 40)
        owner.Retire(); Assert.Equal(10, native.Value);
    }
    [Fact(Timeout = 30_000)]
    public async Task TwoModsDoublingInOnePassDoNotStack()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var a = Owner(native, "a"); var b = Owner(native, "b");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, Read(c) * 2); }), a, Root, false);
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, Read(c) * 2); }), b, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(20, native.Value); // like GameStart: both read 10, in any order
        a.Retire(); b.Retire();
    }
    [Fact(Timeout = 30_000)]
    public async Task AnEarlierModsFinalBeatsALaterModsAlterInOnePass()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var a = Owner(native, "a"); var b = Owner(native, "b");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Final) Edit(c, 70); }), a, Root, false);
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 80); }), b, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(70, native.Value);
        a.Retire(); b.Retire();
    }
    [Fact]
    public void AFinalFaultRevertsOnlyTheCurrentPointsEarlierCommits()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry { World = 0 }; using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            var owner = Owner(native, "faulted-point");
            dispatch.Loaded(new WorldAuthor(c => {
                if (c.Phase == RegistryPhase.Alter) Edit(c, c.EntryPoint == RegistryEntryPoint.ModLoad ? 20 : 50);
                if (c.EntryPoint == RegistryEntryPoint.WorldLoad && c.Phase == RegistryPhase.Final)
                    throw new InvalidOperationException("final pass failed");
            }), owner, Root, false);
            dispatch.FlushLoads(); Assert.Equal(20, native.Value);
            native.World = 1; WorldConfigs.Announce(Path.Combine(DataDir, "world-a"));
            dispatch.FlushLoads();
            Assert.Equal(20, native.Value);
            owner.Retire(); Assert.Equal(10, native.Value);
        }
        finally { WorldConfigs.Reset(); }
    }
    [Fact]
    public void AFailedPointIsRetriedForALaterWorld()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry { World = 0 }; using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            var owner = Owner(native, "retry-point"); var calls = 0; var fail = true;
            dispatch.Loaded(new WorldAuthor(c => {
                if (c.EntryPoint != RegistryEntryPoint.WorldLoad) return;
                ++calls;
                if (fail) { fail = false; throw new InvalidOperationException("first world failed"); }
                if (c.Phase == RegistryPhase.Alter) Edit(c, 60);
            }), owner, Root, false);
            dispatch.FlushLoads();
            native.World = 1; WorldConfigs.Announce(Path.Combine(DataDir, "world-a")); dispatch.FlushLoads();
            Assert.Equal(1, calls); Assert.Equal(10, native.Value);
            native.World = 2; WorldConfigs.Announce(Path.Combine(DataDir, "world-b")); dispatch.FlushLoads();
            Assert.Equal(4, calls); Assert.Equal(60, native.Value);
            owner.Retire();
        }
        finally { WorldConfigs.Reset(); }
    }
    private sealed class WorldInput { public int Count { get; set; } = 3; }
    [Fact]
    public void IdlessWorldsDispatchBeforeAdoptionAndRebindWithoutStackingTheirOwnLayers()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            var owner = Owner(native, "new-world");
            var worlds = new List<RegistryWorld>(); var unbound = 0; var bound = 0;
            void Invoke(RegistryContext context)
            {
                if (context.EntryPoint == RegistryEntryPoint.ModLoad)
                {
                    if (context.Phase == RegistryPhase.Alter) Edit(context, 20);
                    return;
                }
                if (context.Phase == RegistryPhase.Add) worlds.Add(context.World!.Value);
                if (context.Phase != RegistryPhase.Alter) return;
                if (context.World!.Value.Id is null)
                {
                    Assert.Equal(RegistryContextError.WorldSettingsUnavailable,
                        Assert.Throws<RegistryContextException>(() => Config.RegisterWorld<WorldInput>()).Code);
                    ++unbound; Edit(context, 50);
                }
                else
                {
                    Assert.Equal(3, Config.RegisterWorld<WorldInput>().Value.Count);
                    ++bound; Edit(context, 60);
                }
            }
            dispatch.Loaded(new WorldAuthor(Invoke), owner, Root, false);
            dispatch.FlushLoads();
            Assert.Equal(new[] { new RegistryWorld(1, WorldKind.Server, null) }, worlds);
            Assert.Equal(1, unbound); Assert.Equal(50, native.Value); Assert.Equal(2, native.LayerCount);
            WorldConfigs.Announce(Path.Combine(DataDir, "world-a")); dispatch.FlushLoads();
            Assert.Equal(new RegistryWorld(1, WorldKind.Server, "world-a"), worlds.Last());
            Assert.Equal(1, bound); Assert.Equal(60, native.Value); Assert.Equal(2, native.LayerCount);
            // Native readiness can precede the save signal: the new token must not inherit the previous world's inputs.
            native.ChangeWorld(2); dispatch.FlushLoads();
            Assert.Equal(new RegistryWorld(2, WorldKind.Server, null), worlds.Last());
            Assert.Equal(2, unbound); Assert.Equal(50, native.Value); Assert.Equal(2, native.LayerCount);
            WorldConfigs.Announce(Path.Combine(DataDir, "world-b")); dispatch.FlushLoads();
            Assert.Equal(new RegistryWorld(2, WorldKind.Server, "world-b"), worlds.Last());
            Assert.Equal(2, bound); Assert.Equal(60, native.Value); Assert.Equal(2, native.LayerCount);
            owner.Retire(); Assert.Equal(10, native.Value);
            var replacement = Owner(native, "new-world");
            dispatch.Loaded(new WorldAuthor(Invoke), replacement, Root, true); dispatch.FlushLoads();
            Assert.Equal(5, worlds.Count); Assert.Equal(3, bound); Assert.Equal(2, native.LayerCount);
            replacement.Retire();
        }
        finally { WorldConfigs.Reset(); }
    }
    [Theory]
    [InlineData(RegistryPhase.Alter)]
    [InlineData(RegistryPhase.Final)]
    public void AutomaticWorldRequiredOutcomesReleaseTheirUncommittedDraft(RegistryPhase phase)
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "world-race");
        dispatch.Loaded(new WorldAuthor(c => {
            if (c.EntryPoint == RegistryEntryPoint.WorldLoad && c.Phase == phase) Edit(c, 90);
        }), owner, Root, false);
        native.WorldRequiredCommits = 1; dispatch.FlushLoads();
        Assert.Equal(0, native.DraftCount); Assert.Equal(10, native.Value);
        owner.Retire();
    }
    [Fact]
    public void ManualWorldRequiredOutcomeReleasesItsDraftAndReturnsNoRetainedHandle()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "manual-race");
        native.WorldRequiredCommits = 1;
        var result = RegistryRuntime.Manual!(owner, c => Edit(c, 90));
        Assert.Equal(4u, result.Outcome); Assert.Equal(0, native.DraftCount);
        Assert.Equal(0ul, result.Handle); Assert.Equal(10, native.Value);
        owner.Retire();
    }
    [Fact]
    public void AContextConstructionFailureDoesNotStarveOtherPendingAuthors()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var first = Owner(native, "first"); var second = Owner(native, "second"); var seen = new List<string>();
        dispatch.Loaded(new Author(_ => seen.Add("first")), first, Root, false);
        dispatch.Loaded(new Author(_ => seen.Add("second")), second, Root, false);
        native.StatusFailures = 1;
        Assert.Null(Record.Exception(dispatch.FlushLoads));
        Assert.Equal(new[] { "second", "second", "second" }, seen);
        first.Retire(); second.Retire();
    }
    [Fact]
    public void AWorldNotificationFaultDoesNotEscapeIntoTheSaveCaller()
    {
        var host = new FakeHost(); var previous = RegistryRuntime.WorldChanged;
        WorldConfigs.Reset();
        try
        {
            RegistryRuntime.WorldChanged = _ => throw new InvalidOperationException("notification fixture fault");
            Assert.Null(Record.Exception(() => WorldConfigs.Announce(null)));
            Assert.True(host.Has("notification fixture fault"), host.Dump());
        }
        finally { RegistryRuntime.WorldChanged = previous; WorldConfigs.Reset(); }
    }
    [Fact]
    public void AThrowingAutomaticAuthorIsNotCalledInLaterPasses()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "fault"); var calls = 0;
        dispatch.Loaded(new Author(c => { ++calls; Edit(c, 99); throw new InvalidOperationException("test fault"); }), owner, Root, false);
        dispatch.FlushLoads();
        Assert.Equal(1, calls); Assert.Equal(0, native.Submits); Assert.Equal(10, native.Value); owner.Retire();
    }
    [Fact]
    public void LoadedContainsCodeIdentityIoFailuresAndStillCleansUpManualChanges()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "missing-code");
        Assert.Null(Record.Exception(() => dispatch.Loaded(new Author(_ => { }), owner, Path.Combine(Root, "removed"), false)));
        Assert.True(native.Host.Has("missing-code"), native.Host.Dump());
        RegistryRuntime.Manual!(owner, c => Edit(c, 90)); Assert.Equal(90, native.Value);
        owner.Retire(); Assert.Equal(10, native.Value);
    }
    [Fact]
    public void DuplicateLoadedNotificationDoesNotThrowOrDoubleDispatch()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "duplicate"); var calls = 0;
        var author = new Author(_ => ++calls);
        dispatch.Loaded(author, owner, Root, false);
        Assert.Null(Record.Exception(() => dispatch.Loaded(author, owner, Root, false)));
        dispatch.FlushLoads(); Assert.Equal(3, calls);
        owner.Retire();
    }
    private static int TrackedChanges(RegistryDispatch dispatch) =>
        ((Dictionary<ulong, HashSet<ulong>>)typeof(RegistryDispatch).GetField("_changes",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dispatch)!).Values.Sum(x => x.Count);
    [Fact]
    public void ManualRevertReleasesHostBookkeeping()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "reverted");
        dispatch.Loaded(new Author(_ => { }), owner, Root, false); dispatch.FlushLoads();
        var change = RegistryRuntime.Manual!(owner, c => Edit(c, 90)); Assert.Equal(1, TrackedChanges(dispatch));
        RegistryRuntime.Revert!(owner, change.Handle);
        Assert.Equal(0, TrackedChanges(dispatch)); Assert.Equal(10, native.Value); owner.Retire();
    }
    [Fact]
    public void ExpiredWorldHandlesArePrunedWhileProcessChangesRemainTracked()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "world-expiry");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 20); }), owner, Root, false);
        dispatch.FlushLoads(); Assert.Equal(1, TrackedChanges(dispatch));
        RegistryRuntime.Manual!(owner, c => Edit(c, 90)); Assert.Equal(2, TrackedChanges(dispatch));
        native.ChangeWorld(2); dispatch.FlushLoads();
        Assert.Equal(1, TrackedChanges(dispatch)); Assert.Equal(20, native.Value); Assert.Equal(1, native.LayerCount);
        owner.Retire(); Assert.Equal(0, TrackedChanges(dispatch)); Assert.Equal(10, native.Value);
    }
    [Fact]
    public void DisposingTheManagerReleasesTheReadySubscriptionAndRuntimeBridge()
    {
        using var native = new FakeRegistry(); var manager = NewManager(native.Host);
        manager.PrepareRegistry(100); Assert.Equal(1, native.Subscriptions);
        manager.Dispose(); Assert.Equal(0, native.Subscriptions); Assert.Null(RegistryRuntime.Manual);
    }
    // review4 NS3: a HotReload pass that replaces a ModLoad|HotReload author's ModLoad layer keeps its process lifetime.
    [Fact(Timeout = 30_000)]
    public async Task HotReloadReplacementKeepsModLoadLifetimeAcrossWorlds()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var old = Owner(native, "review4");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 50); }), old, Root, false);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value);
        native.ChangeWorld(2); dispatch.FlushLoads();
        Assert.Equal(50, native.Value); // before the reload: the ModLoad layer follows the process into world 2
        old.Retire();
        var replacement = Owner(native, "review4"); var points = new List<RegistryEntryPoint>();
        dispatch.Loaded(new Author(c => { points.Add(c.EntryPoint); if (c.Phase == RegistryPhase.Alter) Edit(c, 50); }), replacement, Root, true);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value);
        Assert.All(points, p => Assert.Equal(RegistryEntryPoint.HotReload, p));
        native.ChangeWorld(3); dispatch.FlushLoads();
        Assert.Equal(50, native.Value); // the replacement's edits persist like the ModLoad layer they replaced
        replacement.Retire(); Assert.Equal(10, native.Value);
    }
    // review4 NS3: a replacing HotReload pass that read per-world settings is world-scoped and runs again at the next world.
    [Fact(Timeout = 30_000)]
    public async Task HotReloadReplacementThatReadWorldSettingsRunsAgainAtTheNextWorld()
    {
        await Task.Yield();
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            WorldConfigs.Announce(Path.Combine(DataDir, "world-a"));
            var old = Owner(native, "reload-settings");
            dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 50); }), old, Root, false);
            dispatch.FlushLoads(); Assert.Equal(50, native.Value);
            old.Retire(); Assert.Equal(10, native.Value);
            var replacement = Owner(native, "reload-settings"); var worlds = new List<string?>();
            dispatch.Loaded(new Author(c =>
            {
                if (c.Phase != RegistryPhase.Alter) return;
                worlds.Add(c.World?.Id); Edit(c, 60 + Config.RegisterWorld<WorldInput>().Value.Count);
            }), replacement, Root, true);
            dispatch.FlushLoads(); Assert.Equal(63, native.Value);
            native.ChangeWorld(2); WorldConfigs.Announce(Path.Combine(DataDir, "world-b")); dispatch.FlushLoads();
            Assert.Equal(new string?[] { "world-a", "world-b" }, worlds); Assert.Equal(63, native.Value);
            replacement.Retire(); Assert.Equal(10, native.Value);
        }
        finally { WorldConfigs.Reset(); }
    }
    // review4 NS4: a transient UNAVAILABLE (live install window, budgets, lazy preparation) defers the point to a later tick.
    [Fact(Timeout = 30_000)]
    public async Task ATransientUnavailableModLoadIsRetriedOnALaterTick()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "transient"); var calls = 0;
        dispatch.Loaded(new Author(c => { if (c.Phase != RegistryPhase.Alter) return; ++calls; Edit(c, 50); }), owner, Root, false);
        native.BeginUnavailable = 1;
        dispatch.FlushLoads(); Assert.Equal(10, native.Value); Assert.Equal(1, calls);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value); Assert.Equal(2, calls);
        dispatch.FlushLoads(); Assert.Equal(2, calls); // applied once, never again
        native.ReadUnavailable = 1; // review4 recheck RM1: an UNAVAILABLE read inside the author's code is its fault, not retried
        var reader = Owner(native, "transient-read");
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, int.Parse(c.ReadValue("items", "game:wood", "maxStack"), System.Globalization.CultureInfo.InvariantCulture) + 1); }), reader, Root, false);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value);
        dispatch.FlushLoads(); Assert.Equal(50, native.Value);
        owner.Retire(); reader.Retire();
    }
    [Fact(Timeout = 30_000)]
    public async Task ATransientUnavailableWorldLoadIsRetriedOnALaterTick()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "transient-world"); var calls = 0;
        dispatch.Loaded(new WorldAuthor(c => {
            if (c.EntryPoint != RegistryEntryPoint.WorldLoad || c.Phase != RegistryPhase.Alter) return;
            ++calls; Edit(c, 60);
        }), owner, Root, false);
        native.BeginUnavailable = 1;
        dispatch.FlushLoads(); Assert.Equal(10, native.Value); Assert.Equal(1, calls);
        dispatch.FlushLoads(); Assert.Equal(60, native.Value); Assert.Equal(2, calls);
        dispatch.FlushLoads(); Assert.Equal(2, calls);
        owner.Retire(); Assert.Equal(10, native.Value);
    }
    // review4 nit: with no runtime author a host tick neither polls the registry's locks nor looks its tables up again.
    [Fact(Timeout = 30_000)]
    public async Task ATickWithoutRuntimeAuthorsDoesNotPollTheRegistry()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        dispatch.FlushLoads();
        var (status, lookups) = (native.StatusCalls, native.Host.ServiceLookups);
        for (var i = 0; i < 10; ++i) dispatch.FlushLoads();
        Assert.Equal(status, native.StatusCalls);
        Assert.Equal(lookups, native.Host.ServiceLookups);
        var owner = Owner(native, "late"); var calls = 0;
        dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) { ++calls; Edit(c, 30); } }), owner, Root, false);
        dispatch.FlushLoads(); Assert.Equal(1, calls); Assert.Equal(30, native.Value);
        var before = native.Host.ServiceLookups;
        dispatch.FlushLoads(); dispatch.FlushLoads();
        Assert.True(native.Host.ServiceLookups - before < 6, $"tables are cached ({native.Host.ServiceLookups - before} lookups in two ticks)");
        owner.Retire();
    }
    // review4 nit: START and WORLDLOAD arriving before one tick are both published, in order.
    [Fact(Timeout = 30_000)]
    public async Task ReadyEventsBetweenTicksAreAllPublishedInOrder()
    {
        await Task.Yield();
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var seen = new List<RegistryReadyPhase>(); var previous = RegistryRuntime.Ready;
        RegistryRuntime.Ready = (phase, _, _) => seen.Add(phase);
        try
        {
            native.Phase = 4; native.FireReady(); native.Phase = 5; native.FireReady();
            dispatch.FlushLoads();
            Assert.Equal(new[] { RegistryReadyPhase.Start, RegistryReadyPhase.WorldLoad }, seen);
        }
        finally { RegistryRuntime.Ready = previous; }
    }
    // review4 recheck RM1: an UNAVAILABLE read inside the author's own code (an unmapped field, an entry without the
    // config) faults that pass once; only Kiln's own transient states (begin/commit refused) are retried.
    [Fact(Timeout = 30_000)]
    public void Review4R_PermanentlyUnreadableFieldIsNotRetriedForever()
    {
        using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
        using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
        var owner = Owner(native, "unreadable"); var calls = 0;
        dispatch.Loaded(new Author(c => {
            if (c.Phase != RegistryPhase.Alter) return;
            ++calls; _ = c.ReadValue("items", "game:wood", "manaCost"); Edit(c, 70);
        }), owner, Root, false);
        native.ReadUnavailable = int.MaxValue;
        for (var i = 0; i < 4; ++i) { dispatch.FlushLoads(); Thread.Sleep(450); }
        Assert.Equal(1, calls);
        owner.Retire();
    }
    // review4 recheck RM2: a mod that only uses Registry.Change, with no runtime author, still sees the bound world.
    [Fact(Timeout = 30_000)]
    public void Review4R_ManualOnlyModSeesTheBoundWorldId()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            WorldConfigs.Announce(Path.Combine(DataDir, "world-a"));
            dispatch.FlushLoads(); dispatch.FlushLoads();
            var owner = Owner(native, "manual-only"); RegistryWorld? seen = null; RegistryContextError? error = null;
            var result = RegistryRuntime.Manual!(owner, c => {
                seen = c.World;
                try { _ = Config.RegisterWorld<WorldInput>().Value.Count; } catch (RegistryContextException e) { error = e.Code; }
                Edit(c, 40);
            });
            Assert.Null(error);
            Assert.Equal("world-a", seen?.Id);
            owner.Retire();
        }
        finally { WorldConfigs.Reset(); }
    }
    // review4 recheck RS1: a replaced-ModLoad HotReload whose Alter reads per-world settings and whose Final does not: the
    // pass decides its lifetime once, so a replay at each bound world replaces its layers instead of stacking Finals.
    [Fact(Timeout = 30_000)]
    public void Review4R_MixedLifetimeReplayDoesNotAccumulateLayers()
    {
        WorldConfigs.Reset();
        try
        {
            using var native = new FakeRegistry(); using var manager = NewManager(native.Host);
            using var dispatch = new RegistryDispatch(manager); dispatch.Prepare(100);
            WorldConfigs.Announce(Path.Combine(DataDir, "world-a"));
            var old = Owner(native, "mixed");
            dispatch.Loaded(new Author(c => { if (c.Phase == RegistryPhase.Alter) Edit(c, 20); }), old, Root, false);
            dispatch.FlushLoads();
            old.Retire();
            var replacement = Owner(native, "mixed"); var read = 0;
            dispatch.Loaded(new Author(c => {
                if (c.Phase == RegistryPhase.Alter) { read += Config.RegisterWorld<WorldInput>().Value.Count; Edit(c, 50); }
                if (c.Phase == RegistryPhase.Final) Edit(c, 60);
            }), replacement, Root, true);
            dispatch.FlushLoads();
            var first = native.LayerCount;
            for (ulong w = 2; w <= 4; ++w)
            {
                native.ChangeWorld(w); WorldConfigs.Announce(Path.Combine(DataDir, "world-" + w)); dispatch.FlushLoads();
            }
            Assert.True(read >= 3 * 2, $"per-world settings were read {read / 3} time(s)");
            Assert.Equal(first, native.LayerCount);
            replacement.Retire();
        }
        finally { WorldConfigs.Reset(); }
    }
    [Fact]
    public unsafe void MissingStageFunctionsMakeTheTransportUnavailable()
    {
        using var native = new FakeRegistry(); var transport = new RegistryNative(native.Host);
        Assert.True(transport.Available); native.Stage->Subscribe = null;
        Assert.False(transport.Available);
    }
    public override void Dispose()
    {
        RegistryRuntime.Available = null; RegistryRuntime.Manual = null; RegistryRuntime.Revert = null;
        RegistryRuntime.Read = null; RegistryRuntime.WorldChanged = null; RegistryRuntime.LastReady = null;
        base.Dispose();
    }
}
