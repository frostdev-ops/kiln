//! `kiln.mod.json` (a port of managed/Kiln.Host/Manifest.cs; parity: schema/fixtures/manifest) and `native.json`
//! (schema/native.schema.json). Both allow `//` comments and trailing commas. Also the small enums every format
//! shares (side, clients, mod kind).

use crate::jsonc::{self, JValue, Node};
use crate::semver::{SemVersion, VersionRange};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::Path;

pub const KILN_MANIFEST: &str = "kiln.mod.json";
pub const NATIVE_MANIFEST: &str = "native.json";

/// Where a mod loads (manifest `side`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Side {
    Server,
    Client,
    Both,
}

/// The side a process runs as.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RunSide {
    Server,
    Client,
}

/// What joining players need (manifest `clients`): none | kiln | mod.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Clients {
    None,
    Kiln,
    Mod,
}

impl Clients {
    /// The default when a manifest has no `clients`: server -> none, client/both -> mod.
    pub fn default_for(side: Side) -> Clients {
        if side == Side::Server { Clients::None } else { Clients::Mod }
    }
}

/// Library / profile kinds (`library/<kind>/`, profile.json `mods.<kind>`). The first four are Kiln's own; the rest
/// hold mods made for other engines, run by Kiln's plugin or placed for an external host (XHL-Core) (docs/kiln/engines.md, "Library kinds").
/// Every engine kind has a Library importer (engine_mods::IMPORTED); Ember's mods are held but don't run yet (Ember has
/// published no mod ABI, docs/kiln/engines/ember.md).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ModKind {
    Kiln,
    Native,
    Plugin,
    Emf,
    /// EML data/Lua mods (`mod.json` + `src/mod.lua`), run by kiln-eml (GPL plugin).
    Eml,
    /// Non-executable companions placed in `<game>/mods/<original>/`; only the separately installed XHL-Core loads them.
    Xhl,
    /// ShroudForge packages, run by kiln-shroudforge from ShroudForge's mods folder.
    #[serde(rename = "shroudforge")]
    ShroudForge,
    /// Shrouded Engine Tweaks Lua mods, run by kiln-set from `shrouded_engine_tweaks/mods`.
    Set,
    /// Greeneye Loader DLL mods, run by kiln-greeneye.
    Greeneye,
    /// Flame mods (Creative Mode), run by kiln-flame.
    Flame,
    /// Shroudtopia / Shroudweave DLL mods, run by kiln-shroudtopia.
    Shroudtopia,
    /// Enshwick mods (DLL mods, `pack.toml` data packs and Luau mods), run by kiln-enshwick from `<game>/mods/<id>/`.
    Enshwick,
    /// shroudlink server mod DLLs, run by kiln-shroudlink (shroudlink's own server core).
    Shroudlink,
    /// Ember server SDK mods (one library per folder), found and reported by kiln-ember; not run until Ember publishes
    /// its mod ABI.
    Ember,
}

impl ModKind {
    pub const ALL: [ModKind; 14] = [
        ModKind::Kiln,
        ModKind::Native,
        ModKind::Plugin,
        ModKind::Emf,
        ModKind::Eml,
        ModKind::Xhl,
        ModKind::ShroudForge,
        ModKind::Set,
        ModKind::Greeneye,
        ModKind::Flame,
        ModKind::Shroudtopia,
        ModKind::Enshwick,
        ModKind::Shroudlink,
        ModKind::Ember,
    ];
    /// Kiln's own kinds (kiln-core's `kiln.profile` service counts these: KILN_PROFILE_KIND_*).
    pub const CORE: [ModKind; 4] = [ModKind::Kiln, ModKind::Native, ModKind::Plugin, ModKind::Emf];

    pub fn as_str(self) -> &'static str {
        match self {
            ModKind::Kiln => "kiln",
            ModKind::Native => "native",
            ModKind::Plugin => "plugin",
            ModKind::Emf => "emf",
            ModKind::Eml => "eml",
            ModKind::Xhl => "xhl",
            ModKind::ShroudForge => "shroudforge",
            ModKind::Set => "set",
            ModKind::Greeneye => "greeneye",
            ModKind::Flame => "flame",
            ModKind::Shroudtopia => "shroudtopia",
            ModKind::Enshwick => "enshwick",
            ModKind::Shroudlink => "shroudlink",
            ModKind::Ember => "ember",
        }
    }

    /// `"eml"` -> Eml (the profile/library folder names).
    pub fn parse(s: &str) -> Option<ModKind> {
        ModKind::ALL.into_iter().find(|k| k.as_str() == s)
    }

    /// A kind for another engine's mods (not one of Kiln's own four).
    pub fn is_engine(self) -> bool {
        !ModKind::CORE.contains(&self)
    }

    /// The engine whose plugin runs this kind (EMF for `emf`; None for Kiln's own kinds and non-executable XHL companions).
    pub fn engine(self) -> Option<crate::engines::Engine> {
        use crate::engines::Engine;
        Some(match self {
            ModKind::Kiln | ModKind::Native | ModKind::Plugin | ModKind::Xhl => return None,
            ModKind::Emf => Engine::Emf,
            ModKind::Eml => Engine::Eml,
            ModKind::ShroudForge => Engine::ShroudForge,
            ModKind::Set => Engine::Set,
            ModKind::Greeneye => Engine::Greeneye,
            ModKind::Flame => Engine::Flame,
            ModKind::Shroudtopia => Engine::Shroudtopia,
            ModKind::Enshwick => Engine::Enshwick,
            ModKind::Shroudlink => Engine::Shroudlink,
            ModKind::Ember => Engine::Ember,
        })
    }

    /// The library kind a detected item is imported as (None: a kind that is not a mod, e.g. a save or a proxy).
    pub fn of_detect(k: crate::library::DetectKind) -> Option<ModKind> {
        use crate::library::DetectKind as D;
        Some(match k {
            D::Kiln => ModKind::Kiln,
            D::Native | D::BareDll => ModKind::Native,
            D::Plugin => ModKind::Plugin,
            D::Emf => ModKind::Emf,
            D::Eml => ModKind::Eml,
            D::Xhl => ModKind::Xhl,
            D::ShroudForge => ModKind::ShroudForge,
            D::Set => ModKind::Set,
            D::Greeneye => ModKind::Greeneye,
            D::Flame => ModKind::Flame,
            D::Shroudtopia => ModKind::Shroudtopia,
            D::Enshwick => ModKind::Enshwick,
            D::Shroudlink => ModKind::Shroudlink,
            D::Ember => ModKind::Ember,
            _ => return None,
        })
    }

    /// The Library detection kind of this mod kind.
    pub fn detect_kind(self) -> crate::library::DetectKind {
        use crate::library::DetectKind as D;
        match self {
            ModKind::Kiln => D::Kiln,
            ModKind::Native => D::Native,
            ModKind::Plugin => D::Plugin,
            ModKind::Emf => D::Emf,
            ModKind::Eml => D::Eml,
            ModKind::Xhl => D::Xhl,
            ModKind::ShroudForge => D::ShroudForge,
            ModKind::Set => D::Set,
            ModKind::Greeneye => D::Greeneye,
            ModKind::Flame => D::Flame,
            ModKind::Shroudtopia => D::Shroudtopia,
            ModKind::Enshwick => D::Enshwick,
            ModKind::Shroudlink => D::Shroudlink,
            ModKind::Ember => D::Ember,
        }
    }

    /// The name players see for the kind ("Kiln", "EML", "ShroudForge" ...).
    pub fn label(self) -> &'static str {
        match self {
            ModKind::Kiln => "Kiln",
            ModKind::Native => "Native",
            ModKind::Plugin => "Plugin",
            ModKind::Xhl => "XHL (requires XHL-Core)",
            k => k.engine().map(|e| e.name()).unwrap_or("?"),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Dependency {
    pub id: String,
    /// The range text as written ("*" when empty).
    pub range: String,
    pub optional: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Incompatibility {
    pub id: String,
    pub range: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TestsSpec {
    pub auto_run: bool,
    pub timeout_seconds: i32,
    pub filter: Option<String>,
}

/// A Kiln build (port of managed/Kiln.Host/KilnBuild.cs): its version as the runtime bundle names it
/// (`0.2.0-dev+20261106.0f0a3f6`) and, when known, the commit's time (`2026-11-06T12:12:16Z`, the bundle's `created`).
/// A mod's kiln.mod.json has the one it compiled against (`"kilnBuild"`, written by Kiln.Sdk).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KilnBuild {
    pub version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created: Option<String>,
}

impl KilnBuild {
    /// KilnBuild.From: None when the version is not `semver[+build]`; a created time that is not `yyyy-MM-ddTHH:mm:ssZ` is dropped.
    pub fn from_parts(version: &str, created: Option<&str>) -> Option<KilnBuild> {
        let version = version.trim();
        if version.is_empty() || SemVersion::try_parse(version.split('+').next().unwrap_or("")).is_none() {
            return None;
        }
        let created = created.map(str::trim).filter(|c| is_utc_time(c)).map(str::to_string);
        Some(KilnBuild { version: version.to_string(), created })
    }

    /// KilnBuild.IsNewerThan: by the semantic version, then by the commit time when both have one, else by the day in the
    /// build part. Two builds of one day without a time can't be told apart: not newer.
    pub fn is_newer_than(&self, other: &KilnBuild) -> bool {
        let (a, ad) = split_build(&self.version);
        let (b, bd) = split_build(&other.version);
        let (Some(a), Some(b)) = (a, b) else { return false };
        match a.compare(&b) {
            std::cmp::Ordering::Greater => return true,
            std::cmp::Ordering::Less => return false,
            std::cmp::Ordering::Equal => {}
        }
        if let (Some(x), Some(y)) = (&self.created, &other.created) {
            return x > y;
        }
        ad.len() == 8 && bd.len() == 8 && ad > bd
    }
}

/// `yyyy-MM-ddTHH:mm:ssZ`
fn is_utc_time(s: &str) -> bool {
    let b = s.as_bytes();
    b.len() == 20
        && b.iter().enumerate().all(|(i, &c)| match i {
            4 | 7 => c == b'-',
            10 => c == b'T',
            13 | 16 => c == b':',
            19 => c == b'Z',
            _ => c.is_ascii_digit(),
        })
}

/// "0.2.0-dev+20261106.0f0a3f6" -> (0.2.0-dev, "20261106")
fn split_build(text: &str) -> (Option<SemVersion>, String) {
    let (core, build) = text.split_once('+').unwrap_or((text, ""));
    let day = build.split('.').next().unwrap_or("");
    let day = if day.len() == 8 && day.bytes().all(|c| c.is_ascii_digit()) { day.to_string() } else { String::new() };
    (SemVersion::try_parse(core), day)
}

/// A parsed, validated kiln.mod.json: the fields of schema/fixtures/manifest "expect.manifest".
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KilnManifest {
    pub id: String,
    pub name: String,
    pub version: String,
    /// The version normalized by SemVer (1.2 -> 1.2.0).
    pub parsed_version: String,
    pub api_version: String,
    /// The mod's assembly; empty for a mod of data only (no `entryAssembly`).
    pub entry_assembly: String,
    pub entry_type: Option<String>,
    pub side: Side,
    pub clients: Clients,
    pub authors: Vec<String>,
    pub description: String,
    pub license: Option<String>,
    pub url: Option<String>,
    pub dependencies: Vec<Dependency>,
    pub load_before: Vec<String>,
    pub load_after: Vec<String>,
    pub incompatible: Vec<Incompatibility>,
    pub features: Vec<String>,
    pub exports: Vec<String>,
    pub advanced: bool,
    pub tests: Option<TestsSpec>,
    /// The Kiln build the mod was compiled against (`"kilnBuild"`); None when unknown.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kiln_build: Option<KilnBuild>,
    /// `"boot": true` or `"boot": "required"`: the mod runs in Mod.OnBoot's definition phase (kiln.boot reads it the
    /// same way: native/core/src/services/boot_service.cpp manifest_boot); anything else is no boot mod.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub boot: bool,
    /// Non-fatal findings (unknown keys), same texts as Manifest.cs.
    pub warnings: Vec<String>,
}

/// The manifest id rule (Manifest.IdPattern). Like .NET's `$`, the pattern also matches before a final "\n".
pub const ID_PATTERN: &str = "^[a-z0-9][a-z0-9._-]{0,63}$";
/// The dependency id that is not a mod: the native core's version.
pub const KILN_ID: &str = "kiln";
const RESERVED: [&str; 6] = ["kiln", "kiln.api", "game", "enshrouded", "native", "permissions"];
const KNOWN: [&str; 24] = [
    "$schema", "id", "name", "version", "authors", "author", "description", "license", "url", "apiVersion", "side",
    "clients", "dependencies", "loadBefore", "loadAfter", "incompatible", "features", "entryAssembly", "entryType", "exports",
    "advanced", "tests", "kilnBuild",
    "boot",
];

fn id_rule(s: &str) -> bool {
    let b = s.as_bytes();
    !b.is_empty()
        && b.len() <= 64
        && (b[0].is_ascii_lowercase() || b[0].is_ascii_digit())
        && b[1..].iter().all(|&c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'.' || c == b'_' || c == b'-')
}

/// `Manifest.IdPattern.IsMatch`: the id rule of every mod kind. A final "\n" is tolerated, as .NET's `$` does.
pub fn is_mod_id(s: &str) -> bool {
    id_rule(s.strip_suffix('\n').unwrap_or(s))
}

fn bad(message: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Schema, format!("{KILN_MANIFEST}: {message}"))
}

fn ends_with_ci(s: &str, suffix: &str) -> bool {
    s.len() >= suffix.len() && s.is_char_boundary(s.len() - suffix.len()) && s[s.len() - suffix.len()..].eq_ignore_ascii_case(suffix)
}

/// Manifest.IsDllName: a `.dll` file name directly in the mod folder.
fn is_dll_name(s: &str) -> bool {
    ends_with_ci(s, ".dll") && s.len() > 4 && !s.contains(['/', '\\', ':']) && s != ".."
}

/// Manifest.Str: absent or null -> None; any other non-string -> error.
fn str_of(root: &Node, key: &str) -> Result<Option<String>> {
    match root.get(key) {
        None => Ok(None),
        Some(n) if n.is_null() => Ok(None),
        Some(n) => n.as_str().map(|s| Some(s.to_string())).ok_or_else(|| bad(format!("'{key}' must be a string"))),
    }
}

/// Manifest.Strings.
fn strings(root: &Node, key: &str, allow_single: bool) -> Result<Vec<String>> {
    let Some(v) = root.get(key).filter(|n| !n.is_null()) else { return Ok(Vec::new()) };
    if allow_single && let Some(s) = v.as_str() {
        return Ok(vec![s.to_string()]);
    }
    if !v.is_array() {
        return Err(bad(format!("'{key}' must be an array of strings")));
    }
    let mut list = Vec::new();
    for e in v.elements() {
        match e.node.as_str() {
            Some(s) if !s.trim().is_empty() => list.push(s.to_string()),
            _ => return Err(bad(format!("'{key}' must be an array of non-empty strings"))),
        }
    }
    Ok(list)
}

fn ids(root: &Node, key: &str) -> Result<Vec<String>> {
    let ids = strings(root, key, false)?;
    if let Some(x) = ids.iter().find(|x| !is_mod_id(x)) {
        return Err(bad(format!("{key}: '{x}' is not a valid mod id")));
    }
    Ok(ids)
}

fn range(owner: &str, text: &str) -> Result<VersionRange> {
    VersionRange::try_parse(text).map_err(|e| bad(format!("{owner}: {e}")))
}

fn parse_dependencies(root: &Node, self_id: &str) -> Result<Vec<Dependency>> {
    let mut list: Vec<Dependency> = Vec::new();
    let Some(v) = root.get("dependencies").filter(|n| !n.is_null()) else { return Ok(list) };
    let add = |list: &mut Vec<Dependency>, id: &str, spec: &Node| -> Result<()> {
        if id != KILN_ID && !is_mod_id(id) {
            return Err(bad(format!("dependencies: '{id}' is not a valid mod id")));
        }
        if id == self_id {
            return Err(bad("a mod cannot depend on itself"));
        }
        if list.iter().any(|d| d.id == id) {
            return Err(bad(format!("dependencies: '{id}' is listed twice")));
        }
        let (text, optional) = match &spec.value {
            JValue::String(s) => (s.clone(), false),
            JValue::Object(_) => {
                let text = spec.get("version").and_then(Node::as_str).unwrap_or("*").to_string();
                let optional = match spec.get("optional") {
                    None => false,
                    Some(o) => o.as_bool().ok_or_else(|| bad(format!("dependencies.{id}.optional must be true or false")))?,
                };
                (text, optional)
            }
            _ => return Err(bad(format!("dependencies.{id} must be a version range string or an object"))),
        };
        let trimmed = text.trim();
        let range_text = if trimmed.is_empty() { "*".to_string() } else { trimmed.to_string() };
        range(&format!("dependency '{id}'"), &text)?;
        list.push(Dependency { id: id.to_string(), range: range_text, optional });
        Ok(())
    };
    match &v.value {
        JValue::Object(members) => {
            for m in members {
                add(&mut list, &m.key, &m.value)?;
            }
        }
        JValue::Array(items) => {
            for e in items {
                let id = e.node.is_object().then(|| e.node.get("id")).flatten().and_then(Node::as_str);
                let Some(id) = id else {
                    return Err(bad("dependencies: each entry needs a string 'id'"));
                };
                add(&mut list, id, &e.node)?;
            }
        }
        _ => return Err(bad("dependencies must be an object (id -> range) or an array")),
    }
    Ok(list)
}

fn parse_incompatible(root: &Node, self_id: &str) -> Result<Vec<Incompatibility>> {
    let mut list = Vec::new();
    let Some(v) = root.get("incompatible").filter(|n| !n.is_null()) else { return Ok(list) };
    let add = |list: &mut Vec<Incompatibility>, id: &str, text: &str| -> Result<()> {
        if !is_mod_id(id) {
            return Err(bad(format!("incompatible: '{id}' is not a valid mod id")));
        }
        if id == self_id {
            return Err(bad("a mod cannot be incompatible with itself"));
        }
        range(&format!("incompatible '{id}'"), text)?;
        list.push(Incompatibility { id: id.to_string(), range: text.to_string() });
        Ok(())
    };
    match &v.value {
        JValue::Array(items) => {
            for e in items {
                let id = e.node.as_str().ok_or_else(|| bad("incompatible: entries must be mod ids"))?;
                add(&mut list, id, "*")?;
            }
        }
        JValue::Object(members) => {
            for m in members {
                let text = m.value.as_str().ok_or_else(|| bad(format!("incompatible.{} must be a range", m.key)))?;
                add(&mut list, &m.key, text)?;
            }
        }
        _ => return Err(bad("incompatible must be an array of ids or an object (id -> range)")),
    }
    Ok(list)
}

/// JsonElement.GetInt32: an integer token that fits.
fn get_int32(n: &Node) -> Option<i32> {
    let t = n.as_number()?;
    let digits = t.strip_prefix('-').unwrap_or(t);
    if digits.is_empty() || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return None;
    }
    t.parse().ok()
}

fn parse_tests(root: &Node) -> Result<Option<TestsSpec>> {
    let Some(v) = root.get("tests").filter(|n| !n.is_null()) else { return Ok(None) };
    if let Some(b) = v.as_bool() {
        return Ok(Some(TestsSpec { auto_run: b, timeout_seconds: 0, filter: None }));
    }
    if !v.is_object() {
        return Err(bad("tests must be true/false or an object"));
    }
    let auto_run = v.get("autoRun").and_then(Node::as_bool) == Some(true);
    let timeout_seconds = match v.get("timeoutSeconds") {
        Some(t) if t.as_number().is_some() => {
            get_int32(t).ok_or_else(|| bad("tests.timeoutSeconds must be a whole number (32-bit)"))?
        }
        _ => 0,
    };
    if timeout_seconds < 0 {
        return Err(bad("tests.timeoutSeconds must be >= 0"));
    }
    let filter = v.get("filter").and_then(Node::as_str).map(str::to_string);
    Ok(Some(TestsSpec { auto_run, timeout_seconds, filter }))
}

/// Parses and validates a kiln.mod.json; errors carry Manifest.cs's message ("kiln.mod.json: ..."). Syntax errors
/// have code Parse ("kiln.mod.json: not valid JSON (...)"), rule violations code Schema.
pub fn parse_kiln(text: &str) -> Result<KilnManifest> {
    let root = jsonc::parse_tree(text)
        .map_err(|e| Error::new(ErrorCode::Parse, format!("{KILN_MANIFEST}: not valid JSON ({e})")))?;
    if !root.is_object() {
        return Err(bad("the file must contain one JSON object"));
    }
    let mut warnings: Vec<String> = root
        .members()
        .iter()
        .filter(|m| !KNOWN.contains(&m.key.as_str()))
        .map(|m| format!("unknown key '{}' (ignored)", m.key))
        .collect();

    let id = str_of(&root, "id")?.ok_or_else(|| bad("'id' is required"))?;
    if !is_mod_id(&id) {
        return Err(bad(format!("id '{id}' must match {ID_PATTERN}")));
    }
    if RESERVED.contains(&id.as_str()) {
        return Err(bad(format!("id '{id}' is reserved")));
    }
    let version = str_of(&root, "version")?.ok_or_else(|| bad("'version' is required"))?;
    let parsed = SemVersion::try_parse(&version)
        .ok_or_else(|| bad(format!("version '{version}' is not a semantic version (e.g. 1.2.0)")))?;
    let api = str_of(&root, "apiVersion")?
        .ok_or_else(|| bad("'apiVersion' is required (the Kiln API version the mod was built for, e.g. \"0.2\")"))?;
    if SemVersion::try_parse(&api).is_none() {
        VersionRange::try_parse(&api).map_err(|e| bad(format!("apiVersion: {e}")))?;
    }
    // No entryAssembly: a mod of data only (content packs, assets). Kiln.Host loads it with nothing to run.
    let entry = str_of(&root, "entryAssembly")?;
    if let Some(entry) = &entry
        && !is_dll_name(entry)
    {
        return Err(bad(format!("entryAssembly '{entry}' must be a .dll file name in the mod folder")));
    }
    let side = match str_of(&root, "side")?.as_deref().unwrap_or("server") {
        "server" => Side::Server,
        "client" => Side::Client,
        "both" => Side::Both,
        s => return Err(bad(format!("side '{s}' must be server, client or both"))),
    };
    let clients = match str_of(&root, "clients")?.as_deref() {
        None => Clients::default_for(side),
        Some("none") => Clients::None,
        Some("kiln") => Clients::Kiln,
        Some("mod") => Clients::Mod,
        Some(c) => return Err(bad(format!("clients '{c}' must be none, kiln or mod"))),
    };
    let mut authors = strings(&root, "authors", true)?;
    if authors.is_empty()
        && let Some(a) = str_of(&root, "author")?
    {
        authors = vec![a];
    }
    let dependencies = parse_dependencies(&root, &id)?;
    let exports = strings(&root, "exports", false)?;
    if let Some(x) = exports.iter().find(|x| !is_dll_name(x)) {
        return Err(bad(format!("exports: '{x}' must be a .dll file name in the mod folder")));
    }
    let load_before = ids(&root, "loadBefore")?;
    let load_after = ids(&root, "loadAfter")?;
    if load_before.contains(&id) || load_after.contains(&id) {
        return Err(bad("loadBefore/loadAfter must not name the mod itself"));
    }
    if let Some(x) = load_before.iter().find(|x| load_after.contains(x)) {
        return Err(bad(format!("'{x}' is in both loadBefore and loadAfter")));
    }
    // never a reason to refuse a mod: a stamp Kiln can't read is ignored
    let mut kiln_build = None;
    if let Some(kb) = root.get("kilnBuild").filter(|n| !n.is_null()) {
        if kb.is_object()
            && let Some(v) = kb.get("version").and_then(Node::as_str)
        {
            kiln_build = KilnBuild::from_parts(v, kb.get("created").and_then(Node::as_str));
        }
        if kiln_build.is_none() {
            warnings.push(r#"kilnBuild must be {"version": "<semver>+<build>", "created": "<UTC time>"} (ignored)"#.to_string());
        }
    }
    let advanced = match root.get("advanced") {
        None => false,
        Some(a) => a.as_bool().ok_or_else(|| bad("advanced must be true or false"))?,
    };
    // the object initializer's order (Manifest.cs): name, entryType, description, license, url, incompatible,
    // features, tests
    let name = str_of(&root, "name")?.unwrap_or_else(|| id.clone());
    let entry_type = str_of(&root, "entryType")?;
    let description = str_of(&root, "description")?.unwrap_or_default();
    let license = str_of(&root, "license")?;
    let url = str_of(&root, "url")?;
    let incompatible = parse_incompatible(&root, &id)?;
    let features = strings(&root, "features", false)?;
    let tests = parse_tests(&root)?;
    let boot = root.get("boot").is_some_and(|b| b.as_bool() == Some(true) || b.as_str() == Some("required"));
    Ok(KilnManifest {
        id,
        name,
        version,
        parsed_version: parsed.to_string(),
        api_version: api,
        entry_assembly: entry.unwrap_or_default(),
        entry_type,
        side,
        clients,
        authors,
        description,
        license,
        url,
        dependencies,
        load_before,
        load_after,
        incompatible,
        features,
        exports,
        advanced,
        tests,
        kiln_build,
        boot,
        warnings,
    })
}

/// File.ReadAllText: a BOM picks the encoding (UTF-8, UTF-16 LE/BE, UTF-32 LE), else UTF-8 (invalid bytes become
/// U+FFFD).
pub(crate) fn read_text(path: &Path) -> Result<String> {
    let b = std::fs::read(path).map_err(|e| Error::from(e).with_detail(path.display().to_string()))?;
    Ok(decode_text(&b))
}

pub(crate) fn decode_text(b: &[u8]) -> String {
    let utf16 = |data: &[u8], le: bool| {
        let units: Vec<u16> = data
            .chunks(2)
            .map(|c| if c.len() == 2 { if le { u16::from_le_bytes([c[0], c[1]]) } else { u16::from_be_bytes([c[0], c[1]]) } } else { 0xFFFD })
            .collect();
        String::from_utf16_lossy(&units)
    };
    if let Some(r) = b.strip_prefix(b"\xEF\xBB\xBF") {
        String::from_utf8_lossy(r).into_owned()
    } else if let Some(r) = b.strip_prefix(b"\xFF\xFE\x00\x00") {
        r.chunks(4)
            .map(|c| if c.len() == 4 { char::from_u32(u32::from_le_bytes([c[0], c[1], c[2], c[3]])).unwrap_or('\u{FFFD}') } else { '\u{FFFD}' })
            .collect()
    } else if let Some(r) = b.strip_prefix(b"\xFF\xFE") {
        utf16(r, true)
    } else if let Some(r) = b.strip_prefix(b"\xFE\xFF") {
        utf16(r, false)
    } else {
        String::from_utf8_lossy(b).into_owned()
    }
}

/// `<dir>/kiln.mod.json`, parsed and validated.
pub fn read_kiln(dir: &Path) -> Result<KilnManifest> {
    parse_kiln(&read_text(&dir.join(KILN_MANIFEST))?)
}

/// native.json `phase`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum NativePhase {
    Dllmain,
    #[default]
    Early,
    Boot,
    World,
}

/// `["a", "b"]` or `{"a": "<range>"}`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum IncompatibleSpec {
    Ids(Vec<String>),
    Ranges(std::collections::BTreeMap<String, String>),
}

impl Default for IncompatibleSpec {
    fn default() -> Self {
        IncompatibleSpec::Ids(Vec::new())
    }
}

/// `"a"` or `["a", "b"]`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(untagged)]
pub enum OneOrMany {
    One(String),
    Many(Vec<String>),
}

fn default_native_side() -> Side {
    Side::Client
}

fn yes() -> bool {
    true
}

/// native.json (schema/native.schema.json), as written.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct NativeManifest {
    #[serde(default, rename = "$schema", skip_serializing_if = "Option::is_none")]
    pub schema: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub format: Option<u32>,
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    pub version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub authors: Option<OneOrMany>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub license: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub url: Option<String>,
    #[serde(default = "default_native_side")]
    pub side: Side,
    /// None: Clients::default_for(side).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub clients: Option<Clients>,
    pub dlls: Vec<String>,
    #[serde(default)]
    pub phase: NativePhase,
    #[serde(default)]
    pub init: Option<String>,
    #[serde(default)]
    pub after: Vec<String>,
    #[serde(default)]
    pub before: Vec<String>,
    #[serde(default)]
    pub requires: Vec<String>,
    #[serde(default)]
    pub incompatible: IncompatibleSpec,
    #[serde(default = "yes")]
    pub enabled: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kiln: Option<String>,
    #[serde(default)]
    pub builds: Vec<String>,
}

fn nbad(message: impl std::fmt::Display) -> Error {
    Error::new(ErrorCode::Schema, format!("{NATIVE_MANIFEST}: {message}"))
}

/// common.schema.json relPath: relative, '/' separated, no '..' component, no Windows-invalid characters.
pub fn is_rel_path(s: &str) -> bool {
    !s.is_empty()
        && !s.starts_with('/')
        && !s.contains(['\\', ':', '*', '?', '"', '<', '>', '|'])
        && !s.split('/').any(|c| c == "..")
}

fn opt_string(root: &Node, key: &str) -> Result<()> {
    match root.get(key) {
        None => Ok(()),
        Some(n) if n.is_null() || n.as_str().is_some() => Ok(()),
        Some(_) => Err(nbad(format!("'{key}' must be a string"))),
    }
}

fn id_list(root: &Node, key: &str) -> Result<()> {
    let Some(v) = root.get(key).filter(|n| !n.is_null()) else { return Ok(()) };
    if !v.is_array() {
        return Err(nbad(format!("'{key}' must be an array of mod ids")));
    }
    let mut seen = std::collections::BTreeSet::new();
    for e in v.elements() {
        let Some(s) = e.node.as_str() else {
            return Err(nbad(format!("'{key}' must be an array of mod ids")));
        };
        if !id_rule(s) {
            return Err(nbad(format!("{key}: '{s}' is not a valid mod id")));
        }
        if !seen.insert(s) {
            return Err(nbad(format!("{key}: '{s}' is listed twice")));
        }
    }
    Ok(())
}

fn init_rule(s: &str) -> bool {
    let mut c = s.chars();
    c.next().is_some_and(|f| f.is_ascii_alphabetic() || f == '_')
        && c.all(|x| x.is_ascii_alphanumeric() || matches!(x, '_' | '@' | '?' | '$'))
}

/// Parses (JSONC) and validates a native.json (rules of the schema: id pattern, semver, dll names, no init with
/// phase dllmain).
pub fn parse_native(text: &str) -> Result<NativeManifest> {
    let root = jsonc::parse_tree(text)
        .map_err(|e| Error::new(ErrorCode::Parse, format!("{NATIVE_MANIFEST}: not valid JSON ({e})")))?;
    if !root.is_object() {
        return Err(nbad("the file must contain one JSON object"));
    }
    if let Some(f) = root.get("format").filter(|n| !n.is_null()) {
        match f.as_number().and_then(|n| n.parse::<u64>().ok()) {
            Some(1) => {}
            Some(n) if n > 1 => return Err(nbad(format!("format {n} is newer than this version understands (1)"))),
            _ => return Err(nbad("format must be 1")),
        }
    }
    let id = match root.get("id").and_then(Node::as_str) {
        Some(id) => id,
        None if root.get("id").is_some_and(|n| !n.is_null()) => return Err(nbad("'id' must be a string")),
        None => return Err(nbad("'id' is required")),
    };
    if !id_rule(id) {
        return Err(nbad(format!("id '{id}' must match {ID_PATTERN}")));
    }
    let version = match root.get("version").and_then(Node::as_str) {
        Some(v) => v,
        None if root.get("version").is_some_and(|n| !n.is_null()) => return Err(nbad("'version' must be a string")),
        None => return Err(nbad("'version' is required")),
    };
    if SemVersion::try_parse(version).is_none() {
        return Err(nbad(format!("version '{version}' is not a semantic version (e.g. 1.2.0)")));
    }
    for k in ["$schema", "name", "description", "license", "url"] {
        opt_string(&root, k)?;
    }
    if let Some(a) = root.get("authors").filter(|n| !n.is_null())
        && a.as_str().is_none()
        && !(a.is_array() && a.elements().iter().all(|e| e.node.as_str().is_some_and(|s| !s.is_empty())))
    {
        return Err(nbad("'authors' must be a string or an array of non-empty strings"));
    }
    if let Some(s) = root.get("side").filter(|n| !n.is_null())
        && !matches!(s.as_str(), Some("server" | "client" | "both"))
    {
        return Err(nbad(format!("side {} must be server, client or both", s.raw(text))));
    }
    if let Some(c) = root.get("clients").filter(|n| !n.is_null())
        && !matches!(c.as_str(), Some("none" | "kiln" | "mod"))
    {
        return Err(nbad(format!("clients {} must be none, kiln or mod", c.raw(text))));
    }
    let Some(dlls) = root.get("dlls").filter(|n| !n.is_null()) else {
        return Err(nbad("'dlls' is required (the DLLs to load, e.g. [\"my-mod.dll\"])"));
    };
    if !dlls.is_array() || dlls.elements().is_empty() {
        return Err(nbad("'dlls' must be a non-empty array of DLL paths"));
    }
    let mut seen = std::collections::BTreeSet::new();
    for e in dlls.elements() {
        let Some(d) = e.node.as_str() else {
            return Err(nbad("'dlls' must be a non-empty array of DLL paths"));
        };
        if !is_rel_path(d) || !ends_with_ci(d, ".dll") || d.len() <= 4 {
            return Err(nbad(format!("dlls: '{d}' must be a relative path of a .dll in the mod folder ('/' separators, no '..')")));
        }
        if !seen.insert(d) {
            return Err(nbad(format!("dlls: '{d}' is listed twice")));
        }
    }
    let phase = match root.get("phase").filter(|n| !n.is_null()) {
        None => NativePhase::Early,
        Some(p) => match p.as_str() {
            Some("early") => NativePhase::Early,
            Some("boot") => NativePhase::Boot,
            Some("world") => NativePhase::World,
            Some("dllmain") => NativePhase::Dllmain,
            _ => return Err(nbad(format!("phase {} must be early, boot, world or dllmain", p.raw(text)))),
        },
    };
    if let Some(i) = root.get("init").filter(|n| !n.is_null()) {
        match i.as_str() {
            Some(s) if init_rule(s) => {}
            _ => return Err(nbad(format!("init {} must be the name of an export of dlls[0] (or null)", i.raw(text)))),
        }
        if phase == NativePhase::Dllmain {
            return Err(nbad("init must be null for phase dllmain (the proxy only loads the DLL)"));
        }
    }
    for k in ["after", "before", "requires"] {
        id_list(&root, k)?;
    }
    if let Some(x) = root.get("incompatible").filter(|n| !n.is_null()) {
        if x.is_array() {
            id_list(&root, "incompatible")?;
        } else if x.is_object() {
            for m in x.members() {
                if !id_rule(&m.key) {
                    return Err(nbad(format!("incompatible: '{}' is not a valid mod id", m.key)));
                }
                let Some(r) = m.value.as_str() else {
                    return Err(nbad(format!("incompatible.{} must be a range", m.key)));
                };
                VersionRange::try_parse(r).map_err(|e| nbad(format!("incompatible '{}': {e}", m.key)))?;
            }
        } else {
            return Err(nbad("incompatible must be an array of ids or an object (id -> range)"));
        }
    }
    if let Some(e) = root.get("enabled")
        && e.as_bool().is_none()
    {
        return Err(nbad("enabled must be true or false"));
    }
    if let Some(k) = root.get("kiln").filter(|n| !n.is_null()) {
        let Some(r) = k.as_str() else { return Err(nbad("'kiln' must be a version range string")) };
        VersionRange::try_parse(r).map_err(|e| nbad(format!("kiln: {e}")))?;
    }
    if let Some(b) = root.get("builds").filter(|n| !n.is_null()) {
        let ok = b.is_array()
            && b.elements().iter().all(|e| {
                e.node.as_str().is_some_and(|s| s.len() == 64 && s.bytes().all(|c| c.is_ascii_digit() || (b'a'..=b'f').contains(&c)))
            });
        if !ok {
            return Err(nbad("builds must be an array of lower-case sha256 hex strings"));
        }
    }
    serde_json::from_value::<NativeManifest>(root.to_value()).map_err(nbad)
}

/// `<dir>/native.json`, parsed and validated.
pub fn read_native(dir: &Path) -> Result<NativeManifest> {
    parse_native(&read_text(&dir.join(NATIVE_MANIFEST))?)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn b(v: &str, c: Option<&str>) -> KilnBuild {
        KilnBuild::from_parts(v, c).unwrap()
    }

    /// The same cases as Kiln.Host.Tests' NewerRuntimeTests.Builds_order_by_version_then_commit_time_then_day.
    #[test]
    fn builds_order_by_version_then_commit_time_then_day() {
        let old = b("0.2.0-dev+20261106.10a0d37", Some("2026-11-06T10:54:02Z"));
        let new = b("0.2.0-dev+20261106.0f0a3f6", Some("2026-11-06T12:12:16Z"));
        assert!(new.is_newer_than(&old));
        assert!(!old.is_newer_than(&new));
        assert!(!new.is_newer_than(&new));
        assert!(!b("0.2.0-dev+20261106.0f0a3f6", None).is_newer_than(&old));
        assert!(b("0.2.0-dev+20261107.0f0a3f6", None).is_newer_than(&old));
        assert!(b("0.2.1-dev+20261101.0f0a3f6", Some("2026-11-01T00:00:00Z")).is_newer_than(&old));
        assert!(!b("0.2.0-dev+20261201.0f0a3f6", Some("2026-12-01T00:00:00Z")).is_newer_than(&b("0.3.0", None)));
        assert!(KilnBuild::from_parts("not a version", None).is_none());
        assert_eq!(b("0.2.0", Some("yesterday")).created, None);
    }

    fn example(name: &str) -> String {
        std::fs::read_to_string(Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/examples").join(name)).unwrap()
    }

    #[test]
    fn native_examples_parse() {
        let n = parse_native(&example("native.json")).unwrap();
        assert_eq!((n.id.as_str(), n.phase, n.dlls.len()), ("creative-mode", NativePhase::Early, 2));
        assert_eq!(n.init.as_deref(), Some("kiln_native_init"));
        let p = parse_native(&example("native-plain.json")).unwrap();
        assert_eq!((p.phase, p.side, p.enabled, p.init), (NativePhase::Dllmain, Side::Client, true, None));
    }

    #[test]
    fn native_rules() {
        let base = r#"{"id": "m", "version": "1.0.0", "dlls": ["m.dll"]"#;
        let with = |extra: &str| format!("{base}{extra}}}");
        assert!(parse_native(&with("")).is_ok());
        for (extra, want) in [
            (r#", "phase": "dllmain", "init": "go""#, "init must be null for phase dllmain"),
            (r#", "format": 2"#, "format 2 is newer"),
            (r#", "phase": "late""#, "phase \"late\" must be"),
            (r#", "after": ["Bad"]"#, "after: 'Bad' is not a valid mod id"),
            (r#", "builds": ["ABC"]"#, "builds must be"),
            (r#", "kiln": "abc""#, "kiln: version range"),
            (r#", "init": "1x""#, "init \"1x\" must be"),
        ] {
            let e = parse_native(&with(extra)).unwrap_err();
            assert_eq!(e.code, ErrorCode::Schema, "{extra}");
            assert!(e.message.contains(want), "{extra}: {}", e.message);
        }
        for dlls in [r#"[]"#, r#"["../m.dll"]"#, r#"["m.exe"]"#, r#"["a\\m.dll"]"#, r#"["/m.dll"]"#, r#"["m.dll", "m.dll"]"#] {
            let t = format!(r#"{{"id": "m", "version": "1", "dlls": {dlls}}}"#);
            assert!(parse_native(&t).is_err(), "{dlls}");
        }
        assert!(parse_native(r#"{"id": "m", "version": "1", "dlls": ["lib/m.DLL"],}"#).is_ok());
        assert_eq!(parse_native("{").unwrap_err().code, ErrorCode::Parse);
        assert_eq!(parse_native(r#"{"version": "1", "dlls": ["m.dll"]}"#).unwrap_err().message, "native.json: 'id' is required");
    }

    #[test]
    fn a_mod_without_entry_assembly_is_a_mod_of_data_only() {
        let m = parse_kiln(r#"{"id": "pack-only", "version": "1.0.0", "apiVersion": "0.2", "side": "both"}"#).unwrap();
        assert_eq!((m.entry_assembly.as_str(), m.clients), ("", Clients::Mod));
        let m = parse_kiln(r#"{"id": "n", "version": "1", "apiVersion": "0.2", "entryAssembly": null}"#).unwrap();
        assert_eq!(m.entry_assembly, "");
        assert!(parse_kiln(r#"{"id": "n", "version": "1", "apiVersion": "0.2", "entryAssembly": ""}"#).is_err());
        assert!(parse_kiln(r#"{"id": "n", "version": "1", "apiVersion": "0.2", "entryAssembly": "n.dll"}"#).is_ok());
    }

    #[test]
    fn kiln_id_rule_and_files() {
        assert!(is_mod_id("a.b-c_d") && is_mod_id("abc\n") && !is_mod_id("Abc") && !is_mod_id(&"a".repeat(65)));
        let dir = std::env::temp_dir().join(format!("kiln-manifest-test-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let text = r#"{"id": "x", "version": "1.2", "apiVersion": "0.2", "entryAssembly": "X.dll", "side": "client"}"#;
        let mut bom = b"\xEF\xBB\xBF".to_vec();
        bom.extend_from_slice(text.as_bytes());
        std::fs::write(dir.join(KILN_MANIFEST), &bom).unwrap();
        let m = read_kiln(&dir).unwrap();
        assert_eq!((m.parsed_version.as_str(), m.clients), ("1.2.0", Clients::Mod));
        let utf16: Vec<u8> = [0xFF, 0xFE].into_iter().chain(text.encode_utf16().flat_map(|u| u.to_le_bytes())).collect();
        std::fs::write(dir.join(KILN_MANIFEST), utf16).unwrap();
        assert_eq!(read_kiln(&dir).unwrap().id, "x");
        assert_eq!(read_kiln(&dir.join("missing")).unwrap_err().code, ErrorCode::NotFound);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
