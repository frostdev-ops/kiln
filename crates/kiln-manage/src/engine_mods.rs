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
//! - [`eml_backup`]: read-only shared archive differences. [`eml_restore_vanilla`] refuses legacy direct restores;
//!   only the separate archive writer can verify ownership and safely hand off loader state.

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
/// Where [`place`] keeps the files a mod wrote into its placed folder while the mod is not placed (switched off, gone
/// from the profile, or a file the new version ships itself): `<game>/kiln/kept-mod-files/<kind>/<id>/`, relative to
/// the game. The next placement of the same kind and id puts them back.
pub const KEPT_MOD_FILES: &str = "kiln/kept-mod-files";

/// The settings files an engine documents that its mods write into their own folder at run time. A placed copy whose
/// file of this name differs from the library's is still Kiln's copy: the changed file is the player's and is carried
/// over like any file the mod added. Names are top-level files of the mod folder, case ignored.
/// SET 0.2.0: `Settings.define` saves `settings.json` (kiln-set's `modsettings`; `.tmp`/`.bad` are new files anyway).
/// ShroudForge keeps its switch and settings in `extended.mod.json`, which [`placement_files`] compares without them.
/// EML mods (e.g. Auto Loot Plus) use `config.txt` and `config.ini`; their code and `native-plugin.ini` are not settings.
/// EMF, Greeneye, Flame and Shroudtopia document none (files their mods add are kept all the same).
pub fn settings_files(kind: ModKind) -> &'static [&'static str] {
    match kind {
        ModKind::Eml => &["config.txt", "config.ini"],
        ModKind::Set => &["settings.json"],
        _ => &[],
    }
}

fn is_settings_file(kind: ModKind, rel: &str) -> bool {
    settings_files(kind).iter().any(|s| s.eq_ignore_ascii_case(rel))
}

/// shroudlink's mods folder, relative to the game (its DLLs directly, and Kiln's synced folders one level down).
pub const SHROUDLINK_MODS: &str = "shroudlink_mods";
/// Greeneye Loader's mods folder, relative to the game. Greeneye mods always run from Kiln's copy there
/// ([`game_path_of`]): Greeneye's API gives a mod its folder as `...\greeneye\<id>\`, and mods keep their settings,
/// logs and per-world data in it (The Worst Map Mod: minimap_config.ini, looted_chests.ini, unlock_map_markers.log). A
/// content-addressed Library entry is neither where players and mod authors look for those files nor a place that
/// survives the next version.
pub const GREENEYE_MODS: &str = "greeneye";
/// Where a Flame mod reads its own files, relative to the game: `<game>\mods\<id>\`, whatever folder its DLL was loaded
/// from (Creative Mode 3.2.0 run by kiln-flame from the Library logs "Mod directory: ...\Enshrouded\mods\creative" and
/// looks for `sigs\<build key>.json` and `symbols\` there). Flame mods always run from Kiln's copy there
/// ([`game_path_of`]), and write their logs, notice and settings into it.
pub const FLAME_MODS: &str = "mods";

/// The game folder a kind's mods always run from, as Kiln's copy `<folder>/<library id>/` ([`place`]), with or without
/// that engine's own loader: Greeneye ([`GREENEYE_MODS`]) and Flame ([`FLAME_MODS`]). None for the other kinds.
pub fn run_folder(kind: ModKind) -> Option<&'static str> {
    match kind {
        ModKind::Greeneye => Some(GREENEYE_MODS),
        ModKind::Flame => Some(FLAME_MODS),
        _ => None,
    }
}
/// Ember's mods folder, relative to the server: `ember/mods/<mod>/`, one library each.
pub const EMBER_MODS: &str = "ember/mods";
/// Enshwick's loader keeps its own files in `<game>/mods/enshwick/`: no mod may take that folder name.
pub const ENSHWICK_LOADER_FOLDER: &str = "enshwick";

/// The kinds with a Library importer: every engine kind (EMF's mods are Kiln's own `emf` kind).
pub const IMPORTED: [ModKind; 10] = [ModKind::Eml, ModKind::Xhl, ModKind::ShroudForge, ModKind::Set, ModKind::Greeneye, ModKind::Flame,
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
        ModKind::Eml | ModKind::Xhl | ModKind::ShroudForge | ModKind::Flame | ModKind::Enshwick => (Side::Both, Clients::Mod),
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

/// The Kiln id an engine mod gets in the Library, from the id its own manifest gives (`manifest_id`).
///
/// Engine manifests have no id rule of Kiln's: real ids are `HoardersHelper`, `Less Dense Shroud Fog`, `antisocialian's
/// tweaks`. (Enshwick keeps the strict id: it copies its mods into a folder named by it. ShroudForge's packages are
/// placed under the Library id: ShroudForge finds them by their mod.json id.) A valid Kiln id is kept; any other becomes its slug ([`crate::library::id_from_name`]). The mod's files
/// are never changed: the plugin reads the manifest's own id. Two different manifest ids never share a library id:
/// when the slug belongs to another manifest id already (`Ember` and `ember`, `a b` and `a-b`), a short hash of the
/// manifest id is added. Returns the library id.
fn library_id_for(dirs: &DataDirs, kind: ModKind, manifest_id: &str) -> String {
    let base = if crate::manifest::is_mod_id(manifest_id) { manifest_id.to_string() } else { crate::library::id_from_name(manifest_id) };
    let mut candidate = base.clone();
    for round in 0u32.. {
        match crate::library::id_holder(dirs, kind, &candidate) {
            None => return candidate,
            Some(holder) if holder == manifest_id => return candidate,
            Some(_) => {
                let h = crate::hash::sha256_hex(format!("{manifest_id}\n{round}").as_bytes());
                // at most 64 characters in all: `<slug>-<6 hex digits>`
                let keep: String = base.chars().take(57).collect();
                candidate = format!("{}-{}", keep.trim_end_matches(['-', '.', '_']), &h[..6]);
            }
        }
    }
    unreachable!()
}

/// The importer of every engine kind: the folder (or the lone mod DLL, wrapped into a folder with the manifest its
/// engine needs) goes into `library/<kind>/<id>/<version>+<hash8>/`. The id is a Kiln id made from the manifest's
/// ([`library_id_for`]); the manifest's own id stays in the import record (`manifestId`).
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
            Err(e) => return skip(ctx.report, format!("not a ShroudForge package Kiln can run: {}", e.message)),
        }
    }
    if id.is_empty() && d.id.is_none() {
        id = crate::library::id_from_name(name.as_deref().unwrap_or_default());
    }
    if id.is_empty() || id.chars().any(char::is_control) || id.chars().count() > 256 {
        return skip(ctx.report, "its manifest gives no usable id (empty, with control characters, or longer than 256): ask the author for a fixed version".into());
    }
    let manifest_id = id;
    if kind == ModKind::ShroudForge && !crate::shroudforge::is_package_id(&manifest_id) {
        // ShroudForge finds a package by its mod.json id (the folder name does not matter to it), so Kiln places it
        // under its Library id; an id ShroudForge's own rule refuses would never load
        return skip(ctx.report, format!("the package id {manifest_id:?} is not a valid ShroudForge package id (ASCII letters, digits, . _ -, at most 80): ShroudForge would not load it, so the author has to change it"));
    }
    if kind == ModKind::Enshwick {
        // Enshwick copies a mod into <game>/mods/<id>/ itself and holds its authors to one id shape: no renaming here.
        if !crate::manifest::is_mod_id(&manifest_id) {
            return skip(ctx.report, format!("the pack id {manifest_id:?} is not a valid mod id: Enshwick packs use lower case letters, digits, . _ - (at most 64)"));
        }
        if manifest_id.eq_ignore_ascii_case(ENSHWICK_LOADER_FOLDER) {
            // kiln-enshwick copies the mod into <game>/mods/<id>/, and mods/enshwick/ is the loader's own folder
            return skip(ctx.report, "the id \"enshwick\" is Enshwick's own loader folder, not a mod".into());
        }
        if !safe_enshwick_id(&manifest_id) {
            return skip(ctx.report, format!("the pack id {manifest_id:?} is not a safe Windows folder name"));
        }
        if src.is_dir() && src.join("pack.toml").is_file() && crate::engines::pack_toml(src).is_none() {
            return skip(ctx.report, "its pack.toml is invalid or uses metadata Kiln can't read".into());
        }
    }
    if kind == ModKind::Xhl && (!src.is_dir() || !is_game_folder_name(&manifest_id)) {
        return skip(ctx.report, "add the original XHL-Core plugin folder with its native-plugin.ini, using a safe Windows folder name".into());
    }
    let xhl_folder = (kind == ModKind::Xhl).then(|| manifest_id.clone());
    let id = library_id_for(ctx.dirs, kind, &manifest_id);
    let manifest_id = (manifest_id != id).then_some(manifest_id);
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
            // XHL's own mod.json (since 2026-11) only names the mod; an EML part (src/mod.lua) makes it an EML mod
            ModKind::Xhl => has_xhl_plugin_ini(&work) && !work.join("src").join("mod.lua").exists(),
            _ => true,
        };
        if !found {
            let why = match kind {
                ModKind::Ember => "an Ember mod folder holds exactly one library (.dll), and no other engine's mod DLL",
                ModKind::Xhl => "the XHL companion must retain native-plugin.ini and must not be an EML package (src/mod.lua)",
                ModKind::Enshwick => "invalid, unsupported or changed pack.toml, or no Enshwick mod DLL",
                _ => "no DLL exporting shroudlink_mod_v2 or shroudlink_mod_v1",
            };
            return skip(ctx.report, why.to_string());
        }
        let required = match kind {
            ModKind::Greeneye | ModKind::Shroudtopia => Some("mod.json"),
            ModKind::Flame => Some("flame.json"),
            _ => None,
        };
        if required.is_some_and(|m| !work.join(m).is_file()) {
            let dll = dll_of(&work, kind)?.ok_or_else(|| Error::invalid(format!("no {} mod DLL in it", kind.label())))?;
            let file = dll.file_name().and_then(|n| n.to_str()).ok_or_else(|| Error::invalid("DLL name must be UTF-8"))?;
            size += write_dll_manifest(kind, &work, &id, &version, file)?;
        }
        size
    } else {
        if kind == ModKind::Ember {
            // nothing in a lone DLL says it is Ember's; detection only names folders in ember/mods/
            return skip(ctx.report, "add the Ember mod's folder (ember/mods/<mod>/), not the DLL alone".into());
        }
        std::fs::create_dir_all(&work).map_err(io_at(&work, "creating"))?;
        let file = src.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| "mod.dll".into());
        let size = std::fs::copy(src, work.join(&file)).map_err(io_at(src, "copying"))?;
        size + write_dll_manifest(kind, &work, &id, &version, &file)?
    };
    let hash = crate::hash::dirhash(&work)?;
    let (side, clients) = side_of(kind, &work);
    let game_folder = if let Some(folder) = xhl_folder {
        Some((folder, "requires XHL-Core: only that separately installed host runs it; Kiln never loads this plugin DLL itself".into()))
    } else if kind == ModKind::Eml && src.is_dir() { eml_game_folder(src) } else { None };
    if let Some((folder, why)) = &game_folder {
        let shown = name.clone().unwrap_or_else(|| folder.clone());
        ctx.report.note(&d.path, format!("{shown}: {why}, so Kiln copies it into the game's mods\\{folder} folder while a profile with it is active"));
    }
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
        manifest_id,
        game_folder: game_folder.map(|(f, _)| f),
    };
    crate::library::commit_folder(ctx.dirs, &work, rec, ctx.report)
}

/// XHL-Core's plugin description, in the folder of each XHL mod (`mods/<name>/native-plugin.ini`).
pub const XHL_PLUGIN_INI: &str = "native-plugin.ini";

/// A folder name Kiln may create under `<game>/mods/`: one plain component a Windows game folder can hold. The same rule
/// as the native core's `../mods/<folder>` check (native/core/src/profile.cpp placed_game_path) and the profile schema's
/// pattern; schema/fixtures/profile/game-folder-names.json holds the names all three are tested with. The part before
/// the first dot is the name Windows (a device) and Enshwick's loader folder are compared with.
pub fn is_game_folder_name(name: &str) -> bool {
    let stem = name.split('.').next().unwrap_or_default().to_ascii_lowercase();
    let device = matches!(stem.as_str(), "con" | "prn" | "aux" | "nul")
        || (stem.len() == 4 && (stem.starts_with("com") || stem.starts_with("lpt")) && stem.as_bytes()[3].is_ascii_digit());
    !name.is_empty() && name.chars().count() <= 128 && !name.starts_with('.') && !name.ends_with(['.', ' '])
        && !name.chars().any(|c| c.is_control() || "/\\:*?\"<>|".contains(c)) && !device
        && stem != ENSHWICK_LOADER_FOLDER
}

/// Does a native DLL read this EML mod in the game's `mods` folder? `src` is the mod folder as dropped (an unpacked
/// archive's entry). Some((folder name, why)) when the mod's own folder holds a DLL or XHL-Core's `native-plugin.ini`
/// (the XHL mods: XHL-Core loads the plugin from `<game>/mods/<name>/`), or when its package carries a DLL mod under a
/// proxy name next to the `mods` folder that holds it (Auto Loot Plus: its dinput8.dll reads and writes
/// `mods/auto-loot-plus/config.txt` and needs the EML part there). Such a mod is placed there at activation
/// ([`place`]), and kiln-eml runs that copy, so the DLL and the Lua read the same files.
///
/// The folder is named as the package names it, without a browser's or an unpacker's copy suffix ("XHL-Auto-Loot (1)"
/// dropped on its own); a mod at an archive's root (no folder name of its own) takes its manifest's id. A folder the
/// package names under a name Kiln cannot create there (`Enshwick.Tweaks`) is not a game folder: placing it under
/// another name would not be the folder its DLL reads.
pub fn eml_game_folder(src: &Path) -> Option<(String, String)> {
    let own = src.file_name()?.to_str()?;
    let named = crate::library::strip_copy_suffix(own).to_string();
    let folder = if is_game_folder_name(&named) { named }
        else if own.starts_with(".kiln-unpack-") { manifest_id_raw(src).filter(|id| is_game_folder_name(id))? }
        else { return None };
    let files: Vec<PathBuf> = std::fs::read_dir(src).ok()?.flatten().map(|e| e.path()).filter(|p| p.is_file()).collect();
    if files.iter().any(|p| p.file_name().is_some_and(|n| n.eq_ignore_ascii_case(XHL_PLUGIN_INI))) {
        return Some((folder, "it is also an XHL-Core plugin (native-plugin.ini), which XHL-Core loads from the game's mods folder".into()));
    }
    if let Some(dll) = files.iter().find(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("dll"))) {
        let file = dll.file_name()?.to_string_lossy().into_owned();
        return Some((folder, format!("it carries {file}, which is loaded from the game's mods folder")));
    }
    let parent = src.parent()?;
    if !parent.file_name().is_some_and(|n| n.eq_ignore_ascii_case("mods")) { return None; }
    let package = parent.parent()?;
    // A game folder is no package: Steam's steam_api64.dll (or any proxy-named DLL a player put there) says nothing
    // about a hand-installed mod in its mods folder. The package's DLL is the one Library import makes its native mod.
    if [crate::game::CLIENT_EXE, crate::game::SERVER_EXE].iter().any(|exe| package.join(exe).exists()) { return None; }
    let dll = crate::library::native_bundle_proxy(package)?;
    let file = dll.file_name()?.to_string_lossy().into_owned();
    Some((folder, format!("its package's {file} reads it from the game's mods folder")))
}

/// Does `dir` hold XHL-Core's `native-plugin.ini`, spelled in any case (Windows and XHL-Core do not care)?
pub fn has_xhl_plugin_ini(dir: &Path) -> bool {
    std::fs::read_dir(dir).into_iter().flatten().flatten()
        .any(|e| e.file_name().to_str().is_some_and(|n| n.eq_ignore_ascii_case(XHL_PLUGIN_INI)) && e.path().is_file())
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
    /// ShroudForge mods whose `extended.mod.json` switch changed, and EML copies in ShroudForge's mods folder whose
    /// ShroudForge switch Kiln turned on (an older Kiln's copy without it, or one switched off in the game).
    pub switched: Vec<String>,
    /// A folder with the mod's name that the player put there: left as it is. For the own-loader kinds also a
    /// player's folder holding the same mod (the same manifest id): the own loader runs that one.
    pub own: Vec<String>,
    /// Kiln's copy whose library files changed since it was placed (the player edited the mod), or that activation
    /// cannot inspect or carry: left as it is, neither refreshed nor removed. Files the mod added and its settings files
    /// do not count. Each one is in [`PlaceReport::retained`] too, with its path and reason.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub changed: Vec<String>,
    /// The copies of [`PlaceReport::changed`]: where they are and why they stay. The mod's own loader may still run
    /// them, whatever the profile says (`mayRun`), as for [`unplace`].
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub retained: Vec<RetainedPlacement>,
    /// Per-mod notes that are not about kept files: a setting value a new ShroudForge version refuses, earlier
    /// settings that could not be read. One sentence each, `<kind>:<id>: ...`.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub warnings: Vec<String>,
    /// One sentence per entry of [`PlaceReport::own`]: the player's own folder that serves it. An activation passes
    /// them on with its notes; a reconcile of an unchanged profile does not repeat them.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub own_notes: Vec<String>,
    /// The notes of [`PlaceReport::own_notes`] about a player's own folder that may still run although the profile has
    /// its mod off (Kiln never switches it): the launcher raises these even with its notes about mod folders off.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub safety_notes: Vec<String>,
    /// Files a mod wrote into its placed folder (its settings): one sentence per mod when they were moved to
    /// [`KEPT_MOD_FILES`] (the mod was removed, or its new version ships a file of the same name) or put back from
    /// there. Files carried into a new version of the copy are not listed: they stay where they were.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub kept_files: Vec<String>,
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
    /// Exact SF extension written by Kiln, including its last profile switch. Unlike `files`, this does not
    /// normalize settings: uninstall must not erase player edits. Old receipts have no proof and are retained.
    /// An EML copy that ships the file has it too, switched or not (ShroudForge may run the copy later, and the values
    /// the player then changes are the player's); for an older EML receipt without it see [`sf_values_changed`].
    #[serde(default, skip_serializing_if = "Option::is_none")]
    extension: Option<String>,
    /// An EML mod or non-executing native settings companion placed in `<game>/mods/` for a DLL that reads it there.
    /// The DLL may write settings into the copy, which Kiln keeps across updates ([`settings_changed`]).
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    game_folder: bool,
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
    #[cfg(test)]
    if tests::FAIL_SAVE_PLACEMENTS.with(|f| f.replace(false)) { return Err(Error::invalid("injected receipt write failure")); }
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
                let hash = if rel == "extended.mod.json" { extension_hash(root, &path, kind)? } else { crate::hash::sha256_file(&path)?.0 };
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

/// The inventory hash of a copy's `extended.mod.json` ([`placement_files`]): ShroudForge's file of a ShroudForge mod,
/// and of an EML mod (ShroudForge runs those from its mods folder too, by the same switch), without its switch and
/// settings, which are the player's to change. An EML mod's file that is no JSON object is hashed as it is (kiln-eml
/// and EML do not read it; ShroudForge refuses it).
fn extension_hash(root: &Path, path: &Path, kind: ModKind) -> Result<String> {
    let doc = match kind {
        ModKind::ShroudForge => Some(read_json_checked(root, path)?.ok_or_else(|| Error::invalid("missing SF extension"))?),
        ModKind::Eml => read_json_checked(root, path).ok().flatten().filter(serde_json::Value::is_object),
        _ => None,
    };
    let Some(mut doc) = doc else { return Ok(crate::hash::sha256_file(path)?.0) };
    let obj = doc.as_object_mut().ok_or_else(|| Error::invalid("SF extension must be an object"))?;
    obj.remove("enabled");
    obj.remove("settings");
    Ok(crate::hash::sha256_hex(&serde_json::to_vec(&doc)?))
}

/// [`extension_hash`] of a copy's `extended.mod.json` as it was before ShroudForge's own Modloader wrote it. Its in-game
/// menu (a setting saved, the switch flipped) adds `targets` with ShroudForge's default when the file has none:
/// ["client"], or ["client", "server"] for launcher EML (kiln-shroudforge modloader update_mod_configuration.rs,
/// `prepare_existing_extension`). That default is what ShroudForge reads from a file without `targets`, so the file
/// still says what Kiln placed. None: the file holds no `targets` equal to that default (or is no JSON object).
fn extension_hash_untargeted(root: &Path, path: &Path, kind: ModKind) -> Option<String> {
    if !matches!(kind, ModKind::ShroudForge | ModKind::Eml) { return None; }
    let mut doc = read_json_checked(root, path).ok().flatten()?;
    let obj = doc.as_object_mut()?;
    let eml = obj.get("launcher").and_then(|l| l.as_str()) == Some("EML");
    let default = if eml { serde_json::json!(["client", "server"]) } else { serde_json::json!(["client"]) };
    if obj.get("targets") != Some(&default) { return None; }
    obj.remove("targets");
    obj.remove("enabled");
    obj.remove("settings");
    Some(crate::hash::sha256_hex(&serde_json::to_vec(&doc).ok()?))
}

/// An EML copy's receipt as [`placement_files`] lists it now. Before ShroudForge's switch was part of Kiln's EML copies
/// (0.2.0-dev.24), a receipt listed a shipped `extended.mod.json` by the file's own hash; while the file is still the one
/// Kiln placed, that entry means the same, and Kiln's bytes are its proof ([`Placement::extension`]).
fn current_receipt(dst: &Path, r: &Placement) -> Placement {
    const EXT: &str = "extended.mod.json";
    let path = dst.join(EXT);
    if r.marker.kind != ModKind::Eml || r.extension.is_some() { return r.clone(); }
    let Some(listed) = r.files.get(EXT) else { return r.clone() };
    if crate::hash::sha256_file(&path).ok().map(|h| h.0).as_ref() != Some(listed) { return r.clone(); }
    let (Ok(hash), Ok(text)) = (extension_hash(dst, &path, ModKind::Eml), std::fs::read_to_string(&path)) else { return r.clone() };
    let mut r = r.clone();
    r.files.insert(EXT.into(), hash);
    r.extension = Some(text);
    r
}

/// The settings of a ShroudForge `extended.mod.json` text by key (`settings.<key>`: its metadata and, with `values`, its
/// value; a shorthand value counts as a value); None when it is no JSON object.
fn sf_settings_in(text: &str, values: bool) -> Option<std::collections::BTreeMap<String, serde_json::Value>> {
    let doc = crate::jsonc::parse(text.trim_start_matches('\u{feff}')).ok().filter(serde_json::Value::is_object)?;
    Some(doc.get("settings").and_then(|s| s.as_object()).into_iter().flatten().map(|(k, e)| {
        let mut e = e.clone();
        if !values {
            match e.as_object_mut() {
                Some(o) => { o.remove("value"); }
                None => e = serde_json::Value::Null,
            }
        }
        (k.clone(), e)
    }).collect())
}

/// Whether the ShroudForge settings (`extended.mod.json` `settings`) of Kiln's EML copy `dst` are not the ones Kiln
/// placed: their values and metadata, or with `values` false their metadata only. A receipt that records Kiln's own
/// file tells ([`Placement::extension`]). An older one that lists the file without it (an earlier 0.2.0-dev.24 placed
/// the copy while ShroudForge did not run it) is compared with the Library's file, the one Kiln copied; a file whose
/// Library entry cannot be read counts as changed. `dirs`: Kiln's data dir.
fn sf_settings_changed(dirs: &DataDirs, dst: &Path, r: &Placement, values: bool) -> bool {
    const EXT: &str = "extended.mod.json";
    let Ok(now) = std::fs::read_to_string(dst.join(EXT)) else { return false };
    let placed = match current_receipt(dst, r).extension {
        Some(placed) => placed,
        None if r.files.contains_key(EXT) => {
            let m = &r.marker;
            let library = crate::library::path_of(dirs, &crate::library::LibraryRef {
                kind: m.kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() });
            match library.ok().and_then(|l| std::fs::read_to_string(l.join(EXT)).ok()) {
                Some(shipped) => shipped,
                None => return true,
            }
        }
        None => return false,
    };
    sf_settings_in(&now, values) != sf_settings_in(&placed, values)
}

/// Kiln's EML copy `dst` holds ShroudForge settings that are not the ones Kiln placed ([`sf_settings_changed`]): values
/// the player changed in the game or Kiln carried from an earlier version, or metadata the player edited by hand. They
/// are the player's (the receipt lists the file without them).
fn sf_values_changed(dirs: &DataDirs, dst: &Path, r: &Placement) -> bool {
    sf_settings_changed(dirs, dst, r, true)
}

/// The player's files of Kiln's EML copy `dst` to carry to its next version (`player`), with its `extended.mod.json`
/// when the player edited a setting's metadata there: the values go over by themselves (the caller's `kept`), and a
/// file the new version ships itself goes to [`kept_dir`] rather than being overwritten ([`carry_in`]).
fn with_edited_extension(dirs: &DataDirs, dst: &Path, receipt: &Placement, mut player: Vec<String>) -> Vec<String> {
    const EXT: &str = "extended.mod.json";
    if receipt.marker.kind == ModKind::Eml && !player.iter().any(|p| p == EXT) && sf_settings_changed(dirs, dst, receipt, false) {
        player.push(EXT.into());
    }
    player
}

/// Sets ShroudForge's switch of Kiln's EML copy `dst` (in ShroudForge's mods folder) to `on`, writing the file
/// ShroudForge's own `ensure_eml_extension` writes when the copy has none (an older Kiln placed it without). The receipt
/// advances with Kiln's own write when it recorded the file as it is now, or the copy had none: the file is part of what
/// Kiln placed. A file holding the player's changes gets the switch and keeps its earlier record, as a ShroudForge
/// mod's does ([`switch_copy`]). Ok(true) when the file changed.
fn switch_eml_copy(game: &Path, receipts: &mut Vec<Placement>, dst: &Path, on: bool) -> Result<bool> {
    let ext = dst.join("extended.mod.json");
    refuse_symlink_path(game, &dst.join("extended.mod.json.tmp"))?;
    refuse_symlink_path(game, &ext)?;
    let m = crate::shroudforge::read_mod(dst)?;
    if m.extended && m.enabled == on && m.repair.is_empty() { return Ok(false); }
    let before = if m.extended { Some(std::fs::read_to_string(&ext).map_err(io_at(&ext, "reading"))?) } else { None };
    let index = receipts.iter().position(|r| r.path == dst);
    let ours = index.map(|i| current_receipt(dst, &receipts[i])).filter(|r| match &before {
        Some(text) => r.extension.as_deref() == Some(text.as_str()),
        None => !r.files.contains_key("extended.mod.json"),
    });
    crate::shroudforge::set_enabled(dst, on)?;
    if let (Some(i), Some(mut receipt)) = (index, ours) {
        let mut next = receipts.clone();
        let saved = (|| {
            receipt.files.insert("extended.mod.json".into(), extension_hash(dst, &ext, ModKind::Eml)?);
            receipt.extension = Some(std::fs::read_to_string(&ext).map_err(io_at(&ext, "reading"))?);
            next[i] = receipt;
            save_placements(game, &next)
        })();
        if let Err(e) = saved {
            let back = match &before {
                Some(text) => crate::util::write_atomic_exclusive(&ext, text.as_bytes()),
                None => std::fs::remove_file(&ext).map_err(io_at(&ext, "removing")),
            };
            if let Err(back) = back {
                return Err(Error::new(e.code, format!("{}; the previous ShroudForge switch could not be restored: {}", e.message, back.message)));
            }
            return Err(e);
        }
        *receipts = next;
    }
    Ok(true)
}

/// [`switch_eml_copy`] for a copy activation keeps or has just placed: a copy whose switch changed is in
/// [`PlaceReport::switched`]; one that cannot be switched (ShroudForge cannot read the package, a write failed) does not
/// run under ShroudForge, and the result says so.
fn switch_on_eml_copy(game: &Path, receipts: &mut Vec<Placement>, rep: &mut PlaceReport, tag: String, dst: &Path) {
    match switch_eml_copy(game, receipts, dst, true) {
        Ok(true) => rep.switched.push(tag),
        Ok(false) => {}
        Err(e) => rep.warnings.push(format!("{tag}: ShroudForge's switch in {} could not be turned on ({}), so ShroudForge does not run it",
            dst.display(), reason_at(dst, e.message))),
    }
}

/// A copy of an EML mod the profile no longer runs that holds the player's ShroudForge settings ([`sf_values_changed`]):
/// it stays where it is, switched off in ShroudForge's file so ShroudForge does not run it either, and the result says
/// why. Some(()) when it was kept so.
fn keep_sf_values(dirs: &DataDirs, game: &Path, receipts: &mut Vec<Placement>, rep: &mut PlaceReport, tag: String, dst: &Path) -> Option<()> {
    if !sf_values_changed(dirs, dst, receipts.iter().find(|r| r.path == dst)?) { return None; }
    let off = match switch_eml_copy(game, receipts, dst, false) {
        Ok(_) => "Kiln switched it off in ShroudForge's settings".to_string(),
        Err(e) => format!("Kiln could not switch it off in ShroudForge's settings ({}), so ShroudForge may still run it", reason_at(dst, e.message)),
    };
    retain_off(rep, tag, dst, format!("its ShroudForge settings (extended.mod.json) were changed since Kiln placed it (values set in the game, or an edit), so Kiln keeps the folder and {off}. \
        Switch the mod on in the profile to use them again, or remove the folder by hand to drop them"), true);
    Some(())
}

/// What a placed copy holds beyond the library copy its receipt lists: the files and folders the mod or the player
/// added (`<rel>` for a file, `<rel>/` for a folder) and the kind's [`settings_files`] that changed, in path order (a
/// folder before what is in it). None: a file of the library copy changed or went missing (not a settings file).
/// `now` and `listed` are file inventories (relative path to content hash; `<rel>/` with "" for folders).
pub(crate) fn player_entries_of(now: std::collections::BTreeMap<String, String>, listed: &std::collections::BTreeMap<String, String>,
                                kind: ModKind) -> Option<Vec<String>> {
    for (rel, hash) in listed {
        if now.get(rel) != Some(hash) && !is_settings_file(kind, rel) {
            return None;
        }
    }
    Some(now.into_iter().filter(|(rel, hash)| listed.get(rel) != Some(hash)).map(|(rel, _)| rel).collect())
}

fn player_entries(dst: &Path, kind: ModKind, receipt: &Placement) -> Result<Option<Vec<String>>> {
    const EXT: &str = "extended.mod.json";
    let mut now = placement_files(dst, kind)?;
    // ShroudForge's own menu wrote the file and added its default `targets`: still the file Kiln placed
    if let (Some(listed), Some(hash)) = (receipt.files.get(EXT), now.get(EXT))
        && listed != hash
        && extension_hash_untargeted(dst, &dst.join(EXT), kind).as_ref() == Some(listed)
    {
        now.insert(EXT.into(), listed.clone());
    }
    Ok(player_entries_of(now, &receipt.files, kind))
}

/// A copy [`place`] made, as its receipt says, with what the mod wrote into it since ([`player_entries`]).
#[derive(Clone)]
struct Owned {
    receipt: Placement,
    player: Vec<String>,
}

/// `game` is the managed root `dst` lies under. A copy is Kiln's when its receipt and marker match and every library
/// file is unchanged (settings files excepted); what the mod added is the player's and does not change that.
fn owned(game: &Path, entries: &[Placement], dst: &Path, kind: ModKind, id: &str) -> Result<Option<Owned>> {
    refuse_symlink_path(game, dst)?;
    if !dst.exists() { return Ok(None); }
    let Some(receipt) = entries.iter().find(|r| r.path == dst && r.marker.kind == kind && r.marker.id == id) else { return Ok(None) };
    let receipt = &current_receipt(dst, receipt);
    let valid = receipt.marker.format == 1 && crate::manifest::is_mod_id(&receipt.marker.id)
        && marker(dst).as_ref() == Some(&receipt.marker);
    let player = if valid { player_entries(dst, kind, receipt)? } else { None };
    let Some(player) = player else {
        return Err(Error::new(ErrorCode::Refused, format!("{}: managed copy changed; refusing to replace or remove player files", dst.display())));
    };
    Ok(Some(Owned { receipt: receipt.clone(), player }))
}

/// `<game>/kiln/kept-mod-files/<kind>/<id>`.
pub(crate) fn kept_dir(game: &Path, kind: ModKind, id: &str) -> PathBuf {
    game.join(KEPT_MOD_FILES).join(kind.as_str()).join(id)
}

/// The file entries of [`player_entries`] (folders only matter through the files in them, and empty ones are dropped).
fn file_entries(entries: &[String]) -> impl Iterator<Item = &String> {
    entries.iter().filter(|e| !e.ends_with('/'))
}

/// "settings.json, notes.txt and 3 more".
fn some_names(files: &[String]) -> String {
    let shown: Vec<&str> = files.iter().take(3).map(String::as_str).collect();
    match files.len() {
        0 => String::new(),
        n if n <= 3 => shown.join(", "),
        n => format!("{} and {} more", shown.join(", "), n - 3),
    }
}

/// Never discard a previous retained value, including a directory where the newer mod wrote a file (or vice versa).
/// Keep both beside each other; the latest uses the original name. These backups are ordinary player files too.
fn park_kept(game: &Path, path: &Path) -> Result<()> {
    refuse_symlink_path(game, path)?;
    let name = path.file_name().ok_or_else(|| Error::invalid("no kept file name"))?.to_string_lossy();
    for n in 1..=1000 {
        let backup = path.with_file_name(format!("{name}.kept-{n}"));
        refuse_symlink_path(game, &backup)?;
        if !backup.exists() {
            return std::fs::rename(path, &backup).map_err(io_at(path, "keeping earlier player file"));
        }
    }
    Err(Error::new(ErrorCode::Refused, "too many earlier retained files; no player files were removed"))
}

/// Copies the player's `entries` from `from` into `to` (both under `game`). Only a staged Library copy may be
/// overwritten. When retaining files, conflicting earlier player data is parked as `.kept-N`, never deleted.
fn copy_entries(game: &Path, from: &Path, to: &Path, entries: &[&String], retaining: bool) -> Result<()> {
    for rel in entries {
        let src = from.join(rel.as_str());
        refuse_symlink_path(game, &src)?;
        // Inspect parents first: checking the full path through an existing file would fail with NotADirectory.
        let mut parent = to.to_path_buf();
        refuse_symlink_path(game, &parent)?;
        for part in Path::new(rel).parent().into_iter().flat_map(Path::components) {
            parent.push(part);
            refuse_symlink_path(game, &parent)?;
            if retaining && parent.is_file() { park_kept(game, &parent)?; }
        }
        std::fs::create_dir_all(&parent).map_err(io_at(&parent, "creating"))?;
        let dst = to.join(rel.as_str());
        refuse_symlink_path(game, &dst)?;
        if retaining && dst.exists() {
            if dst.is_file() && crate::hash::sha256_file(&src)?.0 == crate::hash::sha256_file(&dst)?.0 { continue; }
            park_kept(game, &dst)?;
        }
        // carry_in excludes staged directory/file collisions; never remove a directory to copy one file over it.
        std::fs::copy(&src, &dst).map_err(io_at(&src, "keeping"))?;
    }
    Ok(())
}

/// Before Kiln removes its copy `dst`: the files the mod wrote there go to [`kept_dir`]. A conflicting earlier retained
/// file or directory is parked as `.kept-N`; neither value is discarded. Returns the sentence for [`PlaceReport::kept_files`], if any.
pub(crate) fn keep_player_files(game: &Path, dst: &Path, kind: ModKind, id: &str, player: &[String]) -> Result<Option<String>> {
    let files: Vec<&String> = file_entries(player).collect();
    if files.is_empty() { return Ok(None); }
    let kept = kept_dir(game, kind, id);
    copy_entries(game, dst, &kept, &files, true)?;
    let names: Vec<String> = files.iter().map(|s| s.to_string()).collect();
    Ok(Some(format!("{}:{id}: kept {} the mod wrote ({}) in {KEPT_MOD_FILES}/{}/{id}; they go back when the mod is placed again",
        kind.as_str(), if names.len() == 1 { "1 file" } else { "the files" }, some_names(&names), kind.as_str())))
}

/// Player files for a new copy: from the copy it replaces, or from [`kept_dir`] when there is no copy.
pub(crate) struct Carry<'a> {
    pub(crate) from: &'a Path,
    pub(crate) entries: Vec<String>,
    /// `from` is the kept folder: the files that went back are removed from it.
    pub(crate) from_kept: bool,
}

/// What [`carry_in`] did: the files that went into the new copy and the ones left in (or moved to) the kept folder.
pub(crate) struct Carried {
    back: Vec<String>,
    aside: Vec<String>,
}

/// Copies the player's files of `c` over a staged new copy of `kind`/`id` (staged under `game`). A file the new copy
/// ships under the same name stays the library's (unless it is a settings file): the player's goes to [`kept_dir`].
pub(crate) fn carry_in(game: &Path, kind: ModKind, id: &str, c: &Carry, staged: &Path) -> Result<Carried> {
    let (mut back, mut aside) = (Vec::new(), Vec::new());
    for rel in file_entries(&c.entries) {
        let to = staged.join(rel);
        // the library's own file, or a library file where a folder of the path would be, keeps that path
        let blocked = rel.match_indices('/').any(|(i, _)| staged.join(&rel[..i]).is_file());
        if blocked || to.is_dir() || (to.is_file() && !is_settings_file(kind, rel)) {
            aside.push(rel.clone());
        } else {
            back.push(rel.clone());
        }
    }
    copy_entries(game, c.from, staged, &back.iter().collect::<Vec<_>>(), false)?;
    // a file the new version ships itself: the player's goes to the kept folder (already there when it came from it)
    if !aside.is_empty() && !c.from_kept {
        copy_entries(game, c.from, &kept_dir(game, kind, id), &aside.iter().collect::<Vec<_>>(), true)?;
    }
    Ok(Carried { back, aside })
}

/// After the new copy `dst` is in place: the files that went back leave the kept folder, each only while the kept
/// file still holds the bytes the copy now has (a value edited there meanwhile stays). The sentence for
/// [`PlaceReport::kept_files`], if any (files carried from the old copy need none).
pub(crate) fn carried(game: &Path, kind: ModKind, id: &str, c: &Carry, done: &Carried, dst: &Path) -> Result<Option<String>> {
    let kind = kind.as_str();
    let files = |n: usize| if n == 1 { "1 file" } else { "the files" };
    let mut said = Vec::new();
    if c.from_kept && !done.back.is_empty() {
        let mut same = Vec::new();
        for rel in &done.back {
            let (kept, placed) = (c.from.join(rel), dst.join(rel));
            refuse_symlink_path(game, &kept)?;
            refuse_symlink_path(game, &placed)?;
            if kept.is_file() && placed.is_file() && crate::hash::sha256_file(&kept)?.0 == crate::hash::sha256_file(&placed)?.0 {
                same.push(rel.clone());
            }
        }
        prune_kept(game, c.from, &same)?;
        said.push(format!("put back {} the mod wrote earlier ({}) from {KEPT_MOD_FILES}/{kind}/{id}", files(done.back.len()),
            some_names(&done.back)));
    }
    if !done.aside.is_empty() {
        said.push(format!("kept {} the mod wrote ({}) in {KEPT_MOD_FILES}/{kind}/{id}: this version ships its own file of that name",
            files(done.aside.len()), some_names(&done.aside)));
    }
    Ok((!said.is_empty()).then(|| format!("{kind}:{id}: {}", said.join("; "))))
}

/// `name.kept-N`: an earlier value [`park_kept`] set aside. It is a recovery copy, never a file the mod wrote.
fn is_parked(rel: &str) -> bool {
    rel.rsplit_once(".kept-").is_some_and(|(_, n)| !n.is_empty() && n.bytes().all(|b| b.is_ascii_digit()))
}

/// The kept files of `kind`/`id` (None: nothing kept). Parked `.kept-N` values stay where they are.
pub(crate) fn kept_carry<'a>(game: &Path, kept: &'a Path, kind: ModKind) -> Result<Option<Carry<'a>>> {
    refuse_symlink_path(game, kept)?;
    if !kept.is_dir() { return Ok(None); }
    let entries: Vec<String> = placement_files(kept, kind)?.into_keys()
        .filter(|e| !e.ends_with('/') && !Path::new(e).components().any(|c| is_parked(&c.as_os_str().to_string_lossy()))).collect();
    Ok((!entries.is_empty()).then_some(Carry { from: kept, entries, from_kept: true }))
}

/// Removes the restored `files` from the kept folder, then the folders left empty up to `<game>/kiln/kept-mod-files`.
/// Callers pass only files whose bytes they proved went back.
fn prune_kept(game: &Path, kept: &Path, files: &[String]) -> Result<()> {
    for rel in files {
        let p = kept.join(rel);
        refuse_symlink_path(game, &p)?;
        if p.is_file() { std::fs::remove_file(&p).map_err(io_at(&p, "removing"))?; }
    }
    let stop = game.join(KEPT_MOD_FILES);
    fn prune(dir: &Path) -> bool {
        let mut empty = true;
        for e in std::fs::read_dir(dir).into_iter().flatten().flatten() {
            let p = e.path();
            if e.file_type().is_ok_and(|t| t.is_dir()) && prune(&p) { let _ = std::fs::remove_dir(&p); } else { empty = false; }
        }
        empty
    }
    if prune(kept) { let _ = std::fs::remove_dir(kept); }
    let mut up = kept.parent();
    while let Some(d) = up.filter(|d| d.starts_with(&stop)) {
        if std::fs::remove_dir(d).is_err() { break; }
        up = d.parent();
    }
    Ok(())
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
/// `carry`: the player's files for the new copy (see [`Carry`]). They are copied over the staged library copy after its
/// receipt is taken, so the next placement sees them as the player's again. A file the library copy ships under the
/// same name stays the library's (unless it is a settings file): the player's goes to [`kept_dir`]. Returns the
/// sentence for [`PlaceReport::kept_files`], if any, and the [`PlaceReport::warnings`] (ShroudForge settings the new
/// version refuses: it starts with its own default for them).
#[allow(clippy::too_many_arguments)]
fn copy_in(roots: Roots, src: &Path, dst: &Path, m: &Marker, kept: &[(String, serde_json::Value)], enabled: bool,
           carry: Option<Carry>, persist: impl FnOnce(&Placement) -> Result<()>) -> Result<(Option<String>, Vec<String>)> {
    copy_in_with(roots, src, dst, m, kept, enabled, carry, &[], None, false, persist)
}

/// The last step of publishing a staged copy (tests make it fail).
fn rename_in(staged: &Path, dst: &Path) -> std::io::Result<()> {
    #[cfg(test)]
    if tests::FAIL_RENAME_IN.with(|f| f.replace(false)) { return Err(std::io::Error::other("injected rename failure")); }
    std::fs::rename(staged, dst)
}

/// Shared placement transaction. Both generic player files (`carry`) and native game-folder settings (`settings`)
/// go over the staged Library copy only after its receipt is taken; neither becomes a disposable shipped default.
/// `game_folder`: a copy in `<game>/mods/` ([`Placement::game_folder`]), with its [`settings_key`]; player files set
/// aside go to the [`kept_dir`] of that key (one native package can have several such folders).
/// `sf_switch`: an EML copy in the mods folder ShroudForge reads while ShroudForge runs the profile's EML mods. It gets
/// ShroudForge's switch (`enabled`) as a ShroudForge mod's copy does, and `kept` carries the player's ShroudForge
/// settings to it the same way.
#[allow(clippy::too_many_arguments)]
fn copy_in_with(roots: Roots, src: &Path, dst: &Path, m: &Marker, kept: &[(String, serde_json::Value)], enabled: bool,
                carry: Option<Carry>, settings: &[(String, Vec<u8>)], game_folder: Option<&str>, sf_switch: bool,
                persist: impl FnOnce(&Placement) -> Result<()>) -> Result<(Option<String>, Vec<String>)> {
    refuse_symlink_path(roots.data, src)?;
    refuse_symlink_path(roots.game, dst)?;
    // A native settings companion is a subtree of its selected Library package; the marker still identifies that
    // package, not an invented executable mod. Verify the parent package and the copied subtree independently.
    let key = game_folder.unwrap_or(&m.id);
    let package = if m.kind == ModKind::Native && game_folder.is_some() {
        src.parent().filter(|p| p.file_name().is_some_and(|n| n.eq_ignore_ascii_case("mods"))).and_then(Path::parent)
            .ok_or_else(|| Error::invalid("native settings must be under the package's mods folder"))?
    } else { src };
    if crate::hash::dirhash(package)? != m.hash {
        return Err(Error::new(ErrorCode::HashMismatch, format!("{}: library hash mismatch", package.display())));
    }
    let source_hash = crate::hash::dirhash(src)?;
    let parent = dst.parent().ok_or_else(|| Error::invalid("no mod parent"))?;
    std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
    let tmp = crate::library::TempDir::new(parent, "kiln-place")?;
    let staged = tmp.path().join("new");
    crate::library::copy_mod_tree(src, &staged)?;
    if crate::hash::dirhash(&staged)? != source_hash || crate::hash::dirhash(package)? != m.hash {
        return Err(Error::new(ErrorCode::HashMismatch, "mod changed while placing"));
    }
    let mut warnings = Vec::new();
    // An EML package ShroudForge cannot read gets no switch: placed as before, it does not run under ShroudForge (the
    // caller's switch_on_eml_copy says why).
    let switched = (m.kind == ModKind::ShroudForge || (m.kind == ModKind::Eml && sf_switch)) && match crate::shroudforge::set_enabled(&staged, enabled) {
        Ok(_) => true,
        Err(_) if m.kind == ModKind::Eml => false,
        Err(e) => return Err(e),
    };
    let extension = if switched {
        // Record the Library defaults before carrying the player's settings to an upgraded copy. Carried changes
        // remain player-owned, so cleanup retains them even after a version change.
        let expected = std::fs::read_to_string(staged.join("extended.mod.json"))?;
        let new = crate::shroudforge::read_mod(&staged)?;
        for (key, value) in kept {
            let Some(default) = new.settings.iter().find(|s| &s.key == key).map(|s| s.value.clone()) else { continue };
            // a value this version refuses (another range, a whole number, a shortcut syntax) keeps its default: the
            // update and every other mod of the profile go on, and the player is told the old value
            match crate::shroudforge::set_setting(&staged, key, value) {
                Ok(_) => {}
                Err(e) if e.code == ErrorCode::InvalidArgument => warnings.push(format!(
                    "{}:{}: the setting {key} was {value}, which version {} does not accept ({}); it starts with that version's default {default}",
                    m.kind.as_str(), m.id, m.version, e.message)),
                Err(e) => return Err(e),
            }
        }
        Some(expected)
    } else if m.kind == ModKind::Eml {
        // an EML copy ShroudForge does not run now still ships its file: Kiln's bytes are its proof once ShroudForge
        // runs it (switch_eml_copy) and the player changes a value there
        std::fs::read_to_string(staged.join("extended.mod.json")).ok()
    } else { None };
    crate::util::write_json_atomic(&staged.join(MARKER), m)?;
    // The receipt lists the Library copy only: carried files and changed SF settings remain the player's.
    let receipt = Placement { path: dst.to_path_buf(), marker: m.clone(), files: placement_files(&staged, m.kind)?, extension,
        game_folder: game_folder.is_some() };
    let done = match &carry {
        Some(c) => Some(carry_in(roots.game, m.kind, key, c, &staged)?),
        None => None,
    };
    for (rel, bytes) in settings {
        crate::bundle::safe_rel_path(rel)?;
        let to = staged.join(rel);
        refuse_symlink_path(&staged, &to)?;
        if let Some(p) = to.parent() { std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?; }
        std::fs::write(&to, bytes).map_err(io_at(&to, "writing"))?;
    }
    let old = tmp.path().join("old");
    let replaced = dst.exists();
    if replaced { std::fs::rename(dst, &old).map_err(io_at(dst, "parking old mod"))?; }
    if let Err(e) = rename_in(&staged, dst) {
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
    let said = match (carry, done) {
        (Some(c), Some(done)) => carried(roots.game, m.kind, key, &c, &done, dst)?,
        _ => None,
    };
    Ok((said, warnings))
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

/// Keep the shared list unchanged on a failed write: the folder transaction will roll that copy back.
fn put_placement(game: &Path, receipts: &mut Vec<Placement>, receipt: &Placement) -> Result<()> {
    let mut next = receipts.iter().filter(|r| r.path != receipt.path).cloned().collect::<Vec<_>>();
    next.push(receipt.clone());
    save_placements(game, &next)?;
    *receipts = next;
    Ok(())
}

/// Publish a removal's receipt only after the folder transaction succeeds. A later mod must never save the
/// rolled-back removal of an earlier mod from this run.
fn remove_placement(game: &Path, receipts: &mut Vec<Placement>, dst: &Path) -> Result<()> {
    let next = receipts.iter().filter(|r| r.path != dst).cloned().collect::<Vec<_>>();
    if std::fs::symlink_metadata(dst).is_ok() {
        remove_copy(game, dst, || save_placements(game, &next))?;
    } else {
        save_placements(game, &next)?;
    }
    *receipts = next;
    Ok(())
}

/// A placed folder that cannot safely be removed. It stays at `path`; the foreign loader may still run it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RetainedPlacement {
    pub path: PathBuf,
    pub reason: String,
    pub may_run: bool,
    /// The profile has this mod off (or no longer has it) while the folder stays: what may run from it is a mod the
    /// player switched off. The launcher shows this one even with its notes about mod folders switched off.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub off: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlacementCleanup {
    pub removed: Vec<PathBuf>,
    pub retained: Vec<RetainedPlacement>,
    /// Files the mods wrote into removed copies, now in [`KEPT_MOD_FILES`]: one sentence per mod, as
    /// [`PlaceReport::kept_files`]. Absent or empty: none.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub kept_files: Vec<String>,
}

impl PlacementCleanup {
    pub fn warning(&self) -> Option<String> {
        (!self.retained.is_empty()).then(|| format!(
            "Kept mod folders to preserve player files: {}. These mods may still run through Kiln or another loader; review or move these folders with the game closed.",
            self.retained.iter().map(|r| format!("{} ({})", r.path.display(), r.reason)).collect::<Vec<_>>().join("; ")))
    }
}

/// A receipt is not permission to remove an arbitrary path, even when its marker and contents still match.
fn check_placement_path(game_dir: &Path, receipt: &Placement) -> Result<()> {
    let dst = &receipt.path;
    let root = match receipt.marker.kind {
        ModKind::ShroudForge => crate::shroudforge::mods_dir(game_dir),
        ModKind::Set => game_dir.join(SET_MODS),
        ModKind::Emf => game_dir.join("emf/mods"),
        ModKind::Greeneye => game_dir.join(GREENEYE_MODS),
        ModKind::Eml if dst.parent() == Some(crate::shroudforge::mods_dir(game_dir).as_path()) => crate::shroudforge::mods_dir(game_dir),
        ModKind::Eml | ModKind::Flame | ModKind::Shroudtopia => game_dir.join("mods"),
        ModKind::Native | ModKind::Xhl if receipt.game_folder => game_dir.join(EML_GAME_MODS),
        _ => PathBuf::new(),
    };
    if root.as_os_str().is_empty() || dst.parent() != Some(root.as_path()) {
        return Err(Error::new(ErrorCode::Refused, "placement folder changed or is not an engine mod folder"));
    }
    Ok(())
}

/// Undo profile delivery, not the player's mod installation. Requires a trusted receipt AND matching contents;
/// markers alone never authorize deletion. Conflicts stay in place with structured, visible recovery instructions.
/// Archives and other loaders' working backups are deliberately not ours to restore to vanilla.
pub fn unplace(dirs: &DataDirs, game_dir: &Path) -> Result<PlacementCleanup> {
    let file = placements_file(game_dir);
    refuse_symlink_path(game_dir, &file)?;
    // No delivery receipt means no ownership to undo. Do not create state (including a lock) in an otherwise
    // untouched installation; adopted pre-existing runtime files must still roundtrip byte-for-byte.
    if !file.try_exists().map_err(io_at(&file, "inspecting"))? { return Ok(PlacementCleanup::default()); }
    let _lock = engine_lock(game_dir, &game_dir.join("kiln/state"))?;
    let mut receipts = placements(game_dir)?;
    let mut report = PlacementCleanup::default();
    for receipt in receipts.clone() {
        let dst = &receipt.path;
        let kind = receipt.marker.kind;
        if is_xhl_core_copy(game_dir, &receipt) {
            match remove_xhl_core_copy(game_dir, &mut receipts, &receipt) {
                Ok(changed) if changed.is_empty() => report.removed.push(dst.clone()),
                Ok(changed) => report.retained.push(xhl_core_kept(dst, &changed)),
                Err(e) => report.retained.push(RetainedPlacement { path: dst.clone(), reason: reason_at(dst, e.message), may_run: false, off: false }),
            }
            continue;
        }
        let check = (|| -> Result<Vec<String>> {
            check_placement_path(game_dir, &receipt)?;
            refuse_symlink_path(game_dir, dst)?;
            if !dst.exists() { return Ok(Vec::new()); }
            let have = owned(game_dir, &receipts, dst, kind, &receipt.marker.id)?
                .ok_or_else(|| Error::new(ErrorCode::Refused, "placement ownership could not be verified"))?;
            // Greeneye and Flame: a copy its mod only added files to (settings, logs) goes, those files wait in kept_dir
            // as on a switch-off: left in greeneye/ or mods/ it would run without the profile (the plugins' drop-in
            // scan of the legacy layout, the engines' own loaders). A changed file Kiln placed still keeps the whole
            // copy (owned refuses).
            if run_folder(kind).is_some() { return Ok(have.player); }
            if !have.player.is_empty() {
                return Err(Error::new(ErrorCode::Refused, format!(
                    "player files or runtime settings were added or changed: {}", some_names(&have.player))));
            }
            if kind == ModKind::ShroudForge {
                let text = std::fs::read_to_string(dst.join("extended.mod.json"))?;
                if receipt.extension.as_deref() != Some(text.as_str()) {
                    return Err(Error::new(ErrorCode::Refused, "ShroudForge settings changed, or an older receipt cannot prove they are unchanged"));
                }
            }
            // an EML copy's ShroudForge switch is the profile's; its setting values may be the player's
            if kind == ModKind::Eml && sf_values_changed(dirs, dst, &receipt) {
                return Err(Error::new(ErrorCode::Refused, "its ShroudForge settings (extended.mod.json) were changed since Kiln placed it (values set in the game, or an edit)"));
            }
            Ok(Vec::new())
        })();
        let player = match check {
            Ok(player) => player,
            Err(e) => {
                report.retained.push(RetainedPlacement { path: dst.clone(), reason: reason_at(dst, e.message), may_run: true, off: true });
                continue;
            }
        };
        let said = if player.is_empty() { None } else {
            match keep_player_files(game_dir, dst, kind, &receipt.marker.id, &player) {
                Ok(said) => said,
                Err(e) => {
                    report.retained.push(RetainedPlacement { path: dst.clone(), reason: reason_at(dst, e.message), may_run: true, off: true });
                    continue;
                }
            }
        };
        if dst.exists() {
            remove_copy(game_dir, dst, || {
                receipts.retain(|r| r.path != *dst);
                save_placements(game_dir, &receipts)
            })?;
            report.removed.push(dst.clone());
            report.kept_files.extend(said);
        } else {
            receipts.retain(|r| r.path != *dst);
            save_placements(game_dir, &receipts)?;
        }
    }
    Ok(report)
}

/// Only a profile's own switch advances the exact cleanup receipt. If the player changed anything else, keep the
/// earlier expected bytes, so subsequent activation cannot accidentally adopt those edits as disposable.
fn switch_copy(game: &Path, receipts: &mut [Placement], dst: &Path, enabled: bool) -> Result<()> {
    refuse_symlink_path(game, &dst.join("extended.mod.json.tmp"))?;
    let current = std::fs::read_to_string(dst.join("extended.mod.json"))?;
    let index = receipts.iter().position(|r| r.path == dst);
    let unchanged = index.is_some_and(|i| receipts[i].extension.as_deref() == Some(current.as_str()));
    crate::shroudforge::set_enabled(dst, enabled)?;
    if unchanged {
        let mut next = receipts.to_vec();
        let saved = (|| {
            next[index.expect("checked")].extension = Some(std::fs::read_to_string(dst.join("extended.mod.json"))?);
            save_placements(game, &next)
        })();
        if let Err(e) = saved {
            if let Err(back) = crate::util::write_atomic_exclusive(&dst.join("extended.mod.json"), current.as_bytes()) {
                return Err(Error::new(e.code, format!("{}; the previous ShroudForge switch could not be restored: {}", e.message, back.message)));
            }
            return Err(e);
        }
        receipts.clone_from_slice(&next);
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
/// Kiln's copies of this kind: the player installed it there. Real EML stops the game on a duplicate id, so Kiln places
/// no second copy beside it, whatever its folder is called; Copy mode names it only under the core's folder name rule
/// (profile::render falls back to the copy under kiln/profiles/).
fn same_mod_elsewhere(root: &Path, kind: ModKind, src: &Path, dst: &Path) -> Option<PathBuf> {
    let id = manifest_id_raw(src)?;
    std::fs::read_dir(root).ok()?.flatten().map(|e| e.path())
        .filter(|p| p != dst && p.is_dir() && !marker(p).is_some_and(|m| m.kind == kind))
        .find(|p| manifest_id_raw(p).is_some_and(|other| other.eq_ignore_ascii_case(&id)))
}

/// Reconcile only copies authorized by the install's receipt. Markers shipped in archives do not grant ownership.
///
/// ShroudForge and SET: always (their plugins read only their own folder). Greeneye and Flame: always, in their
/// [`run_folder`] (their mods read and write their own files there). EML, EMF and Shroudtopia
/// (own_loaders::PLACED_FOR_LOADER): only while that engine's own loader is installed ([`crate::own_loaders::find`]),
/// because that loader reads only its own folder and never the Library. Then the profile's enabled mods are copied
/// there as `<folder>/<library id>/` with the marker, current copies are kept, Kiln's copies of mods switched off or
/// gone from the profile are removed. Without that loader Kiln's copies of the kind are removed again (the plugin runs
/// the mods from the Library and skips marked copies). Player folders are never touched.
///
/// When that loader is ShroudForge (Kiln hosts it, or its own loader is installed), Kiln's EML copies in its mods folder
/// (the game-folder ones too) carry ShroudForge's switch: ShroudForge runs a package there only while its
/// `extended.mod.json` says `enabled: true`. A copy without that file gets the one ShroudForge's own Modloader writes for
/// a legacy EML package, a shipped one only its `enabled` value (byte-preserving). The file is part of what Kiln placed;
/// the setting values in it are the player's afterwards, carried to the next version as a ShroudForge mod's are, and a
/// copy holding them is kept (switched off) rather than removed. The profile is the switch, as for ShroudForge mods.
pub fn place(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path) -> Result<PlaceReport> {
    let view = crate::own_loaders::ProfileView::of_profile(profile);
    let loaders = crate::own_loaders::find(game_dir, Some(&view));
    let mut rep = place_with(dirs, profile, game_dir, &loaders)?;
    rep.own_loaders = crate::own_loaders::notes(game_dir, &loaders, Some(&view));
    Ok(rep)
}

/// A copy activation leaves as it is: named in [`PlaceReport::changed`] and, with its path and the reason, in
/// [`PlaceReport::retained`]. Its own loader may still run it; the other mods of the profile go on.
fn retain(rep: &mut PlaceReport, tag: String, path: &Path, reason: impl Into<String>) {
    retain_off(rep, tag, path, reason, false);
}

/// [`retain`]; `off`: the profile has the mod off ([`RetainedPlacement::off`]).
fn retain_off(rep: &mut PlaceReport, tag: String, path: &Path, reason: impl Into<String>, off: bool) {
    rep.changed.push(tag);
    rep.retained.push(RetainedPlacement { path: path.to_path_buf(), reason: reason_at(path, reason.into()), may_run: true, off });
}

/// A profile entry the player's own folder serves (`own`): Kiln leaves the folder alone and places nothing. Said once
/// per activation in plain words ([`PlaceReport::own_notes`]); it is the player's installation, not a problem.
fn own_folder(rep: &mut PlaceReport, tag: String, path: &Path, on: bool) {
    if on {
        rep.own_notes.push(format!("{tag}: the game uses your own folder {} for this mod; Kiln leaves it as it is and places no copy of its own.", path.display()));
    } else {
        // off in the profile, but its loader may still run it from there: a safety note
        let note = format!("{tag}: your own folder {} stays as it is; Kiln never switches a folder it did not place.", path.display());
        rep.safety_notes.push(note.clone());
        rep.own_notes.push(note);
    }
    rep.own.push(tag);
}

/// [`own_folder`] for an EML entry the player's own folder serves. `sf`: the folder is the one ShroudForge reads while it
/// runs the EML mods, and ShroudForge runs a package there only while its switch is on, for this side. Kiln never writes
/// into the player's folder, so when ShroudForge does not run it the note says why, and where to switch it on when a
/// switch helps.
fn own_eml_folder(rep: &mut PlaceReport, tag: String, path: &Path, sf: bool, server: bool) {
    use crate::shroudforge::SfOff;
    let Some(off) = sf.then(|| crate::shroudforge::switched_off(path, server)).flatten() else { return own_folder(rep, tag, path, true) };
    let folder = path.display();
    rep.own_notes.push(match off {
        SfOff::Switch(why) => format!("{tag}: your own folder {folder} is in ShroudForge's mods folder, but ShroudForge has it switched off \
            ({why}), so it does not run while ShroudForge runs the EML mods. Kiln never writes into your own folder: switch it on in Kiln's \
            menu (Mods, ShroudForge) or in ShroudForge's own Modloader, then start the game again."),
        SfOff::Side(why) => format!("{tag}: your own folder {folder} is in ShroudForge's mods folder, and {why}: ShroudForge runs it on \
            that side, not {}. Kiln leaves it as it is.", if server { "on this server" } else { "in this game" }),
        SfOff::Unreadable(why) => format!("{tag}: your own folder {folder} is in ShroudForge's mods folder, but ShroudForge cannot read it \
            ({why}), so it does not run while ShroudForge runs the EML mods, and no switch changes that. Kiln never writes into your own folder."),
    });
    rep.own.push(tag);
}

/// The ShroudForge setting values of the copy in `dst` ([`crate::shroudforge::read_mod`]), to carry to its next version:
/// none when it has no settings or ShroudForge cannot read it.
fn sf_settings(dst: &Path) -> Vec<(String, serde_json::Value)> {
    crate::shroudforge::read_mod(dst).map(|m| m.settings.into_iter().map(|s| (s.key, s.value)).collect()).unwrap_or_default()
}

/// [`retain`], with what still runs from that copy ([`still_runs`]) after the reason.
fn retain_runs(rep: &mut PlaceReport, tag: String, path: &Path, reason: impl Into<String>, runs: String) {
    let reason = reason_at(path, reason.into());
    let off = runs.starts_with(OFF_IN_PROFILE);
    retain_off(rep, tag, path, format!("{}. {runs}", reason.trim_end_matches('.')), off);
}

/// How [`still_runs`] starts for a mod the profile has off.
const OFF_IN_PROFILE: &str = "It is off in the profile";

/// What may still run from a copy activation leaves as it is. `on`: the profile has the mod on; `have` and `want`:
/// the copy's marker and the profile's entry. The profile's switch is not proof that the retained copy loads (its own
/// manifest may disable it, or its code may be missing). State the version difference without promising execution.
fn still_runs(kind: ModKind, on: bool, have: Option<&Marker>, want: Option<&Marker>) -> String {
    if !on {
        return match kind {
            ModKind::Set => format!("{OFF_IN_PROFILE}, but Kiln's SET plugin runs every mod in that folder."),
            ModKind::ShroudForge => format!("{OFF_IN_PROFILE}, but ShroudForge may still load it."),
            _ => format!("{OFF_IN_PROFILE}, but its own loader may still load it from there."),
        };
    }
    match (have, want) {
        (Some(h), Some(w)) if h.version != w.version => format!("It is on in the profile, but this copy is from version {}, not the requested version {}; its loader may still use it.", h.version, w.version),
        (Some(h), Some(w)) if h.hash != w.hash => format!("It is on in the profile, but this copy is not the profile's build of version {}; its loader may still use it.", w.version),
        _ => "It is on in the profile; this changed copy stays as it is, and its loader may still use it.".into(),
    }
}

/// A failed new copy has no retained folder; a failed update may leave the earlier copy running.
fn not_placed(rep: &mut PlaceReport, tag: String, dst: &Path, error: Error, runs: String) {
    if std::fs::symlink_metadata(dst).is_ok() {
        retain_runs(rep, tag, dst, error.message, runs);
    } else {
        rep.warnings.push(format!("{tag}: not placed in {}: {}", dst.display(), error.message));
    }
}

/// The marker Kiln's receipt for `path` names.
fn marker_at<'a>(receipts: &'a [Placement], path: &Path) -> Option<&'a Marker> {
    receipts.iter().find(|r| r.path == path).map(|r| &r.marker)
}

/// A reason for keeping `path`, without the path an error message starts with (the path is shown beside it).
fn reason_at(path: &Path, message: String) -> String {
    message.strip_prefix(&format!("{}: ", path.display())).map(str::to_owned).unwrap_or(message)
}

/// Why a game-folder copy with these changed, missing or added code files stays.
fn changed_reason(files: &[String]) -> String {
    format!("files Kiln placed were changed or removed, or code was added ({}); Kiln neither updates nor removes it", some_names(files))
}

fn place_with(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path, loaders: &[crate::own_loaders::OwnLoader])
              -> Result<PlaceReport> {
    let _lock = engine_lock(game_dir, &game_dir.join("kiln/state"))?;
    let mut rep = PlaceReport::default();
    let mut receipts = placements(game_dir)?;
    let roots = Roots { data: &dirs.root, game: game_dir };
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
            // A copy Kiln placed but may not touch now (the player edited its code) stays as it is and is reported. It
            // never stops the other mods, its own removal from the profile, or Play.
            let have = match owned(game_dir, &receipts, &dst, kind, &m.id) {
                Ok(h) => h,
                Err(e) => { retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, m.enabled, marker_at(&receipts, &dst), Some(&want))); continue; }
            };
            if have.is_none() && (dst.exists() || (kind == ModKind::ShroudForge && crate::shroudforge::other_holder(&root, &src, &dst).is_some())) {
                own_folder(&mut rep, tag(&m.id), crate::shroudforge::other_holder(&root, &src, &dst).as_deref().filter(|_| !dst.exists()).unwrap_or(&dst), m.enabled);
                continue; // In particular, never apply a ShroudForge switch to a player's folder.
            }
            if kind == ModKind::Set && !m.enabled {
                if let Some(h) = have {
                    let removed = (|| {
                        rep.kept_files.extend(keep_player_files(game_dir, &dst, kind, &m.id, &h.player)?);
                        remove_placement(game_dir, &mut receipts, &dst)
                    })();
                    match removed {
                        Ok(()) => rep.removed.push(tag(&m.id)),
                        Err(e) => retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, false, None, None)),
                    }
                }
                continue;
            }
            if have.as_ref().is_some_and(|h| h.receipt.marker == want) {
                if kind == ModKind::ShroudForge {
                    match crate::shroudforge::read_mod(&dst) {
                        Err(e) => {
                            retain_runs(&mut rep, tag(&m.id), &dst, format!("its ShroudForge settings cannot be read, so its switch was left as it is: {}", e.message), still_runs(kind, m.enabled, Some(&want), Some(&want)));
                            continue;
                        }
                        // also a copy that already has the wanted switch but waits for its fractions to be put back
                        // (an earlier Kiln's write): ShroudForge refuses the whole mod until the file is written again,
                        // and writing the switch does that (shroudforge::set_enabled)
                        Ok(sf) if sf.enabled != m.enabled || !sf.repair.is_empty() => {
                            if let Err(e) = switch_copy(game_dir, &mut receipts, &dst, m.enabled) {
                                retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, m.enabled, Some(&want), Some(&want)));
                                continue;
                            }
                            if sf.enabled != m.enabled { rep.switched.push(tag(&m.id)); }
                            if !sf.repair.is_empty() {
                                rep.warnings.push(format!("{}: repaired its settings file (an earlier Kiln wrote {} without the fraction, and ShroudForge refused the mod until now); your values are unchanged",
                                    tag(&m.id), sf.repair.join(", ")));
                            }
                        }
                        Ok(_) => {}
                    }
                }
                rep.kept.push(tag(&m.id));
            } else {
                // a Library package the reader refuses (imported by an older Kiln with a laxer reader): ShroudForge would
                // refuse it too. It is not placed, the result names it, and the other mods go on.
                if kind == ModKind::ShroudForge && let Err(e) = crate::shroudforge::read_mod(&src) {
                    rep.warnings.push(format!("{}: version {} is not placed: its package files cannot be read ({}); ShroudForge would refuse them too",
                        tag(&m.id), m.version, e.message));
                    continue;
                }
                let kept: Vec<_> = if kind == ModKind::ShroudForge && have.is_some() {
                    match crate::shroudforge::read_mod(&dst) {
                        Ok(sf) => sf.settings.into_iter().map(|s| (s.key, s.value)).collect(),
                        Err(e) => {
                            retain_runs(&mut rep, tag(&m.id), &dst, format!("its ShroudForge settings cannot be read, so version {} was not placed: {}", m.version, e.message), still_runs(kind, m.enabled, marker_at(&receipts, &dst), Some(&want)));
                            continue;
                        }
                    }
                } else { Vec::new() };
                // Library path/integrity failures keep their hard-refusal contract; publication and recovery I/O
                // failures belong to this mod, not to the rest of the profile.
                refuse_symlink_path(roots.data, &src)?;
                let kept_at = kept_dir(game_dir, kind, &m.id);
                let published = (|| {
                    let carry = match &have {
                        Some(h) => Some(Carry { from: &dst, entries: h.player.clone(), from_kept: false }),
                        None => kept_carry(game_dir, &kept_at, kind)?,
                    };
                    copy_in(roots, &src, &dst, &want, &kept, m.enabled, carry,
                        |receipt| put_placement(game_dir, &mut receipts, receipt))
                })();
                let (said, warned) = match published {
                    Ok(done) => done,
                    Err(e) if e.code == ErrorCode::HashMismatch => return Err(e),
                    Err(e) => {
                        not_placed(&mut rep, tag(&m.id), &dst, e, still_runs(kind, m.enabled, marker_at(&receipts, &dst), Some(&want)));
                        continue;
                    }
                };
                rep.kept_files.extend(said);
                rep.warnings.extend(warned);
                rep.placed.push(tag(&m.id));
            }
        }
        // Enumerate trusted receipts, never infer ownership by scanning arbitrary player folders.
        let unlisted: Vec<_> = receipts.iter().filter(|r| r.marker.kind == kind && r.path.parent() == Some(root.as_path())
            && !list.iter().any(|m| m.id == r.marker.id)).cloned().collect();
        for receipt in unlisted {
            let dst = &receipt.path;
            if !dst.exists() {
                if let Err(e) = remove_placement(game_dir, &mut receipts, dst) {
                    rep.warnings.push(format!("{}: could not clear its old placement receipt: {}", tag(&receipt.marker.id), e.message));
                }
                continue;
            }
            let have = match owned(game_dir, &receipts, dst, kind, &receipt.marker.id) {
                Ok(h) => h,
                Err(e) => { retain_runs(&mut rep, tag(&receipt.marker.id), dst, e.message, still_runs(kind, false, None, None)); continue; }
            };
            if kind == ModKind::Set {
                let removed = (|| {
                    if let Some(h) = &have {
                        rep.kept_files.extend(keep_player_files(game_dir, dst, kind, &receipt.marker.id, &h.player)?);
                    }
                    remove_placement(game_dir, &mut receipts, dst)
                })();
                match removed {
                    Ok(()) => rep.removed.push(tag(&receipt.marker.id)),
                    Err(e) => retain_runs(&mut rep, tag(&receipt.marker.id), dst, e.message, still_runs(kind, false, None, None)),
                }
                continue;
            }
            match crate::shroudforge::read_mod(dst) {
                Err(e) => retain_runs(&mut rep, tag(&receipt.marker.id), dst,
                    format!("its ShroudForge settings cannot be read, so it was not switched off: {}", e.message), still_runs(kind, false, None, None)),
                Ok(sf) if sf.enabled => {
                    match switch_copy(game_dir, &mut receipts, dst, false) {
                        Ok(()) => rep.switched.push(tag(&receipt.marker.id)),
                        Err(e) => retain_runs(&mut rep, tag(&receipt.marker.id), dst, e.message, still_runs(kind, false, None, None)),
                    }
                }
                Ok(_) => {}
            }
        }
    }
    // Two passes over these kinds: first every kind's copies the profile no longer wants go, then the wanted ones are
    // placed. A folder one kind leaves (Auto Loot Plus Lite's settings) is then free for another kind (Full's EML part)
    // in the same activation, and the files a removed copy wrote are kept before its new copy looks for them.
    let kinds = || crate::own_loaders::PLACED_FOR_LOADER.into_iter().chain([ModKind::Native, ModKind::Xhl]);
    for (placing, kind) in [false, true].into_iter().flat_map(|placing| kinds().map(move |k| (placing, k))) {
        if run_folder(kind).is_some() {
            place_run_folder(dirs, profile, game_dir, roots, kind, placing, &mut receipts, &mut rep);
            continue;
        }
        let loader = crate::own_loaders::of_kind(loaders, kind).filter(|l| l.delivery == crate::own_loaders::Delivery::Placed);
        let root = loader.map(|l| l.root(game_dir));
        let list = profile.mods.list(kind);
        let tag = |id: &str| format!("{}:{id}", kind.as_str());
        if let Some(root) = &root {
            refuse_symlink_path(game_dir, root)?;
        }
        // ShroudForge may run the profile's EML mods (Kiln hosts it, or its own loader is installed; also when EML's own
        // proxy file is there, which under Wine or Proton loads only with a DLL override): it runs a package of its mods
        // folder only while the package's switch is on, so Kiln's copies there carry that switch (EML's own loader reads
        // only `launcher` from the file). Copies in another folder (a game-folder copy while ShroudForge reads a mods
        // folder configured elsewhere) are not its.
        let sf_root = (kind == ModKind::Eml && crate::own_loaders::shroudforge_runs_eml(loaders))
            .then(|| crate::shroudforge::mods_dir(game_dir));
        let sf_reads = |dst: &Path| sf_root.as_ref().is_some_and(|r| dst.parent() == Some(r.as_path()));
        let server = game_dir.join(crate::game::SERVER_EXE).is_file();
        // Where each enabled mod goes: `<game>/mods/<folder>` for an EML mod a native DLL reads there (always, with or
        // without EML's own loader), else the own loader's folder while that loader is installed.
        let mut targets: Vec<(&crate::profile::ProfileMod, PathBuf, PathBuf, bool)> = Vec::new();
        for m in list.iter().filter(|m| m.enabled) {
            if kind != ModKind::Native && root.is_none() && game_folder_of(dirs, kind, m).is_none() { continue; }
            let src = crate::library::path_of(dirs, &crate::library::LibraryRef {
                kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?;
            if kind == ModKind::Native {
                for folder in crate::library::native_settings_dirs(&src) {
                    let dst = game_dir.join(EML_GAME_MODS).join(folder.file_name().expect("settings folder"));
                    targets.push((m, folder, dst, true));
                }
                continue;
            }
            match (game_folder_of(dirs, kind, m), &root) {
                (Some(folder), _) => targets.push((m, src, game_dir.join(EML_GAME_MODS).join(folder), true)),
                (None, Some(root)) => targets.push((m, src, root.join(&m.id), false)),
                (None, None) => {}
            }
        }
        for (m, src, dst, game_folder) in targets.iter().filter(|_| placing) {
            let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
            let dst = dst.clone();
            if *game_folder {
                let key = settings_key(kind, &m.id, &dst);
                let mods = game_dir.join(EML_GAME_MODS);
                let kept_at = kept_dir(game_dir, kind, &key);
                let (settings, carry) = match settings_changed(game_dir, &receipts, &dst, kind, &m.id) {
                    // nothing there to keep (a link in the way): the mod is not placed, and the result says why
                    Err(e) if std::fs::symlink_metadata(&dst).is_err() => {
                        rep.warnings.push(format!("{}: not placed in {}: {}", tag(&m.id), dst.display(), e.message));
                        continue;
                    }
                    Err(e) => { retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, true, marker_at(&receipts, &dst), Some(&want))); continue; }
                    Ok(Mine::Yes(have, ..)) if have.marker == want => {
                        if sf_reads(&dst) { switch_on_eml_copy(game_dir, &mut receipts, &mut rep, tag(&m.id), &dst); }
                        rep.kept.push(tag(&m.id));
                        continue;
                    }
                    // another version: the new copy keeps the settings and the files the DLL or the player wrote into
                    // the old one
                    Ok(Mine::Yes(have, changed, added)) => match read_settings(&dst, &changed) {
                        Ok(settings) => {
                            // a ShroudForge file with the player's metadata edits: set aside, its values go over by themselves
                            let added = if changed.iter().any(|c| c == "extended.mod.json") { added } else { with_edited_extension(dirs, &dst, &have, added) };
                            (settings, (!added.is_empty()).then(|| Carry { from: &dst, entries: added, from_kept: false }))
                        }
                        Err(e) => { retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, true, marker_at(&receipts, &dst), Some(&want))); continue; }
                    },
                    Ok(Mine::No) => {
                        own_eml_folder(&mut rep, tag(&m.id), &dst, sf_reads(&dst), server);
                        continue;
                    }
                    Ok(Mine::Changed(files)) => {
                        retain_runs(&mut rep, tag(&m.id), &dst, changed_reason(&files), still_runs(kind, true, marker_at(&receipts, &dst), Some(&want)));
                        continue;
                    }
                    Ok(Mine::Absent) => {
                        if let Some(theirs) = same_mod_elsewhere(&mods, kind, src, &dst) {
                            own_eml_folder(&mut rep, tag(&m.id), &theirs, sf_reads(&theirs), server);
                            continue;
                        }
                        // what it wrote before it was switched off: its settings in the stash, other files (and those of
                        // a copy whose receipt had no game-folder flag) in the kept folder. Unreadable ones stay there.
                        let settings = stashed_settings(game_dir, kind, &key).unwrap_or_else(|e| {
                            rep.warnings.push(format!("{}: its earlier settings in {} cannot be read ({}); they stay there and the mod starts with its own",
                                tag(&m.id), stash_dir(game_dir, kind, &key).display(), e.message));
                            Vec::new()
                        });
                        let carry = kept_carry(game_dir, &kept_at, kind).unwrap_or_else(|e| {
                            rep.warnings.push(format!("{}: the files it wrote earlier in {} cannot be read ({}); they stay there",
                                tag(&m.id), kept_at.display(), e.message));
                            None
                        });
                        (settings, carry)
                    }
                };
                refuse_symlink_path(roots.data, src)?;
                let sf = sf_reads(&dst);
                let values = if sf { sf_settings(&dst) } else { Vec::new() };
                let (said, warned) = match copy_in_with(roots, src, &dst, &want, &values, true, carry, &settings, Some(&key), sf,
                    |receipt| put_placement(game_dir, &mut receipts, receipt)) {
                    Ok(done) => done,
                    Err(e) if e.code == ErrorCode::HashMismatch => return Err(e),
                    Err(e) => {
                        not_placed(&mut rep, tag(&m.id), &dst, e, still_runs(kind, true, marker_at(&receipts, &dst), Some(&want)));
                        continue;
                    }
                };
                rep.kept_files.extend(said);
                rep.warnings.extend(warned);
                // a whole extended.mod.json carried as the player's settings file holds the old copy's switch; a package
                // ShroudForge cannot read got none, and this says why
                if sf { switch_on_eml_copy(game_dir, &mut receipts, &mut rep, tag(&m.id), &dst); }
                // tidying the stash never undoes a placed copy (an unreadable stash was reported above)
                if !settings.is_empty() && let Err(e) = drop_stash(game_dir, kind, &key, &settings) {
                    rep.warnings.push(format!("{}: the settings that went back stay in {} too: {}", tag(&m.id), stash_dir(game_dir, kind, &key).display(), e.message));
                }
                let stash = stash_dir(game_dir, kind, &key);
                if stash.is_dir() {
                    rep.kept_files.push(format!("{}: earlier settings and recovery files remain in {}", tag(&m.id), stash.display()));
                }
                rep.placed.push(tag(&m.id));
                continue;
            }
            // A copy whose library files the player edited is not Kiln's to replace (files the mod added are fine).
            let have = match owned(game_dir, &receipts, &dst, kind, &m.id) {
                Ok(h) => h,
                Err(e) => { retain_runs(&mut rep, tag(&m.id), &dst, e.message, still_runs(kind, true, marker_at(&receipts, &dst), Some(&want))); continue; }
            };
            if have.is_none() && dst.exists() {
                own_eml_folder(&mut rep, tag(&m.id), &dst, sf_reads(&dst), server);
                continue;
            }
            if have.as_ref().is_some_and(|h| h.receipt.marker == want) {
                if sf_reads(&dst) { switch_on_eml_copy(game_dir, &mut receipts, &mut rep, tag(&m.id), &dst); }
                rep.kept.push(tag(&m.id));
                continue;
            }
            let root = dst.parent().expect("own loader folder").to_path_buf();
            if let Some(theirs) = same_mod_elsewhere(&root, kind, src, &dst).filter(|_| have.is_none()) {
                own_eml_folder(&mut rep, tag(&m.id), &theirs, sf_reads(&theirs), server);
                continue;
            }
            refuse_symlink_path(roots.data, src)?;
            let kept_at = kept_dir(game_dir, kind, &m.id);
            let sf = sf_reads(&dst);
            let values = if sf && have.is_some() { sf_settings(&dst) } else { Vec::new() };
            let published = (|| {
                let carry = match &have {
                    Some(h) => Some(Carry { from: &dst, entries: with_edited_extension(dirs, &dst, &h.receipt, h.player.clone()), from_kept: false }),
                    None => kept_carry(game_dir, &kept_at, kind)?,
                };
                copy_in_with(roots, src, &dst, &want, &values, true, carry, &[], None, sf,
                    |receipt| put_placement(game_dir, &mut receipts, receipt))
            })();
            let (said, warned) = match published {
                Ok(done) => done,
                Err(e) if e.code == ErrorCode::HashMismatch => return Err(e),
                Err(e) => {
                    not_placed(&mut rep, tag(&m.id), &dst, e, still_runs(kind, true, marker_at(&receipts, &dst), Some(&want)));
                    continue;
                }
            };
            rep.kept_files.extend(said);
            rep.warnings.extend(warned);
            if sf { switch_on_eml_copy(game_dir, &mut receipts, &mut rep, tag(&m.id), &dst); }
            rep.placed.push(tag(&m.id));
        }
        // Kiln's copies the profile does not want there (switched off, gone from the profile, no loader, another folder).
        // XHL-Core's copies in kiln/chain are place_xhl_core_copies' below.
        let stale: Vec<_> = receipts.iter().filter(|r| !placing && r.marker.kind == kind && !is_xhl_core_copy(game_dir, r)
            && !targets.iter().any(|(m, _, dst, _)| r.path == *dst && m.id == r.marker.id)).cloned().collect();
        for receipt in stale {
            let dst = &receipt.path;
            let id = &receipt.marker.id;
            if let Err(e) = check_placement_path(game_dir, &receipt) {
                retain(&mut rep, tag(id), dst, e.message);
                continue;
            }
            if !dst.exists() {
                if let Err(e) = remove_placement(game_dir, &mut receipts, dst) {
                    rep.warnings.push(format!("{}: could not clear its old placement receipt: {}", tag(&receipt.marker.id), e.message));
                }
                continue;
            }
            if receipt.game_folder {
                // Its DLL's settings and files wait for the mod to come back. The copy goes so a drop-in scan cannot run it.
                let key = settings_key(kind, id, dst);
                match settings_changed(game_dir, &receipts, dst, kind, id) {
                    Ok(Mine::Yes(..)) if kind == ModKind::Eml && keep_sf_values(dirs, game_dir, &mut receipts, &mut rep, tag(id), dst).is_some() => continue,
                    Ok(Mine::Yes(_, changed, added)) => {
                        let files = match read_settings(dst, &changed) {
                            Ok(files) => files,
                            Err(e) => { retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)); continue; }
                        };
                        if let Err(e) = stash_settings(game_dir, kind, &key, &files) {
                            retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None));
                            continue;
                        }
                        if !files.is_empty() {
                            rep.kept_files.push(format!("{}: kept runtime settings ({}) in {}; they return when the mod is enabled again",
                                tag(id), some_names(&changed), stash_dir(game_dir, kind, &key).display()));
                        }
                        match keep_player_files(game_dir, dst, kind, &key, &added) {
                            Ok(said) => rep.kept_files.extend(said),
                            Err(e) => { retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)); continue; }
                        }
                    }
                    Ok(Mine::Changed(files)) => { retain_runs(&mut rep, tag(id), dst, changed_reason(&files), still_runs(kind, false, None, None)); continue; }
                    Ok(_) => { retain_runs(&mut rep, tag(id), dst, "placement ownership could not be verified (its marker is not the receipt's)", still_runs(kind, false, None, None)); continue; }
                    Err(e) => { retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)); continue; }
                }
            } else {
                let have = match owned(game_dir, &receipts, dst, kind, id) {
                    Ok(Some(h)) => h,
                    Ok(None) => { retain_runs(&mut rep, tag(id), dst, "placement ownership could not be verified", still_runs(kind, false, None, None)); continue; }
                    Err(e) => { retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)); continue; }
                };
                if kind == ModKind::Eml && keep_sf_values(dirs, game_dir, &mut receipts, &mut rep, tag(id), dst).is_some() { continue; }
                match keep_player_files(game_dir, dst, kind, id, &have.player) {
                    Ok(said) => rep.kept_files.extend(said),
                    Err(e) => { retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)); continue; }
                }
            }
            match remove_placement(game_dir, &mut receipts, dst) {
                Ok(()) => rep.removed.push(tag(id)),
                Err(e) => retain_runs(&mut rep, tag(id), dst, e.message, still_runs(kind, false, None, None)),
            }
        }
    }
    // XHL-Core named dinput8.dll runs from its copy under its own name (xhl_core_chain_dlls)
    place_xhl_core_copies(dirs, profile, game_dir, &mut receipts, &mut rep, true)?;
    Ok(rep)
}

// ---- Greeneye and Flame mods: always in <game>/<run folder>/<library id>/ ----

/// Greeneye mods are copied to `<game>/greeneye/<library id>/` ([`GREENEYE_MODS`]), Flame mods to
/// `<game>/mods/<library id>/` ([`FLAME_MODS`]), with or without the engine's own loader: they read and write their own
/// files in that folder (Greeneye: settings and logs in the folder its API names; Flame: `sigs\`, `symbols\`, logs,
/// notice and settings in `<game>\mods\<id>\` wherever its DLL was loaded from). One mod that cannot be placed (its
/// Library entry changed or unreadable, a linked folder, an I/O error) never stops the rest of the placement, the profile
/// switch or Play: the profile names its Library entry, as before dev.24 ([`game_path_of`] names only a copy whose
/// receipt is current), and the result says why. A copy that stays (the player edited it, it could not be updated or
/// removed) is in [`PlaceReport::retained`], since the engine's own loader may still run it; a mod with no copy there
/// is in [`PlaceReport::warnings`]. An id that is no safe Windows folder name ([`is_game_folder_name`]) is not placed
/// either. Copies of mods switched off or gone from the profile are removed, the files the mod wrote kept in
/// [`kept_dir`]. A player's folder under the mod's name stays theirs ([`PlaceReport::own`]); for Flame, a folder there
/// that only holds files a mod wrote (no manifest, no code: [`written_files_only`]) takes Kiln's copy and keeps them.
#[allow(clippy::too_many_arguments)]
fn place_run_folder(dirs: &DataDirs, profile: &crate::profile::Profile, game_dir: &Path, roots: Roots, kind: ModKind, placing: bool,
                    receipts: &mut Vec<Placement>, rep: &mut PlaceReport) {
    let root = game_dir.join(run_folder(kind).expect("a kind with a run folder"));
    let list = profile.mods.list(kind);
    let tag = |id: &str| format!("{}:{id}", kind.as_str());
    // a copy that stays where it is, or a mod without one: the mod runs from its Library entry either way
    let kept = |rep: &mut PlaceReport, id: &str, dst: &Path, why: &str| {
        if std::fs::symlink_metadata(dst).is_ok() {
            retain(rep, tag(id), dst, why);
        } else {
            rep.warnings.push(format!("{}: not placed in {}: {why}; it runs from the Library", tag(id), dst.display()));
        }
    };
    if let Err(e) = refuse_symlink_path(game_dir, &root) {
        // nothing is read or written through a linked folder
        for m in list.iter().filter(|m| placing && m.enabled) {
            rep.warnings.push(format!("{}: not placed in {}: {}; it runs from the Library", tag(&m.id), root.display(), e.message));
        }
        return;
    }
    // Join the shared removal pass before any engine takes a folder another engine is releasing.
    let wanted: Vec<PathBuf> = list.iter().filter(|m| m.enabled && is_game_folder_name(&m.id)).map(|m| root.join(&m.id)).collect();
    for m in list.iter().filter(|m| placing && m.enabled) {
        if !is_game_folder_name(&m.id) {
            rep.warnings.push(format!("{}: not placed: the id is no folder name Windows can hold; it runs from the Library", tag(&m.id)));
            continue;
        }
        let dst = root.join(&m.id);
        if let Err(e) = place_run_folder_one(dirs, game_dir, roots, kind, receipts, rep, m, &root, &dst) {
            kept(rep, &m.id, &dst, &e.message);
        }
    }
    let stale: Vec<Placement> = receipts.iter()
        .filter(|r| !placing && r.marker.kind == kind && !r.game_folder && !wanted.contains(&r.path)).cloned().collect();
    for receipt in stale {
        if let Err(e) = remove_run_folder_copy(game_dir, receipts, rep, &receipt) {
            kept(rep, &receipt.marker.id, &receipt.path, &e.message);
        }
    }
}

/// Top-level files that make a folder in the game's `mods` a mod of some engine (EML, ShroudForge, Flame, Shroudtopia,
/// Enshwick, XHL-Core) or Kiln's copy: a folder with one is never taken for files a mod only wrote.
const MOD_MANIFESTS: [&str; 9] = ["mod.json", "kiln.mod.json", "native.json", "flame.json", "shroudtopia.json", "pack.toml", "extended.mod.json", XHL_PLUGIN_INI, MARKER];

/// The files of `dst`, a folder at a Flame mod's place in `<game>/mods/` that no receipt names, when it holds no mod:
/// only files a mod wrote there. Creative Mode makes `<game>\mods\creative\` and writes `logs\` and `notice.txt` into it
/// even when its DLL runs from elsewhere (Kiln 0.2.0-dev.23 ran it from the Library), so that folder holds no flame.json.
/// None: a manifest of any engine ([`MOD_MANIFESTS`]), code ([`is_game_code`]), a link or anything that is no regular
/// file is in it (a mod, or something else of the player's), or it cannot be read.
fn written_files_only(dst: &Path) -> Option<Vec<String>> {
    let names: Vec<String> = std::fs::read_dir(dst).ok()?.map(|e| e.map(|e| e.file_name().to_string_lossy().into_owned()))
        .collect::<std::io::Result<_>>().ok()?;
    if names.iter().any(|n| MOD_MANIFESTS.iter().any(|m| m.eq_ignore_ascii_case(n))) { return None; }
    let files = placement_files(dst, ModKind::Flame).ok()?;
    if files.keys().any(|rel| is_game_code(rel)) { return None; }
    Some(files.into_keys().filter(|rel| !rel.ends_with('/')).collect())
}

#[allow(clippy::too_many_arguments)]
fn place_run_folder_one(dirs: &DataDirs, game_dir: &Path, roots: Roots, kind: ModKind, receipts: &mut Vec<Placement>,
                        rep: &mut PlaceReport, m: &crate::profile::ProfileMod, root: &Path, dst: &Path) -> Result<()> {
    let tag = format!("{}:{}", kind.as_str(), m.id);
    let src = crate::library::path_of(dirs, &crate::library::LibraryRef {
        kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() })?;
    let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
    // a copy whose library files the player edited is not Kiln's to replace (files the mod added are fine)
    let have = match owned(game_dir, receipts, dst, kind, &m.id) {
        Ok(h) => h,
        Err(e) if e.code == ErrorCode::Refused => {
            retain(rep, tag, dst, e.message);
            return Ok(());
        }
        Err(e) => return Err(e),
    };
    // Flame: the files a mod wrote into its folder while it ran from elsewhere are the player's, and the folder is the
    // one the mod reads: Kiln's copy goes into it and they stay in it (never a folder another receipt names)
    let written = (have.is_none() && kind == ModKind::Flame && dst.is_dir() && !receipts.iter().any(|r| r.path == dst))
        .then(|| written_files_only(dst)).flatten();
    if have.is_none() && dst.exists() && written.is_none() {
        rep.own.push(tag);
        return Ok(());
    }
    if have.as_ref().is_some_and(|h| h.receipt.marker == want) {
        rep.kept.push(tag);
        return Ok(());
    }
    if have.is_none() && same_mod_elsewhere(root, kind, &src, dst).is_some() {
        rep.own.push(tag);
        return Ok(());
    }
    let Some(split) = split_runtime_files(dirs, game_dir, kind, m, &src)? else {
        return Err(Error::new(ErrorCode::HashMismatch,
            "its Library entry differs from the import in a file of the mod itself, so it runs from the Library"));
    };
    let kept_at = kept_dir(game_dir, kind, &m.id);
    let published = (|| {
        let carry = match (&have, &written) {
            (Some(h), _) => Some(Carry { from: dst, entries: h.player.clone(), from_kept: false }),
            // the folder's files join what is kept for this mod already (a kept file of the same name is parked beside
            // them, never dropped), and all of them come back together
            (None, Some(files)) => {
                copy_entries(game_dir, dst, &kept_at, &files.iter().collect::<Vec<_>>(), true)?;
                kept_carry(game_dir, &kept_at, kind)?
            }
            (None, None) => kept_carry(game_dir, &kept_at, kind)?,
        };
        // the new receipt list is written first and only then taken: a failed write leaves the list as on disk, so a
        // later save of this run cannot record a copy that was rolled back
        copy_in(roots, &src, dst, &want, &[], true, carry, |receipt| {
            let mut next = receipts.clone();
            next.retain(|r| r.path != dst);
            next.push(receipt.clone());
            save_placements(game_dir, &next)?;
            *receipts = next;
            Ok(())
        })
    })();
    let (said, warned) = match published {
        Ok(done) => done,
        Err(e) => {
            // not placed: the mod runs from its Library entry, so what was split off it goes back there
            if let Err(back) = unsplit(dirs, game_dir, kind, &m.id, &src, &split) {
                return Err(Error::new(e.code, format!("{}; and the files it wrote could not go back into its Library entry ({}): \
                    they are in {}", e.message, back.message, kept_at.display())));
            }
            return Err(e);
        }
    };
    if let Some(files) = written.filter(|f| !f.is_empty()) {
        rep.kept_files.push(format!("{tag}: {} held only files the mod wrote there earlier ({}); Kiln placed the mod in that \
            folder, where it reads its own files, and they stay in it", dst.display(), some_names(&files)));
    }
    rep.kept_files.extend(said);
    rep.warnings.extend(warned);
    rep.placed.push(tag);
    Ok(())
}

fn remove_run_folder_copy(game_dir: &Path, receipts: &mut Vec<Placement>, rep: &mut PlaceReport, receipt: &Placement) -> Result<()> {
    check_placement_path(game_dir, receipt)?;
    let kind = receipt.marker.kind;
    let dst = &receipt.path;
    let tag = format!("{}:{}", kind.as_str(), receipt.marker.id);
    // the list without this receipt, taken only once it is written (see place_run_folder_one)
    let without = || receipts.iter().filter(|r| r.path != *dst).cloned().collect::<Vec<_>>();
    if !dst.exists() {
        let next = without();
        save_placements(game_dir, &next)?;
        *receipts = next;
        return Ok(());
    }
    let have = match owned(game_dir, receipts, dst, kind, &receipt.marker.id) {
        Ok(Some(h)) => h,
        Ok(None) => return Err(Error::new(ErrorCode::Refused, "placement ownership could not be verified")),
        Err(e) if e.code == ErrorCode::Refused => {
            retain_off(rep, tag, dst, e.message, true);
            return Ok(());
        }
        Err(e) => return Err(e),
    };
    rep.kept_files.extend(keep_player_files(game_dir, dst, kind, &receipt.marker.id, &have.player)?);
    let next = without();
    remove_copy(game_dir, dst, || save_placements(game_dir, &next))?;
    *receipts = next;
    rep.removed.push(tag);
    Ok(())
}

// ---- EML mods a native DLL reads in the game's mods folder (ImportRecord::game_folder) ----

/// The folder those EML mods are placed in, relative to the game.
pub const EML_GAME_MODS: &str = "mods";

/// The original `<game>/mods/` folder name of a native-related EML mod or external XHL companion, from its import record.
pub fn game_folder_of(dirs: &DataDirs, kind: ModKind, m: &crate::profile::ProfileMod) -> Option<String> {
    if !matches!(kind, ModKind::Eml | ModKind::Xhl) { return None; }
    let r = crate::library::LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
    crate::library::record(dirs, &r).ok()?.game_folder.filter(|f| is_game_folder_name(f))
}

/// The actual folder the profile runs: a pre-existing hand installation of the same mod wins over creating a duplicate.
/// A hand installation's name need not pass [`is_game_folder_name`]: Copy mode then keeps the kiln/profiles copy
/// (profile::render checks the name before it writes `../mods/<folder>`).
/// Greeneye and Flame: Kiln's copy in `<game>/greeneye/<id>/` or `<game>/mods/<id>/` ([`run_folder`]) once [`place`]
/// has made it for this version (its receipt and marker name it), else None (the Library entry: a folder the player put
/// there, a play-once version, a switched-off mod).
pub fn game_path_of(dirs: &DataDirs, kind: ModKind, m: &crate::profile::ProfileMod, game: &Path) -> Option<PathBuf> {
    if let Some(folder) = run_folder(kind) {
        if !m.enabled || !is_game_folder_name(&m.id) { return None; }
        let dst = game.join(folder).join(&m.id);
        let want = Marker { format: 1, kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        let placed = placements(game).ok()?.iter().any(|r| r.path == dst && r.marker == want);
        return (placed && marker(&dst).as_ref() == Some(&want)).then_some(dst);
    }
    let root = game.join(EML_GAME_MODS);
    let dst = root.join(game_folder_of(dirs, kind, m)?);
    if dst.exists() { return Some(dst); }
    let r = crate::library::LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
    let src = crate::library::path_of(dirs, &r).ok()?;
    Some(same_mod_elsewhere(&root, kind, &src, &dst).unwrap_or(dst))
}

/// Kiln 0.2.0-dev.23 and earlier ran Greeneye mods from their Library entry, and a Greeneye mod writes its settings and
/// logs into its own folder, so such an entry no longer matches its kdh1 and could not be placed ("library hash
/// mismatch"). What the mod wrote are the newest files: when leaving out the newest few (at most 64) gives exactly the
/// imported size and kdh1, those files move to [`kept_dir`], from where the placement carries them into the game-folder
/// copy, and the entry is as imported again. Some(moved files, relative): empty when the entry is as imported. None: the
/// entry differs in a file of the mod itself, or the kept folder already holds a file of one of those names; nothing
/// moves. When the placement then fails, [`unsplit`] puts them back.
fn split_runtime_files(dirs: &DataDirs, game: &Path, kind: ModKind, m: &crate::profile::ProfileMod, src: &Path) -> Result<Option<Vec<String>>> {
    if crate::hash::dirhash(src)? == m.hash { return Ok(Some(Vec::new())); }
    let r = crate::library::LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
    let imported = crate::library::record(dirs, &r)?.size;
    // the files dirhash counts (no dot entries), newest first
    let mut files: Vec<(std::time::SystemTime, crate::hash::Record)> = Vec::new();
    fn walk(dir: &Path, prefix: &str, out: &mut Vec<(std::time::SystemTime, crate::hash::Record)>) -> Result<()> {
        for e in std::fs::read_dir(dir).map_err(io_at(dir, "reading"))? {
            let e = e?;
            let name = e.file_name().to_string_lossy().into_owned();
            if name.starts_with('.') { continue; }
            let rel = if prefix.is_empty() { name } else { format!("{prefix}/{name}") };
            let meta = std::fs::symlink_metadata(e.path())?;
            if meta.is_dir() {
                walk(&e.path(), &rel, out)?;
            } else if meta.is_file() {
                let (sha256, size) = crate::hash::sha256_file(&e.path())?;
                out.push((meta.modified().unwrap_or(std::time::UNIX_EPOCH), crate::hash::Record { path: rel, size, sha256 }));
            }
        }
        Ok(())
    }
    refuse_symlink_path(&dirs.root, src)?;
    walk(src, "", &mut files)?;
    files.sort_by(|a, b| b.0.cmp(&a.0).then_with(|| a.1.path.cmp(&b.1.path)));
    let total: u64 = files.iter().map(|(_, f)| f.size).sum();
    let Some(extra) = total.checked_sub(imported) else { return Ok(None) };
    let mut added = 0u64;
    let mut cut = None;
    for n in 1..=files.len().min(64) {
        added += files[n - 1].1.size;
        if added > extra { break; }
        if added == extra && crate::hash::dirhash_records(files[n..].iter().map(|(_, f)| f.clone()))? == m.hash {
            cut = Some(n);
            break;
        }
    }
    let Some(n) = cut else { return Ok(None) };
    let kept = kept_dir(game, kind, &m.id);
    let rels: Vec<String> = files[..n].iter().map(|(_, f)| f.path.clone()).collect();
    // never park a kept file here: what is already kept for this mod is its own and stays as it is
    if rels.iter().any(|rel| std::fs::symlink_metadata(kept.join(rel)).is_ok()) { return Ok(None); }
    let mut moved: Vec<String> = Vec::new();
    for rel in &rels {
        let step = (|| -> Result<()> {
            let from = src.join(rel);
            let to = kept.join(rel);
            refuse_symlink_path(&dirs.root, &from)?;
            refuse_symlink_path(game, &to)?;
            if let Some(parent) = to.parent() {
                refuse_symlink_path(game, parent)?;
                std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
            }
            std::fs::copy(&from, &to).map_err(io_at(&from, "keeping"))?;
            std::fs::remove_file(&from).map_err(io_at(&from, "removing"))
        })();
        if let Err(e) = step {
            unsplit(dirs, game, kind, &m.id, src, &moved)?;
            return Err(e);
        }
        moved.push(rel.clone());
    }
    if crate::hash::dirhash(src)? != m.hash {
        unsplit(dirs, game, kind, &m.id, src, &moved)?;
        return Err(Error::new(ErrorCode::HashMismatch, format!("{}: library hash mismatch after keeping the mod's own files", src.display())));
    }
    Ok(Some(moved))
}

/// Puts the files [`split_runtime_files`] moved to [`kept_dir`] back into the Library entry `src` (a placement that
/// failed after the split: the mod runs from that entry again, with its own files).
fn unsplit(dirs: &DataDirs, game: &Path, kind: ModKind, id: &str, src: &Path, moved: &[String]) -> Result<()> {
    if moved.is_empty() { return Ok(()); }
    let kept = kept_dir(game, kind, id);
    for rel in moved {
        let from = kept.join(rel);
        let to = src.join(rel);
        refuse_symlink_path(game, &from)?;
        refuse_symlink_path(&dirs.root, &to)?;
        if let Some(parent) = to.parent() { std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?; }
        std::fs::copy(&from, &to).map_err(io_at(&from, "putting back"))?;
    }
    prune_kept(game, &kept, moved)
}

// One native package can carry several settings folders; their config.txt files must not share a stash.
fn settings_key(kind: ModKind, id: &str, dst: &Path) -> String {
    if kind == ModKind::Native { format!("{id}/{}", dst.file_name().expect("settings folder").to_string_lossy()) }
    else { id.to_string() }
}

/// Whose is a game-folder copy, and what in it changed since Kiln placed it.
enum Mine {
    /// Kiln's (its receipt and its marker agree), with what the DLL or the player wrote since (relative, '/'
    /// separated): the settings files changed or added ([`is_game_settings_file`]), then the other files added that
    /// are no code ([`is_game_code`]), such as a log or a cache the DLL writes beside itself.
    Yes(Placement, Vec<String>, Vec<String>),
    /// There, and not Kiln's: the player's own folder, or one whose marker is not the receipt's.
    No,
    /// Kiln's marker still matches, but code, the manifest or another file Kiln placed changed or went missing, or
    /// code was added (these files): leave it intact.
    Changed(Vec<String>),
    Absent,
}

fn settings_changed(game: &Path, receipts: &[Placement], dst: &Path, kind: ModKind, id: &str) -> Result<Mine> {
    refuse_symlink_path(game, dst)?;
    if !dst.exists() { return Ok(Mine::Absent); }
    let Some(receipt) = receipts.iter().find(|r| r.path == dst && r.marker.kind == kind && r.marker.id == id) else { return Ok(Mine::No) };
    let receipt = &current_receipt(dst, receipt);
    if receipt.marker.format != 1 || !crate::manifest::is_mod_id(&receipt.marker.id) || marker(dst).as_ref() != Some(&receipt.marker) {
        return Ok(Mine::No);
    }
    let now = placement_files(dst, kind)?;
    let (mut settings, mut added, mut code) = (Vec::new(), Vec::new(), Vec::new());
    for (rel, _) in now.iter().filter(|(rel, hash)| !rel.ends_with('/') && receipt.files.get(*rel) != Some(*hash)) {
        if is_game_settings_file(rel) {
            settings.push(rel.clone());
        } else if !receipt.files.contains_key(rel) && !is_game_code(rel) {
            added.push(rel.clone());
        } else {
            code.push(rel.clone());
        }
    }
    // Receipt ownership never authorizes throwing away player edits to code or manifests, including removed files.
    code.extend(receipt.files.keys().filter(|rel| !rel.ends_with('/') && !now.contains_key(*rel) && !is_game_settings_file(rel)).cloned());
    if !code.is_empty() { return Ok(Mine::Changed(code)); }
    Ok(Mine::Yes(receipt.clone(), settings, added))
}

/// Code or a manifest in a game-folder copy: the manifest, the Lua, the plugin ini, DLLs. A file of this kind that is
/// new since placement makes the copy the player's (XHL-Core and the package's DLL load what is there).
fn is_game_code(rel: &str) -> bool {
    let low = rel.to_ascii_lowercase();
    let ext = low.rsplit_once('.').map(|(_, x)| x).unwrap_or("");
    low == "mod.json" || low == XHL_PLUGIN_INI || low.starts_with("src/") || matches!(ext, "dll" | "exe" | "asi" | "lua" | "luac")
}

/// A file a mod's DLL (or the player) may have written as settings: not the manifest, the Lua, code or the plugin ini.
fn is_game_settings_file(rel: &str) -> bool {
    let low = rel.to_ascii_lowercase();
    let ext = low.rsplit_once('.').map(|(_, x)| x).unwrap_or("");
    !(low == "mod.json" || low == MARKER || low == XHL_PLUGIN_INI || low.starts_with("src/"))
        && matches!(ext, "txt" | "ini" | "cfg" | "json" | "toml" | "yaml" | "yml")
}

fn read_settings(dir: &Path, changed: &[String]) -> Result<Vec<(String, Vec<u8>)>> {
    let mut out = Vec::new();
    for rel in changed.iter().filter(|r| is_game_settings_file(r)) {
        crate::bundle::safe_rel_path(rel)?;
        let p = dir.join(rel);
        refuse_symlink_path(dir, &p)?;
        let meta = std::fs::metadata(&p).map_err(io_at(&p, "reading"))?;
        if meta.len() > 16 * 1024 * 1024 {
            return Err(Error::new(ErrorCode::Refused, format!("{}: changed settings exceed 16 MiB; keeping the mod folder intact", p.display())));
        }
        out.push((rel.clone(), std::fs::read(&p).map_err(io_at(&p, "reading"))?));
    }
    Ok(out)
}

fn stash_dir(game: &Path, kind: ModKind, id: &str) -> PathBuf {
    game.join("kiln/state/placed-settings").join(kind.as_str()).join(id)
}

fn stash_settings(game: &Path, kind: ModKind, id: &str, files: &[(String, Vec<u8>)]) -> Result<()> {
    let dir = stash_dir(game, kind, id);
    refuse_symlink_path(game, &dir)?;
    if files.is_empty() { return Ok(()); }
    let parent = dir.parent().expect("settings stash parent");
    std::fs::create_dir_all(parent).map_err(io_at(parent, "creating"))?;
    let staged = crate::library::TempDir::new(parent, "settings")?;
    let mut entries = Vec::new();
    for (rel, bytes) in files {
        crate::bundle::safe_rel_path(rel)?;
        let to = staged.path().join(rel);
        if let Some(p) = to.parent() { std::fs::create_dir_all(p).map_err(io_at(p, "creating"))?; }
        std::fs::write(&to, bytes).map_err(io_at(&to, "writing"))?;
        entries.push(rel.clone());
    }
    // A failed earlier removal or a player edit may leave older data here. Preserve conflicting values as .kept-N,
    // including file/directory collisions, exactly as generic SET/player-file retention does.
    copy_entries(game, staged.path(), &dir, &entries.iter().collect::<Vec<_>>(), true)
}

fn stashed_settings(game: &Path, kind: ModKind, id: &str) -> Result<Vec<(String, Vec<u8>)>> {
    let dir = stash_dir(game, kind, id);
    refuse_symlink_path(game, &dir)?;
    if !dir.is_dir() { return Ok(Vec::new()); }
    let files = placement_files(&dir, kind)?;
    read_settings(&dir, &files.into_keys().filter(|r| !r.ends_with('/')
        && !Path::new(r).components().any(|c| is_parked(&c.as_os_str().to_string_lossy()))).collect::<Vec<_>>())
}

fn drop_stash(game: &Path, kind: ModKind, id: &str, restored: &[(String, Vec<u8>)]) -> Result<()> {
    let dir = stash_dir(game, kind, id);
    refuse_symlink_path(game, &dir)?;
    if !dir.is_dir() { return Ok(()); }
    let mut files = Vec::new();
    for (rel, bytes) in restored {
        crate::bundle::safe_rel_path(rel)?;
        let path = dir.join(rel);
        refuse_symlink_path(game, &path)?;
        if path.is_file() && std::fs::read(&path).map_err(io_at(&path, "reading"))? == *bytes {
            files.push(rel.clone());
        }
    }
    // Only values actually restored to the new copy may leave the stash. Earlier .kept-N values, unknown files,
    // and files changed by an external editor remain player-owned, rather than being swept with the directory.
    prune_kept(game, &dir, &files)
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

/// An engine folder's identity: (Kiln id, version, name). An Enshwick pack.toml is authoritative; invalid pack
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
    } else {
        // a manifest id that is not a Kiln id (`HoardersHelper`) gets the slug the Library imports it under, so a
        // server advertises the same id a player's Library holds; no manifest id: the folder name, slugged the same way
        let id = explicit.unwrap_or(folder);
        if crate::manifest::is_mod_id(&id) { id } else { crate::library::id_from_name(&id) }
    };
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
    /// ShroudForge: `file` is in the player's own folder, one Kiln did not place. Kiln writes only a setting the player
    /// changes in the launcher there, and never switches, replaces or removes the folder.
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub own_folder: bool,
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
        own_folder: false,
    };
    match kind {
        ModKind::ShroudForge => {
            // The managed root of the folder: the game folder for the folder ShroudForge reads (Kiln's copy or the
            // player's own), else the data dir of the Library entry.
            let placed = match game_dir.map(|g| sf_folder(g, id, folder, false).map(|f| (g, f))).transpose()? {
                Some((_, SfFolder::Several(dirs))) => return Ok(none(&SfFolder::several(id, &dirs))),
                Some((g, SfFolder::At(p, own))) => Some((g, p, own)),
                _ => None,
            };
            let Some((root, dir, own_folder)) = placed.or_else(|| folder.map(|f| (library_root(f), f.to_path_buf(), false))) else {
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
                own_folder,
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
                schema: schema_from_values(&values), values, note: None, own_folder: false,
            })
        }
        ModKind::Emf => Ok(none("EMF settings: use the EMF ini editor")),
        ModKind::Enshwick => Ok(none("Enshwick keeps each mod's settings per world (<save>-mod-settings/<id>/settings.toml): use the game's Mods screen or `enshwick config` on the server")),
        ModKind::Ember => Ok(none("Ember mods don't run yet: Ember has not published its mod ABI")),
        _ => Ok(none("this engine's mods declare no settings")),
    }
}

/// Where the settings of a ShroudForge profile entry are in the game ([`sf_folder`]).
enum SfFolder {
    /// The folder, and whether it is the player's own (not a copy Kiln placed).
    At(PathBuf, bool),
    /// More than one folder of the player's holds the entry's package: Kiln does not pick one.
    Several(Vec<PathBuf>),
    Nowhere,
}

impl SfFolder {
    /// The plain sentence for [`SfFolder::Several`].
    fn several(id: &str, dirs: &[PathBuf]) -> String {
        format!("ShroudForge mod {id}: more than one of your folders holds its package ({}). ShroudForge leaves out a package it \
            finds twice, and Kiln does not pick one to change: keep one of them, then change its settings",
            dirs.iter().map(|d| d.display().to_string()).collect::<Vec<_>>().join(", "))
    }
}

/// The ShroudForge package id (mod.json `id`) of the package in `folder`.
fn sf_package_id(folder: &Path) -> Option<String> {
    let file = folder.join("mod.json");
    if std::fs::metadata(&file).ok()?.len() > 1024 * 1024 { return None; }
    read_json(&file)?.get("id")?.as_str().map(str::to_owned)
}

/// The folder ShroudForge reads the settings of the profile entry `id` from in `game`, and whether it is the player's
/// own. Kiln's copy `<mods>/<id>` (its receipt names it; a changed copy is refused when `writing`, as [`owned`] says,
/// and shown as Kiln's when reading). A folder Kiln did not place only when it holds THAT package: its mod.json id is
/// the id of the entry's Library package (`library`, its folder). The player's folder named `<id>` that holds another
/// package is not it; the folders that hold the package are then looked for by id, and more than one is
/// [`SfFolder::Several`]. Without the Library folder a folder Kiln did not place cannot be told, so it is not used.
fn sf_folder(game: &Path, id: &str, library: Option<&Path>, writing: bool) -> Result<SfFolder> {
    let root = crate::shroudforge::mods_dir(game);
    let dst = root.join(id);
    if dst.is_dir() {
        let receipts = placements(game)?;
        match owned(game, &receipts, &dst, ModKind::ShroudForge, id) {
            Ok(Some(_)) => return Ok(SfFolder::At(dst, false)),
            Err(e) if writing => return Err(e),
            Err(_) => return Ok(SfFolder::At(dst, false)),
            Ok(None) => {}
        }
    }
    let Some(package) = library.and_then(sf_package_id) else { return Ok(SfFolder::Nowhere) };
    let mut holders: Vec<PathBuf> = std::fs::read_dir(&root).into_iter().flatten().flatten().map(|e| e.path())
        .filter(|p| p.is_dir() && sf_package_id(p).as_deref() == Some(package.as_str())).collect();
    holders.sort();
    Ok(match holders.len() {
        0 => SfFolder::Nowhere,
        1 => SfFolder::At(holders.remove(0), true),
        _ => SfFolder::Several(holders),
    })
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
            // Kiln's copy, or the player's own folder that serves the entry: the player changing a setting here is the
            // player's own request, so that value (and only it) is written in their folder too. A copy Kiln placed whose
            // library files changed stays refused ([`owned`]); switching, replacing and removing a player's folder
            // stay [`place`]'s refusals.
            let not_there = || Error::not_found(format!("ShroudForge mod {id} is not in ShroudForge's mods folder: activate the profile first"));
            let game = game_dir.ok_or_else(not_there)?;
            let dir = match sf_folder(game, id, folder, true)? {
                SfFolder::At(dir, _) => dir,
                SfFolder::Several(dirs) => return Err(Error::new(ErrorCode::Refused, SfFolder::several(id, &dirs))),
                SfFolder::Nowhere => return Err(not_there()),
            };
            refuse_symlink_path(game, &dir.join("mod.json"))?;
            refuse_symlink_path(game, &dir.join("extended.mod.json"))?;
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

// ---- XHL-Core under its own name ----

/// Where Kiln chain-loads XHL-Core from when its Library file is called `dinput8.dll`:
/// `<game>/kiln/chain/<library id>/` ([`xhl_core_chain_dlls`]).
pub const XHL_CORE_COPIES: &str = crate::paths::game::CHAIN_DIR;
/// XHL-Core's DLL in that copy.
pub const XHL_CORE_DLL: &str = "xhl-core.dll";
/// The log XHL-Core writes next to its DLL.
const XHL_CORE_LOG: &str = "XHL-Native-Plugin-Host.log";

/// The DLLs of the dllmain native mod in the Library folder `folder` as Kiln chain-loads them, `(Library file, file in
/// the copy)`, when one of them is XHL-Core named `dinput8.dll` (known by its bytes, so an entry imported before Kiln
/// knew XHL-Core 2.x counts too). None for every other mod, which runs from its Library entry (Auto Loot Plus's
/// `dinput8.dll` too).
///
/// XHL-Core 2.x picks its role in DllMain: when `GetModuleHandleW(L"dinput8.dll")` is its own module it takes the
/// role "primary" and starts its plugins when the game calls its own DirectInput8Create. Behind Kiln's proxy the game's
/// import is bound to the proxy, which forwards to the system's dinput8.dll, so that call never comes. Under Wine the
/// lookup answers with the DLL Kiln just loaded when that is called `dinput8.dll` (a player's log: "role=primary." and
/// no plugin started). Under another name XHL-Core finds the game's import bound to another module, wraps it and
/// starts its plugins at the game's first DirectInput8Create call ("role=chained"). So Kiln chain-loads a copy named
/// [`XHL_CORE_DLL`] from [`xhl_core_copy`]: profile::render names that folder, compat::dllmain_entries the DLL in it,
/// and [`place`] makes it.
pub fn xhl_core_chain_dlls(folder: &Path) -> Option<Vec<(String, String)>> {
    let n = crate::manifest::read_native(folder).ok()?;
    if n.phase != crate::manifest::NativePhase::Dllmain { return None; }
    let named = |dir: &Path, name: &str| std::fs::read_dir(dir).into_iter().flatten().flatten()
        .any(|e| e.file_name().to_string_lossy().eq_ignore_ascii_case(name));
    let mut renamed = false;
    let mut dlls = Vec::new();
    for d in &n.dlls {
        let (dir, file) = d.rsplit_once('/').unwrap_or(("", d));
        let path = folder.join(d);
        let xhl = file.eq_ignore_ascii_case("dinput8.dll")
            && std::fs::metadata(&path).is_ok_and(|m| m.is_file() && m.len() <= 128 * 1024 * 1024)
            && std::fs::read(&path).is_ok_and(|b| crate::library::is_xhl_core(&b));
        if !xhl {
            dlls.push((d.clone(), d.clone()));
            continue;
        }
        // a package that has a file of that name next to it already stays as it is
        if named(&folder.join(dir), XHL_CORE_DLL) { return None; }
        renamed = true;
        dlls.push((d.clone(), if dir.is_empty() { XHL_CORE_DLL.to_string() } else { format!("{dir}/{XHL_CORE_DLL}") }));
    }
    renamed.then_some(dlls)
}

/// Kiln's copy of a native mod [`xhl_core_chain_dlls`] renames: `<game>/kiln/chain/<library id>` (joined per component,
/// so a Windows path has one kind of separator).
pub fn xhl_core_copy(game: &Path, id: &str) -> PathBuf {
    XHL_CORE_COPIES.split('/').fold(game.to_path_buf(), |p, c| p.join(c)).join(id)
}

/// [`xhl_core_copy`] relative to `<game>/kiln/`, as Copy mode names it in profile.json (and chain.ini after it).
pub fn xhl_core_copy_rel(id: &str) -> String {
    format!("{}/{id}", XHL_CORE_COPIES.trim_start_matches("kiln/"))
}

fn is_xhl_core_copy(game: &Path, r: &Placement) -> bool {
    r.marker.kind == ModKind::Native && !r.game_folder && r.path.parent() == xhl_core_copy(game, "id").parent()
}

/// The files of XHL-Core's copy, `(file in the copy, bytes)`: the Library entry's files, the renamed DLLs under their
/// new names, and native.json naming them so (the rest of it as it was). Left out is what XHL-Core writes or reads next
/// to itself: its log, and a `mods` folder (it would load its plugins from there instead of the game's mods folder,
/// where Kiln places the XHL mods and the package's settings folders). Refused when the entry is not as imported
/// (`hash`), apart from that log: XHL-Core wrote it into the entry when it ran from there.
fn xhl_core_copy_files(folder: &Path, hash: &str, dlls: &[(String, String)]) -> Result<Vec<(String, Vec<u8>)>> {
    let lower = |s: &str| s.to_ascii_lowercase();
    let renamed: Vec<(String, &str)> = dlls.iter().filter(|(l, r)| l != r).map(|(l, r)| (lower(l), r.as_str())).collect();
    let next_to: Vec<String> = renamed.iter().map(|(l, _)| l.rsplit_once('/').map_or(String::new(), |(d, _)| format!("{d}/"))).collect();
    let is_log = |p: &str| next_to.iter().any(|d| lower(p) == format!("{d}{}", lower(XHL_CORE_LOG)));
    let in_mods = |p: &str| next_to.iter().any(|d| lower(p).starts_with(&format!("{d}mods/")));
    let records = crate::hash::dir_records(folder)?;
    if crate::hash::dirhash_records(records.clone())? != hash
        && crate::hash::dirhash_records(records.iter().filter(|r| !is_log(&r.path)).cloned())? != hash
    {
        return Err(Error::new(ErrorCode::HashMismatch, format!("{}: the Library entry changed since it was imported", folder.display())));
    }
    let mut out = Vec::new();
    for r in records.iter().filter(|r| !is_log(&r.path) && !in_mods(&r.path) && r.path != crate::manifest::NATIVE_MANIFEST) {
        let from = folder.join(&r.path);
        let bytes = std::fs::read(&from).map_err(io_at(&from, "reading"))?;
        if crate::hash::sha256_hex(&bytes) != r.sha256 {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: changed while copying", from.display())));
        }
        let rel = renamed.iter().find(|(l, _)| *l == lower(&r.path)).map_or_else(|| r.path.clone(), |(_, run)| run.to_string());
        out.push((rel, bytes));
    }
    let manifest = folder.join(crate::manifest::NATIVE_MANIFEST);
    let mut doc = crate::jsonc::parse(&std::fs::read_to_string(&manifest).map_err(io_at(&manifest, "reading"))?)?;
    doc["dlls"] = serde_json::Value::from(dlls.iter().map(|(_, run)| run.clone()).collect::<Vec<_>>());
    let mut text = serde_json::to_string_pretty(&doc)?;
    text.push('\n');
    out.push((crate::manifest::NATIVE_MANIFEST.to_string(), text.into_bytes()));
    Ok(out)
}

/// sha256 of a regular file; None when there is none.
fn file_sha(path: &Path) -> Result<Option<String>> {
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(io_at(path, "inspecting")(e)),
        Ok(m) if m.is_file() => Ok(Some(crate::hash::sha256_file(path)?.0)),
        Ok(_) => Err(Error::new(ErrorCode::Refused, format!("{}: not a regular file", path.display()))),
    }
}

/// Places one copy for [`place_xhl_core_copies`]: true when it wrote, false when it was current. Ownership goes file by
/// file: Kiln writes a file of the copy only where there is none or where the file still holds the bytes its receipt
/// lists, and leaves every other file there as it is (XHL-Core's log). A file of the copy that holds other bytes
/// (changed since Kiln wrote it, or put there by hand) is refused, and the copy is left as it is.
fn place_xhl_core_copy(dirs: &DataDirs, game: &Path, src: &Path, dlls: &[(String, String)], dst: &Path, want: Marker,
                       receipts: &mut Vec<Placement>) -> Result<bool> {
    refuse_symlink_path(&dirs.root, src)?;
    refuse_symlink_path(game, dst)?;
    let files = xhl_core_copy_files(src, &want.hash, dlls)?;
    let listed: std::collections::BTreeMap<String, String> = files.iter().map(|(rel, b)| (rel.clone(), crate::hash::sha256_hex(b))).collect();
    let earlier = receipts.iter().find(|r| r.path == dst && is_xhl_core_copy(game, r)).cloned();
    let ours = |rel: &str, have: &str| earlier.as_ref().and_then(|r| r.files.get(rel)).is_some_and(|h| h == have);
    let mut write = Vec::new();
    for (rel, bytes) in &files {
        let path = dst.join(rel);
        refuse_symlink_path(game, &path)?;
        match file_sha(&path)? {
            None => write.push((path, bytes)),
            Some(have) if have == listed[rel] => {}
            Some(have) if ours(rel, &have) => write.push((path, bytes)),
            Some(_) => return Err(Error::new(ErrorCode::Refused, format!(
                "{}: not the file Kiln placed (changed since, or put there by hand); Kiln leaves the copy as it is", path.display()))),
        }
    }
    // files of an earlier version's copy this one does not have: Kiln's own (unchanged) go
    let mut gone = Vec::new();
    for (rel, sha) in earlier.iter().flat_map(|r| r.files.iter()).filter(|(rel, _)| !listed.contains_key(*rel)) {
        crate::bundle::safe_rel_path(rel)?;
        let path = dst.join(rel);
        refuse_symlink_path(game, &path)?;
        if file_sha(&path)?.as_ref() == Some(sha) { gone.push(path); }
    }
    if write.is_empty() && gone.is_empty() && earlier.as_ref().is_some_and(|r| r.marker == want && r.files == listed) {
        return Ok(false);
    }
    for (path, bytes) in &write {
        crate::util::write_atomic_exclusive(path, bytes)?;
    }
    for path in &gone {
        std::fs::remove_file(path).map_err(io_at(path, "removing"))?;
    }
    put_placement(game, receipts, &Placement { path: dst.to_path_buf(), marker: want, files: listed, extension: None, game_folder: false })?;
    Ok(true)
}

/// Removes an XHL-Core copy Kiln placed: its files that still hold the bytes of Kiln's receipt, then the receipt.
/// Returns the files that changed since: then nothing is removed and the receipt stays. Folders left empty go; anything
/// else in the copy (XHL-Core's log) stays where it is.
fn remove_xhl_core_copy(game: &Path, receipts: &mut Vec<Placement>, r: &Placement) -> Result<Vec<String>> {
    let mut remove = Vec::new();
    let mut changed = Vec::new();
    for (rel, sha) in &r.files {
        crate::bundle::safe_rel_path(rel)?;
        let path = r.path.join(rel);
        refuse_symlink_path(game, &path)?;
        match file_sha(&path) {
            Ok(None) => {}
            Ok(Some(have)) if have == *sha => remove.push(path),
            _ => changed.push(rel.clone()),
        }
    }
    if !changed.is_empty() { return Ok(changed); }
    for path in &remove {
        std::fs::remove_file(path).map_err(io_at(path, "removing"))?;
    }
    let next: Vec<Placement> = receipts.iter().filter(|x| x.path != r.path).cloned().collect();
    save_placements(game, &next)?;
    *receipts = next;
    let mut folders: Vec<&Path> = remove.iter().flat_map(|p| p.ancestors().skip(1)).filter(|d| d.starts_with(&r.path)).collect();
    folders.sort_by(|a, b| b.components().count().cmp(&a.components().count()).then_with(|| a.cmp(b)));
    folders.dedup();
    for d in folders {
        let _ = std::fs::remove_dir(d);
    }
    Ok(Vec::new())
}

/// Why an XHL-Core copy stays (its files `changed` since Kiln placed them). Nothing loads it while chain.ini does not
/// name it.
fn xhl_core_kept(path: &Path, changed: &[String]) -> RetainedPlacement {
    RetainedPlacement { path: path.to_path_buf(), may_run: false, off: false, reason: format!(
        "files of Kiln's XHL-Core copy were changed since Kiln placed them ({}); Kiln neither updates nor removes it", some_names(changed)) }
}

/// The copies [`xhl_core_chain_dlls`] asks for, for every enabled native mod of `profile`. `prune` (activation):
/// Kiln's copies the profile does not want go, and one that cannot be placed or removed is reported, never an error.
/// Without it (play-once) nothing is removed, an error stops the start, and the copy of another version of the mod that
/// the active profile runs is not replaced.
fn place_xhl_core_copies(dirs: &DataDirs, profile: &crate::profile::Profile, game: &Path, receipts: &mut Vec<Placement>,
                         rep: &mut PlaceReport, prune: bool) -> Result<()> {
    let active = if prune { None } else { crate::profile::read_game_profile(game)? };
    let runs_active = |have: &Marker| active.iter().flat_map(|f| &f.mods.native).any(|f| f.enabled && f.id == have.id
        && f.version.as_deref() == Some(have.version.as_str()) && f.hash.as_deref() == Some(have.hash.as_str()));
    let mut wanted = Vec::new();
    for (m, src, dlls) in xhl_core_mods(dirs, profile) {
        let tag = format!("native:{}", m.id);
        let dst = xhl_core_copy(game, &m.id);
        wanted.push(dst.clone());
        let want = Marker { format: 1, kind: ModKind::Native, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        if !prune && marker_at(receipts, &dst).is_some_and(|have| *have != want && runs_active(have)) {
            return Err(Error::new(ErrorCode::Unsupported, format!("{tag}: XHL-Core runs from Kiln's copy in {}, which holds the active \
                profile's version; activate this profile to change it before playing (play-once keeps the active selection)", dst.display())));
        }
        match place_xhl_core_copy(dirs, game, &src, &dlls, &dst, want, receipts) {
            Ok(true) => rep.placed.push(tag),
            Ok(false) => rep.kept.push(tag),
            Err(e) if !prune => return Err(e),
            Err(e) => rep.warnings.push(format!("{tag}: Kiln's copy of XHL-Core in {} was not written ({}); the game loads what \
                is there now, if anything", dst.display(), reason_at(&dst, e.message))),
        }
    }
    if !prune { return Ok(()); }
    let stale: Vec<Placement> = receipts.iter().filter(|r| is_xhl_core_copy(game, r) && !wanted.contains(&r.path)).cloned().collect();
    for r in stale {
        let tag = format!("native:{}", r.marker.id);
        match remove_xhl_core_copy(game, receipts, &r) {
            Ok(changed) if changed.is_empty() => rep.removed.push(tag),
            Ok(changed) => { rep.changed.push(tag); rep.retained.push(xhl_core_kept(&r.path, &changed)); }
            Err(e) => {
                rep.changed.push(tag);
                rep.retained.push(RetainedPlacement { path: r.path.clone(), reason: reason_at(&r.path, e.message), may_run: false, off: false });
            }
        }
    }
    Ok(())
}

/// An enabled native mod that runs from an XHL-Core copy: (entry, Library folder, its DLLs as [`xhl_core_chain_dlls`]
/// names them).
type XhlCoreMod<'a> = (&'a crate::profile::ProfileMod, PathBuf, Vec<(String, String)>);

/// The enabled native mods of `profile` that run from an XHL-Core copy.
fn xhl_core_mods<'a>(dirs: &DataDirs, profile: &'a crate::profile::Profile) -> Vec<XhlCoreMod<'a>> {
    profile.mods.native.iter().filter(|m| m.enabled).filter_map(|m| {
        let r = crate::library::LibraryRef { kind: ModKind::Native, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
        let src = crate::library::path_of(dirs, &r).ok()?;
        let dlls = xhl_core_chain_dlls(&src)?;
        Some((m, src, dlls))
    }).collect()
}

/// Play-once: the XHL-Core copies the profile of one start runs from ([`place_xhl_core_copies`] without removing any).
/// Nothing is written when the profile has none.
pub fn place_xhl_core_for_once(dirs: &DataDirs, profile: &crate::profile::Profile, game: &Path) -> Result<()> {
    if xhl_core_mods(dirs, profile).is_empty() { return Ok(()); }
    let _lock = engine_lock(game, &game.join("kiln/state"))?;
    let mut receipts = placements(game)?;
    place_xhl_core_copies(dirs, profile, game, &mut receipts, &mut PlaceReport::default(), false)
}

// ---- status ----

/// A filesystem prerequisite, not evidence that the host or a mod ran successfully.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HostPrerequisite {
    pub available: bool,
    pub text: String,
}

/// Recognize XHL-Core only on configured load paths, never from a plugin DLL or a generic chain-loader marker.
/// On Unix only Wine's Z: absolute mapping is reversible here; unknown drive mappings remain unverified.
pub fn xhl_host(game_dir: &Path) -> HostPrerequisite {
    let mut paths: Vec<PathBuf> = crate::engines::scan_proxies(game_dir).into_iter()
        .map(|p| game_dir.join(p.name)).collect();
    if let Ok(ini) = crate::chain::read(game_dir) {
        for e in ini.chain.iter().chain(&ini.profile).chain(&ini.late)
            .filter(|e| e.status == crate::chain::EntryStatus::Ok)
        {
            let rel = e.path.replace('\\', "/");
            let path = if e.relative {
                if Path::new(&rel).components().any(|c| !matches!(c, std::path::Component::Normal(_))) { continue; }
                game_dir.join("kiln").join(rel)
            } else if cfg!(windows) { PathBuf::from(&e.path) }
            else if rel.get(..3).is_some_and(|p| p.eq_ignore_ascii_case("z:/")) {
                PathBuf::from(&rel[2..])
            } else { continue; };
            paths.push(path);
        }
    }
    let available = paths.into_iter().any(|p| {
        let Ok(m) = std::fs::symlink_metadata(&p) else { return false };
        if !m.is_file() || m.len() > 128 * 1024 * 1024 { return false; }
        let Ok(b) = std::fs::read(&p) else { return false };
        crate::pe::is_pe64_dll(&b) && crate::pe::parse(&b).is_ok_and(|p| p.exports.iter().any(|s| s == "DirectInput8Create"))
            && ["XHL-Native-Plugin-Host.log", "native-plugin.ini"].iter().all(|s| crate::pe::contains_text(&b, s))
    });
    HostPrerequisite { available, text: if available {
        "XHL-Core is recognized on an active proxy/chain path. Only that external host loads XHL companions; placement does not verify gameplay. On Wine/Proton, proxy loading also needs its DLL override."
    } else {
        "Requires XHL-Core: no recognized host was found on this game's active proxy/chain paths. Import XHL-Core separately as a Native mod and enable it, or install it using its author's instructions. Kiln never loads companion plugin DLLs itself; unknown Wine drive mappings cannot be verified here."
    }.into() }
}

/// One engine plugin's state file, or an external host's prerequisite report.
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
    /// SF's own runtime heartbeat, per-mod observations and errors (not implied by the adapter being hosted).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub effects: Option<crate::shroudforge::SfEffects>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub prerequisite: Option<HostPrerequisite>,
    /// ShroudForge's startup pass failed at the last start ([`crate::sf_startup::EngineNotes`]): on the `shroudforge`
    /// row whoever ran it, on the `eml` row as a pointer while ShroudForge runs the EML mods.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub startup_failed: Option<crate::sf_startup::SfStartupNote>,
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
    let loaders = crate::own_loaders::find(game_dir, view.as_ref());
    let notes = crate::own_loaders::notes(game_dir, &loaders, view.as_ref());
    let sf_startup = crate::sf_startup::EngineNotes::read(game_dir, &loaders);
    ModKind::ALL
        .into_iter()
        .filter_map(|k| {
            if k == ModKind::Xhl {
                return Some(EngineStatus { kind: k, plugin: String::new(), state: None, error: None,
                    file: String::new(), own_loader: None, effects: None, prerequisite: Some(xhl_host(game_dir)), startup_failed: None });
            }
            let f = game_dir.join("kiln").join("state").join(state_file(k)?);
            let (state, error) = match read_json_checked(game_dir, &f) {
                Ok(state) => (state, None),
                Err(e) => (None, Some(e.message)),
            };
            let effects = (k == ModKind::ShroudForge).then(|| {
                let mut effects = crate::shroudforge::effects(game_dir);
                if effects.state == "current" && state.as_ref().is_some_and(|s|
                    s["runtime"].as_str() == Some("stopped") || matches!(s["mode"].as_str(), Some("off" | "failed" | "not-installed"))) {
                    effects.state = "stopped".into();
                    effects.reason = Some("ShroudForge adapter is not running; previous mod effects are not current".into());
                    effects.mods.clear();
                }
                effects
            });
            Some(EngineStatus {
                kind: k,
                plugin: plugin_of(k).unwrap_or_default().to_string(),
                state,
                error,
                file: f.display().to_string(),
                own_loader: notes.iter().find(|n| n.loader.kind == k).cloned(),
                effects,
                prerequisite: None,
                startup_failed: sf_startup.of(k),
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
    /// The game's file differs from the shared backup; this does not identify its writer.
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

/// Retained for older command callers, but direct shared-backup restoration is unsafe.
/// The archive writer owns receipt verification, resource/container consistency and loader-cache handoff.
/// Keep those operations in that separate component rather than duplicating them in management code.
pub fn eml_restore_vanilla(_game_dir: &Path) -> Result<Vec<String>> {
    Err(Error::new(ErrorCode::Refused,
        "Direct restoration of shared archive backups cannot verify ownership. Disable EML mods and start the game through Kiln: its archive writer restores only output it can prove it wrote (earlier Kiln versions' too); other loaders' data is kept. When it cannot prove that, it leaves the data alone and says why (Logs > Engines); then enable any EML mod for one start and disable it again, or use Steam's \"Verify integrity of game files\"."))
}

#[cfg(test)]
mod tests {
    use super::*;

    std::thread_local! {
        /// The next [`rename_in`] on this thread fails.
        pub(super) static FAIL_RENAME_IN: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
        /// The next [`save_placements`] on this thread fails.
        pub(super) static FAIL_SAVE_PLACEMENTS: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
    }

    /// Greeneye mods of a test Library, by id and version (a stand-in DLL whose bytes differ per version).
    fn greeneye_mod(dirs: &DataDirs, root: &Path, id: &str, version: &str) -> crate::profile::ProfileMod {
        let src = root.join(format!("src-{id}-{version}"));
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("mod.json"), format!(r#"{{"id":"{id}","version":"{version}","dll":"m.dll","side":"client","api":2}}"#)).unwrap();
        std::fs::write(src.join("m.dll"), crate::pe::build_test_dll(&["GreeneyeModStart"], format!("{id} {version}").as_bytes())).unwrap();
        let r = crate::library::import(dirs, &[src], &mut crate::task::NoProgress).unwrap();
        let r = r.imported.iter().chain(&r.existing).find(|x| x.kind == ModKind::Greeneye).cloned().unwrap();
        crate::profile::ProfileMod { id: r.id, version: r.version, hash: r.hash, enabled: true }
    }

    fn greeneye_place(dirs: &DataDirs, game: &Path, mods: Vec<crate::profile::ProfileMod>) -> PlaceReport {
        let mut p = crate::profile::create(dirs, "p", None).unwrap();
        p.mods.greeneye = mods;
        place(dirs, &p, game).unwrap()
    }

    /// Review R3: a receipt write that fails for one Greeneye mod must not leave its change in the receipt list a later
    /// save of the same run writes. Before, the list said version 2 for a copy rolled back to version 1 (Kiln's own
    /// copy then "changed" for good), or had no receipt for a copy whose removal was rolled back (taken for the
    /// player's from then on).
    #[test]
    fn a_failed_greeneye_receipt_write_leaves_the_receipts_as_on_disk() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "greeneye-receipts").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let game = tmp.path().join("game");
        std::fs::create_dir_all(game.join("kiln")).unwrap();
        let (a1, b1) = (greeneye_mod(&dirs, tmp.path(), "a", "1.0.0"), greeneye_mod(&dirs, tmp.path(), "b", "1.0.0"));
        let (a2, b2) = (greeneye_mod(&dirs, tmp.path(), "a", "2.0.0"), greeneye_mod(&dirs, tmp.path(), "b", "2.0.0"));
        assert_eq!(greeneye_place(&dirs, &game, vec![a1.clone(), b1.clone()]).placed, ["greeneye:a", "greeneye:b"]);
        // update: a's receipt write fails (its copy goes back to 1.0.0), b's succeeds in the same run
        FAIL_SAVE_PLACEMENTS.with(|f| f.set(true));
        let rep = greeneye_place(&dirs, &game, vec![a2.clone(), b2.clone()]);
        assert_eq!(rep.placed, ["greeneye:b"], "{rep:?}");
        assert_eq!(marker(&game.join("greeneye/a")).unwrap().version, "1.0.0");
        let rep = greeneye_place(&dirs, &game, vec![a2.clone(), b2.clone()]);
        assert_eq!((rep.placed.as_slice(), rep.retained.len()), (&["greeneye:a".to_string()][..], 0), "Kiln's copy is still Kiln's: {rep:?}");
        // removal: both switched off; the first receipt write fails (that copy comes back), the second removal succeeds
        // in the same run
        let off = |m: &crate::profile::ProfileMod| crate::profile::ProfileMod { enabled: false, ..m.clone() };
        FAIL_SAVE_PLACEMENTS.with(|f| f.set(true));
        let rep = greeneye_place(&dirs, &game, vec![off(&a2), off(&b2)]);
        assert_eq!(rep.removed.len(), 1, "{rep:?}");
        let left = if rep.removed[0] == "greeneye:a" { "b" } else { "a" };
        assert!(game.join("greeneye").join(left).is_dir());
        let rep = greeneye_place(&dirs, &game, vec![off(&a2), off(&b2)]);
        assert_eq!(rep.removed, [format!("greeneye:{left}")], "still Kiln's copy, removed now: {rep:?}");
        assert!(!game.join("greeneye").join(left).exists());
    }

    /// A failed publication rolls back only that mod, keeps its old receipt, and lets the next mod publish.
    #[test]
    fn failed_publication_keeps_its_receipt_and_places_the_next_mod() {
        use crate::own_loaders::{Delivery, OwnLoader, Via};
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "publish-receipts").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let make = |kind: ModKind, id: &str, version: &str, game_folder: bool| {
            let src = tmp.path().join(format!("{}-{id}-{version}-{game_folder}", kind.as_str()));
            std::fs::create_dir_all(&src).unwrap();
            let content = if kind == ModKind::Native { format!("mods/{id}/config.txt") } else { "config.txt".into() };
            std::fs::create_dir_all(src.join(&content).parent().unwrap()).unwrap();
            std::fs::write(src.join(&content), version).unwrap();
            if kind == ModKind::ShroudForge {
                std::fs::write(src.join("mod.json"), format!(r#"{{"id":"{id}","name":"Test","version":"{version}"}}"#)).unwrap();
                std::fs::write(src.join("extended.mod.json"), br#"{"schemaVersion":1,"enabled":true,"settings":{}}"#).unwrap();
            }
            let hash = crate::hash::dirhash(&src).unwrap();
            let rec = ImportRecord { format: 1, kind, id: id.into(), version: version.into(), hash: hash.clone(), imported: crate::util::now_rfc3339(),
                source: None, size: 0, name: None, side: None, clients: None, file: None, manifest_id: None,
                game_folder: game_folder.then(|| id.into()) };
            crate::library::commit_folder(&dirs, &src, rec, &mut Default::default()).unwrap();
            crate::profile::ProfileMod { id: id.into(), version: version.into(), hash, enabled: true }
        };
        for (kind, folder, game_folder) in [(ModKind::Set, SET_MODS, false), (ModKind::ShroudForge, "mods", false),
            (ModKind::Eml, "mods", false), (ModKind::Emf, "emf/mods", false), (ModKind::Flame, "mods", false),
            (ModKind::Shroudtopia, "mods", false), (ModKind::Eml, "mods", true), (ModKind::Xhl, "mods", true), (ModKind::Native, "mods", true)] {
            let loaders = vec![OwnLoader { kind, engine: crate::engines::Engine::Emf, file: "test.dll".into(), via: Via::Chain,
                folder: folder.into(), delivery: Delivery::Placed }];
            let a1 = make(kind, "a", "1.0.0", game_folder);
            let b1 = make(kind, "b", "1.0.0", game_folder);
            let a2 = make(kind, "a", "2.0.0", game_folder);
            let b2 = make(kind, "b", "2.0.0", game_folder);
            for updating in [false, true] {
                let game = tmp.path().join(format!("game-{}-{game_folder}-{updating}", kind.as_str()));
                let mut p = crate::profile::create(&dirs, &format!("{}-{game_folder}-{updating}", kind.as_str()), None).unwrap();
                if updating {
                    *p.mods.list_mut(kind) = vec![a1.clone(), b1.clone()];
                    place_with(&dirs, &p, &game, &loaders).unwrap();
                }
                let before = placements(&game).unwrap();
                *p.mods.list_mut(kind) = vec![a2.clone(), b2.clone()];
                FAIL_SAVE_PLACEMENTS.with(|f| f.set(true));
                let result = place_with(&dirs, &p, &game, &loaders);
                assert!(result.is_ok(), "one failed publication must not block the next mod: {kind:?}, updating={updating}: {result:?}");
                let rep = result.unwrap();
                assert_eq!(rep.placed, [format!("{}:b", kind.as_str())], "{rep:?}");
                let a = game.join(folder).join("a");
                if updating {
                    assert_eq!(marker(&a).unwrap().version, "1.0.0");
                    assert!(rep.retained.iter().any(|r| r.path == a && r.may_run), "{rep:?}");
                } else {
                    assert!(!a.exists());
                    assert!(rep.warnings.iter().any(|w| w.contains("injected receipt write failure")), "{rep:?}");
                }
                let current = placements(&game).unwrap();
                assert_eq!(serde_json::to_value(current.iter().filter(|r| r.marker.id == "a").collect::<Vec<_>>()).unwrap(),
                    serde_json::to_value(before.iter().filter(|r| r.marker.id == "a").collect::<Vec<_>>()).unwrap());
                assert_eq!(marker(&game.join(folder).join("b")).unwrap().version, "2.0.0");
                let retry = place_with(&dirs, &p, &game, &loaders).unwrap();
                assert_eq!(retry.placed, [format!("{}:a", kind.as_str())], "retry stays owned: {retry:?}");
            }
        }
    }

    #[test]
    fn failed_sf_switch_rolls_back_its_receipt_and_switches_the_next_mod() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "sf-switch-receipts").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let mut sources = Vec::new();
        for id in ["a", "b"] {
            let src = tmp.path().join(id);
            std::fs::create_dir_all(&src).unwrap();
            std::fs::write(src.join("mod.json"), format!(r#"{{"id":"{id}","name":"Test","version":"1.0.0"}}"#)).unwrap();
            std::fs::write(src.join("extended.mod.json"), br#"{"schemaVersion":1,"launcher":"SF","enabled":true,"settings":{}}"#).unwrap();
            sources.push(src);
        }
        let imported = crate::library::import(&dirs, &sources, &mut crate::task::NoProgress).unwrap();
        assert_eq!(imported.imported.len(), 2, "{imported:?}");
        for listed in [false, true] {
            let game = tmp.path().join(format!("game-{listed}"));
            let mut p = crate::profile::create(&dirs, &format!("switch-{listed}"), None).unwrap();
            p.mods.shroudforge = imported.imported.iter().map(|r| crate::profile::ProfileMod {
                id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true }).collect();
            place(&dirs, &p, &game).unwrap();
            let a = game.join("mods/a");
            let before = files_of(&a);
            let receipt = placements(&game).unwrap().into_iter().find(|r| r.marker.id == "a").unwrap();
            if listed { for m in &mut p.mods.shroudforge { m.enabled = false; } } else { p.mods.shroudforge.clear(); }
            FAIL_SAVE_PLACEMENTS.with(|f| f.set(true));
            let result = place(&dirs, &p, &game);
            assert!(result.is_ok(), "one failed SF switch must not block the others: listed={listed}: {result:?}");
            let rep = result.unwrap();
            assert_eq!(rep.switched, ["shroudforge:b"], "{rep:?}");
            assert_eq!(files_of(&a), before, "failed switch is rolled back byte for byte");
            assert!(!crate::shroudforge::read_mod(&game.join("mods/b")).unwrap().enabled);
            assert!(rep.retained.iter().any(|r| r.path == a && r.may_run), "{rep:?}");
            let after = placements(&game).unwrap().into_iter().find(|r| r.marker.id == "a").unwrap();
            assert_eq!(serde_json::to_value(&after).unwrap(), serde_json::to_value(&receipt).unwrap());
            let retry = place(&dirs, &p, &game).unwrap();
            assert_eq!(retry.switched, ["shroudforge:a"], "retry remains owned: {retry:?}");
            assert!(!crate::shroudforge::read_mod(&a).unwrap().enabled);
            assert_eq!(unplace(&dirs, &game).unwrap().removed.len(), 2, "successful switches keep exact cleanup receipts");
        }
    }

    /// A failure for one stale copy must not leak its rolled-back removal into the next copy's saved receipt list.
    #[test]
    fn failed_stale_removal_keeps_its_receipt_and_removes_the_next_mod() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "stale-receipts").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let profile = crate::profile::create(&dirs, "empty", None).unwrap();
        for (kind, rel, game_folder) in [(ModKind::Set, SET_MODS, false), (ModKind::Eml, "mods", false),
            (ModKind::Emf, "emf/mods", false), (ModKind::Flame, "mods", false), (ModKind::Shroudtopia, "mods", false),
            (ModKind::Native, "mods", true), (ModKind::Xhl, "mods", true), (ModKind::Eml, "mods", true)] {
            let game = tmp.path().join(format!("{}-{game_folder}", kind.as_str()));
            let mut receipts = Vec::new();
            for id in ["a", "b"] {
                let dst = game.join(rel).join(id);
                std::fs::create_dir_all(&dst).unwrap();
                std::fs::write(dst.join("config.txt"), b"defaults").unwrap();
                let marker = Marker { format: 1, kind, id: id.into(), version: "1.0.0".into(), hash: crate::hash::dirhash(&dst).unwrap() };
                crate::util::write_json_atomic(&dst.join(MARKER), &marker).unwrap();
                receipts.push(Placement { path: dst, marker, files: placement_files(&game.join(rel).join(id), kind).unwrap(),
                    extension: None, game_folder });
            }
            save_placements(&game, &receipts).unwrap();
            FAIL_SAVE_PLACEMENTS.with(|f| f.set(true));
            let result = place(&dirs, &profile, &game);
            assert!(result.is_ok(), "one failed receipt must not stop the next removal: {kind:?}: {result:?}");
            let rep = result.unwrap();
            assert_eq!(rep.removed, [format!("{}:b", kind.as_str())], "{rep:?}");
            assert!(rep.retained.iter().any(|r| r.path == game.join(rel).join("a") && r.may_run), "{rep:?}");
            assert_eq!(serde_json::to_value(placements(&game).unwrap()).unwrap(), serde_json::to_value(&receipts[..1]).unwrap());
            assert_eq!(std::fs::read(game.join(rel).join("a/config.txt")).unwrap(), b"defaults");
            assert!(!game.join(rel).join("b").exists());
            let rep = place(&dirs, &profile, &game).unwrap();
            assert_eq!(rep.removed, [format!("{}:a", kind.as_str())], "retry remains owned: {rep:?}");
            assert!(placements(&game).unwrap().is_empty());
        }
    }

    #[test]
    fn parked_settings_directories_never_return_as_live_mod_files() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "parked-settings").unwrap();
        let game = tmp.path();
        let kind = ModKind::Native;
        let key = "package/settings";
        stash_settings(game, kind, key, &[("config.txt/options.ini".into(), b"older settings".to_vec())]).unwrap();
        // A newer version writes a file where the older one had a directory. The old directory is recovery only.
        stash_settings(game, kind, key, &[("config.txt".into(), b"current settings".to_vec())]).unwrap();
        let restored = stashed_settings(game, kind, key).unwrap();
        assert_eq!(restored, [("config.txt".into(), b"current settings".to_vec())], "recovery directories must not be restored");
        drop_stash(game, kind, key, &restored).unwrap();
        assert_eq!(std::fs::read(stash_dir(game, kind, key).join("config.txt.kept-1/options.ini")).unwrap(), b"older settings");
        assert!(!stash_dir(game, kind, key).join("config.txt").exists());
    }

    /// Review R4: when the copy cannot be published after an older Kiln's runtime files were split off the Library entry,
    /// they go back into the entry. Left in the kept folder, the mod ran from the stripped entry, wrote defaults, and
    /// the next placement parked the player's real files as `.kept-1` in favour of those defaults.
    #[test]
    fn a_failed_greeneye_placement_puts_split_files_back() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "greeneye-split-undo").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let game = tmp.path().join("game");
        std::fs::create_dir_all(game.join("kiln")).unwrap();
        let a = greeneye_mod(&dirs, tmp.path(), "a", "1.0.0");
        let lib = crate::library::path_of(&dirs, &crate::library::LibraryRef { kind: ModKind::Greeneye, id: a.id.clone(),
            version: a.version.clone(), hash: a.hash.clone() }).unwrap();
        std::thread::sleep(std::time::Duration::from_millis(20));
        std::fs::write(lib.join("settings.ini"), b"the player's real settings").unwrap();
        FAIL_RENAME_IN.with(|f| f.set(true));
        let rep = greeneye_place(&dirs, &game, vec![a.clone()]);
        assert!(rep.placed.is_empty() && !game.join("greeneye/a").exists(), "{rep:?}");
        assert_eq!(std::fs::read(lib.join("settings.ini")).unwrap(), b"the player's real settings", "back in the entry it runs from");
        assert!(!kept_dir(&game, ModKind::Greeneye, "a").join("settings.ini").exists());
        // the next placement takes them along, nothing is parked
        let rep = greeneye_place(&dirs, &game, vec![a.clone()]);
        assert_eq!(rep.placed, ["greeneye:a"], "{rep:?}");
        assert_eq!(std::fs::read(game.join("greeneye/a/settings.ini")).unwrap(), b"the player's real settings");
        assert_eq!(crate::hash::dirhash(&lib).unwrap(), a.hash);
        assert!(files_of(&game.join("kiln")).keys().all(|k| !k.contains(".kept-")), "{:?}", files_of(&game.join("kiln")).keys());
    }


    fn files_of(dir: &Path) -> std::collections::BTreeMap<String, Vec<u8>> {
        let mut out = std::collections::BTreeMap::new();
        let mut stack = vec![dir.to_path_buf()];
        while let Some(d) = stack.pop() {
            for e in std::fs::read_dir(&d).into_iter().flatten().flatten() {
                let p = e.path();
                if p.is_dir() { stack.push(p); continue; }
                out.insert(p.strip_prefix(dir).unwrap().to_string_lossy().into_owned(), std::fs::read(&p).unwrap());
            }
        }
        out
    }

    /// The failure paths of [`copy_in_with`] after staging: the new copy cannot be renamed in, or its receipt cannot be
    /// written with player files carried from the old copy, from the kept folder, or with stashed settings. Each time
    /// the old copy comes back byte for byte and no recovery file is lost or pruned.
    #[test]
    fn failed_publication_restores_the_old_copy_and_keeps_every_recovery_file() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "engine-publish-rollback").unwrap();
        let game = tmp.path();
        let src = game.join("source");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("init.lua"), b"new").unwrap();
        std::fs::write(src.join("notes.txt"), b"new version's notes").unwrap();
        let marker = Marker { format: 1, kind: ModKind::Set, id: "test".into(), version: "2.0.0".into(), hash: crate::hash::dirhash(&src).unwrap() };
        let roots = Roots { data: game, game };
        let dst = game.join("shrouded_engine_tweaks/mods/test");
        let kept = kept_dir(game, ModKind::Set, "test");
        let old_copy = || {
            let _ = std::fs::remove_dir_all(&dst);
            std::fs::create_dir_all(&dst).unwrap();
            std::fs::write(dst.join("init.lua"), b"old").unwrap();
            std::fs::write(dst.join("settings.json"), b"player settings").unwrap();
            std::fs::write(dst.join("notes.txt"), b"player notes").unwrap();
        };
        let player = vec!["notes.txt".to_string(), "settings.json".to_string()];
        let injected = |_: &Placement| Err(Error::invalid("injected receipt failure"));

        // 1. the rename into place fails: the old copy is renamed back, the receipt is never written
        old_copy();
        let before = files_of(&dst);
        FAIL_RENAME_IN.with(|f| f.set(true));
        let carry = Carry { from: &dst, entries: player.clone(), from_kept: false };
        let e = copy_in(roots, &src, &dst, &marker, &[], true, Some(carry), |_| panic!("no receipt for a copy that is not in place")).unwrap_err();
        assert!(e.message.contains("placing"), "{e:?}");
        assert_eq!(files_of(&dst), before);

        // 2. the receipt write fails with files carried from the old copy: the old copy is back as it was
        let carry = Carry { from: &dst, entries: player.clone(), from_kept: false };
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, Some(carry), injected).is_err());
        assert_eq!(files_of(&dst), before);

        // 3. ... with files carried from the kept folder: they stay in the kept folder
        std::fs::remove_dir_all(&dst).unwrap();
        std::fs::create_dir_all(&kept).unwrap();
        std::fs::write(kept.join("settings.json"), b"kept settings").unwrap();
        std::fs::write(kept.join("notes.txt"), b"kept notes").unwrap();
        let kept_before = files_of(&kept);
        let carry = kept_carry(game, &kept, ModKind::Set).unwrap();
        assert!(carry.is_some());
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, carry, injected).is_err());
        assert!(!dst.exists());
        assert_eq!(files_of(&kept), kept_before, "nothing leaves the kept folder for a copy that was rolled back");
        std::fs::remove_dir_all(game.join(KEPT_MOD_FILES)).unwrap();

        // 4. ... with stashed settings of a game-folder copy: the old copy is back without them
        let eml = Marker { kind: ModKind::Eml, ..marker.clone() };
        let gdst = game.join("mods/test");
        std::fs::create_dir_all(&gdst).unwrap();
        std::fs::write(gdst.join("config.txt"), b"old copy's settings").unwrap();
        let gbefore = files_of(&gdst);
        let stashed = [("config.txt".to_string(), b"stashed settings".to_vec())];
        assert!(copy_in_with(roots, &src, &gdst, &eml, &[], true, None, &stashed, Some("test"), false, injected).is_err());
        assert_eq!(files_of(&gdst), gbefore);
        // no staging folder is left beside the copies
        for parent in [dst.parent().unwrap(), gdst.parent().unwrap()] {
            let left: Vec<_> = std::fs::read_dir(parent).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned())
                .filter(|n| n.starts_with('.')).collect();
            assert!(left.is_empty(), "{left:?}");
        }
    }

    /// A file put back from the kept folder leaves it only while the kept file still has the bytes that went back: a
    /// value edited there while the copy was published stays.
    #[test]
    fn files_put_back_from_the_kept_folder_leave_it_only_with_the_bytes_that_went_back() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "kept-prune").unwrap();
        let game = tmp.path();
        let src = game.join("source");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("init.lua"), b"code").unwrap();
        let marker = Marker { format: 1, kind: ModKind::Set, id: "test".into(), version: "1.0.0".into(), hash: crate::hash::dirhash(&src).unwrap() };
        let roots = Roots { data: game, game };
        let dst = game.join("shrouded_engine_tweaks/mods/test");
        let kept = kept_dir(game, ModKind::Set, "test");
        std::fs::create_dir_all(&kept).unwrap();
        std::fs::write(kept.join("settings.json"), b"earlier").unwrap();
        std::fs::write(kept.join("log.txt"), b"went back").unwrap();
        let carry = kept_carry(game, &kept, ModKind::Set).unwrap();
        let (said, _) = copy_in(roots, &src, &dst, &marker, &[], true, carry, |_| {
            std::fs::write(kept.join("settings.json"), b"edited meanwhile").unwrap();
            Ok(())
        }).unwrap();
        assert_eq!(std::fs::read(dst.join("settings.json")).unwrap(), b"earlier");
        assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), b"edited meanwhile", "changed recovery bytes are never deleted");
        assert!(!kept.join("log.txt").exists(), "an exact copy went back: {said:?}");
    }

    /// A receipt and matching marker prove content, not that an arbitrary path is a placement destination.
    #[test]
    fn stale_receipts_never_remove_copies_outside_their_engine_folder() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "placement-receipt-roots").unwrap();
        let dirs = DataDirs::at(tmp.path().join("data"));
        let profile = crate::profile::create(&dirs, "empty", None).unwrap();
        for kind in [ModKind::Eml, ModKind::Emf, ModKind::Flame, ModKind::Shroudtopia,
                     ModKind::Native, ModKind::Xhl, ModKind::Greeneye] {
            for outside in [true, false] {
                let game = tmp.path().join(format!("game-{}-{outside}", kind.as_str()));
                let dst = if outside { tmp.path().join(format!("outside-{}", kind.as_str())) }
                    else { game.join("player-backups/mod") };
                std::fs::create_dir_all(&dst).unwrap();
                std::fs::write(dst.join("config.txt"), b"player backup").unwrap();
                let marker = Marker { format: 1, kind, id: "mod".into(), version: "1.0.0".into(), hash: crate::hash::dirhash(&dst).unwrap() };
                crate::util::write_json_atomic(&dst.join(MARKER), &marker).unwrap();
                let receipt = Placement { path: dst.clone(), marker, files: placement_files(&dst, kind).unwrap(),
                    extension: None, game_folder: matches!(kind, ModKind::Native | ModKind::Xhl) };
                save_placements(&game, &[receipt]).unwrap();
                let rep = place(&dirs, &profile, &game).unwrap();
                assert_eq!(std::fs::read(dst.join("config.txt")).ok().as_deref(), Some(b"player backup".as_slice()),
                    "{} outside={outside}: an invalid receipt must not authorize deletion: {rep:?}", kind.as_str());
                assert!(rep.retained.iter().any(|r| r.path == dst && r.reason.contains("not an engine mod folder")), "{rep:?}");
                assert_eq!(placements(&game).unwrap().len(), 1, "the unverified receipt stays");
            }
        }
    }

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
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, None, |_| Err(Error::invalid("injected receipt failure"))).is_err());
        assert_eq!(std::fs::read(dst.join("init.lua")).unwrap(), b"old");
        assert!(remove_copy(tmp.path(), &dst, || Err(Error::invalid("injected receipt failure"))).is_err());
        assert_eq!(std::fs::read(dst.join("init.lua")).unwrap(), b"old");
        std::fs::remove_dir_all(&dst).unwrap();
        assert!(copy_in(roots, &src, &dst, &marker, &[], true, None, |_| Err(Error::invalid("injected receipt failure"))).is_err());
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
    fn retaining_files_never_discards_earlier_player_files_or_directories() {
        let tmp = crate::library::TempDir::new(&std::env::temp_dir(), "kept-collisions").unwrap();
        let game = tmp.path();
        let src = game.join("mods/test");
        let kept = kept_dir(game, ModKind::Set, "test");
        std::fs::create_dir_all(kept.join("settings.json")).unwrap();
        std::fs::write(kept.join("settings.json/old.txt"), b"earlier directory").unwrap();
        std::fs::write(kept.join("data"), b"earlier file").unwrap();
        std::fs::create_dir_all(src.join("data")).unwrap();
        std::fs::write(src.join("settings.json"), b"latest settings").unwrap();
        std::fs::write(src.join("data/log.txt"), b"latest log").unwrap();
        let player = vec!["data/log.txt".into(), "settings.json".into()];
        keep_player_files(game, &src, ModKind::Set, "test", &player).unwrap();
        assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), b"latest settings");
        assert_eq!(std::fs::read(kept.join("settings.json.kept-1/old.txt")).unwrap(), b"earlier directory");
        assert_eq!(std::fs::read(kept.join("data/log.txt")).unwrap(), b"latest log");
        assert_eq!(std::fs::read(kept.join("data.kept-1")).unwrap(), b"earlier file");
        std::fs::write(src.join("settings.json"), b"newer settings").unwrap();
        keep_player_files(game, &src, ModKind::Set, "test", &player).unwrap();
        assert_eq!(std::fs::read(kept.join("settings.json.kept-2")).unwrap(), b"latest settings");
        assert_eq!(std::fs::read(kept.join("settings.json")).unwrap(), b"newer settings");
    }

    #[test]
    fn only_documented_shipped_settings_may_change() {
        use std::collections::BTreeMap;
        let before = BTreeMap::from([("config.txt".into(), "old".into()), ("src/mod.lua".into(), "lua".into())]);
        let mut now = before.clone();
        now.insert("config.txt".into(), "player".into());
        now.insert("notes.json".into(), "new".into());
        assert_eq!(player_entries_of(now.clone(), &before, ModKind::Eml).unwrap(), ["config.txt", "notes.json"]);
        assert!(player_entries_of(now.clone(), &before, ModKind::Emf).is_none());
        now.insert("src/mod.lua".into(), "edited".into());
        assert!(player_entries_of(now, &before, ModKind::Eml).is_none());
        assert!(!is_settings_file(ModKind::Eml, "native-plugin.ini"));
        assert!(!is_settings_file(ModKind::Eml, "mod.json"));
    }

    /// manage-import F5, native-core-sf F4: one table of folder names for the three validators of Copy mode's
    /// `../mods/<folder>` (this one, native/core/tests/profile_test.cpp, schema/tests/profile_review.mjs).
    #[test]
    fn game_folder_names_follow_the_shared_table() {
        let file = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../schema/fixtures/profile/game-folder-names.json");
        let t: serde_json::Value = serde_json::from_slice(&std::fs::read(file).unwrap()).unwrap();
        let mut n = 0;
        for (verdict, want) in [("accept", true), ("reject", false)] {
            for name in t[verdict].as_array().unwrap() {
                let name = name.as_str().unwrap();
                assert_eq!(is_game_folder_name(name), want, "{verdict}: {name:?}");
                n += 1;
            }
        }
        assert!(n > 50);
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
    fn every_imported_kind_has_an_explicit_execution_or_companion_contract() {
        for k in IMPORTED {
            if k == ModKind::Xhl {
                assert!(plugin_of(k).is_none() && state_file(k).is_none() && k.engine().is_none());
            } else {
                assert!(plugin_of(k).is_some(), "{k:?}");
                assert!(state_file(k).is_some(), "{k:?}");
            }
            assert!(k.is_engine());
            assert!(default_side(k) == (Side::Server, Clients::None) || runs_yet(k), "{k:?}");
        }
        // every engine kind but EMF (Kiln's own emf kind) has an importer
        for k in ModKind::ALL.into_iter().filter(|k| k.is_engine() && *k != ModKind::Emf) {
            assert!(IMPORTED.contains(&k), "{k:?}");
        }
    }
}
