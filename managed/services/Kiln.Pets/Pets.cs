// SPDX-License-Identifier: MIT
// Kiln.Pets' face (19d §5.3): definitions, owned records, bodies, orders, revives. Every call that changes a pet runs on the
// server's tick thread and is serialized per pet; the answer is a task that completes at a later step with the settled
// snapshot (or the refusal and its reason). docs/kiln/services/pets.md.
namespace Kiln;

/// <summary>
/// Pets: owned creatures that follow their owner, fight, go down and are revived. Kiln keeps each pet's durable record per
/// character in the world's save, makes and retires its body, holds its movement lease, protects it, and publishes its
/// state; every behaviour choice is the mod's (<see cref="PetDefinition"/>). Since 0.2.0-dev.32.
/// </summary>
/// <remarks>
/// <para>Definitions: <see cref="Define"/> (or a <c>PetBlock</c> through Kiln.Blocks: the same record). A pet record
/// names its definition; a record whose definition is not defined in this session stays dormant, never erased.</para>
/// <para>Error rule: a bad argument throws <see cref="ArgumentException"/>; an off capability throws
/// <see cref="KilnUnavailableException"/> naming its row; a refusal by Kiln or the game is <c>Ok = false</c> with a
/// reason (and its cost receipts, when it came after a cost was taken).</para>
/// </remarks>
public static partial class Pets
{
    static Pets() => Caps.Rows.Ensure();

    /// <summary>Registers a kind of pet as the calling mod's. The same definition again is Ok (no event); another one
    /// under the same id replaces it, except while one of its pets has a live body or a revive running
    /// (<c>DefinitionInUse</c>). A creature Kiln cannot find yet (a game client before content is ready) is checked
    /// again when a body is first made.</summary>
    /// <exception cref="ArgumentException">A value is out of its range, or the id is not the calling mod's
    /// (<c>&lt;mod id&gt;:&lt;name&gt;</c>).</exception>
    [RequiresCapability(Caps.Definitions)]
    public static PetDefinitionResult Define(PetDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (ModContext.CurrentMod is { } mod && !definition.Id.StartsWith(mod.Info.Id.ToLowerInvariant() + ":", StringComparison.Ordinal))
            throw new ArgumentException($"pet definition ids of mod {mod.Info.Id} start with \"{mod.Info.Id.ToLowerInvariant()}:\", not \"{definition.Id}\"", nameof(definition));
        return PetsRuntime.Define(definition, ModContext.CurrentMod?.Info.Id);
    }

    /// <summary>Kiln.Blocks: a PetBlock's definition, as the mod that defined the block (its id is that mod's already).</summary>
    internal static PetDefinitionResult DefineAs(PetDefinition definition, string mod)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return PetsRuntime.Define(definition, mod);
    }

    /// <summary>The definition registered under <paramref name="id"/>, or null. Any thread.</summary>
    [AnyThread]
    public static PetDefinition? Definition(string id) => PetsRuntime.Definition(id);

    /// <summary>Every registered definition, by id. Any thread.</summary>
    [AnyThread]
    public static IReadOnlyList<PetDefinition> Definitions => PetsRuntime.AllDefinitions();

    // ---- records ----

    /// <summary>The character's pets (released tombstones left out). The character need not be online. Server tick thread.</summary>
    /// <exception cref="ArgumentException">No character (none, or an account-only key).</exception>
    /// <exception cref="KilnUnavailableException"><c>pets.records</c> is off (a joined client, no server world).</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static IReadOnlyList<PetSnapshot> Of(CharacterKey owner)
    {
        if (owner.IsNone || owner.IsAccountOnly) throw new ArgumentException("a pet owner is a character (account and character id)", nameof(owner));
        return Roster("Pets.Of").Of(owner);
    }

    /// <summary>The pet, or null (unknown here, or its owner's records are not read yet: <see cref="Of"/> reads them).
    /// A released pet is returned (Life Released) until a committed save holds its release. Server tick thread.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static PetSnapshot? Get(PetId pet) => pet.IsNone ? null : Roster("Pets.Get").Get(pet);

    /// <summary>Gives a character a new pet of <paramref name="definition"/>: a Ready record with no body (Summon brings it
    /// out). The mod's way to hand out pets (a reward, a quest, an admin command); taming (19e) makes records too.</summary>
    /// <returns>Ok with the new pet; or Ok false: <c>DefinitionUnknown</c>, <c>Capacity</c> (the definition's
    /// RetainedPerCharacter, or 128 records), <c>LevelUnavailable</c>, <c>RecordsUnreadable</c>.</returns>
    /// <exception cref="ArgumentException">No character, an empty definition id, or an empty operation id.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static Task<PetResult> Grant(CharacterKey owner, string definition, PetRequest request)
    {
        if (owner.IsNone || owner.IsAccountOnly) throw new ArgumentException("a pet owner is a character (account and character id)", nameof(owner));
        ArgumentException.ThrowIfNullOrEmpty(definition);
        Check(request);
        var r = Roster("Pets.Grant");
        return r.Submit(new PetRoster.Op
        {
            Kind = PetRoster.Kind.Grant, OperationId = request.OperationId, OwnerKey = owner, Definition = definition, Expected = 0,
            Payload = $"grant|{definition}",
        });
    }

    /// <summary>Brings the pet out: a Ready pet gets a protected body 2 to 4 m from its owner, at its kept health, following;
    /// a Downed pet's recovery anchor is put next to its owner (no body, no health). Already out: Ok, nothing changes.</summary>
    /// <returns>Ok with the pet once its body is live (or its anchor shown); else <c>Busy</c>, <c>Capacity</c>,
    /// <c>NoSafeGround</c>, <c>BodyNotReady</c>, <c>DefinitionUnknown</c>, <c>CreatureUnknown</c>, <c>NotOwner</c>,
    /// <c>OwnerOffline</c>, ... A body that goes down before the answer is <c>BodyDowned</c> with the real downed pet.</returns>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Summon)]
    public static Task<PetResult> Summon(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Summon, "Pets.Summon", Caps.Summon, owner, pet, request);

    /// <summary>Puts the pet away: a live body is retired (its health kept), a downed pet's anchor is hidden (never healed);
    /// the owner's wish to have it out is cleared.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Summon)]
    public static Task<PetResult> Dismiss(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Dismiss, "Pets.Dismiss", Caps.Summon, owner, pet, request);

    /// <summary>Releases the pet for good: a body is retired first, then the record becomes a tombstone that goes once a
    /// committed save holds it. No wild creature is left behind.</summary>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Records)]
    public static Task<PetResult> Release(PlayerRef owner, PetId pet, PetRequest request) => Request(PetRoster.Kind.Release, "Pets.Release", Caps.Records, owner, pet, request);

    /// <summary>Orders a live pet: follow, stay, or attack <paramref name="target"/> (needs <c>pets.fight</c>, and the pet's
    /// target rule must allow it: else <c>TargetProtected</c> and no threat is added).</summary>
    /// <exception cref="ArgumentException">Attack without a target.</exception>
    [ServerOnly, TickThreadOnly, RequiresCapability(Caps.Follow)]
    public static Task<PetResult> Command(PlayerRef owner, PetId pet, PetOrder order, EntityRef target, PetRequest request)
    {
        if (!Enum.IsDefined(order)) throw new ArgumentException($"{order} is not an order", nameof(order));
        if (order == PetOrder.Attack && target.IsNone) throw new ArgumentException("an attack order needs a target", nameof(target));
        if (order == PetOrder.Attack) Require(Caps.Fight);
        return Request(PetRoster.Kind.Command, "Pets.Command", Caps.Follow, owner, pet, request, o =>
        {
            o.Order = order;
            o.Target = order == PetOrder.Attack ? target : EntityRef.None;
            o.Payload += $"|{order}|{o.Target.Id.Value}:{o.Target.Serial}";
        });
    }

    // ---- helpers ----

    private static PetRoster Roster(string api)
    {
        Services.RequireTickThread(api);
        return PetsRuntime.Roster ?? throw Unavailable(Caps.Records, "no server world runs here");
    }

    private static void Check(PetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OperationId == Guid.Empty) throw new ArgumentException("an operation id is a new Guid per request", nameof(request));
    }

    private static void Require(string row)
    {
        if (PetsRuntime.TestRows || Capabilities.Is(row)) return;
        throw Unavailable(row, Capabilities.Get(row).ReasonText);
    }

    private static KilnUnavailableException Unavailable(string row, string why)
    {
        Caps.Rows.Ensure();
        return ServiceCaps.Unavailable(row, why);
    }

    internal static Task<PetResult> Request(PetRoster.Kind kind, string api, string row, PlayerRef owner, PetId pet, PetRequest request, Action<PetRoster.Op>? fill = null)
    {
        if (pet.IsNone) throw new ArgumentException("no pet", nameof(pet));
        if (owner.IsNone) throw new ArgumentException("no owner", nameof(owner));
        Check(request);
        var r = Roster(api);
        Require(row);
        var who = r.World.Owner(owner, out var why);
        if (who is null) return Task.FromResult(new PetResult(false, why ?? "OwnerOffline", r.Get(pet), request.OperationId, []));
        var op = new PetRoster.Op
        {
            Kind = kind, OperationId = request.OperationId, OwnerKey = who.Value.Key, Who = owner, Pet = pet, Expected = request.ExpectedStateRevision,
            Payload = $"{kind}|{pet}|{request.ExpectedStateRevision}",
        };
        fill?.Invoke(op);
        return r.Submit(op);
    }
}

/// <summary>A pet definition was registered or replaced (<see cref="Pets.Define"/>). Posted on the tick thread, at the
/// step after it. Since 0.2.0-dev.32.</summary>
public sealed class PetDefinitionChangedEvent : KilnEvent
{
    internal PetDefinitionChangedEvent(PetDefinition? before, PetDefinition after) => (Before, After) = (before, after);
    /// <summary>The definition it replaced; null for a new one.</summary>
    public PetDefinition? Before { get; }
    /// <summary>The definition now.</summary>
    public PetDefinition After { get; }
}
