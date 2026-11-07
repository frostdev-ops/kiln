//! Every Enshrouded mod engine Kiln knows: its proxy DLL names, the markers that identify its files, and what the
//! launcher tells a player about it (docs/kiln/universal-loader.md).
//!
//! - [`classify_markers`]: which engine a DLL belongs to, from its exports and the texts inside it (ASCII or UTF-16LE,
//!   case ignored). `pe::classify_bytes` calls it after its own Kiln / EMF / ShroudForge checks.
//! - [`scan_proxies`]: every DLL under a proxy name next to the game, classified.
//! - [`detect_dll`] / [`detect_folder`] / [`detect_file`]: the Library's detection of mods made for other engines
//!   (`library::detect` calls them), each with a reason a player can read.
//! - [`register_loader`]: how support for an engine's mod kind plugs in. A kind with a registered loader is imported
//!   through it; a kind without one is skipped with its "support is coming" reason, never imported as a plain DLL.
//!
//! The tool words and markers start from EMF's `src/core/known_tools.txt` and the archive rules of its
//! `src/nexus/classify.h` (github.com/matenm/emf-enshrouded, MIT); the other markers come from each engine's public
//! source and docs. EML's formats were learned from its documentation only.

use crate::library::{DetectKind, Detected, ImportReport, ImportSource};
use crate::paths::DataDirs;
use crate::pe::{ProxyClass, ProxyKind};
use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{OnceLock, RwLock};

/// File names DLL mods and loaders use to get into the game (the game imports them, or a DLL it imports loads them).
/// Kiln itself owns only `dinput8.dll`.
pub const PROXY_NAMES: [&str; 13] = [
    "dinput8.dll",
    "dbghelp.dll",
    "dbgcore.dll",
    "umpdc.dll",
    "cfgmgr32.dll",
    "winmm.dll",
    "powrprof.dll",
    "iphlpapi.dll",
    "version.dll",
    "xinput1_4.dll",
    "xinput9_1_0.dll",
    "dxgi.dll",
    "steam_api64.dll",
];

/// True for one of [`PROXY_NAMES`] (case ignored).
pub fn is_proxy_name(file_name: &str) -> bool {
    PROXY_NAMES.iter().any(|n| n.eq_ignore_ascii_case(file_name))
}

/// A mod engine, loader or tool that lives next to the game.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Engine {
    Kiln,
    Emf,
    #[serde(rename = "shroudforge")]
    ShroudForge,
    Eml,
    Set,
    Enshwick,
    Greeneye,
    Shroudtopia,
    Flame,
    Shroudlink,
    Ember,
    ModLoaderProxy,
    UltimateAsiLoader,
    Aldrich,
    LiveMap,
    SharedXp,
    #[serde(rename = "reshade")]
    ReShade,
    ChainLoader,
}

/// What Kiln knows about one engine.
#[derive(Debug, Clone, Copy)]
pub struct EngineInfo {
    pub engine: Engine,
    /// Short file-name word (`kiln/chain/<word>-dinput8.dll`), as in EMF's known_tools.txt where it has one.
    pub word: &'static str,
    /// The name players see.
    pub name: &'static str,
    /// The proxy names its loader uses.
    pub proxies: &'static [&'static str],
    /// Exports that identify its loader DLL.
    pub exports: &'static [&'static str],
    /// Texts inside its loader DLL, lower case (matched as ASCII or UTF-16LE, case ignored).
    pub texts: &'static [&'static str],
    /// What it is, for a player (one short phrase).
    pub what: &'static str,
}

/// In priority order: the first engine whose marker a DLL has wins (ShroudForge's DLLs carry EML's text too, EMF's
/// loader carries ShroudForge's export name, many loaders mention `dinput8-chain.ini`).
pub const ENGINES: &[EngineInfo] = &[
    EngineInfo { engine: Engine::Emf, word: "emf", name: "EMF", proxies: &["dinput8.dll"],
        exports: &["emf_loader_identity", "emf_loader_play_once"], texts: &["emf-loader-7d3c9f"],
        what: "the Enshrouded Modding Framework" },
    EngineInfo { engine: Engine::ShroudForge, word: "shroudforge", name: "ShroudForge",
        proxies: &["dinput8.dll", "dbghelp.dll", "winmm.dll"], exports: &["ShroudforgeSetDinput8Provider"],
        texts: &["shroudforge"], what: "a Lua mod loader" },
    EngineInfo { engine: Engine::Eml, word: "eml", name: "EML (Enshrouded Mod Loader)", proxies: &["dbghelp.dll", "dinput8.dll"],
        exports: &[], texts: &["eml_log_file_path"], what: "a data mod loader" },
    EngineInfo { engine: Engine::Set, word: "set", name: "Shrouded Engine Tweaks", proxies: &["powrprof.dll"],
        exports: &[], texts: &["shrouded_engine_tweaks"], what: "a Lua and overlay mod loader" },
    EngineInfo { engine: Engine::Enshwick, word: "enshwick", name: "Enshwick", proxies: &["powrprof.dll"],
        exports: &[], texts: &["enshwick"], what: "a mod framework" },
    EngineInfo { engine: Engine::Greeneye, word: "greeneye", name: "Greeneye Loader",
        proxies: &["dbgcore.dll", "umpdc.dll", "cfgmgr32.dll"], exports: &[], texts: &["greeneye"],
        what: "a DLL mod loader" },
    EngineInfo { engine: Engine::Shroudtopia, word: "shroudtopia", name: "Shroudtopia", proxies: &["winmm.dll"],
        exports: &[], texts: &["shroudtopia"], what: "a DLL mod loader (also its Shroudweave fork)" },
    EngineInfo { engine: Engine::Flame, word: "flame", name: "Flame", proxies: &["dinput8.dll", "powrprof.dll"],
        exports: &[], texts: &["flamestartruntime", "flame.json"], what: "Creative Mode's loader" },
    // shroudlink 0.8 (github.com/bjck/shroudlink, MIT): the server proxy renames Steam's DLL to steam_api64_o.dll and
    // logs to shroudlink.log (mod/server/src/steam_proxy.rs, lib.rs)
    EngineInfo { engine: Engine::Shroudlink, word: "shroudlink", name: "shroudlink", proxies: &["steam_api64.dll", "dinput8.dll"],
        exports: &[], texts: &["shroudlink", "steam_api64_o.dll", "shroudlink.log"], what: "a server mod framework" },
    // Ember has no loader release: its ember-loader crate holds no identifying string yet, so the crate name (in Rust's
    // panic paths) stays the marker until a release shows better ones
    EngineInfo { engine: Engine::Ember, word: "ember", name: "Ember (server SDK)",
        proxies: &["powrprof.dll", "iphlpapi.dll", "dbghelp.dll"], exports: &[], texts: &["ember-loader", "ember_loader"],
        what: "a server mod framework" },
    EngineInfo { engine: Engine::ModLoaderProxy, word: "modloader-proxy", name: "ModLoader Proxy",
        proxies: &["dinput8.dll", "powrprof.dll"], exports: &[], texts: &["modloader-proxy.dll", "chainloader.ini"],
        what: "a DLL chain loader" },
    EngineInfo { engine: Engine::UltimateAsiLoader, word: "ultimate-asi-loader", name: "Ultimate ASI Loader",
        proxies: &["dinput8.dll", "xinput9_1_0.dll", "version.dll", "dxgi.dll", "winmm.dll"],
        exports: &["IsUltimateASILoader"], texts: &["isultimateasiloader"], what: "an .asi plugin loader" },
    EngineInfo { engine: Engine::Aldrich, word: "aldrich-modmenu", name: "Aldrich Mod Menu", proxies: &["dinput8.dll"],
        exports: &[], texts: &["aldrich"], what: "a mod menu" },
    EngineInfo { engine: Engine::LiveMap, word: "livemap", name: "Live Map", proxies: &["iphlpapi.dll"],
        exports: &[], texts: &["livemap.ini"], what: "a stand-alone server mod" },
    EngineInfo { engine: Engine::SharedXp, word: "shared-xp", name: "Shared XP", proxies: &["powrprof.dll"],
        exports: &[], texts: &["xpshare.ini"], what: "a stand-alone server mod" },
    EngineInfo { engine: Engine::ReShade, word: "reshade", name: "ReShade", proxies: &["dxgi.dll"],
        exports: &[], texts: &["reshade"], what: "a graphics post-processing tool" },
    EngineInfo { engine: Engine::ChainLoader, word: "chain-loader", name: "a dinput8-chain.ini chain loader", proxies: &["dinput8.dll"],
        exports: &[], texts: &["dinput8-chain.ini"], what: "a DLL chain loader (XHL-Core, Auto Loot Plus and others)" },
];

const KILN_INFO: EngineInfo = EngineInfo { engine: Engine::Kiln, word: "kiln", name: "Kiln", proxies: &["dinput8.dll"],
    exports: &["KilnProxyInfo"], texts: &[], what: "this mod loader" };

/// The table row of an engine.
pub fn info(engine: Engine) -> &'static EngineInfo {
    ENGINES.iter().find(|i| i.engine == engine).unwrap_or(&KILN_INFO)
}

impl Engine {
    pub fn name(self) -> &'static str {
        info(self).name
    }

    /// The proxy classification of this engine's loader.
    pub fn proxy_kind(self) -> ProxyKind {
        match self {
            Engine::Kiln => ProxyKind::Kiln,
            Engine::Emf => ProxyKind::Emf,
            Engine::ShroudForge => ProxyKind::ShroudForge,
            Engine::Eml => ProxyKind::Eml,
            Engine::Set => ProxyKind::Set,
            Engine::Enshwick => ProxyKind::Enshwick,
            Engine::Greeneye => ProxyKind::Greeneye,
            Engine::Shroudtopia => ProxyKind::Shroudtopia,
            Engine::Flame => ProxyKind::Flame,
            Engine::Shroudlink => ProxyKind::Shroudlink,
            Engine::Ember => ProxyKind::Ember,
            Engine::ModLoaderProxy => ProxyKind::ModLoaderProxy,
            Engine::UltimateAsiLoader => ProxyKind::UltimateAsiLoader,
            Engine::Aldrich => ProxyKind::Aldrich,
            Engine::LiveMap => ProxyKind::LiveMap,
            Engine::SharedXp => ProxyKind::SharedXp,
            Engine::ReShade => ProxyKind::ReShade,
            Engine::ChainLoader => ProxyKind::ChainLoader,
        }
    }

    /// The engine of a proxy classification (None for none, unknown, not a DLL).
    pub fn of_proxy(kind: ProxyKind) -> Option<Engine> {
        std::iter::once(Engine::Kiln).chain(ENGINES.iter().map(|i| i.engine)).find(|e| e.proxy_kind() == kind)
    }
}

// ---- markers ----

/// Positions of `needles` in `hay`, case ignored (ASCII). One pass: needles are grouped by their first byte.
fn find_any(hay_lower: &[u8], needles: &[Vec<u8>]) -> Vec<bool> {
    let mut found = vec![false; needles.len()];
    let mut by_first: [Vec<usize>; 256] = std::array::from_fn(|_| Vec::new());
    for (i, n) in needles.iter().enumerate() {
        if let Some(&f) = n.first() {
            by_first[f as usize].push(i);
        }
    }
    let mut left = needles.iter().filter(|n| !n.is_empty()).count();
    for at in 0..hay_lower.len() {
        let candidates = &by_first[hay_lower[at] as usize];
        if candidates.is_empty() {
            continue;
        }
        for &i in candidates {
            if !found[i] && hay_lower[at..].starts_with(&needles[i]) {
                found[i] = true;
                left -= 1;
            }
        }
        if left == 0 {
            break;
        }
    }
    found
}

fn utf16(s: &str) -> Vec<u8> {
    s.encode_utf16().flat_map(|c| c.to_le_bytes()).collect()
}

/// The engine whose markers a DLL has (its export names, or a text inside it), in [`ENGINES`] order, with the marker
/// that matched. Kiln's own markers are `pe`'s business and are not looked at here.
pub fn classify_markers(bytes: &[u8], exports: &[String]) -> Option<(Engine, String)> {
    let lower: Vec<u8> = bytes.iter().map(|b| b.to_ascii_lowercase()).collect();
    let mut needles: Vec<Vec<u8>> = Vec::new();
    let mut owners: Vec<(usize, String)> = Vec::new();
    for (row, e) in ENGINES.iter().enumerate() {
        for t in e.texts {
            needles.push(t.as_bytes().to_vec());
            owners.push((row, format!("text {t}")));
            needles.push(utf16(t));
            owners.push((row, format!("UTF-16 text {t}")));
        }
    }
    let found = find_any(&lower, &needles);
    for (row, e) in ENGINES.iter().enumerate() {
        if let Some(x) = e.exports.iter().find(|x| exports.iter().any(|have| have == *x)) {
            return Some((e.engine, format!("export {x}")));
        }
        if let Some(i) = owners.iter().enumerate().position(|(i, (r, _))| *r == row && found[i]) {
            return Some((e.engine, owners[i].1.clone()));
        }
    }
    None
}

/// One DLL under a proxy name next to the game.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProxySlot {
    /// The file name as it is on disk.
    pub name: String,
    pub class: ProxyClass,
}

/// Every file next to the game whose name is a proxy name (case ignored), classified, in [`PROXY_NAMES`] order.
pub fn scan_proxies(game_dir: &Path) -> Vec<ProxySlot> {
    let mut found: Vec<(usize, String)> = std::fs::read_dir(game_dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_file()))
        .filter_map(|e| {
            let n = e.file_name().to_string_lossy().into_owned();
            PROXY_NAMES.iter().position(|p| p.eq_ignore_ascii_case(&n)).map(|i| (i, n))
        })
        .collect();
    found.sort();
    found
        .into_iter()
        .filter_map(|(_, name)| {
            let class = crate::pe::classify_proxy(&game_dir.join(&name)).ok()?;
            Some(ProxySlot { name, class })
        })
        .collect()
}

// ---- what a player reads ----

/// "<what it is>: <why it cannot load yet>" for an engine's mod (no final period: the launcher adds one).
pub fn mod_reason(engine: Engine, what: &str) -> String {
    let n = engine.name();
    let a = if n.starts_with(['A', 'E', 'I', 'O', 'U']) { "an" } else { "a" };
    match loader_for(engine_kind(engine)) {
        // Ember has published no mod ABI: the Library holds its mods, kiln-ember finds and reports them, none runs yet
        Some(loader) if engine == Engine::Ember => format!(
            "{what}: {a} {n} mod (Library importer: {}). It doesn't run yet: Ember has not published its mod ABI, so kiln-ember only finds and reports it",
            loader.name
        ),
        Some(loader) => format!("{what}: {a} {n} mod (Library importer: {})", loader.name),
        None => format!("{what}: {a} {n} mod. Kiln can't import {n} mods into the Library yet; no importer is registered"),
    }
}

/// Why a loader or tool DLL is not imported as a mod.
pub fn loader_reason(engine: Engine, file: &str) -> String {
    let n = engine.name();
    match engine {
        Engine::Kiln => "Kiln's own dinput8 proxy: install a runtime bundle instead".into(),
        Engine::Emf => format!("{file} is EMF's own loader. Kiln runs EMF mods itself: add the EMF mods, not the loader"),
        Engine::ShroudForge => format!("{file} is ShroudForge's loader, not a mod to import. Install Kiln with 'switch from ShroudForge' to back up its dinput8.dll; its other proxy files stay in place"),
        Engine::LiveMap | Engine::SharedXp => format!(
            "{file} is {n}, {}. It runs on its own next to the server and Kiln: copy it into the server folder by hand",
            info(engine).what
        ),
        Engine::ReShade => format!("{file} is {n}, {}. It runs on its own next to Kiln; Kiln doesn't install it", info(engine).what),
        Engine::ChainLoader | Engine::ModLoaderProxy => format!(
            "{file} is {n}, {}. Kiln chains such loaders when it is installed; it is not a mod to import",
            info(engine).what
        ),
        _ => format!("{file} is {n}'s loader ({}), not a mod to import. Kiln chains it when installed over dinput8.dll; add its mods separately", info(engine).what),
    }
}

// ---- Library detection ----

/// Mod-DLL exports of other engines (a mod, not a loader).
const GREENEYE_MOD_EXPORT: &str = "GreeneyeModStart";
const ENSHWICK_MOD_EXPORTS: [&str; 3] = ["enshwick_mod_abi", "enshwick_mod_query", "enshwick_mod_init"];
const SHROUDTOPIA_MOD_EXPORT: &str = "CreateModInstance";
const FLAME_MOD_EXPORT: &str = "FlameStartRuntime";
/// shroudlink's mod DLLs (its server looks for `shroudlink_mod_v2`, then `_v1`).
const SHROUDLINK_MOD_EXPORTS: [&str; 2] = ["shroudlink_mod_v2", "shroudlink_mod_v1"];

fn detected(kind: DetectKind, engine: Option<Engine>, path: &Path, reason: String) -> Detected {
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned());
    Detected {
        kind,
        path: path.display().to_string(),
        id: name.as_deref().map(crate::library::id_from_name),
        version: None,
        name,
        reason,
        items: vec![],
        engine,
    }
}

/// A DLL made for another engine, or another engine's loader (None: Kiln's own rules decide). `exports` are the DLL's,
/// `bytes` its content. Kiln plugins and EMF mod DLLs are recognised by the caller first.
pub fn detect_dll(path: &Path, bytes: &[u8], exports: &[String]) -> Option<Detected> {
    if !crate::pe::is_pe64_dll(bytes) { return None; }
    let has = |e: &str| exports.iter().any(|x| x == e);
    let file = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let mod_dll = |engine: Engine| Some(detected(engine_kind(engine), Some(engine), path, mod_reason(engine, &file)));
    if has(GREENEYE_MOD_EXPORT) {
        return mod_dll(Engine::Greeneye);
    }
    if ENSHWICK_MOD_EXPORTS.iter().any(|e| has(e)) {
        return mod_dll(Engine::Enshwick);
    }
    if has(FLAME_MOD_EXPORT) {
        return mod_dll(Engine::Flame);
    }
    if has(SHROUDTOPIA_MOD_EXPORT) {
        return mod_dll(Engine::Shroudtopia);
    }
    if SHROUDLINK_MOD_EXPORTS.iter().any(|e| has(e)) {
        return mod_dll(Engine::Shroudlink);
    }
    let class = crate::pe::classify_bytes(bytes);
    let engine = Engine::of_proxy(class.kind).filter(|e| *e != Engine::Kiln)?; // Kiln's own proxy: library's rule
    // a plain DLL that merely mentions "dinput8-chain.ini" or "reshade" keeps Kiln's own rules unless it is one by name
    if matches!(engine, Engine::ChainLoader | Engine::ReShade | Engine::Aldrich) && !is_proxy_name(&file) {
        return None;
    }
    Some(detected(DetectKind::Proxy, Some(engine), path, loader_reason(engine, &file)))
}

/// The Library kind of an engine's mods.
pub fn engine_kind(engine: Engine) -> DetectKind {
    match engine {
        Engine::Kiln => DetectKind::Kiln,
        Engine::Emf => DetectKind::Emf,
        Engine::ShroudForge => DetectKind::ShroudForge,
        Engine::Eml => DetectKind::Eml,
        Engine::Set => DetectKind::Set,
        Engine::Enshwick => DetectKind::Enshwick,
        Engine::Greeneye => DetectKind::Greeneye,
        Engine::Shroudtopia => DetectKind::Shroudtopia,
        Engine::Flame => DetectKind::Flame,
        Engine::Shroudlink => DetectKind::Shroudlink,
        Engine::Ember => DetectKind::Ember,
        Engine::UltimateAsiLoader => DetectKind::Asi,
        Engine::ReShade => DetectKind::Reshade,
        Engine::ModLoaderProxy | Engine::Aldrich | Engine::LiveMap | Engine::SharedXp | Engine::ChainLoader => DetectKind::Proxy,
    }
}

fn read_json(path: &Path) -> Option<serde_json::Value> {
    crate::jsonc::parse(&crate::manifest::read_text(path).ok()?).ok()
}

/// The top-level `key = "string"` pairs of a TOML file (Enshwick's `pack.toml`: `id`, `version`, `name`, `side`), as a
/// JSON object. Tables, arrays, multi-line strings and non-string values are skipped; the default build has no TOML
/// parser, and these few keys are all the launcher reads.
pub fn toml_strings(text: &str) -> serde_json::Map<String, serde_json::Value> {
    fn string(v: &str) -> Option<(String, &str)> {
        let mut out = String::new();
        if let Some(rest) = v.strip_prefix('\'') {
            let end = rest.find('\'')?;
            return Some((rest[..end].to_string(), &rest[end + 1..]));
        }
        let mut chars = v.strip_prefix('"')?.char_indices();
        while let Some((i, c)) = chars.next() {
            match c {
                '"' => return Some((out, &v[i + 2..])),
                '\\' => match chars.next()?.1 {
                    'n' => out.push('\n'),
                    't' => out.push('\t'),
                    'r' => out.push('\r'),
                    '"' => out.push('"'),
                    '\\' => out.push('\\'),
                    'u' => {
                        let hex: String = (0..4).filter_map(|_| chars.next().map(|x| x.1)).collect();
                        out.push(char::from_u32(u32::from_str_radix(&hex, 16).ok()?)?);
                    }
                    _ => return None,
                },
                c => out.push(c),
            }
        }
        None
    }
    let mut map = serde_json::Map::new();
    let mut skip_until: Option<&str> = None;
    let mut depth = 0i32;
    for line in text.trim_start_matches('\u{feff}').lines() {
        let line = line.trim();
        if let Some(end) = skip_until {
            if line.contains(end) { skip_until = None; }
            continue;
        }
        if depth > 0 {
            depth += line.matches('[').count() as i32 - line.matches(']').count() as i32;
            continue;
        }
        if line.starts_with('[') { break; } // the first table ends the top level
        let Some((key, value)) = line.split_once('=') else { continue };
        let key = key.trim().trim_matches('"');
        let value = value.trim();
        if value.starts_with("\"\"\"") || value.starts_with("'''") {
            let delim = &value[..3];
            if !value[3..].contains(delim) { skip_until = Some(if delim == "'''" { "'''" } else { "\"\"\"" }); }
            continue;
        }
        if value.starts_with('[') {
            depth = value.matches('[').count() as i32 - value.matches(']').count() as i32;
            continue;
        }
        if key.is_empty() || !key.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-') { continue; }
        if let Some((v, rest)) = string(value) {
            let rest = rest.trim();
            if rest.is_empty() || rest.starts_with('#') {
                map.entry(key.to_string()).or_insert(serde_json::Value::String(v));
            }
        }
    }
    map
}

/// `pack.toml`'s top-level strings (Enshwick data packs and Luau mods), as JSON.
pub fn pack_toml(dir: &Path) -> Option<serde_json::Value> {
    let text = crate::manifest::read_text(&dir.join("pack.toml")).ok()?;
    Some(serde_json::Value::Object(toml_strings(&text)))
}

/// A folder in Ember's mods folder, `.../ember/mods/<mod>/` (Ember's mods carry no manifest or known export yet, so the
/// place is what identifies them).
fn in_ember_mods(dir: &Path) -> bool {
    let name = |p: Option<&Path>| p.and_then(|p| p.file_name()).map(|n| n.to_string_lossy().to_ascii_lowercase());
    let parent = dir.parent();
    name(parent).as_deref() == Some("mods") && name(parent.and_then(Path::parent)).as_deref() == Some("ember")
}

/// The one library of an Ember mod folder (Ember: "each mod is one directory holding one library"); None when the
/// folder holds none, several, or another engine's mod DLL.
pub fn ember_library(dir: &Path) -> Option<PathBuf> {
    let dlls = dlls_in(dir);
    let [dll] = dlls.as_slice() else { return None };
    // another engine's mod DLL is that engine's
    let bytes = std::fs::read(dll).ok()?;
    let exports = crate::pe::parse(&bytes).map(|i| i.exports).unwrap_or_default();
    if !crate::pe::is_pe64_dll(&bytes) || detect_dll(dll, &bytes, &exports).is_some() { return None; }
    Some(dll.clone())
}

/// A folder of mods holding EML data mods (`<sub>/mod.json` + `<sub>/src/mod.lua`) next to shroudlink DLLs: shroudlink
/// hands those to kiln-eml, so the Library takes them as EML mods and each DLL on its own.
fn has_eml_subfolder(dir: &Path) -> bool {
    std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path())
        .any(|p| p.is_dir() && p.join("mod.json").is_file() && p.join("src").join("mod.lua").is_file())
}

fn folder_detected(kind: DetectKind, engine: Engine, dir: &Path, manifest: Option<&serde_json::Value>, what: &str) -> Detected {
    let s = |k: &str| manifest.and_then(|m| m.get(k)).and_then(|v| v.as_str()).map(str::to_string);
    let mut d = detected(kind, Some(engine), dir, String::new());
    if let Some(id) = s("id") {
        d.id = Some(id);
    }
    d.version = s("version");
    if let Some(name) = s("name").or_else(|| s("title")) {
        d.name = Some(name);
    }
    let shown = d.name.clone().unwrap_or_else(|| what.to_string());
    d.reason = mod_reason(engine, &shown);
    d
}

/// DLL files directly in `dir`.
fn dlls_in(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_file() && p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")))
        .collect();
    v.sort();
    v
}

/// A mod folder made for another engine (None: not one; Kiln's own manifests are checked by the caller first). Order
/// after EMF's archive rules: Flame, ShroudForge package, EML mod, Enshwick pack, SET mod, Shroudtopia, then a folder
/// whose own DLL is another engine's mod (Greeneye, Enshwick, Flame, Shroudtopia).
pub fn detect_folder(dir: &Path) -> Option<Detected> {
    let has = |f: &str| dir.join(f).is_file();
    if has("flame.json") {
        let m = read_json(&dir.join("mod.json"));
        return Some(folder_detected(DetectKind::Flame, Engine::Flame, dir, m.as_ref(), "this Flame mod"));
    }
    // Both engines use capabilities (including runtime). Only SF-specific metadata distinguishes them.
    if has("mod.json") && crate::shroudforge::is_sf_mod(dir) {
        let m = read_json(&dir.join("mod.json"));
        let eml = !crate::shroudforge::is_sf_own_mod(dir) && dir.join("src").join("mod.lua").is_file();
        let (kind, engine, what) = if eml {
            (DetectKind::Eml, Engine::Eml, "this EML mod")
        } else {
            (DetectKind::ShroudForge, Engine::ShroudForge, "this ShroudForge package")
        };
        return Some(folder_detected(kind, engine, dir, m.as_ref(), what));
    }
    if has("pack.toml") {
        // a data pack, or a Luau mod (pack.toml + main.luau): its id, version and name are pack.toml's
        let m = pack_toml(dir);
        let what = if has("main.luau") { "this Enshwick Luau mod" } else { "this Enshwick pack" };
        return Some(folder_detected(DetectKind::Enshwick, Engine::Enshwick, dir, m.as_ref(), what));
    }
    if has("init.lua") && !has("native.json") {
        let m = read_json(&dir.join("mod.json"));
        return Some(folder_detected(DetectKind::Set, Engine::Set, dir, m.as_ref(), "this Shrouded Engine Tweaks mod"));
    }
    if has("shroudtopia.json") {
        return Some(folder_detected(DetectKind::Shroudtopia, Engine::Shroudtopia, dir, None, "this Shroudtopia setup"));
    }
    // a folder whose DLL is another engine's mod: the folder is the mod (its other DLLs are the mod's own)
    for dll in dlls_in(dir) {
        let Ok(bytes) = std::fs::read(&dll) else { continue };
        let exports = crate::pe::parse(&bytes).map(|i| i.exports).unwrap_or_default();
        if let Some(d) = detect_dll(&dll, &bytes, &exports)
            && d.kind != DetectKind::Proxy
            && let Some(engine) = d.engine
        {
            if engine == Engine::Shroudlink && has_eml_subfolder(dir) {
                return None; // a folder of mods: each DLL and each EML folder on its own
            }
            let m = read_json(&dir.join("mod.json"));
            let mut found = folder_detected(d.kind, engine, dir, m.as_ref(), &format!("this {} mod", engine.name()));
            // a release zip's shroudlink_mods/ folder holding one mod: the mod is named after its DLL
            if engine == Engine::Shroudlink && m.is_none()
                && dir.file_name().is_some_and(|n| n.eq_ignore_ascii_case(crate::engine_mods::SHROUDLINK_MODS))
            {
                found.id = d.id;
                found.name = d.name;
            }
            return Some(found);
        }
    }
    // (no DLL here is another engine's mod: the loop above returned for those). Ember's importer refuses a folder with
    // more than one library, as kiln-ember does, rather than taking its DLLs as plain mods.
    if in_ember_mods(dir) && !dlls_in(dir).is_empty() {
        return Some(folder_detected(DetectKind::Ember, Engine::Ember, dir, None, "this Ember mod"));
    }
    None
}

/// Extensions of files that are no mods for Kiln but that players drop: saves, Cheat Engine tables, ReShade presets,
/// patched game data, .asi plugins.
pub fn detect_file(path: &Path) -> Option<Detected> {
    let file = path.file_name()?.to_string_lossy().into_owned();
    let low = file.to_lowercase();
    let ext = path.extension().map(|x| x.to_string_lossy().to_lowercase()).unwrap_or_default();
    let d = |kind, engine, reason: String| Some(detected(kind, engine, path, reason));
    if ext == "ksd" || is_save_name(&low) {
        return d(DetectKind::Save, None, format!("{file} is a save file, not a mod. Kiln doesn't import saves here yet; support is coming"));
    }
    if ext == "ct" || ext == "cetrainer" {
        return d(DetectKind::CheatTable, None, format!("{file} is a Cheat Engine table, not a mod; Kiln doesn't load these"));
    }
    if ext == "ini" && low.contains("reshade") {
        return d(DetectKind::Reshade, Some(Engine::ReShade), format!("{file} is a ReShade preset. ReShade runs on its own next to Kiln; Kiln doesn't install it"));
    }
    if ext == "kfc" {
        return d(DetectKind::Data, None, format!(
            "{file} is a patched game data file: it replaces one of the game's files. Kiln can't apply patched game files yet; support is coming"
        ));
    }
    if ext == "asi" {
        return d(DetectKind::Asi, Some(Engine::UltimateAsiLoader), format!(
            "{file} is an .asi plugin for Ultimate ASI Loader. Kiln can't load .asi plugins yet; support is coming"
        ));
    }
    None
}

/// "characters", "characters-3", "3ad85aea", "3ad85aea_info", "3ad85aea-1" (EMF's save-name rule): a save file.
fn is_save_name(low: &str) -> bool {
    let (stem, rest) = if let Some(r) = low.strip_prefix("characters") {
        ("c", r)
    } else if low.len() >= 8 && low.is_char_boundary(8) && low[..8].bytes().all(|b| b.is_ascii_hexdigit()) {
        ("h", &low[8..])
    } else {
        return false;
    };
    let rest = rest.strip_prefix("_info").unwrap_or(rest);
    if rest.is_empty() {
        // 8 hex digits alone are not enough (a hash-named file); "characters" alone is
        return stem == "c" || low.ends_with("_info");
    }
    rest.strip_prefix('-').is_some_and(|n| !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit()))
}

// ---- loaders: how support for an engine's mods plugs in ----

/// What a loader gets to import one detected item.
pub struct ImportCtx<'a> {
    pub dirs: &'a DataDirs,
    pub detected: &'a Detected,
    /// The real path (a zip's entry is unpacked already).
    pub src: &'a Path,
    pub from: &'a ImportSource,
    pub report: &'a mut ImportReport,
}

/// Support for one Library kind of another engine.
#[derive(Clone, Copy)]
pub struct Loader {
    /// Who provides it ("kiln-shroudforge"), for logs.
    pub name: &'static str,
    /// Imports one detected item into the library (`library::commit_folder` does the content-addressed part), or adds
    /// a `skipped` entry with its reason.
    pub import: fn(&mut ImportCtx) -> Result<()>,
}

fn registry() -> &'static RwLock<HashMap<DetectKind, Loader>> {
    static R: OnceLock<RwLock<HashMap<DetectKind, Loader>>> = OnceLock::new();
    R.get_or_init(|| {
        let mut m = HashMap::new();
        builtin_loaders(&mut m);
        RwLock::new(m)
    })
}

/// Loaders built into kiln-manage. Support for an engine's mods adds one line here:
/// `m.insert(DetectKind::Greeneye, crate::greeneye::LOADER);`
/// Every engine mod kind (EML ... Ember): `engine_mods::import_engine_mod` (content-addressed copy).
fn builtin_loaders(m: &mut HashMap<DetectKind, Loader>) {
    crate::engine_mods::builtin_loaders(m);
}

/// Registers (or replaces) the loader of a Library kind at run time. Returns the previous one.
pub fn register_loader(kind: DetectKind, loader: Loader) -> Option<Loader> {
    registry().write().ok()?.insert(kind, loader)
}

/// The loader of a Library kind, when one is registered.
pub fn loader_for(kind: DetectKind) -> Option<Loader> {
    registry().read().ok()?.get(&kind).copied()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::pe::build_test_dll;

    fn kind_of(exports: &[&str], text: &[u8]) -> ProxyKind {
        crate::pe::classify_bytes(&build_test_dll(exports, text)).kind
    }

    #[test]
    fn every_engine_classifies_by_its_markers() {
        let d8 = "DirectInput8Create";
        assert_eq!(kind_of(&[d8, "emf_loader_identity"], b""), ProxyKind::Emf);
        assert_eq!(kind_of(&[d8], b"..EMF-LOADER-7d3c9f.."), ProxyKind::Emf);
        assert_eq!(kind_of(&[d8], b"x shroudforge\\config"), ProxyKind::ShroudForge);
        assert_eq!(kind_of(&["MiniDumpWriteDump"], b"EML_LOG_FILE_PATH"), ProxyKind::Eml);
        assert_eq!(kind_of(&["PowerGetActiveScheme"], b"shrouded_engine_tweaks\\plugins"), ProxyKind::Set);
        assert_eq!(kind_of(&["PowerGetActiveScheme"], b"mods\\enshwick\\enshwick.dll"), ProxyKind::Enshwick);
        assert_eq!(kind_of(&["MiniDumpWriteDump"], b"Greeneye Loader 1.2.2"), ProxyKind::Greeneye);
        assert_eq!(kind_of(&["timeGetTime"], b"shroudtopia.dll"), ProxyKind::Shroudtopia);
        assert_eq!(kind_of(&[d8], b"FlameStartRuntime\0flame.json"), ProxyKind::Flame);
        assert_eq!(kind_of(&["SteamAPI_Init"], b"shroudlink server"), ProxyKind::Shroudlink);
        assert_eq!(kind_of(&["SteamAPI_Init"], b"LoadLibraryW steam_api64_o.dll"), ProxyKind::Shroudlink);
        assert_eq!(kind_of(&["PowerGetActiveScheme"], b"crates\\ember-loader\\src\\lib.rs"), ProxyKind::Ember);
        assert_eq!(kind_of(&[d8], &utf16("ChainLoader.ini")), ProxyKind::ModLoaderProxy);
        assert_eq!(kind_of(&[d8, "IsUltimateASILoader"], b""), ProxyKind::UltimateAsiLoader);
        assert_eq!(kind_of(&[d8], b"aldrich-modmenu"), ProxyKind::Aldrich);
        assert_eq!(kind_of(&["GetAdaptersInfo"], b"disabled in livemap.ini"), ProxyKind::LiveMap);
        assert_eq!(kind_of(&["PowerGetActiveScheme"], b"disabled in xpshare.ini"), ProxyKind::SharedXp);
        assert_eq!(kind_of(&["CreateDXGIFactory"], b"ReShade 6.3"), ProxyKind::ReShade);
        assert_eq!(kind_of(&[d8], b"[Chain] dinput8-chain.ini"), ProxyKind::ChainLoader);
        assert_eq!(kind_of(&[d8], b"nothing known"), ProxyKind::Unknown);
    }

    #[test]
    fn priority_follows_the_table() {
        // ShroudForge's DLLs carry EML's text too: ShroudForge wins
        assert_eq!(kind_of(&[], b"EML_LOG_FILE_PATH shroudforge"), ProxyKind::ShroudForge);
        // a loader that reads dinput8-chain.ini is still itself
        assert_eq!(kind_of(&[], b"dinput8-chain.ini greeneye"), ProxyKind::Greeneye);
        // UTF-16 texts count, case ignored
        assert_eq!(kind_of(&[], &utf16("SHROUDTOPIA")), ProxyKind::Shroudtopia);
    }

    #[test]
    fn every_engine_maps_to_its_own_proxy_kind() {
        let mut seen = std::collections::HashSet::new();
        for e in ENGINES {
            assert!(seen.insert(e.engine.proxy_kind()), "{:?} shares a ProxyKind", e.engine);
            assert_eq!(Engine::of_proxy(e.engine.proxy_kind()), Some(e.engine));
            assert!(e.texts.iter().all(|t| t.to_lowercase() == *t), "{:?}: texts are lower case", e.engine);
            assert!(e.proxies.iter().all(|p| is_proxy_name(p)), "{:?}: every proxy is in PROXY_NAMES", e.engine);
        }
        assert_eq!(Engine::of_proxy(ProxyKind::Unknown), None);
        assert_eq!(Engine::of_proxy(ProxyKind::Kiln), Some(Engine::Kiln));
    }

    #[test]
    fn save_names() {
        for yes in ["characters", "characters-3", "3ad85aea_info", "3ad85aea-1", "3ad85aea_info-12"] {
            assert!(is_save_name(yes), "{yes}");
        }
        for no in ["3ad85aea", "character", "3ad85aeg_info", "3ad85aea-", "readme"] {
            assert!(!is_save_name(no), "{no}");
        }
    }

    #[test]
    fn loader_registry() {
        fn noop(_: &mut ImportCtx) -> Result<()> {
            Ok(())
        }
        // every engine mod kind has a built-in loader (engine_mods); files that are no mods have none
        for (kind, name) in [(DetectKind::Shroudtopia, "kiln-shroudtopia"), (DetectKind::Enshwick, "kiln-enshwick"),
                             (DetectKind::Shroudlink, "kiln-shroudlink"), (DetectKind::Ember, "kiln-ember")] {
            assert_eq!(loader_for(kind).map(|l| l.name), Some(name));
        }
        assert!(loader_for(DetectKind::Asi).is_none());
        assert!(register_loader(DetectKind::Asi, Loader { name: "test", import: noop }).is_none());
        assert_eq!(loader_for(DetectKind::Asi).map(|l| l.name), Some("test"));
        registry().write().unwrap().remove(&DetectKind::Asi);
    }

    #[test]
    fn toml_strings_reads_the_top_level_only() {
        let m = toml_strings("\u{feff}# a pack\nid = \"you.lanterns\" # the id\nversion = '1.0.0'\nname = \"L\\u00e4nterns \\\"x\\\"\"\n\
            description = \"\"\"\nid = \"not.this\"\n\"\"\"\nauthors = [\n  \"side = x\",\n]\nn = 3\nside = \"server\"\n\
            [requires]\nversion = \"9.9.9\"\n");
        assert_eq!(m.get("id").and_then(|v| v.as_str()), Some("you.lanterns"));
        assert_eq!(m.get("version").and_then(|v| v.as_str()), Some("1.0.0"));
        assert_eq!(m.get("name").and_then(|v| v.as_str()), Some("L\u{e4}nterns \"x\""));
        assert_eq!(m.get("side").and_then(|v| v.as_str()), Some("server"));
        assert!(m.get("n").is_none() && m.get("description").is_none() && m.get("authors").is_none());
        assert!(toml_strings("{}").is_empty() && toml_strings("id = \"open").is_empty());
    }
}
