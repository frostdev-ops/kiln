// SPDX-License-Identifier: MIT
// What Kiln.Pets' roster asks of the engine and of the save (internal). GamePetWorld and GamePetStore answer in a game; the
// tests have fakes with a deterministic clock. The roster never touches a game service itself.
namespace Kiln;

/// <summary>An owner in the world: the character, its session entity and where it stands.</summary>
internal readonly record struct PetOwner(CharacterKey Key, EntityRef Entity, WorldPosition Position, int Level, ulong Session);

/// <summary>What the roster asks for when it makes a body.</summary>
internal sealed record PetBodyRequest
{
    public required PetId Pet { get; init; }
    public required CharacterKey Owner { get; init; }
    public required ulong OwnerSession { get; init; }
    public required PetDefinition Definition { get; init; }
    public required PetCreatureInfo Creature { get; init; }
    public required WorldPosition Position { get; init; }
    public required int Level { get; init; }
    public required double HealthFraction { get; init; }
    /// <summary>A revive's body: an inert preview until its cost is paid (19d §4.5); a summon's body activates when ready.</summary>
    public bool Preview { get; init; }
}

/// <summary>A body the world made for a pet (a staging body until <see cref="IPetWorld.Activate"/>).</summary>
internal sealed class PetBody(EntityRef entity, ulong worldEpoch, ulong generation)
{
    public EntityRef Entity { get; } = entity;
    public ulong WorldEpoch { get; } = worldEpoch;
    public ulong Generation { get; } = generation;
    /// <summary>The world's own state for it (leases, native rows).</summary>
    public object? Tag { get; set; }
    public override string ToString() => $"{Entity}/g{Generation}";
}

/// <summary>A body's state as the world reads it at the start of a step.</summary>
/// <param name="Exists">The entity is there.</param>
/// <param name="Health">Its health (signed; 0 or less: lethal).</param>
/// <param name="HealthMax">Its maximum.</param>
/// <param name="Dead">The engine's dead or death-pending flag is set.</param>
/// <param name="DownPending">The native guard latched a down (pkg 5).</param>
/// <param name="Position">Where it is.</param>
/// <param name="ProtectionLost">Kiln can no longer protect it (its rows or a capability went away).</param>
/// <param name="ControlLost">A lease of the pet's ended without its asking (the broker failed it): why; null otherwise.</param>
internal readonly record struct PetBodyReading(bool Exists, int Health, int HealthMax, bool Dead, bool DownPending, WorldPosition Position, bool ProtectionLost = false,
    string? ControlLost = null)
{
    public bool Lethal => Exists && (Dead || DownPending || Health <= 0);
}

internal enum PetBodyPoll { Pending, Ready, Failed }

/// <summary>What the world says of a would-be target (the roster adds what it knows of pets).</summary>
internal readonly record struct PetTargetInfo(bool Exists, bool Alive, bool IsPlayer, CharacterKey Player, Faction Faction, bool EnemyAi);

internal interface IPetWorld
{
    /// <summary>The server world's session number (0: no server world here).</summary>
    ulong WorldEpoch { get; }
    /// <summary>The server step.</summary>
    ulong Step { get; }
    /// <summary>Simulation seconds: the game's world clock in a game (never the step count), the test's in tests.</summary>
    double Now { get; }
    /// <summary>Bodies can be made and retired here (pets.summon); else why not.</summary>
    bool CanMakeBodies(out string reason);
    /// <summary>An owner in game, or null with the reason (offline, no character, account-only key).</summary>
    PetOwner? Owner(PlayerRef who, out string? reason);
    /// <summary>The character's owner in game, or null.</summary>
    PetOwner? OnlineOwner(CharacterKey key);
    /// <summary>Every character in game.</summary>
    IReadOnlyList<PetOwner> OnlineOwners();
    /// <summary>A safe spot on the ground within <paramref name="minRadius"/>..<paramref name="maxRadius"/> of
    /// <paramref name="near"/> for a body of <paramref name="definition"/>; null when none. <paramref name="owner"/> (the
    /// owner's player entity, standing at or next to <paramref name="near"/>) never counts as an entity in the way.</summary>
    WorldPosition? SafeGround(WorldPosition near, double minRadius, double maxRadius, PetDefinition definition, PetCreatureInfo? creature, EntityId owner);
    /// <summary>Makes a protected staging body now (synchronously, on the tick), or null with the reason.</summary>
    PetBody? CreateBody(PetBodyRequest request, out string? reason);
    /// <summary>Whether a staging body is ready (health initialized, placed, quiet); Failed after its bound with the reason.</summary>
    PetBodyPoll Poll(PetBody body, double since, out string? reason);
    /// <summary>A ready staging body becomes the pet's live body (its native row activated, its base lease taken).</summary>
    bool Activate(PetBody body, out string? reason);
    /// <summary>The body now.</summary>
    PetBodyReading Read(PetBody body);
    /// <summary>The owner's health now (null: unknown): a revive channel watches it.</summary>
    int? OwnerHealth(PetOwner owner);
    /// <summary>A would-be attack target, as the game has it now.</summary>
    PetTargetInfo Classify(EntityRef target);
    /// <summary>The declared hostile character pairs (PvP by mod rule): the native rule gets the pairs of characters in game.</summary>
    void SyncHostility(IReadOnlyCollection<(CharacterKey A, CharacterKey B)> pairs);
    /// <summary>A live body's order: its base lease (Follow, Stay) or a PetCombat lease (Attack). Admission only.</summary>
    bool Order(PetBody body, PetOrder order, EntityRef target, out string? reason);
    /// <summary>Removes the body (its producers revoked first) and confirms it is gone; false when it is still there.</summary>
    bool Retire(PetBody body, PetChangeCause cause);
}

/// <summary>The per-character record documents in the world's save, with the receipts of what each staging serialized.</summary>
internal interface IPetStore
{
    bool Available { get; }
    string? Load(CharacterKey key);
    void Write(CharacterKey key, string json);
    void Delete(CharacterKey key);
    event Action<ServiceCharacters.StagingInfo>? Staging;
    event Action<ServiceCharacters.Receipt>? Staged;
    event Action<ServiceCharacters.CommitInfo>? Committed;
}
