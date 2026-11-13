// SPDX-License-Identifier: MIT
// The records of world progress (WorldProgress.cs): what the game stores about the world's flame, its difficulty, Ancient
// Spires, Flame Shrines and Sanctums, Shroud Roots and Elixir Wells, Ancient Obelisks, bosses and chests, and how to
// read each fact for the world or for one character. Usage: docs/kiln/services/world-progress.md.
namespace Kiln;

/// <summary>Where the game keeps a fact.</summary>
public enum ProgressScope : byte
{
    /// <summary>The world's knowledge only: one value for everyone.</summary>
    World = 0,
    /// <summary>Each character's own knowledge (saved with the character).</summary>
    Character = 1,
    /// <summary>The world's knowledge, with a per-character twin that a character in personal quest progression reads instead.</summary>
    Both = 2,
}

/// <summary>A fact as the game stores it: a requirement (a knowledge id compared to a value, or a named query) and where
/// it lives. <see cref="Holds(EntityId)"/> evaluates it with the game's own evaluator, so personal progression and the game's
/// rules for world and player objects apply.</summary>
public sealed record ProgressFact(KnowledgeQuery Query, ProgressScope Scope)
{
    /// <summary>The knowledge id (or the named query's id).</summary>
    public uint Id => Query.Id;

    /// <summary>For the world (<paramref name="target"/> None) or a player in game. Tick thread. Null when it cannot be read:
    /// kiln.knowledge is not loaded, the player is not in game, or a character fact asked of the world.</summary>
    [TickThreadOnly]
    public bool? Holds(EntityId target)
    {
        if (!Knowledge.Available) return null;
        if (target.IsNone && Scope == ProgressScope.Character) return null;
        Services.RequireTickThread("ProgressFact.Holds");
        return Knowledge.TryQuery(target, Query, target.IsNone ? KnowledgeMode.Shared : KnowledgeMode.Player);
    }

    [TickThreadOnly]
    public bool? Holds(Player player) => Holds(player.Entity);

    public override string ToString() => $"{Query} ({Scope})";
}

/// <summary>The world's flame: the Mother Flame's level and the flame altars (the game's NetworkServerProgress on the
/// world's progress entity, the level the Flame Altar menu shows).</summary>
public sealed record FlameStatus(int Level, int Altars, int HighestAltarLevel, int WaterSources, EntityId Entity)
{
    /// <summary>The <c>Level</c> attribute of the progress entity: the store the game writes when the flame levels up (and its
    /// admin command writes); <see cref="Level"/> is its replicated copy. Null when kiln.knowledge's flame part is off.</summary>
    public int? Stored { get; init; }

    /// <summary>The game's maximum flame level (the second value of the <c>Level</c> attribute, <c>flameLevelMax</c>).</summary>
    public int? Max { get; init; }

    /// <summary>What the game's own altar, water-source and fog rules read (<c>ServerProgressSystem.progressLevel</c>).</summary>
    public int? SystemLevel { get; init; }

    public override string ToString() => $"flame level {Level}, {Altars} altar(s) (highest level {HighestAltarLevel}), {WaterSources} water source(s)";
}

/// <summary>What the game derives from a flame level (the balancing table's per-level lists and the fog rule).</summary>
/// <param name="Level">The flame level asked about.</param>
/// <param name="Altars">How many Flame Altars the world may have (<c>altarsPerFlameLevel</c>).</param>
/// <param name="WaterSources">How many water sources it may have (<c>waterSourcesPerFlameLevel</c>).</param>
/// <param name="FogResistance">The Shroud resistance of a player: <c>playerBaseFogResistance + playerFogResistancePerFlameLevel * level</c>.</param>
/// <param name="FogRemovalLevel">The server setting <c>flameLevelFogRemoval</c> (null when kiln.settings is not ready).</param>
/// <param name="FogRemoved">The level reaches <see cref="FogRemovalLevel"/>.</param>
public sealed record FlameLimits(int Level, int Altars, int WaterSources, int FogResistance, int? FogRemovalLevel, bool? FogRemoved)
{
    public override string ToString() =>
        $"flame level {Level}: {Altars} altar(s), {WaterSources} water source(s), fog resistance {FogResistance}" +
        (FogRemovalLevel is { } f ? $", fog removal from level {f} ({(FogRemoved == true ? "yes" : "no")})" : "");
}

/// <summary>The outcome of <see cref="WorldProgress.SetFlameLevel"/>.</summary>
/// <param name="Request">The native request id (0 when refused at once).</param>
/// <param name="Ok">The requested write completed (see <see cref="Mirrored"/>). False with <see cref="Reason"/> otherwise;
/// a failed rollback can leave changed state, reported explicitly in the reason.</param>
/// <param name="Status">"ok", "unavailable", "internal", ...</param>
/// <param name="Requested">What was asked for.</param>
/// <param name="Before">The stored level before and after the write (after: once the game's attribute formula ran).</param>
/// <param name="Max">The game's maximum level (0 when unknown).</param>
/// <param name="Clamped">The game's formula limited the value.</param>
/// <param name="Marked">The component was marked written (so the game saves and replicates it).</param>
/// <param name="Mirrored"><c>NetworkServerProgress.level</c> followed (what players see).</param>
/// <param name="Steps">Steps from the write until it was mirrored.</param>
public sealed record FlameChange(ulong Request, bool Ok, string Status, int Requested, int Before, int After, int Max, bool Clamped,
    bool Marked, bool Mirrored, int Steps, string Reason)
{
    internal static FlameChange Failed(int requested, string status, string reason) =>
        new(0, false, status, requested, 0, 0, 0, false, false, false, 0, reason);

    public override string ToString() => Ok
        ? $"flame level {Before} -> {After}{(Reason.Length > 0 ? $" ({Reason})" : "")}{(Mirrored ? $", mirrored after {Steps} step(s)" : ", not mirrored yet")}"
        : $"flame level {Requested} refused: {Reason}";
}

/// <summary>The world's difficulty: the server's preset and the game settings in effect (kiln.settings).</summary>
/// <param name="Preset">Default, Relaxed, Hard, Survival or Custom (the game's GameSettingsPreset); null when unknown.</param>
/// <param name="Settings">Every game setting's current value by name, as <c>enshrouded_server.json</c> spells it.</param>
public sealed record WorldDifficulty(string? Preset, IReadOnlyDictionary<string, object> Settings)
{
    public bool IsCustom => Preset == "Custom";

    /// <summary>A setting as a number (factors, durations in ns, option values); null when absent or not numeric.</summary>
    public double? Number(string name) => Settings.TryGetValue(name, out var v) ? v switch
    {
        double d => d,
        long l => l,
        bool b => b ? 1 : 0,
        _ => null,
    } : null;

    public override string ToString() => $"preset {Preset ?? "?"}, {Settings.Count} settings";
}

/// <summary>An Ancient Spire: its fast-travel point, the knowledge the game sets when it is unlocked, the per-character
/// "top reached" knowledge and the beacon on top.</summary>
/// <param name="Id">Lower-case, from the fast-travel marker's template name (<c>grassland</c>, <c>capital</c>, ...).</param>
/// <param name="NameTag">Loca tag of the map marker's name (the catalog's displayName).</param>
public sealed record Spire(string Id, string Template, uint NameTag, WorldPosition Position, ProgressFact Unlock,
    ProgressFact? TopReached, ProgressFact? Beacon)
{
    /// <summary>The spire's fast travel is unlocked in this world (tick thread; null when unreadable).</summary>
    [TickThreadOnly]
    public bool? Unlocked => Unlock.Holds(EntityId.None);
    /// <summary>Unlocked as this character sees it (the world's value, or its own twin in personal progression).</summary>
    [TickThreadOnly]
    public bool? UnlockedFor(Player player) => Unlock.Holds(player.Entity);
    /// <summary>The character reached the top (its own knowledge; the game's spire achievement reads it).</summary>
    [TickThreadOnly]
    public bool? TopReachedBy(Player player) => TopReached?.Holds(player.Entity);
    /// <summary>The beacon on top is lit in this world.</summary>
    [TickThreadOnly]
    public bool? BeaconLit => Beacon?.Holds(EntityId.None);
    public override string ToString() => $"spire {Id} ({Template}) at {Position}";
}

public enum ShrineKind : byte
{
    /// <summary>A Flame Shrine.</summary>
    Shrine = 0,
    /// <summary>A Flame Sanctum (a flame temple).</summary>
    Sanctum = 1,
}

/// <summary>A Flame Shrine or Flame Sanctum. The game keeps one fact per shrine: a character found it (came in range of
/// its marker; exploration XP is paid then and the game's "all shrines found" achievement counts it).</summary>
/// <param name="Id">Lower-case, from the game's achievement query (<c>grassland_flameshrine_01</c>).</param>
public sealed record FlameShrine(string Id, ShrineKind Kind, string Region, string? Template, uint NameTag, WorldPosition? Position,
    ProgressFact Discovery)
{
    [TickThreadOnly]
    public bool? FoundBy(Player player) => Discovery.Holds(player.Entity);
    public override string ToString() => $"{Kind.ToString().ToLowerInvariant()} {Id}";
}

/// <summary>A Shroud Root. Destroying it gives skill points (the root of an Elixir Well 3, the others 1); the game keeps
/// one fact per character.</summary>
/// <param name="ElixirWell">This root is the one at an Elixir Well (the well counts as cleared when it is destroyed).</param>
public sealed record ShroudRoot(string Id, string Region, bool ElixirWell, int SkillPoints, WorldPosition? Position, ProgressFact Destruction)
{
    [TickThreadOnly]
    public bool? DestroyedBy(Player player) => Destruction.Holds(player.Entity);
    public override string ToString() => $"{(ElixirWell ? "elixir well root" : "shroud root")} {Id}";
}

/// <summary>An Ancient Obelisk (reading one reveals its region's shrines).</summary>
public sealed record Obelisk(string Id, string Region, WorldPosition? Position, ProgressFact Reading)
{
    [TickThreadOnly]
    public bool? ReadBy(Player player) => Reading.Holds(player.Entity);
    public override string ToString() => $"obelisk {Id}";
}

/// <summary>A boss placed in the world: a creature the game gives a boss health bar, placed directly or by a spawner. The
/// game records a kill for some of them (<see cref="Kill"/>); for the others only the live <see cref="BossKilledEvent"/> tells.</summary>
/// <param name="Id">The boss template's name, lower-case, with <c>_n</c> when the world has several of it.</param>
/// <param name="Spawner">The placed spawner's template, when a spawner places it.</param>
/// <param name="NameTag">Loca tag of the health bar's name.</param>
public sealed record Boss(string Id, string Template, string? Spawner, uint NameTag, WorldPosition Position, ProgressFact? Kill)
{
    /// <summary>The game records this boss's kill.</summary>
    public bool Tracked => Kill is not null;
    /// <summary>Killed in this world (tick thread); null when the game keeps no record or it is a character fact.</summary>
    [TickThreadOnly]
    public bool? Killed => Kill?.Holds(EntityId.None);
    [TickThreadOnly]
    public bool? KilledBy(Player player) => Kill?.Holds(player.Entity);
    public override string ToString() => $"boss {Id} at {Position}";
}

/// <summary>A loot chest placed in the world and its state. <see cref="LootId"/> is the game's loot container id of the
/// placement (stable for a game build). The state is read from the chest's entity, which exists only while its area is
/// loaded (near a player or a base); otherwise <see cref="Loaded"/> is false and the state is null.</summary>
/// <param name="Opened">The chest's loot was generated (it was opened at least once).</param>
/// <param name="Looted">Opened and emptied.</param>
public sealed record ChestStatus(uint LootId, string Template, WorldPosition Position, EntityId Entity, bool? Opened, bool? Looted)
{
    public bool Loaded => !Entity.IsNone;
    public override string ToString() =>
        $"chest 0x{LootId:x8} {Template} at {Position}: {(Loaded ? Looted == true ? "looted" : Opened == true ? "opened" : "untouched" : "not loaded")}";
}

/// <summary>A survivor (an NPC of the game's roster with a rescue requirement).</summary>
public sealed record Survivor(string Id, uint NameTag, ProgressFact Rescue)
{
    [TickThreadOnly]
    public bool? Rescued => Rescue.Holds(EntityId.None);
    [TickThreadOnly]
    public bool? RescuedFor(Player player) => Rescue.Holds(player.Entity);
}

public enum WorldIndexState : byte
{
    NotStarted = 0,
    Building = 1,
    Ready = 2,
    Failed = 3,
}

/// <summary>What world progress can read on this server, and how far its index of the game's data is.</summary>
/// <param name="Places">Spires, shrines, roots, obelisks (the query db and the scene's global chunk).</param>
/// <param name="Bosses">Bosses and root positions (every chunk of the scene and every template).</param>
public sealed record WorldProgressStatus(bool Knowledge, bool World, bool Settings, bool Assets, WorldIndexState Places,
    WorldIndexState Bosses, int Spires, int Shrines, int Roots, int Obelisks, int BossCount, int TrackedBosses, double PlacesSeconds,
    double BossesSeconds, IReadOnlyList<string> Problems)
{
    public override string ToString() =>
        $"places {Places} ({Spires} spires, {Shrines} shrines, {Roots} roots, {Obelisks} obelisks, {PlacesSeconds:0.00} s), " +
        $"bosses {Bosses} ({BossCount}, {TrackedBosses} with a kill record, {BossesSeconds:0.00} s); knowledge {(Knowledge ? "on" : "OFF")}, " +
        $"world {(World ? "on" : "OFF")}, settings {(Settings ? "on" : "OFF")}, assets {(Assets ? "on" : "OFF")}" +
        (Problems.Count > 0 ? $"; {Problems.Count} problem(s): {string.Join("; ", Problems)}" : "");
}
