//! Launcher process state shared by the commands: the data dirs, settings, game lookup, and a
//! helper to run blocking crate calls off the async runtime.

use kiln_manage::game::GameInstall;
use kiln_manage::paths::DataDirs;
use kiln_manage::{Error, ErrorCode};
use std::sync::OnceLock;

static DIRS: OnceLock<DataDirs> = OnceLock::new();

/// The launcher data dirs (`$KILN_DATA_DIR`, else the platform default); the root is created on first use.
pub fn dirs() -> Result<DataDirs, Error> {
    if let Some(d) = DIRS.get() {
        return Ok(d.clone());
    }
    let d = DataDirs::locate()?;
    std::fs::create_dir_all(&d.root).map_err(|e| Error::from(e).with_detail(d.root.display().to_string()))?;
    Ok(DIRS.get_or_init(|| d).clone())
}

/// Runs a blocking closure on the blocking pool (file and process work never runs on the IPC thread).
pub async fn blocking<T: Send + 'static>(f: impl FnOnce() -> Result<T, Error> + Send + 'static) -> Result<T, Error> {
    tauri::async_runtime::spawn_blocking(f).await.map_err(|e| Error::new(ErrorCode::Internal, format!("worker failed: {e}")))?
}

pub fn read_settings() -> Result<crate::commands::app::LauncherSettings, Error> {
    let d = dirs()?;
    Ok(kiln_manage::util::read_json(&d.settings())?.unwrap_or_default())
}

/// A known game install by id (`<data>/installs/`; ids are stable per folder).
pub fn game(id: &str) -> Result<GameInstall, Error> {
    let d = dirs()?;
    kiln_manage::game::known(&d)?
        .into_iter()
        .find(|g| g.id == id)
        .ok_or_else(|| Error::not_found(format!("No game '{id}'. Detect or add it on Play.")))
}

/// Opens a folder, file or https URL with the desktop (xdg-open / explorer). Never waits for it.
pub fn open_external(target: &std::ffi::OsStr) -> Result<(), Error> {
    #[cfg(windows)]
    let mut cmd = std::process::Command::new("explorer");
    #[cfg(not(windows))]
    let mut cmd = std::process::Command::new("xdg-open");
    cmd.arg(target).stdin(std::process::Stdio::null()).stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null());
    cmd.spawn().map(|_| ()).map_err(|e| Error::new(ErrorCode::Unsupported, format!("could not open it: {e}")))
}

// ---------------------------------------------------------------- Steam roots, install state cache

/// Steam roots: what kiln-manage finds (or `$KILN_STEAM_ROOTS`) plus the settings' extra folders.
pub fn steam_roots() -> Vec<kiln_manage::steam::SteamRoot> {
    let mut roots = kiln_manage::steam::roots().unwrap_or_default();
    if let Ok(s) = read_settings() {
        for r in s.steam_roots {
            let p = std::path::PathBuf::from(&r);
            if !roots.iter().any(|x| x.path == p) {
                roots.push(kiln_manage::steam::SteamRoot { flavor: kiln_manage::steam::flavor_of(&p), path: p });
            }
        }
    }
    roots
}

use kiln_manage::install::InstallState;

/// `install::status` (cheap: kiln-manage caches file hashes by path, size and mtime). `fresh` is kept for callers that
/// want to say so; both paths recompute.
pub fn install_state(install: &GameInstall, _fresh: bool) -> Result<InstallState, Error> {
    kiln_manage::install::status(&dirs()?, install)
}

/// Kept for callers after install/uninstall (nothing cached here any more).
pub fn forget_install_state(_id: &str) {}

/// `game::status` with the launcher's Steam roots (same fields as kiln_manage::game::status_with).
pub fn game_status(install: &GameInstall) -> Result<kiln_manage::game::GameStatus, Error> {
    let roots = steam_roots();
    let proc = kiln_manage::proc::find_game(install).ok().flatten();
    let steam_updating = install
        .library
        .as_deref()
        .and_then(|l| kiln_manage::steam::app_manifest(l, install.app_id).ok().flatten())
        .is_some_and(|m| m.updating);
    let runtime = install_state(install, false)?;
    let active_profile = kiln_manage::profile::read_game_profile(&install.path).ok().flatten().map(|p| p.id);
    let boot = kiln_manage::state::read_boot(&install.path).ok().flatten();
    let newer_mods = kiln_manage::game::newer_mods(&dirs()?, &runtime, active_profile.as_deref());
    Ok(kiln_manage::game::GameStatus {
        id: install.id.clone(),
        running: proc.is_some(),
        pid: proc.as_ref().map(|p| p.pid),
        started: proc.as_ref().and_then(|p| kiln_manage::proc::start_rfc3339(p.pid)),
        steam_updating,
        runtime,
        active_profile,
        play_once_pending: install.path.join(kiln_manage::paths::game::PLAY_ONCE).is_file(),
        boot,
        launch_options: kiln_manage::game::launch_options_info(install, &roots),
        newer_mods,
    })
}

/// Refuses while the game runs (activation, install, restore).
pub fn refuse_running(install: &GameInstall) -> Result<(), Error> {
    if kiln_manage::proc::find_game(install).ok().flatten().is_some() {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first."));
    }
    Ok(())
}
