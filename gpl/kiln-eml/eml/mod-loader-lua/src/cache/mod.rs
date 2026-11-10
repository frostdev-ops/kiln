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
}

impl Default for FileStateCache {
    fn default() -> Self {
        Self {
            build_id: BUILD_ID.to_string(),
            files: HashMap::new(),
            stamps: HashMap::new(),
            unread: BTreeSet::new(),
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
        }
    }

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
