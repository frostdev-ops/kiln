// SPDX-License-Identifier: MIT
// kiln.abilities lab test, player-free. Prints "abilities-test: PASS ..." / "FAIL ..." / "INFO ..." lines to kiln.log.
// Everything it spawns stands 40+ m from the copied world's flame altar and is removed at the end.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Kiln;

namespace AbilitiesTest;
#pragma warning disable CS0618

public sealed class AbilitiesTestMod : Mod
{
    private const uint ExplosionProgram = 0x7a120ee8;  // 80a2448f-…, Explosion_Player_Spell_Fireball_1's program
    private const string Explosion = "Explosion_Player_Spell_Fireball_1";

    private int _pass, _fail;
    private readonly List<EntityId> _spawned = new();
    private readonly List<IDisposable> _handles = new();

    // event logs (tick thread)
    private readonly List<ImpactStartedEvent> _started = new();
    private readonly List<ImpactStartingEvent> _starting = new();
    private readonly List<ImpactEndedEvent> _ended = new();
    private readonly List<ImpactNativeEvent> _natives = new();
    private readonly List<ImpactSignalEvent> _signals = new();
    private readonly Dictionary<uint, (float? Noise, uint? Flags)> _startingSeen = new();
    private Func<ImpactStartingEvent, bool>? _onStarting;  // returns true = cancel
    private long _wrapCalls, _wrapSawProgram, _hookReadsOk, _hookCalls;

    public override void OnLoad()
    {
        RegisterSessionCommands();
        // The automatic suite runs on a dedicated (lab) server; in a client session only the /abt commands are used.
        // kiln/data/abilities-test/reload-only: no suite, just one of each registration (for `reload-test`).
        if (File.Exists(Path.Combine(Context.DataDirectory, "reload-only")))
        {
            _handles.Add(Abilities.Register(new AbilityDefinition { Id = "abilities-test:reload", PlayersOnly = false, OnCast = _ => Task.CompletedTask }));
            _handles.Add(Abilities.HoldPrograms(ExplosionProgram));
            _handles.Add(Abilities.Natives.Wrap("spawnEntity", (ref NativeCall c) => Interlocked.Increment(ref _wrapCalls)));
            Abilities.Natives.Register("Reload", 1, _ => 1);
            Info("reload-only: an ability, a hold filter, a wrap and a native registered");
        }
        else if (File.Exists(Path.Combine(Context.DataDirectory, "session-only")))
            Info("session-only (kiln/data/abilities-test/session-only): automatic suite skipped; /abt ... for the session");
        else if (Client.IsDedicatedServer) _ = Run();
        else Info("client: automatic suite skipped; chat commands /abt ... for the session");
    }

    // ---- manual checks for a client session (chat: /abt <what>) ----
    private bool _session;
    private void RegisterSessionCommands()
    {
        try
        {
            Commands.Register("abt", ctx =>
            {
                var what = ctx.Count > 0 ? ctx.Args[0].ToLowerInvariant() : "help";
                var p = ctx.Source.Player;
                if (!ctx.Source.IsPlayer)
                {
                    // the console (offline hosted sessions have no chat): act for the only player in the world
                    var players = World.Find("PlayerInput");
                    p = players.Count == 1 ? players[0] : EntityId.None;
                    if (p.IsNone && what != "status") { ctx.Reply($"players only ({players.Count} players)"); return; }
                }
                _session = true;
                switch (what)
                {
                    case "status": ctx.Reply($"{Abilities.Status}"); break;
                    case "mana":
                        ctx.Reply($"mana {Abilities.Attributes.Get(p, AbilityResource.Mana)}/{Abilities.Attributes.Max(p, AbilityResource.Mana)}, " +
                                  $"stamina {Abilities.Attributes.Get(p, AbilityResource.Stamina)}/{Abilities.Attributes.Max(p, AbilityResource.Stamina)}, " +
                                  $"special {Abilities.Attributes.Get(p, AbilityResource.SpecialAbility)}/{Abilities.Attributes.Max(p, AbilityResource.SpecialAbility)}, " +
                                  $"health {Abilities.Attributes.Get(p, AbilityResource.Health)}");
                        break;
                    case "tp":
                    {
                        // relative teleport (offline hosted sessions: get out of the base): abt tp <dx> <dz> [dy]
                        var pos = World.Position(p);
                        if (pos is null || ctx.Count < 3) { ctx.Reply("abt tp <dx> <dz> [dy]"); break; }
                        var dx = double.Parse(ctx.Args[1], System.Globalization.CultureInfo.InvariantCulture);
                        var dz = double.Parse(ctx.Args[2], System.Globalization.CultureInfo.InvariantCulture);
                        var dy = ctx.Count > 3 ? double.Parse(ctx.Args[3], System.Globalization.CultureInfo.InvariantCulture) : 0;
                        var to = new WorldPosition(pos.Value.X + dx, pos.Value.Y + dy, pos.Value.Z + dz);
                        _ = Teleport.To(p, to, new TeleportOptions { FindGround = true }).ContinueWith(t => Info($"session tp -> {t.Result}"));
                        ctx.Reply($"teleporting {p} from {pos} to {to}");
                        break;
                    }
                    case "seqof":
                    {
                        var e = ctx.Count > 1 ? new EntityId(uint.Parse(ctx.Args[1])) : p;
                        ctx.Reply($"{e}: sequence {Abilities.SequenceOf(e)}");
                        break;
                    }
                    case "seqx":
                    {
                        var guid = ctx.Count > 1 ? new Guid(ctx.Args[1]) : new Guid("deff43ba-530a-4bee-be84-9b3c781e9556");
                        var ok = Abilities.PlaySequence(p, guid, abortCurrent: false);
                        _ = WatchSeq(p);
                        ctx.Reply($"PlaySequence({p}, {guid}, ExternalTrigger): {ok}");
                        break;
                    }
                    case "where": ctx.Reply($"{p} at {World.Position(p)} yaw {World.Transform(p)?.YawDegrees:F0}"); break;
                    case "heal": Combat.Heal(p, 1000); ctx.Reply($"health {Combat.GetHealth(p)}"); break;
                    case "hurt": Combat.SetHealth(p, 250); ctx.Reply($"health {Combat.GetHealth(p)}"); _ = WatchHealth(p); break;
                    case "day": Time.Set(12); ctx.Reply("time 12:00"); break;
                    case "night": Time.Set(23); ctx.Reply("time 23:00"); break;
                    case "vukah":
                    {
                        var at = Ahead(p, 6);
                        var e = Spawn.Entity("Enemy_Vukah_Melee", at, 0, new SpawnOptions { Tag = "abilities-session", Flags = SpawnFlags.Transient }).Result;
                        ctx.Reply($"Vukah {e} at {at}");
                        break;
                    }
                    case "boom":
                    {
                        var tpl = ctx.Count > 1 ? ctx.Args[1] : Explosion;
                        var at = Ahead(p, 4);
                        var e = Abilities.RunImpact(tpl, at, new ImpactOptions { Root = p }).Result;
                        ctx.Reply($"{tpl} as {e} at {at} (root {p})");
                        break;
                    }
                    case "near":
                    {
                        var tpl = ctx.Count > 1 ? ctx.Args[1] : Explosion;
                        var me = World.Position(p);
                        var best = World.Find("NetworkEnemy").Select(x => (x, d: World.Position(x)?.DistanceTo(me ?? default) ?? 1e9))
                            .Where(t => t.d < 40).OrderBy(t => t.d).FirstOrDefault();
                        if (best.x.IsNone) { ctx.Reply("no enemy within 40 m"); break; }
                        // abt near [template] [fire factor]: a Damage1 config gives the explosion its own damage (else it
                        // takes the used item's damageSetup, and a Kiln impact has no used item)
                        var fire = ctx.Count > 2 ? float.Parse(ctx.Args[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                        var cfg = fire > 0 ? new ImpactConfigs { ["Damage1"] = DamageSetup.FireOnly(fire) } : null;
                        var e = Abilities.RunImpact(tpl, World.Position(best.x)!.Value, new ImpactOptions { Root = p, Configs = cfg }).Result;
                        ctx.Reply($"{tpl} as {e} on enemy {best.x} ({best.d:F1} m), health {Combat.GetHealth(best.x)}");
                        break;
                    }
                    case "seq":
                    {
                        var g = ctx.Count > 1 ? ctx.Args[1].ToLowerInvariant() switch
                        {
                            "fireball" => "deff43ba-530a-4bee-be84-9b3c781e9556",
                            "swing" => "a837f190-d163-4ef5-a48e-3ea7caac7296",
                            "doublejump" => "e7b5dbb5-0000-0000-0000-000000000000",
                            "greet" => "13fd798d-033a-494b-95e8-76fe817202b5",
                            "dance" => "f3e0726c-b527-4218-b5f4-7105953d7835",
                            "fists" => "06cb777d-8ee2-4e2c-bb2b-c5626cc01b9e",
                            "whirl" => "655b74b1-f739-4705-9b4e-b402b8ee4a9e",
                            "bow" => "ca6fdde3-8890-42d5-bf3f-1e216a87ceec",
                            var x => x,
                        } : "deff43ba-530a-4bee-be84-9b3c781e9556";
                        if (!Guid.TryParse(g, out var guid)) { ctx.Reply("seq <guid|fireball|swing|greet|dance|fists|whirl|bow> [nohold] [queue]"); break; }
                        // abt seq <what> [nohold] [queue]: HoldInputs off / ExternalTrigger (the emote trigger)
                        var opts = new SequenceOptions
                        {
                            HoldInputs = !ctx.Args.Skip(2).Contains("nohold"), AbortCurrent = !ctx.Args.Skip(2).Contains("queue"),
                        };
                        ctx.Reply($"PlaySequence({p}, {guid}, hold {opts.HoldInputs}, abort {opts.AbortCurrent}): {Abilities.PlaySequence(p, guid, opts)}; now 0x{Abilities.SequenceOf(p)?.Sequence:x8}");
                        _ = WatchSeq(p);
                        break;
                    }
                    case "buff":
                    {
                        var hp = Combat.GetHealth(p);
                        var e = Abilities.ApplyBuff(p, new Guid(ctx.Count > 1 ? ctx.Args[1] : "be0e7626-e95f-4a14-844a-2af52501698e"),
                            TimeSpan.FromSeconds(10), p).Result;
                        ctx.Reply($"buff applied by {e}; health now {hp}");
                        break;
                    }
                    default: ctx.Reply("abt status|mana|day|night|vukah|boom [template]|near [template]|seq <guid|fireball|swing>|buff [guid]"); break;
                }
                Info($"session /abt {ctx.ArgText} by {p}");
            }, usage: "<what>", description: "kiln.abilities session checks");
        }
        catch (Exception e) { Info($"commands not registered: {e.Message}"); }
    }

    private async Task WatchHealth(EntityId e)
    {
        for (var i = 0; i <= 1200; i++)
        {
            await Context.Scheduler.NextTick();
            if (i % 120 == 0) Info($"SESSION health of {e} after {i} steps: {Combat.GetHealth(e)}");
        }
    }

    private async Task WatchSeq(EntityId e)
    {
        for (var i = 0; i < 80; i++)
        {
            await Context.Scheduler.NextTick();
            if (i % 5 != 0) continue;
            var na = World.ReadBytes(e, "NetworkActor");
            var net = na is null ? "no NetworkActor" : $"net seq 0x{BitConverter.ToUInt32(na, 0):x8} trigger {BitConverter.ToUInt16(na, 0xcc)} states 0x{BitConverter.ToUInt64(na, 0x28):x}";
            Info($"SESSION seq of {e} after {i} steps: {Abilities.SequenceOf(e)}; {net}; mana {Abilities.Attributes.Get(e, AbilityResource.Mana)}");
        }
    }

    private static WorldPosition Ahead(EntityId e, double m)
    {
        var t = World.Transform(e);
        if (t is null) return default;
        var yaw = t.Value.YawDegrees * Math.PI / 180;
        return t.Value.Position with { X = t.Value.Position.X + Math.Sin(yaw) * m, Z = t.Value.Position.Z + Math.Cos(yaw) * m };
    }

    [Subscribe]
    private void OnDamageSession(ImpactDamageEvent e)
    {
        if (_session && (e.Attacker.IsPlayer || e.Target.IsPlayer))
            Info($"SESSION applyDamage {e.Impact}: attacker {e.Attacker} target {e.Target} AttackMod {e.AttackMod} result {e.Result} blocked {e.Blocked} damage total {e.Damage.Total}");
    }

    [Subscribe]
    private void OnHurtSession(EntityDamagedEvent e)
    {
        if (_session) Info($"SESSION damaged {e.Target} by {e.Attacker} src {e.Source}: {e.HealthBefore}->{e.HealthAfter}/{e.HealthMax} flags {e.Flags}");
    }

    [Subscribe]
    private void OnCastSession(AbilityCastedEvent e)
    {
        if (_session || Client.IsClient) Info($"SESSION cast {e.Cast.Ability.Id} by {e.Cast.Caster} ({e.Cast.Origin}), paid {e.Paid}");
    }

    public override void OnUnload()
    {
        foreach (var h in _handles) try { h.Dispose(); } catch { }
        foreach (var e in _spawned) try { if (Spawn.Exists(e)) Spawn.Destroy(e); } catch { }
    }

    [Subscribe] private void OnStarted(ImpactStartedEvent e) => _started.Add(e);
    [Subscribe] private void OnEnded(ImpactEndedEvent e) => _ended.Add(e);
    [Subscribe] private void OnNative(ImpactNativeEvent e) => _natives.Add(e);
    [Subscribe] private void OnSignal(ImpactSignalEvent e) => _signals.Add(e);

    [Subscribe]
    private void OnStarting(ImpactStartingEvent e)
    {
        _starting.Add(e);
        _startingSeen[e.Impact.ImpactId] = (e.GetFloat("NoiseRange"), e.Instance.Flags);
        if (_onStarting?.Invoke(e) == true) e.Cancel();
    }

    private async Task Steps(int n) { for (var i = 0; i < n; i++) await Context.Scheduler.NextTick(); }

    private async Task Run()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!Spawn.WorldRunning && sw.Elapsed.TotalSeconds < 240) await Context.Scheduler.NextTick();
            await Steps(30);
            while ((Abilities.Status?.ProgramsTotal ?? 0) < 571 && sw.Elapsed.TotalSeconds < 300) await Steps(10);
            Info($"ImpactRegistry ready after {sw.Elapsed.TotalSeconds:F1} s: {Abilities.Status}");
            _handles.Add(Abilities.HoldPrograms(ExplosionProgram));
            await TestStatus();
            var anchor = FindAnchor();
            if (anchor is not { } a) { Fail("no anchor entity (flame altar / villager) in the world"); return; }
            WorldPosition At(double dx, double dz) => new(a.X + 40 + dx, a.Y + 0.5, a.Z + dz);

            await TestHooksFix();
            var (casterA, casterB) = await TestSpawnComponents(At(0, 0), At(6, 0));
            await TestRunImpact(casterA, At(0, 8));
            await TestHoldAndCancel(At(0, 16));
            await TestEnded();
            await TestNativesObserveAndWrap(casterA, At(0, 24));
            await TestProgramsAndManagedNative(At(0, 32));
            await TestVerifier();
            await TestAttributes(casterA);
            await TestBuff(casterB);
            await TestRemoveBuff(casterB, At(0, 44));
            await TestAbilities(casterA, casterB);
            await TestSequence(casterA);
            await TestPlayerSequences(At(0, 40));
        }
        catch (Exception e)
        {
            Fail($"threw: {e}");
        }
        finally
        {
            foreach (var h in _handles) try { h.Dispose(); } catch { }
            _handles.Clear();
            foreach (var e in _spawned) try { if (Spawn.Exists(e)) Spawn.Destroy(e); } catch { }
            Info($"status at the end: {Abilities.Status}");
            Info($"summary: {_pass} PASS, {_fail} FAIL");
        }
    }

    private WorldPosition? FindAnchor()
    {
        foreach (var f in new[] { "FlameAltar", "Altar", "Townsfolk", "NPC_" })
        {
            var id = Spawn.FindByName(f, max: 1).FirstOrDefault();
            if (!id.IsNone && Spawn.Inspect(id) is { } inf) { Info($"anchor {inf.TemplateName} at {inf.Position}"); return inf.Position; }
        }
        return null;
    }

    private async Task<EntityId> Make(string template, WorldPosition p, SpawnOptions? o = null)
    {
        var e = await Spawn.Entity(template, p, 0, o ?? new SpawnOptions { Tag = "abilities-test", Flags = SpawnFlags.Transient });
        _spawned.Add(e);
        return e;
    }

    // ------------------------------------------------------------------------------------------------------------
    private async Task TestStatus()
    {
        var s = Abilities.Status;
        if (s is null) { Fail("kiln.abilities is not available"); return; }
        Check(s.Natives, $"natives on: {s.VanillaNatives} vanilla natives captured, registry capacity {s.NativeCapacity}, {s.KilnNatives} Kiln natives");
        Check(s.VanillaNatives == 107, $"107 vanilla natives (got {s.VanillaNatives})");
        Check(s.Programs && s.ProgramsTotal >= 571, $"programs on: ImpactRegistry holds {s.ProgramsTotal}");
        Check(s.ProgramsKiln == 0, $"no vanilla program was taken for a Kiln one ({s.ProgramsKiln} Kiln programs before the test)");
        Check(s.Instances, "instance hooks on");
        Check(s.Attributes, "attributes on");
        var all = Abilities.Natives.All;
        var dmg = all.FirstOrDefault(n => n.Hash == 0xb04d0e7a);
        Check(dmg is { Kind: ImpactNativeKind.Wrapped, Argc: 25 }, $"applyDamage is wrapped for observation: {dmg}");
        var log = all.FirstOrDefault(n => n.Name == "kiln.Log");
        var sig = all.FirstOrDefault(n => n.Name == "kiln.Signal");
        Check(log is { Kind: ImpactNativeKind.Builtin, Argc: 1, Hash: 0xc0b137a7 } && sig is { Argc: 2, Hash: 0x38b05baf },
            $"built-ins: {log}, {sig}");
        var info = Abilities.Programs.Info(ExplosionProgram);
        Check(info is { Variables: 63, DataBytes: 993 }, $"explosion program info: {info}");
        var vars = Abilities.Programs.Variables(ExplosionProgram);
        var noise = vars.FirstOrDefault(v => v.Alias == "NoiseRange");
        Check(noise is { ConfigId: 0xa19409b2, Size: 4, ValueType: ImpactValueType.Float }, $"program variable {noise}");
        await Task.CompletedTask;
    }

    // ---- Kiln.Hooks: Memory.TryRead inside a detour on a job thread (the fix) ----
    private async Task TestHooksFix()
    {
        if (!Symbols.TryGet("abilities/InstanceLookup", out var lookup)) { Fail("no abilities/InstanceLookup symbol"); return; }
        var tickThread = Environment.CurrentManagedThreadId;
        long otherThread = 0;
        var h = Hooks.Detour(lookup, call =>
        {
            Interlocked.Increment(ref _hookCalls);
            if (Environment.CurrentManagedThreadId != tickThread) Interlocked.Increment(ref otherThread);
            // the instance system's +8: the ImpactRegistry pointer (always readable)
            if (Memory.TryRead<ulong>(call[0] + 8, out var reg) && reg != 0) Interlocked.Increment(ref _hookReadsOk);
            return call.CallOriginal();
        });
        await Steps(25);
        h.Remove();
        Check(_hookCalls > 0 && _hookReadsOk == _hookCalls,
            $"Memory.TryRead inside a detour: {_hookReadsOk}/{_hookCalls} reads ok, {otherThread} calls on game job threads");
    }

    // ---- kiln.spawn's SpawnOptions.Components ----
    private async Task<(EntityId, EntityId)> TestSpawnComponents(WorldPosition p1, WorldPosition p2)
    {
        Check(Spawn.SupportsComponents, "kiln.spawn supports SpawnOptions.Components");
        var a = await Make("Enemy_Vukah_Melee", p1, new SpawnOptions
        {
            Tag = "abilities-test", Flags = SpawnFlags.Transient,
            Components = [SpawnComponent.Of("Faction", new byte[] { 11 })],  // PlayerAlly
        });
        Check(World.TryRead<byte>(a, "Faction", out var f) && f == 11, $"Vukah {a} spawned with Faction 11 from a spawn component (read {f})");
        var b = await Make("Enemy_Vukah_Melee", p2);
        Check(World.TryRead<byte>(b, "Faction", out var fb) && fb == 3, $"Vukah {b} without components keeps Faction 3 (read {fb})");
        try
        {
            await Make("Enemy_Vukah_Melee", p2 with { X = p2.X + 3 }, new SpawnOptions
            {
                Flags = SpawnFlags.Transient, Components = [SpawnComponent.Of("Faction", new byte[] { 1, 2 })],
            });
            Fail("a 2-byte Faction component was accepted");
        }
        catch (SpawnException e)
        {
            Pass($"a 2-byte Faction component is refused by the engine (code {e.Code})");
        }
        await Steps(4);
        return (a, b);
    }

    // ---- the runner: an existing explosion with chosen configs, bound to a caster ----
    private async Task TestRunImpact(EntityId caster, WorldPosition p)
    {
        _started.Clear();
        var plain = await Abilities.RunImpact(Explosion, p);
        var tuned = await Abilities.RunImpact(Explosion, p with { X = p.X + 4 }, new ImpactOptions
        {
            Root = caster,
            Configs = new ImpactConfigs { ["NoiseRange"] = 40f, ["AttackMod"] = 2.5f, ["PostWaitTime"] = TimeSpan.FromSeconds(0.3) },
        });
        _spawned.Add(plain);
        _spawned.Add(tuned);
        Info($"explosions {plain} (template configs) and {tuned} (overrides, root {caster})");
        await Steps(3);
        var sp = _started.FirstOrDefault(e => e.Impact.Self == plain);
        var st = _started.FirstOrDefault(e => e.Impact.Self == tuned);
        Check(sp is not null && st is not null, $"ImpactStartedEvent for both: {sp?.Impact} / {st?.Impact}");
        if (sp is null || st is null) return;
        Check(sp.Impact.ProgramId == ExplosionProgram, $"program 0x{sp.Impact.ProgramId:x8} = FNV(80a2448f-…)");
        var pi = sp.Instance;
        var noisePlain = pi.GetFloat("NoiseRange");
        Check(noisePlain == 25f, $"the game copied the template's config: NoiseRange {noisePlain} (template 25, program default 20)");
        Check(pi.GetFloat("AttackMod") == 1f, $"AttackMod default {pi.GetFloat("AttackMod")}");
        var ti = st.Instance;
        Check(ti.GetFloat("NoiseRange") == 40f && ti.GetFloat("AttackMod") == 2.5f && ti.GetTime("PostWaitTime") == TimeSpan.FromSeconds(0.3),
            $"overrides applied before the first run: NoiseRange {ti.GetFloat("NoiseRange")}, AttackMod {ti.GetFloat("AttackMod")}, PostWaitTime {ti.GetTime("PostWaitTime")}");
        Check(st.Impact.Root == caster, $"ImpactHierarchy root from the spawn component: {st.Impact.Root}");
        Check(World.TryRead<byte>(tuned, "Faction", out var tf) && tf == 11, $"the impact took the caster's faction ({tf})");
        Check(sp.Impact.Root.IsNone || sp.Impact.Root == plain, $"the plain explosion is its own root ({sp.Impact.Root}; template ImpactHierarchy)");
        // a later write through the instance view
        Check(ti.SetFloat("NoiseRange", 41f) && ti.GetFloat("NoiseRange") == 41f, "instance write/read back (NoiseRange 41)");
    }

    // ---- ImpactStartingEvent: held before the first run; changed; canceled ----
    private async Task TestHoldAndCancel(WorldPosition p)
    {
        _starting.Clear();
        _startingSeen.Clear();
        EntityId cancelMe = default;
        _onStarting = e =>
        {
            if (e.Impact.Self == cancelMe) return true;
            e.SetFloat("NoiseRange", 33f);
            return false;
        };
        var changed = await Abilities.RunImpact(Explosion, p);
        cancelMe = await Abilities.RunImpact(Explosion, p with { X = p.X + 4 });
        _spawned.Add(changed);
        _spawned.Add(cancelMe);
        await Steps(4);
        _onStarting = null;
        var ch = _starting.FirstOrDefault(e => e.Impact.Self == changed);
        var ca = _starting.FirstOrDefault(e => e.Impact.Self == cancelMe);
        Check(ch is not null && ca is not null, $"ImpactStartingEvent for both ({_starting.Count} held starts seen)");
        if (ch is null || ca is null) return;
        var seen = _startingSeen[ch.Impact.ImpactId];
        Check(seen.Flags is { } fl && (fl & 2) == 0, $"held: the instance had not run when the handler saw it (flags 0x{seen.Flags:x})");
        var inst = Abilities.Instance(ch.Impact.ImpactId);
        Check(inst is not null && inst.GetFloat("NoiseRange") == 33f && inst.Flags is { } f2 && (f2 & 2) != 0,
            $"after release it ran with the handler's value: NoiseRange {inst?.GetFloat("NoiseRange")}, flags 0x{inst?.Flags:x}");
        Check(!Spawn.Exists(cancelMe), $"the canceled impact's entity {cancelMe} is gone");
        var cinst = Abilities.Instance(ca.Impact.ImpactId);
        Check(cinst is null || (cinst.Flags & 2) == 0, $"the canceled instance never ran (flags {cinst?.Flags})");
    }

    private async Task TestEnded()
    {
        var sw = Stopwatch.StartNew();
        while (_ended.Count == 0 && sw.Elapsed.TotalSeconds < 6) await Steps(5);
        Check(_ended.Count > 0, $"ImpactEndedEvent: {_ended.Count} ended ({string.Join(", ", _ended.Take(3).Select(e => e.Impact.ToString()))})");
    }

    // ---- observation (job thread → next Pre) and a synchronous wrapper on the job thread ----
    private async Task TestNativesObserveAndWrap(EntityId caster, WorldPosition p)
    {
        Abilities.Natives.Observe("getTransform");
        _natives.Clear();
        var tick = Environment.CurrentManagedThreadId;
        long offTick = 0;
        var wrap = Abilities.Natives.Wrap("spawnEntity", (ref NativeCall c) =>
        {
            if (Environment.CurrentManagedThreadId != tick) Interlocked.Increment(ref offTick);
            Interlocked.Increment(ref _wrapCalls);
            if (c.ProgramId == ExplosionProgram) Interlocked.Increment(ref _wrapSawProgram);
        });
        var e = await Abilities.RunImpact(Explosion, p, new ImpactOptions { Root = caster });
        _spawned.Add(e);
        await Steps(4);
        wrap.Dispose();
        var gt = _natives.FirstOrDefault(n => n.Native == 0xa18f5959 && n.Impact.Self == e);
        Check(gt is not null, $"observed getTransform of {e}: {gt?.NativeName} argc {gt?.ArgCount} result {gt?.Result}");
        var spn = _natives.OfType<ImpactSpawnEvent>().FirstOrDefault(n => n.Impact.Self == e);
        Check(spn is not null, $"ImpactSpawnEvent (spawnEntity SpawnTemplate_Pre) from {e}: template {spn?.Template}, result {spn?.Result}, root {spn?.Impact.Root}");
        Check(_wrapCalls > 0 && _wrapSawProgram > 0, $"wrap of spawnEntity ran {_wrapCalls}x ({_wrapSawProgram}x in the explosion program, {offTick}x off the tick thread); the game's native still ran after it");
    }

    // ---- a program built here: registration (waits for its native), a managed native, kiln.Signal, kiln.Log ----
    private async Task TestProgramsAndManagedNative(WorldPosition p)
    {
        var guid = new Guid("6b1f1a2e-7c4d-4e8a-9a51-3c2b0d7e9f10");
        var nativeName = "abilities-test.Double";
        var nativeHash = Abilities.HashOf(nativeName);
        var blob = ProgramBuilder.DoubleThenSignal(guid, nativeHash, input: 21, tag: 7);
        var id = Abilities.Programs.Register(blob, out var waiting);
        Check(waiting && Abilities.Programs.Info(id) is null, $"program {guid} (0x{id:x8}) waits for its native {nativeName} (0x{nativeHash:x8})");
        var calls = 0;
        Abilities.Natives.Register("Double", 2, call =>
        {
            calls++;
            var x = call.GetInt(1);
            call.SetInt(0, x * 2);
            Info($"native Double({x}) on {call.Impact}, site 0x{call.Site:x8}");
            return 1;
        });
        var info = Abilities.Programs.Info(id);
        Check(info is { FromKiln: true }, $"after the native was registered the program is in the ImpactRegistry: {info}");
        _signals.Clear();
        _started.Clear();
        EntityId e;
        var generic = Spawn.Templates.Any(t => t.Id == Abilities.GenericImpactTemplate);
        Info($"generic impact template {Abilities.GenericImpactTemplate} loaded by the game: {generic}");
        e = await Abilities.RunProgram(guid, p);
        _spawned.Add(e);
        await Steps(6);
        var st = _started.FirstOrDefault(s => s.Impact.Self == e);
        Check(st is not null && st.Impact.ProgramId == id, $"RunProgram started program 0x{id:x8} on {e} ({st?.Impact})");
        var sig = _signals.FirstOrDefault(s => s.Impact.Self == e);
        Check(calls == 1 && sig is { Tag: 7, AsInt: 42 },
            $"managed native answered on the tick thread ({calls} call), the ECALL resumed: kiln.Signal tag {sig?.Tag} value {sig?.AsInt}");
        if (st is not null)
        {
            var inst = Abilities.Instance(st.Impact.ImpactId);
            Info($"program instance after the run: flags {inst?.Flags}, Out {inst?.GetInt("Out")}");
        }
        try
        {
            Abilities.Programs.Register(blob);
            Fail("a second registration of the same GUID was accepted");
        }
        catch (ArgumentException ex) { Pass($"a second registration is refused: {ex.Message}"); }
    }

    private async Task TestVerifier()
    {
        var bad = new (string What, byte[] Blob)[]
        {
            ("no HALT", ProgramBuilder.Raw(new Guid("6b1f1a2e-7c4d-4e8a-9a51-3c2b0d7e9f11"), [13, 27, 28], [30])),
            ("stack 32", ProgramBuilder.Raw(new Guid("6b1f1a2e-7c4d-4e8a-9a51-3c2b0d7e9f12"),
                Enumerable.Repeat(12u, 32).Concat(Enumerable.Repeat(27u, 32)).Append(30u).ToArray(), [30])),
            ("CALL", ProgramBuilder.Raw(new Guid("6b1f1a2e-7c4d-4e8a-9a51-3c2b0d7e9f13"), [18, 3, 0, 30], [30])),
            ("a vanilla GUID", ProgramBuilder.Raw(new Guid("80a2448f-945c-45f6-8fce-9b09ed61620c"), [30], [30])),
        };
        foreach (var (what, blob) in bad)
        {
            try
            {
                Abilities.Programs.Register(blob);
                Fail($"verifier accepted a program with {what}");
            }
            catch (ArgumentException ex) { Pass($"verifier refuses {what}: {ex.Message}"); }
        }
        var ok = ProgramBuilder.Raw(new Guid("6b1f1a2e-7c4d-4e8a-9a51-3c2b0d7e9f14"), [12, 12, 1, 27, 28, 30], [30]);
        var id = Abilities.Programs.Register(ok, out var waiting);
        Check(!waiting && Abilities.Programs.Info(id) is not null, $"a minimal valid program (0x{id:x8}) is registered");
        await Task.CompletedTask;
    }

    private async Task TestAttributes(EntityId vukah)
    {
        var st = Abilities.Attributes.Get(vukah, AbilityResource.Stamina);
        var max = Abilities.Attributes.Max(vukah, AbilityResource.Stamina);
        var hp = Abilities.Attributes.Get(vukah, AbilityResource.Health);
        Check(st is > 0 && max is > 0 && hp is > 0, $"Vukah attributes: stamina {st}/{max}, health {hp} (kiln.combat {Combat.GetHealth(vukah)})");
        if (st is null) return;
        Check(Abilities.Attributes.Add(vukah, AbilityResource.Stamina, -5) && Abilities.Attributes.Get(vukah, AbilityResource.Stamina) == st - 5,
            $"stamina -5 through the attribute code: {Abilities.Attributes.Get(vukah, AbilityResource.Stamina)}");
        Abilities.Attributes.Add(vukah, AbilityResource.Stamina, 5);
        Check(Abilities.Attributes.Get(vukah, AbilityResource.Mana) is null, "a Vukah has no Mana attribute (null)");
        await Steps(1);
    }

    // ---- a buff through the game's applyBuff: the heal-over-time potion's buff (be0e7626-…, id 0x339519bd) ----
    private async Task TestBuff(EntityId vukah)
    {
        var hot = new Guid("be0e7626-e95f-4a14-844a-2af52501698e");
        Combat.SetHealth(vukah, 40);
        var before = Combat.GetHealth(vukah)?.Current ?? -1;
        _natives.Clear();
        _started.Clear();
        Info($"Vukah {vukah}: BuffContainer {World.Has(vukah, "BuffContainer")} ({World.ComponentSize(vukah, "BuffContainer")} B)");
        var e = await Abilities.ApplyBuff(vukah, hot, TimeSpan.FromSeconds(6));
        _spawned.Add(e);
        await Steps(4);
        var ev = _natives.OfType<ImpactBuffEvent>().FirstOrDefault(n => n.Impact.Self == e);
        Check(ev is not null && ev.Target == vukah && ev.Buff == hot,
            $"ApplyBuff ran the game's applyBuff from {e}: buff {ev?.Buff}, target {ev?.Target}, duration {ev?.Duration}, result {ev?.Result}");
        await Steps(4);
        var buffProgram = Abilities.Programs.IdOf(new Guid("e34f34e4-5e7e-41ca-af26-09c9bd96c877"));
        var inst = _started.FirstOrDefault(s => s.Impact.ProgramId == buffProgram);
        Info($"impacts after ApplyBuff: {string.Join(", ", _started.Take(6).Select(s => $"{s.Impact} {s.TemplateName}"))}");
        Check(inst is not null, $"the buff's own program (e34f34e4, 0x{buffProgram:x8}) started on buff entity {inst?.Impact.Self} (root {inst?.Impact.Root})");
        await Steps(100);
        var after = Combat.GetHealth(vukah)?.Current ?? -1;
        Info($"health of the Vukah over 4 s of the heal-over-time buff: {before} -> {after} (the potion's buff may heal players only)");
        Check(!Spawn.Exists(e), $"the buff applier {e} destroyed itself (DSELF)");
    }

    // ---- RemoveBuff and BuffsOf (dev.27): the game's removeBuff on a creature and on a player-template entity ----
    private readonly List<BuffRemovedEvent> _removed = new();
    [Subscribe] private void OnBuffRemoved(BuffRemovedEvent e) => _removed.Add(e);

    private static int BuffEntities(uint id) =>
        World.Find("BuffNetworkData").Count(x => World.ReadBytes(x, "BuffNetworkData") is { Length: >= 24 } b && BitConverter.ToUInt32(b, 0x10) == id);

    private async Task TestRemoveBuff(EntityId vukah, WorldPosition playerAt)
    {
        var hot = new Guid("be0e7626-e95f-4a14-844a-2af52501698e");
        var hotId = Abilities.IdOf(hot);
        Check(Capabilities.Is("abilities.buffs") && Capabilities.Is("abilities.buff_list"),
            $"capability rows: {Capabilities.Why("abilities.buffs")} / {Capabilities.Why("abilities.buff_list")}");
        await StepsUntil(() => !Abilities.BuffsOf(vukah).Any(b => b.Is(hot)), 240);  // TestBuff's 6 s buff runs out first
        var before = Abilities.BuffsOf(vukah);
        Info($"Vukah {vukah} buffs before: [{string.Join(", ", before.Select(b => $"0x{b.Id:x8} {b.Slot} {b.Remaining}"))}]");
        _spawned.Add(await Abilities.ApplyBuff(vukah, hot, TimeSpan.FromSeconds(60)));
        await Steps(6);
        var one = Abilities.BuffsOf(vukah).Count(b => b.Is(hot));
        var ents1 = BuffEntities(hotId);
        Check(one == 1, $"BuffsOf lists the buff on a creature: {one} x 0x{hotId:x8} ({string.Join(", ", Abilities.BuffsOf(vukah).Select(b => $"0x{b.Id:x8} {b.Slot} {b.Remaining} level {b.Level}"))}), {ents1} buff entity");
        // a second application of the same buff: a second stack, or a renewal of the first?
        _spawned.Add(await Abilities.ApplyBuff(vukah, hot, TimeSpan.FromSeconds(60)));
        await Steps(6);
        var two = Abilities.BuffsOf(vukah).Count(b => b.Is(hot));
        var ents2 = BuffEntities(hotId);
        Info($"after a second ApplyBuff of the same buff: {two} entr(ies) in the buff slots, {ents2} buff entit(ies)");
        _removed.Clear();
        var t0 = AbilitiesStep();
        var r1 = await Abilities.RemoveBuff(vukah, hot);
        var atAnswer = Abilities.BuffsOf(vukah).Count(b => b.Is(hot));
        Check(r1.Ok && r1.Reason is null && r1.Target == vukah && r1.Buff == hotId,
            $"RemoveBuff: the game's removeBuff says removed: {r1} ({AbilitiesStep() - t0} steps)");
        // the game clears the slot and destroys the buff entity after removeBuff returned: count the steps
        var gone = await StepsUntil(() => !Abilities.BuffsOf(vukah).Any(b => b.Is(hot)) && BuffEntities(hotId) == 0, 20);
        var after1 = Abilities.BuffsOf(vukah).Count(b => b.Is(hot));
        var ents3 = BuffEntities(hotId);
        Info($"at the answer the slot still held {atAnswer}; slot and buff entity gone {gone} step(s) later; one RemoveBuff took off {two - after1} of {two} entr(ies) (buff entities {ents2} -> {ents3})");
        Check(gone >= 0 && after1 == 0 && ents3 == 0, $"the buff is gone from the slots ({two} -> {after1}) and its entity with it ({ents2} -> {ents3})");
        Check(_removed.Count == 1 && _removed[0].Target == vukah && _removed[0].Buff == hotId && _removed[0].Mod == "abilities-test",
            $"BuffRemovedEvent: {string.Join(", ", _removed.Select(e => $"{e.Target} 0x{e.Buff:x8} by {e.Mod}"))}");
        var rest = after1;
        for (var i = 0; i < 4 && rest > 0; i++)
        {
            var more = await Abilities.RemoveBuff(vukah, hot);
            rest = Abilities.BuffsOf(vukah).Count(b => b.Is(hot));
            Info($"another RemoveBuff: {more}; {rest} left");
        }
        _removed.Clear();
        var r2 = await Abilities.RemoveBuff(vukah, hot);
        Check(!r2.Ok && r2.Reason is { Length: > 0 } && _removed.Count == 0,
            $"RemoveBuff of a buff the entity does not have: the game answers not removed: {r2}; no event");
        // by id (a GameBuffs.Ids constant or a listed EntityBuff.Id): 16 bytes with that hash
        _spawned.Add(await Abilities.ApplyBuff(vukah, hot, TimeSpan.FromSeconds(60)));
        await Steps(6);
        var r3 = await Abilities.RemoveBuff(vukah, hotId);
        var gone3 = await StepsUntil(() => !Abilities.BuffsOf(vukah).Any(b => b.Id == hotId), 20);
        Check(r3.Ok && gone3 >= 0, $"RemoveBuff by id 0x{hotId:x8}: {r3}; gone {gone3} step(s) later");
        // a player-template entity: a player's own buff container
        EntityId player;
        try { player = await Make("1_Player", playerAt); }
        catch (Exception e) { Info($"no player-template entity in this world ({e.Message}): player buff checks skipped"); return; }
        await Steps(10);
        _spawned.Add(await Abilities.ApplyBuff(player, hot, TimeSpan.FromSeconds(60)));
        await Steps(6);
        var listed = Abilities.BuffsOf(player).Where(b => b.Is(hot)).ToList();
        var onPlayer = listed.Count;
        Info($"on the player entity after ApplyBuff(60 s): [{string.Join(", ", listed.Select(b => $"{b.Slot} {b.Remaining} level {b.Level}"))}]");
        var r4 = await Abilities.RemoveBuff(player, hot);
        await StepsUntil(() => !Abilities.BuffsOf(player).Any(b => b.Is(hot)), 20);
        var leftOnPlayer = Abilities.BuffsOf(player).Count(b => b.Is(hot));
        Check(onPlayer == 1 && r4.Ok && leftOnPlayer == 0 && PlayerStates.BuffsOf(player).All(b => b.Id != hotId),
            $"on a 1_Player entity: listed {onPlayer}, {r4}, {leftOnPlayer} left (PlayerStates.BuffsOf agrees)");
        // bad arguments throw at once
        try { _ = Abilities.RemoveBuff(EntityId.None, hot); Fail("RemoveBuff(None) did not throw"); }
        catch (ArgumentException e) { Pass($"RemoveBuff(None) throws ArgumentException: {e.Message}"); }
        try { _ = Abilities.RemoveBuff(vukah, 0u); Fail("RemoveBuff(id 0) did not throw"); }
        catch (ArgumentException e) { Pass($"RemoveBuff(id 0) throws ArgumentException: {e.Message}"); }
        Check(Abilities.BuffsOf(EntityId.None).Count == 0, "BuffsOf(None) is empty");
    }

    private static ulong AbilitiesStep() => Abilities.Status?.Step ?? 0;

    // Steps until the condition holds (0 = at once), or -1 after max steps.
    private async Task<int> StepsUntil(Func<bool> condition, int max)
    {
        for (var i = 0; i <= max; i++)
        {
            if (condition()) return i;
            await Steps(1);
        }
        return -1;
    }

    private async Task TestAbilities(EntityId caster, EntityId enemy)
    {
        var effects = 0;
        EntityId spawnedBy = default;
        var h = Abilities.Register(new AbilityDefinition
        {
            Id = "abilities-test:stomp", DisplayName = "Stomp", Cost = AbilityCost.OfStamina(5), Cooldown = TimeSpan.FromSeconds(5),
            CastTime = TimeSpan.FromSeconds(0.5), Targeting = AbilityTargeting.NearestEnemy, Range = 15, PlayersOnly = false,
            OnCast = async cast =>
            {
                effects++;
                await cast.At(TimeSpan.FromSeconds(0.25));
                spawnedBy = await cast.Impact(Explosion, cast.CasterPosition);
                _spawned.Add(spawnedBy);
            },
        });
        _handles.Add(h);
        var before = Abilities.Attributes.Get(caster, AbilityResource.Stamina) ?? 0;
        var r1 = Abilities.Cast(caster, "abilities-test:stomp");
        Check(r1.Ok && r1.Cast?.Target == enemy, $"server-side cast by NPC {caster}: {r1.Ok} {r1.Reason}, target {r1.Cast?.Target} (expected {enemy})");
        var after = Abilities.Attributes.Get(caster, AbilityResource.Stamina) ?? 0;
        Check(after == before - 5, $"stamina cost paid: {before} -> {after}");
        var r2 = Abilities.Cast(caster, "abilities-test:stomp");
        Check(!r2.Ok && r2.Reason!.StartsWith("busy"), $"second cast at once refused: {r2.Reason}");
        await Steps(15);
        var r3 = Abilities.Cast(caster, "abilities-test:stomp");
        Check(!r3.Ok && r3.Reason!.StartsWith("cooldown"), $"within the cooldown refused: {r3.Reason}");
        Check(effects == 1 && !spawnedBy.IsNone, $"OnCast ran once and its timed effect spawned {spawnedBy} after 0.25 s");
        _started.Clear();
        await Steps(3);
        Abilities.Register(new AbilityDefinition
        {
            Id = "abilities-test:mana", Cost = AbilityCost.OfMana(10), PlayersOnly = false, OnCast = _ => Task.CompletedTask,
        });
        var r4 = Abilities.Cast(caster, "abilities-test:mana");
        Check(!r4.Ok, $"a mana cost on a caster without mana is refused: {r4.Reason}");
        var canceled = 0;
        using (Events.On<AbilityCastEvent>(ev => { if (ev.Cast.Ability.Id == "abilities-test:free") { canceled++; ev.Reason = "test veto"; ev.Cancel(); } }))
        {
            Abilities.Register(new AbilityDefinition { Id = "abilities-test:free", PlayersOnly = false, OnCast = _ => Task.CompletedTask });
            var r5 = Abilities.Cast(caster, "abilities-test:free");
            Check(!r5.Ok && r5.Reason == "test veto" && canceled == 1, $"AbilityCastEvent cancel: {r5.Reason}");
        }
        var r6 = Abilities.Cast(EntityId.None, "abilities-test:free");
        Check(!r6.Ok, $"no caster refused: {r6.Reason}");
        var r7 = Abilities.Cast(caster, "abilities-test:nope");
        Check(!r7.Ok, $"unknown ability refused: {r7.Reason}");
    }

    private async Task TestSequence(EntityId vukah)
    {
        var s = Abilities.SequenceOf(vukah);
        Check(s is not null, $"Actor sequence state of {vukah}: sequence 0x{s?.Sequence:x8}, trigger {s?.TriggerTime}, call {s?.CallIndex}");
        // the Vukah's melee swing (arsenal attack 6f6144ed → sequence a837f190, 3 SpawnImpacts of program 0c3a7176)
        var swing = new Guid("a837f190-d163-4ef5-a48e-3ea7caac7296");
        _started.Clear();
        var seqStarts = new List<SequenceStartedEvent>();
        using var sub = Events.On<SequenceStartedEvent>(e => { if (e.Actor == vukah) seqStarts.Add(e); });
        await Steps(55);  // the watcher learns the actor list first
        string Trig(EntityId e) => World.ReadBytes(e, "ActorInput") is { Length: >= 0x180 } b
            ? $"triggerContext seq 0x{BitConverter.ToUInt32(b, 8):x8} time {BitConverter.ToInt64(b, 0x10)} actor {BitConverter.ToUInt32(b, 0x18)} type {BitConverter.ToUInt16(b, 0x20)} counter {b[0x178]}"
            : "no ActorInput";
        Info($"before: {Trig(vukah)}; actor bits 0x{(World.ReadBytes(vukah, "Actor") is { Length: > 0xC00 } ab ? BitConverter.ToUInt64(ab, 0xBF8) : 0):x}");
        var ok = Abilities.PlaySequence(vukah, swing);
        Check(ok, $"PlaySequence(Vukah {vukah}, swing 0x{Abilities.IdOf(swing):x8}) accepted");
        Info($"right after: {Trig(vukah)}");
        await Steps(2);
        Info($"2 steps later: {Trig(vukah)}");
        // a villager too (the NPC worker pauses far from players, the actor may not)
        var villager = Spawn.FindByName("NPC_", max: 1).FirstOrDefault();
        if (!villager.IsNone && Abilities.SequenceOf(villager) is { } vs)
            Info($"villager {villager} ({Spawn.Inspect(villager)?.TemplateName}) sequence 0x{vs.Sequence:x8}; {Trig(villager)}");
        Check(!Abilities.PlaySequence(vukah, Guid.NewGuid()), "PlaySequence refuses an unknown sequence GUID");
        uint seen = 0;
        for (var i = 0; i < 60 && seen != Abilities.IdOf(swing); i++)
        {
            await Steps(1);
            seen = Abilities.SequenceOf(vukah)?.Sequence ?? 0;
        }
        Check(seen == Abilities.IdOf(swing), $"the Vukah's Actor.sequenceRuntimeId is the swing now (0x{seen:x8})");
        await Steps(50);
        var hits = _started.Where(e => e.Impact.ProgramId == Abilities.Programs.IdOf(new Guid("0c3a7176-9336-47a3-ae98-d9433ac5ffbb"))).ToList();
        Info($"impacts started after the swing: {string.Join(", ", _started.Take(8).Select(e => e.Impact.ToString()))}");
        Check(hits.Count > 0, $"the swing's SpawnImpact events ran server-side: {hits.Count} instance(s) of program 0c3a7176 (root {hits.FirstOrDefault()?.Impact.Root})");
        Check(seqStarts.Count > 0, $"SequenceStartedEvent for the Vukah ({seqStarts.Count}: 0x{seqStarts.FirstOrDefault()?.Sequence:x8})");
    }

    // Sequences on a player character: a player-template entity (1_Player, no client behind it) runs the
    // same actor/sequence programs as a joined player's character on the server. Emotes and melee attacks have no
    // keep-alive gates; a staff cast and a bow shot end at their first KeepAliveEvent unless the input states are held.
    internal static readonly Guid Greet = new("13fd798d-033a-494b-95e8-76fe817202b5");        // Ability_Player_Emote_Greet 3.83 s
    internal static readonly Guid Fists = new("06cb777d-8ee2-4e2c-bb2b-c5626cc01b9e");        // Attack_Player_Unarmed_Fists_LightChain01 1.68 s
    internal static readonly Guid Fireball = new("deff43ba-530a-4bee-be84-9b3c781e9556");     // Attack_Player_Staff_FixedAnim_Fireball_fast 2.08 s
    internal static readonly Guid BowShot = new("ca6fdde3-8890-42d5-bf3f-1e216a87ceec");      // Attack_Player_Ranged_Bow_Shoot 0.8 s
    private const ulong InRangeCombatShooting = 1ul << 27;

    private async Task<(int Steps, ulong NetStates, ushort Trigger)> Play(EntityId p, Guid seq, SequenceOptions o)
    {
        var id = Abilities.IdOf(seq);
        if (!Abilities.PlaySequence(p, seq, o)) return (-1, 0, 0);
        int last = 0;
        ulong states = 0;
        ushort trig = 0;
        for (var i = 1; i <= 140; i++)
        {
            await Steps(1);
            var na = World.ReadBytes(p, "NetworkActor");
            if (na is null || BitConverter.ToUInt32(na, 0) != id) { if (last > 0) break; continue; }
            last = i;
            states |= BitConverter.ToUInt64(na, 0x28);
            trig = BitConverter.ToUInt16(na, 0xcc);
        }
        await Steps(3);
        return (last, states, trig);
    }

    private async Task TestPlayerSequences(WorldPosition at)
    {
        EntityId p;
        try { p = await Make("1_Player", at); }
        catch (Exception e) { Info($"no player-template entity in this world ({e.Message}): player sequence checks skipped"); return; }
        await Steps(5);
        Check(World.ComponentSize(p, "PlayerInput") > 0 && World.ComponentSize(p, "ActorInput") > 0,
              $"a player-template entity {p} (1_Player) has PlayerInput and ActorInput like a joined player's character");
        var greet = await Play(p, Greet, SequenceOptions.Queue);
        Check(greet.Steps >= 80, $"the Greet emote plays on the player character for {greet.Steps} steps (3.83 s) through ExternalTrigger, the trigger the game's own emotes use (NetworkActor.triggerType {greet.Trigger})");
        Check(greet.Trigger == 22, "NetworkActor (replicated) carries the emote with trigger ExternalTrigger (22)");
        var fists = await Play(p, Fists, SequenceOptions.Default);
        Check(fists.Steps >= 30, $"a melee attack (Attack_Player_Unarmed_Fists_LightChain01, 1.68 s) plays on the player for {fists.Steps} steps (trigger {fists.Trigger})");
        var fbNo = await Play(p, Fireball, new SequenceOptions { HoldInputs = false });
        var fbHold = await Play(p, Fireball, SequenceOptions.Default);
        Info($"Fireball cast: {fbNo.Steps} steps without the hold, {fbHold.Steps} with it (replicated states seen 0x{fbHold.NetStates:x})");
        Check(fbNo.Steps is > 0 and <= 8, $"without the hold the staff cast ends at its first KeepAliveEvent ({fbNo.Steps} steps; 0.2 s = 5): what session #7 saw");
        Check(fbHold.Steps >= 40, $"with HoldInputs the staff cast plays through its shot and its loop ({fbHold.Steps} steps, about 1.9 of its 2.08 s)");
        Check((fbHold.NetStates & InRangeCombatShooting) != 0, "the held input state (InRangeCombatShooting) is in the replicated NetworkActor.setStateMask while it plays");
        var bowNo = await Play(p, BowShot, new SequenceOptions { HoldInputs = false });
        var bowHold = await Play(p, BowShot, SequenceOptions.Default);
        Check(bowHold.Steps > bowNo.Steps + 5, $"a bow shot: {bowNo.Steps} steps without the hold, {bowHold.Steps} with it (0.8 s = 20)");
        var na = World.ReadBytes(p, "Actor");
        Check(na is not null && (BitConverter.ToUInt64(na, 0xbd0) & (0xFul << 25)) == 0, "no held state is left on the actor after the sequences ended");
    }

    // ------------------------------------------------------------------------------------------------------------
    private void Check(bool ok, string what) { if (ok) Pass(what); else Fail(what); }
    private void Pass(string s) { _pass++; Log.Info($"abilities-test: PASS {s}"); }
    private void Fail(string s) { _fail++; Log.Error($"abilities-test: FAIL {s}"); }
    private void Info(string s) => Log.Info($"abilities-test: INFO {s}");
}

/// <summary>Builds keen::ImpactProgram blobs in memory (the format of docs/kiln/services/abilities.md).</summary>
internal static class ProgramBuilder
{
    private const uint Sint32 = 0x9859f31e;

    public static uint Fnv(ReadOnlySpan<byte> b) { uint h = 0x811c9dc5; foreach (var x in b) h = (h ^ x) * 0x01000193; return h; }

    public static byte[] Raw(Guid guid, uint[] code, uint[] shutdown, (string Name, uint Type, int Size, byte[] Init)[]? vars = null) =>
        Build(guid, code, shutdown, vars ?? []);

    // In = input (sint32), Out (sint32): Out = Double(In); kiln.Signal(Out, tag); kiln.Log(Out); HALT
    public static byte[] DoubleThenSignal(Guid guid, uint doubleHash, int input, uint tag)
    {
        const uint hIn = 0xFFFF0000, hOut = 0xFFFF0001;
        uint signal = Fnv("kiln.Signal"u8), log = Fnv("kiln.Log"u8);
        uint[] code =
        [
            11, hIn, 11, hOut, 19, 0x7e570001, doubleHash, 27,   // Double(args[0] = Out, args[1] = In)
            11, tag, 11, hOut, 19, 0x7e570002, signal, 27,       // kiln.Signal(args[0] = Out, args[1] = tag)
            11, hOut, 19, 0x7e570003, log, 27,                   // kiln.Log(Out)
            30,
        ];
        return Build(guid, code, [30], [("In::value", Sint32, 4, BitConverter.GetBytes(input)), ("Out::value", Sint32, 4, new byte[4])]);
    }

    private static byte[] Build(Guid guid, uint[] code, uint[] shutdown, (string Name, uint Type, int Size, byte[] Init)[] vars)
    {
        var names = vars.Select(v => Encoding.UTF8.GetBytes(v.Name)).ToArray();
        var dataSize = vars.Sum(v => v.Size);
        var codeOff = 64;
        var shutOff = codeOff + code.Length * 4;
        var layoutOff = shutOff + shutdown.Length * 4;
        var namesOff = layoutOff + vars.Length * 24;
        var dataOff = namesOff + names.Sum(n => n.Length);
        var total = dataOff + dataSize;
        var b = new byte[(total + 3) & ~3];
        var g = guid.ToByteArray();
        BitConverter.GetBytes(Fnv(g)).CopyTo(b, 0);
        g.CopyTo(b, 4);
        BitConverter.GetBytes((ushort)256).CopyTo(b, 0x14);
        void Arr(int field, int target, int count)
        {
            BitConverter.GetBytes(count == 0 ? 0 : target - field).CopyTo(b, field);
            BitConverter.GetBytes(count).CopyTo(b, field + 4);
        }
        Arr(0x18, codeOff, code.Length);
        Arr(0x20, shutOff, shutdown.Length);
        Arr(0x28, layoutOff, vars.Length);
        Arr(0x30, dataOff, dataSize);
        Arr(0x38, 0, 0);
        for (var i = 0; i < code.Length; i++) BitConverter.GetBytes(code[i]).CopyTo(b, codeOff + i * 4);
        for (var i = 0; i < shutdown.Length; i++) BitConverter.GetBytes(shutdown[i]).CopyTo(b, shutOff + i * 4);
        int nameAt = namesOff, dataAt = 0;
        for (var i = 0; i < vars.Length; i++)
        {
            var e = layoutOff + i * 24;
            BitConverter.GetBytes(Fnv(names[i])).CopyTo(b, e);       // name hash
            BitConverter.GetBytes(0u).CopyTo(b, e + 4);              // config id
            BitConverter.GetBytes(vars[i].Type).CopyTo(b, e + 8);
            BitConverter.GetBytes((ushort)vars[i].Size).CopyTo(b, e + 12);
            BitConverter.GetBytes((ushort)dataAt).CopyTo(b, e + 14);
            BitConverter.GetBytes(nameAt - (e + 16)).CopyTo(b, e + 16);
            BitConverter.GetBytes(names[i].Length).CopyTo(b, e + 20);
            names[i].CopyTo(b, nameAt);
            nameAt += names[i].Length;
            vars[i].Init.CopyTo(b, dataOff + dataAt);
            dataAt += vars[i].Size;
        }
        return b;
    }
}
