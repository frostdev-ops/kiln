// SPDX-License-Identifier: MIT
using System.Numerics;
using Kiln;

namespace VfxTest;

/// <summary>
/// kiln.vfx / Kiln.Vfx test (docs/kiln/services/vfx.md). Lines "vfx-test: PASS|FAIL|INFO ..." in kiln.log.
/// <list type="bullet">
/// <item>Dedicated server: no kiln.vfx, calls relay and don't throw, game names resolve from the server's templates. Run
/// <c>/vfxtest relay</c> from the console folder with fixtures/relay_client.py connected: it checks every message byte by byte.</item>
/// <item>Simulated game (kiln/config/vfx.json {"sim": true}, a lab): the whole path from Kiln.Vfx through the C table to the door of the
/// game: pending, playing, unknown GUIDs, both guards, attach, parameters, stops, the relay's receive side by loopback.</item>
/// <item>Real client: nothing on screen by itself. Passive checks once a world runs; each case that shows something is on a key,
/// docs/kiln/services/vfx.md "Manual test".</item>
/// </list>
/// Keys, and the same cases as <c>/vfxtest 1|2|3|3b|4|4b</c>: Ctrl+1 game VFX 5 m ahead. Ctrl+2 a GUID nobody registered, through Kiln's check and both
/// guards. Ctrl+3 a flame on the right hand for 5 s. Ctrl+Shift+3 a recoloured game VFX with parameters, then a loop stopped at once. Ctrl+4 the
/// red burst, when the VFX builder has made it. Ctrl+Shift+4 the same on a game module, to tell a data fault from a shader fault (docs/re/vfx.md 3.7).
/// </summary>
public sealed class VfxTestMod : Mod
{
    private const string BurstName = "Explosion_Player_Spell_Fireball_1";
    private const string FlameName = "Prop_Cold_Heights_Torch_Steel_Wall_On";  // a torch's flame: a loop
    private const string ColourName = "Base_Impact_Explosion_VFX";             // exposes radius, lifetime, color
    private const string SequenceName = "Player_Teleport_Cancelable";          // a sequence: its first VFX event

    private static readonly VfxId Burst = VfxId.Game(BurstName);
    private static readonly VfxId Flame = VfxId.Game(FlameName);
    private static readonly VfxId Colour = VfxId.Game(ColourName);
    private static readonly Guid Unknown = new("deadbeef-0bad-4bad-8bad-0000deadbeef");  // nobody registers this
    private static readonly Guid SimKnown = new("5f5f5f5f-0000-4000-8000-000000000001");   // kill time 10 s
    private static readonly Guid SimShort = new("5f5f5f5f-0000-4000-8000-000000000002");   // duration 2 + lifetime 1.5
    private static readonly EntityId SimEntity = new(0x70000001), SimEntityGoes = new(0x70000002);

    private int _pass, _fail;
    private uint _checkedSession;

    public override void OnLoad()
    {
        var st = Vfx.Status();
        Info($"OnLoad: side {Mods.Side}, {(Client.Available && Client.IsClient ? $"client {Client.State}" : "dedicated server")}, kiln.vfx {(st.Native ? "present" : "absent")}{(st.Simulated ? " (SIMULATED)" : "")}, relay {(st.Relay ? "open" : "off")}");
        Commands.Register("vfxtest", OnCommand, usage: "status|relay|server|sim|1|2|3|3b|4|4b|5|5b|life <seconds> [name]|name <name>", description: "kiln.vfx test");

        var sizes = VfxTestSeams.NativeSizes;
        Check(sizes == (128, 88, 64, 112), $"the managed mirror has the C header's sizes: {sizes}");
        Check(VfxId.Named("vfx-test", "red_burst").Guid == new Guid("c25f750b-0ae0-5f57-be4d-720b1c93cf5c"),
            "VfxId.Named follows UUIDv5(<mod>:vfx:<name>) (the value from Python's uuid5)");
        Check(VfxId.None.IsNone && VfxId.Of(Unknown).Guid == Unknown && !VfxId.Of(Unknown).IsNone, "VfxId.None and VfxId.Of");
        Check(VfxId.Game(Unknown.ToString()).Guid == Unknown, "VfxId.Game takes a GUID's text");
        Check(VfxHandle.None.IsNone && new VfxHandle(0x8000_0001).IsRemote && !new VfxHandle(1).IsRemote, "handles: none, remote, local");

        if (st.Simulated)
        {
            Info("simulated game: the suite starts when the server world runs");
        }
        else if (Client.Available && Client.IsClient)
        {
            Check(st.Native && st.Guard, "kiln.vfx runs on the client with the guard on");
            Check(Vfx.Available, "Vfx.Available: signatures resolved, play reaches the game");
            Client.Tick += OnClientTick;
            KeyBindings.Register("vfx-test.burst", "Ctrl+1", () => Client.Post(CaseBurst), "vfx-test 1: a game VFX 5 m ahead");
            KeyBindings.Register("vfx-test.guard", "Ctrl+2", () => Client.Post(CaseGuard), "vfx-test 2: a GUID nobody registered");
            KeyBindings.Register("vfx-test.hand", "Ctrl+3", () => Client.Post(CaseHand), "vfx-test 3: a flame on the right hand for 5 s");
            KeyBindings.Register("vfx-test.params", "Ctrl+Shift+3", () => Client.Post(CaseParams), "vfx-test 3b: parameters and an immediate stop");
            KeyBindings.Register("vfx-test.red", "Ctrl+4", () => Client.Post(CaseRed), "vfx-test 4: the red burst (built from vfx/)");
            KeyBindings.Register("vfx-test.red-game", "Ctrl+Shift+4", () => Client.Post(CaseRedGameModule), "vfx-test 4b: the red burst on a game module");
            KeyBindings.Register("vfx-test.soak", "Ctrl+5", () => Client.Post(CaseSoak), "vfx-test 5: about 2,000 sprites for 60 s");
            KeyBindings.Register("vfx-test.play-params", "Ctrl+Shift+5", () => Client.Post(CasePlayParams), "vfx-test 5b: the red burst, blue, twice the size, three times as many");
        }
        else
        {
            Info("dedicated server: the server part runs at ServerStarted (relay checks)");
        }
        if (Lifecycle.IsServerRunning) Context.Scheduler.Post(ServerStart);
    }

    public override void OnUnload() => Info($"OnUnload: summary: {_pass} PASS, {_fail} FAIL");

    [Subscribe]
    private void OnServerStarted(ServerStartedEvent e) => ServerStart();

    private void ServerStart()
    {
        if (Vfx.Status().Simulated) Start(SimSuite);
        else if (!(Client.Available && Client.IsClient)) ServerPart();
    }

    // ---- dedicated server (tick thread) ----

    private void ServerPart()
    {
        var st = Vfx.Status();
        var listeners = Network.Available ? Network.Peers.Count(p => p.State == NetPeerState.Ready && p.HasChannel("kiln:vfx")) : 0;
        Info($"server part: tick thread {Game.IsTickThread}, Vfx.Available {Vfx.Available}, listeners {listeners}");
        Check(!st.Native, "dedicated server: no kiln.vfx (the server has no VFX)");
        Check(st.Relay, "the relay channel kiln:vfx is open");
        Check(Vfx.Available == listeners > 0, $"dedicated server: Vfx.Available ({Vfx.Available}) is true exactly when a Kiln client has kiln:vfx ({listeners})");
        Check(Vfx.State(new VfxHandle(1)) == VfxState.None && Vfx.Info(new VfxHandle(1)) is null && !Vfx.IsKnown(Burst), "no kiln.vfx: state, info and is-known read as nothing, without a fault");

        // names resolve from the server's own templates: its data names the same VFX the client has
        Check(Burst.Guid != Guid.Empty, $"VfxId.Game(\"{BurstName}\") resolves: {Burst.Guid}");
        Check(Flame.Guid != Guid.Empty, $"VfxId.Game(\"{FlameName}\") resolves: {Flame.Guid}");
        Check(Colour.Guid != Guid.Empty, $"VfxId.Game(\"{ColourName}\") resolves: {Colour.Guid}");
        Check(VfxId.Game(SequenceName).Guid == new Guid("d55cba5e-fd51-4095-9c74-685e64328322"), $"VfxId.Game(\"{SequenceName}\"), a sequence, resolves to its first VFX event (the GUID tools/kfc reads in the client's data)");
        var noName = VfxId.Game("No_Such_Template_Anywhere");
        Check(noName.Guid == Guid.Empty && !noName.IsNone, "a name that is nothing resolves to no GUID");
        Check(Vfx.PlayAt(noName, new WorldPosition(0, 100, 0)).IsNone, "playing a name that is nothing returns none, no throw");
        Start(GameNamesOffTheThread);

        var h = Vfx.PlayAt(Burst, new WorldPosition(0, 100, 0));
        Check(h.IsRemote, $"server-side PlayAt returns a remote handle: {h}");
        Vfx.Set(h, "radius", 2f);
        Vfx.Stop(h);
        Check(true, "server-side Set and Stop relay without errors");
        var u = Vfx.PlayAt(VfxId.Of(Unknown), new WorldPosition(0, 100, 0));
        Check(u.IsRemote, "the server relays a GUID it can't judge (it has no VFX): the client's guard decides");
        Check(Vfx.PlayFor(new EntityId(123456), Burst, new WorldPosition(0, 100, 0)).IsRemote, "PlayFor a player who isn't there: no throw");

        var some = World.Find("CurrentTransform").FirstOrDefault();
        if (!some.IsNone)
        {
            var on = Vfx.PlayOn(Burst, some, Slot.HandR, new Offset(new Vector3(0, 1, 0)));
            Check(on.IsRemote, $"server-side PlayOn entity {some}: {on}");
            Vfx.Stop(on, immediate: true);
        }
        var threw = false;
        try { Vfx.PlayOn(Burst, EntityId.None); } catch (ArgumentException) { threw = true; }
        Check(threw, "PlayOn with no entity is an argument error");
        Info($"server part done: summary: {_pass} PASS, {_fail} FAIL");
    }

    // kiln.gamedata answers on the world's thread only. A name not looked up yet, asked from elsewhere, is looked up at the next tick.
    private async Task GameNamesOffTheThread()
    {
        const string fresh = "Prop_Torch01_Wall_Range";
        var id = VfxId.Game(fresh);
        var off = await Task.Run(() => id.Guid);
        Check(off == Guid.Empty, "a game name asked from a thread-pool thread: not known yet");
        Check(await Until(() => Task.Run(() => id.Guid).Result != Guid.Empty), $"and known after the next tick, from that thread as well: {id.Guid}");
    }

    // The fixed messages fixtures/relay_client.py compares byte by byte. A lab peer has no player, so radius -1: every Kiln client.
    private void RelayFixtures()
    {
        var parameters = new VfxParams().Set("radius", 2.5f).Set("targetPosition", new Vector3(1, 2, 3)).Set("color", new Color(10, 20, 30, 40)).Set("count", 7);
        var at = Vfx.PlayAt(VfxId.Of(Unknown), new WorldPosition(1.5, 2.5, 3.5), new Quaternion(0, 0, 0, 1), 2f, parameters, TimeSpan.FromSeconds(3), radius: -1);
        var some = World.Find("CurrentTransform").FirstOrDefault();
        var on = Vfx.PlayOn(VfxId.Of(Unknown), some, Slot.HandR, new Offset(new Vector3(0.1f, 0.2f, 0.3f), new Vector3(1, 2, 3), new Quaternion(0, 1, 0, 0)), null, Vfx.UntilStopped, radius: -1);
        Vfx.Set(at, "radius", 9f);
        Vfx.Stop(at, immediate: true);
        Vfx.Stop(on);
        Info($"relay fixtures sent: {at}, {on} on entity {some}; Vfx.Available {Vfx.Available}, sent {Vfx.Status().RelaySent}");
    }

    // ---- simulated game ----

    private async Task SimSuite()
    {
        var st = Vfx.Status();
        Check(st.Native && st.Guard && st.Simulated && st.SystemReady && st.Registered == 2, $"simulated: status {st}");
        var at = new WorldPosition(10, 20, 30);

        var h = Vfx.PlayAt(VfxId.Of(SimKnown), at);
        Check(!h.IsNone && !h.IsRemote && Vfx.State(h) == VfxState.Pending, $"a handle at once, pending: {h}");
        Check(await Until(() => Vfx.State(h) == VfxState.Playing), "the next update starts it");
        var i = Vfx.Info(h)!;
        Check(i.FxHandle != 0 && i.Instance != 0 && i.Position == at, $"playing: {i}");
        Check(i.Lifetime == TimeSpan.FromSeconds(10), "a one-shot takes its length from the resource: the kill time, 10 s");
        Vfx.Stop(h);
        Check(Vfx.State(h) == VfxState.Stopped, "Stop reads stopped at once");
        await Until(() => Vfx.Info(h)?.Reason == VfxEndReason.Stopped);
        Check(Vfx.Info(h)?.Reason == VfxEndReason.Stopped, "and ends at the update");

        var s = Vfx.PlayAt(VfxId.Of(SimShort), at, null, 1f, null);
        await Until(() => Vfx.State(s) == VfxState.Playing);
        Check(Vfx.Info(s)!.Lifetime == TimeSpan.FromSeconds(3.5), "a one-shot with duration and lifetime parameters: 3.5 s");
        Vfx.Stop(s, immediate: true);

        var l = Vfx.PlayAt(VfxId.Of(SimKnown), at, lifetime: TimeSpan.FromMilliseconds(200));
        Check(await Until(() => Vfx.State(l) == VfxState.Playing), "a given lifetime plays");
        Check(await Until(() => Vfx.Info(l)?.Reason == VfxEndReason.Finished, 5000), "and ends by itself: finished");

        // the unknown GUID, three ways
        var refused = Vfx.Status().Refused;
        var u = Vfx.PlayAt(VfxId.Of(Unknown), at);
        Check(await Until(() => Vfx.State(u) == VfxState.Failed), "an unknown GUID fails");
        Check(Vfx.Info(u)?.Reason == VfxEndReason.UnknownVfx && Vfx.Info(u)?.FxHandle == 0, "Kiln's check: unknown, and the game was not called");
        Check(Vfx.Status().Refused == refused, "Kiln's own check is no guard refusal");
        var g1 = VfxTestSeams.GuardThroughFxController(Unknown, at);
        Check(await Until(() => Vfx.Status().Refused == refused + 1), "the guard at FxStart refuses it, counted");
        Check(await Until(() => Vfx.Info(g1)?.Reason == VfxEndReason.Finished), "and the emptied entry is gone at the next update");
        var g2 = VfxTestSeams.GuardThroughCreateInstance(Unknown);
        Check(await Until(() => Vfx.Status().Refused == refused + 2), "the guard on VfxCreateInstance refuses it, counted");
        Check(await Until(() => Vfx.State(g2) == VfxState.Failed && Vfx.Info(g2)?.Reason == VfxEndReason.UnknownVfx), "and the game's call returned 0");
        var g3 = VfxTestSeams.GuardThroughFxController(SimKnown, at);
        Check(await Until(() => Vfx.Info(g3)?.Reason == VfxEndReason.Refused), "the test seams refuse a GUID the game knows");
        Check(Vfx.IsKnown(VfxId.Of(SimKnown)) && !Vfx.IsKnown(VfxId.Of(Unknown)) && !Vfx.IsKnown(VfxId.None), "IsKnown");

        // other threads
        var off = await Task.Run(() => Vfx.PlayAt(VfxId.Of(SimKnown), at, lifetime: Vfx.UntilStopped));
        Check(await Until(() => Vfx.State(off) == VfxState.Playing), "a play from a thread-pool thread starts at the update");
        Check((Vfx.Info(off)?.Lifetime) == Vfx.UntilStopped, "until stopped: no end time");
        await Task.Run(() => Vfx.Stop(off));
        Check(Vfx.State(off) == VfxState.Stopped, "a stop from a thread-pool thread");

        // a game name from a thread that can't look it up: the handle at once, the GUID at the next tick. The simulated game lacks this VFX.
        var byName = await Task.Run(() => Vfx.PlayAt(VfxId.Game("Prop_Cold_Heights_Torch_Steel_Wall_On"), at));
        Check(!byName.IsNone, "a game name played from a thread-pool thread gets a handle at once");
        Check(await Until(() => Vfx.Info(byName)?.Reason == VfxEndReason.UnknownVfx), "the name resolved at the next tick, and the simulated game doesn't have it: unknown");
        var byNoName = await Task.Run(() => Vfx.PlayAt(VfxId.Game("No_Such_Template_Anywhere_2"), at));
        Check(await Until(() => Vfx.Info(byNoName)?.Reason == VfxEndReason.UnknownVfx), "a name that is nothing, from a thread-pool thread: fails, not stuck");

        // on an entity
        var on = Vfx.PlayOn(VfxId.Of(SimKnown), SimEntity, Slot.HandR, new Offset(new Vector3(0, 0.5f, 0)));
        Check(await Until(() => Vfx.State(on) == VfxState.Playing), "PlayOn plays");
        var oi = Vfx.Info(on)!;
        Check(oi.Attached && !oi.Unmatched && oi.Position == new WorldPosition(1, 2, 3) && oi.Lifetime == Vfx.UntilStopped, $"attached, at the entity, until stopped: {oi}");
        Vfx.Stop(on);
        var missing = Vfx.PlayOn(VfxId.Of(SimKnown), new EntityId(0x70000099));
        Check(await Until(() => Vfx.Info(missing)?.Reason == VfxEndReason.EntityGone), "PlayOn an entity that isn't there: failed, by name");
        var goes = Vfx.PlayOn(VfxId.Of(SimKnown), SimEntityGoes);
        Check(await Until(() => Vfx.State(goes) == VfxState.Playing), "PlayOn an entity that lives");
        Check(await Until(() => Vfx.Info(goes)?.Reason == VfxEndReason.EntityGone, 15000), "it ends when the entity goes");

        // parameters (the lines "vfx: sim: parameter ..." in kiln.log show what arrived)
        var ps = new VfxParams().Set("radius", 2.5f).Set("targetPosition", new Vector3(1, 2, 3)).Set("color", new Color(10, 20, 30, 40)).Set("count", 7);
        var p = Vfx.PlayAt(VfxId.Of(SimKnown), at, parameters: ps, lifetime: Vfx.UntilStopped);
        await Until(() => Vfx.State(p) == VfxState.Playing);
        await Context.Scheduler.NextTick();
        await Context.Scheduler.NextTick();
        Vfx.Set(p, "radius", 9f);
        await Context.Scheduler.NextTick();
        await Context.Scheduler.NextTick();
        Check(Vfx.State(p) == VfxState.Playing, "four parameters at the start and one later (see the lines above this one)");
        Vfx.Stop(p, immediate: true);

        // the relay's receive side, in this process: encode, decode, play on the simulated game
        var relayed = VfxTestSeams.LoopbackPlay(SimKnown, at, 2f, SimEntity, Slot.HandR, new Offset(new Vector3(1, 0, 0)), -1, ps);
        Check(await Until(() => Vfx.State(relayed) == VfxState.Playing), "a relayed PlayOn decodes and plays");
        var ri = Vfx.Info(relayed)!;
        Check(!ri.Attached && ri.Unmatched, "the entity stands far from the server's position: played there, not attached");
        var near = VfxTestSeams.LoopbackPlay(SimKnown, new WorldPosition(1, 2, 3), 1f, SimEntity, Slot.HandR, default, -1, null);
        Check(await Until(() => Vfx.State(near) == VfxState.Playing) && Vfx.Info(near)!.Attached, "the entity stands where the server said: attached");
        VfxTestSeams.LoopbackStop(relayed);
        VfxTestSeams.LoopbackStop(near);
        Check(await Until(() => Vfx.State(relayed) == VfxState.Stopped && Vfx.State(near) == VfxState.Stopped), "a relayed stop");

        // the mod's own handles
        var a = Vfx.PlayAt(VfxId.Of(SimKnown), at, lifetime: Vfx.UntilStopped);
        var b = Vfx.PlayAt(VfxId.Of(SimKnown), at, lifetime: Vfx.UntilStopped);
        await Until(() => Vfx.State(a) == VfxState.Playing && Vfx.State(b) == VfxState.Playing);
        Check(Vfx.StopAll() >= 2, "StopAll stops this mod's");
        Vfx.Preload(VfxId.Of(SimKnown));
        Vfx.Preload(VfxId.Of(Unknown));
        await Context.Scheduler.NextTick();
        await Context.Scheduler.NextTick();
        Check(Vfx.StatusJson().Contains("\"sim\":true"), "StatusJson");
        Info($"status {Vfx.Status()}");
        Info($"sim suite done: summary: {_pass} PASS, {_fail} FAIL");
    }

    // ---- real client ----

    private void OnClientTick(in TickInfo t)
    {
        if (Client.LocalPlayer.IsNone) return;
        var s = Client.ClientWorldSession;
        if (_checkedSession == s) return;
        _checkedSession = s;
        var st = Vfx.Status();
        Check(st.Controller || st.Updates > 0 || st.SystemReady, $"in a world: {st}");
        Check(st.Registered >= 1500, $"the VFX system's map reads: {st.Registered} registered of capacity {st.MapCapacity}");
        Check(!Vfx.IsKnown(VfxId.Of(Unknown)), $"{Unknown} is not registered");
        Start(GameNameInTheWorld);
        Info("the cases that show something are on keys: Ctrl+1, Ctrl+2, Ctrl+3, Ctrl+Shift+3, Ctrl+4, Ctrl+Shift+4");
    }

    // A game name resolves on the thread the game's data answers on, at its next tick: asked from the client's tick, it reads as no GUID until then.
    private async Task GameNameInTheWorld()
    {
        var t0 = Environment.TickCount64;
        var resolved = await Until(() => Burst.Guid != Guid.Empty, 15_000);
        Check(resolved && Vfx.IsKnown(Burst),
            resolved ? $"{BurstName} is {Burst.Guid} after {Environment.TickCount64 - t0} ms, and the game has it: {Vfx.IsKnown(Burst)}"
                     : $"{BurstName} found no GUID in 15 s (no template carries it and no sequence plays it on this build)");
    }

    private static WorldPosition? Ahead(float metres)
    {
        if (World.Transform(Client.LocalPlayer) is not { } t) return null;
        var fwd = Vector3.Transform(new Vector3(0, 0, 1), t.Rotation);
        return new WorldPosition(t.Position.X + fwd.X * metres, t.Position.Y + 1.0, t.Position.Z + fwd.Z * metres);
    }

    // World.Transform belongs to the world's tick thread; the cases run on the main thread, like a mod's key handler.
    private void WithPlaceAhead(float metres, string name, Action<WorldPosition> run) =>
        Context.Scheduler.Post(() =>
        {
            if (Ahead(metres) is not { } at) { Check(false, $"{name}: the local player has no position"); return; }
            Client.Post(() => run(at));
        });

    // Key 1: a game VFX 5 m ahead. It shows only on a screen; the lines say the game took it and let it go again.
    private void CaseBurst()
    {
        Info("case 1: a game VFX 5 m ahead");
        WithPlaceAhead(5, "case 1", at =>
        {
            var h = Vfx.PlayAt(Burst, at);
            var t0 = Environment.TickCount64;
            Start(async () =>
            {
                Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case 1: {BurstName} plays: {Vfx.Info(h)}");
                var life = Vfx.Info(h)?.Lifetime;
                Check(life is { TotalSeconds: >= 1 and <= 60 }, $"case 1: stopped by Kiln after {life} (from its resource)");
                Check(await Until(() => Vfx.State(h) == VfxState.Stopped, 90_000), $"case 1: it ended by itself after {(Environment.TickCount64 - t0) / 1000.0:F1} s: {Vfx.Info(h)?.Reason}");
            });
        });
    }

    // Key 2: the most important safety check. If a guard failed the game would abort here: the last line before it names which.
    private void CaseGuard()
    {
        var st = Vfx.Status();
        Info($"case 2: a GUID nobody registered, {Unknown}; {st.Refused} refused so far");
        WithPlaceAhead(5, "case 2", at => Start(async () =>
        {
            var u = Vfx.PlayAt(VfxId.Of(Unknown), at);
            Check(await Until(() => Vfx.State(u) == VfxState.Failed) && Vfx.Info(u)?.Reason == VfxEndReason.UnknownVfx, "case 2: Kiln's check refused it");

            Info("case 2: through the FxController, as data would: if the game aborts now, the guard at FxStart failed");
            var refused = Vfx.Status().Refused;
            var a = VfxTestSeams.GuardThroughFxController(Unknown, at);
            Check(await Until(() => Vfx.Status().Refused > refused), "case 2: the guard at FxStart refused it");
            Check(await Until(() => Vfx.Info(a)?.Reason == VfxEndReason.Finished), "case 2: the emptied entry is gone");

            Info("case 2: straight into RenderCreateVfx: if the game aborts now, the guard on VfxCreateInstance failed");
            refused = Vfx.Status().Refused;
            var b = VfxTestSeams.GuardThroughCreateInstance(Unknown);
            Check(await Until(() => Vfx.Status().Refused > refused) && await Until(() => Vfx.State(b) == VfxState.Failed), "case 2: the guard on VfxCreateInstance refused it");
            Info("case 2 done: the game is still running");
        }));
    }

    // Key 3: a flame on the right hand; walk and it walks with you.
    private void CaseHand()
    {
        Info("case 3: a torch flame on the right hand for 5 s");
        var h = Vfx.PlayOn(Flame, Client.LocalPlayer, Slot.HandR, new Offset(new Vector3(0, 0.1f, 0)));
        Start(async () =>
        {
            Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case 3: {FlameName} on the hand: {Vfx.Info(h)}");
            Check(Vfx.Info(h)?.Attached == true, "case 3: attached");
            await Task.Delay(5000);
            Vfx.Stop(h);
            Check(await Until(() => Vfx.Info(h)?.Reason == VfxEndReason.Stopped), "case 3: stopped after 5 s");
        });
    }

    // Key 3b: a parameter the VFX exposes, and an immediate stop.
    private void CaseParams()
    {
        Info("case 3b: blue, radius 4, then a loop stopped at once");
        WithPlaceAhead(6, "case 3b", at =>
        {
            var p = new VfxParams().Set("color", new Color(40, 120, 255)).Set("radius", 4f);
            var h = Vfx.PlayAt(Colour, at, parameters: p);
            Start(async () =>
            {
                Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case 3b: {ColourName} plays: {Vfx.Info(h)}");
                await Task.Delay(3000);
                var loop = Vfx.PlayAt(Flame, at, lifetime: Vfx.UntilStopped);
                await Until(() => Vfx.State(loop) == VfxState.Playing);
                await Task.Delay(3000);
                Vfx.Stop(loop, immediate: true);
                Check(await Until(() => Vfx.Info(loop)?.Reason == VfxEndReason.Stopped), "case 3b: the flame was stopped immediately: it should be gone at once");
            });
        });
    }

    // Key 4 and 4b: the same effect on Kiln's module and on a game module (blocks/vfx.json, the builder's job).
    private void CaseRed() => PlayBuilt("red_burst", "4", "Kiln's sprite module");
    private void CaseRedGameModule() => PlayBuilt("red_burst_game_module", "4b", "a game module: shows while 4 doesn't = the shader is at fault, not the data");

    // Key 5: a loop of ~2,000 sprites, stopped by Kiln after 60 s. What to watch is the frame rate and that the counts go back to 0.
    private void CaseSoak()
    {
        var id = VfxId.Named(Context, "soak");
        if (!Vfx.IsKnown(id))
        {
            Info("case 5: soak isn't registered in this game (mods/tests/vfx-test/blocks). Nothing to see");
            return;
        }
        WithPlaceAhead(8, "case 5", at =>
        {
            var h = Vfx.PlayAt(id, at, lifetime: Vfx.UntilStopped);
            Info("case 5: soak plays for 60 s; watch the frame rate");
            Start(async () =>
            {
                Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case 5: soak plays: {Vfx.Info(h)}");
                await Task.Delay(60_000);
                Vfx.Stop(h);
                Check(await Until(() => Vfx.State(h) == VfxState.Stopped), $"case 5: soak stopped after 60 s: {Vfx.Info(h)}");
            });
        });
    }

    // Key Shift+5: per-play parameters on a Kiln VFX (every one describes color, size, speed, brightness, count).
    private void CasePlayParams()
    {
        var id = VfxId.Named(Context, "red_burst");
        if (!Vfx.IsKnown(id))
        {
            Info("case 5b: red_burst isn't registered in this game. Nothing to see");
            return;
        }
        var p = new VfxParams().Set("color", new Color(40, 120, 255)).Set("size", 2f).Set("count", 3f);
        WithPlaceAhead(5, "case 5b", at =>
        {
            var h = Vfx.PlayAt(id, at, parameters: p);
            Start(async () => Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case 5b: red_burst with parameters plays: {Vfx.Info(h)}"));
        });
    }

    private void PlayBuilt(string name, string key, string what)
    {
        var id = VfxId.Named(Context, name);
        Info($"case {key}: {name} ({id.Guid}) on {what}");
        if (!Vfx.IsKnown(id))
        {
            Info($"case {key}: {name} isn't registered in this game (mods/tests/vfx-test/blocks). Nothing to see");
            return;
        }
        WithPlaceAhead(5, $"case {key}", at =>
        {
            var h = Vfx.PlayAt(id, at);
            Start(async () => Check(await Until(() => Vfx.State(h) == VfxState.Playing), $"case {key}: {name} plays: {Vfx.Info(h)}"));
        });
    }

    // ---- plumbing ----

    private void OnCommand(CommandContext ctx)
    {
        var arg = ctx.Args.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "status";
        switch (arg)
        {
            case "life":
                {
                    // how long the FxController holds an entry after its end time: the instance goes at most its kill time later
                    var seconds = ctx.Args.Count > 1 ? double.Parse(ctx.Args[1], System.Globalization.CultureInfo.InvariantCulture) : 1.0;
                    var which = ctx.Args.Count > 2 ? VfxId.Game(ctx.Args[2]) : Burst;
                    WithPlaceAhead(5, "life", at =>
                    {
                        var h = Vfx.PlayAt(which, at, lifetime: TimeSpan.FromSeconds(seconds));
                        var t0 = Environment.TickCount64;
                        Start(async () =>
                        {
                            await Until(() => Vfx.State(h) == VfxState.Playing);
                            Info($"life: {Vfx.Info(h)}");
                            await Until(() => Vfx.State(h) == VfxState.Stopped, 120_000);
                            Info($"life {seconds} s {which}: the entry was dropped after {(Environment.TickCount64 - t0) / 1000.0:F1} s: {Vfx.Info(h)?.Reason}");
                        });
                    });
                    break;
                }
            case "name":
                {
                    var n = ctx.Args.Count > 1 ? ctx.Args[1] : BurstName;
                    var g = VfxId.Game(n).Guid;
                    Info($"name {n} -> {g}, known {Vfx.IsKnown(VfxId.Of(g))}");
                    ctx.Reply(g.ToString());
                    break;
                }
            case "status":
                ctx.Reply($"{Vfx.Status()} ({_pass} PASS, {_fail} FAIL)");
                Info(Vfx.StatusJson());
                break;
            case "server":
                ServerPart();
                ctx.Reply("server part ran (see kiln.log)");
                break;
            case "relay":
                RelayFixtures();
                ctx.Reply("relay fixtures sent");
                break;
            case "sim":
                Start(SimSuite);
                ctx.Reply("sim suite started (see kiln.log)");
                break;
            case "1": Client.Post(CaseBurst); break;
            case "2": Client.Post(CaseGuard); break;
            case "3": Client.Post(CaseHand); break;
            case "3b": Client.Post(CaseParams); break;
            case "4": Client.Post(CaseRed); break;
            case "4b": Client.Post(CaseRedGameModule); break;
            case "5": Client.Post(CaseSoak); break;
            case "5b": Client.Post(CasePlayParams); break;
            default:
                ctx.Reply("usage: /vfxtest status|relay|server|sim|1|2|3|3b|4|4b|5|5b|life <seconds> [name]|name <name>");
                break;
        }
    }

    private void Start(Func<Task> suite) => _ = Run(suite);

    private async Task Run(Func<Task> suite)
    {
        try
        {
            await suite();
        }
        catch (Exception e)
        {
            Check(false, $"suite threw: {e}");
        }
    }

    private static async Task<bool> Until(Func<bool> done, int ms = 3000)
    {
        var t0 = Environment.TickCount64;
        while (Environment.TickCount64 - t0 < ms)
        {
            if (done()) return true;
            await Task.Delay(20);
        }
        return done();
    }

    private void Info(string s) => Log.Info($"vfx-test: INFO {s}");

    private bool Check(bool ok, string what)
    {
        if (ok)
        {
            _pass++;
            Log.Info($"vfx-test: PASS {what}");
        }
        else
        {
            _fail++;
            Log.Warn($"vfx-test: FAIL {what}");
        }
        return ok;
    }
}
