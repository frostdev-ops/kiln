//! Every place the core hooks or calls in the server, by feature. At startup all of them
//! are looked up; a feature whose hook points are missing (a game update changed that
//! code) is switched off on its own while the others keep working. The result goes to the
//! log, to `shroudlink_hooks.txt` and, as the feature list, to every addon in the welcome.
//!
//! `research/scripts/checksigs.py` checks these signatures against a new game build before
//! anything is deployed.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::sync::OnceLock;

use chestsort_common::log;
use chestsort_common::scan::Module;

pub const REPORT: &str = "shroudlink_hooks.txt";

#[derive(Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Debug)]
pub enum Feature {
    /// the channel to client addons (mods, windows, building blocks)
    Channel,
    /// inventory events for quick stack and Quick Stack Stations, the game API
    Inventory,
    /// inventory events for "move all"
    MoveAll,
    /// a game context outside inventory hooks (self-test, actions like teleport)
    WorldContext,
    /// game events (died, picked up, levelled up, built …) for mods
    GameEvents,
    /// buffs for mods, through the game's impact VM
    Buffs,
    /// creating and removing items for mods (`server/src/items.rs`)
    Items,
}

impl Feature {
    pub fn name(self) -> &'static str {
        match self {
            Feature::Channel => "channel",
            Feature::Inventory => "inventory",
            Feature::MoveAll => "move_all",
            Feature::WorldContext => "world_context",
            Feature::GameEvents => "game_events",
            Feature::Buffs => "buffs",
            Feature::Items => "items",
        }
    }
}

pub struct HookPoint {
    pub name: &'static str,
    pub feature: Feature,
    /// IDA-style pattern at the function entry, unique in `.text`
    pub signature: &'static str,
}

pub const POINTS: &[HookPoint] = &[
    // FUN_140909660(out, strings, oldId, &text): stores a rename sent by a client (the
    // client exe has a second function with the first 12 bytes, hence the longer pattern)
    HookPoint { name: "set_string", feature: Feature::Channel, signature: "48 89 5C 24 10 55 56 57 41 54 41 57 48 83 EC 30" },
    // FUN_1409234b0(strings, &id, &text, &size, slot, gen): next string to send to a slot
    HookPoint { name: "next_pending", feature: Feature::Channel, signature: "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 57 41 54 41 55 41 56 41 57 48 83 EC 20 8B 7C 24 70" },
    // FUN_140942620(strings, id, &text, slot, size): a string was sent
    HookPoint { name: "mark_sent", feature: Feature::Channel, signature: "48 89 74 24 20 57 48 83 EC 20 83 B9 E4 01 00 00 00" },
    // FUN_1401ad1c0(out, ctx, to, from, feedback): quick stack; its calls give the helpers
    HookPoint { name: "quick_stack", feature: Feature::Inventory, signature: "48 89 5C 24 18 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 20 FE FF FF 48 81 EC E0 02 00 00 48 8B 72 18" },
    // FUN_1401ac400(out, ctx, player, station, sound): Quick Stack Station; gives the entity lookup
    HookPoint { name: "area_deposit", feature: Feature::Inventory, signature: "48 89 5C 24 18 48 89 4C 24 08 55 56 57 41 54 41 55 41 56 41 57 48 8D AC 24 20 FE FF FF 48 81 EC E0 02 00 00 4C 8B FA" },
    // FUN_1401ad940(out, ctx, from, to, mode, flag): move all
    HookPoint { name: "move_all", feature: Feature::MoveAll, signature: "48 89 4C 24 08 55 53 56 57 41 54 41 56 48 8D AC 24 A8 FC FF FF" },
    // FUN_14019c480(system): world system that builds an inventory context once after loading
    HookPoint { name: "boot_system", feature: Feature::WorldContext, signature: "48 89 5C 24 18 55 56 57 41 56 41 57 48 8D AC 24 30 8E FF FF" },
    // FUN_1401b2bf0(ctx, descriptor): fills an inventory context
    HookPoint { name: "build_ctx", feature: Feature::WorldContext, signature: "C6 81 A8 71 00 00 00 48 8B 42 08 48 89 01" },
    // FUN_1406ba490(scheduler): dispatches the frame's game events and empties the queues
    HookPoint { name: "dispatch_events", feature: Feature::GameEvents, signature: "40 56 41 55 41 56 48 83 EC 40 48 8B 31 45 33 ED 4C 8B F1 4C 39 A9" },
    // FUN_140182290(out, args, vm): impact native getSelf, called by nearly every program
    HookPoint { name: "impact_get_self", feature: Feature::Buffs, signature: "40 57 48 83 EC 20 48 8B 02 48 8B F9 4D 8B 08 8B 08 85 C9 74 ?? 0F B7 C1 49 8B 48 30 48 89 5C 24 38" },
    // FUN_1401775c0(out, args, vm): impact native applyBuff(buffType, target, …)
    HookPoint { name: "impact_apply_buff", feature: Feature::Buffs, signature: "4C 89 44 24 18 55 56 57 41 54 41 55 41 56 41 57 48 8D 6C 24 80" },
    // FUN_1401ac790(out, ctx, entity, slot, item, count, level?, rarity?, data?): puts items into an inventory
    HookPoint { name: "give_items", feature: Feature::Items, signature: "4C 89 4C 24 20 48 89 54 24 10 48 89 4C 24 08 55 53 56 57 41 54 41 55 41 56 48 8D AC 24 60 FF FF FF" },
    // FUN_1401b3c00(out, ctx, slot, count, clear): takes items out of a slot
    HookPoint { name: "take_items", feature: Feature::Items, signature: "40 53 55 57 41 54 41 56 48 83 EC 60" },
    // FUN_14003aba0(ctx): zeroes an inventory context
    HookPoint { name: "init_ctx", feature: Feature::WorldContext, signature: "33 D2 33 C0 48 89 11 48 89 51 08 48 89 51 10 48 89 51 18 48 89 51 20 48 89 51 28 48 89 51 30 48 89 51 38 48 89 51 40 48 89 51 48 48 89 51 50 48 89 51 58 48 89 51 60 48 89 51 68 48 89 51 70 48 89 51 78 48 89 81 80 00 00 00" },
];

pub struct Registry {
    found: BTreeMap<&'static str, Result<usize, String>>,
    /// features switched off later (e.g. a hook that could not be installed), with the reason
    disabled: BTreeMap<Feature, String>,
}

static REGISTRY: OnceLock<std::sync::Mutex<Registry>> = OnceLock::new();

fn registry() -> std::sync::MutexGuard<'static, Registry> {
    REGISTRY.get().expect("hooks::scan runs first").lock().unwrap_or_else(|e| e.into_inner())
}

/// Looks up every hook point once. The self-test can simulate a game update that broke
/// hook points: lines `break <name>` in its marker file make them missing.
pub fn scan(module: &Module) {
    let marker = std::fs::read_to_string(chestsort_common::game_dir().join(crate::selftest::MARKER)).unwrap_or_default();
    let broken: Vec<&str> = marker.lines().filter_map(|l| l.trim().strip_prefix("break ")).map(str::trim).collect();
    let found = POINTS
        .iter()
        .map(|p| (p.name, if broken.contains(&p.name) { Err("broken by the self-test".to_string()) } else { module.find(p.signature) }))
        .collect();
    let _ = REGISTRY.set(std::sync::Mutex::new(Registry { found, disabled: BTreeMap::new() }));
}

/// Address of a hook point, if it was found.
pub fn address(name: &str) -> Result<usize, String> {
    registry().found.get(name).cloned().unwrap_or_else(|| Err(format!("unknown hook point {name}")))
}

/// Why a feature is off, or None when all its hook points were found and it was not disabled.
pub fn problem(feature: Feature) -> Option<String> {
    let r = registry();
    if let Some(reason) = r.disabled.get(&feature) {
        return Some(reason.clone());
    }
    let missing: Vec<String> = POINTS.iter().filter(|p| p.feature == feature).filter_map(|p| r.found[p.name].as_ref().err().map(|e| format!("{}: {e}", p.name))).collect();
    (!missing.is_empty()).then(|| missing.join("; "))
}

pub fn available(feature: Feature) -> bool {
    problem(feature).is_none()
}

/// Switches a feature off (its hook points were found, but something else failed).
pub fn disable(feature: Feature, reason: String) {
    log!("feature '{}' off: {reason}", feature.name());
    registry().disabled.insert(feature, reason);
}

pub const FEATURES: &[Feature] = &[Feature::Channel, Feature::Inventory, Feature::MoveAll, Feature::GameEvents, Feature::WorldContext, Feature::Buffs, Feature::Items];

/// The features that work, for addons and mods (none before the scan, e.g. in unit tests).
pub fn working() -> Vec<&'static str> {
    if REGISTRY.get().is_none() {
        return Vec::new();
    }
    FEATURES.iter().filter(|&&f| available(f)).map(|f| f.name()).collect()
}

/// Logs the result and writes `shroudlink_hooks.txt`.
pub fn report(module: &Module) {
    let mut text = String::new();
    for &feature in FEATURES {
        match problem(feature) {
            None => {
                let _ = writeln!(text, "ON  {}", feature.name());
            }
            Some(why) => {
                let _ = writeln!(text, "OFF {}: {why}", feature.name());
            }
        }
    }
    let r = registry();
    for p in POINTS {
        let _ = match &r.found[p.name] {
            Ok(addr) => writeln!(text, "    {:<14} {:<10} +{:#x}", p.name, p.feature.name(), module.rva(*addr)),
            Err(e) => writeln!(text, "    {:<14} {:<10} {e}", p.name, p.feature.name()),
        };
    }
    drop(r);
    let _ = std::fs::write(chestsort_common::game_dir().join(REPORT), &text);
    log!("features: {}", FEATURES.iter().map(|&f| format!("{} {}", f.name(), if available(f) { "on" } else { "OFF" })).collect::<Vec<_>>().join(", "));
}
