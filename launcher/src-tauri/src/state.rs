//! Launcher process state shared by the commands: the data dirs, settings, game lookup, and a
//! helper to run blocking crate calls off the async runtime.

use kiln_manage::game::GameInstall;
use kiln_manage::paths::DataDirs;
use kiln_manage::{Error, ErrorCode};
use std::sync::atomic::{AtomicUsize, Ordering};

/// The launcher's data folder for this process (`$KILN_DATA_DIR`, else the pointer's folder, else the platform
/// default; crate::folder): found once and kept until the process ends. Refused while the folder is held (a broken
/// pointer, a move here or in another launcher, a committed move waiting for the restart): the one choke point for
/// commands and background threads.
pub fn dirs() -> Result<DataDirs, Error> {
    crate::folder::current().dirs()
}

/// Closures [`blocking`] runs right now (a move waits for them to end: crate::commands::data_folder).
static IN_FLIGHT: AtomicUsize = AtomicUsize::new(0);

pub fn in_flight() -> usize {
    IN_FLIGHT.load(Ordering::SeqCst)
}

struct Flight;
impl Drop for Flight {
    fn drop(&mut self) {
        IN_FLIGHT.fetch_sub(1, Ordering::SeqCst);
    }
}

/// A writer of the data folder outside [`blocking`] and outside a task (a background thread's round, `launcher_log`, the
/// self-update's `update.json`): counted with `blocking`'s work while it lives, so a move waits for it to end. Take it
/// before [`dirs`]: a writer that got the folder before a move's hold is waited for, one that asks after it is refused.
#[must_use]
pub fn writing() -> Writing {
    IN_FLIGHT.fetch_add(1, Ordering::SeqCst);
    Writing(Flight)
}

/// [`writing`]'s guard.
pub struct Writing(#[allow(dead_code)] Flight);

/// Runs a blocking closure on the blocking pool (file and process work never runs on the IPC thread).
pub async fn blocking<T: Send + 'static>(f: impl FnOnce() -> Result<T, Error> + Send + 'static) -> Result<T, Error> {
    IN_FLIGHT.fetch_add(1, Ordering::SeqCst);
    let flight = Flight;
    let r = tauri::async_runtime::spawn_blocking(move || {
        let _flight = flight;
        f()
    });
    r.await.map_err(|e| Error::new(ErrorCode::Internal, format!("worker failed: {e}")))?
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

/// Where the launcher keeps kiln_manage::filecache's sha256s between runs.
pub fn file_hashes(d: &DataDirs) -> std::path::PathBuf {
    d.root.join("state").join("file-hashes.json")
}

/// Opens a folder, file or https URL with the desktop (xdg-open / explorer). Never waits for it.
pub fn open_external(target: &std::ffi::OsStr) -> Result<(), Error> {
    #[cfg(windows)]
    let mut cmd = std::process::Command::new("explorer");
    // without the AppImage's own environment: what it opens (a file manager, a browser) is a program of the host
    #[cfg(not(windows))]
    let mut cmd = kiln_manage::hostenv::command("xdg-open");
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
    // this data folder's profile, or another data folder's (a launcher before dev.26 after a move, KILN_DATA_DIR,
    // another launcher): that one is said, never shown as loaded
    let d = dirs()?;
    use kiln_manage::profile::GameProfileOwner as Owner;
    let (active_profile, other_folder_profile) = match kiln_manage::profile::game_profile_owner(&d, install) {
        Ok(Owner::Here { id }) => (Some(id), None),
        Ok(Owner::Elsewhere { id, root }) => (None, Some(kiln_manage::game::OtherFolderProfile { id, root })),
        Ok(Owner::None) | Err(_) => (None, None),
    };
    let boot = kiln_manage::state::read_boot(&install.path).ok().flatten();
    let newer_mods = kiln_manage::game::newer_mods(&d, &runtime, active_profile.as_deref());
    Ok(kiln_manage::game::GameStatus {
        id: install.id.clone(),
        running: proc.is_some(),
        pid: proc.as_ref().map(|p| p.pid),
        started: proc.as_ref().and_then(|p| kiln_manage::proc::start_rfc3339(p.pid)),
        steam_updating,
        runtime,
        active_profile,
        other_folder_profile,
        play_once_pending: install.path.join(kiln_manage::paths::game::PLAY_ONCE).is_file(),
        boot,
        launch_options: launch_options(install, &roots),
        newer_mods,
    })
}

/// `game::launch_options_info`, read again only when one of its inputs changed: the Steam roots it reads, the prefix's
/// user.reg (MBs) and each Steam user's localconfig.vdf (game_status runs every few seconds).
fn launch_options(install: &GameInstall, roots: &[kiln_manage::steam::SteamRoot]) -> Option<kiln_manage::game::LaunchOptionsInfo> {
    use kiln_manage::filecache::FileKey;
    use std::sync::{LazyLock, Mutex};
    type Inputs = (String, Vec<(std::path::PathBuf, Option<FileKey>)>);
    static LAST: LazyLock<Mutex<std::collections::HashMap<String, (Inputs, Option<kiln_manage::game::LaunchOptionsInfo>)>>> =
        LazyLock::new(Default::default);
    if install.platform != kiln_manage::game::Platform::Proton {
        return None;
    }
    // the roots it reads launch options from, as launch_options_info picks them (libraryfolders.vdf is small)
    let candidates: Vec<kiln_manage::steam::SteamRoot> = match install.library.as_deref().and_then(|l| kiln_manage::steam::root_of_library(roots, l)) {
        Some(r) => vec![r],
        None => roots.to_vec(),
    };
    let mut files = Vec::new();
    if let Some(p) = &install.prefix {
        files.push(p.join("user.reg"));
    }
    for r in &candidates {
        let users = r.path.join("userdata");
        files.push(users.clone());
        let mut u: Vec<std::path::PathBuf> = std::fs::read_dir(&users).into_iter().flatten().flatten().map(|e| e.path().join("config/localconfig.vdf")).collect();
        u.sort();
        files.extend(u);
    }
    let what = format!("{:?}|{:?}|{:?}|{:?}|{:?}", install.platform, install.prefix, install.library, install.app_id, candidates.iter().map(|r| &r.path).collect::<Vec<_>>());
    let inputs: Inputs = (what, files.into_iter().map(|f| { let k = FileKey::of(&f); (f, k) }).collect());
    if let Some((have, info)) = LAST.lock().ok().and_then(|m| m.get(&install.id).cloned())
        && have == inputs
    {
        return info;
    }
    let info = kiln_manage::game::launch_options_info(install, roots);
    if let Ok(mut m) = LAST.lock() {
        m.insert(install.id.clone(), (inputs, info.clone()));
    }
    info
}

/// Refuses while the game runs (activation, install, restore).
pub fn refuse_running(install: &GameInstall) -> Result<(), Error> {
    if kiln_manage::proc::find_game(install).ok().flatten().is_some() {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first."));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use kiln_manage::game::{GameKind, PathMode, Platform};
    use kiln_manage::steam::{SteamFlavor, SteamRoot};
    use std::path::Path;

    fn put(p: &Path, text: &str) {
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        // through a rename, then the old mtime back: what the cache must not be fooled by
        let t = std::fs::metadata(p).and_then(|m| m.modified()).ok();
        let tmp = p.with_extension("swap");
        std::fs::write(&tmp, text).unwrap();
        std::fs::rename(&tmp, p).unwrap();
        if let Some(t) = t {
            std::fs::File::options().write(true).open(p).unwrap().set_modified(t).unwrap();
        }
    }

    fn localconfig(opts: &str) -> String {
        format!("\"UserLocalConfigStore\"\n{{\n\"Software\"\n{{\n\"Valve\"\n{{\n\"Steam\"\n{{\n\"apps\"\n{{\n\"{}\"\n{{\n\"LaunchOptions\" \"{opts}\"\n}}\n}}\n}}\n}}\n}}\n}}\n", kiln_manage::CLIENT_APP_ID)
    }

    #[test]
    fn launch_options_are_read_again_when_an_input_changes() {
        let t = std::env::temp_dir().join(format!("kl-launchopts-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&t);
        let pfx = t.join("pfx");
        let root = t.join("Steam");
        std::fs::create_dir_all(root.join("steamapps")).unwrap();
        put(&pfx.join("user.reg"), "WINE REGISTRY Version 2\n");
        put(&root.join("userdata/111/config/localconfig.vdf"), &localconfig("%command%"));
        let g = GameInstall {
            id: "g-launchopts".into(), kind: GameKind::Client, path: t.join("game"), exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(pfx.clone()), build_id: None,
            exe_sha256: None, steam_flavor: None, path_mode: PathMode::Copy, source: "manual".into(),
        };
        let roots = vec![SteamRoot { flavor: SteamFlavor::Native, path: root.clone() }];
        let fresh = || kiln_manage::game::launch_options_info(&g, &roots);
        let check = |why: &str| assert_eq!(launch_options(&g, &roots), fresh(), "{why}");
        check("first");
        assert!(!fresh().unwrap().has_dll_override);
        check("unchanged");
        // the prefix gets the override
        put(&pfx.join("user.reg"), "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\DllOverrides] 1\n\"dinput8\"=\"native,builtin\"\n");
        assert!(fresh().unwrap().has_dll_override);
        check("user.reg rewritten");
        // launch options edited in Steam
        put(&root.join("userdata/111/config/localconfig.vdf"), &localconfig("WINEDLLOVERRIDES=dinput8=n,b %command%"));
        check("localconfig.vdf rewritten");
        // another Steam user
        put(&root.join("userdata/222/config/localconfig.vdf"), &localconfig("-high %command%"));
        std::fs::remove_dir_all(root.join("userdata/111")).unwrap();
        check("users changed");
        assert_eq!(fresh().unwrap().value.as_deref(), Some("-high %command%"));
        let _ = std::fs::remove_dir_all(&t);
    }
}
