// SPDX-License-Identifier: MIT
//! kiln: shroudlink's server core as a Kiln plugin (`<game>/kiln/plugins/kiln-shroudlink.dll`). Kiln's own file, added
//! to the vendored core (docs/kiln/engines/shroudlink.md). In place of the `steam_api64.dll` proxy:
//!
//! 1. `kiln_plugin_early` hands shroudlink's EML data mods (bigger stacks/chests) to kiln-eml, the one writer of game
//!    data (`kiln_eml.h`: `kiln_eml_service(1)->add_mod`);
//! 2. `kiln_plugin_main` + `on_ready`: finds the mods (the profile's `mods.shroudlink` list, then `shroudlink_mods/`,
//!    the rule of every Kiln engine plugin), steps aside when a real shroudlink core runs in this process, and starts
//!    the core unchanged (`crate::start`) on its own thread, as its DllMain does;
//! 3. mirrors shroudlink's log into kiln.log (`[shroudlink] ...`) and writes `kiln/state/shroudlink.json`.
//!
//! Only the KilnApi fields used here are declared, in the order of native/core/abi/kiln_api.h.

use std::ffi::c_void;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicPtr, AtomicU64, Ordering};
use std::sync::Mutex;

use serde_json::{json, Value};
use windows_sys::Win32::Foundation::HMODULE;
use windows_sys::Win32::System::LibraryLoader::{GetModuleFileNameW, GetModuleHandleW, GetProcAddress};
use windows_sys::Win32::System::ProcessStatus::K32EnumProcessModules;
use windows_sys::Win32::System::Threading::GetCurrentProcess;

use crate::{hooks, plugins};

pub const PLUGIN_ID: &str = "kiln-shroudlink";
pub const PLUGIN_VERSION: &str = concat!(env!("CARGO_PKG_VERSION"), "+kiln.1");

const KILN_OK: i32 = 0;
const KILN_ERR_UNAVAILABLE: i32 = 3;
const LOG_ERROR: i32 = 1;
const LOG_WARN: i32 = 2;
const LOG_INFO: i32 = 3;
const PATH_GAME: i32 = 0;
const PATH_KILN: i32 = 1;
const PATH_DATA: i32 = 4;
const PATH_STATE: i32 = 7;
const PROFILE_LEGACY: u32 = 0;
const PROFILE_NO_MODS: u32 = 3;
const PROFILE_INVALID: u32 = 4;
/// The key of shroudlink's mods in a Kiln profile (`mods.shroudlink`), as `mods.greeneye` for Greeneye.
pub const ENGINE: &str = "shroudlink";

#[repr(C)]
#[derive(Clone, Copy)]
pub struct KilnStr {
    ptr: *const u8,
    len: usize,
}

impl KilnStr {
    fn of(s: &str) -> Self {
        Self { ptr: s.as_ptr(), len: s.len() }
    }
    fn lit(s: &'static str) -> Self {
        Self::of(s)
    }
    unsafe fn text(self) -> String {
        if self.ptr.is_null() || self.len == 0 {
            return String::new();
        }
        String::from_utf8_lossy(std::slice::from_raw_parts(self.ptr, self.len)).into_owned()
    }
}

type Fp = *const c_void;

#[repr(C)]
pub struct KilnApi {
    size: u32,
    abi_version: u32,
    version: Fp,
    log: unsafe extern "C" fn(u64, i32, KilnStr),
    build_info: Fp,
    feature: Fp,
    path: unsafe extern "C" fn(i32, *mut u8, usize, *mut usize) -> i32,
    owner_create: Fp,
    owner_revoke: Fp,
    subscribe_tick: Fp,
    unsubscribe: Fp,
    post: Fp,
    on_tick_thread: Fp,
    tick_info: Fp,
    subscribe_log: Fp,
    symbol: Fp,
    constant: Fp,
    service: unsafe extern "C" fn(KilnStr, u32) -> *const c_void,
    register_service: Fp,
    server_world: Fp,
    singleton: Fp,
    side: unsafe extern "C" fn() -> u32,
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
struct KilnServiceHeader {
    size: u32,
    version: u32,
}

#[repr(C)]
struct KilnProfileApi {
    header: KilnServiceHeader,
    mode: unsafe extern "C" fn() -> u32,
    id: unsafe extern "C" fn() -> KilnStr,
    name: Fp,
    generation: Fp,
    count: Fp,
    get: Fp,
    dropins: Fp,
    config_dir: Fp,
    data_dir: Fp,
    json: unsafe extern "C" fn() -> KilnStr,
}

/// kiln_eml.h KilnEmlApi v1 (the table only: kiln-eml is GPL and never linked)
#[repr(C)]
struct KilnEmlApi {
    header: KilnServiceHeader,
    add_mod: unsafe extern "C" fn(KilnStr, KilnStr) -> i32,
    phase: unsafe extern "C" fn() -> u32,
    state_json: Fp,
}

static API: AtomicPtr<KilnApi> = AtomicPtr::new(std::ptr::null_mut());
static OWNER: AtomicU64 = AtomicU64::new(0);

fn api() -> Option<&'static KilnApi> {
    unsafe { API.load(Ordering::Acquire).as_ref() }
}

fn klog(level: i32, text: &str) {
    if let Some(a) = api() {
        let line = format!("[shroudlink] {text}");
        unsafe { (a.log)(OWNER.load(Ordering::Relaxed), level, KilnStr::of(&line)) };
    }
}

/// Every line shroudlink writes to shroudlink.log also goes to kiln.log.
fn sink(line: &str) {
    let level = if line.starts_with("PANIC") || line.starts_with("CRASH") { LOG_ERROR } else if line.contains(" off: ") { LOG_WARN } else { LOG_INFO };
    klog(level, line.strip_prefix("shroudlink: ").unwrap_or(line));
}

fn kiln_path(kind: i32) -> Option<PathBuf> {
    let a = api()?;
    let mut buf = vec![0u8; 2048];
    let mut n = 0usize;
    if unsafe { (a.path)(kind, buf.as_mut_ptr(), buf.len(), &mut n) } != KILN_OK || n == 0 || n > buf.len() {
        return None;
    }
    buf.truncate(n);
    Some(PathBuf::from(String::from_utf8_lossy(&buf).into_owned()))
}

fn side_name() -> &'static str {
    match api() {
        // `side` was appended to KilnApi; an older core is server-only
        Some(a) if a.size as usize >= std::mem::size_of::<KilnApi>() && unsafe { (a.side)() } == 1 => "client",
        _ => "server",
    }
}

fn profile() -> Option<&'static KilnProfileApi> {
    let a = api()?;
    let p = unsafe { (a.service)(KilnStr::lit("kiln.profile"), 1) } as *const KilnProfileApi;
    let p = unsafe { p.as_ref()? };
    (p.header.size as usize >= std::mem::size_of::<KilnProfileApi>()).then_some(p)
}

// ---- the mod list -------------------------------------------------------------------------------------------------

/// One profile entry of `mods.shroudlink`: a mod DLL, or a folder of mod DLLs and/or EML data mods.
#[derive(Clone, Debug, PartialEq)]
pub struct Entry {
    pub id: String,
    pub path: PathBuf,
    pub enabled: bool,
}

/// What the profile says about shroudlink (the rule of docs/kiln/engines/README.md "Where mods are found").
#[derive(Clone, Debug, PartialEq)]
pub struct Plan {
    /// false: play-once no-mods or an invalid profile, nothing loads
    pub load: bool,
    pub listed: Vec<Entry>,
    /// also read `<game>/shroudlink_mods/`
    pub read_folder: bool,
    pub note: String,
}

/// A profile path as kiln-core resolves it: '/' -> '\', relative to `<game>/kiln/`.
fn resolve(path: &str, kiln: &Path) -> PathBuf {
    let p = path.replace('/', "\\");
    let b = p.as_bytes();
    let absolute = (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\') || p.starts_with("\\\\");
    if absolute {
        PathBuf::from(p)
    } else {
        kiln.join(p)
    }
}

/// The plan from the profile mode and text (pure: host-tested).
pub fn plan_from(mode: Option<u32>, profile_id: &str, text: &str, kiln: &Path) -> Plan {
    let mut plan = Plan { load: true, listed: Vec::new(), read_folder: true, note: String::new() };
    let invalid = |why: &str| Plan { load: false, listed: Vec::new(), read_folder: false, note: why.into() };
    match mode {
        None => {
            plan.note = "no kiln.profile service: shroudlink_mods/ only".into();
            return plan;
        }
        Some(PROFILE_NO_MODS) => return Plan { note: "play-once no-mods: nothing loads".into(), ..invalid("") },
        Some(PROFILE_INVALID) => return invalid("the profile is invalid: nothing loads"),
        Some(PROFILE_LEGACY) => {
            plan.note = "no profile (legacy layout): shroudlink_mods/".into();
            return plan;
        }
        Some(_) => {}
    }
    let Ok(doc) = serde_json::from_str::<Value>(text) else { return invalid("the profile text is invalid: nothing loads") };
    let Some(obj) = doc.as_object() else { return invalid("the profile text is invalid: nothing loads") };
    let mods = obj.get("mods");
    if mods.is_some_and(|m| !m.is_object()) {
        return invalid("the profile's mods are invalid: nothing loads");
    }
    let list = mods.and_then(|m| m.get(ENGINE));
    if list.is_some_and(|l| !l.is_array()) {
        return invalid("the profile's shroudlink list is invalid: nothing loads");
    }
    let dropins = obj.get("dropins").and_then(Value::as_bool).unwrap_or(false);
    if let Some(list) = list.and_then(Value::as_array) {
        for m in list {
            let path = m.get("path").and_then(Value::as_str).filter(|p| !p.is_empty() && !p.contains('\0'));
            let id_ok = m.get("id").map_or(true, |i| i.as_str().is_some_and(|s| !s.is_empty() && !s.contains('\0')));
            let enabled_ok = m.get("enabled").map_or(true, Value::is_boolean);
            let (Some(path), true, true) = (path, id_ok, enabled_ok) else {
                return invalid("a shroudlink profile entry is invalid: nothing loads");
            };
            plan.listed.push(Entry {
                id: m.get("id").and_then(Value::as_str).unwrap_or("").to_string(),
                path: resolve(path, kiln),
                enabled: m.get("enabled").and_then(Value::as_bool).unwrap_or(true),
            });
        }
        plan.read_folder = dropins;
    }
    plan.note = format!(
        "profile '{profile_id}': {} listed{}",
        plan.listed.len(),
        match (plan.read_folder, list.is_some()) {
            (false, _) => "",
            (true, true) => ", plus shroudlink_mods/ (dropins)",
            (true, false) => ", plus shroudlink_mods/ (the profile does not manage shroudlink)",
        }
    );
    plan
}

fn plan() -> Plan {
    let kiln = kiln_path(PATH_KILN).unwrap_or_default();
    match profile() {
        None => plan_from(None, "", "", &kiln),
        Some(p) => unsafe { plan_from(Some((p.mode)()), &(p.id)().text(), &(p.json)().text(), &kiln) },
    }
}

/// True for an EML data mod folder (mod.json + src/mod.lua), the kind kiln-eml runs.
pub fn is_eml_mod(dir: &Path) -> bool {
    dir.join("mod.json").is_file() && dir.join("src").join("mod.lua").is_file()
}

/// EML data mods among the roots: a root that is one, or its direct subfolders that are.
pub fn eml_mods(roots: &[PathBuf]) -> Vec<PathBuf> {
    let mut out = Vec::new();
    for root in roots.iter().filter(|r| r.is_dir()) {
        if is_eml_mod(root) {
            out.push(root.clone());
            continue;
        }
        let Ok(entries) = std::fs::read_dir(root) else { continue };
        let mut subs: Vec<PathBuf> = entries.flatten().map(|e| e.path()).filter(|p| p.is_dir() && is_eml_mod(p)).collect();
        subs.sort();
        out.extend(subs);
    }
    out
}

/// The roots shroudlink's loader reads, in order: enabled profile entries, then `<game>/shroudlink_mods`.
pub fn roots(plan: &Plan, game: &Path) -> Vec<PathBuf> {
    if !plan.load {
        return Vec::new();
    }
    let mut roots: Vec<PathBuf> = plan.listed.iter().filter(|e| e.enabled).map(|e| e.path.clone()).collect();
    if plan.read_folder {
        roots.push(game.join(plugins::DIR));
    }
    roots
}

/// Mod DLLs below the roots (a root DLL, or the DLLs directly in a root folder), as the loader will see them.
pub fn mod_dlls(roots: &[PathBuf]) -> Vec<PathBuf> {
    let is_dll = |p: &Path| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll"));
    let mut out = Vec::new();
    for root in roots {
        if root.is_file() {
            if is_dll(root) {
                out.push(root.clone());
            }
        } else if let Ok(entries) = std::fs::read_dir(root) {
            let mut v: Vec<PathBuf> = entries.flatten().map(|e| e.path()).filter(|p| p.is_file() && is_dll(p)).collect();
            v.sort();
            out.extend(v);
        }
    }
    out
}

// ---- step aside -----------------------------------------------------------------------------------------------------

/// A real shroudlink core already loaded in this process: `steam_api64.dll`/`crashhandler64.dll`/`dinput8.dll` (also
/// chained by Kiln from kiln/chain/) or `shroudlink_core.dll` loaded by the client addon. The core is the only DLL
/// exporting both `CrashHandlerFactory` and `DirectInput8Create`.
fn real_core_in_process() -> Option<String> {
    let ours = own_module();
    let mut mods: Vec<HMODULE> = vec![std::ptr::null_mut(); 1024];
    let mut needed = 0u32;
    let ok = unsafe { K32EnumProcessModules(GetCurrentProcess(), mods.as_mut_ptr(), (mods.len() * 8) as u32, &mut needed) };
    if ok == 0 {
        return None;
    }
    mods.truncate((needed as usize / 8).min(1024));
    for m in mods {
        if m.is_null() || m == ours {
            continue;
        }
        let both = unsafe { GetProcAddress(m, b"CrashHandlerFactory\0".as_ptr()).is_some() && GetProcAddress(m, b"DirectInput8Create\0".as_ptr()).is_some() };
        let name = module_path(m);
        let is_core_name = name.rsplit(['\\', '/']).next().is_some_and(|n| n.eq_ignore_ascii_case("shroudlink_core.dll"));
        if both || is_core_name {
            return Some(name);
        }
    }
    None
}

fn own_module() -> HMODULE {
    use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleExW, GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT};
    let mut m: HMODULE = std::ptr::null_mut();
    unsafe { GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, own_module as *const u16, &mut m) };
    m
}

fn module_path(m: HMODULE) -> String {
    let mut buf = [0u16; 1024];
    let n = unsafe { GetModuleFileNameW(m, buf.as_mut_ptr(), buf.len() as u32) } as usize;
    String::from_utf16_lossy(&buf[..n])
}

// ---- state --------------------------------------------------------------------------------------------------------

struct State {
    plan_note: String,
    stepped_aside: String,
    started: bool,
    claimed_elsewhere: bool,
    profile_mods: Vec<Entry>,
    eml: Vec<(String, String)>,
}

static STATE: Mutex<State> = Mutex::new(State {
    plan_note: String::new(),
    stepped_aside: String::new(),
    started: false,
    claimed_elsewhere: false,
    profile_mods: Vec::new(),
    eml: Vec::new(),
});

fn write_state() {
    let Some(dir) = kiln_path(PATH_STATE) else { return };
    let st = STATE.lock().unwrap_or_else(|e| e.into_inner());
    let outcomes = plugins::OUTCOMES.lock().unwrap_or_else(|e| e.into_inner()).clone();
    let mut mods: Vec<Value> = outcomes
        .iter()
        .map(|(p, r)| match r {
            Ok(id) => json!({ "id": id, "dll": p.display().to_string(), "status": "loaded", "reason": "" }),
            Err(e) => json!({ "id": p.file_stem().map(|s| s.to_string_lossy().to_string()).unwrap_or_default(), "dll": p.display().to_string(), "status": "skipped", "reason": e }),
        })
        .collect();
    for e in st.profile_mods.iter().filter(|e| !e.enabled) {
        mods.push(json!({ "id": e.id, "dll": e.path.display().to_string(), "status": "skipped", "reason": "switched off in the profile" }));
    }
    let (on, off): (Vec<Value>, serde_json::Map<String, Value>) = if st.started {
        let on = hooks::working().into_iter().map(Value::from).collect();
        let off = hooks::FEATURES.iter().filter_map(|&f| hooks::problem(f).map(|why| (f.name().to_string(), Value::from(why)))).collect();
        (on, off)
    } else {
        (Vec::new(), serde_json::Map::new())
    };
    let doc = json!({
        "format": 1,
        "plugin": PLUGIN_ID,
        "version": PLUGIN_VERSION,
        "side": side_name(),
        "plan": st.plan_note,
        "steppedAside": !st.stepped_aside.is_empty(),
        "steppedAsideReason": st.stepped_aside,
        "started": st.started,
        "claimedElsewhere": st.claimed_elsewhere,
        "features": on,
        "featuresOff": off,
        "mods": mods,
        "eml": st.eml.iter().map(|(p, r)| json!({ "path": p, "result": r })).collect::<Vec<_>>(),
        "updatedUnixMs": std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0),
    });
    let file = dir.join("shroudlink.json");
    let tmp = dir.join("shroudlink.json.tmp");
    let text = serde_json::to_string_pretty(&doc).unwrap_or_default() + "\n";
    if std::fs::write(&tmp, text).and_then(|_| std::fs::rename(&tmp, &file)).is_err() {
        klog(LOG_WARN, &format!("cannot write {}", file.display()));
    }
}

// ---- entry points ---------------------------------------------------------------------------------------------------

/// KILN_ABI_VERSION 1, and a table long enough for every field declared here.
unsafe fn api_usable(api: *const KilnApi) -> bool {
    api.as_ref().is_some_and(|a| a.abi_version == 1 && a.size as usize >= std::mem::offset_of!(KilnApi, side))
}

/// Early phase: hand the EML data mods among shroudlink's mods to kiln-eml before the game reads its data.
#[no_mangle]
pub unsafe extern "C" fn kiln_plugin_early(api: *const KilnApi, owner: u64, info: *mut KilnPluginInfo) -> i32 {
    if !api_usable(api) {
        return KILN_ERR_UNAVAILABLE;
    }
    API.store(api as *mut KilnApi, Ordering::Release);
    OWNER.store(owner, Ordering::Relaxed);
    if let Some(info) = info.as_mut() {
        info.id = KilnStr::lit(PLUGIN_ID);
        info.version = KilnStr::lit(PLUGIN_VERSION);
    }
    let plan = plan();
    let game = kiln_path(PATH_GAME).unwrap_or_default();
    let found = eml_mods(&roots(&plan, &game));
    if found.is_empty() {
        return KILN_ERR_UNAVAILABLE;
    }
    let name: Vec<u16> = "kiln-eml.dll".encode_utf16().chain([0]).collect();
    let module = GetModuleHandleW(name.as_ptr());
    let get = if module.is_null() { None } else { GetProcAddress(module, b"kiln_eml_service\0".as_ptr()) };
    let eml: Option<&KilnEmlApi> = get.and_then(|f| {
        let f: unsafe extern "C" fn(u32) -> *const KilnEmlApi = std::mem::transmute(f);
        f(1).as_ref().filter(|t| t.header.size as usize >= std::mem::size_of::<KilnEmlApi>())
    });
    let mut st = STATE.lock().unwrap_or_else(|e| e.into_inner());
    for dir in &found {
        let path = dir.display().to_string();
        let result = match eml {
            None => {
                klog(LOG_WARN, &format!("EML data mod {path} not run: kiln-eml.dll is not installed (it is Kiln's EML runner)"));
                "kiln-eml missing".to_string()
            }
            Some(t) => match (t.add_mod)(KilnStr::of(&path), KilnStr::lit("shroudlink")) {
                KILN_OK => {
                    klog(LOG_INFO, &format!("EML data mod {path} handed to kiln-eml"));
                    "handed to kiln-eml".to_string()
                }
                rc => {
                    klog(LOG_WARN, &format!("kiln-eml refused EML data mod {path} ({rc})"));
                    format!("refused ({rc})")
                }
            },
        };
        st.eml.push((path, result));
    }
    KILN_OK
}

#[no_mangle]
pub unsafe extern "C" fn kiln_plugin_main(api: *const KilnApi, owner: u64, info: *mut KilnPluginInfo) -> i32 {
    if info.is_null() || !api_usable(api) {
        return KILN_ERR_UNAVAILABLE;
    }
    API.store(api as *mut KilnApi, Ordering::Release);
    OWNER.store(owner, Ordering::Relaxed);
    let info = &mut *info;
    info.id = KilnStr::lit(PLUGIN_ID);
    info.version = KilnStr::lit(PLUGIN_VERSION);
    if info.size as usize >= std::mem::size_of::<KilnPluginInfo>() {
        info.on_ready = Some(on_ready);
    } else {
        klog(LOG_ERROR, "this Kiln core has no on_ready; shroudlink stays off");
        return KILN_ERR_UNAVAILABLE;
    }
    KILN_OK
}

unsafe extern "C" fn on_ready(_user: *mut c_void) {
    let plan = plan();
    let game = kiln_path(PATH_GAME).unwrap_or_else(chestsort_common::game_dir);
    let roots = roots(&plan, &game);
    {
        let mut st = STATE.lock().unwrap_or_else(|e| e.into_inner());
        st.plan_note = plan.note.clone();
        st.profile_mods = plan.listed.clone();
    }
    klog(LOG_INFO, &format!("{PLUGIN_ID} {PLUGIN_VERSION} ({}): {}", side_name(), plan.note));
    if let Some(core) = real_core_in_process() {
        let why = format!("shroudlink's own core runs in this process ({core}); its mods in shroudlink_mods/ load there");
        klog(LOG_INFO, &format!("{PLUGIN_ID} steps aside: {why}"));
        STATE.lock().unwrap_or_else(|e| e.into_inner()).stepped_aside = why;
        write_state();
        return;
    }
    let dlls = mod_dlls(&roots);
    for e in plan.listed.iter().filter(|e| !e.enabled) {
        klog(LOG_INFO, &format!("{} ({}): switched off in the profile", if e.id.is_empty() { "?" } else { &e.id }, e.path.display()));
    }
    if dlls.is_empty() {
        klog(LOG_INFO, "no shroudlink mods (profile mods.shroudlink, shroudlink_mods/): the core stays off, nothing is hooked");
        write_state();
        return;
    }
    klog(LOG_INFO, &format!("starting shroudlink's core for {} mod DLL(s)", dlls.len()));
    chestsort_common::log::set_sink(sink);
    let shadow = kiln_path(PATH_DATA).unwrap_or_else(|| game.join("kiln").join("data")).join("shroudlink").join("loaded");
    // as the core's own DllMain: on a thread of its own, so the game goes on starting while it scans and hooks
    std::thread::spawn(move || {
        let started = crate::start(move || plugins::load_from(roots, shadow));
        {
            let mut st = STATE.lock().unwrap_or_else(|e| e.into_inner());
            st.started = started;
            st.claimed_elsewhere = !started;
        }
        if started {
            klog(LOG_INFO, &format!("shroudlink core running: features [{}]", hooks::working().join(", ")));
        } else {
            klog(LOG_INFO, &format!("{PLUGIN_ID} steps aside: another shroudlink core claimed this process first"));
        }
        write_state();
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    fn kiln() -> PathBuf {
        PathBuf::from("C:\\g\\kiln")
    }

    #[test]
    fn legacy_and_missing_service_read_the_folder() {
        for mode in [None, Some(PROFILE_LEGACY)] {
            let p = plan_from(mode, "", "", &kiln());
            assert!(p.load && p.read_folder && p.listed.is_empty());
        }
    }

    #[test]
    fn no_mods_and_invalid_load_nothing() {
        for mode in [PROFILE_NO_MODS, PROFILE_INVALID] {
            let p = plan_from(Some(mode), "x", "{}", &kiln());
            assert!(!p.load && !p.read_folder);
            assert!(roots(&p, Path::new("C:\\g")).is_empty());
        }
        assert!(!plan_from(Some(1), "x", "not json", &kiln()).load);
        assert!(!plan_from(Some(1), "x", r#"{"mods":{"shroudlink":{}}}"#, &kiln()).load);
        assert!(!plan_from(Some(1), "x", r#"{"mods":{"shroudlink":[{"id":"a"}]}}"#, &kiln()).load);
        assert!(!plan_from(Some(1), "x", r#"{"mods":{"shroudlink":[{"path":"a","enabled":"no"}]}}"#, &kiln()).load);
    }

    #[test]
    fn profile_list_replaces_the_folder_unless_dropins() {
        let text = r#"{"mods":{"shroudlink":[{"id":"chestsort","path":"library/chestsort/chestsort.dll"},{"id":"meter","path":"D:/m/meter","enabled":false}]}}"#;
        let p = plan_from(Some(1), "main", text, &kiln());
        assert!(p.load && !p.read_folder);
        assert_eq!(p.listed[0].path, PathBuf::from("C:\\g\\kiln").join("library\\chestsort\\chestsort.dll"));
        assert_eq!(p.listed[1].path, PathBuf::from("D:\\m\\meter"));
        assert!(!p.listed[1].enabled);
        let r = roots(&p, Path::new("C:\\g"));
        assert_eq!(r.len(), 1, "the disabled entry is not a root");
        let with = plan_from(Some(1), "main", &text.replace("{\"mods\"", "{\"dropins\":true,\"mods\""), &kiln());
        assert!(with.read_folder);
        assert_eq!(roots(&with, Path::new("C:\\g")).last().unwrap(), &Path::new("C:\\g").join("shroudlink_mods"));
        // a profile that does not list shroudlink keeps the hand-installed folder
        let other = plan_from(Some(1), "main", r#"{"mods":{"kiln":[]}}"#, &kiln());
        assert!(other.load && other.read_folder);
    }

    /// The loader over Kiln's roots, with shroudlink's own example mod (hello_mod.dll next to the test binary's
    /// folder, as upstream's plugins::tests::example_plugin_round_trip expects; build.sh --test builds it).
    #[test]
    fn loads_a_real_mod_from_a_profile_root() {
        let dll = std::env::current_exe().unwrap().parent().unwrap().parent().unwrap().join("hello_mod.dll");
        if !dll.exists() {
            eprintln!("skipped: build hello_mod first ({})", dll.display());
            return;
        }
        let base = std::env::temp_dir().join(format!("kiln-shroudlink-load-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&base);
        let lib = base.join("library").join("hello");
        std::fs::create_dir_all(&lib).unwrap();
        std::fs::copy(&dll, lib.join("hello_mod.dll")).unwrap();
        std::fs::write(lib.join("broken.dll"), b"not a dll").unwrap();
        plugins::load_from(vec![lib.clone(), base.join("missing")], base.join("shadow"));
        let out = plugins::OUTCOMES.lock().unwrap().clone();
        assert!(out.iter().any(|(p, r)| p == &lib.join("hello_mod.dll") && r.as_deref() == Ok("hello")), "{out:?}");
        assert!(out.iter().any(|(p, r)| p == &lib.join("broken.dll") && r.is_err()), "{out:?}");
        assert!(base.join("shadow").read_dir().unwrap().count() >= 1, "loaded from a shadow copy");
    }

    #[test]
    fn finds_dlls_and_eml_mods() {
        let base = std::env::temp_dir().join(format!("kiln-shroudlink-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&base);
        let mods = base.join("shroudlink_mods");
        std::fs::create_dir_all(mods.join("stacks").join("src")).unwrap();
        std::fs::write(mods.join("b.dll"), b"").unwrap();
        std::fs::write(mods.join("a.DLL"), b"").unwrap();
        std::fs::write(mods.join("notes.txt"), b"").unwrap();
        std::fs::write(mods.join("stacks").join("mod.json"), b"{}").unwrap();
        std::fs::write(mods.join("stacks").join("src").join("mod.lua"), b"").unwrap();
        let single = base.join("one.dll");
        std::fs::write(&single, b"").unwrap();
        let roots = vec![single.clone(), mods.clone()];
        let dlls = mod_dlls(&roots);
        assert_eq!(dlls, vec![single, mods.join("a.DLL"), mods.join("b.dll")]);
        assert_eq!(eml_mods(&roots), vec![mods.join("stacks")]);
        assert_eq!(eml_mods(&[mods.join("stacks")]), vec![mods.join("stacks")]);
        let _ = std::fs::remove_dir_all(&base);
    }
}
