//! Offline mod configs: the values files in a config folder (a profile's `config/`, a server's `kiln/config`), their
//! schema snapshots in `.schema/` (schema/config-schema.schema.json; written by Kiln.Api Config.Register and by
//! kiln-emf), and the native mod user state `<configDir>/native.json` (schema/native-state.schema.json).
//! Edits change only value tokens (jsonc), so comments survive; validation mirrors Kiln.Api's (types, min/max,
//! options). Running servers use the admin ops config_get/config_set instead.

use crate::jsonc;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub const SCHEMA_DIR: &str = ".schema";
pub const NATIVE_STATE: &str = "native.json";

/// Which config folder (Tauri commands): `{"profile": id}` | `{"server": id}` | `{"game": id}` (kiln/config).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConfigTarget {
    Profile(String),
    Server(String),
    Game(String),
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SnapshotSource {
    Kiln,
    Emf,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ValueFormat {
    #[default]
    Jsonc,
    Ini,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotSection {
    pub path: String,
    #[serde(default)]
    pub comment: Option<String>,
    #[serde(default)]
    pub synced: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SnapshotEntry {
    pub path: String,
    /// bool | int | float | string | enum | duration | list<...>
    #[serde(rename = "type")]
    pub kind: String,
    #[serde(default)]
    pub comment: Option<String>,
    #[serde(default)]
    pub default: serde_json::Value,
    #[serde(default)]
    pub requires_restart: bool,
    #[serde(default)]
    pub synced: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max: Option<f64>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub options: Vec<String>,
}

/// `<configDir>/.schema/<file>.json`
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SchemaSnapshot {
    pub format: u32,
    pub source: SnapshotSource,
    pub file: String,
    #[serde(default)]
    pub value_format: ValueFormat,
    pub id: String,
    #[serde(rename = "mod")]
    pub mod_id: String,
    #[serde(default)]
    pub mod_version: Option<String>,
    #[serde(default)]
    pub kind: Option<String>,
    #[serde(default)]
    pub written: Option<String>,
    #[serde(default)]
    pub kiln: Option<String>,
    #[serde(default)]
    pub sections: Vec<SnapshotSection>,
    pub entries: Vec<SnapshotEntry>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeModSwitch {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub enabled: Option<bool>,
}

/// `<configDir>/native.json`
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeUserState {
    pub format: u32,
    #[serde(default)]
    pub mods: BTreeMap<String, NativeModSwitch>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigFileInfo {
    /// Relative to the config folder, '/' separated.
    pub file: String,
    #[serde(rename = "mod")]
    pub mod_id: Option<String>,
    /// "kiln" | "emf" | "native" | "other"
    pub kind: String,
    pub has_schema: bool,
    pub mod_version: Option<String>,
    pub modified: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ValidationError {
    pub path: String,
    pub message: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConfigDocument {
    pub file: String,
    /// The file as it is on disk (JSONC or INI).
    pub text: String,
    /// The values as JSON (comments dropped).
    pub values: serde_json::Value,
    pub schema: Option<SchemaSnapshot>,
    pub errors: Vec<ValidationError>,
}

/// `.schema/` + `file` with its extension replaced by `.json` (my-mod.json -> .schema/my-mod.json,
/// emf/beastmaster.ini -> .schema/emf/beastmaster.json).
pub fn snapshot_path(config_dir: &Path, file: &str) -> PathBuf {
    let rel = Path::new(file).with_extension("json");
    config_dir.join(SCHEMA_DIR).join(rel)
}

// ------------------------------------------------------------------------------------------------ files ---------

/// A config file name relative to the config folder: '/' separated, no '..', not absolute, no dot-folders.
fn safe_rel(file: &str) -> Result<PathBuf> {
    let ok = !file.is_empty()
        && !file.starts_with(['/', '\\'])
        && !file.contains(['\\', ':', '\0'])
        && file.split('/').all(|c| !c.is_empty() && c != "." && c != ".." && !c.starts_with('.'));
    if !ok {
        return Err(Error::invalid(format!("'{file}' is not a config file name (relative, '/' separated, no '..')")));
    }
    Ok(file.split('/').collect())
}

/// Writes `<path>.tmp` next to the file, flushes it, renames it over the file.
pub(crate) fn atomic_write(path: &Path, bytes: &[u8]) -> Result<()> {
    use std::io::Write;
    if let Some(dir) = path.parent() {
        std::fs::create_dir_all(dir)?;
    }
    let mut tmp = path.as_os_str().to_owned();
    tmp.push(".tmp");
    let tmp = PathBuf::from(tmp);
    let res = (|| -> std::io::Result<()> {
        let mut f = std::fs::File::create(&tmp)?;
        f.write_all(bytes)?;
        f.sync_all()?;
        drop(f);
        std::fs::rename(&tmp, path)
    })();
    if let Err(e) = res {
        let _ = std::fs::remove_file(&tmp);
        return Err(Error::from(e).with_detail(path.display().to_string()));
    }
    Ok(())
}

fn read_json_file(path: &Path) -> Result<Option<serde_json::Value>> {
    let bytes = match std::fs::read(path) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(Error::from(e).with_detail(path.display().to_string())),
    };
    let (_, node) = jsonc::parse_tree_bytes(&bytes)
        .map_err(|e| Error::new(ErrorCode::Parse, format!("{}: not valid JSON ({e})", path.display())))?;
    Ok(Some(node.to_value()))
}

fn check_format(v: &serde_json::Value, what: &str) -> Result<()> {
    match v.get("format").and_then(|f| f.as_u64()) {
        Some(n) if n > 1 => Err(Error::new(ErrorCode::Schema, format!("{what}: format {n} is newer than this version understands (1)"))),
        _ => Ok(()),
    }
}

/// The schema snapshot of `file`, None when there is none.
pub fn read_snapshot(config_dir: &Path, file: &str) -> Result<Option<SchemaSnapshot>> {
    safe_rel(file)?;
    let path = snapshot_path(config_dir, file);
    let Some(v) = read_json_file(&path)? else { return Ok(None) };
    check_format(&v, &path.display().to_string())?;
    serde_json::from_value(v)
        .map(Some)
        .map_err(|e| Error::new(ErrorCode::Schema, format!("{}: {e}", path.display())))
}

fn rfc3339(t: std::time::SystemTime) -> String {
    let secs = match t.duration_since(std::time::UNIX_EPOCH) {
        Ok(d) => d.as_secs() as i64,
        Err(e) => -(e.duration().as_secs() as i64),
    };
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    // Howard Hinnant's civil_from_days
    let z = days + 719_468;
    let era = (if z >= 0 { z } else { z - 146_096 }) / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = yoe + era * 400 + i64::from(m <= 2);
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, rem / 60 % 60, rem % 60)
}

fn modified(path: &Path) -> Option<String> {
    std::fs::metadata(path).ok()?.modified().ok().map(rfc3339)
}

fn walk(dir: &Path, prefix: &str, out: &mut Vec<String>) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let name = e.file_name().to_string_lossy().into_owned();
        if name.starts_with('.') || name.ends_with(".tmp") {
            continue;
        }
        let rel = if prefix.is_empty() { name.clone() } else { format!("{prefix}/{name}") };
        let Ok(ft) = e.file_type() else { continue };
        if ft.is_dir() {
            walk(&e.path(), &rel, out);
        } else if ft.is_file() && (rel.ends_with(".json") || rel.ends_with(".ini")) {
            out.push(rel);
        }
    }
}

fn walk_snapshots(dir: &Path, out: &mut Vec<PathBuf>) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let Ok(ft) = e.file_type() else { continue };
        if ft.is_dir() {
            walk_snapshots(&e.path(), out);
        } else if e.path().extension().is_some_and(|x| x == "json") {
            out.push(e.path());
        }
    }
}

/// The config files in a config folder (values files, `native.json`, and files a snapshot announces that do not
/// exist yet), sorted by name.
pub fn list(config_dir: &Path) -> Result<Vec<ConfigFileInfo>> {
    let mut files = Vec::new();
    walk(config_dir, "", &mut files);
    let mut out: Vec<ConfigFileInfo> = Vec::new();
    for file in files {
        let path = config_dir.join(&file);
        if file == NATIVE_STATE {
            out.push(ConfigFileInfo { file, mod_id: None, kind: "native".into(), has_schema: false, mod_version: None,
                modified: modified(&path) });
            continue;
        }
        let snap = read_snapshot(config_dir, &file).ok().flatten();
        let stem = Path::new(&file).file_stem().map(|s| s.to_string_lossy().into_owned());
        let (kind, mod_id) = match &snap {
            Some(s) => (if s.source == SnapshotSource::Emf { "emf" } else { "kiln" }.to_string(), Some(s.mod_id.clone())),
            None if file.starts_with("emf/") && file.ends_with(".ini") => ("emf".into(), stem),
            None if file.ends_with(".json") => ("kiln".into(), None),
            None => ("other".into(), None),
        };
        out.push(ConfigFileInfo {
            file,
            mod_id,
            kind,
            has_schema: snap.is_some(),
            mod_version: snap.and_then(|s| s.mod_version),
            modified: modified(&path),
        });
    }
    let mut snaps = Vec::new();
    walk_snapshots(&config_dir.join(SCHEMA_DIR), &mut snaps);
    for p in snaps {
        let Ok(Some(v)) = read_json_file(&p) else { continue };
        let Ok(s) = serde_json::from_value::<SchemaSnapshot>(v) else { continue };
        if safe_rel(&s.file).is_err() || out.iter().any(|f| f.file == s.file) || snapshot_path(config_dir, &s.file) != p {
            continue;
        }
        out.push(ConfigFileInfo {
            file: s.file.clone(),
            mod_id: Some(s.mod_id.clone()),
            kind: if s.source == SnapshotSource::Emf { "emf" } else { "kiln" }.into(),
            has_schema: true,
            mod_version: s.mod_version.clone(),
            modified: None,
        });
    }
    out.sort_by(|a, b| a.file.cmp(&b.file));
    Ok(out)
}

fn value_format(file: &str, snapshot: Option<&SchemaSnapshot>) -> ValueFormat {
    match snapshot {
        Some(s) => s.value_format,
        None if file.ends_with(".ini") => ValueFormat::Ini,
        None => ValueFormat::Jsonc,
    }
}

/// The values of a file's text: (values for validation, values for the document) and syntax problems.
fn values_of(text: &str, format: ValueFormat, snapshot: Option<&SchemaSnapshot>) -> (serde_json::Value, serde_json::Value, Vec<ValidationError>) {
    match format {
        ValueFormat::Jsonc => match jsonc::parse_tree(text) {
            Ok(n) if n.is_object() => {
                let v = n.to_value();
                (v.clone(), v, Vec::new())
            }
            Ok(n) => (n.to_value(), n.to_value(), vec![ValidationError { path: String::new(), message: "the file must hold one { ... } object".into() }]),
            Err(e) => (serde_json::Value::Null, serde_json::Value::Null, vec![ValidationError {
                path: String::new(),
                message: format!("line {}: {}", e.line + 1, e.message),
            }]),
        },
        ValueFormat::Ini => {
            let raw = ini::values(text);
            let typed = match snapshot {
                Some(s) => ini::typed(&raw, s),
                None => raw.clone(),
            };
            (raw, typed, Vec::new())
        }
    }
}

fn document(config_dir: &Path, file: &str, text: String) -> Result<ConfigDocument> {
    let (schema, mut errors) = match read_snapshot(config_dir, file) {
        Ok(s) => (s, Vec::new()),
        Err(e) => (None, vec![ValidationError { path: SCHEMA_DIR.into(), message: e.message }]),
    };
    let format = value_format(file, schema.as_ref());
    let (check, values, syntax) = values_of(&text, format, schema.as_ref());
    let broken = !syntax.is_empty();
    errors.extend(syntax);
    if let Some(s) = &schema
        && !broken
    {
        errors.extend(validate(s, &check));
    }
    Ok(ConfigDocument { file: file.to_string(), text, values, schema, errors })
}

pub fn read(config_dir: &Path, file: &str) -> Result<ConfigDocument> {
    let path = config_dir.join(safe_rel(file)?);
    let text = std::fs::read(&path)
        .map_err(|e| Error::from(e).with_detail(path.display().to_string()))
        .map(|b| crate::manifest::decode_text(&b))?;
    document(config_dir, file, text)
}

fn find_entry<'s>(s: &'s SchemaSnapshot, path: &str) -> Option<&'s SnapshotEntry> {
    s.entries.iter().find(|e| e.path == path).or_else(|| s.entries.iter().find(|e| jsonc::eq_ci(&e.path, path)))
}

fn schema_error(file: &str, errors: Vec<ValidationError>) -> Error {
    let first = errors.first().map(|e| e.message.clone()).unwrap_or_default();
    let detail = serde_json::to_value(&errors).unwrap_or_default();
    Error::new(ErrorCode::Schema, format!("{file}: {first}")).with_detail(detail)
}

fn current_text(path: &Path, format: ValueFormat) -> Result<String> {
    match std::fs::read(path) {
        Ok(b) => Ok(crate::manifest::decode_text(&b)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(match format {
            ValueFormat::Jsonc => "{\n}\n".into(),
            ValueFormat::Ini => String::new(),
        }),
        Err(e) => Err(Error::from(e).with_detail(path.display().to_string())),
    }
}

/// Applies value edits (path, canonical JSON value; None = remove) to the text of a values file.
fn edit_text(file: &str, text: &str, format: ValueFormat, edits: &[(String, Option<serde_json::Value>)]) -> Result<String> {
    match format {
        ValueFormat::Jsonc => {
            let mut doc = jsonc::ConfigDoc::parse_ci(text.to_string())
                .map_err(|e| Error::new(ErrorCode::Parse, format!("{file}: cannot edit: {}", e.message)))?;
            if !doc.root().is_object() {
                return Err(Error::new(ErrorCode::Schema, format!("{file}: the file must hold one {{ ... }} object")));
            }
            for (path, v) in edits {
                let segs = jsonc::split_path(path);
                match v {
                    Some(v) => doc.set(&segs, v)?,
                    None => {
                        doc.remove(&segs)?;
                    }
                }
            }
            Ok(doc.into_text())
        }
        ValueFormat::Ini => {
            let mut t = text.to_string();
            for (path, v) in edits {
                t = match v {
                    Some(v) => ini::set(&t, path, &ini::text_of(v)),
                    None => ini::remove(&t, path),
                };
            }
            Ok(t)
        }
    }
}

/// Sets one value (dotted `path`) keeping comments; validates against the snapshot first (Kiln.Api's
/// `FromObject` rules: out of range is an error, text forms such as "on" or "5m" are accepted and written in
/// Kiln's canonical form). With a snapshot, a path it does not list is refused.
pub fn set(config_dir: &Path, file: &str, path: &str, value: &serde_json::Value) -> Result<ConfigDocument> {
    let fpath = config_dir.join(safe_rel(file)?);
    let schema = read_snapshot(config_dir, file)?;
    let format = value_format(file, schema.as_ref());
    let (path, canonical) = match &schema {
        Some(s) => {
            let Some(entry) = find_entry(s, path) else {
                return Err(Error::new(ErrorCode::Schema, format!("{file}: the config has no entry '{path}'"))
                    .with_detail(s.entries.iter().map(|e| e.path.clone()).collect::<Vec<_>>()));
            };
            let v = check_value(entry, value)
                .map_err(|m| schema_error(file, vec![ValidationError { path: entry.path.clone(), message: m }]))?;
            (entry.path.clone(), v)
        }
        None => (path.to_string(), value.clone()),
    };
    if jsonc::split_path(&path).is_empty() {
        return Err(Error::invalid("an empty config path"));
    }
    let text = current_text(&fpath, format)?;
    let out = edit_text(file, &text, format, &[(path, Some(canonical))])?;
    atomic_write(&fpath, out.as_bytes())?;
    document(config_dir, file, out)
}

/// Replaces the whole text (raw editor) after validating it: syntax (JSONC) and the snapshot's rules.
pub fn write(config_dir: &Path, file: &str, text: &str) -> Result<ConfigDocument> {
    let fpath = config_dir.join(safe_rel(file)?);
    let schema = read_snapshot(config_dir, file)?;
    let format = value_format(file, schema.as_ref());
    if format == ValueFormat::Jsonc {
        let root = jsonc::parse_tree(text).map_err(|e| Error::new(ErrorCode::Parse, format!("{file}: not valid JSON ({e})")))?;
        if !root.is_object() {
            return Err(Error::new(ErrorCode::Schema, format!("{file}: the file must hold one {{ ... }} object")));
        }
    }
    if let Some(s) = &schema {
        let (check, _, _) = values_of(text, format, Some(s));
        let errors = validate(s, &check);
        if !errors.is_empty() {
            return Err(schema_error(file, errors));
        }
    }
    atomic_write(&fpath, text.as_bytes())?;
    document(config_dir, file, text.to_string())
}

/// Back to the default: one value, or the whole file when `path` is None. With a snapshot the defaults are written
/// in place (comments kept); without one the member is removed (Kiln then uses its default), and a whole-file reset
/// deletes the file (Kiln writes it again from the defaults).
pub fn reset(config_dir: &Path, file: &str, path: Option<&str>) -> Result<ConfigDocument> {
    let fpath = config_dir.join(safe_rel(file)?);
    let schema = read_snapshot(config_dir, file)?;
    let format = value_format(file, schema.as_ref());
    let edits: Vec<(String, Option<serde_json::Value>)> = match (path, &schema) {
        (Some(p), Some(s)) => {
            let Some(entry) = find_entry(s, p) else {
                return Err(Error::new(ErrorCode::Schema, format!("{file}: the config has no entry '{p}'")));
            };
            vec![(entry.path.clone(), Some(entry.default.clone()))]
        }
        (Some(p), None) => vec![(p.to_string(), None)],
        (None, Some(s)) => s.entries.iter().map(|e| (e.path.clone(), Some(e.default.clone()))).collect(),
        (None, None) => {
            match std::fs::remove_file(&fpath) {
                Ok(()) => {}
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
                Err(e) => return Err(Error::from(e).with_detail(fpath.display().to_string())),
            }
            return Ok(ConfigDocument { file: file.to_string(), text: String::new(), values: serde_json::Value::Null,
                schema: None, errors: Vec::new() });
        }
    };
    let text = current_text(&fpath, format)?;
    let out = edit_text(file, &text, format, &edits)?;
    if out != text || !fpath.exists() {
        atomic_write(&fpath, out.as_bytes())?;
    }
    document(config_dir, file, out)
}

/// The value at a dotted path of `values`, keys compared case-insensitively (the last match, as Kiln's reader).
fn lookup<'v>(values: &'v serde_json::Value, path: &str) -> Lookup<'v> {
    let mut cur = values;
    let mut walked = String::new();
    for seg in path.split('.') {
        let Some(obj) = cur.as_object() else {
            return Lookup::NotSection(walked);
        };
        let found = obj.get(seg).or_else(|| obj.iter().rev().find(|(k, _)| jsonc::eq_ci(k, seg)).map(|(_, v)| v));
        match found {
            Some(v) => cur = v,
            None => return Lookup::Missing,
        }
        if !walked.is_empty() {
            walked.push('.');
        }
        walked.push_str(seg);
    }
    Lookup::Found(cur)
}

enum Lookup<'v> {
    Found(&'v serde_json::Value),
    Missing,
    /// A section on the way is not an object (its path).
    NotSection(String),
}

/// Checks `values` (a values file as JSON) against a snapshot the way Kiln.Api's `FromObject` would accept them:
/// types, ranges, options, durations; a missing value is fine (the default applies). Messages are Kiln's.
pub fn validate(snapshot: &SchemaSnapshot, values: &serde_json::Value) -> Vec<ValidationError> {
    let mut out = Vec::new();
    if !values.is_object() {
        out.push(ValidationError { path: String::new(), message: "the file must hold one { ... } object".into() });
        return out;
    }
    let mut bad_sections = std::collections::BTreeSet::new();
    for s in &snapshot.sections {
        if let Lookup::Found(v) = lookup(values, &s.path)
            && !v.is_object()
        {
            out.push(ValidationError { path: s.path.clone(), message: format!("\"{}\" must be a section ({{ ... }}); using its defaults", s.path) });
            bad_sections.insert(s.path.clone());
        }
    }
    for e in &snapshot.entries {
        match lookup(values, &e.path) {
            Lookup::Found(v) => {
                if let Err(m) = check_value(e, v) {
                    out.push(ValidationError { path: e.path.clone(), message: m });
                }
            }
            Lookup::Missing => {}
            Lookup::NotSection(sec) => {
                if !bad_sections.contains(&sec) && !sec.is_empty() {
                    out.push(ValidationError { path: sec.clone(), message: format!("\"{sec}\" must be a section ({{ ... }}); using its defaults") });
                    bad_sections.insert(sec);
                }
            }
        }
    }
    out
}

pub fn read_native_state(config_dir: &Path) -> Result<NativeUserState> {
    let path = config_dir.join(NATIVE_STATE);
    let Some(v) = read_json_file(&path)? else { return Ok(NativeUserState { format: 1, mods: BTreeMap::new() }) };
    check_format(&v, &path.display().to_string())?;
    let mut s: NativeUserState =
        serde_json::from_value(v).map_err(|e| Error::new(ErrorCode::Schema, format!("{}: {e}", path.display())))?;
    if s.format == 0 {
        s.format = 1;
    }
    Ok(s)
}

pub fn write_native_state(config_dir: &Path, state: &NativeUserState) -> Result<()> {
    if let Some(id) = state.mods.keys().find(|id| !crate::manifest::is_mod_id(id) || id.ends_with('\n')) {
        return Err(Error::invalid(format!("'{id}' is not a valid mod id")));
    }
    let mut s = state.clone();
    s.format = 1;
    let text = serde_json::to_string_pretty(&s)? + "\n";
    atomic_write(&config_dir.join(NATIVE_STATE), text.as_bytes())
}

// ------------------------------------------------------------------------------------------------ values --------

/// ConfigValueKind as a snapshot spells it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ValueKind {
    Bool,
    Int,
    Float,
    String,
    Enum,
    Duration,
}

impl ValueKind {
    fn parse(s: &str) -> Option<ValueKind> {
        Some(match s {
            "bool" => ValueKind::Bool,
            "int" => ValueKind::Int,
            "float" => ValueKind::Float,
            "string" => ValueKind::String,
            "enum" => ValueKind::Enum,
            "duration" => ValueKind::Duration,
            _ => return None,
        })
    }

    /// ConfigSchema.KindName
    pub fn name(self) -> &'static str {
        match self {
            ValueKind::Bool => "bool",
            ValueKind::Int => "int",
            ValueKind::Float => "float",
            ValueKind::String => "string",
            ValueKind::Enum => "enum",
            ValueKind::Duration => "duration",
        }
    }
}

/// An entry's type: a scalar kind, or a list of one (`list<int>`). None for a type this version does not know.
pub fn entry_kind(e: &SnapshotEntry) -> Option<(ValueKind, bool)> {
    if let Some(inner) = e.kind.strip_prefix("list<").and_then(|r| r.strip_suffix('>')) {
        return ValueKind::parse(inner).map(|k| (k, true));
    }
    ValueKind::parse(&e.kind).map(|k| (k, false))
}

/// Checks one value like Kiln.Api's `ConfigSchema.FromObject` (a JSON value set through the API or the
/// dashboard): Ok(the value in Kiln's canonical JSON form) or Err(Kiln's message). Unknown types pass unchanged.
pub fn check_value(e: &SnapshotEntry, value: &serde_json::Value) -> std::result::Result<serde_json::Value, String> {
    let Some((kind, list)) = entry_kind(e) else { return Ok(value.clone()) };
    if !list {
        return scalar_from_json(e, kind, value, &e.path, false).map(|(v, _)| v);
    }
    let Some(items) = value.as_array() else {
        return Err(format!("\"{}\" must be a list ([...]); using the default", e.path));
    };
    let mut out = Vec::new();
    let mut warnings = Vec::new();
    for (i, item) in items.iter().enumerate() {
        match scalar_from_json(e, kind, item, &format!("{}[{i}]", e.path), true) {
            Ok((v, problem)) => {
                warnings.extend(problem);
                out.push(v);
            }
            Err(problem) => warnings.push(format!("{problem}; dropped")),
        }
    }
    match warnings.into_iter().next() {
        Some(w) => Err(w),
        None => Ok(serde_json::Value::Array(out)),
    }
}

/// Checks a value given as text (an INI value) like `ConfigSchema.ParseText`.
pub fn check_text(e: &SnapshotEntry, text: &str) -> std::result::Result<serde_json::Value, String> {
    check_value(e, &serde_json::Value::String(text.to_string()))
}

fn raw_json(v: &serde_json::Value) -> String {
    match v {
        serde_json::Value::Number(n) => n.to_string(),
        other => serde_json::to_string(other).unwrap_or_default(),
    }
}

/// ConfigSchema.Short: the first 40 characters.
fn short(s: &str) -> String {
    let units: Vec<u16> = s.encode_utf16().collect();
    if units.len() > 40 { format!("{}...", String::from_utf16_lossy(&units[..40])) } else { s.to_string() }
}

/// ScalarFromJson: Ok((canonical value, clamp warning)) or Err(problem).
fn scalar_from_json(e: &SnapshotEntry, kind: ValueKind, v: &serde_json::Value, path: &str, clamp: bool)
    -> std::result::Result<(serde_json::Value, Option<String>), String> {
    use serde_json::Value;
    match (v, kind) {
        (Value::Bool(b), ValueKind::Bool) => Ok((Value::Bool(*b), None)),
        (Value::Number(n), ValueKind::Int | ValueKind::Float) => check_number(e, kind, n.as_f64().unwrap_or(f64::NAN), path, clamp),
        (Value::Number(n), ValueKind::Duration) => {
            check_duration(e, durations::ticks_from_seconds(n.as_f64().unwrap_or(f64::NAN)), path, clamp)
        }
        (Value::String(s), _) => parse_text(e, kind, s, path, clamp),
        (Value::Number(_) | Value::Bool(_), ValueKind::String) => Ok((Value::String(raw_json(v)), None)),
        (Value::Number(n), ValueKind::Enum) => {
            // Enum.IsDefined: a snapshot has the names only; default numbering (0, 1, ...) is assumed
            match n.as_i64().filter(|&i| i >= 0 && (i as usize) < e.options.len()) {
                Some(i) => Ok((Value::String(e.options[i as usize].clone()), None)),
                None => Err(format!("\"{path}\" = {} is not one of {}", raw_json(v), e.options.join(", "))),
            }
        }
        _ => Err(format!("\"{path}\" = {} is not a {}", short(&raw_json(v)), kind.name())),
    }
}

fn parse_text(e: &SnapshotEntry, kind: ValueKind, s: &str, path: &str, clamp: bool)
    -> std::result::Result<(serde_json::Value, Option<String>), String> {
    use serde_json::Value;
    let t = s.trim();
    match kind {
        ValueKind::String => Ok((Value::String(s.to_string()), None)),
        ValueKind::Bool => match t.to_lowercase().as_str() {
            "true" | "on" | "yes" | "1" => Ok((Value::Bool(true), None)),
            "false" | "off" | "no" | "0" => Ok((Value::Bool(false), None)),
            _ => Err(format!("\"{path}\" = \"{}\" is not true/false", short(s))),
        },
        ValueKind::Int | ValueKind::Float => match parse_double(t) {
            Some(d) => check_number(e, kind, d, path, clamp),
            None => Err(format!("\"{path}\" = \"{}\" is not a number", short(s))),
        },
        ValueKind::Enum => match e.options.iter().find(|o| jsonc::eq_ci(o, t)) {
            Some(o) => Ok((Value::String(o.clone()), None)),
            None => Err(format!("\"{path}\" = \"{}\" is not one of {}", short(s), e.options.join(", "))),
        },
        ValueKind::Duration => match durations::parse(t) {
            Some(ticks) => check_duration(e, Some(ticks), path, clamp),
            None => Err(format!("\"{path}\" = \"{}\" is not a duration (e.g. \"90s\", \"5m\", \"1h30m\", \"00:05:00\")", short(s))),
        },
    }
}

/// double.TryParse(s, NumberStyles.Float, InvariantCulture).
fn parse_double(t: &str) -> Option<f64> {
    let t = t.trim_matches(|c: char| c.is_whitespace());
    let body = t.strip_prefix(['+', '-']).unwrap_or(t);
    match body {
        "Infinity" | "∞" => return Some(if t.starts_with('-') { f64::NEG_INFINITY } else { f64::INFINITY }),
        "NaN" => return Some(f64::NAN),
        _ => {}
    }
    if body.is_empty() || !body.bytes().all(|b| b.is_ascii_digit() || matches!(b, b'.' | b'e' | b'E' | b'+' | b'-')) {
        return None;
    }
    t.parse().ok()
}

/// Math.Round (banker's rounding).
fn round_even(d: f64) -> f64 {
    let r = d.round();
    if (d - d.trunc()).abs() == 0.5 { 2.0 * (d / 2.0).round() } else { r }
}

/// `d.ToString(format "0.######")`-style: at most `places` decimals, no trailing zeros.
fn fixed(d: f64, places: i32) -> String {
    let f = 10f64.powi(places);
    let r = (d * f).round() / f;
    let mut s = format!("{:.*}", places as usize, r);
    if s.contains('.') {
        s = s.trim_end_matches('0').trim_end_matches('.').to_string();
    }
    if s == "-0" { "0".into() } else { s }
}

/// ConfigSchema.Num
fn num(d: f64) -> String {
    if d >= 9e18 {
        "max".into()
    } else if d <= -9e18 {
        "min".into()
    } else {
        fixed(d, 6)
    }
}

/// double.ToString() (shortest round trip).
fn double_text(d: f64) -> String {
    format!("{d}")
}

/// CheckNumber. `int` entries are taken as C# `int` (the usual config type), `float` as `double`.
fn check_number(e: &SnapshotEntry, kind: ValueKind, d: f64, path: &str, clamp: bool)
    -> std::result::Result<(serde_json::Value, Option<String>), String> {
    if !d.is_finite() {
        return Err(format!("\"{path}\" must be a finite number"));
    }
    let (lo, hi) = if kind == ValueKind::Int { (i32::MIN as f64, i32::MAX as f64) } else { (f64::MIN, f64::MAX) };
    let mut min = e.min.unwrap_or(f64::NEG_INFINITY).max(lo);
    let mut max = e.max.unwrap_or(f64::INFINITY).min(hi);
    let mut d = d;
    let mut warning = None;
    if kind == ValueKind::Int {
        if (d - d.round()).abs() > 1e-9 {
            if !clamp {
                return Err(format!("\"{path}\" must be a whole number"));
            }
            warning = Some(format!("\"{path}\" = {} is not a whole number; using {}", double_text(d), double_text(round_even(d))));
            d = round_even(d);
        }
        min = min.ceil();
        max = max.floor();
    }
    if d < min || d > max {
        let range = format!("{}..{}", num(e.min.unwrap_or(lo)), num(e.max.unwrap_or(hi)));
        if !clamp {
            return Err(format!("\"{path}\" must be in {range} (got {})", num(d)));
        }
        let c = d.clamp(min, max);
        warning = Some(format!("\"{path}\" = {} is outside {range}; using {}", num(d), num(c)));
        d = c;
    }
    let v = if kind == ValueKind::Int {
        serde_json::Value::from(round_even(d) as i64)
    } else if d.fract() == 0.0 && d.abs() < 1e15 {
        serde_json::Value::from(d as i64)
    } else {
        serde_json::Number::from_f64(d).map(serde_json::Value::Number).unwrap_or(serde_json::Value::Null)
    };
    Ok((v, warning))
}

fn check_duration(e: &SnapshotEntry, ticks: Option<i64>, path: &str, clamp: bool)
    -> std::result::Result<(serde_json::Value, Option<String>), String> {
    let Some(ts) = ticks else {
        return Err(format!("\"{path}\" is not a duration"));
    };
    let s = ts as f64 / durations::TICKS_PER_SECOND as f64;
    let mut out = ts;
    let mut warning = None;
    if e.min.is_some_and(|m| s < m) || e.max.is_some_and(|m| s > m) {
        let lo = e.min.and_then(durations::ticks_from_seconds).unwrap_or(0);
        let hi = e.max.and_then(durations::ticks_from_seconds).unwrap_or(i64::MAX);
        let range = format!("{}..{}", durations::format(lo), if e.max.is_none() { String::new() } else { durations::format(hi) });
        if !clamp {
            return Err(format!("\"{path}\" must be in {range} (got {})", durations::format(ts)));
        }
        let c = if ts < lo { lo } else if ts > hi { hi } else { ts };
        warning = Some(format!("\"{path}\" = {} is outside {range}; using {}", durations::format(ts), durations::format(c)));
        out = c;
    }
    Ok((serde_json::Value::String(durations::format(out)), warning))
}

/// Kiln.Api's `Durations`: "250ms", "90s", "5m", "1h30m", "2d", "1.5h", "00:05:00", or a number of seconds. Values
/// are TimeSpan ticks (100 ns).
pub mod durations {
    pub const TICKS_PER_SECOND: i64 = 10_000_000;
    const TICKS_PER_MS: i64 = 10_000;
    const TICKS_PER_MINUTE: i64 = 60 * TICKS_PER_SECOND;
    const TICKS_PER_HOUR: i64 = 60 * TICKS_PER_MINUTE;
    const TICKS_PER_DAY: i64 = 24 * TICKS_PER_HOUR;

    /// TimeSpan.FromSeconds (None when out of TimeSpan's range or not a number).
    pub fn ticks_from_seconds(s: f64) -> Option<i64> {
        let t = s * TICKS_PER_SECOND as f64;
        (t.is_finite() && t.abs() < 9.2e18).then_some(t as i64)
    }

    /// Durations.TryParse.
    pub fn parse(text: &str) -> Option<i64> {
        if text.trim().is_empty() {
            return None;
        }
        let s = text.trim().to_lowercase();
        if let Some(secs) = super::parse_double(&s) {
            if !secs.is_finite() || secs < 0.0 || secs > 1e10 {
                return None;
            }
            return ticks_from_seconds(secs);
        }
        if s.contains(':') {
            return parse_clock(&s).filter(|&t| t >= 0);
        }
        let b: Vec<char> = s.chars().collect();
        let (mut total, mut i, mut any) = (0.0f64, 0usize, false);
        while i < b.len() {
            let start = i;
            while i < b.len() && (b[i].is_ascii_digit() || b[i] == '.') {
                i += 1;
            }
            if i == start {
                return None;
            }
            let n: f64 = b[start..i].iter().collect::<String>().parse().ok()?;
            let u = i;
            while i < b.len() && b[i].is_ascii_alphabetic() {
                i += 1;
            }
            let unit: String = b[u..i].iter().collect();
            let factor = match unit.as_str() {
                "ms" => 0.001,
                "s" | "sec" | "secs" | "second" | "seconds" => 1.0,
                "m" | "min" | "mins" | "minute" | "minutes" => 60.0,
                "h" | "hr" | "hrs" | "hour" | "hours" => 3600.0,
                "d" | "day" | "days" => 86400.0,
                _ => return None,
            };
            total += n * factor;
            any = true;
            while i < b.len() && b[i] == ' ' {
                i += 1;
            }
        }
        if !any || total > 1e10 {
            return None;
        }
        ticks_from_seconds(total)
    }

    /// TimeSpan.TryParse(InvariantCulture) for the clock forms: `[-][d.]h:m[:s[.fffffff]]` and `[-]d:h:m:s[.f]`.
    fn parse_clock(s: &str) -> Option<i64> {
        let (neg, s) = match s.strip_prefix('-') {
            Some(r) => (true, r),
            None => (false, s),
        };
        let num = |x: &str| -> Option<i64> { (!x.is_empty() && x.len() <= 8 && x.bytes().all(|b| b.is_ascii_digit())).then(|| x.parse().ok()).flatten() };
        let frac = |x: &str| -> Option<i64> {
            if x.is_empty() || x.len() > 7 || !x.bytes().all(|b| b.is_ascii_digit()) {
                return None;
            }
            format!("{x:0<7}").parse::<i64>().ok()
        };
        let parts: Vec<&str> = s.split(':').collect();
        let (days, h, m, sec_part) = match parts.len() {
            2 | 3 => {
                let (d, h) = match parts[0].split_once('.') {
                    Some((d, h)) => (num(d)?, num(h)?),
                    None => (0, num(parts[0])?),
                };
                (d, h, num(parts[1])?, parts.get(2).copied())
            }
            4 => (num(parts[0])?, num(parts[1])?, num(parts[2])?, Some(parts[3])),
            _ => return None,
        };
        let (secs, f) = match sec_part {
            None => (0, 0),
            Some(x) => match x.split_once('.') {
                Some((a, b)) => (num(a)?, frac(b)?),
                None => (num(x)?, 0),
            },
        };
        if h > 23 || m > 59 || secs > 59 {
            return None;
        }
        let t = days.checked_mul(TICKS_PER_DAY)?
            .checked_add(h * TICKS_PER_HOUR)?
            .checked_add(m * TICKS_PER_MINUTE)?
            .checked_add(secs * TICKS_PER_SECOND)?
            .checked_add(f)?;
        Some(if neg { -t } else { t })
    }

    /// Durations.Format.
    pub fn format(t: i64) -> String {
        if t == i64::MAX {
            return "max".into();
        }
        if t < 0 {
            return format!("-{}", format(t.checked_neg().unwrap_or(i64::MAX)));
        }
        if t == 0 {
            return "0s".into();
        }
        if t < TICKS_PER_SECOND {
            return format!("{}ms", super::fixed(t as f64 / TICKS_PER_MS as f64, 3));
        }
        let mut s = String::new();
        let days = t / TICKS_PER_DAY;
        let hours = t / TICKS_PER_HOUR % 24;
        let minutes = t / TICKS_PER_MINUTE % 60;
        let seconds = t / TICKS_PER_SECOND % 60;
        let millis = t / TICKS_PER_MS % 1000;
        if days > 0 {
            s.push_str(&format!("{days}d"));
        }
        if hours > 0 {
            s.push_str(&format!("{hours}h"));
        }
        if minutes > 0 {
            s.push_str(&format!("{minutes}m"));
        }
        let sec = seconds as f64 + millis as f64 / 1000.0;
        if sec > 0.0 {
            s.push_str(&format!("{}s", super::fixed(sec, 3)));
        }
        s
    }
}

/// INI values files (EMF settings): `[section]` headers, `key = value` lines, `;`/`#` comments. Edits replace the
/// value text of one line and keep everything else byte-identical.
pub mod ini {
    use super::{SchemaSnapshot, entry_kind};
    use serde_json::{Map, Value};

    struct Line<'a> {
        start: usize,
        /// The line without its line ending.
        text: &'a str,
    }

    fn lines(text: &str) -> Vec<Line<'_>> {
        let mut out = Vec::new();
        let mut start = 0;
        for piece in text.split_inclusive('\n') {
            let body = piece.strip_suffix('\n').unwrap_or(piece);
            let body = body.strip_suffix('\r').unwrap_or(body);
            out.push(Line { start, text: body });
            start += piece.len();
        }
        out
    }

    fn section_of(t: &str) -> Option<String> {
        let t = t.trim();
        (t.starts_with('[') && t.ends_with(']')).then(|| t[1..t.len() - 1].trim().to_string())
    }

    fn key_value(t: &str) -> Option<(&str, &str)> {
        let s = t.trim_start();
        if s.is_empty() || s.starts_with([';', '#']) || s.starts_with('[') {
            return None;
        }
        let (k, v) = t.split_once('=')?;
        Some((k.trim(), v.trim()))
    }

    /// `{key: "value", section: {key: "value"}}` (strings; a repeated key: the last one).
    pub fn values(text: &str) -> Value {
        let mut root = Map::new();
        let mut section: Option<String> = None;
        for l in lines(text) {
            if let Some(s) = section_of(l.text) {
                root.entry(s.clone()).or_insert_with(|| Value::Object(Map::new()));
                section = Some(s);
                continue;
            }
            let Some((k, v)) = key_value(l.text) else { continue };
            let target = match &section {
                Some(s) => root.get_mut(s).and_then(Value::as_object_mut).expect("section inserted"),
                None => &mut root,
            };
            target.insert(k.to_string(), Value::String(v.to_string()));
        }
        Value::Object(root)
    }

    /// The raw string values converted to the snapshot's types where they validate.
    pub fn typed(raw: &Value, snapshot: &SchemaSnapshot) -> Value {
        let mut out = raw.clone();
        for e in &snapshot.entries {
            if entry_kind(e).is_none() {
                continue;
            }
            let (sec, key) = match e.path.split_once('.') {
                Some((s, k)) => (Some(s), k),
                None => (None, e.path.as_str()),
            };
            let obj = match sec {
                Some(s) => out.as_object_mut().and_then(|o| {
                    let name = o.keys().find(|k| crate::jsonc::eq_ci(k, s)).cloned()?;
                    o.get_mut(&name)?.as_object_mut()
                }),
                None => out.as_object_mut(),
            };
            let Some(obj) = obj else { continue };
            let Some(name) = obj.keys().find(|k| crate::jsonc::eq_ci(k, key)).cloned() else { continue };
            if let Some(Value::String(s)) = obj.get(&name)
                && let Ok(v) = super::check_text(e, s)
            {
                obj.insert(name, v);
            }
        }
        out
    }

    /// The text of a value in an INI file.
    pub fn text_of(v: &Value) -> String {
        match v {
            Value::String(s) => s.clone(),
            Value::Null => String::new(),
            Value::Array(items) => items.iter().map(text_of).collect::<Vec<_>>().join(","),
            other => other.to_string(),
        }
    }

    fn split(path: &str) -> (Option<&str>, &str) {
        match path.split_once('.') {
            Some((s, k)) => (Some(s), k),
            None => (None, path),
        }
    }

    /// (start, end) byte offsets of the key's line (end excludes the line ending), and of the section's last line.
    fn locate(text: &str, path: &str) -> (Option<(usize, usize, usize)>, Option<usize>, bool) {
        let (sec, key) = split(path);
        let mut current: Option<String> = None;
        let mut found = None;
        let mut section_end = None;
        let mut section_seen = sec.is_none();
        for l in lines(text) {
            if let Some(s) = section_of(l.text) {
                current = Some(s);
                if sec.is_some_and(|w| current.as_deref().is_some_and(|c| crate::jsonc::eq_ci(c, w))) {
                    section_seen = true;
                    section_end = Some(l.start + l.text.len());
                }
                continue;
            }
            let in_section = match (sec, &current) {
                (None, None) => true,
                (Some(w), Some(c)) => crate::jsonc::eq_ci(c, w),
                _ => false,
            };
            if !in_section {
                continue;
            }
            if let Some((k, _)) = key_value(l.text) {
                section_end = Some(l.start + l.text.len());
                if crate::jsonc::eq_ci(k, key) {
                    let eq = l.text.find('=').expect("key_value found '='");
                    found = Some((l.start, l.start + eq + 1, l.start + l.text.len()));
                }
            }
        }
        (found, section_end, section_seen)
    }

    fn eol(text: &str) -> &'static str {
        if text.contains("\r\n") { "\r\n" } else { "\n" }
    }

    fn style(text: &str) -> &'static str {
        if lines(text).iter().any(|l| key_value(l.text).is_some() && l.text.contains(" = ")) || !text.contains('=') {
            " = "
        } else {
            "="
        }
    }

    /// `text` with `path` (`section.key`, or `key` before any section) set to `value`.
    pub fn set(text: &str, path: &str, value: &str) -> String {
        let (sec, key) = split(path);
        let nl = eol(text);
        let (found, section_end, section_seen) = locate(text, path);
        if let Some((_, eq_end, line_end)) = found {
            let after = &text[eq_end..line_end];
            let pad = &after[..after.len() - after.trim_start().len()];
            let mut out = text.to_string();
            out.replace_range(eq_end..line_end, &format!("{pad}{value}"));
            return out;
        }
        let line = format!("{key}{}{value}", style(text));
        let mut out = text.to_string();
        match (section_end, section_seen, sec) {
            (Some(end), _, _) => out.insert_str(end, &format!("{nl}{line}")),
            (None, false, Some(s)) => {
                if !out.is_empty() && !out.ends_with('\n') {
                    out.push_str(nl);
                }
                if !out.is_empty() {
                    out.push_str(nl);
                }
                out.push_str(&format!("[{s}]{nl}{line}{nl}"));
            }
            (None, _, None) => out.insert_str(0, &format!("{line}{nl}")),
            (None, true, Some(_)) => unreachable!("a seen section has an end"),
        }
        out
    }

    /// `text` without the line of `path`.
    pub fn remove(text: &str, path: &str) -> String {
        let (found, _, _) = locate(text, path);
        let Some((start, _, end)) = found else { return text.to_string() };
        let b = text.as_bytes();
        let mut e = end;
        if b.get(e) == Some(&b'\r') {
            e += 1;
        }
        if b.get(e) == Some(&b'\n') {
            e += 1;
        }
        let mut out = text.to_string();
        out.replace_range(start..e, "");
        out
    }
}
