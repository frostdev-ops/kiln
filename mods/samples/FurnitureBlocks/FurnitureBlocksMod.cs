// SPDX-License-Identifier: MIT
using Kiln;
using Kiln.GameIds;

namespace FurnitureBlocks;

/// <summary>
/// Furniture that does what the game's does, and more. Each starts from a game item of its kind: a chest, a door, a bed, a chair. What it takes, what the player plays and what
/// happens to them are the block's. blocks/twins.json says the same in data.
/// </summary>
public sealed class FurnitureBlocksMod : Mod
{
    public override void OnLoad()
    {
        // The game's own lock: without a piece of scrap metal in the bag, it stays shut, and the player's game shows it locked.
        Blocks.Define("lockbox", new ContainerBlock
        {
            Name = "Lockbox", Description = "Opens for whoever carries scrap.",
            Slots = 8,
            OnInteract = new InteractBlock { Requires = new RequiresBlock { Item = GameItems.Material_T1_Metal_Scraps } },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 4, [GameItems.Material_T1_Metal_Scraps] = 1 } },
        });

        // Only at night: the game's daytime lock.
        Blocks.Define("night-door", new DoorBlock
        {
            Name = "Night Door",
            OnInteract = new InteractBlock { Requires = new RequiresBlock { Time = DayPart.Night } },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 6 } },
        });

        // The game's sleep and rest. Kiln adds the rest: where a player comes back after dying, and the morning when everyone sleeps.
        Blocks.Define("camp-bed", new BedBlock
        {
            Name = "Camp Bed",
            Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Bed_Cot },
            Comfort = new ComfortBlock { Amount = 2, Category = ComfortCategory.Bed },
            SpawnPoint = true,
            SkipsNight = true,
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Plant_Fibre] = 10, [GameItems.Material_T1_Wood_Logs] = 2 } },
        });

        // A seat that gives back stamina, once the game has sat the player down.
        Blocks.Define("milking-stool", new SeatBlock
        {
            Name = "Milking Stool",
            Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Chair_Stool },
            OnInteract = new InteractBlock { Effects = [new EffectBlock { Restores = Restore.Stamina(20) }] },
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 2 } },
        });

        // An interaction is a part: write it once, use it on several things. Kiln checks the player's state on the server and declines the way the game does.
        var pray = Blocks.Define("pray", new InteractBlock
        {
            Prompt = "Pray",
            Requires = new RequiresBlock { Unless = ["InCombat"] },
            Effects = [new EffectBlock { Heal = 25 }],
        });
        Blocks.Define("prayer-bench", new SeatBlock
        {
            Name = "Prayer Bench",
            Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Bench },
            OnInteract = pray,
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 3 } },
        });

        // A container that holds something when it is placed. Kiln rolls the table and fills it once: not again after a restart, nor when it is placed again on the same spot.
        Blocks.Define("scrap-pile", new LootTableBlock
        {
            Entries = [new LootEntry { Item = GameItems.Material_T1_Metal_Scraps, CountMin = 3 }, new LootEntry { Item = GameItems.Material_T1_Twigs, CountMin = 2 }],
        });
        Blocks.Define("supply-cache", new ContainerBlock { Name = "Supply Cache", Description = "Packed before you came.", DefaultLoot = "scrap-pile" });

        // A barrel has no lid to lift: the container still opens its screen, and nothing moves.
        Blocks.Define("barrel", new ContainerBlock { Name = "Barrel", Lid = new LidBlock { Moves = false } });

        // A door that swings one way and can be used from one side: the other side is a wall.
        Blocks.Define("one-way-door", new DoorBlock { Name = "One Way Door", Opens = DoorSide.Front, Sides = DoorSide.Front });

        // A table is its top and its legs: three boxes, one shape. (A model of yours has a box around it by itself; say them when one box is wrong.)
        Blocks.Define("trestle", new PropBlock
        {
            Name = "Trestle Table",
            Visual = new VisualBlock { Model = "item:" + GameItems.Prop_Decoration_Comfort1_T0_Table_Medium },
            Colliders =
            [
                new BoxCollider { Size = new(2, 0.15f, 1), Center = new(0, 0.9f, 0) },
                new BoxCollider { Size = new(0.15f, 0.85f, 0.8f), Center = new(-0.85f, 0.425f, 0) },
                new BoxCollider { Size = new(0.15f, 0.85f, 0.8f), Center = new(0.85f, 0.425f, 0) },
            ],
        });

        // A prop a recipe can ask for nearby. The game keeps 13 free places for such props, for every mod together.
        var runeTable = Blocks.Define("rune-table", new PropBlock
        {
            Name = "Rune Table", Visual = new VisualBlock { Like = GameItems.Prop_Decoration_Comfort1_T0_Table_Medium },
            Provides = true,
            Recipe = new RecipeBlock { Needs = { [GameItems.Material_T1_Wood_Logs] = 4 } },
        });
        // ... and a workbench whose recipe asks for it: the player can craft only with a rune table near.
        Blocks.Define("rune-bench", new StationBlock
        {
            Name = "Rune Workbench", Kind = StationKind.Workbench,
            Recipes = [new RecipeBlock { Needs = { [GameItems.Material_T1_Twigs] = 2 }, Output = GameItems.Material_T1_Wood_Logs, NeedsProps = [runeTable] }],
        });

        // A shape of your own for every building material: a 4 m wall with an arched window. Half a metre a cell; layers from the bottom up.
        string[] Row(string cells) => [cells];
        Blocks.Define("arched-window", new BlueprintBlock
        {
            Name = "Arched Window Wall",
            Cells =
            [
                Row("########"), Row("########"), Row("##....##"), Row("##....##"),
                Row("##....##"), Row("###..###"), Row("########"), Row("########"),
            ],
        });

        // Hair and beards are worn looks. These take a game style's look; a model of your own is a WornLook with a file, as a cosmetic's.
        Blocks.Define("short-crop", new HairBlock { Name = "Short Crop", Look = new WornLook { Like = "VC_Hair_04_02_frenchcrop" } });
        Blocks.Define("trim", new BeardBlock { Name = "Trim", Look = new WornLook { Like = "VC_Beard12_stubble" } });
    }
}
