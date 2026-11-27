//! Tauri commands: admin. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! One [`AdminHub`] for the process (one session per server, reconnects with backoff, re-subscribes); its events go
//! out as `kiln://admin` `{session, serverId, event}`. Blocking work runs on `spawn_blocking`.

use crate::events::{AdminEventPayload, ADMIN};
use kiln_manage::admin::client::{AdminClient, AdminSession, Transport};
use kiln_manage::admin::hub::AdminHub;
use kiln_manage::admin::web_panel::settings::{self as web_panel_settings, WebPanelSettings};
use kiln_manage::admin::web_panel::{Route, WebPanelInfo, WebPanels};
use crate::state::blocking;
use kiln_manage::Error;
use std::sync::{Mutex, OnceLock};
use tauri::{AppHandle, Emitter};

static HUB: OnceLock<AdminHub> = OnceLock::new();
static WEB_PANELS: OnceLock<Mutex<WebPanels>> = OnceLock::new();

/// The open web panels (one gateway per server, kiln_manage::admin::web_panel); their origins are kept in the data
/// folder's web-panel-origins.json.
fn web_panels() -> Result<&'static Mutex<WebPanels>, Error> {
    if let Some(p) = WEB_PANELS.get() {
        return Ok(p);
    }
    let file = crate::state::dirs()?.root.join(kiln_manage::admin::web_panel::origins::FILE);
    Ok(WEB_PANELS.get_or_init(|| Mutex::new(WebPanels::new(file))))
}

/// At exit: every gateway and its ssh connections go.
pub fn close_web_panel_tunnels() {
    if let Some(panels) = WEB_PANELS.get() {
        panels.lock().unwrap_or_else(|p| p.into_inner()).close_all();
    }
}

/// Opens the server's web panel in the browser (0.2.0-dev.31.12): through its own gateway on its own loopback
/// origin, signed in with a one-time login that only a one-use bootstrap address hands over (never a command line).
/// (0.2.0-dev.31.19) `route` "auto" (the default) opens the panel's address on the internet when its
/// launcher-endpoint.json names one and the check passes; "local" always opens it on this computer.
#[tauri::command]
pub async fn admin_open_web_panel(server_id: String, route: Option<Route>) -> Result<WebPanelInfo, Error> {
    blocking(move || {
        let server = kiln_manage::server::instance::get(&server_id)?;
        let opened = web_panels()?.lock().unwrap_or_else(|p| p.into_inner()).open_via(&server, route.unwrap_or_default())?;
        crate::state::open_external(std::ffi::OsStr::new(&opened.url))?;
        Ok(opened.info)
    }).await
}

/// Closes the server's web panel gateway (its tabs lose the connection); whether one was open.
#[tauri::command]
pub async fn admin_close_web_panel(server_id: String) -> Result<bool, Error> {
    blocking(move || Ok(web_panels()?.lock().unwrap_or_else(|p| p.into_inner()).close(&server_id))).await
}

/// The web panels open now (gateways that closed by themselves are gone).
#[tauri::command]
pub async fn admin_web_panels() -> Result<Vec<WebPanelInfo>, Error> {
    blocking(move || Ok(web_panels()?.lock().unwrap_or_else(|p| p.into_inner()).list())).await
}

/// (0.2.0-dev.31.20) The data folder's web-panel-settings.json, beside web-panel-origins.json (where [`WebPanels`]
/// reads it). The commands below don't take the web panels' lock, so a change never waits for an open's check.
fn web_panel_settings_file() -> Result<std::path::PathBuf, Error> {
    Ok(crate::state::dirs()?.root.join(web_panel_settings::FILE))
}

/// (0.2.0-dev.31.20) The server's web panel settings: "Always open the web panel on this computer" (`alwaysLocal`).
#[tauri::command]
pub async fn admin_web_panel_settings(server_id: String) -> Result<WebPanelSettings, Error> {
    blocking(move || Ok(web_panel_settings::get(&web_panel_settings_file()?, &server_id))).await
}

/// (0.2.0-dev.31.20) Saves the server's web panel settings (a known server's only); what they are now.
#[tauri::command]
pub async fn admin_web_panel_settings_set(server_id: String, settings: WebPanelSettings) -> Result<WebPanelSettings, Error> {
    blocking(move || {
        kiln_manage::server::instance::get(&server_id)?;
        web_panel_settings::set(&web_panel_settings_file()?, &server_id, settings)
    })
    .await
}

/// The process-wide hub; the first caller's AppHandle receives the events.
pub fn hub(app: &AppHandle) -> &'static AdminHub {
    HUB.get_or_init(|| {
        let app = app.clone();
        AdminHub::new(move |e| {
            let _ = app.emit(ADMIN, AdminEventPayload { session: e.session, server_id: e.server_id, event: e.event });
        })
    })
}

#[tauri::command]
pub async fn admin_connect(app: AppHandle, server_id: String, transport: Transport) -> Result<AdminSession, Error> {
    blocking(move || {
        let instance = kiln_manage::server::instance::get(&server_id)?;
        hub(&app).connect(&server_id, Box::new(move || AdminClient::connect(&instance, transport)))
    })
    .await
}

#[tauri::command]
pub async fn admin_disconnect(app: AppHandle, session: String) -> Result<(), Error> {
    blocking(move || hub(&app).disconnect(&session)).await
}

#[tauri::command]
pub async fn admin_sessions(app: AppHandle) -> Result<Vec<AdminSession>, Error> {
    Ok(hub(&app).sessions())
}

#[tauri::command]
pub async fn admin_call(app: AppHandle, session: String, op: String, args: serde_json::Map<String, serde_json::Value>) -> Result<serde_json::Value, Error> {
    blocking(move || hub(&app).call(&session, &op, args)).await
}

#[tauri::command]
pub async fn admin_subscribe(app: AppHandle, session: String, topic: String) -> Result<(), Error> {
    blocking(move || hub(&app).subscribe(&session, &topic)).await
}

#[tauri::command]
pub async fn admin_unsubscribe(app: AppHandle, session: String, topic: String) -> Result<(), Error> {
    blocking(move || hub(&app).unsubscribe(&session, &topic)).await
}
