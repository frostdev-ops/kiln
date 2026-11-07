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

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ImportReport {
    pub imported: Vec<LibraryRef>,
    /// Already in the library (same kind, id, version and hash).
    pub existing: Vec<LibraryRef>,
    pub skipped: Vec<Skipped>,
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
    if crate::pe::classify_bytes(&bytes).kind == crate::pe::ProxyKind::Kiln {
        return d(DetectKind::Unknown, "Kiln's own dinput8 proxy: install a runtime bundle instead");
    }
    if has(crate::pe::KILN_PLUGIN_EXPORT) {
        return d(DetectKind::Plugin, "a Kiln plugin (exports kiln_plugin_main)");
    }
    if has(crate::pe::EMF_MOD_EXPORT) {
        return d(DetectKind::Emf, "an EMF mod DLL (exports emf_mod_load); a mod.json is generated");
    }
    // another engine's mod DLL or loader: never imported as a plain DLL
    if let Some(e) = crate::engines::detect_dll(path, &bytes, &exports) {
        return Detected { id: Some(stem_id.clone()), version: Some("0.0.0".into()), ..e };
    }
    if has(crate::pe::DINPUT8_EXPORT) {
        let c = crate::pe::classify_bytes(&bytes);
        return match c.kind {
            crate::pe::ProxyKind::Kiln => Detected {
                kind: DetectKind::Unknown,
                reason: "Kiln's own dinput8 proxy: install a runtime bundle instead".into(),
                ..d(DetectKind::Unknown, "")
            },
            crate::pe::ProxyKind::ShroudForge => Detected {
                kind: DetectKind::Unknown,
                reason: "ShroudForge's mod loader: it cannot run next to Kiln".into(),
                ..d(DetectKind::Unknown, "")
            },
            _ => d(DetectKind::BareDll, "a dinput8 proxy (exports DirectInput8Create): a native mod with phase dllmain (chain-loaded)"),
        };
    }
    d(DetectKind::BareDll, "a plain DLL: a native mod with a generated native.json (phase early)")
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

fn detect_folder(dir: &Path) -> Detected {
    detect_folder_at(dir, 0)
}

fn detect_folder_at(dir: &Path, depth: usize) -> Detected {
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
            Err(e) => mk(DetectKind::Unknown, None, None, None, e.message),
        };
    }
    if dir.join(crate::manifest::NATIVE_MANIFEST).is_file() {
        return match crate::manifest::read_native(dir) {
            Ok(m) => mk(DetectKind::Native, Some(m.id.clone()), Some(m.version.clone()), m.name.clone().or(Some(m.id)), "a native mod (native.json)".into()),
            Err(e) => mk(DetectKind::Unknown, None, None, None, e.message),
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
    // a folder of mods (an unpacked zip): what is inside
    let items = children(dir, depth);
    if items.iter().any(|i| i.kind != DetectKind::Unknown) {
        let n = items.iter().filter(|i| i.kind.importable()).count();
        let other = items.iter().filter(|i| i.kind != DetectKind::Unknown && !i.kind.importable()).count();
        let reason = if other == 0 {
            format!("a folder with {n} importable item(s)")
        } else if n == 0 {
            // nothing Kiln can load: the first item says what it is and why
            items.iter().find(|i| i.kind != DetectKind::Unknown).map(|i| i.reason.clone()).unwrap_or_default()
        } else {
            format!("a folder with {n} importable item(s) and {other} Kiln can't load yet")
        };
        let mut d = mk(DetectKind::Unknown, None, None, None, reason);
        d.items = items;
        return d;
    }
    mk(DetectKind::Unknown, None, None, None, crate::shroudforge::skip_reason(dir))
}

fn children(dir: &Path, depth: usize) -> Vec<Detected> {
    let mut entries: Vec<PathBuf> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path()).collect();
    entries.sort();
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
        if e.is_dir() {
            let d = detect_folder_at(&e, depth + 1);
            if d.kind == DetectKind::Unknown && !d.items.is_empty() {
                out.extend(d.items);
            } else if d.kind != DetectKind::Unknown || name != "__MACOSX" {
                out.push(d);
            }
        } else if name.to_lowercase().ends_with(".dll") {
            out.push(detect_dll(&e));
        } else if let Some(d) = crate::engines::detect_file(&e) {
            out.push(d);
        }
    }
    out
}

pub fn detect(path: &Path) -> Result<Detected> {
    let meta = std::fs::symlink_metadata(path).map_err(io(path, "reading"))?;
    if meta.file_type().is_symlink() {
        return Ok(Detected { kind: DetectKind::Unknown, path: path.display().to_string(), id: None, version: None, name: None,
            reason: "links are not mod imports".into(), items: vec![], engine: None });
    }
    if meta.is_dir() {
        return Ok(detect_folder(path));
    }
    let lower = path.to_string_lossy().to_lowercase();
    if lower.ends_with(".dll") {
        return Ok(detect_dll(path));
    }
    if lower.ends_with(".zip") {
        let tmp = unpack_to_temp(path, &std::env::temp_dir())?;
        let mut d = detect_folder(tmp.path());
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
        reason: "not a folder, .zip or .dll".into(),
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

/// Unpacks a zip (zip-slip guarded, symlink entries refused) into a temp folder below `parent`.
pub fn unpack_to_temp(zip: &Path, parent: &Path) -> Result<TempDir> {
    let tmp = TempDir::new(parent, "kiln-unpack")?;
    unpack(zip, tmp.path())?;
    Ok(tmp)
}

#[cfg(feature = "zip")]
fn unpack(zip: &Path, dest: &Path) -> Result<()> {
    let f = std::fs::File::open(zip).map_err(io(zip, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?;
    for i in 0..a.len() {
        let mut e = a.by_index(i).map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?;
        let name = e.name().map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display())))?.replace('\\', "/");
        if e.is_symlink() {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlink entry {name}", zip.display())));
        }
        let rel = crate::bundle::safe_rel_path(name.trim_end_matches('/'))?;
        let target = dest.join(rel);
        if e.is_dir() {
            std::fs::create_dir_all(&target).map_err(io(&target, "creating"))?;
            continue;
        }
        if let Some(p) = target.parent() {
            std::fs::create_dir_all(p).map_err(io(p, "creating"))?;
        }
        let mut out = std::fs::File::create(&target).map_err(io(&target, "writing"))?;
        std::io::copy(&mut e, &mut out).map_err(io(&target, "writing"))?;
    }
    Ok(())
}

#[cfg(not(feature = "zip"))]
fn unpack(zip: &Path, _dest: &Path) -> Result<()> {
    Err(Error::new(ErrorCode::Unsupported, format!("{}: kiln-manage was built without the zip feature", zip.display())))
}

/// Copies a mod folder without its dot entries (not part of its kdh1); symlinks are refused.
fn copy_mod_tree(from: &Path, to: &Path) -> Result<u64> {
    std::fs::create_dir_all(to).map_err(io(to, "creating"))?;
    let mut size = 0;
    for e in std::fs::read_dir(from).map_err(io(from, "reading"))? {
        let e = e?;
        let name = e.file_name();
        if name.to_string_lossy().starts_with('.') {
            continue;
        }
        let ft = std::fs::symlink_metadata(e.path())?.file_type();
        let dst = to.join(&name);
        if ft.is_symlink() {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not allowed in a mod folder", e.path().display())));
        } else if ft.is_dir() {
            size += copy_mod_tree(&e.path(), &dst)?;
        } else if ft.is_file() {
            size += std::fs::copy(e.path(), &dst).map_err(io(&dst, "copying to"))?;
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

/// Moves a prepared folder into the library as `<kind>/<id>/<version>+<hash8>` with its record. Existing entries
/// (same hash) are kept as they are (content addressed: same name, same bytes).
fn commit(dirs: &DataDirs, prepared: &Path, rec: ImportRecord, report: &mut ImportReport) -> Result<()> {
    commit_folder(dirs, prepared, rec, report)
}

/// For engine loaders (engines::Loader): moves a prepared folder (made in a [`TempDir`] below `dirs.library()`) into
/// the library as `<kind>/<id>/<version>+<hash8>` with its record; an entry with the same hash is kept.
pub fn commit_folder(dirs: &DataDirs, prepared: &Path, rec: ImportRecord, report: &mut ImportReport) -> Result<()> {
    let r = LibraryRef { kind: rec.kind, id: rec.id.clone(), version: rec.version.clone(), hash: rec.hash.clone() };
    let folder = dirs.library_entry(rec.kind, &rec.id, &rec.version, &rec.hash)?;
    if folder.is_dir() && record_path(&folder).is_file() {
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
            if !crate::manifest::is_mod_id(&id) {
                skip(report, format!("id {id:?} is not a valid mod id"));
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
            let version = if semverish(&version) { version } else { format!("0.0.0-{}", id_from_name(&version)) };
            let rec = ImportRecord { format: 1, kind, id, version, hash, imported: now, source: Some(from.clone()), size, name: d.name.clone(), side, clients, file: None };
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
                source: Some(from.clone()), size, name: Some(file.clone()), side: None, clients: None, file: Some(file) };
            commit(dirs, &work, rec, report)
        }
        DetectKind::BareDll | DetectKind::Emf => {
            // a bare DLL becomes a mod folder: native.json (phase early, or dllmain for a proxy) or an EMF mod.json
            let tmp = TempDir::new(&staging_parent, "import")?;
            let work = tmp.path().join("m");
            std::fs::create_dir_all(&work).map_err(io(&work, "creating"))?;
            let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "mod.dll".into());
            let mut size = std::fs::copy(src, work.join(&file)).map_err(io(src, "copying"))?;
            let id = d.id.clone().unwrap_or_else(|| id_from_name(&file));
            let (kind, manifest_name, text) = if d.kind == DetectKind::Emf {
                (ModKind::Emf, EMF_MANIFEST, serde_json::json!({
                    "id": id, "name": file, "version": "0.0.0", "emf": 21, "dll": file,
                    "description": format!("Generated by Kiln from {file}"),
                }))
            } else {
                let proxy = d.reason.contains("DirectInput8Create");
                (ModKind::Native, crate::manifest::NATIVE_MANIFEST, serde_json::json!({
                    "format": 1, "id": id, "name": file, "version": "0.0.0",
                    "description": format!("Generated by Kiln from {file}{}", if proxy { " (a dinput8 proxy, chain-loaded in DllMain)" } else { "" }),
                    "side": "client", "dlls": [file], "phase": if proxy { "dllmain" } else { "early" },
                }))
            };
            let mut body = serde_json::to_string_pretty(&text)?;
            body.push('\n');
            size += body.len() as u64;
            std::fs::write(work.join(manifest_name), body).map_err(io(&work, "writing"))?;
            let hash = crate::hash::dirhash(&work)?;
            let rec = ImportRecord { format: 1, kind, id, version: "0.0.0".into(), hash, imported: now, source: Some(from.clone()), size,
                name: Some(file), side: Some(Side::Client), clients: Some(Clients::Mod), file: None };
            commit(dirs, &work, rec, report)
        }
        DetectKind::Runtime => {
            skip(report, "a Kiln runtime: import it on the Runtime page (bundle::import)".into());
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
        let lower = p.to_string_lossy().to_lowercase();
        if lower.ends_with(".zip") && p.is_file() {
            std::fs::create_dir_all(dirs.library()).map_err(io(&dirs.library(), "creating"))?;
            let tmp = unpack_to_temp(p, &dirs.library())?;
            let mut d = detect_folder(tmp.path());
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
    let folder = dirs.library_entry(r.kind, &r.id, &r.version, &r.hash)?;
    crate::util::read_json::<ImportRecord>(&record_path(&folder))?
        .filter(|rec| rec.hash == r.hash)
        .ok_or_else(|| Error::not_found(format!("{} {} {} is not in the library", r.kind.as_str(), r.id, r.version)))
}

/// Profiles (their ids) that list this entry.
pub fn users(dirs: &DataDirs, r: &LibraryRef) -> Vec<String> {
    let mut out = Vec::new();
    for e in std::fs::read_dir(dirs.profiles()).into_iter().flatten().flatten() {
        let Ok(Some(p)) = crate::util::read_json::<crate::profile::Profile>(&e.path().join("meta.json")) else { continue };
        let list = match r.kind {
            ModKind::Kiln => &p.mods.kiln,
            ModKind::Native => &p.mods.native,
            ModKind::Plugin => &p.mods.plugin,
            ModKind::Emf => &p.mods.emf,
        };
        if list.iter().any(|m| m.id == r.id && m.version == r.version && m.hash == r.hash) {
            out.push(p.id);
        }
    }
    out.sort();
    out
}

/// Refuses (Conflict) while a profile lists the version, unless `force`.
pub fn remove(dirs: &DataDirs, r: &LibraryRef, force: bool) -> Result<()> {
    let folder = dirs.library_entry(r.kind, &r.id, &r.version, &r.hash)?;
    if !folder.is_dir() {
        return Err(Error::not_found(format!("{} {} {} is not in the library", r.kind.as_str(), r.id, r.version)));
    }
    let used = users(dirs, r);
    if !used.is_empty() && !force {
        return Err(Error::new(ErrorCode::Conflict, format!("{} {} is used by profile(s) {}", r.id, r.version, used.join(", "))).with_detail(used));
    }
    std::fs::remove_file(record_path(&folder)).map_err(io(&folder, "removing the record of"))?;
    std::fs::remove_dir_all(&folder).map_err(io(&folder, "removing"))?;
    if let Some(parent) = folder.parent() {
        let _ = std::fs::remove_dir(parent);
    }
    Ok(())
}

/// The folder (or DLL file, for a plugin) of a library entry.
pub fn path_of(dirs: &DataDirs, r: &LibraryRef) -> Result<PathBuf> {
    let dir = dirs.library_entry(r.kind, &r.id, &r.version, &r.hash)?;
    if r.kind == ModKind::Plugin {
        let rec = record(dirs, r)?;
        let file = rec.file.ok_or_else(|| Error::new(ErrorCode::Schema, format!("plugin {} has no file in its record", r.id)))?;
        return Ok(dir.join(file));
    }
    Ok(dir)
}
