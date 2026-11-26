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
//! - kiln.eml.archive (archive_service.rs): published in `start` with its capability rows; its early export lives there.
//! - kiln.eml.values (archive_values/abi.rs): published in `start` right after it, with its rows; no early export.

use std::ffi::c_void;
use std::io::Write as _;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicBool, AtomicPtr, AtomicU32, AtomicU64, Ordering};
use std::sync::Mutex;

use camino::{Utf8Path, Utf8PathBuf};
use tracing::Level;
use tracing_subscriber::prelude::*;

use crate::discover::{self, Candidate};
use crate::kiln_mode::{self, KilnMode, Launch};
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

// ---- kiln.launch (kiln_launch.h; it ships with Kiln's interpreter) ----

const KILN_LAUNCH_BACKEND_NONE: u32 = 0;
const KILN_LAUNCH_BACKEND_HOSTED: u32 = 1;
const KILN_LAUNCH_BACKEND_INSTALLED: u32 = 2;
const KILN_LAUNCH_BACKEND_KILN: u32 = 3;

/// kiln_launch.h KilnLaunchStatus v1 (the whole record; kiln-eml reads backend, frozen, reason_code and plan_hash)
#[repr(C)]
#[derive(Clone, Copy)]
#[allow(dead_code)]
struct KilnLaunchStatus {
    size: u32,
    backend: u32,
    frozen: u32,
    reason_code: u32,
    launch_id: u64,
    plan_hash: [u8; 32],
    freeze_stamp: u64,
    reason: [u8; 128],
}
const _: () = assert!(std::mem::size_of::<KilnLaunchStatus>() == 192 && std::mem::offset_of!(KilnLaunchStatus, plan_hash) == 24
    && std::mem::offset_of!(KilnLaunchStatus, reason) == 64);

/// kiln_launch.h KilnLaunchApi v1 (`notice_copy` is not used here)
#[repr(C)]
#[allow(dead_code)]
struct KilnLaunchApi {
    header: KilnServiceHeader,
    status: Option<unsafe extern "C" fn(*mut KilnLaunchStatus) -> i32>,
    notice_copy: *const c_void,
}

/// The core's frozen ShroudForge backend for this start, classified as kiln-sfi and kiln-shroudforge classify it: every
/// member is checked against the table's declared size and version before use, and the record against its own; anything
/// else (no table: an older core) is [`Launch::Absent`].
///
/// # Safety
/// `service` is the core's `KilnApi.service`; a table it returns is readable for its declared size.
unsafe fn read_launch(service: unsafe extern "C" fn(KilnStr, u32) -> *const c_void) -> Launch {
    let table = unsafe { service(KilnStr::lit("kiln.launch"), 1) }.cast::<KilnLaunchApi>();
    if table.is_null() || !table.is_aligned() { return Launch::Absent; }
    // SAFETY: the table starts with its header; `status` is read only when the declared size covers it
    let header = unsafe { table.cast::<KilnServiceHeader>().read() };
    if header.version < 1 || (header.size as usize) < std::mem::offset_of!(KilnLaunchApi, status) + std::mem::size_of::<usize>() {
        return Launch::Absent;
    }
    let Some(status) = (unsafe { std::ptr::addr_of!((*table).status).read() }) else { return Launch::Absent };
    // SAFETY: an all-zero record is valid; the core fills it during the call
    let mut record: KilnLaunchStatus = unsafe { std::mem::zeroed() };
    record.size = std::mem::size_of::<KilnLaunchStatus>() as u32;
    if unsafe { status(&mut record) } != KILN_OK || record.frozen != 1
        || (record.size as usize) < std::mem::size_of::<KilnLaunchStatus>() {
        return Launch::Absent;
    }
    match record.backend {
        KILN_LAUNCH_BACKEND_HOSTED | KILN_LAUNCH_BACKEND_INSTALLED => Launch::Shroudforge,
        KILN_LAUNCH_BACKEND_KILN => Launch::Kiln { plan_hash: record.plan_hash, launch_id: record.launch_id },
        KILN_LAUNCH_BACKEND_NONE if (0x100..0x200).contains(&record.reason_code) => Launch::Refused,
        KILN_LAUNCH_BACKEND_NONE if (0x200..0x300).contains(&record.reason_code) => Launch::Idle,
        _ => Launch::Absent,
    }
}

/// kiln-eml's route this start (kiln_mode.rs): Some when kiln.launch froze backend `kiln` (and `KILN_SHROUDFORGE=0`
/// does not stand it aside); None keeps the rules from before kiln.launch, and logs nothing new. The plan is the one
/// kiln-eml's early phase read (startup_seam.rs), read here only when no early phase ran.
fn kiln_route(game: &Utf8Path, kiln: &Utf8Path) -> Option<std::sync::Arc<KilnMode>> {
    let launch = api().map_or(Launch::Absent, |a| unsafe { read_launch(a.service) });
    let env = std::env::var("KILN_SHROUDFORGE").ok();
    let mode = kiln_mode::select(&launch, env.as_deref(), game, |hash| {
        crate::startup_seam::early_plan().unwrap_or_else(|| {
            let json = profile().map(|p| unsafe { (p.json)().to_string() });
            let (exe_sha256, side) = build_identity().ok_or("the core does not say which game executable runs")?;
            let context = sfi_policy::launch_plan::ValidationContext { exe_sha256, side: side.into(), upstream_180_final: false };
            kiln_mode::read_plan(json.as_deref(), kiln, game, hash, &context)
        })
    })?;
    klog(if mode.plan_read() { 3 } else { 2 }, &mode.summary());
    Some(std::sync::Arc::new(mode))
}

/// `build_info`'s executable SHA-256 (64 lower-case hex digits) and side (kiln_api.h KilnBuildInfo), as kiln-sfi reads
/// them for the launch plan's checks; None when the core does not say. Served in the early step too.
fn build_identity() -> Option<(String, &'static str)> {
    use kiln_abi::{KILN_SIDE_SERVER, KilnBuildInfo};
    let a = api()?;
    if a.build_info.is_null() { return None; }
    // SAFETY: kiln_api.h declares the member with this signature; valid_api checked the table covers it.
    let build_info: unsafe extern "C" fn(*mut KilnBuildInfo) -> i32 = unsafe { std::mem::transmute(a.build_info) };
    // SAFETY: an all-zero record is valid; the core fills it during the call.
    let mut info: KilnBuildInfo = unsafe { std::mem::zeroed() };
    info.size = std::mem::size_of::<KilnBuildInfo>() as u32;
    if unsafe { build_info(&mut info) } != KILN_OK { return None; }
    let exe: String = info.exe_sha256.iter().take_while(|&&c| c != 0).map(|&c| c as u8 as char).collect();
    if exe.len() != 64 || !exe.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) { return None; }
    Some((exe, if info.side == KILN_SIDE_SERVER as u32 { "server" } else { "client" }))
}

// (kiln mode's early phase)
/// kiln-eml's early phase of a kiln-backend start (startup_seam.rs `early`): the plan read again, the install lock taken
/// for the process (`other_game_running` with it), the startup controller's witness before any package DLL loads, and the launch installed for
/// kiln-sfi's attach. `loaders`: another EML loader that patched the game files in this process refuses the witness (no
/// stand-aside outcome in kiln mode: ShroudForge's config is never read here). False: not a kiln start, the rules from
/// before kiln.launch apply.
fn kiln_early(loaders: Option<&LoadedLoaders>) -> bool {
    let launch = api().map_or(Launch::Absent, |a| unsafe { read_launch(a.service) });
    let Launch::Kiln { plan_hash, launch_id } = launch else { return false };
    if !kiln_mode::active(&launch, std::env::var("KILN_SHROUDFORGE").ok().as_deref()) { return false; }
    // what kiln mode's on_ready asks the system through the loader, asked now, on the boot thread before any package
    // DLL loads: on_ready may run while a package DLL's initializer holds the loader lock, which Wine's
    // GetModuleFileNameW, GetModuleHandleW and GetProcAddress take
    let _ = exe_stem();
    crate::discover::prime_dos_name();
    let (Some(game), Some(kiln)) = (kpath(KILN_PATH_GAME), kpath(KILN_PATH_KILN)) else {
        klog(1, "kiln-eml: Kiln gave no game/kiln path; Kiln's ShroudForge startup pass does not run");
        return true;
    };
    let stem = exe_stem().unwrap_or_default();
    let json = profile().map(|p| unsafe { (p.json)().to_string() });
    let read = crate::startup_seam::early(&crate::startup_seam::EarlyInputs {
        game: &game,
        kiln: &kiln,
        stem: &stem,
        launch_id,
        plan_sha256: plan_hash,
        build: build_identity(),
        profile_json: json.as_deref(),
        foreign_loader: loaders.map(|l| l.why.clone()),
        lock: &crate::startup_seam::install_lock,
        witness: &crate::startup::witness,
    });
    if let Err(why) = read {
        klog(2, &format!("kiln-eml: Kiln's interpreter has this start (kiln.launch), but its launch plan cannot \
            be read again ({why}): no startup pass runs this start"));
    }
    true
}

/// The chosen rule, in this one place: a start of hosted
/// ShroudForge (`stand_aside`, under the install lock) clears the handoff an earlier kiln-mode start owes ShroudForge
/// (`<stem>.sf-handoff-owed.json`): ShroudForge's applied record is marked stale before ShroudForge reads it, so it never
/// reuses a record of game data Kiln changed since. Nothing happens when no handoff is owed. A failure is logged and the
/// start goes on (the marker stays for the next start that may use ShroudForge's folder).
fn settle_owed_handoff_under_hosted(game: &Utf8Path) {
    let Some(stem) = exe_stem().filter(|s| s == "enshrouded" || s == "enshrouded_server") else { return };
    match mod_loader::lua::settle_owed_handoff(game.as_std_path(), &stem) {
        Ok(true) => klog(3, "kiln-eml: marked ShroudForge's record of its prepared asset mods stale: Kiln's \
            interpreter changed the game data since ShroudForge last ran"),
        Ok(false) => {}
        Err(e) => klog(2, &format!("kiln-eml: could not mark ShroudForge's record of its prepared asset mods stale ({e}); \
            the next start that may use ShroudForge's folder tries again")),
    }
}

/// The file stem of this process's executable, lower case (`enshrouded`, `enshrouded_server` in the game); a unit test
/// names the game's in its child process. Asked once per process: Wine's GetModuleFileNameW takes the loader lock, and
/// kiln mode's on_ready may run while a package DLL's initializer holds it (the first call is kiln-eml's early phase).
fn exe_stem() -> Option<String> {
    #[cfg(test)]
    if let Some(stem) = tests::EXE_STEM.get() { return Some(stem.clone()); }
    static STEM: std::sync::OnceLock<Option<String>> = std::sync::OnceLock::new();
    STEM.get_or_init(|| std::env::current_exe().ok().and_then(|p| p.file_stem().map(|s| s.to_string_lossy().to_lowercase())))
        .clone()
}

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

/// A module path EML's loader may have: its proxy names (`dbghelp.dll`, `dinput8.dll`), the renamed builds mods bundle
/// (`dbghelp_<mod>.dll`, `dinput8_<name>.dll`: issue 23 had `dinput8_eml.dll` next to a mod's `dbghelp_*.dll`), or
/// Kiln's chain folder.
fn loader_module_candidate(path: &str) -> bool {
    let lower = path.to_lowercase().replace('/', "\\");
    let name = lower.rsplit('\\').next().unwrap_or("");
    let renamed = |stem: &str| name.strip_prefix(stem).is_some_and(|rest| rest.starts_with('_') && rest.ends_with(".dll"));
    name == "dbghelp.dll" || renamed("dbghelp") || name == "dinput8.dll" || renamed("dinput8")
        || lower.contains("\\kiln\\chain\\")
}

/// A modified EML loader runs in this process (its own code reads its settings file next to the game).
static MODIFIED_LOADED: AtomicBool = AtomicBool::new(false);

/// The line for an EML loader file in the folder whose code is not the module the process loaded under its name (Wine's
/// builtin without a DLL override): who the file is, and for a modified EML loader that its own code does not run.
#[cfg_attr(not(windows), allow(dead_code))]
fn unloaded_line(path: &str, who: &str, modified: Option<&str>) -> String {
    let name = path.replace('/', "\\").rsplit('\\').next().unwrap_or("dbghelp.dll").to_string();
    let short = if who.starts_with("ShroudForge") { "ShroudForge's loader" } else { "EML's loader" };
    let own = modified.map(|what| format!(". It is a modified EML loader with code of its own ({what}): that code does not \
        run either, only the EML mods' Lua part does")).unwrap_or_default();
    format!("{path}: {short} is in the folder but this process loaded another {name} (Wine's builtin, without a DLL \
        override): kiln-eml runs the EML mods{own}")
}

/// Who an EML-marked loader file is: ShroudForge's loader (it carries EML) or EML's own. None: not an EML loader (no
/// `EML_LOG_FILE_PATH`, or Kiln's own proxy, which names every engine).
fn eml_loader_who(bytes: &[u8]) -> Option<&'static str> {
    let has = |needle: &[u8]| bytes.windows(needle.len()).any(|w| w == needle);
    if has(b"KilnProxy/") || !has(b"EML_LOG_FILE_PATH") {
        return None;
    }
    Some(if has(b"shroudforge") || has(b"ShroudForge") {
        "ShroudForge's loader (it runs EML packages itself)"
    } else {
        "EML's own loader"
    })
}

/// More than one EML loader in this process (issue 23: `dinput8_eml.dll` and a mod's bundled `dbghelp_*.dll` build;
/// EML's log said "Applied 2281 patches" twice per start): each one patches the game files in its DllMain, and kiln-eml
/// cannot stop either. The sentence for kiln.log and `reason` in kiln/state/eml.json (the launcher's engines view shows
/// it). None for one loader or none.
fn several_loaders_note(paths: &[String]) -> Option<String> {
    if paths.len() < 2 {
        return None;
    }
    let count = if paths.len() == 2 { "two".to_string() } else { paths.len().to_string() };
    Some(format!("{count} EML loaders in this process ({}): each one patches the game files at every start. Keep one \
        and remove the others (on a Kiln server remove them all: Kiln runs the EML mods)", paths.join(", ")))
}

/// EML's own proxy (dbghelp.dll, or dinput8.dll chained by Kiln), or ShroudForge's (which carries EML), loaded in this
/// process: it already patched the game files in its DllMain, so kiln-eml must not patch them a second time.
#[cfg(windows)]
fn eml_loader_loaded() -> Option<LoadedLoaders> {
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
    let mut found: Vec<(String, &'static str)> = Vec::new();
    for &m in &mods[..n] {
        let mut buf = [0u16; 1024];
        let len = unsafe { GetModuleFileNameW(Some(m), &mut buf) } as usize;
        if len == 0 {
            continue;
        }
        let path = String::from_utf16_lossy(&buf[..len]);
        let lower = path.to_lowercase();
        let candidate = loader_module_candidate(&lower);
        if !candidate || lower.contains("\\windows\\") {
            continue;
        }
        let Ok(bytes) = std::fs::read(&path) else { continue };
        let Some(who) = eml_loader_who(&bytes) else { continue };
        // a modified EML loader (Holster Toggle's dbghelp.dll): its own code runs only while it is the loader that runs
        let modified = (!who.starts_with("ShroudForge")).then(|| crate::modified::what(&bytes)).flatten();
        // Wine without a DLL override loads its builtin dbghelp but reports the game folder's path for it: the file
        // there is EML's, the code in the process is not. The loaded image's PE header must be the file's.
        // A header that cannot be read on either side keeps the old answer: the file's loader counts as running.
        // SAFETY: a loaded module's base maps at least its first page (the headers, 4 KiB on x64).
        let image = unsafe { std::slice::from_raw_parts(m.0 as *const u8, 4096) };
        if let (Some(loaded), Some(file)) = (pe_identity(image), pe_identity(&bytes))
            && loaded != file
        {
            tracing::info!("{}", unloaded_line(&path, who, modified.as_deref()));
            continue;
        }
        if let Some(what) = &modified {
            MODIFIED_LOADED.store(true, Ordering::Release);
            tracing::info!("{path} is a modified EML loader with code of its own ({what}): that code runs in this process too");
        }
        found.push((path, who));
    }
    loaded_loaders(found)
}

/// The EML loaders found in this process, as kiln-eml reports them: the first one names who patched the game files;
/// `several` is set when there is more than one.
#[cfg_attr(not(windows), allow(dead_code))]
struct LoadedLoaders {
    why: String,
    who: &'static str,
    paths: Vec<String>,
    several: Option<String>,
}

#[cfg_attr(not(windows), allow(dead_code))]
fn loaded_loaders(found: Vec<(String, &'static str)>) -> Option<LoadedLoaders> {
    let (path, who) = found.first().cloned()?;
    let short = if who.starts_with("ShroudForge") { "ShroudForge's loader" } else { "EML's own loader" };
    let paths: Vec<String> = found.into_iter().map(|(p, _)| p).collect();
    let several = several_loaders_note(&paths);
    let why = match &several {
        Some(note) => format!("{note}. {path} is loaded: {who} patched the game files this start"),
        None => format!("{path} is loaded: {who} patched the game files this start"),
    };
    Some(LoadedLoaders { why, who: short, paths, several })
}

/// (TimeDateStamp, SizeOfImage, AddressOfEntryPoint) of a PE image's headers: the same for a file and its loaded image.
fn pe_identity(b: &[u8]) -> Option<(u32, u32, u32)> {
    let u32_at = |o: usize| b.get(o..o + 4).map(|x| u32::from_le_bytes([x[0], x[1], x[2], x[3]]));
    if b.get(0..2)? != b"MZ" { return None; }
    let pe = u32_at(0x3c)? as usize;
    if pe > 2048 || b.get(pe..pe + 4)? != b"PE\0\0" { return None; }
    let opt = pe + 24;
    Some((u32_at(pe + 8)?, u32_at(opt + 56)?, u32_at(opt + 16)?))
}

#[cfg(not(windows))]
fn eml_loader_loaded() -> Option<LoadedLoaders> { None }

/// The folders another EML runner reads: EML's `<game>/mods`, and ShroudForge's configured mods folder.
fn loader_roots(game: &Utf8Path) -> Vec<Utf8PathBuf> {
    let mut roots = Vec::new();
    let sf = std::fs::read_to_string(game.join("shroudforge/config/modloader-config.json")).ok()
        .and_then(|t| serde_json::from_str::<serde_json::Value>(t.trim_start_matches('\u{feff}')).ok())
        .and_then(|v| v.pointer("/paths/mods").and_then(|m| m.as_str()).map(str::to_owned))
        .filter(|m| !m.trim().is_empty());
    if let Some(m) = sf {
        // Windows (the game, also under Wine): as Windows names the folder, not ShroudForge's canonical `\\?\C:\…\mods`.
        // The folders kiln-eml names in it are this root joined with their names (`C:\…\mods\bare` in kiln.log, eml.json
        // and the launcher, never `//?/C:/…/mods\bare`). Elsewhere (kiln-eml-run) `\` is no separator.
        let p = Utf8PathBuf::from(if cfg!(windows) { discover::windows_plain_path(&m) } else { m.replace('\\', "/") });
        roots.push(if p.is_absolute() { p } else { game.join(p) });
    }
    roots.push(game.join("mods"));
    roots
}

/// ShroudForge's mods folder: its configured `paths.mods`, else `<game>/mods` (the first of [`loader_roots`]).
fn shroudforge_root(game: &Utf8Path) -> Utf8PathBuf {
    loader_roots(game).into_iter().next().unwrap_or_else(|| game.join("mods"))
}

/// ShroudForge's own proxy runs ShroudForge unless its config makes it plain EML (`"modloader": "eml"`).
fn shroudforge_mode(game: &Utf8Path) -> bool {
    std::fs::read_to_string(game.join("shroudforge/config/modloader-config.json")).ok()
        .and_then(|t| serde_json::from_str::<serde_json::Value>(t.trim_start_matches('\u{feff}')).ok())
        .and_then(|v| v.get("modloader").and_then(|m| m.as_str()).map(str::to_owned))
        .is_none_or(|m| !m.eq_ignore_ascii_case("eml"))
}

/// The `aside` outcome: `why` names the loader that runs the EML mods; the profile's EML mods are listed as running
/// under it (Kiln's copy is in its folder) or not loading (only in the Library), the latter as warnings. `shroudforge`:
/// that loader is ShroudForge, which reads only its own mods folder and runs a package there only while the package's
/// switch is on, for this side (discover::shroudforge_switch). Under ShroudForge with drop-ins on (no profile, or the
/// profile's drop-ins on), the drop-in folders' EML mods that nothing runs are listed too (discover::aside_dropins): one
/// warning in kiln.log; their rows in the state file on a dedicated server only (a client's in-game menu already lists
/// ShroudForge's folder as ShroudForge's rows) and for kiln/eml/mods. Under ShroudForge, one info line also counts the EML
/// packages of its folder and names those it runs (discover::shroudforge_runs). Listing only: what runs stays as it was,
/// except while Kiln hosts ShroudForge (`skip`, discover::aside_entries_with): a profile mod that is off, whose folder
/// ShroudForge would run (the player's own folder, which Kiln never writes into), is named to ShroudForge's runtime to be
/// skipped this start ([`host_skip`]).
fn aside_outcome(why: &str, who: &str, shroudforge: bool, skip: Option<&mut dyn FnMut(&str) -> bool>) -> Outcome {
    let mut out = Outcome::new(Phase::Aside, Vec::new());
    out.aside = Some(why.into());
    let (Some(game), Some(kiln)) = (kpath(KILN_PATH_GAME), kpath(KILN_PATH_KILN)) else { return out };
    let mode = profile().map(|p| unsafe { (p.mode)() });
    if matches!(mode, Some(KILN_PROFILE_NO_MODS | KILN_PROFILE_INVALID)) { return out; }
    let json = profile().filter(|_| matches!(mode, Some(KILN_PROFILE_ACTIVE | KILN_PROFILE_ONCE)))
        .map(|p| unsafe { (p.json)().to_string() });
    let server = exe_stem().is_some_and(|s| s == "enshrouded_server");
    let roots = if shroudforge { vec![shroudforge_root(&game)] } else { loader_roots(&game) };
    if let Some(json) = &json {
        out.mods = discover::aside_entries_with(json, &kiln, &roots, who, shroudforge.then_some(server), skip);
        for (level, line) in discover::aside_log(&out.mods, who) {
            klog(level, &line);
        }
        for (level, line) in discover::apply_partial(json, &mut out.mods) {
            klog(level, &line);
        }
    }
    // what ShroudForge does run from its folder, by name (info): a start that runs its mods says so too
    if shroudforge {
        let (skipped, skipped_ids) = discover::skipped_rows(&out.mods);
        let (runs, total) = discover::shroudforge_runs(&roots[0], server, &skipped);
        if let Some(line) = discover::shroudforge_runs_log(&runs, total, &roots[0], &skipped_ids) {
            klog(3, &line);
        }
    }
    // no profile (or the legacy layout): drop-ins on, as run_now reads them
    if shroudforge && json.as_deref().is_none_or(discover::profile_dropins) {
        let ids = json.as_deref().map(discover::profile_ids).unwrap_or_default();
        let mut extra = discover::aside_dropins(&roots[0], &kiln, &ids, &out.mods, who, server);
        if let Some(line) = discover::aside_dropins_log(&extra, &roots[0], server) {
            // on a client a package switched off in ShroudForge's own menu is usually the player's choice: info there.
            // A server (nothing switches it on), or EML mods in kiln/eml/mods (ShroudForge never reads them): a warning.
            let warn = server || extra.iter().any(|e| e.source == "kiln-dropin");
            klog(if warn { 2 } else { 3 }, &line);
        }
        extra.retain(|e| server || e.source == "kiln-dropin");
        out.mods.extend(extra);
    }
    out
}

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

/// kiln.caps v1, when the core serves it in full (it does in the early step and in safe idle).
fn caps_table() -> Option<&'static kiln_abi::services::kiln_caps::KilnCapsApi> {
    use kiln_abi::services::kiln_caps::KilnCapsApi;
    let a = api()?;
    let p = unsafe { (a.service)(KilnStr::lit("kiln.caps"), 1) } as *const KilnCapsApi;
    if p.is_null() { return None; }
    unsafe {
        let h = &*p.cast::<KilnServiceHeader>();
        if h.version < 1 || (h.size as usize) < std::mem::size_of::<KilnCapsApi>() { return None; }
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
        // Managed placements run only through an explicit profile entry, never again as drop-ins (including retained edits).
        let ids = profile().map(|p| discover::profile_ids(&unsafe { (p.json)().to_string() })).unwrap_or_default();
        for dir in [kiln.join("eml").join("mods"), game.join("mods")] {
            let source = if dir.starts_with(&kiln) { "kiln-dropin" } else { "game-mods" };
            let (kept, skipped) = discover::without_profile_copies(discover::folder_candidates(&dir, source), &ids, &kiln);
            for s in skipped {
                tracing::info!("{s}: Kiln-managed placement; not a drop-in (only an enabled profile entry may run it)");
            }
            candidates.extend(kept);
        }
    }
    let (mut mods, mut entries) = discover::resolve(&candidates);
    // the player's own folder of an EML mod the profile has off ran as a drop-in: it is skipped now, its files untouched
    if dropins && let Some(p) = profile().filter(|p| matches!(unsafe { (p.mode)() }, KILN_PROFILE_ACTIVE | KILN_PROFILE_ONCE)) {
        for line in discover::honor_profile_off(&unsafe { (p.json)().to_string() }, &kiln, &mut mods, &mut entries) {
            klog(3, &line);
        }
    }
    let (export, export_dir) = eml_json(&game);
    if let Some(d) = &export_dir {
        let _ = std::fs::create_dir_all(d);
    }
    // the core's progress window (kiln.progress; none on a server, with KILN_UI_DISABLE=1 or in an older kiln-core)
    let table = api().map_or(std::ptr::null(), |a| unsafe { (a.service)(KilnStr::lit("kiln.progress"), 1) });
    let route = kiln_route(&game, &kiln);
    // (none in a kiln-mode start either. Its on_ready may run while a package DLL's initializer
    // holds the loader lock, and the core's first begin asks user32 for the screen size, which can load Wine's display
    // driver (Proton would wait for that initializer); the window would show EML's title and never the startup pass.
    // The state file's progress stays, for the launcher.)
    let table = if route.is_some() { std::ptr::null() } else { table };
    let progress = Some(std::sync::Arc::new(unsafe { crate::progress::Display::new(state.clone(), table) }));
    let cfg = RunConfig {
        progress,
        game_dir: game.clone(),
        // (asked once per process, in a kiln start's early phase, never under a package DLL's
        // loader lock; public: `std::env::current_exe()`'s file stem in lower case, asked here)
        file_name: exe_stem(),
        cache_dir: kiln.join("eml").join("cache"),
        patch: true,
        export,
        export_dir,
        skip_cache: false,
        kiln: route,
    };
    if let Some(p) = profile() {
        match &cfg.kiln {
            Some(mode) => discover::apply_runtime_blocks_with(&unsafe { (p.json)().to_string() }, &kiln, &mut entries, |e| mode.delegated(e)),
            None => discover::apply_runtime_blocks(&unsafe { (p.json)().to_string() }, &kiln, &mut entries, &runner::runtime_skip_reason(&cfg)),
        }
    }
    tracing::debug!(candidates = candidates.len(), run = mods.len(), dropins, profile_note, "kiln-eml discovery");
    let mut out = runner::run(&cfg, mods, entries);
    // EML mods of which only the Lua part runs (a modified EML loader in their package carries the rest): say so
    if let Some(p) = profile() {
        for (level, line) in discover::apply_partial(&unsafe { (p.json)().to_string() }, &mut out.mods) {
            klog(level, &line);
        }
    }
    for m in &out.mods {
        let id = m.id.as_deref().unwrap_or("?");
        let reason = m.reason.as_deref().unwrap_or("");
        match m.state.as_str() {
            "failed" => klog(1, &format!("kiln-eml: EML mod {id} ({}) failed: {reason}", m.path)),
            "duplicate" | "disabled" => klog(3, &format!("kiln-eml: EML mod {id} {}: {reason}", m.state)),
            // a Kiln mod dropped into EML's folder is a player's mistake to fix: a warning, not an info line
            "skipped" if reason.contains("kiln.mod.json") => klog(2, &format!("kiln-eml: {} skipped: {reason}", m.path)),
            "skipped" if !reason.starts_with("no mod.json") => {
                klog(3, &format!("kiln-eml: {} skipped: {reason}", m.path))
            }
            _ => {}
        }
    }
    let summary = match out.phase {
        // (a kiln-mode start's line)
        _ if cfg.kiln.is_some() => kiln_summary(&out),
        Phase::Idle if out.restored => "kiln-eml: no EML mods; the game data was restored to vanilla (EML's .kfc.bak)".to_string(),
        Phase::Idle => match &out.reason {
            Some(reason) => format!("kiln-eml: no EML mods; game files untouched. {reason}"),
            None if out.stale_backup => "kiln-eml: no EML mods; game files untouched (the vanilla backup is of an earlier \
                game build: the game was updated since; the next start with an EML mod makes a new one)".to_string(),
            None => "kiln-eml: no EML mods; game files untouched".to_string(),
        },
        Phase::Done if out.cached => format!(
            "kiln-eml: {} EML mod(s) loaded; nothing changed since the last run, the game files keep their patches ({:.0} ms{})",
            out.loaded(),
            out.ms,
            fingerprint_note(&out)
        ),
        Phase::Done => format!(
            "kiln-eml: {} EML mod(s) loaded, {} resource(s) patched in {} ({:.0} ms{})",
            out.loaded(),
            out.patched,
            out.file.as_deref().unwrap_or("?"),
            out.ms,
            fingerprint_note(&out)
        ),
        Phase::Failed if out.start_blocked => format!("kiln-eml: {}; stopping this start before the game entry point",
            out.error.as_deref().unwrap_or("install lock failed")),
        _ => format!(
            "kiln-eml: the EML run failed ({}); game data {}; the game and other mods run on",
            out.error.as_deref().unwrap_or("unknown"),
            if out.restored { "restored to vanilla" } else { "left as it was" }
        ),
    };
    klog(if out.phase == Phase::Failed { 1 } else if out.reason.is_some() { 2 } else { 3 }, &summary);
    if out.phase == Phase::Done && let Some(reason) = &out.reason {
        klog(2, &format!("kiln-eml: {reason}"));
    }
    publish(&out, state.as_deref());
    enforce_startup_safety(&out);
}

/// kiln.log's line for a kiln-mode start (its startup pass, runner.rs `run_kiln`).
fn kiln_summary(out: &Outcome) -> String {
    let applied = out.mods.iter().filter(|m| m.source == "shroudforge" && m.state == "loaded").count();
    let error = out.error.as_deref().unwrap_or("no reason was given");
    match out.phase {
        Phase::Failed if out.start_blocked => format!("kiln-eml: {error}; stopping this start before the game entry point"),
        Phase::Failed => format!("kiln-eml: Kiln's ShroudForge startup pass applied nothing this start ({error}); the game \
            and other mods run on"),
        Phase::Done if out.cached => format!("kiln-eml: Kiln's ShroudForge startup pass: {applied} package(s) applied, \
            nothing changed since the last start ({:.0} ms)", out.ms),
        Phase::Done => format!("kiln-eml: Kiln's ShroudForge startup pass: {applied} package(s) applied ({:.0} ms)", out.ms),
        _ if out.restored => "kiln-eml: Kiln's ShroudForge startup pass applied no package; the game's own data was put back".into(),
        _ => "kiln-eml: Kiln's ShroudForge startup pass applied no package; game files untouched".into(),
    }
}

/// `; hashed 0 B, reused 57 file(s)`: what reading the game's files for the change cache cost this start.
fn fingerprint_note(out: &Outcome) -> String {
    out.fingerprint.map(|f| format!("; {}", f.brief())).unwrap_or_default()
}

fn enforce_startup_safety(out: &Outcome) {
    if out.start_blocked {
        klog(1, "kiln-eml: stopping this start: the install cannot be locked; another process may still be publishing game data");
        std::process::exit(1);
    }
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
    // kiln.eml.archive v1: the same table as the early export kiln_eml_archive_service, and its rows.
    let rc = crate::archive_service::publish(
        owner,
        |name, table| unsafe { (a.register_service)(owner, KilnStr::of(name), table) },
        caps_table(),
    );
    if rc != KILN_OK {
        tracing::debug!(rc, "kiln.eml.archive not published");
    }
    // kiln.eml.values v1: normal lookup only (no early export), and its rows.
    let rc = crate::archive_values::abi::publish(
        owner,
        |name, table| unsafe { (a.register_service)(owner, KilnStr::of(name), table) },
        caps_table(),
    );
    if rc != KILN_OK {
        tracing::debug!(rc, "kiln.eml.values not published");
    }
    let loaders = eml_loader_loaded();
    // (a kiln-backend start's early phase; no stand-aside outcome in kiln mode)
    let kiln = with_ready && kiln_early(loaders.as_ref());
    let loaders = loaders.filter(|_| !kiln);
    if let Some(found) = loaders {
        RAN.store(true, Ordering::Release);
        let LoadedLoaders { why, who, paths, several } = found;
        if several.is_some() {
            klog(2, &format!("kiln-eml: standing aside: {why}. Remove them to let Kiln run the EML mods (kiln/state/eml.json)"));
        } else {
            klog(3, &format!("kiln-eml: standing aside: {why}. Remove it to let Kiln run the EML mods (kiln/state/eml.json)"));
        }
        let sf = who.starts_with("ShroudForge") && kpath(KILN_PATH_GAME).is_some_and(|g| shroudforge_mode(&g));
        let mut out = aside_outcome(&why, who, sf, None);
        out.loaders = paths;
        if several.is_some() {
            out.reason = several;
        }
        publish(&out, state_dir(kpath(KILN_PATH_KILN).as_deref()).as_deref());
    }
    // a modified EML loader's settings next to the game while no such loader runs: its code (a hotkey) does nothing
    if let Some(game) = kpath(KILN_PATH_GAME)
        && let Some(line) = crate::modified::settings_without_loader(game.as_std_path(), MODIFIED_LOADED.load(Ordering::Acquire))
    {
        klog(2, &line);
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
            // (what the startup seam's members noted, logged here on the boot thread)
            for line in crate::startup_seam::late_notes() {
                klog(2, &line);
            }
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
        // Kiln's interpreter has this start: kiln-eml is its launch plan's data writer, and no runtime reserves the run
        // (kiln-shroudforge stands aside before it would ask). Nothing changes; collection stays open.
        if kiln_mode::active(&api().map_or(Launch::Absent, |a| unsafe { read_launch(a.service) }), std::env::var("KILN_SHROUDFORGE").ok().as_deref()) {
            klog(2, &format!("kiln-eml: refused a request to stand aside (\"{reason}\"): Kiln's interpreter has \
                this start (kiln.launch), and kiln-eml is its launch plan's data writer"));
            return KILN_ERR_UNAVAILABLE;
        }
        let _added = ADDED.lock().unwrap_or_else(|e| e.into_inner());
        if RAN.load(Ordering::Acquire) { return KILN_ERR_UNAVAILABLE; }
        if let Some(game) = kpath(KILN_PATH_GAME) {
            if runner::lock_game(&game).is_err() { return KILN_ERR_UNAVAILABLE; }
            settle_owed_handoff_under_hosted(&game);
        } else { return KILN_ERR_UNAVAILABLE; }
        RAN.store(true, Ordering::Release);
        let out = aside_outcome(reason, "ShroudForge (hosted by Kiln)", true, Some(&mut host_skip));
        publish(&out, state_dir(kpath(KILN_PATH_KILN).as_deref()).as_deref());
        KILN_OK
    })).unwrap_or(KILN_ERR_INTERNAL)
}

/// Has the ShroudForge runtime Kiln hosts in this process skip the package `id` (its mod.json id) this start? Through its
/// optional export shroudforge_host_skip_packages (the fork's kiln/abi/host_skip.h, 0.2.0-dev.31.12). kiln-shroudforge
/// loaded `shroudforge-runtime.dll` and asks kiln-eml to stand aside (`eml_stand_aside`) before ShroudForge's startup
/// pass, which is when the runtime takes the list. False: a runtime without the export (an installed ShroudForge, an
/// older bundled one), one that refused, or none loaded; the package then runs, and its row says so.
fn host_skip(id: &str) -> bool {
    #[cfg(windows)]
    {
        use std::ffi::c_void;
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn GetModuleHandleW(name: *const u16) -> *mut c_void;
            fn GetProcAddress(module: *mut c_void, name: *const u8) -> *mut c_void;
        }
        let name: Vec<u16> = "shroudforge-runtime.dll".encode_utf16().chain(Some(0)).collect();
        let module = unsafe { GetModuleHandleW(name.as_ptr()) };
        if module.is_null() { return false; }
        let export = unsafe { GetProcAddress(module, c"shroudforge_host_skip_packages".as_ptr().cast()) };
        if export.is_null() { return false; }
        // SAFETY: the fork's declaration, bool shroudforge_host_skip_packages(const uint16_t* ids)
        let skip: unsafe extern "C" fn(*const u16) -> bool = unsafe { std::mem::transmute(export) };
        let ids: Vec<u16> = id.encode_utf16().chain(Some(0)).collect();
        unsafe { skip(ids.as_ptr()) }
    }
    #[cfg(not(windows))]
    {
        let _ = id;
        false
    }
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

// Every kiln.launch record but kiln leaves the whole start as without the table
#[cfg(test)]
#[path = "plugin_records_tests.rs"]
mod records_tests;

#[cfg(test)]
mod tests {
    use super::*;

    /// ShroudForge writes its `paths.mods` from a canonical path (`\\?\C:\…\mods`). The folders kiln-eml names in it,
    /// in kiln.log, eml.json and the launcher's engine status, are that root joined with a folder's name: they read
    /// `C:\…\mods\bare`, as Windows and the launcher name them, never `//?/C:/…/mods\bare`.
    #[cfg(windows)]
    #[test]
    fn shroudforges_mods_folder_is_named_as_windows_names_it() {
        let game = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-sf-root-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&game);
        let mods = game.join("mods");
        std::fs::create_dir_all(mods.join("bare").join("src")).unwrap();
        std::fs::write(mods.join("bare").join("mod.json"), r#"{"id":"bare","name":"N","version":"1.0.0","capabilities":["patch"]}"#).unwrap();
        std::fs::write(mods.join("bare").join("src").join("mod.lua"), "-- x").unwrap();
        std::fs::create_dir_all(game.join("shroudforge").join("config")).unwrap();
        for configured in [format!(r"\\?\{mods}"), format!(r"\\?\{mods}").replace('\\', "/"), mods.as_str().replace('\\', "/")] {
            std::fs::write(game.join("shroudforge").join("config").join("modloader-config.json"),
                serde_json::json!({"paths": {"mods": configured}}).to_string()).unwrap();
            let root = shroudforge_root(&game);
            // what a dedicated server's kiln.log and eml.json say about a package ShroudForge has off
            let rows = discover::aside_dropins(&root, &game.join("kiln"), &[], &[], "ShroudForge (hosted by Kiln)", true);
            let reason = rows[0].reason.as_deref().unwrap();
            assert!(reason.contains(&format!(r"at {mods}\bare switched off")), "{configured}: {reason}");
            let listed: Vec<_> = discover::folder_candidates(&root, "game-mods").into_iter().map(|c| c.path.to_string()).collect();
            assert_eq!(listed, [format!(r"{mods}\bare")], "{configured}");
            assert_eq!(root.as_str(), mods.as_str(), "{configured}");
        }
        std::fs::remove_dir_all(&game).unwrap();
    }

    /// Proton without a dbghelp override (player report 2026-11-17, Holster Toggle): the line names who the file is
    /// (it was ShroudForge's loader, not EML's) and the file it is, and for a modified EML loader that its own code does
    /// not run either.
    #[test]
    fn the_unloaded_loader_line_says_who_and_what_does_not_run() {
        let sf = unloaded_line(r"S:\steamapps\common\Enshrouded\dbghelp.dll", "ShroudForge's loader (it runs EML packages itself)", None);
        assert!(sf.contains(": ShroudForge's loader is in the folder but this process loaded another dbghelp.dll (Wine's builtin"), "{sf}");
        assert!(!sf.contains("modified"), "{sf}");
        let holster = unloaded_line("C:/Game/dbghelp.dll", "EML's own loader", Some("a weapon holster hotkey; settings in m182_native.json"));
        assert!(holster.contains(": EML's loader is in the folder but this process loaded another dbghelp.dll"), "{holster}");
        assert!(holster.ends_with("It is a modified EML loader with code of its own (a weapon holster hotkey; settings in \
            m182_native.json): that code does not run either, only the EML mods' Lua part does"), "{holster}");
    }

    #[test]
    fn recognizes_renamed_eml_loaders_before_deciding_to_patch() {
        for path in [r"C:\Game\dbghelp.dll", r"C:\Game\dbghelp_mod.dll", r"C:\Game\DBGHELP_HoardersHelper.DLL",
            r"C:\Game\dinput8.dll", r"C:\Game\kiln\chain\custom-eml.dll"] {
            assert!(loader_module_candidate(path), "loaded EML at {path} must be inspected before kiln-eml runs");
        }
        assert!(!loader_module_candidate(r"C:\Game\unrelated.dll"));
        assert!(!loader_module_candidate(r"C:\Game\dinput8x.dll"));
    }
    /// Issue 23: `dinput8_eml.dll` and a mod's bundled `dbghelp_*.dll` EML build in one server process (EML's log:
    /// "Applied 2281 patches" twice per start). Both are EML loaders, and kiln-eml says there are two and to keep one.
    #[test]
    fn two_eml_loaders_in_one_process_are_reported() {
        let eml = |tail: &[u8]| [b"MZ...EML_LOG_FILE_PATH...".as_slice(), tail].concat();
        let a = r"C:\Servers\Game\dinput8_eml.dll".to_string();
        let b = r"C:\Servers\Game\dbghelp_XHL-Unlimited-FlameAltars.dll".to_string();
        assert!(loader_module_candidate(&a), "a renamed dinput8 build of EML's loader is a candidate");
        assert!(loader_module_candidate(&b));
        assert_eq!(eml_loader_who(&eml(b"")), Some("EML's own loader"));
        assert_eq!(eml_loader_who(b"MZ plain proxy"), None);
        assert_eq!(eml_loader_who(&eml(b"KilnProxy/2 0.2.0")), None, "Kiln's proxy names EML but is not its loader");
        let one = loaded_loaders(vec![(b.clone(), "EML's own loader")]).unwrap();
        assert!(one.several.is_none() && one.paths == [b.clone()]);
        assert!(one.why.starts_with(&format!("{b} is loaded: EML's own loader patched")), "{}", one.why);
        let two = loaded_loaders(vec![(a.clone(), "EML's own loader"), (b.clone(), "EML's own loader")]).unwrap();
        let note = two.several.as_deref().expect("two loaders are reported");
        assert!(note.starts_with(&format!("two EML loaders in this process ({a}, {b})")), "{note}");
        assert!(note.contains("Keep one"), "{note}");
        assert!(two.why.starts_with(note), "the stand-aside line leads with it: {}", two.why);
        assert_eq!(two.paths, [a.clone(), b.clone()]);
        assert!(several_loaders_note(&[a.clone(), b.clone(), "C:\\x\\dbghelp.dll".into()]).unwrap().starts_with("3 EML loaders"));
        // the state file: `reason` carries the sentence the launcher's engines view shows, `loaders` the paths
        let mut out = Outcome::new(Phase::Aside, Vec::new());
        out.aside = Some(two.why.clone());
        out.loaders = two.paths.clone();
        out.reason = two.several.clone();
        let v = serde_json::to_value(&out).unwrap();
        assert_eq!(v["loaders"].as_array().map(Vec::len), Some(2));
        assert!(v["reason"].as_str().unwrap().contains("Keep one"));
        assert!(serde_json::to_value(Outcome::new(Phase::Aside, Vec::new())).unwrap().get("loaders").is_none());
    }
    #[test]
    fn pe_identity_reads_the_header_fields() {
        let mut b = vec![0u8; 512];
        b[0..2].copy_from_slice(b"MZ");
        b[0x3c..0x40].copy_from_slice(&0x80u32.to_le_bytes());
        b[0x80..0x84].copy_from_slice(b"PE\0\0");
        b[0x88..0x8c].copy_from_slice(&0x1234u32.to_le_bytes());
        b[0x98 + 16..0x98 + 20].copy_from_slice(&0x1000u32.to_le_bytes());
        b[0x98 + 56..0x98 + 60].copy_from_slice(&0x9000u32.to_le_bytes());
        assert_eq!(pe_identity(&b), Some((0x1234, 0x9000, 0x1000)));
        let mut other = b.clone();
        other[0x88] = 0x35;
        assert_ne!(pe_identity(&other), pe_identity(&b), "a builtin with another header is not the file");
        assert_eq!(pe_identity(b"MZ"), None);
    }

    #[test]
    fn a_busy_install_cannot_return_to_the_game_entry_point() {
        const CHILD: &str = "KILN_EML_BUSY_START_TEST";
        if let Ok(game) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(game);
            let mut go = [0u8];
            std::io::Read::read_exact(&mut std::io::stdin(), &mut go).unwrap();
            let out = runner::run(&RunConfig { game_dir: game.clone(), cache_dir: game.join("cache"),
                file_name: Some("enshrouded".into()), patch: true, ..Default::default() }, Vec::new(), Vec::new());
            enforce_startup_safety(&out);
            panic!("the game entry point was reached while another process owned the install");
        }
        let game = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-busy-start-{}", std::process::id()));
        std::fs::create_dir_all(&game).unwrap();
        // The second process is created before this one takes the lock, and starts only once told to: a process created
        // while the lock is held would share its file description (the test gate never creates one then).
        let mut child = mod_loader_lua::test_gate::spawn(std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "plugin::tests::a_busy_install_cannot_return_to_the_game_entry_point"])
            .env(CHILD, game.as_str()).stdin(std::process::Stdio::piped())).unwrap();
        let _release = runner::GameLockRelease(game.clone());
        runner::lock_game(&game).unwrap();
        std::io::Write::write_all(&mut child.stdin.take().unwrap(), b"g").unwrap();
        let result = child.wait().unwrap();
        runner::release_game_lock(&game);
        assert_eq!(result.code(), Some(1));
        let _ = std::fs::remove_dir_all(game);
    }
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

    // ---- kiln.launch: fakes of the core's tables, per thread ----

    /// The fake core's game folder (KilnApi.path), set only in a child process.
    static GAME_DIR: std::sync::OnceLock<String> = std::sync::OnceLock::new();

    std::thread_local! {
        /// What the fake kiln.launch serves: None, no table; Some((header, status's result, record)).
        static LAUNCH: std::cell::RefCell<Option<(KilnServiceHeader, Option<i32>, KilnLaunchStatus)>> = const { std::cell::RefCell::new(None) };
        static LAUNCH_TABLE: std::cell::RefCell<Option<Box<KilnLaunchApi>>> = const { std::cell::RefCell::new(None) };
        static ASKED: std::cell::RefCell<Vec<(String, u32)>> = const { std::cell::RefCell::new(Vec::new()) };
        static PROFILE_JSON: std::cell::Cell<&'static str> = const { std::cell::Cell::new("") };
        /// What the fake kiln.profile's mode answers (an active profile unless a test says otherwise).
        static PROFILE_MODE: std::cell::Cell<u32> = const { std::cell::Cell::new(KILN_PROFILE_ACTIVE) };
        static LOGGED: std::cell::RefCell<Vec<(i32, String)>> = const { std::cell::RefCell::new(Vec::new()) };
    }

    fn record(backend: u32, code: u32) -> KilnLaunchStatus {
        let mut r: KilnLaunchStatus = unsafe { std::mem::zeroed() };
        (r.size, r.backend, r.frozen, r.reason_code, r.launch_id) = (192, backend, 1, code, 0x1234);
        r.plan_hash = [7; 32];
        r
    }

    unsafe extern "C" fn fake_status(out: *mut KilnLaunchStatus) -> i32 {
        LAUNCH.with_borrow(|l| {
            let (_, rc, r) = l.as_ref().unwrap();
            assert_eq!(unsafe { (*out).size }, 192, "the caller asks for the v1 record");
            unsafe { *out = *r };
            rc.unwrap_or(KILN_OK)
        })
    }

    unsafe extern "C" fn fake_service(name: KilnStr, version: u32) -> *const c_void {
        let name = unsafe { name.to_string() };
        ASKED.with_borrow_mut(|a| a.push((name.clone(), version)));
        if name == "kiln.profile" { return fake_profile_table(); }
        if name != "kiln.launch" { return std::ptr::null(); }
        LAUNCH.with_borrow(|l| {
            let Some((header, rc, _)) = l else { return std::ptr::null() };
            let table = Box::new(KilnLaunchApi {
                header: KilnServiceHeader { size: header.size, version: header.version },
                status: if *rc == Some(-1) { None } else { Some(fake_status) },
                notice_copy: std::ptr::null(),
            });
            let ptr = &*table as *const KilnLaunchApi as *const c_void;
            LAUNCH_TABLE.with_borrow_mut(|t| *t = Some(table));
            ptr
        })
    }

    fn serve(header: (u32, u32), rc: Option<i32>, r: Option<KilnLaunchStatus>) {
        LAUNCH.with_borrow_mut(|l| *l = r.map(|r| (KilnServiceHeader { size: header.0, version: header.1 }, rc, r)));
    }

    /// Every record shape and how kiln-eml classifies it: as kiln-sfi's read_launch and kiln-shroudforge's read_frozen do.
    #[test]
    fn the_frozen_record_is_classified_as_the_other_loaders_classify_it() {
        let read = || unsafe { read_launch(fake_service) };
        let full = (std::mem::size_of::<KilnLaunchApi>() as u32, 1);
        serve(full, None, None);
        assert_eq!(read(), Launch::Absent, "no table: an older core");
        assert_eq!(ASKED.with_borrow(|a| a.last().cloned()), Some(("kiln.launch".to_owned(), 1)));
        for (code, want) in [(0x001, Launch::Absent), (0x0ff, Launch::Absent), (0x100, Launch::Refused), (0x106, Launch::Refused),
                             (0x1ff, Launch::Refused), (0x200, Launch::Idle), (0x204, Launch::Idle), (0x2ff, Launch::Idle),
                             (0x300, Launch::Absent)] {
            serve(full, None, Some(record(KILN_LAUNCH_BACKEND_NONE, code)));
            assert_eq!(read(), want, "none, {code:#x}");
        }
        for backend in [KILN_LAUNCH_BACKEND_HOSTED, KILN_LAUNCH_BACKEND_INSTALLED] {
            serve(full, None, Some(record(backend, 0x002)));
            assert_eq!(read(), Launch::Shroudforge);
        }
        serve(full, None, Some(record(KILN_LAUNCH_BACKEND_KILN, 0x005)));
        assert_eq!(read(), Launch::Kiln { plan_hash: [7; 32], launch_id: 0x1234 });
        serve(full, None, Some(record(4, 0x005)));
        assert_eq!(read(), Launch::Absent, "an unknown backend");
        // a table or record that cannot be read: the rules from before the table
        let kiln = record(KILN_LAUNCH_BACKEND_KILN, 0x005);
        for (header, rc, r, what) in [
            ((full.0, 0), None, kiln, "table version 0"),
            ((8, 1), None, kiln, "a table without status"),
            ((15, 1), None, kiln, "a table that ends inside status"),
            (full, Some(-1), kiln, "a null status"),
            (full, Some(KILN_ERR_UNAVAILABLE), kiln, "not frozen"),
            (full, None, KilnLaunchStatus { frozen: 0, ..kiln }, "frozen 0"),
            (full, None, KilnLaunchStatus { frozen: 2, ..kiln }, "frozen 2"),
            (full, None, KilnLaunchStatus { size: 188, ..kiln }, "a shorter record"),
        ] {
            serve(header, rc, Some(r));
            assert_eq!(read(), Launch::Absent, "{what}");
        }
        serve((16, 1), None, Some(kiln));
        assert_eq!(read(), Launch::Kiln { plan_hash: [7; 32], launch_id: 0x1234 }, "a v1 table without notice_copy still has status");
    }

    fn fake_profile_table() -> *const c_void {
        unsafe extern "C" fn mode() -> u32 { PROFILE_MODE.get() }
        unsafe extern "C" fn text() -> KilnStr { KilnStr::of(PROFILE_JSON.get()) }
        unsafe extern "C" fn zero() -> u64 { 0 }
        unsafe extern "C" fn count(_: u32) -> u32 { 0 }
        unsafe extern "C" fn none() -> u32 { 0 }
        let table = Box::leak(Box::new(KilnProfileApi {
            header: KilnServiceHeader { size: std::mem::size_of::<KilnProfileApi>() as u32, version: 1 },
            mode, id: text, name: text, generation: zero, count, get: std::ptr::null(), dropins: none, config_dir: text,
            data_dir: text, json: text,
        }));
        table as *const KilnProfileApi as *const c_void
    }

    fn fake_api() -> *mut KilnApi {
        unsafe extern "C" fn version() -> KilnStr { KilnStr::lit("test") }
        unsafe extern "C" fn log(_: u64, level: i32, text: KilnStr) { LOGGED.with_borrow_mut(|l| l.push((level, unsafe { text.to_string() }))); }
        unsafe extern "C" fn path(kind: i32, buf: *mut u8, cap: usize, len: *mut usize) -> i32 {
            let Some(game) = GAME_DIR.get() else { return KILN_ERR_UNAVAILABLE };
            let p = match kind {
                KILN_PATH_GAME => game.clone(),
                KILN_PATH_KILN => format!("{game}/kiln"),
                KILN_PATH_LOGS => format!("{game}/kiln/logs"),
                KILN_PATH_STATE => format!("{game}/kiln/state"),
                _ => return KILN_ERR_UNAVAILABLE,
            };
            unsafe { *len = p.len() };
            if cap < p.len() { return KILN_ERR_BUFFER_TOO_SMALL; }
            unsafe { std::ptr::copy_nonoverlapping(p.as_ptr(), buf, p.len()) };
            KILN_OK
        }
        unsafe extern "C" fn register(_: u64, _: KilnStr, _: *const c_void) -> i32 { KILN_OK }
        // build_info: the executable the strict test plans are made for (kiln_mode tests' EXE), a client
        unsafe extern "C" fn build_info(out: *mut kiln_abi::KilnBuildInfo) -> i32 {
            let exe = crate::kiln_mode::tests::EXE.as_bytes();
            unsafe {
                (*out).side = kiln_abi::KILN_SIDE_CLIENT as u32;
                for (i, b) in exe.iter().enumerate() { (*out).exe_sha256[i] = *b as _; }
                (*out).exe_sha256[64] = 0;
            }
            KILN_OK
        }
        let null = std::ptr::null();
        Box::leak(Box::new(KilnApi {
            size: std::mem::size_of::<KilnApi>() as u32, abi_version: 1, version, log, build_info: build_info as *const c_void, feature: null, path,
            owner_create: null, owner_revoke: null, subscribe_tick: null, unsubscribe: null, post: null, on_tick_thread: null,
            tick_info: null, subscribe_log: null, symbol: null, constant: null, service: fake_service, register_service: register,
        }))
    }

    /// run_now's route (kiln_route): the frozen record through the core's lookup, the profile's plan reference, the
    /// environment, and the kiln.log line. In child processes: the API pointer and the environment are process-wide.
    #[test]
    fn the_route_follows_the_record_the_plan_and_the_environment() {
        const CHILD: &str = "KILN_EML_ROUTE_TEST_CHILD";
        if let Ok(game) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(game);
            let kiln = game.join("kiln");
            API.store(fake_api(), Ordering::Release);
            use crate::kiln_mode::tests::{p, strict_plan};
            let text = strict_plan(&[p("a", "mods/a", "shroudforge", &["patch"], "candidate")]);
            let text = text.as_str();
            let id = crate::kiln_mode::tests::PLAN;
            std::fs::create_dir_all(kiln.join("launch-plans")).unwrap();
            std::fs::write(kiln.join(format!("launch-plans/{id}.json")), text).unwrap();
            use sha2::Digest as _;
            let hash: [u8; 32] = sha2::Sha256::digest(text).as_slice().try_into().unwrap();
            PROFILE_JSON.set(Box::leak(serde_json::json!({"sfLaunchPlan": {"path": format!("launch-plans/{id}.json"), "sha256": "x"}}).to_string().into_boxed_str()));
            let full = (std::mem::size_of::<KilnLaunchApi>() as u32, 1);
            let route = |r: Option<KilnLaunchStatus>| {
                serve(full, None, r);
                LOGGED.with_borrow_mut(|l| l.clear());
                let mode = kiln_route(&game, &kiln);
                (mode.map(|m| m.plan_read()), LOGGED.with_borrow(|l| l.clone()))
            };
            let kiln_record = KilnLaunchStatus { plan_hash: hash, ..record(KILN_LAUNCH_BACKEND_KILN, 0x005) };
            if std::env::var("KILN_SHROUDFORGE").as_deref() == Ok("0") {
                // KILN_SHROUDFORGE=0 stands Kiln's interpreter aside too: the rules from before, nothing new logged
                assert_eq!(route(Some(kiln_record)), (None, Vec::new()));
                return;
            }
            let (mode, log) = route(Some(kiln_record));
            assert_eq!(mode, Some(true));
            assert_eq!(log, [(3, format!("kiln-eml: Kiln's interpreter has this start (kiln.launch); kiln-eml \
                follows its launch plan launch-plans/{id}.json (startup dialect ShroudForge)"))]);
            // the record froze another plan than the file now: kiln mode, nothing runs, and kiln.log says why
            let (mode, log) = route(Some(KilnLaunchStatus { plan_hash: [9; 32], ..kiln_record }));
            assert_eq!(mode, Some(false));
            assert_eq!(log, [(2, format!("kiln-eml: Kiln's interpreter has this start (kiln.launch), but its \
                launch plan cannot be read again (launch-plans/{id}.json changed since this start froze it): no EML mod runs \
                this start"))]);
            // every other record, and no table: the rules from before, nothing new logged
            for r in [None, Some(record(KILN_LAUNCH_BACKEND_HOSTED, 1)), Some(record(KILN_LAUNCH_BACKEND_INSTALLED, 3)),
                      Some(record(KILN_LAUNCH_BACKEND_NONE, 0x101)), Some(record(KILN_LAUNCH_BACKEND_NONE, 0x204))] {
                assert_eq!(route(r), (None, Vec::new()));
            }
            return;
        }
        let game = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-route-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&game);
        for env in [None, Some("1"), Some("0")] {
            let mut child = std::process::Command::new(std::env::current_exe().unwrap());
            child.args(["--exact", "plugin::tests::the_route_follows_the_record_the_plan_and_the_environment"]).env(CHILD, game.as_str());
            match env {
                Some(v) => child.env("KILN_SHROUDFORGE", v),
                None => child.env_remove("KILN_SHROUDFORGE"),
            };
            let out = mod_loader_lua::test_gate::output(&mut child).unwrap();
            assert!(out.status.success() && String::from_utf8_lossy(&out.stdout).contains("1 passed"), "KILN_SHROUDFORGE={env:?}: {}{}",
                String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
        let _ = std::fs::remove_dir_all(&game);
    }

    /// Under Kiln's interpreter kiln-eml is the launch plan's data writer: a request to stand aside is refused, nothing is
    /// locked or published and collection stays open. KILN_SHROUDFORGE=0, hosted and no table keep the rule from before.
    /// In child processes: the API pointer and RAN are process-wide.
    #[test]
    fn stand_aside_is_refused_under_kiln() {
        const CHILD: &str = "KILN_EML_ASIDE_TEST_CHILD";
        if let Ok(case) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(std::env::var("KILN_EML_ASIDE_GAME").unwrap());
            let _ = std::fs::remove_dir_all(&game);
            std::fs::create_dir_all(game.join("mods/a/src")).unwrap();
            GAME_DIR.set(game.to_string()).unwrap();
            API.store(fake_api(), Ordering::Release);
            match case.as_str() {
                "none" => serve((24, 1), None, None),
                "hosted" => serve((24, 1), None, Some(record(KILN_LAUNCH_BACKEND_HOSTED, 0x001))),
                _ => serve((24, 1), None, Some(record(KILN_LAUNCH_BACKEND_KILN, 0x005))),
            }
            let rc = unsafe { eml_stand_aside(KilnStr::lit("hosted ShroudForge runs EML")) };
            let published = game.join("kiln/state/eml.json").exists();
            let locked = game.join(".kiln-eml.lock").exists();
            let added = unsafe { eml_add_mod(KilnStr::of(game.join("mods/a").as_str()), KilnStr::lit("plugin")) };
            let refused = LOGGED.with_borrow(|l| l.iter().any(|(level, t)| *level == 2 && t.starts_with("kiln-eml: refused a request to stand aside")));
            if case == "kiln" && std::env::var("KILN_SHROUDFORGE").as_deref() != Ok("0") {
                assert_eq!((rc, published, locked, added, refused), (KILN_ERR_UNAVAILABLE, false, false, KILN_OK, true), "kiln");
            } else {
                assert_eq!((rc, published, locked, added, refused), (KILN_OK, true, true, KILN_ERR_UNAVAILABLE, false), "{case}");
            }
            let _ = std::fs::remove_dir_all(&game);
            return;
        }
        for (case, env) in [("kiln", None), ("kiln", Some("0")), ("hosted", None), ("none", None)] {
            let game = std::env::temp_dir().join(format!("kiln-eml-aside-{case}-{}", std::process::id()));
            let mut child = std::process::Command::new(std::env::current_exe().unwrap());
            child.args(["--exact", "plugin::tests::stand_aside_is_refused_under_kiln"]).env(CHILD, case).env("KILN_EML_ASIDE_GAME", &game);
            match env {
                Some(v) => child.env("KILN_SHROUDFORGE", v),
                None => child.env_remove("KILN_SHROUDFORGE"),
            };
            let out = mod_loader_lua::test_gate::output(&mut child).unwrap();
            assert!(out.status.success() && String::from_utf8_lossy(&out.stdout).contains("1 passed"), "{case} {env:?}: {}{}",
                String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
    }

    /// The game's executable stem in a child process (`exe_stem`): the test binary's own name is not a game's.
    pub(super) static EXE_STEM: std::sync::OnceLock<String> = std::sync::OnceLock::new();

    /// A hosted start's stand_aside makes the handoff an earlier kiln-mode start owes ShroudForge. With
    /// the marker there and ShroudForge's record `applied` for this archive, the record is marked stale (`failed`, Kiln's
    /// text) and the marker goes, and kiln.log says so. ShroudForge busy (its write lock held) keeps both and kiln.log says
    /// the next start tries again; the hosted start goes on either way. In child processes: the API pointer, RAN and the
    /// stem are process-wide.
    #[test]
    fn a_hosted_stand_aside_settles_the_handoff_a_kiln_start_owes() {
        const CHILD: &str = "KILN_EML_OWED_TEST_CHILD";
        if let Ok(case) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(std::env::var("KILN_EML_OWED_GAME").unwrap());
            let _ = std::fs::remove_dir_all(&game);
            std::fs::create_dir_all(game.join("shroudforge/config")).unwrap();
            std::fs::create_dir_all(game.join("kiln/eml/ownership")).unwrap();
            let state = game.join("shroudforge/state.json");
            std::fs::write(&state, serde_json::json!({"schemaVersion": 1, "windows": {"favorite": "cyan"}, "assets": {
                "schemaVersion": 1, "status": "applied", "phase": "startup", "target": "enshrouded", "fingerprint": "a".repeat(64),
                "completedAt": 42, "mods": [{"id": "player-sf", "version": "1.0.0"}]}}).to_string()).unwrap();
            let marker = game.join("kiln/eml/ownership/enshrouded.sf-handoff-owed.json");
            std::fs::write(&marker, br#"{"format":1,"stem":"enshrouded","text":"owed"}"#).unwrap();
            GAME_DIR.set(game.to_string()).unwrap();
            EXE_STEM.set("enshrouded".into()).unwrap();
            API.store(fake_api(), Ordering::Release);
            serve((24, 1), None, Some(record(KILN_LAUNCH_BACKEND_HOSTED, 0x001)));
            // this test locks ShroudForge's file itself: under the test gate (no process is created meanwhile)
            let _hold = mod_loader_lua::test_gate::lock_hold();
            let busy = (case == "busy").then(|| {
                let f = std::fs::File::create(game.join("shroudforge/config/.shroudforge-write.lock")).unwrap();
                f.lock().unwrap();
                f
            });
            let rc = unsafe { eml_stand_aside(KilnStr::lit("hosted ShroudForge runs EML")) };
            let sf: serde_json::Value = serde_json::from_slice(&std::fs::read(&state).unwrap()).unwrap();
            let logged = LOGGED.with_borrow(|l| l.clone());
            assert_eq!(rc, KILN_OK, "the hosted start goes on: {logged:?}");
            assert_eq!(sf["windows"]["favorite"], "cyan", "ShroudForge's other state is kept");
            if busy.is_some() {
                assert!(marker.exists(), "the marker stays for the next start");
                assert_eq!(sf["assets"]["status"], "applied");
                assert!(logged.iter().any(|(level, t)| *level == 2
                    && t.starts_with("kiln-eml: could not mark ShroudForge's record of its prepared asset mods stale")), "{logged:?}");
            } else {
                assert!(!marker.exists(), "the owed handoff was made");
                assert_eq!(sf["assets"]["status"], "failed", "{sf}");
                assert!(logged.contains(&(3, "kiln-eml: marked ShroudForge's record of its prepared asset mods stale: Kiln's \
                    interpreter changed the game data since ShroudForge last ran".to_owned())), "{logged:?}");
            }
            drop(busy);
            crate::runner::release_game_lock(&game);
            let _ = std::fs::remove_dir_all(&game);
            return;
        }
        for case in ["owed", "busy"] {
            let game = std::env::temp_dir().join(format!("kiln-eml-owed-{case}-{}", std::process::id()));
            let mut child = std::process::Command::new(std::env::current_exe().unwrap());
            child.args(["--exact", "plugin::tests::a_hosted_stand_aside_settles_the_handoff_a_kiln_start_owes"])
                .env(CHILD, case).env("KILN_EML_OWED_GAME", &game).env_remove("KILN_SHROUDFORGE");
            let out = mod_loader_lua::test_gate::output(&mut child).unwrap();
            assert!(out.status.success() && String::from_utf8_lossy(&out.stdout).contains("1 passed"), "{case}: {}{}",
                String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
    }

    /// run_now end to end (kiln_plugin_main without an early step): the route, the launcher's delegated entries and the
    /// runner reach kiln/state/eml.json. Kiln mode never reads kiln-shroudforge's state file; a hosted record keeps the
    /// rule from before (it does). In child processes: the API pointer and the run's statics are process-wide.
    #[test]
    fn kiln_mode_reaches_the_state_file_through_the_plugin() {
        const CHILD: &str = "KILN_EML_PRESTART_TEST_CHILD";
        const SENTINEL: &str = "SF-STATE-REASON-sentinel";
        if let Ok(case) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(std::env::var("KILN_EML_PRESTART_GAME").unwrap());
            let _ = std::fs::remove_dir_all(&game);
            GAME_DIR.set(game.to_string()).unwrap();
            let kiln = game.join("kiln");
            std::fs::create_dir_all(kiln.join("state")).unwrap();
            std::fs::write(kiln.join("state/shroudforge.json"), serde_json::json!({
                "pid": std::process::id(), "mode": "off", "reason": SENTINEL }).to_string()).unwrap();
            for (id, caps) in [("quiver", serde_json::json!(["runtime"])), ("stacks", serde_json::json!(["patch"]))] {
                std::fs::create_dir_all(game.join(format!("mods/{id}/src"))).unwrap();
                std::fs::write(game.join(format!("mods/{id}/mod.json")), serde_json::json!({
                    "id": id, "name": id, "version": "1.0.0", "capabilities": caps }).to_string()).unwrap();
                std::fs::write(game.join(format!("mods/{id}/src/mod.lua")), "-- fixture").unwrap();
            }
            use crate::kiln_mode::tests::{p, strict_plan};
            let plan = strict_plan(&[p("quiver", "mods/quiver", "eml", &["runtime"], "candidate"),
                p("stacks", "mods/stacks", "eml", &["patch"], "candidate")]);
            let id = crate::kiln_mode::tests::PLAN;
            std::fs::create_dir_all(kiln.join("launch-plans")).unwrap();
            std::fs::write(kiln.join(format!("launch-plans/{id}.json")), &plan).unwrap();
            use sha2::Digest as _;
            let hash: [u8; 32] = sha2::Sha256::digest(&plan).as_slice().try_into().unwrap();
            PROFILE_JSON.set(Box::leak(serde_json::json!({
                "mods": {"eml": [{"id": "quiver", "path": "../mods/quiver", "enabled": false},
                                 {"id": "stacks", "path": "../mods/stacks", "enabled": true}]},
                "emlRuntime": ["quiver"],
                "sfLaunchPlan": {"path": format!("launch-plans/{id}.json"), "sha256": "x"},
            }).to_string().into_boxed_str()));
            let r = match case.as_str() {
                "kiln" => KilnLaunchStatus { plan_hash: hash, ..record(KILN_LAUNCH_BACKEND_KILN, 0x005) },
                _ => record(KILN_LAUNCH_BACKEND_HOSTED, 0x001),
            };
            serve((std::mem::size_of::<KilnLaunchApi>() as u32, 1), None, Some(r));
            let mut info = KilnPluginInfo { size: std::mem::size_of::<KilnPluginInfo>() as u32, id: KilnStr::lit(""),
                version: KilnStr::lit(""), on_unload: None, user: std::ptr::null_mut(), on_ready: None };
            assert_eq!(unsafe { kiln_plugin_main(fake_api(), 1, &mut info) }, KILN_OK);
            let text = std::fs::read_to_string(kiln.join("state/eml.json")).unwrap();
            let state: serde_json::Value = serde_json::from_str(&text).unwrap();
            let row = |id: &str| {
                let m = state["mods"].as_array().unwrap().iter().find(|m| m["id"] == id).unwrap_or_else(|| panic!("{id}: {text}"));
                (m["state"].as_str().unwrap().to_owned(), m["reason"].as_str().unwrap_or("").to_owned())
            };
            let plan_name = format!("launch-plans/{id}.json");
            if case == "kiln" {
                assert!(!text.contains(SENTINEL), "kiln mode read shroudforge.json: {text}");
                assert_eq!(row("quiver"), ("skipped".into(), format!("this start's launch plan ({plan_name}) gives this package \
                    to Kiln's interpreter, with no startup data to apply this start, so kiln-eml has nothing to run for it")));
                // a participant of the startup pass: no early phase ran here (kiln_plugin_main), so no start and nothing runs
                assert_eq!(row("stacks"), ("skipped".into(), crate::startup_seam::NO_START.into()), "{text}");
            } else {
                assert!(row("quiver").1.contains(SENTINEL), "the rule from before: {text}");
                assert_eq!(row("stacks"), ("pending".into(), String::new()), "the run took it (and found no game data here)");
            }
            let _ = std::fs::remove_dir_all(&game);
            return;
        }
        for case in ["kiln", "hosted"] {
            let game = std::env::temp_dir().join(format!("kiln-eml-prestart-{case}-{}", std::process::id()));
            let out = mod_loader_lua::test_gate::output(std::process::Command::new(std::env::current_exe().unwrap())
                .args(["--exact", "plugin::tests::kiln_mode_reaches_the_state_file_through_the_plugin"])
                .env(CHILD, case).env("KILN_EML_PRESTART_GAME", &game).env_remove("KILN_SHROUDFORGE")).unwrap();
            assert!(out.status.success() && String::from_utf8_lossy(&out.stdout).contains("1 passed"), "{case}: {}{}",
                String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
    }

    /// A hosted ShroudForge start without a profile (kiln.profile's legacy layout: a server from a server pack,
    /// 2026-11-21): kiln-eml stands aside, and the EML packages of ShroudForge's folder that it has off, and the EML mods of
    /// kiln/eml/mods, are said in one kiln.log warning. A server's state file lists them; a client's lists only
    /// kiln/eml/mods (its in-game menu lists ShroudForge's folder). An active profile with drop-ins off: as before. The
    /// files are left as they are. In child processes: the API pointer, RAN and the stem are process-wide.
    #[test]
    fn a_hosted_stand_aside_without_a_profile_names_the_dropins_nothing_runs() {
        const CHILD: &str = "KILN_EML_DROPINS_TEST_CHILD";
        if let Ok(case) = std::env::var(CHILD) {
            let game = Utf8PathBuf::from(std::env::var("KILN_EML_DROPINS_GAME").unwrap());
            let _ = std::fs::remove_dir_all(&game);
            let package = |dir: &str, id: &str, ext: Option<&str>| {
                let p = game.join(dir);
                std::fs::create_dir_all(p.join("src")).unwrap();
                std::fs::write(p.join("mod.json"), serde_json::json!({"id": id, "name": id, "version": "1.0.0", "capabilities": ["patch"]}).to_string()).unwrap();
                std::fs::write(p.join("src/mod.lua"), "-- fixture").unwrap();
                if let Some(ext) = ext { std::fs::write(p.join("extended.mod.json"), ext).unwrap(); }
            };
            package("mods/greeneye", "greeneye", None);
            package("mods/xhl", "xhl", Some(r#"{"schemaVersion":1,"launcher":"EML","enabled":false}"#));
            package("mods/on", "on", Some(crate::discover::SERVER_SWITCH_FILE));
            package("kiln/eml/mods/drop", "drop", None);
            let before = std::fs::read_dir(game.join("mods")).unwrap().count();
            GAME_DIR.set(game.to_string()).unwrap();
            EXE_STEM.set(if case == "client" { "enshrouded" } else { "enshrouded_server" }.into()).unwrap();
            if case == "profile" {
                PROFILE_JSON.set(r#"{"mods":{"eml":[]},"dropins":false}"#);
            } else {
                PROFILE_MODE.set(0);  // KILN_PROFILE_LEGACY: no kiln/profile.json
            }
            API.store(fake_api(), Ordering::Release);
            serve((24, 1), None, Some(record(KILN_LAUNCH_BACKEND_HOSTED, 0x001)));
            let rc = unsafe { eml_stand_aside(KilnStr::lit("ShroudForge owns startup asset patching and runs EML packages in its mods folder")) };
            let logged = LOGGED.with_borrow(|l| l.clone());
            assert_eq!(rc, KILN_OK, "{logged:?}");
            let text = std::fs::read_to_string(game.join("kiln/state/eml.json")).unwrap();
            let state: serde_json::Value = serde_json::from_str(&text).unwrap();
            assert_eq!(state["phase"], "aside", "{text}");
            let rows: Vec<(String, String)> = state["mods"].as_array().unwrap().iter()
                .map(|m| (m["id"].as_str().unwrap().to_owned(), m["state"].as_str().unwrap().to_owned())).collect();
            let warned: Vec<&String> = logged.iter().filter(|(level, t)| *level == 2 && t.contains("EML package(s) in")).map(|(_, t)| t).collect();
            match case.as_str() {
                "server" => {
                    assert_eq!(rows, [("greeneye".to_owned(), "off-in-shroudforge".to_owned()), ("xhl".into(), "off-in-shroudforge".into()),
                        ("drop".into(), "not-loaded".into())], "{text}");
                    assert_eq!(warned.len(), 1, "{logged:?}");
                    assert!(warned[0].starts_with("kiln-eml: 2 EML package(s) in ") && warned[0].contains("(greeneye, xhl)")
                        && warned[0].contains("a server needs an extended.mod.json"), "{}", warned[0]);
                }
                "client" => {
                    assert_eq!(rows, [("drop".to_owned(), "not-loaded".to_owned())], "{text}");
                    assert_eq!(warned.len(), 1, "{logged:?}");
                    assert!(warned[0].contains("switch them on in Kiln's in-game menu"), "{}", warned[0]);
                }
                _ => {
                    assert!(rows.is_empty(), "{text}");
                    assert!(warned.is_empty() && !logged.iter().any(|(_, t)| t.contains("kiln/eml/mods")), "{logged:?}");
                }
            }
            // listing only: no switch written, nothing moved
            assert!(!game.join("mods/greeneye/extended.mod.json").exists());
            assert_eq!(std::fs::read_dir(game.join("mods")).unwrap().count(), before);
            crate::runner::release_game_lock(&game);
            let _ = std::fs::remove_dir_all(&game);
            return;
        }
        for case in ["server", "client", "profile"] {
            let game = std::env::temp_dir().join(format!("kiln-eml-dropins-{case}-{}", std::process::id()));
            let mut child = std::process::Command::new(std::env::current_exe().unwrap());
            child.args(["--exact", "plugin::tests::a_hosted_stand_aside_without_a_profile_names_the_dropins_nothing_runs"])
                .env(CHILD, case).env("KILN_EML_DROPINS_GAME", &game).env_remove("KILN_SHROUDFORGE");
            let out = mod_loader_lua::test_gate::output(&mut child).unwrap();
            assert!(out.status.success() && String::from_utf8_lossy(&out.stdout).contains("1 passed"), "{case}: {}{}",
                String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
    }

}
