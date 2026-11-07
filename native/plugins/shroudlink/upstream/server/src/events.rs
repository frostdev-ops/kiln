//! Inventory events: hooks on every way the game moves a whole inventory, dispatched
//! to the mods (see docs/sdk-v2.md).
//!
//! - `FUN_1401ad1c0(out, ctx, to, from, feedback)`: quick stack (hold R / F, and once
//!   per chest for a Quick Stack Station). Runs in both directions, chest -> player too.
//! - `FUN_1401ad940(out, ctx, from, to, mode, flag)`: move all (tap R / F).
//!
//! Per event: mods name slots to keep, mods move stacks themselves through the game
//! API, then the game's own transfer runs with the kept and the player's protected stacks
//! (equipment, item in hand, action bar) hidden from it.

use std::collections::HashSet;
use std::ffi::c_void;
use std::sync::OnceLock;

use retour::GenericDetour;
use shroudlink_sdk::{InventoryEvent, TransferKind};

use crate::game::{slot_id, Ctx, DepositIntoChestFn, DepositResult, Game, ItemStack, MoveAllFn, SLOTS_PER_PAGE};
use crate::mods;

static GAME: OnceLock<Game> = OnceLock::new();
static HOOK: OnceLock<GenericDetour<DepositIntoChestFn>> = OnceLock::new();
static MOVE_ALL_HOOK: OnceLock<GenericDetour<MoveAllFn>> = OnceLock::new();

/// What the game API sees during one event (the `game` pointer handed to mods).
pub struct EventCtx {
    pub ctx: *mut Ctx,
    pub event: InventoryEvent,
    /// slots no mod may move: the player's equipment, item in hand and actionbar pages,
    /// plus everything mods asked to keep
    pub protected: HashSet<u64>,
}

pub fn game() -> &'static Game {
    GAME.get().unwrap()
}

/// Hooks quick stack (required) and move all (when found; otherwise only that is off).
pub fn install(game: Game) -> Result<(), String> {
    let (deposit, move_all) = (game.deposit_into_chest, game.move_all);
    let _ = GAME.set(game);
    unsafe {
        let hook = GenericDetour::<DepositIntoChestFn>::new(deposit, quick_stack_hook).map_err(|e| format!("creating quick stack hook failed: {e}"))?;
        hook.enable().map_err(|e| format!("enabling quick stack hook failed: {e}"))?;
        let _ = HOOK.set(hook);
        if let Some(move_all) = move_all {
            let result = GenericDetour::<MoveAllFn>::new(move_all, move_all_hook).and_then(|hook| hook.enable().map(|()| hook));
            match result {
                Ok(hook) => {
                    let _ = MOVE_ALL_HOOK.set(hook);
                }
                Err(e) => crate::hooks::disable(crate::hooks::Feature::MoveAll, format!("hook failed: {e}")),
            }
        }
    }
    Ok(())
}

unsafe extern "system" fn quick_stack_hook(out: *mut DepositResult, ctx: *mut Ctx, to: u32, from: u32, feedback: *mut c_void) -> *mut DepositResult {
    let event = InventoryEvent { kind: TransferKind::QuickStack, from, to };
    let original = HOOK.get().unwrap();
    around(ctx, event, || original.call(out, ctx, to, from, feedback))
}

unsafe extern "system" fn move_all_hook(out: *mut DepositResult, ctx: *mut Ctx, from: u32, to: u32, mode: u32, flag: u8) -> *mut DepositResult {
    let event = InventoryEvent { kind: TransferKind::MoveAll, from, to };
    let original = MOVE_ALL_HOOK.get().unwrap();
    around(ctx, event, || original.call(out, ctx, from, to, mode, flag))
}

/// Runs the mods around the game's own transfer `vanilla`.
unsafe fn around(ctx: *mut Ctx, event: InventoryEvent, vanilla: impl FnOnce() -> *mut DepositResult) -> *mut DepositResult {
    let game = game();
    let mut ev = EventCtx { ctx, event, protected: HashSet::new() };
    if game.is_player(ctx, event.from) {
        let reserved = game.player_reserved(ctx, event.from);
        ev.protected.extend(reserved.slots);
        for page in reserved.pages.into_iter().filter(|&p| p != 0) {
            ev.protected.extend((0..SLOTS_PER_PAGE).map(|s| slot_id(page, s)));
        }
    }
    let kept: HashSet<u64> = mods::keep(&mut ev as *mut EventCtx as *mut c_void, &event).into_iter().collect();
    ev.protected.extend(&kept);
    let moved = mods::transfer(&mut ev as *mut EventCtx as *mut c_void, &event);

    // the game's own transfer skips empty stacks, so protected stacks are shown to it as
    // empty for the duration of the call: what mods keep, and the player's equipment,
    // item in hand and action bar (the game's own "move all" takes the action bar along)
    let hidden = hide(game, ctx, event.from, &ev.protected);
    let result = vanilla();
    for (stack, count) in hidden {
        (*stack).count = count;
    }
    if moved != 0 && !result.is_null() && (*result).error == 0 {
        (*result).moved += moved;
    }
    result
}

/// Empties the stacks of `slots` in the entity's inventory; returns them with their counts.
unsafe fn hide(game: &Game, ctx: *mut Ctx, entity: u32, slots: &HashSet<u64>) -> Vec<(*mut ItemStack, u32)> {
    let mut hidden = Vec::new();
    if slots.is_empty() {
        return hidden;
    }
    let Some(setup) = game.inventory_setup(ctx, entity) else { return hidden };
    for page in game.pages(ctx, setup).pages().iter().filter(|p| p.entity != 0) {
        for slot in 0..SLOTS_PER_PAGE {
            let stack = page.slots.add(slot as usize);
            if (*stack).count != 0 && slots.contains(&slot_id(page.entity, slot)) {
                hidden.push((stack, (*stack).count));
                (*stack).count = 0;
            }
        }
    }
    hidden
}
