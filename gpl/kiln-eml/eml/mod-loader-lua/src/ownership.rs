// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: working .bak files are shared with other writers. Neither their presence nor an EML cache is ownership.
use std::{collections::BTreeMap, fs, io::{self, Read}, path::{Path, PathBuf}};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
struct Digest { size: u64, sha256: String }

fn digest(root: &Path, path: &Path) -> io::Result<Digest> {
    checked_path(root, path)?;
    super::transaction::regular(path)?;
    let mut file = fs::File::open(path)?;
    let mut hash = Sha256::new();
    let mut size = 0;
    let mut buffer = [0u8; 65536];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 { break; }
        size += n as u64;
        hash.update(&buffer[..n]);
    }
    Ok(Digest { size, sha256: format!("{:x}", hash.finalize()) })
}

fn invalid(message: &str) -> io::Error { io::Error::other(message) }

fn check_stem(stem: &str) -> io::Result<()> {
    if stem.is_empty() || !stem.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-') {
        return Err(invalid("invalid archive stem"));
    }
    Ok(())
}

/// `path` is the trusted location itself or one of its ancestors (lexically; case-insensitive on Windows).
fn at_or_above_root(root: &Path, path: &Path) -> bool {
    #[cfg(windows)]
    fn lexical(path: &Path) -> PathBuf {
        // SF persists verbatim paths; Kiln's native host may supply ordinary drive/UNC paths. NTFS names are
        // case-insensitive, so a custom SF path spelled in another case than the exe path is the same location.
        let text = path.to_string_lossy();
        let text = if let Some(rest) = text.strip_prefix(r"\\?\UNC\") { format!(r"\\{rest}") }
            else { text.strip_prefix(r"\\?\").unwrap_or(&text).to_owned() };
        PathBuf::from(text.to_lowercase())
    }
    #[cfg(windows)]
    let (root, path) = (lexical(root), lexical(path));
    root.starts_with(path)
}

// Check existing descendants of the selected game location, including Windows junctions.
// The location itself and ancestors ABOVE it may be legitimate links: volume mounts (Wine reports them as links), or
// a game folder moved with Steam Mover / `mklink /J`. Only what lies below it is Kiln's to vouch for.
// Custom SF state outside the game is checked up to its common ancestor with that location, or its drive root.
fn checked_path(root: &Path, path: &Path) -> io::Result<()> {
    for component in path.ancestors() {
        if component.as_os_str().is_empty() { continue; }
        if at_or_above_root(root, component) { break; }
        if component.parent().is_none() { break; }
        match fs::symlink_metadata(component) {
            Ok(m) => {
                if m.file_type().is_symlink() { return Err(invalid(&format!("linked archive/state path: {}", component.display()))); }
                #[cfg(windows)] {
                    use std::os::windows::fs::MetadataExt;
                    if m.file_attributes() & 0x400 != 0 { return Err(invalid("reparse archive/state path")); }
                }
            },
            Err(e) if e.kind() == io::ErrorKind::NotFound => {},
            Err(e) => return Err(e),
        }
    }
    Ok(())
}

fn bounded(root: &Path, path: &Path) -> io::Result<Vec<u8>> {
    checked_path(root, path)?;
    super::transaction::regular(path)?;
    let mut bytes = Vec::new();
    fs::File::open(path)?.take((16 << 20) + 1).read_to_end(&mut bytes)?;
    if bytes.len() > 16 << 20 { return Err(invalid("archive/state receipt is too large")); }
    Ok(bytes)
}

fn directory(root: &Path) -> io::Result<PathBuf> {
    let dir = root.join("kiln/eml/ownership");
    checked_path(root, &dir)?;
    fs::create_dir_all(&dir)?;
    checked_path(root, &dir)?;
    Ok(dir)
}

fn valid_name(stem: &str, name: &str) -> bool {
    name == format!("{stem}.kfc") || name == format!("{stem}.kfc_resources") || name
        .strip_prefix(&format!("{stem}_")).and_then(|s| s.strip_suffix(".dat"))
        .is_some_and(|s| !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()))
}

#[derive(Serialize, Deserialize)]
struct Receipt {
    format: u32,
    stem: String,
    files: BTreeMap<String, Digest>,
    backups: BTreeMap<String, Digest>,
    /// The owned output that was live when this receipt was written ahead of a publication. Until that publication's
    /// first change the live files are still these, so they stay proof; dropped once the new header is live.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    previous: Option<BTreeMap<String, Digest>>,
}

fn output_set(stem: &str, files: &BTreeMap<String, Digest>) -> bool {
    files.len() >= 2 && files.contains_key(&format!("{stem}.kfc")) && files.contains_key(&format!("{stem}.kfc_resources"))
        && files.keys().all(|n| valid_name(stem, n))
}

fn receipt_path(root: &Path, stem: &str) -> io::Result<PathBuf> {
    check_stem(stem)?;
    let path = root.join("kiln/eml/ownership").join(format!("{stem}.json"));
    checked_path(root, &path)?;
    Ok(path)
}

/// The receipt of `stem`, None when there is none. Err: a receipt Kiln does not understand (kept for recovery).
fn read_receipt(root: &Path, stem: &str) -> io::Result<Option<Receipt>> {
    let path = receipt_path(root, stem)?;
    if !path.try_exists()? { return Ok(None); }
    let receipt: Receipt = serde_json::from_slice(&bounded(root, &path)?).map_err(|_| invalid("invalid EML ownership receipt"))?;
    if receipt.format != 1 || receipt.stem != stem || !output_set(stem, &receipt.files)
        || receipt.previous.as_ref().is_some_and(|p| !output_set(stem, p)) {
        return Err(invalid("unknown EML ownership receipt"));
    }
    Ok(Some(receipt))
}

/// The files of the game folder, each read (SHA-256) at most once per check, and only once its size matches.
struct Live<'a> { root: &'a Path, memo: BTreeMap<String, Option<Digest>> }

impl<'a> Live<'a> {
    fn new(root: &'a Path) -> Self { Self { root, memo: BTreeMap::new() } }

    /// Size from metadata, None when the file is missing.
    fn size(&self, name: &str) -> io::Result<Option<u64>> {
        if let Some(known) = self.memo.get(name) { return Ok(known.as_ref().map(|d| d.size)); }
        let path = self.root.join(name);
        checked_path(self.root, &path)?;
        match fs::symlink_metadata(&path) {
            Ok(m) => Ok(Some(m.len())),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(None),
            Err(e) => Err(e),
        }
    }

    fn digest(&mut self, name: &str) -> io::Result<Option<Digest>> {
        if let Some(known) = self.memo.get(name) { return Ok(known.clone()); }
        let path = self.root.join(name);
        let found = if self.size(name)?.is_some() { Some(digest(self.root, &path)?) } else { None };
        self.memo.insert(name.to_owned(), found.clone());
        Ok(found)
    }

    /// Is every file exactly as listed? Sizes are compared first, so another writer's output is rejected unread.
    fn matches(&mut self, files: &BTreeMap<String, Digest>) -> io::Result<bool> {
        for (name, expected) in files {
            if self.size(name)? != Some(expected.size) { return Ok(false); }
        }
        for (name, expected) in files {
            if self.digest(name)?.as_ref() != Some(expected) { return Ok(false); }
        }
        Ok(true)
    }

    /// The backups present, exactly as listed (and no other): the working baseline the output was built from.
    fn same_backups(&mut self, stem: &str, expected: &BTreeMap<String, Digest>) -> io::Result<bool> {
        for extension in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
            let name = format!("{stem}.{extension}");
            if self.size(&name)? != expected.get(&name).map(|d| d.size) { return Ok(false); }
        }
        self.matches(expected)
    }

    fn backups(&mut self, stem: &str) -> io::Result<BTreeMap<String, Digest>> {
        let mut result = BTreeMap::new();
        for extension in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
            let name = format!("{stem}.{extension}");
            if let Some(d) = self.digest(&name)? { result.insert(name, d); }
        }
        if !result.contains_key(&format!("{stem}.kfc.bak")) { return Err(invalid("no working baseline")); }
        Ok(result)
    }

    /// The generation of `receipt` that is live and was built from the backups present, if one is.
    fn owned(&mut self, stem: &str, receipt: &Receipt) -> io::Result<Option<BTreeMap<String, Digest>>> {
        if !self.same_backups(stem, &receipt.backups)? { return Ok(None); }
        if self.matches(&receipt.files)? { return Ok(Some(receipt.files.clone())); }
        match &receipt.previous {
            Some(previous) if self.matches(previous)? => Ok(Some(previous.clone())),
            _ => Ok(None),
        }
    }
}

/// Exact output and original working-baseline proof. Legacy performance caches never authorize restoration.
pub fn owns_output(root: &Path, stem: &str) -> io::Result<bool> {
    let Some(receipt) = read_receipt(root, stem)? else { return Ok(false) };
    Ok(Live::new(root).owned(stem, &receipt)?.is_some())
}

/// Write ahead: only the exact staged set can later match this proof, including after a publication failure. The
/// owned output still live is kept as the previous generation until the publication's last step (`committed`).
/// The returned guard (SF's locks, when SF's state was handed over) is held until the last archive is published.
pub(crate) fn prepare(root: &Path, stem: &str, files: &[PathBuf]) -> io::Result<Option<SfGuard>> {
    check_stem(stem)?;
    let path = directory(root)?.join(format!("{stem}.json"));
    let earlier = if path.try_exists()? {
        let earlier: Receipt = serde_json::from_slice(&bounded(root, &path)?)?;
        if earlier.format != 1 || earlier.stem != stem {
            return Err(invalid("unknown EML ownership receipt; retain it for recovery before retrying"));
        }
        Some(earlier)
    } else { None };
    let mut live = Live::new(root);
    let mut output = BTreeMap::new();
    let mut changed = false;
    for path in files {
        let name = path.file_name().and_then(|n| n.to_str()).ok_or_else(|| invalid("invalid output name"))?;
        if !valid_name(stem, name) { return Err(invalid("unexpected EML output file")); }
        let expected = digest(root, path)?;
        if live.size(name)? != Some(expected.size) || live.digest(name)?.as_ref() != Some(&expected) { changed = true; }
        output.insert(name.to_owned(), expected);
    }
    if !output.contains_key(&format!("{stem}.kfc")) || !output.contains_key(&format!("{stem}.kfc_resources")) {
        return Err(invalid("incomplete EML output set"));
    }
    let previous = match &earlier {
        Some(earlier) if output_set(stem, &earlier.files) => live.owned(stem, earlier)?.filter(|p| *p != output),
        _ => None,
    };
    let receipt = Receipt { format: 1, stem: stem.into(), files: output, backups: live.backups(stem)?, previous };
    let guard = if changed { handoff(root, stem)? } else { None };
    super::transaction::atomic_write(&path, &serde_json::to_vec(&receipt)?)?;
    Ok(guard)
}

/// What `adopt` found.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Adoption {
    /// A receipt exists already: nothing to adopt (it alone decides).
    Receipt,
    /// The output of an earlier kiln-eml was proven and now has a normal receipt.
    Adopted,
    /// No receipt, and nothing proves the live output is EML's: why, in a few words for the player.
    Unproven(String),
}

/// SHA-256 (for the receipt) and CRC-64 (what the old change cache recorded) of one file, in one pass.
fn digest_and_crc(root: &Path, path: &Path) -> io::Result<(Digest, u64)> {
    checked_path(root, path)?;
    super::transaction::regular(path)?;
    let mut file = fs::File::open(path)?;
    let (mut hash, mut crc, mut size) = (Sha256::new(), crate::cache::CRC64.digest(), 0u64);
    let mut buffer = vec![0u8; 1 << 20];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 { break; }
        size += n as u64;
        hash.update(&buffer[..n]);
        crc.update(&buffer[..n]);
    }
    Ok((Digest { size, sha256: format!("{:x}", hash.finalize()) }, crc.finalize()))
}

/// One-time adoption of the output an earlier kiln-eml (dev.20 to dev.23, before receipts) published, so that switching
/// EML mods off after an update restores it as before. That runner's change cache (`<cache_dir>/files.json`) is
/// written only after a completed publication (or on a start that found nothing changed) and holds the length and
/// CRC-64 of the header and resource file it had just published: read by content, as they were seconds old.
/// It is proof only when it is the cache of a patch run of this stem, names the live header and resource file by
/// content (not by stamp) exactly, and names the backups present exactly; and the backup pair passes its seal. Then a
/// normal receipt is written from the bytes just read. A cache that merely exists, or names files only by their
/// stamps, proves nothing. Reads the header, the resource file and the backups once (hundreds of MiB), only after the
/// cheap checks (names, lengths) passed.
pub fn adopt(root: &Path, stem: &str, cache_dir: &Path) -> io::Result<Adoption> {
    if read_receipt(root, stem)?.is_some() { return Ok(Adoption::Receipt); }
    let unproven = |why: &str| Ok(Adoption::Unproven(why.to_owned()));
    let file = cache_dir.join("files.json");
    if file.starts_with(root) { checked_path(root, &file)?; }
    let meta = match fs::symlink_metadata(&file) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return unproven("no change cache of an earlier kiln-eml names it"),
        Err(e) => return Err(e),
    };
    if !meta.is_file() || meta.file_type().is_symlink() || meta.len() > 64 << 20 {
        return unproven("the earlier change cache is not a regular file");
    }
    let Some(cache) = crate::cache::FileStateCache::read_file(&file) else { return unproven("the earlier change cache cannot be read") };
    if !cache.patched(stem) { return unproven("the earlier change cache is not the record of a completed EML patch run") }
    let live = Live::new(root);
    let outputs = [format!("{stem}.kfc"), format!("{stem}.kfc_resources")];
    for name in &outputs {
        match (cache.saved(&root.join(name)), live.size(name)?) {
            (Some((len, true)), Some(now)) if len == now => {},
            (Some((_, false)), _) => return unproven("the earlier change cache recorded it by its change stamp only, not by content"),
            _ => return unproven("it is not the output the earlier change cache recorded"),
        }
    }
    let backups: Vec<String> = ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"].iter().map(|e| format!("{stem}.{e}")).collect();
    if live.size(&backups[0])?.is_none() { return unproven("there is no vanilla backup"); }
    for name in &backups {
        if cache.saved(&root.join(name)).map(|(len, _)| len) != live.size(name)? {
            return unproven("its backups are not the ones the earlier change cache recorded");
        }
    }
    let bytes: u64 = outputs.iter().chain(&backups).map(|n| live.size(n).ok().flatten().unwrap_or(0)).sum();
    tracing::info!("No ownership receipt for {stem}: checking the earlier kiln-eml's change cache against the game data ({:.1} MiB read, once)", bytes as f64 / (1u64 << 20) as f64);
    let (mut files, mut sealed, mut volume) = (BTreeMap::new(), BTreeMap::new(), None);
    for name in outputs.iter().chain(&backups) {
        let path = root.join(name);
        if !path.try_exists()? { continue; }
        let (digest, crc) = digest_and_crc(root, &path)?;
        if let Err(why) = cache.describes(&path, (digest.size, crc), &mut volume) {
            tracing::info!(file = %name, why, "the earlier change cache does not prove this file");
            return unproven(if outputs.contains(name) { "it is not the output the earlier change cache recorded" }
                else { "its backups are not the ones the earlier change cache recorded" });
        }
        if outputs.contains(name) { files.insert(name.clone(), digest); } else { sealed.insert(name.clone(), digest); }
    }
    let (header, backup) = (root.join(&outputs[0]), root.join(&backups[0]));
    let (Some(header), Some(backup)) = (camino::Utf8Path::from_path(&header), camino::Utf8Path::from_path(&backup)) else {
        return unproven("the game folder's path is not valid UTF-8");
    };
    if !crate::load::validate_backup(header, backup) { return unproven("the vanilla backup does not pass its seal"); }
    let receipt = Receipt { format: 1, stem: stem.into(), files, backups: sealed, previous: None };
    super::transaction::atomic_write(&directory(root)?.join(format!("{stem}.json")), &serde_json::to_vec(&receipt)?)?;
    tracing::info!("Adopted the {stem} game data an earlier kiln-eml published (its change cache names exactly these files and backups): it now has an ownership receipt");
    Ok(Adoption::Adopted)
}

/// The publication's patched header is live: the previous generation is no longer what the game runs.
pub(crate) fn committed(root: &Path, stem: &str) -> io::Result<()> {
    let Some(mut receipt) = read_receipt(root, stem)? else { return Ok(()) };
    if receipt.previous.take().is_none() { return Ok(()); }
    super::transaction::atomic_write(&receipt_path(root, stem)?, &serde_json::to_vec(&receipt)?)
}

/// Hands the archives over from SF before a restore; the returned guard is held until the restore copies are done.
pub(crate) fn before_restore(root: &Path, stem: &str) -> io::Result<Option<SfGuard>> {
    check_stem(stem)?;
    for extension in ["kfc", "kfc_resources"] {
        let backup = root.join(format!("{stem}.{extension}.bak"));
        if backup.try_exists()? && digest(root, &backup)? != digest(root, &root.join(format!("{stem}.{extension}")))? {
            return handoff(root, stem);
        }
    }
    Ok(None)
}

/// SF's write lock, held from the state edit until the last archive write is done. Every SF state write (the
/// `preparing` an SF preparation writes before it touches an archive included) waits for it, so no SF preparation
/// can start in between. SF's startup lease (`<runtime>/startup-assets.lock`) is never taken: SF opens it once,
/// exclusively and without waiting, and a held lease fails both its preparation and its recovery.
pub(crate) struct SfGuard { _write: fs::File }

/// The folder SF's path helpers use for `key` (resolve_installation_paths.rs): they read the config with serde_json
/// and SF's schema (`paths.*`: a non-empty string or null) and use the defaults when that fails, so a BOM, a damaged
/// file or an empty path mean the default folder here too. (SF's own startup refuses such a config outright and
/// restores both backups; its state is still read and written at the default path.)
fn sf_dir(root: &Path, config: Option<&serde_json::Value>, key: &str) -> PathBuf {
    let paths = config.and_then(|c| c.get("paths"));
    let valid = paths.is_none_or(|p| p.as_object().is_some_and(|p| p.values().all(|v| v.is_null() || v.as_str().is_some_and(|s| !s.is_empty()))));
    match paths.filter(|_| valid).and_then(|p| p.get(key)).and_then(|v| v.as_str()) {
        Some(text) => { let path = PathBuf::from(text); if path.is_absolute() { path } else { root.join(path) } },
        None if key == "state" => root.join("shroudforge"),
        None => root.join("shroudforge").join(key),
    }
}

fn sf_config(root: &Path, path: &Path) -> io::Result<Option<serde_json::Value>> {
    if !path.try_exists()? { return Ok(None); }
    Ok(serde_json::from_slice(&bounded(root, path)?).ok())
}

/// SF's own archive publication left its journal (game_file_transaction.rs): SF's next start rolls the live files back
/// from it, over anything written since (and removes files its publication added). Nothing may be written before that.
fn sf_publication_pending(root: &Path, stem: &str) -> io::Result<()> {
    let journal = root.join(format!(".{stem}.shroudforge-stage")).join("pending.json");
    checked_path(root, &journal)?;
    match fs::symlink_metadata(&journal) {
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(e) => Err(e),
        Ok(_) => Err(invalid(&format!("ShroudForge's own publication of {stem} did not finish (its recovery journal is in \
            the game folder): start the game once with ShroudForge so it can recover, then start again"))),
    }
}

// Only invalidate a recognized, unchanged prepared-output cache, never settings or the rest of state.json.
// Keep the exact original bytes and replacement in a recovery journal. Do not restore a now-false "applied" claim.
// Recognition follows SF's own rules (asset-preparation.schema.json, update_loader_state.rs); any other shape refuses.
fn handoff(root: &Path, stem: &str) -> io::Result<Option<SfGuard>> {
    let config_dir = root.join("shroudforge/config");
    let config_path = config_dir.join("modloader-config.json");
    checked_path(root, &config_path)?;
    let config = sf_config(root, &config_path)?;
    let state_dir = sf_dir(root, config.as_ref(), "state");
    let state = state_dir.join("state.json");
    checked_path(root, &state)?;
    if !state.try_exists()? { sf_publication_pending(root, stem)?; return Ok(None); }
    // Same installation lock as ShroudForge's document writer. Never wait on an active standalone loader.
    checked_path(root, &config_dir)?;
    fs::create_dir_all(&config_dir)?;
    let lock_path = config_dir.join(".shroudforge-write.lock");
    checked_path(root, &lock_path)?;
    let lock = fs::OpenOptions::new().create(true).truncate(false).read(true).write(true).open(&lock_path)?;
    lock.try_lock().map_err(|e| invalid(&format!("ShroudForge state is in use: {e}")))?;
    let guard = SfGuard { _write: lock };
    sf_publication_pending(root, stem)?;
    // Recheck the configured destination under the lock, not only the state bytes.
    if sf_dir(root, sf_config(root, &config_path)?.as_ref(), "state") != state_dir {
        return Err(invalid("ShroudForge state path changed during handoff"));
    }
    let original = bounded(root, &state)?;
    let mut value: serde_json::Value = serde_json::from_slice(&original)?;
    if value.get("schemaVersion").is_some_and(|v| v.as_u64() != Some(1)) {
        return Err(invalid("unknown ShroudForge state schema; refusing archive handoff"));
    }
    let Some(assets) = value.get_mut("assets") else { return Ok(Some(guard)); };
    let target = assets.get("target").and_then(|v| v.as_str());
    let other = |t: Option<&str>| matches!(t, Some(t @ ("enshrouded" | "enshrouded_server")) if t != stem);
    match assets.get("status").and_then(|v| v.as_str()) {
        Some("failed") => return Ok(Some(guard)),
        Some("preparing" | "applied") if other(target) => return Ok(Some(guard)),
        Some("preparing") if target == Some(stem) => return Err(invalid(&format!(
            "ShroudForge is preparing its asset mods for {stem} (or its last preparation did not finish): let it finish, then start again"))),
        Some("applied") if target == Some(stem) => {},
        _ => return Err(invalid("unknown ShroudForge prepared-output cache; refusing archive handoff")),
    }
    let fingerprint = assets.get("fingerprint").and_then(|v| v.as_str()).unwrap_or("");
    if assets.get("schemaVersion").and_then(|v| v.as_u64()) != Some(1) || fingerprint.len() != 64 ||
        !fingerprint.bytes().all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) ||
        !assets.get("mods").is_some_and(|v| v.is_array()) ||
        !assets.get("completedAt").is_some_and(|v| v.is_u64()) ||
        !matches!(assets.get("phase").and_then(|v| v.as_str()), Some("prepare" | "startup")) {
        return Err(invalid("unknown ShroudForge prepared-output cache; refusing archive handoff"));
    }
    assets["status"] = "failed".into();
    assets["error"] = "Kiln's EML writer changed game data; ShroudForge must prepare its asset mods again".into();
    let replacement = serde_json::to_vec_pretty(&value)?;
    let recovery = directory(root)?.join(format!("sf-handoff-{}.json", uuid::Uuid::new_v4()));
    let journal = serde_json::json!({"format":1,"target":stem,"statePath":state,"original":String::from_utf8(original.clone()).map_err(|_| invalid("non UTF-8 SF state"))?,"replacement":String::from_utf8(replacement.clone()).unwrap()});
    super::transaction::atomic_write(&recovery, &serde_json::to_vec(&journal)?)?;
    if bounded(root, &state)? != original { return Err(invalid("ShroudForge state changed during handoff; original retained")); }
    super::transaction::atomic_write(&state, &replacement)?;
    tracing::info!(path = %state.display(), recovery = %recovery.display(), "Invalidated only ShroudForge's stale prepared-output cache before EML changed game data; settings retained");
    Ok(Some(guard))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (PathBuf, Vec<PathBuf>) {
        let root = std::env::temp_dir().join(format!("kiln-ownership-{}", uuid::Uuid::new_v4()));
        fs::create_dir_all(root.join("stage")).unwrap();
        let mut files = Vec::new();
        for (name, old, new) in [("g.kfc", "SF header", "EML header"), ("g.kfc_resources", "SF resources", "EML resources"), ("g_010.dat", "", "content")] {
            fs::write(root.join(name), old).unwrap();
            let path = root.join("stage").join(name);
            fs::write(&path, new).unwrap();
            files.push(path);
        }
        fs::write(root.join("g.kfc.bak"), "original header").unwrap();
        fs::write(root.join("g.kfc_resources.bak"), "original resources").unwrap();
        (root, files)
    }
    fn sf(root: &Path, custom: bool) -> (PathBuf, Vec<u8>) {
        fs::create_dir_all(root.join("shroudforge/config")).unwrap();
        let dir = if custom { root.join("custom-state") } else { root.join("shroudforge") };
        fs::create_dir_all(&dir).unwrap();
        fs::write(root.join("shroudforge/config/modloader-config.json"), serde_json::to_vec(&serde_json::json!({"paths":{"state":dir},"playerSetting":"keep"})).unwrap()).unwrap();
        let bytes = serde_json::to_vec(&serde_json::json!({"schemaVersion":1,"windows":{"favorite":"cyan"},"assets":{"schemaVersion":1,"status":"applied","phase":"startup","target":"g","fingerprint":"a".repeat(64),"completedAt":42,"mods":[{"id":"player-sf","version":"1.0.0"}]}})).unwrap();
        let path = dir.join("state.json");
        fs::write(&path, &bytes).unwrap();
        (path, bytes)
    }
    fn publish(root: &Path, files: &[PathBuf]) {
        for path in files { fs::copy(path, root.join(path.file_name().unwrap())).unwrap(); }
    }
    #[test]
    fn real_eml_content_write_disable_restores_original_pair_and_referenced_dat_sizes() {
        use std::rc::Rc;
        use kfc::{container::{KFCFile, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};
        let root = std::env::temp_dir().join(format!("kiln-real-owner-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        fs::File::create(root.join("g.kfc")).unwrap();
        fs::File::create(root.join("g.kfc_resources")).unwrap();
        let types = Rc::new(TypeRegistry::default());
        let mut original: crate::transaction::Writer = KFCWriter::new(&root, "g", types.clone(), "fixture").unwrap();
        original.write_content(&ContentHash::from_data(b"original"), b"original").unwrap();
        original.finalize().unwrap();
        let original_header = fs::read(root.join("g.kfc")).unwrap();
        let original_resources = fs::read(root.join("g.kfc_resources")).unwrap();
        let path = camino::Utf8Path::from_path(&root).unwrap();
        crate::load::create_backup(path.join("g.kfc")).unwrap();
        let base = Rc::new(KFCFile::from_path(root.join("g.kfc.bak"), false).unwrap());
        let mut patch = crate::load::create_writer(path, "g", &types, &base).unwrap();
        patch.write_content(&ContentHash::from_data(b"new content"), b"new content").unwrap();
        patch.finalize().unwrap();
        assert!(owns_output(&root, "g").unwrap());
        assert!(crate::restore(path, "g"));
        assert_eq!(fs::read(root.join("g.kfc")).unwrap(), original_header);
        assert_eq!(fs::read(root.join("g.kfc_resources")).unwrap(), original_resources);
        let restored = KFCFile::from_path(root.join("g.kfc"), false).unwrap();
        for (index, container) in restored.containers().iter().enumerate() {
            assert_eq!(fs::metadata(root.join(format!("g_{index:03}.dat"))).unwrap().len(), container.size,
                "every container the restored header lists has the size it lists (the game refuses to start otherwise)");
        }
        // the container EML wrote stays on disk, outside the restored header's list (the game never opens it; the next
        // patch run truncates or reuses it)
        let written = restored.containers().len();
        assert!(fs::metadata(root.join(format!("g_{written:03}.dat"))).unwrap().len() > 0);
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    /// A real EML publication over a real KFC pair (sealed backups, a new content container), as above.
    fn real_published() -> (PathBuf, Vec<u8>, Vec<u8>) {
        use std::rc::Rc;
        use kfc::{container::{KFCFile, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};
        let root = std::env::temp_dir().join(format!("kiln-real-handoff-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        fs::File::create(root.join("g.kfc")).unwrap();
        fs::File::create(root.join("g.kfc_resources")).unwrap();
        let types = Rc::new(TypeRegistry::default());
        let mut original: crate::transaction::Writer = KFCWriter::new(&root, "g", types.clone(), "fixture").unwrap();
        original.write_content(&ContentHash::from_data(b"original"), b"original").unwrap();
        original.finalize().unwrap();
        let (header, resources) = (fs::read(root.join("g.kfc")).unwrap(), fs::read(root.join("g.kfc_resources")).unwrap());
        let path = camino::Utf8Path::from_path(&root).unwrap();
        crate::load::create_backup(path.join("g.kfc")).unwrap();
        let base = Rc::new(KFCFile::from_path(root.join("g.kfc.bak"), false).unwrap());
        let mut patch = crate::load::create_writer(path, "g", &types, &base).unwrap();
        patch.write_content(&ContentHash::from_data(b"new content"), b"new content").unwrap();
        patch.finalize().unwrap();
        assert!(owns_output(&root, "g").unwrap());
        (root, header, resources)
    }
    // The restore side of the handoff: a recognised SF claim is invalidated (journalled) before the vanilla data goes
    // back; a busy SF, an SF preparation in progress or a state Kiln does not know refuses, and nothing is copied.
    #[test]
    fn restore_hands_over_a_recognized_sf_state_and_refuses_a_busy_or_unknown_one() {
        for case in ["applied", "busy", "unknown", "preparing"] {
            let (root, header, resources) = real_published();
            let patched = (fs::read(root.join("g.kfc")).unwrap(), fs::read(root.join("g.kfc_resources")).unwrap());
            let (state, _) = sf(&root, false);
            let held = match case {
                "busy" => { let f = fs::File::create(root.join("shroudforge/config/.shroudforge-write.lock")).unwrap(); f.lock().unwrap(); Some(f) },
                "unknown" => { edit_sf(&state, |v| v["assets"]["schemaVersion"] = 99.into()); None },
                "preparing" => { edit_sf(&state, |v| v["assets"] = serde_json::json!({"schemaVersion":1,"status":"preparing","target":"g"})); None },
                _ => None,
            };
            let before = fs::read(&state).unwrap();
            let restored = crate::restore(camino::Utf8Path::from_path(&root).unwrap(), "g");
            let now = (fs::read(root.join("g.kfc")).unwrap(), fs::read(root.join("g.kfc_resources")).unwrap());
            if case == "applied" {
                assert!(restored, "{case}");
                assert_eq!(now, (header, resources), "{case}");
                let current: serde_json::Value = serde_json::from_slice(&fs::read(&state).unwrap()).unwrap();
                assert_eq!(current["assets"]["status"], "failed", "{case}: SF must prepare again");
                assert!(fs::read_dir(root.join("kiln/eml/ownership")).unwrap().flatten().any(|e| e.file_name().to_string_lossy().starts_with("sf-handoff-")));
            } else {
                assert!(!restored, "{case}");
                assert_eq!(now, patched, "{case}: nothing copied");
                assert_eq!(fs::read(&state).unwrap(), before, "{case}: SF state untouched");
            }
            drop(held);
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn legacy_cache_and_foreign_backups_do_not_authorize_restore() {
        let (root, _) = fixture();
        fs::create_dir_all(root.join("kiln/eml/cache")).unwrap();
        fs::write(root.join("kiln/eml/cache/files.json"), "{}").unwrap();
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn proof_requires_exact_published_header_resources_dat_and_original_backups() {
        for change in ["g.kfc", "g.kfc_resources", "g_010.dat", "g.kfc.bak", "g.kfc_resources.bak"] {
            let (root, files) = fixture();
            prepare(&root, "g", &files).unwrap();
            assert!(!owns_output(&root, "g").unwrap(), "staging is not publication");
            publish(&root, &files);
            assert!(owns_output(&root, "g").unwrap());
            fs::write(root.join(change), "player or other writer changed it").unwrap();
            assert!(!owns_output(&root, "g").unwrap(), "{change}");
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn only_recognized_sf_cache_is_invalidated_and_exact_original_is_journalled() {
        for custom in [false, true] {
            let (root, files) = fixture();
            let (state, original) = sf(&root, custom);
            let config = fs::read(root.join("shroudforge/config/modloader-config.json")).unwrap();
            prepare(&root, "g", &files).unwrap();
            let current: serde_json::Value = serde_json::from_slice(&fs::read(&state).unwrap()).unwrap();
            let mut expected: serde_json::Value = serde_json::from_slice(&original).unwrap();
            expected["assets"]["status"] = "failed".into();
            expected["assets"]["error"] = current["assets"]["error"].clone();
            assert_eq!(current, expected, "all non-cache player state survives");
            assert_eq!(fs::read(root.join("shroudforge/config/modloader-config.json")).unwrap(), config);
            let journal = fs::read_dir(root.join("kiln/eml/ownership")).unwrap().flatten().find(|e| e.file_name().to_string_lossy().starts_with("sf-handoff-")).unwrap();
            let value: serde_json::Value = serde_json::from_slice(&fs::read(journal.path()).unwrap()).unwrap();
            assert_eq!(value["original"].as_str().unwrap().as_bytes(), original);
            assert_eq!(value["replacement"].as_str().unwrap().as_bytes(), fs::read(state).unwrap());
            fs::remove_dir_all(root).unwrap();
        }
    }
    #[test]
    fn unchanged_output_never_mutates_sf_state() {
        let (root, files) = fixture();
        publish(&root, &files);
        let (state, original) = sf(&root, false);
        prepare(&root, "g", &files).unwrap();
        assert_eq!(fs::read(state).unwrap(), original);
        assert_eq!(fs::read_dir(root.join("kiln/eml/ownership")).unwrap().count(), 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn unknown_sf_schema_blocks_publication_without_modifying_state_or_game() {
        let (root, files) = fixture();
        let (state, _) = sf(&root, false);
        let bytes = fs::read_to_string(&state).unwrap().replacen("\"schemaVersion\":1", "\"schemaVersion\":99", 1);
        fs::write(&state, &bytes).unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read_to_string(state).unwrap(), bytes);
        assert_eq!(fs::read(root.join("g.kfc")).unwrap(), b"SF header");
        assert!(!owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn unknown_receipt_is_not_adopted() {
        let (root, files) = fixture();
        prepare(&root, "g", &files).unwrap(); publish(&root, &files);
        fs::write(root.join("kiln/eml/ownership/g.json"), r#"{"format":99,"stem":"g","files":{},"backups":{}}"#).unwrap();
        assert!(owns_output(&root, "g").is_err());
        let original = fs::read(root.join("kiln/eml/ownership/g.json")).unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read(root.join("kiln/eml/ownership/g.json")).unwrap(), original);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn invalid_stem_never_creates_or_reads_a_receipt() {
        let (root, files) = fixture();
        for stem in ["", "../g", "g/other", r"g\other", "."] {
            assert!(owns_output(&root, stem).is_err());
            assert!(prepare(&root, stem, &files).is_err());
            assert!(before_restore(&root, stem).is_err());
        }
        assert!(!root.join("kiln").exists());
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn blocked_handoff_keeps_previous_output_proof_for_safe_retry() {
        let (root, files) = fixture();
        prepare(&root, "g", &files).unwrap(); publish(&root, &files);
        let original_receipt = fs::read(root.join("kiln/eml/ownership/g.json")).unwrap();
        let (state, _) = sf(&root, false);
        let original = fs::read_to_string(&state).unwrap().replacen("\"schemaVersion\":1", "\"schemaVersion\":99", 1);
        fs::write(&state, &original).unwrap();
        fs::write(&files[0], "next EML header").unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert!(owns_output(&root, "g").unwrap());
        assert_eq!(fs::read_to_string(state).unwrap(), original);
        assert_eq!(fs::read(root.join("kiln/eml/ownership/g.json")).unwrap(), original_receipt);
        fs::remove_dir_all(root).unwrap();
    }
    fn edit_sf(state: &Path, edit: impl FnOnce(&mut serde_json::Value)) -> Vec<u8> {
        let mut value: serde_json::Value = serde_json::from_slice(&fs::read(state).unwrap()).unwrap();
        edit(&mut value);
        let bytes = serde_json::to_vec(&value).unwrap();
        fs::write(state, &bytes).unwrap();
        bytes
    }
    // SF's own rules (asset-preparation.schema.json, update_loader_state.rs): an `applied` claim names its target,
    // the root schema is version 1. Any other shape is not something Kiln may rewrite.
    #[test]
    fn sf_state_shapes_outside_sf_rules_refuse_and_another_target_is_left_alone() {
        let cases: [(&str, fn(&mut serde_json::Value), bool); 6] = [
            ("no target", |v| { v["assets"].as_object_mut().unwrap().remove("target"); }, false),
            ("target not a string", |v| v["assets"]["target"] = 7.into(), false),
            ("unknown target", |v| v["assets"]["target"] = "game".into(), false),
            ("root schema 2", |v| v["schemaVersion"] = 2.into(), false),
            ("unknown status", |v| v["assets"]["status"] = "done".into(), false),
            ("the other side's archives", |v| v["assets"]["target"] = "enshrouded_server".into(), true),
        ];
        for (what, edit, publishes) in cases {
            let (root, files) = fixture();
            let (state, _) = sf(&root, false);
            let bytes = edit_sf(&state, edit);
            assert_eq!(prepare(&root, "g", &files).is_ok(), publishes, "{what}");
            assert_eq!(fs::read(&state).unwrap(), bytes, "{what}: SF state untouched");
            fs::remove_dir_all(root).unwrap();
        }
    }
    // SF reads its config with serde_json and falls back to its default paths when that fails (read_loader(root).ok()?):
    // a BOM or a damaged config means the state SF uses is the default one, which is the one to hand over.
    #[test]
    fn unreadable_sf_config_means_sf_default_state_path() {
        for config in [b"\xef\xbb\xbf{\"paths\":{\"state\":\"custom-state\"}}".as_slice(), b"{not json", b"", b"{\"paths\":{\"state\":\"\"}}"] {
            let (root, files) = fixture();
            let (state, original) = sf(&root, false);
            fs::write(root.join("shroudforge/config/modloader-config.json"), config).unwrap();
            prepare(&root, "g", &files).unwrap();
            let current: serde_json::Value = serde_json::from_slice(&fs::read(&state).unwrap()).unwrap();
            assert_eq!(current["assets"]["status"], "failed", "{:?}", String::from_utf8_lossy(config));
            assert_ne!(fs::read(&state).unwrap(), original);
            fs::remove_dir_all(root).unwrap();
        }
    }
    // SF's standalone prepare writes `preparing` and then rewrites the archives holding no lock (only "game stopped").
    #[test]
    fn sf_preparation_in_progress_blocks_archive_handoff() {
        let (root, files) = fixture();
        let (state, _) = sf(&root, false);
        let bytes = edit_sf(&state, |v| v["assets"] = serde_json::json!({"schemaVersion":1,"status":"preparing","target":"g"}));
        assert!(prepare(&root, "g", &files).is_err());
        assert!(before_restore(&root, "g").is_err());
        assert_eq!(fs::read(&state).unwrap(), bytes);
        fs::remove_dir_all(root).unwrap();
    }
    // An unfinished SF publication: SF's next start rolls the live files back from its journal, over Kiln's output.
    #[test]
    fn unfinished_sf_publication_blocks_archive_handoff() {
        let (root, files) = fixture();
        let (state, original) = sf(&root, false);
        fs::create_dir_all(root.join(".g.shroudforge-stage")).unwrap();
        fs::write(root.join(".g.shroudforge-stage/pending.json"), "[]").unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert!(before_restore(&root, "g").is_err());
        assert_eq!(fs::read(&state).unwrap(), original);
        // the same without any SF state: the journal alone still means SF will roll back
        fs::remove_file(&state).unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        fs::remove_file(root.join(".g.shroudforge-stage/pending.json")).unwrap();
        assert!(prepare(&root, "g", &files).is_ok());
        fs::remove_dir_all(root).unwrap();
    }
    // Every SF state write (an SF preparation's `preparing` first) waits for the write lock: holding it until the last
    // archive write means no SF preparation starts in between. SF's startup lease is never taken or created: SF opens
    // it once without waiting, and a held lease would fail its preparation and its recovery.
    #[test]
    fn sf_write_lock_is_held_until_the_archives_are_written_and_the_startup_lease_never_taken() {
        let (root, _) = fixture();
        let (state, _) = sf(&root, false);
        fs::create_dir_all(root.join("shroudforge/runtime")).unwrap();
        let guard = handoff(&root, "g").unwrap();
        assert!(guard.is_some());
        let busy = || fs::OpenOptions::new().read(true).write(true).open(root.join("shroudforge/config/.shroudforge-write.lock")).unwrap().try_lock().is_err();
        assert!(busy());
        assert!(!root.join("shroudforge/runtime/startup-assets.lock").exists());
        drop(guard);
        assert!(!busy());
        let current: serde_json::Value = serde_json::from_slice(&fs::read(state).unwrap()).unwrap();
        // what SF needs of a failed document (asset-preparation.schema.json), and a root it can read (state.schema.json)
        let assets = &current["assets"];
        assert_eq!((assets["status"].as_str(), assets["target"].as_str(), assets["schemaVersion"].as_u64()), (Some("failed"), Some("g"), Some(1)));
        assert!(assets["phase"].is_string() && assets["completedAt"].is_u64() && assets["error"].as_str().is_some_and(|e| !e.is_empty()));
        assert_eq!(current["schemaVersion"], 1);
        fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn busy_sf_writer_blocks_archive_handoff() {
        let (root, files) = fixture();
        let (state, original) = sf(&root, false);
        let lock = fs::File::create(root.join("shroudforge/config/.shroudforge-write.lock")).unwrap();
        lock.lock().unwrap();
        assert!(prepare(&root, "g", &files).is_err());
        assert_eq!(fs::read(state).unwrap(), original);
        drop(lock); fs::remove_dir_all(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn trusted_location_can_have_mounted_ancestors_but_not_linked_descendants() {
        let (root, _) = fixture();
        fs::create_dir_all(root.join("volume/game")).unwrap();
        std::os::unix::fs::symlink(root.join("volume"), root.join("mount")).unwrap();
        let game = root.join("mount/game");
        fs::write(game.join("safe"), "mounted volume").unwrap();
        assert!(checked_path(&game, &game.join("safe")).is_ok());
        std::os::unix::fs::symlink(game.join("safe"), game.join("linked")).unwrap();
        assert!(checked_path(&game, &game.join("linked")).is_err());
        assert!(checked_path(&root, &game.join("safe")).is_err());
        fs::remove_dir_all(root).unwrap();
    }
    // Steam Mover and `mklink /J` leave the game folder itself as a link: the trusted location may be one.
    #[cfg(unix)]
    #[test]
    fn a_game_folder_that_is_itself_a_link_can_publish_and_restore() {
        let (real, files) = fixture();
        let game = real.with_extension("link");
        std::os::unix::fs::symlink(&real, &game).unwrap();
        let files: Vec<PathBuf> = files.iter().map(|f| game.join("stage").join(f.file_name().unwrap())).collect();
        prepare(&game, "g", &files).unwrap();
        publish(&game, &files);
        assert!(owns_output(&game, "g").unwrap());
        before_restore(&game, "g").unwrap();
        // a link below the trusted location is still refused
        std::os::unix::fs::symlink(real.join("victim"), game.join("kiln/eml/linked")).unwrap();
        assert!(checked_path(&game, &game.join("kiln/eml/linked/g.json")).is_err());
        fs::remove_file(&game).unwrap();
        fs::remove_dir_all(real).unwrap();
    }
    #[cfg(windows)]
    #[test]
    fn logical_windows_game_root_accepts_ordinary_and_verbatim_paths() {
        let (root, files) = fixture();
        assert!(checked_path(&root, &files[0]).is_ok());
        let verbatim = root.canonicalize().unwrap();
        assert!(checked_path(&root, &verbatim.join("stage/g.kfc")).is_ok());
        assert!(checked_path(&verbatim, &root.join("stage/g.kfc")).is_ok());
        // the same folder spelled in another case (a custom SF path typed by hand) is the trusted location too
        let upper = PathBuf::from(root.to_string_lossy().to_uppercase());
        assert!(checked_path(&upper, &root.join("stage/g.kfc")).is_ok());
        // a game folder that is itself a directory link (Steam Mover, mklink /J) is trusted; links below it are not
        let link = root.with_extension("link");
        std::os::windows::fs::symlink_dir(&root, &link).unwrap();
        let staged: Vec<PathBuf> = files.iter().map(|f| link.join("stage").join(f.file_name().unwrap())).collect();
        prepare(&link, "g", &staged).unwrap();
        publish(&link, &staged);
        assert!(owns_output(&link, "g").unwrap());
        std::os::windows::fs::symlink_dir(root.join("stage"), link.join("kiln/eml/linked")).unwrap();
        assert!(checked_path(&link, &link.join("kiln/eml/linked/g.json")).is_err());
        fs::remove_dir(link.join("kiln/eml/linked")).unwrap();
        fs::remove_dir(&link).unwrap();
        fs::remove_dir_all(root).unwrap();
    }
    #[cfg(any(unix, windows))]
    #[test]
    fn linked_state_and_linked_ownership_parent_are_not_followed() {
        for state_link in [false, true] {
            let (root, files) = fixture();
            let (state, original) = sf(&root, false);
            if state_link {
                fs::rename(&state, root.join("victim.json")).unwrap();
                #[cfg(unix)]
                std::os::unix::fs::symlink(root.join("victim.json"), &state).unwrap();
                #[cfg(windows)]
                std::os::windows::fs::symlink_file(root.join("victim.json"), &state).unwrap();
            } else {
                fs::create_dir_all(root.join("kiln/eml")).unwrap();
                fs::create_dir(root.join("victim")).unwrap();
                #[cfg(unix)]
                std::os::unix::fs::symlink(root.join("victim"), root.join("kiln/eml/ownership")).unwrap();
                #[cfg(windows)]
                std::os::windows::fs::symlink_dir(root.join("victim"), root.join("kiln/eml/ownership")).unwrap();
            }
            assert!(prepare(&root, "g", &files).is_err());
            assert_eq!(fs::read(&state).unwrap(), original);
            fs::remove_dir_all(root).unwrap();
        }
    }
}
