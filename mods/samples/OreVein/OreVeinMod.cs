// SPDX-License-Identifier: MIT
using Kiln;

namespace OreVein;

/// <summary>
/// Kiln.Terrain sample. <c>/orevein</c> puts a small mound next to the player, with an ore vein inside it, as two
/// terrain overlays: the mound is a <see cref="TerrainHeightfield"/> made in C#, the vein a voxel volume read from
/// <c>vein.json</c> (the same shape as the C# record). Both are kept: the world remembers them (per spot), a second
/// <c>/orevein</c> at the same spot changes nothing, and the cells are the game's own materials, so players without
/// mods see and mine them. <c>/orevein list</c> shows what this world has.
/// </summary>
public sealed class OreVeinMod : Mod
{
    private const byte Stone = 1, Ore = 12;   // material numbers of the game (a cell's low byte, see Kiln.Build's Voxel)

    [Command("orevein", Description = "a small mound with an ore vein next to you (\"list\": what this world has)", Permission = CommandPermission.Admin)]
    private async Task Command(CommandContext c)
    {
        if (!Capabilities.Is("terrain.overlays")) throw new CommandException(Capabilities.Why("terrain.overlays"));
        if (c.Args.Count > 0 && c.Args[0] == "list")
        {
            var plans = Terrain.Applied;
            c.Reply(plans.Count == 0 ? "No overlay of this mod in this world." : string.Join("; ", plans.Select(p => $"{p.Plan} v{p.Version} at {p.Region}")));
            return;
        }
        if (!c.Source.IsPlayer) throw new CommandException("stand where the mound should be");
        var at = Players.PositionOf(c.Source.Player) ?? throw new CommandException("your position is unknown");

        // 4 m in front of the player along x, on the 8-cell grid so that two calls at one spot name the same plan
        var cell = Build.CellOf(new WorldPosition(at.X + 4, at.Y, at.Z));
        var corner = new CellPos(cell.X / 8 * 8, cell.Y - 1, cell.Z / 8 * 8);
        var spot = $"{corner.X}.{corner.Z}";

        // The mound: 16 x 16 columns, up to 6 cells (3 m) high, added on top of the ground that is there.
        var heights = new float[16 * 16];
        for (var z = 0; z < 16; z++)
        for (var x = 0; x < 16; x++)
            heights[z * 16 + x] = MathF.Max(0, 6f - MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (z - 7.5f) * (z - 7.5f)));
        var mound = new TerrainPatch($"mound-{spot}", 1, new CellBox(corner.X, corner.Y, corner.Z, 16, 6, 16),
            new TerrainHeightfield(heights, Fill: 1, Mode: TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(Stone)]);

        // The vein: the JSON twin, moved to the middle of the mound. Its 0 cells stay stone.
        var json = TerrainPatch.FromJson(File.ReadAllText(Path.Combine(Context.Info.Directory, "vein.json")));
        var vein = json with { Id = $"vein-{spot}", Region = json.Region.Offset(new CellPos(corner.X + 5, corner.Y + 1, corner.Z + 5)) };

        var report = await Terrain.Inspect(mound);
        if (!report.Ok) throw new CommandException($"the ground here cannot be read: {report.Reason}");
        foreach (var patch in new[] { mound, vein })
        {
            var r = await Terrain.Apply(patch);
            // Ok is Completed or AlreadyApplied; anything else says what was left alone and why.
            c.Reply(r.Ok
                ? $"{patch.Id}: {r.State}, {r.ChangedCells} cells in {r.Batches} step(s)."
                : $"{patch.Id}: {r.State} ({r.Reason}); {r.Conflicts.Count} block(s) left alone, {r.ChangedCells} cells written.");
        }
    }
}
