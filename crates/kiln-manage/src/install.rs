//! Install / upgrade / uninstall / rollback of a runtime bundle into a game folder (contracts.md, "Install").
//! Refuses while the game runs or Steam updates it. Stages in `kiln/.staging-*`, verifies, classifies the existing
//! `dinput8.dll` (pe::classify_proxy: Kiln -> replace; ShroudForge -> refuse unless `switch_from_shroudforge`, then
//! only its dinput8.dll goes to the backup; EMF and every other engine or unknown DLL -> `kiln/chain/<word>-dinput8.dll`
//! plus chain.ini [chain]; Ultimate ASI Loader -> `xinput9_1_0.dll` next to the game, the name it runs under), never
//! touches DLLs under other proxy names (dbghelp.dll, winmm.dll, powrprof.dll ... stay where they are and keep
//! working), backs up what it replaces to
//! `kiln/backups/install-<yyyymmddThhmmssZ>/`, puts `dinput8.dll` last, writes the receipt `kiln/.install.json`.
//! One install or uninstall at a time per game folder (`kiln/.install.lock`).

use crate::bundle::RuntimeManifest;
use crate::game::GameInstall;
use crate::manifest::RunSide;
use crate::paths::DataDirs;
use crate::pe::ProxyClass;
use crate::task::Progress;
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallReceipt {
    pub format: u32,
    pub install_id: String,
    pub installed: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub updated: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub installer: Option<String>,
    pub runtime: ReceiptRuntime,
    pub side: RunSide,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub platform: Option<crate::game::Platform>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub game: Option<ReceiptGame>,
    pub files: Vec<ReceiptFile>,
    #[serde(default)]
    pub replaced: Vec<ReplacedFile>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReceiptRuntime {
    pub version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
    #[serde(default)]
    pub components: Vec<String>,
    /// Default components the player turned off (an explicit choice): upgrades keep them off unless required.
    /// Other defaults, including new and newly default components, are added on upgrade. `None`: before this field
    /// (its plain list records no choice against anything, so nothing counts as turned off). Opt-outs of components
    /// a bundle no longer has are kept, so a rollback and a later update do not bring them back.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub deselected: Option<Vec<String>>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReceiptGame {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exe: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub sha256: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub steam_build: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReceiptFile {
    pub path: String,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub size: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub component: Option<String>,
    #[serde(default)]
    pub config: bool,
}

/// `replaced[].class`
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ReplacedClass {
    Kiln,
    ShroudForge,
    Emf,
    Unknown,
    File,
}

/// `replaced[].action`
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum ReplacedAction {
    Chained,
    BackedUp,
    Replaced,
    /// The path existed before the install with the bundle's exact bytes (e.g. a kiln/ folder deployed by
    /// deploy-server.sh or client.sh): left in place and kept by uninstall. No backup needed. Becomes `replaced` (with
    /// a backup of those bytes) when an upgrade changes the file.
    Adopted,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ReplacedFile {
    pub path: String,
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub size: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub class: Option<ReplacedClass>,
    pub action: ReplacedAction,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub chain_path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub backup: Option<String>,
    /// The engine it belongs to (pe::ProxyKind), when it is one Kiln knows; `class` stays one of the five classes.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub engine: Option<ProxyKind>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallOptions {
    /// None: the bundle's default components for this side; on an upgrade, the installed ones plus every default
    /// the player did not turn off (resolve_components). Some: the player's explicit choice; the bundle's defaults
    /// it leaves out are recorded as turned off (`runtime.deselected`).
    #[serde(default)]
    pub components: Option<Vec<String>>,
    /// Move ShroudForge's dinput8.dll to the backup (never chained) instead of refusing. Its dbghelp.dll, winmm.dll
    /// and `shroudforge/` stay in place (Kiln's ShroudForge plugin drives the installed runtime).
    #[serde(default)]
    pub switch_from_shroudforge: bool,
    /// The receipt's `installer` (default `kiln-manage <VERSION>`); the launcher passes `kiln-launcher <version>`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub installer: Option<String>,
}

/// What an install did besides writing its receipt (not part of the receipt file).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallReport {
    /// Kiln files changed by hand since the last install that this install replaced (game-relative paths). The
    /// changed copies are in `backup` and in `kiln/backups/install-<ts>/` of the game folder.
    #[serde(default)]
    pub changed_backed_up: Vec<String>,
    /// This install's backup folder in the launcher's data dir (`<data>/backups/install/<installId>/install-<ts>`),
    /// when it backed anything up.
    #[serde(default)]
    pub backup: Option<String>,
    #[serde(default)]
    pub restored_shroudforge: Vec<String>,
    #[serde(default)]
    pub warnings: Vec<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallState {
    pub installed: bool,
    pub receipt: Option<InstallReceipt>,
    /// What `dinput8.dll` is now.
    pub proxy: Option<ProxyClass>,
    /// Receipt files whose sha256 changed since the install (game-relative paths).
    pub modified: Vec<String>,
    /// A newer bundle in `<data>/runtime/` (its version).
    pub upgrade_available: Option<String>,
    /// Components of the installed bundle (when it is in `<data>/runtime/`) missing from the receipt that a
    /// reinstall adds: defaults not turned off and requirements of the resulting selection.
    #[serde(default)]
    pub missing_parts: Vec<String>,
}

use crate::bundle::RuntimeFile;
use crate::pe::ProxyKind;
use crate::util::{io_at, now_unix, rfc3339, ts_compact};
use crate::ErrorCode;
use std::collections::BTreeSet;
use std::path::PathBuf;

/// ShroudForge's proxy names. Only its dinput8.dll is ever moved (by "switch from ShroudForge"); the others are its
/// own loader and helper, and the same names belong to EML (dbghelp.dll) and Shroudtopia (winmm.dll) in other setups.
pub const SHROUDFORGE_DLLS: [&str; 3] = ["dbghelp.dll", "dinput8.dll", "winmm.dll"];
/// The name Ultimate ASI Loader is chained under (it refuses to run under names it does not know; the game loads
/// nothing by this name). EMF's installer does the same.
pub const ASI_LOADER_NAME: &str = "xinput9_1_0.dll";
pub const PROXY: &str = "dinput8.dll";
/// Matches native/proxy/dinput8.cpp's kMaxIni.
const MAX_CHAIN_BYTES: u64 = 256 * 1024;

pub fn read_receipt(game_dir: &Path) -> Result<Option<InstallReceipt>> {
    let r: Option<InstallReceipt> = crate::util::read_json(&game_dir.join(crate::paths::game::RECEIPT))?;
    if let Some(r) = &r
        && r.format > 1
    {
        return Err(Error::new(ErrorCode::Unsupported, format!("kiln/.install.json format {} (this Kiln reads format 1)", r.format)));
    }
    if let Some(r) = &r {
        for p in receipt_paths(r) { crate::bundle::safe_rel_path(p)?; }
    }
    Ok(r)
}

fn receipt_paths(r: &InstallReceipt) -> impl Iterator<Item = &str> {
    r.files.iter().map(|f| f.path.as_str()).chain(r.replaced.iter().flat_map(|f| {
        std::iter::once(f.path.as_str()).chain(f.chain_path.as_deref()).chain(f.backup.as_deref())
    }))
}

fn side_of(install: &GameInstall) -> RunSide {
    match install.kind {
        crate::game::GameKind::Server => RunSide::Server,
        crate::game::GameKind::Client => RunSide::Client,
    }
}

/// sha256 of a file, remembered while it is unchanged (crate::filecache: size, mtime, inode/ctime; across launcher
/// starts too): `status` re-hashes only files that changed since the last call.
fn cached_sha(p: &Path) -> Option<String> {
    crate::filecache::sha256(p).ok()
}

/// Receipt files whose content changed since the install (missing files count; config files never do). A file whose
/// size differs from the receipt's is changed without hashing; the others are hashed (cached by size and mtime).
pub fn modified_files(game_dir: &Path, receipt: &InstallReceipt) -> Vec<String> {
    receipt
        .files
        .iter()
        .filter(|f| !f.config)
        .filter(|f| {
            let p = game_dir.join(&f.path);
            match (std::fs::metadata(&p), f.size) {
                (Err(_), _) => true,
                (Ok(md), Some(size)) if md.len() != size => true,
                _ => cached_sha(&p).is_none_or(|h| h != f.sha256),
            }
        })
        .map(|f| f.path.clone())
        .collect()
}

pub fn status(dirs: &DataDirs, install: &GameInstall) -> Result<InstallState> {
    let receipt = read_receipt(&install.path)?;
    let proxy = crate::pe::classify_proxy(&install.path.join(PROXY)).ok();
    let modified = receipt.as_ref().map(|r| modified_files(&install.path, r)).unwrap_or_default();
    let side = side_of(install);
    let bundles = receipt.as_ref().and_then(|_| crate::bundle::list(dirs).ok());
    let missing_parts = receipt
        .as_ref()
        .and_then(|r| {
            let b = bundles.as_ref()?.iter().find(|b| b.version == r.runtime.version)?;
            let manifest = crate::bundle::read(&b.file).ok()?;
            let choice = resolve_components(&manifest, side, Some(r), None).ok()?;
            Some(
                choice.components
                    .into_iter()
                    .filter(|p| !r.runtime.components.contains(p))
                    .collect(),
            )
        })
        .unwrap_or_default();
    let upgrade_available = receipt.as_ref().and_then(|r| {
        let bundles = bundles?;
        // newer than the installed bundle in the list's order (bundle::compare: version, build date, build time); a
        // bundle no longer in the list compares by its version alone
        let installed = bundles.iter().find(|b| b.version == r.runtime.version).cloned();
        bundles
            .into_iter()
            .filter(|b| b.sides.contains(&side) && b.version != r.runtime.version)
            .find(|b| match &installed {
                Some(i) => crate::bundle::compare(b, i) == std::cmp::Ordering::Greater,
                None => crate::bundle::compare_versions(&b.version, &r.runtime.version) == std::cmp::Ordering::Greater,
            })
            .map(|b| b.version)
    });
    crate::filecache::flush();
    Ok(InstallState { installed: receipt.is_some(), receipt, proxy, modified, upgrade_available, missing_parts })
}

/// The components `requested` (None: defaults) for `side`, with everything they require.
pub fn select_components(manifest: &RuntimeManifest, side: RunSide, requested: Option<&[String]>) -> Result<Vec<String>> {
    let mut want: Vec<String> = match requested {
        Some(list) => {
            for c in list {
                let comp = manifest.components.get(c).ok_or_else(|| Error::invalid(format!("the bundle has no component {c}")))?;
                if !comp.sides.contains(&side) {
                    return Err(Error::invalid(format!("component {c} is not for the {} side", side_name(side))));
                }
            }
            list.to_vec()
        }
        None => manifest
            .components
            .iter()
            .filter(|(_, c)| c.default && c.sides.contains(&side))
            .map(|(n, _)| n.clone())
            .collect(),
    };
    let mut i = 0;
    while i < want.len() {
        let c = &manifest.components[&want[i]];
        for r in &c.requires {
            if !want.contains(r) {
                let rc = manifest.components.get(r).ok_or_else(|| Error::invalid(format!("component {} requires unknown {r}", want[i])))?;
                if !rc.sides.contains(&side) {
                    return Err(Error::invalid(format!("component {} requires {r}, which is not for the {} side", want[i], side_name(side))));
                }
                want.push(r.clone());
            }
        }
        i += 1;
    }
    want.sort();
    want.dedup();
    Ok(want)
}

/// What an install puts in: `components` (with what they require) and `deselected`, the receipt's record of the
/// defaults the player turned off.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ComponentChoice {
    pub components: Vec<String>,
    pub deselected: Vec<String>,
}

/// The components an install of `manifest` puts into a `side` folder whose receipt is `old`:
/// - `requested` (an explicit choice): exactly those, plus what they require; the bundle's defaults it leaves out are
///   recorded as turned off, as are earlier opt-outs not selected (even if now optional or absent);
/// - none and no receipt: the side's defaults;
/// - none and a receipt (update, reinstall, rollback): the installed components the bundle still has for the side,
///   plus every default of the bundle the player did not turn off (new components, components that became
///   defaults). Requirements override opt-outs for this install without erasing them. A receipt without
///   `deselected` (written before it existed) turned nothing off.
pub fn resolve_components(manifest: &RuntimeManifest, side: RunSide, old: Option<&InstallReceipt>, requested: Option<&[String]>)
                          -> Result<ComponentChoice> {
    let defaults: Vec<&String> =
        manifest.components.iter().filter(|(_, c)| c.default && c.sides.contains(&side)).map(|(n, _)| n).collect();
    let old = old.filter(|o| o.side == side);
    let before: Vec<String> = old.and_then(|o| o.runtime.deselected.clone()).unwrap_or_default();
    let components = match (requested, old) {
        (Some(list), _) => select_components(manifest, side, Some(list))?,
        (None, None) => select_components(manifest, side, None)?,
        (None, Some(o)) => {
            let mut want: Vec<String> = o
                .runtime
                .components
                .iter()
                .filter(|c| !before.contains(c) && manifest.components.get(*c).is_some_and(|m| m.sides.contains(&side)))
                .cloned()
                .collect();
            for d in &defaults {
                if !before.contains(d) && !want.contains(d) {
                    want.push((*d).clone());
                }
            }
            select_components(manifest, side, Some(&want))?
        }
    };
    let mut deselected: Vec<String> = match requested {
        Some(_) => defaults.iter().filter(|d| !components.contains(d)).map(|d| (*d).clone())
            .chain(before.iter().filter(|c| !components.contains(c)).cloned())
            .collect(),
        None => before,
    };
    deselected.sort();
    deselected.dedup();
    Ok(ComponentChoice { components, deselected })
}

fn side_name(s: RunSide) -> &'static str {
    match s {
        RunSide::Server => "server",
        RunSide::Client => "client",
    }
}

/// The files of `manifest` for `side` and `components` (None: defaults).
pub fn plan(manifest: &RuntimeManifest, side: RunSide, components: Option<&[String]>) -> Result<Vec<RuntimeFile>> {
    let selected = select_components(manifest, side, components)?;
    Ok(manifest.files.iter().filter(|f| selected.contains(&f.component)).cloned().collect())
}

/// Folders every Kiln game folder has (tools/deploy-server.sh creates the same, so a bundle install matches it).
pub fn standard_dirs(side: RunSide) -> Vec<String> {
    let s = side_name(side);
    ["host", "mods", "plugins", "services", "logs", "control", "data"]
        .iter()
        .map(|d| format!("kiln/{d}"))
        .chain([format!("kiln/sigdb/builds/{s}"), format!("kiln/sigdb/{s}.d")])
        .collect()
}

fn refuse_busy(install: &GameInstall) -> Result<()> {
    if let Some(p) = crate::proc::find_game(install)? {
        return Err(Error::new(ErrorCode::GameRunning, format!("{} is running (pid {}); quit it first", install.exe, p.pid))
            .with_detail(serde_json::json!({ "pid": p.pid })));
    }
    for lib in steam_libraries_of(install) {
        match crate::steam::app_manifest(&lib, install.app_id) {
            Ok(Some(m)) if m.updating => {
                return Err(Error::new(
                    ErrorCode::SteamUpdating,
                    format!("Steam is updating or validating the game (StateFlags {}); wait until it is done", m.state_flags),
                )
                .with_detail(serde_json::json!({ "stateFlags": m.state_flags, "library": lib })));
            }
            Ok(_) => {}
            Err(e) if e.code == ErrorCode::NotImplemented => {}
            Err(_) => {} // an unreadable manifest is Steam's business; the game files are what we check
        }
    }
    Ok(())
}

/// Steam libraries whose appmanifest describes this install: the recorded library, `<lib>/steamapps/common/<dir>`
/// above the folder, and the folder itself (SteamCMD's force_install_dir keeps `steamapps/` inside).
fn steam_libraries_of(install: &GameInstall) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = install.library.iter().cloned().collect();
    if let Some(common) = install.path.parent()
        && common.file_name().is_some_and(|n| n == "common")
        && let Some(steamapps) = common.parent()
        && steamapps.file_name().is_some_and(|n| n == "steamapps")
        && let Some(lib) = steamapps.parent()
    {
        v.push(lib.to_path_buf());
    }
    v.push(install.path.clone());
    v.dedup();
    v
}

/// `kiln/.install.lock`: one install, upgrade or uninstall at a time per game folder. Two launchers (or a launcher and
/// kilnctl) would otherwise clear each other's staging folder. The file holds `<pid> <unix time>`; a lock whose
/// process is gone, or that is older than an hour, is stale and taken over. Removed when dropped.
struct InstallLock(PathBuf);

impl InstallLock {
    fn take(game_dir: &Path) -> Result<Self> {
        let kiln = game_dir.join(crate::paths::game::KILN);
        std::fs::create_dir_all(&kiln).map_err(io_at(&kiln, "creating"))?;
        let p = kiln.join(".install.lock");
        for _ in 0..2 {
            match std::fs::OpenOptions::new().write(true).create_new(true).open(&p) {
                Ok(mut f) => {
                    use std::io::Write;
                    let _ = writeln!(f, "{} {}", std::process::id(), now_unix());
                    return Ok(InstallLock(p));
                }
                Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => {
                    let text = std::fs::read_to_string(&p).unwrap_or_default();
                    let mut it = text.split_whitespace();
                    let pid: Option<u32> = it.next().and_then(|s| s.parse().ok());
                    let at: Option<i64> = it.next().and_then(|s| s.parse().ok());
                    if let Some(pid) = pid
                        && crate::proc::alive(pid)
                        && at.is_some_and(|t| now_unix() - t < 3600)
                    {
                        return Err(Error::new(
                            ErrorCode::Conflict,
                            format!("another install or uninstall is running in this game folder (pid {pid}); wait for it to end, or delete {} if nothing runs", p.display()),
                        )
                        .with_detail(serde_json::json!({ "pid": pid, "lock": p.display().to_string() })));
                    }
                    let _ = std::fs::remove_file(&p); // stale: its process is gone
                }
                Err(e) => return Err(io_at(&p, "creating")(e)),
            }
        }
        Err(Error::new(ErrorCode::Conflict, format!("{} is held by another install", p.display())))
    }
}

impl Drop for InstallLock {
    fn drop(&mut self) {
        let _ = std::fs::remove_file(&self.0);
    }
}

fn rand_suffix() -> Result<String> {
    Ok(crate::hash::hex(&crate::util::random_bytes::<6>()?))
}

fn has_previous(staging: &Path) -> bool {
    match std::fs::read_dir(staging.join("previous")) {
        Ok(mut entries) => entries.next().is_some(),
        Err(e) => e.kind() != std::io::ErrorKind::NotFound,
    }
}

/// Removes abandoned extraction folders, but never the originals held by an interrupted install.
fn clean_stale_staging(kiln: &Path) {
    for e in std::fs::read_dir(kiln).into_iter().flatten().flatten() {
        if e.file_name().to_string_lossy().starts_with(".staging-") && !has_previous(&e.path()) {
            let _ = std::fs::remove_dir_all(e.path());
        }
    }
}

/// What the install did so far, undone in reverse order when a later step fails.
enum Step {
    /// `target` was created from staging; `previous` holds the file it replaced (inside the staging folder).
    Placed { target: PathBuf, previous: Option<PathBuf> },
    /// A file moved away (displaced proxy, ShroudForge DLL).
    Moved { from: PathBuf, to: PathBuf },
    /// Exact pre-edit bytes (chain.ini); rollback must also undo a failed writer.
    Edited { target: PathBuf, previous: Option<Vec<u8>> },
}

fn undo(steps: Vec<Step>) -> Result<()> {
    let mut failures = Vec::new();
    for s in steps.into_iter().rev() {
        let result = (|| {
            match s {
                Step::Placed { target, previous } => {
                    match std::fs::remove_file(&target) {
                        Ok(()) => {},
                        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {},
                        Err(e) => return Err(io_at(&target, "undoing install")(e)),
                    }
                    if let Some(p) = previous { std::fs::rename(&p, &target).map_err(io_at(&target, "restoring"))?; }
                }
                Step::Moved { from, to } => { std::fs::rename(&to, &from).map_err(io_at(&from, "restoring"))?; }
                Step::Edited { target, previous } => {
                    if let Some(bytes) = previous { crate::util::write_atomic(&target, &bytes)?; }
                    else if target.exists() { std::fs::remove_file(&target).map_err(io_at(&target, "undoing edit"))?; }
                }
            }
            Ok(())
        })();
        if let Err(e) = result { failures.push(e.message); }
    }
    if failures.is_empty() { Ok(()) }
    else { Err(Error::new(ErrorCode::Io, format!("rollback incomplete: {}", failures.join("; ")))) }
}

fn rollback_error(steps: Vec<Step>, original: Error, staging: &Path) -> Error {
    match undo(steps) {
        Ok(()) => original,
        Err(e) => Error::new(original.code, format!("{}; {}. Recovery files remain in {} and kiln/backups", original.message, e.message, staging.display())),
    }
}

fn copy_into_backup(game_dir: &Path, backup_rel: &str, rel: &str, mirror: Option<&Path>) -> Result<String> {
    let src = game_dir.join(rel);
    let dst_rel = format!("{backup_rel}/{rel}");
    let dst = game_dir.join(&dst_rel);
    if let Some(p) = dst.parent() {
        std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
    }
    copy_new(&src, &dst)?;
    if let Some(m) = mirror {
        let mdst = m.join(rel);
        if let Some(p) = mdst.parent() {
            std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
        }
        copy_new(&src, &mdst)?;
    }
    Ok(dst_rel)
}

fn copy_new(src: &Path, dst: &Path) -> Result<()> {
    let mut input = std::fs::File::open(src).map_err(io_at(src, "reading"))?;
    let mut output = std::fs::OpenOptions::new().write(true).create_new(true).open(dst).map_err(io_at(dst, "creating"))?;
    let result = std::io::copy(&mut input, &mut output).and_then(|_| output.sync_all());
    if let Err(e) = result {
        drop(output);
        let _ = std::fs::remove_file(dst);
        return Err(io_at(dst, "copying to")(e));
    }
    Ok(())
}

/// Never follow a game-folder symlink while replacing or moving files (including a dangling one).
fn refuse_symlinks(game_dir: &Path, relative: &str) -> Result<()> {
    let mut p = game_dir.to_path_buf();
    for c in Path::new(relative).components() {
        p.push(c);
        match std::fs::symlink_metadata(&p) {
            Ok(md) if md.file_type().is_symlink() => return Err(Error::new(ErrorCode::Refused, format!("{} is a symlink; move it away before installing", p.display()))),
            Ok(_) => {},
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => break,
            Err(e) => return Err(io_at(&p, "reading")(e)),
        }
    }
    Ok(())
}

fn file_sha(p: &Path) -> Option<(String, u64)> {
    crate::hash::sha256_file(p).ok()
}

/// Treat every existing Windows name as occupied, including unreadable files, directories and dangling symlinks.
fn occupied(path: &Path) -> Result<bool> {
    let parent = path.parent().ok_or_else(|| Error::invalid("path has no parent"))?;
    let name = path.file_name().ok_or_else(|| Error::invalid("path has no name"))?.to_string_lossy();
    let entries = match std::fs::read_dir(parent) {
        Ok(e) => e,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(false),
        Err(e) => return Err(io_at(parent, "reading")(e)),
    };
    for e in entries {
        let e = e.map_err(io_at(parent, "reading"))?;
        if e.file_name().to_string_lossy().eq_ignore_ascii_case(&name) { return Ok(true); }
    }
    Ok(false)
}

/// A fresh slot: even identical existing files belong to the player and must survive uninstall.
fn chain_slot(game_dir: &Path, kind: &str) -> Result<String> {
    for i in 1.. {
        let name = if i == 1 { format!("{kind}-dinput8.dll") } else { format!("{kind}-dinput8-{i}.dll") };
        let rel = format!("{}/{name}", crate::paths::game::CHAIN_DIR);
        if !occupied(&game_dir.join(&rel))? { return Ok(rel); }
    }
    unreachable!()
}

/// `kiln/chain/x.dll` -> the chain.ini line (relative to kiln/, backslashes); a game-folder path outside kiln/
/// (`xinput9_1_0.dll`) -> `..\xinput9_1_0.dll`.
fn chain_line(chain_rel: &str) -> String {
    match chain_rel.strip_prefix("kiln/") {
        Some(rest) => rest.replace('/', "\\"),
        None => format!("..\\{}", chain_rel.replace('/', "\\")),
    }
}

/// The file-name word of a chained proxy's kind (`kiln/chain/<word>-dinput8.dll`).
fn chain_word(kind: ProxyKind) -> &'static str {
    match crate::engines::Engine::of_proxy(kind) {
        Some(e) => crate::engines::info(e).word,
        None => "unknown",
    }
}

/// Installs or upgrades (an existing receipt: deletes obsolete files only when unchanged).
pub fn install(dirs: &DataDirs, install: &GameInstall, bundle: &Path, options: &InstallOptions,
               progress: &mut dyn Progress) -> Result<InstallReceipt> {
    install_report(dirs, install, bundle, options, progress).map(|(r, _)| r)
}

/// `install`, with what it backed up besides the receipt (hand-changed Kiln files).
pub fn install_report(dirs: &DataDirs, install: &GameInstall, bundle: &Path, options: &InstallOptions,
                      progress: &mut dyn Progress) -> Result<(InstallReceipt, InstallReport)> {
    install_report_adding(dirs, install, bundle, options, &[], progress)
}

/// Install while adding named parts to the automatic selection (e.g. `deploy --samples`). This is not an
/// explicit replacement of the player's selection: only the named additions clear earlier opt-outs.
pub fn install_adding(dirs: &DataDirs, install: &GameInstall, bundle: &Path, options: &InstallOptions,
                      additions: &[String], progress: &mut dyn Progress) -> Result<InstallReceipt> {
    install_report_adding(dirs, install, bundle, options, additions, progress).map(|(r, _)| r)
}

fn install_report_adding(dirs: &DataDirs, install: &GameInstall, bundle: &Path, options: &InstallOptions,
                         additions: &[String], progress: &mut dyn Progress) -> Result<(InstallReceipt, InstallReport)> {
    let game_dir = install.path.as_path();
    let exe = game_dir.join(&install.exe);
    if !exe.is_file() {
        return Err(Error::not_found(format!("{} has no {}", game_dir.display(), install.exe)));
    }
    let side = side_of(install);
    let expected_exe = match side {
        RunSide::Server => "enshrouded_server.exe",
        RunSide::Client => "enshrouded.exe",
    };
    if !install.exe.eq_ignore_ascii_case(expected_exe) {
        return Err(Error::invalid(format!("a {} install runs {expected_exe}, not {}", side_name(side), install.exe)));
    }
    refuse_busy(install)?;
    refuse_symlinks(game_dir, "kiln/.install.lock")?;
    let _lock = InstallLock::take(game_dir)?;
    progress.report(Some(0.0), "verifying the bundle");
    let manifest = crate::bundle::verify(bundle, progress)?;
    let bundle_sha = crate::hash::sha256_file(bundle)?.0;
    let old = read_receipt(game_dir)?;
    if let Some(r) = &old {
        for p in receipt_paths(r) { refuse_symlinks(game_dir, p)?; }
    }
    if let Some(o) = &old
        && o.side != side
    {
        return Err(Error::new(ErrorCode::Conflict, format!("the receipt says {} side, this is a {} install", side_name(o.side), side_name(side))));
    }
    let mut choice = resolve_components(&manifest, side, old.as_ref(), options.components.as_deref())?;
    if !additions.is_empty() {
        choice.components.extend_from_slice(additions);
        choice.components = select_components(&manifest, side, Some(&choice.components))?;
        choice.deselected.retain(|c| !additions.contains(c));
    }
    let files = plan(&manifest, side, Some(&choice.components))?;
    for f in &files {
        let low = f.path.to_ascii_lowercase();
        if (crate::engines::is_proxy_name(&low) && low != PROXY)
            || low.starts_with("kiln/chain/") || low.starts_with("kiln/backups/")
            || low.starts_with("kiln/.install") || low.starts_with("kiln/.staging-")
            || (low == "kiln/chain.ini" && !f.config)
        {
            return Err(Error::new(ErrorCode::Refused, format!("a runtime cannot own {}: other proxy slots and installer recovery paths are reserved", f.path)));
        }
    }
    for rel in files.iter().map(|f| f.path.as_str()).chain(["kiln/chain", "kiln/backups", "kiln/chain.ini", "kiln/.install.json"]) {
        refuse_symlinks(game_dir, rel)?;
    }
    if !files.iter().any(|f| f.path == PROXY) {
        return Err(Error::invalid(format!("the selected components have no {PROXY}; select core")));
    }
    let kiln = game_dir.join(crate::paths::game::KILN);
    std::fs::create_dir_all(&kiln).map_err(io_at(&kiln, "creating"))?;
    clean_stale_staging(&kiln);
    let staging = kiln.join(format!(".staging-{}", rand_suffix()?));
    let result = install_staged(dirs, install, bundle, &bundle_sha, &manifest, &choice, &files, old, options, &staging, progress);
    if result.is_ok() || !has_previous(&staging) { let _ = std::fs::remove_dir_all(&staging); }
    result
}

#[allow(clippy::too_many_arguments)]
fn install_staged(dirs: &DataDirs, install: &GameInstall, bundle: &Path, bundle_sha: &str, manifest: &RuntimeManifest,
                  choice: &ComponentChoice, files: &[RuntimeFile], old: Option<InstallReceipt>, options: &InstallOptions,
                  staging: &Path, progress: &mut dyn Progress) -> Result<(InstallReceipt, InstallReport)> {
    let game_dir = install.path.as_path();
    let side = side_of(install);
    let new_dir = staging.join("new");
    let prev_dir = staging.join("previous");
    progress.report(Some(0.3), "staging");
    crate::bundle::extract(bundle, files, &new_dir, progress)?;
    if progress.canceled() {
        return Err(Error::new(ErrorCode::Canceled, "canceled"));
    }
    let now = now_unix();
    // the id stays what the folder had: the receipt's, else the install's path-derived UUID (game::install_id_for)
    let install_id = match &old {
        Some(o) => o.install_id.clone(),
        None if crate::game::is_uuid(&install.id) => install.id.clone(),
        None => crate::game::path_install_id(&install.path),
    };
    let backup_name = format!("install-{}-{}", ts_compact(now), rand_suffix()?);
    let backup_rel = format!("{}/{backup_name}", crate::paths::game::BACKUPS);
    let mirror = dirs.backups().join("install").join(&install_id).join(&backup_name);
    let old_files: std::collections::BTreeMap<&str, &ReceiptFile> =
        old.iter().flat_map(|o| o.files.iter()).map(|f| (f.path.as_str(), f)).collect();
    let mut replaced: Vec<ReplacedFile> = old.as_ref().map(|o| o.replaced.clone()).unwrap_or_default();
    let mut steps: Vec<Step> = Vec::new();
    let mut changed_backed_up: Vec<String> = Vec::new();
    let mut restored_shroudforge = Vec::new();
    let mut warnings = Vec::new();

    let outcome: Result<()> = (|| {
        // Older switch-from-SF installs displaced all three DLLs. Restore only missing helpers with proven original
        // hashes. Never chain them or overwrite a helper the player has since installed. This is a courtesy for files
        // Kiln does not own: only while ShroudForge itself is still in the game folder, and a backup that is gone is a
        // warning, never a refused install.
        let sf_installed = crate::shroudforge::detect(game_dir).is_some();
        for rel in ["dbghelp.dll", "winmm.dll"] {
            let Some(r) = replaced.iter().find(|r| r.path == rel) else { continue };
            if r.class != Some(ReplacedClass::ShroudForge) || r.action != ReplacedAction::BackedUp || !sf_installed { continue; }
            let target = game_dir.join(rel);
            if occupied(&target)? {
                if file_sha(&target).is_none_or(|(h, _)| h != r.sha256) {
                    warnings.push(format!("Kept current {rel}; it differs from the previous ShroudForge helper. The original remains in {}.", r.backup.as_deref().unwrap_or("the install backup")));
                }
                continue;
            }
            // the backup in the game folder, else the second copy every install backup has in the launcher's data folder
            let good = |p: &Path| file_sha(p).is_some_and(|(h, _)| h == r.sha256);
            let copies = r.backup.as_ref().map(|b| {
                let mirrored = b.strip_prefix(&format!("{}/", crate::paths::game::BACKUPS))
                    .map(|rest| dirs.backups().join("install").join(&install_id).join(rest));
                [Some(game_dir.join(b)), mirrored]
            });
            let Some(backup) = copies.into_iter().flatten().flatten().find(|p| good(p)) else {
                warnings.push(format!("Did not restore ShroudForge's {rel}: its backup {} is missing or changed, and the launcher's backups hold no copy either. Reinstall ShroudForge if you need it.",
                    r.backup.as_deref().unwrap_or("(none recorded)")));
                continue;
            };
            copy_new(&backup, &target)?;
            steps.push(Step::Placed { target: target.clone(), previous: None });
            if file_sha(&target).is_none_or(|(h, _)| h != r.sha256) {
                return Err(Error::new(ErrorCode::HashMismatch, format!("recovered ShroudForge {rel} does not match its original")));
            }
            restored_shroudforge.push(rel.to_owned());
            progress.report(None, &format!("restored {rel} from the earlier switch-from-ShroudForge backup"));
        }
        // 1. dinput8.dll: what is there now decides
        let proxy_path = game_dir.join(PROXY);
        let class = crate::pe::classify_proxy(&proxy_path)?;
        let ours = class.sha256.as_deref().is_some_and(|h| old_files.get(PROXY).is_some_and(|f| f.sha256 == h));
        match class.kind {
            ProxyKind::None => {}
            _ if ours => {} // our previous install: replaced below like every receipt file
            ProxyKind::Kiln => {
                let backup = copy_into_backup(game_dir, &backup_rel, PROXY, Some(&mirror))?;
                replaced.push(ReplacedFile {
                    path: PROXY.into(),
                    sha256: class.sha256.clone().unwrap_or_default(),
                    size: class.size,
                    class: Some(ReplacedClass::Kiln),
                    action: ReplacedAction::Replaced,
                    chain_path: None,
                    backup: Some(backup),
                    engine: Some(ProxyKind::Kiln),
                });
            }
            ProxyKind::ShroudForge => {
                if !options.switch_from_shroudforge {
                    return Err(Error::new(
                        ErrorCode::Refused,
                        "ShroudForge's mod loader is installed (dinput8.dll); choose \"switch from ShroudForge\" to move it to a \
                         backup (it is never chained with Kiln)",
                    )
                    .with_detail(serde_json::json!({ "detail": class.detail })));
                }
                // only ShroudForge's dinput8.dll: Kiln's proxy takes that name. Its dbghelp.dll, winmm.dll and
                // shroudforge/ stay (Kiln's ShroudForge plugin drives the installed runtime; its repair check only
                // looks for the files)
                let (sha, size) = file_sha(&proxy_path).ok_or_else(|| Error::not_found(format!("{} vanished", proxy_path.display())))?;
                let backup = copy_into_backup(game_dir, &backup_rel, PROXY, Some(&mirror))?;
                let hold = prev_dir.join(PROXY);
                std::fs::create_dir_all(&prev_dir).map_err(io_at(&prev_dir, "creating"))?;
                std::fs::rename(&proxy_path, &hold).map_err(io_at(&proxy_path, "moving"))?;
                steps.push(Step::Moved { from: proxy_path.clone(), to: hold });
                replaced.push(ReplacedFile {
                    path: PROXY.into(),
                    sha256: sha,
                    size: Some(size),
                    class: Some(ReplacedClass::ShroudForge),
                    action: ReplacedAction::BackedUp,
                    chain_path: None,
                    backup: Some(backup),
                    engine: Some(ProxyKind::ShroudForge),
                });
            }
            ProxyKind::NotDll => {
                return Err(Error::new(
                    ErrorCode::Refused,
                    format!("{} is not a 64-bit DLL ({}); move it away first", proxy_path.display(), class.detail.unwrap_or_default()),
                ));
            }
            _ if class.detail.as_deref() != Some("Wine builtin DLL") => {
                // EMF, every other engine Kiln knows, or an unknown DLL mod: chained, never displaced
                let kind = chain_word(class.kind);
                let sha = class.sha256.clone().unwrap_or_default();
                let chain_path = game_dir.join(crate::paths::game::CHAIN);
                if std::fs::metadata(&chain_path).is_ok_and(|m| m.len() > MAX_CHAIN_BYTES)
                    || crate::chain::read(game_dir)?.chain.len() >= crate::chain::MAX_ENTRIES
                {
                    return Err(Error::new(ErrorCode::Refused, "chain.ini is at the proxy's size or entry limit; no loader was moved"));
                }
                let chain_rel = if class.kind == ProxyKind::UltimateAsiLoader {
                    if occupied(&game_dir.join(ASI_LOADER_NAME))? {
                        return Err(Error::new(
                            ErrorCode::Refused,
                            format!("Ultimate ASI Loader (dinput8.dll) can only be chained as {ASI_LOADER_NAME}, and another file has that name; move one of them away first"),
                        ));
                    }
                    ASI_LOADER_NAME.to_string()
                } else {
                    chain_slot(game_dir, kind)?
                };
                let backup = copy_into_backup(game_dir, &backup_rel, PROXY, Some(&mirror))?;
                let dst = game_dir.join(&chain_rel);
                if let Some(p) = dst.parent() {
                    std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
                }
                // create_new prevents a race from replacing a file that appeared after slot selection.
                copy_new(&proxy_path, &dst)?;
                if let Err(e) = std::fs::remove_file(&proxy_path) {
                    let _ = std::fs::remove_file(&dst);
                    return Err(io_at(&proxy_path, "moving the proxy from")(e));
                }
                steps.push(Step::Moved { from: proxy_path.clone(), to: dst });
                let line = chain_line(&chain_rel);
                let previous = match std::fs::read(&chain_path) {
                    Ok(b) => Some(b),
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
                    Err(e) => return Err(io_at(&chain_path, "reading")(e)),
                };
                steps.push(Step::Edited { target: chain_path, previous });
                crate::chain::add_chain_entry(game_dir, &line)?;
                if std::fs::metadata(game_dir.join(crate::paths::game::CHAIN)).map_err(io_at(game_dir, "reading chain.ini"))?.len() > MAX_CHAIN_BYTES {
                    return Err(Error::new(ErrorCode::Refused, "adding this loader would exceed the proxy's chain.ini size limit"));
                }
                replaced.push(ReplacedFile {
                    path: PROXY.into(),
                    sha256: sha,
                    size: class.size,
                    class: Some(if class.kind == ProxyKind::Emf { ReplacedClass::Emf } else { ReplacedClass::Unknown }),
                    action: ReplacedAction::Chained,
                    chain_path: Some(chain_rel),
                    backup: Some(backup),
                    engine: crate::engines::Engine::of_proxy(class.kind).map(|_| class.kind),
                });
            }
            _ => {
                // a Wine builtin copied into the folder: kept in the backup, never chained
                let backup = copy_into_backup(game_dir, &backup_rel, PROXY, Some(&mirror))?;
                replaced.push(ReplacedFile {
                    path: PROXY.into(),
                    sha256: class.sha256.clone().unwrap_or_default(),
                    size: class.size,
                    class: Some(ReplacedClass::File),
                    action: ReplacedAction::BackedUp,
                    chain_path: None,
                    backup: Some(backup),
                    engine: None,
                });
            }
        }
        // every other loader under its own proxy name keeps running next to Kiln: left in place, said once
        for slot in crate::engines::scan_proxies(game_dir).iter().filter(|s| !s.name.eq_ignore_ascii_case(PROXY)) {
            if let Some(e) = crate::engines::Engine::of_proxy(slot.class.kind) {
                progress.report(None, &format!("{} ({}) stays in place and keeps running next to Kiln", slot.name, e.name()));
            }
        }
        // 2. every other file, dinput8.dll last. Every path that existed before is recorded: identical bytes (and
        // config files, never touched) as `adopted`, other bytes backed up as `replaced`; uninstall keeps or restores
        // them, and removes only files that did not exist.
        let mut order: Vec<&RuntimeFile> = files.iter().filter(|f| f.path != PROXY).collect();
        order.extend(files.iter().filter(|f| f.path == PROXY));
        let total = order.len().max(1);
        for (i, f) in order.iter().enumerate() {
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "canceled"));
            }
            let target = game_dir.join(&f.path);
            let current = file_sha(&target);
            if let Some((h, size)) = &current {
                // ours = the previous install brought it (it was not there before); tracked = still its bytes
                let ours = old_files.contains_key(f.path.as_str());
                let tracked = old_files.get(f.path.as_str()).is_some_and(|o| o.sha256 == *h);
                let has_record = replaced.iter().any(|r| r.path == f.path);
                if f.path == PROXY {
                    // classified above
                } else if ours && !tracked && !f.config && *h != f.sha256 {
                    // one of our files, changed by hand: keep a copy of the edit, but it is no original
                    let b = copy_into_backup(game_dir, &backup_rel, &f.path, Some(&mirror))?;
                    progress.report(None, &format!("{} was changed since the last install; the changed file is in {b}", f.path));
                    changed_backed_up.push(f.path.clone());
                } else if !ours {
                    if !has_record && (f.config || *h == f.sha256) {
                        replaced.push(ReplacedFile {
                            path: f.path.clone(),
                            sha256: h.clone(),
                            size: Some(*size),
                            class: Some(ReplacedClass::File),
                            action: ReplacedAction::Adopted,
                            chain_path: None,
                            backup: None,
                            engine: None,
                        });
                    } else if !f.config && *h != f.sha256 {
                        let backup = copy_into_backup(game_dir, &backup_rel, &f.path, Some(&mirror))?;
                        replaced.push(ReplacedFile {
                            path: f.path.clone(),
                            sha256: h.clone(),
                            size: Some(*size),
                            class: Some(ReplacedClass::File),
                            action: ReplacedAction::Replaced,
                            chain_path: None,
                            backup: Some(backup),
                            engine: None,
                        });
                    }
                } else if *h != f.sha256 && !f.config
                    && let Some(r) = replaced.iter_mut().find(|r| r.path == f.path && r.action == ReplacedAction::Adopted && r.sha256 == *h)
                {
                    // an adopted file the upgrade changes: keep its original bytes for uninstall
                    r.backup = Some(copy_into_backup(game_dir, &backup_rel, &f.path, Some(&mirror))?);
                    r.action = ReplacedAction::Replaced;
                }
            }
            if f.config && current.is_some() {
                continue; // user-editable: installed only when missing
            }
            if current.as_ref().is_some_and(|(h, _)| *h == f.sha256) {
                continue; // already these bytes
            }
            if let Some(p) = target.parent() {
                std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
            }
            let previous = if current.is_some() {
                let hold = prev_dir.join(&f.path);
                if let Some(p) = hold.parent() {
                    std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
                }
                std::fs::rename(&target, &hold).map_err(io_at(&target, "moving aside"))?;
                Some(hold)
            } else {
                None
            };
            let src = new_dir.join(&f.path);
            if let Err(e) = std::fs::rename(&src, &target) {
                if let Some(p) = &previous {
                    let _ = std::fs::rename(p, &target);
                }
                return Err(io_at(&target, "installing")(e));
            }
            steps.push(Step::Placed { target, previous });
            progress.report(Some(0.5 + 0.4 * (i + 1) as f32 / total as f32), &format!("installed {}", f.path));
        }
        Ok(())
    })();
    if let Err(e) = outcome {
        return Err(rollback_error(steps, e, staging));
    }

    // All remaining mutations, including the receipt, are in the same rollback boundary.
    let finish = (|| {
        // 3. upgrade: files the new bundle no longer has go when they are unchanged
        let mut kept_obsolete = Vec::new();
        if let Some(o) = &old {
            let new_paths: BTreeSet<&str> = files.iter().map(|f| f.path.as_str()).collect();
            for f in o.files.iter().filter(|f| !new_paths.contains(f.path.as_str()) && !f.config) {
                let p = game_dir.join(&f.path);
                let original = replaced.iter().position(|r| r.path == f.path);
                match file_sha(&p) {
                    Some((h, _)) if h == f.sha256 => match original.map(|i| (i, replaced[i].action)) {
                        // it was there before the first install with these bytes: it stays the user's
                        Some((_, ReplacedAction::Adopted)) => {}
                        // the bundle no longer brings it: the original comes back now
                        Some((i, ReplacedAction::Replaced)) if replaced[i].backup.is_some() => {
                            let b = game_dir.join(replaced[i].backup.as_deref().expect("checked"));
                            if file_sha(&b).is_some_and(|(bh, _)| bh == replaced[i].sha256) {
                                steps.push(Step::Edited { target: p.clone(), previous: Some(std::fs::read(&p).map_err(io_at(&p, "reading"))?) });
                                std::fs::copy(&b, &p).map_err(io_at(&p, "restoring"))?;
                                replaced.remove(i);
                            } else {
                                kept_obsolete.push(f.path.clone());
                            }
                        }
                        _ => {
                            steps.push(Step::Edited { target: p.clone(), previous: Some(std::fs::read(&p).map_err(io_at(&p, "reading"))?) });
                            std::fs::remove_file(&p).map_err(io_at(&p, "removing"))?;
                        }
                    },
                    Some(_) => kept_obsolete.push(f.path.clone()),
                    None => {}
                }
            }
        }
        for d in standard_dirs(side) {
            let _ = std::fs::create_dir_all(game_dir.join(d));
        }
        // 4. the receipt, last
        let exe_sha = crate::hash::sha256_file(&game_dir.join(&install.exe)).ok().map(|(h, _)| h);
        let receipt = InstallReceipt {
            format: 1,
            install_id: install_id.clone(),
            installed: old.as_ref().map(|o| o.installed.clone()).unwrap_or_else(|| rfc3339(now)),
            updated: old.as_ref().map(|_| rfc3339(now)),
            installer: Some(options.installer.clone().unwrap_or_else(|| format!("kiln-manage {}", crate::VERSION))),
            runtime: ReceiptRuntime {
                version: manifest.version.clone(),
                sha256: Some(bundle_sha.to_string()),
                components: choice.components.clone(),
                deselected: Some(choice.deselected.clone()),
            },
            side,
            platform: Some(install.platform),
            game: Some(ReceiptGame { exe: Some(install.exe.clone()), sha256: exe_sha, steam_build: install.build_id.clone() }),
            files: files
                .iter()
                .map(|f| ReceiptFile { path: f.path.clone(), sha256: f.sha256.clone(), size: Some(f.size), component: Some(f.component.clone()), config: f.config })
                .collect(),
            replaced,
        };
        crate::util::write_json_atomic(&game_dir.join(crate::paths::game::RECEIPT), &receipt)?;
        write_record(dirs, install, &receipt);
        if !kept_obsolete.is_empty() {
            progress.report(Some(1.0), &format!("kept {} changed file(s) the new bundle no longer has: {}", kept_obsolete.len(), kept_obsolete.join(", ")));
        }
        progress.report(Some(1.0), &format!("installed Kiln {} ({} files)", manifest.version, files.len()));
        let backup = mirror.is_dir().then(|| mirror.to_string_lossy().into_owned());
        for warning in &warnings { progress.report(None, warning); }
        Ok((receipt, InstallReport { changed_backed_up, backup, restored_shroudforge, warnings }))
    })();
    match finish {
        Ok(r) => Ok(r),
        Err(e) => Err(rollback_error(steps, e, staging)),
    }
}

/// `<data>/installs/<installId>.json` (game::write_record): the install under its receipt id, with a copy of the
/// receipt. Best effort: the receipt in the game folder is the source of truth.
fn write_record(dirs: &DataDirs, install: &GameInstall, receipt: &InstallReceipt) {
    let mut gi = install.clone();
    gi.id = receipt.install_id.clone();
    let _ = crate::game::write_record(dirs, &gi, serde_json::to_value(receipt).ok());
}

fn remove_empty_parents(game_dir: &Path, file: &Path) {
    let kiln = game_dir.join(crate::paths::game::KILN);
    let mut d = file.parent();
    while let Some(dir) = d {
        if dir == game_dir || dir == kiln || !dir.starts_with(&kiln) {
            break;
        }
        if std::fs::remove_dir(dir).is_err() {
            break;
        }
        d = dir.parent();
    }
}

/// What `uninstall` left in place (changed files are never deleted).
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct UninstallReport {
    pub removed: u32,
    pub restored: Vec<String>,
    /// Files that existed before the install with the bundle's bytes (`adopted`): left in place.
    #[serde(default)]
    pub adopted: Vec<String>,
    /// Receipt files whose content changed: left in place.
    pub kept: Vec<String>,
    /// Fixed-folder profile copies removed or retained (with reasons) outside kiln/.
    #[serde(default)]
    pub placements: crate::engine_mods::PlacementCleanup,
    /// Current player files that conflict with restoration: original backup locations and next steps.
    #[serde(default)]
    pub warnings: Vec<String>,
}

/// Restores `replaced` originals byte-identically unless a newer player file occupies their path (kept with a
/// recovery warning). Removes unchanged receipt files and profile placements; `config` files and `kiln/` player
/// data (mods, configs, logs, backups) stay.
pub fn uninstall(dirs: &DataDirs, install: &GameInstall, progress: &mut dyn Progress) -> Result<()> {
    uninstall_report(dirs, install, progress).map(|_| ())
}

pub fn uninstall_report(dirs: &DataDirs, install: &GameInstall, progress: &mut dyn Progress) -> Result<UninstallReport> {
    let game_dir = install.path.as_path();
    refuse_busy(install)?;
    // unplace below edits engine folders that a client and a server installed in this folder share
    crate::profile::refuse_running(install)?;
    let receipt = read_receipt(game_dir)?
        .ok_or_else(|| Error::not_found(format!("Kiln is not installed in {} (no kiln/.install.json)", game_dir.display())))?;
    for p in receipt_paths(&receipt).chain(["kiln/.install.lock", "kiln/chain.ini", "kiln/backups"]) {
        refuse_symlinks(game_dir, p)?;
    }
    let _lock = InstallLock::take(game_dir)?;
    // The oldest record per path is the original (an upgrade may add newer ones). Check every restore source first:
    // nothing is removed when an original cannot come back.
    let mut originals: Vec<&ReplacedFile> = Vec::new();
    for r in &receipt.replaced {
        if !originals.iter().any(|o| o.path == r.path) {
            originals.push(r);
        }
    }
    // adopted originals were there before with the bundle's bytes: they stay where they are
    let adopted: BTreeSet<String> =
        originals.iter().filter(|r| r.action == ReplacedAction::Adopted).map(|r| r.path.clone()).collect();
    originals.retain(|r| r.action != ReplacedAction::Adopted);
    // the chained copy, the backup in the game folder, else that backup's second copy in the launcher's data folder
    let source = |r: &ReplacedFile| -> Option<PathBuf> {
        let ok = |p: PathBuf| file_sha(&p).is_some_and(|(h, _)| h == r.sha256).then_some(p);
        let mirrored = |b: &String| b.strip_prefix(&format!("{}/", crate::paths::game::BACKUPS))
            .map(|rest| dirs.backups().join("install").join(&receipt.install_id).join(rest));
        r.chain_path.as_ref().and_then(|c| ok(game_dir.join(c))).or_else(|| r.backup.as_ref().and_then(|b| ok(game_dir.join(b))))
            .or_else(|| r.backup.as_ref().and_then(mirrored).and_then(ok))
    };
    let in_place = |r: &ReplacedFile| file_sha(&game_dir.join(&r.path)).is_some_and(|(h, _)| h == r.sha256);
    // a ShroudForge helper an early switch-from-ShroudForge displaced: a courtesy restore, as on install
    let helper = |r: &ReplacedFile| r.class == Some(ReplacedClass::ShroudForge) && r.action == ReplacedAction::BackedUp
        && matches!(r.path.as_str(), "dbghelp.dll" | "winmm.dll");
    for r in &originals {
        if source(r).is_none() && !in_place(r) && !helper(r) {
            return Err(Error::new(ErrorCode::HashMismatch, format!(
                "cannot restore the original {}: neither {} nor {} holds it (sha256 {})",
                r.path,
                r.chain_path.as_deref().unwrap_or("-"),
                r.backup.as_deref().unwrap_or("-"),
                r.sha256
            )));
        }
    }
    let mut report = UninstallReport { placements: crate::engine_mods::unplace(game_dir)?, ..Default::default() };
    if let Some(warning) = report.placements.warning() { progress.report(None, &warning); }
    let total = receipt.files.len().max(1);
    for (i, f) in receipt.files.iter().enumerate() {
        if f.config {
            continue;
        }
        if adopted.contains(&f.path) {
            report.adopted.push(f.path.clone());
            continue;
        }
        let p = game_dir.join(&f.path);
        match file_sha(&p) {
            // Preflight accepts an original already at its path even when every backup is gone. It may also match
            // this bundle's file (an update brought those bytes back); never delete our only surviving restore source.
            Some((h, _)) if originals.iter().any(|r| r.path == f.path && r.sha256 == h && source(r).is_none()) => {}
            Some((h, _)) if h == f.sha256 => {
                std::fs::remove_file(&p).map_err(io_at(&p, "removing"))?;
                report.removed += 1;
                remove_empty_parents(game_dir, &p);
            }
            Some(_) => report.kept.push(f.path.clone()),
            None => {}
        }
        progress.report(Some(0.8 * (i + 1) as f32 / total as f32), &format!("removed {}", f.path));
    }
    for r in &receipt.replaced {
        if let Some(c) = &r.chain_path {
            let _ = crate::chain::remove_chain_entry(game_dir, &chain_line(c));
        }
    }
    for r in originals {
        let target = game_dir.join(&r.path);
        let Some(src) = source(r) else {
            // let through above: already the original, or a ShroudForge helper with no backup left anywhere
            if !in_place(r) {
                let warning = format!("Did not restore ShroudForge's {}: its backup {} is missing or changed, and the launcher's backups hold no copy either.",
                    r.path, r.backup.as_deref().unwrap_or("(none recorded)"));
                progress.report(None, &warning);
                report.warnings.push(warning);
            }
            continue;
        };
        if occupied(&target)? {
            if file_sha(&target).is_some_and(|(h, _)| h == r.sha256) {
                // Already the original (e.g. a recovered legacy SF helper): do not replace the same path again.
                continue;
            }
            if r.path == PROXY && crate::pe::classify_proxy(&target).is_ok_and(|c| c.kind == ProxyKind::Kiln) {
                // Kiln's own proxy, changed since the install (a hand-deployed build): it goes to kiln/backups and the
                // original comes back. Left in place, the game would load a Kiln proxy without its runtime while the
                // original loader is no longer chained and the receipt is gone.
                let aside = game_dir.join(crate::paths::game::BACKUPS).join(format!("uninstall-{}-{}", ts_compact(now_unix()), rand_suffix()?)).join(&r.path);
                if let Some(p) = aside.parent() {
                    std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
                }
                std::fs::rename(&target, &aside).map_err(io_at(&target, "moving aside"))?;
                report.kept.retain(|k| k != &r.path);
                let warning = format!("Moved the changed Kiln {} to {} and put the original back.", r.path, aside.display());
                progress.report(None, &warning);
                report.warnings.push(warning);
            } else {
                // another loader's file the player put there since: theirs to keep
                if !report.kept.contains(&r.path) { report.kept.push(r.path.clone()); }
                let warning = format!("Kept current {} because it changed outside Kiln. The original is preserved at {}; with the game closed, compare the files and restore that backup manually if wanted.", r.path, src.display());
                progress.report(None, &warning);
                report.warnings.push(warning);
                continue;
            }
        }
        if let Some(p) = target.parent() {
            std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
        }
        if r.chain_path.as_ref().is_some_and(|c| game_dir.join(c) == src) {
            std::fs::rename(&src, &target).map_err(io_at(&target, "restoring"))?;
        } else {
            std::fs::copy(&src, &target).map_err(io_at(&target, "restoring"))?;
        }
        if file_sha(&target).map(|(h, _)| h) != Some(r.sha256.clone()) {
            return Err(Error::new(ErrorCode::HashMismatch, format!("restored {} does not match its receipt sha256", r.path)));
        }
        report.restored.push(r.path.clone());
    }
    let chain_dir = game_dir.join(crate::paths::game::CHAIN_DIR);
    let _ = std::fs::remove_dir(&chain_dir);
    let receipt_path = game_dir.join(crate::paths::game::RECEIPT);
    std::fs::remove_file(&receipt_path).map_err(io_at(&receipt_path, "removing"))?;
    if let Ok(Some(rec)) = crate::game::record(dirs, &receipt.install_id) {
        let _ = crate::game::write_record(dirs, &rec.install, None);
    }
    progress.report(Some(1.0), &format!(
        "uninstalled: {} file(s) removed, {} restored, {} adopted file(s) left as they were, {} changed file(s) kept",
        report.removed,
        report.restored.len(),
        report.adopted.len(),
        report.kept.len()
    ));
    Ok(report)
}

/// Installs an older bundle from `<data>/runtime/` by version, keeping the player's parts (resolve_components:
/// the installed ones the bundle has, its defaults unless turned off; components it lacks are simply left out).
pub fn rollback(dirs: &DataDirs, install: &GameInstall, version: &str, progress: &mut dyn Progress) -> Result<InstallReceipt> {
    let b = crate::bundle::find(dirs, version)?;
    let options = InstallOptions { components: None, switch_from_shroudforge: false, installer: None };
    self::install(dirs, install, &b.file, &options, progress)
}
