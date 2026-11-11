//! ShroudForge (github.com/bonsaibauer/shroudforge, MIT) mods next to Kiln: the install in a game folder, its Lua mod
//! packages (`mod.json` + `extended.mod.json`), their switches and settings, and kiln-shroudforge's status file.
//! Design and API coverage: `docs/kiln/shroudforge-compat.md`.
//!
//! The files are ShroudForge's own: a mod's switch is `extended.mod.json` `enabled`, a setting is
//! `settings.<key>.value` (or the shorthand `settings.<key> = value`). Edits splice only the changed value
//! ([`crate::jsonc::set_value`]), so the rest of the file stays byte-identical and ShroudForge's own Modloader keeps
//! reading it. A package without `extended.mod.json` (a legacy EML package) gets the same file ShroudForge's
//! `ensure_eml_extension` would write.
//!
//! Who runs the mods: `<game>/kiln/plugins/kiln-shroudforge.dll` hosts the installed ShroudForge runtime (or steps
//! aside when ShroudForge's own dbghelp.dll already started it) and writes `kiln/state/shroudforge.json` ([`status`]).

use crate::jsonc::{set_value, PathSeg};
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// `kiln/state/shroudforge.json` (written by kiln-shroudforge.dll).
pub const STATUS_FILE: &str = "kiln/state/shroudforge.json";
/// The runtime ShroudForge's bootstrap loads; its presence means ShroudForge is installed.
pub const RUNTIME_DLL: &str = "shroudforge/shroudforge-runtime.dll";
/// ShroudForge's settings file (paths, modules, logging).
pub const CONFIG_FILE: &str = "shroudforge/config/modloader-config.json";

/// ShroudForge in a game folder.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfInstall {
    /// `shroudforge/version.json` "version".
    pub version: Option<String>,
    /// Where ShroudForge reads mods: modloader-config.json `paths.mods`, else `<game>/mods`.
    pub mods_dir: PathBuf,
    /// `<game>/winmm.dll` is classified as ShroudForge's bootstrap.
    pub bootstrap: bool,
    /// `<game>/dbghelp.dll` is classified as ShroudForge: on Windows (and under Wine with `dbghelp=n`) it starts SF by itself,
    /// next to Kiln ("self-hosted"); otherwise kiln-shroudforge hosts it.
    pub dbghelp: bool,
    /// `shroudforge/kfc-runtime.dll` (ShroudForge's native game access) is there.
    pub native_runtime: bool,
}

/// Is ShroudForge installed in `game_dir`?
pub fn detect(game_dir: &Path) -> Option<SfInstall> {
    if !game_dir.join(RUNTIME_DLL).is_file() {
        return None;
    }
    let version = std::fs::read(game_dir.join("shroudforge/version.json"))
        .ok()
        .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
        .and_then(|v| v.get("version").and_then(|s| s.as_str()).map(str::to_owned));
    Some(SfInstall {
        version,
        mods_dir: mods_dir(game_dir),
        bootstrap: crate::pe::classify_proxy(&game_dir.join("winmm.dll")).is_ok_and(|c| c.kind == crate::pe::ProxyKind::ShroudForge),
        dbghelp: crate::pe::classify_proxy(&game_dir.join("dbghelp.dll")).is_ok_and(|c| c.kind == crate::pe::ProxyKind::ShroudForge),
        native_runtime: game_dir.join("shroudforge/kfc-runtime.dll").is_file(),
    })
}

/// ShroudForge's mods folder for `game_dir` (its `paths.mods` setting, relative to the game folder, else `mods`).
/// The setting holds a Windows path when absolute; a host path is used as is when it exists.
pub fn mods_dir(game_dir: &Path) -> PathBuf {
    let configured = std::fs::read(game_dir.join(CONFIG_FILE))
        .ok()
        .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
        .and_then(|v| v.pointer("/paths/mods").and_then(|s| s.as_str()).map(str::to_owned))
        .filter(|s| !s.trim().is_empty());
    match configured {
        Some(p) => resolve_mods(game_dir, p.trim()),
        None => game_dir.join("mods"),
    }
}

/// `paths.mods` as a folder on this system.
///
/// ShroudForge writes an absolute Windows path there. Under Proton it was seen with Windows' verbatim prefix and a
/// drive that is not Z: (`\\?\S:\steamapps\common\Enshrouded\mods` on a Steam Deck: S: is the Steam library the game
/// is in). Taken as a Linux path that is `//?/S:/...`, which cannot be created, and every mod added to a profile
/// failed with "Read-only file system". Such a path is found again through the game folder: the two name the same
/// folders from the point where the Windows path enters the game's path.
fn resolve_mods(game_dir: &Path, p: &str) -> PathBuf {
    let host = PathBuf::from(p);
    if host.is_absolute() && host.is_dir() && (cfg!(windows) || !p.starts_with("//")) {
        return host;
    }
    let plain = crate::winpath::strip_verbatim(&p.replace('/', "\\")).into_owned();
    if !crate::winpath::is_absolute_game_path(&plain) {
        return game_dir.join(p.replace('\\', "/"));
    }
    if let Some(z) = crate::winpath::z_to_unix(&plain).filter(|h| h.is_dir()) {
        return z;
    }
    // another drive of the prefix (or a Windows path read on another system): the part inside the game folder
    let win: Vec<&str> = plain.split('\\').filter(|c| !c.is_empty()).skip(1).collect(); // without the drive
    let game: Vec<String> = game_dir.components().filter_map(|c| match c {
        std::path::Component::Normal(n) => Some(n.to_string_lossy().into_owned()),
        _ => None,
    }).collect();
    for k in (1..=win.len().min(game.len())).rev() {
        if win[..k].iter().zip(&game[game.len() - k..]).all(|(a, b)| a.eq_ignore_ascii_case(b)) {
            return win[k..].iter().fold(game_dir.to_path_buf(), |dir, c| dir.join(c));
        }
    }
    // a folder outside the game that this system cannot name: ShroudForge's default, never a path made of a drive letter
    game_dir.join("mods")
}

/// One setting of a ShroudForge mod (extended.mod.json `settings.<key>`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfSetting {
    pub key: String,
    pub value: serde_json::Value,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub label: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    /// toggle, checkbox, text, textarea, number, slider, select, radio, segmented, multiselect, keybind, color
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub control: Option<String>,
    /// "live" (the running mod sees it at once) or "restart"
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub apply_at: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub step: Option<f64>,
    /// value -> label (select, radio, segmented, multiselect)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub options: Option<serde_json::Map<String, serde_json::Value>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub enabled_when: Option<serde_json::Value>,
    /// the shorthand form `settings.<key> = value` (no label, no control)
    #[serde(default)]
    pub shorthand: bool,
}

/// A ShroudForge mod package.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfMod {
    pub id: String,
    pub name: String,
    pub version: String,
    #[serde(default)]
    pub description: String,
    #[serde(default)]
    pub authors: Vec<String>,
    pub folder: PathBuf,
    /// extended.mod.json `enabled` (false when the file is missing and mod.json says nothing).
    pub enabled: bool,
    /// "client", "server" (extended.mod.json `targets`; ShroudForge's defaults when missing).
    pub targets: Vec<String>,
    /// mod.json `capabilities`: patch (startup asset changes), export, runtime (live game access), runtime-register-dll
    pub capabilities: Vec<String>,
    #[serde(default)]
    pub dependencies: Vec<serde_json::Value>,
    pub settings: Vec<SfSetting>,
    #[serde(default)]
    pub groups: Vec<serde_json::Value>,
    /// "EML" for a legacy EML package, "SF" or none for ShroudForge's own
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub launcher: Option<String>,
    /// extended.mod.json exists
    pub extended: bool,
}

fn read_json_file(p: &Path) -> Result<serde_json::Value> {
    let bytes = std::fs::read(p).map_err(crate::util::io_at(p, "reading"))?;
    let text = String::from_utf8_lossy(&bytes);
    crate::jsonc::parse(text.trim_start_matches('\u{feff}'))
}

/// Does `folder` look like a ShroudForge (or EML) Lua package rather than an EMF mod (which also has a mod.json)?
/// Yes when mod.json parses with `id`, `name`, `version` and the folder has `extended.mod.json`, an SF schema, or
/// Lua code (`src/*.lua`, `mod.lua`). Shared EML `capabilities` alone do not identify a package. This includes EML packages that SF
/// can host; [`is_sf_own_mod`] distinguishes the engines for the Library.
pub fn is_sf_mod(folder: &Path) -> bool {
    let Ok(m) = read_json_file(&folder.join("mod.json")) else { return false };
    if !["id", "name", "version"].iter().all(|k| m.get(*k).and_then(|v| v.as_str()).is_some()) {
        return false;
    }
    // EMF manifests name a DLL or a main script and an EMF API version
    if m.get("dll").is_some() || m.get("main").is_some() || m.get("emf").is_some() || m.get("emfMin").is_some() || m.get("emf_api").is_some() {
        return false;
    }
    if folder.join("extended.mod.json").is_file() {
        return true;
    }
    if m.get("$schema").and_then(|s| s.as_str()).is_some_and(|s| s.to_ascii_lowercase().contains("shroudforge")) {
        return true;
    }
    folder.join("mod.lua").is_file()
        || std::fs::read_dir(folder.join("src"))
            .into_iter()
            .flatten()
            .flatten()
            .any(|e| e.path().extension().is_some_and(|x| x == "lua"))
}

/// SF-specific metadata, as opposed to EML's shared manifest and capabilities. ShroudForge stamps legacy EML
/// packages with an extension whose launcher is EML; those remain EML mods.
pub fn is_sf_own_mod(folder: &Path) -> bool {
    let ext = folder.join("extended.mod.json");
    if ext.is_file() {
        return !read_json_file(&ext).ok().and_then(|v| str_of(&v, "launcher")).is_some_and(|s| s.eq_ignore_ascii_case("EML"));
    }
    read_json_file(&folder.join("mod.json")).ok()
        .and_then(|v| str_of(&v, "$schema"))
        .is_some_and(|s| s.to_ascii_lowercase().contains("shroudforge"))
}

fn str_of(v: &serde_json::Value, k: &str) -> Option<String> {
    v.get(k).and_then(|s| s.as_str()).map(str::to_owned)
}

fn parse_setting(key: &str, raw: &serde_json::Value) -> SfSetting {
    let obj = raw.as_object().filter(|o| o.contains_key("value"));
    let Some(o) = obj else {
        return SfSetting {
            key: key.into(),
            value: raw.clone(),
            label: None,
            description: None,
            control: None,
            apply_at: None,
            min: None,
            max: None,
            step: None,
            options: None,
            enabled_when: None,
            shorthand: true,
        };
    };
    let f = |k: &str| o.get(k).and_then(|v| v.as_f64());
    SfSetting {
        key: key.into(),
        value: o["value"].clone(),
        label: o.get("label").and_then(|s| s.as_str()).map(str::to_owned),
        description: o.get("description").and_then(|s| s.as_str()).map(str::to_owned),
        control: o.get("control").and_then(|s| s.as_str()).map(str::to_owned),
        apply_at: o.get("applyAt").and_then(|s| s.as_str()).map(str::to_owned).or_else(|| {
            o.get("restartRequired").and_then(|b| b.as_bool()).map(|r| if r { "restart" } else { "live" }.to_owned())
        }),
        min: f("min"),
        max: f("max"),
        step: f("step"),
        options: o.get("options").and_then(|v| v.as_object()).cloned(),
        enabled_when: o.get("enabledWhen").cloned(),
        shorthand: false,
    }
}

/// ShroudForge's own rule for a package id (mod.json `id`): 1 to 80 ASCII letters (either case), digits, `.`, `-`, `_`.
/// Wider than a Kiln id: Kiln imports such a package under a slug Library id and keeps the files as they are.
pub fn is_package_id(id: &str) -> bool {
    !id.is_empty() && id.len() <= 80 && id.bytes().all(|b| b.is_ascii_alphanumeric() || matches!(b, b'.' | b'-' | b'_'))
}

/// Another folder in ShroudForge's mods folder `root` (not `dst`) whose mod.json holds the package id of the package
/// in `src`. ShroudForge finds packages by that id, not by folder name, and drops every package of an id it finds twice:
/// such a folder (the player's own copy) means Kiln must not place a second one.
pub fn other_holder(root: &Path, src: &Path, dst: &Path) -> Option<PathBuf> {
    let id = read_json_file(&src.join("mod.json")).ok().and_then(|m| str_of(&m, "id"))?;
    let mut dirs: Vec<PathBuf> = std::fs::read_dir(root).into_iter().flatten().flatten().map(|e| e.path())
        .filter(|p| p.is_dir() && p.as_path() != dst).collect();
    dirs.sort();
    dirs.into_iter().find(|p| read_json_file(&p.join("mod.json")).ok().and_then(|m| str_of(&m, "id")).as_deref() == Some(id.as_str()))
}

/// Reads one package (`folder` holds mod.json).
pub fn read_mod(folder: &Path) -> Result<SfMod> {
    let m = read_json_file(&folder.join("mod.json"))?;
    let id = str_of(&m, "id").ok_or_else(|| Error::invalid(format!("{}/mod.json has no \"id\"", folder.display())))?;
    let ext_path = folder.join("extended.mod.json");
    let extended = ext_path.is_file();
    let ext = if extended { read_json_file(&ext_path)? } else { serde_json::json!({}) };
    let launcher = str_of(&ext, "launcher").or_else(|| (!extended).then(|| "EML".to_owned()));
    let targets = match ext.get("targets").and_then(|t| t.as_array()) {
        Some(a) => a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect(),
        // ShroudForge: legacy EML packages run on both sides, ShroudForge packages on the client
        None if launcher.as_deref() == Some("EML") => vec!["client".into(), "server".into()],
        None => vec!["client".into()],
    };
    let settings = ext
        .get("settings")
        .and_then(|s| s.as_object())
        .map(|o| o.iter().map(|(k, v)| parse_setting(k, v)).collect())
        .unwrap_or_default();
    Ok(SfMod {
        name: str_of(&m, "name").unwrap_or_else(|| id.clone()),
        id,
        version: str_of(&m, "version").unwrap_or_default(),
        description: str_of(&m, "description").unwrap_or_default(),
        authors: m.get("authors").and_then(|a| a.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect()).unwrap_or_default(),
        folder: folder.to_path_buf(),
        enabled: ext.get("enabled").and_then(|b| b.as_bool()).or_else(|| m.get("enabled").and_then(|b| b.as_bool())).unwrap_or(false),
        targets,
        capabilities: m.get("capabilities").and_then(|a| a.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect()).unwrap_or_default(),
        dependencies: m.get("dependencies").and_then(|a| a.as_array()).cloned().unwrap_or_default(),
        settings,
        groups: ext.get("groups").and_then(|a| a.as_array()).cloned().unwrap_or_default(),
        launcher,
        extended,
    })
}

/// A folder in the mods dir that is not a readable ShroudForge package, and why.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfSkipped {
    pub folder: PathBuf,
    pub reason: String,
}

/// Every package in ShroudForge's mods folder of `game_dir`, by folder name; plus the folders that are not.
pub fn list_mods(game_dir: &Path) -> (Vec<SfMod>, Vec<SfSkipped>) {
    list_mods_in(&mods_dir(game_dir))
}

pub fn list_mods_in(dir: &Path) -> (Vec<SfMod>, Vec<SfSkipped>) {
    let mut mods = Vec::new();
    let mut skipped = Vec::new();
    let mut entries: Vec<PathBuf> =
        std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
    entries.sort();
    for folder in entries {
        if !folder.join("mod.json").is_file() {
            skipped.push(SfSkipped { reason: skip_reason(&folder), folder });
            continue;
        }
        if !is_sf_mod(&folder) {
            skipped.push(SfSkipped { reason: skip_reason(&folder), folder });
            continue;
        }
        match read_mod(&folder) {
            Ok(m) => mods.push(m),
            Err(e) => skipped.push(SfSkipped { folder, reason: format!("a ShroudForge mod whose files cannot be read: {}", e.message) }),
        }
    }
    (mods, skipped)
}

/// What a folder is, for the Library's skip messages: plain words, what it is and why it does not load.
pub fn skip_reason(folder: &Path) -> String {
    if folder.join("mod.json").is_file() {
        if let Err(e) = read_json_file(&folder.join("mod.json")) {
            return format!("unreadable mod.json: {}", e.message);
        }
        if is_sf_mod(folder) {
            return match read_mod(folder) {
                Ok(m) => format!(
                    "{} {} is a ShroudForge Lua mod: it runs through ShroudForge, which Kiln hosts when ShroudForge is installed in the game folder",
                    m.name, m.version
                ),
                Err(e) => format!("a ShroudForge mod with an unreadable mod.json or extended.mod.json: {}", e.message),
            };
        }
        return "mod.json does not describe a recognised mod package (EML needs id, name, version and src/mod.lua); helper DLLs are not imported separately".into();
    }
    let has = |ext: &str| {
        std::fs::read_dir(folder).into_iter().flatten().flatten().any(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case(ext)))
    };
    if has("lua") {
        "has Lua files but no mod.json: not a mod package (ShroudForge mods need mod.json with id, name and version)".into()
    } else if has("pak") || has("kfc") {
        "game data files (.pak/.kfc): not a mod Kiln can load".into()
    } else {
        "no supported mod manifest or identifiable mod entry point".into()
    }
}

/// extended.mod.json of `folder`, created like ShroudForge's `ensure_eml_extension` when missing.
fn extension_text(m: &SfMod) -> Result<String> {
    let p = m.folder.join("extended.mod.json");
    if p.is_file() {
        let bytes = std::fs::read(&p).map_err(crate::util::io_at(&p, "reading"))?;
        return Ok(String::from_utf8_lossy(&bytes).trim_start_matches('\u{feff}').to_string());
    }
    let v = serde_json::json!({
        "$schema": "https://bonsaibauer.github.io/shroudforge/schemas/extended.mod.schema.json",
        "schemaVersion": 1,
        "enabled": m.enabled,
        "targets": ["client", "server"],
        "launcher": "EML",
        "settings": {}
    });
    Ok(serde_json::to_string_pretty(&v).map_err(|e| Error::invalid(e.to_string()))? + "\n")
}

fn write_extension(m: &SfMod, text: &str) -> Result<()> {
    crate::jsonc::parse(text)?; // never write a broken file
    crate::util::write_atomic(&m.folder.join("extended.mod.json"), text.as_bytes())
}

/// Switches a mod on or off (ShroudForge's own switch; a running ShroudForge picks it up live).
pub fn set_enabled(folder: &Path, enabled: bool) -> Result<SfMod> {
    let m = read_mod(folder)?;
    if m.extended && m.enabled == enabled {
        return Ok(m);
    }
    let text = extension_text(&m)?;
    let out = set_value(&text, &[PathSeg::Key("enabled".into())], &serde_json::Value::Bool(enabled))?;
    write_extension(&m, &out)?;
    read_mod(folder)
}

/// Checks `value` against the setting's current kind, range and options (ShroudForge rejects other kinds).
pub fn check_value(s: &SfSetting, value: &serde_json::Value) -> Result<()> {
    use serde_json::Value as V;
    let same_kind = matches!(
        (&s.value, value),
        (V::Bool(_), V::Bool(_)) | (V::String(_), V::String(_)) | (V::Number(_), V::Number(_)) | (V::Array(_), V::Array(_))
    );
    if !same_kind {
        return Err(Error::invalid(format!("setting {} holds a {}, not a {}", s.key, kind(&s.value), kind(value))));
    }
    if let Some(n) = value.as_f64()
        && (s.min.is_some_and(|m| n < m) || s.max.is_some_and(|m| n > m))
    {
        return Err(Error::invalid(format!(
            "setting {} must be within {}..{}",
            s.key,
            s.min.map(|v| v.to_string()).unwrap_or_default(),
            s.max.map(|v| v.to_string()).unwrap_or_default()
        )));
    }
    if let Some(opts) = &s.options {
        let ok = |v: &V| v.as_str().is_some_and(|k| opts.contains_key(k)) || v.as_f64().is_some_and(|n| opts.contains_key(&n.to_string()));
        let all = match value {
            V::Array(items) => items.iter().all(ok),
            v => ok(v),
        };
        if !all {
            return Err(Error::invalid(format!("setting {}: not one of {}", s.key, opts.keys().cloned().collect::<Vec<_>>().join(", "))));
        }
    }
    Ok(())
}

fn kind(v: &serde_json::Value) -> &'static str {
    match v {
        serde_json::Value::Bool(_) => "boolean",
        serde_json::Value::String(_) => "string",
        serde_json::Value::Number(_) => "number",
        serde_json::Value::Array(_) => "list",
        serde_json::Value::Object(_) => "object",
        serde_json::Value::Null => "null",
    }
}

/// Sets `settings.<key>.value` (or the shorthand value). Unknown keys are refused: a mod's settings are declared in its
/// extended.mod.json.
pub fn set_setting(folder: &Path, key: &str, value: &serde_json::Value) -> Result<SfMod> {
    let m = read_mod(folder)?;
    let s = m.settings.iter().find(|s| s.key == key).ok_or_else(|| Error::not_found(format!("{} has no setting {key}", m.id)))?;
    check_value(s, value)?;
    let text = extension_text(&m)?;
    let mut path = vec![PathSeg::Key("settings".into()), PathSeg::Key(key.into())];
    if !s.shorthand {
        path.push(PathSeg::Key("value".into()));
    }
    let out = set_value(&text, &path, value)?;
    write_extension(&m, &out)?;
    read_mod(folder)
}

/// A JSON Schema of the mod's settings for the launcher's SchemaForm (titles, descriptions, ranges, enums; `x-kiln-*`
/// hints for the control, the group and "applies after a restart").
pub fn settings_schema(m: &SfMod) -> serde_json::Value {
    let mut props = serde_json::Map::new();
    let group_of = |key: &str| -> Option<String> {
        m.groups.iter().find_map(|g| {
            let listed = g.get("settings").and_then(|s| s.as_array()).is_some_and(|a| a.iter().any(|v| v.as_str() == Some(key)));
            listed.then(|| g.get("label").and_then(|l| l.as_str()).unwrap_or("Settings").to_owned())
        })
    };
    for s in &m.settings {
        let mut p = serde_json::Map::new();
        let ty = match &s.value {
            serde_json::Value::Bool(_) => "boolean",
            serde_json::Value::Number(n) if n.is_i64() || n.is_u64() => "integer",
            serde_json::Value::Number(_) => "number",
            serde_json::Value::Array(_) => "array",
            _ => "string",
        };
        // an integer setting with a fractional step is a number
        let ty = if ty == "integer" && s.step.is_some_and(|st| st.fract() != 0.0) { "number" } else { ty };
        p.insert("type".into(), ty.into());
        p.insert("title".into(), s.label.clone().unwrap_or_else(|| s.key.clone()).into());
        if let Some(d) = &s.description {
            p.insert("description".into(), d.clone().into());
        }
        p.insert("default".into(), s.value.clone());
        if let Some(v) = s.min {
            p.insert("minimum".into(), v.into());
        }
        if let Some(v) = s.max {
            p.insert("maximum".into(), v.into());
        }
        if let Some(v) = s.step {
            p.insert("multipleOf".into(), v.into());
        }
        if let Some(opts) = &s.options {
            let keys: Vec<serde_json::Value> = opts.keys().map(|k| k.clone().into()).collect();
            let labels: Vec<serde_json::Value> = opts.values().cloned().collect();
            if ty == "array" {
                p.insert("items".into(), serde_json::json!({ "type": "string", "enum": keys, "x-kiln-enum-labels": labels }));
                p.insert("uniqueItems".into(), true.into());
            } else {
                p.insert("enum".into(), keys.into());
                p.insert("x-kiln-enum-labels".into(), labels.into());
            }
        } else if ty == "array" {
            p.insert("items".into(), serde_json::json!({ "type": "string" }));
        }
        if let Some(c) = &s.control {
            p.insert("x-kiln-control".into(), c.clone().into());
        }
        if s.apply_at.as_deref() == Some("restart") {
            p.insert("x-kiln-restart".into(), true.into());
        }
        if let Some(g) = group_of(&s.key) {
            p.insert("x-kiln-group".into(), g.into());
        }
        if let Some(w) = &s.enabled_when {
            p.insert("x-kiln-enabled-when".into(), w.clone());
        }
        props.insert(s.key.clone(), p.into());
    }
    serde_json::json!({
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "title": m.name,
        "description": m.description,
        "type": "object",
        "properties": props,
        "additionalProperties": false,
    })
}

/// The settings' current values as one object (key -> value), the SchemaForm's data.
pub fn settings_values(m: &SfMod) -> serde_json::Value {
    serde_json::Value::Object(m.settings.iter().map(|s| (s.key.clone(), s.value.clone())).collect())
}

/// Profile activation: each listed mod's switch as the profile says (ids match mod.json `id`, case-insensitive). Mods
/// the profile does not list keep their switch. Returns the ids whose switch changed.
pub fn apply_switches(game_dir: &Path, wanted: &[(String, bool)]) -> Result<Vec<String>> {
    let (mods, _) = list_mods(game_dir);
    let mut changed = Vec::new();
    for (id, on) in wanted {
        if let Some(m) = mods.iter().find(|m| m.id.eq_ignore_ascii_case(id))
            && m.enabled != *on
        {
            set_enabled(&m.folder, *on)?;
            changed.push(m.id.clone());
        }
    }
    Ok(changed)
}

/// `kiln/state/shroudforge.json`: `mode` hosted | self-hosted | not-installed | off | failed, `reason`, `version`,
/// `side`, `nativeAbi`, `native` (ShroudForge's native runtime status text), `turns`, `updated` (unix seconds).
pub fn status(game_dir: &Path) -> Result<Option<serde_json::Value>> {
    crate::util::read_json(&game_dir.join(STATUS_FILE))
}

/// ShroudForge's own per-mod effect reports (`runtime.report_effect`) from its state file: id -> {state, detail}.
pub fn effects(game_dir: &Path) -> serde_json::Value {
    let state_dir = std::fs::read(game_dir.join(CONFIG_FILE))
        .ok()
        .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
        .and_then(|v| v.pointer("/paths/state").and_then(|s| s.as_str()).map(str::to_owned))
        .filter(|s| !s.trim().is_empty());
    let file = match state_dir {
        Some(d) => game_dir.join(d.replace('\\', "/")).join("state.json"),
        None => game_dir.join("shroudforge/state.json"),
    };
    std::fs::read(file)
        .ok()
        .and_then(|b| serde_json::from_slice::<serde_json::Value>(&b).ok())
        .and_then(|v| v.pointer("/runtime/effects").cloned())
        .unwrap_or_else(|| serde_json::json!({}))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn pkg(dir: &Path, id: &str, ext: Option<&str>) -> PathBuf {
        let f = dir.join(id);
        std::fs::create_dir_all(f.join("src")).unwrap();
        std::fs::write(f.join("mod.json"), format!(r#"{{"id":"{id}","name":"Test {id}","version":"1.0.0","capabilities":["runtime"]}}"#)).unwrap();
        std::fs::write(f.join("src/mod.lua"), "return {}").unwrap();
        if let Some(e) = ext {
            std::fs::write(f.join("extended.mod.json"), e).unwrap();
        }
        f
    }

    const EXT: &str = r#"{
  "schemaVersion": 1,
  "enabled": false,
  "targets": ["client", "server"],
  "settings": {
    "infiniteStamina": { "value": true, "label": "Infinite Stamina", "description": "Keeps stamina full." },
    "speed": { "value": 2, "control": "slider", "min": 1, "max": 10, "step": 1, "applyAt": "restart" },
    "mode": { "value": "a", "control": "select", "options": { "a": "Alpha", "b": "Beta" } },
    "raw": 5
  },
  "groups": [{ "label": "Survival", "settings": ["infiniteStamina"] }]
}
"#;

    #[test]
    fn reads_and_edits_preserving_layout() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-test").unwrap();
        let f = pkg(t.path(), "sf-survival", Some(EXT));
        assert!(is_sf_mod(&f));
        let m = read_mod(&f).unwrap();
        assert!(!m.enabled);
        assert_eq!(m.targets, vec!["client", "server"]);
        assert_eq!(m.settings.len(), 4);
        assert!(m.settings.iter().find(|s| s.key == "raw").unwrap().shorthand);
        let m = set_enabled(&f, true).unwrap();
        assert!(m.enabled);
        set_setting(&f, "infiniteStamina", &serde_json::json!(false)).unwrap();
        set_setting(&f, "raw", &serde_json::json!(7)).unwrap();
        let text = std::fs::read_to_string(f.join("extended.mod.json")).unwrap();
        // only the values changed: same lines otherwise
        let expected = EXT.replace("\"enabled\": false", "\"enabled\": true").replace(
            "\"value\": true, \"label\": \"Infinite Stamina\"",
            "\"value\": false, \"label\": \"Infinite Stamina\"",
        ).replace("\"raw\": 5", "\"raw\": 7");
        assert_eq!(text, expected);
        assert!(set_setting(&f, "speed", &serde_json::json!(11)).is_err());
        assert!(set_setting(&f, "speed", &serde_json::json!("x")).is_err());
        assert!(set_setting(&f, "mode", &serde_json::json!("c")).is_err());
        assert!(set_setting(&f, "nope", &serde_json::json!(1)).is_err());
        set_setting(&f, "mode", &serde_json::json!("b")).unwrap();
    }

    #[test]
    fn schema_for_schemaform() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-test").unwrap();
        let f = pkg(t.path(), "m", Some(EXT));
        let s = settings_schema(&read_mod(&f).unwrap());
        assert_eq!(s["properties"]["infiniteStamina"]["type"], "boolean");
        assert_eq!(s["properties"]["infiniteStamina"]["x-kiln-group"], "Survival");
        assert_eq!(s["properties"]["speed"]["type"], "integer");
        assert_eq!(s["properties"]["speed"]["maximum"], 10.0);
        assert_eq!(s["properties"]["speed"]["x-kiln-restart"], true);
        assert_eq!(s["properties"]["mode"]["enum"], serde_json::json!(["a", "b"]));
    }

    #[test]
    fn legacy_eml_package_gets_shroudforge_extension() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-test").unwrap();
        let f = pkg(t.path(), "old", None);
        let m = read_mod(&f).unwrap();
        assert_eq!(m.launcher.as_deref(), Some("EML"));
        assert_eq!(m.targets, vec!["client", "server"]);
        set_enabled(&f, true).unwrap();
        let v: serde_json::Value = serde_json::from_slice(&std::fs::read(f.join("extended.mod.json")).unwrap()).unwrap();
        assert_eq!(v["launcher"], "EML");
        assert_eq!(v["enabled"], true);
    }

    #[test]
    fn emf_and_other_folders_are_not_sf() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-test").unwrap();
        let emf = t.path().join("emfmod");
        std::fs::create_dir_all(&emf).unwrap();
        std::fs::write(emf.join("mod.json"), r#"{"id":"x","name":"X","version":"1.0.0","dll":"x.dll"}"#).unwrap();
        assert!(!is_sf_mod(&emf));
        let lua = t.path().join("loose");
        std::fs::create_dir_all(&lua).unwrap();
        std::fs::write(lua.join("a.lua"), "").unwrap();
        assert!(skip_reason(&lua).contains("no mod.json"));
        let sf = pkg(t.path(), "sf", Some(EXT));
        assert!(skip_reason(&sf).contains("ShroudForge Lua mod"));
        let (mods, skipped) = list_mods_in(t.path());
        assert_eq!(mods.len(), 1);
        assert_eq!(skipped.len(), 2);
    }

    #[test]
    fn detect_and_switches() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-test").unwrap();
        let g = t.path();
        assert!(detect(g).is_none());
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::write(g.join(RUNTIME_DLL), b"MZ").unwrap();
        std::fs::write(g.join("shroudforge/version.json"), r#"{"version":"1.7.9"}"#).unwrap();
        std::fs::write(g.join(CONFIG_FILE), r#"{"paths":{"mods":null}}"#).unwrap();
        let i = detect(g).unwrap();
        assert_eq!(i.version.as_deref(), Some("1.7.9"));
        assert_eq!(i.mods_dir, g.join("mods"));
        pkg(&g.join("mods"), "sf-survival", Some(EXT));
        let changed = apply_switches(g, &[("SF-Survival".into(), true), ("missing".into(), true)]).unwrap();
        assert_eq!(changed, vec!["sf-survival"]);
        assert!(apply_switches(g, &[("sf-survival".into(), true)]).unwrap().is_empty());
        std::fs::write(g.join(CONFIG_FILE), r#"{"paths":{"mods":"MyMods"}}"#).unwrap();
        assert_eq!(mods_dir(g), g.join("MyMods"));
    }

    /// `paths.mods` as ShroudForge writes it under Proton: a Windows path on a drive of the prefix. As a Linux path
    /// it was `//?/S:/steamapps/...`, which nothing can create.
    #[cfg(not(windows))]
    #[test]
    fn a_windows_mods_path_is_found_through_the_game_folder() {
        let t = crate::library::TempDir::new(&std::env::temp_dir(), "kiln-sf-winpath").unwrap();
        let g = t.path().join("SteamLibrary/steamapps/common/Enshrouded");
        std::fs::create_dir_all(g.join("mods")).unwrap();
        // a Steam Deck: S: is the library, with the verbatim prefix
        assert_eq!(resolve_mods(&g, r"\\?\S:\steamapps\common\Enshrouded\mods"), g.join("mods"));
        assert_eq!(resolve_mods(&g, "//?/S:/steamapps/common/Enshrouded/mods"), g.join("mods"));
        // another drive that maps higher up, another folder name and another case
        assert_eq!(resolve_mods(&g, r"E:\steamlibrary\STEAMAPPS\common\Enshrouded\MyMods"), g.join("MyMods"));
        assert_eq!(resolve_mods(&g, r"S:\steamapps\common\Enshrouded\mods\packs"), g.join("mods/packs"));
        // the game folder itself
        assert_eq!(resolve_mods(&g, r"S:\steamapps\common\Enshrouded"), g);
        // Z: is the host's root
        let z = format!("Z:{}", g.join("mods").display().to_string().replace('/', "\\"));
        assert_eq!(resolve_mods(&g, &z), g.join("mods"));
        assert_eq!(resolve_mods(&g, &format!(r"\\?\{z}")), g.join("mods"));
        // outside the game: the default, never a path that starts with a drive letter
        for outside in [r"D:\Elsewhere\mods", r"\\?\D:\mods"] {
            let got = resolve_mods(&g, outside);
            assert_eq!(got, g.join("mods"), "{outside}");
            assert!(!got.to_string_lossy().contains(':'));
        }
        // a host path and a relative one, as before
        assert_eq!(resolve_mods(&g, &g.join("mods").display().to_string()), g.join("mods"));
        assert_eq!(resolve_mods(&g, r"sub\mods"), g.join("sub/mods"));
        // through the setting
        std::fs::create_dir_all(g.join("shroudforge/config")).unwrap();
        std::fs::write(g.join(CONFIG_FILE), r#"{"paths":{"mods":"\\\\?\\S:\\steamapps\\common\\Enshrouded\\mods"}}"#).unwrap();
        assert_eq!(mods_dir(&g), g.join("mods"));
    }
}
