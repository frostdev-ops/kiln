// SPDX-License-Identifier: MIT
// A second mod with instance data (task t4, "a mod that is not loaded"). Reads plan.json in its data folder, which
// instances-test writes: phase "tag" -> it sets its value on the two swords at the plan's places and writes "tagged";
// phase "tagged" -> it checks that the sword at each place carries its own value (never the other's), logs PASS / FAIL,
// and writes checked.flag with the process id.
using System.Text.Json;
using Kiln;

namespace InstancesRider;

public sealed class RiderTag
{
    public string Name { get; set; } = "";
}

public sealed class InstancesRiderMod : Mod
{
    private InstanceKey<RiderTag>? _key;
    private bool _stop;

    private sealed class Pos
    {
        public string Template { get; set; } = "";
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
    }

    private sealed class PlanItem
    {
        public string Name { get; set; } = "";
        public Pos Holder { get; set; } = new();
        public int Flat { get; set; }
    }

    private sealed class Plan
    {
        public string Phase { get; set; } = "";
        public List<PlanItem> Items { get; set; } = [];
    }

    public override void OnLoad()
    {
        _key = InstanceData.Key<RiderTag>("rider.tag");
        _ = Run();
    }

    public override void OnUnload() => _stop = true;

    [Subscribe]
    private void OnRestored(InstanceDataRestoredEvent e)
    {
        if (e.Mod == Context.Info.Id) Log.Info($"INFO rider: restored {e.Key} on {e.Item} ({e.Match}): {e.Detail}");
    }

    [Subscribe]
    private void OnLost(InstanceDataLostEvent e)
    {
        if (e.Mod == Context.Info.Id) Log.Info($"INFO rider: lost [{string.Join(",", e.Keys)}] of {e.Item} ({e.Reason}): {e.Detail}");
    }

    private async Task<bool> Until(Func<bool> ready, int max)
    {
        for (var i = 0; i < max && !_stop; i++)
        {
            if (ready()) return true;
            await Context.Scheduler.NextTick();
        }
        return ready();
    }

    private string PlanPath => Path.Combine(Context.DataDirectory, "plan.json");

    private Plan? Read() => File.Exists(PlanPath) ? JsonSerializer.Deserialize<Plan>(File.ReadAllText(PlanPath)) : null;

    private static EntityId Resolve(Pos p)
    {
        var near = Items.FindInventories(InventoryKinds.Containers)
            .Where(e => Spawn.Inspect(e) is { } i && (i.TemplateName ?? i.Template?.ToString()) == p.Template &&
                        Math.Abs(i.Position.X - p.X) < 0.5 && Math.Abs(i.Position.Y - p.Y) < 0.5 && Math.Abs(i.Position.Z - p.Z) < 0.5)
            .ToList();
        return near.Count == 1 ? near[0] : EntityId.None;
    }

    private static InventorySlot? AtFlat(EntityId holder, int flat)
    {
        var c = Items.Read(holder);
        if (c == null) return null;
        foreach (var s in c.Slots)
        {
            var generic = 0;
            foreach (var (inv, cat) in c.Inventories)
            {
                if (inv == s.Slot.Inventory)
                {
                    if (cat == InventoryCategory.Generic && generic * 8 + s.Slot.Index == flat) return s;
                    break;
                }
                if (cat == InventoryCategory.Generic) generic++;
            }
        }
        return null;
    }

    private async Task Run()
    {
        int pass = 0, fail = 0;
        try
        {
            await Until(() => Spawn.WorldRunning && Items.Status() is { Transactions: true } && Saves.WorldDataReady, 25 * 240);
            await Until(() => Read() is { Items.Count: > 0 }, 25 * 600);
            await Until(() => false, 100);
            var plan = Read();
            if (plan == null || _stop) return;
            if (plan.Phase == "tag")
            {
                foreach (var i in plan.Items)
                {
                    var s = AtFlat(Resolve(i.Holder), i.Flat);
                    if (s == null || s.Ref.IsNone)
                    {
                        Log.Info($"FAIL rider: no item for {i.Name} at {i.Holder.Template} flat {i.Flat}");
                        fail++;
                        continue;
                    }
                    s.Ref.Set(_key!, new RiderTag { Name = i.Name });
                    Log.Info($"PASS rider: tagged {i.Name} = {s.Ref} at flat {i.Flat}");
                    pass++;
                }
                plan.Phase = "tagged";
                File.WriteAllText(PlanPath, JsonSerializer.Serialize(plan));
                return;
            }
            // wait for the matcher to decide each one
            await Until(() => plan.Items.All(i => AtFlat(Resolve(i.Holder), i.Flat) is { } s && !s.Ref.IsNone &&
                                                  (s.Ref.Has(_key!) || InstanceData.Parked(_key!).Any(p => p.Value?.Name == i.Name))), 25 * 90);
            foreach (var i in plan.Items)
            {
                var s = AtFlat(Resolve(i.Holder), i.Flat);
                if (s == null || s.Ref.IsNone)
                {
                    Log.Info($"FAIL rider: no item at {i.Name}'s place {i.Holder.Template} flat {i.Flat}");
                    fail++;
                }
                else if (s.Ref.TryGet(_key!, out var v))
                {
                    var ok = v.Name == i.Name;
                    Log.Info($"{(ok ? "PASS" : "FAIL")} rider: the sword at {i.Name}'s place (flat {i.Flat}, {s.Ref}) carries {(ok ? "its own value" : $"MIXUP: {v.Name}'s value")}");
                    if (ok) pass++;
                    else fail++;
                }
                else
                {
                    var p = InstanceData.Parked(_key!).FirstOrDefault(x => x.Value?.Name == i.Name);
                    Log.Info(p != null ? $"ASIDE rider: {i.Name}'s record: {p}" : $"FAIL rider: {i.Name} has no value and no parked record");
                    if (p == null) fail++;
                }
            }
            foreach (var (item, v) in InstanceData.All(_key!))
                Log.Info($"INFO rider: InstanceData.All: {v.Name} on {item} at {item.Location()}");
        }
        catch (Exception e)
        {
            Log.Info($"FAIL rider: threw {e}");
            fail++;
        }
        finally
        {
            Log.Info($"instances-rider done: {pass} PASS, {fail} FAIL");
            if (!_stop) File.WriteAllText(Path.Combine(Context.DataDirectory, "checked.flag"), Environment.ProcessId.ToString());
        }
    }
}
