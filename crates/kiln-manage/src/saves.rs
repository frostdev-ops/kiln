//! Save slots of a client (Steam userdata/<id>/1203620/remote/*.ksd; under Proton inside the prefix's Steam
//! folder), automatic backups (before a launch whose mod-set fingerprint changed, before a modpack is applied; keep
//! 10 per game) in `<data>/profiles/<pid>/save-backups/` or `<data>/backups/saves/<gameId>/` without a profile, and
//! restore (refused while the game runs). `editor` carries the Save Editor's commands, Linux-aware.
//!
//! Where a client's saves are (checked on the Steam build of 2026-11): every Steam root's
//! `userdata/<steamid>/1203620/remote/` (`character<N>.ksd`, `world<N>.ksd`, `global.ksd`, `user_settings.json`; the
//! game's own rolling copies in `remote/backup/` are not backed up), plus the local settings in
//! `Saved Games/keen games/Enshrouded/` (`<pfx>/drive_c/users/steamuser/...` under Proton, `%USERPROFILE%` on
//! Windows). A backup is `<backup dir>/<id>/` with one sub folder per source (`steam-<steamid>/`, `local/`) and
//! `backup.json`; restore puts every file back byte-identically.

use crate::game::{GameInstall, GameKind};
use crate::hash;
use crate::paths::DataDirs;
use crate::steam::{self, SteamRoot};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

pub const KEEP: usize = 10;
pub const BACKUP_JSON: &str = "backup.json";

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SlotKind {
    World,
    Character,
    Other,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveSlot {
    pub kind: SlotKind,
    pub file: String,
    pub path: String,
    pub modified: String,
    pub size: u64,
    pub steam_id: Option<String>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum BackupReason {
    Launch,
    Modpack,
    Manual,
    Restore,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveBackup {
    pub id: String,
    pub created: String,
    pub reason: BackupReason,
    pub profile_id: Option<String>,
    pub fingerprint: Option<String>,
    pub note: Option<String>,
    pub files: u32,
    pub bytes: u64,
}

/// One folder holding saves: `label` names its sub folder in a backup (`steam-<steamid>`, `local`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SaveDir {
    pub label: String,
    pub path: PathBuf,
    pub steam_id: Option<String>,
    /// Walk sub folders (the local settings folder) or only the top level (Steam's remote/, whose backup/ is the
    /// game's own).
    pub recursive: bool,
}

/// One file of a backup (`backup.json` `entries[]`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupEntry {
    /// The SaveDir label it came from.
    pub dir: String,
    /// '/' separated, relative to the SaveDir (and to `<backup>/<dir>/`).
    pub rel: String,
    /// The host path it was copied from (restore target when no SaveDir has the label any more).
    pub source: String,
    pub size: u64,
    pub sha256: String,
}

/// `<backup>/backup.json`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BackupManifest {
    pub format: u32,
    pub game_id: String,
    #[serde(flatten)]
    pub backup: SaveBackup,
    pub entries: Vec<BackupEntry>,
}

// ---- time (UTC) ----

fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = (if z >= 0 { z } else { z - 146_096 }) / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32;
    (yoe + era * 400 + i64::from(m <= 2), m, d)
}

fn utc(t: SystemTime) -> (i64, u32, u32, u32, u32, u32) {
    let secs = match t.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_secs() as i64,
        Err(e) => -(e.duration().as_secs() as i64),
    };
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    let (y, mo, d) = civil_from_days(days);
    (y, mo, d, (rem / 3600) as u32, (rem / 60 % 60) as u32, (rem % 60) as u32)
}

/// `2026-11-04T12:00:00Z`
pub(crate) fn rfc3339(t: SystemTime) -> String {
    let (y, mo, d, h, mi, s) = utc(t);
    format!("{y:04}-{mo:02}-{d:02}T{h:02}:{mi:02}:{s:02}Z")
}

/// `20261104T120000Z` (backup folder names).
pub(crate) fn stamp(t: SystemTime) -> String {
    let (y, mo, d, h, mi, s) = utc(t);
    format!("{y:04}{mo:02}{d:02}T{h:02}{mi:02}{s:02}Z")
}

// ---- where saves are ----

/// The slot kind a file name suggests: `character*` -> Character, `world*` / an 8-hex-digit world id -> World.
pub fn slot_kind(file: &str) -> SlotKind {
    let lower = file.to_ascii_lowercase();
    let stem = lower.split('.').next().unwrap_or("");
    if stem.starts_with("character") {
        SlotKind::Character
    } else if stem.starts_with("world") || (stem.len() == 8 && stem.bytes().all(|b| b.is_ascii_hexdigit())) {
        SlotKind::World
    } else {
        SlotKind::Other
    }
}

/// The Windows user's Saved Games folder (its known-folder location, which may have been moved off `%USERPROFILE%`).
fn windows_saved_games() -> Option<PathBuf> {
    #[cfg(windows)]
    {
        crate::steam::win::saved_games()
    }
    #[cfg(not(windows))]
    {
        std::env::var_os("USERPROFILE").map(|h| Path::new(&h).join("Saved Games"))
    }
}

fn local_settings_dirs(install: &GameInstall) -> Vec<PathBuf> {
    let mut v = Vec::new();
    if cfg!(windows) || install.platform == crate::game::Platform::Windows {
        if let Some(base) = windows_saved_games() {
            v.push(base.join("keen games").join("Enshrouded"));
            v.push(base.join("Enshrouded"));
        }
    } else if let Some(pfx) = &install.prefix {
        let base = pfx.join("drive_c/users/steamuser/Saved Games");
        v.push(base.join("keen games/Enshrouded"));
        v.push(base.join("Enshrouded"));
    }
    v
}

/// The folders holding this client's saves, given the Steam roots (servers keep theirs in the server folder: none).
pub fn save_dirs_in(install: &GameInstall, roots: &[SteamRoot]) -> Vec<SaveDir> {
    let mut out: Vec<SaveDir> = Vec::new();
    if install.kind != GameKind::Client {
        return out;
    }
    let preferred: Vec<SteamRoot> = match install.library.as_deref().and_then(|l| steam::root_of_library(roots, l)) {
        Some(r) => vec![r],
        None => roots.to_vec(),
    };
    for r in &preferred {
        let mut users: Vec<PathBuf> = std::fs::read_dir(r.path.join("userdata")).into_iter().flatten().flatten().map(|e| e.path()).collect();
        users.sort();
        for u in users {
            let Some(id) = u.file_name().and_then(|n| n.to_str()).filter(|n| n.bytes().all(|b| b.is_ascii_digit())) else {
                continue;
            };
            let remote = u.join(install.app_id.to_string()).join("remote");
            if remote.is_dir() && !out.iter().any(|d| d.steam_id.as_deref() == Some(id)) {
                out.push(SaveDir { label: format!("steam-{id}"), path: remote, steam_id: Some(id.to_string()), recursive: false });
            }
        }
    }
    for (i, d) in local_settings_dirs(install).into_iter().enumerate() {
        if d.is_dir() {
            out.push(SaveDir { label: if i == 0 { "local".into() } else { format!("local{}", i + 1) }, path: d, steam_id: None, recursive: true });
        }
    }
    let characters = install.path.join("kiln").join("data").join("characters");
    if characters.is_dir() {
        out.push(SaveDir { label: "kiln-characters".into(), path: characters, steam_id: None, recursive: false });
    }
    out
}

pub fn save_dirs(install: &GameInstall) -> Result<Vec<SaveDir>> {
    Ok(save_dirs_in(install, &steam::roots().unwrap_or_default()))
}

/// (relative path, absolute path) of every file in a SaveDir.
fn files_of(d: &SaveDir) -> Result<Vec<(String, PathBuf)>> {
    let mut out = Vec::new();
    walk(&d.path, "", d.recursive, &mut out)?;
    out.sort();
    Ok(out)
}

fn walk(dir: &Path, prefix: &str, recursive: bool, out: &mut Vec<(String, PathBuf)>) -> Result<()> {
    let rd = match std::fs::read_dir(dir) {
        Ok(rd) => rd,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e.into()),
    };
    for e in rd {
        let e = e?;
        let name = e.file_name().to_string_lossy().into_owned();
        let rel = if prefix.is_empty() { name.clone() } else { format!("{prefix}/{name}") };
        let ft = std::fs::symlink_metadata(e.path())?.file_type();
        if ft.is_file() {
            if !name.ends_with(".kiln-tmp") {
                out.push((rel, e.path()));
            }
        } else if ft.is_dir() && recursive {
            walk(&e.path(), &rel, true, out)?;
        }
    }
    Ok(())
}

pub fn list_in(dirs: &[SaveDir]) -> Result<Vec<SaveSlot>> {
    let mut out = Vec::new();
    for d in dirs {
        for (rel, p) in files_of(d)? {
            let md = std::fs::metadata(&p)?;
            let file = rel.rsplit('/').next().unwrap_or(&rel).to_string();
            out.push(SaveSlot {
                kind: slot_kind(&file),
                file,
                path: p.display().to_string(),
                modified: rfc3339(md.modified().unwrap_or(UNIX_EPOCH)),
                size: md.len(),
                steam_id: d.steam_id.clone(),
            });
        }
    }
    Ok(out)
}

pub fn list(install: &GameInstall) -> Result<Vec<SaveSlot>> {
    list_in(&save_dirs(install)?)
}

// ---- backups ----

/// The profile of this data folder active in the game: another data folder's profile of the same id is not
/// (`profile::game_profile_owner`), so its save backups are not filed under this folder's profile.
fn active_profile(dirs: &DataDirs, install: &GameInstall) -> Option<String> {
    match crate::profile::game_profile_owner(dirs, install) {
        Ok(crate::profile::GameProfileOwner::Here { id }) => Some(id),
        _ => None,
    }
}

/// Where a new backup of this install goes: the active profile's `save-backups/`, else `backups/saves/<gameId>/`.
pub fn backup_root(dirs: &DataDirs, install: &GameInstall, profile: Option<&str>) -> PathBuf {
    match profile {
        Some(pid) => dirs.profile(pid).join("save-backups"),
        None => dirs.backups().join("saves").join(&install.id),
    }
}

/// Every backup folder of this install: `(folder, manifest)`, newest first.
fn all_backups(dirs: &DataDirs, install: &GameInstall) -> Vec<(PathBuf, BackupManifest)> {
    let mut roots = vec![dirs.backups().join("saves").join(&install.id)];
    for e in std::fs::read_dir(dirs.profiles()).into_iter().flatten().flatten() {
        roots.push(e.path().join("save-backups"));
    }
    let mut out = Vec::new();
    for r in roots {
        for e in std::fs::read_dir(&r).into_iter().flatten().flatten() {
            let Ok(text) = std::fs::read_to_string(e.path().join(BACKUP_JSON)) else { continue };
            let Ok(m) = serde_json::from_str::<BackupManifest>(&text) else { continue };
            if m.game_id == install.id {
                out.push((e.path(), m));
            }
        }
    }
    out.sort_by_key(|b| std::cmp::Reverse(order_key(&b.1.backup)));
    out
}

/// Newest first: creation time, then the clash suffix (`<stamp>`, `<stamp>-2`, `<stamp>-3`, ...).
fn order_key(b: &SaveBackup) -> (String, String, u32) {
    let (base, n) = match b.id.rsplit_once('-') {
        Some((base, n)) if n.bytes().all(|c| c.is_ascii_digit()) => (base.to_string(), n.parse().unwrap_or(1)),
        _ => (b.id.clone(), 1),
    };
    (b.created.clone(), base, n)
}

pub fn backups(dirs: &DataDirs, install: &GameInstall) -> Result<Vec<SaveBackup>> {
    Ok(all_backups(dirs, install).into_iter().map(|(_, m)| m.backup).collect())
}

fn copy_verified(src: &Path, dst: &Path) -> Result<(u64, String)> {
    if let Some(p) = dst.parent() {
        std::fs::create_dir_all(p)?;
    }
    let tmp = dst.with_file_name(format!(".{}.kiln-tmp", dst.file_name().unwrap_or_default().to_string_lossy()));
    std::fs::copy(src, &tmp)?;
    let (want, size) = hash::sha256_file(src)?;
    let (got, _) = hash::sha256_file(&tmp)?;
    if want != got {
        let _ = std::fs::remove_file(&tmp);
        return Err(Error::new(ErrorCode::HashMismatch, format!("{} changed while it was copied", src.display())));
    }
    std::fs::rename(&tmp, dst).inspect_err(|_| {
        let _ = std::fs::remove_file(&tmp);
    })?;
    Ok((size, want))
}

fn write_json<T: Serialize>(path: &Path, v: &T) -> Result<()> {
    let tmp = path.with_extension("json.tmp");
    std::fs::write(&tmp, serde_json::to_string_pretty(v)? + "\n")?;
    std::fs::rename(&tmp, path)?;
    Ok(())
}

/// Deletes automatic backups beyond KEEP (oldest first); manual ones and `protect` stay.
fn prune(dirs: &DataDirs, install: &GameInstall, protect: Option<&str>) -> Result<()> {
    let auto: Vec<(PathBuf, BackupManifest)> =
        all_backups(dirs, install).into_iter().filter(|(_, m)| m.backup.reason != BackupReason::Manual).collect();
    for (dir, m) in auto.into_iter().skip(KEEP) {
        if Some(m.backup.id.as_str()) == protect {
            continue;
        }
        std::fs::remove_dir_all(&dir)?;
    }
    Ok(())
}

/// `backup` from explicit save folders, with the profile and mod-set fingerprint recorded (launch backups).
pub fn backup_from(
    dirs: &DataDirs, install: &GameInstall, saves: &[SaveDir], reason: BackupReason, note: Option<&str>,
    fingerprint: Option<&str>,
) -> Result<SaveBackup> {
    let b = backup_unpruned(dirs, install, saves, reason, note, fingerprint)?;
    prune(dirs, install, None)?;
    Ok(b)
}

fn backup_unpruned(
    dirs: &DataDirs, install: &GameInstall, saves: &[SaveDir], reason: BackupReason, note: Option<&str>,
    fingerprint: Option<&str>,
) -> Result<SaveBackup> {
    let now = SystemTime::now();
    let profile = active_profile(dirs, install);
    let root = backup_root(dirs, install, profile.as_deref());
    std::fs::create_dir_all(&root)?;
    // `<stamp>`, then `<stamp>-2`, `-3`, ... above every id of that second (also pruned ones' successors), so the
    // order of ids is the order of creation
    let base = stamp(now);
    let mut taken: Vec<String> = all_backups(dirs, install).into_iter().map(|(_, m)| m.backup.id).collect();
    taken.extend(std::fs::read_dir(&root).into_iter().flatten().flatten().map(|e| e.file_name().to_string_lossy().into_owned()));
    let max_n = taken
        .iter()
        .filter_map(|t| if *t == base { Some(1) } else { t.strip_prefix(&format!("{base}-"))?.parse::<u32>().ok() })
        .max();
    let id = match max_n {
        None => base.clone(),
        Some(n) => format!("{base}-{}", n + 1),
    };
    let partial = root.join(format!(".{id}.partial"));
    let _ = std::fs::remove_dir_all(&partial);
    std::fs::create_dir_all(&partial)?;
    let mut entries = Vec::new();
    let mut run = || -> Result<()> {
        for d in saves {
            for (rel, src) in files_of(d)? {
                let (size, sha256) = copy_verified(&src, &partial.join(&d.label).join(&rel))?;
                entries.push(BackupEntry { dir: d.label.clone(), rel, source: src.display().to_string(), size, sha256 });
            }
        }
        Ok(())
    };
    if let Err(e) = run() {
        let _ = std::fs::remove_dir_all(&partial);
        return Err(e);
    }
    let backup = SaveBackup {
        id: id.clone(),
        created: rfc3339(now),
        reason,
        profile_id: profile,
        fingerprint: fingerprint.map(str::to_string),
        note: note.map(str::to_string),
        files: entries.len() as u32,
        bytes: entries.iter().map(|e| e.size).sum(),
    };
    let m = BackupManifest { format: 1, game_id: install.id.clone(), backup: backup.clone(), entries };
    write_json(&partial.join(BACKUP_JSON), &m)?;
    std::fs::rename(&partial, root.join(&id))?;
    Ok(backup)
}

/// Copies every save slot; prunes to KEEP (manual backups are never pruned).
pub fn backup(dirs: &DataDirs, install: &GameInstall, reason: BackupReason, note: Option<&str>) -> Result<SaveBackup> {
    backup_from(dirs, install, &save_dirs(install)?, reason, note, None)
}

/// `backup` with the launch's mod-set fingerprint (kms1) recorded.
pub fn backup_with_fingerprint(
    dirs: &DataDirs, install: &GameInstall, reason: BackupReason, note: Option<&str>, fingerprint: Option<&str>,
) -> Result<SaveBackup> {
    backup_from(dirs, install, &save_dirs(install)?, reason, note, fingerprint)
}

fn find_backup(dirs: &DataDirs, install: &GameInstall, id: &str) -> Result<(PathBuf, BackupManifest)> {
    all_backups(dirs, install)
        .into_iter()
        .find(|(_, m)| m.backup.id == id)
        .ok_or_else(|| Error::not_found(format!("no save backup {id} for {}", install.id)))
}

/// `restore` from explicit save folders (the running check stays).
pub fn restore_to(dirs: &DataDirs, install: &GameInstall, saves: &[SaveDir], backup_id: &str) -> Result<()> {
    if let Ok(Some(p)) = crate::proc::find_game(install) {
        return Err(Error::new(ErrorCode::GameRunning, "quit the game before restoring saves").with_detail(serde_json::json!({"pid": p.pid})));
    }
    let (dir, m) = find_backup(dirs, install, backup_id)?;
    // check the backup before touching anything
    for e in &m.entries {
        if e.rel.split('/').any(|c| c == ".." || c.is_empty()) || e.dir.contains(['/', '\\']) || e.dir == ".." {
            return Err(Error::new(ErrorCode::Schema, format!("backup {backup_id}: bad entry {}/{}", e.dir, e.rel)));
        }
        let (sha, _) = hash::sha256_file(&dir.join(&e.dir).join(&e.rel))?;
        if sha != e.sha256 {
            return Err(Error::new(ErrorCode::HashMismatch, format!("backup {backup_id}: {}/{} is damaged", e.dir, e.rel)));
        }
    }
    let before = backup_unpruned(dirs, install, saves, BackupReason::Restore, Some(&format!("before restoring {backup_id}")), None)?;
    for e in &m.entries {
        let target = match saves.iter().find(|d| d.label == e.dir) {
            Some(d) => e.rel.split('/').fold(d.path.clone(), |p, c| p.join(c)),
            None => PathBuf::from(&e.source),
        };
        let (_, sha) = copy_verified(&dir.join(&e.dir).join(&e.rel), &target)?;
        if sha != e.sha256 {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: restored copy differs", target.display())));
        }
    }
    prune(dirs, install, Some(&before.id))?;
    Ok(())
}

/// Refuses (GameRunning) while the game runs; backs up the current saves first (reason restore).
pub fn restore(dirs: &DataDirs, install: &GameInstall, backup_id: &str) -> Result<()> {
    restore_to(dirs, install, &save_dirs(install)?, backup_id)
}

pub fn delete(dirs: &DataDirs, install: &GameInstall, backup_id: &str) -> Result<()> {
    let (dir, _) = find_backup(dirs, install, backup_id)?;
    std::fs::remove_dir_all(dir)?;
    Ok(())
}

/// The Save Editor's Tauri commands (upstream src-tauri/src/main.rs), ported.
pub mod editor {
    use crate::{Error, ErrorCode, Result};
    use serde::{Deserialize, Serialize};
    use std::path::{Path, PathBuf};
    use std::time::UNIX_EPOCH;

    #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
    #[serde(rename_all = "camelCase")]
    pub struct EditorSaveFile {
        pub path: String,
        pub file: String,
        /// ms since the epoch
        pub modified: f64,
    }

    #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
    #[serde(rename_all = "camelCase")]
    pub struct EditorFileInfo {
        pub modified: f64,
    }

    #[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
    #[serde(rename_all = "camelCase")]
    pub struct EditorBackup {
        /// "game" (remote/backup) | "folder" (the editor's backups)
        pub kind: String,
        pub label: String,
        pub path: String,
        pub modified: f64,
    }

    fn modified_ms(p: &Path) -> f64 {
        std::fs::metadata(p)
            .and_then(|m| m.modified())
            .ok()
            .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
            .map(|d| d.as_millis() as f64)
            .unwrap_or(0.0)
    }

    fn is_save(path: &str) -> Result<PathBuf> {
        let p = PathBuf::from(path);
        match p.extension().and_then(|e| e.to_str()) {
            Some(e) if e.eq_ignore_ascii_case("ksd") => Ok(p),
            _ => Err(Error::invalid("only .ksd files can be read or written")),
        }
    }

    fn is_character_file(name: &str) -> bool {
        name.len() > 13
            && name.starts_with("character")
            && name.ends_with(".ksd")
            && name[9..name.len() - 4].bytes().all(|c| c.is_ascii_digit())
    }

    /// `characterN.ksd` in every Steam user's `1203620/remote/` (all Steam roots: native, Flatpak, Snap, Windows).
    pub fn list_saves_in(roots: &[crate::steam::SteamRoot]) -> Vec<EditorSaveFile> {
        let mut out = Vec::new();
        for r in roots {
            let mut users: Vec<PathBuf> = std::fs::read_dir(r.path.join("userdata")).into_iter().flatten().flatten().map(|e| e.path()).collect();
            users.sort();
            for u in users {
                let dir = u.join(crate::CLIENT_APP_ID.to_string()).join("remote");
                let mut files: Vec<PathBuf> = std::fs::read_dir(&dir).into_iter().flatten().flatten().map(|e| e.path()).collect();
                files.sort();
                for f in files {
                    let name = f.file_name().unwrap_or_default().to_string_lossy().into_owned();
                    if is_character_file(&name) {
                        out.push(EditorSaveFile { path: f.display().to_string(), file: name, modified: modified_ms(&f) });
                    }
                }
            }
        }
        out
    }

    pub fn list_saves() -> Result<Vec<EditorSaveFile>> {
        Ok(list_saves_in(&crate::steam::roots().unwrap_or_default()))
    }

    pub fn file_info(path: &str) -> Result<EditorFileInfo> {
        Ok(EditorFileInfo { modified: modified_ms(Path::new(path)) })
    }

    /// Only `.ksd` files.
    pub fn read_file(path: &str) -> Result<Vec<u8>> {
        Ok(std::fs::read(is_save(path)?)?)
    }

    #[cfg(feature = "saves")]
    pub fn zstd_decompress(data: &[u8]) -> Result<Vec<u8>> {
        zstd::stream::decode_all(data).map_err(|e| Error::new(ErrorCode::Parse, format!("zstd: {e}")))
    }

    #[cfg(feature = "saves")]
    pub fn zstd_compress(data: &[u8]) -> Result<Vec<u8>> {
        zstd::bulk::compress(data, 19).map_err(|e| Error::new(ErrorCode::Internal, format!("zstd: {e}")))
    }

    #[cfg(not(feature = "saves"))]
    pub fn zstd_decompress(data: &[u8]) -> Result<Vec<u8>> {
        let _ = data;
        Err(Error::new(ErrorCode::Unsupported, "zstd needs kiln-manage's \"saves\" feature"))
    }

    #[cfg(not(feature = "saves"))]
    pub fn zstd_compress(data: &[u8]) -> Result<Vec<u8>> {
        let _ = data;
        Err(Error::new(ErrorCode::Unsupported, "zstd needs kiln-manage's \"saves\" feature"))
    }

    /// Any `enshrouded.exe` process (the editor does not know which install a save belongs to).
    pub fn game_running() -> Result<bool> {
        Ok(crate::proc::list()?.iter().any(|p| {
            p.name.eq_ignore_ascii_case("enshrouded.exe")
                || p.exe.as_deref().and_then(|e| e.file_name()).is_some_and(|n| n.eq_ignore_ascii_case("enshrouded.exe"))
        }))
    }

    /// The editor's backups: `<data>/backups/editor` (Windows: `%USERPROFILE%\Documents\Enshrouded-Backups\editor`, as
    /// the Save Editor keeps them).
    pub fn backups_dir() -> Result<PathBuf> {
        if cfg!(windows)
            && let Some(home) = std::env::var_os("USERPROFILE")
        {
            return Ok(Path::new(&home).join("Documents").join("Enshrouded-Backups").join("editor"));
        }
        Ok(crate::paths::DataDirs::locate()?.backups().join("editor"))
    }

    fn backups_home() -> Result<PathBuf> {
        let r = backups_dir()?;
        Ok(r.parent().map(Path::to_path_buf).unwrap_or(r))
    }

    fn copy_dir(src: &Path, dst: &Path) -> std::io::Result<()> {
        std::fs::create_dir_all(dst)?;
        for e in std::fs::read_dir(src)?.flatten() {
            let t = dst.join(e.file_name());
            if e.path().is_dir() { copy_dir(&e.path(), &t)?; } else { std::fs::copy(e.path(), t)?; }
        }
        Ok(())
    }

    fn make_backup(file: &Path) -> Result<PathBuf> {
        let secs = std::time::SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0);
        let base = backups_dir()?;
        let mut dir = base.join(format!("tauri-{secs}"));
        let mut n = 2;
        while dir.exists() {
            dir = base.join(format!("tauri-{secs}-{n}"));
            n += 1;
        }
        std::fs::create_dir_all(&dir)?;
        let name = file.file_name().ok_or_else(|| Error::invalid(format!("{}: no file name", file.display())))?;
        std::fs::copy(file, dir.join(name))?;
        if let Some(parent) = file.parent() {
            let b = parent.join("backup");
            if b.is_dir() {
                copy_dir(&b, &dir.join("backup"))?;
            }
        }
        Ok(dir)
    }

    pub fn backup_file(path: &str) -> Result<String> {
        make_backup(&is_save(path)?).map(|d| d.display().to_string())
    }

    fn running_refusal(what: &str) -> Error {
        Error::new(ErrorCode::GameRunning, format!("quit Enshrouded before {what}"))
    }

    /// Backs up first; refuses while the game runs.
    pub fn write_save(path: &str, data: &[u8]) -> Result<()> {
        if game_running()? {
            return Err(running_refusal("saving"));
        }
        let p = is_save(path)?;
        if data.len() < 0x30 || &data[..4] != b"SV2C" {
            return Err(Error::invalid("not a save (SV2C header missing)"));
        }
        if p.is_file() {
            make_backup(&p)?;
        }
        let tmp = p.with_extension("ksd.kiln-tmp");
        std::fs::write(&tmp, data)?;
        std::fs::rename(&tmp, &p)?;
        Ok(())
    }

    fn collect(dir: &Path, home: &Path, base: &str, depth: u32, out: &mut Vec<EditorBackup>) {
        let Ok(rd) = std::fs::read_dir(dir) else { return };
        for e in rd.flatten() {
            let p = e.path();
            if p.is_dir() {
                if depth > 0 {
                    collect(&p, home, base, depth - 1, out);
                }
            } else if e.file_name().to_string_lossy() == base {
                let label = p.parent().and_then(|d| d.strip_prefix(home).ok()).map(|r| r.display().to_string()).unwrap_or_default();
                out.push(EditorBackup { kind: "folder".into(), label, path: p.display().to_string(), modified: modified_ms(&p) });
            }
        }
    }

    /// The game's own rolling copies (`remote/backup/<file>.backup-N`) and the editor's / launcher's backups.
    pub fn list_backups(target: &str) -> Result<Vec<EditorBackup>> {
        let t = PathBuf::from(target);
        let base = t.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let mut out = Vec::new();
        if let Some(dir) = t.parent() {
            for e in std::fs::read_dir(dir.join("backup")).into_iter().flatten().flatten() {
                let n = e.file_name().to_string_lossy().into_owned();
                if n.starts_with(&format!("{base}.backup-")) {
                    out.push(EditorBackup { kind: "game".into(), label: n, path: e.path().display().to_string(), modified: modified_ms(&e.path()) });
                }
            }
        }
        let home = backups_home()?;
        collect(&home, &home, &base, 3, &mut out);
        out.sort_by(|a, b| b.modified.partial_cmp(&a.modified).unwrap_or(std::cmp::Ordering::Equal));
        Ok(out)
    }

    pub fn restore_backup(src: &str, target: &str) -> Result<()> {
        if game_running()? {
            return Err(running_refusal("restoring"));
        }
        let t = is_save(target)?;
        let s = PathBuf::from(src);
        let game_backups = t.parent().map(|d| d.join("backup")).unwrap_or_default();
        if !(s.starts_with(backups_home()?) || s.starts_with(&game_backups)) || !s.is_file() {
            return Err(Error::not_found("backup not found"));
        }
        let data = std::fs::read(&s)?;
        if data.len() < 4 || &data[..4] != b"SV2C" {
            return Err(Error::invalid("the backup is not a save (SV2C header missing)"));
        }
        if t.is_file() {
            make_backup(&t)?;
        }
        std::fs::write(t, data)?;
        Ok(())
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn character_names() {
            assert!(is_character_file("character0.ksd"));
            assert!(is_character_file("character12.ksd"));
            assert!(!is_character_file("characters.ksd"));
            assert!(!is_character_file("character.ksd"));
            assert!(!is_character_file("world0.ksd"));
            assert!(is_save("x/a.KSD").is_ok());
            assert!(is_save("x/a.json").is_err());
        }

        #[cfg(feature = "saves")]
        #[test]
        fn zstd_round_trip() {
            let data = b"SV2C".repeat(100);
            let z = zstd_compress(&data).unwrap();
            assert!(z.len() < data.len());
            assert_eq!(zstd_decompress(&z).unwrap(), data);
            assert!(zstd_decompress(b"not zstd").is_err());
        }

        #[cfg(not(feature = "saves"))]
        #[test]
        fn zstd_needs_the_feature() {
            assert_eq!(zstd_compress(b"x").unwrap_err().code, ErrorCode::Unsupported);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_and_times() {
        assert_eq!(slot_kind("character0.ksd"), SlotKind::Character);
        assert_eq!(slot_kind("world2.ksd"), SlotKind::World);
        assert_eq!(slot_kind("3ad85aea"), SlotKind::World);
        assert_eq!(slot_kind("3ad85aea_info"), SlotKind::Other);
        assert_eq!(slot_kind("global.ksd"), SlotKind::Other);
        assert_eq!(slot_kind("user_settings.json"), SlotKind::Other);
        let t = UNIX_EPOCH + std::time::Duration::from_secs(1_793_756_357);
        assert_eq!(rfc3339(t), "2026-11-04T01:39:17Z");
        assert_eq!(stamp(t), "20261104T013917Z");
    }

    /// Windows: the Saved Games known folder from the registry (expanded, absolute), and both of the game's folders in
    /// it for a native client.
    #[cfg(windows)]
    #[test]
    fn windows_saved_games_known_folder() {
        let base = windows_saved_games().expect("Saved Games folder");
        assert!(base.is_absolute() && !base.to_string_lossy().contains('%'), "{}", base.display());
        if let Some(home) = std::env::var_os("USERPROFILE") {
            // the default location unless the user moved it
            eprintln!("Saved Games: {} (USERPROFILE {})", base.display(), Path::new(&home).display());
        }
        let g = GameInstall {
            id: "x".into(),
            kind: GameKind::Client,
            path: PathBuf::from(r"C:\Games\Enshrouded"),
            exe: "enshrouded.exe".into(),
            app_id: crate::CLIENT_APP_ID,
            library: None,
            platform: crate::game::Platform::Windows,
            prefix: None,
            build_id: None,
            exe_sha256: None,
            steam_flavor: None,
            path_mode: crate::game::PathMode::Native,
            source: "manual".into(),
        };
        assert_eq!(local_settings_dirs(&g), vec![base.join("keen games").join("Enshrouded"), base.join("Enshrouded")]);
    }
}
