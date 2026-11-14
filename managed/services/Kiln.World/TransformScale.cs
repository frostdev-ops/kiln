// SPDX-License-Identifier: MIT
// World.SetTransformScale (0.2.0-dev.28, chapter 13 §13b.4-§13b.5; capability world.transform_scale): the three scale
// floats of an entity's CurrentTransform, written as one tracked write (World.WriteAt<Scale3>), the C# equivalent of
// ShroudForge's guarded transform-scale setter. The place of the scale is found in the game's own type table when the
// row is reported (CurrentTransform -> transform -> scale), never assumed: a build whose layout is not one of the two
// accepted shapes turns the row off and nothing is written. Not a physics resize: colliders and ScaleComponent are not
// touched (CreatureRef.Scale is the uniform creature scale). Guide: docs/kiln/services/world.md "Transform scale".
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Kiln;

/// <summary>The three scale factors of a transform (<c>keen::float3</c>): X, Y, Z, 12 bytes, sequential. JSON:
/// <c>schema/scale3.schema.json</c> (<c>{"x":1.25,"y":0.75,"z":1.5}</c>).</summary>
/// <param name="X">The factor along the entity's X axis.</param>
/// <param name="Y">Along Y (up).</param>
/// <param name="Z">Along Z.</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct Scale3(
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("z")] float Z)
{
    /// <summary>1, 1, 1: the size the game gave.</summary>
    public static Scale3 One => new(1f, 1f, 1f);

    /// <summary>The same factor on every axis.</summary>
    public static Scale3 Uniform(float factor) => new(factor, factor, factor);

    /// <summary>Every factor is finite and above zero (what <see cref="World.SetTransformScale"/> takes).</summary>
    [JsonIgnore]
    public bool IsValid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && X > 0f && Y > 0f && Z > 0f;

    /// <summary><c>(1.25, 0.75, 1.5)</c>, invariant culture.</summary>
    public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"({X}, {Y}, {Z})");
}

/// <summary>The answer to <see cref="World.SetTransformScale"/>: the tracked write's, with the scale before and after
/// decoded. <see cref="Ok"/> false with a <see cref="Reason"/> when the game did not take it. Since 0.2.0-dev.28.</summary>
/// <param name="Entity">The entity.</param>
/// <param name="Before">The scale the entity had right before the write, in the step the write ran; null when nothing
/// was read (refused before it ran).</param>
/// <param name="Requested">The scale asked for.</param>
/// <param name="Now">The scale right after the write; null when nothing was read.</param>
/// <param name="Kept">For a component the game writes too: whether the scale was still there one step later (as
/// <see cref="WorldWriteResult.Kept"/>); null when not checked. It is the tracked readback, not a promise that the save
/// or a player's game keeps it.</param>
/// <param name="WorldWriteStatus">What became of the tracked write.</param>
/// <param name="Reason">Why it did not happen; null when <see cref="Ok"/>.</param>
/// <param name="SaveTracked">Whether the game's write tracking records CurrentTransform for the save (the write's own
/// answer, <see cref="WorldWriteResult.SaveTracked"/>); null when this build does not tell.</param>
/// <param name="ReplicationTracked">Whether it records it for replication (<see cref="WorldWriteResult.ReplicationTracked"/>);
/// null when this build does not tell. What a player sees is proven only in a real game.</param>
[ServerOnly]
public sealed record EntityScaleResult(EntityId Entity, Scale3? Before, Scale3 Requested, Scale3? Now, bool? Kept,
    WorldWriteStatus WorldWriteStatus, string? Reason, bool? SaveTracked, bool? ReplicationTracked) : IKilnResult
{
    /// <summary>True when the entity's transform holds the scale now: written, or it held it already.</summary>
    public bool Ok => WorldWriteStatus is WorldWriteStatus.Written or WorldWriteStatus.Unchanged;

    /// <summary>True when the scale changed (<see cref="WorldWriteStatus.Written"/> and another value than before).</summary>
    public bool Changed => WorldWriteStatus == WorldWriteStatus.Written && Before is { } b && Now is { } n && b != n;
}

/// <summary>An entity's transform scale changed through <see cref="World.SetTransformScale"/>: posted for a write that
/// was committed and changed the vector (none for <see cref="WorldWriteStatus.Unchanged"/> or a refusal), at the start of
/// the step after its answer, after that write's <see cref="ComponentWrittenEvent"/>. Only built while the event has
/// subscribers. Not cancelable. Since 0.2.0-dev.28.</summary>
[ServerOnly]
public sealed class EntityScaleChangedEvent : KilnEvent, IEntityEvent
{
    internal EntityScaleChangedEvent(EntityScaleResult result, string? modId)
    {
        Result = result;
        ModId = modId;
    }

    /// <summary>The entity.</summary>
    public EntityId Entity => Result.Entity;

    /// <summary>The scale before.</summary>
    public Scale3 Before => Result.Before ?? Scale3.One;

    /// <summary>The scale now.</summary>
    public Scale3 Now => Result.Now ?? Result.Requested;

    /// <summary>The id of the mod that set it; null for Kiln's own tools.</summary>
    public string? ModId { get; }

    /// <summary>The whole answer.</summary>
    public EntityScaleResult Result { get; }

    /// <inheritdoc/>
    public bool Involves(EntityId entity) => entity == Entity;

    /// <summary>The change in one line, for a log.</summary>
    public override string ToString() => $"scale of {Entity}: {Before} -> {Now}{(ModId is null ? "" : " by " + ModId)}";
}

public static unsafe partial class World
{
    /// <summary>The capability of <see cref="SetTransformScale"/>: <c>world.transform_scale</c>.</summary>
    public const string TransformScaleCapability = TransformScale.Capability;

    // reported at Kiln.World's first use, beside its other rows (World's static constructor runs this initializer first)
    private static readonly bool TransformScaleRowsStarted = TransformScale.Start();

    /// <summary>
    /// Sets the three scale factors of <paramref name="entity"/>'s <c>CurrentTransform</c> through the game's write
    /// tracking: one tracked write of 12 bytes (<see cref="WriteAt{T}"/>) at the place the game's type table gives, so
    /// the position and orientation stay as they are and the save and replication learn of it as of the game's own
    /// writes (where the game tracks the type: see the result). The same thing ShroudForge's scale setter does; not a
    /// physics resize: colliders, attachments and <c>ScaleComponent</c> are not changed (a creature's uniform scale:
    /// <c>CreatureRef.Scale</c>). The mod must list <c>CurrentTransform</c> under <c>"writes"</c> in its kiln.mod.json
    /// when the server starts: Kiln.Sdk adds it from this call at build time. Since 0.2.0-dev.28.
    /// </summary>
    /// <returns>The answer, one step after the call (two when the game writes the type too); <see cref="EntityScaleResult.Ok"/>
    /// false with the reason when the game did not take it (no such entity, not listed, the entity went).</returns>
    /// <exception cref="ArgumentException"><paramref name="entity"/> is none, or a factor is not finite or not above 0.</exception>
    /// <exception cref="KilnUnavailableException"><c>world.transform_scale</c> is off (this build's transform has no scale of
    /// an accepted shape, kiln.types is not running, or this is a game client), or <c>world.tracked_writes</c> is off.</exception>
    /// <exception cref="InvalidOperationException">Not on the tick thread, or inside a world program's update.</exception>
    [ServerOnly]
    [TickThreadOnly]
    [RequiresCapability(TransformScaleCapability)]
    [RequiresCapability(TrackedWritesCapability)]
    public static Task<EntityScaleResult> SetTransformScale(EntityId entity, Scale3 scale)
    {
        if (entity.IsNone) throw new ArgumentException("EntityId.None names no entity", nameof(entity));
        if (!scale.IsValid) throw new ArgumentOutOfRangeException(nameof(scale), scale, "every factor finite and above 0");
        _ = TransformScaleRowsStarted;
        var layout = TransformScale.Require();
        var modId = ModContext.CurrentMod?.Info.Id;
        var write = WriteAt(entity, TransformScale.Component, layout.Offset, scale);
        return TransformScale.Finish(entity, scale, write, modId);
    }
}

/// <summary>Where CurrentTransform keeps its scale on this build, the row, and the answer.</summary>
internal static class TransformScale
{
    public const string Capability = "world.transform_scale";
    public const string Component = "CurrentTransform";
    public const string QualifiedComponent = "keen::ecs::CurrentTransform";

    /// <summary>The scale's place: its offset in CurrentTransform and which accepted shape it has.</summary>
    internal sealed record Layout(int Offset, string Shape);

    /// <summary>A reflected type as the resolver sees it (a pure description, so every branch is tested without a game).</summary>
    internal sealed record Node(string Name, GameTypeKind Kind, int Size, IReadOnlyList<(string Name, int Offset, Node Type)> Fields,
        Node? Inner = null, int ArrayLength = 0);

    internal static readonly ServiceCapRows Rows = new("kiln.world transform scale", Report);
    private static Layout? _layout;

    internal static bool Start()
    {
        Rows.Ensure();
        return true;
    }

    // The row: decided once kiln.types has the type table (static for the process: a build's layout does not change).
    private static void Report()
    {
        // no side check: the write runs where the tracked writes run (a server world: a dedicated server or a hosting game;
        // world.tracked_writes says when there is none)
        _layout = null;
        if (!GameTypes.Available)
        {
            ServiceCaps.Report(Capability, CapabilityState.Unavailable, CapabilityReason.ServiceMissing,
                "needs kiln.types: the scale's place is read from the game's type table");
            return;
        }
        var (layout, why) = Resolve(GameTypes.Find(QualifiedComponent) is { } t ? NodeOf(t) : null);
        _layout = layout;
        if (layout is null) ServiceCaps.Report(Capability, CapabilityState.Unavailable, CapabilityReason.SymbolMissing, why);
        else ServiceCaps.Report(Capability, CapabilityState.Available, CapabilityReason.None, why);
    }

    /// <summary>The layout, or the exception the call throws: the row is off.</summary>
    internal static Layout Require()
    {
        Rows.Ensure();
        if (_layout is { } l && Capabilities.Is(Capability)) return l;
        throw ServiceCaps.Unavailable(Capability);
    }

    /// <summary>Where CurrentTransform keeps a scale of an accepted shape: <c>transform</c> (a struct), then <c>scale</c>:
    /// three contiguous float32 (a struct of three float32 fields at 0, 4, 8, or a static array of three float32), 12
    /// bytes, typedefs followed, 4-aligned, inside the component. Anything else: null and the shape that was found.</summary>
    internal static (Layout? Layout, string Why) Resolve(Node? component)
    {
        if (component is null) return (null, $"this build has no {QualifiedComponent} in its type table");
        if (component.Kind != GameTypeKind.Struct) return (null, $"{QualifiedComponent} is a {component.Kind}, not a struct");
        var transform = Field(component, "transform");
        if (transform is not { } tf) return (null, $"{QualifiedComponent} has no field 'transform' (fields: {Names(component)})");
        var tt = Resolved(tf.Type);
        if (tt.Kind != GameTypeKind.Struct) return (null, $"{QualifiedComponent}.transform is a {tt.Kind} of {tt.Size} bytes, not a struct with a scale");
        var scale = Field(tt, "scale");
        if (scale is not { } sf) return (null, $"{QualifiedComponent}.transform ({tt.Name}) has no field 'scale' (fields: {Names(tt)})");
        var st = Resolved(sf.Type);
        long offset = (long)tf.Offset + sf.Offset;
        string? shape = null;
        if (st.Size == 12 && st.Kind == GameTypeKind.Struct && st.Fields.Count == 3 &&
            st.Fields.Select((f, i) => (f, i)).All(p => p.f.Offset == p.i * 4 && Resolved(p.f.Type) is { Kind: GameTypeKind.F32, Size: 4 }))
            shape = $"{st.Name}: a struct of three float32";
        else if (st.Size == 12 && st.Kind == GameTypeKind.StaticArray && st.ArrayLength == 3 && st.Inner is { } e && Resolved(e) is { Kind: GameTypeKind.F32, Size: 4 })
            shape = $"{st.Name}: an array of three float32";
        if (shape is null)
            return (null, $"{QualifiedComponent}.transform.scale is {st.Name} ({st.Kind}, {st.Size} bytes{Describe(st)}): not three contiguous float32");
        if (tf.Offset < 0 || sf.Offset < 0 || tf.Offset + tt.Size > component.Size || sf.Offset + 12 > tt.Size || offset + 12 > component.Size)
            return (null, $"{QualifiedComponent}.transform.scale at +{offset} does not lie inside the component ({component.Size} bytes)");
        if (offset % 4 != 0) return (null, $"{QualifiedComponent}.transform.scale at +{offset} is not 4-aligned");
        return (new Layout((int)offset, shape), $"{QualifiedComponent}.transform.scale at +{offset} ({shape})");
    }

    private static (string Name, int Offset, Node Type)? Field(Node n, string name)
    {
        foreach (var f in n.Fields)
            if (f.Name == name) return f;
        return null;
    }

    private static Node Resolved(Node n)
    {
        for (var i = 0; i < 16 && n.Kind == GameTypeKind.Typedef && n.Inner is { } inner && inner.Size == n.Size; i++) n = inner;
        return n;
    }

    private static string Names(Node n) => n.Fields.Count == 0 ? "none" : string.Join(", ", n.Fields.Select(f => f.Name));

    private static string Describe(Node n) => n.Kind switch
    {
        GameTypeKind.Struct => $": {string.Join(", ", n.Fields.Select(f => $"{f.Name} {Resolved(f.Type).Kind}@{f.Offset}"))}",
        GameTypeKind.StaticArray => $" of {n.ArrayLength} {n.Inner?.Kind}",
        _ => "",
    };

    /// <summary>A reflected type as a <see cref="Node"/> (every field, base classes first), bounded.</summary>
    internal static Node NodeOf(GameType t, int depth = 0)
    {
        if (depth > 8) return new Node(t.QualifiedName, t.Kind, t.Size, []);
        var fields = t.Kind == GameTypeKind.Struct
            ? t.AllFields.Select(f => (f.Name, f.Offset, NodeOf(f.Type, depth + 1))).ToList()
            : [];
        var inner = t.Kind is GameTypeKind.Typedef or GameTypeKind.StaticArray && t.Inner is { } i ? NodeOf(i, depth + 1) : null;
        return new Node(t.QualifiedName, t.Kind, t.Size, fields, inner, t.ArrayLength);
    }

    /// <summary>The tracked write's answer as the scale's: the 12 bytes before and after decoded, the event posted for a
    /// committed change.</summary>
    internal static async Task<EntityScaleResult> Finish(EntityId entity, Scale3 requested, Task<WorldWriteResult> write, string? modId)
    {
        // back on the caller's context (a mod's code: the tick thread, in its mod context), so the answer arrives as a
        // tracked write's does and the event is queued from there
        var r = await write;
        var result = Of(entity, requested, r);
        if (result.Changed && Events.HasSubscribers<EntityScaleChangedEvent>()) Events.Enqueue(new EntityScaleChangedEvent(result, modId));
        return result;
    }

    internal static EntityScaleResult Of(EntityId entity, Scale3 requested, WorldWriteResult r) => new(
        entity, Decode(r.Before), requested, Decode(r.After), r.Kept, r.Status, r.Reason, r.SaveTracked, r.ReplicationTracked);

    private static Scale3? Decode(ReadOnlyMemory<byte> bytes) =>
        bytes.Length == 12 ? MemoryMarshal.Read<Scale3>(bytes.Span) : null;
}
