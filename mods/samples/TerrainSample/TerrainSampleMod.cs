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

    [Command("glacier-vein", Description = "a vein of Glacier Ore in the ground 6 m east of you, or at x y z (~ for yours)", Permission = CommandPermission.Admin)]
    private Task Vein(CommandContext c, WorldPosition at = default) => Put(c, at, "glacier-ore-json", 1.5, true);

    [Command("snow-mound", Description = "a mound of Packed Snow on the ground 6 m east of you, or at x y z (~ for yours)", Permission = CommandPermission.Admin)]
    private Task Mound(CommandContext c, WorldPosition at = default) => Put(c, at, "packed-snow-json", 2, false);

    // How far above and below the aimed point the ground is looked for, in metres: a slope, a ledge, the foot of a small cliff.
    private const double GroundAbove = 8, GroundBelow = 32;

    // The data file's twins: they're in the tables from the first start. A refusal, and an edit that changed no cell, end the command with an error (as
    // c.Fail does), not a reply: the log line and the console's result then say it didn't happen.
    private static async Task Put(CommandContext c, WorldPosition given, string block, double radius, bool inside)
    {
        WorldPosition aim;
        if (given != default)
            aim = given;
        else if (!c.Source.Player.IsNone && World.Position(c.Source.Player) is { } at)
            aim = new WorldPosition(at.X + 6, at.Y, at.Z);
        else
            throw new CommandException("only a player in the world can do this; a console gives a position: x y z");
        if (Blocks.Find(block) is not TableBlockHandle<TerrainMaterialBlock> { CellId: > 0 } m)
            throw new CommandException($"{block} isn't in this game's tables yet: restart once");
        // The ground there, not the height aimed at: on a slope or at a cliff's edge a shape at the aimed height is in the air, and painting air changes nothing.
        double? found;
        try { found = await GroundAt(aim); }
        catch (BuildException e) { throw new CommandException($"{block}: {e.Message}"); }
        if (found is not { } ground)
            throw new CommandException($"{block}: no ground under ({aim.X:F1}, {aim.Z:F1}) from {aim.Y + GroundAbove:F1} m down to {aim.Y - GroundBelow:F1} m");
        var options = c.Source.Player.IsNone ? BuildOptions.Default : new BuildOptions { Actor = c.Source.Player };
        // a vein replaces the ground it's in, its top showing at the surface; a mound is added on the ground
        var shape = BuildShape.Sphere(new WorldPosition(aim.X, inside ? ground - radius * 0.5 : ground, aim.Z), radius);
        var r = inside ? await Build.Paint(shape, (byte)m.CellId, options) : await Build.Add(shape, (byte)m.CellId, options);
        if (r.Refused) c.Fail($"{block}: refused ({r.Refusal})");
        if (r.CellsChanged == 0)
            c.Fail($"{block}: no cell changed at ({aim.X:F1}, {ground:F1}, {aim.Z:F1})" + (inside ? ": the ground there is Glacier Ore already" : ""));
        c.Reply($"{block}: cell {m.CellId}, {r.CellsChanged} cells at ({aim.X:F1}, {ground:F1}, {aim.Z:F1})");
    }

    // The top of the ground in the column under a point: the highest cell at least half full, from GroundAbove above it to GroundBelow below. Null: air all the way.
    private static async Task<double?> GroundAt(WorldPosition p)
    {
        var column = Build.BoxBetween(new WorldPosition(p.X, p.Y - GroundBelow, p.Z), new WorldPosition(p.X, p.Y + GroundAbove, p.Z));
        var cells = await Build.ReadAsync(column);   // one cell wide: index = height in the column
        for (var y = column.SizeY - 1; y >= 0; y--)
            if (cells[y].Material != 0 && cells[y].Fill >= 128)
                return Build.CornerOf(new CellPos(column.X, column.Y + y, column.Z)).Y + Build.Grid!.CellSize;
        return null;
    }
}
