//! kdh1 and kms1 are stable formats (docs/kiln/launcher/contracts.md); schema/fixtures/dirhash pins kdh1.
//!
//! kdh1, Kiln's directory hash:
//! 1. Walk the folder. Skip every entry whose name starts with "." (with everything below it). The root may be a
//!    symlink; nothing below it may be one (error), and every entry must be a regular file or a folder (error).
//! 2. A file's relative path: components joined with "/", each NFC-normalized, UTF-8. Non-Unicode names and two
//!    paths equal after NFC are errors.
//! 3. Sort by the UTF-8 bytes.
//! 4. `h = sha256( for each file: path "\0" size-decimal "\0" sha256hex(content) "\n" )`; result `"kdh1:" + hex(h)`.
//!
//! A mod's identity is the kdh1 of the files that are the mod ([`mod_identity`]). A Kiln mod (a folder with a regular
//! file `kiln.mod.json` directly in it) does not count its cook outputs, the folder `assets/cooked` directly in it: the
//! cook makes them on each machine from that machine's game, so a server's and a player's differ. Names are compared
//! as kdh1 writes them (NFC, no case folding). Every other folder's identity is its kdh1. The library, a profile's
//! entries, a modpack and what a server advertises all name a Kiln mod by its identity ([`of_mod`]).
//!
//! kms1, the mod-set fingerprint of a profile: `sha256( for each enabled entry sorted by (kind, id):
//! kind "\0" id "\0" version "\0" hash "\n" )` as `"kms1:" + hex`.

use crate::{Error, ErrorCode, Result};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::Read;
use std::path::Path;
use unicode_normalization::UnicodeNormalization;

pub const KDH1_PREFIX: &str = "kdh1:";
pub const KMS1_PREFIX: &str = "kms1:";
/// The manifest that makes a folder a Kiln mod.
pub const MOD_MANIFEST: &str = "kiln.mod.json";
/// A Kiln mod's cook outputs, as a kdh1 path: made on each machine from that machine's game, never part of the mod.
pub const COOKED: &str = "assets/cooked";

pub fn hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(bytes.len() * 2);
    for b in bytes {
        s.push(DIGITS[(b >> 4) as usize] as char);
        s.push(DIGITS[(b & 15) as usize] as char);
    }
    s
}

pub fn sha256_hex(data: &[u8]) -> String {
    hex(Sha256::digest(data).as_slice())
}

/// sha256 of a file, streamed. Returns (hex, size).
pub fn sha256_file(path: &Path) -> Result<(String, u64)> {
    let mut f = fs::File::open(path)?;
    let mut h = Sha256::new();
    let mut buf = vec![0u8; 1 << 16];
    let mut size = 0u64;
    loop {
        let n = f.read(&mut buf)?;
        if n == 0 {
            break;
        }
        size += n as u64;
        h.update(&buf[..n]);
    }
    Ok((hex(h.finalize().as_slice()), size))
}

/// The 8 hex chars after "kdh1:" (or of a bare sha256): library folder names `<version>+<hash8>`.
pub fn hash8(hash: &str) -> Option<&str> {
    let hex = hash.strip_prefix(KDH1_PREFIX).unwrap_or(hash);
    (hex.len() == 64 && hex.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())).then(|| &hex[..8])
}

/// One file of a kdh1 hash: relative path (any normalization, '/' separated), size, sha256 hex.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Record {
    pub path: String,
    pub size: u64,
    pub sha256: String,
}

fn bad(message: String) -> Error {
    Error::new(ErrorCode::Refused, message)
}

/// The kdh1 key of a path (components NFC, joined with "/"); None for a path kdh1 skips (a dot entry, or empty).
pub fn key_of(path: &str) -> Option<String> {
    let parts: Vec<&str> = path.split('/').filter(|p| !p.is_empty()).collect();
    if parts.is_empty() || parts.iter().any(|p| p.starts_with('.')) {
        return None;
    }
    Some(parts.iter().map(|p| p.nfc().collect::<String>()).collect::<Vec<_>>().join("/"))
}

/// Whether a kdh1 key names a file below a Kiln mod's cook outputs folder.
pub fn is_cooked_key(key: &str) -> bool {
    key.strip_prefix(COOKED).is_some_and(|rest| rest.starts_with('/'))
}

fn keyed(records: impl IntoIterator<Item = Record>) -> Vec<(Vec<u8>, Record)> {
    records.into_iter().filter_map(|r| key_of(&r.path).map(|k| (k.into_bytes(), r))).collect()
}

/// kdh1 from records (also for a zip's entries): applies the dot-skip, NFC, the duplicate check and the sort.
pub fn dirhash_records(records: impl IntoIterator<Item = Record>) -> Result<String> {
    hash_keyed(keyed(records))
}

/// A mod's identity from records (a zip's entries): kdh1, without the files below `assets/cooked/` when the records
/// hold a `kiln.mod.json` at the top (a Kiln mod).
pub fn mod_identity_records(records: impl IntoIterator<Item = Record>) -> Result<String> {
    let mut items = keyed(records);
    if items.iter().any(|(k, _)| k == MOD_MANIFEST.as_bytes()) {
        items.retain(|(k, _)| !std::str::from_utf8(k).is_ok_and(is_cooked_key));
    }
    hash_keyed(items)
}

fn hash_keyed(mut items: Vec<(Vec<u8>, Record)>) -> Result<String> {
    items.sort_by(|a, b| a.0.cmp(&b.0));
    if let Some(w) = items.windows(2).find(|w| w[0].0 == w[1].0) {
        return Err(bad(format!("two files are the same path after NFC normalization: {}", String::from_utf8_lossy(&w[0].0))));
    }
    let mut h = Sha256::new();
    for (key, r) in &items {
        h.update(key);
        h.update(b"\0");
        h.update(r.size.to_string().as_bytes());
        h.update(b"\0");
        h.update(r.sha256.as_bytes());
        h.update(b"\n");
    }
    Ok(format!("{KDH1_PREFIX}{}", hex(h.finalize().as_slice())))
}

/// kdh1 of a folder on disk.
pub fn dirhash(root: &Path) -> Result<String> {
    dirhash_records(dir_records(root)?)
}

/// A mod's identity: the kdh1 of the files that are the mod. A Kiln mod's `assets/cooked` folder is not walked (its
/// files are not read, and a link inside it is not looked at; a link named `assets/cooked` is refused like any other).
pub fn mod_identity(root: &Path) -> Result<String> {
    dirhash_records(mod_records(root)?)
}

/// The hash a mod folder of `kind` is known by: a Kiln mod's identity, the plain kdh1 of any other kind's folder
/// (another loader's files are all its own). Plugins are single DLLs with a sha256 and never come here.
pub fn of_mod(kind: crate::manifest::ModKind, root: &Path) -> Result<String> {
    if kind == crate::manifest::ModKind::Kiln { mod_identity(root) } else { dirhash(root) }
}

/// [`of_mod`] for a zip's entries of one mod.
pub fn of_mod_records(kind: crate::manifest::ModKind, records: impl IntoIterator<Item = Record>) -> Result<String> {
    if kind == crate::manifest::ModKind::Kiln { mod_identity_records(records) } else { dirhash_records(records) }
}

/// A regular file named `kiln.mod.json` (exactly, as the folder lists it) directly in the folder.
pub fn is_kiln_mod(root: &Path) -> bool {
    fs::read_dir(root).into_iter().flatten().flatten().any(|e| {
        e.file_name().to_str().is_some_and(|n| n.nfc().collect::<String>() == MOD_MANIFEST)
            && fs::symlink_metadata(e.path()).is_ok_and(|m| m.file_type().is_file() && !is_reparse_point(&m))
    })
}

/// The files kdh1 counts in a folder on disk (dot entries skipped), with their sizes and hashes.
pub(crate) fn dir_records(root: &Path) -> Result<Vec<Record>> {
    records_of(root, false)
}

/// The files a mod's identity counts: [`dir_records`] without a Kiln mod's cook outputs.
pub(crate) fn mod_records(root: &Path) -> Result<Vec<Record>> {
    records_of(root, true)
}

fn records_of(root: &Path, mod_files: bool) -> Result<Vec<Record>> {
    if !fs::metadata(root)?.is_dir() {
        return Err(Error::invalid(format!("{}: not a folder", root.display())));
    }
    let mut records = Vec::new();
    walk(root, "", &mut records, mod_files && is_kiln_mod(root))?;
    Ok(records)
}

fn walk(dir: &Path, prefix: &str, out: &mut Vec<Record>, skip_cooked: bool) -> Result<()> {
    for entry in fs::read_dir(dir)? {
        let entry = entry?;
        let name = entry.file_name();
        let Some(name) = name.to_str() else {
            return Err(bad(format!("{}: name is not valid Unicode", entry.path().display())));
        };
        if name.starts_with('.') {
            continue;
        }
        let meta = fs::symlink_metadata(entry.path())?;
        let rel = if prefix.is_empty() { name.to_string() } else { format!("{prefix}/{name}") };
        let ft = meta.file_type();
        if ft.is_symlink() || is_reparse_point(&meta) {
            return Err(bad(format!("{}: symlinks are not allowed in a mod folder", entry.path().display())));
        } else if ft.is_dir() {
            if skip_cooked && key_of(&rel).as_deref() == Some(COOKED) {
                continue;
            }
            walk(&entry.path(), &rel, out, skip_cooked)?;
        } else if ft.is_file() {
            let (sha256, size) = sha256_file(&entry.path())?;
            out.push(Record { path: rel, size, sha256 });
        } else {
            return Err(bad(format!("{}: not a regular file or folder", entry.path().display())));
        }
    }
    Ok(())
}

#[cfg(windows)]
fn is_reparse_point(meta: &fs::Metadata) -> bool {
    use std::os::windows::fs::MetadataExt;
    meta.file_attributes() & 0x400 != 0 // FILE_ATTRIBUTE_REPARSE_POINT
}

#[cfg(not(windows))]
fn is_reparse_point(_meta: &fs::Metadata) -> bool {
    false
}

/// One profile entry of a kms1 fingerprint.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FingerprintEntry {
    pub kind: String,
    pub id: String,
    pub version: String,
    pub hash: String,
}

/// kms1 over the enabled entries of a profile (sorted by kind, then id, bytewise).
pub fn fingerprint(entries: impl IntoIterator<Item = FingerprintEntry>) -> String {
    let mut v: Vec<FingerprintEntry> = entries.into_iter().collect();
    v.sort_by(|a, b| (a.kind.as_bytes(), a.id.as_bytes()).cmp(&(b.kind.as_bytes(), b.id.as_bytes())));
    let mut h = Sha256::new();
    for e in &v {
        for (i, part) in [&e.kind, &e.id, &e.version, &e.hash].into_iter().enumerate() {
            if i > 0 {
                h.update(b"\0");
            }
            h.update(part.as_bytes());
        }
        h.update(b"\n");
    }
    format!("{KMS1_PREFIX}{}", hex(h.finalize().as_slice()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn empty_folder_hashes_the_empty_string() {
        assert_eq!(dirhash_records([]).unwrap(), "kdh1:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    #[test]
    fn records_are_sorted_normalized_and_dot_entries_skipped() {
        let r = |p: &str| Record { path: p.into(), size: 1, sha256: "00".into() };
        let a = dirhash_records([r("b"), r("a/x"), r(".git/HEAD"), r("cafe\u{301}")]).unwrap();
        let b = dirhash_records([r("caf\u{e9}"), r("a/x"), r("b")]).unwrap();
        assert_eq!(a, b);
        assert!(dirhash_records([r("caf\u{e9}"), r("cafe\u{301}")]).is_err());
    }

    #[test]
    fn a_kiln_mods_records_leave_out_its_cook_outputs() {
        let r = |p: &str| Record { path: p.into(), size: 1, sha256: "00".into() };
        let shipped = [r("kiln.mod.json"), r("assets/assets.json"), r("Mod.dll")];
        let cooked = [r("kiln.mod.json"), r("assets/assets.json"), r("Mod.dll"), r("assets/cooked/a.bin"), r("assets/cooked/impact/b.bin")];
        assert_eq!(mod_identity_records(cooked.clone()).unwrap(), dirhash_records(shipped.clone()).unwrap());
        assert_ne!(dirhash_records(cooked.clone()).unwrap(), dirhash_records(shipped).unwrap());
        // no kiln.mod.json at the top: every file counts; so do the names elsewhere and in another case
        let other = [r("mod.json"), r("assets/cooked/a.bin"), r("inner/kiln.mod.json")];
        assert_eq!(mod_identity_records(other.clone()).unwrap(), dirhash_records(other).unwrap());
        let names = [r("kiln.mod.json"), r("assets/cooked"), r("assets/cooked.txt"), r("Assets/Cooked/a.bin"), r("data/assets/cooked/a.bin")];
        assert_eq!(mod_identity_records(names.clone()).unwrap(), dirhash_records(names).unwrap());
        assert!(is_cooked_key("assets/cooked/a") && !is_cooked_key("assets/cooked") && !is_cooked_key("assets/cookedx/a"));
        // a kind that is not a Kiln mod keeps plain kdh1 even with that manifest in it
        assert_eq!(of_mod_records(crate::manifest::ModKind::Eml, cooked.clone()).unwrap(), dirhash_records(cooked.clone()).unwrap());
        assert_eq!(of_mod_records(crate::manifest::ModKind::Kiln, cooked.clone()).unwrap(), mod_identity_records(cooked).unwrap());
    }

    #[test]
    fn hash8_and_fingerprint() {
        assert_eq!(hash8("kdh1:3f2a9c1d00112233445566778899aabbccddeeff00112233445566778899aabb"), Some("3f2a9c1d"));
        assert_eq!(hash8("kdh1:XYZ"), None);
        let e = |k: &str, i: &str| FingerprintEntry { kind: k.into(), id: i.into(), version: "1.0.0".into(), hash: "h".into() };
        assert_eq!(fingerprint([e("native", "b"), e("kiln", "a")]), fingerprint([e("kiln", "a"), e("native", "b")]));
        assert_eq!(fingerprint([]), format!("kms1:{}", sha256_hex(b"")));
    }
}
