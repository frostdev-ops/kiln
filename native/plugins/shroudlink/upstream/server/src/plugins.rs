//! Mod DLLs in `shroudlink_mods/` next to the server executable (see the
//! `shroudlink-sdk` crate for the ABI). Each becomes a regular `ServerMod`.
//!
//! Mods are loaded from shadow copies in `shroudlink_mods/.loaded/`, so the DLL in
//! `shroudlink_mods/` can be overwritten while the server runs; a changed DLL is loaded
//! again within seconds (hot reload). The old copy stays loaded but unused.

use std::ffi::c_void;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, Instant, SystemTime};

use chestsort_common::log;
use serde_json::{json, Value};
use shroudlink_sdk::{HostApi, InventoryEvent, ModDecl, ModDeclV1, ModDeclV2, ABI_VERSION};
use windows_sys::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryW};

use crate::game_api;
use crate::link_host::Host;
use crate::mods::{self, ClientId, ServerMod};

pub const DIR: &str = "shroudlink_mods";
const SHADOW_DIR: &str = ".loaded";
const RELOAD_CHECK: Duration = Duration::from_secs(2);

/// `ctx` is the network `Host` inside message callbacks, null inside inventory events
/// (the message is queued and sent by the next network update).
unsafe extern "C" fn host_send(ctx: *mut c_void, client: u64, json: *const u8, len: usize) {
    match serde_json::from_slice::<Value>(std::slice::from_raw_parts(json, len)) {
        Ok(message) => match (ctx as *mut Host).as_mut() {
            Some(host) => host.send(ClientId(client), &message),
            None => mods::queue(ClientId(client), message),
        },
        Err(e) => log!("shroudlink: mod sent invalid JSON ({e})"),
    }
}

unsafe extern "C" fn host_log(text: *const u8, len: usize) {
    log!("{}", String::from_utf8_lossy(std::slice::from_raw_parts(text, len)));
}

static API: HostApi = HostApi {
    version: ABI_VERSION,
    send: host_send,
    log: host_log,
    slots: game_api::slots,
    is_player: game_api::is_player,
    chest_name: game_api::chest_name,
    max_stack: game_api::max_stack,
    move_stack: game_api::move_stack,
    players: crate::world::api_players,
    position: crate::world::api_position,
    teleport: crate::world::api_teleport,
    player_of: crate::world::api_player_of,
    read_component: crate::world::api_read_component,
    write_component: crate::world::api_write_component,
    inventories_near: crate::world::api_inventories_near,
    inventory: crate::world::api_inventory,
    move_stack_between: crate::world::api_move_stack,
    apply_buff: crate::world::api_apply_buff,
    player_name: crate::world::api_player_name,
    entities_near: crate::world::api_entities_near,
    move_items: crate::world::api_move_items,
    give_items: crate::items::api_give_items,
    remove_items: crate::items::api_remove_items,
    world_chest_name: crate::world::api_chest_name,
};

/// Ticks handed to v5 mods, for the self-test.
pub static TICKS: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

enum Decl {
    V1(&'static ModDeclV1),
    V2(&'static ModDeclV2),
    V3(&'static ModDecl),
}

struct Plugin {
    decl: Decl,
    id: &'static str,
    /// game event types (v3), asked once at load
    game_events: Vec<u32>,
}

// the mod's state is only touched under the mod registry lock
unsafe impl Send for Plugin {}

impl Plugin {
    /// The v1 part every version has (later versions append to it).
    fn v1(&self) -> &ModDeclV1 {
        match self.decl {
            Decl::V1(d) => d,
            Decl::V2(d) => unsafe { &*(d as *const ModDeclV2 as *const ModDeclV1) },
            Decl::V3(d) => unsafe { &*(d as *const ModDecl as *const ModDeclV1) },
        }
    }

    /// The v2 part (inventory events), for v2 and later.
    fn v2(&self) -> Option<&ModDeclV2> {
        match self.decl {
            Decl::V1(_) => None,
            Decl::V2(d) => Some(d),
            Decl::V3(d) => Some(unsafe { &*(d as *const ModDecl as *const ModDeclV2) }),
        }
    }
}

impl ServerMod for Plugin {
    fn id(&self) -> &'static str {
        self.id
    }
    fn describe(&self) -> Value {
        json!({ "id": self.id, "plugin": true })
    }
    fn on_join(&mut self, host: &mut Host, client: ClientId) {
        let d = self.v1();
        unsafe { (d.on_join)(d.state, &API, host as *mut Host as *mut c_void, client.0) }
    }
    fn on_message(&mut self, host: &mut Host, client: ClientId, message: &Value) {
        let text = message.to_string();
        let d = self.v1();
        unsafe { (d.on_message)(d.state, &API, host as *mut Host as *mut c_void, client.0, text.as_ptr(), text.len()) }
    }
    fn on_leave(&mut self, host: &mut Host, client: ClientId) {
        let d = self.v1();
        unsafe { (d.on_leave)(d.state, &API, host as *mut Host as *mut c_void, client.0) }
    }
    fn keep(&mut self, game: *mut c_void, event: &InventoryEvent) -> Vec<u64> {
        let Some(d) = self.v2() else { return Vec::new() };
        let mut out = vec![0u64; 64];
        let n = unsafe { (d.keep)(d.state, &API, game, event, out.as_mut_ptr(), out.len()) };
        if n > out.len() {
            out.resize(n, 0);
            unsafe { (d.keep)(d.state, &API, game, event, out.as_mut_ptr(), out.len()) };
        }
        out.truncate(n);
        out
    }
    fn on_transfer(&mut self, game: *mut c_void, event: &InventoryEvent) -> u32 {
        let Some(d) = self.v2() else { return 0 };
        unsafe { (d.on_transfer)(d.state, &API, game, event) }
    }
    fn game_events(&self) -> Vec<u32> {
        self.game_events.clone()
    }
    fn on_game_event(&mut self, hash: u32, data: &[u8]) {
        if let Decl::V3(d) = self.decl {
            unsafe { (d.on_game_event)(d.state, &API, hash, data.as_ptr(), data.len()) }
        }
    }
    fn on_tick(&mut self, host: &mut Host) {
        match self.decl {
            Decl::V3(d) if d.version >= 5 => {
                TICKS.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
                unsafe { (d.on_tick)(d.state, &API, host as *mut Host as *mut c_void) }
            }
            _ => {}
        }
    }
}

unsafe fn load(path: &Path) -> Result<Plugin, String> {
    let wide: Vec<u16> = path.as_os_str().to_string_lossy().encode_utf16().chain([0]).collect();
    #[cfg(not(feature = "kiln"))]
    let module = LoadLibraryW(wide.as_ptr());
    // kiln: dependency search is limited to the DLL/application/system folders, never cwd or PATH.
    #[cfg(feature = "kiln")]
    let module = kiln_sl_load(wide.as_ptr());
    if module.is_null() {
        return Err("cannot load".into());
    }
    let decl = if let Some(entry) = GetProcAddress(module, b"shroudlink_mod_v2\0".as_ptr()) {
        // the declaration says which layout it has: v2, or v3 with game events appended
        let entry: extern "C" fn() -> *const ModDeclV2 = std::mem::transmute(entry);
        let decl = entry().as_ref().ok_or("shroudlink_mod_v2 returned null")?;
        (decl.on_load)(decl.state, &API);
        match decl.version {
            2 => Decl::V2(decl),
            // v4 has the v3 layout, v5 appends on_tick (read only for v5 and later)
            3.. => Decl::V3(&*(decl as *const ModDeclV2 as *const ModDecl)),
            v => return Err(format!("ABI version {v} (host {ABI_VERSION})")),
        }
    } else {
        let entry = GetProcAddress(module, b"shroudlink_mod_v1\0".as_ptr()).ok_or("no shroudlink_mod_v2/v1 export")?;
        let entry: extern "C" fn() -> *const ModDeclV1 = std::mem::transmute(entry);
        let decl = entry().as_ref().ok_or("shroudlink_mod_v1 returned null")?;
        if decl.version != 1 {
            return Err(format!("ABI version {} (expected 1)", decl.version));
        }
        Decl::V1(decl)
    };
    let mut plugin = Plugin { decl, id: "", game_events: Vec::new() };
    if let Decl::V3(d) = plugin.decl {
        let mut hashes = vec![0u32; 256];
        let n = (d.game_events)(d.state, hashes.as_mut_ptr(), hashes.len());
        hashes.truncate(n.min(256));
        plugin.game_events = hashes;
    }
    let d = plugin.v1();
    let mut len = 0usize;
    let ptr = (d.id)(d.state, &mut len);
    plugin.id = std::str::from_utf8(std::slice::from_raw_parts(ptr, len)).map_err(|_| "id is not UTF-8")?;
    Ok(plugin)
}

// kiln: keep SEH outside the Rust initialization frame. Faulted modules remain resident, just like every loaded
// generation: initialization may already have started a thread or installed a hook, so FreeLibrary is unsafe.
#[cfg(feature = "kiln")]
extern "C" {
    fn kiln_sl_guard(run: unsafe extern "C" fn(*mut c_void), arg: *mut c_void) -> u32;
    fn kiln_sl_load(path: *const u16) -> windows_sys::Win32::Foundation::HMODULE;
}
#[cfg(feature = "kiln")]
fn guarded<T>(run: impl FnOnce() -> T) -> Result<T, String> {
    struct Call<F, T> { run: Option<F>, result: Option<T> }
    unsafe extern "C" fn invoke<F: FnOnce() -> T, T>(arg: *mut c_void) {
        let call = &mut *arg.cast::<Call<F, T>>();
        call.result = Some((call.run.take().unwrap())());
    }
    fn call<F: FnOnce() -> T, T>(run: F) -> Result<T, String> {
        let mut arg = Call { run: Some(run), result: None };
        let fault = unsafe { kiln_sl_guard(invoke::<F, T>, (&mut arg as *mut Call<F, T>).cast()) };
        if fault != 0 { Err(format!("native initialization fault 0x{fault:08x}")) }
        else { arg.result.ok_or_else(|| "native initialization returned no result".into()) }
    }
    call(run)
}

/// A mod DLL in `shroudlink_mods/` and the version of it that is loaded.
struct Source {
    path: PathBuf,
    modified: Option<SystemTime>,
}

struct Loader {
    /// kiln: mod folders and single mod DLLs (upstream: the one folder `shroudlink_mods/`)
    roots: Vec<PathBuf>,
    /// kiln: where shadow copies go (upstream: `shroudlink_mods/.loaded/`)
    shadow: PathBuf,
    sources: Vec<Source>,
    copies: u32,
    checked: Instant,
}

static LOADER: Mutex<Option<Loader>> = Mutex::new(None);

fn modified(path: &Path) -> Option<SystemTime> {
    std::fs::metadata(path).and_then(|m| m.modified()).ok()
}

impl Loader {
    /// Copies the DLL to a fresh shadow file and loads that.
    fn load_copy(&mut self, path: &Path) -> Result<Plugin, String> {
        // kiln: validate again on every reload and keep the source locked through the shadow copy.
        #[cfg(feature = "kiln")]
        let _source = {
            let root = self.roots.iter().find(|r| *r == path || path.parent() == Some(r.as_path())).ok_or("outside mod roots")?;
            let base = if root == path { root.parent().ok_or("no mod folder")? } else { root.as_path() };
            crate::kiln::CheckedPath::open(base, path, true).ok_or("unsafe mod path (link, traversal or outside root)")?
        };
        let shadow = self.shadow.clone();
        std::fs::create_dir_all(&shadow).map_err(|e| format!("cannot create {}: {e}", shadow.display()))?;
        // kiln: inspect the shadow root before creating a file through it, not only after copying.
        #[cfg(feature = "kiln")]
        let _shadow = crate::kiln::CheckedPath::open(&shadow, &shadow, false).ok_or("unsafe shadow folder")?;
        self.copies += 1;
        let stem = path.file_stem().unwrap_or_default().to_string_lossy();
        let copy = shadow.join(format!("{stem}.{}.{}.dll", std::process::id(), self.copies));
        // kiln: never overwrite a pre-existing shadow file (including a link planted at its predictable name).
        #[cfg(feature = "kiln")]
        {
            let mut src = std::fs::File::open(path).map_err(|e| format!("cannot read: {e}"))?;
            let mut dst = std::fs::OpenOptions::new().write(true).create_new(true).open(&copy).map_err(|e| format!("cannot create shadow: {e}"))?;
            std::io::copy(&mut src, &mut dst).map_err(|e| format!("cannot copy: {e}"))?;
        }
        #[cfg(not(feature = "kiln"))]
        std::fs::copy(path, &copy).map_err(|e| format!("cannot copy: {e}"))?;
        #[cfg(feature = "kiln")]
        let _copy = crate::kiln::CheckedPath::open(&shadow, &copy, true).ok_or("unsafe shadow path")?;
        #[cfg(feature = "kiln")]
        { guarded(|| unsafe { load(&copy) })? }
        #[cfg(not(feature = "kiln"))]
        unsafe { load(&copy) }
    }

    fn dlls(&self) -> Vec<PathBuf> {
        // kiln: discovery and the initial no-mods decision must apply the same path policy as hot reload.
        #[cfg(feature = "kiln")]
        { return crate::kiln::mod_dlls(&self.roots); }
        #[cfg(not(feature = "kiln"))]
        {
        // kiln: every root in order; a root is a folder (its DLLs, sorted, as upstream) or one DLL
        let is_dll = |p: &Path| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll"));
        let mut all = Vec::new();
        for root in &self.roots {
            if root.is_file() {
                if is_dll(root) && !all.contains(root) {
                    all.push(root.clone());
                }
                continue;
            }
            let Ok(entries) = std::fs::read_dir(root) else { continue };
            let mut paths: Vec<_> = entries.flatten().map(|e| e.path()).filter(|p| p.is_file() && is_dll(p)).collect();
            paths.sort();
            for p in paths {
                if !all.contains(&p) {
                    all.push(p);
                }
            }
        }
        all
        }
    }
}

/// Loads every mod DLL of `dir` (missing directory: nothing to do).
#[allow(dead_code)] // kiln: the Kiln build calls load_from
pub fn load_all(dir: &Path) {
    load_from(vec![dir.to_path_buf()], dir.join(SHADOW_DIR));
}

/// kiln: what happened to each mod DLL (path, mod id or the reason it was skipped), for kiln/state/shroudlink.json.
pub static OUTCOMES: Mutex<Vec<(PathBuf, Result<String, String>)>> = Mutex::new(Vec::new());

fn outcome(path: &Path, result: Result<String, String>) {
    let mut all = OUTCOMES.lock().unwrap_or_else(|e| e.into_inner());
    all.retain(|(p, _)| p != path);
    all.push((path.to_path_buf(), result));
}

/// kiln: `load_all` over several roots (folders or single DLLs), with the shadow copies in `shadow`.
pub fn load_from(roots: Vec<PathBuf>, shadow: PathBuf) {
    // kiln: do not clean old copies through a linked shadow directory.
    #[cfg(feature = "kiln")]
    let _shadow = {
        if std::fs::create_dir_all(&shadow).is_err() { return; }
        let Some(guard) = crate::kiln::CheckedPath::open(&shadow, &shadow, false) else { return };
        guard
    };
    // shadow copies of earlier runs are no longer loaded by anyone
    if let Ok(old) = std::fs::read_dir(&shadow) {
        for entry in old.flatten() {
            let _ = std::fs::remove_file(entry.path());
        }
    }
    let mut loader = Loader { roots, shadow, sources: Vec::new(), copies: 0, checked: Instant::now() };
    for path in loader.dlls() {
        let stamp = modified(&path);
        match loader.load_copy(&path) {
            Ok(plugin) => {
                log!("shroudlink: loaded mod '{}' from {}", plugin.id, path.display());
                outcome(&path, Ok(plugin.id.to_string()));
                mods::register(Box::new(plugin));
            }
            Err(e) => {
                log!("shroudlink: skipped {}: {e}", path.display());
                outcome(&path, Err(e));
            }
        }
        loader.sources.push(Source { path, modified: stamp });
    }
    *LOADER.lock().unwrap_or_else(|e| e.into_inner()) = Some(loader);
}

/// Hot reload: loads mod DLLs that are new or changed since the last check.
pub fn reload_changed(host: &mut Host) {
    let mut guard = LOADER.lock().unwrap_or_else(|e| e.into_inner());
    let Some(loader) = guard.as_mut() else { return };
    if loader.checked.elapsed() < RELOAD_CHECK {
        return;
    }
    loader.checked = Instant::now();
    for path in loader.dlls() {
        let stamp = modified(&path);
        let known = loader.sources.iter().position(|s| s.path == path);
        if known.is_some_and(|i| loader.sources[i].modified == stamp) {
            continue;
        }
        // a file still being written fails to load; it is retried on the next check
        match loader.load_copy(&path) {
            Ok(plugin) => {
                log!("shroudlink: {} mod '{}' from {}", if known.is_some() { "reloaded" } else { "loaded" }, plugin.id, path.display());
                outcome(&path, Ok(plugin.id.to_string()));
                mods::replace(host, Box::new(plugin));
            }
            Err(e) => {
                log!("shroudlink: could not load {}: {e}", path.display());
                continue;
            }
        }
        match known {
            Some(i) => loader.sources[i].modified = stamp,
            None => loader.sources.push(Source { path, modified: stamp }),
        }
    }
}

/// Self-test: rewrites `shroudlink_mods/<id>.dll` so it counts as changed.
pub fn touch_for_test(id: &str) -> Result<(), String> {
    // kiln: the first `<id>.dll` among the roots
    let path = LOADER
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .as_ref()
        .and_then(|l| l.dlls().into_iter().find(|p| p.file_name().is_some_and(|n| n.eq_ignore_ascii_case(format!("{id}.dll").as_str()))))
        .ok_or("no mods loaded")?;
    let bytes = std::fs::read(&path).map_err(|e| format!("{}: {e}", path.display()))?;
    std::thread::sleep(Duration::from_millis(20));
    std::fs::write(&path, bytes).map_err(|e| format!("{}: {e}", path.display()))?;
    Ok(())
}

/// Hot reload without waiting for the next check.
pub fn reload_now(host: &mut Host) {
    if let Some(loader) = LOADER.lock().unwrap_or_else(|e| e.into_inner()).as_mut() {
        loader.checked = Instant::now() - RELOAD_CHECK;
    }
    reload_changed(host);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(feature = "kiln")]
    #[test]
    fn kiln_native_initialization_fault_is_contained() {
        extern "C" { fn kiln_sl_test_fault(); }
        assert!(guarded(|| unsafe { kiln_sl_test_fault() }).unwrap_err().contains("e0000042"));
        assert_eq!(guarded(|| 42).unwrap(), 42);
    }

    // kiln: predictable shadow names must never overwrite an existing file, and a linked shadow root is rejected
    // before either stale-file cleanup or creating a new copy through it.
    #[cfg(feature = "kiln")]
    #[test]
    fn kiln_shadow_copy_never_overwrites_and_refuses_linked_roots() {
        let base = std::env::temp_dir().join(format!("sl-shadow-{}", std::process::id()));
        let shadow = base.join("loaded");
        std::fs::create_dir_all(&shadow).unwrap();
        let source = base.join("m.dll");
        std::fs::write(&source, b"source").unwrap();
        let occupied = shadow.join(format!("m.{}.1.dll", std::process::id()));
        std::fs::write(&occupied, b"preserve").unwrap();
        let mut loader = Loader { roots: vec![base.clone()], shadow: shadow.clone(), sources: vec![], copies: 0, checked: Instant::now() };
        assert!(loader.load_copy(&source).is_err());
        assert_eq!(std::fs::read(&occupied).unwrap(), b"preserve");
        let linked = base.join("linked");
        if std::os::windows::fs::symlink_dir(&shadow, &linked).is_ok() {
            loader.shadow = linked.clone();
            assert!(loader.load_copy(&source).is_err());
            assert_eq!(std::fs::read(&occupied).unwrap(), b"preserve");
            let _ = std::fs::remove_dir(linked);
        }
        let _ = std::fs::remove_dir_all(base);
    }

    /// Loads the example mod DLL built in the same workspace and drives it through the host.
    #[test]
    fn example_plugin_round_trip() {
        let dll = std::env::current_exe().unwrap().parent().unwrap().parent().unwrap().join("hello_mod.dll");
        if !dll.exists() {
            eprintln!("skipped: build hello_mod first ({})", dll.display());
            return;
        }
        let mut plugin = unsafe { load(&dll) }.expect("loads");
        assert_eq!(plugin.id(), "hello");
        assert!(matches!(plugin.decl, Decl::V3(_)), "built against SDK v3");
        assert!(!plugin.game_events().is_empty(), "hello subscribes to game events");
        let mut host = Host::default();
        let nonce = 0x42;
        host.connect_for_test(4, nonce);
        plugin.on_join(&mut host, ClientId(nonce));
        let click = json!({ "mod": "hello", "op": "ui_event", "event": { "window": "hello", "node": "click", "event": { "kind": "click" } } });
        plugin.on_message(&mut host, ClientId(nonce), &click);
        plugin.on_message(&mut host, ClientId(nonce), &click);
        let sent: Vec<Value> = host.received_for_test(4, nonce).into_iter().filter(|m| m["owner"] == "hello").collect();
        assert_eq!(sent[0]["op"], "entries");
        let last = sent.last().unwrap();
        assert_eq!(last["op"], "open");
        assert!(last["window"]["content"].to_string().contains("clicked 2 time(s)"));
        assert!(sent.iter().any(|m| m["op"] == "section" && m["id"] == "wave"), "hello adds its backpack section");
        let wave = json!({ "mod": "hello", "op": "ui_event", "event": { "window": "wave", "node": "wave", "event": { "kind": "click" } } });
        plugin.on_message(&mut host, ClientId(nonce), &wave);
        let note = host.received_for_test(4, nonce);
        assert_eq!(note[0]["op"], "notify");
        // outside an inventory event the game API answers nothing
        let event = InventoryEvent { kind: shroudlink_sdk::TransferKind::MoveAll, from: 1, to: 2 };
        assert!(plugin.keep(std::ptr::null_mut(), &event).is_empty());
        plugin.on_leave(&mut host, ClientId(nonce));
    }
}
