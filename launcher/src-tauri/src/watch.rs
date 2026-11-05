//! Background watchers: the game process and its state files (`kiln://game`) and followed log
//! files (`kiln://log`). `kiln://last-server` comes from the modpack watcher (commands/modpack.rs). One thread, polling every
//! second; cheap checks only (process list, file mtimes), the full status is computed when something changed.

use crate::events::{self, GameEvent, GameEventKind, LogEvent};
use kiln_manage::Error;
use kiln_manage::paths::game as gp;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{LazyLock, Mutex};
use std::time::{Duration, Instant, SystemTime};
use tauri::{AppHandle, Emitter};

#[derive(Default, Clone, PartialEq)]
struct Seen {
    pid: Option<u32>,
    boot: Option<SystemTime>,
    mods: Option<SystemTime>,
    native: Option<SystemTime>,
}

fn mtime(p: &Path) -> Option<SystemTime> {
    std::fs::metadata(p).and_then(|m| m.modified()).ok()
}

/// Emits `kiln://game {kind: status}` with a fresh status (after install, activation, launch).
pub fn emit_status(app: &AppHandle, game_id: &str) {
    emit_game(app, game_id, GameEventKind::Status, None);
}

fn emit_game(app: &AppHandle, game_id: &str, kind: GameEventKind, pid: Option<u32>) {
    let status = crate::state::game(game_id).and_then(|g| crate::state::game_status(&g)).ok();
    let pid = pid.or_else(|| status.as_ref().and_then(|s| s.pid));
    let _ = app.emit(events::GAME, GameEvent { game_id: game_id.to_string(), kind, pid, status });
}

/// Copy mode: the game wrote mod configs into `<game>/kiln/profiles/<pid>/config/`; bring them back to the launcher's
/// profile after it exits (the profile that ran: boot.json's, and the active profile.json's). No-op otherwise.
fn copy_back(g: &kiln_manage::game::GameInstall) {
    let Ok(d) = crate::state::dirs() else { return };
    let mut ids: Vec<String> = Vec::new();
    if let Ok(Some(b)) = kiln_manage::state::read_boot(&g.path)
        && let Some(id) = b.profile.id
    {
        ids.push(id);
    }
    if let Ok(Some(p)) = kiln_manage::profile::read_game_profile(&g.path) {
        ids.push(p.id);
    }
    ids.sort();
    ids.dedup();
    for id in ids {
        if let Err(e) = kiln_manage::profile::copy_back(&d, &id, g) {
            eprintln!("copy_back {id}: {e}");
        }
    }
}

// ---------------------------------------------------------------- log follows

struct Follow {
    target: crate::commands::logs::LogTarget,
    file: String,
    follower: kiln_manage::logs::Follower,
}

static FOLLOWS: LazyLock<Mutex<HashMap<String, Follow>>> = LazyLock::new(|| Mutex::new(HashMap::new()));
static FOLLOW_SEQ: AtomicU64 = AtomicU64::new(0);

/// Starts following `path` from its end; lines arrive as `kiln://log` with the returned subscription id.
pub fn follow(target: crate::commands::logs::LogTarget, file: String, path: PathBuf) -> Result<String, Error> {
    let follower = kiln_manage::logs::Follower::open(&path)?;
    let id = format!("log-{}", FOLLOW_SEQ.fetch_add(1, Ordering::Relaxed) + 1);
    FOLLOWS.lock().unwrap().insert(id.clone(), Follow { target, file, follower });
    Ok(id)
}

pub fn unfollow(id: &str) -> bool {
    FOLLOWS.lock().unwrap().remove(id).is_some()
}

fn poll_follows(app: &AppHandle) {
    let mut map = FOLLOWS.lock().unwrap();
    for (id, f) in map.iter_mut() {
        if let Ok(lines) = f.follower.poll()
            && !lines.is_empty()
        {
            let _ = app.emit(
                events::LOG,
                LogEvent { subscription: id.clone(), target: f.target.clone(), file: f.file.clone(), lines, next: f.follower.offset() },
            );
        }
    }
}

// ---------------------------------------------------------------- the loop

/// Starts the watcher thread (once, from setup).
pub fn start(app: AppHandle) {
    std::thread::Builder::new()
        .name("kiln-watch".into())
        .spawn(move || {
            let mut seen: HashMap<String, Seen> = HashMap::new();
            let mut games: Vec<kiln_manage::game::GameInstall> = Vec::new();
            let mut games_at = Instant::now() - Duration::from_secs(60);
            loop {
                poll_follows(&app);
                if games_at.elapsed() > Duration::from_secs(10) {
                    games = crate::state::dirs()
                        .and_then(|d| kiln_manage::game::known(&d))
                        .unwrap_or_default()
                        .into_iter()
                        .filter(|g| g.kind == kiln_manage::game::GameKind::Client)
                        .collect();
                    games_at = Instant::now();
                }
                for g in &games {
                    let now = Seen {
                        pid: kiln_manage::proc::find_game(g).ok().flatten().map(|p| p.pid),
                        boot: mtime(&g.path.join(gp::BOOT_JSON)),
                        mods: mtime(&g.path.join(gp::MODS_JSON)),
                        native: mtime(&g.path.join(gp::NATIVE_JSON)),
                    };
                    let Some(before) = seen.insert(g.id.clone(), now.clone()) else { continue };
                    if before == now {
                        continue;
                    }
                    match (before.pid, now.pid) {
                        (None, Some(p)) => emit_game(&app, &g.id, GameEventKind::Started, Some(p)),
                        (Some(_), None) => {
                            copy_back(g);
                            emit_game(&app, &g.id, GameEventKind::Stopped, None)
                        }
                        _ => {}
                    }
                    if before.boot != now.boot && now.boot.is_some() {
                        emit_game(&app, &g.id, GameEventKind::Boot, now.pid);
                    }
                    if before.mods != now.mods && now.mods.is_some() {
                        emit_game(&app, &g.id, GameEventKind::Mods, now.pid);
                    }
                    if before.native != now.native && now.native.is_some() {
                        emit_game(&app, &g.id, GameEventKind::Native, now.pid);
                    }
                }
                std::thread::sleep(Duration::from_millis(1000));
            }
        })
        .expect("watcher thread");
}
