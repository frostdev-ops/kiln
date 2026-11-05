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
        _ => Err(Error::new(ErrorCode::Unsupported, format!("{} mods are not synced to servers", kind.as_str()))),
    }
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
    for kind in [ModKind::Kiln, ModKind::Native] {
        let r = root(server, kind)?;
        let Ok(rd) = std::fs::read_dir(&r) else { continue };
        let mut dirs: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
        dirs.sort();
        for d in dirs {
            let folder = d.file_name().and_then(|n| n.to_str()).unwrap_or("").to_string();
            if folder.starts_with('.') {
                continue;
            }
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
    // 1. removals: synced folders no longer wanted
    for kind in [ModKind::Kiln, ModKind::Native] {
        let r = root(&server, kind)?;
        let Ok(rd) = std::fs::read_dir(&r) else { continue };
        for e in rd.flatten() {
            let d = e.path();
            let Some(m) = read_marker(&d) else { continue };
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
        let dst_root = root(&server, r.kind)?;
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
    let dir = [server.join("kiln/mods").join(folder), server.join("kiln/native").join(folder)]
        .into_iter()
        .find(|d| d.is_dir())
        .ok_or_else(|| Error::not_found(format!("no mod folder {folder}")))?;
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
