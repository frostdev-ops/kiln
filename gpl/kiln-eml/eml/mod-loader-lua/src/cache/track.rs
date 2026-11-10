// kiln-eml: include resources/backups/config and make unreadable inputs invalidate the snapshot.
use std::path::Path;

use walkdir::WalkDir;

use crate::{cache::{FileStateCache, Fingerprints}, log::warn};

impl FileStateCache {

    /// Inputs which publication must never change. Compare these around Lua execution and publication as well
    /// as recording the final output files: otherwise a changed input can be cached alongside an older patch.
    pub fn track_game_inputs(&mut self, game_dir: &Path, file_name: &str, vanilla_containers: usize, prints: &mut Fingerprints) {
        self.track(game_dir.join(format!("{file_name}.kfc.bak")), prints);
        for suffix in ["exe", "kfc_resources.bak", "kfc.bak.integrity"] {
            let path = game_dir.join(format!("{file_name}.{suffix}"));
            if !matches!(path.try_exists(), Ok(false)) { self.track(path, prints); }
        }
        let config = game_dir.join("eml.json");
        if !matches!(config.try_exists(), Ok(false)) { self.track(config, prints); }
        for index in 0..vanilla_containers { self.track(game_dir.join(format!("{file_name}_{index:03}.dat")), prints); }
    }

    pub fn only_game_inputs(mut self, game_dir: &Path, file_name: &str, vanilla_containers: usize) -> Self {
        let mut paths: std::collections::HashSet<_> = ["exe", "kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"]
            .map(|suffix| game_dir.join(format!("{file_name}.{suffix}"))).into_iter().collect();
        paths.insert(game_dir.join("eml.json"));
        paths.extend((0..vanilla_containers).map(|index| game_dir.join(format!("{file_name}_{index:03}.dat"))));
        self.files.retain(|p, _| paths.contains(p));
        self.stamps.retain(|p, _| paths.contains(p));
        if !self.build_id.is_empty() { self.build_id = super::BUILD_ID.into(); }
        self
    }

    pub fn track_game_files(
        &mut self,
        game_dir: impl AsRef<Path>,
        prints: &mut Fingerprints,
    ) {
        let game_dir = game_dir.as_ref();

        let game_files = match std::fs::read_dir(game_dir) {
            Ok(entries) => entries,
            Err(e) => {
                warn!(
                    error = %e,
                    path = game_dir.display().to_string(),
                    "Failed to read game directory"
                );
                self.build_id.clear();
                return;
            }
        };

        // kiln-eml: list the folder first. The volume test (prints.rs) creates and removes a file in it, which must not
        // disturb a listing in progress.
        for entry in game_files.collect::<Vec<_>>() {
            let entry = match entry {
                Ok(e) => e,
                Err(e) => {
                    warn!(
                        error = %e,
                        path = game_dir.display().to_string(),
                        "Failed to read game directory entry, skipping"
                    );
                    self.build_id.clear();
                    continue;
                }
            };

            let path = entry.path();
            let extension = path.extension().and_then(|ext| ext.to_str());

            if let Some(ext) = extension {
                if matches!(ext, "dat" | "kfc" | "kfc_resources" | "exe" | "bak" | "integrity") || path.file_name().is_some_and(|n| n == "eml.json") {
                    self.track(path, prints);
                }
            }
        }
    }

    pub fn track_mod_files(
        &mut self,
        mods_dir: impl AsRef<Path>,
        prints: &mut Fingerprints,
    ) {
        self.track_directory(mods_dir, prints);
    }

    pub fn track_directory(
        &mut self,
        dir: impl AsRef<Path>,
        prints: &mut Fingerprints,
    ) {
        let mod_files = WalkDir::new(dir)
            .follow_links(false)
            .follow_root_links(false)
            .into_iter();

        for mod_files in mod_files {
            let entry = match mod_files {
                Ok(e) => e,
                Err(e) => {
                    let path = e.path();

                    if let Some(ancestor) = e.loop_ancestor() {
                        warn!(
                            path = path.map_or_else(
                                || "unknown".into(),
                                |p| p.display().to_string()
                            ),
                            ancestor = ancestor.display().to_string(),
                            error = %e,
                            "Skipping directory loop"
                        );
                    } else {
                        warn!(
                            path = path.map_or_else(
                                || "unknown".into(),
                                |p| p.display().to_string()
                            ),
                            error = %e,
                            "Failed to read directory entry, skipping"
                        );
                    }

                    self.build_id.clear();
                    continue;
                }
            };

            let path = entry.into_path();

            self.track(&path, prints);
        }
    }

}
