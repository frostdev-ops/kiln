//! 7z and rar packages for the Library. A zip is `library::unpack`'s own; these two go into a private temp folder under
//! the same limits (`quick::MAX_IMPORT_*`: entry count, total and per-file size, depth; no links; no path outside the
//! folder; no modpack manifest, a modpack is a zip with the player's consent):
//!
//! - **7z** is read in-process by the pure-Rust `sevenz-rust2` (feature `sevenz`; LZMA, LZMA2, BCJ, Delta, PPMd, BZip2
//!   and Deflate; no AES: a password-protected archive is refused with its reason). Its decoder memory is capped.
//! - **rar** has no acceptable pure-Rust reader, so a tool already on the machine does it: `unrar`, 7-Zip (`7zz`, `7z`),
//!   or `bsdtar` (libarchive; Windows' own `tar.exe`). The tool is found by absolute path (never the current folder or
//!   a relative `PATH` entry), gets the archive and the target as arguments and an empty private working folder, reads
//!   nothing from the dropped content, and is stopped when its output passes the import limits. The extracted tree is
//!   checked with `quick::validate_import_path` before the Library looks at it. Without a tool the message says to
//!   extract the rar first and drop the folder.

use crate::library::TempDir;
use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};

const NO_RAR_TOOL: &str = "Kiln can't open .rar files on this PC. Extract this .rar first, then drop the folder.";

fn modpack_refused() -> Error {
    Error::new(ErrorCode::ConsentRequired, "A modpack must be a .zip, because installing one needs your consent. Drop the modpack's .zip.")
}

fn is_modpack_manifest(rel: &str) -> bool {
    Path::new(rel).file_name().and_then(|n| n.to_str()).is_some_and(|n| n.eq_ignore_ascii_case("kiln-modpack.json"))
}

// ---- 7z ----

/// The memory the 7z decoder may take for a dictionary or a header (KiB): 7-Zip's own defaults go to 64 MiB.
#[cfg(feature = "sevenz")]
const SEVENZ_MEMORY_KB: usize = 256 * 1024;

#[cfg(feature = "sevenz")]
fn sevenz_error(path: &Path, e: sevenz_rust2::Error) -> Error {
    use sevenz_rust2::Error as E;
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    match e {
        E::PasswordRequired | E::MaybeBadPassword(_) => Error::new(ErrorCode::Unsupported,
            format!("{name} is password-protected. Kiln can't open it: extract it first, then drop the folder.")),
        E::MaxMemLimited { .. } => Error::new(ErrorCode::Refused, format!("{name} needs too much memory to unpack (its dictionary is larger than 256 MiB).")),
        E::UnsupportedCompressionMethod(m) => Error::new(ErrorCode::Unsupported,
            format!("{name} uses the compression method {m}, which Kiln can't read. Extract it first, then drop the folder.")),
        other => Error::new(ErrorCode::Parse, format!("{name} is not a readable 7z ({other:?}). It may be damaged or only one part of a split archive.")),
    }
}

#[cfg(feature = "sevenz")]
fn open_sevenz(path: &Path) -> Result<sevenz_rust2::ArchiveReader<std::fs::File>> {
    let f = std::fs::File::open(path).map_err(crate::util::io_at(path, "opening"))?;
    sevenz_rust2::ArchiveReader::new_with_memory_limit(f, sevenz_rust2::Password::empty(), SEVENZ_MEMORY_KB).map_err(|e| sevenz_error(path, e))
}

/// The entry rules of a 7z, from its header alone: count, names, links, declared sizes. Returns each file's
/// archive-relative path.
#[cfg(feature = "sevenz")]
fn check_sevenz_entries(archive: &sevenz_rust2::Archive) -> Result<Vec<String>> {
    use crate::quick::{MAX_IMPORT_BYTES, MAX_IMPORT_DEPTH, MAX_IMPORT_ENTRIES, MAX_IMPORT_FILE};
    if archive.files.len() > MAX_IMPORT_ENTRIES {
        return Err(Error::new(ErrorCode::Refused, "Too many files in this 7z (limit: 10000 entries)."));
    }
    let mut total = 0u64;
    let mut names = Vec::with_capacity(archive.files.len());
    for e in &archive.files {
        if e.is_anti_item {
            continue;
        }
        let name = e.name.replace('\\', "/");
        let rel = crate::bundle::safe_rel_path(name.trim_end_matches('/'))?;
        if Path::new(rel).components().count() > MAX_IMPORT_DEPTH {
            return Err(Error::new(ErrorCode::Refused, "Paths in this 7z are too deeply nested."));
        }
        if is_modpack_manifest(rel) {
            return Err(modpack_refused());
        }
        if e.has_windows_attributes {
            let a = e.windows_attributes;
            // 0x400: a reparse point (Windows link); 0x8000 marks unix mode bits in the high half (p7zip, 7-Zip on Linux)
            let unix_link = a & 0x8000 != 0 && (a >> 16) & 0o170000 == 0o120000;
            let unix_special = a & 0x8000 != 0 && !matches!((a >> 16) & 0o170000, 0 | 0o100000 | 0o040000);
            if a & 0x400 != 0 || unix_link || unix_special {
                return Err(Error::new(ErrorCode::Refused, format!("{name}: links and special files are not allowed in a mod")));
            }
        }
        if !e.is_directory {
            if e.size > MAX_IMPORT_FILE || total.saturating_add(e.size) > MAX_IMPORT_BYTES {
                return Err(Error::new(ErrorCode::Refused, "Expanded 7z is too large (limit: 512 MiB per file, 1 GiB total)."));
            }
            total += e.size;
        }
        names.push(rel.to_string());
    }
    Ok(names)
}

/// What `quick::validate_import_path` checks of a 7z before anything is unpacked.
#[cfg(feature = "sevenz")]
pub(crate) fn validate_sevenz(path: &Path) -> Result<()> {
    let r = open_sevenz(path)?;
    check_sevenz_entries(r.archive()).map(|_| ())
}

#[cfg(feature = "sevenz")]
pub(crate) fn unpack_sevenz(path: &Path, dest: &Path) -> Result<()> {
    use crate::quick::{MAX_IMPORT_BYTES, MAX_IMPORT_FILE};
    use std::io::Read;
    let mut r = open_sevenz(path)?;
    check_sevenz_entries(r.archive())?;
    let mut total = 0u64;
    let mut failure: Option<Error> = None;
    let outcome = r.for_each_entries(|entry, reader| {
        if entry.is_anti_item {
            return Ok(true);
        }
        let name = entry.name.replace('\\', "/");
        let step = (|| -> Result<()> {
            let rel = crate::bundle::safe_rel_path(name.trim_end_matches('/'))?;
            let target = dest.join(rel);
            if entry.is_directory {
                return std::fs::create_dir_all(&target).map_err(crate::util::io_at(&target, "creating"));
            }
            if let Some(p) = target.parent() {
                std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?;
            }
            let mut out = std::fs::File::create(&target).map_err(crate::util::io_at(&target, "writing"))?;
            let allowance = MAX_IMPORT_FILE.min(MAX_IMPORT_BYTES - total);
            let n = std::io::copy(&mut reader.take(allowance + 1), &mut out).map_err(crate::util::io_at(&target, "writing"))?;
            if n > allowance || n != entry.size {
                return Err(Error::new(ErrorCode::Refused, "Expanded 7z exceeds its declared size or the import limits."));
            }
            total += n;
            Ok(())
        })();
        match step {
            Ok(()) => Ok(true),
            Err(e) => {
                failure = Some(e);
                Err(sevenz_rust2::Error::Other("stopped".into()))
            }
        }
    });
    if let Some(e) = failure {
        return Err(e);
    }
    outcome.map_err(|e| sevenz_error(path, e))
}

#[cfg(not(feature = "sevenz"))]
pub(crate) fn validate_sevenz(_path: &Path) -> Result<()> {
    Ok(())
}

#[cfg(not(feature = "sevenz"))]
pub(crate) fn unpack_sevenz(_path: &Path, _dest: &Path) -> Result<()> {
    Err(Error::new(ErrorCode::Unsupported, "This build of Kiln can't open .7z files. Extract this .7z first, then drop the folder."))
}

// ---- rar, with a tool that is already installed ----

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum ToolKind {
    Unrar,
    SevenZip,
    Bsdtar,
}

#[derive(Clone, Debug)]
pub(crate) struct Tool {
    pub path: PathBuf,
    pub kind: ToolKind,
}

/// `name` in the absolute folders of `PATH` (relative entries, the current folder among them, never count).
fn on_path(name: &str) -> Option<PathBuf> {
    let paths = std::env::var_os("PATH")?;
    let file = if cfg!(windows) && !name.to_ascii_lowercase().ends_with(".exe") { format!("{name}.exe") } else { name.to_string() };
    std::env::split_paths(&paths).filter(|d| d.is_absolute()).map(|d| d.join(&file)).find(|p| p.is_file())
}

#[cfg(windows)]
fn well_known() -> Vec<Tool> {
    let var = |n: &str| std::env::var_os(n).map(PathBuf::from).filter(|p| p.is_absolute());
    let mut v = Vec::new();
    for pf in [var("ProgramFiles"), var("ProgramFiles(x86)")].into_iter().flatten() {
        v.push(Tool { path: pf.join("7-Zip").join("7z.exe"), kind: ToolKind::SevenZip });
        v.push(Tool { path: pf.join("WinRAR").join("UnRAR.exe"), kind: ToolKind::Unrar });
    }
    // Windows' own tar.exe is bsdtar (libarchive)
    if let Some(root) = var("SystemRoot") {
        v.push(Tool { path: root.join("System32").join("tar.exe"), kind: ToolKind::Bsdtar });
    }
    v.into_iter().filter(|t| t.path.is_file()).collect()
}

#[cfg(not(windows))]
fn well_known() -> Vec<Tool> {
    Vec::new()
}

/// A tool that can read a rar, best reader first: `unrar`, then 7-Zip, then bsdtar.
pub(crate) fn find_rar_tool() -> Option<Tool> {
    let mut all: Vec<Tool> = Vec::new();
    for (name, kind) in [("unrar", ToolKind::Unrar), ("7zz", ToolKind::SevenZip), ("7z", ToolKind::SevenZip), ("bsdtar", ToolKind::Bsdtar)] {
        if let Some(path) = on_path(name) {
            all.push(Tool { path, kind });
        }
    }
    all.extend(well_known());
    for kind in [ToolKind::Unrar, ToolKind::SevenZip, ToolKind::Bsdtar] {
        if let Some(t) = all.iter().find(|t| t.kind == kind) {
            return Some(t.clone());
        }
    }
    None
}

fn tool_command(tool: &Tool, archive: &Path, dest: &Path, cwd: &Path) -> std::process::Command {
    use std::process::Stdio;
    let mut c = crate::hostenv::command(&tool.path);
    match tool.kind {
        // x: with paths; -y: yes to all (never ask); -idq: quiet; -p-: no password prompt; -o+: overwrite
        ToolKind::Unrar => {
            let mut target = dest.as_os_str().to_os_string();
            target.push(std::path::MAIN_SEPARATOR_STR);
            c.args(["x", "-y", "-idq", "-p-", "-o+"]).arg(archive).arg(target);
        }
        // -o<dir>: output; -aoa: overwrite; -bd/-bso0/-bsp0: no progress or output; -spe is not needed
        ToolKind::SevenZip => {
            let mut out = std::ffi::OsString::from("-o");
            out.push(dest);
            c.args(["x", "-y", "-aoa", "-bd", "-bso0", "-bsp0", "-p-"]).arg(out).arg(archive);
        }
        // libarchive refuses absolute paths and `..` unless -P is given
        ToolKind::Bsdtar => {
            c.arg("-x").arg("-f").arg(archive).arg("-C").arg(dest).args(["--no-same-owner", "--no-same-permissions"]);
        }
    }
    c.current_dir(cwd).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null());
    #[cfg(windows)]
    {
        // no console window flashing up from the launcher (CREATE_NO_WINDOW)
        use std::os::windows::process::CommandExt;
        c.creation_flags(0x0800_0000);
    }
    c
}

/// Entries and bytes below `dir` (links counted, never followed), stopping once past the import limits.
fn usage(dir: &Path) -> (usize, u64) {
    let (mut entries, mut bytes) = (0usize, 0u64);
    let mut pending = vec![dir.to_path_buf()];
    while let Some(d) = pending.pop() {
        let Ok(rd) = std::fs::read_dir(&d) else { continue };
        for e in rd.flatten() {
            entries += 1;
            let Ok(m) = std::fs::symlink_metadata(e.path()) else { continue };
            if m.is_dir() {
                pending.push(e.path());
            } else {
                bytes = bytes.saturating_add(m.len());
            }
            if entries > crate::quick::MAX_IMPORT_ENTRIES || bytes > crate::quick::MAX_IMPORT_BYTES {
                return (entries, bytes);
            }
        }
    }
    (entries, bytes)
}

/// How long a tool may run before it is stopped.
const RAR_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(10 * 60);

/// Unpacks a rar into `dest` (an empty private folder) with `tool`; `scratch` is where its empty working folder goes.
pub(crate) fn unpack_rar_with(tool: Option<Tool>, archive: &Path, dest: &Path, scratch: &Path) -> Result<()> {
    let tool = tool.ok_or_else(|| Error::new(ErrorCode::Unsupported, NO_RAR_TOOL))?;
    let cwd = TempDir::new(scratch, "kiln-rar-cwd")?;
    let mut child = tool_command(&tool, archive, dest, cwd.path())
        .spawn()
        .map_err(|e| Error::new(ErrorCode::Unsupported, format!("{} did not start ({e}). Extract this .rar first, then drop the folder.", tool.path.display())))?;
    let started = std::time::Instant::now();
    let status = loop {
        match child.try_wait() {
            Ok(Some(s)) => break s,
            Ok(None) => {}
            Err(e) => {
                let _ = child.kill();
                return Err(Error::from(e));
            }
        }
        let (entries, bytes) = usage(dest);
        if entries > crate::quick::MAX_IMPORT_ENTRIES || bytes > crate::quick::MAX_IMPORT_BYTES || started.elapsed() > RAR_TIMEOUT {
            let _ = child.kill();
            let _ = child.wait();
            return Err(Error::new(ErrorCode::Refused, if started.elapsed() > RAR_TIMEOUT {
                "This .rar took too long to unpack."
            } else {
                "Expanded .rar exceeds the import limits (10000 entries, 1 GiB)."
            }));
        }
        std::thread::sleep(std::time::Duration::from_millis(50));
    };
    if !status.success() {
        return Err(Error::new(ErrorCode::Parse, "Kiln couldn't unpack this .rar. It may be damaged, split in parts or password-protected: extract it first, then drop the folder."));
    }
    // nothing may have gone anywhere but the target: the working folder stays empty
    if std::fs::read_dir(cwd.path()).map_err(crate::util::io_at(cwd.path(), "reading"))?.next().is_some() {
        return Err(Error::new(ErrorCode::Refused, "This .rar wrote outside its folder."));
    }
    crate::quick::validate_import_path(dest)?;
    let mut pending = vec![dest.to_path_buf()];
    while let Some(d) = pending.pop() {
        for e in std::fs::read_dir(&d).map_err(crate::util::io_at(&d, "reading"))? {
            let p = e?.path();
            if p.is_dir() {
                pending.push(p.clone());
            }
            if is_modpack_manifest(&p.to_string_lossy()) {
                return Err(modpack_refused());
            }
        }
    }
    Ok(())
}

pub(crate) fn unpack_rar(archive: &Path, dest: &Path, scratch: &Path) -> Result<()> {
    unpack_rar_with(find_rar_tool(), archive, dest, scratch)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn without_a_tool_the_message_says_what_to_do() {
        let t = TempDir::new(&std::env::temp_dir(), "rar-none").unwrap();
        let e = unpack_rar_with(None, Path::new("x.rar"), t.path(), t.path()).unwrap_err();
        assert_eq!(e.code, ErrorCode::Unsupported);
        assert_eq!(e.message, "Kiln can't open .rar files on this PC. Extract this .rar first, then drop the folder.");
    }

    #[test]
    fn a_relative_path_entry_is_never_searched() {
        // the tool lookup only takes absolute PATH folders
        let found = find_rar_tool();
        if let Some(t) = found {
            assert!(t.path.is_absolute(), "{}", t.path.display());
        }
    }
}
