// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>
/// A mod changed an entity through Kiln.Combat without a hit or a heal: its health set (<see cref="Combat.SetHealth"/>),
/// its god mode (<see cref="Combat.SetInvulnerable"/>) or an attribute's current value (<see cref="Attributes"/>:
/// <c>Set</c>, <c>Add</c>, <c>TrySet</c>, <c>TryAdd</c>). Posted on the tick thread right after the change took, and only
/// when the value changed; a change made while this event is being dispatched (a handler that writes again) is posted
/// at the start of the next step instead, once per entity and value (the newest wins), so such a handler cannot loop.
/// Observe only. Built only while someone subscribes. Hits and heals keep their own events
/// (<see cref="EntityHurtEvent"/>, <see cref="EntityHealEvent"/>). Since 0.2.0-dev.27.
/// </summary>
public sealed class CombatChangedEvent : KilnEvent, IEntityEvent
{
    internal CombatChangedEvent(EntityId entity, string what, int before, int after, string origin)
    {
        Entity = entity;
        What = what;
        Before = before;
        After = after;
        Origin = origin;
    }

    /// <summary>The entity.</summary>
    public EntityId Entity { get; }

    /// <summary>What changed: <c>Health</c>, <c>Invulnerable</c>, or <c>Attribute.&lt;name&gt;</c>
    /// (<c>Attribute.Stamina</c>; <c>Attribute.0x…</c> for an id outside the five pairs).</summary>
    public string What { get; }

    /// <summary>The value before (health points, the attribute's current value, 1/0 for god mode on/off).</summary>
    public int Before { get; }

    /// <summary>The value after.</summary>
    public int After { get; }

    /// <summary>The id of the mod that made the change ("" when Kiln made it for no mod).</summary>
    public string Origin { get; }

    /// <summary>Whether the event is about <paramref name="entity"/>.</summary>
    public bool Involves(EntityId entity) => !entity.IsNone && entity == Entity;

    /// <summary>The change and who made it, for a log line.</summary>
    public override string ToString() => $"entity {Entity}: {What} {Before} -> {After} by {(Origin.Length > 0 ? Origin : "kiln")}";
}
