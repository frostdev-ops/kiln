//! What a file's bytes gave, remembered while the file is unchanged: its sha256 ([`sha256`]) and any value parsed from it
//! ([`Memo`]). "Unchanged" is a [`FileKey`]: size and mtime, and what a rewrite always changes besides: on Unix the inode
//! and ctime (no tool can set ctime back), on Windows the creation time. A file rewritten in place, replaced by rename or
//! copied over with its old mtime gets a new key.
//!
//! The sha256s can also outlive the process: [`persist_at`] (the launcher calls it at start) loads them from a file and
//! [`flush`] writes new ones back, so a launcher start does not hash the installed runtime and every bundle zip again
//! (1.5 GB on a real install). Without it (kilnctl, tests) they live in memory only.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::SystemTime;

/// A file's identity for caching (see the module docs). None when the file cannot be stat'ed or has no mtime.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
pub struct FileKey {
    pub len: u64,
    /// mtime as nanoseconds since the epoch (before: negative)
    pub mtime: i128,
    /// Unix: inode; Windows: 0.
    pub ino: u64,
    /// Unix: ctime in nanoseconds; Windows: the creation time (FILETIME ticks).
    pub changed: i128,
}

fn nanos(t: SystemTime) -> i128 {
    match t.duration_since(SystemTime::UNIX_EPOCH) {
        Ok(d) => d.as_nanos() as i128,
        Err(e) => -(e.duration().as_nanos() as i128),
    }
}

impl FileKey {
    pub fn of(path: &Path) -> Option<FileKey> {
        let md = std::fs::metadata(path).ok()?;
        Self::from_metadata(&md)
    }

    pub fn from_metadata(md: &std::fs::Metadata) -> Option<FileKey> {
        let mtime = nanos(md.modified().ok()?);
        #[cfg(unix)]
        {
            use std::os::unix::fs::MetadataExt;
            Some(FileKey { len: md.len(), mtime, ino: md.ino(), changed: md.ctime() as i128 * 1_000_000_000 + md.ctime_nsec() as i128 })
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            Some(FileKey { len: md.len(), mtime, ino: 0, changed: md.creation_time() as i128 })
        }
        #[cfg(not(any(unix, windows)))]
        {
            Some(FileKey { len: md.len(), mtime, ino: 0, changed: 0 })
        }
    }
}

// ---------------------------------------------------------------- sha256

#[derive(Default)]
struct Hashes {
    map: HashMap<PathBuf, (FileKey, String)>,
    store: Option<PathBuf>,
    dirty: bool,
}

const MAX_ENTRIES: usize = 100_000;

#[derive(serde::Serialize, serde::Deserialize)]
struct StoreFile {
    format: u32,
    files: Vec<StoreEntry>,
}

#[derive(serde::Serialize, serde::Deserialize)]
struct StoreEntry {
    path: PathBuf,
    key: FileKey,
    sha256: String,
}

fn hashes() -> &'static Mutex<Hashes> {
    static H: OnceLock<Mutex<Hashes>> = OnceLock::new();
    H.get_or_init(Default::default)
}

/// The sha256 (hex) of `path`, hashed only when its [`FileKey`] changed since the last time.
pub fn sha256(path: &Path) -> crate::Result<String> {
    let key = FileKey::of(path);
    if let Some(k) = key
        && let Ok(h) = hashes().lock()
        && let Some((have, sha)) = h.map.get(path)
        && *have == k
    {
        return Ok(sha.clone());
    }
    let (sha, _) = crate::hash::sha256_file(path)?;
    // remembered only when the file did not change while it was read
    if let Some(k) = key
        && FileKey::of(path) == Some(k)
        && let Ok(mut h) = hashes().lock()
    {
        if h.map.len() >= MAX_ENTRIES {
            h.map.clear();
        }
        h.map.insert(path.to_path_buf(), (k, sha.clone()));
        h.dirty = true;
    }
    Ok(sha)
}

/// Keeps the sha256s in `file` across runs: loads what it holds now (entries whose file changed are simply never hit),
/// and [`flush`] writes new ones there. A missing or broken file starts empty.
pub fn persist_at(file: &Path) {
    let loaded: Option<StoreFile> = std::fs::read(file).ok().and_then(|b| serde_json::from_slice(&b).ok()).filter(|s: &StoreFile| s.format == 1);
    let Ok(mut h) = hashes().lock() else { return };
    if let Some(s) = loaded {
        for e in s.files {
            h.map.entry(e.path).or_insert((e.key, e.sha256));
        }
    }
    h.store = Some(file.to_path_buf());
}

/// Stops keeping the sha256s in the [`persist_at`] file: the next [`flush`] writes nothing; the sha256s in memory stay
/// (and are written again after a new [`persist_at`]). The launcher calls it when a move of its data folder starts, so no
/// flush puts the cache back into the old folder, and [`persist_at`] again after a move that failed or was canceled.
pub fn unpersist() {
    if let Ok(mut h) = hashes().lock() {
        h.store = None;
    }
}

/// Writes the sha256s to the [`persist_at`] file when new ones came in (only entries whose file still exists unchanged).
/// No-op without one.
pub fn flush() {
    let (store, files) = {
        let Ok(mut h) = hashes().lock() else { return };
        let Some(store) = h.store.clone().filter(|_| h.dirty) else { return };
        h.dirty = false;
        let files: Vec<StoreEntry> = h.map.iter().map(|(p, (k, s))| StoreEntry { path: p.clone(), key: *k, sha256: s.clone() }).collect();
        (store, files)
    };
    let files = files.into_iter().filter(|e| FileKey::of(&e.path) == Some(e.key)).collect();
    if let Err(e) = crate::util::write_json_atomic(&store, &StoreFile { format: 1, files }) {
        eprintln!("file hash cache {}: {e}", store.display());
    }
}

// ---------------------------------------------------------------- parsed values

/// A value parsed from one file per path, reused while the file's [`FileKey`] is the same. `T` is cloned out (wrap
/// big values in an `Arc`).
pub struct Memo<T> {
    map: Mutex<HashMap<PathBuf, (FileKey, T)>>,
    cap: usize,
}

impl<T: Clone> Memo<T> {
    pub fn new(cap: usize) -> Memo<T> {
        Memo { map: Mutex::new(HashMap::new()), cap }
    }

    /// `parse(path)` unless the file is unchanged since the last call that succeeded; errors are never remembered.
    pub fn get<E>(&self, path: &Path, parse: impl FnOnce(&Path) -> Result<T, E>) -> Result<T, E> {
        let key = FileKey::of(path);
        if let Some(k) = key
            && let Ok(m) = self.map.lock()
            && let Some((have, v)) = m.get(path)
            && *have == k
        {
            return Ok(v.clone());
        }
        let v = parse(path)?;
        if let Some(k) = key
            && FileKey::of(path) == Some(k)
            && let Ok(mut m) = self.map.lock()
        {
            if m.len() >= self.cap {
                m.clear();
            }
            m.insert(path.to_path_buf(), (k, v.clone()));
        }
        Ok(v)
    }

    pub fn clear(&self) {
        if let Ok(mut m) = self.map.lock() {
            m.clear();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(tag: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("km-filecache-{tag}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn a_rewrite_with_the_same_size_and_mtime_is_a_new_key() {
        let d = tmp("key");
        let p = d.join("f.bin");
        std::fs::write(&p, b"aaaa").unwrap();
        let t = std::fs::metadata(&p).unwrap().modified().unwrap();
        let k1 = FileKey::of(&p).unwrap();
        assert_eq!(FileKey::of(&p), Some(k1), "stable while untouched");
        assert_eq!(sha256(&p).unwrap(), crate::hash::sha256_hex(b"aaaa"));
        // replaced by rename (what every atomic writer does), same size, mtime set back
        std::fs::write(d.join("g.bin"), b"bbbb").unwrap();
        std::fs::rename(d.join("g.bin"), &p).unwrap();
        std::fs::File::options().write(true).open(&p).unwrap().set_modified(t).unwrap();
        assert_ne!(FileKey::of(&p), Some(k1));
        assert_eq!(sha256(&p).unwrap(), crate::hash::sha256_hex(b"bbbb"));
        // rewritten in place, same size, mtime set back
        std::fs::write(&p, b"cccc").unwrap();
        std::fs::File::options().write(true).open(&p).unwrap().set_modified(t).unwrap();
        #[cfg(unix)]
        assert_eq!(sha256(&p).unwrap(), crate::hash::sha256_hex(b"cccc"), "ctime moved");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn memo_reparses_only_after_a_change_and_never_keeps_errors() {
        let d = tmp("memo");
        let p = d.join("x.json");
        let m: Memo<String> = Memo::new(8);
        let calls = std::cell::Cell::new(0);
        let read = |p: &Path| -> Result<String, std::io::Error> {
            calls.set(calls.get() + 1);
            std::fs::read_to_string(p)
        };
        assert!(m.get(&p, read).is_err(), "missing");
        assert!(m.get(&p, read).is_err(), "still missing: errors are not remembered");
        assert_eq!(calls.get(), 2);
        std::fs::write(&p, "one").unwrap();
        assert_eq!(m.get(&p, read).unwrap(), "one");
        assert_eq!(m.get(&p, read).unwrap(), "one");
        assert_eq!(calls.get(), 3, "second read came from the memo");
        std::fs::write(d.join("y"), "two").unwrap();
        std::fs::rename(d.join("y"), &p).unwrap();
        assert_eq!(m.get(&p, read).unwrap(), "two");
        std::fs::remove_file(&p).unwrap();
        assert!(m.get(&p, read).is_err(), "deleted: read again");
        let _ = std::fs::remove_dir_all(&d);
    }

    /// The persisted store is one per process: the tests that set it take turns.
    static PERSIST: Mutex<()> = Mutex::new(());

    #[test]
    fn persisted_hashes_load_and_stale_ones_are_not_used() {
        let _turn = PERSIST.lock().unwrap_or_else(|e| e.into_inner());
        let d = tmp("persist");
        let p = d.join("big.bin");
        std::fs::write(&p, b"payload").unwrap();
        let k = FileKey::of(&p).unwrap();
        // a store from an earlier run: one entry right, one for a file that changed since (its key is old)
        let q = d.join("changed.bin");
        std::fs::write(&q, b"new bytes").unwrap();
        let mut old = FileKey::of(&q).unwrap();
        old.mtime -= 1_000_000_000;
        let store = d.join("hashes.json");
        let s = StoreFile { format: 1, files: vec![
            StoreEntry { path: p.clone(), key: k, sha256: "remembered".into() },
            StoreEntry { path: q.clone(), key: old, sha256: "stale".into() },
        ] };
        std::fs::write(&store, serde_json::to_vec(&s).unwrap()).unwrap();
        persist_at(&store);
        assert_eq!(sha256(&p).unwrap(), "remembered", "an unchanged file is not hashed again");
        assert_eq!(sha256(&q).unwrap(), crate::hash::sha256_hex(b"new bytes"), "a changed one is");
        flush();
        let back: StoreFile = serde_json::from_slice(&std::fs::read(&store).unwrap()).unwrap();
        let q_entry = back.files.iter().find(|e| e.path == q).unwrap();
        assert_eq!(q_entry.sha256, crate::hash::sha256_hex(b"new bytes"));
        // stop persisting for the other tests of this process
        hashes().lock().unwrap().store = None;
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn a_flush_after_unpersist_writes_nothing() {
        let _turn = PERSIST.lock().unwrap_or_else(|e| e.into_inner());
        let d = tmp("unpersist");
        let store = d.join("state/file-hashes.json");
        persist_at(&store);
        std::fs::write(d.join("a.bin"), b"a").unwrap();
        sha256(&d.join("a.bin")).unwrap();
        flush();
        assert!(store.is_file(), "persisting: the flush writes the store");
        std::fs::remove_file(&store).unwrap();
        unpersist();
        std::fs::write(d.join("b.bin"), b"b").unwrap();
        assert_eq!(sha256(&d.join("b.bin")).unwrap(), crate::hash::sha256_hex(b"b"));
        flush();
        assert!(!store.exists(), "after unpersist a flush writes nothing");
        assert!(hashes().lock().unwrap().map.contains_key(&d.join("b.bin")), "the sha256s in memory stay");
        persist_at(&store);
        flush();
        assert!(store.is_file(), "persisting again: the new sha256 is written");
        unpersist();
        let _ = std::fs::remove_dir_all(&d);
    }
}
