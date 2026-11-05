//! Tauri commands: editor. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! The vendored Save Editor's commands (launcher/src/editor/src/tauri-api.js maps its invoke names to these), on
//! kiln_manage::saves::editor (Linux-aware: Steam userdata under every root, Proton prefixes). Binary results go back
//! as raw IPC responses (ArrayBuffer in JS).

use crate::state::blocking;
use kiln_manage::Error;
use kiln_manage::saves::editor::{self, EditorBackup, EditorFileInfo, EditorSaveFile};
use tauri::ipc::Response;

#[tauri::command]
pub async fn editor_list_saves() -> Result<Vec<EditorSaveFile>, Error> {
    blocking(editor::list_saves).await
}

#[tauri::command]
pub async fn editor_file_info(path: String) -> Result<EditorFileInfo, Error> {
    blocking(move || editor::file_info(&path)).await
}

#[tauri::command]
pub async fn editor_read_file(path: String) -> Result<Response, Error> {
    blocking(move || editor::read_file(&path).map(Response::new)).await
}

#[tauri::command]
pub async fn editor_zstd_decompress(data: Vec<u8>) -> Result<Response, Error> {
    blocking(move || editor::zstd_decompress(&data).map(Response::new)).await
}

#[tauri::command]
pub async fn editor_zstd_compress(data: Vec<u8>) -> Result<Response, Error> {
    blocking(move || editor::zstd_compress(&data).map(Response::new)).await
}

#[tauri::command]
pub async fn editor_game_running() -> Result<bool, Error> {
    blocking(editor::game_running).await
}

#[tauri::command]
pub async fn editor_backup_file(path: String) -> Result<String, Error> {
    blocking(move || editor::backup_file(&path)).await
}

#[tauri::command]
pub async fn editor_write_save(path: String, data: Vec<u8>) -> Result<(), Error> {
    blocking(move || editor::write_save(&path, &data)).await
}

#[tauri::command]
pub async fn editor_list_backups(target: String) -> Result<Vec<EditorBackup>, Error> {
    blocking(move || editor::list_backups(&target)).await
}

#[tauri::command]
pub async fn editor_restore_backup(src: String, target: String) -> Result<(), Error> {
    blocking(move || editor::restore_backup(&src, &target)).await
}

#[tauri::command]
pub async fn editor_open_backups() -> Result<(), Error> {
    blocking(|| {
        let dir = editor::backups_dir()?;
        std::fs::create_dir_all(&dir)?;
        crate::state::open_external(dir.as_os_str())
    })
    .await
}
