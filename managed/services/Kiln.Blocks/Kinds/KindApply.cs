// SPDX-License-Identifier: MIT
// Applying a kind's plan (chapter 16d section 4): the frozen products are lowered by Kiln's own compilers into a ledger of
// the plan's own (KindTransaction), under a native owner of the plan's own, and nothing of it is published until every
// registration was taken. A refusal part way revokes that owner and the plan leaves nothing behind. A plan is bound to the
// mod instance that compiled it; the provider's code never runs here.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Kiln;

internal sealed class KindApplication
{
    public required BlockPlan Plan;
    public required ModBlocks Target;
    public required BlockHandle Handle;      // provisional until Applied; then what Blocks.Find returns
    public required KindStage Stage;
    public BlockApplyState State = BlockApplyState.PendingContent;
    public string Reason = "PendingContent";
    public ulong Generation, ContentGeneration, Owner;

    public BlockApplyResult Result => new()
    {
        Ok = State == BlockApplyState.Applied, Reason = State == BlockApplyState.Applied ? "" : Reason, State = State, RequestId = Plan.RequestId,
        Generation = Generation, ContentGeneration = ContentGeneration, Guid = Handle.Guid, Id = Handle.Id, GameName = Handle.GameName,
    };
}

/// <summary>A plan lowered and not committed: the ledger, the blocks in commit order and the primary's handle.</summary>
internal sealed record KindStage(ModBlocks Ledger, IReadOnlyList<KindTransaction.StagedBlock> Blocks, BlockHandle Primary, ulong? ContentGeneration, long Bytes);

internal static class KindApply
{
    internal const long MaxPlanBytes = 16L << 20, MaxConsumerBytes = 64L << 20;

    private static readonly object Gate = new();
    private static readonly Dictionary<(object Consumer, string Name), KindApplication> Apps = new();
    private static readonly ConditionalWeakTable<BlockHandle, KindApplication> ByHandle = new();
    private static readonly HashSet<object> Watched = new(ReferenceEqualityComparer.Instance);
    private static ulong _generation;
    private static IDisposable? _tick;

    // ---- what the game answers; tests replace these ------------------------------------------------------------

    /// <summary>The content generation now, or null while content is not ready.</summary>
    internal static Func<ulong?> ContentGeneration = () => Content.Status() is { Ready: true } s ? s.Generation : null;
    /// <summary>A native owner for one plan.</summary>
    internal static Func<string, ulong> CreateOwner = BlocksBridge.ServiceOwner;
    /// <summary>Revokes a plan's owner: every layer it registered comes off. Null: this build cannot (then nothing is applied).</summary>
    internal static Action<ulong>? RevokeOwner;
    internal static Func<string, bool> CapabilityIs = Capabilities.Is;
    internal static Func<bool> OnTickThread = () => Game.IsTickThread;

    private static string Staged(BlockPlan plan, string key) => KindCompiler.StagedName(plan.Name, key);

    private static IBlock Parse(BlockProduct p) => p.Kind switch
    {
        BuiltInBlockProductKind.Item => BlockJson.Parse<ItemBlock>(p.CanonicalJson),
        BuiltInBlockProductKind.Weapon => BlockJson.Parse<WeaponBlock>(p.CanonicalJson),
        BuiltInBlockProductKind.Spell => BlockJson.Parse<SpellBlock>(p.CanonicalJson),
        BuiltInBlockProductKind.Status => BlockJson.Parse<StatusBlock>(p.CanonicalJson),
        _ => BlockJson.Parse<EffectBlock>(p.CanonicalJson),
    };

    private static BlockHandle Define(ModBlocks target, string name, IBlock block) => block switch
    {
        ItemBlock b => Compiler.Define(target, name, b),
        WeaponBlock b => Compiler.Define(target, name, b),
        SpellBlock b => Compiler.Define(target, name, b),
        StatusBlock b => Compiler.Define(target, name, b),
        _ => Compiler.Define(target, name, (EffectBlock)block),
    };

    /// <summary>Lowers the plan's products into a ledger of their own. Registers nothing. What is wrong with a product is a
    /// <see cref="BlockError"/> that names the plan's block.</summary>
    internal static KindStage Stage(ModBlocks target, BlockPlan plan)
    {
        KindCompiler.RefuseInsideCompile("Blocks.Apply");
        using var tx = KindTransaction.Begin(target);
        var label = $"{plan.KindId} {plan.Name}";
        try
        {
            // each product waits under its staged name: one that names another (ProductName) stages it first
            foreach (var p in plan.Products)
            {
                var (name, product) = (Staged(plan, p.Key), p);
                if (target.Handles.ContainsKey(name)) throw new BlockError(label, p.Key, $"mod {target.Id} has a block \"{name}\" already: a kind's plan never replaces one");
                BlockHandle? done = null;
                tx.Stage.Pending[name] = () => done ??= Define(target, name, Parse(product));
            }
            foreach (var p in plan.Products)
            {
                var name = Staged(plan, p.Key);
                if (tx.Stage.Handles.ContainsKey(name)) continue;
                if (!tx.Stage.Compiling.Add(name)) throw new BlockError(label, p.Key, "the product refers back to itself");
                try { tx.Stage.Pending[name](); }
                finally { tx.Stage.Compiling.Remove(name); }
            }
        }
        catch (BlockError e) when (!e.Block.StartsWith(plan.KindId, StringComparison.Ordinal))
        {
            throw new BlockError(label, e.Field, $"{e.Block}: {e.Problem}");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new BlockError(label, null, $"a frozen product does not read back as its block: {e.Message}");
        }
        tx.Stage.Pending.Clear();
        var bytes = tx.Staged.Sum(s => s.Low.GeneratedBytes);
        if (bytes > MaxPlanBytes) throw new BlockError(label, null, $"the plan makes {bytes >> 20} MiB of programs and definitions; at most {MaxPlanBytes >> 20} MiB");
        var primary = tx.Stage.Handles[Staged(plan, plan.PrimaryKey)];
        return new KindStage(tx.Stage, [.. tx.Staged], primary, ContentGeneration(), bytes);
    }

    /// <summary>The identities of the consumer's plans that are compiled and not refused: a later compile may name them.</summary>
    internal static List<(string, string, KindCompiler.Identity)> Provisional(KindOwner consumer)
    {
        lock (Gate)
            return [.. Apps.Where(a => ReferenceEquals(a.Key.Consumer, consumer.Token) && a.Value.State is BlockApplyState.PendingContent or BlockApplyState.Applied)
                .Select(a => (consumer.ModId, a.Key.Name, new KindCompiler.Identity(a.Value.Handle.Guid, a.Value.Handle.GameName, a.Value.Handle.BlockType, a.Value.Plan.KindId)))];
    }

    private static void CheckCaller(BlockPlan plan, KindOwner caller, string api)
    {
        if (!plan.Consumer.Same(caller))
            throw new ArgumentException($"{api}: the plan of \"{plan.Name}\" was compiled by {(plan.ConsumerModId == caller.ModId ? "an earlier load of this mod" : $"mod {plan.ConsumerModId}")}; a plan is applied by the mod instance that compiled it");
        if (!caller.Alive()) throw new ArgumentException($"{api}: mod {caller.ModId} is unloading");
    }

    /// <summary>Takes a plan for its consumer: staged at once (so the identity is known and what is wrong throws here), applied
    /// at once when it can be, else when content is ready. <paramref name="handle"/> makes the block's handle from the primary's identity.</summary>
    internal static KindApplication Submit(ModBlocks target, KindOwner caller, BlockPlan plan, Func<KindStage, BlockHandle> handle)
    {
        CheckCaller(plan, caller, "Blocks.Apply");
        var key = (plan.Consumer.Token, plan.Name);
        KindApplication? before;
        lock (Gate) Apps.TryGetValue(key, out before);
        if (before != null)
        {
            if (before.State == BlockApplyState.Applied)
            {
                if (before.Plan.InputHash == plan.InputHash) return before;   // the same plan again: the same answer, no new layer, no event
                return new KindApplication { Plan = plan, Target = target, Handle = before.Handle, Stage = before.Stage, State = BlockApplyState.Refused, Reason = "NameAlreadyApplied: reload the mod to change a block that is applied" };
            }
            if (before.State == BlockApplyState.PendingContent && before.Plan.InputHash == plan.InputHash) return Pump(before);
            if (plan.RequestId < before.Plan.RequestId)
                return new KindApplication { Plan = plan, Target = target, Handle = before.Handle, Stage = before.Stage, State = BlockApplyState.Refused, Reason = "StaleRequest: a later compile of this block was submitted" };
        }
        if (target.Handles.ContainsKey(plan.Name)) throw new BlockError($"{plan.KindId} {plan.Name}", null, $"mod {target.Id} has a block \"{plan.Name}\" already");
        var stage = Stage(target, plan);
        long others;
        lock (Gate) others = Apps.Where(a => ReferenceEquals(a.Key.Consumer, plan.Consumer.Token) && a.Key.Name != plan.Name && a.Value.State != BlockApplyState.Refused).Sum(a => a.Value.Stage.Bytes);
        if (others + stage.Bytes > MaxConsumerBytes) throw new BlockError($"{plan.KindId} {plan.Name}", null, $"mod {target.Id}'s plans make more than {MaxConsumerBytes >> 20} MiB together");
        var app = new KindApplication { Plan = plan, Target = target, Stage = stage, Handle = handle(stage) };
        lock (Gate)
        {
            Apps[key] = app;
            ByHandle.AddOrUpdate(app.Handle, app);
            if (Watched.Add(plan.Consumer.Token) && plan.Consumer.Token is ModContext mod)
            {
                var token = plan.Consumer.Token;
                mod.Retiring += () => RetireConsumer(token);
            }
            try { _tick ??= BlocksBridge.AddTick("kiln blocks kinds", _ => PumpAll()); }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException) { }
        }
        if (before != null) Changed(app, before.Result);
        return Pump(app);
    }

    /// <summary>What <see cref="Blocks.Application(BlockPlan)"/> answers: the state of the plan's block for its consumer.</summary>
    internal static BlockApplyResult Of(BlockPlan plan)
    {
        lock (Gate)
        {
            if (Apps.TryGetValue((plan.Consumer.Token, plan.Name), out var app) && app.Plan.InputHash == plan.InputHash) return app.Result;
        }
        return new BlockApplyResult { Ok = false, State = BlockApplyState.Removed, Reason = "NotSubmitted: the plan was not applied, or its mod was unloaded", RequestId = plan.RequestId };
    }

    internal static BlockApplyResult? Of(BlockHandle handle) => ByHandle.TryGetValue(handle, out var app) ? app.Result : null;

    private static KindApplication Pump(KindApplication app)
    {
        if (app.State == BlockApplyState.PendingContent && OnTickThread()) TryCommit(app);
        return app;
    }

    /// <summary>The tick: plans that wait for content are tried again.</summary>
    internal static void PumpAll()
    {
        KindApplication[] waiting;
        lock (Gate) waiting = [.. Apps.Values.Where(a => a.State == BlockApplyState.PendingContent)];
        foreach (var app in waiting) TryCommit(app);
    }

    private static void Set(KindApplication app, BlockApplyState state, string reason)
    {
        var before = app.Result;
        app.State = state;
        app.Reason = reason;
        Changed(app, before);
    }

    private static void Changed(KindApplication app, BlockApplyResult before)
    {
        var after = app.Result;
        if (before.State == after.State && before.Generation == after.Generation && before.RequestId == after.RequestId) return;
        KindEvents.Queue(new BlockApplicationChangedEvent(app.Plan.ConsumerModId, app.Plan.Name, before, after));
    }

    private static void TryCommit(KindApplication app)
    {
        var plan = app.Plan;
        var label = $"{plan.KindId} {plan.Name}";
        if (!plan.Consumer.Alive()) return;   // its retirement takes it away
        if (plan.Kind.Retired) { Set(app, BlockApplyState.Refused, $"ProviderRetired: kind {plan.KindId} was taken away before the plan was applied"); return; }
        if (ContentGeneration() is not { } generation) return;   // PendingContent: tried again on the tick
        foreach (var need in plan.Requires)
            if (!CapabilityIs(need)) { Set(app, BlockApplyState.Refused, $"RequiresOff: kind {plan.KindId} needs {need}, which is off here"); return; }
        var have = (app.Target.ManifestClients ??= BlocksBridge.ManifestClients(app.Target.Id)) switch { "none" => ClientsLevel.None, "mod" => ClientsLevel.Mod, _ => ClientsLevel.Kiln };
        if (have < plan.Clients) { Set(app, BlockApplyState.Refused, $"ClientsTooLow: kind {plan.KindId} asks \"clients\": \"{plan.Clients.ToString().ToLowerInvariant()}\" of mod {app.Target.Id}"); return; }
        if (RevokeOwner is not { } revoke) { Set(app, BlockApplyState.Refused, "blocks.apply is off: this build cannot take a refused plan's registrations back"); return; }
        if (app.Target.Handles.ContainsKey(plan.Name)) { Set(app, BlockApplyState.Refused, $"NameTaken: mod {app.Target.Id} defined a block \"{plan.Name}\" in the meantime"); return; }
        // the plan was lowered against another content generation: lowered again, against this one
        if (app.Stage.ContentGeneration != generation)
        {
            try { app.Stage = Stage(app.Target, plan); }
            catch (KindDependencyPending) { return; }
            catch (BlockError e) { Set(app, BlockApplyState.Refused, $"DependencyRefused: {e.Message}"); return; }
        }
        ulong owner = 0;
        var ledger = app.Stage.Ledger;
        var tx = KindTransaction.Resume(app.Target, ledger);
        try
        {
            owner = CreateOwner($"blocks:{app.Target.Id}:kind:{plan.Name}");
            if (owner == 0) throw new InvalidOperationException("no owner could be made for the plan");
            ledger.Owner = owner;
            tx.BeginCommit();
            foreach (var staged in app.Stage.Blocks) staged.Low.CommitRegistrations();
            if (ContentGeneration() != generation) throw new KindStale();
            foreach (var staged in app.Stage.Blocks) staged.Low.CommitAfter();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            tx.Dispose();
            ledger.Revoked = true;
            if (owner != 0)
            {
                try { revoke(owner); }
                catch (Exception x) when (x is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Error, $"blocks: {app.Target.Id}: block {label}: its owner could not be revoked: {x.Message}"); }
            }
            if (e is KindStale)
            {
                // content changed under the attempt: nothing of it stays, and the plan is tried again against the new content
                app.Stage = app.Stage with { ContentGeneration = null };
                BlocksBridge.Log(LogLevel.Info, $"blocks: {app.Target.Id}: block {label}: StaleContent, the attempt was taken back and is tried again");
                return;
            }
            BlocksBridge.Log(LogLevel.Error, $"blocks: {app.Target.Id}: block {label} was refused, and nothing of it stays: {e.Message}");
            Set(app, BlockApplyState.Refused, $"Refused: {e.Message}");
            return;
        }
        tx.Dispose();
        // every registration was taken: the block exists from here on
        app.Owner = owner;
        app.ContentGeneration = generation;
        BlockRuntime.Promote(ledger);
        var json = new JsonObject { ["kind"] = plan.KindId, ["primary"] = app.Stage.Primary.Name, ["inputHash"] = plan.InputHash };
        var products = new JsonObject();
        foreach (var staged in app.Stage.Blocks) products[staged.Name] = staged.Low.Json.DeepClone();
        json["products"] = products;
        foreach (var (guid, program) in ledger.Programs) app.Target.Programs[guid] = program;
        app.Target.Handles[plan.Name] = app.Handle;
        app.Target.Lowered[plan.Name] = json;
        app.Target.ReportDirty = true;
        var before = app.Result;
        lock (Gate) app.Generation = ++_generation;
        app.State = BlockApplyState.Applied;
        app.Reason = "";
        BlocksBridge.Log(LogLevel.Info, $"blocks: {app.Target.Id}: {label} -> {app.Stage.Blocks.Count} block(s) of Kiln's own ({string.Join(", ", app.Stage.Blocks.Select(b => b.Label))})");
        Changed(app, before);
    }

    private sealed class KindStale : Exception;

    /// <summary>The consumer mod instance is gone: its plans' owners are revoked and their blocks are no more.</summary>
    internal static void RetireConsumer(object consumer)
    {
        KindApplication[] gone;
        lock (Gate)
        {
            gone = [.. Apps.Where(a => ReferenceEquals(a.Key.Consumer, consumer)).Select(a => a.Value)];
            foreach (var app in gone) Apps.Remove((consumer, app.Plan.Name));
            Watched.Remove(consumer);
        }
        foreach (var app in gone)
        {
            app.Stage.Ledger.Revoked = true;
            if (app.State == BlockApplyState.Applied)
            {
                if (app.Target.Handles.TryGetValue(app.Plan.Name, out var h) && ReferenceEquals(h, app.Handle)) app.Target.Handles.TryRemove(app.Plan.Name, out _);
                try { RevokeOwner?.Invoke(app.Owner); }
                catch (Exception x) when (x is not OutOfMemoryException) { BlocksBridge.Log(LogLevel.Error, $"blocks: {app.Target.Id}: block {app.Plan.Name}: its owner could not be revoked: {x.Message}"); }
            }
            if (app.State is BlockApplyState.Applied or BlockApplyState.PendingContent) Set(app, BlockApplyState.Removed, "Removed: the mod was unloaded");
        }
    }

    /// <summary>For tests: forget every plan, with no revoke and no event.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Apps.Clear();
            Watched.Clear();
            _generation = 0;
        }
    }
}
