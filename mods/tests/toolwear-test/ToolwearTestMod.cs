// SPDX-License-Identifier: MIT
// toolwear-test: kiln.toolwear in a lab, on real debits of a real pickaxe (chapter 16b §6; plan S4). A spawned player
// actor holds the pickaxe; the game debits it
//   - for HitEvents the declared program "hits" sends (kiln.programs.json; rootSourceId +0x90 = the actor, weaponPideId
//     +0xac = the pickaxe's item entity, hitCounter +0xd0 = 0, or 65535 to go over the cap; targetId +0x94 and
//     armorPideId +0xb0 for the armor case), and
//   - after a projectile spawn: a clone of a staff attack with isProjectile and a real projectile template, used through
//     the lab use-item helper, with the item entity written into the pending ActorInput (the helper writes slot and item
//     id only).
// Every case asserts the exact total twice: from the durability read before and after, and from the change events.
// Lines: "toolwear-test: PASS <case> ..." / "FAIL <case> ...", then "toolwear-test: FINISHED passes=N failures=M".
using System.Text.Json.Nodes;
using Kiln;
using Kiln.Testing;

namespace ToolwearTest;

public sealed class ToolwearTestMod : Mod
{
    private const string Capability = "items.tool_wear_debit";
    private const string Projectile = "5e4449a3-10a6-434c-9cc1-2dea5ff43bba";  // a vanilla projectile template (the chapter's fixture)
    private static readonly Guid Sequence = new("44d58df8-c58c-5ff7-a2fb-06ef4b2d9171");

    // the program's inputs (written on the tick thread, read on whichever thread runs the program)
    private static int _pending;
    private static uint _actor, _weapon, _target, _armor, _counter;
    private static int _emitted, _refused;

    private IDisposable? _program;
    private readonly List<ToolDurabilityChangedEvent> _changes = new();
    private readonly List<ToolWearRuleChangedEvent> _rules = new();
    private readonly List<string> _b = new();
    private int _passes, _failures;

    private void Check(bool ok, string name, string detail)
    {
        if (ok) _passes++;
        else _failures++;
        Log.Info($"toolwear-test: {(ok ? "PASS" : "FAIL")} {name} {detail}");
    }

    public override void OnLoad()
    {
        try
        {
            _program = EcsPrograms.Bind("hits", Emit);
        }
        catch (Exception e)
        {
            Check(false, "bind", e.Message);
        }
        Events.On<ToolDurabilityChangedEvent>(e => _changes.Add(e));
        Events.On<ToolWearRuleChangedEvent>(e => _rules.Add(e));
        Context.Messages.On("b-done", m => _b.Add(m.Payload as string ?? ""));
        if (Mods.Side == ModSide.Client)
        {
            // a hosted game: the lab cases spawn actors and wear tools, which is for a dedicated lab; here the mod is the
            // fixture guided check 16b-g needs (mods/tests/client-pass/Dev28ToolDurability.cs)
            Log.Info("toolwear-test: on a game client the lab cases do not run; this mod is the fixture of check 16b-g");
            return;
        }
        _ = Run();
    }

    // The program "hits": sends what the tick asked for. A HitEvent is 248 bytes (its reflected size; EmitEvent checks it).
    private static void Emit(EcsProgramContext context)
    {
        var n = Interlocked.Exchange(ref _pending, 0);
        for (var i = 0; i < n; i++)
        {
            var e = new byte[248];
            BitConverter.TryWriteBytes(e.AsSpan(0x90), Volatile.Read(ref _actor));
            BitConverter.TryWriteBytes(e.AsSpan(0xac), Volatile.Read(ref _weapon));
            BitConverter.TryWriteBytes(e.AsSpan(0xd0), (ushort)Volatile.Read(ref _counter));
            var target = Volatile.Read(ref _target);
            if (target != 0)
            {
                BitConverter.TryWriteBytes(e.AsSpan(0x94), target);
                BitConverter.TryWriteBytes(e.AsSpan(0xb0), Volatile.Read(ref _armor));
            }
            if (context.EmitEvent("HitEvent", e)) Interlocked.Increment(ref _emitted);
            else Interlocked.Increment(ref _refused);
        }
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    // The sum of an item entity's Durability values (the attribute container: values at the offset in its u16 at +4,
    // count in its byte at +7). Only the current value moves under a debit, so a difference of sums is the debit.
    private static long Durability(EntityId item)
    {
        var b = World.ReadBytes(item, "Durability");
        if (b is null || b.Length < 8) return long.MinValue;
        int off = BitConverter.ToUInt16(b, 4), count = b[7];
        long sum = 0;
        for (var i = 0; i < count && off + 4 * i + 4 <= b.Length; i++) sum += BitConverter.ToInt32(b, off + 4 * i);
        return sum;
    }

    private async Task<long> Hits(uint counter, int repeats, uint target = 0, uint armor = 0, int wait = 20)
    {
        Volatile.Write(ref _counter, counter);
        Volatile.Write(ref _target, target);
        Volatile.Write(ref _armor, armor);
        var sent = Volatile.Read(ref _emitted);
        Interlocked.Exchange(ref _pending, repeats);
        for (var i = 0; i < 60 && Volatile.Read(ref _emitted) < sent + repeats; i++) await Steps(1);
        await Steps(wait);
        Volatile.Write(ref _target, 0u);
        return Volatile.Read(ref _emitted) - sent;
    }

    private async Task Run()
    {
        EntityId actor = default, target = default;
        try
        {
            for (var i = 0; i < 3000; i++)
            {
                if (Items.Status() is { Transactions: true, Catalog: true } && Spawn.Available && Content.Ready) break;
                await Steps(1);
            }
            await Steps(60);
            if (!Capabilities.Is(Capability))
            {
                Check(false, "capability", Capabilities.Why(Capability));
                return;
            }
            var all = Items.All;
            ItemInfo Pick(string what, Func<ItemInfo, bool> match) => all.FirstOrDefault(match) ?? throw new InvalidOperationException(
                $"no {what} among {all.Count} items (gear by category: {string.Join(", ", all.Where(x => x.IsGear).GroupBy(x => x.Category).Select(g => $"{g.Key} {g.Count()}"))})");
            bool Named(ItemInfo x, string word) => x.Label.Contains(word, StringComparison.OrdinalIgnoreCase) || x.Name.Contains(word, StringComparison.OrdinalIgnoreCase);
            Log.Info($"toolwear-test: tools: {string.Join("; ", all.Where(x => x.IsGear && x.Category == ItemCategory.Tools).Select(x => $"{x.Name}={x.Label} 0x{x.Flags:x}"))}");
            // the game files its pickaxes under build tools, with an item entity (ItemFlag HasPIDE) and durability
            var pickaxe = Pick("pickaxe", x => x.IsGear && (x.Flags & 0x2) != 0 && Named(x, "Pickaxe"));
            var axe = Pick("axe", x => x.IsGear && x.Category == ItemCategory.Tools && (x.Flags & 0x2) != 0 && Named(x, "Axe"));
            Log.Info($"toolwear-test: torches: {string.Join("; ", all.Where(x => Named(x, "Torch")).Take(12).Select(x => $"{x.Name}={x.Label} {x.Category} gear={x.IsGear} 0x{x.Flags:x}"))}");
            foreach (var x in all.Where(x => Named(x, "Pickaxe")).Take(6))
            {
                var st0 = Items.ToolWearStatus(x.Id);
                Log.Info($"toolwear-test: pickaxe candidate {x.Name}={x.Label} {x.Category} gear={x.IsGear} 0x{x.Flags:x}: ok={st0.Ok} reason={st0.Reason}");
            }
            var torch = all.FirstOrDefault(x => x.IsGear && Named(x, "Torch")) ?? Pick("torch", x => Named(x, "Torch"));
            var armors = all.Where(x => x.IsGear && x.Category == ItemCategory.Equipment && x.Name.StartsWith("Armor_", StringComparison.Ordinal) &&
                                        !x.Name.Contains("NPC", StringComparison.OrdinalIgnoreCase)).Take(8).ToList();
            var armorItem = armors.FirstOrDefault() ?? Pick("armor", x => x.IsGear && x.Category == ItemCategory.Equipment);
            actor = await Spawn.Entity("1_Player", new WorldPosition(3845, 835, 1480), 0, new SpawnOptions { Flags = SpawnFlags.Transient, Tag = "toolwear-test" });
            target = await Spawn.Entity("1_Player", new WorldPosition(3849, 835, 1480), 0, new SpawnOptions { Flags = SpawnFlags.Transient, Tag = "toolwear-test" });
            await Steps(60);
            World.TryWrite(actor, "PlayerInput", 0x5b8, (byte)1);
            PlayerStates.Diagnostics.Watch(actor);
            var givePick = await Items.Give(actor, pickaxe.Id, 1);
            var giveArmor = await Items.Give(target, armorItem.Id, 1);
            var giveAxe = await Items.Give(target, axe.Id, 1);
            await Steps(15);
            var slot = Items.Read(actor)?.Slots.FirstOrDefault(x => x.Item == pickaxe.Id)
                       ?? throw new InvalidOperationException($"the actor holds no {pickaxe}: give={givePick}");
            var pide = slot.ItemEntity;
            var armor = Items.Read(target)?.Slots.FirstOrDefault(x => x.Item == armorItem.Id)?.ItemEntity ?? EntityId.None;
            // an armor item entity with durability of its own (some armor has none)
            foreach (var other in armors.Skip(1))
            {
                if (!armor.IsNone && Durability(armor) != long.MinValue) break;
                armorItem = other;
                giveArmor = await Items.Give(target, other.Id, 1);
                await Steps(10);
                armor = Items.Read(target)?.Slots.FirstOrDefault(x => x.Item == other.Id)?.ItemEntity ?? EntityId.None;
            }
            if (armor.IsNone || Durability(armor) == long.MinValue) Log.Info($"toolwear-test: the target holds no armor with durability ({armorItem}): give={giveArmor}");
            var standIn = Items.Read(target)?.Slots.FirstOrDefault(x => x.Item == axe.Id)?.ItemEntity ?? EntityId.None;
            Volatile.Write(ref _actor, actor.Value);
            Volatile.Write(ref _weapon, pide.Value);
            Log.Info($"toolwear-test: setup actor={actor} pickaxe={pickaxe} pide={pide} durability={Durability(pide)} target={target} armor={armorItem} armorPide={armor}");

            // ---- refused: a torch burns down by another path ----
            var t = Items.SetToolWearPerDebit(torch.Id, 7);
            Check(!t.Ok && t.Reason.StartsWith("UnsupportedTool", StringComparison.Ordinal) && t.Rule is null, "toolwear-torch-refused", $"item={torch} reason={t.Reason}");

            // ---- the rule ----
            var set = Items.SetToolWearPerDebit(pickaxe.Id, 7);
            var st = Items.ToolWearStatus(pickaxe.Id);
            Check(set.Ok && st.PerDebit == 7 && st.OwnerModId == "toolwear-test", "toolwear-rule-set", $"ok={set.Ok} reason={set.Reason} perDebit={st.PerDebit} owner={st.OwnerModId}");

            async Task HitCase(string name, uint counter, int repeats, int spent)
            {
                _changes.Clear();
                var before = Durability(pide);
                var counters = Items.LabToolWearCounters();
                var emitted = await Hits(counter, repeats);
                var after = Durability(pide);
                var mine = _changes.Where(c => c.Item.Entity.Id == pide).ToList();
                var now = Items.LabToolWearCounters();
                var exact = mine.Count == (spent == 0 ? 0 : repeats) && mine.All(c => c.Source == ToolWearSource.WeaponHit && c.RequestedDelta == -spent / Math.Max(1, repeats) &&
                    c.OriginalDelta == -1 && c.Actor == actor && c.Item.Item == pickaxe.Id) && mine.Sum(c => c.Before - c.After) == spent;
                Check(emitted == repeats && before - after == spent && exact, name,
                    $"emitted={emitted} before={before} after={after} spent={before - after} events={mine.Count} eventSpent={mine.Sum(c => c.Before - c.After)} " +
                    $"hitSeen+={now.HitSeen - counters.HitSeen} hitApplied+={now.HitApplied - counters.HitApplied}");
            }

            await HitCase("toolwear-hit-1x7", 0, 1, 7);
            await HitCase("toolwear-hit-2x14", 0, 2, 14);
            await HitCase("toolwear-hit-overcap-0", 65535, 1, 0);

            // ---- the armor site: the game's own debit, a rule never reaches it. The armor of this game's armor items has no
            // durability of its own, so an axe with a rule of its own (cost 7) stands in as the hit's armor: the armor site
            // debits it, and it must lose the game's 1, not 7 ----
            if (standIn.IsNone || Durability(standIn) == long.MinValue) Check(false, "toolwear-armor-untouched", $"no stand-in with durability (give: {giveAxe}; armor give: {giveArmor})");
            else
            {
                var axeRule = Items.SetToolWearPerDebit(axe.Id, 7);
                _changes.Clear();
                long wb = Durability(pide), ab = Durability(standIn);
                var emitted = await Hits(0, 1, target.Value, standIn.Value);
                long wa = Durability(pide), aa = Durability(standIn);
                Check(axeRule.Ok && emitted == 1 && wb - wa == 7 && ab - aa == 1 && _changes.All(c => c.Item.Entity.Id != standIn), "toolwear-armor-untouched",
                    $"weapon {wb}->{wa} (spent {wb - wa}), armor-site stand-in {axe} rule 7: {ab}->{aa} (spent {ab - aa}), its events={_changes.Count(c => c.Item.Entity.Id == standIn)} " +
                    $"(armor items have no durability here: {armorItem} {(armor.IsNone ? "none" : Durability(armor) == long.MinValue ? "no Durability" : "has Durability")})");
            }

            // ---- the projectile spawn's debit ----
            var labOps = Path.GetFullPath(Path.Combine(Context.DataDirectory, "..", "skills", "lab-ops"));
            Directory.CreateDirectory(Path.GetDirectoryName(labOps)!);
            if (!File.Exists(labOps)) File.WriteAllText(labOps, "toolwear-test");
            Content.Sequences.Define(new JsonObject
            {
                ["guid"] = Sequence.ToString(), ["name"] = "toolwear-test.projectile", ["base"] = "Attack_Player_Staff_FixedAnim_Fireball_fast",
                ["fields"] = new JsonObject
                {
                    ["events[SpawnEntityEvent#0].spawnTransform"] = "Spawner", ["events[SpawnEntityEvent#0].isProjectile"] = true,
                    ["events[SpawnEntityEvent#0].addWeaponReference"] = false, ["events[SpawnEntityEvent#0].consume"] = false,
                    ["events[SpawnEntityEvent#0].time"] = 0.04, ["events[SpawnEntityEvent#0].templateReference"] = Projectile,
                    ["events[LoopEvent#0].duration"] = 0.0, ["events[LoopEvent#1].duration"] = 0.0,
                },
            });
            for (var i = 0; i < 300 && Content.Sequences.Get(Sequence) is null; i++) await Steps(1);
            await Steps(30);
            Log.Info($"toolwear-test: projectile sequence ready={Content.Sequences.Get(Sequence) is not null} select={GameTree.GameRequest(actor, 5, (uint)slot.Slot.Index)}");
            await Steps(24);

            async Task SpawnCase(string name, int cost)
            {
                var r = Items.SetToolWearPerDebit(pickaxe.Id, cost);
                _changes.Clear();
                var before = Durability(pide);
                var counters = Items.LabToolWearCounters();
                var started = PlayerStates.Diagnostics.LabUseItem(actor, Abilities.Programs.IdOf(Sequence), PlayerStateId.ActionPrimary, slot.Slot.Value, pickaxe.Id.Value);
                var pideSet = World.WriteRaw(actor, "ActorInput", 0x44, pide.Value);
                for (var i = 0; i < 100 && Items.LabToolWearCounters().SpawnApplied == counters.SpawnApplied; i++) await Steps(1);
                await Steps(30);
                var after = Durability(pide);
                var now = Items.LabToolWearCounters();
                var mine = _changes.Where(c => c.Item.Entity.Id == pide).ToList();
                var exact = cost == 0
                    ? mine.Count == 0
                    : mine.Count == 1 && mine[0].Source == ToolWearSource.ProjectileSpawn && mine[0].RequestedDelta == -cost && mine[0].Before - mine[0].After == cost;
                Check(r.Ok && started && pideSet && now.SpawnApplied == counters.SpawnApplied + 1 && before - after == cost && exact, name,
                    $"started={started} pideSet={pideSet} before={before} after={after} spent={before - after} events={mine.Count} " +
                    $"spawnSeen+={now.SpawnSeen - counters.SpawnSeen} spawnApplied+={now.SpawnApplied - counters.SpawnApplied}");
            }

            await SpawnCase("toolwear-projectile-7", 7);
            await SpawnCase("toolwear-projectile-0", 0);
            Items.SetToolWearPerDebit(pickaxe.Id, 7);

            // ---- reveal: another mod's rule on top, then taken away ----
            {
                await Steps(5);
                _rules.Clear();
                _b.Clear();
                Mods.Send("toolwear-test-b", "set", (pickaxe.Id.Value, 3));
                for (var i = 0; i < 40 && _b.Count == 0; i++) await Steps(1);
                var top = Items.ToolWearStatus(pickaxe.Id);
                _changes.Clear();
                var before = Durability(pide);
                await Hits(0, 1);
                var spentUnderB = before - Durability(pide);
                Mods.Send("toolwear-test-b", "drop");
                for (var i = 0; i < 40 && _b.Count < 2; i++) await Steps(1);
                var back = Items.ToolWearStatus(pickaxe.Id);
                var events = _rules.Where(r => r.Item == pickaxe.Id).ToList();
                var again = Items.SetToolWearPerDebit(pickaxe.Id, 7);  // identical: no event
                await Steps(3);
                var replayEvents = _rules.Count(r => r.Item == pickaxe.Id) - events.Count;
                before = Durability(pide);
                await Hits(0, 1);
                var spentAfter = before - Durability(pide);
                Check(_b.Count == 2 && _b[0] == "set" && top.PerDebit == 3 && top.OwnerModId == "toolwear-test-b" && spentUnderB == 3 &&
                      back.PerDebit == 7 && back.OwnerModId == "toolwear-test" && events.Count == 2 && events[0].After == 3 && events[0].BeforeModId == "toolwear-test" &&
                      events[1].Before == 3 && events[1].After == 7 && again.Ok && replayEvents == 0 && spentAfter == 7, "toolwear-rule-reveal",
                    $"b={string.Join("/", _b)} top={top.PerDebit}@{top.OwnerModId} spentUnderB={spentUnderB} back={back.PerDebit}@{back.OwnerModId} " +
                    $"ruleEvents={string.Join(",", events.Select(r => $"{r.Before}->{r.After}"))} replayEvents={replayEvents} spentAfter={spentAfter}");
            }

            // ---- forced parallel mode: the debits stay exact; the hooks' threads as the programs' flags make them ----
            {
                var forced = LabSystems.ForceParallel(600);
                await Steps(3);
                var mask = LabSystems.PlayersMask;
                var counters = Items.LabToolWearCounters();
                _changes.Clear();
                var before = Durability(pide);
                await Hits(0, 1);
                var hitSpent = before - Durability(pide);
                before = Durability(pide);
                var started = PlayerStates.Diagnostics.LabUseItem(actor, Abilities.Programs.IdOf(Sequence), PlayerStateId.ActionPrimary, slot.Slot.Value, pickaxe.Id.Value);
                World.WriteRaw(actor, "ActorInput", 0x44, pide.Value);
                for (var i = 0; i < 100 && Items.LabToolWearCounters().SpawnApplied == counters.SpawnApplied; i++) await Steps(1);
                await Steps(30);
                var spawnSpent = before - Durability(pide);
                var now = Items.LabToolWearCounters();
                var stillOn = Capabilities.Is(Capability) && Items.ToolWearStatus(pickaxe.Id).Ok;
                var forcedNow = LabSystems.Forced;
                LabSystems.StopParallel();
                Check(forced && forcedNow && mask is { } m && (m & 0x8000) != 0 && started && hitSpent == 7 && spawnSpent == 7 && stillOn, "toolwear-workers-pinned",
                    $"forced={forced} mask=0x{mask ?? 0:x4} hitSpent={hitSpent} spawnSpent={spawnSpent} hookThreads={now.HookThreads} (before the parallel run {counters.HookThreads}) " +
                    $"capabilityOn={stillOn} events={_changes.Count(c => c.Item.Entity.Id == pide)}");
            }

            // ---- the game's clamp: a cost larger than what is left ends at 0 ----
            {
                Items.SetToolWearPerDebit(pickaxe.Id, 32767);
                _changes.Clear();
                var before = Durability(pide);
                await Hits(0, 1);
                var mine = _changes.Where(c => c.Item.Entity.Id == pide).ToList();
                var cur = mine.Count == 1 ? mine[0].After : -1;
                Check(mine.Count == 1 && mine[0].After == 0 && mine[0].RequestedDelta == -32767 && mine[0].Before > 0, "toolwear-break-clamp",
                    $"durabilitySum {before}->{Durability(pide)} event before={(mine.Count == 1 ? mine[0].Before : -1)} after={cur} requested={(mine.Count == 1 ? mine[0].RequestedDelta : 0)}");
            }
            Log.Info($"toolwear-test: program sent={Volatile.Read(ref _emitted)} refused={Volatile.Read(ref _refused)} counters={Items.LabToolWearCounters()}");
        }
        catch (Exception e)
        {
            Check(false, "fixture", e.ToString());
        }
        finally
        {
            if (!actor.IsNone)
            {
                PlayerStates.Diagnostics.Watch(actor, false);
                Spawn.Destroy(actor);
            }
            if (!target.IsNone) Spawn.Destroy(target);
            Log.Info($"toolwear-test: FINISHED passes={_passes} failures={_failures}");
        }
    }

    public override void OnUnload() => _program?.Dispose();
}
