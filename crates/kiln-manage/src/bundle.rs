//! Runtime bundles `kiln-runtime-<semver>+<yyyymmdd>.<gitsha>.zip` with `kiln-runtime.json` at the root
//! (schema/kiln-runtime.schema.json). Deterministic: entries sorted bytewise, times fixed to the commit time,
//! no extra attributes, deflate. Created by `kilnctl bundle create` from tools/package's staging folder.

use crate::manifest::RunSide;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

pub const MANIFEST: &str = "kiln-runtime.json";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeManifest {
    pub format: u32,
    pub version: String,
    pub kiln: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub git: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created: Option<String>,
    pub abi: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub api: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub dotnet: Option<String>,
    pub components: BTreeMap<String, RuntimeComponent>,
    pub files: Vec<RuntimeFile>,
    #[serde(default)]
    pub builds: RuntimeBuilds,
}

fn yes() -> bool {
    true
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeComponent {
    pub sides: Vec<RunSide>,
    #[serde(default = "yes")]
    pub default: bool,
    #[serde(default)]
    pub requires: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub description: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeFile {
    /// Relative to the game folder, '/' separated.
    pub path: String,
    pub size: u64,
    pub sha256: String,
    pub component: String,
    /// User-editable: installed only when missing, never replaced or deleted.
    #[serde(default)]
    pub config: bool,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeBuilds {
    #[serde(default)]
    pub server: Vec<RuntimeBuild>,
    #[serde(default)]
    pub client: Vec<RuntimeBuild>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeBuild {
    pub sha256: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub steam_build: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub svn: Option<u64>,
    #[serde(default = "yes")]
    pub verified: bool,
}

/// A bundle the launcher has (in `<data>/runtime/`), for the UI.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RuntimeBundle {
    pub version: String,
    pub kiln: String,
    pub file: PathBuf,
    pub sha256: String,
    pub created: Option<String>,
    pub sides: Vec<RunSide>,
    pub components: Vec<String>,
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CreateOptions {
    /// Commit time (seconds) for entry times and `created`; SOURCE_DATE_EPOCH.
    pub source_date_epoch: i64,
    pub git: String,
}

/// The zip name of a bundle version: `kiln-runtime-<version>.zip`.
pub fn file_name(version: &str) -> String {
    format!("kiln-runtime-{version}.zip")
}

/// Every side some component of the bundle supports.
pub fn sides(m: &RuntimeManifest) -> Vec<RunSide> {
    let mut v: Vec<RunSide> = Vec::new();
    for c in m.components.values() {
        for s in &c.sides {
            if !v.contains(s) {
                v.push(*s);
            }
        }
    }
    v.sort_by_key(|s| match s {
        RunSide::Server => 0,
        RunSide::Client => 1,
    });
    v
}

/// A zip entry name or manifest path that is safe to join to a folder (zip-slip guard): relative, '/' separated, no
/// empty, `.` or `..` component, no backslash, drive letter, NUL or control character. Returns it unchanged.
pub fn safe_rel_path(name: &str) -> Result<&str> {
    let bad = |why: &str| Err(Error::new(ErrorCode::Refused, format!("unsafe path {name:?} in the bundle: {why}")));
    if name.is_empty() {
        return bad("empty");
    }
    if name.starts_with('/') {
        return bad("absolute");
    }
    if name.contains('\\') {
        return bad("backslash");
    }
    if name.chars().any(|c| (c as u32) < 32 || c == ':') {
        return bad("drive letter, stream name or control character");
    }
    for part in name.split('/') {
        if part.is_empty() || part == "." || part == ".." {
            return bad("empty, . or .. component");
        }
    }
    Ok(name)
}

/// Checks the manifest's own rules: format, paths, components of files, sorted unique paths.
pub fn validate(m: &RuntimeManifest) -> Result<()> {
    if m.format != 1 {
        return Err(Error::new(ErrorCode::Unsupported, format!("kiln-runtime.json format {} (this Kiln reads format 1)", m.format)));
    }
    let mut problems = Vec::new();
    for (i, f) in m.files.iter().enumerate() {
        if let Err(e) = safe_rel_path(&f.path) {
            problems.push(e.message);
        }
        if !m.components.contains_key(&f.component) {
            problems.push(format!("{}: unknown component {}", f.path, f.component));
        }
        if f.sha256.len() != 64 || !f.sha256.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()) {
            problems.push(format!("{}: bad sha256", f.path));
        }
        if i > 0 && m.files[i - 1].path.as_bytes() >= f.path.as_bytes() {
            problems.push(format!("{}: files are not sorted or not unique", f.path));
        }
        if f.path == MANIFEST {
            problems.push(format!("{MANIFEST} cannot be a runtime file"));
        }
    }
    for (name, c) in &m.components {
        for r in &c.requires {
            if !m.components.contains_key(r) {
                problems.push(format!("component {name} requires unknown component {r}"));
            }
        }
        if c.sides.is_empty() {
            problems.push(format!("component {name} has no sides"));
        }
    }
    if !problems.is_empty() {
        return Err(Error::new(ErrorCode::Schema, format!("kiln-runtime.json breaks the format's rules: {}", problems.join("; ")))
            .with_detail(problems));
    }
    Ok(())
}

#[cfg(feature = "zip")]
mod imp {
    use super::*;
    use crate::hash::hex;
    use sha2::{Digest, Sha256};
    use std::io::{Read, Write};

    fn zip_err(zip: &Path) -> impl Fn(zip::result::ZipError) -> Error + '_ {
        move |e| Error::new(ErrorCode::Parse, format!("{}: {e}", zip.display()))
    }

    pub fn open(zip: &Path) -> Result<zip::ZipArchive<std::fs::File>> {
        let f = std::fs::File::open(zip).map_err(crate::util::io_at(zip, "opening"))?;
        zip::ZipArchive::new(f).map_err(zip_err(zip))
    }

    pub fn read_manifest(a: &mut zip::ZipArchive<std::fs::File>, zip: &Path) -> Result<RuntimeManifest> {
        let mut e = a.by_name(MANIFEST).map_err(|_| Error::new(ErrorCode::Schema, format!("{}: no {MANIFEST}", zip.display())))?;
        let mut text = Vec::new();
        e.read_to_end(&mut text).map_err(crate::util::io_at(zip, "reading"))?;
        let m: RuntimeManifest = serde_json::from_slice(&text)
            .map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {MANIFEST}: {e}", zip.display())))?;
        validate(&m)?;
        Ok(m)
    }

    pub fn read(zip: &Path) -> Result<RuntimeManifest> {
        read_manifest(&mut open(zip)?, zip)
    }

    fn check_entries(a: &mut zip::ZipArchive<std::fs::File>, m: &RuntimeManifest, zip: &Path) -> Result<()> {
        let wanted: std::collections::BTreeSet<&str> = m.files.iter().map(|f| f.path.as_str()).collect();
        let mut seen = std::collections::BTreeSet::new();
        for i in 0..a.len() {
            let e = a.by_index_raw(i).map_err(zip_err(zip))?;
            let name = e.name().map_err(zip_err(zip))?.to_string();
            if e.is_symlink() {
                return Err(Error::new(ErrorCode::Refused, format!("{}: symlink entry {name}", zip.display())));
            }
            if e.is_dir() {
                safe_rel_path(name.trim_end_matches('/'))?;
                continue;
            }
            safe_rel_path(&name)?;
            if name != MANIFEST && !wanted.contains(name.as_str()) {
                return Err(Error::new(ErrorCode::Schema, format!("{}: entry {name} is not in {MANIFEST}", zip.display())));
            }
            if !seen.insert(name.clone()) {
                return Err(Error::new(ErrorCode::Schema, format!("{}: entry {name} is there twice", zip.display())));
            }
        }
        if let Some(missing) = wanted.iter().find(|p| !seen.contains(**p)) {
            return Err(Error::new(ErrorCode::Schema, format!("{}: {missing} is listed but not in the zip", zip.display())));
        }
        Ok(())
    }

    /// Streams one entry into `out` (when given) while hashing it; checks size and sha256.
    fn copy_checked(a: &mut zip::ZipArchive<std::fs::File>, f: &RuntimeFile, zip: &Path, mut out: Option<&mut dyn Write>) -> Result<()> {
        let mut e = a.by_name(&f.path).map_err(zip_err(zip))?;
        let mut h = Sha256::new();
        let mut buf = vec![0u8; 1 << 16];
        let mut size = 0u64;
        loop {
            let n = e.read(&mut buf).map_err(|err| Error::new(ErrorCode::Parse, format!("{}: {}: {err}", zip.display(), f.path)))?;
            if n == 0 {
                break;
            }
            size += n as u64;
            if size > f.size {
                break;
            }
            h.update(&buf[..n]);
            if let Some(o) = out.as_mut() {
                o.write_all(&buf[..n])?;
            }
        }
        let sha = hex(h.finalize().as_slice());
        if size != f.size || sha != f.sha256 {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{}: {} does not match {MANIFEST} (size {size}, sha256 {sha})", zip.display(), f.path))
                .with_detail(serde_json::json!({ "path": f.path, "expectedSha256": f.sha256, "expectedSize": f.size })));
        }
        Ok(())
    }

    pub fn verify(zip: &Path, progress: &mut dyn Progress) -> Result<RuntimeManifest> {
        let mut a = open(zip)?;
        let m = read_manifest(&mut a, zip)?;
        check_entries(&mut a, &m, zip)?;
        let total: u64 = m.files.iter().map(|f| f.size).sum::<u64>().max(1);
        let mut done = 0u64;
        for f in &m.files {
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "canceled"));
            }
            copy_checked(&mut a, f, zip, None)?;
            done += f.size;
            progress.report(Some(done as f32 / total as f32), &format!("verified {}", f.path));
        }
        Ok(m)
    }

    pub fn extract(zip: &Path, files: &[RuntimeFile], dest: &Path, progress: &mut dyn Progress) -> Result<()> {
        let mut a = open(zip)?;
        let m = read_manifest(&mut a, zip)?;
        let total: u64 = files.iter().map(|f| f.size).sum::<u64>().max(1);
        let mut done = 0u64;
        for f in files {
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "canceled"));
            }
            let listed = m.files.iter().find(|x| x.path == f.path);
            if listed != Some(f) {
                return Err(Error::invalid(format!("{} is not a file of this bundle", f.path)));
            }
            let target = dest.join(safe_rel_path(&f.path)?);
            if !target.starts_with(dest) {
                return Err(Error::new(ErrorCode::Refused, format!("{} escapes the staging folder", f.path)));
            }
            if let Some(parent) = target.parent() {
                std::fs::create_dir_all(parent).map_err(crate::util::io_at(parent, "creating"))?;
            }
            let mut out = std::fs::File::create(&target).map_err(crate::util::io_at(&target, "writing"))?;
            copy_checked(&mut a, f, zip, Some(&mut out))?;
            out.sync_all().map_err(crate::util::io_at(&target, "flushing"))?;
            done += f.size;
            progress.report(Some(done as f32 / total as f32), &format!("staged {}", f.path));
        }
        Ok(())
    }

    fn zip_time(epoch: i64) -> zip::DateTime {
        let (y, mo, d, h, mi, s) = crate::util::utc_secs(epoch.max(315_532_800)); // 1980-01-01
        zip::DateTime::from_date_and_time(y.min(2107) as u16, mo as u8, d as u8, h as u8, mi as u8, s as u8)
            .unwrap_or_else(|_| zip::DateTime::default_for_write())
    }

    pub fn write_zip(out: &Path, entries: &[(String, Vec<u8>)], epoch: i64) -> Result<()> {
        let tmp = crate::util::with_suffix(out, ".tmp");
        let f = std::fs::File::create(&tmp).map_err(crate::util::io_at(&tmp, "writing"))?;
        let mut w = zip::ZipWriter::new(std::io::BufWriter::new(f));
        let opts = zip::write::SimpleFileOptions::default()
            .compression_method(zip::CompressionMethod::Deflated)
            .compression_level(Some(6))
            .last_modified_time(zip_time(epoch))
            .unix_permissions(0o644)
            .system(zip::System::Unix)
            .large_file(false);
        let mut sorted: Vec<&(String, Vec<u8>)> = entries.iter().collect();
        sorted.sort_by(|a, b| a.0.as_bytes().cmp(b.0.as_bytes()));
        for (name, data) in sorted {
            let o = if data.len() as u64 >= 0xFFFF_0000 { opts.large_file(true) } else { opts };
            w.start_file(name.as_str(), o).map_err(zip_err(out))?;
            w.write_all(data)?;
        }
        let mut inner = w.finish().map_err(zip_err(out))?;
        inner.flush()?;
        inner.into_inner().map_err(|e| Error::from(e.into_error()))?.sync_all()?;
        crate::util::rename_replace(&tmp, out)
    }
}

/// Staging folder layout for [`create`]: `<stage>/kiln-runtime.json` = the manifest without `files` (plus
/// `"config": ["kiln/kiln.json", ...]`, the user-editable files) and one folder per component holding its files at
/// their game-relative paths: `<stage>/core/dinput8.dll`, `<stage>/core/kiln/kiln-core.dll`,
/// `<stage>/sigdb-server/kiln/sigdb/server.json`, ...
pub const STAGE_CONFIG_KEY: &str = "config";

fn walk_stage(root: &Path, dir: &Path, out: &mut Vec<(String, PathBuf)>) -> Result<()> {
    let mut entries: Vec<_> = std::fs::read_dir(dir).map_err(crate::util::io_at(dir, "reading"))?.collect::<std::io::Result<Vec<_>>>()?;
    entries.sort_by_key(|e| e.file_name());
    for e in entries {
        let p = e.path();
        let ft = std::fs::symlink_metadata(&p)?.file_type();
        if ft.is_symlink() {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not bundled", p.display())));
        }
        if ft.is_dir() {
            walk_stage(root, &p, out)?;
        } else if ft.is_file() {
            let rel = p.strip_prefix(root).expect("below root");
            let rel = rel
                .components()
                .map(|c| c.as_os_str().to_str().map(str::to_string))
                .collect::<Option<Vec<_>>>()
                .ok_or_else(|| Error::invalid(format!("{}: name is not UTF-8", p.display())))?
                .join("/");
            safe_rel_path(&rel)?;
            out.push((rel, p));
        }
    }
    Ok(())
}

/// The manifest `create` would write (no zip), for tests and `kilnctl bundle create --dry-run`.
pub fn manifest_for_stage(stage: &Path, options: &CreateOptions) -> Result<(RuntimeManifest, Vec<(String, PathBuf)>)> {
    let mpath = stage.join(MANIFEST);
    let mut v: serde_json::Value = crate::util::read_json(&mpath)?
        .ok_or_else(|| Error::not_found(format!("{}: no {MANIFEST} in the staging folder", stage.display())))?;
    let obj = v.as_object_mut().ok_or_else(|| Error::new(ErrorCode::Schema, format!("{}: not an object", mpath.display())))?;
    let config: Vec<String> = match obj.remove(STAGE_CONFIG_KEY) {
        Some(c) => serde_json::from_value(c)?,
        None => Vec::new(),
    };
    let kiln = obj.get("kiln").and_then(|k| k.as_str()).ok_or_else(|| Error::new(ErrorCode::Schema, "kiln-runtime.json: no kiln version"))?;
    if options.git.is_empty() || !options.git.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()) || !(7..=40).contains(&options.git.len()) {
        return Err(Error::invalid(format!("git {:?}: 7-40 lower-case hex chars", options.git)));
    }
    let (y, mo, d, ..) = crate::util::utc_secs(options.source_date_epoch);
    let version = format!("{kiln}+{y:04}{mo:02}{d:02}.{}", options.git);
    obj.insert("version".into(), version.into());
    obj.insert("git".into(), options.git.clone().into());
    obj.insert("created".into(), crate::util::rfc3339(options.source_date_epoch).into());
    obj.insert("files".into(), serde_json::Value::Array(Vec::new()));
    let mut m: RuntimeManifest = serde_json::from_value(v)
        .map_err(|e| Error::new(ErrorCode::Schema, format!("{}: {e}", mpath.display())))?;
    let mut files: Vec<(String, PathBuf)> = Vec::new();
    let mut entries: Vec<_> = std::fs::read_dir(stage).map_err(crate::util::io_at(stage, "reading"))?.collect::<std::io::Result<Vec<_>>>()?;
    entries.sort_by_key(|e| e.file_name());
    for e in entries {
        let name = e.file_name().to_string_lossy().into_owned();
        if name == MANIFEST {
            continue;
        }
        if !e.file_type()?.is_dir() {
            return Err(Error::new(ErrorCode::Schema, format!("{}: unexpected file {name} (components are folders)", stage.display())));
        }
        if !m.components.contains_key(&name) {
            return Err(Error::new(ErrorCode::Schema, format!("{}: folder {name} is not a component in {MANIFEST}", stage.display())));
        }
        let mut found = Vec::new();
        walk_stage(&e.path(), &e.path(), &mut found)?;
        for (rel, src) in found {
            let (sha256, size) = crate::hash::sha256_file(&src)?;
            m.files.push(RuntimeFile { config: config.contains(&rel), path: rel.clone(), size, sha256, component: name.clone() });
            files.push((rel, src));
        }
    }
    m.files.sort_by(|a, b| a.path.as_bytes().cmp(b.path.as_bytes()));
    if let Some(w) = m.files.windows(2).find(|w| w[0].path == w[1].path) {
        return Err(Error::new(ErrorCode::Conflict, format!("{} is in two components ({}, {})", w[0].path, w[0].component, w[1].component)));
    }
    for c in &config {
        if !m.files.iter().any(|f| &f.path == c) {
            return Err(Error::new(ErrorCode::Schema, format!("config file {c} is not in any component")));
        }
    }
    validate(&m)?;
    Ok((m, files))
}

/// Reads `kiln-runtime.json` from a bundle zip (feature `zip`).
pub fn read(zip: &Path) -> Result<RuntimeManifest> {
    #[cfg(feature = "zip")]
    {
        imp::read(zip)
    }
    #[cfg(not(feature = "zip"))]
    {
        let _ = zip;
        Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
    }
}

/// Reads the manifest and checks every file's size and sha256 against it.
pub fn verify(zip: &Path, progress: &mut dyn Progress) -> Result<RuntimeManifest> {
    #[cfg(feature = "zip")]
    {
        imp::verify(zip, progress)
    }
    #[cfg(not(feature = "zip"))]
    {
        let _ = (zip, progress);
        Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
    }
}

/// Unpacks `files` (entries of the bundle's manifest) below `dest`, checking each one's size and sha256 while it is
/// written (zip-slip guarded). The installer stages with it.
pub fn extract(zip: &Path, files: &[RuntimeFile], dest: &Path, progress: &mut dyn Progress) -> Result<()> {
    #[cfg(feature = "zip")]
    {
        imp::extract(zip, files, dest, progress)
    }
    #[cfg(not(feature = "zip"))]
    {
        let _ = (zip, files, dest, progress);
        Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
    }
}

/// Builds a deterministic bundle from a staging folder (game-relative layout + kiln-runtime.json without files[]).
/// Returns the zip's path in `out_dir`.
pub fn create(stage: &Path, out_dir: &Path, options: &CreateOptions) -> Result<PathBuf> {
    let (m, files) = manifest_for_stage(stage, options)?;
    #[cfg(feature = "zip")]
    {
        let mut entries: Vec<(String, Vec<u8>)> = Vec::with_capacity(files.len() + 1);
        let mut text = serde_json::to_string_pretty(&m)?;
        text.push('\n');
        entries.push((MANIFEST.to_string(), text.into_bytes()));
        for (rel, src) in files {
            entries.push((rel, std::fs::read(&src).map_err(crate::util::io_at(&src, "reading"))?));
        }
        std::fs::create_dir_all(out_dir).map_err(crate::util::io_at(out_dir, "creating"))?;
        let out = out_dir.join(file_name(&m.version));
        imp::write_zip(&out, &entries, options.source_date_epoch)?;
        Ok(out)
    }
    #[cfg(not(feature = "zip"))]
    {
        let _ = (m, files, out_dir);
        Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
    }
}

fn describe(zip: &Path, m: &RuntimeManifest) -> Result<RuntimeBundle> {
    Ok(RuntimeBundle {
        version: m.version.clone(),
        kiln: m.kiln.clone(),
        file: zip.to_path_buf(),
        sha256: crate::hash::sha256_file(zip)?.0,
        created: m.created.clone(),
        sides: sides(m),
        components: m.components.keys().cloned().collect(),
    })
}

/// The bundles in `<data>/runtime/` (unreadable zips are skipped), newest version first.
pub fn list(dirs: &crate::paths::DataDirs) -> Result<Vec<RuntimeBundle>> {
    let dir = dirs.runtime();
    let mut out = Vec::new();
    for e in std::fs::read_dir(&dir).into_iter().flatten().flatten() {
        let p = e.path();
        if p.extension().is_some_and(|x| x == "zip")
            && let Ok(m) = read(&p)
        {
            out.push(describe(&p, &m)?);
        }
    }
    out.sort_by(|a, b| compare_versions(&b.version, &a.version).then_with(|| b.created.cmp(&a.created)));
    Ok(out)
}

/// Bundle versions `<semver>+<yyyymmdd>.<git>`: by the semver, then by the date.
pub fn compare_versions(a: &str, b: &str) -> std::cmp::Ordering {
    let split = |v: &str| -> (Option<crate::semver::SemVersion>, String) {
        let (base, build) = v.split_once('+').unwrap_or((v, ""));
        (crate::semver::SemVersion::parse(base).ok(), build.split('.').next().unwrap_or("").to_string())
    };
    let (va, da) = split(a);
    let (vb, db) = split(b);
    match (va, vb) {
        (Some(x), Some(y)) => x.compare(&y).then(da.cmp(&db)),
        _ => a.cmp(b),
    }
}

/// Copies a bundle zip into `<data>/runtime/` after `verify`.
pub fn import(dirs: &crate::paths::DataDirs, zip: &Path) -> Result<RuntimeBundle> {
    let m = verify(zip, &mut crate::task::NoProgress)?;
    let dir = dirs.runtime();
    std::fs::create_dir_all(&dir).map_err(crate::util::io_at(&dir, "creating"))?;
    let dst = dir.join(file_name(&m.version));
    if std::fs::canonicalize(zip).ok() != std::fs::canonicalize(&dst).ok() {
        let tmp = crate::util::with_suffix(&dst, ".tmp");
        std::fs::copy(zip, &tmp).map_err(crate::util::io_at(&tmp, "copying to"))?;
        crate::util::rename_replace(&tmp, &dst)?;
    }
    describe(&dst, &m)
}

/// Copies the bundle a launcher ships into `<data>/runtime/`, unless that version is already there and readable.
/// `None`: nothing to do.
pub fn import_shipped(dirs: &crate::paths::DataDirs, zip: &Path) -> Result<Option<RuntimeBundle>> {
    let m = read(zip)?;
    if read(&dirs.runtime().join(file_name(&m.version))).is_ok() {
        return Ok(None);
    }
    import(dirs, zip).map(Some)
}

/// The bundle of `version` in `<data>/runtime/`.
pub fn find(dirs: &crate::paths::DataDirs, version: &str) -> Result<RuntimeBundle> {
    list(dirs)?
        .into_iter()
        .find(|b| b.version == version)
        .ok_or_else(|| Error::not_found(format!("no runtime bundle {version} in {}", dirs.runtime().display())))
}
