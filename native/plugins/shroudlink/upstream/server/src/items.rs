//! Creating and destroying items for mods (HostApi v7), through the game's own inventory
//! functions, so slots, stack limits and the change log stay the game's:
//!
//! - `FUN_1401ac790(out, ctx, entity, slot, item, count, level?, rarity?, data?)` puts
//!   items into an entity's inventory (partial stacks first, then free slots), as the craft
//!   routine does with its outputs (`FUN_140194a30`); slot 0 lets it choose, the three
//!   optional values are `{ value, isSet }` and stay unset.
//! - `FUN_1401b3c00(out, ctx, slot, count, clear)` takes items out of a slot, as the slot
//!   transfer does with its source.
//!
//! Only plain stackable items: nothing with its own item entity (`HasPIDE`: durability,
//! level, perks), and never a player's inventory. Results are counted from the inventory
//! before and after, not taken from the game's return value.

use std::sync::OnceLock;

use chestsort_common::log;

use crate::events;
use crate::game::{Ctx, SLOTS_PER_PAGE};
use crate::hooks;
use crate::world_ctx;

type GiveFn = unsafe extern "system" fn(out: *mut [u8; 32], ctx: *mut Ctx, entity: u32, slot: u64, item: u32, count: i32, level: *mut [u8; 8], rarity: *mut [u8; 2], data: *mut [u8; 8]) -> *mut u8;
type TakeFn = unsafe extern "system" fn(out: *mut [u8; 32], ctx: *mut Ctx, slot: u64, count: u32, clear: u8) -> *mut u8;

struct Functions {
    give: GiveFn,
    take: TakeFn,
}

static FUNCTIONS: OnceLock<Option<Functions>> = OnceLock::new();

/// `ItemInfo.flags` (+0x5d8) bit 1: `HasPIDE`.
const ITEM_FLAGS: usize = 0x5d8;
const HAS_PIDE: u8 = 2;
/// At most this many items per call.
const MAX_COUNT: u32 = 10_000;

fn functions() -> Option<&'static Functions> {
    FUNCTIONS
        .get_or_init(|| {
            if !hooks::available(hooks::Feature::Items) {
                return None;
            }
            let (give, take) = (hooks::address("give_items").ok()?, hooks::address("take_items").ok()?);
            unsafe { Some(Functions { give: std::mem::transmute::<usize, GiveFn>(give), take: std::mem::transmute::<usize, TakeFn>(take) }) }
        })
        .as_ref()
}

/// Items of `item` in an entity's inventory.
unsafe fn count_of(ctx: *mut Ctx, entity: u32, item: u32) -> Option<u32> {
    let game = events::game();
    let setup = game.inventory_setup(ctx, entity)?;
    let mut n = 0;
    for page in game.pages(ctx, setup).pages().iter().filter(|p| p.entity != 0) {
        for slot in 0..SLOTS_PER_PAGE {
            let stack = &*page.slots.add(slot as usize);
            if stack.item_id == item {
                n += stack.count;
            }
        }
    }
    Some(n)
}

/// A plain stackable item the game knows.
unsafe fn plain(ctx: *mut Ctx, item: u32) -> bool {
    events::game().item_info(ctx, item).is_some_and(|info| *(info as *const _ as *const u8).add(ITEM_FLAGS) & HAS_PIDE == 0)
}

/// Puts up to `count` of `item` into an entity's inventory; returns how many it took.
pub fn give(entity: u32, item: u32, count: u32) -> u32 {
    let Some(f) = functions() else { return 0 };
    let count = count.min(MAX_COUNT);
    if count == 0 {
        return 0;
    }
    let given = world_ctx::with(|ctx| unsafe {
        let game = events::game();
        if game.is_player(ctx, entity) || !plain(ctx, item) {
            return 0;
        }
        let Some(before) = count_of(ctx, entity, item) else { return 0 };
        let mut out = [0u8; 32];
        let (mut level, mut rarity, mut data) = ([0u8; 8], [0u8; 2], [0u8; 8]);
        (f.give)(&mut out, ctx, entity, 0, item, count as i32, &mut level, &mut rarity, &mut data);
        count_of(ctx, entity, item).unwrap_or(before).saturating_sub(before)
    })
    .unwrap_or(0);
    log!("items: {given} of {count} x item {item} created in {entity}");
    given
}

/// Removes up to `count` items from one slot of an entity's inventory; returns how many.
pub fn remove(entity: u32, slot: u64, count: u32) -> u32 {
    let Some(f) = functions() else { return 0 };
    let count = count.min(MAX_COUNT);
    if count == 0 {
        return 0;
    }
    let removed = world_ctx::with(|ctx| unsafe {
        let game = events::game();
        if game.is_player(ctx, entity) {
            return 0;
        }
        let Some(setup) = game.inventory_setup(ctx, entity) else { return 0 };
        let (page, index) = (slot as u32, (slot >> 32) as u8);
        let pages = game.pages(ctx, setup);
        let Some(page) = pages.pages().iter().find(|p| p.entity == page && p.entity != 0) else { return 0 };
        if index >= SLOTS_PER_PAGE {
            return 0;
        }
        let stack = &*page.slots.add(index as usize);
        let (item, before) = (stack.item_id, stack.count);
        if before == 0 || !plain(ctx, item) {
            return 0;
        }
        let mut out = [0u8; 32];
        (f.take)(&mut out, ctx, slot, count.min(before), 1);
        let after = if stack.item_id == item { stack.count } else { 0 };
        before.saturating_sub(after)
    })
    .unwrap_or(0);
    log!("items: {removed} of {count} removed from slot {slot:#x} of {entity}");
    removed
}

pub unsafe extern "C" fn api_give_items(entity: u32, item: u32, count: u32) -> u32 {
    give(entity, item, count)
}

pub unsafe extern "C" fn api_remove_items(entity: u32, slot: u64, count: u32) -> u32 {
    remove(entity, slot, count)
}
