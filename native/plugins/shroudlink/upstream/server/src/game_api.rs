//! The game functions of the mod API (`HostApi` v2). `game` is the `EventCtx` of the
//! inventory event being dispatched; outside of one the host passes null and every
//! call returns nothing.

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};

use chestsort_common::log;
use shroudlink_sdk::Slot;

use crate::events::{self, EventCtx};
use crate::game::{slot_id, Ctx, Game, Page, SLOTS_PER_PAGE};
use crate::names;

/// Component type index of `keen::ecs::CustomPlayerString`, learned on first match.
static NAME_COMPONENT: AtomicU32 = AtomicU32::new(u32::MAX);
static LAYOUT_CHECKED: AtomicBool = AtomicBool::new(false);
static LAYOUT_OK: AtomicBool = AtomicBool::new(false);

unsafe fn current<'a>(game: *mut c_void) -> Option<&'a EventCtx> {
    (game as *const EventCtx).as_ref()
}

pub unsafe extern "C" fn slots(game: *mut c_void, entity: u32, out: *mut Slot, cap: usize) -> usize {
    let Some(ev) = current(game) else { return 0 };
    let g = events::game();
    let Some(setup) = g.inventory_setup(ev.ctx, entity) else { return 0 };
    let mut n = 0;
    for page in g.pages(ev.ctx, setup).pages().iter().filter(|p| p.entity != 0) {
        for slot in 0..SLOTS_PER_PAGE {
            if n < cap {
                let stack = &*page.slots.add(slot as usize);
                *out.add(n) = Slot { id: slot_id(page.entity, slot), item: stack.item_id, count: stack.count };
            }
            n += 1;
        }
    }
    n
}

pub unsafe extern "C" fn is_player(game: *mut c_void, entity: u32) -> bool {
    current(game).is_some_and(|ev| events::game().is_player(ev.ctx, entity))
}

pub unsafe extern "C" fn chest_name(game: *mut c_void, entity: u32, buf: *mut u8, cap: usize) -> usize {
    let Some(ev) = current(game) else { return 0 };
    let Some(name) = name_of(events::game(), ev.ctx, entity) else { return 0 };
    std::ptr::copy_nonoverlapping(name.as_ptr(), buf, name.len().min(cap));
    name.len()
}

pub unsafe extern "C" fn max_stack(game: *mut c_void, item: u32) -> u32 {
    current(game).and_then(|ev| events::game().item_info(ev.ctx, item)).map_or(0, |info| info.max_stack as u32)
}

/// Moves one stack of the event's source inventory into `to`, as often as the game
/// accepts (partial stacks of the same item first, then empty slots).
pub unsafe extern "C" fn move_stack(game: *mut c_void, slot: u64, to: u32) -> u32 {
    let Some(ev) = current(game) else { return 0 };
    move_stack_in(events::game(), ev.ctx, ev.event.from, slot, to, &ev.protected)
}

/// Moves the stack in `slot` of `from`'s inventory into `to`'s. Never takes `protected`
/// slots, and never fills a player's action bar.
pub(crate) unsafe fn move_stack_in(g: &Game, ctx: *mut Ctx, from: u32, slot: u64, to: u32, protected: &std::collections::HashSet<u64>) -> u32 {
    if protected.contains(&slot) || g.deposit_disabled(ctx) {
        return 0;
    }
    let (Some(from_setup), Some(to_setup)) = (g.inventory_setup(ctx, from), g.inventory_setup(ctx, to)) else { return 0 };
    let from_pages = g.pages(ctx, from_setup);
    let (page, index) = (slot as u32, (slot >> 32) as u8);
    let Some(page) = from_pages.pages().iter().find(|p| p.entity == page && p.entity != 0) else { return 0 };
    if index >= SLOTS_PER_PAGE {
        return 0;
    }
    let stack = &*page.slots.add(index as usize);
    if stack.count == 0 {
        return 0;
    }
    let Some(info) = g.item_info(ctx, stack.item_id) else { return 0 };
    let skip = if g.is_player(ctx, to) { g.player_reserved(ctx, to).pages } else { [0, 0] };
    let to_pages: Vec<Page> = g.pages(ctx, to_setup).pages().iter().copied().filter(|p| p.entity != 0 && !skip.contains(&p.entity)).collect();
    let mut moved = 0;
    // keep going until the stack is empty or the target has no room
    while stack.count != 0 {
        let Some(dst) = find_destination(&to_pages, stack.item_id, info.max_stack as u32) else { break };
        let result = g.transfer(ctx, dst, slot);
        if result.error != 0 || result.moved == 0 {
            break;
        }
        moved += result.moved;
    }
    moved
}

/// Moves `count` items of the stack in `slot` of `from` into `to` (the whole stack when it
/// holds no more); same protection as [`move_stack_in`]. Below a whole stack the game moves
/// one item per transfer (mode "one", as a right click does), partial stacks first.
pub(crate) unsafe fn move_count_in(g: &Game, ctx: *mut Ctx, from: u32, slot: u64, to: u32, count: u32, protected: &std::collections::HashSet<u64>) -> u32 {
    if count == 0 || protected.contains(&slot) || g.deposit_disabled(ctx) {
        return 0;
    }
    let Some(from_setup) = g.inventory_setup(ctx, from) else { return 0 };
    let from_pages = g.pages(ctx, from_setup);
    let (page, index) = (slot as u32, (slot >> 32) as u8);
    let Some(page) = from_pages.pages().iter().find(|p| p.entity == page && p.entity != 0) else { return 0 };
    if index >= SLOTS_PER_PAGE {
        return 0;
    }
    let stack = &*page.slots.add(index as usize);
    if stack.count <= count {
        return move_stack_in(g, ctx, from, slot, to, protected);
    }
    let (item, Some(info)) = (stack.item_id, g.item_info(ctx, stack.item_id)) else { return 0 };
    let Some(to_setup) = g.inventory_setup(ctx, to) else { return 0 };
    let skip = if g.is_player(ctx, to) { g.player_reserved(ctx, to).pages } else { [0, 0] };
    let to_pages: Vec<Page> = g.pages(ctx, to_setup).pages().iter().copied().filter(|p| p.entity != 0 && !skip.contains(&p.entity)).collect();
    let mut moved = 0;
    while moved < count && stack.count != 0 && stack.item_id == item {
        let Some(dst) = find_destination(&to_pages, item, info.max_stack as u32) else { break };
        let result = g.transfer_mode(ctx, dst, slot, crate::game::TRANSFER_ONE);
        if result.error != 0 || result.moved == 0 {
            break;
        }
        moved += result.moved;
    }
    moved
}

/// A partial stack of the same item, otherwise the first empty slot.
unsafe fn find_destination(pages: &[Page], item_id: u32, max_stack: u32) -> Option<u64> {
    let mut empty = None;
    for page in pages.iter().filter(|p| p.entity != 0) {
        for slot in 0..SLOTS_PER_PAGE {
            let stack = &*page.slots.add(slot as usize);
            if stack.count == 0 {
                empty.get_or_insert(slot_id(page.entity, slot));
            } else if stack.item_id == item_id && stack.count < max_stack {
                return Some(slot_id(page.entity, slot));
            }
        }
    }
    empty
}

/// The name given to an entity with the Rename button, if any.
pub(crate) unsafe fn name_of(game: &Game, ctx: *mut Ctx, entity: u32) -> Option<String> {
    name_with(game, ctx, entity, true)
}

/// Like [`name_of`], but never rescans the heap for an unknown key (a rescan takes about
/// half a second on the game thread); the background scan finds new names within 15 s.
pub(crate) unsafe fn name_known(game: &Game, ctx: *mut Ctx, entity: u32) -> Option<String> {
    name_with(game, ctx, entity, false)
}

unsafe fn name_with(game: &Game, ctx: *mut Ctx, entity: u32, fresh: bool) -> Option<String> {
    if let Some(name) = crate::selftest::fake_name(entity) {
        return Some(name);
    }
    if !LAYOUT_CHECKED.swap(true, Ordering::Relaxed) {
        let ok = game.verify_component_layout(ctx, entity);
        LAYOUT_OK.store(ok, Ordering::Relaxed);
        log!("component layout check: {}", if ok { "ok" } else { "FAILED - chest names disabled" });
    }
    if !LAYOUT_OK.load(Ordering::Relaxed) {
        return None;
    }
    let record = game.entity_record(ctx, entity)?;
    let known = NAME_COMPONENT.load(Ordering::Relaxed);
    if known != u32::MAX {
        let (addr, size) = record.component(known as u16)?;
        let key = *(addr as *const u64);
        // no name given: nothing to look up (an unknown key rescans the heap)
        return (size == 8 && key != 0).then(|| if fresh { names::lookup_fresh(key) } else { names::lookup(key) }).flatten();
    }
    // not learned yet: the name component is the 8-byte component whose value is a name key
    for (index, addr) in record.components_of_size(8) {
        if let Some(name) = names::lookup(*(addr as *const u64)) {
            NAME_COMPONENT.store(index as u32, Ordering::Relaxed);
            log!("chest name component is type #{index}");
            return Some(name);
        }
    }
    None
}
