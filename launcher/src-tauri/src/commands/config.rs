//! Tauri commands: config. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Offline configs on kiln_manage::config (value-token edits, comments kept). Plus `config_kiln_*`:
//! Kiln's own `<game>/kiln/kiln.json`, edited the same way against a built-in schema snapshot.

use crate::state::{blocking, dirs};
use kiln_manage::config::{self, ConfigDocument, ConfigFileInfo, ConfigTarget, NativeModSwitch, NativeUserState, SchemaSnapshot};
use kiln_manage::mod_settings::SettingsRemoval;
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

/// A profile's list marks the files of mods that are not installed anywhere (`orphaned`: kiln_manage::config_orphans);
/// a server's and a game's list says nothing of that (it is false there).
#[tauri::command]
pub async fn config_list(target: ConfigTarget) -> Result<Vec<ConfigFileInfo>, Error> {
    blocking(move || {
        let dir = config_dir(&target)?;
        if !dir.is_dir() {
            return Ok(vec![]);
        }
        match &target {
            ConfigTarget::Profile(id) => kiln_manage::config_orphans::list_profile(&dirs()?, id),
            _ => config::list(&dir),
        }
    })
    .await
}

/// The Configs window's "Clear settings" (and "Clear all"): the files `config_list` marked `orphaned` go into
/// `<data>/backups/mod-settings/config-<profile>-<time>/` with their schema snapshots (kiln_manage::mod_settings::clear_orphans).
/// A profile's folder only. Refused whole (conflict) for a file whose mod is installed now, in the profile on or off, in the
/// Library or in a game folder, or whose mod is not known; `game_running` while a game runs.
#[tauri::command]
pub async fn config_clear_orphans(target: ConfigTarget, files: Vec<String>) -> Result<SettingsRemoval, Error> {
    blocking(move || {
        let ConfigTarget::Profile(id) = &target else {
            return Err(Error::new(ErrorCode::Unsupported, "Only a profile's configs can be cleared here."));
        };
        let r = kiln_manage::mod_settings::clear_orphans(&dirs()?, id, &files)?;
        if !r.removed.is_empty() {
            log_setting(&clear_line(&target, &files, r.backup.as_deref()));
        }
        for n in &r.notes {
            log_setting(n);
        }
        Ok(r)
    })
    .await
}

#[tauri::command]
pub async fn config_read(target: ConfigTarget, file: String) -> Result<ConfigDocument, Error> {
    blocking(move || config::read(&config_dir(&target)?, &file)).await
}

#[tauri::command]
pub async fn config_set(target: ConfigTarget, file: String, path: String, value: serde_json::Value) -> Result<ConfigDocument, Error> {
    blocking(move || {
        let doc = config::set(&config_dir(&target)?, &file, &path, &value)?;
        log_setting(&config_line(&target, &file, Some(&path), "changed"));
        Ok(doc)
    })
    .await
}

#[tauri::command]
pub async fn config_write(target: ConfigTarget, file: String, text: String) -> Result<ConfigDocument, Error> {
    blocking(move || {
        let doc = config::write(&config_dir(&target)?, &file, &text)?;
        log_setting(&config_line(&target, &file, None, "written as a whole (the file editor)"));
        Ok(doc)
    })
    .await
}

#[tauri::command]
pub async fn config_reset(target: ConfigTarget, file: String, path: Option<String>) -> Result<ConfigDocument, Error> {
    blocking(move || {
        let doc = config::reset(&config_dir(&target)?, &file, path.as_deref())?;
        log_setting(&config_line(&target, &file, path.as_deref(), "back to its default"));
        Ok(doc)
    })
    .await
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
        let id = mod_id;
        match enabled {
            None => {
                st.mods.remove(&id);
            }
            Some(on) => {
                st.mods.insert(id.clone(), NativeModSwitch { enabled: Some(on) });
            }
        }
        config::write_native_state(&dir, &st)?;
        log_setting(&format!("{}: native mod {} {}", target_name(&target), one_line(&id), match enabled {
            Some(true) => "switched on",
            Some(false) => "switched off",
            None => "back to the profile's switch",
        }));
        Ok(st)
    })
    .await
}

// ---------------------------------------------------------------- launcher.log lines for changed settings
//
// A player's report of 13 Nov 2026: "Use my installed ShroudForge" (kiln.json shroudforge.useInstalled) was off in one
// report and on in the next, and nothing in either said when it was switched or that the launcher did it. A changed
// setting now leaves a line. kiln.json's values are switches, numbers and fixed words (the schema snapshot has no free
// text), so its line carries the value; a mod's config file may hold a password or a token: its line names the key only.

fn log_setting(line: &str) {
    crate::commands::app::launcher_log("settings", line);
}

/// A name as one piece of a log line: a key of a config file without a schema, a file name and a mod's id are what
/// the caller sent, and a line break in one would start a line of its own in the log (review of 6a78171d). Control
/// characters become spaces, and a long name is cut.
fn one_line(s: &str) -> String {
    let mut out: String = s.chars().take(120).map(|c| if c.is_control() { ' ' } else { c }).collect();
    if s.chars().count() > 120 {
        out.push_str("...");
    }
    out
}

fn target_name(target: &ConfigTarget) -> String {
    match target {
        ConfigTarget::Profile(id) => format!("profile {}", one_line(id)),
        ConfigTarget::Game(id) => format!("game {}", one_line(id)),
        ConfigTarget::Server(id) => format!("server {}", one_line(id)),
    }
}

/// `profile test: mods/sky.json: weather.hold changed` (never the value).
pub fn config_line(target: &ConfigTarget, file: &str, key: Option<&str>, what: &str) -> String {
    match key {
        Some(k) => format!("{}: {}: {} {what}", target_name(target), one_line(file), one_line(k)),
        None => format!("{}: {}: every setting {what}", target_name(target), one_line(file)),
    }
}

/// `profile work: cleared the settings of mods that are not installed: a.json, emf/b.ini (a copy is in <folder>)`: the
/// Configs window's Clear (never a value; the names go through [`one_line`]).
pub fn clear_line(target: &ConfigTarget, files: &[String], backup: Option<&str>) -> String {
    let names: Vec<String> = files.iter().map(|f| one_line(f)).collect();
    let copy = match backup {
        Some(b) => format!("a copy is in {}", one_line(b)),
        None => "there was nothing to copy".to_string(),
    };
    format!("{}: cleared the settings of mods that are not installed: {} ({copy})", target_name(target), names.join(", "))
}

/// `kiln.json of steam-client: shroudforge.useInstalled = true`. Called after the value passed the schema.
pub fn kiln_setting_line(game_id: &str, key: &str, value: Option<&serde_json::Value>) -> String {
    let v = match value {
        None => "back to its default".to_string(),
        Some(serde_json::Value::Bool(b)) => format!("= {b}"),
        Some(serde_json::Value::Number(n)) => format!("= {n}"),
        Some(serde_json::Value::String(s)) if s.len() <= 32 && s.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == '_') => format!("= {s}"),
        Some(_) => "changed".to_string(),
    };
    format!("kiln.json of {}: {} {v}", one_line(game_id), one_line(key))
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
    blocking(move || {
        let doc = kiln_set(&kiln_json_path(&game_id)?, &path, Some(&value))?;
        log_setting(&kiln_setting_line(&game_id, &path, Some(&value)));
        Ok(doc)
    })
    .await
}

#[tauri::command]
pub async fn config_kiln_reset(game_id: String, path: String) -> Result<ConfigDocument, Error> {
    blocking(move || {
        let doc = kiln_set(&kiln_json_path(&game_id)?, &path, None)?;
        log_setting(&kiln_setting_line(&game_id, &path, None));
        Ok(doc)
    })
    .await
}

#[cfg(test)]
mod setting_line_tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn a_changed_setting_leaves_a_line_and_a_mods_value_is_never_in_it() {
        assert_eq!(kiln_setting_line("steam-client", "shroudforge.useInstalled", Some(&json!(true))),
            "kiln.json of steam-client: shroudforge.useInstalled = true");
        assert_eq!(kiln_setting_line("g", "mods.fault_window_s", Some(&json!(2.5))), "kiln.json of g: mods.fault_window_s = 2.5");
        assert_eq!(kiln_setting_line("g", "policy", Some(&json!("verified-only"))), "kiln.json of g: policy = verified-only");
        assert_eq!(kiln_setting_line("g", "policy", None), "kiln.json of g: policy back to its default");
        // not a fixed word: a line break or a path would let a value write its own log lines
        assert_eq!(kiln_setting_line("g", "x", Some(&json!("a\n2026 settings: forged"))), "kiln.json of g: x changed");
        assert_eq!(kiln_setting_line("g", "x", Some(&json!({"a": 1}))), "kiln.json of g: x changed");
        let t = ConfigTarget::Profile("test".into());
        assert_eq!(config_line(&t, "mods/sky.json", Some("server.password"), "changed"), "profile test: mods/sky.json: server.password changed");
        assert_eq!(config_line(&ConfigTarget::Game("g".into()), "a.json", None, "back to its default"), "game g: a.json: every setting back to its default");
        // a key, a file name or an id with a line break stays on its line (review of 6a78171d)
        let forged = "x\n2026-11-13T00:00:00Z settings: forged";
        for line in [config_line(&ConfigTarget::Profile(forged.into()), forged, Some(forged), "changed"),
            config_line(&t, forged, None, "changed"), kiln_setting_line(forged, forged, Some(&json!(true)))] {
            assert!(!line.contains('\n') && line.contains("x 2026-11-13T00:00:00Z settings: forged"), "{line}");
        }
        assert_eq!(one_line(&"k".repeat(300)).chars().count(), 123);
    }

    #[test]
    fn clearing_the_settings_of_mods_that_are_gone_leaves_a_line_with_the_files_and_the_copy() {
        let t = ConfigTarget::Profile("work".into());
        let files = vec!["expeditions.json".to_string(), "emf/beastmaster.ini".to_string()];
        assert_eq!(clear_line(&t, &files, Some("/data/backups/mod-settings/config-work-20261128T180000Z")),
            "profile work: cleared the settings of mods that are not installed: expeditions.json, emf/beastmaster.ini (a copy is in /data/backups/mod-settings/config-work-20261128T180000Z)");
        assert_eq!(clear_line(&t, &files[..1], None), "profile work: cleared the settings of mods that are not installed: expeditions.json (there was nothing to copy)");
        // a name with a line break stays on its line
        let line = clear_line(&t, &["x\n2026-11-13T00:00:00Z settings: forged.json".to_string()], None);
        assert!(!line.contains('\n') && line.contains("x 2026-11-13T00:00:00Z settings: forged.json"), "{line}");
    }
}

#[cfg(test)]
mod kiln_graphics_tests {
    use super::*;
    use serde_json::json;

    /// (dev.29.18) kiln.json "graphics" (Settings, Graphics): the snapshot holds the keys the game reads, kiln_set
    /// writes them into a commented kiln.json and refuses values out of range.
    #[test]
    fn the_graphics_section_is_set_and_checked() {
        let schema = kiln_snapshot();
        let keys: Vec<&str> = schema.entries.iter().map(|e| e.path.as_str()).filter(|p| p.starts_with("graphics.")).collect();
        assert_eq!(keys, ["graphics.fpsCap", "graphics.dlssPreset", "graphics.dlssScale", "graphics.dlssRenderScale",
            "graphics.dlssQualityScale", "graphics.dlssBalancedScale", "graphics.dlssPerformanceScale",
            "graphics.dlssUltraPerformanceScale", "graphics.readout", "graphics.upscaler", "graphics.amdUpscaler"]);
        let dir = std::env::temp_dir().join(format!("kiln-launcher-graphics-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("kiln.json");
        std::fs::write(&path, "{\n  // keep me\n  \"policy\": \"verified-only\"\n}\n").unwrap();
        let doc = kiln_set(&path, "graphics.fpsCap", Some(&json!(144))).unwrap();
        assert_eq!(doc.values["graphics"]["fpsCap"], 144);
        let doc = kiln_set(&path, "graphics.dlssPreset", Some(&json!("K"))).unwrap();
        assert_eq!(doc.values["graphics"]["dlssPreset"], "K");
        let doc = kiln_set(&path, "graphics.dlssScale", Some(&json!("perMode"))).unwrap();
        assert_eq!(doc.values["graphics"]["dlssScale"], "perMode");
        let doc = kiln_set(&path, "graphics.dlssQualityScale", Some(&json!(0.75))).unwrap();
        assert_eq!(doc.values["graphics"]["dlssQualityScale"], 0.75);
        let doc = kiln_set(&path, "graphics.upscaler", Some(&json!("amd"))).unwrap();
        assert_eq!(doc.values["graphics"]["upscaler"], "amd");
        let doc = kiln_set(&path, "graphics.amdUpscaler", Some(&json!("fsr3"))).unwrap();
        assert_eq!(doc.values["graphics"]["amdUpscaler"], "fsr3");
        // (0.2.0-dev.29.20) "fsr4": FSR 4 as an explicit, experimental choice; "auto" runs FSR 3.1
        let doc = kiln_set(&path, "graphics.amdUpscaler", Some(&json!("fsr4"))).unwrap();
        assert_eq!(doc.values["graphics"]["amdUpscaler"], "fsr4");
        let doc = kiln_set(&path, "graphics.readout", Some(&json!(true))).unwrap();
        assert!(doc.errors.is_empty(), "{:?}", doc.errors);
        assert!(doc.text.contains("// keep me"));
        for (key, bad) in [("graphics.fpsCap", json!(501)), ("graphics.fpsCap", json!(1.5)), ("graphics.dlssPreset", json!("N")),
            ("graphics.dlssRenderScale", json!(0.2)), ("graphics.dlssRenderScale", json!(1.1)), ("graphics.readout", json!(1)),
            ("graphics.upscaler", json!("dlss")), ("graphics.upscaler", json!(7)), ("graphics.amdUpscaler", json!("fsr5"))] {
            assert!(kiln_set(&path, key, Some(&bad)).is_err(), "{key} = {bad}");
        }
        let doc = kiln_set(&path, "graphics.fpsCap", None).unwrap();
        assert!(doc.values["graphics"].get("fpsCap").is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// (dev.29.18) kiln.json "ui": {"overlay": "off"} (Settings, the kiln.json card): Kiln's overlay drawing off, as
    /// KILN_UI_OVERLAY=0 (native/services/ui/overlay_setting.h reads it); "auto" by default. (0.2.0-dev.31.25) "on": drawn
    /// anyway, as KILN_UI_OVERLAY=1 (Settings, Graphics and the Play page: "Draw Kiln's overlay anyway" writes it, and
    /// switching that off removes the key); nothing else is taken.
    #[test]
    fn the_overlay_switch_is_set_and_checked() {
        let schema = kiln_snapshot();
        let e = schema.entries.iter().find(|e| e.path == "ui.overlay").expect("ui.overlay in the snapshot");
        assert_eq!(e.default, json!("auto"));
        assert_eq!(serde_json::to_value(&e.options).unwrap(), json!(["auto", "off", "on"]));
        assert!(e.requires_restart, "kiln.ui reads it at the game's start");
        let dir = std::env::temp_dir().join(format!("kiln-launcher-overlay-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("kiln.json");
        std::fs::write(&path, "{\n  // keep me\n  \"ui\": {\"look\": \"game\"}\n}\n").unwrap();
        let doc = kiln_set(&path, "ui.overlay", Some(&json!("off"))).unwrap();
        assert_eq!(doc.values["ui"]["overlay"], "off");
        assert_eq!(doc.values["ui"]["look"], "game");
        assert!(doc.errors.is_empty() && doc.text.contains("// keep me"), "{:?}", doc.errors);
        // (an enum also takes its option's index and any case, as Kiln.Api's ConfigSchema does: written as "off" then)
        for bad in [json!("yes"), json!(true), json!(false), json!(3)] {
            assert!(kiln_set(&path, "ui.overlay", Some(&bad)).is_err(), "{bad}");
        }
        assert_eq!(kiln_set(&path, "ui.overlay", Some(&json!("OFF"))).unwrap().values["ui"]["overlay"], "off");
        // "on", written exactly so: kiln.ui takes the value as it is ("ON" would be Kiln deciding)
        let doc = kiln_set(&path, "ui.overlay", Some(&json!("on"))).unwrap();
        assert_eq!(doc.values["ui"]["overlay"], "on");
        assert!(doc.errors.is_empty() && doc.text.contains("// keep me") && doc.values["ui"]["look"] == "game", "{:?}", doc.errors);
        assert_eq!(kiln_set(&path, "ui.overlay", Some(&json!("On"))).unwrap().values["ui"]["overlay"], "on");
        assert_eq!(kiln_set(&path, "ui.overlay", Some(&json!(2))).unwrap().values["ui"]["overlay"], "on");
        assert_eq!(kiln_setting_line("g", "ui.overlay", Some(&json!("on"))), "kiln.json of g: ui.overlay = on");
        let doc = kiln_set(&path, "ui.overlay", None).unwrap();
        assert!(doc.values["ui"].get("overlay").is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }
}

#[cfg(test)]
mod developer_debugging_tests {
    use super::*;
    use serde_json::json;

    /// (dev.31) Settings, the kiln.json card: "Developer debugging" writes kiln.json "developer_debugging" (the core reads it
    /// at the start: native/core/src/config.cpp read_debugging), off by default, a bool and nothing else.
    #[test]
    fn the_developer_debugging_switch_is_set_and_checked() {
        let schema = kiln_snapshot();
        let e = schema.entries.iter().find(|e| e.path == "developer_debugging").expect("developer_debugging in the snapshot");
        assert_eq!(e.default, json!(false));
        assert!(e.requires_restart, "the core reads it at the start");
        let dir = std::env::temp_dir().join(format!("kiln-launcher-debugging-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("kiln.json");
        std::fs::write(&path, "{\n  // keep me\n  \"policy\": \"verified-only\"\n}\n").unwrap();
        let doc = kiln_set(&path, "developer_debugging", Some(&json!(true))).unwrap();
        assert_eq!(doc.values["developer_debugging"], true);
        assert!(doc.errors.is_empty() && doc.text.contains("// keep me"), "{:?}", doc.errors);
        // the schema's text forms ("on", "true") are written as a JSON bool, which is all the core reads
        assert_eq!(kiln_set(&path, "developer_debugging", Some(&json!("on"))).unwrap().values["developer_debugging"], json!(true));
        for bad in [json!("maybe"), json!(1), json!(null), json!({"a": 1})] {
            assert!(kiln_set(&path, "developer_debugging", Some(&bad)).is_err(), "{bad}");
        }
        assert_eq!(kiln_setting_line("g", "developer_debugging", Some(&json!(true))), "kiln.json of g: developer_debugging = true");
        let doc = kiln_set(&path, "developer_debugging", None).unwrap();
        assert!(doc.values.get("developer_debugging").is_none());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
