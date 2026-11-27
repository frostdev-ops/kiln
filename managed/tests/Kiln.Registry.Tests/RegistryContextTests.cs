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

    // dev.32: the skills and stations registries are typed entries like the others: a node's cost reads and edits, its kind and
    // unlocked ability read; a station is read-only (no setter), its item a Kiln id.
    [Fact]
    public void SkillsAndStationsAreTypedEntries()
    {
        var context = new RegistryContext(Mod(), RegistryEntryPoint.GameStart, RegistryPhase.Alter, null, "test-code",
            (registry, _, field) => (registry, field) switch
            {
                ("skills", "cost") => "3", ("skills", "kind") => "\"active\"", ("skills", "class") => "\"neutral\"",
                ("stations", "npc") => "true", ("stations", "item") => "\"game:npc_crafter_blacksmith01\"", _ => "null",
            }, (_, _) => new[] { "game:skill_4475e8dd" });
        using var scope = context.Enter();
        var registry = DataRegistry.For(context);
        var node = registry.Skills["game:skill_4475e8dd"];
        Assert.Equal(3, node.Cost);
        Assert.Equal(SkillNodeKind.Active, node.Kind);
        Assert.Equal(SkillNodeClass.Neutral, node.Class);
        Assert.Null(node.Unlocks);
        node.Cost = 0;
        Assert.Equal(0, node.Cost);
        var station = registry.Stations["game:npc_crafter_blacksmith01"];
        Assert.True(station.Npc);
        Assert.Equal(new RegistryId("game:npc_crafter_blacksmith01"), station.Item);
        Assert.Null(typeof(StationEntry).GetProperty(nameof(StationEntry.Npc))!.SetMethod);
        var ir = JsonNode.Parse(context.CaptureIr()!)!;
        var edit = Assert.Single(ir["edits"]!.AsArray())!;
        Assert.Equal("skills", edit["registry"]!.GetValue<string>());
        Assert.Equal("cost", edit["field"]!.GetValue<string>());
        Assert.Equal(0, edit["op"]!.GetValue<int>());
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
