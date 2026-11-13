// SPDX-License-Identifier: MIT
// The game's own hits through the filtered hold, on a dedicated server: a spawned player character with the game's sword
// swings at Vukahs round it, driven through its input as mods/tests/weapon-moves-test does, and the Vukahs (if their AI
// runs for a character with no client) attack it. Every hurt event a filtered handler gets is logged with the entities it
// names, so the shapes the game produces (a melee hit on a creature, a creature's attack on a player) are on record.
using Kiln;

namespace EventFilterTest;

public sealed partial class EventFilterTestMod
{
    private const int PlayerInputReady = 0x5b8;    // PlayerInput.fromClientIsReadyForGameplay (mods/tests/creative-test)
    private const int DigitalInput = 936 + 8;      // PlayerInput.fromClient.digitalInput: one bit per keen::ecs::PlayerInputType
    private const ulong Mainhand = 1UL << 0, MainhandTap = 1UL << 1;
    private const string GameSword = "Weapon_T1_1H_Sword_01";
    private readonly List<HookHandle> _detours = new();

    private static double? GroundY(WorldPosition at)
    {
        try
        {
            var box = Build.BoxAround(at with { Y = at.Y + 100 - 128 }, 0.1, 128, 0.1);
            if (!Build.TryRead(box, out var cells)) return null;
            var layer = Math.Max(1, box.SizeX * box.SizeZ);
            for (var i = cells.Length - 1; i >= 0; i--)
                if (!cells[i].IsAir) return Build.CornerOf(new CellPos(box.X, box.Y + i / layer, box.Z)).Y + (Build.Grid?.CellSize ?? 0.5);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { }
        return null;
    }

    // a player character standing on the ground, ready for gameplay (as weapon-moves-test makes one)
    private async Task<EntityId> Character(WorldPosition near, List<EntityId> made, List<EntityId> gods)
    {
        try { await Build.Load(Build.BoxAround(near, 8, 128, 8)); } catch (Exception e) when (e is InvalidOperationException or TimeoutException) { Info($"loading the terrain there: {e.Message}"); }
        var at = near with { Y = (GroundY(near) ?? near.Y) + 1.0 };
        var p = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = "event-filter-test", Flags = SpawnFlags.Transient });
        if (!p.IsNone) made.Add(p);
        if (!Check(!p.IsNone && World.Has(p, "PlayerInput"), $"a player character {p} at {at}")) return EntityId.None;
        for (var i = 0; i < 50 && Combat.GetHealth(p) is null; i++) await Steps(1);
        Combat.SetInvulnerable(p);
        gods.Add(p);
        World.WriteRaw(p, "PlayerInput", PlayerInputReady, (byte)1);
        foreach (var r in Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8"))
            _detours.Add(Hooks.Detour(r, _ => 1));  // "the terrain at the character is loaded"
        for (var i = 0; i < 240 && !PlayerStates.Is(p, PlayerStateId.IsReadyForGameplay); i++) await Steps(1);
        for (var i = 0; i < 400 && !PlayerStates.Is(p, PlayerStateId.Grounded); i++) await Steps(1);
        await Steps(10);
        Info($"the character: ready {PlayerStates.Is(p, PlayerStateId.IsReadyForGameplay)}, grounded {PlayerStates.Is(p, PlayerStateId.Grounded)}, at {World.Position(p)}");
        return p;
    }

    private async Task Press(EntityId p, ulong bits)
    {
        World.WriteRaw(p, "PlayerInput", DigitalInput, bits);
        await Steps(1);
        World.WriteRaw(p, "PlayerInput", DigitalInput, 0UL);
        await Steps(1);
    }

    /// <summary>7. The game's own hits: a player character's sword swings on watched Vukahs, and their attacks on it.
    /// However it ends, the terrain detours come out, every god-mode hold it took is released, and then what it spawned
    /// is destroyed (kiln.combat keeps a hold by entity id until it is released: a destroyed entity's id, reused, would
    /// stay invulnerable).</summary>
    private async Task GameHits(WorldPosition anchor)
    {
        var made = new List<EntityId>();
        var gods = new List<EntityId>();
        try
        {
            await GameHitsIn(anchor, made, gods);
        }
        finally
        {
            foreach (var h in _detours) try { h.Remove(); } catch (Exception e) { Info($"a detour did not come out: {e.Message}"); }
            _detours.Clear();
            foreach (var e in gods) try { Combat.SetInvulnerable(e, false); } catch (Exception x) { Info($"god mode of {e}: {x.Message}"); }
            if (gods.Count > 0)
                Check(gods.All(e => !Combat.IsInvulnerable(e)), $"every god-mode hold of this part is released before its entity is destroyed ({gods.Count})");
            foreach (var e in made) try { if (Spawn.Exists(e)) Spawn.Destroy(e); } catch (Exception x) { Info($"destroying {e}: {x.Message}"); }
        }
    }

    private async Task GameHitsIn(WorldPosition anchor, List<EntityId> made, List<EntityId> gods)
    {
        var p = await Character(anchor with { X = anchor.X + 40, Z = anchor.Z + 20 }, made, gods);
        if (p.IsNone) return;
        var give = await Items.Give(p, GameSword, 1);
        await Steps(10);
        var slot = Items.Read(p)?.Slots.FirstOrDefault(s => !s.IsEmpty && s.Item.Value == Content.Items.Get(GameSword)?.Id);
        if (!Check(slot is not null, $"the game's sword is in the character's bag: {give}")) return;
        var equip = await Items.Equip(p, slot!.Slot);
        await Steps(10);
        Check(equip.Ok, $"and in its hand: {equip}");

        // Vukahs all round it, so that a swing meets one whichever way it faces; all of them watched
        var c = World.Position(p)!.Value;
        var ring = new List<EntityId>();
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var v = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(c.X + 1.6 * Math.Sin(a), c.Y, c.Z + 1.6 * Math.Cos(a)), (float)(i * 45 + 180),
                new SpawnOptions { Tag = "event-filter-test", Flags = SpawnFlags.Transient });
            made.Add(v);
            ring.Add(v);
        }
        for (var i = 0; i < 20 && ring.Any(v => Combat.GetHealth(v) is null); i++) await Steps(1);
        foreach (var v in ring)
        {
            Combat.SetInvulnerable(v);
            gods.Add(v);
        }
        var onRing = new List<EntityHurtEvent>();
        var byCharacter = new List<EntityHurtEvent>();
        using var a1 = Events.On<EntityHurtEvent>(e => { onRing.Add(e); if (onRing.Count <= 30) Info($"a hit about a watched Vukah: {Shape(e)}"); },
            EventFilter.ForEntities(ring));
        using var a2 = Events.On<EntityHurtEvent>(byCharacter.Add, EventFilter.ForEntity(p));
        await Steps(3);
        var held0 = Held;
        for (var tries = 0; tries < 6 && !onRing.Any(e => e.Attacker == p); tries++)
        {
            await Press(p, Mainhand | MainhandTap);
            await Steps(45);
        }
        await Steps(10);
        var mine = onRing.Where(e => e.Attacker == p).ToList();
        var theirs = onRing.Where(e => ring.Contains(e.Attacker)).ToList();
        Info($"the character's swings: {mine.Count} hurt event(s) on the Vukahs from {mine.Select(e => e.ImpactId).Distinct().Count()} impact(s); " +
             $"their own attacks on it: {theirs.Count}; hits held {held0} -> {Held}; " +
             $"the character's own handler: {byCharacter.Count}");
        if (!Check(mine.Count > 0, $"a sword swing of the character (the game's melee hit) reaches the Vukahs' filtered handler: {string.Join("; ", mine.Take(4).Select(Shape))}"))
            return;
        Check(mine.All(e => ring.Contains(e.Target) && e.Origin == CombatOrigin.Game),
            $"the game's melee hit names the Vukah it landed on as Target (and the character as Attacker): {string.Join("; ", mine.Take(4).Select(e => $"target {e.Target} part {e.Part} source {e.Source}"))}");
        Check(mine.All(e => byCharacter.Any(b => b.Step == e.Step && b.Target == e.Target)),
            $"the same hits reach a handler filtered on the character (its Attacker): {byCharacter.Count(e => e.Attacker == p)} of them");
        Check(Held - held0 >= (ulong)mine.Count, $"and were held for the handlers: hits held {held0} -> {Held}");
        // the owner's shape, if the Vukahs' AI runs here: a watched creature's own attack, Target the player
        if (theirs.Count > 0)
            Check(theirs.All(e => e.Target == p), $"a watched Vukah's own attack names the character as Target and the Vukah as Attacker: {string.Join("; ", theirs.Take(4).Select(Shape))}");
        else
            Info("the Vukahs did not attack the character here (no client behind it): their attacks are shown by Kiln's game hits above");
        // the same swing on Vukahs out of god mode: how many hurt events one impact gives when the hit applies (god mode
        // drops a hit before the game records that the impact touched the target)
        foreach (var v in ring) Combat.SetInvulnerable(v, false);
        await Steps(30);
        onRing.Clear();
        for (var tries = 0; tries < 6 && !onRing.Any(e => e.Attacker == p); tries++)
        {
            await Press(p, Mainhand | MainhandTap);
            await Steps(45);
        }
        await Steps(10);
        var plain = onRing.Where(e => e.Attacker == p).ToList();
        Info($"out of god mode: {plain.Count} hurt event(s) from {plain.Select(e => e.ImpactId).Distinct().Count()} impact(s) of the character's swings " +
             $"[{string.Join("; ", plain.Select(e => $"target {e.Target} impact 0x{e.ImpactId:x8} step {e.Step}"))}]; " +
             $"in god mode: {mine.Count} from {mine.Select(e => e.ImpactId).Distinct().Count()} [{string.Join("; ", mine.Select(e => $"target {e.Target} impact 0x{e.ImpactId:x8} step {e.Step}"))}]");
    }
}
