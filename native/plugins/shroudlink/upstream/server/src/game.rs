//! Server-side game functions and data layouts (Enshrouded 1.0, build 1269579).
//!
//! Only the two area-deposit functions are located by signature; every helper is
//! resolved through the n-th direct `call` inside them, which survives patches
//! better than signatures for tiny functions. See research/decomp for the
//! decompiled originals.

use std::ffi::c_void;

use chestsort_common::scan::call_targets;

use crate::hooks;


pub type MoveAllFn =
    unsafe extern "system" fn(out: *mut DepositResult, ctx: *mut Ctx, source: u32, target: u32, mode: u32, flag: u8) -> *mut DepositResult;

pub type DepositIntoChestFn =
    unsafe extern "system" fn(out: *mut DepositResult, ctx: *mut Ctx, chest: u32, player: u32, feedback: *mut c_void) -> *mut DepositResult;

type StorageOfFn = unsafe extern "system" fn(world: *mut c_void) -> *mut c_void;
/// Writes the component's address and its size (16 bytes) into `out` and returns `out`.
type ComponentByIdFn = unsafe extern "system" fn(out: *mut [usize; 2], storage: *mut c_void, handle: usize, entity: u32) -> *mut *mut u8;
type CollectPagesFn = unsafe extern "system" fn(list: *mut PageList, ctx: *mut Ctx, setup: *mut u8) -> u64;
type PlayerLinksFn = unsafe extern "system" fn(world: *mut c_void, handle: usize, entity: u32) -> *mut u8;
type ItemInfoFn = unsafe extern "system" fn(item_id: u32, registry: usize) -> *const ItemInfo;
type TransferFn = unsafe extern "system" fn(out: *mut TransferResult, ctx: *mut Ctx, dst: u64, src: u64, mode: i32) -> *mut TransferResult;
type EntityFn = unsafe extern "system" fn(world: *mut c_void, entity: u32) -> *mut u8;

/// Opaque system context shared by the deposit functions; accessed as `usize` words.
#[repr(C)]
pub struct Ctx {
    words: [usize; 0x20],
}

impl Ctx {
    fn word(&self, index: usize) -> usize {
        self.words[index]
    }
    fn world(&self) -> *mut c_void {
        self.word(0) as *mut c_void
    }
    fn flags_b0(&self) -> u8 {
        unsafe { *((self as *const Ctx as *const u8).add(0xb0)) }
    }
}

/// Return value of the per-chest deposit: error code + number of items moved.
#[repr(C)]
pub struct DepositResult {
    pub error: u8,
    _pad: [u8; 3],
    _zero: u32,
    pub moved: u32,
}

#[repr(C)]
#[derive(Default)]
pub struct TransferResult {
    pub error: u8,
    _pad: [u8; 7],
    pub moved: u32,
    _rest: [u32; 5],
}

/// `keen::ItemStack`
#[repr(C)]
pub struct ItemStack {
    pub item_id: u32,
    pub count: u32,
    pub pide: u32,
}

pub const SLOTS_PER_PAGE: u8 = 8;

/// Transfer modes of the game's slot transfer.
pub const TRANSFER_STACK: i32 = 0;
pub const TRANSFER_ONE: i32 = 2;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Page {
    pub slots: *mut ItemStack,
    pub entity: u32,
    _pad: u32,
}

const PAGE_CAPACITY: usize = 64;

/// The game's small-vector used by `collect_pages`; growth is disabled by passing no grow callback.
#[repr(C)]
pub struct PageList {
    data: *mut Page,
    len: usize,
    capacity: usize,
    grow: usize,
    extra: usize,
    storage: [Page; PAGE_CAPACITY],
}

impl PageList {
    pub fn new() -> Box<PageList> {
        let mut list = Box::new(PageList {
            data: std::ptr::null_mut(),
            len: 0,
            capacity: PAGE_CAPACITY,
            grow: 0,
            extra: 0,
            storage: [Page { slots: std::ptr::null_mut(), entity: 0, _pad: 0 }; PAGE_CAPACITY],
        });
        list.data = list.storage.as_mut_ptr();
        list
    }
    pub fn pages(&self) -> &[Page] {
        &self.storage[..self.len.min(PAGE_CAPACITY)]
    }
}

/// Prefix of `keen::ItemInfo` as laid out in memory.
#[repr(C)]
pub struct ItemInfo {
    pub item_id: u32,
    _a: [u8; 0x10],
    pub max_stack: u16,
}

// `ItemInfo.flags` (+0x5d8) bit 1 is `HasPIDE`: the item owns an entity with its
// durability, level and perks. Vanilla quick stacking skips those items; the
// category deposit does not need to, as the transfer moves the PIDE with the slot.

/// The self-test lets an inventory play the player (there is none without a client).
pub static FAKE_PLAYER: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// Player inventory that deposits leave alone.
#[derive(Default)]
pub struct Reserved {
    /// actionbar pages (entity ids, 0 = none)
    pub pages: [u32; 2],
    /// equipped items and the item in hand
    pub slots: std::collections::HashSet<u64>,
}

/// `EquipmentSlotData`: vanity x5, armour x5, rings x2, ranged weapon, shield, arrow,
/// spell, fishing bait, hook shot, glider, backpack.
const EQUIPMENT_SLOTS: usize = 20;

/// `keen::InventorySlotId` packed into a register: entity id | slot index << 32.
pub fn slot_id(entity: u32, slot: u8) -> u64 {
    entity as u64 | (slot as u64) << 32
}

pub struct Game {
    pub deposit_into_chest: DepositIntoChestFn,
    pub move_all: Option<MoveAllFn>,
    storage_of: StorageOfFn,
    component_by_id: ComponentByIdFn,
    collect_pages: CollectPagesFn,
    player_links: PlayerLinksFn,
    item_info: ItemInfoFn,
    transfer: TransferFn,
    entity: EntityFn,
}

/// Bytes of `FUN_1401ad1c0` that contain the calls we resolve (function is 0xd20 long).
const DEPOSIT_SCAN_LEN: usize = 0x700;
const AREA_SCAN_LEN: usize = 0x200;

impl Game {
    /// The inventory functions, from the hook registry; "move all" is optional.
    pub fn resolve() -> Result<Game, String> {
        let deposit = hooks::address("quick_stack")?;
        let area = hooks::address("area_deposit")?;
        let move_all = hooks::address("move_all").ok();
        let calls = call_targets(deposit, DEPOSIT_SCAN_LEN);
        let area_calls = call_targets(area, AREA_SCAN_LEN);
        if calls.len() < 15 || area_calls.len() < 2 {
            return Err(format!("unexpected call layout: {} / {} calls", calls.len(), area_calls.len()));
        }
        // the per-chest function calls these pairs twice (chest, player); the duplicates must agree
        if calls[0] != calls[2] || calls[1] != calls[3] || calls[4] != calls[5] {
            return Err("call layout of deposit function changed".into());
        }
        unsafe {
            Ok(Game {
                deposit_into_chest: std::mem::transmute::<usize, DepositIntoChestFn>(deposit),
                move_all: move_all.map(|a| std::mem::transmute::<usize, MoveAllFn>(a)),
                storage_of: std::mem::transmute::<usize, StorageOfFn>(calls[0]),
                component_by_id: std::mem::transmute::<usize, ComponentByIdFn>(calls[1]),
                collect_pages: std::mem::transmute::<usize, CollectPagesFn>(calls[4]),
                player_links: std::mem::transmute::<usize, PlayerLinksFn>(calls[6]),
                item_info: std::mem::transmute::<usize, ItemInfoFn>(calls[10]),
                transfer: std::mem::transmute::<usize, TransferFn>(calls[14]),
                entity: std::mem::transmute::<usize, EntityFn>(area_calls[0]),
            })
        }
    }

    /// `InventorySetup` component of an entity, if initialised.
    pub unsafe fn inventory_setup(&self, ctx: *mut Ctx, entity: u32) -> Option<*mut u8> {
        let storage = (self.storage_of)(*((*ctx).world() as *mut *mut c_void));
        // address + size: an 8-byte buffer here once overwrote a saved register
        let mut out = [0usize; 2];
        let setup = *(self.component_by_id)(&mut out, storage, (*ctx).word(3), entity);
        (!setup.is_null() && *setup.add(0x53) != 0).then_some(setup)
    }

    pub unsafe fn pages(&self, ctx: *mut Ctx, setup: *mut u8) -> Box<PageList> {
        let mut list = PageList::new();
        (self.collect_pages)(&mut *list, ctx, setup);
        list
    }

    /// What a deposit must never take from the player, read from the player's
    /// `ecs::SlotSelection` (216 bytes): the 20 equipment slots (`equipment`, an
    /// `EquipmentSlotData` of `InventorySlotId`s at +0x0), the two actionbar pages
    /// (`actionbarEntityIds` at +0xa0) and the item in hand (`actionItem.slotId` at +0xa8).
    /// Equipped items stay in their inventory slot; the equipment only points at it.
    /// True for player entities (they have a slot selection with actionbar pages).
    pub unsafe fn is_player(&self, ctx: *mut Ctx, entity: u32) -> bool {
        if entity != 0 && FAKE_PLAYER.load(std::sync::atomic::Ordering::Relaxed) == entity {
            return true;
        }
        let selection = (self.player_links)((*ctx).world(), (*ctx).word(0x1d), entity);
        !selection.is_null() && *(selection.add(0xa0) as *const u32) != 0
    }

    pub unsafe fn player_reserved(&self, ctx: *mut Ctx, player: u32) -> Reserved {
        let mut reserved = Reserved::default();
        let selection = (self.player_links)((*ctx).world(), (*ctx).word(0x1d), player);
        if selection.is_null() {
            return reserved;
        }
        let slot = |offset: usize| {
            let entity = *(selection.add(offset) as *const u32);
            let index = *selection.add(offset + 4);
            (entity != 0).then(|| slot_id(entity, index))
        };
        reserved.slots.extend((0..EQUIPMENT_SLOTS).filter_map(|i| slot(i * 8)));
        reserved.slots.extend(slot(0xa8));
        reserved.pages = [*(selection.add(0xa0) as *const u32), *(selection.add(0xa4) as *const u32)];
        reserved
    }

    pub unsafe fn item_info(&self, ctx: *mut Ctx, item_id: u32) -> Option<&ItemInfo> {
        (self.item_info)(item_id, (*ctx).word(1)).as_ref()
    }

    /// Vanilla transfer between two inventory slots (mode 0 = whole stack).
    pub unsafe fn transfer(&self, ctx: *mut Ctx, dst: u64, src: u64) -> TransferResult {
        self.transfer_mode(ctx, dst, src, TRANSFER_STACK)
    }

    /// Vanilla transfer with a mode: [`TRANSFER_STACK`], half or [`TRANSFER_ONE`]
    /// (`FUN_1401aed90`: the mode decides the amount; 1 and 2 also split into an empty slot).
    pub unsafe fn transfer_mode(&self, ctx: *mut Ctx, dst: u64, src: u64, mode: i32) -> TransferResult {
        let mut result = TransferResult::default();
        (self.transfer)(&mut result, ctx, dst, src, mode);
        result
    }

    pub unsafe fn deposit_disabled(&self, ctx: *mut Ctx) -> bool {
        (*ctx).flags_b0() & 0x80 != 0
    }

    pub unsafe fn entity_record(&self, ctx: *mut Ctx, entity: u32) -> Option<EntityRecord> {
        let ptr = (self.entity)((*ctx).world(), entity);
        (!ptr.is_null()).then_some(EntityRecord(ptr))
    }

    /// Checks our struct-of-arrays addressing against the game's own accessor
    /// for the `InventorySetup` component (handle `ctx[3]` points at its type index).
    pub unsafe fn verify_component_layout(&self, ctx: *mut Ctx, entity: u32) -> bool {
        let Some(record) = self.entity_record(ctx, entity) else { return false };
        let index = *((*ctx).word(3) as *const u16);
        let ours = record.component(index).map(|(addr, _)| addr);
        let theirs = self.inventory_setup(ctx, entity).map(|p| p as usize);
        ours.is_some() && ours == theirs
    }
}

/// Raw ECS entity record: archetype at +0x18, chunk base at +0x20, row at +0x30.
/// Archetypes store a component bitmask at +0, per-component array offsets at
/// +0x84 and element sizes at +0xa84 (both u16, indexed by component type).
pub struct EntityRecord(pub *mut u8);

pub const MAX_COMPONENT_TYPES: u16 = 0x420;

impl EntityRecord {
    unsafe fn archetype(&self) -> *const u8 {
        *(self.0.add(0x18) as *const *const u8)
    }

    /// Address and element size of component type `index`, if the entity has it.
    pub unsafe fn component(&self, index: u16) -> Option<(usize, u16)> {
        let arch = self.archetype();
        if arch.is_null() || index >= MAX_COMPONENT_TYPES {
            return None;
        }
        let word = *(arch.add((index as usize >> 6) * 8) as *const u64);
        if word >> (index & 63) & 1 == 0 {
            return None;
        }
        let offset = *(arch.add(0x84 + index as usize * 2) as *const u16) as usize;
        let size = *(arch.add(0xa84 + index as usize * 2) as *const u16);
        let row = *(self.0.add(0x30) as *const u32) as usize;
        let chunk = *(self.0.add(0x20) as *const usize);
        Some((chunk + offset + row * size as usize, size))
    }

    /// All component types of this entity whose element size is `size`.
    pub unsafe fn components_of_size(&self, size: u16) -> Vec<(u16, usize)> {
        (0..MAX_COMPONENT_TYPES)
            .filter_map(|i| self.component(i).filter(|(_, s)| *s == size).map(|(addr, _)| (i, addr)))
            .collect()
    }
}
