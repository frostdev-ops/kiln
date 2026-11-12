use std::{collections::{BTreeSet, HashMap}, path::PathBuf};

use serde::{Deserialize, Serialize};

mod io;
// kiln-eml: fingerprint reuse (bisdat/kiln-mods#17)
mod prints;
mod stamp;
mod track;

pub use prints::{FingerprintStats, Fingerprints, Need};
pub(crate) use prints::CRC64;
#[cfg(test)]
pub(crate) use prints::RACY_WINDOW_OVERRIDE;
use stamp::Stamp;

const BUILD_ID: &str = env!("BUILD_ID");

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct FileStateCache {
    build_id: String,
    files: HashMap<PathBuf, (u64, u128, u64)>,
    // kiln-eml: the OS stamp each fingerprint above was taken under, for the files where it can tell a later change
    // (prints.rs). Not part of what `diff` compares; a cache file from before this field loads with none, so its
    // files are read once and the next save carries stamps.
    #[serde(default, skip_serializing_if = "HashMap::is_empty")]
    stamps: HashMap<PathBuf, Stamp>,
    // kiln-eml: the files whose entry above was never computed from their bytes: its third number is a digest of the
    // stamp in `stamps`, and the file counts as unchanged exactly while that stamp is the file's (prints.rs, `Record`).
    // Not part of what `diff` compares (the entries are). An older reader ignores the list and sees entries that no
    // content has, so that a changed stamp reads as a changed file there too; a cache file from before the list has
    // only entries that were read.
    #[serde(default, skip_serializing_if = "BTreeSet::is_empty")]
    unread: BTreeSet<PathBuf>,
    // kiln-eml: SHA-256 of the ownership receipt (ownership.rs `marker`) whose output the game-file fingerprints above
    // are: recorded right after a publication (the receipt lists exactly the bytes just put in place) or after a full
    // check on a start that changed nothing. While the cache is a hit and the receipt is these bytes, that output is
    // still live and is not read again. Not part of what `diff` compares.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    owner: Option<String>,
}

impl Default for FileStateCache {
    fn default() -> Self {
        Self {
            build_id: BUILD_ID.to_string(),
            files: HashMap::new(),
            stamps: HashMap::new(),
            unread: BTreeSet::new(),
            owner: None,
        }
    }
}

impl FileStateCache {

    pub fn configuration(&mut self, key: &str) {
        if !self.build_id.is_empty() { self.build_id.push_str(key); }
    }

    pub fn error() -> Self {
        Self {
            build_id: String::new(),
            files: HashMap::new(),
            stamps: HashMap::new(),
            unread: BTreeSet::new(),
            owner: None,
        }
    }

    // kiln-eml
    pub(crate) fn owner(&self) -> Option<&str> { self.owner.as_deref() }
    pub(crate) fn set_owner(&mut self, owner: Option<String>) { self.owner = owner; }

    pub fn track(
        &mut self,
        path: impl Into<PathBuf>,
        prints: &mut Fingerprints,
    ) {
        self.track_as(path, prints, Need::Content);
    }

    /// `track` with what the caller needs of the file: its content (a mod's file), or only to know it is the same
    /// file later (the game's data files: prints.rs).
    pub fn track_as(
        &mut self,
        path: impl Into<PathBuf>,
        prints: &mut Fingerprints,
        need: Need,
    ) {
        let path = path.into();
        // kiln-eml: whole-second mtimes miss rapid edits, preserved timestamps and resource/backup changes.
        // Hash bytes as well (once per file while its OS stamp shows it unchanged: prints.rs); unreadable files
        // poison this snapshot rather than producing a cache hit.
        match prints.fingerprint(&path, need) {
            Ok(fp) => {
                match fp.stamp {
                    Some(stamp) => { self.stamps.insert(path.clone(), stamp); }
                    None => { self.stamps.remove(&path); }
                }
                if fp.unread { self.unread.insert(path.clone()); } else { self.unread.remove(&path); }
                self.files.insert(path, fp.sig);
            }
            Err(_) => { self.build_id.clear(); }
        }
    }

    // kiln-eml
    /// Does `saved` lack, or hold other stamps than, this snapshot? A hit that finds so is saved again, which is how
    /// a cache file from before stamps (or one whose files were changed recently) gets them without a patch run.
    pub fn stamps_differ(&self, saved: &Self) -> bool {
        self.stamps != saved.stamps || self.unread != saved.unread
    }

}

// kiln-eml: the change cache an earlier kiln-eml (before ownership receipts) left, read once to adopt the output that
// run published (ownership.rs `adopt`). Nothing here trusts a cache that merely exists.
impl FileStateCache {
    /// Was this cache saved by an EML patch run of `stem`? Its build id ends in the run's key, `<stem>|RunOptions {..}`.
    pub(crate) fn patched(&self, stem: &str) -> bool {
        let Some((before, options)) = self.build_id.split_once("|RunOptions {") else { return false };
        before.ends_with(stem) && before.len() > stem.len() && options.contains("patch: true") && options.ends_with('}')
    }

    /// The saved length of `path` and whether that entry is a CRC of the file's bytes (not a digest of its stamp).
    pub(crate) fn saved(&self, path: &std::path::Path) -> Option<(u64, bool)> {
        self.files.get(path).map(|sig| (sig.0, !self.unread.contains(path)))
    }

    /// Does the saved entry of `path` describe the file as it is now? `content` is the file's length and CRC-64, just
    /// read by the caller. An entry that is a digest of the file's stamp (`unread`) is compared by that stamp, on the
    /// same conditions the cache itself trusts one under: the file system kept the change time through a same-size
    /// rewrite in this folder (`probe`, run once and kept in `volume`), and length and last-write time are unchanged.
    pub(crate) fn describes(&self, path: &std::path::Path, content: (u64, u64), volume: &mut Option<Result<u64, String>>) -> Result<(), String> {
        let Some(&(len, mtime, value)) = self.files.get(path) else { return Err("it is not in the change cache".into()) };
        if !self.unread.contains(path) {
            return if (len, value) == content { Ok(()) } else { Err("it is not the file the last run saved".into()) };
        }
        let Some(saved) = self.stamps.get(path) else { return Err("its change stamp was not kept".into()) };
        let file = std::fs::File::open(path).map_err(|e| e.to_string())?;
        let meta = file.metadata().map_err(|e| e.to_string())?;
        let modified = meta.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| d.as_nanos());
        if meta.len() != len || modified != Some(mtime) || stamp::read(&file).as_ref() != Some(saved) {
            return Err("it changed since the last run saved it".into());
        }
        let dir = path.parent().ok_or("no folder")?;
        let tested = volume.get_or_insert_with(|| stamp::probe(dir, stamp::read));
        match tested {
            // the test ran in this very folder, so even a volume Wine reports as 0 is the tested one (prints.rs Gate)
            Ok(v) if *v == saved.volume => Ok(()),
            Ok(_) => Err("its volume is not the one the change stamps were tested on".into()),
            Err(why) => Err(format!("change stamps are not reliable here ({why})")),
        }
    }
}

pub struct CacheDiff {
    build_id_changed: bool,
    files_changed: bool,
}

#[allow(dead_code)]
impl CacheDiff {

    pub fn new_dirty() -> Self {
        Self {
            build_id_changed: true,
            files_changed: true,
        }
    }

    pub fn build_id_changed(&self) -> bool {
        self.build_id_changed
    }

    pub fn files_changed(&self) -> bool {
        self.files_changed
    }

    pub fn is_none(&self) -> bool {
        !self.is_some()
    }

    pub fn is_some(&self) -> bool {
        self.build_id_changed || self.files_changed
    }

}

impl FileStateCache {

    pub fn diff(&self, other: &Self) -> CacheDiff {
        CacheDiff {
            build_id_changed: !self.eq_build_id(other),
            files_changed: !self.eq_files(other),
        }
    }

    fn eq_build_id(&self, other: &Self) -> bool {
        !self.build_id.is_empty() && self.build_id == other.build_id
    }

    fn eq_files(&self, other: &Self) -> bool {
        for (path, timestamp) in &other.files {
            match self.files.get(path) {
                Some(cached_timestamp) => {
                    if cached_timestamp != timestamp {
                        return false;
                    }
                },
                None => {
                    return false;
                }
            }
        }

        for path in self.files.keys() {
            if !other.files.contains_key(path) {
                return false;
            }
        }

        true
    }

}

#[cfg(test)]
mod tests;
