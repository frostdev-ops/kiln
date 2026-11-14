// SPDX-License-Identifier: MIT
// What creature blocks need while the game runs: brains (the game's AI off for a creature, C# driving it on the tick thread, with a perception of its own,
// since the game's target pass stops with its AI), named pickers and brains for blocks in data files, and effects run by rules.
using System.Numerics;
using System.Text.Json.Nodes;

namespace Kiln;

/// <summary>
/// Your code in place of the game's AI, for each creature whose behaviour names it: <c>Behaviour = new BehaviourBlock { Brain = () =&gt; new MyBrain() }</c>.
/// <see cref="Tick"/> runs on the tick thread every step, with the whole API. The game's decisions stop for that creature, and so does its perception:
/// <see cref="NpcControl"/> sees for it. What the game's combat states did (blocking, dodging, flanking, hit reactions) is yours to do; the body, its
/// sequences and damage run as before. About 4 µs per creature per step.
/// </summary>
public abstract class NpcBrain
{
    /// <summary>Once, when it takes the creature over.</summary>
    public virtual void Start(NpcControl npc) { }

    /// <summary>Every step while the creature lives.</summary>
    public abstract void Tick(NpcControl npc);

    /// <summary>Once, when the creature is gone or the mod unloads.</summary>
    public virtual void Stop(NpcControl npc) { }
}

/// <summary>One creature as a brain sees and drives it. Tick thread.</summary>
public sealed class NpcControl
{
    internal NpcControl(EntityId entity, NpcCreature block)
    {
        Entity = entity;
        Block = block;
        Perception = new NpcPerception(block.Perception);
    }

    internal NpcPerception Perception { get; }

    /// <summary>How this creature sees: its behaviour's <see cref="BehaviourBlock.Perception"/> as it was when the creature was spawned.</summary>
    [ServerOnly, TickThreadOnly]
    public NpcPerceptionPolicy PerceptionPolicy => Perception.Policy;

    /// <summary>What its sight did so far: how many looks, and how each candidate's line of sight ended.</summary>
    [ServerOnly, TickThreadOnly]
    public NpcPerceptionStats PerceptionStats => Perception.Stats;

    internal NpcCreature Block { get; }
    internal readonly List<EntityId> SeenList = [];
    internal long NextLook;

    public EntityId Entity { get; }
    /// <summary>The creature block's name.</summary>
    public string Name => Block.Name;
    /// <summary>The creature as rules see it: health, phase, position.</summary>
    public NpcView View => new(Entity);
    public WorldPosition? Position => World.Position(Entity);
    public bool Alive => World.Exists(Entity) && (Creatures.Of(Entity).Inspect() is not { } i || i.HealthMax <= 0 || i.Health > 0);

    /// <summary>Enemies within its sight, nearest first, looked for four times a second. Its own perception: the game's stops with its AI.</summary>
    public IReadOnlyList<EntityId> Seen => SeenList;

    /// <summary>What it is after: the nearest of <see cref="Seen"/> until you set another. Default when there is none.</summary>
    public EntityId Target { get; set; }

    /// <summary>Metres to <see cref="Target"/>, or null.</summary>
    public double? TargetDistance => Target.IsNone || !World.TryGetPosition(Entity, out var a) || !World.TryGetPosition(Target, out var b) ? null : a.DistanceTo(b);

    /// <summary>Seconds since the brain took over.</summary>
    public double Time { get; internal set; }

    /// <summary>Walks (or runs) toward a place: the body turns and moves as its walk or run state plays. Each step's input lasts that step: call it every tick.</summary>
    public void MoveTo(WorldPosition to, bool run = false)
    {
        if (!World.TryGetPosition(Entity, out var at)) return;
        var d = new Vector3((float)(to.X - at.X), 0, (float)(to.Z - at.Z));
        if (d.LengthSquared() < 0.01f)
        {
            Stop();
            return;
        }
        d = Vector3.Normalize(d);
        NpcInput.Write(Entity, d, d, run ? NpcInput.Run : NpcInput.Walk);
    }

    /// <summary>Stands still.</summary>
    public void Stop() => NpcInput.Write(Entity, Vector3.Zero, null, NpcInput.Zero);

    /// <summary>Turns toward a place where it stands.</summary>
    public void Face(WorldPosition to)
    {
        if (!World.TryGetPosition(Entity, out var at)) return;
        var d = new Vector3((float)(to.X - at.X), 0, (float)(to.Z - at.Z));
        if (d.LengthSquared() > 0.0001f) NpcInput.Write(Entity, Vector3.Zero, Vector3.Normalize(d), NpcInput.Zero);
    }

    /// <summary>Starts one of its attacks by name: its sequence plays, and its hit lands where it lands. False: no such attack, or another is playing.</summary>
    public bool Attack(string name)
    {
        if (!Block.AttackSequences.TryGetValue(name, out var seq)) return false;
        if (Abilities.SequenceOf(Entity) is { Sequence: not 0 }) return false;
        return Abilities.PlaySequence(Entity, seq, abortCurrent: false);
    }

    /// <summary>Holds a state of its animation while no sequence plays (the state input when nothing else sets one): <see cref="AnimState.Idle"/> to let go.</summary>
    public void Pose(AnimState state) => NpcInput.Pose(Entity, state.Hash(Block.Mod));

    /// <summary>Runs an effect block on what it is after (or on <paramref name="on"/>), with this creature as the one who did it. The effect must be one
    /// the creature's rules or this mod's blocks name, so that its program exists: <see cref="Blocks.Find"/>.</summary>
    public void Effect(string effect, EntityId? on = null) => NpcRuntime.RunEffect(Block, effect, Entity, on ?? Target);

    /// <summary>The creature's own changes, as a mod's layers: level, faction, AI values, overrides.</summary>
    public CreatureRef Creature => Creatures.Of(Entity).As(Block.Owner);
}

/// <summary>Where a rule's effect lands.</summary>
public enum NpcEffectOn
{
    /// <summary>On the creature's combat target, from the creature. Without one, nothing happens.</summary>
    Target,
    /// <summary>On the creature itself: an area around it reaches its enemies.</summary>
    Self,
}

/// <summary>What creature blocks add to <see cref="Blocks"/> and <see cref="Act"/>.</summary>
public static class NpcBlocksApi
{
    extension(Act)
    {
        /// <summary>Runs an effect block: on the creature's combat target (it did it), or on itself. Queued to the tick thread like <see cref="Act.Custom"/>.</summary>
        public static RuleAction Effect(EffectRef effect, NpcEffectOn on = NpcEffectOn.Target) => NpcRuntime.EffectAction(effect, on);
    }

    extension(Blocks)
    {
        /// <summary>A brain for blocks in data files to name: <c>"brain": "name"</c>. Call in <c>OnLoad</c>.</summary>
        public static void Brain(string name, Func<NpcBrain> make) => NpcRuntime.Named(NpcRuntime.Brains, name, make);

        /// <summary>A target picker for data files: <c>"pickTarget": "name"</c>.</summary>
        public static void Target(string name, Func<NpcView, EntityId> pick) => NpcRuntime.Named(NpcRuntime.Targets, name, pick);

        /// <summary>An attack picker for data files: <c>"pickAttack": "name"</c>.</summary>
        public static void AttackPicker(string name, Func<NpcView, string?> pick) => NpcRuntime.Named(NpcRuntime.AttackPicks, name, pick);

        /// <summary>A rule condition for data files: <c>{ "custom": "name" }</c>. Runs on the tick thread, as <see cref="Is.Custom"/>.</summary>
        public static void Condition(string name, Func<NpcView, bool> check, double every = 1) => Arsenals.Condition(name, check, every);

        /// <summary>A rule action for data files: <c>{ "custom": "name" }</c>. Runs on the tick thread.</summary>
        public static void Action(string name, Action<NpcView> run) => Arsenals.Action(name, run);

        /// <summary>Spawns a creature block of this mod (or <c>"other-mod:name"</c>) with all its parts. Tick thread.</summary>
        public static Task<EntityId> Spawn(string creature, WorldPosition at, float yawDegrees = 0, CreatureSpawnOptions? options = null)
        {
            var h = (creature.Contains(':') ? Blocks.Find(creature[(creature.IndexOf(':') + 1)..], creature[..creature.IndexOf(':')]) : Blocks.Find(creature)) as CreatureHandle
                    ?? throw new KeyNotFoundException($"no creature block \"{creature}\"");
            return h.Spawn(at, yawDegrees, options);
        }
    }
}

/// <summary>
/// What a creature block registers in the server world: its arsenal, variant and spawn rules. On the tick thread of a server they are written at once
/// (a dedicated server runs OnLoad and data files there). A game client runs OnLoad off the tick thread, and has a server world only while it hosts one:
/// there they wait, and are written on the tick thread of the first server world that runs, before anything of it spawns. A client joined to another
/// server never needs them: the creature's mind is that server's.
/// </summary>
internal static class NpcServerSide
{
    private static readonly object Gate = new();
    private static readonly List<(ModBlocks Mod, string Block, Action Run)> Pending = [];
    private static IDisposable? _tick;

    // where it runs: the tick thread, a game client, a server world running here (tests put their own in)
    internal static Func<bool> OnTickThread = () => Game.IsTickThread;
    internal static Func<bool> IsClient = () => BlocksBridge.IsClient;
    internal static Func<bool> ServerRunning = () => Lifecycle.IsServerRunning;

    /// <summary>Server-world work can run here and now: the tick thread of a server, or of a client that hosts the world that runs.</summary>
    public static bool Now => OnTickThread() && (!IsClient() || ServerRunning());

    /// <summary>How many wait for a server world.</summary>
    public static int Waiting
    {
        get { lock (Gate) return Pending.Count; }
    }

    /// <summary>Runs <paramref name="run"/> now if it can (see <see cref="Now"/>), otherwise at the first step of a server world here.</summary>
    public static void Post(ModBlocks mod, string block, Action run)
    {
        if (Now)
        {
            Run(mod, block, run);
            return;
        }
        lock (Gate)
        {
            Pending.Add((mod, block, run));
            _tick ??= BlocksBridge.AddTick("kiln creature blocks: server world", OnTick);
        }
    }

    /// <summary>A block's server-world part was refused after its Define returned: an error of the mod, by block and field, as a data file's are.</summary>
    public static void Fail(ModBlocks mod, string message)
    {
        lock (mod.Errors) mod.Errors.Add(message);
        mod.ReportDirty = true;
        BlocksBridge.Log(LogLevel.Error, $"blocks: {mod.Id}: {message}");
    }

    internal static void OnTick(ulong step)
    {
        if (!Now) return;   // a client's menu, or its own world joined to another server: nothing of a creature's mind runs here
        List<(ModBlocks Mod, string Block, Action Run)> work;
        lock (Gate)
        {
            work = Pending.ToList();
            Pending.Clear();
        }
        foreach (var (mod, block, run) in work) Run(mod, block, run);
    }

    private static void Run(ModBlocks mod, string block, Action run)
    {
        try { run(); }
        catch (BlockError e) { Fail(mod, e.Message); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException or KeyNotFoundException) { Fail(mod, $"block {block}: {e.Message}"); }
    }
}

/// <summary>What <see cref="Blocks.Define{T}"/> gives back for a creature: its variant, template and arsenal, and a way to spawn it.</summary>
public sealed class CreatureHandle : BlockHandle<CreatureBlock>
{
    internal CreatureHandle(string mod, string name, Guid guid, string gameName, CreatureBlock block, NpcCreature c) : base(mod, name, guid, gameName, block) => Info = c;

    internal NpcCreature Info { get; }
    /// <summary>The kiln.creatures variant every creature of this block is: <c>/creature spawn</c> takes it.</summary>
    public string Variant => Info.Variant;
    /// <summary>The template its creatures are: a game creature's, or a new one for a body of its own.</summary>
    public string Template => Info.Template;
    /// <summary>Its arsenal, when its behaviour has attacks or rules; null otherwise.</summary>
    public Guid? Arsenal => Info.Arsenal;
    /// <summary>The state ids its animation answers to, by name.</summary>
    public IReadOnlyDictionary<string, uint> States => Info.States;

    /// <summary>Spawns one with all its parts. Tick thread.</summary>
    public async Task<EntityId> Spawn(WorldPosition at, float yawDegrees = 0, CreatureSpawnOptions? options = null)
    {
        var e = await Creatures.Spawn(Info.Variant, at, yawDegrees, options);
        NpcRuntime.Spawned(Info, e);
        AiRuntime.Spawned(Info, e);   // npcai
        return e;
    }
}

/// <summary>A creature block as the runtime knows it.</summary>
internal sealed class NpcCreature
{
    public required string Mod;
    public required string Name;
    public required string Variant;
    public required string Template;
    public Guid TemplateGuid;
    public Guid? Arsenal;
    public required Creatures.OwnerKey Owner;
    public Func<NpcBrain>? Brain;
    public double Sight = 30;
    public Faction? Faction;
    /// <summary>How its brain sees (BehaviourBlock.Perception), copied to each creature's control.</summary>
    public NpcPerceptionPolicy Perception = new();
    /// <summary>The mod instance that defined it: its brain's callbacks and its sight run as that mod.</summary>
    public ModContext? Context;
    public Dictionary<string, uint> States = new(StringComparer.Ordinal);
    public Dictionary<string, Guid> AttackSequences = new(StringComparer.Ordinal);
    public Dictionary<string, (Guid Program, NpcEffectOn On)> Effects = new(StringComparer.Ordinal);
}

/// <summary>The game's per-entity input: where a body is told to go. Offsets come from the game's reflection, through a template's own copy of the component.</summary>
internal static class NpcInput
{
    public const uint Zero = 0, Walk = 2, Run = 3;
    private static int _move = -1, _look = -1, _speed = -1, _state = -1, _speedSize = 4;
    private static bool _tried, _warned;

    private static bool Resolve()
    {
        if (_tried) return _move >= 0;
        _tried = true;
        try
        {
            var t = Content.Templates.Get("Enemy_Vukah_Melee");
            if (t == null) return false;
            long Address(string path) => t.GetJson(path)?["address"]?.GetValue<long>() ?? -1;
            var at = Address("ActorInput");
            if (at <= 0) return false;
            int Off(string f) => Address($"ActorInput.{f}") is var a and > 0 ? (int)(a - at) : -1;
            _state = Off("defaultAnimationStateId");
            _move = Off("desiredWorldMoveInput");
            _look = Off("desiredWorldLookDirection");
            _speed = Off("locomotionSpeed");
            if (t.GetJson("ActorInput.locomotionSpeed")?["type"]?.ToString() is { } type && (type.Contains("uint8") || type.Contains("enum8"))) _speedSize = 1;
            BlocksBridge.Log(LogLevel.Info, $"blocks: creature input: state +0x{_state:x}, move +0x{_move:x}, look +0x{_look:x}, speed +0x{_speed:x} ({_speedSize} B)");
        }
        catch (Exception e) when (e is NotSupportedException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            BlocksBridge.Log(LogLevel.Warn, $"blocks: creature input is not known on this build, brains can't move bodies: {e.Message}");
        }
        return _move >= 0;
    }

    /// <summary>The broker's lease each brain-driven body is moved under (NpcRuntime.Spawned); none when creature_control is off.</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, ControlLeaseId> Leases = new();

    public static void Write(EntityId e, Vector3 move, Vector3? look, uint speed)
    {
        // a body under a lease moves only while the broker says this lease drives it (its SteeringAgent removed, the AI held
        // off): Pending, Preempted (a rider), released or lost, nothing is written
        if (Leases.TryGetValue(e.Value, out var lease) && !CreatureControl.CanDriveNow(lease)) return;
        if (!Resolve())
        {
            if (!_warned) BlocksBridge.Log(LogLevel.Warn, "blocks: a brain asked a body to move, and this build's input fields were not found");
            _warned = true;
            return;
        }
        World.TryWrite(e, "ActorInput", _move, move);
        if (look is { } l && _look >= 0) World.TryWrite(e, "ActorInput", _look, l);
        if (_speed >= 0)
        {
            if (_speedSize == 1) World.TryWrite(e, "ActorInput", _speed, (byte)speed);
            else World.TryWrite(e, "ActorInput", _speed, speed);
        }
    }

    public static void Pose(EntityId e, uint state)
    {
        if (Resolve() && _state >= 0) World.TryWrite(e, "ActorInput", _state, state);
    }
}

internal static class NpcRuntime
{
    internal static readonly Dictionary<string, Func<NpcBrain>> Brains = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, Func<NpcView, EntityId>> Targets = new(StringComparer.Ordinal);
    internal static readonly Dictionary<string, Func<NpcView, string?>> AttackPicks = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, NpcCreature> ByVariant = new(StringComparer.Ordinal);
    private static readonly Dictionary<uint, (NpcControl Control, NpcBrain Brain)> Driven = new();
    private static readonly Dictionary<int, (EffectRef Effect, NpcEffectOn On)> PendingEffects = new();
    private static IDisposable? _tick;
    private static long _ticks, _brainTicks;
    private static int _nextEffect = 1;
    private static bool _unleased;

    /// <summary>Steps brains ran, all creatures together; and how many creatures are driven now.</summary>
    internal static (long Ticks, int Driven) Counters
    {
        get { lock (Gate) return (_brainTicks, Driven.Count); }
    }

    internal static void Named<T>(Dictionary<string, T> table, string name, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        lock (Gate) table[name] = value;
    }

    internal static T? Lookup<T>(Dictionary<string, T> table, string name) where T : class
    {
        lock (Gate) return table.TryGetValue(name, out var v) ? v : null;
    }

    // ---- effects from rules ----

    /// <summary>A rule action that runs an effect: a placeholder the creature's compile replaces with the effect's program.</summary>
    internal static RuleAction EffectAction(EffectRef effect, NpcEffectOn on)
    {
        lock (Gate)
        {
            var id = _nextEffect++;
            PendingEffects[id] = (effect, on);
            return new RuleAction(new JsonObject { ["blocksEffect"] = id });
        }
    }

    internal static (EffectRef Effect, NpcEffectOn On)? EffectOf(RuleAction a)
    {
        if (a.Json["blocksEffect"]?.GetValue<int>() is not { } id) return null;
        lock (Gate) return PendingEffects.TryGetValue(id, out var v) ? v : null;
    }

    internal static void RunEffect(NpcCreature c, string effect, EntityId by, EntityId on)
    {
        if (!c.Effects.TryGetValue(effect, out var p)) throw new KeyNotFoundException($"creature {c.Name} has no effect \"{effect}\": name it in one of its rules");
        RunProgram(c, p.Program, p.On, by, on);
    }

    internal static void RunProgram(NpcCreature c, Guid program, NpcEffectOn on, EntityId by, EntityId target)
    {
        if (!World.TryGetPosition(by, out var at)) return;
        if (on == NpcEffectOn.Target)
        {
            if (target.IsNone || !World.Exists(target)) return;
            if (World.TryGetPosition(target, out var tp)) at = tp;
        }
        var options = new ImpactOptions { Root = by, Tag = $"blocks:{c.Mod}:{c.Name}", Configs = on == NpcEffectOn.Target ? new ImpactConfigs { [$"0x{EffectEmitter.CfgTarget:x8}"] = target } : null };
        var t = Abilities.RunProgram(program, at, options);
        if (t.IsFaulted) BlocksBridge.Log(LogLevel.Warn, $"blocks: creature {c.Name}: an effect did not start: {t.Exception?.GetBaseException().Message}");
    }

    // ---- creatures and brains ----

    internal static void Register(NpcCreature c)
    {
        lock (Gate) ByVariant[c.Variant] = c;
        if (c.Brain != null) EnsureTick();
    }

    internal static NpcCreature? Of(string variant)
    {
        lock (Gate) return ByVariant.TryGetValue(variant, out var c) ? c : null;
    }

    /// <summary>A creature of a block appeared (spawned by the block, or found by the poll).</summary>
    internal static void Spawned(NpcCreature c, EntityId e)
    {
        if (c.Brain == null) return;
        lock (Gate)
            if (Driven.ContainsKey(e.Value))
                return;
        NpcBrain brain;
        try { brain = c.Brain(); }
        catch (Exception x)
        {
            BlocksBridge.Log(LogLevel.Error, $"blocks: creature {c.Name}: its brain could not be made: {x.Message}");
            return;
        }
        var ctl = new NpcControl(e, c);
        // The body's movement belongs to kiln.creature_control: a base lease (PetFollow) under Kiln.Blocks' own owner, for the mod
        // that defined the creature. The broker holds the game's AI off (perception included: the target pass checks the same
        // switch) and removes the SteeringAgent that overwrote every input write, so MoveTo walks; it gives both back when the
        // lease ends. Without the broker the AI is switched off as before, and the body cannot walk.
        NpcInput.Leases.TryRemove(e.Value, out _);
        string? why = null;
        if (CreatureControl.Available && Capabilities.Is("creature_control"))
        {
            try
            {
                var r = CreatureControl.AcquireAs("kiln.blocks", c.Context, new EntityRef(e, 0), ControlRole.PetFollow);
                if (r.Ok) NpcInput.Leases[e.Value] = r.Lease;
                else why = r.Reason;
            }
            catch (Exception x) when (x is ArgumentException or KilnUnavailableException or InvalidOperationException) { why = x.Message; }
        }
        else why = Capabilities.Is("creature_control") ? "kiln.creature_control is not loaded" : Capabilities.Why("creature_control");
        if (why != null)
            try
            {
                Creatures.Of(e).As(c.Owner).AiEnabled = false;
                if (!_unleased)
                    BlocksBridge.Log(LogLevel.Warn, $"blocks: creature {c.Name}: no movement lease for {e} ({why}): the game's AI is off, and its brain's MoveTo cannot walk the body");
                _unleased = true;
            }
            catch (Exception x) when (x is NotSupportedException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                BlocksBridge.Log(LogLevel.Error, $"blocks: creature {c.Name}: the game's AI could not be turned off for {e}, so its brain does not run: {x.Message}");
                return;
            }
        lock (Gate) Driven[e.Value] = (ctl, brain);
        EnsureTick();
        var previous = ModContext.CurrentMod;
        if (c.Context is { Alive: true } owner) ModContext.CurrentMod = owner;
        try { brain.Start(ctl); }
        catch (Exception x) { BlocksBridge.Log(LogLevel.Error, $"blocks: creature {c.Name}: its brain's Start threw: {x}"); }
        finally { ModContext.CurrentMod = previous; }
        BlocksBridge.Log(LogLevel.Info, $"blocks: creature {c.Name}: {e} is driven by its brain");
    }

    private static void EnsureTick()
    {
        lock (Gate) _tick ??= BlocksBridge.AddTick("kiln creature brains", OnTick);
    }

    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    internal static void OnTick(ulong step)
    {
        // A creature's mind is the server's: brains run on the tick thread of a server world here (a dedicated server's, or the one a client
        // hosts), never in a client's menu or in its world joined to another server, where the game's state is not this side's to touch.
        if (!NpcServerSide.Now)
        {
            if (NpcServerSide.IsClient() && !NpcServerSide.ServerRunning()) Drop();
            return;
        }
        _ticks++;
        if (step % 40 == 7) Poll();
        List<(NpcControl Control, NpcBrain Brain)> list;
        lock (Gate) list = Driven.Values.ToList();
        var now = Clock.ElapsedMilliseconds;
        foreach (var (ctl, brain) in list)
        {
            if (!World.Exists(ctl.Entity))
            {
                Release(ctl, brain);
                continue;
            }
            // the look and the brain run as the mod that defined the creature (what they ask of Kiln is that mod's)
            var previous = ModContext.CurrentMod;
            if (ctl.Block.Context is { Alive: true } owner) ModContext.CurrentMod = owner;
            try
            {
                if (now >= ctl.NextLook)
                {
                    ctl.NextLook = now + 250;
                    Look(ctl);
                }
                ctl.Time += 1.0 / 24.0;
                brain.Tick(ctl);
                lock (Gate) _brainTicks++;
            }
            catch (Exception x)
            {
                BlocksBridge.Log(LogLevel.Error, $"blocks: creature {ctl.Name}: its brain threw, and is stopped for {ctl.Entity}: {x}");
                Release(ctl, brain);
            }
            finally { ModContext.CurrentMod = previous; }
        }
    }

    /// <summary>The hosted world stopped, its creatures with it: their brains are let go without a Stop (nothing of that world is left to touch).</summary>
    private static void Drop()
    {
        int n;
        lock (Gate)
        {
            n = Driven.Count;
            if (n == 0) return;
            Driven.Clear();
        }
        BlocksBridge.Log(LogLevel.Info, $"blocks: the server world stopped: {n} creature brain(s) let go");
    }

    private static void Release(NpcControl ctl, NpcBrain brain)
    {
        lock (Gate) Driven.Remove(ctl.Entity.Value);
        // the body's lease ends: the broker restores its SteeringAgent and the game's AI (a body that is gone: its lease failed already)
        if (NpcInput.Leases.TryRemove(ctl.Entity.Value, out var lease)) CreatureControl.ReleaseNow(lease);
        var previous = ModContext.CurrentMod;
        if (ctl.Block.Context is { Alive: true } owner) ModContext.CurrentMod = owner;
        try { brain.Stop(ctl); } catch (Exception) { }
        finally { ModContext.CurrentMod = previous; }
    }

    /// <summary>Creatures of a block with a brain that the game spawned, which no block call saw: by their variant, or a template of the block's own.</summary>
    private static void Poll()
    {
        List<NpcCreature> brainy;
        lock (Gate) brainy = ByVariant.Values.Where(c => c.Brain != null).ToList();
        if (brainy.Count == 0) return;
        foreach (var e in World.Find("Enemy01BehaviorState"))
        {
            lock (Gate)
                if (Driven.ContainsKey(e.Value))
                    continue;
            var v = CreatureVariants.Of(e);
            var c = v != null ? brainy.FirstOrDefault(x => x.Variant == v.Id) : null;
            if (c == null && Spawn.Inspect(e)?.Template is { } g) c = brainy.FirstOrDefault(x => x.TemplateGuid == g && x.TemplateGuid != Guid.Empty);
            if (c != null) Spawned(c, e);
        }
    }

    /// <summary>The brain's own perception: enemies within sight by the game's faction rule (equal sides are friends, as are all on the players' side).</summary>
    private static void Look(NpcControl ctl)
    {
        ctl.SeenList.Clear();
        if (!World.TryGetPosition(ctl.Entity, out var at))
        {
            Normalize(ctl);
            return;
        }
        var mine = ctl.Block.Faction ?? SafeFaction(ctl.Entity);
        var found = new List<(EntityId E, double D)>();
        foreach (var p in Players.All)
            if (!p.Entity.IsNone && World.TryGetPosition(p.Entity, out var pp) && pp.DistanceTo(at) <= ctl.Block.Sight && Hostile(mine, Faction.Player))
                found.Add((p.Entity, pp.DistanceTo(at)));
        foreach (var e in Creatures.Near(at, ctl.Block.Sight))
        {
            if (e == ctl.Entity || !World.TryGetPosition(e, out var ep)) continue;
            if (Creatures.Of(e).Inspect() is { HealthMax: > 0, Health: <= 0 }) continue;
            if (Hostile(mine, SafeFaction(e))) found.Add((e, ep.DistanceTo(at)));
        }
        // nearest first, then by id; with Voxels only those with a clear line (NpcPerception): never a fallback to distance alone
        var candidates = found.OrderBy(x => x.D).ThenBy(x => x.E.Value).Select(x => (x.E, World.TryGetPosition(x.E, out var p) ? p : at)).ToList();
        ctl.SeenList.AddRange(ctl.Perception.Look(ctl.Entity, at, candidates, SightOff, Queries.VoxelLineOfSight));
        Normalize(ctl);
    }

    private static string? SightOff() => Capabilities.Is(NpcPerception.Capability) ? null : Capabilities.Why(NpcPerception.Capability);

    /// <summary>The target follows what is seen, on every way out of a look: nothing seen leaves no auto-chosen target alive.</summary>
    private static void Normalize(NpcControl ctl)
    {
        if (ctl.Target.IsNone || !World.Exists(ctl.Target) || !ctl.SeenList.Contains(ctl.Target))
            ctl.Target = ctl.SeenList.Count > 0 ? ctl.SeenList[0] : default;
    }

    private static Faction SafeFaction(EntityId e)
    {
        try { return Creatures.Of(e).Faction; }
        catch (Exception x) when (x is NotSupportedException or KeyNotFoundException or InvalidOperationException or ArgumentException) { return Faction.None; }
    }

    internal static bool Hostile(Faction a, Faction b)
    {
        if (a == b || a == Faction.None || b == Faction.None || a == Faction.Environment || b == Faction.Environment) return false;
        static bool Players(Faction f) => f is Faction.Player or Faction.AutomatedPlayer or Faction.PlayerAlly;
        return !(Players(a) && Players(b));
    }
}
