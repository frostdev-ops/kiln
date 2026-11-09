use std::{collections::HashMap, fs::DirEntry, io::BufReader, ops::Deref, sync::Arc};

mod manifest;
mod fs;

use parking_lot::{Mutex, MutexGuard};
use crate::{alias::{Path, PathBuf}, log::{info, warn}, IoError, ModEnvironmentErrorReport, ModError, ModErrorReport};

pub use manifest::*;
pub use fs::{FileSystem, ReadFailure}; // kiln-eml: ReadFailure

#[derive(Debug)]
struct ModInner {
    info: ModManifest,
    fs: Mutex<FileSystem>,
    // kiln-eml: when mod.json was read (the registry load), for "mod loaded N s ago" in read failures.
    loaded: std::time::Instant,
}

#[derive(Debug, Clone)]
pub struct Mod {
    inner: Arc<ModInner>,
}

impl Mod {

    pub fn info(&self) -> &ModManifest {
        &self.inner.info
    }

    pub fn fs(&self) -> MutexGuard<FileSystem> {
        self.inner.fs.lock()
    }

    /// kiln-eml: the time since this mod was loaded into the registry (when its mod.json was read).
    pub fn age(&self) -> std::time::Duration {
        self.inner.loaded.elapsed()
    }

}

#[derive(Debug, Default)]
pub struct ModRegistry {
    mods: HashMap<String, Mod>,
}

impl ModRegistry {

    pub(crate) fn load(
        mods_dir: impl AsRef<Path>,
    ) -> Result<Self, ModEnvironmentErrorReport> {
        let mods_dir = mods_dir.as_ref().to_path_buf();
        let mut mods = HashMap::new();

        if let Err(e) = std::fs::create_dir_all(&mods_dir) {
            return Err(ModEnvironmentErrorReport::base_io(IoError {
                path: mods_dir.into_string(),
                source: e,
            }));
        }

        let mod_files = match std::fs::read_dir(&mods_dir) {
            Ok(entries) => entries,
            Err(e) => {
                return Err(ModEnvironmentErrorReport::base_io(IoError {
                    path: mods_dir.into_string(),
                    source: e,
                }));
            }
        };

        let mut errors = Vec::new();

        for entry in mod_files {
            let entry = match entry {
                Ok(e) => e,
                Err(e) => {
                    warn!(
                        error = %e,
                        path = mods_dir.as_str(),
                        "Error reading entry in mods directory, skipping",
                    );

                    continue;
                }
            };

            let r#mod = match Self::load_mod(&mods, entry) {
                Ok(Some(m)) => m,
                Ok(None) => continue,
                Err(e) => {
                    errors.push(e);
                    continue;
                }
            };

            let mod_id = r#mod.info().id.clone();

            mods.insert(mod_id, r#mod);
        }

        let registry = Self {
            mods,
        };

        if !errors.is_empty() {
            Err(ModEnvironmentErrorReport::with_mod_errors(
                errors,
                registry,
            ))
        } else {
            Ok(registry)
        }
    }

    /// kiln-eml: a registry from explicit mod folders or zips, in order (the first of two mods with one id wins; the
    /// second is a DuplicateModId error). Hidden entries and other files are skipped, as in `load`.
    pub fn load_paths(
        paths: &[PathBuf],
    ) -> Result<Self, ModEnvironmentErrorReport> {
        let mut mods = HashMap::new();
        let mut errors = Vec::new();

        for path in paths {
            let file_type = match std::fs::metadata(path) {
                Ok(m) => m.file_type(),
                Err(e) => {
                    errors.push(ModErrorReport::new(
                        path.clone(),
                        ModError::Io(IoError { path: path.to_string(), source: e }),
                    ));
                    continue;
                }
            };

            match Self::load_mod_at(&mods, path.clone(), file_type) {
                Ok(Some(m)) => {
                    let mod_id = m.info().id.clone();
                    mods.insert(mod_id, m);
                }
                Ok(None) => {}
                Err(e) => errors.push(e),
            }
        }

        let registry = Self { mods };

        if !errors.is_empty() {
            Err(ModEnvironmentErrorReport::with_mod_errors(errors, registry))
        } else {
            Ok(registry)
        }
    }

    fn load_mod(
        mods: &HashMap<String, Mod>,
        entry: DirEntry,
    ) -> Result<Option<Mod>, ModErrorReport> {
        let path = PathBuf::from_path_buf(entry.path())
            .map_err(|e| ModErrorReport::new(
                entry.path().to_string_lossy().to_string(),
                ModError::Utf8(e)
            ))?;

        let file_type = entry.file_type()
            .map_err(|e| ModErrorReport::new(
                path.clone(),
                ModError::Io(IoError {
                    path: path.to_string(),
                    source: e,
                })
            ))?;
        Self::load_mod_at(mods, path, file_type)
    }

    // kiln-eml: the body of load_mod, taking a path instead of a DirEntry (shared by load and load_paths).
    fn load_mod_at(
        mods: &HashMap<String, Mod>,
        path: PathBuf,
        file_type: std::fs::FileType,
    ) -> Result<Option<Mod>, ModErrorReport> {
        let file_name = path.file_name().unwrap_or_default();

        if file_name.starts_with('.') {
            info!(
                path = path.as_str(),
                "Skipping hidden file or directory",
            );

            return Ok(None);
        }

        let mut fs = if file_type.is_dir() {
            FileSystem::new_disk(&path)
                .map_err(|e| ModErrorReport::new(
                    path.clone(),
                    ModError::Io(IoError {
                        path: path.to_string(),
                        source: e,
                    })
                ))?
        } else if file_type.is_file() {
            let extension = path.extension().unwrap_or_default();

            match extension {
                "zip" => {},
                _ => return Ok(None),
            }

            FileSystem::new_zip(&path)
                .map_err(|e| ModErrorReport::new(
                    path.clone(),
                    ModError::Io(IoError {
                        path: path.to_string(),
                        source: e,
                    })
                ))?
        } else {
            warn!(
                path = path.as_str(),
                "Skipping non-file and non-directory entry"
            );

            return Ok(None);
        };

        let manifest_reader = fs.read_file("mod.json")
            .map(BufReader::new)
            .map_err(|e| ModErrorReport::new(
                path.clone(),
                ModError::Io(IoError {
                    path: path.join("mod.json").to_string(),
                    source: e,
                })
            ))?;

        let mod_info = serde_json::from_reader::<_, ModManifest>(manifest_reader)
            .map_err(|e| ModErrorReport::new(
                path.clone(),
                ModError::Json {
                    path: path.join("mod.json").to_string(),
                    source: e,
                }
            ))?;

        let mod_id = mod_info.id.clone();

        if mods.contains_key(&mod_id) {
            return Err(ModErrorReport::new(
                path.clone(),
                ModError::DuplicateModId(mod_id.clone())
            ).with_id(mod_id));
        }

        Ok(Some(Mod {
            inner: Arc::new(ModInner {
                info: mod_info,
                fs: Mutex::new(fs),
                loaded: std::time::Instant::now(),
            }),
        }))
    }

}

impl Deref for ModRegistry {
    type Target = HashMap<String, Mod>;

    fn deref(&self) -> &Self::Target {
        &self.mods
    }
}
