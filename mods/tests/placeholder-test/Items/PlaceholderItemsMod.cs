// SPDX-License-Identifier: MIT
using Kiln;

namespace PlaceholderItems;

/// <summary>placeholder-test's removable mod: a gear item, a stackable with a raised max stack, a pack item, a recipe.</summary>
public sealed class PlaceholderItemsMod : Mod
{
    public override void OnLoad()
    {
        if (!NewContent.Available) return;
        Content.Items.Register(new ItemDefinition
        {
            Guid = Guid.Parse("2f7c9e14-5a3b-4d68-b0e1-6c4a8f2d9b37"),
            Name = "Kiln_PH_Sword",
            BaseOn = "Weapon_T1_1H_SwordEpic_01",
            DisplayName = "Placeholder Test Sword",
            Model = "item:Weapon_T4_1H_SwordEpic_02",
            // the test gives it at this level: a new item has one level unless it opts in to rolled loot
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.3, ["itemLevelRange.minLevel"] = 7 },
        });
        Content.Items.Register(new ItemDefinition
        {
            Guid = Guid.Parse("8e4d1b6a-0f93-4c27-9d5e-3a7b2c1f8e60"),
            Name = "Kiln_PH_Ore",
            BaseOn = "Material_T1_Metal_Scraps",
            DisplayName = "Placeholder Test Ore",
            Fields = new Dictionary<string, object> { ["maxStackSize"] = 500 },
        });
        Content.Recipes.Register(new RecipeDefinition
        {
            Guid = Guid.Parse("5c1e8a3f-9b2d-4f70-a6e4-0d8b7c3a1f92"),
            Name = "Kiln_PH_Ore_Recipe",
            Station = "hand",
            Inputs = [("Material_T1_Twigs", 1)],
            Outputs = [("Kiln_PH_Ore", 2)],
        });
    }
}
