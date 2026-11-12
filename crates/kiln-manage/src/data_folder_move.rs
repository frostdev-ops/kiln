//! Moving the launcher's data folder, using another one, starting fresh, and finishing or undoing a move at the next
//! start (dev.25, package `data-folder`, task core-2). What a folder is and the target check are in [`crate::data_folder`].
//!
//! Two rules stand above the rest:
//! - **Never without a complete data folder in use.** Before the pointer is written the old place is complete and in
//!   use; after it, the new place is. Writing the pointer ([`crate::data_folder::write_pointer`]; removing it when the
//!   new place is the default root) is the only commit point.
//! - **A file is removed only when the move's own record names it and the identical file is in the new place.** When
//!   in doubt the old folder stays and the result says where it is.
//!
//! A move ([`move_to`]): the lock (exclusive [`DataLock`], and `quick.lock`), the target check again
//! ([`crate::data_folder::check`]), the journal ([`Journal`], `<default root>/data-folder.move.json`), the target with
//! its [`MOVE_MARKER`], the copy (hard links on one volume where they work, except what is written in place:
//! [`crate::data_folder::written_in_place`]), the verify (sha256 of every file read back, kdh1 of every library entry),
//! the source checked for changes, the file record ([`FileRecord`]), the [`MARKER`], the pointer, the new place
//! checked, then the records that name the root written again (install records' `pathMode`, active profiles, server
//! records' `backupDir`, built modpacks' `file`). Any failure before the commit removes the partial copy and the
//! journal and leaves the old place as it was and in use.
//!
//! At the next start, never by themselves: [`pending`], [`remove_old_copy`] (file by file, by the record),
//! [`remove_partial_copy`] (only with the target's marker), [`acknowledge`].

use crate::data_folder::{self as df, DataLock, MARKER, MOVE_MARKER, PathParts, PathStyle, Plan, RefusalCode, SystemFacts};
use crate::filecache::FileKey;
use crate::paths::{DataDirs, DataEnv, Located};
use crate::task::Progress;
use crate::util::io_at;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use sha2::Digest;
use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};

// ------------------------------------------------------------------------------------------------ formats

/// A top-level entry a move copies.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JournalEntry {
    pub name: String,
    /// One of the launcher's own entries (`data_folder::KNOWN_ENTRIES`). Unknown ones are copied and never removed.
    pub known: bool,
    pub bytes: u64,
}

/// `<default root>/data-folder.move.json`: the record of one move. `state` is for people and logs; recovery decides by
/// what is on disk (does the folder in use match `to`), never by `state` alone.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Journal {
    pub format: u32,
    /// Random; the target's [`MOVE_MARKER`] and the [`FileRecord`] carry it.
    pub id: String,
    pub from: PathBuf,
    pub to: PathBuf,
    pub started: String,
    /// The move made `to` (it was not there): a partial copy's removal removes `to` too when it is empty.
    pub created_target: bool,
    /// Folders above `to` the move made, top first: removed with a partial copy when empty.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub created_parents: Vec<PathBuf>,
    pub entries: Vec<JournalEntry>,
    /// `copying`, `verifying`, `committed`, `moved`, `cleaning`, `cleaned`, `failed`.
    pub state: String,
    /// What the pointer named before the move (None: there was no pointer, the default root was in use).
    pub previous_pointer: Option<PathBuf>,
    /// Files the move rewrote in the copy (relative paths): only `servers/<id>.json` `backupDir`, from the old root
    /// to the new one. Their record holds both hashes.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub rewritten: Vec<String>,
    /// Files the move itself wrote again in the new place after the commit (install records, activation records, the
    /// active profiles' configs), with the sha256 it wrote: their old copy is removed when it is unchanged since the
    /// move and the new one is still exactly what the move wrote.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub rewritten_after: Vec<Rewrite>,
    /// The files were hard-linked where they could be.
    #[serde(default)]
    pub hard_links: bool,
    /// The removal of the old copy (or of a partial copy): kept until [`acknowledge`].
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result: Option<Cleanup>,
}

/// A file the move wrote again in the new place after the commit ([`Journal::rewritten_after`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Rewrite {
    pub path: String,
    pub sha256: String,
}

/// One file a move copied.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecordedFile {
    /// Relative to the data root, `/` between names.
    pub path: String,
    pub size: u64,
    /// The file in the new place.
    pub sha256: String,
    /// Hard-linked (one file with two names) rather than copied.
    pub linked: bool,
    /// The old file's sha256 when the move rewrote the copy ([`Journal::rewritten`]).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source_sha256: Option<String>,
}

/// `<default root>/data-folder.move.files.json`: every file a move copied, written once before the pointer.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FileRecord {
    pub format: u32,
    pub id: String,
    pub files: Vec<RecordedFile>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct MoveMark {
    format: u32,
    id: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FolderMark {
    format: u32,
    created: String,
}

/// Something a removal left in place, and why.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Kept {
    pub path: PathBuf,
    pub bytes: u64,
    pub reason: String,
}

/// What a removal of the old copy or of a partial copy did.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Cleanup {
    /// The old folder (or the partial copy).
    pub folder: PathBuf,
    /// Everything that may be removed is removed (what stays is in `kept`).
    pub done: bool,
    /// Stopped between two files (a cancel): the next call goes on from the record.
    pub interrupted: bool,
    pub removed_files: u64,
    pub removed_bytes: u64,
    /// The folder itself is gone.
    pub folder_removed: bool,
    pub kept: Vec<Kept>,
    /// Why nothing was removed (no proof), for the player: where the folder is.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

impl Cleanup {
    fn refused(folder: &Path, reason: String) -> Cleanup {
        Cleanup { folder: folder.to_path_buf(), done: false, interrupted: false, removed_files: 0, removed_bytes: 0, folder_removed: false, kept: vec![], reason: Some(reason) }
    }
}

/// An entry a move copied and left in the old folder (the launcher does not know it, so it does not remove it).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Stayed {
    pub name: String,
    pub bytes: u64,
    /// Where it still is.
    pub path: PathBuf,
}

/// What a move did ([`move_to`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MoveResult {
    pub id: String,
    pub from: PathBuf,
    pub to: PathBuf,
    pub bytes: u64,
    pub files: u64,
    /// Files hard-linked and files copied for real.
    pub linked: u64,
    pub copied: u64,
    /// One sentence each: an entry whose files differ from their import record, a game that could not be activated
    /// again, a record that could not be rewritten.
    pub warnings: Vec<String>,
    /// Unknown entries: copied, and left in the old folder.
    pub stayed: Vec<Stayed>,
    /// Games (install ids) whose profile was activated again from the new place.
    pub activated: Vec<String>,
}

/// Where a move stands at a start ([`pending`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "state")]
pub enum Pending {
    /// No journal.
    Nothing,
    /// The new place (`to`) is in use: the old copy may still be at `from` ([`remove_old_copy`]).
    Committed { journal: Journal },
    /// The old place (`from`) is in use: a partial copy may be at `to` ([`remove_partial_copy`]).
    Uncommitted { journal: Journal },
    /// The journal matches neither: nothing is removed. `journal`: None when it cannot be read.
    Unclear { journal: Option<Journal>, reason: String },
}

/// Test hooks of [`move_to`]: where a move fails or stops as if the process died. Not for the launcher.
#[doc(hidden)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Inject {
    /// An error after the copy, before the verify.
    FailAfterCopy,
    /// An error after the verify, before the file record.
    FailAfterVerify,
    /// The check of the new place after the commit fails.
    FailCheckAfterCommit,
    /// The disk is full after this many files copied for real.
    FullAfter(u64),
    /// A cancel after this many files.
    CancelAfter(u64),
    /// The process "dies" after this step: the function returns at once, nothing is cleaned up.
    StopAfter(Step),
}

/// The steps of a move, for [`Inject::StopAfter`].
#[doc(hidden)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Step {
    Journal,
    Marker,
    Copy,
    Verify,
    FileRecord,
    FolderMarker,
    Commit,
    Activate,
}

/// How a move copies.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct MoveOptions {
    /// Real copies, also where hard links would work.
    pub force_copy: bool,
    #[doc(hidden)]
    pub inject: Option<Inject>,
}

/// How many times a move copies again what changed in the source while it copied.
pub const MAX_ROUNDS: u32 = 3;

// ------------------------------------------------------------------------------------------------ small helpers

fn default_root(env: &DataEnv) -> Result<PathBuf> {
    env.default_root()
}

fn journal_path(default: &Path) -> PathBuf {
    default.join(df::JOURNAL)
}

fn record_path(default: &Path) -> PathBuf {
    default.join(df::FILE_RECORD)
}

/// The journal in the default root: Ok(None) when there is none.
pub fn read_journal(env: &DataEnv) -> Result<Option<Journal>> {
    let j: Option<Journal> = crate::util::read_json(&journal_path(&default_root(env)?))?;
    if let Some(j) = &j
        && j.format > 1
    {
        return Err(Error::new(ErrorCode::Unsupported, "The data folder's move journal was written by a newer Kiln Launcher."));
    }
    Ok(j)
}

fn write_journal(default: &Path, j: &Journal) -> Result<()> {
    crate::util::write_json_atomic(&journal_path(default), j)
}

fn read_record(default: &Path) -> Result<Option<FileRecord>> {
    crate::util::read_json(&record_path(default))
}

fn remove_if_there(p: &Path) -> Result<()> {
    match std::fs::remove_file(p) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(io_at(p, "removing")(e)),
    }
}

fn style() -> PathStyle {
    if cfg!(windows) { PathStyle::Windows } else { PathStyle::Unix }
}

/// Two paths name one folder (canonical where they exist; Windows without case).
fn same_folder(a: &Path, b: &Path) -> bool {
    PathParts::of(&df::canonical(a), style()).same(&PathParts::of(&df::canonical(b), style()))
}

fn move_mark_id(dir: &Path) -> Option<String> {
    let p = dir.join(MOVE_MARKER);
    if !std::fs::symlink_metadata(&p).is_ok_and(|m| m.is_file()) {
        return None;
    }
    crate::util::read_json::<MoveMark>(&p).ok().flatten().map(|m| m.id)
}

fn new_id() -> Result<String> {
    Ok(crate::hash::hex(&crate::util::random_bytes::<16>()?))
}

fn rel_of(names: &[String]) -> String {
    names.join("/")
}

/// `rel` (from a record) as a path below `root`, or None when it would leave it.
fn below(root: &Path, rel: &str) -> Option<PathBuf> {
    let mut p = root.to_path_buf();
    for n in rel.split('/') {
        if n.is_empty() || n == "." || n == ".." || n.contains(['\\', ':']) && cfg!(windows) {
            return None;
        }
        p.push(n);
    }
    Some(p)
}

fn sha256_of(p: &Path) -> Option<String> {
    crate::hash::sha256_file(p).ok().map(|(h, _)| h)
}

#[cfg(unix)]
fn same_inode(a: &std::fs::Metadata, b: &std::fs::Metadata) -> bool {
    use std::os::unix::fs::MetadataExt;
    a.dev() == b.dev() && a.ino() == b.ino()
}

#[cfg(not(unix))]
fn same_inode(_: &std::fs::Metadata, _: &std::fs::Metadata) -> bool {
    false
}

/// `value` (a path text) when it starts with `old`, as the same path below `new`, by the rules of `style` (Windows:
/// without case, `\\?\` taken off, `/` or `\` between names); None when it does not start with `old`.
pub(crate) fn rebase(value: &str, old: &str, new: &str, style: PathStyle) -> Option<String> {
    let v = PathParts::parse(value, style);
    let o = PathParts::parse(old, style);
    if !o.is_absolute() || !v.within(&o) {
        return None;
    }
    let mut n = PathParts::parse(new, style);
    n.names.extend(v.names[o.names.len()..].iter().cloned());
    Some(n.text())
}

/// A path that starts with `old` (by this platform's rules) as the same path below `new`; None when it does not.
fn under_new_root(value: &Path, old: &Path, new: &Path) -> Option<PathBuf> {
    rebase(&value.to_string_lossy(), &old.to_string_lossy(), &new.to_string_lossy(), style()).map(PathBuf::from)
}

/// `text` (a JSON object) with the string field `key` rewritten from the old root to the new one, the other bytes as
/// they were. None when the field is not there or does not start with one of `olds`.
pub(crate) fn rewrite_root_field(text: &str, key: &str, olds: &[PathBuf], new: &Path) -> Option<String> {
    let v: serde_json::Value = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
    let cur = v.get(key)?.as_str()?.to_string();
    let to = olds.iter().find_map(|o| under_new_root(Path::new(&cur), o, new))?;
    let to = to.to_string_lossy().into_owned();
    let (a, b) = (serde_json::to_string(&cur).ok()?, serde_json::to_string(&to).ok()?);
    let needle = format!("\"{key}\": {a}");
    let out = if text.matches(&needle).count() == 1 {
        text.replacen(&needle, &format!("\"{key}\": {b}"), 1)
    } else {
        let mut v = v;
        v[key] = serde_json::Value::String(to.clone());
        serde_json::to_string_pretty(&v).ok()? + "\n"
    };
    let back: serde_json::Value = serde_json::from_str(out.trim_start_matches('\u{feff}')).ok()?;
    (back.get(key)?.as_str()? == to).then_some(out)
}

// ------------------------------------------------------------------------------------------------ locks

/// The exclusive lock a move or a switch holds: the caller's own shared lock made exclusive, or one of its own.
enum Held<'a> {
    Own(#[allow(dead_code)] DataLock),
    Upgraded(&'a mut DataLock),
}

impl Drop for Held<'_> {
    fn drop(&mut self) {
        if let Held::Upgraded(l) = self {
            let _ = l.downgrade();
        }
    }
}

fn refuse(code: RefusalCode, reason: impl Into<String>) -> Error {
    df::Refusal::new(code, reason).into_error()
}

fn take_locks<'a>(root: &Path, own: Option<&'a mut DataLock>) -> Result<(Held<'a>, std::fs::File)> {
    let held = match own {
        Some(l) => {
            if !l.upgrade()? {
                return Err(refuse(RefusalCode::Locked, "Another Kiln Launcher is using the data folder: close it first."));
            }
            Held::Upgraded(l)
        }
        None => match DataLock::exclusive(root)? {
            Some(l) => Held::Own(l),
            None => return Err(refuse(RefusalCode::Locked, "Another Kiln Launcher is using the data folder: close it first.")),
        },
    };
    let q = root.join("quick.lock");
    let quick = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&q).map_err(io_at(&q, "opening"))?;
    match quick.try_lock() {
        Ok(()) => Ok((held, quick)),
        Err(std::fs::TryLockError::WouldBlock) => {
            Err(refuse(RefusalCode::Busy, "The launcher is busy with the data folder (an import or a profile change): wait for it to finish."))
        }
        Err(std::fs::TryLockError::Error(e)) => Err(io_at(&q, "locking")(e)),
    }
}

/// The target check for the data folder in use, with the lock already held.
fn plan_for(located: &Located, env: &DataEnv, picked: &Path, facts: &dyn SystemFacts) -> Result<(Plan, df::Inventory)> {
    let inv = df::inventory(&located.dirs.root, env)?;
    let installs = crate::game::known(&located.dirs)?;
    let servers: Vec<PathBuf> = df::known_servers(&located.dirs).into_iter().filter_map(|s| s.dir).collect();
    let input = df::CheckInput {
        current: &located.dirs.root,
        source: located.source,
        picked,
        env,
        inventory: &inv,
        installs: &installs,
        servers: &servers,
        locked: false,
    };
    let plan = df::check(&input, facts).map_err(df::Refusal::into_error)?;
    Ok((plan, inv))
}

// ------------------------------------------------------------------------------------------------ the copy

/// A file or folder of the source, with its names below the data root.
struct Item {
    names: Vec<String>,
    dir: bool,
}

/// Every folder and file of `entries` below `root`, folders before what is in them. A link or a special file stops it
/// (named); `nexus/*.lock` files are left out.
fn walk_source(root: &Path, entries: &[String], out: &mut Vec<Item>) -> Result<()> {
    fn go(root: &Path, names: &mut Vec<String>, out: &mut Vec<Item>) -> Result<()> {
        let path = names.iter().fold(root.to_path_buf(), |p, n| p.join(n));
        let m = std::fs::symlink_metadata(&path).map_err(io_at(&path, "reading"))?;
        let link = m.file_type().is_symlink() || {
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                m.file_attributes() & 0x400 != 0
            }
            #[cfg(not(windows))]
            {
                false
            }
        };
        if link || !(m.is_dir() || m.is_file()) {
            return Err(refuse(
                RefusalCode::LinkInside,
                format!("{} in the data folder is a link or a special file: a move never follows or copies one.", path.display()),
            ));
        }
        if m.is_file() {
            if !(names.len() == 2 && names[0] == "nexus" && names[1].to_lowercase().ends_with(".lock")) {
                out.push(Item { names: names.clone(), dir: false });
            }
            return Ok(());
        }
        out.push(Item { names: names.clone(), dir: true });
        let mut kids: Vec<String> = std::fs::read_dir(&path)
            .map_err(io_at(&path, "reading"))?
            .map(|e| e.map(|e| e.file_name().to_string_lossy().into_owned()).map_err(io_at(&path, "reading")))
            .collect::<Result<_>>()?;
        kids.sort();
        for k in kids {
            names.push(k);
            go(root, names, out)?;
            names.pop();
        }
        Ok(())
    }
    for e in entries {
        go(root, &mut vec![e.clone()], out)?;
    }
    Ok(())
}

/// What the copy keeps of each file: the record line and the source's identity when it was taken.
struct Copied {
    line: RecordedFile,
    key: Option<FileKey>,
}

struct Copier<'a> {
    from: &'a Path,
    to: &'a Path,
    olds: Vec<PathBuf>,
    link: bool,
    inject: Option<Inject>,
    total: u64,
    done_bytes: u64,
    done_files: u64,
    real_files: u64,
    files: BTreeMap<String, Copied>,
    rewritten: Vec<String>,
}

fn full_disk(path: &Path) -> Error {
    Error::new(ErrorCode::Io, format!("{}: no space left on the disk", path.display()))
        .with_detail(serde_json::json!({ "os": std::io::Error::from(std::io::ErrorKind::StorageFull).to_string() }))
}

/// Copies `src` to `dst` (a new file), hashing what it reads; keeps the modification time and the permissions.
fn copy_hashed(src: &Path, dst: &Path) -> Result<(String, u64)> {
    use std::io::{Read, Write};
    let mut r = std::fs::File::open(src).map_err(|e| {
        Error::new(ErrorCode::Io, format!("Kiln cannot read {}: {e} (another program may hold it open)", src.display()))
            .with_detail(src.display().to_string())
    })?;
    let meta = r.metadata().map_err(io_at(src, "reading"))?;
    let mut w = std::fs::OpenOptions::new().write(true).create_new(true).open(dst).map_err(io_at(dst, "writing"))?;
    let mut h = sha2::Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    let mut n_total = 0u64;
    loop {
        let n = r.read(&mut buf).map_err(|e| {
            Error::new(ErrorCode::Io, format!("Kiln cannot read {}: {e} (another program may hold it open)", src.display()))
                .with_detail(src.display().to_string())
        })?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
        w.write_all(&buf[..n]).map_err(io_at(dst, "writing"))?;
        n_total += n as u64;
    }
    w.sync_all().map_err(io_at(dst, "flushing"))?;
    if let Ok(t) = meta.modified() {
        let _ = w.set_modified(t);
    }
    drop(w);
    std::fs::set_permissions(dst, meta.permissions()).map_err(io_at(dst, "setting the permissions of"))?;
    Ok((crate::hash::hex(h.finalize().as_slice()), n_total))
}

impl Copier<'_> {
    fn file(&mut self, names: &[String], progress: &mut dyn Progress) -> Result<()> {
        if progress.canceled() || matches!(self.inject, Some(Inject::CancelAfter(n)) if self.done_files >= n) {
            return Err(Error::new(ErrorCode::Canceled, "The move was canceled."));
        }
        let rel = rel_of(names);
        let src = names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
        let dst = names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
        if std::fs::symlink_metadata(&dst).is_ok() {
            std::fs::remove_file(&dst).map_err(io_at(&dst, "replacing"))?;
        }
        // servers/<id>.json: backupDir names the old root (a Steam server's tarball backups): rewritten in the copy
        let server_record = names.len() == 2 && names[0] == "servers" && names[1].ends_with(".json");
        let rewrite = if server_record {
            std::fs::read_to_string(&src).ok().and_then(|t| rewrite_root_field(&t, "backupDir", &self.olds, self.to).map(|n| (t, n)))
        } else {
            None
        };
        let key_before = FileKey::of(&src);
        let real = rewrite.is_some() || !self.link || df::written_in_place(names, style());
        if real && matches!(self.inject, Some(Inject::FullAfter(n)) if self.real_files >= n) {
            return Err(full_disk(&dst));
        }
        let line = if let Some((old_text, new_text)) = rewrite {
            crate::util::write_atomic(&dst, new_text.as_bytes())?;
            if let Ok(m) = std::fs::metadata(&src) {
                let _ = std::fs::set_permissions(&dst, m.permissions());
            }
            if !self.rewritten.contains(&rel) {
                self.rewritten.push(rel.clone());
            }
            self.real_files += 1;
            RecordedFile {
                path: rel.clone(),
                size: new_text.len() as u64,
                sha256: crate::hash::sha256_hex(new_text.as_bytes()),
                linked: false,
                source_sha256: Some(crate::hash::sha256_hex(old_text.as_bytes())),
            }
        } else if !real && std::fs::hard_link(&src, &dst).is_ok() {
            let (sha256, size) = crate::hash::sha256_file(&dst).map_err(|e| {
                Error::new(ErrorCode::Io, format!("Kiln cannot read {}: {} (another program may hold it open)", src.display(), e.message))
            })?;
            RecordedFile { path: rel.clone(), size, sha256, linked: true, source_sha256: None }
        } else {
            let (sha256, size) = copy_hashed(&src, &dst)?;
            self.real_files += 1;
            RecordedFile { path: rel.clone(), size, sha256, linked: false, source_sha256: None }
        };
        // a link changes the source's ctime: its identity is taken after it; a copy's before it was read
        let key = if line.linked { FileKey::of(&src) } else { key_before.filter(|k| FileKey::of(&src) == Some(*k)) };
        self.done_bytes += line.size;
        self.done_files += 1;
        progress.report(Some(0.05 + 0.65 * (self.done_bytes as f32 / self.total.max(1) as f32).min(1.0)), &format!("copying {rel}"));
        self.files.insert(rel, Copied { line, key });
        Ok(())
    }

    fn items(&mut self, items: &[Item], progress: &mut dyn Progress) -> Result<()> {
        for it in items {
            if it.dir {
                let dst = it.names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
                match std::fs::symlink_metadata(&dst) {
                    Ok(m) if m.is_dir() => {}
                    Ok(_) => return Err(Error::new(ErrorCode::Conflict, format!("{}: a file is in the way of a folder", dst.display()))),
                    Err(_) => std::fs::create_dir(&dst).map_err(io_at(&dst, "creating"))?,
                }
            } else {
                self.file(&it.names, progress)?;
            }
        }
        Ok(())
    }

    /// Folder permissions and times like the source's (after their files: a read-only folder is still filled).
    fn folders(&self, items: &[Item]) {
        for it in items.iter().rev().filter(|i| i.dir) {
            let src = it.names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
            let dst = it.names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
            if let Ok(m) = std::fs::metadata(&src) {
                let _ = std::fs::set_permissions(&dst, m.permissions());
                #[cfg(unix)]
                if let (Ok(t), Ok(f)) = (m.modified(), std::fs::File::open(&dst)) {
                    let _ = f.set_modified(t);
                }
            }
        }
    }

    /// Reads every file back from the target: its sha256 must be the one taken while copying. A hard link whose
    /// content differs changed in the source meanwhile (one file, two names): it is returned to copy again. A real
    /// copy that differs fails the move.
    fn verify(&self, which: Option<&BTreeSet<String>>, progress: &mut dyn Progress) -> Result<BTreeSet<String>> {
        let mut again = BTreeSet::new();
        let n = self.files.len().max(1);
        for (i, (rel, c)) in self.files.iter().enumerate() {
            if which.is_some_and(|w| !w.contains(rel)) {
                continue;
            }
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "The move was canceled."));
            }
            let dst = below(self.to, rel).ok_or_else(|| Error::new(ErrorCode::Internal, format!("{rel}: not a relative path")))?;
            let got = sha256_of(&dst);
            if got.as_deref() != Some(c.line.sha256.as_str()) {
                if c.line.linked {
                    again.insert(rel.clone());
                } else {
                    return Err(Error::new(ErrorCode::HashMismatch, format!("The copy of {rel} in {} does not match the file it was copied from.", self.to.display())));
                }
            }
            if i % 64 == 0 {
                progress.report(Some(0.70 + 0.15 * i as f32 / n as f32), &format!("checking {rel}"));
            }
        }
        Ok(again)
    }

    /// What changed in the source since it was copied: files added, gone, or with another identity (size, time,
    /// inode). Gone files are taken out of the copy and the record.
    fn changes(&mut self, entries: &[String]) -> Result<(Vec<Item>, BTreeSet<String>)> {
        let mut now = Vec::new();
        walk_source(self.from, entries, &mut now)?;
        let seen: BTreeSet<String> = now.iter().filter(|i| !i.dir).map(|i| rel_of(&i.names)).collect();
        let gone: Vec<String> = self.files.keys().filter(|k| !seen.contains(*k)).cloned().collect();
        for g in gone {
            if let Some(p) = below(self.to, &g) {
                let _ = std::fs::remove_file(p);
            }
            self.files.remove(&g);
        }
        let mut redo = Vec::new();
        let mut changed = BTreeSet::new();
        for it in now {
            let rel = rel_of(&it.names);
            if it.dir {
                let dst = it.names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
                if !dst.is_dir() {
                    redo.push(it);
                }
                continue;
            }
            let src = it.names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
            let same = self.files.get(&rel).is_some_and(|c| c.key.is_some() && c.key == FileKey::of(&src));
            if !same {
                changed.insert(rel);
                redo.push(it);
            }
        }
        Ok((redo, changed))
    }
}

/// The library entries of `dirs` whose files do not have the hash their import record names. `source`: the same
/// entries where they came from; a mismatch there too is a warning (copied as it was), else an error.
fn check_library(target: &DataDirs, source: &DataDirs, warnings: &mut Vec<String>) -> Result<()> {
    for e in crate::library::list(target, None)? {
        for v in &e.versions {
            let r = crate::library::LibraryRef { kind: e.kind, id: e.id.clone(), version: v.version.clone(), hash: v.hash.clone() };
            let hash_of = |d: &DataDirs| -> Option<String> {
                let p = crate::library::path_of(d, &r).ok()?;
                if e.kind == crate::manifest::ModKind::Plugin { sha256_of(&p) } else { crate::hash::dirhash(&p).ok() }
            };
            let got = hash_of(target);
            if got.as_deref() == Some(v.hash.as_str()) {
                continue;
            }
            if got.is_some() && hash_of(source) == got {
                warnings.push(format!(
                    "{} {} {}: its files did not match the hash it was imported with before the move; it was copied as it is.",
                    e.kind.as_str(), e.id, v.version
                ));
            } else {
                return Err(Error::new(ErrorCode::HashMismatch, format!("The copy of {} {} {} does not match its import record.", e.kind.as_str(), e.id, v.version)));
            }
        }
    }
    Ok(())
}

/// What the new place must show after the commit: the same profiles and library entries, a readable settings file.
#[derive(Debug, Clone, PartialEq, Eq)]
struct Counts {
    profiles: usize,
    library: usize,
    settings: bool,
}

fn counts(dirs: &DataDirs) -> Result<Counts> {
    Ok(Counts {
        profiles: crate::profile::list(dirs)?.len(),
        library: crate::library::list(dirs, None)?.iter().map(|e| e.versions.len()).sum(),
        settings: crate::util::read_json::<serde_json::Value>(&dirs.settings())?.is_some(),
    })
}

// ------------------------------------------------------------------------------------------------ the move

fn stopped(step: Step) -> Error {
    Error::new(ErrorCode::Internal, format!("stopped after {step:?} (a simulated crash)"))
}

/// Moves the data folder in use to the folder the player picked. It checks again everything
/// [`crate::data_folder::check`] refuses (never a plan made earlier), takes the exclusive [`DataLock`] (`own`: this
/// process's shared lock, made exclusive for the move and shared again after) and `quick.lock`, and refuses while a
/// journal exists. Then: journal, target and its [`MOVE_MARKER`], copy (hard links where [`Plan::hard_links`] says
/// they work and `opts.force_copy` is off, except what is written in place), verify (sha256 of every file read back,
/// kdh1 of every library entry), the source's changes copied again (at most [`MAX_ROUNDS`] rounds), the file record,
/// the [`MARKER`], **the pointer** (the commit; removed when the new place is the default root), the new place checked
/// (`locate` gives it; profiles, library entries and settings read as in the old place; if not, the previous pointer
/// is put back and the move fails), then install records and active profiles written again for the new root, and
/// built modpacks' `file` rewritten (failures there are warnings).
///
/// A cancel, an error, a full disk or a file that cannot be read before the commit: the partial copy is removed
/// ([`remove_partial_copy`]'s rule), the journal too, and the old place is as it was and in use. After the commit the
/// old copy stays until [`remove_old_copy`].
pub fn move_to(
    located: &Located, env: &DataEnv, picked: &Path, facts: &dyn SystemFacts, own: Option<&mut DataLock>, opts: &MoveOptions,
    progress: &mut dyn Progress,
) -> Result<MoveResult> {
    let from = located.dirs.root.clone();
    let default = default_root(env)?;
    let (_held, _quick) = take_locks(&from, own)?;
    progress.report(Some(0.0), "checking the new place");
    let (plan, inv) = plan_for(located, env, picked, facts)?;
    if plan.action != df::Action::Move {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("{} is a Kiln data folder already: use it instead of moving there.", plan.target.display()))
            .with_detail(serde_json::json!({ "action": "use", "target": plan.target })));
    }
    let to = plan.target.clone();
    let link = plan.hard_links && !opts.force_copy;
    let entries: Vec<String> = inv.known.iter().chain(&inv.unknown).map(|e| e.name.clone()).collect();
    let before = counts(&located.dirs)?;

    // the journal, then the target and its marker
    let mut missing = Vec::new();
    for a in to.ancestors() {
        if std::fs::symlink_metadata(a).is_ok() {
            break;
        }
        missing.push(a.to_path_buf());
    }
    missing.reverse();
    let created_target = missing.last().is_some_and(|m| *m == to);
    let created_parents: Vec<PathBuf> = missing.iter().filter(|m| **m != to).cloned().collect();
    let mut journal = Journal {
        format: 1,
        id: new_id()?,
        from: from.clone(),
        to: to.clone(),
        started: crate::util::now_rfc3339(),
        created_target,
        created_parents,
        entries: inv.known.iter().map(|e| (e, true)).chain(inv.unknown.iter().map(|e| (e, false)))
            .map(|(e, known)| JournalEntry { name: e.name.clone(), known, bytes: e.bytes }).collect(),
        state: "copying".into(),
        previous_pointer: match located.source {
            crate::paths::RootSource::Pointer => Some(from.clone()),
            _ => None,
        },
        rewritten: vec![],
        rewritten_after: vec![],
        hard_links: link,
        result: None,
    };
    write_journal(&default, &journal)?;
    let stop = |s: Step| opts.inject == Some(Inject::StopAfter(s));
    if stop(Step::Journal) {
        return Err(stopped(Step::Journal));
    }
    let fail = |e: Error, journal: &Journal| -> Error { abort(&default, journal, e) };
    // before the marker is there nothing of the move is in the target: the folders it made go if empty, then the journal
    let fail_early = |e: Error| -> Error {
        for m in missing.iter().rev() {
            let _ = std::fs::remove_dir(m);
        }
        let _ = remove_if_there(&journal_path(&default));
        e
    };
    for m in &missing {
        if let Err(e) = std::fs::create_dir(m) {
            return Err(fail_early(io_at(m, "creating")(e)));
        }
    }
    if let Err(e) = crate::util::write_json_atomic(&to.join(MOVE_MARKER), &MoveMark { format: 1, id: journal.id.clone() }) {
        let _ = remove_if_there(&to.join(format!("{MOVE_MARKER}.tmp")));
        return Err(fail_early(e));
    }
    if stop(Step::Marker) {
        return Err(stopped(Step::Marker));
    }

    // copy, verify, again what changed
    let mut olds = vec![from.clone(), df::canonical(&from)];
    olds.dedup();
    let mut c = Copier {
        from: &from,
        to: &to,
        olds,
        link,
        inject: opts.inject,
        total: inv.bytes,
        done_bytes: 0,
        done_files: 0,
        real_files: 0,
        files: BTreeMap::new(),
        rewritten: vec![],
    };
    let mut warnings = Vec::new();
    let copied = (|| -> Result<()> {
        let mut items = Vec::new();
        walk_source(&from, &entries, &mut items)?;
        c.items(&items, progress)?;
        c.folders(&items);
        Ok(())
    })();
    if let Err(e) = copied {
        return Err(fail(e, &journal));
    }
    if stop(Step::Copy) {
        return Err(stopped(Step::Copy));
    }
    if opts.inject == Some(Inject::FailAfterCopy) {
        return Err(fail(Error::new(ErrorCode::Io, "a failure injected after the copy"), &journal));
    }
    journal.state = "verifying".into();
    let _ = write_journal(&default, &journal);
    let verified = (|| -> Result<()> {
        let mut again = c.verify(None, progress)?;
        let mut rounds = 0;
        loop {
            let (redo, mut changed) = c.changes(&entries)?;
            changed.extend(again.iter().cloned());
            if changed.is_empty() && redo.is_empty() {
                break;
            }
            rounds += 1;
            if rounds > MAX_ROUNDS {
                return Err(Error::new(ErrorCode::Conflict, format!(
                    "{} kept changing while it was copied ({} files changed in the last round): close what writes there and move again.",
                    from.display(),
                    changed.len()
                )));
            }
            for rel in &again {
                if let Some(p) = below(&to, rel) {
                    let _ = std::fs::remove_file(p);
                }
            }
            let mut items: Vec<Item> = redo;
            for rel in &again {
                if !items.iter().any(|i| rel_of(&i.names) == *rel) {
                    items.push(Item { names: rel.split('/').map(str::to_string).collect(), dir: false });
                }
            }
            c.items(&items, progress)?;
            c.folders(&items);
            again = c.verify(Some(&changed), progress)?;
        }
        check_library(&DataDirs::at(&to), &located.dirs, &mut warnings)
    })();
    if let Err(e) = verified {
        return Err(fail(e, &journal));
    }
    if stop(Step::Verify) {
        return Err(stopped(Step::Verify));
    }
    if opts.inject == Some(Inject::FailAfterVerify) {
        return Err(fail(Error::new(ErrorCode::Io, "a failure injected after the verify"), &journal));
    }

    // the file record, the marker, the pointer
    journal.rewritten = c.rewritten.clone();
    let record = FileRecord { format: 1, id: journal.id.clone(), files: c.files.values().map(|f| f.line.clone()).collect() };
    if let Err(e) = crate::util::write_json_atomic(&record_path(&default), &record) {
        return Err(fail(e, &journal));
    }
    let _ = write_journal(&default, &journal);
    if stop(Step::FileRecord) {
        return Err(stopped(Step::FileRecord));
    }
    if let Err(e) = crate::util::write_json_atomic(&to.join(MARKER), &FolderMark { format: 1, created: crate::util::now_rfc3339() }) {
        return Err(fail(e, &journal));
    }
    if stop(Step::FolderMarker) {
        return Err(stopped(Step::FolderMarker));
    }
    progress.report(Some(0.9), "switching to the new place");
    let to_default = same_folder(&to, &default);
    let commit = if to_default { df::remove_pointer(&default) } else { df::write_pointer(&default, &to).map(|_| ()) };
    if let Err(e) = commit {
        return Err(fail(e, &journal));
    }
    if stop(Step::Commit) {
        return Err(stopped(Step::Commit));
    }
    journal.state = "committed".into();
    let _ = write_journal(&default, &journal);

    // the new place must read as the old one did
    let new_dirs = DataDirs::at(&to);
    let check = (|| -> Result<()> {
        if opts.inject == Some(Inject::FailCheckAfterCommit) {
            return Err(Error::new(ErrorCode::DataFolder, "a failure injected in the check after the switch"));
        }
        let l = env.locate()?;
        if !same_folder(&l.dirs.root, &to) {
            return Err(Error::new(ErrorCode::DataFolder, format!("After the switch the launcher finds {}, not {}.", l.dirs.root.display(), to.display())));
        }
        let after = counts(&new_dirs)?;
        if after != before {
            return Err(Error::new(ErrorCode::DataFolder, format!(
                "The new place does not read as the old one: {} profiles and {} library entries there, {} and {} before.",
                after.profiles, after.library, before.profiles, before.library
            )));
        }
        Ok(())
    })();
    if let Err(e) = check {
        let back = match &journal.previous_pointer {
            Some(p) => df::write_pointer(&default, p).map(|_| ()),
            None => df::remove_pointer(&default),
        };
        if let Err(b) = back {
            // the new place is complete (verified): it stays in use rather than none
            return Err(Error::new(e.code, format!("{} The previous place could not be set back ({}), so {} stays in use.", e.message, b.message, to.display())));
        }
        return Err(fail(e, &journal));
    }

    // what names the root, written again for the new one
    progress.report(Some(0.95), "writing the games' profiles for the new place");
    let mut activated = Vec::new();
    for g in crate::game::known(&new_dirs).unwrap_or_default() {
        let mut g2 = g.clone();
        g2.path_mode = crate::game::path_mode_for(g.platform, g.steam_flavor, g.prefix.as_deref(), &to);
        let receipt = crate::game::record(&new_dirs, &g.id).ok().flatten().and_then(|r| r.receipt);
        if let Err(e) = crate::game::write_record(&new_dirs, &g2, receipt) {
            warnings.push(format!("The record of the game at {} could not be written for the new place: {}", g.path.display(), e.message));
        }
        let Ok(Some(file)) = crate::profile::read_game_profile(&g.path) else { continue };
        if !crate::profile::file_is_here(&new_dirs, &g2, &file) {
            continue;
        }
        match crate::profile::activate(&new_dirs, &file.id, &g2) {
            Ok(_) => activated.push(g.id.clone()),
            Err(e) => warnings.push(format!(
                "The profile of the game at {} could not be written for the new place ({}): activate it again before you play.",
                g.path.display(),
                e.message
            )),
        }
    }
    for s in df::known_servers(&new_dirs) {
        let Some(dir) = s.dir else { continue };
        let f = dir.join(crate::modpack::BUILD_RECORD);
        let Ok(text) = std::fs::read_to_string(&f) else { continue };
        if let Some(new) = rewrite_root_field(&text, "file", &c.olds, &to)
            && let Err(e) = crate::util::write_atomic(&f, new.as_bytes())
        {
            warnings.push(format!("{} still names the old place: {}", f.display(), e.message));
        }
    }
    // what the move itself wrote again after the commit, with what it wrote (the old copies' removal needs it)
    for f in c.files.values() {
        if let Some(p) = below(&to, &f.line.path)
            && let Some(now) = sha256_of(&p)
            && now != f.line.sha256
        {
            journal.rewritten_after.push(Rewrite { path: f.line.path.clone(), sha256: now });
        }
    }
    if stop(Step::Activate) {
        return Err(stopped(Step::Activate));
    }
    journal.state = "moved".into();
    let _ = write_journal(&default, &journal);
    progress.report(Some(1.0), &format!("moved to {}", to.display()));
    let linked = c.files.values().filter(|f| f.line.linked).count() as u64;
    Ok(MoveResult {
        id: journal.id.clone(),
        from: from.clone(),
        to: to.clone(),
        bytes: c.files.values().map(|f| f.line.size).sum(),
        files: c.files.len() as u64,
        linked,
        copied: c.files.len() as u64 - linked,
        warnings,
        stayed: inv.unknown.iter().map(|e| Stayed { name: e.name.clone(), bytes: e.bytes, path: from.join(&e.name) }).collect(),
        activated,
    })
}

/// Ends a move that failed before (or at) its commit: the partial copy removed by the proof of its marker, then the
/// journal and the file record. The error says where anything that could not be removed is.
fn abort(default: &Path, journal: &Journal, e: Error) -> Error {
    let c = remove_partial(default, journal);
    if c.reason.is_none() && c.kept.is_empty() {
        e
    } else {
        let left = c.reason.clone().unwrap_or_else(|| format!("{} files were left in {}", c.kept.len(), journal.to.display()));
        Error::new(e.code, format!("{} The partial copy could not be removed: {left}.", e.message)).with_detail(serde_json::to_value(&c).unwrap_or_default())
    }
}

// ------------------------------------------------------------------------------------------------ use, reset

/// Makes the Kiln data folder at `picked` the one in use, as it is: no copy, no merge, nothing removed. The same
/// refusals as a move except space; the target must be a Kiln data folder. The pointer is written (removed when the
/// folder is the default root). Returns where the launcher finds its data now.
pub fn use_folder(located: &Located, env: &DataEnv, picked: &Path, facts: &dyn SystemFacts, own: Option<&mut DataLock>) -> Result<Located> {
    let default = default_root(env)?;
    let (_held, _quick) = take_locks(&located.dirs.root, own)?;
    let (plan, _) = plan_for(located, env, picked, facts)?;
    if plan.action != df::Action::Use {
        return Err(refuse(RefusalCode::NotAKilnFolder, format!("{} is not a Kiln data folder: move the data folder there instead.", plan.target.display())));
    }
    let before = df::read_pointer(&default).map_err(|p| p.into_error())?;
    if same_folder(&plan.target, &default) {
        df::remove_pointer(&default)?;
    } else {
        df::write_pointer(&default, &plan.target)?;
    }
    match env.locate() {
        Ok(l) if same_folder(&l.dirs.root, &plan.target) => Ok(l),
        other => {
            let _ = match before {
                Some(p) => df::write_pointer(&default, &p.path).map(|_| ()),
                None => df::remove_pointer(&default),
            };
            Err(match other {
                Err(e) => e,
                Ok(l) => Error::new(ErrorCode::DataFolder, format!("After the switch the launcher finds {}, not {}.", l.dirs.root.display(), plan.target.display())),
            })
        }
    }
}

/// Starts fresh at the default root after a broken pointer: the pointer file is renamed to `data-folder.json.old`.
/// Refused when the pointer is not broken.
pub fn reset(env: &DataEnv) -> Result<Located> {
    match env.locate_checked() {
        Ok(_) => Err(Error::new(ErrorCode::Refused, "The data folder's pointer is fine: start fresh only after a broken one.")),
        Err(p) if p.kind == crate::paths::ProblemKind::NoDefault || p.pointer.is_none() => Err(p.into_error()),
        Err(p) => {
            let file = p.pointer.clone().unwrap_or_default();
            let old = file.with_file_name(df::POINTER_OLD);
            crate::util::rename_replace(&file, &old)?;
            env.locate()
        }
    }
}

// ------------------------------------------------------------------------------------------------ recovery

/// Where a move stands, from what is on disk: the journal against the folder in use.
pub fn pending(env: &DataEnv) -> Result<Pending> {
    let journal = match read_journal(env) {
        Ok(None) => return Ok(Pending::Nothing),
        Ok(Some(j)) => j,
        Err(e) => return Ok(Pending::Unclear { journal: None, reason: format!("The move's journal cannot be read: {}", e.message) }),
    };
    match env.locate_checked() {
        Ok(l) if same_folder(&l.dirs.root, &journal.to) => Ok(Pending::Committed { journal }),
        Ok(l) if same_folder(&l.dirs.root, &journal.from) => Ok(Pending::Uncommitted { journal }),
        Ok(l) => {
            let reason = format!(
                "The launcher uses {}, but the move went from {} to {}: nothing is removed.",
                l.dirs.root.display(),
                journal.from.display(),
                journal.to.display()
            );
            Ok(Pending::Unclear { journal: Some(journal), reason })
        }
        Err(p) => Ok(Pending::Unclear { journal: Some(journal), reason: p.reason }),
    }
}

fn save_result(default: &Path, journal: &mut Journal, c: &Cleanup, state: &str) {
    journal.result = Some(c.clone());
    journal.state = state.into();
    let _ = write_journal(default, journal);
}

/// Removes the old copy of a committed move, file by file, by the file record: an old file goes only when its sha256
/// is the record's (for a rewritten file, the old one's) and the file in the new place has the record's sha256 too.
/// Then the folders that are empty, the known lock files and the old marker, and the old root itself when it is
/// empty and is not the default root. Entries the launcher does not know are never removed. A cancel (between two
/// files) stops it; the next call goes on. Without proof (no committed move, the folder in use is not `to`, the
/// marker's id or the record's differs, no record) nothing is removed and the result says where the old folder is.
/// The result is kept in the journal until [`acknowledge`].
pub fn remove_old_copy(env: &DataEnv, progress: &mut dyn Progress) -> Result<Cleanup> {
    let default = default_root(env)?;
    let mut journal = match pending(env)? {
        Pending::Committed { journal } => journal,
        Pending::Nothing => return Err(Error::not_found("No move of the data folder to finish.")),
        Pending::Uncommitted { journal } => {
            return Ok(Cleanup::refused(&journal.from, format!("The move to {} was not finished: {} is still the data folder.", journal.to.display(), journal.from.display())));
        }
        Pending::Unclear { journal, reason } => {
            let at = journal.map(|j| j.from).unwrap_or_default();
            return Ok(Cleanup::refused(&at, format!("{reason} The old folder is at {}.", at.display())));
        }
    };
    let from = journal.from.clone();
    let no = |why: String| Cleanup::refused(&from, format!("{why} Nothing was removed: the old folder is at {}.", from.display()));
    if move_mark_id(&journal.to).as_deref() != Some(journal.id.as_str()) {
        return Ok(no(format!("{} does not carry this move's marker.", journal.to.display())));
    }
    let record = match read_record(&default) {
        Ok(Some(r)) if r.id == journal.id && r.format <= 1 => r,
        Ok(Some(_)) => return Ok(no("The move's file record belongs to another move.".into())),
        Ok(None) => return Ok(no("The move's file record is missing.".into())),
        Err(e) => return Ok(no(format!("The move's file record cannot be read ({}).", e.message))),
    };
    // a game whose profile still names the old folder (Z or native mode) loads its files: nothing goes while one does
    let new_dirs = DataDirs::at(&journal.to);
    for g in crate::game::known(&new_dirs).unwrap_or_default() {
        if let Ok(crate::profile::GameProfileOwner::Elsewhere { root: Some(root), .. }) = crate::profile::game_profile_owner(&new_dirs, &g)
            && same_folder(&root, &from)
        {
            return Ok(no(format!("The game at {} still runs a profile from the old folder.", g.path.display())));
        }
    }
    let unknown: BTreeSet<&str> = journal.entries.iter().filter(|e| !e.known).map(|e| e.name.as_str()).collect();
    let after: BTreeMap<&str, &str> = journal.rewritten_after.iter().map(|r| (r.path.as_str(), r.sha256.as_str())).collect();
    let mut c = journal.result.clone().filter(|r| r.folder == from).unwrap_or(Cleanup {
        folder: from.clone(),
        done: false,
        interrupted: false,
        removed_files: 0,
        removed_bytes: 0,
        folder_removed: false,
        kept: vec![],
        reason: None,
    });
    c.interrupted = false;
    c.kept.retain(|k| unknown.iter().any(|u| k.path == from.join(u)) && k.path.exists());
    let n = record.files.len().max(1);
    for (i, f) in record.files.iter().enumerate() {
        if progress.canceled() {
            c.interrupted = true;
            save_result(&default, &mut journal, &c, "cleaning");
            return Ok(c);
        }
        if unknown.contains(f.path.split('/').next().unwrap_or_default()) {
            continue;
        }
        let (Some(old), Some(new)) = (below(&from, &f.path), below(&journal.to, &f.path)) else {
            c.kept.push(Kept { path: from.join(&f.path), bytes: f.size, reason: "its name in the record is not a path in the folder".into() });
            continue;
        };
        let Ok(om) = std::fs::symlink_metadata(&old) else { continue }; // already removed
        let keep = |reason: &str, c: &mut Cleanup| c.kept.push(Kept { path: old.clone(), bytes: om.len(), reason: reason.into() });
        if !om.is_file() {
            keep("it is no longer a plain file", &mut c);
            continue;
        }
        let nm = std::fs::symlink_metadata(&new).ok();
        let new_sha = nm.as_ref().filter(|m| m.is_file()).and_then(|_| sha256_of(&new));
        let old_sha = if nm.as_ref().is_some_and(|m| same_inode(&om, m)) { new_sha.clone() } else { sha256_of(&old) };
        let old_ok = old_sha.as_deref() == Some(f.source_sha256.as_deref().unwrap_or(&f.sha256));
        // the new file is the one copied, or the one the move itself wrote after the commit
        let new_ok = new_sha.as_deref() == Some(f.sha256.as_str()) || (new_sha.is_some() && new_sha.as_deref() == after.get(f.path.as_str()).copied());
        if !old_ok {
            keep("it changed in the old folder after the move", &mut c);
        } else if !new_ok {
            keep("its copy in the new place changed or is gone, so this one stays", &mut c);
        } else {
            remove_if_there(&old)?;
            c.removed_files += 1;
            c.removed_bytes += om.len();
        }
        if i % 64 == 0 {
            progress.report(Some(i as f32 / n as f32), &format!("removing the old copy: {}", f.path));
        }
    }
    // what is left of the launcher's own entries: empty folders, lock files, the old markers
    for e in journal.entries.iter().filter(|e| e.known) {
        let p = from.join(&e.name);
        remove_empty_dirs(&p);
        if e.name == "nexus" {
            for l in std::fs::read_dir(&p).into_iter().flatten().flatten() {
                if l.file_name().to_string_lossy().to_lowercase().ends_with(".lock") && l.path().is_file() {
                    let _ = std::fs::remove_file(l.path());
                }
            }
            remove_empty_dirs(&p);
        }
    }
    for f in std::fs::read_dir(&from).into_iter().flatten().flatten() {
        let name = f.file_name().to_string_lossy().into_owned();
        let ours = name.to_lowercase().ends_with(".lock") || name == MARKER || name == MOVE_MARKER;
        if ours && std::fs::symlink_metadata(f.path()).is_ok_and(|m| m.is_file()) {
            let _ = std::fs::remove_file(f.path());
        }
    }
    for u in &unknown {
        let p = from.join(u);
        if let Ok(m) = std::fs::symlink_metadata(&p) {
            let bytes = if m.is_dir() { dir_bytes(&p) } else { m.len() };
            if !c.kept.iter().any(|k| k.path == p) {
                c.kept.push(Kept { path: p, bytes, reason: "the launcher does not know it, so it does not remove it (it was copied to the new place)".into() });
            }
        }
    }
    if !same_folder(&from, &default) && std::fs::read_dir(&from).is_ok_and(|mut d| d.next().is_none()) {
        c.folder_removed = std::fs::remove_dir(&from).is_ok();
    }
    c.done = true;
    save_result(&default, &mut journal, &c, "cleaned");
    progress.report(Some(1.0), "the old copy is removed");
    Ok(c)
}

fn dir_bytes(p: &Path) -> u64 {
    let mut n = 0;
    for e in std::fs::read_dir(p).into_iter().flatten().flatten() {
        match std::fs::symlink_metadata(e.path()) {
            Ok(m) if m.is_dir() => n += dir_bytes(&e.path()),
            Ok(m) => n += m.len(),
            Err(_) => {}
        }
    }
    n
}

/// Removes the folders below and at `p` that hold nothing (bottom up). Never a file.
fn remove_empty_dirs(p: &Path) {
    let Ok(m) = std::fs::symlink_metadata(p) else { return };
    if !m.is_dir() {
        return;
    }
    for e in std::fs::read_dir(p).into_iter().flatten().flatten() {
        remove_empty_dirs(&e.path());
    }
    let _ = std::fs::remove_dir(p); // fails while it holds anything
}

/// The partial copy of a move that did not commit: only with the proof of the target's [`MOVE_MARKER`] (this
/// journal's id) are the journal's entries removed from the target, then the two markers, then the target and the
/// folders above it the move made, when they are empty. Anything else in the target stays. Then the journal and the
/// file record go. Without the proof nothing is removed (the journal stays: [`acknowledge`]).
fn remove_partial(default: &Path, journal: &Journal) -> Cleanup {
    let to = &journal.to;
    let mut c = Cleanup { folder: to.clone(), done: false, interrupted: false, removed_files: 0, removed_bytes: 0, folder_removed: false, kept: vec![], reason: None };
    let drop_records = || {
        let _ = remove_if_there(&record_path(default));
        let _ = remove_if_there(&journal_path(default));
    };
    if std::fs::symlink_metadata(to).is_err() {
        // never made: nothing to remove
        drop_records();
        c.done = true;
        c.folder_removed = true;
        return c;
    }
    if move_mark_id(to).as_deref() != Some(journal.id.as_str()) {
        c.reason = Some(format!("{} does not carry this move's marker, so nothing was removed from it.", to.display()));
        return c;
    }
    for e in &journal.entries {
        let p = to.join(&e.name);
        let r = match std::fs::symlink_metadata(&p) {
            Err(_) => continue,
            Ok(m) if m.is_dir() => std::fs::remove_dir_all(&p),
            Ok(_) => std::fs::remove_file(&p),
        };
        match r {
            Ok(()) => c.removed_bytes += e.bytes,
            Err(err) => c.kept.push(Kept { path: p, bytes: e.bytes, reason: format!("it could not be removed: {err}") }),
        }
    }
    if !c.kept.is_empty() {
        c.reason = Some(format!("Parts of the partial copy in {} could not be removed.", to.display()));
        return c;
    }
    let _ = remove_if_there(&to.join(MARKER));
    let _ = remove_if_there(&to.join(MOVE_MARKER));
    if journal.created_target {
        c.folder_removed = std::fs::remove_dir(to).is_ok();
    }
    for p in journal.created_parents.iter().rev() {
        let _ = std::fs::remove_dir(p);
    }
    drop_records();
    c.done = true;
    c
}

/// Removes the partial copy of a move that did not commit ([`pending`] says `Uncommitted`): see `remove_partial`'s
/// rule (the target's marker must carry the journal's id).
pub fn remove_partial_copy(env: &DataEnv) -> Result<Cleanup> {
    let default = default_root(env)?;
    match pending(env)? {
        Pending::Uncommitted { journal } => {
            let mut journal = journal;
            let c = remove_partial(&default, &journal);
            if !c.done {
                save_result(&default, &mut journal, &c, "failed");
            }
            Ok(c)
        }
        Pending::Nothing => Err(Error::not_found("No unfinished move of the data folder.")),
        Pending::Committed { journal } => Ok(Cleanup::refused(&journal.to, format!("The move to {} was finished: {} is the data folder now.", journal.to.display(), journal.to.display()))),
        Pending::Unclear { journal, reason } => {
            let at = journal.map(|j| j.to).unwrap_or_default();
            Ok(Cleanup::refused(&at, format!("{reason} The partial copy, if any, is at {}.", at.display())))
        }
    }
}

/// The launcher has shown the result: the journal and the file record go, and the new place's [`MOVE_MARKER`] (only
/// when it carries this journal's id). Nothing of the data is removed; an old copy still there stays where it is.
pub fn acknowledge(env: &DataEnv) -> Result<()> {
    let default = default_root(env)?;
    if let Ok(Some(j)) = read_journal(env)
        && move_mark_id(&j.to).as_deref() == Some(j.id.as_str())
        && matches!(pending(env)?, Pending::Committed { .. })
    {
        remove_if_there(&j.to.join(MOVE_MARKER))?;
    }
    remove_if_there(&record_path(&default))?;
    remove_if_there(&journal_path(&default))?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn root_fields_are_rewritten_only_below_the_old_root() {
        let old = PathBuf::from("/data/kiln");
        let new = Path::new("/mnt/big/kiln");
        let text = "{\n  \"id\": \"isles\",\n  \"backupDir\": \"/data/kiln/backups/servers/isles\",\n  \"stopTimeoutS\": 90\n}\n";
        let out = rewrite_root_field(text, "backupDir", std::slice::from_ref(&old), new).unwrap();
        assert_eq!(out, text.replace("/data/kiln/backups", "/mnt/big/kiln/backups"), "only that field, the rest byte for byte");
        assert_eq!(rewrite_root_field(text, "backupDir", &[PathBuf::from("/data/kil")], new), None, "a name prefix is not the root");
        assert_eq!(rewrite_root_field("{\"backupDir\": \"/srv/isles.backups\"}", "backupDir", &[old.clone()], new), None);
        assert_eq!(rewrite_root_field("{\"backupDir\": null}", "backupDir", &[old.clone()], new), None);
        assert_eq!(rewrite_root_field("{\"other\": 1}", "backupDir", &[old.clone()], new), None);
        let exact = rewrite_root_field("{\"file\":\"/data/kiln\"}", "file", &[old], new).unwrap();
        let v: serde_json::Value = serde_json::from_str(&exact).unwrap();
        assert_eq!(v["file"], "/mnt/big/kiln");
    }

    #[test]
    fn rebase_follows_windows_rules() {
        let w = PathStyle::Windows;
        let old = r"C:\Users\Jörg Müller\AppData\Local\Kiln";
        let v = r"c:\users\jörg müller\appdata\local\kiln\backups\servers\isles";
        assert_eq!(rebase(v, old, r"D:\", w).as_deref(), Some(r"D:\backups\servers\isles"), "to a drive root");
        assert_eq!(rebase(v, old, r"E:\Spiele Ordner\データ\Kiln", w).as_deref(), Some(r"E:\Spiele Ordner\データ\Kiln\backups\servers\isles"));
        assert_eq!(rebase(&format!(r"\\?\{v}"), old, r"D:\Kiln", w).as_deref(), Some(r"D:\Kiln\backups\servers\isles"), r"\\?\ taken off");
        assert_eq!(rebase(v, &format!(r"\\?\{old}"), r"\\?\D:\Kiln", w).as_deref(), Some(r"D:\Kiln\backups\servers\isles"));
        assert_eq!(rebase(r"E:\Kiln\backups", old, r"D:\Kiln", w), None, "another drive is not below it");
        assert_eq!(rebase(r"C:\Users\Jörg Müller\AppData\Local\Kiln2\x", old, r"D:\Kiln", w), None, "a name prefix is not the root");
        let long = format!(r"D:\{}", "a".repeat(280));
        assert_eq!(rebase(&format!(r"{old}\backups"), old, &long, w), Some(format!(r"{long}\backups")), "more than 260 characters");
        assert_eq!(rebase(&format!(r"{old}\x"), "Kiln", r"D:\Kiln", w), None, "a relative root is no root");
        let u = PathStyle::Unix;
        assert_eq!(rebase("/home/u/.local/share/kiln/backups/x", "/home/u/.local/share/kiln", "/mnt/big/kiln", u).as_deref(), Some("/mnt/big/kiln/backups/x"));
        assert_eq!(rebase("/home/u/.local/share/Kiln/backups", "/home/u/.local/share/kiln", "/mnt/big/kiln", u), None, "case counts on Linux");
    }

    #[test]
    fn record_paths_stay_below_their_root() {
        let r = Path::new("/r");
        assert_eq!(below(r, "a/b.json"), Some(PathBuf::from("/r/a/b.json")));
        for bad in ["", "../x", "a/../../x", "a//b", "./a"] {
            assert_eq!(below(r, bad), None, "{bad}");
        }
    }
}
