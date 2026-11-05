// SPDX-License-Identifier: MIT
// Gameplay events on Kiln's bus (Forge: PlayerInteractEvent, BlockEvent, ItemCraftedEvent, EntityItemPickupEvent,
// EntityTeleportEvent, LivingSpawnEvent.CheckSpawn). All are posted on the tick thread, and only while a handler is
// subscribed to them. See docs/kiln/services/gameplay.md for when each fires and what canceling does in the game.
namespace Kiln;

/// <summary>Actions a protection zone can deny (and the action an event is about).</summary>
[Flags]
public enum GameplayActions : uint
{
    None = 0,
    /// <summary>Place building blocks and props.</summary>
    Build = 0x01,
    /// <summary>Dismantle blocks, pick props up.</summary>
    Break = 0x02,
    /// <summary>Dig, add or flatten terrain.</summary>
    Terraform = 0x04,
    /// <summary>Accept interaction offers (open chests and doors, use stations, beds, portals, talk to NPCs).</summary>
    Interact = 0x08,
    /// <summary>Loot, harvest, pick items up.</summary>
    Loot = 0x10,
    /// <summary>Craft at stations inside.</summary>
    Craft = 0x20,
    /// <summary>Natural spawns (random spawners) inside; applies to everyone.</summary>
    Spawn = 0x40,
    All = 0x7F,
}

/// <summary>A stack of items (item id as the game's <c>ItemId</c>, see Kiln.Items).</summary>
public readonly record struct GameplayItemStack(uint ItemId, int Count);

/// <summary>Base of the cancelable gameplay events: a protection zone may have canceled the event before any handler
/// ran (<see cref="DeniedByZone"/> != 0, <see cref="CancelableEvent.Canceled"/> already true). A handler that
/// subscribed with <c>receiveCanceled</c> may un-cancel it, which overrides the zone for this one event.</summary>
public abstract class GameplayCancelableEvent : CancelableEvent
{
    /// <summary>Server-world step.</summary>
    public ulong Step { get; internal init; }

    /// <summary>Id of the zone that denied this (0 = none).</summary>
    public uint DeniedByZone { get; internal init; }

    /// <summary>Name of that zone ("" when none or unknown).</summary>
    public string ZoneName => DeniedByZone == 0 ? "" : Gameplay.ZoneName(DeniedByZone);

    /// <summary>Sent by Kiln (<see cref="Gameplay.Inject"/>), not by the game.</summary>
    public bool Injected { get; internal init; }
}

/// <summary>Base of the gameplay notifications.</summary>
public abstract class GameplayEvent : KilnEvent
{
    public ulong Step { get; internal init; }
}

// ------------------------------------------------------------------------------------------------- crafting ----

/// <summary>A craft request the game handles in this step (a player's craft at a station or by hand, or a mod's
/// request). Pre phase: before the game's <c>handle_crafting_events</c> reads it. Cancel: the request is dropped;
/// nothing is consumed or made, and the client is told nothing (its UI shows the materials still there).</summary>
public sealed class CraftEvent : GameplayCancelableEvent
{
    /// <summary>Whose inventory pays and receives: a player, a chest, a villager.</summary>
    public EntityId Operator { get; internal init; }
    /// <summary>The entity with <c>Crafting</c>: a workbench, a villager, or the player itself (hand crafting).</summary>
    public EntityId Station { get; internal init; }
    public uint RecipeId { get; internal init; }
    /// <summary>How many times the recipe is crafted.</summary>
    public int Amount { get; internal init; }
    public WorldPosition? StationPosition { get; internal init; }
    public bool ByPlayer => Operator.IsPlayer;
}

/// <summary>The game's result codes of one craft (the craft core).</summary>
public enum CraftResult
{
    Crafted = 0,
    NoRoomForOutput = 2,
    MissingMaterials = 4,
    MissingCraftingProps = 5,
    Other = -1,
}

/// <summary>The game ran its craft core once (one unit of a craft request, or an automated station's cycle). Post
/// phase of the same step. Forge's ItemCraftedEvent, fired for failures too: check <see cref="Succeeded"/>.</summary>
public sealed class ItemCraftedEvent : GameplayEvent
{
    public EntityId Operator { get; internal init; }
    public EntityId Station { get; internal init; }
    public uint RecipeId { get; internal init; }
    public uint WorkshopId { get; internal init; }
    public CraftResult Result { get; internal init; }
    public int RawResult { get; internal init; }
    public bool Succeeded => RawResult == 0;
    /// <summary>The recipe's outputs for one craft (what arrived when <see cref="Succeeded"/>).</summary>
    public IReadOnlyList<GameplayItemStack> Outputs { get; internal init; } = [];
    public WorldPosition? StationPosition { get; internal init; }
    public bool ByPlayer => Operator.IsPlayer;
}

// ---------------------------------------------------------------------------------------------- interaction ----

/// <summary>Someone accepts an interaction offer: E on a chest, door, workstation, bed, portal, NPC (villagers send
/// it too). Pre phase, before the game's <c>interaction_attempt</c>. Cancel: the attempt becomes the game's own
/// DeclinedInteraction, which ends the guest's interaction sequence as when the game refuses (the client sees the
/// interaction fail; nothing opens).</summary>
public sealed class InteractEvent : GameplayCancelableEvent
{
    public EntityId Guest { get; internal init; }
    public EntityId Host { get; internal init; }
    public uint OfferId { get; internal init; }
    public WorldPosition? HostPosition { get; internal init; }
    public bool ByPlayer => Guest.IsPlayer;
}

/// <summary>The game's answer to an interaction attempt of the previous step (Pre phase): accepted, or declined
/// (including the ones Kiln declined).</summary>
public sealed class InteractionResultEvent : GameplayEvent
{
    public EntityId Guest { get; internal init; }
    public EntityId Host { get; internal init; }
    public uint OfferId { get; internal init; }
    public bool Accepted { get; internal init; }
}

/// <summary>A loot interaction: take an item lying in the world, loot a container all at once, harvest a plant,
/// gather produce (Forge's EntityItemPickupEvent). Pre phase, before the game's loot programs. Cancel: becomes a
/// DeclinedInteraction; nothing moves. Items collected by walking over them (no interaction) are not seen here,
/// only in <see cref="ItemPickupEvent"/>.</summary>
public sealed class LootEvent : GameplayCancelableEvent
{
    /// <summary>Who receives: a player, or a chest / villager for mod-sent loot.</summary>
    public EntityId Guest { get; internal init; }
    /// <summary>What is looted: the item pickup, container, plant, animal.</summary>
    public EntityId Host { get; internal init; }
    public uint OfferId { get; internal init; }
    public bool LootAll { get; internal init; }
    public bool BypassInteraction { get; internal init; }
    public WorldPosition? HostPosition { get; internal init; }
    public bool ByPlayer => Guest.IsPlayer;
}

/// <summary>Items arrived in an inventory in the previous step (the game's PickupEvent: the "+N" popup). Fires for
/// looting, harvesting, crafting output, building refunds (Forge's ItemPickupEvent, after the fact).</summary>
public sealed class ItemPickupEvent : GameplayEvent
{
    public EntityId Receiver { get; internal init; }
    /// <summary>Where the items came from (none for crafting output).</summary>
    public EntityId Source { get; internal init; }
    public uint ItemId { get; internal init; }
    public int Count { get; internal init; }
    /// <summary>The 8-slot inventory entity the items went to, and the slot index.</summary>
    public EntityId SlotInventory { get; internal init; }
    public int SlotIndex { get; internal init; }
    public bool HiddenInUi { get; internal init; }
    public WorldPosition Position { get; internal init; }
}

// ------------------------------------------------------------------------------------------------- building ----

public enum BuildKind
{
    PlaceBlock = 1,
    PlaceProp = 2,
    Dismantle = 3,
    PickUpProp = 4,
    Terraform = 5,
}

public enum BuildOutcome
{
    /// <summary>The game did it.</summary>
    Done,
    /// <summary>The game refused it itself (no room, collision, missing materials, ...).</summary>
    RefusedByGame,
    /// <summary>A protection zone refused it (the game rolled the inventory back and told the client it failed).</summary>
    DeniedByZone,
}

/// <summary>A building or terraforming operation of this step (Forge's BlockEvent.EntityPlaceEvent / BreakEvent).
/// Post phase: the operation already ran (or was refused) inside the step on a game worker thread, where no mod code
/// can run. To refuse edits use <see cref="Gameplay.Protect"/> zones, which the game's building code consults
/// synchronously; this event then reports <see cref="BuildOutcome.DeniedByZone"/>.</summary>
public sealed class BuildEvent : GameplayEvent
{
    public BuildKind Kind { get; internal init; }
    public EntityId Player { get; internal init; }
    /// <summary>Block / prop item (place), material item (dismantle); 0 for terraforming.</summary>
    public uint ItemId { get; internal init; }
    public WorldPosition Position { get; internal init; }
    /// <summary>The prop entity (<see cref="BuildKind.PickUpProp"/>).</summary>
    public EntityId Target { get; internal init; }
    /// <summary>Terraform: 0 remove (dig), 1 add, 2-3 flatten, 4-5 overgrowth, 6-7 decay.</summary>
    public int TerraformType { get; internal init; }
    public BuildOutcome Outcome { get; internal init; }
    public uint DeniedByZone { get; internal init; }
    public string ZoneName => DeniedByZone == 0 ? "" : Gameplay.ZoneName(DeniedByZone);
    public bool IsPlacement => Kind is BuildKind.PlaceBlock or BuildKind.PlaceProp;
    public bool IsRemoval => Kind is BuildKind.Dismantle or BuildKind.PickUpProp || (Kind == BuildKind.Terraform && TerraformType == 0);
}

// ------------------------------------------------------------------------------------------------ teleports ----

/// <summary>A watched entity (every player; others with <see cref="Gameplay.WatchTeleports"/>) is about to be
/// teleported by the game: fast travel, respawn, portal, admin teleport, kiln.world. Pre phase, the step before the
/// game's <c>teleport</c> program moves it. Cancel: the entity stays where it is. Setting <see cref="Target"/> sends
/// it there instead.</summary>
public sealed class TeleportEvent : GameplayCancelableEvent
{
    public EntityId Entity { get; internal init; }
    public WorldPosition From { get; internal init; }
    /// <summary>The destination. Handlers may change it (metres).</summary>
    public WorldPosition Target { get; set; }
    /// <summary>The spawn point / flame altar it travels to (none for a plain position).</summary>
    public EntityId TargetEntity { get; internal init; }
    /// <summary>The game searches walkable ground near the destination.</summary>
    public bool SearchesGround { get; internal init; }
    public bool IsPlayer => Entity.IsPlayer;
}

/// <summary>A watched entity was teleported this step (its TeleportCount changed). Post phase.</summary>
public sealed class TeleportedEvent : GameplayEvent
{
    public EntityId Entity { get; internal init; }
    public WorldPosition From { get; internal init; }
    public WorldPosition To { get; internal init; }
    public int Count { get; internal init; }
}

// ------------------------------------------------------------------------------------------------- spawning ----

/// <summary>A random spawner (the world's natural enemy and wildlife spawns, which run near players) created a
/// creature this step (Forge's LivingSpawnEvent.CheckSpawn). Post phase: the entity exists but no client has seen it.
/// Cancel: it is destroyed before the step returns; the spawner treats it as gone. Spawns refused by a
/// <see cref="GameplayActions.Spawn"/> zone arrive already canceled with <see cref="Entity"/> none.</summary>
public sealed class NaturalSpawnEvent : GameplayCancelableEvent
{
    public EntityId Entity { get; internal init; }
    public Guid Template { get; internal init; }
    public WorldPosition Position { get; internal init; }
    /// <summary>A <see cref="Gameplay.ReplaceNaturalSpawns"/> rule swapped the spawner's template: <see cref="Template"/>
    /// is the replacement.</summary>
    public bool Replaced { get; internal init; }
}

// ------------------------------------------------------------------------------------------- entity lifecycle ----

/// <summary>Kinds of entities for <see cref="EntityJoinLevelEvent"/> / <see cref="EntityLeaveLevelEvent"/> (from the
/// template name, like <see cref="TemplateKind"/>).</summary>
[Flags]
public enum EntityCategories : uint
{
    None = 0,
    Player = 0x01,
    /// <summary>Enemies and animals.</summary>
    Creature = 0x02,
    /// <summary>Villagers.</summary>
    Npc = 0x04,
    /// <summary>Props: chests, stations, decoration, building props.</summary>
    Prop = 0x08,
    /// <summary>Loose item pickups.</summary>
    Pickup = 0x10,
    /// <summary>The rest: VFX, attacks, projectiles, loot containers, spawners, weakspots, child entities. Many and
    /// short-lived: only reported when a mod asks for them with <see cref="Gameplay.WatchEntities"/>.</summary>
    Other = 0x20,
    /// <summary>What is reported when no mod narrowed or widened it.</summary>
    Default = Player | Creature | Npc | Prop | Pickup,
    All = 0x3F,
}

/// <summary>Fields shared by <see cref="EntityJoinLevelEvent"/> and <see cref="EntityLeaveLevelEvent"/>.</summary>
public interface IEntityLifecycleEvent
{
    EntityId Entity { get; }
    /// <summary>Template GUID (null for entities without one).</summary>
    Guid? Template { get; }
    /// <summary>The entity this one belongs to (a creature for its weakspot), or None.</summary>
    EntityId Parent { get; }
    EntityCategories Category { get; }
    /// <summary>Where it was (metres); null when it has no transform.</summary>
    WorldPosition? Position { get; }
    /// <summary>Created / destroyed outside the game's sim step: by Kiln (Kiln.Spawn, a service, a mod's Pre/Post code),
    /// not by a game program. Entities the game makes for a Kiln request inside the step (a gear item's entity from
    /// Kiln.Items) count as the game's.</summary>
    bool ByKiln { get; }
    /// <summary>Kiln.Spawn's template record (name, display name, kind), or null.</summary>
    TemplateInfo? TemplateInfo { get; }
    /// <summary>The engine's template name ("Enemy_Vukah_Melee"), or "".</summary>
    string TemplateName { get; }
}

internal struct TemplateLookup
{
    private TemplateInfo? _info;
    private bool _looked;

    public TemplateInfo? Get(Guid? template)
    {
        if (_looked) return _info;
        _looked = true;
        return _info = template is { } g ? Spawn.Template(g) : null;
    }
}

/// <summary>An entity was created in the server world (Forge: EntityJoinLevelEvent), by any path: spawners, NPC
/// logic, loot, child entities, a player joining, the save loading (once, at world start), or Kiln.Spawn. Posted in
/// Post of the step that created it (or the Post after a creation on the tick thread). Only the categories mods ask for
/// are reported (<see cref="Gameplay.WatchEntities"/>; <see cref="EntityCategories.Default"/> otherwise).</summary>
/// <remarks>Cancel (not players): the entity is destroyed again right away, like a canceled
/// <see cref="NaturalSpawnEvent"/>; game logic that created it treats it as gone. Be selective: removing child entities
/// (weakspots, links) of a creature you keep can break it.</remarks>
public sealed class EntityJoinLevelEvent : GameplayCancelableEvent, IEntityLifecycleEvent
{
    private TemplateLookup _t;
    public EntityId Entity { get; internal init; }
    public Guid? Template { get; internal init; }
    public EntityId Parent { get; internal init; }
    public EntityCategories Category { get; internal init; }
    public WorldPosition? Position { get; internal init; }
    public bool ByKiln { get; internal init; }
    public TemplateInfo? TemplateInfo => _t.Get(Template);
    public string TemplateName => TemplateInfo?.Name ?? "";
    public bool IsPlayer => Category == EntityCategories.Player;
}

/// <summary>An entity was destroyed (Forge: EntityLeaveLevelEvent): LifeTime ran out, the game culled or despawned
/// it, its loot was taken, a queued destroy, <c>Spawn.Destroy</c>. Posted in Post; observe only. Not reported for the
/// bulk removal when the world shuts down.</summary>
public sealed class EntityLeaveLevelEvent : GameplayEvent, IEntityLifecycleEvent
{
    private TemplateLookup _t;
    public EntityId Entity { get; internal init; }
    public Guid? Template { get; internal init; }
    public EntityId Parent { get; internal init; }
    public EntityCategories Category { get; internal init; }
    public WorldPosition? Position { get; internal init; }
    public bool ByKiln { get; internal init; }
    public TemplateInfo? TemplateInfo => _t.Get(Template);
    public string TemplateName => TemplateInfo?.Name ?? "";
    public bool IsPlayer => Category == EntityCategories.Player;
}

// ------------------------------------------------------------------------------------------------- discard ------

/// <summary>How a player discarded a stack (<see cref="ItemTossEvent.How"/>).</summary>
public enum DiscardKind
{
    /// <summary>The inventory's destroy action (dragging a stack out / the destroy button).</summary>
    Destroy = 1,
    /// <summary>The item menu's Trash.</summary>
    Trash = 2,
}

/// <summary>A player is about to discard an item stack from an inventory (Forge: ItemTossEvent). Enshrouded has no
/// world drop: discarding destroys the stack. Pre phase, read from the player's input before the game's
/// <c>inventory_actions</c> handles it. Cancel: the action is marked as handled, the game never runs it and the item
/// stays in the slot.</summary>
public sealed class ItemTossEvent : GameplayCancelableEvent
{
    public EntityId Player { get; internal init; }
    public DiscardKind How { get; internal init; }
    /// <summary>The inventory entity of the slot (a player's bag, a chest's inventory link).</summary>
    public EntityId Inventory { get; internal init; }
    public int SlotIndex { get; internal init; }
    /// <summary>The game's item id in that slot (Kiln.Items <c>ItemId</c>); 0 if the slot could not be read.</summary>
    public uint ItemId { get; internal init; }
    public int Count { get; internal init; }
    /// <summary>The stack's own entity (gear with level, perks, durability), or None.</summary>
    public EntityId ItemEntity { get; internal init; }
    public WorldPosition? PlayerPosition { get; internal init; }
}
