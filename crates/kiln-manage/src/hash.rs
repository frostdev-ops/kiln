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

/// kdh1 from records (also for a zip's entries): applies the dot-skip, NFC, the duplicate check and the sort.
pub fn dirhash_records(records: impl IntoIterator<Item = Record>) -> Result<String> {
    let mut items: Vec<(Vec<u8>, Record)> = Vec::new();
    for r in records {
        let parts: Vec<&str> = r.path.split('/').filter(|p| !p.is_empty()).collect();
        if parts.is_empty() || parts.iter().any(|p| p.starts_with('.')) {
            continue;
        }
        let key: String = parts.iter().map(|p| p.nfc().collect::<String>()).collect::<Vec<_>>().join("/");
        items.push((key.into_bytes(), r));
    }
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
    if !fs::metadata(root)?.is_dir() {
        return Err(Error::invalid(format!("{}: not a folder", root.display())));
    }
    let mut records = Vec::new();
    walk(root, "", &mut records)?;
    dirhash_records(records)
}

fn walk(dir: &Path, prefix: &str, out: &mut Vec<Record>) -> Result<()> {
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
            walk(&entry.path(), &rel, out)?;
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
    fn hash8_and_fingerprint() {
        assert_eq!(hash8("kdh1:3f2a9c1d00112233445566778899aabbccddeeff00112233445566778899aabb"), Some("3f2a9c1d"));
        assert_eq!(hash8("kdh1:XYZ"), None);
        let e = |k: &str, i: &str| FingerprintEntry { kind: k.into(), id: i.into(), version: "1.0.0".into(), hash: "h".into() };
        assert_eq!(fingerprint([e("native", "b"), e("kiln", "a")]), fingerprint([e("kiln", "a"), e("native", "b")]));
        assert_eq!(fingerprint([]), format!("kms1:{}", sha256_hex(b"")));
    }
}
