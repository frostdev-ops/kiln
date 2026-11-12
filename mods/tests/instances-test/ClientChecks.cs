// SPDX-License-Identifier: MIT
// Task t3: guided client checks for data on items in a player's inventory (the half a lab cannot prove: a simulated
// player has no inventory). Each check gives the player marked items (Items.Give, then a t3.mark value on each),
// tells the player one thing to do, asserts what the server can read (values, locations, ItemMovedEvent, the store's
// events) and asks for the verdict. Checks that span a leave, a crash or a restart come in two parts, ".before" and
// ".after", that share a plan file in the mod's data folder. Nothing here uses level, experience, skill-point or
// knowledge writes, so no check is tagged cheats. Docs: docs/kiln/modding/inventory.md, "Client checklist".
using System.Text.Json;
using Kiln;
using Kiln.Testing;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    private const string T3Area = "Item data", T3Since = "0.2.0-dev.26";

    [LabOnLoad("t3.client-checks")]
    private void T3RegisterClientChecks()
    {
        var n = ClientChecks.Register(this);
        Info($"t3: {n} guided client check(s) registered");
    }

    // ------------------------------------------------------------------------------------------- helpers ----

    /// <summary>What a two-part check remembers between its parts: the character, each label's flat slot, steps.</summary>
    private sealed class T3Plan
    {
        public Guid Character { get; set; }
        public Dictionary<string, int> Slots { get; set; } = [];
        public Dictionary<string, int> After { get; set; } = [];
        public ulong Step { get; set; }
        public ulong SwapStep { get; set; }
        public string Note { get; set; } = "";
    }

    private string T3PlanFile(string check) => Path.Combine(Context.DataDirectory, "t3", check + ".json");

    private void T3Save(string check, T3Plan p)
    {
        Directory.CreateDirectory(Path.Combine(Context.DataDirectory, "t3"));
        File.WriteAllText(T3PlanFile(check), JsonSerializer.Serialize(p));
    }

    private T3Plan? T3Load(string check) =>
        File.Exists(T3PlanFile(check)) ? JsonSerializer.Deserialize<T3Plan>(File.ReadAllText(T3PlanFile(check))) : null;

    /// <summary>The player runs the check in game (not the control folder).</summary>
    private static bool T3HasPlayer(CheckContext c) =>
        c.Expect(!c.Player.IsNone && Players.Find(c.Player) is { InGame: true }, "a player in game runs the check",
            c.Player.IsNone ? "no player (control folder)" : $"entity {c.Player}");

    private static Guid T3CharacterOf(EntityId player) => Players.Find(player)?.CharacterId ?? Guid.Empty;

    /// <summary>Gives the player one item (a sword by default) and puts a t3.mark value on it. Twins: a sword with a
    /// gem slot forced, so two of them have equal save records.</summary>
    private async Task<ItemInstanceRef> T3Give(CheckContext c, string label, int n, ItemInfo? item = null)
    {
        item ??= Sword();
        if (item == null)
        {
            c.Expect(false, $"give {label}", "no gear item in the catalog");
            return ItemInstanceRef.None;
        }
        var gear = item.Category == ItemCategory.Weapons ? new Gear(10, ItemRarity.Rare) { GemSlot = true } : new Gear(item.LevelMin, ItemRarity.Common);
        var r = await Items.Give(c.Player, item.Id, 1, gear, GiveOptions.Notify | GiveOptions.NoMerge);
        var ch = r.Changes.FirstOrDefault(x => !x.Ref.IsNone);
        if (!c.Expect(r.Status == ItemsStatus.Done && !ch.Ref.IsNone, $"{label}: {item.Label} given and marked", r.Status.ToString()))
            return ItemInstanceRef.None;
        ch.Ref.Set(T3Key, new T3Mark { Label = label, N = n });
        c.Log.Info($"t3 check {c.Id}: {label} = {ch.Ref} at {ch.Ref.Location()}");
        return ch.Ref;
    }

    private static string T3Label(ItemInstanceRef r, InstanceKey<T3Mark> key) =>
        r.TryGet(key, out var m) ? $"{m.Label}={m.N}" : "(no value)";

    /// <summary>The marked items in the player's inventory now: label, value, flat slot, ref.</summary>
    private List<(string Label, int N, int Flat, ItemInstanceRef Ref)> T3Marked(EntityId player)
    {
        var list = new List<(string, int, int, ItemInstanceRef)>();
        if (Items.Read(player) is not { } inv) return list;
        foreach (var s in inv.Slots)
            if (!s.Ref.IsNone && s.Ref.TryGet(T3Key, out var m))
                list.Add((m.Label, m.N, s.Ref.Location()?.FlatSlot ?? -1, s.Ref));
        return list;
    }

    /// <summary>The records of the character that are set aside (Ambiguous) or waiting.</summary>
    private List<ParkedInstance<T3Mark>> T3ParkedOf(Guid character) =>
        InstanceData.Parked(T3Key).Where(p => p.Character.Id == character).ToList();

    /// <summary>Moves of <paramref name="items"/> from now on (the store follows marked items; kiln.items reports).</summary>
    private static List<ItemMovedEvent> T3Watch(CheckContext c, params ItemInstanceRef[] items)
    {
        var seen = new List<ItemMovedEvent>();
        var sub = Events.On<ItemMovedEvent>(e =>
        {
            if (!items.Contains(e.Item)) return;
            seen.Add(e);
            c.Log.Info($"t3 check {c.Id}: moved {e.Item} {e.From} -> {e.To} ({e.Cause}, step {e.Step})");
        });
        c.OnEnd(() => sub.Dispose());
        return seen;
    }

    private void T3Unmark(CheckContext c, params ItemInstanceRef[] items) =>
        c.OnEnd(() =>
        {
            foreach (var i in items)
                if (!i.IsNone)
                    i.Remove(T3Key);
        });

    /// <summary>The second part of a leave, restart or crash check: the character's marked items after the rejoin. Each
    /// label must be at the slot in <paramref name="expected"/> or set aside; never at another label's slot.</summary>
    private async Task T3AfterRejoin(CheckContext c, string check, T3Plan plan, IReadOnlyDictionary<string, int> expected, bool allowAside)
    {
        c.Expect(T3CharacterOf(c.Player) == plan.Character, "the same character came back", $"{T3CharacterOf(c.Player)} (planned {plan.Character})");
        await c.Until(() => T3Marked(c.Player).Count + T3ParkedOf(plan.Character).Count(p => p.State == ParkedState.SetAside) >= expected.Count,
            TimeSpan.FromSeconds(60), "every marked item is back or set aside");
        var now = T3Marked(c.Player);
        var aside = T3ParkedOf(plan.Character).Where(p => p.State == ParkedState.SetAside).ToList();
        foreach (var (label, slot) in expected)
        {
            var on = now.Where(x => x.Label == label).ToList();
            if (on.Count == 1)
                c.Expect(on[0].Flat == slot, $"{label} is on the item in its slot {slot}", $"on the item in slot {on[0].Flat}");
            else if (on.Count == 0)
                c.Expect(allowAside && aside.Any(p => p.Value?.Label == label), $"{label} is set aside and reported (not on another item)",
                    aside.Any(p => p.Value?.Label == label) ? "set aside" : "neither on an item nor set aside");
            else
                c.Expect(false, $"{label} is on one item", $"on {on.Count} items");
        }
        c.Expect(now.All(x => !expected.ContainsKey(x.Label) || expected[x.Label] == x.Flat), "no value is on another item",
            string.Join(", ", now.Select(x => $"{x.Label}@{x.Flat}")));
        c.Log.Info($"t3 check {check}: after the rejoin: on items [{string.Join(", ", now.Select(x => $"{x.Label}@{x.Flat}"))}], " +
                   $"set aside [{string.Join(", ", aside.Select(p => $"{p.Value?.Label}: {p.Reason}"))}]");
    }

    // -------------------------------------------------------------------------------- in one session ----

    [ClientCheck("instances.held-moves", "A value on an item follows it: action bar, hand, chest, back", Area = T3Area, Since = T3Since,
        Order = 10)]
    public async Task T3HeldMoves(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var s = await T3Give(c, "held", 1);
        if (s.IsNone) { await c.Verdict(); return; }
        T3Unmark(c, s);
        var moves = T3Watch(c, s);
        var held = false;
        c.Instruct("A sword was put into your backpack. Drag it onto your action bar and take it in hand; then drag it into a chest; " +
                   "then drag it back into your backpack. One drag at a time.");
        await c.Until(() =>
        {
            held |= Items.Held(c.Player) == s;
            return held && moves.Any(m => m.To.IsPlaced && m.To.Holder != c.Player) && s.Location() is { } l && l.Holder == c.Player &&
                   moves.Count >= 3;
        }, TimeSpan.FromMinutes(5), "in hand, in a chest, and back in the backpack");
        c.Expect(held, "Items.Held named this sword while it was in hand");
        c.Expect(moves.Count > 0 && moves.All(m => m.Cause == ItemMoveCause.Observed), "every drag came as ItemMovedEvent with cause Observed",
            string.Join(", ", moves.Select(m => $"{m.From}->{m.To} {m.Cause}")));
        c.Expect(moves.Any(m => m.To.IsPlaced && m.To.Holder != c.Player && Items.Read(m.To.Holder) is { IsPlayer: false }),
            "one move named the chest as holder");
        c.Expect(s.TryGet(T3Key, out var v) && v.Label == "held" && v.N == 1, "the value is still on this sword", T3Label(s, T3Key));
        c.Instruct("Answer pass when you did the four drags and nothing else happened to the sword.");
        await c.Verdict();
    }

    [ClientCheck("instances.twins-swap", "Two identical items with different values, swapped by hand, into a chest and back",
        Area = T3Area, Since = T3Since, Order = 20)]
    public async Task T3TwinsSwap(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var a = await T3Give(c, "twin-1", 1);
        var b = await T3Give(c, "twin-2", 2);
        if (a.IsNone || b.IsNone) { await c.Verdict(); return; }
        T3Unmark(c, a, b);
        var moves = T3Watch(c, a, b);
        int fa = a.Location()?.FlatSlot ?? -1, fb = b.Location()?.FlatSlot ?? -1;
        c.Instruct("Two identical swords were put into your backpack. Drag one onto the other so they swap places.");
        var swapped = await c.Until(() => a.Location()?.FlatSlot == fb && b.Location()?.FlatSlot == fa, TimeSpan.FromMinutes(3), "the two swords swapped places");
        c.Expect(swapped && a.TryGet(T3Key, out var va) && va.N == 1 && b.TryGet(T3Key, out var vb) && vb.N == 2,
            "after the swap each sword keeps its own value", $"{T3Label(a, T3Key)} at {a.Location()}, {T3Label(b, T3Key)} at {b.Location()}");
        c.Instruct("Now drag both swords into a chest, one at a time, then both back into your backpack.");
        await c.Until(() => moves.Count(m => m.To.IsPlaced && m.To.Holder != c.Player) >= 2 &&
                            a.Location()?.Holder == c.Player && b.Location()?.Holder == c.Player, TimeSpan.FromMinutes(5),
            "both were in a chest and are back");
        c.Expect(T3Label(a, T3Key) == "twin-1=1" && T3Label(b, T3Key) == "twin-2=2", "each sword still has its own value",
            $"{T3Label(a, T3Key)} at {a.Location()}, {T3Label(b, T3Key)} at {b.Location()}");
        c.Expect(moves.All(m => m.Cause == ItemMoveCause.Observed || m.Cause == ItemMoveCause.Transfer), "the moves came as Observed (drags) or Transfer (quick stack)",
            string.Join(", ", moves.Select(m => m.Cause)));
        await c.Verdict();
    }

    [ClientCheck("instances.equip", "Equip and unequip a marked item; Items.Equipped names it", Area = T3Area, Since = T3Since, Order = 30)]
    public async Task T3Equip(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var armour = Items.Lookup("0x2efa3d10") ??
                     Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Equipment && i.EquipSlot != 0 && i.LevelMin > 0);
        var s = await T3Give(c, "worn", 3, armour);
        if (s.IsNone) { await c.Verdict(); return; }
        T3Unmark(c, s);
        var moves = T3Watch(c, s);
        EquipmentSlot? slot = null;
        c.Instruct($"A {armour?.Label} was put into your backpack. Put it on (equip it), wait a moment, then take it off again.");
        await c.Until(() =>
        {
            foreach (var e in Enum.GetValues<EquipmentSlot>())
                if (Items.Equipped(c.Player, e) == s) slot = e;
            return slot != null;
        }, TimeSpan.FromMinutes(3), "Items.Equipped names the marked item in one slot");
        c.Expect(slot != null, "Items.Equipped named it while it was worn", slot?.ToString());
        c.Expect(s.Location() is { IsSavedSlot: false }, "while worn its flat slot is outside the saved backpack slots (256 and up)", s.Location()?.ToString());
        await c.Until(() => slot is { } e && Items.Equipped(c.Player, e) != s && s.Location()?.Holder == c.Player, TimeSpan.FromMinutes(3), "taken off again");
        c.Expect(moves.Count >= 2 && moves.All(m => m.Cause == ItemMoveCause.Observed), "equip and unequip came as ItemMovedEvent, cause Observed",
            string.Join(", ", moves.Select(m => $"{m.From}->{m.To} {m.Cause}")));
        c.Expect(T3Label(s, T3Key) == "worn=3", "the value stayed on it", T3Label(s, T3Key));
        await c.Verdict();
    }

    [ClientCheck("instances.held-equipped", "Items.Held and every Items.Equipped slot name what you hold and wear", Area = T3Area,
        Since = T3Since, Order = 40)]
    public async Task T3HeldEquipped(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        c.Instruct("Take a weapon in hand and wear some armour, then wait.");
        await c.Delay(TimeSpan.FromSeconds(10));
        string Name(ItemInstanceRef r) => r.IsNone ? "-" : Items.Get(r.Item)?.Label ?? r.Item.ToString();
        var held = Items.Held(c.Player);
        var worn = Enum.GetValues<EquipmentSlot>().Select(e => (e, Items.Equipped(c.Player, e))).Where(x => !x.Item2.IsNone).ToList();
        var text = $"In hand: {Name(held)}. Worn: {(worn.Count == 0 ? "nothing" : string.Join(", ", worn.Select(x => $"{x.e} {Name(x.Item2)}")))}.";
        c.Log.Info($"t3 check {c.Id}: {text}");
        c.Expect(worn.All(x => x.Item2.Location()?.Holder == c.Player), "every equipped item is in this player's inventories");
        c.Instruct(text + " Pass if that is exactly what you hold and wear; fail with what differs.");
        await c.Verdict();
    }

    [ClientCheck("instances.trash", "Trash a marked item: its data is reported destroyed, not kept", Area = T3Area, Since = T3Since, Order = 50)]
    public async Task T3Trash(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var s = await T3Give(c, "trash", 5);
        if (s.IsNone) { await c.Verdict(); return; }
        var lost = T3Log.Count;
        c.Instruct("A sword was put into your backpack. Trash it (the item's Trash action).");
        await c.Until(() => T3Log.Skip(lost).Any(x => x.Kind == "lost" && x.Who == s.ToString()), TimeSpan.FromMinutes(3), "its data is reported lost");
        var mine = T3Log.Skip(lost).Where(x => x.Who == s.ToString() || x.Kind == "parked").ToList();
        c.Expect(mine.Any(x => x.Kind == "lost" && x.Detail.StartsWith(nameof(InstanceLossReason.Destroyed))), "InstanceDataLostEvent with reason Destroyed",
            string.Join("; ", mine.Select(x => $"{x.Kind} {x.Detail}")));
        c.Expect(!mine.Any(x => x.Kind == "parked"), "nothing parked with the character");
        c.Expect(!s.Has(T3Key), "the old ref has no value");
        await c.Verdict();
    }

    [ClientCheck("instances.craft", "Craft with a marked item as an ingredient: its data is reported destroyed", Area = T3Area,
        Since = T3Since, Order = 55)]
    public async Task T3Craft(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        // mark every item of the player's own that has an item entity, so whichever one the recipe takes is watched
        var inv = Items.Read(c.Player);
        var marked = inv?.Slots.Where(x => !x.Ref.IsNone && !x.Ref.Has(T3Key)).Select(x => x.Ref).ToList() ?? [];
        for (var i = 0; i < marked.Count; i++) marked[i].Set(T3Key, new T3Mark { Label = $"own-{i}", N = i });
        T3Unmark(c, marked.ToArray());
        var from = T3Log.Count;
        c.Instruct($"{marked.Count} of your gear and tool items carry a value now. Craft something at a station that uses one of " +
                   "them as an ingredient. If no recipe in your world does, answer skip.");
        await c.Until(() => T3Log.Skip(from).Any(x => x.Kind == "lost"), TimeSpan.FromMinutes(5), "one marked ingredient's data is reported lost");
        var seen = T3Log.Skip(from).ToList();
        c.Expect(seen.Any(x => x.Kind == "lost" && x.Detail.StartsWith(nameof(InstanceLossReason.Destroyed))), "the ingredient's data: Destroyed",
            string.Join("; ", seen.Select(x => $"{x.Kind} {x.Detail}")));
        c.Expect(!seen.Any(x => x.Kind == "parked"), "nothing parked with the character");
        await c.Verdict();
    }

    [ClientCheck("instances.tombstone", "Die with a marked item that drops to the tombstone, loot it back", Area = T3Area, Since = T3Since,
        Order = 60)]
    public async Task T3Tombstone(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var s = await T3Give(c, "grave", 6);
        if (s.IsNone) { await c.Verdict(); return; }
        T3Unmark(c, s);
        var moves = T3Watch(c, s);
        c.Instruct("Needs the server's tombstone mode that drops everything (the default keeps gear on you). A sword was put into " +
                   "your backpack. Die, go to your tombstone and loot it back into your backpack.");
        await c.Until(() => moves.Any(m => m.To.IsPlaced && m.To.Holder != c.Player), TimeSpan.FromMinutes(10), "the sword went to the tombstone");
        await c.Until(() => s.Location()?.Holder == c.Player, TimeSpan.FromMinutes(10), "the sword is back in the backpack");
        c.Expect(moves.All(m => m.Cause == ItemMoveCause.Observed), "death and loot came as ItemMovedEvent, cause Observed",
            string.Join(", ", moves.Select(m => $"{m.From}->{m.To} {m.Cause}")));
        c.Expect(T3Label(s, T3Key) == "grave=6", "the value stayed on the sword", T3Label(s, T3Key));
        await c.Verdict();
    }

    [ClientCheck("instances.gift", "Gift a marked item to a second player", Area = T3Area, Since = T3Since,
        Tags = [ClientCheckTags.TwoPlayers], Order = 70)]
    public async Task T3Gift(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var s = await T3Give(c, "gift", 7);
        if (s.IsNone) { await c.Verdict(); return; }
        T3Unmark(c, s);
        var moves = T3Watch(c, s);
        c.Instruct("A sword was put into your backpack. Give it to the other player (the gift action, standing next to them).");
        await c.Until(() => s.Location() is { } l && l.Holder != c.Player && Players.Find(l.Holder) is { InGame: true }, TimeSpan.FromMinutes(5),
            "the sword is in another player's inventory");
        c.Expect(moves.Any(m => m.Cause == ItemMoveCause.Observed && Players.Find(m.To.Holder) is { } p && p.Entity != c.Player),
            "the gift came as ItemMovedEvent, cause Observed, to the other player", string.Join(", ", moves.Select(m => $"{m.From}->{m.To} {m.Cause}")));
        c.Expect(T3Label(s, T3Key) == "gift=7", "the value went with the sword", T3Label(s, T3Key));
        await c.Verdict();
    }

    // ------------------------------------------------------------------- across a leave, a crash, a restart ----

    [ClientCheck("instances.leave.before", "Leave from the menu and rejoin (1 of 2): items marked", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 100)]
    public async Task T3LeaveBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var plan = await T3Prepare(c, ["leave-1", "leave-2", "leave-u"]);
        T3Save("leave", plan);
        c.Instruct("Three swords now carry values (two of them identical). Answer pass, then leave to the main menu through the game's " +
                   "menu, join this server again and run \"instances.leave.after\".");
        await c.Verdict();
    }

    [ClientCheck("instances.leave.after", "Leave from the menu and rejoin (2 of 2): values back, in the leave's order", Area = T3Area,
        Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 101)]
    public async Task T3LeaveAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("leave") is not { } plan) { c.Expect(false, "instances.leave.before ran first"); await c.Verdict(); return; }
        await T3AfterRejoin(c, "leave", plan, plan.Slots, allowAside: false);
        // the order at the leave (out/instances/t3/leave-order.md): the leave save, then kiln.players' Left, then the items parked
        var after = T3Log.Where(x => x.Step >= plan.Step).ToList();
        var save = after.FirstOrDefault(x => x.Kind == "saving" && x.Detail == nameof(SaveReason.PlayerLeft));
        var left = after.FirstOrDefault(x => x.Kind == "left" && x.Who == plan.Character.ToString());
        var parked = after.Where(x => x.Kind == "parked" && x.Who == plan.Character.ToString()).ToList();
        c.Log.Info($"t3 check leave: leave save step {save.Step}, Left step {left.Step}, parked steps [{string.Join(",", parked.Select(p => p.Step))}]");
        c.Expect(save.Kind != null && left.Kind != null && parked.Count == plan.Slots.Count && save.Step <= left.Step && parked.All(p => p.Step >= left.Step),
            "the leave save came first, then Players.Left, then each marked item was parked with the character (PlayerLeft)",
            $"save {save.Step}, left {left.Step}, parked {parked.Count} at [{string.Join(",", parked.Select(p => p.Step))}]");
        c.Expect(!after.Any(x => x.Kind == "lost"), "no marked item was reported destroyed at the leave");
        await c.Verdict();
    }

    [ClientCheck("instances.move-leave.before", "Move, then leave within a second (1 of 2)", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 110)]
    public async Task T3MoveLeaveBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var s = await T3Give(c, "quick", 8);
        if (s.IsNone) { await c.Verdict(); return; }
        var plan = new T3Plan { Character = T3CharacterOf(c.Player), Step = T3Step };
        T3Save("move-leave", plan);
        c.Instruct("Answer pass, then drag the new sword to another slot of your backpack and leave to the main menu at once (within a " +
                   "second). Join again and run \"instances.move-leave.after\".");
        await c.Verdict();
        var from = s.Location()?.FlatSlot ?? -1;
        await c.Until(() => s.Location() is not { } l || l.FlatSlot != from, TimeSpan.FromMinutes(2), "the sword moved");
        plan.Slots["quick"] = s.Location()?.FlatSlot ?? -1;
        T3Save("move-leave", plan);
    }

    [ClientCheck("instances.move-leave.after", "Move, then leave within a second (2 of 2): the value in the new slot", Area = T3Area,
        Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 111)]
    public async Task T3MoveLeaveAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("move-leave") is not { } plan || !plan.Slots.ContainsKey("quick"))
        {
            c.Expect(false, "instances.move-leave.before ran first and saw the move");
            await c.Verdict();
            return;
        }
        await T3AfterRejoin(c, "move-leave", plan, plan.Slots, allowAside: false);
        await c.Verdict();
    }

    [ClientCheck("instances.crash-swap.before", "Swap two identical marked items, then close the game (1 of 2)", Area = T3Area,
        Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 120)]
    public async Task T3CrashSwapBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var a = await T3Give(c, "crash-1", 1);
        var b = await T3Give(c, "crash-2", 2);
        if (a.IsNone || b.IsNone) { await c.Verdict(); return; }
        var save = await Saves.Request();   // the character file the client keeps: both swords where they are now
        c.Expect(save.Success, "a save after marking (the client's file has the swords where they are now)", save.ToString());
        var plan = new T3Plan { Character = T3CharacterOf(c.Player), Step = T3Step };
        plan.Slots["crash-1"] = a.Location()?.FlatSlot ?? -1;
        plan.Slots["crash-2"] = b.Location()?.FlatSlot ?? -1;
        T3Save("crash-swap", plan);
        c.Instruct("Two identical swords now carry different values. Drag one onto the other so they swap places.");
        var swapped = await c.Until(() => a.Location()?.FlatSlot == plan.Slots["crash-2"] && b.Location()?.FlatSlot == plan.Slots["crash-1"],
            TimeSpan.FromMinutes(3), "the two swords swapped places");
        plan.SwapStep = T3Step;
        plan.After["crash-1"] = plan.Slots["crash-2"];
        plan.After["crash-2"] = plan.Slots["crash-1"];
        T3Save("crash-swap", plan);
        c.Expect(swapped, "the swap was seen", $"{a.Location()}, {b.Location()}");
        c.Instruct("Answer pass, then at once close the game without the menu (Alt+F4, or end the game's process). Start it again, " +
                   "join this server and run \"instances.crash-swap.after\".");
        await c.Verdict();
    }

    [ClientCheck("instances.crash-swap.after", "Swap two identical marked items, then close the game (2 of 2): never the other's value",
        Area = T3Area, Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 121)]
    public async Task T3CrashSwapAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("crash-swap") is not { SwapStep: > 0 } plan) { c.Expect(false, "instances.crash-swap.before ran first and saw the swap"); await c.Verdict(); return; }
        var since = T3Log.Where(x => x.Step >= plan.SwapStep).ToList();
        var left = since.FirstOrDefault(x => x.Kind == "left" && x.Who == plan.Character.ToString());
        var leaveSave = since.Any(x => x.Kind == "saving" && x.Detail == nameof(SaveReason.PlayerLeft) && (left.Kind == null || x.Step <= left.Step));
        var saveBetween = since.Any(x => x.Kind == "saving" && (left.Kind == null || x.Step <= left.Step));
        c.Expect(!leaveSave, "the leave was a crash: no leave save", leaveSave ? "a PlayerLeft save came: the game left through its menu" : "none");
        c.Log.Info($"t3 check crash-swap: a save between the swap and the leave: {saveBetween}");
        // no save between the swap and the crash: the client's file has the swords as before the swap; with one, the file may be
        // either state, and only "never the other's value" can be asserted
        var now = T3Marked(c.Player);
        await c.Until(() => (now = T3Marked(c.Player)).Count + T3ParkedOf(plan.Character).Count(p => p.State == ParkedState.SetAside) >= 2,
            TimeSpan.FromSeconds(60), "both values are back or set aside");
        string Where(string l) => now.FirstOrDefault(x => x.Label == l) is { Label: not null } x ? x.Flat.ToString() : "aside";
        var pre = plan.Slots.All(kv => Where(kv.Key) == kv.Value.ToString() || Where(kv.Key) == "aside");
        var post = plan.After.All(kv => Where(kv.Key) == kv.Value.ToString() || Where(kv.Key) == "aside");
        c.Log.Info($"t3 check crash-swap: crash-1 at {Where("crash-1")}, crash-2 at {Where("crash-2")} (before the swap {plan.Slots["crash-1"]}, {plan.Slots["crash-2"]})");
        c.Expect(saveBetween ? pre || post : pre,
            "each sword has its own value as the client's file has it, or both are set aside; never the other's",
            $"crash-1 at {Where("crash-1")}, crash-2 at {Where("crash-2")}; slots before the swap {plan.Slots["crash-1"]}/{plan.Slots["crash-2"]}" +
            (saveBetween ? "; a save came between the swap and the crash: either state is the file's" : ""));
        await c.Verdict();
    }

    [ClientCheck("instances.shutdown.before", "Server shutdown with you in game (1 of 2)", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 130)]
    public async Task T3ShutdownBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var plan = await T3Prepare(c, ["down-1", "down-2", "down-u"]);
        T3Save("shutdown", plan);
        c.Instruct("Three swords now carry values. Answer pass and stay in game: the server's admin stops the server (a normal stop, " +
                   "not a kill) and starts it again. Then join and run \"instances.shutdown.after\".");
        await c.Verdict();
    }

    [ClientCheck("instances.shutdown.after", "Server shutdown with you in game (2 of 2): values back", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 131)]
    public async Task T3ShutdownAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("shutdown") is not { } plan) { c.Expect(false, "instances.shutdown.before ran first"); await c.Verdict(); return; }
        await T3AfterRejoin(c, "shutdown", plan, plan.Slots, allowAside: false);
        await c.Verdict();
    }

    [ClientCheck("instances.restart.before", "Leave, server restart, rejoin (1 of 2)", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 140)]
    public async Task T3RestartBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var plan = await T3Prepare(c, ["restart-1", "restart-2", "restart-u"]);
        T3Save("restart", plan);
        c.Instruct("Three swords now carry values. Answer pass, leave through the menu; the admin restarts the server; join and run " +
                   "\"instances.restart.after\".");
        await c.Verdict();
    }

    [ClientCheck("instances.restart.after", "Leave, server restart, rejoin (2 of 2): values back", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 141)]
    public async Task T3RestartAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("restart") is not { } plan) { c.Expect(false, "instances.restart.before ran first"); await c.Verdict(); return; }
        await T3AfterRejoin(c, "restart", plan, plan.Slots, allowAside: false);
        await c.Verdict();
    }

    [ClientCheck("instances.elsewhere.before", "The character played elsewhere in between (1 of 2)", Area = T3Area, Since = T3Since,
        Needs = ClientCheckNeeds.Joined, Order = 150)]
    public async Task T3ElsewhereBefore(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        var plan = await T3Prepare(c, ["away-1", "away-2", "away-u"]);
        T3Save("elsewhere", plan);
        c.Instruct("Three swords now carry values (two identical, one different). Answer pass, leave through the menu, play this " +
                   "character in another world without Kiln (your own single-player world): move the different sword to another slot " +
                   "and swap the two identical ones there. Leave, join this server again and run \"instances.elsewhere.after\".");
        await c.Verdict();
    }

    [ClientCheck("instances.elsewhere.after", "The character played elsewhere in between (2 of 2): moved or set aside, never another's value",
        Area = T3Area, Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 151)]
    public async Task T3ElsewhereAfter(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        if (T3Load("elsewhere") is not { } plan) { c.Expect(false, "instances.elsewhere.before ran first"); await c.Verdict(); return; }
        await c.Until(() => T3Marked(c.Player).Count + T3ParkedOf(plan.Character).Count(p => p.State == ParkedState.SetAside) >= 3,
            TimeSpan.FromSeconds(60), "the values are back or set aside");
        var now = T3Marked(c.Player);
        var aside = T3ParkedOf(plan.Character).Where(p => p.State == ParkedState.SetAside).Select(p => p.Value?.Label).ToList();
        c.Log.Info($"t3 check elsewhere: on items [{string.Join(", ", now.Select(x => $"{x.Label}@{x.Flat}"))}], set aside [{string.Join(", ", aside)}]");
        // the inventory changed while no Kiln watched: the unique sword is found by its fingerprint (Moved); the twins can't be
        // told apart, so both are set aside
        c.Expect(now.Any(x => x.Label == "away-u"), "the different sword has its value (found where it was moved)",
            now.FirstOrDefault(x => x.Label == "away-u").Flat.ToString());
        c.Expect(aside.Contains("away-1") && aside.Contains("away-2") && !now.Any(x => x.Label is "away-1" or "away-2"),
            "the two identical swords are both set aside and reported", $"set aside: {string.Join(", ", aside)}");
        await c.Verdict();
    }

    [ClientCheck("instances.rejoin-ids", "After a rejoin: item entity ids beside their flat slots (log only)", Area = T3Area,
        Since = T3Since, Needs = ClientCheckNeeds.Joined, Order = 160)]
    public async Task T3RejoinIds(CheckContext c)
    {
        if (!T3HasPlayer(c)) { await c.Verdict(); return; }
        c.Instruct("Run this right after joining (best: leave and join again first, and do nothing in between). Then answer pass.");
        // The store takes an exact-slot match among look-alikes on trust only when the ids ascend in flat-slot order in one
        // unbroken run (the loader order measured for chests); nobody has seen a player's inventory load yet. The line is the
        // result; nothing is asserted.
        var inv = Items.Read(c.Player);
        var items = inv?.Slots.Where(s => !s.Ref.IsNone).Select(s => (Flat: s.Ref.Location()?.FlatSlot ?? -1, Id: s.ItemEntity.Value))
            .OrderBy(x => x.Flat).ToList() ?? [];
        var ascending = items.Zip(items.Skip(1)).All(p => p.Second.Id > p.First.Id);
        var run = items.Count > 0 && items.Last().Id - items.First().Id == (uint)(items.Count - 1);
        c.Log.Info($"t3 check rejoin-ids: player {c.Player}, {items.Count} item entities, flat:id " +
                   $"{string.Join(" ", items.Select(x => $"{x.Flat}:{x.Id}"))}; ascending {ascending}, one run {run}");
        await c.Verdict();
    }

    /// <summary>Two identical swords and one other item, marked; their slots and the character in a plan.</summary>
    private async Task<T3Plan> T3Prepare(CheckContext c, string[] labels)
    {
        var plan = new T3Plan { Character = T3CharacterOf(c.Player), Step = T3Step };
        var other = Items.All.FirstOrDefault(i => i.IsGear && i.Category == ItemCategory.Weapons && i.LevelMin > 0 && i.Id != Sword()?.Id);
        for (var i = 0; i < labels.Length; i++)
        {
            var r = await T3Give(c, labels[i], i + 1, i == 2 ? other : null);
            plan.Slots[labels[i]] = r.IsNone ? -1 : r.Location()?.FlatSlot ?? -1;
        }
        c.Expect(plan.Character != Guid.Empty, "the player's character has an id", plan.Character.ToString());
        c.Expect(plan.Slots.Values.All(s => s is >= 0 and < 256), "all three are in saved backpack slots", string.Join(", ", plan.Slots));
        return plan;
    }
}
