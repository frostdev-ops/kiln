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
    // A separate directory per attempt keeps simultaneous installs from truncating or replacing each other's files.
    let attempt = dir.join(format!("download-{}", crate::hash::hex(&crate::util::random_bytes::<16>()?)));
    let builder = std::fs::DirBuilder::new();
    #[cfg(unix)]
    {
        use std::os::unix::fs::DirBuilderExt;
        let mut builder = builder;
        builder.mode(0o700);
        builder.create(&attempt).map_err(crate::util::io_at(&attempt, "creating"))?;
    }
    #[cfg(not(unix))]
    builder.create(&attempt).map_err(crate::util::io_at(&attempt, "creating"))?;
    let mut name = file_name_of(url, fallback);
    let part = attempt.join("download.part");
    let r = (|| -> Result<(String, u64, [u8; 8])> {
        let mut f = std::fs::OpenOptions::new().write(true).create_new(true).open(&part).map_err(crate::util::io_at(&part, "writing"))?;
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
            let _ = std::fs::remove_dir_all(&attempt);
            return Err(e);
        }
    };
    match sniff(&head) {
        Format::Zip if !name.to_lowercase().ends_with(".zip") => name.push_str(".zip"),
        Format::Dll if !name.to_lowercase().ends_with(".dll") => name.push_str(".dll"),
        Format::Other(what) => {
            let _ = std::fs::remove_dir_all(&attempt);
            return Err(Error::new(ErrorCode::Unsupported, format!("The file is {what}: Kiln adds .zip mods and DLLs. Download it from the site and unpack it.")));
        }
        _ => {}
    }
    let dest = attempt.join(&name);
    if let Err(e) = crate::util::rename_replace(&part, &dest) {
        let _ = std::fs::remove_dir_all(&attempt);
        return Err(e);
    }
    Ok((dest, sha, size))
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
fn norm(p: &str) -> String {
    p.replace('\\', "/").trim_start_matches('/').to_lowercase()
}

/// Maximum aggregate expanded size, and maximum number of entries, before an archive may reach the importer.
pub const MAX_UNPACKED: u64 = 2 * 1024 * 1024 * 1024;
pub const MAX_ARCHIVE_ENTRIES: usize = 100_000;

/// Reject dangerous paths consistently on Unix and Windows before hashing or extraction.
#[cfg(feature = "zip")]
fn archive_path(name: &str) -> Result<String> {
    let name = name.replace('\\', "/");
    let path = name.trim_end_matches('/');
    crate::bundle::safe_rel_path(path)?;
    for component in path.split('/') {
        let stem = component.split('.').next().unwrap_or("").to_ascii_uppercase();
        let device = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || stem.strip_prefix("COM").or_else(|| stem.strip_prefix("LPT")).is_some_and(|n| n.len() == 1 && matches!(n.as_bytes()[0], b'1'..=b'9'));
        if component.ends_with(['.', ' ']) || device {
            return Err(Error::new(ErrorCode::Refused, "The archive contains a path that is unsafe on Windows."));
        }
    }
    Ok(norm(path))
}

/// Validates every entry and its actual expanded size, then checks all files listed by Nexus's manifest.
/// Paths are matched as published, then without one top folder on either side (repacked uploads).
#[cfg(feature = "zip")]
pub fn check_zip(path: &Path, manifest: &[ManifestEntry]) -> Result<CheckReport> {
    check_zip_bounded(path, manifest, MAX_UNPACKED, MAX_ARCHIVE_ENTRIES)
}

#[cfg(feature = "zip")]
fn check_zip_bounded(path: &Path, manifest: &[ManifestEntry], max_size: u64, max_entries: usize) -> Result<CheckReport> {
    use sha2::Digest;
    let f = std::fs::File::open(path).map_err(crate::util::io_at(path, "opening"))?;
    let mut a = zip::ZipArchive::new(f).map_err(|e| Error::new(ErrorCode::Parse, format!("not a readable zip: {e}")))?;
    if a.len() > max_entries {
        return Err(Error::new(ErrorCode::Refused, "The archive contains too many entries."));
    }
    let mut entries: Vec<(String, u64, String)> = Vec::new();
    let mut names = std::collections::HashSet::new();
    let mut total = 0u64;
    for i in 0..a.len() {
        let mut e = a.by_index(i).map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?;
        let name = archive_path(&e.name().map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?)?;
        if e.is_symlink() || e.unix_mode().is_some_and(|m| !matches!(m & 0o170000, 0 | 0o100000 | 0o040000)) {
            return Err(Error::new(ErrorCode::Refused, "The archive contains a link or special file."));
        }
        if !names.insert(name.clone()) {
            return Err(Error::new(ErrorCode::Refused, "The archive contains duplicate paths."));
        }
        if e.is_dir() {
            continue;
        }
        if e.size() > max_size.saturating_sub(total) {
            return Err(Error::new(ErrorCode::Refused, "The expanded archive exceeds Kiln's 2 GB limit."));
        }
        let mut h = sha2::Sha256::new();
        let mut size = 0u64;
        let mut buf = [0u8; 64 * 1024];
        // Read every file, even without a manifest hash: this checks actual inflation and the ZIP CRC before import.
        loop {
            let remaining = max_size.saturating_sub(total).saturating_sub(size);
            let want = (remaining.saturating_add(1)).min(buf.len() as u64) as usize;
            let n = e.read(&mut buf[..want]).map_err(|e| Error::new(ErrorCode::Parse, format!("zip: {e}")))?;
            if n == 0 { break; }
            size += n as u64;
            if size > max_size.saturating_sub(total) {
                return Err(Error::new(ErrorCode::Refused, "The expanded archive exceeds Kiln's 2 GB limit."));
            }
            h.update(&buf[..n]);
        }
        if size != e.size() {
            return Err(Error::new(ErrorCode::Parse, "The archive entry has an incorrect expanded size."));
        }
        total += size;
        entries.push((name, size, crate::hash::hex(h.finalize().as_slice())));
    }
    let strip = |p: &str| p.split_once('/').map(|(_, r)| r.to_string());
    let mut report = CheckReport { files: entries.len(), no_manifest: manifest.is_empty(), ..Default::default() };
    for m in manifest {
        let mp = archive_path(&m.path)?;
        let found = entries.iter().find(|e| e.0 == mp)
            .or_else(|| strip(&mp).and_then(|s| entries.iter().find(|e| e.0 == s)))
            .or_else(|| entries.iter().find(|e| strip(&e.0).as_deref() == Some(mp.as_str())));
        let Some((name, size, have)) = found else {
            return Err(Error::new(ErrorCode::HashMismatch, format!("{mp} is listed by Nexus Mods but missing from the archive.")));
        };
        if let Some(want) = m.size {
            if want != *size {
                return Err(Error::new(ErrorCode::HashMismatch, format!("{name} is {size} bytes; Nexus Mods lists {want}. The download is damaged or not the file Nexus has.")));
            }
            report.matched += 1;
        }
        if let Some(want) = &m.sha256 {
            if !have.eq_ignore_ascii_case(want) {
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
    let valid_id = |id: &str| !id.is_empty() && id.len() <= 20 && id.bytes().all(|b| b.is_ascii_digit()) && id.parse::<u64>().is_ok_and(|n| n > 0);
    if !valid_id(&req.mod_id) || !valid_id(&req.file_id) {
        return Err(Error::invalid("A Nexus Mods mod or file id is invalid."));
    }
    catalog::refuse_kiln(req.mod_id.trim_start_matches('0'))?;
    let client = catalog.client();
    progress.report(Some(0.02), "Asking Nexus Mods");
    let version: FileVersion = client.file_version_by_gsid(GAME, &req.file_id)?;
    let details = client.mod_details(GAME, &req.mod_id)?;
    // A file id is game-scoped, not mod-scoped. Bind it to the claimed page before allowing the download.
    if !client.mod_files(&details.id)?.iter().any(|f| f.id == version.file.id) {
        return Err(Error::new(ErrorCode::Refused, "That file does not belong to the requested Nexus Mods page."));
    }
    let name = details.name;
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
        let manifest = version.manifest_url.as_deref().map(|u| client.manifest(u)).transpose()?.unwrap_or_default();
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
    // Import and profile edits are a read/modify/write transaction; different Nexus installs must serialize it.
    let mutation = super::handler::lock_state(catalog.root(), "install.lock")?;
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
    drop(mutation);
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

#[cfg(all(test, feature = "zip"))]
mod tests {
    use super::*;
    use super::super::api::{AppId, Client, HttpRequest, HttpResponse, HttpStream, Transport};
    use std::sync::Arc;

    fn temp() -> crate::library::TempDir {
        crate::library::TempDir::new(&std::env::temp_dir(), "nexus-install-test").unwrap()
    }
    fn archive(dir: &Path, files: &[(&str, &[u8])]) -> PathBuf {
        let path = dir.join("archive.zip");
        let mut writer = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
        for (name, bytes) in files {
            writer.start_file(*name, zip::write::SimpleFileOptions::default()).unwrap();
            writer.write_all(bytes).unwrap();
        }
        writer.finish().unwrap();
        path
    }

    #[test]
    fn archive_preflight_rejects_traversal_windows_devices_and_links() {
        let dir = temp();
        for name in ["../evil.dll", "/evil.dll", "a/../../evil.dll", "C:/evil.dll", "a\\..\\evil.dll", "a/NUL.dll", "a/file.dll."] {
            let path = archive(dir.path(), &[(name, b"MZ")]);
            assert!(check_zip(&path, &[]).is_err(), "{name}");
        }
        let path = dir.path().join("link.zip");
        let mut writer = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
        writer.add_symlink("evil.dll", "../../outside", zip::write::SimpleFileOptions::default()).unwrap();
        writer.finish().unwrap();
        assert_eq!(check_zip(&path, &[]).unwrap_err().code, ErrorCode::Refused);
    }

    #[test]
    fn archive_preflight_bounds_total_size_and_entry_count_without_a_manifest() {
        let dir = temp();
        let path = archive(dir.path(), &[("one.dll", b"123456"), ("two.dll", b"123456")]);
        assert_eq!(check_zip_bounded(&path, &[], 10, 10).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(check_zip_bounded(&path, &[], 20, 1).unwrap_err().code, ErrorCode::Refused);
        assert_eq!(check_zip_bounded(&path, &[], 12, 2).unwrap().files, 2);
    }

    #[test]
    fn archive_preflight_rejects_duplicate_portable_paths_and_missing_manifest_files() {
        let dir = temp();
        let path = archive(dir.path(), &[("One.dll", b"one"), ("one.DLL", b"two")]);
        assert_eq!(check_zip(&path, &[]).unwrap_err().code, ErrorCode::Refused);
        let path = archive(dir.path(), &[("one.dll", b"one")]);
        let manifest = [ManifestEntry { path: "missing.dll".into(), size: Some(3), sha256: None }];
        assert_eq!(check_zip(&path, &manifest).unwrap_err().code, ErrorCode::HashMismatch);
        let manifest = [ManifestEntry { path: "one.dll".into(), size: Some(3), sha256: Some("0".repeat(64)) }];
        assert_eq!(check_zip(&path, &manifest).unwrap_err().code, ErrorCode::HashMismatch);
    }

    struct Downloads(Vec<u8>);
    impl Transport for Downloads {
        fn send(&self, _: &HttpRequest) -> Result<HttpResponse> { unreachable!() }
        fn open(&self, _: &str, _: &[(String, String)]) -> Result<HttpStream> {
            Ok(HttpStream { status: 200, length: Some(self.0.len() as u64), reader: Box::new(std::io::Cursor::new(self.0.clone())) })
        }
    }

    struct WrongPage;
    impl Transport for WrongPage {
        fn send(&self, req: &HttpRequest) -> Result<HttpResponse> {
            let data = if req.url.ends_with("/mod-file-versions/20") {
                serde_json::json!({"id":"200", "file":{"id":"999", "name":"Wrong page"}})
            } else if req.url.ends_with("/mods/1") {
                serde_json::json!({"id":"42", "game_scoped_id":"1", "name":"Claimed page"})
            } else if req.url.ends_with("/mods/42/files") {
                serde_json::json!({"mod_files":[{"id":"43"}]})
            } else {
                panic!("Unexpected request before validation: {}", req.url);
            };
            Ok(HttpResponse { status: 200, headers: vec![], body: serde_json::to_vec(&serde_json::json!({"data":data})).unwrap() })
        }
        fn open(&self, _: &str, _: &[(String, String)]) -> Result<HttpStream> { panic!("invalid requests must not download") }
    }

    #[test]
    fn install_refuses_invalid_ids_self_install_and_a_file_from_another_mod() {
        use crate::game::{GameKind, PathMode, Platform};
        let dir = temp();
        let catalog = Catalog::new(dir.path().to_owned(), Client::new(Arc::new(WrongPage), Some("dummy-key".into()), AppId::kiln_launcher("test")).without_gap());
        let dirs = DataDirs::at(dir.path().join("data"));
        let game = GameInstall {
            id: "test".into(), kind: GameKind::Client, path: dir.path().join("game"), exe: "enshrouded.exe".into(), app_id: crate::CLIENT_APP_ID,
            library: None, platform: Platform::Proton, prefix: None, build_id: None, exe_sha256: None, steam_flavor: None,
            path_mode: PathMode::Copy, source: "manual".into(),
        };
        for mod_id in ["1/../../escape", "", "0", "18446744073709551616"] {
            let req = InstallRequest { mod_id: mod_id.into(), file_id: "20".into(), key: None, expires: None, source: "nxm".into() };
            assert_eq!(install(&catalog, &dirs, &game, &req, Some(false), &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::InvalidArgument);
        }
        let req = InstallRequest { mod_id: format!("0{}", catalog::KILN_MOD), file_id: "20".into(), key: None, expires: None, source: "nxm".into() };
        assert_eq!(install(&catalog, &dirs, &game, &req, Some(false), &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
        let req = InstallRequest { mod_id: "1".into(), ..req };
        assert_eq!(install(&catalog, &dirs, &game, &req, Some(false), &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
        assert!(!dir.path().join("downloads").exists());
    }

    #[test]
    fn concurrent_downloads_have_distinct_files_and_do_not_follow_old_part_symlinks() {
        let dir = temp();
        let catalog = Catalog::new(dir.path().to_owned(), Client::new(Arc::new(Downloads(b"MZ-test-bytes".to_vec())), None, AppId::kiln_launcher("test")));
        let downloads = dir.path().join("downloads");
        std::fs::create_dir_all(&downloads).unwrap();
        let victim = dir.path().join("victim");
        std::fs::write(&victim, b"keep me").unwrap();
        #[cfg(unix)]
        std::os::unix::fs::symlink(&victim, downloads.join(".same.dll.part")).unwrap();
        let results = std::thread::scope(|scope| {
            let a = scope.spawn(|| download(&catalog, "https://cdn.test/same.dll", &downloads, "same.dll", &mut crate::task::NoProgress).unwrap());
            let b = scope.spawn(|| download(&catalog, "https://cdn.test/same.dll", &downloads, "same.dll", &mut crate::task::NoProgress).unwrap());
            (a.join().unwrap(), b.join().unwrap())
        });
        assert_ne!(results.0.0, results.1.0);
        assert_eq!(std::fs::read(results.0.0).unwrap(), b"MZ-test-bytes");
        assert_eq!(std::fs::read(results.1.0).unwrap(), b"MZ-test-bytes");
        assert_eq!(std::fs::read(victim).unwrap(), b"keep me");
    }
}
