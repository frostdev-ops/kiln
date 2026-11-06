// SPDX-License-Identifier: MIT
// Terrain materials on a server, player free: what kiln.worldtables made of terrain-sample's blocks and the fixtures run.py writes, the sample's C# blocks
// against its blocks/twins.json, cells written through Kiln.Build and read back across restarts, the game's own explosion on cubes of them, and the served
// tables as bytes for run.py to decode with tools/kfc. run.py picks the mode (kiln/data/terrain-test/mode) and reads the "tt:" lines.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kiln;

namespace TerrainTest;

public sealed class TestMod : Mod
{
    const string Sample = "terrain-sample";
    static readonly string[] Twins = ["glacier-ore", "packed-snow"];

    string Dir => Context.DataDirectory;
    string Mode => File.Exists(Path.Combine(Dir, "mode")) ? File.ReadAllText(Path.Combine(Dir, "mode")).Trim() : "status";
    void L(string m) => Context.Log.Info("tt: " + m);

    // The save's plateau, far from any base (what's written there is gone at the next start), and a place inside the base zone, where the world keeps it.
    static readonly WorldPosition Open = new(3006, 906, 1555), Base = new(3836, 800, 1478);
    const string Tnt = "Terraform_Explosion_TNT_T1";
    const double AltarX = 3800, AltarZ = 1434;   // the save's base altar (docs/re/build.md 7)

    public override void OnLoad()
    {
        Directory.CreateDirectory(Dir);
        if (Mode.Contains("code-bad")) CodeBad();
        _ = Run();
    }

    async Task Wait(int ticks) { for (var i = 0; i < ticks; i++) await Context.Scheduler.NextTick(); }

    async Task Run()
    {
        try
        {
            for (var i = 0; i < 24 * 300 && !(Spawn.Available && Spawn.WorldRunning && Build.Available && Build.Grid is not null); i++)
                await Context.Scheduler.NextTick();
            await Wait(24 * 4);
            L($"mode {Mode}: state {WorldTables.State}, hash {WorldTables.Hash}");
            Status();
            foreach (var part in Mode.Split('+'))
                switch (part)
                {
                    case "status" or "code-bad": break;
                    case "write": await Write(); break;
                    case "read": await Read(); break;
                    case "blast": await Blasts(); break;
                    case "dump": Dump(); break;
                    case "commands": await SampleCommands(); break;
                    default: L("unknown mode part " + part); break;
                }
            L($"done {Mode}");
        }
        catch (Exception e) { L("THREW " + e); L($"done {Mode}"); }
    }

    // ---- what the tables and the blocks say -------------------------------------------------------------------------------
    void Status()
    {
        foreach (var m in WorldTables.Materials) L($"table: {m.CellId} {m.Guid} {m.Block} ({m.Mod}) {(m.Live ? "live" : "kept")}{(m.Terrain ? " terrain" : " building")}");
        using (var doc = JsonDocument.Parse(WorldTables.StatusJson()))
        {
            var root = doc.RootElement;
            L($"room: building {root.GetProperty("room").GetInt32()}, terrain {(root.TryGetProperty("terrainRoom", out var tr) ? tr.GetInt32() : -1)}");
            foreach (var t in root.GetProperty("tables").EnumerateArray())
                L($"table bytes: {t.GetProperty("name").GetString()}: game {t.GetProperty("gameBytes").GetInt64()}, served {t.GetProperty("servedBytes").GetInt64()}, identical {t.GetProperty("identical").GetBoolean()}");
            foreach (var e in root.GetProperty("errors").EnumerateArray()) L("error: " + e.GetString());
            foreach (var e in root.GetProperty("refused").EnumerateArray()) L("refused: " + e.GetString());
        }
        foreach (var name in Twins.SelectMany(n => new[] { n, n + "-json" }))
        {
            L(Blocks.Find(name, Sample) switch
            {
                TableBlockHandle<TerrainMaterialBlock> t => $"block {name}: status {t.Status}, cell {t.CellId}, guid {t.Guid}",
                null => $"block {name}: missing",
                var h => $"block {name}: a {h.GetType().Name}",
            });
            L($"lowered {name}: {Blocks.Lowered(name, Sample)?.ReplaceLineEndings(" ")}");
        }
        // JSON and C# are the same records: what they lower to differs only in names, the ids that follow from names, and whether the game has it yet
        static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"),
            "-json|\"(status|cellId)\": ?(\"\\w+\"|-?\\d+),? ?|0x[0-9a-f]{8}|(?<=\"equipment\\.voxelData\\.placeVoxelMaterialId\": )\\d+",
            m => m.Value.StartsWith("0x") ? "0xID" : char.IsDigit(m.Value[0]) ? "ID" : "");
        foreach (var n in Twins)
        {
            string cs = Normal(Blocks.Lowered(n, Sample)), json = Normal(Blocks.Lowered(n + "-json", Sample));
            L($"twin {n}: same {cs.Length > 10 && cs == json} ({cs.Length} characters)");
            if (cs != json) L($"twin {n}: C# {cs.ReplaceLineEndings(" ")} | JSON {json.ReplaceLineEndings(" ")}");
        }
        foreach (var (mod, (_, errors)) in Blocks.Report())
            foreach (var e in errors) L($"block error: {mod}: {e}");
        // a placeable material's item: places its cell id and GUID, and the game's terrain shapes list it
        var pools = Assets.ReadResource(Assets.ListResources(new AssetKey(Guid.Empty, "keen::VoxelBlueprintMaterialPoolRegistryResource").Type)[0]) ?? [];
        foreach (var m in WorldTables.Materials.Where(m => m.Live && m.Terrain))
        {
            var item = Content.Items.Get($"{m.Mod}.{m.Block}");
            if (item == null)
            {
                L($"item {m.Block}: none");
                continue;
            }
            L($"item {m.Block}: {item.Name} 0x{item.Id:x8} guid {item.Guid}, voxel id {item.GetNumber("equipment.voxelData.placeVoxelMaterialId")}, voxel guid {item.GetRef("equipment.voxelData.placeVoxelMaterial")}, " +
              $"building {item.GetNumber("equipment.voxelData.isBuildingVoxel")}, in the pools {pools.AsSpan().IndexOf(BitConverter.GetBytes(item.Id)) >= 0}");
        }
        foreach (var game in new[] { "Material_T7_Celesthium_Ore", "Build_TerrainMaterial_T5_Snow" })
            L($"game item {game}: {Content.Items.Get(game)?.Guid}");
    }

    // Wrong blocks from code: refused at once, by field. (This driver says "clients": "mod", so the field is what refuses them.)
    void CodeBad()
    {
        foreach (var (name, block) in new (string, TerrainMaterialBlock)[]
                 {
                     ("bad-like", new TerrainMaterialBlock { Name = "Bad Like", Like = "WoodRough" }),
                     ("bad-recipe", new TerrainMaterialBlock { Name = "Bad Recipe", Recipe = new RecipeBlock { Needs = { ["Material_T1_Twigs"] = 1 } } }),
                     ("bad-look", new TerrainMaterialBlock { Name = "Bad Look", Look = "NoSuchMaterial" }),
                 })
        {
            try
            {
                var h = Blocks.Define(name, block);
                L($"code-bad {name}: taken, status {h.Status}");
            }
            catch (BlockError e) { L($"code-bad {name}: refused: {e.Message}"); }
        }
    }

    // ---- cells -------------------------------------------------------------------------------------------------------------
    async Task<(CellBox Box, Voxel[] Before)> Cube(WorldPosition c, byte mat, BuildFlags flags, int size = 8)
    {
        var box = Build.BoxAround(c, size * 0.25, size * 0.25, size * 0.25);
        await Build.Load(box);
        var cells = new Voxel[box.SizeX * box.SizeY * box.SizeZ];
        Array.Fill(cells, Voxel.Of(mat, 255));
        var r = await Build.WriteAsync(box, cells, new BuildOptions { Flags = flags });
        if (r.Refused) L($"write of cell {mat} refused: {r.Refusal}");
        return (box, await Build.ReadAsync(box));
    }

    static string Summary(Voxel[] c) => string.Join(", ", c.GroupBy(v => (v.Material, v.Fill)).OrderByDescending(g => g.Count()).Take(3).Select(g => $"mat {g.Key.Material} fill {g.Key.Fill} x{g.Count()}"));

    string Boxes => Path.Combine(Dir, "boxes.json");
    Dictionary<string, int[]> LoadBoxes() => File.Exists(Boxes) ? JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(Boxes))! : new();

    // one small cube per live terrain material, in the base zone, where the world keeps it
    async Task Write()
    {
        var boxes = LoadBoxes();
        foreach (var m in WorldTables.Materials.Where(m => m.Live && m.Terrain))
        {
            if (boxes.ContainsKey(m.Guid.ToString())) continue;
            var c = new WorldPosition(Base.X + boxes.Count * 4, Base.Y, Base.Z);
            var (box, after) = await Cube(c, (byte)m.CellId, BuildFlags.InBases, 4);
            boxes[m.Guid.ToString()] = [box.X, box.Y, box.Z, box.SizeX, box.SizeY, box.SizeZ];
            L($"wrote {m.Block} at cell id {m.CellId}: {Summary(after)}");
        }
        File.WriteAllText(Boxes, JsonSerializer.Serialize(boxes));
        L($"save {await Saves.Request()}");
        await Wait(24);
    }

    async Task Read()
    {
        foreach (var (guid, a) in LoadBoxes())
        {
            var box = new CellBox(a[0], a[1], a[2], a[3], a[4], a[5]);
            await Build.Load(box);
            L($"cells of {guid}: the table says {WorldTables.CellIdOf(Guid.Parse(guid))}, the world has {Summary(await Build.ReadAsync(box))}");
        }
        L($"save {await Saves.Request()}");
        await Wait(24);
    }

    // ---- the game's own explosion -------------------------------------------------------------------------------------------
    double _x = Open.X;

    async Task Blast(string label, byte mat)
    {
        _x += 10;
        var c = new WorldPosition(_x, Open.Y, Open.Z);
        var (box, before) = await Cube(c, mat, BuildFlags.Transient | BuildFlags.InBases);
        await Spawn.Entity(Tnt, c);
        await Wait(24 * 3);
        var after = await Build.ReadAsync(box);
        int solid = 0, removed = 0, partly = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Material != mat || before[i].Fill == 0) continue;
            solid++;
            if (after[i].Fill == 0) removed++;
            else if (after[i].Fill != before[i].Fill) partly++;
        }
        L($"blast {Tnt} on {label} (cell {mat}): solid {solid}, removed {removed}, partly {partly}");
    }

    async Task Blasts()
    {
        await Blast("TerrainStone", 2);
        await Blast("Cell26", 26);
        foreach (var m in WorldTables.Materials.Where(m => m.Terrain)) await Blast(m.Block + (m.Live ? "" : " (kept)"), (byte)m.CellId);
    }

    // ---- the sample's commands, as a server console runs them (a position instead of a player) ------------------------------
    // The ground's top in a column: the highest cell at least half full, from 8 m above to 32 m below.
    async Task<double?> Ground(double x, double y, double z)
    {
        var col = Build.BoxBetween(new WorldPosition(x, y - 32, z), new WorldPosition(x, y + 8, z));
        var cells = await Build.ReadAsync(col);
        for (var j = col.SizeY - 1; j >= 0; j--)
            if (cells[j].Material != 0 && cells[j].Fill >= 128) return Build.CornerOf(new CellPos(col.X, col.Y + j, col.Z)).Y + Build.Grid!.CellSize;
        return null;
    }

    async Task<int> CountAround(double x, double y, double z, byte mat)
    {
        var box = Build.BoxAround(new WorldPosition(x, y, z), 3, 12, 3);   // tall: an earlier run's mound (kept, then turned to grass) raises the ground
        return (await Build.ReadAsync(box)).Count(v => v.Material == mat && v.Fill > 0);
    }

    async Task Run(string line)
    {
        var r = Commands.Execute(CommandSource.Console, line);
        L($"run {line}: started {r.Ok}{(r.Error is null ? "" : ": " + r.Error)}");
        await Wait(24 * 3);   // an async command logs its outcome when it ends: "command /<name> by console: ok" or the error
    }

    async Task SampleCommands()
    {
        var ore = WorldTables.Materials.FirstOrDefault(m => m.Block == "glacier-ore-json" && m.Live);
        var snow = WorldTables.Materials.FirstOrDefault(m => m.Block == "packed-snow-json" && m.Live);
        if (ore is null || snow is null)
        {
            L("commands: the sample's materials aren't live");
            return;
        }
        // the save's plateau: docs/re/build.md has this column's surface at 902 m
        const double x = 3024, z = 1552;
        // what an earlier run left (edits outside a base are kept): the ground back to grass where the vein and the mound go
        foreach (var (cx, mat) in new[] { (x, (byte)ore.CellId), (x + 10, (byte)snow.CellId) })
            await Build.Replace(BuildShape.Sphere(new WorldPosition(cx, 902, z), 6), mat, 1);
        double baseTop = 0;
        foreach (var (px, pz) in new[] { (x, z), (3016.0, 1600.0), (AltarX + 6, AltarZ) })
        {
            // a wide look first: where the top of the ground is in the column, if anywhere from 600 m to 1000 m
            var col = Build.BoxBetween(new WorldPosition(px, 600, pz), new WorldPosition(px, 1000, pz));
            var cells = await Build.ReadAsync(col);
            var top = Enumerable.Range(0, cells.Length).Where(j => cells[j].Material != 0 && cells[j].Fill >= 128).DefaultIfEmpty(-1).Max();
            if (px == AltarX + 6 && top >= 0) baseTop = Build.CornerOf(new CellPos(col.X, col.Y + top, col.Z)).Y;
            L($"commands: probe ({px}, {pz}): {(top < 0 ? "no solid cell from 600 m to 1000 m" : $"top solid cell at {Build.CornerOf(new CellPos(col.X, col.Y + top, col.Z)).Y:F1} m, mat {cells[top].Material} fill {cells[top].Fill}")}");
        }
        if (await Ground(x, 910, z) is not { } g)
        {
            L($"commands: no ground at ({x}, {z})");
            return;
        }
        L($"commands: ground at ({x}, {z}) is {g:F2} m; ore is cell {ore.CellId}, snow cell {snow.CellId}");
        // what the command did before: a sphere 1.5 m across whose top is at the aimed height, painted; aimed 20 m above the ground (a ledge, a cliff's edge)
        var old = await Build.Paint(BuildShape.Sphere(new WorldPosition(x, g + 20 - 1.5, z), 1.5), (byte)ore.CellId, new BuildOptions { Flags = BuildFlags.DryRun });
        L($"commands: the old aim 20 m above the ground: {old.CellsChanged} cells ({old.Refusal})");
        await Run($"/glacier-vein {x} {g + 20:F2} {z}");
        L($"commands: ore cells after the vein: {await CountAround(x, g, z, (byte)ore.CellId)}");
        await Run($"/glacier-vein {x} {g:F2} {z}");                       // again: the ground there is ore already
        await Run($"/glacier-vein {x} 1500 {z}");                         // nothing within 32 m below
        await Run($"/snow-mound {x + 10} {g:F2} {z}");
        L($"commands: snow cells after the mound: {await CountAround(x + 10, g, z, (byte)snow.CellId)}");
        await Run($"/glacier-vein {AltarX + 6} {baseTop + 1:F1} {AltarZ}"); // on the ground 6 m from the base's altar, without InBases
        await Run("/glacier-vein");                                       // the console has no position
    }

    // ---- the served tables, for tools/kfc --------------------------------------------------------------------------------------
    void Dump()
    {
        foreach (var (name, type) in new[] { ("registry", "keen::TerraformingEfficiencyRegistryResource"), ("pools", "keen::VoxelBlueprintMaterialPoolRegistryResource"), ("vfx", "keen::VoxelMaterialVfxDataResource"), ("world", "keen::VoxelWorldResource") })
            foreach (var key in Assets.ListResources(new AssetKey(Guid.Empty, type).Type))
            {
                var bytes = Assets.ReadResource(key);
                if (bytes == null || (name == "world" && bytes.Length < 100000)) continue;   // the world with a material table on this side
                File.WriteAllBytes(Path.Combine(Dir, $"served-{name}.bin"), bytes);
                L($"dump {name}: {key.Guid} {bytes.Length} bytes sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()[..16]}");
                break;
            }
    }
}
