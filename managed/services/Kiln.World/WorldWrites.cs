// SPDX-License-Identifier: MIT
// Tracked writes (kiln.world v3, native/core/abi/services/kiln_world.h "tracked writes"): World.Write<T>,
// World.Update<T>, World.WriteField and World.WriteAt change a component through the game's own write tracking, inside
// Kiln's write program, so the save and replication see the change. Capability world.tracked_writes. Guide:
// docs/kiln/services/world.md; evidence: docs/re/world.md "Tracked writes".
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Kiln;

/// <summary>What became of a tracked write (<see cref="WorldWriteResult.Status"/>).</summary>
public enum WorldWriteStatus
{
    /// <summary>Written through the game's tracked path: marked for the save and for replication.</summary>
    Written = 0,

    /// <summary>The component already held the value: nothing was written or marked.</summary>
    Unchanged = 1,

    /// <summary>The entity does not exist or has no such component.</summary>
    NoComponent = 2,

    /// <summary>The bytes do not fit the entity's component (its size differs on this game build).</summary>
    OutOfRange = 3,

    /// <summary>The component is not in the write list for this mod: no <c>"writes"</c> entry in its kiln.mod.json when
    /// the server started, an unknown component name, or the list was full. The reason says what to add.</summary>
    NotDeclared = 4,

    /// <summary>The write was turned down: an attribute's header would have changed.</summary>
    Refused = 5,

    /// <summary>The change callback threw, or the game's write faulted. The component is as it was.</summary>
    Faulted = 6,

    /// <summary>It never ran: no server world, or the write program is off.</summary>
    Dropped = 7,

    /// <summary>The generated type's layout is not the running game build's: nothing was sent to the game.</summary>
    LayoutMismatch = 100,
}

/// <summary>Whether a component type can be written right now (<see cref="WorldWriteInfo.State"/>).</summary>
public enum WorldWriteState
{
    /// <summary>Yes.</summary>
    Ready = 0,

    /// <summary>Listed, and the write program waits for a server world.</summary>
    Pending = 1,

    /// <summary>No loaded mod listed it under <c>"writes"</c> when the server started.</summary>
    NotDeclared = 2,

    /// <summary>Another mod listed it; the asking mod did not.</summary>
    NotListed = 3,

    /// <summary>Listed, but this game build has no component type of that name.</summary>
    UnknownComponent = 4,

    /// <summary>Listed after the write program's 91 component types were taken.</summary>
    OverLimit = 5,

    /// <summary>Tracked writes do not work here (<see cref="Capabilities.Why"/> of <c>world.tracked_writes</c> says why).</summary>
    Off = 6,

    /// <summary>The mod's <c>"writes"</c> in kiln.mod.json could not be read when the server started (not a list, a name
    /// that is not a component's, more than 64 entries): the mod loaded, none of its tracked writes is taken, and kiln.log
    /// and each write's answer say what is wrong with the list.</summary>
    ListRefused = 7,
}

/// <summary>A change to a component, made on the value the entity holds at that moment (<see cref="World.Update{T}"/>).</summary>
/// <typeparam name="T">The component type.</typeparam>
/// <param name="value">The component's value after the game's own programs of the step: change it in place.</param>
public delegate void WorldChange<T>(ref T value) where T : unmanaged;

/// <summary>The answer to a tracked write (<see cref="World.Write{T}"/>, <see cref="World.Update{T}"/>,
/// <see cref="World.WriteField"/>, <see cref="World.WriteAt{T}"/>). <see cref="Ok"/> false with a <see cref="Reason"/>
/// when the game did not take it. Since 0.2.0-dev.27.</summary>
[ServerOnly]
public sealed record WorldWriteResult : IKilnResult
{
    internal WorldWriteResult() { }

    /// <summary>What became of the write.</summary>
    public WorldWriteStatus Status { get; internal init; }

    /// <summary>True when the component holds the value now: written, or it held it already.</summary>
    public bool Ok => Status is WorldWriteStatus.Written or WorldWriteStatus.Unchanged;

    /// <summary>Why it did not happen, as a sentence; null when <see cref="Ok"/>.</summary>
    public string? Reason { get; internal init; }

    /// <summary>The entity.</summary>
    public EntityId Entity { get; internal init; }

    /// <summary>The component's short name.</summary>
    public string Component { get; internal init; } = "";

    /// <summary>True when bytes changed (<see cref="WorldWriteStatus.Written"/>).</summary>
    public bool Changed => Status == WorldWriteStatus.Written;

    /// <summary>How many of the game's own programs write this component type on the server. 0: Kiln's write is the only
    /// one and the value stays until a mod changes it. Otherwise Kiln's write lands after theirs in the same step, and
    /// from the next step on they see it and may change it again (<see cref="Kept"/>).</summary>
    public int GameWriterCount { get; internal init; }

    /// <summary>The names of the first of those programs (<c>fuel_setup</c>, <c>actor_interaction</c>).</summary>
    public IReadOnlyList<string> GameWriters { get; internal init; } = [];

    /// <summary>For a component the game writes too: whether the written bytes were still there one step later, after the
    /// game's programs ran once more. False: something wrote over it in that step. <see cref="WrittenOverBy"/> names it
    /// when it is a policy of Kiln's (a water refill); otherwise it was one of <see cref="GameWriters"/> (a value the
    /// game derives every step cannot be set this way) or a program of another mod or plugin. Null: not checked (no
    /// game writer, nothing written, or the world closed).</summary>
    public bool? Kept { get; internal init; }

    /// <summary>For a write that was not <see cref="Kept"/>: the policy of Kiln's that wrote over it in the step the write
    /// was checked (its own report of that step shows it changing the written value), as a phrase that names it (<c>"kiln.items' water refill 1 (keep full) of the inventories of 1, set by kiln"</c>). Such a policy
    /// writes the component again every step, after the game's programs: a water refill (<c>Items.Fuel.Refill</c>,
    /// <c>Items.Fuel.KeepFull</c>, or a player's <c>Water</c> rule; the Creative menu's rule set has one) keeps every
    /// container it covers at its level, so a tracked write of their <c>Fuel</c> lasts one step. End that policy (or
    /// narrow it to other items) before the write, or change the water through it. Null when the write was kept, was
    /// not checked, or Kiln cannot show that one of its policies wrote over it (then one of the game's programs most
    /// likely did); never the name of a policy that did not write. Since 0.2.0-dev.27.</summary>
    public string? WrittenOverBy { get; internal init; }

    /// <summary>Whether the game's write tracking records a change of this component type for the save (the game's own
    /// per-type bit); null when this build does not tell. True: the write marked the entity, and the next save writes
    /// it. False: no write of this type marks anything, the game's own included, so on an entity a save has already
    /// written the value is gone after a restart, tracked or not.</summary>
    public bool? SaveTracked { get; internal init; }

    /// <summary>Whether the game's write tracking records a change of this component type for replication (the game's
    /// own per-type bit); null when this build does not tell. False does not mean players never see the value: the game
    /// sends many values through another component it derives from this one (Fuel through NetworkFuel), or by other
    /// means than this tracking. What a player's screen shows is proven only in a real game.</summary>
    public bool? ReplicationTracked { get; internal init; }

    /// <summary>True for an attribute (Health, Stamina, Fuel): the game's formulas ran after the write, so
    /// <see cref="After"/> may differ from what was asked (a clamp to the maximum).</summary>
    public bool Attribute { get; internal init; }

    /// <summary>The first byte written, within the component.</summary>
    public int Offset { get; internal init; }

    /// <summary>The size of the entity's component in bytes; 0 when it was not found.</summary>
    public int ComponentSize { get; internal init; }

    /// <summary>Sim steps from the call to this answer.</summary>
    public int Steps { get; internal init; }

    /// <summary>The bytes the write named, as the component held them before; empty when nothing was read.</summary>
    public ReadOnlyMemory<byte> Before { get; internal init; }

    /// <summary>The same bytes afterwards.</summary>
    public ReadOnlyMemory<byte> After { get; internal init; }

    /// <summary><c>"Written: Fuel of 1042 (9 game writers, kept, marked for the save)"</c>, <c>"NotDeclared: Fuel of 1042: ..."</c>,
    /// <c>"Written: Fuel of 1042 (9 game writers, written over one step later by kiln.items' water refill 1 (keep full) of
    /// the inventories of 1, set by kiln, marked for the save)"</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder().Append(Status).Append(": ").Append(Component).Append(" of ").Append(Entity);
        if (!Ok) return text.Append(": ").Append(Reason).ToString();
        var notes = new List<string>();
        if (GameWriterCount > 0) notes.Add($"{GameWriterCount} game writer{(GameWriterCount == 1 ? "" : "s")}");
        if (Kept is { } kept)
            notes.Add(kept ? "kept" : WrittenOverBy is { } by ? $"written over one step later by {by}" : "written over one step later");
        if (SaveTracked is { } saved) notes.Add(saved ? "marked for the save" : "a type the save does not track");
        return notes.Count == 0 ? text.ToString() : text.Append(" (").AppendJoin(", ", notes).Append(')').ToString();
    }
}

/// <summary>What kiln.world knows about writing one component type (<see cref="World.WriteInfo"/>). Since 0.2.0-dev.27.</summary>
/// <param name="Component">The component's short name.</param>
/// <param name="State">Whether the calling mod can write it right now.</param>
/// <param name="Reason">Why not, as a sentence that says what to do; null when <see cref="CanWrite"/>.</param>
/// <param name="GameWriterCount">The game's own programs that write it on the server, every step they run.</param>
/// <param name="GameWriters">The names of the first of them.</param>
/// <param name="InitWriterCount">The game's programs that write it only when an entity is created.</param>
/// <param name="SaveTracked">Whether the game's write tracking records changes of the type for the save; null when this
/// build does not tell (<see cref="WorldWriteResult.SaveTracked"/>).</param>
/// <param name="ReplicationTracked">Whether it records them for replication; null when this build does not tell
/// (<see cref="WorldWriteResult.ReplicationTracked"/>).</param>
/// <param name="Attribute">An attribute (Health, Stamina, Fuel): the game's formulas run after a write.</param>
/// <param name="ComponentSize">The type's size in bytes on this build; 0 when not known.</param>
[ServerOnly]
public sealed record WorldWriteInfo(string Component, WorldWriteState State, string? Reason, int GameWriterCount,
    IReadOnlyList<string> GameWriters, int InitWriterCount, bool? SaveTracked, bool? ReplicationTracked, bool Attribute, int ComponentSize)
{
    /// <summary><see cref="State"/> is <see cref="WorldWriteState.Ready"/>.</summary>
    public bool CanWrite => State == WorldWriteState.Ready;

    /// <summary>False while the running server world has not published what it knows of the type (no world runs yet, or
    /// a new one is being set up): the counts are 0 and the tracked flags null, not the previous world's. Ask again after
    /// a step. <see cref="State"/> is known either way.</summary>
    public bool FactsKnown { get; init; } = true;
}

/// <summary>A tracked write changed a component: posted on the tick thread when the write's answer arrives, one step
/// after the call (two when the game writes the type too), for every write a mod made through <see cref="World"/> that
/// changed bytes. Only built while the event has subscribers. Not cancelable (it already happened). A write the game
/// makes itself raises nothing. Since 0.2.0-dev.27.</summary>
[ServerOnly]
public sealed class ComponentWrittenEvent : KilnEvent, IEntityEvent
{
    internal ComponentWrittenEvent(WorldWriteResult result, string? modId)
    {
        Result = result;
        ModId = modId;
    }

    /// <summary>The entity whose component changed.</summary>
    public EntityId Entity => Result.Entity;

    /// <summary>The component's short name.</summary>
    public string Component => Result.Component;

    /// <summary>The id of the mod that wrote; null for Kiln's own tools.</summary>
    public string? ModId { get; }

    /// <summary>The write's answer: the bytes before and after, who else writes the type, whether the save tracks it.</summary>
    public WorldWriteResult Result { get; }

    /// <inheritdoc/>
    public bool Involves(EntityId entity) => entity == Entity;
}

public static unsafe partial class World
{
    /// <summary>The capability of tracked writes: <c>world.tracked_writes</c>.</summary>
    public const string TrackedWritesCapability = "world.tracked_writes";

    /// <summary>True when tracked writes work in this process right now (<see cref="Capabilities.Why"/> of
    /// <see cref="TrackedWritesCapability"/> says why not). A component still has to be listed: <see cref="WriteInfo"/>.</summary>
    [AnyThread]
    public static bool SupportsTrackedWrites => WorldCapabilities.Is(TrackedWritesCapability);

    /// <summary>Sets component <typeparamref name="T"/> of <paramref name="entity"/> to <paramref name="value"/> through
    /// the game's write tracking, so the game's save and its replication learn of the change as they do of the game's
    /// own (where the game tracks that component type: see the result). The write happens in the next sim step, after the game's own programs of that
    /// step; the task completes on the tick thread one step later, two when the game writes the type too.
    /// The type must be listed under <c>"writes"</c> in the mod's kiln.mod.json when the server starts: Kiln.Sdk adds it
    /// from this call at build time. An attribute (Health, Stamina, Fuel) takes its header from the game: use
    /// <see cref="Update{T}"/> to change its values. The write goes to the entity the id names at the call: its
    /// generation (its SpawnTime) is compared right before the write, and a write for an entity without one (loaded
    /// from the save, an item entity, maybe a player) is not kept waiting behind a full step (it answers
    /// <see cref="WorldWriteStatus.Dropped"/> then). Since 0.2.0-dev.27.</summary>
    /// <returns>The answer; <see cref="WorldWriteResult.Ok"/> false with the reason when the game did not take it
    /// (no such entity, not listed, another layout on this build).</returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.tracked_writes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(TrackedWritesCapability)]
    public static Task<WorldWriteResult> Write<T>(EntityId entity, T value) where T : unmanaged, IGameComponent<T>
    {
        var t = WorldWrites.Use("World.Write", entity);
        if (WorldWrites.LayoutProblem(typeof(T), T.Name, T.QualifiedName, T.Size, sizeof(T), entity) is { } bad) return Task.FromResult(bad);
        return WorldWrites.Submit(t, entity, T.Name, 0, new ReadOnlySpan<byte>(&value, sizeof(T)), whole: true, change: null);
    }

    /// <summary>Changes component <typeparamref name="T"/> of <paramref name="entity"/>: <paramref name="change"/> gets
    /// the value the entity holds after the game's own programs of the step and edits it in place; the result is written
    /// through the game's write tracking (see <see cref="Write{T}"/>). The way to change part of a component, and the
    /// only way to change an attribute's values. <paramref name="change"/> runs inside the game's step, on the tick
    /// thread on a dedicated server and maybe on a game worker thread in a hosted game: it may touch only the value, no
    /// Kiln or game API. When it throws, nothing is written (<see cref="WorldWriteStatus.Faulted"/>). The type must be
    /// listed under <c>"writes"</c>, as for <see cref="Write{T}"/>. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.tracked_writes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(TrackedWritesCapability)]
    public static Task<WorldWriteResult> Update<T>(EntityId entity, WorldChange<T> change) where T : unmanaged, IGameComponent<T>
    {
        ArgumentNullException.ThrowIfNull(change);
        var t = WorldWrites.Use("World.Update", entity);
        if (WorldWrites.LayoutProblem(typeof(T), T.Name, T.QualifiedName, T.Size, sizeof(T), entity) is { } bad) return Task.FromResult(bad);
        return WorldWrites.Submit(t, entity, T.Name, 0, default, whole: true, change: (data, size) =>
        {
            if (size != (uint)sizeof(T)) return false;  // another layout on this build: leave the component alone
            change(ref *(T*)data);
            return true;
        });
    }

    /// <summary>Sets one field of a component by its path in the game's own type (<c>"dataStorage[0]"</c>,
    /// <c>"scale"</c>), checked against the game's reflected layout like <see cref="ReflectedValue"/>'s setter, and
    /// written through the game's write tracking (see <see cref="Write{T}"/>). Needs kiln.types. The component must be
    /// listed under <c>"writes"</c> in the mod's kiln.mod.json by hand: the build cannot see a name in a string.
    /// Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none, the component type or the field is unknown,
    /// the field is not a scalar, or the value does not fit it.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.tracked_writes</c> is off, or kiln.types is not running.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(TrackedWritesCapability)]
    public static Task<WorldWriteResult> WriteField(EntityId entity, string component, string path, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        var t = WorldWrites.Use("World.WriteField", entity);
        if (!GameTypes.Available) throw new KilnUnavailableException("kiln.types", "World.WriteField reads the field's place from the game's type table");
        var type = GameTypes.Find(component);
        if (type is not { Kind: GameTypeKind.Struct }) throw new ArgumentException($"unknown component type '{component}'", nameof(component));
        var (field, offset) = ReflectedValue.ResolvePath(type, path);
        if (!GameValueCodec.IsScalar(field.Kind)) throw new ArgumentException($"{path} is a {field.Kind}; set its fields one by one", nameof(path));
        Span<byte> bytes = stackalloc byte[8];
        GameValueCodec.Write(bytes, field.Kind, field.Size, value, field.EnumValues, path);
        return WorldWrites.Submit(t, entity, type.Name, offset, bytes[..field.Size], whole: false, change: null);
    }

    /// <summary>Stores <paramref name="value"/> at <paramref name="offset"/> of a component through the game's write
    /// tracking (see <see cref="Write{T}"/>): the tracked form of a store by offset, for a field no generated type or
    /// reflected path names. The layout is the caller's to know and differs between game builds. The component must be
    /// listed under <c>"writes"</c> in the mod's kiln.mod.json by hand. Since 0.2.0-dev.27.</summary>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none, <paramref name="component"/> is empty, or
    /// <paramref name="offset"/> is negative.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.tracked_writes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(TrackedWritesCapability)]
    public static Task<WorldWriteResult> WriteAt<T>(EntityId entity, string component, int offset, T value) where T : unmanaged
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        if (offset < 0) throw new ArgumentException("an offset is not negative", nameof(offset));
        var t = WorldWrites.Use("World.WriteAt", entity);
        return WorldWrites.Submit(t, entity, component, offset, new ReadOnlySpan<byte>(&value, sizeof(T)), whole: false, change: null);
    }

    /// <summary>What kiln.world knows about writing component <paramref name="component"/> from the calling mod: whether
    /// it is listed, which of the game's programs write it too, whether the save tracks it. Any thread.</summary>
    /// <exception cref="ArgumentException"><paramref name="component"/> is null or empty.</exception>
    [ServerOnly]
    [AnyThread]
    public static WorldWriteInfo WriteInfo(string component)
    {
        ArgumentException.ThrowIfNullOrEmpty(component);
        return WorldWrites.Info(component, ModContext.CurrentMod?.Info.Id);
    }

    /// <summary><see cref="WriteInfo(string)"/> for a generated component type.</summary>
    [ServerOnly]
    [AnyThread]
    public static WorldWriteInfo WriteInfo<T>() where T : unmanaged, IGameComponent<T> => WriteInfo(T.Name);
}

/// <summary>The managed side of kiln.world's write queue: requests, their answers on the tick thread, the event.</summary>
internal static unsafe class WorldWrites
{
    internal delegate bool Change(byte* data, uint size);

    private sealed class Pending
    {
        public required TaskCompletionSource<WorldWriteResult> Source;
        public required ModContext? Mod;
        public required string Component;
        public Change? Change;
        public Exception? ChangeError;
        public bool ChangeSize;  // the change saw another size than its type's
        public ulong Ticket;
        public EntityId Entity;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<long, Pending> PendingById = new();
    private static readonly Dictionary<ModContext, HashSet<long>> ByMod = new();
    private static long _nextCookie;

    /// <summary>Kiln.Game's first-use check of a generated type against the running game (EcsLayoutCheck.cs): null when
    /// the type can be used, else the difference. Set by Kiln.Game's module initializer, which runs before any member of
    /// a generated type does.</summary>
    internal static Func<Type, string, string?>? LayoutWhyNot;

    private static WorldWritesTable* _table;
    private static bool _looked;

    /// <summary>Forgets a remembered miss, so the next use looks for the table again (ServiceCaps.WhenFound: the refresh
    /// found it after all).</summary>
    internal static void LookAgain()
    {
        if (_table == null) _looked = false;
    }

    /// <summary>The v3 table, or null when kiln.world is missing or older.</summary>
    internal static WorldWritesTable* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _table = Services.Get<WorldWritesTable>(WorldNative.Name, 1);
            _looked = true;
            return _table;
        }
    }

    /// <summary>Tests and the test kit: forget the table (a new backend may publish another).</summary>
    internal static void Reset()
    {
        _table = null;
        _looked = false;
        lock (Gate)
        {
            PendingById.Clear();
            ByMod.Clear();
        }
    }

    /// <summary>The checks every write starts with: the argument, the thread, the capability.</summary>
    internal static WorldWritesTable* Use(string api, EntityId entity)
    {
        if (entity.IsNone) throw new ArgumentException("EntityId.None names no entity", nameof(entity));
        Services.RequireTickThread(api);
        var t = T;
        if (t == null || !WorldCapabilities.Is(World.TrackedWritesCapability))
            throw new KilnUnavailableException(World.TrackedWritesCapability, WorldCapabilities.Why(World.TrackedWritesCapability));
        return t;
    }

    /// <summary>A result for a generated type whose layout is not the running build's; null when it can be written.</summary>
    internal static WorldWriteResult? LayoutProblem(Type type, string name, string qualifiedName, int declaredSize, int size, EntityId entity)
    {
        string? why = declaredSize != size ? $"{type.FullName} is {size} bytes but says its game type has {declaredSize}" : LayoutWhyNot?.Invoke(type, qualifiedName);
        if (why is null) return null;
        return new WorldWriteResult
        {
            Status = WorldWriteStatus.LayoutMismatch, Entity = entity, Component = name,
            Reason = $"{type.FullName} does not have this game build's layout of {name}: {why}",
        };
    }

    internal static Task<WorldWriteResult> Submit(WorldWritesTable* t, EntityId entity, string component, int offset,
        ReadOnlySpan<byte> data, bool whole, Change? change)
    {
        var mod = ModContext.CurrentMod;
        var cookie = Interlocked.Increment(ref _nextCookie);
        var pending = new Pending
        {
            Source = new TaskCompletionSource<WorldWriteResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            Mod = mod, Component = component, Change = change, Entity = entity,
        };
        lock (Gate)
        {
            PendingById[cookie] = pending;
            if (mod is not null)
            {
                if (!ByMod.TryGetValue(mod, out var set))
                {
                    ByMod[mod] = set = [];
                    mod.Retiring += () => Retire(mod);
                }
                set.Add(cookie);
            }
        }
        var id = Encoding.UTF8.GetBytes(mod?.Info.Id ?? "");
        ulong ticket = 0;
        int rc;
        fixed (byte* bytes = data, modId = id)
        {
            WriteRequestNative request;
            request.Size = (uint)sizeof(WriteRequestNative);
            // Verify: kiln.world holds the answer one step only for a type the game's programs write too.
            request.Flags = (whole && change is null ? WorldWritesAbi.Whole : 0) | WorldWritesAbi.Verify;
            request.Entity = entity.Value;
            request.Component = World.ComponentHash(component);
            request.Offset = (uint)offset;
            request.Length = (uint)data.Length;
            request.Data = change is null ? bytes : null;
            request.Change = change is null ? null : &OnChange;
            request.Callback = &OnResult;
            request.User = (void*)cookie;
            request.ModId = new NativeStr { Ptr = modId, Len = (nuint)id.Length };
            rc = t->WriteSubmit(Services.CallerOwner, &request, &ticket);
        }
        if (rc != WorldNative.Ok)
        {
            Forget(cookie);
            if (rc == WorldNative.Unavailable)
                throw new KilnUnavailableException(World.TrackedWritesCapability, WorldCapabilities.Why(World.TrackedWritesCapability));
            if (rc == WorldNative.WrongThread)
                throw new InvalidOperationException("a tracked write is asked for on the tick thread between two steps, not inside a world program's update");
            if (rc == WorldNative.BufferTooSmall)
                return Task.FromResult(new WorldWriteResult
                {
                    Status = WorldWriteStatus.Dropped, Entity = entity, Component = component,
                    Reason = "4096 tracked writes are waiting already: the write program is not keeping up, or no server world runs",
                });
            throw new ArgumentException(data.Length > WorldWritesAbi.MaxBytes
                ? $"a tracked write takes up to {WorldWritesAbi.MaxBytes} bytes; {component} would be {data.Length} (write a field with World.WriteField or World.WriteAt)"
                : $"kiln.world refused the write of {component}: {WorldNative.Describe(rc)}");
        }
        pending.Ticket = ticket;
        return pending.Source.Task;
    }

    internal static WorldWriteInfo Info(string component, string? modId)
    {
        var t = T;
        if (t == null)
            return new WorldWriteInfo(component, WorldWriteState.Off, WorldCapabilities.Why(World.TrackedWritesCapability), 0, [], 0, null, null, false, 0);
        var id = Encoding.UTF8.GetBytes(modId ?? "");
        WriteInfoNative n;
        n.Size = (uint)sizeof(WriteInfoNative);
        int rc;
        fixed (byte* p = id) rc = t->WriteInfo(new NativeStr { Ptr = p, Len = (nuint)id.Length }, World.ComponentHash(component), &n);
        if (rc != WorldNative.Ok)
            return new WorldWriteInfo(component, WorldWriteState.Off, $"kiln.world did not answer ({WorldNative.Describe(rc)})", 0, [], 0, null, null, false, 0);
        return FromNative(component, modId, in n);
    }

    internal static WorldWriteInfo FromNative(string component, string? modId, in WriteInfoNative n)
    {
        var state = n.State <= (uint)WorldWriteState.ListRefused ? (WorldWriteState)n.State : WorldWriteState.Off;
        if ((n.Flags & WorldWritesAbi.NotReady) != 0)
            return new WorldWriteInfo(component, state, RefusalText(state, component, modId), 0, [], 0, null, null, false, 0) { FactsKnown = false };
        string names;
        fixed (byte* p = n.WriterNames) names = Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(p));
        bool known = (n.Flags & WorldWritesAbi.BitsKnown) != 0;
        return new WorldWriteInfo(component, state, RefusalText(state, component, modId), (int)n.Writers,
            names.Length == 0 ? [] : names.Split(", "), (int)n.InitWriters,
            known ? (n.Flags & WorldWritesAbi.Saved) != 0 : null, known ? (n.Flags & WorldWritesAbi.Replicated) != 0 : null,
            (n.Flags & WorldWritesAbi.Attribute) != 0, (int)n.ComponentSize);
    }

    /// <summary>Why a component cannot be written, as a sentence that says what to do (the native service's own words
    /// for the same states: write_queue.h write_refusal).</summary>
    internal static string? RefusalText(WorldWriteState state, string component, string? modId) => state switch
    {
        WorldWriteState.Ready => null,
        WorldWriteState.Pending => "no server world runs yet: the write program has not been registered",
        WorldWriteState.NotDeclared =>
            $"no loaded mod lists {component} under \"writes\" in its kiln.mod.json: add \"writes\": [\"{component}\"] (Kiln.Sdk does that " +
            $"for a mod whose code calls World.Write<{component}> or World.Update<{component}>) and restart the server; the game fixes a " +
            "program's access when the world starts",
        WorldWriteState.NotListed =>
            $"mod '{modId}' does not list {component} under \"writes\" in its kiln.mod.json (another mod does): add \"writes\": " +
            $"[\"{component}\"] and restart the server",
        WorldWriteState.UnknownComponent => $"this game build has no component type '{component}' (listed under \"writes\"; check the name)",
        WorldWriteState.OverLimit =>
            $"{component} was listed after the write program's {WorldWritesAbi.MaxTypes} component types were taken (the game's limit " +
            "for one program): fewer mods or fewer \"writes\" entries are needed",
        WorldWriteState.ListRefused =>
            $"the \"writes\" of mod '{modId}' in its kiln.mod.json was not read when the server started (kiln.log says what is wrong " +
            "with it), so none of its tracked writes is taken (the mod itself loaded): fix the list and restart the server",
        _ => "tracked writes are off: " + WorldCapabilities.Why(World.TrackedWritesCapability),
    };

    internal static WorldWriteResult FromNative(string component, in WriteResultNative r, Exception? changeError, bool changeSize)
    {
        var status = r.Status <= (uint)WorldWriteStatus.Dropped ? (WorldWriteStatus)r.Status : WorldWriteStatus.Faulted;
        var reason = r.Reason.ToString();
        if (status == WorldWriteStatus.Refused && changeError is not null)
        {
            status = WorldWriteStatus.Faulted;
            reason = $"the change threw {changeError.GetType().Name}: {changeError.Message}; the component is as it was";
        }
        else if (status == WorldWriteStatus.Refused && changeSize)
        {
            status = WorldWriteStatus.LayoutMismatch;
            reason = $"the entity's {component} is {r.ComponentSize} bytes on this game build, not the type's size; nothing was written";
        }
        var ok = status is WorldWriteStatus.Written or WorldWriteStatus.Unchanged;
        bool known = (r.Flags & WorldWritesAbi.BitsKnown) != 0, read = r.Before != null && r.After != null && r.Length <= 1 << 20;
        return new WorldWriteResult
        {
            Status = status,
            Reason = ok ? null : reason.Length > 0 ? reason : $"the write of {component} did not happen ({status})",
            Entity = new EntityId(r.Entity),
            Component = component,
            GameWriterCount = (int)r.Writers,
            Kept = (r.Flags & WorldWritesAbi.Checked) != 0 ? (r.Flags & WorldWritesAbi.Kept) != 0 : null,
            SaveTracked = known ? (r.Flags & WorldWritesAbi.Saved) != 0 : null,
            ReplicationTracked = known ? (r.Flags & WorldWritesAbi.Replicated) != 0 : null,
            Attribute = (r.Flags & WorldWritesAbi.Attribute) != 0,
            Offset = (int)r.Offset,
            ComponentSize = (int)r.ComponentSize,
            Steps = (int)r.Steps,
            Before = read ? new ReadOnlySpan<byte>(r.Before, (int)r.Length).ToArray() : default,
            After = read ? new ReadOnlySpan<byte>(r.After, (int)r.Length).ToArray() : default,
        };
    }

    /// <summary>A write that was not kept, with the policy of Kiln's that wrote over it when its service can show that it
    /// did (kiln.items' water refills: ComponentKeepers). Tick thread, in the Pre the answer arrives in.</summary>
    internal static WorldWriteResult WrittenOver(WorldWriteResult result) =>
        result.Kept == false && ComponentKeepers.Who(result.Entity, result.Component, result.Offset, result.After) is { } by
            ? result with { WrittenOverBy = by }
            : result;

    private static Pending? Forget(long cookie)
    {
        lock (Gate)
        {
            if (!PendingById.Remove(cookie, out var p)) return null;
            if (p.Mod is not null && ByMod.TryGetValue(p.Mod, out var set)) set.Remove(cookie);
            return p;
        }
    }

    private static void Retire(ModContext mod)
    {
        List<Pending> gone = [];
        lock (Gate)
        {
            if (!ByMod.Remove(mod, out var set)) return;
            foreach (var cookie in set)
                if (PendingById.Remove(cookie, out var p)) gone.Add(p);
        }
        var t = T;
        foreach (var p in gone)
        {
            // After write_cancel returns, the game calls neither the change nor the result of this request any more.
            if (t != null && p.Ticket != 0) t->WriteCancel(p.Ticket);
            p.Source.TrySetCanceled();
        }
    }

    // Inside the write program, in the game's step (maybe a worker thread): run the mod's change on the copy.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnChange(uint entity, void* data, uint size, void* user)
    {
        try
        {
            Pending? p;
            lock (Gate) PendingById.TryGetValue((long)user, out p);
            if (p?.Change is not { } change || p.Mod is { Alive: false }) return 0;
            var previous = ModContext.CurrentMod;
            try
            {
                ModContext.CurrentMod = p.Mod;
                if (change((byte*)data, size)) return 1;
                p.ChangeSize = true;
                return 0;
            }
            catch (Exception e)
            {
                p.ChangeError = e;
                return 0;
            }
            finally { ModContext.CurrentMod = previous; }
        }
        catch { return 0; /* no managed exception may cross the game's frame */ }
    }

    // Tick thread, Pre: the answer.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResult(WriteResultNative* r, void* user)
    {
        try
        {
            if (Forget((long)user) is not { } p) return;
            var result = FromNative(p.Component, in *r, p.ChangeError, p.ChangeSize);
            // The names of the game's writers, and for a type that is not this mod's to write the sentence with its name
            // in it (kiln.world knows a type nobody listed only by its hash).
            if (result.GameWriterCount > 0 || result.Status == WorldWriteStatus.NotDeclared)
            {
                var info = Info(p.Component, p.Mod?.Info.Id);
                result = result with
                {
                    GameWriters = info.GameWriters,
                    // kiln.world's own words for a refused list say what is wrong with it; the managed ones cannot
                    Reason = result.Status == WorldWriteStatus.NotDeclared && info.State != WorldWriteState.ListRefused ? info.Reason ?? result.Reason : result.Reason,
                };
            }
            result = WrittenOver(result);
            if (p.ChangeError is { } error) p.Mod?.Log.Error($"World.Update<{p.Component}>: the change threw, nothing was written: {error}");
            if (result.Changed && Events.HasSubscribers<ComponentWrittenEvent>())
                Events.Enqueue(new ComponentWrittenEvent(result, p.Mod?.Info.Id));
            p.Source.TrySetResult(result);
        }
        catch (Exception e)
        {
            Backend.Current.Log(0, LogLevel.Error, $"World: a tracked write's answer could not be delivered: {e}");
        }
    }
}

/// <summary>The numbers of kiln_world.h's tracked-write section.</summary>
internal static class WorldWritesAbi
{
    // KilnWorldWriteRequest.flags
    public const uint Whole = 0x01, Verify = 0x02;
    // KilnWorldWriteResult.flags / KilnWorldWriteInfo.flags
    public const uint GameWrites = 0x01, BitsKnown = 0x02, Saved = 0x04, Replicated = 0x08, Attribute = 0x10, Checked = 0x20, Kept = 0x40;
    public const uint NotReady = 0x80;  // KILN_WORLD_INFO_NOT_READY
    public const uint CapTrackedWrites = 1u << 3;
    public const int MaxBytes = 4096, MaxTypes = 91;
    // KILN_PROGRAM_* (KilnWorldWriteStatus.program)
    public const uint ProgramPending = 0, ProgramActive = 1, ProgramRefused = 2, ProgramFaulted = 3, ProgramSkipped = 4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WriteRequestNative
{
    public uint Size, Flags, Entity, Component, Offset, Length;
    public void* Data;
    public delegate* unmanaged[Cdecl]<uint, void*, uint, void*, byte> Change;
    public delegate* unmanaged[Cdecl]<WriteResultNative*, void*, void> Callback;
    public void* User;
    public NativeStr ModId;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WriteResultNative
{
    public uint Size, Status;
    public ulong Ticket;
    public uint Entity, Component, Offset, Length, ComponentSize, Flags, Steps, Writers;
    public void* Before, After;
    public NativeStr Reason;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WriteInfoNative
{
    public uint Size, State, Flags, Writers, InitWriters, ComponentSize;
    public fixed byte WriterNames[232];
}

[StructLayout(LayoutKind.Sequential)]
internal struct WriteStatusNative
{
    public uint Size, Program;
    public byte Available, Running, Attributes, Bits;
    public uint Declared, Refused, Pending, Reserved;
    public ulong Submitted, Written, Unchanged, Failed, Updates;
}

/// <summary>kiln.world's whole table as of v3 (kiln_world.h): the v1 part, the v2 program entries, the tracked writes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WorldWritesTable
{
    public WorldNative.Table V1;
    public nint WriteOther, Unregister, Bind;
    public delegate* unmanaged[Cdecl]<uint> Capabilities;
    public nint Events, Emit;
    public delegate* unmanaged[Cdecl]<ulong, WriteRequestNative*, ulong*, int> WriteSubmit;
    public delegate* unmanaged[Cdecl]<ulong, int> WriteCancel;
    public delegate* unmanaged[Cdecl]<NativeStr, uint, WriteInfoNative*, int> WriteInfo;
    public delegate* unmanaged[Cdecl]<WriteStatusNative*, int> WriteStatus;
    public delegate* unmanaged[Cdecl]<ulong, NativeStr, NativeStr*, uint, int> WriteDeclare;
}
