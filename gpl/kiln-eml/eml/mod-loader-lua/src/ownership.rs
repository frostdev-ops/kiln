// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: working .bak files are shared with other writers. Neither their presence nor an EML cache is ownership.
use std::{collections::BTreeMap, fs, io::{self, Read}, path::{Path, PathBuf}};
use serde::{Deserialize, Serialize};
use sha2::{Digest as _, Sha256};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub(crate) struct Digest { size: u64, sha256: String }

thread_local! { static READ: std::cell::Cell<u64> = const { std::cell::Cell::new(0) }; }

/// Bytes the ownership checks of this thread hashed since `reset_read` (they are outside the change cache's counter).
pub(crate) fn read() -> u64 { READ.with(|r| r.get()) }
pub(crate) fn reset_read() { READ.with(|r| r.set(0)); DISPLACED.with(|d| d.borrow_mut().clear()); }

thread_local! { static DISPLACED: std::cell::RefCell<Vec<String>> = const { std::cell::RefCell::new(Vec::new()) }; }

/// The ShroudForge asset mods (`<id> <version>`) whose prepared output a publication of this thread replaced since
/// `reset_read`: kiln-eml builds from the vanilla backup, so this start's game data does not have their patches. (It
/// publishes only in a start where ShroudForge does not run: hosted ShroudForge makes it stand aside.)
pub(crate) fn displaced() -> Vec<String> { DISPLACED.with(|d| d.borrow().clone()) }
fn count(n: usize) { READ.with(|r| r.set(r.get() + n as u64)); }

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
        count(n);
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
                    if (m.file_attributes() | super::transaction::attributes_by_name(component)?) & 0x400 != 0 {
                        return Err(invalid(&format!("reparse archive/state path: {}", component.display())));
                    }
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

    /// Has every file the listed size (from metadata, nothing read)?
    fn sizes(&self, files: &BTreeMap<String, Digest>) -> io::Result<bool> {
        for (name, expected) in files {
            if self.size(name)? != Some(expected.size) { return Ok(false); }
        }
        Ok(true)
    }

    /// Is every file exactly as listed? Sizes are compared first, so another writer's output is rejected unread.
    fn matches(&mut self, files: &BTreeMap<String, Digest>) -> io::Result<bool> {
        if !self.sizes(files)? { return Ok(false); }
        for (name, expected) in files {
            if self.digest(name)?.as_ref() != Some(expected) { return Ok(false); }
        }
        Ok(true)
    }

    /// The backups present are the listed ones, with the listed sizes (and no other; nothing read).
    fn backup_sizes(&self, stem: &str, expected: &BTreeMap<String, Digest>) -> io::Result<bool> {
        for extension in ["kfc.bak", "kfc_resources.bak", "kfc.bak.integrity"] {
            let name = format!("{stem}.{extension}");
            if self.size(&name)? != expected.get(&name).map(|d| d.size) { return Ok(false); }
        }
        Ok(true)
    }

    /// The backups present, exactly as listed (and no other): the working baseline the output was built from.
    fn same_backups(&mut self, stem: &str, expected: &BTreeMap<String, Digest>) -> io::Result<bool> {
        Ok(self.backup_sizes(stem, expected)? && self.matches(expected)?)
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

    /// Could a generation of `receipt` be live, from sizes alone (metadata only)?
    fn plausible(&self, stem: &str, receipt: &Receipt) -> io::Result<bool> {
        Ok(self.backup_sizes(stem, &receipt.backups)?
            && (self.sizes(&receipt.files)? || match &receipt.previous { Some(p) => self.sizes(p)?, None => false }))
    }

    /// The generation of `receipt` that is live and was built from the backups present, if one is. Every size is
    /// compared before anything is read.
    fn owned(&mut self, stem: &str, receipt: &Receipt) -> io::Result<Option<BTreeMap<String, Digest>>> {
        if !self.plausible(stem, receipt)? || !self.same_backups(stem, &receipt.backups)? { return Ok(None); }
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

/// SHA-256 of the receipt's bytes: what the change cache keeps as `owner` (cache/mod.rs) once its fingerprints of the
/// game files are known to be that receipt's live output. None: no receipt.
pub(crate) fn marker(root: &Path, stem: &str) -> io::Result<Option<String>> {
    let path = receipt_path(root, stem)?;
    if !path.try_exists()? { return Ok(None); }
    Ok(Some(format!("{:x}", Sha256::digest(bounded(root, &path)?))))
}

/// The unchanged start (cache hit): the change cache found every game file as it fingerprinted them when it recorded
/// `marker` for this very receipt, so the receipt's output is still what is live. Only metadata is looked at here
/// (the receipt's sizes, as a second opinion); the full SHA-256 check stays for the moment before a restore.
pub(crate) fn vouched(root: &Path, stem: &str, saved: Option<&str>) -> io::Result<Option<String>> {
    let Some(saved) = saved else { return Ok(None) };
    let Some(now) = marker(root, stem)? else { return Ok(None) };
    if now != saved { return Ok(None); }
    let Some(receipt) = read_receipt(root, stem)? else { return Ok(None) };
    Ok(Live::new(root).plausible(stem, &receipt)?.then_some(now))
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
    let guard = if changed { handoff(root, stem, true)? } else { None };
    super::transaction::atomic_write(&path, &serde_json::to_vec(&receipt)?)?;
    Ok(guard)
}

fn is_uuid(text: &str) -> bool { text.len() == 36 && uuid::Uuid::try_parse(text).is_ok() }

/// `<base>.kiln-<uuid>`: the temp copy `atomic_copy`/`atomic_write` makes next to its destination (transaction.rs).
fn temp_of(name: &str) -> Option<&str> {
    let (base, id) = name.rsplit_once(".kiln-")?;
    is_uuid(id).then_some(base)
}

/// Removes what a kiln-eml run killed half way left behind: its private stage folders (`.kiln-eml-stage-<uuid>`, a full
/// copy of the resource file each), temp copies over the archives, backups and receipts (`<name>.kiln-<uuid>`), and
/// SF handoff journals beyond the newest 16 (one is written per handoff; the newest hold any state worth recovering).
/// Call it holding the install lock, so no other run of this install is writing. Only exact shapes go: a stage folder
/// holding anything but staged game files, a link, or a name without kiln-eml's uuid is left alone.
pub fn prune(root: &Path, stem: &str) {
    if check_stem(stem).is_err() { return; }
    let stems = ["enshrouded", "enshrouded_server", stem];
    let staged = |name: &str| stems.iter().any(|s| valid_name(s, name)) || name == super::transaction::VANILLA_HEADER || temp_of(name).is_some();
    let (mut removed, mut freed) = (Vec::new(), 0u64);
    let mut remove = |path: &Path, what: String| {
        let size = fs::symlink_metadata(path).map_or(0, |m| m.len());
        if fs::remove_file(path).is_ok() { freed += size; removed.push(what); }
    };
    let Ok(entries) = fs::read_dir(root) else { return };
    for entry in entries.flatten() {
        let (path, name) = (entry.path(), entry.file_name().to_string_lossy().into_owned());
        if checked_path(root, &path).is_err() { continue; }
        if let Some(id) = name.strip_prefix(".kiln-eml-stage-") {
            if !is_uuid(id) || !fs::symlink_metadata(&path).is_ok_and(|m| m.is_dir()) { continue; }
            let Ok(files) = fs::read_dir(&path).map(|e| e.flatten().collect::<Vec<_>>()) else { continue };
            if !files.iter().all(|f| super::transaction::regular(&f.path()).is_ok() && staged(&f.file_name().to_string_lossy())) {
                tracing::warn!(folder = %path.display(), "an old EML stage folder holds other files; left as it is");
                continue;
            }
            for f in &files { remove(&f.path(), format!("{name}/{}", f.file_name().to_string_lossy())); }
            let _ = fs::remove_dir(&path);
        } else if let Some(base) = temp_of(&name) {
            if stems.iter().any(|s| base == *s || base == format!("{s}.kfc") || base == format!("{s}.kfc.bak"))
                && super::transaction::regular(&path).is_ok() {
                remove(&path, name);
            }
        }
    }
    let dir = root.join("kiln/eml/ownership");
    if checked_path(root, &dir).is_ok() && fs::symlink_metadata(&dir).is_ok_and(|m| m.is_dir()) {
        let mut journals = Vec::new();
        for entry in fs::read_dir(&dir).into_iter().flatten().flatten() {
            let (path, name) = (entry.path(), entry.file_name().to_string_lossy().into_owned());
            if super::transaction::regular(&path).is_err() { continue; }
            if let Some(base) = temp_of(&name) {
                if stems.contains(&base) || base.strip_prefix("sf-handoff-").is_some_and(|r| r.strip_suffix(".json").is_some_and(is_uuid)) {
                    remove(&path, name);
                }
            } else if name.strip_prefix("sf-handoff-").and_then(|r| r.strip_suffix(".json")).is_some_and(is_uuid) {
                journals.push((entry.metadata().and_then(|m| m.modified()).unwrap_or(std::time::UNIX_EPOCH), path, name));
            }
        }
        journals.sort_by(|a, b| b.0.cmp(&a.0));
        for (_, path, name) in journals.into_iter().skip(16) { remove(&path, name); }
    }
    if !removed.is_empty() {
        tracing::info!("Removed {} file(s) ({:.1} MiB) an earlier EML run left behind: {}", removed.len(), freed as f64 / (1u64 << 20) as f64, removed.join(", "));
    }
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
        count(n);
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
    // the containers that run added (beyond the vanilla header's), where the cache names them by content: listed, so
    // that a restore can remove them too. One the cache does not prove is left out (and so never removed).
    let vanilla = containers(root, &backups[0]).unwrap_or(usize::MAX);
    for entry in fs::read_dir(root)? {
        let name = entry?.file_name().to_string_lossy().into_owned();
        if container_index(stem, &name).is_none_or(|i| i < vanilla) { continue; }
        let path = root.join(&name);
        if !cache.saved(&path).is_some_and(|(len, content)| content && live.size(&name).ok().flatten() == Some(len)) { continue; }
        let (digest, crc) = digest_and_crc(root, &path)?;
        if cache.describes(&path, (digest.size, crc), &mut volume).is_ok() { files.insert(name, digest); }
    }
    let receipt = Receipt { format: 1, stem: stem.into(), files, backups: sealed, previous: None };
    super::transaction::atomic_write(&directory(root)?.join(format!("{stem}.json")), &serde_json::to_vec(&receipt)?)?;
    tracing::info!("Adopted the {stem} game data an earlier kiln-eml published (its change cache names exactly these files and backups): it now has an ownership receipt");
    Ok(Adoption::Adopted)
}

/// The publication's patched header is live: the previous generation is no longer what the game runs. Containers it
/// added that the new set does not replace and the new header does not reference are removed (`remove_unreferenced`).
pub(crate) fn committed(root: &Path, stem: &str) -> io::Result<()> {
    let Some(mut receipt) = read_receipt(root, stem)? else { return Ok(()) };
    let Some(previous) = receipt.previous.take() else { return Ok(()) };
    super::transaction::atomic_write(&receipt_path(root, stem)?, &serde_json::to_vec(&receipt)?)?;
    let dropped: BTreeMap<String, Digest> = previous.into_iter().filter(|(n, _)| !receipt.files.contains_key(n)).collect();
    remove_unreferenced(root, stem, &dropped)
}

/// `<stem>_NNN.dat`: the container's index.
fn container_index(stem: &str, name: &str) -> Option<usize> {
    if !valid_name(stem, name) { return None; }
    name.strip_prefix(&format!("{stem}_"))?.strip_suffix(".dat")?.parse().ok()
}

/// How many containers the header at `name` lists (container `i` is `<stem>_{i:03}.dat`).
fn containers(root: &Path, name: &str) -> Option<usize> {
    let path = root.join(name);
    checked_path(root, &path).ok()?;
    super::transaction::regular(&path).ok()?;
    Some(kfc::container::KFCFile::from_path(&path, false).ok()?.containers().len())
}

/// The containers (`<stem>_NNN.dat`) of the owned output that is live now, as its receipt lists them: what a restore
/// leaves behind unless it removes them. Read before the vanilla header goes back. None: no live generation is listed.
pub(crate) fn live_containers(root: &Path, stem: &str) -> io::Result<Option<BTreeMap<String, Digest>>> {
    let Some(receipt) = read_receipt(root, stem)? else { return Ok(None) };
    let header = format!("{stem}.kfc");
    let mut live = Live::new(root);
    let now = live.digest(&header)?;
    let generation = [Some(&receipt.files), receipt.previous.as_ref()].into_iter().flatten()
        .find(|g| now.is_some() && g.get(&header) == now.as_ref());
    Ok(generation.map(|g| g.iter().filter(|(n, _)| container_index(stem, n).is_some()).map(|(n, d)| (n.clone(), d.clone())).collect()))
}

/// Removes the containers in `files` that the live header does not reference, each only when it is still exactly the
/// listed file (size, then SHA-256). Never a container the live header lists (the game's own included), never a file
/// the list does not name. EML adds containers after the vanilla ones and pads their count to a power of two (an
/// empty file each), so a restore otherwise leaves the content and the padding behind.
pub(crate) fn remove_unreferenced(root: &Path, stem: &str, files: &BTreeMap<String, Digest>) -> io::Result<()> {
    if files.is_empty() { return Ok(()); }
    let Some(referenced) = containers(root, &format!("{stem}.kfc")) else { return Err(invalid("the live header cannot be read")) };
    let mut live = Live::new(root);
    let mut removed = Vec::new();
    for (name, expected) in files {
        if container_index(stem, name).is_none_or(|i| i < referenced) { continue; }
        if live.size(name)? != Some(expected.size) || live.digest(name)?.as_ref() != Some(expected) {
            if live.size(name)?.is_some() { tracing::info!(file = %name, "kept: not the container EML wrote"); }
            continue;
        }
        fs::remove_file(root.join(name))?;
        removed.push(name.as_str());
    }
    if !removed.is_empty() {
        tracing::info!("Removed {} container file(s) EML had added and the game no longer references: {}", removed.len(), removed.join(", "));
    }
    Ok(())
}

/// Hands the archives over from SF before a restore; the returned guard is held until the restore copies are done.
pub(crate) fn before_restore(root: &Path, stem: &str) -> io::Result<Option<SfGuard>> {
    check_stem(stem)?;
    let mut live = Live::new(root);
    for extension in ["kfc", "kfc_resources"] {
        let (backup, current) = (format!("{stem}.{extension}.bak"), format!("{stem}.{extension}"));
        let Some(size) = live.size(&backup)? else { continue };
        if live.size(&current)? != Some(size) || live.digest(&backup)? != live.digest(&current)? {
            return handoff(root, stem, false);
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
// `publishing`: the archives are about to get EML's output (not vanilla data back): SF's asset mods drop out of them.
fn handoff(root: &Path, stem: &str, publishing: bool) -> io::Result<Option<SfGuard>> {
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
    let mods: Vec<String> = assets["mods"].as_array().into_iter().flatten().filter_map(|m| {
        let id = m.get("id")?.as_str()?;
        Some(match m.get("version").and_then(|v| v.as_str()) { Some(v) => format!("{id} {v}"), None => id.to_owned() })
    }).collect();
    assets["status"] = "failed".into();
    assets["error"] = "Kiln's EML writer changed game data; ShroudForge must prepare its asset mods again".into();
    let replacement = serde_json::to_vec_pretty(&value)?;
    let recovery = directory(root)?.join(format!("sf-handoff-{}.json", uuid::Uuid::new_v4()));
    let journal = serde_json::json!({"format":1,"target":stem,"statePath":state,"original":String::from_utf8(original.clone()).map_err(|_| invalid("non UTF-8 SF state"))?,"replacement":String::from_utf8(replacement.clone()).unwrap()});
    super::transaction::atomic_write(&recovery, &serde_json::to_vec(&journal)?)?;
    if bounded(root, &state)? != original { return Err(invalid("ShroudForge state changed during handoff; original retained")); }
    super::transaction::atomic_write(&state, &replacement)?;
    tracing::info!(path = %state.display(), recovery = %recovery.display(), "Invalidated only ShroudForge's stale prepared-output cache before EML changed game data; settings retained");
    if publishing && !mods.is_empty() { DISPLACED.with(|d| d.borrow_mut().extend(mods)); }
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
        // the container EML wrote, outside the restored header's list, is removed with it (its receipt lists it)
        let written = restored.containers().len();
        assert!(!root.join(format!("g_{written:03}.dat")).exists());
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
    /// A real publication over a vanilla pair of `vanilla` containers (one content each), with `content` new blobs.
    /// The writer pads the container count to a power of two with empty files, as on a real client (55 + 1 -> 64).
    fn real_patch(vanilla: usize, content: &[&[u8]]) -> PathBuf {
        use std::rc::Rc;
        use kfc::{container::{KFCFile, KFCWriteOptions, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};
        let root = std::env::temp_dir().join(format!("kiln-real-containers-{}", uuid::Uuid::new_v4()));
        fs::create_dir(&root).unwrap();
        fs::File::create(root.join("g.kfc")).unwrap();
        fs::File::create(root.join("g.kfc_resources")).unwrap();
        let types = Rc::new(TypeRegistry::default());
        let mut original: crate::transaction::Writer = KFCWriter::new_with_options(&root, "g", types.clone(), "fixture",
            KFCWriteOptions { max_container_size: 1, ..Default::default() }).unwrap();
        for i in 0..vanilla { let blob = format!("vanilla {i}"); original.write_content(&ContentHash::from_data(blob.as_bytes()), blob.as_bytes()).unwrap(); }
        original.finalize().unwrap();
        let path = camino::Utf8Path::from_path(&root).unwrap();
        crate::load::create_backup(path.join("g.kfc")).unwrap();
        republish(&root, content);
        root
    }
    fn republish(root: &Path, content: &[&[u8]]) {
        use std::rc::Rc;
        use kfc::{container::KFCFile, guid::ContentHash, reflection::TypeRegistry};
        let path = camino::Utf8Path::from_path(root).unwrap();
        let types = Rc::new(TypeRegistry::default());
        let base = Rc::new(KFCFile::from_path(root.join("g.kfc.bak"), false).unwrap());
        let mut patch = crate::load::create_writer(path, "g", &types, &base).unwrap();
        for blob in content { patch.write_content(&ContentHash::from_data(blob), blob).unwrap(); }
        patch.finalize().unwrap();
        assert!(owns_output(root, "g").unwrap());
    }
    fn dats(root: &Path) -> Vec<(String, u64)> {
        let mut out: Vec<_> = fs::read_dir(root).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|n| n.starts_with("g_")).map(|n| { let len = fs::metadata(root.join(&n)).unwrap().len(); (n, len) }).collect();
        out.sort();
        out
    }
    // A restore puts back the header and the resource file. The containers EML added (content and the empty padding to
    // a power of two) are no longer referenced: exactly those the receipt lists are removed, nothing else.
    #[test]
    fn a_restore_removes_exactly_the_containers_eml_added() {
        let root = real_patch(2, &[b"new content"]);
        let vanilla_dats: Vec<_> = dats(&root).into_iter().filter(|(n, _)| n == "g_000.dat" || n == "g_001.dat").collect();
        assert_eq!(dats(&root).iter().map(|(n, l)| (n.as_str(), *l > 0)).collect::<Vec<_>>(),
            [("g_000.dat", true), ("g_001.dat", true), ("g_002.dat", true), ("g_003.dat", false)], "content and padding");
        // a player's own files with similar names, which no receipt lists
        fs::write(root.join("g_007.dat"), "player's").unwrap();
        fs::write(root.join("g_002.dat.keep"), "player's").unwrap();
        assert!(crate::restore(camino::Utf8Path::from_path(&root).unwrap(), "g"));
        let mut expected = vanilla_dats.clone();
        expected.push(("g_007.dat".into(), 8));
        assert_eq!(dats(&root).into_iter().filter(|(n, _)| n != "g_002.dat.keep").collect::<Vec<_>>(), expected);
        assert_eq!(fs::read(root.join("g_002.dat.keep")).unwrap(), b"player's");
        fs::remove_dir_all(root).unwrap();
    }
    // Even a receipt that lists a container the vanilla header references never removes it.
    #[test]
    fn a_restore_never_removes_a_container_the_vanilla_header_references() {
        let root = real_patch(2, &[b"new content"]);
        let receipt_path = root.join("kiln/eml/ownership/g.json");
        let mut receipt: serde_json::Value = serde_json::from_slice(&fs::read(&receipt_path).unwrap()).unwrap();
        receipt["files"]["g_000.dat"] = serde_json::to_value(digest(&root, &root.join("g_000.dat")).unwrap()).unwrap();
        fs::write(&receipt_path, serde_json::to_vec(&receipt).unwrap()).unwrap();
        assert!(owns_output(&root, "g").unwrap());
        let vanilla = fs::read(root.join("g_000.dat")).unwrap();
        assert!(crate::restore(camino::Utf8Path::from_path(&root).unwrap(), "g"));
        assert_eq!(fs::read(root.join("g_000.dat")).unwrap(), vanilla);
        assert!(!root.join("g_002.dat").exists());
        fs::remove_dir_all(root).unwrap();
    }
    // A publication with fewer containers than the one it replaces: the old ones the new header does not reference go.
    #[test]
    fn a_publication_removes_the_previous_containers_the_new_header_does_not_reference() {
        let root = real_patch(1, &[b"one"]);
        assert_eq!(dats(&root).len(), 2, "1 vanilla + 1 content (already a power of two)");
        fs::write(root.join("g_009.dat"), "player's").unwrap();
        republish(&root, &[]);
        assert_eq!(dats(&root).iter().map(|(n, _)| n.as_str()).collect::<Vec<_>>(), ["g_000.dat", "g_009.dat"]);
        fs::remove_dir_all(root).unwrap();
    }
    /// A game update as ShroudForge handles it: new vanilla live files (another build, one more container), and SF's
    /// fresh working backups captured from them. `keep_seal`: an installed SF (its Updater replaces both backups and
    /// leaves kiln-eml's seal); otherwise Kiln's bundled SF (2eb8f0b), which moves the old pair and the seal aside.
    /// `sf_patched`: SF's own asset patch then appends to the live resource file, as it does before kiln-eml runs.
    fn sf_game_update(root: &Path, keep_seal: bool, sf_patched: bool) -> (Vec<u8>, Vec<u8>) {
        use std::rc::Rc;
        use kfc::{container::{KFCWriteOptions, KFCWriter}, guid::ContentHash, reflection::TypeRegistry};
        for n in ["g.kfc", "g.kfc_resources", "g.kfc.bak", "g.kfc_resources.bak"] { fs::remove_file(root.join(n)).unwrap(); }
        if !keep_seal { fs::remove_file(root.join("g.kfc.bak.integrity")).unwrap(); }
        for n in dats(root) { fs::remove_file(root.join(n.0)).unwrap(); }
        fs::File::create(root.join("g.kfc")).unwrap();
        fs::File::create(root.join("g.kfc_resources")).unwrap();
        let mut update: crate::transaction::Writer = KFCWriter::new_with_options(root, "g", Rc::new(TypeRegistry::default()), "fixture 2",
            KFCWriteOptions { max_container_size: 1, ..Default::default() }).unwrap();
        for i in 0..3 { let blob = format!("updated {i}"); update.write_content(&ContentHash::from_data(blob.as_bytes()), blob.as_bytes()).unwrap(); }
        update.finalize().unwrap();
        // the new build's resource file is another size, as after a real update (the header lists none of these bytes)
        std::io::Write::write_all(&mut fs::OpenOptions::new().append(true).open(root.join("g.kfc_resources")).unwrap(), b"new build").unwrap();
        fs::copy(root.join("g.kfc"), root.join("g.kfc.bak")).unwrap();
        fs::copy(root.join("g.kfc_resources"), root.join("g.kfc_resources.bak")).unwrap();
        let vanilla = (fs::read(root.join("g.kfc")).unwrap(), fs::read(root.join("g.kfc_resources")).unwrap());
        if sf_patched { std::io::Write::write_all(&mut fs::OpenOptions::new().append(true).open(root.join("g.kfc_resources")).unwrap(), b"SF asset patch").unwrap(); }
        vanilla
    }
    // After a game update SF replaced the working backups. kiln-eml must not take the new pair for its own output,
    // must never put the old game's bytes back, and must not refuse every start: it accepts the pair by its own check
    // (the live resource prefix), seals it and patches from it.
    #[test]
    fn backups_replaced_by_shroudforge_after_a_game_update_are_taken_up_not_refused() {
        for (keep_seal, sf_patched) in [(false, false), (false, true), (true, false), (true, true)] {
            let what = format!("seal {}, SF output {}", if keep_seal { "left behind" } else { "moved" }, if sf_patched { "live" } else { "absent" });
            let root = real_patch(2, &[b"old game content"]);
            let (header, resources) = sf_game_update(&root, keep_seal, sf_patched);
            assert!(!owns_output(&root, "g").unwrap(), "{what}: the new pair is not EML's output");
            let path = camino::Utf8Path::from_path(&root).unwrap();
            assert!(crate::load::create_backup(path.join("g.kfc")).is_ok(), "{what}: taken up, not refused");
            assert!(crate::load::validate_backup(&path.join("g.kfc"), &path.join("g.kfc.bak")), "{what}: sealed");
            assert_eq!(fs::read(root.join("g.kfc.bak")).unwrap(), header, "{what}: SF's new backup kept as it is");
            republish(&root, &[b"new game content"]);
            assert!(crate::restore(path, "g"), "{what}");
            assert_eq!(fs::read(root.join("g.kfc")).unwrap(), header, "{what}: the new game's vanilla header, never the old one");
            assert!(fs::read(root.join("g.kfc_resources")).unwrap().starts_with(&resources), "{what}");
            fs::remove_dir_all(root).unwrap();
        }
    }
    // ShroudForge (112d939) writes its backup copies to `<backup>.partial` first; one can stay after a killed start.
    // They are SF's and never a backup to kiln-eml: not read, sealed, restored from, compared or removed.
    #[test]
    fn shroudforge_partial_backup_copies_are_never_backups_and_never_removed() {
        let root = real_patch(2, &[b"content"]);
        for n in ["g.kfc.bak.partial", "g.kfc_resources.bak.partial"] { fs::write(root.join(n), "half an SF copy").unwrap(); }
        let seal = fs::read(root.join("g.kfc.bak.integrity")).unwrap();
        let path = camino::Utf8Path::from_path(&root).unwrap();
        assert!(owns_output(&root, "g").unwrap());
        assert!(crate::load::create_backup(path.join("g.kfc")).is_ok());
        assert_eq!(fs::read(root.join("g.kfc.bak.integrity")).unwrap(), seal, "the seal still describes the real pair");
        prune(&root, "g");
        assert!(crate::restore(path, "g"));
        for n in ["g.kfc.bak.partial", "g.kfc_resources.bak.partial"] {
            assert_eq!(fs::read(root.join(n)).unwrap(), b"half an SF copy", "{n} is SF's and stays");
        }
        assert_ne!(fs::read(root.join("g.kfc")).unwrap(), b"half an SF copy");
        fs::remove_dir_all(root).unwrap();
    }
    // A damaged backup at the same game build (same length, other bytes) under its seal is still refused.
    #[test]
    fn a_damaged_backup_under_its_seal_is_still_refused() {
        let root = real_patch(2, &[]);
        let path = camino::Utf8Path::from_path(&root).unwrap();
        let mut bytes = fs::read(root.join("g.kfc.bak")).unwrap();
        let last = bytes.len() - 1;
        bytes[last] ^= 1;
        fs::write(root.join("g.kfc.bak"), &bytes).unwrap();
        assert!(crate::load::create_backup(path.join("g.kfc")).is_err());
        assert_eq!(fs::read(root.join("g.kfc.bak")).unwrap(), bytes);
        fs::remove_dir_all(root).unwrap();
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
    // Another writer's output (another size than the receipt lists) is rejected from metadata: no backup and no live
    // file is read. On an idle start that is every start while SF's output is live.
    #[test]
    fn another_writers_output_is_rejected_without_reading_anything() {
        let (root, files) = fixture();
        prepare(&root, "g", &files).unwrap();
        publish(&root, &files);
        assert!(owns_output(&root, "g").unwrap());
        fs::write(root.join("g.kfc"), "a header of another length").unwrap();
        reset_read();
        assert!(!owns_output(&root, "g").unwrap());
        assert_eq!(read(), 0, "rejected by size alone");
        fs::remove_dir_all(root).unwrap();
    }
    // A run killed half way leaves its private stage (a full copy of the resource file), temp copies and, one per
    // handoff, SF journals. The next run (under the install lock) removes exactly those, by their exact shape.
    #[test]
    fn leftovers_of_a_killed_run_are_pruned_and_nothing_else() {
        let root = std::env::temp_dir().join(format!("kiln-leftovers-{}", uuid::Uuid::new_v4()));
        let id = || uuid::Uuid::new_v4().to_string();
        let stale = root.join(format!(".kiln-eml-stage-{}", id()));
        fs::create_dir_all(&stale).unwrap();
        for n in ["g.kfc", "g.kfc_resources", "g_002.dat", ".vanilla-header"] { fs::write(stale.join(n), "staged").unwrap(); }
        fs::write(stale.join(format!("g.kiln-{}", id())), "temp").unwrap();
        let foreign = root.join(format!(".kiln-eml-stage-{}", id()));
        fs::create_dir_all(&foreign).unwrap();
        fs::write(foreign.join("notes.txt"), "a player's").unwrap();
        fs::create_dir_all(root.join(".kiln-eml-stage-mine")).unwrap();
        let temp = root.join(format!("g.kiln-{}", id()));
        fs::write(&temp, "temp").unwrap();
        let backup_temp = root.join(format!("g.kfc.kiln-{}", id()));
        fs::write(&backup_temp, "temp").unwrap();
        for n in ["g.kfc", "g.kiln-backup", "other.kiln-0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"] { fs::write(root.join(n), "keep").unwrap(); }
        fs::create_dir_all(root.join("kiln/eml/ownership")).unwrap();
        for i in 0..20 {
            let journal = root.join(format!("kiln/eml/ownership/sf-handoff-{}.json", id()));
            fs::write(&journal, format!("{i}")).unwrap();
            fs::File::options().write(true).open(&journal).unwrap()
                .set_modified(std::time::SystemTime::UNIX_EPOCH + std::time::Duration::from_secs(1_000_000 + i)).unwrap();
        }
        fs::write(root.join(format!("kiln/eml/ownership/g.kiln-{}", id())), "receipt temp").unwrap();
        prune(&root, "g");
        assert!(!stale.exists() && !temp.exists() && !backup_temp.exists());
        assert!(foreign.join("notes.txt").exists() && root.join(".kiln-eml-stage-mine").is_dir());
        for n in ["g.kfc", "g.kiln-backup", "other.kiln-0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"] { assert!(root.join(n).exists(), "{n}"); }
        let mut kept: Vec<String> = fs::read_dir(root.join("kiln/eml/ownership")).unwrap().flatten()
            .map(|e| fs::read_to_string(e.path()).unwrap()).collect();
        kept.sort_by_key(|k| k.parse::<u32>().unwrap());
        assert_eq!(kept, (4..20).map(|i| i.to_string()).collect::<Vec<_>>(), "the 16 newest journals stay, the receipt temp goes");
        fs::remove_dir_all(root).unwrap();
    }
    // The stage a real writer leaves when the process dies before publication (no Drop runs).
    #[test]
    fn the_stage_of_a_run_killed_before_publication_is_pruned() {
        use std::rc::Rc;
        use kfc::{container::KFCFile, guid::ContentHash, reflection::TypeRegistry};
        let root = real_patch(2, &[]);
        let live = dats(&root);
        let path = camino::Utf8Path::from_path(&root).unwrap();
        let base = Rc::new(KFCFile::from_path(root.join("g.kfc.bak"), false).unwrap());
        let mut patch = crate::load::create_writer(path, "g", &Rc::new(TypeRegistry::default()), &base).unwrap();
        patch.write_content(&ContentHash::from_data(b"half way"), b"half way").unwrap();
        let stage = patch.stage.clone();
        // a killed process holds no handles any more: close the writer's files, then skip the stage's own clean-up
        drop(patch.writer.take());
        std::mem::forget(patch);
        assert!(stage.is_dir() && fs::read_dir(&stage).unwrap().count() >= 3);
        prune(&root, "g");
        assert!(!stage.exists(), "the stage a killed run left is removed");
        assert_eq!(dats(&root), live, "the live game files are untouched");
        assert!(owns_output(&root, "g").unwrap());
        fs::remove_dir_all(root).unwrap();
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
        let guard = handoff(&root, "g", true).unwrap();
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
    // Under Wine, a link made on the Linux side (`ln -s`) is followed by handle-based queries and shows as a plain
    // folder; only the by-name attribute query exposes it (discover.rs). The harness (out of tree) makes such links in
    // a fixture: <fixture>/game with kiln/eml/ownership -> ../../../victim and shroudforge -> ../sf-elsewhere.
    #[cfg(windows)]
    #[test]
    #[ignore = "needs KILN_EML_HOST_LINK_FIXTURE: a game folder with links made on the Linux side (Wine harness)"]
    fn links_made_on_the_linux_side_are_refused_under_wine() {
        let Some(fixture) = std::env::var_os("KILN_EML_HOST_LINK_FIXTURE") else { return };
        let game = PathBuf::from(fixture).join("game");
        assert!(fs::symlink_metadata(game.join("kiln/eml/ownership")).unwrap().is_dir(), "the harness made the link");
        assert!(checked_path(&game, &game.join("kiln/eml/ownership/g.json")).is_err(), "linked receipt folder");
        assert!(checked_path(&game, &game.join("shroudforge/state.json")).is_err(), "linked SF folder");
        assert!(directory(&game).is_err());
        assert!(checked_path(&game, &game.join("g.kfc")).is_ok(), "the game folder itself is fine");
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
