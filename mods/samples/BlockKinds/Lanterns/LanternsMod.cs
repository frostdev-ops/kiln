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
        if (!Capabilities.Is("blocks.kinds"))
        {
            Log.Warn($"block kinds are off here: {Capabilities.Why("blocks.kinds")}");
            return;
        }
        var result = Blocks.RegisterKind(new BlockKindDescriptor
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
        if (result.Ok) Log.Info($"block kind {result.Registration!.Id} registered");
        else Log.Warn($"the lantern kind is not registered: {result.Reason}");
    }
}
