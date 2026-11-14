// SPDX-License-Identifier: MIT
// The provider of a block kind: a record of its own (Lanterns.Api) and a pure compiler from it to Kiln's blocks.
using Kiln;

namespace Lanterns;

public sealed class LanternsMod : Mod
{
    /// <summary>What a lantern becomes: one item, a torch with a recipe. Pure: it reads the block and asks the context.</summary>
    private sealed class LanternKind : IBlockKind<LanternBlock>
    {
        public BlockPlan Compile(BlockCompileContext context, LanternBlock b)
        {
            if (string.IsNullOrWhiteSpace(b.Name)) throw new BlockError($"lantern {context.Name}", "name", "a lantern needs a name players can read");
            if (b.Twigs is < 1 or > 99) throw new BlockError($"lantern {context.Name}", "twigs", $"{b.Twigs} is outside 1..99");
            return context.Plan("light",
            [
                context.Item("light", new ItemBlock
                {
                    Name = b.Name,
                    Tool = new ToolBlock { Harvests = ToolClass.Torch, Durability = b.Seconds },
                    Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = b.Twigs } },
                }),
            ]);
        }
    }

    public override void OnLoad()
    {
        // Kiln.Blocks reports its capability rows at its first use, so the answer is read from the call itself: where block
        // kinds are off, RegisterKind throws KilnUnavailableException naming the row (blocks.kinds).
        BlockKindResult result;
        try
        {
            result = Register();
        }
        catch (KilnUnavailableException e)
        {
            Log.Warn($"block kinds are off here: {e.Message}");
            return;
        }
        if (result.Ok) Log.Info($"block kind {result.Registration!.Id} registered");
        else Log.Warn($"the lantern kind is not registered: {result.Reason}");
    }

    private static BlockKindResult Register() =>
        Blocks.RegisterKind(new BlockKindDescriptor
        {
            Id = "lanterns:lantern",
            JsonSection = "lanterns:lanterns",
            SchemaJson = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "minLength": 1 },
                    "seconds": { "type": "number", "exclusiveMinimum": 0 },
                    "twigs": { "type": "integer", "minimum": 1, "maximum": 99 }
                  },
                  "required": ["name"],
                  "additionalProperties": false
                }
                """,
            Clients = ClientsLevel.Kiln,   // a new item: players need Kiln, which sends them the definitions
        }, new LanternKind());
}
