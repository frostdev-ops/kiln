//! The audit of one mod zip against the artifact policy ([`super::policy`]) before it is frozen for publishing.
//!
//! Nothing is extracted. Every member is read once as a stream: its real size is counted (declared sizes are not
//! trusted), hashed, scanned for a private-key header and for the game's archive magic. The zip must hold exactly one
//! mod under `<id>/` with its manifest at `<id>/kiln.mod.json` or `<id>/native.json`; a zip with the manifest at its
//! top level is turned into that layout by [`normalize_root`] first.
//!
//! Cooked output has no receipt or provenance record in this tree: the audit therefore refuses every member under
//! `assets/cooked/`, under any folder named `cooked`, and under the `out` folder of any cook recipe in the zip
//! (`cook.json`). A cooked file placed elsewhere under another name is not recognised; that is why the plan is
//! reviewed by a person.

use super::policy::{self, PackageFile, PemScan, Purpose, Refusal};
use crate::manifest::{self, Side};
use crate::{Error, ErrorCode, Result};
use sha2::Digest;
use std::collections::{HashMap, HashSet};
use std::io::Read;
use std::path::Path;

/// What the plan expects the zip to be, and what the config declares.
#[derive(Debug, Clone, Default)]
pub struct AuditInput<'a> {
    pub expect_id: Option<&'a str>,
    pub expect_version: Option<&'a str>,
    pub package_files: &'a [PackageFile],
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModKind {
    Kiln,
    Native,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Member {
    /// Normalized name in the zip (`<id>/...`).
    pub name: String,
    pub bytes: u64,
    pub sha256: String,
}

/// A zip that passed.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AuditReport {
    pub policy_version: u32,
    pub kind: ModKind,
    pub id: String,
    /// As the manifest writes it.
    pub version: String,
    pub side: Side,
    /// The manifest's `apiVersion` (a Kiln mod), or the native manifest's `kiln` range, else `*`.
    pub api_version: String,
    pub zip_bytes: u64,
    pub zip_sha256: String,
    pub expanded_bytes: u64,
    /// Files only, sorted by name.
    pub members: Vec<Member>,
}

/// The ratio rule looks at members above this size only: a few kilobytes of repetitive JSON compress far better than
/// 100:1 and are no bomb.
const RATIO_FLOOR: u64 = 64 * 1024;
const CAPTURE_MAX: usize = 1024 * 1024;

fn refuse<T>(reason: &'static str, member: Option<&str>, message: impl Into<String>) -> Result<T> {
    Err(Refusal::new(reason, member, message).into())
}

fn unreadable(e: impl std::fmt::Display) -> Error {
    Refusal::new("zip-unreadable", None, format!("it is not a zip Kiln can read ({e})")).into()
}

struct Entry {
    index: usize,
    name: String,
    is_dir: bool,
    compressed: u64,
}

fn list(a: &mut zip::ZipArchive<std::fs::File>) -> Result<Vec<Entry>> {
    if a.len() > policy::MAX_MEMBERS {
        return refuse("too-many-members", None, format!("it has more than {} members", policy::MAX_MEMBERS));
    }
    let mut out = Vec::with_capacity(a.len());
    let mut exact: HashSet<String> = HashSet::new();
    let mut folded: HashMap<String, String> = HashMap::new();
    for i in 0..a.len() {
        let e = a.by_index_raw(i).map_err(unreadable)?;
        let name = policy::normalize_name(e.name_raw())?;
        if e.encrypted() {
            return refuse("encrypted", Some(&name), "it is encrypted");
        }
        // Unix file types other than a regular file or a folder: links, devices, sockets, pipes.
        let kind = e.unix_mode().map(|m| m & 0o170000).unwrap_or(0);
        if e.is_symlink() || !matches!(kind, 0 | 0o100000 | 0o040000) {
            return refuse("link", Some(&name), "it is a link or a device, not a file");
        }
        if !exact.insert(name.clone()) {
            return refuse("duplicate-name", Some(&name), "the zip holds this name twice");
        }
        let lower = name.to_lowercase();
        if let Some(other) = folded.insert(lower, name.clone()) {
            return refuse("case-collision", Some(&name), format!("it differs from {other} only by letter case"));
        }
        out.push(Entry { index: i, name, is_dir: e.is_dir(), compressed: e.compressed_size() });
    }
    // a file may not also be a folder of another member ("a" and "a/b")
    for e in &out {
        let mut at = e.name.as_str();
        while let Some((parent, _)) = at.rsplit_once('/') {
            if out.iter().any(|o| !o.is_dir && o.name == parent) {
                return refuse("duplicate-name", Some(&e.name), format!("{parent} is both a file and a folder"));
            }
            at = parent;
        }
    }
    Ok(out)
}

fn is_manifest_name(base: &str) -> bool {
    base == manifest::KILN_MANIFEST || base == manifest::NATIVE_MANIFEST
}

/// The single root folder and the manifest's name, or the layout refusal.
fn layout(entries: &[Entry]) -> Result<(String, &'static str)> {
    let files: Vec<&Entry> = entries.iter().filter(|e| !e.is_dir).collect();
    if files.is_empty() {
        return refuse("layout", None, "it is empty");
    }
    if let Some(top) = files.iter().find(|e| !e.name.contains('/')) {
        return if is_manifest_name(&top.name) {
            refuse("layout-root-manifest", Some(&top.name), "the manifest is at the top of the zip; a mod zip holds one folder named after the mod id")
        } else {
            refuse("layout", Some(&top.name), "it is outside the mod's folder; a mod zip holds one folder named after the mod id")
        };
    }
    let root = files[0].name.split('/').next().unwrap_or("").to_string();
    if let Some(other) = entries.iter().find(|e| e.name.split('/').next() != Some(root.as_str())) {
        return refuse("layout", Some(&other.name), format!("the zip holds more than the one folder {root} (modpacks and several mods in one zip are not published this way)"));
    }
    if !manifest::is_mod_id(&root) || root.ends_with('\n') {
        return refuse("layout", Some(&root), "the folder's name is not a mod id");
    }
    let has = |name: &str| files.iter().any(|e| e.name == format!("{root}/{name}"));
    let found = match (has(manifest::KILN_MANIFEST), has(manifest::NATIVE_MANIFEST)) {
        (true, false) => manifest::KILN_MANIFEST,
        (false, true) => manifest::NATIVE_MANIFEST,
        (true, true) => return refuse("manifest-multiple", Some(&root), "it has both kiln.mod.json and native.json"),
        (false, false) => return refuse("manifest-missing", Some(&root), "it has neither kiln.mod.json nor native.json"),
    };
    // another mod further down
    if let Some(deeper) = files.iter().find(|e| e.name.matches('/').count() > 1 && e.name.ends_with(&format!("/{}", manifest::KILN_MANIFEST))) {
        return refuse("manifest-multiple", Some(&deeper.name), "a second mod manifest");
    }
    Ok((root, found))
}

struct Read1 {
    bytes: u64,
    sha256: String,
    pem: bool,
    kfc: bool,
    captured: Option<Vec<u8>>,
}

fn read_member(a: &mut zip::ZipArchive<std::fs::File>, e: &Entry, total: &mut u64, capture: bool) -> Result<Read1> {
    let mut f = a.by_index(e.index).map_err(unreadable)?;
    let declared = f.size();
    let mut hasher = sha2::Sha256::new();
    let mut pem = PemScan::default();
    let mut head: Vec<u8> = Vec::with_capacity(4); // the member's first four bytes, whatever the chunking
    let mut captured = capture.then(Vec::new);
    let mut bytes = 0u64;
    let mut buf = vec![0u8; 64 * 1024];
    loop {
        let n = f.read(&mut buf).map_err(|err| Error::from(Refusal::new("zip-unreadable", Some(&e.name), format!("it cannot be read ({err})"))))?;
        if n == 0 {
            break;
        }
        bytes = bytes.checked_add(n as u64).ok_or_else(|| Error::from(Refusal::new("member-too-large", Some(&e.name), "it is too large")))?;
        if bytes > policy::MAX_MEMBER_BYTES {
            return refuse("member-too-large", Some(&e.name), format!("it expands to more than {} MiB", policy::MAX_MEMBER_BYTES >> 20));
        }
        *total = total.checked_add(n as u64).ok_or_else(|| Error::from(Refusal::new("expanded-too-large", None, "it expands too far")))?;
        if *total > policy::MAX_EXPANDED_BYTES {
            return refuse("expanded-too-large", None, format!("it expands to more than {} MiB", policy::MAX_EXPANDED_BYTES >> 20));
        }
        if bytes > RATIO_FLOOR && bytes / policy::MAX_RATIO > e.compressed.max(1) {
            return refuse("ratio", Some(&e.name), format!("it expands more than {} times", policy::MAX_RATIO));
        }
        hasher.update(&buf[..n]);
        pem.feed(&buf[..n]);
        if head.len() < 4 {
            head.extend_from_slice(&buf[..n.min(4 - head.len())]);
        }
        if let Some(c) = captured.as_mut() {
            if c.len() + n > CAPTURE_MAX {
                return refuse("member-too-large", Some(&e.name), "this file is read by the audit and is larger than 1 MiB");
            }
            c.extend_from_slice(&buf[..n]);
        }
    }
    if bytes != declared {
        return refuse("size-mismatch", Some(&e.name), "its real size differs from the size the zip states");
    }
    Ok(Read1 { bytes, sha256: crate::hash::hex(&hasher.finalize()), pem: pem.found, kfc: head == b"KFC4", captured })
}

/// Joins a recipe's `out` to the folder of its cook.json, resolving `..`. None when it leaves the mod.
fn resolve_out(cook_dir: &str, out: &str) -> Option<String> {
    let mut parts: Vec<String> = cook_dir.split('/').filter(|p| !p.is_empty()).map(str::to_string).collect();
    for p in out.replace('\\', "/").split('/') {
        match p {
            "" | "." => {}
            ".." => {
                parts.pop()?;
            }
            p => parts.push(p.to_string()),
        }
    }
    Some(parts.join("/").to_lowercase())
}

fn outs_of(v: &serde_json::Value, found: &mut Vec<String>) {
    match v {
        serde_json::Value::Object(o) => {
            for (k, child) in o {
                if k == "out" {
                    if let Some(s) = child.as_str() {
                        found.push(s.to_string());
                    }
                }
                outs_of(child, found);
            }
        }
        serde_json::Value::Array(a) => a.iter().for_each(|c| outs_of(c, found)),
        _ => {}
    }
}

/// A `key = value` or `key: value` line of a non-JSON config whose key names a secret and whose value is not empty.
fn secret_line(text: &str) -> Option<String> {
    text.lines().find_map(|line| {
        let line = line.trim();
        if line.starts_with(['#', ';', '/']) {
            return None;
        }
        let (k, v) = line.split_once(['=', ':'])?;
        let v = v.trim().trim_matches(['"', '\'', ',']);
        (policy::is_secret_key(k.trim().trim_matches(['"', '\''])) && !v.is_empty() && v != "null").then(|| k.trim().to_string())
    })
}

/// Audits `zip`. An `Err` made from a [`Refusal`] carries its reason in `detail.reason` ([`policy::reason_of`]).
pub fn audit(zip: &Path, input: &AuditInput) -> Result<AuditReport> {
    let (zip_sha256, zip_bytes) = crate::hash::sha256_file(zip)?;
    if zip_bytes > policy::MAX_ZIP_BYTES {
        return refuse("too-large", None, format!("it is larger than {} MiB", policy::MAX_ZIP_BYTES >> 20));
    }
    let f = std::fs::File::open(zip).map_err(crate::util::io_at(zip, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(unreadable)?;
    let entries = list(&mut a)?;
    let (root, manifest_name) = layout(&entries)?;
    let prefix = format!("{root}/");

    let declared: HashMap<String, &PackageFile> = input.package_files.iter().map(|p| (p.path.to_lowercase(), p)).collect();
    for p in input.package_files {
        let lower = p.path.to_lowercase();
        if policy::normalize_name(p.path.as_bytes()).is_err() || !crate::release_binding::is_sha256_hex(&p.sha256) {
            return refuse("package-file-invalid", Some(&p.path), "this packageFiles entry has a bad path or sha256");
        }
        match p.purpose {
            Purpose::DefaultConfig if !lower.starts_with("config/defaults/") => {
                return refuse("package-file-invalid", Some(&p.path), "a default-config entry must be under config/defaults/")
            }
            Purpose::Dependency if !lower.ends_with(".dll") || p.license.as_deref().is_none_or(|l| l.trim().is_empty()) => {
                return refuse("package-file-invalid", Some(&p.path), "a dependency entry must be a .dll and name its license")
            }
            _ => {}
        }
    }

    let mut members = Vec::new();
    let mut total = 0u64;
    let mut manifest_text = None;
    let mut cooks: Vec<(String, Vec<u8>)> = Vec::new();
    let mut dlls: Vec<(String, String, String)> = Vec::new(); // (member, rel lower, sha)
    let mut seen_declared: HashSet<String> = HashSet::new();
    for e in entries.iter().filter(|e| !e.is_dir) {
        let rel = e.name.strip_prefix(&prefix).unwrap_or(&e.name);
        let lower = rel.to_lowercase();
        let base = lower.rsplit('/').next().unwrap_or(&lower).to_string();
        // Forbidden categories first: nothing declared can waive them.
        if let Some((reason, why)) = policy::refuse_by_path(&lower) {
            return refuse(reason, Some(&e.name), why);
        }
        if policy::is_runtime_dll(&base) {
            return refuse("runtime-dll", Some(&e.name), "it is a DLL of Kiln or of .NET itself; a mod never ships those");
        }
        let is_manifest = rel == manifest_name;
        let is_cook = base == "cook.json";
        let is_default = lower.starts_with("config/defaults/");
        let r = read_member(&mut a, e, &mut total, is_manifest || is_cook || is_default)?;
        if r.pem {
            return refuse("private-key", Some(&e.name), "it holds a private key");
        }
        if r.kfc {
            return refuse("game-data", Some(&e.name), "it is one of the game's data archives (by its content)");
        }
        if is_manifest {
            manifest_text = r.captured.clone();
        }
        if is_cook {
            cooks.push((lower.clone(), r.captured.clone().unwrap_or_default()));
        }
        if let Some(p) = declared.get(&lower) {
            if p.sha256 != r.sha256 {
                return refuse("package-file-mismatch", Some(&e.name), "it is not the file packageFiles declares (another sha256)");
            }
            seen_declared.insert(lower.clone());
        }
        if is_default {
            match declared.get(&lower) {
                Some(p) if p.purpose == Purpose::DefaultConfig => {}
                _ => return refuse("undeclared-default-config", Some(&e.name), "a shipped default config needs a packageFiles entry (path, sha256, purpose default-config)"),
            }
            let bytes = r.captured.as_deref().unwrap_or_default();
            let text = String::from_utf8_lossy(bytes);
            let hit = match crate::jsonc::parse(&text) {
                Ok(v) => policy::secret_value(&v, ""),
                Err(_) => secret_line(&text),
            };
            if let Some(key) = hit {
                return refuse("secret-in-config", Some(&e.name), format!("its key {key} has a value; a shipped default may only leave such a key empty"));
            }
        }
        if base.ends_with(".dll") {
            dlls.push((e.name.clone(), lower.clone(), r.sha256.clone()));
        }
        members.push(Member { name: e.name.clone(), bytes: r.bytes, sha256: r.sha256 });
    }
    if total / policy::MAX_RATIO > zip_bytes.max(1) {
        return refuse("ratio", None, format!("as a whole it expands more than {} times", policy::MAX_RATIO));
    }
    if let Some(missing) = input.package_files.iter().find(|p| !seen_declared.contains(&p.path.to_lowercase())) {
        return refuse("package-file-missing", Some(&missing.path), "packageFiles declares it and the zip does not hold it");
    }

    // The manifest: one mod, and the one the plan names.
    let text = String::from_utf8(manifest_text.unwrap_or_default())
        .map_err(|_| Error::from(Refusal::new("manifest-invalid", Some(manifest_name), "it is not UTF-8")))?;
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let invalid = |e: Error| Error::from(Refusal::new("manifest-invalid", Some(manifest_name), e.message));
    let (kind, id, version, side, api_version, own_dlls) = if manifest_name == manifest::KILN_MANIFEST {
        let m = manifest::parse_kiln(text).map_err(invalid)?;
        let own = if m.entry_assembly.is_empty() { vec![] } else { vec![m.entry_assembly.to_lowercase()] };
        (ModKind::Kiln, m.id, m.version, m.side, m.api_version, own)
    } else {
        let m = manifest::parse_native(text).map_err(invalid)?;
        let own = m.dlls.iter().map(|d| d.replace('\\', "/").to_lowercase()).collect();
        (ModKind::Native, m.id, m.version, m.side, m.kiln.unwrap_or_else(|| "*".into()), own)
    };
    if id != root {
        return refuse("manifest-mismatch", Some(manifest_name), format!("its id {id} is not the folder's name {root}"));
    }
    if input.expect_id.is_some_and(|x| x != id) {
        return refuse("manifest-mismatch", Some(manifest_name), format!("its id {id} is not the mod this release is for"));
    }
    let parsed = crate::semver::SemVersion::try_parse(&version)
        .ok_or_else(|| Error::from(Refusal::new("manifest-invalid", Some(manifest_name), "its version is not a semantic version")))?;
    if let Some(want) = input.expect_version {
        let same = crate::semver::SemVersion::try_parse(want).is_some_and(|w| w.compare(&parsed) == std::cmp::Ordering::Equal && w.build == parsed.build);
        if !same {
            return refuse("manifest-mismatch", Some(manifest_name), format!("its version {version} is not the version this release is for"));
        }
    }

    // DLLs: the mod's own, or declared dependencies.
    for (name, lower, _) in &dlls {
        if own_dlls.iter().any(|d| d == lower) {
            continue;
        }
        match declared.get(lower) {
            Some(p) if p.purpose == Purpose::Dependency => {}
            _ => return refuse("undeclared-dll", Some(name), "a DLL that is not the mod's own needs a packageFiles entry (path, sha256, purpose dependency, license)"),
        }
    }

    // Cook recipes: whatever lies in a recipe's output folder is cooked output.
    for (cook_path, bytes) in &cooks {
        let dir = cook_path.rsplit_once('/').map(|(d, _)| d).unwrap_or("");
        let mut outs = Vec::new();
        if let Ok(v) = crate::jsonc::parse(&String::from_utf8_lossy(bytes)) {
            outs_of(&v, &mut outs);
        }
        if outs.is_empty() {
            outs.push("cooked".into()); // the schema's default
        }
        for out in outs {
            let Some(folder) = resolve_out(dir, &out) else { continue };
            if folder.is_empty() {
                continue;
            }
            let under = format!("{folder}/");
            if let Some(m) = members.iter().find(|m| m.name.strip_prefix(&prefix).unwrap_or(&m.name).to_lowercase().starts_with(&under)) {
                return refuse("cooked-output", Some(&m.name), format!("it is in the output folder of the cook recipe {cook_path}"));
            }
        }
    }

    members.sort_by(|x, y| x.name.cmp(&y.name));
    Ok(AuditReport { policy_version: policy::POLICY_VERSION, kind, id, version, side, api_version, zip_bytes, zip_sha256, expanded_bytes: total, members })
}

/// Rewrites a zip whose manifest is at its top level into the `<id>/...` layout (members are copied as they are,
/// compressed bytes untouched). Returns false and writes nothing when `src` already has one root folder. The result
/// still has to pass [`audit`].
pub fn normalize_root(src: &Path, dst: &Path) -> Result<bool> {
    let f = std::fs::File::open(src).map_err(crate::util::io_at(src, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(unreadable)?;
    let entries = list(&mut a)?;
    let top = entries.iter().find(|e| !e.is_dir && is_manifest_name(&e.name));
    let Some(top) = top else { return Ok(false) };
    let mut total = 0u64;
    let r = read_member(&mut a, top, &mut total, true)?;
    let text = String::from_utf8_lossy(r.captured.as_deref().unwrap_or_default()).into_owned();
    let text = text.strip_prefix('\u{feff}').unwrap_or(&text);
    let invalid = |e: Error| Error::from(Refusal::new("manifest-invalid", Some(&top.name), e.message));
    let id = if top.name == manifest::KILN_MANIFEST { manifest::parse_kiln(text).map_err(invalid)?.id } else { manifest::parse_native(text).map_err(invalid)?.id };
    let out = std::fs::OpenOptions::new().write(true).create_new(true).open(dst).map_err(crate::util::io_at(dst, "writing"))?;
    let mut w = zip::ZipWriter::new(std::io::BufWriter::new(out));
    let werr = |e: zip::result::ZipError| Error::new(ErrorCode::Io, format!("{}: {e}", dst.display()));
    for e in entries.iter().filter(|e| !e.is_dir) {
        let file = a.by_index_raw(e.index).map_err(unreadable)?;
        w.raw_copy_file_rename(file, format!("{id}/{}", e.name)).map_err(werr)?;
    }
    let inner = w.finish().map_err(werr)?;
    inner.into_inner().map_err(|e| Error::new(ErrorCode::Io, format!("{}: {e}", dst.display())))?.sync_all().map_err(crate::util::io_at(dst, "flushing"))?;
    Ok(true)
}

#[cfg(test)]
#[path = "artifact_tests.rs"]
mod tests;
