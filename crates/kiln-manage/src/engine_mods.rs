//! Mods made for other engines, managed like Kiln mods (docs/kiln/engines.md "Managing every engine's mods"):
//!
//! - [`builtin_loaders`]: the Library importers of every engine kind (EML, ShroudForge, SET, Greeneye, Flame,
//!   Shroudtopia, Enshwick, shroudlink, Ember). Each copies the mod into a content-addressed library folder
//!   (`library::commit_folder`). The launcher only moves files: no engine code (EML's GPL crates included) is linked or
//!   run here. Ember's mods are held and synced like the others but don't run yet: Ember has published no mod ABI, so
//!   kiln-ember only finds and reports them (docs/kiln/engines/ember.md).
//! - Enshwick's plugin copies a profile's Enshwick mods into `<game>/mods/<id>/` itself (with its own receipts), so the
//!   profile names the library folder; shroudlink and Ember read the library folder in place.
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

/// shroudlink's mods folder, relative to the game (its DLLs directly, and Kiln's synced folders one level down).
pub const SHROUDLINK_MODS: &str = "shroudlink_mods";
/// Ember's mods folder, relative to the server: `ember/mods/<mod>/`, one library each.
pub const EMBER_MODS: &str = "ember/mods";
/// Enshwick's loader keeps its own files in `<game>/mods/enshwick/`: no mod may take that folder name.
pub const ENSHWICK_LOADER_FOLDER: &str = "enshwick";

/// The kinds with a Library importer: every engine kind (EMF's mods are Kiln's own `emf` kind).
pub const IMPORTED: [ModKind; 9] = [ModKind::Eml, ModKind::ShroudForge, ModKind::Set, ModKind::Greeneye, ModKind::Flame,
    ModKind::Shroudtopia, ModKind::Enshwick, ModKind::Shroudlink, ModKind::Ember];

/// Kinds whose mods Kiln holds but cannot run yet (Ember: no published mod ABI). The launcher says so.
pub fn runs_yet(kind: ModKind) -> bool {
    kind != ModKind::Ember
}

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
        ModKind::Enshwick => "kiln-enshwick",
        ModKind::Shroudlink => "kiln-shroudlink",
        ModKind::Ember => "kiln-ember",
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
        // data patches and new content: both sides must have the same data. Enshwick: a mod's side is "both" unless it
        // says "server" (pack.toml; a DLL's is compiled in), and its join check refuses players without a "both" mod.
        ModKind::Eml | ModKind::ShroudForge | ModKind::Flame | ModKind::Enshwick => (Side::Both, Clients::Mod),
        ModKind::Greeneye => (Side::Client, Clients::None),
        // server frameworks: shroudlink's mods run in its server core (players use its client addon), Ember is for
        // dedicated servers only
        ModKind::Shroudlink | ModKind::Ember => (Side::Server, Clients::None),
        _ => (Side::Both, Clients::None),
    }
}

fn read_json(p: &Path) -> Option<serde_json::Value> {
    crate::jsonc::parse(crate::manifest::read_text(p).ok()?.trim_start_matches('\u{feff}')).ok()
}

// Detection owns framework precedence: an ancillary pack.toml must not replace a
// Flame, ShroudForge, Kiln or native manifest selected before Enshwick's pack rule.
fn is_enshwick_pack(folder: &Path) -> bool {
    folder.join("pack.toml").is_file()
        && crate::library::detect(folder).is_ok_and(|d| d.kind == DetectKind::Enshwick)
}

/// The manifest an engine's mod folder has (mod.json, flame.json, shroudtopia.json, Enshwick's pack.toml), as JSON.
pub fn manifest_json(folder: &Path) -> Option<serde_json::Value> {
    if is_enshwick_pack(folder) { return crate::engines::pack_toml(folder); }
    ["mod.json", "flame.json", "shroudtopia.json"].iter().find_map(|f| read_json(&folder.join(f)))
}

fn side_of(kind: ModKind, folder: &Path) -> (Side, Clients) {
    let (mut side, mut clients) = default_side(kind);
    if matches!(kind, ModKind::Shroudlink | ModKind::Ember) {
        return (side, clients); // server-only engines: joining players never need their mods
    }
    if kind == ModKind::Enshwick {
        // Only Enshwick's own pack controls its join requirements. An unrelated mod.json
        // must not turn a both-side pack into a server-only advertisement.
        return crate::engines::pack_toml(folder).and_then(|m| m.get("side").and_then(|s| s.as_str()).map(str::to_owned))
            .filter(|s| s == "server").map_or((side, clients), |_| (Side::Server, Clients::None));
    }
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

/// The minimal manifest a DLL-only release needs, whether the DLL was dropped alone or in an asset folder.
fn write_dll_manifest(kind: ModKind, folder: &Path, id: &str, version: &str, file: &str) -> Result<u64> {
    let generated = match kind {
        ModKind::Greeneye => Some(("mod.json", serde_json::json!({ "id": id, "name": file, "version": version, "dll": file, "side": "client",
            "description": format!("Generated by Kiln from {file}") }))),
        ModKind::Flame => Some(("flame.json", serde_json::json!({ "dll": file }))),
        // kiln-shroudtopia's folder scan (servers, drop-ins) takes folders with a mod.json.
        ModKind::Shroudtopia => Some(("mod.json", serde_json::json!({ "id": id, "name": file, "version": version,
            "description": format!("Generated by Kiln from {file}") }))),
        // Enshwick's and shroudlink's manifests are compiled into the DLL (enshwick_mod_query, shroudlink_mod_v2), and
        // Ember's mods have none: the DLL alone in its folder is the mod
        _ => None,
    };
    let Some((name, value)) = generated else { return Ok(0); };
    let mut body = serde_json::to_string_pretty(&value)?;
    body.push('\n');
    std::fs::write(folder.join(name), &body).map_err(io_at(folder, "writing"))?;
    Ok(body.len() as u64)
}

/// The first DLL directly in `dir` that is a mod DLL of `kind` (by its exports).
fn dll_of(dir: &Path, kind: ModKind) -> Result<Option<PathBuf>> {
    let mut files = Vec::new();
    for e in std::fs::read_dir(dir).map_err(io_at(dir, "reading"))? {
        let e = e?;
        if e.file_type()?.is_file() && e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("dll")) {
            files.push(e.path());
        }
    }
    files.sort();
    Ok(files.into_iter().find(|p| crate::library::detect(p).is_ok_and(|d| ModKind::of_detect(d.kind) == Some(kind))))
}

/// Enshwick's Windows folder rule, in addition to Kiln's ASCII identity rule.
fn safe_enshwick_id(id: &str) -> bool {
    let stem = id.split('.').next().unwrap_or_default();
    let device = matches!(stem, "con" | "prn" | "aux" | "nul")
        || (stem.len() == 4 && (stem.starts_with("com") || stem.starts_with("lpt"))
            && matches!(stem.as_bytes()[3], b'1'..=b'9'));
    crate::manifest::is_mod_id(id) && !id.chars().any(char::is_control)
        && !id.ends_with('.') && !device && !id.eq_ignore_ascii_case(ENSHWICK_LOADER_FOLDER)
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
    let src = ctx.src;
    // Library importers are also called directly (outside the drop/Deck flow). Validate before reading a
    // manifest or DLL, including hidden links which are not part of kdh1 but are still unsafe imports.
    crate::quick::validate_import_path(src)?;
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
    if id.is_empty() && d.id.is_none() {
        id = crate::library::id_from_name(name.as_deref().unwrap_or_default());
    }
    if !crate::manifest::is_mod_id(&id) || id.chars().any(char::is_control) {
        // Do not silently rename an explicit manifest id: the plugin reads the unmodified manifest.
        return skip(ctx.report, format!("{}: id {id:?} is not a valid mod id", d.path));
    }
    if kind == ModKind::Enshwick && id.eq_ignore_ascii_case(ENSHWICK_LOADER_FOLDER) {
        // kiln-enshwick copies the mod into <game>/mods/<id>/, and mods/enshwick/ is the loader's own folder
        return skip(ctx.report, format!("{}: id \"enshwick\" is Enshwick's own loader folder, not a mod", d.path));
    }
    if kind == ModKind::Enshwick {
        if !safe_enshwick_id(&id) {
            return skip(ctx.report, format!("{}: id {id:?} is not a safe Windows mod folder", d.path));
        }
        if src.is_dir() && src.join("pack.toml").is_file() && crate::engines::pack_toml(src).is_none() {
            return skip(ctx.report, format!("{}: invalid or unsupported pack.toml metadata", d.path));
        }
    }
    let version = if let Ok(parsed) = crate::semver::SemVersion::parse(&version) {
        parsed.to_string()
    } else if version.is_empty() {
        "0.0.0".to_string()
    } else {
        // A digit-only slug such as "001" is not a valid SemVer prerelease identifier by itself.
        crate::library::fallback_version(&version)
    };
    let staging = ctx.dirs.library();
    std::fs::create_dir_all(&staging).map_err(io_at(&staging, "creating"))?;
    let tmp = crate::library::TempDir::new(&staging, "import")?;
    let work = tmp.path().join("m");
    let size = if src.is_dir() {
        let mut size = crate::library::copy_mod_tree(src, &work)?;
        if crate::hash::dirhash(&work)? != crate::hash::dirhash(src)? {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while it was imported", src.display())));
        }
        // what each plugin needs to find the mod in the folder, checked on the copy that is committed
        let found = match kind {
            ModKind::Enshwick => {
                if work.join("pack.toml").is_file() {
                    crate::engines::pack_toml(&work).is_some_and(|m| {
                        m.get("id").and_then(|v| v.as_str()).map(str::to_owned)
                            .or_else(|| src.file_name().and_then(|n| n.to_str()).map(crate::library::id_from_name)) == d.id
                            && m.get("version").and_then(|v| v.as_str()).map(str::to_owned) == d.version
                    })
                } else { dll_of(&work, kind)?.is_some() }
            },
            ModKind::Shroudlink => dll_of(&work, kind)?.is_some(),
            ModKind::Ember => crate::engines::ember_library(&work).is_some(),
            _ => true,
        };
        if !found {
            let why = match kind {
                ModKind::Ember => "an Ember mod folder holds exactly one library (.dll), and no other engine's mod DLL",
                ModKind::Enshwick => "invalid, unsupported or changed pack.toml, or no Enshwick mod DLL",
                _ => "no DLL exporting shroudlink_mod_v2 or shroudlink_mod_v1",
            };
            return skip(ctx.report, format!("{}: {why}", d.path));
        }
        let required = match kind {
            ModKind::Greeneye | ModKind::Shroudtopia => Some("mod.json"),
            ModKind::Flame => Some("flame.json"),
            _ => None,
        };
        if required.is_some_and(|m| !work.join(m).is_file()) {
            let dll = dll_of(&work, kind)?.ok_or_else(|| Error::invalid(format!("{}: no {} mod DLL", src.display(), kind.label())))?;
            let file = dll.file_name().and_then(|n| n.to_str()).ok_or_else(|| Error::invalid("DLL name must be UTF-8"))?;
            size += write_dll_manifest(kind, &work, &id, &version, file)?;
        }
        size
    } else {
        if kind == ModKind::Ember {
            // nothing in a lone DLL says it is Ember's; detection only names folders in ember/mods/
            return skip(ctx.report, format!("{}: add the Ember mod's folder (ember/mods/<mod>/), not the DLL alone", d.path));
        }
        std::fs::create_dir_all(&work).map_err(io_at(&work, "creating"))?;
        let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "mod.dll".into());
        let size = std::fs::copy(src, work.join(&file)).map_err(io_at(src, "copying"))?;
        size + write_dll_manifest(kind, &work, &id, &version, &file)?
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

// ---- placing mods in their engine's folder: ShroudForge and SET always, the others for their own loader ----

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
    /// A folder with the mod's name that the player put there: left as it is. For the own-loader kinds also a
    /// player's folder holding the same mod (the same manifest id): the own loader runs that one.
    pub own: Vec<String>,
    /// Kiln's copy for an own loader changed since it was placed (the mod or the player wrote into it): left as it
    /// is, neither refreshed nor removed.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub changed: Vec<String>,
    /// What each installed own loader means for the profile (own_loaders::notes, after placing).
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_loaders: Vec<crate::own_loaders::OwnLoaderNote>,
}

/// Refuse symlinks (including dangling links and Windows junctions) from `root` down to `path`.
///
/// `root` is the folder Kiln manages: the data dir, the game or server folder, a Library entry or a staging folder.
/// `root` itself and every existing component below it must be a real folder or file. Folders ABOVE `root` belong to the
/// system and the player and may be links: Fedora Atomic and Bazzite make `/home` a link to `/var/home`, and Steam
/// libraries are often reached through links. A `path` that is not under `root` gets the strict check from its first
/// component on, so a caller that passes the wrong root only becomes stricter. Parent traversal (`..`) is refused
/// anywhere in `path`.
///
/// Mutation callers check the parent before creating missing components as well as the leaf.
pub fn refuse_symlink_path(root: &Path, path: &Path) -> Result<()> {
    // The prefix length (in components) from which links are refused.
    let first_checked = if path.starts_with(root) { root.components().count() } else { 0 };
    let mut prefix = PathBuf::new();
    let mut components = path.components().enumerate().peekable();
    while let Some((index, component)) = components.next() {
        if matches!(component, std::path::Component::ParentDir) {
            return Err(Error::new(ErrorCode::Refused, format!("{}: parent traversal is not allowed", path.display())));
        }
        prefix.push(component.as_os_str());
        // An absolute Windows prefix (e.g. `\\?\C:`) needs its following root separator.
        // Drive-relative prefixes (`C:child`) still need inspection of the drive's current directory.
        if matches!(component, std::path::Component::Prefix(_))
            && matches!(components.peek(), Some((_, std::path::Component::RootDir))) { continue; }
        // `index + 1` components are in `prefix`: the folders above the managed root are not ours to judge.
        if index + 1 < first_checked { continue; }
        match std::fs::symlink_metadata(&prefix) {
            Ok(meta) => {
                #[cfg(windows)]
                let reparse = {
                    use std::os::windows::fs::MetadataExt;
                    meta.file_attributes() & 0x400 != 0
                };
                #[cfg(not(windows))]
                let reparse = false;
                if meta.file_type().is_symlink() || reparse {
                    return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not allowed", prefix.display())));
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(io_at(&prefix, "inspecting")(e)),
        }
    }
    Ok(())
}

#[derive(Debug, Clone, Serialize, Deserialize)]
struct Placement {
    path: PathBuf,
    marker: Marker,
    // Includes hidden files and unexpected additions. SF's mutable switch and settings are normalized below.
    files: std::collections::BTreeMap<String, String>,
}

/// `managed` is the folder links are refused from (see [`refuse_symlink_path`]); the lock lives in `root` below it.
fn engine_lock(managed: &Path, root: &Path) -> Result<std::fs::File> {
    let path = root.join("engine-mods.lock");
    refuse_symlink_path(managed, &path)?;
    std::fs::create_dir_all(root).map_err(io_at(root, "creating"))?;
    let lock = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&path)?;
    lock.lock()?;
    Ok(lock)
}

fn placements_file(game: &Path) -> PathBuf {
    game.join("kiln/state/library-placements.json")
}

fn placements(game: &Path) -> Result<Vec<Placement>> {
    let file = placements_file(game);
    refuse_symlink_path(game, &file)?;
    Ok(crate::util::read_json(&file)?.unwrap_or_default())
}

fn save_placements(game: &Path, entries: &[Placement]) -> Result<()> {
    let file = placements_file(game);
    refuse_symlink_path(game, &file)?;
    let parent = file.parent().expect("receipt parent");
    std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
    let tmp = crate::library::TempDir::new(parent, "receipt")?;
    let staged = tmp.path().join("receipt.json");
    crate::util::write_json_atomic(&staged, &entries)?;
    crate::util::rename_replace(&staged, &file)
}

fn placement_files(root: &Path, kind: ModKind) -> Result<std::collections::BTreeMap<String, String>> {
    fn walk(root: &Path, dir: &Path, kind: ModKind, out: &mut std::collections::BTreeMap<String, String>) -> Result<()> {
        refuse_symlink_path(root, dir)?;
        for entry in std::fs::read_dir(dir).map_err(io_at(dir, "reading"))? {
            let entry = entry?;
            let path = entry.path();
            refuse_symlink_path(root, &path)?;
            let rel = path.strip_prefix(root).expect("tree child").to_str()
                .ok_or_else(|| Error::invalid("non-Unicode placed mod path"))?.replace('\\', "/");
            if rel == MARKER { continue; }
            if entry.file_type()?.is_dir() {
                // Keep empty and newly added directories in the ownership inventory too.
                out.insert(format!("{rel}/"), String::new());
                walk(root, &path, kind, out)?;
            } else if entry.file_type()?.is_file() {
                let hash = if kind == ModKind::ShroudForge && rel == "extended.mod.json" {
                    let mut doc = read_json_checked(root, &path)?.ok_or_else(|| Error::invalid("missing SF extension"))?;
                    let obj = doc.as_object_mut().ok_or_else(|| Error::invalid("SF extension must be an object"))?;
                    obj.remove("enabled");
                    obj.remove("settings");
                    crate::hash::sha256_hex(&serde_json::to_vec(&doc)?)
                } else { crate::hash::sha256_file(&path)?.0 };
                out.insert(rel, hash);
            } else {
                return Err(Error::new(ErrorCode::Refused, format!("{}: not a regular file", path.display())));
            }
        }
        Ok(())
    }
    let mut files = std::collections::BTreeMap::new();
    walk(root, root, kind, &mut files)?;
    Ok(files)
}

/// `game` is the managed root `dst` lies under.
fn owned<'a>(game: &Path, entries: &'a [Placement], dst: &Path, kind: ModKind, id: &str) -> Result<Option<&'a Placement>> {
    refuse_symlink_path(game, dst)?;
    if !dst.exists() { return Ok(None); }
    let Some(receipt) = entries.iter().find(|r| r.path == dst && r.marker.kind == kind && r.marker.id == id) else { return Ok(None) };
    let valid = receipt.marker.format == 1 && crate::manifest::is_mod_id(&receipt.marker.id)
        && marker(dst).as_ref() == Some(&receipt.marker)
        && placement_files(dst, kind)? == receipt.files;
    if !valid {
        return Err(Error::new(ErrorCode::Refused, format!("{}: managed copy changed; refusing to replace or remove player files", dst.display())));
    }
    Ok(Some(receipt))
}

// A failed rollback must leave recoverable bytes on disk, even if the destination was replaced concurrently.
pub(crate) fn preserve_recovery(tmp: crate::library::TempDir, error: Error) -> Error {
    let recovery = tmp.path().to_path_buf();
    std::mem::forget(tmp);
    error.with_detail(serde_json::json!({ "recovery": recovery }))
}

/// The folders links are refused from when a mod is copied from the Library into a game folder.
#[derive(Clone, Copy)]
struct Roots<'a> {
    /// Kiln's data dir (the Library entry is below it).
    data: &'a Path,
    /// The game folder (the destination is below it).
    game: &'a Path,
}

/// Stage the entire replacement first, then rename the old copy aside; restore it if publication fails.
fn copy_in(roots: Roots, src: &Path, dst: &Path, m: &Marker, kept: &[(String, serde_json::Value)], enabled: bool,
           persist: impl FnOnce(&Placement) -> Result<()>) -> Result<()> {
    refuse_symlink_path(roots.data, src)?;
    refuse_symlink_path(roots.game, dst)?;
    if crate::hash::dirhash(src)? != m.hash {
        return Err(Error::new(ErrorCode::HashMismatch, format!("{}: library hash mismatch", src.display())));
    }
    let parent = dst.parent().ok_or_else(|| Error::invalid("no mod parent"))?;
    std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
    let tmp = crate::library::TempDir::new(parent, "kiln-place")?;
    let staged = tmp.path().join("new");
    crate::library::copy_mod_tree(src, &staged)?;
    if crate::hash::dirhash(&staged)? != m.hash {
        return Err(Error::new(ErrorCode::HashMismatch, "mod changed while placing"));
    }
    if m.kind == ModKind::ShroudForge {
        let new = crate::shroudforge::read_mod(&staged)?;
        for (key, value) in kept {
            if new.settings.iter().any(|s| &s.key == key) {
                crate::shroudforge::set_setting(&staged, key, value)?;
            }
        }
        crate::shroudforge::set_enabled(&staged, enabled)?;
    }
    crate::util::write_json_atomic(&staged.join(MARKER), m)?;
    let receipt = Placement { path: dst.to_path_buf(), marker: m.clone(), files: placement_files(&staged, m.kind)? };
    let old = tmp.path().join("old");
    let replaced = dst.exists();
    if replaced { std::fs::rename(dst, &old).map_err(io_at(dst, "parking old mod"))?; }
    if let Err(e) = std::fs::rename(&staged, dst) {
        if replaced {
            if let Err(e) = std::fs::rename(&old, dst) {
                return Err(preserve_recovery(tmp, io_at(dst, "restoring old mod")(e)));
            }
        }
        return Err(io_at(dst, "placing")(e));
    }
    if let Err(error) = persist(&receipt) {
        if let Err(e) = std::fs::rename(dst, &staged) {
            return Err(preserve_recovery(tmp, io_at(dst, "rolling back new mod")(e)));
        }
        if replaced {
            if let Err(e) = std::fs::rename(&old, dst) {
                return Err(preserve_recovery(tmp, io_at(dst, "restoring old mod")(e)));
            }
        }
        return Err(error);
    }
    Ok(())
}

fn remove_copy(game: &Path, dst: &Path, persist: impl FnOnce() -> Result<()>) -> Result<()> {
    refuse_symlink_path(game, dst)?;
    let parent = dst.parent().ok_or_else(|| Error::invalid("no mod parent"))?;
    let tmp = crate::library::TempDir::new(parent, "kiln-remove")?;
    let parked = tmp.path().join("removed");
    std::fs::rename(dst, &parked).map_err(io_at(dst, "removing"))?;
    if let Err(error) = persist() {
        if let Err(e) = std::fs::rename(&parked, dst) {
            return Err(preserve_recovery(tmp, io_at(dst, "restoring removed mod")(e)));
        }
        return Err(error);
    }
    Ok(())
}

/// The `id` a mod's own manifest names (`mod.json`, for EML, EMF, Greeneye, Flame and Shroudtopia 1.0), as written.
fn manifest_id_raw(dir: &Path) -> Option<String> {
    let file = dir.join("mod.json");
    if std::fs::metadata(&file).ok()?.len() > 1024 * 1024 { return None; }
    read_json(&file)?.get("id")?.as_str().map(str::to_owned)
}

/// A folder in `root` other than `dst` that holds the same mod (the same manifest id, case ignored) and is not one of
/// Kiln's copies of this kind: the player installed it there. Real EML stops the game on a duplicate id.
fn same_mod_elsewhere(root: &Path, kind: ModKind, src: &Path, dst: &Path) -> Option<PathBuf> {
    let id = manifest_id_raw(src)?;
    std::fs::read_dir(root).ok()?.flatten().map(|e| e.path())
        .filter(|p| p != dst && p.is_dir() && !marker(p).is_some_and(|m| m.kind == kind))
        .find(|p| manifest_id_raw(p).is_some_and(|other| other.eq_ignore_ascii_case(&id)))
}

/// Reconcile only copies authorized by the install's receipt. Markers shipped in archives do not grant ownership.
///
/// ShroudForge and SET: always (their plugins read only their own folder). EML, EMF, Greeneye, Flame and Shroudtopia
/// (own_loaders::PLACED_FOR_LOADER): only while that engine's own loader is installed ([`crate::own_loaders::find`]),
/// because that loader reads only its own folder and never the Library. Then the profile's enabled mods are copied
/// there as `<folder>/<library id>/` with the marker, current copies are kept, Kiln's copies of mods switched off or
/// gone from the profile are removed. Without that loader Kiln's copies of the kind are removed again (the plugin runs
/// the mods from the Library and skips marked copies). Player folders are never touched.
pub fn place(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path) -> Result<PlaceReport> {
    let view = crate::own_loaders::ProfileView::of_profile(profile);
    let loaders = crate::own_loaders::find(game_dir, Some(&view));
    let mut rep = place_with(dirs, profile, game_dir, &loaders)?;
    rep.own_loaders = crate::own_loaders::notes(game_dir, &loaders, Some(&view));
    Ok(rep)
}

fn place_with(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path, loaders: &[crate::own_loaders::OwnLoader])
              -> Result<PlaceReport> {
    let _lock = engine_lock(game_dir, &game_dir.join("kiln/state"))?;
    let mut rep = PlaceReport::default();
    let mut receipts = placements(game_dir)?;
    for kind in [ModKind::ShroudForge, ModKind::Set] {
        let list = profile.mods.list(kind);
        let root = match kind {
            ModKind::ShroudForge => crate::shroudforge::mods_dir(game_dir),
            _ => game_dir.join(SET_MODS),
        };
        refuse_symlink_path(game_dir, &root)?;
        let tag = |id: &str| format!("{}:{id}", kind.as_str());
        for m in list {
            // path_of validates id, version and hash before any destination is joined or touched.
            let src = crate::library::path_of(dirs, &crate::library::LibraryRef {
                kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?;
            let dst = root.join(&m.id);
            let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
            let have = owned(game_dir, &receipts, &dst, kind, &m.id)?.cloned();
            if have.is_none() && dst.exists() {
                rep.own.push(tag(&m.id));
                continue; // In particular, never apply a ShroudForge switch to a player's folder.
            }
            if kind == ModKind::Set && !m.enabled {
                if have.is_some() {
                    remove_copy(game_dir, &dst, || {
                        receipts.retain(|r| r.path != dst);
                        save_placements(game_dir, &receipts)
                    })?;
                    rep.removed.push(tag(&m.id));
                }
                continue;
            }
            if have.as_ref().is_some_and(|h| h.marker == want) {
                if kind == ModKind::ShroudForge {
                    refuse_symlink_path(game_dir, &dst.join("extended.mod.json.tmp"))?;
                    if crate::shroudforge::read_mod(&dst)?.enabled != m.enabled {
                        crate::shroudforge::set_enabled(&dst, m.enabled)?;
                        rep.switched.push(tag(&m.id));
                    }
                }
                rep.kept.push(tag(&m.id));
            } else {
                let kept: Vec<_> = if kind == ModKind::ShroudForge && have.is_some() {
                    crate::shroudforge::read_mod(&dst)?.settings.into_iter().map(|s| (s.key, s.value)).collect()
                } else { Vec::new() };
                copy_in(Roots { data: &dirs.root, game: game_dir }, &src, &dst, &want, &kept, m.enabled, |receipt| {
                    receipts.retain(|r| r.path != dst);
                    receipts.push(receipt.clone());
                    save_placements(game_dir, &receipts)
                })?;
                rep.placed.push(tag(&m.id));
            }
        }
        // Enumerate trusted receipts, never infer ownership by scanning arbitrary player folders.
        let unlisted: Vec<_> = receipts.iter().filter(|r| r.marker.kind == kind && r.path.parent() == Some(root.as_path())
            && !list.iter().any(|m| m.id == r.marker.id)).cloned().collect();
        for receipt in unlisted {
            let dst = &receipt.path;
            if !dst.exists() {
                receipts.retain(|r| r.path != *dst);
                save_placements(game_dir, &receipts)?;
                continue;
            }
            owned(game_dir, &receipts, dst, kind, &receipt.marker.id)?;
            if kind == ModKind::Set {
                remove_copy(game_dir, dst, || {
                    receipts.retain(|r| r.path != *dst);
                    save_placements(game_dir, &receipts)
                })?;
                rep.removed.push(tag(&receipt.marker.id));
            } else if crate::shroudforge::read_mod(dst)?.enabled {
                refuse_symlink_path(game_dir, &dst.join("extended.mod.json.tmp"))?;
                crate::shroudforge::set_enabled(dst, false)?;
                rep.switched.push(tag(&receipt.marker.id));
            }
        }
    }
    for kind in crate::own_loaders::PLACED_FOR_LOADER {
        let loader = crate::own_loaders::of_kind(loaders, kind).filter(|l| l.delivery == crate::own_loaders::Delivery::Placed);
        let root = loader.map(|l| l.root(game_dir));
        let list = profile.mods.list(kind);
        let tag = |id: &str| format!("{}:{id}", kind.as_str());
        // A changed copy (a mod writing its settings into its folder, the player editing it) is not Kiln's to replace.
        let soft = |r: Result<Option<&Placement>>| match r {
            Ok(h) => Ok(Some(h.cloned())),
            Err(e) if e.code == ErrorCode::Refused => Ok(None),
            Err(e) => Err(e),
        };
        if let Some(root) = &root {
            refuse_symlink_path(game_dir, root)?;
            for m in list.iter().filter(|m| m.enabled) {
                let src = crate::library::path_of(dirs, &crate::library::LibraryRef {
                    kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?;
                let dst = root.join(&m.id);
                let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
                let Some(have) = soft(owned(game_dir, &receipts, &dst, kind, &m.id))? else {
                    rep.changed.push(tag(&m.id));
                    continue;
                };
                if have.is_none() && dst.exists() {
                    rep.own.push(tag(&m.id));
                    continue;
                }
                if have.as_ref().is_some_and(|h| h.marker == want) {
                    rep.kept.push(tag(&m.id));
                    continue;
                }
                if have.is_none() && same_mod_elsewhere(root, kind, &src, &dst).is_some() {
                    rep.own.push(tag(&m.id));
                    continue;
                }
                copy_in(Roots { data: &dirs.root, game: game_dir }, &src, &dst, &want, &[], true, |receipt| {
                    receipts.retain(|r| r.path != dst);
                    receipts.push(receipt.clone());
                    save_placements(game_dir, &receipts)
                })?;
                rep.placed.push(tag(&m.id));
            }
        }
        // Kiln's copies the loader should not run (switched off, gone from the profile, no loader, another folder).
        let stale: Vec<_> = receipts.iter().filter(|r| r.marker.kind == kind && !root.as_ref().is_some_and(|root|
            r.path.parent() == Some(root.as_path()) && list.iter().any(|m| m.enabled && m.id == r.marker.id))).cloned().collect();
        for receipt in stale {
            let dst = &receipt.path;
            if !dst.exists() {
                receipts.retain(|r| r.path != *dst);
                save_placements(game_dir, &receipts)?;
                continue;
            }
            if soft(owned(game_dir, &receipts, dst, kind, &receipt.marker.id))?.is_none() {
                rep.changed.push(tag(&receipt.marker.id));
                continue;
            }
            remove_copy(game_dir, dst, || {
                receipts.retain(|r| r.path != *dst);
                save_placements(game_dir, &receipts)
            })?;
            rep.removed.push(tag(&receipt.marker.id));
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
        // Enshwick's loader reads every folder in <server>/mods (kiln-enshwick hosts it on servers too)
        ModKind::Flame | ModKind::Shroudtopia | ModKind::Enshwick => server.join("mods"),
        // kiln-shroudlink reads shroudlink_mods/*.dll and the DLLs in its direct subfolders (one per synced mod)
        ModKind::Shroudlink => server.join(SHROUDLINK_MODS),
        ModKind::Ember => server.join(EMBER_MODS),
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
        // its roots are Ember's own (ember/mods, kiln/disabled/ember): a folder with one library that no other engine claims
        ModKind::Ember => crate::engines::ember_library(dir).is_some(),
        // <mods>/enshwick/ is Enshwick's loader folder (mods.toml, enshwick.dll), never a mod
        ModKind::Enshwick if dir.file_name().is_some_and(|n| n.eq_ignore_ascii_case(ENSHWICK_LOADER_FOLDER)) => false,
        _ => crate::library::detect(dir).is_ok_and(|d| ModKind::of_detect(d.kind) == Some(kind)),
    }
}

/// An engine folder's identity. An Enshwick pack.toml is authoritative; invalid pack
/// metadata has no identity and must never fall through to mod.json or a folder alias.
pub fn identity(dir: &Path) -> (String, Option<String>, Option<String>) {
    let folder = dir.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
    let pack = is_enshwick_pack(dir);
    let m = if pack {
        let Some(m) = crate::engines::pack_toml(dir) else { return (String::new(), None, None); };
        Some(m)
    } else { read_json(&dir.join("mod.json")) };
    let s = |k: &str| m.as_ref().and_then(|v| v.get(k)).and_then(|x| x.as_str()).map(str::to_owned);
    let explicit = s("id");
    let id = if pack {
        let id = explicit.unwrap_or(folder);
        if !safe_enshwick_id(&id) { return (String::new(), None, None); }
        id
    } else { explicit.filter(|i| crate::manifest::is_mod_id(i)).unwrap_or(folder) };
    (id, s("version"), s("name"))
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
    name.unwrap_or_else(|| id.to_string())
}

fn read_json_checked(root: &Path, path: &Path) -> Result<Option<serde_json::Value>> {
    refuse_symlink_path(root, path)?;
    let text = match std::fs::read_to_string(path) {
        Ok(text) => text,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(io_at(path, "reading")(e)),
    };
    crate::jsonc::parse(text.trim_start_matches('\u{feff}')).map(Some)
}

fn validate_setting_id(id: &str) -> Result<()> {
    if !crate::manifest::is_mod_id(id) || id.chars().any(char::is_control) { return Err(Error::invalid("invalid engine mod id")); }
    Ok(())
}

fn validate_value(old: &serde_json::Value, value: &serde_json::Value, key: &str) -> Result<()> {
    use serde_json::Value as V;
    let valid = match (old, value) {
        (V::Object(a), V::Object(b)) => a.keys().eq(b.keys()) && a.iter().all(|(k, v)| validate_value(v, &b[k], k).is_ok()),
        (V::Number(n), V::Number(v)) => !(n.is_i64() || n.is_u64()) || v.is_i64() || v.is_u64(),
        (V::Array(_), V::Array(_)) => true,
        _ => std::mem::discriminant(old) == std::mem::discriminant(value),
    };
    if valid { Ok(()) } else { Err(Error::invalid(format!("{key}: expected the declared setting type"))) }
}

/// The managed root of a profile's config folder: `<data>/profiles/<pid>/config` -> `<data>`, a copied profile's
/// `<game>/kiln/profiles/<pid>/config` -> `<game>/kiln`. Links are refused from there down, so a link inside the data
/// dir or the game's kiln folder above the config folder is refused too. Any other shape is its own root.
fn config_root(config_dir: &Path) -> &Path {
    match config_dir.parent().and_then(Path::parent) {
        Some(c) if c.file_name().is_some_and(|n| n == "profiles") => c.parent().unwrap_or(config_dir),
        _ => config_dir,
    }
}

/// The managed root of a Library entry (`<data>/library/<kind>/<id>/<version>+<hash8>` -> `<data>`); any other shape
/// is its own root.
fn library_root(folder: &Path) -> &Path {
    match folder.ancestors().nth(3) {
        Some(lib) if lib.file_name().is_some_and(|n| n == "library") => lib.parent().unwrap_or(folder),
        _ => folder,
    }
}

/// The settings of one mod of an engine kind. `game_dir` finds ShroudForge's copy, `config_dir` (the profile's config
/// folder) Shroudtopia's file, `folder` is the mod's library folder (defaults for a Shroudtopia mod without values yet).
pub fn settings(kind: ModKind, id: &str, game_dir: Option<&Path>, config_dir: &Path, folder: Option<&Path>) -> Result<EngineSettings> {
    validate_setting_id(id)?;
    if let Some(folder) = folder { refuse_symlink_path(library_root(folder), &folder.join("mod.json"))?; }
    let none = |note: &str| EngineSettings {
        kind, id: id.to_string(), format: "none".into(), file: None,
        schema: serde_json::json!({ "type": "object", "properties": {} }), values: serde_json::json!({}), note: Some(note.into()),
    };
    match kind {
        ModKind::ShroudForge => {
            // The managed root of the folder: the game folder for a placed copy, else the data dir of the Library entry.
            let placed = game_dir.and_then(|g| placed_folder(kind, g, id).map(|p| (g, p))).filter(|(_, p)| p.is_dir());
            let Some((root, dir)) = placed.or_else(|| folder.map(|f| (library_root(f), f.to_path_buf()))) else {
                return Ok(none("activate the profile once: ShroudForge's settings live in its mods folder"));
            };
            refuse_symlink_path(root, &dir.join("mod.json"))?;
            refuse_symlink_path(root, &dir.join("extended.mod.json"))?;
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
            let doc = read_json_checked(config_root(config_dir), &file)?.unwrap_or_else(|| serde_json::json!({ "mods": {} }));
            if !doc.is_object() || doc.get("mods").is_some_and(|m| !m.is_object()) {
                return Err(Error::invalid("Shroudtopia settings and mods must be objects"));
            }
            let key = shroudtopia_key(&doc, id, folder);
            let mut values = doc.pointer(&format!("/mods/{}", key.replace('~', "~0").replace('/', "~1"))).cloned();
            if values.is_none() {
                // the defaults its manifest declares (kiln-shroudtopia writes them at the first start)
                values = folder.and_then(|f| read_json(&f.join("mod.json"))).and_then(|v| v.pointer("/shroudtopia/default").cloned());
            }
            let values = values.unwrap_or_else(|| serde_json::json!({ "active": true }));
            if !values.is_object() { return Err(Error::invalid("Shroudtopia mod settings must be an object")); }
            Ok(EngineSettings {
                kind, id: key, format: "shroudtopia.json".into(), file: Some(file.display().to_string()),
                schema: schema_from_values(&values), values, note: None,
            })
        }
        ModKind::Emf => Ok(none("EMF settings: use the EMF ini editor")),
        ModKind::Enshwick => Ok(none("Enshwick keeps each mod's settings per world (<save>-mod-settings/<id>/settings.toml): use the game's Mods screen or `enshwick config` on the server")),
        ModKind::Ember => Ok(none("Ember mods don't run yet: Ember has not published its mod ABI")),
        _ => Ok(none("this engine's mods declare no settings")),
    }
}

/// Saves one declared setting with type validation. The command caller refuses writes while the game runs.
/// ShroudForge values live in the owned placed copy; Shroudtopia values live in the effective profile config.
pub fn set_setting(kind: ModKind, id: &str, key: &str, value: &serde_json::Value, game_dir: Option<&Path>, config_dir: &Path,
                   folder: Option<&Path>) -> Result<EngineSettings> {
    validate_setting_id(id)?;
    let _lock = match (kind, game_dir) {
        (ModKind::ShroudForge, Some(game)) => Some(engine_lock(game, &game.join("kiln/state"))?),
        (ModKind::Shroudtopia, _) => Some(engine_lock(config_root(config_dir), config_dir)?),
        _ => None,
    };
    match kind {
        ModKind::ShroudForge => {
            let dir = game_dir.and_then(|g| placed_folder(kind, g, id)).filter(|p| p.is_dir())
                .ok_or_else(|| Error::not_found(format!("ShroudForge mod {id} is not in ShroudForge's mods folder: activate the profile first")))?;
            let game = game_dir.expect("placed game");
            let receipts = placements(game)?;
            if owned(game, &receipts, &dir, kind, id)?.is_none() {
                return Err(Error::new(ErrorCode::Refused, "ShroudForge settings belong to a player-owned folder"));
            }
            refuse_symlink_path(game, &dir.join("extended.mod.json.tmp"))?;
            crate::shroudforge::set_setting(&dir, key, value)?;
        }
        ModKind::Shroudtopia => {
            let cur = settings(kind, id, game_dir, config_dir, folder)?;
            let old = cur.values.get(key).ok_or_else(|| Error::invalid(format!("unknown setting {key}")))?;
            validate_value(old, value, key)?;
            let file = shroudtopia_file(config_dir);
            refuse_symlink_path(config_root(config_dir), &crate::util::with_suffix(&file, ".tmp"))?;
            let mut doc = read_json_checked(config_root(config_dir), &file)?.unwrap_or_else(|| serde_json::json!({}));
            if !doc.is_object() || doc.get("mods").is_some_and(|m| !m.is_object()) {
                return Err(Error::invalid("Shroudtopia settings and mods must be objects"));
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
    /// A present but unreadable or malformed state file must not look like a plugin that never ran.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
    /// The file's host path.
    pub file: String,
    /// This engine's own loader, when one is installed in the game, and what it means for the active profile's mods
    /// of the kind (`text`: one sentence for the launcher). None: no own loader of this engine is installed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub own_loader: Option<crate::own_loaders::OwnLoaderNote>,
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
        ModKind::Enshwick => "enshwick.json",
        ModKind::Shroudlink => "shroudlink.json",
        ModKind::Ember => "ember.json",
        _ => return None,
    })
}

/// Every engine plugin's state in `<game>/kiln/state/` (EMF, EML, ShroudForge, Greeneye, Flame, Shroudtopia, SET,
/// Enshwick, shroudlink, Ember).
pub fn status(game_dir: &Path) -> Vec<EngineStatus> {
    let view = crate::profile::read_game_profile(game_dir).ok().flatten().map(|f| crate::own_loaders::ProfileView::of_file(&f));
    let notes = crate::own_loaders::notes(game_dir, &crate::own_loaders::find(game_dir, view.as_ref()), view.as_ref());
    ModKind::ALL
        .into_iter()
        .filter_map(|k| {
            let f = game_dir.join("kiln").join("state").join(state_file(k)?);
            let (state, error) = match read_json_checked(game_dir, &f) {
                Ok(state) => (state, None),
                Err(e) => (None, Some(e.message)),
            };
            Some(EngineStatus {
                kind: k,
                plugin: plugin_of(k).unwrap_or_default().to_string(),
                state,
                error,
                file: f.display().to_string(),
                own_loader: notes.iter().find(|n| n.loader.kind == k).cloned(),
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
pub fn eml_backup(game_dir: &Path) -> Result<Vec<EmlBackup>> {
    let mut out = Vec::new();
    for exe in ["enshrouded", "enshrouded_server"] {
        let file = game_dir.join(format!("{exe}.kfc"));
        let bak = game_dir.join(format!("{exe}.kfc.bak"));
        refuse_symlink_path(game_dir, &file)?;
        refuse_symlink_path(game_dir, &bak)?;
        if !bak.exists() || !file.exists() { continue; }
        if !bak.is_file() || !file.is_file() { return Err(Error::invalid("EML backup and game data must be regular files")); }
        let same = crate::hash::sha256_file(&file)?.0 == crate::hash::sha256_file(&bak)?.0;
        out.push(EmlBackup { file: file.display().to_string(), backup: bak.display().to_string(), patched: !same });
    }
    Ok(out)
}

/// Restore through a streamed temporary sibling, preserving the current game file until the copy is complete.
/// The command caller refuses while the game runs.
pub fn eml_restore_vanilla(game_dir: &Path) -> Result<Vec<String>> {
    let backups = eml_backup(game_dir)?; // inspect every candidate before changing any file
    let mut done = Vec::new();
    for b in backups.into_iter().filter(|b| b.patched) {
        let file = Path::new(&b.file);
        let bak = Path::new(&b.backup);
        refuse_symlink_path(game_dir, file)?;
        refuse_symlink_path(game_dir, bak)?;
        let tmp = crate::library::TempDir::new(game_dir, "eml-restore")?;
        let staged = tmp.path().join("data.kfc");
        std::fs::copy(bak, &staged).map_err(io_at(bak, "copying"))?;
        if crate::hash::sha256_file(&staged)? != crate::hash::sha256_file(bak)? {
            return Err(Error::new(ErrorCode::HashMismatch, "EML backup changed while restoring"));
        }
        std::fs::File::open(&staged)?.sync_all()?;
        crate::util::rename_replace(&staged, file)?;
        done.push(b.file);
    }
    Ok(done)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn settings_files_are_checked_from_the_data_or_game_folder_down() {
        let p = Path::new;
        assert_eq!(config_root(p("/home/u/.local/share/kiln/profiles/p1/config")), p("/home/u/.local/share/kiln"));
        assert_eq!(config_root(p("/games/Enshrouded/kiln/profiles/p1/config")), p("/games/Enshrouded/kiln"));
        assert_eq!(config_root(p("/tmp/x/cfg")), p("/tmp/x/cfg"));
        assert_eq!(library_root(p("/d/kiln/library/shroudtopia/map/1.0.0+abcd1234")), p("/d/kiln"));
        assert_eq!(library_root(p("/tmp/x/map")), p("/tmp/x/map"));
        // a link inside the data dir above the config folder is refused (the old strict check refused it too)
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "config-root").unwrap();
        let data = tmp.path().join("kiln");
        std::fs::create_dir_all(tmp.path().join("elsewhere/p1/config")).unwrap();
        std::fs::create_dir_all(&data).unwrap();
        #[cfg(unix)]
        {
            std::os::unix::fs::symlink(tmp.path().join("elsewhere"), data.join("profiles")).unwrap();
            let cfg = data.join("profiles/p1/config");
            let err = settings(ModKind::Shroudtopia, "map", None, &cfg, None).unwrap_err();
            assert_eq!(err.code, ErrorCode::Refused);
        }
    }

    #[test]
    fn failed_receipt_write_rolls_back_copy_and_removal() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "engine-receipt-rollback").unwrap();
        let src = tmp.path().join("source");
        let dst = tmp.path().join("mods/test");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::create_dir_all(&dst).unwrap();
        std::fs::write(src.join("init.lua"), b"new").unwrap();
        std::fs::write(dst.join("init.lua"), b"old").unwrap();
        let marker = Marker { format: 1, kind: ModKind::Set, id: "test".into(), version: "1.0.0".into(), hash: crate::hash::dirhash(&src).unwrap() };
        let roots = Roots { data: tmp.path(), game: tmp.path() };
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, |_| Err(Error::invalid("injected receipt failure"))).is_err());
        assert_eq!(std::fs::read(dst.join("init.lua")).unwrap(), b"old");
        assert!(remove_copy(tmp.path(), &dst, || Err(Error::invalid("injected receipt failure"))).is_err());
        assert_eq!(std::fs::read(dst.join("init.lua")).unwrap(), b"old");
        std::fs::remove_dir_all(&dst).unwrap();
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, |_| Err(Error::invalid("injected receipt failure"))).is_err());
        assert!(!dst.exists());
        std::fs::create_dir_all(&dst).unwrap();
        std::fs::write(dst.join("init.lua"), b"recover me").unwrap();
        let error = remove_copy(tmp.path(), &dst, || {
            std::fs::create_dir_all(&dst).unwrap();
            std::fs::write(dst.join("new-player-file"), b"keep too").unwrap();
            Err(Error::invalid("injected receipt failure"))
        }).unwrap_err();
        let recovery = PathBuf::from(error.detail.unwrap()["recovery"].as_str().unwrap());
        assert_eq!(std::fs::read(recovery.join("removed/init.lua")).unwrap(), b"recover me");
        assert_eq!(std::fs::read(dst.join("new-player-file")).unwrap(), b"keep too");
    }

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
            assert!(k.is_engine());
            assert!(default_side(k) == (Side::Server, Clients::None) || runs_yet(k), "{k:?}");
        }
        // every engine kind but EMF (Kiln's own emf kind) has an importer
        for k in ModKind::ALL.into_iter().filter(|k| k.is_engine() && *k != ModKind::Emf) {
            assert!(IMPORTED.contains(&k), "{k:?}");
        }
    }
}
