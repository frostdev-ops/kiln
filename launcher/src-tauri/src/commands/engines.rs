//! Tauri commands: other engines' mods (docs/kiln/engines.md "Managing every engine's mods"; contracts.md section 9).
//! Settings per format (ShroudForge's `extended.mod.json`, Shroudtopia's `shroudtopia.json`), the plugins' state files
//! for the Play and Logs pages, and EML's backup of the vanilla `.kfc` ("Restore vanilla game data"). All on
//! kiln_manage::engine_mods; the launcher only reads and writes those files.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::config::ConfigTarget;
use kiln_manage::engine_mods::{self, EmlBackup, EngineSettings, EngineStatus};
use kiln_manage::library::LibraryRef;
use kiln_manage::manifest::ModKind;
use std::path::PathBuf;

/// What a settings call needs: the game folder (optional), the profile's config folder, and the library folder of the
/// profile's entry (None when the profile does not hold the mod or the library lost it).
fn settings_ctx(kind: ModKind, id: &str, profile_id: &str, game_id: Option<&str>) -> Result<(Option<PathBuf>, PathBuf, Option<PathBuf>), Error> {
    let config_dir = super::config::config_dir(&ConfigTarget::Profile(profile_id.to_string()))?;
    let game_dir = match game_id {
        Some(g) => Some(crate::state::game(g)?.path),
        None => None,
    };
    let d = dirs()?;
    let profile = kiln_manage::profile::get(&d, profile_id)?;
    let folder = profile.mods.list(kind).iter().find(|m| m.id == id).and_then(|m| {
        kiln_manage::library::path_of(&d, &LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })
            .ok()
            .filter(|p| p.is_dir())
    });
    Ok((game_dir, config_dir, folder))
}

#[tauri::command]
pub async fn engine_settings_get(kind: ModKind, id: String, profile_id: String, game_id: Option<String>) -> Result<EngineSettings, Error> {
    blocking(move || {
        let (game_dir, config_dir, folder) = settings_ctx(kind, &id, &profile_id, game_id.as_deref())?;
        engine_mods::settings(kind, &id, game_dir.as_deref(), &config_dir, folder.as_deref())
    })
    .await
}

#[tauri::command]
pub async fn engine_settings_set(kind: ModKind, id: String, profile_id: String, game_id: Option<String>, key: String,
                                 value: serde_json::Value) -> Result<EngineSettings, Error> {
    blocking(move || {
        let (game_dir, config_dir, folder) = settings_ctx(kind, &id, &profile_id, game_id.as_deref())?;
        engine_mods::set_setting(kind, &id, &key, &value, game_dir.as_deref(), &config_dir, folder.as_deref())
    })
    .await
}

#[tauri::command]
pub async fn engine_status(game_id: String) -> Result<Vec<EngineStatus>, Error> {
    blocking(move || Ok(engine_mods::status(&crate::state::game(&game_id)?.path))).await
}

#[tauri::command]
pub async fn eml_backup(game_id: String) -> Result<Vec<EmlBackup>, Error> {
    blocking(move || Ok(engine_mods::eml_backup(&crate::state::game(&game_id)?.path))).await
}

/// Copies EML's `<exe>.kfc.bak` back over each patched `.kfc`; refused while the game runs.
#[tauri::command]
pub async fn eml_restore(game_id: String) -> Result<Vec<String>, Error> {
    blocking(move || {
        let g = crate::state::game(&game_id)?;
        crate::state::refuse_running(&g)?;
        engine_mods::eml_restore_vanilla(&g.path)
    })
    .await
}
