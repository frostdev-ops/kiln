//! The launcher's data folder as a whole (dev.25, package `data-folder`): what a folder is ([`recognize`]), what the
//! data folder holds ([`inventory`]), and whether a folder the player picks can take it ([`check`]: a [`Plan`], or a
//! [`Refusal`] with a stable code and one plain sentence).
//!
//! The environment comes in as a value ([`DataEnv`]; its `windows` flag picks the platform's path rules, so the
//! Windows rules also run in a test on Linux). Facts from the system (running game or server, free space, writing,
//! volumes, hard links) come in through [`SystemFacts`]: [`Facts`] is a set of values for tests, [`RealFacts`] asks
//! the system. Path comparison is done on [`PathParts`] (string rules, case-insensitive for Windows), over canonical
//! paths where the folder exists.
//!
//! Decided for dev.25 (2026-11-11): the whole data folder moves as one; a pointer file [`POINTER`] in the default root
//! names it; `$KILN_DATA_DIR` wins over the pointer, the pointer over the default. Writing the pointer, the lock
//! ([`LOCK`]), the journal ([`JOURNAL`]), the marker ([`MARKER`]) and the move itself are not here (`core-2`).

use crate::game::{GameInstall, PathMode};
use crate::paths::{DataDirs, DataEnv, Located, RootSource};
use crate::util::io_at;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// The pointer file in the default root that names the data folder after a move ([`Pointer`]). No pointer file: the
/// default root is the data folder.
pub const POINTER: &str = "data-folder.json";
/// What [`crate::data_folder_move::reset`] renames a broken pointer to.
pub const POINTER_OLD: &str = "data-folder.json.old";
/// The move's journal in the default root (`data_folder_move::Journal`).
pub const JOURNAL: &str = "data-folder.move.json";
/// Every file a move copied (`data_folder_move::FileRecord`), in the default root, written once before the pointer.
pub const FILE_RECORD: &str = "data-folder.move.files.json";
/// The file that marks a folder as a Kiln data folder (`{format, created}`): a move writes it into the new place just
/// before the pointer. Folders made before dev.25 have none and are recognised by their layout.
pub const MARKER: &str = ".kiln-data-folder.json";
/// The mark of a move in progress (`{format, id}`): in the target from the first moment a move writes there until the
/// move is acknowledged. A folder with it is never "use this one" and never a target.
pub const MOVE_MARKER: &str = ".kiln-data-move.json";
/// The data folder's own lock ([`DataLock`]): shared by every launcher process from dev.25 on, exclusive by a move.
pub const LOCK: &str = "data-folder.lock";

/// The top-level entries a launcher data folder has (contracts.md section 3, and the launcher's and the `kiln` CLI's
/// own folders), with whether each is a folder. Lock files and the files of the default root ([`POINTER`],
/// [`JOURNAL`]) are not in it: they are not data.
pub const KNOWN_ENTRIES: [(&str, bool); 19] = [
    ("settings.json", false),
    ("library", true),
    ("profiles", true),
    ("installs", true),
    ("runtime", true),
    ("schema-cache", true),
    ("backups", true),
    ("logs", true),
    ("update.json", false),
    ("servers", true),
    ("modpacks", true),
    ("nexus", true),
    ("catalog", true),
    ("editor-assets", true),
    ("admin-assets", true),
    ("sdk", true),
    ("tools", true),
    ("state", true),
    (MARKER, false),
];

/// Files of a data folder that are written in place (appended, or truncated and written again, without a temporary
/// file and a rename), by the launcher, the `kiln` CLI or the game: a move copies them for real and never hard-links
/// them, so a write through the new place never changes the old copy. `names`: the path below the data root.
/// - `logs/**`: the launcher's log (`launcher_log` appends);
/// - `profiles/<pid>/config/**`: copy mode's `profile::copy_back` copies the game's files over them, and in Z and
///   native mode the game writes mod configs there; `profiles/<pid>/data/**`: the game writes per-mod data;
/// - `admin-assets/**`, `editor-assets/**`: the launcher's asset imports copy files over existing ones;
/// - `sdk/**`: the `kiln` CLI unpacks the SDK with `unzip -o` over an earlier one;
/// - `modpacks/downloads/*.part`, `nexus/downloads/**`: downloads that may be under way.
pub fn written_in_place(names: &[String], style: PathStyle) -> bool {
    let is = |i: usize, b: &str| names.get(i).is_some_and(|n| style.same_name(n, b));
    is(0, "logs")
        || is(0, "admin-assets")
        || is(0, "editor-assets")
        || is(0, "sdk")
        || (is(0, "profiles") && names.len() >= 3 && (is(2, "config") || is(2, "data")))
        || (is(0, "modpacks") && is(1, "downloads") && names.last().is_some_and(|n| n.to_lowercase().ends_with(".part")))
        || (is(0, "nexus") && is(1, "downloads"))
}

/// `<default root>/data-folder.json`: where the data folder is when it is not the default root.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Pointer {
    pub format: u32,
    /// The data folder: absolute, a host path, without `\\?\`.
    pub path: PathBuf,
    /// RFC 3339 UTC.
    pub written: String,
    /// `kiln-manage <version>`.
    pub writer: String,
}

/// Reads the pointer in `default_root`: Ok(None) when there is none. A file that cannot be read or parsed, has a
/// larger `format`, or names a relative path is a problem ([`crate::paths::LocateProblem`]). The folder it names is
/// not looked at here.
pub fn read_pointer(default_root: &Path) -> std::result::Result<Option<Pointer>, crate::paths::LocateProblem> {
    use crate::paths::{LocateProblem, ProblemKind};
    let file = default_root.join(POINTER);
    let problem = |kind, reason: String, path: Option<PathBuf>| LocateProblem { kind, pointer: Some(file.clone()), path, reason };
    let bytes = match std::fs::read(&file) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(problem(ProblemKind::Unreadable, format!("Kiln cannot read the data folder's pointer {}: {e}", file.display()), None)),
    };
    let bytes = bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes);
    let v: serde_json::Value = serde_json::from_slice(bytes)
        .map_err(|e| problem(ProblemKind::Unparsable, format!("The data folder's pointer {} is not valid JSON ({e}).", file.display()), None))?;
    if v.get("format").and_then(|f| f.as_u64()).is_some_and(|f| f > 1) {
        return Err(problem(ProblemKind::NewerFormat, format!("The data folder's pointer {} was written by a newer Kiln Launcher.", file.display()), None));
    }
    let p: Pointer = serde_json::from_value(v)
        .map_err(|e| problem(ProblemKind::Unparsable, format!("The data folder's pointer {} is not a pointer ({e}).", file.display()), None))?;
    if !p.path.is_absolute() || p.path.to_string_lossy().starts_with(r"\\?\") {
        return Err(problem(ProblemKind::NotAbsolute, format!("The data folder's pointer {} names {}, which is not a full path.", file.display(), p.path.display()), Some(p.path)));
    }
    Ok(Some(p))
}

/// Writes the pointer naming `path` into `default_root`, atomically (a temporary file in the same folder, flushed,
/// renamed over the old one).
pub fn write_pointer(default_root: &Path, path: &Path) -> Result<Pointer> {
    let p = Pointer {
        format: 1,
        path: crate::steam::strip_verbatim(path.to_path_buf()),
        written: crate::util::now_rfc3339(),
        writer: format!("kiln-manage {}", crate::VERSION),
    };
    let mut bytes = serde_json::to_vec_pretty(&p)?;
    bytes.push(b'\n');
    write_pointer_bytes(default_root, &bytes)?;
    Ok(p)
}

/// Pointer-only atomic replacement. On Windows the rename is write-through before old data may be removed.
pub(crate) fn write_pointer_bytes(default_root: &Path, bytes: &[u8]) -> Result<()> {
    #[cfg(not(windows))]
    { crate::util::write_atomic(&default_root.join(POINTER), bytes) }
    #[cfg(windows)]
    {
        use std::io::Write;
        std::fs::create_dir_all(default_root).map_err(io_at(default_root, "creating"))?;
        let tmp = default_root.join(format!("{POINTER}.{}.tmp", crate::hash::hex(&crate::util::random_bytes::<16>()?)));
        let mut file = std::fs::OpenOptions::new().write(true).create_new(true).open(&tmp).map_err(io_at(&tmp, "writing"))?;
        let result = (|| {
            file.write_all(bytes).map_err(io_at(&tmp, "writing"))?;
            file.sync_all().map_err(io_at(&tmp, "flushing"))?;
            drop(file);
            pointer_rename(&tmp, &default_root.join(POINTER), true)
        })();
        if result.is_err() { let _ = std::fs::remove_file(&tmp); }
        result
    }
}

#[cfg(windows)]
fn pointer_rename(from: &Path, to: &Path, replace: bool) -> Result<()> {
    use windows_sys::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH};
    let from = sys::wide(from);
    let dest = sys::wide(to);
    let flags = MOVEFILE_WRITE_THROUGH | if replace { MOVEFILE_REPLACE_EXISTING } else { 0 };
    // SAFETY: both paths are live NUL-terminated UTF-16 buffers; no handles are retained.
    if unsafe { MoveFileExW(from.as_ptr(), dest.as_ptr(), flags) } == 0 {
        return Err(io_at(to, "renaming to")(std::io::Error::last_os_error()));
    }
    Ok(())
}

/// The folder a pointer names must be a real folder and a Kiln data folder: it has [`MARKER`] (one stat), else its
/// layout says so ([`recognize`]: `Kiln`, or a committed move). `pointer`: the pointer file, for the problem.
pub fn check_pointed(path: &Path, env: &DataEnv, pointer: &Path) -> std::result::Result<(), crate::paths::LocateProblem> {
    use crate::paths::{LocateProblem, ProblemKind};
    let problem = |kind, reason: String| LocateProblem { kind, pointer: Some(pointer.to_path_buf()), path: Some(path.to_path_buf()), reason };
    let shown = path.display();
    match std::fs::symlink_metadata(path) {
        Ok(m) if is_link(&m) => return Err(problem(ProblemKind::NotAFolder, format!("The data folder {shown} is a link: it must be a real folder."))),
        Ok(m) if !m.is_dir() => return Err(problem(ProblemKind::NotAFolder, format!("The data folder {shown} is a file, not a folder."))),
        Ok(_) => {}
        Err(e) if matches!(e.kind(), std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory) => {
            return Err(problem(ProblemKind::Missing, format!("The data folder {shown} is not there (a disk that is not connected, or a folder that was moved or deleted).")));
        }
        Err(e) => return Err(problem(ProblemKind::FolderUnreadable, format!("Kiln cannot read the data folder {shown}: {e}."))),
    }
    if std::fs::symlink_metadata(path.join(MARKER)).is_ok_and(|m| m.is_file()) {
        return Ok(());
    }
    match recognize(path, env) {
        Ok(FolderState::Kiln { .. } | FolderState::Moving { committed: true, .. }) => Ok(()),
        Ok(_) => Err(problem(ProblemKind::NotKiln, format!("{shown} is not a Kiln data folder."))),
        Err(e) => Err(problem(ProblemKind::FolderUnreadable, format!("Kiln cannot read the data folder {shown}: {}.", e.message))),
    }
}

/// Removes the pointer in `default_root` (the default root becomes the data folder). Ok when there is none.
pub fn remove_pointer(default_root: &Path) -> Result<()> {
    let f = default_root.join(POINTER);
    #[cfg(windows)]
    {
        match std::fs::symlink_metadata(&f) {
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
            Err(e) => return Err(io_at(&f, "reading")(e)),
            Ok(_) => {}
        }
        let tombstone = default_root.join(format!("{POINTER}.{}.removed", crate::hash::hex(&crate::util::random_bytes::<16>()?)));
        // Never replace a pre-existing tombstone. The durable rename, not its later deletion, is the commit.
        pointer_rename(&f, &tombstone, false)?;
        let _ = std::fs::remove_file(&tombstone);
        return Ok(());
    }
    #[cfg(not(windows))]
    match std::fs::remove_file(&f) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(io_at(&f, "removing")(e)),
    }
}

/// The data folder's lock ([`LOCK`] in the data root), held as a value: released when dropped. Every launcher process
/// from dev.25 on holds it shared for as long as it runs; a move holds it exclusively. Taking it never waits. A
/// launcher before dev.25 takes no such lock.
#[derive(Debug)]
pub struct DataLock {
    file: std::fs::File,
    path: PathBuf,
    exclusive: bool,
}

impl DataLock {
    fn open(root: &Path) -> Result<(std::fs::File, PathBuf)> {
        let path = root.join(LOCK);
        if std::fs::symlink_metadata(&path).is_ok_and(|m| is_link(&m) || !m.is_file()) {
            return Err(Error::new(ErrorCode::Refused, format!("{}: the data folder's lock must be a regular file", path.display())));
        }
        let file = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&path).map_err(io_at(&path, "opening"))?;
        Ok((file, path))
    }

    /// Shared, for a launcher process. None: a move holds it.
    pub fn shared(root: &Path) -> Result<Option<DataLock>> {
        let (file, path) = Self::open(root)?;
        match file.try_lock_shared() {
            Ok(()) => Ok(Some(DataLock { file, path, exclusive: false })),
            Err(std::fs::TryLockError::WouldBlock) => Ok(None),
            Err(std::fs::TryLockError::Error(e)) => Err(io_at(&path, "locking")(e)),
        }
    }

    /// Exclusive, for a move. None: another launcher (or a move) holds it.
    pub fn exclusive(root: &Path) -> Result<Option<DataLock>> {
        let (file, path) = Self::open(root)?;
        match file.try_lock() {
            Ok(()) => Ok(Some(DataLock { file, path, exclusive: true })),
            Err(std::fs::TryLockError::WouldBlock) => Ok(None),
            Err(std::fs::TryLockError::Error(e)) => Err(io_at(&path, "locking")(e)),
        }
    }

    pub fn is_exclusive(&self) -> bool {
        self.exclusive
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Shared to exclusive (this process's own shared lock, for its move). Ok(false): another launcher holds it too;
    /// the lock is shared again then.
    pub fn upgrade(&mut self) -> Result<bool> {
        if self.exclusive {
            return Ok(true);
        }
        self.file.unlock().map_err(io_at(&self.path, "unlocking"))?;
        match self.file.try_lock() {
            Ok(()) => {
                self.exclusive = true;
                Ok(true)
            }
            Err(e) => {
                let _ = self.file.try_lock_shared();
                match e {
                    std::fs::TryLockError::WouldBlock => Ok(false),
                    std::fs::TryLockError::Error(e) => Err(io_at(&self.path, "locking")(e)),
                }
            }
        }
    }

    /// Exclusive back to shared.
    pub fn downgrade(&mut self) -> Result<()> {
        if !self.exclusive {
            return Ok(());
        }
        self.file.unlock().map_err(io_at(&self.path, "unlocking"))?;
        self.exclusive = false;
        match self.file.try_lock_shared() {
            Ok(()) => Ok(()),
            Err(std::fs::TryLockError::WouldBlock) => Err(Error::new(ErrorCode::Busy, format!("{}: another process took the lock", self.path.display()))),
            Err(std::fs::TryLockError::Error(e)) => Err(io_at(&self.path, "locking")(e)),
        }
    }
}

/// The most a path the game opens without a `\\?\` prefix may have on Windows: MAX_PATH (260) less the closing NUL.
pub const WINDOWS_MAX_PATH: usize = 259;

/// Free space a move keeps on the target beyond what it copies: 256 MiB, plus 5 % of the bytes ([`needed_bytes`]).
pub const MARGIN_BYTES: u64 = 256 * 1024 * 1024;

// ------------------------------------------------------------------------------------------------ path rules

/// Which rules a path string follows.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum PathStyle {
    /// Drive letters, `\` and `/`, UNC, `\\?\`, names compared without case.
    Windows,
    /// `/`, names compared exactly.
    Unix,
}

impl PathStyle {
    /// The style of an environment ([`DataEnv::windows`]).
    pub fn of(env: &DataEnv) -> PathStyle {
        if env.windows { PathStyle::Windows } else { PathStyle::Unix }
    }

    /// The name of the subfolder a non-empty picked folder gets (`Kiln` on Windows, `kiln` elsewhere).
    pub fn subfolder(self) -> &'static str {
        match self {
            PathStyle::Windows => "Kiln",
            PathStyle::Unix => "kiln",
        }
    }

    /// Two folder names are the same name.
    pub fn same_name(self, a: &str, b: &str) -> bool {
        match self {
            PathStyle::Windows => a == b || a.to_lowercase() == b.to_lowercase(),
            PathStyle::Unix => a == b,
        }
    }
}

/// Where a path starts.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "kind")]
pub enum PathRoot {
    /// No root: relative to the current folder.
    Relative,
    /// `/` (Unix).
    Unix,
    /// `C:\` (the letter upper-cased).
    Drive { letter: char },
    /// `C:name`: relative to the current folder of drive C.
    DriveRelative { letter: char },
    /// `\name`: the root of the current drive.
    CurrentDrive,
    /// `\\server\share`, also written `\\?\UNC\server\share` or `//server/share`.
    Unc { server: String, share: String },
    /// `\\.\…`, `\\?\Volume{…}\…` and the other device forms, kept as written.
    Device { text: String },
}

/// A path split into its root and its names by string rules alone (no file system): `.` names dropped, `..` kept,
/// Windows' verbatim prefix `\\?\` taken off, and (outside a verbatim path) the trailing dots and spaces Windows
/// ignores taken off each name.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PathParts {
    pub style: PathStyle,
    pub root: PathRoot,
    pub names: Vec<String>,
}

fn is_sep(c: char) -> bool {
    c == '\\' || c == '/'
}

impl PathParts {
    pub fn parse(text: &str, style: PathStyle) -> PathParts {
        match style {
            PathStyle::Unix => {
                let root = if text.starts_with('/') { PathRoot::Unix } else { PathRoot::Relative };
                let names = text.split('/').filter(|n| !n.is_empty() && *n != ".").map(str::to_string).collect();
                PathParts { style, root, names }
            }
            PathStyle::Windows => Self::parse_windows(text),
        }
    }

    fn parse_windows(text: &str) -> PathParts {
        let style = PathStyle::Windows;
        let names_of = |rest: &str, verbatim: bool| -> Vec<String> {
            rest.split(['\\', '/'])
                .filter(|n| !n.is_empty() && *n != ".")
                .map(|n| {
                    if verbatim || n == ".." {
                        return n.to_string();
                    }
                    let t = n.trim_end_matches(['.', ' ']);
                    if t.is_empty() { n.to_string() } else { t.to_string() }
                })
                .collect()
        };
        /// `C:` at the start: the letter and what follows.
        fn drive(s: &str) -> Option<(char, &str)> {
            let mut it = s.chars();
            let l = it.next().filter(char::is_ascii_alphabetic)?;
            (it.next() == Some(':')).then(|| (l.to_ascii_uppercase(), &s[2..]))
        }
        let unc = |rest: &str, verbatim: bool| -> PathParts {
            let mut parts = names_of(rest, verbatim).into_iter();
            match (parts.next(), parts.next()) {
                (Some(server), Some(share)) => PathParts { style, root: PathRoot::Unc { server, share }, names: parts.collect() },
                _ => PathParts { style, root: PathRoot::Device { text: text.to_string() }, names: vec![] },
            }
        };
        if let Some(rest) = text.strip_prefix(r"\\?\") {
            if let Some(r) = rest.strip_prefix(r"UNC\").or_else(|| rest.strip_prefix("unc\\")) {
                return unc(r, true);
            }
            if let Some((letter, r)) = drive(rest)
                && (r.is_empty() || r.starts_with('\\'))
            {
                return PathParts { style, root: PathRoot::Drive { letter }, names: names_of(r, true) };
            }
            return PathParts { style, root: PathRoot::Device { text: text.to_string() }, names: vec![] };
        }
        let b: Vec<char> = text.chars().take(4).collect();
        if b.len() >= 2 && is_sep(b[0]) && is_sep(b[1]) {
            // `\\.\X`, `\\?\` with forward slashes: device namespaces
            if b.len() >= 3 && (b[2] == '.' || b[2] == '?') && (b.len() == 3 || is_sep(b[3])) {
                return PathParts { style, root: PathRoot::Device { text: text.to_string() }, names: vec![] };
            }
            return unc(&text[2..], false);
        }
        if let Some((letter, r)) = drive(text) {
            return match r.chars().next() {
                Some(c) if is_sep(c) => PathParts { style, root: PathRoot::Drive { letter }, names: names_of(r, false) },
                _ => PathParts { style, root: PathRoot::DriveRelative { letter }, names: names_of(r, false) },
            };
        }
        if text.starts_with(is_sep) {
            return PathParts { style, root: PathRoot::CurrentDrive, names: names_of(text, false) };
        }
        PathParts { style, root: PathRoot::Relative, names: names_of(text, false) }
    }

    /// A host path (lossy for a name that is not valid Unicode: two such paths compare as equal, never as different).
    pub fn of(path: &Path, style: PathStyle) -> PathParts {
        PathParts::parse(&path.to_string_lossy(), style)
    }

    /// A full path: `/…`, `C:\…` or `\\server\share\…`.
    pub fn is_absolute(&self) -> bool {
        matches!(self.root, PathRoot::Unix | PathRoot::Drive { .. } | PathRoot::Unc { .. })
    }

    pub fn has_parent_ref(&self) -> bool {
        self.names.iter().any(|n| n == "..")
    }

    /// A network folder: `\\server\share\…`.
    pub fn is_network(&self) -> bool {
        matches!(self.root, PathRoot::Unc { .. })
    }

    /// The root of a volume: `D:\`, `\\server\share`, `/`.
    pub fn is_volume_root(&self) -> bool {
        self.is_absolute() && self.names.is_empty()
    }

    /// The path as the platform writes it, without `\\?\`: `C:\a\b`, `\\server\share\a`, `/a/b`.
    pub fn text(&self) -> String {
        let sep = if self.style == PathStyle::Windows { "\\" } else { "/" };
        let joined = self.names.join(sep);
        match &self.root {
            PathRoot::Relative => joined,
            PathRoot::Unix => format!("/{joined}"),
            PathRoot::Drive { letter } => format!("{letter}:\\{joined}"),
            PathRoot::DriveRelative { letter } => format!("{letter}:{joined}"),
            PathRoot::CurrentDrive => format!("\\{joined}"),
            PathRoot::Unc { server, share } if joined.is_empty() => format!("\\\\{server}\\{share}"),
            PathRoot::Unc { server, share } => format!("\\\\{server}\\{share}\\{joined}"),
            PathRoot::Device { text } => text.clone(),
        }
    }

    /// The length Windows counts against MAX_PATH: UTF-16 code units of [`PathParts::text`].
    pub fn units(&self) -> usize {
        self.text().encode_utf16().count()
    }

    fn same_root(&self, other: &PathParts) -> bool {
        let n = |s: &str| if self.style == PathStyle::Windows { s.to_lowercase() } else { s.to_string() };
        match (&self.root, &other.root) {
            (PathRoot::Unc { server: a, share: b }, PathRoot::Unc { server: c, share: d }) => n(a) == n(c) && n(b) == n(d),
            (PathRoot::Device { text: a }, PathRoot::Device { text: b }) => n(a) == n(b),
            (a, b) => a == b,
        }
    }

    /// `self` is `other` or a folder below it.
    pub fn within(&self, other: &PathParts) -> bool {
        self.same_root(other)
            && self.names.len() >= other.names.len()
            && other.names.iter().zip(&self.names).all(|(a, b)| self.style.same_name(a, b))
    }

    /// `self` is a folder below `other` (not `other` itself).
    pub fn inside(&self, other: &PathParts) -> bool {
        self.names.len() > other.names.len() && self.within(other)
    }

    pub fn same(&self, other: &PathParts) -> bool {
        self.names.len() == other.names.len() && self.within(other)
    }
}

/// The checks on the picked path's form, before the file system is asked anything: it must be a full path, without
/// `..`, and (Windows) not a network folder. `\\?\` is taken off, not refused (`steam::strip_verbatim`).
pub fn check_form(picked: &str, style: PathStyle) -> std::result::Result<PathParts, Refusal> {
    let p = PathParts::parse(picked, style);
    if p.is_network() {
        return Err(Refusal::new(RefusalCode::NetworkPath, format!("{picked} is a network folder: pick a folder on a drive of this PC.")));
    }
    if !p.is_absolute() {
        return Err(Refusal::new(RefusalCode::NotAbsolute, format!("{picked} is not a full path to a folder.")));
    }
    if p.has_parent_ref() {
        return Err(Refusal::new(RefusalCode::ParentTraversal, format!("{picked} has '..' in it: pick the folder itself.")));
    }
    Ok(p)
}

/// The target against the current data folder: the same folder, inside it, or (with `contains`) holding it.
pub fn relation_to_current(target: &PathParts, current: &PathParts, contains: bool) -> Option<RefusalCode> {
    if target.same(current) {
        Some(RefusalCode::SameFolder)
    } else if target.inside(current) {
        Some(RefusalCode::InsideCurrent)
    } else if contains && current.inside(target) {
        Some(RefusalCode::ContainsCurrent)
    } else {
        None
    }
}

/// Windows: the longest path the game would open with the data folder at `target`, when it is longer than
/// [`WINDOWS_MAX_PATH`]: `target` + `\` + the inventory's longest relative path under `library/` or a profile's
/// `config/` and `data/`. None elsewhere, and when it fits.
pub fn game_path_too_long(target: &PathParts, longest_game: Option<&LongestPath>) -> Option<usize> {
    let rel = longest_game?;
    if target.style != PathStyle::Windows {
        return None;
    }
    let n = target.units() + 1 + rel.units;
    (n > WINDOWS_MAX_PATH).then_some(n)
}

fn is_link(meta: &std::fs::Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return true; // FILE_ATTRIBUTE_REPARSE_POINT: a junction or a symlink
        }
    }
    meta.file_type().is_symlink()
}

/// The canonical form of a path whose end may not exist yet: its nearest existing ancestor canonicalized (links
/// resolved, `\\?\` taken off), the rest appended. Only absolute paths reach it: `ancestors()` of `C:\a` stops at
/// `C:\` and never asks the file system about a bare `C:` (GitHub issue #1).
pub fn canonical(path: &Path) -> PathBuf {
    let mut tail: Vec<&std::ffi::OsStr> = Vec::new();
    let mut cur = path;
    loop {
        if let Ok(c) = std::fs::canonicalize(cur) {
            let mut out = crate::steam::strip_verbatim(c);
            out.extend(tail.iter().rev());
            return out;
        }
        match (cur.parent(), cur.file_name()) {
            (Some(parent), Some(name)) if !parent.as_os_str().is_empty() => {
                tail.push(name);
                cur = parent;
            }
            _ => return path.to_path_buf(),
        }
    }
}

// ------------------------------------------------------------------------------------------------ recognition

/// Why a folder counts as a Kiln data folder.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Evidence {
    /// It has the [`MARKER`] file.
    Marker,
    /// `library/<kind>/` with a mod kind's name.
    Library,
    /// `profiles/<pid>/meta.json`.
    Profiles,
    /// `installs/<id>.json`.
    Installs,
    /// `runtime/kiln-runtime-<version>.zip`.
    Runtime,
}

/// What a folder is ([`recognize`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "state")]
pub enum FolderState {
    /// Nothing at that path.
    Missing,
    /// A file (or another entry that is not a folder).
    NotAFolder,
    /// A link or a junction: a data root must be a real folder.
    Link,
    /// Nothing in it, or only what a default root keeps after a move: the pointer, the journal (and their `.tmp`),
    /// lock files (`*.lock`, a `nexus/` holding only `*.lock` files), and, when the folder is the default root, the
    /// entry that holds Kiln's state or settings folder (Windows: `state\`).
    Empty,
    /// A Kiln data folder: it has the marker, or the layout of one (see [`recognize`]).
    Kiln { by: Evidence },
    /// It holds [`MOVE_MARKER`]: a move writes into it, or wrote into it and was not acknowledged yet. `committed`: it
    /// has [`MARKER`] too (written just before the pointer). Never "use this one", never a target.
    Moving { id: String, committed: bool },
    /// Something else; `entry` is the first entry (by name) that is not a launcher entry, else the first entry.
    Other { entry: String },
}

fn names_in(dir: &Path) -> Result<Vec<String>> {
    let mut v: Vec<String> = std::fs::read_dir(dir)
        .map_err(io_at(dir, "reading"))?
        .map(|e| e.map(|e| e.file_name().to_string_lossy().into_owned()).map_err(io_at(dir, "reading")))
        .collect::<Result<_>>()?;
    v.sort();
    Ok(v)
}

/// The real folders (no links) and files directly in `dir`, by name; empty when `dir` is missing or unreadable.
fn children(dir: &Path) -> Vec<(String, std::fs::Metadata)> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Vec::new() };
    rd.flatten()
        .filter_map(|e| {
            let m = std::fs::symlink_metadata(e.path()).ok()?;
            (!is_link(&m)).then(|| (e.file_name().to_string_lossy().into_owned(), m))
        })
        .collect()
}

/// The layout of a launcher data folder (contracts.md section 3): one of the structures only the launcher makes.
fn evidence(dir: &Path, style: PathStyle) -> Option<Evidence> {
    let is = |a: &str, b: &str| style.same_name(a, b);
    let sub = |name: &str| -> Option<PathBuf> {
        children(dir).into_iter().find(|(n, m)| m.is_dir() && is(n, name)).map(|(n, _)| dir.join(n))
    };
    if let Some(lib) = sub("library")
        && children(&lib).iter().any(|(n, m)| m.is_dir() && crate::manifest::ModKind::ALL.iter().any(|k| is(n, k.as_str())))
    {
        return Some(Evidence::Library);
    }
    if let Some(profiles) = sub("profiles")
        && children(&profiles).iter().any(|(n, m)| {
            m.is_dir() && children(&profiles.join(n)).iter().any(|(f, fm)| fm.is_file() && is(f, crate::profile::META))
        })
    {
        return Some(Evidence::Profiles);
    }
    if let Some(installs) = sub("installs")
        && children(&installs).iter().any(|(n, m)| m.is_file() && n.to_lowercase().ends_with(".json"))
    {
        return Some(Evidence::Installs);
    }
    if let Some(runtime) = sub("runtime")
        && children(&runtime).iter().any(|(n, m)| {
            let l = n.to_lowercase();
            m.is_file() && l.starts_with("kiln-runtime-") && l.ends_with(".zip")
        })
    {
        return Some(Evidence::Runtime);
    }
    None
}

/// The top-level entries of `dir` that are or hold Kiln's state folder or settings folder of `env` (Windows:
/// `%LOCALAPPDATA%\Kiln\state` is `state` in the default root). They are not launcher data and never move; a move
/// never copies into them, replaces or removes them.
pub fn own_entries(dir: &Path, env: &DataEnv) -> Vec<String> {
    let style = PathStyle::of(env);
    let d = PathParts::of(&canonical(dir), style);
    [env.state_dir(), env.config_dir()]
        .iter()
        .filter_map(|s| {
            let s = PathParts::of(&canonical(s), style);
            s.inside(&d).then(|| s.names[d.names.len()].clone())
        })
        .collect()
}

fn is_lock_name(name: &str) -> bool {
    name.to_lowercase().ends_with(".lock")
}

/// The lock files at the top of a data folder: `quick.lock` and [`LOCK`], by their exact names. Any other `*.lock`
/// there is an entry the launcher does not know (copied, kept). Inside `nexus/` every `*.lock` is a lock file.
pub fn is_top_lock(name: &str, style: PathStyle) -> bool {
    style.same_name(name, "quick.lock") || style.same_name(name, LOCK)
}

/// The launcher's file-hash cache (`state/file-hashes.json`, keyed by absolute paths): never copied by a move.
pub const HASH_CACHE: [&str; 2] = ["state", "file-hashes.json"];

fn is_hash_cache(names: &[String], style: PathStyle) -> bool {
    names.len() == 2 && style.same_name(&names[0], HASH_CACHE[0]) && style.same_name(&names[1], HASH_CACHE[1])
}

/// What a default root keeps after a move ([`FolderState::Empty`]): the data-folder files, the two lock files, the
/// entry holding Kiln's state or settings folder. Nothing a move would write into.
fn is_leftover(dir: &Path, name: &str, own: &[String], style: PathStyle) -> bool {
    let is = |b: &str| style.same_name(name, b);
    if is_own_file(name, style) || own.iter().any(|o| is(o)) {
        return true;
    }
    is_top_lock(name, style) && std::fs::symlink_metadata(dir.join(name)).is_ok_and(|m| m.is_file() && !is_link(&m))
}

/// The entries of `dir` that make it not empty ([`FolderState::Empty`]'s leftovers left out), by name.
pub fn foreign_entries(dir: &Path, env: &DataEnv) -> Vec<String> {
    let style = PathStyle::of(env);
    let is_default = env.default_root().is_ok_and(|d| PathParts::of(&canonical(&d), style).same(&PathParts::of(&canonical(dir), style)));
    let own = if is_default { own_entries(dir, env) } else { Vec::new() };
    names_in(dir).unwrap_or_default().into_iter().filter(|n| !is_leftover(dir, n, &own, style)).collect()
}

fn is_known(name: &str, style: PathStyle) -> bool {
    KNOWN_ENTRIES.iter().any(|(k, _)| style.same_name(name, k))
}

/// What the folder at `dir` is, without following a link at `dir` itself (the platform's rules from `env`).
///
/// The rule for a Kiln data folder: it has the [`MARKER`] file, or (folders made before dev.25 have none) one of the
/// structures only the launcher makes: `library/<kind>/` named after a mod kind, `profiles/<pid>/meta.json`,
/// `installs/<id>.json` or `runtime/kiln-runtime-<version>.zip`. Entries the launcher does not know may sit next to
/// them. `settings.json`, `logs/` or `state/` alone are not enough: those names are common.
pub fn recognize(dir: &Path, env: &DataEnv) -> Result<FolderState> {
    let style = PathStyle::of(env);
    use std::io::ErrorKind as K;
    let meta = match std::fs::symlink_metadata(dir) {
        Ok(m) => m,
        // nothing can be there: a file above it (the check then names that file), a name too long for this disk
        Err(e) if matches!(e.kind(), K::NotFound | K::NotADirectory | K::InvalidFilename) => return Ok(FolderState::Missing),
        Err(e) => return Err(io_at(dir, "inspecting")(e)),
    };
    if is_link(&meta) {
        return Ok(FolderState::Link);
    }
    if !meta.is_dir() {
        return Ok(FolderState::NotAFolder);
    }
    let names = names_in(dir)?;
    let is_file = |n: &str| std::fs::symlink_metadata(dir.join(n)).is_ok_and(|m| m.is_file());
    if let Some(m) = names.iter().find(|n| style.same_name(n, MOVE_MARKER))
        && is_file(m)
    {
        let id = crate::util::read_json::<serde_json::Value>(&dir.join(m)).ok().flatten()
            .and_then(|v| v.get("id").and_then(|i| i.as_str()).map(str::to_string)).unwrap_or_default();
        let committed = names.iter().any(|n| style.same_name(n, MARKER) && is_file(n));
        return Ok(FolderState::Moving { id, committed });
    }
    if let Some(m) = names.iter().find(|n| style.same_name(n, MARKER))
        && std::fs::symlink_metadata(dir.join(m)).is_ok_and(|m| m.is_file())
    {
        return Ok(FolderState::Kiln { by: Evidence::Marker });
    }
    let is_default = env.default_root().is_ok_and(|d| PathParts::of(&canonical(&d), style).same(&PathParts::of(&canonical(dir), style)));
    let own = if is_default { own_entries(dir, env) } else { Vec::new() };
    let rest: Vec<&String> = names.iter().filter(|n| !is_leftover(dir, n, &own, style)).collect();
    if rest.is_empty() {
        return Ok(FolderState::Empty);
    }
    if let Some(by) = evidence(dir, style) {
        return Ok(FolderState::Kiln { by });
    }
    let entry = rest.iter().find(|n| !is_known(n, style)).or(rest.first()).map(|n| n.to_string()).unwrap_or_default();
    Ok(FolderState::Other { entry })
}

// ------------------------------------------------------------------------------------------------ inventory

/// One top-level entry of a data folder: its size and how many files it holds (links and special files not counted).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EntrySize {
    pub name: String,
    pub folder: bool,
    pub bytes: u64,
    pub files: u64,
}

/// A path relative to the data root (`/` between names) and its length in UTF-16 code units (what Windows counts).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LongestPath {
    pub path: String,
    pub units: usize,
}

/// What a data folder holds ([`inventory`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Inventory {
    pub root: PathBuf,
    /// The launcher's own top-level entries ([`KNOWN_ENTRIES`]), by name.
    pub known: Vec<EntrySize>,
    /// Top-level entries the launcher does not know (a move copies them and leaves them at the old place).
    pub unknown: Vec<EntrySize>,
    /// Entries that are not launcher data and never move: [`POINTER`], [`POINTER_OLD`], [`JOURNAL`], [`FILE_RECORD`]
    /// (and their `.tmp`), the markers [`MARKER`] and [`MOVE_MARKER`], and the entry that holds Kiln's state or settings
    /// folder (Windows: `state\` in the default root). Not in the totals.
    pub stays: Vec<EntrySize>,
    /// Lock files (`quick.lock` and [`LOCK`] at the top, `nexus/*.lock`): not data, not in the totals.
    pub locks: Vec<String>,
    /// Caches a move does not copy (`state/file-hashes.json`): not in the totals.
    #[serde(default)]
    pub caches: Vec<String>,
    /// Links and junctions found inside (relative paths): reported, never followed, not in the totals.
    pub links: Vec<String>,
    /// Entries that are neither a file, a folder nor a link (sockets, pipes, devices): reported, not in the totals.
    pub special: Vec<String>,
    /// `known` and `unknown` together: what a move copies.
    pub bytes: u64,
    pub files: u64,
    /// The longest relative path of any file or folder.
    pub longest: Option<LongestPath>,
    /// The longest one the game opens: under `library/`, or a profile's `config/` and `data/`.
    pub longest_game: Option<LongestPath>,
    /// Of `bytes` and `files`: what is written in place ([`written_in_place`]) and so copied for real by a move that
    /// hard-links the rest.
    #[serde(default)]
    pub in_place_bytes: u64,
    #[serde(default)]
    pub in_place_files: u64,
}

impl Inventory {
    pub fn unknown_names(&self) -> Vec<String> {
        self.unknown.iter().map(|e| e.name.clone()).collect()
    }
}

#[derive(Default)]
struct Walk {
    links: Vec<String>,
    special: Vec<String>,
    locks: Vec<String>,
    longest: Option<LongestPath>,
    longest_game: Option<LongestPath>,
    in_place: (u64, u64),
    caches: Vec<String>,
}

fn longer(slot: &mut Option<LongestPath>, rel: &str) {
    let units = rel.encode_utf16().count();
    if slot.as_ref().is_none_or(|l| units > l.units) {
        *slot = Some(LongestPath { path: rel.to_string(), units });
    }
}

/// `library/…`, `profiles/<pid>/config/…`, `profiles/<pid>/data/…`: what the game opens by a path into the data
/// folder (Z mode, native mode).
fn game_reads(names: &[String], style: PathStyle) -> bool {
    let is = |i: usize, b: &str| names.get(i).is_some_and(|n| style.same_name(n, b));
    is(0, "library") || (is(0, "profiles") && names.len() >= 3 && (is(2, "config") || is(2, "data")))
}

/// Size and file count below `path` (`names`: its relative names), never following a link.
fn walk(path: &Path, names: &mut Vec<String>, style: PathStyle, w: &mut Walk) -> Result<(u64, u64)> {
    let rel = names.join("/");
    let m = std::fs::symlink_metadata(path).map_err(io_at(path, "inspecting"))?;
    if is_link(&m) {
        w.links.push(rel);
        return Ok((0, 0));
    }
    let note = |w: &mut Walk| {
        longer(&mut w.longest, &rel);
        if game_reads(names, style) {
            longer(&mut w.longest_game, &rel);
        }
    };
    if m.is_file() {
        // nexus/*.lock: the catalog's state locks and instance.lock
        if names.len() == 2 && style.same_name(&names[0], "nexus") && is_lock_name(&names[1]) {
            w.locks.push(rel);
            return Ok((0, 0));
        }
        if is_hash_cache(names, style) {
            w.caches.push(rel);
            return Ok((0, 0));
        }
        note(w);
        if written_in_place(names, style) {
            w.in_place.0 += m.len();
            w.in_place.1 += 1;
        }
        return Ok((m.len(), 1));
    }
    if !m.is_dir() {
        w.special.push(rel);
        return Ok((0, 0));
    }
    note(w);
    let (mut bytes, mut files) = (0, 0);
    for name in names_in(path)? {
        names.push(name);
        let (b, f) = walk(&path.join(names.last().expect("pushed")), names, style, w)?;
        names.pop();
        bytes += b;
        files += f;
    }
    Ok((bytes, files))
}

/// What the data folder at `root` holds: its top-level entries split into the launcher's own and unknown ones, with
/// bytes and files per entry and in total, lock files apart, links and special files reported (never followed), and
/// the longest relative paths. `root` itself must be a real folder (not a link: `engine_mods::refuse_symlink_path`
/// refuses that as a data root). Fails on a folder it cannot read.
pub fn inventory(root: &Path, env: &DataEnv) -> Result<Inventory> {
    let style = PathStyle::of(env);
    let m = std::fs::symlink_metadata(root).map_err(io_at(root, "inspecting"))?;
    if is_link(&m) || !m.is_dir() {
        return Err(Error::new(ErrorCode::Refused, format!("{}: the data folder must be a real folder, not a link or a file", root.display())));
    }
    let own = own_entries(root, env);
    let mut w = Walk::default();
    let (mut known, mut unknown, mut stays) = (Vec::new(), Vec::new(), Vec::new());
    for name in names_in(root)? {
        let path = root.join(&name);
        let em = std::fs::symlink_metadata(&path).map_err(io_at(&path, "inspecting"))?;
        let is = |b: &str| style.same_name(&name, b);
        let stay = is_own_file(&name, style) || own.iter().any(|o| is(o));
        if !stay && em.is_file() && !is_link(&em) && is_top_lock(&name, style) {
            w.locks.push(name);
            continue;
        }
        if stay {
            // sized as far as it can be read (it is not data: a file a running server writes there must not fail the
            // inventory); its links and longest paths are not the data folder's
            let mut aside = Walk::default();
            let (bytes, files) = walk(&path, &mut vec![name.clone()], style, &mut aside).unwrap_or((0, 0));
            stays.push(EntrySize { name, folder: em.is_dir(), bytes, files });
            continue;
        }
        let (bytes, files) = walk(&path, &mut vec![name.clone()], style, &mut w)?;
        let e = EntrySize { name: name.clone(), folder: em.is_dir() && !is_link(&em), bytes, files };
        if is_known(&name, style) { known.push(e) } else { unknown.push(e) }
    }
    let (bytes, files) = known.iter().chain(&unknown).fold((0, 0), |(b, f), e| (b + e.bytes, f + e.files));
    Ok(Inventory {
        root: root.to_path_buf(),
        known,
        unknown,
        stays,
        locks: w.locks,
        caches: w.caches,
        links: w.links,
        special: w.special,
        bytes,
        files,
        longest: w.longest,
        longest_game: w.longest_game,
        in_place_bytes: w.in_place.0,
        in_place_files: w.in_place.1,
    })
}

/// The data-folder machinery's own top-level files (pointer, journal, file record, their `.tmp`, the old pointer, the
/// markers): never data, never copied by a move.
pub fn is_own_file(name: &str, style: PathStyle) -> bool {
    [POINTER, POINTER_OLD, JOURNAL, FILE_RECORD, MARKER, MOVE_MARKER]
        .iter()
        .any(|b| style.same_name(name, b) || style.same_name(name, &format!("{b}.tmp")))
}

// ------------------------------------------------------------------------------------------------ system facts

/// Free space on a volume and its allocation unit (cluster) in bytes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Space {
    pub free: u64,
    pub unit: u64,
}

/// What [`check`] asks the system. [`RealFacts`] asks it; [`Facts`] holds fixed answers (tests, the launcher's mock).
pub trait SystemFacts {
    /// A sentence part naming the running game of one of `installs` ("enshrouded.exe is running (pid 4242)"), if one
    /// runs (the client's or the server's exe in any of their folders).
    fn game_running(&self, installs: &[GameInstall]) -> Result<Option<String>>;
    /// The name of a running Kiln server (launcher records and kilnctl instances), if one runs.
    fn server_running(&self) -> Result<Option<String>>;
    /// Free space where `dir` (an existing folder) is. None: unknown.
    fn space(&self, dir: &Path) -> Option<Space>;
    /// A file can be made in `dir` and removed again.
    fn writable(&self, dir: &Path) -> bool;
    /// `a` and `b` (existing folders) are on one volume. None: unknown.
    fn same_volume(&self, a: &Path, b: &Path) -> Option<bool>;
    /// A file in `from` can be hard-linked into `to` (both existing folders).
    fn hard_links(&self, from: &Path, to: &Path) -> bool;
    /// `dir` (an existing folder) is where a volume is mounted (`/run/media/<user>/USB`, a folder a Windows volume is
    /// mounted on). Drive letters, shares and `/` are told by their path ([`PathParts::is_volume_root`]).
    fn volume_root(&self, dir: &Path) -> bool;
    /// Unix: a file made in `dir` with mode 0600 keeps it (a disk without Unix permissions does not). None: not known.
    fn private_files(&self, dir: &Path) -> Option<bool>;
}

/// Fixed answers for [`SystemFacts`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Facts {
    pub game_running: Option<String>,
    pub server_running: Option<String>,
    pub space: Option<Space>,
    pub writable: bool,
    pub same_volume: Option<bool>,
    pub hard_links: bool,
    pub volume_root: bool,
    pub private_files: Option<bool>,
}

impl Facts {
    /// Nothing runs, 1 TiB free in 4 KiB units, writable, another volume, no hard links, no mount points.
    pub fn all_clear() -> Facts {
        Facts {
            game_running: None,
            server_running: None,
            space: Some(Space { free: 1 << 40, unit: 4096 }),
            writable: true,
            same_volume: Some(false),
            hard_links: false,
            volume_root: false,
            private_files: Some(true),
        }
    }
}

impl SystemFacts for Facts {
    fn game_running(&self, _: &[GameInstall]) -> Result<Option<String>> {
        Ok(self.game_running.clone())
    }
    fn server_running(&self) -> Result<Option<String>> {
        Ok(self.server_running.clone())
    }
    fn space(&self, _: &Path) -> Option<Space> {
        self.space
    }
    fn writable(&self, _: &Path) -> bool {
        self.writable
    }
    fn same_volume(&self, _: &Path, _: &Path) -> Option<bool> {
        self.same_volume
    }
    fn hard_links(&self, _: &Path, _: &Path) -> bool {
        self.hard_links
    }
    fn volume_root(&self, _: &Path) -> bool {
        self.volume_root
    }
    fn private_files(&self, _: &Path) -> Option<bool> {
        self.private_files
    }
}

/// The servers whose running blocks a move: the launcher's records in `<data>/servers/` and kilnctl's instances
/// (`server::instance::list`; Steam installs nobody recorded are not counted).
pub fn known_servers(dirs: &DataDirs) -> Vec<crate::server::instance::ServerInstance> {
    use crate::server::instance::{ServerInstance, validate_name};
    let mut out: Vec<ServerInstance> = std::fs::read_dir(dirs.root.join("servers"))
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().ends_with(".json"))
        .filter_map(|e| crate::util::read_json::<ServerInstance>(&e.path()).ok().flatten())
        .filter(|s| validate_name(&s.id).is_ok())
        .collect();
    for s in crate::server::instance::list().unwrap_or_default() {
        if s.source == "kilnctl" && !out.iter().any(|o| o.id == s.id) {
            out.push(s);
        }
    }
    out.sort_by(|a, b| a.id.cmp(&b.id));
    out
}

/// [`SystemFacts`] from the system: the process list, `statvfs` / `GetDiskFreeSpaceExW`, the volume of a folder
/// (`st_dev` / `GetVolumePathNameW`), and probe files (`.kiln-probe-<random>`, removed at once) for writing and hard
/// links.
pub struct RealFacts<'a> {
    /// The data folder whose server records count ([`known_servers`]).
    pub dirs: &'a DataDirs,
}

fn probe_name(what: &str) -> String {
    let r = crate::util::random_bytes::<6>().map(|b| crate::hash::hex(&b)).unwrap_or_else(|_| std::process::id().to_string());
    format!(".kiln-probe-{what}-{r}")
}

impl SystemFacts for RealFacts<'_> {
    fn game_running(&self, installs: &[GameInstall]) -> Result<Option<String>> {
        let mut finder = crate::proc::Finder::default();
        for i in installs {
            let mut exes: Vec<&str> = Vec::new();
            for e in [i.exe.as_str(), crate::game::CLIENT_EXE, crate::game::SERVER_EXE] {
                if !exes.contains(&e) {
                    exes.push(e);
                }
            }
            for exe in exes {
                if let Some(p) = finder.find(&i.path, exe)? {
                    return Ok(Some(format!("{} is running (pid {})", p.name, p.pid)));
                }
            }
        }
        Ok(None)
    }

    fn server_running(&self) -> Result<Option<String>> {
        for s in known_servers(self.dirs) {
            if crate::server::run::pid(&s).is_some() {
                return Ok(Some(s.name));
            }
        }
        Ok(None)
    }

    fn space(&self, dir: &Path) -> Option<Space> {
        sys::space(dir)
    }

    fn writable(&self, dir: &Path) -> bool {
        let p = dir.join(probe_name("write"));
        match std::fs::OpenOptions::new().write(true).create_new(true).open(&p) {
            Ok(f) => {
                drop(f);
                std::fs::remove_file(&p).is_ok()
            }
            Err(_) => false,
        }
    }

    fn same_volume(&self, a: &Path, b: &Path) -> Option<bool> {
        sys::same_volume(a, b)
    }

    fn hard_links(&self, from: &Path, to: &Path) -> bool {
        let a = from.join(probe_name("link"));
        let b = to.join(probe_name("link"));
        if std::fs::OpenOptions::new().write(true).create_new(true).open(&a).is_err() {
            return false;
        }
        let ok = std::fs::hard_link(&a, &b).is_ok();
        if ok {
            let _ = std::fs::remove_file(&b);
        }
        let _ = std::fs::remove_file(&a);
        ok
    }

    fn volume_root(&self, dir: &Path) -> bool {
        sys::volume_root(dir)
    }

    fn private_files(&self, dir: &Path) -> Option<bool> {
        #[cfg(unix)]
        {
            use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
            let p = dir.join(probe_name("private"));
            let f = std::fs::OpenOptions::new().write(true).create_new(true).mode(0o600).open(&p).ok()?;
            drop(f);
            let ok = std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o600)).is_ok()
                && std::fs::metadata(&p).is_ok_and(|m| m.permissions().mode() & 0o077 == 0);
            let _ = std::fs::remove_file(&p);
            Some(ok)
        }
        #[cfg(not(unix))]
        {
            let _ = dir;
            None
        }
    }
}

#[cfg(unix)]
mod sys {
    use super::Space;
    use std::path::Path;

    pub fn space(dir: &Path) -> Option<Space> {
        use std::os::unix::ffi::OsStrExt;
        let c = std::ffi::CString::new(dir.as_os_str().as_bytes()).ok()?;
        let mut st: libc::statvfs = unsafe { std::mem::zeroed() };
        // SAFETY: a NUL-terminated path and a zeroed out-struct of the right type.
        if unsafe { libc::statvfs(c.as_ptr(), &mut st) } != 0 {
            return None;
        }
        let unit = if st.f_frsize > 0 { st.f_frsize as u64 } else { st.f_bsize as u64 };
        Some(Space { free: (st.f_bavail as u64).saturating_mul(unit), unit: unit.max(1) })
    }

    pub fn same_volume(a: &Path, b: &Path) -> Option<bool> {
        use std::os::unix::fs::MetadataExt;
        Some(std::fs::metadata(a).ok()?.dev() == std::fs::metadata(b).ok()?.dev())
    }

    /// `/`, or a folder on another device than the folder above it.
    pub fn volume_root(dir: &Path) -> bool {
        match dir.parent() {
            None => true,
            Some(up) => same_volume(dir, up) == Some(false),
        }
    }
}

#[cfg(windows)]
mod sys {
    use super::Space;
    use std::os::windows::ffi::OsStrExt;
    use std::path::Path;
    use windows_sys::Win32::Storage::FileSystem::{GetDiskFreeSpaceExW, GetDiskFreeSpaceW, GetVolumePathNameW};

    /// The path as a NUL-terminated wide string. A full path that may pass MAX_PATH gets the `\\?\` prefix (as std does
    /// for its own file calls): without it these Win32 calls fail for a folder deeper than 259 characters, unless long
    /// paths are switched on for the PC and the program.
    pub(super) fn wide(p: &Path) -> Vec<u16> {
        let s = p.as_os_str();
        let text = s.to_string_lossy();
        let long = s.encode_wide().count() >= 248;
        let b = text.as_bytes();
        let drive = b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && matches!(b[2], b'\\' | b'/');
        let unc = text.starts_with(r"\\") && !text.starts_with(r"\\?\") && !text.starts_with(r"\\.\");
        // `C:\…` -> `\\?\C:\…`, `\\server\share\…` -> `\\?\UNC\server\share\…` (a verbatim path takes `\` only)
        let (prefix, skip) = match (long, drive, unc) {
            (true, true, _) => (r"\\?\", 0),
            (true, false, true) => (r"\\?\UNC", 1),
            _ => ("", 0),
        };
        let sep = |c: u16| if !prefix.is_empty() && c == u16::from(b'/') { u16::from(b'\\') } else { c };
        prefix.encode_utf16().chain(s.encode_wide().skip(skip).map(sep)).chain(Some(0)).collect()
    }

    /// `C:\` for `C:\a\b`, `\\server\share\` for a share, the mount folder for a mounted volume (without `\\?\`).
    fn volume_of(p: &Path) -> Option<String> {
        let w = wide(p);
        let mut buf = vec![0u16; 32_768];
        // SAFETY: a NUL-terminated path and a buffer of the length given.
        if unsafe { GetVolumePathNameW(w.as_ptr(), buf.as_mut_ptr(), buf.len() as u32) } == 0 {
            return None;
        }
        let n = buf.iter().position(|c| *c == 0).unwrap_or(buf.len());
        let v = String::from_utf16_lossy(&buf[..n]);
        Some(crate::steam::strip_verbatim(v.into()).to_string_lossy().into_owned())
    }

    pub fn space(dir: &Path) -> Option<Space> {
        let w = wide(dir);
        let (mut free, mut total, mut all) = (0u64, 0u64, 0u64);
        // SAFETY: a NUL-terminated path and three out-values.
        if unsafe { GetDiskFreeSpaceExW(w.as_ptr(), &mut free, &mut total, &mut all) } == 0 {
            return None;
        }
        let unit = volume_of(dir)
            .and_then(|v| {
                let vw: Vec<u16> = v.encode_utf16().chain(Some(0)).collect();
                let (mut spc, mut bps, mut fc, mut tc) = (0u32, 0u32, 0u32, 0u32);
                // SAFETY: a NUL-terminated root path and four out-values.
                (unsafe { GetDiskFreeSpaceW(vw.as_ptr(), &mut spc, &mut bps, &mut fc, &mut tc) } != 0)
                    .then(|| spc as u64 * bps as u64)
            })
            .filter(|u| *u > 0)
            .unwrap_or(4096);
        Some(Space { free, unit })
    }

    pub fn same_volume(a: &Path, b: &Path) -> Option<bool> {
        Some(volume_of(a)?.to_lowercase() == volume_of(b)?.to_lowercase())
    }

    /// The folder is its own volume path (`D:\`, a share, a folder a volume is mounted on).
    pub fn volume_root(dir: &Path) -> bool {
        let norm = |s: &str| crate::steam::strip_verbatim(s.into()).to_string_lossy().trim_end_matches(['\\', '/']).to_lowercase();
        volume_of(dir).is_some_and(|v| norm(&v) == norm(&dir.to_string_lossy()))
    }
}

#[cfg(not(any(unix, windows)))]
mod sys {
    use super::Space;
    use std::path::Path;
    pub fn space(_: &Path) -> Option<Space> {
        None
    }
    pub fn same_volume(_: &Path, _: &Path) -> Option<bool> {
        None
    }
    pub fn volume_root(_: &Path) -> bool {
        false
    }
}

// ------------------------------------------------------------------------------------------------ the check

/// What happens with the picked folder.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Action {
    /// The data folder moves there.
    Move,
    /// It already is a Kiln data folder: the launcher switches to it as it is (no merge).
    Use,
}

/// Why a picked folder cannot take the data folder. Serialized in snake_case; new codes go at the end.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum RefusalCode {
    /// `$KILN_DATA_DIR` sets the data folder: a pointer would not be read.
    EnvOverride,
    /// Another launcher holds the data folder's lock.
    Locked,
    /// The game (any known install) is running.
    GameRunning,
    /// A Kiln server (launcher record or kilnctl instance) is running.
    ServerRunning,
    NotAbsolute,
    ParentTraversal,
    /// Windows: `\\server\share`.
    NetworkPath,
    /// The picked folder (or its `Kiln` subfolder) is a link or a junction.
    Link,
    /// The picked folder (or its `Kiln` subfolder, or a folder above it) is a file.
    NotAFolder,
    /// Kiln could not read the picked folder.
    Unreadable,
    SameFolder,
    InsideCurrent,
    ContainsCurrent,
    /// Inside a game folder: a known install, or any folder above that holds the game's exe.
    InsideGame,
    /// Inside a known server's folder.
    InsideServer,
    /// Inside the folder where Kiln keeps its own state or settings (`util::state_dir`, `util::config_dir`).
    InsideKilnState,
    /// `<picked>/Kiln` exists, is not empty and is not a Kiln data folder.
    KilnSubfolderTaken,
    NotWritable,
    TooSmall,
    /// Windows: a path the game reads would pass 259 characters.
    PathTooLong,
    /// The folder holds a move in progress ([`MOVE_MARKER`]).
    MoveInProgress,
    /// The default root holds files that are not the launcher's (it never gets a `Kiln` subfolder).
    DefaultRootTaken,
    /// A move has not been finished: its journal is still in the default root.
    MovePending,
    /// A link or a special file is inside the current data folder: a move never follows or copies one.
    LinkInside,
    /// The launcher is busy with the data folder (an import or another flow holds `quick.lock`).
    Busy,
    /// "Use this one" on a folder that is not a Kiln data folder.
    NotAKilnFolder,
    /// The target already holds an entry of the name a move would make there.
    TargetHasEntry,
}

impl RefusalCode {
    pub fn as_str(self) -> &'static str {
        match self {
            RefusalCode::EnvOverride => "env_override",
            RefusalCode::Locked => "locked",
            RefusalCode::GameRunning => "game_running",
            RefusalCode::ServerRunning => "server_running",
            RefusalCode::NotAbsolute => "not_absolute",
            RefusalCode::ParentTraversal => "parent_traversal",
            RefusalCode::NetworkPath => "network_path",
            RefusalCode::Link => "link",
            RefusalCode::NotAFolder => "not_a_folder",
            RefusalCode::Unreadable => "unreadable",
            RefusalCode::SameFolder => "same_folder",
            RefusalCode::InsideCurrent => "inside_current",
            RefusalCode::ContainsCurrent => "contains_current",
            RefusalCode::InsideGame => "inside_game",
            RefusalCode::InsideServer => "inside_server",
            RefusalCode::InsideKilnState => "inside_kiln_state",
            RefusalCode::KilnSubfolderTaken => "kiln_subfolder_taken",
            RefusalCode::NotWritable => "not_writable",
            RefusalCode::TooSmall => "too_small",
            RefusalCode::PathTooLong => "path_too_long",
            RefusalCode::MoveInProgress => "move_in_progress",
            RefusalCode::DefaultRootTaken => "default_root_taken",
            RefusalCode::MovePending => "move_pending",
            RefusalCode::LinkInside => "link_inside",
            RefusalCode::Busy => "busy",
            RefusalCode::NotAKilnFolder => "not_a_kiln_folder",
            RefusalCode::TargetHasEntry => "target_has_entry",
        }
    }
}

/// A refused folder: a stable code and one plain sentence for the player.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Refusal {
    pub code: RefusalCode,
    pub reason: String,
}

impl Refusal {
    pub fn new(code: RefusalCode, reason: impl Into<String>) -> Refusal {
        Refusal { code, reason: reason.into() }
    }

    /// As the crate's error: `game_running` for a running game, else `refused`; `detail.refusal` is the code.
    pub fn into_error(self) -> Error {
        let code = if self.code == RefusalCode::GameRunning { ErrorCode::GameRunning } else { ErrorCode::Refused };
        Error::new(code, self.reason).with_detail(serde_json::json!({ "refusal": self.code }))
    }
}

impl std::fmt::Display for Refusal {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} ({})", self.reason, self.code.as_str())
    }
}

/// What a move to the picked folder would do ([`check`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Plan {
    /// The folder the player picked (`\\?\` taken off, `.` names dropped).
    pub picked: PathBuf,
    /// The data folder's new place: `picked`, or its `Kiln` subfolder; canonical where it exists.
    pub target: PathBuf,
    pub action: Action,
    /// What moves: every entry but the locks and `stays` (0 for `use`).
    pub bytes: u64,
    pub files: u64,
    /// What the target needs free ([`needed_bytes`]; 0 for `use`, and for hard links on one volume).
    pub needed: u64,
    /// Free bytes there. None: unknown.
    pub free: Option<u64>,
    /// The target is on the current folder's volume. None: unknown.
    pub same_volume: Option<bool>,
    /// Hard links from the current folder work there (then a move needs no space).
    pub hard_links: bool,
    /// Known game installs (ids) that would run in copy mode with the data folder there, and those that do today.
    pub copy_mode_after: Vec<String>,
    pub copy_mode_now: Vec<String>,
    /// Entries the launcher does not know: copied (they count in `bytes`) and left at the old place.
    pub unknown: Vec<EntrySize>,
    /// What stays at the old place because it is not data ([`Inventory::stays`]), by name.
    pub stays: Vec<String>,
    /// Links and special files in the current folder ([`Inventory::links`], [`Inventory::special`]); a move is refused
    /// while there are any.
    pub links: Vec<String>,
    pub special: Vec<String>,
    /// Unix: files made there keep their permissions (`nexus/` needs 0700 and 0600; a disk without Unix permissions,
    /// such as exFAT, ignores them). None: not known (Windows).
    #[serde(default)]
    pub private_files: Option<bool>,
    /// What will not work from there, one sentence each.
    #[serde(default)]
    pub warnings: Vec<String>,
}

/// What a copy of `bytes` in `files` files needs on a volume with allocation unit `unit`: the bytes, up to one unit
/// per file of rounding (exFAT's units are up to 32 MiB, 128 KiB on a 250 GB disk), and [`MARGIN_BYTES`] plus 5 % so
/// the disk is not full after the move.
pub fn needed_bytes(bytes: u64, files: u64, unit: u64) -> u64 {
    bytes.saturating_add(files.saturating_mul(unit)).saturating_add(MARGIN_BYTES).saturating_add(bytes / 20)
}

/// Where the data folder goes for a picked folder ([`target_rule`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Target {
    Move(PathBuf),
    Use(PathBuf),
    /// Refused: the code and the folder it is about.
    Refused(RefusalCode, PathBuf),
}

/// The picked-folder rule, in one place (decided 2026-11-11): a missing or empty folder becomes the data folder; a
/// Kiln data folder is offered as it is ("use this one", no merge); any other folder, and a volume root (`D:\`, `/`, a
/// mount point) unless it is a Kiln data folder, gets the subfolder `Kiln` (Windows) or `kiln`, which must itself be
/// missing, empty or a Kiln data folder. So a drive root never becomes a data folder. The default root
/// (`default_root`) never gets the subfolder: empty, it is the target; a Kiln data folder by its marker or layout, it is
/// used; anything else (also launcher entries only, as a launcher before dev.25 leaves) is refused. A folder with a move in progress
/// is refused. `sub` recognizes the subfolder.
pub fn target_rule(
    picked: &Path, state: &FolderState, volume_root: bool, default_root: bool, style: PathStyle,
    sub: &mut dyn FnMut(&Path) -> Result<FolderState>,
) -> Result<Target> {
    Ok(match state {
        FolderState::Kiln { .. } => Target::Use(picked.to_path_buf()),
        FolderState::Moving { .. } => Target::Refused(RefusalCode::MoveInProgress, picked.to_path_buf()),
        FolderState::Link => Target::Refused(RefusalCode::Link, picked.to_path_buf()),
        FolderState::NotAFolder => Target::Refused(RefusalCode::NotAFolder, picked.to_path_buf()),
        FolderState::Missing | FolderState::Empty if default_root || !volume_root => Target::Move(picked.to_path_buf()),
        FolderState::Other { .. } if default_root => Target::Refused(RefusalCode::DefaultRootTaken, picked.to_path_buf()),
        _ => {
            let s = picked.join(style.subfolder());
            match sub(&s)? {
                FolderState::Missing | FolderState::Empty => Target::Move(s),
                FolderState::Kiln { .. } => Target::Use(s),
                FolderState::Moving { .. } => Target::Refused(RefusalCode::MoveInProgress, s),
                FolderState::Link => Target::Refused(RefusalCode::Link, s),
                FolderState::NotAFolder => Target::Refused(RefusalCode::NotAFolder, s),
                FolderState::Other { .. } => Target::Refused(RefusalCode::KilnSubfolderTaken, s),
            }
        }
    })
}

/// What [`check`] takes.
pub struct CheckInput<'a> {
    /// The data root in use, and how it was found.
    pub current: &'a Path,
    pub source: RootSource,
    /// The folder the player picked, as given.
    pub picked: &'a Path,
    /// The environment (its `windows` flag picks the path rules).
    pub env: &'a DataEnv,
    /// The current data folder's [`inventory`].
    pub inventory: &'a Inventory,
    /// The game installs the launcher knows (`game::known`).
    pub installs: &'a [GameInstall],
    /// The local folders of the known servers ([`known_servers`]).
    pub servers: &'a [PathBuf],
    /// Another launcher holds the data folder's lock (`core-2` takes it).
    pub locked: bool,
}

fn say(code: RefusalCode, text: String) -> Refusal {
    Refusal::new(code, text)
}

/// The nearest folder at or above `path` that exists (where a probe can write), else the topmost ancestor (the
/// volume root of a drive that is not there). Err: a file sits in the way.
fn probe_dir(path: &Path) -> std::result::Result<PathBuf, PathBuf> {
    let mut last = path;
    for a in path.ancestors().filter(|a| !a.as_os_str().is_empty()) {
        last = a;
        if let Ok(m) = std::fs::metadata(a) {
            return if m.is_dir() { Ok(a.to_path_buf()) } else { Err(a.to_path_buf()) };
        }
    }
    Ok(last.to_path_buf())
}

/// String canonicalization on Unix need not fold case (for example exFAT). Compare actual ancestor identities too.
#[cfg(unix)]
fn identity_relation(path: &Path, current: &Path, contains: bool) -> Option<RefusalCode> {
    use std::os::unix::fs::MetadataExt;
    let id = |p: &Path| std::fs::metadata(p).ok().map(|m| (m.dev(), m.ino()));
    let root = id(current)?;
    for (i, a) in path.ancestors().enumerate() {
        if id(a) == Some(root) {
            return Some(if i == 0 { RefusalCode::SameFolder } else { RefusalCode::InsideCurrent });
        }
    }
    if contains && let Some(target) = id(path) {
        if current.ancestors().skip(1).any(|a| id(a) == Some(target)) {
            return Some(RefusalCode::ContainsCurrent);
        }
    }
    None
}

/// The places a data folder may not be (the same folder, inside the current one or (`contains`) around it, inside a
/// game folder, a server's folder, or Kiln's state or settings folder).
fn place_refusal(path: &Path, current: &Path, contains: bool, input: &CheckInput, style: PathStyle) -> Option<Refusal> {
    let p = PathParts::of(path, style);
    let shown = path.display();
    let relation = relation_to_current(&p, &PathParts::of(current, style), contains);
    #[cfg(unix)]
    let relation = relation.or_else(|| (style == PathStyle::Unix).then(|| identity_relation(path, current, contains)).flatten());
    match relation {
        Some(RefusalCode::SameFolder) => return Some(say(RefusalCode::SameFolder, format!("{shown} is the data folder already."))),
        Some(RefusalCode::InsideCurrent) => {
            return Some(say(RefusalCode::InsideCurrent, format!("{shown} is inside the current data folder {}.", current.display())));
        }
        Some(c) => return Some(say(c, format!("{shown} holds the current data folder {}.", current.display()))),
        None => {}
    }
    for g in input.installs {
        let gc = canonical(&g.path);
        if p.within(&PathParts::of(&gc, style)) {
            return Some(say(RefusalCode::InsideGame, format!("{shown} is inside the game folder {}: pick a folder outside it.", gc.display())));
        }
    }
    for a in path.ancestors().filter(|a| !a.as_os_str().is_empty()) {
        if a.join(crate::game::CLIENT_EXE).is_file() || a.join(crate::game::SERVER_EXE).is_file() {
            return Some(say(RefusalCode::InsideGame, format!("{shown} is inside the game folder {}: pick a folder outside it.", a.display())));
        }
    }
    for s in input.servers {
        let sc = canonical(s);
        if p.within(&PathParts::of(&sc, style)) {
            return Some(say(RefusalCode::InsideServer, format!("{shown} is inside the server folder {}: pick a folder outside it.", sc.display())));
        }
    }
    for d in [input.env.state_dir(), input.env.config_dir()] {
        let dc = canonical(&d);
        if p.within(&PathParts::of(&dc, style)) {
            return Some(say(RefusalCode::InsideKilnState, format!("{shown} is inside {}, where Kiln keeps its own state and settings.", dc.display())));
        }
    }
    None
}

/// Whether the picked folder can take the data folder, and what that would do. Stops at the first refusal, in this
/// order: the environment and what runs (`KILN_DATA_DIR`, the lock, the game, a server), the picked path's form,
/// the picked folder itself (a link, a file, a place it may not be), the picked-folder rule ([`target_rule`]), the
/// target's place, then (Windows) the length of the paths the game reads, writing, and the space a move needs.
/// A target that makes a game run in copy mode is not refused: the plan names those games.
pub fn check(input: &CheckInput, facts: &dyn SystemFacts) -> std::result::Result<Plan, Refusal> {
    use RefusalCode as C;
    let style = PathStyle::of(input.env);
    if input.source == RootSource::Env {
        return Err(say(C::EnvOverride, "The data folder is set by KILN_DATA_DIR, so the launcher would not use a new place: change that variable instead.".into()));
    }
    if input.locked {
        return Err(say(C::Locked, "Another Kiln Launcher is using the data folder: close it first.".into()));
    }
    match facts.game_running(input.installs) {
        Ok(Some(what)) => return Err(say(C::GameRunning, format!("{what}: quit the game first."))),
        Err(e) => return Err(say(C::GameRunning, format!("Kiln could not tell whether the game is running ({}): quit it and try again.", e.message))),
        Ok(None) => {}
    }
    match facts.server_running() {
        Ok(Some(name)) => return Err(say(C::ServerRunning, format!("The server {name} is running: stop it first."))),
        Err(e) => return Err(say(C::ServerRunning, format!("Kiln could not tell whether a server is running ({}): stop your servers and try again.", e.message))),
        Ok(None) => {}
    }
    if let Ok(d) = input.env.default_root()
        && std::fs::symlink_metadata(d.join(JOURNAL)).is_ok()
    {
        return Err(say(C::MovePending, format!("A move of the data folder has not been finished ({}): finish it first.", d.join(JOURNAL).display())));
    }
    let text = input.picked.to_string_lossy();
    check_form(&text, style)?;
    let picked = crate::util::clean(&crate::steam::strip_verbatim(input.picked.to_path_buf()));
    let current = canonical(input.current);
    let picked_state = match recognize(&picked, input.env) {
        Ok(s) => s,
        Err(e) => return Err(say(C::Unreadable, format!("Kiln cannot read {}: {}.", picked.display(), e.message))),
    };
    match picked_state {
        FolderState::Link => return Err(say(C::Link, format!("{} is a link to another folder: pick the folder itself.", picked.display()))),
        FolderState::NotAFolder => return Err(say(C::NotAFolder, format!("{} is a file, not a folder.", picked.display()))),
        _ => {}
    }
    if let Some(r) = place_refusal(&canonical(&picked), &current, false, input, style) {
        return Err(r);
    }
    let volume_root = PathParts::of(&picked, style).is_volume_root() || (picked.is_dir() && facts.volume_root(&picked));
    let default_root = input.env.default_root().is_ok_and(|d| PathParts::of(&canonical(&d), style).same(&PathParts::of(&canonical(&picked), style)));
    let mut sub = |p: &Path| recognize(p, input.env);
    let (target, action) = match target_rule(&picked, &picked_state, volume_root, default_root, style, &mut sub) {
        Err(e) => return Err(say(C::Unreadable, format!("Kiln cannot read {}: {}.", picked.join(style.subfolder()).display(), e.message))),
        Ok(Target::Move(t)) => (canonical(&t), Action::Move),
        Ok(Target::Use(t)) => (canonical(&t), Action::Use),
        Ok(Target::Refused(code, at_path)) => {
            let at = at_path.display();
            return Err(say(code, match code {
                C::Link => format!("{at} is a link to another folder: pick the folder itself."),
                C::NotAFolder => format!("{at} is a file, not a folder."),
                C::MoveInProgress => format!("{at} holds a move of a data folder that has not been finished: finish that one first."),
                C::DefaultRootTaken => format!(
                    "{at}, the launcher's own place, still holds {}: remove them first, then move the data folder there.",
                    foreign_entries(&at_path, input.env).join(", ")
                ),
                _ => format!("{at} already exists and holds files that are not a Kiln data folder: pick another folder, or empty that one."),
            }));
        }
    };
    if let Some(r) = place_refusal(&target, &current, true, input, style) {
        return Err(r);
    }
    // what the target keeps for itself (Windows: the default root's `state\`) is not copied: it stays in the old folder
    let target_own = if action == Action::Move { own_entries(&target, input.env) } else { Vec::new() };
    let skipped: Vec<&EntrySize> = input.inventory.known.iter().chain(&input.inventory.unknown)
        .filter(|e| target_own.iter().any(|o| style.same_name(o, &e.name))).collect();
    if action == Action::Move {
        // a move never writes into what it did not make: an entry of the same name already there refuses it (the
        // target's own state or settings folder is left out: the move does not copy into it)
        let own = &target_own;
        for e in input.inventory.known.iter().chain(&input.inventory.unknown) {
            if !own.iter().any(|o| style.same_name(o, &e.name)) && std::fs::symlink_metadata(target.join(&e.name)).is_ok() {
                return Err(say(C::TargetHasEntry, format!(
                    "{} already holds {}: a move never writes into what is already there, so remove it or pick another folder.",
                    target.display(),
                    e.name
                )));
            }
        }
    }
    if action == Action::Move
        && let Some(first) = input.inventory.links.first().or(input.inventory.special.first())
    {
        return Err(say(C::LinkInside, format!(
            "{first} in the data folder {} is a link or a special file: a move never follows or copies one, so move what it points to into the folder or remove it first.",
            current.display()
        )));
    }
    let parts = PathParts::of(&target, style);
    if action == Action::Move
        && let Some(n) = game_path_too_long(&parts, input.inventory.longest_game.as_ref())
    {
        return Err(say(C::PathTooLong, format!(
            "With the data folder at {}, a path the game reads would be {n} characters long, more than Windows allows ({WINDOWS_MAX_PATH}): pick a shorter path.",
            target.display()
        )));
    }
    let dir = match probe_dir(&target) {
        Ok(d) => d,
        Err(file) => return Err(say(C::NotAFolder, format!("{} is a file, so {} cannot be made.", file.display(), target.display()))),
    };
    if !facts.writable(&dir) {
        return Err(say(C::NotWritable, format!("Kiln cannot write to {}.", dir.display())));
    }
    let space = facts.space(&dir);
    let (mut bytes, mut files, mut needed, mut same_volume, mut hard_links) = (0, 0, 0, None, false);
    if action == Action::Move {
        bytes = input.inventory.bytes - skipped.iter().map(|e| e.bytes).sum::<u64>();
        files = input.inventory.files - skipped.iter().map(|e| e.files).sum::<u64>();
        same_volume = facts.same_volume(&current, &dir);
        hard_links = same_volume == Some(true) && facts.hard_links(&current, &dir);
        let unit = space.map_or(4096, |s| s.unit);
        // with hard links only what is written in place is copied for real (written_in_place)
        needed = if hard_links { needed_bytes(input.inventory.in_place_bytes, input.inventory.in_place_files, unit) } else { needed_bytes(bytes, files, unit) };
        if let Some(s) = space
            && s.free < needed
        {
            return Err(say(C::TooSmall, format!(
                "{} has {} free, and the data folder needs {} there.",
                dir.display(),
                crate::util::human_size(s.free),
                crate::util::human_size(needed)
            )));
        }
    }
    let private_files = facts.private_files(&dir);
    let mut warnings = Vec::new();
    if private_files == Some(false) && crate::nexus::enabled() {
        warnings.push(format!(
            "{} does not keep file permissions: from there, Mod Manager Download links from the browser do not reach the launcher, and a Nexus Mods login kept without a keyring is refused.",
            dir.display()
        ));
    }
    let mode = |root: &Path| -> Vec<String> {
        input
            .installs
            .iter()
            .filter(|i| crate::game::path_mode_for(i.platform, i.steam_flavor, i.prefix.as_deref(), root) == PathMode::Copy)
            .map(|i| i.id.clone())
            .collect()
    };
    Ok(Plan {
        picked,
        copy_mode_after: mode(&target),
        copy_mode_now: mode(input.current),
        target,
        action,
        bytes,
        files,
        needed,
        free: space.map(|s| s.free),
        same_volume,
        hard_links,
        unknown: if action == Action::Move { input.inventory.unknown.clone() } else { Vec::new() },
        stays: input.inventory.stays.iter().chain(skipped.iter().copied()).map(|e| e.name.clone()).collect(),
        links: input.inventory.links.clone(),
        special: input.inventory.special.clone(),
        private_files,
        warnings,
    })
}

/// [`check`] for the data folder in use, with what the launcher knows (its inventory, `game::known`,
/// [`known_servers`]) and the system's answers ([`RealFacts`]). Err: the current data folder cannot be read.
pub fn check_here(located: &Located, env: &DataEnv, picked: &Path, locked: bool) -> Result<std::result::Result<Plan, Refusal>> {
    let inv = inventory(&located.dirs.root, env)?;
    let installs = crate::game::known(&located.dirs)?;
    let servers: Vec<PathBuf> = known_servers(&located.dirs).into_iter().filter_map(|s| s.dir).collect();
    let input = CheckInput {
        current: &located.dirs.root,
        source: located.source,
        picked,
        env,
        inventory: &inv,
        installs: &installs,
        servers: &servers,
        locked,
    };
    Ok(check(&input, &RealFacts { dirs: &located.dirs }))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn win(s: &str) -> PathParts {
        PathParts::parse(s, PathStyle::Windows)
    }

    #[test]
    fn windows_forms_parse_by_string_rules() {
        let p = win(r"\\?\C:\Users\Jörg Müller\AppData\Local\Kiln");
        assert_eq!(p.root, PathRoot::Drive { letter: 'C' });
        assert_eq!(p.text(), r"C:\Users\Jörg Müller\AppData\Local\Kiln");
        assert!(p.is_absolute() && !p.is_network());
        let d = win(r"D:\");
        assert!(d.is_volume_root() && d.is_absolute());
        assert_eq!(d.text(), r"D:\");
        assert!(win(r"\\?\D:\").is_volume_root());
        assert!(win("d:/").is_volume_root());
        assert_eq!(win("d:/Games/Kiln/").text(), r"D:\Games\Kiln");
        for unc in [r"\\server\share", r"\\server\share\Kiln", "//server/share/x", r"\\?\UNC\server\share\Kiln"] {
            let u = win(unc);
            assert!(u.is_network() && u.is_absolute(), "{unc}");
        }
        assert!(win(r"\\?\UNC\server\share\Kiln").same(&win(r"\\SERVER\Share\kiln")));
        assert!(win(r"\\server\share").is_volume_root());
        for rel in ["C:foo", r"\foo", r"foo\bar", "Kiln", ""] {
            assert!(!win(rel).is_absolute(), "{rel:?}");
        }
        for dev in [r"\\.\PhysicalDrive0", r"\\?\Volume{0b1c}\x", r"\\server"] {
            let p = win(dev);
            assert!(matches!(p.root, PathRoot::Device { .. }) && !p.is_absolute(), "{dev}");
        }
        assert!(win(r"D:\a\..\b").has_parent_ref());
        assert!(!win(r"D:\a\.\b").has_parent_ref());
        assert_eq!(win(r"D:\a\.\b").names, ["a", "b"]);
        // Windows drops trailing dots and spaces of a name (not in a verbatim path)
        assert!(win(r"C:\Kiln.").same(&win(r"C:\Kiln")));
        assert!(win(r"C:\Kiln \x").same(&win(r"C:\Kiln\x")));
        assert!(!win(r"\\?\C:\Kiln.").same(&win(r"C:\Kiln")));
    }

    #[test]
    fn comparison_follows_the_platform() {
        let cur = win(r"C:\Users\A\AppData\Local\Kiln");
        assert_eq!(relation_to_current(&win(r"c:\users\a\appdata\local\kiln"), &cur, true), Some(RefusalCode::SameFolder));
        assert_eq!(relation_to_current(&win(r"\\?\C:\USERS\a\AppData\Local\Kiln\"), &cur, true), Some(RefusalCode::SameFolder));
        assert_eq!(relation_to_current(&win(r"C:\Users\A\AppData\Local\KILN\library"), &cur, true), Some(RefusalCode::InsideCurrent));
        assert_eq!(relation_to_current(&win(r"C:\Users\A"), &cur, true), Some(RefusalCode::ContainsCurrent));
        assert_eq!(relation_to_current(&win(r"C:\Users\A"), &cur, false), None, "only the final target is checked for holding it");
        assert_eq!(relation_to_current(&win(r"C:\Users\A\AppData\Local\Kiln2"), &cur, true), None, "a name prefix is not a folder");
        assert_eq!(relation_to_current(&win(r"D:\Users\A\AppData\Local\Kiln"), &cur, true), None, "another drive");
        assert_eq!(relation_to_current(&win(r"D:\"), &cur, true), None);
        let ux = |s: &str| PathParts::parse(s, PathStyle::Unix);
        let cur = ux("/home/u/.local/share/kiln");
        assert_eq!(relation_to_current(&ux("/home/u/.local/share/Kiln"), &cur, true), None, "case counts on Linux");
        assert_eq!(relation_to_current(&ux("/home/u/.local/share/kiln/"), &cur, true), Some(RefusalCode::SameFolder));
        assert_eq!(relation_to_current(&ux("/home/u/.local/share/kiln/x"), &cur, true), Some(RefusalCode::InsideCurrent));
        assert_eq!(relation_to_current(&ux("/"), &cur, true), Some(RefusalCode::ContainsCurrent));
        assert!(ux("/").is_volume_root());
        // a Unix name may hold a backslash: it is one name
        assert_eq!(ux(r"/a\b").names, [r"a\b"]);
    }

    #[test]
    fn picked_path_form_refusals() {
        let code = |s: &str| check_form(s, PathStyle::Windows).err().map(|r| r.code);
        assert_eq!(code(r"\\server\share\Kiln"), Some(RefusalCode::NetworkPath));
        assert_eq!(code(r"\\?\UNC\server\share"), Some(RefusalCode::NetworkPath));
        assert_eq!(code("//nas/games"), Some(RefusalCode::NetworkPath));
        assert_eq!(code("C:Kiln"), Some(RefusalCode::NotAbsolute));
        assert_eq!(code(r"\Kiln"), Some(RefusalCode::NotAbsolute));
        assert_eq!(code("Kiln"), Some(RefusalCode::NotAbsolute));
        assert_eq!(code(r"\\.\PhysicalDrive0"), Some(RefusalCode::NotAbsolute));
        assert_eq!(code(r"D:\Games\..\Kiln"), Some(RefusalCode::ParentTraversal));
        // `\\?\` is taken off, not refused; drive roots, spaces, non-ASCII and long paths pass the form check
        assert_eq!(check_form(r"\\?\D:\Games", PathStyle::Windows).unwrap().text(), r"D:\Games");
        assert!(check_form(r"D:\", PathStyle::Windows).unwrap().is_volume_root());
        assert!(check_form(r"E:\Spiele Ordner\Jörg Müller\データ", PathStyle::Windows).is_ok());
        assert!(check_form(&format!(r"D:\{}", "a".repeat(300)), PathStyle::Windows).is_ok());
        let ux = |s: &str| check_form(s, PathStyle::Unix).err().map(|r| r.code);
        assert_eq!(ux("relative/kiln"), Some(RefusalCode::NotAbsolute));
        assert_eq!(ux("/a/../b"), Some(RefusalCode::ParentTraversal));
        assert_eq!(ux(r"\\server\share"), Some(RefusalCode::NotAbsolute), "not a network path on Linux");
        assert_eq!(ux("/run/media/u/USB STICK/Kiln Daten"), None);
    }

    #[test]
    fn long_paths_count_utf16_units_against_max_path() {
        let rel = |n: usize| LongestPath { path: "x".repeat(n), units: n };
        let t = win(r"D:\Kiln");
        assert_eq!(t.units(), 7);
        assert_eq!(game_path_too_long(&t, Some(&rel(WINDOWS_MAX_PATH - 8))), None, "259 exactly fits");
        assert_eq!(game_path_too_long(&t, Some(&rel(WINDOWS_MAX_PATH - 7))), Some(260));
        assert_eq!(game_path_too_long(&t, None), None, "an empty library");
        // a character outside the BMP is two units, as Windows counts it
        assert_eq!(win("D:\\\u{1F600}").units(), 5);
        assert_eq!(win(r"E:\Jörg").units(), 7);
        let long = win(&format!(r"D:\{}", "a".repeat(260)));
        assert_eq!(game_path_too_long(&long, Some(&rel(1))), Some(265));
        // the rule is Windows'
        assert_eq!(game_path_too_long(&PathParts::parse(&format!("/{}", "a".repeat(300)), PathStyle::Unix), Some(&rel(10))), None);
    }

    #[test]
    fn the_picked_folder_rule() {
        let p = Path::new("/picked");
        let other = FolderState::Other { entry: "x".into() };
        let kiln = FolderState::Kiln { by: Evidence::Library };
        let rule = |state: &FolderState, root: bool, style: PathStyle, sub: FolderState| {
            target_rule(p, state, root, false, style, &mut |s: &Path| {
                assert_eq!(s, p.join(style.subfolder()));
                Ok(sub.clone())
            })
            .unwrap()
        };
        use FolderState::*;
        for s in [Missing, Empty] {
            assert_eq!(rule(&s, false, PathStyle::Unix, other.clone()), Target::Move(p.into()));
            // a volume root never becomes the data folder, even an empty one
            assert_eq!(rule(&s, true, PathStyle::Unix, Missing), Target::Move(p.join("kiln")));
        }
        assert_eq!(rule(&kiln, true, PathStyle::Unix, other.clone()), Target::Use(p.into()), "a Kiln data folder is used as it is");
        assert_eq!(rule(&other, false, PathStyle::Windows, Missing), Target::Move(p.join("Kiln")));
        assert_eq!(rule(&other, false, PathStyle::Unix, Empty), Target::Move(p.join("kiln")));
        assert_eq!(rule(&other, false, PathStyle::Unix, kiln.clone()), Target::Use(p.join("kiln")));
        assert_eq!(rule(&other, false, PathStyle::Unix, other.clone()), Target::Refused(RefusalCode::KilnSubfolderTaken, p.join("kiln")));
        assert_eq!(rule(&other, false, PathStyle::Unix, Link), Target::Refused(RefusalCode::Link, p.join("kiln")));
        assert_eq!(rule(&other, false, PathStyle::Unix, NotAFolder), Target::Refused(RefusalCode::NotAFolder, p.join("kiln")));
        assert_eq!(rule(&Link, false, PathStyle::Unix, Missing), Target::Refused(RefusalCode::Link, p.into()));
        assert_eq!(rule(&NotAFolder, false, PathStyle::Unix, Missing), Target::Refused(RefusalCode::NotAFolder, p.into()));
    }

    #[test]
    fn needed_space_has_rounding_and_a_margin() {
        assert_eq!(needed_bytes(0, 0, 4096), MARGIN_BYTES);
        let gb = 1u64 << 30;
        assert_eq!(needed_bytes(20 * gb, 10_000, 128 * 1024), 20 * gb + 10_000 * 128 * 1024 + MARGIN_BYTES + gb);
        assert_eq!(needed_bytes(u64::MAX, u64::MAX, u64::MAX), u64::MAX);
    }
}
