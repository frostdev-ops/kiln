//! World backups of a server, two kinds:
//! - `kilnctl`: `<backup_dir>/<id>-savegame-<UTC stamp>.tar.gz` (the savegame folder, then `kiln/data`), kilnctl's
//!   format; made with the server stopped or running (tar retries when the game writes meanwhile).
//! - `kiln`: Kiln.Saves backups in `<server>/kiln/backups/<stamp>-<name>/backup.json`, consistent (made right after
//!   a save); created through the admin op `backup_create` while the server runs.
//!
//! Restore with the server stopped: the current savegame (and kiln/data/worlds) move aside to
//! `<backup_dir>/before-restore-<stamp>/` first. While it runs: a kiln backup is staged for the next start
//! (Kiln.Saves `backup_restore`); a tarball is refused.

use super::instance::ServerInstance;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::Duration;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerBackup {
    pub name: String,
    pub created: String,
    pub bytes: u64,
    /// "kilnctl" (tar.gz) | "kiln" (Kiln.Saves)
    pub source: String,
}

fn backup_dir(instance: &ServerInstance) -> Result<PathBuf> {
    instance.backups().ok_or_else(|| Error::new(ErrorCode::Unsupported, "this server has no backup folder"))
}

fn dir_size(p: &Path) -> u64 {
    std::fs::read_dir(p)
        .into_iter()
        .flatten()
        .flatten()
        .map(|e| match e.file_type() {
            Ok(t) if t.is_dir() => dir_size(&e.path()),
            Ok(_) => e.metadata().map(|m| m.len()).unwrap_or(0),
            Err(_) => 0,
        })
        .sum()
}

pub fn list(instance: &ServerInstance) -> Result<Vec<ServerBackup>> {
    let server = instance.local_dir()?;
    let mut out = Vec::new();
    if let Ok(b) = backup_dir(instance) {
        for e in std::fs::read_dir(&b).into_iter().flatten().flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            if !name.ends_with(".tar.gz") {
                continue;
            }
            let md = e.metadata()?;
            let created = md.modified().map(crate::util::fmt_utc).unwrap_or_default();
            out.push(ServerBackup { name, created, bytes: md.len(), source: "kilnctl".into() });
        }
    }
    for e in std::fs::read_dir(server.join("kiln/backups")).into_iter().flatten().flatten() {
        let p = e.path();
        let Ok(Some(meta)) = crate::util::read_json::<serde_json::Value>(&p.join("backup.json")) else { continue };
        let created = meta.get("created").and_then(|c| c.as_str()).map(String::from).unwrap_or_default();
        out.push(ServerBackup { name: e.file_name().to_string_lossy().into(), created, bytes: dir_size(&p), source: "kiln".into() });
    }
    out.sort_by(|a, b| b.created.cmp(&a.created));
    Ok(out)
}

fn admin(instance: &ServerInstance) -> Option<crate::admin::client::AdminClient> {
    super::run::pid(instance)?;
    crate::admin::client::AdminClient::connect_dir(instance.dir.as_deref()?, &instance.id, crate::admin::client::Transport::Auto).ok()
}

/// Running with Kiln.Admin: a Kiln.Saves backup (consistent, right after a save). Otherwise a tarball.
pub fn create(instance: &ServerInstance, progress: &mut dyn Progress) -> Result<ServerBackup> {
    if let Some(c) = admin(instance).filter(|c| c.session().ops.iter().any(|o| o == "backup_create")) {
        let name = format!("launcher-{}", crate::util::ts_compact(crate::util::now_unix()));
        progress.report(None, &format!("Kiln.Saves backup {name} (after a fresh save)"));
        let mut a = serde_json::Map::new();
        a.insert("name".into(), serde_json::Value::String(name));
        let r = c.call("backup_create", a)?;
        c.shutdown();
        let folder = r.get("folder").and_then(|f| f.as_str()).unwrap_or_default().to_string();
        return list(instance)?.into_iter().find(|b| b.name == folder).ok_or_else(|| Error::new(ErrorCode::Internal, format!("backup {folder} not found after creating it")));
    }
    tarball(instance, progress)
}

fn tarball(instance: &ServerInstance, progress: &mut dyn Progress) -> Result<ServerBackup> {
    let server = instance.local_dir()?;
    let save = super::run::save_dir(instance)?;
    if !save.is_dir() {
        return Err(Error::not_found(format!("no savegame folder yet ({})", save.display())));
    }
    let b = backup_dir(instance)?;
    std::fs::create_dir_all(&b).map_err(crate::util::io_at(&b, "creating"))?;
    let stamp = crate::util::stamp(std::time::SystemTime::now());
    let mut file = b.join(format!("{}-savegame-{stamp}.tar.gz", instance.id));
    let mut n = 1;
    while file.exists() {
        file = b.join(format!("{}-savegame-{stamp}-{n}.tar.gz", instance.id));
        n += 1;
    }
    let parent = save.parent().ok_or_else(|| Error::invalid("savegame folder has no parent"))?;
    let base = save.file_name().ok_or_else(|| Error::invalid("savegame folder name"))?;
    let mut args: Vec<std::ffi::OsString> = vec!["-czf".into(), file.clone().into(), "--exclude=*.tmp-*".into(), "-C".into(), parent.into(), base.into()];
    if server.join("kiln/data").is_dir() {
        args.extend(["-C".into(), server.into(), Path::new("kiln").join("data").into()]);
    }
    progress.report(None, &format!("tar -czf {}", file.display()));
    for attempt in 1..=3 {
        let st = Command::new("tar").args(&args).status().map_err(|e| Error::new(ErrorCode::Io, format!("running tar: {e}")))?;
        match st.code() {
            Some(0) => {
                let md = std::fs::metadata(&file)?;
                progress.report(Some(1.0), &format!("backup {} ({})", file.display(), crate::util::human_size(md.len())));
                return Ok(ServerBackup { name: file.file_name().unwrap().to_string_lossy().into(), created: crate::util::now_rfc3339(), bytes: md.len(), source: "kilnctl".into() });
            }
            Some(1) if attempt < 3 => std::thread::sleep(Duration::from_secs(2)),  // a file changed while read: again
            _ => {
                let _ = std::fs::remove_file(&file);
                return Err(Error::new(ErrorCode::Io, format!("tar failed ({st})")));
            }
        }
    }
    let _ = std::fs::remove_file(&file);
    Err(Error::new(ErrorCode::Io, "the save kept changing during the backup"))
}

fn safe_name(name: &str) -> Result<()> {
    if name.is_empty() || name.contains('/') || name.contains('\\') || name.starts_with('.') || name.contains("..") {
        return Err(Error::invalid(format!("{name:?} is not a backup name")));
    }
    Ok(())
}

/// Server stopped: restores at once; running: stages it for the next start (Kiln.Saves backup_restore).
pub fn restore(instance: &ServerInstance, name: &str, progress: &mut dyn Progress) -> Result<()> {
    safe_name(name)?;
    let server = instance.local_dir()?.to_path_buf();
    let backups = list(instance)?;
    let b = backups.iter().find(|b| b.name == name).ok_or_else(|| Error::not_found(format!("no backup {name}")))?;
    if super::run::pid(instance).is_some() {
        if b.source != "kiln" {
            return Err(Error::new(ErrorCode::Conflict, "stop the server to restore a tarball backup"));
        }
        let c = admin(instance).ok_or_else(|| Error::new(ErrorCode::AdminUnavailable, "the running server has no admin connection"))?;
        let meta: serde_json::Value = crate::util::read_json(&server.join("kiln/backups").join(name).join("backup.json"))?.unwrap_or_default();
        let mut a = serde_json::Map::new();
        a.insert("name".into(), meta.get("name").cloned().unwrap_or_else(|| serde_json::Value::String(name.into())));
        c.call("backup_restore", a)?;
        progress.report(Some(1.0), "staged: restart the server to restore it");
        return Ok(());
    }
    let save = super::run::save_dir(instance)?;
    let aside = backup_dir(instance)?.join(format!("before-restore-{}", crate::util::stamp(std::time::SystemTime::now())));
    std::fs::create_dir_all(&aside).map_err(crate::util::io_at(&aside, "creating"))?;
    progress.report(Some(0.1), &format!("moving the current save to {}", aside.display()));
    if save.exists() {
        crate::util::rename_replace(&save, &aside.join("savegame"))?;
    }
    let worlds = server.join("kiln/data/worlds");
    if worlds.exists() {
        std::fs::create_dir_all(aside.join("kiln-data"))?;
        crate::util::rename_replace(&worlds, &aside.join("kiln-data/worlds"))?;
    }
    if b.source == "kilnctl" {
        let file = backup_dir(instance)?.join(name);
        let parent = save.parent().ok_or_else(|| Error::invalid("savegame folder has no parent"))?;
        // the tarball holds <savegame folder name>/ then kiln/data/: extract both where they belong
        let st = Command::new("tar").arg("-xzf").arg(&file).arg("-C").arg(parent).arg("--exclude=kiln").status()?;
        if !st.success() {
            return Err(Error::new(ErrorCode::Io, format!("tar -x failed ({st}); the previous save is in {}", aside.display())));
        }
        let _ = Command::new("tar").arg("-xzf").arg(&file).arg("-C").arg(&server).arg("kiln/data").status();
    } else {
        let src = server.join("kiln/backups").join(name);
        crate::util::copy_tree(&src.join("savegame"), &save)?;
        if src.join("worlds").is_dir() {
            crate::util::copy_tree(&src.join("worlds"), &worlds)?;
        }
    }
    progress.report(Some(1.0), &format!("restored {name}; the previous save is in {}", aside.display()));
    Ok(())
}
