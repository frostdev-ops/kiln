//! Tauri commands: logs. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Log files on kiln_manage::logs (names relative to the game/server folder, checked there); follows run in
//! crate::watch and arrive as `kiln://log`.

use crate::state::blocking;
use kiln_manage::{Error, ErrorCode};
use kiln_manage::logs::{self, CrashReport, LogChunk, LogFile};
use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// `{"game": id}` | `{"server": id}`
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum LogTarget {
    Game(String),
    Server(String),
}

/// The folder log names are relative to.
pub fn target_dir(target: &LogTarget) -> Result<PathBuf, Error> {
    match target {
        LogTarget::Game(id) => Ok(crate::state::game(id)?.path),
        LogTarget::Server(id) => kiln_manage::server::instance::get(id)?
            .dir
            .ok_or_else(|| Error::new(ErrorCode::Unsupported, "Remote server logs come through the admin pages.")),
    }
}

#[tauri::command]
pub async fn logs_list(target: LogTarget) -> Result<Vec<LogFile>, Error> {
    blocking(move || logs::list(&target_dir(&target)?)).await
}

#[tauri::command]
pub async fn logs_read(target: LogTarget, file: String, offset: Option<u64>, max_bytes: Option<u64>) -> Result<LogChunk, Error> {
    blocking(move || logs::read(&target_dir(&target)?, &file, offset, max_bytes)).await
}

#[tauri::command]
pub async fn logs_follow(target: LogTarget, file: String) -> Result<String, Error> {
    blocking(move || {
        let dir = target_dir(&target)?;
        // the same name check as logs_read (a zero-byte read)
        logs::read(&dir, &file, None, Some(0))?;
        let path = dir.join(&file);
        crate::watch::follow(target, file, path)
    })
    .await
}

#[tauri::command]
pub async fn logs_unfollow(subscription: String) -> Result<(), Error> {
    if crate::watch::unfollow(&subscription) { Ok(()) } else { Err(Error::not_found(format!("no follow {subscription}"))) }
}

#[tauri::command]
pub async fn logs_crash_reports(target: LogTarget) -> Result<Vec<CrashReport>, Error> {
    blocking(move || logs::crash_reports(&target_dir(&target)?)).await
}
