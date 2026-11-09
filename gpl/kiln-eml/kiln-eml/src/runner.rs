// SPDX-License-Identifier: GPL-3.0-or-later
//! One EML run, Kiln's way: EML's own runner (`mod_loader::lua::run`, patch + optional export) over an explicit mod
//! list, failing soft. Before the long game-data phase, each mod's entry module (`src/mod.lua`) is opened through the
//! mod's own EML file system (`preflight`); a mod that cannot be read is switched off with the stage that failed.
//! A mod whose Lua fails is named (EML loads each module as chunk `mods.<id>.<path>`, and a module that cannot be
//! read names `mods.<id>.<module>`), switched off and the run repeats without it; when the failure names no mod, the
//! game data goes back to vanilla (`<file>.kfc.bak`, EML's backup) so the game never starts on a half-applied state.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::time::Instant;

use camino::{Utf8Path, Utf8PathBuf};
use mod_loader::lua::{self, RunArgs, RunOptions};
use mod_loader::{Capability, ModEnvironment};
use serde::Serialize;

use crate::discover::Entry;

#[derive(Clone, Debug, Default)]
pub struct RunConfig {
    pub game_dir: Utf8PathBuf,
    /// The actual host executable stem; offline callers may leave this unset for discovery.
    pub file_name: Option<String>,
    /// kiln-eml's own cache (types.json, files.json): `<game>/kiln/eml/cache`, not EML's `<game>/.cache`.
    pub cache_dir: Utf8PathBuf,
    pub patch: bool,
    pub export: bool,
    pub export_dir: Option<PathBuf>,
    pub skip_cache: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Done,
    Failed,
    Aside,
    Idle,
}

impl Phase {
    /// KILN_EML_* (kiln_eml.h)
    pub fn code(self) -> u32 {
        match self {
            Phase::Done => 1,
            Phase::Failed => 2,
            Phase::Aside => 3,
            Phase::Idle => 4,
        }
    }
}

#[derive(Clone, Debug, Serialize)]
pub struct Outcome {
    /// Internal gate result: a failed lock must never hand control to a concurrent reader of the live files.
    #[serde(skip)]
    pub start_blocked: bool,
    pub format: u32,
    pub pid: u32,
    #[serde(rename = "kilnEml")]
    pub kiln_eml: &'static str,
    pub eml: &'static str,
    pub phase: Phase,
    /// The game's data file stem (`enshrouded` / `enshrouded_server`), when found.
    pub file: Option<String>,
    pub patched: usize,
    pub cached: bool,
    pub restored: bool,
    pub ms: f64,
    pub error: Option<String>,
    pub aside: Option<String>,
    pub mods: Vec<Entry>,
}

impl Outcome {
    pub fn new(phase: Phase, mods: Vec<Entry>) -> Self {
        Self {
            start_blocked: false,
            format: 1,
            pid: std::process::id(),
            kiln_eml: crate::VERSION,
            eml: crate::EML_VERSION,
            phase,
            file: None,
            patched: 0,
            cached: false,
            restored: false,
            ms: 0.0,
            error: None,
            aside: None,
            mods,
        }
    }

    pub fn loaded(&self) -> usize {
        self.mods.iter().filter(|m| m.state == "loaded").count()
    }
}

/// `enshrouded` / `enshrouded_server` (EML's rule: the `.kfc` or `.exe` the game folder holds).
pub fn game_file_name(game_dir: &Utf8Path) -> Option<String> {
    for n in ["enshrouded", "enshrouded_server"] {
        if game_dir.join(format!("{n}.kfc")).exists() {
            return Some(n.into());
        }
    }
    for n in ["enshrouded", "enshrouded_server"] {
        if game_dir.join(format!("{n}.exe")).exists() {
            return Some(n.into());
        }
    }
    None
}

/// Is `needle` in `error`, followed by something that ends an id (so `mods.foo` does not match `mods.foo_bar`)?
fn names_id(error: &str, needle: &str) -> bool {
    error.match_indices(needle).any(|(i, _)| {
        let next = error[i + needle.len()..].chars().next();
        matches!(next, None | Some('.') | Some('"') | Some('\'') | Some(']') | Some(':') | Some(' '))
    })
}

/// The mod id a failure names: EML's chunk names are `mods.<id>.<module>`. A module that cannot be read says so
/// (`failed to load module: mods.<id>.<module> (...)`, kiln-eml: EML's error carries the qualified module); that mod
/// is the one at fault even when another mod's chunk is on the Lua stack (it required the module).
pub fn failing_mod<'a>(error: &str, ids: impl IntoIterator<Item = &'a str>) -> Option<String> {
    let ids: Vec<&str> = ids.into_iter().collect();
    // the longest id wins ("mods.foo_bar" also contains "mods.foo")
    let longest = |hit: &dyn Fn(&str) -> bool| ids.iter().copied().filter(|id| hit(id)).max_by_key(|id| id.len());
    longest(&|id| names_id(error, &format!("failed to load module: mods.{id}")))
        .or_else(|| longest(&|id| names_id(error, &format!("mods.{id}"))))
        .map(str::to_owned)
}

/// The module EML runs first for every mod (`mods.<id>` is `src/mod.lua`).
pub const ENTRY_MODULE: &str = "src/mod.lua";

/// Reads the entry module of every mod EML will run (`patch`/`export` capabilities of the run) through that mod's
/// own EML file system: the same root and the same path checks `require` uses later, after the long game-data phase.
/// Returns `(mod id, reason)` for each mod that cannot be read, sorted by id. The reason names the stage that failed
/// (root metadata, directory metadata, file metadata, canonicalize, containment check, open, read) with the raw OS
/// error and which parts of the path exist, e.g. `entry src/mod.lua: directory metadata (src) failed: Path not found
/// (os error 3); root exists, src missing`.
pub fn preflight(env: &ModEnvironment, patch: bool, export: bool) -> Vec<(String, String)> {
    let mut failed: Vec<(String, String)> = Vec::new();
    for (id, m) in env.mod_registry().iter() {
        let caps = &m.info().capabilities;
        let runs = patch && caps.contains(&Capability::Patch) || export && caps.contains(&Capability::Export);
        if !runs {
            continue;
        }
        if let Err(why) = m.fs().probe_read(ENTRY_MODULE) {
            failed.push((id.clone(), format!("entry {ENTRY_MODULE}: {why}")));
        }
    }
    failed.sort();
    failed
}

/// Runs `preflight`: each mod whose entry module cannot be read is marked `failed` in `entries` (with the stage
/// reason) and taken out of `mods`. True when at least one mod was taken out.
fn drop_unreadable(env: &ModEnvironment, cfg: &RunConfig, entries: &mut [Entry], mods: &mut Vec<Utf8PathBuf>) -> bool {
    let before = mods.len();
    for (id, reason) in preflight(env, cfg.patch, cfg.export) {
        let path = entries.iter()
            .find(|m| m.state == "pending" && m.id.as_deref() == Some(id.as_str()))
            .map(|m| Utf8PathBuf::from(&m.path));
        tracing::error!(mod_id = %id, error = %reason, "EML mod cannot be read; running the others without it");
        if let Some(path) = path {
            set_state(entries, &path, "failed", Some(reason));
            mods.retain(|p| p != &path);
        }
    }
    mods.len() != before
}

fn panic_text(p: Box<dyn std::any::Any + Send>) -> String {
    if let Some(s) = p.downcast_ref::<&str>() {
        (*s).to_owned()
    } else if let Some(s) = p.downcast_ref::<String>() {
        s.clone()
    } else {
        "panic".into()
    }
}

fn set_state(entries: &mut [Entry], path: &Utf8Path, state: &str, reason: Option<String>) {
    for e in entries.iter_mut().filter(|e| e.path == path.as_str() && e.state == "pending") {
        e.state = state.into();
        e.reason = reason.clone();
    }
}

// A DLL keeps the per-install lock until process exit, so another Kiln process cannot rewrite files the game uses.
// The host runner also serializes calls in-process. OS locks are released after a crash; the lock file is never deleted.
static RUN_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
static GAME_LOCKS: std::sync::Mutex<Vec<(Utf8PathBuf, std::fs::File)>> = std::sync::Mutex::new(Vec::new());
#[cfg(windows)]
fn other_game_running(game: &Utf8Path) -> bool {
    // Also refuse an already-running non-Kiln game in this install (it does not honor our cooperative file lock).
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn K32EnumProcesses(pids: *mut u32, size: u32, needed: *mut u32) -> i32;
        fn OpenProcess(access: u32, inherit: i32, pid: u32) -> *mut std::ffi::c_void;
        fn QueryFullProcessImageNameW(process: *mut std::ffi::c_void, flags: u32, name: *mut u16, len: *mut u32) -> i32;
        fn CloseHandle(handle: *mut std::ffi::c_void) -> i32;
    }
    let mut pids = vec![0u32; 65536];
    let mut needed = 0;
    if unsafe { K32EnumProcesses(pids.as_mut_ptr(), (pids.len() * 4) as u32, &mut needed) } == 0 { return true; }
    if needed as usize >= pids.len() * 4 { return true; }
    let targets = [game.join("enshrouded.exe"), game.join("enshrouded_server.exe")];
    for &pid in &pids[..needed as usize / 4] {
        if pid == 0 || pid == std::process::id() { continue; }
        let h = unsafe { OpenProcess(0x1000, 0, pid) };
        if h.is_null() { continue; }
        let mut buf = vec![0u16; 32768];
        let mut len = buf.len() as u32;
        let ok = unsafe { QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len) };
        unsafe { CloseHandle(h); }
        if ok == 0 { continue; }
        let path = Utf8PathBuf::from(String::from_utf16_lossy(&buf[..len as usize]));
        if let Ok(path) = path.canonicalize_utf8() {
            if targets.iter().any(|p| p.as_str().eq_ignore_ascii_case(path.as_str())) { return true; }
        }
    }
    false
}

pub(crate) fn lock_game(game: &Utf8Path) -> std::io::Result<()> {
    let game = game.canonicalize_utf8()?;
    #[cfg(windows)]
    if other_game_running(&game) { return Err(std::io::Error::other("another game process is using this install")); }
    let mut held = GAME_LOCKS.lock().map_err(|_| std::io::Error::other("poisoned game lock"))?;
    if held.iter().any(|(p, _)| p == &game) { return Ok(()); }
    let path = game.join(".kiln-eml.lock");
    if std::fs::symlink_metadata(&path).is_ok_and(|m| !m.is_file() || m.file_type().is_symlink()) {
        return Err(std::io::Error::other("unsafe game lock path"));
    }
    let f = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(path)?;
    f.try_lock().map_err(std::io::Error::other)?;
    held.push((game, f));
    Ok(())
}

fn files_differ(a: &Utf8Path, b: &Utf8Path) -> bool {
    use std::io::Read;
    let compare = || -> std::io::Result<bool> {
        let mut a = std::fs::File::open(a)?;
        let mut b = std::fs::File::open(b)?;
        if a.metadata()?.len() != b.metadata()?.len() { return Ok(true); }
        let mut x = [0u8; 65536]; let mut y = [0u8; 65536];
        loop {
            let n = a.read(&mut x)?;
            b.read_exact(&mut y[..n])?;
            if x[..n] != y[..n] { return Ok(true); }
            if n == 0 { return Ok(false); }
        }
    };
    compare().unwrap_or(true)
}

/// Runs EML over `mods` (the paths `discover::resolve` chose; `entries` are its entries, updated in place).
pub fn run(cfg: &RunConfig, mut mods: Vec<Utf8PathBuf>, entries: Vec<Entry>) -> Outcome {
    let _serial = RUN_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let started = Instant::now();
    let mut out = Outcome::new(Phase::Done, entries);
    let finish = |mut o: Outcome| {
        o.ms = started.elapsed().as_secs_f64() * 1000.0;
        o
    };
    let selected = match cfg.file_name.as_deref() {
        Some(name @ ("enshrouded" | "enshrouded_server")) => Some(name.to_owned()),
        Some(_) => None,
        None => game_file_name(&cfg.game_dir),
    };
    let Some(file) = selected else {
        out.phase = Phase::Failed;
        out.error = Some(format!("no enshrouded(.kfc|.exe) or enshrouded_server(.kfc|.exe) in {}", cfg.game_dir));
        return finish(out);
    };
    out.file = Some(file.clone());
    if let Err(e) = lock_game(&cfg.game_dir) {
        out.start_blocked = true;
        out.phase = Phase::Failed;
        out.error = Some(format!("game data is in use or cannot be locked: {e}"));
        return finish(out);
    }

    let backup = cfg.game_dir.join(format!("{file}.kfc.bak"));
    if mods.is_empty() {
        // no EML mod (left): put the game data back byte for byte from EML's backup, once
        out.phase = Phase::Idle;
        let kfc = cfg.game_dir.join(format!("{file}.kfc"));
        let resources_backup = cfg.game_dir.join(format!("{file}.kfc_resources.bak"));
        let resource_difference = resources_backup.exists() && files_differ(&cfg.game_dir.join(format!("{file}.kfc_resources")), &resources_backup);
        if backup.exists() && cfg.patch && (files_differ(&kfc, &backup) || resource_difference) {
            out.restored = lua::restore(&cfg.game_dir, &file);
            if !out.restored { out.phase = Phase::Failed; out.error = Some("vanilla restore failed".into()); }
            let _ = std::fs::remove_file(cfg.cache_dir.join("files.json"));
        }
        return finish(out);
    }
    if let Err(e) = std::fs::create_dir_all(&cfg.cache_dir) {
        tracing::warn!(path = %cfg.cache_dir, error = %e, "cannot create the cache directory");
    }

    let total = mods.len();
    for _attempt in 0..=total {
        if mods.is_empty() {
            out.phase = Phase::Idle;
            if cfg.patch && backup.exists() {
                out.restored = lua::restore(&cfg.game_dir, &file);
                let _ = std::fs::remove_file(cfg.cache_dir.join("files.json"));
                if !out.restored { out.phase = Phase::Failed; out.error = Some("vanilla restore failed".into()); }
            }
            break;
        }
        let env = match ModEnvironment::load_from(&cfg.game_dir, &cfg.cache_dir, mods.clone()) {
            Ok(env) => env,
            Err(report) => {
                if let Some(error) = report.error {
                    out.phase = Phase::Failed;
                    out.error = Some(format!("mod environment: {error}"));
                    break;
                }
                for m in &report.mods {
                    let path = Utf8PathBuf::from(m.path.as_str());
                    tracing::error!(path = %path, error = %m.error, "EML mod failed to load; skipped");
                    set_state(&mut out.mods, &path, "failed", Some(m.error.to_string()));
                    mods.retain(|p| p != &path);
                }
                if mods.is_empty() { continue; }
                ModEnvironment::from_parts(
                    cfg.game_dir.clone(),
                    cfg.cache_dir.clone(),
                    mods.clone(),
                    report.mod_registry,
                )
            }
        };
        // Open every mod's entry module now: a mod whose files are gone, unreadable or not UTF-8 would otherwise fail
        // only after EML's game-data phase (minutes on a big install), and take the whole run with it.
        if drop_unreadable(&env, cfg, &mut out.mods, &mut mods) {
            continue; // the environment is built again from the mods left (cheap: it only reads their mod.json)
        }
        let args = RunArgs {
            file_name: file.clone(),
            options: RunOptions {
                skip_cache: cfg.skip_cache,
                is_server: None,
                patch: cfg.patch,
                export: cfg.export,
                runtime: false, // EML 0.2.0's runtime step is a stub (register_dll loads nothing)
                export_dir: cfg.export_dir.clone(),
            },
        };
        let result = catch_unwind(AssertUnwindSafe(|| lua::run(&env, args)))
            .unwrap_or_else(|p| Err(anyhow::anyhow!("panic: {}", panic_text(p))));
        match result {
            Ok(r) => {
                out.patched = r.patched;
                out.cached = r.cached;
                for p in &mods {
                    set_state(&mut out.mods, p, "loaded", None);
                }
                out.phase = Phase::Done;
                out.error = None;
                break;
            }
            Err(e) => {
                let text = format!("{e:#}");
                let ids: Vec<(String, Utf8PathBuf)> = out
                    .mods
                    .iter()
                    .filter(|m| m.state == "pending")
                    .filter_map(|m| Some((m.id.clone()?, Utf8PathBuf::from(&m.path))))
                    .collect();
                match failing_mod(&text, ids.iter().map(|(i, _)| i.as_str())) {
                    Some(id) => {
                        let path = ids.iter().find(|(i, _)| *i == id).map(|(_, p)| p.clone()).unwrap();
                        tracing::error!(mod_id = %id, error = %text, "EML mod failed; running the others without it");
                        set_state(&mut out.mods, &path, "failed", Some(text));
                        mods.retain(|p| p != &path);
                        // run again (no cache was written by the failed run)
                    }
                    None => {
                        out.phase = Phase::Failed;
                        out.error = Some(text);
                        break;
                    }
                }
            }
        }
    }
    if out.phase == Phase::Failed {
        for e in out.mods.iter_mut().filter(|e| e.state == "pending") {
            e.state = "failed".into();
            e.reason = Some("the EML run failed (see error)".into());
        }
        // never start on a half-applied state: back to vanilla data, and forget the cache so the next start re-runs
        if backup.exists() && cfg.patch {
            out.restored = lua::restore(&cfg.game_dir, &file);
        }
        let _ = std::fs::remove_file(cfg.cache_dir.join("files.json"));
    }
    finish(out)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_the_failing_mod() {
        let e = r#"runtime error: [string "mods.better_stamina.mod"]:3: attempt to index a nil value"#;
        assert_eq!(failing_mod(e, ["better", "better_stamina", "x"]).as_deref(), Some("better_stamina"));
        assert_eq!(failing_mod(e, ["better"]), None);
        assert_eq!(failing_mod("callback error: mods.a.util:1", ["a", "b"]).as_deref(), Some("a"));
        assert_eq!(failing_mod("Failed to apply resource 123", ["a"]), None);
    }

    #[test]
    fn names_the_mod_of_a_module_that_cannot_be_read() {
        // kiln-eml: EML's module-load error carries the qualified module
        let entry = "failed to load module: mods.foo (src/mod.lua): directory metadata (src) failed: Path not found (os error 3); root exists, src missing; mod loaded 184.0s ago";
        assert_eq!(failing_mod(entry, ["foo", "foo_bar", "bar"]).as_deref(), Some("foo"));
        assert_eq!(failing_mod(&entry.replace("mods.foo", "mods.foo_bar"), ["foo", "foo_bar"]).as_deref(), Some("foo_bar"));
        // a helper module of another mod: the Lua stack names mod a, the unreadable module is b's
        let nested = "runtime error: [string \"mods.a.mod\"]:3: failed to load module: mods.b.util (src/util.lua): open failed: Access denied (os error 5); root exists, src exists, src/util.lua exists\nstack traceback:\n\t[C]: in ?";
        assert_eq!(failing_mod(nested, ["a", "b"]).as_deref(), Some("b"));
        assert_eq!(failing_mod("failed to load module: mods.zed.mod (src/mod.lua): x", ["a", "b"]), None);
    }

    /// A game folder with the given EML mods, each with `mod.json` (patch capability) and `src/mod.lua`.
    fn mods_fixture(tag: &str, ids: &[&str]) -> (Utf8PathBuf, Vec<Utf8PathBuf>) {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-pre-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        let paths = ids.iter().map(|id| {
            let p = d.join("mods").join(id);
            std::fs::create_dir_all(p.join("src")).unwrap();
            std::fs::write(p.join("mod.json"), format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["patch"]}}"#)).unwrap();
            std::fs::write(p.join("src/mod.lua"), "-- fixture").unwrap();
            p
        }).collect();
        (d, paths)
    }

    fn env_of(game: &Utf8Path, paths: &[Utf8PathBuf]) -> ModEnvironment {
        ModEnvironment::load_from(game, game.join("cache"), paths.to_vec()).unwrap_or_else(|r| panic!("registry: {:?}", r.error))
    }

    fn failures(env: &ModEnvironment) -> Vec<(String, String)> {
        preflight(env, true, false)
    }

    #[test]
    fn preflight_passes_readable_mods() {
        let (d, paths) = mods_fixture("ok", &["a", "b"]);
        assert!(failures(&env_of(&d, &paths)).is_empty());
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn preflight_catches_src_removed_after_the_registry_load() {
        // the issue: mod.json is read when the registry loads; src/mod.lua is needed much later
        let (d, paths) = mods_fixture("src", &["a", "b"]);
        let env = env_of(&d, &paths);
        std::fs::remove_dir_all(paths[1].join("src")).unwrap();
        let bad = failures(&env);
        assert_eq!(bad.len(), 1, "{bad:?}");
        assert_eq!(bad[0].0, "b");
        assert!(bad[0].1.starts_with("entry src/mod.lua: directory metadata (src) failed: "), "{}", bad[0].1);
        assert!(bad[0].1.contains("(os error"), "raw OS error: {}", bad[0].1);
        assert!(bad[0].1.ends_with("root exists, src missing"), "{}", bad[0].1);
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn preflight_names_each_stage() {
        let (d, paths) = mods_fixture("stages", &["root", "file", "dir", "utf8", "src"]);
        let env = env_of(&d, &paths);
        std::fs::remove_dir_all(&paths[0]).unwrap(); // the whole Library entry is gone
        std::fs::remove_file(paths[1].join("src/mod.lua")).unwrap();
        std::fs::remove_file(paths[2].join("src/mod.lua")).unwrap();
        std::fs::create_dir(paths[2].join("src/mod.lua")).unwrap();
        std::fs::write(paths[3].join("src/mod.lua"), [0xff, 0xfe, 0x00, 0x41]).unwrap();
        std::fs::remove_dir_all(paths[4].join("src")).unwrap();
        std::fs::write(paths[4].join("src"), "a file where the folder was").unwrap();
        let bad: std::collections::BTreeMap<_, _> = failures(&env).into_iter().collect();
        assert_eq!(bad.len(), 5, "{bad:#?}");
        assert!(bad["root"].starts_with("entry src/mod.lua: root metadata failed: "), "{}", bad["root"]);
        assert!(bad["root"].ends_with("root missing"), "{}", bad["root"]);
        assert!(bad["file"].starts_with("entry src/mod.lua: file metadata failed: "), "{}", bad["file"]);
        assert!(bad["file"].ends_with("root exists, src exists, src/mod.lua missing"), "{}", bad["file"]);
        assert!(bad["dir"].contains("file metadata failed: not a regular file; root exists, src exists, src/mod.lua is not a file"), "{}", bad["dir"]);
        assert!(bad["utf8"].contains("read failed: "), "{}", bad["utf8"]);
        assert!(bad["utf8"].ends_with("root exists, src exists, src/mod.lua exists"), "{}", bad["utf8"]);
        assert!(bad["src"].contains("directory metadata (src) failed: not a directory"), "{}", bad["src"]);
        assert!(bad["src"].ends_with("root exists, src is not a directory"), "{}", bad["src"]);
        let _ = std::fs::remove_dir_all(d);
    }

    #[cfg(unix)]
    #[test]
    fn preflight_names_the_open_stage() {
        use std::os::unix::fs::PermissionsExt;
        let (d, paths) = mods_fixture("open", &["locked"]);
        let env = env_of(&d, &paths);
        let file = paths[0].join("src/mod.lua");
        std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o000)).unwrap();
        let can_still_open = std::fs::File::open(&file).is_ok(); // root ignores the mode bits
        let bad = failures(&env);
        let _ = std::fs::set_permissions(&file, std::fs::Permissions::from_mode(0o644));
        if !can_still_open {
            assert_eq!(bad.len(), 1);
            assert!(bad[0].1.starts_with("entry src/mod.lua: open failed: "), "{}", bad[0].1);
            assert!(bad[0].1.contains("(os error"), "{}", bad[0].1);
        }
        let _ = std::fs::remove_dir_all(d);
    }

    #[cfg(unix)]
    #[test]
    fn preflight_names_the_containment_check() {
        // a link that leaves the mod folder passes the metadata stages (they follow links) and fails EML's own check
        let (d, paths) = mods_fixture("link", &["linked"]);
        let env = env_of(&d, &paths);
        let outside = d.join("outside.lua");
        std::fs::write(&outside, "-- not the mod's").unwrap();
        std::fs::remove_file(paths[0].join("src/mod.lua")).unwrap();
        std::os::unix::fs::symlink(&outside, paths[0].join("src/mod.lua")).unwrap();
        let bad = failures(&env);
        assert_eq!(bad.len(), 1, "{bad:?}");
        assert!(bad[0].1.starts_with("entry src/mod.lua: containment check failed: "), "{}", bad[0].1);
        assert!(bad[0].1.contains("is outside"), "{}", bad[0].1);
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn preflight_skips_mods_that_do_not_run() {
        let (d, paths) = mods_fixture("skip", &["lib", "ex"]);
        std::fs::write(paths[0].join("mod.json"), r#"{"id":"lib","name":"lib","version":"1.0.0"}"#).unwrap();
        std::fs::write(paths[1].join("mod.json"), r#"{"id":"ex","name":"ex","version":"1.0.0","capabilities":["export"]}"#).unwrap();
        let env = env_of(&d, &paths);
        std::fs::remove_dir_all(paths[0].join("src")).unwrap();
        std::fs::remove_dir_all(paths[1].join("src")).unwrap();
        // a library mod (no capability) is only required by others; an export mod runs only when export is on
        assert!(preflight(&env, true, false).is_empty());
        let with_export = preflight(&env, true, true);
        assert_eq!(with_export.iter().map(|(id, _)| id.as_str()).collect::<Vec<_>>(), ["ex"]);
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn preflight_reads_zip_mods() {
        let (d, _) = mods_fixture("zip", &[]);
        std::fs::create_dir_all(&d).unwrap();
        let write = |name: &str, with_entry: bool| {
            let path = d.join(name);
            let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
            let opt = zip::write::SimpleFileOptions::default();
            z.start_file("mod.json", opt).unwrap();
            let id = name.trim_end_matches(".zip");
            std::io::Write::write_all(&mut z, format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["patch"]}}"#).as_bytes()).unwrap();
            if with_entry {
                z.start_file("src/mod.lua", opt).unwrap();
                std::io::Write::write_all(&mut z, b"-- fixture").unwrap();
            }
            z.finish().unwrap();
            path
        };
        let paths = vec![write("good.zip", true), write("empty.zip", false)];
        let bad = failures(&env_of(&d, &paths));
        assert_eq!(bad.len(), 1, "{bad:?}");
        assert_eq!(bad[0].0, "empty");
        assert!(bad[0].1.starts_with("entry src/mod.lua: zip entry failed: "), "{}", bad[0].1);
        assert!(bad[0].1.ends_with("zip exists, src/mod.lua missing"), "{}", bad[0].1);
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn an_unreadable_mod_is_dropped_and_the_others_stay() {
        let (d, paths) = mods_fixture("drop", &["good", "broken", "also-good"]);
        let candidates: Vec<crate::discover::Candidate> = paths.iter()
            .map(|p| crate::discover::Candidate { path: p.clone(), source: "test".into(), enabled: true }).collect();
        let (mut run, mut entries) = crate::discover::resolve(&candidates);
        assert_eq!(run.len(), 3, "discovery sees three valid mods");
        let env = env_of(&d, &run);
        std::fs::remove_dir_all(paths[1].join("src")).unwrap(); // gone between discovery and the run
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        assert!(drop_unreadable(&env, &cfg, &mut entries, &mut run));
        assert_eq!(run, vec![paths[0].clone(), paths[2].clone()]);
        let state = |id: &str| entries.iter().find(|e| e.id.as_deref() == Some(id)).unwrap();
        assert_eq!(state("broken").state, "failed");
        assert!(state("broken").reason.as_deref().unwrap().contains("directory metadata (src) failed"));
        assert_eq!((state("good").state.as_str(), state("also-good").state.as_str()), ("pending", "pending"));
        // the environment of the mods left is clean: nothing more to drop
        assert!(!drop_unreadable(&env_of(&d, &run), &cfg, &mut entries, &mut run));
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn idle_without_mods_and_backup() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-idle-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        std::fs::write(d.join("enshrouded_server.kfc"), b"KFC").unwrap();
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        let o = run(&cfg, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Idle);
        assert_eq!(o.file.as_deref(), Some("enshrouded_server"));
        assert!(!d.join("cache").exists());
    }

    #[test]
    fn restores_vanilla_when_no_mods_are_left() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-restore-{}", std::process::id()));
        std::fs::create_dir_all(d.join("cache")).unwrap();
        std::fs::write(d.join("enshrouded.kfc"), b"KFC patched").unwrap();
        std::fs::write(d.join("enshrouded.kfc.bak"), b"KFC vanilla").unwrap();
        std::fs::write(d.join("cache/files.json"), b"{}").unwrap();
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        let o = run(&cfg, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Failed);
        // EML's restore checks the version tag of both files; fake files have none, so it refuses: no damage done
        assert!(!o.restored);
        assert_eq!(std::fs::read(d.join("enshrouded.kfc")).unwrap(), b"KFC patched");
    }

    #[test]
    fn fails_without_game_files() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-nogame-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        let o = run(&RunConfig { game_dir: d.clone(), cache_dir: d.join("c"), ..Default::default() }, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Failed);
    }
    #[test]
    fn holds_install_lock_against_a_second_process() {
        if let Ok(path) = std::env::var("KILN_EML_LOCK_TEST_CHILD") {
            assert!(lock_game(Utf8Path::new(&path)).is_err());
            return;
        }
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-lock-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        lock_game(&d).unwrap();
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "runner::tests::holds_install_lock_against_a_second_process"])
            .env("KILN_EML_LOCK_TEST_CHILD", d.as_str()).status().unwrap();
        assert!(status.success());
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn an_exited_process_leaves_no_stale_install_lock() {
        const CHILD: &str = "KILN_EML_EXIT_LOCK_TEST";
        if let Ok(path) = std::env::var(CHILD) {
            lock_game(Utf8Path::new(&path)).unwrap();
            std::process::exit(0); // no Rust destructors: the kernel, not lock-file deletion, must release it
        }
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-exit-lock-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "runner::tests::an_exited_process_leaves_no_stale_install_lock"])
            .env(CHILD, d.as_str()).status().unwrap();
        assert!(status.success());
        assert!(d.join(".kiln-eml.lock").exists());
        lock_game(&d).unwrap();
        let _ = std::fs::remove_dir_all(d);
    }

    #[test]
    fn server_host_selects_server_data_when_both_games_are_present() {
        let dir = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-side-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        for name in ["enshrouded", "enshrouded_server"] { std::fs::write(dir.join(format!("{name}.kfc")), "fixture").unwrap(); }
        let config = RunConfig { game_dir: dir.clone(), file_name: Some("enshrouded_server".into()), ..Default::default() };
        assert_eq!(run(&config, Vec::new(), Vec::new()).file.as_deref(), Some("enshrouded_server"));
        let _ = std::fs::remove_dir_all(dir);
    }

}
