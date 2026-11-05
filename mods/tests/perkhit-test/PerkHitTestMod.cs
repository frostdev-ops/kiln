// SPDX-License-Identifier: MIT
// On-hit perks, player-free. A perk's program sits on its wearer and reads the hits the wearer lands (OnHit stream). The
// hits here go through the game's own applyDamage and hit conclusion (impact/hit.kimp), from a player-template entity to a
// Vukah, with a chosen weapon category: the vanilla Poisoned perk's program is the control (it reads BuffType and
// ChanceValue from the instance's configs, and has a weapon check that lets daggers through), then NewItems' Frostbite Edge.
using Kiln;

namespace PerkHitTest;

public sealed class PerkHitTestMod : Mod
{
    private int _pass, _fail;
    private static readonly Guid VanillaProgram = Guid.Parse("4abf7534-ad1c-45f3-9ba7-adbe812ea3bf");
    private static readonly Guid PoisonGuid = Guid.Parse("52db2488-61a6-4ed4-81be-0b06878f1fb5");
    private static readonly Guid FrostbiteGuid = Guid.Parse("c7a1e5d2-49b6-4f80-a3d1-2e6b9f0c4a58");
    private static readonly Guid HitProgram = Guid.Parse("6ec4a48d-0d7b-4f8f-a831-17ee57be9d0e");
    private const uint CfgBuff = 0x5adb2644, CfgChance = 0xde1695c2;
    private const uint PoisonId = 0x5f425faf, FrostbiteId = 0x26317bd2;
    private readonly List<EntityId> _spawned = new();

    private void Info(string s) => Log.Info($"perkhit-test: INFO {s}");
    private void Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"perkhit-test: PASS {what}"); }
        else { _fail++; Log.Error($"perkhit-test: FAIL {what}"); }
    }

    private bool _landed;

    public override void OnLoad()
    {
        if (!Client.IsDedicatedServer) return;
        Directory.CreateDirectory(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills")));
        File.WriteAllText(Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops")), "");   // the test-only requests of kiln.skills are off without it
        Events.On<ImpactSignalEvent>(e => { if (e.Tag == 0x701) _landed = true; });
        // For the client run: a real Glacier from the hotbar has no unlocked enhancement until it is upgraded. No level, no XP,
        // no skill points involved. The next equip (or the same tick) starts the game's perk instance.
        Commands.Register("perkhit-arm", ArmCommand, CommandPermission.Admin,
            description: "test: unlock 3 enhancements on the Glaciers you carry and set Frostbite Edge's chance to 100%");
        _ = Run();
    }

    private void ArmCommand(CommandContext c)
    {
        var player = c.Source.Player;
        var slots = Items.Read(player)?.Slots.Where(x => !x.IsEmpty && x.Item.Value == 0x723a87fa && !x.ItemEntity.IsNone).ToList() ?? [];
        foreach (var s in slots) GameTree.GameRequest(s.ItemEntity, 4, 3);
        Content.Perks.Get("Kiln_Sample_FrostbiteEdge")?.Set("impactValues.ChanceValue", 1.0);
        Log.Info($"perkhit-test: armed {slots.Count} Glacier(s) of {player}: 3 enhancements unlocked (server side, no cost), chance 100%");
        c.Reply($"{slots.Count} Glacier(s) armed; put one in hand and hit an enemy");
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    private static uint Fnv(string s)
    {
        var h = 0x811c9dc5u;
        foreach (var c in System.Text.Encoding.UTF8.GetBytes(s)) h = (h ^ c) * 0x1000193u;
        return h;
    }

    private static string Cfg(uint id) => $"0x{id:x8}";

    /// <summary>Buff entities of a type now (BuffNetworkData: lifetime, end, buffTypeId, level).</summary>
    private static HashSet<uint> Buffs(uint type)
    {
        var r = new HashSet<uint>();
        foreach (var x in World.Find("BuffNetworkData"))
            if (World.ReadBytes(x, "BuffNetworkData") is { Length: >= 24 } b && BitConverter.ToUInt32(b, 0x10) == type) r.Add(x.Value);
        return r;
    }

    private async Task<int> Hit(EntityId attacker, EntityId target, Guid weapon, uint buff)
    {
        var before = Buffs(buff);
        var at = World.Position(target)!.Value;
        var h0 = Combat.GetHealth(target);
        var he = await Abilities.RunProgram(HitProgram, at, new ImpactOptions
        {
            Root = attacker,
            Configs = new ImpactConfigs
            {
                [Cfg(Fnv("perkhit.Target"))] = target, [Cfg(Fnv("perkhit.Weapon"))] = weapon,
                [Cfg(Fnv("perkhit.At"))] = new[] { (float)at.X, (float)at.Y, (float)at.Z }.SelectMany(BitConverter.GetBytes).ToArray(),
            },
            Tag = "perkhit",
        });
        await Steps(4);
        await Steps(8);
        Spawn.Destroy(he);
        Info($"hit: enemy health {h0?.Current} -> {Combat.GetHealth(target)?.Current}{(_landed ? "" : " (the hit did not land)")}");
        _landed = false;
        return Buffs(buff).Except(before).Count();
    }

    private async Task<EntityId> Perk(Guid program, EntityId wearer, Guid buff, double chance)
    {
        var e = await Abilities.RunProgram(program, World.Position(wearer)!.Value, new ImpactOptions
        {
            Root = wearer,
            Configs = new ImpactConfigs { [Cfg(CfgBuff)] = buff, [Cfg(CfgChance)] = chance },
            Tag = "perkhit",
        });
        await Steps(4);
        return e;
    }

    private async Task Run()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(60);
            await Suite();
        }
        catch (Exception e)
        {
            Check(false, $"exception: {e}");
        }
        foreach (var e in _spawned) Spawn.Destroy(e);
        Log.Info($"perkhit-test done: {_pass} PASS, {_fail} FAIL");
    }

    private WorldPosition _at;

    private async Task<EntityId> Enemy()
    {
        var e = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(_at.X + 3, _at.Y, _at.Z), 0, new SpawnOptions { Tag = "perkhit", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        await Steps(10);
        return e;
    }

    /// <summary>A fresh enemy each time: a buff that is already on the target renews, it makes no new buff entity.</summary>
    private async Task<int> FreshHit(EntityId wearer, Guid weapon, uint buff)
    {
        var enemy = await Enemy();
        var n = await Hit(wearer, enemy, weapon, buff);
        Spawn.Destroy(enemy);
        await Steps(2);
        return n;
    }

    private async Task Suite()
    {
        var glacierData = Content.ItemData.Get("Kiln_Sample_Glacier");
        var daggerData = Content.ItemData.Get("Weapon_T1_2H_Daggers_02_Parry");
        var perk = Content.Perks.Get("Kiln_Sample_FrostbiteEdge");
        if (glacierData is null || daggerData is null || perk is null)
        {
            Check(false, $"needs NewItems: Glacier {glacierData is not null}, dagger {daggerData is not null}, perk {perk is not null}");
            return;
        }
        var sword = glacierData.Get("weaponCategoryReference").Guid;
        var dagger = daggerData.Get("weaponCategoryReference").Guid;
        var edgeProgram = perk.Get("impact").Guid;
        var edgeBuff = perk.Get("impactValues.BuffType").Guid;
        Info($"weapon categories: Glacier {sword}, a dagger {dagger}; Frostbite Edge runs {edgeProgram}, buff {edgeBuff}, chance {perk.Get("impactValues.ChanceValue")}");
        Check(sword != Guid.Empty && dagger != Guid.Empty && sword != dagger, "Glacier and a dagger have different weapon categories");

        WorldPosition? anchor = null;
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf)
            {
                anchor = inf.Position;
                break;
            }
        }
        if (anchor is not { } a)
        {
            Check(false, "no anchor entity in the world");
            return;
        }
        _at = new WorldPosition(a.X + 40, a.Y + 0.5, a.Z + 40);
        var wearer = await Spawn.Entity("1_Player", _at, 0, new SpawnOptions { Tag = "perkhit", Flags = SpawnFlags.Transient });
        _spawned.Add(wearer);
        await Steps(20);

        // the control: the vanilla program, its own buff, chance 1
        var p = await Perk(VanillaProgram, wearer, PoisonGuid, 1.0);
        var n = await FreshHit(wearer, dagger, PoisonId);
        Check(n >= 1, $"control: the vanilla Poisoned perk's program applies Poisoned on a dagger hit ({n} new buff(s)): the hit reaches the program's OnHit stream");
        n = await FreshHit(wearer, sword, PoisonId);
        Check(n == 0, $"control: the same program does nothing on a sword hit ({n}): its weapon check lets only daggers through, so a clone of it never fired on Glacier");
        Spawn.Destroy(p);
        await Steps(4);

        // the program reads the instance's configs: a clone's values are used
        p = await Perk(VanillaProgram, wearer, FrostbiteGuid, 1.0);
        n = await FreshHit(wearer, dagger, FrostbiteId);
        Check(n >= 1, $"the program reads its BuffType config: Frostbite (the buff clone) on a dagger hit ({n})");
        Spawn.Destroy(p);
        await Steps(4);
        p = await Perk(VanillaProgram, wearer, FrostbiteGuid, 0.0);
        n = await FreshHit(wearer, dagger, FrostbiteId);
        Check(n == 0, $"the program reads its ChanceValue config: chance 0, no buff ({n})");
        Spawn.Destroy(p);
        await Steps(4);

        // Frostbite Edge's own program, with the perk record's buff, chance raised for the test
        Check(edgeProgram != VanillaProgram && edgeProgram != Guid.Empty, $"the perk clone Frostbite Edge runs its own program ({edgeProgram})");
        p = await Perk(edgeProgram, wearer, edgeBuff, 1.0);
        n = await FreshHit(wearer, sword, FrostbiteId);
        Check(n >= 1, $"Frostbite Edge applies Frostbite on a hit with Glacier's weapon category ({n})");
        Spawn.Destroy(p);
        await Steps(4);
        p = await Perk(edgeProgram, wearer, edgeBuff, 0.0);
        n = await FreshHit(wearer, sword, FrostbiteId);
        Check(n == 0, $"its chance config is read: chance 0, no buff ({n})");
        Spawn.Destroy(p);
        await Steps(4);

        await Equipment(wearer, sword, edgeBuff);
    }

    private const int HeldSlot = 0;

    private static (uint Item, uint Pide, uint[] Perks, uint[] Ids) Held(EntityId wearer)
    {
        var eq = World.ReadBytes(wearer, "Equipment");
        if (eq is null) return default;
        var o = 8 + HeldSlot * 0x48;
        return (BitConverter.ToUInt32(eq, o + 8), BitConverter.ToUInt32(eq, o + 0xc),
            Enumerable.Range(0, 5).Select(k => BitConverter.ToUInt32(eq, o + 0x18 + k * 4)).ToArray(),
            Enumerable.Range(0, 5).Select(k => BitConverter.ToUInt32(eq, o + 0x2c + k * 4)).ToArray());
    }

    private static string Show((uint Item, uint Pide, uint[] Perks, uint[] Ids) h) =>
        $"item 0x{h.Item:x8}, perk entities [{string.Join(",", h.Perks ?? [])}], perk ids [{string.Join(",", (h.Ids ?? []).Select(i => i.ToString("x")))}]";

    // The way a real player's perk starts: the game's equipment_impacts spawns a perk's program for the item in an equipment
    // slot, per enhancement that is unlocked (Equipment.equipmentImpacts[slot].perkEntities).
    private async Task Equipment(EntityId wearer, Guid sword, Guid buff)
    {
        var sel = World.ReadBytes(wearer, "SlotSelection");
        if (sel is null || World.ComponentSize(wearer, "Equipment") == 0)
        {
            Check(false, "the player-template entity has no SlotSelection or Equipment");
            return;
        }
        var bar = new EntityId(BitConverter.ToUInt32(sel, 0xa0));
        var give = await Items.Give(wearer, "Kiln_Sample_Glacier", 1, new Gear(null, ItemRarity.Epic));
        Info($"give Epic Glacier: {give}");
        await Steps(20);
        var inv = Items.Read(wearer);
        var slot = inv?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == 0x723a87fa);
        if (slot is not { } sl || sl.ItemEntity.IsNone)
        {
            Check(false, $"Glacier not found in the inventory: {inv?.Slots.Count(x => !x.IsEmpty)} slots used");
            return;
        }
        var c = World.ReadBytes(sl.ItemEntity, "PerkContainerNew");
        Info($"Glacier's slot {sl.Slot}, link {sl.Link}, hotbar entity {bar}; PerkContainerNew available {c?[0]}, unlocked {c?[1]}");
        Check(c is [4, 0], "a new Epic Glacier has 4 enhancements, none unlocked (a perk's program runs for the unlocked ones only)");
        // put it in hand: the hotbar slot it landed in
        var index = (uint)sl.Slot.Index;
        var hand = GameTree.GameRequest(wearer, 5, index);
        await Steps(10);
        var held = Held(wearer);
        Info($"selected hotbar slot {index} ({hand}): {Show(held)}");
        var heldOk = held.Item == 0x723a87fa;
        Check(heldOk, "the game's equipment slot follows the selected hotbar slot: Glacier is in hand");
        if (!heldOk) return;
        Check(held.Perks.All(x => x == 0), "no enhancement unlocked: the game runs none of its perks");
        var locked = await FreshHit(wearer, sword, FrostbiteId);
        Check(locked == 0, $"and a hit applies no Frostbite ({locked}): that was the item in the client test too");

        // unlock as far as Frostbite Edge (index 2); chance 1 for the test
        var perkRec = Content.Perks.Get("Kiln_Sample_FrostbiteEdge")!;
        perkRec.Set("impactValues.ChanceValue", 1.0);
        GameTree.GameRequest(sl.ItemEntity, 4, 3);
        await Steps(10);
        held = Held(wearer);
        Info($"3 enhancements unlocked: {Show(held)}");
        var edgeId = 0x4b76be40u;
        var perkAt = Array.IndexOf(held.Ids, edgeId);
        Check(perkAt == 2 && held.Perks[2] != 0, $"the game started Frostbite Edge's program for the equipped item: perk entity {(perkAt >= 0 ? held.Perks[perkAt] : 0)}");
        var first = perkAt >= 0 ? held.Perks[perkAt] : 0;
        var hit = await FreshHit(wearer, sword, FrostbiteId);
        Check(hit >= 1, $"a hit by the wearer applies Frostbite, through the game's own perk instance ({hit})");

        // the hotbar: another slot in hand, then back
        var other = (uint)(index == 0 ? 1 : 0);
        GameTree.GameRequest(wearer, 5, other);
        await Steps(10);
        var away = Held(wearer);
        Info($"hotbar slot {other}: {Show(away)}");
        Check(away.Perks.All(x => x == 0), "another hotbar slot: the perk's program is gone");
        var off = await FreshHit(wearer, sword, FrostbiteId);
        Check(off == 0, $"and a hit applies nothing ({off})");
        GameTree.GameRequest(wearer, 5, index);
        await Steps(10);
        var back = Held(wearer);
        Info($"back to hotbar slot {index}: {Show(back)}");
        Check(back.Perks[2] != 0 && back.Perks[2] != first, $"back on Glacier the game starts it again: perk entity {first} -> {back.Perks[2]} (switching the hotbar re-applies the perk)");
        hit = await FreshHit(wearer, sword, FrostbiteId);
        Check(hit >= 1, $"and hits apply Frostbite again ({hit})");
        perkRec.Reset("impactValues.ChanceValue");
    }
}
