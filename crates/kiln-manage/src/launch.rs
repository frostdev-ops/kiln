//! Starting the game through Steam (`steam://rungameid/<app>`; never Steam's config), with the launch flow of
//! contracts.md: refuse while running, play-once for vanilla / no-mods / once, save backup when the mod-set
//! fingerprint changed, open the URI, then watch for the process and state/boot.json.

use crate::game::GameInstall;
use crate::paths::DataDirs;
use crate::saves::SaveBackup;
use crate::proc::ProcessInfo;
use crate::state::BootJson;
use crate::{Error, ErrorCode, Result};
use std::time::{Duration, Instant};
use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum LaunchMode {
    /// The activated profile (kiln/profile.json), as Steam's Play button would.
    Profile,
    /// play-once mode=vanilla: no Kiln, no chained DLLs.
    Vanilla,
    /// play-once mode=no-mods: Kiln without any mod.
    NoMods,
    /// play-once mode=profile: another profile, this start only (kiln/profile.once.json + kiln/chain/once.ini).
    Once,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchRequest {
    pub mode: LaunchMode,
    /// mode once: the profile to use.
    #[serde(default)]
    pub profile_id: Option<String>,
    /// Skip the automatic save backup.
    #[serde(default)]
    pub skip_backup: bool,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchResult {
    pub game_id: String,
    pub mode: LaunchMode,
    /// The play-once nonce (echoed in state/boot.json), None for mode profile.
    pub nonce: Option<String>,
    pub backup: Option<SaveBackup>,
    pub uri: String,
    pub started: String,
    /// Mode profile: the activation when the active profile had changed and was written again before the start
    /// (profile::refresh_active), with what it kept in the game folder (`retained`, `keptFiles`, `warnings`).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub activation: Option<crate::profile::Activation>,
}

/// `steam://rungameid/<app_id>`
pub fn steam_uri(app_id: u32) -> String {
    format!("steam://rungameid/{app_id}")
}

/// Only `steam://` URIs without whitespace, quotes or control characters are handed to the desktop.
fn check_uri(uri: &str) -> Result<()> {
    let ok = uri.starts_with("steam://")
        && uri.len() < 512
        && !uri.chars().any(|c| c.is_whitespace() || c.is_control() || matches!(c, '"' | '\'' | '`' | '<' | '>' | '|' | '^'));
    if ok { Ok(()) } else { Err(Error::invalid(format!("not a steam:// URI: {uri:?}"))) }
}

/// Validates `uri` and hands it to `opener` (tests pass a recorder; `open_uri` passes the desktop).
pub fn open_uri_with(uri: &str, opener: &dyn Fn(&str) -> Result<()>) -> Result<()> {
    check_uri(uri)?;
    opener(uri)
}

/// The desktop's opener: `xdg-open` on Linux (spawned detached, reaped on a thread), ShellExecuteW on Windows.
pub fn desktop_open(uri: &str) -> Result<()> {
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::Shell::ShellExecuteW;
        use windows_sys::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
        let wide = |s: &str| s.encode_utf16().chain(std::iter::once(0)).collect::<Vec<u16>>();
        let (verb, file) = (wide("open"), wide(uri));
        // SAFETY: NUL-terminated UTF-16 strings that outlive the call.
        let h = unsafe {
            ShellExecuteW(std::ptr::null_mut(), verb.as_ptr(), file.as_ptr(), std::ptr::null(), std::ptr::null(), SW_SHOWNORMAL)
        };
        if h as isize <= 32 {
            return Err(Error::new(ErrorCode::Unsupported, format!("ShellExecuteW({uri}) failed ({})", h as isize)));
        }
        Ok(())
    }
    #[cfg(not(windows))]
    {
        let mut child = std::process::Command::new("xdg-open")
            .arg(uri)
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .spawn()
            .map_err(|e| Error::new(ErrorCode::Unsupported, format!("starting xdg-open: {e}")))?;
        std::thread::spawn(move || {
            let _ = child.wait();
        });
        Ok(())
    }
}

/// Hands a steam:// URI to the desktop (xdg-open / ShellExecute).
pub fn open_uri(uri: &str) -> Result<()> {
    open_uri_with(uri, &desktop_open)
}

/// Polls the process list until this install's game runs (Some) or `timeout` passes (None).
pub fn wait_started(install: &GameInstall, timeout: Duration, poll: Duration) -> Result<Option<ProcessInfo>> {
    let t0 = Instant::now();
    loop {
        if let Some(p) = crate::proc::find_game(install)? {
            return Ok(Some(p));
        }
        if t0.elapsed() >= timeout {
            return Ok(None);
        }
        std::thread::sleep(poll.min(timeout.saturating_sub(t0.elapsed())).max(Duration::from_millis(10)));
    }
}

/// Whether `kiln/state/boot.json` belongs to the game process that runs now: the file was written after that process
/// started (`proc::start_time`, 2 s of slack for file-time precision). On Windows its `pid` must also be the pid of this
/// install's running exe (Windows reuses pids quickly: a pid that is merely alive may be another program now). Under
/// Wine/Proton the pid in the file is the game's Windows pid, which the host cannot see, so the times decide alone.
pub fn boot_is_current(install: &GameInstall, boot: &BootJson) -> bool {
    let Ok(Some(p)) = crate::proc::find_game(install) else { return false };
    if cfg!(windows) && p.pid != boot.pid {
        return false;
    }
    let written = std::fs::metadata(install.path.join(crate::paths::game::BOOT_JSON)).and_then(|m| m.modified()).ok();
    written_after_start(written, crate::proc::start_time(p.pid))
}

/// boot.json's write time against the process start; an unknown start counts as current (the process runs and the file
/// is there), an unknown write time as not.
pub(crate) fn written_after_start(written: Option<std::time::SystemTime>, started: Option<std::time::SystemTime>) -> bool {
    match (written, started) {
        (Some(w), Some(s)) => w + Duration::from_secs(2) >= s,
        (Some(_), None) => true,
        (None, _) => false,
    }
}

/// `kiln/state/boot.json` of the running game: current (see `boot_is_current`) and, when `nonce` is given, with that
/// play-once nonce. None otherwise.
pub fn read_boot_for(install: &GameInstall, nonce: Option<&str>) -> Result<Option<BootJson>> {
    let Some(boot) = crate::state::read_boot(&install.path)? else { return Ok(None) };
    if let Some(n) = nonce
        && boot.play_once.as_ref().and_then(|p| p.nonce.as_deref()) != Some(n)
    {
        return Ok(None);
    }
    Ok(boot_is_current(install, &boot).then_some(boot))
}

/// `<data>/installs/<installId>.launch.json`: what the last launch loaded (its kms1), for the save-backup rule.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LastLaunch {
    pub fingerprint: String,
    pub mode: LaunchMode,
    #[serde(default)]
    pub profile_id: Option<String>,
    pub launched: String,
}

fn last_launch_path(dirs: &DataDirs, install: &GameInstall) -> std::path::PathBuf {
    dirs.installs().join(format!("{}.launch.json", install.id))
}

pub fn last_launch(dirs: &DataDirs, install: &GameInstall) -> Result<Option<LastLaunch>> {
    crate::util::read_json(&last_launch_path(dirs, install))
}

/// kms1 of the legacy layout: the drop-in folders `kiln/mods/<folder>` (without `.disabled`) as kiln entries.
fn dropin_fingerprint(install: &GameInstall) -> String {
    let mut entries = Vec::new();
    for e in std::fs::read_dir(install.path.join(crate::paths::game::DROPIN_KILN)).into_iter().flatten().flatten() {
        let p = e.path();
        if !p.is_dir() || p.join(crate::paths::game::DISABLED_MARKER).exists() {
            continue;
        }
        let version = crate::manifest::read_kiln(&p).map(|m| m.version).unwrap_or_default();
        let id = crate::manifest::read_kiln(&p).map(|m| m.id).unwrap_or_else(|_| e.file_name().to_string_lossy().into_owned());
        entries.push(crate::hash::FingerprintEntry {
            kind: "kiln".into(),
            id,
            version,
            hash: crate::hash::dirhash(&p).unwrap_or_default(),
        });
    }
    crate::hash::fingerprint(entries)
}

/// The kms1 of what a launch in this mode loads (vanilla and no-mods: the empty set).
pub fn launch_fingerprint(dirs: &DataDirs, install: &GameInstall, request: &LaunchRequest) -> Result<String> {
    Ok(match request.mode {
        LaunchMode::Vanilla | LaunchMode::NoMods => crate::hash::fingerprint([]),
        LaunchMode::Once => {
            let pid = request.profile_id.as_deref().ok_or_else(|| Error::invalid("launch mode once needs a profileId"))?;
            crate::profile::fingerprint(&crate::profile::get(dirs, pid)?)
        }
        LaunchMode::Profile => match crate::profile::read_game_profile(&install.path)? {
            Some(f) => f.fingerprint.unwrap_or_else(|| crate::hash::fingerprint([])),
            None => dropin_fingerprint(install),
        },
    })
}

/// The launch flow (contracts.md 7) with explicit save folders (None: the install's) and URI opener.
pub fn play_with(dirs: &DataDirs, install: &GameInstall, request: &LaunchRequest, saves: Option<&[crate::saves::SaveDir]>,
                 opener: &dyn Fn(&str) -> Result<()>) -> Result<LaunchResult> {
    if let Some(p) = crate::proc::find_game(install)? {
        return Err(Error::new(ErrorCode::GameRunning, format!("{} is already running (pid {})", install.exe, p.pid))
            .with_detail(serde_json::json!({ "pid": p.pid })));
    }
    if install.kind != crate::game::GameKind::Client {
        return Err(Error::invalid("launch_play starts the game client; servers start through server_start"));
    }
    // the active profile as it is now: a profile changed since its activation (kilnctl, the profile pages) is
    // written again, or the game would load the mods of the old profile.json
    let activation = if request.mode == LaunchMode::Profile {
        crate::profile::refresh_active(dirs, install).map_err(|e| {
            let msg = format!("the active profile changed and cannot be applied: {}", e.message);
            Error { message: msg, ..e }
        })?
    } else { None };
    let fingerprint = launch_fingerprint(dirs, install, request)?;
    // 1. the files the proxy reads at this start
    let now = crate::util::now_unix();
    let nonce = match request.mode {
        LaunchMode::Profile => {
            crate::playonce::clear(&install.path)?; // a stale flag must not change Steam's Play-button start
            None
        }
        LaunchMode::Vanilla | LaunchMode::NoMods | LaunchMode::Once => {
            let mode = match request.mode {
                LaunchMode::Vanilla => crate::playonce::PlayOnceMode::Vanilla,
                LaunchMode::NoMods => crate::playonce::PlayOnceMode::NoMods,
                _ => {
                    let pid = request.profile_id.as_deref().ok_or_else(|| Error::invalid("launch mode once needs a profileId"))?;
                    crate::profile::write_once(dirs, pid, install)?;
                    crate::playonce::PlayOnceMode::Profile
                }
            };
            let flag = crate::playonce::PlayOnce { mode, created: now, nonce: crate::playonce::new_nonce()?, profile: None };
            crate::playonce::write(&install.path, &flag)?;
            Some(flag.nonce)
        }
    };
    // 2. a save backup when the mod set changes
    let last = last_launch(dirs, install).ok().flatten();
    let mut backup = None;
    if !request.skip_backup && last.as_ref().is_none_or(|l| l.fingerprint != fingerprint) {
        let dirs_list = match saves {
            Some(s) => s.to_vec(),
            None => crate::saves::save_dirs(install).unwrap_or_default(),
        };
        if !dirs_list.is_empty() {
            match crate::saves::backup_from(dirs, install, &dirs_list, crate::saves::BackupReason::Launch, None, Some(&fingerprint)) {
                Ok(b) => backup = Some(b),
                Err(e) => {
                    let _ = crate::playonce::clear(&install.path);
                    return Err(Error::new(e.code, format!("the save backup before the launch failed: {}", e.message)));
                }
            }
        }
    }
    // 3. Steam starts the game (never its config)
    let uri = steam_uri(install.app_id);
    if let Err(e) = open_uri_with(&uri, opener) {
        let _ = crate::playonce::clear(&install.path);
        return Err(e);
    }
    let started = crate::util::rfc3339(now);
    let rec = LastLaunch { fingerprint, mode: request.mode, profile_id: request.profile_id.clone(), launched: started.clone() };
    let _ = crate::util::write_json_atomic(&last_launch_path(dirs, install), &rec);
    Ok(LaunchResult { game_id: install.id.clone(), mode: request.mode, nonce, backup, uri, started, activation })
}

pub fn play(dirs: &DataDirs, install: &GameInstall, request: &LaunchRequest) -> Result<LaunchResult> {
    play_with(dirs, install, request, None, &desktop_open)
}

#[cfg(test)]
mod start_tests {
    use super::written_after_start;
    use std::time::{Duration, UNIX_EPOCH};

    #[test]
    fn boot_json_counts_only_when_written_after_the_process_started() {
        let start = UNIX_EPOCH + Duration::from_secs(1_793_970_000);
        assert!(written_after_start(Some(start + Duration::from_secs(5)), Some(start)), "written during the boot");
        assert!(written_after_start(Some(start - Duration::from_secs(1)), Some(start)), "file-time slack");
        assert!(!written_after_start(Some(start - Duration::from_secs(600)), Some(start)), "an earlier run's file");
        assert!(written_after_start(Some(start), None), "start unknown: the running game's file");
        assert!(!written_after_start(None, Some(start)), "no file time");
    }
}
