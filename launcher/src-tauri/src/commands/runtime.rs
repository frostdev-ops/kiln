//! Tauri commands: runtime. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Bundles on kiln_manage::bundle, install/uninstall/rollback on kiln_manage::install, run as tasks (spawn_task).

use super::TaskId;
use super::app::{spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::bundle::{self, RuntimeBundle};
use kiln_manage::game::{GameInstall, GameKind};
use kiln_manage::install::{self, InstallOptions, InstallReceipt, InstallState};
use kiln_manage::manifest::RunSide;
use kiln_manage::pe::{self, ProxyClass};
use kiln_manage::task::Progress;
use kiln_manage::{Error, ErrorCode};
use tauri::path::BaseDirectory;
use tauri::{AppHandle, Manager};

/// The Kiln runtime this launcher ships (tools/launcher/shipped-runtime.sh), as a resource of the app.
const SHIPPED_RUNTIME: &str = "runtime/kiln-runtime.zip";
static SHIPPED_DONE: std::sync::Mutex<bool> = std::sync::Mutex::new(false);

/// Copies the shipped runtime into `<data>/runtime/`, once per run. A build without one has nothing to copy, and
/// Runtime still takes a dropped zip. Callers wait on the lock, so the list never shows before the copy is done.
pub fn import_shipped(app: &AppHandle) {
    let mut done = SHIPPED_DONE.lock().unwrap_or_else(|e| e.into_inner());
    if std::mem::replace(&mut *done, true) {
        return;
    }
    let Ok(zip) = app.path().resolve(SHIPPED_RUNTIME, BaseDirectory::Resource) else { return };
    if !zip.is_file() {
        return;
    }
    match dirs().and_then(|d| bundle::import_shipped(&d, &zip)) {
        Ok(Some(b)) => eprintln!("shipped Kiln runtime {} added", b.version),
        Ok(None) => {}
        Err(e) => eprintln!("shipped Kiln runtime {}: {e}", zip.display()),
    }
}

#[tauri::command]
pub async fn runtime_list(app: AppHandle) -> Result<Vec<RuntimeBundle>, Error> {
    blocking(move || {
        import_shipped(&app);
        bundle::list(&dirs()?)
    })
    .await
}

#[tauri::command]
pub async fn runtime_import(path: String) -> Result<RuntimeBundle, Error> {
    blocking(move || bundle::import(&dirs()?, std::path::Path::new(&path))).await
}

/// Always fresh (hashes the installed files to list changed ones).
#[tauri::command]
pub async fn runtime_status(game_id: String) -> Result<InstallState, Error> {
    blocking(move || crate::state::install_state(&crate::state::game(&game_id)?, true)).await
}

fn installer() -> Option<String> {
    Some(format!("kiln-launcher {}", env!("CARGO_PKG_VERSION")))
}

/// The fresh status goes out from its own thread: computing it (hashes, the bundle list, the process list) must not
/// hold the task's end back.
fn after_change(app: &AppHandle, game_id: &str) {
    crate::state::forget_install_state(game_id);
    let (app, id) = (app.clone(), game_id.to_string());
    std::thread::spawn(move || crate::watch::emit_status(&app, &id));
}

fn side_of(g: &GameInstall) -> RunSide {
    match g.kind {
        GameKind::Server => RunSide::Server,
        GameKind::Client => RunSide::Client,
    }
}

/// The task result of an install: the receipt, with `changedBackedUp` (the Kiln files changed by hand that the install
/// replaced; their copies are in the backup) and `backup` (the launcher's copy of the backup folder, or null) added.
pub(crate) fn install_result(receipt: &InstallReceipt, report: &install::InstallReport) -> Result<serde_json::Value, Error> {
    let mut v = to_value(receipt)?;
    if let Some(o) = v.as_object_mut() {
        o.insert("changedBackedUp".into(), serde_json::json!(report.changed_backed_up));
        o.insert("backup".into(), serde_json::json!(report.backup));
    }
    Ok(v)
}

/// An install only counts when the game folder says so afterwards: the receipt names the bundle's version and parts,
/// and every file it lists has its bytes. Anything else fails the task with what is wrong.
pub(crate) fn check_applied(g: &GameInstall, version: &str, receipt: &InstallReceipt, p: &mut dyn Progress) -> Result<(), Error> {
    p.report(Some(0.95), "checking the game folder");
    let on_disk = install::read_receipt(&g.path)?
        .ok_or_else(|| Error::new(ErrorCode::Internal, format!("the install ended, but {} has no kiln/.install.json", g.path.display())))?;
    if on_disk.runtime.version != version || on_disk.runtime.components != receipt.runtime.components {
        return Err(Error::new(ErrorCode::Internal, format!(
            "the install ended, but kiln/.install.json says Kiln {} ({}), not {version} ({})",
            on_disk.runtime.version,
            on_disk.runtime.components.join(", "),
            receipt.runtime.components.join(", ")
        )));
    }
    let wrong = install::modified_files(&g.path, &on_disk);
    if !wrong.is_empty() {
        return Err(Error::new(ErrorCode::HashMismatch, format!(
            "Kiln {version} is not fully in the game folder: {} file(s) differ from the install, e.g. {}",
            wrong.len(),
            wrong.iter().take(3).cloned().collect::<Vec<_>>().join(", ")
        ))
        .with_detail(serde_json::json!({ "files": wrong })));
    }
    p.report(Some(1.0), &format!("Kiln {version} is in the game folder ({} files, {})", on_disk.files.len(), on_disk.runtime.components.join(", ")));
    Ok(())
}

#[tauri::command]
pub async fn runtime_install(app: AppHandle, game_id: String, version: String, options: InstallOptions) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let b = blocking({ let v = version.clone(); move || bundle::find(&dirs()?, &v) }).await?;
    // parts this side cannot take fail now, before the bundle's verify (a minute's work on a slow disk). None: the
    // installer keeps the player's parts and adds new or newly default ones not turned off (resolve_components).
    if let Some(parts) = options.components.clone() {
        let (file, side) = (b.file.clone(), side_of(&g));
        blocking(move || install::select_components(&bundle::read(&file)?, side, Some(&parts)).map(|_| ())).await?;
    }
    let title = format!("Install Kiln {}", b.kiln);
    let a = app.clone();
    Ok(spawn_task(&app, "runtime_install", title, move |p| {
        crate::state::refuse_running(&g)?;
        let d = dirs()?;
        let mut options = options;
        options.installer = options.installer.or_else(installer);
        let r = install::install_report(&d, &g, &b.file, &options, p);
        after_change(&a, &game_id);
        let (r, report) = r?;
        check_applied(&g, &b.version, &r, p)?;
        install_result(&r, &report)
    }))
}

#[tauri::command]
pub async fn runtime_uninstall(app: AppHandle, game_id: String) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let a = app.clone();
    Ok(spawn_task(&app, "runtime_uninstall", "Uninstall Kiln".into(), move |p| {
        crate::state::refuse_running(&g)?;
        let r = install::uninstall_report(&dirs()?, &g, p);
        after_change(&a, &game_id);
        let r = r?;
        // the result stays null (contract); the summary is the task's last message
        let mut msg = format!("removed {} files", r.removed);
        if !r.restored.is_empty() {
            msg.push_str(&format!("; restored {}", r.restored.join(", ")));
        }
        if !r.kept.is_empty() {
            msg.push_str(&format!("; kept {} changed files", r.kept.len()));
        }
        p.report(Some(1.0), &msg);
        Ok(serde_json::Value::Null)
    }))
}

#[tauri::command]
pub async fn runtime_rollback(app: AppHandle, game_id: String, version: String) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let a = app.clone();
    Ok(spawn_task(&app, "runtime_rollback", format!("Roll back to {version}"), move |p| {
        crate::state::refuse_running(&g)?;
        // install::rollback, with the launcher as the receipt's installer
        let d = dirs()?;
        let b = bundle::find(&d, &version)?;
        // the player's parts carry over by install::resolve_components (an older bundle without some of them leaves
        // them out instead of failing; their opt-outs stay recorded)
        let options = InstallOptions { components: None, switch_from_shroudforge: false, installer: installer() };
        let r = install::install_report(&d, &g, &b.file, &options, p);
        after_change(&a, &game_id);
        let (r, report) = r?;
        check_applied(&g, &b.version, &r, p)?;
        install_result(&r, &report)
    }))
}

#[tauri::command]
pub async fn runtime_classify(path: String) -> Result<ProxyClass, Error> {
    blocking(move || pe::classify_proxy(std::path::Path::new(&path))).await
}
