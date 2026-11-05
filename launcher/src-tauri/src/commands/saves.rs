//! Tauri commands: saves. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Save slots and backups on kiln_manage::saves; restore refuses while the game runs.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::saves::{self, BackupReason, SaveBackup, SaveSlot};

#[tauri::command]
pub async fn saves_list(game_id: String) -> Result<Vec<SaveSlot>, Error> {
    blocking(move || saves::list(&crate::state::game(&game_id)?)).await
}

#[tauri::command]
pub async fn saves_backups(game_id: String) -> Result<Vec<SaveBackup>, Error> {
    blocking(move || saves::backups(&dirs()?, &crate::state::game(&game_id)?)).await
}

#[tauri::command]
pub async fn saves_backup(game_id: String, note: Option<String>) -> Result<SaveBackup, Error> {
    blocking(move || {
        let note = note.as_deref().map(str::trim).filter(|n| !n.is_empty());
        saves::backup(&dirs()?, &crate::state::game(&game_id)?, BackupReason::Manual, note)
    })
    .await
}

#[tauri::command]
pub async fn saves_restore(game_id: String, backup_id: String) -> Result<(), Error> {
    blocking(move || {
        let g = crate::state::game(&game_id)?;
        crate::state::refuse_running(&g)?;
        saves::restore(&dirs()?, &g, &backup_id)
    })
    .await
}

#[tauri::command]
pub async fn saves_delete(game_id: String, backup_id: String) -> Result<(), Error> {
    blocking(move || saves::delete(&dirs()?, &crate::state::game(&game_id)?, &backup_id)).await
}
