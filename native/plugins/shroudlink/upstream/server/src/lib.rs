//! shroudlink server core: hooks the game, hosts the channel to client addons and
//! loads the mods in `shroudlink_mods/` (chestsort is one of them).
//!
//! The same DLL can be installed under two names next to `enshrouded_server.exe`:
//! - `crashhandler64.dll` (recommended): `steamclient64.dll` probes for Steam's
//!   crash handler there, but the dedicated server does not ship one. Works under
//!   Wine/Proton without DLL overrides and is never touched by game updates.
//! - `dinput8.dll`: classic proxy; needs `WINEDLLOVERRIDES="dinput8=native,builtin"` on Wine.
//! - `steam_api64.dll` (Wine/Proton hosts): the game's file is renamed to
//!   `steam_api64_o.dll` and all Steam calls are passed through to it. Proton
//!   replaces Steam's own client DLL, so `crashhandler64.dll` is never probed there.
//!
//! Singleplayer: as `shroudlink_core.dll` next to `enshrouded.exe`, loaded by the client
//! addon. The client hosts the world itself and carries the same server functions, so
//! the hooks and mods work unchanged; the addon reaches this host through the game's
//! own loopback.

mod buffs;
mod components;
mod events;
mod game;
mod game_events;
mod game_api;
mod hooks;
mod items;
#[cfg(feature = "kiln")]
mod kiln; // kiln: kiln_plugin_main / kiln_plugin_early (the Kiln plugin build)
mod link_host;
mod mods;
mod names;
mod plugins;
mod selftest;
mod steam_proxy;
mod world;
mod world_ctx;

use std::ffi::c_void;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::OnceLock;

use chestsort_common::{log, proxy, scan::Module};
use windows_sys::Win32::Foundation::{HMODULE, TRUE};
use windows_sys::Win32::System::LibraryLoader::{
    GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_PIN,
};
use windows_sys::Win32::System::SystemServices::DLL_PROCESS_ATTACH;

#[cfg(not(feature = "kiln"))] // kiln: as a Kiln plugin the DLL is no proxy
#[no_mangle]
pub unsafe extern "system" fn DirectInput8Create(
    hinst: HMODULE,
    version: u32,
    riid: *const c_void,
    out: *mut *mut c_void,
    outer: *mut c_void,
) -> i32 {
    proxy::direct_input8_create(hinst, version, riid, out, outer)
}

// Steam crash handler interface: we provide none, crash reporting simply stays off
// (which is the dedicated server's normal state, as it ships without the DLL).
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn CrashHandlerFactory(_name: *const i8, return_code: *mut i32) -> *mut c_void {
    if !return_code.is_null() {
        unsafe { *return_code = 1 };
    }
    std::ptr::null_mut()
}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamMiniDumpInit() {}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamSendMiniDump() {}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamSetAppID() {}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamSetSteamID() {}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamWriteMiniDumpSetComment() {}
#[cfg(not(feature = "kiln"))]
#[no_mangle]
pub extern "C" fn Breakpad_SteamWriteMiniDumpUsingExceptionInfoWithBuildId() {}

static STARTED: AtomicBool = AtomicBool::new(false);
/// Outcome of setting up the `steam_api64.dll` proxy, logged once logging is up.
static PROXY_STATUS: OnceLock<Result<(), String>> = OnceLock::new();

#[no_mangle]
pub extern "system" fn DllMain(module: HMODULE, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH && !STARTED.swap(true, Ordering::SeqCst) {
        // the game calls Steam as soon as the loader returns, so this cannot wait for `init`
        if !cfg!(feature = "kiln") && own_file_name().is_some_and(|n| n.eq_ignore_ascii_case("steam_api64.dll")) {
            let _ = PROXY_STATUS.set(unsafe { steam_proxy::init(module) });
        }
        // our hooks point into this DLL, so it must never be unloaded again
        unsafe {
            let mut module: HMODULE = std::ptr::null_mut();
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                DllMain as *const u16,
                &mut module,
            );
        }
        // leave the loader lock before touching the game
        // kiln: the Kiln plugin starts from kiln_plugin_main/on_ready instead (src/kiln.rs)
        if !cfg!(feature = "kiln") {
            std::thread::spawn(init);
        }
    }
    TRUE
}

/// Only one copy may hook the game, even if both file names are installed.
/// kiln: the same claim keeps Kiln's copy and a real shroudlink core (steam_api64.dll, crashhandler64.dll,
/// shroudlink_core.dll) from both hooking: whichever claims first runs.
fn claim_process() -> bool {
    let name = format!("chestsort_server_{}", std::process::id());
    let var = std::env::var_os(&name);
    if var.is_some() {
        return false;
    }
    std::env::set_var(&name, "1");
    true
}

#[allow(dead_code)] // kiln: the Kiln build calls start
fn init() {
    start(|| plugins::load_all(&chestsort_common::game_dir().join(plugins::DIR)));
}

/// kiln: `init` with the mod loading supplied by the caller (Kiln: the profile's mods and shroudlink_mods/).
/// False when another copy claimed the process first.
pub(crate) fn start(load_mods: impl FnOnce()) -> bool {
    if !claim_process() {
        return false;
    }
    chestsort_common::log::init("shroudlink.log");
    // a panic aborts the server (panic = "abort"); at least say where and why
    std::panic::set_hook(Box::new(|info| log!("PANIC: {info}")));
    let in_client = host_is_client();
    // the dedicated server has no crash handler of its own; the game client has one
    // kiln: Kiln keeps its own crash handler
    if !in_client && !cfg!(feature = "kiln") {
        unsafe { windows_sys::Win32::System::Diagnostics::Debug::SetUnhandledExceptionFilter(Some(on_crash)) };
    }
    log!(
        "shroudlink server {} starting (loaded as {}{})",
        env!("CARGO_PKG_VERSION"),
        own_file_name().unwrap_or_else(|| "?".into()),
        if in_client { ", singleplayer host in the game client" } else { "" }
    );
    match PROXY_STATUS.get() {
        Some(Ok(())) => log!("steam_api64 proxy: all calls go to {}", steam_proxy::ORIGINAL),
        Some(Err(e)) => log!("steam_api64 proxy: {e}"),
        None => {}
    }

    let Some(module) = Module::main() else {
        log!("could not inspect the host executable");
        return true;
    };
    hooks::scan(&module);
    // every feature starts on its own: a game update that breaks one keeps the others
    if hooks::available(hooks::Feature::Inventory) {
        match game::Game::resolve().and_then(|game| {
            log!("quick stack at +{:#x}", module.rva(game.deposit_into_chest as usize));
            events::install(game)
        }) {
            Ok(()) => log!("inventory hooks installed"),
            Err(e) => hooks::disable(hooks::Feature::Inventory, e),
        }
    }
    if !hooks::available(hooks::Feature::Inventory) && hooks::available(hooks::Feature::MoveAll) {
        hooks::disable(hooks::Feature::MoveAll, "needs the inventory feature".into());
    }
    names::start();
    if hooks::available(hooks::Feature::WorldContext) {
        if let Err(e) = world_ctx::install() {
            hooks::disable(hooks::Feature::WorldContext, e);
        }
    }
    if hooks::available(hooks::Feature::Buffs) {
        if let Err(e) = buffs::install() {
            hooks::disable(hooks::Feature::Buffs, e);
        }
    }
    selftest::install();
    if hooks::available(hooks::Feature::GameEvents) {
        if let Err(e) = game_events::install() {
            hooks::disable(hooks::Feature::GameEvents, e);
        }
    }
    if hooks::available(hooks::Feature::Channel) {
        // mods first: an addon that connects in between would be welcomed without them
        // (in singleplayer the addon is already running when the core starts)
        load_mods(); // kiln (upstream: plugins::load_all(<game>/shroudlink_mods))
        if let Err(e) = link_host::install(&module) {
            hooks::disable(hooks::Feature::Channel, e);
        }
    }
    hooks::report(&module);
    true
}

/// The dedicated server has no crash handler (and ours is a stub), so a crash ends the
/// process silently: log what and where first.
unsafe extern "system" fn on_crash(info: *const windows_sys::Win32::System::Diagnostics::Debug::EXCEPTION_POINTERS) -> i32 {
    let record = &*(*info).ExceptionRecord;
    let addr = record.ExceptionAddress as usize;
    let base = Module::main().map_or(0, |m| m.base);
    let ours = DllMain as *const () as usize;
    let place = if addr >= base && addr < base + 0x4000000 { format!("game +{:#x}", addr - base) } else { format!("{addr:#x} (shroudlink DllMain at {ours:#x})") };
    log!(
        "CRASH: exception {:#010x} at {place}, data [{:#x}, {:#x}]",
        record.ExceptionCode as u32,
        record.ExceptionInformation[0],
        record.ExceptionInformation[1]
    );
    0 // EXCEPTION_CONTINUE_SEARCH: let the process end as it would have
}

fn own_file_name() -> Option<String> {
    use windows_sys::Win32::System::LibraryLoader::{GetModuleFileNameW, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT};
    unsafe {
        let mut module: HMODULE = std::ptr::null_mut();
        GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            init as *const u16,
            &mut module,
        );
        let mut buf = [0u16; 260];
        let len = GetModuleFileNameW(module, buf.as_mut_ptr(), buf.len() as u32) as usize;
        let path = String::from_utf16_lossy(&buf[..len]);
        path.rsplit(['\\', '/']).next().map(str::to_owned)
    }
}

/// True inside the game client (`enshrouded.exe`), i.e. a singleplayer or co-op host.
fn host_is_client() -> bool {
    std::env::current_exe()
        .ok()
        .and_then(|p| p.file_name().map(|n| n.to_string_lossy().to_ascii_lowercase()))
        .is_some_and(|n| n == "enshrouded.exe")
}
