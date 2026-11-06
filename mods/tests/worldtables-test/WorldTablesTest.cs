// SPDX-License-Identifier: MIT
// kiln.worldtables, player free: the game's own explosions on cubes of known materials, and cells that have to survive restarts and changed mod lists. run.py picks the mode.
using System.Security.Cryptography;
using System.Text.Json;
using Kiln;

namespace WorldTablesTest;

public sealed class TestMod : Mod
{
    string Dir => Context.DataDirectory;
    string Mode => File.Exists(Path.Combine(Dir, "mode")) ? File.ReadAllText(Path.Combine(Dir, "mode")).Trim() : "status";
    void L(string m) => Context.Log.Info("wt: " + m);

    // The save's plateau, far from any base, and a place inside a base zone, where a world keeps what's written.
    static readonly WorldPosition Open = new(3006, 906, 1535), Base = new(3836, 800, 1470);
    const string Tnt = "Terraform_Explosion_TNT_T1", Strike = "Terraform_Fogger_Heavy_Strike";

    public override void OnLoad()
    {
        Directory.CreateDirectory(Dir);
        if (Mode is "code")
        {
            var h = Blocks.Define("code-tweak", new MaterialTweakBlock { Material = "StoneFancy", Strength = new StrengthBlock { Hardness = Hardness.Unbreakable } });
            L($"code tweak: status {h.Status}, cell {h.CellId}");
        }
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
            Tables();
            foreach (var part in Mode.Split('+'))
                switch (part)
                {
                    case "status": break;
                    case "blast": await Blasts(); break;
                    case "code": await One(Tnt, "StoneFancy", 128); break;
                    case "write": await Write(); break;
                    case "read": await Read(); break;
                    case "dump": Dump(); break;
                    case "blast-new": await BlastNew(); break;
                    case "item": CheckItems(); break;
                    case "shapes": CheckShapes(); break;
                    default: L("unknown mode part " + part); break;
                }
            L($"done {Mode}");
        }
        catch (Exception e) { L("THREW " + e); L($"done {Mode}"); }
    }

    void Tables()
    {
        foreach (var m in WorldTables.Materials) L($"table: {m.CellId} {m.Guid} {m.Block} ({m.Mod}) {(m.Live ? "live" : "kept")}{(m.Reused ? " reused" : "")}");
        foreach (var t in WorldTables.Tweaks) L($"tweak listed: {t.CellId} {t.Block} ({t.Mod})");
        foreach (var name in new[] { "StoneRough", "Cell143", "TerrainStone", "NoSuchMaterial" }) L($"game material {name}: {WorldTables.GameMaterialId(name)}");
        using var doc = JsonDocument.Parse(WorldTables.StatusJson());
        foreach (var t in doc.RootElement.GetProperty("tables").EnumerateArray())
            L($"table bytes: {t.GetProperty("name").GetString()}: game {t.GetProperty("gameBytes").GetInt64()} {t.GetProperty("gameFnv").GetString()}, served {t.GetProperty("servedBytes").GetInt64()} {t.GetProperty("servedFnv").GetString()}, identical {t.GetProperty("identical").GetBoolean()}");
        foreach (var e in doc.RootElement.GetProperty("errors").EnumerateArray()) L("error: " + e.GetString());
        foreach (var e in doc.RootElement.GetProperty("refused").EnumerateArray()) L("refused: " + e.GetString());
    }

    // ---- the game's own dig kernel --------------------------------------------------------------------------------------
    async Task<(CellBox Box, Voxel[] Before)> Cube(WorldPosition c, byte mat, BuildFlags flags, int size = 8)
    {
        var box = Build.BoxAround(c, size * 0.25, size * 0.25, size * 0.25);
        await Build.Load(box);
        var cells = new Voxel[box.SizeX * box.SizeY * box.SizeZ];
        Array.Fill(cells, Voxel.Of(mat, 255));
        await Build.WriteAsync(box, cells, new BuildOptions { Flags = flags });
        return (box, await Build.ReadAsync(box));
    }

    async Task Blast(string template, string label, WorldPosition c, byte mat)
    {
        var (box, before) = await Cube(c, mat, BuildFlags.Transient | BuildFlags.InBases);
        await Spawn.Entity(template, c);
        await Wait(24 * 3);
        var after = await Build.ReadAsync(box);
        int removed = 0, partly = 0, solid = 0;
        for (var i = 0; i < before.Length; i++)
        {
            if (before[i].Material != mat || before[i].Fill == 0) continue;
            solid++;
            if (after[i].Fill == 0) removed++;
            else if (after[i].Fill != before[i].Fill) partly++;
        }
        L($"blast {template} on {label} (cell {mat}): solid {solid}, removed {removed}, partly {partly}");
    }

    double _x = 3006;
    WorldPosition Next() { _x += 10; return new WorldPosition(_x, 906, 1535); }
    Task One(string template, string label, byte mat) => Blast(template, label, Next(), mat);

    async Task Blasts()
    {
        foreach (var (name, mat) in new (string, byte)[] { ("StoneRough", 155), ("WoodRough", 143), ("WoodFancy", 144), ("MetalIron", 135), ("StoneFancy", 128) })
            await One(Tnt, name, mat);
        // the strike has 45 against soft and slightly hard, and 0 against anything harder
        await One(Strike, "WoodRough", 143);
        await One(Strike, "WoodDeadwood", 147);
    }

    async Task BlastNew()
    {
        foreach (var m in WorldTables.Materials) await One(Tnt, m.Block + (m.Live ? "" : " (kept)"), (byte)m.CellId);
    }

    // ---- cells across restarts: one small cube per material GUID, in the base zone ------------------------------------------
    string Boxes => Path.Combine(Dir, "boxes.json");
    Dictionary<string, int[]> LoadBoxes() => File.Exists(Boxes) ? JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(Boxes))! : new();

    async Task Write()
    {
        var boxes = LoadBoxes();
        foreach (var m in WorldTables.Materials.Where(m => m.Live))
        {
            if (boxes.ContainsKey(m.Guid.ToString())) continue;
            var c = new WorldPosition(Base.X + boxes.Count * 4, Base.Y, Base.Z);
            var (box, _) = await Cube(c, (byte)m.CellId, BuildFlags.InBases, 4);
            boxes[m.Guid.ToString()] = [box.X, box.Y, box.Z, box.SizeX, box.SizeY, box.SizeZ];
            L($"wrote {m.Block} at cell id {m.CellId}: {Summary(await Build.ReadAsync(box))}");
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
            var id = WorldTables.CellIdOf(Guid.Parse(guid));
            L($"cells of {guid}: the table says {id}, the world has {Summary(await Build.ReadAsync(box))}");
        }
        L($"save {await Saves.Request()}");
        await Wait(24);
    }

    static string Summary(Voxel[] c) => string.Join(", ", c.GroupBy(v => (v.Material, v.Fill)).OrderByDescending(g => g.Count()).Take(3).Select(g => $"mat {g.Key.Material} fill {g.Key.Fill} x{g.Count()}"));

    // ---- bytes ---------------------------------------------------------------------------------------------------------------
    void Dump()
    {
        foreach (var (name, type) in new[] { ("registry", "keen::TerraformingEfficiencyRegistryResource"), ("pools", "keen::VoxelBlueprintMaterialPoolRegistryResource"), ("blueprints", "keen::VoxelBlueprintItemRegistryResource"), ("world", "keen::VoxelWorldResource") })
        {
            var i = 0;
            foreach (var key in Assets.ListResources(new AssetKey(Guid.Empty, type).Type))
            {
                var bytes = Assets.ReadResource(key);
                if (bytes == null) continue;
                if (name == "world" && bytes.Length < 100000) continue;     // the one with a material table on this side
                File.WriteAllBytes(Path.Combine(Dir, $"{name}.bin"), bytes);
                L($"dump {name}: {key.Guid} {bytes.Length} bytes sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
                if (++i > 0) break;
            }
        }
    }

    // The game's 4 m door wall: its blueprint item, and the preview model that item names.
    static readonly Guid DoorWallItem = Guid.Parse("74cf7092-c244-49a4-87bf-7e95641037ba"), DoorWallModel = Guid.Parse("34f8d9a7-2eeb-4828-b934-0a4c8809749f");

    void CheckShapes()
    {
        var bps = Assets.ReadResource(Assets.ListResources(new AssetKey(Guid.Empty, "keen::VoxelBlueprintItemRegistryResource").Type)[0]) ?? [];
        using var doc = JsonDocument.Parse(WorldTables.StatusJson());
        foreach (var sh in doc.RootElement.GetProperty("shapes").EnumerateArray())
        {
            var (mod, block, guid) = (sh.GetProperty("mod").GetString()!, sh.GetProperty("block").GetString()!, Guid.Parse(sh.GetProperty("item").GetString()!));
            var item = Content.Items.Get($"{mod}.{block}");
            var entry = Entry(bps, BlockIds.Fnv(guid));
            L($"shape {block}: item {(item == null ? "not there" : $"{item.Name} 0x{item.Id:x8} guid {item.Guid}")}, table entry {entry}");
            if (item == null) continue;
            var model = Assets.ReadResource(new AssetKey(guid, "keen::VoxelModelResource"));
            L($"shape {block}: voxelObject {item.GetRef("equipment.voxelObject")}, pool {item.GetRef("equipment.voxelBlueprintMaterialPool")}, preview model {(model == null ? "none" : $"{model.Length} bytes sha256 {Hash(model)}")}");
        }
        var game = Content.Items.Get(BlockIds.Fnv(DoorWallItem));
        var gameModel = Assets.ReadResource(new AssetKey(DoorWallModel, "keen::VoxelModelResource"));
        L($"game door wall: table entry {Entry(bps, game?.Id ?? 0)}, pool {game?.GetRef("equipment.voxelBlueprintMaterialPool")}, preview model {(gameModel == null ? "none" : $"{gameModel.Length} bytes sha256 {Hash(gameModel)}")}");
    }

    static string Hash(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant()[..16];

    // VoxelBlueprintItemRegistryResource: BlobArray of {itemId, uint3 size, BlobArray<u8> data, bool compressed}, 28 bytes each
    static string Entry(byte[] t, uint itemId)
    {
        var off = BitConverter.ToInt32(t, 0);
        var n = BitConverter.ToInt32(t, 4);
        for (var i = 0; i < n; i++)
        {
            var e = off + i * 28;
            if (BitConverter.ToUInt32(t, e) != itemId) continue;
            var d = e + 16 + BitConverter.ToInt32(t, e + 16);
            var len = BitConverter.ToInt32(t, e + 20);
            return $"{BitConverter.ToUInt32(t, e + 4)}x{BitConverter.ToUInt32(t, e + 8)}x{BitConverter.ToUInt32(t, e + 12)} compressed {t[e + 24]} cells {Convert.ToHexString(t, d, len).ToLowerInvariant()}";
        }
        return "none";
    }

    void CheckItems()
    {
        foreach (var m in WorldTables.Materials.Where(m => m.Live))
        {
            var item = Content.Items.Get($"{m.Mod}.{m.Block}");
            L(item == null
                ? $"item {m.Mod}.{m.Block}: not there"
                : $"item {item.Name}: id 0x{item.Id:x8}, voxel id {item.GetNumber("equipment.voxelData.placeVoxelMaterialId")}, table says {m.CellId}");
            if (item == null) continue;
            var pools = Assets.ReadResource(Assets.ListResources(new AssetKey(Guid.Empty, "keen::VoxelBlueprintMaterialPoolRegistryResource").Type)[0]) ?? [];
            var bps = Assets.ReadResource(Assets.ListResources(new AssetKey(Guid.Empty, "keen::VoxelBlueprintItemRegistryResource").Type)[0]) ?? [];
            var id = BitConverter.GetBytes(item.Id);
            L($"item {item.Name}: in the pools {pools.AsSpan().IndexOf(id) >= 0}, in the blueprint items {bps.AsSpan().IndexOf(id) >= 0}");
        }
    }
}
