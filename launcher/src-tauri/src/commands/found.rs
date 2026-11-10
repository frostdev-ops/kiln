//! Tauri commands: mods in the game folder that Kiln did not put there (kiln_manage::found; docs/kiln/profiles.md
//! "Mods in the game folder"). The profile pages' "In the game folder" section and Play's line read `found_scan`;
//! "Add to this profile" is `found_add`, a task like `library_add`. Switching a kind's drop-in folder on is a plain
//! profile save.

use super::TaskId;
use super::app::{spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::found::{self, FoundReport};
use tauri::AppHandle;

/// Every mod in the game's engine folders that Kiln did not put there, and whether it loads with `profile_id` (the
/// profile the page shows, active in the game or not). Cheap: no hashing, bounded listings; never fails over a missing
/// or unreadable folder.
#[tauri::command]
pub async fn found_scan(game_id: String, profile_id: String) -> Result<FoundReport, Error> {
    blocking(move || found::scan_ids(&dirs()?, &crate::state::game(&game_id)?, &profile_id)).await
}

/// Imports the found mod at `path` (a `FoundMod.path`) into the Library and adds it to the profile, as one task (result:
/// AddReport). When the profile is active in the game, the game's profile.json is written again too.
#[tauri::command]
pub async fn found_add(app: AppHandle, game_id: String, profile_id: String, path: String) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let title = format!("Add {}", path.rsplit('/').next().unwrap_or(&path));
    let a = app.clone();
    Ok(spawn_task(&app, "found_add", title, move |p| {
        let r = found::add(&dirs()?, &g, &profile_id, &path, p)?;
        if r.applied {
            super::profile::emit_status_later(a, game_id);
        }
        to_value(r)
    }))
}
