// SPDX-License-Identifier: MIT
// An item's own model on a dedicated server that did not cook it (kiln.content's item path, native/services/content/refs.cpp): a model the item's own mod
// declares in its assets/assets.json is carried as its GUID, and anything else nobody serves stays refused. Then the held items in the world, the server
// staying up: given, equipped and switched, dropped and picked up (as far as the lab can drive it), stored in a chest, and found there after a restart.
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    // a GUID no mod declares and no game item uses, and one only a mod folder that is not loaded declares (run2.sh puts it in kiln/mods/decl-only, .disabled)
    private static readonly Guid Unknown = BlockIds.Derive(Me, "model", "nobody-declares-this");
    internal static readonly Guid NotLoaded = Guid.Parse("4f1d2c3b-5a69-5e7d-8c9b-0a1b2c3d4e5f");
    private const string ModelField = "equipment.equipmentEntityModelSetup.model";

    private static void Probe(string name, Guid model) => NewContent.RegisterJson(ContentKind.Items,
        $"{{\"guid\":\"{BlockIds.Derive(Me, "item", name)}\",\"name\":\"{G(name)}\",\"base\":\"{SwordStarter}\",\"model\":\"{model}\"}}");

    private async Task ItemModels()
    {
        // the look's rigid model, a model of this mod: taken whether or not this machine cooked it
        foreach (var h in HeldCases)
        {
            var item = Content.Items.Get(G(h.Block))!;
            var field = h.Pair ? "equipment.visualModel" : ModelField;
            var now = item.GetRef(field);
            var want = _wrong && h.Block == "held-sword" ? Guid.Empty : RigidBlade;
            Check(now == want, $"{h.Block}: {field} is the look's rigid model {RigidBlade}, a model of this mod ({(HeldCooked ? "cooked here" : "not cooked here")}): {now}");
        }
        Probe("probe-unknown-model", Unknown);
        Probe("probe-not-loaded-model", NotLoaded);
        Probe("probe-other-mod-model", BlockIds.Derive(Sample, "model", "moves_blade"));
        await Steps(30);
        foreach (var (name, model, what) in new[]
                 {
                     ("probe-unknown-model", Unknown, "a GUID no mod declares and no game item uses"),
                     ("probe-not-loaded-model", NotLoaded, "a GUID only a mod folder that is not loaded declares"),
                 })
        {
            var def = NewContent.Definitions().FirstOrDefault(d => d.Name == G(name));
            var refused = def?.Warnings.FirstOrDefault(w => w.Contains(model.ToString(), StringComparison.OrdinalIgnoreCase));
            var kept = Content.Items.Get(G(name))?.GetRef(ModelField);
            Check(def != null && refused != null && kept != model, $"{what} is still refused: {refused ?? "(no warning)"}; the item keeps {kept}");
        }
        // another loaded mod's model: taken where that mod is served (the sample, cooked here), never because it declares it
        var other = NewContent.Definitions().FirstOrDefault(d => d.Name == G("probe-other-mod-model"));
        Info($"another mod's declared model ({Sample}'s moves_blade): warnings [{string.Join(" | ", other?.Warnings ?? [])}], the item has {Content.Items.Get(G("probe-other-mod-model"))?.GetRef(ModelField)}");
    }

    // ---- the held items in the world, on a server that did not cook their models ----

    private async Task HeldItemsInTheWorld(EntityId p)
    {
        // give, equip, switch: each in hand in turn
        foreach (var h in HeldCases)
        {
            var id = Content.Items.Get(G(h.Block))!.Id;
            // a shield goes to its own equipment slot, which the hotbar request does not fill: not driven here
            if (h.Block == "held-shield") { Info("held-shield: a shield is not equipped by the hotbar request this lab uses: equipping it is not driven"); continue; }
            if (await ToHand(p, h.Block)) Check(Held(p) == id || Equipped(p, id), $"{h.Block}: equipped and switched to, the server still up");
        }
        // drop and pick up: out of the bag and onto the ground as a pickup, then the character's loot input beside it
        var sword = Content.Items.Get(G("held-sword"))!;
        var at = World.Position(p)!.Value;
        var take = await Items.Take(p, new ItemId(sword.Id), 1);
        var pickup = take.Status == ItemsStatus.Done ? await Spawn.Item(sword.Id, 1, at with { X = at.X + 0.5 }, options: new SpawnOptions { Tag = Me }) : EntityId.None;
        await Steps(20);
        Check(!pickup.IsNone && World.Exists(pickup), $"held-sword dropped: out of the bag ({take.Status}) and on the ground as {pickup} {Spawn.Inspect(pickup)?.TemplateName}");
        var before = Items.Read(p)?.Count(new ItemId(sword.Id)) ?? 0;
        foreach (var bits in new[] { 1UL << 8, 1UL << 5 | 1UL << 6 })   // AreaLoot, then ContextualAction(+Tap)
        {
            await Press(p, bits, 3);
            await Steps(20);
            if ((Items.Read(p)?.Count(new ItemId(sword.Id)) ?? 0) > before) break;
        }
        var after = Items.Read(p)?.Count(new ItemId(sword.Id)) ?? 0;
        if (after > before) Check(!World.Exists(pickup), $"held-sword picked up again by the character's own loot input: {before} -> {after} in the bag");
        else
        {
            Info($"the character's loot input did not pick the pickup up ({before} -> {after}): pick-up is not driven by this lab; the pickup is taken away");
            if (World.Exists(pickup)) Spawn.Destroy(pickup);
            await Items.Give(p, new ItemId(sword.Id), 1);
        }
        await Steps(10);
    }

    // a chest of the world, saved with it: the held items go in, and after a restart they are still there
    private async Task HeldItemsInAChest(EntityId p)
    {
        var file = Path.Combine(Context.DataDirectory, "chest.json");
        var ids = HeldCases.Select(h => Content.Items.Get(G(h.Block))!.Id).ToList();
        if (File.Exists(file))
        {
            var holder = Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty).FirstOrDefault(e => Items.Read(e) is { } c && ids.All(i => c.Count(new ItemId(i)) > 0));
            var c = holder.IsNone ? null : Items.Read(holder);
            Info($"after the restart: containers holding a held item: [{string.Join(", ", Items.FindInventories(InventoryKinds.Containers | InventoryKinds.NonEmpty).Where(e => Items.Read(e) is { } x && ids.Any(i => x.Count(new ItemId(i)) > 0)).Select(e => $"{e} {Spawn.Inspect(e)?.TemplateName} at {World.Position(e)}"))}]; written: {File.ReadAllText(file)}");
            var want = _wrong ? 2 : 1;
            Check(c != null && ids.All(i => c.Count(new ItemId(i)) == want), $"after the save and the restart the chest {holder} still holds the five held items: [{string.Join(", ", ids.Select(i => c?.Count(new ItemId(i)) ?? 0))}]");
            if (!holder.IsNone) foreach (var i in ids) await Items.Take(holder, new ItemId(i), 1);
            // this run's character got them again: out of its bag, so the action bar has room for what follows
            foreach (var i in ids) await Items.Take(p, new ItemId(i), Items.Read(p)?.Count(new ItemId(i)) ?? 0);
            File.Delete(file);
            return;
        }
        var at = World.Position(p)!.Value;
        // a container the world saves (as blocks-items-test finds one): a chest spawned or placed outside a base is not saved
        var all = Items.FindInventories(InventoryKinds.Containers);
        var chest = all.FirstOrDefault(e => Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= ids.Count + 1 && (!EntityData.Available || EntityData.IsPersistent(e)));
        Info($"containers in the world: {all.Count}, saved ones with room: {all.Count(e => Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= ids.Count + 1 && (!EntityData.Available || EntityData.IsPersistent(e)))}");
        var own = chest.IsNone;
        if (own)
        {
            // none the world saves (a fresh world): a chest of the test's own, stored in, but not across a restart
            chest = await Spawn.Entity("Prop_Decoration_T1_Storage_24", at with { X = at.X - 3 }, 0, new SpawnOptions { Tag = Me });
            for (var i = 0; i < 60 && Items.Read(chest) is not { Initialized: true }; i++) await Steps(1);
            Info("no container the world saves: the held items go into a chest of the test's own, which the world does not save, so the restart step is not run");
        }
        for (var i = 0; i < 60 && Items.Read(chest) is not { Initialized: true }; i++) await Steps(1);
        // out of the character's bag and into the chest (Items.Move refuses a slot on the action bar, where given items land)
        // what an earlier run left in it out first, so the chest holds one of each after this
        foreach (var i in ids)
            if (Items.Read(chest)?.Count(new ItemId(i)) is > 0 and var n) await Items.Take(chest, new ItemId(i), n);
        var moved = new List<string>();
        foreach (var i in ids)
        {
            if ((Items.Read(p)?.Count(new ItemId(i)) ?? 0) == 0) await Items.Give(p, new ItemId(i), 1);   // the shield was not taken in hand above
            await Steps(2);
            var took = await Items.Take(p, new ItemId(i), 1);
            var gave = took.Status == ItemsStatus.Done ? await Items.Give(chest, new ItemId(i), 1) : took;
            moved.Add($"{took.Status}/{gave.Status}");
            await Steps(2);
        }
        var held = Items.Read(chest);
        Check(held != null && ids.All(i => held.Count(new ItemId(i)) == 1), $"the five held items stored in a chest {chest}: [{string.Join("; ", moved)}]");
        if (!own)
        {
            File.WriteAllText(file, $"{chest.Value} {Spawn.Inspect(chest)?.TemplateName} at {World.Position(chest)}");
            Info("chest written: the restart checks it");
        }
        else
        {
            foreach (var i in ids) await Items.Take(chest, new ItemId(i), 1);
            Spawn.Destroy(chest);
        }
    }
}
