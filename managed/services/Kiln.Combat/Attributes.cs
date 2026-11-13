// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>The attribute pairs <see cref="Attributes"/> handles: a current value with a minimum and a maximum that the
/// game computes (level, gear, buffs).</summary>
public enum GameAttribute
{
    /// <summary>Health. Writes are <see cref="Combat.SetHealth"/>: silent, 0 kills on the next step, god mode keeps 1.</summary>
    Health = 1,
    /// <summary>Stamina (creatures and players).</summary>
    Stamina = 2,
    /// <summary>Mana (players, and the few creatures that carry it, such as Player_Vukah_Melee).</summary>
    Mana = 3,
    /// <summary>Body heat (players).</summary>
    BodyHeat = 4,
    /// <summary>Oxygen (players and most creatures).</summary>
    Oxygen = 5,
}

/// <summary>The game's attribute ids of one pair, read from its attribute resource when the first world starts.</summary>
/// <param name="Kind">The pair.</param>
/// <param name="CurrentId">The id of the current value (the one <see cref="Attributes.Set(EntityId, GameAttribute, int)"/> writes).</param>
/// <param name="MinId">The id of the minimum, 0 when the pair has none.</param>
/// <param name="MaxId">The id of the maximum, 0 when the pair has none.</param>
/// <param name="ComponentHash">FNV-1a of the component's type name (keen::ecs::Stamina is 0x92410664).</param>
/// <param name="GameClamp">The game's own formula clamps the current value to [min, max] after every change.</param>
public readonly record struct AttributeIds(GameAttribute Kind, uint CurrentId, uint MinId, uint MaxId, uint ComponentHash,
    bool GameClamp);

/// <summary>One attribute of an entity with its pair's current value and bounds.</summary>
/// <param name="Id">The attribute id asked for.</param>
/// <param name="Kind">The pair it belongs to.</param>
/// <param name="Value">The value of <paramref name="Id"/> (for a current value: the same as <paramref name="Current"/>).</param>
/// <param name="Current">The pair's current value.</param>
/// <param name="Min">The pair's minimum, null when it has none.</param>
/// <param name="Max">The pair's maximum, null when it has none.</param>
/// <param name="IsCurrent"><paramref name="Id"/> is the pair's current value, the one that can be set and added to.</param>
/// <param name="CurrentId">The pair's current-value id.</param>
/// <param name="MaxId">The pair's maximum id, 0 when it has none.</param>
public readonly record struct AttributeReading(uint Id, GameAttribute Kind, int Value, int Current, int? Min, int? Max,
    bool IsCurrent, uint CurrentId, uint MaxId)
{
    /// <summary>Current / maximum, 0 without a positive maximum.</summary>
    public double Fraction => Max is > 0 ? (double)Current / Max.Value : 0;
    public override string ToString() => $"{Current}/{(Max is { } m ? m.ToString() : "?")} ({Kind})";
}

/// <summary>
/// Health, stamina, mana, body heat and oxygen of any entity on the server world, by the game's own attribute ids
/// (<see cref="Ids"/>: resolved from the game's attribute resource, not built in). Reading works on creatures and
/// players alike; <see cref="Set(EntityId, GameAttribute, int)"/> and <see cref="Add(EntityId, GameAttribute, int)"/>
/// clamp to the pair's minimum and maximum and go through the game's attribute code (its formulas re-run, and its
/// own programs copy the values into what players are sent). A write is silent in game (no hit); on Kiln's bus it posts
/// <see cref="CombatChangedEvent"/> when the value changed.
/// Tick thread only (except <see cref="Ids"/>).
/// </summary>
/// <remarks>The ids are also accepted as numbers (the game's attribute hash), which is how EMF's attribute_get and
/// attribute_add name them; any id of the five components can be read, only a pair's current value can be written.
/// <para><c>Abilities.Attributes</c> (Kiln.Abilities) is the older, narrower API over the same game function: health,
/// mana, stamina and the special-ability charge by built-in ids, <c>Add</c> only, returning false on a missing attribute.
/// Both write through the game's attribute add, so a value written by one reads back the same in the other; this class
/// adds the run-time ids, an explicit clamp, body heat and oxygen, and the pair's bounds.</para>
/// Player persistence is unverified: a write is not marked written for saving (Kiln's only mark-written path, kiln.world's,
/// needs an ECS program's write record), the same as <see cref="Combat.SetHealth"/> (docs/re/combat.md "Attributes").</remarks>
[ServerOnly]
public static unsafe class Attributes
{
    /// <summary>True when the attribute functions work on this build and a world has started (capability
    /// <c>combat.attributes</c>).</summary>
    [AnyThread]
    public static bool Available => Ids(GameAttribute.Stamina) is not null;

    /// <summary>The game's ids of a pair, or null before the first server world runs or when this build has no
    /// attribute resource Kiln understands.</summary>
    [AnyThread]
    public static AttributeIds? Ids(GameAttribute attribute)
    {
        Combat.Used();
        var t = CombatNative.V2;
        if (t == null) return null;
        CombatNative.AttributeIds ids = default;
        ids.Size = (uint)sizeof(CombatNative.AttributeIds);
        if (t->AttributeIdsFn((uint)attribute, &ids) != CombatNative.Ok) return null;
        return new AttributeIds(attribute, ids.CurrentId, ids.MinId, ids.MaxId, ids.Component, ids.GameClamp != 0);
    }

    /// <summary>The current value of a pair with its bounds, or null when the entity has no such attribute (a creature
    /// without mana, a missing entity) or attributes are unavailable.</summary>
    [TickThreadOnly]
    public static AttributeReading? Get(EntityId entity, GameAttribute attribute)
    {
        var ids = Ids(attribute);
        return ids is null ? null : Get(entity, ids.Value.CurrentId);
    }

    /// <summary>Any attribute of the five components by its id (current, minimum, maximum, recharge...), with its pair's
    /// current value and bounds; null when the entity lacks it. An id Kiln does not know throws.</summary>
    [TickThreadOnly]
    [RequiresCapability(CombatCaps.Attributes)]
    public static AttributeReading? Get(EntityId entity, uint attributeId)
    {
        Combat.Used();
        var t = CombatNative.RequireV2("Attributes.Get");
        Services.RequireTickThread("Attributes.Get");
        CombatNative.AttributeValue v = default;
        v.Size = (uint)sizeof(CombatNative.AttributeValue);
        var rc = t->AttributeGet(entity.Value, attributeId, &v);
        if (rc is CombatNative.ErrNotFound or CombatNative.ErrUnavailable) return null;
        if (rc != CombatNative.Ok) throw new InvalidOperationException($"Attributes.Get({entity}, 0x{attributeId:X8}): {CombatNative.DescribeAttribute(rc)}");
        return Reading(v);
    }

    /// <summary>Sets the current value, clamped to the pair's [min, max]. Returns the new value. Throws when the entity
    /// has no such attribute (see <see cref="TrySet(EntityId, GameAttribute, int, out AttributeReading)"/>).</summary>
    [TickThreadOnly]
    public static int Set(EntityId entity, GameAttribute attribute, int value) => Write("Attributes.Set", entity, attribute, value, add: false).Current;

    /// <summary>Adds <paramref name="delta"/> (negative to spend) to the current value, clamped like Set. Returns the new
    /// value.</summary>
    [TickThreadOnly]
    public static int Add(EntityId entity, GameAttribute attribute, int delta) => Write("Attributes.Add", entity, attribute, delta, add: true).Current;

    /// <summary>Set by the game's attribute id of a pair's current value (see <see cref="Ids"/>).</summary>
    [TickThreadOnly]
    [RequiresCapability(CombatCaps.Attributes)]
    public static int Set(EntityId entity, uint attributeId, int value) => Write("Attributes.Set", entity, attributeId, value, add: false).Current;

    /// <summary>Add by the game's attribute id of a pair's current value.</summary>
    [TickThreadOnly]
    [RequiresCapability(CombatCaps.Attributes)]
    public static int Add(EntityId entity, uint attributeId, int delta) => Write("Attributes.Add", entity, attributeId, delta, add: true).Current;

    /// <summary>Like <see cref="Set(EntityId, GameAttribute, int)"/>, false instead of throwing when the entity lacks the
    /// attribute or attributes are unavailable.</summary>
    [TickThreadOnly]
    public static bool TrySet(EntityId entity, GameAttribute attribute, int value, out AttributeReading result) =>
        TryWrite(entity, attribute, value, add: false, out result);

    /// <summary>Like <see cref="Add(EntityId, GameAttribute, int)"/>, false instead of throwing when the entity lacks
    /// the attribute or attributes are unavailable.</summary>
    [TickThreadOnly]
    public static bool TryAdd(EntityId entity, GameAttribute attribute, int delta, out AttributeReading result) =>
        TryWrite(entity, attribute, delta, add: true, out result);

    private static bool TryWrite(EntityId entity, GameAttribute attribute, int value, bool add, out AttributeReading result)
    {
        Services.RequireTickThread(add ? "Attributes.TryAdd" : "Attributes.TrySet");
        result = default;
        var t = CombatNative.V2;  // an older runtime without the attribute functions: unavailable, so false (not a throw)
        var ids = t == null ? null : Ids(attribute);
        if (ids is null) return false;
        var before = Before(t, entity, ids.Value.CurrentId);
        CombatNative.AttributeValue v = default;
        v.Size = (uint)sizeof(CombatNative.AttributeValue);
        var rc = add ? t->AttributeAdd(entity.Value, ids.Value.CurrentId, value, &v) : t->AttributeSet(entity.Value, ids.Value.CurrentId, value, &v);
        if (rc != CombatNative.Ok) return false;
        result = Reading(v);
        Posted(entity, result, before);
        return true;
    }

    // The current value before a write, read only while someone listens for CombatChangedEvent.
    private static int? Before(CombatNative.TableV2* t, EntityId entity, uint attributeId)
    {
        if (!Events.HasSubscribers<CombatChangedEvent>()) return null;
        CombatNative.AttributeValue v = default;
        v.Size = (uint)sizeof(CombatNative.AttributeValue);
        return t->AttributeGet(entity.Value, attributeId, &v) == CombatNative.Ok ? v.Current : null;
    }

    private static void Posted(EntityId entity, in AttributeReading after, int? before)
    {
        if (before is not { } b || b == after.Current) return;
        var name = Enum.IsDefined(after.Kind) ? after.Kind.ToString() : $"0x{after.CurrentId:X8}";
        Combat.Changed(entity, "Attribute." + name, b, after.Current);
    }

    private static AttributeReading Write(string api, EntityId entity, GameAttribute attribute, int value, bool add)
    {
        var ids = Ids(attribute) ?? throw new InvalidOperationException($"{api}({entity}, {attribute}): {CombatNative.DescribeAttribute(CombatNative.ErrUnavailable)}");
        return Write(api, entity, ids.CurrentId, value, add);
    }

    private static AttributeReading Write(string api, EntityId entity, uint attributeId, int value, bool add)
    {
        Combat.Used();
        var t = CombatNative.RequireV2(api);
        Services.RequireTickThread(api);
        var before = Before(t, entity, attributeId);
        CombatNative.AttributeValue v = default;
        v.Size = (uint)sizeof(CombatNative.AttributeValue);
        var rc = add ? t->AttributeAdd(entity.Value, attributeId, value, &v) : t->AttributeSet(entity.Value, attributeId, value, &v);
        if (rc != CombatNative.Ok) throw new InvalidOperationException($"{api}({entity}, 0x{attributeId:X8}): {CombatNative.DescribeAttribute(rc)}");
        var reading = Reading(v);
        Posted(entity, reading, before);
        return reading;
    }

    private static AttributeReading Reading(in CombatNative.AttributeValue v) =>
        new(v.Id, (GameAttribute)v.Kind, v.Value, v.Current, v.HasMin != 0 ? v.Min : null, v.HasMax != 0 ? v.Max : null,
            v.IsCurrent != 0, v.CurrentId, v.MaxId);
}
