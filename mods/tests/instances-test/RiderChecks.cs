// SPDX-License-Identifier: MIT
// Task t4: a mod that is not loaded. The second small mod in rider/ (id instances-rider) keeps its own value on two
// identical swords this test gives it (plan.json in its data folder). While it is not loaded (run-t4.sh moves it out of
// kiln/mods), this test moves its swords with Items.Move; when it is back, it checks its values are on the same swords.
using System.Text.Json;
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    private sealed class RiderPlanItem
    {
        public string Name { get; set; } = "";
        public HolderPos Holder { get; set; } = new();
        public int Flat { get; set; }
    }

    private sealed class RiderPlanFile
    {
        public string Phase { get; set; } = "tag";
        public List<RiderPlanItem> Items { get; set; } = [];
    }

    private string RiderDir => Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "instances-rider"));
    private string RiderPlanPath => Path.Combine(RiderDir, "plan.json");

    private void WriteRiderPlan(RiderPlanFile p)
    {
        Directory.CreateDirectory(RiderDir);
        File.WriteAllText(RiderPlanPath + ".tmp", JsonSerializer.Serialize(p));
        File.Move(RiderPlanPath + ".tmp", RiderPlanPath, true);
    }

    private RiderPlanFile? ReadRiderPlan() =>
        File.Exists(RiderPlanPath) ? JsonSerializer.Deserialize<RiderPlanFile>(File.ReadAllText(RiderPlanPath)) : null;

    /// <summary>d1: two identical swords into chest E for the rider (in this test's ledger as untagged R1 and R2: no value
    /// of this test may appear on them); waits until the rider has tagged them.</summary>
    private async Task RiderPlan()
    {
        var sword = Sword()!;
        var r1 = await Give("R1", "E", false, TwinGear(sword), sword);
        var r2 = await Give("R2", "E", false, TwinGear(sword), sword);
        if (r1 == null || r2 == null) return;
        WriteRiderPlan(new RiderPlanFile
        {
            Phase = "tag",
            Items = [new() { Name = "R1", Holder = r1.Holder, Flat = r1.Flat }, new() { Name = "R2", Holder = r2.Holder, Flat = r2.Flat }],
        });
        var ok = await Until(() => ReadRiderPlan()?.Phase == "tagged", 25 * 60);
        Check(ok, $"t4 rider: instances-rider tagged its two swords in E (flat {r1.Flat}, {r2.Flat})");
    }

    /// <summary>Waits for the rider's check of this boot (its flag carries the process id).</summary>
    private async Task RiderWait(string when)
    {
        var flag = Path.Combine(RiderDir, "checked.flag");
        var me = Environment.ProcessId.ToString();
        var ok = await Until(() => File.Exists(flag) && File.ReadAllText(flag).Trim() == me, 25 * 120);
        Check(ok, $"t4 rider: instances-rider checked its values this boot ({when}); its PASS / FAIL lines are its own");
    }

    /// <summary>d8, the rider not loaded: R1 to chest F, R2 to another slot of E (through F); the plan follows.</summary>
    private async Task RiderMove()
    {
        var plan = ReadRiderPlan();
        if (plan == null)
        {
            Check(false, "t4 rider: no plan");
            return;
        }
        Check(!Mods.All.Any(m => m.Id == "instances-rider" && m.IsLoaded), "t4 rider: instances-rider is not loaded this boot");
        var freeE = FreeGeneric(Chest("E")).Select(s => FlatOf(Items.Read(Chest("E"))!, s.Slot)).Last();
        var ok = await MoveTo("R1", "F");
        ok &= await MoveTo("R2", "F");
        ok &= await MoveTo("R2", "E", freeE);
        foreach (var i in plan.Items)
        {
            var rec = Rec(i.Name);
            i.Holder = rec.Holder;
            i.Flat = rec.Flat;
        }
        plan.Phase = "tagged";
        WriteRiderPlan(plan);
        VerifyLive("d8 the rider's swords moved");
        Check(ok, $"t4 rider: its swords moved while it is not loaded: R1 to F flat {Rec("R1").Flat}, R2 to E flat {Rec("R2").Flat}");
    }
}
