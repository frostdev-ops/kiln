// SPDX-License-Identifier: MIT
// The moves played: a spawned player character with the weapon in hand, dummies around it, the chain driven as a player's input would (when the lab can),
// else by Abilities.PlaySequence, and the step of the hurt event against the step the sequence started.
using Kiln;

namespace WeaponMovesTest;

public sealed partial class WeaponMovesTestMod
{
    private const int PlayerInputReady = 0x5b8;      // PlayerInput.fromClientIsReadyForGameplay (mods/tests/creative-test)
    private const int DigitalInput = 936 + 8;        // PlayerInput.fromClient.digitalInput: one bit per keen::ecs::PlayerInputType
    private const ulong Mainhand = 1UL << 0, MainhandTap = 1UL << 1, MainhandHold = 1UL << 2, MainhandRelease = 1UL << 3;
    private static int HeavyHold = 12;   // steps the attack button is held for a heavy attack (found by HeavyInput)
    private const string Doll = "Prop_Decoration_T3_TrainingDoll_01";

    private WorldPosition _at;
    private readonly List<EntityId> _dummies = new();
    private readonly List<HookHandle> _detours = new();

    private async Task World_()
    {
        _at = await Anchor();
        var p = await Character(_at);
        if (p.IsNone) return;
        if (!await ToHand(p, "chain-sword")) return;
        await Dummies(p);
        await ChainPlayed(p);
        await Hits(p);
        foreach (var h in _detours) h.Remove();
        _detours.Clear();
    }

    private async Task<WorldPosition> Anchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) return new WorldPosition(Math.Floor(inf.Position.X) + 40.25, inf.Position.Y, Math.Floor(inf.Position.Z) + 40.25);
        }
        await Steps(1);
        throw new InvalidOperationException("no anchor entity");
    }

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

    // a player character standing on the ground, ready for gameplay (as creative-test makes one)
    private async Task<EntityId> Character(WorldPosition near)
    {
        try { await Build.Load(Build.BoxAround(near, 8, 128, 8)); } catch (Exception e) when (e is InvalidOperationException or TimeoutException) { Info($"loading the terrain there: {e.Message}"); }
        var at = near with { Y = (GroundY(near) ?? near.Y) + 1.0 };
        var p = await Spawn.Entity("1_Player", at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        _spawned.Add(p);
        if (!Check(!p.IsNone && World.Has(p, "PlayerInput"), $"a player character {p} at {at}")) return EntityId.None;
        for (var i = 0; i < 50 && Combat.GetHealth(p) is null; i++) await Steps(1);
        Combat.SetInvulnerable(p);
        World.TryWrite(p, "PlayerInput", PlayerInputReady, (byte)1);
        foreach (var r in Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8"))
            _detours.Add(Hooks.Detour(r, _ => 1));  // "the terrain at the character is loaded"
        for (var i = 0; i < 240 && !PlayerStates.Is(p, PlayerStateId.IsReadyForGameplay); i++) await Steps(1);
        for (var i = 0; i < 400 && !PlayerStates.Is(p, PlayerStateId.Grounded); i++) await Steps(1);
        await Steps(10);
        Info($"the character: ready {PlayerStates.Is(p, PlayerStateId.IsReadyForGameplay)}, grounded {PlayerStates.Is(p, PlayerStateId.Grounded)}, at {World.Position(p)}");
        return p;
    }

    private static uint Held(EntityId p) => World.ReadBytes(p, "Equipment") is { Length: >= 20 } eq ? BitConverter.ToUInt32(eq, 8 + 8) : 0;

    private async Task<bool> ToHand(EntityId p, string block)
    {
        var item = Content.Items.Get(G(block)) ?? Content.Items.Get(block)!;   // a block of this mod, or a game item
        if (Items.Read(p)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == item.Id) is not { } slot)
        {
            var give = await Items.Give(p, item.Name, 1);
            await Steps(20);
            slot = Items.Read(p)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == item.Id);
            if (!Check(slot is not null, $"{block} is in the character's bag: {give}")) return false;
        }
        GameTree.GameRequest(p, 5, (uint)slot!.Slot.Index);
        await Steps(24);
        return Check(Held(p) == item.Id, $"{block} is in hand: Equipment slot 0 holds 0x{Held(p):x8} (the item 0x{item.Id:x8})");
    }

    // Training dolls (a prop that stands still) all round the character, at its height, so that a swing meets one whichever way it faces.
    private async Task Dummies(EntityId p)
    {
        var c = World.Position(p)!.Value;
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            var at = new WorldPosition(c.X + 1.7 * Math.Sin(a), c.Y, c.Z + 1.7 * Math.Cos(a));
            EntityId d;
            try { d = Build.PlaceProp(Doll, at, options: new BuildOptions { Flags = BuildFlags.InBases | BuildFlags.Transient }); }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException) { Info($"a doll at {at}: {e.Message}"); d = EntityId.None; }
            if (d.IsNone)
            {
                d = await Spawn.Entity("Enemy_Vukah_Melee", at, (float)(i * 45 + 180), new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
                _spawned.Add(d);
            }
            _dummies.Add(d);
        }
        await Steps(20);
        Info($"dummies round the character at {c}: {string.Join(", ", _dummies.Select(d => $"{d} {Spawn.Inspect(d)?.TemplateName} at {World.Position(d)} health {Combat.GetHealth(d)?.Current}"))}");
        Check(_dummies.Count(d => World.Exists(d)) == 8, $"8 dummies stand round the character ({string.Join(", ", _dummies.Select(d => Spawn.Inspect(d)?.TemplateName ?? "none").Distinct())})");
    }

    private void Refill()
    {
        foreach (var d in _dummies)
            if (World.Exists(d) && Combat.GetHealth(d) is { } h && h.Current < h.Max) Combat.SetHealth(d, h.Max);
    }

    private static string Name(uint id) => Content.Sequences.Get($"0x{id:x8}")?.Name ?? $"0x{id:x8}";

    // ---- 4. the chain, played ---------------------------------------------------------------------------------------

    private async Task ChainPlayed(EntityId p)
    {
        var one = Abilities.IdOf(CloneGuid("chain-sword", "primary.1"));
        var two = Abilities.IdOf(CloneGuid("chain-sword", "primary.2"));
        var w = new Watch(p);
        _watch = w;
        await Steps(30);
        // As a player's input: the attack button's bits in the character's replicated input, so that the game picks the item's slot and the follow-up.
        var pressed = false;
        foreach (var (bits, hold) in new[] { (Mainhand | MainhandTap, 1), (Mainhand, 4), (MainhandTap, 1), (Mainhand | MainhandRelease, 1) })
        {
            w.Seen.Clear();
            await Press(p, bits, hold);
            for (var i = 0; i < 20 && w.Seen.Count == 0; i++) await Steps(1);
            Info($"input bits 0x{bits:x} for {hold} step(s): the character started [{string.Join(", ", w.Seen.Select(s => Name(s.Sequence)))}]");
            if (w.Seen.Any(s => s.Sequence == one)) { pressed = true; break; }
            await Steps(50);
        }
        if (pressed)
        {
            // the follow-up: the button again while the running link's Primary opening is open, by the sequence's own clock
            var open = 0.0;
            for (var k = 1; k <= 2; k++)
            {
                for (var i = 0; i < 60 && w.Seen.Count < k; i++) await Steps(1);
                if (w.Seen.Count < k) break;
                var running = w.Seen[k - 1];
                var link = Content.Sequences.Get($"0x{running.Sequence:x8}")!;
                open = Openings(link).Where(o => o.Trigger == "Primary" && o.FollowUp != Guid.Empty).Min(o => o.Time);
                // a little before the follow-up time: inside the opening, as a player's second click is
                for (var i = 0; i < 120 && _clock.GetValueOrDefault(_step) / 60.0 - running.TriggerTime / 1e9 < open - 0.1; i++) await Steps(1);
                await Press(p, Mainhand | MainhandTap, 1);
            }
            for (var i = 0; i < 60 && w.Seen.Count < 3; i++) await Steps(1);
            var order = w.Seen.Select(s => s.Sequence).ToList();
            Info($"by input: [{string.Join(" -> ", w.Seen.Select(s => $"{Name(s.Sequence)} step {s.Step}"))}] (opening at {open:0.###} s)");
            Check(order.Count >= 3 && order[0] == one && order[1] == two && order[2] == one, $"the chain by a player's input: the game played link 1, then link 2, then link 1 again: [{string.Join(" -> ", order.Select(Name))}]");
            await Steps(40);
        }
        else Info("the lab's input bits started no attack: the chain is played by Abilities.PlaySequence");
        await LabUse(p, one);

        // by PlaySequence: link 1, then the sequence its Primary follow-up names, then that one's
        w.Seen.Clear();
        var first = Seq($"{G("chain-sword")}.primary.1");
        Abilities.PlaySequence(p, first.Guid);
        await Steps(8);
        var next = Openings(first).First(o => o.Trigger == "Primary" && o.FollowUp != Guid.Empty).FollowUp;
        Abilities.PlaySequence(p, next);
        await Steps(8);
        var after = Openings(Content.Sequences.Get(next.ToString())!).First(o => o.Trigger == "Primary" && o.FollowUp != Guid.Empty).FollowUp;
        Abilities.PlaySequence(p, after);
        await Steps(50);
        var seen = w.Seen.Select(s => s.Sequence).ToList();
        var expect = new List<uint> { one, two, one };
        if (_wrong) expect.Reverse(0, 2);
        Info($"by PlaySequence: [{string.Join(" -> ", w.Seen.Select(s => $"{Name(s.Sequence)} step {s.Step}"))}]");
        Check(seen.Take(3).SequenceEqual(expect), $"the chain, played from the server: link 1, the sequence its Primary follow-up names (link 2), and its follow-up (link 1), entered in that order: [{string.Join(" -> ", seen.Select(Name))}]");
        await Steps(30);
    }

    private async Task Press(EntityId p, ulong bits, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            World.TryWrite(p, "PlayerInput", DigitalInput, bits);
            await Steps(1);
        }
        World.TryWrite(p, "PlayerInput", DigitalInput, 0UL);
        await Steps(1);
    }

    // A swing with the sword as the used item, as a player's starts (KILN_PLAYERSTATE_LAB only): the request names the sequence, so the game does not pick the slot
    private async Task LabUse(EntityId p, uint sequence)
    {
        var sword = Content.Items.Get(G("chain-sword"))!;
        var slot = Items.Read(p)?.Slots.FirstOrDefault(s => !s.IsEmpty && s.Item.Value == sword.Id);
        if (slot == null) return;
        _watch!.Seen.Clear();
        if (!PlayerStates.Diagnostics.LabUseItem(p, sequence, PlayerStateId.ActionPrimary, slot.Slot.Value, sword.Id))
        {
            Info("KILN_PLAYERSTATE_LAB is off: no swing with the sword as the used item");
            return;
        }
        await Steps(10);
        Info($"LabUseItem(ActionPrimary, the sword as the used item, link 1): the character started [{string.Join(", ", _watch.Seen.Select(s => Name(s.Sequence)))}]");
        Check(_watch.Seen.Any(s => s.Sequence == sequence), "a swing started as a player's is, with the sword as the used item: link 1 plays");
        await Steps(50);
    }

    // ---- 5. the blow lands at hitAt ---------------------------------------------------------------------------------

    private enum Start { Play, Use, Input, InputSecond, InputHeavy }

    /// <summary>Starts a sequence and returns the step it started in, the first step a hurt event of the character came from, and the game time between them.
    /// <see cref="Start.Play"/>: Abilities.PlaySequence (no used item); <see cref="Start.Use"/>: LabUseItem with the held weapon as the used item;
    /// <see cref="Start.Input"/>: the attack button in the character's input, so the game picks the held weapon's slot (a light attack only).</summary>
    private async Task<(ulong Start, ulong? Hit, double? Seconds, string Detail, double? Opened, double OpenStep, long? StepsOff)> Blow(double at, EntityId p, Guid sequence, int steps, Start how, PlayerStateId trigger)
    {
        Refill();
        await Steps(4);
        var w = new Watch(p);
        _watch = w;
        await Steps(2);
        _hurts.Clear();
        _damaged.Clear();
        _impacts.Clear();
        var id = Abilities.IdOf(sequence);
        var impacts0 = World.Find("Impact").Count;
        var played = true;
        switch (how)
        {
            case Start.Play: played = Abilities.PlaySequence(p, sequence); break;
            case Start.Use:
                var held = Held(p);
                var slot = Items.Read(p)?.Slots.FirstOrDefault(x => !x.IsEmpty && x.Item.Value == held);
                played = slot != null && PlayerStates.Diagnostics.LabUseItem(p, id, trigger, slot.Slot.Value, held);
                break;
            case Start.Input: await Press(p, Mainhand | MainhandTap, 1); break;
            case Start.InputSecond:
                // the chain's second link: the button again once the first link's Primary opening is open
                await Press(p, Mainhand | MainhandTap, 1);
                for (var i = 0; i < 30 && w.Seen.Count == 0; i++) await Steps(1);
                var open = w.Seen.Count > 0 && Content.Sequences.Get($"0x{w.Seen[0].Sequence:x8}") is { } first
                    ? Openings(first).Where(o => o.Trigger == "Primary" && o.FollowUp != Guid.Empty).Select(o => o.Time).DefaultIfEmpty(0.6).Min() : 0.6;
                await Steps((int)Math.Ceiling(open / StepSeconds()) + 1);
                await Press(p, Mainhand | MainhandTap, 1);
                break;
            default: await Press(p, Mainhand | MainhandHold, HeavyHold); break;
        }
        var most = 0;
        for (var i = 0; i < steps; i++)
        {
            await Steps(1);
            most = Math.Max(most, World.Find("Impact").Count - impacts0);
        }
        _watch = null;
        var start = w.Seen.FirstOrDefault(s => s.Sequence == id);
        if (!played || start.Sequence == 0) return (0, null, null, $"{how}: started {played}, seen [{string.Join(", ", w.Seen.Select(s => Name(s.Sequence)))}]", null, 0, null);
        var mine = _hurts.Where(h => (h.Attacker == p || _dummies.Contains(h.Target)) && h.Step >= start.Step).OrderBy(h => h.Step).ToList();
        var hit = mine.FirstOrDefault();
        var impacts = _impacts.Where(x => x.Root == p).ToList();
        var detail = $"{how}: hurt events [{string.Join("; ", mine.Select(h => $"step {h.Step} on {h.Target} by {h.Attacker} {h.Amount}"))}]; damaged [{string.Join("; ", _damaged.Select(h => $"step {h.Step} on {h.Target} by {h.Attacker} {h.Amount}"))}]; " +
                     $"impacts started by the character [{string.Join("; ", impacts.Select(x => $"step {x.Step} 0x{x.Program:x8} {x.Template}"))}], at most {most} more Impact entities than before; trigger time {start.TriggerTime} ns, clock at start {_clock.GetValueOrDefault(start.Step)}";
        // game time since the sequence's own start: its trigger time is on the same clock (ECS nanoseconds = the 60 Hz clock's ticks)
        double Since(ulong step) => _clock.GetValueOrDefault(step) / 60.0 - start.TriggerTime / 1e9;
        // the damage window: the first impact the sequence's SpawnImpact spawned (the game's default impact template) after the start
        var window = impacts.Where(x => x.Template == "DefaultImpact" && x.Step >= start.Step).Select(x => (ulong?)x.Step).FirstOrDefault();
        var opened = window is { } ws ? Since(ws) : (double?)null;
        // the step the window opened in: an event fires in the first step at or past its time, so it comes at most this late
        var openStep = window is { } wo ? (_clock.GetValueOrDefault(wo) - (double)_clock.GetValueOrDefault(wo - 1)) / 60.0 : 0;
        // the step at whose time the sequence's clock first reaches the moment: the window opens in it, or (the spawn is reported a step later) the next
        var due = start.Step;
        while (_clock.ContainsKey(due + 1) && Since(due) < at - 1e-6) due++;
        long? off = window is { } wd ? (long)wd - (long)due : null;
        if (mine.Count == 0) return (start.Step, null, null, detail, opened, openStep, off);
        return (start.Step, hit.Step, Since(hit.Step), detail, opened, openStep, off);
    }

    private double StepSeconds()
    {
        var keys = _clock.Keys.Order().ToList();
        if (keys.Count < 20) return 1 / 24.5;
        var span = keys.TakeLast(200).ToList();
        return (_clock[span[^1]] - (double)_clock[span[0]]) / 60.0 / (span[^1] - span[0]);
    }

    private async Task Hits(EntityId p)
    {
        Check(Combat.Available, "kiln.combat is there: hurt events come");
        // which start lands a blow at all, with the game's own light chain on the plain sword: the dolls first, then creatures in their place
        Start? how = null;
        if (!await ToHand(p, "plain-sword")) return;
        async Task<Start?> Probe()
        {
            foreach (var m in new[] { Start.Input, Start.Use, Start.Play })
            {
                var b = await Blow(Seq(Light1).Get("events[SpawnImpact#0].time").Number, p, Seq(Light1).Guid, 50, m, PlayerStateId.ActionPrimary);
                Info($"a swing of the game's light chain, started by {m}: {b.Detail}");
                if (b.Hit != null) return m;
            }
            return null;
        }
        how = await Probe();
        if (how == null)
        {
            foreach (var d in _dummies.ToList())
                if (World.Exists(d)) Spawn.Destroy(d);
            _dummies.Clear();
            var c = World.Position(p)!.Value;
            for (var i = 0; i < 8; i++)
            {
                var a = i * Math.PI / 4;
                var v = await Spawn.Entity("Enemy_Vukah_Melee", new WorldPosition(c.X + 1.6 * Math.Sin(a), c.Y, c.Z + 1.6 * Math.Cos(a)), (float)(i * 45 + 180), new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
                _spawned.Add(v);
                _dummies.Add(v);
            }
            await Steps(20);
            Info("no hurt event from the dolls: Vukahs round the character instead");
            how = await Probe();
        }
        if (!Check(how != null, $"a swing of the game's own light chain lands a hurt event on a dummy (started by {how?.ToString() ?? "nothing tried"})")) return;
        async Task One(string label, string weapon, Guid sequence, Start m, double at)
        {
            if (!await ToHand(p, weapon)) return;
            var want = _wrong && label.StartsWith("link 1", StringComparison.Ordinal) ? at + 0.3 : at;
            var b = await Blow(want, p, sequence, 90, m, label.Contains("heavy", StringComparison.Ordinal) ? PlayerStateId.ActionHeavy : PlayerStateId.ActionPrimary);
            var step = StepSeconds();
            var span = Content.Sequences.Get(sequence.ToString())!.Get("events[SpawnImpact#0].duration").Number;
            Info($"{label}: started step {b.Start}, damage window opened {b.Opened?.ToString("0.###") ?? "-"} s (in a step of {b.OpenStep:0.###} s), first hurt step {b.Hit?.ToString() ?? "none"} = {b.Seconds?.ToString("0.###") ?? "-"} s ({(b.Hit is { } h ? (long)(h - b.Start) : -1)} steps of {step:0.####} s); {b.Detail}");
            // one step: the step it fired in or the mean step of this run, whichever is longer, and the 60 Hz clock both ends are read on (1/60 s)
            Check(b.Opened is { } o && Math.Abs(o - want) <= Math.Max(step, b.OpenStep) + 1 / 60.0 + 1e-6,
                $"{label}: its damage window (the SpawnImpact's impact) opens {b.Opened?.ToString("0.###") ?? "never"} s after the sequence started, {want:0.###} s asked, within one step ({step:0.###} s, read on the 1/60 s clock): {b.StepsOff?.ToString("+0;-0;0") ?? "no"} step(s) from the step the sequence's clock reaches it in (that step {b.OpenStep:0.###} s)");
            Check(b.Seconds is { } s && s >= want - step - 1e-3 && s <= want + span + step + 1e-3,
                $"{label}: the hurt event on the dummy comes {b.Seconds?.ToString("0.###") ?? "never"} s after the start ({(b.Hit is { } h2 ? (long)(h2 - b.Start) : -1)} steps), inside the blow's window {want:0.###}..{want + span:0.###} s give or take one step");
        }
        var heavy = how == Start.Input ? await HeavyInput(p) : how!.Value;
        var light = how!.Value;
        await One("link 1 of chain-sword, retimed to 0.40 s", "chain-sword", CloneGuid("chain-sword", "primary.1"), light, 0.40);
        await One("link 2 of chain-sword, retimed to 0.45 s", "chain-sword", CloneGuid("chain-sword", "primary.2"), light == Start.Input ? Start.InputSecond : light, 0.45);
        await One("chain-sword's heavy, retimed to 0.60 s", "chain-sword", CloneGuid("chain-sword", "heavy"), heavy, 0.60);
        await One($"the control, the game's {Light1} on the plain sword, unchanged", "plain-sword", Seq(Light1).Guid, light, Seq(Light1).Get("events[SpawnImpact#0].time").Number);
        await One("heavy-greataxe's heavy, retimed to 1.40 s", "heavy-greataxe", CloneGuid("heavy-greataxe", "heavy"), heavy, 1.40);
        await One($"the control, the game's {AxeHeavy} on the game's greataxe, unchanged", AxeStarter, Seq(AxeHeavy).Guid, heavy, Seq(AxeHeavy).Get("events[SpawnImpact#0].time").Number);
    }

    // How long the button is held for the game to start the held weapon's heavy attack, on the chain sword
    private async Task<Start> HeavyInput(EntityId p)
    {
        if (!await ToHand(p, "chain-sword")) return Start.Use;
        var want = Abilities.IdOf(CloneGuid("chain-sword", "heavy"));
        foreach (var hold in new[] { 12, 20, 6 })
        {
            var w = new Watch(p);
            _watch = w;
            await Steps(2);
            await Press(p, Mainhand | MainhandHold, hold);
            await Steps(10);
            _watch = null;
            Info($"the button held {hold} steps: the character started [{string.Join(", ", w.Seen.Select(s => Name(s.Sequence)))}]");
            if (w.Seen.Any(s => s.Sequence == want))
            {
                HeavyHold = hold;
                await Steps(60);
                Check(true, $"a held attack button starts the held weapon's heavy slot (the button {hold} steps): the game played chain-sword's heavy");
                return Start.InputHeavy;
            }
            await Steps(60);
        }
        Info("the held button started no heavy attack: heavies start with the weapon as the used item (LabUseItem)");
        return Start.Use;
    }
}
