// SPDX-License-Identifier: MIT
using Kiln;

namespace Kiln.GameTypes;

/// <summary>
/// Typed, name-stable reads of ECS components, using the generated layouts (see Generated.cs) over kiln.world. The
/// struct's engine field offsets come from the game's own reflection, so <c>Components.Get&lt;NetworkHealth&gt;(e).health</c>
/// reads the real component memory on this build. Call on the tick thread (game state is single-threaded).
/// </summary>
[Obsolete("Use Kiln.Ecs.")]
public static class Components
{
    /// <summary>True when kiln.world is available (component reads work on this build).</summary>
    public static bool Available => World.Available;

    /// <summary>The ECS component name for a generated type (e.g. "NetworkHealth"), or null if it is not generated.</summary>
    public static string? NameOf<T>() where T : unmanaged =>
        ComponentRegistry.Names.TryGetValue(typeof(T), out var n) ? n : null;

    /// <summary>Reads component <typeparamref name="T"/> of <paramref name="entity"/>. False when the entity has no
    /// such component (or the type is not generated).</summary>
    public static bool TryGet<T>(EntityId entity, out T value) where T : unmanaged
    {
        value = default;
        var name = NameOf<T>();
        return name is not null && (!World.Available || Checked<T>(name)) && World.TryRead(entity, name, out value);
    }

    /// <summary>The component, or null when the entity does not have it.</summary>
    public static T? Get<T>(EntityId entity) where T : unmanaged => TryGet<T>(entity, out var v) ? v : null;

    /// <summary>True if the entity has component <typeparamref name="T"/>.</summary>
    public static bool Has<T>(EntityId entity) where T : unmanaged =>
        NameOf<T>() is { } name && (!World.Available || Checked<T>(name)) && World.Has(entity, name);

    /// <summary>Every entity carrying component <typeparamref name="T"/>.</summary>
    public static IReadOnlyList<EntityId> All<T>() where T : unmanaged =>
        NameOf<T>() is { } name && (!World.Available || Checked<T>(name)) ? World.Find(name) : [];

    // The first-use check of Kiln.Ecs (docs/kiln/typegen.md §4), by the type's game name.
    private static bool Checked<T>(string name) => EcsSupport.Usable(typeof(T), "keen::ecs::" + name, legacy: typeof(T));
}
