// SPDX-License-Identifier: GPL-3.0-or-later
//! One EML run, Kiln's way: EML's own runner (`mod_loader::lua::run`, patch + optional export) over an explicit mod
//! list, failing soft. Before the long game-data phase, each mod's entry module (`src/mod.lua`) is opened through the
//! mod's own EML file system (`preflight`); a mod that cannot be read is switched off with the stage that failed.
//! A mod whose Lua fails is named (EML loads each module as chunk `mods.<id>.<path>`, and a module that cannot be
//! read names `mods.<id>.<module>`), switched off and the run repeats without it; when the failure names no mod, the
//! game data goes back to vanilla (`<file>.kfc.bak`, EML's backup) so the game never starts on a half-applied state.

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Instant;

use camino::{Utf8Path, Utf8PathBuf};
use mod_loader::lua::{self, FingerprintStats, Progress, RunArgs, RunOptions, Sink, Stage};
use mod_loader::{Capability, ModEnvironment};
use serde::Serialize;

use crate::discover::Entry;
use crate::kiln_mode::KilnMode;
use crate::progress::{Display, Live};

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
    /// Where the run reports what it is doing (the live state file and the progress window), when the host has one.
    pub progress: Option<Arc<Display>>,
    /// Kiln's interpreter has this start (kiln.launch backend `kiln`): its launch plan decides the packages
    /// that need ShroudForge's runtime (kiln_mode.rs). None: the rules from before kiln.launch.
    pub kiln: Option<Arc<KilnMode>>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Done,
    Failed,
    Aside,
    Idle,
    /// The state file while the run is going on (kiln_eml.h KILN_EML_RUNNING): never the result.
    Running,
}

impl Phase {
    /// KILN_EML_* (kiln_eml.h)
    pub fn code(self) -> u32 {
        match self {
            Phase::Done => 1,
            Phase::Failed => 2,
            Phase::Aside => 3,
            Phase::Idle => 4,
            Phase::Running => 5,
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
    // kiln-eml: what fingerprinting the game's files cost the run that finished (bytes hashed, files reused; the cache
    // and input barriers of EML's runner), when one did.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub fingerprint: Option<FingerprintStats>,
    pub error: Option<String>,
    pub aside: Option<String>,
    /// Player guidance: unproven idle output, displaced ShroudForge mods, or extra containers retained for inspection.
    /// The launcher shows this alongside the outcome, including failed or stale-backup runs.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
    /// ShroudForge asset mods (`<id> <version>`) whose prepared output this start's publication replaced (phase `done`):
    /// this start's game data does not have their patches. `reason` says so in a sentence.
    #[serde(rename = "sfDisplaced", skip_serializing_if = "Vec::is_empty")]
    pub sf_displaced: Vec<String>,
    /// The live generation of an EML start that ended well (phase `done`): the SHA-256 of the ownership receipt of the
    /// output it published, or that an unchanged start found live. The EML dialect's change journal
    /// (`kiln/eml/ownership/<stem>.change-journal.json`, format 2) names it; a reader uses that journal only when this is
    /// its `generation`. Absent: no output of this start is proven live (and in kiln mode, whose generation is the startup
    /// outcome's `archiveSha256`).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub generation: Option<String>,
    /// An idle start found the vanilla backup of another game build than the game data (the game was updated since):
    /// it was not compared with it and nothing was claimed or restored.
    #[serde(rename = "staleBackup", skip_serializing_if = "std::ops::Not::not")]
    pub stale_backup: bool,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub recovery: Option<serde_json::Value>,
    /// kiln-eml: EML loaders (`dbghelp.dll`, `dinput8.dll` and renamed builds) found in this process when it stood aside
    /// (phase `aside`). More than one: each patches the game files; `reason` says to keep one.
    #[serde(skip_serializing_if = "Vec::is_empty")]
    pub loaders: Vec<String>,
    pub mods: Vec<Entry>,
    // kiln-eml: only in the state file of a run that is going on (`phase: running`): what it is doing (progress.rs).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub progress: Option<Live>,
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
            fingerprint: None,
            error: None,
            aside: None,
            reason: None,
            sf_displaced: Vec::new(),
            generation: None,
            loaders: Vec::new(),
            stale_backup: false,
            recovery: None,
            mods,
            progress: None,
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

/// kiln-eml's EML error for a module whose mod's files cannot be read (root or `src` gone, open or read failed).
const UNREADABLE: &str = "failed to load module: ";
/// kiln-eml's EML error for a module its (intact) mod does not have: a `require` with a wrong path.
const MISSING: &str = "no such module: ";

/// `error` with the module named after each `no such module: ` left out, so the mod of a module that does not exist
/// does not count as named.
fn without_missing_modules(error: &str) -> String {
    let mut out = String::with_capacity(error.len());
    let mut rest = error;
    while let Some(i) = rest.find(MISSING) {
        let after = i + MISSING.len();
        out.push_str(&rest[..after]);
        let end = rest[after..].find(char::is_whitespace).map_or(rest.len(), |n| after + n);
        rest = &rest[end..];
    }
    out.push_str(rest);
    out
}

/// The mod id a failure names: EML's chunk names are `mods.<id>.<module>`. kiln-eml's EML errors name the module a
/// `require` could not load, in one of two ways:
/// - `failed to load module: mods.<id>.<module> (...)`: that mod's files cannot be read. It is at fault even when
///   another mod's chunk is on the Lua stack (that one only required it).
/// - `no such module: mods.<id>.<module> (...)`: the mod is intact and has no such module (a typo, or a version of the
///   mod without it). The chunk that required it is at fault; the named mod only when no other chunk is named.
pub fn failing_mod<'a>(error: &str, ids: impl IntoIterator<Item = &'a str>) -> Option<String> {
    let ids: Vec<&str> = ids.into_iter().collect();
    // the longest id wins ("mods.foo_bar" also contains "mods.foo"); the first one listed on a tie
    let longest = |text: &str, prefix: &str| {
        ids.iter().copied()
            .filter(|id| names_id(text, &format!("{prefix}mods.{id}")))
            .fold(None, |best: Option<&str>, id| if best.is_none_or(|b| id.len() > b.len()) { Some(id) } else { best })
    };
    longest(error, UNREADABLE)
        .or_else(|| longest(&without_missing_modules(error), ""))
        .or_else(|| longest(error, MISSING))
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
pub(crate) static RUN_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
/// The install locks this process holds, for its life. In this crate's unit tests each keeps its test-gate hold too
/// (mod-loader-lua's test_gate.rs), dropped after the file. The file is shared (`held_lock`): a kiln-mode start's
/// witness, pass and publication borrow the one lock taken in kiln-eml's early phase.
#[cfg(not(test))]
type GameLock = Arc<std::fs::File>;
#[cfg(test)]
type GameLock = (Arc<std::fs::File>, mod_loader_lua::test_gate::KeptHold);
// (each held install with its identity, so another spelling of it is the same install;
// public: `Vec<(Utf8PathBuf, GameLock)>` compared by exact path)
static GAME_LOCKS: std::sync::Mutex<Vec<(Utf8PathBuf, Option<crate::same_path::Identity>, GameLock)>> = std::sync::Mutex::new(Vec::new());
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
    // (the game by any of its names; public: the canonical image name compared with
    // eq_ignore_ascii_case). Under Wine an image name and its canonical path keep the spelling the game was started by
    // (another case, an 8.3 name, Z: for C:), and on Windows a game started from a local share is named
    // \\?\UNC\localhost\C$\...: each one counts by name (case-folded as NTFS folds) or by identity. An executable that
    // cannot be identified is compared by name only, as before.
    let ids: Vec<_> = targets.iter().filter_map(|t| crate::same_path::identity(t.as_std_path()).ok().flatten()).collect();
    for &pid in &pids[..needed as usize / 4] {
        if pid == 0 || pid == std::process::id() { continue; }
        let h = unsafe { OpenProcess(0x1000, 0, pid) };
        if h.is_null() { continue; }
        let mut buf = vec![0u16; 32768];
        let mut len = buf.len() as u32;
        let ok = unsafe { QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut len) };
        unsafe { CloseHandle(h); }
        if ok == 0 { continue; }
        let image = PathBuf::from(<std::ffi::OsString as std::os::windows::ffi::OsStringExt>::from_wide(&buf[..len as usize]));
        let named = std::fs::canonicalize(&image).unwrap_or_else(|_| image.clone());
        if targets.iter().any(|t| crate::same_path::names_equal(t.as_std_path(), &named)) { return true; }
        if crate::same_path::identity(&image).ok().flatten().is_some_and(|id| ids.contains(&id)) { return true; }
    }
    false
}

pub(crate) fn lock_game(game: &Utf8Path) -> std::io::Result<()> {
    let game = game.canonicalize_utf8()?;
    // (the install by name or identity; public: exact canonical path)
    let id = crate::same_path::identity(game.as_std_path())?;
    #[cfg(windows)]
    if other_game_running(&game) { return Err(std::io::Error::other("another game process is using this install")); }
    // In this crate's unit tests the lock is taken under the test gate (mod-loader-lua's test_gate.rs), before the
    // GAME_LOCKS mutex: a first hold may wait for a process being created, never while it holds that mutex.
    #[cfg(test)]
    let hold = mod_loader_lua::test_gate::lock_hold();
    let mut held = GAME_LOCKS.lock().map_err(|_| std::io::Error::other("poisoned game lock"))?;
    // (the exact name or the identity, never the coarser fold; public: `p == &game`)
    if held.iter().any(|(p, i, _)| crate::same_path::same(p.as_std_path(), *i, game.as_std_path(), id)) { return Ok(()); }
    let path = game.join(".kiln-eml.lock");
    if std::fs::symlink_metadata(&path).is_ok_and(|m| !m.is_file() || m.file_type().is_symlink()) {
        return Err(std::io::Error::other("unsafe game lock path"));
    }
    let f = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(path)?;
    f.try_lock().map_err(std::io::Error::other)?;
    let f = Arc::new(f);
    #[cfg(test)]
    let f = (f, hold.keep());
    held.push((game, id, f));
    Ok(())
}

/// The install lock of `game` this process holds (`lock_game`), shared: never dropped or taken again while the process
/// lives. Every kiln-mode host borrows it, from the early witness to the startup cache's record.
pub(crate) fn held_lock(game: &Utf8Path) -> Option<Arc<std::fs::File>> {
    let game = game.canonicalize_utf8().ok()?;
    // the install by its exact name or its identity, as `lock_game` knows it held
    let id = crate::same_path::identity(game.as_std_path()).ok().flatten();
    let held = GAME_LOCKS.lock().unwrap_or_else(|e| e.into_inner());
    let found = held.iter().find(|(p, i, _)| crate::same_path::same(p.as_std_path(), *i, game.as_std_path(), id));
    #[cfg(not(test))]
    let found = found.map(|(_, _, f)| f.clone());
    #[cfg(test)]
    let found = found.map(|(_, _, (f, _))| f.clone());
    found
}

/// Unit tests: run() keeps the install lock until the process exits, as the game's DLL must (GAME_LOCKS). A test drops
/// it when it is done: Windows deletes a file that is still open only when its last handle closes (always under Wine),
/// so a removed game folder would still hold .kiln-eml.lock (os error 145), and the test gate (mod-loader-lua's
/// test_gate.rs) creates no process while any install lock is kept.
#[cfg(test)]
pub(crate) fn release_game_lock(game: &Utf8Path) {
    let Ok(game) = game.canonicalize_utf8() else { return };
    // (public: `p != &game`)
    let id = crate::same_path::identity(game.as_std_path()).ok().flatten();
    GAME_LOCKS.lock().unwrap_or_else(|e| e.into_inner()).retain(|(p, i, _)| !crate::same_path::same(p.as_std_path(), *i, game.as_std_path(), id));
}

/// Unit tests: `release_game_lock` when dropped, so a test that fails before its own release still releases the install
/// lock as it unwinds. A kept one would make every later process creation of this test binary wait for it (the test
/// gate), then fail after 300 s.
#[cfg(test)]
pub(crate) struct GameLockRelease(pub(crate) Utf8PathBuf);
#[cfg(test)]
impl Drop for GameLockRelease {
    fn drop(&mut self) { release_game_lock(&self.0); }
}

fn files_differ(a: &Utf8Path, b: &Utf8Path, progress: &Progress) -> bool {
    use std::io::Read;
    let compare = || -> std::io::Result<bool> {
        let mut a = std::fs::File::open(a)?;
        let mut b = std::fs::File::open(b)?;
        if a.metadata()?.len() != b.metadata()?.len() { return Ok(true); }
        let mut x = vec![0u8; 1 << 20]; let mut y = vec![0u8; 1 << 20];
        loop {
            let n = a.read(&mut x)?;
            b.read_exact(&mut y[..n])?;
            progress.add(2 * n as u64);
            if x[..n] != y[..n] { return Ok(true); }
            if n == 0 { return Ok(false); }
        }
    };
    compare().unwrap_or(true)
}

/// Why an EML package that needs ShroudForge's runtime is skipped when no ShroudForge reserved this start.
pub(crate) fn runtime_skip_reason(cfg: &RunConfig) -> String {
    skip_reason(std::env::var("KILN_SHROUDFORGE").ok().as_deref(), || runtime_unavailable(cfg))
}

/// `env`: the KILN_SHROUDFORGE variable (only the exact value 0 switches ShroudForge off). Players set it to 0 thinking it
/// means "default" (inbox 36); a variable set with setx reaches only programs started after it. The launcher's note for
/// the same case (kiln-manage own_loaders::RUNTIME_ENV_OFF_NOTE) says the same.
pub(crate) fn skip_reason(env: Option<&str>, unavailable: impl FnOnce() -> String) -> String {
    if env == Some("0") {
        return "needs ShroudForge's runtime, which the KILN_SHROUDFORGE=0 environment variable switches off; its data was \
                not applied, so the game is as without the mod. Remove that variable (do not set it to 0), restart Steam and \
                the Kiln launcher, and start the game again".into();
    }
    format!("needs ShroudForge's runtime, which is not running: {}; its data was not applied, so the game is as without the mod", unavailable())
}

pub(crate) fn runtime_unavailable(cfg: &RunConfig) -> String {
    let state = cfg.game_dir.join("kiln/state/shroudforge.json");
    if let Ok(bytes) = std::fs::read(state) {
        if let Ok(v) = serde_json::from_slice::<serde_json::Value>(&bytes) {
            if v.get("pid").and_then(|v| v.as_u64()) == Some(std::process::id() as u64) {
                if let Some(why) = v.get("reason").and_then(|v| v.as_str()) {
                    return format!("{why}. Enable/install the ShroudForge component and activate the profile again");
                }
            }
        }
    }
    "ShroudForge did not reserve this start (component missing, off, incompatible, or an older adapter); install/update the ShroudForge component and activate the profile again".into()
}

/// How many threads each block-compressed texture of a run is encoded on (kfc-content `set_encode_threads`; the bytes
/// are the serial encode's). BC7 is nearly all of a texture-heavy run's time: EMF Beastmaster's three packs spent 397 of
/// their 401 s of Lua in it on one core (0.2.0-dev.31.13). `kiln_mode`: kiln.launch backend `kiln`, whose pass can run
/// while a package DLL's initializer holds the Windows loader lock, where a new thread cannot start: EML's serial encode
/// there. `env`: KILN_EML_ENCODE_THREADS (1 = EML's serial encode; a count is used as it is, at most 64). Otherwise the
/// PC's threads (`available`), at most 16.
pub(crate) fn encode_threads(kiln_mode: bool, env: Option<&str>, available: usize) -> usize {
    if kiln_mode {
        return 1;
    }
    match env.and_then(|v| v.trim().parse::<usize>().ok()).filter(|&n| n >= 1) {
        Some(n) => n.min(64),
        None => available.clamp(1, 16),
    }
}

/// The run's encode thread count, put back as it was when the run ends.
pub(crate) struct EncodeThreads(usize);

impl EncodeThreads {
    pub(crate) fn set(threads: usize) -> Self {
        Self(kfc::content::image::set_encode_threads(threads))
    }
}

impl Drop for EncodeThreads {
    fn drop(&mut self) {
        kfc::content::image::set_encode_threads(self.0);
    }
}

/// Runs EML over `mods` (the paths `discover::resolve` chose; `entries` are its entries, updated in place).
pub fn run(cfg: &RunConfig, mut mods: Vec<Utf8PathBuf>, entries: Vec<Entry>) -> Outcome {
    // In this crate's unit tests a run holds the test gate (mod-loader-lua's test_gate.rs) from before RUN_LOCK: its locks
    // (the install lock, ShroudForge's) then never wait for a process being created while it holds RUN_LOCK, which a
    // thread that keeps an install lock may be waiting for.
    #[cfg(test)]
    let _gate = mod_loader_lua::test_gate::lock_hold();
    let _serial = RUN_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let started = Instant::now();
    let mut out = Outcome::new(Phase::Done, entries);
    let finish = |mut o: Outcome| {
        if cfg.patch && !o.start_blocked && let Some(file) = o.file.as_deref() {
            // Recovery is offered only for the receipt-less failed/idle case. Do not add full ownership
            // hashing to successful or already-owned starts just to prepare a launcher offer.
            if matches!(o.phase, Phase::Failed | Phase::Idle)
                && cfg.game_dir.join(format!("kiln/eml/ownership/{file}.json")).try_exists().is_ok_and(|exists| !exists) {
                o.recovery = lua::recovery_inventory(cfg.game_dir.as_std_path(), file).ok();
            }
            match lua::retained_containers(cfg.game_dir.as_std_path(), file, o.phase == Phase::Done) {
                Ok(files) if !files.is_empty() => {
                    let retained = format!("Extra archive containers retained: {}. They may belong to another loader or an earlier EML run; inspect them before manual cleanup. Steam verification may leave extra files", files.join(", "));
                    tracing::warn!("{retained}");
                    o.reason = Some(match o.reason.take() { Some(why) => format!("{why}. {retained}"), None => retained });
                }
                Err(error) => tracing::warn!(%error, "Could not inventory retained archive containers"),
                _ => {}
            }
        }
        if cfg.patch && !o.start_blocked && let Some(file) = o.file.as_deref() {
            match lua::recovery_guidance(cfg.game_dir.as_std_path(), file) {
                Ok(messages) => for message in messages {
                    tracing::warn!("{message}");
                    o.reason = Some(match o.reason.take() { Some(why) => format!("{why}. {message}"), None => message });
                },
                Err(error) => tracing::warn!(%error, "Could not inventory EML recovery files"),
            }
        }
        o.ms = started.elapsed().as_secs_f64() * 1000.0;
        o
    };
    // Kiln's interpreter has this start: its launch plan decides and kiln-eml's startup barrier runs it;
    // kiln/state/shroudforge.json is not read
    if let Some(kiln) = &cfg.kiln {
        let mut out = run_kiln(cfg, kiln, mods, std::mem::take(&mut out.mods));
        out.ms = started.elapsed().as_secs_f64() * 1000.0;
        return out;
    }
    // EML's run, outside kiln mode: in the plugin it runs in kiln-core's early step on the game's main thread, outside
    // the loader lock (as the core's progress window thread does), so block-compressed textures may use the cores
    let available = std::thread::available_parallelism().map_or(1, |n| n.get());
    let _encode = EncodeThreads::set(encode_threads(false, std::env::var("KILN_EML_ENCODE_THREADS").ok().as_deref(), available));
    tracing::debug!(threads = kfc::content::image::encode_threads(), "block-compressed textures: encode threads");
    // Reaching this runner means no ShroudForge writer reserved the run. Never run just the data half.
    {
        let why = runtime_skip_reason(cfg);
        mods.retain(|path| {
            let needs = crate::discover::classify(path).is_ok_and(|m| m.capabilities.iter()
                .any(|c| matches!(c, Capability::Runtime | Capability::RuntimeRegisterDll)));
            if needs {
                let reason = why.clone();
                tracing::warn!(%path, %reason, "EML package skipped whole");
                set_state(&mut out.mods, path, "skipped", Some(reason));
            }
            !needs
        });
    }
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

    // Consume write-ahead scratch records under the install lock; unrecorded game-folder copies stay.
    if cfg.patch { lua::prune(cfg.game_dir.as_std_path(), &file); }

    // what the run does is reported into the live state file and the progress window (progress.rs), and to the log
    let progress = Progress::start(cfg.progress.clone().map(|d| d as Arc<dyn Sink>));
    if let Some(display) = &cfg.progress {
        display.set_base(&out);
    }

    let backup = cfg.game_dir.join(format!("{file}.kfc.bak"));
    if mods.is_empty() {
        idle(cfg, &file, &progress, &mut out);
        return finish(out);
    }
    if let Err(e) = std::fs::create_dir_all(&cfg.cache_dir) {
        tracing::warn!(path = %cfg.cache_dir, error = %e, "cannot create the cache directory");
    }

    // Output an earlier kiln-eml published has no receipt: adopt it now if its change cache proves it (once), so that a
    // publication that fails before its first change, or a later start without EML mods, can still restore it.
    if cfg.patch && backup.exists() {
        match lua::adopt(cfg.game_dir.as_std_path(), &file, cfg.cache_dir.as_std_path()) {
            Ok(lua::Adoption::Unproven(why)) => tracing::debug!(why, "no earlier EML output to adopt"),
            Ok(_) => {}
            Err(error) => tracing::warn!(%error, "could not check for earlier EML output to adopt"),
        }
    }

    let total = mods.len();
    let mut retried = false; // a mod's Lua failed and the run repeats without it: the journal's attempt is a retry
    for _attempt in 0..=total {
        if mods.is_empty() {
            idle(cfg, &file, &progress, &mut out);
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
        if let Some(display) = &cfg.progress {
            display.set_base(&out); // the mods as they stand: one may have been switched off since the last attempt
        }
        let result = catch_unwind(AssertUnwindSafe(|| lua::run_with(&env, args, &progress)))
            .unwrap_or_else(|p| Err(anyhow::anyhow!("panic: {}", panic_text(p))));
        match result {
            Ok(r) => {
                out.patched = r.patched;
                out.cached = r.cached;
                out.fingerprint = Some(r.fingerprint);
                out.generation = r.generation.clone();
                // the EML dialect's change journal of the generation this run published (an unchanged start keeps the one
                // beside the receipt it found live)
                if let (Some(record), Some(generation)) = (&r.journal, &r.generation)
                    && let Err(why) = write_journal(cfg, &file, record, generation, retried, &out.mods)
                {
                    tracing::warn!(%why, "the EML pass's change journal was not saved: Kiln's registry names none of its mods this start");
                }
                if !r.sf_displaced.is_empty() {
                    out.reason = Some(displaced_sentence(&r.sf_displaced));
                    out.sf_displaced = r.sf_displaced;
                }
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
                        retried = true;
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
        let reason = format!("the EML run failed: {}", out.error.as_deref().unwrap_or("unknown cause"));
        out.reason = Some(reason.clone());
        for e in out.mods.iter_mut().filter(|e| e.state == "pending") {
            e.state = "failed".into();
            e.reason = Some(reason.clone());
        }
        // never start on a half-applied state: back to vanilla data, and forget the cache so the next start re-runs
        if backup.exists() && cfg.patch && owned(cfg, &file).is_ok() {
            out.restored = lua::restore(&cfg.game_dir, &file);
        }
        let _ = std::fs::remove_file(cfg.cache_dir.join("files.json"));
    }
    finish(out)
}

/// The EML dialect's change journal (sf_startup/journal.rs, format 2) of the generation a run just published, beside the
/// receipt with SHA-256 `generation`, only while that receipt is still the one (`StartupJournal::write_for`), under the
/// install lock this process holds. `mods`: the run's mods (their manifest versions). Every publication and restore
/// removes the journal first (mod-loader-lua ownership.rs), so one on disk always belongs to the receipt beside it.
fn write_journal(cfg: &RunConfig, file: &str, record: &lua::WriteRecord, generation: &str, retried: bool, mods: &[Entry]) -> Result<(), String> {
    use crate::sf_startup::journal::{Header, Identity, Journal, NotApplied, Tail};
    let digest = crate::startup::unhex(generation).ok_or("the receipt's SHA-256 is not 64 hex digits")?;
    let header = Header { stem: file, generation: digest, receipt_sha256: digest, dialect: "eml", attempt: if retried { "retry" } else { "first" } };
    let identity = |id: &str| {
        Some(Identity {
            version: mods.iter().find(|m| m.id.as_deref() == Some(id)).and_then(|m| m.version.clone()).filter(|v| !v.is_empty() && v.len() <= 64),
            kind: "eml",
            content_sha256: [0; 32],
            settings_sha256: [0; 32],
        })
    };
    // 0.2.0-dev.32: the mods whose Lua failed (the run went on without them) are listed as not applied (the registry's
    // effect replay reads it); a run without one writes the record as before
    let not_applied: Vec<NotApplied> = mods
        .iter()
        .filter(|m| m.state == "failed")
        .filter_map(|m| {
            Some(NotApplied {
                id: m.id.clone()?,
                version: m.version.clone().filter(|v| !v.is_empty() && v.len() <= 64),
                kind: "eml",
                content_sha256: [0; 32],
                reason: m.reason.clone().unwrap_or_else(|| "failed".into()),
            })
        })
        .collect();
    let tail = Tail { writer: None, not_applied: (!not_applied.is_empty()).then_some(&not_applied[..]) };
    let bytes = Journal::from_eml(record).json_with(&header, &identity, &tail);
    let lock = held_lock(&cfg.game_dir).ok_or("the install lock is not held")?;
    let journal = lua::startup_host::StartupJournal::bind(cfg.game_dir.as_std_path(), file, &lock).map_err(|e| format!("{e:#}"))?;
    journal.write_for(generation, bytes.as_bytes()).map_err(|e| e.to_string())
}

/// A kiln-mode start: no EML mod runs on its own. The plan decides each discovered package
/// (`KilnMode::apply`); kiln-eml's startup barrier (`startup_seam::barrier`) waits for kiln-sfi's native terminal, runs
/// the startup controller's startup pass over this start's witness, publishes once and grants; the state file then shows its outcome: one
/// row per package of the pass (`source: "shroudforge"`), the unlisted contributors of a start without a startup
/// dialect as the EML pass left them, and everything else with the plan's reason. A start whose early phase witnessed
/// nothing (the plan could not be read, or no early step ran) runs nothing: every package is skipped with why.
fn run_kiln(cfg: &RunConfig, kiln: &KilnMode, mods: Vec<Utf8PathBuf>, entries: Vec<Entry>) -> Outcome {
    let mut out = Outcome::new(Phase::Idle, entries);
    let unlisted = kiln.apply(mods, &mut out.mods);
    out.file = match cfg.file_name.as_deref() {
        Some(name @ ("enshrouded" | "enshrouded_server")) => Some(name.to_owned()),
        Some(_) => None,
        None => game_file_name(&cfg.game_dir),
    };
    use crate::startup_seam::{self as seam, Barriered};
    // the launcher's start bar and kiln.log follow the pass step by step (progress.rs `PassSteps`): a slow drive makes a
    // slow start, never one that looks hung
    let steps = kiln.plan().ok().map(|plan| crate::progress::PassSteps::begin(cfg.progress.clone(), &out, plan.dialect()));
    let observe = |point: crate::startup::Point<'_>| {
        if let Some(steps) = &steps {
            steps.observe(&point);
        }
    };
    let barrier = match kiln.plan() {
        Ok(_) => seam::barrier(&seam::BarrierPorts {
            cache_dir: &cfg.cache_dir,
            unlisted: &unlisted,
            wait: seam::TERMINAL_WAIT,
            observe: &observe,
        }),
        Err(_) => Barriered::NoStart,
    };
    // what the pass and each step took, for a start whose pass ran
    if let (Some(line), Barriered::Done(_)) = (steps.and_then(crate::progress::PassSteps::finish), &barrier) {
        tracing::info!("{line}");
    }
    let (plan, finished) = match (kiln.plan(), barrier) {
        (Ok(plan), Barriered::Done(finished)) => (plan, finished),
        (plan, barrier) => {
            // nothing witnessed this start's game files, or the barrier stopped: nothing of the plan runs, and no unlisted
            // contributor either
            let why = match (plan, barrier) {
                (Err(why), _) => format!("Kiln could not read this start's launch plan again ({why}), so no startup pass ran \
                    this start; its data was not applied, so the game is as without the mod. Start the game again from the \
                    Kiln launcher"),
                (_, Barriered::Faulted(why)) => {
                    out.phase = Phase::Failed;
                    out.error = Some(why.clone());
                    why
                }
                _ => seam::NO_START.to_owned(),
            };
            for e in out.mods.iter_mut().filter(|e| e.state == "pending") {
                e.state = "skipped".into();
                e.reason = Some(why.clone());
            }
            return out;
        }
    };
    seam::settle_entries(&mut out, &finished, plan, &cfg.game_dir, &unlisted);
    out
}

/// No EML mod (left): put the game data back byte for byte from EML's backup, once, when it is output kiln-eml can
/// prove it wrote; otherwise leave it and say why (`reason`). A backup of another game build than the live data (the
/// game was updated since it was made) is not compared with at all (`staleBackup`): it is of no use for this build, and
/// the next start with an EML mod makes a new one.
fn idle(cfg: &RunConfig, file: &str, progress: &Progress, out: &mut Outcome) {
    out.phase = Phase::Idle;
    let backup = cfg.game_dir.join(format!("{file}.kfc.bak"));
    let kfc = cfg.game_dir.join(format!("{file}.kfc"));
    if !cfg.patch || !backup.exists() { return; }
    let build = |p: &Utf8Path| kfc::container::KFCFile::get_version_tag(p).ok();
    if let (Some(live), Some(saved)) = (build(&kfc), build(&backup)) && live != saved {
        tracing::info!("No EML mods; the vanilla backup is of another game build ({saved}) than the game data ({live}): \
            the game was updated since it was made, so it is not compared and nothing is restored. The next start with an \
            EML mod makes a new backup");
        out.stale_backup = true;
        return;
    }
    let resources_backup = cfg.game_dir.join(format!("{file}.kfc_resources.bak"));
    // comparing the live files with the backup reads both (hundreds of MiB on a real client): say so
    let len = |p: &Utf8Path| std::fs::metadata(p.as_std_path()).map_or(0, |m| m.len());
    let live_resources = cfg.game_dir.join(format!("{file}.kfc_resources"));
    let bytes = 2 * (len(&kfc) + len(&live_resources) * u64::from(resources_backup.exists()));
    tracing::info!("No EML mods: comparing the game data with the vanilla backup ({} read) to see whether it needs restoring", mib(bytes));
    progress.stage(Stage::Checking);
    progress.announce(if resources_backup.exists() { 4 } else { 2 }, bytes);
    progress.begin_file(resources_backup.file_name().unwrap_or("the vanilla backup"));
    let resource_difference = resources_backup.exists() && files_differ(&live_resources, &resources_backup, progress);
    let differ = files_differ(&kfc, &backup, progress) || resource_difference;
    progress.end_file();
    if !differ { return; }
    if let Err(why) = owned(cfg, file) {
        out.reason = Some(unproven_sentence(&why));
        return;
    }
    out.restored = lua::restore(&cfg.game_dir, file);
    if !out.restored { out.phase = Phase::Failed; out.error = Some("vanilla restore failed".into()); }
    let _ = std::fs::remove_file(cfg.cache_dir.join("files.json"));
}

/// Is the live game data output Kiln's EML writer can prove it published? Its receipt decides; with none, the change
/// cache of an earlier kiln-eml may prove it once (`lua::adopt`). Err: why not, in a few words.
fn owned(cfg: &RunConfig, file: &str) -> Result<(), String> {
    let root = cfg.game_dir.as_std_path();
    let why = match lua::owns_output(root, file) {
        Ok(true) => return Ok(()),
        Ok(false) => match lua::adopt(root, file, cfg.cache_dir.as_std_path()) {
            Ok(lua::Adoption::Adopted) => return Ok(()),
            Ok(lua::Adoption::Receipt) => "it is not the output its ownership receipt lists (another loader, a game update or a \
                file check changed it since)".to_owned(),
            Ok(lua::Adoption::Unproven(why)) => format!("it has no ownership receipt, and {why}"),
            Err(error) => format!("its ownership could not be checked: {error}"),
        },
        Err(error) => format!("its ownership receipt cannot be used: {error}"),
    };
    tracing::warn!("Game data is not output Kiln's EML writer can prove it published ({why}); leaving it and the shared .bak files alone");
    Err(why)
}

/// The `reason` of a start whose publication replaced ShroudForge's prepared asset patches. kiln-eml publishes only in a
/// start where ShroudForge does not run: hosted ShroudForge reserves the writer and kiln-eml stands aside
/// (`eml_stand_aside`), and ShroudForge's own proxy makes it stand aside too. So the SF state it replaced is from an
/// earlier start.
fn displaced_sentence(mods: &[String]) -> String {
    format!("ShroudForge's asset mods {}, prepared by an earlier start, are not in this start's game data: ShroudForge \
        did not run this start (its mods are off in the active profile, or it could not start: see its row on Logs > \
        Engines), so kiln-eml built the game data from the vanilla backup with the EML mods only. ShroudForge prepares \
        them again the next time it runs", mods.join(", "))
}

/// The `reason` of an idle start whose game data differs from the backup and is not provably EML's.
fn unproven_sentence(why: &str) -> String {
    format!("The game data differs from its vanilla backup, and Kiln cannot prove it is its own EML output ({why}), so it \
        was left as it is. If ShroudForge asset mods or another loader wrote it, nothing needs doing. If it still carries \
        EML mods you switched off: enable any EML mod for one start and disable it again; Kiln preserves unproven \
        destination containers in kiln/eml/displaced rather than deleting them. Or use Steam's \"Verify integrity of \
        game files\" (which may leave extra containers): verify the game files in Steam, then start through Kiln again")
}

/// `224.4 MiB`
fn mib(bytes: u64) -> String {
    format!("{:.1} MiB", bytes as f64 / (1u64 << 20) as f64)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn block_compression_gets_the_cores_only_outside_kiln_mode() {
        // EML's run (no kiln.launch backend kiln): the PC's threads, at most 16
        assert_eq!(encode_threads(false, None, 16), 16);
        assert_eq!(encode_threads(false, None, 32), 16);
        assert_eq!(encode_threads(false, None, 4), 4);
        assert_eq!(encode_threads(false, None, 0), 1);
        // KILN_EML_ENCODE_THREADS: 1 is EML's serial encode; another count is used as it is (at most 64)
        assert_eq!(encode_threads(false, Some("1"), 16), 1);
        assert_eq!(encode_threads(false, Some(" 3 "), 16), 3);
        assert_eq!(encode_threads(false, Some("200"), 16), 64);
        for junk in ["0", "", "x", "-2", "2.5"] {
            assert_eq!(encode_threads(false, Some(junk), 6), 6, "{junk:?} is not a count: the default");
        }
        // kiln mode: its pass can run while a package DLL holds the Windows loader lock, where a new thread cannot start
        assert_eq!(encode_threads(true, None, 16), 1);
        assert_eq!(encode_threads(true, Some("8"), 16), 1);
    }

    #[test]
    fn a_run_puts_the_encode_setting_back() {
        let _serial = RUN_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        let before = kfc::content::image::encode_threads();
        {
            let _threads = EncodeThreads::set(5);
            assert_eq!(kfc::content::image::encode_threads(), 5);
        }
        assert_eq!(kfc::content::image::encode_threads(), before);
        // a panic in the run (kiln-eml unwinds: Cargo.toml's release profile) puts it back as well
        let unwound = std::panic::catch_unwind(|| {
            let _threads = EncodeThreads::set(7);
            panic!("a panic in EML's run");
        });
        assert!(unwound.is_err());
        assert_eq!(kfc::content::image::encode_threads(), before);
        drop(_serial);
        // a whole run (one that fails at once: no game here) leaves it as it found it
        let dir = std::env::temp_dir().join(format!("kiln-eml-encode-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let cfg = RunConfig { game_dir: Utf8PathBuf::from_path_buf(dir.clone()).unwrap(), ..Default::default() };
        let out = run(&cfg, vec![Utf8PathBuf::from("/nonexistent/mod")], Vec::new());
        assert_eq!(out.phase, Phase::Failed);
        // read under RUN_LOCK: another test's run on another thread holds the PC's count while it runs
        let _serial = RUN_LOCK.lock().unwrap_or_else(|e| e.into_inner());
        assert_eq!(kfc::content::image::encode_threads(), before);
        let _ = std::fs::remove_dir_all(dir);
    }

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

    #[test]
    fn a_module_the_mod_does_not_have_blames_the_mod_that_required_it() {
        // mod a requires mods.b.util, but the installed b has no src/util.lua (a typo, or another version of b)
        let missing = "runtime error: [string \"mods.a.mod\"]:3: no such module: mods.b.util (src/util.lua): file metadata failed: No such file or directory (os error 2); root exists, src exists, src/util.lua missing; mod loaded 3.0s ago\nstack traceback:\n\t[C]: in ?";
        assert_eq!(failing_mod(missing, ["a", "b"]).as_deref(), Some("a"));
        assert_eq!(failing_mod(missing, ["b", "a"]).as_deref(), Some("a"), "whatever order the mods are listed in");
        // b's own chunk requires the module it does not have: b
        assert_eq!(failing_mod(&missing.replace("mods.a.mod", "mods.b.mod"), ["a", "b"]).as_deref(), Some("b"));
        // the module's mod id is left out as a whole: b_extra is not taken for b (or the other way round)
        assert_eq!(failing_mod(&missing.replace("mods.b.util", "mods.b_extra.util"), ["b", "b_extra", "a"]).as_deref(), Some("a"));
        // nothing else on the stack (EML's runner itself required the module): the named mod
        let alone = "no such module: mods.b (src/mod.lua): file metadata failed: x; root exists, src exists, src/mod.lua missing";
        assert_eq!(failing_mod(alone, ["a", "b"]).as_deref(), Some("b"));
        assert_eq!(failing_mod(&format!("{alone}\n{alone}"), ["a", "b"]).as_deref(), Some("b"));
        // an unreadable module still wins over the requiring chunk, wherever it is in the text
        let both = format!("{missing}\ncaused by: failed to load module: mods.c.x (src/x.lua): open failed: y");
        assert_eq!(failing_mod(&both, ["a", "b", "c"]).as_deref(), Some("c"));
        assert_eq!(without_missing_modules("no such module: mods.b.util (src/util.lua) no such module: mods.c"), "no such module:  (src/util.lua) no such module: ");
    }

    #[test]
    fn a_path_the_intact_mod_does_not_have_is_missing_not_unreadable() {
        let (d, paths) = mods_fixture("missing", &["a"]);
        let env = env_of(&d, &paths);
        let m = env.mod_registry().iter().next().unwrap().1.clone();
        let probe = |rel: &str| m.fs().probe_read(rel).unwrap_err();
        // the mod is intact: a file, or a folder below src, that it does not have
        assert!(probe("src/util.lua").missing, "{}", probe("src/util.lua"));
        assert!(probe("src/lib/util.lua").missing, "{}", probe("src/lib/util.lua"));
        // a file that is there but cannot be loaded is the mod's own problem
        std::fs::create_dir(paths[0].join("src/dir.lua")).unwrap();
        assert!(!probe("src/dir.lua").missing);
        // src (or the whole mod) gone: the mod's files cannot be read
        std::fs::remove_dir_all(paths[0].join("src")).unwrap();
        assert!(!probe("src/util.lua").missing, "{}", probe("src/util.lua"));
        std::fs::remove_dir_all(&paths[0]).unwrap();
        assert!(!probe("src/util.lua").missing, "{}", probe("src/util.lua"));
        let _ = std::fs::remove_dir_all(d);

        // a zip: the entry is not in an archive that has src/, or the archive has no src/ at all
        let (d, _) = mods_fixture("missing-zip", &[]);
        std::fs::create_dir_all(&d).unwrap();
        let zip = |name: &str, entries: &[&str]| {
            let path = d.join(name);
            let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
            let opt = zip::write::SimpleFileOptions::default();
            let id = name.trim_end_matches(".zip");
            z.start_file("mod.json", opt).unwrap();
            std::io::Write::write_all(&mut z, format!(r#"{{"id":"{id}","name":"{id}","version":"1.0.0","capabilities":["patch"]}}"#).as_bytes()).unwrap();
            for e in entries {
                z.start_file(*e, opt).unwrap();
                std::io::Write::write_all(&mut z, b"-- fixture").unwrap();
            }
            z.finish().unwrap();
            path
        };
        let env = env_of(&d, &[zip("full.zip", &["src/mod.lua"]), zip("bare.zip", &[])]);
        let probe = |id: &str, rel: &str| env.mod_registry().iter().find(|(i, _)| *i == id).unwrap().1.fs().probe_read(rel).unwrap_err();
        assert!(probe("full", "src/util.lua").missing);
        assert!(!probe("bare", "src/util.lua").missing);
        let _ = std::fs::remove_dir_all(d);
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
        let _release = GameLockRelease(d.clone());
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        let o = run(&cfg, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Idle);
        assert_eq!(o.file.as_deref(), Some("enshrouded_server"));
        assert!(!d.join("cache").exists());
        release_game_lock(&d);
    }

    #[test]
    fn no_mods_never_restores_unowned_game_data_even_with_an_old_cache() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-restore-{}", std::process::id()));
        std::fs::create_dir_all(d.join("cache")).unwrap();
        std::fs::write(d.join("enshrouded.kfc"), b"KFC patched").unwrap();
        std::fs::write(d.join("enshrouded.kfc.bak"), b"KFC vanilla").unwrap();
        std::fs::write(d.join("cache/files.json"), b"{}").unwrap();
        let _release = GameLockRelease(d.clone());
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        let o = run(&cfg, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Idle);
        // A cache or a compatible backup cannot prove whose current patches these are.
        assert!(!o.restored);
        assert_eq!(std::fs::read(d.join("enshrouded.kfc")).unwrap(), b"KFC patched");
        assert_eq!(std::fs::read(d.join("cache/files.json")).unwrap(), b"{}");
        release_game_lock(&d);
    }

    #[test]
    fn the_live_state_is_written_when_the_run_starts_and_the_vanilla_check_counts_what_it_reads() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-live-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        // game data identical to its backup: nothing to restore, but the check reads both pairs
        for name in ["enshrouded.kfc", "enshrouded.kfc.bak", "enshrouded.kfc_resources", "enshrouded.kfc_resources.bak"] {
            std::fs::write(d.join(name), vec![7u8; 3 << 20]).unwrap();
        }
        let _release = GameLockRelease(d.clone());
        let display = std::sync::Arc::new(Display::with_window(Some(d.join("state")), None));
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, progress: Some(display), ..Default::default() };
        let o = run(&cfg, Vec::new(), Vec::new());
        assert_eq!((o.phase, o.restored), (Phase::Idle, false));
        // the plugin publishes the result over it; until then the file says what the run is doing, with the mods list of a state
        let live: serde_json::Value = serde_json::from_str(&std::fs::read_to_string(d.join("state/eml.json")).unwrap()).unwrap();
        assert_eq!((live["phase"].as_str(), live["format"].as_u64(), live["file"].as_str()), (Some("running"), Some(1), Some("enshrouded")));
        assert!(live["mods"].is_array() && live["progress"]["stage"].is_string(), "{live}");

        // `files_differ` counts the bytes it reads into the progress, and still finds a single changed byte
        let progress = Progress::none();
        progress.stage(Stage::Checking);
        progress.announce(2, 12 << 20);
        let (a, b) = (d.join("enshrouded.kfc"), d.join("enshrouded.kfc.bak"));
        assert!(!files_differ(&a, &b, &progress));
        assert_eq!(progress.snapshot().unwrap().done, 6 << 20);
        let mut bytes = std::fs::read(&b).unwrap();
        bytes[(3 << 20) - 1] ^= 1;
        std::fs::write(&b, &bytes).unwrap();
        assert!(files_differ(&a, &b, &progress));
        std::fs::write(&b, b"short").unwrap();
        assert!(files_differ(&a, &b, &progress), "another length");
        release_game_lock(&d);
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn preparation_cause_reaches_status_reasons() {
        let (d, paths) = mods_fixture("prepare-cause", &["a", "b"]);
        // Enough to select the archive; a missing reflection executable fails preparation before backup creation.
        std::fs::write(d.join("enshrouded.kfc"), b"KFC").unwrap();
        let candidates = paths.iter().map(|path| crate::discover::Candidate {
            path: path.clone(), source: "test".into(), enabled: true,
        }).collect::<Vec<_>>();
        let (mods, entries) = crate::discover::resolve(&candidates);
        let _release = GameLockRelease(d.clone());
        let cfg = RunConfig { game_dir: d.clone(), cache_dir: d.join("cache"), patch: true, ..Default::default() };
        let out = run(&cfg, mods, entries);
        assert_eq!(out.phase, Phase::Failed);
        let error = out.error.as_deref().unwrap();
        assert!(error.contains("could not prepare the game data: load_type_registry:") && error.contains("enshrouded.exe") && error.contains("os error"), "{error}");
        assert!(out.reason.as_deref().unwrap().contains(error));
        for entry in &out.mods {
            assert_eq!(entry.state, "failed");
            assert!(entry.reason.as_deref().unwrap().contains(error));
        }
        let json = serde_json::to_value(&out).unwrap();
        assert!(json["reason"].as_str().unwrap().contains(error));
        assert!(json["mods"][0]["reason"].as_str().unwrap().contains(error));
        release_game_lock(&d);
        std::fs::remove_dir_all(d).unwrap();
    }

    #[test]
    fn fails_without_game_files() {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-nogame-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        let o = run(&RunConfig { game_dir: d.clone(), cache_dir: d.join("c"), ..Default::default() }, Vec::new(), Vec::new());
        assert_eq!(o.phase, Phase::Failed);
    }
    /// The second process is created before this one takes the lock, and takes it only once told to: a process created
    /// while the lock is held would share its file description (the test gate never creates one then), so the lock it
    /// finds held is one it never had.
    #[test]
    fn holds_install_lock_against_a_second_process() {
        use std::io::{Read, Write};
        if let Ok(path) = std::env::var("KILN_EML_LOCK_TEST_CHILD") {
            let mut go = [0u8];
            std::io::stdin().read_exact(&mut go).unwrap();
            assert!(lock_game(Utf8Path::new(&path)).is_err());
            return;
        }
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-lock-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        let mut child = mod_loader_lua::test_gate::spawn(std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "runner::tests::holds_install_lock_against_a_second_process"])
            .env("KILN_EML_LOCK_TEST_CHILD", d.as_str()).stdin(std::process::Stdio::piped())).unwrap();
        let _release = GameLockRelease(d.clone());
        lock_game(&d).unwrap();
        child.stdin.take().unwrap().write_all(b"g").unwrap();
        let status = child.wait().unwrap();
        release_game_lock(&d);
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
        let status = mod_loader_lua::test_gate::status(std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "runner::tests::an_exited_process_leaves_no_stale_install_lock"])
            .env(CHILD, d.as_str())).unwrap();
        assert!(status.success());
        assert!(d.join(".kiln-eml.lock").exists());
        let _release = GameLockRelease(d.clone());
        lock_game(&d).unwrap();
        release_game_lock(&d);
        let _ = std::fs::remove_dir_all(d);
    }

    //
    /// One install by any of its names: a game started from it by another spelling (the Greek letter's other case, the
    /// 8.3 name, Z: under Wine or a local share on Windows) is seen running, and its install lock, once held, is held
    /// under every name (a second lock_game would otherwise lock the same file twice and fail).
    #[cfg(windows)]
    #[test]
    fn the_same_install_by_another_name_is_the_same_game() {
        use std::io::{Read, Write};
        use crate::same_path::windows_names;
        const CHILD: &str = "KILN_EML_OTHER_NAME_GAME";
        if std::env::var(CHILD).is_ok() {
            let mut go = [0u8];
            let _ = std::io::stdin().read_exact(&mut go);
            return;
        }
        let root = std::env::temp_dir().join(format!("kiln-eml-names-{}", std::process::id()));
        // a long folder name (it has an 8.3 name) with a Greek letter with a subscript iota: ᾳ U+1FB3 and ᾼ U+1FBC are one
        // letter to NTFS and Wine
        let game = root.join("Enshrouded \u{1FB3} game");
        std::fs::create_dir_all(&game).unwrap();
        std::fs::copy(std::env::current_exe().unwrap(), game.join("enshrouded.exe")).unwrap();
        let canonical = Utf8PathBuf::from_path_buf(std::fs::canonicalize(&game).unwrap()).unwrap();
        let mut names = vec![(root.join("ENSHROUDED \u{1FBC} GAME"), "another case")];
        match windows_names::short(&game) {
            Some(short) => names.push((short, "the 8.3 name")),
            None => eprintln!("no 8.3 name for {} on this volume", game.display()),
        }
        match windows_names::other_path(&game) {
            Some(other) => names.push((other, "another drive letter or a share")),
            None => eprintln!("no other path to {} here", game.display()),
        }
        assert!(!other_game_running(&canonical), "nothing runs from the install yet");
        let mut missed = Vec::new();
        for (name, why) in &names {
            assert!(std::fs::metadata(name.join("enshrouded.exe")).is_ok(), "{} is there ({why})", name.display());
            let mut child = mod_loader_lua::test_gate::spawn(std::process::Command::new(name.join("enshrouded.exe"))
                .args(["--exact", "runner::tests::the_same_install_by_another_name_is_the_same_game"])
                .env(CHILD, "1").stdin(std::process::Stdio::piped())).unwrap();
            let seen = other_game_running(&canonical);
            child.stdin.take().unwrap().write_all(b"g").unwrap();
            assert!(child.wait().unwrap().success());
            if !seen { missed.push(format!("{why} ({})", name.display())); }
        }
        assert!(missed.is_empty(), "a game started by another name of the install was not seen running: {missed:?}");
        let _release = GameLockRelease(canonical.clone());
        lock_game(&canonical).unwrap();
        for (name, why) in &names {
            let name = Utf8PathBuf::from_path_buf(name.clone()).unwrap();
            if let Err(e) = lock_game(&name) { missed.push(format!("{why} ({name}): {e}")); }
        }
        release_game_lock(&canonical);
        assert!(missed.is_empty(), "the held install was not known by another name: {missed:?}");
        let tag = format!("kiln-eml-names-{}", std::process::id());
        assert!(!GAME_LOCKS.lock().unwrap().iter().any(|(p, _, _)| p.as_str().contains(&tag)), "released under its own name");
        let _ = std::fs::remove_dir_all(root);
    }

    /// Two installs whose names the name fold joins but NTFS and Wine keep apart (the Kelvin sign and `K`: the fold is
    /// coarser than the system's table) are two installs: holding the lock of one never counts as holding the other,
    /// whose own lock is taken.
    #[cfg(windows)]
    #[test]
    fn two_installs_the_fold_joins_are_two_locks() {
        let root = std::env::temp_dir().join(format!("kiln-eml-kelvin-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let names = [root.join("game K"), root.join("game \u{212A}")];
        for d in &names { std::fs::create_dir_all(d).unwrap(); }
        let [a, b] = names.map(|d| Utf8PathBuf::from_path_buf(std::fs::canonicalize(d).unwrap()).unwrap());
        let ids = [&a, &b].map(|d| crate::same_path::identity(d.as_std_path()).unwrap());
        assert!(ids[0].is_some() && ids[0] != ids[1], "two folders to this system: {ids:?}");
        assert!(crate::same_path::names_equal(a.as_std_path(), b.as_std_path()), "one name to the fold");
        let (_release_a, _release_b) = (GameLockRelease(a.clone()), GameLockRelease(b.clone()));
        lock_game(&a).unwrap();
        lock_game(&b).unwrap();
        assert!(b.join(".kiln-eml.lock").is_file(), "the second install's own lock was taken");
        let held = GAME_LOCKS.lock().unwrap().iter().filter(|(p, _, _)| *p == a || *p == b).count();
        assert_eq!(held, 2, "each install holds its own lock");
        release_game_lock(&a);
        assert!(GAME_LOCKS.lock().unwrap().iter().any(|(p, _, _)| *p == b), "releasing one keeps the other");
        release_game_lock(&b);
        let _ = std::fs::remove_dir_all(root);
    }

    #[test]
    fn server_host_selects_server_data_when_both_games_are_present() {
        let dir = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-side-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        for name in ["enshrouded", "enshrouded_server"] { std::fs::write(dir.join(format!("{name}.kfc")), "fixture").unwrap(); }
        let _release = GameLockRelease(dir.clone());
        let config = RunConfig { game_dir: dir.clone(), file_name: Some("enshrouded_server".into()), ..Default::default() };
        assert_eq!(run(&config, Vec::new(), Vec::new()).file.as_deref(), Some("enshrouded_server"));
        release_game_lock(&dir);
        let _ = std::fs::remove_dir_all(dir);
    }

}

#[cfg(test)]
#[path = "runner_ownership_tests.rs"]
mod ownership_tests;

#[cfg(test)]
mod skip_reason_tests {
    use super::skip_reason;

    #[test]
    fn the_environment_switch_says_how_to_undo_it() {
        // inbox 36: a player set KILN_SHROUDFORGE=0 thinking 0 means "default"
        let note = skip_reason(Some("0"), || unreachable!("the variable is the reason"));
        for text in ["KILN_SHROUDFORGE=0 environment variable switches off", "Remove that variable (do not set it to 0)",
                     "restart Steam and the Kiln launcher", "start the game again", "the game is as without the mod"] {
            assert!(note.contains(text), "{note}");
        }
        assert!(!note.contains("override") && !note.to_lowercase().contains("install"), "{note}");
        // unset, 1 and anything else do not switch ShroudForge off: the other reason stays
        for env in [None, Some("1"), Some(""), Some("other")] {
            let note = skip_reason(env, || "ShroudForge did not reserve this start".into());
            assert!(note.starts_with("needs ShroudForge's runtime, which is not running: ShroudForge did not reserve this start;"),
                    "{env:?}: {note}");
        }
    }
}
