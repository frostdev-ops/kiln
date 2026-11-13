// SPDX-License-Identifier: MIT
// InstanceData.Claim and Discard against the game (dev.27), three boots of t4.data (run-t4.sh), started on their own by
// writing {"Phase": "x1"} into kiln/data/instances-test/t4/data.json:
//   x1  twins X1 X2 (tagged) in chest X; a save; both moved through chest Y to other slots of X; a save; drop-gen.
//   x2  the older generation loads: both records set aside (Kiln cannot tell the twins apart). X1's is claimed onto the
//       sword in X1's place, X2's discarded; /instances lists nothing parked; a save.                                stop
//   x3  after a clean restart: X1's value is on that sword (Exact), nothing parked, the discarded value nowhere.     end
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    private async Task X1()
    {
        var quiet = (await QuietContainers(7, 200)).Where(e => Spawn.Inspect(e)?.TemplateName != null).Take(2).ToList();
        Check(quiet.Count == 2, $"t4 x1: {quiet.Count} quiet containers with 7 free slots");
        if (quiet.Count < 2) { Ready("end"); return; }
        _ds.Chests["X"] = PosOf(quiet[0]);
        _ds.Chests["Y"] = PosOf(quiet[1]);
        var sword = Sword()!;
        var gear = TwinGear(sword);
        await Give("X1", "X", true, gear, sword, _ds.Extra);
        await Give("X2", "X", true, gear, sword, _ds.Extra);
        var r1 = await SaveNow("claim: twins given");
        var free = FreeGeneric(Chest("X")).Select(s => FlatOf(Items.Read(Chest("X"))!, s.Slot)).ToList();
        foreach (var (n, to) in new[] { ("X1", free[^1]), ("X2", free[^2]) })
        {
            await MoveTo(n, "Y", list: _ds.Extra);
            await MoveTo(n, "X", to, _ds.Extra);
        }
        Info($"t4 x1: twins now {string.Join(", ", _ds.Extra.Where(s => s.Name is "X1" or "X2"))}");
        var r2 = await SaveNow("claim: twins moved");
        _ds.Notes["claim"] = $"{r1.Revision} then {r2.Revision} (dropped)";
        _ds.Phase = "x2";
        SaveState();
        Ready($"drop-gen {r2.Revision}");
    }

    private async Task X2()
    {
        Info($"t4 x2: loaded {_worldLoaded} (expected the older generation: {_ds.Notes.GetValueOrDefault("claim")})");
        List<ParkedInstance<T4Tag>> Twins() => InstanceData.Parked(Key).Where(p => p.Value?.Name is "X1" or "X2").ToList();
        await Until(() => Twins().Count(p => p.State == ParkedState.SetAside) >= 2, 25 * 60);
        var twins = Twins();
        Check(twins.Count == 2 && twins.All(p => p.State == ParkedState.SetAside),
            $"t4 x2: both twins' records are set aside: {string.Join("; ", twins.Select(p => $"{p.Value?.Name} {p.RecordId} {p.State}: {p.Reason}"))}");
        if (twins.Count != 2) { Next("end", "end"); return; }
        var x1 = _ds.Extra.First(s => s.Name == "X1");
        var target = AtFlat(Chest("X"), x1.Flat);
        Check(target is { Ref.IsNone: false } && !target.Ref.Has(Key), $"t4 x2: the sword in X1's place (flat {x1.Flat}) carries no value: {target?.Ref}");
        if (target is null) { Next("end", "end"); return; }
        var restored = new List<InstanceDataRestoredEvent>();
        var lost = new List<InstanceDataLostEvent>();
        using (Events.On<InstanceDataRestoredEvent>(e => restored.Add(e)))
        using (Events.On<InstanceDataLostEvent>(e => lost.Add(e)))
        {
            var claim = InstanceData.Claim(twins.First(p => p.Value!.Name == "X1"), target.Ref);
            Check(claim.Ok && claim.Keys.SequenceEqual(["t4.tag"]), $"t4 x2: InstanceData.Claim: {claim}");
            Check(target.Ref.TryGet(Key, out var v) && v.Name == "X1", $"t4 x2: the sword in X1's place carries X1's value now: {v?.Name} #{v?.Count}");
            var again = Assert(() => InstanceData.Claim(twins.First(p => p.Value!.Name == "X1"), target.Ref));
            Check(again is ArgumentException, $"t4 x2: claiming the same record again throws ArgumentException: {again?.Message}");
            Check(InstanceData.Discard(twins.First(p => p.Value!.Name == "X2")), "t4 x2: InstanceData.Discard of X2's record: true");
            Check(!InstanceData.Discard(twins.First(p => p.Value!.Name == "X2")), "t4 x2: a second Discard: false");
        }
        Check(restored.Count == 1 && restored[0].Match == InstanceMatch.Claimed && restored[0].Item == target.Ref,
            $"t4 x2: InstanceDataRestoredEvent Claimed: {string.Join("; ", restored.Select(e => $"{e} {e.Detail}"))}");
        Check(lost.Count == 1 && lost[0].Reason == InstanceLossReason.Discarded,
            $"t4 x2: InstanceDataLostEvent Discarded: {string.Join("; ", lost.Select(e => e.ToString()))}");
        Check(Twins().Count == 0, $"t4 x2: nothing of the twins is parked any more ({Twins().Count})");
        _ds.Notes["claimed"] = $"{target.Ref.Entity.Id.Value}@{x1.Flat}";
        await SaveNow("claim: after claim and discard");
        Next("x3", "stop");
    }

    private async Task X3()
    {
        var x1 = _ds.Extra.First(s => s.Name == "X1");
        var x2 = _ds.Extra.First(s => s.Name == "X2");
        await Until(() => AtFlat(Chest("X"), x1.Flat) is { } s && (s.Ref.Has(Key) || _restored.Count > 0), 25 * 60);
        var at1 = AtFlat(Chest("X"), x1.Flat);
        var at2 = AtFlat(Chest("X"), x2.Flat);
        Check(at1 is not null && at1.Ref.TryGet(Key, out var v1) && v1.Name == "X1",
            $"t4 x3: after a restart the claimed value is on the sword in X1's place ({(at1 is not null && _restored.TryGetValue(at1.Ref, out var m) ? m : "not restored")})");
        Check(at2 is not null && !at2.Ref.Has(Key), "t4 x3: the discarded value is nowhere: X2's sword has none");
        var parked = InstanceData.Parked(Key).Where(p => p.Value?.Name is "X1" or "X2").ToList();
        Check(parked.Count == 0, $"t4 x3: nothing of the twins is parked ({parked.Count})");
        Next("end", "end");
    }

    private static Exception? Assert(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e; }
    }
}
