//! The mod library: `<data>/library/<kind>/<id>/<version>+<hash8>/`, immutable and content-addressed (hash8 from
//! the folder's kdh1; a plugin DLL's from its sha256). Import detects what a file or folder is: a Kiln mod
//! (kiln.mod.json), a native mod (native.json), an EMF mod (mod.json of EMF), a Kiln plugin DLL (exports
//! kiln_plugin_main), a modpack (kiln-modpack.json), a runtime bundle (kiln-runtime.json), a bare DLL (wrapped into a
//! native mod with a generated native.json), or nothing usable. Zips are unpacked first.

use crate::manifest::{Clients, ModKind, Side};
use crate::paths::DataDirs;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryRef {
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    /// kdh1 (sha256 for a plugin DLL).
    pub hash: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportSource {
    /// "file" | "folder" | "modpack"
    #[serde(rename = "type")]
    pub kind: String,
    pub from: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryVersion {
    pub version: String,
    pub hash: String,
    pub folder: PathBuf,
    pub imported: String,
    pub side: Option<Side>,
    pub clients: Option<Clients>,
    pub size: u64,
    pub source: Option<ImportSource>,
    /// The id the mod's own manifest gives, when the library id is not that (an engine mod whose id is not a Kiln
    /// id: `Hoarders Helper` is `hoarders-helper` here). Engine plugins report the manifest's id in their state files.
    pub manifest_id: Option<String>,
    /// The manifest as JSON (kiln.mod.json / native.json / EMF mod.json).
    pub manifest: Option<serde_json::Value>,
    /// Its files were changed in its folder since Kiln imported them (a player edited its manifest): Play refuses it as
    /// it is until [`take_over`] makes the changed files a Library entry of their own. Looked at cheaply
    /// ([`changed_on_disk`]).
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub changed: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LibraryEntry {
    pub kind: ModKind,
    pub id: String,
    pub name: String,
    pub versions: Vec<LibraryVersion>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum DetectKind {
    Kiln,
    Native,
    Plugin,
    Emf,
    Modpack,
    Runtime,
    BareDll,
    Unknown,
    // Mods made for other engines, and other things players drop (engines.rs detects them). Each is imported only
    // through a loader registered for it (engines::register_loader); without one it is skipped with its reason.
    /// An EML mod (`mod.json` + `src/mod.lua`).
    Eml,
    /// A native-plugin.ini companion for the separately installed XHL-Core, never executed by Kiln.
    Xhl,
    /// A ShroudForge package (SF-specific extension or schema; capabilities are shared with EML).
    #[serde(rename = "shroudforge")]
    ShroudForge,
    /// A Shrouded Engine Tweaks mod (`init.lua`).
    Set,
    /// An Enshwick mod (a DLL with `enshwick_mod_*` exports, or a `pack.toml` data/Luau pack).
    Enshwick,
    /// A Greeneye Loader mod (`mod.json` + a DLL exporting `GreeneyeModStart`).
    Greeneye,
    /// A Shroudtopia/Shroudweave mod (a DLL exporting `CreateModInstance`, or `shroudtopia.json`).
    Shroudtopia,
    /// A Flame mod (`flame.json`, a DLL exporting `FlameStartRuntime`): Creative Mode.
    Flame,
    /// A shroudlink mod.
    Shroudlink,
    /// An Ember server mod.
    Ember,
    /// Another engine's loader or a tool under a proxy name (`engine` says which): never a mod to import.
    Proxy,
    /// Patched game data files (`.kfc`).
    Data,
    /// A save file (`*.ksd`, `characters`, a world save).
    Save,
    /// A ReShade preset.
    Reshade,
    /// A Cheat Engine table (`.ct`, `.cetrainer`).
    CheatTable,
    /// An `.asi` plugin (Ultimate ASI Loader).
    Asi,
}

impl DetectKind {
    /// A kind made for another engine or tool (detected by engines.rs).
    pub fn is_engine(self) -> bool {
        !matches!(self, DetectKind::Kiln | DetectKind::Native | DetectKind::Plugin | DetectKind::Emf | DetectKind::Modpack
            | DetectKind::Runtime | DetectKind::BareDll | DetectKind::Unknown)
    }

    /// Can the library import it now: Kiln's own kinds, and engine kinds with a registered loader.
    pub fn importable(self) -> bool {
        match self {
            DetectKind::Kiln | DetectKind::Native | DetectKind::Plugin | DetectKind::Emf | DetectKind::Modpack | DetectKind::BareDll => true,
            DetectKind::Runtime | DetectKind::Unknown => false,
            k => crate::engines::loader_for(k).is_some(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Detected {
    pub kind: DetectKind,
    pub path: String,
    pub id: Option<String>,
    pub version: Option<String>,
    pub name: Option<String>,
    /// Why it was classified so (or why it is unknown).
    pub reason: String,
    /// A zip or folder holding several mods.
    #[serde(default)]
    pub items: Vec<Detected>,
    /// The engine it was made for (other engines' kinds, and loaders under proxy names).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub engine: Option<crate::engines::Engine>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Skipped {
    pub path: String,
    pub reason: String,
}

/// Something left out on purpose that needs nothing from the player: a loader the package carries, which Kiln does
/// not need ("EML's own loader is in this package: not needed, Kiln runs EML mods").
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportNote {
    pub path: String,
    pub text: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportReport {
    pub imported: Vec<LibraryRef>,
    /// Already in the library (same kind, id, version and hash).
    pub existing: Vec<LibraryRef>,
    /// Left out, with the reason: what happened and, when the player has to do something, what.
    pub skipped: Vec<Skipped>,
    /// Left out because it is not needed; each text once per import.
    #[serde(default)]
    pub notes: Vec<ImportNote>,
    /// The display name of every mod in `imported` and `existing`, by `<kind>/<id>`.
    #[serde(default)]
    pub names: std::collections::BTreeMap<String, String>,
}

impl ImportReport {
    /// An information line, once per distinct text.
    pub(crate) fn note(&mut self, path: &str, text: String) {
        if !self.notes.iter().any(|n| n.text == text) {
            self.notes.push(ImportNote { path: path.to_string(), text });
        }
    }
}

/// `<version>+<hash8>.json` next to a library folder (contracts.md 3).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportRecord {
    pub format: u32,
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    pub hash: String,
    pub imported: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub source: Option<ImportSource>,
    pub size: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub side: Option<Side>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub clients: Option<Clients>,
    /// Plugins: the DLL's file name inside the folder.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub file: Option<String>,
    /// Engine mods: the id the mod's own manifest gives, when `id` (a Kiln id made from it) is not that. The files
    /// stay as the author made them: the engine plugin reads the manifest's id and reports its rows under it.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub manifest_id: Option<String>,
    /// EML mods a native DLL reads in the game's `mods` folder (a DLL of its package next to that folder, as Auto Loot
    /// Plus's dinput8.dll, or a DLL in the mod's own folder, as the XHL-Core plugins): the folder name under
    /// `<game>/mods/`. Activation places the mod there and kiln-eml runs that copy (engine_mods::place).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub game_folder: Option<String>,
}

const EMF_MANIFEST: &str = "mod.json";
const MODPACK_MANIFEST: &str = "kiln-modpack.json";

fn io(path: &Path, what: &str) -> impl FnOnce(std::io::Error) -> Error {
    crate::util::io_at(path, what)
}

/// What a bare DLL is, by its exports (`library::detect`).
fn detect_dll(path: &Path) -> Detected {
    let p = path.display().to_string();
    let stem_id = id_from_name(path.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default().as_str());
    let bytes = match std::fs::read(path) {
        Ok(b) => b,
        Err(e) => return Detected { kind: DetectKind::Unknown, path: p, id: None, version: None, name: None, reason: e.to_string(), items: vec![], engine: None },
    };
    if !crate::pe::is_pe64_dll(&bytes) {
        return Detected { kind: DetectKind::Unknown, path: p, id: None, version: None, name: None, reason: "not a 64-bit Windows DLL".into(), items: vec![], engine: None };
    }
    let exports = crate::pe::parse(&bytes).map(|i| i.exports).unwrap_or_default();
    let has = |e: &str| exports.iter().any(|x| x == e);
    let d = |kind, reason: &str| Detected {
        kind,
        path: p.clone(),
        id: Some(stem_id.clone()),
        version: Some("0.0.0".into()),
        name: path.file_name().map(|n| n.to_string_lossy().into_owned()),
        reason: reason.into(),
        items: vec![],
        engine: None,
    };
    // A DLL under a proxy name says nothing about what mod it is (`winmm.dll`, `dinput8.dll`): two of them must not
    // become one library entry, so its id carries a few characters of its content.
    // XHL-Core is one mod whatever copy of it this is: its builds replace each other (package_dll_identity).
    let plain = |mut x: Detected| {
        if is_xhl_core(&bytes) {
            x.id = Some(XHL_CORE_ID.into());
        } else if crate::engines::is_proxy_name(&file_name(path)) {
            x.id = Some(format!("{stem_id}-{}", &crate::hash::sha256_hex(&bytes)[..6]));
        }
        x
    };
    if crate::pe::classify_bytes(&bytes).kind == crate::pe::ProxyKind::Kiln {
        return d(DetectKind::Unknown, &crate::engines::loader_reason(crate::engines::Engine::Kiln, &file_name(path)));
    }
    if has(crate::pe::KILN_PLUGIN_EXPORT) {
        return d(DetectKind::Plugin, "a Kiln plugin (exports kiln_plugin_main)");
    }
    if has(crate::pe::EMF_MOD_EXPORT) {
        return d(DetectKind::Emf, "an EMF mod DLL (exports emf_mod_load); a mod.json is generated");
    }
    // another engine's mod DLL or loader: never imported as a plain DLL. A DLL that reads dinput8-chain.ini is not only
    // a chain loader: Auto Loot Plus's dinput8.dll is the mod itself (its looting, its F8 menu), XHL-Core's is the host
    // of the XHL mods. Kiln chain-loads it like any dinput8 mod; the DLLs it chains are the same files Kiln reads from
    // dinput8-chain.ini, so nothing loads twice.
    let mut chains = false;
    if let Some(e) = crate::engines::detect_dll(path, &bytes, &exports) {
        if !(e.kind == DetectKind::Proxy && e.engine == Some(crate::engines::Engine::ChainLoader)) {
            return Detected { id: Some(stem_id.clone()), version: Some("0.0.0".into()), ..e };
        }
        chains = true;
    }
    if chains {
        return plain(d(DetectKind::BareDll, "a DLL mod that also loads the DLLs named in dinput8-chain.ini (Auto Loot Plus, XHL-Core): \
            a native mod, phase dllmain (chain-loaded)"));
    }
    if has(crate::pe::DINPUT8_EXPORT) {
        let c = crate::pe::classify_bytes(&bytes);
        return match c.kind {
            crate::pe::ProxyKind::Kiln => Detected {
                kind: DetectKind::Unknown,
                reason: crate::engines::loader_reason(crate::engines::Engine::Kiln, &file_name(path)),
                ..d(DetectKind::Unknown, "")
            },
            crate::pe::ProxyKind::ShroudForge => Detected {
                kind: DetectKind::Unknown,
                reason: "ShroudForge's mod loader can't run next to Kiln: Kiln runs ShroudForge mods itself".into(),
                ..d(DetectKind::Unknown, "")
            },
            _ => plain(d(DetectKind::BareDll, "a DLL mod that loads as a DirectInput8 proxy: a native mod, phase dllmain (chain-loaded)")),
        };
    }
    plain(d(DetectKind::BareDll, "a plain DLL: added as a native mod, loaded early"))
}

fn file_name(path: &Path) -> String {
    path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default()
}

/// A mod id from a file or folder name: lower case, other characters as '-', at most 64.
pub fn id_from_name(name: &str) -> String {
    let mut s: String = name.to_lowercase().chars().map(|c| if c.is_ascii_alphanumeric() || "._-".contains(c) { c } else { '-' }).collect();
    while s.contains("--") {
        s = s.replace("--", "-");
    }
    let s = s.trim_matches(|c| c == '-' || c == '.' || c == '_').to_string();
    let s: String = s.chars().take(64).collect();
    if s.is_empty() { "mod".into() } else { s }
}

/// A browser's or an unpacker's copy suffix gone: `Name (1)`, `Name(2)` -> `Name`.
pub(crate) fn strip_copy_suffix(name: &str) -> &str {
    let t = name.trim_end();
    if let Some(open) = t.strip_suffix(')').and_then(|s| s.rfind('('))
        && t[open + 1..t.len() - 1].bytes().all(|b| b.is_ascii_digit()) && t.len() - open > 2 && open > 0
    {
        return t[..open].trim_end();
    }
    t
}

/// What a package is called without its version and download decorations: the words before the trailing run of
/// words that are numbers or versions (`AutoLootPlus 1.1.8 170 1.1.8 2026-10-23T21-27Z 2FzbANfp1` -> `AutoLootPlus`,
/// Nexus' `Auto-Loot-Plus-170-1-1-8-1731234567-AbC123` -> `Auto-Loot-Plus`), after a copy suffix. A number followed by a word
/// is part of the name (`Enshrouded 60 FPS`, `Enshrouded 2x Stamina 1.0` -> `Enshrouded 2x Stamina`); a name that is
/// all numbers stays as it is.
pub(crate) fn package_stem(name: &str) -> &str {
    let mut name = strip_copy_suffix(name);
    // A random download token need not begin with a digit. Recognize it only directly after a Unix upload timestamp,
    // not after an ordinary number in a title such as "Enshrouded 60 FPS".
    if let Some((before, token)) = name.rsplit_once([' ', '-', '_']) {
        let stamp = before.rsplit([' ', '-', '_']).next().unwrap_or_default();
        if stamp.len() == 10 && stamp.bytes().all(|b| b.is_ascii_digit())
            && token.len() >= 4 && token.bytes().all(|b| b.is_ascii_alphanumeric())
        {
            name = before;
        }
    }
    let numeric = |w: &str| {
        let digit = |s: &str| s.bytes().next().is_some_and(|b| b.is_ascii_digit());
        digit(w) || (w.starts_with(['v', 'V']) && digit(&w[1..]))
    };
    let mut cut = None;
    let mut start = 0;
    for (i, c) in name.char_indices().chain(std::iter::once((name.len(), ' '))) {
        if matches!(c, ' ' | '-' | '_') {
            if i > start {
                if numeric(&name[start..i]) { cut.get_or_insert(start); } else { cut = None; }
            }
            start = i + c.len_utf8();
        }
    }
    match cut.map(|i| name[..i].trim_end_matches([' ', '-', '_', '.'])) {
        Some(stem) if !stem.is_empty() => stem,
        _ => name,
    }
}

/// A mod id for a name, kept apart from every other name: `id_from_name` keeps only ASCII letters and digits, so a
/// name with other letters (`自动拾取`, `Ümlaut`) or none gets a few characters of the name's hash, and `fallback` (a
/// DLL's file stem) when nothing of it is left.
fn distinct_id(name: &str, fallback: &str) -> String {
    let lossy = name.chars().any(|c| !c.is_ascii() && c.is_alphanumeric()) || !name.chars().any(|c| c.is_ascii_alphanumeric());
    if !lossy {
        return id_from_name(name);
    }
    let base = if name.chars().any(|c| c.is_ascii_alphanumeric()) { id_from_name(name) } else { id_from_name(fallback) };
    let base: String = base.chars().take(55).collect();
    format!("{}-{}", base.trim_end_matches(['-', '.', '_']), &crate::hash::sha256_hex(name.as_bytes())[..8])
}

/// The file version in a DLL's VS_VERSION_INFO resource (`a.b.c`; the fourth number is left out), when it has one.
pub(crate) fn dll_file_version(bytes: &[u8]) -> Option<String> {
    let key: Vec<u8> = "VS_VERSION_INFO\0".encode_utf16().flat_map(|u| u.to_le_bytes()).collect();
    let at = bytes.windows(key.len()).position(|w| w == key.as_slice())? + key.len();
    let sig = (0..4).map(|pad| at + pad).find(|&i| bytes.get(i..i + 4) == Some(&0xFEEF_04BDu32.to_le_bytes()[..]))?;
    let word = |i: usize| bytes.get(i..i + 4).map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]));
    let (ms, ls) = (word(sig + 8)?, word(sig + 12)?);
    (ms != 0 || ls != 0).then(|| format!("{}.{}.{}", ms >> 16, ms & 0xFFFF, ls >> 16))
}

/// XHL-Core's DLL, by the markers engine_mods::xhl_host recognises it with on the game's load paths.
pub(crate) fn is_xhl_core(bytes: &[u8]) -> bool {
    crate::pe::is_pe64_dll(bytes) && crate::pe::parse(bytes).is_ok_and(|p| p.exports.iter().any(|s| s == crate::pe::DINPUT8_EXPORT))
        && ["XHL-Native-Plugin-Host.log", "native-plugin.ini"].iter().all(|s| crate::pe::contains_text(bytes, s))
}

pub(crate) const XHL_CORE_ID: &str = "xhl-core";

/// The id and name of a package's own DLL under a proxy name (Auto Loot Plus's dinput8.dll, an XHL-Core bundled with an
/// XHL mod): what stays the same across versions and downloads, so that a new version or a second copy replaces the
/// one in the profile (quick::merge goes by id) instead of being chain-loaded next to it. XHL-Core is "xhl-core"; a DLL
/// that reads its package's `mods/<folder>` (Auto Loot Plus and Auto Loot Plus Lite: `mods/auto-loot-plus`, a folder
/// name the DLL itself names) takes that folder's name; any other the package's name without its version.
/// `package`: the folder holding the DLL; `package_name`: what the package is called (an archive's file name).
fn package_dll_identity(dll: &Path, package: &Path, package_name: &str) -> (String, String) {
    if std::fs::read(dll).is_ok_and(|b| is_xhl_core(&b)) {
        return (XHL_CORE_ID.into(), "XHL-Core".into());
    }
    let mut folders: Vec<PathBuf> = std::fs::read_dir(package).into_iter().flatten().flatten().map(|e| e.path())
        .filter(|p| file_name(p).eq_ignore_ascii_case("mods") && p.is_dir() && !p.is_symlink())
        .flat_map(|p| std::fs::read_dir(p).into_iter().flatten().flatten().map(|e| e.path()))
        .filter(|p| p.is_dir() && !p.is_symlink() && crate::engine_mods::is_game_folder_name(&file_name(p)))
        .collect();
    folders.sort();
    let dll_stem = dll.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    if folders.is_empty() {
        return (distinct_id(package_stem(package_name), &dll_stem), package_name.to_string());
    }
    let names: Vec<String> = folders.iter().map(|p| file_name(p)).collect();
    // named after the package (without its version), so two packages that carry the same mods/<folder> show apart
    // and quick::merge can say which one replaced which; the same for every download of one version
    (distinct_id(&names.join("-"), &dll_stem), format!("{} ({})", package_stem(package_name), file_name(dll)))
}

fn emf_manifest(dir: &Path) -> Option<(serde_json::Value, String)> {
    let text = crate::manifest::read_text(&dir.join(EMF_MANIFEST)).ok()?;
    let v = crate::jsonc::parse(&text).ok()?;
    let emf = v.get("emf")?;
    if !emf.is_i64() && !emf.is_u64() {
        return None;
    }
    let id = v.get("id")?.as_str()?.to_string();
    let dll = v.get("dll").and_then(|d| d.as_str()).map(str::to_string).unwrap_or_else(|| format!("{id}.dll"));
    dir.join(&dll).is_file().then_some((v, dll))
}

/// Why a Kiln or native manifest can't be used, for the player: an id that is not a Kiln id gets its own sentence.
fn manifest_problem(file: &str, message: &str) -> String {
    let id = message.split_once("id '").and_then(|(_, rest)| rest.split_once("' must match")).map(|(id, _)| id);
    match id {
        Some(id) => format!("{file} gives the id {id:?}, which is not a Kiln mod id (lower case letters, digits, . _ -, at most 64): the author has to change it"),
        None => format!("{file} can't be used: {message}"),
    }
}

/// Folders that are never a mod and say nothing worth reporting beside the mods of a package.
fn is_junk_folder(name: &str) -> bool {
    matches!(name.to_ascii_lowercase().as_str(), "__macosx" | "fomod" | "docs" | "doc" | "documentation" | "screenshots"
        | "licenses" | "licences" | "images")
}

/// A file that documents or installs a package (readmes, licences, scripts, a mod manager's instructions): not data
/// the mod reads.
fn is_docs_file(name: &str) -> bool {
    let low = name.to_ascii_lowercase();
    let ext = low.rsplit_once('.').map(|(_, e)| e).unwrap_or("");
    if matches!(ext, "txt" | "md" | "rtf" | "pdf" | "nfo" | "url" | "lnk" | "html" | "htm" | "bat" | "cmd" | "ps1" | "sh") {
        return true;
    }
    low == "vortex_override_instructions.json"
        || ["readme", "license", "licence", "changelog", "credits", "notice", "permissions", "third_party", "copying", "uninstall"]
            .iter().any(|w| low.starts_with(w))
}

/// The manifest files that make a folder a mod of some engine.
const MOD_MANIFESTS: [&str; 8] = ["mod.json", "kiln.mod.json", "native.json", "flame.json", "pack.toml", "init.lua", "extended.mod.json", "native-plugin.ini"];

/// Is one of `dir`'s folders a mod on its own?
fn has_mod_subfolder(dir: &Path) -> bool {
    std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path())
        .any(|p| p.is_dir() && !p.file_name().is_some_and(|n| n.to_string_lossy().starts_with('.')) && has_mod_manifest(&p))
}

/// Does `dir` hold one of [`MOD_MANIFESTS`]? XHL-Core's native-plugin.ini in any case, as detection finds it.
fn has_mod_manifest(dir: &Path) -> bool {
    MOD_MANIFESTS.iter().any(|m| dir.join(m).is_file()) || crate::engine_mods::has_xhl_plugin_ini(dir)
}

/// What the DLLs directly in a folder are: the ones that are no known loader or engine mod (plain DLL mods), every
/// other DLL, and a loader of a tool that runs on its own (Aldrich's mod menu, ReShade ...).
struct TopDlls {
    bare: Vec<PathBuf>,
    others: usize,
    tool: Option<(crate::engines::Engine, String)>,
}

fn survey_dlls(dir: &Path) -> TopDlls {
    let mut t = TopDlls { bare: Vec::new(), others: 0, tool: None };
    for p in crate::engines::dlls_in(dir) {
        let d = detect_dll(&p);
        match (d.kind, d.engine) {
            (DetectKind::BareDll, _) => t.bare.push(p),
            (DetectKind::Proxy, Some(e)) if crate::engines::is_standalone_tool(e) => {
                t.tool.get_or_insert((e, file_name(&p)));
                t.others += 1;
            }
            _ => t.others += 1,
        }
    }
    t
}

/// A plain DLL mod with the files it reads next to it (`dinput8.dll` + `.ini`/`.dat`, `powrprof.dll` + a folder):
/// the folder is one mod, so the files stay with the DLL. A lone DLL with only documents is the DLL alone, except a
/// DLL under a proxy name, whose id and name come from the package.
fn package_dll(dir: &Path, dlls: &TopDlls) -> Option<PathBuf> {
    let [dll] = dlls.bare.as_slice() else { return None };
    if dlls.others > 0 || has_mod_subfolder(dir) {
        return None;
    }
    (has_package_data(dir, dll) || crate::engines::is_proxy_name(&file_name(dll))).then(|| dll.clone())
}

/// Does the folder hold files the DLL may read (anything but documents, installer scripts and other DLLs)?
fn has_package_data(dir: &Path, dll: &Path) -> bool {
    std::fs::read_dir(dir).into_iter().flatten().flatten().any(|e| {
        let name = e.file_name().to_string_lossy().into_owned();
        let is_dir = e.path().is_dir();
        !name.starts_with('.') && e.path() != *dll && !(if is_dir { is_junk_folder(&name) } else { is_docs_file(&name) })
            && !name.to_ascii_lowercase().ends_with(".dll")
    })
}

/// A package's native input proxy, as opposed to the EML loader often shipped beside it. Other bare DLLs beside a
/// single proxy are its dependencies, not independent mods to execute. Keep this rule narrow: two proxies are ambiguous.
pub(crate) fn native_bundle_proxy(dir: &Path) -> Option<PathBuf> {
    let mut proxies = crate::engines::dlls_in(dir).into_iter().filter(|p| {
        crate::engines::is_proxy_name(&file_name(p)) && detect_dll(p).kind == DetectKind::BareDll
            && std::fs::read(p).ok().and_then(|b| crate::pe::parse(&b).ok())
                .is_some_and(|info| info.exports.iter().any(|e| e == crate::pe::DINPUT8_EXPORT))
    });
    let p = proxies.next()?;
    proxies.next().is_none().then_some(p)
}

/// Files owned by the native part of a game-folder bundle. Engine mods and bundled loaders remain separate items.
fn native_companion(path: &Path, proxy: &Path) -> bool {
    if path == proxy { return true; }
    let name = file_name(path);
    if name.starts_with('.') { return false; }
    if path.is_dir() {
        return !name.eq_ignore_ascii_case("mods") && !name.eq_ignore_ascii_case(crate::engine_mods::SHROUDLINK_MODS)
            && !crate::engines::is_container(path) && !has_mod_subfolder(path)
            && !has_mod_manifest(path)
            && !crate::engines::dlls_in(path).iter().any(|dll| detect_dll(dll).kind != DetectKind::BareDll);
    }
    if path.extension().is_some_and(|e| e.eq_ignore_ascii_case("dll")) {
        return detect_dll(path).kind == DetectKind::BareDll && !crate::engines::is_proxy_name(&name);
    }
    !MOD_MANIFESTS.iter().any(|m| name.eq_ignore_ascii_case(m))
}

/// Translate this package's chain into native.json's DLL list. The INI itself is retained beside the proxy, but is
/// never written over the player's game-wide chain. Helpers not listed here are resolved by Windows beside the DLL.
fn package_chain(dir: &Path, primary: &str, report: &mut ImportReport, label: &str) -> Vec<String> {
    let mut dlls = vec![primary.to_string()];
    let ini = dir.join("dinput8-chain.ini");
    let Ok(bytes) = std::fs::read(&ini) else { return dlls };
    if bytes.len() > crate::chain::MAX_BYTES {
        report.skipped.push(Skipped { path: label.into(), reason: "dinput8-chain.ini exceeds the chain size limit".into() });
        return dlls;
    }
    let text = crate::chain::decode(&bytes);
    for line in crate::chain::lines(&text) {
        let crate::chain::IniLine::Text(section, text) = line else { continue };
        if section.is_some_and(|s| s != "chain" && s != "emf") { continue; }
        // the proxy's own rules for this file (dinput8.cpp shared_chain_value): a comment after the name is cut,
        // "key = value" counts by its value, quotes are dropped, and a line that names no DLL is no entry
        let value = crate::chain::shared_chain_value(text);
        if !value.to_ascii_lowercase().ends_with(".dll") { continue; }
        let rel = value.replace('\\', "/");
        if crate::bundle::safe_rel_path(&rel).is_err() {
            report.skipped.push(Skipped { path: label.into(), reason: format!("dinput8-chain.ini: unsafe DLL path {value:?} was not imported") });
            continue;
        }
        // Windows finds the file whatever the case of its name: the path as it is spelled in the package
        // A redundant bundled EML proxy is deliberately not present in this native folder: kiln-eml runs those mods.
        let Some(rel) = rel_ignoring_case(dir, &rel) else {
            report.note(label, format!("dinput8-chain.ini: {value} is not in the native package (a bundled loader or a missing file); not chained"));
            continue;
        };
        if !dlls.iter().any(|p| p.eq_ignore_ascii_case(&rel)) { dlls.push(rel); }
    }
    dlls
}

/// `rel` ('/'-separated, already checked to be safe) below `dir` as the files are spelled there, ASCII case ignored;
/// None when no such file exists. An exact match wins over another spelling.
fn rel_ignoring_case(dir: &Path, rel: &str) -> Option<String> {
    let mut at = dir.to_path_buf();
    let mut out = Vec::new();
    for part in rel.split('/').filter(|p| !p.is_empty()) {
        let name = if at.join(part).symlink_metadata().is_ok() { part.to_string() } else {
            std::fs::read_dir(&at).ok()?.flatten().map(|e| e.file_name().to_string_lossy().into_owned())
                .find(|n| n.eq_ignore_ascii_case(part))?
        };
        at.push(&name);
        out.push(name);
    }
    at.is_file().then(|| out.join("/"))
}

/// `top_name`: what the package is called when `dir` is an unpacked archive (its file name without the extension).
fn detect_folder_at(dir: &Path, depth: usize, top_name: Option<&str>) -> Detected {
    let p = dir.display().to_string();
    let mk = |kind, id: Option<String>, version: Option<String>, name: Option<String>, reason: String| Detected {
        kind, path: p.clone(), id, version, name, reason, items: vec![], engine: None,
    };
    if dir.is_symlink() || depth > 32 {
        return mk(DetectKind::Unknown, None, None, None, "links or folders deeper than 32 levels are not mod imports".into());
    }
    if dir.join(crate::bundle::MANIFEST).is_file() {
        return mk(DetectKind::Runtime, None, None, None, "a staged Kiln runtime (kiln-runtime.json)".into());
    }
    if dir.join(MODPACK_MANIFEST).is_file() {
        let mut d = mk(DetectKind::Modpack, None, None, None, "a server modpack (kiln-modpack.json)".into());
        d.items = children(dir, depth, top_name);
        return d;
    }
    if dir.join(crate::manifest::KILN_MANIFEST).is_file() {
        return match crate::manifest::read_kiln(dir) {
            Ok(m) => mk(DetectKind::Kiln, Some(m.id), Some(m.version), Some(m.name), "a Kiln mod (kiln.mod.json)".into()),
            Err(e) => mk(DetectKind::Unknown, None, None, None, manifest_problem("kiln.mod.json", &e.message)),
        };
    }
    if dir.join(crate::manifest::NATIVE_MANIFEST).is_file() {
        return match crate::manifest::read_native(dir) {
            Ok(m) => mk(DetectKind::Native, Some(m.id.clone()), Some(m.version.clone()), m.name.clone().or(Some(m.id)), "a native mod (native.json)".into()),
            Err(e) => mk(DetectKind::Unknown, None, None, None, manifest_problem("native.json", &e.message)),
        };
    }
    if let Some((v, _)) = emf_manifest(dir) {
        let s = |k: &str| v.get(k).and_then(|x| x.as_str()).map(str::to_string);
        return mk(DetectKind::Emf, s("id"), s("version"), s("name"), format!("an EMF mod (mod.json, EMF API {})", v["emf"]));
    }
    if let Some(d) = crate::engines::detect_folder(dir) {
        return d;
    }
    let has_lua = dir.join("src/mod.lua").is_file()
        || std::fs::read_dir(dir).into_iter().flatten().flatten().any(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lua")));
    // An XHL mod without an EML part: a plugin XHL-Core loads from the game's mods folder, never a DLL to load on its own.
    // XHL's mods since 2026-11 also carry a mod.json that only names the mod (id, name, version, no src/mod.lua): it is
    // no EML package, and the folder is still XHL-Core's plugin.
    if crate::engine_mods::has_xhl_plugin_ini(dir) && !has_lua {
        // XHL-Core loads every folder that holds the ini: a copy suffix, or a version in an archive's name, would make
        // a second companion beside the first
        let name = match top_name.filter(|_| depth == 0) {
            Some(n) if n != file_name(dir) => package_stem(n).to_string(),
            Some(n) => strip_copy_suffix(n).to_string(),
            None => strip_copy_suffix(&file_name(dir)).to_string(),
        };
        if !crate::engine_mods::is_game_folder_name(&name) {
            return mk(DetectKind::Unknown, None, None, None, "XHL-Core's plugin folder is not a safe Windows folder name".into());
        }
        return mk(DetectKind::Xhl, Some(name.clone()), Some("0.0.0".into()), Some(name),
            "requires XHL-Core: Kiln preserves and places this native-plugin.ini companion in the game's mods folder; Kiln never loads this plugin DLL itself".into());
    }
    // A package with an unknown/broken manifest is one item, never a bag of helper DLLs to execute.
    if dir.join(EMF_MANIFEST).is_file() || has_lua {
        return mk(DetectKind::Unknown, None, None, None, crate::shroudforge::skip_reason(dir));
    }
    // `mods/<name>/` with nothing but settings files: the settings of a DLL that reads them in the game folder
    // (Auto Loot Plus Lite: dinput8.dll + mods/auto-loot-plus/config.txt)
    if depth > 0 && dir.parent().and_then(Path::file_name).is_some_and(|n| n.eq_ignore_ascii_case("mods")) && only_settings(dir) {
        let name = file_name(dir);
        return mk(DetectKind::Unknown, None, None, None, format!("mods/{name} {SETTINGS_WORDS}: a DLL of this package reads them in the \
            game's mods folder, and Kiln does not copy them there. To use them, copy the folder {name} into the mods folder of the \
            Enshrouded folder yourself; without them the mod starts with its own defaults or makes the file itself"));
    }
    if !crate::engines::is_container(dir) {
        let dlls = survey_dlls(dir);
        // a tool's package (Aldrich's mod menu, ReShade): left out whole, its helper DLLs are no mods
        if let Some((engine, file)) = dlls.tool {
            let mut d = mk(DetectKind::Proxy, None, None, None, crate::engines::loader_reason(engine, &file));
            d.engine = Some(engine);
            return d;
        }
        // a plain DLL mod with the files it reads
        if let Some(dll) = package_dll(dir, &dlls) {
            let shown = match (depth, top_name) {
                (0, Some(n)) => n.to_string(),
                _ => file_name(dir),
            };
            let reason = format!("{}: a DLL mod with its files, added as one native mod", file_name(&dll));
            // under a proxy name the DLL says nothing about the mod: the same identity as a bundle's proxy, so the next
            // version replaces this one (Auto Loot Plus Lite: dinput8.dll + mods/auto-loot-plus/config.txt)
            let (id, shown) = if crate::engines::is_proxy_name(&file_name(&dll)) {
                package_dll_identity(&dll, dir, &shown)
            } else {
                (id_from_name(&shown), shown)
            };
            return mk(DetectKind::BareDll, Some(id), Some("0.0.0".into()), Some(shown), reason);
        }
    }
    // a folder of mods (an unpacked zip): what is inside
    let mut items = children(dir, depth, top_name);
    // A package shaped like the game folder whose own DLL sits under a proxy name next to its `mods` folder (Auto Loot
    // Plus: dinput8.dll + mods/auto-loot-plus): the DLL is named after the package, not "dinput8.dll".
    if let (0, Some(n)) = (depth, top_name) {
        let mut top = items.iter_mut().filter(|i| i.kind == DetectKind::BareDll && Path::new(&i.path).parent() == Some(dir)
            && crate::engines::is_proxy_name(&file_name(Path::new(&i.path))));
        if let (Some(only), None) = (top.next(), top.next()) {
            let (id, name) = package_dll_identity(Path::new(&only.path), dir, n);
            only.id = Some(id);
            only.name = Some(name);
        }
    }
    if items.iter().any(|i| i.kind != DetectKind::Unknown) {
        let n = items.iter().filter(|i| i.kind.importable()).count();
        // a loader the package carries that Kiln does not need (it runs that engine's mods itself) is not something
        // "Kiln can't load yet": Find's zip brings Greeneye Loader's dbgcore.dll, and the mod beside it is all of it
        let unneeded = |i: &Detected| i.kind == DetectKind::Proxy && i.engine.is_some_and(crate::engines::loader_is_note);
        let loaders = items.iter().filter(|i| unneeded(i)).count();
        let other = items.iter().filter(|i| i.kind != DetectKind::Unknown && !i.kind.importable() && !unneeded(i)).count();
        let reason = if n == 0 {
            // nothing Kiln can add: the first item says what it is and why
            items.iter().find(|i| i.kind != DetectKind::Unknown).map(|i| i.reason.clone()).unwrap_or_default()
        } else {
            let mut text = format!("a folder with {n} importable item(s)");
            if other > 0 {
                text += &format!(" and {other} Kiln can't load yet");
            }
            if loaders > 0 {
                text += &format!("; {loaders} loader(s) in it are not needed");
            }
            text
        };
        let mut d = mk(DetectKind::Unknown, None, None, None, reason);
        d.items = items;
        return d;
    }
    // nothing usable: an item with its own reason says it better than the package's generic one
    if let Some(i) = items.iter().find(|i| i.reason.contains(SETTINGS_WORDS)) {
        return mk(DetectKind::Unknown, None, None, None, i.reason.clone());
    }
    mk(DetectKind::Unknown, None, None, None, crate::shroudforge::skip_reason(dir))
}

const SETTINGS_WORDS: &str = "holds only settings";

/// A folder with files and nothing but settings files (`.txt`, `.ini`, `.cfg`, `.json`, `.toml`, `.yaml`) in it.
fn only_settings(dir: &Path) -> bool {
    let mut any = false;
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let low = e.file_name().to_string_lossy().to_ascii_lowercase();
        if low.starts_with('.') { continue; }
        if MOD_MANIFESTS.contains(&low.as_str()) { return false; }
        let ext = low.rsplit_once('.').map(|(_, x)| x).unwrap_or("");
        if !e.path().is_file() || !matches!(ext, "txt" | "ini" | "cfg" | "json" | "toml" | "yaml" | "yml") { return false; }
        any = true;
    }
    any
}

/// Settings-only game folders carried by a native package (Auto Loot Plus Lite). They are not executable mods.
/// Keep the original folder spelling, but never accept a destination outside one safe Windows component.
pub(crate) fn native_settings_dirs(dir: &Path) -> Vec<PathBuf> {
    let mut out: Vec<_> = std::fs::read_dir(dir).into_iter().flatten().flatten()
        .map(|e| e.path()).filter(|p| file_name(p).eq_ignore_ascii_case("mods") && p.is_dir())
        .flat_map(|p| std::fs::read_dir(p).into_iter().flatten().flatten().map(|e| e.path()))
        .filter(|p| p.is_dir() && crate::engine_mods::is_game_folder_name(&file_name(p)) && only_settings(p))
        .collect();
    out.sort();
    out
}

/// `top_name`: what `dir` is called when it is an unpacked archive at depth 0 (detect_folder_at).
fn children(dir: &Path, depth: usize, top_name: Option<&str>) -> Vec<Detected> {
    let mut entries: Vec<PathBuf> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).collect();
    entries.sort();
    let native = native_bundle_proxy(dir);
    let settings = if native.is_some() { native_settings_dirs(dir) } else { Vec::new() };
    // `dir` is a package's `mods` folder: its settings-only folders belong to the package's native proxy (read once,
    // not once per entry: each look reads and parses the proxy DLL)
    let parent_settings = match dir.parent() {
        Some(parent) if dir.file_name().is_some_and(|n| n.eq_ignore_ascii_case("mods")) && native_bundle_proxy(parent).is_some() =>
            native_settings_dirs(parent),
        _ => Vec::new(),
    };
    let package_name = match (depth, top_name) {
        (0, Some(n)) => n.to_string(),
        _ => file_name(dir),
    };
    let mut out = Vec::new();
    for e in entries {
        let name = e.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        if name.starts_with('.') {
            continue;
        }
        if e.is_symlink() {
            out.push(Detected { kind: DetectKind::Unknown, path: e.display().to_string(), id: None, version: None, name: None,
                reason: "links are not mod imports".into(), items: vec![], engine: None });
            continue;
        }
        if native.as_ref().is_some_and(|p| &e != p && (native_companion(&e, p)
            || (name.eq_ignore_ascii_case("mods") && std::fs::read_dir(&e).is_ok_and(|mut entries| entries.all(|entry|
                entry.is_ok_and(|entry| settings.contains(&entry.path()))))))) {
            continue; // kept with the native proxy, never imported/executed as another mod
        }
        if parent_settings.contains(&e) {
            continue;
        }
        if e.is_dir() {
            let d = detect_folder_at(&e, depth + 1, None);
            if d.kind == DetectKind::Unknown && !d.items.is_empty() {
                out.extend(d.items);
            } else if d.kind != DetectKind::Unknown || !is_junk_folder(&name) {
                out.push(d);
            }
        } else if name.to_lowercase().ends_with(".dll") {
            let mut d = detect_dll(&e);
            if native.as_ref() == Some(&e) {
                let (id, name) = package_dll_identity(&e, dir, &package_name);
                d.id = Some(id);
                d.name = Some(name);
            }
            out.push(d);
        } else if let Some(d) = crate::engines::detect_file(&e) {
            out.push(d);
        }
    }
    out
}

/// Is this a package file Kiln opens itself or with a tool: a zip, a 7z or a rar?
pub fn is_archive(path: &Path) -> bool {
    matches!(archive_kind(path), Some(_))
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum ArchiveKind {
    Zip,
    SevenZ,
    Rar,
}

fn archive_kind(path: &Path) -> Option<ArchiveKind> {
    let ext = path.extension()?.to_string_lossy().to_ascii_lowercase();
    match ext.as_str() {
        "zip" => Some(ArchiveKind::Zip),
        "7z" => Some(ArchiveKind::SevenZ),
        "rar" => Some(ArchiveKind::Rar),
        _ => None,
    }
}

/// What an archive is called: its file name without the extension.
fn archive_name(path: &Path) -> Option<String> {
    path.file_stem().map(|n| n.to_string_lossy().into_owned())
}

pub fn detect(path: &Path) -> Result<Detected> {
    let meta = std::fs::symlink_metadata(path).map_err(io(path, "reading"))?;
    if crate::quick::import_is_link(&meta) {
        return Ok(Detected { kind: DetectKind::Unknown, path: path.display().to_string(), id: None, version: None, name: None,
            reason: "links are not mod imports".into(), items: vec![], engine: None });
    }
    crate::quick::validate_import_path(path)?;
    if meta.is_dir() {
        return Ok(detect_folder_at(path, 0, path.file_name().map(|n| n.to_string_lossy().into_owned()).as_deref()));
    }
    let lower = path.to_string_lossy().to_lowercase();
    if lower.ends_with(".dll") {
        return Ok(detect_dll(path));
    }
    if is_archive(path) {
        let tmp = unpack_to_temp(path, &std::env::temp_dir())?;
        let mut d = detect_folder_at(tmp.path(), 0, archive_name(path).as_deref());
        relabel(&mut d, tmp.path(), path);
        return Ok(d);
    }
    if let Some(d) = crate::engines::detect_file(path) {
        return Ok(d);
    }
    Ok(Detected {
        kind: DetectKind::Unknown,
        path: path.display().to_string(),
        id: None,
        version: None,
        name: None,
        reason: "not a folder, .zip, .7z, .rar or .dll".into(),
        items: vec![],
        engine: None,
    })
}

/// Paths inside an unpacked zip shown as `<zip>/<entry>`.
fn relabel(d: &mut Detected, tmp: &Path, zip: &Path) {
    if let Ok(rest) = Path::new(&d.path).strip_prefix(tmp) {
        // Path::join, not "/": a verbatim Windows path (\\?\C:\...) does not read '/' as a separator
        d.path = if rest.as_os_str().is_empty() { zip.display().to_string() } else { zip.join(rest).display().to_string() };
    }
    for i in &mut d.items {
        relabel(i, tmp, zip);
    }
}

/// A temporary folder removed on drop.
pub struct TempDir(PathBuf);

impl TempDir {
    pub fn new(parent: &Path, tag: &str) -> Result<TempDir> {
        let p = parent.join(format!(".{tag}-{}", crate::hash::hex(&crate::util::random_bytes::<6>()?)));
        std::fs::create_dir_all(&p).map_err(io(&p, "creating"))?;
        Ok(TempDir(p))
    }
    pub fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Unpacks a package (a zip; a 7z; a rar through a tool the PC already has: [`crate::archive`]) into a temp folder
/// below `parent`: zip-slip guarded, links refused, the import limits kept.
pub fn unpack_to_temp(zip: &Path, parent: &Path) -> Result<TempDir> {
    // This is shared by direct Library callers and modpack installation, which do not necessarily go
    // through quick::add. Bound the central directory and expanded bytes before creating any output.
    crate::quick::validate_import_path(zip)?;
    let tmp = TempDir::new(parent, "kiln-unpack")?;
    match archive_kind(zip) {
        Some(ArchiveKind::SevenZ) => crate::archive::unpack_sevenz(zip, tmp.path())?,
        Some(ArchiveKind::Rar) => crate::archive::unpack_rar(zip, tmp.path(), parent)?,
        _ => unpack(zip, tmp.path())?,
    }
    Ok(tmp)
}

#[cfg(feature = "zip")]
fn unpack(zip: &Path, dest: &Path) -> Result<()> {
    use std::io::Read;
    let f = std::fs::File::open(zip).map_err(io(zip, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?;
    if a.len() > crate::quick::MAX_IMPORT_ENTRIES {
        return Err(Error::new(ErrorCode::Refused, "Too many files in this zip."));
    }
    let mut total = 0u64;
    for i in 0..a.len() {
        let mut e = a.by_index(i).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?;
        let name = e.name().map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?.replace('\\', "/");
        if e.is_symlink() {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlink entry {name}", zip.display())));
        }
        let rel = crate::bundle::safe_rel_path(name.trim_end_matches('/'))?;
        if Path::new(rel).components().count() > crate::quick::MAX_IMPORT_DEPTH {
            return Err(Error::new(ErrorCode::Refused, "Paths in this zip are too deeply nested."));
        }
        if e.size() > crate::quick::MAX_IMPORT_FILE || total.saturating_add(e.size()) > crate::quick::MAX_IMPORT_BYTES {
            return Err(Error::new(ErrorCode::Refused, "Expanded zip exceeds the import limits."));
        }
        let target = dest.join(rel);
        if e.is_dir() {
            std::fs::create_dir_all(&target).map_err(io(&target, "creating"))?;
            continue;
        }
        if let Some(p) = target.parent() {
            std::fs::create_dir_all(p).map_err(io(p, "creating"))?;
        }
        let mut out = std::fs::File::create(&target).map_err(io(&target, "writing"))?;
        let allowance = crate::quick::MAX_IMPORT_FILE.min(crate::quick::MAX_IMPORT_BYTES - total);
        let n = std::io::copy(&mut (&mut e).take(allowance + 1), &mut out).map_err(io(&target, "writing"))?;
        if n > allowance || n != e.size() {
            return Err(Error::new(ErrorCode::Refused, "Expanded zip exceeds its declared size or the import limits."));
        }
        total += n;
    }
    Ok(())
}

#[cfg(not(feature = "zip"))]
fn unpack(zip: &Path, _dest: &Path) -> Result<()> {
    Err(Error::new(ErrorCode::Unsupported, format!("{}: kiln-manage was built without the zip feature", zip.display())))
}

/// Copies a mod folder without its dot entries (not part of its kdh1); symlinks are refused.
pub(crate) fn copy_mod_tree(from: &Path, to: &Path) -> Result<u64> {
    crate::quick::validate_import_path(from)?;
    copy_mod_tree_inner(from, to, None, &mut false)
}

/// [`copy_mod_tree`] for a Kiln mod on its way into the Library: its cook outputs (`assets/cooked`, with everything in
/// it) are not copied. They are no part of the mod (no hash covers them: `hash::mod_identity`), so whatever a mod
/// folder or archive carries there never reaches the Library: cooked files are made on this PC, by a cook. -> (bytes
/// copied, whether the source held cook outputs that were left out). A link named `assets/cooked` is refused like any.
pub(crate) fn copy_kiln_mod_tree(from: &Path, to: &Path) -> Result<(u64, bool)> {
    crate::quick::validate_import_path(from)?;
    let cooked = crate::hash::is_kiln_mod(from).then(|| crate::hash::COOKED.split('/').fold(from.to_path_buf(), |p, part| p.join(part)));
    let mut left_out = false;
    let size = copy_mod_tree_inner(from, to, cooked.as_deref(), &mut left_out)?;
    Ok((size, left_out))
}

fn copy_mod_tree_inner(from: &Path, to: &Path, skip: Option<&Path>, skipped: &mut bool) -> Result<u64> {
    std::fs::create_dir_all(to).map_err(io(to, "creating"))?;
    let mut size = 0;
    for e in std::fs::read_dir(from).map_err(io(from, "reading"))? {
        let e = e?;
        let name = e.file_name();
        if name.to_string_lossy().starts_with('.') {
            continue;
        }
        let meta = std::fs::symlink_metadata(e.path())?;
        let ft = meta.file_type();
        let dst = to.join(&name);
        if crate::quick::import_is_link(&meta) {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not allowed in a mod folder", e.path().display())));
        } else if ft.is_dir() && skip == Some(e.path().as_path()) {
            // a Kiln mod's cook outputs: left out (an empty folder is nothing to tell the player about)
            *skipped |= std::fs::read_dir(e.path()).is_ok_and(|rd| rd.flatten().any(|x| !x.file_name().to_string_lossy().starts_with('.')));
        } else if ft.is_dir() {
            size += copy_mod_tree_inner(&e.path(), &dst, skip, skipped)?;
        } else if ft.is_file() {
            size += std::fs::copy(e.path(), &dst).map_err(io(&dst, "copying to"))?;
        } else {
            return Err(Error::new(ErrorCode::Refused, format!("{}: not a regular file or folder", e.path().display())));
        }
    }
    Ok(size)
}

/// The name part of a Library folder moved aside to be removed ([`Guard::remove_checked`]).
const REMOVING: &str = ".removing-";

fn record_path(folder: &Path) -> PathBuf {
    crate::util::with_suffix(folder, ".json")
}

fn kind_dir(dirs: &DataDirs, kind: ModKind) -> PathBuf {
    dirs.library().join(kind.as_str())
}

/// The manifest id of whatever holds library id `id` of `kind` now (None: nothing does). An entry without a
/// `manifestId` is its own manifest id.
pub(crate) fn id_holder(dirs: &DataDirs, kind: ModKind, id: &str) -> Option<String> {
    records(dirs, kind).into_iter().find(|(_, rec)| rec.id == id).map(|(_, rec)| rec.manifest_id.unwrap_or(rec.id))
}

/// A LibraryRef is also deserialized from callers and profiles. Validate before joining any of its fields.
fn checked_entry(dirs: &DataDirs, r: &LibraryRef) -> Result<PathBuf> {
    if !crate::manifest::is_mod_id(&r.id) || r.id.chars().any(char::is_control) {
        return Err(Error::invalid(format!("invalid library mod id {:?}", r.id)));
    }
    if r.version != r.version.trim() || r.version.contains(['/', '\\', ':']) || r.version.chars().any(char::is_control)
        || crate::semver::SemVersion::parse(&r.version).is_err()
    {
        return Err(Error::invalid(format!("invalid library version {:?}", r.version)));
    }
    if (r.kind == ModKind::Plugin) == r.hash.starts_with(crate::hash::KDH1_PREFIX) {
        return Err(Error::invalid(format!("{} uses {} hashes", r.kind.as_str(), if r.kind == ModKind::Plugin { "sha256" } else { "kdh1" })));
    }
    dirs.library_entry(r.kind, &r.id, &r.version, &r.hash)
}

fn plugin_file(folder: &Path, file: &str) -> Result<PathBuf> {
    crate::bundle::safe_rel_path(file)?;
    if file.contains('/') || !file.to_ascii_lowercase().ends_with(".dll") {
        return Err(Error::invalid("a library plugin must name one DLL directly in its folder"));
    }
    Ok(folder.join(file))
}

fn recorded_hash(folder: &Path, rec: &ImportRecord) -> Result<String> {
    if rec.kind == ModKind::Plugin {
        let file = rec.file.as_deref().ok_or_else(|| Error::invalid("a library plugin record must name its DLL"))?;
        Ok(crate::hash::sha256_file(&plugin_file(folder, file)?)?.0)
    } else {
        // a Kiln mod's identity: an entry the player cooked in place is still the entry that was imported
        crate::hash::of_mod(rec.kind, folder)
    }
}

/// Moves a prepared folder into the library as `<kind>/<id>/<version>+<hash8>` with its record. Existing entries
/// (same hash) are kept as they are (content addressed: same name, same bytes).
fn commit(dirs: &DataDirs, prepared: &Path, rec: ImportRecord, report: &mut ImportReport) -> Result<()> {
    commit_folder(dirs, prepared, rec, report)
}

/// For engine loaders (engines::Loader): moves a prepared folder (made in a [`TempDir`] below `dirs.library()`) into
/// the library as `<kind>/<id>/<version>+<hash8>` with its record; an entry with the same hash is kept.
pub fn commit_folder(dirs: &DataDirs, prepared: &Path, rec: ImportRecord, report: &mut ImportReport) -> Result<()> {
    crate::quick::validate_import_path(prepared)?;
    let r = LibraryRef { kind: rec.kind, id: rec.id.clone(), version: rec.version.clone(), hash: rec.hash.clone() };
    let folder = checked_entry(dirs, &r)?;
    if rec.format != 1 || recorded_hash(prepared, &rec)? != rec.hash {
        return Err(Error::new(ErrorCode::HashMismatch, "Prepared library content does not match its import record."));
    }
    let shown = rec.name.clone().filter(|n| !n.trim().is_empty()).or_else(|| rec.manifest_id.clone()).unwrap_or_else(|| rec.id.clone());
    report.names.insert(format!("{}/{}", rec.kind.as_str(), rec.id), shown);
    if folder.is_dir() && record_path(&folder).is_file() {
        let old = record(dirs, &r)?;
        if recorded_hash(&folder, &old)? != r.hash {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: existing library content does not match its hash", folder.display())));
        }
        // An entry imported before Kiln knew its package's DLL reads it in the game folder learns that now (the same
        // archive dropped again); the content and everything else in the record stay.
        if rec.game_folder.is_some() && old.game_folder.is_none() {
            crate::util::write_json_atomic(&record_path(&folder), &ImportRecord { game_folder: rec.game_folder.clone(), ..old })?;
        }
        report.existing.push(r);
        return Ok(());
    }
    if let Some(p) = folder.parent() {
        std::fs::create_dir_all(p).map_err(io(p, "creating"))?;
    }
    let _ = std::fs::remove_dir_all(&folder); // a half import without its record
    std::fs::rename(prepared, &folder).map_err(io(&folder, "moving into the library"))?;
    crate::util::write_json_atomic(&record_path(&folder), &rec)?;
    report.imported.push(r);
    Ok(())
}

/// A Kiln entry [`migrate_identities`] moved to its identity: the hash it was recorded under, and the one it has now.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MigratedEntry {
    pub id: String,
    pub version: String,
    /// The kdh1 of the whole folder, cook outputs included: what a Kiln before 0.2.0-dev.27 recorded.
    pub from: String,
    /// Its identity (`hash::mod_identity`).
    pub to: String,
}

/// `<library>/kiln-moved.json`: every move [`migrate_identities`] made, kept, so a reference made before a move (a
/// server's list of mods, a page the launcher still shows) finds the entry it became at any later call ([`moved_to`]).
fn moved_file(dirs: &DataDirs) -> PathBuf {
    dirs.library().join("kiln-moved.json")
}

/// The moves [`migrate_identities`] made so far, oldest first (none when nothing was moved, or the file cannot be read).
pub fn moved(dirs: &DataDirs) -> Vec<MigratedEntry> {
    crate::util::read_json::<Vec<MigratedEntry>>(&moved_file(dirs)).ok().flatten().unwrap_or_default()
}

/// A reference by the hash a Kiln entry had before [`migrate_identities`] moved it: the reference it became (followed
/// through every move); any other reference as it is. A [`take_over`] is not followed: it changed the contents, and
/// whoever asked for a hash (a server's list, a modpack) is never handed other contents.
pub fn moved_to(dirs: &DataDirs, r: &LibraryRef) -> LibraryRef {
    let all = moved(dirs);
    let mut cur = r.clone();
    for _ in 0..=all.len() {
        match all.iter().find(|m| cur.kind == ModKind::Kiln && m.id == cur.id && m.version == cur.version && m.from == cur.hash) {
            Some(m) => cur.hash = m.to.clone(),
            None => break,
        }
    }
    cur
}

/// `<version>+<hash8>.taken-over` next to an entry [`take_over`] could not remove yet (something still read it): the
/// hash its files were taken over as. It lives and goes with that entry, and counts only while the entry's files are
/// still exactly those ([`taken_over_leftover`]).
fn taken_over_path(folder: &Path) -> PathBuf {
    crate::util::with_suffix(folder, ".taken-over")
}

/// An entry a take-over left behind: its marker names the hash its files became, and they still are exactly that (a
/// copy of them is the new entry). Anything else (no marker, files changed or restored since) is an entry like any.
fn taken_over_leftover(folder: &Path, rec: &ImportRecord) -> bool {
    let Ok(to) = std::fs::read_to_string(taken_over_path(folder)) else { return false };
    recorded_hash(folder, rec).is_ok_and(|h| h == to.trim())
}

/// Kiln entries imported before 0.2.0-dev.27 from a folder that held cook outputs are recorded under the kdh1 of the
/// whole folder, `assets/cooked` included. Every check compares a Kiln mod by its identity now, so such an entry would
/// read as changed: an activation in copy mode would copy it again and fail, a server sync would be refused.
///
/// Such an entry is verified against the hash it was imported with (the kdh1 of the whole folder still is the recorded
/// one: nothing changed since), then moved to its identity: a copy at `<version>+<hash8 of the identity>` with its
/// record, every profile that lists the old hash listing the new one, and the old entry removed once nothing uses it
/// (a game's profile file may still name its path until that game's next activation: then it stays until the next
/// run). Its cook outputs are left out of the copy, as an import leaves them out: nothing proves this PC made them
/// (cooked files are made on each PC by its own cook, docs/kiln/modding/cooking.md), and the launcher cooks the mod
/// before the next start. An entry at the identity that is there already is used only when its files are that
/// identity; one whose files changed is replaced by the verified copy, and the old entry is removed only after that.
/// An entry whose files changed since its import is left alone, as before. Each step can be repeated: a stop between
/// two leaves both entries, and the next run goes on. Every move is kept in `kiln-moved.json` ([`moved_to`]).
/// A damaged entry at the identity is not replaced while a game or server that may read it runs: both entries stay as
/// they are, and a later run goes on.
/// -> the entries moved by this run.
pub fn migrate_identities(dirs: &DataDirs) -> Result<Vec<MigratedEntry>> {
    let mut finder = crate::proc::Finder::default();
    migrate_identities_with(dirs, &mut |dir, exe| finder.find(dir, exe))
}

/// [`migrate_identities`] with the process lookup given (`find(game folder, exe name)`), for tests.
pub fn migrate_identities_with(dirs: &DataDirs, find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>)
                               -> Result<Vec<MigratedEntry>> {
    let mut moved = Vec::new();
    for (folder, rec) in records(dirs, ModKind::Kiln) {
        if rec.kind != ModKind::Kiln || dirs.library_entry(ModKind::Kiln, &rec.id, &rec.version, &rec.hash).ok().as_deref() != Some(folder.as_path()) {
            continue;
        }
        let Ok(identity) = crate::hash::mod_identity(&folder) else { continue };
        if identity == rec.hash || crate::hash::dirhash(&folder).ok().as_deref() != Some(rec.hash.as_str()) {
            continue; // recorded by its identity already, or changed since its import
        }
        let entry = MigratedEntry { id: rec.id.clone(), version: rec.version.clone(), from: rec.hash.clone(), to: identity.clone() };
        if move_entry(dirs, &folder, &rec, ImportRecord { hash: identity.clone(), ..rec.clone() }, Some(entry.clone()), &mut *find)?.is_some() {
            moved.push(entry);
        }
    }
    // what a removal moved aside and could not delete all the way (a file in it was open then)
    for kind in ModKind::ALL {
        for idd in std::fs::read_dir(kind_dir(dirs, kind)).into_iter().flatten().flatten() {
            for e in std::fs::read_dir(idd.path()).into_iter().flatten().flatten() {
                let name = e.file_name().to_string_lossy().into_owned();
                let aside = name.rsplit_once(REMOVING).is_some_and(|(_, tag)| tag.len() == 8 && tag.bytes().all(|b| b.is_ascii_hexdigit()));
                if aside && e.path().is_dir() && !record_path(&e.path()).exists() {
                    let _ = std::fs::remove_dir_all(e.path());
                }
            }
        }
    }
    // the entries a take-over left behind because something still read them then: gone once nothing does, and only
    // while their files are still exactly what was taken over (a copy of them is the new entry)
    for kind in ModKind::ALL {
        for (folder, rec) in records(dirs, kind) {
            if taken_over_leftover(&folder, &rec) {
                let old = LibraryRef { kind: rec.kind, id: rec.id.clone(), version: rec.version.clone(), hash: rec.hash.clone() };
                let _ = remove_with(dirs, &old, false, &mut *find);
            }
        }
    }
    Ok(moved)
}

/// Moves the Library entry at `folder` (recorded as `rec`) to the entry `to_rec` names, the step [`migrate_identities`]
/// (`migrated`: the move to keep) and [`take_over`] (`None`) share: (1) the entry at `to_rec` is used when its record
/// and files are that already, else made from a copy of `folder` checked against `to_rec.hash` (never replacing what is
/// there under a game or server that may read it, nor under a cook of it; a take-over never replaces it at all: that
/// entry's own files may be a player's edit, and it is refused); (2) a migration's move is kept in `kiln-moved.json`
/// before anything points at the new entry ([`moved_to`]); (3) every profile that lists the old entry lists the new
/// one; (4) the old entry goes unless something still reads it (a running game, a game's profile file naming its path)
/// or, for a take-over, its files changed again since they were copied: then they stay, and the answer says so.
/// A take-over also carries every file of the old folder the new entry lacks into it before the old folder goes (files
/// its hash does not count: a plugin's other files, dot files), never overwriting one, and stops when both hold a file of
/// one name with other contents ([`carry_files`]). -> None: left for a later run (busy); Some(what was left behind).
fn move_entry(dirs: &DataDirs, folder: &Path, rec: &ImportRecord, mut to_rec: ImportRecord, migrated: Option<MigratedEntry>,
              find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>) -> Result<Option<Left>> {
    let kind = rec.kind;
    let old = LibraryRef { kind, id: rec.id.clone(), version: rec.version.clone(), hash: rec.hash.clone() };
    let new = LibraryRef { kind, id: to_rec.id.clone(), version: to_rec.version.clone(), hash: to_rec.hash.clone() };
    let to = checked_entry(dirs, &new)?;
    if to == folder {
        return Err(Error::new(ErrorCode::Conflict, format!("{}: the changed files would replace their own folder", folder.display())));
    }
    // 1. the entry at the new hash (the same mod imported again may be there already): used only when its record and its
    // files are that hash, checked by its own record (a plugin's DLL may have another name there)
    let dest_rec = crate::util::read_json::<ImportRecord>(&record_path(&to)).ok().flatten();
    let mut there = to.is_dir() && dest_rec.as_ref().is_some_and(|r| r.hash == new.hash && recorded_hash(&to, r).ok().as_deref() == Some(new.hash.as_str()));
    if !there && migrated.is_none() && to.is_dir() && dest_rec.is_none() {
        // a folder without its record: a take-over stopped after its copy was put in place. When its files are the ones
        // being taken over (the same check), it is finished; else it is not Kiln's entry, and nothing of it is touched
        if recorded_hash(&to, &to_rec).ok().as_deref() != Some(new.hash.as_str()) {
            return Err(Error::new(ErrorCode::Conflict, format!(
                "A folder with no import record is where the changed files would go ({}), and its files are not these: it is not an entry of Kiln's Library. Move it out of the Library (or delete it), then try again.",
                to.display())));
        }
        to_rec.size = crate::hash::dir_records(&to)?.iter().map(|r| r.size).sum();
        crate::util::write_json_atomic(&record_path(&to), &to_rec)?;
        there = true;
    }
    if there && migrated.is_none() && let Some(rel) = carry_files(folder, &to, rec, dest_rec.as_ref().unwrap_or(&to_rec), false)? {
        return Err(clash(&rel, folder, &to));
    }
    if !there {
        if migrated.is_none() && std::fs::symlink_metadata(&to).is_ok() {
            // another entry is where the changed files would go, and its own files are not those: they may be the
            // player's edit of that entry, so nothing of it is touched
            return Err(Error::new(ErrorCode::Conflict, format!(
                "Another entry of this mod is where the changed files would go ({}), and its own files were changed too: take that one over first (or put its files back), then try again.",
                to.display())));
        }
        // what is there is replaced below: never under a game or server that may be reading it
        if std::fs::symlink_metadata(&to).is_ok() && Guard::read(dirs, &mut *find)?.refuse_while_running(dirs, &new).is_err() {
            return Ok(None);
        }
        // nor under a cook of it (a game that loads the Library entry itself cooks in it: the cook has set its last
        // good files aside there and changes the recipe, which is why it reads as changed right now): held until the
        // verified copy is in place; a cook that runs now leaves both entries to a later run
        let _cook = if std::fs::symlink_metadata(&to).is_ok() {
            match crate::state::lock_cook_folder(&to, std::time::Duration::ZERO, &|| false, "a cook of this Library entry runs") {
                Ok(held) => Some(held),
                Err(_) => return Ok(None),
            }
        } else { None };
        let tmp = TempDir::new(&dirs.library(), "migrate")?;
        let work = tmp.path().join("m");
        // a Kiln mod's cook outputs are left out, as an import leaves them out
        to_rec.size = if kind == ModKind::Kiln { copy_kiln_mod_tree(folder, &work)?.0 } else { copy_mod_tree(folder, &work)? };
        if recorded_hash(&work, &to_rec)? != new.hash {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while it was moved to its new hash", folder.display())));
        }
        // a half move without its record, or (a migration only) an entry whose files are not its hash
        if std::fs::symlink_metadata(&to).is_ok() {
            std::fs::remove_dir_all(&to).map_err(io(&to, "replacing"))?;
        }
        if let Some(p) = to.parent() {
            std::fs::create_dir_all(p).map_err(io(p, "creating"))?;
        }
        std::fs::rename(&work, &to).map_err(io(&to, "moving into the library"))?;
        crate::util::write_json_atomic(&record_path(&to), &to_rec)?;
    }
    // 2. a migration's move is kept before anything points at the new entry: a reference made before it finds that entry
    if let Some(entry) = migrated.clone() {
        let mut all = self::moved(dirs);
        if !all.contains(&entry) {
            all.push(entry);
            crate::util::write_json_atomic(&moved_file(dirs), &all)?;
        }
    }
    // 3. the profiles that list the old entry
    for summary in crate::profile::list(dirs)? {
        let mut p = crate::profile::get(dirs, &summary.id)?;
        let mut changed = false;
        for m in p.mods.list_mut(kind).iter_mut().filter(|m| m.id == old.id && m.version == old.version && m.hash == old.hash) {
            m.version = new.version.clone();
            m.hash = new.hash.clone();
            changed = true;
        }
        if changed {
            crate::profile::save(dirs, &p)?;
        }
    }
    // 4. the old entry, unless something still reads it (a running game, a game's profile file naming its path)
    if migrated.is_some() {
        let _ = remove_with(dirs, &old, false, &mut *find);
        return Ok(Some(Left::Nothing));
    }
    // a take-over removes the player's folder only while it still holds exactly what was taken: looked at again after
    // the running check, right before it goes (an editor may have saved once more meanwhile)
    let guard = Guard::read(dirs, &mut *find)?;
    if recorded_hash(folder, &to_rec).ok().as_deref() != Some(new.hash.as_str()) {
        return Ok(Some(Left::ChangedAgain));
    }
    // and only once every file of it is in the new entry with the same bytes
    let dest_rec = crate::util::read_json::<ImportRecord>(&record_path(&to)).ok().flatten().unwrap_or(to_rec);
    if let Some(rel) = carry_files(folder, &to, rec, &dest_rec, true)? {
        return Ok(Some(Left::Clash(clash(&rel, folder, &to).message)));
    }
    match guard.refuse_while_running(dirs, &old).and_then(|()| guard.remove_checked(dirs, &old, folder, &Force::No)) {
        Ok(None) => Ok(Some(Left::Nothing)),
        Ok(Some(rest)) => Ok(Some(Left::Rest(rest))),
        // something still reads it (a game's profile file names it): it stays until nothing does (migrate_identities),
        // hidden while it is this copy
        Err(e) if matches!(e.code, ErrorCode::Conflict | ErrorCode::GameRunning) => {
            let _ = crate::util::write_atomic(&taken_over_path(folder), new.hash.as_bytes());
            Ok(Some(Left::Nothing))
        }
        // it could not be moved aside (a file in it is open): it keeps its record and stays a Library entry, so a
        // later take-over finishes
        Err(e) => {
            let _ = crate::util::write_atomic(&taken_over_path(folder), new.hash.as_bytes());
            Ok(Some(Left::Stays(e.message)))
        }
    }
}

/// What a take-over left of the old folder.
enum Left {
    Nothing,
    /// It changed again while it was taken over: kept as it is.
    ChangedAgain,
    /// A file of it is in the new entry with other contents: kept (the message says which).
    Clash(String),
    /// It could not be moved aside (the reason): still the entry it was.
    Stays(String),
    /// Its record went, and this much of it could not be deleted (moved aside under this name).
    Rest(PathBuf),
}

/// The refusal for a file both folders hold with other contents.
fn clash(rel: &str, from: &Path, to: &Path) -> Error {
    Error::new(ErrorCode::Conflict, format!(
        "{rel} is in {} and in {} with different contents: Kiln keeps both and deletes nothing. Make the two files the same (or remove the one you do not want), then try again.",
        from.display(), to.display()))
}

/// Copies into `to` (an entry recorded as `to_rec`) every file of `from` (recorded as `from_rec`) that `to` lacks, never
/// overwriting one; with `write` false it only looks. A Kiln mod's cook outputs are left out (made on each PC by its
/// cook, as an import leaves them out), and so is a plugin's own DLL when the new entry has the same bytes under its own
/// name. -> the first file (relative path) both hold with other contents: nothing more is copied then.
fn carry_files(from: &Path, to: &Path, from_rec: &ImportRecord, to_rec: &ImportRecord, write: bool) -> Result<Option<String>> {
    fn walk(dir: &Path, prefix: &str, out: &mut Vec<String>) -> Result<()> {
        for e in std::fs::read_dir(dir).map_err(io(dir, "reading"))? {
            let e = e?;
            let name = e.file_name().to_string_lossy().into_owned();
            let rel = if prefix.is_empty() { name } else { format!("{prefix}/{name}") };
            let meta = std::fs::symlink_metadata(e.path())?;
            if crate::quick::import_is_link(&meta) {
                return Err(Error::new(ErrorCode::Refused, format!("{}: links are not allowed in a mod folder", e.path().display())));
            } else if meta.is_dir() {
                walk(&e.path(), &rel, out)?;
            } else if meta.is_file() {
                out.push(rel);
            }
        }
        Ok(())
    }
    let mut files = Vec::new();
    walk(from, "", &mut files)?;
    let cooked = (from_rec.kind == ModKind::Kiln).then(|| format!("{}/", crate::hash::COOKED));
    let same_dll = match (from_rec.file.as_deref(), to_rec.file.as_deref()) {
        (Some(a), Some(b)) if a != b => crate::hash::sha256_file(&from.join(a)).ok().map(|x| x.0) == crate::hash::sha256_file(&to.join(b)).ok().map(|x| x.0),
        _ => false,
    };
    for rel in files {
        if cooked.as_deref().is_some_and(|c| rel.replace('\\', "/").to_ascii_lowercase().starts_with(c)) { continue; }
        if same_dll && from_rec.file.as_deref() == Some(rel.as_str()) { continue; }
        let (src, dst) = (from.join(&rel), to.join(&rel));
        if std::fs::symlink_metadata(&dst).is_ok() {
            if !dst.is_file() || crate::hash::sha256_file(&src)?.0 != crate::hash::sha256_file(&dst)?.0 {
                return Ok(Some(rel));
            }
            continue;
        }
        if write {
            if let Some(p) = dst.parent() { std::fs::create_dir_all(p).map_err(io(p, "creating"))?; }
            std::fs::copy(&src, &dst).map_err(io(&dst, "copying to"))?;
            if crate::hash::sha256_file(&dst)?.0 != crate::hash::sha256_file(&src)?.0 {
                return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while it was copied", src.display())));
            }
        }
    }
    Ok(None)
}

/// The record of the changed files in `folder`, the entry `rec` names, read from its manifest again (`hash`: their
/// hash). Refused when the folder is no longer a mod of its kind Kiln can read, or its manifest names another mod.
fn record_of_changed(folder: &Path, rec: &ImportRecord, hash: String) -> Result<ImportRecord> {
    let kind = rec.kind;
    let shown = rec.name.clone().unwrap_or_else(|| rec.id.clone());
    let unreadable = |why: &str| Error::new(ErrorCode::Refused, format!(
        "{shown} was changed in its folder ({}), and Kiln cannot read it as a mod of its kind ({}) any more: {why}. Put its files back as they were, or fix what is named here.",
        folder.display(), kind.label()));
    let path = if kind == ModKind::Plugin {
        plugin_file(folder, rec.file.as_deref().ok_or_else(|| Error::invalid("a library plugin record must name its DLL"))?)?
    } else { folder.to_path_buf() };
    let d = detect(&path)?;
    let readable = ModKind::of_detect(d.kind) == Some(kind) || (kind == ModKind::Ember && crate::engines::ember_library(folder).is_some());
    if !readable {
        return Err(unreadable(&d.reason));
    }
    // a name or id that only comes from the folder's name (`<version>+<hash8>`) is not the manifest's: the old ones stay
    let folder_name = file_name(folder);
    let from_folder = |v: &Option<String>| v.as_deref().is_none_or(|v| v == folder_name || v == id_from_name(&folder_name));
    let explicit_id = match kind {
        ModKind::Kiln | ModKind::Native | ModKind::Emf => d.id.clone(),
        ModKind::Plugin | ModKind::Xhl | ModKind::Ember => None,
        ModKind::ShroudForge => Some(crate::shroudforge::read_mod(folder).map_err(|e| unreadable(&e.message))?.id),
        _ => crate::engine_mods::manifest_json(folder).and_then(|m| m.get("id").and_then(|v| v.as_str()).map(str::to_owned)),
    };
    let known_id = rec.manifest_id.clone().unwrap_or_else(|| rec.id.clone());
    if let Some(id) = explicit_id.filter(|id| *id != known_id) {
        return Err(Error::new(ErrorCode::Refused, format!(
            "{shown} was changed in its folder ({}), and its manifest now gives the id {id:?} instead of {known_id:?}: to Kiln that is another mod. \
             Put the old id back to keep it as this mod, or add the folder as a new mod.", folder.display())));
    }
    let version = match kind {
        ModKind::Kiln | ModKind::Native | ModKind::Emf => {
            let v = d.version.clone().unwrap_or_default();
            if semverish(&v) { v.trim().to_string() } else { fallback_version(&v) }
        }
        ModKind::Plugin => rec.version.clone(),
        _ if kind.is_engine() && kind != ModKind::Xhl => crate::engine_mods::engine_version(d.version.as_deref().unwrap_or_default()),
        _ => rec.version.clone(),
    };
    let name = match kind {
        ModKind::ShroudForge => crate::shroudforge::read_mod(folder).ok().map(|m| m.name),
        ModKind::Plugin | ModKind::Xhl => None,
        _ => d.name.clone().filter(|_| !from_folder(&d.name)),
    }.or_else(|| rec.name.clone());
    let (side, clients) = match kind {
        ModKind::Kiln => crate::manifest::read_kiln(folder).map(|m| (Some(m.side), Some(m.clients))).map_err(|e| unreadable(&e.message))?,
        ModKind::Native => crate::manifest::read_native(folder).map(|m| (Some(m.side), Some(m.clients.unwrap_or(Clients::default_for(m.side)))))
            .map_err(|e| unreadable(&e.message))?,
        k if k.is_engine() => {
            let (s, c) = crate::engine_mods::clients_of(k, folder);
            (Some(s), Some(c))
        }
        _ => (rec.side, rec.clients),
    };
    Ok(ImportRecord { version, hash, imported: crate::util::now_rfc3339(), name, side, clients, ..rec.clone() })
}

/// The entry that [`take_over`] made of a changed one.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TakenOver {
    /// The entry as it was recorded.
    pub from: LibraryRef,
    /// The entry its changed files are now (`from` again when they were not changed).
    pub to: LibraryRef,
    /// Its name as its manifest gives it now.
    pub name: String,
    /// Something the player should know: the folder changed again while it was taken over, so it was kept.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub note: Option<String>,
}

/// Has the entry at `folder` changed since its record was written? Cheap enough for every list: the entry is hashed only
/// when its manifest (or a plugin's DLL), or the folder itself, is newer than its record, and the answer is remembered
/// while those stay as they are. An edit of a file deeper in the folder that touches none of them is not seen here;
/// Play still refuses the entry, with the same way out ([`changed_error`]).
pub fn changed_on_disk(folder: &Path, rec: &ImportRecord) -> bool {
    use crate::filecache::FileKey;
    static SEEN: std::sync::Mutex<Option<std::collections::HashMap<PathBuf, (Vec<Option<FileKey>>, bool)>>> = std::sync::Mutex::new(None);
    let Some(recorded) = FileKey::of(&record_path(folder)) else { return false };
    let mut files: Vec<PathBuf> = vec![folder.to_path_buf()];
    match (rec.kind, rec.file.as_deref()) {
        (ModKind::Plugin, Some(f)) => files.push(folder.join(f)),
        _ => files.extend(MOD_MANIFESTS.iter().chain(&["shroudtopia.json", "src/mod.lua"]).map(|m| folder.join(m))),
    }
    let keys: Vec<Option<FileKey>> = std::iter::once(Some(recorded)).chain(files.iter().map(|f| FileKey::of(f))).collect();
    if !keys[1..].iter().flatten().any(|k| k.mtime > recorded.mtime) {
        return false;
    }
    if let Some((k, changed)) = SEEN.lock().ok().and_then(|s| s.as_ref().and_then(|m| m.get(folder).cloned())) && k == keys {
        return changed;
    }
    let changed = recorded_hash(folder, rec).is_ok_and(|h| h != rec.hash);
    if let Ok(mut s) = SEEN.lock() {
        s.get_or_insert_with(Default::default).insert(folder.to_path_buf(), (keys, changed));
    }
    changed
}

/// The error of a Library entry whose files are not the ones its record names (Play, activation, a placement): which
/// mod was changed in its folder, in words, and in `detail.libraryChanged` the entry to give [`take_over`]
/// ("Use the changed files", then try again).
pub fn changed_error(r: &LibraryRef, folder: &Path) -> Error {
    let name = crate::util::read_json::<ImportRecord>(&record_path(folder)).ok().flatten().and_then(|rec| rec.name)
        .filter(|n| !n.trim().is_empty()).unwrap_or_else(|| r.id.clone());
    Error::new(ErrorCode::HashMismatch, format!(
        "{name} was changed in its folder ({}): its files are not the ones Kiln imported, so Kiln does not load it as it is. \
         Use the changed files (Kiln takes them over as the mod's new files), or put the old files back.", folder.display()))
        .with_detail(serde_json::json!({ "libraryChanged": r, "name": name, "folder": folder.display().to_string() }))
}

/// Takes over a Library entry whose files were changed in its folder (a player edited its `mod.json`): the folder must
/// still be a mod of its kind Kiln can read, with the same id. Its files get their new hash and move to their own entry
/// (`<version>+<hash8>`, the version its manifest gives now) with a new record (name, version, side read from the
/// manifest again), every profile that listed the old entry lists the new one, and the move is kept as
/// [`migrate_identities`] keeps one ([`moved_to`]). Refused (GameRunning) while a game, or a server that may read the
/// entry, runs; Busy while a cook of it runs. Files that were not changed are taken over as they are (`to == from`).
/// The player's choice, never made on its own: the launcher offers it ("Use the changed files").
pub fn take_over(dirs: &DataDirs, r: &LibraryRef) -> Result<TakenOver> {
    let mut finder = crate::proc::Finder::default();
    take_over_with(dirs, r, &mut |dir, exe| finder.find(dir, exe))
}

/// [`take_over`] with the process lookup given (`find(game folder, exe name)`), for tests.
pub fn take_over_with(dirs: &DataDirs, r: &LibraryRef, find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>)
                      -> Result<TakenOver> {
    let folder = Guard::folder(dirs, r)?;
    let rec = record(dirs, r)?;
    let shown = |rec: &ImportRecord| rec.name.clone().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| rec.id.clone());
    Guard::read(dirs, &mut *find)?.refuse_while_running(dirs, r)?;
    // a cook of a Kiln mod in its Library folder writes there, and is why it may read as changed right now
    let _cook = if r.kind == ModKind::Kiln {
        Some(crate::state::lock_cook_folder(&folder, std::time::Duration::ZERO, &|| false,
            &format!("Kiln is cooking {} right now: wait until that is done, then try again.", shown(&rec)))?)
    } else { None };
    let hash = recorded_hash(&folder, &rec)?;
    if hash == rec.hash {
        return Ok(TakenOver { from: r.clone(), to: r.clone(), name: shown(&rec), note: None });
    }
    let to_rec = record_of_changed(&folder, &rec, hash)?;
    let to = LibraryRef { kind: r.kind, id: to_rec.id.clone(), version: to_rec.version.clone(), hash: to_rec.hash.clone() };
    let name = shown(&to_rec);
    let Some(left) = move_entry(dirs, &folder, &rec, to_rec, None, &mut *find)? else {
        return Err(Error::new(ErrorCode::Busy, format!("{name}: its new Library entry is in use right now (a game, a server or a cook reads it). Try again when it is done.")));
    };
    let note = match left {
        Left::Nothing => None,
        Left::ChangedAgain => Some(format!("{name}'s folder changed again while Kiln took it over: the newer files stay in {}, and it reads as changed again. Use the changed files once more to take those too.",
            folder.display())),
        Left::Clash(why) => Some(format!("{name} uses the changed files now, but its old folder was kept: {why}")),
        Left::Stays(why) => Some(format!("{name} uses the changed files now, but its old folder could not be removed ({why}): it stays in {}, and Kiln removes it the next time it loads a profile into a game once nothing holds it open.",
            folder.display())),
        Left::Rest(rest) => Some(format!("{name} uses the changed files now. Kiln could not delete all of its old folder: what is left is in {}, and Kiln removes it the next time it loads a profile into a game (or delete it yourself).",
            rest.display())),
    };
    Ok(TakenOver { from: r.clone(), to, name, note })
}

fn source_of(path: &Path, kind: &str) -> ImportSource {
    ImportSource { kind: kind.into(), from: path.display().to_string() }
}

fn semverish(v: &str) -> bool {
    crate::semver::SemVersion::parse(v).is_ok()
}

/// Keep legacy fallback identities when valid; a numeric slug like "001" needs a nonnumeric prefix.
pub(crate) fn fallback_version(version: &str) -> String {
    let slug = id_from_name(version);
    let legacy = format!("0.0.0-{slug}");
    if semverish(&legacy) { legacy } else { format!("0.0.0-import-{slug}") }
}

/// Imports one detected item (folder or DLL) into the library.
fn import_item(dirs: &DataDirs, d: &Detected, src: &Path, from: &ImportSource, report: &mut ImportReport) -> Result<()> {
    let staging_parent = dirs.library();
    std::fs::create_dir_all(&staging_parent).map_err(io(&staging_parent, "creating"))?;
    let now = crate::util::now_rfc3339();
    let skip = |report: &mut ImportReport, why: String| report.skipped.push(Skipped { path: d.path.clone(), reason: why });
    match d.kind {
        DetectKind::Kiln | DetectKind::Native | DetectKind::Emf if src.is_dir() => {
            let kind = match d.kind {
                DetectKind::Kiln => ModKind::Kiln,
                DetectKind::Native => ModKind::Native,
                _ => ModKind::Emf,
            };
            let (id, version) = (d.id.clone().unwrap_or_default(), d.version.clone().unwrap_or_default());
            // Kiln's own kinds keep the strict id: their manifests are Kiln's contract (engine mods get a Kiln id made from
            // theirs, engine_mods::import_engine_mod)
            if !crate::manifest::is_mod_id(&id) {
                let file = match kind { ModKind::Kiln => "kiln.mod.json", ModKind::Native => "native.json", _ => "mod.json" };
                skip(report, format!("{file} gives the id {id:?}, which is not a Kiln mod id (lower case letters, digits, . _ -, at most 64): the author has to change it"));
                return Ok(());
            }
            let tmp = TempDir::new(&staging_parent, "import")?;
            let work = tmp.path().join("m");
            // A Kiln mod is known by its identity, which leaves its cook outputs (assets/cooked) out. No hash covers
            // them, so they are not imported either: a file there is made on this PC by a cook, never taken from a
            // folder or an archive (a modpack that carries one is refused: modpack::verify).
            let (size, cooked_left_out) = if kind == ModKind::Kiln { copy_kiln_mod_tree(src, &work)? } else { (copy_mod_tree(src, &work)?, false) };
            if cooked_left_out {
                report.note(&d.path, format!("{}: its cooked files (assets/cooked) were left out. They are made on each PC from its own game, never shipped: cook the mod on this one (kiln cook).",
                    d.name.clone().or_else(|| d.id.clone()).unwrap_or_else(|| d.path.clone())));
            }
            let hash = crate::hash::of_mod(kind, &work)?;
            let source_hash = crate::hash::of_mod(kind, src)?;
            if hash != source_hash {
                return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while it was imported", src.display())));
            }
            let (side, clients) = match kind {
                ModKind::Kiln => crate::manifest::read_kiln(&work).map(|m| (Some(m.side), Some(m.clients))).unwrap_or((None, None)),
                ModKind::Native => crate::manifest::read_native(&work)
                    .map(|m| (Some(m.side), Some(m.clients.unwrap_or(Clients::default_for(m.side)))))
                    .unwrap_or((None, None)),
                _ => (None, None),
            };
            let version = if semverish(&version) { version.trim().to_string() } else { fallback_version(&version) };
            let rec = ImportRecord { format: 1, kind, id, version, hash, imported: now, source: Some(from.clone()), size, name: d.name.clone(), side, clients, file: None, manifest_id: None, game_folder: None };
            commit(dirs, &work, rec, report)
        }
        DetectKind::Plugin => {
            let tmp = TempDir::new(&staging_parent, "import")?;
            let work = tmp.path().join("m");
            std::fs::create_dir_all(&work).map_err(io(&work, "creating"))?;
            let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "plugin.dll".into());
            let size = std::fs::copy(src, work.join(&file)).map_err(io(src, "copying"))?;
            let (hash, _) = crate::hash::sha256_file(&work.join(&file))?;
            let id = d.id.clone().unwrap_or_else(|| id_from_name(&file));
            let rec = ImportRecord { format: 1, kind: ModKind::Plugin, id, version: d.version.clone().unwrap_or("0.0.0".into()), hash, imported: now,
                source: Some(from.clone()), size, name: Some(file.clone()), side: None, clients: None, file: Some(file), manifest_id: None,
                game_folder: None };
            commit(dirs, &work, rec, report)
        }
        DetectKind::BareDll if src.is_dir() => {
            // a plain DLL mod with the files it reads: the folder is one native mod, its DLL listed in a generated native.json
            let dlls = survey_dlls(src);
            let Some(dll) = package_dll(src, &dlls) else {
                skip(report, "no single DLL mod in this folder".into());
                return Ok(());
            };
            let file = file_name(&dll);
            let proxy = std::fs::read(&dll).ok().and_then(|b| crate::pe::parse(&b).ok()).is_some_and(|i| i.exports.iter().any(|x| x == crate::pe::DINPUT8_EXPORT));
            let tmp = TempDir::new(&staging_parent, "import")?;
            let work = tmp.path().join("m");
            let mut size = copy_mod_tree(src, &work)?;
            if crate::hash::dirhash(&work)? != crate::hash::dirhash(src)? {
                return Err(Error::new(ErrorCode::HashMismatch, "It changed while it was imported."));
            }
            let id = d.id.clone().unwrap_or_else(|| id_from_name(&file));
            let name = d.name.clone().unwrap_or_else(|| file.clone());
            if has_package_data(src, &dll) {
                // Kiln does not put the files into the game folder: say where they are, so a mod that looks for them
                // there is not a mystery
                report.note(&d.path, format!("{name} is a DLL mod with files: Kiln loads {file} from its own folder, with those files beside it"));
            }
            let dlls = package_chain(&work, &file, report, &d.path);
            // the DLL's own file version when it carries one, so the Library and quick::merge can tell builds apart
            let version = std::fs::read(work.join(&file)).ok().and_then(|b| dll_file_version(&b)).unwrap_or_else(|| "0.0.0".into());
            let manifest = serde_json::json!({
                "format": 1, "id": id, "name": name, "version": version,
                "description": format!("Generated by Kiln from {file} and the files next to it{}", if proxy { " (a dinput8 proxy, chain-loaded in DllMain)" } else { "" }),
                "side": "client", "dlls": dlls, "phase": if proxy { "dllmain" } else { "early" },
            });
            let mut body = serde_json::to_string_pretty(&manifest)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(crate::manifest::NATIVE_MANIFEST), body).map_err(io(&work, "writing"))?;
            let hash = crate::hash::dirhash(&work)?;
            let rec = ImportRecord { format: 1, kind: ModKind::Native, id, version, hash, imported: now, source: Some(from.clone()), size,
                name: Some(name), side: Some(Side::Client), clients: Some(Clients::Mod), file: None, manifest_id: None, game_folder: None };
            commit(dirs, &work, rec, report)
        }
        DetectKind::BareDll | DetectKind::Emf => {
            // a bare DLL becomes a mod folder: native.json (phase early, or dllmain for a proxy) or an EMF mod.json
            let tmp = TempDir::new(&staging_parent, "import")?;
            let work = tmp.path().join("m");
            std::fs::create_dir_all(&work).map_err(io(&work, "creating"))?;
            let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "mod.dll".into());
            let bundled = d.kind == DetectKind::BareDll && d.name.as_deref() != Some(file.as_str())
                && src.parent().is_some_and(|p| native_bundle_proxy(p).as_deref() == Some(src));
            let mut size = if bundled {
                let parent = src.parent().expect("bundle parent");
                let mut size = 0;
                for e in std::fs::read_dir(parent).map_err(io(parent, "reading"))? {
                    let path = e?.path();
                    if !native_companion(&path, src) { continue; }
                    if std::fs::symlink_metadata(&path)?.file_type().is_symlink() {
                        return Err(Error::new(ErrorCode::Refused, "links are not allowed in a native package"));
                    }
                    let to = work.join(file_name(&path));
                    if path.is_dir() {
                        size += copy_mod_tree(&path, &to)?;
                        if crate::hash::dirhash(&path)? != crate::hash::dirhash(&to)? {
                            return Err(Error::new(ErrorCode::HashMismatch, "native companion changed while importing"));
                        }
                    } else if path.is_file() {
                        let before = crate::hash::sha256_file(&path)?.0;
                        size += std::fs::copy(&path, &to).map_err(io(&path, "copying"))?;
                        if crate::hash::sha256_file(&to)?.0 != before || crate::hash::sha256_file(&path)?.0 != before {
                            return Err(Error::new(ErrorCode::HashMismatch, "native companion changed while importing"));
                        }
                    } else {
                        return Err(Error::new(ErrorCode::Refused, "native companions must be regular files"));
                    }
                }
                for folder in native_settings_dirs(parent) {
                    let to = work.join("mods").join(file_name(&folder));
                    size += copy_mod_tree(&folder, &to)?;
                    if crate::hash::dirhash(&folder)? != crate::hash::dirhash(&to)? {
                        return Err(Error::new(ErrorCode::HashMismatch, "native settings changed while importing"));
                    }
                    report.note(&d.path, format!("{}: settings are placed in the game's mods/{} folder while this native mod is enabled", file_name(&folder), file_name(&folder)));
                }
                size
            } else {
                std::fs::copy(src, work.join(&file)).map_err(io(src, "copying"))?
            };
            let id = d.id.clone().unwrap_or_else(|| id_from_name(&file));
            // the file name, or the package's name for a package's own proxy DLL (detect_folder_at)
            let shown = d.name.clone().unwrap_or_else(|| file.clone());
            let version = if d.kind == DetectKind::Emf { "0.0.0".to_string() } else {
                std::fs::read(work.join(&file)).ok().and_then(|b| dll_file_version(&b)).unwrap_or_else(|| "0.0.0".into())
            };
            let (kind, manifest_name, text) = if d.kind == DetectKind::Emf {
                (ModKind::Emf, EMF_MANIFEST, serde_json::json!({
                    "id": id, "name": file, "version": "0.0.0", "emf": 21, "dll": file,
                    "description": format!("Generated by Kiln from {file}"),
                }))
            } else {
                let proxy = std::fs::read(src).ok().and_then(|b| crate::pe::parse(&b).ok()).is_some_and(|i| i.exports.iter().any(|x| x == crate::pe::DINPUT8_EXPORT));
                let dlls = package_chain(&work, &file, report, &d.path);
                if bundled && has_package_data(&work, &work.join(&file)) {
                    report.note(&d.path, format!("{shown}: native companion files stay beside {file}; DLLs from the package's dinput8-chain.ini are loaded by Kiln, without changing the game's chain file"));
                }
                (ModKind::Native, crate::manifest::NATIVE_MANIFEST, serde_json::json!({
                    "format": 1, "id": id, "name": shown, "version": version,
                    "description": format!("Generated by Kiln from {file}{}", if proxy { " (a dinput8 proxy, chain-loaded in DllMain)" } else { "" }),
                    "side": "client", "dlls": dlls, "phase": if proxy { "dllmain" } else { "early" },
                }))
            };
            let mut body = serde_json::to_string_pretty(&text)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(manifest_name), body).map_err(io(&work, "writing"))?;
            let hash = crate::hash::dirhash(&work)?;
            let rec = ImportRecord { format: 1, kind, id, version, hash, imported: now, source: Some(from.clone()), size,
                name: Some(if kind == ModKind::Native { shown } else { file }), side: Some(Side::Client), clients: Some(Clients::Mod), file: None,
                manifest_id: None, game_folder: None };
            commit(dirs, &work, rec, report)
        }
        DetectKind::Runtime => {
            skip(report, "this is a Kiln runtime, not a mod: add it on the Runtime page".into());
            Ok(())
        }
        // another engine's loader or a tool: information when Kiln has no use for it, else what the player does instead
        DetectKind::Proxy => {
            if d.engine.is_some_and(crate::engines::loader_is_note) {
                report.note(&d.path, d.reason.clone());
            } else {
                skip(report, d.reason.clone());
            }
            Ok(())
        }
        k if k.is_engine() => match crate::engines::loader_for(k) {
            Some(l) => (l.import)(&mut crate::engines::ImportCtx { dirs, detected: d, src, from, report }),
            None => {
                skip(report, d.reason.clone());
                Ok(())
            }
        },
        _ => {
            skip(report, d.reason.clone());
            Ok(())
        }
    }
}

fn import_detected(dirs: &DataDirs, d: &Detected, base_label: &Path, base_real: &Path, from: &ImportSource, report: &mut ImportReport,
                   progress: &mut dyn Progress) -> Result<()> {
    if progress.canceled() {
        return Err(Error::new(ErrorCode::Canceled, "canceled"));
    }
    // map the label (may be "<zip>/<entry>") back to the real path
    let real = match Path::new(&d.path).strip_prefix(base_label) {
        Ok(rest) if rest.as_os_str().is_empty() => base_real.to_path_buf(),
        Ok(rest) => base_real.join(rest),
        Err(_) => PathBuf::from(&d.path),
    };
    if !d.items.is_empty() {
        for i in &d.items {
            import_detected(dirs, i, base_label, base_real, from, report, progress)?;
        }
        return Ok(());
    }
    progress.report(None, &format!("importing {}", d.path));
    if let Err(e) = import_item(dirs, d, &real, from, report) {
        report.skipped.push(Skipped { path: d.path.clone(), reason: e.message });
    }
    Ok(())
}

pub fn import(dirs: &DataDirs, paths: &[PathBuf], progress: &mut dyn Progress) -> Result<ImportReport> {
    let mut report = ImportReport::default();
    for p in paths {
        if is_archive(p) && p.is_file() {
            std::fs::create_dir_all(dirs.library()).map_err(io(&dirs.library(), "creating"))?;
            progress.report(None, &format!("unpacking {}", file_name(p)));
            // the archive's digest, taken before it is unpacked: what update checks compare a release with
            let digest = crate::hash::sha256_file(p).ok().map(|x| x.0);
            let tmp = match unpack_to_temp(p, &dirs.library()) {
                Ok(t) => t,
                // an archive Kiln can't open (a rar without a tool, a password, a damaged file) is left out with
                // its reason, and the other paths still go
                Err(e) if matches!(e.code, ErrorCode::Unsupported | ErrorCode::Parse) => {
                    report.skipped.push(Skipped { path: p.display().to_string(), reason: e.message });
                    continue;
                }
                Err(e) => return Err(e),
            };
            let mut d = detect_folder_at(tmp.path(), 0, archive_name(p).as_deref());
            relabel(&mut d, tmp.path(), p);
            let from = source_of(p, if d.kind == DetectKind::Modpack { "modpack" } else { "file" });
            if d.kind == DetectKind::Unknown && d.items.is_empty() {
                report.skipped.push(Skipped { path: d.path.clone(), reason: d.reason.clone() });
                continue;
            }
            let (imported, existing) = (report.imported.len(), report.existing.len());
            import_detected(dirs, &d, p, tmp.path(), &from, &mut report, progress)?;
            // Update checks: every entry this archive gave is bound to the archive's digest (updates::receipt). A
            // modpack is not the archive a mod was released as. The import stands whether or not this can be written.
            if d.kind != DetectKind::Modpack
                && let Some(sha256) = &digest
            {
                let refs: Vec<LibraryRef> = report.imported[imported..].iter().chain(&report.existing[existing..]).cloned().collect();
                let _ = crate::updates::receipt::capture_archive(dirs, &refs, sha256);
            }
        } else {
            let d = detect(p)?;
            let from = source_of(p, if p.is_dir() { "folder" } else { "file" });
            if d.kind == DetectKind::Unknown && d.items.is_empty() {
                report.skipped.push(Skipped { path: d.path.clone(), reason: d.reason.clone() });
                continue;
            }
            import_detected(dirs, &d, p, p, &from, &mut report, progress)?;
        }
    }
    progress.report(Some(1.0), &format!("{} imported, {} already there, {} skipped", report.imported.len(), report.existing.len(), report.skipped.len()));
    Ok(report)
}

fn records(dirs: &DataDirs, kind: ModKind) -> Vec<(PathBuf, ImportRecord)> {
    let mut out = Vec::new();
    for idd in std::fs::read_dir(kind_dir(dirs, kind)).into_iter().flatten().flatten() {
        for f in std::fs::read_dir(idd.path()).into_iter().flatten().flatten() {
            let p = f.path();
            if p.extension().is_some_and(|x| x == "json")
                && let Ok(Some(rec)) = crate::util::read_json::<ImportRecord>(&p)
            {
                let folder = p.with_extension("");
                if folder.is_dir() {
                    out.push((folder, rec));
                }
            }
        }
    }
    out
}

fn read_manifest_json(kind: ModKind, folder: &Path) -> Option<serde_json::Value> {
    let file = match kind {
        ModKind::Kiln => crate::manifest::KILN_MANIFEST,
        ModKind::Native => crate::manifest::NATIVE_MANIFEST,
        ModKind::Emf => EMF_MANIFEST,
        ModKind::Plugin => return None,
        _ => return crate::engine_mods::manifest_json(folder),
    };
    crate::jsonc::parse(&crate::manifest::read_text(&folder.join(file)).ok()?).ok()
}

pub fn list(dirs: &DataDirs, kind: Option<ModKind>) -> Result<Vec<LibraryEntry>> {
    let kinds: Vec<ModKind> = match kind {
        Some(k) => vec![k],
        None => ModKind::ALL.to_vec(),
    };
    let mut out: Vec<LibraryEntry> = Vec::new();
    for k in kinds {
        let mut by_id: std::collections::BTreeMap<String, Vec<(PathBuf, ImportRecord)>> = Default::default();
        for (folder, rec) in records(dirs, k) {
            // what a take-over left behind while something still read it: a copy of its files is the new entry, and it
            // goes at the next activation (migrate_identities); not a version to list
            if taken_over_leftover(&folder, &rec) {
                continue;
            }
            by_id.entry(rec.id.clone()).or_default().push((folder, rec));
        }
        for (id, mut versions) in by_id {
            versions.sort_by(|a, b| crate::bundle::compare_versions(&b.1.version, &a.1.version).then_with(|| b.1.imported.cmp(&a.1.imported)));
            let name = versions.first().and_then(|v| v.1.name.clone()).unwrap_or_else(|| id.clone());
            out.push(LibraryEntry {
                kind: k,
                id,
                name,
                versions: versions
                    .into_iter()
                    .map(|(folder, r)| LibraryVersion {
                        changed: changed_on_disk(&folder, &r),
                        manifest: read_manifest_json(k, &folder),
                        manifest_id: r.manifest_id,
                        version: r.version,
                        hash: r.hash,
                        folder,
                        imported: r.imported,
                        side: r.side,
                        clients: r.clients,
                        size: r.size,
                        source: r.source,
                    })
                    .collect(),
            });
        }
    }
    Ok(out)
}

/// The import record of a library entry.
pub fn record(dirs: &DataDirs, r: &LibraryRef) -> Result<ImportRecord> {
    let folder = checked_entry(dirs, r)?;
    crate::util::read_json::<ImportRecord>(&record_path(&folder))?
        .filter(|rec| rec.format == 1 && rec.kind == r.kind && rec.id == r.id && rec.version == r.version && rec.hash == r.hash)
        .ok_or_else(|| Error::not_found(format!("{} {} {} is not in the library", r.kind.as_str(), r.id, r.version)))
}

/// What lists library versions: the launcher's profiles, and the profile files the known games hold now (the game
/// reads those, not the profiles: `<game>/kiln/profile.json` is the active one, `profile.once.json` a queued start,
/// counted only while `kiln/play-once.ini` still asks for it: [`crate::playonce::reads_once_profile`]).
struct Usage {
    profiles: Vec<(String, crate::profile::ProfileMods)>,
    games: Vec<ActiveFile>,
}

struct ActiveFile {
    /// The game folder's name, for `<profile> (active in <game>)`.
    game: String,
    once: bool,
    /// The profile id the file was written from.
    id: String,
    entries: Vec<Listed>,
}

/// One mod entry of a game's profile file, its game-side path resolved to the host once (canonical form; lower case on
/// Windows), so `unused` does not resolve every entry again for every library version.
struct Listed {
    kind: ModKind,
    id: Option<String>,
    version: Option<String>,
    hash: Option<String>,
    host: Option<PathBuf>,
}

impl Usage {
    fn read(dirs: &DataDirs) -> Usage {
        let mut profiles = Vec::new();
        for e in std::fs::read_dir(dirs.profiles()).into_iter().flatten().flatten() {
            let Ok(Some(p)) = crate::util::read_json::<crate::profile::Profile>(&e.path().join("meta.json")) else { continue };
            profiles.push((p.id, p.mods));
        }
        let mut games = Vec::new();
        let now = crate::util::now_unix();
        for install in crate::game::known(dirs).unwrap_or_default() {
            // the once file stays after its start: it lists nothing a start can read without a flag asking for it
            let server = install.kind == crate::game::GameKind::Server;
            let once_live = crate::playonce::reads_once_profile(&install.path, server, now);
            for (once, rel) in [(false, crate::paths::game::PROFILE), (true, crate::paths::game::PROFILE_ONCE)] {
                if once && !once_live {
                    continue;
                }
                // an unreadable file lists nothing we can name; the running check still protects a live game
                if let Ok(Some(file)) = crate::util::read_json::<crate::profile::ProfileFile>(&install.path.join(rel)) {
                    games.push(ActiveFile::of(&install, once, &file));
                }
            }
        }
        Usage { profiles, games }
    }

    /// Profiles (their ids) that list the version, then `<profile> (active in <game>)` / `(play-once in <game>)` for
    /// each game whose profile file points at it without that profile (still) listing it.
    fn users(&self, dirs: &DataDirs, r: &LibraryRef) -> Vec<String> {
        let mut out: Vec<String> = self.profiles.iter()
            .filter(|(_, mods)| mods.list(r.kind).iter().any(|m| m.id == r.id && m.version == r.version && m.hash == r.hash))
            .map(|(id, _)| id.clone())
            .collect();
        let entry = checked_entry(dirs, r).ok().map(|e| host_key(&e));
        for g in &self.games {
            if out.iter().any(|u| *u == g.id) || !g.lists(r, entry.as_deref()) {
                continue;
            }
            out.push(format!("{} ({} in {})", g.id, if g.once { "play-once" } else { "active" }, g.game));
        }
        out.sort();
        out.dedup();
        out
    }
}

/// A host path as compared here: canonical (symlinks such as `/home -> /var/home` resolved), lower case on Windows.
fn host_key(path: &Path) -> PathBuf {
    let c = crate::steam::canon(path);
    if cfg!(windows) { PathBuf::from(c.to_string_lossy().to_lowercase()) } else { c }
}

impl ActiveFile {
    fn of(install: &crate::game::GameInstall, once: bool, file: &crate::profile::ProfileFile) -> ActiveFile {
        let host = |game_path: &str| crate::winpath::to_host(game_path, install).ok().map(|h| host_key(&h));
        let mut entries = Vec::new();
        for k in ModKind::ALL {
            match file.mods.folders(k) {
                Some(list) => entries.extend(list.iter().map(|m| Listed {
                    kind: k, id: Some(m.id.clone()), version: m.version.clone(), hash: m.hash.clone(), host: host(&m.path),
                })),
                None => entries.extend(file.mods.plugin.iter().map(|m| Listed {
                    kind: k, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone(), host: host(&m.path),
                })),
            }
        }
        let game = install.path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| install.id.clone());
        ActiveFile { game, once, id: file.id.clone(), entries }
    }

    /// Does this profile file list the library version? By id + version + hash when the entry carries them (Z and
    /// native paths do too), else by its game-side path (`Z:\...` or a Windows path) mapped back to the host and
    /// compared with the entry's folder (`entry`, a [`host_key`]; a plugin DLL lies inside it).
    fn lists(&self, r: &LibraryRef, entry: Option<&Path>) -> bool {
        self.entries.iter().filter(|m| m.kind == r.kind).any(|m| {
            (m.id.as_deref() == Some(r.id.as_str()) && m.version.as_deref() == Some(r.version.as_str()) && m.hash.as_deref() == Some(r.hash.as_str()))
                || entry.is_some_and(|e| m.host.as_deref().is_some_and(|h| h.starts_with(e)))
        })
    }
}

/// Profiles (their ids) that list this entry, and the games whose active or play-once profile file points at it
/// (`<profile> (active in <game folder>)`): the game reads those files, whatever the profile says now.
pub fn users(dirs: &DataDirs, r: &LibraryRef) -> Vec<String> {
    Usage::read(dirs).users(dirs, r)
}

/// Every version of the library that no profile and no known game's profile file lists: what "Clean up" may remove.
pub fn unused(dirs: &DataDirs) -> Result<Vec<LibraryRef>> {
    let usage = Usage::read(dirs);
    let mut out = Vec::new();
    for e in list(dirs, None)? {
        for v in e.versions {
            let r = LibraryRef { kind: e.kind, id: e.id.clone(), version: v.version, hash: v.hash };
            if usage.users(dirs, &r).is_empty() {
                out.push(r);
            }
        }
    }
    Ok(out)
}

/// A folder whose game or server process may be reading the library: the installs the launcher knows (clients and
/// servers) and its local servers (`<data>/servers/*.json`, as a server install), with the exe to look for first.
fn game_folders(dirs: &DataDirs) -> Result<Vec<crate::game::GameInstall>> {
    #[derive(Deserialize)]
    struct ServerFolder {
        #[serde(default)]
        dir: Option<PathBuf>,
        #[serde(default)]
        prefix: Option<PathBuf>,
    }
    let mut out: Vec<crate::game::GameInstall> = Vec::new();
    let mut add = |g: crate::game::GameInstall| {
        let c = crate::steam::canon(&g.path);
        if !out.iter().any(|d| crate::steam::same_path(&crate::steam::canon(&d.path), &c)) {
            out.push(g);
        }
    };
    for g in crate::game::known(dirs)? {
        add(g);
    }
    for e in std::fs::read_dir(dirs.root.join("servers")).into_iter().flatten().flatten() {
        if e.path().extension().is_some_and(|x| x == "json")
            && let Ok(Some(ServerFolder { dir: Some(dir), prefix })) = crate::util::read_json::<ServerFolder>(&e.path())
        {
            add(crate::game::GameInstall {
                id: format!("server:{}", dir.display()),
                kind: crate::game::GameKind::Server,
                path: dir,
                exe: crate::game::SERVER_EXE.into(),
                app_id: crate::SERVER_APP_ID,
                library: None,
                platform: if cfg!(windows) { crate::game::Platform::Windows } else { crate::game::Platform::Wine },
                prefix,
                build_id: None,
                exe_sha256: None,
                steam_flavor: None,
                path_mode: crate::game::PathMode::Copy,
                source: "server".into(),
            });
        }
    }
    Ok(out)
}

/// A game or server process running from a known folder, found once per removal (or batch of removals).
struct Running {
    folder: PathBuf,
    process: crate::proc::ProcessInfo,
    reader: Reader,
}

/// What a running process may read of the library.
enum Reader {
    /// A game client: a play-once start can point anywhere.
    Client,
    /// A dedicated server gets its mods as copies (`server::control::sync` into `<server>/kiln/mods`, engine folders
    /// via `engine_mods::place`), so it reads the library only through a profile file the launcher wrote for it
    /// (`profile::activate` works on any install): `kiln/profile.json` (chain.ini's DLL paths come from the same
    /// entries; servers ignore play-once, so never `profile.once.json`). Its entries' host paths (`None`: cannot be
    /// resolved); `None` when the file exists but cannot be read (it may point anywhere).
    Server(Option<Vec<Option<PathBuf>>>),
}

impl Running {
    /// Could this process be reading the library version whose folder is `entry` (a [`host_key`]; `None` when it is
    /// unknown)? A client always; a server when an entry of its profile file resolves into that folder, or into the
    /// library when the folder is unknown, or cannot be resolved at all.
    fn reads(&self, entry: Option<&Path>, library: &Path) -> bool {
        match &self.reader {
            Reader::Client | Reader::Server(None) => true,
            Reader::Server(Some(hosts)) => hosts.iter().any(|h| match h {
                None => true,
                Some(h) => h.starts_with(entry.unwrap_or(library)),
            }),
        }
    }
}

/// What a removal checks, read once for any number of removals: the processes running from the known game folders
/// (`profile::running_process_with`, the check activation uses) and what lists each version ([`Usage`]).
struct Guard {
    running: Vec<Running>,
    usage: Usage,
    library: PathBuf,
}

impl Guard {
    fn read(dirs: &DataDirs, find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>) -> Result<Guard> {
        let mut running = Vec::new();
        for install in game_folders(dirs)? {
            let Some(process) = crate::profile::running_process_with(&install.path, &install.exe, &mut *find)? else { continue };
            let reader = if process.name.to_ascii_lowercase().starts_with("enshrouded_serv") {
                Reader::Server(match crate::util::read_json::<crate::profile::ProfileFile>(&install.path.join(crate::paths::game::PROFILE)) {
                    Ok(Some(file)) => Some(ActiveFile::of(&install, false, &file).entries.into_iter().map(|m| m.host).collect()),
                    Ok(None) => Some(Vec::new()),
                    Err(_) => None,
                })
            } else {
                Reader::Client
            };
            running.push(Running { folder: install.path, process, reader });
        }
        Ok(Guard { running, usage: Usage::read(dirs), library: host_key(&dirs.library()) })
    }

    /// Refuses (GameRunning) while a game, or a server that may be reading this version, runs on this machine: a
    /// folder removed under it fails minutes into its start (EML reads a mod's `src/mod.lua` long after its
    /// `mod.json`). A server that runs all the time with synced copies does not block the library.
    fn refuse_while_running(&self, dirs: &DataDirs, r: &LibraryRef) -> Result<()> {
        let entry = checked_entry(dirs, r).ok().map(|e| host_key(&e));
        let Some(run) = self.running.iter().find(|run| run.reads(entry.as_deref(), &self.library)) else { return Ok(()) };
        let message = match run.reader {
            Reader::Server(_) => "Stop the Enshrouded server first: it may be reading this mod.",
            Reader::Client => "Close Enshrouded first: the game may be reading this mod.",
        };
        Err(Error::new(ErrorCode::GameRunning, message)
            .with_detail(serde_json::json!({ "pid": run.process.pid, "name": run.process.name, "folder": run.folder.display().to_string() })))
    }

    /// The version's folder, refused (NotFound) when it is not in the library.
    fn folder(dirs: &DataDirs, r: &LibraryRef) -> Result<PathBuf> {
        let folder = checked_entry(dirs, r)?;
        if !folder.is_dir() {
            return Err(Error::not_found(format!("{} {} {} is not in the library", r.kind.as_str(), r.id, r.version)));
        }
        Ok(folder)
    }

    /// Removes one version once the running check passed: refused (Conflict) while something lists it that `force`
    /// does not go past.
    /// The folder is moved aside first (a file in it that is open stops that, and nothing is changed then), its record
    /// goes next, and the moved folder last: a folder that cannot be deleted all the way is never an entry without its
    /// record. -> the moved folder when part of it is left (`*.removing-*`, removed by [`migrate_identities`]).
    fn remove_checked(&self, dirs: &DataDirs, r: &LibraryRef, folder: &Path, force: &Force) -> Result<Option<PathBuf>> {
        let used = self.usage.users(dirs, r);
        let past = match force {
            Force::No => used.is_empty(),
            Force::All => true,
            Force::Past(shown) => used.iter().all(|u| shown.contains(u)),
        };
        if !past {
            return Err(Error::new(ErrorCode::Conflict, format!("{} {} is used by profile(s) {}", r.id, r.version, used.join(", "))).with_detail(used));
        }
        let aside = crate::util::with_suffix(folder, &format!("{REMOVING}{}", crate::hash::hex(&crate::util::random_bytes::<4>()?)));
        std::fs::rename(folder, &aside).map_err(io(folder, "moving aside to remove"))?;
        if let Err(e) = std::fs::remove_file(record_path(folder)) {
            let _ = std::fs::rename(&aside, folder);
            return Err(io(folder, "removing the record of")(e));
        }
        let _ = std::fs::remove_file(taken_over_path(folder));
        let rest = std::fs::remove_dir_all(&aside).err().map(|_| aside);
        if let Some(parent) = folder.parent() {
            let _ = std::fs::remove_dir(parent);
        }
        Ok(rest)
    }
}

/// How far a removal may go past what lists the version ([`users`]).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Force {
    /// Refused (Conflict) while anything lists it.
    No,
    /// Past every user (`force = true`).
    All,
    /// Past exactly these users, as [`users`] named them (what a confirmation showed). Any other user is a Conflict.
    Past(Vec<String>),
}

impl From<bool> for Force {
    fn from(force: bool) -> Self { if force { Force::All } else { Force::No } }
}

/// Removes a version. Refuses (GameRunning) while a game runs, or a server whose `kiln/profile.json` may point at the
/// version, whatever `force` says. Refuses (Conflict) while a profile lists the version, or a known game's active
/// profile file (or a play-once file a start can still read) points at it, unless `force` goes past it (`true`: every
/// user; [`Force::Past`]: only the users named).
pub fn remove(dirs: &DataDirs, r: &LibraryRef, force: impl Into<Force>) -> Result<()> {
    let mut finder = crate::proc::Finder::default();
    remove_with(dirs, r, force, &mut |dir, exe| finder.find(dir, exe))
}

/// [`remove`] with the process lookup given (`find(game folder, exe name)`), for tests.
pub fn remove_with(
    dirs: &DataDirs,
    r: &LibraryRef,
    force: impl Into<Force>,
    find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>,
) -> Result<()> {
    let folder = Guard::folder(dirs, r)?;
    let guard = Guard::read(dirs, find)?;
    guard.refuse_while_running(dirs, r)?;
    guard.remove_checked(dirs, r, &folder, &force.into()).map(|_| ())
}

/// What [`remove_many`] did: the versions removed, and each one that was not with the reason (`Conflict` while
/// something lists it and `force` is off, `NotFound`, I/O).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoveReport {
    pub removed: Vec<LibraryRef>,
    pub failed: Vec<RemoveFailure>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RemoveFailure {
    pub entry: LibraryRef,
    pub error: Error,
}

/// Removes several versions ("Clean up") with one look at the running processes, the profiles and the games' profile
/// files, instead of one per version. Refuses (GameRunning) the whole batch, removing nothing, while a game runs or a
/// server may read one of them. Otherwise each version goes as [`remove`] would take it; the ones that cannot are
/// reported and the others still go.
pub fn remove_many(dirs: &DataDirs, refs: &[LibraryRef], force: bool) -> Result<RemoveReport> {
    let mut finder = crate::proc::Finder::default();
    remove_many_with(dirs, refs, force, &mut |dir, exe| finder.find(dir, exe))
}

/// [`remove_many`] with the process lookup given, for tests.
pub fn remove_many_with(
    dirs: &DataDirs,
    refs: &[LibraryRef],
    force: bool,
    find: &mut dyn FnMut(&Path, &str) -> Result<Option<crate::proc::ProcessInfo>>,
) -> Result<RemoveReport> {
    let guard = Guard::read(dirs, find)?;
    for r in refs {
        guard.refuse_while_running(dirs, r)?;
    }
    let mut report = RemoveReport::default();
    let force = Force::from(force);
    for r in refs {
        match Guard::folder(dirs, r).and_then(|folder| guard.remove_checked(dirs, r, &folder, &force).map(|_| ())) {
            Ok(()) => report.removed.push(r.clone()),
            Err(error) => report.failed.push(RemoveFailure { entry: r.clone(), error }),
        }
    }
    Ok(report)
}

/// The folder (or DLL file, for a plugin) of a library entry.
pub fn path_of(dirs: &DataDirs, r: &LibraryRef) -> Result<PathBuf> {
    let dir = checked_entry(dirs, r)?;
    if r.kind == ModKind::Plugin {
        let rec = record(dirs, r)?;
        let file = rec.file.ok_or_else(|| Error::new(ErrorCode::Schema, format!("plugin {} has no file in its record", r.id)))?;
        return plugin_file(&dir, &file);
    }
    Ok(dir)
}
