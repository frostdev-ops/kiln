//! Game installs the launcher manages: clients found through Steam or added by hand, server folders. A record per
//! install lives in `<data>/installs/<installId>.json` (InstallRecord); the game-side receipt is `kiln/.install.json`.

use crate::paths::DataDirs;
use crate::state::BootJson;
use crate::steam::{self, SteamFlavor, SteamRoot};
use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum GameKind {
    Client,
    Server,
}

/// How the game runs here.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Platform {
    /// Steam + Proton on Linux.
    Proton,
    /// Plain Wine (servers through kilnctl).
    Wine,
    /// Native Windows.
    Windows,
}

/// How profile.json names mod folders (contracts.md, path translation).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum PathMode {
    /// `Z:\...` into the launcher library (Proton with `pfx/dosdevices/z:` -> `/`).
    Z,
    /// Real Windows paths (Windows).
    Native,
    /// Mods copied under `<game>/kiln/profiles/<pid>/`, relative paths (Flatpak/Snap Steam, no usable Z:).
    Copy,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameInstall {
    /// Stable id (the receipt's installId once Kiln is installed; before that derived from the path).
    pub id: String,
    pub kind: GameKind,
    /// The game folder (host path).
    pub path: PathBuf,
    /// `enshrouded.exe` or `enshrouded_server.exe`
    pub exe: String,
    pub app_id: u32,
    /// The Steam library holding it, None for a manual/kilnctl install.
    pub library: Option<PathBuf>,
    pub platform: Platform,
    /// Proton/Wine prefix (`.../compatdata/1203620/pfx`), None on Windows.
    pub prefix: Option<PathBuf>,
    pub build_id: Option<String>,
    pub exe_sha256: Option<String>,
    pub steam_flavor: Option<crate::steam::SteamFlavor>,
    pub path_mode: PathMode,
    /// "steam" | "manual" | "kilnctl"
    pub source: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchOptionsInfo {
    /// Steam's launch options for the app (read-only), None when unset.
    pub value: Option<String>,
    /// Under Proton: WINEDLLOVERRIDES has dinput8=n,b (or the prefix's registry override is set).
    pub has_dll_override: bool,
    /// What the user should change, e.g. `WINEDLLOVERRIDES="dinput8=n,b" %command%`.
    pub advice: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GameStatus {
    pub id: String,
    pub running: bool,
    pub pid: Option<u32>,
    /// When the running game's process started (RFC 3339 UTC, `proc::start_time`; the Wine process under Proton), however
    /// it was started (the launcher, Steam, a shortcut). None while it is not running or when the time is unreadable.
    #[serde(default)]
    pub started: Option<String>,
    pub steam_updating: bool,
    pub runtime: crate::install::InstallState,
    pub active_profile: Option<String>,
    /// kiln/play-once.ini still exists (the proxy has not consumed it).
    pub play_once_pending: bool,
    pub boot: Option<BootJson>,
    pub launch_options: Option<LaunchOptionsInfo>,
    /// Kiln mods of the active profile built against a newer Kiln than the one in the game (`kilnBuild` against the
    /// runtime's): they may fail to load until Kiln is updated on the Runtime page.
    #[serde(default)]
    pub newer_mods: Vec<crate::profile::NewerKilnMod>,
}

pub const CLIENT_EXE: &str = "enshrouded.exe";
pub const SERVER_EXE: &str = "enshrouded_server.exe";
/// What a Proton launch needs so the game loads Kiln's proxy (shown, never written: Kiln never edits Steam's config).
pub const DLL_OVERRIDE_ADVICE: &str = "WINEDLLOVERRIDES=\"dinput8=n,b\" %command%";

/// `<data>/installs/<installId>.json`: the launcher's record of a game install.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InstallRecord {
    pub format: u32,
    pub install: GameInstall,
    /// A copy of the game's `kiln/.install.json` when Kiln is installed (as JSON; the installer writes it).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub receipt: Option<serde_json::Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub added: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub updated: Option<String>,
}

fn canon(p: &Path) -> PathBuf {
    steam::canon(p)
}

/// The install id of a game folder: the receipt's `installId` (`kiln/.install.json`) when Kiln is installed, else a
/// UUID derived from sha256(canonical path) (version nibble 8, RFC 9562 "custom"). The first install keeps that id
/// as its receipt's `installId`, so a folder's id does not change when Kiln is installed (or reinstalled).
pub fn install_id_for(dir: &Path) -> String {
    if let Some(id) = receipt_install_id(dir) {
        return id;
    }
    path_install_id(dir)
}

/// The path-derived install id (see `install_id_for`). Windows paths are case-insensitive, so there the path is
/// lower-cased first: `c:\program files (x86)\steam\...` (the registry's SteamPath) and the folder's real spelling
/// are one install.
pub fn path_install_id(dir: &Path) -> String {
    let c = canon(dir);
    let text = c.to_string_lossy();
    let text = if cfg!(windows) { text.to_lowercase() } else { text.into_owned() };
    let h = crate::hash::sha256_hex(text.as_bytes());
    let mut b: Vec<char> = h[..32].chars().collect();
    b[12] = '8';
    b[16] = ['8', '9', 'a', 'b'][(b[16].to_digit(16).unwrap_or(0) & 3) as usize];
    let s: String = b.into_iter().collect();
    format!("{}-{}-{}-{}-{}", &s[0..8], &s[8..12], &s[12..16], &s[16..20], &s[20..32])
}

/// True for a UUID text (8-4-4-4-12 lower-case hex): the receipt's `installId` shape.
pub fn is_uuid(s: &str) -> bool {
    let parts: Vec<&str> = s.split('-').collect();
    parts.len() == 5
        && parts.iter().zip([8, 4, 4, 4, 12]).all(|(p, n)| p.len() == n && p.bytes().all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase()))
}

fn receipt_json(dir: &Path) -> Option<serde_json::Value> {
    let text = std::fs::read_to_string(dir.join(crate::paths::game::RECEIPT)).ok()?;
    serde_json::from_str(&text).ok()
}

fn receipt_install_id(dir: &Path) -> Option<String> {
    let v = receipt_json(dir)?;
    let id = v.get("installId")?.as_str()?;
    (!id.is_empty() && id.len() <= 64 && id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-')).then(|| id.to_string())
}

/// Path mode from the facts (contracts.md section 6): Windows -> Native; Flatpak/Snap Steam -> Copy; a prefix whose
/// `dosdevices/z:` resolves to `/` and a launcher data dir that translates to `Z:\...` -> Z; else Copy.
pub fn path_mode_for(platform: Platform, flavor: Option<SteamFlavor>, prefix: Option<&Path>, data_root: &Path) -> PathMode {
    if platform == Platform::Windows {
        return PathMode::Native;
    }
    if matches!(flavor, Some(SteamFlavor::Flatpak | SteamFlavor::Snap)) {
        return PathMode::Copy;
    }
    match prefix {
        Some(p) if crate::winpath::z_drive_ok(p) && crate::winpath::unix_to_z(data_root).is_ok() => PathMode::Z,
        _ => PathMode::Copy,
    }
}

fn default_data_root() -> PathBuf {
    DataDirs::locate().map(|d| d.root).unwrap_or_else(|_| PathBuf::from("/nonexistent-kiln-data"))
}

fn exe_kind(dir: &Path) -> Option<(GameKind, &'static str, u32)> {
    if dir.join(SERVER_EXE).is_file() {
        Some((GameKind::Server, SERVER_EXE, crate::SERVER_APP_ID))
    } else if dir.join(CLIENT_EXE).is_file() {
        Some((GameKind::Client, CLIENT_EXE, crate::CLIENT_APP_ID))
    } else {
        None
    }
}

/// `<library>/steamapps/common/<dir>` -> `<library>`.
fn library_of(dir: &Path) -> Option<PathBuf> {
    let common = dir.parent()?;
    let steamapps = common.parent()?;
    let ok = common.file_name()?.eq_ignore_ascii_case("common") && steamapps.file_name()?.eq_ignore_ascii_case("steamapps");
    ok.then(|| steamapps.parent().map(Path::to_path_buf)).flatten()
}

fn from_steam(root: &SteamRoot, library: &Path, m: &steam::AppManifest, data_root: &Path) -> Option<GameInstall> {
    let dir = canon(&m.install_dir);
    let (kind, exe, _) = exe_kind(&dir)?;
    let platform = if root.flavor == SteamFlavor::Windows || cfg!(windows) { Platform::Windows } else { Platform::Proton };
    let prefix = (platform != Platform::Windows).then(|| steam::compatdata(library, m.app_id).join("pfx"));
    let path_mode = path_mode_for(platform, Some(root.flavor), prefix.as_deref(), data_root);
    Some(GameInstall {
        id: install_id_for(&dir),
        kind,
        path: dir,
        exe: exe.to_string(),
        app_id: m.app_id,
        library: Some(library.to_path_buf()),
        platform,
        prefix,
        build_id: m.build_id.clone(),
        exe_sha256: None,
        steam_flavor: Some(root.flavor),
        path_mode,
        source: "steam".into(),
    })
}

/// `detect` over the given roots (tests use fake trees). The data root decides Z vs copy mode.
pub fn detect_in(roots: &[SteamRoot], data_root: &Path) -> Result<Vec<GameInstall>> {
    let mut out: Vec<GameInstall> = Vec::new();
    for app in [crate::CLIENT_APP_ID, crate::SERVER_APP_ID] {
        for (root, lib, m) in steam::find_app(roots, app) {
            if let Some(g) = from_steam(&root, &lib.path, &m, data_root)
                && !out.iter().any(|o| steam::same_path(&o.path, &g.path))
            {
                out.push(g);
            }
        }
    }
    Ok(out)
}

/// Finds Enshrouded clients (and servers installed through Steam) in every Steam library.
/// `exe_sha256` stays None (hashing ~100 MB per install is left to `add`).
pub fn detect() -> Result<Vec<GameInstall>> {
    detect_in(&steam::roots()?, &default_data_root())
}

/// `inspect` with explicit Steam roots and launcher data root.
pub fn inspect_with(path: &Path, roots: &[SteamRoot], data_root: &Path) -> Result<GameInstall> {
    let mut dir = canon(path);
    if dir.is_file() {
        dir = dir.parent().map(Path::to_path_buf).unwrap_or(dir);
    }
    if !dir.is_dir() {
        return Err(Error::not_found(format!("{}: no such folder", path.display())));
    }
    let (kind, exe, app_id) = exe_kind(&dir).ok_or_else(|| {
        Error::invalid(format!("{} has neither {CLIENT_EXE} nor {SERVER_EXE}", dir.display()))
    })?;
    // inside a Steam library: the same facts detect gives
    if let Some(lib) = library_of(&dir)
        && let Ok(Some(m)) = steam::app_manifest(&lib, app_id)
        && steam::same_path(&canon(&m.install_dir), &dir)
    {
        let root = steam::root_of_library(roots, &lib)
            .unwrap_or(SteamRoot { path: lib.clone(), flavor: steam::flavor_of(&lib) });
        if let Some(g) = from_steam(&root, &lib, &m, data_root) {
            return Ok(g);
        }
    }
    // a folder of its own: kilnctl's `<dir>.pfx` (plain Wine), else the client's Steam prefix, else none
    let mut pfx_os = dir.clone().into_os_string();
    pfx_os.push(".pfx");
    let own_pfx = PathBuf::from(pfx_os);
    let (platform, prefix) = if cfg!(windows) {
        (Platform::Windows, None)
    } else if own_pfx.join("dosdevices").is_dir() {
        (Platform::Wine, Some(own_pfx))
    } else {
        let guess = steam::find_app(roots, app_id)
            .into_iter()
            .map(|(_, lib, _)| steam::compatdata(&lib.path, app_id).join("pfx"))
            .find(|p| p.join("dosdevices").is_dir());
        (Platform::Proton, guess)
    };
    let flavor: Option<SteamFlavor> = None;
    let path_mode = path_mode_for(platform, flavor, prefix.as_deref(), data_root);
    Ok(GameInstall {
        id: install_id_for(&dir),
        kind,
        path: dir,
        exe: exe.to_string(),
        app_id,
        library: None,
        platform,
        prefix,
        build_id: None,
        exe_sha256: None,
        steam_flavor: if cfg!(windows) { Some(SteamFlavor::Windows) } else { None },
        path_mode,
        source: "manual".into(),
    })
}

/// Describes the game folder at `path` (exe, platform, prefix, path mode).
pub fn inspect(path: &Path) -> Result<GameInstall> {
    inspect_with(path, &steam::roots().unwrap_or_default(), &default_data_root())
}

fn record_path(dirs: &DataDirs, id: &str) -> Result<PathBuf> {
    if id.is_empty() || id.len() > 64 || !id.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'-') {
        return Err(Error::invalid(format!("not an install id: {id:?}")));
    }
    Ok(dirs.installs().join(format!("{id}.json")))
}

fn write_atomic(path: &Path, bytes: &[u8]) -> Result<()> {
    let dir = path.parent().ok_or_else(|| Error::invalid(format!("{}: no parent", path.display())))?;
    std::fs::create_dir_all(dir)?;
    let tmp = dir.join(format!(".{}.tmp{}", path.file_name().unwrap_or_default().to_string_lossy(), std::process::id()));
    {
        use std::io::Write;
        let mut f = std::fs::File::create(&tmp)?;
        f.write_all(bytes)?;
        f.sync_all()?;
    }
    std::fs::rename(&tmp, path).inspect_err(|_| {
        let _ = std::fs::remove_file(&tmp);
    })?;
    Ok(())
}

/// The record of an install, None when the launcher does not know it.
pub fn record(dirs: &DataDirs, id: &str) -> Result<Option<InstallRecord>> {
    let p = record_path(dirs, id)?;
    match std::fs::read_to_string(&p) {
        Ok(t) => Ok(Some(serde_json::from_str(&t).map_err(|e| Error::from(e).with_detail(p.display().to_string()))?)),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
        Err(e) => Err(e.into()),
    }
}

/// Writes `<data>/installs/<id>.json` atomically (keeps `added`, sets `updated`). Removes other records of the same
/// folder (an install's id changes once Kiln is installed: path hash -> receipt id).
pub fn write_record(dirs: &DataDirs, install: &GameInstall, receipt: Option<serde_json::Value>) -> Result<InstallRecord> {
    let now = crate::saves::rfc3339(std::time::SystemTime::now());
    let old = record(dirs, &install.id)?;
    let rec = InstallRecord {
        format: 1,
        install: install.clone(),
        receipt,
        added: old.and_then(|o| o.added).or_else(|| Some(now.clone())),
        updated: Some(now),
    };
    let mut text = serde_json::to_string_pretty(&rec)?;
    text.push('\n');
    write_atomic(&record_path(dirs, &install.id)?, text.as_bytes())?;
    for other in known(dirs)? {
        if other.id != install.id && steam::same_path(&canon(&other.path), &canon(&install.path)) {
            let _ = std::fs::remove_file(record_path(dirs, &other.id)?);
        }
    }
    Ok(rec)
}

/// The installs the launcher knows (`<data>/installs/*.json`).
pub fn known(dirs: &crate::paths::DataDirs) -> Result<Vec<GameInstall>> {
    let mut out = Vec::new();
    let rd = match std::fs::read_dir(dirs.installs()) {
        Ok(rd) => rd,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(out),
        Err(e) => return Err(e.into()),
    };
    let mut files: Vec<PathBuf> = rd.flatten().map(|e| e.path()).filter(|p| p.extension().is_some_and(|x| x == "json")).collect();
    files.sort();
    for f in files {
        let Ok(text) = std::fs::read_to_string(&f) else { continue };
        if let Ok(r) = serde_json::from_str::<InstallRecord>(&text) {
            out.push(r.install);
        }
    }
    Ok(out)
}

/// Adds (or refreshes) the install at `path`: inspect, exe sha256, record.
pub fn add(dirs: &crate::paths::DataDirs, path: &Path) -> Result<GameInstall> {
    let roots = steam::roots().unwrap_or_default();
    let mut g = inspect_with(path, &roots, &dirs.root)?;
    g.exe_sha256 = crate::hash::sha256_file(&g.path.join(&g.exe)).ok().map(|(h, _)| h);
    write_record(dirs, &g, receipt_json(&g.path))?;
    Ok(g)
}

pub fn remove(dirs: &crate::paths::DataDirs, id: &str) -> Result<()> {
    let p = record_path(dirs, id)?;
    match std::fs::remove_file(&p) {
        Ok(()) => Ok(()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Err(Error::not_found(format!("no game install {id}"))),
        Err(e) => Err(e.into()),
    }
}

/// The value of `WINEDLLOVERRIDES=` in a launch options line (quoted or bare), if any.
pub fn overrides_in_launch_options(options: &str) -> Option<String> {
    let i = options.find("WINEDLLOVERRIDES=")?;
    let rest = &options[i + "WINEDLLOVERRIDES=".len()..];
    let v = if let Some(q) = rest.strip_prefix('"') {
        q.split('"').next().unwrap_or_default()
    } else if let Some(q) = rest.strip_prefix('\'') {
        q.split('\'').next().unwrap_or_default()
    } else {
        rest.split_whitespace().next().unwrap_or_default()
    };
    Some(v.to_string())
}

/// `WINEDLLOVERRIDES` text loads dinput8 native first (`dinput8=n,b`, `dinput8=native`, later entries win).
pub fn overrides_dinput8_native(overrides: &str) -> bool {
    let (mut explicit, mut wildcard): (Option<String>, Option<String>) = (None, None);
    for entry in overrides.split(';') {
        let Some((dlls, m)) = entry.split_once('=') else { continue };
        let m = m.trim().to_ascii_lowercase().replace(' ', "");
        for dll in dlls.split(',') {
            let d = dll.trim().to_ascii_lowercase();
            match d.strip_suffix(".dll").unwrap_or(&d) {
                "dinput8" => explicit = Some(m.clone()),
                "*" => wildcard = Some(m.clone()),
                _ => {}
            }
        }
    }
    explicit.or(wildcard).and_then(|m| m.split(',').next().map(str::to_string)).is_some_and(|f| f == "n" || f == "native")
}

/// A prefix's user.reg has `[Software\\Wine\\DllOverrides]` `"dinput8"="native..."` (or `"*dinput8"`).
pub fn user_reg_dinput8_native(user_reg: &str) -> bool {
    let mut inside = false;
    let mut native = false;
    for l in user_reg.lines() {
        let l = l.trim();
        if l.starts_with('[') {
            inside = l.to_ascii_lowercase().starts_with("[software\\\\wine\\\\dlloverrides]");
            continue;
        }
        if !inside {
            continue;
        }
        let Some((k, v)) = l.split_once('=') else { continue };
        let k = k.trim().trim_matches('"').to_ascii_lowercase();
        if k == "dinput8" || k == "*dinput8" {
            let v = v.trim().trim_matches('"').to_ascii_lowercase();
            native = v.split(',').next().is_some_and(|f| f == "native" || f == "n");
        }
    }
    native
}

/// Launch options facts of an install (read-only): Proton clients only; None elsewhere.
pub fn launch_options_info(install: &GameInstall, roots: &[SteamRoot]) -> Option<LaunchOptionsInfo> {
    if install.platform != Platform::Proton {
        return None;
    }
    let candidates: Vec<SteamRoot> = match install.library.as_deref().and_then(|l| steam::root_of_library(roots, l)) {
        Some(r) => vec![r],
        None => roots.to_vec(),
    };
    let value = candidates
        .iter()
        .flat_map(|r| steam::launch_options(r, install.app_id).unwrap_or_default())
        .map(|o| o.value)
        .next();
    let from_options = value.as_deref().and_then(overrides_in_launch_options).is_some_and(|o| overrides_dinput8_native(&o));
    let from_prefix = install
        .prefix
        .as_deref()
        .and_then(|p| std::fs::read(p.join("user.reg")).ok())
        .is_some_and(|b| user_reg_dinput8_native(&String::from_utf8_lossy(&b)));
    let has = from_options || from_prefix;
    Some(LaunchOptionsInfo { value, has_dll_override: has, advice: (!has).then(|| DLL_OVERRIDE_ADVICE.to_string()) })
}

fn fallback_install_state(install: &GameInstall) -> crate::install::InstallState {
    let receipt = std::fs::read_to_string(install.path.join(crate::paths::game::RECEIPT))
        .ok()
        .and_then(|t| serde_json::from_str::<crate::install::InstallReceipt>(&t).ok());
    crate::install::InstallState {
        installed: receipt.is_some(),
        receipt,
        proxy: None,
        modified: Vec::new(),
        upgrade_available: None,
        missing_parts: Vec::new(),
    }
}

/// `GameStatus.newer_mods`: the active profile's Kiln mods built against a newer Kiln than the installed runtime.
pub fn newer_mods(dirs: &DataDirs, runtime: &crate::install::InstallState, active_profile: Option<&str>) -> Vec<crate::profile::NewerKilnMod> {
    let Some(receipt) = runtime.receipt.as_ref() else { return Vec::new() };
    let Some(profile) = active_profile.and_then(|id| crate::profile::get(dirs, id).ok()) else { return Vec::new() };
    let Some(build) = crate::profile::runtime_build(dirs, receipt) else { return Vec::new() };
    crate::profile::built_for_newer(dirs, &profile, &build)
}

/// `status` with explicit Steam roots.
pub fn status_with(dirs: &DataDirs, install: &GameInstall, roots: &[SteamRoot]) -> Result<GameStatus> {
    let proc = crate::proc::find_game(install).ok().flatten();
    let steam_updating = install
        .library
        .as_deref()
        .and_then(|l| steam::app_manifest(l, install.app_id).ok().flatten())
        .is_some_and(|m| m.updating);
    let runtime = crate::install::status(dirs, install).unwrap_or_else(|_| fallback_install_state(install));
    let active_profile = crate::profile::read_game_profile(&install.path).ok().flatten().map(|p| p.id);
    let boot = crate::state::read_boot(&install.path).ok().flatten();
    let newer_mods = newer_mods(dirs, &runtime, active_profile.as_deref());
    Ok(GameStatus {
        id: install.id.clone(),
        running: proc.is_some(),
        pid: proc.as_ref().map(|p| p.pid),
        started: proc.as_ref().and_then(|p| crate::proc::start_rfc3339(p.pid)),
        steam_updating,
        runtime,
        active_profile,
        play_once_pending: install.path.join(crate::paths::game::PLAY_ONCE).is_file(),
        boot,
        launch_options: launch_options_info(install, roots),
        newer_mods,
    })
}

pub fn status(dirs: &crate::paths::DataDirs, install: &GameInstall) -> Result<GameStatus> {
    status_with(dirs, install, &steam::roots().unwrap_or_default())
}

/// Z when `<prefix>/dosdevices/z:` resolves to `/` and the launcher data dir is visible to the game (not Flatpak/Snap
/// Steam), Native on Windows, else Copy.
pub fn path_mode(install: &GameInstall, dirs: &crate::paths::DataDirs) -> Result<PathMode> {
    Ok(path_mode_for(install.platform, install.steam_flavor, install.prefix.as_deref(), &dirs.root))
}
