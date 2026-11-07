//! Getting a mod file from Nexus Mods into the game: download links (premium: by the API alone; free: with the key and
//! expiry of a Mod Manager Download link), the download (https, at most 2 GB, into `<data>/nexus/downloads/`), the
//! check (a zip or a DLL; every file whose path and size Nexus's archive manifest lists must match, and its sha256 when
//! the manifest has one; something Kiln can load must be inside), then the simple flow (`quick::add`: import, add to
//! the active profile, write profile.json; Undo is `quick::revert`) and the install record. The file's requirements on
//! Nexus come back with the report, for the UI to offer.

use super::api::{FileVersion, ManifestEntry};
use super::catalog::{self, Catalog, GAME, InstallRecord, Requirement};
use crate::game::GameInstall;
use crate::paths::DataDirs;
use crate::quick::AddReport;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallRequest {
    /// Game-scoped mod id.
    pub mod_id: String,
    /// Game-scoped file version id.
    pub file_id: String,
    /// From an nxm link (free accounts); never serialized.
    #[serde(skip)]
    pub key: Option<String>,
    #[serde(skip)]
    pub expires: Option<i64>,
    /// "nxm" or "catalog".
    pub source: String,
}

impl std::fmt::Debug for InstallRequest {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "InstallRequest(mod {} file {} via {})", self.mod_id, self.file_id, self.source)
    }
}

/// What the check found.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CheckReport {
    /// Files in the archive.
    pub files: usize,
    /// Files whose size matched Nexus's manifest.
    pub matched: usize,
    /// Of those, files whose sha256 matched too.
    pub hashed: usize,
    /// Nexus had no manifest for the upload (nothing to compare).
    pub no_manifest: bool,
    pub sha256: String,
    pub size: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallReport {
    /// The simple flow's report (Undo: `profile_revert` with its `profileId` and `changes`).
    pub add: AddReport,
    pub mod_id: String,
    pub name: String,
    pub version: String,
    pub file_name: String,
    pub check: CheckReport,
    /// The file's requirements on Nexus (Kiln itself and other games marked; the UI offers the rest).
    pub requirements: Vec<Requirement>,
    pub download: String,
}

fn file_name_of(url: &str, fallback: &str) -> String {
    let path = url.split(['?', '#']).next().unwrap_or(url);
    let last = path.rsplit('/').next().unwrap_or("");
    let decoded = {
        let b = last.as_bytes();
        let mut out = Vec::new();
        let mut i = 0;
        while i < b.len() {
            if b[i] == b'%' && i + 2 < b.len()
                && let Some(v) = std::str::from_utf8(&b[i + 1..i + 3]).ok().and_then(|h| u8::from_str_radix(h, 16).ok())
            {
                out.push(v);
                i += 3;
                continue;
            }
            out.push(b[i]);
            i += 1;
        }
        String::from_utf8_lossy(&out).into_owned()
    };
    let pick = if decoded.contains('.') { decoded } else { fallback.to_string() };
    let clean: String = pick.chars().map(|c| if c.is_alphanumeric() || "._-+() ".contains(c) { c } else { '_' }).collect();
    let clean = clean.trim_matches(|c| c == '.' || c == ' ').chars().take(120).collect::<String>();
    if clean.is_empty() { "download".into() } else { clean }
}

enum Format {
    Zip,
    Dll,
    Other(&'static str),
}

fn sniff(head: &[u8]) -> Format {
    if head.starts_with(b"PK\x03\x04") || head.starts_with(b"PK\x05\x06") {
        Format::Zip
    } else if head.starts_with(b"MZ") {
        Format::Dll
    } else if head.starts_with(b"7z\xBC\xAF\x27\x1C") {
        Format::Other("a 7z archive")
    } else if head.starts_with(b"Rar!") {
        Format::Other("a RAR archive")
    } else {
        Format::Other("not a zip or a DLL")
    }
}

/// Downloads `url` into `dir` (https only; at most 2 GB), naming it from the URL (else `fallback`) with the right
/// extension. Returns the path, its sha256 and size.
pub fn download(catalog: &Catalog, url: &str, dir: &Path, fallback: &str, progress: &mut dyn Progress) -> Result<(PathBuf, String, u64)> {
    use sha2::Digest;
    let mut s = catalog.client().open_download(url)?;
    if s.length.is_some_and(|n| n > super::MAX_DOWNLOAD) {
        return Err(Error::new(ErrorCode::Refused, "The file is over Kiln's 2 GB limit."));
    }
    std::fs::create_dir_all(dir).map_err(crate::util::io_at(dir, "creating"))?;
    let mut name = file_name_of(url, fallback);
    let part = dir.join(format!(".{name}.part"));
    let r = (|| -> Result<(String, u64, [u8; 8])> {
        let mut f = std::fs::File::create(&part).map_err(crate::util::io_at(&part, "writing"))?;
        let mut h = sha2::Sha256::new();
        let mut buf = vec![0u8; 256 * 1024];
        let mut got = 0u64;
        let mut head = [0u8; 8];
        let mut last = std::time::Instant::now();
        loop {
            if progress.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "download canceled"));
            }
            let n = s.reader.read(&mut buf).map_err(|e| Error::new(ErrorCode::Network, format!("downloading: {e}")))?;
            if n == 0 {
                break;
            }
            if got < 8 {
                let k = (8 - got as usize).min(n);
                head[got as usize..got as usize + k].copy_from_slice(&buf[..k]);
            }
            got += n as u64;
            if got > super::MAX_DOWNLOAD {
                return Err(Error::new(ErrorCode::Refused, "The file is over Kiln's 2 GB limit."));
            }
            h.update(&buf[..n]);
            f.write_all(&buf[..n])?;
            if last.elapsed() > std::time::Duration::from_millis(150) {
                last = std::time::Instant::now();
                progress.report(s.length.map(|t| (got as f32 / t.max(1) as f32) * 0.8), &format!("Downloading {}", crate::util::human_size(got)));
            }
        }
        f.sync_all()?;
        if let Some(t) = s.length
            && t != got
        {
            return Err(Error::new(ErrorCode::Network, format!("The download stopped at {} of {}.", crate::util::human_size(got), crate::util::human_size(t))));
        }
        Ok((crate::hash::hex(h.finalize().as_slice()), got, head))
    })();
    let (sha, size, head) = match r {
        Ok(x) => x,
        Err(e) => {
            let _ = std::fs::remove_file(&part);
            return Err(e);
        }
    };
    match sniff(&head) {
        Format::Zip if !name.to_lowercase().ends_with(".zip") => name.push_str(".zip"),
        Format::Dll if !name.to_lowercase().ends_with(".dll") => name.push_str(".dll"),
        Format::Other(what) => {
            let _ = std::fs::remove_file(&part);
            return Err(Error::new(ErrorCode::Unsupported, format!("The file is {what}: Kiln adds .zip mods and DLLs. Download it from the site and unpack it.")));
        }
        _ => {}
    }
    let dest = dir.join(&name);
    crate::util::rename_replace(&part, &dest)?;
    Ok((dest, sha, size))
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
fn norm(p: &str) -> String {
    p.replace('\\', "/").trim_start_matches('/').to_lowercase()
}

/// Compares a zip with Nexus's manifest: a listed file of another size (or sha256) fails the check. Paths are matched
/// as they are, then without a top folder on either side (repacked uploads).
#[cfg(feature = "zip")]
pub fn check_zip(path: &Path, manifest: &[ManifestEntry]) -> Result<CheckReport> {
    use sha2::Digest;
    let f = std::fs::File::open(path).map_err(crate::util::io_at(path, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(|e| Error::new(ErrorCode::Parse, format!("not a readable zip: {e}")))?;
    let mut entries: Vec<(String, u64, usize)> = Vec::new();
    for i in 0..a.len() {
        let e = a.by_index(i).map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?;
        if e.is_dir() {
            continue;
        }
        let name = e.name().map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?.to_string();
        entries.push((norm(&name), e.size(), i));
    }
    let strip = |p: &str| p.split_once('/').map(|(_, r)| r.to_string());
    let mut report = CheckReport { files: entries.len(), no_manifest: manifest.is_empty(), ..Default::default() };
    for m in manifest {
        let mp = norm(&m.path);
        let found = entries.iter().find(|e| e.0 == mp)
            .or_else(|| strip(&mp).and_then(|s| entries.iter().find(|e| e.0 == s)))
            .or_else(|| entries.iter().find(|e| strip(&e.0).as_deref() == Some(mp.as_str())));
        let Some((name, size, idx)) = found.cloned() else { continue };
        if let Some(want) = m.size {
            if want != size {
                return Err(Error::new(ErrorCode::HashMismatch, format!("{name} is {size} bytes; Nexus Mods lists {want}. The download is damaged or not the file Nexus has.")));
            }
            report.matched += 1;
        }
        if let Some(want) = &m.sha256 {
            let mut e = a.by_index(idx).map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?;
            let mut h = sha2::Sha256::new();
            let mut buf = vec![0u8; 64 * 1024];
            loop {
                let n = e.read(&mut buf).map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?;
                if n == 0 {
                    break;
                }
                h.update(&buf[..n]);
            }
            let have = crate::hash::hex(h.finalize().as_slice());
            if &have != want {
                return Err(Error::new(ErrorCode::HashMismatch, format!("{name} does not match Nexus Mods' file list (sha256).")));
            }
            report.hashed += 1;
        }
    }
    Ok(report)
}

#[cfg(not(feature = "zip"))]
pub fn check_zip(_path: &Path, _manifest: &[ManifestEntry]) -> Result<CheckReport> {
    Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
}

/// Downloads, checks and adds one file. `running`: None asks the system whether the game runs (tests pass it).
pub fn install(catalog: &Catalog, dirs: &DataDirs, game: &GameInstall, req: &InstallRequest, running: Option<bool>, progress: &mut dyn Progress) -> Result<InstallReport> {
    catalog::refuse_kiln(&req.mod_id)?;
    let client = catalog.client();
    progress.report(Some(0.02), "Asking Nexus Mods");
    let version: FileVersion = client.file_version_by_gsid(GAME, &req.file_id)?;
    let name = client.mod_details(GAME, &req.mod_id).map(|d| d.name).unwrap_or_else(|_| version.file.name.clone());
    let links = client.download_links(GAME, &req.file_id, req.key.as_deref(), req.expires)?;
    let dir = catalog.root().join("downloads").join(format!("{}-{}", req.mod_id, req.file_id));
    let mut last_err = None;
    let mut got = None;
    for l in links.iter().take(3) {
        match download(catalog, &l.url, &dir, &version.name, progress) {
            Ok(x) => {
                got = Some(x);
                break;
            }
            Err(e) if e.code == ErrorCode::Network => last_err = Some(e),
            Err(e) => return Err(e),
        }
    }
    let (path, sha, size) = got.ok_or_else(|| last_err.unwrap_or_else(|| Error::new(ErrorCode::Network, "no download link worked")))?;
    progress.report(Some(0.82), "Checking the file");
    let mut check = if path.extension().is_some_and(|x| x.eq_ignore_ascii_case("zip")) {
        let manifest = version.manifest_url.as_deref().map(|u| client.manifest(u)).transpose().unwrap_or(None).unwrap_or_default();
        check_zip(&path, &manifest)?
    } else {
        CheckReport { files: 1, no_manifest: true, ..Default::default() }
    };
    check.sha256 = sha;
    check.size = size;
    let detected = crate::library::detect(&path)?;
    let loadable = |d: &crate::library::Detected| !matches!(d.kind, crate::library::DetectKind::Unknown | crate::library::DetectKind::Runtime);
    if !loadable(&detected) && !detected.items.iter().any(loadable) {
        return Err(Error::new(ErrorCode::Unsupported, format!("{name}: {}. Kiln can't load it.", detected.reason)));
    }
    progress.report(Some(0.88), "Adding to the profile");
    let mut quiet = |_: Option<f32>, _: &str| {};
    let add = match running {
        Some(r) => crate::quick::add_with(dirs, game, std::slice::from_ref(&path), r, &mut quiet)?,
        None => crate::quick::add(dirs, game, std::slice::from_ref(&path), &mut quiet)?,
    };
    let refs: Vec<_> = add.import.imported.iter().chain(add.import.existing.iter()).cloned().collect();
    if !refs.is_empty() {
        catalog.record_install(InstallRecord {
            mod_id: req.mod_id.clone(),
            name: name.clone(),
            file_id: version.file.id.clone(),
            file_name: version.file.name.clone(),
            version_id: version.id.clone(),
            version_gsid: version.game_scoped_id.clone(),
            version: version.version.clone(),
            refs,
            installed: crate::util::now_rfc3339(),
            source: req.source.clone(),
        })?;
    }
    progress.report(Some(0.95), "Looking up requirements");
    let installed = catalog.installed(dirs).unwrap_or_default();
    let requirements = catalog.requirements(&version.id, &installed);
    progress.report(Some(1.0), "Done");
    Ok(InstallReport {
        add,
        mod_id: req.mod_id.clone(),
        name,
        version: version.version.clone(),
        file_name: version.file.name.clone(),
        check,
        requirements,
        download: path.display().to_string(),
    })
}
