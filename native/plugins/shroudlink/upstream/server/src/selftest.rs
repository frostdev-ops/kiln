//! Self-test inside the real dedicated server, without any player (research/scripts/selftest.py
//! runs it). Active only with a `shroudlink_selftest` file next to the server; the result
//! goes to `shroudlink_selftest.txt`.
//!
//! Inventory functions need a game context, which the game only builds while it works on
//! inventories. Without players that happens once: `FUN_14019c480`, a world system that
//! runs after the world has loaded, builds one from world-level storages
//! (`FUN_14003aba0` init + `FUN_1401b2bf0(ctx, descriptor)`). We copy its descriptor, and
//! when the system returns we build our own context from it and run the tests there, on
//! the game thread.
//!
//! The tests change the world (items move): the runner restores the savegame afterwards.

use std::fmt::Write as _;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Mutex;

use chestsort_common::log;
use serde_json::{json, Value};

use crate::events;
use crate::hooks::{self, Feature};
use crate::game::{slot_id, Ctx, DepositResult, Game, FAKE_PLAYER, SLOTS_PER_PAGE};
use crate::link_host;
use crate::mods;

pub const MARKER: &str = "shroudlink_selftest";
const REPORT: &str = "shroudlink_selftest.txt";


/// Entity ids searched for inventories (the lookup is a bounds-checked hash map).
const MAX_ENTITY: u32 = 2_000_000;
/// Player slot used by the fake client addon session.
const FAKE_SLOT: usize = link_host::SLOTS - 1;
const FAKE_CLIENT: u64 = 0x5e1f_7e57;

static DONE: AtomicBool = AtomicBool::new(false);

/// A name the test gives one inventory (chests near players only load with a player).
static FAKE_NAME: Mutex<Option<(u32, String)>> = Mutex::new(None);

/// The test name of an entity, if the self-test gave it one.
pub fn fake_name(entity: u32) -> Option<String> {
    FAKE_NAME.lock().unwrap().as_ref().filter(|(e, _)| *e == entity).map(|(_, n)| n.clone())
}

pub fn install() {
    if !chestsort_common::game_dir().join(MARKER).exists() {
        return;
    }
    let _ = std::fs::remove_file(chestsort_common::game_dir().join(REPORT));
    for feature in [Feature::WorldContext, Feature::Inventory] {
        if let Some(why) = hooks::problem(feature) {
            write_report(&format!("FAIL setup: feature {} is off ({why})\nRESULT setup\n", feature.name()));
            return;
        }
    }
    log!("selftest: armed, runs once the world has loaded");
    crate::world_ctx::on_ready(|| {
        if !DONE.swap(true, Ordering::SeqCst) {
            unsafe { crate::world_ctx::with(|ctx| run(ctx)) };
        }
    });
}

fn write_report(text: &str) {
    let _ = std::fs::write(chestsort_common::game_dir().join(REPORT), text);
    for line in text.lines() {
        log!("selftest: {line}");
    }
}

struct Report {
    text: String,
    failed: usize,
}

impl Report {
    fn check(&mut self, name: &str, result: Result<String, String>) {
        let line = match &result {
            Ok(detail) => format!("PASS {name}: {detail}"),
            Err(detail) => {
                self.failed += 1;
                format!("FAIL {name}: {detail}")
            }
        };
        let _ = writeln!(self.text, "{line}");
    }
    fn skip(&mut self, name: &str, why: &str) {
        let _ = writeln!(self.text, "SKIP {name}: {why}");
    }
}

/// Inventory slots of an entity: (slot id, item, count).
unsafe fn slots(game: &Game, ctx: *mut Ctx, entity: u32) -> Vec<(u64, u32, u32)> {
    let Some(setup) = game.inventory_setup(ctx, entity) else { return Vec::new() };
    let mut out = Vec::new();
    for page in game.pages(ctx, setup).pages().iter().filter(|p| p.entity != 0) {
        for slot in 0..SLOTS_PER_PAGE {
            let stack = &*page.slots.add(slot as usize);
            out.push((slot_id(page.entity, slot), stack.item_id, stack.count));
        }
    }
    out
}

fn items(slots: &[(u64, u32, u32)]) -> u32 {
    slots.iter().map(|s| s.2).sum()
}

/// Messages the fake addon session received since the last call.
fn received() -> Vec<Value> {
    link_host::with_host(|host| host.take_messages(FAKE_SLOT, FAKE_CLIENT))
}

fn send(message: Value) -> Vec<Value> {
    link_host::with_host(|host| host.deliver_test_message(FAKE_CLIENT, &message));
    received()
}

fn ui_event(window: &str, node: &str, event: Value) -> Value {
    json!({ "mod": "chestsort", "op": "ui_event", "event": { "window": window, "node": node, "event": event } })
}

unsafe fn run(ctx: *mut Ctx) {
    let mut report = Report { text: String::new(), failed: 0 };
    let _ = writeln!(report.text, "shroudlink selftest {}", env!("CARGO_PKG_VERSION"));

    let game = events::game();

    // ---- hook registry ----
    let off: Vec<String> = hooks::FEATURES.iter().filter_map(|&f| hooks::problem(f).map(|why| format!("{} ({why})", f.name()))).collect();
    let marker = std::fs::read_to_string(chestsort_common::game_dir().join(MARKER)).unwrap_or_default();
    if marker.contains("break ") {
        let _ = writeln!(report.text, "INFO simulated broken hook points: {}", marker.lines().filter_map(|l| l.strip_prefix("break ")).collect::<Vec<_>>().join(", "));
        report.check("only broken features are off", Ok(format!("on: {}; off: {}", hooks::working().join(", "), off.join("; "))));
    } else {
        report.check("hook points", if off.is_empty() { Ok(format!("{} found, features: {}", hooks::POINTS.len(), hooks::working().join(", "))) } else { Err(format!("off: {}", off.join("; "))) });
    }

    // ---- mods ----
    let ids = mods::ids();
    report.check(
        "mods loaded",
        if ids.iter().any(|i| i == "chestsort") { Ok(ids.join(", ")) } else { Err(format!("chestsort missing, loaded: {ids:?}")) },
    );

    // ---- channel: a client addon session on a free player slot ----
    link_host::with_host(|host| host.connect_fake(FAKE_SLOT, FAKE_CLIENT));
    let welcome = received();
    report.check(
        "addon session",
        match welcome.iter().find(|m| m["op"] == "welcome") {
            Some(w) => Ok(format!("welcome lists {}", w["mods"])),
            None => Err(format!("no welcome, got {welcome:?}")),
        },
    );
    report.check(
        "slot key offered",
        if welcome.iter().any(|m| m["op"] == "slot_keys" && m["keys"][0]["key"] == "L") { Ok("chestsort registers L".into()) } else { Err("no slot_keys from chestsort".into()) },
    );
    report.check(
        "MODS menu entry",
        if welcome.iter().any(|m| m["op"] == "entries" && m["owner"] == "chestsort") { Ok("chestsort offers its menu".into()) } else { Err("no chestsort entries".into()) },
    );
    report.check(
        "backpack section offered",
        if welcome.iter().any(|m| m["op"] == "section" && m["owner"] == "hello") { Ok("hello adds its Wave row".into()) } else { Err("no section from hello".into()) },
    );
    let assistants = send(json!({ "mod": "assistants", "op": "ui_event", "event": { "window": "", "node": "open", "event": { "kind": "menu" } } }));
    report.check(
        "assistants window",
        if welcome.iter().any(|m| m["op"] == "entries" && m["owner"] == "assistants") && assistants.iter().any(|m| m["op"] == "open" && m["window"]["title"] == "Assistants") {
            Ok("menu entry, and the window opens".into())
        } else {
            Err(format!("got {assistants:?}"))
        },
    );
    let wave = send(json!({ "mod": "hello", "op": "ui_event", "event": { "window": "wave", "node": "wave", "event": { "kind": "click" } } }));
    report.check("notification", if wave.iter().any(|m| m["op"] == "notify") { Ok("Wave answers with a HUD message".into()) } else { Err(format!("got {wave:?}")) });
    let open = send(ui_event("", "categories", json!({ "kind": "menu" })));
    let lists = open.iter().any(|m| m["op"] == "open" && m["window"]["content"].to_string().contains("\"list\""));
    report.check("chest categories window", if lists { Ok("opens with the category list".into()) } else { Err(format!("got {open:?}")) });
    let found = send(ui_event("categories", "search", json!({ "kind": "submit", "value": "iron bar" })));
    let ores = found.iter().any(|m| m["window"]["content"].to_string().contains("found:Ores"));
    report.check("item search", if ores { Ok("\"iron bar\" belongs to Ores".into()) } else { Err(format!("got {found:?}")) });

    // ---- inventories of the world ----
    let mut inventories: Vec<(u32, Option<String>, Vec<(u64, u32, u32)>)> = Vec::new();
    for entity in 1..=MAX_ENTITY {
        if game.entity_record(ctx, entity).is_none() || game.inventory_setup(ctx, entity).is_none() {
            continue;
        }
        let name = crate::game_api::name_of(game, ctx, entity);
        inventories.push((entity, name, slots(game, ctx, entity)));
    }
    let named: Vec<&(u32, Option<String>, Vec<(u64, u32, u32)>)> = inventories.iter().filter(|i| i.1.is_some()).collect();
    report.check(
        "world inventories",
        if inventories.is_empty() {
            Err("no entity with an inventory found".into())
        } else {
            Ok(format!("{} inventories, {} named: {}", inventories.len(), named.len(), named.iter().map(|i| format!("{} '{}'", i.0, i.1.as_deref().unwrap_or(""))).collect::<Vec<_>>().join(", ")))
        },
    );

    for (entity, name, slots) in &inventories {
        let _ = writeln!(
            report.text,
            "INFO inventory {entity} {:?}: {} slot(s), {} stack(s), {} item(s), player: {}",
            name.as_deref().unwrap_or(""),
            slots.len(),
            slots.iter().filter(|s| s.2 != 0).count(),
            items(slots),
            game.is_player(ctx, *entity)
        );
    }

    // two inventories serve as test player and test chest; chestsort tells us the item ids
    let found = |query: &str| -> Option<u32> {
        let reply = send(ui_event("categories", "search", json!({ "kind": "submit", "value": query })));
        reply.iter().find_map(|m| {
            m["window"]["content"].as_array()?.iter().find(|n| n["type"] == "item_grid" && n["id"].as_str().is_some_and(|id| id.starts_with("found:")))?["items"][0]["id"].as_u64()
        }).map(|id| id as u32)
    };
    match (inventories.len() >= 2, found("iron bar"), found("plant fibre")) {
        (true, Some(ore), Some(fibre)) => inventory_tests(&mut report, game, ctx, inventories[0].0, inventories[1].0, ore, fibre),
        (false, ..) => report.skip("inventory events", "fewer than two inventories in the world"),
        _ => report.skip("inventory events", "chestsort did not name test items"),
    }

    // ---- tick: every v5 mod gets it ----
    let before = crate::plugins::TICKS.load(Ordering::SeqCst);
    link_host::with_host(crate::mods::tick_now);
    let ticked = crate::plugins::TICKS.load(Ordering::SeqCst) - before;
    report.check("mod tick", if ticked >= 3 { Ok(format!("{ticked} v5 mod(s) got on_tick")) } else { Err(format!("{ticked} mod(s) ticked")) });

    // ---- hot reload ----
    match crate::plugins::touch_for_test("chestsort") {
        Ok(()) => {
            link_host::with_host(|host| crate::plugins::reload_now(host));
            let again = received();
            let reopened = again.iter().any(|m| m["op"] == "entries" && m["owner"] == "chestsort");
            report.check("hot reload", if reopened { Ok("chestsort reloaded and greeted the session again".into()) } else { Err(format!("after reload got {again:?}")) });
        }
        Err(e) => report.skip("hot reload", &e),
    }

    let _ = writeln!(report.text, "{}", if report.failed == 0 { "RESULT ok".to_string() } else { format!("RESULT {} failure(s)", report.failed) });
    write_report(&report.text);
}

/// Research lines for the assistants mod: every loaded production station and NPC with
/// the state the mod reads (`ecs::FactoryStation`, `ecs::Crafting`, `ecs::NetworkFuel`,
/// `ecs::ActiveNpcState`). Without players only the world around spawn is loaded.
fn research_production(report: &mut Report, around: u32) {
    let hash = |name: &str| shroudlink_sdk::components::hash_of(name).unwrap_or(0);
    let read = |entity: u32, name: &str| {
        let mut data = [0u8; 512];
        let size = crate::world::read_component(entity, hash(name), &mut data);
        (size != 0).then(|| data[..size.min(512)].to_vec())
    };
    let u32_at = |data: &[u8], at: usize| u32::from_le_bytes(data[at..at + 4].try_into().unwrap());
    let started = std::time::Instant::now();
    let stations = crate::world::entities_near(around, 1e7, hash("FactoryStation"));
    let _ = writeln!(report.text, "INFO entities_near scans the loaded world in {:.1} ms", started.elapsed().as_secs_f64() * 1000.0);
    let npcs = crate::world::entities_near(around, 1e7, hash("ActiveNpcState"));
    let _ = writeln!(report.text, "INFO production: {} station(s), {} NPC(s) loaded", stations.len(), npcs.len());
    for &station in stations.iter().take(20) {
        let Some(f) = read(station, "FactoryStation") else { continue };
        let workshop = read(station, "Crafting").map_or(0, |c| u32_at(&c, 0x10));
        let fuel = read(station, "NetworkFuel").map(|n| format!("{}/{}", u16::from_le_bytes([n[0], n[1]]), u16::from_le_bytes([n[2], n[3]])));
        let stacks: Vec<String> = crate::world::inventory(station).iter().filter(|s| s.2 != 0).map(|s| format!("{}x{}", s.2, s.1)).collect();
        let _ = writeln!(
            report.text,
            "INFO station {station}: workshop {workshop}, state {}, running {}, next {}, stopped {}, missing {:#04x}, fuel {}, holds [{}]",
            f[0], u32_at(&f, 4), u32_at(&f, 0x28), f[0x61], f[0x62], fuel.as_deref().unwrap_or("-"), stacks.join(" ")
        );
    }
    for &npc in npcs.iter().take(20) {
        let id = read(npc, "ActiveNpcState").map_or(0, |a| u32_at(&a, 0));
        let workshop = read(npc, "Crafting").map_or(0, |c| u32_at(&c, 0x10));
        let _ = writeln!(report.text, "INFO npc {npc}: npc id {id}, workshop {workshop}, at {:?}", crate::world::position(npc).map(|p| p.map(|v| v.round())));
    }
}

/// Writes a stack straight into an inventory slot (test setup only).
unsafe fn put(game: &Game, ctx: *mut Ctx, entity: u32, index: usize, item: u32, count: u32) -> Option<u64> {
    let setup = game.inventory_setup(ctx, entity)?;
    let pages = game.pages(ctx, setup);
    let page = pages.pages().iter().filter(|p| p.entity != 0).nth(index / SLOTS_PER_PAGE as usize)?;
    let stack = page.slots.add(index % SLOTS_PER_PAGE as usize);
    (*stack).item_id = item;
    (*stack).count = count;
    (*stack).pide = 0;
    Some(slot_id(page.entity, (index % SLOTS_PER_PAGE as usize) as u8))
}

fn count(slots: &[(u64, u32, u32)], item: u32) -> u32 {
    slots.iter().filter(|s| s.1 == item).map(|s| s.2).sum()
}

/// Quick stack and move all from `player` (a test player) into `chest` (named "Ores" for
/// the test): chestsort sorts by category, locks hold, the game's own transfer runs.
unsafe fn inventory_tests(report: &mut Report, game: &Game, ctx: *mut Ctx, player: u32, chest: u32, ore: u32, fibre: u32) {
    FAKE_PLAYER.store(player, Ordering::SeqCst);
    *FAKE_NAME.lock().unwrap() = Some((chest, "Ores".into()));
    let max = game.item_info(ctx, ore).map_or(0, |i| i.max_stack as u32);
    let _ = writeln!(report.text, "INFO test player {player}, test chest {chest} named \"Ores\"; iron bar {ore} (max stack {max}), plant fibre {fibre}");
    let setup = (put(game, ctx, player, 0, ore, 5), put(game, ctx, player, 1, ore, 7), put(game, ctx, player, 2, fibre, 3));
    let (Some(locked), Some(_), Some(_)) = setup else {
        report.skip("inventory events", "could not set up test stacks");
        return;
    };
    // the addon reports the backpack layout and presses L over the first test stack
    let mut pages: Vec<u32> = Vec::new();
    for (slot, ..) in slots(game, ctx, player) {
        if !pages.contains(&(slot as u32)) {
            pages.push(slot as u32);
        }
    }
    let position = [pages.iter().position(|&p| p == locked as u32).unwrap_or(0) as u32, (locked >> 32) as u32];
    send(json!({ "mod": "chestsort", "op": "layout", "pages": pages }));
    let reply = send(json!({ "mod": "chestsort", "op": "slot_key", "key": "L", "slot": locked, "position": position, "item": ore }));
    let stored = reply.iter().find(|m| m["op"] == "store").map(|m| m["data"]["locked"].clone());
    let marked = reply.iter().rev().find(|m| m["op"] == "marks").map(|m| m["marks"][0]["slots"].clone());
    report.check("L stores the lock on the PC", if stored == Some(json!([position])) { Ok(format!("stored position {position:?}")) } else { Err(format!("store message: {stored:?}")) });
    report.check("L marks the slot", if marked == Some(json!([locked])) { Ok("rim on the locked slot".into()) } else { Err(format!("marks message: {marked:?}")) });
    let locked_intact = |game: &Game| slots(game, ctx, player).iter().any(|s| *s == (locked, ore, 5));

    let mut out: DepositResult = std::mem::zeroed();
    let result = (game.deposit_into_chest)(&mut out, ctx, chest, player, std::ptr::null_mut());
    let (p, c) = (slots(game, ctx, player), slots(game, ctx, chest));
    let _ = writeln!(report.text, "INFO after quick stack: player ore {} fibre {}, chest ore {} fibre {}, error {}", count(&p, ore), count(&p, fibre), count(&c, ore), count(&c, fibre), result.as_ref().map_or(255, |r| r.error));
    report.check("quick stack sorts by category", if count(&c, ore) == 7 { Ok("7 iron bars went into the chest named Ores".into()) } else { Err(format!("chest holds {} iron bar(s), expected 7", count(&c, ore))) });
    report.check("quick stack leaves other items", if count(&p, fibre) == 3 && count(&c, fibre) == 0 { Ok("plant fibre stayed with the player".into()) } else { Err(format!("player fibre {}, chest fibre {}", count(&p, fibre), count(&c, fibre))) });
    report.check("quick stack keeps locked slot", if locked_intact(game) { Ok("5 locked iron bars stayed".into()) } else { Err("the locked stack changed".into()) });

    match game.move_all {
        None => report.skip("move all", "feature move_all is off"),
        Some(move_all) => {
            let mut out: DepositResult = std::mem::zeroed();
            move_all(&mut out, ctx, player, chest, 0, 0);
            let (p, c) = (slots(game, ctx, player), slots(game, ctx, chest));
            let _ = writeln!(report.text, "INFO after move all: player ore {} fibre {}, chest ore {} fibre {}", count(&p, ore), count(&p, fibre), count(&c, ore), count(&c, fibre));
            report.check("move all moves the rest", if count(&c, fibre) == 3 { Ok("plant fibre went into the chest".into()) } else { Err(format!("chest holds {} plant fibre, expected 3", count(&c, fibre))) });
            report.check("move all keeps locked slot", if locked_intact(game) { Ok("5 locked iron bars stayed".into()) } else { Err("the locked stack changed".into()) });
        }
    }

    let reply = send(json!({ "mod": "chestsort", "op": "slot_key", "key": "L", "slot": locked, "position": position, "item": ore }));
    let cleared = reply.iter().rev().find(|m| m["op"] == "marks").map(|m| m["marks"][0]["slots"].clone());
    report.check("L again unlocks", if cleared == Some(json!([])) { Ok("rim removed".into()) } else { Err(format!("marks message: {cleared:?}")) });

    // ---- world access for mods (HostApi v4), outside any player action ----
    let slots_seen = crate::world::inventory(chest);
    report.check("world inventory", if slots_seen.len() >= 8 { Ok(format!("{} slot(s) in the test chest", slots_seen.len())) } else { Err(format!("{} slot(s)", slots_seen.len())) });
    let ores_in_chest = |s: &[(u64, u32, u32)]| s.iter().filter(|x| x.1 == ore).map(|x| x.2).sum::<u32>();
    let before = ores_in_chest(&crate::world::inventory(chest));
    let moved = put(game, ctx, player, 3, ore, 4).map_or(0, |slot| crate::world::move_stack(player, slot, chest));
    let after = ores_in_chest(&crate::world::inventory(chest));
    report.check("world move_stack", if moved == 4 && after == before + 4 { Ok(format!("4 iron bars moved, chest {before} -> {after}")) } else { Err(format!("moved {moved}, chest {before} -> {after}")) });
    let mut transform = [0u8; 64];
    let size = crate::world::read_component(chest, crate::components::hash::CURRENT_TRANSFORM, &mut transform);
    report.check("world component read", if size == 56 { Ok(format!("CurrentTransform of the chest, {size} bytes")) } else { Err(format!("size {size}")) });

    // ---- HostApi v6: exact counts, entities by component ----
    let before = ores_in_chest(&crate::world::inventory(chest));
    let source = put(game, ctx, player, 4, ore, 9);
    let moved = source.map_or(0, |slot| crate::world::move_items(player, slot, chest, 3));
    let after = ores_in_chest(&crate::world::inventory(chest));
    let left = source.and_then(|slot| crate::world::inventory(player).into_iter().find(|s| s.0 == slot)).map_or(0, |s| s.2);
    report.check(
        "world move_items",
        if moved == 3 && after == before + 3 && left == 6 { Ok(format!("3 of 9 iron bars moved, chest {before} -> {after}, 6 left")) } else { Err(format!("moved {moved}, chest {before} -> {after}, {left} left")) },
    );
    let setup_hash = shroudlink_sdk::components::hash_of("InventorySetup").unwrap_or(0);
    let near = crate::world::entities_near(chest, 0.5, setup_hash);
    report.check("world entities_near", if near.contains(&chest) { Ok(format!("{} inventory entity(s) within 0.5 m of the test chest", near.len())) } else { Err(format!("found {near:?}")) });
    // ---- HostApi v7: items made and taken by the game's own functions ----
    if crate::hooks::available(crate::hooks::Feature::Items) {
        let fibres = |s: &[(u64, u32, u32)]| s.iter().filter(|x| x.1 == fibre).map(|x| x.2).sum::<u32>();
        let before = fibres(&crate::world::inventory(chest));
        let given = crate::items::give(chest, fibre, 5);
        let after = fibres(&crate::world::inventory(chest));
        report.check("give_items", if given == 5 && after == before + 5 { Ok(format!("5 plant fibre created, chest {before} -> {after}")) } else { Err(format!("gave {given}, chest {before} -> {after}")) });
        let slot = crate::world::inventory(chest).into_iter().find(|s| s.1 == fibre && s.2 >= 2).map(|s| s.0);
        let removed = slot.map_or(0, |s| crate::items::remove(chest, s, 2));
        let left = fibres(&crate::world::inventory(chest));
        report.check("remove_items", if removed == 2 && left == after - 2 { Ok(format!("2 removed, chest {after} -> {left}")) } else { Err(format!("removed {removed}, chest {after} -> {left}")) });
        let refused = crate::items::give(player, fibre, 1) == 0 || !game.is_player(ctx, player);
        report.check("give_items refuses players", if refused { Ok("nothing created in the test player".into()) } else { Err("created items in a player".into()) });
    } else {
        report.skip("give_items", "feature items is off");
    }
    // ---- HostApi v8: chest names from the world, outside an inventory event ----
    let name_of = |entity: u32| {
        let mut buf = [0u8; 64];
        let n = crate::world::api_chest_name(entity, buf.as_mut_ptr(), buf.len());
        String::from_utf8_lossy(&buf[..n.min(buf.len())]).into_owned()
    };
    let (named, unnamed) = (name_of(chest), name_of(player));
    report.check("world chest_name", if named == "Ores" && unnamed.is_empty() { Ok("the test chest is \"Ores\", the player has no name".into()) } else { Err(format!("chest {named:?}, player {unnamed:?}")) });
    research_production(report, chest);
    // buffs need a running impact program, which needs a real player nearby
    let _ = writeln!(report.text, "INFO buffs: feature {}, getSelf ran {} time(s) so far", if crate::hooks::available(crate::hooks::Feature::Buffs) { "on" } else { "OFF" }, crate::buffs::calls());
    FAKE_PLAYER.store(0, Ordering::SeqCst);
    *FAKE_NAME.lock().unwrap() = None;
}
