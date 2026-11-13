// SPDX-License-Identifier: MIT
// Lab test of affixes (dev.27), no player. A player-template entity stands in for a player (Affixes.LabTrack): a sword is
// put into its hand through kiln.skills' lab request, and every claim is checked by the value the game's own attribute
// holds (Affixes.StatOf, the game's attribute read run in a small program), not by "something was started".
// Lines "affix-test: PASS/FAIL ..." in kiln.log, then "affix-test: done: N PASS, M FAIL". Boot 1 leaves a sword with a
// roll in a chest; boot 2 finds the roll on it and puts it into a new stand-in's hand.
using System.Reflection;
using Kiln;

namespace AffixTest;

public sealed partial class AffixTestMod : Mod
{
    private int _pass, _fail;
    private bool _stop;
    private WorldPosition _at;
    private readonly List<EntityId> _spawned = [];
    private ItemInfo _sword = null!;
    private string BootFile => Path.Combine(Context.DataDirectory, "boot.txt");

    private bool Check(bool ok, string what)
    {
        if (ok) _pass++;
        else _fail++;
        Log.Info($"affix-test: {(ok ? "PASS" : "FAIL")} {what}");
        return ok;
    }

    private void Info(string what) => Log.Info($"affix-test: INFO {what}");

    private async Task Steps(int n)
    {
        for (var i = 0; i < n && !_stop; i++) await Context.Scheduler.NextTick();
    }

    private async Task<bool> Until(Func<bool> ready, int steps)
    {
        for (var i = 0; i < steps && !_stop; i++)
        {
            if (ready()) return true;
            await Steps(1);
        }
        return ready();
    }

    public override void OnLoad()
    {
        var n = Kiln.Testing.ClientChecks.Register(this);
        DefineForPlayers();
        if (Client.IsClient)
        {
            Log.Info($"affix-test: in a game client: {n} guided check(s), no lab run");
            return;
        }
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");  // kiln.skills' test-only requests (a slot in hand, an equipment slot)
        _ = Run();
    }

    public override void OnUnload() => _stop = true;

    private async Task Run()
    {
        try
        {
            var ready = await Until(() => Spawn.WorldRunning && Items.Status() is { Transactions: true, Catalog: true }, 25 * 240);
            if (!Check(ready, "world running and kiln.items ready")) return;
            await Steps(75);
            var boot = File.Exists(BootFile) && int.TryParse(File.ReadAllText(BootFile), out var b) ? b + 1 : 1;
            Directory.CreateDirectory(Context.DataDirectory);
            File.WriteAllText(BootFile, boot.ToString());
            Info($"boot {boot}");
            _sword = Items.Lookup("Weapon_T1_1H_SwordEpic_01") ?? Items.All.First(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0);
            _at = await Anchor();
            await Until(() => Capabilities.Is("blocks.affixes.effects"), 25 * 30);
            Check(Capabilities.Is("blocks.affixes"), $"capability blocks.affixes: {Capabilities.Why("blocks.affixes")}");
            Check(Capabilities.Is("blocks.affixes.effects"), $"capability blocks.affixes.effects: {Capabilities.Why("blocks.affixes.effects")}");
            if (boot == 1)
            {
                Authoring();
                await EveryStat();
                await Lifecycle();
                await Rolls();
                await LeaveForRestart();
            }
            else await AfterRestart();
        }
        catch (Exception e)
        {
            Check(false, $"aborted: {e}");
        }
        foreach (var e in _spawned)
            if (World.Exists(e)) Spawn.Destroy(e);
        Info(Affixes.LabStatus);
        Log.Info($"affix-test: done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task<WorldPosition> Anchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(inf.Position.X + 40, inf.Position.Y + 0.5, inf.Position.Z - 40);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

    private async Task<EntityId> Make(string template, double dx, double dz)
    {
        var e = await Spawn.Entity(template, new WorldPosition(_at.X + dx, _at.Y, _at.Z + dz), 0, new SpawnOptions { Tag = "affix-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(10);
        return e;
    }

    /// <summary>A sword into the stand-in's bag; its ref.</summary>
    private async Task<ItemInstanceRef> GiveSword(EntityId to)
    {
        var g = await Items.Give(to, _sword.Id, 1, new Gear(Math.Max(1, _sword.LevelMin), ItemRarity.Rare), GiveOptions.NoMerge);
        var r = g.Changes.FirstOrDefault(c => !c.Ref.IsNone).Ref;
        if (r.IsNone) throw new InvalidOperationException($"give: {g}");
        await Steps(10);
        return r;
    }

    /// <summary>Puts the item into the stand-in's hand: the hotbar slot it sits in becomes the selected one.</summary>
    private async Task<bool> Hold(EntityId wearer, ItemInstanceRef item)
    {
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(s => s.Ref == item);
        if (slot is null) return false;
        GameTree.GameRequest(wearer, 5, (uint)slot.Slot.Index);
        return await Until(() => Items.Held(wearer) == item, 50);
    }

    /// <summary>Takes the hand off it: another hotbar slot is selected.</summary>
    private async Task<bool> PutAway(EntityId wearer, ItemInstanceRef item)
    {
        var slot = Items.Read(wearer)?.Slots.FirstOrDefault(s => s.Ref == item);
        GameTree.GameRequest(wearer, 5, (uint)(((slot?.Slot.Index ?? 0) + 3) % 8));
        return await Until(() => Items.Held(wearer) != item, 50);
    }

    private static IEnumerable<Stat> AllStats() =>
        typeof(Stat).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.FieldType == typeof(Stat)).Select(f => (Stat)f.GetValue(null)!);

    private static bool Near(double? a, double b) => a is { } x && Math.Abs(x - b) < 1e-3;

    // ---- authoring: what is refused, and the same shapes as data -------------------------------------------------

    private void Authoring()
    {
        static string Refused(Action define)
        {
            try { define(); return ""; }
            catch (BlockError e) { return e.Message; }
        }
        var ok = new AffixTier { Min = 1, Max = 3 };
        Check(Refused(() => Affixes.Define("Bad Name", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [ok] })).Contains("lower-case"), "a name with capitals or spaces is refused");
        Check(Refused(() => Affixes.Define("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [] })).Contains("1 to 16 tiers"), "an affix without tiers is refused");
        Check(Refused(() => Affixes.Define("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [new AffixTier { Min = -1, Max = 2 }] })).Contains("never holds 0"), "a range across 0 is refused");
        Check(Refused(() => Affixes.Define("a", new AffixBlock { Name = "x", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 1.5, Max = 2 }] })).Contains("whole numbers"), "a fraction for a whole-number stat is refused");
        Check(Refused(() => Affixes.Define("a", new AffixBlock { Name = "x", Stat = Stat.MoveSpeed, Tiers = [new AffixTier { Min = 0.2, Max = 0.1 }] })).Contains("less than min"), "max below min is refused");
        Check(Refused(() => Affixes.DefinePool("p", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "no-such" }] })).Contains("no affix \"affix-test:no-such\""), "a pool that names an affix nobody defined is refused, by name");
        var file = Affixes.Load("""
            { "format": 1, // the same shapes as the C# records
              "affixes": { "json-bear": { "name": "of the Bear", "stat": "strength", "tiers": [ { "min": 2, "max": 4 }, { "min": 5, "max": 9, "weight": 0.5, "minItemLevel": 20 } ], "group": "suffix" },
                           "json-fleet": { "name": "Fleet", "stat": "move-speed", "stacks": false, "tiers": [ { "min": 0.05, "max": 0.1, "step": 0.05 } ] } },
              "pools": { "json-blades": { "items": [ "Weapon_T1_1H_SwordEpic_01" ], "affixes": [ { "affix": "json-bear" }, { "affix": "json-fleet", "weight": 2 } ], "min": 1, "max": 2 } } }
            """);
        Check(file.Affixes.Count == 2 && file.Pools.Count == 1 && Affixes.Find("json-bear") is { Block.Tiers.Count: 2, Block.Group: "suffix" } && Affixes.Find("affix-test:json-fleet") is { Block.Stacks: false } &&
              ReferenceEquals(Affixes.Find("json-fleet")!.Block.Stat, Stat.MoveSpeed) && Affixes.FindPool("json-blades") is { Block.Max: 2 },
            $"a data file defines the same affixes and pools: {string.Join(", ", file.Affixes.Select(a => a.Id))}; {string.Join(", ", file.Pools.Select(p => p.Id))}");
        Check(Refused(() => Affixes.Load("""{ "format": 1, "affixes": { "x": { "name": "x", "stat": "strenght", "tiers": [ { "min": 1, "max": 2 } ] } } }""")).Contains("not a stat"), "a data file with an unknown stat is refused, saying which stats there are");
        Check(Refused(() => Affixes.Load("""{ "format": 2 }""")).Contains("format 1"), "another format is refused");
    }

    // ---- every stat, by the value that changes -----------------------------------------------------------------

    private async Task EveryStat()
    {
        var wearer = await Make("1_Player", 0, 0);
        Affixes.LabTrack(wearer);
        var sword = await GiveSword(wearer);
        if (!Check(await Hold(wearer, sword), $"the sword {sword} is in the stand-in's hand (Items.Held)")) return;
        var good = new List<string>();
        var bad = new List<string>();
        foreach (var stat in AllStats())
        {
            if (_stop) return;
            var value = stat.Whole ? 7 : 0.15;
            var affix = Affixes.Define("s-" + stat.Key, new AffixBlock { Name = "Test " + stat.Key, Stat = stat, Tiers = [new AffixTier { Min = value, Max = value }] });
            var before = await Affixes.StatOf(wearer, stat);
            var add = Affixes.Add(sword, affix.Id);
            await Until(() => Affixes.Active(wearer).Any(a => a.Affix == affix.Id), 40);
            await Steps(4);
            var with = await Affixes.StatOf(wearer, stat);
            var removed = Affixes.Remove(sword, affix.Id);
            await Until(() => Affixes.Active(wearer).Count == 0, 40);
            await Steps(4);
            var after = await Affixes.StatOf(wearer, stat);
            var expect = before is { } b0 ? stat.Subtract ? b0 - value : b0 + value : double.NaN;
            var ok = add.Ok && removed && Near(with, expect) && before is { } b1 && Near(after, b1);
            (ok ? good : bad).Add(stat.Key);
            Check(ok, $"{stat.Key}: the game's attribute {before} -> {with} with \"{add.Affixes.FirstOrDefault()?.Text}\" in hand -> {after} when the affix is gone" +
                      (stat.Subtract ? " (a resistance takes its share off the damage still taken)" : ""));
        }
        Info($"stats that changed and came back: {good.Count} of {good.Count + bad.Count}{(bad.Count > 0 ? "; not: " + string.Join(", ", bad) : "")}");
        Affixes.LabTrack(wearer, false);
        Spawn.Destroy(wearer);
        await Steps(10);
    }

    // ---- what happens to the item, and to who holds it ---------------------------------------------------------

    private async Task Lifecycle()
    {
        var wearer = await Make("1_Player", 4, 0);
        Affixes.LabTrack(wearer);
        var bear = Affixes.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 3, Max = 3 }, new AffixTier { Min = 5, Max = 5 }] });
        var keen = Affixes.Define("keen", new AffixBlock { Name = "Keen", Stat = Stat.CritChance, Stacks = false, Tiers = [new AffixTier { Min = 0.03, Max = 0.03 }, new AffixTier { Min = 0.05, Max = 0.05 }] });
        var applied = new List<AffixesAppliedEvent>();
        var changed = new List<AffixesChangedEvent>();
        using var s1 = Events.On<AffixesAppliedEvent>(applied.Add);
        using var s2 = Events.On<AffixesChangedEvent>(changed.Add);
        var str0 = await Affixes.StatOf(wearer, Stat.Strength);
        var crit0 = await Affixes.StatOf(wearer, Stat.CritChance);
        var a = await GiveSword(wearer);
        var r = Affixes.Add(a, "bear", 1);
        Check(r.Ok && r.Affixes.Single().Text == "+3 Strength (of the Bear)" && changed.Count == 1 && changed[0].Before.Count == 0 && changed[0].Now.Count == 1,
            $"Add: {r}; one AffixesChangedEvent");
        Check(!Affixes.Add(a, "bear", 2).Ok, $"the same affix twice on one item is refused: {Affixes.Add(a, "bear", 2).Reason}");
        try { Affixes.Add(a, "no-such"); Check(false, "an unknown affix did not throw"); }
        catch (ArgumentException e) { Check(true, $"an unknown affix throws ArgumentException: {e.Message}"); }
        try { Affixes.Add(a, "bear", 3); Check(false, "a tier it does not have did not throw"); }
        catch (ArgumentException e) { Check(true, $"a tier it does not have throws ArgumentException: {e.Message}"); }
        try { Affixes.Of(ItemInstanceRef.None); Check(false, "a none ref did not throw"); }
        catch (ArgumentException) { Check(true, "a none ref throws ArgumentException"); }
        await Steps(30);
        Check(Near(await Affixes.StatOf(wearer, Stat.Strength), str0!.Value), $"in the bag, not in hand: nothing acts (strength {str0})");
        Check(Affixes.Active(wearer).Count == 0, "and Affixes.Active says so");

        Check(await Hold(wearer, a), "in hand");
        await Until(() => Affixes.Active(wearer).Count == 1, 40);
        await Steps(4);
        Check(Near(await Affixes.StatOf(wearer, Stat.Strength), str0.Value + 3), $"in hand it acts: strength {str0} + 3");
        Check(applied.Count == 1 && applied[0].Wearer == wearer && applied[0].Now.Single() is { Affix: "affix-test:bear", Stat: "strength", Value: 3 },
            $"one AffixesAppliedEvent: {string.Join(" | ", applied.Select(e => string.Join(",", e.Now.Select(x => $"{x.Affix} {x.Value}"))))}");

        // put away, taken up again
        Check(await PutAway(wearer, a), "put away (another hotbar slot)");
        await Until(() => Affixes.Active(wearer).Count == 0, 40);
        await Steps(4);
        Check(Near(await Affixes.StatOf(wearer, Stat.Strength), str0.Value), "put away, the strength is back");
        await Hold(wearer, a);
        await Until(() => Affixes.Active(wearer).Count == 1, 40);

        // a second item worn at the same time: the equipment inventory's chest slot, which the lab says is that slot
        var b = await GiveSword(wearer);
        var inv = Items.Read(wearer)!;
        var equipment = inv.Slots.First(s => s.Category == InventoryCategory.Equipment).Slot.Inventory;
        const int chestSlot = 6;
        var from = inv.Slots.First(s => s.Ref == b).Slot;
        var move = await Items.Move(wearer, from, wearer, toSlot: SlotId.Of(equipment, chestSlot));
        if (!move.Succeeded)
        {
            // within one holder the move is refused: take it and give it into the slot
            await Items.Take(wearer, _sword.Id, 1, from, true, b.Entity.Id);
            var g = await Items.Give(wearer, _sword.Id, 1, new Gear(Math.Max(1, _sword.LevelMin), ItemRarity.Rare), slot: SlotId.Of(equipment, chestSlot));
            b = g.Changes.FirstOrDefault(c => !c.Ref.IsNone).Ref;
        }
        await Steps(10);
        GameTree.GameRequest(wearer, 8 | (chestSlot << 8), equipment.Value | ((uint)chestSlot << 24));
        var worn = await Until(() => Items.Equipped(wearer, EquipmentSlot.ArmourUpperBody) == b, 60);
        if (Check(worn, $"a second item {b} sits in the stand-in's chest slot (Items.Equipped)"))
        {
            Affixes.Add(b, "bear", 2);
            Affixes.Add(a, "keen", 1);
            Affixes.Add(b, "keen", 2);
            await Until(() => Affixes.Active(wearer).Count == 3, 40);
            await Steps(4);
            var str = await Affixes.StatOf(wearer, Stat.Strength);
            var crit = await Affixes.StatOf(wearer, Stat.CritChance);
            Check(Near(str, str0.Value + 3 + 5), $"two worn items with an affix that stacks add up: strength {str0} -> {str} (+3 and +5)");
            Check(Near(crit, crit0!.Value + 0.05), $"with one that does not stack only the highest counts: crit chance {crit0} -> {crit} (0.03 and 0.05)");
            Info($"active: {string.Join("; ", Affixes.Active(wearer).Select(x => $"{x.Affix} {x.Stat} {x.Value} item {x.Item}"))}");

            // the item goes: into a chest (a move the game makes), then destroyed
            var chest = Items.FindInventories(InventoryKinds.Containers).FirstOrDefault(e => Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 2);
            var out1 = await Items.Move(wearer, SlotId.Of(equipment, chestSlot), chest, options: MoveOptions.Equipped);
            await Until(() => Affixes.Active(wearer).Count == 2, 60);
            await Steps(4);
            str = await Affixes.StatOf(wearer, Stat.Strength);
            crit = await Affixes.StatOf(wearer, Stat.CritChance);
            Check(out1.Succeeded && Near(str, str0.Value + 3) && Near(crit, crit0.Value + 0.03),
                $"the worn item moved into a chest ({out1.Status}): its share is gone, the other's stays: strength {str}, crit chance {crit}");
            Check(Affixes.Of(b).Count == 2, $"in the chest the item still carries its affixes: {string.Join(", ", Affixes.Of(b).Select(x => x.Text))}");
            var at = b.Location();
            if (at is { IsPlaced: true } loc) await Items.Take(loc.Holder, _sword.Id, 1, loc.Slot, true, b.Entity.Id);
        }

        // nothing stops by itself: a minute and a half on
        var status0 = Affixes.LabStatus;
        await Steps(25 * 90);
        var strLate = await Affixes.StatOf(wearer, Stat.Strength);
        Check(Near(strLate, str0.Value + 3), $"90 seconds later the change still holds: strength {strLate}");
        Info($"before the wait: {status0}");
        Info($"after the wait:  {Affixes.LabStatus}");
        Check(Affixes.LabStatus.EndsWith(" 0 found ended by the game"), "the game ended none of the stat changes by itself in that time");

        // the item destroyed while in hand
        var held = a.Location();
        await Items.Take(wearer, _sword.Id, 1, held!.Value.Slot, true, a.Entity.Id);
        await Until(() => Affixes.Active(wearer).Count == 0, 60);
        await Steps(4);
        Check(Near(await Affixes.StatOf(wearer, Stat.Strength), str0.Value) && Near(await Affixes.StatOf(wearer, Stat.CritChance), crit0!.Value),
            "the item destroyed while in hand: nothing stays on who held it");

        // who holds it goes (a player who leaves): what was held ends
        var c1 = await GiveSword(wearer);
        Affixes.Add(c1, "bear", 2);
        await Hold(wearer, c1);
        await Until(() => Affixes.Active(wearer).Count == 1, 60);
        var n = applied.Count;
        Spawn.Destroy(wearer);
        await Until(() => Affixes.Active(wearer).Count == 0, 60);
        Check(Affixes.Active(wearer).Count == 0 && applied.Count == n + 1 && applied[^1].Now.Count == 0, "the holder gone: Kiln holds nothing for it any more, and says so (AffixesAppliedEvent with nothing)");
        Affixes.LabTrack(wearer, false);
        await Steps(10);
        var st = Affixes.LabStatus;
        Info(st);
        Check(st.Contains(" 0 wearer(s) with 0 stat change(s) held"), "no stat change is left running");
    }

    // ---- rolls -------------------------------------------------------------------------------------------------

    private async Task Rolls()
    {
        var holder = await Make("1_Player", 8, 0);
        var sword = await GiveSword(holder);
        Affixes.Define("ember-ward", new AffixBlock { Name = "Ember Ward", Stat = Stat.ResistFire, Group = "ward", Tiers = [new AffixTier { Min = 0.05, Max = 0.2 }] });
        Affixes.Define("frost-ward", new AffixBlock { Name = "Frost Ward", Stat = Stat.ResistIce, Group = "ward", Tiers = [new AffixTier { Min = 0.05, Max = 0.2 }] });
        Affixes.Define("vigor", new AffixBlock { Name = "of Vigor", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 10, Max = 20 }, new AffixTier { Min = 100, Max = 200, MinItemLevel = 500 }] });
        var one = Affixes.DefinePool("one-of-three", new AffixPoolBlock { Items = ["Weapon_T1_1H_SwordEpic_01"], Affixes = [new AffixEntry { Affix = "bear" }, new AffixEntry { Affix = "ember-ward" }, new AffixEntry { Affix = "vigor" }] });
        var wards = Affixes.DefinePool("wards", new AffixPoolBlock { Affixes = [new AffixEntry { Affix = "ember-ward" }, new AffixEntry { Affix = "frost-ward" }, new AffixEntry { Affix = "vigor" }], Min = 3, Max = 3 });
        var other = Affixes.DefinePool("not-swords", new AffixPoolBlock { Items = ["Material_T1_Wood_Logs"], Affixes = [new AffixEntry { Affix = "bear" }] });

        var first = one.Roll(sword, new Random(1));
        Check(first.Ok && first.Affixes.Count == 1, $"a pool of three rolls one onto the sword: {first}");
        var again = one.Roll(sword, new Random(1));
        Check(!again.Ok && again.Affixes.SequenceEqual(first.Affixes), $"an item that has affixes is not rolled again by itself: {again.Reason}");
        Check(!other.Roll(sword, replace: true).Ok, $"a pool that is not for this item type refuses it: {other.Roll(sword, replace: true).Reason}");
        var seen = new Dictionary<string, int>();
        var inRange = true;
        for (var i = 0; i < 90; i++)
        {
            var r = one.Roll(sword, new Random(100 + i), replace: true);
            var x = r.Affixes.Single();
            seen[x.Affix] = seen.GetValueOrDefault(x.Affix) + 1;
            inRange &= x.Affix switch
            {
                "affix-test:bear" => x.Value is 3 or 5 && x.Tier is 1 or 2,
                "affix-test:ember-ward" => x.Value is >= 0.05 and <= 0.2 && Math.Abs(x.Value * 100 - Math.Round(x.Value * 100)) < 1e-6,
                _ => x.Tier == 1 && x.Value is >= 10 and <= 20 && x.Value == Math.Floor(x.Value),  // tier 2 asks item level 500
            };
        }
        Check(seen.Count == 3 && seen.Values.All(n => n >= 15), $"90 rolls: each of the three comes up: {string.Join(", ", seen.Select(kv => $"{kv.Key} {kv.Value}"))}");
        Check(inRange, "every value lies in its tier's range, on its grid (whole points, whole percents), and a tier above the item's level never rolls");
        Check(one.Roll(sword, new Random(7), replace: true).Affixes.SequenceEqual(one.Roll(sword, new Random(7), replace: true).Affixes), "the same seed rolls the same");
        var grouped = wards.Roll(sword, new Random(3), replace: true);
        Check(grouped.Ok && grouped.Affixes.Count == 2 && grouped.Affixes.Count(x => x.Affix.EndsWith("-ward")) == 1,
            $"two affixes of one group never roll together (a pool that asks 3 gives 2): {grouped}");
        Check(Affixes.Clear(sword) && Affixes.Of(sword).Count == 0 && !Affixes.Clear(sword), "Clear takes them all off; a second Clear says there was nothing");
        var set = Affixes.Set(sword, [new RolledAffix("some-mod:gone", "Lost", "strength", 1, 4), new RolledAffix("affix-test:bear", "of the Bear", "strength", 9, 2)]);
        Check(set.Ok && Affixes.Of(sword).Count == 2, $"Set gives exactly what it is told, also an affix of a mod that is not loaded: {set}");
        Affixes.LabTrack(holder);
        await Hold(holder, sword);
        await Until(() => Affixes.Active(holder).Count >= 1, 40);
        await Steps(10);
        Check(Affixes.Active(holder).Select(x => (x.Affix, x.Value)).SequenceEqual([("affix-test:bear", 2.0)]),
            $"an affix whose mod is not loaded stays on the item and does nothing; the one that is defined acts: {string.Join(", ", Affixes.Active(holder).Select(x => $"{x.Affix} {x.Value}"))}");
        Affixes.LabTrack(holder, false);
        Spawn.Destroy(holder);
        await Steps(10);
    }

    // ---- across a restart --------------------------------------------------------------------------------------

    private async Task LeaveForRestart()
    {
        var chest = Items.FindInventories(InventoryKinds.Containers).First(e => Items.Read(e) is { Initialized: true } c && c.FreeSlots() >= 2 && Spawn.Inspect(e) is { TemplateName: not null });
        var free = Items.Read(chest)!.Slots.First(s => s.IsEmpty && s.Category == InventoryCategory.Generic);
        var g = await Items.Give(chest, _sword.Id, 1, new Gear(Math.Max(1, _sword.LevelMin), ItemRarity.Epic), slot: free.Slot);
        var sword = g.Changes.First(c => !c.Ref.IsNone).Ref;
        Affixes.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 3, Max = 3 }, new AffixTier { Min = 5, Max = 5 }] });
        var r = Affixes.Set(sword, [new RolledAffix("affix-test:bear", "of the Bear", "strength", 2, 5), new RolledAffix("affix-test:vigor", "of Vigor", "max-health", 1, 17)]);
        var save = await Saves.Request();
        Check(r.Ok && save.Success, $"a sword with two affixes lies in chest {chest} and the world is saved (revision {save.Revision}): stop the server and start it again");
    }

    private async Task AfterRestart()
    {
        Affixes.Define("bear", new AffixBlock { Name = "of the Bear", Stat = Stat.Strength, Tiers = [new AffixTier { Min = 3, Max = 3 }, new AffixTier { Min = 5, Max = 5 }] });
        Affixes.Define("vigor", new AffixBlock { Name = "of Vigor", Stat = Stat.MaxHealth, Tiers = [new AffixTier { Min = 10, Max = 20 }] });
        ItemInstanceRef found = default;
        EntityId chest = default;
        // the store looks for a chest's records as the world streams in: within a few seconds
        await Until(() =>
        {
            foreach (var e in Items.FindInventories(InventoryKinds.Containers))
                foreach (var s in Items.Read(e)?.Slots ?? [])
                    if (!s.Ref.IsNone && s.Item == _sword.Id && Affixes.Of(s.Ref).Count == 2)
                    {
                        (found, chest) = (s.Ref, e);
                        return true;
                    }
            return false;
        }, 25 * 60);
        if (!Check(!found.IsNone, $"after the restart the sword in chest {chest} carries its two affixes again: {string.Join(", ", found.IsNone ? [] : Affixes.Of(found).Select(a => a.Text))}")) return;
        Check(Affixes.Of(found).Select(a => (a.Affix, a.Tier, a.Value)).SequenceEqual([("affix-test:bear", 2, 5.0), ("affix-test:vigor", 1, 17.0)]), "the same affixes, tiers and values");
        // into another holder's hand: a move, as a gift or a pick from a chest is
        var wearer = await Make("1_Player", 12, 0);
        Affixes.LabTrack(wearer);
        var str0 = await Affixes.StatOf(wearer, Stat.Strength);
        var hp0 = await Affixes.StatOf(wearer, Stat.MaxHealth);
        // onto the action bar (a plain move never lands there), so it can be taken in hand. Which inventory the bar is, a
        // given sword tells: a give to a player-template entity fills the bar first.
        var probe = await GiveSword(wearer);
        var first = Items.Read(wearer)!.Slots.First(s => s.Ref == probe).Slot;
        var bar = SlotId.Of(first.Inventory, first.Index + 1);
        var move = await Items.Move(chest, found.Location()!.Value.Slot, wearer, options: MoveOptions.ActionBar, toSlot: bar);
        Check(move.Succeeded && found.Location() is { } at && at.Holder == wearer, $"Items.Move takes it from the chest to the stand-in's action bar ({move.Status}): the same item entity");
        if (!Check(await Hold(wearer, found), "in the new holder's hand")) return;
        await Until(() => Affixes.Active(wearer).Count == 2, 60);
        await Steps(4);
        var str = await Affixes.StatOf(wearer, Stat.Strength);
        var hp = await Affixes.StatOf(wearer, Stat.MaxHealth);
        Check(Near(str, str0!.Value + 5) && Near(hp, hp0!.Value + 17), $"it acts for the new holder: strength {str0} -> {str}, max health adder {hp0} -> {hp}");
        var loc = found.Location()!.Value;
        await Items.Take(wearer, _sword.Id, 1, loc.Slot, true, found.Entity.Id);
        await Until(() => Affixes.Active(wearer).Count == 0, 60);
        Affixes.LabTrack(wearer, false);
        File.Delete(BootFile);  // the next run starts at boot 1 again
    }
}
