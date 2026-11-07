use std::sync::Arc;

use crate::{alias::{Path, PathBuf}, ModEnvironmentErrorReport, ModRegistry};

struct ModEnvironmentInner {
    game_dir: PathBuf,
    cache_dir: PathBuf,
    mods_dir: PathBuf,
    // kiln-eml: the mod folders/zips this environment was built from (load_from); empty for load().
    mod_paths: Vec<PathBuf>,

    registry: ModRegistry,
}

#[derive(Clone)]
pub struct ModEnvironment {
    inner: Arc<ModEnvironmentInner>,
}

impl ModEnvironment {

    pub fn load(
        game_dir: impl AsRef<Path>,
    ) -> Result<Self, ModEnvironmentErrorReport> {
        let game_dir = game_dir.as_ref().to_path_buf();
        let cache_dir = game_dir.join(".cache");
        let mods_dir = game_dir.join("mods");

        let registry = ModRegistry::load(&mods_dir)?;

        Ok(Self {
            inner: Arc::new(ModEnvironmentInner {
                game_dir,
                cache_dir,
                mods_dir,
                mod_paths: Vec::new(),
                registry,
            }),
        })
    }

    /// kiln-eml: an environment from an explicit list of mod folders or zips (Kiln profiles, `<game>/mods`,
    /// `<game>/kiln/eml/mods`) and a cache directory of its own. Same errors as `load`; a mod that fails is reported
    /// and the others stay in the report's registry.
    pub fn load_from(
        game_dir: impl AsRef<Path>,
        cache_dir: impl AsRef<Path>,
        mod_paths: Vec<PathBuf>,
    ) -> Result<Self, ModEnvironmentErrorReport> {
        let game_dir = game_dir.as_ref().to_path_buf();
        let registry = ModRegistry::load_paths(&mod_paths)?;

        Ok(Self::from_parts(game_dir, cache_dir.as_ref().to_path_buf(), mod_paths, registry))
    }

    /// kiln-eml: builds an environment from a registry (used with the registry of a partial error report).
    pub fn from_parts(
        game_dir: PathBuf,
        cache_dir: PathBuf,
        mod_paths: Vec<PathBuf>,
        registry: ModRegistry,
    ) -> Self {
        let mods_dir = game_dir.join("mods");

        Self {
            inner: Arc::new(ModEnvironmentInner {
                game_dir,
                cache_dir,
                mods_dir,
                mod_paths,
                registry,
            }),
        }
    }

    /// kiln-eml: the mod folders/zips given to `load_from` (empty when built with `load`).
    pub fn mod_paths(&self) -> &[PathBuf] {
        &self.inner.mod_paths
    }

    pub fn game_dir(&self) -> &Path {
        &self.inner.game_dir
    }

    pub fn cache_dir(&self) -> &Path {
        &self.inner.cache_dir
    }

    pub fn mods_dir(&self) -> &Path {
        &self.inner.mods_dir
    }

    pub fn mod_registry(&self) -> &ModRegistry {
        &self.inner.registry
    }

}
