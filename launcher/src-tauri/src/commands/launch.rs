//! Tauri commands: launch. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! The launch flow is kiln_manage::launch::play (play-once/once files, save backup, steam:// URI); the watcher
//! (crate::watch) reports started/boot/mods/native/stopped as `kiln://game`.

use crate::state::{blocking, dirs};
use kiln_manage::Error;
use kiln_manage::launch::{LaunchRequest, LaunchResult};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};
use tauri::AppHandle;

/// `launch_state`: the game's state files, read together (launcher-only type).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootState {
    pub boot: Option<kiln_manage::state::BootJson>,
    pub mods: Option<kiln_manage::state::ModsJson>,
    pub native: Option<kiln_manage::state::NativeJson>,
    /// boot.json is not from the game process that runs now (`launch::boot_is_current`).
    pub stale: bool,
    /// The error lines kiln.log has for the run that wrote boot.json (errors after the boot: service ticks, mods,
    /// the menu). None when kiln.log is from another run or missing.
    #[serde(default)]
    pub log: Option<LogErrors>,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogErrors {
    /// `[E ...]` lines since the start.
    pub errors: u32,
    /// The first few, as written (trimmed to 300 characters).
    pub first: Vec<String>,
}

/// What has been counted of one kiln.log: its first line (a new run truncates the file and writes a new one), how far,
/// and the errors so far. launch_state runs every few seconds; only new bytes are read.
#[derive(Default)]
struct LogScan {
    head: String,
    offset: u64,
    errors: u32,
    first: Vec<String>,
}

static SCANS: std::sync::LazyLock<std::sync::Mutex<std::collections::HashMap<PathBuf, LogScan>>> = std::sync::LazyLock::new(Default::default);

fn count_errors(path: &Path) -> Option<(String, LogErrors)> {
    use std::io::{BufRead, Read, Seek, SeekFrom};
    let f = std::fs::File::open(path).ok()?;
    let len = f.metadata().ok()?.len();
    let mut r = std::io::BufReader::new(f);
    let mut head = Vec::new();
    r.by_ref().take(512).read_until(b'\n', &mut head).ok()?;
    let head = String::from_utf8_lossy(&head).trim_end().to_string();
    let mut scans = SCANS.lock().ok()?;
    let s = scans.entry(path.to_path_buf()).or_default();
    if s.head != head || len < s.offset {
        *s = LogScan { head: head.clone(), ..Default::default() };
    }
    r.seek(SeekFrom::Start(s.offset)).ok()?;
    let mut buf = Vec::new();
    r.read_to_end(&mut buf).ok()?;
    // whole lines only: a line being written is counted next time
    let end = buf.iter().rposition(|&b| b == b'\n').map_or(0, |i| i + 1);
    for line in buf[..end].split(|&b| b == b'\n') {
        if line.starts_with(b"[E ") {
            s.errors += 1;
            if s.first.len() < 5 {
                s.first.push(String::from_utf8_lossy(line).trim_end().chars().take(300).collect());
            }
        }
    }
    s.offset += end as u64;
    Some((head, LogErrors { errors: s.errors, first: s.first.clone() }))
}

/// kiln.log's errors when it is the log of the run that wrote `boot`: its start line names boot.json's pid, and
/// boot.json was written after the previous run's log (kiln.prev.log) ended. A start that died before writing boot.json
/// leaves an older boot.json behind, which kiln.log's errors do not belong to.
fn log_errors(game: &Path, boot: &kiln_manage::state::BootJson) -> Option<LogErrors> {
    let logs = game.join(kiln_manage::paths::game::LOGS);
    let boot_written = std::fs::metadata(game.join(kiln_manage::paths::game::BOOT_JSON)).and_then(|m| m.modified()).ok()?;
    if let Ok(prev) = std::fs::metadata(logs.join("kiln.prev.log")).and_then(|m| m.modified())
        && boot_written <= prev
    {
        return None;
    }
    let (head, errors) = count_errors(&logs.join("kiln.log"))?;
    let pid = head.split("(pid ").nth(1).and_then(|r| r.split(')').next()).and_then(|p| p.trim().parse::<u32>().ok());
    (pid == Some(boot.pid)).then_some(errors)
}

/// `KILN_LAUNCH_DRY_RUN=1`: launch_play does everything except opening the steam:// URI.
pub fn dry_run() -> bool {
    std::env::var("KILN_LAUNCH_DRY_RUN").is_ok_and(|v| v == "1")
}

#[tauri::command]
pub async fn launch_play(app: AppHandle, game_id: String, request: LaunchRequest) -> Result<LaunchResult, Error> {
    let id = game_id.clone();
    let r = blocking(move || {
        let g = crate::state::game(&id)?;
        crate::state::refuse_running(&g)?;
        if dry_run() {
            // tests against game copies: write the play-once files, back up, but never open steam:// (that would
            // start the real game through Steam)
            return kiln_manage::launch::play_with(&dirs()?, &g, &request, None, &|uri| {
                eprintln!("KILN_LAUNCH_DRY_RUN: not opening {uri}");
                Ok(())
            });
        }
        kiln_manage::launch::play(&dirs()?, &g, &request)
    })
    .await?;
    crate::watch::emit_status(&app, &game_id);
    Ok(r)
}

/// `launch_prestart`: what kiln-eml is doing before the game's window exists (a first start that fingerprints the game's data
/// files, a patch run): `kiln/state/eml.json` with phase `running`, while the game runs and the file is fresh. None otherwise.
/// The Play page polls it faster than `launch_state` while it waits for Kiln to boot.
#[tauri::command]
pub async fn launch_prestart(game_id: String) -> Result<Option<kiln_manage::prestart::PreStart>, Error> {
    blocking(move || kiln_manage::prestart::read(&crate::state::game(&game_id)?)).await
}

#[tauri::command]
pub async fn launch_state(game_id: String) -> Result<BootState, Error> {
    blocking(move || {
        let g = crate::state::game(&game_id)?;
        let boot = kiln_manage::state::read_boot(&g.path)?;
        let mods = kiln_manage::state::read_mods(&g.path)?;
        let native = kiln_manage::state::read_native(&g.path)?;
        // under Proton the pid in boot.json is the game's Windows pid: boot_is_current compares write and start times
        let stale = boot.as_ref().is_some_and(|b| !kiln_manage::launch::boot_is_current(&g, b));
        let log = boot.as_ref().and_then(|b| log_errors(&g.path, b));
        Ok(BootState { boot, mods, native, stale, log })
    })
    .await
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, SystemTime};

    fn put(p: &Path, text: &str, at: SystemTime) {
        std::fs::create_dir_all(p.parent().unwrap()).unwrap();
        std::fs::write(p, text).unwrap();
        std::fs::File::options().write(true).open(p).unwrap().set_modified(at).unwrap();
    }

    #[test]
    fn last_boot_counts_the_error_lines_of_its_own_run() {
        let game = std::env::temp_dir().join(format!("kl-logerr-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&game);
        let t0 = SystemTime::now() - Duration::from_secs(600);
        let boot: kiln_manage::state::BootJson = serde_json::from_str(
            r#"{"format":1,"kiln":"0.2.0-dev","pid":384,"written":"2026-11-06T07:00:02Z","side":"client","hooks":true,"profile":{"mode":"active"}}"#,
        )
        .unwrap();
        let logs = game.join("kiln/logs");
        put(&logs.join("kiln.prev.log"), "[I 01:50:00.000] [kiln] Kiln 0.2.0-dev starting (pid 384)\n", t0);
        put(&game.join("kiln/state/boot.json"), "{}", t0 + Duration::from_secs(60));
        let start = "[I 02:00:00.000] [kiln] Kiln 0.2.0-dev+20261106.8d20598 starting (pid 384)\n";
        let clean = format!("{start}[I 02:00:01.000] [kiln] boot complete; handing control to the game\n");
        put(&logs.join("kiln.log"), &clean, t0 + Duration::from_secs(61));
        assert_eq!(log_errors(&game, &boot).unwrap().errors, 0, "a clean boot");
        // errors after the boot, a stack trace under one of them, and a line still being written
        let later = format!(
            "{clean}[E 02:00:20.749] [kiln] kiln service tick 'kiln creature brains' threw (1x): boom\n   at Kiln.World.Find()\n[W 02:00:21.000] [kiln] slow\n[E 02:00:22.073] [kiln] kiln service tick 'kiln creature brains' threw (2x): boom\n[E 02:00:23"
        );
        put(&logs.join("kiln.log"), &later, t0 + Duration::from_secs(90));
        let e = log_errors(&game, &boot).unwrap();
        assert_eq!(e.errors, 2, "{e:?}");
        assert!(e.first[0].starts_with("[E 02:00:20.749] [kiln] kiln service tick"));
        // the half line completes: counted once, from where the last read stopped
        put(&logs.join("kiln.log"), &format!("{later}.407] [kiln] third\n"), t0 + Duration::from_secs(95));
        assert_eq!(log_errors(&game, &boot).unwrap().errors, 3);
        // another process's log (the pid differs) is not this boot's
        let other: kiln_manage::state::BootJson = serde_json::from_value(serde_json::json!({
            "format": 1, "kiln": "0.2.0-dev", "pid": 512, "written": "2026-11-06T07:00:02Z", "side": "client", "hooks": true, "profile": { "mode": "active" }
        }))
        .unwrap();
        assert!(log_errors(&game, &other).is_none());
        // a new start died before writing boot.json: kiln.log is the new run's, boot.json the old one's
        put(&logs.join("kiln.prev.log"), &later, t0 + Duration::from_secs(120));
        put(&logs.join("kiln.log"), &format!("{start}[E 02:10:00.000] [kiln] cannot parse the game image\n"), t0 + Duration::from_secs(130));
        assert!(log_errors(&game, &boot).is_none());
        let _ = std::fs::remove_dir_all(&game);
    }
}
