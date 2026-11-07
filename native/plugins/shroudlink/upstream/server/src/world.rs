//! World access for mods that is not tied to an inventory event (HostApi v4): the players
//! in the world, positions, teleporting. Runs on a context from [`crate::world_ctx`].
//!
//! Positions: `CurrentTransform.transform.position` is a `keen::WorldPosition`, three
//! `sint64` in metres * 2^32 (y up). A teleport writes the new position into the entity's
//! current and last transform and increments its `TeleportCount`: the counter the client
//! watches to snap to the server's position instead of correcting it (verified in game).

use std::sync::Mutex;

use chestsort_common::log;

use crate::components::{self, hash};
use crate::events;
use crate::game::EntityRecord;
use crate::world_ctx;

const UNITS_PER_METRE: f64 = 4_294_967_296.0;

/// Addon session -> its player entity, learned from the backpack pages the addon reports.
static CLIENTS: Mutex<Vec<(u64, u32)>> = Mutex::new(Vec::new());

/// Player entity -> the name its addon reported (the Steam name).
static NAMES: Mutex<Vec<(u32, String)>> = Mutex::new(Vec::new());

/// Addon session -> the name it sent at the welcome, before the session has a player.
static CLIENT_NAMES: Mutex<Vec<(u64, String)>> = Mutex::new(Vec::new());

/// An addon reported its backpack pages (and its player's name): find the player whose
/// inventory has them.
pub fn identify(client: u64, pages: &[u32], name: Option<String>) -> Option<u32> {
    let player = world_ctx::with(|ctx| unsafe {
        let game = events::game();
        players().into_iter().find(|&p| {
            let theirs: Vec<u32> = game.inventory_setup(ctx, p).map(|setup| game.pages(ctx, setup).pages().iter().map(|page| page.entity).filter(|&e| e != 0).collect()).unwrap_or_default();
            if crate::game_events::probing() {
                log!("world: player {p} has pages {theirs:?}, addon reported {pages:?}");
            }
            theirs.iter().any(|e| pages.contains(e))
        })
    })
    .flatten()?;
    let mut clients = CLIENTS.lock().unwrap();
    clients.retain(|&(c, p)| c != client && p != player);
    clients.push((client, player));
    drop(clients);
    let name = name.or_else(|| client_name(client));
    log!("world: client {client:016x} is player {player}{}", name.as_deref().map(|n| format!(" ({n})")).unwrap_or_default());
    if let Some(name) = name {
        set_player_name(player, name);
    }
    Some(player)
}

/// The addon sent its player's name at the welcome (newer addons): kept for the session,
/// given to the player once the session has one.
pub fn name_client(client: u64, name: String) {
    let mut names = CLIENT_NAMES.lock().unwrap();
    names.retain(|(c, _)| *c != client);
    names.push((client, name.clone()));
    drop(names);
    if let Some(player) = player_of(client) {
        set_player_name(player, name);
    }
}

fn client_name(client: u64) -> Option<String> {
    CLIENT_NAMES.lock().unwrap().iter().find(|(c, _)| *c == client).map(|(_, n)| n.clone())
}

fn set_player_name(player: u32, name: String) {
    let mut names = NAMES.lock().unwrap();
    names.retain(|(p, _)| *p != player);
    names.push((player, name));
}

pub fn forget_client(client: u64) {
    CLIENTS.lock().unwrap().retain(|&(c, _)| c != client);
    CLIENT_NAMES.lock().unwrap().retain(|(c, _)| *c != client);
}

/// Addons without a player yet, before they opened a chest: when exactly one addon and
/// exactly one player are unmatched, they belong together (one player, or players joining
/// one after another). Returns the matches made.
pub fn match_unidentified(clients: &[u64]) -> Vec<(u64, u32)> {
    let unmatched: Vec<u64> = clients.iter().copied().filter(|&c| player_of(c).is_none()).collect();
    let [client] = unmatched[..] else { return Vec::new() };
    let players = players();
    let claimed: Vec<u32> = CLIENTS.lock().unwrap().iter().map(|&(_, p)| p).collect();
    let free: Vec<u32> = players.into_iter().filter(|p| !claimed.contains(p)).collect();
    let [player] = free[..] else { return Vec::new() };
    CLIENTS.lock().unwrap().push((client, player));
    let name = client_name(client);
    log!("world: client {client:016x} is player {player} (the only unmatched one){}", name.as_deref().map(|n| format!(" ({n})")).unwrap_or_default());
    if let Some(name) = name {
        set_player_name(player, name);
    }
    vec![(client, player)]
}

/// A player's backpack pages in the order the chest screen's backpack panel draws them:
/// the inventory's pages, with the two actionbar pages (`SlotSelection.actionbarEntityIds`)
/// moved to the end, as the panel draws the actionbar below the backpack.
pub fn layout_of(player: u32) -> Option<Vec<u32>> {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        let setup = game.inventory_setup(ctx, player)?;
        let mut pages: Vec<u32> = game.pages(ctx, setup).pages().iter().map(|p| p.entity).filter(|&e| e != 0).collect();
        let actionbar: Vec<u32> = game.player_reserved(ctx, player).pages.into_iter().filter(|p| *p != 0 && pages.contains(p)).collect();
        pages.retain(|p| !actionbar.contains(p));
        pages.extend(actionbar);
        (!pages.is_empty()).then_some(pages)
    })
    .flatten()
}

/// The player entity of an addon session, once it has identified itself.
pub fn player_of(client: u64) -> Option<u32> {
    CLIENTS.lock().unwrap().iter().find(|&&(c, _)| c == client).map(|&(_, p)| p)
}

/// Player entities seen logging in (LoginEvent); pruned when they stop being players.
static PLAYERS: Mutex<Vec<u32>> = Mutex::new(Vec::new());

/// A player entered the world (from the game event feed).
pub fn on_login(player: u32) {
    let mut players = PLAYERS.lock().unwrap();
    if !players.contains(&player) {
        players.push(player);
        log!("world: player {player} logged in");
    }
}

pub fn players() -> Vec<u32> {
    let known = PLAYERS.lock().unwrap().clone();
    let alive: Vec<u32> = world_ctx::with(|ctx| unsafe { known.iter().copied().filter(|&p| events::game().is_player(ctx, p)).collect() }).unwrap_or_default();
    *PLAYERS.lock().unwrap() = alive.clone();
    alive
}

/// Position in metres.
pub fn position(entity: u32) -> Option<[f64; 3]> {
    world_ctx::with(|ctx| unsafe {
        let record = events::game().entity_record(ctx, entity)?;
        let (addr, _) = components::of(ctx, &record, hash::CURRENT_TRANSFORM)?;
        let p = addr as *const i64;
        Some([*p as f64 / UNITS_PER_METRE, *p.add(1) as f64 / UNITS_PER_METRE, *p.add(2) as f64 / UNITS_PER_METRE])
    })
    .flatten()
}

/// Inventories within `radius` metres of `around` (loaded entities only): (entity,
/// position, custom name).
pub fn inventories_around(around: [f64; 3], radius: f64) -> Vec<(u32, [f64; 3], Option<String>)> {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        let keep = |entity: u32, _: &EntityRecord| game.inventory_setup(ctx, entity).is_some() && !game.is_player(ctx, entity);
        scan_around(ctx, around, radius, keep).into_iter().map(|(entity, pos)| (entity, pos, crate::game_api::name_of(game, ctx, entity))).collect()
    })
    .unwrap_or_default()
}

/// Entity ids the scans look at (the game's lookup is a bounds-checked hash map).
const MAX_ENTITY: u32 = 2_000_000;
/// How far past the highest entity id seen a scan looks: ids are handed out upwards, so
/// new entities appear there. Looking up every possible id costs 5 ms when the game's
/// worker threads are idle and 500 ms when they are busy (seen on a base with 150 chests).
const ID_HEADROOM: u32 = 65_536;
/// The highest entity id a scan found (0: none yet, the first scan looks at all ids).
static HIGHEST_ID: std::sync::atomic::AtomicU32 = std::sync::atomic::AtomicU32::new(0);

/// Loaded entities within `radius` metres of `around` that `keep` accepts, with their
/// position (`CurrentTransform`).
unsafe fn scan_around(ctx: *mut crate::game::Ctx, around: [f64; 3], radius: f64, keep: impl Fn(u32, &EntityRecord) -> bool) -> Vec<(u32, [f64; 3])> {
    let game = events::game();
    let Some(transform) = components::index_of(ctx, hash::CURRENT_TRANSFORM) else { return Vec::new() };
    let mut found = Vec::new();
    let highest = HIGHEST_ID.load(std::sync::atomic::Ordering::Relaxed);
    let end = if highest == 0 { MAX_ENTITY } else { highest.saturating_add(ID_HEADROOM).min(MAX_ENTITY) };
    let mut seen = highest;
    for entity in 1..end {
        let Some(record) = game.entity_record(ctx, entity) else { continue };
        seen = seen.max(entity);
        let Some((addr, _)) = record.component(transform) else { continue };
        let p = addr as *const i64;
        let pos = [*p as f64 / UNITS_PER_METRE, *p.add(1) as f64 / UNITS_PER_METRE, *p.add(2) as f64 / UNITS_PER_METRE];
        let d = ((pos[0] - around[0]).powi(2) + (pos[1] - around[1]).powi(2) + (pos[2] - around[2]).powi(2)).sqrt();
        if d <= radius && keep(entity, &record) {
            found.push((entity, pos));
        }
    }
    HIGHEST_ID.fetch_max(seen, std::sync::atomic::Ordering::Relaxed);
    found
}

/// Loaded entities with a component of type `type_hash` within `radius` metres of
/// `entity` (players included, `entity` itself too).
pub fn entities_near(entity: u32, radius: f64, type_hash: u32) -> Vec<u32> {
    let Some(around) = position(entity) else { return Vec::new() };
    world_ctx::with(|ctx| unsafe {
        let Some(index) = components::index_of(ctx, type_hash) else { return Vec::new() };
        scan_around(ctx, around, radius, |_, record| record.component(index).is_some()).into_iter().map(|(e, _)| e).collect()
    })
    .unwrap_or_default()
}

/// Turns an entity to look along `yaw` radians around the up axis (orientation quaternion
/// of its current and last transform, at +0x18).
pub fn face(entity: u32, yaw: f64) -> bool {
    let q = [0.0f32, (yaw / 2.0).sin() as f32, 0.0, (yaw / 2.0).cos() as f32];
    world_ctx::with(|ctx| unsafe {
        let record = events::game().entity_record(ctx, entity)?;
        for h in [hash::CURRENT_TRANSFORM, hash::LAST_TRANSFORM] {
            if let Some((addr, _)) = components::of(ctx, &record, h) {
                std::ptr::copy_nonoverlapping(q.as_ptr(), addr.add(0x18) as *mut f32, 4);
            }
        }
        Some(())
    })
    .flatten()
    .is_some()
}

/// Moves an entity to `to` (metres); players see themselves there right away.
pub fn teleport(entity: u32, to: [f64; 3]) -> bool {
    let raw = to.map(|v| (v * UNITS_PER_METRE) as i64);
    let moved = world_ctx::with(|ctx| unsafe {
        let record = events::game().entity_record(ctx, entity)?;
        let (current, _) = components::of(ctx, &record, hash::CURRENT_TRANSFORM)?;
        for transform in [Some(current), components::of(ctx, &record, hash::LAST_TRANSFORM).map(|c| c.0)].into_iter().flatten() {
            std::ptr::copy_nonoverlapping(raw.as_ptr(), transform as *mut i64, 3);
        }
        if let Some((count, _)) = components::of(ctx, &record, hash::TELEPORT_COUNT) {
            *count = (*count).wrapping_add(1);
        }
        Some(())
    })
    .flatten()
    .is_some();
    log!("world: teleport {entity} to [{:.1}, {:.1}, {:.1}]: {}", to[0], to[1], to[2], if moved { "done" } else { "failed" });
    moved
}

/// Copies an entity's component of type `type_hash` (its `qualifiedHash`) into `out`;
/// returns the component's size (0: the entity has none).
pub fn read_component(entity: u32, type_hash: u32, out: &mut [u8]) -> usize {
    world_ctx::with(|ctx| unsafe {
        let record = events::game().entity_record(ctx, entity)?;
        let (addr, size) = components::of(ctx, &record, type_hash)?;
        let n = (size as usize).min(out.len());
        std::ptr::copy_nonoverlapping(addr, out.as_mut_ptr(), n);
        Some(size as usize)
    })
    .flatten()
    .unwrap_or(0)
}

/// Research: entity ids below `max` that have a component of type `type_hash`, with its
/// first bytes.
pub fn scan_component(type_hash: u32, max: u32) -> Vec<(u32, Vec<u8>)> {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        (1..max)
            .filter_map(|entity| {
                let record = game.entity_record(ctx, entity)?;
                let (addr, size) = components::of(ctx, &record, type_hash)?;
                Some((entity, std::slice::from_raw_parts(addr, (size as usize).min(32)).to_vec()))
            })
            .collect()
    })
    .unwrap_or_default()
}

/// Overwrites an entity's component of type `type_hash`; `data` must be its exact size.
pub fn write_component(entity: u32, type_hash: u32, data: &[u8]) -> bool {
    world_ctx::with(|ctx| unsafe {
        let record = events::game().entity_record(ctx, entity)?;
        let (addr, size) = components::of(ctx, &record, type_hash)?;
        (size as usize == data.len()).then(|| std::ptr::copy_nonoverlapping(data.as_ptr(), addr, data.len()))
    })
    .flatten()
    .is_some()
}

/// Entities with an inventory within `radius` metres of `entity` (loaded ones), players
/// excluded.
pub fn inventories_near(entity: u32, radius: f64) -> Vec<u32> {
    let Some(around) = position(entity) else { return Vec::new() };
    // without names: an unknown name key rescans the heap for name strings (~0.5 s)
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        let keep = |entity: u32, _: &EntityRecord| game.inventory_setup(ctx, entity).is_some() && !game.is_player(ctx, entity);
        scan_around(ctx, around, radius, keep).into_iter().map(|(entity, _)| entity).collect()
    })
    .unwrap_or_default()
}

/// The slots of any entity's inventory: (slot id, item, count).
pub fn inventory(entity: u32) -> Vec<(u64, u32, u32)> {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        let Some(setup) = game.inventory_setup(ctx, entity) else { return Vec::new() };
        let mut out = Vec::new();
        for page in game.pages(ctx, setup).pages().iter().filter(|p| p.entity != 0) {
            for slot in 0..crate::game::SLOTS_PER_PAGE {
                let stack = &*page.slots.add(slot as usize);
                out.push((crate::game::slot_id(page.entity, slot), stack.item_id, stack.count));
            }
        }
        out
    })
    .unwrap_or_default()
}

/// Moves a stack between two inventories, outside any player action. A player's
/// equipment, item in hand and action bar are never taken, and never filled.
pub fn move_stack(from: u32, slot: u64, to: u32) -> u32 {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        crate::game_api::move_stack_in(game, ctx, from, slot, to, &protected_of(game, ctx, from))
    })
    .unwrap_or(0)
}

/// Moves `count` items of a stack between two inventories (at most the stack), with the
/// same protection as [`move_stack`].
pub fn move_items(from: u32, slot: u64, to: u32, count: u32) -> u32 {
    world_ctx::with(|ctx| unsafe {
        let game = events::game();
        crate::game_api::move_count_in(game, ctx, from, slot, to, count, &protected_of(game, ctx, from))
    })
    .unwrap_or(0)
}

/// A player's equipment, item in hand and action bar slots; nothing for other entities.
unsafe fn protected_of(game: &crate::game::Game, ctx: *mut crate::game::Ctx, from: u32) -> std::collections::HashSet<u64> {
    let mut protected = std::collections::HashSet::new();
    if game.is_player(ctx, from) {
        let reserved = game.player_reserved(ctx, from);
        protected.extend(reserved.slots);
        for page in reserved.pages.into_iter().filter(|&p| p != 0) {
            protected.extend((0..crate::game::SLOTS_PER_PAGE).map(|s| crate::game::slot_id(page, s)));
        }
    }
    protected
}

// ---- C functions of HostApi v4 ----

pub unsafe extern "C" fn api_players(out: *mut u32, cap: usize) -> usize {
    let players = players();
    std::ptr::copy_nonoverlapping(players.as_ptr(), out, players.len().min(cap));
    players.len()
}

pub unsafe extern "C" fn api_position(entity: u32, out: *mut f64) -> bool {
    match position(entity) {
        Some(p) => {
            std::ptr::copy_nonoverlapping(p.as_ptr(), out, 3);
            true
        }
        None => false,
    }
}

pub unsafe extern "C" fn api_teleport(entity: u32, x: f64, y: f64, z: f64) -> bool {
    teleport(entity, [x, y, z])
}

pub unsafe extern "C" fn api_read_component(entity: u32, type_hash: u32, out: *mut u8, cap: usize) -> usize {
    read_component(entity, type_hash, std::slice::from_raw_parts_mut(out, cap))
}

pub unsafe extern "C" fn api_write_component(entity: u32, type_hash: u32, data: *const u8, len: usize) -> bool {
    write_component(entity, type_hash, std::slice::from_raw_parts(data, len))
}

pub unsafe extern "C" fn api_inventories_near(entity: u32, radius: f64, out: *mut u32, cap: usize) -> usize {
    let found = inventories_near(entity, radius);
    std::ptr::copy_nonoverlapping(found.as_ptr(), out, found.len().min(cap));
    found.len()
}

pub unsafe extern "C" fn api_inventory(entity: u32, out: *mut shroudlink_sdk::Slot, cap: usize) -> usize {
    let slots = inventory(entity);
    for (i, &(id, item, count)) in slots.iter().take(cap).enumerate() {
        *out.add(i) = shroudlink_sdk::Slot { id, item, count };
    }
    slots.len()
}

pub unsafe extern "C" fn api_move_stack(from: u32, slot: u64, to: u32) -> u32 {
    move_stack(from, slot, to)
}

pub unsafe extern "C" fn api_entities_near(entity: u32, radius: f64, type_hash: u32, out: *mut u32, cap: usize) -> usize {
    let found = entities_near(entity, radius, type_hash);
    std::ptr::copy_nonoverlapping(found.as_ptr(), out, found.len().min(cap));
    found.len()
}

pub unsafe extern "C" fn api_move_items(from: u32, slot: u64, to: u32, count: u32) -> u32 {
    move_items(from, slot, to, count)
}

pub unsafe extern "C" fn api_apply_buff(entity: u32, buff: *const u8, seconds: f64) -> bool {
    let mut guid = [0u8; 16];
    std::ptr::copy_nonoverlapping(buff, guid.as_mut_ptr(), 16);
    let keep = if seconds.is_finite() { seconds.clamp(0.0, 3600.0) } else { 0.0 };
    crate::buffs::apply(crate::buffs::Request { target: entity, buff: guid }, std::time::Duration::from_secs_f64(keep))
}

/// The name a player's addon reported, if any.
pub fn player_name(player: u32) -> Option<String> {
    NAMES.lock().unwrap().iter().find(|(p, _)| *p == player).map(|(_, n)| n.clone())
}

pub unsafe extern "C" fn api_player_name(entity: u32, buf: *mut u8, cap: usize) -> usize {
    let Some(name) = player_name(entity) else { return 0 };
    std::ptr::copy_nonoverlapping(name.as_ptr(), buf, name.len().min(cap));
    name.len()
}

pub unsafe extern "C" fn api_chest_name(entity: u32, buf: *mut u8, cap: usize) -> usize {
    let Some(name) = world_ctx::with(|ctx| crate::game_api::name_known(events::game(), ctx, entity)).flatten() else { return 0 };
    std::ptr::copy_nonoverlapping(name.as_ptr(), buf, name.len().min(cap));
    name.len()
}

pub unsafe extern "C" fn api_player_of(client: u64) -> u32 {
    player_of(client).unwrap_or(0)
}
