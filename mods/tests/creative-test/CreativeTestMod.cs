// SPDX-License-Identifier: MIT
// Creative mode's tools on a dedicated server with no player: the item catalogue and taking items, NoFallDamage, and
// the day and night cycle (kiln.time's held clock speed). A spawned player character stands in for a player
// (Rules.Simulate).
using Kiln;

namespace CreativeTest;

public sealed class CreativeTestMod : Mod
{
    private const string Me = "creative-test";
    private const int PlayerInputReady = 0x5b8;  // PlayerInput.fromClientIsReadyForGameplay
    private int _pass, _fail;

    private void Info(string s) => Log.Info($"{Me}: INFO {s}");
    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"{Me}: PASS {what}"); }
        else { _fail++; Log.Error($"{Me}: FAIL {what}"); }
        return ok;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    public override void OnLoad() => _ = Run().ContinueWith(t =>
    {
        if (t.Exception != null) Check(false, $"the test threw: {t.Exception.InnerException}");
        Log.Info($"{Me} done: {_pass} PASS, {_fail} FAIL");
    });

    private async Task Run()
    {
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Items.All.Count > 0 && Time.Ready); i++) await Steps(25);
        await Steps(50);
        Catalog();
        await Cycle();
        Commands_();
        var f = await Character();
        if (f.IsNone) return;
        Rules.Simulate(f);
        try
        {
            await Take(f);
            await Crafting(f);
            Found(f);
            await Falls(f);
            await FlightProbe(f);
        }
        finally
        {
            Creative.Set(f, false);
            Rules.Simulate(f, false);
            Spawn.Destroy(f);
        }
    }

    // ---- the catalogue the menu lists ----
    private void Catalog()
    {
        var all = CreativeTools.Catalog();
        Check(all.Count > 1000 && all.Count <= Items.All.Count, $"the creative catalogue lists {all.Count} of the world's {Items.All.Count} items");
        var hidden = Items.All.Where(i => !all.Contains(i)).Select(i => i.Name).ToList();
        Check(!all.Any(i => i.Name.Contains("debug", StringComparison.OrdinalIgnoreCase)),
            $"no debug entries listed; {hidden.Count} left out, e.g. {string.Join(", ", hidden.Take(4))}");
    }

    // ---- the day and night cycle ----
    private async Task Cycle()
    {
        Check(Time.CanSetSpeed, $"kiln.time can hold the clock's speed on this build (held {Time.HeldSpeed?.ToString() ?? "none"})");
        if (!Time.CanSetSpeed) return;
        var s0 = Time.State;
        Info($"clock: {s0}");

        var r = CreativeTools.SetCycle(TimeCycle.Stopped, Me);
        await Steps(2);
        var a = Time.State;
        await Steps(100);
        var b = Time.State;
        Check(r.Ok && Time.Cycle == TimeCycle.Stopped && b.TimeScale == 0 && Math.Abs(b.Hours - a.Hours) < 1e-6,
            $"Stopped: {a.Clock} -> {b.Clock} over 100 steps ({b.Hours - a.Hours:0.000000} h), scale {b.TimeScale}");
        Check(b.ChangeCounter == a.ChangeCounter,
            $"while stopped the counter holds ({a.ChangeCounter} -> {b.ChangeCounter}): the game's speed requests are refused without a re-sync every step");

        r = CreativeTools.SetCycle(TimeCycle.Fast, Me);
        await Steps(2);
        a = Time.State;
        await Steps(100);
        b = Time.State;
        var fast = (b.Hours - a.Hours + 24) % 24;
        var normal = 100 * 0.041 / (a.DayLength + a.NightLength).TotalSeconds * 24;  // hours 100 steps take at speed 1
        Check(r.Ok && Time.Cycle == TimeCycle.Fast && fast > normal * 10,
            $"Fast (speed {Time.HeldSpeed:0.0}): {a.Clock} -> {b.Clock} over 100 steps ({fast:0.000} h; at the normal speed about {normal:0.000} h)");

        r = CreativeTools.SetCycle(TimeCycle.Normal, Me);
        await Steps(2);
        a = Time.State;
        await Steps(100);
        b = Time.State;
        Check(r.Ok && Time.HeldSpeed is null && b.TimeScale == 1 && b.Hours > a.Hours,
            $"Normal: the game decides again, scale {b.TimeScale}, {a.Clock} -> {b.Clock}");
    }

    // ---- /creative time and cycle from the console ----
    private void Commands_()
    {
        var r = Commands.Execute(CommandSource.Console, "creative time 7:30");
        Check(r.Ok && Math.Abs(Time.OfDay - 7.5) < 0.05, $"/creative time 7:30: {string.Join(" ", r.Output)} {r.Error} (now {Time.State.Clock})");
        r = Commands.Execute(CommandSource.Console, "creative cycle stopped");
        Check(r.Ok && Time.Cycle == TimeCycle.Stopped, $"/creative cycle stopped: {string.Join(" ", r.Output)} {r.Error}");
        r = Commands.Execute(CommandSource.Console, "creative cycle normal");
        Check(r.Ok && Time.Cycle == TimeCycle.Normal, $"/creative cycle normal: {string.Join(" ", r.Output)} {r.Error}");
        r = Commands.Execute(CommandSource.Console, "creative cycle sideways");
        Check(!r.Ok, $"/creative cycle sideways is refused: {r.Error}");
    }

    // ---- taking items ----
    private async Task Take(EntityId f)
    {
        var all = CreativeTools.Catalog();
        var mat = all.FirstOrDefault(i => i.Category == ItemCategory.Materials && i.MaxStack >= 20 && !i.IsGear);
        var gear = all.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMax > i.LevelMin + 2);
        if (!Check(mat is not null && gear is not null, $"a material ({mat}) and a weapon with a level range ({gear} {gear?.LevelMin}-{gear?.LevelMax})")) return;

        var r = await CreativeTools.Give(f, mat!.Id, 7);
        Check(!r.Ok, $"without creative mode taking is refused: {r.Message}");
        var set = Creative.Set(f, true);
        Check(set.Ok && Creative.IsOn(f) && CreativeTools.CanGive(f), $"creative mode on for the character: {set.Message}");

        var before = Items.Read(f)?.Count(mat.Id) ?? 0;
        r = await CreativeTools.Give(f, mat.Id, 7);
        await Steps(8); // the shared give admission interval is 250 ms
        var after = Items.Read(f)?.Count(mat.Id) ?? 0;
        Check(r.Ok && after - before == 7, $"took 7 x {mat.Label}: {before} -> {after} ({r.Message})");

        Check(!(await CreativeTools.Give(f, mat.Id, 0)).Ok, "zero take is refused");
        Check(!(await CreativeTools.Give(f, mat.Id, int.MaxValue)).Ok, "oversized take is refused");
        Check(!(await CreativeTools.Give(f, gear!.Id, 1, gear.LevelMax + 1)).Ok, "out-of-range gear level is refused");
        Check(!(await CreativeTools.Give(f, gear.Id, 1, gear.LevelMax, (ItemRarity)255)).Ok, "undefined rarity is refused");
        var level = gear.LevelMax;
        r = await CreativeTools.Give(f, gear.Id, 1, level, ItemRarity.Epic);
        await Steps(2);
        var slot = Items.Read(f)?.Slots.Where(s => s.Item == gear.Id).OrderByDescending(s => s.Level).FirstOrDefault();
        Check(r.Ok && slot is not null && slot.Level == level, $"took {gear.Label} at level {level}, Epic: in the inventory at level {slot?.Level}, {slot?.Rarity} ({r.Message})");

        r = await CreativeTools.Give(f, new ItemId(0xDEADBEEF), 1);
        Check(!r.Ok, $"an item that does not exist is refused: {r.Message}");

        await GearOptions(f, gear);

        var st = CreativeTools.SetTime(f, 3);
        Info($"the character (no permission entry) asks for 03:00: {st.Ok}, {st.Message}");
    }

    // ---- Gear options at creation: upgrades unlocked, a gem slot (kiln.items' createPide hook) ----
    private async Task GearOptions(EntityId f, ItemInfo gear)
    {
        if (!Items.GearOptionsAvailable)
        {
            Check(!(await CreativeTools.Give(f, gear.Id, 1, gear.LevelMin, ItemRarity.Epic, 5, true)).Ok,
                "without the createPide hook, upgrades and gem slots are refused");
            return;
        }
        var helmet = Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Equipment && i.Name.StartsWith("Armor_", StringComparison.Ordinal));
        if (helmet is not null)
        {
            await Steps(10);
            var no = await CreativeTools.Give(f, helmet.Id, 1, null, null, 0, true);
            Check(!no.Ok, $"a gem slot on armour is refused: {no.Message}");
        }
        async Task<InventorySlot?> GiveAndRead(int level, int upgrades, bool gem)
        {
            await Steps(10);  // the take gate: one take per 250 ms
            var before = Items.Read(f)?.Slots.Where(s => s.Item == gear.Id).Select(s => s.ItemEntity).ToHashSet() ?? new();
            var r = await CreativeTools.Give(f, gear.Id, 1, level, ItemRarity.Epic, upgrades, gem);
            await Steps(3);
            var slot = Items.Read(f)?.Slots.FirstOrDefault(s => s.Item == gear.Id && !before.Contains(s.ItemEntity));
            Info($"gave {gear.Label} level {level}, Epic, upgrades {upgrades}, gem slot {gem}: {r.Message}");
            return slot;
        }
        (int Avail, int Unlocked) Perks(InventorySlot s) =>
            World.ReadBytes(s.ItemEntity, "PerkContainerNew") is { Length: >= 2 } b ? (b[0], b[1]) : (-1, -1);
        bool Gem(InventorySlot s) => World.Has(s.ItemEntity, "WeaponGemSlot");

        var plain = await GiveAndRead(30, 0, false);
        var full = await GiveAndRead(30, 5, true);
        var two = await GiveAndRead(30, 2, false);
        if (!Check(plain is not null && full is not null && two is not null, "three gear items given for the options")) return;
        var pp = Perks(plain!);
        var fp = Perks(full!);
        var tp = Perks(two!);
        Check(pp.Avail > 0 && pp.Unlocked == 0 && plain!.Level == 30,
            $"plain Epic: perks {pp.Unlocked}/{pp.Avail} unlocked, level {plain!.Level} (gem slot from the game's roll: {Gem(plain)})");
        Check(fp.Avail == pp.Avail && fp.Unlocked == fp.Avail && full!.Level == 30 + fp.Avail,
            $"all upgrades: perks {fp.Unlocked}/{fp.Avail}, level {full!.Level} (asked 30 + {fp.Avail})");
        Check(Gem(full!), $"the weapon has a gem slot (WeaponGemSlot on item entity {full!.ItemEntity})");
        Check(tp.Unlocked == Math.Min(2, tp.Avail) && two!.Level == 30 + tp.Unlocked,
            $"two upgrades: perks {tp.Unlocked}/{tp.Avail}, level {two!.Level}");
        var st = World.ReadBytes(full!.ItemEntity, "ItemState");
        Info($"the upgraded item's ItemState: {(st is null ? "unreadable" : Convert.ToHexString(st))}");
    }

    // ---- Free crafting inside the game's own transaction (kiln.rules' recipe payment hook) ----
    private async Task Crafting(EntityId f)
    {
        if (!Gameplay.Available || !Gameplay.Enable()) { Info("Kiln.Gameplay is not available: crafting skipped"); return; }
        var on = Creative.Set(f, true);
        var r0 = Rules.Of(f);
        Info($"rules with creative on: {r0} (denied: {string.Join(", ", r0.Denied)}; {on.Message.Length} chars)");
        Check(r0.FreeCrafting ? !r0.Denied.Contains("freeCraftingUnavailable") : r0.Denied.Contains("freeCraftingUnavailable"),
            $"free crafting is on exactly when the native recipe-payment hook is installed (on: {r0.FreeCrafting})");
        Check(r0.KeepConsumables ? !r0.Denied.Contains("keepConsumablesUnavailable") : r0.Denied.Contains("keepConsumablesUnavailable"),
            $"kept consumables are on exactly when the native consume hook is installed (on: {r0.KeepConsumables})");
        GameplayRecipe? recipe = null;
        var station = EntityId.None;
        foreach (var s in World.Find("Crafting").Where(e => !e.IsPlayer && !World.Has(e, "FactoryStation")))
        {
            var b = World.ReadBytes(s, "Crafting");
            if (b is not { Length: >= 0x14 }) continue;
            recipe = Gameplay.WorkshopRecipes(BitConverter.ToUInt32(b, 0x10)).Select(Gameplay.Recipe)
                .FirstOrDefault(r => r is { HasCategoryInputs: false, RequiredProps: 0 } && r.Inputs.Count is > 0 and <= 3 &&
                                     r.Inputs.All(i => i.Count <= 20) && r.Outputs.Count > 0 &&
                                     r.Outputs.All(o => r.Inputs.All(i => i.ItemId != o.ItemId)));
            if (recipe is not null) { station = s; break; }
        }
        if (!Check(recipe is not null, $"a station {station} with a simple recipe 0x{recipe?.Id:x8}")) return;
        int Count(uint id) => Items.Read(f)?.Count(new ItemId(id)) ?? 0;
        var outId = recipe!.Outputs[0].ItemId;
        var perRun = recipe.Outputs.Where(o => o.ItemId == outId).Sum(o => o.Count);
        async Task<int> Craft(int amount = 1)
        {
            var before = Count(outId);
            Gameplay.Inject.Craft(f, station, recipe.Id, amount);
            for (var i = 0; i < 40 && Count(outId) == before; i++) await Steps(1);
            await Steps(3);
            return Count(outId) - before;
        }
        string Inputs() => string.Join(", ", recipe.Inputs.Select(i => $"0x{i.ItemId:x8} x{Count(i.ItemId)}"));
        foreach (var i in recipe.Inputs) await Items.Give(f, new ItemId(i.ItemId), i.Count);
        await Steps(2);
        var inBefore = recipe.Inputs.ToDictionary(i => i.ItemId, i => Count(i.ItemId));
        var made = await Craft();
        var kept = recipe.Inputs.All(i => Count(i.ItemId) == inBefore[i.ItemId]);
        if (r0.FreeCrafting)
        {
            Check(made == perRun && kept, $"free craft at {station}: exactly {perRun} output, got {made}, inputs kept ({Inputs()})");
            var repeated = await Craft(2);
            Check(repeated == 2 * perRun && recipe.Inputs.All(i => Count(i.ItemId) == inBefore[i.ItemId]),
                $"repeated multi-craft: exactly {2 * perRun} output, got {repeated}, inputs unchanged");
            foreach (var i in recipe.Inputs)
                if (Count(i.ItemId) > 0) await Items.Take(f, new ItemId(i.ItemId), Count(i.ItemId));
            await Steps(2);
            var none = recipe.Inputs.All(i => Count(i.ItemId) == 0);
            var bare = await Craft();
            Check(none && bare == perRun, $"free craft with no inputs at all: exactly {perRun} output, got {bare} (inputs {Inputs()})");
            Check(Rules.Status() is { FreeCrafting: true, CraftsFree: > 0 } st,
                $"kiln.rules counted the free crafts ({Rules.Status()?.CraftsFree})");
            // A rejected output must not be replaced by an asynchronous give. Fill this transient character's bag
            // with another material after removing any mergeable output stacks, then try two crafts.
            foreach (var o in recipe.Outputs)
                if (Count(o.ItemId) > 0) await Items.Take(f, new ItemId(o.ItemId), Count(o.ItemId));
            var filler = Items.All.FirstOrDefault(i => i.Category == ItemCategory.Materials && !i.IsGear && i.MaxStack > 1 &&
                recipe.Outputs.All(o => o.ItemId != i.Id.Value) && recipe.Inputs.All(o => o.ItemId != i.Id.Value));
            if (filler is not null && Items.Read(f) is { } inventory)
            {
                var oldFiller = Count(filler.Id.Value);
                await Items.Give(f, filler.Id, Math.Min(100000, Math.Max(1, filler.MaxStack) * inventory.Slots.Count),
                    options: GiveOptions.NoMerge);
                await Steps(3);
                Check(Items.Read(f)?.FreeSlots() == 0, "the transient test backpack is full for the failed-output check");
                var failed = await Craft(2);
                await Steps(10);
                Check(failed == 0 && recipe.Outputs.All(o => Count(o.ItemId) == 0) && recipe.Inputs.All(i => Count(i.ItemId) == 0),
                    "failed output creates no items or delayed reimbursement, even after repeated crafts");
                var addedFiller = Count(filler.Id.Value) - oldFiller;
                if (addedFiller > 0) await Items.Take(f, filler.Id, addedFiller);
            }
        }
        else
        {
            Check(made > 0 && !kept, $"without the hook the game's craft spends the inputs: output +{made} ({Inputs()})");
        }
        Creative.Set(f, false);
        Check(!(await CreativeTools.Give(f, new ItemId(outId), 1)).Ok, "turning creative off refuses subsequent takes");
        foreach (var i in recipe.Inputs) await Items.Give(f, new ItemId(i.ItemId), i.Count);
        await Steps(3);
        var inOff = recipe.Inputs.ToDictionary(i => i.ItemId, i => Count(i.ItemId));
        var madeOff = await Craft();
        var spent = recipe.Inputs.Any(i => Count(i.ItemId) < inOff[i.ItemId]);
        Check(madeOff > 0 && spent, $"without the rule the game's own craft takes the inputs: output +{madeOff}, inputs spent {spent}");
        var bareOff = 0;
        foreach (var i in recipe.Inputs)
            if (Count(i.ItemId) > 0) await Items.Take(f, new ItemId(i.ItemId), Count(i.ItemId));
        await Steps(2);
        bareOff = await Craft();
        Check(bareOff == 0, $"without the rule and without inputs nothing is made (output +{bareOff})");
        // Partial payment cannot mint extra outputs: provide exactly one run's inputs and request two runs.
        foreach (var i in recipe.Inputs) await Items.Give(f, new ItemId(i.ItemId), i.Count);
        await Steps(2);
        var partial = await Craft(2);
        Check(partial is >= 0 && partial <= perRun && partial % perRun == 0 &&
              recipe.Inputs.All(i => Count(i.ItemId) == i.Count * (1 - partial / perRun)),
            $"a partial paid craft matches outputs to spent inputs, or rolls back both (output +{partial})");
    }

    // ---- journey mode: the items the character has found ----
    private void Found(EntityId f)
    {
        var found = CreativeTools.Found(f);
        Info(found is null
            ? "the found items of the character are not readable (it is not a joined player: knowledge has no player slot for it)"
            : $"the character has found {found.Count} catalogue items");
    }

    // ---- NoFallDamage ----
    private async Task Falls(EntityId f)
    {
        Creative.Set(f, false);  // its invulnerability would cancel falls on its own
        await Steps(5);
        if (!Combat.Available || Combat.Status is not { Falls: true })
        {
            Info("Kiln.Combat does not see falls on this build: skipped");
            return;
        }
        var rule = Rules.Apply(f, new PlayerRules { NoFallDamage = true }, label: Me);
        var (hit, h0, h1) = await Drop(f, 40);
        Check(Rules.Of(f).NoFallDamage && h1 >= h0, $"NoFallDamage: a 40 m drop leaves health at {h1} of {h0}{(hit ? "" : " (no landing seen)")}");
        rule.Dispose();
        await Steps(5);
        (hit, h0, h1) = await Drop(f, 40);
        if (h1 < h0) Check(true, $"without the rule the same drop costs health: {h0} -> {h1}");
        else Info($"without the rule the drop left health at {h1} of {h0}{(hit ? "" : " (no landing seen)")}: the game decided no damage here");
    }

    // ---- research for character flight (docs/re/creative-mode-146.md Part 3 section 6): INFO only ----
    // The state decision keeps LocomotionState.BuildingFlying (11) once set, and its mover is the building spark's. What
    // does a player character do in it, with gravity still on? Raw write of DynamicLocomotion.state (+0x59).
    private async Task FlightProbe(EntityId f)
    {
        const int State = 0x59;
        if (!World.TryRead<byte>(f, "DynamicLocomotion", out _)) { Info("flight probe: no DynamicLocomotion readable"); return; }
        var at = World.Position(f)!.Value;
        await Teleport.To(f, at with { Y = at.Y + 30 }, new TeleportOptions { FindGround = false });
        for (var i = 0; i < 60 && World.Position(f)!.Value.Y < at.Y + 20; i++) await Steps(1);
        byte Read() => World.ReadBytes(f, "DynamicLocomotion") is { Length: > State } b ? b[State] : (byte)255;
        var y0 = World.Position(f)!.Value.Y;
        Info($"flight probe: 30 m up, state {Read()}, y {y0:0.0}; writing BuildingFlying (11)");
        World.TryWrite<byte>(f, "DynamicLocomotion", State, 11);
        for (var i = 1; i <= 50; i++)
        {
            await Steps(1);
            if (i is 1 or 5 or 10 or 25 or 50)
                Info($"flight probe: step {i}: state {Read()}, y {World.Position(f)!.Value.Y:0.00} ({World.Position(f)!.Value.Y - y0:+0.00;-0.00} m)");
        }
        World.TryWrite<byte>(f, "DynamicLocomotion", State, 2);
        for (var i = 0; i < 200 && !PlayerStates.Is(f, PlayerStateId.Grounded); i++) await Steps(1);
        Info($"flight probe: back to Falling (2): state {Read()}, grounded {PlayerStates.Is(f, PlayerStateId.Grounded)}, y {World.Position(f)!.Value.Y:0.0}");
    }

    private async Task<(bool Landed, int Before, int After)> Drop(EntityId f, double height)
    {
        Combat.SetHealth(f, Combat.GetHealth(f)?.Max ?? 200);
        await Steps(3);
        var before = Combat.GetHealth(f)?.Current ?? 0;
        var at = World.Position(f)!.Value;
        await Teleport.To(f, at with { Y = at.Y + height }, new TeleportOptions { FindGround = false });
        var landed = false;
        for (var i = 0; i < 240; i++)
        {
            await Steps(1);
            if (i > 10 && PlayerStates.Is(f, PlayerStateId.Grounded)) { landed = true; break; }
        }
        await Steps(30);
        return (landed, before, Combat.GetHealth(f)?.Current ?? 0);
    }

    // ---- a player character near the first base, ready for gameplay ----
    private async Task<EntityId> Character()
    {
        var bases = Build.Bases;
        WorldPosition anchor;
        if (bases.Count > 0) anchor = bases[0].Center;
        else if (Spawn.FindByName("FlameAltar", max: 1).FirstOrDefault() is { IsNone: false } a && Spawn.Inspect(a) is { } ai) anchor = ai.Position;
        else { Check(false, "an anchor in the world (a base or an altar)"); return default; }
        var at = anchor with { X = Math.Floor(anchor.X) + 160.25, Z = Math.Floor(anchor.Z) + 0.25 };
        try { await Build.Load(Build.BoxAround(at, 8, 128, 8)); } catch (Exception e) { Info($"loading the terrain there: {e.Message}"); }
        var ground = GroundY(at) ?? anchor.Y;
        at = at with { Y = ground + 1.0 };
        var f = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        if (!Check(!f.IsNone && World.Has(f, "PlayerInput"), $"a player character {f} at {at}")) return default;
        for (var i = 0; i < 50 && Combat.GetHealth(f) is null; i++) await Steps(1);
        Combat.SetInvulnerable(f);
        World.TryWrite(f, "PlayerInput", PlayerInputReady, (byte)1);
        var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
        foreach (var r in ready) Hooks.Detour(r, _ => 1);  // "the terrain at the character is loaded"
        for (var i = 0; i < 240 && !PlayerStates.Is(f, PlayerStateId.IsReadyForGameplay); i++) await Steps(1);
        for (var i = 0; i < 400 && !PlayerStates.Is(f, PlayerStateId.Grounded); i++) await Steps(1);
        Check(PlayerStates.Is(f, PlayerStateId.Grounded), $"it stands on the ground: {World.Position(f)}");
        await Steps(10);
        Combat.SetInvulnerable(f, false);
        return f;
    }

    private static double? GroundY(WorldPosition at)
    {
        try
        {
            var box = Build.BoxAround(at with { Y = at.Y + 100 - 128 }, 0.1, 128, 0.1);
            if (!Build.TryRead(box, out var cells)) return null;
            var layer = Math.Max(1, box.SizeX * box.SizeZ);
            for (var i = cells.Length - 1; i >= 0; i--)
                if (!cells[i].IsAir) return Build.CornerOf(new CellPos(box.X, box.Y + i / layer, box.Z)).Y + Build.Grid.CellSize;
        }
        catch (Exception) { }
        return null;
    }
}
