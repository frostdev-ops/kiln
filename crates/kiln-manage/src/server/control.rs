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
use std::path::{Path, PathBuf};

pub const MARKER: &str = ".kiln-source.json";
pub const DISABLED: &str = ".disabled";

fn root(server: &Path, kind: ModKind) -> Result<PathBuf> {
    match kind {
        ModKind::Kiln => Ok(server.join("kiln/mods")),
        ModKind::Native => Ok(server.join("kiln/native")),
        // other engines' mods go where that engine's plugin reads them on a server (engine_mods::server_root)
        k => crate::engine_mods::server_root(k, server)
            .ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{} mods are not synced to servers", kind.as_str()))),
    }
}

/// The kinds a server takes: Kiln's managed and native mods, then every engine kind with a server folder.
fn server_kinds() -> impl Iterator<Item = ModKind> {
    ModKind::ALL.into_iter().filter(|k| matches!(k, ModKind::Kiln | ModKind::Native) || (k.is_engine() && !k.is_reserved()))
}

/// The folders of `kind` in `dir` (a root several engines may share: each folder is checked for the kind).
fn kind_dirs(dir: &Path, kind: ModKind) -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(dir) else { return Vec::new() };
    let mut dirs: Vec<PathBuf> = rd
        .flatten()
        .map(|e| e.path())
        .filter(|p| p.is_dir() && !p.file_name().and_then(|n| n.to_str()).unwrap_or(".").starts_with('.'))
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

/// Every mod folder of the server (managed and native), with its marker and, while it runs, its state.
pub fn mods(instance: &ServerInstance) -> Result<Vec<ServerMod>> {
    let server = instance.local_dir()?;
    let states = crate::state::read_mods(server).ok().flatten().map(|m| m.mods).unwrap_or_default();
    let mut out = Vec::new();
    for kind in server_kinds() {
        let r = root(server, kind)?;
        if kind.is_engine() {
            // engine mods: on in the engine's folder, off in kiln/disabled/<kind>/
            let off = crate::engine_mods::server_disabled_root(kind, server);
            for (d, enabled) in kind_dirs(&r, kind).into_iter().map(|d| (d, true)).chain(kind_dirs(&off, kind).into_iter().map(|d| (d, false))) {
                let folder = d.file_name().and_then(|n| n.to_str()).unwrap_or("").to_string();
                let (id, version, _) = crate::engine_mods::identity(&d);
                out.push(ServerMod {
                    id,
                    version,
                    enabled,
                    source: read_marker(&d).map(|m| m.library),
                    clients: Some(crate::engine_mods::clients_of(kind, &d).1),
                    state: None,
                    folder,
                    kind: Some(kind),
                });
            }
            continue;
        }
        for d in kind_dirs(&r, kind) {
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
    let server = instance.local_dir()?.to_path_buf();
    let live = admin(instance);
    let mut conflicts = Vec::new();
    // 1. removals: synced folders no longer wanted (engine kinds: also their switched-off copies)
    for kind in server_kinds() {
        let r = root(&server, kind)?;
        let mut places = vec![r];
        if kind.is_engine() {
            places.push(crate::engine_mods::server_disabled_root(kind, &server));
        }
        for e in places.iter().filter_map(|p| std::fs::read_dir(p).ok()).flat_map(|rd| rd.flatten()) {
            let d = e.path();
            let Some(m) = read_marker(&d) else { continue };
            if m.library.kind != kind {
                continue; // a root several engines share: another kind's synced folder
            }
            if mods.iter().any(|x| x.kind == kind && x.id == m.library.id) {
                continue;
            }
            if kind == ModKind::Kiln {
                op(&live, "mod_unload", "id", &m.library.id, progress);
            }
            progress.report(None, &format!("removing {} (synced {}, not in the list)", d.display(), m.library.version));
            std::fs::remove_dir_all(&d).map_err(crate::util::io_at(&d, "deleting"))?;
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
            if read_marker(&off.join(&r.id)).is_some() {
                dst_root = off;
            }
        }
        std::fs::create_dir_all(&dst_root).map_err(crate::util::io_at(&dst_root, "creating"))?;
        if !folder_ok(&r.id) {
            return Err(Error::invalid(format!("mod id {:?} cannot be a folder name", r.id)));
        }
        let target = dst_root.join(&r.id);
        let marker = read_marker(&target);
        if target.exists() && marker.is_none() {
            conflicts.push(format!("{}: {} exists and was not put there by the launcher", r.id, target.display()));
            continue;
        }
        if marker.as_ref().is_some_and(|m| m.library == *r) {
            continue;  // unchanged
        }
        let src = crate::library::path_of(dirs, r)?;
        if crate::hash::dirhash(&src)? != r.hash {
            return Err(Error::new(ErrorCode::HashMismatch, format!("library entry {} {} changed on disk", r.id, r.version)));
        }
        let tag = crate::hash::hex(&crate::util::random_bytes::<4>()?);
        let tmp = dst_root.join(format!(".{}.kiln-sync-{tag}", r.id));
        crate::util::copy_tree(&src, &tmp)?;
        let was_disabled = target.join(DISABLED).exists();
        if was_disabled {
            std::fs::write(tmp.join(DISABLED), b"")?;
        }
        let m = SourceMarker { format: 1, library: r.clone(), synced: crate::util::now_rfc3339(), by: format!("kiln-launcher {}", crate::VERSION) };
        crate::util::write_json_atomic(&tmp.join(MARKER), &m)?;
        let existed = target.exists();
        if existed {
            let old = dst_root.join(format!(".{}.kiln-old-{tag}", r.id));
            crate::util::rename_replace(&target, &old)?;
            crate::util::rename_replace(&tmp, &target)?;
            let _ = std::fs::remove_dir_all(&old);
        } else {
            crate::util::rename_replace(&tmp, &target)?;
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
fn set_engine_enabled(server: &Path, folder: &str, enabled: bool) -> Result<()> {
    for kind in server_kinds().filter(|k| k.is_engine()) {
        let Ok(on_root) = root(server, kind) else { continue };
        let off_root = crate::engine_mods::server_disabled_root(kind, server);
        let (on, off) = (on_root.join(folder), off_root.join(folder));
        let (from, to) = if enabled { (off, on) } else { (on, off) };
        if !from.is_dir() || !(read_marker(&from).is_some_and(|m| m.library.kind == kind) || crate::engine_mods::folder_is(kind, &from)) {
            continue;
        }
        if to.exists() {
            return Err(Error::new(ErrorCode::Conflict, format!("{} exists already", to.display())));
        }
        if let Some(p) = to.parent() {
            std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?;
        }
        return crate::util::rename_replace(&from, &to);
    }
    let on_off = if enabled { "switched-off" } else { "" };
    Err(Error::not_found(format!("no {on_off} mod folder {folder}")))
}

fn mods_of(instance: &ServerInstance) -> Result<Vec<ServerMod>> {
    mods(instance)
}

/// `.disabled` on (enabled false) or off. Works for any folder under kiln/mods or kiln/native (an empty marker file;
/// other tools toggle it the same way). The server sees it at its next start (or a mod_load/mod_unload).
pub fn set_enabled(instance: &ServerInstance, folder: &str, enabled: bool) -> Result<()> {
    if !folder_ok(folder) {
        return Err(Error::invalid(format!("{folder:?} is not a mod folder name")));
    }
    let server = instance.local_dir()?;
    let Some(dir) = [server.join("kiln/mods").join(folder), server.join("kiln/native").join(folder)].into_iter().find(|d| d.is_dir()) else {
        return set_engine_enabled(server, folder, enabled);
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
