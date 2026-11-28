# Items (Kiln.Items)

## AmmoKinds `0.2.16` — Kinds of ammunition, for `StorageFilter.Ammunition`.
enum: Arrow, Spell

## ClientTransfer — A client's `inventoryTransferAction`.
- `ClientTransfer()`
- `ClientTransfer(EntityId Source, EntityId Target, SlotId SourceSlot, SlotId TargetSlot, ClientTransferAmount Amount, int CustomAmount, bool RefillStacks, bool MoveAllStacks, bool Gifting)`
- `Amount { get; init; } -> ClientTransferAmount` — How much of the stack the client asks for.
- `CustomAmount { get; init; } -> int` — The amount when `ClientTransfer.Amount` is `ClientTransferAmount.CustomAmount`.
- `Gifting { get; init; } -> bool` — True when the player gives the items to another player.
- `MoveAllStacks { get; init; } -> bool` — True for a move all.
- `RefillStacks { get; init; } -> bool` — True for a quick stack (the R key held).
- `Source { get; init; } -> EntityId` — The inventory owner the items leave.
- `SourceSlot { get; init; } -> SlotId` — The slot they leave (a single-stack transfer).
- `Target { get; init; } -> EntityId` — The inventory owner they go to.
- `TargetSlot { get; init; } -> SlotId` — The slot they go to (a single-stack transfer).

## ClientTransferAmount — What a client's `inventoryTransferAction` asks for (`Transfers.ParseAction`).
enum: One, Half, All, CustomAmount, Destroy

## CollectionEquipmentChange `0.2.4` — The game's own collection swap (swapEquipmentSetAction): the player swaps the stacks of `CollectionEquipmentChange.TargetInventory` into its active equipment or its...
- `CollectionEquipmentChange(EventEntityKey TargetInventory, bool Vanity, uint Version, IReadOnlyList<CollectionStack> Contents)`
- `Contents { get; init; } -> IReadOnlyList<CollectionStack>` — The collection's stacks (a copy of the list it was made with).
- `TargetInventory { get; init; } -> EventEntityKey` — The collection's inventory.
- `Vanity { get; init; } -> bool` — True: into the vanity slots; false: the active equipment.
- `Version { get; init; } -> uint` — The input's version.

## CollectionStack `0.2.4` — One stack of a collection as it was read in the Pre of the event.
- `CollectionStack(SlotId Source, ItemId Item, int Count, ItemInstanceRef Instance)`
- `Count { get; init; } -> int` — How many.
- `Instance { get; init; } -> ItemInstanceRef` — The item instance; `ItemInstanceRef.None` for a plain stack.
- `Item { get; init; } -> ItemId` — The item type.
- `Source { get; init; } -> SlotId` — Its inventory slot.

## DurabilityRule `0.2.12` — What happens to an item's durability when its rarity, enhancements or level change.
enum: KeepRatio, Keep, Repair

## EmptyEquipment `0.2.4` — Nothing in the slot.

## EquipChangedEvent `0.2.2` — Kiln's `Items.Equip` or `Items.Unequip` changed a player's equipment: posted once per committed change, on the tick thread when its result arrives (also when one ring...
- `After { get; } -> ItemInstanceRef` — What it holds now (none after an unequip).
- `AfterItem { get; } -> ItemId` — The item type now.
- `Before { get; } -> ItemInstanceRef` — What the slot held before (an unequip: the item taken off; an equip: what it replaced, none when it was empty).
- `BeforeItem { get; } -> ItemId` — The item type before (see `EquipChangedEvent.Before`).
- `Player { get; } -> EntityId` — The player.
- `Result { get; } -> EquipResult` — The committed change.
- `Slot { get; } -> EquipmentSlot?` — The equipment slot; null when the game's choice could not be read back.

## EquipmentChange `0.2.4` — One equipment change: `SingleEquipmentChange`, `CollectionEquipmentChange` or `SetEquipmentChange`.
- `EquipmentChange(EquipmentChange original)`
- `EquipmentChange(EquipmentChangeKind Kind)`
- `Kind { get; init; } -> EquipmentChangeKind` — Which one.

## EquipmentChangeKind `0.2.4` — Which kind of equipment change a `PlayerEquippingEvent` is about.
enum: Equip, Unequip, Collection, Set

## EquipmentSet `0.2.4` — A set of equipment slots and what each should hold.
- `EquipmentSet(EquipmentSetScope Scope, IReadOnlyList<EquipmentSetEntry> Entries)`
- `Entries { get; init; } -> IReadOnlyList<EquipmentSetEntry>` — One entry per slot (a copy of the list it was made with).
- `Scope { get; init; } -> EquipmentSetScope` — Where the set is valid.

## EquipmentSetCaptureResult `0.2.4` — What `Items.CaptureSet` returned: all 20 equipment slots of a player as one `EquipmentSet` (empty slots as `EmptyEquipment`), scoped to this process, this server...
- `EquipmentSetCaptureResult(bool Ok, string? Reason, EquipmentSet? Set)`
- `Ok { get; init; } -> bool` — True when the set was read.
- `Reason { get; init; } -> string?` — Why not; null when `Ok`.
- `Set { get; init; } -> EquipmentSet?` — The set; null when not `Ok`.

## EquipmentSetChangedEvent `0.2.4` — A set changed a player's equipment: posted once per committed set (Kiln's `Items.EquipSet`), on the tick thread in the Pre its result arrives in, before the asking...
- `EquipmentSetChangedEvent(EntityId player, ulong ticket, EquipmentSetStatus outcome, IEnumerable<EquipmentSlotChange> changes)` — Makes the event (Kiln posts it; a mod has no reason to).
- `Changes { get; } -> IReadOnlyList<EquipmentSlotChange>` — The slots that changed, one each.
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`: the player.
- `Outcome { get; } -> EquipmentSetStatus` — Done, or InvariantFailure (see the class).
- `Player { get; } -> EntityId` — The player.
- `Ticket { get; } -> ulong` — kiln.items' ticket of the set (`EquipmentSetResult.Ticket`).

## EquipmentSetEntry `0.2.4` — What one equipment slot of a set holds.
- `EquipmentSetEntry(EquipmentSlot Slot, EquipmentSetSelection Selection)`
- `Selection { get; init; } -> EquipmentSetSelection` — Empty, one item instance, or a plain stack.
- `Slot { get; init; } -> EquipmentSlot` — The slot.

## EquipmentSetFaultedEvent `0.2.4` — A set's transaction left the inventory neither as asked nor as before, or the game faulted in the set's reads or inside its transaction...
- `EquipmentSetFaultedEvent(EquipmentSetResult result)`
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`: the player.
- `Result { get; } -> EquipmentSetResult` — The set's result (Status InvariantFailure, MayHaveMutated).

## EquipmentSetJson `0.2.4` — Writes and reads an `EquipmentSet` as JSON (schema 1) for the current session or for a log.
- `static Deserialize(string json) -> EquipmentSet` — A set from JSON written by `EquipmentSetJson.Serialize` (schema 1).
- `const Schema = 1 -> int` — The schema this Kiln writes and reads.
- `static Serialize(EquipmentSet set, bool indented = false) -> string` — The set as JSON.

## EquipmentSetPlan `0.2.4` — A set checked against a snapshot of the player's inventory (`Items.PlanSet`): the steps the transaction would run and how many free backpack slots it needs on the way.
- `EquipmentSetPlan(EntityId Player, EquipmentSet Set, InventorySnapshotToken Snapshot, ulong ValidThroughStep, IReadOnlyList<EquipmentSetStep> Steps, int RequiredFreeSlots)`
- `Dispose() -> void` — Releases the plan's snapshot when it was not applied; nothing after an apply or a second call.
- `Player { get; init; } -> EntityId` — The player.
- `RequiredFreeSlots { get; init; } -> int` — The most free Generic backpack slots the steps need at one time (an unequip puts the item into a free one).
- `Set { get; init; } -> EquipmentSet` — The set that was planned (a copy).
- `Snapshot { get; init; } -> InventorySnapshotToken` — The snapshot the plan was checked against.
- `Steps { get; init; } -> IReadOnlyList<EquipmentSetStep>` — The calls the transaction would make, in order (a copy of the list it was made with).
- `ValidThroughStep { get; init; } -> ulong` — The last server step the snapshot can be applied in (its capture step + 250).

## EquipmentSetPlanResult `0.2.4` — What `Items.PlanSet` returned.
- `EquipmentSetPlanResult(bool Ok, string? Reason, EquipmentSetPlan? Plan)`
- `Ok { get; init; } -> bool` — True when the set can be applied now as planned.
- `Plan { get; init; } -> EquipmentSetPlan?` — The plan; null when not `Ok`.
- `Reason { get; init; } -> string?` — Why not (a stale scope, the snapshot budget, an item that is not there, no room, a form Kiln refuses); null when `Ok`.

## EquipmentSetResult `0.2.4` — What `Items.EquipSet` did.
- `EquipmentSetResult(EquipmentSetStatus Status, string? Reason, EntityId Player, ulong Ticket, DecisionId? Decision, bool DryRun, bool Committed, bool MayHaveMutated, int? FailedOperation, int GameError, IReadOnlyList<EquipmentSlotChange> Changes)`
- `Changes { get; init; } -> IReadOnlyList<EquipmentSlotChange>` — The slots a commit changed (a copy of the list it was made with).
- `Committed { get; init; } -> bool` — The game's commit ran and changed the inventory.
- `Decision { get; init; } -> DecisionId?` — The `PlayerEquippingEvent` decision of the set; null when none was filed (no change, a refusal before the decision...
- `DryRun { get; init; } -> bool` — Asked with `EquipOptions.DryRun` (kept on every result, refusals included).
- `FailedOperation { get; init; } -> int?` — The index of the step the game refused (see `EquipmentSetPlan.Steps`); null when no step was refused.
- `GameError { get; init; } -> int` — The game's error code of that step (0x11 refused, 0x15 no item, 0x1f no equipment or no room); 0 none.
- `MayHaveMutated { get; init; } -> bool` — The inventory may differ from both the set and the state before (`EquipmentSetStatus.InvariantFailure`).
- `Ok { get; } -> bool` — True for `EquipmentSetStatus.Done`, `EquipmentSetStatus.NoChange` and `EquipmentSetStatus.Simulated`.
- `Player { get; init; } -> EntityId` — The player.
- `Reason { get; init; } -> string?` — Why, for anything but Done, NoChange and Simulated.
- `Status { get; init; } -> EquipmentSetStatus` — The outcome.
- `Ticket { get; init; } -> ulong` — kiln.items' ticket of the request (0 when it was refused before it was queued).

## EquipmentSetScope `0.2.4` — Where an `EquipmentSet` is valid: this process, this server world, this life of the player.
- `EquipmentSetScope(Guid ProcessSession, ulong WorldEpoch, EventEntityKey Player)`
- `Player { get; init; } -> EventEntityKey` — The player's life.
- `ProcessSession { get; init; } -> Guid` — The process's session nonce (kiln.window_clock).
- `WorldEpoch { get; init; } -> ulong` — The server world it was made in.

## EquipmentSetSelection `0.2.4` — What an equipment slot holds or is to hold: `EmptyEquipment`, `InstanceEquipment` (one item with its own entity: gear, a ring) or `StackEquipment` (a plain stack with...
- `EquipmentSetSelection(EquipmentSetSelection original)`

## EquipmentSetStatus `0.2.4` — How an `Items.EquipSet` ended.
enum: Done, NoChange, Simulated, Canceled, StaleSnapshot, Refused, CommitRolledBack, Dropped, InvariantFailure, Unavailable

## EquipmentSetStep `0.2.4` — One step of an `EquipmentSetPlan`: one of the game's equip or unequip calls, in the order the transaction would run them.
- `EquipmentSetStep(int Index, EquipmentSlot Target, EquipmentSetSelection Selection)`
- `Index { get; init; } -> int` — Its place in the order, from 0.
- `Selection { get; init; } -> EquipmentSetSelection` — What goes in (`EmptyEquipment`: what is there comes off).
- `Target { get; init; } -> EquipmentSlot` — The equipment slot the call works on.

## EquipmentSlot — One of a player's equipment slots, named and ordered as the game's `SlotSelection.equipment` fields (the same values as Kiln.Players' `PlayerEquipSlot`).
enum: VanityHead, VanityUpperBody, VanityArms, VanityLowerBody, VanityFeet, ArmourHead, ArmourUpperBody, ArmourArms, ArmourLowerBody, ArmourFeet, Ring0, Ring1, RangedWeapon, Shield, Arrow, Spell, FishingBait, HookShot, Glider, Backpack

## EquipmentSlotChange `0.2.4` — One equipment slot a committed set changed: what it held before and after the transaction.
- `EquipmentSlotChange(EquipmentSlot Slot, EquipmentSlotValue Before, EquipmentSlotValue After)`
- `After { get; init; } -> EquipmentSlotValue` — After its commit.
- `Before { get; init; } -> EquipmentSlotValue` — Before the transaction.
- `Slot { get; init; } -> EquipmentSlot` — The equipment slot.

## EquipmentSlotValue `0.2.4` — What one equipment slot held at one moment: the item type, the item entity (none for a plain stack), the inventory slot it sits in and the count.
- `EquipmentSlotValue(ItemId Item, ItemInstanceRef Instance, SlotId Source, int Count)`
- `Count { get; init; } -> int` — How many; 0 when empty.
- `Instance { get; init; } -> ItemInstanceRef` — The item entity; `ItemInstanceRef.None` for a plain stack or an empty slot.
- `Item { get; init; } -> ItemId` — The item type; none when empty.
- `Source { get; init; } -> SlotId` — The inventory slot that holds it (an equipment page's slot); none when empty.

## EquipOptions `0.2.2` — Options of `Items.Equip` and `Items.Unequip`.
enum: None, DryRun

## EquipResult `0.2.2` — What `Items.Equip` or `Items.Unequip` did.
- `EquipResult(ItemsStatus Status, EntityId Player, EquipmentSlot? Slot, bool Unequipped, ItemId Item, EntityId ItemEntity, SlotId From, SlotId To, ItemId Replaced, EntityId ReplacedEntity, SlotId ReplacedTo, string Message, int GameError, int Steps, bool DryRun)`
- `DryRun { get; init; } -> bool` — Run with `EquipOptions.DryRun`: nothing changed.
- `From { get; init; } -> SlotId` — Where the item was.
- `GameError { get; init; } -> int` — The game's error code (0x11 refused, 0x15 no item, 0x1f no equipment or no room); 0 none.
- `Item { get; init; } -> ItemId` — The item put on or taken off; none when nothing was found.
- `ItemEntity { get; init; } -> EntityId` — Its item entity; none for a plain stack (arrows, bait).
- `Message { get; init; } -> string` — Why, for anything but `ItemsStatus.Done`.
- `Ok { get; } -> bool` — True when the item was put on or taken off (a dry run: would have been).
- `Player { get; init; } -> EntityId` — The player character.
- `Reason { get; } -> string?` — Why it did not happen; null when `EquipResult.Ok`.
- `Ref { get; init; } -> ItemInstanceRef` — The item as one particular item (`ItemInstanceRef.None` for a plain stack, or when the capability `items.instances`...
- `Replaced { get; init; } -> ItemId` — An equip: what the equipment slot held before; none when it was empty.
- `ReplacedCount { get; init; } -> int` `0.2.15` — GiveEquip: the number in the old stack (also on DryRun); 0 for ordinary Equip and Unequip.
- `ReplacedEntity { get; init; } -> EntityId` — The item entity of `Replaced`.
- `ReplacedItem { get; } -> ItemId` `0.2.15` — The stack replaced by GiveEquip; an alias of Replaced.
- `ReplacedRef { get; init; } -> ItemInstanceRef` — `EquipResult.Replaced` as one particular item.
- `ReplacedTo { get; init; } -> SlotId` — Where `Replaced` went; none when not found.
- `Slot { get; init; } -> EquipmentSlot?` — The equipment slot: the one asked for, or after an equip into the item's own slot the one it went to; null when unknown.
- `Status { get; init; } -> ItemsStatus` — The outcome.
- `Steps { get; init; } -> int` — Sim steps between the call and the run.
- `To { get; init; } -> SlotId` — Where it is now (a dry run: would be); none when nothing changed.
- `Unequipped { get; init; } -> bool` — True for `Items.Unequip`.

## FuelChange — One container a water write changed.
- `FuelChange()`
- `FuelChange(EntityId Container, ItemId Item, SlotId Slot, int Before, int After, int Max)`
- `After { get; init; } -> int` — Units after it (a dry run: what they would be).
- `Before { get; init; } -> int` — Units before the write.
- `Container { get; init; } -> EntityId` — The item entity, or the placed container.
- `Item { get; init; } -> ItemId` — Its item (none for a placed container).
- `Max { get; init; } -> int` — The container's max, in units.
- `Slot { get; init; } -> SlotId` — Where it sits (none for a placed container).

## FuelKeep — An active refill policy (`Fuel.Refill`).
- `Active { get; } -> bool` — True until the policy is disposed.
- `Dispose() -> void` — Stops the policy.
- `Entity { get; } -> EntityId` — The holder or container the policy is on.
- `Inventories { get; } -> bool` — True when `FuelKeep.Entity` is a holder and the policy covers the containers in its inventories; false when it is...
- `Item { get; } -> ItemId` — The item the policy is limited to; none for every container.
- `Origin { get; } -> string` — Id of the mod that set it ("kiln" outside mod code).
- `Policy { get; } -> FuelRefill` — How the water is given back.
- `Refilled -> Action<IReadOnlyList<FuelChange>>?` — Raised on the tick thread (Pre) with the containers this policy refilled since the last call.
- `UnitsAdded { get; } -> long` — Units given back so far.

## FuelLevel — The water a container holds, in units (64 per charge).
- `FuelLevel()`
- `FuelLevel(int Units, int Max)`
- `Charges { get; } -> double` — `FuelLevel.Units` in charges (64 units each).
- `IsFull { get; } -> bool` — True when the container holds its maximum.
- `Max { get; init; } -> int` — The most it can hold, in units.
- `MaxCharges { get; } -> double` — `FuelLevel.Max` in charges (64 units each).
- `Units { get; init; } -> int` — The water the container holds now, in units.

## FuelRefill — How a refill policy gives water back (`Fuel.Refill`).
- `static ChargesPerSecond(double charges, int limitUnits = 0) -> FuelRefill` — `charges` charges per second (64 units each).
- `static Full { get; } -> FuelRefill` — Full every step.
- `IsFull { get; } -> bool` — True when the policy keeps containers full every step (`FuelRefill.UnitsPerSecond` is infinite).
- `Limit { get; init; } -> int` — The level kept or refilled to, in units; 0 = each container's max.
- `UnitsPerSecond { get; init; } -> double` — Units per second of game time; `Double.PositiveInfinity` keeps the containers full.

## FuelResult — What a water write did.
- `FuelResult(ItemsStatus Status, EntityId Entity, ItemId Item, int Matched, int Changed, IReadOnlyList<FuelChange> Changes, string Message, int Steps, bool DryRun)`
- `Changed { get; init; } -> int` — How many of them it changed.
- `Changes { get; init; } -> IReadOnlyList<FuelChange>` — The containers it changed: before, after and maximum (only the first 16).
- `DryRun { get; init; } -> bool` — True when nothing was written: the result says what would change.
- `Entity { get; init; } -> EntityId` — The entity the write was for: a holder or the container itself.
- `Item { get; init; } -> ItemId` — The item it was limited to; none when it was not limited.
- `Matched { get; init; } -> int` — How many containers it found.
- `Message { get; init; } -> string` — Why it failed, in words; empty when there is nothing to add.
- `Ok { get; } -> bool` — True when `FuelResult.Status` is `ItemsStatus.Done`, also when nothing needed a change.
- `Status { get; init; } -> ItemsStatus` — How it ended; see the type's summary.
- `Steps { get; init; } -> int` — How many sim steps passed between the request and the write.

## Gear — Gear parameters for `Items.Give`.
- `Gear()`
- `Gear(int? Level = null, ItemRarity? Rarity = null)`
- `Deconstruct(out int? Level, out ItemRarity? Rarity) -> void`
- `Equals(Gear other) -> bool`
- `GemSlot { get; init; } -> bool` — A weapon gets an empty gem slot when the game's own roll at creation gave none.
- `Level { get; init; } -> int?` — The item's level; null for the game's default (a random roll in the item's level range).
- `static operator !=(Gear left, Gear right) -> bool`
- `static operator ==(Gear left, Gear right) -> bool`
- `Rarity { get; init; } -> ItemRarity?` — The item's rarity; null for the game's default (the item's own rarity, or Common for an item with `generateRarity` set).
- `Unlocked { get; init; } -> int?` — Upgrades (perks) unlocked at creation, as the workbench's upgrade does it: each raises the level by 1.

## GearChangedEvent `0.2.12` — An item's enhancement slots, unlocked enhancements, level or (a repair) durability changed: posted for every such change, before `GearRarityChangedEvent` and...
- `Changed { get; } -> GearChanges` — What changed between `GearEvent.Before` and `GearEvent.After`.

## GearChanges `0.2.12` — What a gear write changed (`GearResult.Changed`).
enum: None, Enhancements, Level, Durability, DurabilityRefresh, DurabilityMax

## GearChangeSource `0.2.12` — What wrote a gear change (`GearEvent.Source`).
enum: Code, Upgrade, Game

## GearEnhancedEvent `0.2.12` — How many of an item's enhancements are unlocked changed: a player unlocked one at the workbench (`GearChangeSource.Game`), `Items.SetEnhancements`, or a lower rarity...
- `From { get; } -> int` — Unlocked before.
- `To { get; } -> int` — Unlocked now.

## GearEvent `0.2.12` — Base of the gear events: an item's enhancement slots (so its rarity), its unlocked enhancements or its level changed in place (`Items.SetRarity`...
- `After { get; } -> GearState` — The item after it.
- `Before { get; } -> GearState` — The item before the change.
- `Item { get; } -> ItemInstanceRef` — The item.
- `Origin { get; } -> string` — Id of the mod that wrote it ("kiln" outside mod code; "game" for `GearChangeSource.Game`).
- `Source { get; } -> GearChangeSource` — What made the change.

## GearRarityChangedEvent `0.2.12` — An item's rarity changed (its enhancement slots): see `GearEvent`.
- `From { get; } -> ItemRarity` — The rarity before.
- `To { get; } -> ItemRarity` — The rarity now.

## GearResult `0.2.12` — What `Items.SetRarity`, `Items.SetEnhancements` or `Items.SetLevel` did.
- `GearResult(GearStatus Status, ItemInstanceRef Item, GearState? Before, GearState? After, GearChanges Changed, string Message, int Steps, bool DryRun)`
- `After { get; init; } -> GearState?` — The item after it (a dry run: what it would be; a refusal: as before); null as `GearResult.Before`.
- `Before { get; init; } -> GearState?` — The item before the write; null when it never ran or the item was not found.
- `Changed { get; init; } -> GearChanges` — What changed (a dry run: would change).
- `DryRun { get; init; } -> bool` — Run with `GearWriteOptions.DryRun`: nothing changed.
- `Item { get; init; } -> ItemInstanceRef` — The item.
- `Kept { get; init; } -> bool` — A value the game does not keep through a load (a level outside the type's range with `GearWriteOptions.AnyLevel`, a...
- `Message { get; init; } -> string` — Why, for anything but `GearStatus.Done`.
- `Ok { get; } -> bool` — True when the write was made (a dry run: would be).
- `Reason { get; } -> string?` — Why it did not happen; null when `GearResult.Ok`.
- `Status { get; init; } -> GearStatus` — The outcome.
- `Steps { get; init; } -> int` — Sim steps between the call and the write.

## GearState `0.2.12` — One item's rarity, enhancements, level and durability as the server's world has them now (`Items.GearOf`).
- `GearState(ItemInstanceRef Item, ItemId Type, ItemRarity Rarity, ItemRarity TypeRarity, ItemRarity MaxRarity, int Level, int BaseLevel, int Enhancements, int Unlocked, int MaxLevel, bool HasSocket, int? Durability, int? DurabilityMax, IReadOnlyList<PerkSlot> Perks)`
- `BaseLevel { get; init; } -> int` — `GearState.Level` without the unlocked enhancements: the level it was made at, inside the type's level range.
- `Durability { get; init; } -> int?` — Its durability now; null when it has none.
- `DurabilityMax { get; init; } -> int?` — Its max durability; null when it has none.
- `Enhancements { get; init; } -> int` — How many enhancement slots it has (rarity + 1; 0 when it has none).
- `HasEnhancementSlots { get; init; } -> bool` — The item has enhancement slots at all (the game's PerkContainerNew): only then can its rarity change.
- `HasSocket { get; init; } -> bool` — It has a gem socket (the game's HasWeaponGemSlot item state).
- `IsEquipped { get; init; } -> bool` — A player wears or holds it in an equipment slot.
- `Item { get; init; } -> ItemInstanceRef` — The item.
- `Level { get; init; } -> int` — Its level, the unlocked enhancements included (the game's Level attribute; the shown level when it has none).
- `MaxLevel { get; init; } -> int` — The level it reaches with every slot unlocked (the game's "LEVEL x/max").
- `MaxRarity { get; init; } -> ItemRarity` — The highest rarity its type has a perk slot for: the most `Items.SetRarity` gives without...
- `Perks { get; init; } -> IReadOnlyList<PerkSlot>` — The five enhancement slots.
- `Rarity { get; init; } -> ItemRarity` — Its rarity: its enhancement slots - 1 (the game's itemRarityOf), or the type's rarity when the item has no...
- `ShownLevel { get; init; } -> int` — The level the game derived and sends to players (`ItemState.itemLevelUi`): it follows a change in the next step.
- `ShownRarity { get; init; } -> ItemRarity` — The rarity the game derived and sends to players (`ItemState.itemRarityUi`): it follows a change in the next step.
- `Type { get; init; } -> ItemId` — Its item type.
- `TypeLevelMax { get; init; } -> int` — The highest level of the type's level range (0: none).
- `TypeLevelMin { get; init; } -> int` — The lowest level of the type's level range (0: none).
- `TypeRarity { get; init; } -> ItemRarity` — The rarity its type names (`ItemInfo.rarity`).
- `Unlocked { get; init; } -> int` — How many of them are unlocked.

## GearStatus `0.2.12` — What a gear write did (`GearResult.Status`); the values are kiln.items' own.
enum: Done, Stale, NotGear, NoSlot, OutOfRange, Equipped, Unavailable, Dropped, Refused

## GearUpgradedEvent `0.2.12` — An upgrade happened: it succeeded (`UpgradeStatus.Done`) or its roll failed (`UpgradeStatus.Failed`).
- `Item { get; } -> ItemInstanceRef` — The item (after a won socket, the re-created one).
- `Origin { get; } -> string` — Id of the mod that asked ("kiln" outside mod code).
- `Player { get; } -> EntityId` — The player it ran for.
- `Result { get; } -> UpgradeResult` — What happened.
- `Succeeded { get; } -> bool` — It succeeded.

## GearUpgradingEvent `0.2.12` — An upgrade is about to run (`Items.UpgradeRarity`, from code or a station's interaction), after its checks and before anything is paid or rolled.
- `Chance { get; set; } -> double` — 0 to 1: the chance of success (a value outside is clamped; not a number counts as 0).
- `Cost { get; set; } -> IReadOnlyDictionary<ItemId, int>` — What it costs, by item (already scaled by the item's level; empty: free).
- `From { get; } -> ItemRarity` — Its rarity now.
- `Gear { get; } -> GearState` — The item now.
- `Item { get; } -> ItemInstanceRef` — The item.
- `Origin { get; } -> string` — Id of the mod that asked ("kiln" outside mod code).
- `Player { get; } -> EntityId` — The player it runs for.
- `Rule { get; } -> string?` — The rule's id when it is a block (`"mod:name"`); null for a rule made in code.
- `To { get; } -> ItemRarity` — The rarity the upgrade reaches.

## GearValues `0.2.12` — One item's values beyond its game components (`Items.ValuesOf`).
- `GearValues(ItemInstanceRef Item, double Power, double Armor, int? KeptBaseLevel, int? KeptDurabilityMax, bool Kept)`
- `Armor { get; init; } -> double` — The multiplier of the armour it adds to its wearer (1: its own).
- `Item { get; init; } -> ItemInstanceRef` — The item.
- `Kept { get; init; } -> bool` — The values above are kept with the item through saves and restarts (Kiln's item data is on).
- `KeptBaseLevel { get; init; } -> int?` — A base level outside its type's range that Kiln keeps and writes again after each load...
- `KeptDurabilityMax { get; init; } -> int?` — A max durability Kiln keeps and writes again after each load (`Items.SetDurabilityMax`); null: none.
- `Power { get; init; } -> double` — The multiplier of its power (1: its own).

## GearValuesResult `0.2.12` — What `Items.SetPower` or `Items.SetArmor` did.
- `GearValuesResult(GearValuesStatus Status, ItemInstanceRef Item, double Power, double Armor, bool Kept, string Message)`
- `Armor { get; init; } -> double` — Its armour multiplier now.
- `Item { get; init; } -> ItemInstanceRef` — The item.
- `Kept { get; init; } -> bool` — Kiln keeps the multipliers with the item through saves and restarts (item data).
- `Message { get; init; } -> string` — Why, for anything but `GearValuesStatus.Done`.
- `Ok { get; } -> bool` — True when the value is set.
- `Power { get; init; } -> double` — Its power multiplier now.
- `Reason { get; } -> string?` — Why it did not happen; null when `GearValuesResult.Ok`.
- `Status { get; init; } -> GearValuesStatus` — The outcome.

## GearValuesStatus `0.2.12` — What `Items.SetPower` and `Items.SetArmor` did.
enum: Done, Stale, Full

## GearWriteOptions `0.2.12` — Options of `Items.SetRarity`, `Items.SetEnhancements` and `Items.SetLevel`.
- `AllowEmptySlot { get; init; } -> bool` — Give a rarity whose enhancement slot the item's type has no perk for: the slot exists and can be unlocked (a level)...
- `AnyLevel { get; init; } -> bool` — `Items.SetLevel` only: allow a level whose base (the level without the unlocked enhancements) is outside the item...
- `static Default { get; } -> GearWriteOptions` — The defaults: durability `DurabilityRule.KeepRatio`, no empty slot, equipped items allowed.
- `DryRun { get; init; } -> bool` — Say what would change, change nothing.
- `Durability { get; init; } -> DurabilityRule` — What happens to durability (default `DurabilityRule.KeepRatio`).
- `RefuseEquipped { get; init; } -> bool` — Refuse (`GearStatus.Equipped`) when a player wears or holds the item.

## GiveEquipOptions `0.2.15` — Options of Items.GiveEquip.
- `DryRun { get; init; } -> bool` — Run the transaction and roll it back, creating no item entity and changing nothing.
- `Replaced { get; init; } -> Replaced` — Where the old stack goes; defaults to Backpack.

## GiveOptions — Options of `Items.Give` and the calls like it.
enum: None, Notify, AllOrNothing, NoMerge, DryRun

## InstanceEquipment `0.2.4` — One item instance.
- `InstanceEquipment(ItemInstanceRef Item)`
- `Item { get; init; } -> ItemInstanceRef` — The item.

## InventoryCategory — What a linked inventory is for (InventorySetup link category).
enum: None, Customization, Equipment, Currency, Generic, Virtual

## InventoryContents — An entity's inventory: every slot of every linked 8-slot inventory, in link order.
- `InventoryContents(EntityId Entity, bool Initialized, byte SetupFlags, int GenericSlots, int AvailableSlots, IReadOnlyList<(EntityId Inventory, InventoryCategory Category)> Inventories, IReadOnlyList<InventorySlot> Slots)`
- `AvailableSlots { get; init; } -> int` — The setup's `availableSlotCount`.
- `Count(ItemId item) -> int` — How many of `item` the inventory holds.
- `Entity { get; init; } -> EntityId` — The inventory owner that was read.
- `FreeSlots(InventoryCategory category = InventoryCategory.Generic) -> int` — How many empty slots there are in inventories of `category`.
- `GenericSlots { get; init; } -> int` — The setup's `genericSlotCount`.
- `Initialized { get; init; } -> bool` — True when the game has set up the owner's inventories; false means the other values are not meaningful yet.
- `Inventories { get; init; } -> IReadOnlyList<(EntityId Inventory, InventoryCategory Category)>` — The inventory entities of the setup, in link order, each with its category.
- `IsNpc { get; } -> bool` — True when the setup is flagged as an NPC's.
- `IsPlayer { get; } -> bool` — True when the setup is flagged as a player's.
- `SetupFlags { get; init; } -> byte` — The game's InventorySetup flag byte.
- `Slots { get; init; } -> IReadOnlyList<InventorySlot>` — Every slot of every linked inventory, in link order (empty ones included).
- `UsedSlots { get; } -> int` — How many slots hold an item.

## InventoryKinds — Which kinds of inventory owner `Items.FindInventories` looks for.
enum: Containers, Players, Npcs, NonEmpty, PlayerStorage `0.2.16`, All

## InventoryOwnerInfo `0.2.17` — An inventory owner and its kind (`Items.FindInventoryOwners`).
- `InventoryOwnerInfo()`
- `InventoryOwnerInfo(EntityId Entity, InventoryOwnerKind Kind)`
- `Entity { get; init; } -> EntityId` — The entity that owns the inventory.
- `Kind { get; init; } -> InventoryOwnerKind` — What it is, as far as Kiln can prove it.

## InventoryOwnerKind `0.2.17` — What an inventory owner is, as far as Kiln can prove it (`Items.FindInventoryOwners`).
enum: Unknown, Player, Npc, PlayerStorage, ProductionStation, BaseStorage

## InventorySlot — One slot of an inventory.
- `InventorySlot(SlotId Slot, ItemId Item, int Count, EntityId ItemEntity, int Level, ItemRarity? Rarity, InventoryCategory Category, int Link, bool IsGear, bool IsEquipped, bool IsBroken, ushort ItemState)`
- `Category { get; init; } -> InventoryCategory` — The category of the inventory link the slot sits in.
- `Count { get; init; } -> int` — How many of it are in the slot.
- `IsBroken { get; init; } -> bool` — True when the item is broken (no durability left).
- `IsEmpty { get; } -> bool` — True when the slot holds no item or a count of 0.
- `IsEquipped { get; init; } -> bool` — True when the item is equipped.
- `IsGear { get; init; } -> bool` — True for gear: the slot holds an item entity with a level and a rarity.
- `Item { get; init; } -> ItemId` — The item in the slot; `ItemId.None` when it is empty.
- `ItemEntity { get; init; } -> EntityId` — The item entity of a piece of gear, tool or can; none for a plain stack.
- `ItemState { get; init; } -> ushort` — The game's raw ItemState bits of the item (`itemState`; for example 0x100 is HasWeaponGemSlot).
- `Level { get; init; } -> int` — The item's level; meaningful for gear.
- `Link { get; init; } -> int` — The index of that link in the owner's inventory setup.
- `Rarity { get; init; } -> ItemRarity?` — The item's rarity; null when the game reports none for the slot.
- `Ref { get; init; } -> ItemInstanceRef` — The item in this slot as one particular item, with the stamp kiln.items gave it at this read: equal to the `Ref` of...
- `Slot { get; init; } -> SlotId` — The slot (inventory entity and index).

## InventorySnapshotToken `0.2.4` — An opaque snapshot of a player's inventory that a set change is checked against (kiln.items keeps the snapshot; this is its name).
- `InventorySnapshotToken()`
- `InventorySnapshotToken(ulong Value, ulong WorldEpoch, EntityId Player)`
- `Player { get; init; } -> EntityId` — The player.
- `Value { get; init; } -> ulong` — The token, never 0 for a real snapshot.
- `WorldEpoch { get; init; } -> ulong` — The server world it was taken in.

## InventoryTransferEvent — The game's quick stack or move all ran and kiln.items saw it: posted on the tick thread in the Pre after the step it ran in, while a handler subscribes (otherwise...
- `ByMod { get; } -> bool` — A `Items.QuickStack` or `Items.MoveAll` of a mod, not the game's own caller.
- `Canceled { get; } -> bool` — A rule canceled the game's transfer.
- `From { get; } -> EntityId` — The inventory owner the items left.
- `Kept { get; } -> int` — Stacks hidden from the game's transfer by rules.
- `Kind { get; } -> TransferKind` — Which transfer it was: quick stack or move all.
- `Moved { get; } -> int` — Items the game's transfer moved.
- `Player { get; } -> EntityId` — Whichever of `InventoryTransferEvent.From` and `InventoryTransferEvent.To` is a player's inventory; none if neither.
- `Step { get; } -> ulong` — The sim step it ran in.
- `To { get; } -> EntityId` — The inventory owner they entered.

## ItemCategory — The game's ItemCategory (ItemInfo +0xCC).
enum: Customization, Equipment, Weapons, Tools, Instrument, FishingRod, BuildTools, Consumables, Ammunition, Materials, Blueprints, Currency, Collectibles, AnimalFood, WeaponGem, ColorPalette

## ItemChange — One slot a transaction changed.
- `ItemChange()`
- `ItemChange(SlotId Slot, ItemId Item, int Amount, EntityId ItemEntity)`
- `Amount { get; init; } -> int` — How many were put in (a give) or taken out (a take).
- `Item { get; init; } -> ItemId` — The item put into or taken out of it.
- `ItemEntity { get; init; } -> EntityId` — The item entity of a piece of gear: a give, the one the game created (none in a dry run); a take, the one it destroyed.
- `Ref { get; init; } -> ItemInstanceRef` — The item entity as one particular item, with the stamp kiln.items gave it at the commit (a give: the item the game...
- `Slot { get; init; } -> SlotId` — The slot that changed.

## ItemInfo — An item definition from the server world's item registry.
- `ItemInfo(ItemId Id, string Name, string? DisplayName, int MaxStack, ItemCategory Category, ItemRarity Rarity, bool IsGear, int LevelMin, int LevelMax, byte EquipSlot, uint Flags, Guid Guid)`
- `Category { get; init; } -> ItemCategory` — The game's category of the item.
- `DisplayName { get; init; } -> string?` — The name players see, from the catalog Kiln ships; null when that catalog does not know the item.
- `EquipSlot { get; init; } -> byte` — The game's equipment slot type of the item.
- `Flags { get; init; } -> uint` — The game's raw ItemFlag bits.
- `Guid { get; init; } -> Guid` — The item's GUID in the game's registry.
- `Id { get; init; } -> ItemId` — The item's id, the hash of its `Guid` (see `ItemId.FromGuidBytes`).
- `IsBlueprint { get; } -> bool` — Blueprints and other unlock items (ItemFlag IsBlueprint).
- `IsGear { get; init; } -> bool` — True for gear: an item with a level, a rarity and perks.
- `Label { get; } -> string` — The display name when the shipped catalog knows it, else the game's debug name.
- `LevelMax { get; init; } -> int` — The highest level the item rolls at.
- `LevelMin { get; init; } -> int` — The lowest level the item rolls at.
- `MaxStack { get; init; } -> int` — How many fit in one stack.
- `Name { get; init; } -> string` — The game's debug name, e.g. `Material_T1_Wood_Logs`.
- `Rarity { get; init; } -> ItemRarity` — The item definition's rarity.

## ItemInstanceExtensions — Where an `ItemInstanceRef` is (capability `items.instances`).
- `static Location(this ItemInstanceRef item) -> ItemLocation?` — Where the item is now; null when it is gone or the ref is stale (the id now names another item entity).

## ItemLocation — Where one item sits: the holder whose inventory holds it (a chest, a player, a villager; a chest's first inventory is the chest itself), the slot, the link of the...
- `ItemLocation()`
- `ItemLocation(EntityId Holder, SlotId Slot, int Link, int FlatSlot)`
- `FlatSlot { get; init; } -> int` — 0-255: the slot number the game's world and character save records use (8 per Generic link, counted over the Generic...
- `Holder { get; init; } -> EntityId` — The entity whose inventory setup links the slot's inventory; `EntityId.None` when the item entity sits in no...
- `IsPlaced { get; } -> bool` — True when the item sits in a slot of a holder's inventory.
- `IsSavedSlot { get; } -> bool` — True when `ItemLocation.FlatSlot` is the slot number of the game's save record.
- `Link { get; init; } -> int` — The inventory setup's link (0-15; an inventory nested in a Virtual link: the outer link); -1 when not placed.
- `Slot { get; init; } -> SlotId` — The slot (inventory entity and index).

## ItemMoveCause — How Kiln learned that a followed item moved (`ItemMovedEvent.Cause`).
enum: Transaction, Transfer, Observed

## ItemMovedEvent — A followed item moved: another holder or another slot.
- `Cause { get; } -> ItemMoveCause` — How Kiln learned it.
- `From { get; } -> ItemLocation` — Where it was when Kiln saw it last.
- `Item { get; } -> ItemInstanceRef` — The item (the same entity and stamp before and after).
- `Step { get; } -> ulong` — The step it was reported in.
- `To { get; } -> ItemLocation` — Where it is now.

## ItemRarity — The rarity of an item, from Common (the lowest) to Legendary (the highest).
enum: Common, Uncommon, Rare, Epic, Legendary

## Items — Items in inventories: give and take (stacks and gear with a level and rarity), read an entity's inventory, and the game's item catalog.
- `static All { get; } -> IReadOnlyList<ItemInfo>` — Every item of the server world's registry, sorted by debug name; empty until the world has loaded its items (a few...
- `static ArmorAvailable { get; } -> bool` `0.2.12` — True when `Items.SetArmor` can act (capability `items.gear.armor`).
- `static Available { get; } -> bool` — True when the native service is loaded: capability `kiln.items` (`Capabilities.Why` says why not).
- `static CanTransact { get; } -> bool` — True when give/take can run (the service's program runs and its checks passed).
- `static CaptureSet(EntityId player) -> EquipmentSetCaptureResult` `0.2.4` — Reads all 20 equipment slots of `player` as an `EquipmentSet`: the item worn in each (`InstanceEquipment` for an...
- `static CheckedMovesAvailable { get; } -> bool` `0.2.28` — True when `Items.MoveChecked` works right now: moves work (capability `items.move`, and the kiln_items program runs...
- `static ContainerName(EntityId entity) -> string?` — The name a player gave an object with the Rename button (a chest, a station), or null when it has none.
- `const ContainerNameMaxBytes = 64 -> int` `0.2.13` — The most bytes of UTF-8 `Items.SetContainerName` takes: what the game's Rename field holds (64).
- `const ContainerNameMaxChars = 48 -> int` `0.2.13` — The most characters (Unicode code points) `Items.SetContainerName` takes: the game's Rename field's limit (48).
- `static ContainerNamesAvailable { get; } -> bool` — True when `Items.ContainerName` can read names on this game build (capability `items.names`).
- `static ContainerRenameAvailable { get; } -> bool` `0.2.13` — True when `Items.SetContainerName` can rename here now: capability `items.rename` (the game build's rename is...
- `static Description(ItemId item) -> string?` `0.2.22` — The item's description in the player's game language: `Items.DescriptionIn` with `null`.
- `static DescriptionIn(ItemId item, string? language) -> string?` `0.2.22` — The item's description in `language` without the game's markup: a game item's from the game's own text table of that...
- `static DisplayName(ItemId item) -> string?` `0.2.7` — The item's name for players (since 0.2.0-dev.29.15).
- `static DisplayNameIn(ItemId item, string? language) -> string?` `0.2.22` — The item's name in `language` (a key the game ships, `"de-de"`; null: the player's game language), as...
- `static Equip(EntityId player, SlotId from, EquipmentSlot? slot = null, EquipOptions options = EquipOptions.None, EntityId itemEntity = default(EntityId)) -> Task<EquipResult>` `0.2.2` — Puts an item a player has on: the stack in `from` (a slot of the player's own inventories) goes into the equipment...
- `static Equipped(EntityId player, EquipmentSlot slot) -> ItemInstanceRef` — The item `player` wears or holds in an equipment slot.
- `static EquipSet(EntityId player, EquipmentSet set, EquipOptions options = EquipOptions.None) -> Task<EquipmentSetResult>` `0.2.4` — Changes `player`'s equipment to `set` in one inventory transaction: every slot the set names changes, or none does...
- `static EquipSet(EquipmentSetPlan plan, EquipOptions options = EquipOptions.None) -> Task<EquipmentSetResult>` `0.2.4` — Applies a plan of `Items.PlanSet`: the set it was made for, checked against the snapshot it holds (anything that...
- `static FindInventories(InventoryKinds kinds = InventoryKinds.Containers) -> IReadOnlyList<EntityId>` — Entities with an initialized inventory of these kinds (one pass over the world's entities).
- `static FindInventoryOwners(InventoryKinds kinds = InventoryKinds.Containers) -> IReadOnlyList<InventoryOwnerInfo>` `0.2.17` — The entities with an initialized inventory of these kinds, as `Items.FindInventories` finds them (the same flags...
- `static GearOf(ItemInstanceRef item) -> GearState?` `0.2.12` — One item's rarity, enhancements, level and durability now, as the server's world has them; null when the item is...
- `static GearOptionsAvailable { get; } -> bool` — This build supports `Gear.Unlocked` and `Gear.GemSlot` (capability `items.pide_options`; a give with them is...
- `static GearReadAvailable { get; } -> bool` `0.2.12` — True when `Items.GearOf` can read (capability `items.gear.read`).
- `static GearWriteAvailable { get; } -> bool` `0.2.12` — True when `Items.SetRarity`, `Items.SetEnhancements` and `Items.SetLevel` can write (capability `items.gear.write`).
- `static Get(ItemId id) -> ItemInfo?` — The item with this id, or null.
- `static Give(EntityId target, ItemId item, int count = 1, Gear? gear = null, GiveOptions options = GiveOptions.None, SlotId? slot = null) -> Task<ItemsResult>` — Puts `count` x `item` into `target`'s inventory (a player, chest, villager): stacks are topped up first, then empty...
- `static Give(EntityId target, string item, int count = 1, Gear? gear = null, GiveOptions options = GiveOptions.None) -> Task<ItemsResult>` — Give by name or id (see `Items.Lookup`); an unknown name completes with `ItemsStatus.UnknownItem`.
- `static GiveEquip(EntityId player, ItemId item, int count, EquipmentSlot slot, GiveEquipOptions? options = null) -> Task<EquipResult>` `0.2.15` — Creates one stack straight into an empty or occupied equipment slot, atomically.
- `static GiveEquipAvailable { get; } -> bool` `0.2.15` — Whether give-and-equip can run in this server session.
- `static GiveRecipeResult(EntityId target, uint recipeId, int count = 1, GiveOptions options = GiveOptions.None) -> Task<ItemsResult>` — Creates the outputs of a recipe in `target`'s inventory through the game's own recipe result function, exactly as...
- `static Held(EntityId player) -> ItemInstanceRef` — The item in `player`'s hand: what the running action uses, else the selected action-bar slot (as the game picks it).
- `const InventoryKindsCapability = "items.inventory_kinds" -> string` `0.2.17` — The capability row of `Items.FindInventoryOwners`: the kiln.items.inventory_kinds table is here.
- `static Lookup(string nameOrId) -> ItemInfo?` — Finds an item by id ("0x2b0cf1d5", "722268629"), debug name ("Material_T1_Wood_Logs") or display name ("Wood Logs")...
- `const MaxScale = 100 -> double` `0.2.12` — The highest power or armour multiplier.
- `const MinScale = 0.01 -> double` `0.2.12` — The lowest power or armour multiplier.
- `static Move(EntityId from, SlotId slot, EntityId to, int count = 0, MoveOptions options = MoveOptions.None, SlotId? toSlot = null) -> Task<MoveResult>` — Moves a stack, or part of it, from one inventory to another and keeps its state: a piece of gear arrives with its...
- `static MoveAll(EntityId from, EntityId to, MoveOptions options = MoveOptions.None) -> Task<MoveResult>` — Runs the game's own move all from `from` into `to`: every stack that fits.
- `static MoveChecked(EntityId from, InventorySlot expected, EntityId to, int count, InventorySlot? destination = null) -> Task<MoveResult>` `0.2.28` — Moves `count` items of a slot only while it still holds what a read saw, and, with `destination`, only into exactly...
- `static MovesAvailable { get; } -> bool` — True when moves work right now (capability `items.move`, and the kiln_items program runs in the server world)...
- `static PlanSet(EntityId player, EquipmentSet set) -> EquipmentSetPlanResult` `0.2.4` — Checks `set` against a snapshot of `player`'s inventory taken now and returns the plan: the game's equip and unequip...
- `static PowerAvailable { get; } -> bool` `0.2.12` — True when `Items.SetPower` can act (capability `items.gear.power`).
- `static QuickStack(EntityId from, EntityId to, MoveOptions options = MoveOptions.None) -> Task<MoveResult>` — Runs the game's own quick stack from `from` into `to`: every stack of `from` that `to` already holds a stack of...
- `static RarityOf(ItemInstanceRef item) -> ItemRarity?` `0.2.12` — The item's rarity now (the game's itemRarityOf: its enhancement slots - 1, or its type's rarity when it has none)...
- `static Read(EntityId entity) -> InventoryContents?` — The inventory of `entity`, or null when it has none (or it does not exist).
- `static RegisterPlayerStorage(string key, int slots, StorageFilter filter, StorageDeath death = StorageDeath.Keep) -> PlayerStorage` `0.2.16` — Registers a storage every player of the server world gets: a quiver, a spellbook, an extra bag.
- `static ResultPeekAvailable { get; } -> bool` — The runtime can observe inventory commits before their next-step callbacks.
- `static Search(string text, int max = 25) -> IReadOnlyList<ItemInfo>` — Items whose debug or display name contains `text` (ignoring case).
- `static SetArmor(ItemInstanceRef item, double scale) -> GearValuesResult` `0.2.12` — Multiplies the armour one worn item adds to its wearer (its type's armour at its level and that of its unlocked...
- `static SetContainerName(EntityId entity, string name) -> Task<bool>` `0.2.13` — Renames a container the way a player's Rename button does, through the game's own rename on the server: the name...
- `static SetDurabilityMax(ItemInstanceRef item, int? max, GearWriteOptions? options = null) -> Task<GearResult>` `0.2.12` — Sets one item's max durability in place, keeping its current durability at the same share (a half-worn item stays...
- `static SetEnhancements(ItemInstanceRef item, int unlocked, GearWriteOptions? options = null) -> Task<GearResult>` `0.2.12` — Sets how many of an item's enhancement slots are unlocked, as the workbench does without its cost: each unlocked...
- `static SetLevel(ItemInstanceRef item, int level, GearWriteOptions? options = null) -> Task<GearResult>` `0.2.12` — Sets an item's level, the unlocked enhancements included (the level the game shows).
- `static SetPower(ItemInstanceRef item, double scale) -> GearValuesResult` `0.2.12` — Multiplies one item's power: the damage of its swings, shots and casts (the game computes an item's power when an...
- `static SetRarity(ItemInstanceRef item, ItemRarity rarity, GearWriteOptions? options = null) -> Task<GearResult>` `0.2.12` — Sets an item's rarity in place: the same item entity, its item data and affixes stay on it.
- `static SetToolWearPerDebit(ItemId tool, int perDebit) -> ToolWearResult` `0.2.3` — Sets what each of the game's debits costs `tool`: a hit that debits the weapon, a projectile spawned with it.
- `static Status() -> ItemsServiceStatus?` — The service's state, or null when it is not loaded.
- `static Status3() -> ItemsV3Status?` — The v3 parts' state (moves, names, transfer hooks, counters), or null on an older runtime.
- `static Take(EntityId target, ItemId item, int count = 1, SlotId? from = null, bool exactSlot = false, EntityId? itemEntity = null, bool dryRun = false) -> Task<ItemsResult>` — Takes `count` x `item` out of `target`'s inventory, all or nothing.
- `static TakeSlot(EntityId target, InventorySlot slot, bool dryRun = false) -> Task<ItemsResult>` — Takes everything in one slot as read (exact slot, the same item entity for gear).
- `static TextTags(ItemId item) -> ItemTextTags?` `0.2.22` — The loca tag ids of a game item type's texts (name, caption, description, lore), read from its `ItemInfo`: look them...
- `static ToolWearStatus(ItemId tool) -> ToolWearState` `0.2.3` — What one debit of `tool` costs now, whose rule that is, and whether a rule can be set here.
- `static TryGetResult(Task<ItemsResult> transaction, out ItemsResult? result) -> bool` — Observes the result of a Give/Take task, including an inventory commit whose normal callback has not yet run.
- `static Unequip(EntityId player, EquipmentSlot slot, EquipOptions options = EquipOptions.None) -> Task<EquipResult>` `0.2.2` — Takes off what `player` wears or holds in `slot`: the game's own unequip moves it into a free slot of the backpack.
- `static UpgradeAvailable { get; } -> bool` `0.2.12` — True when `Items.UpgradeRarity` can run (capability `items.gear.upgrade`).
- `static UpgradeRarity(EntityId player, ItemInstanceRef item, RarityUpgradeRule rule, UpgradeOptions? options = null) -> Task<UpgradeResult>` `0.2.12` — Raises an item's rarity by one step of `rule`, in place: the item gets its next enhancement slot (locked: the player...
- `static UpgradeRarity(EntityId player, ItemInstanceRef item, string rule, UpgradeOptions? options = null) -> Task<UpgradeResult>` `0.2.12` — As `Items.UpgradeRarity`, with a rule a mod defined as a block (`RarityUpgradeBlock`, `rarityUpgrades` in...
- `static ValuesOf(ItemInstanceRef item) -> GearValues?` `0.2.12` — One item's power and armour multipliers and the values Kiln keeps for it through loads; null when the item is gone...

## Items.Fuel — Water in containers: watering cans, water pouches, the water pump, barrels and wells.
- `static Add(EntityId container, int units, bool dryRun = false) -> Task<FuelResult>` — Adds `units` (negative: takes) to `container`'s water.
- `static Available { get; } -> bool` — True when the native service has the water functions and they resolved on this game build (capability `items.fuel`)...
- `static Fill(EntityId container, bool dryRun = false) -> Task<FuelResult>` — Fills `container` to its max.
- `static FillAll(EntityId holder, ItemId item = default(ItemId), bool dryRun = false) -> Task<FuelResult>` — Fills every water container in `holder`'s inventories (a player's backpack, action bars and equipment, a chest)...
- `static FillAll(EntityId holder, IEnumerable<ItemId> items, bool dryRun = false) -> Task<FuelResult>` — Fills every water container of the given items in `holder`'s inventories, one request per item (an empty list: every...
- `static KeepFull(EntityId entity, ItemId item = default(ItemId), bool inventories = true) -> FuelKeep` — Keeps water containers full (`Fuel.Refill` with `FuelRefill.Full`).
- `static Read(EntityId container) -> FuelLevel?` — The water `container` holds (an item entity, a barrel, a well), or null when it holds none.
- `static Refill(EntityId entity, FuelRefill policy, ItemId item = default(ItemId), bool inventories = true) -> FuelKeep` — A refill policy: every sim step, after the game's own fill and pour, the containers get water back as `policy` says...
- `static Set(EntityId container, int units, bool dryRun = false) -> Task<FuelResult>` — Sets `container`'s water to `units` (clamped to 0..max).
- `static SetAll(EntityId holder, int units, ItemId item = default(ItemId), bool dryRun = false) -> Task<FuelResult>` — Sets every water container in `holder`'s inventories to `units` (each clamped to its max), only those of `item` when...
- `const UnitsPerCharge = 64 -> int` — Units per charge (the game's `waterChargeAmount`).

## Items.PlayerStorages `0.2.16` — Every mod's player storages: what a player has (registered and parked) and the return of parked items.
- `static Of(EntityId player) -> IReadOnlyList<PlayerStorageInfo>` — Every storage key of `player`: the registered ones (any state) and those whose items wait parked, sorted by key...
- `static ReturnOrphans(EntityId player, string fullKey, OrphanTarget target) -> Task<MoveResult>` — Puts every parked item of storage `fullKey` back into the game: into `player`'s backpack (as far as there is room)...

## Items.Transfers — Rules and events for the game's quick stack and move all: what a player does with the R key or a Quick Stack Station, what `Items.QuickStack` and `Items.MoveAll` do...
- `static Available { get; } -> bool` — True when the hooks can be installed on this game build (capability `items.transfer`).
- `static Deny(EntityId container, TransferKinds kinds = TransferKinds.Any) -> TransferRule` — A transfer into or out of `container` does not run (the caller is told it moved nothing).
- `static Keep(SlotId slot, TransferKinds kinds = TransferKinds.Any) -> TransferRule` — The stack in `slot` stays where it is while it lies in the source inventory of a transfer (a locked slot).
- `static KeepItem(ItemId item, EntityId owner = default(EntityId), TransferKinds kinds = TransferKinds.Any) -> TransferRule` — Stacks of `item` stay in the source inventory of a transfer; with `owner` only when that entity's inventory is the...
- `static ParseAction(ReadOnlySpan<byte> payload) -> ClientTransfer?` — Reads the payload of a client's `inventoryTransferAction` (32 bytes: the 4-byte version, source and target entity...
- `static Status() -> ItemsV3Status?` — The hooks' and rules' state, counters included.

## ItemsGivenEvent — Kiln.Items gave items (see `ItemsTransactionEvent`).

## ItemsResult — What a give or take did.
- `ItemsResult(ItemsStatus Status, EntityId Entity, ItemId Item, int Requested, int Done, IReadOnlyList<ItemChange> Changes, string Message, int GameError, int Steps, bool DryRun)`
- `Changes { get; init; } -> IReadOnlyList<ItemChange>` — The slots it changed, with the amount in each.
- `Done { get; init; } -> int` — How many were given or taken (a dry run: how many would be).
- `DryRun { get; init; } -> bool` — True when it was a dry run: nothing changed, the result says what would have.
- `Entity { get; init; } -> EntityId` — The inventory owner (player, chest, NPC) it ran on.
- `GameError { get; init; } -> int` — The game's own error code when its transaction returned one (`ItemsStatus.Refused`); 0 otherwise.
- `Item { get; init; } -> ItemId` — The item asked for.
- `Message { get; init; } -> string` — Why it failed or fell short, in words; empty when there is nothing to add.
- `Requested { get; init; } -> int` — How many were asked for.
- `Status { get; init; } -> ItemsStatus` — How it ended.
- `Steps { get; init; } -> int` — How many sim steps passed between the request and the transaction.
- `Succeeded { get; } -> bool` — Something was given / taken (or would be, in a dry run).

## ItemsServiceStatus — State of the kiln.items service.
- `ItemsServiceStatus(bool Transactions, bool Read, bool Catalog, int Program, int Pending, int Items, ulong Committed, ulong RolledBack, ulong ProgramUpdates, string Detail)`
- `Catalog { get; init; } -> bool` — True when the item catalog is loaded.
- `Committed { get; init; } -> ulong` — Transactions committed so far.
- `Detail { get; init; } -> string` — The reason when `ItemsServiceStatus.Transactions` is false; empty otherwise.
- `Items { get; init; } -> int` — How many items the catalog holds.
- `Pending { get; init; } -> int` — Requests waiting for the program.
- `Program { get; init; } -> int` — The state of the kiln.items program in the server world: 0 not registered, 1 waiting for the world, 2 registered but...
- `ProgramUpdates { get; init; } -> ulong` — How many times the program has run.
- `Read { get; init; } -> bool` — True when `Items.Read` and `Items.FindInventories` work.
- `RolledBack { get; init; } -> ulong` — Transactions rolled back so far.
- `Transactions { get; init; } -> bool` — True when give and take can run.

## ItemsStatus — How a give, take, move or water write ended.
enum: Done, Partial, NoRoom, NotEnough, NoInventory, UnknownItem, SlotChanged, Refused, Unavailable, Dropped, Filtered `0.2.16`, Invalid

## ItemsTakenEvent — Kiln.Items took items (see `ItemsTransactionEvent`).

## ItemsTransactionEvent — Base of `ItemsGivenEvent` and `ItemsTakenEvent`: a Kiln.Items transaction finished with something moved (Done or Partial, not a dry run).
- `Count { get; } -> int` — How many were given / taken.
- `Info { get; } -> ItemInfo?` — The item from the catalog (null when unknown).
- `Item { get; } -> ItemId` — The item that was given or taken.
- `Origin { get; } -> string` — Id of the mod that asked ("kiln" outside mod code).
- `Result { get; } -> ItemsResult` — The result of the transaction.
- `Target { get; } -> EntityId` — The inventory owner (player, chest, NPC).

## ItemsV3Status — State of kiln.items' v3 parts.
- `ItemsV3Status(bool Moves, bool Names, bool TransferHooks, bool HooksInstalled, int Rules, int Handlers, int Reporters, ulong TransfersSeen, ulong TransfersCanceled, ulong MovesDone, int NamesFound, string Detail)`
- `Detail { get; init; } -> string` — The reason when a part is off; empty otherwise.
- `Handlers { get; init; } -> int` — Active native transfer handlers.
- `HooksInstalled { get; init; } -> bool` — True when they are installed (a rule or handler was registered once).
- `Moves { get; init; } -> bool` — True when `Items.Move`, `Items.QuickStack` and `Items.MoveAll` work.
- `MovesDone { get; init; } -> ulong` — Move requests that moved something.
- `Names { get; init; } -> bool` — True when `Items.ContainerName` works on this game build.
- `NamesFound { get; init; } -> int` — Container name lookups that found a name.
- `Reporters { get; init; } -> int` — Active listeners of transfer reports.
- `Rules { get; init; } -> int` — Active transfer rules.
- `TransferHooks { get; init; } -> bool` — True when the quick stack and move all hooks can be installed.
- `TransfersCanceled { get; init; } -> ulong` — How many of those a rule or handler canceled.
- `TransfersSeen { get; init; } -> ulong` — Calls of the game's quick stack and move all since the hooks went in.

## ItemTextTags `0.2.22` — The loca tag ids of an item type's texts: FNV-1a-32 of the `keen::ItemInfo` LocaTag GUIDs (the ids `Texts.TagOf` and the game's tables use), 0 where the item has none.
- `ItemTextTags()`
- `ItemTextTags(uint Name, uint Caption, uint Description, uint Lore)`
- `Caption { get; init; } -> uint` — The line under the name (`ItemInfo.caption`).
- `Description { get; init; } -> uint` — The description (`ItemInfo.description`).
- `Lore { get; init; } -> uint` — The lore text (`ItemInfo.lore`).
- `Name { get; init; } -> uint` — The name (`ItemInfo.name`).

## MoveChange — One piece of a move's ledger: `MoveChange.Amount` of `MoveChange.Item` left `MoveChange.From` and arrived in `MoveChange.To`.
- `MoveChange()`
- `MoveChange(SlotId From, SlotId To, ItemId Item, int Amount, EntityId ItemEntity)`
- `Amount { get; init; } -> int` — How many moved.
- `From { get; init; } -> SlotId` — The slot the items left.
- `Item { get; init; } -> ItemId` — The item that moved.
- `ItemEntity { get; init; } -> EntityId` — The item entity of a piece of gear that moved whole; none for a plain stack.
- `Ref { get; init; } -> ItemInstanceRef` — The item entity that moved, as one particular item: it moves whole, stamp and all, so this equals its `Ref` in a...
- `To { get; init; } -> SlotId` — The slot they arrived in.

## MoveKind — What a mod can move: one stack, the game's quick stack, or its move all.
enum: Stack, QuickStack, All

## MoveOptions — Options of a move (`Items.Move`, `Items.QuickStack`, `Items.MoveAll`).
enum: None, DryRun, Equipped, ActionBar, Silent

## MoveResult — What a move did.
- `MoveResult(ItemsStatus Status, MoveKind Kind, EntityId From, EntityId To, int Requested, int Done, int Stacks, IReadOnlyList<MoveChange> Changes, string Message, int GameError, bool Canceled, int Steps, bool DryRun)`
- `Canceled { get; init; } -> bool` — True when a transfer rule or handler canceled the game's quick stack or move all.
- `Changes { get; init; } -> IReadOnlyList<MoveChange>` — The moves, slot by slot (the first ones only; `MoveResult.Done` counts them all).
- `Done { get; init; } -> int` — How many items moved (a dry run: how many would).
- `DryRun { get; init; } -> bool` — True when nothing moved: the result says what would.
- `From { get; init; } -> EntityId` — The inventory owner the items left.
- `GameError { get; init; } -> int` — The game's own error code when it refused; 0 otherwise.
- `Kind { get; init; } -> MoveKind` — Which move it was.
- `Message { get; init; } -> string` — Why it failed or fell short, in words; empty when it went through.
- `Requested { get; init; } -> int` — A stack move: the count asked for (the stack's size when none was given).
- `Stacks { get; init; } -> int` — How many source stacks gave something.
- `Status { get; init; } -> ItemsStatus` — How the move ended; see the type's summary.
- `Steps { get; init; } -> int` — How many sim steps passed between the request and the move.
- `Succeeded { get; } -> bool` — Something moved (or would, in a dry run).
- `To { get; init; } -> EntityId` — The inventory owner they went to.

## OrphanTarget `0.2.16` — Where `PlayerStorages.ReturnOrphans` puts parked items.
- `static Backpack { get; } -> OrphanTarget` — The player's backpack (as far as there is room).
- `Container { get; } -> EntityId` — The container; none for the backpack.
- `Equals(OrphanTarget other) -> bool`
- `static Into(EntityId container) -> OrphanTarget` — An inventory owner: a chest, a station.
- `IsBackpack { get; } -> bool` — The player's backpack.
- `static operator !=(OrphanTarget left, OrphanTarget right) -> bool`
- `static operator ==(OrphanTarget left, OrphanTarget right) -> bool`

## PerkSlot `0.2.12` — One of an item's five enhancement slots (the game's perk slots: one per rarity, Common's first).
- `PerkSlot(int Index, bool Available, bool Unlocked, bool TypeHasSlot)`
- `Available { get; init; } -> bool` — The item has this slot now (its rarity reaches it): the player can unlock it at the workbench.
- `Index { get; init; } -> int` — 0 to 4: the slot of the rarity with this value (0 Common ..
- `TypeHasSlot { get; init; } -> bool` — The item's type has a perk up to this slot (the slot is at or below the type's last filled perk slot; a rare gap...
- `Unlocked { get; init; } -> bool` — The slot is unlocked: its perk acts, and it counts one level.

## PlayerEquippingEvent `0.2.4` — A player is about to change equipment: one slot (`SingleEquipmentChange`), the game's collection swap (`CollectionEquipmentChange`), or a Kiln set (`SetEquipmentChange`).
- `PlayerEquippingEvent(DecisionId id, EventEntityKey player, EquipmentChange change, bool priorCanceled, string? priorCanceledBy)` — Makes the event (Kiln posts it; a mod has no reason to).
- `Change { get; } -> EquipmentChange` — What would change.
- `Id { get; } -> DecisionId` — The decision; the same id is on the input's `PlayerClientActionEvent` and on its `DecisionCompletedEvent`.
- `Involves(EntityId entity) -> bool` — Whether the event is about `entity`: the player, the item instance or the inventory a selection comes from, the...
- `Player { get; } -> EntityId` — The player.
- `PlayerKey { get; } -> EventEntityKey` — Which life of the player.
- `PriorCanceled { get; } -> bool` — True when a `PlayerClientActionEvent` handler of the same input canceled it already.
- `PriorCanceledBy { get; } -> string?` — The mod that canceled it there; null when none did.

## PlayerStorage `0.2.16` — One storage a mod registered (`Items.RegisterPlayerStorage`): every player of the server world has one.
- `Activate(EntityId player, int index) -> Task<EquipResult>` — Makes the arrows or spell in slot `index` the player's active ammunition through the backpack: the stack moves into...
- `static Available { get; } -> bool` — Whether player storage works on this game build (capability `items.storage`)...
- `const Capability = "items.storage" -> string` — The capability row of player storage: `items.storage`.
- `Death { get; } -> StorageDeath` — What happens to its items when the player dies.
- `EntityOf(EntityId player) -> EntityId` — The storage's inventory owner for `player` (use it with Kiln.Items' calls), or none while it is not ready.
- `Filter { get; } -> StorageFilter` — What it takes.
- `FullKey { get; } -> string` — "\<mod id>:\<key>": the storage's name in saves, logs and `PlayerStorages`.
- `IsReady(EntityId player) -> bool` — The storage is restored for `player` and takes items.
- `Key { get; } -> string` — The key the mod gave.
- `Read(EntityId player) -> InventoryContents?` — What the storage holds for `player` (`Items.Read` of `PlayerStorage.EntityOf`), or null while it is not ready.
- `Retrieve(EntityId player, int index, int count = 0, EntityId to = default(EntityId)) -> Task<MoveResult>` — Moves the stack in slot `index` (or `count` of it) out of the storage into `player`'s backpack, or into `to` (a...
- `Slot(EntityId player, int index) -> SlotId` — Slot `index` (0 to `PlayerStorage.Slots` - 1) of the storage for `player`, or none while it is not ready.
- `Slots { get; } -> int` — How many slots it has (1 to 64).
- `Status(EntityId player) -> PlayerStorageStatus` — Where the storage stands for `player`.
- `Store(EntityId player, SlotId from, int count = 0) -> Task<MoveResult>` — Moves a stack (or `count` of it) from `player`'s inventory into the storage: `Items.Move`, with Kiln's choice of slot.

## PlayerStorage.Client `0.2.16` — The player's own read-only copy of their storages on their game: rows the server sends the storage's owner (never another player) when a storage changes, so a mod's...
- `static Available { get; } -> bool` — Whether the client copy works here (capability `items.storage.client`: Kiln's network channel is open).
- `const Capability = "items.storage.client" -> string` — The capability row of the client copy: `items.storage.client`.
- `static ProvideAmmo(string fullKey, AmmoKinds kinds, PlayerStorageAmmoOptions? options = null) -> PlayerStorageAmmoProvider` — Shows the arrows and spells of your mod's storage `fullKey` in the HUD's ammunition and spell lists (`Ui.AmmoList`...
- `static Rows(string fullKey) -> IReadOnlyList<PlayerStorageRow>` — The local player's rows of storage `fullKey` (`PlayerStorage.FullKey`), by slot: one per slot that holds something.

## PlayerStorageAmmoOptions `0.2.16` — How `Client.ProvideAmmo` shows a storage's ammunition and what a pick does.
- `Activate { get; init; } -> bool` — A pick swaps the chosen stack into the backpack and equips it there (`PlayerStorage.Activate`), instead of equipping...
- `Replace { get; init; } -> bool` — The storage's rows instead of the game's own rows (the HUD list's Replace mode); default false: after the game's...

## PlayerStorageAmmoProvider `0.2.16` — A storage's ammunition in the HUD's lists and the server half that takes its picks (`Client.ProvideAmmo`).
- `Active { get; } -> bool` — False once disposed.
- `Dispose() -> void` — Ends both halves: the rows leave the HUD at the next UI frame, and this server takes no more picks of the storage.
- `FullKey { get; } -> string` — The storage's full key.
- `Kinds { get; } -> AmmoKinds` — The lists it provides for.
- `Options { get; } -> PlayerStorageAmmoOptions` — How it shows and what a pick does.
- `Shown { get; } -> bool` — This game shows the storage's rows in the HUD's lists (a game client where `ui.ammo.list` is on).
- `WhyNotShown { get; } -> string?` — Why the rows are not shown in this process (a dedicated server has no HUD; `ui.ammo.list` is off); null when they are.

## PlayerStorageClientChangedEvent `0.2.16` — The local player's client copy of a storage changed (`Client`): the server sent other rows, the storage went (its rows are then empty), or the game left the world.
- `Key { get; } -> string` — The storage's full key.
- `Rows { get; } -> IReadOnlyList<PlayerStorageRow>` — Its rows now.

## PlayerStorageDeathEvent `0.2.16` — A player died: what Kiln did with a storage's items (`StorageDeath`).
- `Items { get; } -> int` — Their items together.
- `Key { get; } -> string` — The storage's full key.
- `Moved { get; } -> int` — Stacks moved into the tombstone (0: kept).
- `Player { get; } -> EntityId` — The player.
- `Rule { get; } -> StorageDeath` — The storage's death rule.
- `To { get; } -> EntityId` — The tombstone; none when the storage kept everything.

## PlayerStorageInfo `0.2.16` — One key of a player's storages, any mod's (`PlayerStorages.Of`).
- `PlayerStorageInfo(string FullKey, bool Registered, int Slots, EntityId Holder, PlayerStorageStatus Status)`
- `FullKey { get; init; } -> string` — "\<mod id>:\<name>".
- `Holder { get; init; } -> EntityId` — The storage's inventory (none until restored).
- `Registered { get; init; } -> bool` — A loaded mod registered it.
- `Slots { get; init; } -> int` — The registration's slots (0 when unregistered).
- `Status { get; init; } -> PlayerStorageStatus` — Where it stands.

## PlayerStorageParkedEvent `0.2.16` — Items of a player's storage were parked: they wait, unchanged, in Kiln's copies (`PlayerStorageParkedEvent.Reason`).
- `Character { get; } -> Guid` — The game's CharacterId of the character.
- `Key { get; } -> string` — The storage's full key.
- `Player { get; } -> EntityId` — The player.
- `Reason { get; } -> PlayerStorageParkReason` — Why.
- `Stacks { get; } -> int` — Stacks parked.

## PlayerStorageParkReason `0.2.16` — Why a storage's items wait parked.
enum: Unregistered, Overflow, Filter, Requested, Stale, Locked, ReturnedOutsideKiln

## PlayerStorageRestoredEvent `0.2.16` — Kiln restored a player's storage of one key at the join (or when a mod registered it while the player was on): it is ready now.
- `Character { get; } -> Guid` — The game's CharacterId of the character.
- `Key { get; } -> string` — The storage's full key.
- `Live { get; } -> bool` — A registration while the player was on (not the join).
- `Paired { get; } -> bool` — The copy was saved together with exactly this character (see `PlayerStorageStatus.Paired`).
- `Player { get; } -> EntityId` — The player.
- `Revision { get; } -> long` — The copy's revision.
- `Source { get; } -> PlayerStorageSource` — The copy it came from.
- `Stacks { get; } -> int` — Stacks put back.

## PlayerStorageRow `0.2.16` — One slot of a storage in the client copy (`Client.Rows`): a picture of what the server holds, never authority.
- `PlayerStorageRow()`
- `PlayerStorageRow(string Key, int Index, ItemId Item, int Count, int Level, uint Flags)`
- `Count { get; init; } -> int` — How many (at least 1).
- `Flags { get; init; } -> uint` — The item's `AmmoKinds` as the server's item catalog has it: 1 (`AmmoKinds.Arrow`) for arrows, 2 (`AmmoKinds.Spell`)...
- `Index { get; init; } -> int` — The slot (0 to slots - 1), as `PlayerStorage.Slot` counts them.
- `Item { get; init; } -> ItemId` — The item.
- `Key { get; init; } -> string` — The storage's full key.
- `Level { get; init; } -> int` — Gear's level (0 for a plain stack).

## PlayerStorageSource `0.2.16` — Which copy a player's storages were restored from.
enum: None, New, Server, Client

## PlayerStorageState `0.2.16` — Where a player's storage stands.
enum: None, Waiting, Ready, Parked, Locked

## PlayerStorageStatus `0.2.16` — One player's storage of one key (`PlayerStorage.Status`).
- `PlayerStorageStatus(PlayerStorageState State, bool Paired, PlayerStorageSource Source, int Stacks, int Parked, int Quarantined, long Revision)`
- `Paired { get; init; } -> bool` — The restored copy was saved together with exactly the character the player brought (the safe case); false: Kiln took...
- `Parked { get; init; } -> int` — Records of this key waiting parked.
- `Quarantined { get; init; } -> int` — Records of this key Kiln could not use (an unknown item, a bad count or slot); kept, never deleted.
- `Restored { get; } -> bool` — The storage is restored and takes items.
- `Revision { get; init; } -> long` — The restored copy's revision.
- `Source { get; init; } -> PlayerStorageSource` — The copy it was restored from.
- `Stacks { get; init; } -> int` — Stacks in the storage now.
- `State { get; init; } -> PlayerStorageState` — Where it stands.

## RarityUpgradeRule `0.2.12` — The rule of a rarity upgrade: which items it takes, up to which rarity, and what each step costs.
- `AllowEmptySlot { get; init; } -> bool` — Give a rarity whose enhancement slot the item's type has no perk for (the slot exists and can be unlocked for a...
- `Durability { get; init; } -> DurabilityRule` — What happens to the item's durability (default `DurabilityRule.KeepRatio`: the game computes max durability again at...
- `Items { get; init; } -> IReadOnlyList<string>?` — The item types it takes: game names, `"other-mod:name"`, an item of the rule's mod by block name, or a pattern with...
- `Max { get; init; } -> ItemRarity` — The highest rarity it upgrades to (default Legendary, the game's highest).
- `Socket { get; init; } -> SocketRule` — What happens about a gem socket (default `SocketRule.Keep`).
- `Steps { get; init; } -> IDictionary<ItemRarity, UpgradeStep>` — The steps, by the rarity each reaches (Uncommon ..

## Replaced `0.2.15` — Where GiveEquip puts the equipment slot's previous stack.
enum: Backpack, Take

## SetEquipmentChange `0.2.4` — A Kiln set request (chapter 10): one decision for the whole set, never one per slot.
- `SetEquipmentChange(ulong ItemsTicket, InventorySnapshotToken Snapshot, EquipmentSet Set, bool DryRun)`
- `DryRun { get; init; } -> bool` — True: only checked, nothing is changed.
- `ItemsTicket { get; init; } -> ulong` — kiln.items' ticket of the request.
- `Set { get; init; } -> EquipmentSet` — The set.
- `Snapshot { get; init; } -> InventorySnapshotToken` — The snapshot the set is checked against.

## SingleEquipmentChange `0.2.4` — One slot: an equip (`SingleEquipmentChange.Selection` is what goes in) or an unequip (`SingleEquipmentChange.Selection` is what is in the slot now; `EmptyEquipment`...
- `SingleEquipmentChange(EquipmentChangeKind Operation, EquipmentSlot Slot, EquipmentSetSelection Selection)`
- `Operation { get; init; } -> EquipmentChangeKind` — `EquipmentChangeKind.Equip` or `EquipmentChangeKind.Unequip`.
- `Selection { get; init; } -> EquipmentSetSelection` — The item, read in the Pre of the event.
- `Slot { get; init; } -> EquipmentSlot` — The equipment slot.

## SlotId — One slot of an 8-slot inventory: the inventory entity and the slot index 0-7 (the game's InventorySlotId).
- `SlotId()`
- `SlotId(ulong Value)`
- `Index { get; } -> int` — The slot's index in its inventory, 0-7.
- `Inventory { get; } -> EntityId` — The inventory entity this slot belongs to.
- `IsNone { get; } -> bool` — True for the default value, which names no slot.
- `static Of(EntityId inventory, int index) -> SlotId` — The slot `index` (0-7) of the inventory entity `inventory`.
- `Value { get; init; } -> ulong` — The packed id: the inventory entity's id in the low 32 bits and the slot index (0-7) from bit 32.

## SocketRule `0.2.12` — What an upgrade does about a gem socket (`RarityUpgradeRule.Socket`).
enum: Keep, GameChance

## StackEquipment `0.2.4` — A plain stack (no item entity): the item type, the inventory slot it is in, and how many it held when the decision was made.
- `StackEquipment(ItemId Item, SlotId Source, int ExpectedCount)`
- `ExpectedCount { get; init; } -> int` — The count read in Pre.
- `Item { get; init; } -> ItemId` — The item type.
- `Source { get; init; } -> SlotId` — The inventory slot of the stack.

## StorageDeath `0.2.16` — What happens to a storage's items when its player dies.
enum: Keep, Grave, FollowGame

## StorageFilter `0.2.16` — What items a player storage takes.
- `static Ammunition(AmmoKinds kinds) -> StorageFilter` — Ammunition of these kinds (what the game equips into the arrow or spell slot).
- `And(StorageFilter other) -> StorageFilter` — Only items both filters take.
- `static Any { get; } -> StorageFilter` — Any item.
- `static Categories(params ItemCategory[] categories) -> StorageFilter` — Items of these categories.
- `Equals(StorageFilter other) -> bool`
- `static Items(params ItemId[] items) -> StorageFilter` — Exactly these items.
- `NoItemEntities() -> StorageFilter` — The same filter, taking only items without an item entity: plain stackables, no gear.
- `static operator !=(StorageFilter left, StorageFilter right) -> bool`
- `static operator ==(StorageFilter left, StorageFilter right) -> bool`
- `static Where(Func<ItemInfo, bool> pick) -> StorageFilter` — Items `pick` says yes to, asked once per catalog item (see the remarks of `StorageFilter`): keep it a pure function...

## ToolDurabilityChangedEvent `0.2.3` — A tool with a rule lost durability to one of the game's debits.
- `Actor { get; } -> EntityId` — Whose action it was.
- `After { get; } -> int` — The durability after.
- `Before { get; } -> int` — The durability before.
- `Item { get; } -> ItemInstanceRef` — The tool: the item entity as it was when the debit happened.
- `Maximum { get; } -> int` — The tool's full durability.
- `OriginalDelta { get; } -> int` — What the game asked for: -1.
- `RequestedDelta { get; } -> int` — What the rule asked for: minus its cost.
- `Source { get; } -> ToolWearSource` — Which debit.
- `Step { get; } -> ulong` — The server step of the debit.

## ToolWearResult `0.2.3` — What `Items.SetToolWearPerDebit` answers.
- `Ok { get; init; } -> bool` — The rule is set.
- `Reason { get; init; } -> string` — Empty when set; else why not: `UnsupportedTool: ...` (a torch, a build tool, another kind of item)...
- `Rule { get; init; } -> ToolWearRule?` — The rule, to dispose.

## ToolWearRule `0.2.3` — A mod's rule for what one debit of a tool costs.
- `Dispose() -> void` — Takes the rule away.
- `Item { get; } -> ItemId` — The tool the rule is for.

## ToolWearRuleChangedEvent `0.2.3` — The rule in effect for a tool changed: set, replaced, taken away, or another mod's rule revealed.
- `After { get; } -> int?` — What it costs now.
- `AfterModId { get; } -> string?` — The mod whose rule is in effect now.
- `Before { get; } -> int?` — What a debit cost before.
- `BeforeModId { get; } -> string?` — The mod whose rule was in effect.
- `Generation { get; } -> ulong` — `ToolWearState.Generation` after the change: one more than the change before it, unless more than 1024 changes came...
- `Item { get; } -> ItemId` — The tool.

## ToolWearSource `0.2.3` — Which of the game's debits changed a tool's durability.
enum: WeaponHit, ProjectileSpawn

## ToolWearState `0.2.3` — What `Items.ToolWearStatus` answers: a copy that does not change.
- `DroppedEvents { get; init; } -> ulong` — Durability changes that were not reported because too many came at once, since the game started.
- `Generation { get; init; } -> ulong` — Grows by one with every change of any tool's rule in effect.
- `Ok { get; init; } -> bool` — A rule can be set for the tool here.
- `OwnerModId { get; init; } -> string?` — The mod whose rule is in effect.
- `Pending { get; init; } -> bool` — The game's items are not read yet: ask again.
- `PerDebit { get; init; } -> int?` — What one debit costs under the rule in effect.
- `Reason { get; init; } -> string` — Empty when `ToolWearState.Ok`; else why not (as `ToolWearResult.Reason`).

## TransferKind — Which of the game's two inventory-wide transfers.
enum: QuickStack, MoveAll

## TransferKinds — Which of the game's inventory-wide transfers a rule applies to.
enum: QuickStack, MoveAll, Any

## TransferRule — A transfer rule (`Transfers`); dispose it to end it.
- `Dispose() -> void` — Ends the rule.
- `IsActive { get; } -> bool` — True until the rule is disposed.
- `What { get; } -> string` — What the rule does, for logs.

## UpgradeFail `0.2.12` — What a failed roll of an upgrade costs (`UpgradeStep.OnFail`).
enum: KeepItemLoseCost, KeepItemKeepCost, Downgrade

## UpgradeOptions `0.2.12` — Options of one `Items.UpgradeRarity`.
- `static Default { get; } -> UpgradeOptions` — The defaults: the player pays, the rule's costs and chances.
- `DryRun { get; init; } -> bool` — Check, count and plan; pay and change nothing, roll nothing (`UpgradeResult.Rolled` null).
- `Free { get; init; } -> bool` — Nothing is paid (a quest reward, a creative tool); the chance still applies.
- `Guaranteed { get; init; } -> bool` — Always succeed, whatever the step's chance (a guaranteed upgrade).
- `PayFrom { get; init; } -> EntityId` — Who pays: a container your mod vouches for (a loaded chest's entity), or none (the default): the player.

## UpgradeResult `0.2.12` — What `Items.UpgradeRarity` did.
- `UpgradeResult(UpgradeStatus Status, EntityId Player, ItemInstanceRef Item, ItemRarity From, ItemRarity To, GearState? Before, GearState? After, IReadOnlyDictionary<ItemId, int> Paid, double Chance, double? Rolled, string Message)`
- `After { get; init; } -> GearState?` — The item after (a dry run: what a success would make; a refusal: as before); null as `UpgradeResult.Before`.
- `Before { get; init; } -> GearState?` — The item before; null when it was not found.
- `Chance { get; init; } -> double` — The chance the roll had (after the `GearUpgradingEvent` handlers).
- `DryRun { get; init; } -> bool` — A dry run: nothing changed.
- `From { get; init; } -> ItemRarity` — Its rarity before.
- `Item { get; init; } -> ItemInstanceRef` — The item asked about.
- `Message { get; init; } -> string` — Why, for anything but `UpgradeStatus.Done`; what the socket roll did, when it ran.
- `Ok { get; } -> bool` — True when the item is one rarity higher (a dry run: would be).
- `Paid { get; init; } -> IReadOnlyDictionary<ItemId, int>` — What the payers paid, by item (a dry run: what a success would cost; empty when nothing was paid).
- `Player { get; init; } -> EntityId` — The player it ran for.
- `Reason { get; } -> string?` — Why it did not happen; null when `UpgradeResult.Ok`.
- `Rolled { get; init; } -> double?` — The server's roll, 0 (inclusive) to 1 (exclusive): below `UpgradeResult.Chance` succeeds.
- `Rule { get; init; } -> string?` — The rule's id when it was a block (`"mod:name"`); null for a rule made in code.
- `SocketAdded { get; init; } -> bool` — With `SocketRule.GameChance`: the item won a socket and was re-created with it.
- `Status { get; init; } -> UpgradeStatus` — The outcome.
- `To { get; init; } -> ItemRarity` — The rarity the step reaches (also when it failed or was refused; `UpgradeResult.From` at the top).

## UpgradeStatus `0.2.12` — What an upgrade did (`UpgradeResult.Status`).
enum: Done, Failed, NoSlot, AtMax, NotAllowed, CantPay, Canceled, Refused, Unavailable, Stale, NotGear, Dropped

## UpgradeStep `0.2.12` — One step of a `RarityUpgradeRule`: what reaching one rarity costs and how likely it is.
- `Chance { get; init; } -> double` — 0 to 1: how likely the step is to succeed (default `UpgradeStep.DefaultChance`).
- `Cost { get; init; } -> IDictionary<string, int>` — The items it costs, by name: game items (`GameItems` constants), `"other-mod:name"`, or an item of the rule's own...
- `const DefaultChance = 1 -> double` — The success chance of a step that names none: 1 (always).
- `const DefaultOnFail = UpgradeFail.KeepItemLoseCost -> UpgradeFail` — What a failed roll costs when a step names nothing: `UpgradeFail.KeepItemLoseCost`.
- `LevelScale { get; init; } -> double` — 0 or more: the cost grows with the item's level, `count x (1 + LevelScale x (level - 1))` rounded up (at most 100000...
- `OnFail { get; init; } -> UpgradeFail` — What a failed roll costs (default `UpgradeStep.DefaultOnFail`).
- `Unlock { get; init; } -> bool` — The upgrade also unlocks one more enhancement slot at once, as the workbench does without its cost (one level, one...

## WaterRefilledEvent — Water a refill policy gave back (`Fuel.Refill`): posted on the tick thread in the Pre after it happened, at most once per step and policy, with every container it...
- `Changes { get; } -> IReadOnlyList<FuelChange>` — The containers it changed since the last event, with their units before and after.
- `Entity { get; } -> EntityId` — The holder (or the container itself) the policy is on.
- `Keep { get; } -> FuelKeep` — The refill policy that gave the water back.
- `Origin { get; } -> string` — Id of the mod that set the policy ("kiln" outside mod code).
- `Units { get; } -> long` — Units given back, over every container.
