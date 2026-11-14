//! The folder of one publish transaction: `<data>/publish/transactions/<uuid>/`, owner-only, holding the frozen
//! inputs (`artifact.zip`, `config.json`, `notes.utf8`, `kiln-release.json`, `artifact.sha256`), `plan.json`, later
//! `journal.json`, and `origin.json` (where the inputs came from on this PC: local, never part of the plan).
//!
//! Frozen means: written once to a new file, flushed to disk with its folder, made read-only, and from then on
//! only ever opened for reading and rehashed before use.

use crate::{Error, ErrorCode, Result};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

pub const ARTIFACT: &str = "artifact.zip";
pub const CONFIG: &str = "config.json";
pub const NOTES: &str = "notes.utf8";
pub const RELEASE: &str = "kiln-release.json";
pub const CHECKSUM: &str = "artifact.sha256";
pub const PLAN: &str = "plan.json";
pub const ORIGIN: &str = "origin.json";
pub const JOURNAL: &str = "journal.json";

pub fn transactions(data_root: &Path) -> PathBuf {
    data_root.join("publish").join("transactions")
}

/// A transaction id is a version 4 UUID in its canonical lower-case form: the only thing ever joined to the
/// transactions folder.
pub fn is_tx_id(s: &str) -> bool {
    s.len() == 36
        && s.bytes().enumerate().all(|(i, b)| match i {
            8 | 13 | 18 | 23 => b == b'-',
            _ => b.is_ascii_digit() || (b'a'..=b'f').contains(&b),
        })
}

pub fn dir_of(data_root: &Path, tx_id: &str) -> Result<PathBuf> {
    if !is_tx_id(tx_id) {
        return Err(Error::invalid("that is not a transaction id"));
    }
    Ok(transactions(data_root).join(tx_id))
}

/// Creates the folder of a new transaction (and its parents), for the owner only. Fails when it exists.
pub fn create(data_root: &Path, tx_id: &str) -> Result<PathBuf> {
    let dir = dir_of(data_root, tx_id)?;
    let parent = transactions(data_root);
    let mut b = std::fs::DirBuilder::new();
    b.recursive(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        b.mode(0o700);
    }
    b.create(&parent).map_err(crate::util::io_at(&parent, "creating"))?;
    b.recursive(false);
    b.create(&dir).map_err(crate::util::io_at(&dir, "creating"))?;
    sync_dir(&parent)?;
    Ok(dir)
}

/// Flushes a folder's entries to disk (Unix; on Windows a file's own flush covers its entry).
pub fn sync_dir(dir: &Path) -> Result<()> {
    #[cfg(unix)]
    std::fs::File::open(dir).and_then(|f| f.sync_all()).map_err(crate::util::io_at(dir, "flushing"))?;
    #[cfg(not(unix))]
    let _ = dir;
    Ok(())
}

fn finish(mut f: std::fs::File, path: &Path) -> Result<()> {
    f.flush().map_err(crate::util::io_at(path, "writing"))?;
    f.sync_all().map_err(crate::util::io_at(path, "flushing"))?;
    drop(f);
    let mut perm = std::fs::metadata(path).map_err(crate::util::io_at(path, "reading"))?.permissions();
    perm.set_readonly(true);
    std::fs::set_permissions(path, perm).map_err(crate::util::io_at(path, "protecting"))
}

fn create_new(path: &Path) -> Result<std::fs::File> {
    let mut o = std::fs::OpenOptions::new();
    o.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        o.mode(0o600);
    }
    o.open(path).map_err(crate::util::io_at(path, "writing"))
}

/// Writes a new frozen file. Returns its SHA-256 and size.
pub fn freeze_bytes(dir: &Path, name: &str, data: &[u8]) -> Result<(String, u64)> {
    let path = dir.join(name);
    let mut f = create_new(&path)?;
    f.write_all(data).map_err(crate::util::io_at(&path, "writing"))?;
    finish(f, &path)?;
    Ok((crate::hash::sha256_hex(data), data.len() as u64))
}

/// Copies `src` into a new frozen file (a link is refused: the copy must be of a real file). Returns the SHA-256
/// and size of what was written.
pub fn freeze_copy(dir: &Path, name: &str, src: &Path) -> Result<(String, u64)> {
    use sha2::Digest;
    let meta = std::fs::symlink_metadata(src).map_err(crate::util::io_at(src, "reading"))?;
    if !meta.file_type().is_file() {
        return Err(Error::new(ErrorCode::Refused, format!("{} is not a regular file.", src.display())));
    }
    let mut input = std::fs::File::open(src).map_err(crate::util::io_at(src, "reading"))?;
    let path = dir.join(name);
    let mut f = create_new(&path)?;
    let mut hasher = sha2::Sha256::new();
    let mut buf = vec![0u8; 256 * 1024];
    let mut total = 0u64;
    loop {
        let n = input.read(&mut buf).map_err(crate::util::io_at(src, "reading"))?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        f.write_all(&buf[..n]).map_err(crate::util::io_at(&path, "writing"))?;
        total += n as u64;
    }
    finish(f, &path)?;
    Ok((crate::hash::hex(&hasher.finalize()), total))
}

/// Makes an already written file of the folder frozen (flush, read-only) and returns its hash and size.
pub fn freeze_existing(dir: &Path, name: &str) -> Result<(String, u64)> {
    let path = dir.join(name);
    let f = std::fs::OpenOptions::new().read(true).open(&path).map_err(crate::util::io_at(&path, "reading"))?;
    f.sync_all().map_err(crate::util::io_at(&path, "flushing"))?;
    drop(f);
    let mut perm = std::fs::metadata(&path).map_err(crate::util::io_at(&path, "reading"))?.permissions();
    perm.set_readonly(true);
    std::fs::set_permissions(&path, perm).map_err(crate::util::io_at(&path, "protecting"))?;
    crate::hash::sha256_file(&path)
}

/// Opens a frozen file once, refusing a link, and checks its hash and size by reading it whole. The returned handle
/// is rewound: stream from it, do not open the path again.
pub fn open_verified(dir: &Path, name: &str, sha256: &str, bytes: Option<u64>) -> Result<std::fs::File> {
    use sha2::Digest;
    use std::io::Seek;
    let path = dir.join(name);
    let changed = || Error::new(ErrorCode::HashMismatch, format!("{name} of this transaction is missing or was changed. Nothing was sent; make a new plan."));
    let meta = std::fs::symlink_metadata(&path).map_err(|_| changed())?;
    if !meta.file_type().is_file() {
        return Err(changed());
    }
    let mut f = std::fs::File::open(&path).map_err(|_| changed())?;
    let mut hasher = sha2::Sha256::new();
    let mut buf = vec![0u8; 256 * 1024];
    let mut total = 0u64;
    loop {
        let n = f.read(&mut buf).map_err(|_| changed())?;
        if n == 0 {
            break;
        }
        hasher.update(&buf[..n]);
        total += n as u64;
    }
    if crate::hash::hex(&hasher.finalize()) != sha256 || bytes.is_some_and(|b| b != total) {
        return Err(changed());
    }
    f.rewind().map_err(|_| changed())?;
    Ok(f)
}

/// Removes a transaction folder that never got a plan (a failed plan step). Read-only files are made writable first.
pub fn discard(dir: &Path) {
    if let Ok(rd) = std::fs::read_dir(dir) {
        for e in rd.flatten() {
            if let Ok(m) = e.metadata() {
                let mut p = m.permissions();
                #[allow(clippy::permissions_set_readonly_false)]
                p.set_readonly(false);
                let _ = std::fs::set_permissions(e.path(), p);
            }
        }
    }
    let _ = std::fs::remove_dir_all(dir);
}
