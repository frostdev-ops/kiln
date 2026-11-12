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
    announce_applied(&app, applied);
    Ok(saved)
}

/// The client games whose profile.json names `profile_id` (and that are not running), re-applied when it changed.
/// Returns the activations written.
pub(crate) fn reapply_where_active(d: &kiln_manage::paths::DataDirs, profile_id: &str) -> Vec<Activation> {
    let games = kiln_manage::game::known(d).unwrap_or_default();
    let mut applied = Vec::new();
    for g in games.iter().filter(|g| g.kind == kiln_manage::game::GameKind::Client) {
        let active_here = profile::read_game_profile(&g.path).ok().flatten().is_some_and(|f| f.id == profile_id);
        if active_here && let Some(a) = reapply(d, g) {
            applied.push(a);
        }
    }
    applied
}

/// A profile written again without profile_activate: its notices wait for the launcher ([`queue_notices`]), then the
/// game's fresh status goes out.
pub(crate) fn announce_applied(app: &AppHandle, applied: Vec<Activation>) {
    for a in applied {
        queue_notices(app, &a);
        emit_status_later(app.clone(), a.game_id);
    }
}

/// Activations whose notices the window has not taken yet ([`activation_notices`]). The watcher's first pass runs at
/// start-up, before the window listens, and Tauri does not keep an event nobody listens to: the window takes what
/// waits once its listener is up, and at every `kiln://activation`. Taking empties the list, so nothing shows twice.
static NOTICES: std::sync::Mutex<Vec<Activation>> = std::sync::Mutex::new(Vec::new());

/// Keeps `a` for the window when it has something to show (kept copies, kept files, notes) and says so with
/// `kiln://activation {gameId}`. Undelivered recovery paths stay until a window takes them.
pub(crate) fn queue_notices(app: &AppHandle, a: &Activation) {
    use tauri::Emitter;
    if push_notice(a) {
        let _ = app.emit(crate::events::ACTIVATION, serde_json::json!({ "gameId": a.game_id }));
    }
}

/// [`queue_notices`] without the event: true when `a` was kept.
fn push_notice(a: &Activation) -> bool {
    if a.retained.is_empty() && a.kept_files.is_empty() && a.warnings.is_empty() {
        return false;
    }
    let mut q = NOTICES.lock().unwrap_or_else(|e| e.into_inner());
    q.push(a.clone());
    true
}

/// Takes the activations waiting for the window ([`queue_notices`]), oldest first.
pub(crate) fn take_notices() -> Vec<Activation> {
    std::mem::take(&mut *NOTICES.lock().unwrap_or_else(|e| e.into_inner()))
}

/// `activation_notices`: the re-applies' activations the window has not shown yet (each is returned once).
#[tauri::command]
pub async fn activation_notices() -> Result<Vec<Activation>, Error> {
    Ok(take_notices())
}

/// One sentence of an activation's notices (`notices_unseen`). `safety`: the first notice of a folder that may still
/// run although its mod is off (RetainedPlacement `mayRun` and `off`): shown even with the notes switched off.
#[derive(Debug, Clone, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NoticeLine {
    pub text: String,
    #[serde(default)]
    pub safety: bool,
}

/// The lines of `lines` to raise now: the setting "Show notes about mod folders" (off: only `safety` lines), then
/// the ones not shown before for this game (kiln_manage::notices::unseen; asking remembers nothing).
fn unseen_lines(d: &kiln_manage::paths::DataDirs, game_id: &str, notes_on: bool, lines: Vec<NoticeLine>) -> Result<Vec<String>, Error> {
    let lines: Vec<String> = lines.into_iter().filter(|l| notes_on || l.safety).map(|l| l.text).collect();
    kiln_manage::notices::unseen(d, game_id, &lines)
}

/// `notices_shown`: the window has these sentences of `game_id` on screen (a toast in the stack, the Deck's notice
/// dialog): from now on they are not raised again (kept with the data folder in `installs/<id>.notices-seen.json`).
#[tauri::command]
pub async fn notices_shown(game_id: String, lines: Vec<String>) -> Result<(), Error> {
    blocking(move || kiln_manage::notices::mark_seen(&dirs()?, &game_id, &lines)).await
}

/// `notices_unseen`: which sentences of an activation the window raises for `game_id` (each once per game: the window
/// reports what it showed with `notices_shown`); the rest are in the notes the main list opens.
#[tauri::command]
pub async fn notices_unseen(game_id: String, lines: Vec<NoticeLine>) -> Result<Vec<String>, Error> {
    blocking(move || {
        let d = dirs()?;
        let on = crate::state::read_settings().map(|s| s.mod_folder_notes).unwrap_or(true);
        unseen_lines(&d, &game_id, on, lines)
    }).await
}

/// `profile::refresh_active` for one game that is not running, logged to launcher.log. Includes reports without a write.
pub(crate) fn reapply(d: &kiln_manage::paths::DataDirs, g: &kiln_manage::game::GameInstall) -> Option<Activation> {
    reapply_checked(d, g).map(|(a, _)| a)
}

/// The report and whether this call actually wrote profile.json. Determine that under the same lock as the refresh,
/// not before waiting for a profile page's save: that save may already have written the new generation.
///
/// One at a time: a profile page's save and the watcher both come here for the same change. Side by side they both
/// found the file out of date, both activated the profile (copying its mods twice at once) and both wrote the same
/// generation (seen in a player's launcher.log as every "written again" line twice). The second one now waits, finds
/// the file current and writes nothing.
pub(crate) fn reapply_checked(d: &kiln_manage::paths::DataDirs, g: &kiln_manage::game::GameInstall) -> Option<(Activation, bool)> {
    static ONE_AT_A_TIME: std::sync::Mutex<()> = std::sync::Mutex::new(());
    let _one = ONE_AT_A_TIME.lock().unwrap_or_else(|e| e.into_inner());
    if kiln_manage::proc::find_game(g).ok().flatten().is_some() {
        return None;
    }
    let before = profile::read_game_profile(&g.path).ok().flatten().map(|f| f.generation);
    match profile::refresh_active(d, g) {
        Ok(Some(a)) => {
            let log = |line: String| crate::commands::app::launcher_log("profile", &line);
            if before == Some(a.generation) {
                log(format!("{}: profile.json is current; the mod folders in {} were checked again", a.profile_id, g.path.display()));
            } else {
                log(format!("{} changed: written again to {} (generation {})", a.profile_id, g.path.display(), a.generation));
            }
            for r in &a.retained {
                log(format!("kept {} in the game folder: {}", r.path.display(), r.reason));
            }
            for line in a.kept_files.iter().chain(&a.warnings) {
                log(format!("note: {line}"));
            }
            let written = before != Some(a.generation);
            Some((a, written))
        }
        Ok(None) => None,
        Err(e) => {
            crate::commands::app::launcher_log("profile", &format!("the active profile of {} changed but does not apply: {}", g.path.display(), e.message));
            None
        }
    }
}

#[tauri::command]
pub async fn profile_delete(profile_id: String) -> Result<(), Error> {
    blocking(move || profile::delete(&dirs()?, &profile_id)).await
}

/// `game_id`: the game the preview is for; its kiln.json `mods.hosted` says whether a client's server mods start with
/// the player's own world (absent: they do, Kiln's default).
#[tauri::command]
pub async fn profile_resolve(profile_id: String, side: RunSide, game_id: Option<String>) -> Result<ResolvePreview, Error> {
    blocking(move || {
        let d = dirs()?;
        let p = profile::get(&d, &profile_id)?;
        let hosted = match game_id {
            Some(g) => profile::hosted_server_mods(&crate::state::game(&g)?.path),
            None => true,
        };
        profile::resolve_preview_for(&d, &p, side, hosted)
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
pub async fn profile_deactivate(app: AppHandle, game_id: String) -> Result<kiln_manage::engine_mods::PlacementCleanup, Error> {
    let id = game_id.clone();
    let report = blocking(move || {
        let g = crate::state::game(&id)?;
        crate::state::refuse_running(&g)?;
        profile::deactivate_report(&dirs()?, &g)
    })
    .await?;
    emit_status_later(app, game_id);
    Ok(report)
}

/// The profile of this data folder that is active in the game. None also when another data folder's profile is
/// (`GameStatus.otherFolderProfile` says that one): it is never shown as loaded here, even with the same id.
#[tauri::command]
pub async fn profile_active(game_id: String) -> Result<Option<Activation>, Error> {
    blocking(move || {
        let (d, g) = (dirs()?, crate::state::game(&game_id)?);
        if matches!(profile::game_profile_owner(&d, &g)?, profile::GameProfileOwner::Elsewhere { .. }) {
            return Ok(None);
        }
        profile::active(&d, &g)
    })
    .await
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
    announce_applied(&app, applied);
    Ok(saved)
}

/// The profile the simple flow uses in a game (kiln_manage::quick::ensure_default): the active one; with no profile at
/// all, a new "My mods" (every drop-in folder on), activated. Null when profiles exist but none is active here. Play
/// calls it after it installed Kiln.
#[tauri::command]
pub async fn profile_ensure_default(app: AppHandle, game_id: String) -> Result<Option<Activation>, Error> {
    let id = game_id.clone();
    let (activation, created, fresh) = blocking(move || {
        let d = dirs()?;
        let g = crate::state::game(&id)?;
        match kiln_manage::quick::ensure_default(&d, &g)? {
            None => Ok((None, false, false)),
            Some(t) if t.created || t.resumed => Ok((t.activation, t.created, true)),
            Some(_) => Ok((profile::active(&d, &g)?, false, false)),
        }
    })
    .await?;
    // a profile made or turned back on just now: what that activation kept is shown like a re-apply's (the answer is
    // also what profile_active says, which is never shown as news)
    if let Some(a) = activation.as_ref().filter(|_| fresh) {
        queue_notices(&app, a);
    }
    if created {
        crate::commands::app::launcher_log("profile", &format!("{} made and activated in {game_id}", kiln_manage::quick::DEFAULT_PROFILE));
        emit_status_later(app, game_id);
    }
    Ok(activation)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn activation(n: u64, note: bool) -> Activation {
        serde_json::from_value(serde_json::json!({ "gameId": "g", "profileId": "p", "generation": n, "pathMode": "z",
            "profileJson": "/g/kiln/profile.json", "copied": 0, "fingerprint": "kms1:00", "activated": "2026-11-11T00:00:00Z",
            "warnings": if note { vec!["a note"] } else { Vec::<&str>::new() } })).unwrap()
    }

    /// What a re-apply kept waits until the window takes it, also when that happened before the window listened; it is
    /// handed out once, and an activation with nothing to say is not kept.
    #[test]
    fn re_apply_notices_wait_for_the_window_and_are_taken_once() {
        take_notices();
        assert!(push_notice(&activation(1, true)));
        assert!(!push_notice(&activation(2, false)));
        let taken = take_notices();
        assert_eq!(taken.iter().map(|a| a.generation).collect::<Vec<_>>(), [1]);
        assert!(take_notices().is_empty(), "taken once");
        for n in 0..40 { push_notice(&activation(n, true)); }
        let taken = take_notices();
        assert_eq!(taken.iter().map(|a| a.generation).collect::<Vec<_>>(), (0..40).collect::<Vec<_>>(), "every undelivered notice waits, including its recovery paths");
        // A notice queued after one window's take belongs to the next take, not the emptied batch.
        assert!(push_notice(&activation(40, true)));
        assert_eq!(take_notices().iter().map(|a| a.generation).collect::<Vec<_>>(), [40]);
        assert!(take_notices().is_empty(), "a second window cannot take either batch again");
    }

    /// The setting "Show notes about mod folders" off: only the first notice of a folder that may still run although
    /// its mod is off comes; on: every line, each once per game.
    #[test]
    fn notices_unseen_follow_the_setting_and_come_once() {
        let root = std::env::temp_dir().join(format!("kl-notices-unseen-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let d = kiln_manage::paths::DataDirs::at(root.clone());
        let line = |t: &str, safety: bool| NoticeLine { text: t.into(), safety };
        let lines = || vec![line("retained:/g/mods/a: may still run", true), line("note:own folder", false)];
        assert_eq!(unseen_lines(&d, "g", false, lines()).unwrap(), ["retained:/g/mods/a: may still run"]);
        // asked, not shown: asked again, the same
        assert_eq!(unseen_lines(&d, "g", true, lines()).unwrap(), ["retained:/g/mods/a: may still run", "note:own folder"]);
        kiln_manage::notices::mark_seen(&d, "g", &["retained:/g/mods/a: may still run".to_string()]).unwrap();
        assert_eq!(unseen_lines(&d, "g", true, lines()).unwrap(), ["note:own folder"]);
        kiln_manage::notices::mark_seen(&d, "g", &["note:own folder".to_string()]).unwrap();
        assert!(unseen_lines(&d, "g", true, lines()).unwrap().is_empty());
        let _ = std::fs::remove_dir_all(root);
    }
}
