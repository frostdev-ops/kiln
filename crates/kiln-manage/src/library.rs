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
    let plain = |mut x: Detected| {
        if crate::engines::is_proxy_name(&file_name(path)) {
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
const MOD_MANIFESTS: [&str; 7] = ["mod.json", "kiln.mod.json", "native.json", "flame.json", "pack.toml", "init.lua", "extended.mod.json"];

/// Is one of `dir`'s folders a mod on its own?
fn has_mod_subfolder(dir: &Path) -> bool {
    std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path())
        .any(|p| p.is_dir() && !p.file_name().is_some_and(|n| n.to_string_lossy().starts_with('.')) && MOD_MANIFESTS.iter().any(|m| p.join(m).is_file()))
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
fn native_bundle_proxy(dir: &Path) -> Option<PathBuf> {
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
            && !MOD_MANIFESTS.iter().any(|m| path.join(m).is_file())
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
        let crate::chain::IniLine::Text(section, value) = line else { continue };
        if section.is_some_and(|s| s != "chain" && s != "emf") { continue; }
        let rel = value.replace('\\', "/");
        if crate::bundle::safe_rel_path(&rel).is_err() || !rel.to_ascii_lowercase().ends_with(".dll") {
            report.skipped.push(Skipped { path: label.into(), reason: format!("dinput8-chain.ini: unsafe DLL path {value:?} was not imported") });
            continue;
        }
        // A redundant bundled EML proxy is deliberately not present in this native folder: kiln-eml runs those mods.
        if !dir.join(&rel).is_file() {
            report.note(label, format!("dinput8-chain.ini: {value} is not in the native package (a bundled loader or a missing file); not chained"));
            continue;
        }
        if !dlls.iter().any(|p| p.eq_ignore_ascii_case(&rel)) { dlls.push(rel); }
    }
    dlls
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
        d.items = children(dir, depth);
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
    // A package with an unknown/broken manifest is one item, never a bag of helper DLLs to execute.
    if dir.join(EMF_MANIFEST).is_file() || dir.join("src/mod.lua").is_file()
        || std::fs::read_dir(dir).into_iter().flatten().flatten().any(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lua")))
    {
        return mk(DetectKind::Unknown, None, None, None, crate::shroudforge::skip_reason(dir));
    }
    // An XHL mod without an EML part: a plugin XHL-Core loads from the game's mods folder, never a DLL to load on its own
    if dir.join(crate::engine_mods::XHL_PLUGIN_INI).is_file() {
        return mk(DetectKind::Unknown, None, None, None, xhl_plugin_reason(&file_name(dir)));
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
            return mk(DetectKind::BareDll, Some(id_from_name(&shown)), Some("0.0.0".into()), Some(shown), reason);
        }
    }
    // a folder of mods (an unpacked zip): what is inside
    let mut items = children(dir, depth);
    // A package shaped like the game folder whose own DLL sits under a proxy name next to its `mods` folder (Auto Loot
    // Plus: dinput8.dll + mods/auto-loot-plus): the DLL is named after the package, not "dinput8.dll".
    if let (0, Some(n)) = (depth, top_name) {
        let mut top = items.iter_mut().filter(|i| i.kind == DetectKind::BareDll && Path::new(&i.path).parent() == Some(dir)
            && crate::engines::is_proxy_name(&file_name(Path::new(&i.path))));
        if let (Some(only), None) = (top.next(), top.next()) {
            only.id = Some(id_from_name(n));
            only.name = Some(n.to_string());
        }
    }
    if items.iter().any(|i| i.kind != DetectKind::Unknown) {
        let n = items.iter().filter(|i| i.kind.importable()).count();
        let other = items.iter().filter(|i| i.kind != DetectKind::Unknown && !i.kind.importable()).count();
        let reason = if other == 0 {
            format!("a folder with {n} importable item(s)")
        } else if n == 0 {
            // nothing Kiln can add: the first item says what it is and why
            items.iter().find(|i| i.kind != DetectKind::Unknown).map(|i| i.reason.clone()).unwrap_or_default()
        } else {
            format!("a folder with {n} importable item(s) and {other} Kiln can't load yet")
        };
        let mut d = mk(DetectKind::Unknown, None, None, None, reason);
        d.items = items;
        return d;
    }
    // nothing usable: an item with its own reason says it better than the package's generic one
    if let Some(i) = items.iter().find(|i| i.reason.contains(XHL_PLUGIN_WORDS) || i.reason.contains(SETTINGS_WORDS)) {
        return mk(DetectKind::Unknown, None, None, None, i.reason.clone());
    }
    mk(DetectKind::Unknown, None, None, None, crate::shroudforge::skip_reason(dir))
}

const XHL_PLUGIN_WORDS: &str = "is a plugin of XHL-Core";
const SETTINGS_WORDS: &str = "holds only settings";

/// A folder with files and nothing but settings files (`.txt`, `.ini`, `.cfg`, `.json`, `.toml`, `.yaml`) in it.
fn only_settings(dir: &Path) -> bool {
    let mut any = false;
    for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
        let low = e.file_name().to_string_lossy().to_ascii_lowercase();
        if low.starts_with('.') { continue; }
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

/// Why an XHL-Core plugin without an EML part is not imported, and what the player does instead.
fn xhl_plugin_reason(folder: &str) -> String {
    format!("{folder} {XHL_PLUGIN_WORDS} (native-plugin.ini without an EML mod): XHL-Core loads it from the game's mods folder, \
        which Kiln does not fill for plain plugins yet. Add XHL-Core to Kiln and copy the folder {folder} into the mods folder of the \
        Enshrouded folder yourself")
}

fn children(dir: &Path, depth: usize) -> Vec<Detected> {
    let mut entries: Vec<PathBuf> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).collect();
    entries.sort();
    let native = native_bundle_proxy(dir);
    let settings = if native.is_some() { native_settings_dirs(dir) } else { Vec::new() };
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
        if dir.file_name().is_some_and(|n| n.eq_ignore_ascii_case("mods"))
            && dir.parent().is_some_and(|parent| native_bundle_proxy(parent).is_some()
                && native_settings_dirs(parent).contains(&e)) {
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
                let shown = file_name(dir);
                d.id = Some(id_from_name(&shown));
                d.name = Some(shown);
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
    copy_mod_tree_inner(from, to)
}

fn copy_mod_tree_inner(from: &Path, to: &Path) -> Result<u64> {
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
        } else if ft.is_dir() {
            size += copy_mod_tree_inner(&e.path(), &dst)?;
        } else if ft.is_file() {
            size += std::fs::copy(e.path(), &dst).map_err(io(&dst, "copying to"))?;
        } else {
            return Err(Error::new(ErrorCode::Refused, format!("{}: not a regular file or folder", e.path().display())));
        }
    }
    Ok(size)
}

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
        crate::hash::dirhash(folder)
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
            let size = copy_mod_tree(src, &work)?;
            let hash = crate::hash::dirhash(&work)?;
            let source_hash = crate::hash::dirhash(src)?;
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
            let manifest = serde_json::json!({
                "format": 1, "id": id, "name": name, "version": "0.0.0",
                "description": format!("Generated by Kiln from {file} and the files next to it{}", if proxy { " (a dinput8 proxy, chain-loaded in DllMain)" } else { "" }),
                "side": "client", "dlls": dlls, "phase": if proxy { "dllmain" } else { "early" },
            });
            let mut body = serde_json::to_string_pretty(&manifest)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(crate::manifest::NATIVE_MANIFEST), body).map_err(io(&work, "writing"))?;
            let hash = crate::hash::dirhash(&work)?;
            let rec = ImportRecord { format: 1, kind: ModKind::Native, id, version: "0.0.0".into(), hash, imported: now, source: Some(from.clone()), size,
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
                    "format": 1, "id": id, "name": shown, "version": "0.0.0",
                    "description": format!("Generated by Kiln from {file}{}", if proxy { " (a dinput8 proxy, chain-loaded in DllMain)" } else { "" }),
                    "side": "client", "dlls": dlls, "phase": if proxy { "dllmain" } else { "early" },
                }))
            };
            let mut body = serde_json::to_string_pretty(&text)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(manifest_name), body).map_err(io(&work, "writing"))?;
            let hash = crate::hash::dirhash(&work)?;
            let rec = ImportRecord { format: 1, kind, id, version: "0.0.0".into(), hash, imported: now, source: Some(from.clone()), size,
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
            import_detected(dirs, &d, p, tmp.path(), &from, &mut report, progress)?;
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
    fn remove_checked(&self, dirs: &DataDirs, r: &LibraryRef, folder: &Path, force: &Force) -> Result<()> {
        let used = self.usage.users(dirs, r);
        let past = match force {
            Force::No => used.is_empty(),
            Force::All => true,
            Force::Past(shown) => used.iter().all(|u| shown.contains(u)),
        };
        if !past {
            return Err(Error::new(ErrorCode::Conflict, format!("{} {} is used by profile(s) {}", r.id, r.version, used.join(", "))).with_detail(used));
        }
        std::fs::remove_file(record_path(folder)).map_err(io(folder, "removing the record of"))?;
        std::fs::remove_dir_all(folder).map_err(io(folder, "removing"))?;
        if let Some(parent) = folder.parent() {
            let _ = std::fs::remove_dir(parent);
        }
        Ok(())
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
    guard.remove_checked(dirs, r, &folder, &force.into())
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
        match Guard::folder(dirs, r).and_then(|folder| guard.remove_checked(dirs, r, &folder, &force)) {
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
