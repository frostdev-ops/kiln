using Kiln;

namespace KilnWorldBlocks1;

/// <summary>Things to place, from building blocks: a prop, a station that works on its own, a container and a door.
/// blocks/world.json makes the same kinds as data. A game item's look brings its model, its collision and how it places.</summary>
public sealed class KilnWorldBlocks1Mod : Mod
{
    public override void OnLoad()
    {
        // a crafting prop: the manual bench below needs it nearby. C# crafting props enter the game's tables on the second start;
        // the JSON pine-banner is there on the first. A crafting prop needs the mod on clients.
        var table = Blocks.Define("oak-table", new PropBlock
        {
            Name = "Oak Table",
            Visual = new VisualBlock { Like = "Prop_Decoration_Comfort1_T0_Table_Medium" },
            Comfort = new ComfortBlock { Amount = 3, Category = ComfortCategory.Table },
            Provides = true,
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 4 } },
        });

        // a station that works on its own, like the game's drying rack: a cycle takes 4 s, the inputs go when it starts
        Blocks.Define("twig-press", new StationBlock
        {
            Name = "Twig Press", Description = "Presses twigs into logs while you are away.",
            Placement = new PlacementBlock { On = Surface.Floor, InBase = true },
            Processing = new ProcessingBlock { Slots = 8 },
            Recipe = new RecipeBlock { Station = StationKind.Workbench.Workshop, Needs = { ["Material_T1_Wood_Logs"] = 6 } },
            Recipes = [new RecipeBlock { Seconds = 4, Needs = { ["Material_T1_Twigs"] = 3 }, Output = "Material_T1_Wood_Logs" }],
        });

        // nearby crafting props are checked for a player's craft at a manual station, not an automated factory
        Blocks.Define("oak-bench", new StationBlock
        {
            Name = "Oak Workbench", Kind = StationKind.Workbench,
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 6 } },
            Recipes = [new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 3 }, Output = "Material_T1_Resin", NeedsProps = [table] }],
        });

        // a container: 8 slots, and it opens only for whoever carries scrap metal
        Blocks.Define("scrap-box", new ContainerBlock
        {
            Name = "Scrap Box", Description = "Opens for whoever carries scrap.",
            Slots = 8,
            // One fill per placed location, including across restarts. Choose cheap contents for a craftable container.
            DefaultLoot = new LootTableBlock { Entries = [new LootEntry { Item = "Material_T1_Twigs", CountMin = 1, CountMax = 2 }] },
            Lid = new LidBlock { Moves = true },
            OnInteract = new InteractBlock { Requires = new RequiresBlock { Item = "Material_T1_Metal_Scraps" } },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 4, ["Material_T1_Metal_Scraps"] = 1 } },
        });

        // a door that opens only at night
        Blocks.Define("night-gate", new DoorBlock
        {
            Name = "Night Gate",
            Opens = DoorSide.Front, Sides = DoorSide.Both,
            OnInteract = new InteractBlock { Requires = new RequiresBlock { Time = DayPart.Night } },
            Recipe = new RecipeBlock { Needs = { ["Material_T1_Wood_Logs"] = 6 } },
        });
    }
}
