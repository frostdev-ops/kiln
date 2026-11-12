// SPDX-License-Identifier: MIT
// Checks 1-3 of task t1 (entity refs): EntityRef.Of in OnLoad and in the first ticks; refs of spawned entities and of
// item entities across destroy and new creation (ids, serials, the same step); one item's refs from a read, a give and a
// move agree, a plain stack has none.
using Kiln;

namespace InstancesTest;

public sealed partial class InstancesTestMod
{
    private bool _firstTickSeen, _firstRunningTickSeen;

    // ---- check 1: OnLoad throws identity.entities; the first tick callback works ----
    [LabOnLoad("identity.onload")]
    private void IdentityOnLoad()
    {
        // A hot reload runs OnLoad while the world runs and the sources are long registered: there the contract is the
        // other way round, EntityRef.Of answers in OnLoad.
        bool reloaded;
        try
        {
            reloaded = Spawn.Available && Spawn.WorldRunning;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException)
        {
            reloaded = false;
        }
        if (reloaded)
        {
            try
            {
                var r = EntityRef.Of(new EntityId(1024));
                Check(true, $"check 1 (hot reload): the world runs, so EntityRef.Of answers in OnLoad ({r})");
            }
            catch (KilnUnavailableException e)
            {
                Check(false, $"check 1 (hot reload): EntityRef.Of throws {e.Capability} in OnLoad although the world runs ({e.Reason})");
            }
            Context.Tick.Pre += FirstTicks;
            return;
        }
        try
        {
            var r = EntityRef.Of(new EntityId(1024));
            Check(false, $"check 1: EntityRef.Of in OnLoad did not throw (gave {r}); the entity sources were already registered");
        }
        catch (KilnUnavailableException e)
        {
            Check(e.Capability == "identity.entities", $"check 1: EntityRef.Of throws {e.Capability} in OnLoad ({e.Reason})");
        }
        Context.Tick.Pre += FirstTicks;
    }

    private void FirstTicks(in TickInfo t)
    {
        var running = Spawn.WorldRunning;
        string what;
        bool ok;
        try
        {
            var r = EntityRef.Of(new EntityId(1024));
            what = $"gave {r}";
            ok = true;
        }
        catch (KilnUnavailableException e)
        {
            what = $"threw {e.Capability}: {e.Reason}";
            ok = false;
        }
        if (!_firstTickSeen)
        {
            _firstTickSeen = true;
            Info($"check 1: first tick callback (step {t.Step}, world running {running}): EntityRef.Of {what}");
        }
        if (running && !_firstRunningTickSeen)
        {
            _firstRunningTickSeen = true;
            Check(ok, $"check 1: EntityRef.Of works in the first tick callback with the world running (step {t.Step}): {what}");
            Context.Tick.Pre -= FirstTicks;
        }
    }

    // ---- check 2: spawned and item entities, destroy, new ones; ids never reused, old refs dead ----
    [LabCheck(20, "identity.reuse")]
    private async Task IdentityReuse()
    {
        var containers = await QuietContainers(4, 2);
        Check(containers.Count > 0, $"check 2: a quiet container with 4 free slots ({containers.Count} found)");
        if (containers.Count == 0) return;
        var chest = containers[0];
        var at = Spawn.Inspect(chest)?.Position ?? new WorldPosition(0, 0, 0);
        var spot = new WorldPosition(at.X + 3, at.Y + 30, at.Z + 3);
        var seen = new HashSet<uint>();
        var reused = 0;

        // spawned entities: destroy and spawn again in the same tick callback, tracked and untracked
        var tmpl = Spawn.Lookup("Enemy_Vukah_Melee") ?? Spawn.Templates.FirstOrDefault(x => x.Kind == TemplateKind.Creature && x.NeverSaved == true);
        Check(tmpl != null, $"check 2: a creature template to spawn: {tmpl?.Name}");
        if (tmpl == null) return;
        foreach (var flags in new[] { SpawnFlags.Transient, SpawnFlags.Transient | SpawnFlags.NoTrack })
        {
            var opts = new SpawnOptions { Flags = flags, Tag = "instances-test" };
            var cur = Spawn.Entity(tmpl.Id, spot, 0, opts).GetAwaiter().GetResult();
            var curRef = EntityRef.Of(cur);
            var curInfo = Spawn.Inspect(cur);
            seen.Add(cur.Value);
            Check(curRef.HasSerial && Place(curRef) == 3 && curRef.IsAlive,
                $"check 2: spawned {cur} ({flags}): ref {curRef} with a serial of the Spawn place, alive; tracked {curInfo?.IsTracked}, " +
                $"record serial {curInfo?.Serial}, SpawnTime {curInfo?.SpawnTime}");
            if (curInfo is { IsTracked: true, SpawnTime: not null })
                Check(((curRef.Serial >> 59) & 1) == 0 && (curRef.Serial & ((1UL << 59) - 1)) == ((ulong)curInfo.SpawnTime.Value & ((1UL << 59) - 1)),
                    $"check 2: a tracked entity with a SpawnTime is stamped by its SpawnTime, not its tracker serial {curInfo.Serial}");
            var stable = curRef;
            await Steps(30);
            Check(EntityRef.Of(cur) == stable, $"check 2: the stamp of {cur} does not change over 30 steps ({EntityRef.Of(cur)})");
            var sameSpawnTime = 0;
            for (var i = 0; i < 15; i++)
            {
                // one tick callback: destroy, then spawn
                Spawn.Destroy(cur);
                var next = Spawn.Entity(tmpl.Id, spot, 0, opts).GetAwaiter().GetResult();
                var nextRef = EntityRef.Of(next);
                var nextInfo = Spawn.Inspect(next);
                if (!seen.Add(next.Value)) reused++;
                if (nextInfo?.SpawnTime == curInfo?.SpawnTime) sameSpawnTime++;
                var oldAlive = curRef.IsAlive;
                var ofOld = EntityRef.Of(cur);
                Check(!oldAlive && ofOld.IsNone && nextRef.IsAlive && nextRef.HasSerial && next != cur,
                    $"check 2: destroy {cur} and spawn {next} in one callback: old ref alive {oldAlive}, Of(old id) {ofOld}, new {nextRef} alive " +
                    $"{nextRef.IsAlive}; SpawnTime old {curInfo?.SpawnTime} new {nextInfo?.SpawnTime}");
                cur = next;
                curRef = nextRef;
                curInfo = nextInfo;
                await Steps(1);
            }
            Info($"check 2: {flags}: {sameSpawnTime} of 15 respawns in the same callback had the old entity's SpawnTime; the ids were new each time");
            Spawn.Destroy(cur);
        }

        // item entities: give, take (destroyed), give again
        var sword = Sword();
        Check(sword is { IsGear: true }, $"check 2: a gear item: {sword}");
        if (sword == null) return;
        ItemInstanceRef last = default;
        for (var i = 0; i < 10; i++)
        {
            var g = await Items.Give(chest, sword.Id, 1);
            var c = g.Changes.FirstOrDefault(x => !x.ItemEntity.IsNone);
            var r = c.Ref;
            if (!seen.Add(c.ItemEntity.Value)) reused++;
            var of = c.ItemEntity.IsNone ? EntityRef.None : EntityRef.Of(c.ItemEntity);
            Check(g.Succeeded && !r.IsNone && Place(r.Entity) == 2 && of == r.Entity && r.IsAlive && r.Item == sword.Id,
                $"check 2: give {i}: item entity {c.ItemEntity}, ref {r} (Items place), EntityRef.Of gives {of}");
            if (!last.IsNone)
                Check(!last.IsAlive && EntityRef.Of(last.Entity.Id).IsNone && r.Entity.Serial != last.Entity.Serial,
                    $"check 2: the previous item {last} is not alive, Of(its id) is none, the new serial differs");
            var t = await Items.Take(chest, sword.Id, 1, c.Slot, true, c.ItemEntity);
            var tr = t.Changes.FirstOrDefault().Ref;
            Check(t.Succeeded && tr == r && !r.IsAlive, $"check 2: take {i}: the take names the same ref {tr}; now alive {r.IsAlive}");
            last = r;
        }

        // the same step: a take destroys A, a give creates B
        var ga = await Items.Give(chest, sword.Id, 1);
        var a = ga.Changes.First(x => !x.ItemEntity.IsNone);
        var takeA = Items.Take(chest, sword.Id, 1, a.Slot, true, a.ItemEntity);
        var giveB = Items.Give(chest, sword.Id, 1);
        var ta = await takeA;
        var gb = await giveB;
        var b = gb.Changes.FirstOrDefault(x => !x.ItemEntity.IsNone);
        if (!seen.Add(b.ItemEntity.Value)) reused++;
        Check(ta.Succeeded && gb.Succeeded && ta.Steps == gb.Steps && b.ItemEntity != a.ItemEntity && !a.Ref.IsAlive && b.Ref.IsAlive &&
              b.Ref.Entity.Serial != a.Ref.Entity.Serial && EntityRef.Of(a.ItemEntity).IsNone,
            $"check 2: take {a.Ref} and give in one step (both after {ta.Steps}/{gb.Steps} steps): new {b.Ref}, old alive {a.Ref.IsAlive}, new alive {b.Ref.IsAlive}");
        if (!b.ItemEntity.IsNone) await Items.Take(chest, sword.Id, 1, b.Slot, true, b.ItemEntity);
        Check(reused == 0, $"check 2: entity ids handed out again: {reused} of {seen.Count} new entities (the engine's counter only grows)");
        Info($"check 2: {Items.LabInstancesStatus}");
    }

    // ---- which entities get which serial (for docs/kiln/modding/contract.md) ----
    [LabCheck(25, "identity.census")]
    private Task IdentityCensus()
    {
        var m = System.Text.RegularExpressions.Regex.Match(Items.LabInstancesStatus, @"entity id counter (\d+)");
        var top = m.Success ? uint.Parse(m.Groups[1].Value) : 4096u;
        int absent = 0, none = 0, items = 0, spawnTime = 0, tracker = 0, players = 0, other = 0, threw = 0;
        var noSerial = new Dictionary<string, int>();
        for (var id = 1u; id <= top; id++)
        {
            try
            {
                var r = EntityRef.Of(new EntityId(id));
                if (r.IsNone) { absent++; continue; }
                if (id < 1024) players++;
                switch (Place(r))
                {
                    case 2: items++; break;
                    case 3 when ((r.Serial >> 59) & 1) == 0: spawnTime++; break;
                    case 3: tracker++; break;
                    case 0:
                        none++;
                        var kind = Spawn.Inspect(new EntityId(id))?.TemplateName is { } n ? n.Split('_')[0] : "?";
                        noSerial[kind] = noSerial.GetValueOrDefault(kind) + 1;
                        break;
                    default: other++; break;
                }
            }
            catch (KilnUnavailableException)
            {
                threw++;
            }
        }
        Info($"census of ids 1..{top}: absent {absent}; item stamps {items}; SpawnTime {spawnTime}; tracker serial {tracker}; " +
             $"no serial {none} ({string.Join(", ", noSerial.OrderByDescending(x => x.Value).Take(12).Select(x => $"{x.Key} {x.Value}"))}); " +
             $"player slots alive {players}; other {other}; threw {threw}");
        Check(threw == 0 && other == 0, $"census: every id answered by a source (threw {threw}, unknown place {other})");
        return Task.CompletedTask;
    }

    // ---- check 3: one item's ref from a read, a give and a move agree; a plain stack has none ----
    [LabCheck(30, "identity.refs-agree")]
    private async Task IdentityRefsAgree()
    {
        var containers = await QuietContainers(4, 2);
        Check(containers.Count >= 2, $"check 3: two quiet containers ({containers.Count})");
        if (containers.Count < 2) return;
        var (a, b) = (containers[0], containers[1]);
        var sword = Sword();
        var wood = Items.Lookup("Material_T1_Wood_Logs");
        if (sword == null || wood == null) return;
        var g = await Items.Give(a, sword.Id, 1);
        var gc = g.Changes.First(x => !x.ItemEntity.IsNone);
        var slotA = SlotOf(a, gc.ItemEntity);
        Check(slotA != null && slotA.Ref == gc.Ref && !gc.Ref.IsNone, $"check 3: the read's Ref equals the give's Ref: {slotA?.Ref} == {gc.Ref}");
        var locA = gc.Ref.Location();
        Check(locA is { IsPlaced: true } l && l.Holder == a && l.Slot == slotA!.Slot && l.Link == slotA.Link,
            $"check 3: Location() of the given item: {locA} (slot {slotA?.Slot}, link {slotA?.Link}, category {slotA?.Category})");
        var m = await Items.Move(a, slotA!.Slot, b);
        var mc = m.Changes.FirstOrDefault(x => x.ItemEntity == gc.ItemEntity);
        Check(m.Succeeded && mc.Ref == gc.Ref, $"check 3: the move's Ref equals it too: {mc.Ref}");
        var slotB = SlotOf(b, gc.ItemEntity);
        Check(slotB?.Ref == gc.Ref && gc.Ref.Location()?.Holder == b, $"check 3: read in the target: {slotB?.Ref}, now at {gc.Ref.Location()}");
        var gw = await Items.Give(a, wood.Id, 3);
        var wslot = Items.Read(a)?.Slots.FirstOrDefault(s => s.Item == wood.Id && s.Slot == gw.Changes.FirstOrDefault().Slot);
        Check(gw.Succeeded && gw.Changes.All(x => x.Ref.IsNone) && wslot is { Ref.IsNone: true },
            $"check 3: a plain stack has no ref (give {gw.Changes.FirstOrDefault().Ref}, read {wslot?.Ref})");
        Check(Items.Held(a).IsNone && Items.Equipped(a, EquipmentSlot.ArmourHead).IsNone,
            "check 3: Items.Held / Equipped of a container (no SlotSelection) are none");
        // restore
        if (SlotOf(b, gc.ItemEntity) is { } s) await Items.Take(b, sword.Id, 1, s.Slot, true, gc.ItemEntity);
        foreach (var c in gw.Changes) await Items.Take(a, wood.Id, c.Amount, c.Slot, true);
        Check(!gc.Ref.IsAlive && gc.Ref.Location() == null, "check 3: taken back: the ref is not alive and has no location");
    }
}
