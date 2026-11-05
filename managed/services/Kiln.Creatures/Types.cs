// SPDX-License-Identifier: MIT
// Public value types of Kiln.Creatures. Enum values are the game's own (keen::enemy01::State, StateCondition,
// keen::ecs::EnemyFeature, keen::FactionType), from the reflection of build 1285601.
namespace Kiln;

/// <summary>What a creature template is (by the AI program its entities run).</summary>
public enum CreatureKind
{
    None = 0,
    /// <summary>Enemy AI (enemy01): monsters, bosses, summons and companions.</summary>
    Enemy = 1,
    Wildlife = 2,
    /// <summary>Farm animals and pets.</summary>
    Animal = 3,
    /// <summary>Villagers and workshop NPCs.</summary>
    Villager = 4,
}

/// <summary>keen::FactionType.</summary>
public enum Faction : byte
{
    Environment = 0, HitAll = 1, Fogger = 2, Vukah = 3, Scavenger = 4, Wildlife = 5, WildBeast = 6, Player = 7,
    AutomatedPlayer = 8, HitOthers = 9, Skeleton = 10,
    /// <summary>Fights on the players' side (summons, companions).</summary>
    PlayerAlly = 11,
    FarmAnimal = 12, Cyclops = 13, Drak = 14, Ancients = 15,
    None = 255,
}

/// <summary>enemy01::State: the enemy AI's state machine.</summary>
public enum EnemyState : byte
{
    Invalid = 0, Start = 1, Idle = 2, Dead = 3,
    /// <summary>Waiting for the game to mark the actor ready (only near players on a dedicated server).</summary>
    Spawning = 4,
    Investigate = 5, Combat = 6, ReturnHome = 7, FleeHome = 8, WaitAtHome = 9, ReactToNoise = 10, Patrol = 11,
    TurnAfterPatrol = 12, WaitAfterPatrol = 13, ControlledByAttack = 14, Roaming = 15, Flee = 16, WaitAfterFlee = 17,
    Falling = 18, Hit = 19, Guard = 20, Interact = 21, Landing = 22, BlockProjectile = 23, MoveToMarker = 24,
    Sleep = 25, Circling = 26, AvoidWater = 27, Revive = 28, Stunned = 29,
}

/// <summary>enemy01::StateCondition: why the state machine moved.</summary>
public enum StateCondition : byte
{
    Death = 0, TargetSelected = 1, TargetLost = 2, IsFinished = 3, TargetUnreachable = 4, TargetOutsideLeash = 5,
    NoiseHeard = 6, ProjectileHeard = 7, HasReachedNoAwarenessState = 8, HasReachedLowAwarenessState = 9,
    HasReachedHighAwarenessState = 10, IsReadyToPatrol = 11, IsReadyToInteract = 12, IsReadyToTriggerAlarm = 13,
    IsControlledByAttack = 14, IsReadyForRoaming = 15, IsReadyForCircling = 16, WantsToFlee = 17, HasBeenHit = 18,
    HasBeenHitRecently = 19, WasHookshotTargetRecently = 20, IsFalling = 21, HitReaction = 22, NeedsToGuard = 23,
    WantsToLand = 24, NeedsToTurn = 25, CanMoveToMarker = 26, WantsToSleep = 27, WantsToAvoidWater = 28, Revive = 29,
    IsExhausted = 30, IsHelpingEnemy = 31, NeedsToInvestigate = 32,
    /// <summary>The state was set without a condition Kiln saw.</summary>
    Unknown = 255,
}

/// <summary>ecs::EnemyFeature as a mask (bit = the game's enum value).</summary>
[Flags]
public enum EnemyFeatures : uint
{
    None = 0,
    UseTaunt = 1u << 0, UseThreatSystem = 1u << 1, UseBlocking = 1u << 2, Move = 1u << 3, Prowl = 1u << 4,
    Rotate = 1u << 5, RotateWhileStanding = 1u << 6, Patrol = 1u << 7, Roaming = 1u << 8, ShowSpawnSequence = 1u << 9,
    ReturnHome = 1u << 10, FleeHome = 1u << 11, Combat = 1u << 12, Interact = 1u << 13, TriggerAlarm = 1u << 14,
    CallForHelp = 1u << 15, Run = 1u << 16, IdleTurn = 1u << 17, BlockProjectiles = 1u << 18,
    SearchForCover = 1u << 19, MoveToMarker = 1u << 20, Sleep = 1u << 21, RunAround = 1u << 22, Circling = 1u << 23,
    StunnedState = 1u << 24, Awareness = 1u << 25, Investigate = 1u << 26,
}

/// <summary>DamageSusceptibility slots (attribute structure order). 1.0 = normal damage taken, 0 = immune, 2 = double.
/// Fire/Fog/Ice/Lightning sit under Magical; Magical itself is computed from its base/multiplier/adder.</summary>
public enum DamageKind
{
    All = 0, Physical = 1, Cut = 2, Pierce = 3, Blunt = 4, Wood = 5, Stone = 6, Metal = 7, Poison = 8, Explosion = 9,
    SkillShot = 10, Magical = 11, MagicalBase = 12, MagicalAdder = 13, MagicalMultiplier = 14, Fire = 15, Fog = 16,
    Ice = 17, Lightning = 18,
}

/// <summary>Template flags from the catalog.</summary>
[Flags]
public enum CreatureFlags : uint
{
    None = 0, Boss = 1, Phases = 2,
    /// <summary>Entities of the template can reach the save (no DoNotSave).</summary>
    Saved = 4,
    GuardsPlayer = 8, Ally = 16, Weakspot = 32, BossHealthBar = 64,
    Catalog = 1u << 8,
    /// <summary>The running game has the template.</summary>
    Loaded = 1u << 9,
    /// <summary>Not in Kiln's shipped catalog: found in the engine (a kiln.content clone, or a newer game build). Only
    /// the name, GUID and kind are known.</summary>
    Runtime = 1u << 10,
}

/// <summary>One creature/NPC template of the catalog.</summary>
public sealed record CreatureTemplate(
    Guid Id, string Name, string DisplayName, CreatureKind Kind, CreatureFlags Flags, Faction Faction, int Level,
    int LevelMax, int EnemyClass, Guid Arsenal, Guid LootLabels, int LootSlotsMin, int LootSlotsMax, float XpMod,
    float Scale, Guid Model, Guid AnimationGraph)
{
    public bool IsBoss => Flags.HasFlag(CreatureFlags.Boss);
    public bool IsVillager => Kind == CreatureKind.Villager;
    public bool IsSaved => Flags.HasFlag(CreatureFlags.Saved);
    public bool IsLoaded => Flags.HasFlag(CreatureFlags.Loaded);
    /// <summary>Found in the engine only (kiln.content clones): <see cref="CreatureFlags.Runtime"/>.</summary>
    public bool IsRuntime => Flags.HasFlag(CreatureFlags.Runtime);
    /// <summary>"Melee", "Ranger", "Mage" or "".</summary>
    public string ClassName => EnemyClass switch { 0 => "Melee", 1 => "Ranger", 2 => "Mage", _ => "" };
    public override string ToString() => DisplayName.Length > 0 ? $"{Name} ({DisplayName})" : Name;
}

/// <summary>A snapshot of one creature (<see cref="CreatureRef.Inspect"/>).</summary>
public sealed record CreatureInfo(
    EntityId Entity, Guid Template, string TemplateName, CreatureKind Kind, bool HasAi, EnemyState State,
    byte AwarenessState, float Awareness, Faction Faction, int Level, int LevelMax, int Health, int HealthMax,
    float Scale, float XpFactor, float SpeedFactor, float DamageMultiplier, EnemyFeatures TemplateFeatures,
    EnemyFeatures EnabledFeatures, EnemyFeatures DisabledFeatures, EntityId Target, EntityId Guard, WorldPosition Home,
    Guid LootLabels, int LootSlotsMin, int LootSlotsMax, bool LootDisabled, bool Dead, bool Ready, bool ForcedReady,
    int Layers, int PrivateCopies);

/// <summary>What kiln.creatures can do on this build (<see cref="Creatures.Status"/>).</summary>
public sealed record CreaturesStatus(
    bool Slots, bool AiHooks, bool ReadyBit, bool Arsenals, bool Reflection, bool Commands, bool Marks, bool LabReady,
    int Catalog, int CatalogLoaded, int EngineCreaturesMissing, int ArsenalCount, int Entities, int Layers,
    int PrivateCopies, int PendingFree, ulong StateChanges, ulong EventsDelivered, ulong EventsDropped,
    ulong CommandsSent, ulong MarksWritten, ulong CopyReads);
