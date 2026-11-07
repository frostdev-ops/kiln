//! Game events for mods: every event the server's ECS raises, read from the game's own
//! event queues right before the game dispatches them.
//!
//! Events are reserved with `FUN_1405f6bb0(queue, typeHash, size, extra)`, which appends
//! a record to a per-thread queue `{ data, capacity, used: u32 }` (0x18 bytes):
//! `[u32 typeHash][u16 extra][u8 size][u8 0][payload, padded to 8]`. The caller fills the
//! payload afterwards. Once per frame `FUN_1406ba490(scheduler)` walks all queues
//! (`scheduler + 0xeb9ef0`: array, `+ 0xeb9ef8`: count, read from its own instruction),
//! calls the handlers registered per type and empties them; we read the queues on entry,
//! when every record is complete. Found with a hardware write watchpoint on a queue's
//! `used` field (the only code that sets it to 0).

use std::sync::OnceLock;

use chestsort_common::log;
use retour::GenericDetour;

use crate::hooks;
use crate::mods;

type DispatchFn = unsafe extern "system" fn(scheduler: *mut u8);

static HOOK: OnceLock<GenericDetour<DispatchFn>> = OnceLock::new();
/// Offset of the queue count in the scheduler; the array pointer sits 8 bytes before it.
static COUNT_OFFSET: OnceLock<usize> = OnceLock::new();
/// `cmp qword ptr [rcx + disp32], r13` at this offset of the dispatcher holds the count offset.
const COUNT_DISP_AT: usize = 0x16;
const QUEUE_SIZE: usize = 0x18;
/// Records larger than this are not events (the allocator refuses them).
const MAX_PAYLOAD: usize = 0x100;

/// The records of one queue's bytes: (type hash, payload).
pub fn records(bytes: &[u8]) -> Vec<(u32, &[u8])> {
    let mut out = Vec::new();
    let mut at = 0;
    while at + 8 <= bytes.len() {
        let hash = u32::from_le_bytes(bytes[at..at + 4].try_into().unwrap());
        let size = bytes[at + 6] as usize;
        let padded = (size + 7) & !7;
        if size >= MAX_PAYLOAD || at + 8 + padded > bytes.len() {
            break; // not a record layout we know: stop rather than misread
        }
        out.push((hash, &bytes[at + 8..at + 8 + size]));
        at += 8 + padded;
    }
    out
}

/// Research: with a `shroudlink_probe` file, log each player's component types at login.
pub fn probing() -> bool {
    static PROBE: OnceLock<bool> = OnceLock::new();
    *PROBE.get_or_init(|| chestsort_common::game_dir().join("shroudlink_probe").exists())
}

/// Logins feed the core's player list; with a probe file the components are logged too.
fn track_logins(events: &[(u32, &[u8])]) {
    let login = shroudlink_sdk::events::hash_of("LoginEvent");
    for (hash, data) in events {
        if Some(*hash) != login || data.len() < 4 {
            continue;
        }
        let player = u32::from_le_bytes(data[..4].try_into().unwrap());
        crate::world::on_login(player);
        if !probing() {
            continue;
        }
        crate::world_ctx::with(|ctx| unsafe {
            let game = crate::events::game();
            if let Some(record) = game.entity_record(ctx, player) {
                let list: Vec<String> = (0..crate::game::MAX_COMPONENT_TYPES).filter_map(|i| record.component(i).map(|(_, size)| format!("{i}:{size}"))).collect();
                log!("probe: player {player} components {}", list.join(" "));
                let words: Vec<String> = (0..0x20usize)
                    .map(|i| {
                        let w = *(ctx as *const usize).add(i);
                        let idx = if w > 0x10000 { *(w as *const u16) as i64 } else { -1 };
                        format!("{i:#x}={w:#x}/{idx}")
                    })
                    .collect();
                log!("probe: ctx {}", words.join(" "));
            }
        });
    }
}

/// Research (probe file): which event types go by, logged every 5 s.
fn count_types(events: &[(u32, &[u8])]) {
    static SEEN: std::sync::Mutex<Option<(std::time::Instant, std::collections::BTreeMap<u32, u32>)>> = std::sync::Mutex::new(None);
    let mut seen = SEEN.lock().unwrap();
    let (since, counts) = seen.get_or_insert_with(|| (std::time::Instant::now(), Default::default()));
    for (hash, _) in events {
        *counts.entry(*hash).or_default() += 1;
    }
    if since.elapsed() >= std::time::Duration::from_secs(5) && !counts.is_empty() {
        let list: Vec<String> = counts.iter().map(|(h, n)| format!("{}:{n}", shroudlink_sdk::events::name_of(*h).unwrap_or("?"))).collect();
        log!("probe: events {}", list.join(" "));
        *seen = None;
    }
}

unsafe extern "system" fn dispatch_hook(scheduler: *mut u8) {
    // always read: logins feed the player list
    {
        let offset = *COUNT_OFFSET.get().unwrap();
        let count = *(scheduler.add(offset) as *const usize);
        let array = *(scheduler.add(offset - 8) as *const *const u8);
        let mut events = Vec::new();
        for i in 0..count.min(256) {
            let queue = array.add(i * QUEUE_SIZE);
            let data = *(queue as *const *const u8);
            let used = *(queue.add(0x10) as *const u32) as usize;
            if data.is_null() || used == 0 {
                continue;
            }
            let all = records(std::slice::from_raw_parts(data, used));
            track_logins(&all);
            if probing() {
                count_types(&all);
            }
            events.extend(all.into_iter().filter(|(hash, _)| mods::wants_game_event(*hash)));
        }
        if !events.is_empty() {
            mods::game_events(&events);
        }
    }
    HOOK.get().unwrap().call(scheduler)
}

pub fn install() -> Result<(), String> {
    let target = hooks::address("dispatch_events")?;
    unsafe {
        let disp = *((target + COUNT_DISP_AT) as *const u32) as usize;
        let _ = COUNT_OFFSET.set(disp);
        let hook = GenericDetour::<DispatchFn>::new(std::mem::transmute::<usize, DispatchFn>(target), dispatch_hook).map_err(|e| e.to_string())?;
        hook.enable().map_err(|e| e.to_string())?;
        let _ = HOOK.set(hook);
        log!("game events: dispatcher hooked, queues at scheduler +{:#x}", disp - 8);
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn record(hash: u32, payload: &[u8]) -> Vec<u8> {
        let mut r = hash.to_le_bytes().to_vec();
        r.extend_from_slice(&[0, 0, payload.len() as u8, 0]);
        r.extend_from_slice(payload);
        r.resize(8 + ((payload.len() + 7) & !7), 0);
        r
    }

    #[test]
    fn queue_records_are_read_in_order() {
        let mut queue = record(0xd8ec982a, &[1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
        queue.extend(record(0x554be244, &[7, 0, 0, 0]));
        let got = records(&queue);
        assert_eq!(got.len(), 2);
        assert_eq!(got[0].0, 0xd8ec982a);
        assert_eq!(got[0].1.len(), 16);
        assert_eq!(got[1], (0x554be244, &[7u8, 0, 0, 0][..]));
    }

    #[test]
    fn a_broken_queue_stops_reading() {
        let mut queue = record(1, &[0; 8]);
        queue.extend_from_slice(&[9, 9, 9, 9, 0, 0, 200, 0]); // claims 200 bytes that are not there
        assert_eq!(records(&queue).len(), 1);
    }
}
