// SPDX-License-Identifier: MIT
// The acting half of the affix engine (AffixRuntime.cs has the rest): the two stat-change programs, the pass that keeps
// what Kiln holds on each player equal to what the player wears, the stat read, and the capability rows.
using System.Collections.Concurrent;

namespace Kiln;

internal static partial class AffixRuntime
{
    /// <summary>The equipment slots whose item acts: everything but the vanity (look-only) slots.</summary>
    internal static readonly EquipmentSlot[] ActingSlots =
    [
        EquipmentSlot.ArmourHead, EquipmentSlot.ArmourUpperBody, EquipmentSlot.ArmourArms, EquipmentSlot.ArmourLowerBody, EquipmentSlot.ArmourFeet,
        EquipmentSlot.Ring0, EquipmentSlot.Ring1, EquipmentSlot.RangedWeapon, EquipmentSlot.Shield, EquipmentSlot.Arrow, EquipmentSlot.Spell,
        EquipmentSlot.FishingBait, EquipmentSlot.HookShot, EquipmentSlot.Glider, EquipmentSlot.Backpack,
    ];

    /// <summary>What the pass needs from the game, behind one interface, so the pass is tested without one
    /// (managed/tests/Kiln.Blocks.Tests/AffixTests.cs). Wearers and stat changes are stamped entities (<see cref="EntityRef"/>):
    /// an id the game gives to another player or entity later is never taken for the one Kiln knew.</summary>
    internal interface IAffixGame
    {
        /// <summary>Everyone whose worn and held items count, each as the session it is now: the players in game (their
        /// session stamp) and a lab's stand-ins (their spawn stamp).</summary>
        IReadOnlyList<EntityRef> Wearers();

        /// <summary>Every item entity in the wearer's inventories; null when the inventory cannot be read now.</summary>
        IReadOnlyList<ItemInstanceRef>? Carried(EntityId wearer);

        /// <summary>The items that act: in an acting equipment slot, or in hand.</summary>
        IReadOnlyList<ItemInstanceRef> Acting(EntityId wearer);

        /// <summary>Whether the id still belongs to the session (or spawn) the ref was stamped with.</summary>
        bool IsAlive(EntityRef entity);

        /// <summary>Whether any entity has this id now.</summary>
        bool Exists(EntityId id);

        /// <summary>Whether the entity is still the stat change Kiln started: the same id and spawn stamp, and Kiln's Impact
        /// entity template. False once the game ended it, also when its id belongs to another entity now.</summary>
        bool IsHelper(EntityRef helper);

        /// <summary>Whether a stat change can be ended at all here (kiln.spawn's entity destruction is on). Nothing starts
        /// without it: a change that cannot end would stay on the player.</summary>
        bool CanEnd { get; }

        /// <summary>Starts a stat change on the wearer; its entity, stamped. Null when it could not start now, or when its
        /// entity has no spawn stamp (then it is ended at once: a change Kiln could not tell apart later must not run).</summary>
        EntityRef? Start(EntityRef wearer, Stat stat, double value);

        /// <summary>Ends a stat change the pass checked is still Kiln's (<see cref="IsHelper"/>): the game takes the value
        /// back. False when it could not be ended, and still acts.</summary>
        bool End(EntityRef helper);
    }

    /// <summary>The running game: Kiln.Players, Kiln.Items, Kiln.World, Kiln.Abilities, Kiln.Spawn.</summary>
    private sealed class RealGame : IAffixGame
    {
        public IReadOnlyList<EntityRef> Wearers()
        {
            var list = new List<EntityRef>();
            if (Players.Available)
                foreach (var p in Players.All)
                    if (p.InGame && !p.Entity.IsNone) list.Add(((PlayerRef)p).Entity);
            foreach (var e in LabWearers)
                if (Stamp(new EntityId(e)) is { IsNone: false } r) list.Add(r);
            return list;
        }

        public IReadOnlyList<ItemInstanceRef>? Carried(EntityId wearer) =>
            Items.Read(wearer) is { Initialized: true } inventory ? inventory.Slots.Where(s => !s.Ref.IsNone).Select(s => s.Ref).ToList() : null;

        public IReadOnlyList<ItemInstanceRef> Acting(EntityId wearer)
        {
            var list = new List<ItemInstanceRef>();
            void Take(ItemInstanceRef r)
            {
                if (!r.IsNone && !list.Contains(r)) list.Add(r);
            }
            Take(Items.Held(wearer));
            foreach (var slot in ActingSlots) Take(Items.Equipped(wearer, slot));
            return list;
        }

        public bool IsAlive(EntityRef entity) => entity.IsAlive;  // KilnUnavailableException: the pass tries again

        public bool Exists(EntityId id) => World.Exists(id);

        public bool IsHelper(EntityRef helper) =>
            helper.HasSerial && helper.IsAlive && Spawn.Inspect(helper.Id) is { Template: { } t } && t == Abilities.GenericImpactTemplate;

        public bool CanEnd => Spawn.Available && Capabilities.Get(DestroyCapability).State == CapabilityState.Available;

        public EntityRef? Start(EntityRef wearer, Stat stat, double value)
        {
            if (World.Position(wearer.Id) is not { } at) return null;  // not in the world yet: the next pass tries again
            EntityId helper;
            try
            {
                helper = Abilities.RunProgram(stat.Whole ? _modWhole : _modShare, at, new ImpactOptions
                {
                    Tag = "kiln-affix",
                    Configs = new ImpactConfigs
                    {
                        [CfgTarget] = wearer.Id,
                        [CfgAttr] = stat.Attribute.ToByteArray(),
                        [CfgOp] = new[] { (byte)(stat.Subtract ? 1 : 0) },
                        [CfgValue] = stat.Whole ? (int)value : (float)value,
                    },
                }).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Warn($"a stat change for {wearer} could not start: {e.Message}");
                return null;
            }
            var stamped = Stamp(helper);
            if (stamped.HasSerial) return stamped;
            // Kiln could not tell this entity apart from a later one with its id: it must not run. Its id is surely its own now.
            try
            {
                Spawn.Destroy(helper);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or SpawnException)
            {
                Backend.Current.Log(0, LogLevel.Error, $"affixes: a stat change ({helper}) without a spawn stamp could not be ended: {e.Message}");
            }
            Warn($"a stat change's entity {helper} has no spawn stamp on this build: no stat change starts");
            return null;
        }

        public bool End(EntityRef helper)
        {
            try
            {
                Spawn.Destroy(helper.Id);  // true: destroyed now; false: not found, so gone already. Either way it acts no more
                return true;
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NotSupportedException or SpawnException)
            {
                Warn($"a stat change could not be ended ({helper}): {e.Message}; it is tried again, less often each time");
                return false;
            }
        }

        private static EntityRef Stamp(EntityId e)
        {
            try
            {
                return EntityRef.Of(e);
            }
            catch (KilnUnavailableException)
            {
                return EntityRef.None;
            }
        }
    }

    private static readonly IAffixGame Real = new RealGame();

    /// <summary>The sigdb feature the stat changes' teardown needs: kiln.spawn's entity destruction.</summary>
    internal const string DestroyCapability = "spawn.destroy";

    /// <summary>Stat changes that could not be ended, over all wearers and wearers gone: at this many, no new stat change
    /// starts for anyone (one error line in kiln.log).</summary>
    internal const int MaxStuck = 64;

    /// <summary>A change that could not be ended is tried again after 2, 4, 8 ... passes, at most this many apart.</summary>
    internal const int MaxRetryPasses = 64;

    private sealed class Applied
    {
        public required string Affix;
        public required Stat Stat;
        public required double Value;
        public required uint Item;

        /// <summary>The session it acts on: its program's target is this id, so it must never end while the id belongs to
        /// another session (the revert would take the value from that one).</summary>
        public required EntityRef Wearer;

        /// <summary>Its entity as Kiln started it (id and spawn stamp).</summary>
        public required EntityRef Helper;

        /// <summary>Failed or deferred ends so far, and the pass from which it is tried again.</summary>
        public int Tries;

        public ulong NextTry;

        /// <summary>Deferred because its wearer's id is another session's now.</summary>
        public bool WaitsForId;
    }

    /// <summary>One stat change Kiln wants on a wearer: per affix and stat (a roll acts by the stat it names), and per item
    /// unless the affix does not stack (item 0).</summary>
    private readonly record struct Slot(string Affix, string Stat, uint Item);

    private sealed class Wearer
    {
        public readonly Dictionary<Slot, Applied> Held = new();

        /// <summary>Stat changes that are no longer wanted but could not be ended: they still act. They are tried again,
        /// less often each time; while there is one, the wearer gets no new change (one that is wanted again as it is is
        /// taken back).</summary>
        public readonly Dictionary<Slot, Applied> Stuck = new();

        public bool Noted;
    }

    /// <summary>Wearers by session: a player who leaves and one who gets the same entity id are two wearers.</summary>
    private static readonly Dictionary<EntityRef, Wearer> Wearers = new();

    /// <summary>Stat changes of wearers who are gone, not ended yet: tried again until their entity is gone, never while
    /// their wearer's id is another session's.</summary>
    private static readonly List<Applied> Orphans = [];

    private static readonly HashSet<uint> LabWearers = new();
    private static readonly Dictionary<uint, (TaskCompletionSource<double?> Done, bool Whole, ulong Until)> Probes = new();
    private static volatile bool _dirty;
    private static ulong _step, _passes, _started_, _ended, _restarted, _failedEnds, _endTries;
    private static Guid _modWhole, _modShare, _probeWhole, _probeShare;
    private static bool _programsBuilt, _warned, _noEndNoted, _capNoted;

    /// <summary>Times kiln.log was told a wearer gets no new change while one of its own could not be ended, and that no
    /// one does while too many could not (the tests read them).</summary>
    internal static int BlockedNotes, CapNotes;
    private static readonly HashSet<string> NotedStats = new(StringComparer.Ordinal);
    private static (CapabilityState, CapabilityReason, string)? _shownRolls, _shownEffects;
    private static long _shownEpoch = -1;  // the registry binding the shown rows were reported under

    private static string Cfg(string name) => $"0x{BlockIds.Fnv(System.Text.Encoding.UTF8.GetBytes(name)):x8}";

    private static readonly string CfgTarget = Cfg("kilnaffix.Target"), CfgAttr = Cfg("kilnaffix.Attr"), CfgOp = Cfg("kilnaffix.Op"), CfgValue = Cfg("kilnaffix.Value");

    private static uint CfgId(string name) => BlockIds.Fnv(System.Text.Encoding.UTF8.GetBytes(name));

    internal static void EnsureStarted()
    {
        if (_started) return;
        lock (Gate)
        {
            if (_started) return;
            _started = true;
        }
        var outer = ModContext.CurrentMod;
        ModContext.CurrentMod = null;  // Kiln's own tick and rows, not the calling mod's
        try
        {
            ServiceTick.Add("kiln affixes", Tick);
            // The stat read's answer comes as a signal. kiln.abilities starts delivering signals a step after the first
            // subscriber, so the subscription is made here, long before the first read (in the lab the first read's
            // answer was lost when it was made at that read).
            if (Abilities.Available) Events.On<ImpactSignalEvent>(OnSignal);
            RefreshCapabilities();
        }
        finally
        {
            ModContext.CurrentMod = outer;
        }
    }

    // ------------------------------------------------------------------------------------------- the programs ----

    /// <summary>The stat-change program: applyAttributeValue on the target in its code, then it stays alive; the game runs
    /// its shutdown code, the matching revert, when its entity is destroyed.</summary>
    private static ProgramBuilder ModifierProgram(bool whole)
    {
        var b = new ProgramBuilder(whole ? "affix.modify.whole" : "affix.modify.share") { Stable = true };
        var type = whole ? ImpactType.Sint32 : ImpactType.Float;
        var target = b.V("target", ImpactType.Entity, default, CfgId("kilnaffix.Target"));
        var attr = b.V("attr", ImpactType.Attribute, default, CfgId("kilnaffix.Attr"));
        var op = b.V("op", ImpactType.MathOp, default, CfgId("kilnaffix.Op"));
        var storage = b.V("storage", type);
        var sign = b.V("sign", ImpactType.Bool);
        var value = b.V("value", type, default, CfgId("kilnaffix.Value"));
        b.BeginCode();
        b.Call("applyAttributeValue", "modify", attr, op, storage, sign, target, null, value);
        b.Pop();
        var idle = b.NewLabel();
        b.Mark(idle);
        b.Yield();
        b.Goto(idle);
        b.Halt();
        b.BeginShutdown();
        b.Call("revertApplyAttributeValue", "modify", attr, op, storage, sign, target, null, value);
        b.Pop();
        b.Halt();
        return b;
    }

    /// <summary>The stat read: fetchOptionalAttribute into a variable, sent back through kiln.Signal, then the entity goes.</summary>
    private static ProgramBuilder ProbeProgram(bool whole)
    {
        var b = new ProgramBuilder(whole ? "affix.probe.whole" : "affix.probe.share") { Stable = true };
        var target = b.V("target", ImpactType.Entity, default, CfgId("kilnaffix.Target"));
        var attr = b.V("attr", ImpactType.Attribute, default, CfgId("kilnaffix.Attr"));
        var value = b.V("value", whole ? ImpactType.Sint32 : ImpactType.Float);
        var tag = b.V("tag", ImpactType.Uint32, BitConverter.GetBytes(ProbeTag));
        b.BeginCode();
        b.Call("fetchOptionalAttribute", null, value, attr, target);
        b.Pop();
        b.Call("kiln.Signal", null, value, tag);
        b.Pop();
        b.DestroySelf();
        b.Halt();
        b.BeginShutdown();
        b.Halt();
        return b;
    }

    /// <summary>Builds the four programs once and hands them to kiln.assets, from where kiln.abilities registers them in
    /// every server world. True once the running world's registry has them.</summary>
    private static bool ProgramsReady()
    {
        if (!Abilities.Available || !Assets.Available) return false;
        if (!_programsBuilt)
        {
            var owner = BlocksBridge.ServiceOwner(Affixes.DataMod);
            Guid Make(ProgramBuilder b)
            {
                var (guid, blob) = b.Build(Affixes.DataMod);
                Assets.RegisterAs(owner, new AssetKey(guid, ProgramType), blob);
                return guid;
            }
            _modWhole = Make(ModifierProgram(true));
            _modShare = Make(ModifierProgram(false));
            _probeWhole = Make(ProbeProgram(true));
            _probeShare = Make(ProbeProgram(false));
            _programsBuilt = true;
            Abilities.Programs.Refresh();
        }
        if (Abilities.Programs.Info(Abilities.Programs.IdOf(_modWhole)) is not null && Abilities.Programs.Info(Abilities.Programs.IdOf(_probeShare)) is not null) return true;
        Abilities.Programs.Refresh();  // the registry of a world that just started takes them now
        return Abilities.Programs.Info(Abilities.Programs.IdOf(_modWhole)) is not null && Abilities.Programs.Info(Abilities.Programs.IdOf(_probeShare)) is not null;
    }

    // ---------------------------------------------------------------------------------------------- the pass ----

    private static void Tick(in TickInfo tick)
    {
        _step = tick.Step;
        if (Probes.Count > 0) ExpireProbes();
        if (!Lifecycle.IsServerRunning)
        {
            // the world is gone, and with it every entity: nothing is held on anybody any more
            if (Wearers.Count > 0) Wearers.Clear();
            Orphans.Clear();
            return;
        }
        if (tick.Step % CapsEverySteps == 0) RefreshCapabilities();
        if (!_dirty && tick.Step % PollEverySteps != 0) return;
        _dirty = false;
        try
        {
            Pass();
        }
        catch (KilnUnavailableException e)
        {
            Warn($"the pass could not read the game ({e.Capability}: {e.Reason}); it tries again");
        }
    }

    private static void Warn(string text)
    {
        if (_warned) return;
        _warned = true;
        Backend.Current.Log(0, LogLevel.Warn, "affixes: " + text);
    }

    /// <summary>The stat a roll acts on: the one it names (<see cref="RolledAffix.Stat"/>, the stat players' games show
    /// with it), not what the affix means now. An affix defined again with another stat leaves its earlier rolls their
    /// meaning; new rolls take the new one. Null when Kiln cannot name the roll's stat (an attribute of a mod's own that the
    /// affix no longer uses): the roll then does nothing.</summary>
    internal static Stat? StatOfRoll(RolledAffix roll, AffixDef def) =>
        def.Block.Stat.Key == roll.Stat ? def.Block.Stat : Stat.All.FirstOrDefault(s => s.Key == roll.Stat);

    /// <summary>What <paramref name="wearer"/> should hold now: one stat change per affix of each acting item, and for an
    /// affix that does not stack one with the highest value (per stat).</summary>
    private static Dictionary<Slot, (Stat Stat, double Value)> Desired(IAffixGame game, EntityId wearer,
        Dictionary<uint, (ItemInstanceRef Ref, IReadOnlyList<RolledAffix> Rolls)> affixed)
    {
        var want = new Dictionary<Slot, (Stat, double)>();
        if (affixed.Count == 0) return want;
        // one read says whether the wearer carries anything with affixes at all; only then are the slots asked
        if (game.Carried(wearer) is not { } carried) return want;
        if (!carried.Any(r => affixed.TryGetValue(r.Entity.Id.Value, out var a) && a.Ref == r)) return want;
        foreach (var item in game.Acting(wearer))
        {
            if (!affixed.TryGetValue(item.Entity.Id.Value, out var found) || found.Ref != item) continue;
            foreach (var roll in found.Rolls)
            {
                if (FindAffix(roll.Affix) is not { } def || !double.IsFinite(roll.Value)) continue;  // its mod is not loaded: it waits
                if (StatOfRoll(roll, def) is not { } stat)
                {
                    bool first;
                    lock (Gate) first = NotedStats.Add($"{roll.Affix}|{roll.Stat}");
                    if (first) Backend.Current.Log(0, LogLevel.Warn, $"affixes: a roll of {roll.Affix} names the stat \"{roll.Stat}\", which the affix does not use any more and Kiln does not know: it does nothing");
                    continue;
                }
                var value = stat.Whole ? Math.Round(roll.Value) : roll.Value;
                if (value == 0) continue;
                if (def.Block.Stacks) want[new Slot(def.Id, stat.Key, item.Entity.Id.Value)] = (stat, value);
                else if (!want.TryGetValue(new Slot(def.Id, stat.Key, 0), out var have) || Math.Abs(value) > Math.Abs(have.Item2))
                    want[new Slot(def.Id, stat.Key, 0)] = (stat, value);
            }
        }
        return want;
    }

    private static void Pass()
    {
        if (!ProgramsReady()) return;
        var affixed = new Dictionary<uint, (ItemInstanceRef, IReadOnlyList<RolledAffix>)>();
        foreach (var (item, data) in InstanceData.All(Key))
            if (data.Rolls.Count > 0) affixed[item.Entity.Id.Value] = (item, ToRolled(data));
        Pass(Real, affixed);
    }

    /// <summary>One pass: makes what is held on every wearer equal to what the wearer's acting items say. A stat change
    /// starts only where one can be ended (<see cref="IAffixGame.CanEnd"/>). One that could not be ended is kept and tried
    /// again, less often each time; while a wearer has one, that wearer gets no new change, and while
    /// <see cref="MaxStuck"/> are kept nobody does, so a bonus never stays twice and the cleanup stays bounded. A wearer is
    /// a session: a change is never ended while its wearer's id is another session's, nor taken for another session's.</summary>
    internal static void Pass(IAffixGame game, Dictionary<uint, (ItemInstanceRef Ref, IReadOnlyList<RolledAffix> Rolls)> affixed)
    {
        _passes++;
        var wearers = game.Wearers();
        var present = new HashSet<EntityRef>(wearers);
        // a session that is over holds nothing: its changes end (their revert finds no entity and does nothing), never while
        // its id is another session's (the revert would take the value from that one): they wait as orphans
        foreach (var who in Wearers.Keys.Where(k => !present.Contains(k)).ToArray())
        {
            var gone = Wearers[who];
            Wearers.Remove(who);
            var before = Snapshot(gone);
            foreach (var a in gone.Held.Values.Concat(gone.Stuck.Values))
            {
                a.NextTry = _passes;
                Orphans.Add(a);
            }
            Announce(who.Id, before, []);
        }
        Orphans.RemoveAll(a => Settle(game, a, orphan: true));
        var canEnd = game.CanEnd;
        if (!canEnd && !_noEndNoted)
        {
            _noEndNoted = true;
            Backend.Current.Log(0, LogLevel.Warn, $"affixes: no stat change starts: they could not be ended here ({DestroyCapability} is off)");
        }
        foreach (var wearer in wearers)
        {
            var want = Desired(game, wearer.Id, affixed);
            Wearers.TryGetValue(wearer, out var state);
            if (want.Count == 0 && state is null) continue;
            state ??= Wearers[wearer] = new Wearer();
            var before = Snapshot(state);
            var changed = false;
            foreach (var (at, a) in state.Stuck.ToArray())
            {
                if (want.TryGetValue(at, out var w) && w.Value == a.Value && ReferenceEquals(w.Stat, a.Stat) && game.IsHelper(a.Helper))
                {
                    state.Stuck.Remove(at);  // wanted again as it is and still Kiln's: it acts, so it is held again (never a second one)
                    state.Held[at] = a;
                    changed = true;
                }
                else if (Settle(game, a, orphan: false))
                {
                    state.Stuck.Remove(at);
                    changed = true;
                }
            }
            foreach (var (at, a) in state.Held.ToArray())
            {
                if (!game.IsHelper(a.Helper))
                {
                    // the game ended the entity (and with it ran the revert), or its id is another entity's now: Kiln has
                    // nothing of its own there to end; if it is still wanted it starts again below
                    state.Held.Remove(at);
                    _restarted++;
                    changed = true;
                    continue;
                }
                if (want.TryGetValue(at, out var w) && w.Value == a.Value && ReferenceEquals(w.Stat, a.Stat)) continue;
                state.Held.Remove(at);
                a.NextTry = _passes;
                if (!Settle(game, a, orphan: false)) state.Stuck[at] = a;  // it still acts: kept, tried again
                changed = true;
            }
            if (want.Keys.Any(k => !state.Held.ContainsKey(k)) && Admits(canEnd, state, wearer))
                foreach (var (at, w) in want)
                {
                    if (state.Held.ContainsKey(at)) continue;
                    if (game.Start(wearer, w.Stat, w.Value) is not { } helper) continue;
                    _started_++;
                    state.Held[at] = new Applied { Affix = at.Affix, Stat = w.Stat, Value = w.Value, Item = at.Item, Wearer = wearer, Helper = helper };
                    changed = true;
                }
            if (state.Stuck.Count == 0) state.Noted = false;
            var now = Snapshot(state);
            if (state.Held.Count == 0 && state.Stuck.Count == 0) Wearers.Remove(wearer);
            if (changed && !before.SequenceEqual(now)) Announce(wearer.Id, before, now);
        }
    }

    /// <summary>Whether a new stat change may start on <paramref name="wearer"/>: one can be ended here, none of the
    /// wearer's own is stuck (a new one would add to what still acts), and fewer than <see cref="MaxStuck"/> are stuck in
    /// all. Says once in kiln.log why not.</summary>
    private static bool Admits(bool canEnd, Wearer state, EntityRef wearer)
    {
        if (!canEnd) return false;
        if (state.Stuck.Count > 0)
        {
            if (!state.Noted)
            {
                state.Noted = true;
                BlockedNotes++;
                Backend.Current.Log(0, LogLevel.Warn, $"affixes: {wearer} gets no new stat change until the {state.Stuck.Count} Kiln could not end are gone " +
                                                      "(they still act; Kiln keeps trying)");
            }
            return false;
        }
        var stuck = StuckCount();
        if (stuck >= MaxStuck)
        {
            if (!_capNoted)
            {
                _capNoted = true;
                CapNotes++;
                Backend.Current.Log(0, LogLevel.Error, $"affixes: {stuck} stat changes could not be ended: no new stat change starts for anyone until " +
                                                       $"fewer than {MaxStuck} are left (Kiln keeps trying; a restart of the server clears them)");
            }
            return false;
        }
        _capNoted = false;
        return true;
    }

    private static int StuckCount() => Wearers.Values.Sum(w => w.Stuck.Count) + Orphans.Count;

    /// <summary>Ends a change that is no longer wanted, when its turn has come. True: it acts no more (ended now, or the
    /// game ended it, or its id is another entity's now: then there is nothing of Kiln's to end). False: it is kept and
    /// tried again later, after 2, 4, ... passes; an orphan also waits while its wearer's id is another session's.</summary>
    private static bool Settle(IAffixGame game, Applied a, bool orphan)
    {
        if (_passes < a.NextTry) return false;
        if (!game.IsHelper(a.Helper)) return true;
        if (orphan && game.Exists(a.Wearer.Id) && !game.IsAlive(a.Wearer))
        {
            a.WaitsForId = true;
            Defer(a);
            return false;
        }
        a.WaitsForId = false;
        _endTries++;
        if (game.End(a.Helper))
        {
            _ended++;
            return true;
        }
        _failedEnds++;
        Defer(a);
        return false;
    }

    private static void Defer(Applied a)
    {
        a.Tries++;
        a.NextTry = _passes + (ulong)Math.Min(1 << Math.Min(a.Tries, 10), MaxRetryPasses);
    }

    /// <summary>What acts on the wearer: what Kiln holds, and what it could not end yet.</summary>
    private static IReadOnlyList<ActiveAffix> Snapshot(Wearer w) =>
        w.Held.Values.Concat(w.Stuck.Values).OrderBy(a => a.Affix, StringComparer.Ordinal).ThenBy(a => a.Stat.Key, StringComparer.Ordinal).ThenBy(a => a.Item)
            .Select(a => new ActiveAffix(a.Affix, a.Stat.Key, a.Value, new EntityId(a.Item))).ToList();

    private static void Announce(EntityId wearer, IReadOnlyList<ActiveAffix> before, IReadOnlyList<ActiveAffix> now)
    {
        Backend.Current.Log(0, LogLevel.Debug, $"affixes: {wearer} now holds {(now.Count == 0 ? "nothing" : string.Join(", ", now.Select(a => $"{a.Affix} {a.Stat} {a.Value}")))}");
        if (Events.HasSubscribers<AffixesAppliedEvent>()) Events.Post(new AffixesAppliedEvent(wearer, before, now));
    }

    /// <summary>The tests' reset: nothing is held, defined or counted.</summary>
    internal static void ResetForTests()
    {
        Wearers.Clear();
        Orphans.Clear();
        LabWearers.Clear();
        lock (Gate)
        {
            Defs.Clear();
            Pools.Clear();
            NotedStats.Clear();
        }
        // the same definitions as blocks (one registry): a test's mod ids come back in the next test
        foreach (var mod in Blocks.AllMods)
            foreach (var (name, handle) in mod.Handles)
                if (handle is AffixHandle or AffixPoolHandle) mod.Handles.TryRemove(name, out _);
        _passes = _started_ = _ended = _restarted = _failedEnds = _endTries = 0;
        _noEndNoted = _capNoted = false;
        BlockedNotes = CapNotes = 0;
    }

    /// <summary>What acts on the entity's current session (an earlier session with the same id is gone from the list at the
    /// next pass).</summary>
    internal static IReadOnlyList<ActiveAffix> ActiveOf(EntityId wearer) =>
        Wearers.Where(w => w.Key.Id == wearer).OrderByDescending(w => w.Key.Serial).Select(w => Snapshot(w.Value)).FirstOrDefault() ?? [];

    internal static void LabTrack(EntityId wearer, bool on)
    {
        EnsureStarted();
        if (on) LabWearers.Add(wearer.Value);
        else LabWearers.Remove(wearer.Value);
        _dirty = true;
    }

    internal static string Describe()
    {
        int defs, pools;
        lock (Gate) (defs, pools) = (Defs.Count, Pools.Count);
        return $"affixes: {defs} affix(es), {pools} pool(s) defined; {_passes} pass(es), {Wearers.Count} wearer(s) with {Wearers.Values.Sum(w => w.Held.Count)} stat change(s) held; " +
               $"{_started_} started, {_ended} ended, {_failedEnds} end(s) failed and retried ({_endTries} tries), {StuckCount()} not ended yet " +
               $"({Orphans.Count(a => a.WaitsForId)} waiting for their wearer's id to be free); {_restarted} found ended by the game";
    }

    // ------------------------------------------------------------------------------------------ the stat read ----

    internal static Task<double?> Probe(EntityId entity, Stat stat)
    {
        if (entity.IsNone) throw new ArgumentException("no entity", nameof(entity));
        EnsureStarted();
        if (!Lifecycle.IsServerRunning || !ProgramsReady())
            throw new KilnUnavailableException(Affixes.EffectsCapability, EffectsNow().Item3 is { Length: > 0 } why ? why : "the server world's Impact programs are not ready");
        var at = World.Position(entity) ?? throw new ArgumentException($"entity {entity} has no position", nameof(entity));
        var helper = Abilities.RunProgram(stat.Whole ? _probeWhole : _probeShare, at, new ImpactOptions
        {
            Tag = "kiln-affix-probe",
            Configs = new ImpactConfigs { [CfgTarget] = entity, [CfgAttr] = stat.Attribute.ToByteArray() },
        }).GetAwaiter().GetResult();
        var done = new TaskCompletionSource<double?>();
        Probes[helper.Value] = (done, stat.Whole, _step + ProbeTimeoutSteps);
        return done.Task;
    }

    private static void OnSignal(ImpactSignalEvent e)
    {
        if (e.Tag != ProbeTag || !Probes.Remove(e.Impact.Self.Value, out var p)) return;
        p.Done.TrySetResult(e.Value.Length >= 4 ? p.Whole ? e.AsInt : Math.Round((double)e.AsFloat, 6) : null);
    }

    private static void ExpireProbes()
    {
        foreach (var (id, p) in Probes.ToArray())
        {
            if (_step < p.Until && Lifecycle.IsServerRunning) continue;
            Probes.Remove(id);
            p.Done.TrySetResult(null);
        }
    }

    // ------------------------------------------------------------------------------------------- capabilities ----

    private static (CapabilityState, CapabilityReason, string) RollsNow()
    {
        var data = Capabilities.Get("saves.instance_data");
        return data.State switch
        {
            CapabilityState.Available => (CapabilityState.Available, CapabilityReason.None, ""),
            CapabilityState.Pending => (CapabilityState.Pending, CapabilityReason.Session, $"needs saves.instance_data: {data.ReasonText}"),
            _ => (CapabilityState.Unavailable, data.Reason == CapabilityReason.Unknown ? CapabilityReason.ServiceMissing : data.Reason,
                $"needs saves.instance_data: {(data.ReasonText.Length > 0 ? data.ReasonText : "Kiln.Saves has not started, or kiln.saves is not loaded")}"),
        };
    }

    private static (CapabilityState, CapabilityReason, string) EffectsNow()
    {
        var rolls = RollsNow();
        if (rolls.Item1 == CapabilityState.Unavailable) return (rolls.Item1, rolls.Item2, rolls.Item3.Replace("needs saves", $"needs {Affixes.Capability}, which needs saves"));
        foreach (var need in new[] { "abilities.programs", "abilities.natives", "items.instances" })
        {
            var row = Capabilities.Get(need);
            if (row.State == CapabilityState.Unavailable)
                return (CapabilityState.Unavailable, row.Reason == CapabilityReason.Unknown ? CapabilityReason.ServiceMissing : row.Reason,
                    $"needs {need}: {(row.ReasonText.Length > 0 ? row.ReasonText : "not on this build")}");
        }
        if (!Abilities.Available || !Assets.Available) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "needs kiln.abilities and kiln.assets");
        // a stat change ends when kiln.spawn destroys its entity: without that, none may start (it would stay on the player)
        if (!Spawn.Available) return (CapabilityState.Unavailable, CapabilityReason.ServiceMissing, "needs kiln.spawn: a stat change ends when kiln.spawn destroys its entity");
        var destroy = Capabilities.Get(DestroyCapability);
        if (destroy.State != CapabilityState.Available)
            return (CapabilityState.Unavailable, destroy.Reason == CapabilityReason.Unknown ? CapabilityReason.SymbolMissing : destroy.Reason,
                $"needs {DestroyCapability}: a stat change ends when its entity is destroyed{(destroy.ReasonText.Length > 0 ? $" ({destroy.ReasonText})" : "")}");
        if (rolls.Item1 == CapabilityState.Pending) return rolls;
        if (!Lifecycle.IsServerRunning) return (CapabilityState.Pending, CapabilityReason.Session, "waits for a server world: the stat changes are Impact programs of the running world");
        if (!Game.IsTickThread) return _shownEffects ?? (CapabilityState.Pending, CapabilityReason.Session, "decided on the tick thread");
        return ProgramsReady()
            ? (CapabilityState.Available, CapabilityReason.None, "")
            : (CapabilityState.Pending, CapabilityReason.Session, "the world's Impact program registry has not taken Kiln's affix programs yet");
    }

    /// <summary>Reports the two rows when they changed. Never throws.</summary>
    internal static void RefreshCapabilities()
    {
        try
        {
            var epoch = Capabilities.CurrentBinding.Epoch;
            if (_shownEpoch != epoch)
            {
                _shownRolls = _shownEffects = null;  // a new registry binding has neither row
                _shownEpoch = epoch;
            }
            var rolls = RollsNow();
            if (_shownRolls != rolls)
            {
                _shownRolls = rolls;
                Capabilities.Report(Affixes.Capability, rolls.Item1, rolls.Item2, rolls.Item3);
            }
            var effects = EffectsNow();
            if (_shownEffects != effects)
            {
                _shownEffects = effects;
                Capabilities.Report(Affixes.EffectsCapability, effects.Item1, effects.Item2, effects.Item3);
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            Warn($"capability rows were not reported: {e.Message}");
        }
    }
}
