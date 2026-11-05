//! Tauri commands: game. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Game installs on kiln_manage::game; `game_status` reuses a cached install state (crate::state::install_state).

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::game::{self, GameInstall, GameStatus};
use std::path::PathBuf;

#[tauri::command]
pub async fn game_list() -> Result<Vec<GameInstall>, Error> {
    blocking(|| game::known(&dirs()?)).await
}

/// Scans every Steam library (plus the settings' extra folders) and records the installs not known yet.
#[tauri::command]
pub async fn game_detect() -> Result<Vec<GameInstall>, Error> {
    blocking(|| {
        let d = dirs()?;
        let found = game::detect_in(&crate::state::steam_roots(), &d.root)?;
        let known = game::known(&d)?;
        for g in found {
            if !known.iter().any(|k| k.path == g.path) {
                game::add(&d, &g.path)?;
            }
        }
        game::known(&d)
    })
    .await
}

#[tauri::command]
pub async fn game_add(path: String) -> Result<GameInstall, Error> {
    if path.trim().is_empty() {
        return Err(Error::invalid("Give a game folder."));
    }
    blocking(move || {
        let d = dirs()?;
        let p = PathBuf::from(kiln_manage::util::expand(path.trim(), &kiln_manage::util::home()));
        game::add(&d, &p)
    })
    .await
}

#[tauri::command]
pub async fn game_remove(game_id: String) -> Result<(), Error> {
    blocking(move || {
        crate::state::forget_install_state(&game_id);
        game::remove(&dirs()?, &game_id)
    })
    .await
}

#[tauri::command]
pub async fn game_status(game_id: String) -> Result<GameStatus, Error> {
    blocking(move || crate::state::game_status(&crate::state::game(&game_id)?)).await
}
