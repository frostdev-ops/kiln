// SPDX-License-Identifier: MIT
// The game's own taming as mechanism (docs/re/taming.md): a tameable wild animal eats a bait of its favourite template and
// is petted while it eats; each petting is one step; at its required steps the game makes it a farm animal. Kiln reads it
// (NativeTaming.Read), reports it (NativeTamingProgressEvent, NativeTamedEvent, NativeBaitEatenEvent) and lets a mod drive
// it from the server (PlaceBait: the animal's own bait; PetStep: the animal's own pet sequence, the step a player's petting
// makes). Nothing of the game's is hooked or erased: the animal keeps its own offers, and the policy (who may tame what,
// what it costs) stays the mod's. Who tamed an animal the game does not record: see NativeTamingState.TamedBy.
using System.Buffers.Binary;

namespace Kiln;

/// <summary>Where a wild animal is in the game's taming. Since 0.2.0-dev.32.</summary>
public enum NativeTamingPhase
{
    /// <summary>Not after a bait (roaming, fleeing, idle).</summary>
    Wild = 0,
    /// <summary>Walking to a bait of its favourite food (the game's state 14).</summary>
    PursuingBait = 1,
    /// <summary>Eating it (state 15): a petting now is a step.</summary>
    EatingBait = 2,
    /// <summary>Tamed: the game made it a farm animal (IsTame).</summary>
    Tamed = 3,
    /// <summary>Dead.</summary>
    Dead = 4,
}

/// <summary>One animal's taming as the game has it now. Since 0.2.0-dev.32.</summary>
/// <param name="Creature">The animal.</param>
/// <param name="Tameable">Its template allows taming (the Wildlife template's Tameable feature).</param>
/// <param name="Progress">Steps so far (the game's byte; it never goes above <paramref name="Required"/>).</param>
/// <param name="Required">Steps to the farm transformation (its template's).</param>
/// <param name="Phase">Where it is.</param>
/// <param name="Bait">The bait it is after or eating; none otherwise.</param>
/// <param name="BaitTemplate">Its favourite bait's template (what <see cref="NativeTaming.PlaceBait"/> places); empty
/// when it has none.</param>
/// <param name="SensorRangeMeters">How far it senses a bait.</param>
/// <param name="EatSeconds">How long it eats one bait (a petting must start before it is done).</param>
public sealed record NativeTamingState(EntityId Creature, bool Tameable, int Progress, int Required, NativeTamingPhase Phase, EntityId Bait,
    Guid BaitTemplate, double SensorRangeMeters, double EatSeconds)
{
    /// <summary>The game's raw state byte (wildlife::State), for logs.</summary>
    public int RawState { get; init; }

    /// <summary>Who tamed it: always null on this build. The game records no player for a step (docs/re/taming.md 4), and
    /// Kiln's attribution (<c>taming.native_observe</c>) stays off until a lab with a player proves where the petting player
    /// can be read. A mod that needs an owner decides it (the nearest player, its own attempt).</summary>
    public PlayerRef? TamedBy => null;
}

/// <summary>Why the game's taming progress of an animal changed. Since 0.2.0-dev.32.</summary>
public enum NativeTamingCause
{
    /// <summary>Progress rose: a step (a petting while it ate its bait, or <see cref="NativeTaming.PetStep"/>). The watch
    /// sees only the progress, so a mod's raw write of it reads the same (lab 11-27).</summary>
    Step = 0,
    /// <summary>Progress fell while the animal is wild: a startle (the world setting <c>tamingStartleRepercussion</c>:
    /// some, or all), or a mod's raw write.</summary>
    Startle = 1,
    /// <summary>Progress fell on a tamed animal.</summary>
    Other = 2,
}

/// <summary>An animal's taming progress changed in the game. Posted on the server tick when Kiln's watch sees it (every
/// 5 steps while anyone listens to the native taming events; a progress that changes and changes back between two looks
/// is not seen). Not cancelable: it already happened. Since 0.2.0-dev.32.</summary>
[ServerOnly]
public sealed class NativeTamingProgressEvent : KilnEvent
{
    internal NativeTamingProgressEvent(EntityId creature, int before, int after, int required, NativeTamingCause cause)
    {
        Creature = creature;
        Before = before;
        After = after;
        Required = required;
        Cause = cause;
    }

    /// <summary>The animal.</summary>
    public EntityId Creature { get; }
    /// <summary>Progress before.</summary>
    public int Before { get; }
    /// <summary>Progress now.</summary>
    public int After { get; }
    /// <summary>Steps to the farm transformation.</summary>
    public int Required { get; }
    /// <summary>Why.</summary>
    public NativeTamingCause Cause { get; }

    /// <summary>For logs.</summary>
    public override string ToString() => $"{Creature}: taming {Before} -> {After} of {Required} ({Cause})";
}

/// <summary>The game tamed an animal (it became a farm animal with IsTame). Posted on the server tick when Kiln's watch
/// sees it. Not cancelable. Who tamed it is not known (<see cref="NativeTamingState.TamedBy"/>). Since 0.2.0-dev.32.</summary>
[ServerOnly]
public sealed class NativeTamedEvent : KilnEvent
{
    internal NativeTamedEvent(EntityId creature, int steps)
    {
        Creature = creature;
        Steps = steps;
    }

    /// <summary>The animal (the same entity, now a farm animal).</summary>
    public EntityId Creature { get; }
    /// <summary>The steps it took.</summary>
    public int Steps { get; }

    /// <summary>For logs.</summary>
    public override string ToString() => $"{Creature}: tamed after {Steps} step(s)";
}

/// <summary>An animal finished a bait. <see cref="Counted"/> false: it ate it to the end without a petting, the bait is
/// gone and no step was made (the game's way a taming attempt runs out). Not cancelable. Since 0.2.0-dev.32.</summary>
[ServerOnly]
public sealed class NativeBaitEatenEvent : KilnEvent
{
    internal NativeBaitEatenEvent(EntityId creature, EntityId bait, bool counted)
    {
        Creature = creature;
        Bait = bait;
        Counted = counted;
    }

    /// <summary>The animal.</summary>
    public EntityId Creature { get; }
    /// <summary>The bait it ate (gone now).</summary>
    public EntityId Bait { get; }
    /// <summary>True when the bait went with a step (a petting); false when it was eaten without one.</summary>
    public bool Counted { get; }

    /// <summary>For logs.</summary>
    public override string ToString() => $"{Creature}: bait {Bait} eaten ({(Counted ? "a step" : "no step")})";
}

/// <summary>The result of <see cref="NativeTaming.PetStep"/>. Since 0.2.0-dev.32.</summary>
/// <param name="Ok">The animal's pet sequence started while it eats its bait: the step follows when its taming event
/// fires (3.75 s on this build), unless the bait goes first.</param>
/// <param name="Reason">Why not.</param>
public sealed record NativeStepResult(bool Ok, string? Reason) : IKilnResult;

/// <summary>The game's own taming as mechanism (docs/re/taming.md). Server only, tick thread. Capability
/// <c>taming.native</c> (sigdb: the game's four taming programs and their components' layouts on this build). Since
/// 0.2.0-dev.32.</summary>
[ServerOnly]
public static class NativeTaming
{
    /// <summary>The capability.</summary>
    public const string Capability = "taming.native";

    /// <summary>True when the game's taming can be read and driven here.</summary>
    public static bool Available => NativeTamingRuntime.Available();

    /// <summary>The animal's taming now; null when it is no wild animal (no DynamicWildlife: a farm animal that never was
    /// wild, any other entity) or does not exist.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>taming.native</c> is off.</exception>
    [TickThreadOnly]
    [RequiresCapability("taming.native")]
    public static NativeTamingState? Read(EntityId creature)
    {
        Services.RequireTickThread("NativeTaming.Read");
        NativeTamingRuntime.Require("NativeTaming.Read");
        return NativeTamingRuntime.Read(creature);
    }

    /// <summary>Places the animal's own favourite bait (its template's, as the player's bait item places it) at
    /// <paramref name="at"/>, or <paramref name="distance"/> metres in front of it. No item is taken: the cost of a bait is
    /// the mod's (<see cref="ItemCosts"/>). The bait is not saved (as the game's own), a player can pick it up.</summary>
    /// <returns>The bait entity; none when it could not be placed.</returns>
    /// <exception cref="ArgumentException">The creature is no tameable wild animal.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>taming.native</c> is off.</exception>
    /// <remarks>Experimental (KILNX002): in the lab (docs/re/taming.md 6) a bait spawned from its template was not sensed
    /// by a simulated wild animal 2.5 m away; whether the game senses it as one a player places is not proven yet.</remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX002", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx002")]
    [TickThreadOnly]
    [RequiresCapability("taming.native")]
    public static Task<EntityId> PlaceBait(EntityId creature, WorldPosition? at = null, double distance = 2.5)
    {
        Services.RequireTickThread("NativeTaming.PlaceBait");
        NativeTamingRuntime.Require("NativeTaming.PlaceBait");
        if (NativeTamingRuntime.Read(creature) is not { Tameable: true, BaitTemplate: var bait } || bait == Guid.Empty)
            throw new ArgumentException($"{creature} is no tameable wild animal with a bait", nameof(creature));
        if (distance is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(distance), distance, "0 to 8 m, the game's own placement reach");
        var where = at ?? NativeTamingRuntime.Before(creature, distance);
        if (where is null) return Task.FromResult(EntityId.None);
        return NativeTamingRuntime.World.SpawnTransient(bait, where.Value);
    }

    /// <summary>One step of the game's taming, made by the server: the animal plays its own synced pet sequence, the one a
    /// player's petting starts, so the game counts the step itself (its TameWildlifeEvent, 3.75 s in, consumes the bait and
    /// adds one). Refused, nothing played, unless the animal is eating its bait now and below its required steps. A step
    /// costs a bait like the game's own; the step's cost to a player is the mod's.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>taming.native</c> is off.</exception>
    /// <remarks>Experimental (KILNX002): its refusals are lab-proven; that the game counts the step on an eating animal is not
    /// proven yet (the lab's animals never ate a Kiln-placed bait; docs/re/taming.md 6).</remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX002", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx002")]
    [TickThreadOnly]
    [RequiresCapability("taming.native")]
    public static NativeStepResult PetStep(EntityId creature)
    {
        Services.RequireTickThread("NativeTaming.PetStep");
        NativeTamingRuntime.Require("NativeTaming.PetStep");
        var s = NativeTamingRuntime.Read(creature);
        if (s is null) return new NativeStepResult(false, "NotWild: no wild animal");
        if (!s.Tameable) return new NativeStepResult(false, "NotTameable: its template does not allow taming");
        if (s.Phase == NativeTamingPhase.Tamed) return new NativeStepResult(false, "Tamed: it is a farm animal already");
        if (s.Progress >= s.Required) return new NativeStepResult(false, "Complete: it has its steps; the game tames it in its next step");
        if (s.Phase != NativeTamingPhase.EatingBait) return new NativeStepResult(false, "NotEating: a step counts only while it eats its bait");
        if (!NativeTamingRuntime.HasStandardPetSequence(creature))
            return new NativeStepResult(false, "OtherPetSequence: its pet interaction is not the game's standard one Kiln knows");
        return NativeTamingRuntime.World.PlaySequence(creature, NativeTamingRuntime.AnimalPetSequence)
            ? new NativeStepResult(true, null)
            : new NativeStepResult(false, "SequenceRefused: the game did not start the pet sequence");
    }
}

/// <summary>What NativeTaming asks of the game (tests replace it).</summary>
internal interface INativeTamingWorld
{
    byte[]? ReadBytes(EntityId e, string component);
    /// <summary>A field of a template component as the entity reads it ("Wildlife.tamingSetup.necessaryTamingSteps"), as
    /// text; null when it has none. The game keeps Wildlife in the template only: no entity has a Wildlife component.</summary>
    string? TemplateField(EntityId e, string path);
    /// <summary>The entity's template (the setup cache's key); null when not known.</summary>
    Guid? TemplateOf(EntityId e);
    bool Has(EntityId e, string component);
    bool Exists(EntityId e);
    IReadOnlyList<EntityId> Find(string component);
    WorldPosition? Position(EntityId e);
    float? Yaw(EntityId e);
    Task<EntityId> SpawnTransient(Guid template, WorldPosition at);
    bool PlaySequence(EntityId e, Guid sequence);
}

internal sealed class GameNativeTamingWorld : INativeTamingWorld
{
    public byte[]? ReadBytes(EntityId e, string component) => Kiln.World.ReadBytes(e, component);

    public string? TemplateField(EntityId e, string path)
    {
        try { return Creatures.Available ? Creatures.Of(e).Ai.Get(path) : null; }
        catch (Exception x) when (x is ArgumentException or NotSupportedException or InvalidOperationException) { return null; }
    }

    public Guid? TemplateOf(EntityId e) => Spawn.Inspect(e)?.Template;
    public bool Has(EntityId e, string component) => Kiln.World.Has(e, component);
    public bool Exists(EntityId e) => Kiln.World.Exists(e);
    public IReadOnlyList<EntityId> Find(string component) => Kiln.World.Find(component);
    public WorldPosition? Position(EntityId e) => Kiln.World.Position(e);
    public float? Yaw(EntityId e) => Kiln.World.Transform(e) is { } t ? (float)t.YawDegrees : null;
    public Task<EntityId> SpawnTransient(Guid template, WorldPosition at) => Spawn.Entity(template, at, options: new SpawnOptions { Flags = SpawnFlags.Transient });
    public bool PlaySequence(EntityId e, Guid sequence) => Abilities.PlaySequence(e, sequence, abortCurrent: false);
}

/// <summary>The layouts, the reads and the watch behind NativeTaming.</summary>
internal static class NativeTamingRuntime
{
    // keen::ecs::DynamicWildlife (176 bytes, per entity): this build's reflection (sigdb taming/*, docs/re/taming.md 3). A
    // build whose size differs is refused. The taming setup is the Wildlife template's (no entity has a Wildlife
    // component: lab 11-27), read by its reflection paths through kiln.creatures and kept per template.
    internal const int DynamicSize = 0xb0;
    internal const int State = 0x00, Bait = 0x88, Ate = 0x8d, Progress = 0x8e;
    internal const byte StateDead = 12, StatePursue = 14, StateEat = 15;
    /// <summary>The player's pet interaction every tameable animal of this build has (Interaction_Pet_Wildlife_Taming).</summary>
    internal static readonly Guid PlayerPetSequence = Guid.Parse("2e70d813-b80a-41e5-a6a2-b3ba526ebe8e");
    /// <summary>The animal's side of it (Enemy_Wildlife_Pet_Taming), whose TameWildlifeEvent the game counts.</summary>
    internal static readonly Guid AnimalPetSequence = Guid.Parse("09ad9f39-f2b6-4051-8618-f444961bf6c1");

    internal static INativeTamingWorld World = new GameNativeTamingWorld();
    internal static Func<bool> Available = () => Capabilities.Is(NativeTaming.Capability);
    internal static Action<KilnEvent> Sink = ev => Events.Post(ev);

    internal static void Require(string api)
    {
        if (!Available()) throw new KilnUnavailableException(NativeTaming.Capability, $"{api}: the game's taming is not readable on this build");
    }

    /// <summary>A Wildlife template's taming setup.</summary>
    internal sealed record Setup(bool Tameable, int Steps, Guid Bait, double SensorMeters, double EatSeconds, Guid PetSequence);

    private static readonly Dictionary<Guid, Setup> Setups = new();

    /// <summary>The animal's taming setup (its template's, read once per template); null when it has none.</summary>
    internal static Setup? SetupOf(EntityId e)
    {
        var tpl = World.TemplateOf(e);
        if (tpl is { } t && Setups.TryGetValue(t, out var known)) return known;
        var features = World.TemplateField(e, "Wildlife.enabledFeatures");
        if (features is null) return null;
        var setup = new Setup(
            features.Split('|', StringSplitOptions.TrimEntries).Contains("Tameable"),
            (int)(Number(World.TemplateField(e, "Wildlife.tamingSetup.necessaryTamingSteps")) ?? 0),
            GuidIn(World.TemplateField(e, "Wildlife.tamingSetup.favoriteFood")),
            Number(World.TemplateField(e, "Wildlife.tamingSetup.foodBaitSensorRange")) ?? 0,
            Number(World.TemplateField(e, "Wildlife.tamingSetup.foodBaitEatMaxDuration")) ?? 0,
            GuidIn(World.TemplateField(e, "Wildlife.tamingPetInteractionSequence")));
        if (tpl is { } key) Setups[key] = setup;
        return setup;
    }

    internal static double? Number(string? text) =>
        double.TryParse(text?.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static Guid GuidIn(string? text) => Guid.TryParse(text?.Trim(), out var g) ? g : Guid.Empty;

    internal static NativeTamingState? Read(EntityId creature)
    {
        if (creature.IsNone) return null;
        var d = World.ReadBytes(creature, "DynamicWildlife");
        if (d is null || d.Length < DynamicSize) return null;
        if (SetupOf(creature) is not { } setup) return null;
        var tame = World.Has(creature, "IsTame");
        var state = d[State];
        var phase = tame ? NativeTamingPhase.Tamed : state switch
        {
            StateDead => NativeTamingPhase.Dead,
            StatePursue => NativeTamingPhase.PursuingBait,
            StateEat => NativeTamingPhase.EatingBait,
            _ => NativeTamingPhase.Wild,
        };
        var bait = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(Bait));
        return new NativeTamingState(creature, setup.Tameable, d[Progress], setup.Steps, phase, new EntityId(bait), setup.Bait, setup.SensorMeters, setup.EatSeconds)
        {
            RawState = state,
        };
    }

    internal static bool HasStandardPetSequence(EntityId creature) => SetupOf(creature)?.PetSequence == PlayerPetSequence;

    internal static WorldPosition? Before(EntityId creature, double distance)
    {
        if (World.Position(creature) is not { } p) return null;
        var yaw = (World.Yaw(creature) ?? 0) * Math.PI / 180;
        return new WorldPosition(p.X + (float)(Math.Sin(yaw) * distance), p.Y, p.Z + (float)(Math.Cos(yaw) * distance));
    }

    // ---- the watch: every 5 steps while anyone listens; the wild animals list again every 50 ----
    private sealed class Seen
    {
        public int Progress, Required;
        public bool Tame, Eating;
        public EntityId Bait;
    }

    private static readonly Dictionary<EntityId, Seen> Watched = new();
    private static IReadOnlyList<EntityId> _wild = [];
    private static ulong _listed;
    internal static Func<bool> Listening = () =>
        Events.HasSubscribers<NativeTamingProgressEvent>() || Events.HasSubscribers<NativeTamedEvent>() || Events.HasSubscribers<NativeBaitEatenEvent>();

    internal static void Reset()
    {
        World = new GameNativeTamingWorld();
        Available = () => Capabilities.Is(NativeTaming.Capability);
        Sink = ev => Events.Post(ev);
        Listening = () => Events.HasSubscribers<NativeTamingProgressEvent>() || Events.HasSubscribers<NativeTamedEvent>() || Events.HasSubscribers<NativeBaitEatenEvent>();
        Watched.Clear();
        Setups.Clear();
        _wild = [];
        _listed = 0;
    }

    /// <summary>One look (the service tick calls it every step; it works every 5th).</summary>
    internal static void Tick(ulong step)
    {
        if (step % 5 != 0 || !Available() || !Listening()) return;
        if (_listed == 0 || step >= _listed + 50)
        {
            _wild = World.Find("DynamicWildlife");
            _listed = step;
        }
        var alive = new HashSet<EntityId>();
        foreach (var e in _wild)
        {
            if (Read(e) is not { Tameable: true } s) continue;
            alive.Add(e);
            var eating = s.Phase == NativeTamingPhase.EatingBait;
            var tame = s.Phase == NativeTamingPhase.Tamed;
            if (!Watched.TryGetValue(e, out var was))
            {
                Watched[e] = new Seen { Progress = s.Progress, Required = s.Required, Tame = tame, Eating = eating, Bait = s.Bait };
                continue;
            }
            if (s.Progress != was.Progress)
                Sink(new NativeTamingProgressEvent(e, was.Progress, s.Progress, s.Required,
                    s.Progress > was.Progress ? NativeTamingCause.Step : tame ? NativeTamingCause.Other : NativeTamingCause.Startle));
            // a bait that was being eaten is gone: counted when the progress rose with it
            if (was.Eating && !was.Bait.IsNone && s.Bait != was.Bait && !World.Exists(was.Bait))
                Sink(new NativeBaitEatenEvent(e, was.Bait, s.Progress > was.Progress));
            if (tame && !was.Tame) Sink(new NativeTamedEvent(e, s.Required));
            was.Progress = s.Progress;
            was.Required = s.Required;
            was.Tame = tame;
            was.Eating = eating;
            was.Bait = s.Bait;
        }
        foreach (var gone in Watched.Keys.Where(k => !alive.Contains(k)).ToList()) Watched.Remove(gone);
    }
}
