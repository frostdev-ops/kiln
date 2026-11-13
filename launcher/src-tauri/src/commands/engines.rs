//! Tauri commands: other engines' mods (docs/kiln/engines.md "Managing every engine's mods"; contracts.md section 9).
//! Settings per format (ShroudForge's `extended.mod.json`, Shroudtopia's `shroudtopia.json`), the plugins' state files
//! for the Play and Logs pages, and read-only shared archive backup inspection. The legacy direct-restore command
//! refuses; the separate archive writer owns restoration. All management calls use kiln_manage::engine_mods.

use crate::state::{blocking, dirs};
use kiln_manage::{Error, ErrorCode};
use kiln_manage::engine_mods::{self, Adopted, EmlBackup, EngineSettings, EngineStatus, OwnFolder};
use kiln_manage::library::LibraryRef;
use kiln_manage::manifest::ModKind;
use std::path::PathBuf;

fn game_running_with(game_dir: &std::path::Path, mut find: impl FnMut(&std::path::Path, &str) -> Result<bool, Error>) -> Result<bool, Error> {
    for exe in ["enshrouded.exe", "enshrouded_server.exe"] {
        if find(game_dir, exe)? {
            return Ok(true);
        }
    }
    Ok(false)
}

fn game_running(game: &kiln_manage::game::GameInstall) -> Result<bool, Error> {
    game_running_with(&game.path, |dir, exe| kiln_manage::proc::find_exe(dir, exe).map(|p| p.is_some()))
}

/// Settings calls only address a valid entry in this profile, never an arbitrary game-folder path.
fn settings_entry<'a>(profile: &'a kiln_manage::profile::Profile, kind: ModKind, id: &str) -> Result<&'a kiln_manage::profile::ProfileMod, Error> {
    if !kiln_manage::manifest::is_mod_id(id) {
        return Err(Error::invalid("invalid engine mod id"));
    }
    if !kind.is_engine() && kind != ModKind::Emf {
        return Err(Error::new(ErrorCode::Unsupported, "use this mod kind's config editor"));
    }
    profile.mods.list(kind).iter().find(|m| m.id == id)
        .ok_or_else(|| Error::not_found(format!("{} mod {id} is not in profile {}", kind.as_str(), profile.id)))
}

fn check_write_context(kind: ModKind, profile_id: &str, active: Option<&str>, running: Result<bool, Error>) -> Result<(), Error> {
    if running? {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first."));
    }
    if kind == ModKind::ShroudForge && active != Some(profile_id) {
        return Err(Error::new(ErrorCode::Conflict, "Activate this profile in the selected game before editing ShroudForge settings."));
    }
    Ok(())
}

fn settings_config_dir(d: &kiln_manage::paths::DataDirs, profile_id: &str, selected_copy: Option<&std::path::Path>, copies: &[PathBuf]) -> Result<PathBuf, Error> {
    let game = match (selected_copy, copies) {
        (Some(game), _) => Some(game),
        (None, []) => None,
        (None, [game]) => Some(game.as_path()),
        _ => return Err(Error::new(ErrorCode::Conflict, "Select the game whose copied profile settings you want to edit.")),
    };
    Ok(match game {
        Some(game) => game.join(kiln_manage::paths::game::COPY_PROFILES).join(profile_id).join("config"),
        None => d.profile(profile_id).join("config"),
    })
}

fn settings_ctx(kind: ModKind, id: &str, profile_id: &str, game_id: Option<&str>, writing: bool) -> Result<(Option<PathBuf>, PathBuf, Option<PathBuf>), Error> {
    let d = dirs()?;
    // get validates profile_id before it is ever joined to a path. Settings are scoped to an actual profile entry.
    let profile = kiln_manage::profile::get(&d, profile_id)?;
    let m = settings_entry(&profile, kind, id)?;
    let game = game_id.map(crate::state::game).transpose()?;
    let active = game.as_ref().map(|g| kiln_manage::profile::read_game_profile(&g.path)).transpose()?.flatten();
    let copied = active.as_ref().is_some_and(|p| p.id == profile_id && p.path_mode == Some(kiln_manage::game::PathMode::Copy));
    if writing {
        let running = game.as_ref().map_or(Ok(false), game_running);
        check_write_context(kind, profile_id, active.as_ref().map(|p| p.id.as_str()), running)?;
    }
    let mut copies = Vec::new();
    if writing || kind == ModKind::Shroudtopia {
        // Omitting game_id must not bypass the guard or edit a source that an active game's copy_back would overwrite.
        for g in kiln_manage::game::known(&d)? {
            if let Some(p) = kiln_manage::profile::read_game_profile(&g.path)?
                && p.id == profile_id
            {
                if writing && game_running(&g)? {
                    return Err(Error::new(ErrorCode::GameRunning, "Close the game first."));
                }
                if p.path_mode == Some(kiln_manage::game::PathMode::Copy) {
                    copies.push(g.path);
                }
            }
        }
    }
    // In copy mode these are the live settings. Editing the source instead would lose the edit at activation.
    let config_dir = if kind == ModKind::Shroudtopia {
        settings_config_dir(&d, profile_id, game.as_ref().filter(|_| copied).map(|g| g.path.as_path()), &copies)?
    } else {
        d.profile(profile_id).join("config")
    };
    // An inactive profile reads its library defaults, not another profile's placed ShroudForge settings.
    let game_dir = game.filter(|_| kind != ModKind::ShroudForge || active.as_ref().is_some_and(|p| p.id == profile_id)).map(|g| g.path);
    let folder = Some(kiln_manage::library::path_of(&d, &LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?)
        .filter(|p| p.is_dir());
    Ok((game_dir, config_dir, folder))
}

#[tauri::command]
pub async fn engine_settings_get(kind: ModKind, id: String, profile_id: String, game_id: Option<String>) -> Result<EngineSettings, Error> {
    blocking(move || {
        let (game_dir, config_dir, folder) = settings_ctx(kind, &id, &profile_id, game_id.as_deref(), false)?;
        engine_mods::settings(kind, &id, game_dir.as_deref(), &config_dir, folder.as_deref())
    })
    .await
}

#[tauri::command]
pub async fn engine_settings_set(kind: ModKind, id: String, profile_id: String, game_id: Option<String>, key: String,
                                 value: serde_json::Value) -> Result<EngineSettings, Error> {
    blocking(move || {
        let (game_dir, config_dir, folder) = settings_ctx(kind, &id, &profile_id, game_id.as_deref(), true)?;
        engine_mods::set_setting(kind, &id, &key, &value, game_dir.as_deref(), &config_dir, folder.as_deref())
    })
    .await
}

/// Taking a folder over needs the same state a placement does: no game reading the folder, and the profile the game
/// uses (its entry names the version the folder is compared with, and the next activation manages the folder).
fn check_adopt_context(profile_id: &str, active: Option<&str>, running: Result<bool, Error>) -> Result<(), Error> {
    if running? {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first."));
    }
    if active != Some(profile_id) {
        return Err(Error::new(ErrorCode::Conflict, "Load this profile in the selected game first: Kiln takes a folder over for the profile the game uses."));
    }
    Ok(())
}

/// The player's own ShroudForge folders that serve entries of the profile in the selected game, each with whether
/// Kiln can take it over (`refusal`: why not, naming the first file that differs). Empty unless the profile is the one
/// the game uses: only then does the game folder serve its entries. Reads only.
#[tauri::command]
pub async fn engine_own_folders(profile_id: String, game_id: String) -> Result<Vec<OwnFolder>, Error> {
    blocking(move || {
        let d = dirs()?;
        let profile = kiln_manage::profile::get(&d, &profile_id)?;
        let game = crate::state::game(&game_id)?;
        let active = kiln_manage::profile::read_game_profile(&game.path)?;
        if active.as_ref().map(|p| p.id.as_str()) != Some(profile_id.as_str()) {
            return Ok(Vec::new());
        }
        engine_mods::own_folders(&d, &profile, &game.path)
    })
    .await
}

/// "Let Kiln manage this folder": the player's own ShroudForge folder that serves the profile entry becomes Kiln's
/// placement (engine_mods::adopt). Only on the player's request, only when its files are the Library entry's (`refused`
/// with the first file that differs), with the game closed and the profile loaded in it. Nothing in the folder is
/// rewritten; the next activation switches and updates it.
#[tauri::command]
pub async fn engine_adopt_folder(kind: ModKind, id: String, profile_id: String, game_id: String) -> Result<Adopted, Error> {
    blocking(move || {
        let d = dirs()?;
        // get validates profile_id before it is ever joined to a path; the entry must be in this profile
        let profile = kiln_manage::profile::get(&d, &profile_id)?;
        settings_entry(&profile, kind, &id)?;
        let game = crate::state::game(&game_id)?;
        let active = kiln_manage::profile::read_game_profile(&game.path)?;
        check_adopt_context(&profile_id, active.as_ref().map(|p| p.id.as_str()), game_running(&game))?;
        engine_mods::adopt(&d, &profile, &game.path, kind, &id)
    })
    .await
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn taking_a_folder_over_needs_the_game_closed_and_its_profile_loaded() {
        assert_eq!(check_adopt_context("p", Some("p"), Ok(true)).unwrap_err().code, ErrorCode::GameRunning);
        assert_eq!(check_adopt_context("p", Some("other"), Ok(false)).unwrap_err().code, ErrorCode::Conflict);
        assert_eq!(check_adopt_context("p", None, Ok(false)).unwrap_err().code, ErrorCode::Conflict);
        assert!(check_adopt_context("p", Some("p"), Ok(false)).is_ok());
        assert_eq!(check_adopt_context("p", Some("p"), Err(Error::invalid("cannot tell"))).unwrap_err().code, ErrorCode::InvalidArgument);
    }

    fn profile() -> kiln_manage::profile::Profile {
        serde_json::from_value(serde_json::json!({
            "format": 1, "id": "test", "name": "Test", "created": "", "updated": "", "generation": 1,
            "mods": { "shroudforge": [{ "id": "mine", "version": "1.0.0", "hash": format!("kdh1:{}", "a".repeat(64)), "enabled": true }] }
        })).unwrap()
    }

    #[test]
    fn engine_settings_require_valid_profile_member() {
        let p = profile();
        assert!(settings_entry(&p, ModKind::ShroudForge, "mine").is_ok());
        for id in ["../outside", "a/b", "a\\b", "", "/tmp/x"] {
            assert_eq!(settings_entry(&p, ModKind::ShroudForge, id).unwrap_err().code, ErrorCode::InvalidArgument);
        }
        assert_eq!(settings_entry(&p, ModKind::ShroudForge, "player-owned").unwrap_err().code, ErrorCode::NotFound);
        assert_eq!(settings_entry(&p, ModKind::Shroudtopia, "mine").unwrap_err().code, ErrorCode::NotFound);
    }

    #[test]
    fn settings_write_requires_stopped_game_and_active_shroudforge_profile() {
        for kind in [ModKind::ShroudForge, ModKind::Shroudtopia] {
            assert_eq!(check_write_context(kind, "test", Some("test"), Ok(true)).unwrap_err().code, ErrorCode::GameRunning);
        }
        for active in [None, Some("another")] {
            assert_eq!(check_write_context(ModKind::ShroudForge, "test", active, Ok(false)).unwrap_err().code, ErrorCode::Conflict);
        }
        assert!(check_write_context(ModKind::ShroudForge, "test", Some("test"), Ok(false)).is_ok());
        assert!(check_write_context(ModKind::Shroudtopia, "test", None, Ok(false)).is_ok());
        // An unreadable process table must not be treated as a stopped game (including EML restore).
        assert_eq!(check_write_context(ModKind::Eml, "", None, Err(Error::invalid("process query failed"))).unwrap_err().message, "process query failed");
    }

    #[test]
    fn enshwick_shroudlink_and_ember_entries_reach_their_settings_answer() {
        let hash = format!("kdh1:{}", "a".repeat(64));
        let p: kiln_manage::profile::Profile = serde_json::from_value(serde_json::json!({
            "format": 1, "id": "test", "name": "Test", "created": "", "updated": "", "generation": 1,
            "mods": { "enshwick": [{ "id": "you.lanterns", "version": "1.0.0", "hash": hash, "enabled": true }],
                      "shroudlink": [{ "id": "chestsort", "version": "0.0.0", "hash": hash, "enabled": true }],
                      "ember": [{ "id": "private-chests", "version": "0.0.0", "hash": hash, "enabled": true }] }
        })).unwrap();
        for (kind, id) in [(ModKind::Enshwick, "you.lanterns"), (ModKind::Shroudlink, "chestsort"), (ModKind::Ember, "private-chests")] {
            assert!(settings_entry(&p, kind, id).is_ok(), "{kind:?}");
            // nothing Kiln edits: the note says why (Enshwick per world, Ember not running yet)
            let s = engine_mods::settings(kind, id, None, std::path::Path::new("/none"), None).unwrap();
            assert_eq!(s.format, "none");
            assert!(s.note.is_some());
        }
        assert_eq!(settings_entry(&p, ModKind::Ember, "chestsort").unwrap_err().code, ErrorCode::NotFound);
    }

    #[test]
    fn copied_profile_settings_use_the_game_copy_that_activation_reads_back() {
        let d = kiln_manage::paths::DataDirs { root: PathBuf::from("/data") };
        let copies = [PathBuf::from("/game")];
        assert_eq!(settings_config_dir(&d, "test", Some(std::path::Path::new("/game")), &copies).unwrap(), PathBuf::from("/game/kiln/profiles/test/config"));
        assert_eq!(settings_config_dir(&d, "test", None, &copies).unwrap(), PathBuf::from("/game/kiln/profiles/test/config"));
        assert_eq!(settings_config_dir(&d, "test", None, &[]).unwrap(), PathBuf::from("/data/profiles/test/config"));
        let copies = [PathBuf::from("/game"), PathBuf::from("/other")];
        assert_eq!(settings_config_dir(&d, "test", None, &copies).unwrap_err().code, ErrorCode::Conflict);
        assert_eq!(settings_config_dir(&d, "test", Some(std::path::Path::new("/other")), &copies).unwrap(), PathBuf::from("/other/kiln/profiles/test/config"));
    }

    #[test]
    fn game_file_writes_check_both_colocated_executables_and_propagate_query_errors() {
        let mut checked = Vec::new();
        let running = game_running_with(std::path::Path::new("/game"), |dir, exe| {
            assert_eq!(dir, std::path::Path::new("/game"));
            checked.push(exe.to_string());
            Ok(exe == "enshrouded_server.exe")
        });
        assert_eq!(check_write_context(ModKind::Eml, "", None, running).unwrap_err().code, ErrorCode::GameRunning);
        assert_eq!(checked, ["enshrouded.exe", "enshrouded_server.exe"]);
        let error = game_running_with(std::path::Path::new("/game"), |_, _| Err(Error::new(ErrorCode::Io, "process table unreadable")));
        assert_eq!(check_write_context(ModKind::Eml, "", None, error).unwrap_err().code, ErrorCode::Io);
    }
}

#[tauri::command]
pub async fn engine_status(game_id: String) -> Result<Vec<EngineStatus>, Error> {
    blocking(move || Ok(engine_mods::status(&crate::state::game(&game_id)?.path))).await
}

#[tauri::command]
pub async fn eml_backup(game_id: String) -> Result<Vec<EmlBackup>, Error> {
    blocking(move || engine_mods::eml_backup(&crate::state::game(&game_id)?.path)).await
}

/// Legacy command kept for older callers: it always refuses (`game_running` while the game runs, else `refused`) and
/// copies nothing. A shared `.kfc.bak` cannot prove who wrote an archive; updated kiln-eml's archive writer restores
/// only its own unchanged output at the next start (engine_mods::eml_restore_vanilla).
#[tauri::command]
pub async fn eml_restore(game_id: String) -> Result<Vec<String>, Error> {
    blocking(move || {
        let g = crate::state::game(&game_id)?;
        check_write_context(ModKind::Eml, "", None, game_running(&g))?;
        engine_mods::eml_restore_vanilla(&g.path)
    })
    .await
}
