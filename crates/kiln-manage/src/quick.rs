//! The simple flow: drop a mod on the launcher and it is in the game at the next start. One call imports the files
//! into the library, adds what they hold to the game's active profile (enabled), and writes the game's
//! `kiln/profile.json` again. A first-time player has no profile yet: [`ensure_default`] makes "My mods" (every
//! drop-in folder on, so what loaded before still loads) and activates it. Players who already have profiles keep
//! them: with profiles but none active in the game, the files are only imported. Modpacks are never added here: they
//! go through the consent flow (`modpack::install`), so [`add`] lists them instead.
//! [`revert`] undoes an add (the toast's Undo): the profile entries go back, the library copies stay.

use crate::game::{GameInstall, GameKind};
use crate::library::{self, ImportReport, LibraryRef, Skipped};
use crate::manifest::ModKind;
use crate::paths::DataDirs;
use crate::profile::{self, Activation, Profile, ProfileMod, ProfileMods};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// The name of the profile made for a first-time player.
pub const DEFAULT_PROFILE: &str = "My mods";

const MODPACK_MANIFEST: &str = "kiln-modpack.json";

/// One profile entry an add changed: new to the profile (`before` None), another version replaced, or the same one
/// switched on. [`revert`] puts `before` back.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProfileChange {
    pub kind: ModKind,
    pub before: Option<ProfileMod>,
    pub after: ProfileMod,
}

/// A library entry in the profile, with its display name.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AddedMod {
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    pub hash: String,
    pub name: String,
}

/// A mod an added mod requires that the profile does not have (enabled).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MissingMod {
    pub id: String,
    /// The version range asked for ("*": any).
    pub range: String,
    /// The id of the mod that requires it.
    pub by: String,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AddReport {
    /// What the library took (and skipped).
    pub import: ImportReport,
    /// The profile the mods went into; None when nothing could be added, or when profiles exist but none is active in
    /// this game (the files are then only in the library).
    pub profile_id: Option<String>,
    pub profile_name: Option<String>,
    /// "My mods" was made now (and activated).
    pub created: bool,
    /// Now in the profile and enabled: new, another version, or switched on.
    pub added: Vec<AddedMod>,
    /// Already in the profile as they are.
    pub already: Vec<AddedMod>,
    /// What [`revert`] puts back.
    pub changes: Vec<ProfileChange>,
    /// The game's profile.json was written now.
    pub applied: bool,
    /// The game runs: the profile is saved and applies at the next start (Play writes it again first).
    pub running: bool,
    pub activation: Option<Activation>,
    /// Mods the added ones require that the profile lacks.
    pub missing: Vec<MissingMod>,
    /// Modpack zips among the paths: not imported, they need the player's consent (`modpack::install`).
    pub modpacks: Vec<String>,
}

/// The profile [`add`] puts mods into.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Target {
    pub profile: Profile,
    /// Made now ("My mods").
    pub created: bool,
    /// The activation that made it active (only when created).
    pub activation: Option<Activation>,
}

fn mods_of(m: &mut ProfileMods, k: ModKind) -> &mut Vec<ProfileMod> {
    match k {
        ModKind::Kiln => &mut m.kiln,
        ModKind::Native => &mut m.native,
        ModKind::Plugin => &mut m.plugin,
        ModKind::Emf => &mut m.emf,
    }
}

fn kiln_in_game(install: &GameInstall) -> Result<()> {
    if install.kind != GameKind::Client {
        return Err(Error::invalid("mods are added to a game client's profile; a server takes them on its Mods page"));
    }
    if !install.path.join(crate::paths::game::KILN).is_dir() {
        return Err(Error::new(ErrorCode::Unsupported, "Kiln is not in this game yet: install it first."));
    }
    Ok(())
}

/// What the game's profile.json names: Ok(Some) a profile of this data folder, Ok(None) no profile.json, Err(()) a
/// profile.json this data folder does not know (another launcher or kilnctl data folder: left alone).
fn active_here(dirs: &DataDirs, install: &GameInstall) -> Result<std::result::Result<Option<Profile>, ()>> {
    match profile::read_game_profile(&install.path)? {
        None => Ok(Ok(None)),
        Some(f) => Ok(profile::get(dirs, &f.id).map(Some).map_err(|_| ())),
    }
}

/// Makes "My mods" (every drop-in folder on) and activates it; a failed activation leaves no profile behind.
fn create_default(dirs: &DataDirs, install: &GameInstall) -> Result<Target> {
    let made = profile::create(dirs, DEFAULT_PROFILE, None)?;
    let id = made.id.clone();
    let undo = |e: Error| {
        let _ = std::fs::remove_dir_all(dirs.profile(&id));
        e
    };
    let mut p = made;
    // what loaded before (the legacy layout loads every drop-in folder) keeps loading
    p.dropins = ModKind::ALL.to_vec();
    let p = profile::save(dirs, &p).map_err(undo)?;
    let a = profile::activate(dirs, &p.id, install).map_err(undo)?;
    Ok(Target { profile: p, created: true, activation: Some(a) })
}

/// The profile the simple flow uses in a game: the active one; else, when this data folder has no profile at all, a
/// new "My mods", activated. None when profiles exist but none is active here, or the game's profile.json names a
/// profile of another data folder: those players chose that, and are left alone.
pub fn ensure_default(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Target>> {
    let running = crate::proc::find_game(install)?.is_some();
    ensure_default_with(dirs, install, running)
}

/// [`ensure_default`] with the game's running state given (tests).
pub fn ensure_default_with(dirs: &DataDirs, install: &GameInstall, running: bool) -> Result<Option<Target>> {
    kiln_in_game(install)?;
    match active_here(dirs, install)? {
        Ok(Some(p)) => Ok(Some(Target { profile: p, created: false, activation: None })),
        Err(()) => Ok(None),
        Ok(None) if !profile::list(dirs)?.is_empty() => Ok(None),
        Ok(None) if running => Err(Error::new(ErrorCode::GameRunning, "Close the game first: the new profile is activated in the game folder.")),
        Ok(None) => create_default(dirs, install).map(Some),
    }
}

/// A modpack: a folder or zip with kiln-modpack.json at its root.
pub fn is_modpack(path: &Path) -> bool {
    if path.is_dir() {
        return path.join(MODPACK_MANIFEST).is_file();
    }
    path.to_string_lossy().to_lowercase().ends_with(".zip") && zip_has(path, MODPACK_MANIFEST)
}

#[cfg(feature = "zip")]
fn zip_has(zip: &Path, name: &str) -> bool {
    let Ok(f) = std::fs::File::open(zip) else { return false };
    let Ok(mut a) = zip::ZipArchive::new(f) else { return false };
    a.by_name(name).is_ok()
}

#[cfg(not(feature = "zip"))]
fn zip_has(_zip: &Path, _name: &str) -> bool {
    false
}

/// Puts `refs` into `mods` (enabled; another version of the same mod is replaced). Returns the changes, and the refs
/// that were in already as they are.
fn merge(mods: &mut ProfileMods, refs: &[LibraryRef]) -> (Vec<ProfileChange>, Vec<LibraryRef>) {
    let (mut changes, mut already) = (Vec::new(), Vec::new());
    for r in refs {
        let after = ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true };
        let list = mods_of(mods, r.kind);
        match list.iter().position(|m| m.id == r.id) {
            Some(i) if list[i] == after => already.push(r.clone()),
            Some(i) => {
                let before = std::mem::replace(&mut list[i], after.clone());
                changes.push(ProfileChange { kind: r.kind, before: Some(before), after });
            }
            None => {
                list.push(after.clone());
                changes.push(ProfileChange { kind: r.kind, before: None, after });
            }
        }
    }
    (changes, already)
}

fn named(dirs: &DataDirs, r: &LibraryRef) -> AddedMod {
    let name = library::record(dirs, r).ok().and_then(|rec| rec.name).filter(|n| !n.trim().is_empty()).unwrap_or_else(|| r.id.clone());
    AddedMod { kind: r.kind, id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), name }
}

/// What the added Kiln and native mods require that `profile` lacks (enabled, any version).
fn missing_for(dirs: &DataDirs, profile: &Profile, added: &[ProfileChange]) -> Vec<MissingMod> {
    let has = |id: &str| {
        profile.mods.kiln.iter().chain(&profile.mods.native).any(|m| m.enabled && m.id == id)
    };
    let mut out: Vec<MissingMod> = Vec::new();
    let mut need = |id: &str, range: &str, by: &str| {
        if id != crate::manifest::KILN_ID && !has(id) && !out.iter().any(|m| m.id == id) {
            out.push(MissingMod { id: id.into(), range: range.into(), by: by.into() });
        }
    };
    for c in added {
        let r = LibraryRef { kind: c.kind, id: c.after.id.clone(), version: c.after.version.clone(), hash: c.after.hash.clone() };
        let Ok(folder) = library::path_of(dirs, &r) else { continue };
        match c.kind {
            ModKind::Kiln => {
                if let Ok(m) = crate::manifest::read_kiln(&folder) {
                    for d in m.dependencies.iter().filter(|d| !d.optional) {
                        need(&d.id, &d.range, &m.id);
                    }
                }
            }
            ModKind::Native => {
                if let Ok(m) = crate::manifest::read_native(&folder) {
                    for id in &m.requires {
                        need(id, "*", &m.id);
                    }
                }
            }
            _ => {}
        }
    }
    out
}

/// Imports `paths` and adds what they hold to the game's active profile (making "My mods" for a first-time player),
/// then writes the game's profile.json again. Refused before anything changes when Kiln is not in the game, or when
/// "My mods" would have to be activated while the game runs. When writing profile.json fails, the profile change is
/// undone (and a profile made for it removed) before the error returns; the library keeps what it imported.
pub fn add(dirs: &DataDirs, install: &GameInstall, paths: &[PathBuf], progress: &mut dyn Progress) -> Result<AddReport> {
    let running = crate::proc::find_game(install)?.is_some();
    add_with(dirs, install, paths, running, progress)
}

/// [`add`] with the game's running state given (tests).
pub fn add_with(dirs: &DataDirs, install: &GameInstall, paths: &[PathBuf], running: bool, progress: &mut dyn Progress) -> Result<AddReport> {
    if paths.is_empty() {
        return Err(Error::invalid("Nothing to add."));
    }
    kiln_in_game(install)?;
    // where the mods go is decided before anything is imported, so a refusal changes nothing
    let here = active_here(dirs, install)?;
    let make_default = matches!(here, Ok(None)) && profile::list(dirs)?.is_empty();
    if make_default && running {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first: the new profile is activated in the game folder."));
    }
    let mut report = AddReport { running, ..AddReport::default() };
    let mut files = Vec::new();
    let mut skipped = Vec::new();
    for p in paths {
        if !is_modpack(p) {
            files.push(p.clone());
        } else if p.is_dir() {
            skipped.push(Skipped { path: p.display().to_string(), reason: "a modpack folder: drop its zip to install it".into() });
        } else {
            report.modpacks.push(p.display().to_string());
        }
    }
    report.import = if files.is_empty() { ImportReport::default() } else { library::import(dirs, &files, progress)? };
    report.import.skipped.extend(skipped);
    let mut refs: Vec<LibraryRef> = Vec::new();
    for r in report.import.imported.iter().chain(&report.import.existing) {
        if !refs.contains(r) {
            refs.push(r.clone());
        }
    }
    if refs.is_empty() {
        return Ok(report);
    }
    let target = match here {
        Ok(Some(p)) => Target { profile: p, created: false, activation: None },
        Ok(None) if make_default => {
            progress.report(None, &format!("making the profile {DEFAULT_PROFILE}"));
            create_default(dirs, install)?
        }
        // profiles exist but none is active here (or another data folder's is): only the library takes them
        _ => return Ok(report),
    };
    report.profile_id = Some(target.profile.id.clone());
    report.profile_name = Some(target.profile.name.clone());
    report.created = target.created;
    report.activation = target.activation.clone();
    report.applied = target.created;
    let mut p = target.profile.clone();
    let (changes, already) = merge(&mut p.mods, &refs);
    report.already = already.iter().map(|r| named(dirs, r)).collect();
    if changes.is_empty() {
        return Ok(report);
    }
    progress.report(None, &format!("adding to {}", p.name));
    let saved = profile::save(dirs, &p)?;
    if !running {
        progress.report(None, "writing the game's profile.json");
        match profile::refresh_active(dirs, install) {
            Ok(Some(a)) => {
                report.activation = Some(a);
                report.applied = true;
            }
            Ok(None) => {}
            Err(e) => {
                if target.created {
                    let _ = profile::deactivate(dirs, install);
                    let _ = std::fs::remove_dir_all(dirs.profile(&saved.id));
                } else {
                    let _ = revert(dirs, &saved.id, &changes);
                    let _ = profile::refresh_active(dirs, install);
                }
                return Err(Error { message: format!("the mod was not added: {}", e.message), ..e });
            }
        }
    }
    report.missing = missing_for(dirs, &saved, &changes);
    report.added = changes
        .iter()
        .map(|c| named(dirs, &LibraryRef { kind: c.kind, id: c.after.id.clone(), version: c.after.version.clone(), hash: c.after.hash.clone() }))
        .collect();
    report.changes = changes;
    let n = report.added.len();
    progress.report(Some(1.0), &format!("{n} added to {}{}", saved.name, if report.running { " (applies at the next start)" } else { "" }));
    Ok(report)
}

/// Undoes an add: each changed entry goes back to what it was (removed when it was new), newest change first. An entry
/// changed since (another version now) is left as it is. Saves the profile when anything went back; the caller writes
/// the game's profile.json again (as after any profile save).
pub fn revert(dirs: &DataDirs, profile_id: &str, changes: &[ProfileChange]) -> Result<Profile> {
    let mut p = profile::get(dirs, profile_id)?;
    let mut changed = false;
    for c in changes.iter().rev() {
        let list = mods_of(&mut p.mods, c.kind);
        let Some(i) = list.iter().position(|m| m.id == c.after.id) else { continue };
        if list[i].version != c.after.version || list[i].hash != c.after.hash {
            continue;
        }
        match &c.before {
            Some(b) => list[i] = b.clone(),
            None => {
                list.remove(i);
            }
        }
        changed = true;
    }
    if !changed {
        return Ok(p);
    }
    profile::save(dirs, &p)
}
