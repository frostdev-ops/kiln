//! (0.2.0-dev.31.20) Each server's web panel settings, on the launcher's side: `web-panel-settings.json` in the
//! launcher's data folder, beside [`super::origins::FILE`] (`{"format": 1, "servers": {"<server id>": {...}}}`; a server
//! with every setting at its default has no entry).
//!
//! - `alwaysLocal`, "Always open the web panel on this computer" (the server's Overview, under Open web panel): Open web
//!   panel never checks or uses the panel's address on the internet (Kilnwarden's `launcher-endpoint.json` `public`,
//!   hosted access through frostdev.io's relay or an own address) and always opens it through this computer's gateway,
//!   for local and remote (ssh) servers alike. Off by default.
//!
//! A file that is there but can't be read (hand-edited, or a newer format) counts as `alwaysLocal` for every server,
//! so a setting that was on never turns into a check of the public address by accident. A change never replaces such
//! a file: that would turn every other server's setting off (and drop a newer launcher's file), so it is refused and
//! says which file to fix or delete. An entry stays when its server is removed (a new server with the same id starts
//! with the old setting, the more private one). Reads and writes of a change hold `web-panel-settings.json.lock`, as
//! origins does.

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::Path;

pub const FILE: &str = "web-panel-settings.json";

/// One server's web panel settings (contracts.md `WebPanelSettings`).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct WebPanelSettings {
    /// "Always open the web panel on this computer": never the panel's address on the internet.
    pub always_local: bool,
}

#[derive(Debug, Default, Serialize, Deserialize)]
struct SettingsFile {
    format: u32,
    servers: BTreeMap<String, WebPanelSettings>,
}

/// The file's servers (none when there is no file); an error when it is there but can't be read (or is not format 1).
fn read(path: &Path) -> Result<SettingsFile> {
    let b = match std::fs::read(path) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(SettingsFile { format: 1, servers: BTreeMap::new() }),
        Err(e) => return Err(crate::util::io_at(path, "reading")(e)),
    };
    match serde_json::from_slice::<SettingsFile>(&b) {
        Ok(f) if f.format == 1 => Ok(f),
        Ok(f) => Err(Error::new(ErrorCode::Parse, format!("{} is format {}, which this launcher doesn't know", path.display(), f.format))),
        Err(e) => Err(Error::new(ErrorCode::Parse, format!("{}: {e}", path.display()))),
    }
}

/// The settings of `server_id` (the defaults when it has none; see the module's note on an unreadable file).
pub fn get(path: &Path, server_id: &str) -> WebPanelSettings {
    match read(path) {
        Ok(f) => f.servers.get(server_id).copied().unwrap_or_default(),
        Err(_) => WebPanelSettings { always_local: true },
    }
}

/// Saves the settings of `server_id`; what they are now. A file that is there but can't be read is left as it is and
/// the change refused (every server's panel keeps opening on this computer until it is fixed or deleted).
pub fn set(path: &Path, server_id: &str, s: WebPanelSettings) -> Result<WebPanelSettings> {
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let _held = lock(path)?;
    let mut f = read(path).map_err(|e| {
        Error::new(e.code, format!("{FILE} in the launcher's data folder can't be read, so every server's web panel opens on this computer: fix or delete it to change this setting."))
            .with_detail(serde_json::json!({ "path": path.display().to_string(), "reason": e.message }))
    })?;
    if s == WebPanelSettings::default() {
        f.servers.remove(server_id);
    } else {
        f.servers.insert(server_id.to_string(), s);
    }
    crate::util::write_json_atomic(path, &f)?;
    Ok(s)
}

fn lock(path: &Path) -> Result<crate::state::HeldLock> {
    let mut l = path.as_os_str().to_owned();
    l.push(".lock");
    crate::state::lock_file(Path::new(&l), std::time::Duration::from_secs(10), &|| false, "another Kiln Launcher is writing web-panel-settings.json")
}
