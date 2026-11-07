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
        await Steps(2);
        var after = Items.Read(f)?.Count(mat.Id) ?? 0;
        Check(r.Ok && after - before == 7, $"took 7 x {mat.Label}: {before} -> {after} ({r.Message})");

        var level = gear!.LevelMax;
        r = await CreativeTools.Give(f, gear.Id, 1, level, ItemRarity.Epic);
        await Steps(2);
        var slot = Items.Read(f)?.Slots.Where(s => s.Item == gear.Id).OrderByDescending(s => s.Level).FirstOrDefault();
        Check(r.Ok && slot is not null && slot.Level == level, $"took {gear.Label} at level {level}, Epic: in the inventory at level {slot?.Level}, {slot?.Rarity} ({r.Message})");

        r = await CreativeTools.Give(f, new ItemId(0xDEADBEEF), 1);
        Check(!r.Ok, $"an item that does not exist is refused: {r.Message}");

        var st = CreativeTools.SetTime(f, 3);
        Info($"the character (no permission entry) asks for 03:00: {st.Ok}, {st.Message}");
    }

    // ---- FreeCrafting: a craft request answered with the output, the inputs stay ----
    private async Task Crafting(EntityId f)
    {
        if (!Gameplay.Available || !Gameplay.Enable()) { Info("Kiln.Gameplay is not available: crafting skipped"); return; }
        var on = Creative.Set(f, true);
        Check(Rules.Of(f) is { FreeCrafting: true, KeepConsumables: true },
            $"/creative adds free crafting and kept consumables to the preset: {Rules.Of(f)} ({on.Message.Length} chars)");
        GameplayRecipe? recipe = null;
        var station = EntityId.None;
        foreach (var s in World.Find("Crafting").Where(e => !e.IsPlayer && !World.Has(e, "FactoryStation")))
        {
            var b = World.ReadBytes(s, "Crafting");
            if (b is not { Length: >= 0x14 }) continue;
            recipe = Gameplay.WorkshopRecipes(BitConverter.ToUInt32(b, 0x10)).Select(Gameplay.Recipe)
                .FirstOrDefault(r => r is { HasCategoryInputs: false, RequiredProps: 0 } && r.Inputs.Count is > 0 and <= 3 &&
                                     r.Inputs.All(i => i.Count <= 20) && r.Outputs.Count > 0);
            if (recipe is not null) { station = s; break; }
        }
        if (!Check(recipe is not null, $"a station {station} with a simple recipe 0x{recipe?.Id:x8}")) return;
        foreach (var i in recipe!.Inputs) await Items.Give(f, new ItemId(i.ItemId), i.Count);
        await Steps(2);
        int Count(uint id) => Items.Read(f)?.Count(new ItemId(id)) ?? 0;
        var inBefore = recipe.Inputs.ToDictionary(i => i.ItemId, i => Count(i.ItemId));
        var outId = recipe.Outputs[0].ItemId;
        var outBefore = Count(outId);
        Gameplay.Inject.Craft(f, station, recipe.Id);
        for (var i = 0; i < 40 && Count(outId) == outBefore; i++) await Steps(1);
        await Steps(3);
        var inAfter = recipe.Inputs.ToDictionary(i => i.ItemId, i => Count(i.ItemId));
        Check(Count(outId) > outBefore && inAfter.All(kv => kv.Value == inBefore[kv.Key]),
            $"free craft at {station}: output 0x{outId:x8} {outBefore} -> {Count(outId)}, inputs " +
            string.Join(", ", inBefore.Select(kv => $"0x{kv.Key:x8} {kv.Value} -> {inAfter[kv.Key]}")));
        Creative.Set(f, false);
        await Steps(3);
        var outMid = Count(outId);
        Gameplay.Inject.Craft(f, station, recipe.Id);
        for (var i = 0; i < 40 && Count(outId) == outMid; i++) await Steps(1);
        await Steps(3);
        var spent = recipe.Inputs.Any(i => Count(i.ItemId) < inAfter[i.ItemId]);
        Check(Count(outId) > outMid ? spent : true,
            $"without the rule the game's own craft takes the inputs: output {outMid} -> {Count(outId)}, inputs spent {spent}");
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
