// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace ContentTest;

/// <summary>
/// kiln.content lab test (no player connected). Phase "fresh" (no marker file): registries and schema, the mod's own
/// data pack (content/test-pack.json), code changes (max stack, recipe ingredients/results/duration, random loot),
/// a give of more than the vanilla max into one container slot through Kiln.Items and read back as one stack,
/// refusals, conflicts, the dump. It leaves its code changes in place and writes a marker; then:
///  - after <c>reload content-test</c> (same process) the new instance checks that the code changes came off with
///    the old instance while the data pack stayed (it follows the loaded mod), re-applies and checks again;
///  - after a server restart it checks that the data pack was applied at world load and re-applies its code changes.
/// Every check logs one PASS or FAIL line; the container ends as it started.
/// </summary>
public sealed class ContentTestMod : Mod
{
    private const string Wood = "Material_T1_Wood_Logs", Twigs = "Material_T1_Twigs", Resin = "Material_T1_Resin";
    private const string Rod = "Tool_basic_FishingRod_T0_PK", Flint = "Ammo_Arrow_T1_Flint";
    private int _pass, _fail;
    private bool _stop, _ran;

    public override void OnLoad()
    {
        Log.Info($"content-test: OnLoad (content {(Content.Available ? "available" : "MISSING")}, ready {Content.Available && Content.Ready})");
        if (!Content.Available) return;
        var marker = Marker();
        if (File.Exists(marker) && Content.Ready) CheckAfterReload(marker);  // the previous instance's code changes are gone
        Content.OnReady(e =>
        {
            if (_ran) return;  // a later pack reload: nothing to do
            _ran = true;
            _ = RunAsync(e);
        });
    }

    public override void OnUnload()
    {
        _stop = true;
        Log.Info("content-test: OnUnload (its code changes come off now)");
    }

    private string Marker() => Path.Combine(Context.DataDirectory, "phase.json");

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    private async Task RunAsync(ContentReadyEvent e)
    {
        try
        {
            var marker = Marker();
            if (!File.Exists(marker)) await Fresh(e);
            else
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                var pid = doc.RootElement.GetProperty("pid").GetInt32();
                var start = doc.RootElement.GetProperty("start").GetString();
                var sameProcess = pid == Environment.ProcessId && start == ProcessStart();
                if (sameProcess) ReapplyAfterReload();
                else AfterRestart(e);
            }
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        Log.Info($"content-test done: {_pass} PASS, {_fail} FAIL");
    }

    private static string ProcessStart() => Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");

    private void WriteMarker(string phase)
    {
        Directory.CreateDirectory(Context.DataDirectory);
        // Hand-written: JsonSerializer.Serialize of a type from this (collectible) assembly would make System.Text.Json
        // cache it and keep the unloaded mod alive (reload-test counts that as a leak).
        File.WriteAllText(Marker(), $"{{\"phase\": \"{phase}\", \"pid\": {Environment.ProcessId}, \"start\": \"{ProcessStart()}\"}}");
    }

    // ------------------------------------------------------------------------------------------- fresh ---------

    private async Task Fresh(ContentReadyEvent e)
    {
        var st = Content.Status()!;
        Check(st is { Ready: true, HasItems: true, HasRecipes: true, HasLoot: true },
            $"ready (generation {e.Generation}): {st.Items} items, {st.Recipes} recipes, {st.Loot} random-loot items, drop tables " +
            $"{(st.HasDrops ? $"on ({st.Drops} seen)" : "off")}, {st.Packs} pack file(s), {st.PackErrors} pack error(s), " +
            $"{st.Changed} field(s) changed, stack guard {st.StackGuard}");

        // ---- registries against Kiln.Items and the dashboard catalog ----
        // kiln.content is ready at world load (~0.3 s after the first step); kiln.items needs its program to run.
        for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Catalog: true }; i++) await Steps(1);
        Check(Items.Status() is { Transactions: true, Catalog: true }, $"kiln.items ready for the give test ({Items.Status()?.Detail})");
        Check(Content.Items.Count == Items.All.Count, $"items: {Content.Items.Count} in kiln.content, {Items.All.Count} in kiln.items");
        var wood = Content.Items.Get(Wood);
        Check(wood is { Id: 0x2b0cf1d5 } && Content.Items.Get("0x2b0cf1d5")?.Name == Wood && Content.Items.Get("material_t1_wood_logs") != null,
            $"lookup: {wood} by name, hex id and case-insensitive name");
        CompareWithDashboard();
        var f = Content.Items.Field("maxStackSize");
        Check(f is { Type: ContentValueType.Int, Bytes: 2, Signed: false, Writable: true } && Content.Items.Field("itemId") is { Writable: false },
            $"schema: {Content.Items.Fields.Count} item fields from the game's reflection; {f}; itemId read-only");
        var rarity = Content.Items.Field("rarity");
        Check(rarity is { EnumKind: ContentEnumKind.Enum } && rarity.Values.Any(v => v.Name == "Legendary") &&
              wood!.Get("category").Name == "Materials",
            $"schema: enums: rarity = {string.Join("/", rarity?.Values.Select(v => v.Name) ?? [])}; wood category {wood?.Get("category")}");
        Log.Info("item fields (first 30): " + string.Join(", ", Content.Items.Fields.Take(30).Select(x => x.Path)));
        Log.Info("recipe fields: " + string.Join(", ", Content.Recipes.Fields.Select(x => x.Path)));
        Log.Info("loot fields: " + string.Join(", ", Content.Loot.Fields.Select(x => x.Path)));

        // ---- the mod's own data pack ----
        var twigs = Content.Items.Get(Twigs)!;
        Check(twigs.MaxStack == 600 && twigs.Original("maxStackSize").Integer == 250,
            $"data pack: {Twigs}.maxStackSize {twigs.MaxStack} (original {twigs.Original("maxStackSize")})");
        var flint = Content.Recipes.Get(Flint)!;
        Check(flint.Outputs.Count == 1 && flint.Outputs[0].Count == 100,
            $"data pack: {Flint} outputs {string.Join(", ", flint.Outputs)} (x2 of 50)");
        var rod = Content.Recipes.Get(Rod)!;
        Check(rod.Get("showIsImportantLabel").Bool && !rod.Original("showIsImportantLabel").Bool,
            $"data pack: @where output = Tool_T1_FishingRod_basic matched {Rod} (showIsImportantLabel true)");
        var twigLoot = Content.Loot.Get(Twigs)!;
        Check(twigLoot.GetNumber("stackSizeMax") == Math.Max(3, twigLoot.Original("stackSizeMax").Integer * 3),
            $"data pack: loot {Twigs}.stackSizeMax {twigLoot.Get("stackSizeMax")} (original {twigLoot.Original("stackSizeMax")}, x3 min 3)");
        var report = Content.PackReport();
        Check(report.Contains("content-test/content/test-pack.json") && report.Contains("Material_T1_Nonexistent_Item: matches nothing"),
            "data pack report lists the pack and warns about the selector that matches nothing");

        // ---- code changes: max stack + a give of more than the old max into one slot ----
        var woodOrig = (int)wood!.Original("maxStackSize").Integer;
        wood.MaxStack = 2000;
        Check(wood.MaxStack == 2000 && woodOrig == 250 && Content.Items.Get(Wood)!.Modified,
            $"code: {Wood}.MaxStack {wood.MaxStack} (original {woodOrig}), entry marked modified");
        Check(Content.StackGuard, "stack guard switched itself on after a max stack raise");
        var chest = await GiveBigStack(wood);

        // ---- refusals ----
        Check(Throws(() => wood.MaxStack = 70000), "code: maxStackSize 70000 refused (u16)");
        Check(Throws(() => wood.Set("itemId", 1L)), "code: itemId refused (read-only)");
        Check(Throws(() => wood.Set("noSuchField", 1L)), "code: unknown field refused");
        var bad = Content.Apply("{\"items\": {\"Material_T1_Wood_Logs\": {\"maxStackSize\": \"lots\", \"nope\": 1}}, \"weird\": {}}", "bad pack");
        Check(!bad.Ok && bad.Errors.Count >= 2 && bad.Warnings.Any(w => w.Contains("weird")),
            $"apply: bad document reported ({string.Join(" | ", bad.Errors.Concat(bad.Warnings))})");

        // ---- conflict: the pack says 400, code says 500 ----
        var resin = Content.Items.Get(Resin)!;
        resin.MaxStack = 500;
        var conflicts = Content.Apply("{}", "probe").Conflicts;
        var c = conflicts.FirstOrDefault(x => x.Field.Contains("Material_T1_Resin.maxStackSize"));
        Check(resin.MaxStack == 500 && c != null && c.WinnerValue == "500" && c.LoserValue == "400",
            $"conflict reported: {c?.ToString() ?? "none"}");
        Check(resin.Reset("maxStackSize") && resin.MaxStack == 400, $"reset: {Resin} back to the pack's {resin.MaxStack}");

        // ---- recipes: ingredients (a longer list), duration ----
        var rodIn = rod.Inputs;
        Log.Info($"{Rod}: inputs {string.Join(", ", rodIn)} -> outputs {string.Join(", ", rod.Outputs)}; workshop {rod.Get("workshopId")}, duration {rod.CraftingDuration.TotalSeconds}s");
        rod.SetInputs([RecipeStack.Of(Twigs, 1), RecipeStack.Of("Material_T1_String", 1), RecipeStack.Of(Wood, 3), RecipeStack.Of(Resin, 4)]);
        rod.CraftingDuration = TimeSpan.FromSeconds(5);
        var now = rod.Inputs;
        Check(now.Count == 4 && now[2].Count == 3 && Content.Items.Get(now[3].Item.Value)?.Name == Resin && rod.CraftingDuration.TotalSeconds == 5,
            $"recipe: {Rod} inputs now {string.Join(", ", now)}, duration {rod.CraftingDuration.TotalSeconds}s");
        rod.SetOutputs([RecipeStack.Of("Tool_T1_FishingRod_basic", 2)]);
        Check(rod.Outputs is [{ Count: 2 }], $"recipe: {Rod} outputs now {string.Join(", ", rod.Outputs)}");
        Check(Throws(() => rod.SetInputs([RecipeStack.Of(Twigs, 0)])) && Throws(() => rod.SetInputs([])),
            "recipe: amount 0 and an empty list refused");
        var charcoal = Content.Recipes.Get("Material_T1_Factory_03_Charcoal");
        if (charcoal != null)
        {
            var before = charcoal.CraftingDuration;
            charcoal.CraftingDuration = TimeSpan.FromSeconds(30);
            Check(before.TotalSeconds == 120 && charcoal.CraftingDuration.TotalSeconds == 30 && charcoal.Inputs.All(i => i.Category != 0),
                $"recipe: charcoal kiln duration {before.TotalSeconds}s -> {charcoal.CraftingDuration.TotalSeconds}s, inputs {string.Join(", ", charcoal.Inputs)}");
        }

        // ---- random loot ----
        var woodLoot = Content.Loot.Get(Wood)!;
        woodLoot.Set("stackSizeMin", 5L);
        woodLoot.Set("stackSizeMax", 20L);
        Check(woodLoot.GetNumber("stackSizeMin") == 5 && woodLoot.GetNumber("stackSizeMax") == 20,
            $"loot: {Wood} random stacks {woodLoot.Get("stackSizeMin")}..{woodLoot.Get("stackSizeMax")} (originally {woodLoot.Original("stackSizeMin")}..{woodLoot.Original("stackSizeMax")})");

        // ---- drop tables (filled as the game uses them; without players maybe none) ----
        await DropProbe(chest);
        var drops = Content.Drops.Count;
        if (drops == 0) Log.Info("SKIP drop tables: the game has not used any default inventory yet (no player)");
        else
        {
            var table = Content.Drops[0];
            var stacks = table.Stacks;
            var orig = stacks.Select(s => s.CountMax).ToList();
            Check(stacks.Count > 0, $"drops: {drops} table(s) seen; {table.Name}: {string.Join(", ", stacks.Take(6).Select(s => $"{Content.Items.Get(s.Item.Value)?.Name} {s.CountMin}..{s.CountMax} p{s.Chance}"))}");
        }

        // ---- dump ----
        var path = Content.Dump();
        var info = new FileInfo(path);
        Check(info.Exists && info.Length > 1_000_000, $"dump: {path} ({info.Length / 1024} KB)");

        // the code changes stay for the observer; the next phase starts on reload / restart
        WriteMarker("applied");
        Log.Info("content-test: fresh phase done; code changes left in place (wood 2000, rod inputs x4 / outputs 2, rod 5 s, charcoal 30 s, wood loot 5..20)");
    }

    private async Task<EntityId> GiveBigStack(ContentEntry wood)
    {
        var woodId = new ItemId(wood.Id);
        var found = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty);
        EntityId chest = default;
        InventorySlot? free = null;
        var snaps = new Dictionary<EntityId, string>();
        foreach (var e in found)
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 1 && c.Count(woodId) == 0)
                snaps[e] = string.Join(" ", c.Slots.Select(s => $"{s.Item.Value:x}x{s.Count}"));
        await Steps(25);
        foreach (var (e, snap) in snaps)
            if (Items.Read(e) is { } c && string.Join(" ", c.Slots.Select(s => $"{s.Item.Value:x}x{s.Count}")) == snap)
            {
                chest = e;
                free = c.Slots.First(s => s.IsEmpty);
                break;
            }
        Check(!chest.IsNone && free != null, $"give: chose container {chest}, empty slot {free?.Slot}");
        if (chest.IsNone || free == null) return chest;
        var r = await Items.Give(chest, woodId, 1500, slot: free.Slot, options: GiveOptions.AllOrNothing);
        Check(r.Status == ItemsStatus.Done && r.Changes.Count == 1 && r.Changes[0].Amount == 1500 && r.Changes[0].Slot == free.Slot,
            $"give 1500 {Wood} (vanilla max 250) into {free.Slot}: {r.Status} {r.Done}/{r.Requested} in {r.Changes.Count} slot(s) [{string.Join(", ", r.Changes.Select(x => $"{x.Slot}+{x.Amount}"))}]");
        var back = Items.Read(chest)!.Slots.First(s => s.Slot == free.Slot);
        Check(back.Item == woodId && back.Count == 1500, $"read back: {back.Slot} holds {back.Count} x {Wood} as one stack");
        await Steps(5);
        var again = Items.Read(chest)!.Slots.First(s => s.Slot == free.Slot);
        Check(again.Count == 1500, $"still one stack of {again.Count} five steps later (no clamp)");
        var t = await Items.Take(chest, woodId, 1500, from: free.Slot, exactSlot: true);
        var end = Items.Read(chest)!;
        Check(t.Status == ItemsStatus.Done && end.Count(woodId) == 0, $"take the 1500 back out: {t.Status}; container {chest} holds {end.Count(woodId)} wood again");
        return chest;
    }

    /// <summary>Drop tables load on demand: spawn an enemy loot container (it carries DefaultInventory) next to the
    /// test chest; the game's default_inventory program acquires its table, kiln.content's hook registers it and
    /// applies the pack's "drops": {"*": {"count": {"mul": 2}}} before the roll.</summary>
    private async Task DropProbe(EntityId chest)
    {
        if (!Spawn.Available || !Spawn.WorldRunning || chest.IsNone || Spawn.Inspect(chest) is not { } at)
        {
            Log.Info("SKIP drop probe: kiln.spawn unavailable or the world is not running");
            return;
        }
        var template = Spawn.Lookup("LootContainer_Enemy_Scavenger_Melee01") ??
                       Spawn.Templates.FirstOrDefault(t => t.Name.StartsWith("LootContainer_Enemy_", StringComparison.Ordinal));
        if (template == null)
        {
            Log.Info("SKIP drop probe: no LootContainer_Enemy_* template");
            return;
        }
        var before = Content.Drops.Count;
        EntityId e;
        try
        {
            e = await Spawn.Entity(template.Name, at.Position with { Y = at.Position.Y + 1.5 }, options: new SpawnOptions { Tag = "content-test" });
        }
        catch (Exception ex)
        {
            Log.Info($"SKIP drop probe: spawning {template.Name} failed: {ex.Message}");
            return;
        }
        InventoryContents? inv = null;
        for (var i = 0; i < 100 && !_stop; i++)
        {
            inv = Items.Read(e);
            if (Content.Drops.Count > before && inv is { UsedSlots: > 0 }) break;
            await Steps(1);
        }
        Log.Info($"drop probe: spawned {template.Name} as {e} at {at.Position}; drop tables {before} -> {Content.Drops.Count}; " +
                 $"inventory {(inv == null ? "none" : string.Join(", ", inv.Slots.Where(s => !s.IsEmpty).Select(s => $"{Items.Get(s.Item)?.Name} x{s.Count}")))}");
        if (Content.Drops.Count > before)
        {
            var table = Content.Drops[Content.Drops.Count - 1];
            var stacks = table.Stacks;
            var lines = new List<string>();
            var ok = true;
            for (var i = 0; i < stacks.Count; i++)
            {
                var s = stacks[i];
                var o = table.Original($"[{i}].countMax").Integer;
                var omin = table.Original($"[{i}].countMin").Integer;
                ok &= s.CountMax == o * 2 && s.CountMin == omin * 2;
                lines.Add($"{Items.Get(s.Item)?.Name ?? s.Item.ToString()} {omin}..{o} -> {s.CountMin}..{s.CountMax} p{s.Chance:0.##}");
            }
            Check(stacks.Count > 0 && ok, $"drops: table {table.Name} registered on first use, pack rule x2 applied before the roll: {string.Join("; ", lines.Take(8))}");
            if (inv is { UsedSlots: > 0 })
            {
                // The world's own "Loot Amount" (gameSettings.resourceDropStackAmountFactor) scales the roll on top.
                var factor = LootFactor();
                var rolled = inv.Slots.Where(s => !s.IsEmpty).ToList();
                var within = rolled.All(s =>
                {
                    var tableMax = stacks.Where(x => x.Item == s.Item).Sum(x => x.CountMax);
                    return tableMax > 0 && s.Count <= Math.Ceiling(tableMax * factor);
                });
                Check(within, $"drops: the container rolled {string.Join(", ", rolled.Select(s => $"{Items.Get(s.Item)?.Name} x{s.Count}"))} " +
                              $"= the doubled table x the world's Loot Amount {factor} (vanilla: x{factor} of the original)");
            }
        }
        else Log.Info("SKIP drops: no table appeared (the area is not simulated without a player?)");
        Spawn.Destroy(e);
    }

    // ------------------------------------------------------------------------------------------- reload --------

    /// <summary>OnLoad of a reloaded instance: the old instance's code layers are gone, the pack stayed.</summary>
    private void CheckAfterReload(string marker)
    {
        var wood = Content.Items.Get(Wood)!;
        var rod = Content.Recipes.Get(Rod)!;
        var woodLoot = Content.Loot.Get(Wood)!;
        Check(wood.MaxStack == 250, $"after reload: {Wood}.maxStackSize back to {wood.MaxStack} (the old instance's 2000 came off)");
        Check(rod.Inputs.Count == 3 && rod.Outputs is [{ Count: 1 }] && rod.CraftingDuration == TimeSpan.Zero,
            $"after reload: {Rod} back to {string.Join(", ", rod.Inputs)} -> {string.Join(", ", rod.Outputs)}, {rod.CraftingDuration.TotalSeconds}s");
        Check(woodLoot.GetNumber("stackSizeMax") == woodLoot.Original("stackSizeMax").Integer,
            $"after reload: loot {Wood}.stackSizeMax back to {woodLoot.Get("stackSizeMax")}");
    }

    private void ReapplyAfterReload()
    {
        // The ready handler of a mod loaded after the world runs once its own data pack is in (a reload keeps it; a
        // load after an unload brings it back first).
        Check(Content.Items.Get(Twigs)!.MaxStack == 600, "after reload / load: the data pack is in (it follows the loaded mod): twigs 600");
        var wood = Content.Items.Get(Wood)!;
        wood.MaxStack = 2000;
        Check(wood.MaxStack == 2000, $"after reload: re-applied {Wood}.maxStackSize {wood.MaxStack}");
        WriteMarker("reloaded");
    }

    // ------------------------------------------------------------------------------------------- restart -------

    private void AfterRestart(ContentReadyEvent e)
    {
        var twigs = Content.Items.Get(Twigs)!;
        var flint = Content.Recipes.Get(Flint)!;
        var wood = Content.Items.Get(Wood)!;
        Check(e.Generation == 1 && twigs.MaxStack == 600 && flint.Outputs[0].Count == 100 && wood.MaxStack == 250,
            $"after restart: the data pack was applied at world load (generation {e.Generation}): twigs {twigs.MaxStack}, " +
            $"{Flint} x{flint.Outputs[0].Count}; code values not yet (wood {wood.MaxStack})");
        wood.MaxStack = 2000;
        Content.Recipes.Get(Rod)!.CraftingDuration = TimeSpan.FromSeconds(5);
        Check(wood.MaxStack == 2000 && Content.Recipes.Get(Rod)!.CraftingDuration.TotalSeconds == 5,
            "after restart: code changes re-applied in OnReady (wood 2000, rod 5 s)");
        WriteMarker("restarted");  // delete kiln/data/content-test/ for a fresh run
    }

    // ------------------------------------------------------------------------------------------- helpers -------

    private void CompareWithDashboard()
    {
        var file = Path.Combine(Context.Info.Directory, "expected-items.json");
        if (!File.Exists(file))
        {
            Log.Info("SKIP dashboard catalog: expected-items.json not shipped");
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        int known = 0, stackEq = 0, nameEq = 0, catEq = 0;
        var diffs = new List<string>();
        foreach (var x in doc.RootElement.EnumerateArray())
        {
            var e = Content.Items.Get((uint)x.GetProperty("id").GetInt64());
            if (e == null) continue;
            known++;
            if (e.Name == x.GetProperty("debug").GetString()) nameEq++;
            var orig = e.Original("maxStackSize").Integer;
            if (orig == x.GetProperty("maxStack").GetInt32()) stackEq++;
            else if (diffs.Count < 5) diffs.Add($"{e.Name} {orig} vs {x.GetProperty("maxStack").GetInt32()}");
            if (e.Get("category").Name == x.GetProperty("category").GetString()) catEq++;
        }
        Check(known > 4000 && stackEq == known && nameEq == known && catEq == known,
            $"dashboard catalog: {known} known, debug name equal {nameEq}, original max stack equal {stackEq}, category equal {catEq}" +
            (diffs.Count > 0 ? $" (e.g. {string.Join(", ", diffs)})" : ""));
    }

    private double LootFactor()
    {
        try
        {
            var cfg = Path.GetFullPath(Path.Combine(Context.Info.Directory, "..", "..", "..", "enshrouded_server.json"));
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("gameSettings", out var g) && g.TryGetProperty("resourceDropStackAmountFactor", out var f)
                ? f.GetDouble() : 1.0;
        }
        catch (Exception)
        {
            return 1.0;
        }
    }

    private static bool Throws(Action a)
    {
        try
        {
            a();
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}
