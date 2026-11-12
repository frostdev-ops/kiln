// SPDX-License-Identifier: MIT
namespace Kiln;

/// <summary>An entity as it was when it was captured: its id and the spawn serial it had then. Entity ids are reused
/// after an entity is destroyed; the serial tells a later entity with the same id apart, so <see cref="IsAlive"/> is
/// false once the id belongs to another spawn. <c>default</c> is <see cref="None"/>. Converts to <see cref="EntityId"/>,
/// so every API that takes an id takes a ref. Capture one with <see cref="Of"/>.</summary>
/// <param name="Id">The entity id.</param>
/// <param name="Serial">An opaque spawn stamp from the entity source; 0 when the entity has none (then
/// <see cref="IsAlive"/> can only check that the id exists).</param>
public readonly record struct EntityRef(EntityId Id, ulong Serial)
{
    /// <summary>The capability that captures and checks entity serials.</summary>
    internal const string Capability = "identity.entities";

    /// <summary>No entity.</summary>
    public static readonly EntityRef None = default;

    /// <summary>True when this refers to no entity.</summary>
    public bool IsNone => Id.IsNone;

    /// <summary>True when the ref carries a spawn serial. False: <see cref="IsAlive"/> can only check that the id
    /// exists, not that it is still the same entity.</summary>
    public bool HasSerial => Serial != 0;

    /// <summary>The entity id, for every API that takes an <see cref="EntityId"/>.</summary>
    public static implicit operator EntityId(EntityRef r) => r.Id;

    /// <summary>Captures the entity's current serial. <see cref="None"/> for <see cref="EntityId.None"/> and for an id
    /// that does not exist.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">No entity source is registered (<c>identity.entities</c>).</exception>
    [TickThreadOnly]
    public static EntityRef Of(EntityId entity)
    {
        Game.AssertTickThread();
        if (entity.IsNone) return None;
        var source = IdentitySources.Entities
            ?? throw new KilnUnavailableException(Capability, "no service supplies entity serials in this process");
        return source.TryGetSerial(entity, out var serial) ? new EntityRef(entity, serial) : None;
    }

    /// <summary>True while the id still belongs to the spawn this ref captured. False for <see cref="None"/>, for an id
    /// that no longer exists, and when the id now carries another serial. Without a serial (<see cref="HasSerial"/>
    /// false, <see cref="Serial"/> 0) true means no more than "an entity with this id exists";
    /// <see cref="HasSerial"/> is the way to tell.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread (not thrown for <see cref="None"/>).</exception>
    /// <exception cref="KilnUnavailableException">No entity source is registered (<c>identity.entities</c>);
    /// not thrown when <see cref="IsNone"/> is true.</exception>
    [TickThreadOnly]
    public bool IsAlive
    {
        get
        {
            if (IsNone) return false;
            Game.AssertTickThread();
            var source = IdentitySources.Entities
                ?? throw new KilnUnavailableException(Capability, "no service supplies entity serials in this process");
            if (!source.TryGetSerial(Id, out var current)) return false;
            return Serial == 0 || current == Serial;
        }
    }

    /// <summary><c>"12#345"</c>, <c>"12"</c> without a serial, <c>"none"</c>.</summary>
    public override string ToString() => IsNone ? "none" : Serial == 0 ? Id.ToString() : $"{Id}#{Serial}";
}

/// <summary>One particular item in the world (not an item type, which is <see cref="ItemId"/>, and not
/// <c>Kiln.Blocks</c>' <c>ItemRef</c>, which names an item for a recipe): the item's entity and its type; its
/// <see cref="IsAlive"/> says nothing more than its entity's <see cref="EntityRef.IsAlive"/> does. <c>default</c> is
/// <see cref="None"/>.</summary>
/// <param name="Entity">The item's entity.</param>
/// <param name="Item">The item's type.</param>
public readonly record struct ItemInstanceRef(EntityRef Entity, ItemId Item)
{
    /// <summary>No item.</summary>
    public static readonly ItemInstanceRef None = default;

    /// <summary>True when this refers to no item entity.</summary>
    public bool IsNone => Entity.IsNone;

    /// <summary>Exactly the entity's <see cref="EntityRef.IsAlive"/>, nothing more.</summary>
    /// <exception cref="KilnUnavailableException">No entity source is registered (<c>identity.entities</c>);
    /// not thrown when <see cref="IsNone"/> is true.</exception>
    [TickThreadOnly]
    public bool IsAlive => Entity.IsAlive;

    /// <summary><c>"0x0000abcd@12#345"</c>, <c>"none"</c>.</summary>
    public override string ToString() => IsNone ? "none" : $"{Item}@{Entity}";
}

/// <summary>An item type: FNV-1a-32 of the item's 16 GUID bytes, as the game computes it (and the dashboard catalog
/// uses). 0 = none.</summary>
public readonly record struct ItemId(uint Value)
{
    public static readonly ItemId None = default;
    public bool IsNone => Value == 0;
    public override string ToString() => $"0x{Value:x8}";

    /// <summary>The id the game gives the item with these GUID bytes (as stored in the game's data).</summary>
    public static ItemId FromGuidBytes(ReadOnlySpan<byte> guid)
    {
        if (guid.Length != 16) throw new ArgumentException("a GUID has 16 bytes", nameof(guid));
        uint h = 0x811c9dc5;
        var zero = true;
        foreach (var b in guid)
        {
            zero &= b == 0;
            h = (h ^ b) * 0x01000193;
        }
        return new ItemId(zero ? 0 : h);
    }
}

/// <summary>A person on this server: the Steam id, the character they play (may be empty) and their session entity
/// (none while offline). <c>default</c> is <see cref="None"/>.</summary>
/// <param name="SteamId">SteamID64; 0 when unknown.</param>
/// <param name="CharacterId">The game's CharacterId; <see cref="Guid.Empty"/> when unknown.</param>
/// <param name="Entity">The session entity while in game; <see cref="EntityRef.None"/> while offline.</param>
public readonly record struct PlayerRef(ulong SteamId, Guid CharacterId, EntityRef Entity)
{
    /// <summary>The capability that maps players to their session entities.</summary>
    internal const string Capability = "identity.players";

    /// <summary>No player.</summary>
    public static readonly PlayerRef None = default;

    /// <summary>True when every part is empty.</summary>
    public bool IsNone => SteamId == 0 && CharacterId == Guid.Empty && Entity.IsNone;

    /// <summary>True when the character is known.</summary>
    public bool HasCharacter => CharacterId != Guid.Empty;

    /// <summary>True only when the held session entity has a serial and is still alive (<see cref="EntityRef.IsAlive"/>).
    /// False without a serial or for an earlier session; <see cref="Resolve"/> gives the current one.</summary>
    /// <exception cref="KilnUnavailableException">The session entity has a serial but no entity source is registered
    /// (<c>identity.entities</c>).</exception>
    [TickThreadOnly]
    public bool IsOnline => Entity.HasSerial && Entity.IsAlive;

    /// <summary>The player whose session entity this is; <see cref="None"/> when it is not a player in game.</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">No player source is registered (<c>identity.players</c>).</exception>
    [TickThreadOnly]
    public static PlayerRef Of(EntityId player)
    {
        Game.AssertTickThread();
        if (player.IsNone) return None;
        return Source().FromEntity(player);
    }

    /// <summary>The same person, with their current session entity (<see cref="EntityRef.None"/> when offline).</summary>
    /// <exception cref="InvalidOperationException">Not on the tick thread.</exception>
    /// <exception cref="KilnUnavailableException">No player source is registered (<c>identity.players</c>).</exception>
    [TickThreadOnly]
    public PlayerRef Resolve()
    {
        Game.AssertTickThread();
        if (IsNone) return None;
        return Source().Current(SteamId, CharacterId);
    }

    /// <summary><c>"76561198000000000/1b4e28ba-…@3#7"</c>; the character and the entity are left out when empty;
    /// <c>"none"</c>.</summary>
    public override string ToString()
    {
        if (IsNone) return "none";
        var s = SteamId.ToString();
        if (HasCharacter) s += "/" + CharacterId.ToString("D");
        if (!Entity.IsNone) s += "@" + Entity;
        return s;
    }

    private static IPlayerRefSource Source() => IdentitySources.Players
        ?? throw new KilnUnavailableException(Capability, "no service supplies players in this process");
}

/// <summary>Where <see cref="EntityRef"/> reads entity serials. Filled by the service that knows them.</summary>
internal interface IEntitySerialSource
{
    /// <summary>false: no such entity. serial 0: it exists and has no spawn stamp.</summary>
    bool TryGetSerial(EntityId entity, out ulong serial);
}

/// <summary>Where <see cref="PlayerRef"/> resolves players. Filled by the service that knows them.</summary>
internal interface IPlayerRefSource
{
    /// <summary>The player whose session entity this is; <see cref="PlayerRef.None"/> when it is not a player in game.</summary>
    PlayerRef FromEntity(EntityId player);

    /// <summary>The person with their current session entity; entity none when offline.</summary>
    PlayerRef Current(ulong steamId, Guid characterId);
}

/// <summary>The identity seams, set by services (and tests), the way <c>Backend.Current</c> is.</summary>
internal static class IdentitySources
{
    public static IEntitySerialSource? Entities { get; set; }
    public static IPlayerRefSource? Players { get; set; }
}
