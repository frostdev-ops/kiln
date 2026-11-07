// SPDX-License-Identifier: MIT
// Kiln.Rules on a dedicated server with no player. A player character is spawned outside every base and driven through
// the game's own input path (the client's "ready" flag and the terrain check are forced, nothing else), so the game's
// player_control_action_sequence decides about its building fly mode as for a joined player.
using Kiln;

namespace RulesTest;

public sealed class RulesTestMod : Mod
{
    private const string Me = "rules-test";
    private const int PlayerInputDigital = 0x08 + 0x3a8;  // PlayerInput.fromClient.digitalInput (u64)
    private const int PlayerInputReady = 0x5b8;           // PlayerInput.fromClientIsReadyForGameplay
    private const int InputBuildingModeFly = 39;          // keen::ecs::PlayerInputType.BuildingModeFly
    private const int PlayerStateSpark = 0x50;            // PlayerState.buildingSparkEntityId
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
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Items.All.Count > 0); i++) await Steps(25);
        await Steps(50);
        var st = Rules.Status();
        Check(st is { Flight: true, Placement: true, Costs: true, Mining: true, BuildingZone: true, ShroudTimer: true, Gliding: true },
            $"kiln.rules hooks: flight {st?.Flight}, building zone {st?.BuildingZone}, placement {st?.Placement}, costs {st?.Costs}, " +
            $"mining {st?.Mining}, Shroud timer {st?.ShroudTimer}, gliding {st?.Gliding} ({st?.Detail})");

        var f = await Character();
        if (f.IsNone) return;
        Rules.Simulate(f);
        try
        {
            await BuildingZone(f);
            await Flight(f);
            await Vitals(f);
            await Water(f);
            await Digging(f);
            await Layers(f);
            await CreativeMode(f);
            await Shroud(f);
        }
        finally
        {
            Rules.Simulate(f, false);
            Spawn.Destroy(f);
        }
        st = Rules.Status();
        Info($"counters: fly allowed {st?.FlyAllowed}, zone forced {st?.ZoneForced}, cursor cleared {st?.CursorCleared}, costs {st?.CostsScaled}, " +
             $"refunds {st?.RefundsScaled}, digs {st?.DigsScaled}, Shroud held {st?.ShroudHeld}, glide changed {st?.GlideChanged}, faults {st?.Faults}");
        Check(st?.Faults == 0, $"no hook faulted: {st?.Faults}");
    }

    // ---- a player character outside every base, ready for gameplay ----
    private async Task<EntityId> Character()
    {
        var bases = Build.Bases;
        WorldPosition anchor;
        if (bases.Count > 0) anchor = bases[0].Center;
        else if (Spawn.FindByName("FlameAltar", max: 1).FirstOrDefault() is { IsNone: false } a && Spawn.Inspect(a) is { } ai) anchor = ai.Position;
        else { Check(false, "an anchor in the world (a base or an altar)"); return default; }
        // 160 m east of the first base: outside its zone, terrain loaded for the test
        var at = anchor with { X = Math.Floor(anchor.X) + 160.25, Z = Math.Floor(anchor.Z) + 0.25 };   // one cell's column
        var box = Build.BoxAround(at, 8, 128, 8);
        try { await Build.Load(box); } catch (Exception e) { Info($"loading the terrain there: {e.Message}"); }
        var ground = GroundY(at) ?? anchor.Y;
        at = at with { Y = ground + 1.0 };
        var f = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        if (!Check(!f.IsNone && World.Has(f, "PlayerInput") && World.Has(f, "PlayerState"), $"a player character {f} at {at}, {at.X - anchor.X:0} m from the base")) return default;
        foreach (var b in bases)
            Check(Math.Abs(at.X - b.Center.X) > b.Half.X || Math.Abs(at.Z - b.Center.Z) > b.Half.Z, $"it stands outside base {b.Altar} (half size {b.Half.X:0}, {b.Half.Z:0})");
        for (var i = 0; i < 50 && Combat.GetHealth(f) is null; i++) await Steps(1);
        Combat.SetInvulnerable(f);   // the spawn may be above the ground: no fall damage while it settles
        World.TryWrite(f, "PlayerInput", PlayerInputReady, (byte)1);
        var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
        foreach (var r in ready) Hooks.Detour(r, _ => 1);  // "the terrain at the character is loaded"
        for (var i = 0; i < 240 && !PlayerStates.Is(f, PlayerStateId.IsReadyForGameplay); i++) await Steps(1);
        Check(PlayerStates.Is(f, PlayerStateId.IsReadyForGameplay), $"the character is ready for gameplay ({ready.Length} terrain check(s) forced)");
        for (var i = 0; i < 400 && !PlayerStates.Is(f, PlayerStateId.Grounded); i++) await Steps(1);
        Check(PlayerStates.Is(f, PlayerStateId.Grounded), $"it stands on the ground: {World.Position(f)} (ground {ground:0.0} read from the voxels)");
        await Steps(10);
        Combat.SetInvulnerable(f, false);
        Check(Combat.GetHealth(f) is { Dead: false } h && h.Current > 0, $"alive: health {Combat.GetHealth(f)?.Current}");
        return f;
    }

    // The top solid cell of the column at `at`, from 100 m above it down 256 m (a column read through Kiln.Build), in metres.
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

    private static uint SparkOf(EntityId f) =>
        World.ReadBytes(f, "PlayerState") is { Length: >= PlayerStateSpark + 4 } b ? BitConverter.ToUInt32(b, PlayerStateSpark) : 0;

    private async Task Press(EntityId f, int bit, int hold = 3, int after = 8)
    {
        var bytes = World.ReadBytes(f, "PlayerInput")!;
        var v = BitConverter.ToUInt64(bytes, PlayerInputDigital);
        World.TryWrite(f, "PlayerInput", PlayerInputDigital, v | (1ul << bit));
        await Steps(hold);
        bytes = World.ReadBytes(f, "PlayerInput")!;
        v = BitConverter.ToUInt64(bytes, PlayerInputDigital);
        World.TryWrite(f, "PlayerInput", PlayerInputDigital, v & ~(1ul << bit));
        await Steps(after);
    }

    private static double Dist(WorldPosition a, WorldPosition b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    // ---- the building fly mode outside a base ----
    private async Task Flight(EntityId f)
    {
        await Press(f, InputBuildingModeFly);
        Check(SparkOf(f) == 0, $"without a rule the game refuses its fly mode outside a base (spark {SparkOf(f)})");

        var before = Rules.Status()!.FlyAllowed;
        var fly = Rules.Apply(f, new PlayerRules { FlyAnywhere = true }, label: "fly test");
        Check(Rules.Of(f) is { FlyAnywhere: true, FlightCarriesBody: true }, $"Rules.Of: {Rules.Of(f)}");
        await Press(f, InputBuildingModeFly);
        var spark = SparkOf(f);
        Check(spark != 0, $"with FlyAnywhere the game starts it outside a base: spark {spark} ({Spawn.Inspect(new EntityId(spark))?.TemplateName}), gate decisions turned {Rules.Status()!.FlyAllowed - before}");
        if (spark == 0) { fly.Dispose(); return; }
        var sp = new EntityId(spark);
        Check(Spawn.Inspect(sp)?.TemplateName == "2_Player_Building_Spark", $"the spark is the game's own: {Spawn.Inspect(sp)?.TemplateName}");
        var start = World.Position(f);
        await Steps(20);
        Check(SparkOf(f) == spark, "the flight holds while the rule does");
        Check(World.Position(f) is { } still && start is { } st0 && Dist(still, st0) < 1.0,
            $"during the flight the character stays where it was, as in the game: {World.Position(f)} (was {start})");

        // move the spark as a flight would (its transform, raw), so that the flight ends somewhere else
        if (World.Position(sp) is { } s1 && World.ReadBytes(sp, "CurrentTransform") is { Length: >= 8 } raw)
        {
            World.TryWrite(sp, "CurrentTransform", 0, BitConverter.ToInt64(raw, 0) + (long)(20.0 * 4294967296.0));
            await Steps(6);
            Info($"spark moved {s1} -> {World.Position(sp)}");
        }

        // the rule ends mid-air: the game's gate now refuses outside a base, it ends the flight, Kiln sets the character down
        var health = Combat.GetHealth(f)?.Current;
        var sparkAt = World.Position(sp);
        fly.Dispose();
        for (var i = 0; i < 40 && SparkOf(f) != 0; i++) await Steps(1);
        Check(SparkOf(f) == 0, "the rule ended: the game ended the flight by itself (outside a base)");
        await Steps(10);
        var landed = World.Position(f);
        // the game's ground search picks a free spot near the target: within a few metres, on the ground
        Check(landed is { } l && sparkAt is { } sa && l.Y < sa.Y && Math.Sqrt((l.X - sa.X) * (l.X - sa.X) + (l.Z - sa.Z) * (l.Z - sa.Z)) < 6 &&
              start is { } st1 && Math.Abs(l.X - st1.X) > 10,
            $"brought to the ground below where the flight ended: {landed} (the spark was at {sparkAt}, the flight began at {start})");
        Check(Combat.IsInvulnerable(f), "invulnerable while landing");
        for (var i = 0; i < 600 && Combat.IsInvulnerable(f); i++) await Steps(1);
        Check(!Combat.IsInvulnerable(f), "the landing grace ends once it stands on the ground");
        Check(PlayerStates.Is(f, PlayerStateId.Grounded), $"the character stands on the ground: {World.Position(f)}");
        Check(Combat.GetHealth(f)?.Current == health, $"no damage from the flight or the landing: health {health} -> {Combat.GetHealth(f)?.Current}");

        // leaving the mode by its own key
        fly = Rules.Apply(f, new PlayerRules { FlyAnywhere = true }, label: "fly test 2");
        await Press(f, InputBuildingModeFly);
        var spark2 = SparkOf(f);
        Check(spark2 != 0, $"flying again: spark {spark2}");
        await Steps(10);
        await Press(f, InputBuildingModeFly);
        Check(SparkOf(f) == 0, "the fly key ends the flight as in the game");
        await Steps(15);
        Info($"after leaving by the key: {World.Position(f)}, grounded {PlayerStates.Is(f, PlayerStateId.Grounded)}");
        fly.Dispose();
        await Steps(5);
    }

    // ---- vitals ----
    private async Task Vitals(EntityId f)
    {
        int Get(AbilityResource r) => Abilities.Attributes.Get(f, r) ?? -1;
        int Max(AbilityResource r) => Abilities.Attributes.Max(f, r) ?? -1;
        var set = Rules.Apply(f, new PlayerRules { MinStamina = 1, MinMana = 1 }, label: "vitals test");
        await Steps(4);
        foreach (var r in new[] { AbilityResource.Stamina, AbilityResource.Mana })
        {
            Abilities.Attributes.Add(f, r, -Max(r) / 2);
            var spent = Get(r);
            await Steps(4);
            Check(spent < Max(r) && Get(r) == Max(r), $"{r} floor 1: spent to {spent}, back to {Get(r)} of {Max(r)} within 4 steps");
        }
        set.Dispose();
        await Steps(2);
        Abilities.Attributes.Add(f, AbilityResource.Mana, -Max(AbilityResource.Mana) / 2);
        var low = Get(AbilityResource.Mana);
        await Steps(4);
        Check(Get(AbilityResource.Mana) < Max(AbilityResource.Mana), $"without the rule mana stays spent: {low} -> {Get(AbilityResource.Mana)} of {Max(AbilityResource.Mana)}");

        var h0 = Combat.GetHealth(f)!.Value.Current;
        set = Rules.Apply(f, new PlayerRules { Invulnerable = true }, label: "god test");
        await Steps(2);
        var hit = await Combat.Damage(f, 50, EntityId.None, new DamageOptions { NoCredit = true });
        await Steps(3);
        Check(Combat.GetHealth(f)!.Value.Current == h0, $"invulnerable: a 50 hit ({hit.State}) leaves health at {Combat.GetHealth(f)!.Value.Current} of {h0}");
        set.Update(new PlayerRules { MinHealth = 1 });
        await Steps(2);
        Check(!Combat.IsInvulnerable(f), "updated to a health floor only: not invulnerable any more");
        await Combat.Damage(f, 50, EntityId.None, new DamageOptions { NoCredit = true });
        await Steps(1);
        var hurt = Combat.GetHealth(f)!.Value.Current;
        await Steps(4);
        Check(Combat.GetHealth(f)!.Value.Current == Combat.GetHealth(f)!.Value.Max, $"health floor 1: hurt to {hurt}, back to {Combat.GetHealth(f)!.Value.Current} of {Combat.GetHealth(f)!.Value.Max}");
        set.Dispose();
        await Steps(2);
    }

    // ---- water ----
    private async Task Water(EntityId f)
    {
        var can = Items.Lookup("Tool_T1_WateringCan")!.Id;
        var g = await Items.Give(f, can, 1, options: GiveOptions.NoMerge);
        await Steps(3);
        var pide = Items.Read(f)?.Slots.FirstOrDefault(s => s.Item == can)?.ItemEntity ?? EntityId.None;
        if (!Check(!pide.IsNone, $"the character holds a watering can: {g.Status} {pide}")) return;
        var set = Rules.Apply(f, new PlayerRules { Water = WaterRule.KeepFull("Tool_T1_WateringCan") }, label: "water test");
        await Steps(3);
        Check(Items.Fuel.Read(pide)?.IsFull == true, $"water rule: the can is full: {Items.Fuel.Read(pide)}");
        World.TryWrite(pide, "Fuel", 8, 1000);
        await Steps(2);
        Check(Items.Fuel.Read(pide)?.IsFull == true, $"drained to 1000 (as a pour), full again: {Items.Fuel.Read(pide)}");
        set.Dispose();
        await Steps(2);
        World.TryWrite(pide, "Fuel", 8, 1000);
        await Steps(3);
        Check(Items.Fuel.Read(pide)?.Units == 1000, $"the rule ended: a drain stays: {Items.Fuel.Read(pide)}");
    }

    // ---- terraforming damage ----
    private async Task Digging(EntityId f)
    {
        if (World.Position(f) is not { } at) return;
        async Task<(int Removed, ulong Scaled)> Hit(WorldPosition p)
        {
            var box = Build.BoxAround(p, 2, 2, 2);
            var before = Build.Read(box).Count(c => !c.IsAir);
            var scaled = Rules.Status()!.DigsScaled;
            try
            {
                await Abilities.RunImpact("Terraform_Attack", p, new ImpactOptions { Root = f, Tag = Me });
            }
            catch (Exception e)
            {
                Info($"Terraform_Attack: {e.Message}");
            }
            await Steps(15);
            return (before - Build.Read(box).Count(c => !c.IsAir), Rules.Status()!.DigsScaled - scaled);
        }
        var p1 = at with { X = at.X + 3, Y = at.Y - 1.5 };
        var p2 = at with { X = at.X - 3, Y = at.Y - 1.5 };
        var plain = await Hit(p1);
        var set = Rules.Apply(f, new PlayerRules { DigDamage = 1000 }, label: "dig test");
        await Steps(2);
        var strong = await Hit(p2);
        set.Dispose();
        Info($"terraforming as the character: {plain.Removed} cell(s) without the rule (scaled {plain.Scaled}), {strong.Removed} with DigDamage 1000 (scaled {strong.Scaled})");
        Check(plain.Scaled == 0, "no damage is scaled without a rule");
        if (strong.Scaled > 0) Check(strong.Removed >= plain.Removed, $"with DigDamage 1000 the damage of the character's terraforming is scaled ({strong.Scaled} value(s)) and digs at least as much ({strong.Removed} vs {plain.Removed})");
        else Info("the impact dealt no damage the hook saw (the template's own damage config is empty without a tool): mining needs the manual test");
    }

    // ---- creative mode: Kiln.Rules' own (no Kiln.Admin needed) ----
    private async Task CreativeMode(EntityId f)
    {
        var help = Commands.Execute(CommandSource.Console, "/help creative");
        Check(help.Ok && help.Text.Contains("kiln.command.creative"), $"/creative is Kiln's command (node kiln.command.creative): {help.Text.Split('\n').FirstOrDefault()}");
        Info($"Kiln.Admin loaded: {Mods.All.Any(m => m.Id == "kiln-admin")} (creative needs only Kiln.Rules)");
        var r = Creative.Set(f, true);
        await Steps(3);
        var e = Rules.Of(f);
        Check(r.Ok && r.On && Creative.IsOn(f) && e is { FlyAnywhere: true, BuildCost: 0, DigDamage: 1000, Invulnerable: true, MinStamina: 1 } && e.Water?.KeepsFull == true,
            $"Creative.Set on: {r.Message}");
        Check(Combat.IsInvulnerable(f), "creative: invulnerable");
        Check(Creative.Status().Contains("Creative mode:"), $"status: {Creative.Status()}");
        var off = Creative.Set(f, false);
        await Steps(3);
        Check(off.Ok && !off.On && !Creative.IsOn(f) && Rules.Of(f).IsDefault && !Combat.IsInvulnerable(f), $"Creative.Set off: {off.Message}; rules {Rules.Of(f)}");
        var denied = Commands.Execute(CommandSource.ForSteamId(76561190000000099, "Visitor"), "/creative status");
        Check(denied.Denied, $"a player who is not an admin may not use /creative: {denied.Text}");
    }

    // ---- "near a building zone" (the hammer's "Needs a Flame Altar nearby") ----
    private async Task BuildingZone(EntityId f)
    {
        await Steps(10);
        Check(!PlayerStates.Is(f, PlayerStateId.IsNearBuildingZone), "outside a base the game says: not near a building zone");
        var before = Rules.Status()!.ZoneForced;
        var set = Rules.Apply(f, new PlayerRules { BuildOutsideBases = true }, label: "zone test");
        for (var i = 0; i < 40 && !PlayerStates.Is(f, PlayerStateId.IsNearBuildingZone); i++) await Steps(1);
        Check(PlayerStates.Is(f, PlayerStateId.IsNearBuildingZone),
            $"BuildOutsideBases: the game's own state says near a building zone (what the hammer's hint and the fly gate read); tests turned {Rules.Status()!.ZoneForced - before}");
        set.Dispose();
        for (var i = 0; i < 40 && PlayerStates.Is(f, PlayerStateId.IsNearBuildingZone); i++) await Steps(1);
        Check(!PlayerStates.Is(f, PlayerStateId.IsNearBuildingZone), "the rule ended: not near a building zone again");
    }

    // ---- the Shroud timer ----
    private const uint FogResistanceNow = 0x6951df16, FogResistanceMax = 0xb34ad82f;

    private static int? FogTime(EntityId f) => Abilities.Attributes.Get(f, "FogResistance", FogResistanceNow);

    private async Task Shroud(EntityId f)
    {
        // Springlands Shroud: places the map data marks below the fog (an obelisk, Elixir wells, a Shroud root)
        WorldPosition[] spots =
        [
            new(4547.78, 767.75, 2380.02), new(3164.32, 805.42, 2547.17), new(3364.1, 795.14, 2031.47),
            new(3090.49, 803.9, 2526.04), new(2928.73, 832.45, 2324.47),
        ];
        var inFog = false;
        foreach (var spot in spots)
        {
            var box = Build.BoxAround(spot, 8, 128, 8);
            try { await Build.Load(box); } catch (Exception e) { Info($"loading the terrain at {spot}: {e.Message}"); }
            var ground = GroundY(spot) ?? spot.Y;
            Combat.SetInvulnerable(f);
            var t = await Teleport.To(f, spot with { Y = ground + 1.5 }, new TeleportOptions { FindGround = true });
            for (var i = 0; i < 150 && !(inFog = PlayerStates.Is(f, PlayerStateId.InFog)); i++) await Steps(1);
            Info($"teleported to {spot} ({t.Status}, now {World.Position(f)}): in the Shroud {inFog}, timer {FogTime(f)} of {Abilities.Attributes.Get(f, "FogResistance", FogResistanceMax)}");
            if (inFog) break;
        }
        Combat.SetInvulnerable(f, false);
        if (!Check(inFog, "the character stands in the Shroud (the game's InFog state)")) return;

        async Task<(int From, int To)> Watch(int steps)
        {
            var a = FogTime(f) ?? -1;
            await Steps(steps);
            return (a, FogTime(f) ?? -1);
        }
        var plain = await Watch(150);
        Check(plain.To < plain.From, $"without a rule the timer goes down: {plain.From} -> {plain.To} in 150 steps");

        var held = Rules.Status()!.ShroudHeld;
        var set = Rules.Apply(f, new PlayerRules { FreezeShroudTimer = true }, label: "Shroud test");
        await Steps(2);
        var frozen = await Watch(150);
        Check(frozen.To == frozen.From && PlayerStates.Is(f, PlayerStateId.InFog),
            $"FreezeShroudTimer: the timer holds {frozen.From} -> {frozen.To} in 150 steps, still in the Shroud (steps held {Rules.Status()!.ShroudHeld - held})");
        set.Dispose();
        await Steps(2);
        var after = await Watch(150);
        Check(after.From == frozen.To && after.To < after.From, $"the rule ended: it counts down again from where it stood: {after.From} -> {after.To}");

        var max = Abilities.Attributes.Get(f, "FogResistance", FogResistanceMax) ?? -1;
        set = Rules.Apply(f, new PlayerRules { MinShroudTime = 1 }, label: "Shroud floor test");
        var up = await Watch(150);
        Check(up.To == max, $"MinShroudTime 1: back up to the max at the game's own rate: {up.From} -> {up.To} of {max}");
        set.Dispose();
        Check(Combat.GetHealth(f) is { Dead: false }, $"alive: {Combat.GetHealth(f)?.Current}");
    }

    // ---- several rule sets at once ----
    private async Task Layers(EntityId f)
    {
        var a = Rules.Apply(f, new PlayerRules { BuildCost = 0.5, FlyAnywhere = true }, label: "layer a");
        var b = Rules.Apply(RuleScope.Everyone, new PlayerRules { BuildCost = 0.5, FlyAnywhere = false }, label: "layer b");
        await Steps(2);
        var e = Rules.Of(f);
        Check(e.BuildCost == 0.25 && !e.FlyAnywhere && e.From.Count == 2, $"two rule sets: costs multiply, a denial wins: {e}");
        var area = Rules.Apply(RuleScope.Sphere(World.Position(f)!.Value, 5), new PlayerRules { DigDamage = 4 }, label: "area");
        await Steps(8);
        Check(Rules.Of(f).DigDamage == 4, $"an area around the character applies: dig x{Rules.Of(f).DigDamage}");
        area.Dispose();
        var far = Rules.Apply(RuleScope.Sphere(World.Position(f)!.Value with { X = World.Position(f)!.Value.X + 500 }, 5), new PlayerRules { DigDamage = 4 });
        await Steps(8);
        Check(Rules.Of(f).DigDamage == 1, $"an area elsewhere does not: dig x{Rules.Of(f).DigDamage}");
        far.Dispose();
        var timed = Rules.Apply(f, new PlayerRules { MinMana = 1 }, TimeSpan.FromSeconds(1), "timed");
        await Steps(50);
        Check(!timed.Active && Rules.Of(f).MinMana == 0, $"a rule set with a duration ends by itself: active {timed.Active}");
        a.Dispose();
        b.Dispose();
        await Steps(2);
        Check(Rules.Of(f).IsDefault, $"every rule set disposed: {Rules.Of(f)}");
        var native = Rules.Status()!;
        Check(native.Players == 0, $"the native table is empty again: {native.Players}");
    }
}
