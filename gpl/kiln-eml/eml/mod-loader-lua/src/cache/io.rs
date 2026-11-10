use std::io::BufReader;

use crate::{alias::Path, cache::FileStateCache, log::warn};

impl FileStateCache {

    pub fn read(
        cache_dir: impl AsRef<Path>
    ) -> Self {
        let cache_file = cache_dir.as_ref().join("files.json");

        if cache_file.exists() {
            match std::fs::File::open(&cache_file) {
                Ok(file) => {
                    let reader = BufReader::new(file);

                    match serde_json::from_reader::<_, Self>(reader) {
                        Ok(cache) => cache,
                        Err(e) => {
                            warn!(
                                error = %e,
                                path = cache_file.as_str(),
                                "Failed to parse cache file, skipping cache loading"
                            );
                            Self::error()
                        }
                    }
                }
                Err(e) => {
                    warn!(
                        error = %e,
                        path = cache_file.as_str(),
                        "Failed to open cache file, skipping cache loading"
                    );

                    Self::error()
                }
            }
        } else {
            Self::error()
        }
    }

    /// A cache-shaped file that may not be there (the journal of an interrupted run): None when it is missing or does not
    /// parse. Says nothing about either at warning level: it is a best-effort help.
    pub fn read_file(path: &std::path::Path) -> Option<Self> {
        let file = std::fs::File::open(path).ok()?;
        serde_json::from_reader::<_, Self>(BufReader::new(file)).ok()
    }

    /// Replaces `path` whole (`atomic_write`); a failure only costs the help the file gives.
    pub fn write_file(&self, path: &std::path::Path) {
        if let Ok(bytes) = serde_json::to_vec(self) {
            let _ = crate::transaction::atomic_write(path, &bytes);
        }
    }

    pub fn write(
        &self,
        cache_dir: impl AsRef<Path>
    ) {
        let cache_dir = cache_dir.as_ref();

        if let Err(e) = std::fs::create_dir_all(cache_dir) {
            warn!(
                error = %e,
                path = cache_dir.as_str(),
                "Failed to create cache directory"
            );
        } else {
            let cache_file = cache_dir.join("files.json");

            // kiln-eml: replace the file whole. A hit that only adds stamps rewrites a good cache; a crash in the
            // middle of that must not leave a torn file that costs the next start a full patch run.
            match serde_json::to_vec(self) {
                Ok(bytes) => {
                    if let Err(e) = crate::transaction::atomic_write(cache_file.as_std_path(), &bytes) {
                        warn!(
                            error = %e,
                            path = cache_file.as_str(),
                            "Failed to write cache file"
                        );
                    }
                }
                Err(e) => {
                    warn!(
                        error = %e,
                        path = cache_file.as_str(),
                        "Failed to serialize cache file"
                    );
                }
            }
        }
    }

}

