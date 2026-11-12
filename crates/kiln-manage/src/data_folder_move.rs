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
//! journal and leaves the old place as it was and in use. What of the partial copy cannot be removed is named, with
//! the OS error, and keeps its proof: the target's [`MOVE_MARKER`] and the journal stay for the next start.
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

/// `<default root>/data-folder.move.json`: the record of one move. Recovery checks what is on disk and uses commit
/// states conservatively too: they prevent partial-copy deletion even if the destination is absent or reset.
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
    /// The move made `to`'s lock file (`data-folder.lock`): it goes with a partial copy.
    #[serde(default)]
    pub created_lock: bool,
    /// The entries the move copies (never one the target keeps for itself).
    pub entries: Vec<JournalEntry>,
    /// Entries of `from` the move did not copy because the target keeps an entry of its own by that name (Windows: the
    /// default root's `state\`): they stay in the old folder.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub skipped: Vec<String>,
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
    /// The removal of the old copy, the move's last phase ([`remove_old_copy`]'s rule): what went, what stayed and why.
    /// Interrupted by a cancel after the switch, it goes on at the next start. Not run when a game's profile could not
    /// be activated again (the reason names the game). None only if the move stopped before.
    pub cleanup: Option<Cleanup>,
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
    /// The disk is full after this many files copied for real (before the next one is made).
    FullAfter(u64),
    /// The disk fills up in the middle of the copy after this many files copied for real: the next destination is made
    /// and partly written, then the write fails.
    FullDuring(u64),
    /// As `FullDuring`, and that file cannot be removed by this process (as when another program opened it without
    /// delete sharing): every removal of it the move tries fails. A new process (the next start) can remove it.
    HeldDuring(u64),
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

/// Even if the pointer was reset or a drive is absent, a verified destination is never a partial copy.
fn may_have_committed(journal: &Journal) -> bool {
    !matches!(journal.state.as_str(), "copying" | "verifying" | "failed")
        || std::fs::symlink_metadata(journal.to.join(MARKER)).is_ok()
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
/// (named); `nexus/*.lock` files and the hash cache (`state/file-hashes.json`) are left out.
fn walk_source(root: &Path, entries: &[String], style: PathStyle, out: &mut Vec<Item>) -> Result<()> {
    fn go(root: &Path, names: &mut Vec<String>, style: PathStyle, out: &mut Vec<Item>) -> Result<()> {
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
            let lock = names.len() == 2 && style.same_name(&names[0], "nexus") && names[1].to_lowercase().ends_with(".lock");
            let cache = names.len() == 2 && style.same_name(&names[0], df::HASH_CACHE[0]) && style.same_name(&names[1], df::HASH_CACHE[1]);
            if !(lock || cache) {
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
            go(root, names, style, out)?;
            names.pop();
        }
        Ok(())
    }
    for e in entries {
        go(root, &mut vec![e.clone()], style, out)?;
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
    style: PathStyle,
    inject: Option<Inject>,
    total: u64,
    done_bytes: u64,
    done_files: u64,
    real_files: u64,
    /// The files this move made in the target (and only those may be replaced or removed).
    files: BTreeMap<String, Copied>,
    /// The folders this move made in the target.
    made_dirs: BTreeSet<String>,
    rewritten: Vec<String>,
    /// [`Inject::HeldDuring`]: the file this process cannot remove.
    held: Option<PathBuf>,
}

fn full_disk(path: &Path) -> Error {
    Error::new(ErrorCode::Io, format!("{}: no space left on the disk", path.display()))
        .with_detail(serde_json::json!({ "os": std::io::Error::from(std::io::ErrorKind::StorageFull).to_string() }))
}

/// A failed write into the target: the error, and whether the call had made `dst` (then the caller removes it:
/// [`Copier::undo`]).
type Written<T> = std::result::Result<T, (Error, bool)>;

/// Copies `src` to `dst` (a new file), hashing what it reads; keeps the modification time and the permissions. A
/// failure after `dst` was made says so (the caller removes it). `full`: a full disk injected after the first bytes are
/// written (a test hook).
fn copy_hashed(src: &Path, dst: &Path, full: bool) -> Written<(String, u64)> {
    use std::io::{Read, Write};
    let mut r = std::fs::File::open(src).map_err(|e| (cannot_read(src, &e.to_string()), false))?;
    let meta = r.metadata().map_err(|e| (io_at(src, "reading")(e), false))?;
    let mut w = std::fs::OpenOptions::new().write(true).create_new(true).open(dst).map_err(|e| (io_at(dst, "writing")(e), false))?;
    // from here `dst` is this call's
    let written = (|| -> Result<(String, u64)> {
        let mut h = sha2::Sha256::new();
        let mut buf = vec![0u8; 1 << 16];
        let mut n_total = 0u64;
        loop {
            let n = r.read(&mut buf).map_err(|e| cannot_read(src, &e.to_string()))?;
            if n == 0 {
                break;
            }
            h.update(&buf[..n]);
            w.write_all(&buf[..n]).map_err(io_at(dst, "writing"))?;
            n_total += n as u64;
            if full {
                return Err(full_disk(dst));
            }
        }
        if full {
            return Err(full_disk(dst));
        }
        w.sync_all().map_err(io_at(dst, "flushing"))?;
        if let Ok(t) = meta.modified() {
            let _ = w.set_modified(t);
        }
        Ok((crate::hash::hex(h.finalize().as_slice()), n_total))
    })();
    drop(w);
    written
        .and_then(|r| std::fs::set_permissions(dst, meta.permissions()).map(|_| r).map_err(io_at(dst, "setting the permissions of")))
        .map_err(|e| (e, true))
}

/// Writes `data` to `dst` (a new file, never one that is there). A failure after `dst` was made says so (the caller
/// removes it). No temporary file: the target is verified as a whole before the commit. `full`: a full disk injected
/// after half the bytes are written (a test hook).
fn write_new(dst: &Path, data: &[u8], full: bool) -> Written<()> {
    use std::io::Write;
    let mut w = std::fs::OpenOptions::new().write(true).create_new(true).open(dst).map_err(|e| (io_at(dst, "writing")(e), false))?;
    let done = if full {
        let _ = w.write_all(&data[..data.len() / 2]);
        Err(full_disk(dst))
    } else {
        w.write_all(data).map_err(io_at(dst, "writing")).and_then(|_| w.sync_all().map_err(io_at(dst, "flushing")))
    };
    drop(w);
    done.map_err(|e| (e, true))
}

/// `m` as a sentence (a full stop added when it has none), to add another one after it.
fn sentence(m: &str) -> String {
    if m.ends_with(['.', '!', '?']) { m.to_string() } else { format!("{m}.") }
}

fn cannot_read(src: &Path, why: &str) -> Error {
    Error::new(ErrorCode::Io, format!("Kiln cannot read {}: {why} (another program may hold it open)", src.display())).with_detail(src.display().to_string())
}

impl Copier<'_> {
    /// Removes a file this move made in the target (the OS error as it is, also `NotFound`).
    fn remove_own(&self, p: &Path) -> std::io::Result<()> {
        if self.held.as_deref() == Some(p) {
            return Err(std::io::Error::new(std::io::ErrorKind::PermissionDenied, "another program holds it open (a test hook)"));
        }
        std::fs::remove_file(p)
    }

    /// A file this call made at `dst` and could not finish: removed again. When that fails it stays known to the move
    /// (in its record of made files, so the clean-up tries again and names it) and the error names it and its folder.
    fn undo(&mut self, rel: &str, dst: &Path, e: Error) -> Error {
        match self.remove_own(dst) {
            Ok(()) => e,
            Err(re) if re.kind() == std::io::ErrorKind::NotFound => e,
            Err(re) => {
                let size = std::fs::symlink_metadata(dst).map(|m| m.len()).unwrap_or(0);
                let line = RecordedFile { path: rel.to_string(), size, sha256: String::new(), linked: false, source_sha256: None };
                self.files.insert(rel.to_string(), Copied { line, key: None });
                Error {
                    code: e.code,
                    message: format!("{} The move made {} and could not remove it again ({re}): it is in {}.", sentence(&e.message), dst.display(), self.to.display()),
                    detail: e.detail,
                }
            }
        }
    }

    /// Removes a file this move made before it is copied again; an error names it (it stays in the record).
    fn remove_again(&self, rel: &str) -> Result<()> {
        let Some(p) = below(self.to, rel) else { return Ok(()) };
        match self.remove_own(&p) {
            Ok(()) => Ok(()),
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(e) => Err(Error::new(ErrorCode::Io, format!("The move made {} and cannot remove it to copy it again ({e}): it is in {}.", p.display(), self.to.display()))
                .with_detail(p.display().to_string())),
        }
    }

    fn file(&mut self, names: &[String], progress: &mut dyn Progress) -> Result<()> {
        if progress.canceled() || matches!(self.inject, Some(Inject::CancelAfter(n)) if self.done_files >= n) {
            return Err(Error::new(ErrorCode::Canceled, "The move was canceled."));
        }
        let rel = rel_of(names);
        let src = names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
        let dst = names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
        if std::fs::symlink_metadata(&dst).is_ok() {
            // only a file this move made is replaced; anything else there is not the move's
            if !self.files.contains_key(&rel) {
                return Err(Error::new(ErrorCode::Conflict, format!("{} is already there and the move did not make it: the move stops rather than replace it.", dst.display()))
                    .with_detail(dst.display().to_string()));
            }
            self.remove_again(&rel)?;
        }
        // servers/<id>.json: backupDir names the old root (a Steam server's tarball backups): rewritten in the copy
        let server_record = names.len() == 2 && names[0] == "servers" && names[1].ends_with(".json");
        let rewrite = if server_record {
            std::fs::read_to_string(&src).ok().and_then(|t| rewrite_root_field(&t, "backupDir", &self.olds, self.to).map(|n| (t, n)))
        } else {
            None
        };
        let key_before = FileKey::of(&src);
        let real = rewrite.is_some() || !self.link || df::written_in_place(names, self.style);
        if real && matches!(self.inject, Some(Inject::FullAfter(n)) if self.real_files >= n) {
            return Err(full_disk(&dst));
        }
        // whatever is made in the target here is recorded below, or removed again before the error returns (and when
        // that fails, recorded as made and named in the error: `undo`)
        let full = matches!(self.inject, Some(Inject::FullDuring(n) | Inject::HeldDuring(n)) if self.real_files >= n);
        if full && matches!(self.inject, Some(Inject::HeldDuring(_))) {
            self.held = Some(dst.clone());
        }
        // a file to link is read first (its sha256 and size): one that cannot be read fails here, before anything of it
        // is in the target. The verify reads the link back.
        let linked = if real {
            None
        } else {
            let (sha256, size) = crate::hash::sha256_file(&src).map_err(|e| cannot_read(&src, &e.message))?;
            std::fs::hard_link(&src, &dst).is_ok().then_some((sha256, size))
        };
        let line = if let Some((old_text, new_text)) = rewrite {
            if let Err((e, made)) = write_new(&dst, new_text.as_bytes(), full) {
                return Err(if made { self.undo(&rel, &dst, e) } else { e });
            }
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
        } else if let Some((sha256, size)) = linked {
            RecordedFile { path: rel.clone(), size, sha256, linked: true, source_sha256: None }
        } else {
            let (sha256, size) = match copy_hashed(&src, &dst, full) {
                Ok(r) => r,
                Err((e, made)) => return Err(if made { self.undo(&rel, &dst, e) } else { e }),
            };
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
                let rel = rel_of(&it.names);
                let dst = it.names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
                match std::fs::symlink_metadata(&dst) {
                    Ok(m) if m.is_dir() && self.made_dirs.contains(&rel) => {}
                    Ok(_) => {
                        return Err(Error::new(ErrorCode::Conflict, format!("{} is already there and the move did not make it: the move stops rather than write into it.", dst.display()))
                            .with_detail(dst.display().to_string()));
                    }
                    Err(_) => {
                        std::fs::create_dir(&dst).map_err(io_at(&dst, "creating"))?;
                        self.made_dirs.insert(rel);
                    }
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
        walk_source(self.from, entries, self.style, &mut now)?;
        let seen: BTreeSet<String> = now.iter().filter(|i| !i.dir).map(|i| rel_of(&i.names)).collect();
        let gone: Vec<String> = self.files.keys().filter(|k| !seen.contains(*k)).cloned().collect();
        for g in gone {
            self.remove_again(&g)?;
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
/// journal exists. Then: journal, target, the target's exclusive [`DataLock`] (held until the move returns, so a
/// launcher that starts meanwhile cannot take the new place) and its [`MOVE_MARKER`], copy (hard links where
/// [`Plan::hard_links`] says they work and `opts.force_copy` is off, except what is written in place), verify (sha256
/// of every file read back, kdh1 of every library entry), the source's changes copied again (at most [`MAX_ROUNDS`]
/// rounds), the file record, the [`MARKER`], the folders the move made flushed (Unix), **the pointer** (the commit;
/// removed when the new place is the default root), the new place checked (`locate` gives it; profiles, library
/// entries and settings read as in the old place; if not, the previous pointer is put back and the move fails), then
/// install records written again for the new root, the games whose profile was this folder's before the move
/// activated again, built modpacks' `file` rewritten (failures there are warnings), and last the old copy removed by
/// [`remove_old_copy`]'s rule ([`MoveResult::cleanup`]; a cancel there interrupts only the removal). When a game's
/// profile could not be activated again, that game still names the old copy: the removal is not run at all, the
/// cleanup says which game and why, and the journal stays for the next start.
///
/// A move never copies into, replaces or removes anything in the target that it did not make: an entry the target
/// keeps for itself (its state or settings folder, [`crate::data_folder::own_entries`]) is not copied and stays in the
/// old folder; a file or folder already there fails the move (`conflict`). `state/file-hashes.json` (a cache keyed by
/// absolute paths) is never copied.
///
/// A cancel, an error, a full disk or a file that cannot be read before the commit: what the move made in the target
/// is removed (also the file a failed copy step had just made), the journal too, and the old place is as it was and in
/// use. A file to link is read first (sha256 and size): one that cannot be read fails the move before anything of it
/// is in the target. A file or folder the move made that cannot be removed again is never left unnamed: it stays in
/// the move's record of what it made, the error names it and its folder, and the clean-up keeps its proof (the
/// target's [`MOVE_MARKER`], its [`MARKER`] and lock file, and the journal with the result): the folder reads as a
/// move in progress, and [`remove_partial_copy`] removes the rest at the next start.
pub fn move_to(
    located: &Located, env: &DataEnv, picked: &Path, facts: &dyn SystemFacts, own: Option<&mut DataLock>, opts: &MoveOptions,
    progress: &mut dyn Progress,
) -> Result<MoveResult> {
    let from = located.dirs.root.clone();
    let default = default_root(env)?;
    let style = PathStyle::of(env);
    let (_held, _quick) = take_locks(&from, own)?;
    progress.report(Some(0.0), "checking the new place");
    let (plan, inv) = plan_for(located, env, picked, facts)?;
    if plan.action != df::Action::Move {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("{} is a Kiln data folder already: use it instead of moving there.", plan.target.display()))
            .with_detail(serde_json::json!({ "action": "use", "target": plan.target })));
    }
    let to = plan.target.clone();
    let link = plan.hard_links && !opts.force_copy;
    // what the target keeps for itself (Windows: `state\` in the default root) is not copied, never touched there
    let target_own = df::own_entries(&to, env);
    let kept_by_target = |name: &str| target_own.iter().any(|o| style.same_name(o, name));
    let moving: Vec<(&df::EntrySize, bool)> = inv.known.iter().map(|e| (e, true)).chain(inv.unknown.iter().map(|e| (e, false)))
        .filter(|(e, _)| !kept_by_target(&e.name)).collect();
    let skipped: Vec<&df::EntrySize> = inv.known.iter().chain(&inv.unknown).filter(|e| kept_by_target(&e.name)).collect();
    if let Some((e, _)) = moving.iter().find(|(e, _)| std::fs::symlink_metadata(to.join(&e.name)).is_ok()) {
        return Err(refuse(RefusalCode::TargetHasEntry, format!("{} already holds {}: a move never writes into what is already there.", to.display(), e.name)));
    }
    let entries: Vec<String> = moving.iter().map(|(e, _)| e.name.clone()).collect();
    let before = counts(&located.dirs)?;
    // the games whose profile is this folder's now, decided here with the old root (an older launcher's activation
    // has no record, and its paths name the old root)
    let reactivate: Vec<(crate::game::GameInstall, String)> = crate::game::known(&located.dirs)
        .unwrap_or_default()
        .into_iter()
        .filter_map(|g| {
            let f = crate::profile::read_game_profile(&g.path).ok().flatten()?;
            crate::profile::file_is_here(&located.dirs, &g, &f).then_some((g, f.id))
        })
        .collect();

    // the journal, then the target, its lock and its marker
    let mut missing = Vec::new();
    for a in to.ancestors() {
        if std::fs::symlink_metadata(a).is_ok() {
            break;
        }
        missing.push(a.to_path_buf());
    }
    missing.reverse();
    let created_target = missing.last().is_some_and(|m| *m == to);
    let created_lock = std::fs::symlink_metadata(to.join(df::LOCK)).is_err();
    let mut journal = Journal {
        format: 1,
        id: new_id()?,
        from: from.clone(),
        to: to.clone(),
        started: crate::util::now_rfc3339(),
        created_target,
        created_parents: missing.iter().filter(|m| **m != to).cloned().collect(),
        created_lock,
        entries: moving.iter().map(|(e, known)| JournalEntry { name: e.name.clone(), known: *known, bytes: e.bytes }).collect(),
        skipped: skipped.iter().map(|e| e.name.clone()).collect(),
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
    let mut tlock: Option<DataLock> = None;
    // before the marker is there nothing of the move is in the target: the folders and the lock file it made go, then
    // the journal (the error names a lock file the move made that could not be removed)
    macro_rules! fail_early {
        ($e:expr) => {{
            let mut e: Error = $e;
            drop(tlock.take());
            if created_lock && let Err(r) = remove_if_there(&to.join(df::LOCK)) {
                e.message = format!("{} The move could not remove the lock file it made: {}.", sentence(&e.message), r.message);
            }
            for m in missing.iter().rev() {
                let _ = std::fs::remove_dir(m);
            }
            let _ = remove_if_there(&journal_path(&default));
            return Err(e);
        }};
    }
    for m in &missing {
        if let Err(e) = std::fs::create_dir(m) {
            fail_early!(io_at(m, "creating")(e));
        }
    }
    match DataLock::exclusive(&to) {
        Ok(Some(l)) => tlock = Some(l),
        Ok(None) => fail_early!(refuse(RefusalCode::Locked, format!("A Kiln Launcher is using {}: close it first.", to.display()))),
        Err(e) => fail_early!(e),
    }
    if let Err(mut e) = crate::util::write_json_atomic(&to.join(MOVE_MARKER), &MoveMark { format: 1, id: journal.id.clone() }) {
        if let Err(r) = remove_if_there(&to.join(format!("{MOVE_MARKER}.tmp"))) {
            e.message = format!("{} The move could not remove the file it was writing: {}.", sentence(&e.message), r.message);
        }
        fail_early!(e);
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
        style,
        inject: opts.inject,
        total: moving.iter().map(|(e, _)| e.bytes).sum(),
        done_bytes: 0,
        done_files: 0,
        real_files: 0,
        files: BTreeMap::new(),
        made_dirs: BTreeSet::new(),
        rewritten: vec![],
        held: None,
    };
    // a failure before the commit: what the move made goes, then the markers, the lock, the journal
    macro_rules! fail {
        ($e:expr) => {{
            let e = $e;
            drop(tlock.take());
            return Err(abort(&default, &journal, e, &c));
        }};
    }
    let mut warnings = Vec::new();
    let copied = (|| -> Result<()> {
        let mut items = Vec::new();
        walk_source(&from, &entries, style, &mut items)?;
        c.items(&items, progress)?;
        c.folders(&items);
        Ok(())
    })();
    if let Err(e) = copied {
        fail!(e);
    }
    if stop(Step::Copy) {
        return Err(stopped(Step::Copy));
    }
    if opts.inject == Some(Inject::FailAfterCopy) {
        fail!(Error::new(ErrorCode::Io, "a failure injected after the copy"));
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
                c.remove_again(rel)?;
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
        fail!(e);
    }
    if stop(Step::Verify) {
        return Err(stopped(Step::Verify));
    }
    if opts.inject == Some(Inject::FailAfterVerify) {
        fail!(Error::new(ErrorCode::Io, "a failure injected after the verify"));
    }

    // the file record, the marker, the folders on disk, the pointer
    journal.rewritten = c.rewritten.clone();
    let record = FileRecord { format: 1, id: journal.id.clone(), files: c.files.values().map(|f| f.line.clone()).collect() };
    if let Err(e) = crate::util::write_json_atomic(&record_path(&default), &record) {
        fail!(e);
    }
    let _ = write_journal(&default, &journal);
    if stop(Step::FileRecord) {
        return Err(stopped(Step::FileRecord));
    }
    if let Err(e) = crate::util::write_json_atomic(&to.join(MARKER), &FolderMark { format: 1, created: crate::util::now_rfc3339() }) {
        fail!(e);
    }
    if stop(Step::FolderMarker) {
        return Err(stopped(Step::FolderMarker));
    }
    let mut flush: Vec<PathBuf> = c.made_dirs.iter().rev().filter_map(|d| below(&to, d)).collect();
    flush.push(to.clone());
    flush.extend(to.parent().map(Path::to_path_buf));
    for d in &flush {
        if let Err(e) = sync_dir(d) {
            fail!(e);
        }
    }
    progress.report(Some(0.9), "switching to the new place");
    let to_default = same_folder(&to, &default);
    let commit = if to_default { df::remove_pointer(&default) } else { df::write_pointer(&default, &to).map(|_| ()) };
    if let Err(e) = commit {
        fail!(e);
    }
    let _ = sync_dir(&default);
    if stop(Step::Commit) {
        return Err(stopped(Step::Commit));
    }
    journal.state = "committed".into();
    let _ = write_journal(&default, &journal);

    // The progress boundary also lets fault tests make the previous pointer unwritable after the commit.
    progress.report(Some(0.92), "checking the new place after the switch");
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
            // The verified new place stays selected. This is a committed result, never a failed/canceled move:
            // callers must keep their old-root writers held and offer the restart, not resume that root.
            let reason = format!("{} The previous place could not be set back ({}), so {} stays selected. The old copy stays at {}. Restart Kiln before continuing.", e.message, b.message, to.display(), from.display());
            warnings.push(reason.clone());
            let linked = c.files.values().filter(|f| f.line.linked).count() as u64;
            return Ok(MoveResult {
                id: journal.id.clone(), from: from.clone(), to: to.clone(),
                bytes: c.files.values().map(|f| f.line.size).sum(), files: c.files.len() as u64,
                linked, copied: c.files.len() as u64 - linked, warnings,
                stayed: inv.unknown.iter().map(|e| Stayed { name: e.name.clone(), bytes: e.bytes, path: from.join(&e.name) }).collect(),
                activated: Vec::new(), cleanup: Some(Cleanup::refused(&from, reason)),
            });
        }
        let _ = sync_dir(&default);
        fail!(e);
    }

    // what names the root, written again for the new one; what the move writes is recorded where it writes it
    progress.report(Some(0.95), "writing the games' profiles for the new place");
    let mut after: BTreeMap<String, String> = BTreeMap::new();
    let mut note = |rel: String| {
        if c.files.contains_key(&rel)
            && let Some(p) = below(&to, &rel)
            && let Some(sha) = sha256_of(&p)
        {
            after.insert(rel, sha);
        }
    };
    for g in crate::game::known(&new_dirs).unwrap_or_default() {
        let mut g2 = g.clone();
        g2.path_mode = crate::game::path_mode_for(g.platform, g.steam_flavor, g.prefix.as_deref(), &to);
        let receipt = crate::game::record(&new_dirs, &g.id).ok().flatten().and_then(|r| r.receipt);
        match crate::game::write_record(&new_dirs, &g2, receipt) {
            Ok(_) => note(format!("installs/{}.json", g.id)),
            Err(e) => warnings.push(format!("The record of the game at {} could not be written for the new place: {}", g.path.display(), e.message)),
        }
    }
    let mut activated = Vec::new();
    let mut not_activated = Vec::new();
    let known_now = crate::game::known(&new_dirs).unwrap_or_default();
    for (g, pid) in &reactivate {
        let g2 = known_now.iter().find(|k| k.id == g.id).cloned().unwrap_or_else(|| {
            let mut g2 = g.clone();
            g2.path_mode = crate::game::path_mode_for(g.platform, g.steam_flavor, g.prefix.as_deref(), &to);
            g2
        });
        match crate::profile::activate(&new_dirs, pid, &g2) {
            Ok(_) => {
                activated.push(g.id.clone());
                note(format!("installs/{}.active.json", g.id));
                let mut cfg = Vec::new();
                walk_files(&new_dirs.profile(pid).join("config"), &format!("profiles/{pid}/config"), &mut cfg);
                for rel in cfg {
                    note(rel);
                }
            }
            Err(e) => {
                not_activated.push(g.path.display().to_string());
                warnings.push(format!(
                    "The profile of the game at {} could not be written for the new place ({}): activate it again before you play.",
                    g.path.display(),
                    e.message
                ));
            }
        }
    }
    journal.rewritten_after = after.into_iter().filter(|(rel, sha)| c.files.get(rel).is_some_and(|f| f.line.sha256 != *sha))
        .map(|(path, sha256)| Rewrite { path, sha256 }).collect();
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
    journal.state = "moved".into();
    let _ = write_journal(&default, &journal);
    if stop(Step::Activate) {
        return Err(stopped(Step::Activate));
    }

    // last, while nothing else has written yet: the old copy (the same rule and code as at a next start). Not when a
    // game's profile could not be written for the new place: that game still reads from the old copy.
    let cleanup = if !not_activated.is_empty() {
        Cleanup::refused(&from, format!(
            "The profile of the game at {} could not be written for the new place, so the old copy stays at {}: activate that profile again, then the old copy goes at the next start.",
            not_activated.join(", "),
            from.display()
        ))
    } else {
        match remove_old_copy(env, progress) {
            Ok(c) => c,
            Err(e) => Cleanup::refused(&from, format!("The old copy was not removed ({}): it is at {}.", e.message, from.display())),
        }
    };
    progress.report(Some(1.0), &format!("moved to {}", to.display()));
    let linked = c.files.values().filter(|f| f.line.linked).count() as u64;
    let mut stayed: Vec<Stayed> = inv.unknown.iter().filter(|e| !kept_by_target(&e.name))
        .map(|e| Stayed { name: e.name.clone(), bytes: e.bytes, path: from.join(&e.name) }).collect();
    stayed.extend(skipped.iter().filter(|e| from.join(&e.name).exists()).map(|e| Stayed { name: e.name.clone(), bytes: e.bytes, path: from.join(&e.name) }));
    drop(tlock);
    Ok(MoveResult {
        id: journal.id.clone(),
        from: from.clone(),
        to: to.clone(),
        bytes: c.files.values().map(|f| f.line.size).sum(),
        files: c.files.len() as u64,
        linked,
        copied: c.files.len() as u64 - linked,
        warnings,
        stayed,
        activated,
        cleanup: Some(cleanup),
    })
}

/// Every regular file below `dir`, as `<rel>/<name>` paths (for the files the move rewrote).
fn walk_files(dir: &Path, rel: &str, out: &mut Vec<String>) {
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        let r = format!("{rel}/{name}");
        match std::fs::symlink_metadata(e.path()) {
            Ok(m) if m.is_dir() => walk_files(&e.path(), &r, out),
            Ok(m) if m.is_file() => out.push(r),
            _ => {}
        }
    }
}

/// Unix: a folder's entries flushed to the disk. Windows cannot flush a folder: Ok without doing anything (its files
/// are flushed one by one as they are written). A file system that cannot flush folders (`EINVAL`) is not an error.
fn sync_dir(dir: &Path) -> Result<()> {
    #[cfg(unix)]
    {
        match std::fs::File::open(dir).and_then(|f| f.sync_all()) {
            Ok(()) => Ok(()),
            Err(e) if e.raw_os_error() == Some(libc::EINVAL) || e.kind() == std::io::ErrorKind::Unsupported => Ok(()),
            Err(e) => Err(io_at(dir, "flushing")(e)),
        }
    }
    #[cfg(not(unix))]
    {
        let _ = dir;
        Ok(())
    }
}

/// Ends a move that failed before (or at) its commit: exactly what this process's move made goes ([`remove_made`]).
/// When anything of it stays, the journal (with that result, `failed`) and the target's [`MOVE_MARKER`] stay too: the
/// folder reads as a move in progress, and the next start removes the rest ([`remove_partial_copy`]). The error then
/// names what stayed, why, and where.
fn abort(default: &Path, journal: &Journal, e: Error, made: &Copier) -> Error {
    let c = remove_made(default, journal, made);
    if c.done {
        return e;
    }
    let mut j = journal.clone();
    save_result(default, &mut j, &c, "failed");
    let mut left: Vec<String> = c.kept.iter().take(3).map(|k| format!("{} ({})", k.path.display(), k.reason)).collect();
    if c.kept.len() > 3 {
        left.push(format!("and {} more", c.kept.len() - 3));
    }
    Error {
        code: e.code,
        message: format!(
            "{} What the move made in {} could not all be removed: {}. The folder stays marked as an unfinished move, and the next start removes the rest.",
            sentence(&e.message),
            journal.to.display(),
            left.join(", ")
        ),
        detail: Some(serde_json::to_value(&c).unwrap_or_default()),
    }
}

/// What this process's move made in the target, removed: its files, its folders (deepest first), then the proof
/// ([`remove_proof`]). A file or folder of the move that cannot be removed is kept, with the OS error; then the
/// markers, the lock file, the target and the records stay (the clean-up keeps its proof). Anything else in the target
/// stays.
fn remove_made(default: &Path, journal: &Journal, made: &Copier) -> Cleanup {
    let to = &journal.to;
    let mut c = Cleanup { folder: to.clone(), done: false, interrupted: false, removed_files: 0, removed_bytes: 0, folder_removed: false, kept: vec![], reason: None };
    for (rel, f) in &made.files {
        let Some(p) = below(to, rel) else { continue };
        match made.remove_own(&p) {
            Ok(()) => {
                c.removed_files += 1;
                c.removed_bytes += f.line.size;
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => c.kept.push(Kept { path: p, bytes: f.line.size, reason: format!("it could not be removed: {e}") }),
        }
    }
    for d in made.made_dirs.iter().rev() {
        let Some(p) = below(to, d) else { continue };
        match std::fs::remove_dir(&p) {
            Ok(()) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            // it holds what is kept already
            Err(_) if c.kept.iter().any(|k| k.path.starts_with(&p)) => {}
            Err(e) => c.kept.push(Kept { bytes: dir_bytes(&p), path: p, reason: format!("it could not be removed: {e}") }),
        }
    }
    remove_proof(default, journal, &mut c);
    c
}

/// The last step of removing a partial copy when nothing of it was kept: the folder marker, the lock file the move made
/// and last the move marker (the proof goes last: when one cannot be removed, it is kept and the move marker stays),
/// then the target and the folders above it the move made (when empty), the file record and the journal. With anything
/// kept (before or here) nothing of the proof is removed and the result says where the folder is.
fn remove_proof(default: &Path, journal: &Journal, c: &mut Cleanup) {
    let to = &journal.to;
    if c.kept.is_empty() {
        for (name, ours) in [(MARKER, true), (df::LOCK, journal.created_lock), (MOVE_MARKER, true)] {
            let p = to.join(name);
            if !ours {
                continue;
            }
            if let Err(e) = remove_if_there(&p) {
                let bytes = std::fs::symlink_metadata(&p).map(|m| m.len()).unwrap_or(0);
                c.kept.push(Kept { path: p, bytes, reason: format!("it could not be removed: {}", e.message) });
                break;
            }
        }
    }
    if !c.kept.is_empty() {
        c.reason = Some(format!("Parts of the partial copy in {} could not be removed: the folder stays marked as an unfinished move.", to.display()));
        return;
    }
    if journal.created_target {
        c.folder_removed = std::fs::remove_dir(to).is_ok();
    }
    for p in journal.created_parents.iter().rev() {
        let _ = std::fs::remove_dir(p);
    }
    let _ = remove_if_there(&record_path(default));
    let _ = remove_if_there(&journal_path(default));
    c.done = true;
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
            let default = default_root(env)?;
            if std::fs::symlink_metadata(default.join(MOVE_MARKER)).is_ok() {
                return Err(refuse(RefusalCode::MoveInProgress, format!("{} holds a move marker: choose its committed data folder instead of starting fresh here.", default.display())));
            }
            let old = file.with_file_name(df::POINTER_OLD);
            crate::util::rename_replace(&file, &old)?;
            env.locate()
        }
    }
}

/// Points the launcher at a Kiln data folder the player picks after a broken pointer (there is no folder in use, so
/// [`use_folder`] cannot run). Refused unless the pointer is broken ([`DataEnv::locate_checked`] fails with a pointer
/// problem: never with `KILN_DATA_DIR` set, never when the default root cannot be told). `picked` must be a Kiln data
/// folder ([`crate::data_folder::recognize`]: not a move in progress, not empty; no `Kiln` subfolder: the player points
/// at their data) whose path is a full path without `..` and not a network path (`\\?\` taken off), not a link, not a
/// file, not inside a game folder (a folder above it holds the game's exe) and not inside Kiln's state or settings
/// folder; the default root must be writable (`facts`). The pointer is written atomically (removed when `picked` is the
/// default root) and the data root located again; if that does not give `picked`, the previous pointer file is put back
/// byte for byte. Nothing else is made in the default root (no lock file, no journal); nothing is copied or removed.
pub fn repoint(env: &DataEnv, picked: &Path, facts: &dyn SystemFacts) -> Result<Located> {
    use crate::data_folder::{FolderState as S, RefusalCode as C};
    match env.locate_checked() {
        Ok(_) => return Err(Error::new(ErrorCode::Refused, "The data folder's pointer works: pick another folder through a move or \"use this one\".")),
        Err(p) if p.kind == crate::paths::ProblemKind::NoDefault || p.pointer.is_none() => return Err(p.into_error()),
        Err(_) => {}
    }
    let default = default_root(env)?;
    let style = PathStyle::of(env);
    df::check_form(&picked.to_string_lossy(), style).map_err(df::Refusal::into_error)?;
    let picked = crate::util::clean(&crate::steam::strip_verbatim(picked.to_path_buf()));
    let shown = picked.display().to_string();
    match df::recognize(&picked, env) {
        Ok(S::Kiln { .. }) => {}
        Ok(S::Link) => return Err(refuse(C::Link, format!("{shown} is a link to another folder: pick the folder itself."))),
        Ok(S::NotAFolder) => return Err(refuse(C::NotAFolder, format!("{shown} is a file, not a folder."))),
        Ok(S::Moving { id, committed: true }) if read_journal(env)?.is_some_and(|j| j.id == id) => {}
        Ok(S::Moving { .. }) => return Err(refuse(C::MoveInProgress, format!("{shown} holds a move marker that is not this launcher's committed move: nothing was changed."))),
        Ok(_) => return Err(refuse(C::NotAKilnFolder, format!("{shown} is not a Kiln data folder: pick the folder that holds your library and profiles."))),
        Err(e) => return Err(refuse(C::Unreadable, format!("Kiln cannot read {shown}: {}.", e.message))),
    }
    let canon = df::canonical(&picked);
    for a in canon.ancestors().filter(|a| !a.as_os_str().is_empty()) {
        if a.join(crate::game::CLIENT_EXE).is_file() || a.join(crate::game::SERVER_EXE).is_file() {
            return Err(refuse(C::InsideGame, format!("{shown} is inside the game folder {}: pick a folder outside it.", a.display())));
        }
    }
    let p = PathParts::of(&canon, style);
    for d in [env.state_dir(), env.config_dir()] {
        let dc = df::canonical(&d);
        if p.within(&PathParts::of(&dc, style)) {
            return Err(refuse(C::InsideKilnState, format!("{shown} is inside {}, where Kiln keeps its own state and settings.", dc.display())));
        }
    }
    if !facts.writable(&default) {
        return Err(refuse(C::NotWritable, format!("Kiln cannot write to {}.", default.display())));
    }
    let pointer = default.join(df::POINTER);
    let before = std::fs::read(&pointer).ok();
    if same_folder(&picked, &default) {
        df::remove_pointer(&default)?;
    } else {
        df::write_pointer(&default, &picked)?;
    }
    match env.locate() {
        Ok(l) if same_folder(&l.dirs.root, &picked) => Ok(l),
        other => {
            let _ = match &before {
                Some(b) => df::write_pointer_bytes(&default, b),
                None => df::remove_pointer(&default),
            };
            Err(match other {
                Err(e) => e,
                Ok(l) => Error::new(ErrorCode::DataFolder, format!("After the switch the launcher finds {}, not {}.", l.dirs.root.display(), picked.display())),
            })
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
        Ok(l) if same_folder(&l.dirs.root, &journal.from) && !may_have_committed(&journal) => Ok(Pending::Uncommitted { journal }),
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
/// is the record's (for a rewritten file, the old one's) and the file in the new place has the record's sha256, or the
/// one the move itself wrote there after the commit ([`Journal::rewritten_after`]). Then the hash cache
/// (`state/file-hashes.json`, by its exact name, not in a `state` the old root keeps for itself), the lock files
/// (`quick.lock`, `data-folder.lock`, `nexus/*.lock`), the old markers, the folders that are empty, and the old root
/// itself when it is empty and is not the default root. Entries the launcher does not know, and entries the target
/// kept for itself ([`Journal::skipped`]), are never removed. A cancel (between two files) stops it; the next call goes
/// on, and a call when nothing is left removes what a running move could not (its own lock files, the empty old
/// root). Without proof (no committed move, the folder in use is not `to`, the marker's id or the record's differs, no
/// record) nothing is removed and the result says where the old folder is. Nor while a known game's profile names a
/// path under the old root, whoever wrote it (`kiln/profile.json`, `profile.once.json`, `chain.ini`, `chain/once.ini`):
/// the result says which game, and that activating its profile again lets the old copy go at the next start. The move
/// itself runs this as its last phase ([`MoveResult::cleanup`]), unless a game's profile could not be written for the
/// new place. The result is kept in the journal until [`acknowledge`].
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
    // a game whose profile names a path under the old folder loads its files from there, whoever wrote that profile:
    // nothing goes while one does
    if let Some(why) = game_names_old_root(&DataDirs::at(&journal.to), &from) {
        return Ok(no(why));
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
    c.done = false;
    c.reason = None;
    // a call again (the next start) works the record out anew; only what never goes is carried over
    let stays: Vec<PathBuf> = unknown.iter().map(|u| from.join(u)).chain(journal.skipped.iter().map(|s| from.join(s))).collect();
    c.kept.retain(|k| stays.contains(&k.path) && k.path.exists());
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
            match remove_if_there(&old) {
                Ok(()) => {
                    c.removed_files += 1;
                    c.removed_bytes += om.len();
                }
                Err(e) => keep(&format!("it could not be removed: {}", e.message), &mut c),
            }
        }
        if i % 64 == 0 {
            progress.report(Some(i as f32 / n as f32), &format!("removing the old copy: {}", f.path));
        }
    }
    // what is left of the launcher's own entries: the hash cache (by its exact name, never in a `state` the old root
    // keeps for itself), lock files (`quick.lock`, `data-folder.lock`, `nexus/*.lock`), the old markers, then empty
    // folders. One that cannot be removed is kept, with the OS error (the next call tries again).
    let st = PathStyle::of(env);
    let is = |a: &str, b: &str| st.same_name(a, b);
    let file = |p: &Path| std::fs::symlink_metadata(p).is_ok_and(|m| m.is_file());
    let remove = |p: &Path, c: &mut Cleanup| {
        let bytes = std::fs::symlink_metadata(p).map(|m| m.len()).unwrap_or(0);
        if let Err(e) = remove_if_there(p) {
            c.kept.push(Kept { path: p.to_path_buf(), bytes, reason: format!("it could not be removed: {}", e.message) });
        }
    };
    let old_own = df::own_entries(&from, env);
    if !old_own.iter().any(|o| is(o, df::HASH_CACHE[0])) {
        let cache = from.join(df::HASH_CACHE[0]).join(df::HASH_CACHE[1]);
        if file(&cache) {
            remove(&cache, &mut c);
        }
    }
    let recorded: BTreeSet<PathBuf> = record.files.iter().filter_map(|f| below(&from, &f.path)).collect();
    for e in journal.entries.iter().filter(|e| e.known) {
        let p = from.join(&e.name);
        if is(&e.name, "nexus") {
            for l in std::fs::read_dir(&p).into_iter().flatten().flatten() {
                if l.file_name().to_string_lossy().to_lowercase().ends_with(".lock") && file(&l.path()) {
                    remove(&l.path(), &mut c);
                }
            }
        }
        remove_empty_dirs(&p, &mut c.kept);
        report_unrecorded(&p, &recorded, &mut c.kept);
    }
    for name in ["quick.lock", df::LOCK, MARKER, MOVE_MARKER] {
        let p = from.join(name);
        if file(&p) {
            remove(&p, &mut c);
        }
    }
    // entries the target kept for itself were not copied: what is left of them stays (an empty folder goes)
    for name in &journal.skipped {
        let p = from.join(name);
        if old_own.iter().any(|o| is(o, name)) {
            continue;
        }
        remove_empty_dirs(&p, &mut c.kept);
        if let Ok(m) = std::fs::symlink_metadata(&p)
            && !c.kept.iter().any(|k| k.path == p)
        {
            let bytes = if m.is_dir() { dir_bytes(&p) } else { m.len() };
            c.kept.push(Kept { path: p, bytes, reason: "the new place keeps a folder of its own by this name, so it was not copied".into() });
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
    for e in std::fs::read_dir(&from).into_iter().flatten().flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        if !journal.entries.iter().any(|j| is(&j.name, &name)) && !journal.skipped.iter().any(|s| is(s, &name))
            && !old_own.iter().any(|o| is(o, &name)) && !df::is_own_file(&name, st) && !df::is_top_lock(&name, st)
        {
            report_unrecorded(&e.path(), &recorded, &mut c.kept);
        }
    }
    if !same_folder(&from, &default) && std::fs::read_dir(&from).is_ok_and(|mut d| d.next().is_none()) {
        match std::fs::remove_dir(&from) {
            Ok(()) => c.folder_removed = true,
            Err(e) => c.kept.push(Kept { path: from.clone(), bytes: 0, reason: format!("the empty folder could not be removed: {e}") }),
        }
    }
    c.done = true;
    save_result(&default, &mut journal, &c, "cleaned");
    progress.report(Some(1.0), "the old copy is removed");
    Ok(c)
}

/// Why the old copy must stay because a known game still reads from it: its `kiln/profile.json`, `kiln/profile.once.json`,
/// `kiln/chain.ini` or `kiln/chain/once.ini` names a path under `old` (Z and native mode), whoever wrote it, or its
/// profile file cannot be read (then Kiln cannot tell). None: no game names it.
fn game_names_old_root(dirs: &DataDirs, old: &Path) -> Option<String> {
    let o = PathParts::of(&df::canonical(old), style());
    let games = match crate::game::known(dirs) {
        Ok(g) => g,
        Err(e) => {
            return Some(format!(
                "The list of known games cannot be read ({}), so Kiln cannot tell whether a game still uses the old folder: it stays.",
                e.message
            ));
        }
    };
    for g in games {
        let under = |path: &str| {
            let p = path.trim();
            (crate::winpath::is_absolute_game_path(p) || p.starts_with('/'))
                && crate::winpath::to_host(p, &g).is_ok_and(|h| PathParts::of(&df::canonical(&h), style()).within(&o))
        };
        let says = |file: &Path| {
            format!(
                "The game at {} still runs a profile that names the old folder ({}): activate that profile again from the new place, then the old copy goes at the next start.",
                g.path.display(),
                file.display()
            )
        };
        for rel in [crate::paths::game::PROFILE, crate::paths::game::PROFILE_ONCE] {
            let file = g.path.join(rel);
            match crate::util::read_json::<crate::profile::ProfileFile>(&file) {
                Ok(None) => {}
                Ok(Some(f)) => {
                    let mut paths: Vec<&str> = f.config_dir.iter().chain(f.data_dir.iter()).map(String::as_str).collect();
                    for k in crate::manifest::ModKind::ALL {
                        paths.extend(f.mods.folders(k).into_iter().flatten().map(|m| m.path.as_str()));
                    }
                    paths.extend(f.mods.plugin.iter().map(|m| m.path.as_str()));
                    if paths.into_iter().any(under) {
                        return Some(says(&file));
                    }
                }
                Err(e) => {
                    return Some(format!(
                        "{} cannot be read ({}), so Kiln cannot tell whether the game still uses the old folder: it stays.",
                        file.display(),
                        e.message
                    ));
                }
            }
        }
        let chain = crate::chain::read(&g.path).map(|c| c.profile).unwrap_or_default();
        let once = std::fs::read(g.path.join(crate::paths::game::CHAIN_ONCE)).map(|b| crate::chain::parse_once(&b).profile).unwrap_or_default();
        if chain.iter().chain(&once).any(|e| under(&e.path)) {
            return Some(says(&g.path.join(crate::paths::game::CHAIN)));
        }
    }
    None
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

/// Name additions under a recorded entry too: leaving a file must not hide the old folder from the notice.
fn report_unrecorded(p: &Path, recorded: &BTreeSet<PathBuf>, kept: &mut Vec<Kept>) {
    let Ok(m) = std::fs::symlink_metadata(p) else { return };
    #[cfg(windows)]
    let link = { use std::os::windows::fs::MetadataExt; m.file_attributes() & 0x400 != 0 };
    #[cfg(not(windows))]
    let link = m.file_type().is_symlink();
    if m.is_dir() && !link {
        match std::fs::read_dir(p) {
            Ok(entries) => for e in entries.flatten() { report_unrecorded(&e.path(), recorded, kept); },
            Err(e) => kept.push(Kept { path: p.to_path_buf(), bytes: 0, reason: format!("its remaining files could not be listed: {e}") }),
        }
    } else if !recorded.contains(p) && !kept.iter().any(|k| k.path == p) {
        kept.push(Kept { path: p.to_path_buf(), bytes: m.len(), reason: "it was not recorded by the move, so it stays in the old folder".into() });
    }
}

/// Removes the folders below and at `p` that hold nothing (bottom up). Never a file. A folder that holds nothing and
/// cannot be removed goes into `left`, with the OS error (one that holds anything stays without a word: what it holds
/// is named, or is not the launcher's).
fn remove_empty_dirs(p: &Path, left: &mut Vec<Kept>) {
    let Ok(m) = std::fs::symlink_metadata(p) else { return };
    if !m.is_dir() {
        return;
    }
    for e in std::fs::read_dir(p).into_iter().flatten().flatten() {
        remove_empty_dirs(&e.path(), left);
    }
    if let Err(e) = std::fs::remove_dir(p)
        && std::fs::read_dir(p).is_ok_and(|mut d| d.next().is_none())
    {
        left.push(Kept { path: p.to_path_buf(), bytes: 0, reason: format!("the empty folder could not be removed: {e}") });
    }
}

// ------------------------------------------------------------------------------------------------ a partial copy

/// Why a file or folder of a partial copy stays: the reasons a player reads in [`Cleanup::kept`].
pub mod partial_reason {
    /// The old folder has nothing (or only a link, or something that is not a plain file) at that path.
    pub const NO_SUCH_FILE: &str = "the old folder has no such file, so Kiln cannot tell that the move made it";
    /// Size or content differ from the old folder's file.
    pub const DIFFERS: &str = "it differs from the file in the old folder (added, changed or replaced after the move stopped, or the old folder's file changed since)";
    /// A symlink, a junction or another reparse point: never followed, never removed.
    pub const LINK: &str = "it is a link, which Kiln never follows or removes";
    /// Not a plain file or folder (a device, a pipe, a socket).
    pub const SPECIAL: &str = "it is not a plain file or folder";
    /// A top-level entry the journal does not list.
    pub const NOT_COPIED: &str = "the move copied nothing by this name";
    /// A file the move never copies (`nexus/*.lock`, `state/file-hashes.json`).
    pub const NEVER_COPIED: &str = "the move never copies this file";
    /// An empty folder the old folder does not have.
    pub const NO_SUCH_FOLDER: &str = "the old folder has no such folder, so Kiln cannot tell that the move made it";
    /// A leftover of the move's own marker writes whose content does not prove it is this move's.
    pub const NOT_THIS_MOVE: &str = "its content does not show that this move wrote it";
}

/// A symlink, or on Windows any reparse point (a junction too): never followed.
fn is_link_meta(m: &std::fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if m.file_attributes() & 0x400 != 0 {
            return true; // FILE_ATTRIBUTE_REPARSE_POINT
        }
    }
    m.file_type().is_symlink()
}

/// The removal of a partial copy by proof from the old folder (`from`, still the folder in use and complete).
struct Recovery<'a> {
    from: &'a Path,
    to: &'a Path,
    /// The old root as the move wrote it into a rewritten server record (as given, and canonical).
    olds: Vec<PathBuf>,
    style: PathStyle,
    total: u64,
    seen: u64,
    files: u64,
    c: Cleanup,
}

/// A cancel between two files.
struct Canceled;

impl Recovery<'_> {
    fn keep(&mut self, path: PathBuf, bytes: u64, reason: impl Into<String>) {
        self.c.kept.push(Kept { path, bytes, reason: reason.into() });
    }

    /// One entry below a journal entry of the target (`names` from the target root). `src_dir`: every folder above it
    /// in the old folder is a real folder (no link), so the old folder's path can prove what is here.
    fn visit(&mut self, names: &mut Vec<String>, src_dir: bool, progress: &mut dyn Progress) -> std::result::Result<(), Canceled> {
        let t = names.iter().fold(self.to.to_path_buf(), |p, n| p.join(n));
        let m = match std::fs::symlink_metadata(&t) {
            Ok(m) => m,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(e) => {
                self.keep(t, 0, format!("it could not be inspected: {e}"));
                return Ok(());
            }
        };
        if is_link_meta(&m) {
            self.keep(t, m.len(), partial_reason::LINK);
        } else if m.is_dir() {
            let s = names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
            let s_dir = src_dir && std::fs::symlink_metadata(&s).is_ok_and(|sm| sm.is_dir() && !is_link_meta(&sm));
            let mut kids: Vec<String> = match std::fs::read_dir(&t) {
                Ok(rd) => rd.flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect(),
                Err(e) => {
                    self.keep(t, 0, format!("its contents could not be listed: {e}"));
                    return Ok(());
                }
            };
            kids.sort();
            for k in kids {
                names.push(k);
                let r = self.visit(names, s_dir, progress);
                names.pop();
                r?;
            }
            // a folder goes when nothing is left in it and the old folder has it; one that holds anything stays
            // without a word (what it holds is named)
            if std::fs::read_dir(&t).is_ok_and(|mut d| d.next().is_none()) {
                if !s_dir {
                    self.keep(t, 0, partial_reason::NO_SUCH_FOLDER);
                } else if let Err(e) = std::fs::remove_dir(&t) {
                    self.keep(t, 0, format!("it could not be removed: {e}"));
                }
            }
        } else if m.is_file() {
            if progress.canceled() {
                return Err(Canceled);
            }
            let rel = rel_of(names);
            self.files += 1;
            self.seen += m.len();
            // every 64th file, and each large one (its content may be read): a report with a new text is an event
            if self.files % 64 == 1 || m.len() >= 4 << 20 {
                progress.report(Some((self.seen as f32 / self.total.max(1) as f32).min(1.0)), &format!("checking {rel}"));
            }
            match self.proof(names, &t, &m, src_dir) {
                Ok(()) => match std::fs::remove_file(&t) {
                    Ok(()) => {
                        self.c.removed_files += 1;
                        self.c.removed_bytes += m.len();
                    }
                    Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                    Err(e) => self.keep(t, m.len(), format!("it could not be removed: {e}")),
                },
                Err(why) => self.keep(t, m.len(), why),
            }
        } else {
            self.keep(t, m.len(), partial_reason::SPECIAL);
        }
        Ok(())
    }

    /// Ok when the old folder proves the move made the file at `t`: a plain file at the same path (no link on the way)
    /// with the same size and sha256, the same file (a hard link), or for `servers/<id>.json` what the move writes
    /// from the old folder's file. Err: why it stays.
    fn proof(&self, names: &[String], t: &Path, tm: &std::fs::Metadata, src_dir: bool) -> std::result::Result<(), String> {
        let never = (names.len() == 2 && self.style.same_name(&names[0], "nexus") && names[1].to_lowercase().ends_with(".lock"))
            || (names.len() == 2 && self.style.same_name(&names[0], df::HASH_CACHE[0]) && self.style.same_name(&names[1], df::HASH_CACHE[1]));
        if never {
            return Err(partial_reason::NEVER_COPIED.into());
        }
        if !src_dir {
            return Err(partial_reason::NO_SUCH_FILE.into());
        }
        let s = names.iter().fold(self.from.to_path_buf(), |p, n| p.join(n));
        let sm = match std::fs::symlink_metadata(&s) {
            Ok(sm) if sm.is_file() && !is_link_meta(&sm) => sm,
            _ => return Err(partial_reason::NO_SUCH_FILE.into()),
        };
        // the same file under two names (a hard link the move made): proof without reading it
        if same_inode(tm, &sm) {
            return Ok(());
        }
        let compare = |e: std::io::Error| format!("Kiln could not compare it with the old folder's file: {e}");
        // servers/<id>.json: the move wrote it with backupDir rewritten from the old root to the new one
        if names.len() == 2 && names[0] == "servers" && names[1].ends_with(".json")
            && let Ok(text) = std::fs::read_to_string(&s)
            && let Some(written) = rewrite_root_field(&text, "backupDir", &self.olds, self.to)
        {
            if tm.len() != written.len() as u64 {
                return Err(partial_reason::DIFFERS.into());
            }
            let here = std::fs::read(t).map_err(compare)?;
            return if here == written.as_bytes() { Ok(()) } else { Err(partial_reason::DIFFERS.into()) };
        }
        if tm.len() != sm.len() {
            return Err(partial_reason::DIFFERS.into());
        }
        let hash = |p: &Path| crate::hash::sha256_file(p).map(|(h, _)| h).map_err(|e| format!("Kiln could not compare it with the old folder's file: {}", e.message));
        if hash(t)? == hash(&s)? { Ok(()) } else { Err(partial_reason::DIFFERS.into()) }
    }
}

/// A leftover of the move's own atomic marker writes at the target's top (`<marker>.tmp`): the move's only when its
/// content shows it. A move marker's carries this journal's id; a folder marker's (it has no id) was created at or
/// after the move started.
fn own_marker_tmp(p: &Path, name: &str, journal: &Journal, style: PathStyle) -> Option<bool> {
    if style.same_name(name, &format!("{MOVE_MARKER}.tmp")) {
        return Some(crate::util::read_json::<MoveMark>(p).ok().flatten().is_some_and(|m| m.format == 1 && m.id == journal.id));
    }
    if style.same_name(name, &format!("{MARKER}.tmp")) {
        return Some(crate::util::read_json::<FolderMark>(p).ok().flatten().is_some_and(|m| m.format == 1 && m.created >= journal.started));
    }
    None
}

/// The partial copy of a move that did not commit, by proof: only with the target's [`MOVE_MARKER`] (this journal's
/// id), and only what the old folder (`from`, still in use and complete) proves the move made: a plain file below one
/// of the journal's entries goes when the old folder has a plain file at the same path with the same size and sha256,
/// or the same file (a hard link), or for `servers/<id>.json` exactly what the move writes from the old folder's
/// file. A folder goes when it is empty after that and the old folder has it. Nothing is read through a link, here or
/// in the old folder. Everything else stays and is named in `kept` ([`partial_reason`]): a file the old folder does
/// not have or that differs, a link, a special file, an empty folder the old folder does not have, a top-level entry
/// the journal does not list. The launcher's own files at the top (lock files, the pointer and the journal at a
/// default root, the entry holding Kiln's state) are not the copy's and stay without a word. Then, only when nothing
/// was kept, the proof ([`remove_proof`]). A cancel (between two files) stops it with the marker and the journal kept:
/// the next call goes on. A target that is gone, or that the player emptied by hand (no marker, nothing in it but the
/// launcher's own files: the lock file the move made, at a default root the pointer and the journal), ends the move
/// cleanly: the folder goes when the move made it and is empty, the records go.
fn remove_partial(env: &DataEnv, default: &Path, journal: &Journal, progress: &mut dyn Progress) -> Cleanup {
    let to = &journal.to;
    let mut c = Cleanup { folder: to.clone(), done: false, interrupted: false, removed_files: 0, removed_bytes: 0, folder_removed: false, kept: vec![], reason: None };
    if may_have_committed(journal) {
        c.reason = Some(format!("{} may hold a committed data folder: nothing was removed, and the move's journal stays.", to.display()));
        return c;
    }
    let drop_records = || {
        let _ = remove_if_there(&record_path(default));
        let _ = remove_if_there(&journal_path(default));
    };
    match std::fs::symlink_metadata(to) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => {
            // never made: nothing to remove
            drop_records();
            c.done = true;
            c.folder_removed = true;
            return c;
        }
        Err(e) => {
            c.reason = Some(format!("{} cannot be inspected ({e}): nothing was removed, and the move's records stay.", to.display()));
            return c;
        }
        Ok(_) => {}
    }
    let style = PathStyle::of(env);
    let own = df::own_entries(to, env);
    // the launcher's own files and entries at the top (lock files; at a default root the pointer, the journal and the
    // file record, the entry holding Kiln's state): never the copy's
    let launchers = |name: &str, m: Option<&std::fs::Metadata>| {
        let plain = m.is_some_and(|m| m.is_file() && !is_link_meta(m));
        (plain && (df::is_own_file(name, style) || df::is_top_lock(name, style))) || own.iter().any(|o| style.same_name(o, name))
    };
    if move_mark_id(to).as_deref() != Some(journal.id.as_str()) {
        // a target the player emptied by hand, its marker too: nothing of the copy is in it, nothing the launcher could
        // remove wrongly. The lock file the move made and the folder it made go, then the records: the move is finished
        let names: Option<Vec<String>> = std::fs::read_dir(to).ok().map(|rd| rd.flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect());
        if let Some(names) = names.filter(|v| v.iter().all(|n| launchers(n, std::fs::symlink_metadata(to.join(n)).ok().as_ref()))) {
            if journal.created_lock
                && let Some(n) = names.iter().find(|n| style.same_name(n, df::LOCK))
                && let Err(e) = remove_if_there(&to.join(n))
            {
                c.reason = Some(format!("{} holds nothing of the move, but the lock file it made could not be removed ({}): the move's records stay.", to.display(), e.message));
                return c;
            }
            if journal.created_target {
                c.folder_removed = std::fs::remove_dir(to).is_ok();
            }
            for p in journal.created_parents.iter().rev() {
                let _ = std::fs::remove_dir(p);
            }
            drop_records();
            c.done = true;
            return c;
        }
        c.reason = Some(format!("{} does not carry this move's marker, so nothing was removed from it.", to.display()));
        return c;
    }
    let names: Vec<String> = match std::fs::read_dir(to) {
        Ok(rd) => {
            let mut v: Vec<String> = rd.flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
            v.sort();
            v
        }
        Err(e) => {
            c.reason = Some(format!("{} cannot be listed ({e}): nothing was removed, and the move's records stay.", to.display()));
            return c;
        }
    };
    let mut olds = vec![journal.from.clone(), df::canonical(&journal.from)];
    olds.dedup();
    let mut r = Recovery { from: &journal.from, to, olds, style, total: journal.entries.iter().map(|e| e.bytes).sum(), seen: 0, files: 0, c };
    progress.report(Some(0.0), &format!("removing the partial copy at {}", to.display()));
    for name in names {
        let p = to.join(&name);
        if journal.entries.iter().any(|e| style.same_name(&e.name, &name)) {
            if r.visit(&mut vec![name], true, progress).is_err() {
                r.c.interrupted = true;
                return r.c;
            }
            continue;
        }
        let m = std::fs::symlink_metadata(&p).ok();
        let plain = m.as_ref().is_some_and(|m| m.is_file() && !is_link_meta(m));
        if style.same_name(&name, MOVE_MARKER) || style.same_name(&name, MARKER) {
            continue; // the proof: last, by remove_proof
        }
        match own_marker_tmp(&p, &name, journal, style) {
            Some(true) if plain => {
                if let Err(e) = remove_if_there(&p) {
                    r.keep(p, m.map_or(0, |m| m.len()), format!("it could not be removed: {}", e.message));
                }
                continue;
            }
            Some(_) => {
                r.keep(p, m.map_or(0, |m| m.len()), partial_reason::NOT_THIS_MOVE);
                continue;
            }
            None => {}
        }
        if launchers(&name, m.as_ref()) {
            continue;
        }
        let bytes = match &m {
            Some(m) if m.is_dir() && !is_link_meta(m) => dir_bytes(&p),
            Some(m) => m.len(),
            None => 0,
        };
        let why = if m.as_ref().is_some_and(is_link_meta) { partial_reason::LINK } else { partial_reason::NOT_COPIED };
        r.keep(p, bytes, why);
    }
    let mut c = r.c;
    remove_proof(default, journal, &mut c);
    progress.report(Some(1.0), if c.done { "the partial copy is removed" } else { "the partial copy is not removed in full" });
    c
}

/// Removes the partial copy of a move that did not commit ([`pending`] says `Uncommitted`) by `remove_partial`'s rule:
/// only what the old folder proves the move made; everything else stays and is named, and the marker and the journal
/// stay for the next call. Reports a fraction and the file it checks; a cancel between two files returns `interrupted`
/// (the next call goes on). The result is kept in the journal when not done.
pub fn remove_partial_copy_with(env: &DataEnv, progress: &mut dyn Progress) -> Result<Cleanup> {
    let default = default_root(env)?;
    match pending(env)? {
        Pending::Uncommitted { journal } => {
            let mut journal = journal;
            let c = remove_partial(env, &default, &journal, progress);
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

/// [`remove_partial_copy_with`] without progress or cancel.
pub fn remove_partial_copy(env: &DataEnv) -> Result<Cleanup> {
    remove_partial_copy_with(env, &mut crate::task::NoProgress)
}

/// The launcher has shown the result: the journal and the file record go, and the new place's [`MOVE_MARKER`] (only
/// when it carries this journal's id). Nothing of the data is removed; an old copy still there stays where it is.
/// Refused while the partial copy of a move that did not commit still carries this journal's marker (a clean-up that
/// left something): the marker and the journal are the proof [`remove_partial_copy`] removes the rest with at the next
/// start, and without them the folder would stay marked as an unfinished move that nothing can finish.
pub fn acknowledge(env: &DataEnv) -> Result<()> {
    let default = default_root(env)?;
    if let Pending::Unclear { reason, .. } = pending(env)? {
        return Err(Error::new(ErrorCode::Refused, format!("{reason} The move's journal and file record stay until its data folder is in use again.")));
    }
    if let Ok(Some(j)) = read_journal(env)
        && move_mark_id(&j.to).as_deref() == Some(j.id.as_str())
    {
        match pending(env)? {
            Pending::Committed { .. } => remove_if_there(&j.to.join(MOVE_MARKER))?,
            Pending::Uncommitted { .. } => {
                return Err(Error::new(ErrorCode::Refused, format!(
                    "{} still holds what an unfinished move made there: the next start removes it, and the move's journal stays until then.",
                    j.to.display()
                ))
                .with_detail(j.to.display().to_string()));
            }
            _ => {}
        }
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
