//! Mods made for other engines, managed like Kiln mods (docs/kiln/engines.md "Managing every engine's mods"):
//!
//! - [`builtin_loaders`]: the Library importers of the engine kinds Kiln runs (EML, ShroudForge, SET, Greeneye, Flame,
//!   Shroudtopia). Each copies the mod into a content-addressed library folder (`library::commit_folder`). The
//!   launcher only moves files: no engine code (EML's GPL crates included) is linked or run here.
//! - [`place`]: ShroudForge and SET read their mods only from their own folder in the game. Profile activation copies
//!   the profile's mods of those kinds there (a `.kiln-library.json` marker says the copy is Kiln's), keeps copies
//!   that are current, and never touches folders the player put there.
//! - Settings per format ([`settings`], [`set_setting`]): ShroudForge's `extended.mod.json` settings (on the copy in
//!   SF's mods folder) and Shroudtopia's `shroudtopia.json` (in the profile's config folder). EMF's ini is `config`'s.
//! - [`status`]: the plugins' state files (`kiln/state/<engine>.json`) for the Play and Logs pages.
//! - [`eml_backup`] / [`eml_restore_vanilla`]: EML's `<exe>.kfc.bak`, for "Restore vanilla game data".

use crate::engines::{ImportCtx, Loader};
use crate::library::{DetectKind, ImportRecord, Skipped};
use crate::manifest::{Clients, ModKind, Side};
use crate::paths::DataDirs;
use crate::util::io_at;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

/// The marker file in a copy [`place`] made: `{format, kind, id, version, hash}`.
pub const MARKER: &str = ".kiln-library.json";
/// SET's mods folder, relative to the game.
pub const SET_MODS: &str = "shrouded_engine_tweaks/mods";

/// The kinds with a Library importer (the reserved enshwick, shroudlink and ember have none yet).
pub const IMPORTED: [ModKind; 6] =
    [ModKind::Eml, ModKind::ShroudForge, ModKind::Set, ModKind::Greeneye, ModKind::Flame, ModKind::Shroudtopia];

/// The plugin that runs a kind ("kiln-eml"), for logs and the launcher.
pub fn plugin_of(kind: ModKind) -> Option<&'static str> {
    Some(match kind {
        ModKind::Emf => "kiln-emf",
        ModKind::Eml => "kiln-eml",
        ModKind::ShroudForge => "kiln-shroudforge",
        ModKind::Set => "kiln-set",
        ModKind::Greeneye => "kiln-greeneye",
        ModKind::Flame => "kiln-flame",
        ModKind::Shroudtopia => "kiln-shroudtopia",
        _ => return None,
    })
}

/// engines::builtin_loaders: one importer per engine kind Kiln runs.
pub fn builtin_loaders(m: &mut HashMap<DetectKind, Loader>) {
    for k in IMPORTED {
        m.insert(k.detect_kind(), Loader { name: plugin_of(k).unwrap_or("kiln"), import: import_engine_mod });
    }
}

// ---- import ----

/// Where a kind's mod runs and what joining players need, when its files don't say (`side`/`clients` in mod.json win).
pub fn default_side(kind: ModKind) -> (Side, Clients) {
    match kind {
        // data patches and new content: both sides must have the same data
        ModKind::Eml | ModKind::ShroudForge | ModKind::Flame => (Side::Both, Clients::Mod),
        ModKind::Greeneye => (Side::Client, Clients::None),
        ModKind::Shroudlink | ModKind::Ember => (Side::Server, Clients::None),
        _ => (Side::Both, Clients::None),
    }
}

fn read_json(p: &Path) -> Option<serde_json::Value> {
    crate::jsonc::parse(crate::manifest::read_text(p).ok()?.trim_start_matches('\u{feff}')).ok()
}

/// The manifest an engine's mod folder has (mod.json, flame.json, shroudtopia.json), as JSON.
pub fn manifest_json(folder: &Path) -> Option<serde_json::Value> {
    ["mod.json", "flame.json", "shroudtopia.json"].iter().find_map(|f| read_json(&folder.join(f)))
}

fn side_of(kind: ModKind, folder: &Path) -> (Side, Clients) {
    let (mut side, mut clients) = default_side(kind);
    if kind == ModKind::ShroudForge
        && let Ok(m) = crate::shroudforge::read_mod(folder)
    {
        let c = m.targets.iter().any(|t| t == "client");
        let s = m.targets.iter().any(|t| t == "server");
        side = match (c, s) {
            (true, false) => Side::Client,
            (false, true) => Side::Server,
            _ => Side::Both,
        };
        if side == Side::Server {
            clients = Clients::None;
        }
    }
    if let Some(v) = read_json(&folder.join("mod.json")) {
        match v.get("side").and_then(|s| s.as_str()) {
            Some("client") => side = Side::Client,
            Some("server") => side = Side::Server,
            Some("both") => side = Side::Both,
            _ => {}
        }
        match v.get("clients").and_then(|s| s.as_str()) {
            Some("none") => clients = Clients::None,
            Some("kiln") => clients = Clients::Kiln,
            Some("mod") => clients = Clients::Mod,
            _ => {}
        }
    }
    (side, clients)
}

/// The importer of every engine kind: the folder (or the lone mod DLL, wrapped into a folder with the manifest its
/// engine needs) goes into `library/<kind>/<id>/<version>+<hash8>/`.
pub fn import_engine_mod(ctx: &mut ImportCtx) -> Result<()> {
    let d = ctx.detected;
    let skip = |report: &mut crate::library::ImportReport, why: String| {
        report.skipped.push(Skipped { path: d.path.clone(), reason: why });
        Ok(())
    };
    let Some(kind) = ModKind::of_detect(d.kind).filter(|k| k.is_engine()) else {
        return skip(ctx.report, d.reason.clone());
    };
    if kind.is_reserved() {
        return skip(ctx.report, d.reason.clone());
    }
    let src = ctx.src;
    let mut id = d.id.clone().unwrap_or_default();
    let mut version = d.version.clone().unwrap_or_default();
    let mut name = d.name.clone();
    if kind == ModKind::ShroudForge && src.is_dir() {
        match crate::shroudforge::read_mod(src) {
            Ok(m) => {
                id = m.id;
                version = m.version;
                name = Some(m.name);
            }
            Err(e) => return skip(ctx.report, format!("{}: not a ShroudForge package it can run: {}", d.path, e.message)),
        }
    }
    if !crate::manifest::is_mod_id(&id) {
        let from = if id.is_empty() { name.clone().unwrap_or_default() } else { id.clone() };
        id = crate::library::id_from_name(&from);
    }
    if !crate::manifest::is_mod_id(&id) {
        return skip(ctx.report, format!("{}: no usable mod id", d.path));
    }
    let version = if crate::semver::SemVersion::parse(&version).is_ok() {
        version
    } else if version.is_empty() {
        "0.0.0".to_string()
    } else {
        format!("0.0.0-{}", crate::library::id_from_name(&version))
    };
    let staging = ctx.dirs.library();
    std::fs::create_dir_all(&staging).map_err(io_at(&staging, "creating"))?;
    let tmp = crate::library::TempDir::new(&staging, "import")?;
    let work = tmp.path().join("m");
    let size = if src.is_dir() {
        let size = crate::library::copy_mod_tree(src, &work)?;
        if crate::hash::dirhash(&work)? != crate::hash::dirhash(src)? {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while it was imported", src.display())));
        }
        size
    } else {
        std::fs::create_dir_all(&work).map_err(io_at(&work, "creating"))?;
        let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "mod.dll".into());
        let mut size = std::fs::copy(src, work.join(&file)).map_err(io_at(src, "copying"))?;
        // the manifest each engine's loader needs to find the DLL
        let generated = match kind {
            ModKind::Greeneye => Some(("mod.json", serde_json::json!({ "id": id, "name": file, "version": version, "dll": file, "side": "client",
                "description": format!("Generated by Kiln from {file}") }))),
            ModKind::Flame => Some(("flame.json", serde_json::json!({ "dll": file }))),
            // kiln-shroudtopia's folder scan (servers, drop-ins) takes folders with a mod.json
            ModKind::Shroudtopia => Some(("mod.json", serde_json::json!({ "id": id, "name": file, "version": version,
                "description": format!("Generated by Kiln from {file}") }))),
            _ => None,
        };
        if let Some((f, v)) = generated {
            let mut body = serde_json::to_string_pretty(&v)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(f), body).map_err(io_at(&work, "writing"))?;
        }
        size
    };
    let hash = crate::hash::dirhash(&work)?;
    let (side, clients) = side_of(kind, &work);
    let rec = ImportRecord {
        format: 1,
        kind,
        id,
        version,
        hash,
        imported: crate::util::now_rfc3339(),
        source: Some(ctx.from.clone()),
        size,
        name,
        side: Some(side),
        clients: Some(clients),
        file: None,
    };
    crate::library::commit_folder(ctx.dirs, &work, rec, ctx.report)
}

// ---- placing ShroudForge and SET mods in their engine's folder ----

/// The folder a kind's mod `id` is placed in for its engine (None: the plugin reads the library folder itself).
pub fn placed_folder(kind: ModKind, game_dir: &Path, id: &str) -> Option<PathBuf> {
    Some(match kind {
        ModKind::ShroudForge => crate::shroudforge::mods_dir(game_dir).join(id),
        ModKind::Set => game_dir.join(SET_MODS).join(id),
        _ => return None,
    })
}

/// The marker of a placed copy.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Marker {
    pub format: u32,
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    pub hash: String,
}

/// The marker in `folder` (None: not a copy Kiln placed).
pub fn marker(folder: &Path) -> Option<Marker> {
    crate::util::read_json::<Marker>(&folder.join(MARKER)).ok().flatten()
}

/// What [`place`] did, by kind and id.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlaceReport {
    /// Copied (new or a new version).
    pub placed: Vec<String>,
    /// Already current.
    pub kept: Vec<String>,
    /// Kiln's copies removed (SET: switched off or no longer in the profile).
    pub removed: Vec<String>,
    /// ShroudForge mods whose `extended.mod.json` switch changed.
    pub switched: Vec<String>,
    /// A folder with the mod's name that the player put there: left as it is.
    pub own: Vec<String>,
}

/// Copies a library folder to `dst` (through a temporary sibling) with its marker.
fn copy_in(src: &Path, dst: &Path, m: &Marker) -> Result<()> {
    let parent = dst.parent().ok_or_else(|| Error::invalid(format!("{}: no parent folder", dst.display())))?;
    std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
    let tmp = parent.join(format!(".kiln-place-{}", m.id));
    let _ = std::fs::remove_dir_all(&tmp);
    crate::util::copy_tree(src, &tmp)?;
    crate::util::write_json_atomic(&tmp.join(MARKER), m)?;
    if dst.exists() {
        std::fs::remove_dir_all(dst).map_err(io_at(dst, "replacing"))?;
    }
    std::fs::rename(&tmp, dst).map_err(io_at(dst, "placing"))?;
    Ok(())
}

/// Profile activation for the kinds whose engine reads a fixed folder (ShroudForge, SET). The profile's mods are copied
/// there; ShroudForge's switches follow the profile (`shroudforge::apply_switches`), and Kiln's copies of mods the
/// profile no longer lists are switched off (their settings stay). SET has no switch: Kiln's copies of switched-off
/// or unlisted SET mods are removed. A new version keeps the ShroudForge settings the player changed.
pub fn place(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path) -> Result<PlaceReport> {
    let mut rep = PlaceReport::default();
    for kind in [ModKind::ShroudForge, ModKind::Set] {
        let list = profile.mods.list(kind);
        let root = match kind {
            ModKind::ShroudForge => crate::shroudforge::mods_dir(game_dir),
            _ => game_dir.join(SET_MODS),
        };
        if list.is_empty() && !root.is_dir() {
            continue;
        }
        let tag = |id: &str| format!("{}:{id}", kind.as_str());
        let mut switches: Vec<(String, bool)> = Vec::new();
        for m in list {
            let dst = root.join(&m.id);
            let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
            let have = marker(&dst);
            if kind == ModKind::Set && !m.enabled {
                if have.is_some() {
                    std::fs::remove_dir_all(&dst).map_err(io_at(&dst, "removing"))?;
                    rep.removed.push(tag(&m.id));
                }
                continue;
            }
            match have {
                Some(h) if h.hash == m.hash && h.kind == kind => rep.kept.push(tag(&m.id)),
                None if dst.exists() => rep.own.push(tag(&m.id)),
                old => {
                    let src = crate::library::path_of(dirs, &crate::library::LibraryRef {
                        kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?;
                    if !src.is_dir() {
                        return Err(Error::not_found(format!("{} {} {} is not in the library", kind.as_str(), m.id, m.version)));
                    }
                    // a new version keeps the values the player set
                    let kept: Vec<(String, serde_json::Value)> = match (&old, kind) {
                        (Some(_), ModKind::ShroudForge) => crate::shroudforge::read_mod(&dst)
                            .map(|o| o.settings.into_iter().map(|s| (s.key, s.value)).collect())
                            .unwrap_or_default(),
                        _ => Vec::new(),
                    };
                    copy_in(&src, &dst, &want)?;
                    for (k, v) in kept {
                        let _ = crate::shroudforge::set_setting(&dst, &k, &v);
                    }
                    rep.placed.push(tag(&m.id));
                }
            }
            switches.push((m.id.clone(), m.enabled));
        }
        // Kiln's copies the profile does not list
        for e in std::fs::read_dir(&root).into_iter().flatten().flatten() {
            let name = e.file_name().to_string_lossy().into_owned();
            if name.starts_with('.') || list.iter().any(|m| m.id == name) {
                continue;
            }
            let Some(mk) = marker(&e.path()).filter(|mk| mk.kind == kind) else { continue };
            match kind {
                ModKind::Set => {
                    std::fs::remove_dir_all(e.path()).map_err(io_at(&e.path(), "removing"))?;
                    rep.removed.push(tag(&mk.id));
                }
                _ => switches.push((name, false)),
            }
        }
        if kind == ModKind::ShroudForge && !switches.is_empty() {
            for id in crate::shroudforge::apply_switches(game_dir, &switches)? {
                rep.switched.push(tag(&id));
            }
        }
    }
    Ok(rep)
}

// ---- servers: each engine's own folder (servers keep the legacy layout, no profiles) ----

/// Where a server's plugin for `kind` reads its mods without a profile (None: not synced to servers).
pub fn server_root(kind: ModKind, server: &Path) -> Option<PathBuf> {
    Some(match kind {
        ModKind::Emf => server.join("emf/mods"),
        ModKind::Eml => server.join("kiln/eml/mods"),
        ModKind::ShroudForge => crate::shroudforge::mods_dir(server),
        ModKind::Set => server.join(SET_MODS),
        ModKind::Greeneye => server.join("greeneye"),
        ModKind::Flame | ModKind::Shroudtopia => server.join("mods"),
        _ => return None,
    })
}

/// Where the launcher parks a switched-off engine mod on a server (`kiln/disabled/<kind>/<id>`): engines have no
/// common off switch, so the folder leaves the engine's own folder.
pub fn server_disabled_root(kind: ModKind, server: &Path) -> PathBuf {
    server.join("kiln/disabled").join(kind.as_str())
}

/// Is `dir` (in a root several engines share, like `<server>/mods`) a mod of `kind`?
pub fn folder_is(kind: ModKind, dir: &Path) -> bool {
    match kind {
        ModKind::Emf => dir.join("mod.json").is_file(),
        _ => crate::library::detect(dir).is_ok_and(|d| ModKind::of_detect(d.kind) == Some(kind)),
    }
}

/// id, version, name of an engine mod folder (mod.json `id`/`version`/`name`, else the folder name).
pub fn identity(dir: &Path) -> (String, Option<String>, Option<String>) {
    let folder = dir.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let m = read_json(&dir.join("mod.json"));
    let s = |k: &str| m.as_ref().and_then(|v| v.get(k)).and_then(|x| x.as_str()).map(str::to_owned);
    (s("id").filter(|i| crate::manifest::is_mod_id(i)).unwrap_or(folder), s("version"), s("name"))
}

/// What joining players need for an engine mod folder (its mod.json `clients`/`side`, else the kind's default).
pub fn clients_of(kind: ModKind, dir: &Path) -> (Side, Clients) {
    side_of(kind, dir)
}

// ---- settings per format ----

/// A settings screen for one mod: a JSON Schema for the launcher's SchemaForm and the current values.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineSettings {
    pub kind: ModKind,
    pub id: String,
    /// "extended.mod.json" | "shroudtopia.json" | "none"
    pub format: String,
    /// The file the values live in (host path); None when the mod has no settings file yet.
    pub file: Option<String>,
    pub schema: serde_json::Value,
    pub values: serde_json::Value,
    /// Why there is nothing to edit, for the launcher.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

/// `<configDir>/shroudtopia/shroudtopia.json`, as kiln-shroudtopia reads it.
pub fn shroudtopia_file(config_dir: &Path) -> PathBuf {
    config_dir.join("shroudtopia").join("shroudtopia.json")
}

/// A JSON Schema from example values (Shroudtopia declares no types: they come from the values).
pub fn schema_from_values(values: &serde_json::Value) -> serde_json::Value {
    use serde_json::{Value, json};
    match values {
        Value::Object(m) => {
            let props: serde_json::Map<String, Value> = m.iter().map(|(k, v)| (k.clone(), schema_from_values(v))).collect();
            json!({ "type": "object", "properties": props })
        }
        Value::Bool(_) => json!({ "type": "boolean" }),
        Value::Number(n) if n.is_i64() || n.is_u64() => json!({ "type": "integer" }),
        Value::Number(_) => json!({ "type": "number" }),
        Value::String(_) => json!({ "type": "string" }),
        Value::Array(_) => json!({ "type": "array" }),
        Value::Null => json!({}),
    }
}

/// The Shroudtopia key of a mod: the profile id, else its mod.json `name` (Shroudtopia's `GetMetaData().name`).
fn shroudtopia_key(doc: &serde_json::Value, id: &str, folder: Option<&Path>) -> String {
    let mods = doc.get("mods").and_then(|m| m.as_object());
    if mods.is_some_and(|m| m.contains_key(id)) {
        return id.to_string();
    }
    let name = folder.and_then(|f| read_json(&f.join("mod.json"))).and_then(|v| v.get("name").and_then(|n| n.as_str()).map(str::to_owned));
    match name {
        Some(n) if mods.is_some_and(|m| m.contains_key(&n)) => n,
        _ => id.to_string(),
    }
}

/// The settings of one mod of an engine kind. `game_dir` finds ShroudForge's copy, `config_dir` (the profile's config
/// folder) Shroudtopia's file, `folder` is the mod's library folder (defaults for a Shroudtopia mod without values yet).
pub fn settings(kind: ModKind, id: &str, game_dir: Option<&Path>, config_dir: &Path, folder: Option<&Path>) -> Result<EngineSettings> {
    let none = |note: &str| EngineSettings {
        kind, id: id.to_string(), format: "none".into(), file: None,
        schema: serde_json::json!({ "type": "object", "properties": {} }), values: serde_json::json!({}), note: Some(note.into()),
    };
    match kind {
        ModKind::ShroudForge => {
            let placed = game_dir.and_then(|g| placed_folder(kind, g, id)).filter(|p| p.is_dir());
            let Some(dir) = placed.or_else(|| folder.map(Path::to_path_buf)) else {
                return Ok(none("activate the profile once: ShroudForge's settings live in its mods folder"));
            };
            let m = crate::shroudforge::read_mod(&dir)?;
            Ok(EngineSettings {
                kind, id: id.to_string(), format: "extended.mod.json".into(),
                file: Some(dir.join("extended.mod.json").display().to_string()),
                schema: crate::shroudforge::settings_schema(&m), values: crate::shroudforge::settings_values(&m),
                note: (m.settings.is_empty()).then(|| "this mod declares no settings".into()),
            })
        }
        ModKind::Shroudtopia => {
            let file = shroudtopia_file(config_dir);
            let doc = read_json(&file).unwrap_or_else(|| serde_json::json!({ "mods": {} }));
            let key = shroudtopia_key(&doc, id, folder);
            let mut values = doc.pointer(&format!("/mods/{}", key.replace('~', "~0").replace('/', "~1"))).cloned();
            if values.is_none() {
                // the defaults its manifest declares (kiln-shroudtopia writes them at the first start)
                values = folder.and_then(|f| read_json(&f.join("mod.json"))).and_then(|v| v.pointer("/shroudtopia/default").cloned());
            }
            let values = values.filter(|v| v.is_object()).unwrap_or_else(|| serde_json::json!({ "active": true }));
            Ok(EngineSettings {
                kind, id: key, format: "shroudtopia.json".into(), file: Some(file.display().to_string()),
                schema: schema_from_values(&values), values, note: None,
            })
        }
        ModKind::Emf => Ok(none("EMF settings: use the EMF ini editor")),
        _ => Ok(none("this engine's mods declare no settings")),
    }
}

/// Saves one setting. ShroudForge: `shroudforge::set_setting` on the copy in its mods folder (a "live" setting applies
/// while the game runs). Shroudtopia: `mods.<key>.<setting>` in the profile's `shroudtopia.json` (the plugin reloads
/// the file when it changes). Values are checked against the settings' schema first.
pub fn set_setting(kind: ModKind, id: &str, key: &str, value: &serde_json::Value, game_dir: Option<&Path>, config_dir: &Path,
                   folder: Option<&Path>) -> Result<EngineSettings> {
    match kind {
        ModKind::ShroudForge => {
            let dir = game_dir.and_then(|g| placed_folder(kind, g, id)).filter(|p| p.is_dir())
                .ok_or_else(|| Error::not_found(format!("ShroudForge mod {id} is not in ShroudForge's mods folder: activate the profile first")))?;
            crate::shroudforge::set_setting(&dir, key, value)?;
        }
        ModKind::Shroudtopia => {
            let cur = settings(kind, id, game_dir, config_dir, folder)?;
            if let Some(old) = cur.values.get(key)
                && std::mem::discriminant(old) != std::mem::discriminant(value)
                && !(old.is_number() && value.is_number())
            {
                return Err(Error::invalid(format!("{key}: expected the same type as {old}")));
            }
            let file = shroudtopia_file(config_dir);
            let mut doc = read_json(&file).unwrap_or_else(|| serde_json::json!({}));
            if !doc.is_object() {
                doc = serde_json::json!({});
            }
            let obj = doc.as_object_mut().expect("object");
            let mods = obj.entry("mods").or_insert_with(|| serde_json::json!({}));
            if !mods.is_object() {
                *mods = serde_json::json!({});
            }
            let entry = mods.as_object_mut().expect("object").entry(cur.id.clone()).or_insert_with(|| cur.values.clone());
            if !entry.is_object() {
                *entry = cur.values.clone();
            }
            entry.as_object_mut().expect("object").insert(key.to_string(), value.clone());
            if let Some(p) = file.parent() {
                std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?;
            }
            crate::util::write_json_atomic(&file, &doc)?;
        }
        _ => return Err(Error::new(ErrorCode::Unsupported, format!("{} mods have no settings Kiln can edit", kind.label()))),
    }
    settings(kind, id, game_dir, config_dir, folder)
}

// ---- status ----

/// One engine plugin's state file.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EngineStatus {
    pub kind: ModKind,
    pub plugin: String,
    /// `kiln/state/<engine>.json` as written by the plugin (None: the plugin has not run in this game yet).
    pub state: Option<serde_json::Value>,
    /// The file's host path.
    pub file: String,
}

/// The state file name of a kind's plugin.
pub fn state_file(kind: ModKind) -> Option<&'static str> {
    Some(match kind {
        ModKind::Emf => "emf.json",
        ModKind::Eml => "eml.json",
        ModKind::ShroudForge => "shroudforge.json",
        ModKind::Greeneye => "greeneye.json",
        ModKind::Flame => "flame.json",
        ModKind::Shroudtopia => "shroudtopia.json",
        ModKind::Set => "set.json",
        _ => return None,
    })
}

/// Every engine plugin's state in `<game>/kiln/state/` (EMF, EML, ShroudForge, Greeneye, Flame, Shroudtopia, SET).
pub fn status(game_dir: &Path) -> Vec<EngineStatus> {
    ModKind::ALL
        .into_iter()
        .filter_map(|k| {
            let f = game_dir.join("kiln").join("state").join(state_file(k)?);
            Some(EngineStatus {
                kind: k,
                plugin: plugin_of(k).unwrap_or_default().to_string(),
                state: crate::util::read_json::<serde_json::Value>(&f).ok().flatten(),
                file: f.display().to_string(),
            })
        })
        .collect()
}

// ---- EML's vanilla backup ----

/// EML's backup of one game data file.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EmlBackup {
    /// `<exe>.kfc`
    pub file: String,
    /// `<exe>.kfc.bak`
    pub backup: String,
    /// The game's file differs from the backup (patched by EML or kiln-eml).
    pub patched: bool,
}

/// EML's `<exe>.kfc.bak` backups in the game folder (client `enshrouded`, server `enshrouded_server`).
pub fn eml_backup(game_dir: &Path) -> Vec<EmlBackup> {
    let mut out = Vec::new();
    for exe in ["enshrouded", "enshrouded_server"] {
        let file = game_dir.join(format!("{exe}.kfc"));
        let bak = game_dir.join(format!("{exe}.kfc.bak"));
        if !bak.is_file() || !file.is_file() {
            continue;
        }
        let same = match (crate::hash::sha256_file(&file), crate::hash::sha256_file(&bak)) {
            (Ok(a), Ok(b)) => a.0 == b.0,
            _ => false,
        };
        out.push(EmlBackup { file: file.display().to_string(), backup: bak.display().to_string(), patched: !same });
    }
    out
}

/// "Restore vanilla game data": copies each patched `<exe>.kfc` back from EML's `.kfc.bak`, byte for byte (what
/// kiln-eml does itself when no EML mod is left). The caller refuses while the game runs. Returns the files restored.
pub fn eml_restore_vanilla(game_dir: &Path) -> Result<Vec<String>> {
    let mut done = Vec::new();
    for b in eml_backup(game_dir).into_iter().filter(|b| b.patched) {
        let bytes = std::fs::read(&b.backup).map_err(io_at(Path::new(&b.backup), "reading"))?;
        crate::util::write_atomic(Path::new(&b.file), &bytes)?;
        done.push(b.file);
    }
    Ok(done)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn schema_from_values_types() {
        let s = schema_from_values(&serde_json::json!({ "active": true, "zoom": 2, "alpha": 0.5, "title": "x" }));
        assert_eq!(s["properties"]["active"]["type"], "boolean");
        assert_eq!(s["properties"]["zoom"]["type"], "integer");
        assert_eq!(s["properties"]["alpha"]["type"], "number");
        assert_eq!(s["properties"]["title"]["type"], "string");
    }

    #[test]
    fn every_imported_kind_has_a_plugin_and_state_file() {
        for k in IMPORTED {
            assert!(plugin_of(k).is_some(), "{k:?}");
            assert!(state_file(k).is_some(), "{k:?}");
            assert!(k.is_engine() && !k.is_reserved());
        }
    }
}
