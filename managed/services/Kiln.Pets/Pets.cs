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
