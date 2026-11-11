//! Tauri commands: profile. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Profiles on kiln_manage::profile; activation refuses while the game runs and emits `kiln://game {status}`.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::manifest::RunSide;
use kiln_manage::profile::{self, Activation, Profile, ProfileSummary, ResolvePreview};
use kiln_manage::quick::ProfileChange;
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

/// Saves, then writes the game's profile.json again in every game where this profile is active and the game is not
/// running, so Steam's own Play button starts the profile as it is now. A profile that does not apply (a mod missing
/// from the library) still saves; Play says why.
#[tauri::command]
pub async fn profile_save(app: AppHandle, profile: Profile) -> Result<Profile, Error> {
    let (saved, applied) = blocking(move || {
        let d = dirs()?;
        let saved = profile::save(&d, &profile)?;
        Ok((saved.clone(), reapply_where_active(&d, &saved.id)))
    })
    .await?;
    for g in applied {
        emit_status_later(app.clone(), g);
    }
    Ok(saved)
}

/// The client games whose profile.json names `profile_id` (and that are not running), re-applied when it changed.
/// Returns the ids of the games written.
pub(crate) fn reapply_where_active(d: &kiln_manage::paths::DataDirs, profile_id: &str) -> Vec<String> {
    let games = kiln_manage::game::known(d).unwrap_or_default();
    let mut applied = Vec::new();
    for g in games.iter().filter(|g| g.kind == kiln_manage::game::GameKind::Client) {
        let active_here = profile::read_game_profile(&g.path).ok().flatten().is_some_and(|f| f.id == profile_id);
        if active_here && reapply(d, g) {
            applied.push(g.id.clone());
        }
    }
    applied
}

/// `profile::refresh_active` for one game that is not running, logged to launcher.log. True when it wrote.
///
/// One at a time: a profile page's save and the watcher both come here for the same change. Side by side they both
/// found the file out of date, both activated the profile (copying its mods twice at once) and both wrote the same
/// generation (seen in a player's launcher.log as every "written again" line twice). The second one now waits, finds
/// the file current and writes nothing.
pub(crate) fn reapply(d: &kiln_manage::paths::DataDirs, g: &kiln_manage::game::GameInstall) -> bool {
    static ONE_AT_A_TIME: std::sync::Mutex<()> = std::sync::Mutex::new(());
    let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|e| e.into_inner());
    if kiln_manage::proc::find_game(g).ok().flatten().is_some() {
        return false;
    }
    match profile::refresh_active(d, g) {
        Ok(Some(a)) => {
            crate::commands::app::launcher_log("profile", &format!("{} changed: written again to {} (generation {})", a.profile_id, g.path.display(), a.generation));
            true
        }
        Ok(None) => false,
        Err(e) => {
            crate::commands::app::launcher_log("profile", &format!("the active profile of {} changed but does not apply: {}", g.path.display(), e.message));
            false
        }
    }
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
pub(crate) fn emit_status_later(app: AppHandle, game_id: String) {
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

/// Undoes a `library_add` (its toast's Undo): the changed entries go back (kiln_manage::quick::revert), then, like
/// profile_save, the game's profile.json is written again where this profile is active. The library copies stay.
#[tauri::command]
pub async fn profile_revert(app: AppHandle, profile_id: String, changes: Vec<ProfileChange>) -> Result<Profile, Error> {
    let (saved, applied) = blocking(move || {
        let d = dirs()?;
        let saved = kiln_manage::quick::revert(&d, &profile_id, &changes)?;
        Ok((saved.clone(), reapply_where_active(&d, &saved.id)))
    })
    .await?;
    for g in applied {
        emit_status_later(app.clone(), g);
    }
    Ok(saved)
}

/// The profile the simple flow uses in a game (kiln_manage::quick::ensure_default): the active one; with no profile at
/// all, a new "My mods" (every drop-in folder on), activated. Null when profiles exist but none is active here. Play
/// calls it after it installed Kiln.
#[tauri::command]
pub async fn profile_ensure_default(app: AppHandle, game_id: String) -> Result<Option<Activation>, Error> {
    let id = game_id.clone();
    let (activation, created) = blocking(move || {
        let d = dirs()?;
        let g = crate::state::game(&id)?;
        match kiln_manage::quick::ensure_default(&d, &g)? {
            None => Ok((None, false)),
            Some(t) if t.created => Ok((t.activation, true)),
            Some(_) => Ok((profile::active(&d, &g)?, false)),
        }
    })
    .await?;
    if created {
        crate::commands::app::launcher_log("profile", &format!("{} made and activated in {game_id}", kiln_manage::quick::DEFAULT_PROFILE));
        emit_status_later(app, game_id);
    }
    Ok(activation)
}
