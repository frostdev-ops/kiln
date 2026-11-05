//! Tauri commands: runtime. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Bundles on kiln_manage::bundle, install/uninstall/rollback on kiln_manage::install, run as tasks (spawn_task).

use super::TaskId;
use super::app::{spawn_task, to_value};
use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::bundle::{self, RuntimeBundle};
use kiln_manage::install::{self, InstallOptions, InstallState};
use kiln_manage::pe::{self, ProxyClass};
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

fn after_change(app: &AppHandle, game_id: &str) {
    crate::state::forget_install_state(game_id);
    crate::watch::emit_status(app, game_id);
}

#[tauri::command]
pub async fn runtime_install(app: AppHandle, game_id: String, version: String, options: InstallOptions) -> Result<TaskId, Error> {
    let g = blocking({ let id = game_id.clone(); move || crate::state::game(&id) }).await?;
    let b = blocking({ let v = version.clone(); move || bundle::find(&dirs()?, &v) }).await?;
    let title = format!("Install Kiln {}", b.kiln);
    let a = app.clone();
    Ok(spawn_task(&app, "runtime_install", title, move |p| {
        crate::state::refuse_running(&g)?;
        let d = dirs()?;
        let mut options = options;
        options.installer = options.installer.or_else(installer);
        let r = install::install(&d, &g, &b.file, &options, p);
        after_change(&a, &game_id);
        to_value(r?)
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
        let options = InstallOptions {
            components: install::read_receipt(&g.path)?.map(|r| r.runtime.components),
            switch_from_shroudforge: false,
            installer: installer(),
        };
        let r = install::install(&d, &g, &b.file, &options, p);
        after_change(&a, &game_id);
        to_value(r?)
    }))
}

#[tauri::command]
pub async fn runtime_classify(path: String) -> Result<ProxyClass, Error> {
    blocking(move || pe::classify_proxy(std::path::Path::new(&path))).await
}
