// kiln-eml: include resources/backups/config and make unreadable inputs invalidate the snapshot.
use std::path::Path;

use walkdir::WalkDir;

use crate::{cache::FileStateCache, log::warn};

impl FileStateCache {

    pub fn track_game_files(
        &mut self,
        game_dir: impl AsRef<Path>,
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

        for entry in game_files {
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
                if matches!(ext, "dat" | "kfc" | "kfc_resources" | "exe" | "bak") || path.file_name().is_some_and(|n| n == "eml.json") {
                    self.track(path);
                }
            }
        }
    }

    pub fn track_mod_files(
        &mut self,
        mods_dir: impl AsRef<Path>,
    ) {
        self.track_directory(mods_dir);
    }

    pub fn track_directory(
        &mut self,
        dir: impl AsRef<Path>,
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

            self.track(&path);
        }
    }

}
