// SPDX-License-Identifier: MIT
// The pets end-to-end lab (dev.32 PETS-E2E): a lab bot (tools/labbot) owns PetsSample's pets on a dedicated lab server,
// with no game client. The bot types PetsSample's /pet commands; this mod records what Kiln.Pets did and does what a bot
// cannot, through admin commands the lab script runs as the bot (bot.command("e2e ...")):
//   e2e site                  a sealed lane of building blocks beside the bot (two rooms, a 1 m wall with a 2 m gap) and a
//                             pad 85 m away; their coordinates go to the evidence ("site")
//   e2e state                 the caller's pets: life, presence, body, its faction and AI target, distances, revisions,
//                             the follower's limit and catch-ups (a JSON line)
//   e2e enemy <tag> <dx> <dz> an unowned early-game wolf (WildBeast) at the caller + (dx, dz), invulnerable
//   e2e attack <pet> <entity> Pets.Command Attack; the body's AI target is recorded every step for 10 s
//   e2e lethal <pet> <entity> a lethal engine hit on the pet's body from that creature (Combat.Damage)
//   e2e kill <entity>         Combat.Kill with the caller as the attacker (the XP and loot control)
//   e2e give <item> <n>, e2e count <item>, e2e xp, e2e god <on|off>
//   e2e revive <pet> <tag>    Pets.Revive as the caller; the answer with its receipts goes to the evidence
//   e2e save <tag>            Saves.Request; e2e downsave <pet> <entity>: a lethal hit and a save request in one step
//   e2e sweep <max>           the revive-cancel sweep: CancelRevive k steps after the revive's preview body appears
// Evidence: kiln/data/pets-e2e-lab/e2e.jsonl, one JSON object a line (kind, wall and game seconds, the server step):
// every pet event, the broker's lease changes, Kiln.Creatures' changes of pet bodies with their origin, deaths and
// drops, saves and commits, the owners' view publisher's sends (read by reflection: lab evidence only), and every
// owner's and pet's position each step. Time is the game's own clock (ClockDelta, 60 Hz units), as Kiln.Pets' own.
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Kiln;

namespace PetsE2eLab;

public sealed partial class PetsE2eLabMod : Mod
{
    internal const string Me = "pets-e2e-lab";
    internal const string Mule = Me + ":mule";
    private StreamWriter? _out;
    private readonly object _outGate = new();
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private double _game;
    private ulong _step;
    private bool _trace = true;
    private readonly List<EventSubscription> _subs = [];
    private readonly List<EntityId> _spawned = [];
    private readonly Queue<Action> _nextPre = new();
    private readonly Dictionary<PetId, PetSnapshot> _known = [];
    private readonly Dictionary<long, string> _bodies = [];          // body entity -> pet id (every body a pet had)
    private readonly Dictionary<PetId, EntityId> _lastBody = [];      // pet -> the last body it had
    private readonly Dictionary<CharacterKey, (ulong Join, ulong Token, ulong Seq, double Last, bool Dirty)> _hud = [];
    private (PetId Pet, EntityId Target, double Until)? _attackWatch;
    private bool _muleDefined;
    private ulong? _previewStep;    // the sweep: the step a revive's preview body was set up (its Faction change)
    private bool _awaitPreview;

    internal void Info(string s) => Log.Info($"{Me}: {s}");

    public override void OnLoad()
    {
        Directory.CreateDirectory(Context.DataDirectory);
        _out = new StreamWriter(Path.Combine(Context.DataDirectory, "e2e.jsonl"), append: false) { AutoFlush = true };
        Context.Tick.Pre += OnPre;
        Subscribe();
        Write("load", new JsonObject { ["kiln"] = Kiln.Mods.RuntimeApiVersion });
        Info("loaded; evidence in e2e.jsonl");
    }

    public override void OnUnload()
    {
        Context.Tick.Pre -= OnPre;
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        foreach (var e in _spawned)
            try { Spawn.Destroy(e); } catch (Exception) { }
        lock (_outGate)
        {
            _out?.Dispose();
            _out = null;
        }
    }

    internal void Write(string kind, JsonObject o)
    {
        o["kind"] = kind;
        o["wall"] = R(_wall.Elapsed.TotalSeconds);
        o["game"] = R(_game);
        o["step"] = _step;
        var line = o.ToJsonString();
        lock (_outGate) _out?.WriteLine(line);
    }

    // ------------------------------------------------------------------------------------------- the step ------

    private void OnPre(in TickInfo t)
    {
        _game += t.ClockDelta / 60.0;
        _step = t.Step;
        try
        {
            while (_nextPre.Count > 0) _nextPre.Dequeue()();
            if (!_muleDefined) DefineMule();
            if (_trace) Trace();
            Hud();
            WatchAttack();
        }
        catch (Exception e)
        {
            Write("error", new JsonObject { ["where"] = "pre", ["error"] = e.ToString() });
        }
    }

    private void Trace()
    {
        if (!Capabilities.Is("pets.records")) return;
        var rows = new JsonArray();
        foreach (var pl in Players.All)
        {
            if (pl.Entity.IsNone || !pl.InGame) continue;
            var key = CharacterData.KeyOf(pl.Entity);
            if (key.IsNone) continue;
            var op = World.Position(pl.Entity);
            var pets = new JsonArray();
            foreach (var p in Pets.Of(key))
            {
                _known[p.Id] = p;
                if (!p.Body.IsNone)
                {
                    _bodies[p.Body.Id.Value] = p.Id.ToString();
                    _lastBody[p.Id] = p.Body.Id;
                }
                if (p.Body.IsNone && p.Presence != PetPresence.RecoveryAnchor) continue;
                var bp = p.Body.IsNone ? null : World.Position(p.Body.Id);
                pets.Add(new JsonArray(Short(p.Id), p.Presence.ToString(), bp is { } b ? Pos(b) : null, p.RecoveryAnchor is { } a ? Pos(a) : null, p.RequestedOrder.ToString()));
            }
            rows.Add(new JsonObject { ["o"] = pl.Name, ["at"] = op is { } o ? Pos(o) : null, ["pets"] = pets });
        }
        if (rows.Count > 0) Write("tr", new JsonObject { ["rows"] = rows });
    }

    // ---- the owners' view publisher (Kiln.PetsRuntime.Publisher's links), by reflection: lab evidence only ----

    private static readonly Assembly PetsAsm = typeof(Pets).Assembly;
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static object? Runtime(string member) => PetsAsm.GetType("Kiln.PetsRuntime")?.GetProperty(member, Any)?.GetValue(null);

    private static object? Field(object? o, string name) => o?.GetType().GetField(name, Any)?.GetValue(o);

    private static object? Prop(object? o, string name) => o?.GetType().GetProperty(name, Any)?.GetValue(o);

    internal static double? PetsNow() => Prop(Prop(Runtime("Roster"), "World"), "Now") as double?;

    private void Hud()
    {
        if (Field(Runtime("Publisher"), "_links") is not IDictionary links) return;
        var now = PetsNow();
        var seen = new HashSet<CharacterKey>();
        foreach (DictionaryEntry kv in links)
        {
            if (kv.Key is not CharacterKey key || kv.Value is not { } l) continue;
            seen.Add(key);
            var cur = ((ulong)Field(l, "Join")!, (ulong)Field(l, "Token")!, (ulong)Field(l, "Sequence")!, (double)Field(l, "LastSent")!, (bool)Field(l, "Dirty")!);
            if (_hud.TryGetValue(key, out var was) && was == cur) continue;
            _hud[key] = cur;
            var owner = Players.All.FirstOrDefault(p => !p.Entity.IsNone && CharacterData.KeyOf(p.Entity) == key);
            var peer = owner is null ? null : Network.PeerOf(owner.Entity);
            Write("hud", new JsonObject
            {
                ["owner"] = key.ToString(), ["name"] = owner?.Name, ["join"] = cur.Item1.ToString(CultureInfo.InvariantCulture),
                ["token"] = cur.Item2.ToString("x", CultureInfo.InvariantCulture), ["seq"] = cur.Item3, ["lastSent"] = R(cur.Item4),
                ["dirty"] = cur.Item5, ["petsNow"] = now is { } n ? R(n) : null, ["entries"] = Pets.Of(key).Count(p => p.Life != PetLife.Released),
                ["peer"] = peer is null ? null : $"0x{peer.Id:x}", ["peerState"] = peer?.State.ToString(),
            });
        }
        foreach (var gone in _hud.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _hud.Remove(gone);
            Write("hud", new JsonObject { ["owner"] = gone.ToString(), ["gone"] = true });
        }
    }

    private void WatchAttack()
    {
        if (_attackWatch is not { } w) return;
        if (_game > w.Until)
        {
            _attackWatch = null;
            Write("attackwatch", new JsonObject { ["end"] = true });
            return;
        }
        var p = Pets.Get(w.Pet);
        var body = p?.Body ?? EntityRef.None;
        string? target = null;
        string? state = null;
        if (!body.IsNone)
            try
            {
                var c = Creatures.Of(body.Id);
                target = c.Target.Value.ToString(CultureInfo.InvariantCulture);
                state = c.State.ToString();
            }
            catch (Exception e) { target = $"error: {e.Message}"; }
        Write("attackwatch", new JsonObject
        {
            ["pet"] = Short(w.Pet), ["body"] = body.Id.Value, ["want"] = w.Target.Value, ["aiTarget"] = target, ["aiState"] = state,
            ["order"] = p?.RequestedOrder.ToString(), ["requestedTarget"] = p?.RequestedTarget.Id.Value,
            ["targetAlive"] = Combat.TryGetHealth(w.Target, out var h) ? !h.Dead && h.Current > 0 : null,
        });
    }

    // ------------------------------------------------------------------------------------------- events ------

    private void Subscribe()
    {
        _subs.Add(Events.On<PetChangeEvent>(e =>
        {
            var c = e.Change;
            _known[e.Pet] = c.After;
            if (!c.After.Body.IsNone) _bodies[c.After.Body.Id.Value] = e.Pet.ToString();
            var beforeBody = c.Before.Body;
            var o = new JsonObject
            {
                ["ev"] = e.GetType().Name, ["pet"] = Short(e.Pet), ["petId"] = e.Pet.ToString(), ["owner"] = e.Owner.ToString(),
                ["def"] = c.After.Definition, ["cause"] = c.Cause.ToString(), ["op"] = c.RequestId.ToString(),
                ["rev"] = c.After.StateRevision, ["durable"] = c.After.DurableRevision, ["observedStep"] = c.ObservedStep,
                ["before"] = Snap(c.Before), ["after"] = Snap(c.After),
                ["beforeBodyExists"] = beforeBody.IsNone ? null : World.Exists(beforeBody.Id),
                ["afterBodyExists"] = c.After.Body.IsNone ? null : World.Exists(c.After.Body.Id),
            };
            if (!c.After.Body.IsNone && World.Position(c.After.Body.Id) is { } bp) o["bodyAt"] = Pos(bp);
            if (c.After.RecoveryAnchor is { } a) o["anchor"] = Pos(a);
            var owner = OwnerOf(e.Owner);
            if (owner is not null && World.Position(owner.Entity) is { } op)
            {
                o["ownerAt"] = Pos(op);
                if (World.Position(c.After.Body.Id) is { } bp2 && !c.After.Body.IsNone) o["bodyDistance"] = R(bp2.DistanceTo(op));
                if (c.After.RecoveryAnchor is { } a2) o["anchorDistance"] = R(a2.DistanceTo(op));
            }
            if (e is PetSummonedEvent && !c.After.Body.IsNone)
                try { o["faction"] = Creatures.Of(c.After.Body.Id).Faction.ToString(); }
                catch (Exception x) { o["faction"] = $"error: {x.Message}"; }
            if (e is PetReviveStartedEvent && c.After.Definition == Mule) _awaitPreview = true;
            if ((e is PetDownedEvent or PetBodyRetiredEvent or PetBodyLostEvent) && _lastBody.TryGetValue(e.Pet, out var last))
            {
                o["lastBody"] = last.Value;
                o["lastBodyExists"] = World.Exists(last);
            }
            if (!c.After.Body.IsNone) _lastBody[e.Pet] = c.After.Body.Id;
            Write("pet", o);
        }));
        _subs.Add(Events.On<PetDowningEvent>(e => Write("downing", new JsonObject
        {
            ["pet"] = Short(e.Pet.Id), ["cause"] = e.Cause.ToString(), ["outcome"] = e.Outcome.ToString(),
            ["body"] = e.Pet.Body.Id.Value, ["bodyExists"] = e.Pet.Body.IsNone ? null : World.Exists(e.Pet.Body.Id),
            ["rev"] = e.Pet.StateRevision,
        })));
        _subs.Add(Events.On<PetRevivingEvent>(e => Write("reviving", new JsonObject
        {
            ["pet"] = Short(e.Pet.Id), ["op"] = e.RequestId.ToString(), ["cost"] = e.Cost is { } c ? $"{c.Count} x {c.Item}" : null,
            ["canceled"] = e.Canceled,
        }), EventPriority.Monitor));
        _subs.Add(Events.On<PetRecordCommittedEvent>(e =>
        {
            var now = Pets.Get(e.Pet);
            Write("committed", new JsonObject
            {
                ["pet"] = Short(e.Pet), ["captured"] = e.CapturedStateRevision, ["previous"] = e.PreviousDurableRevision,
                ["worldRevision"] = e.Commit.WorldRevision, ["commitRev"] = e.Commit.StateRevision,
                ["nowLife"] = now?.Life.ToString(), ["nowRev"] = now?.StateRevision, ["nowDurable"] = now?.DurableRevision, ["present"] = now is not null,
            });
        }));
        _subs.Add(Events.On<PetControlChangedEvent>(e => Write("lease", new JsonObject
        {
            ["pet"] = Short(e.Pet), ["before"] = e.Before is { } b ? $"{b.EffectiveRole}/{b.State}" : null,
            ["role"] = e.After.RequestedRole.ToString(), ["effective"] = e.After.EffectiveRole.ToString(), ["state"] = e.After.State.ToString(),
            ["canDrive"] = e.After.CanDrive, ["lease"] = e.After.Lease.Value, ["winning"] = e.After.WinningLease.Value,
            ["transition"] = e.After.TransitionSequence, ["reason"] = e.After.Reason,
        })));
        _subs.Add(Events.On<CreatureChangedEvent>(e =>
        {
            var pet = _bodies.GetValueOrDefault(e.Entity.Value);
            if (pet is null)
            {
                // a body set up in this step is not in the roster's snapshots yet: Kiln.Pets' spawn tag tells it
                var o2 = new JsonObject { ["entity"] = e.Entity.Value, ["what"] = e.What, ["origin"] = e.Origin, ["pet"] = null };
                if (e.What == "Faction" && e.Origin == Me && _awaitPreview && _previewStep is null)
                {
                    _previewStep = _step;
                    o2["preview"] = true;
                }
                Write("creature", o2);
                return;
            }
            Write("creature", new JsonObject { ["entity"] = e.Entity.Value, ["what"] = e.What, ["origin"] = e.Origin, ["pet"] = pet[..8] });
        }));
        _subs.Add(Events.On<EntityDeathEvent>(e => Write("death", new JsonObject
        {
            ["victim"] = e.Victim.Value, ["killer"] = e.Killer.Value, ["petBody"] = _bodies.GetValueOrDefault(e.Victim.Value),
            ["isPlayer"] = e.IsPlayer, ["at"] = Pos(e.Position),
        })));
        _subs.Add(Events.On<EntityDropsEvent>(e => Write("drops", new JsonObject
        {
            ["victim"] = e.Victim.Value, ["container"] = e.Container.Value, ["killer"] = e.Killer.Value,
            ["petBody"] = _bodies.GetValueOrDefault(e.Victim.Value), ["at"] = Pos(e.Position),
        })));
        _subs.Add(Events.On<WorldSavingEvent>(e =>
        {
            var pets = new JsonArray();
            foreach (var id in _known.Keys.ToList())
                if (Pets.Get(id) is { } p)
                    pets.Add(new JsonObject
                    {
                        ["pet"] = Short(id), ["life"] = p.Life.ToString(), ["presence"] = p.Presence.ToString(), ["transition"] = p.Transition.ToString(),
                        ["rev"] = p.StateRevision, ["durable"] = p.DurableRevision,
                        ["bodyHealth"] = !p.Body.IsNone && Combat.TryGetHealth(p.Body.Id, out var h) ? h.ToString() : null,
                    });
            Write("saving", new JsonObject { ["reason"] = e.Reason.ToString(), ["revision"] = e.Revision, ["beforeSnapshot"] = e.BeforeSnapshot, ["pets"] = pets });
        }));
        _subs.Add(Events.On<WorldSavedEvent>(e => Write("saved", new JsonObject
        {
            ["reason"] = e.Reason.ToString(), ["success"] = e.Success, ["revision"] = e.Revision, ["ms"] = R(e.Duration.TotalMilliseconds),
        })));
    }

    // ------------------------------------------------------------------------------------------- helpers ------

    internal static JsonNode? R(double v) => double.IsFinite(v) ? JsonValue.Create(Math.Round(v, 3)) : null;

    internal static JsonArray Pos(WorldPosition p) => new(R(p.X), R(p.Y), R(p.Z));

    internal static string Short(PetId id) => id.ToString()[..8];

    private static JsonObject Snap(PetSnapshot s) => new()
    {
        ["life"] = s.Life.ToString(), ["presence"] = s.Presence.ToString(), ["transition"] = s.Transition.ToString(),
        ["order"] = s.RequestedOrder.ToString(), ["target"] = s.RequestedTarget.Id.Value, ["body"] = s.Body.Id.Value,
        ["health"] = s.Health, ["healthMax"] = s.HealthMax, ["wants"] = s.WantsSummoned, ["rev"] = s.StateRevision,
        ["durable"] = s.DurableRevision, ["reviveOp"] = s.ReviveRequest?.ToString(), ["reviveElapsed"] = R(s.ReviveElapsed.TotalSeconds),
    };

    private static Player? OwnerOf(CharacterKey key) =>
        Players.All.FirstOrDefault(p => !p.Entity.IsNone && CharacterData.KeyOf(p.Entity) == key);

    private static (EntityId Who, CharacterKey Key) Caller(CommandContext c)
    {
        if (c.Source.Player.IsNone) throw new CommandException("only a player in the world");
        var key = CharacterData.KeyOf(c.Source.Player);
        if (key.IsNone || key.IsAccountOnly) throw new CommandException("the caller's character is not known");
        return (c.Source.Player, key);
    }

    /// <summary>A pet by its 1-based place in Pets.Of (released tombstones included, as PetsSample counts) or an id prefix.</summary>
    private static PetSnapshot PetOf(CharacterKey key, string sel)
    {
        var all = Pets.Of(key);
        if (int.TryParse(sel, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return all.ElementAtOrDefault(n - 1) ?? throw new CommandException($"no pet {n}");
        return all.FirstOrDefault(p => p.Id.ToString().StartsWith(sel, StringComparison.OrdinalIgnoreCase)) ?? throw new CommandException($"no pet {sel}");
    }

    private static EntityId Entity(string s) =>
        uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0 ? new EntityId(v) : throw new CommandException($"not an entity: {s}");

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : throw new CommandException($"not a number: {s}");

    private static ItemInfo Item(string name) => Items.Lookup(name) ?? throw new CommandException($"no item {name}");

    internal JsonObject Describe(PetSnapshot p, EntityId owner)
    {
        var o = new JsonObject
        {
            ["pet"] = Short(p.Id), ["petId"] = p.Id.ToString(), ["def"] = p.Definition, ["label"] = p.Label, ["life"] = p.Life.ToString(),
            ["presence"] = p.Presence.ToString(), ["transition"] = p.Transition.ToString(), ["order"] = p.RequestedOrder.ToString(),
            ["target"] = p.RequestedTarget.Id.Value, ["wants"] = p.WantsSummoned, ["health"] = p.Health, ["healthMax"] = p.HealthMax,
            ["rev"] = p.StateRevision, ["durable"] = p.DurableRevision, ["body"] = p.Body.Id.Value,
            ["commitRev"] = p.LastCommit?.StateRevision, ["commitWorldRevision"] = p.LastCommit?.WorldRevision,
        };
        var op = World.Position(owner);
        if (op is { } ownerAt) o["ownerAt"] = Pos(ownerAt);
        if (!p.Body.IsNone)
        {
            o["bodyExists"] = World.Exists(p.Body.Id);
            if (World.Position(p.Body.Id) is { } bp)
            {
                o["bodyAt"] = Pos(bp);
                if (op is { } a) o["distance"] = R(bp.DistanceTo(a));
            }
            try
            {
                var c = Creatures.Of(p.Body.Id);
                o["faction"] = c.Faction.ToString();
                o["aiTarget"] = c.Target.Value;
                o["aiState"] = c.State.ToString();
            }
            catch (Exception e) { o["creature"] = $"error: {e.Message}"; }
            if (Combat.TryGetHealth(p.Body.Id, out var h)) o["bodyHealth"] = h.ToString();
        }
        if (p.RecoveryAnchor is { } an)
        {
            o["anchor"] = Pos(an);
            if (op is { } a) o["anchorDistance"] = R(an.DistanceTo(a));
        }
        // the follower (PetState.Session): its limit and catch-ups, by reflection
        if (Field(Runtime("Roster"), "_pets") is IDictionary pets && pets.Contains(p.Id) && Field(pets[p.Id], "Session") is { } f)
        {
            o["limited"] = Prop(f, "Limited") as string;
            o["catchUps"] = Prop(f, "CatchUps") as int?;
        }
        return o;
    }

    private static JsonObject Result(PetResult r) => new()
    {
        ["ok"] = r.Ok, ["reason"] = r.Reason, ["op"] = r.RequestId.ToString(), ["life"] = r.Pet?.Life.ToString(), ["presence"] = r.Pet?.Presence.ToString(),
        ["costs"] = new JsonArray(r.Costs.Select(c => (JsonNode)new JsonObject { ["item"] = c.Item.Value, ["requested"] = c.Requested, ["taken"] = c.Taken, ["committed"] = c.Committed }).ToArray()),
    };

    internal void Later(string what, Func<Task> work) => _ = Run(what, work);

    private async Task Run(string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception e)
        {
            Write("error", new JsonObject { ["where"] = what, ["error"] = e.ToString() });
            Info($"{what}: {e.Message}");
        }
    }

    internal async Task Steps(int n)
    {
        for (var i = 0; i < n; i++) await Context.Scheduler.NextTick();
    }

    internal async Task Wait(double seconds)
    {
        var until = _game + seconds;
        do await Context.Scheduler.NextTick();
        while (_game < until);
    }

    internal async Task<bool> Until(Func<bool> done, double seconds)
    {
        var until = _game + seconds;
        while (!done())
        {
            if (_game >= until) return false;
            await Context.Scheduler.NextTick();
        }
        return true;
    }

    private void DefineMule()
    {
        if (!Capabilities.Is("pets.definitions") || !Capabilities.Is("kiln.items")) return;
        ItemInfo? twigs;
        try { twigs = Items.Lookup("Material_T1_Twigs"); }
        catch (Exception) { return; }
        if (twigs is null) return;
        _muleDefined = true;
        // the sweep's own pet: a short channel and a one-twig cost, so a cancel can be timed against the debit; movement
        // does not cancel it (the bot stands still anyway)
        var r = Pets.Define(new PetDefinition
        {
            Id = Mule, Creature = "game:enemy_wildbeast_wolf_earlygame", Name = "Mule",
            Revive = new PetRevivePolicy
            {
                ChannelDuration = TimeSpan.FromSeconds(0.5), RangeMeters = 8, Cost = new PetCostDefinition(twigs.Id, 1),
                CancelOnMovement = false, CancelOnObservedOwnerDamage = false,
            },
            Limits = new PetLimits { LivePerCharacter = 3 },
        });
        Write("define", new JsonObject { ["id"] = Mule, ["ok"] = r.Ok, ["reason"] = r.Reason });
    }
}
