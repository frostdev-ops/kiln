// SPDX-License-Identifier: MIT
using System.Runtime.InteropServices;

namespace Kiln.TestKit;

/// <summary>What a program of the game does to a component every step (<see cref="FakeWorld.GameWrites{T}"/>).</summary>
/// <typeparam name="T">The component type.</typeparam>
/// <param name="entity">The entity whose component it is.</param>
/// <param name="value">The component: change it in place.</param>
public delegate void FakeGameWriter<T>(EntityId entity, ref T value) where T : unmanaged;

/// <summary>One tracked write a mod asked for, as the fake world ran it (<see cref="FakeWorld.Writes"/>).</summary>
/// <param name="Step">The harness step at whose start the write ran: the step after the call.</param>
/// <param name="ModId">The mod that asked; null for a call outside any mod.</param>
/// <param name="Entity">The entity.</param>
/// <param name="Component">The component's short name.</param>
/// <param name="Offset">The first byte the write named.</param>
/// <param name="Status">What became of it.</param>
/// <param name="Before">The bytes it named, as they were; empty when nothing was read.</param>
/// <param name="After">The same bytes afterwards.</param>
public sealed record FakeWorldWrite(ulong Step, string? ModId, EntityId Entity, string Component, int Offset, WorldWriteStatus Status,
    byte[] Before, byte[] After);

/// <summary>
/// The fake world of a harness (<see cref="KilnHarness.Entities"/>): entities with components laid out by Kiln.Game's
/// types, under the real Kiln.World. A mod's <see cref="World"/> calls (<c>Has</c>, <c>TryRead</c>, <c>Find</c>,
/// <c>Query</c>, <c>Position</c>, <c>Write</c>, <c>Update</c>, <c>WriteField</c>, <c>WriteAt</c>, <c>WriteRaw</c>), Kiln.Game's
/// typed reads (<c>Ecs.TryGet</c>) and <see cref="Reflected"/> components run on it unchanged, and a tracked write
/// answers as in the game: one step later, refused for a type the mod's manifest does not list, with
/// <see cref="ComponentWrittenEvent"/> for a change.
/// </summary>
/// <remarks>
/// <para>The methods here are the test's hand, not the game's API: <see cref="Set{T}"/> puts a component on an entity as
/// if the game had (no write tracking, no event). Every method runs on the harness's tick thread.</para>
/// <para><b>Tracked writes</b> follow kiln.world's own rules (the boundaries are tested against the native service's
/// numbers): a write runs at the start of the harness's next step, after the fake game programs of that step
/// (<see cref="GameWrites{T}"/>), and its task completes in that step; for a type a fake game program writes too, one
/// step later, with <see cref="WorldWriteResult.Kept"/>. One step runs at most 512 requests (the rest wait for the next
/// steps) and delivers at most 1024 answers; 4096 requests not yet answered, refusals included, are the most that can
/// wait. A request whose entity is removed while it waits never runs, also when the id comes back (a player's entity is
/// slot + 1, and the next player in the slot gets it). The component types a mod may write are the <c>"writes"</c> of
/// the manifests in the harness's mods folder at its first step, as kiln.world reads them when a server starts: a mod
/// loaded later (a hot load) is refused, as in the game; a name Kiln.Game's types do not know is refused as unknown and
/// takes none of the 92 places. A mod built with Kiln.Sdk has the list filled; for a mod loaded from its class,
/// <see cref="ModManifest.Writes"/> lists them.</para>
/// <para><b>Where an entity stands</b> is its <c>CurrentTransform</c> component, as in the game:
/// <see cref="Create(WorldPosition)"/> and <see cref="SetPosition"/> write it, and a write into its bytes moves the entity.
/// A fake player's <c>CurrentTransform</c> follows <see cref="FakePlayer.Position"/>, and a player's components go with
/// the player: the next player in the slot starts with none.</para>
/// <para><b>Not modelled:</b> the game's attribute formulas (a write over the maximum is not clamped), which types the
/// game saves or replicates (<see cref="WorldWriteResult.SaveTracked"/> is null unless <see cref="Track"/> says), a save
/// of the world's entities, world programs (<see cref="EcsPrograms"/> is unavailable: there is no scheduler), game
/// systems and their hooks, the game's event streams, teleport, and everything behind a component (a Health of 0 kills
/// nobody).</para>
/// </remarks>
public sealed unsafe class FakeWorld
{
    private readonly KilnHarness _harness;

    internal FakeWorld(KilnHarness harness) => _harness = harness;

    private EntitiesFake Fake => _harness.EntitiesFake ?? throw new InvalidOperationException("the harness has no world yet");

    /// <summary>Makes an entity with no component and no position.</summary>
    public EntityId Create() => _harness.Call(() => new EntityId(Fake.Create(null)));

    /// <summary>Makes an entity that stands at <paramref name="position"/>: its <c>CurrentTransform</c> (identity rotation,
    /// scale 1), which <see cref="World.Position"/> and <see cref="World.Transform"/> read.</summary>
    public EntityId Create(WorldPosition position) => _harness.Call(() => new EntityId(Fake.Create(position)));

    /// <summary>Takes an entity and its components out of the world. False when there is none.</summary>
    public bool Destroy(EntityId entity) => _harness.Call(() => Fake.Destroy(entity.Value));

    /// <summary>Every entity made here (and every fake player's entity that was given a component), by id.</summary>
    public IReadOnlyList<EntityId> All => _harness.Call(() => (IReadOnlyList<EntityId>)[.. Fake.Entities.Select(e => new EntityId(e))]);

    /// <summary>Whether the entity is in the world: made here, or a fake player's.</summary>
    public bool Exists(EntityId entity) => _harness.Call(() => Fake.Exists(entity.Value));

    /// <summary>Moves an entity (or gives it its first position): the position in its <c>CurrentTransform</c>.</summary>
    /// <exception cref="ArgumentException">No such entity.</exception>
    public void SetPosition(EntityId entity, WorldPosition position) => _harness.Call(() => Fake.SetPosition(entity.Value, position));

    /// <summary>Puts component <typeparamref name="T"/> on an entity, or replaces its value, as if the game had: no write
    /// tracking, no event, at once. The entity may be a fake player's (<see cref="FakePlayer.Entity"/>).</summary>
    /// <returns>This world, to chain.</returns>
    /// <exception cref="ArgumentException">No such entity.</exception>
    public FakeWorld Set<T>(EntityId entity, T value) where T : unmanaged, IGameComponent<T>
    {
        var attribute = typeof(T).GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IGameAttribute<,>));
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value)).ToArray();
        var name = T.Name;
        _harness.Call(() => Fake.Set(entity.Value, name, bytes, attribute));
        return this;
    }

    /// <summary>The entity's component.</summary>
    /// <exception cref="InvalidOperationException">The entity has no such component (or it has another size).</exception>
    public T Get<T>(EntityId entity) where T : unmanaged, IGameComponent<T> =>
        TryGet<T>(entity, out var value) ? value : throw new InvalidOperationException($"entity {entity} has no {T.Name} of {sizeof(T)} bytes in the fake world");

    /// <summary>The entity's component; false when it has none (or one of another size).</summary>
    public bool TryGet<T>(EntityId entity, out T value) where T : unmanaged, IGameComponent<T>
    {
        var bytes = GetBytes(entity, T.Name);
        if (bytes is null || bytes.Length != sizeof(T))
        {
            value = default;
            return false;
        }
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    /// <summary>Takes component <typeparamref name="T"/> off an entity. False when it had none.</summary>
    public bool Remove<T>(EntityId entity) where T : unmanaged, IGameComponent<T>
    {
        var name = T.Name;
        return _harness.Call(() => Fake.Remove(entity.Value, name));
    }

    /// <summary>Puts a component on an entity by its game name, as bytes: for a component no generated type names. As
    /// <see cref="Set{T}"/>: untracked, at once.</summary>
    /// <exception cref="ArgumentException">No such entity, an empty name, no bytes, or a name that is no component type of
    /// the game (when Kiln.Game's types are beside the tests: an entity of the game carries no other).</exception>
    public FakeWorld SetBytes(EntityId entity, string component, ReadOnlySpan<byte> bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        var copy = bytes.ToArray();
        _harness.Call(() => Fake.Set(entity.Value, component, copy, false));
        return this;
    }

    /// <summary>A copy of the component's bytes; null when the entity has none.</summary>
    public byte[]? GetBytes(EntityId entity, string component)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        return _harness.Call(() => Fake.Bytes(entity.Value, component));
    }

    /// <summary>A program of the game that writes <typeparamref name="T"/>: from now on <see cref="World.WriteInfo"/> and
    /// every tracked write of the type name it (<see cref="WorldWriteResult.GameWriters"/>), the write's answer waits one
    /// more step, and <paramref name="each"/>, when given, runs on every entity's <typeparamref name="T"/> at each step,
    /// before Kiln's writes of that step. A writer that derives the value every step makes a tracked write come back
    /// with <see cref="WorldWriteResult.Kept"/> false. Dispose the result to end the program.</summary>
    /// <param name="program">The program's name, as the game names its own (<c>fuel_setup</c>).</param>
    /// <param name="each">What it does to the component every step; null: it only counts as a writer.</param>
    /// <exception cref="ArgumentException"><paramref name="program"/> is null or empty.</exception>
    public IDisposable GameWrites<T>(string program, FakeGameWriter<T>? each = null) where T : unmanaged, IGameComponent<T>
    {
        ArgumentException.ThrowIfNullOrEmpty(program);
        var name = T.Name;
        Action<uint, nint, int>? run = each is null ? null : (entity, data, size) =>
        {
            if (size == sizeof(T)) each(new EntityId(entity), ref *(T*)data);
        };
        var writer = _harness.Call(() => Fake.AddWriter(name, program, run));
        return new Ended(() => _harness.Call(() => Fake.RemoveWriter(writer)));
    }

    /// <summary>Says what the game's write tracking does with a component type, which the kit cannot know: a tracked
    /// write of it then answers <see cref="WorldWriteResult.SaveTracked"/> and
    /// <see cref="WorldWriteResult.ReplicationTracked"/> with these values instead of null.</summary>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null or empty.</exception>
    public void Track(string component, bool save, bool replication)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        _harness.Call(() => Fake.Track(component, save, replication));
    }

    /// <summary>Every tracked write a mod asked for that reached the world, in the order they ran, with what became of
    /// each (a write refused because its type is not listed never runs and is not here: its answer says why).</summary>
    public IReadOnlyList<FakeWorldWrite> Writes => _harness.Call(() => Fake.Record);

    private sealed class Ended(Action end) : IDisposable
    {
        private Action? _end = end;

        public void Dispose() => Interlocked.Exchange(ref _end, null)?.Invoke();
    }
}
