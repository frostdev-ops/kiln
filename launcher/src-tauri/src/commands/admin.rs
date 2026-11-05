//! Tauri commands: admin. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! One [`AdminHub`] for the process (one session per server, reconnects with backoff, re-subscribes); its events go
//! out as `kiln://admin` `{session, serverId, event}`. Blocking work runs on `spawn_blocking`.

use crate::events::{AdminEventPayload, ADMIN};
use kiln_manage::admin::client::{AdminClient, AdminSession, Transport};
use kiln_manage::admin::hub::AdminHub;
use crate::state::blocking;
use kiln_manage::Error;
use std::sync::OnceLock;
use tauri::{AppHandle, Emitter};

static HUB: OnceLock<AdminHub> = OnceLock::new();

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
