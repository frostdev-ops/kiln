//! Tauri commands: server. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Dedicated servers: kiln_manage::server (instances, run, settings, mods, backups, query) and the server side of
//! modpacks (kiln_manage::modpack build/publish). Long operations are tasks (`kiln://task`); a start/stop also emits
//! `kiln://server` with the new status.
//!
//! Console: `server_console_open(serverId, maxLines)` returns the last
//! lines of the server's output (kiln_manage::server::console: the capture file of a windowless start, else the game
//! log) and a subscription; new lines arrive as `kiln://console` until `server_console_close`. Commands typed in the
//! Console go to Kiln.Admin over the existing admin session (`admin_call` op `command_run`).

use super::app::{spawn_task, to_value};
use super::TaskId;
use crate::events::{ServerEvent, SERVER};
use crate::state::{blocking, dirs};
use kiln_manage::library::LibraryRef;
use kiln_manage::modpack::ModpackPublishState;
use kiln_manage::server::backup::ServerBackup;
use kiln_manage::server::instance::{self, ServerInstance, ServerSpec};
use kiln_manage::server::query::ServerQuery;
use kiln_manage::server::settings::ServerSettings;
use kiln_manage::server::{ServerMod, ServerStatus};
use kiln_manage::server::console::{ConsoleFollower, ConsoleLine, ConsoleSnapshot, ConsoleSource, ConsoleUpdate};
use kiln_manage::{Error, ErrorCode};
use serde::Serialize;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use tauri::{AppHandle, Emitter};

fn emit_status(app: &AppHandle, s: &ServerStatus) {
    let _ = app.emit(SERVER, ServerEvent { server_id: s.id.clone(), status: s.clone() });
}

#[tauri::command]
pub async fn server_list() -> Result<Vec<ServerInstance>, Error> {
    blocking(instance::list).await
}

#[tauri::command]
pub async fn server_get(server_id: String) -> Result<ServerInstance, Error> {
    blocking(move || instance::get(&server_id)).await
}

#[tauri::command]
pub async fn server_create(app: AppHandle, spec: ServerSpec) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_create", format!("Creating server {}", spec.name), move |p| to_value(instance::create(&spec, p)?)))
}

#[tauri::command]
pub async fn server_import(dir: String, name: String) -> Result<ServerInstance, Error> {
    blocking(move || instance::import(std::path::Path::new(&dir), &name)).await
}

#[tauri::command]
pub async fn server_remove(server_id: String, delete_files: bool) -> Result<(), Error> {
    blocking(move || instance::remove(&server_id, delete_files)).await
}

#[tauri::command]
pub async fn server_status(server_id: String) -> Result<ServerStatus, Error> {
    blocking(move || kiln_manage::server::run::status(&instance::get(&server_id)?)).await
}

fn run_task(app: &AppHandle, kind: &str, verb: &str, server_id: String, f: fn(&ServerInstance, &mut dyn kiln_manage::task::Progress) -> Result<ServerStatus, Error>) -> TaskId {
    let a = app.clone();
    spawn_task(app, kind, format!("{verb} {server_id}"), move |p| {
        let inst = instance::get(&server_id)?;
        let r = f(&inst, p);
        if let Ok(s) = kiln_manage::server::run::status(&inst) {
            emit_status(&a, &s);
        }
        to_value(r?)
    })
}

#[tauri::command]
pub async fn server_start(app: AppHandle, server_id: String) -> Result<TaskId, Error> {
    Ok(run_task(&app, "server_start", "Starting", server_id, kiln_manage::server::run::start))
}

#[tauri::command]
pub async fn server_stop(app: AppHandle, server_id: String) -> Result<TaskId, Error> {
    Ok(run_task(&app, "server_stop", "Stopping", server_id, kiln_manage::server::run::stop))
}

#[tauri::command]
pub async fn server_restart(app: AppHandle, server_id: String) -> Result<TaskId, Error> {
    Ok(run_task(&app, "server_restart", "Restarting", server_id, kiln_manage::server::run::restart))
}

#[tauri::command]
pub async fn server_update(app: AppHandle, server_id: String) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_update", format!("Updating {server_id} (SteamCMD)"), move |p| {
        let inst = instance::get(&server_id)?;
        if inst.runner == instance::ServerRunner::Steam {
            return Err(Error::new(ErrorCode::Unsupported, "Steam updates this server itself."));
        }
        if kiln_manage::server::run::pid(&inst).is_some() {
            return Err(Error::new(ErrorCode::Conflict, "Stop the server before updating it."));
        }
        to_value(kiln_manage::server::steamcmd::update(inst.local_dir()?, p)?)
    }))
}

#[tauri::command]
pub async fn server_deploy(app: AppHandle, server_id: String, version: String) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_deploy", format!("Installing Kiln {version} on {server_id}"), move |p| {
        let inst = instance::get(&server_id)?;
        if kiln_manage::server::run::pid(&inst).is_some() {
            return Err(Error::new(ErrorCode::Conflict, "Stop the server before installing Kiln."));
        }
        let d = dirs()?;
        let bundle = kiln_manage::bundle::list(&d)?
            .into_iter()
            .find(|b| b.version == version)
            .ok_or_else(|| Error::not_found(format!("No runtime bundle {version}: import it on the Runtime page.")))?;
        let game = kiln_manage::game::inspect(inst.local_dir()?)?;
        let opts = kiln_manage::install::InstallOptions { components: None, switch_from_shroudforge: false, installer: Some(format!("kiln-launcher {}", env!("CARGO_PKG_VERSION"))) };
        let (receipt, report) = kiln_manage::install::install_report(&d, &game, &bundle.file, &opts, p)?;
        // the server's active profile, written again for the runtime it has now (an older core refuses newer forms)
        let resumed = super::runtime::resume_profile(&d, &game, p);
        let mut v = super::runtime::install_result(&receipt, &report)?;
        super::runtime::add_resumed(&mut v, resumed);
        Ok(v)
    }))
}

#[tauri::command]
pub async fn server_settings_get(server_id: String) -> Result<ServerSettings, Error> {
    blocking(move || kiln_manage::server::settings::read(&instance::get(&server_id)?)).await
}

#[tauri::command]
pub async fn server_settings_set(server_id: String, values: serde_json::Value) -> Result<ServerSettings, Error> {
    blocking(move || kiln_manage::server::settings::write(&instance::get(&server_id)?, &values)).await
}

#[tauri::command]
pub async fn server_mods(server_id: String) -> Result<Vec<ServerMod>, Error> {
    blocking(move || kiln_manage::server::control::mods(&instance::get(&server_id)?)).await
}

#[tauri::command]
pub async fn server_mods_sync(app: AppHandle, server_id: String, mods: Vec<LibraryRef>) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_mods_sync", format!("Syncing mods to {server_id}"), move |p| {
        let inst = instance::get(&server_id)?;
        to_value(kiln_manage::server::control::sync(&dirs()?, &inst, &mods, p)?)
    }))
}

#[tauri::command]
pub async fn server_mod_enable(server_id: String, folder: String, enabled: bool) -> Result<(), Error> {
    blocking(move || kiln_manage::server::control::set_enabled(&instance::get(&server_id)?, &folder, enabled)).await
}

#[tauri::command]
pub async fn server_backups(server_id: String) -> Result<Vec<ServerBackup>, Error> {
    blocking(move || kiln_manage::server::backup::list(&instance::get(&server_id)?)).await
}

#[tauri::command]
pub async fn server_backup_create(app: AppHandle, server_id: String) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_backup_create", format!("Backing up {server_id}"), move |p| {
        to_value(kiln_manage::server::backup::create(&instance::get(&server_id)?, p)?)
    }))
}

#[tauri::command]
pub async fn server_backup_restore(app: AppHandle, server_id: String, name: String) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_backup_restore", format!("Restoring {name} on {server_id}"), move |p| {
        kiln_manage::server::backup::restore(&instance::get(&server_id)?, &name, p)?;
        Ok(serde_json::Value::Null)
    }))
}

#[tauri::command]
pub async fn server_query(server_id: String) -> Result<ServerQuery, Error> {
    blocking(move || {
        let inst = instance::get(&server_id)?;
        let host = inst.remote.as_ref().map(|r| r.host.clone()).unwrap_or_else(|| "127.0.0.1".into());
        kiln_manage::server::query::query(&format!("{host}:{}", inst.query_port), std::time::Duration::from_secs(2))
    })
    .await
}

#[tauri::command]
pub async fn server_modpack_status(server_id: String) -> Result<ModpackPublishState, Error> {
    blocking(move || kiln_manage::modpack::publish_state(instance::get(&server_id)?.local_dir()?)).await
}

#[tauri::command]
pub async fn server_modpack_build(app: AppHandle, server_id: String, name: String, version: String) -> Result<TaskId, Error> {
    Ok(spawn_task(&app, "server_modpack_build", format!("Building modpack {name} {version}"), move |p| {
        let inst = instance::get(&server_id)?;
        let out = dirs()?.root.join("modpacks").join("built").join(&inst.id);
        to_value(kiln_manage::modpack::build(inst.local_dir()?, &out, &name, &version, p)?)
    }))
}

#[tauri::command]
pub async fn server_modpack_publish(server_id: String, url: String) -> Result<ModpackPublishState, Error> {
    blocking(move || {
        let inst = instance::get(&server_id)?;
        let dir = inst.local_dir()?;
        let built = kiln_manage::modpack::publish_state(dir)?
            .built
            .ok_or_else(|| Error::not_found("Build the modpack first."))?;
        kiln_manage::modpack::publish(dir, &built, &url)
    })
    .await
}

// ---------------------------------------------------------------- console

/// `kiln://console`: new lines of a followed server console (contract addition; belongs in events.rs).
pub const CONSOLE: &str = "kiln://console";

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleEvent {
    pub subscription: String,
    pub server_id: String,
    /// true: `lines` replace everything shown (the source changed: a start, a stop, a start outside the launcher)
    pub reset: bool,
    pub source: ConsoleSource,
    /// with `reset`: the file now shown, and whether older lines were left out
    pub path: Option<std::path::PathBuf>,
    pub truncated: bool,
    pub lines: Vec<ConsoleLine>,
}

/// `server_console_open`'s answer: the subscription and the snapshot (`source, path, lines, truncated`).
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleOpen {
    pub subscription: String,
    pub server_id: String,
    #[serde(flatten)]
    pub snapshot: ConsoleSnapshot,
}

/// Open consoles (subscription, stop flag); the oldest is closed past the limit (a reloaded webview leaves its
/// subscriptions behind).
static CONSOLES: LazyLock<Mutex<Vec<(String, Arc<AtomicBool>)>>> = LazyLock::new(|| Mutex::new(Vec::new()));
static CONSOLE_SEQ: AtomicU64 = AtomicU64::new(0);
const MAX_CONSOLES: usize = 16;
/// Lines per `kiln://console` event at most (a burst is split).
const CONSOLE_BATCH: usize = 2000;

#[tauri::command]
pub async fn server_console_open(app: AppHandle, server_id: String, max_lines: Option<u32>) -> Result<ConsoleOpen, Error> {
    blocking(move || {
        let inst = instance::get(&server_id)?;
        if inst.runner == instance::ServerRunner::Remote {
            return Err(Error::new(ErrorCode::Unsupported, "A remote server's output stays on its machine; its admin console works over SSH."));
        }
        let max = max_lines.map(|n| n as usize).unwrap_or(kiln_manage::server::console::DEFAULT_LINES);
        let (snapshot, mut follower) = ConsoleFollower::open(&inst, max);
        let id = format!("console-{}", CONSOLE_SEQ.fetch_add(1, Ordering::Relaxed) + 1);
        let stop = Arc::new(AtomicBool::new(false));
        {
            let mut subs = CONSOLES.lock().unwrap();
            subs.push((id.clone(), stop.clone()));
            while subs.len() > MAX_CONSOLES {
                subs.remove(0).1.store(true, Ordering::Relaxed);
            }
        }
        let (sub, sid) = (id.clone(), server_id.clone());
        std::thread::Builder::new()
            .name(format!("kiln-{id}"))
            .spawn(move || {
                let emit = |reset: bool, source: ConsoleSource, path: Option<std::path::PathBuf>, truncated: bool, lines: Vec<ConsoleLine>| {
                    let _ = app.emit(CONSOLE, ConsoleEvent { subscription: sub.clone(), server_id: sid.clone(), reset, source, path, truncated, lines });
                };
                while !stop.load(Ordering::Relaxed) {
                    std::thread::sleep(std::time::Duration::from_millis(500));
                    if stop.load(Ordering::Relaxed) {
                        break;
                    }
                    match follower.poll() {
                        ConsoleUpdate::Lines(lines) => {
                            let mut lines = lines;
                            while !lines.is_empty() {
                                let rest = lines.split_off(lines.len().min(CONSOLE_BATCH));
                                emit(false, follower.source(), None, false, lines);
                                lines = rest;
                            }
                        }
                        ConsoleUpdate::Reset(s) => emit(true, s.source, s.path, s.truncated, s.lines),
                    }
                }
            })
            .map_err(|e| Error::new(ErrorCode::Internal, format!("console thread: {e}")))?;
        Ok(ConsoleOpen { subscription: id, server_id, snapshot })
    })
    .await
}

#[tauri::command]
pub async fn server_console_close(subscription: String) -> Result<(), Error> {
    let mut subs = CONSOLES.lock().unwrap();
    match subs.iter().position(|(id, _)| *id == subscription) {
        Some(i) => {
            subs.remove(i).1.store(true, Ordering::Relaxed);
            Ok(())
        }
        None => Err(Error::not_found(format!("no console {subscription}"))),
    }
}
