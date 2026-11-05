// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace PlaceholderTest;

/// <summary>
/// Lab test (no player) for missing-content placeholders (Forge's missing registry entries), over four boots. The
/// items come from the separate mod <c>placeholder-items</c> (Items/), which the operator removes and puts back:
/// <list type="number">
/// <item>with placeholder-items: give its sword (gear, level 7 Epic), 420 ore (its max stack is 500, vanilla 250) and
/// 280 gems (from its data pack) into a container, save. Then: disable placeholder-items (<c>.disabled</c>), restart.</item>
/// <item>without it: the three items are placeholders "[Missing: placeholder-items:...]" with the same ids, the
/// stacks are all there (420 not cut to 250), its recipe is not; content_status counts them; save. Then: restart
/// again, also with <c>kill -9</c> after this save.</item>
/// <item>still without it: the same again (the record survived the restart).</item>
/// <item>with placeholder-items back: the items are its own again (same ids, not placeholders), the stacks are intact;
/// they are taken out again.</item>
/// </list>
/// The phase is kept in kiln/data/placeholder-test/phase.json. Every check logs one PASS or FAIL line.
/// </summary>
public sealed class PlaceholderTestMod : Mod
{
    private static readonly (string Name, Guid Guid)[] Defs =
    [
        ("Kiln_PH_Sword", Guid.Parse("2f7c9e14-5a3b-4d68-b0e1-6c4a8f2d9b37")),
        ("Kiln_PH_Ore", Guid.Parse("8e4d1b6a-0f93-4c27-9d5e-3a7b2c1f8e60")),
        ("Kiln_PH_Gem", Guid.Parse("d3b0a4f1-7c2e-4e19-8a65-1f0b9c7e2d84")),
    ];
    private const string ItemsMod = "placeholder-items";

    private int _pass, _fail;
    private bool _ran, _stop;

    public override void OnLoad()
    {
        if (!Content.Available || !NewContent.Available) return;
        Content.OnReady(ev =>
        {
            if (_ran || ev.Reload) return;
            _ran = true;
            _ = RunAsync();
        });
    }

    public override void OnUnload() => _stop = true;

    private string Marker => Path.Combine(Context.DataDirectory, "phase.json");

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    private static bool ItemsLoaded => Mods.All.Any(m => m.Id == ItemsMod && m.IsLoaded);

    private async Task RunAsync()
    {
        try
        {
            // the definitions as the registries had them at world load (before the save was read)
            var atLoad = NewContent.Definitions();
            for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Read: true }; i++) await Steps(1);
            await Steps(25);
            var m = File.Exists(Marker) ? JsonDocument.Parse(File.ReadAllText(Marker)).RootElement.Clone() : (JsonElement?)null;
            var phase = m?.GetProperty("phase").GetInt32() ?? 0;
            if (m is { } mm && mm.GetProperty("pid").GetInt32() == Environment.ProcessId && mm.GetProperty("start").GetString() == ProcessStart())
            {
                Log.Info("placeholder-test: the marker is from this process (a reload): nothing to do");
                return;
            }
            Log.Info($"placeholder-test: phase {phase + 1}, {ItemsMod} {(ItemsLoaded ? "loaded" : "NOT loaded")}");
            switch (phase)
            {
                case 0 when ItemsLoaded: await Phase1(); break;
                case 1 or 2 when !ItemsLoaded: await Missing(m!.Value, phase + 1, atLoad); break;
                case 3 when ItemsLoaded: await Back(m!.Value, atLoad); break;
                default:
                    Log.Warn($"placeholder-test: phase {phase + 1} expects {ItemsMod} to be {(phase is 1 or 2 ? "absent" : "loaded")}; nothing done");
                    return;
            }
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        Log.Info($"placeholder-test done: {_pass} PASS, {_fail} FAIL");
    }

    private static ItemId Id(int i) => new(NewContent.IdOf(Defs[i].Guid));

    private async Task Phase1()
    {
        var defs = NewContent.Definitions();
        Check(Defs.All(d => defs.Any(x => x.Guid == d.Guid && x.State == "registered" && !x.IsPlaceholder && x.Mod == ItemsMod)),
            $"phase 1: {ItemsMod}'s items are registered: {string.Join(", ", Defs.Select(d => defs.FirstOrDefault(x => x.Guid == d.Guid)?.ToString() ?? d.Name))}");
        EntityId chest = EntityId.None;
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 4 && Enumerable.Range(0, Defs.Length).All(i => c.Count(Id(i)) == 0) &&
                (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                chest = e;
                break;
            }
        if (!Check(!chest.IsNone, $"phase 1: container {chest} with four free slots")) return;
        var before = Describe(Items.Read(chest)!);
        var g1 = await Items.Give(chest, Id(0), 1, new Gear(7, ItemRarity.Epic));
        var g2 = await Items.Give(chest, Id(1), 420);
        var g3 = await Items.Give(chest, Id(2), 280);
        Check(g1.Status == ItemsStatus.Done && g2.Status == ItemsStatus.Done && g2.Done == 420 && g3.Done == 280,
            $"phase 1: gave the sword (level 7 Epic), 420 ore (max stack 500, vanilla 250), 280 gems: {g1}; {g2}; {g3}");
        await Steps(5);
        var inv = Items.Read(chest)!;
        var ore = inv.Slots.Where(s => s.Item == Id(1)).ToList();
        Check(ore is [{ Count: 420 }], $"phase 1: the ore is one stack of 420: {Describe(inv)}");
        var saved = await Saves.Request();
        Check(saved.Success, $"phase 1: saved: {saved}");
        Write(1, chest, before);
        Log.Info($"placeholder-test: phase 1 done. Now disable {ItemsMod} (touch kiln/mods/{ItemsMod}/.disabled) and restart the server");
    }

    private async Task Missing(JsonElement m, int phase, IReadOnlyList<ContentDefinition> atLoad)
    {
        var chest = new EntityId(m.GetProperty("chest").GetUInt32());
        var defs = NewContent.Definitions();
        for (var i = 0; i < Defs.Length; i++)
        {
            var d = defs.FirstOrDefault(x => x.Guid == Defs[i].Guid);
            var load = atLoad.FirstOrDefault(x => x.Guid == Defs[i].Guid);
            Check(d is { IsPlaceholder: true, State: "registered", Mod: ItemsMod } && d.Id == Id(i).Value && load is { IsPlaceholder: true },
                $"phase {phase}: {Defs[i].Name} is a placeholder from world load on, same id: {d} (mod {d?.Mod}, at load {load?.State})");
        }
        using (var doc = JsonDocument.Parse(NewContent.DefinitionsJson()))
        {
            var names = doc.RootElement.EnumerateArray().Where(e => e.TryGetProperty("placeholder", out _))
                .Select(e => e.GetProperty("displayName").GetString()).ToList();
            Check(names.Contains($"[Missing: {ItemsMod}:Kiln_PH_Sword]") && names.Contains($"[Missing: {ItemsMod}:Kiln_PH_Gem]"),
                $"phase {phase}: placeholder names: {string.Join(", ", names)}");
        }
        var ore = Content.Items.Get("Kiln_PH_Ore");
        var sword = Content.Items.Get("Kiln_PH_Sword");
        Check(ore?.MaxStack == 500 && sword?.Get("flags").Name?.Contains("HasPIDE") == true && Content.Recipes.Get("Kiln_PH_Ore_Recipe") == null,
            $"phase {phase}: the placeholders keep max stack ({ore?.MaxStack}) and gear flags ({sword?.Get("flags")}); the recipe is gone");
        var st = Content.Status()!;
        var mine = NewContent.Placeholders().Count(p => p.Mod == ItemsMod);
        Check(st.Placeholders >= 3 && mine == 3,
            $"phase {phase}: content status: {st.Placeholders} placeholder(s) ({mine} of {ItemsMod}), {st.Definitions} definition(s)");
        var inv = Items.Read(chest);
        if (!Check(inv != null, $"phase {phase}: container {chest} is there")) return;
        var s1 = inv!.Slots.FirstOrDefault(s => s.Item == Id(0));
        var s2 = inv.Slots.FirstOrDefault(s => s.Item == Id(1));
        var s3 = inv.Slots.FirstOrDefault(s => s.Item == Id(2));
        Check(s1 is { Count: 1, IsGear: true, Level: 7 } && s2 is { Count: 420 } && s3 is { Count: 280 },
            $"phase {phase}: the stacks survived without the mod: sword {s1?.Count} level {s1?.Level} {s1?.Rarity}, ore {s2?.Count}, gems {s3?.Count}");
        await Steps(50);
        var again = Items.Read(chest)!;
        Check(again.Count(Id(1)) == 420 && again.Count(Id(0)) == 1, $"phase {phase}: 2 s later still there: {Describe(again)}");
        var saved = await Saves.Request();
        Check(saved.Success, $"phase {phase}: saved with the placeholders: {saved}");
        Write(phase, chest, m.GetProperty("before").GetString()!);
        Log.Info(phase == 2
            ? "placeholder-test: phase 2 done. Restart again without the mod (kill -9 now is fine: the save is written)"
            : $"placeholder-test: phase 3 done. Now enable {ItemsMod} again (remove .disabled) and restart");
    }

    private async Task Back(JsonElement m, IReadOnlyList<ContentDefinition> atLoad)
    {
        var chest = new EntityId(m.GetProperty("chest").GetUInt32());
        var defs = NewContent.Definitions();
        for (var i = 0; i < Defs.Length; i++)
        {
            var d = defs.FirstOrDefault(x => x.Guid == Defs[i].Guid);
            Check(d is { IsPlaceholder: false, State: "registered", Mod: ItemsMod } && d.Id == Id(i).Value,
                $"phase 4: {Defs[i].Name} is {ItemsMod}'s again, same id: {d}");
        }
        Check(NewContent.Placeholders().All(p => p.Mod != ItemsMod) && Content.Recipes.Get("Kiln_PH_Ore_Recipe") != null,
            $"phase 4: no placeholders of {ItemsMod} left ({Content.Status()!.Placeholders} of other mods); the recipe is back");
        var inv = Items.Read(chest)!;
        var s1 = inv.Slots.FirstOrDefault(s => s.Item == Id(0));
        var s2 = inv.Slots.FirstOrDefault(s => s.Item == Id(1));
        var s3 = inv.Slots.FirstOrDefault(s => s.Item == Id(2));
        Check(s1 is { Count: 1, IsGear: true, Level: 7 } && s2 is { Count: 420 } && s3 is { Count: 280 },
            $"phase 4: the stacks are the mod's items again: sword level {s1?.Level}, ore {s2?.Count}, gems {s3?.Count}");
        foreach (var s in new[] { s1, s2, s3 })
            if (s != null) Check((await Items.TakeSlot(chest, s)).Status == ItemsStatus.Done, $"phase 4: take {s.Item} x{s.Count} out of {s.Slot}");
        var end = Describe(Items.Read(chest)!);
        Check(end == m.GetProperty("before").GetString(), $"phase 4: container {chest} ends as it started: {end}");
        File.Delete(Marker);
    }

    private void Write(int phase, EntityId chest, string before)
    {
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["phase"] = phase, ["chest"] = chest.Value, ["before"] = before, ["pid"] = Environment.ProcessId, ["start"] = ProcessStart(),
        }));
    }

    private static string Describe(InventoryContents c) =>
        string.Join(" ", c.Slots.Select(s => s.IsEmpty ? "-" : $"{s.Item.Value:x8}x{s.Count}"));

    private static string ProcessStart() => Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");
}
