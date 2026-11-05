//! Tauri commands: config. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Offline configs on kiln_manage::config (value-token edits, comments kept). Plus `config_kiln_*`:
//! Kiln's own `<game>/kiln/kiln.json`, edited the same way against a built-in schema snapshot.

use crate::state::{blocking, dirs};
use kiln_manage::config::{self, ConfigDocument, ConfigFileInfo, ConfigTarget, NativeModSwitch, NativeUserState, SchemaSnapshot};
use kiln_manage::{Error, ErrorCode};
use std::path::PathBuf;

/// The folder a target names: a profile's `config/`, a game's `kiln/config`, a server's `kiln/config`.
pub fn config_dir(target: &ConfigTarget) -> Result<PathBuf, Error> {
    match target {
        ConfigTarget::Profile(id) => {
            let d = dirs()?;
            if !d.profile(id).join("meta.json").is_file() {
                return Err(Error::not_found(format!("No profile '{id}'.")));
            }
            Ok(d.profile(id).join("config"))
        }
        ConfigTarget::Game(id) => Ok(crate::state::game(id)?.path.join(kiln_manage::paths::game::CONFIG)),
        ConfigTarget::Server(id) => {
            let s = kiln_manage::server::instance::get(id)?;
            let dir = s.dir.ok_or_else(|| Error::new(ErrorCode::Unsupported, "Remote servers edit configs through the admin pages."))?;
            Ok(dir.join(kiln_manage::paths::game::CONFIG))
        }
    }
}

#[tauri::command]
pub async fn config_list(target: ConfigTarget) -> Result<Vec<ConfigFileInfo>, Error> {
    blocking(move || {
        let dir = config_dir(&target)?;
        if !dir.is_dir() {
            return Ok(vec![]);
        }
        config::list(&dir)
    })
    .await
}

#[tauri::command]
pub async fn config_read(target: ConfigTarget, file: String) -> Result<ConfigDocument, Error> {
    blocking(move || config::read(&config_dir(&target)?, &file)).await
}

#[tauri::command]
pub async fn config_set(target: ConfigTarget, file: String, path: String, value: serde_json::Value) -> Result<ConfigDocument, Error> {
    blocking(move || config::set(&config_dir(&target)?, &file, &path, &value)).await
}

#[tauri::command]
pub async fn config_write(target: ConfigTarget, file: String, text: String) -> Result<ConfigDocument, Error> {
    blocking(move || config::write(&config_dir(&target)?, &file, &text)).await
}

#[tauri::command]
pub async fn config_reset(target: ConfigTarget, file: String, path: Option<String>) -> Result<ConfigDocument, Error> {
    blocking(move || config::reset(&config_dir(&target)?, &file, path.as_deref())).await
}

#[tauri::command]
pub async fn config_native_state(target: ConfigTarget) -> Result<NativeUserState, Error> {
    blocking(move || config::read_native_state(&config_dir(&target)?)).await
}

#[tauri::command]
pub async fn config_native_set(target: ConfigTarget, mod_id: String, enabled: Option<bool>) -> Result<NativeUserState, Error> {
    blocking(move || {
        let dir = config_dir(&target)?;
        let mut st = config::read_native_state(&dir)?;
        st.format = 1;
        match enabled {
            None => {
                st.mods.remove(&mod_id);
            }
            Some(on) => {
                st.mods.insert(mod_id, NativeModSwitch { enabled: Some(on) });
            }
        }
        config::write_native_state(&dir, &st)?;
        Ok(st)
    })
    .await
}

// ---------------------------------------------------------------- kiln.json

const KILN_SNAPSHOT: &str = include_str!("../../../src/data/kiln-json.snapshot.json");

pub fn kiln_snapshot() -> SchemaSnapshot {
    serde_json::from_str(KILN_SNAPSHOT).expect("built-in kiln.json snapshot")
}

fn kiln_json_path(game_id: &str) -> Result<PathBuf, Error> {
    let g = crate::state::game(game_id)?;
    if !g.path.join(kiln_manage::paths::game::KILN).is_dir() {
        return Err(Error::not_found("Kiln is not installed in this game."));
    }
    Ok(g.path.join(kiln_manage::paths::game::KILN).join("kiln.json"))
}

fn kiln_doc(path: &std::path::Path) -> Result<ConfigDocument, Error> {
    let text = match std::fs::read_to_string(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => String::new(),
        Err(e) => return Err(Error::from(e).with_detail(path.display().to_string())),
    };
    let values = if text.trim().is_empty() { serde_json::json!({}) } else { kiln_manage::jsonc::parse(&text)? };
    let schema = kiln_snapshot();
    let errors = config::validate(&schema, &values);
    Ok(ConfigDocument { file: "kiln.json".into(), text, values, schema: Some(schema), errors })
}

pub fn kiln_set(path: &std::path::Path, key: &str, value: Option<&serde_json::Value>) -> Result<ConfigDocument, Error> {
    let schema = kiln_snapshot();
    let entry = schema.entries.iter().find(|e| e.path == key).ok_or_else(|| Error::invalid(format!("kiln.json has no setting {key}")))?;
    let text = std::fs::read_to_string(path).unwrap_or_default();
    let text = if text.trim().is_empty() { "{\n}\n".to_string() } else { text };
    let segs = kiln_manage::jsonc::split_path(key);
    let next = match value {
        Some(v) => {
            let v = config::check_value(entry, v).map_err(|m| Error::new(ErrorCode::Schema, format!("{key}: {m}")))?;
            kiln_manage::jsonc::set_value(&text, &segs, &v)?
        }
        None => kiln_manage::jsonc::remove_value(&text, &segs)?,
    };
    kiln_manage::util::write_atomic(path, next.as_bytes())?;
    kiln_doc(path)
}

#[tauri::command]
pub async fn config_kiln_read(game_id: String) -> Result<ConfigDocument, Error> {
    blocking(move || kiln_doc(&kiln_json_path(&game_id)?)).await
}

#[tauri::command]
pub async fn config_kiln_set(game_id: String, path: String, value: serde_json::Value) -> Result<ConfigDocument, Error> {
    blocking(move || kiln_set(&kiln_json_path(&game_id)?, &path, Some(&value))).await
}

#[tauri::command]
pub async fn config_kiln_reset(game_id: String, path: String) -> Result<ConfigDocument, Error> {
    blocking(move || kiln_set(&kiln_json_path(&game_id)?, &path, None)).await
}
