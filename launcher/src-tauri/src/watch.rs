//! Background watchers: the game process and its state files (`kiln://game`) and followed log
//! files (`kiln://log`). `kiln://last-server` comes from the modpack watcher (commands/modpack.rs). One thread, polling every
//! second; cheap checks only (process list, file mtimes; while the game runs only its own pid), the full status is
//! computed when something changed.

use crate::events::{self, GameEvent, GameEventKind, LogEvent};
use kiln_manage::Error;
use kiln_manage::paths::game as gp;
use std::collections::{HashMap, VecDeque};
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
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

/// Set when this launcher is on its way out (closing, or restarting into an update). The watcher stops: a process
/// that hangs in its exit handlers must not go on rewriting profile.json next to the launcher that replaced it.
static LEAVING: AtomicBool = AtomicBool::new(false);

pub fn leaving() {
    LEAVING.store(true, Ordering::SeqCst);
}

/// How often `follow_profile` may answer a change of the game's profile.json itself (not of the profile) with a write
/// of its own: REWRITES times in REWRITE_WINDOW. Two Kiln programs that render a profile differently (an older
/// launcher that is still open, a kilnctl of another version) would otherwise answer each other's write for ever,
/// about twice a second, copying the profile's mods every time.
const REWRITES: usize = 3;
const REWRITE_WINDOW: Duration = Duration::from_secs(60);

#[derive(Default)]
struct Rewrites {
    at: VecDeque<Instant>,
    /// the pause was logged (once per pause)
    said: bool,
}

impl Rewrites {
    /// May the watcher write now? Forgets the writes older than the window.
    fn room(&mut self, now: Instant) -> bool {
        while self.at.front().is_some_and(|t| now.duration_since(*t) >= REWRITE_WINDOW) {
            self.at.pop_front();
        }
        self.at.len() < REWRITES
    }

    fn wrote(&mut self, now: Instant) {
        self.at.push_back(now);
        self.said = false;
    }
}

#[derive(Default)]
struct Followed {
    seen: Option<ProfileSeen>,
    rewrites: Rewrites,
}

/// The active profile as the watcher last saw it: profile.json's mtime, the profile it names and its meta.json's mtime.
#[derive(Clone, PartialEq)]
struct ProfileSeen {
    file: Option<SystemTime>,
    id: Option<String>,
    meta: Option<SystemTime>,
}

fn profile_seen(d: &kiln_manage::paths::DataDirs, g: &kiln_manage::game::GameInstall, before: Option<&ProfileSeen>) -> ProfileSeen {
    let file = mtime(&g.path.join(gp::PROFILE));
    // profile.json is read again only when it changed
    let id = match before {
        Some(b) if b.file == file => b.id.clone(),
        _ => kiln_manage::profile::read_game_profile(&g.path).ok().flatten().map(|f| f.id),
    };
    let meta = id.as_deref().and_then(|id| mtime(&d.profile(id).join("meta.json")));
    ProfileSeen { file, id, meta }
}

/// The active profile changed outside this launcher's profile pages (kilnctl, another window) while the game is not
/// running: write the game's profile.json again, so Steam's own Play button starts it as it is now. Once per change
/// (and once at startup). When something else keeps writing profile.json differently, the watcher stops answering
/// after REWRITES writes and looks again when the minute is over.
fn follow_profile(app: &AppHandle, g: &kiln_manage::game::GameInstall, followed: &mut HashMap<String, Followed>) {
    let Ok(d) = crate::state::dirs() else { return };
    let f = followed.entry(g.id.clone()).or_default();
    if let Some(a) = follow_profile_in(&d, g, f) {
        crate::commands::profile::queue_notices(app, &a);
        emit_game(app, &g.id, GameEventKind::Status, None);
    }
}

/// The file/rewrite decision without window events, also exercised against isolated game folders in tests.
fn follow_profile_in(d: &kiln_manage::paths::DataDirs, g: &kiln_manage::game::GameInstall, f: &mut Followed) -> Option<kiln_manage::profile::Activation> {
    let now = profile_seen(d, g, f.seen.as_ref());
    if f.seen.as_ref() == Some(&now) {
        return None;
    }
    // profile.json is not as this watcher left it: somebody else wrote it
    let foreign = f.seen.as_ref().is_some_and(|s| s.file != now.file);
    if foreign && !f.rewrites.room(Instant::now()) {
        if !f.rewrites.said {
            f.rewrites.said = true;
            crate::commands::app::launcher_log("profile", &format!(
                "{}: this launcher wrote it {REWRITES} times within a minute and found it changed again each time. Another Kiln program writes it too (an older launcher that is still open?). Left as it is for now; looked at again within a minute.",
                g.path.join(gp::PROFILE).display()));
        }
        return None;
    }
    let applied = if now.id.is_some() { crate::commands::profile::reapply_checked(d, g) } else { None };
    if foreign && applied.as_ref().is_some_and(|(_, written)| *written) {
        f.rewrites.wrote(Instant::now());
    }
    // A completed command's profile.json is already current and its caller has its report. A notes-only save also
    // reconciles and queues its report without changing profile.json. Only startup reports a current file here;
    // subsequent checks must actually rewrite it to announce another activation.
    let applied = applied.filter(|(_, written)| *written || f.seen.is_none()).map(|(a, _)| a);
    // what it looks like after the write, so the write itself is not a change
    f.seen = Some(profile_seen(d, g, None));
    applied
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

/// The game's pid for the watcher. While it runs (`last`), only that pid is checked; a whole /proc scan only to see it
/// start or end.
pub(crate) fn game_pid(g: &kiln_manage::game::GameInstall, last: Option<u32>) -> Option<u32> {
    last.and_then(|p| kiln_manage::proc::still_running(p, &g.path, &g.exe))
        .or_else(|| kiln_manage::proc::find_game(g).ok().flatten())
        .map(|p| p.pid)
}

/// Starts the watcher thread (once, from setup).
pub fn start(app: AppHandle) {
    std::thread::Builder::new()
        .name("kiln-watch".into())
        .spawn(move || {
            let mut seen: HashMap<String, Seen> = HashMap::new();
            let mut profiles: HashMap<String, Followed> = HashMap::new();
            let mut games: Vec<kiln_manage::game::GameInstall> = Vec::new();
            let mut games_at = Instant::now() - Duration::from_secs(60);
            loop {
                if LEAVING.load(Ordering::SeqCst) {
                    return;
                }
                // a round reads and writes the data folder (copy back, re-applied profiles, activation records): counted
                // as a writer, and skipped while a move holds the folder
                let writing = crate::state::writing();
                if crate::folder::current().held_error().is_some() {
                    drop(writing);
                    std::thread::sleep(Duration::from_millis(1000));
                    continue;
                }
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
                        pid: game_pid(g, seen.get(&g.id).and_then(|s| s.pid)),
                        boot: mtime(&g.path.join(gp::BOOT_JSON)),
                        mods: mtime(&g.path.join(gp::MODS_JSON)),
                        native: mtime(&g.path.join(gp::NATIVE_JSON)),
                    };
                    if now.pid.is_none() {
                        follow_profile(&app, g, &mut profiles);
                    }
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
                drop(writing);
                std::thread::sleep(Duration::from_millis(1000));
            }
        })
        .expect("watcher thread");
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A real placed SET copy whose code the player changed. Reconciliation reports it without rewriting profile.json.
    #[cfg(unix)]
    fn retained_game() -> (kiln_manage::library::TempDir, kiln_manage::paths::DataDirs, kiln_manage::game::GameInstall, String) {
        use kiln_manage::{game::{GameInstall, GameKind, PathMode, Platform}, library, paths::DataDirs, profile, task::NoProgress};
        let tmp = library::TempDir::new(&std::env::temp_dir(), "watch-retained").unwrap();
        let root = tmp.path();
        let d = DataDirs::at(root.join("data"));
        let src = root.join("test-tweaks");
        std::fs::create_dir_all(&src).unwrap();
        std::fs::write(src.join("init.lua"), b"print('fixture')").unwrap();
        let imported = library::import(&d, &[src], &mut NoProgress).unwrap();
        let r = &imported.imported[0];
        let mut p = profile::create(&d, "Watcher test", None).unwrap();
        p.mods.set.push(profile::ProfileMod { id: r.id.clone(), version: r.version.clone(), hash: r.hash.clone(), enabled: true });
        let p = profile::save(&d, &p).unwrap();
        let path = root.join("game");
        std::fs::create_dir_all(path.join("kiln")).unwrap();
        std::fs::write(path.join("enshrouded.exe"), b"fixture").unwrap();
        let prefix = root.join("pfx");
        std::fs::create_dir_all(prefix.join("dosdevices")).unwrap();
        std::os::unix::fs::symlink("/", prefix.join("dosdevices/z:")).unwrap();
        let g = GameInstall { id: "watch-test".into(), kind: GameKind::Client, path, exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(prefix),
            build_id: None, exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into() };
        profile::activate(&d, &p.id, &g).unwrap();
        std::fs::write(g.path.join("shrouded_engine_tweaks/mods").join(&r.id).join("init.lua"), b"print('player edit')").unwrap();
        (tmp, d, g, p.id)
    }

    #[test]
    #[cfg(unix)]
    fn reconciliation_reports_do_not_spend_the_watchers_rewrite_budget() {
        use kiln_manage::profile;
        let (_tmp, d, g, id) = retained_game();
        let mut f = Followed::default();
        let start = follow_profile_in(&d, &g, &mut f).unwrap();
        assert_eq!(start.retained.len(), 1, "startup still reports changed copies");
        for _ in 0..4 {
            // A completed launcher command writes and returns its own notice to its caller.
            let a = profile::activate(&d, &id, &g).unwrap();
            assert_eq!(a.retained.len(), 1);
            // Model the previous poll's older mtime without depending on filesystem clock resolution.
            f.seen.as_mut().unwrap().file = Some(SystemTime::UNIX_EPOCH);
            let _ = follow_profile_in(&d, &g, &mut f);
            assert!(f.rewrites.at.is_empty(), "a retained-copy report did not rewrite profile.json");
            assert_eq!(profile::read_game_profile(&g.path).unwrap().unwrap().generation, a.generation);
        }
        // A genuinely out-of-date game profile does require a rewrite and uses one slot.
        let file = g.path.join(gp::PROFILE);
        let mut json: serde_json::Value = serde_json::from_slice(&std::fs::read(&file).unwrap()).unwrap();
        json["dropins"] = serde_json::json!(true);
        std::fs::write(&file, serde_json::to_vec(&json).unwrap()).unwrap();
        f.seen.as_mut().unwrap().file = Some(SystemTime::UNIX_EPOCH);
        assert!(follow_profile_in(&d, &g, &mut f).is_some());
        assert_eq!(f.rewrites.at.len(), 1);
    }

    #[test]
    #[cfg(unix)]
    fn the_watcher_does_not_repeat_a_completed_commands_retained_copy_notice() {
        let (_tmp, d, g, id) = retained_game();
        let mut f = Followed::default();
        assert!(follow_profile_in(&d, &g, &mut f).is_some(), "startup reports once");
        assert!(follow_profile_in(&d, &g, &mut f).is_none(), "an unchanged poll says nothing");
        let direct = kiln_manage::profile::activate(&d, &id, &g).unwrap();
        assert_eq!(direct.retained.len(), 1, "the command already returns the notice");
        f.seen.as_mut().unwrap().file = Some(SystemTime::UNIX_EPOCH);
        assert!(follow_profile_in(&d, &g, &mut f).is_none(), "checking a current profile must not duplicate the command result");
    }

    #[test]
    #[cfg(unix)]
    fn metadata_only_saves_do_not_repeat_the_commands_reconciliation_report() {
        use kiln_manage::{manifest::ModKind, profile};
        let (_tmp, d, g, id) = retained_game();
        let mut f = Followed::default();
        assert!(follow_profile_in(&d, &g, &mut f).is_some());
        let mut p = profile::get(&d, &id).unwrap();
        p.notes = "only the notes changed".into();
        let mut p = profile::save(&d, &p).unwrap();
        let (report, written) = crate::commands::profile::reapply_checked(&d, &g).unwrap();
        assert!(!written);
        assert_eq!(report.retained.len(), 1, "profile_save queues this report already");
        f.seen.as_mut().unwrap().meta = Some(SystemTime::UNIX_EPOCH);
        assert!(follow_profile_in(&d, &g, &mut f).is_none(), "a notes-only save must not double its notice");
        // A metadata edit outside the launcher that really changes the rendered file still applies and reports.
        p.dropins.push(ModKind::Emf);
        profile::save(&d, &p).unwrap();
        f.seen.as_mut().unwrap().meta = Some(SystemTime::UNIX_EPOCH);
        assert!(follow_profile_in(&d, &g, &mut f).is_some());
    }

    #[test]
    fn the_watcher_writes_a_profile_three_times_a_minute_at_most() {
        let t0 = Instant::now();
        let mut r = Rewrites::default();
        for i in 0..REWRITES as u64 {
            assert!(r.room(t0 + Duration::from_secs(i)));
            r.wrote(t0 + Duration::from_secs(i));
        }
        // a fourth change inside the minute waits
        assert!(!r.room(t0 + Duration::from_secs(5)));
        r.said = true;
        assert!(!r.room(t0 + Duration::from_secs(59)));
        // the oldest write leaves the window: one more
        assert!(r.room(t0 + Duration::from_secs(60)));
        r.wrote(t0 + Duration::from_secs(60));
        assert!(!r.said, "the next pause is logged again");
        assert!(!r.room(t0 + Duration::from_secs(60)));
        assert!(r.room(t0 + Duration::from_secs(61)));
        // quiet for a while: all of it again
        assert!(r.room(t0 + Duration::from_secs(600)));
        assert!(r.at.is_empty());
    }
}
