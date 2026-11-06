//! Tauri commands: profile. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Profiles on kiln_manage::profile; activation refuses while the game runs and emits `kiln://game {status}`.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::manifest::RunSide;
use kiln_manage::profile::{self, Activation, Profile, ProfileSummary, ResolvePreview};
use tauri::AppHandle;

#[tauri::command]
pub async fn profile_list() -> Result<Vec<ProfileSummary>, Error> {
    blocking(|| profile::list(&dirs()?)).await
}

#[tauri::command]
pub async fn profile_get(profile_id: String) -> Result<Profile, Error> {
    blocking(move || profile::get(&dirs()?, &profile_id)).await
}

#[tauri::command]
pub async fn profile_create(name: String, copy_from: Option<String>) -> Result<Profile, Error> {
    if name.trim().is_empty() {
        return Err(Error::invalid("Give the profile a name."));
    }
    blocking(move || profile::create(&dirs()?, name.trim(), copy_from.as_deref())).await
}

#[tauri::command]
pub async fn profile_save(profile: Profile) -> Result<Profile, Error> {
    blocking(move || profile::save(&dirs()?, &profile)).await
}

#[tauri::command]
pub async fn profile_delete(profile_id: String) -> Result<(), Error> {
    blocking(move || profile::delete(&dirs()?, &profile_id)).await
}

#[tauri::command]
pub async fn profile_resolve(profile_id: String, side: RunSide) -> Result<ResolvePreview, Error> {
    blocking(move || {
        let d = dirs()?;
        let p = profile::get(&d, &profile_id)?;
        profile::resolve_preview(&d, &p, side)
    })
    .await
}

/// The game's fresh status, from its own thread: the activation answers as soon as profile.json is written (the
/// status hashes files and lists processes, which used to hold the answer, and Play's header with it, for seconds).
fn emit_status_later(app: AppHandle, game_id: String) {
    std::thread::spawn(move || crate::watch::emit_status(&app, &game_id));
}

#[tauri::command]
pub async fn profile_activate(app: AppHandle, profile_id: String, game_id: String) -> Result<Activation, Error> {
    let id = game_id.clone();
    let r = blocking(move || {
        let g = crate::state::game(&id)?;
        crate::state::refuse_running(&g)?;
        profile::activate(&dirs()?, &profile_id, &g)
    })
    .await?;
    emit_status_later(app, game_id);
    Ok(r)
}

#[tauri::command]
pub async fn profile_deactivate(app: AppHandle, game_id: String) -> Result<(), Error> {
    let id = game_id.clone();
    blocking(move || {
        let g = crate::state::game(&id)?;
        crate::state::refuse_running(&g)?;
        profile::deactivate(&dirs()?, &g)
    })
    .await?;
    emit_status_later(app, game_id);
    Ok(())
}

#[tauri::command]
pub async fn profile_active(game_id: String) -> Result<Option<Activation>, Error> {
    blocking(move || profile::active(&dirs()?, &crate::state::game(&game_id)?)).await
}
