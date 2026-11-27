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
/// <param name="EatDuration">How long it eats one bait (a petting must start before it is done).</param>
public sealed record NativeTamingState(EntityId Creature, bool Tameable, int Progress, int Required, NativeTamingPhase Phase, EntityId Bait,
    Guid BaitTemplate, double SensorRangeMeters, TimeSpan EatDuration)
{
    // in seconds (internal)
    internal double EatSeconds => EatDuration.TotalSeconds;

    /// <summary>The game's raw state byte (wildlife::State), for logs.</summary>
    public int RawState { get; init; }

    /// <summary>Who tamed it. Unavailable until native attribution is proven: null while
    /// <see cref="NativeTaming.ObserveCapability"/> (<c>taming.native_observe</c>) is off, as it is on every build this Kiln
    /// knows (the game records no player for a step, docs/re/taming.md 4). Kiln fills it once a lab proves where the petting
    /// player is read; meanwhile a mod that needs an owner decides it (the nearest player, its own attempt).</summary>
    [RequiresCapability(NativeTaming.ObserveCapability)]
    public PlayerRef? TamedBy { get; init; }
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

    /// <summary>The wild animal whose taming progress changed.</summary>
    public EntityId Creature { get; }
    /// <summary>Its progress before the change, in the game's steps.</summary>
    public int Before { get; }
    /// <summary>Its progress now, in the game's steps (<see cref="Required"/> is the number that tames it).</summary>
    public int After { get; }
    /// <summary>Steps to the farm transformation.</summary>
    public int Required { get; }
    /// <summary>Why the progress changed: a step, a startle, or something else.</summary>
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
/// <param name="Ok">The animal's pet sequence started while it eats its bait. That the game then counts the step (its
/// taming event, 3.75 s in on this build, unless the bait goes first) is not proven yet: watch
/// <see cref="NativeTamingProgressEvent"/> for the step itself.</param>
/// <param name="Reason">Why not, in words for a log: its first word is <see cref="Refusal"/>.</param>
public sealed record NativeStepResult(bool Ok, string? Reason) : IKilnResult
{
    /// <summary>Why not, as a value (<see cref="TamingRefusal.NotWild"/>, <see cref="TamingRefusal.NotEating"/>, ...):
    /// <see cref="TamingRefusal.None"/> when <see cref="Ok"/>.</summary>
    public TamingRefusal Refusal => RefusalWords.Of(Ok, Reason, TamingRefusal.None, TamingRefusal.Other);
}

/// <summary>
/// The game's own taming as mechanism: a wild animal eats a bait of its favourite food and is petted while it eats; each
/// petting is one step, and at its required steps the game makes it a farm animal. Kiln reads that state
/// (<see cref="Read"/>), reports it as events (<see cref="NativeTamingProgressEvent"/>, <see cref="NativeTamedEvent"/>,
/// <see cref="NativeBaitEatenEvent"/>) and lets a server mod play the animal's own pet sequence (<see cref="PetStep"/>).
/// Nothing of the game's is hooked or replaced: the animal keeps its own offers, and who may tame what and what it costs stay
/// the mod's. Server only, tick thread. Capability <c>taming.native</c> (sigdb: the game's four taming programs and their
/// components' layouts on this build). Since 0.2.0-dev.32.
/// </summary>
/// <remarks>To make the game's taming a step of a Kiln taming, give a <see cref="TamingDefinition"/> a <see cref="NativeStep"/>:
/// Kiln then waits for the game's own count to rise (<c>NativePending</c> until it does) and takes no cost of its own for that
/// step. Use <see cref="Read"/> directly to build a HUD or a quest on the game's progress.</remarks>
/// <example><code>
/// if (NativeTaming.Available &amp;&amp; NativeTaming.Read(animal) is { Tameable: true } state)   // animal: an EntityId
///     Log.Info($"{state.Phase}: {state.Progress}/{state.Required} step(s)");
/// Events.On&lt;NativeTamedEvent&gt;(e =&gt; Log.Info($"the game tamed {e.Creature} after {e.Steps} step(s)"));
/// </code></example>
/// <seealso cref="NativeStep"/>
/// <seealso cref="Taming"/>
/// <seealso href="https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/taming.md">Taming: the modder's guide</seealso>
[ServerOnly]
public static class NativeTaming
{
    /// <summary>The capability of reading and driving the game's taming: <c>"taming.native"</c>. It is a sigdb feature, so it
    /// is off on a game build whose taming layouts Kiln does not know, and when <c>Kiln.Creatures</c> is unavailable.</summary>
    public const string Capability = "taming.native";

    /// <summary>The capability of <see cref="PlaceBait"/>: a bait Kiln places that the game senses. Off on every build so far
    /// (not proven).</summary>
    public const string BaitCapability = "taming.native_bait";

    /// <summary>The capability of who tamed an animal (<see cref="NativeTamingState.TamedBy"/>): off until a lab with a player
    /// proves where the game's petting player can be read.</summary>
    public const string ObserveCapability = "taming.native_observe";

    internal const string BaitUnproven = "not proven: a bait Kiln spawns from its template has no bait marker and no animal sensed it in the lab "
        + "(docs/re/taming.md 6), while a player could pick it up as a free item; a lab must show a placement the game senses first";

    /// <summary>True when the game's taming can be read and driven here (the capability <see cref="Capability"/> is on). Any thread.</summary>
    public static bool Available => NativeTamingRuntime.Available();

    /// <summary>The animal's taming now; null when it is no wild animal (no DynamicWildlife: a farm animal that never was
    /// wild, any other entity) or does not exist.</summary>
    /// <param name="creature">The animal's entity id.</param>
    /// <returns>Its state as the game has it now, or null.</returns>
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
    /// <param name="creature">The animal whose favourite bait is placed.</param>
    /// <param name="at">Where to place it; null: <paramref name="distance"/> metres in front of the animal.</param>
    /// <param name="distance">0 to 8 metres: how far in front of the animal, when <paramref name="at"/> is null. Default 2.5.</param>
    /// <returns>A task of the bait entity; none when it could not be placed. It completes at a later step.</returns>
    /// <exception cref="ArgumentException">The creature is no tameable wild animal with a bait.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="distance"/> is outside 0 to 8.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>taming.native</c> is off.</exception>
    /// <exception cref="KilnUnavailableException">The capability <c>taming.native_bait</c> is off: on every build so far (see
    /// remarks).</exception>
    /// <remarks>Experimental (KILNX002), and refused until proven: in the lab (docs/re/taming.md 6) a bait spawned from its
    /// template had no FoodBait marker and was not sensed by a simulated wild animal 2.5 m away, while any player could pick
    /// it up as a free bait item. So <c>taming.native_bait</c> stays off (policy) and this throws, spawning nothing, until a
    /// lab proves a placement the game senses.</remarks>
    [System.Diagnostics.CodeAnalysis.Experimental("KILNX002", UrlFormat = "https://github.com/frostdev-ops/kiln/blob/main/docs/kiln/modding/analyzers.md#kilnx002")]
    [TickThreadOnly]
    [RequiresCapability("taming.native")]
    [RequiresCapability("taming.native_bait")]
    public static Task<EntityId> PlaceBait(EntityId creature, WorldPosition? at = null, double distance = 2.5)
    {
        Services.RequireTickThread("NativeTaming.PlaceBait");
        NativeTamingRuntime.Require("NativeTaming.PlaceBait");
        if (!NativeTamingRuntime.BaitProven())
            throw new KilnUnavailableException(NativeTaming.BaitCapability, "NativeTaming.PlaceBait: " + NativeTaming.BaitUnproven);
        if (NativeTamingRuntime.Read(creature) is not { Tameable: true, BaitTemplate: var bait } || bait == Guid.Empty)
            throw new ArgumentException($"{creature} is no tameable wild animal with a bait", nameof(creature));
        if (distance is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(distance), distance, "0 to 8 m, the game's own placement reach");
        var where = at ?? NativeTamingRuntime.Before(creature, distance);
        if (where is null) return Task.FromResult(EntityId.None);
        return NativeTamingRuntime.World.SpawnTransient(bait, where.Value);
    }

    /// <summary>One step of the game's taming, made by the server: the animal plays its own synced pet sequence, the one a
    /// player's petting starts, so the game may count the step itself (its TameWildlifeEvent, 3.75 s in, would consume the
    /// bait and add one: not proven yet, see remarks). Nothing is written: it only plays the game's own sequence. Refused, nothing played, unless the animal is eating its bait now and below its required steps. A step
    /// costs a bait like the game's own; the step's cost to a player is the mod's.</summary>
    /// <param name="creature">The animal that is eating its bait.</param>
    /// <returns>Ok when the animal's pet sequence started; else a reason (a word first): <c>NotWild</c>, <c>NotTameable</c>,
    /// <c>Tamed</c>, <c>Complete</c> (it has its steps; the game tames it at its next step), <c>NotEating</c> (a step counts only
    /// while it eats its bait), <c>OtherPetSequence</c> (its pet interaction is not the game's standard one) or
    /// <c>SequenceRefused</c>. Ok only means the sequence started: watch <see cref="NativeTamingProgressEvent"/> for the step.</returns>
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
    internal static Func<bool> BaitProven = () => Capabilities.Is(NativeTaming.BaitCapability);
    internal static Action<KilnEvent> Sink = ev => Events.Post(ev);

    internal static void Require(string api)
    {
        if (!Available()) throw new KilnUnavailableException(NativeTaming.Capability, $"{api}: the game's taming is not readable on this build");
    }

    /// <summary>A Wildlife template's taming setup (its steps are read again at every read: the creatures registry's
    /// tamingSteps is live).</summary>
    internal sealed record Setup(bool Tameable, int Steps, Guid Bait, double SensorMeters, double EatSeconds, Guid PetSequence);

    internal const string StepsPath = "Wildlife.tamingSetup.necessaryTamingSteps";

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
            (int)(Number(World.TemplateField(e, StepsPath)) ?? 0),
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
        if (d is null || d.Length != DynamicSize) return null;   // another build's layout (shorter or longer): refused, never misread
        if (SetupOf(creature) is not { } setup) return null;
        // the steps as the template has them now (a registry edit of creatures.tamingSteps reaches every animal at once);
        // the rest of the setup is the cached first read
        var steps = (int)(Number(World.TemplateField(creature, StepsPath)) ?? setup.Steps);
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
        return new NativeTamingState(creature, setup.Tameable, d[Progress], steps, phase, new EntityId(bait), setup.Bait, setup.SensorMeters, Seconds.Of(setup.EatSeconds))
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
        BaitProven = () => Capabilities.Is(NativeTaming.BaitCapability);
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
