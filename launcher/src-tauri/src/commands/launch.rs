//! Tauri commands: launch. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! The launch flow is kiln_manage::launch::play (play-once/once files, save backup, steam:// URI); the watcher
//! (crate::watch) reports started/boot/mods/native/stopped as `kiln://game`.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::launch::{LaunchRequest, LaunchResult};
use serde::{Deserialize, Serialize};
use tauri::AppHandle;

/// `launch_state`: the game's state files, read together (launcher-only type).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootState {
    pub boot: Option<kiln_manage::state::BootJson>,
    pub mods: Option<kiln_manage::state::ModsJson>,
    pub native: Option<kiln_manage::state::NativeJson>,
    /// boot.json is not from the game process that runs now (`launch::boot_is_current`).
    pub stale: bool,
}

/// `KILN_LAUNCH_DRY_RUN=1`: launch_play does everything except opening the steam:// URI.
pub fn dry_run() -> bool {
    std::env::var("KILN_LAUNCH_DRY_RUN").is_ok_and(|v| v == "1")
}

#[tauri::command]
pub async fn launch_play(app: AppHandle, game_id: String, request: LaunchRequest) -> Result<LaunchResult, Error> {
    let id = game_id.clone();
    let r = blocking(move || {
        let g = crate::state::game(&id)?;
        crate::state::refuse_running(&g)?;
        if dry_run() {
            // tests against game copies: write the play-once files, back up, but never open steam:// (that would
            // start the real game through Steam)
            return kiln_manage::launch::play_with(&dirs()?, &g, &request, None, &|uri| {
                eprintln!("KILN_LAUNCH_DRY_RUN: not opening {uri}");
                Ok(())
            });
        }
        kiln_manage::launch::play(&dirs()?, &g, &request)
    })
    .await?;
    crate::watch::emit_status(&app, &game_id);
    Ok(r)
}

#[tauri::command]
pub async fn launch_state(game_id: String) -> Result<BootState, Error> {
    blocking(move || {
        let g = crate::state::game(&game_id)?;
        let boot = kiln_manage::state::read_boot(&g.path)?;
        let mods = kiln_manage::state::read_mods(&g.path)?;
        let native = kiln_manage::state::read_native(&g.path)?;
        // under Proton the pid in boot.json is the game's Windows pid: boot_is_current compares write and start times
        let stale = boot.as_ref().is_some_and(|b| !kiln_manage::launch::boot_is_current(&g, b));
        Ok(BootState { boot, mods, native, stale })
    })
    .await
}
