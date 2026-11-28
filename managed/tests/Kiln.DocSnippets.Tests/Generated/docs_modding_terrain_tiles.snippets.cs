// Made by tools/docs-site/snippets.py from docs/kiln/modding/terrain-tiles.md: do not edit; edit the page or
// tools/docs-site/snippets.json and run python3 -I tools/docs-site/snippets.py
#pragma warning disable CS1591

namespace DocSnippets.docs_modding_terrain_tiles_1
{
#line 24 "docs/kiln/modding/terrain-tiles.md"
public sealed class MoundMod : Mod
{
    [Command("mound", Description = "raise a mound next to you", Permission = CommandPermission.Admin)]
    private async Task Mound(CommandContext c)
    {
        if (!Build.Available || !Capabilities.Is("terrain.overlays")) throw new CommandException("terrain overlays are off on this server");
        var at = World.Position(c.Source.Player) ?? throw new CommandException("your position is not known");
        var heights = new float[16 * 16];
        for (var z = 0; z < 16; z++)
        for (var x = 0; x < 16; x++)
            heights[z * 16 + x] = MathF.Max(0, 6f - MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (z - 7.5f) * (z - 7.5f)));   // cells above the floor
        var corner = Build.CellOf(at);
        var mound = new TerrainPatch("mound", 1, new CellBox(corner.X, corner.Y, corner.Z, 16, 6, 16),
            new TerrainHeightfield(heights, Fill: 1, Mode: TerrainHeightMode.AddOnTop), [new TerrainPaletteEntry(1)]);

        var result = await Terrain.Apply(mound);                 // bounded units across ticks
        c.Reply(result.Ok ? "the mound is up" : $"{result.State}: {result.Reason}");
    }
}
#line default
}
