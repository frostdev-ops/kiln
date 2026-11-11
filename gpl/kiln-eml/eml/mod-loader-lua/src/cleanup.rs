// SPDX-License-Identifier: GPL-3.0-or-later
// Write-ahead scratch ownership. Only the installation-lock holder may create or consume these records.
use std::{fs, io, path::{Path, PathBuf}};
use serde::{Deserialize, Serialize};
use crate::{ownership::{bounded, checked_path, check_stem, directory, is_uuid, temp_of, valid_name}, transaction};

#[derive(Serialize, Deserialize)]
struct Record { format: u32, stem: String, name: String }

fn stage(name: &str) -> Option<&str> { name.strip_prefix(".kiln-eml-stage-").filter(|id| is_uuid(id)) }
fn temp(stem: &str, name: &str) -> bool {
    temp_of(name).is_some_and(|base| [stem.to_owned(), format!("{stem}.kfc"), format!("{stem}.kfc_resources"), format!("{stem}.kfc.bak")].iter().any(|s| s == base))
}
fn record_path(root: &Path, name: &str) -> Option<PathBuf> {
    let id = stage(name).or_else(|| temp_of(name).and_then(|_| name.rsplit_once(".kiln-").map(|(_, id)| id)))?;
    Some(root.join("kiln/eml/ownership").join(format!("cleanup-{id}.json")))
}

/// Flush the exact future name before creating a directory or writing the first byte of a game-folder temp.
pub(crate) fn record(root: &Path, stem: &str, name: &str) -> io::Result<PathBuf> {
    check_stem(stem)?;
    if stage(name).is_none() && !temp(stem, name) { return Err(io::Error::other("invalid scratch name")); }
    let target = root.join(name);
    checked_path(root, &target)?;
    match fs::symlink_metadata(&target) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => {},
        Err(e) => return Err(e),
        Ok(_) => return Err(io::Error::other("scratch destination already exists")),
    }
    directory(root)?;
    let path = record_path(root, name).unwrap();
    if path.try_exists()? { return Err(io::Error::other("scratch record already exists")); }
    transaction::atomic_write(&path, &serde_json::to_vec(&Record { format: 1, stem: stem.into(), name: name.into() })?)?;
    Ok(path)
}

/// Stage temps are covered by their folder's record; ownership/cache/SF state temps stay in their own state folders.
pub(crate) fn record_temp(dst: &Path, tmp: &Path) -> io::Result<Option<PathBuf>> {
    let Some(root) = dst.parent() else { return Ok(None) };
    if root.file_name().and_then(|n| n.to_str()).is_some_and(|n| stage(n).is_some()) { return Ok(None); }
    let Some(name) = dst.file_name().and_then(|n| n.to_str()) else { return Ok(None) };
    let stem = [".kfc.bak.integrity", ".kfc_resources.bak", ".kfc.bak", ".kfc_resources", ".kfc"].iter().find_map(|s| name.strip_suffix(s));
    let Some(stem) = stem else { return Ok(None) };
    record(root, stem, tmp.file_name().and_then(|n| n.to_str()).ok_or_else(|| io::Error::other("invalid temp name"))?).map(Some)
}

fn staged(stem: &str, name: &str) -> bool {
    valid_name(stem, name) || name == transaction::VANILLA_HEADER
        || temp(stem, name) || temp_of(name) == Some(transaction::VANILLA_HEADER)
}

fn consume(root: &Path, path: &Path) -> io::Result<()> {
    let r: Record = serde_json::from_slice(&bounded(root, path)?)?;
    check_stem(&r.stem)?;
    if r.format != 1 || record_path(root, &r.name).as_deref() != Some(path)
        || (stage(&r.name).is_none() && !temp(&r.stem, &r.name)) {
        return Err(io::Error::other("unknown scratch record; retained"));
    }
    let target = root.join(&r.name);
    checked_path(root, &target)?;
    match fs::symlink_metadata(&target) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => {},
        Err(e) => return Err(e),
        Ok(m) if stage(&r.name).is_some() && m.is_dir() => {
            for e in fs::read_dir(&target)? {
                let e = e?;
                if staged(&r.stem, &e.file_name().to_string_lossy()) && transaction::regular(&e.path()).is_ok() {
                    fs::remove_file(e.path())?;
                }
            }
            transaction::sync_dir(&target)?;
            if fs::read_dir(&target)?.next().is_none() { fs::remove_dir(&target)?; }
            // Foreign children stay; inventory names them even after this record is dropped.
        },
        Ok(_) if stage(&r.name).is_none() => { transaction::regular(&target)?; fs::remove_file(&target)?; },
        Ok(_) => return Err(io::Error::other("recorded scratch changed type; retained")),
    }
    transaction::sync_dir(root)?;
    fs::remove_file(path)?;
    transaction::sync_dir(path.parent().unwrap())
}

pub(crate) fn finish_stage(root: &Path, name: &str) {
    if let Some(path) = record_path(root, name) {
        if path.exists() && let Err(error) = consume(root, &path) {
            tracing::warn!(%error, "Could not finish recorded EML scratch cleanup; next start will retry");
        }
    }
}

/// Next-start cleanup under the installation lock. Unrecorded game-folder copies are never deletion candidates.
pub fn prune(root: &Path, stem: &str) {
    if check_stem(stem).is_err() { return; }
    let dir = root.join("kiln/eml/ownership");
    if checked_path(root, &dir).is_err() { return; }
    let Ok(entries) = fs::read_dir(&dir) else { return };
    let mut journals = Vec::new();
    for e in entries.flatten() {
        let path = e.path();
        let name = e.file_name().to_string_lossy().into_owned();
        if transaction::regular(&path).is_err() { continue; }
        if name.strip_prefix("cleanup-").and_then(|s| s.strip_suffix(".json")).is_some_and(is_uuid) {
            if let Err(error) = consume(root, &path) { tracing::warn!(%error, path = %path.display(), "EML scratch record retained"); }
        } else if temp_of(&name).is_some_and(|base| ["enshrouded", "enshrouded_server", stem].contains(&base)
            || ["sf-handoff-", "cleanup-"].iter().any(|prefix| base.strip_prefix(prefix).is_some_and(is_uuid))) {
            if let Err(error) = fs::remove_file(&path) { tracing::warn!(%error, "Could not remove EML state temp"); }
        } else if name.strip_prefix("sf-handoff-").and_then(|s| s.strip_suffix(".json")).is_some_and(is_uuid) {
            journals.push((e.metadata().and_then(|m| m.modified()).unwrap_or(std::time::UNIX_EPOCH), path));
        }
    }
    journals.sort_by(|a, b| b.cmp(a));
    for (_, path) in journals.into_iter().skip(16) {
        if let Err(error) = fs::remove_file(&path) { tracing::warn!(%error, "Could not prune old SF handoff journal"); }
    }
}

// Inventory never follows links, and reads no file contents. A size is the sum of regular files, not allocated blocks.
fn inventory(root: &Path, path: &Path, out: &mut Vec<String>, depth: usize) -> io::Result<u64> {
    checked_path(root, path)?;
    let m = fs::symlink_metadata(path)?;
    let name = path.strip_prefix(root).map_err(io::Error::other)?.to_string_lossy().replace('\\', "/");
    if m.is_file() { out.push(format!("{name} ({} bytes)", m.len())); return Ok(m.len()); }
    if !m.is_dir() || depth > 32 { return Err(io::Error::other("recovery path type or depth cannot be inventoried")); }
    let mut bytes = 0;
    for e in fs::read_dir(path)? { bytes += inventory(root, &e?.path(), out, depth + 1)?; }
    out.push(format!("{name}/ ({bytes} bytes total)"));
    Ok(bytes)
}

/// Player-facing state/log guidance. A recorded stage with foreign children becomes an unrecorded retained folder.
pub fn recovery_guidance(root: &Path, stem: &str) -> io::Result<Vec<String>> {
    check_stem(stem)?;
    let mut paths = Vec::new();
    for e in fs::read_dir(root)? {
        let e = e?;
        let name = e.file_name().to_string_lossy().into_owned();
        if stage(&name).is_some() || [stem, "enshrouded", "enshrouded_server"].iter().any(|s| temp(s, &name)) {
            if let Err(error) = inventory(root, &e.path(), &mut paths, 0) {
                paths.push(format!("{name} (size unavailable: {error})"));
            }
        }
    }
    paths.sort();
    if paths.is_empty() { return Ok(Vec::new()); }
    Ok(vec![format!("Recovery files retained without cleanup proof: {}. Inspect them before manual cleanup; Kiln will not delete these files", paths.join(", "))])
}
