// SPDX-License-Identifier: MIT
// IInstanceWorld over the real game: items through Kiln.Items' internal face (ItemInstances: kiln.items v4 stamps,
// locations, save-record fields, the tracker), holders through kiln.spawn (SpawnLink) and kiln.players (Players,
// CharacterStore), the step through the service tick. Set at Kiln.Saves' start; whether item instances are on is asked at
// every call (Require), so InstanceData is off, naming items.instances, while kiln.items has no v4 or the feature is off.
namespace Kiln;

internal sealed class InstanceGame : IInstanceWorld
{
    private Action<ItemInstanceRef, InstancePlace, InstancePlace>? _moved;
    private Action<ItemInstanceRef, InstancePlace, ulong>? _gone;
    private bool _subscribed;

    public void Require() => ItemInstances.Require();

    public ulong Step => ServiceTick.Step;

    public InstanceItem? Read(ItemInstanceRef item) => ItemInstances.Read(item) is { } info ? Map(info) : null;

    public IReadOnlyList<InstanceItem> ReadHolder(EntityId holder, out bool initialized) =>
        ItemInstances.ReadHolder(holder, out initialized).Where(i => i.Location.IsPlaced && i.Location.Holder == holder).Select(i => Map(i)).ToList();

    public IDisposable Follow(ItemInstanceRef item) => ItemInstances.Follow(item);

    public event Action<ItemInstanceRef, InstancePlace, InstancePlace>? Moved
    {
        add
        {
            _moved += value;
            Subscribe();
        }
        remove => _moved -= value;
    }

    public event Action<ItemInstanceRef, InstancePlace, ulong>? Gone
    {
        add
        {
            _gone += value;
            Subscribe();
        }
        remove => _gone -= value;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;
        ItemInstances.Moved += (item, from, to, _) => _moved?.Invoke(item, PlaceOf(from), PlaceOf(to));
        ItemInstances.Gone += (item, last, step) => _gone?.Invoke(item, PlaceOf(last), step);
    }

    public HolderIdentity Holder(EntityId holder) => IdentityOf(holder, CharacterStore.KeyOf);

    public IReadOnlyList<EntityId> FindHolders(Guid template, WorldPosition near, double radius) => SpawnLink.Find(template, near, radius, 16);

    public IReadOnlyList<(EntityId Entity, CharacterKey Character)> PlayersInGame() =>
        Players.Available
            ? Players.All.Where(p => p.InGame && !p.Entity.IsNone).Select(p => (p.Entity, CharacterStore.KeyOf(p))).ToList()
            : [];

    // ---- the mapping (pure, unit-tested) ----

    /// <summary>Kiln.Items' item entity as the store sees it. An item in no holder's slot has no place (holder none): the
    /// store keeps its data for the session only.</summary>
    internal static InstanceItem Map(in ItemInstanceInfo info) => new(info.Item, PlaceOf(info.Location), FieldsOf(info.Record));

    /// <summary>The flat slot is kept as Kiln.Items numbers it, an opaque number unique in the holder: 0-255 are the
    /// game's own save slots (Generic links), 256 and up the other links (equipment, currency, customization, nested).</summary>
    internal static InstancePlace PlaceOf(in ItemLocation location) =>
        location.IsPlaced ? new InstancePlace(location.Holder, location.FlatSlot) : new InstancePlace(EntityId.None, -1);

    internal static InstanceFields FieldsOf(in ItemRecord r) =>
        new(r.Item, r.Count, r.Level, r.Durability, r.DurabilityMax, r.Fuel, r.FuelMax, r.Dye, r.PerksAvailable, r.PerksUnlocked, r.Gem, r.GemLevel);

    /// <summary>Who a holder is: a player by its character; a world entity by kiln.spawn's template and position, when that
    /// template is known, saved and has a position (else none: data there is for the session only).</summary>
    /// <exception cref="KilnUnavailableException">kiln.spawn cannot tell right now (never "none" for that).</exception>
    internal static HolderIdentity IdentityOf(EntityId holder, Func<EntityId, CharacterKey> characterOf)
    {
        if (holder.IsNone) return HolderIdentity.None;
        if (holder.IsPlayer) return HolderIdentity.OfPlayer(characterOf(holder), $"player {holder}");
        if (!SpawnLink.Available) return HolderIdentity.None;
        SpawnLink.Ent? ent;
        try
        {
            ent = SpawnLink.Inspect(holder);
        }
        catch (InvalidOperationException e)
        {
            throw new KilnUnavailableException("kiln.spawn", e.Message, e);
        }
        if (ent?.Template is not { } template) return HolderIdentity.None;
        var meta = SpawnLink.Template(template);
        if (!meta.canPersist) return HolderIdentity.None;
        return HolderIdentity.OfWorld(template, ent.Position, SpawnLink.IsMobile(template), ent.Name);
    }
}
