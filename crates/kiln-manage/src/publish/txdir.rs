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

/// The publish folder, transactions folder and transaction itself must be real directories, not aliases outside
/// the staging tree. The user's data root may itself live on a mounted or linked volume.
pub(crate) fn check_directory(dir: &Path) -> Result<()> {
    for path in dir.ancestors().take(3) {
        let meta = std::fs::symlink_metadata(path).map_err(|_| Error::new(ErrorCode::HashMismatch, "The transaction folder is missing or was replaced."))?;
        if !meta.is_dir() || meta.file_type().is_symlink() {
            return Err(Error::new(ErrorCode::HashMismatch, "A transaction folder is a link or is not a directory. Nothing was sent."));
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if meta.file_attributes() & 0x400 != 0 { return Err(Error::new(ErrorCode::HashMismatch, "A transaction folder is a reparse point. Nothing was sent.")); }
        }
    }
    Ok(())
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
    if name == ARTIFACT && meta.len() > super::policy::MAX_ZIP_BYTES {
        return Err(super::policy::Refusal::new("too-large", None, "the source zip is larger than 512 MiB").into());
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
        total = total.checked_add(n as u64).ok_or_else(|| Error::new(ErrorCode::Refused, "The source file grew too large."))?;
        if name == ARTIFACT && total > super::policy::MAX_ZIP_BYTES {
            return Err(super::policy::Refusal::new("too-large", None, "the source zip grew beyond 512 MiB while copying").into());
        }
        hasher.update(&buf[..n]);
        f.write_all(&buf[..n]).map_err(crate::util::io_at(&path, "writing"))?;
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
    check_directory(dir)?;
    let meta = std::fs::symlink_metadata(&path).map_err(|_| changed())?;
    if !meta.file_type().is_file() {
        return Err(changed());
    }
    let mut options = std::fs::OpenOptions::new();
    options.read(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.custom_flags(0x00200000); // FILE_FLAG_OPEN_REPARSE_POINT
    }
    let mut f = options.open(&path).map_err(|_| changed())?;
    let opened = f.metadata().map_err(|_| changed())?;
    if !opened.is_file() { return Err(changed()); }
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if opened.nlink() != 1 { return Err(changed()); }
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if opened.file_attributes() & 0x400 != 0 { return Err(changed()); }
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle};
        let mut info = std::mem::MaybeUninit::<BY_HANDLE_FILE_INFORMATION>::uninit();
        if unsafe { GetFileInformationByHandle(f.as_raw_handle(), info.as_mut_ptr()) } == 0
            || unsafe { info.assume_init() }.nNumberOfLinks != 1 { return Err(changed()); }
    }
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

/// Hashes the bytes actually consumed by a transport, even if another handle modifies the same inode after the
/// preflight. Completion is recorded on the last chunk (a length-delimited transport need not ask for EOF).
pub(crate) struct UploadReader {
    file: std::fs::File,
    hasher: sha2::Sha256,
    left: u64,
    expected: String,
    verified: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

impl UploadReader {
    pub(crate) fn new(file: std::fs::File, bytes: u64, expected: &str) -> (Self, std::sync::Arc<std::sync::atomic::AtomicBool>) {
        use sha2::Digest;
        let verified = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(bytes == 0 && crate::hash::sha256_hex(&[]) == expected));
        (Self { file, hasher: sha2::Sha256::new(), left: bytes, expected: expected.into(), verified: verified.clone() }, verified)
    }
}

impl Read for UploadReader {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        use sha2::Digest;
        if self.left == 0 || buf.is_empty() { return Ok(0); }
        let want = buf.len().min(usize::try_from(self.left).unwrap_or(usize::MAX));
        let n = self.file.read(&mut buf[..want])?;
        if n == 0 { return Err(std::io::Error::new(std::io::ErrorKind::UnexpectedEof, "the frozen upload was shortened")); }
        self.left -= n as u64;
        self.hasher.update(&buf[..n]);
        if self.left == 0 {
            if crate::hash::hex(&self.hasher.clone().finalize()) != self.expected {
                return Err(std::io::Error::new(std::io::ErrorKind::InvalidData, "the frozen upload changed while streaming"));
            }
            self.verified.store(true, std::sync::atomic::Ordering::Release);
        }
        Ok(n)
    }
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
