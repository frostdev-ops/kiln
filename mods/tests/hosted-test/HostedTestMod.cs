// SPDX-License-Identifier: MIT
// A side "server" mod: on a dedicated (lab) server it loads at the first server step as always; on a client it loads
// with each hosted world and unloads when that world ends (docs/kiln/client.md, "Mods and sides"). Lines
// "hosted-test: PASS/FAIL/INFO ..." in kiln.log.
//  - lifecycle: OnLoad, ServerStarting/Started/Stopping and OnUnload with phase, thread and client state;
//  - Combat.GetHealth's dead state (the "dead while alive" report): a Vukah spawned away from players is killed and
//    revived with SetHealth/Heal, and Dead must read back what the game's death check will make of it in the same
//    step, while the game's own dead mark (Actor state bit 7) is shown to lag one step (the old Dead was that bit);
//  - on a hosting client: the host player's health while alive, deaths and respawns of players.
// Console/chat command `/hosted status|deadcheck|revive` (revive kills and heals the host player: only on request).
using System.Diagnostics;
using Kiln;

namespace HostedTest;

public sealed class HostedTestMod : Mod
{
    // Actor layout of svn 1287369 (sigdb combat/actor.state*, docs/re/combat.md "Death"): state word, override flag
    // byte, forced-on and forced-off words. Only for showing the game's own mark next to GetHealth.
    private const int StateOff = 0xBF8, FlagsOff = 0x1B1, OnOff = 0xBD0, OffOff = 0xBD8;

    private int _pass, _fail;
    private ulong _steps, _firstStep;
    private bool _serverRunning, _checking;
    private readonly Stopwatch _since = new();
    private readonly List<EntityId> _spawned = [];

    public override void OnLoad()
    {
        Info($"OnLoad: side {Mods.Side}, {ClientText()}, phase {Lifecycle.Phase}, tick thread {Game.IsTickThread}, " +
             $"mods loaded {Lifecycle.ModsLoaded}, combat {(Combat.Available ? "available" : "unavailable")}");
        Context.Tick.Pre += OnPre;
        // Players die and respawn only where players are: a client. (On a lab server these handlers would also widen
        // kiln.combat's listener interest, which combat-test checks.)
        if (Client.Available && Client.IsClient)
        {
            Events.On<EntityDeathEvent>(OnDeath);
            Events.On<PlayerRespawnEvent>(OnRespawn);
        }
        try
        {
            Commands.Register("hosted", OnCommand, usage: "status|deadcheck|revive", description: "hosted-test checks");
        }
        catch (Exception e)
        {
            Info($"command not registered: {e.Message}");
        }
        if (Lifecycle.IsServerRunning) Begin("loaded while the server runs");  // hot reload
    }

    public override void OnUnload()
    {
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch { }
        Info($"OnUnload: phase {Lifecycle.Phase}, tick thread {Game.IsTickThread}, {ClientText()}, {_steps} steps ticked; " +
             $"summary: {_pass} PASS, {_fail} FAIL");
    }

    private void OnPre(in TickInfo tick)
    {
        if (_firstStep == 0) _firstStep = tick.Step;
        _steps++;
    }

    [Subscribe]
    private void OnStarting(ServerStartingEvent e) =>
        Info($"ServerStarting at step {e.Step}: phase {Lifecycle.Phase}, tick thread {Game.IsTickThread}, {ClientText()}");

    [Subscribe]
    private void OnStarted(ServerStartedEvent e)
    {
        Info($"ServerStarted at step {e.Step} ({e.Source}): {ClientText()}");
        Begin(e.Source);
    }

    [Subscribe]
    private void OnStopping(ServerStoppingEvent e)
    {
        _serverRunning = false;
        var rate = _since.Elapsed.TotalSeconds > 0 ? _steps / _since.Elapsed.TotalSeconds : 0;
        Info($"ServerStopping ({e.Reason}) at step {e.Step}: tick thread {Game.IsTickThread}, {_steps} steps ticked " +
             $"({rate:F1}/s over {_since.Elapsed.TotalSeconds:F0} s), {ClientText()}");
    }

    private void OnDeath(EntityDeathEvent e)
    {
        if (!e.IsPlayer) return;
        var h = Combat.GetHealth(e.Victim);
        Info($"player {e.Victim} died (killer {e.Killer}, origin {e.Origin}); health now {h}");
        Check(h is { Dead: true }, $"a dead player reads Dead: {h}");
    }

    private void OnRespawn(PlayerRespawnEvent e)
    {
        var h = Combat.GetHealth(e.Player);
        Info($"{(e.Player.IsPlayer ? "player" : "entity")} {e.Player} alive again: health {e.Health}/{e.HealthMax}, GetHealth {h}, at {e.Position}");
        Check(h is { Dead: false, Current: > 0 }, $"alive again reads alive: {h}");
    }

    private void Begin(string why)
    {
        _serverRunning = true;
        _since.Restart();
        Check(Game.IsTickThread, $"server work starts on the tick thread ({why})");
        if (Client.Available && Client.IsClient)
            Check(Client.IsHosting, $"a server mod runs on a client only while it hosts: {ClientText()}");
        _ = Guard(Client.Available && Client.IsClient ? HostChecks() : LabChecks());
    }

    private async Task Guard(Task t)
    {
        try
        {
            await t;
        }
        catch (Exception e)
        {
            Fail($"checks threw: {e}");
        }
    }

    // ---- dedicated server (lab): player-free ----
    private async Task LabChecks()
    {
        var sw = Stopwatch.StartNew();
        while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
        await Steps(50);
        var anchor = FindAnchor();
        if (!Check(anchor is not null, $"anchor for the dead-state check: {anchor}")) return;
        await DeadCheck(new WorldPosition(anchor!.Value.X + 44, anchor.Value.Y + 0.5, anchor.Value.Z - 4));
        Info($"summary: {_pass} PASS, {_fail} FAIL");
    }

    // ---- hosting client: the host player ----
    private async Task HostChecks()
    {
        var sw = Stopwatch.StartNew();
        EntityId me = EntityId.None;
        while (_serverRunning && sw.Elapsed.TotalSeconds < 300)
        {
            if (Client.HasLocalPlayer && Combat.GetHealth(Client.LocalPlayer) is { HasActor: true })
            {
                me = Client.LocalPlayer;
                break;
            }
            await Steps(30);
        }
        if (!Check(!me.IsNone, $"the host player appeared in the server world after {sw.Elapsed.TotalSeconds:F0} s: {me}")) return;
        var h = Combat.GetHealth(me);
        Check(h is { Current: > 0, Dead: false, HasActor: true }, $"the host player alive: GetHealth({me}) = {h}, game mark {Mark(me)}");
        // every 30 s while hosting: GetHealth agrees with the game's own mark whenever the step is settled
        for (var n = 0; _serverRunning; n++)
        {
            await Steps(1800);
            if (!_serverRunning || !Client.HasLocalPlayer) break;
            var p = Combat.GetHealth(me);
            Info($"host player {me}: {p}, game mark {Mark(me)}, {ClientText()}");
            if (p is { Current: > 0 }) Check(p is { Dead: false }, $"alive player reads alive ({n})");
        }
    }

    private void OnCommand(CommandContext ctx)
    {
        var what = ctx.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "status";
        var me = ctx.Source.IsPlayer ? ctx.Source.Player : Client.Available && Client.HasLocalPlayer ? Client.LocalPlayer : EntityId.None;
        switch (what)
        {
            case "status":
                ctx.Reply($"hosted-test: {ClientText()}, phase {Lifecycle.Phase}, {_steps} steps, player {me}: " +
                          $"{(me.IsNone ? "-" : $"{Combat.GetHealth(me)} mark {Mark(me)}")}; {_pass} PASS, {_fail} FAIL");
                break;
            case "deadcheck":
            {
                // a Vukah 60 m from the player (or the anchor), killed and revived; it is removed at the end
                var at = me.IsNone ? FindAnchor() : World.Position(me);
                if (at is null) { ctx.Reply("no position"); break; }
                if (_checking) { ctx.Reply("already running"); break; }
                ctx.Reply($"dead-state check 60 m from {at}");
                _ = Guard(DeadCheck(new WorldPosition(at.Value.X + 60, at.Value.Y + 1, at.Value.Z)));
                break;
            }
            case "revive":
            {
                // the session-7 case: a dead host player healed back (kills the player for a moment)
                if (me.IsNone) { ctx.Reply("no player"); break; }
                ctx.Reply($"killing and reviving {me}");
                _ = Guard(Revive(me));
                break;
            }
            default:
                ctx.Reply("hosted status|deadcheck|revive");
                break;
        }
        Info($"/hosted {ctx.ArgText} -> done");
    }

    /// <summary>Kill (SetHealth 0) and revive (SetHealth max, then Heal) a fresh Vukah: GetHealth's Dead against the
    /// game's own mark in the same step and after the game's death check ran.</summary>
    private async Task DeadCheck(WorldPosition at)
    {
        _checking = true;
        try
        {
            var v = await Spawn.Entity("Enemy_Vukah_Melee", at, 0, new SpawnOptions { Tag = "hosted-test", Flags = SpawnFlags.Transient });
            _spawned.Add(v);
            await Steps(4);
            var h0 = Combat.GetHealth(v);
            if (!Check(h0 is { Current: > 0, Max: > 0, Dead: false, HasActor: true }, $"Vukah {v} at {at}: {h0}, mark {Mark(v)}")) return;
            var max = h0!.Value.Max;

            Combat.SetHealth(v, 0);
            var h1 = Combat.GetHealth(v);
            var m1 = Mark(v);
            Check(h1 is { Current: 0, Dead: true }, $"SetHealth(0) reads back dead (dying) in the same step: {h1} (game mark {m1})");
            var n1 = await StepsUntil(() => Mark(v) is true, 20);
            Check(Mark(v) is true && Combat.GetHealth(v) is { Dead: true }, $"the game marks it dead {n1} step(s) later: {Combat.GetHealth(v)}");

            Combat.SetHealth(v, max);
            var h2 = Combat.GetHealth(v);
            var m2 = Mark(v);
            Check(h2 is { Dead: false } && h2.Value.Current == max, $"a revive with SetHealth(max) reads back alive in the same step: {h2}");
            Check(m2 is true, $"the game's own mark still says dead in that step (Dead used to report this bit): {m2}");
            var n2 = await StepsUntil(() => Mark(v) is false, 20);
            Check(Mark(v) is false && Combat.GetHealth(v) is { Dead: false }, $"the game clears its mark {n2} step(s) later: {Combat.GetHealth(v)}");

            // the session-7 case on a creature: dead, then Heal (the old reply was "health 413/413 dead")
            Combat.SetHealth(v, 0);
            await StepsUntil(() => Mark(v) is true, 20);
            var healed = Combat.Heal(v, 1000);
            var h3 = Combat.GetHealth(v);
            Check(healed == max && h3 is { Dead: false } && h3.Value.Current == max, $"Heal of a dead entity: +{healed}, reads {h3} (game mark {Mark(v)})");
            await StepsUntil(() => Mark(v) is false, 20);
            Check(Combat.GetHealth(v) is { Dead: false } && Mark(v) is false, $"and stays alive once the game re-checked: {Combat.GetHealth(v)}");

            Spawn.Destroy(v);
            _spawned.Remove(v);
        }
        finally
        {
            _checking = false;
        }
        Info($"dead-state check done: {_pass} PASS, {_fail} FAIL");
    }

    private async Task Revive(EntityId me)
    {
        var max = Combat.GetHealth(me)?.Max ?? 0;
        Combat.SetHealth(me, 0);
        Info($"revive: {me} set to 0: {Combat.GetHealth(me)}, mark {Mark(me)}");
        var n = await StepsUntil(() => Mark(me) is true, 60);
        Info($"revive: the game marked {me} dead after {n} step(s): {Combat.GetHealth(me)}");
        var healed = Combat.Heal(me, 1000);
        var h = Combat.GetHealth(me);
        Check(h is { Dead: false } && h.Value.Current == max, $"revive: Heal(+{healed}) of the dead host player reads back alive: {h} (game mark {Mark(me)})");
        var m = await StepsUntil(() => Mark(me) is false, 60);
        Check(Combat.GetHealth(me) is { Dead: false, Current: > 0 }, $"revive: the game cleared its mark after {m} step(s): {Combat.GetHealth(me)}");
    }

    /// <summary>The game's dead mark: Actor state 7 through the game's state test (null without an Actor or when the
    /// layout does not fit).</summary>
    private static bool? Mark(EntityId e)
    {
        var a = World.ReadBytes(e, "Actor");
        if (a is null || a.Length < StateOff + 8) return null;
        var state = BitConverter.ToUInt64(a, StateOff);
        if ((a[FlagsOff] & 1) != 0) state = (state | BitConverter.ToUInt64(a, OnOff)) & ~BitConverter.ToUInt64(a, OffOff);
        return (state & 0x80) != 0;
    }

    private async Task<int> StepsUntil(Func<bool> done, int max)
    {
        for (var i = 0; i < max; i++)
        {
            if (done()) return i;
            await Context.Scheduler.NextTick();
        }
        return done() ? max : -1;
    }

    private async Task Steps(int n)
    {
        for (var i = 0; i < n; ++i) await Context.Scheduler.NextTick();
    }

    private static WorldPosition? FindAnchor()
    {
        foreach (var filter in new[] { "FlameAltar", "Altar", "Townsfolk" })
            foreach (var id in Spawn.FindByName(filter, max: 4))
                if (Spawn.Inspect(id) is { } info)
                    return info.Position;
        return null;
    }

    private static string ClientText() =>
        !Client.Available ? "no kiln.client" : !Client.IsClient ? "dedicated server" :
        $"client {Client.State}, server world session {Client.ServerWorldSession}, local player {(Client.HasLocalPlayer ? Client.LocalPlayer.ToString() : "-")}";

    private bool Check(bool ok, string what)
    {
        if (ok) { _pass++; Log.Info($"hosted-test: PASS {what}"); }
        else { _fail++; Log.Error($"hosted-test: FAIL {what}"); }
        return ok;
    }

    private void Fail(string s) { _fail++; Log.Error($"hosted-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"hosted-test: INFO {s}");
}
