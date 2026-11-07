//! A game context for the core's own calls into the game, outside of the moments the game
//! hands us one (inventory hooks). Inventory and entity functions take a context built
//! from world-level storages. Without players, the game builds one only once:
//! `FUN_14019c480`, a world system that runs after the world has loaded, fills its context
//! with `FUN_14003aba0` (init) + `FUN_1401b2bf0(ctx, descriptor)` (build). We copy that
//! descriptor; [`with`] builds a fresh context from it whenever the core needs one.
//! Must be used on the game thread (hooks of game systems, the network update).

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};

use chestsort_common::log;
use retour::GenericDetour;

use crate::game::Ctx;
use crate::hooks;

/// Descriptor bytes read by the context builder (last field at +0xe8).
const DESCRIPTOR: usize = 0xf0;
/// Bytes of the world system's stack-held world view kept (pointer, count, inline items).
const WORLD_VIEW: usize = 0x200;
/// The builder writes up to +0x71a8; the game keeps contexts in ~0x71d8 bytes of stack.
const CTX_BYTES: usize = 0x8000;

type SystemFn = unsafe extern "system" fn(system: *mut u8);
type BuildCtxFn = unsafe extern "system" fn(ctx: *mut u8, descriptor: *const u8);
type InitCtxFn = unsafe extern "system" fn(ctx: *mut u8);

struct Hooks {
    boot: GenericDetour<SystemFn>,
    build: GenericDetour<BuildCtxFn>,
    init: InitCtxFn,
}
unsafe impl Send for Hooks {}
unsafe impl Sync for Hooks {}

static HOOKS: OnceLock<Hooks> = OnceLock::new();
static IN_BOOT: AtomicBool = AtomicBool::new(false);
static DESCRIPTOR_COPY: Mutex<Option<Vec<u8>>> = Mutex::new(None);
/// Called once on the game thread when the world has loaded and contexts can be built.
static ON_READY: Mutex<Vec<fn()>> = Mutex::new(Vec::new());

pub fn install() -> Result<(), String> {
    unsafe {
        let boot = GenericDetour::<SystemFn>::new(std::mem::transmute::<usize, SystemFn>(hooks::address("boot_system")?), boot_hook).map_err(|e| e.to_string())?;
        let build = GenericDetour::<BuildCtxFn>::new(std::mem::transmute::<usize, BuildCtxFn>(hooks::address("build_ctx")?), build_hook).map_err(|e| e.to_string())?;
        let init = std::mem::transmute::<usize, InitCtxFn>(hooks::address("init_ctx")?);
        let hooks = HOOKS.get_or_init(|| Hooks { boot, build, init });
        hooks.boot.enable().map_err(|e| e.to_string())?;
        hooks.build.enable().map_err(|e| e.to_string())?;
    }
    Ok(())
}

/// Runs `f` once the world has loaded (immediately if it has).
pub fn on_ready(f: fn()) {
    if ready() {
        f();
    } else {
        ON_READY.lock().unwrap().push(f);
    }
}

pub fn ready() -> bool {
    DESCRIPTOR_COPY.lock().unwrap().is_some()
}

unsafe extern "system" fn build_hook(ctx: *mut u8, descriptor: *const u8) {
    if IN_BOOT.load(Ordering::Relaxed) {
        let mut copy = DESCRIPTOR_COPY.lock().unwrap();
        if copy.is_none() {
            let mut bytes = std::slice::from_raw_parts(descriptor, DESCRIPTOR).to_vec();
            // The first field points at a small object on the world system's stack (the
            // world view: a pointer into itself, a count, then heap pointers), valid only
            // while that system runs. Keep a heap copy with its self-references moved along.
            let view = *(descriptor.add(8) as *const usize);
            let mut stable = vec![0usize; WORLD_VIEW / 8].into_boxed_slice();
            std::ptr::copy_nonoverlapping(view as *const usize, stable.as_mut_ptr(), stable.len());
            let stable = Box::leak(stable);
            let copy_base = stable.as_ptr() as usize;
            for word in stable.iter_mut() {
                if *word >= view && *word < view + WORLD_VIEW {
                    *word = *word - view + copy_base;
                }
            }
            bytes[8..16].copy_from_slice(&copy_base.to_le_bytes());
            *copy = Some(bytes);
        }
    }
    HOOKS.get().unwrap().build.call(ctx, descriptor)
}

unsafe extern "system" fn boot_hook(system: *mut u8) {
    let hooks = HOOKS.get().unwrap();
    let first = !ready();
    IN_BOOT.store(true, Ordering::Relaxed);
    hooks.boot.call(system);
    IN_BOOT.store(false, Ordering::Relaxed);
    if first && ready() {
        log!("world context ready");
        let callbacks = std::mem::take(&mut *ON_READY.lock().unwrap());
        for f in callbacks {
            f();
        }
    }
}

/// Builds a game context and runs `f` with it; None before the world has loaded.
pub fn with<R>(f: impl FnOnce(*mut Ctx) -> R) -> Option<R> {
    let descriptor = DESCRIPTOR_COPY.lock().unwrap().clone()?;
    let hooks = HOOKS.get()?;
    let mut memory = vec![0u128; CTX_BYTES / 16];
    let ctx = memory.as_mut_ptr() as *mut u8;
    unsafe {
        (hooks.init)(ctx);
        hooks.build.call(ctx, descriptor.as_ptr());
    }
    Some(f(ctx as *mut Ctx))
}
