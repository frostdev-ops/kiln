// SPDX-License-Identifier: MIT
// NoClip on a dedicated server with no player (docs/kiln/services/rules.md, "NoClip"). A spawned player character stands
// in for a player (Rules.Simulate); the test writes its held input.
using Kiln;

namespace NoClipTest;

public sealed class NoClipTestMod : Mod
{
    private const string Me = "noclip-test";
    private const int PlayerInputReady = 0x5b8;  // PlayerInput.fromClientIsReadyForGameplay
    private const int State = 0x59, Held = 936 + 8;  // DynamicLocomotion.state; PlayerInput's held input bits
    // PlayerInput.fromClient.movementStick (float2, y forward) and fromClient.camera.orientation (x, y, z, w)
    private const int Stick = 0x2c0, CameraRotation = 0x40;
    private int _pass, _fail;
    private EntityId _neighbour;

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
        for (var i = 0; i < 240 && !(Spawn.WorldRunning && Build.Available); i++) await Steps(25);
        await Steps(50);
        var f = await Character();
        if (f.IsNone) return;
        Rules.Simulate(f);
        try
        {
            await Explore(f);
        }
        finally
        {
            Rules.Simulate(f, false);
            Spawn.Destroy(f);
            if (!_neighbour.IsNone) Spawn.Destroy(_neighbour);
        }
    }

    // Holds the stick forward for `steps` steps (the camera looking along +z): how far the character got across, and in
    // how many of those steps it was on its feet (grounded, walking or sliding). The flight's state moves a character
    // across too, but in the air.
    private async Task<(double Distance, int OnFoot)> Walk(EntityId f, int steps)
    {
        var from = World.Position(f)!.Value;
        World.TryWrite(f, "PlayerInput", CameraRotation, System.Numerics.Quaternion.Identity);
        World.TryWrite(f, "PlayerInput", Stick, new System.Numerics.Vector2(0, 1));
        var onFoot = 0;
        for (var i = 0; i < steps; i++)
        {
            await Steps(1);
            if (PlayerStates.Is(f, PlayerStateId.Grounded) && Loco(f) is 0 or 1) onFoot++;
        }
        World.TryWrite(f, "PlayerInput", Stick, System.Numerics.Vector2.Zero);
        await Steps(10);
        var to = World.Position(f)!.Value;
        return (Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Z - from.Z) * (to.Z - from.Z)), onFoot);
    }

    private byte Loco(EntityId f) => World.ReadBytes(f, "DynamicLocomotion") is { Length: > State } b ? b[State] : (byte)255;
    private static double Y(EntityId f) => World.Position(f)!.Value.Y;
    private static void Hold(EntityId f, bool jump, bool sneak, bool sprint = false) =>
        World.TryWrite(f, "PlayerInput", Held, (jump ? 1UL << 32 : 0) | (sneak ? 1UL << 55 : 0) | (sprint ? 1UL << 53 : 0));

    // MovementCollider: isWorldColliderReady +4, isEnabled +5, stuckTimer +8, stuckHeight +12, results +16 (m_isSet +80)
    private static string Collider(EntityId f)
    {
        if (World.ReadBytes(f, "MovementCollider") is not { Length: >= 84 } b) return "no MovementCollider";
        return $"ready {b[4]} enabled {b[5]} stuck {BitConverter.ToSingle(b, 8):0.00}s/{BitConverter.ToSingle(b, 12):0.00} set {b[80]} " +
               $"groundDist {BitConverter.ToSingle(b, 16 + 36):0.00} ground {b[16 + 57]} walkable {b[16 + 58]} spheroid {b[16 + 59]} emergency {b[16 + 61]}";
    }

    // The voxels at the character's feet, waist and head
    private static string Voxels(WorldPosition p)
    {
        string At(double dy)
        {
            try
            {
                return Build.TryRead(Build.BoxAround(p with { Y = p.Y + dy }, 0.01, 0.01, 0.01), out var c) && c.Length > 0 ? c[0].ToString() : "?";
            }
            catch (Exception e) { return "!" + e.GetType().Name; }
        }
        return $"feet {At(0.1)} waist {At(1.0)} head {At(1.7)}";
    }

    private string Line(EntityId f, double ground) =>
        $"y {Y(f) - ground:+0.00;-0.00} state {Loco(f)} grounded {PlayerStates.Is(f, PlayerStateId.Grounded)} {NoClip.Of(f)} | {Collider(f)} | {Voxels(World.Position(f)!.Value)}";

    private async Task<bool> Until(Func<bool> ok, int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            if (ok()) return true;
            await Steps(1);
        }
        return ok();
    }

    private static bool Solid(WorldPosition p, double dy) =>
        Build.TryRead(Build.BoxAround(p with { Y = p.Y + dy }, 0.01, 0.01, 0.01), out var c) && c.Length > 0 && !c[0].IsAir;

    private async Task Home(EntityId f, WorldPosition home)
    {
        await Teleport.To(f, home, new TeleportOptions { FindGround = false });
        await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded), 120);
        await Steps(20);
    }

    private async Task Explore(EntityId f)
    {
        var ended = new List<PlayerNoClipEndedEvent>();
        using var sub = Events.On<PlayerNoClipEndedEvent>(e => { if (e.Player == f) ended.Add(e); });
        var changed = 0;
        using var sub2 = Events.On<PlayerRulesChangedEvent>(e => { if (e.Player == f && e.Before.NoClip != e.After.NoClip) changed++; });

        var status = Rules.Status();
        Info($"hooks: {status?.Detail}; rules.noclip: {Capabilities.Why(NoClip.Capability)}");
        if (!Check(status is { NoClip: true, CharacterFlight: true } && NoClip.Available && Capabilities.Is(NoClip.Capability),
                "the collision hook and character flight's hooks are installed, and the capability rules.noclip says so")) return;
        if (!Check(Rules.Policy.AllowNoClip, "this lab's rules.json allows NoClip (allowNoClip)")) return;
        // how the character moves before NoClip: what every end of NoClip must give back
        var walk0 = await Walk(f, 40);
        await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded), 60);
        var loco0 = Loco(f);
        Info($"before NoClip: state {loco0}, grounded {PlayerStates.Is(f, PlayerStateId.Grounded)}, with the stick held it walks " +
             $"{walk0.Distance:0.0} m in 40 steps, on its feet in {walk0.OnFoot} of them");
        var home = World.Position(f)!.Value with { Y = Y(f) + 0.3 };
        var ground = Y(f);
        Info($"start: {Line(f, ground)}");
        Check(!NoClip.Of(f).On && !NoClip.Of(f).Active && NoClip.Of(f).LastFree is null, "before: NoClip is off, nothing active, no last free position");

        // ---- 1. a mod's rule set: at once into flight, then down through the ground
        var set = Rules.Apply(f, new PlayerRules { NoClip = true }, label: "noclip-test");
        Check(Rules.Of(f).NoClip && Rules.Of(f).ToString().Contains("NoClip") && changed == 1, $"a rule set with NoClip: the player's rules carry it ({Rules.Of(f)}), and the rules-changed event came");
        var took = await Until(() => Loco(f) == 11 && NoClip.Of(f).Active, 10);
        Check(took, $"standing on the ground it takes off at once: state {Loco(f)}, active {NoClip.Of(f).Active}");
        await Steps(10);
        Check(Math.Abs(Y(f) - ground) < 0.15 && !PlayerStates.Is(f, PlayerStateId.Grounded), $"it hovers where it stood, not grounded any more: {Y(f) - ground:+0.00;-0.00} m");
        var free0 = NoClip.Of(f).LastFree;
        Check(free0 is { } p0 && Math.Abs(p0.Y - Y(f)) < 0.3, $"the last free position is where it is: {free0}");
        var skipped = NoClip.Of(f).StepsSkipped;
        Hold(f, false, true);
        var sank = await Until(() => Y(f) < ground - 5, 60);
        Hold(f, false, false);
        await Steps(8);
        var deep = World.Position(f)!.Value;
        Check(sank && Solid(deep, 0.5) && Solid(deep, 1.0) && Solid(deep, 1.7), $"holding sneak it sinks through the ground into solid rock: {Line(f, ground)}");
        Check(NoClip.Of(f).StepsSkipped > skipped + 10, $"its collision steps were skipped: {NoClip.Of(f).StepsSkipped - skipped} more");
        var free = NoClip.Of(f).LastFree;
        Check(free is { } p1 && p1.Y > ground - 1.0 && p1.Y <= ground + 0.3, $"the last free position stayed at the surface: {(free is { } q ? q.Y - ground : double.NaN):+0.00;-0.00} m from the ground");
        var y0 = Y(f);
        await Steps(25);
        Check(Math.Abs(Y(f) - y0) < 0.3 && Loco(f) == 11, $"inside the rock it hovers with no input: {Y(f) - y0:+0.00;-0.00} m over 25 steps");

        // ---- 2. the rule ends there: back to the last free position before collision returns
        ended.Clear();
        set.Dispose();
        var st = NoClip.Of(f);
        Check(!st.On && st.Restoring, $"the rule set ended inside the rock: NoClip is off in the rules and the character is being moved back ({st})");
        Check(NoClip.Of(f).Active || Loco(f) == 11, "collision stays off while it is moved");
        var back = await Until(() => ended.Count > 0, 100);
        await Steps(3);
        var at = World.Position(f)!.Value;
        Check(back && ended[0] is { Reason: NoClipEnd.RuleEnded, RestoredTo: { } r } && Math.Abs(r.Y - free!.Value.Y) < 0.01 && Math.Abs(at.Y - r.Y) < 1.5,
            $"the ended event says RuleEnded with the position it was put back at; it is there: {(ended.Count > 0 ? ended[0].ToString() : "no event")}, now {at.Y - ground:+0.00;-0.00} m from the ground");
        var stands = await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded) && Loco(f) != 11, 200);
        await Steps(20);
        Check(stands && Y(f) > ground - 1.2 && !NoClip.Of(f).Active, $"collision is back and it stands on the ground, not under it: {Line(f, ground)}");
        y0 = Y(f);
        await Steps(40);
        Check(Math.Abs(Y(f) - y0) < 0.2, $"and it stays there: {Y(f) - y0:+0.00;-0.00} m over 40 steps");

        // ---- 3. Kiln's switch: on, up into the air, off in open space (no move back)
        await Home(f, home);
        ground = Y(f);
        var on = NoClip.Set(f, true);
        Check(on is { Ok: true, On: true, Reason: null } && Rules.Of(f).NoClip, $"NoClip.Set on: {on.Message}");
        Check(NoClip.Set(f, true) is { Ok: true, On: true }, "on again is fine");
        await Until(() => NoClip.Of(f).Active, 10);
        Hold(f, false, true);
        await Until(() => Y(f) < ground - 3, 40);
        Hold(f, true, false);
        var up = await Until(() => Y(f) > ground + 4, 80);
        Hold(f, false, false);
        await Steps(8);
        Check(up && NoClip.Of(f).LastFree is { } p2 && Math.Abs(p2.Y - Y(f)) < 0.6, $"down into the ground and back up through it into the air: {Y(f) - ground:+0.00;-0.00} m, last free position there");
        ended.Clear();
        var off = NoClip.Set(f, false);
        Check(off is { Ok: true, On: false } && !NoClip.Of(f).Restoring, $"NoClip.Set off in open space: {off.Message}");
        await Until(() => ended.Count > 0, 10);
        Check(ended.Count == 1 && ended[0] is { Reason: NoClipEnd.RuleEnded, RestoredTo: null }, $"the ended event has no restored position: {(ended.Count > 0 ? ended[0].ToString() : "no event")}");
        var landed = await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded), 200);
        await Steps(10);
        Check(landed && Y(f) > ground - 1.2 && Y(f) < ground + 1, $"it falls and lands on the ground: {Math.Round(Y(f) - ground, 2):+0.00;-0.00;0.00} m");
        Check(NoClip.Set(f, false) is { Ok: true, On: false }, "off again is fine");

        // ---- 4. a teleport ends it, and the request is used up until it is asked again
        await Home(f, home);
        ground = Y(f);
        NoClip.Set(f, true);
        var mine = Rules.Apply(f, new PlayerRules { NoClip = true }, label: "noclip-test-2");
        await Until(() => NoClip.Of(f).Active, 10);
        await Steps(5);
        ended.Clear();
        await Teleport.To(f, home with { X = home.X + 6, Y = home.Y + 2 }, new TeleportOptions { FindGround = false });
        var tripped = await Until(() => ended.Count > 0, 30);
        Check(tripped && ended[0].Reason == NoClipEnd.Teleport && ended[0].RestoredTo is null && !Rules.Of(f).NoClip,
            $"a teleport ends it: {(ended.Count > 0 ? ended[0].ToString() : "no event")}; the rules no longer carry it though two rule sets still ask");
        await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded), 200);
        await Steps(30);
        Check(!NoClip.Of(f).Active && Loco(f) != 11 && !Rules.Of(f).NoClip, $"it stays off afterwards: state {Loco(f)}");
        mine.Update(mine.Rules);
        Check(Rules.Of(f).NoClip, "updating the mod's rule set asks anew: on again");
        mine.Dispose();
        Check(!Rules.Of(f).NoClip, "the mod's set gone, Kiln's own switch is still used up: off");
        await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded), 200);
        Check(NoClip.Set(f, true) is { Ok: true, On: true } && Rules.Of(f).NoClip, "NoClip.Set on asks anew for Kiln's switch");
        await Until(() => NoClip.Of(f).Active, 10);

        // ---- 5. an explicit false in another rule set wins
        var no = Rules.Apply(f, new PlayerRules { NoClip = false }, label: "noclip-test-no");
        Check(!Rules.Of(f).NoClip, "a rule set that says NoClip false turns it off for the player");
        no.Dispose();
        Check(Rules.Of(f).NoClip, "and without it the switch counts again");
        await Until(() => NoClip.Of(f).Active, 10);

        // ---- 5b. a teleport that moves the character's records. The game keeps components by archetype: a second
        // player character spawned after this one shares its tables, and the Teleport component a teleport adds and takes
        // away moves this one's components to another place (docs/re/creatures.md 1.4). The owner's game showed it:
        // collision came back, walking and animation did not (the flight's state stayed). Every end leaves the
        // character as before NoClip.
        _neighbour = await Spawn.Entity("1_Player", home with { X = home.X - 3, Y = home.Y + 1 }, 0,
            new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        if (Check(!_neighbour.IsNone, $"another player character beside it: {_neighbour}"))
        {
            await Steps(20);
            await Until(() => NoClip.Of(f).Active, 10);
            ended.Clear();
            await Teleport.To(f, home with { X = home.X + 3, Y = home.Y + 2 }, new TeleportOptions { FindGround = false });
            var tripped2 = await Until(() => ended.Count > 0, 30);
            Check(tripped2 && ended[0].Reason == NoClipEnd.Teleport && !Rules.Of(f).NoClip,
                $"with the other character beside it, a teleport ends it too: {(ended.Count > 0 ? ended[0].ToString() : "no event")}");
            var standing = await Until(() => PlayerStates.Is(f, PlayerStateId.Grounded) && Loco(f) != 11, 200);
            await Steps(30);
            Check(standing && Loco(f) is 0 or 1 && PlayerStates.Is(f, PlayerStateId.Grounded) && !NoClip.Of(f).Active,
                $"after that teleport it is out of the flight and on its feet as before NoClip (state {loco0} then): {Line(f, ground)}");
            var walk1 = await Walk(f, 40);
            if (walk0.Distance > 1 && walk0.OnFoot >= 30)
                Check(walk1.Distance > walk0.Distance / 2 && walk1.OnFoot >= 30,
                    $"and it walks again: {walk1.Distance:0.0} m in 40 steps with the stick held, on its feet in {walk1.OnFoot} of them " +
                    $"(before NoClip {walk0.Distance:0.0} m, {walk0.OnFoot})");
            else Info($"the lab's stick did not walk it before NoClip either ({walk0}; after: {walk1})");
            Check(NoClip.Set(f, true) is { Ok: true, On: true }, "NoClip.Set on asks anew after that teleport");
            await Until(() => NoClip.Of(f).Active, 10);
        }

        // ---- 6. death ends it
        await Steps(5);
        ended.Clear();
        Combat.SetInvulnerable(f, false);
        Combat.SetHealth(f, 0);
        var died = await Until(() => ended.Count > 0, 60);
        Check(died && ended[0].Reason == NoClipEnd.Death && !Rules.Of(f).NoClip, $"death ends it: {(ended.Count > 0 ? ended[0].ToString() : $"no event (health {Combat.GetHealth(f)})")}");
        await Steps(10);
        Check(!NoClip.Of(f).Active && Loco(f) != 11, $"and the body has collision again and is out of the flight: state {Loco(f)}");
        Info($"end: {Line(f, ground)}; NoClip status: {status}");
    }

    // ---- a player character near the first base, ready for gameplay (as mods/tests/creative-test does it) ----
    private async Task<EntityId> Character()
    {
        var bases = Build.Bases;
        WorldPosition anchor;
        if (bases.Count > 0) anchor = bases[0].Center;
        else if (Spawn.FindByName("FlameAltar", max: 1).FirstOrDefault() is { IsNone: false } a && Spawn.Inspect(a) is { } ai) anchor = ai.Position;
        else anchor = new WorldPosition(3845 - 160, 835, 1480);  // a fresh world: the hills where a new character starts
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
