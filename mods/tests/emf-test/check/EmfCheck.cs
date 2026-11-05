// SPDX-License-Identifier: MIT
using System.Text.Json.Nodes;
using Kiln;

namespace EmfCheck;

/// <summary>Kiln-side cross-check of kiln-emf in a lab: reads Kiln's own content registry (not the EMF table) to see
/// what big_stacks changed, and checks kiln-emf's status file and the launcher files it writes. One PASS / FAIL line per
/// check, then "emf-check done".</summary>
public sealed class EmfCheckMod : Mod
{
    private int _pass, _fail;
    private bool _ran, _stop;

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    public override void OnLoad()
    {
        Content.OnReady(e => { if (!_ran) { _ran = true; _ = RunAsync(); } });
    }

    public override void OnUnload() => _stop = true;

    private async Task RunAsync()
    {
        // big_stacks starts once the item catalog is in and walks the items 2 ms per step, in no fixed order: wait until
        // the number of changed items has stayed the same for 3 s after it began (at most 90 s)
        int last = -1, stable = 0;
        for (var i = 0; i < 90 && !_stop; i++)
        {
            for (var t = 0; t < 25 && !_stop; t++) await Context.Scheduler.NextTick();
            var changed = Content.Items.Count(e => e.Modified);
            stable = changed > 0 && changed == last ? stable + 1 : 0;
            last = changed;
            if (stable >= 3) break;
        }
        if (_stop) return;
        var kiln = Path.GetDirectoryName(Path.GetDirectoryName(typeof(Mod).Assembly.Location)!)!;
        var state = JsonNode.Parse(File.ReadAllText(Path.Combine(kiln, "state", "emf.json")))!;
        var mods = state["mods"]!.AsArray().ToDictionary(m => (string)m!["id"]!, m => m!);
        string Status(string id) => mods.TryGetValue(id, out var m) ? (string)m["status"]! : "absent";
        Check((bool)state["steppedAside"]! == false && (int)state["emfApi"]! == 21, "state/emf.json: kiln-emf runs, EmfApi v21");
        foreach (var id in new[] { "big_stacks", "herald", "server-admin", "sleep", "teleports" })
            Check(Status(id) == "loaded", $"state/emf.json: {id} loaded ({Status(id)})");
        Check(Status("clock") == "rejected", $"state/emf.json: clock (side client) is not loaded on a server ({Status("clock")})");

        // big_stacks through Kiln's registry: every stackable item x4, at most 9999 (its defaults)
        if (Status("big_stacks") == "loaded")
        {
            var wood = Content.Items.Get("Material_T1_Wood_Logs");
            Check(wood is not null && wood.MaxStack == 1000 && wood.Original("maxStackSize").Integer == 250 && wood.Modified,
                  $"Kiln.Content: Material_T1_Wood_Logs max stack {wood?.MaxStack} (original {wood?.Original("maxStackSize").Integer}) = 250 x4");
            int raised = 0, wrong = 0, kept = 0;
            string? firstWrong = null;
            foreach (var e in Content.Items)
            {
                var orig = (int)e.Original("maxStackSize").Integer;
                var now = e.MaxStack;
                var want = orig <= 1 ? orig : Math.Max(orig, Math.Min(9999, (int)Math.Floor(orig * 4.0 + 0.5)));
                if (now == want && now != orig) raised++;
                else if (now == want) kept++;
                else { wrong++; firstWrong ??= $"{e.Name} {orig} -> {now} (expected {want})"; }
            }
            Check(raised > 2000 && wrong == 0, $"Kiln.Content: {raised} items raised by the formula, {kept} unchanged (do not stack), {wrong} differ{(firstWrong is null ? "" : $" e.g. {firstWrong}")}");
            Check(Content.StackGuard, "Kiln.Content: the stack guard is on (raised stacks are not cut back)");
        }

        // launcher files: <configDir>/emf/<id>.ini + .schema/emf/<id>.json (contracts.md 5.5)
        var cfg = Kiln.Config.Directory;
        var ini = Path.Combine(cfg, "emf", "big_stacks.ini");
        Check(File.Exists(ini) && File.ReadAllText(ini).Contains("[big_stacks]") && File.ReadAllText(ini).Contains("multiplier="),
              $"{ini} has the section and the seeded settings");
        var mirror = Path.Combine(Path.GetDirectoryName(kiln)!, "emf", "config", "big_stacks.ini");
        Check(File.Exists(mirror) && File.ReadAllText(mirror).Contains("enabled=1"), $"mirror {mirror} carries the switch");
        var schema = Path.Combine(cfg, ".schema", "emf", "big_stacks.json");
        var sj = File.Exists(schema) ? JsonNode.Parse(File.ReadAllText(schema)) : null;
        Check(sj is not null && (string)sj["source"]! == "emf" && (string)sj["file"]! == "emf/big_stacks.ini" &&
              sj["entries"]!.AsArray().Any(e => (string)e!["path"]! == "big_stacks.multiplier" && (string)e["type"]! == "float"),
              $"{schema}: source emf, file emf/big_stacks.ini, big_stacks.multiplier float");
        Log.Info($"emf-check done: {_pass} PASS, {_fail} FAIL");
    }
}
