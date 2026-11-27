// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Registry;
using DataRegistry = Kiln.Registry.Registry;
using Xunit;

namespace Kiln.RegistryTests;

public sealed class RegistryContextTests
{
    private static ModContext Mod() => HostAccess.CreateContext(new ModInfo("test", "Test", "1.0.0", "."), 77, ".");
    private static RegistryContext Frame(ModContext mod, RegistryEntryPoint point = RegistryEntryPoint.GameStart,
        RegistryPhase phase = RegistryPhase.Alter, RegistryWorld? world = null) =>
        new(mod, point, phase, world, "test-code", (_, _, _) => "10", (_, _) => new[] { "game:wood" });

    [Fact]
    public void TypedCellsBufferStableIrAndExpireWithTheirContext()
    {
        var context = Frame(Mod());
        using var scope = context.Enter();
        var registry = DataRegistry.For(context);
        var item = registry.Items["game:wood"];
        Assert.Equal(10, item.MaxStack);
        item.MaxStack = 500;
        Assert.Equal(500, item.MaxStack);
        var ir = JsonNode.Parse(context.CaptureIr()!)!;
        Assert.Equal("test", ir["mod"]!.GetValue<string>());
        Assert.Equal("alter", ir["stage"]!.GetValue<string>());
        Assert.Equal("items", ir["edits"]![0]!["registry"]!.GetValue<string>());
        Assert.Equal("maxStack", ir["edits"]![0]!["field"]!.GetValue<string>());
        Assert.Equal(500, ir["edits"]![0]!["op"]!.GetValue<int>());
        context.Close();
        Assert.Equal(RegistryContextError.Closed, Assert.Throws<RegistryContextException>(() => DataRegistry.For(context)).Code);
        Assert.Equal(RegistryContextError.Closed, Assert.Throws<RegistryContextException>(() => item.MaxStack).Code);
        Assert.Equal(RegistryContextError.Closed, Assert.Throws<RegistryContextException>(() => item.MaxStack = 20).Code);
    }

    // 0.2.0-dev.31.19: the singleton loot rules and the keyed random loot tables record edits in Kiln names and shapes
    [Fact]
    public void LootRulesAndRandomLootRecordKilnShapedEdits()
    {
        var context = new RegistryContext(Mod(), RegistryEntryPoint.WorldLoad, RegistryPhase.Alter, null, "test-code",
            (registry, id, field) => field.EndsWith(".mode", StringComparison.Ordinal) ? "\"none\"" : "5.29",
            (registry, _) => registry == "randomLoot" ? new[] { "game:labels_80943338", "game:world_509feadb_0" } : new[] { "game:loot" });
        using var scope = context.Enter();
        var r = DataRegistry.For(context);
        Assert.Equal(5.29, r.LootRules.RarityWeights.Legendary);
        r.LootRules.RarityWeights.Legendary = 84.64;
        Assert.Equal(new[] { "game:labels_80943338", "game:world_509feadb_0" }, r.RandomLoot.Select("*").Select(t => t.Id.Value));
        var t = r.RandomLoot["game:world_509feadb_0"];
        Assert.Equal(RandomLootMode.None, t.Level.Mode);
        t.Level.Mode = RandomLootMode.Set;
        t.Level.Value = 25;
        t.Rarity.Epic.Weight = 320;
        var edits = JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray();
        Assert.Equal(("lootRules", "game:loot", "rarityWeights.legendary"), Edit(edits[0]!));
        Assert.Equal(84.64, edits[0]!["op"]!.GetValue<double>());
        Assert.Equal(("randomLoot", "game:world_509feadb_0", "level.mode"), Edit(edits[1]!));
        Assert.Equal("set", edits[1]!["op"]!.GetValue<string>());
        Assert.Equal(("randomLoot", "game:world_509feadb_0", "rarity.epic.weight"), Edit(edits[3]!));
        context.Close();
        static (string, string, string) Edit(JsonNode e) => (e["registry"]!.GetValue<string>(), e["select"]!.GetValue<string>(), e["field"]!.GetValue<string>());
    }

    // owner 2026-11-26 ("make it a registry"): one call's cap is per registry (RegistrySchema.EditsPerCall). A call over it is
    // never thrown in the mod's code (a caught exception used to cut the call at 4096, an uncaught one dropped the whole pass):
    // the stage keeps one edit past the cap so Kiln refuses the call whole at submit, and the mod's log names the count.
    private sealed class LogBackend : IKilnBackend
    {
        public readonly List<string> Lines = [];
        public string Version => "test";
        public bool IsTickThread => true;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { lock (Lines) Lines.Add($"{level}: {text}"); }
        public nint Service(string name, uint minVersion) => 0;
    }
    [Fact]
    public void EditCapsArePerRegistryAndAnOverCapCallIsNeverThrown()
    {
        Assert.Equal(16384, RegistrySchema.EditsPerCall["randomLoot"]);
        Assert.Equal(4096, RegistrySchema.EditsPerCall["lootRules"]);
        foreach (var shipped in new[] { "items", "recipes", "loot", "drops", "balance" }) Assert.Equal(4096, RegistrySchema.EditsPerCall[shipped]);
        var backend = Backend.Current; var log = new LogBackend(); Backend.Current = log;
        try
        {
            RegistryContext Context() => new(Mod(), RegistryEntryPoint.WorldLoad, RegistryPhase.Alter, null, "test-code",
                (registry, id, field) => field.EndsWith(".mode", StringComparison.Ordinal) ? "\"none\"" : "1", (_, _) => new[] { "game:world_509feadb_38db0fdc" });
            // under: a Loot Rarity Leveler sized call (more than 4096 edits in all, each registry within its own cap)
            var context = Context();
            using (context.Enter())
            {
                var r = DataRegistry.For(context);
                var table = r.RandomLoot["game:world_509feadb_38db0fdc"];
                for (int i = 0; i < 6000; ++i) table.Level.Value = i;
                for (int i = 0; i < 4096; ++i) r.LootRules.RarityWeights.Common = i;
                Assert.Equal(10096, JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray().Count);
            }
            context.Close();
            List<string> Mine() { lock (log.Lines) return log.Lines.Where(l => l.Contains("edits in one call", StringComparison.Ordinal)).ToList(); }
            Assert.Empty(Mine());
            // over: no exception in the mod's code; the call keeps one edit past randomLoot's cap (Kiln refuses it whole) and
            // the mod's log names the count and the cap
            context = Context();
            using (context.Enter())
            {
                var r = DataRegistry.For(context);
                var table = r.RandomLoot["game:world_509feadb_38db0fdc"];
                for (int i = 0; i < 20000; ++i) table.Level.Value = i;
                r.LootRules.RarityWeights.Common = 2;
                var edits = JsonNode.Parse(context.CaptureIr()!)!["edits"]!.AsArray();
                Assert.Equal(16385 + 1, edits.Count);
                Assert.Equal(16385, edits.Count(e => e!["registry"]!.GetValue<string>() == "randomLoot"));
            }
            context.Close();
            var line = Assert.Single(Mine());
            Assert.Contains("OnRegistry WorldLoad Alter: 20000 randomLoot edits in one call, over its cap of 16384 edits per call", line);
            Assert.Contains("Kiln refuses this call whole", line);
            Assert.StartsWith("Warn", line);
        }
        finally { Backend.Current = backend; }
    }

    // Registry.Reauthor inside a pass would queue that pass again from the pass itself: on every tick for ever when a mod
    // asks in each OnRegistry. It is asked for when settings or game state change, outside the callbacks: refused inside.
    [Fact]
    public void ReauthorInsideARegistryCallbackIsRefusedAndNeverQueued()
    {
        var backend = Backend.Current; var log = new LogBackend(); Backend.Current = log;
        var previous = RegistryRuntime.Reauthor; var queued = 0;
        RegistryRuntime.Reauthor = (_, _) => { ++queued; return null; };
        var mod = Mod();
        try
        {
            var context = new RegistryContext(mod, RegistryEntryPoint.WorldLoad, RegistryPhase.Alter, null, "test-code", (_, _, _) => "1", (_, _) => []);
            using (context.Enter()) Assert.False(DataRegistry.Reauthor(RegistryEntryPoint.WorldLoad));
            context.Close();
            Assert.Equal(0, queued);
            lock (log.Lines) Assert.Contains(log.Lines, l => l.Contains("Reauthor(WorldLoad) was not queued: it is asked for outside OnRegistry", StringComparison.Ordinal));
            HostAccess.Current = mod;
            try { Assert.True(DataRegistry.Reauthor(RegistryEntryPoint.WorldLoad)); } finally { HostAccess.Current = null; }
            Assert.Equal(1, queued);
        }
        finally { RegistryRuntime.Reauthor = previous; Backend.Current = backend; mod.Retire(); }
    }

    [Fact]
    public void UnloadedOwnerFailsBeforeAnyBackendAccess()
    {
        var mod = Mod(); var context = Frame(mod);
        using var scope = context.Enter();
        var item = DataRegistry.For(context).Items["game:wood"];
        mod.Retire();
        Assert.Equal(RegistryContextError.Unloaded, Assert.Throws<RegistryContextException>(() => item.MaxStack).Code);
        context.Close();
    }

    [Fact]
    public void CallbackContextCannotBeUsedFromAnotherThread()
    {
        var context = Frame(Mod());
        using var scope = context.Enter();
        Exception? failure = null;
        var thread = new Thread(() => failure = Record.Exception(() => DataRegistry.For(context)));
        thread.Start(); thread.Join();
        Assert.Equal(RegistryContextError.WrongThread, Assert.IsType<RegistryContextException>(failure).Code);
        context.Close();
    }

    [Theory]
    [InlineData(RegistryEntryPoint.GameStart)]
    [InlineData(RegistryEntryPoint.ModLoad)]
    public void EarlyPointsCannotReadWorldSettingsEvenIfAWorldExists(RegistryEntryPoint point)
    {
        var context = Frame(Mod(), point, world: new RegistryWorld(123, WorldKind.Server, "world-a"));
        using var scope = context.Enter();
        Assert.Equal(RegistryContextError.WorldSettingsUnavailable,
            Assert.Throws<RegistryContextException>(() => Config.RegisterWorld<Settings>()).Code);
        context.Close();
    }

    [Fact]
    public void PointWorldAndObservedSettingsContributeToIdentity()
    {
        var mod = Mod();
        var a = Frame(mod, RegistryEntryPoint.WorldLoad, world: new RegistryWorld(1, WorldKind.Server, "a"));
        var b = Frame(mod, RegistryEntryPoint.WorldLoad, world: new RegistryWorld(2, WorldKind.Server, "b"));
        var c = Frame(mod, RegistryEntryPoint.HotReload, world: new RegistryWorld(1, WorldKind.Server, "a"));
        Assert.NotEqual(a.Identity, b.Identity); Assert.NotEqual(a.Identity, c.Identity);
        var before = a.Identity;
        a.ObserveSettings("test", "{\"value\":1}");
        Assert.NotEqual(before, a.Identity);
        a.Close(); b.Close(); c.Close();
    }

    [Fact]
    public void GlobalSettingsAreReadOnlyInputsAndAutomaticallyAffectIdentity()
    {
        var context = Frame(Mod());
        using var scope = context.Enter();
        var before = context.Identity;
        var settings = Config.Register<Settings>(name: "registry-input");
        Assert.Equal(3, settings.Value.Value);
        Assert.NotEqual(before, context.Identity);
        Assert.DoesNotContain(Config.All, file => ReferenceEquals(file, settings));
        Assert.Equal(RegistryContextError.ReadOnly, Assert.Throws<RegistryContextException>(() => settings.Save()).Code);
        context.Close();
    }

    [Fact]
    public void RepeatedReadsOfOneSettingsInputDoNotChangeItsIdentity()
    {
        var context = Frame(Mod()); using var scope = context.Enter();
        var settings = Config.Register<Settings>(name: "repeated-input");
        _ = settings.Value; var identity = context.Identity;
        for (var i = 0; i < 10; ++i) _ = settings.Value;
        Assert.Equal(identity, context.Identity); context.Close();
    }
    [Fact]
    public void InputLoadingObservesOnlyTheCompleteValidatedSettingsOnce()
    {
        var root = Directory.CreateTempSubdirectory("registry-input-").FullName;
        var previous = KilnPaths.Root; Config.Reset();
        KilnPaths.Root = root; KilnPaths.Config = null!;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "test-complete-input.json"), "{\"value\":9}");
            var owner = Mod(); var context = Frame(owner); ModConfig<Settings> input; string identity;
            using (context.Enter())
            {
                input = Config.Register<Settings>(name: "complete-input");
                Assert.Equal(9, input.Value.Value);
                identity = context.Identity; context.Close();
            }
            var expected = Frame(owner);
            expected.ObserveSettings(input.Id, input.ValuesJson());
            Assert.Equal(expected.Identity, identity); expected.Close(); owner.Retire();
        }
        finally { Config.Reset(); KilnPaths.Root = previous; KilnPaths.Config = null!; Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(RegistryPhase.Add)]
    [InlineData(RegistryPhase.Final)]
    public void ReadOnlyFieldsAreRejectedBeforeAWriteIsIgnoredInAnotherPass(RegistryPhase phase)
    {
        var context = Frame(Mod(), phase: phase); using var scope = context.Enter();
        var registry = DataRegistry.For(context); var field = RegistrySchema.Find("items.hasArmor")!;
        Assert.Throws<ArgumentException>(() => registry.Cells("items", "game:wood").Set(field.Id, -1, true));
        Assert.Throws<ArgumentException>(() => registry.Cells("items", "game:wood").ListOp(field.Id, RegistryListOp.Clear, []));
        Assert.Null(context.CaptureIr()); context.Close();
    }
    [Fact]
    public void ManualFinalHelperWritesOneAlterTransaction()
    {
        var context = Frame(Mod(), RegistryEntryPoint.Manual, world: new RegistryWorld(1, WorldKind.Server, "a"));
        using var scope = context.Enter();
        DataRegistry.For(context).Final(view => view.Items["game:wood"].MaxStack = 20);
        var ir = JsonNode.Parse(context.CaptureIr()!)!;
        Assert.Equal("alter", ir["stage"]!.GetValue<string>()); Assert.Single(ir["edits"]!.AsArray());
        context.Close();
    }
    [Fact]
    public void ResolvedViewsCannotWriteEvenInTheAlterPass()
    {
        var owner = Mod();
        var context = new RegistryContext(owner, RegistryEntryPoint.Manual, RegistryPhase.Alter, null, "read",
            (_, _, _) => "10", (_, _) => new[] { "game:wood" }) { ReadOnly = true };
        using var scope = context.Enter();
        var item = DataRegistry.For(context).Items["game:wood"];
        Assert.Equal(RegistryContextError.ReadOnly, Assert.Throws<RegistryContextException>(() => item.MaxStack = 20).Code);
        context.Close();
    }

    // review4 recheck nit: a bulk author pays one world check per tick step, not one native status call per cell.
    [Fact]
    public void AWorldBoundContextChecksItsWorldOncePerStep()
    {
        var owner = Mod(); ulong token = 1, step = 7; var checks = 0;
        var context = new RegistryContext(owner, RegistryEntryPoint.WorldLoad, RegistryPhase.Alter,
            new RegistryWorld(1, WorldKind.Server, "a"), "world", (_, _, _) => "10", (_, _) => new[] { "game:wood" })
            { CurrentWorldToken = () => { ++checks; return token; }, CurrentStep = () => step };
        using var scope = context.Enter();
        var item = DataRegistry.For(context).Items["game:wood"];
        for (var i = 0; i < 100; ++i) Assert.Equal(10, item.MaxStack);
        Assert.Equal(1, checks);
        token = 2; step = 8;  // the next step: checked again, and the world changed
        Assert.Equal(RegistryContextError.WorldChanged, Assert.Throws<RegistryContextException>(() => item.MaxStack).Code);
        Assert.Equal(2, checks);
        context.Close();
    }

    // review4 nit: the code identity (Kiln.Host hashes the mod's DLLs for it) is composed only when Identity is read,
    // never by Close; the identity read after Close is the one the callback would have read.
    [Fact]
    public void TheCodeIdentityIsComputedOnlyWhenIdentityIsRead()
    {
        var owner = Mod(); var hashed = 0;
        var context = new RegistryContext(owner, RegistryEntryPoint.GameStart, RegistryPhase.Alter, null,
            () => { ++hashed; return "code"; }, (_, _, _) => "10", (_, _) => new[] { "game:wood" });
        var open = new RegistryContext(owner, RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "code", (_, _, _) => "10", (_, _) => new[] { "game:wood" });
        using (context.Enter()) { }
        context.Close();
        Assert.Equal(0, hashed);
        string expected;
        using (open.Enter()) expected = open.Identity;
        open.Close();
        Assert.Equal(expected, context.Identity);
        Assert.Equal(expected, context.Identity);
        Assert.Equal(1, hashed);
    }

    [Fact]
    public void AWorldBoundContextFailsBeforeReadingAfterTheWorldChanges()
    {
        var owner = Mod(); ulong token = 1; var reads = 0;
        var context = new RegistryContext(owner, RegistryEntryPoint.WorldLoad, RegistryPhase.Alter,
            new RegistryWorld(1, WorldKind.Server, "a"), "world", (_, _, _) => { ++reads; return "10"; }, (_, _) => new[] { "game:wood" })
            { CurrentWorldToken = () => token };
        using var scope = context.Enter();
        var item = DataRegistry.For(context).Items["game:wood"];
        token = 2;
        Assert.Equal(RegistryContextError.WorldChanged, Assert.Throws<RegistryContextException>(() => item.MaxStack).Code);
        Assert.Equal(0, reads);
        context.Close();
    }

    [Fact]
    public void CatchingAnInvalidIndexedWriteDoesNotLeaveABufferedEdit()
    {
        var owner = Mod();
        var context = new RegistryContext(owner, RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "list",
            (_, _, _) => "[1,2]", (_, _) => []);
        using var scope = context.Enter();
        var registry = DataRegistry.For(context);
        var field = RegistrySchema.Find("balance.xpPerLevel")!;
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Cells("balance", "game:balance").Set(field.Id, 9, 3L));
        Assert.Null(context.CaptureIr()); context.Close();
    }

    [Fact]
    public void ReadyFromANativeThreadIsDeliveredThroughTheOwnersScheduler()
    {
        var owner = Mod(); var calls = 0; var callbackThread = 0;
        var context = Frame(owner, RegistryEntryPoint.ModLoad);
        using (context.Enter()) DataRegistry.Ready += _ => { ++calls; callbackThread = Environment.CurrentManagedThreadId; };
        context.Close(); owner.Pending.Clear();
        var native = new Thread(() => RegistryRuntime.PublishReady(RegistryReadyPhase.Start, 42, null));
        native.Start(); native.Join();
        Assert.Equal(0, calls);
        Assert.Single(owner.Pending);
        owner.Pending.Dequeue()();
        Assert.Equal(1, calls); Assert.Equal(Environment.CurrentManagedThreadId, callbackThread);
        owner.Retire();
    }

    private static int RetirementHandlers(ModContext owner) =>
        (typeof(ModContext).GetField("Retiring", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(owner) as Delegate)?.GetInvocationList().Length ?? 0;
    [Fact]
    public void ReadySubscriptionsShareOneOwnerHookAndRemoveItWithTheLastHandler()
    {
        var owner = Mod(); var context = Frame(owner, RegistryEntryPoint.ModLoad);
        var before = RetirementHandlers(owner);
        Action<RegistryReady> first = _ => { }, second = _ => { };
        using (context.Enter())
        {
            DataRegistry.Ready += first; DataRegistry.Ready += second;
            Assert.Equal(before + 1, RetirementHandlers(owner));
            DataRegistry.Ready -= first; Assert.Equal(before + 1, RetirementHandlers(owner));
            DataRegistry.Ready -= second; Assert.Equal(before, RetirementHandlers(owner));
            for (var i = 0; i < 10; ++i) { DataRegistry.Ready += first; DataRegistry.Ready -= first; }
            Assert.Equal(before, RetirementHandlers(owner));
        }
        context.Close(); owner.Retire();
    }
    [Fact]
    public void CatchingAFailedListReadDoesNotLeaveABufferedEdit()
    {
        var owner = Mod();
        var context = new RegistryContext(owner, RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "list",
            (_, _, _) => throw new KeyNotFoundException("unavailable list"), (_, _) => []);
        using var scope = context.Enter();
        var registry = DataRegistry.For(context);
        Assert.Throws<KeyNotFoundException>(() => registry.Items["game:wood"].Perks.Clear());
        Assert.Null(context.CaptureIr()); context.Close();
    }

    [Fact]
    public void DefaultEntryIdsAreRejectedBeforeTransportAccess()
    {
        var context = Frame(Mod()); using var scope = context.Enter();
        Assert.Throws<ArgumentException>(() => DataRegistry.For(context).Items[default(RegistryId)]);
        context.Close();
    }

    [Fact]
    public void ChangesHaveExplicitRevertLifetimeRatherThanDisposableScopeLifetime()
    {
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(RegistryChange)));
        Assert.Null(typeof(RegistryChange).GetMethod("Dispose"));
        Assert.NotNull(typeof(RegistryChange).GetMethod(nameof(RegistryChange.Revert))!.GetCustomAttributes(typeof(TickThreadOnlyAttribute), false).SingleOrDefault());
    }
    private sealed class ThreadBackend(int tick) : IKilnBackend
    {
        public string Version => "test";
        public bool IsTickThread => Environment.CurrentManagedThreadId == tick;
        public bool HasFeature(string name) => false;
        public void Log(ulong owner, LogLevel level, string text) { }
        public nint Service(string name, uint minVersion) => 0;
    }
    [Fact]
    public void ExplicitRevertRefusesOtherThreadsAndRunsOnlyOnceOnTheTick()
    {
        var owner = Mod(); var calls = 0;
        var backend = Backend.Current; var previous = RegistryRuntime.Revert;
        Backend.Current = new ThreadBackend(Environment.CurrentManagedThreadId);
        RegistryRuntime.Revert = (_, _) => { ++calls; return new(5, 0, 0, 0, 1, 2, 0, ""); };
        try
        {
            var change = new RegistryChange(owner, new(0, 1, 0, 0, 1, 1, 9, ""));
            Exception? failure = null;
            var worker = new Thread(() => failure = Record.Exception(() => { change.Revert(); }));
            worker.Start(); worker.Join();
            Assert.IsType<InvalidOperationException>(failure); Assert.Equal(0, calls);
            Assert.True(change.Revert().Ok); Assert.True(change.Revert().Ok); Assert.Equal(1, calls);
        }
        finally { Backend.Current = backend; RegistryRuntime.Revert = previous; owner.Retire(); }
    }
    [Fact]
    public void AuthoringPassesAndSnapshotReadinessUseSeparateEnums()
    {
        Assert.Equal(new[] { "Add", "Alter", "Final" }, Enum.GetNames<RegistryPhase>());
        var phase = typeof(RegistryReady).GetProperty(nameof(RegistryReady.Phase))!.PropertyType;
        Assert.Equal("Kiln.RegistryReadyPhase", phase.FullName);
        Assert.Equal(new[] { "Start", "WorldLoad" }, Enum.GetNames(phase));
        Assert.Equal(new[] { 4, 5 }, Enum.GetValues(phase).Cast<object>().Select(Convert.ToInt32));
    }
    [Fact]
    public void EveryNonOkCompletionHasAReason()
    {
        for (uint outcome = 0; outcome <= 8; ++outcome)
        {
            var result = RegistryChangeResult.From(new(outcome, 0, 0, 0, 0, 0, 0, ""));
            if (!result.Ok) Assert.False(string.IsNullOrWhiteSpace(result.Reason), $"outcome {outcome} has no reason");
        }
    }

    public sealed class Settings { public int Value { get; set; } = 3; }
}
