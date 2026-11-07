// SPDX-License-Identifier: GPL-3.0-or-later
//! One EML run, Kiln's way: EML's own runner (`mod_loader::lua::run`, patch + optional export) over an explicit mod
//! list, failing soft. A mod whose Lua fails is named (EML loads each module as chunk `mods.<id>.<path>`), switched
//! off and the run repeats without it; when the failure names no mod, the game data goes back to vanilla
//! (`<file>.kfc.bak`, EML's backup) so the game never starts on a half-applied state.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::time::Instant;

use camino::{Utf8Path, Utf8PathBuf};
use mod_loader::lua::{self, RunArgs, RunOptions};
use mod_loader::ModEnvironment;
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

/// The mod id a failure names: EML's chunk names are `mods.<id>.<module>`.
pub fn failing_mod<'a>(error: &str, ids: impl IntoIterator<Item = &'a str>) -> Option<String> {
    let mut best: Option<&str> = None;
    for id in ids {
        let needle = format!("mods.{id}");
        let hit = error.match_indices(&needle).any(|(i, _)| {
            let next = error[i + needle.len()..].chars().next();
            matches!(next, None | Some('.') | Some('"') | Some('\'') | Some(']') | Some(':') | Some(' '))
        });
        // the longest id wins ("mods.foo_bar" also contains "mods.foo")
        if hit && best.is_none_or(|b| id.len() > b.len()) {
            best = Some(id);
        }
    }
    best.map(str::to_owned)
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
    fn server_host_selects_server_data_when_both_games_are_present() {
        let dir = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-side-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        for name in ["enshrouded", "enshrouded_server"] { std::fs::write(dir.join(format!("{name}.kfc")), "fixture").unwrap(); }
        let config = RunConfig { game_dir: dir.clone(), file_name: Some("enshrouded_server".into()), ..Default::default() };
        assert_eq!(run(&config, Vec::new(), Vec::new()).file.as_deref(), Some("enshrouded_server"));
        let _ = std::fs::remove_dir_all(dir);
    }

}
