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

use crate::jsonc::{set_value, Node, PathSeg};
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
    resolve_game_folder(game_dir, p).unwrap_or_else(|| game_dir.join("mods"))
}

fn resolve_game_folder(game_dir: &Path, p: &str) -> Option<PathBuf> {
    let host = PathBuf::from(p);
    if host.is_absolute() && host.is_dir() && (cfg!(windows) || !p.starts_with("//")) {
        return Some(host);
    }
    let plain = crate::winpath::strip_verbatim(&p.replace('/', "\\")).into_owned();
    if !crate::winpath::is_absolute_game_path(&plain) {
        return Some(game_dir.join(p.replace('\\', "/")));
    }
    if let Some(z) = crate::winpath::z_to_unix(&plain).filter(|h| h.is_dir()) {
        return Some(z);
    }
    // another drive of the prefix (or a Windows path read on another system): the part inside the game folder
    let win: Vec<&str> = plain.split('\\').filter(|c| !c.is_empty()).skip(1).collect(); // without the drive
    let game: Vec<String> = game_dir.components().filter_map(|c| match c {
        std::path::Component::Normal(n) => Some(n.to_string_lossy().into_owned()),
        _ => None,
    }).collect();
    for k in (1..=win.len().min(game.len())).rev() {
        if win[..k].iter().zip(&game[game.len() - k..]).all(|(a, b)| a.eq_ignore_ascii_case(b)) {
            return Some(win[k..].iter().fold(game_dir.to_path_buf(), |dir, c| dir.join(c)));
        }
    }
    // A Windows folder this system cannot name must not turn into a relative path containing a drive letter.
    None
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
    /// What the mod declares: "live" or "restart" (ShroudForge's default is "restart" unless `restartRequired` is false).
    /// Whether a change really applies while the game runs is [`SfSetting::live`].
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub apply_at: Option<String>,
    /// A change applies while the game runs (ShroudForge's `settings_to_apply_live`): the mod has a runtime part, and it
    /// has no startup (`patch`) part or the setting is declared live. Filled by [`read_mod`].
    #[serde(default)]
    pub live: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub step: Option<f64>,
    /// value -> label (select, radio, segmented, multiselect)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub options: Option<serde_json::Map<String, serde_json::Value>>,
    /// The option keys in the order the mod declares them (the map above is sorted); [`option_keys`].
    #[serde(skip)]
    pub option_order: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub enabled_when: Option<serde_json::Value>,
    /// text length limits (`minLength`, `maxLength`, in characters)
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub min_length: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub max_length: Option<u64>,
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
    /// Switching it on or off applies while the game runs (a runtime mod without startup asset changes); otherwise at
    /// the next start. ShroudForge's `refresh_runtime_configuration`.
    #[serde(default)]
    pub switch_live: bool,
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
    /// Settings whose whole numbers an earlier Kiln wrote without their fraction: ShroudForge refuses the mod until the
    /// next change (of the switch or any setting) puts the fraction back. Read as the fractional settings they were.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub repair: Vec<String>,
}

/// The largest package file Kiln reads or writes (the in-game page's bound too: ShroudForge.cs `MaxFileBytes`).
pub const MAX_FILE_BYTES: usize = 2 * 1024 * 1024;

/// A package file (mod.json, extended.mod.json) as ShroudForge's serde_json takes it: UTF-8 (a byte order mark is
/// dropped), no lone surrogate escape, no number beyond a double; and at most [`MAX_FILE_BYTES`]. Comments and trailing
/// commas are accepted as everywhere in Kiln. Returns the text (without the mark) and its tree.
fn read_text(p: &Path) -> Result<(String, Node)> {
    use std::io::Read;
    let f = std::fs::File::open(p).map_err(crate::util::io_at(p, "reading"))?;
    let mut bytes = Vec::new();
    f.take(MAX_FILE_BYTES as u64 + 1).read_to_end(&mut bytes).map_err(crate::util::io_at(p, "reading"))?;
    if bytes.len() > MAX_FILE_BYTES {
        return Err(Error::invalid(format!("{} is larger than 2 MiB", p.display())));
    }
    let (text, tree) = crate::jsonc::parse_tree_bytes(&bytes)?;
    if let Some(why) = refused_token(&text, &tree) {
        return Err(Error::invalid(format!("{}: {why}", p.display())));
    }
    Ok((text, tree))
}

/// What ShroudForge's serde_json refuses that Kiln's JSONC reader takes: a lone surrogate escape in a string or key,
/// a number beyond a double.
fn refused_token(text: &str, n: &Node) -> Option<String> {
    use crate::jsonc::JValue;
    match &n.value {
        JValue::Number(s) if s.parse::<f64>().is_ok_and(f64::is_infinite) => Some(format!("the number {s} is beyond a double")),
        JValue::String(_) if lone_surrogate(n.raw(text)) => Some("a text holds a lone surrogate escape".into()),
        JValue::Array(items) => items.iter().find_map(|e| refused_token(text, &e.node)),
        JValue::Object(members) => members.iter().find_map(|m| {
            if lone_surrogate(m.key_span.slice(text)) { Some("a key holds a lone surrogate escape".into()) } else { refused_token(text, &m.value) }
        }),
        _ => None,
    }
}

/// A string token (quotes included) with a `\uD800`-`\uDFFF` escape that is not half of a pair.
fn lone_surrogate(token: &str) -> bool {
    let b = token.as_bytes();
    let hex = |i: usize| b.get(i..i + 4).and_then(|h| std::str::from_utf8(h).ok()).and_then(|h| u16::from_str_radix(h, 16).ok());
    let mut i = 0;
    while i < b.len() {
        if b[i] != b'\\' {
            i += 1;
            continue;
        }
        if b.get(i + 1) != Some(&b'u') {
            i += 2;
            continue;
        }
        match hex(i + 2) {
            Some(0xD800..=0xDBFF) => {
                let low = (b.get(i + 6) == Some(&b'\\') && b.get(i + 7) == Some(&b'u')).then(|| hex(i + 8)).flatten();
                if !matches!(low, Some(0xDC00..=0xDFFF)) {
                    return true;
                }
                i += 12;
            }
            Some(0xDC00..=0xDFFF) => return true,
            _ => i += 6,
        }
    }
    false
}

fn read_json_file(p: &Path) -> Result<serde_json::Value> {
    Ok(read_text(p)?.1.to_value())
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

/// (has a runtime part, has a startup asset part): ShroudForge's `Capability::requires_runtime` and `Patch`.
fn runtime_parts(capabilities: &[String]) -> (bool, bool) {
    (
        capabilities.iter().any(|c| c == "runtime" || c == "runtime-register-dll"),
        capabilities.iter().any(|c| c == "patch"),
    )
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
            apply_at: Some("restart".into()),
            live: false,
            min: None,
            max: None,
            step: None,
            options: None,
            option_order: Vec::new(),
            enabled_when: None,
            min_length: None,
            max_length: None,
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
        // ShroudForge (read_mod_settings.rs): applyAt, else "live" when restartRequired is false, else "restart"
        apply_at: Some(o.get("applyAt").and_then(|s| s.as_str()).map(str::to_owned).unwrap_or_else(|| {
            if o.get("restartRequired").and_then(|b| b.as_bool()) == Some(false) { "live" } else { "restart" }.to_owned()
        })),
        live: false,
        min: f("min"),
        max: f("max"),
        step: f("step"),
        options: o.get("options").and_then(|v| v.as_object()).cloned(),
        option_order: Vec::new(),
        enabled_when: o.get("enabledWhen").cloned(),
        min_length: o.get("minLength").and_then(|v| v.as_u64()),
        max_length: o.get("maxLength").and_then(|v| v.as_u64()),
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
    let tree = if extended { Some(read_text(&ext_path)?.1) } else { None };
    let ext = tree.as_ref().map(Node::to_value).unwrap_or_else(|| serde_json::json!({}));
    let launcher = str_of(&ext, "launcher").or_else(|| (!extended).then(|| "EML".to_owned()));
    let targets = match ext.get("targets").and_then(|t| t.as_array()) {
        Some(a) => a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect(),
        // ShroudForge: legacy EML packages run on both sides, ShroudForge packages on the client
        None if launcher.as_deref() == Some("EML") => vec!["client".into(), "server".into()],
        None => vec!["client".into()],
    };
    let mut settings: Vec<SfSetting> = ext
        .get("settings")
        .and_then(|s| s.as_object())
        .map(|o| o.iter().map(|(k, v)| parse_setting(k, v)).collect())
        .unwrap_or_default();
    // the order the mod declares them in (ShroudForge's own editor shows that order); a repeated key stands where it
    // first appears and holds its last value, as in ShroudForge's serde_json (preserve_order)
    if let Some(declared) = tree.as_ref().and_then(|t| t.get("settings")) {
        let (mut first, mut last) = (std::collections::HashMap::new(), std::collections::HashMap::new());
        for (i, m) in declared.members().iter().enumerate() {
            first.entry(m.key.as_str()).or_insert(i);
            last.insert(m.key.as_str(), &m.value);
        }
        settings.sort_by_cached_key(|s| first.get(s.key.as_str()).copied().unwrap_or(usize::MAX));
        for s in &mut settings {
            let options = last.get(s.key.as_str()).filter(|_| !s.shorthand).and_then(|n| n.get("options"));
            let mut seen = std::collections::HashSet::new();
            s.option_order = options.map(|o| o.members().iter().filter(|m| seen.insert(m.key.as_str())).map(|m| m.key.clone()).collect()).unwrap_or_default();
        }
    }
    let capabilities: Vec<String> =
        m.get("capabilities").and_then(|a| a.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect()).unwrap_or_default();
    let (runtime, patch) = runtime_parts(&capabilities);
    let mut repair = Vec::new();
    for s in &mut settings {
        if recover_fraction(s) {
            repair.push(s.key.clone());
        }
        let raw = &ext["settings"][&s.key];
        if s.value.is_null() || s.value.is_object() ||
            !matches!(s.apply_at.as_deref(), Some("live" | "restart")) ||
            raw.get("restartRequired").and_then(|v| v.as_bool()).is_some_and(|r| r != (s.apply_at.as_deref() == Some("restart"))) ||
            (s.value.is_array() && s.options.as_ref().is_none_or(|o| o.is_empty())) ||
            // ShroudForge's schema: options name at least one choice ("options": {} refuses the mod)
            raw.get("options").is_some_and(|o| o.as_object().is_none_or(|o| o.is_empty())) ||
            s.options.as_ref().is_some_and(|o| o.iter().any(|(k, label)| option_value(s, k).is_none() || !label.is_string())) ||
            s.enabled_when.as_ref().is_some_and(|w| w.get("setting").and_then(|v| v.as_str()).is_none() || w.get("equals").is_none())
        {
            return Err(Error::invalid(format!("setting {} has invalid metadata", s.key)));
        }
        s.live = runtime && (!patch || s.apply_at.as_deref() == Some("live"));
    }
    Ok(SfMod {
        name: str_of(&m, "name").unwrap_or_else(|| id.clone()),
        id,
        version: str_of(&m, "version").unwrap_or_default(),
        description: str_of(&m, "description").unwrap_or_default(),
        authors: m.get("authors").and_then(|a| a.as_array()).map(|a| a.iter().filter_map(|v| v.as_str().map(str::to_owned)).collect()).unwrap_or_default(),
        folder: folder.to_path_buf(),
        enabled: ext.get("enabled").and_then(|b| b.as_bool()).or_else(|| m.get("enabled").and_then(|b| b.as_bool())).unwrap_or(false),
        targets,
        switch_live: runtime && !patch,
        capabilities,
        dependencies: m.get("dependencies").and_then(|a| a.as_array()).cloned().unwrap_or_default(),
        settings,
        groups: ext.get("groups").and_then(|a| a.as_array()).cloned().unwrap_or_default(),
        launcher,
        extended,
        repair,
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
        return Ok(read_text(&p)?.0);
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
    if text.len() > MAX_FILE_BYTES {
        return Err(Error::invalid("the settings file would be larger than 2 MiB"));
    }
    crate::util::write_atomic(&m.folder.join("extended.mod.json"), text.as_bytes())
}

/// A whole number written without its fraction by an earlier Kiln (`[2, 1.5]` for a fractional list, `2` for a
/// fractional select): ShroudForge then types the setting as an integer and refuses its fractional option keys, so the mod
/// no longer loads. When every option key is a finite number and one of them is not whole, the setting can only have been
/// the fractional one: it is read as that, and [`repair_fractions`] puts the fraction back on the next write. True when
/// the setting was read so.
fn recover_fraction(s: &mut SfSetting) -> bool {
    let Some(opts) = s.options.as_ref().filter(|o| !o.is_empty()) else { return false };
    if option_type(s) != "integer"
        || !opts.keys().all(|k| k.parse::<f64>().is_ok_and(f64::is_finite))
        || opts.keys().all(|k| k.parse::<i64>().is_ok())
    {
        return false;
    }
    let fractional = |v: &mut serde_json::Value| {
        if let Some(n) = v.as_f64().and_then(serde_json::Number::from_f64) {
            *v = serde_json::Value::Number(n);
        }
    };
    match &mut s.value {
        serde_json::Value::Array(items) if items.iter().all(|v| v.is_number()) => items.iter_mut().for_each(fractional),
        v @ serde_json::Value::Number(_) => fractional(v),
        _ => return false,
    }
    true
}

/// A number token ShroudForge's serde_json reads as an integer (`as_i64`): `2`, not `2.0`, `-0` or `1e2`.
fn integer_token(n: &Node) -> bool {
    n.as_number().is_some_and(|t| serde_json::from_str::<serde_json::Value>(t).is_ok_and(|v| v.as_i64().is_some()))
}

/// `text` with the fraction put back on the whole numbers of every setting [`recover_fraction`] read as fractional
/// (`2` becomes `2.0`); nothing else changes.
fn repair_fractions(text: &str, m: &SfMod) -> Result<String> {
    let root = crate::jsonc::parse_tree(text)?;
    // the last member of each key, as ShroudForge reads it
    let declared: std::collections::HashMap<&str, &Node> =
        root.get("settings").map(|n| n.members().iter().map(|m| (m.key.as_str(), &m.value)).collect()).unwrap_or_default();
    let mut at: Vec<usize> = Vec::new();
    for s in m.settings.iter().filter(|s| option_type(s) == "number") {
        let Some(mut node) = declared.get(s.key.as_str()).copied() else { continue };
        if !s.shorthand {
            let Some(v) = node.get("value") else { continue };
            node = v;
        }
        let tokens: Vec<&Node> = if node.is_array() { node.elements().iter().map(|e| &e.node).collect() } else { vec![node] };
        if tokens.first().is_some_and(|n| integer_token(n)) {
            at.extend(tokens.iter().filter(|n| integer_token(n)).map(|n| n.span.end));
        }
    }
    let mut out = text.to_string();
    at.sort_unstable();
    for i in at.into_iter().rev() {
        out.insert_str(i, ".0");
    }
    Ok(out)
}

/// A value as text, typed the way ShroudForge reads it back (it types a setting by its value, a list by its first
/// member): the numbers of a fractional setting as serde_json writes a double (always with a fraction or an exponent:
/// `2.0`, `0.75`, `1e-7`), those of an integer setting as integers, anything else as [`crate::jsonc::render`] writes it.
fn render_setting_value(s: &SfSetting, value: &serde_json::Value) -> String {
    let ty = option_type(s);
    let one = |v: &serde_json::Value| -> String {
        match v {
            serde_json::Value::Number(n) if ty == "number" => {
                n.as_f64().and_then(serde_json::Number::from_f64).map(|f| f.to_string()).unwrap_or_else(|| n.to_string())
            }
            serde_json::Value::Number(n) if ty == "integer" && n.is_f64() => {
                n.as_f64().filter(|f| f.fract() == 0.0 && f.abs() < 9.0e15).map(|f| (f as i64).to_string()).unwrap_or_else(|| n.to_string())
            }
            v => crate::jsonc::render(v, ""),
        }
    };
    match value {
        serde_json::Value::Array(items) if items.iter().all(|v| !v.is_array() && !v.is_object()) => {
            format!("[{}]", items.iter().map(one).collect::<Vec<_>>().join(", "))
        }
        serde_json::Value::Array(_) | serde_json::Value::Object(_) => crate::jsonc::render(value, ""),
        v => one(v),
    }
}

/// Switches a mod on or off (ShroudForge's own switch; a running ShroudForge picks it up live).
pub fn set_enabled(folder: &Path, enabled: bool) -> Result<SfMod> {
    let m = read_mod(folder)?;
    if m.extended && m.enabled == enabled {
        return Ok(m);
    }
    let text = repair_fractions(&extension_text(&m)?, &m)?;
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
    // ShroudForge types a setting by its declared value: a whole number is an integer setting
    if s.value.is_i64() {
        let whole = value.is_i64() || value.as_f64().is_some_and(|n| n.fract() == 0.0 && n.abs() < 9.0e15);
        if !whole {
            return Err(Error::invalid(format!("setting {} holds a whole number, not {value}", s.key)));
        }
    }
    if let Some(text) = value.as_str() {
        if s.control.as_deref() == Some("keybind") && !valid_shortcut(text) {
            return Err(Error::invalid(format!("setting {} requires a canonical SDL shortcut (e.g. Ctrl+SDL:66 or Mouse:4)", s.key)));
        }
        let n = text.chars().count() as u64;
        if s.min_length.is_some_and(|m| n < m) || s.max_length.is_some_and(|m| n > m) {
            return Err(Error::invalid(format!(
                "setting {} must be {}..{} characters long",
                s.key,
                s.min_length.unwrap_or(0),
                s.max_length.map(|v| v.to_string()).unwrap_or_default()
            )));
        }
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
    if let Some(opts) = s.options.as_ref().filter(|o| !o.is_empty()) {
        let ok = |v: &V| opts.keys().filter_map(|k| option_value(s, k)).any(|o| same_value(&o, v));
        let all = match value {
            V::Array(items) => items.iter().all(ok),
            v => ok(v),
        };
        if !all {
            return Err(Error::invalid(format!("setting {}: not one of {}", s.key, option_keys(s).iter().map(|k| k.as_str()).collect::<Vec<_>>().join(", "))));
        }
    }
    Ok(())
}

/// SF's canonical portable shortcut syntax. SF's SDL backend additionally checks keyboard scancode availability.
pub fn valid_shortcut(text: &str) -> bool {
    if text.is_empty() { return true; }
    if text.len() > 100 { return false; }
    let (mut keyboard, mut mouse) = (false, false);
    for chord in text.split('|') {
        let parts: Vec<_> = chord.split('+').collect();
        let Some((device, number)) = parts[parts.len() - 1].split_once(':') else { return false };
        let Ok(code) = number.parse::<u16>() else { return false };
        if code.to_string() != number { return false; }
        let primary = match device {
            "SDL" if !keyboard && !mouse && (1..512).contains(&code) => {
                keyboard = true;
                match code { 224 | 228 => 1, 226 | 230 => 2, 225 | 229 => 4, 227 | 231 => 8, _ => 0 }
            }
            "Mouse" if !mouse && (1..=32).contains(&code) => { mouse = true; 0 }
            _ => return false,
        };
        let mut previous = 0;
        for modifier in &parts[..parts.len() - 1] {
            let bit = match *modifier { "Ctrl" => 1, "Alt" => 2, "Shift" => 4, "Meta" => 8, _ => 0 };
            if bit <= previous || bit & primary != 0 { return false; }
            previous = bit;
        }
    }
    true
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
    let text = repair_fractions(&extension_text(&m)?, &m)?;
    let mut path = vec![PathSeg::Key("settings".into()), PathSeg::Key(key.into())];
    if !s.shorthand {
        path.push(PathSeg::Key("value".into()));
    }
    // ShroudForge types a setting by the value it finds in the file: a whole value of a fractional setting is written
    // as 2.0 (as ShroudForge itself writes it), or it would read the setting as a whole number from then on
    let root = crate::jsonc::parse_tree(&text)?;
    let span = root.at(&path).ok_or_else(|| Error::not_found(format!("{} has no setting {key}", m.id)))?.span;
    let out = format!("{}{}{}", &text[..span.start], render_setting_value(s, value), &text[span.end..]);
    write_extension(&m, &out)?;
    read_mod(folder)
}

/// A JSON Schema of the mod's settings for the launcher's SchemaForm (titles, descriptions, ranges, enums; `x-kiln-*`
/// hints for the control, the group and "applies after a restart").
pub fn settings_schema(m: &SfMod) -> serde_json::Value {
    let mut props = serde_json::Map::new();
    // each key's group (the first that lists it), indexed once: a package may declare many settings
    let mut groups: std::collections::HashMap<&str, &str> = std::collections::HashMap::new();
    for g in &m.groups {
        let label = g.get("label").and_then(|l| l.as_str()).unwrap_or("Settings");
        for key in g.get("settings").and_then(|s| s.as_array()).into_iter().flatten().filter_map(|v| v.as_str()) {
            groups.entry(key).or_insert(label);
        }
    }
    let group_of = |key: &str| groups.get(key).map(|l| (*l).to_owned());
    for s in &m.settings {
        let mut p = serde_json::Map::new();
        let ty = value_type(s);
        p.insert("type".into(), ty.into());
        p.insert("title".into(), label_of(s).into());
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
        if let Some(n) = s.min_length { p.insert("minLength".into(), n.into()); }
        if let Some(n) = s.max_length { p.insert("maxLength".into(), n.into()); }
        if let Some(opts) = s.options.as_ref().filter(|o| !o.is_empty()) {
            let order = option_keys(s);
            let keys: Vec<serde_json::Value> = order.iter().filter_map(|k| option_value(s, k)).collect();
            let labels: Vec<serde_json::Value> = order.iter().filter_map(|k| opts.get(k.as_str()).cloned()).collect();
            if ty == "array" {
                p.insert("items".into(), serde_json::json!({ "type": option_type(s), "enum": keys, "x-kiln-enum-labels": labels }));
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
        if !s.live {
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

/// ShroudForge's type of a setting, from its declared value: boolean, integer, number, string or array.
pub fn value_type(s: &SfSetting) -> &'static str {
    match &s.value {
        serde_json::Value::Bool(_) => "boolean",
        serde_json::Value::Number(n) if n.is_i64() => "integer",
        serde_json::Value::Number(_) => "number",
        serde_json::Value::Array(_) => "array",
        _ => "string",
    }
}

fn option_type(s: &SfSetting) -> &'static str {
    if let Some(a) = s.value.as_array() {
        match a.first() {
            Some(serde_json::Value::Bool(_)) => "boolean",
            Some(serde_json::Value::Number(n)) if n.is_i64() => "integer",
            Some(serde_json::Value::Number(_)) => "number",
            _ => "string",
        }
    } else { value_type(s) }
}

/// An option key typed as ShroudForge types it (the scalar value, or the first array member; an empty array uses text),
/// with its rules (read_mod_settings.rs): a yes/no key is true only as "true"; numbers are read by Rust's `str::parse`, and
/// a fractional key that is not a finite double is an option whose value is null. None: ShroudForge refuses the key.
pub fn option_value(s: &SfSetting, key: &str) -> Option<serde_json::Value> {
    match option_type(s) {
        "boolean" => Some((key == "true").into()),
        "integer" => key.parse::<i64>().ok().map(serde_json::Value::from),
        "number" => key.parse::<f64>().ok().map(|f| serde_json::Number::from_f64(f).map_or(serde_json::Value::Null, serde_json::Value::Number)),
        _ => Some(key.into()),
    }
}

/// The option keys in the order the mod declares them.
pub fn option_keys(s: &SfSetting) -> Vec<&String> {
    let Some(opts) = &s.options else { return Vec::new() };
    let declared: Vec<&String> = s.option_order.iter().filter(|k| opts.contains_key(k.as_str())).collect();
    if declared.len() == opts.len() { declared } else { opts.keys().collect() }
}

fn same_value(a: &serde_json::Value, b: &serde_json::Value) -> bool {
    match (a.as_f64(), b.as_f64()) {
        (Some(a), Some(b)) => a == b,
        _ => a == b,
    }
}

/// The label ShroudForge shows: `label`, else the key with a space before each capital (`noFallDamage` -> "no Fall
/// Damage").
pub fn label_of(s: &SfSetting) -> String {
    if let Some(l) = &s.label {
        return l.clone();
    }
    let mut out = String::with_capacity(s.key.len() + 4);
    for (i, c) in s.key.chars().enumerate() {
        if i > 0 && c.is_uppercase() {
            out.push(' ');
        }
        out.push(c);
    }
    out
}

/// The control ShroudForge draws: `control`, else a select (a multiselect for a list) when the setting has options.
pub fn control_of(s: &SfSetting) -> Option<String> {
    s.control.clone().or_else(|| s.options.as_ref().filter(|o| !o.is_empty()).map(|_| {
        if s.value.is_array() { "multiselect" } else { "select" }.to_owned()
    }))
}

/// Whether the setting is editable now: its `enabledWhen` chain holds for `values` (ShroudForge's
/// `setting_is_enabled`: a missing controller or a cycle disables it; numbers compare by value, 5 = 5.0).
pub fn setting_enabled(key: &str, settings: &[SfSetting], values: &serde_json::Value) -> bool {
    let mut current = key.to_owned();
    let mut seen = std::collections::BTreeSet::new();
    loop {
        let Some(s) = settings.iter().find(|s| s.key == current) else { return false };
        let Some(cond) = &s.enabled_when else { return true };
        if !seen.insert(current.clone()) {
            return false;
        }
        let (Some(target), Some(equals)) = (cond.get("setting").and_then(|v| v.as_str()), cond.get("equals")) else {
            return false;
        };
        let Some(value) = values.get(target) else { return false };
        let same = match (value.as_f64(), equals.as_f64()) {
            (Some(a), Some(b)) if value.is_number() && equals.is_number() => a == b,
            _ => value == equals,
        };
        if !same {
            return false;
        }
        current = target.to_owned();
    }
}

/// The settings' current values as one object (key -> value), the SchemaForm's data.
pub fn settings_values(m: &SfMod) -> serde_json::Value {
    serde_json::Value::Object(m.settings.iter().map(|s| (s.key.clone(), s.value.clone())).collect())
}

/// `kiln/state/shroudforge.json`: `mode` hosted | self-hosted | not-installed | off | failed, `reason`, `version`,
/// `side`, `nativeAbi`, `native` (ShroudForge's native runtime status text), `keys` (`{source: kiln | helper | none,
/// reason}`: where mod shortcuts get their key state), `turns`, `updated` (unix seconds).
pub fn status(game_dir: &Path) -> Result<Option<serde_json::Value>> {
    crate::util::read_json(&game_dir.join(STATUS_FILE))
}

/// An observation from SF, not independent verification of a gameplay effect.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfEffect {
    pub id: String,
    pub state: String,
    pub detail: String,
}

/// SF's runtime report, separate from the adapter's "hosted" state. Only a current report has mod rows.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SfEffects {
    pub file: String,
    /// current | stale | stopped | unavailable | invalid
    pub state: String,
    pub updated_at: Option<u64>,
    pub reason: Option<String>,
    pub mods: Vec<SfEffect>,
}

fn read_effect_json(game_dir: &Path, file: &Path) -> std::result::Result<Option<serde_json::Value>, String> {
    use std::io::Read;
    crate::engine_mods::refuse_symlink_path(game_dir, file).map_err(|e| e.message)?;
    match std::fs::metadata(file) {
        Ok(meta) if !meta.is_file() => return Err("ShroudForge report must be a regular file".into()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(format!("Cannot inspect ShroudForge report: {e}")),
        _ => {}
    }
    let input = match std::fs::File::open(file) {
        Ok(input) => input,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(format!("Cannot read ShroudForge report: {e}")),
    };
    const LIMIT: u64 = 2 * 1024 * 1024;
    let mut bytes = Vec::new();
    input.take(LIMIT + 1).read_to_end(&mut bytes).map_err(|e| e.to_string())?;
    if bytes.len() as u64 > LIMIT { return Err("ShroudForge report exceeds 2 MiB".into()); }
    serde_json::from_slice(&bytes).map(Some).map_err(|_| "ShroudForge report or configuration is not valid JSON".into())
}

/// Read SF's own per-mod effects and errors. An unchanged effect remains valid while the runtime heartbeat is fresh;
/// old successes must not be shown as current merely because the adapter started. No game process is started here.
pub fn effects(game_dir: &Path) -> SfEffects {
    let mut report = SfEffects {
        file: game_dir.join("shroudforge/state.json").display().to_string(), state: "invalid".into(),
        updated_at: None, reason: None, mods: Vec::new(),
    };
    let load = || -> std::result::Result<PathBuf, String> {
        let config = read_effect_json(game_dir, &game_dir.join(CONFIG_FILE))?;
        match config.as_ref().and_then(|v| v.pointer("/paths/state")) {
            None | Some(serde_json::Value::Null) => Ok(game_dir.join("shroudforge/state.json")),
            Some(serde_json::Value::String(p)) if p.trim().is_empty() => Ok(game_dir.join("shroudforge/state.json")),
            Some(serde_json::Value::String(p)) => resolve_game_folder(game_dir, p.trim())
                .map(|p| p.join("state.json")).ok_or_else(|| "Configured ShroudForge state path is not accessible on this system".into()),
            _ => Err("ShroudForge paths.state must be a folder path".into()),
        }
    };
    let file = match load() {
        Ok(file) => file,
        Err(reason) => { report.reason = Some(reason); return report; }
    };
    report.file = file.display().to_string();
    let doc = match read_effect_json(game_dir, &file) {
        Ok(Some(doc)) => doc,
        Ok(None) => {
            report.state = "unavailable".into();
            report.reason = Some("No ShroudForge runtime report yet".into());
            return report;
        }
        Err(reason) => { report.reason = Some(reason); return report; }
    };
    let parse = || -> Option<(bool, u64, Vec<SfEffect>)> {
        let runtime = doc.get("runtime")?.as_object()?;
        let running = runtime.get("running")?.as_bool()?;
        let updated = runtime.get("updatedAt")?.as_u64()?;
        let active: Option<Vec<_>> = runtime.get("active")?.as_array()?.iter().map(|id| id.as_str()).collect();
        let active = active?;
        let mut mods = std::collections::BTreeMap::new();
        if let Some(effects) = runtime.get("effects") {
            for (id, value) in effects.as_object()? {
                if !active.contains(&id.as_str()) { continue; }
                mods.insert(id.clone(), SfEffect { id: id.clone(), state: value.get("state")?.as_str()?.into(),
                    detail: value.get("detail").map(|v| v.as_str()).unwrap_or(Some(""))?.into() });
            }
        }
        if let Some(errors) = runtime.get("errors") {
            for (id, detail) in errors.as_object()? {
                mods.insert(id.clone(), SfEffect { id: id.clone(), state: "error".into(), detail: detail.as_str()?.into() });
            }
        }
        Some((running, updated, mods.into_values().collect()))
    };
    let Some((running, updated, mods)) = parse() else {
        report.reason = Some("ShroudForge runtime report has invalid or missing fields".into());
        return report;
    };
    report.updated_at = Some(updated);
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap_or_default().as_secs();
    if !running {
        report.state = "stopped".into();
        report.reason = Some("ShroudForge runtime is stopped; previous mod effects are not current".into());
    } else if updated == 0 || now.saturating_sub(updated) > 30 || updated.saturating_sub(now) > 5 {
        report.state = "stale".into();
        report.reason = Some("ShroudForge runtime report is stale; current mod effects are unknown".into());
    } else {
        report.state = "current".into();
        report.mods = mods;
    }
    report
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
        // a runtime mod without startup asset changes: ShroudForge hands every change to the running mod
        assert!(s["properties"]["speed"].get("x-kiln-restart").is_none());
        let patch = pkg(t.path(), "p", Some(EXT));
        std::fs::write(patch.join("mod.json"), r#"{"id":"p","name":"P","version":"1.0.0","capabilities":["patch","runtime"]}"#).unwrap();
        let s = settings_schema(&read_mod(&patch).unwrap());
        assert_eq!(s["properties"]["speed"]["x-kiln-restart"], true);
        assert_eq!(s["properties"]["infiniteStamina"]["x-kiln-restart"], true, "no applyAt: ShroudForge's default is restart");
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
    fn detect_and_mods_dir() {
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
