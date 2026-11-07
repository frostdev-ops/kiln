use std::{collections::HashMap, path::PathBuf};

use serde::{Deserialize, Serialize};

mod io;
mod track;

const BUILD_ID: &str = env!("BUILD_ID");

#[derive(Debug, Serialize, Deserialize)]
pub struct FileStateCache {
    build_id: String,
    files: HashMap<PathBuf, (u64, u128, u64)>,
}

impl Default for FileStateCache {
    fn default() -> Self {
        Self {
            build_id: BUILD_ID.to_string(),
            files: HashMap::new(),
        }
    }
}

impl FileStateCache {

    pub fn configuration(&mut self, key: &str) { self.build_id.push_str(key); }

    pub fn error() -> Self {
        Self {
            build_id: String::new(),
            files: HashMap::new(),
        }
    }

    pub fn track(
        &mut self,
        path: impl Into<PathBuf>,
    ) {
        let path = path.into();
        // kiln-eml: whole-second mtimes miss rapid edits, preserved timestamps and resource/backup changes.
        // Hash bytes as well; unreadable files poison this snapshot rather than producing a cache hit.
        use std::io::Read;
        let fingerprint = (|| -> std::io::Result<_> {
            let m = path.symlink_metadata()?;
            if m.file_type().is_symlink() { return Err(std::io::Error::other("symlink")); }
            let nanos = m.modified()?.duration_since(std::time::UNIX_EPOCH).map_err(std::io::Error::other)?.as_nanos();
            let crc = crc::Crc::<u64>::new(&crc::CRC_64_ECMA_182);
            let mut digest = crc.digest();
            if m.is_file() {
                let mut f = std::fs::File::open(&path)?;
                let mut buf = [0u8; 65536];
                loop { let n = f.read(&mut buf)?; if n == 0 { break; } digest.update(&buf[..n]); }
            }
            Ok((m.len(), nanos, digest.finalize()))
        })();
        let timestamp = match fingerprint {
            Ok(v) => v,
            Err(_) => { self.build_id.clear(); return; }
        };
        self.files.insert(path, timestamp);
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
mod tests {
    use super::*;
    #[test]
    fn preserved_timestamp_edit_and_unreadable_file_invalidate_cache() {
        let p = std::env::temp_dir().join(format!("kiln-cache-{}", uuid::Uuid::new_v4()));
        std::fs::write(&p, "before").unwrap();
        let time = p.metadata().unwrap().modified().unwrap();
        let mut a = FileStateCache::default(); a.track(&p);
        std::fs::write(&p, "after!").unwrap();
        std::fs::File::options().write(true).open(&p).unwrap().set_modified(time).unwrap();
        let mut b = FileStateCache::default(); b.track(&p);
        assert!(a.diff(&b).is_some());
        std::fs::remove_file(&p).unwrap();
        let mut c = FileStateCache::default(); c.track(&p);
        assert!(c.diff(&c).is_some());
    }
}
