// SPDX-License-Identifier: MIT
// Blocks.Compile (16d S2): the provider runs once, under a context that dies with the call; what it returns is frozen, bounded and
// hashed. The cases of the research contract model (out/blocks-a/probes/contract_checks.py) that concern compile, on the real code.
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Kiln.BlockTests;

[Collection("block kinds")]
public sealed class KindCompileTests
{
    private static readonly KindOwner Consumer = KindKit.Owner(KindKit.Consumer);

    private static BlockPlan Compile(KindEntry entry, LanternBlock b, string name = "camp", KindOwner? consumer = null) => KindCompiler.Compile(consumer ?? Consumer, entry, name, b);

    [Fact]
    public void The_lantern_compiles_to_one_item_and_the_same_from_code_and_JSON()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        var code = Compile(entry, new LanternBlock { Name = "Camp", Seconds = 600 });
        Assert.Equal(1, kind.Calls);
        Assert.Equal(("provider:lantern", "consumer", "camp", "light"), (code.KindId, code.ConsumerModId, code.Name, code.PrimaryKey));
        var p = Assert.Single(code.Products);
        Assert.Equal(("light", BuiltInBlockProductKind.Item), (p.Key, p.Kind));
        // the product reads back as the built-in block the provider made
        var item = BlockJson.Parse<ItemBlock>(p.CanonicalJson);
        Assert.Equal("Camp", item.Name);
        Assert.Equal(600, item.Tool!.Durability);
        Assert.Equal(2, item.Recipe!.Needs[new ItemRef("Material_T1_Twigs")]);

        var record = (LanternBlock)KindJson.ReadRecord(entry, "camp", JsonDocument.Parse("""{"format":1,"seconds":600,"name":"Camp"}""").RootElement);
        var data = Compile(entry, record);
        Assert.Equal(code.InputHash, data.InputHash);
        Assert.Equal(p.CanonicalJson, data.Products[0].CanonicalJson);
        Assert.NotEqual(code.InputHash, Compile(entry, new LanternBlock { Name = "Camp", Seconds = 601 }).InputHash);
        Assert.Equal(64, code.InputHash.Length);
    }

    [Fact]
    public void Changing_the_primary_changes_the_plan_hash()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        var primary = "a";
        kind.Body = (c, _) => c.Plan(primary,
            [c.Effect("a", new EffectBlock { Heal = 1 }), c.Effect("b", new EffectBlock { Heal = 2 })]);
        var first = Compile(entry, new LanternBlock());
        primary = "b";
        var second = Compile(entry, new LanternBlock());
        Assert.NotEqual(first.InputHash, second.InputHash);
    }

    [Fact]
    public void Retirement_during_a_callback_discards_its_result()
    {
        KindKit.Fresh();
        var (entry, kind, provider) = KindKit.Lantern();
        kind.Body = (c, b) =>
        {
            var plan = LanternKind.Default(c, b);
            KindRegistry.Dispose(entry, provider);
            return plan;
        };
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock()));
    }

    [Fact]
    public void The_provider_compiles_a_copy_and_nothing_it_or_the_author_keeps_changes_the_plan()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        List<BlockProduct>? kept = null;
        ItemBlock? made = null;
        kind.Body = (c, b) =>
        {
            made = LanternKind.Torch(b);
            kept = [c.Item("light", made), c.Effect("glow", new EffectBlock { Heal = 1 })];
            return c.Plan("light", kept);
        };
        var author = new LanternBlock { Name = "Camp", Seconds = 600, Tags = ["a"] };
        var plan = Compile(entry, author);
        var (hash, json) = (plan.InputHash, plan.Products.Select(p => p.CanonicalJson).ToArray());
        Assert.NotSame(author, kind.LastInput);          // the callback's input is not the author's object
        Assert.NotSame(author, plan.Projection);
        Assert.NotSame(kind.LastInput, plan.Projection);

        author.Tags.Add("b");                              // the author's record
        ((LanternBlock)plan.Projection!).Tags.Add("c");    // the handle's projection
        kept!.Clear();                                     // the provider's list
        made!.Recipe!.Needs["Material_T1_Twigs"] = 99;     // the provider's block
        Assert.Equal(2, plan.Products.Count);
        Assert.Equal(json, plan.Products.Select(p => p.CanonicalJson));
        Assert.Equal(hash, plan.InputHash);
        Assert.DoesNotContain("99", plan.Products.First(p => p.Key == "light").CanonicalJson);
        // the list a caller gets is a copy too
        Assert.NotSame(plan.Products, plan.Products);
        Assert.Throws<NotSupportedException>(() => ((IList<BlockProduct>)plan.Products)[0] = null!);
    }

    [Fact]
    public void A_plan_has_one_primary_and_at_most_32_effects()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        BlockPlan With(int effects, BlockCompileContext c, LanternBlock b)
        {
            var list = new List<BlockProduct> { c.Item("light", LanternKind.Torch(b)) };
            for (var i = 0; i < effects; i++) list.Add(c.Effect($"e{i}", new EffectBlock { Heal = i + 1 }));
            return c.Plan("light", list);
        }
        kind.Body = (c, b) => With(32, c, b);
        var plan = Compile(entry, new LanternBlock { Name = "Camp" });
        Assert.Equal(33, plan.Products.Count);
        Assert.Equal(plan.Products.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal), plan.Products.Select(p => p.Key));   // canonical key order
        kind.Body = (c, b) => With(33, c, b);
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));

        kind.Body = (c, b) => c.Plan("light", [c.Item("light", LanternKind.Torch(b)), c.Status("s", new StatusBlock { Name = "S", Seconds = 3 })]);
        Assert.Contains("every other product is an effect", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" })).Message);
        kind.Body = (c, b) => c.Plan("missing", [c.Item("light", LanternKind.Torch(b))]);
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        kind.Body = (c, b) => c.Plan("light", []);
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        kind.Body = (c, b) => { c.Effect("e", new EffectBlock { Heal = 1 }); return c.Plan("e", [c.Effect("e", new EffectBlock { Heal = 2 })]); };
        Assert.Contains("already", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" })).Message);
        // each of the five can be the primary
        kind.Body = (c, b) => c.Plan("p", [c.Weapon("p", new WeaponBlock { Name = "W", Class = WeaponClass.Sword })]);
        Assert.Equal(BuiltInBlockProductKind.Weapon, Compile(entry, new LanternBlock()).Products[0].Kind);
        kind.Body = (c, b) => c.Plan("p", [c.Spell("p", new SpellBlock { Name = "S", Cast = new CastBlock { ManaCost = 1 }, Delivery = new SelfBlock { Effects = [new EffectBlock { Heal = 1 }] } })]);
        Assert.Equal(BuiltInBlockProductKind.Spell, Compile(entry, new LanternBlock()).Products[0].Kind);
        kind.Body = (c, b) => c.Plan("p", [c.Status("p", new StatusBlock { Name = "S", Seconds = 3 })]);
        Assert.Equal(BuiltInBlockProductKind.Status, Compile(entry, new LanternBlock()).Products[0].Kind);
    }

    [Fact]
    public void Code_and_a_block_of_another_kind_cannot_hide_inside_a_product()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        kind.Body = (c, b) => c.Plan("light", [c.Item("light", LanternKind.Torch(b) with { CanEat = new EatRule { If = _ => true } })]);
        var code = Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        Assert.Equal("light.canEat.if", code.Field);
        Assert.Contains("delegate", code.Message);

        // a screen effect is a kind with a product of its own (kiln.vfx's), and an effect can hold one inline
        kind.Body = (c, b) => c.Plan("e", [c.Effect("e", new EffectBlock { Heal = 1, Screen = new ScreenEffectBlock() })]);
        var screen = Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        Assert.StartsWith("e.screen", screen.Field);
        Assert.Contains("inline ScreenEffectBlock", screen.Message);
        // by name it is a block that is defined already, not a product
        kind.Body = (c, b) => c.Plan("e", [c.Effect("e", new EffectBlock { Heal = 1, Screen = "flash" })]);
        Assert.Single(Compile(entry, new LanternBlock { Name = "Camp" }).Products);
        // an effect or a status inline is what the five are made of
        kind.Body = (c, b) => c.Plan("e", [c.Effect("e", new EffectBlock { Applies = new StatusBlock { Name = "Chilled", Seconds = 4 }, Then = [new EffectBlock { Heal = 2 }] })]);
        Assert.Single(Compile(entry, new LanternBlock { Name = "Camp" }).Products);
    }

    /// <summary>Every block type an admitted product can hold inline, by the model's property types: each is one of the five,
    /// a listed part, no kind of its own, or refused. A package that adds an inline block field to the five lands here.</summary>
    [Fact]
    public void Every_inline_block_type_reachable_from_the_five_is_classified()
    {
        var reach = new HashSet<Type>();
        void Visit(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t.IsArray) { Visit(t.GetElementType()!); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Visit(a);
            if (t.Assembly != typeof(IBlock).Assembly || t.IsEnum || !reach.Add(t)) return;
            foreach (var d in t.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>()) Visit(d.DerivedType);
            foreach (var i in t.GetInterfaces().Where(i => i.IsGenericType)) foreach (var a in i.GetGenericArguments()) Visit(a);
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Visit(p.PropertyType);
        }
        foreach (var t in KindCompiler.Admitted) Visit(t);
        var kinds = BlockJson.KindTypes().ToHashSet();
        var inlineKinds = reach.Where(t => typeof(IBlock).IsAssignableFrom(t) && kinds.Contains(t) && !KindCompiler.Admitted.Contains(t)).ToList();
        var allowed = inlineKinds.Where(KindCompiler.InlineAllowed).Select(t => t.Name).Order().ToArray();
        var refused = inlineKinds.Where(t => !KindCompiler.InlineAllowed(t)).Select(t => t.Name).Order().ToArray();
        Assert.Equal(KindCompiler.InlineParts.Select(t => t.Name).Order(), allowed);
        Assert.Equal(ExpectedRefused, refused);
    }

    // Kinds with products of their own that the five can hold inline today: refused inside a plan (chapter 16d section 5).
    private static readonly string[] ExpectedRefused = ["ScreenEffectBlock"];

    [Fact]
    public void The_context_dies_with_the_call_and_serves_only_its_own_compile()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        var first = Compile(entry, new LanternBlock { Name = "Camp" });
        var old = kind.LastContext!;
        Assert.Throws<ArgumentException>(() => old.Item("x", new ItemBlock { Name = "X" }));
        Assert.Throws<ArgumentException>(() => old.Plan("light", first.Products));
        Assert.Throws<ArgumentException>(() => old.GuidOf("item", "x"));
        Assert.Throws<ArgumentException>(() => old.ProductName("x"));
        Assert.Throws<ArgumentException>(() => old.Resolve(new BlockRef<ItemBlock>("x")));

        // the plan of an earlier compile, returned again
        kind.Body = (_, _) => first;
        Assert.Throws<ArgumentException>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        // a product of an earlier compile, in this one's plan
        kind.Body = (c, _) => c.Plan("light", [first.Products[0]]);
        Assert.Throws<ArgumentException>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        kind.Body = (_, _) => null!;
        Assert.Throws<ArgumentException>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        kind.Body = (c, b) => { LanternKind.Default(c, b); return LanternKind.Default(c, b); };
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
    }

    [Fact]
    public void A_compiler_cannot_come_back_into_the_kinds_API()
    {
        KindKit.Fresh();
        var (entry, kind, provider) = KindKit.Lantern();
        kind.Body = (c, b) => { KindCompiler.Compile(Consumer, entry, "inner", new LanternBlock { Name = "Inner" }); return LanternKind.Default(c, b); };
        Assert.Contains("inside a block kind's Compile", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" })).Message);
        kind.Body = (c, b) => { KindRegistry.Register(provider, KindKit.Descriptor("provider:other", "provider:others"), new OtherKind()); return LanternKind.Default(c, b); };
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        Assert.Null(KindRegistry.FindId("provider:other"));
        // and the guard is gone after a compile that threw
        kind.Body = null;
        Assert.NotNull(Compile(entry, new LanternBlock { Name = "Camp" }));
    }

    [Fact]
    public void A_retired_kind_compiles_nothing_and_a_wrong_record_or_name_throws()
    {
        KindKit.Fresh();
        var (entry, kind, provider) = KindKit.Lantern();
        Assert.Throws<ArgumentException>(() => KindCompiler.Compile(Consumer, entry, "camp", new OtherBlock()));
        foreach (var bad in new[] { "", "a b", "a:b", new string('x', 65), "é" })
            Assert.Equal("name", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }, bad)).Field);
        Assert.NotNull(Compile(entry, new LanternBlock { Name = "Camp" }, new string('x', 64)));
        var calls = kind.Calls;
        KindRegistry.Dispose(entry, provider);
        Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock { Name = "Camp" }));
        Assert.Equal(calls, kind.Calls);
    }

    [Fact]
    public void Ids_and_names_are_derived_and_checked()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        Guid guid = default;
        string product = "";
        kind.Body = (c, b) =>
        {
            guid = c.GuidOf("item", "light");
            product = c.ProductName("glow");
            Assert.Throws<BlockError>(() => c.GuidOf("item", "a b"));
            Assert.Throws<BlockError>(() => c.GuidOf("", "a"));
            Assert.Throws<BlockError>(() => c.ProductName(new string('k', 33)));
            Assert.Throws<BlockError>(() => c.Item("a/b", LanternKind.Torch(b)));
            return LanternKind.Default(c, b);
        };
        Compile(entry, new LanternBlock { Name = "Camp" });
        Assert.Equal(BlockIds.Derive("consumer", "item", "kind-camp.light"), guid);
        Assert.Equal("consumer:kind-camp.glow", product);
    }

    [Fact]
    public void Resolve_gives_the_identity_of_a_declared_block_and_never_the_block()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        var consumer = KindKit.ModFor("consumer");
        var other = KindKit.ModFor("kinds-other");
        var status = new BlockHandle<StatusBlock>("consumer", "chilled", BlockIds.Derive("consumer", "buff", "chilled"), "consumer.chilled", new StatusBlock { Name = "Chilled", Seconds = 3 });
        var item = new BlockHandle<ItemBlock>("kinds-other", "gem", BlockIds.Derive("kinds-other", "item", "gem"), "kinds-other.gem", new ItemBlock { Name = "Gem" });
        consumer.Handles["chilled"] = status;
        other.Handles["gem"] = item;
        try
        {
            ResolvedBlock<StatusBlock>? own = null;
            ResolvedBlock<ItemBlock>? foreign = null;
            kind.Body = (c, b) =>
            {
                own = c.Resolve(new BlockRef<StatusBlock>("chilled"));
                foreign = c.Resolve(new BlockRef<ItemBlock>("kinds-other:gem"));
                Assert.Equal("chilled", Assert.Throws<BlockError>(() => c.Resolve(new BlockRef<ItemBlock>("chilled"))).Field);   // wrong kind
                Assert.Throws<BlockError>(() => c.Resolve(new BlockRef<ItemBlock>("nothing")));
                Assert.Throws<BlockError>(() => c.Resolve(new BlockRef<ItemBlock>("gem")));                                       // another mod's, without its id
                Assert.Throws<BlockError>(() => c.Resolve(new BlockRef<ItemBlock>(":gem")));
                Assert.Throws<BlockError>(() => c.Resolve(default(BlockRef<ItemBlock>)));
                // declared after the compile began: not in this compile's view
                consumer.Handles["late"] = status;
                Assert.Throws<BlockError>(() => c.Resolve(new BlockRef<StatusBlock>("late")));
                return LanternKind.Default(c, b);
            };
            Compile(entry, new LanternBlock { Name = "Camp" });
            Assert.Equal(("consumer", "chilled", status.Guid, status.Id, "consumer.chilled", "kiln:status"), (own!.ModId, own.Name, own.Guid, own.Id, own.GameName, own.KindId));
            Assert.Equal(("kinds-other", "gem", item.Guid, "kiln:item"), (foreign!.ModId, foreign.Name, foreign.Guid, foreign.KindId));
        }
        finally
        {
            consumer.Handles.Clear();
            other.Handles.Clear();
        }
    }

    // ---- bounds (chapter 16d section 5) -----------------------------------------------------------------------------

    private static EffectBlock Nested(int levels) => levels == 0 ? new EffectBlock { Heal = 1 } : new EffectBlock { Then = [Nested(levels - 1)] };

    private static int DepthOf(IBlock b) => KindJson.Measure(JsonSerializer.SerializeToNode(b, b.GetType(), BlockJson.SharedOptions)).Depth;

    [Fact]
    public void A_product_nests_at_most_32_deep()
    {
        var fits = Enumerable.Range(0, 20).TakeWhile(n => DepthOf(Nested(n)) <= KindCompiler.MaxDepth).Last();
        Assert.True(DepthOf(Nested(fits + 1)) > KindCompiler.MaxDepth);
        Assert.True(fits > 4, "the measure counts objects and lists, not characters");
        KindCompiler.Freeze("t", "e", Nested(fits));
        Assert.Contains("deep", Assert.Throws<BlockError>(() => KindCompiler.Freeze("t", "e", Nested(fits + 1))).Message);
        Assert.Equal((3, 4), KindJson.Measure(JsonNode.Parse("""{"a":[{"b":1},[]],"c":2}""")));
    }

    private static EffectBlock Wide(int n) => new() { Then = [.. Enumerable.Range(0, n).Select(i => (EffectRef)new EffectBlock { Heal = i + 1 })] };

    [Fact]
    public void A_plan_has_at_most_4096_objects_and_lists()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        var one = KindCompiler.Freeze("t", "e", Wide(200)).Nodes;
        Assert.True(one is > 200 and < KindCompiler.MaxNodes);
        var fit = KindCompiler.MaxNodes / one;
        Assert.True(fit < KindCompiler.MaxProducts - 1, "the test needs the node bound to bite before the product count");
        BlockPlan With(int count, BlockCompileContext c)
        {
            var list = new List<BlockProduct>();
            for (var i = 0; i < count; i++) list.Add(c.Effect($"e{i}", Wide(200)));
            return c.Plan("e0", list);
        }
        kind.Body = (c, _) => With(fit, c);
        Assert.Equal(fit, Compile(entry, new LanternBlock()).Products.Count);
        kind.Body = (c, _) => With(fit + 1, c);
        Assert.Contains("objects and lists", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock())).Message);
        Assert.Contains("objects and lists", Assert.Throws<BlockError>(() => KindCompiler.Freeze("t", "e", Wide(KindCompiler.MaxNodes))).Message);
    }

    private static StatusBlock Sized(int bytes)
    {
        var empty = KindCompiler.Freeze("t", "s", new StatusBlock { Name = "S", Seconds = 1, Description = "" }).Bytes;
        return new StatusBlock { Name = "S", Seconds = 1, Description = new string('x', bytes - empty) };
    }

    [Fact]
    public void A_product_is_at_most_64_KiB_and_a_plan_1_MiB()
    {
        KindKit.Fresh();
        var (entry, kind, _) = KindKit.Lantern();
        Assert.Equal(KindCompiler.MaxProductBytes, KindCompiler.Freeze("t", "s", Sized(KindCompiler.MaxProductBytes)).Bytes);
        Assert.Contains("KiB", Assert.Throws<BlockError>(() => KindCompiler.Freeze("t", "s", Sized(KindCompiler.MaxProductBytes + 1))).Message);

        // 16 effects of 64 KiB are 1 MiB exactly; one byte more is refused
        EffectBlock Big(int bytes)
        {
            var empty = KindCompiler.Freeze("t", "e", new EffectBlock { Applies = new StatusBlock { Name = "S", Seconds = 1, Description = "" } }).Bytes;
            return new EffectBlock { Applies = new StatusBlock { Name = "S", Seconds = 1, Description = new string('x', bytes - empty) } };
        }
        BlockPlan With(BlockCompileContext c, int last)
        {
            var list = new List<BlockProduct>();
            for (var i = 0; i < 15; i++) list.Add(c.Effect($"e{i}", Big(KindCompiler.MaxProductBytes)));
            list.Add(c.Effect("last", Big(last)));
            list.Add(c.Effect("tail", Big(1000)));
            return c.Plan("e0", list);
        }
        kind.Body = (c, _) => With(c, KindCompiler.MaxProductBytes - 1000);
        Assert.Equal(KindCompiler.MaxPlanBytes, Compile(entry, new LanternBlock()).Products.Sum(p => System.Text.Encoding.UTF8.GetByteCount(p.CanonicalJson)));
        kind.Body = (c, _) => With(c, KindCompiler.MaxProductBytes - 999);
        Assert.Contains("MiB", Assert.Throws<BlockError>(() => Compile(entry, new LanternBlock())).Message);
    }
}
