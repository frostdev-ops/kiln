//! Tauri commands: the Steam Deck UI (contracts.md section 9; docs/kiln/launcher/deck.md). The work is in
//! crate::deck; detection reaches the frontend through `app_info.deck`.

use crate::deck::{self, browse, gamepad};
use kiln_manage::{Error, ErrorCode};
use tauri::{AppHandle, Emitter, Manager};

/// One folder for Add mods: the mods the library recognises in it, its sub-folders and the places to start from.
/// `dir` null: Downloads (Home without one).
#[tauri::command]
pub async fn deck_browse(dir: Option<String>) -> Result<browse::Folder, Error> {
    crate::state::blocking(move || browse::browse(dir.as_deref().map(std::path::Path::new), &browse::Env::current())).await
}

/// Starts (`on`) or stops the native gamepad reader (`kiln://gamepad`); returns what it sees. Not Linux: `native`
/// false, the frontend reads the browser's Gamepad API.
#[tauri::command]
pub async fn deck_gamepad(app: AppHandle, on: bool) -> Result<gamepad::GamepadStatus, Error> {
    if !on {
        return Ok(gamepad::stop());
    }
    let st = gamepad::start(move |e| {
        let _ = app.emit(deck::GAMEPAD_EVENT, e);
    });
    // the first scan runs on the reader thread: give it a moment so the answer names the controllers
    if st.native && st.devices.is_empty() {
        tokio_sleep(150).await;
        return Ok(gamepad::status());
    }
    Ok(st)
}

async fn tokio_sleep(ms: u64) {
    let _ = tauri::async_runtime::spawn_blocking(move || std::thread::sleep(std::time::Duration::from_millis(ms))).await;
}

/// The main window fullscreen (the Deck UI) or not; returns the new state.
#[tauri::command]
pub async fn deck_fullscreen(app: AppHandle, on: bool) -> Result<bool, Error> {
    let w = app.get_webview_window("main").ok_or_else(|| Error::new(ErrorCode::Internal, "no main window"))?;
    w.set_fullscreen(on).map_err(|e| Error::new(ErrorCode::Unsupported, format!("fullscreen: {e}")))?;
    Ok(w.is_fullscreen().unwrap_or(on))
}

/// Quits the launcher (the Deck UI's Quit: Game Mode has no window buttons).
#[tauri::command]
pub async fn deck_quit(app: AppHandle) -> Result<(), Error> {
    gamepad::stop();
    app.exit(0);
    Ok(())
}
