//! The simple flow: drop a mod on the launcher and it is in the game at the next start. One call imports the files
//! into the library, adds what they hold to the game's active profile (enabled), and writes the game's
//! `kiln/profile.json` again. A first-time player has no profile yet: [`ensure_default`] makes "My mods" (every
//! drop-in folder on, so what loaded before still loads) and activates it. Players who already have profiles keep
//! them. With profiles but none active in the game the drop goes to the profile the launcher remembers for that game
//! (its activation record outlives an uninstall of Kiln), activated again; without one, or when `profile.json` names a
//! profile of another data folder, the files are only imported and the result says so in plain words
//! ([`AddReport::library_only`]); [`add_to_profile`] then puts them into the profile the player picks. Modpacks are
//! never added here: they go through the consent flow (`modpack::install`), so [`add`] lists them instead.
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

/// Bounds shared by dropped files and the Deck browser, before the library's recursive detection/extraction.
pub const MAX_IMPORT_BYTES: u64 = 1024 * 1024 * 1024;
pub const MAX_IMPORT_FILE: u64 = 512 * 1024 * 1024;
pub const MAX_IMPORT_ENTRIES: usize = 10_000;
pub const MAX_IMPORT_DEPTH: usize = 32;

pub(crate) fn import_is_link(meta: &std::fs::Metadata) -> bool {
    #[cfg(windows)] {
        use std::os::windows::fs::MetadataExt;
        // Junctions and other reparse points must not bypass the symlink rule.
        if meta.file_attributes() & 0x400 != 0 { return true; }
    }
    meta.file_type().is_symlink()
}

/// Reject links (including cycles), special files and lossy path names before library detection can follow them.
/// Nested archives are opaque mod assets; the library does not recursively extract them.
pub fn validate_import_path(path: &Path) -> Result<()> {
    let mut pending = vec![(path.to_path_buf(), 0)];
    let (mut entries, mut bytes) = (0, 0u64);
    while let Some((p, depth)) = pending.pop() {
        entries += 1;
        if entries > MAX_IMPORT_ENTRIES || depth > MAX_IMPORT_DEPTH {
            return Err(Error::new(ErrorCode::Refused, "Too many files or folders in this mod (limit: 10000 entries, 32 levels)."));
        }
        if p.to_str().is_none() {
            return Err(Error::invalid("Mod paths must be valid UTF-8; rename this file or folder first."));
        }
        let m = std::fs::symlink_metadata(&p).map_err(crate::util::io_at(&p, "reading"))?;
        if import_is_link(&m) || (!m.is_file() && !m.is_dir()) {
            return Err(Error::new(ErrorCode::Refused, format!("{}: links and special files are not allowed in a mod", p.display())));
        }
        if m.is_dir() {
            for e in std::fs::read_dir(&p).map_err(crate::util::io_at(&p, "reading"))? {
                let e = e?;
                pending.push((e.path(), depth + 1));
                if pending.len() + entries > MAX_IMPORT_ENTRIES {
                    return Err(Error::new(ErrorCode::Refused, "Too many files in this mod (limit: 10000 entries)."));
                }
            }
        } else {
            bytes = bytes.saturating_add(m.len());
            if m.len() > MAX_IMPORT_FILE || bytes > MAX_IMPORT_BYTES {
                return Err(Error::new(ErrorCode::Refused, "Mod is too large (limit: 512 MiB per file, 1 GiB total)."));
            }
        }
    }
    #[cfg(feature = "zip")]
    if path.is_file() && path.extension().is_some_and(|s| s.eq_ignore_ascii_case("zip")) {
        validate_zip(path)?;
    }
    // a 7z: its header (entry count, names, links, declared sizes); a rar is checked after a tool unpacked it
    if path.is_file() && path.extension().is_some_and(|s| s.eq_ignore_ascii_case("7z")) {
        crate::archive::validate_sevenz(path)?;
    }
    Ok(())
}

#[cfg(feature = "zip")]
fn validate_zip(path: &Path) -> Result<()> {
    use std::io::{Read, Seek, SeekFrom};
    let mut file = std::fs::File::open(path)?;
    // Bound the central directory before ZipArchive allocates one entry per member. These import limits never
    // need ZIP64 (under 4 GiB and 65535 entries); reject it rather than trusting an overriding ZIP64 record.
    let len = file.metadata()?.len();
    let tail_len = len.min(65557) as usize;
    file.seek(SeekFrom::End(-(tail_len as i64)))?;
    let mut tail = vec![0; tail_len];
    file.read_exact(&mut tail)?;
    let footer = (0..tail.len().saturating_sub(21)).rev().find(|&i| {
        tail[i..].starts_with(b"PK\x05\x06") && i + 22 + u16::from_le_bytes([tail[i + 20], tail[i + 21]]) as usize == tail.len()
    }).ok_or_else(|| Error::new(ErrorCode::Parse, "Missing zip directory footer."))?;
    let count = u16::from_le_bytes([tail[footer + 10], tail[footer + 11]]) as usize;
    let zip64 = footer >= 20 && tail[footer - 20..].starts_with(b"PK\x06\x07");
    if zip64 || count > MAX_IMPORT_ENTRIES {
        return Err(Error::new(ErrorCode::Refused, "ZIP64 or too many files in this zip (limit: 10000 entries)."));
    }
    file.rewind()?;
    let mut a = zip::ZipArchive::new(file)
        .map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", path.display())))?;
    if a.len() > MAX_IMPORT_ENTRIES {
        return Err(Error::new(ErrorCode::Refused, "Too many files in this zip (limit: 10000 entries)."));
    }
    let mut total = 0u64;
    for i in 0..a.len() {
        let mut e = a.by_index(i).map_err(|e| Error::new(ErrorCode::Parse, e.to_string()))?;
        let name = e.name().map_err(|e| Error::new(ErrorCode::Parse, e.to_string()))?.replace('\\', "/");
        let rel = Path::new(crate::bundle::safe_rel_path(name.trim_end_matches('/'))?);
        if e.is_symlink() || rel.components().count() > MAX_IMPORT_DEPTH {
            return Err(Error::new(ErrorCode::Refused, "Links or deeply nested paths in this zip are not allowed."));
        }
        if rel.file_name().and_then(|n| n.to_str()).is_some_and(|n| n.eq_ignore_ascii_case(MODPACK_MANIFEST)) && rel.components().count() != 1 {
            return Err(Error::new(ErrorCode::ConsentRequired, "A wrapped modpack needs its own consent flow; use a zip with kiln-modpack.json at its root."));
        }
        if e.size() > MAX_IMPORT_FILE || total.saturating_add(e.size()) > MAX_IMPORT_BYTES {
            return Err(Error::new(ErrorCode::Refused, "Expanded zip is too large (limit: 512 MiB per file, 1 GiB total)."));
        }
        // Check actual decompressed bytes as well as the untrusted sizes, without allocating the entry.
        let n = std::io::copy(&mut (&mut e).take(MAX_IMPORT_FILE.min(MAX_IMPORT_BYTES - total) + 1), &mut std::io::sink())?;
        if n != e.size() || n > MAX_IMPORT_FILE || total.saturating_add(n) > MAX_IMPORT_BYTES {
            return Err(Error::new(ErrorCode::Refused, "Expanded zip exceeds its declared size or the import limits."));
        }
        total += n;
    }
    Ok(())
}

/// Serialize quick operations across threads and launcher processes. Keep the lock file: unlinking a held lock
/// would let another process lock a different inode. Profile/game writes themselves use atomic replacement.
fn lock(dirs: &DataDirs) -> Result<std::fs::File> {
    std::fs::create_dir_all(&dirs.root)?;
    let f = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(dirs.root.join("quick.lock"))?;
    f.lock()?;
    Ok(f)
}

fn has_profiles(dirs: &DataDirs) -> Result<bool> {
    match std::fs::read_dir(dirs.profiles()) {
        Ok(mut rd) => rd.next().transpose().map(|e| e.is_some()).map_err(Error::from),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(false),
        Err(e) => Err(e.into()),
    }
}

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
    /// The game had no active profile (Kiln was uninstalled and installed again): `profile_id` was active here before
    /// and is active again, so the added mods load at the next start.
    pub resumed: bool,
    /// The mods reached the Library only, and why, in words for the player. None when they went into a profile.
    pub library_only: Option<String>,
    /// What the add did in the profile besides adding, in words for the player: another mod's entry replaced (Kiln
    /// takes them for one mod), a second copy of the same DLL switched off, an XHL-Core build kept.
    #[serde(default)]
    pub notices: Vec<String>,
}

/// The profile [`add`] puts mods into.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Target {
    pub profile: Profile,
    /// Made now ("My mods").
    pub created: bool,
    /// The activation that made it active (only when created or resumed).
    pub activation: Option<Activation>,
    /// The profile the game had before Kiln was reinstalled, activated again.
    pub resumed: bool,
}

fn mods_of(m: &mut ProfileMods, k: ModKind) -> &mut Vec<ProfileMod> {
    m.list_mut(k)
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
        Some(f) => match profile::get(dirs, &f.id) {
            Ok(p) => Ok(Ok(Some(p))),
            Err(e) if e.code == ErrorCode::NotFound => Ok(Err(())),
            Err(e) => Err(e),
        },
    }
}

/// Makes "My mods" (every drop-in folder on) and activates it. Cleanup failures are reported; an in-use backing
/// profile is kept when the game started during activation.
fn create_default(dirs: &DataDirs, install: &GameInstall) -> Result<Target> {
    let made = profile::create(dirs, DEFAULT_PROFILE, None)?;
    let id = made.id.clone();
    let undo = |e: Error| cleanup_created(dirs, install, &id, e);
    let mut p = made;
    // what loaded before (the legacy layout loads every drop-in folder) keeps loading
    p.dropins = ModKind::ALL.into_iter().filter(|k| *k != ModKind::Xhl).collect();
    let p = profile::save(dirs, &p).map_err(undo)?;
    let a = profile::activate(dirs, &p.id, install).map_err(undo)?;
    Ok(Target { profile: p, created: true, activation: Some(a), resumed: false })
}

fn rollback_error(mut original: Error, rollback: Error) -> Error {
    original.message = format!("{}; rollback failed: {}", original.message, rollback.message);
    original
}

fn cleanup_created(dirs: &DataDirs, install: &GameInstall, id: &str, error: Error) -> Error {
    let cleanup = || -> Result<()> {
        if profile::read_game_profile(&install.path)?.is_some_and(|f| f.id == id) {
            // If the game started, keep its backing profile rather than deleting data it is using.
            profile::deactivate(dirs, install)?;
        }
        std::fs::remove_dir_all(dirs.profile(id))?;
        Ok(())
    };
    match cleanup() { Ok(()) => error, Err(e) => rollback_error(error, e) }
}

/// The profile the launcher remembers for this game while none is active in it: `<data>/installs/<id>.active.json`
/// names the profile that was activated last. Uninstalling Kiln leaves that record (and the profile) alone while
/// `kiln/profile.json` goes with the game's Kiln files, so after a reinstall it is what the player was playing. A
/// deliberate "No profile" (`profile::deactivate`) removes the record: nothing is remembered then. Only a profile that
/// still exists counts.
fn remembered(dirs: &DataDirs, install: &GameInstall) -> Option<Profile> {
    let a: Activation = crate::util::read_json(&dirs.installs().join(format!("{}.active.json", install.id))).ok().flatten()?;
    (a.game_id == install.id).then(|| profile::get(dirs, &a.profile_id).ok()).flatten()
}

/// Where a drop goes in a game.
enum Dest {
    /// The game's active profile.
    Active(Profile),
    /// No profile in this data folder at all: "My mods" is made and activated.
    Make,
    /// Profiles exist and none is active in the game, but this one was before: activated again.
    Resume(Profile),
    /// Only the Library takes it. Why, for the player.
    Library(String),
}

fn destination(dirs: &DataDirs, install: &GameInstall) -> Result<Dest> {
    Ok(match active_here(dirs, install)? {
        Ok(Some(p)) => Dest::Active(p),
        Err(()) => Dest::Library("This game runs a profile from another Kiln data folder, so the mod is only in your Library. \
            Add it to a profile of this launcher to play with it.".into()),
        Ok(None) if !has_profiles(dirs)? => Dest::Make,
        Ok(None) => match remembered(dirs, install) {
            Some(p) => Dest::Resume(p),
            None => Dest::Library("No profile is active in this game, so the mod is only in your Library. \
                Add it to a profile to play with it.".into()),
        },
    })
}

fn needs_activation(dest: &Dest) -> bool {
    matches!(dest, Dest::Make | Dest::Resume(_))
}

fn running_error(dest: &Dest) -> Error {
    let what = match dest {
        Dest::Resume(p) => format!("Kiln turns {} back on in the game folder", p.name),
        _ => "the new profile is activated in the game folder".to_string(),
    };
    Error::new(ErrorCode::GameRunning, format!("Close the game first: {what}."))
}

/// Makes the profile active again (the game's profile.json is gone). Failing, the caller falls back to the Library.
fn resume(dirs: &DataDirs, install: &GameInstall, p: Profile) -> std::result::Result<Target, (Profile, Error)> {
    match profile::activate(dirs, &p.id, install) {
        Ok(a) => Ok(Target { profile: p, created: false, activation: Some(a), resumed: true }),
        Err(e) => Err((p, e)),
    }
}

/// The profile the simple flow uses in a game: the active one; else, when this data folder has no profile at all, a
/// new "My mods", activated; else the profile the launcher remembers for the game (see [`remembered`]), activated
/// again. None when profiles exist but none is active here and none is remembered, or the game's profile.json names a
/// profile of another data folder: those players chose that, and are left alone.
pub fn ensure_default(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Target>> {
    let running = crate::proc::find_game(install)?.is_some();
    ensure_default_with(dirs, install, running)
}

/// [`ensure_default`] with the game's running state given (tests).
pub fn ensure_default_with(dirs: &DataDirs, install: &GameInstall, running: bool) -> Result<Option<Target>> {
    kiln_in_game(install)?;
    let _lock = lock(dirs)?;
    let dest = destination(dirs, install)?;
    if needs_activation(&dest) && running {
        return Err(running_error(&dest));
    }
    match dest {
        Dest::Active(p) => Ok(Some(Target { profile: p, created: false, activation: None, resumed: false })),
        Dest::Library(_) => Ok(None),
        Dest::Make => create_default(dirs, install).map(Some),
        Dest::Resume(p) => resume(dirs, install, p).map(Some).or_else(|(_, e)| if e.code == ErrorCode::GameRunning { Err(e) } else { Ok(None) }),
    }
}

/// After Kiln is installed into a game (a reinstall after an uninstall): the profile the launcher remembers for the game
/// is made the active one again, so the player's mods load without a step. Ok(None): nothing to do (a profile is
/// active, none is remembered, or `profile.json` names a profile of another data folder). Refused (GameRunning) while
/// the game runs, like any activation. An active profile is written again when it renders differently for the runtime
/// the game has now (an update or a rollback: a path form an older core refuses, profile::render), so a start from
/// Steam does not boot with a file the installed core calls invalid; that still returns None.
pub fn resume_active(dirs: &DataDirs, install: &GameInstall) -> Result<Option<Activation>> {
    kiln_in_game(install)?;
    let _lock = lock(dirs)?;
    match destination(dirs, install)? {
        Dest::Resume(p) => profile::activate(dirs, &p.id, install).map(Some),
        Dest::Active(_) => profile::refresh_active(dirs, install).map(|_| None),
        _ => Ok(None),
    }
}

/// A modpack: a folder or zip with kiln-modpack.json at its root.
pub fn is_modpack(path: &Path) -> bool {
    if path.is_dir() {
        return path.join(MODPACK_MANIFEST).is_file();
    }
    path.to_string_lossy().to_lowercase().ends_with(".zip") && zip_has(path, MODPACK_MANIFEST)
}

fn contains_modpack(path: &Path) -> Result<bool> {
    let mut pending = vec![path.to_path_buf()];
    while let Some(dir) = pending.pop() {
        if dir.join(MODPACK_MANIFEST).is_file() {
            return Ok(true);
        }
        for e in std::fs::read_dir(&dir)? {
            let e = e?;
            if e.file_type()?.is_dir() { pending.push(e.path()); }
        }
    }
    Ok(false)
}

#[cfg(feature = "zip")]
fn zip_has(zip: &Path, name: &str) -> bool {
    let Ok(f) = std::fs::File::open(zip) else { return false };
    let Ok(a) = zip::ZipArchive::new(f) else { return false };
    // Windows resolves manifest names without regard to case, unlike a zip's by_name lookup.
    a.file_names().filter_map(|n| n.ok()).any(|n| n.replace('\\', "/").eq_ignore_ascii_case(name))
}

#[cfg(not(feature = "zip"))]
fn zip_has(_zip: &Path, _name: &str) -> bool {
    false
}

/// Puts `refs` into `mods` (enabled; another version of the same mod is replaced). Returns the changes, the refs that
/// were in already as they are, and what the player is told besides ([`AddReport::notices`]):
/// - an entry replaced by one with another name: Kiln takes the two for one mod, which may not be what they are;
/// - a Kiln-made native that brings the same proxy DLL as another enabled Kiln-made native (the same bytes, or another
///   copy of the same package, as one dev.23 named after its version): only one of them stays on, the one with the
///   package's files, else the one added now. A native with its author's own native.json is never switched off;
/// - XHL-Core that came with another mod's package does not replace the enabled XHL-Core with an older build, or with
///   a build of unknown age; XHL-Core dropped on its own does (the player's choice).
pub(crate) fn merge(dirs: &DataDirs, mods: &mut ProfileMods, refs: &[LibraryRef]) -> (Vec<ProfileChange>, Vec<LibraryRef>, Vec<String>) {
    let (mut changes, mut already, mut notices) = (Vec::new(), Vec::new(), Vec::new());
    let name = |r: &LibraryRef| library::record(dirs, r).ok().and_then(|x| x.name).filter(|n| !n.trim().is_empty()).unwrap_or_else(|| r.id.clone());
    let lref = |kind: ModKind, m: &ProfileMod| LibraryRef { kind, id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone() };
    for r in refs {
        let after = ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true };
        let list = mods_of(mods, r.kind);
        match list.iter().position(|m| m.id == r.id) {
            Some(i) if list[i] == after => { already.push(r.clone()); continue; }
            Some(i) => {
                let old_ref = lref(r.kind, &list[i]);
                if r.kind == ModKind::Native && generated_proxy(dirs, r).is_some() && generated_proxy(dirs, &old_ref).is_none() {
                    notices.push(format!("{} stays as selected: its author supplied native.json. {} has the same inferred id ({}), so it is only in your Library.",
                        name(&old_ref), name(r), r.id));
                    continue;
                }
                if r.kind == ModKind::Native && list[i].enabled
                    && generated_proxy(dirs, r).zip(generated_proxy(dirs, &old_ref))
                        .is_some_and(|(new, old)| new.lone && !old.lone && new.sha == old.sha && new.file.eq_ignore_ascii_case(&old.file))
                {
                    notices.push(format!("{} stays enabled with the files of its package: {} is the same DLL alone, now in your Library.", name(&old_ref), name(r)));
                    continue;
                }
                if let Some(why) = keep_xhl_core(r, &list[i], refs, &name) {
                    notices.push(why);
                    continue;
                }
                let (old, new) = (name(&lref(r.kind, &list[i])), name(r));
                if library::package_stem(&old) != library::package_stem(&new) {
                    notices.push(format!("{new} took the place of {old}: Kiln reads the two as one mod ({} {}). Undo puts {old} back.", r.kind.as_str(), r.id));
                }
                let before = std::mem::replace(&mut list[i], after.clone());
                changes.push(ProfileChange { kind: r.kind, before: Some(before), after });
            }
            None => {
                list.push(after.clone());
                changes.push(ProfileChange { kind: r.kind, before: None, after });
            }
        }
        // one copy of a proxy DLL runs
        if r.kind != ModKind::Native { continue; }
        let Some(new) = generated_proxy(dirs, r) else { continue };
        let others: Vec<ProfileMod> = mods.native.iter().filter(|m| m.enabled && m.id != r.id).cloned().collect();
        for other in others {
            let o = lref(ModKind::Native, &other);
            let Some(old) = generated_proxy(dirs, &o) else { continue };
            let (new_name, old_name) = (name(r), name(&o));
            let same_bytes = old.sha == new.sha;
            if !old.file.eq_ignore_ascii_case(&new.file)
                || !(same_bytes || library::package_stem(&old_name) == library::package_stem(&new_name)) { continue; }
            if new.lone && !old.lone {
                // the DLL alone, next to its package with the files it reads: the package's copy stays on
                if let Some(m) = mods.native.iter_mut().find(|m| m.id == r.id) { m.enabled = false; }
                if let Some(c) = changes.iter_mut().rev().find(|c| c.kind == ModKind::Native && c.after.id == r.id) { c.after.enabled = false; }
                notices.push(format!("{new_name} is in the profile switched off: {old_name} already runs the same {}, with the files of its package.", new.file));
                break;
            }
            if let Some(m) = mods.native.iter_mut().find(|m| m.id == other.id) { m.enabled = false; }
            changes.push(ProfileChange { kind: ModKind::Native, before: Some(other.clone()), after: ProfileMod { enabled: false, ..other } });
            notices.push(if same_bytes {
                format!("{old_name} is switched off: {new_name} brings the same {}, and both copies would load.", new.file)
            } else {
                format!("{old_name} is switched off: {new_name} is another copy of the same mod with its own {}, and both would load.", new.file)
            });
        }
    }
    (changes, already, notices)
}

/// A native mod Kiln made from a DLL under a proxy name (its generated native.json): that DLL's file name and sha256,
/// and whether the folder is the DLL alone.
struct GeneratedProxy {
    file: String,
    sha: String,
    lone: bool,
}

fn generated_proxy(dirs: &DataDirs, r: &LibraryRef) -> Option<GeneratedProxy> {
    let folder = library::path_of(dirs, r).ok()?;
    let m: serde_json::Value = crate::util::read_json(&folder.join(crate::manifest::NATIVE_MANIFEST)).ok()??;
    if !m.get("description")?.as_str()?.starts_with("Generated by Kiln from ") { return None; }
    let primary = m.get("dlls")?.get(0)?.as_str()?;
    let file = Path::new(primary).file_name()?.to_str()?.to_string();
    if !crate::engines::is_proxy_name(&file) { return None; }
    let sha = crate::hash::sha256_file(&folder.join(primary)).ok()?.0;
    let lone = std::fs::read_dir(&folder).ok()?.count() <= 2;
    Some(GeneratedProxy { file, sha, lone })
}

/// Why the enabled XHL-Core stays instead of `r` (another build of it), or None to replace it: with both versions known,
/// only an older build stays out; with one unknown, a build that came with other mods in the same drop (an XHL mod's
/// archive bundling it) stays out, and XHL-Core dropped on its own is taken.
fn keep_xhl_core(r: &LibraryRef, have: &ProfileMod, refs: &[LibraryRef], name: &dyn Fn(&LibraryRef) -> String) -> Option<String> {
    if r.kind != ModKind::Native || r.id != library::XHL_CORE_ID || !have.enabled { return None; }
    let came_with = refs.iter().find(|o| *o != r).map(|o| name(o));
    let parse = |v: &str| crate::semver::SemVersion::parse(v).ok().filter(|_| v != "0.0.0");
    match (parse(&have.version), parse(&r.version)) {
        (Some(a), Some(b)) if b.compare(&a).is_lt() => Some(format!("XHL-Core {} stays in the profile: {} brought XHL-Core {}, an older build; it is in your Library.",
            have.version, came_with.unwrap_or_else(|| "this download".into()), r.version)),
        (Some(_), Some(_)) => None,
        _ => came_with.map(|with| format!("The XHL-Core you have stays in the profile: {with} brought another build of XHL-Core{}, now in your \
            Library. Switch to it on the profile's Native mods if you want that one.", if r.version != "0.0.0" { format!(" ({})", r.version) } else { String::new() })),
    }
}

fn named(dirs: &DataDirs, r: &LibraryRef) -> Result<AddedMod> {
    let name = library::record(dirs, r)?.name.filter(|n| !n.trim().is_empty()).unwrap_or_else(|| r.id.clone());
    Ok(AddedMod { kind: r.kind, id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), name })
}

/// What the added Kiln and native mods require that `profile` lacks (enabled, compatible version).
fn missing_for(dirs: &DataDirs, profile: &Profile, added: &[ProfileChange]) -> Result<Vec<MissingMod>> {
    let has = |id: &str, range: &str| {
        profile.mods.kiln.iter().chain(&profile.mods.native).any(|m| {
            m.enabled && m.id == id && crate::semver::VersionRange::parse(range)
                .and_then(|r| Ok(r.satisfies(&crate::semver::SemVersion::parse(&m.version)?))).unwrap_or(false)
        })
    };
    let mut out: Vec<MissingMod> = Vec::new();
    let mut need = |id: &str, range: &str, by: &str| {
        if id != crate::manifest::KILN_ID && !has(id, range) && !out.iter().any(|m| m.id == id && m.range == range && m.by == by) {
            out.push(MissingMod { id: id.into(), range: range.into(), by: by.into() });
        }
    };
    for c in added {
        let r = LibraryRef { kind: c.kind, id: c.after.id.clone(), version: c.after.version.clone(), hash: c.after.hash.clone() };
        let folder = library::path_of(dirs, &r)?;
        match c.kind {
            ModKind::Kiln => {
                let m = crate::manifest::read_kiln(&folder)?;
                for d in m.dependencies.iter().filter(|d| !d.optional) {
                    need(&d.id, &d.range, &m.id);
                }
            }
            ModKind::Native => {
                let m = crate::manifest::read_native(&folder)?;
                for id in &m.requires {
                    need(id, "*", &m.id);
                }
            }
            _ => {}
        }
    }
    Ok(out)
}

/// Imports `paths` and adds what they hold to the game's active profile (making "My mods" for a first-time player),
/// then writes the game's profile.json again. Refused before anything changes when Kiln is not in the game, or when
/// "My mods" would have to be activated while the game runs. When writing profile.json fails, the profile change is
/// undone (and a profile made for it removed) before the error returns; rollback failures are included in the
/// error, and the library keeps what it imported.
pub fn add(dirs: &DataDirs, install: &GameInstall, paths: &[PathBuf], progress: &mut dyn Progress) -> Result<AddReport> {
    add_impl(dirs, install, paths, &|| Ok(crate::proc::find_game(install)?.is_some()), progress)
}

/// [`add`] with the game's running state given (tests).
pub fn add_with(dirs: &DataDirs, install: &GameInstall, paths: &[PathBuf], running: bool, progress: &mut dyn Progress) -> Result<AddReport> {
    add_impl(dirs, install, paths, &|| Ok(running), progress)
}

fn add_impl(dirs: &DataDirs, install: &GameInstall, paths: &[PathBuf], is_running: &dyn Fn() -> Result<bool>, progress: &mut dyn Progress) -> Result<AddReport> {
    if paths.is_empty() {
        return Err(Error::invalid("Nothing to add."));
    }
    kiln_in_game(install)?;
    let _lock = lock(dirs)?;
    // where the mods go is decided before anything is imported, so a refusal changes nothing
    let dest = destination(dirs, install)?;
    let running = is_running()?;
    if needs_activation(&dest) && running {
        return Err(running_error(&dest));
    }
    let mut report = AddReport { running, ..AddReport::default() };
    let mut files = Vec::new();
    let mut skipped = Vec::new();
    for p in paths {
        validate_import_path(p)?;
        if p.is_dir() && !is_modpack(p) && contains_modpack(p)? {
            return Err(Error::new(ErrorCode::ConsentRequired, "This folder contains a modpack; install its zip through the consent flow."));
        }
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
        if let Some(i) = refs.iter().position(|old| old.kind == r.kind && old.id == r.id) {
            // One version per mod, chosen consistently even when import divides a repeated drop into its
            // imported/existing lists in a different order. A repeated multi-version drop is a no-op.
            if crate::bundle::compare_versions(&r.version, &refs[i].version).then_with(|| r.hash.cmp(&refs[i].hash)).is_gt() {
                refs[i] = r.clone();
            }
        } else {
            refs.push(r.clone());
        }
    }
    if progress.canceled() { return Err(Error::new(ErrorCode::Canceled, "canceled")); }
    if refs.is_empty() {
        return Ok(report);
    }
    // Imports may take seconds. Re-read both the active profile and process state before any game write.
    let dest = destination(dirs, install)?;
    let running = is_running()?;
    report.running = running;
    if needs_activation(&dest) && running {
        return Err(running_error(&dest));
    }
    let target = match dest {
        Dest::Active(p) => Target { profile: p, created: false, activation: None, resumed: false },
        Dest::Make => {
            progress.report(None, &format!("making the profile {DEFAULT_PROFILE}"));
            create_default(dirs, install)?
        }
        Dest::Resume(p) => {
            progress.report(None, &format!("turning {} back on", p.name));
            match resume(dirs, install, p) {
                Ok(t) => t,
                Err((p, e)) => {
                    report.library_only = Some(format!("Kiln could not turn {} back on ({}), so the mod is only in your Library.", p.name, e.message));
                    return Ok(report);
                }
            }
        }
        // profiles exist but none is active here (or another data folder's is): only the library takes them
        Dest::Library(why) => {
            report.library_only = Some(why);
            return Ok(report);
        }
    };
    report.profile_id = Some(target.profile.id.clone());
    report.profile_name = Some(target.profile.name.clone());
    report.created = target.created;
    report.resumed = target.resumed;
    report.activation = target.activation.clone();
    report.applied = target.created || target.resumed;
    put_in_profile(dirs, install, &target, &refs, running, progress, &mut report)?;
    Ok(report)
}

/// Merges `refs` into the target profile, saves it and, when the game is not running, writes the game's profile.json
/// again; fills the report (`already`, `added`, `changes`, `missing`). The profile change is undone (and a profile made
/// for it removed) when the game's profile.json cannot be written.
fn put_in_profile(dirs: &DataDirs, install: &GameInstall, target: &Target, refs: &[LibraryRef], running: bool, progress: &mut dyn Progress,
                  report: &mut AddReport) -> Result<()> {
    let mut p = target.profile.clone();
    let (changes, already, notices) = merge(dirs, &mut p.mods, refs);
    report.already = already.iter().map(|r| named(dirs, r)).collect::<Result<_>>()?;
    report.notices = notices;
    if changes.is_empty() {
        return Ok(());
    }
    // Finish fallible report work before saving the profile. Corrupt library records must not become a success
    // with an empty dependency list, or an error after the mods have already been applied.
    let details = || -> Result<(Vec<MissingMod>, Vec<AddedMod>)> {
        let missing = missing_for(dirs, &p, &changes)?;
        // an entry switched off (a second copy of one DLL) is a change, not a mod that now runs
        let added = changes.iter().filter(|c| c.after.enabled).map(|c| named(dirs, &LibraryRef {
            kind: c.kind, id: c.after.id.clone(), version: c.after.version.clone(), hash: c.after.hash.clone()
        })).collect::<Result<_>>()?;
        Ok((missing, added))
    };
    let (missing, added) = details().map_err(|e| if target.created { cleanup_created(dirs, install, &p.id, e) } else { e })?;
    progress.report(None, &format!("adding to {}", p.name));
    let saved = profile::save(dirs, &p).map_err(|e| if target.created { cleanup_created(dirs, install, &p.id, e) } else { e })?;
    if !running {
        progress.report(None, "writing the game's profile.json");
        match profile::refresh_active(dirs, install) {
            Ok(Some(a)) => {
                // another active profile that had changed is written again too; only the target's write counts here
                report.applied = a.profile_id == saved.id;
                report.activation = Some(a);
            }
            Ok(None) => {}
            Err(mut e) => {
                if target.created {
                    e = cleanup_created(dirs, install, &saved.id, e);
                } else {
                    if let Err(rollback) = revert_unlocked(dirs, &saved.id, &changes) {
                        e = rollback_error(e, rollback);
                    } else if let Err(rollback) = profile::refresh_active(dirs, install) {
                        e = rollback_error(e, rollback);
                    }
                }
                return Err(Error { message: format!("could not apply the added mods: {}", e.message), ..e });
            }
        }
    }
    report.missing = missing;
    report.added = added;
    report.changes = changes;
    let n = report.added.len();
    progress.report(Some(1.0), &format!("{n} added to {}{}", saved.name, if report.running { " (applies at the next start)" } else { "" }));
    Ok(())
}

/// Puts library versions into the profile the player picked, for a drop that only reached the Library
/// ([`AddReport::library_only`]). The report is [`add`]'s with an empty `import`; the game's profile.json is written
/// again when that profile is the game's active one and the game is not running. A profile that is not active stays
/// as it is: the result says which profile took the mods, and the player turns it on.
pub fn add_to_profile(dirs: &DataDirs, install: &GameInstall, profile_id: &str, refs: &[LibraryRef], progress: &mut dyn Progress) -> Result<AddReport> {
    let running = crate::proc::find_game(install)?.is_some();
    add_to_profile_with(dirs, install, profile_id, refs, running, progress)
}

/// [`add_to_profile`] with the game's running state given (tests).
pub fn add_to_profile_with(dirs: &DataDirs, install: &GameInstall, profile_id: &str, refs: &[LibraryRef], running: bool,
                           progress: &mut dyn Progress) -> Result<AddReport> {
    if refs.is_empty() {
        return Err(Error::invalid("Nothing to add."));
    }
    let _lock = lock(dirs)?;
    for r in refs {
        library::record(dirs, r)?; // each must be in the library
    }
    let profile = profile::get(dirs, profile_id)?;
    let mut report = AddReport { running, ..AddReport::default() };
    report.profile_id = Some(profile.id.clone());
    report.profile_name = Some(profile.name.clone());
    let target = Target { profile, created: false, activation: None, resumed: false };
    put_in_profile(dirs, install, &target, refs, running, progress, &mut report)?;
    Ok(report)
}

/// Undoes an add: each changed entry goes back to what it was (removed when it was new), newest change first. An entry
/// changed since (another version now) is left as it is. Saves the profile when anything went back; the caller writes
/// the game's profile.json again (as after any profile save).
pub fn revert(dirs: &DataDirs, profile_id: &str, changes: &[ProfileChange]) -> Result<Profile> {
    let _lock = lock(dirs)?;
    revert_unlocked(dirs, profile_id, changes)
}

fn revert_unlocked(dirs: &DataDirs, profile_id: &str, changes: &[ProfileChange]) -> Result<Profile> {
    let mut p = profile::get(dirs, profile_id)?;
    let mut changed = false;
    for c in changes.iter().rev() {
        let list = mods_of(&mut p.mods, c.kind);
        let Some(i) = list.iter().position(|m| m.id == c.after.id) else { continue };
        if list[i] != c.after {
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
