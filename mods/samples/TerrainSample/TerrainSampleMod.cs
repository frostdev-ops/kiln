// SPDX-License-Identifier: MIT
using Kiln;

namespace TerrainSample;

/// <summary>
/// Two terrain materials. The names in Like and Look are the catalog's (GameMaterials has them as constants once `kiln catalog build` has run); an id the game's
/// items don't name answers to Cell&lt;id&gt;: copper ore is Cell26, celesthium ore Cell115. The game reads its material tables once at start, so a material
/// defined here is there from the next start; blocks/twins.json's are there at once.
/// </summary>
public sealed class TerrainSampleMod : Mod
{
    private TableBlockHandle<TerrainMaterialBlock>? _ore, _snow;

    public override void OnLoad()
    {
        // digs like copper ore (very hard, the metal class), looks like celesthium ore, and gives celesthium: one per 12 cells dug
        _ore = Blocks.Define("glacier-ore", new TerrainMaterialBlock
        {
            Name = "Glacier Ore",
            Like = "Cell26",
            Look = "Cell115",
            Strength = new StrengthBlock { Health = 12, Drops = "Material_T7_Celesthium_Ore", ItemsPerBlocks = 12 },
        });

        // snow that has been walked on until it's ice: placed with the game's terrain shapes, slippery, and digging it gives it back
        _snow = Blocks.Define("packed-snow", new TerrainMaterialBlock
        {
            Name = "Packed Snow",
            Description = "Snow trodden hard enough to slide on.",
            Like = "TerrainSnow",
            Look = "TerrainIce",
            Sliding = true,
            Placeable = true,
            Strength = new StrengthBlock { Hardness = Hardness.SlightlyHard, Health = 6 },
            Recipe = new RecipeBlock { Makes = 4, Needs = { ["Build_TerrainMaterial_T5_Snow"] = 8 } },
        });

        Log.Info($"terrain sample: glacier-ore is {_ore.Status} on cell {_ore.CellId}, packed-snow is {_snow.Status} on cell {_snow.CellId}");
    }

    [Command("glacier-vein", Description = "a vein of Glacier Ore in the ground 6 m in front of you", Permission = CommandPermission.Admin)]
    private Task Vein(CommandContext c) => Put(c, "glacier-ore-json", 1.5, true);

    [Command("snow-mound", Description = "a mound of Packed Snow 6 m in front of you", Permission = CommandPermission.Admin)]
    private Task Mound(CommandContext c) => Put(c, "packed-snow-json", 2, false);

    // The data file's twins: they're in the tables from the first start.
    private static async Task Put(CommandContext c, string block, double radius, bool inside)
    {
        // a refusal ends the command with an error (as c.Fail does), not a reply: the log line and the console's result then say it didn't happen
        if (c.Source.Player.IsNone || World.Position(c.Source.Player) is not { } at)
            throw new CommandException("only a player in the world can do this");
        if (Blocks.Find(block) is not TableBlockHandle<TerrainMaterialBlock> { CellId: > 0 } m)
            throw new CommandException($"{block} isn't in this game's tables yet: restart once");
        var center = new WorldPosition(at.X + 6, at.Y + (inside ? -radius : 0), at.Z);
        var shape = BuildShape.Sphere(center, radius);
        // a vein replaces the ground it's in; a mound is added on top
        var r = inside ? await Build.Paint(shape, (byte)m.CellId, new BuildOptions { Actor = c.Source.Player }) : await Build.Add(shape, (byte)m.CellId, new BuildOptions { Actor = c.Source.Player });
        if (r.Refused) c.Fail($"{block}: refused ({r.Refusal})");
        c.Reply($"{block}: cell {m.CellId}, {r.CellsChanged} cells");
    }
}
