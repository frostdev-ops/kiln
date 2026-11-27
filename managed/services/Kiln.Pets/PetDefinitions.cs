// SPDX-License-Identifier: MIT
// What a pet is (19d §5.1): the immutable definition a mod registers with Pets.Define, directly or through Kiln.Blocks'
// PetBlock (the same record either way). Every behaviour choice is here, as the mod's declaration: Kiln only gives a safe
// default for a mod that does not say (players protected, a bodyless recovery anchor). Kiln.Pets never references
// Kiln.Blocks: the block lowers onto these records.
using System.Globalization;

namespace Kiln;

/// <summary>Where a pet's level comes from. Since 0.2.0-dev.32.</summary>
public enum PetLevelMode
{
    /// <summary>Always <see cref="PetLevelPolicy.Level"/>.</summary>
    Fixed = 0,
    /// <summary>The owner's level when the pet was tamed (taming lands with 19e; a granted pet takes the owner's level at
    /// the grant).</summary>
    OwnerAtTame = 1,
    /// <summary>The owner's level each time a body is made (summon, revive).</summary>
    OwnerAtSummon = 2,
}

/// <summary>A pet's level and size. Since 0.2.0-dev.32.</summary>
public sealed record PetLevelPolicy
{
    /// <summary>Where the level comes from. Default <see cref="PetLevelMode.Fixed"/>.</summary>
    public PetLevelMode Mode { get; init; } = PetLevelMode.Fixed;
    /// <summary>The level for <see cref="PetLevelMode.Fixed"/>: 1 up to the creature's highest level (at most 255).</summary>
    public int Level { get; init; } = 1;
    /// <summary>The body's uniform scale, 0.25..4 (1 as made).</summary>
    public double Scale { get; init; } = 1;
}

/// <summary>How a pet's body moves while it follows. Since 0.2.0-dev.32.</summary>
public enum PetMovementMode
{
    /// <summary>Kiln walks the body along walk-only routes it asks kiln.queries for (the broker's PetFollow lease: the
    /// body's own AI held off). The default.</summary>
    QueryInput = 0,
    /// <summary>The game's own guard behaviour keeps the body near its owner (the broker's EngineGuard lease: AI on).</summary>
    EngineGuard = 1,
}

/// <summary>Follow distances, catch-up and their limits: the mod's choice for its pets. Since 0.2.0-dev.32.</summary>
public sealed record PetMovementPolicy
{
    /// <summary>Who walks the body. Default <see cref="PetMovementMode.QueryInput"/>.</summary>
    public PetMovementMode Mode { get; init; } = PetMovementMode.QueryInput;
    /// <summary>Metres from the owner where the pet stops, 1..10.</summary>
    public double FollowDistance { get; init; } = 3;
    /// <summary>Seconds between new routes while the owner moves, 0.1..5.</summary>
    public double RepathSeconds { get; init; } = 0.5;
    /// <summary>Farther than this many metres the pet runs instead of walking: at least <see cref="FollowDistance"/>, at most 30.</summary>
    public double RunDistance { get; init; } = 10;
    /// <summary>Farther than this many metres (10..160) the pet is put next to its owner on safe ground, when
    /// <see cref="AllowCatchUp"/>.</summary>
    public double CatchUpDistance { get; init; } = 60;
    /// <summary>Seconds without progress (1..30) after which a stuck pet catches up, when <see cref="AllowCatchUp"/>.</summary>
    public double StuckSeconds { get; init; } = 5;
    /// <summary>How far, in metres (0.5..8), Kiln looks up and down for ground around a catch-up spot.</summary>
    public double GroundSearchMeters { get; init; } = 4;
    /// <summary>Whether a far or stuck pet is put next to its owner. Off: it stops and waits.</summary>
    public bool AllowCatchUp { get; init; } = true;
}

/// <summary>A cost of so many items of one kind. Since 0.2.0-dev.32.</summary>
/// <param name="Item">A plain stack item (not gear).</param>
/// <param name="Count">1..999.</param>
public sealed record PetCostDefinition(ItemId Item, int Count);

/// <summary>How a downed pet is revived: the channel, its range, the health it comes back with, its cost and what
/// cancels it. The mod's choice. Since 0.2.0-dev.32.</summary>
public sealed record PetRevivePolicy
{
    /// <summary>False: a downed pet of this definition cannot be revived by <c>Pets.Revive</c> (the mod releases it,
    /// or revives it its own way). Default true.</summary>
    public bool Allowed { get; init; } = true;
    /// <summary>Seconds of simulation time the owner channels, 0.1..60.</summary>
    public double ChannelSeconds { get; init; } = 3;
    /// <summary>Metres between the owner and the recovery anchor during the channel, 1..8.</summary>
    public double RangeMeters { get; init; } = 3;
    /// <summary>The share of its maximum health the pet comes back with, more than 0 and at most 1.</summary>
    public double HealthFraction { get; init; } = 0.5;
    /// <summary>What the revive costs, taken when the channel completes; null: free.</summary>
    public PetCostDefinition? Cost { get; init; }
    /// <summary>A hit, a fall or health lost by the owner during the channel cancels it.</summary>
    public bool CancelOnObservedOwnerDamage { get; init; } = true;
    /// <summary>The owner moving farther than <see cref="MaxOwnerDisplacement"/> from where the channel began cancels it.</summary>
    public bool CancelOnMovement { get; init; } = true;
    /// <summary>Metres, 0.1..2.</summary>
    public double MaxOwnerDisplacement { get; init; } = 0.75;
}

/// <summary>Who players may fight through pets. Since 0.2.0-dev.32.</summary>
public enum PetPvpMode
{
    /// <summary>Every player is protected, related or not: pets never harm players, players never harm pets. The default.</summary>
    Off = 0,
    /// <summary>Players and their pets are protected unless the mod declared the two characters hostile
    /// (<c>Pets.SetHostile</c>): consensual duels by the mod's own rule. Kiln enforces the pair; the mod decides it.</summary>
    ByModRule = 1,
}

/// <summary>Who a pet may harm and who may harm it. One rule decides hits, knockback, aggro, explicit attack orders and
/// defend/assist alike. Since 0.2.0-dev.32.</summary>
public sealed record PetTargetPolicy
{
    /// <summary>Players and pets of other characters. Default <see cref="PetPvpMode.Off"/>.</summary>
    public PetPvpMode Pvp { get; init; } = PetPvpMode.Off;
    /// <summary>The factions of unowned creatures a pet may fight. Null: Kiln's list (<see cref="DefaultHostileFactions"/>).
    /// Player, AutomatedPlayer, PlayerAlly and FarmAnimal are never in it (refused); a creature with a player's components
    /// is never a fair target, whatever its faction.</summary>
    public IReadOnlyList<Faction>? HostileFactions { get; init; }

    /// <summary>Kiln's list of hostile factions: Fogger, Vukah, Scavenger, WildBeast, Skeleton, Cyclops, Drak, Ancients.</summary>
    public static IReadOnlyList<Faction> DefaultHostileFactions { get; } =
        [Faction.Fogger, Faction.Vukah, Faction.Scavenger, Faction.WildBeast, Faction.Skeleton, Faction.Cyclops, Faction.Drak, Faction.Ancients];

    internal uint HostileMask
    {
        get
        {
            uint m = 0;
            foreach (var f in HostileFactions ?? DefaultHostileFactions)
                if ((byte)f < 32) m |= 1u << (byte)f;
            return m;
        }
    }
}

/// <summary>What a pet becomes when it goes down. Since 0.2.0-dev.32.</summary>
public enum PetDownedOutcome
{
    /// <summary>The combat body is removed and a bodyless recovery anchor stays where it fell; the owner revives it there.
    /// The default.</summary>
    RecoveryAnchor = 0,
    /// <summary>The combat body is removed and no anchor is shown; the pet stays downed until its owner summons the anchor
    /// next to them and revives it.</summary>
    Absent = 1,
    /// <summary>The pet is released when it goes down: its record ends (a tombstone until the next save).</summary>
    Release = 2,
}

/// <summary>What a downed pet is. The mod's choice; <c>PetDowningEvent</c> may choose per pet. A body left lying
/// (a decorative corpse) is not possible yet: Kiln has no client-side decorative body (see docs/kiln/services/pets.md).
/// Since 0.2.0-dev.32.</summary>
public sealed record PetDownedPolicy
{
    /// <summary>Default <see cref="PetDownedOutcome.RecoveryAnchor"/>.</summary>
    public PetDownedOutcome Outcome { get; init; } = PetDownedOutcome.RecoveryAnchor;
}

/// <summary>How many pets of a definition there may be: the definition's limits. Kiln's own ceilings apply on top (128
/// records per character, 256 controlled bodies in a world); a request past a limit is refused as
/// <c>Capacity</c>. Since 0.2.0-dev.32.</summary>
public sealed record PetLimits
{
    /// <summary>Records of this definition one character keeps (released ones not counted), 1..128.</summary>
    public int RetainedPerCharacter { get; init; } = 32;
    /// <summary>Live bodies of one character (of every definition) while summoning a pet of this definition, 1..16.</summary>
    public int LivePerCharacter { get; init; } = 1;
    /// <summary>Live bodies of this definition in the world, 1..256.</summary>
    public int LivePerWorld { get; init; } = 16;
}

/// <summary>What the owner's built-in HUD shows of pets of this definition (each player's own game may still hide the
/// whole bar, <c>PetHudOptions</c>; a mod may draw its own from <c>PetsClient.Snapshot</c>). Since 0.2.0-dev.32.</summary>
public sealed record PetHudPolicy
{
    /// <summary>A row in the owner's pet bar.</summary>
    public bool Bar { get; init; } = true;
    /// <summary>The recovery anchor's marker in the world.</summary>
    public bool RecoveryMarker { get; init; } = true;
}

/// <summary>
/// A kind of pet, as a mod defines it (<see cref="Pets.Define"/>, or a <c>PetBlock</c> through Kiln.Blocks: both make this
/// record). Immutable: a definition cannot change while one of its pets has a live body or a revive running.
/// Since 0.2.0-dev.32.
/// </summary>
public sealed record PetDefinition
{
    /// <summary>The definition's Kiln id, <c>&lt;mod id&gt;:&lt;name&gt;</c> (lower case letters, digits, '-', '_', '.').
    /// Pet records keep it.</summary>
    public required string Id { get; init; }
    /// <summary>The creature the pet's body is, by its Kiln id: a game creature of the <c>creatures</c> registry
    /// (<c>game:enemy_wildbeast_wolf_earlygame</c>) or a creature block of a mod (<c>&lt;mod id&gt;:&lt;name&gt;</c>). Never a game
    /// template name or GUID. Bodies are enemy creatures that walk (wildlife and flying bodies are not supported yet).</summary>
    public required string Creature { get; init; }
    /// <summary>The name owners read (1 to 64 UTF-8 bytes), as a <c>PetBlock</c>'s <c>Name</c> is; null: the creature's own.
    /// A pet's resolved name is <see cref="PetSnapshot.Label"/>.</summary>
    public string? Name { get; init; }
    /// <summary>Its level and size.</summary>
    public PetLevelPolicy Level { get; init; } = new();
    /// <summary>How it follows.</summary>
    public PetMovementPolicy Movement { get; init; } = new();
    /// <summary>How it is revived.</summary>
    public PetRevivePolicy Revive { get; init; } = new();
    /// <summary>Who it may harm and who may harm it.</summary>
    public PetTargetPolicy Targets { get; init; } = new();
    /// <summary>What it becomes when it goes down.</summary>
    public PetDownedPolicy Downed { get; init; } = new();
    /// <summary>How many there may be (blocks/*.json: <c>limits</c>).</summary>
    public PetLimits Limits { get; init; } = new();
    /// <summary>What the owner's HUD shows of it.</summary>
    public PetHudPolicy Hud { get; init; } = new();

    // the roster's and the binder's name for the limits (internal: kept so their code reads as it did)
    internal PetLimits Caps => Limits;
    // the roster's name for the shown name (internal, as above)
    internal string? DisplayName => Name;
}

/// <summary>The answer of <see cref="Pets.Define"/>. Since 0.2.0-dev.32.</summary>
/// <param name="Ok">True when the definition is registered (or the same one was already).</param>
/// <param name="Reason">Why not, in words for a log: its first word is <see cref="Refusal"/> (<c>DefinitionInUse</c>:
/// another one under the id while its pets have bodies or revives; <c>CreatureUnknown</c>, <c>CreatureUnsupported</c>,
/// ...).</param>
/// <param name="Definition">The registered definition.</param>
public sealed record PetDefinitionResult(bool Ok, string? Reason, PetDefinition? Definition) : IKilnResult
{
    /// <summary>Why not, as a value: <see cref="PetRefusal.None"/> when <see cref="Ok"/>, else the first word of
    /// <see cref="Reason"/>.</summary>
    public PetRefusal Refusal => RefusalWords.Of(Ok, Reason, PetRefusal.None, PetRefusal.Other);
}

/// <summary>The checks of a definition, shared by Pets.Define and Kiln.Blocks (which reports them by field path).</summary>
internal static class PetDefinitionRules
{
    public const int MaxLevel = 255;

    /// <summary>The first problem of <paramref name="d"/> as (field path, problem), or null. <paramref name="levelMax"/>:
    /// the creature's highest level when known.</summary>
    public static (string Field, string Problem)? Check(PetDefinition d, int? levelMax = null)
    {
        if (!ValidId(d.Id)) return ("id", $"\"{d.Id}\" is not a Kiln id <mod id>:<name> (lower case letters, digits, '-', '_', '.')");
        if (d.Creature is null || !ValidId(d.Creature)) return ("creature", $"\"{d.Creature}\" is not a creature's Kiln id (game:<name> or <mod id>:<name>)");
        if (d.Name is { } n && (n.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(n) > 64)) return ("name", "a name is 1 to 64 UTF-8 bytes");
        return CheckLevel(d.Level, levelMax) ?? CheckMovement(d.Movement) ?? CheckRevive(d.Revive) ?? CheckTargets(d.Targets) ?? CheckLimits(d.Limits)
               ?? (Enum.IsDefined(d.Downed.Outcome) ? null : ("downed.outcome", $"{d.Downed.Outcome} is not an outcome"));
    }

    public static (string, string)? CheckLevel(PetLevelPolicy l, int? levelMax)
    {
        if (!Enum.IsDefined(l.Mode)) return ("level.mode", $"{l.Mode} is not a level mode");
        var max = Math.Min(MaxLevel, levelMax is > 0 ? levelMax.Value : MaxLevel);
        if (l.Level < 1 || l.Level > max) return ("level.level", $"{l.Level} is outside 1..{max}");
        if (!Finite(l.Scale) || l.Scale < 0.25 || l.Scale > 4) return ("level.scale", $"{Num(l.Scale)} is outside 0.25..4");
        return null;
    }

    public static (string, string)? CheckMovement(PetMovementPolicy m)
    {
        if (!Enum.IsDefined(m.Mode)) return ("movement.mode", $"{m.Mode} is not a movement mode");
        if (!In(m.FollowDistance, 1, 10)) return ("movement.followDistance", $"{Num(m.FollowDistance)} is outside 1..10 m");
        if (!In(m.RepathSeconds, 0.1, 5)) return ("movement.repathSeconds", $"{Num(m.RepathSeconds)} is outside 0.1..5 s");
        if (!Finite(m.RunDistance) || m.RunDistance < m.FollowDistance || m.RunDistance > 30)
            return ("movement.runDistance", $"{Num(m.RunDistance)} is outside followDistance ({Num(m.FollowDistance)})..30 m");
        if (!In(m.CatchUpDistance, 10, 160)) return ("movement.catchUpDistance", $"{Num(m.CatchUpDistance)} is outside 10..160 m");
        if (!In(m.StuckSeconds, 1, 30)) return ("movement.stuckSeconds", $"{Num(m.StuckSeconds)} is outside 1..30 s");
        if (!In(m.GroundSearchMeters, 0.5, 8)) return ("movement.groundSearchMeters", $"{Num(m.GroundSearchMeters)} is outside 0.5..8 m");
        return null;
    }

    public static (string, string)? CheckRevive(PetRevivePolicy r)
    {
        if (!In(r.ChannelSeconds, 0.1, 60)) return ("revive.channelSeconds", $"{Num(r.ChannelSeconds)} is outside 0.1..60 s");
        if (!In(r.RangeMeters, 1, 8)) return ("revive.rangeMeters", $"{Num(r.RangeMeters)} is outside 1..8 m");
        if (!Finite(r.HealthFraction) || r.HealthFraction <= 0 || r.HealthFraction > 1) return ("revive.healthFraction", $"{Num(r.HealthFraction)} is outside (0, 1]");
        if (!In(r.MaxOwnerDisplacement, 0.1, 2)) return ("revive.maxOwnerDisplacement", $"{Num(r.MaxOwnerDisplacement)} is outside 0.1..2 m");
        if (r.Cost is { } c)
        {
            if (c.Item.Value == 0) return ("revive.cost.item", "no item");
            if (c.Count is < 1 or > 999) return ("revive.cost.count", $"{c.Count} is outside 1..999");
        }
        return null;
    }

    public static (string, string)? CheckTargets(PetTargetPolicy t)
    {
        if (!Enum.IsDefined(t.Pvp)) return ("targets.pvp", $"{t.Pvp} is not a PvP mode");
        if (t.HostileFactions is { } list)
            foreach (var f in list)
                if (f == Faction.None || (byte)f >= 32 || !Enum.IsDefined(f) || (PetTargetRule.NeverFair >> (byte)f & 1u) != 0)
                    return ("targets.hostileFactions", $"{f} cannot be a hostile faction (players, their allies, farm animals and no faction never are)");
        return null;
    }

    public static (string, string)? CheckLimits(PetLimits c)
    {
        if (c.RetainedPerCharacter is < 1 or > 128) return ("limits.retainedPerCharacter", $"{c.RetainedPerCharacter} is outside 1..128");
        if (c.LivePerCharacter is < 1 or > 16) return ("limits.livePerCharacter", $"{c.LivePerCharacter} is outside 1..16");
        if (c.LivePerWorld is < 1 or > 256) return ("limits.livePerWorld", $"{c.LivePerWorld} is outside 1..256");
        return null;
    }

    /// <summary>A Kiln id: <c>&lt;mod&gt;:&lt;name&gt;</c>, lower case, at most 128 bytes.</summary>
    public static bool ValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
        var colon = id.IndexOf(':');
        if (colon <= 0 || colon == id.Length - 1 || id.IndexOf(':', colon + 1) >= 0) return false;
        foreach (var ch in id)
            if (!(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or ':' or '~')) return false;
        return true;
    }

    private static bool Finite(double v) => double.IsFinite(v);
    private static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
    internal static string Num(double v) => v.ToString(CultureInfo.InvariantCulture);
}
