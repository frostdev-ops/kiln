// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using Kiln;

namespace NewContentTest;

/// <summary>
/// Lab test (no player) for kiln.content's new items and recipes (Forge's DeferredRegister).
/// <list type="bullet">
/// <item>OnLoad registers two items and two recipes from code (plus refusals: a bad GUID, a taken name, a missing base);
/// the mod's data pack (content/items.json) adds one item, one recipe and a rule on the new item.</item>
/// <item>Fresh run (no marker): when the registries are ready the new entries are in Content.Items / Content.Recipes and
/// kiln.items' catalog with their ids (FNV-1a of the GUID), texts, field values and recipe lists; the new sword (gear,
/// level 5 Rare) and 7 new ingots go into a container through Kiln.Items and are read back; the world is saved and a
/// marker written.</item>
/// <item>After a restart: the item came back at world load (before the save was read), the container still holds the
/// sword and the ingots, the recipes are readable; then they are taken out again (the container ends as it started).</item>
/// <item>After <c>reload newcontent-test</c>: the definitions are adopted by the new instance and the recipes listed
/// again.</item>
/// </list>
/// Every check logs one PASS or FAIL line.
/// </summary>
public sealed class NewContentTestMod : Mod
{
    // fixed GUIDs (never change them: saved stacks keep the ids)
    private static readonly Guid SwordGuid = Guid.Parse("59a6d47d-0b55-4211-bfd0-16e9bb6e12c9");
    private static readonly Guid IngotGuid = Guid.Parse("c2a48511-1999-4458-ab26-78bae3550baa");
    private static readonly Guid SwordRecipeGuid = Guid.Parse("979e76f9-5089-4383-85a6-ecc4d60883ec");
    private static readonly Guid IngotRecipeGuid = Guid.Parse("dd5f778b-1171-4b9f-9185-c193de3c917d");
    private static readonly Guid BrokenGuid = Guid.Parse("403c1691-6d1f-4f13-8cc8-c3fd5aba39bd");
    private const string Sword = "Kiln_Test_Emberblade", Ingot = "Kiln_Test_Ingot", PackGem = "Kiln_Test_PackGem";
    private const string SwordBase = "Weapon_T1_1H_SwordEpic_01", IngotBase = "Material_T1_Metal_Scraps";

    private int _pass, _fail;
    private bool _stop, _ran;
    private readonly List<string> _early = new();

    public override void OnLoad()
    {
        Log.Info($"newcontent-test: OnLoad (content {(Content.Available ? "available" : "MISSING")}, new content {NewContent.Available}, ready {Content.Available && Content.Ready})");
        if (!Content.Available) return;
        Check(NewContent.Available, "kiln.content v3 with new content on this build");
        if (!NewContent.Available) return;
        var reload = File.Exists(Marker()) && Content.Ready;
        if (File.Exists(Path.Combine(Context.DataDirectory, "without-definitions")) && File.Exists(Marker()) && !reload)
        {
            // a restart without the definitions (as if the mod were removed): what the game does with the stacks
            Content.OnReady(ev =>
            {
                if (_ran) return;
                _ran = true;
                _ = WithoutDefinitions();
            });
            return;
        }
        Register(reload);
        if (reload) _ = CheckAfterReload();
        Content.OnReady(ev =>
        {
            if (_ran || reload) return;
            _ran = true;
            _ = RunAsync();
        });
    }

    public override void OnUnload()
    {
        _stop = true;
        Log.Info("newcontent-test: OnUnload (its items stay registered; its recipes leave their station)");
    }

    private string Marker() => Path.Combine(Context.DataDirectory, "phase.json");

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

    // ---- registration (OnLoad: before the registries load on a fresh boot) ----
    private void Register(bool reload)
    {
        var sword = Content.Items.Register(new ItemDefinition
        {
            Guid = SwordGuid,
            Name = Sword,
            BaseOn = SwordBase,
            DisplayName = "Emberblade (Kiln test)",
            Description = "A sword that exists only because a Kiln mod registered it.",
            // one level, as every new item unless it opts in to rolled loot: the level this test gives and reads back
            Fields = new Dictionary<string, object> { ["damageSetup.dmgMod"] = 1.75, ["rarity"] = "Rare", ["itemLevelRange.minLevel"] = 5 },
        });
        var ingot = Content.Items.Register(new ItemDefinition
        {
            Guid = IngotGuid,
            Name = Ingot,
            BaseOn = IngotBase,
            DisplayName = "Kiln Test Ingot",
            Description = "Crafted from twigs, for science.",
            Fields = new Dictionary<string, object> { ["maxStackSize"] = 50 },
        });
        var swordRecipe = Content.Recipes.Register(new RecipeDefinition
        {
            Guid = SwordRecipeGuid,
            Name = "Kiln_Test_Emberblade_Recipe",
            Station = "hand",
            Inputs = [("Material_T1_Wood_Logs", 5), (Ingot, 2)],
            Outputs = [(Sword, 1)],
        });
        var ingotRecipe = Content.Recipes.Register(new RecipeDefinition
        {
            Guid = IngotRecipeGuid,
            Name = "Kiln_Test_Ingot_Recipe",
            BaseOn = "Tool_basic_FishingRod_T0_PK",
            Inputs = [("Material_T1_Twigs", 1)],
            Outputs = [(Ingot, 3)],
            Duration = TimeSpan.FromSeconds(2),
        });
        var want = reload ? new[] { "adopted", "unchanged" } : new[] { "defined" };
        Check(want.Contains(sword.State) && want.Contains(ingot.State) && want.Contains(swordRecipe.State) && want.Contains(ingotRecipe.State),
            $"register from code ({(reload ? "after a reload" : "OnLoad")}): {sword}, {ingot}, {swordRecipe}, {ingotRecipe}");
        Check(sword.Id == NewContent.IdOf(SwordGuid) && sword.Id == new ItemDefinition { Guid = SwordGuid, Name = "x", BaseOn = "y" }.Id,
            $"deterministic id: {Sword} = 0x{sword.Id:x8} = FNV-1a({SwordGuid})");
        if (reload) return;
        // refusals
        Throws(() => Content.Items.Register(new ItemDefinition { Guid = Guid.Empty, Name = "Kiln_Test_Bad", BaseOn = SwordBase }), "a zero GUID");
        Throws(() => Content.Items.Register(new ItemDefinition { Guid = BrokenGuid, Name = Sword, BaseOn = SwordBase }), "a name another definition has");
        Throws(() => Content.Items.Register(new ItemDefinition { Guid = BrokenGuid, Name = "bad name!", BaseOn = SwordBase }), "a debug name with spaces");
        Throws(() => Content.Recipes.Register(new RecipeDefinition { Guid = BrokenGuid, Name = "Kiln_Test_NoStation", Inputs = [("Material_T1_Twigs", 1)], Outputs = [(Ingot, 1)] }),
            "a recipe without base or station");
        var broken = Content.Items.Register(new ItemDefinition { Guid = BrokenGuid, Name = "Kiln_Test_Orphan_Base", BaseOn = "No_Such_Item_Anywhere" });
        Check(broken.State == "defined", $"a definition whose base does not exist is accepted (checked when the registries load): {broken}");
    }

    private void Throws(Action a, string what)
    {
        try
        {
            a();
            Check(false, $"refused: {what} (no exception)");
        }
        catch (ArgumentException e)
        {
            Check(true, $"refused: {what}: {e.Message}");
        }
    }

    private static ContentDefinition? Def(string name) => NewContent.Definitions().FirstOrDefault(d => d.Name == name);

    // ---- fresh: registries, catalog, give, save ----
    private async Task RunAsync()
    {
        try
        {
            var marker = Marker();
            if (File.Exists(marker))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(marker));
                await AfterRestart(doc.RootElement);
            }
            else await Fresh();
        }
        catch (Exception ex)
        {
            Check(false, $"test aborted: {ex}");
        }
        Log.Info($"newcontent-test done: {_pass} PASS, {_fail} FAIL");
    }

    private void CheckRegistries(string phase)
    {
        var sword = Content.Items.Get(Sword);
        var baseSword = Content.Items.Get(SwordBase)!;
        Check(sword != null && sword.Id == NewContent.IdOf(SwordGuid) && sword.Guid == SwordGuid && Content.Items.Get(sword.Id)?.Name == Sword
              && Content.Items.Get(SwordGuid.ToString())?.Name == Sword,
            $"{phase}: {Sword} in Content.Items as 0x{sword?.Id:x8} {sword?.Guid} (by name, id and GUID)");
        if (sword == null) return;
        Check(Math.Abs(sword.GetNumber("damageSetup.dmgMod") - 1.75) < 1e-6 && Math.Abs(baseSword.GetNumber("damageSetup.dmgMod") - 1.0) < 1e-6,
            $"{phase}: {Sword}.damageSetup.dmgMod = {sword.GetNumber("damageSetup.dmgMod")} (base {SwordBase}: {baseSword.GetNumber("damageSetup.dmgMod")})");
        Check(sword.Get("rarity").Name == "Rare" && sword.Get("category").Integer == baseSword.Get("category").Integer
              && sword.Get("flags").Integer == baseSword.Get("flags").Integer,
            $"{phase}: {Sword}.rarity {sword.Get("rarity")} (base {baseSword.Get("rarity")}), category {sword.Get("category")} and flags {sword.Get("flags")} like the base");
        var ingot = Content.Items.Get(Ingot);
        Check(ingot?.MaxStack == 50, $"{phase}: {Ingot}.maxStackSize {ingot?.MaxStack} (base {IngotBase}: {Content.Items.Get(IngotBase)?.MaxStack})");
        var gem = Content.Items.Get(PackGem);
        Check(gem is { MaxStack: 77 } && gem.Id == NewContent.IdOf(Guid.Parse("0639a2c3-178a-47d4-80f7-fc116023e115")),
            $"{phase}: data pack \"add\": {PackGem} 0x{gem?.Id:x8}, maxStackSize {gem?.MaxStack} (40 in its definition, 77 from the pack's rule after it)");
        var r1 = Content.Recipes.Get("Kiln_Test_Emberblade_Recipe");
        Check(r1 != null && r1.Id == NewContent.IdOf(SwordRecipeGuid) && r1.Inputs.Count == 2 && r1.Inputs[0].Count == 5
              && Content.Items.Get(r1.Inputs[1].Item.Value)?.Name == Ingot && r1.Inputs[1].Count == 2
              && r1.Outputs.Count == 1 && r1.Outputs[0].Item.Value == sword.Id && r1.Get("workshopId").Integer == 0,
            $"{phase}: recipe Kiln_Test_Emberblade_Recipe 0x{r1?.Id:x8} at the hand station: {string.Join(", ", r1?.Inputs.Select(s => s.ToString()) ?? [])} -> {string.Join(", ", r1?.Outputs.Select(s => s.ToString()) ?? [])}");
        var rod = Content.Recipes.Get("Tool_basic_FishingRod_T0_PK")!;
        var r2 = Content.Recipes.Get("Kiln_Test_Ingot_Recipe");
        Check(r2 != null && r2.Get("workshopId").Integer == rod.Get("workshopId").Integer && Math.Abs(r2.CraftingDuration.TotalSeconds - 2) < 1e-6
              && r2.Outputs is [{ Count: 3 }] && r2.Outputs[0].Item.Value == ingot!.Id && r2.Get("knowledgeRequirement.knowledgeOrQueryId").Integer == 0,
            $"{phase}: recipe Kiln_Test_Ingot_Recipe (base {rod.Name}, workshop 0x{r2?.Get("workshopId").Integer:x8}): {string.Join(", ", r2?.Inputs.Select(s => s.ToString()) ?? [])} -> {string.Join(", ", r2?.Outputs.Select(s => s.ToString()) ?? [])}, duration {r2?.CraftingDuration.TotalSeconds} s, no knowledge needed");
        var defs = NewContent.Definitions();
        var mine = defs.Where(d => d.Name.StartsWith("Kiln_Test_", StringComparison.Ordinal)).ToList();
        Check(mine.Count(d => d.State == "registered" && d.Worlds.Contains("server")) == 6,
            $"{phase}: definitions: {string.Join("; ", mine.Select(d => d + (d.Problem != null ? $" [{d.Problem}]" : "")))}");
        var orphanBase = defs.FirstOrDefault(d => d.Name == "Kiln_Test_Orphan_Base");
        Check(orphanBase is { State: "error" } && orphanBase.Problem?.Contains("not found") == true && Content.Items.Get("Kiln_Test_Orphan_Base") == null,
            $"{phase}: a definition with a missing base is reported, not registered: {orphanBase} [{orphanBase?.Problem}]");
    }

    private async Task Fresh()
    {
        CheckRegistries("world load");
        // kiln.items: its catalog is read from the item registry after the insert
        for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Catalog: true }; i++) await Steps(1);
        var sword = Content.Items.Get(Sword)!;
        var ingot = Content.Items.Get(Ingot)!;
        var cat = Items.Get(new ItemId(sword.Id));
        Check(cat != null && cat.Name == Sword && cat.IsGear, $"kiln.items catalog has {Sword}: {cat}");
        Check(Content.Items.Count == Items.All.Count, $"items: {Content.Items.Count} in kiln.content, {Items.All.Count} in kiln.items (both with the new ones)");
        // a container with two free slots
        EntityId chest = EntityId.None;
        foreach (var e in Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty))
            // a saved container (not a DoNotSave loot container another test mod spawned)
            if (Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 2 && c.Count(new ItemId(sword.Id)) == 0 &&
                (!EntityData.Available || EntityData.IsPersistent(e)))
            {
                chest = e;
                break;
            }
        if (!Check(!chest.IsNone, $"give: container {chest} with two free slots")) return;
        var before = Describe(Items.Read(chest)!);
        var g1 = await Items.Give(chest, new ItemId(sword.Id), 1, new Gear(5, ItemRarity.Rare));
        Check(g1.Status == ItemsStatus.Done && g1.Changes is [{ ItemEntity.IsNone: false }],
            $"give 1 {Sword} (gear, level 5 Rare) into {chest}: {g1} -> {string.Join(", ", g1.Changes.Select(c => $"{c.Slot} item entity {c.ItemEntity}"))}");
        var g2 = await Items.Give(chest, new ItemId(ingot.Id), 7);
        Check(g2.Status == ItemsStatus.Done && g2.Done == 7, $"give 7 {Ingot} into {chest}: {g2}");
        await Steps(5);
        var inv = Items.Read(chest)!;
        var s1 = inv.Slots.FirstOrDefault(s => s.Item.Value == sword.Id);
        var s2 = inv.Slots.FirstOrDefault(s => s.Item.Value == ingot.Id);
        Check(s1 is { Count: 1, IsGear: true, Level: 5 } && s2 is { Count: 7 },
            $"read back 5 steps later: {s1?.Slot} {Sword} x{s1?.Count} level {s1?.Level} rarity {s1?.Rarity} entity {s1?.ItemEntity}; {s2?.Slot} {Ingot} x{s2?.Count}");
        if (s1 == null || s2 == null) return;
        // save, marker, restart by the operator
        var saved = await Saves.Request();
        Check(saved.Success, $"save requested: {saved}");
        Directory.CreateDirectory(Context.DataDirectory);
        File.WriteAllText(Marker(), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["pid"] = Environment.ProcessId, ["start"] = ProcessStart(), ["chest"] = chest.Value, ["before"] = before,
            ["sword"] = sword.Id, ["ingot"] = ingot.Id, ["swordSlot"] = Index(inv, s1), ["ingotSlot"] = Index(inv, s2),
        }));
        Log.Info($"newcontent-test: phase 1 done; restart the server to check the new items survive ({chest}: {Describe(inv)})");
    }

    // ---- after a restart ----
    private async Task AfterRestart(JsonElement m)
    {
        if (m.GetProperty("pid").GetInt32() == Environment.ProcessId && m.GetProperty("start").GetString() == ProcessStart())
        {
            Log.Info("newcontent-test: the marker is from this process (reload): checked in OnLoad");
            return;
        }
        CheckRegistries("after the restart");
        for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Read: true }; i++) await Steps(1);
        var chest = new EntityId(m.GetProperty("chest").GetUInt32());
        var sword = new ItemId(m.GetProperty("sword").GetUInt32());
        var ingot = new ItemId(m.GetProperty("ingot").GetUInt32());
        var inv = Items.Read(chest);
        if (!Check(inv != null, $"after the restart: container {chest} is back")) return;
        var s1 = inv!.Slots.FirstOrDefault(s => s.Item == sword);
        var s2 = inv.Slots.FirstOrDefault(s => s.Item == ingot);
        // slot positions, not SlotIds: the linked inventories are run-time entities whose ids change across a restart
        Check(s1 is { Count: 1, IsGear: true, Level: 5 } && Index(inv, s1) == m.GetProperty("swordSlot").GetInt32(),
            $"after the restart: the new sword survived the save: {s1?.Slot} item {s1?.Item} level {s1?.Level} rarity {s1?.Rarity} entity {s1?.ItemEntity}");
        Check(s2 is { Count: 7 } && Index(inv, s2) == m.GetProperty("ingotSlot").GetInt32(),
            $"after the restart: the 7 new ingots survived the save: {s2?.Slot} x{s2?.Count}");
        await Steps(10);
        var again = Items.Read(chest)!;
        Check(again.Count(sword) == 1 && again.Count(ingot) == 7, $"10 steps later still there (not removed as unknown items): {Describe(again)}");
        // clean up
        if (s1 != null) Check((await Items.TakeSlot(chest, s1)).Status == ItemsStatus.Done, $"take the sword back out of {s1.Slot}");
        if (s2 != null) Check((await Items.TakeSlot(chest, s2)).Status == ItemsStatus.Done, $"take the ingots back out of {s2.Slot}");
        var end = Describe(Items.Read(chest)!);
        Check(end == m.GetProperty("before").GetString(), $"container {chest} ends as it started: {end}");
        File.Delete(Marker());
    }

    // ---- a restart without the definitions (kiln/data/newcontent-test/without-definitions) ----
    private async Task WithoutDefinitions()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Marker()));
            var m = doc.RootElement;
            var chest = new EntityId(m.GetProperty("chest").GetUInt32());
            var sword = new ItemId(m.GetProperty("sword").GetUInt32());
            var ingot = new ItemId(m.GetProperty("ingot").GetUInt32());
            for (var i = 0; i < 25 * 120 && !_stop && Items.Status() is not { Transactions: true, Read: true }; i++) await Steps(1);
            // The game alone drops stacks of unknown item ids while it loads the save (seen on 1285601, no log line).
            // Kiln's missing content (content.md, "Missing content: placeholders") registers a placeholder before the save
            // is read, so the item stays known and its stacks stay.
            Check(Content.Items.Get(sword.Value) != null, $"without the definitions: 0x{sword.Value:x8} stays an item (a placeholder)");
            var inv = Items.Read(chest)!;
            Check(inv.Count(sword) == 1 && inv.Count(ingot) == 7,
                $"without the definitions: the placeholders kept the saved stacks: {Describe(inv)}");
            File.Delete(Marker());
            File.Delete(Path.Combine(Context.DataDirectory, "without-definitions"));
        }
        catch (Exception ex)
        {
            Check(false, $"without-definitions phase aborted: {ex}");
        }
        Log.Info($"newcontent-test (without definitions) done: {_pass} PASS, {_fail} FAIL");
    }

    // ---- after `reload newcontent-test` (same process) ----
    private async Task CheckAfterReload()
    {
        await Steps(60);  // the mod's data pack is re-applied a little after OnLoad (the set of loaded mods changed)
        var defs = NewContent.Definitions().Where(d => d.Name.StartsWith("Kiln_Test_", StringComparison.Ordinal) && d.Name != "Kiln_Test_Orphan_Base").ToList();
        Check(defs.Count == 6 && defs.All(d => d.State == "registered"),
            $"after reload: every definition registered again: {string.Join("; ", defs)}");
        Check(Content.Items.Get(Sword) is { } s && Math.Abs(s.GetNumber("damageSetup.dmgMod") - 1.75) < 1e-6 && Content.Recipes.Get("Kiln_Test_Emberblade_Recipe") != null,
            "after reload: the item and its recipe are still readable");
        Log.Info($"newcontent-test (reload) done: {_pass} PASS, {_fail} FAIL");
    }

    // every slot in link order ("-" = empty), without SlotIds (they name run-time inventory entities)
    private static string Describe(InventoryContents c) =>
        string.Join(" ", c.Slots.Select(s => s.IsEmpty ? "-" : $"{s.Item.Value:x8}x{s.Count}"));

    private static int Index(InventoryContents c, InventorySlot s) => c.Slots.ToList().FindIndex(x => x.Slot == s.Slot);

    private static string ProcessStart() => Process.GetCurrentProcess().StartTime.ToUniversalTime().ToString("O");
}
