//! Server-side mod management on disk: list `kiln/mods` (and `kiln/native`), sync library mods in (each with a
//! `.kiln-source.json` marker), switch them with `.disabled`. Folders without the marker (put there by hand, by
//! deploy-server.sh or another tool) are never replaced or deleted. A running server is told through the admin ops
//! (mod_unload before a removal, mod_reload after a change, mod_load for a new folder).

use super::instance::ServerInstance;
use super::{ServerMod, SourceMarker};
use crate::library::LibraryRef;
use crate::manifest::{Clients, ModKind};
use crate::paths::DataDirs;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub const MARKER: &str = ".kiln-source.json";
pub const DISABLED: &str = ".disabled";

// Ownership is recorded outside mod folders: a downloaded package can forge its own marker.
const ENGINE_RECEIPT: &str = "kiln/state/server-library.json";
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
struct EngineCopy {
    library: LibraryRef,
    payload: String,
    /// The synced files (relative path to hash, as [`payload_files`]): the copy stays the launcher's while these are
    /// unchanged, whatever the mod adds next to them (its settings). Receipts written before this have none: their
    /// copy must match `payload` exactly.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    files: Option<BTreeMap<String, String>>,
}
type EngineCopies = BTreeMap<PathBuf, EngineCopy>;

fn engine_copies(server: &Path) -> Result<EngineCopies> {
    let file = server.join(ENGINE_RECEIPT);
    crate::engine_mods::refuse_symlink_path(server, &file)?;
    Ok(crate::util::read_json(&file)?.unwrap_or_default())
}

fn save_engine_copies(server: &Path, copies: &EngineCopies) -> Result<()> {
    let file = server.join(ENGINE_RECEIPT);
    crate::engine_mods::refuse_symlink_path(server, &file)?;
    let parent = file.parent().expect("receipt parent");
    std::fs::create_dir_all(parent)?;
    let staging = crate::library::TempDir::new(parent, "server-receipt")?;
    let tmp = staging.path().join("receipt.json");
    crate::util::write_json_atomic(&tmp, copies)?;
    std::fs::rename(&tmp, &file).map_err(crate::util::io_at(&file, "saving receipt"))
}

fn server_library_lock(server: &Path) -> Result<std::fs::File> {
    let path = server.join("kiln/state/server-library.lock");
    crate::engine_mods::refuse_symlink_path(server, &path)?;
    std::fs::create_dir_all(path.parent().expect("lock parent"))?;
    let file = std::fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(path)?;
    file.lock()?;
    Ok(file)
}

// Include hidden files and empty directories too. SF's mutable setting values and enabled switch are omitted.
fn payload(dir: &Path, kind: ModKind) -> Result<String> {
    Ok(crate::hash::sha256_hex(&serde_json::to_vec(&payload_files(dir, kind)?)?))
}

fn payload_files(dir: &Path, kind: ModKind) -> Result<BTreeMap<String, String>> {
    fn walk(root: &Path, dir: &Path, rel: &str, kind: ModKind, out: &mut BTreeMap<String, String>) -> Result<()> {
        crate::engine_mods::refuse_symlink_path(root, dir)?;
        for e in std::fs::read_dir(dir)? {
            let e = e?;
            let name = e.file_name().into_string().map_err(|_| Error::invalid("non-Unicode mod path"))?;
            if rel.is_empty() && name == MARKER { continue; }
            let path = e.path();
            crate::engine_mods::refuse_symlink_path(root, &path)?;
            let key = if rel.is_empty() { name.clone() } else { format!("{rel}/{name}") };
            let ft = e.file_type()?;
            if ft.is_dir() {
                out.insert(format!("{key}/"), String::new());
                walk(root, &path, &key, kind, out)?;
            } else if ft.is_file() {
                let hash = if kind == ModKind::ShroudForge && rel.is_empty() && name == "extended.mod.json" {
                    let mut v = crate::jsonc::parse(&crate::manifest::read_text(&path)?)?;
                    if let Some(o) = v.as_object_mut() {
                        o.remove("enabled");
                        if let Some(settings) = o.get_mut("settings").and_then(|s| s.as_object_mut()) {
                            for value in settings.values_mut() {
                                if let Some(o) = value.as_object_mut().filter(|o| o.contains_key("value")) { o.remove("value"); }
                                else { *value = serde_json::Value::Null; }
                            }
                        }
                    }
                    crate::hash::sha256_hex(&serde_json::to_vec(&v)?)
                } else { crate::hash::sha256_file(&path)?.0 };
                out.insert(key, hash);
            } else { return Err(Error::invalid(format!("{} is not a regular mod file", path.display()))); }
        }
        Ok(())
    }
    let mut files = BTreeMap::new();
    walk(dir, dir, "", kind, &mut files)?;
    Ok(files)
}

/// `server` is the managed root `dir` lies under.
fn owned_engine(server: &Path, dir: &Path, kind: ModKind, copies: &EngineCopies) -> Result<bool> {
    Ok(engine_player_files(server, dir, kind, copies, None)?.is_some())
}

/// A copy the launcher synced (as [`owned_engine`]), with the files the mod wrote into it since: what it added and its
/// changed settings files (engine_mods::settings_files). None: not the launcher's copy, or a synced file changed.
/// A receipt from before the file list is read against its Library entry when `dirs` is given and that entry still
/// matches the receipt's payload.
fn engine_player_files(server: &Path, dir: &Path, kind: ModKind, copies: &EngineCopies, dirs: Option<&DataDirs>)
                       -> Result<Option<Vec<String>>> {
    crate::engine_mods::refuse_symlink_path(server, dir)?;
    let Some(saved) = copies.get(dir) else { return Ok(None) };
    let Some(marker) = read_marker(dir) else { return Ok(None) };
    if !(marker.format == 1 && marker.library.kind == kind && marker.library == saved.library
        && dir.file_name().and_then(|n| n.to_str()) == Some(marker.library.id.as_str())) {
        return Ok(None);
    }
    let legacy = || -> Result<Option<BTreeMap<String, String>>> {
        let Some(dirs) = dirs else { return Ok(None) };
        let Ok(lib) = crate::library::path_of(dirs, &saved.library) else { return Ok(None) };
        if !lib.is_dir() { return Ok(None); }
        let files = payload_files(&lib, kind)?;
        Ok((crate::hash::sha256_hex(&serde_json::to_vec(&files)?) == saved.payload).then_some(files))
    };
    let files = match &saved.files {
        Some(files) => Some(files.clone()),
        None => legacy()?,
    };
    Ok(match files {
        Some(files) => crate::engine_mods::player_entries_of(payload_files(dir, kind)?, &files, kind),
        None => (payload(dir, kind)? == saved.payload).then(Vec::new),
    })
}

fn refuse_running_engine(running: bool) -> Result<()> {
    if running { Err(Error::new(ErrorCode::GameRunning, "stop the server before changing engine mods")) } else { Ok(()) }
}

fn engine_running_with(mut find: impl FnMut(&str) -> Result<bool>) -> Result<bool> {
    for exe in ["enshrouded.exe", "enshrouded_server.exe"] {
        if find(exe)? { return Ok(true); }
    }
    Ok(false)
}

fn engine_running(server: &Path) -> Result<bool> {
    engine_running_with(|exe| crate::proc::find_exe(server, exe).map(|p| p.is_some()))
}

fn root(server: &Path, kind: ModKind) -> Result<PathBuf> {
    match kind {
        ModKind::Kiln => Ok(server.join("kiln/mods")),
        ModKind::Native => Ok(server.join("kiln/native")),
        ModKind::Xhl => Err(Error::new(ErrorCode::Unsupported,
            "XHL companions require original-folder receipt placement: import them into the Library and activate a profile for this server; install and enable XHL-Core separately. This legacy Mods-page direct sync cannot place them safely")),
        // other engines' mods go where that engine's plugin reads them on a server (engine_mods::server_root)
        k => crate::engine_mods::server_root(k, server)
            .ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{} mods are not synced to servers", kind.as_str()))),
    }
}

// The loader lives beside several engines' mods. Neither a forged marker nor a manually constructed
// library reference grants permission to move, replace or remove it (including Win32 trailing-dot aliases).
fn loader_folder(server: &Path, path: &Path) -> bool {
    path.parent() == Some(server.join("mods").as_path())
        && path.file_name().and_then(|n| n.to_str()).is_some_and(|n|
            n.trim_end_matches(['.', ' ']).eq_ignore_ascii_case(crate::engine_mods::ENSHWICK_LOADER_FOLDER))
}

/// The kinds a server takes: Kiln's managed and native mods, then every engine kind with a server folder.
fn server_kinds() -> impl Iterator<Item = ModKind> {
    // External-host companions are placed by profile activation, not this legacy plugin-root sync path.
    ModKind::ALL.into_iter().filter(|k| matches!(k, ModKind::Kiln | ModKind::Native) || (k.is_engine() && k.engine().is_some()))
}

/// The folders of `kind` in `dir` (a root several engines may share: each folder is checked for the kind).
fn kind_dirs(server: &Path, dir: &Path, kind: ModKind) -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Vec::new() };
    let mut dirs: Vec<PathBuf> = rd
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_dir() && !p.file_name().and_then(|n| n.to_str()).unwrap_or(".").starts_with('.'))
        .filter(|p| !loader_folder(server, p))
        .filter(|p| !kind.is_engine() || read_marker(p).is_some_and(|m| m.library.kind == kind) || crate::engine_mods::folder_is(kind, p))
        .collect();
    dirs.sort();
    dirs
}

fn read_marker(dir: &Path) -> Option<SourceMarker> {
    crate::util::read_json(&dir.join(MARKER)).ok().flatten()
}

fn folder_ok(f: &str) -> bool {
    !f.is_empty() && f.len() <= 80 && !f.starts_with('.') && f.bytes().all(|b| b.is_ascii_alphanumeric() || b"._-".contains(&b))
}

/// A folder a player placed by hand in an engine's folder may have any plain Windows name (EML's `Hoarders Helper`,
/// `antisocialian's tweaks`): one path component, no hidden, reserved or trailing-dot names.
fn hand_placed_ok(f: &str) -> bool {
    !f.is_empty() && f.len() <= 255 && !f.starts_with('.') && !f.ends_with(['.', ' '])
        && !f.chars().any(|c| c.is_control() || "/\\:*?\"<>|".contains(c))
}

/// Every mod folder of the server (managed and native), with its marker and, while it runs, its state.
pub fn mods(instance: &ServerInstance) -> Result<Vec<ServerMod>> {
    let server = instance.local_dir()?;
    let states = crate::state::read_mods(server).ok().flatten().map(|m| m.mods).unwrap_or_default();
    let copies = engine_copies(server)?;
    let mut out = Vec::new();
    for kind in server_kinds() {
        let r = root(server, kind)?;
        if kind.is_engine() {
            // engine mods: on in the engine's folder, off in kiln/disabled/<kind>/
            let off = crate::engine_mods::server_disabled_root(kind, server);
            for (d, enabled) in kind_dirs(server, &r, kind).into_iter().map(|d| (d, true)).chain(kind_dirs(server, &off, kind).into_iter().map(|d| (d, false))) {
                let folder = d.file_name().and_then(|n| n.to_str()).unwrap_or("").to_string();
                let (id, version, _) = crate::engine_mods::identity(&d);
                out.push(ServerMod {
                    id,
                    version,
                    enabled,
                    source: if owned_engine(server, &d, kind, &copies)? { read_marker(&d).map(|m| m.library) } else { None },
                    clients: Some(crate::engine_mods::clients_of(kind, &d).1),
                    state: None,
                    folder,
                    kind: Some(kind),
                });
            }
            continue;
        }
        for d in kind_dirs(server, &r, kind) {
            let folder = d.file_name().and_then(|n| n.to_str()).unwrap_or("").to_string();
            let (id, version, clients) = match kind {
                ModKind::Kiln => match crate::manifest::read_kiln(&d) {
                    Ok(m) => (m.id, Some(m.version), Some(m.clients)),
                    Err(_) => (folder.clone(), None, None),
                },
                _ => match crate::manifest::read_native(&d) {
                    Ok(m) => (m.id, Some(m.version), Some(m.clients.unwrap_or_else(|| Clients::default_for(m.side)))),
                    Err(_) => (folder.clone(), None, None),
                },
            };
            let state = if kind == ModKind::Kiln { states.iter().find(|s| s.id == id).map(|s| s.state) } else { None };
            out.push(ServerMod {
                id,
                version,
                enabled: !d.join(DISABLED).exists(),
                source: read_marker(&d).map(|m| m.library),
                clients,
                state,
                folder,
                kind: Some(kind),
            });
        }
    }
    Ok(out)
}

fn admin(instance: &ServerInstance) -> Option<crate::admin::client::AdminClient> {
    if super::run::pid(instance).is_none() {
        return None;
    }
    crate::admin::client::AdminClient::connect_dir(instance.dir.as_deref()?, &instance.id, crate::admin::client::Transport::Auto).ok()
}

fn op(c: &Option<crate::admin::client::AdminClient>, name: &str, key: &str, value: &str, progress: &mut dyn Progress) {
    if let Some(c) = c {
        let mut a = serde_json::Map::new();
        a.insert(key.into(), serde_json::Value::String(value.into()));
        match c.call(name, a) {
            Ok(_) => progress.report(None, &format!("{name} {value}: ok")),
            Err(e) => progress.report(None, &format!("{name} {value}: {}", e.message)),
        }
    }
}

/// Makes `kiln/mods` + `kiln/native` hold exactly these library mods among the launcher-synced ones (folders with a
/// marker); hand-placed folders stay. A folder of the same id without a marker is a conflict (left alone, reported).
pub fn sync(dirs: &DataDirs, instance: &ServerInstance, mods: &[LibraryRef], progress: &mut dyn Progress) -> Result<Vec<ServerMod>> {
    // Kiln entries an older Kiln recorded with their cook outputs in the hash move to their identity first; a caller
    // that still names one by its old hash means the entry it became
    let moved = crate::library::migrate_identities(dirs)?;
    let renamed: Vec<LibraryRef> = mods.iter().map(|r| match moved.iter().find(|m| r.kind == ModKind::Kiln && m.id == r.id && m.version == r.version && m.from == r.hash) {
        Some(m) => LibraryRef { hash: m.to.clone(), ..r.clone() },
        None => r.clone(),
    }).collect();
    let mods = renamed.as_slice();
    let server = instance.local_dir()?.to_path_buf();
    let _lock = server_library_lock(&server)?;
    let mut copies = engine_copies(&server)?;
    // Engine plugins read files directly and have no safe managed hot-reload protocol.
    if mods.iter().any(|r| r.kind.is_engine()) || !copies.is_empty() {
        refuse_running_engine(super::run::pid(instance).is_some() || engine_running(&server)?)?;
    }
    for r in mods {
        if !folder_ok(&r.id) { return Err(Error::invalid(format!("mod id {:?} cannot be a folder name", r.id))); }
        crate::library::path_of(dirs, r)?;
        if loader_folder(&server, &root(&server, r.kind)?.join(&r.id)) {
            return Err(Error::new(ErrorCode::Refused, "mods/enshwick is the loader's own folder"));
        }
    }
    let live = admin(instance);
    let mut conflicts = Vec::new();
    // 1. removals: synced folders no longer wanted (engine kinds: also their switched-off copies)
    for kind in server_kinds() {
        let r = root(&server, kind)?;
        let mut places = vec![r];
        if kind.is_engine() {
            places.push(crate::engine_mods::server_disabled_root(kind, &server));
        }
        for place in &places { crate::engine_mods::refuse_symlink_path(&server, place)?; }
        for e in places.iter().filter_map(|p| std::fs::read_dir(p).ok()).flat_map(|rd| rd.flatten()) {
            let d = e.path();
            if loader_folder(&server, &d) { continue; }
            let Some(m) = read_marker(&d) else { continue };
            if m.library.kind != kind {
                continue; // a root several engines share: another kind's synced folder
            }
            if mods.iter().any(|x| x.kind == kind && x.id == m.library.id) {
                continue;
            }
            let player = if kind.is_engine() {
                let Some(player) = engine_player_files(&server, &d, kind, &copies, Some(dirs))? else { continue };
                player
            } else { Vec::new() };
            if let Some(said) = crate::engine_mods::keep_player_files(&server, &d, kind, &m.library.id, &player)? {
                progress.report(None, &said);
            }
            if kind == ModKind::Kiln {
                op(&live, "mod_unload", "id", &m.library.id, progress);
            }
            progress.report(None, &format!("removing {} (synced {}, not in the list)", d.display(), m.library.version));
            if kind.is_engine() {
                let parked = d.with_file_name(format!(".kiln-remove-{}", crate::hash::hex(&crate::util::random_bytes::<6>()?)));
                std::fs::rename(&d, &parked).map_err(crate::util::io_at(&d, "removing"))?;
                copies.remove(&d);
                if let Err(error) = save_engine_copies(&server, &copies) {
                    let _ = std::fs::rename(&parked, &d);
                    return Err(error);
                }
                std::fs::remove_dir_all(&parked).map_err(crate::util::io_at(&parked, "deleting"))?;
            } else { std::fs::remove_dir_all(&d).map_err(crate::util::io_at(&d, "deleting"))?; }
        }
    }
    // 2. installs and updates
    for (i, r) in mods.iter().enumerate() {
        if progress.canceled() {
            return Err(Error::new(ErrorCode::Canceled, "canceled"));
        }
        progress.report(Some(i as f32 / mods.len().max(1) as f32), &format!("{} {}", r.id, r.version));
        let mut dst_root = root(&server, r.kind)?;
        if r.kind.is_engine() {
            // a switched-off engine mod stays off: its new version goes to kiln/disabled/<kind>/
            let off = crate::engine_mods::server_disabled_root(r.kind, &server);
            let off_target = off.join(&r.id);
            crate::engine_mods::refuse_symlink_path(&server, &off_target)?;
            if off_target.exists() && !owned_engine(&server, &off_target, r.kind, &copies)? {
                conflicts.push(format!("{}: {} has player changes and was left disabled", r.id, off_target.display()));
                continue;
            }
            if off_target.exists() {
                dst_root = off;
            }
        }
        crate::engine_mods::refuse_symlink_path(&server, &dst_root)?;
        std::fs::create_dir_all(&dst_root).map_err(crate::util::io_at(&dst_root, "creating"))?;
        if !folder_ok(&r.id) {
            return Err(Error::invalid(format!("mod id {:?} cannot be a folder name", r.id)));
        }
        let target = dst_root.join(&r.id);
        crate::engine_mods::refuse_symlink_path(&server, &target)?;
        let marker = read_marker(&target);
        let player = if r.kind.is_engine() && target.exists() { engine_player_files(&server, &target, r.kind, &copies, Some(dirs))? } else { None };
        if target.exists() && (marker.as_ref().is_none_or(|m| m.library.kind != r.kind || m.library.id != r.id)
            || (r.kind.is_engine() && player.is_none())) {
            conflicts.push(format!("{}: {} exists and was not put there by the launcher", r.id, target.display()));
            continue;
        }
        if marker.as_ref().is_some_and(|m| m.library == *r) {
            continue;  // unchanged
        }
        let src = crate::library::path_of(dirs, r)?;
        crate::engine_mods::refuse_symlink_path(&dirs.root, &src)?;
        if crate::hash::of_mod(r.kind, &src)? != r.hash {
            return Err(Error::new(ErrorCode::HashMismatch, format!("library entry {} {} changed on disk", r.id, r.version)));
        }
        let staging = crate::library::TempDir::new(&dst_root, "kiln-sync")?;
        let tmp = staging.path().join("new");
        crate::library::copy_mod_tree(&src, &tmp)?;
        if crate::hash::of_mod(r.kind, &tmp)? != r.hash {
            let _ = std::fs::remove_dir_all(&tmp);
            return Err(Error::new(ErrorCode::HashMismatch, "library mod changed while copying"));
        }
        if r.kind == ModKind::ShroudForge {
            if target.exists() {
                // settings the old copy cannot show are not thrown away: that copy stays, the others go on
                let old = match crate::shroudforge::read_mod(&target) {
                    Ok(m) => m,
                    Err(e) => {
                        conflicts.push(format!("{}: {} was left as it is: its ShroudForge settings cannot be read ({})", r.id, target.display(), e.message));
                        continue;
                    }
                };
                for setting in old.settings {
                    // Removed settings are allowed; a value the new version refuses keeps its default (as activation does)
                    let Some(default) = crate::shroudforge::read_mod(&tmp)?.settings.into_iter().find(|s| s.key == setting.key).map(|s| s.value) else { continue };
                    match crate::shroudforge::set_setting(&tmp, &setting.key, &setting.value) {
                        Ok(_) => {}
                        Err(e) if e.code == ErrorCode::InvalidArgument => progress.report(None, &format!(
                            "{}: the setting {} was {}, which version {} does not accept ({}); it starts with that version's default {default}",
                            r.id, setting.key, setting.value, r.version, e.message)),
                        Err(e) => return Err(e),
                    }
                }
            }
            let file = tmp.join("extended.mod.json");
            let text = crate::manifest::read_text(&file)?;
            let body = crate::jsonc::set_value(&text, &crate::jsonc::split_path("enabled"), &serde_json::json!(true))?;
            crate::util::write_atomic(&file, body.as_bytes())?;
        }
        let was_disabled = target.join(DISABLED).exists();
        if was_disabled {
            std::fs::write(tmp.join(DISABLED), b"")?;
        }
        let m = SourceMarker { format: 1, library: r.clone(), synced: crate::util::now_rfc3339(), by: format!("kiln-launcher {}", crate::VERSION) };
        crate::util::write_json_atomic(&tmp.join(MARKER), &m)?;
        let signature = if r.kind.is_engine() { Some(payload_files(&tmp, r.kind)?) } else { None };
        // what the mod wrote into its old copy (or what was kept when it was removed) goes into the new one
        let kept_at = crate::engine_mods::kept_dir(&server, r.kind, &r.id);
        let carry = match player {
            Some(entries) => Some(crate::engine_mods::Carry { from: &target, entries, from_kept: false }),
            None if r.kind.is_engine() && !target.exists() => crate::engine_mods::kept_carry(&server, &kept_at, r.kind)?,
            None => None,
        };
        let done = match &carry {
            Some(c) => Some(crate::engine_mods::carry_in(&server, r.kind, &r.id, c, &tmp)?),
            None => None,
        };
        let existed = target.exists();
        let old = staging.path().join("old");
        if existed { std::fs::rename(&target, &old).map_err(crate::util::io_at(&target, "parking"))?; }
        if let Err(error) = std::fs::rename(&tmp, &target).map_err(crate::util::io_at(&target, "placing")) {
            if existed && let Err(e) = std::fs::rename(&old, &target) {
                return Err(crate::engine_mods::preserve_recovery(staging, crate::util::io_at(&target, "restoring old mod")(e)));
            }
            return Err(error);
        }
        if r.kind.is_engine() {
            let files = signature.expect("engine payload checked before publication");
            let payload = crate::hash::sha256_hex(&serde_json::to_vec(&files)?);
            copies.insert(target.clone(), EngineCopy { library: r.clone(), payload, files: Some(files) });
            if let Err(error) = save_engine_copies(&server, &copies) {
                if let Err(e) = std::fs::rename(&target, &tmp) {
                    return Err(crate::engine_mods::preserve_recovery(staging, crate::util::io_at(&target, "rolling back new mod")(e)));
                }
                if existed && let Err(e) = std::fs::rename(&old, &target) {
                    return Err(crate::engine_mods::preserve_recovery(staging, crate::util::io_at(&target, "restoring old mod")(e)));
                }
                return Err(error);
            }
        }
        if let (Some(c), Some(done)) = (&carry, &done)
            && let Some(said) = crate::engine_mods::carried(&server, r.kind, &r.id, c, done, &target)? {
            progress.report(None, &said);
        }
        if r.kind == ModKind::Kiln && !was_disabled {
            if existed { op(&live, "mod_reload", "id", &r.id, progress) } else { op(&live, "mod_load", "folder", &r.id, progress) }
        }
    }
    if let Some(c) = live {
        c.shutdown();
    }
    if !conflicts.is_empty() {
        return Err(Error::new(ErrorCode::Conflict, format!("{} mod(s) were left alone: {}", conflicts.len(), conflicts.join("; "))).with_detail(serde_json::json!(conflicts)));
    }
    progress.report(Some(1.0), "synced");
    mods_of(instance)
}

/// An engine mod: moved between the engine's folder and `kiln/disabled/<kind>/` (a folder the launcher synced, or
/// one the engine's folder holds for that kind).
fn engine_switch_candidates(server: &Path, folder: &str, enabled: bool) -> Result<Vec<(PathBuf, PathBuf, ModKind)>> {
    let mut candidates = Vec::new();
    for kind in server_kinds().filter(|k| k.is_engine()) {
        let Ok(on_root) = root(server, kind) else { continue };
        let off_root = crate::engine_mods::server_disabled_root(kind, server);
        let (on, off) = (on_root.join(folder), off_root.join(folder));
        let (from, to) = if enabled { (off, on) } else { (on, off) };
        if loader_folder(server, &from) || loader_folder(server, &to) { continue; }
        crate::engine_mods::refuse_symlink_path(server, &from)?;
        crate::engine_mods::refuse_symlink_path(server, &to)?;
        if from.is_dir() && (read_marker(&from).is_some_and(|m| m.library.kind == kind) || crate::engine_mods::folder_is(kind, &from)) {
            candidates.push((from, to, kind));
        }
    }
    Ok(candidates)
}

fn set_engine_enabled(server: &Path, folder: &str, enabled: bool, running: bool) -> Result<()> {
    refuse_running_engine(running || engine_running(server)?)?;
    let _lock = server_library_lock(server)?;
    let mut copies = engine_copies(server)?;
    let mut candidates = engine_switch_candidates(server, folder, enabled)?;
    if candidates.len() > 1 { return Err(Error::new(ErrorCode::Conflict, format!("more than one engine has a mod folder {folder}"))); }
    let Some((from, to, kind)) = candidates.pop() else { return Err(Error::not_found(format!("no mod folder {folder} to switch"))); };
    if to.exists() { return Err(Error::new(ErrorCode::Conflict, format!("{} exists already", to.display()))); }
    let owned = owned_engine(server, &from, kind, &copies)?;
    if let Some(p) = to.parent() { std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?; }
    std::fs::rename(&from, &to).map_err(crate::util::io_at(&from, "moving"))?;
    if owned {
        let saved = copies.remove(&from).expect("owned entry");
        copies.insert(to.clone(), saved);
        if let Err(error) = save_engine_copies(server, &copies) {
            let _ = std::fs::rename(&to, &from);
            return Err(error);
        }
    }
    Ok(())
}

fn mods_of(instance: &ServerInstance) -> Result<Vec<ServerMod>> {
    mods(instance)
}

/// `.disabled` on (enabled false) or off. Works for any folder under kiln/mods or kiln/native (an empty marker file;
/// other tools toggle it the same way). The server sees it at its next start (or a mod_load/mod_unload).
pub fn set_enabled(instance: &ServerInstance, folder: &str, enabled: bool) -> Result<()> {
    if !folder_ok(folder) && !hand_placed_ok(folder) {
        return Err(Error::invalid(format!("{folder:?} is not a mod folder name")));
    }
    let server = instance.local_dir()?;
    let mut core: Vec<_> = [server.join("kiln/mods").join(folder), server.join("kiln/native").join(folder)].into_iter().filter(|d| d.is_dir()).collect();
    if core.len() > 1 || (!core.is_empty() && !engine_switch_candidates(server, folder, enabled)?.is_empty()) {
        return Err(Error::new(ErrorCode::Conflict, format!("more than one kind has a mod folder {folder}")));
    }
    let Some(dir) = core.pop() else {
        return set_engine_enabled(server, folder, enabled, super::run::pid(instance).is_some());
    };
    let flag = dir.join(DISABLED);
    if enabled {
        if flag.exists() {
            std::fs::remove_file(&flag).map_err(crate::util::io_at(&flag, "deleting"))?;
        }
    } else {
        std::fs::write(&flag, b"").map_err(crate::util::io_at(&flag, "writing"))?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enshwick_loader_is_protected_even_with_a_forged_source_marker_and_receipt() {
        let temp = crate::library::TempDir::new(&std::env::temp_dir(), "enshwick-loader-guard").unwrap();
        let server = temp.path().join("server");
        let dirs = DataDirs::at(temp.path().join("data"));
        let instance: ServerInstance = serde_json::from_value(serde_json::json!({
            "id": "t", "name": "t", "kind": "local", "dir": server,
            "port": 27140, "queryPort": 27140, "wine": null, "policy": "any",
            "source": "launcher", "remote": null
        })).unwrap();
        let mut copies = EngineCopies::new();
        for id in ["enshwick", "ENSHWICK", "enshwick."] {
            let dir = server.join("mods").join(id);
            std::fs::create_dir_all(&dir).unwrap();
            std::fs::write(dir.join("loader.dll"), b"loader: preserve me").unwrap();
            let reference = LibraryRef { kind: ModKind::Enshwick, id: id.into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "a".repeat(64)) };
            crate::util::write_json_atomic(&dir.join(MARKER), &SourceMarker {
                format: 1, library: reference.clone(), synced: "now".into(), by: "forged".into()
            }).unwrap();
            copies.insert(dir.clone(), EngineCopy { library: reference, payload: payload(&dir, ModKind::Enshwick).unwrap(), files: None });
            assert!(engine_switch_candidates(&server, id, false).unwrap().is_empty());
            assert!(!mods(&instance).unwrap().iter().any(|m| m.folder == id));
        }
        save_engine_copies(&server, &copies).unwrap();
        // Even an authentic-looking old receipt must never authorize removal of the loader.
        sync(&dirs, &instance, &[], &mut crate::task::NoProgress).unwrap();
        for id in ["enshwick", "ENSHWICK", "enshwick."] {
            assert_eq!(std::fs::read(server.join("mods").join(id).join("loader.dll")).unwrap(), b"loader: preserve me");
        }
        // Import checks are not a trust boundary: callers can construct library references directly.
        for id in ["enshwick", "enshwick."] {
            let reference = LibraryRef { kind: ModKind::Enshwick, id: id.into(), version: "1.0.0".into(), hash: format!("kdh1:{}", "a".repeat(64)) };
            assert_eq!(sync(&dirs, &instance, &[reference], &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
        }
        // A different engine's isolated root is not the Enshwick loader.
        assert!(!loader_folder(&server, &server.join("ember/mods/enshwick")));
        assert!(!loader_folder(&server, &server.join("kiln/mods/enshwick")));
    }

    #[test]
    fn external_xhl_requires_receipt_based_profile_placement_not_legacy_direct_sync() {
        let err = root(Path::new("server"), ModKind::Xhl).unwrap_err();
        assert_eq!(err.code, ErrorCode::Unsupported);
        assert!(err.message.contains("activate a profile") && err.message.contains("XHL-Core separately"));
        assert!(!server_kinds().any(|k| k == ModKind::Xhl));
    }

    #[test]
    fn engine_disk_changes_require_a_stopped_server() {
        assert_eq!(refuse_running_engine(true).unwrap_err().code, ErrorCode::GameRunning);
        refuse_running_engine(false).unwrap();
    }

    #[test]
    fn engine_disk_changes_also_guard_a_colocated_client() {
        for running_exe in ["enshrouded.exe", "enshrouded_server.exe"] {
            let running = engine_running_with(|exe| Ok(exe == running_exe)).unwrap();
            assert_eq!(refuse_running_engine(running).unwrap_err().code, ErrorCode::GameRunning);
        }
        assert!(engine_running_with(|_| Err(Error::invalid("query failed"))).is_err());
    }
}
