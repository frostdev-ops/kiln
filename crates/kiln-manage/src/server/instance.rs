//! The dedicated servers the launcher manages, one [`ServerInstance`] each:
//! - **kilnctl** (Linux): a kilnctl instance file (`~/.config/kiln/instances/<name>.toml`), Wine with its own prefix,
//!   a SteamCMD install. kilnctl and the launcher manage the same instances.
//! - **steam**: a server the Steam client installed. Found in the Steam libraries; started without Steam with the
//!   Proton build and prefix Steam would use (proton.rs; no console window), stop with SIGINT, never while players
//!   are online.
//! - **windows**: the exe started directly on Windows, windowless; stop = the admin `save_now` op, then terminate.
//! - **remote**: another machine over SSH (kilnctl there when `remote.instance` is set).
//!
//! Launcher records live in `<data>/servers/<id>.json` (steam, windows, remote, renamed entries); kilnctl instances are
//! read from their files, Steam installs are detected. Ids: `^[a-z0-9][a-z0-9_-]{0,47}$`.

use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum InstanceKind {
    Local,
    Remote,
}

/// How a server is run (ServerInstance `runner`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ServerRunner {
    /// kilnctl instance: Wine (GE-Proton) with its own prefix, SteamCMD install (Linux).
    #[default]
    Kilnctl,
    /// Installed by the Steam client (Proton on Linux), like the user's live server; the launcher starts it itself.
    Steam,
    /// The Windows exe, started directly.
    Windows,
    /// Another machine over SSH.
    Remote,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerInstance {
    /// kilnctl instance name, or the launcher record id.
    pub id: String,
    pub name: String,
    pub kind: InstanceKind,
    /// Server folder (local), None for remote.
    pub dir: Option<PathBuf>,
    /// The game's UDP port (= the query port: the game uses one port).
    pub port: u16,
    pub query_port: u16,
    /// kilnctl wine spec ("ge:...", "system", ...).
    pub wine: Option<String>,
    /// verified-only | any
    pub policy: String,
    /// "kilnctl" | "launcher" | "remote" | "steam" (detected Steam install, no record yet)
    pub source: String,
    pub remote: Option<super::remote::RemoteSpec>,
    #[serde(default)]
    pub runner: ServerRunner,
    /// Wine prefix (kilnctl; Proton's `compatdata/2278520/pfx` for steam).
    #[serde(default)]
    pub prefix: Option<PathBuf>,
    /// Where tarball backups go (kilnctl's backup_dir; `<dir>.backups` otherwise).
    #[serde(default)]
    pub backup_dir: Option<PathBuf>,
    /// The kilnctl instance behind it.
    #[serde(default)]
    pub kilnctl: Option<String>,
    /// Seconds a stop waits for the save.
    #[serde(default = "d_stop_s")]
    pub stop_timeout_s: u64,
}

fn d_stop_s() -> u64 {
    90
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ServerSpec {
    pub id: String,
    pub name: String,
    pub dir: PathBuf,
    pub port: u16,
    /// Copy the game files from this install first (cp --reflink: free on btrfs/XFS); SteamCMD then only validates.
    #[serde(default)]
    pub reflink_from: Option<PathBuf>,
    #[serde(default)]
    pub policy: Option<String>,
    /// kilnctl wine spec (default: the newest GE-Proton, pinned).
    #[serde(default)]
    pub wine: Option<String>,
    /// Skip SteamCMD (the files are there already, e.g. reflinked from an up-to-date install). Default false.
    #[serde(default)]
    pub skip_steamcmd: bool,
}

impl ServerInstance {
    /// The server folder; Err for a remote server.
    pub fn local_dir(&self) -> Result<&Path> {
        self.dir.as_deref().ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{} is a remote server", self.id)))
    }

    /// The kilnctl instance (kilnctl runner).
    #[cfg(feature = "server")]
    pub fn kilnctl_instance(&self) -> Result<Instance> {
        let name = self.kilnctl.as_deref().ok_or_else(|| Error::new(ErrorCode::Unsupported, format!("{} is not a kilnctl instance", self.id)))?;
        load(name)
    }

    /// Tarball backups folder.
    pub fn backups(&self) -> Option<PathBuf> {
        self.backup_dir.clone().or_else(|| self.dir.as_ref().map(|d| crate::util::with_suffix(d, ".backups")))
    }
}

/// `<data>/servers`.
pub fn registry_dir() -> Result<PathBuf> {
    Ok(crate::paths::DataDirs::locate()?.root.join("servers"))
}

fn record_file(id: &str) -> Result<PathBuf> {
    validate_name(id)?;
    Ok(registry_dir()?.join(format!("{id}.json")))
}

fn records() -> Vec<ServerInstance> {
    let Ok(dir) = registry_dir() else { return vec![] };
    let mut v: Vec<ServerInstance> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().ends_with(".json"))
        .filter_map(|e| crate::util::read_json::<ServerInstance>(&e.path()).ok().flatten())
        .filter(|s| validate_name(&s.id).is_ok())
        .collect();
    v.sort_by(|a, b| a.id.cmp(&b.id));
    v
}

fn save_record(s: &ServerInstance) -> Result<()> {
    crate::util::write_json_atomic(&record_file(&s.id)?, s)
}

/// The name in enshrouded_server.json, else `fallback`.
fn display_name(dir: &Path, fallback: &str) -> String {
    super::game::read_config(&dir.join(super::game::CONFIG)).map(|c| c.name).ok().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| fallback.to_string())
}

fn query_port_of(dir: &Path) -> u16 {
    super::game::read_config(&dir.join(super::game::CONFIG)).map(|c| c.query_port).unwrap_or(DEFAULT_PORT)
}

fn policy_of(dir: &Path) -> String {
    std::fs::read_to_string(dir.join("kiln/kiln.json"))
        .ok()
        .and_then(|t| crate::admin::parse_jsonc(&t).ok())
        .and_then(|v| v.get("policy").and_then(|p| p.as_str()).map(String::from))
        .unwrap_or_else(|| "verified-only".into())
}

#[cfg(feature = "server")]
fn from_kilnctl(i: &Instance) -> ServerInstance {
    ServerInstance {
        id: i.name.clone(),
        name: display_name(&i.dir, &i.name),
        kind: InstanceKind::Local,
        dir: Some(i.dir.clone()),
        port: i.query_port,
        query_port: i.query_port,
        wine: Some(i.wine.to_config()),
        policy: i.kiln.policy.as_str().into(),
        source: "kilnctl".into(),
        remote: None,
        runner: ServerRunner::Kilnctl,
        prefix: Some(i.prefix.clone()),
        backup_dir: Some(i.backup_dir.clone()),
        kilnctl: Some(i.name.clone()),
        stop_timeout_s: i.stop_timeout.as_secs(),
    }
}

/// A Steam-installed dedicated server (detected or recorded).
pub fn steam_instance(id: &str, dir: &Path, library: Option<&Path>) -> ServerInstance {
    // a library Steam does not list (a copied library, another root): the one the folder sits in
    let library = library.map(Path::to_path_buf).or_else(|| super::proton::library_of(dir));
    let prefix = library.map(|l| l.join("steamapps/compatdata").join(crate::SERVER_APP_ID.to_string()).join("pfx")).filter(|p| p.is_dir());
    let q = query_port_of(dir);
    ServerInstance {
        id: id.to_string(),
        name: display_name(dir, "Steam dedicated server"),
        kind: InstanceKind::Local,
        dir: Some(dir.to_path_buf()),
        port: q,
        query_port: q,
        wine: None,
        policy: policy_of(dir),
        source: "steam".into(),
        remote: None,
        runner: ServerRunner::Steam,
        prefix,
        // never write next to a Steam library's install: backups go to <data>/backups/servers/<id>
        backup_dir: crate::paths::DataDirs::locate().ok().map(|d| d.backups().join("servers").join(id)),
        kilnctl: None,
        stop_timeout_s: 90,
    }
}

/// Steam libraries' EnshroudedServer installs.
fn detected_steam() -> Vec<(PathBuf, PathBuf)> {
    let mut out = Vec::new();
    for root in crate::steam::roots().unwrap_or_default() {
        for lib in crate::steam::libraries(&root).unwrap_or_default() {
            if let Ok(Some(m)) = crate::steam::app_manifest(&lib.path, crate::SERVER_APP_ID) {
                if m.install_dir.join(super::game::EXE).exists() {
                    out.push((m.install_dir.clone(), lib.path.clone()));
                }
            }
        }
    }
    out.sort();
    out.dedup();
    out
}

fn same_dir(a: &Path, b: &Path) -> bool {
    a == b || (a.canonicalize().ok().is_some() && a.canonicalize().ok() == b.canonicalize().ok())
}

/// Every server: kilnctl instances, launcher records, then Steam installs nobody recorded yet.
pub fn list() -> Result<Vec<ServerInstance>> {
    let mut out: Vec<ServerInstance> = Vec::new();
    #[cfg(feature = "server")]
    for n in names() {
        if let Ok(i) = load(&n) {
            out.push(from_kilnctl(&i));
        }
    }
    for r in records() {
        match out.iter_mut().find(|s| s.id == r.id) {
            // a record next to a kilnctl instance only renames it
            Some(s) => s.name = r.name,
            None => out.push(r),
        }
    }
    for (n, (dir, lib)) in detected_steam().into_iter().enumerate() {
        if out.iter().any(|s| s.dir.as_deref().is_some_and(|d| same_dir(d, &dir))) {
            continue;
        }
        let id = if n == 0 { "steam".to_string() } else { format!("steam-{}", n + 1) };
        if out.iter().any(|s| s.id == id) {
            continue;
        }
        out.push(steam_instance(&id, &dir, Some(&lib)));
    }
    Ok(out)
}

pub fn get(id: &str) -> Result<ServerInstance> {
    list()?.into_iter().find(|s| s.id == id).ok_or_else(|| Error::not_found(format!("No server '{id}'.")))
}

/// Writes the kilnctl instance file, copies the game files (reflink) when asked, runs SteamCMD, writes
/// enshrouded_server.json, creates the Wine prefix (Linux). Windows: a launcher record + SteamCMD. Kiln is deployed
/// afterwards (server_deploy).
pub fn create(spec: &ServerSpec, progress: &mut dyn Progress) -> Result<ServerInstance> {
    validate_name(&spec.id)?;
    if spec.name.trim().is_empty() || spec.name.len() > 64 {
        return Err(Error::invalid("server name: 1 to 64 characters"));
    }
    if spec.port < 1024 {
        return Err(Error::invalid(format!("port {} is below 1024", spec.port)));
    }
    if !spec.dir.is_absolute() {
        return Err(Error::invalid(format!("{} must be an absolute path", spec.dir.display())));
    }
    if list()?.iter().any(|s| s.id == spec.id) {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("a server '{}' exists", spec.id)));
    }
    if spec.dir.join(super::game::EXE).exists() && spec.reflink_from.is_some() {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("{} already holds a server; import it instead", spec.dir.display())));
    }
    create_platform(spec, progress)
}

#[cfg(all(unix, feature = "server"))]
fn create_platform(spec: &ServerSpec, progress: &mut dyn Progress) -> Result<ServerInstance> {
    let home = crate::util::home();
    let mut wine = WineSpec::parse(spec.wine.as_deref().unwrap_or("ge"), &home)?;
    if wine == WineSpec::GeLatest {
        // pin the newest GE-Proton now (a later install must not swap the Wine under the prefix), as kilnctl init does
        let (n, _) = crate::wine::ge_installs(&home)
            .into_iter()
            .next()
            .ok_or_else(|| Error::new(ErrorCode::Unsupported, "no GE-Proton installed (see docs/kiln/kilnctl.md), or give a wine"))?;
        wine = WineSpec::Ge(n);
    }
    let policy = match spec.policy.as_deref() {
        Some("any") => Policy::Any,
        _ => Policy::VerifiedOnly,
    };
    let source = compiled_source();
    let prefix = crate::util::with_suffix(&spec.dir, ".pfx");
    let backup_dir = crate::util::with_suffix(&spec.dir, ".backups");
    let build = source.join("native/build");
    let text = render(&InitSpec {
        name: &spec.id,
        dir: &spec.dir,
        prefix: &prefix,
        wine: &wine,
        port: spec.port,
        backup_dir: &backup_dir,
        source: &source,
        build: &build,
        policy,
        samples: false,
    });
    let file = instance_file(&spec.id);
    let inst = parse(&spec.id, &file, &text, &home)?;
    std::fs::create_dir_all(instances_dir())?;
    if file.exists() {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("{} exists", file.display())));
    }
    std::fs::write(&file, &text).map_err(crate::util::io_at(&file, "writing"))?;
    progress.report(Some(0.02), &format!("instance file {}", file.display()));
    std::fs::create_dir_all(&inst.dir).map_err(crate::util::io_at(&inst.dir, "creating"))?;
    if let Some(src) = &spec.reflink_from {
        progress.report(Some(0.03), &format!("copying the game files from {} (reflink)", src.display()));
        reflink_copy(src, &inst.dir)?;
    }
    if !spec.skip_steamcmd {
        super::steamcmd::install(&inst.dir, &mut Scaled { inner: progress, from: 0.05, to: 0.8 })?;
    } else if !inst.exe().exists() {
        return Err(Error::not_found(format!("{} is missing and SteamCMD was skipped", inst.exe().display())));
    }
    progress.report(Some(0.82), "writing enshrouded_server.json");
    let cfg = inst.server_json();
    if !cfg.exists() {
        std::fs::write(&cfg, super::game::default_config(spec.name.trim(), spec.port)?).map_err(crate::util::io_at(&cfg, "writing"))?;
    }
    super::settings::set_name_port(&cfg, spec.name.trim(), spec.port)?;
    progress.report(Some(0.85), "creating the Wine prefix (wineboot)");
    super::run::make_prefix(&inst)?;
    progress.report(Some(1.0), &format!("server {} ready in {}", spec.id, inst.dir.display()));
    Ok(from_kilnctl(&inst))
}

#[cfg(not(all(unix, feature = "server")))]
fn create_platform(spec: &ServerSpec, progress: &mut dyn Progress) -> Result<ServerInstance> {
    if !cfg!(windows) {
        return Err(Error::new(ErrorCode::Unsupported, "kiln-manage was built without the server feature"));
    }
    std::fs::create_dir_all(&spec.dir).map_err(crate::util::io_at(&spec.dir, "creating"))?;
    if !spec.skip_steamcmd {
        super::steamcmd::install(&spec.dir, &mut Scaled { inner: progress, from: 0.05, to: 0.9 })?;
    }
    let cfg = spec.dir.join(super::game::CONFIG);
    if !cfg.exists() {
        std::fs::write(&cfg, super::game::default_config(spec.name.trim(), spec.port)?).map_err(crate::util::io_at(&cfg, "writing"))?;
    }
    super::settings::set_name_port(&cfg, spec.name.trim(), spec.port)?;
    let s = windows_instance(&spec.id, spec.name.trim(), &spec.dir);
    save_record(&s)?;
    progress.report(Some(1.0), "done");
    Ok(s)
}

fn windows_instance(id: &str, name: &str, dir: &Path) -> ServerInstance {
    let q = query_port_of(dir);
    ServerInstance {
        id: id.into(),
        name: name.into(),
        kind: InstanceKind::Local,
        dir: Some(dir.to_path_buf()),
        port: q,
        query_port: q,
        wine: None,
        policy: policy_of(dir),
        source: "launcher".into(),
        remote: None,
        runner: ServerRunner::Windows,
        prefix: None,
        backup_dir: Some(crate::util::with_suffix(dir, ".backups")),
        kilnctl: None,
        stop_timeout_s: 90,
    }
}

/// `cp -a --reflink=always` of the install's entries (not its instance files: config, saves, logs, Kiln).
#[cfg(unix)]
#[cfg_attr(not(feature = "server"), allow(dead_code))]
fn reflink_copy(src: &Path, dst: &Path) -> Result<()> {
    let src = src.canonicalize().map_err(crate::util::io_at(src, "reading"))?;
    if !src.join(super::game::EXE).exists() {
        return Err(Error::invalid(format!("{} has no {}", src.display(), super::game::EXE)));
    }
    let d = dst.canonicalize().unwrap_or_else(|_| dst.to_path_buf());
    if d.starts_with(&src) || src.starts_with(&d) {
        return Err(Error::invalid("source and target overlap"));
    }
    const SKIP: &[&str] = &["kiln", "savegame", "logs", "dinput8.dll", "enshrouded_server.json", "backup", "shroudforge"];
    let mut entries: Vec<PathBuf> = std::fs::read_dir(&src)?.flatten().filter(|e| !SKIP.contains(&e.file_name().to_string_lossy().as_ref())).map(|e| e.path()).collect();
    entries.sort();
    let st = std::process::Command::new("cp").args(["-a", "--reflink=always"]).args(&entries).arg(dst).status()?;
    if !st.success() {
        return Err(Error::new(ErrorCode::Io, format!("cp --reflink=always failed ({st}); source and target must be on the same btrfs/XFS filesystem")));
    }
    Ok(())
}

#[cfg(not(unix))]
#[allow(dead_code)]
fn reflink_copy(_src: &Path, _dst: &Path) -> Result<()> {
    Err(Error::new(ErrorCode::Unsupported, "reflink copies need Linux (btrfs/XFS)"))
}

/// Adopts an existing server folder: a Steam library install becomes a steam record, a folder of its own a kilnctl
/// instance (Linux; prefix `<dir>.pfx`) or a windows record.
pub fn import(dir: &Path, name: &str) -> Result<ServerInstance> {
    if !dir.join(super::game::EXE).exists() {
        return Err(Error::invalid(format!("{} has no {}", dir.display(), super::game::EXE)));
    }
    if let Some(s) = list()?.into_iter().find(|s| s.dir.as_deref().is_some_and(|d| same_dir(d, dir)) && s.source != "steam") {
        return Err(Error::new(ErrorCode::AlreadyExists, format!("{} is server '{}' already", dir.display(), s.id)));
    }
    let base: String = name.to_ascii_lowercase().chars().map(|c| if c.is_ascii_alphanumeric() { c } else { '-' }).collect::<String>().trim_matches('-').chars().take(40).collect();
    let base = if base.is_empty() { "server".to_string() } else { base };
    let taken: Vec<String> = list()?.into_iter().map(|s| s.id).collect();
    let mut id = base.clone();
    let mut n = 2;
    while taken.contains(&id) {
        id = format!("{base}-{n}");
        n += 1;
    }
    let lib = detected_steam().into_iter().find(|(d, _)| same_dir(d, dir)).map(|(_, l)| l);
    let s = if lib.is_some() || dir.to_string_lossy().contains("/steamapps/common/") {
        let mut s = steam_instance(&id, dir, lib.as_deref());
        s.source = "launcher".into();
        s.name = name.trim().to_string();
        s
    } else if cfg!(windows) {
        windows_instance(&id, name.trim(), dir)
    } else {
        return import_kilnctl(&id, dir, name);
    };
    save_record(&s)?;
    Ok(s)
}

#[cfg(all(unix, feature = "server"))]
fn import_kilnctl(id: &str, dir: &Path, name: &str) -> Result<ServerInstance> {
    let home = crate::util::home();
    let (n, _) = crate::wine::ge_installs(&home).into_iter().next().ok_or_else(|| Error::new(ErrorCode::Unsupported, "no GE-Proton installed"))?;
    let wine = WineSpec::Ge(n);
    let source = compiled_source();
    let prefix = crate::util::with_suffix(dir, ".pfx");
    let text = render(&InitSpec {
        name: id,
        dir,
        prefix: &prefix,
        wine: &wine,
        port: query_port_of(dir),
        backup_dir: &crate::util::with_suffix(dir, ".backups"),
        source: &source,
        build: &source.join("native/build"),
        policy: if policy_of(dir) == "any" { Policy::Any } else { Policy::VerifiedOnly },
        samples: false,
    });
    let file = instance_file(id);
    let inst = parse(id, &file, &text, &home)?;
    std::fs::create_dir_all(instances_dir())?;
    std::fs::write(&file, &text).map_err(crate::util::io_at(&file, "writing"))?;
    let mut s = from_kilnctl(&inst);
    if !name.trim().is_empty() && name.trim() != s.name {
        s.name = name.trim().to_string();
        save_record(&s)?;
    }
    Ok(s)
}

#[cfg(not(all(unix, feature = "server")))]
fn import_kilnctl(_id: &str, _dir: &Path, _name: &str) -> Result<ServerInstance> {
    Err(Error::new(ErrorCode::Unsupported, "kilnctl instances need Linux and the server feature"))
}

/// Forgets a server (its record or kilnctl instance file). `delete_files` also deletes its folder, prefix and backups
/// (never a Steam library's install, never while it runs).
pub fn remove(id: &str, delete_files: bool) -> Result<()> {
    let s = get(id)?;
    if super::run::status(&s).map(|st| st.pid.is_some()).unwrap_or(false) {
        return Err(Error::new(ErrorCode::Conflict, format!("{id} is running; stop it first")));
    }
    if delete_files {
        let dir = s.local_dir()?;
        if s.runner == ServerRunner::Steam || dir.to_string_lossy().contains("/steamapps/") {
            return Err(Error::new(ErrorCode::Refused, "a Steam install is removed through Steam, not here"));
        }
        for p in [Some(dir.to_path_buf()), s.prefix.clone(), s.backups()].into_iter().flatten() {
            if p.exists() {
                std::fs::remove_dir_all(&p).map_err(crate::util::io_at(&p, "deleting"))?;
            }
        }
    }
    if let Some(name) = &s.kilnctl {
        let f = instance_file(name);
        if f.exists() {
            std::fs::remove_file(&f).map_err(crate::util::io_at(&f, "deleting"))?;
        }
    }
    if let Ok(f) = record_file(id) {
        if f.exists() {
            std::fs::remove_file(&f).map_err(crate::util::io_at(&f, "deleting"))?;
        }
    }
    Ok(())
}

/// Adds or replaces a launcher record (remote servers, renames).
pub fn record(s: &ServerInstance) -> Result<()> {
    if let Some(r) = &s.remote {
        r.validate()?;
    }
    save_record(s)
}

/// Maps a sub-step's 0..1 into `from..to`.
pub(crate) struct Scaled<'a> {
    pub inner: &'a mut dyn Progress,
    pub from: f32,
    pub to: f32,
}

impl Progress for Scaled<'_> {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        self.inner.report(fraction.map(|f| self.from + f.clamp(0.0, 1.0) * (self.to - self.from)), message)
    }
    fn canceled(&self) -> bool {
        self.inner.canceled()
    }
}

// ---- kilnctl instance files -----------------------------
// `~/.config/kiln/instances/<name>.toml` (`$XDG_CONFIG_HOME` respected). Parsing and rendering need the `server`
// feature (toml); the types are always there.

use crate::wine::WineSpec;
use std::collections::BTreeMap;
use std::time::Duration;

/// The game's default query port.
pub const DEFAULT_PORT: u16 = 15637;

/// kiln/kiln.json `policy` an instance deploys.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Policy {
    /// Hooks only on builds with a verified record (kiln/sigdb/builds/server/<sha256>.json)
    #[default]
    VerifiedOnly,
    /// Also on unverified builds (labs)
    Any,
}

impl Policy {
    pub fn as_str(self) -> &'static str {
        match self {
            Policy::VerifiedOnly => "verified-only",
            Policy::Any => "any",
        }
    }
}

#[cfg(feature = "server")]
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct Raw {
    dir: String,
    prefix: Option<String>,
    #[serde(default = "d_wine")]
    wine: String,
    #[serde(default = "d_port")]
    query_port: u16,
    backup_dir: Option<String>,
    #[serde(default = "d_true")]
    require_ntsync: bool,
    #[serde(default = "d_stop")]
    stop_timeout: u64,
    #[serde(default)]
    kiln: RawKiln,
    #[serde(default)]
    env: BTreeMap<String, String>,
}

#[cfg(feature = "server")]
#[derive(Debug, Default, Deserialize)]
#[serde(deny_unknown_fields)]
struct RawKiln {
    source: Option<String>,
    build: Option<String>,
    #[serde(default)]
    policy: Policy,
    #[serde(default)]
    samples: bool,
}

#[cfg(feature = "server")]
fn d_wine() -> String {
    "ge".into()
}
#[cfg(feature = "server")]
fn d_port() -> u16 {
    DEFAULT_PORT
}
#[cfg(feature = "server")]
fn d_true() -> bool {
    true
}
#[cfg(feature = "server")]
fn d_stop() -> u64 {
    90
}

/// The Kiln an instance deploys.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Kiln {
    /// Kiln source tree: tools/deploy-server.sh, sigdb/, tools/sigport/sigdb_check.py.
    pub source: PathBuf,
    /// Kiln build output (`KILN_OUT`).
    pub build: PathBuf,
    pub policy: Policy,
    pub samples: bool,
}

/// A kilnctl instance file, parsed and validated.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Instance {
    pub name: String,
    pub file: PathBuf,
    pub dir: PathBuf,
    pub prefix: PathBuf,
    pub wine: WineSpec,
    pub query_port: u16,
    pub backup_dir: PathBuf,
    pub require_ntsync: bool,
    pub stop_timeout: Duration,
    pub kiln: Kiln,
    pub env: BTreeMap<String, String>,
}

impl Instance {
    pub fn exe(&self) -> PathBuf {
        self.dir.join(super::game::EXE)
    }
    pub fn server_json(&self) -> PathBuf {
        self.dir.join(super::game::CONFIG)
    }
    pub fn unit(&self) -> String {
        format!("kiln@{}.service", self.name)
    }
    /// Output of `kilnctl run` when started with `kilnctl start` (not systemd).
    pub fn run_log(&self) -> PathBuf {
        crate::util::state_dir().join(format!("{}.run.log", self.name))
    }
}

pub fn instances_dir() -> PathBuf {
    crate::util::config_dir().join("instances")
}

pub fn instance_file(name: &str) -> PathBuf {
    instances_dir().join(format!("{name}.toml"))
}

/// Instance names double as systemd instance names (`kiln@<name>`) and file names.
pub fn validate_name(name: &str) -> Result<()> {
    let ok = !name.is_empty()
        && name.len() <= 48
        && name.bytes().next().is_some_and(|b| b.is_ascii_lowercase() || b.is_ascii_digit())
        && name.bytes().all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-' || b == b'_');
    if !ok {
        return Err(Error::invalid(format!("instance name {name:?}: use 1-48 of [a-z0-9_-], starting with a letter or digit")));
    }
    Ok(())
}

/// The Kiln tree this crate was built from (default `kiln.source`). A release build (`KILN_RELEASE_BUILD` set while
/// compiling, tools/release) knows no tree: the build machine's path would mean nothing elsewhere.
pub fn compiled_source() -> PathBuf {
    const TREE: &str = match option_env!("KILN_RELEASE_BUILD") {
        Some(_) => "",
        None => env!("CARGO_MANIFEST_DIR"),
    };
    if TREE.is_empty() {
        return PathBuf::new();
    }
    let p = Path::new(TREE).join("../..");
    p.canonicalize().unwrap_or(p)
}

/// Kilnctl manages its own SteamCMD installs; a Steam client library (or Proton prefix) belongs to the client.
#[cfg(feature = "server")]
fn in_steam_client_library(p: &Path) -> bool {
    let s = format!("{}/", p.display());
    s.contains("/steamapps/common/") || s.contains("/steamapps/compatdata/")
}

#[cfg(feature = "server")]
fn bad(message: String) -> Error {
    Error::invalid(message)
}

#[cfg(feature = "server")]
pub fn parse(name: &str, file: &Path, text: &str, home: &Path) -> Result<Instance> {
    use crate::util::{clean, expand, with_suffix};
    validate_name(name)?;
    let raw: Raw = toml::from_str(text).map_err(|e| Error::new(crate::ErrorCode::Parse, format!("parsing {}: {e}", file.display())))?;
    let abs = |what: &str, s: &str| -> Result<PathBuf> {
        let p = expand(s, home);
        if !p.is_absolute() {
            return Err(bad(format!("{what} must be an absolute path (or ~/...), not {s:?}")));
        }
        Ok(clean(&p))
    };
    let dir = abs("dir", &raw.dir)?;
    let prefix = match &raw.prefix {
        Some(p) => abs("prefix", p)?,
        None => with_suffix(&dir, ".pfx"),
    };
    let backup_dir = match &raw.backup_dir {
        Some(p) => abs("backup_dir", p)?,
        None => with_suffix(&dir, ".backups"),
    };
    // empty = not set: a released launcher has no source tree (compiled_source() is empty there), and only kilnctl's
    // source-tree commands (deploy from a build, gate, sigdb check) need one
    let set = |v: &Option<String>| v.as_deref().map(str::trim).filter(|s| !s.is_empty()).map(str::to_owned);
    let source = match set(&raw.kiln.source) {
        Some(p) => abs("kiln.source", &p)?,
        None => compiled_source(),
    };
    let build = match set(&raw.kiln.build) {
        Some(p) => abs("kiln.build", &p)?,
        None if source.as_os_str().is_empty() => PathBuf::new(),
        None => source.join("native/build"),
    };
    if dir == prefix {
        return Err(bad("dir and prefix are the same folder".into()));
    }
    for (what, p) in [("dir", &dir), ("prefix", &prefix)] {
        if in_steam_client_library(p) {
            return Err(bad(format!(
                "{what} {} is inside a Steam client library; kilnctl manages its own SteamCMD install and prefix \
                 (pick a folder outside steamapps/)",
                p.display()
            )));
        }
    }
    if raw.query_port < 1024 {
        return Err(bad(format!("query_port {} is below 1024", raw.query_port)));
    }
    if !(10..=900).contains(&raw.stop_timeout) {
        return Err(bad(format!("stop_timeout {} s: use 10-900", raw.stop_timeout)));
    }
    for (k, v) in &raw.env {
        let valid = k.bytes().next().is_some_and(|b| b.is_ascii_alphabetic() || b == b'_')
            && k.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
        if !valid {
            return Err(bad(format!("[env] key {k:?} is not a variable name")));
        }
        match k.as_str() {
            "WINEPREFIX" => return Err(bad("[env] cannot set WINEPREFIX; use prefix = \"...\"".into())),
            "WINEDLLOVERRIDES" => crate::wine::check_mscoree(v)?,
            _ => {}
        }
    }
    Ok(Instance {
        name: name.to_string(),
        file: file.to_path_buf(),
        dir,
        prefix,
        wine: WineSpec::parse(&raw.wine, home)?,
        query_port: raw.query_port,
        backup_dir,
        require_ntsync: raw.require_ntsync,
        stop_timeout: Duration::from_secs(raw.stop_timeout),
        kiln: Kiln { source, build, policy: raw.kiln.policy, samples: raw.kiln.samples },
        env: raw.env,
    })
}

#[cfg(feature = "server")]
pub fn load(name: &str) -> Result<Instance> {
    validate_name(name)?;
    let file = instance_file(name);
    let text = std::fs::read_to_string(&file).map_err(|e| {
        let base = Error::from(e);
        Error::new(
            base.code,
            format!(
                "no instance {name:?}: cannot read {} (create it with kilnctl init {name} --dir <dir>): {}",
                file.display(),
                base.message
            ),
        )
    })?;
    parse(name, &file, &text, &crate::util::home())
}

/// Names of all instance files (kilnctl's `config::list`).
pub fn names() -> Vec<String> {
    let mut v: Vec<String> = std::fs::read_dir(instances_dir())
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| e.file_name().to_str()?.strip_suffix(".toml").map(str::to_string))
        .filter(|n| validate_name(n).is_ok())
        .collect();
    v.sort();
    v
}

#[cfg(feature = "server")]
fn q(s: &str) -> String {
    toml::Value::String(s.to_string()).to_string()
}

#[cfg(feature = "server")]
fn qp(p: &Path) -> String {
    q(&p.display().to_string())
}

/// What `kilnctl init` writes.
pub struct InitSpec<'a> {
    pub name: &'a str,
    pub dir: &'a Path,
    pub prefix: &'a Path,
    pub wine: &'a WineSpec,
    pub port: u16,
    pub backup_dir: &'a Path,
    pub source: &'a Path,
    pub build: &'a Path,
    pub policy: Policy,
    pub samples: bool,
}

/// The instance file `kilnctl init` writes (every setting spelled out, with comments).
#[cfg(feature = "server")]
pub fn render(s: &InitSpec) -> String {
    format!(
        r#"# kilnctl instance "{name}" (docs/kiln/kilnctl.md)

# Server folder (enshrouded_server.exe), installed and updated with SteamCMD
dir = {dir}
# Wine prefix of this instance (kilnctl prefix {name} creates it)
prefix = {prefix}
# Wine: "ge" (newest GE-Proton), "ge:<GE-Proton folder name or path>" (pinned), "system" (wine on PATH),
# or the path of a wine binary with its wineserver next to it
wine = {wine}
# UDP query port; must match queryPort in enshrouded_server.json
query_port = {port}
# Timestamped savegame tars (kilnctl backup, kilnctl update)
backup_dir = {backup}
# Fail preflight when /dev/ntsync is missing or unusable
require_ntsync = true
# Seconds kilnctl stop waits for the server to save and exit
stop_timeout = 90

[kiln]
# Kiln source tree (tools/deploy-server.sh, sigdb/, tools/sigport/sigdb_check.py)
{source}
# Kiln build output (KILN_OUT) that kilnctl deploy lays out
{build}
# "verified-only": hooks only on builds with a verified record; "any": unverified builds too (labs)
policy = "{policy}"
# Also deploy the sample mod and native plugin
samples = {samples}

# Extra environment for every Wine call (WINEDEBUG, KILN_DISABLE, ...). WINEPREFIX comes from prefix above;
# WINEDLLOVERRIDES defaults to "{overrides}" and must never disable mscoree.
[env]
"#,
        name = s.name,
        dir = qp(s.dir),
        prefix = qp(s.prefix),
        wine = q(&s.wine.to_config()),
        port = s.port,
        backup = qp(s.backup_dir),
        // a released launcher knows no source tree: the keys stay commented out (the launcher installs Kiln from its
        // runtime bundles; only kilnctl's source-tree commands need them)
        source = if s.source.as_os_str().is_empty() { "# source = \"<a Kiln source tree>\"".to_owned() } else { format!("source = {}", qp(s.source)) },
        build = if s.source.as_os_str().is_empty() || s.build.as_os_str().is_empty() {
            "# build = \"<its build output>\"".to_owned()
        } else {
            format!("build = {}", qp(s.build))
        },
        policy = s.policy.as_str(),
        samples = s.samples,
        overrides = crate::wine::DLL_OVERRIDES,
    )
}

/// A TOML basic string (`"..."`), for callers that append keys to a rendered file.
#[cfg(feature = "server")]
pub fn toml_str(s: &str) -> String {
    q(s)
}
