//! Server modpacks (contracts.md, "Modpacks"). Server side: build a deterministic zip of the mods players need
//! (manifest clients: mod), its sha256, publish the admin-entered URL into network.json. Client side: from
//! state/last-server.json -> consent (the user's explicit yes; https only by default) -> download -> zip sha256 ->
//! kiln-modpack.json -> every mod's kdh1 -> library import -> profile "Server: <name>" -> save backup -> activate.

use crate::game::GameInstall;
use crate::library::LibraryRef;
use crate::manifest::{Clients, ModKind, Side};
use crate::paths::DataDirs;
use crate::profile::Profile;
use crate::saves::SaveBackup;
use crate::state::RequiredMod;
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const MANIFEST: &str = "kiln-modpack.json";

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackServer {
    pub id: String,
    #[serde(default)]
    pub name: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackMod {
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Inside the zip: kiln/<id>, native/<id>, emf/<id>, plugin/<file>.dll
    pub path: String,
    /// kdh1 (sha256 for a plugin DLL).
    pub hash: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub clients: Option<Clients>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub side: Option<Side>,
}

/// `kiln-modpack.json` (schema/kiln-modpack.schema.json).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackManifest {
    pub format: u32,
    pub name: String,
    pub version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub created: Option<String>,
    pub server: ModpackServer,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub kiln: Option<String>,
    pub mods: Vec<ModpackMod>,
}

/// Where a modpack comes from (Tauri: `{url, sha256}` or `{path, sha256?}`).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", untagged)]
pub enum ModpackSource {
    Url { url: String, sha256: String },
    Path { path: PathBuf, #[serde(default)] sha256: Option<String> },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackPreview {
    pub manifest: Option<ModpackManifest>,
    pub server: crate::state::ServerRef,
    pub url: Option<String>,
    pub https: bool,
    pub size: Option<u64>,
    /// Required mods the library lacks (by kind/id/version/hash).
    pub missing: Vec<RequiredMod>,
    pub present: Vec<RequiredMod>,
    /// Native/plugin/EMF code in the pack (the consent text says "runs code on your PC" either way).
    pub native_code: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackInstallRequest {
    pub source: ModpackSource,
    /// Must be true: the user said yes in the consent modal. false -> ConsentRequired.
    pub consent: bool,
    /// Allow http:// (off by default; settings).
    #[serde(default)]
    pub allow_http: bool,
    /// Activate the new profile for the game after the import (default true).
    #[serde(default)]
    pub activate: Option<bool>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackInstallResult {
    pub profile: Profile,
    pub imported: Vec<LibraryRef>,
    pub backup: Option<SaveBackup>,
    pub activation: Option<crate::profile::Activation>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BuiltModpack {
    pub file: PathBuf,
    pub sha256: String,
    pub size: u64,
    pub manifest: ModpackManifest,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModpackPublishState {
    /// network.json modpack as configured.
    pub config: Option<crate::profile::ModpackRef>,
    pub built: Option<BuiltModpack>,
    /// What the server advertises (clients: mod mods with their kdh1).
    pub required: Vec<RequiredMod>,
}

/// Largest modpack the launcher downloads.
pub const MAX_DOWNLOAD: u64 = 2 * 1024 * 1024 * 1024;
/// The launcher's record of the last build, inside the server folder (Kiln.Network ignores it).
pub const BUILD_RECORD: &str = "kiln/data/kiln-network/modpack-build.json";
/// The server id file Kiln.Network generates (and this module, when the server never ran).
pub const SERVER_ID_FILE: &str = "kiln/data/kiln-network/server-id";

fn err(code: ErrorCode, msg: impl Into<String>) -> Error {
    Error::new(code, msg)
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
/// The manifest id rule (`^[a-z0-9][a-z0-9._-]{0,63}$`), for every mod kind.
fn mod_id_ok(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 64
        && id.bytes().next().is_some_and(|b| b.is_ascii_lowercase() || b.is_ascii_digit())
        && id.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'.' || b == b'_' || b == b'-')
}

// ================================================================================================= server side

/// A mod in a server folder that players need (manifest `clients: "mod"`, not switched off with `.disabled`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientMod {
    pub kind: ModKind,
    pub id: String,
    pub version: String,
    pub name: Option<String>,
    pub side: Side,
    pub dir: PathBuf,
}

/// The mods of `<server>/kiln/mods/*` and `<server>/kiln/native/*` that players need: the same rule as Kiln.Network's
/// advertised required[] (a managed mod that failed to load, per state/mods.json, is left out like the server does).
/// Also other engines' mods in each engine's server folder (`engine_mods::server_root`) whose `clients` is `mod`
/// (stated in their mod.json or Enshwick's pack.toml, else the kind's default: EML, ShroudForge, Flame and Enshwick mods
/// change what both sides load; shroudlink and Ember are server-only).
pub fn client_mods(server_dir: &Path) -> Result<Vec<ClientMod>> {
    let kiln = server_dir.join("kiln");
    let failed: std::collections::HashSet<String> = crate::state::read_mods(server_dir)
        .ok()
        .flatten()
        .map(|m| m.mods.into_iter().filter(|x| x.state == crate::state::ManagedState::Failed).map(|x| x.id).collect())
        .unwrap_or_default();
    let mut out = Vec::new();
    for (kind, folder) in [(ModKind::Kiln, "mods"), (ModKind::Native, "native")] {
        let root = kiln.join(folder);
        let Ok(rd) = std::fs::read_dir(&root) else { continue };
        let mut dirs: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
        dirs.sort();
        for dir in dirs {
            let name = dir.file_name().and_then(|n| n.to_str()).unwrap_or("");
            if name.starts_with('.') || dir.join(".disabled").exists() {
                continue;
            }
            let m = match kind {
                ModKind::Kiln => match crate::manifest::read_kiln(&dir) {
                    Ok(m) if m.clients == Clients::Mod && !failed.contains(&m.id) => ClientMod { kind, id: m.id, version: m.version, name: Some(m.name), side: m.side, dir },
                    _ => continue,
                },
                _ => match crate::manifest::read_native(&dir) {
                    Ok(m) if m.clients.unwrap_or_else(|| Clients::default_for(m.side)) == Clients::Mod => ClientMod { kind, id: m.id, version: m.version, name: m.name, side: m.side, dir },
                    _ => continue,
                },
            };
            out.push(m);
        }
    }
    for kind in ModKind::ALL.into_iter().filter(|k| k.is_engine()) {
        let Some(root) = crate::engine_mods::server_root(kind, server_dir) else { continue };
        let Ok(rd) = std::fs::read_dir(&root) else { continue };
        let mut dirs: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.is_dir()).collect();
        dirs.sort();
        for dir in dirs {
            let name = dir.file_name().and_then(|n| n.to_str()).unwrap_or("");
            if name.starts_with('.') || !crate::engine_mods::folder_is(kind, &dir) {
                continue;
            }
            let (side, clients) = crate::engine_mods::clients_of(kind, &dir);
            if clients != Clients::Mod || side == Side::Server {
                continue;
            }
            // ShroudForge: a package switched off in its extended.mod.json does not run
            if kind == ModKind::ShroudForge && crate::shroudforge::read_mod(&dir).is_ok_and(|m| !m.enabled) {
                continue;
            }
            let (id, version, name) = crate::engine_mods::identity(&dir);
            if !mod_id_ok(&id) {
                continue;
            }
            out.push(ClientMod { kind, id, version: version.unwrap_or_else(|| "0.0.0".into()), name, side, dir });
        }
    }
    out.sort_by(|a, b| (a.kind.as_str(), a.id.as_str()).cmp(&(b.kind.as_str(), b.id.as_str())));
    out.dedup_by(|a, b| a.kind == b.kind && a.id == b.id);
    Ok(out)
}

/// What the server advertises: each client mod's hash (`hash::of_mod`: a Kiln mod's identity, its files without what
/// the cook made in `assets/cooked`; the kdh1 of any other kind's folder).
pub fn required(server_dir: &Path) -> Result<Vec<RequiredMod>> {
    client_mods(server_dir)?
        .into_iter()
        .map(|m| Ok(RequiredMod { hash: crate::hash::of_mod(m.kind, &m.dir)?, id: m.id, version: m.version, kind: Some(m.kind), unknown_kind: None, name: m.name }))
        .collect()
}

/// `kiln/config/network.json` of a server (Kiln's relaxed JSON); an empty object when absent.
fn network_json(server_dir: &Path) -> Result<(PathBuf, String, serde_json::Value)> {
    let path = server_dir.join("kiln/config/network.json");
    match std::fs::read_to_string(&path) {
        Ok(text) => {
            let v = crate::admin::parse_jsonc(&text).map_err(|e| err(ErrorCode::Parse, format!("{}: {}", path.display(), e.message)))?;
            Ok((path, text, v))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok((path, "{}\n".into(), serde_json::json!({}))),
        Err(e) => Err(crate::util::io_at(&path, "reading")(e)),
    }
}

fn server_id_ok(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 64
        && s.bytes().next().is_some_and(|b| b.is_ascii_alphanumeric())
        && s.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'.' || b == b'_' || b == b'-')
}

/// The server's id and name as Kiln.Network computes them: network.json `serverId`, else the generated
/// `kiln/data/kiln-network/server-id` (written here in Kiln.Network's format when the server never ran);
/// network.json `serverName`, else enshrouded_server.json `name`.
pub fn server_identity(server_dir: &Path) -> Result<ModpackServer> {
    let (_, _, net) = network_json(server_dir)?;
    let id = match net.get("serverId").and_then(|v| v.as_str()).filter(|s| server_id_ok(s)) {
        Some(id) => id.to_string(),
        None => {
            let f = server_dir.join(SERVER_ID_FILE);
            match std::fs::read_to_string(&f).ok().map(|t| t.trim().to_string()).filter(|t| server_id_ok(t)) {
                Some(id) => id,
                None => {
                    let id = format!("srv-{}", crate::hash::hex(&crate::util::random_bytes::<6>()?));
                    crate::util::write_atomic(&f, format!("{id}\n").as_bytes())?;
                    id
                }
            }
        }
    };
    let name = net
        .get("serverName")
        .and_then(|v| v.as_str())
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
        .or_else(|| crate::server::game::read_config(&server_dir.join(crate::server::game::CONFIG)).ok().map(|c| c.name));
    Ok(ModpackServer { id, name })
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
/// Zip-relative file list of a mod folder with kdh1's rules (dot entries skipped, symlinks refused), sorted: the files
/// its hash counts. A Kiln mod's cook outputs (`assets/cooked`) are left out: they are made from the server's game
/// files, differ on a player's machine and are never shipped; each player's own cook makes them.
fn mod_files(kind: ModKind, dir: &Path) -> Result<Vec<(String, PathBuf)>> {
    fn walk(dir: &Path, rel: &str, out: &mut Vec<(String, PathBuf)>, skip_cooked: bool) -> Result<()> {
        for e in std::fs::read_dir(dir).map_err(crate::util::io_at(dir, "reading"))? {
            let e = e?;
            let name = e.file_name();
            if name.as_encoded_bytes().first() == Some(&b'.') {
                continue; // a dot entry is not packed, whatever its name is made of (as kdh1 skips it)
            }
            let name = name.to_str().ok_or_else(|| err(ErrorCode::Refused, format!("{}: name is not valid Unicode", e.path().display())))?;
            let nfc: String = unicode_normalization::UnicodeNormalization::nfc(name).collect();
            let r = if rel.is_empty() { nfc } else { format!("{rel}/{nfc}") };
            let ft = std::fs::symlink_metadata(e.path())?.file_type();
            if ft.is_symlink() {
                return Err(err(ErrorCode::Refused, format!("{}: symlinks are not packed", e.path().display())));
            } else if ft.is_dir() {
                if skip_cooked && r == crate::hash::COOKED {
                    continue;
                }
                walk(&e.path(), &r, out, skip_cooked)?;
            } else if ft.is_file() {
                out.push((r, e.path()));
            } else {
                return Err(err(ErrorCode::Refused, format!("{}: not a regular file", e.path().display())));
            }
        }
        Ok(())
    }
    let mut out = Vec::new();
    walk(dir, "", &mut out, kind == ModKind::Kiln && crate::hash::is_kiln_mod(dir))?;
    out.sort_by(|a, b| a.0.as_bytes().cmp(b.0.as_bytes()));
    Ok(out)
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
fn zip_path(kind: ModKind, id: &str) -> String {
    format!("{}/{id}", kind.as_str())
}

/// Deterministic zip of a server's client mods: `kiln-modpack.json` + `<kind>/<id>/...`, entries sorted bytewise,
/// one fixed time (the newest file's), mode 0644, deflate level 6. Same input, same bytes, same sha256.
#[cfg(feature = "zip")]
pub fn build(server_dir: &Path, out_dir: &Path, name: &str, version: &str, progress: &mut dyn Progress) -> Result<BuiltModpack> {
    use std::io::Write;
    if name.trim().is_empty() || name.len() > 128 || version.trim().is_empty() || version.len() > 64 {
        return Err(Error::invalid("modpack name: 1 to 128 characters; version: 1 to 64"));
    }
    let server = server_identity(server_dir)?;
    let mods = client_mods(server_dir)?;
    if mods.is_empty() {
        return Err(err(ErrorCode::NotFound, "this server has no mods players need (manifest clients: \"mod\")"));
    }
    let mut entries: Vec<(String, PathBuf)> = Vec::new();
    let mut listed = Vec::new();
    let mut newest = 0i64;
    for (i, m) in mods.iter().enumerate() {
        if progress.canceled() {
            return Err(err(ErrorCode::Canceled, "canceled"));
        }
        progress.report(Some(i as f32 / mods.len() as f32 * 0.5), &format!("hashing {}", m.id));
        let files = mod_files(m.kind, &m.dir)?;
        for (rel, p) in &files {
            if let Ok(t) = std::fs::metadata(p).and_then(|md| md.modified()) {
                newest = newest.max(crate::util::unix(t));
            }
            entries.push((format!("{}/{rel}", zip_path(m.kind, &m.id)), p.clone()));
        }
        listed.push(ModpackMod {
            kind: m.kind,
            id: m.id.clone(),
            version: m.version.clone(),
            name: m.name.clone(),
            path: zip_path(m.kind, &m.id),
            hash: crate::hash::of_mod(m.kind, &m.dir)?,
            clients: Some(Clients::Mod),
            side: Some(m.side),
        });
    }
    let kiln = crate::state::read_boot(server_dir).ok().flatten().map(|b| b.kiln).unwrap_or_else(|| crate::VERSION.to_string());
    let manifest = ModpackManifest {
        format: 1,
        name: name.trim().to_string(),
        version: version.trim().to_string(),
        created: Some(crate::util::rfc3339(newest.max(315_532_800))),
        server,
        kiln: Some(format!(">={kiln}")),
        mods: listed,
    };
    let mut manifest_bytes = serde_json::to_vec_pretty(&manifest)?;
    manifest_bytes.push(b'\n');
    std::fs::create_dir_all(out_dir).map_err(crate::util::io_at(out_dir, "creating"))?;
    let stem: String = format!("{}-{}", manifest.name, manifest.version)
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() || c == '-' || c == '.' || c == '_' { c.to_ascii_lowercase() } else { '-' })
        .collect();
    let file = out_dir.join(format!("{stem}.zip"));
    let tmp = crate::util::with_suffix(&file, ".tmp");
    let time = {
        let (y, mo, d, h, mi, s) = crate::util::utc_secs(newest.max(315_532_800));
        zip::DateTime::from_date_and_time(y.min(2107) as u16, mo as u8, d as u8, h as u8, mi as u8, s as u8).unwrap_or_else(|_| zip::DateTime::default_for_write())
    };
    let opts = zip::write::SimpleFileOptions::default()
        .compression_method(zip::CompressionMethod::Deflated)
        .compression_level(Some(6))
        .last_modified_time(time)
        .unix_permissions(0o644)
        .system(zip::System::Unix)
        .large_file(false);
    entries.sort_by(|a, b| a.0.as_bytes().cmp(b.0.as_bytes()));
    {
        let f = std::fs::File::create(&tmp).map_err(crate::util::io_at(&tmp, "writing"))?;
        let mut w = zip::ZipWriter::new(std::io::BufWriter::new(f));
        let zerr = |e: zip::result::ZipError| err(ErrorCode::Io, format!("writing {}: {e}", tmp.display()));
        // the manifest first (readers find it at once), then the mods' files sorted bytewise
        w.start_file(MANIFEST, opts).map_err(zerr)?;
        w.write_all(&manifest_bytes)?;
        let total = entries.len().max(1);
        for (i, (name, path)) in entries.iter().enumerate() {
            if progress.canceled() {
                drop(w);
                let _ = std::fs::remove_file(&tmp);
                return Err(err(ErrorCode::Canceled, "canceled"));
            }
            let data = std::fs::read(path).map_err(crate::util::io_at(path, "reading"))?;
            let o = if data.len() as u64 >= 0xFFFF_0000 { opts.large_file(true) } else { opts };
            w.start_file(name.as_str(), o).map_err(zerr)?;
            w.write_all(&data)?;
            if i % 16 == 0 {
                progress.report(Some(0.5 + i as f32 / total as f32 * 0.5), &format!("packing {name}"));
            }
        }
        let mut inner = w.finish().map_err(zerr)?;
        inner.flush()?;
        inner.into_inner().map_err(|e| Error::from(e.into_error()))?.sync_all()?;
    }
    crate::util::rename_replace(&tmp, &file)?;
    let (sha256, size) = crate::hash::sha256_file(&file)?;
    let built = BuiltModpack { file, sha256, size, manifest };
    crate::util::write_json_atomic(&server_dir.join(BUILD_RECORD), &built)?;
    progress.report(Some(1.0), &format!("built {} ({} mods, {} bytes)", built.file.display(), built.manifest.mods.len(), built.size));
    Ok(built)
}

#[cfg(not(feature = "zip"))]
pub fn build(server_dir: &Path, out_dir: &Path, name: &str, version: &str, progress: &mut dyn Progress) -> Result<BuiltModpack> {
    let _ = (server_dir, out_dir, name, version, progress);
    Err(err(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
}

fn check_url(url: &str, allow_http: bool) -> Result<()> {
    let lower = url.to_ascii_lowercase();
    if lower.starts_with("https://") {
        return Ok(());
    }
    if lower.starts_with("http://") {
        return if allow_http { Ok(()) } else { Err(err(ErrorCode::Refused, "only https:// modpack URLs are allowed (settings: allow http)")) };
    }
    Err(err(ErrorCode::Refused, format!("{url}: not an http(s) URL")))
}

/// Writes network.json `modpack {url, sha256, name, version, size}` for the built zip (only that value changes:
/// comments and the other keys stay), then asks a running server to re-read it (admin op network_reload).
pub fn publish(server_dir: &Path, built: &BuiltModpack, url: &str) -> Result<ModpackPublishState> {
    check_url(url, true)?;
    if url.len() > 2048 {
        return Err(Error::invalid("the URL is longer than 2048 characters"));
    }
    let (path, text, _) = network_json(server_dir)?;
    let value = serde_json::to_value(crate::profile::ModpackRef {
        url: url.to_string(),
        sha256: built.sha256.clone(),
        name: Some(built.manifest.name.clone()),
        version: Some(built.manifest.version.clone()),
        size: Some(built.size),
    })?;
    let edited = crate::jsonc::set_value(&text, &crate::jsonc::split_path("modpack"), &value)?;
    crate::util::write_atomic(&path, edited.as_bytes())?;
    // a running server picks it up now (else at its next start)
    if let Ok(c) = crate::admin::client::AdminClient::connect_dir(server_dir, "publish", crate::admin::client::Transport::Auto) {
        let _ = c.call("network_reload", serde_json::Map::new());
        c.shutdown();
    }
    publish_state(server_dir)
}

/// network.json's modpack, the last build, and what the server advertises (computed from its folders).
pub fn publish_state(server_dir: &Path) -> Result<ModpackPublishState> {
    let (_, _, net) = network_json(server_dir)?;
    let config = net.get("modpack").and_then(|m| serde_json::from_value::<crate::profile::ModpackRef>(m.clone()).ok());
    let built: Option<BuiltModpack> = crate::util::read_json(&server_dir.join(BUILD_RECORD)).ok().flatten();
    Ok(ModpackPublishState { config, built, required: required(server_dir)? })
}

// ================================================================================================= client side

fn library_has(dirs: &DataDirs, r: &RequiredMod) -> bool {
    if r.unknown_kind.is_some() { return false; }
    let kind = r.kind.unwrap_or(ModKind::Kiln);
    crate::library::list(dirs, Some(kind))
        .map(|all| all.iter().any(|e| e.kind == kind && e.id == r.id && e.versions.iter().any(|v| v.hash == r.hash)))
        .unwrap_or(false)
}

/// Preview from last-server.json data, a URL or a local zip. A URL is never fetched here: the advertised data
/// (last-server.json) describes it. A local zip is verified and listed.
pub fn inspect(dirs: &DataDirs, source: &ModpackSource, last: Option<&crate::state::LastServer>) -> Result<ModpackPreview> {
    let (manifest, url, size) = match source {
        ModpackSource::Url { url, .. } => (None, Some(url.clone()), last.and_then(|l| l.modpack.as_ref()).and_then(|m| m.size)),
        ModpackSource::Path { path, sha256 } => {
            let m = verify(path, sha256.as_deref())?;
            (Some(m), None, std::fs::metadata(path).ok().map(|m| m.len()))
        }
    };
    let required: Vec<RequiredMod> = match (&manifest, last) {
        (Some(m), _) => m.mods.iter().map(|x| RequiredMod { id: x.id.clone(), version: x.version.clone(), hash: x.hash.clone(), kind: Some(x.kind), unknown_kind: None, name: x.name.clone() }).collect(),
        (None, Some(l)) => l.required.clone(),
        (None, None) => vec![],
    };
    let (present, missing): (Vec<RequiredMod>, Vec<RequiredMod>) = required.into_iter().partition(|r| library_has(dirs, r));
    let native_code = manifest.as_ref().is_some_and(|m| m.mods.iter().any(|x| x.kind != ModKind::Kiln))
        || missing.iter().chain(&present).any(|r| r.kind.unwrap_or(ModKind::Kiln) != ModKind::Kiln);
    let server = match (&manifest, last) {
        (_, Some(l)) => l.server.clone(),
        (Some(m), None) => crate::state::ServerRef { id: Some(m.server.id.clone()), name: m.server.name.clone(), address: None, kiln: None },
        (None, None) => crate::state::ServerRef::default(),
    };
    Ok(ModpackPreview { manifest, server, https: url.as_deref().is_some_and(|u| u.to_ascii_lowercase().starts_with("https://")), url, size, missing, present, native_code })
}

/// Downloads `url` into `dest` (https only unless `allow_http`; at most 2 GB), checking the sha256 as it streams.
/// Progress through `progress`; canceled downloads leave nothing behind.
#[cfg(feature = "http")]
pub fn download(url: &str, sha256: &str, dest: &Path, allow_http: bool, progress: &mut dyn Progress) -> Result<PathBuf> {
    use sha2::Digest;
    use std::io::{Read, Write};
    check_url(url, allow_http)?;
    let want = sha256.to_ascii_lowercase();
    if want.len() != 64 || !want.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(Error::invalid("the modpack sha256 must be 64 hex characters"));
    }
    let agent: ureq::Agent = ureq::Agent::config_builder()
        .https_only(!allow_http)
        .timeout_connect(Some(std::time::Duration::from_secs(20)))
        .timeout_recv_response(Some(std::time::Duration::from_secs(60)))
        .max_redirects(5)
        .user_agent(format!("kiln-launcher/{}", crate::VERSION))
        .build()
        .into();
    progress.report(None, &format!("connecting to {url}"));
    let mut resp = agent.get(url).call().map_err(|e| err(ErrorCode::Network, format!("downloading {url}: {e}")))?;
    let total = resp.body().content_length();
    if total.is_some_and(|n| n > MAX_DOWNLOAD) {
        return Err(err(ErrorCode::Refused, format!("the modpack is {} bytes, over the 2 GB limit", total.unwrap_or(0))));
    }
    let part = crate::util::with_suffix(dest, ".part");
    if let Some(d) = dest.parent() {
        std::fs::create_dir_all(d).map_err(crate::util::io_at(d, "creating"))?;
    }
    let result = (|| -> Result<()> {
        let mut f = std::fs::File::create(&part).map_err(crate::util::io_at(&part, "writing"))?;
        let mut r = resp.body_mut().with_config().limit(MAX_DOWNLOAD + 1).reader();
        let mut h = sha2::Sha256::new();
        let mut buf = vec![0u8; 256 * 1024];
        let mut got = 0u64;
        let mut last = std::time::Instant::now();
        loop {
            if progress.canceled() {
                return Err(err(ErrorCode::Canceled, "download canceled"));
            }
            let n = r.read(&mut buf).map_err(|e| err(ErrorCode::Network, format!("downloading {url}: {e}")))?;
            if n == 0 {
                break;
            }
            got += n as u64;
            if got > MAX_DOWNLOAD {
                return Err(err(ErrorCode::Refused, "the modpack is over the 2 GB limit"));
            }
            h.update(&buf[..n]);
            f.write_all(&buf[..n])?;
            if last.elapsed() > std::time::Duration::from_millis(150) {
                last = std::time::Instant::now();
                progress.report(total.map(|t| got as f32 / t.max(1) as f32), &format!("downloaded {}", crate::util::human_size(got)));
            }
        }
        f.sync_all()?;
        let have = crate::hash::hex(h.finalize().as_slice());
        if have != want {
            return Err(err(ErrorCode::HashMismatch, format!("the download's sha256 is {have}, the server advertises {want}")));
        }
        Ok(())
    })();
    if let Err(e) = result {
        let _ = std::fs::remove_file(&part);
        return Err(e);
    }
    crate::util::rename_replace(&part, dest)?;
    progress.report(Some(1.0), "downloaded");
    Ok(dest.to_path_buf())
}

#[cfg(not(feature = "http"))]
pub fn download(url: &str, sha256: &str, dest: &Path, allow_http: bool, progress: &mut dyn Progress) -> Result<PathBuf> {
    let _ = (url, sha256, dest, allow_http, progress);
    Err(err(ErrorCode::Unsupported, "kiln-manage was built without the http feature"))
}

#[cfg_attr(not(feature = "zip"), allow(dead_code))]
fn valid_rel(p: &str) -> bool {
    crate::bundle::safe_rel_path(p).is_ok()
}

/// sha256 of the zip (when given), kiln-modpack.json (format, mods unique, paths `<kind>/<id>`), every mod's hash
/// over its entries (kdh1; a Kiln mod's identity); refuses symlink entries, paths outside the listed folders, absolute
/// or `..` paths, and a Kiln mod that carries cook outputs (`assets/cooked/`: no hash covers them, and they are made
/// on each machine).
#[cfg(feature = "zip")]
pub fn verify(zip_file: &Path, sha256: Option<&str>) -> Result<ModpackManifest> {
    use std::io::Read;
    if let Some(want) = sha256 {
        let (have, _) = crate::hash::sha256_file(zip_file)?;
        if !have.eq_ignore_ascii_case(want) {
            return Err(err(ErrorCode::HashMismatch, format!("{}: sha256 {have}, expected {want}", zip_file.display())));
        }
    }
    crate::quick::validate_import_path(zip_file)?;
    let f = std::fs::File::open(zip_file).map_err(crate::util::io_at(zip_file, "opening"))?;
    let zerr = |e: zip::result::ZipError| err(ErrorCode::Parse, format!("{}: {e}", zip_file.display()));
    let mut a = zip::ZipArchive::new(f).map_err(zerr)?;
    let manifest: ModpackManifest = {
        let e = a.by_name(MANIFEST).map_err(|_| err(ErrorCode::Schema, format!("{}: no {MANIFEST}", zip_file.display())))?;
        let mut b = Vec::new();
        e.take(4 << 20).read_to_end(&mut b)?;
        serde_json::from_slice(&b).map_err(|e| err(ErrorCode::Schema, format!("{MANIFEST}: {e}")))?
    };
    if manifest.format != 1 {
        return Err(err(ErrorCode::Unsupported, format!("{MANIFEST} format {} is newer than this launcher", manifest.format)));
    }
    if manifest.name.is_empty() || manifest.version.is_empty() || !server_id_ok(&manifest.server.id) {
        return Err(err(ErrorCode::Schema, format!("{MANIFEST}: name, version and server.id are required")));
    }
    let mut seen = std::collections::HashSet::new();
    for m in &manifest.mods {
        let expect = if m.kind == ModKind::Plugin { None } else { Some(zip_path(m.kind, &m.id)) };
        let path_ok = match &expect {
            Some(p) => &m.path == p,
            None => m.path.starts_with("plugin/") && m.path.to_ascii_lowercase().ends_with(".dll") && valid_rel(&m.path),
        };
        if !path_ok || !mod_id_ok(&m.id) || crate::semver::SemVersion::parse(&m.version).is_err() || !seen.insert((m.kind, m.id.clone())) {
            return Err(err(ErrorCode::Schema, format!("{MANIFEST}: mod {} has a bad or duplicate path {}", m.id, m.path)));
        }
    }
    // Hash every mod from its entries; limits are rechecked while streaming as well as at archive preflight.
    let mut expanded = 0u64;
    let mut paths = std::collections::HashSet::new();
    let mut records: std::collections::BTreeMap<String, Vec<crate::hash::Record>> = Default::default();
    // Kiln mods with an entry below assets/cooked/ (the first one each)
    let mut cooked: std::collections::BTreeMap<String, String> = Default::default();
    for i in 0..a.len() {
        let mut e = a.by_index(i).map_err(zerr)?;
        let name = e.name().map_err(zerr)?.to_string();
        let rel = name.trim_end_matches('/');
        if !valid_rel(rel) || e.enclosed_name().is_none() || !paths.insert(rel.to_ascii_lowercase()) {
            return Err(err(ErrorCode::Refused, format!("{}: entry {name:?} has an unsafe path", zip_file.display())));
        }
        if e.is_symlink() || e.unix_mode().is_some_and(|m| m & 0o170000 == 0o120000) {
            return Err(err(ErrorCode::Refused, format!("{}: entry {name} is a symlink", zip_file.display())));
        }
        if e.is_dir() || name == MANIFEST {
            continue;
        }
        let owner = manifest.mods.iter().find(|m| if m.kind == ModKind::Plugin { name == m.path } else { name.starts_with(&format!("{}/", m.path)) });
        let Some(owner) = owner else {
            return Err(err(ErrorCode::Refused, format!("{}: entry {name} belongs to no listed mod", zip_file.display())));
        };
        let mut h = <sha2::Sha256 as sha2::Digest>::new();
        let mut buf = vec![0u8; 256 * 1024];
        let mut size = 0u64;
        loop {
            let n = e.read(&mut buf)?;
            if n == 0 {
                break;
            }
            size += n as u64;
            expanded += n as u64;
            if size > crate::quick::MAX_IMPORT_FILE || expanded > crate::quick::MAX_IMPORT_BYTES {
                return Err(err(ErrorCode::Refused, "modpack exceeds the import size limits"));
            }
            sha2::Digest::update(&mut h, &buf[..n]);
        }
        let sha = crate::hash::hex(sha2::Digest::finalize(h).as_slice());
        let rel = if owner.kind == ModKind::Plugin { String::new() } else { name[owner.path.len() + 1..].to_string() };
        if owner.kind == ModKind::Kiln && crate::hash::key_of(&rel).as_deref().is_some_and(crate::hash::is_cooked_key) {
            cooked.entry(owner.id.clone()).or_insert_with(|| name.clone());
        }
        records.entry(owner.path.clone()).or_default().push(crate::hash::Record { path: if rel.is_empty() { name.clone() } else { rel }, size, sha256: sha });
    }
    for m in &manifest.mods {
        let rs = records.remove(&m.path).unwrap_or_default();
        // A Kiln mod's cook outputs are no part of it and of no hash. A pack built before 0.2.0-dev.27 from a cooked
        // folder carries them (and names the hash of the folder with them): refused with what to do, not as a bad hash.
        if m.kind == ModKind::Kiln && rs.iter().any(|r| crate::hash::key_of(&r.path).as_deref() == Some(crate::hash::MOD_MANIFEST))
            && let Some(entry) = cooked.get(&m.id) {
            return Err(err(ErrorCode::Refused, format!(
                "this modpack carries cooked files of {} ({entry}): they are made on each machine from its own game and are not part of a modpack.                  It was built by an older Kiln: the server's operator has to build it again with Kiln 0.2.0-dev.27 or newer.", m.id)));
        }
        let have = if m.kind == ModKind::Plugin {
            rs.first().map(|r| r.sha256.clone()).unwrap_or_default()
        } else {
            crate::hash::of_mod_records(m.kind, rs)?
        };
        if have != m.hash {
            return Err(err(ErrorCode::HashMismatch, format!("mod {} in the modpack hashes to {have}, its manifest says {}", m.id, m.hash)));
        }
    }
    Ok(manifest)
}

#[cfg(not(feature = "zip"))]
pub fn verify(zip_file: &Path, sha256: Option<&str>) -> Result<ModpackManifest> {
    let _ = (zip_file, sha256);
    Err(err(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
}

/// Unpacks every mod of a verified modpack into `<staging>/<kind>/<id>/` (plugins: `<staging>/plugin/<file>`) and
/// checks each folder's kdh1 on disk again. Returns the extracted paths in manifest order.
#[cfg(feature = "zip")]
pub fn extract(zip_file: &Path, manifest: &ModpackManifest, staging: &Path) -> Result<Vec<PathBuf>> {
    // Reverify the bytes we will extract, including the manifest: callers may retain a preview while the zip changes.
    let current = verify(zip_file, None)?;
    if current != *manifest {
        return Err(err(ErrorCode::HashMismatch, "the modpack changed after it was inspected"));
    }
    // The staging folder is the managed root: it and everything below it must be real folders.
    crate::engine_mods::refuse_symlink_path(staging, staging)?;
    std::fs::create_dir_all(staging).map_err(crate::util::io_at(staging, "creating"))?;
    let unpacked = crate::library::unpack_to_temp(zip_file, staging)?;
    for m in &manifest.mods {
        let to = staging.join(&m.path);
        crate::engine_mods::refuse_symlink_path(staging, &to)?;
        let from = unpacked.path().join(&m.path);
        if m.kind == ModKind::Plugin {
            if let Some(p) = to.parent() {
                std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?;
            }
            std::fs::copy(&from, &to).map_err(crate::util::io_at(&to, "copying"))?;
        } else {
            crate::library::copy_mod_tree(&from, &to)?;
        }
    }
    let mut out = Vec::new();
    for m in &manifest.mods {
        let p = staging.join(&m.path);
        let have = if m.kind == ModKind::Plugin { crate::hash::sha256_file(&p)?.0 } else { crate::hash::of_mod(m.kind, &p)? };
        if have != m.hash {
            return Err(err(ErrorCode::HashMismatch, format!("mod {} unpacked to {have}, expected {}", m.id, m.hash)));
        }
        out.push(p);
    }
    Ok(out)
}

#[cfg(not(feature = "zip"))]
pub fn extract(zip_file: &Path, manifest: &ModpackManifest, staging: &Path) -> Result<Vec<PathBuf>> {
    let _ = (zip_file, manifest, staging);
    Err(err(ErrorCode::Unsupported, "kiln-manage was built without the zip feature"))
}

/// The required mods the server advertised must all be in the modpack with the same hash (a modpack that does not
/// carry what the server checks would refuse the player again).
pub fn check_against(manifest: &ModpackManifest, required: &[RequiredMod]) -> Result<()> {
    let bad: Vec<String> = required
        .iter()
        .filter(|r| r.unknown_kind.is_some() || !manifest.mods.iter().any(|m| m.kind == r.kind.unwrap_or(ModKind::Kiln) && m.id == r.id && m.hash == r.hash))
        .map(|r| format!("{} {} ({})", r.id, r.version, r.hash))
        .collect();
    if bad.is_empty() {
        Ok(())
    } else {
        Err(err(ErrorCode::HashMismatch, format!("the modpack lacks what the server requires: {}", bad.join(", "))).with_detail(serde_json::json!(bad)))
    }
}

/// Consent -> download (or a local zip) -> zip sha256 -> kiln-modpack.json -> each mod's kdh1 -> library import ->
/// profile "Server: <name>" bound to the server -> save backup (reason modpack) -> activation (unless
/// `activate: false`). Nothing is fetched without `consent: true`.
pub fn install(dirs: &DataDirs, game: &GameInstall, request: &ModpackInstallRequest, progress: &mut dyn Progress) -> Result<ModpackInstallResult> {
    if !request.consent {
        return Err(err(ErrorCode::ConsentRequired, "installing a modpack runs code from the server on this PC: the user must agree first"));
    }
    let last = crate::state::read_last_server(&game.path).ok().flatten();
    let work = dirs.root.join("modpacks");
    let (zip_file, sha) = match &request.source {
        ModpackSource::Url { url, sha256 } => {
            let dest = work.join("downloads").join(format!("{}.zip", sha256.to_ascii_lowercase()));
            let cached = dest.exists() && crate::hash::sha256_file(&dest).map(|(h, _)| h.eq_ignore_ascii_case(sha256)).unwrap_or(false);
            if !cached {
                download(url, sha256, &dest, request.allow_http, &mut Scaled { inner: progress, from: 0.0, to: 0.6 })?;
            }
            (dest, Some(sha256.clone()))
        }
        ModpackSource::Path { path, sha256 } => (path.clone(), sha256.clone()),
    };
    progress.report(Some(0.62), "checking the modpack");
    let manifest = verify(&zip_file, sha.as_deref())?;
    // what the server checks at the join must be in the pack (only when last-server.json is about this server)
    if let Some(l) = last.as_ref().filter(|l| l.server.id.as_deref() == Some(manifest.server.id.as_str())) {
        if !l.truncated {
            check_against(&manifest, &l.required)?;
        }
    }
    if progress.canceled() {
        return Err(err(ErrorCode::Canceled, "canceled"));
    }
    let staging = work.join("staging").join(crate::hash::hex(&crate::util::random_bytes::<6>()?));
    let paths = extract(&zip_file, &manifest, &staging)?;
    progress.report(Some(0.7), "importing into the library");
    let report = crate::library::import(dirs, &paths, &mut Scaled { inner: progress, from: 0.7, to: 0.85 });
    let _ = std::fs::remove_dir_all(&staging);
    let report = report?;
    let mut refs: Vec<LibraryRef> = report.imported.iter().chain(&report.existing).cloned().collect();
    refs.sort_by(|a, b| (a.kind.as_str(), a.id.as_str()).cmp(&(b.kind.as_str(), b.id.as_str())));
    for m in &manifest.mods {
        if !refs.iter().any(|r| r.kind == m.kind && r.id == m.id && r.hash == m.hash) {
            return Err(err(ErrorCode::Internal, format!("the library did not take mod {} ({})", m.id, m.hash)).with_detail(serde_json::to_value(&report.skipped).unwrap_or_default()));
        }
    }
    // the profile bound to this server: reuse it (same server id), else "Server: <name>"
    progress.report(Some(0.87), "setting up the profile");
    let name = manifest.server.name.clone().or_else(|| last.as_ref().and_then(|l| l.server.name.clone())).unwrap_or_else(|| manifest.name.clone());
    let existing = crate::profile::list(dirs)?.into_iter().find(|p| p.server.as_ref().is_some_and(|s| s.id == manifest.server.id));
    let mut profile = match existing {
        Some(p) => crate::profile::get(dirs, &p.id)?,
        None => crate::profile::create(dirs, &format!("Server: {name}"), None)?,
    };
    let entry = |m: &ModpackMod| crate::profile::ProfileMod { id: m.id.clone(), version: m.version.clone(), hash: m.hash.clone(), enabled: true };
    let mut mods = crate::profile::ProfileMods::default();
    for m in &manifest.mods {
        mods.list_mut(m.kind).push(entry(m));
    }
    profile.mods = mods;
    let modpack = match &request.source {
        ModpackSource::Url { url, sha256 } => Some(crate::profile::ModpackRef {
            url: url.clone(),
            sha256: sha256.clone(),
            name: Some(manifest.name.clone()),
            version: Some(manifest.version.clone()),
            size: std::fs::metadata(&zip_file).ok().map(|m| m.len()),
        }),
        ModpackSource::Path { .. } => None,
    };
    profile.server = Some(crate::profile::BoundServer {
        id: manifest.server.id.clone(),
        name: Some(name),
        address: last.as_ref().filter(|l| l.server.id.as_deref() == Some(manifest.server.id.as_str())).and_then(|l| l.server.address.clone()),
        modpack,
    });
    let profile = crate::profile::save(dirs, &profile)?;
    let mut backup = None;
    let mut activation = None;
    if request.activate != Some(false) {
        progress.report(Some(0.92), "backing up saves");
        backup = Some(crate::saves::backup(dirs, game, crate::saves::BackupReason::Modpack, Some(&format!("before modpack {} {}", manifest.name, manifest.version)))?);
        progress.report(Some(0.96), "activating the profile");
        activation = Some(crate::profile::activate(dirs, &profile.id, game)?);
    }
    progress.report(Some(1.0), &format!("modpack {} {} installed as profile {}", manifest.name, manifest.version, profile.name));
    Ok(ModpackInstallResult { profile, imported: report.imported, backup, activation })
}

/// Maps a sub-step's 0..1 into `from..to` of the whole task.
struct Scaled<'a> {
    inner: &'a mut dyn Progress,
    from: f32,
    to: f32,
}

impl Progress for Scaled<'_> {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        self.inner.report(fraction.map(|f| self.from + f.clamp(0.0, 1.0) * (self.to - self.from)), message)
    }
    fn canceled(&self) -> bool {
        self.inner.canceled()
    }
}

#[cfg(all(test, feature = "zip"))]
mod tests {
    use super::*;

    fn temp(tag: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("kiln-modpack-{tag}-{}-{}", std::process::id(), crate::hash::hex(&crate::util::random_bytes::<4>().unwrap())));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    fn server() -> PathBuf {
        let s = temp("server");
        let m = s.join("kiln/mods/isle-icons");
        std::fs::create_dir_all(m.join("icons")).unwrap();
        std::fs::write(m.join("kiln.mod.json"), r#"{"id":"isle-icons","name":"Isle Icons","version":"1.1.0","apiVersion":"0.2","entryAssembly":"IsleIcons.dll","side":"both"}"#).unwrap();
        std::fs::write(m.join("IsleIcons.dll"), b"MZ fake").unwrap();
        std::fs::write(m.join("icons/a.png"), b"png").unwrap();
        std::fs::write(m.join(".kiln-source.json"), b"{}").unwrap();
        let srv = s.join("kiln/mods/admin-only");
        std::fs::create_dir_all(&srv).unwrap();
        std::fs::write(srv.join("kiln.mod.json"), r#"{"id":"admin-only","name":"x","version":"1.0.0","apiVersion":"0.2","entryAssembly":"A.dll"}"#).unwrap();
        let off = s.join("kiln/mods/off");
        std::fs::create_dir_all(&off).unwrap();
        std::fs::write(off.join("kiln.mod.json"), r#"{"id":"off","name":"x","version":"1.0.0","apiVersion":"0.2","entryAssembly":"A.dll","side":"client"}"#).unwrap();
        std::fs::write(off.join(".disabled"), b"").unwrap();
        let n = s.join("kiln/native/fps");
        std::fs::create_dir_all(&n).unwrap();
        std::fs::write(n.join("native.json"), r#"{"id":"fps","version":"2.0.0","dlls":["fps.dll"]}"#).unwrap();
        std::fs::write(n.join("fps.dll"), b"MZ").unwrap();
        std::fs::create_dir_all(s.join("kiln/config")).unwrap();
        std::fs::write(s.join("kiln/config/network.json"), "{\n  // mod sync\n  \"modSync\": \"refuse\",\n  \"serverName\": \"Ember Isles\",\n}\n").unwrap();
        s
    }

    #[test]
    fn build_is_deterministic_and_verifies() {
        let s = server();
        let out = temp("out");
        let mods = client_mods(&s).unwrap();
        assert_eq!(mods.iter().map(|m| (m.kind, m.id.as_str())).collect::<Vec<_>>(), [(ModKind::Kiln, "isle-icons"), (ModKind::Native, "fps")]);
        let a = build(&s, &out, "Ember Isles", "3", &mut crate::task::NoProgress).unwrap();
        let first = std::fs::read(&a.file).unwrap();
        let b = build(&s, &out, "Ember Isles", "3", &mut crate::task::NoProgress).unwrap();
        assert_eq!(a.sha256, b.sha256, "same input, same zip");
        assert_eq!(first, std::fs::read(&b.file).unwrap());
        assert_eq!(a.manifest.server.name.as_deref(), Some("Ember Isles"));
        assert!(s.join(SERVER_ID_FILE).exists(), "server id generated");
        let m = verify(&a.file, Some(&a.sha256)).unwrap();
        assert_eq!(m, a.manifest);
        assert_eq!(m.mods[0].hash, crate::hash::dirhash(&s.join("kiln/mods/isle-icons")).unwrap());
        assert_eq!(verify(&a.file, Some(&"0".repeat(64))).unwrap_err().code, ErrorCode::HashMismatch);
        // the advertised list equals the manifest's
        let req = required(&s).unwrap();
        check_against(&m, &req).unwrap();
        let mut wrong = req.clone();
        wrong[0].hash = format!("kdh1:{}", "f".repeat(64));
        assert_eq!(check_against(&m, &wrong).unwrap_err().code, ErrorCode::HashMismatch);
        // extract re-checks kdh1 on disk
        let st = temp("staging");
        let paths = extract(&a.file, &m, &st).unwrap();
        assert_eq!(crate::hash::dirhash(&paths[0]).unwrap(), m.mods[0].hash);
        // publish keeps comments and the other keys
        let state = publish(&s, &a, "https://example.org/ember-isles-3.zip").unwrap();
        let text = std::fs::read_to_string(s.join("kiln/config/network.json")).unwrap();
        assert!(text.contains("// mod sync") && text.contains("\"modSync\": \"refuse\""), "{text}");
        assert_eq!(state.config.as_ref().unwrap().sha256, a.sha256);
        assert_eq!(state.built.as_ref().unwrap().sha256, a.sha256);
        assert_eq!(state.required.len(), 2);
        assert_eq!(publish(&s, &a, "ftp://x").unwrap_err().code, ErrorCode::Refused);
        for d in [s, out, st] {
            let _ = std::fs::remove_dir_all(d);
        }
    }

    /// What a cook leaves in a Kiln mod's folder: the shipped manifest names the cooked files, the cook writes them and
    /// its report into assets/cooked. A server's cook makes no models and its report names the server's files.
    fn cook(mod_dir: &Path, side: &str) {
        let a = mod_dir.join("assets");
        std::fs::create_dir_all(a.join("cooked/impact")).unwrap();
        std::fs::write(a.join("cooked/clip_wave.bin"), b"clip").unwrap();
        std::fs::write(a.join("cooked/impact/heal.bin"), b"program").unwrap();
        if side == "client" {
            std::fs::write(a.join("cooked/sign.bin"), b"model").unwrap();
            std::fs::write(a.join("cooked/sign.blob"), b"render data").unwrap();
        }
        std::fs::write(a.join("cooked/cook-report.json"), format!(r#"{{ "game": {{ "kfc": "/games/{side}/enshrouded.kfc", "side": "{side}" }} }}"#)).unwrap();
    }

    fn zip_names(file: &Path) -> Vec<String> {
        let mut a = zip::ZipArchive::new(std::fs::File::open(file).unwrap()).unwrap();
        (0..a.len()).map(|i| a.by_index(i).unwrap().name().unwrap().to_string()).collect()
    }

    /// A copy of `file` with one more entry.
    fn zip_with(file: &Path, to: &Path, name: &str, data: &[u8]) {
        use std::io::Write;
        let mut src = zip::ZipArchive::new(std::fs::File::open(file).unwrap()).unwrap();
        let mut w = zip::ZipWriter::new(std::fs::File::create(to).unwrap());
        for i in 0..src.len() {
            let e = src.by_index(i).unwrap();
            w.raw_copy_file(e).unwrap();
        }
        w.start_file(name, zip::write::SimpleFileOptions::default()).unwrap();
        w.write_all(data).unwrap();
        w.finish().unwrap();
    }

    #[test]
    fn a_cooked_mod_is_packed_without_its_cook_outputs_and_is_the_mod_the_server_advertises() {
        let s = server();
        let out = temp("cooked-out");
        let m = s.join("kiln/mods/isle-icons");
        std::fs::create_dir_all(m.join("assets")).unwrap();
        std::fs::write(m.join("assets/cook.json"), br#"{ "format": 1, "out": "cooked", "steps": [] }"#).unwrap();
        std::fs::write(m.join("assets/assets.json"), br#"{ "format": 1, "entries": [ { "kind": "blob", "file": "cooked/sign.blob" } ] }"#).unwrap();
        let shipped = crate::hash::dirhash(&m).unwrap();
        cook(&m, "server");
        // a native mod's folder is all its own: a folder named assets/cooked there is packed and counted
        let n = s.join("kiln/native/fps");
        std::fs::create_dir_all(n.join("assets/cooked")).unwrap();
        std::fs::write(n.join("assets/cooked/table.bin"), b"its own data").unwrap();
        assert_ne!(crate::hash::dirhash(&m).unwrap(), shipped, "the cook changed the folder");
        let req = required(&s).unwrap();
        assert_eq!((req[0].id.as_str(), req[0].hash.as_str()), ("isle-icons", shipped.as_str()), "advertised: the mod without its cook outputs");
        assert_eq!(req[1].hash, crate::hash::dirhash(&n).unwrap());

        let built = build(&s, &out, "Ember Isles", "4", &mut crate::task::NoProgress).unwrap();
        let names = zip_names(&built.file);
        assert!(names.iter().any(|n| n == "kiln/isle-icons/assets/assets.json") && names.iter().any(|n| n == "kiln/isle-icons/assets/cook.json"), "{names:?}");
        assert!(!names.iter().any(|n| n.starts_with("kiln/isle-icons/assets/cooked")), "no cook output is packed: {names:?}");
        assert!(names.iter().any(|n| n == "native/fps/assets/cooked/table.bin"), "{names:?}");
        let man = verify(&built.file, Some(&built.sha256)).unwrap();
        assert_eq!(man.mods[0].hash, shipped);
        check_against(&man, &req).unwrap();

        // what a player gets from the pack, cooked on the player's machine (other files, another report), is that mod
        let st = temp("cooked-staging");
        let paths = extract(&built.file, &man, &st).unwrap();
        assert!(!paths[0].join("assets/cooked").exists());
        assert_eq!(crate::hash::dirhash(&paths[0]).unwrap(), shipped);
        cook(&paths[0], "client");
        assert_ne!(crate::hash::dirhash(&paths[0]).unwrap(), crate::hash::dirhash(&m).unwrap(), "the two cooked folders differ");
        assert_eq!(crate::hash::of_mod(ModKind::Kiln, &paths[0]).unwrap(), req[0].hash);
        // and a pack built from the player's cooked copy would be the same pack entry
        assert_eq!(mod_files(ModKind::Kiln, &paths[0]).unwrap().iter().map(|f| f.0.clone()).collect::<Vec<_>>(),
            mod_files(ModKind::Kiln, &m).unwrap().iter().map(|f| f.0.clone()).collect::<Vec<_>>());
        // the same build again after the server cooked again (a newer report): the same zip, byte for byte
        let first = std::fs::read(&built.file).unwrap();
        std::fs::write(m.join("assets/cooked/cook-report.json"), b"{ \"again\": true }").unwrap();
        std::fs::write(m.join("assets/cooked/clip_wave.bin"), b"another clip").unwrap();
        let again = build(&s, &out, "Ember Isles", "4", &mut crate::task::NoProgress).unwrap();
        assert_eq!(again.manifest.mods, built.manifest.mods);
        assert_eq!((again.sha256.as_str(), std::fs::read(&again.file).unwrap()), (built.sha256.as_str(), first));
        for d in [s, out, st] {
            let _ = std::fs::remove_dir_all(d);
        }
    }

    #[test]
    fn verify_refuses_a_pack_that_carries_a_kiln_mods_cook_outputs() {
        let s = server();
        let out = temp("cooked-refuse");
        let built = build(&s, &out, "P", "1", &mut crate::task::NoProgress).unwrap();
        let old = out.join("old.zip");
        zip_with(&built.file, &old, "kiln/isle-icons/assets/cooked/clip_wave.bin", b"clip");
        let e = verify(&old, None).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused, "{e}");
        assert!(e.message.contains("cooked files of isle-icons") && e.message.contains("build it again"), "{}", e.message);
        // the same entry in a native mod is one of its files: a changed folder, so the hash says no
        let native = out.join("native.zip");
        zip_with(&built.file, &native, "native/fps/assets/cooked/table.bin", b"x");
        assert_eq!(verify(&native, None).unwrap_err().code, ErrorCode::HashMismatch);
        let _ = std::fs::remove_dir_all(s);
        let _ = std::fs::remove_dir_all(out);
    }

    #[test]
    fn verify_refuses_tampered_and_unlisted_entries() {
        use std::io::Write;
        let s = server();
        let out = temp("out2");
        let a = build(&s, &out, "P", "1", &mut crate::task::NoProgress).unwrap();
        // re-pack with an extra entry outside the listed mods
        let evil = out.join("evil.zip");
        {
            let mut src = zip::ZipArchive::new(std::fs::File::open(&a.file).unwrap()).unwrap();
            let mut w = zip::ZipWriter::new(std::fs::File::create(&evil).unwrap());
            for i in 0..src.len() {
                let e = src.by_index(i).unwrap();
                w.raw_copy_file(e).unwrap();
            }
            w.start_file("kiln/other/x.dll", zip::write::SimpleFileOptions::default()).unwrap();
            w.write_all(b"x").unwrap();
            w.finish().unwrap();
        }
        assert_eq!(verify(&evil, None).unwrap_err().code, ErrorCode::Refused);
        // a changed file inside a listed mod
        let bad = out.join("bad.zip");
        {
            let mut src = zip::ZipArchive::new(std::fs::File::open(&a.file).unwrap()).unwrap();
            let mut w = zip::ZipWriter::new(std::fs::File::create(&bad).unwrap());
            for i in 0..src.len() {
                let e = src.by_index(i).unwrap();
                if e.name().unwrap() == "kiln/isle-icons/IsleIcons.dll" {
                    drop(e);
                    w.start_file("kiln/isle-icons/IsleIcons.dll", zip::write::SimpleFileOptions::default()).unwrap();
                    w.write_all(b"MZ evil").unwrap();
                } else {
                    w.raw_copy_file(e).unwrap();
                }
            }
            w.finish().unwrap();
        }
        assert_eq!(verify(&bad, None).unwrap_err().code, ErrorCode::HashMismatch);
        let _ = std::fs::remove_dir_all(s);
        let _ = std::fs::remove_dir_all(out);
    }

    #[test]
    fn install_needs_consent_and_https() {
        let dirs = DataDirs::at(temp("data"));
        let game = temp("game");
        let g = fake_game(&game);
        let req = ModpackInstallRequest { source: ModpackSource::Url { url: "http://127.0.0.1:1/x.zip".into(), sha256: "a".repeat(64) }, consent: false, allow_http: false, activate: None };
        assert_eq!(install(&dirs, &g, &req, &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::ConsentRequired);
        let req = ModpackInstallRequest { consent: true, ..req };
        assert_eq!(install(&dirs, &g, &req, &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused, "http refused by default");
        let _ = std::fs::remove_dir_all(&dirs.root);
        let _ = std::fs::remove_dir_all(game);
    }

    /// Serves `body` once per connection over plain HTTP on 127.0.0.1.
    fn http_server(body: Vec<u8>) -> u16 {
        use std::io::{Read, Write};
        let l = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = l.local_addr().unwrap().port();
        std::thread::spawn(move || {
            for s in l.incoming().flatten() {
                let mut s = s;
                let mut req = [0u8; 2048];
                let _ = s.read(&mut req);
                let _ = write!(s, "HTTP/1.1 200 OK\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", body.len());
                let _ = s.write_all(&body);
            }
        });
        port
    }

    #[cfg(feature = "http")]
    #[test]
    fn download_checks_sha_and_scheme() {
        let body = b"pretend this is a zip".to_vec();
        let sha = crate::hash::sha256_hex(&body);
        let port = http_server(body.clone());
        let d = temp("dl");
        let url = format!("http://127.0.0.1:{port}/pack.zip");
        let dest = d.join("pack.zip");
        assert_eq!(download(&url, &sha, &dest, false, &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::Refused);
        let mut seen = vec![];
        let got = download(&url, &sha, &dest, true, &mut |f: Option<f32>, m: &str| seen.push((f, m.to_string()))).unwrap();
        assert_eq!(std::fs::read(got).unwrap(), body);
        assert!(seen.iter().any(|(f, _)| *f == Some(1.0)));
        let bad = d.join("bad.zip");
        assert_eq!(download(&url, &"0".repeat(64), &bad, true, &mut crate::task::NoProgress).unwrap_err().code, ErrorCode::HashMismatch);
        assert!(!bad.exists() && !crate::util::with_suffix(&bad, ".part").exists(), "nothing left behind");
        let _ = std::fs::remove_dir_all(d);
    }

    /// `KILN_LAB=<lab dir> cargo test -p kiln-manage --all-features -- --ignored lab_required`: the offline required
    /// list equals what the running server's Kiln.Network advertises (network_status).
    #[test]
    #[ignore]
    fn lab_required_matches_server() {
        let lab = PathBuf::from(std::env::var("KILN_LAB").expect("KILN_LAB"));
        let c = crate::admin::client::AdminClient::connect_dir(&lab, "lab", crate::admin::client::Transport::Tcp).unwrap();
        let ns = c.call("network_status", Default::default()).unwrap();
        let server: Vec<(String, String, String)> = ns["required"].as_array().unwrap().iter()
            .map(|r| (r["id"].as_str().unwrap().into(), r["version"].as_str().unwrap().into(), r["hash"].as_str().unwrap().into())).collect();
        let offline: Vec<(String, String, String)> = required(&lab).unwrap().into_iter().map(|r| (r.id, r.version, r.hash)).collect();
        println!("server {server:?}\noffline {offline:?}");
        assert_eq!(server, offline);
        assert_eq!(ns["serverId"].as_str().unwrap(), server_identity(&lab).unwrap().id);
    }

    /// Server build -> publish -> last-server.json on the client -> install over http: library, profile, activation.
    #[cfg(feature = "http")]
    #[test]
    fn install_end_to_end() {
        let s = server();
        let out = temp("e2e-out");
        let built = build(&s, &out, "Ember Isles", "3", &mut crate::task::NoProgress).unwrap();
        let port = http_server(std::fs::read(&built.file).unwrap());
        let url = format!("http://127.0.0.1:{port}/ember-isles-3.zip");
        let dirs = DataDirs::at(temp("e2e-data"));
        let game_dir = temp("e2e-game");
        let game = fake_game(&game_dir);
        let required = required(&s).unwrap();
        let last = serde_json::json!({
            "format": 1, "written": "2026-11-04T12:30:00Z", "outcome": "rejected", "reason": "needs mods",
            "server": {"id": built.manifest.server.id, "name": "Ember Isles", "address": null, "kiln": "0.2.0-dev"},
            "modpack": {"url": url, "sha256": built.sha256, "name": "Ember Isles", "version": "3", "size": built.size},
            "required": required, "truncated": false, "missing": ["isle-icons", "fps"]
        });
        crate::util::write_json_atomic(&game_dir.join("kiln/state/last-server.json"), &last).unwrap();
        let last: crate::state::LastServer = serde_json::from_value(last).unwrap();
        let src = ModpackSource::Url { url: url.clone(), sha256: built.sha256.clone() };
        let pre = inspect(&dirs, &src, Some(&last)).unwrap();
        assert_eq!(pre.missing.len(), 2);
        assert!(!pre.https && pre.native_code);
        let req = ModpackInstallRequest { source: src, consent: true, allow_http: true, activate: Some(false) };
        let mut log = vec![];
        let r = install(&dirs, &game, &req, &mut |f: Option<f32>, m: &str| log.push(format!("{f:?} {m}"))).unwrap_or_else(|e| panic!("{e}\n{}", log.join("\n")));
        assert_eq!(r.profile.name, "Server: Ember Isles");
        assert_eq!(r.profile.server.as_ref().unwrap().id, built.manifest.server.id);
        assert_eq!(r.profile.mods.kiln.len(), 1);
        assert_eq!(r.profile.mods.native.len(), 1);
        assert_eq!(r.imported.len(), 2);
        assert!(inspect(&dirs, &ModpackSource::Url { url, sha256: built.sha256.clone() }, Some(&last)).unwrap().missing.is_empty(), "now in the library");
        // a second install reuses the download and the bound profile
        let again = install(&dirs, &game, &req, &mut crate::task::NoProgress).unwrap();
        assert_eq!(again.profile.id, r.profile.id);
        for d in [s, out, dirs.root.clone(), game_dir] {
            let _ = std::fs::remove_dir_all(d);
        }
    }

    /// `KILN_LAB=<lab> KILN_PUBLISH_DIR=<dir> KILN_PUBLISH_BASE=http://127.0.0.1:18080/ ... -- --ignored lab_publish`:
    /// builds the lab's modpack into the dir and publishes base + file name (a running lab re-reads network.json).
    #[test]
    #[ignore]
    fn lab_publish() {
        let lab = PathBuf::from(std::env::var("KILN_LAB").expect("KILN_LAB"));
        let dir = PathBuf::from(std::env::var("KILN_PUBLISH_DIR").expect("KILN_PUBLISH_DIR"));
        let base = std::env::var("KILN_PUBLISH_BASE").expect("KILN_PUBLISH_BASE");
        let b = build(&lab, &dir, "Kiln lab", "1", &mut crate::task::NoProgress).unwrap();
        let url = format!("{base}{}", b.file.file_name().unwrap().to_string_lossy());
        let st = publish(&lab, &b, &url).unwrap();
        println!("built {} sha256 {} size {}\npublished {}\nrequired {:?}", b.file.display(), b.sha256, b.size, url, st.required);
    }

    fn fake_game(dir: &Path) -> GameInstall {
        std::fs::write(dir.join("enshrouded.exe"), b"MZ").unwrap();
        crate::game::inspect_with(dir, &[], &dir.join("data")).unwrap()
    }
}
