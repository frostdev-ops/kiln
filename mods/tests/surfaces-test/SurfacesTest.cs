// SPDX-License-Identifier: MIT
// Building surfaces on a server, player free: what kiln.worldtables made of the material blocks of surface-sample and the fixtures, and whether the sample's
// C# blocks lower to what its blocks/twins.json says. run.py reads the "sf:" lines.
using System.Text.RegularExpressions;
using Kiln;

namespace SurfacesTest;

public sealed class TestMod : Mod
{
    const string Sample = "surface-sample";
    static readonly string[] Twins = ["moss", "moss-brick", "rune-stone", "snowy-logs"];

    void L(string m) => Context.Log.Info("sf: " + m);

    public override void OnLoad() => _ = Run();

    async Task Run()
    {
        try
        {
            for (var i = 0; i < 24 * 300 && !(Spawn.Available && Spawn.WorldRunning); i++) await Context.Scheduler.NextTick();
            for (var i = 0; i < 24 * 4; i++) await Context.Scheduler.NextTick();
            L($"state {WorldTables.State}, hash {WorldTables.Hash}");
            foreach (var m in WorldTables.Materials) L($"table: {m.CellId} {m.Guid} {m.Block} ({m.Mod}) {(m.Live ? "live" : "kept")}{(m.Reused ? " reused" : "")}");
            foreach (var t in WorldTables.Tweaks) L($"tweak listed: {t.CellId} {t.Block} ({t.Mod})");
            foreach (var name in Twins.SelectMany(n => new[] { n, n + "-json" }))
            {
                var h = Blocks.Find(name, Sample);
                L(h switch
                {
                    TableBlockHandle<BuildingMaterialBlock> b => $"block {name}: status {b.Status}, cell {b.CellId}, guid {b.Guid}",
                    TableBlockHandle<MaterialTweakBlock> t => $"block {name}: status {t.Status}, cell {t.CellId}",
                    BlockHandle<SurfaceBlock> => $"block {name}: a surface",
                    null => $"block {name}: missing",
                    _ => $"block {name}: a {h.GetType().Name}",
                });
                L($"lowered {name}: {Blocks.Lowered(name, Sample)?.ReplaceLineEndings(" ")}");
            }
            // JSON and C# are the same records: what they lower to differs only in names, the ids that follow from names (GUIDs, item ids, the cell id, which
            // follows the GUID's order), and whether the game has it yet
            static string Normal(string? json) => Regex.Replace(Regex.Replace(json ?? "", "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"),
                "-json|\"(status|cellId)\": ?(\"\\w+\"|-?\\d+),? ?|0x[0-9a-f]{8}|(?<=\"equipment\\.voxelData\\.placeVoxelMaterialId\": )\\d+",
                m => m.Value.StartsWith("0x") ? "0xID" : char.IsDigit(m.Value[0]) ? "ID" : "");
            foreach (var n in Twins)
            {
                string cs = Normal(Blocks.Lowered(n, Sample)), json = Normal(Blocks.Lowered(n + "-json", Sample));
                L($"twin {n}: same {cs.Length > 10 && cs == json} ({cs.Length} characters)");
                if (cs != json) L($"twin {n}: C# {cs.ReplaceLineEndings(" ")} | JSON {json.ReplaceLineEndings(" ")}");
            }
            foreach (var (mod, (blocks, errors)) in Blocks.Report())
                foreach (var e in errors) L($"block error: {mod}: {e}");
            L("done");
        }
        catch (Exception e)
        {
            L("THREW " + e);
            L("done");
        }
    }
}
