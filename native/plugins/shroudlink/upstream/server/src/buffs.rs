//! Buffs for mods, through the game's own impact VM.
//!
//! Impact programs (item, buff and ability scripts) call natives registered by name in
//! `FUN_14022e680`; each native is `fn(out, args, vm) -> out`. `args` points at a list of
//! u32 variable indices (0: argument not given), `vm + 0x00` at the variable layout
//! (`keen::ImpactVariable`, 0x18 bytes, `offsetInBytes` at +0xe), `vm + 0x10` at the
//! variable data and `vm + 0x30` at the context of the program that is running (the
//! entity it runs for, component storages). `applyBuff(buffType, target, …)`
//! (`FUN_1401775c0`) hashes the buff's Guid, looks it up in the buff registry and applies
//! it to `target`; `out[8]` is 1 when it was applied.
//!
//! That context exists only while a program runs, so requests are queued and applied from
//! inside `getSelf` (`FUN_140182290`), a native nearly every program calls: with the
//! running program's context and our own arguments, the same way a program would.
//!
//! Buffs that items keep up last only a moment (the Ring of Light's: 0.3 s, `keen::Time`
//! is in nanoseconds) and are re-applied by the item's program every 0.1 s. A kept buff
//! is re-applied the same way, every [`REAPPLY`], until its time is up.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use chestsort_common::log;
use retour::GenericDetour;

use crate::hooks;

type NativeFn = unsafe extern "system" fn(out: *mut u8, args: *const *const u32, vm: *mut u8) -> *mut u8;

struct Hooks {
    get_self: GenericDetour<NativeFn>,
    apply_buff: NativeFn,
}
unsafe impl Send for Hooks {}
unsafe impl Sync for Hooks {}

static HOOKS: OnceLock<Hooks> = OnceLock::new();
/// buffs to apply or keep up
static KEPT: Mutex<Vec<Kept>> = Mutex::new(Vec::new());
static ACTIVE: AtomicBool = AtomicBool::new(false);
/// milliseconds since [`start`] when the next buff is due
static NEXT_DUE: AtomicU64 = AtomicU64::new(0);
/// how often a kept buff is applied again (what items do for their own buffs)
const REAPPLY: Duration = Duration::from_millis(100);
/// getSelf calls seen, for the self-test (does the hook run at all).
static CALLS: AtomicU64 = AtomicU64::new(0);

/// `keen::ImpactVariable`
const VARIABLE_SIZE: usize = 0x18;
const VARIABLE_OFFSET_AT: usize = 0xe;
/// vm fields the natives read: layout, data, running context
const VM_LAYOUT: usize = 0x00;
const VM_DATA: usize = 0x10;
const VM_BYTES: usize = 0x38;
/// `out[8]`: the native succeeded
const OUT_OK: usize = 8;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Request {
    pub target: u32,
    /// `keen::Guid` of the `BuffType`, in memory order
    pub buff: [u8; 16],
}

struct Kept {
    request: Request,
    /// re-applied until then; None: applied once
    until: Option<Instant>,
    next: Instant,
}

fn start() -> Instant {
    static START: OnceLock<Instant> = OnceLock::new();
    *START.get_or_init(Instant::now)
}

fn millis(at: Instant) -> u64 {
    at.saturating_duration_since(start()).as_millis() as u64
}

pub fn install() -> Result<(), String> {
    unsafe {
        let get_self = GenericDetour::<NativeFn>::new(std::mem::transmute::<usize, NativeFn>(hooks::address("impact_get_self")?), get_self_hook).map_err(|e| e.to_string())?;
        let apply_buff = std::mem::transmute::<usize, NativeFn>(hooks::address("impact_apply_buff")?);
        let hooks = HOOKS.get_or_init(|| Hooks { get_self, apply_buff });
        hooks.get_self.enable().map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Applies a buff to `request.target` and keeps it up for `keep` (zero: applied once, with
/// its own lifetime). A new call for the same target and buff replaces the old one, so
/// `keep` zero also ends a kept buff. Applied the next time any impact program runs.
pub fn apply(request: Request, keep: Duration) -> bool {
    if HOOKS.get().is_none() {
        return false;
    }
    let now = Instant::now();
    let mut kept = KEPT.lock().unwrap();
    kept.retain(|k| k.request != request);
    kept.push(Kept { request, until: (!keep.is_zero()).then(|| now + keep), next: now });
    NEXT_DUE.store(millis(now), Ordering::Release);
    ACTIVE.store(true, Ordering::Release);
    true
}

pub fn calls() -> u64 {
    CALLS.load(Ordering::Relaxed)
}

unsafe extern "system" fn get_self_hook(out: *mut u8, args: *const *const u32, vm: *mut u8) -> *mut u8 {
    let hooks = HOOKS.get().unwrap();
    let result = hooks.get_self.call(out, args, vm);
    CALLS.fetch_add(1, Ordering::Relaxed);
    // plain loads first: this runs on every program step, on several threads
    if ACTIVE.load(Ordering::Relaxed) && millis(Instant::now()) >= NEXT_DUE.load(Ordering::Relaxed) {
        if let Ok(mut kept) = KEPT.try_lock() {
            let now = Instant::now();
            for k in kept.iter_mut().filter(|k| k.next <= now) {
                let applied = call_apply_buff(hooks.apply_buff, vm, &k.request);
                if !applied || (crate::game_events::probing() && k.until.is_none()) {
                    log!("buffs: buff {} for entity {}: {}", shroudlink_sdk::guid_hash(&k.request.buff), k.request.target, if applied { "applied" } else { "refused" });
                }
                // a refused buff (entity gone, unknown buff) is dropped below
                if !applied {
                    k.until = None;
                }
                k.next = now + REAPPLY;
            }
            kept.retain(|k| k.until.is_some_and(|u| u > now));
            ACTIVE.store(!kept.is_empty(), Ordering::Release);
            NEXT_DUE.store(kept.iter().map(|k| millis(k.next)).min().unwrap_or(u64::MAX), Ordering::Release);
        }
    }
    result
}

/// Runs `applyBuff(buff, target)` with the running program's context.
unsafe fn call_apply_buff(apply_buff: NativeFn, vm: *mut u8, request: &Request) -> bool {
    // variables 1 and 2 (0 means "not given"): the buff Guid at 0, the target at 16
    let mut layout = [0u8; VARIABLE_SIZE * 3];
    layout[2 * VARIABLE_SIZE + VARIABLE_OFFSET_AT..][..2].copy_from_slice(&16u16.to_le_bytes());
    let mut data = [0u8; 32];
    data[..16].copy_from_slice(&request.buff);
    data[16..20].copy_from_slice(&request.target.to_le_bytes());
    let indices: [u32; 5] = [1, 2, 0, 0, 0];
    let args = indices.as_ptr();
    let mut own_vm = [0u8; VM_BYTES];
    std::ptr::copy_nonoverlapping(vm, own_vm.as_mut_ptr(), VM_BYTES);
    own_vm[VM_LAYOUT..][..8].copy_from_slice(&(layout.as_ptr() as usize).to_le_bytes());
    own_vm[VM_DATA..][..8].copy_from_slice(&(data.as_ptr() as usize).to_le_bytes());
    let mut out = [0u8; 16];
    apply_buff(out.as_mut_ptr(), &args, own_vm.as_mut_ptr());
    out[OUT_OK] == 1
}
