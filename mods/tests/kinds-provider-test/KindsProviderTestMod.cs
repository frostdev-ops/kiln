// SPDX-License-Identifier: MIT
// Lab test of block kinds, the provider (chapter 16d §6). Public SDK only.
using Kiln;

namespace KindsProviderTest;

public sealed class KindsProviderTestMod : Mod
{
    private BlockKindRegistration? _lantern, _needy;

    private sealed class LanternKind : IBlockKind<LanternBlock>
    {
        public BlockPlan Compile(BlockCompileContext context, LanternBlock b)
        {
            LanternProbe.Count();
            if (string.IsNullOrWhiteSpace(b.Name)) throw new BlockError($"lantern {context.Name}", "name", "a lantern needs a name");
            var recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = b.Twigs } };
            foreach (var (item, count) in b.Extras) recipe.Needs[item] = count;
            return context.Plan("light",
            [
                context.Item("light", new ItemBlock
                {
                    Name = b.Name,
                    Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = b.Seconds },
                    Recipe = recipe,
                }),
            ]);
        }
    }

    private sealed class NeedyKind : IBlockKind<NeedyBlock>
    {
        public BlockPlan Compile(BlockCompileContext context, NeedyBlock b)
        {
            LanternProbe.Count();
            return context.Plan("thing", [context.Item("thing", new ItemBlock { Name = b.Name })]);
        }
    }

    private const string Schema = """{ "type": "object" }""";

    private void Register()
    {
        var a = Blocks.RegisterKind(new BlockKindDescriptor
        {
            Id = "kinds-provider-test:lantern", JsonSection = "kinds-provider-test:lanterns", SchemaJson = Schema, Clients = ClientsLevel.Kiln,
        }, new LanternKind());
        var b = Blocks.RegisterKind(new BlockKindDescriptor
        {
            Id = "kinds-provider-test:needy", JsonSection = "kinds-provider-test:needies", SchemaJson = Schema, Clients = ClientsLevel.Kiln,
            Requires = ["kinds-provider-test.never"],
        }, new NeedyKind());
        (_lantern, _needy) = (a.Registration, b.Registration);
        Log.Info($"kinds-provider-test: INFO kinds registered: lantern {a.Ok} {a.Reason}, needy {b.Ok} {b.Reason}");
    }

    public override void OnLoad()
    {
        try
        {
            Register();
        }
        catch (KilnUnavailableException e)
        {
            Log.Warn($"kinds-provider-test: INFO block kinds are off: {e.Message}");
            return;
        }
        Context.Messages.On("retire", _ =>
        {
            _lantern?.Dispose();
            _needy?.Dispose();
            (_lantern, _needy) = (null, null);
            Log.Info("kinds-provider-test: INFO kinds retired");
        });
        Context.Messages.On("restore", _ => Register());
        // another mod's plan: applying it here must be an argument error, never a game write
        Context.Messages.On("apply-foreign", m =>
        {
            var said = "";
            try { said = $"no exception, state {Blocks.Apply((BlockPlan)m.Payload!).State}"; }
            catch (ArgumentException e) { said = "ArgumentException: " + e.Message; }
            catch (Exception e) { said = e.GetType().Name + ": " + e.Message; }
            var ok = said.StartsWith("ArgumentException", StringComparison.Ordinal);
            if (ok) Log.Info($"kinds-provider-test: PASS consumer-binding: the provider cannot apply {m.Sender}'s plan ({said})");
            else Log.Error($"kinds-provider-test: FAIL consumer-binding: the provider applied or mis-refused {m.Sender}'s plan ({said})");
            Mods.Send(m.Sender, "foreign-result", ok);
        });
    }
}
