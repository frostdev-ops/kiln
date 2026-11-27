// SPDX-License-Identifier: MIT
// Pet clearance calibration on a dedicated lab server (dossier 19d, gate C6), player-free:
//   fixtures  sealed lanes of building blocks (Fixtures.cs): corridors 0.5/1/2 m wide with 1/2/3 m headroom, a 1.5 m gap
//             under 3 m, 1.5 m of headroom over 2 m, a door-wall doorway, two S-bends, the wide open control; written once
//             per session (Transient), far from the base;
//   bodies    each plan creature spawned without an owner, forced ready (lab), invulnerable, on the players' side, at its
//             scale, held by Kiln.Pets' own broker leases (PetControl: PetStay, then PetFollow) as a pet's body is;
//   sweep     per fixture, kiln.queries routes from the start to the exit room for every HorizontalSpace x VerticalSpace
//             pair: requirements, status, points (with their move tags);
//   walks     every accepted route's pair walked by Kiln.Pets' real follower and mover (PetFollower + GamePetMover, the
//             body's ActorInput under the broker's can_drive), catch-up and teleport off, the body's position every step;
//   native    (comparison) the same body under an EngineGuard lease, the game's own guard walk to a marker at the exit;
//   roster    (needs an owner: LABBOT's bot) a pet of the first creature summoned for a connected player through the
//             Pets roster, its body's trace while the player stands at the control lane; skipped without a player.
// Evidence: kiln/data/pets-clearance-lab/runs.jsonl (tools/pets-clearance/profiles.py judges it and writes the profiles);
// "pets-clearance-lab: ..." lines in kiln.log, the last one "pets-clearance-lab: DONE".
// Time is the game's own clock (TickInfo.ClockDelta, 60 Hz units, summed per step, as kiln.creature_control's sim clock),
// never a count of steps: a dedicated server steps its world 15 to 25 times a second, not 60. Every trace time, wait,
// timeout and the follower's own timers (repath, stuck) run on it.
// Needs kiln/data/kiln.creatures/force-ready (forced readiness, lab only) and detours the two terrain checks of
// actor_is_ready_for_gameplay (as creature-control-test does), so bodies ground and walk without a player.
using System.Diagnostics;
using System.Numerics;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsClearanceLab;

public sealed partial class PetsClearanceLabMod : Mod
{
    internal const string Me = "pets-clearance-lab";
    private const int Actor_State = 0xbf8, Flag_Grounded = 0, Flag_Ready = 50;

    private Out? _out;
    private Plan _plan = new();
    private readonly List<EntityId> _spawned = [];
    private readonly List<HookHandle> _hooks = [];
    private readonly List<PetControl> _controls = [];
    private long _steps;
    private double _game;   // game seconds since load (the game's 60 Hz clock)
    private readonly Stopwatch _wall = new();
    private int _walks, _queries;

    internal void Info(string s) => Log.Info($"{Me}: {s}");

    public override void OnLoad()
    {
        Context.Tick.Pre += OnPre;
        _ = RunGuarded();
    }

    public override void OnUnload()
    {
        Context.Tick.Pre -= OnPre;
        Cleanup();
    }

    private void OnPre(in TickInfo t) => _game += t.ClockDelta / 60.0;

    private void Cleanup()
    {
        foreach (var c in _controls)
            try { c.Release(); } catch (Exception) { }
        _controls.Clear();
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch (Exception) { }
        _spawned.Clear();
        foreach (var h in _hooks)
            try { h.Remove(); } catch (Exception) { }
        _hooks.Clear();
    }

    internal async Task Steps(int n)
    {
        for (var i = 0; i < n; i++)
        {
            await Context.Scheduler.NextTick();
            _steps++;
        }
    }

    /// <summary>Waits game time: at least <paramref name="seconds"/> of the game's clock, at least one step.</summary>
    internal async Task Wait(double seconds)
    {
        var until = Now + seconds;
        do await Steps(1);
        while (Now < until);
    }

    /// <summary>Steps until <paramref name="done"/> holds or <paramref name="seconds"/> of game time passed; whether it held.</summary>
    internal async Task<bool> Until(Func<bool> done, double seconds)
    {
        var until = Now + seconds;
        while (!done())
        {
            if (Now >= until) return false;
            await Steps(1);
        }
        return true;
    }

    /// <summary>Game seconds since load: the game's 60 Hz clock as the steps advanced it (the follower's clock here).</summary>
    internal double Now => _game;

    internal long StepCount => _steps;

    private async Task RunGuarded()
    {
        _wall.Start();
        try
        {
            var sw = Stopwatch.StartNew();
            while (!(Spawn.WorldRunning && Build.Available && Creatures.Available) && sw.Elapsed.TotalSeconds < 240) await Steps(1);
            Info($"world running after {sw.Elapsed.TotalSeconds:F1} s");
            var ready = Symbols.FindAll("48 89 5C 24 08 57 48 83 EC 30 F3 0F 10 15 ?? ?? ?? ?? 0F 57 C0 F3 49 0F 2A 00 48 8B C2 48 8B F9 48 8D 54 24 20 48 8B C8 49 8B D8");
            foreach (var r in ready) _hooks.Add(Hooks.Detour(r, _ => 1));
            Info($"terrain readiness checks detoured: {ready.Length}");
            _plan = Plan.Read(Path.Combine(Context.DataDirectory, "plan.json"), out var problem);
            if (problem is not null) Info(problem);
            Directory.CreateDirectory(Context.DataDirectory);
            _out = new Out(Path.Combine(Context.DataDirectory, "runs.jsonl"));
            await Session(ready.Length);
            if (_plan.Census) await Census();
            var site = await BuildSite();
            if (site is null) return;
            foreach (var c in _plan.Creatures) await Calibrate(c, site);
            if (_plan.Roster) await Roster(site);
        }
        catch (Exception e)
        {
            Info($"FAIL the session threw: {e}");
            _out?.Write("error", new JsonObject { ["error"] = e.ToString() });
        }
        finally
        {
            Cleanup();
            var rate = _game > 0 ? _steps / _game : 0;
            _out?.Write("done", new JsonObject
            {
                ["steps"] = _steps, ["gameSeconds"] = Out.R(_game), ["wallSeconds"] = Out.R(_wall.Elapsed.TotalSeconds),
                ["stepsPerGameSecond"] = Out.R(rate), ["queries"] = _queries, ["walks"] = _walks,
            });
            _out?.Dispose();
            Info($"DONE: {_queries} route requests, {_walks} walks, {_steps} steps in {_game:F0} s of game time ({_wall.Elapsed.TotalSeconds:F0} s wall, {rate:F1} steps a game second)");
        }
    }

    private async Task Session(int detours)
    {
        PetClearance.Warm();
        await Until(() => PetClearance.GameBuild() is not null, 30);
        var build = PetClearance.GameBuild();
        Info($"game build {build?.ToString() ?? "unknown"}; creature_control {Capabilities.Is("creature_control")}, queries.path {Capabilities.Is("queries.path")}, kiln.gamedata {Capabilities.Is("kiln.gamedata")}");
        _out!.Write("session", new JsonObject
        {
            ["game"] = build, ["kiln"] = Kiln.Mods.RuntimeApiVersion, ["detours"] = detours, ["clock"] = "game",
            ["capabilities"] = new JsonObject
            {
                ["creature_control"] = Capabilities.Is("creature_control"), ["queries.path"] = Capabilities.Is("queries.path"),
                ["kiln.gamedata"] = Capabilities.Is("kiln.gamedata"), ["build.write"] = Capabilities.Is("build.write"),
            },
            ["plan"] = new JsonObject
            {
                ["creatures"] = new JsonArray(_plan.Creatures.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["scale"] = c.Scale }).ToArray()),
                ["horizontal"] = new JsonArray(_plan.Horizontal.Select(h => (JsonNode)h).ToArray()),
                ["vertical"] = new JsonArray(_plan.Vertical.Select(v => (JsonNode)v).ToArray()),
                ["native"] = _plan.Native, ["walkSeconds"] = _plan.WalkSeconds, ["nativeSeconds"] = _plan.NativeSeconds,
            },
        });
    }

    // ---- helpers ----

    internal static WorldPosition Pos(EntityId e) => World.Position(e) ?? new WorldPosition(double.NaN, double.NaN, double.NaN);

    internal static double Flat(WorldPosition a, WorldPosition b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));

    private static ulong StateWord(EntityId e) => World.ReadBytes(e, "Actor") is { Length: > Actor_State + 8 } a ? BitConverter.ToUInt64(a, Actor_State) : 0;

    internal static bool Has(EntityId e, int flag) => ((StateWord(e) >> flag) & 1) != 0;

    internal static bool Grounded(EntityId e) => Has(e, Flag_Grounded);

    internal static double? Number(GameDataEntry? t, string path)
    {
        if (t is null) return null;
        try { return t.GetNumber(path); }
        catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException or NotSupportedException or ArgumentException or FormatException) { return null; }
    }

    internal static bool? Present(GameDataEntry? t, string component)
    {
        if (t is null) return null;
        try { return t.GetJson(component) is not JsonObject o || o["absent"] is null; }
        catch (KeyNotFoundException) { return false; }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>Puts the body at <paramref name="at"/> (the lab's own setup step, not a pet's catch-up) and waits until it
    /// stands there, grounded.</summary>
    internal async Task<bool> Place(EntityId e, WorldPosition at)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await Teleport.To(e, at, new TeleportOptions { FindGround = false }); }
            catch (Exception x) when (x is InvalidOperationException or ArgumentException or NotSupportedException) { Info($"place: {x.Message}"); }
            var t0 = Now;
            while (Now - t0 < 3)
            {
                await Steps(1);
                var p = Pos(e);
                if (Now - t0 >= 0.5 && Grounded(e) && Flat(p, at) < 0.4 && Math.Abs(p.Y - at.Y) < 0.6) return true;
            }
        }
        return false;
    }

    internal async Task<EntityId> SpawnCreature(PetCreatureInfo info, WorldPosition at)
    {
        EntityId e;
        if (info.Variant is { } variant) e = await Creatures.Spawn(variant, at, 0, new CreatureSpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        else e = await Spawn.Entity(info.Template, at, 0, new SpawnOptions { Tag = Me, Flags = SpawnFlags.Transient });
        if (e.IsNone) return e;
        _spawned.Add(e);
        await Until(() => World.Exists(e) && double.IsFinite(Pos(e).X), 5);
        return e;
    }

    internal void Forget(EntityId e)
    {
        try { Spawn.Destroy(e); } catch (Exception) { }
        _spawned.Remove(e);
    }

    internal void Track(PetControl c) => _controls.Add(c);

    internal void Untrack(PetControl c)
    {
        try { c.Release(); } catch (Exception) { }
        _controls.Remove(c);
    }

    internal Out Evidence => _out!;
    internal Plan Plan => _plan;
    internal void CountQuery() => _queries++;
    internal void CountWalk() => _walks++;

    internal static Vector3 Zero => Vector3.Zero;
}
