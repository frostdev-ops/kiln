// SPDX-License-Identifier: MIT
// Baked terrain on a lab server, player free (dossier chapter 19f route B, step 1; docs/re/build.md section 9). run.py
// cooks the pack into this mod's folder with tools/kfc (terraincook.py, plan.json): level-6 tile 983 changed and
// written by Kiln's tile writer under the game's own ContentHash (a blob entry with that explicit hash), tile 1063 the
// game's own all-air tile reused by its hash (no blob at all), and the VoxelWorldResource root with those two
// references as a boot override. Once the world runs, this mod reads every check box of the pack's receipt.json through
// kiln.build and compares the cells with the ones the pack holds:
//   terrain-tile-test: PASS|FAIL <check> cells <n> matter <n> sha256 <hex>     one per check
//   terrain-tile-test: READY <pass> PASS, <fail> FAIL                          at the end
// A failing check's cells go to kiln/data/terrain-tile-test/<check>.bin (u16 little-endian, x fastest, then y, then z);
// a box the server does not load (`Build.ReadAsync` refuses it) is a FAIL of that check, and the next check runs.
// Dedicated lab servers only: the pack's coarser levels and companions are stale, and no client may join.
using System.Security.Cryptography;
using System.Text.Json;
using Kiln;

namespace TerrainTileTest;

public sealed class TerrainTileTestMod : Mod
{
    private const string Tag = "terrain-tile-test";
    private int _pass, _fail;

    public override void OnLoad() => _ = Run();

    private void Line(bool ok, string text)
    {
        if (ok) { _pass++; Context.Log.Info($"{Tag}: PASS {text}"); }
        else { _fail++; Context.Log.Warn($"{Tag}: FAIL {text}"); }
    }

    private static byte[] Bytes(Voxel[] cells)
    {
        var b = new byte[cells.Length * 2];
        for (var i = 0; i < cells.Length; i++)
        {
            b[2 * i] = (byte)cells[i].Raw;
            b[2 * i + 1] = (byte)(cells[i].Raw >> 8);
        }
        return b;
    }

    private async Task Run()
    {
        try
        {
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(Context.Info.Directory, "receipt.json")));
            for (var i = 0; i < 24 * 300 && (!Build.Available || !Spawn.WorldRunning || Build.Grid is null); i++)
                await Context.Scheduler.NextTick();
            Line(Build.Grid is not null && Spawn.WorldRunning, $"world running {Build.Grid}");
            foreach (var t in receipt.RootElement.GetProperty("tiles").EnumerateArray())
                Context.Log.Info($"{Tag}: INFO tile {t.GetProperty("index").GetInt32()} {t.GetProperty("old").GetString()} -> " +
                                 $"{t.GetProperty("new").GetString()} (shipped blob reused: {t.GetProperty("reusesShippedBlob").GetBoolean()})");
            foreach (var c in receipt.RootElement.GetProperty("checks").EnumerateArray())
            {
                var name = c.GetProperty("name").GetString()!;
                var b = c.GetProperty("box");
                var box = new CellBox(b.GetProperty("x").GetInt32(), b.GetProperty("y").GetInt32(), b.GetProperty("z").GetInt32(),
                    b.GetProperty("sizeX").GetInt32(), b.GetProperty("sizeY").GetInt32(), b.GetProperty("sizeZ").GetInt32());
                Voxel[] cells;
                try
                {
                    cells = await Build.ReadAsync(box, TimeSpan.FromSeconds(60));
                }
                catch (BuildException e)
                {
                    Line(false, $"{name} not read: {e.Message}");
                    continue;
                }
                var bytes = Bytes(cells);
                var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                var want = c.GetProperty("sha256").GetString();
                var matter = cells.Count(v => v.Raw >> 8 != 0);
                var ok = sha == want && cells.Length == c.GetProperty("cells").GetInt32();
                Line(ok, $"{name} cells {cells.Length} matter {matter} (pack {c.GetProperty("matter").GetInt32()}) sha256 {sha}" +
                         (ok ? "" : $" (pack {want})"));
                if (!ok)
                {
                    Directory.CreateDirectory(Context.DataDirectory);
                    File.WriteAllBytes(Path.Combine(Context.DataDirectory, name + ".bin"), bytes);
                }
            }
        }
        catch (Exception e)
        {
            Line(false, "exception " + e);
        }
        Context.Log.Info($"{Tag}: READY {_pass} PASS, {_fail} FAIL");
    }
}
