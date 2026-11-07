// SPDX-License-Identifier: GPL-3.0-or-later
//! The DLL side (Windows): Kiln's plugin exports. The structs mirror Kiln's MIT ABI headers
//! (native/core/abi/kiln_api.h, services/kiln_profile.h, services/kiln_eml.h); only the fields used here are declared
//! (a struct read through a pointer may stop early).
//!
//! - `kiln_plugin_early`: Kiln's early step (before sigdb, hooks and any game code; safe idle too). Stands aside when
//!   EML's own loader (or ShroudForge's) is loaded in this process, else publishes kiln.eml and waits for on_ready,
//!   so other early plugins can add their EML-format mods first.
//! - on_ready: discovers and runs the mods, writes `<game>/kiln/state/eml.json`, logs to kiln.log and kiln/logs/eml.log.
//! - `kiln_plugin_main`: the normal plugin step later in the same boot; runs everything then if no early step did
//!   (an older kiln-core).
//! - `kiln_eml_service`: the kiln.eml table for early plugins (services are not served before hooks are on).

use std::ffi::c_void;
use std::io::Write as _;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, AtomicU64, Ordering};
use std::sync::Mutex;

use camino::{Utf8Path, Utf8PathBuf};
use tracing::Level;
use tracing_subscriber::prelude::*;

use crate::discover::{self, Candidate};
use crate::logging::LineLayer;
use crate::runner::{self, Outcome, Phase, RunConfig};

// ---- Kiln ABI mirror (kiln_api.h) ----

const KILN_OK: i32 = 0;
const KILN_ERR_INVALID_ARGUMENT: i32 = 1;
const KILN_ERR_UNAVAILABLE: i32 = 3;
const KILN_ERR_BUFFER_TOO_SMALL: i32 = 5;
const KILN_ERR_INTERNAL: i32 = 7;

const KILN_PATH_GAME: i32 = 0;
const KILN_PATH_KILN: i32 = 1;
const KILN_PATH_LOGS: i32 = 3;
const KILN_PATH_STATE: i32 = 7;

const KILN_PROFILE_ACTIVE: u32 = 1;
const KILN_PROFILE_ONCE: u32 = 2;
const KILN_PROFILE_NO_MODS: u32 = 3;
const KILN_PROFILE_INVALID: u32 = 4;

const EML_COLLECTING: u32 = 0;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct KilnStr {
    ptr: *const u8,
    len: usize,
}

impl KilnStr {
    const fn lit(s: &'static str) -> Self {
        Self { ptr: s.as_ptr(), len: s.len() }
    }

    fn of(s: &str) -> Self {
        Self { ptr: s.as_ptr(), len: s.len() }
    }

    /// # Safety
    /// `ptr` must point to `len` bytes that live as long as the result is used.
    unsafe fn to_string(self) -> String {
        if self.ptr.is_null() || self.len == 0 {
            return String::new();
        }
        String::from_utf8_lossy(unsafe { std::slice::from_raw_parts(self.ptr, self.len) }).into_owned()
    }
}

#[repr(C)]
pub struct KilnApi {
    size: u32,
    abi_version: u32,
    version: unsafe extern "C" fn() -> KilnStr,
    log: unsafe extern "C" fn(u64, i32, KilnStr),
    build_info: *const c_void,
    feature: *const c_void,
    path: unsafe extern "C" fn(i32, *mut u8, usize, *mut usize) -> i32,
    owner_create: *const c_void,
    owner_revoke: *const c_void,
    subscribe_tick: *const c_void,
    unsubscribe: *const c_void,
    post: *const c_void,
    on_tick_thread: *const c_void,
    tick_info: *const c_void,
    subscribe_log: *const c_void,
    symbol: *const c_void,
    constant: *const c_void,
    service: unsafe extern "C" fn(KilnStr, u32) -> *const c_void,
    register_service: unsafe extern "C" fn(u64, KilnStr, *const c_void) -> i32,
}

#[repr(C)]
pub struct KilnPluginInfo {
    size: u32,
    id: KilnStr,
    version: KilnStr,
    on_unload: Option<unsafe extern "C" fn(*mut c_void)>,
    user: *mut c_void,
    on_ready: Option<unsafe extern "C" fn(*mut c_void)>,
}

#[repr(C)]
pub struct KilnServiceHeader {
    size: u32,
    version: u32,
}

#[repr(C)]
struct KilnProfileApi {
    header: KilnServiceHeader,
    mode: unsafe extern "C" fn() -> u32,
    id: unsafe extern "C" fn() -> KilnStr,
    name: unsafe extern "C" fn() -> KilnStr,
    generation: unsafe extern "C" fn() -> u64,
    count: unsafe extern "C" fn(u32) -> u32,
    get: *const c_void,
    dropins: unsafe extern "C" fn() -> u32,
    config_dir: unsafe extern "C" fn() -> KilnStr,
    data_dir: unsafe extern "C" fn() -> KilnStr,
    json: unsafe extern "C" fn() -> KilnStr,
}

/// kiln_eml.h KilnEmlApi v2 (the v1 prefix is unchanged)
#[repr(C)]
pub struct KilnEmlApi {
    header: KilnServiceHeader,
    add_mod: unsafe extern "C" fn(KilnStr, KilnStr) -> i32,
    phase: unsafe extern "C" fn() -> u32,
    state_json: unsafe extern "C" fn(*mut u8, usize, *mut usize) -> i32,
    stand_aside: unsafe extern "C" fn(KilnStr) -> i32,
}

// Function pointers are plain addresses; the table is immutable.
unsafe impl Sync for KilnEmlApi {}

static TABLE: KilnEmlApi = KilnEmlApi {
    header: KilnServiceHeader { size: std::mem::size_of::<KilnEmlApi>() as u32, version: 2 },
    add_mod: eml_add_mod,
    phase: eml_phase,
    state_json: eml_state_json,
    stand_aside: eml_stand_aside,
};

// ---- state ----

static API: AtomicPtr<KilnApi> = AtomicPtr::new(std::ptr::null_mut());
static OWNER: AtomicU64 = AtomicU64::new(0);
static PHASE: AtomicU32 = AtomicU32::new(EML_COLLECTING);
static STARTED: AtomicBool = AtomicBool::new(false); // kiln_plugin_early (or the fallback) ran
static RAN: AtomicBool = AtomicBool::new(false); // the run (or the decision to stand aside) is over
static ADDED: Mutex<Vec<Candidate>> = Mutex::new(Vec::new());
static STATE_JSON: Mutex<String> = Mutex::new(String::new());
static EML_LOG: Mutex<Option<std::fs::File>> = Mutex::new(None);

const ID: &str = "kiln-eml";

fn api() -> Option<&'static KilnApi> {
    let p = API.load(Ordering::Acquire);
    if p.is_null() { None } else { Some(unsafe { &*p }) }
}

fn klog(level: i32, text: &str) {
    if let Some(a) = api() {
        unsafe { (a.log)(OWNER.load(Ordering::Relaxed), level, KilnStr::of(text)) };
    }
}

fn kpath(kind: i32) -> Option<Utf8PathBuf> {
    let a = api()?;
    let mut buf = vec![0u8; 1024];
    let mut len = 0usize;
    let mut rc = unsafe { (a.path)(kind, buf.as_mut_ptr(), buf.len(), &mut len) };
    if rc == KILN_ERR_BUFFER_TOO_SMALL {
        buf.resize(len + 1, 0);
        rc = unsafe { (a.path)(kind, buf.as_mut_ptr(), buf.len(), &mut len) };
    }
    if rc != KILN_OK {
        return None;
    }
    buf.truncate(len);
    String::from_utf8(buf).ok().map(Utf8PathBuf::from)
}

fn init_logging(logs: Option<&Utf8Path>) {
    if let Some(dir) = logs {
        let _ = std::fs::create_dir_all(dir);
        if let Ok(f) = std::fs::File::create(dir.join("eml.log")) {
            *EML_LOG.lock().unwrap() = Some(f);
        }
    }
    let layer = LineLayer::new(
        Level::DEBUG,
        Box::new(|level, target, text| {
            if let Some(f) = EML_LOG.lock().unwrap().as_mut() {
                let _ = writeln!(f, "[{level}] {target}: {text}");
            }
            let k = match level {
                Level::ERROR => 1,
                Level::WARN => 2,
                Level::INFO => 3,
                _ => return, // debug/trace: eml.log only
            };
            klog(k, &format!("[eml] {text}"));
        }),
    );
    let _ = tracing_subscriber::registry().with(layer).try_init();
}

// ---- standing aside for EML's own loader ----

/// EML's own proxy (dbghelp.dll, or dinput8.dll chained by Kiln), or ShroudForge's (which carries EML), loaded in this
/// process: it already patched the game files in its DllMain, so kiln-eml must not patch them a second time.
#[cfg(windows)]
fn eml_loader_loaded() -> Option<String> {
    use windows::Win32::Foundation::HMODULE;
    use windows::Win32::System::LibraryLoader::GetModuleFileNameW;
    use windows::Win32::System::ProcessStatus::EnumProcessModules;
    use windows::Win32::System::Threading::GetCurrentProcess;

    let mut mods = vec![HMODULE::default(); 2048];
    let mut needed = 0u32;
    let ok = unsafe {
        EnumProcessModules(
            GetCurrentProcess(),
            mods.as_mut_ptr(),
            (mods.len() * std::mem::size_of::<HMODULE>()) as u32,
            &mut needed,
        )
    };
    if ok.is_err() {
        return None;
    }
    let n = (needed as usize / std::mem::size_of::<HMODULE>()).min(mods.len());
    for &m in &mods[..n] {
        let mut buf = [0u16; 1024];
        let len = unsafe { GetModuleFileNameW(Some(m), &mut buf) } as usize;
        if len == 0 {
            continue;
        }
        let path = String::from_utf16_lossy(&buf[..len]);
        let lower = path.to_lowercase();
        let name = lower.rsplit(['\\', '/']).next().unwrap_or("");
        let candidate = name == "dbghelp.dll" || name == "dinput8.dll" || lower.contains("\\kiln\\chain\\");
        if !candidate || lower.contains("\\windows\\") {
            continue;
        }
        let Ok(bytes) = std::fs::read(&path) else { continue };
        let has = |needle: &[u8]| bytes.windows(needle.len()).any(|w| w == needle);
        if has(b"KilnProxy/") || !has(b"EML_LOG_FILE_PATH") {
            continue;
        }
        let who = if has(b"shroudforge") || has(b"ShroudForge") {
            "ShroudForge's loader (it runs EML packages itself)"
        } else {
            "EML's own loader"
        };
        return Some(format!("{path} is loaded: {who} patched the game files this start"));
    }
    None
}

#[cfg(not(windows))]
fn eml_loader_loaded() -> Option<String> { None }

// Validate the headers before forming references to append-only ABI tables.
fn valid_api(api: *const KilnApi, info: *mut KilnPluginInfo, ready: bool) -> bool {
    if api.is_null() || info.is_null() { return false; }
    unsafe {
        let size = std::ptr::read(api.cast::<u32>()) as usize;
        let version = std::ptr::read(api.cast::<u32>().add(1));
        let info_size = std::ptr::read(info.cast::<u32>()) as usize;
        if version != 1 || size < std::mem::size_of::<KilnApi>()
            || info_size < if ready { std::mem::size_of::<KilnPluginInfo>() } else { std::mem::offset_of!(KilnPluginInfo, on_unload) } { return false; }
        for offset in [std::mem::offset_of!(KilnApi, log), std::mem::offset_of!(KilnApi, path),
            std::mem::offset_of!(KilnApi, service), std::mem::offset_of!(KilnApi, register_service)] {
            if std::ptr::read(api.cast::<u8>().add(offset).cast::<usize>()) == 0 { return false; }
        }
    }
    true
}

// ---- the run ----

fn profile() -> Option<&'static KilnProfileApi> {
    let a = api()?;
    let p = unsafe { (a.service)(KilnStr::lit("kiln.profile"), 1) } as *const KilnProfileApi;
    if p.is_null() { return None; }
    unsafe {
        let h = &*p.cast::<KilnServiceHeader>();
        if h.version != 1 || (h.size as usize) < std::mem::size_of::<KilnProfileApi>() { return None; }
        if std::ptr::read(p.cast::<u8>().add(std::mem::offset_of!(KilnProfileApi, mode)).cast::<usize>()) == 0
            || std::ptr::read(p.cast::<u8>().add(std::mem::offset_of!(KilnProfileApi, json)).cast::<usize>()) == 0 { return None; }
        Some(&*p)
    }
}

fn eml_json(game: &Utf8Path) -> (bool, Option<std::path::PathBuf>) {
    // EML's eml.json (read only; kiln-eml never creates it): use_export_flag, export_directory
    let Ok(text) = std::fs::read_to_string(game.join("eml.json")) else { return (false, None) };
    let Ok(v) = serde_json::from_str::<serde_json::Value>(text.trim_start_matches('\u{feff}')) else {
        return (false, None);
    };
    let export = v.get("use_export_flag").and_then(|b| b.as_bool()).unwrap_or(false);
    let dir = v.get("export_directory").and_then(|d| d.as_str()).map(|d| game.join(d).into_std_path_buf());
    (export, dir)
}

fn publish(out: &Outcome, state_dir: Option<&Utf8Path>) {
    let json = serde_json::to_string_pretty(out).unwrap_or_default();
    if let Some(dir) = state_dir {
        let _ = std::fs::create_dir_all(dir);
        let tmp = dir.join("eml.json.tmp");
        if std::fs::write(&tmp, &json).is_ok() {
            let _ = std::fs::rename(&tmp, dir.join("eml.json"));
        }
    }
    *STATE_JSON.lock().unwrap() = json;
    PHASE.store(out.phase.code(), Ordering::Release);
}

fn state_dir(kiln: Option<&Utf8Path>) -> Option<Utf8PathBuf> {
    kpath(KILN_PATH_STATE).or_else(|| kiln.map(|k| k.join("state")))
}

fn run_now() {
    let game = kpath(KILN_PATH_GAME);
    let kiln = kpath(KILN_PATH_KILN);
    let state = state_dir(kiln.as_deref());
    let (Some(game), Some(kiln)) = (game, kiln) else {
        klog(1, "kiln-eml: Kiln gave no game/kiln path; EML mods do not run");
        return;
    };

    let mut candidates: Vec<Candidate> = Vec::new();
    let mut dropins = true;
    let mut profile_note = "no profile";
    if let Some(p) = profile() {
        let mode = unsafe { (p.mode)() };
        if mode == KILN_PROFILE_NO_MODS || mode == KILN_PROFILE_INVALID {
            klog(3, "kiln-eml: no mods this start (play-once no-mods or an invalid profile)");
            publish(&Outcome::new(Phase::Idle, Vec::new()), state.as_deref());
            return;
        }
        if mode == KILN_PROFILE_ACTIVE || mode == KILN_PROFILE_ONCE {
            let json = unsafe { (p.json)().to_string() };
            candidates.extend(discover::profile_candidates(&json, &kiln));
            dropins = discover::profile_dropins(&json);
            profile_note = "profile";
        }
    }
    candidates.extend(ADDED.lock().unwrap().iter().cloned());
    if dropins {
        candidates.extend(discover::folder_candidates(&kiln.join("eml").join("mods"), "kiln-dropin"));
        candidates.extend(discover::folder_candidates(&game.join("mods"), "game-mods"));
    }
    let (mods, entries) = discover::resolve(&candidates);
    let (export, export_dir) = eml_json(&game);
    if let Some(d) = &export_dir {
        let _ = std::fs::create_dir_all(d);
    }
    let cfg = RunConfig {
        game_dir: game.clone(),
        file_name: std::env::current_exe().ok().and_then(|p| p.file_stem().map(|s| s.to_string_lossy().to_lowercase())),
        cache_dir: kiln.join("eml").join("cache"),
        patch: true,
        export,
        export_dir,
        skip_cache: false,
    };
    tracing::debug!(candidates = candidates.len(), run = mods.len(), dropins, profile_note, "kiln-eml discovery");
    let out = runner::run(&cfg, mods, entries);
    for m in &out.mods {
        let id = m.id.as_deref().unwrap_or("?");
        let reason = m.reason.as_deref().unwrap_or("");
        match m.state.as_str() {
            "failed" => klog(1, &format!("kiln-eml: EML mod {id} ({}) failed: {reason}", m.path)),
            "duplicate" | "disabled" => klog(3, &format!("kiln-eml: EML mod {id} {}: {reason}", m.state)),
            "skipped" if !reason.starts_with("no mod.json") => {
                klog(3, &format!("kiln-eml: {} skipped: {reason}", m.path))
            }
            _ => {}
        }
    }
    let summary = match out.phase {
        Phase::Idle if out.restored => "kiln-eml: no EML mods; the game data was restored to vanilla (EML's .kfc.bak)".to_string(),
        Phase::Idle => "kiln-eml: no EML mods; game files untouched".to_string(),
        Phase::Done if out.cached => format!(
            "kiln-eml: {} EML mod(s) loaded; nothing changed since the last run, the game files keep their patches ({:.0} ms)",
            out.loaded(),
            out.ms
        ),
        Phase::Done => format!(
            "kiln-eml: {} EML mod(s) loaded, {} resource(s) patched in {} ({:.0} ms)",
            out.loaded(),
            out.patched,
            out.file.as_deref().unwrap_or("?"),
            out.ms
        ),
        _ => format!(
            "kiln-eml: the EML run failed ({}); game data {}; the game and other mods run on",
            out.error.as_deref().unwrap_or("unknown"),
            if out.restored { "restored to vanilla" } else { "left as it was" }
        ),
    };
    klog(if out.phase == Phase::Failed { 1 } else { 3 }, &summary);
    publish(&out, state.as_deref());
}

fn guarded_run() {
    {
        let _added = ADDED.lock().unwrap_or_else(|e| e.into_inner());
        if RAN.swap(true, Ordering::AcqRel) || PHASE.load(Ordering::Acquire) != EML_COLLECTING { return; }
        PHASE.store(5, Ordering::Release); // running: add_mod is closed
    }
    if catch_unwind(AssertUnwindSafe(run_now)).is_err() {
        klog(1, "kiln-eml: the EML run panicked; the game and other mods run on");
        let mut out = Outcome::new(Phase::Failed, Vec::new());
        out.error = Some("panic (kiln/logs/eml.log)".into());
        publish(&out, state_dir(kpath(KILN_PATH_KILN).as_deref()).as_deref());
    }
}

unsafe extern "C" fn on_ready(_user: *mut c_void) {
    guarded_run();
}

/// Common to the early step and the fallback: identity, logging, the stand-aside check, the service.
fn start(api: *const KilnApi, owner: u64, info: *mut KilnPluginInfo, with_ready: bool) -> i32 {
    if !valid_api(api, info, with_ready) { return KILN_ERR_INVALID_ARGUMENT; }
    API.store(api as *mut KilnApi, Ordering::Release);
    OWNER.store(owner, Ordering::Relaxed);
    let info = unsafe { &mut *info };
    info.id = KilnStr::lit(ID);
    info.version = KilnStr::lit(crate::VERSION);
    if with_ready && info.size as usize >= std::mem::size_of::<KilnPluginInfo>() {
        info.on_ready = Some(on_ready);
    }
    if STARTED.swap(true, Ordering::AcqRel) {
        return KILN_OK;
    }
    init_logging(kpath(KILN_PATH_LOGS).as_deref());
    tracing::info!("kiln-eml {} (EML {}) starting", crate::VERSION, crate::EML_VERSION);
    let a = unsafe { &*api };
    let rc = unsafe { (a.register_service)(owner, KilnStr::lit("kiln.eml"), &TABLE as *const KilnEmlApi as *const c_void) };
    if rc != KILN_OK {
        tracing::debug!(rc, "kiln.eml not published");
    }
    if let Some(why) = eml_loader_loaded() {
        RAN.store(true, Ordering::Release);
        klog(3, &format!("kiln-eml: standing aside: {why}. Remove it to let Kiln run the EML mods (kiln/state/eml.json)"));
        let mut out = Outcome::new(Phase::Aside, Vec::new());
        out.aside = Some(why);
        publish(&out, state_dir(kpath(KILN_PATH_KILN).as_deref()).as_deref());
    }
    KILN_OK
}

/// Kiln's early step (native/core/src/early_plugins.cpp).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kiln_plugin_early(api: *const KilnApi, owner: u64, info: *mut KilnPluginInfo) -> i32 {
    catch_unwind(AssertUnwindSafe(|| start(api, owner, info, true))).unwrap_or(KILN_ERR_INTERNAL)
}

/// Kiln's plugin step (hooks on). After an early step there is nothing left to do; with a kiln-core that has no early
/// step this is the first call, still before the game's entry point, so the run happens here.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kiln_plugin_main(api: *const KilnApi, owner: u64, info: *mut KilnPluginInfo) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if !valid_api(api, info, false) { return KILN_ERR_INVALID_ARGUMENT; }
        let first = !STARTED.load(Ordering::Acquire);
        if !first {
            // keep the early owner as the one that logs; just name ourselves for boot.json
            let info = unsafe { &mut *info };
            info.id = KilnStr::lit(ID);
            info.version = KilnStr::lit(crate::VERSION);
            return KILN_OK;
        }
        let rc = start(api, owner, info, false);
        if rc == KILN_OK {
            guarded_run();
        }
        rc
    }))
    .unwrap_or(KILN_ERR_INTERNAL)
}

/// The kiln.eml table for early plugins (kiln_eml.h: GetModuleHandleW(L"kiln-eml.dll") + GetProcAddress).
#[unsafe(no_mangle)]
pub extern "C" fn kiln_eml_service(min_version: u32) -> *const KilnEmlApi {
    if min_version <= 2 { &TABLE } else { std::ptr::null() }
}

unsafe extern "C" fn eml_add_mod(path: KilnStr, source: KilnStr) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        unsafe fn input(s: KilnStr, max: usize) -> Option<String> {
            if s.len > max || (s.len != 0 && s.ptr.is_null()) { return None; }
            if s.len == 0 { return Some(String::new()); }
            let bytes = unsafe { std::slice::from_raw_parts(s.ptr, s.len) };
            if bytes.contains(&0) { return None; }
            std::str::from_utf8(bytes).ok().map(str::to_owned)
        }
        let (Some(path), Some(mut source)) = (unsafe { input(path, 32768) }, unsafe { input(source, 256) }) else {
            return KILN_ERR_INVALID_ARGUMENT;
        };
        let path = Utf8PathBuf::from(path);
        let game = kpath(KILN_PATH_GAME).or_else(|| std::env::current_exe().ok()
            .and_then(|p| p.parent().map(|p| p.to_owned())).and_then(|p| Utf8PathBuf::from_path_buf(p).ok()));
        let Some(game) = game else { return KILN_ERR_UNAVAILABLE; };
        let kiln = kpath(KILN_PATH_KILN).unwrap_or_else(|| game.join("kiln"));
        let mut roots = vec![game.join("mods"), kiln.join("eml/mods"), kiln.join("mods")];
        if let Some(p) = profile() {
            if let Ok(v) = serde_json::from_str::<serde_json::Value>(&unsafe { (p.json)().to_string() }) {
                if let Some(kinds) = v.get("mods").and_then(|m| m.as_object()) {
                    for list in kinds.values().filter_map(|v| v.as_array()) {
                        roots.extend(list.iter().filter_map(|v| v.get("path")?.as_str().map(|path| discover::resolve_profile_path(path, &kiln))));
                    }
                }
            }
        }
        if discover::validate_path(&path, &roots).is_err() { return KILN_ERR_INVALID_ARGUMENT; }
        let mut added = ADDED.lock().unwrap_or_else(|e| e.into_inner());
        if RAN.load(Ordering::Acquire) { return KILN_ERR_UNAVAILABLE; }
        if source.is_empty() { source = "plugin".into(); }
        added.push(Candidate { path, source, enabled: true });
        KILN_OK
    })).unwrap_or(KILN_ERR_INTERNAL)
}

unsafe extern "C" fn eml_stand_aside(reason: KilnStr) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if reason.ptr.is_null() || reason.len == 0 || reason.len > 1024 { return KILN_ERR_INVALID_ARGUMENT; }
        let bytes = unsafe { std::slice::from_raw_parts(reason.ptr, reason.len) };
        let Ok(reason) = std::str::from_utf8(bytes) else { return KILN_ERR_INVALID_ARGUMENT; };
        if reason.contains('\0') { return KILN_ERR_INVALID_ARGUMENT; }
        let _added = ADDED.lock().unwrap_or_else(|e| e.into_inner());
        if RAN.load(Ordering::Acquire) { return KILN_ERR_UNAVAILABLE; }
        if let Some(game) = kpath(KILN_PATH_GAME) {
            if runner::lock_game(&game).is_err() { return KILN_ERR_UNAVAILABLE; }
        } else { return KILN_ERR_UNAVAILABLE; }
        RAN.store(true, Ordering::Release);
        let mut out = Outcome::new(Phase::Aside, Vec::new());
        out.aside = Some(reason.into());
        publish(&out, state_dir(kpath(KILN_PATH_KILN).as_deref()).as_deref());
        KILN_OK
    })).unwrap_or(KILN_ERR_INTERNAL)
}

unsafe extern "C" fn eml_phase() -> u32 {
    PHASE.load(Ordering::Acquire)
}

unsafe extern "C" fn eml_state_json(buffer: *mut u8, capacity: usize, out_len: *mut usize) -> i32 {
    let s = STATE_JSON.lock().unwrap_or_else(|e| e.into_inner());
    if !out_len.is_null() {
        unsafe { *out_len = s.len() };
    }
    if buffer.is_null() || capacity < s.len() {
        return KILN_ERR_BUFFER_TOO_SMALL;
    }
    unsafe { std::ptr::copy_nonoverlapping(s.as_ptr(), buffer, s.len()) };
    KILN_OK
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rejects_short_and_old_abi_tables_without_reading_slots() {
        let mut info = [0u64; 16];
        info[0] = std::mem::size_of::<KilnPluginInfo>() as u64;
        let mut header = [8u32, 1];
        assert!(!valid_api(header.as_ptr().cast(), info.as_mut_ptr().cast(), true));
        header[1] = 99;
        assert!(!valid_api(header.as_ptr().cast(), info.as_mut_ptr().cast(), false));
        assert!(!valid_api(std::ptr::null(), std::ptr::null_mut(), true));
    }
    #[test]
    fn eml_table_versions_and_invalid_ffi_arguments() {
        assert!(!kiln_eml_service(1).is_null());
        let table = unsafe { &*kiln_eml_service(2) };
        assert_eq!(table.header.version, 2);
        assert_eq!(table.header.size as usize, std::mem::size_of::<KilnEmlApi>());
        assert!(kiln_eml_service(3).is_null());
        let bad = KilnStr { ptr: std::ptr::null(), len: 1 };
        assert_eq!(unsafe { eml_add_mod(bad, KilnStr::lit("test")) }, KILN_ERR_INVALID_ARGUMENT);
        assert_eq!(unsafe { eml_stand_aside(bad) }, KILN_ERR_INVALID_ARGUMENT);
    }

    #[test]
    fn v1_reader_calls_only_the_unchanged_prefix_of_the_v2_table() {
        #[repr(C)]
        struct V1Reader {
            header: KilnServiceHeader,
            add_mod: unsafe extern "C" fn(KilnStr, KilnStr) -> i32,
            phase: unsafe extern "C" fn() -> u32,
            state_json: unsafe extern "C" fn(*mut u8, usize, *mut usize) -> i32,
        }
        assert_eq!(std::mem::size_of::<V1Reader>(), std::mem::offset_of!(KilnEmlApi, stand_aside));
        assert_eq!(std::mem::offset_of!(V1Reader, add_mod), std::mem::offset_of!(KilnEmlApi, add_mod));
        assert_eq!(std::mem::offset_of!(V1Reader, phase), std::mem::offset_of!(KilnEmlApi, phase));
        assert_eq!(std::mem::offset_of!(V1Reader, state_json), std::mem::offset_of!(KilnEmlApi, state_json));
        let ptr = kiln_eml_service(1).cast::<V1Reader>();
        assert!(!ptr.is_null());
        // From here the reader knows only v1. It never reads or calls the appended slot.
        let reader = unsafe { &*ptr };
        assert!(reader.header.version >= 1);
        assert!(reader.header.size as usize >= std::mem::size_of::<V1Reader>());
        assert!(unsafe { (reader.phase)() } <= 5);
        let mut needed = usize::MAX;
        assert_eq!(unsafe { (reader.state_json)(std::ptr::null_mut(), 0, &mut needed) }, KILN_ERR_BUFFER_TOO_SMALL);
        let mut text = vec![0u8; needed];
        assert_eq!(unsafe { (reader.state_json)(text.as_mut_ptr(), text.len(), &mut needed) }, KILN_OK);
        assert_eq!(unsafe { (reader.add_mod)(KilnStr { ptr: std::ptr::null(), len: 1 }, KilnStr::lit("v1-reader")) }, KILN_ERR_INVALID_ARGUMENT);
    }

}
