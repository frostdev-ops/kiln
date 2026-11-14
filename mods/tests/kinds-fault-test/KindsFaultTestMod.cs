// SPDX-License-Identifier: MIT
// Lab test of block kinds, the part a public mod cannot test (chapter 16d §6): a plan that is refused part way. For each of
// the five primaries a registration is made to fail at every position in turn (Kiln.Blocks' sink, which this friend
// assembly may replace); after each the plan is Refused, no block is published, and the same name applies cleanly
// afterwards, which it could not if the refused attempt's owner had kept a definition. kinds-provider-test and
// kinds-consumer-test do the rest with the public SDK only.
using System.Text.Json.Nodes;
using Kiln;

namespace KindsFaultTest;

public sealed record KindProbeBlock : IBlock
{
    public string Primary { get; init; } = "item";
}

public sealed class KindsFaultTestMod : Mod
{
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"kinds-fault-test: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"kinds-fault-test: PASS {what}"); }
        else { _fail++; Log.Error($"kinds-fault-test: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    /// <summary>The plan's application once it no longer waits (an item plan's parts at content ready take one pass and the check of its
    /// definitions, about 150 steps), at most <paramref name="max"/> steps.</summary>
    private async Task<BlockApplyResult> Settled(BlockPlan plan, int max = 600)
    {
        var r = Blocks.Application(plan);
        for (var i = 0; i < max && r.State == BlockApplyState.PendingContent; i++)
        {
            await Steps(1);
            r = Blocks.Application(plan);
        }
        return r;
    }

    public override void OnLoad()
    {
        if (Mods.Side != ModSide.Server) return;
        _ = Run();
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (Content.Status() is not { Ready: true } && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(90);
            await KindFaults();
            await DataOnlyUnload();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        Log.Info($"kinds-fault-test: DONE pass={_pass} fail={_fail}");
    }

    // A mod with blocks files and no code has an owner of Kiln's own: unloading it takes its recipe and rules back as a
    // code mod's unload does, and loading it again makes them once.
    private async Task DataOnlyUnload()
    {
        const string fixture = "kinds-dataonly-fixture", item = "kinds-dataonly-fixture.pebble";
        if (!Mods.IsLoaded(fixture))
        {
            Info($"{fixture} is not in this profile: the data-only unload checks did not run");
            return;
        }
        for (var i = 0; i < 240 && Content.Items.Get(item) == null; i++) await Steps(1);
        int Recipes() => Content.Recipes.Count(r => r.Name.StartsWith(item, StringComparison.Ordinal));
        if (!Check(Content.Items.Get(item) != null && Recipes() == 1, $"data-only: the fixture's item and its recipe are defined ({Recipes()} recipe(s))")) return;
        var folder = Path.GetFileName(Mods.Get(fixture)!.Directory.TrimEnd('/', '\\'));
        var unloaded = await Mods.UnloadAsync(fixture);
        await Steps(30);
        // kiln.content keeps a definition whose owner went listed until the game restarts, as "orphaned"
        string States() => string.Join(", ", NewContent.Definitions().Where(d => d.Name.StartsWith(item, StringComparison.Ordinal)).Select(d => $"{d.Kind} {d.State}"));
        bool All(string state) => NewContent.Definitions().Where(d => d.Name.StartsWith(item, StringComparison.Ordinal)).ToList() is { Count: >= 2 } ds && ds.All(d => d.State == state);
        Check(unloaded.Ok && All("orphaned") && Blocks.Find("pebble", fixture) == null, $"data-only: unloaded, its block is gone and its definitions are orphaned ({unloaded.Message}; {States()})");
        var loaded = await Mods.LoadAsync(folder);
        for (var i = 0; i < 240 && !All("registered"); i++) await Steps(1);
        await Steps(30);
        Check(loaded.Ok && All("registered") && Recipes() == 1, $"data-only: loaded again, the item and one recipe are registered ({loaded.Message}; {States()})");
    }

    private sealed class KindProbe : IBlockKind<KindProbeBlock>
    {
        private static StatusBlock Chilled => new() { Name = "Probe chill", Seconds = 4, Kind = StatusKind.Debuff };

        public BlockPlan Compile(BlockCompileContext c, KindProbeBlock b) => b.Primary switch
        {
            "item" => c.Plan("p", [c.Item("p", new ItemBlock { Name = "Probe berry", Kind = ItemKind.Consumable, OnUse = [new EffectBlock { On = EffectOn.Caster, Applies = Chilled }], Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 2 } } }), c.Effect("aux", new EffectBlock { Heal = 1 })]),
            "weapon" => c.Plan("p", [c.Weapon("p", new WeaponBlock { Name = "Probe edge", Class = WeaponClass.Sword, OnHit = [new EffectBlock { Applies = Chilled }] })]),
            "spell" => c.Plan("p", [c.Spell("p", new SpellBlock { Name = "Probe bolt", Cast = new CastBlock { ManaCost = 5 }, Delivery = new ProjectileBlock { OnImpact = [new EffectBlock { Area = new AreaBlock { Radius = 3, Effects = [new EffectBlock { Damage = Damage.Ice(5), Applies = Chilled }] } }] } })]),
            "status" => c.Plan("p", [c.Status("p", Chilled with { OnApply = [new EffectBlock { Heal = 2 }] })]),
            _ => c.Plan("p", [c.Effect("p", new EffectBlock { Damage = Damage.Ice(5), Applies = Chilled })]),
        };
    }

    private sealed class FaultSink(Lowered.ILoweredSink inner) : Lowered.ILoweredSink
    {
        public int FailAt = -1, Writes;
        private void Step()
        {
            if (Writes == FailAt) throw new InvalidOperationException("refused: injected by kinds-fault-test");
            Writes++;
        }
        public void Program(ulong owner, Guid guid, byte[] blob) { Step(); inner.Program(owner, guid, blob); }
        public void Clone(ulong owner, string section, JsonObject def) { Step(); inner.Clone(owner, section, def); }
        public void Item(ulong owner, ItemDefinition d) { Step(); inner.Item(owner, d); }
        public void Recipe(ulong owner, RecipeDefinition d) { Step(); inner.Recipe(owner, d); }
    }

    private BlockApplyResult ApplyThrough(FaultSink sink, BlockPlan plan)
    {
        Lowered._sinkOverride = sink;
        try { return Blocks.Apply(plan); }
        finally { Lowered._sinkOverride = null; }
    }

    private async Task KindFaults()
    {
        var registered = Blocks.RegisterKind(new BlockKindDescriptor { Id = "kinds-fault-test:probe", JsonSection = "kinds-fault-test:probes", SchemaJson = """{ "type": "object" }""", Clients = ClientsLevel.Kiln }, new KindProbe());
        if (!Check(registered.Ok, $"block kinds: kinds-fault-test registers a kind of its own ({registered.Reason})")) return;
        if (!Check(Capabilities.Is("blocks.apply"), $"blocks.apply is on ({Capabilities.Why("blocks.apply")})")) return;
        var real = Lowered.Sink;
        foreach (var primary in new[] { "item", "weapon", "spell", "status", "effect" })
        {
            var block = new KindProbeBlock { Primary = primary };
            if (!Capabilities.Is($"blocks.apply.{primary}"))
            {
                Info($"block kinds: blocks.apply.{primary} is off ({Capabilities.Why($"blocks.apply.{primary}")})");
                continue;
            }
            // how many registrations a clean apply makes
            var count = new FaultSink(real);
            var cleanPlan = Blocks.Compile($"kf-{primary}-clean", block);
            var clean = ApplyThrough(count, cleanPlan);
            clean = await Settled(cleanPlan);
            if (!Check(clean.Ok && count.Writes >= 1, $"block kinds: a clean {primary} plan applies ({clean.State} {clean.Reason}, {count.Writes} registration(s))")) continue;
            var name = $"kf-{primary}";
            var allRefused = true;
            var said = "";
            for (var failAt = 0; failAt < count.Writes; failAt++)
            {
                var sink = new FaultSink(real) { FailAt = failAt };
                var plan = Blocks.Compile(name, block);
                var r = ApplyThrough(sink, plan);
                await Steps(2);
                r = Blocks.Application(plan);
                var ok = !r.Ok && r.State == BlockApplyState.Refused && r.Reason.StartsWith("Refused: refused:", StringComparison.Ordinal) && Blocks.Find(name) == null && sink.Writes == failAt;
                if (!ok) said += $" [at {failAt}: {r.State} {r.Reason}, handle {(Blocks.Find(name) != null)}, {sink.Writes} write(s)]";
                allRefused &= ok;
            }
            Check(allRefused, $"refusal-no-publish {primary}: a refusal at each of {count.Writes} registration(s) leaves the plan Refused and no block{said}");
            // nothing of the refused attempts is left under the name: the same block applies, and is the game's
            var finalPlan = Blocks.Compile(name, block);
            var final = Blocks.Apply(finalPlan);
            final = await Settled(finalPlan);
            if (Blocks.Find(name) is { } made) final = Blocks.Application(made);
            Check(final.Ok && Blocks.Find(name) != null, $"refusal-no-publish {primary}: after {count.Writes} refused attempt(s) the same name applies cleanly ({final.State} {final.Reason})");
        }
    }
}
