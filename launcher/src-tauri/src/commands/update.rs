//! Tauri commands: the launcher's self-update (crate::selfupdate). Names and arguments: docs/kiln/launcher/contracts.md
//! section 9.

use super::TaskId;
use crate::selfupdate::{self, UpdateStatus};
use kiln_manage::Error;
use tauri::AppHandle;

/// The update state now (also sent as `kiln://update` at every change).
#[tauri::command]
pub async fn update_status() -> Result<UpdateStatus, Error> {
    Ok(selfupdate::status())
}

/// Checks frostdev.io (then GitHub) now.
#[tauri::command]
pub async fn update_check(app: AppHandle) -> Result<UpdateStatus, Error> {
    Ok(selfupdate::check(&app, true).await)
}

/// Downloads, verifies and (AppImage) installs the update; the task's result is the status.
#[tauri::command]
pub async fn update_install(app: AppHandle) -> Result<TaskId, Error> {
    selfupdate::install(&app)
}

/// "Restart now" into the update that is ready. Returns only on failure.
#[tauri::command]
pub async fn update_restart(app: AppHandle) -> Result<(), Error> {
    selfupdate::restart(&app)
}

/// Skips `version` (null: un-skips).
#[tauri::command]
pub async fn update_skip(app: AppHandle, version: Option<String>) -> Result<UpdateStatus, Error> {
    Ok(selfupdate::skip(&app, version))
}

/// The user has seen the "updated" notice (clears `justUpdated`).
#[tauri::command]
pub async fn update_ack(app: AppHandle) -> Result<UpdateStatus, Error> {
    Ok(selfupdate::ack(&app))
}
