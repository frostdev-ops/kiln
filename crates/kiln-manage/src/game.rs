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
    /// Ultimate ASI Loader is chained under Proton without its native-first override.
    #[serde(default)]
    pub chained_override_missing: bool,
    /// What the user should change, e.g. `WINEDLLOVERRIDES="dinput8=n,b" %command%`.
    pub advice: Option<String>,
    /// The remedy retained for a real "No Kiln" failure, even on a runner that normally loads dinput8.
    #[serde(default)]
    pub fallback_advice: Option<String>,
    /// The selected, known Steam compatibility tool prefers native dinput8 without an override.
    #[serde(default)]
    pub dinput8_native_by_runner: bool,
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
    /// The profile of this data folder that is active in the game. None also when another data folder's profile is
    /// (then `other_folder_profile`).
    pub active_profile: Option<String>,
    /// Another data folder's profile is active in the game (another launcher, a `KILN_DATA_DIR`, a launcher before
    /// dev.26 after a move: `profile::game_profile_owner`): said, never shown as loaded. Left out when there is none.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub other_folder_profile: Option<OtherFolderProfile>,
    /// kiln/play-once.ini still exists (the proxy has not consumed it).
    pub play_once_pending: bool,
    pub boot: Option<BootJson>,
    pub launch_options: Option<LaunchOptionsInfo>,
    /// Kiln mods of the active profile built against a newer Kiln than the one in the game (`kilnBuild` against the
    /// runtime's): they may fail to load until Kiln is updated on the Runtime page.
    #[serde(default)]
    pub newer_mods: Vec<crate::profile::NewerKilnMod>,
    /// (dev.29.17) How this client starts (`direct::effective_mode`: through Steam, or directly); left out for a server.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub start: Option<crate::direct::StartMode>,
    /// (dev.29.17) Whether Kiln knows the build of the game's exe (`direct::build_check`); left out when it cannot be
    /// told (the exe unreadable, no runtime in the game and none in the launcher).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub build: Option<crate::direct::BuildCheck>,
    /// (dev.29.18) Graphics tools in the game folder that run on their own next to Kiln and change what Kiln's in-game
    /// overlay does (OptiScaler: `graphics_tools`), one note each. Left out when there is none.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub graphics_tools: Vec<GraphicsToolNote>,
    /// (dev.29.18) Tools in the game folder that hook what Kiln's graphics tools hook (kiln.json "graphics": the frame
    /// cap, DLSS settings): Kiln's step aside next to them (`graphics_step_aside`). Left out when there is none.
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub graphics_step_aside: Vec<GraphicsStepAside>,
}

/// `GameStatus.graphics_step_aside`: a tool in the game folder that makes Kiln's graphics tools step aside.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GraphicsStepAside {
    /// "optiscaler", "streamline", "vkfg", "dlsstweaks" or "nvngx".
    pub kind: String,
    /// The tool's file relative to the game folder, forward slashes, as it is named on disk.
    pub file: String,
    /// Kiln's frame cap steps aside too (else only Kiln's DLSS settings do; the readout always works).
    pub frame_cap: bool,
}

/// The entry in `dir` whose name is `name` in any case (Windows and Wine match names so; the game folder may sit on a
/// case-sensitive file system) and that is a file (`want_dir` false) or a folder, a symbolic link followed as the game
/// follows it (Linux mod managers link mods into the game folder). The first in sorted order when several match.
fn entry_named(dir: &Path, name: &str, want_dir: bool) -> Option<String> {
    let mut found: Vec<String> = std::fs::read_dir(dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().eq_ignore_ascii_case(name))
        .filter(|e| std::fs::metadata(e.path()).is_ok_and(|m| if want_dir { m.is_dir() } else { m.is_file() }))
        .map(|e| e.file_name().to_string_lossy().into_owned())
        .collect();
    found.sort();
    found.into_iter().next()
}

/// The file in `dir` whose name is `name` in any case (`entry_named`).
fn file_named(dir: &Path, name: &str) -> Option<String> {
    entry_named(dir, name, false)
}

/// The directory in `dir` whose name is `name` in any case (`entry_named`).
fn dir_named(dir: &Path, name: &str) -> Option<String> {
    entry_named(dir, name, true)
}

/// The tools in a game folder next to which Kiln's graphics tools (kiln.json "graphics") step aside, in this order:
/// OptiScaler (`engines::optiscaler_in`), NVIDIA Streamline (`sl.interposer.dll` in the folder or `Streamline/`), a DLSS
/// frame generation mod (`EnshroudedVKFG.ini`), DLSSTweaks (`dlsstweaks.ini`) and another DLSS loader (`nvngx.dll` or
/// `_nvngx.dll`; the game's own `nvngx_dlss.dll` is not one). The first three take the frame cap too. Names match in
/// any case.
pub fn graphics_step_aside(game_dir: &Path) -> Vec<GraphicsStepAside> {
    let entry = |kind: &str, file: String, frame_cap: bool| GraphicsStepAside { kind: kind.to_string(), file, frame_cap };
    let mut out = Vec::new();
    if let Some(file) = crate::engines::optiscaler_in(game_dir) {
        out.push(entry("optiscaler", file, true));
    }
    let streamline = file_named(game_dir, "sl.interposer.dll").or_else(|| {
        let sub = dir_named(game_dir, "Streamline")?;
        file_named(&game_dir.join(&sub), "sl.interposer.dll").map(|f| format!("{sub}/{f}"))
    });
    if let Some(file) = streamline {
        out.push(entry("streamline", file, true));
    }
    if let Some(file) = file_named(game_dir, "EnshroudedVKFG.ini") {
        out.push(entry("vkfg", file, true));
    }
    if let Some(file) = file_named(game_dir, "dlsstweaks.ini") {
        out.push(entry("dlsstweaks", file, false));
    }
    if let Some(file) = file_named(game_dir, "nvngx.dll").or_else(|| file_named(game_dir, "_nvngx.dll")) {
        out.push(entry("nvngx", file, false));
    }
    out
}

/// `GameStatus.graphics_tools`: one tool and what to expect from Kiln's overlay next to it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct GraphicsToolNote {
    pub engine: crate::engines::Engine,
    /// The tool's file directly in the game folder (`dxgi.dll`, `OptiScaler.asi`).
    pub file: String,
    /// What to expect from Kiln's overlay, one or two sentences for the Play page (its title names the tool and file).
    pub text: String,
    /// Kiln's overlay draws next to it (OptiScaler: its own overlay menu off in OptiScaler.ini); false: kiln.ui's
    /// drawing steps aside on its own.
    pub kiln_overlay: bool,
}

/// The graphics tools in a game folder that change what Kiln's overlay does (`GameStatus.graphics_tools`).
pub fn graphics_tools(game_dir: &Path) -> Vec<GraphicsToolNote> {
    crate::engines::optiscaler_in(game_dir)
        .map(|file| {
            let ini = crate::engines::OptiScalerIni::read(game_dir);
            GraphicsToolNote {
                engine: crate::engines::Engine::OptiScaler,
                text: crate::engines::optiscaler_overlay_note(&ini),
                kiln_overlay: ini.kiln_overlay_draws(),
                file,
            }
        })
        .into_iter()
        .collect()
}

/// `GameStatus.other_folder_profile`: the id of another data folder's profile active in the game, and the data folder
/// its paths name (Z and native mode; left out in copy mode).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OtherFolderProfile {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub root: Option<PathBuf>,
}

pub const CLIENT_EXE: &str = "enshrouded.exe";
pub const SERVER_EXE: &str = "enshrouded_server.exe";
/// What a Proton launch needs so the game loads Kiln's proxy (shown, never written: Kiln never edits Steam's config).
pub const DLL_OVERRIDE_ADVICE: &str = "WINEDLLOVERRIDES=\"dinput8=n,b\" %command%";
/// Proton advice when Ultimate ASI Loader is chained next to the game.
pub const CHAINED_DLL_OVERRIDE_ADVICE: &str = "WINEDLLOVERRIDES=\"dinput8,xinput9_1_0=n,b\" %command%";

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
    /// (dev.29.17) How the player wants this install started (`direct::StartSettings`): left out while every choice
    /// is Kiln's default (through Steam for a Steam install, directly for a folder no Steam library knows).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub start: Option<crate::direct::StartSettings>,
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
    // inside a Steam library: the same facts detect gives. Not when no Steam is installed at all: a library left on a
    // drive (another system's, Steam uninstalled) is a folder Steam's Play cannot start here, so it is a custom folder
    // (direct.rs), keeping the manifest's build id.
    let manifest = library_of(&dir).and_then(|lib| {
        let m = steam::app_manifest(&lib, app_id).ok().flatten()?;
        steam::same_path(&canon(&m.install_dir), &dir).then_some((lib, m))
    });
    if let Some((lib, m)) = &manifest
        && !roots.is_empty()
    {
        let root = steam::root_of_library(roots, lib)
            .unwrap_or(SteamRoot { path: lib.clone(), flavor: steam::flavor_of(lib) });
        if let Some(g) = from_steam(&root, lib, m, data_root) {
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
        build_id: manifest.and_then(|(_, m)| m.build_id),
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
        added: old.as_ref().and_then(|o| o.added.clone()).or_else(|| Some(now.clone())),
        updated: Some(now),
        // the player's start choice stays with the record whoever writes it again (an install, a move, Add again)
        start: old.and_then(|o| o.start),
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
    add_with(dirs, path, &steam::roots().unwrap_or_default())
}

/// `add` with explicit Steam roots. A folder that starts directly (a custom folder by default, or the player's choice
/// kept in its record) gets the platform and prefix of that start ([`apply_start`]).
pub fn add_with(dirs: &crate::paths::DataDirs, path: &Path, roots: &[SteamRoot]) -> Result<GameInstall> {
    add_on(dirs, path, roots, &crate::direct::Host::current())
}

/// [`add_with`] on a given host (the runners and Kiln's prefixes a direct start would use: tests give fixture folders).
pub fn add_on(dirs: &crate::paths::DataDirs, path: &Path, roots: &[SteamRoot], host: &crate::direct::Host) -> Result<GameInstall> {
    let mut g = inspect_with(path, roots, &dirs.root)?;
    g.exe_sha256 = crate::hash::sha256_file(&g.path.join(&g.exe)).ok().map(|(h, _)| h);
    let settings = record(dirs, &g.id).ok().flatten().and_then(|r| r.start).unwrap_or_default();
    apply_start_on(&mut g, &settings, roots, &dirs.root, host);
    write_record(dirs, &g, receipt_json(&g.path))?;
    Ok(g)
}

/// The install as it runs with `settings`: a direct start under umu-run, Proton or Wine (Linux) runs in its own prefix, which
/// is the install's `prefix` and `platform` then (saves, path mode, logs follow it); a start through Steam keeps what
/// `inspect` found. Windows and servers are left as they are.
pub fn apply_start(install: &mut GameInstall, settings: &crate::direct::StartSettings, roots: &[SteamRoot], data_root: &Path) {
    apply_start_on(install, settings, roots, data_root, &crate::direct::Host::current())
}

/// [`apply_start`] on a given host.
pub fn apply_start_on(install: &mut GameInstall, settings: &crate::direct::StartSettings, roots: &[SteamRoot], data_root: &Path,
                      host: &crate::direct::Host) {
    if install.kind != GameKind::Client || cfg!(windows) {
        return;
    }
    if crate::direct::effective_mode(install, Some(settings)) == crate::direct::StartMode::Direct
        && let Some((platform, pfx)) = crate::direct::prefix_on(install, settings, roots, host)
    {
        install.platform = platform;
        install.prefix = Some(pfx);
        install.path_mode = path_mode_for(platform, install.steam_flavor, install.prefix.as_deref(), data_root);
    }
}

/// Changes how an install starts (`game_start_set`): validates the choice, keeps it in the record and writes the
/// install's platform and prefix for it again. A server, or a Proton/Wine/prefix choice on Windows, is refused.
pub fn set_start(dirs: &DataDirs, id: &str, settings: &crate::direct::StartSettings, roots: &[SteamRoot]) -> Result<GameInstall> {
    set_start_on(dirs, id, settings, roots, &crate::direct::Host::current())
}

/// [`set_start`] on a given host.
pub fn set_start_on(dirs: &DataDirs, id: &str, settings: &crate::direct::StartSettings, roots: &[SteamRoot],
                    host: &crate::direct::Host) -> Result<GameInstall> {
    let rec = record(dirs, id)?.ok_or_else(|| Error::not_found(format!("no game install {id}")))?;
    let old = rec.install.clone();
    if old.kind != GameKind::Client {
        return Err(Error::invalid("a dedicated server starts on the Servers page; its start is not set here"));
    }
    if cfg!(windows) && (settings.runner.is_some() || settings.prefix.is_some()) {
        return Err(Error::invalid("umu-run, Proton, Wine and a prefix are for Linux: Windows starts the game itself"));
    }
    if let Some(r) = &settings.runner {
        crate::direct::runner_at(r)?;
    }
    if let Some(p) = &settings.prefix {
        if !p.is_absolute() {
            return Err(Error::invalid(format!("the prefix must be an absolute path, not {}", p.display())));
        }
        if p.exists() && !p.is_dir() {
            return Err(Error::invalid(format!("{} is not a folder", p.display())));
        }
        let kind = match &settings.runner {
            Some(r) => crate::direct::runner_at(r)?.kind,
            None => crate::direct::default_runner(&crate::direct::runners(roots, host))
                .map(|r| r.kind).unwrap_or(crate::direct::RunnerKind::Proton),
        };
        crate::direct::prefix_paths(kind, Some(p), p)?;
    }
    // what the folder is without a direct start (Steam's facts, or inspect's guess), then the start's own
    let mut g = inspect_with(&old.path, roots, &dirs.root).map(|mut fresh| {
        fresh.id = old.id.clone();
        fresh.exe_sha256 = old.exe_sha256.clone().or(fresh.exe_sha256);
        fresh.build_id = fresh.build_id.or(old.build_id.clone());
        fresh
    }).unwrap_or(old);
    apply_start_on(&mut g, settings, roots, &dirs.root, host);
    let mut new = InstallRecord { start: (settings != &crate::direct::StartSettings::default()).then(|| settings.clone()), ..rec };
    new.install = g.clone();
    new.updated = Some(crate::saves::rfc3339(std::time::SystemTime::now()));
    let mut text = serde_json::to_string_pretty(&new)?;
    text.push('\n');
    write_atomic(&record_path(dirs, id)?, text.as_bytes())?;
    Ok(g)
}

/// The install with the platform and prefix its start uses now (a launcher from before dev.29.17 recorded a custom
/// folder with Steam's prefix): written again when they differ. Cheap enough for the game list and Play.
pub fn sync_start(dirs: &DataDirs, install: &GameInstall, roots: &[SteamRoot]) -> Result<GameInstall> {
    if install.kind != GameKind::Client || cfg!(windows) {
        return Ok(install.clone());
    }
    let Some(rec) = record(dirs, &install.id)? else { return Ok(install.clone()) };
    let settings = rec.start.clone().unwrap_or_default();
    if crate::direct::effective_mode(&rec.install, Some(&settings)) != crate::direct::StartMode::Direct {
        return Ok(rec.install);
    }
    let mut g = rec.install.clone();
    apply_start(&mut g, &settings, roots, &dirs.root);
    if g != rec.install {
        write_record(dirs, &g, rec.receipt)?;
    }
    Ok(g)
}

/// Ultimate ASI Loader is chained next to the game (by Kiln's installer `[chain]`, or by activation for the profile's
/// `.asi` packages `[profile]`), so Wine must load `xinput9_1_0` native first too.
pub fn asi_loader_chained(install: &GameInstall) -> bool {
    crate::chain::read(&install.path).is_ok_and(|ini| ini.chain.iter().any(|e| {
        e.status == crate::chain::EntryStatus::Ok
            && e.path.rsplit('\\').next().is_some_and(|n| n.eq_ignore_ascii_case(crate::install::ASI_LOADER_NAME))
    }) || crate::game_files::ual_in_profile_section(&ini, &install.path)) && std::fs::read_dir(&install.path).is_ok_and(|entries| entries.flatten().any(|e| {
        e.file_name().to_string_lossy().eq_ignore_ascii_case(crate::install::ASI_LOADER_NAME) && e.path().is_file()
    }))
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
    overrides_native(overrides, "dinput8")
}

/// Native-first load order for a DLL name; explicit entries beat the wildcard, later entries win.
pub fn overrides_native(overrides: &str, name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    let name = name.strip_suffix(".dll").unwrap_or(&name);
    let (mut explicit, mut wildcard): (Option<String>, Option<String>) = (None, None);
    for entry in overrides.split(';') {
        let Some((dlls, m)) = entry.split_once('=') else { continue };
        let m = m.trim().to_ascii_lowercase().replace(' ', "");
        for dll in dlls.split(',') {
            let d = dll.trim().to_ascii_lowercase();
            match d.strip_suffix(".dll").unwrap_or(&d) {
                d if d == name => explicit = Some(m.clone()),
                "*" => wildcard = Some(m.clone()),
                _ => {}
            }
        }
    }
    explicit.or(wildcard).and_then(|m| m.split(',').next().map(str::to_string)).is_some_and(|f| f == "n" || f == "native")
}

/// A prefix's user.reg has `[Software\\Wine\\DllOverrides]` `"dinput8"="native..."` (or `"*dinput8"`).
pub fn user_reg_dinput8_native(user_reg: &str) -> bool {
    user_reg_native(user_reg, "dinput8")
}

/// A DLL override in the prefix registry, including Wine's optional leading `*`.
pub fn user_reg_native(user_reg: &str, name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    let name = name.strip_suffix(".dll").unwrap_or(&name);
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
        let k = k.strip_prefix('*').unwrap_or(&k);
        if k.strip_suffix(".dll").unwrap_or(k) == name {
            let v = v.trim().trim_matches('"').to_ascii_lowercase();
            native = v.split(',').next().is_some_and(|f| f == "native" || f == "n");
        }
    }
    native
}

/// Known Steam runners that load the game's native dinput8.dll without WINEDLLOVERRIDES.
/// Keep this in one place: unknown runners still get conservative advice.
fn runner_loads_native_dinput8(tool: &str) -> bool {
    matches!(crate::wine::ge_version(tool).as_deref(), Some([major, minor, ..]) if (*major, *minor) >= (11, 11))
}

/// Launch options facts of an install (read-only): Proton clients only; None elsewhere.
pub fn launch_options_info(install: &GameInstall, roots: &[SteamRoot]) -> Option<LaunchOptionsInfo> {
    if install.platform != Platform::Proton {
        return None;
    }
    // Having GE-Proton installed does not mean Steam selected it for this app.
    // Prefer the per-app mapping, then Steam's global default (CompatToolMapping/0).
    // Keep compat_tool itself per-app-only for every other caller.
    let root = install.library.as_deref().and_then(|l| steam::root_of_library(roots, l));
    let dinput8_native_by_runner = root.as_ref()
        .and_then(|r| {
            steam::compat_tool(r, install.app_id).ok().flatten()
                .or_else(|| steam::compat_tool(r, 0).ok().flatten())
        })
        .as_deref()
        .is_some_and(runner_loads_native_dinput8);
    let candidates: Vec<SteamRoot> = match root {
        Some(r) => vec![r],
        None => roots.to_vec(),
    };
    let value = candidates
        .iter()
        .flat_map(|r| steam::launch_options(r, install.app_id).unwrap_or_default())
        .map(|o| o.value)
        .next();
    let overrides = value.as_deref().and_then(overrides_in_launch_options);
    let registry = install.prefix.as_deref().and_then(|p| std::fs::read(p.join("user.reg")).ok());
    let native = |name| {
        overrides.as_deref().is_some_and(|o| overrides_native(o, name))
            || registry.as_deref().is_some_and(|b| user_reg_native(&String::from_utf8_lossy(b), name))
    };
    // chained by Kiln's installer ([chain]), or by activation for the profile's .asi packages ([profile], game_files)
    let chained = asi_loader_chained(install);
    let has = native("dinput8");
    let chained_override_missing = chained && !native("xinput9_1_0");
    let remedy = if chained { CHAINED_DLL_OVERRIDE_ADVICE } else { DLL_OVERRIDE_ADVICE };
    Some(LaunchOptionsInfo {
        value,
        has_dll_override: has,
        chained_override_missing,
        advice: ((!has && !dinput8_native_by_runner) || chained_override_missing).then(|| remedy.to_string()),
        // If the game actually fails to load Kiln, always offer the override as a fallback.
        fallback_advice: (!has || chained_override_missing).then(|| remedy.to_string()),
        dinput8_native_by_runner,
    })
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
        switch_back: None,
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
    let (active_profile, other_folder_profile) = match crate::profile::game_profile_owner(dirs, install) {
        Ok(crate::profile::GameProfileOwner::Here { id }) => (Some(id), None),
        Ok(crate::profile::GameProfileOwner::Elsewhere { id, root }) => (None, Some(OtherFolderProfile { id, root })),
        _ => (None, None),
    };
    let boot = crate::state::read_boot(&install.path).ok().flatten();
    let newer_mods = newer_mods(dirs, &runtime, active_profile.as_deref());
    let start = (install.kind == GameKind::Client).then(|| crate::direct::mode_of(dirs, install));
    let side = if install.kind == GameKind::Client { crate::manifest::RunSide::Client } else { crate::manifest::RunSide::Server };
    let build = crate::direct::build_check(install, || crate::direct::newest_bundle(dirs, side));
    Ok(GameStatus {
        id: install.id.clone(),
        running: proc.is_some(),
        pid: proc.as_ref().map(|p| p.pid),
        started: proc.as_ref().and_then(|p| crate::proc::start_rfc3339(p.pid)),
        steam_updating,
        runtime,
        active_profile,
        other_folder_profile,
        play_once_pending: install.path.join(crate::paths::game::PLAY_ONCE).is_file(),
        boot,
        launch_options: if start == Some(crate::direct::StartMode::Direct) { None } else { launch_options_info(install, roots) },
        newer_mods,
        start,
        build,
        graphics_tools: if install.kind == GameKind::Client { graphics_tools(&install.path) } else { Vec::new() },
        graphics_step_aside: if install.kind == GameKind::Client { graphics_step_aside(&install.path) } else { Vec::new() },
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

#[cfg(test)]
mod override_tests {
    use super::*;

    #[test]
    fn named_native_overrides() {
        for name in ["dinput8", "xinput9_1_0", "XINPUT9_1_0.DLL"] {
            assert!(overrides_native("DINPUT8.dll,XInput9_1_0.DLL=n,b", name));
            assert!(overrides_native("*=native,builtin", name));
        }
        assert!(overrides_native("dinput8=n,b;xinput9_1_0=b", "dinput8"));
        assert!(!overrides_native("dinput8=n,b;xinput9_1_0=b", "xinput9_1_0"));
        assert!(!overrides_native("*=n;xinput9_1_0=b,n", "xinput9_1_0"));
        assert!(!overrides_native("xinput9_1_0=n;xinput9_1_0=b", "xinput9_1_0"));
        assert!(overrides_native("xinput9_1_0=b;xinput9_1_0=n", "xinput9_1_0"));
        assert!(!overrides_native("dinput8=n,b", "xinput9_1_0"));
    }

    #[test]
    fn named_registry_overrides() {
        let reg = "[Software\\\\Wine\\\\DllOverrides]\n\"xinput9_1_0\"=\"native,builtin\"\n";
        assert!(user_reg_native(reg, "xinput9_1_0"));
        assert!(!user_reg_dinput8_native(reg));
        assert!(user_reg_native(&reg.replace("xinput9_1_0", "*XINPUT9_1_0.DLL"), "xinput9_1_0.dll"));
        assert!(!user_reg_native(&reg.replace("native,builtin", "builtin,native"), "xinput9_1_0"));
        assert!(!user_reg_native(&reg.replace("DllOverrides", "Other"), "xinput9_1_0"));
        assert!(!user_reg_native(&format!("{reg}\"xinput9_1_0\"=\"builtin\"\n"), "xinput9_1_0"));
    }

    #[test]
    fn native_dinput8_runner_policy() {
        for tool in ["GE-Proton11-11-x86_64", "GE-Proton11-12-x86_64", "GE-Proton12-1-x86_64"] {
            assert!(runner_loads_native_dinput8(tool), "{tool}");
        }
        for tool in ["GE-Proton11-10-x86_64", "GE-Proton11-0.2", "GE-Proton10-25", "proton_experimental", "UMU-Proton10-4", ""] {
            assert!(!runner_loads_native_dinput8(tool), "{tool}");
        }
    }

    #[test]
    fn older_launch_options_info_defaults_chained_flag() {
        let info: LaunchOptionsInfo = serde_json::from_str(r#"{"value":null,"hasDllOverride":false,"advice":null}"#).unwrap();
        assert!(!info.chained_override_missing);
        assert!(!info.dinput8_native_by_runner);
        assert!(info.fallback_advice.is_none());
    }
}
