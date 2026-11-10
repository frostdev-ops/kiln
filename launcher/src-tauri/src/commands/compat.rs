//! Tauri commands: closed DLL mods on a profile's native page (dev.21). The chain entries with their late switch, the
//! compat rules and the last session's overlaps (kiln_manage::compat::view); the late switch of one entry.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::compat::{self, ChainView};
use kiln_manage::profile;
use tauri::AppHandle;

#[tauri::command]
pub async fn chain_view(profile_id: String, game_id: String) -> Result<ChainView, Error> {
    blocking(move || {
        let d = dirs()?;
        let g = crate::state::game(&game_id)?;
        compat::view(&d, &profile::get(&d, &profile_id)?, &g)
    })
    .await
}

/// Loads one chain entry after Kiln and ShroudForge (or not). A [profile] entry changes the profile (saved, and
/// written to the game where it is active and the game is not running); a [chain] entry changes the game's
/// chain.ini [late] for every profile. Answers the fresh view.
#[tauri::command]
pub async fn chain_set_late(app: AppHandle, profile_id: String, game_id: String, path: String, late: bool) -> Result<ChainView, Error> {
    let (view, applied) = blocking(move || {
        let d = dirs()?;
        let g = crate::state::game(&game_id)?;
        let mut p = profile::get(&d, &profile_id)?;
        // only the entry: no full view (hooks.json, Library) before the switch, one after it
        let entry = compat::find_entry(&d, &p, &g, &path)?;
        let mut applied = Vec::new();
        if compat::set_entry_late(&g, &mut p, &entry, late)? {
            let saved = profile::save(&d, &p)?;
            applied = crate::commands::profile::reapply_where_active(&d, &saved.id);
            p = saved;
        }
        crate::commands::app::launcher_log("chain", &format!("{} loads {} ({})", entry.path, if late { "late" } else { "in the proxy" }, g.path.display()));
        Ok((compat::view(&d, &p, &g)?, applied))
    })
    .await?;
    for g in applied {
        crate::commands::profile::emit_status_later(app.clone(), g);
    }
    Ok(view)
}
