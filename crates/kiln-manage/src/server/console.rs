//! The server console: what a console window would show (the server's stdout and stderr, Wine's and the launcher's
//! lines), captured to a file by every start, so no window opens and the launcher shows the output instead.
//!
//! - **Capture file** `<state>/<id>.run.log` (`$XDG_STATE_HOME/kiln`, Windows `%LOCALAPPDATA%\Kiln\state`): the
//!   file `kilnctl start` already writes for kilnctl instances. Every start appends a marker line
//!   (`---- kiln-launcher start <id> at <time>`, kilnctl's `---- kilnctl start <id> at <time>`); the console shows the
//!   last run. A start rotates a file over 8 MiB to `.old` first (kilnctl's rule).
//! - The server outlives the launcher (its own session; output into a file, not a pipe). A reopened launcher reads
//!   the file again: that is the reattach.
//! - **Levels**: the game prints every line of its log (`logs/enshrouded_server.log`, `[I|W|E hh:mm:ss,mmm] text`)
//!   to stdout without the level (measured: stdout = the game log + Wine/Steam lines, in lockstep). A captured line
//!   takes the level of the same text in the game log; Wine's `err:`/`fixme:` lines and the launcher's own lines are
//!   known by their prefix.
//! - A server the launcher did not start (Steam's Play button, systemd, by hand) has no current capture; the console
//!   then shows its game log (`source: gamelog`). The capture counts as current while it is no more than a minute
//!   older than the game log (both grow together while the game runs).

use super::instance::ServerInstance;
use crate::logs::Follower;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime};

/// A `WAYLAND_DISPLAY` socket that does not exist: with `DISPLAY` unset too, Wine reaches no display server and no
/// window of the server's (console, message box, crash dialog) can appear. Unset is not enough: libwayland then tries
/// `$XDG_RUNTIME_DIR/wayland-0`, the desktop's compositor.
pub const NO_DISPLAY: &str = "kiln-no-display";
/// A start rotates a larger capture file to `<file>.old` (kilnctl's limit).
pub const ROTATE_BYTES: u64 = 8 << 20;
/// How much of the capture file a snapshot reads at most (one run after a rotation fits).
pub const TAIL_BYTES: u64 = 12 << 20;
/// Lines a snapshot returns by default / at most.
pub const DEFAULT_LINES: usize = 5000;
pub const MAX_LINES: usize = 50_000;
/// Marker prefixes of a start in the capture file.
pub const MARKERS: [&str; 2] = ["---- kiln-launcher start ", "---- kilnctl start "];
const CURRENT_SLACK: Duration = Duration::from_secs(60);

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConsoleLevel {
    Info,
    Warn,
    Error,
    Debug,
    /// The launcher's and kilnctl's own lines (start markers, what was started).
    Meta,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ConsoleLine {
    pub text: String,
    pub level: ConsoleLevel,
}

/// Where the console's lines come from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum ConsoleSource {
    /// The capture file of a launcher (or `kilnctl start`) run.
    Capture,
    /// The game log: the server was started some other way (or never by the launcher).
    Gamelog,
    /// Nothing to show yet (never started, or a remote server).
    None,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ConsoleSnapshot {
    pub source: ConsoleSource,
    /// The file shown.
    pub path: Option<PathBuf>,
    /// The last lines of the run (oldest first).
    pub lines: Vec<ConsoleLine>,
    /// Older lines of the run were left out (`maxLines`, or the run is longer than the part read).
    pub truncated: bool,
}

/// The capture file of a local server (None for a remote one).
pub fn capture_path(instance: &ServerInstance) -> Option<PathBuf> {
    instance.dir.as_ref()?;
    let name = instance.kilnctl.as_deref().unwrap_or(&instance.id);
    Some(crate::util::state_dir().join(format!("{name}.run.log")))
}

/// Opens the capture file for a start: rotates it when large, appends the start marker and `notes`
/// (`kiln-launcher: ...` lines). The server's stdout and stderr go to the returned file.
pub fn open_capture(instance: &ServerInstance, notes: &[String]) -> crate::Result<(PathBuf, std::fs::File)> {
    use std::io::Write;
    let path = capture_path(instance).ok_or_else(|| crate::Error::invalid(format!("{} is a remote server", instance.id)))?;
    if let Some(d) = path.parent() {
        std::fs::create_dir_all(d).map_err(crate::util::io_at(d, "creating"))?;
    }
    if std::fs::metadata(&path).is_ok_and(|m| m.len() > ROTATE_BYTES) {
        let old = crate::util::with_suffix(&path, ".old");
        let _ = std::fs::remove_file(&old);
        std::fs::rename(&path, &old).map_err(crate::util::io_at(&path, "rotating"))?;
    }
    let mut f = std::fs::OpenOptions::new().create(true).append(true).open(&path).map_err(crate::util::io_at(&path, "opening"))?;
    let mut head = format!("{}{} at {}\n", MARKERS[0], instance.id, crate::util::now_rfc3339());
    for n in notes {
        head += &format!("kiln-launcher: {n}\n");
    }
    f.write_all(head.as_bytes()).map_err(crate::util::io_at(&path, "writing"))?;
    Ok((path, f))
}

fn mtime(p: &Path) -> Option<SystemTime> {
    std::fs::metadata(p).and_then(|m| m.modified()).ok()
}

/// The capture belongs to the latest run when it is no more than a minute older than the game log.
pub fn capture_is_current(capture: &Path, game_log: Option<&Path>) -> bool {
    let Some(c) = mtime(capture) else { return false };
    match game_log.and_then(mtime) {
        None => true,
        Some(g) => c + CURRENT_SLACK >= g,
    }
}

/// The file the console shows for `instance` and its kind.
pub fn pick(instance: &ServerInstance) -> (ConsoleSource, Option<PathBuf>) {
    let glog = super::run::log_path(instance).ok();
    if let Some(cap) = capture_path(instance)
        && capture_is_current(&cap, glog.as_deref())
    {
        return (ConsoleSource::Capture, Some(cap));
    }
    match glog {
        Some(g) if g.exists() => (ConsoleSource::Gamelog, Some(g)),
        _ => (ConsoleSource::None, None),
    }
}

/// `[W 00:00:01,234] text` -> (W, text).
pub fn split_game_line(line: &str) -> Option<(char, &str)> {
    let rest = line.strip_prefix('[')?;
    let mut ch = rest.chars();
    let lvl = ch.next()?;
    if !lvl.is_ascii_uppercase() || ch.next()? != ' ' {
        return None;
    }
    let close = rest.find(']')?;
    let stamp = &rest[2..close];
    if stamp.is_empty() || !stamp.bytes().all(|b| b.is_ascii_digit() || b == b':' || b == b',' || b == b'.') {
        return None;
    }
    let text = &rest[close + 1..];
    Some((lvl, text.strip_prefix(' ').unwrap_or(text)))
}

fn game_level(c: char) -> ConsoleLevel {
    match c {
        'W' => ConsoleLevel::Warn,
        'E' | 'F' => ConsoleLevel::Error,
        'D' | 'T' | 'V' => ConsoleLevel::Debug,
        _ => ConsoleLevel::Info,
    }
}

/// Wine's debug channels: `0124:err:module:...`, `err:ole:...`, `wine: Unhandled page fault ...`.
fn wine_level(line: &str) -> Option<ConsoleLevel> {
    if line.starts_with("wine: ") {
        return Some(ConsoleLevel::Error);
    }
    let rest = match line.split_once(':') {
        Some((tid, rest)) if !tid.is_empty() && tid.len() <= 8 && tid.bytes().all(|b| b.is_ascii_hexdigit()) => rest,
        _ => line,
    };
    let (class, tail) = rest.split_once(':')?;
    if tail.is_empty() {
        return None;
    }
    Some(match class {
        "err" => ConsoleLevel::Error,
        "warn" => ConsoleLevel::Warn,
        "fixme" | "trace" => ConsoleLevel::Debug,
        _ => return None,
    })
}

pub fn is_meta(line: &str) -> bool {
    line.starts_with("---- ") || line.starts_with("kiln-launcher: ") || line.starts_with("kilnctl: ")
}

pub fn is_start_marker(line: &str) -> bool {
    MARKERS.iter().any(|m| line.starts_with(m))
}

/// Levels of the game log's lines that are not info, by text (what stdout prints for them).
#[derive(Debug, Default)]
pub struct GameLevels {
    map: HashMap<String, ConsoleLevel>,
}

impl GameLevels {
    pub fn learn(&mut self, game_log_line: &str) {
        if let Some((c, text)) = split_game_line(game_log_line) {
            let l = game_level(c);
            if l != ConsoleLevel::Info && !text.trim().is_empty() {
                if self.map.len() > 20_000 {
                    self.map.clear();
                }
                self.map.insert(text.to_string(), l);
            }
        }
    }

    pub fn clear(&mut self) {
        self.map.clear();
    }

    /// The level of a captured (stdout/stderr) line.
    pub fn classify(&self, line: &str) -> ConsoleLevel {
        if is_meta(line) {
            return ConsoleLevel::Meta;
        }
        if let Some(l) = self.map.get(line) {
            return *l;
        }
        wine_level(line).unwrap_or(ConsoleLevel::Info)
    }
}

/// A game log line as a console line (level from its prefix, which is dropped).
pub fn game_line(line: &str) -> ConsoleLine {
    match split_game_line(line) {
        Some((c, text)) => ConsoleLine { text: text.to_string(), level: game_level(c) },
        None => ConsoleLine { text: line.to_string(), level: ConsoleLevel::Info },
    }
}

fn lines_of(bytes: &[u8]) -> Vec<String> {
    let text = String::from_utf8_lossy(bytes);
    let mut v: Vec<String> = text.split('\n').map(|l| l.strip_suffix('\r').unwrap_or(l).to_string()).collect();
    if v.last().is_some_and(|l| l.is_empty()) {
        v.pop();
    }
    v
}

/// The last `max` bytes of a file as lines (a cut first line is dropped), whether the start was cut, and the file
/// length read up to.
pub fn tail_lines(path: &Path, max: u64) -> std::io::Result<(Vec<String>, bool, u64)> {
    let mut f = std::fs::File::open(path)?;
    let len = f.metadata()?.len();
    let from = len.saturating_sub(max);
    f.seek(SeekFrom::Start(from))?;
    let mut buf = Vec::with_capacity((len - from) as usize);
    f.take(len - from).read_to_end(&mut buf)?;
    let mut start = 0;
    if from > 0 {
        start = buf.iter().position(|&b| b == b'\n').map_or(buf.len(), |i| i + 1);
    }
    // complete lines only: a line still being written is read by the follower once it ends
    let end = buf.iter().rposition(|&b| b == b'\n').map_or(start, |i| (i + 1).max(start));
    Ok((lines_of(&buf[start..end]), from > 0, from + end as u64))
}

/// The capture's last run: the lines from the last start marker on (all lines read when there is none).
pub fn last_run(lines: Vec<String>) -> (Vec<String>, bool) {
    match lines.iter().rposition(|l| is_start_marker(l)) {
        Some(i) => (lines[i..].to_vec(), true),
        None => (lines, false),
    }
}

/// Learns the game log's levels; returns the length read up to.
fn learn_file(levels: &mut GameLevels, game_log: Option<&Path>) -> Option<u64> {
    let (lines, _, end) = tail_lines(game_log?, TAIL_BYTES).ok()?;
    for l in &lines {
        levels.learn(l);
    }
    Some(end)
}

/// Reads what the console shows now (the last `max_lines` lines of the run).
pub fn snapshot(instance: &ServerInstance, max_lines: usize, levels: &mut GameLevels) -> ConsoleSnapshot {
    snapshot_at(instance, max_lines, levels).0
}

/// The snapshot and the offsets it read the capture and the game log up to (a follower continues there).
fn snapshot_at(instance: &ServerInstance, max_lines: usize, levels: &mut GameLevels) -> (ConsoleSnapshot, Option<u64>, Option<u64>) {
    let (source, path) = pick(instance);
    let max_lines = max_lines.clamp(1, MAX_LINES);
    let glog = super::run::log_path(instance).ok();
    let (mut lines, mut truncated, cap_end, game_end) = match (&source, &path) {
        (ConsoleSource::Capture, Some(p)) => {
            levels.clear();
            let game_end = learn_file(levels, glog.as_deref());
            let (all, cut, end) = tail_lines(p, TAIL_BYTES).unwrap_or_default();
            let (run, found) = last_run(all);
            let lines: Vec<ConsoleLine> = run.iter().map(|l| ConsoleLine { text: l.clone(), level: levels.classify(l) }).collect();
            (lines, cut && !found, Some(end), game_end)
        }
        (ConsoleSource::Gamelog, Some(p)) => {
            let (all, cut, end) = tail_lines(p, TAIL_BYTES).unwrap_or_default();
            (all.iter().map(|l| game_line(l)).collect(), cut, None, Some(end))
        }
        _ => (Vec::new(), false, None, glog.as_deref().and_then(|g| std::fs::metadata(g).ok()).map(|m| m.len())),
    };
    if lines.len() > max_lines {
        lines.drain(..lines.len() - max_lines);
        truncated = true;
    }
    (ConsoleSnapshot { source, path, lines, truncated }, cap_end, game_end)
}

/// New console lines, or a whole new snapshot when the source changed (a start, a stop, a start elsewhere).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConsoleUpdate {
    Lines(Vec<ConsoleLine>),
    Reset(ConsoleSnapshot),
}

/// Follows a server's console after a snapshot: poll it every half second or so.
pub struct ConsoleFollower {
    instance: ServerInstance,
    max_lines: usize,
    source: ConsoleSource,
    path: Option<PathBuf>,
    capture: Option<Follower>,
    game: Option<Follower>,
    levels: GameLevels,
    /// Captured lines from the previous poll: classified once the game log had a poll to catch up.
    held: Vec<String>,
    picked: Instant,
}

impl ConsoleFollower {
    /// A snapshot and a follower that continues right after it.
    pub fn open(instance: &ServerInstance, max_lines: usize) -> (ConsoleSnapshot, ConsoleFollower) {
        let mut levels = GameLevels::default();
        let (snap, cap_end, game_end) = snapshot_at(instance, max_lines, &mut levels);
        let mut f = ConsoleFollower {
            instance: instance.clone(),
            max_lines,
            source: snap.source,
            path: snap.path.clone(),
            capture: None,
            game: None,
            levels,
            held: Vec::new(),
            picked: Instant::now(),
        };
        f.attach(cap_end, game_end);
        (snap, f)
    }

    /// Follows the files from where the snapshot read them up to (from their end when unknown).
    fn attach(&mut self, cap_end: Option<u64>, game_end: Option<u64>) {
        let at = |p: &Path, end: Option<u64>| match end {
            Some(e) => Follower::open_at(p, e).ok(),
            None => Follower::open(p).ok(),
        };
        let glog = super::run::log_path(&self.instance).ok();
        self.game = glog.as_deref().and_then(|g| at(g, game_end));
        self.capture = match (self.source, &self.path) {
            (ConsoleSource::Capture, Some(p)) => at(p, cap_end),
            _ => None,
        };
        self.held.clear();
    }

    pub fn source(&self) -> ConsoleSource {
        self.source
    }

    pub fn poll(&mut self) -> ConsoleUpdate {
        if self.picked.elapsed() >= Duration::from_secs(2) {
            self.picked = Instant::now();
            let (source, path) = pick(&self.instance);
            if source != self.source || path != self.path {
                let (snap, cap_end, game_end) = snapshot_at(&self.instance, self.max_lines, &mut self.levels);
                self.source = snap.source;
                self.path = snap.path.clone();
                self.attach(cap_end, game_end);
                return ConsoleUpdate::Reset(snap);
            }
        }
        let mut out = Vec::new();
        let game_new = self.game.as_mut().and_then(|f| f.poll().ok()).unwrap_or_default();
        match self.source {
            ConsoleSource::Capture => {
                for l in &game_new {
                    self.levels.learn(l);
                }
                for l in std::mem::take(&mut self.held) {
                    if is_start_marker(&l) {
                        self.levels.clear();
                    }
                    out.push(ConsoleLine { level: self.levels.classify(&l), text: l });
                }
                if let Some(f) = self.capture.as_mut() {
                    self.held = f.poll().unwrap_or_default();
                }
            }
            ConsoleSource::Gamelog => out.extend(game_new.iter().map(|l| game_line(l))),
            ConsoleSource::None => {}
        }
        ConsoleUpdate::Lines(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn game_lines() {
        assert_eq!(split_game_line("[W 00:00:00,487] [steam] Could not find MatchmakingServers interface"), Some(('W', "[steam] Could not find MatchmakingServers interface")));
        assert_eq!(split_game_line("[I 00:00:00,063] "), Some(('I', "")));
        assert_eq!(split_game_line("[I 14:39:30.828] [kiln] Kiln starting"), Some(('I', "[kiln] Kiln starting")));
        assert_eq!(split_game_line("[os] Started thread 0x0000000010010008"), None);
        assert_eq!(split_game_line("[E] x"), None);
        assert_eq!(game_line("[E 00:00:01,526] [ecss] Could not find component").level, ConsoleLevel::Error);
        assert_eq!(game_line("plain").text, "plain");
    }

    #[test]
    fn levels_from_the_game_log() {
        let mut g = GameLevels::default();
        for l in ["[I 00:00:00,063] Config Parsed", "[W 00:00:00,487] [steam] Could not find MatchmakingServers interface", "[E 00:00:01,526] [ecss] Could not find component 6b1adfa8"] {
            g.learn(l);
        }
        assert_eq!(g.classify("[steam] Could not find MatchmakingServers interface"), ConsoleLevel::Warn);
        assert_eq!(g.classify("[ecss] Could not find component 6b1adfa8"), ConsoleLevel::Error);
        assert_eq!(g.classify("Config Parsed"), ConsoleLevel::Info);
        assert_eq!(g.classify("0124:err:module:import_dll Library X not found"), ConsoleLevel::Error);
        assert_eq!(g.classify("0124:fixme:ntdll:NtQuerySystemInformation info_class"), ConsoleLevel::Debug);
        assert_eq!(g.classify("err:ole:CoGetClassObject class not registered"), ConsoleLevel::Error);
        assert_eq!(g.classify("wine: Unhandled page fault on read access"), ConsoleLevel::Error);
        assert_eq!(g.classify("ntsync: up and running."), ConsoleLevel::Info);
        assert_eq!(g.classify("GetTimerResolution 0: 1,000 ( 1,000 - 15,625 ) us"), ConsoleLevel::Info);
        assert_eq!(g.classify("---- kilnctl start lab at 2026-11-04 19:39:29Z"), ConsoleLevel::Meta);
        assert_eq!(g.classify("kiln-launcher: wine ./enshrouded_server.exe"), ConsoleLevel::Meta);
        assert_eq!(g.classify("kilnctl: server pid 1890903 (0.9 s after launch)"), ConsoleLevel::Meta);
    }

    #[test]
    fn last_run_and_tail() {
        let dir = std::env::temp_dir().join(format!("kiln-console-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("x.run.log");
        std::fs::write(&p, "old 1\r\n---- kilnctl start x at 2026-11-04 19:39:29Z\nrun1\n---- kiln-launcher start x at 2026-11-04T20:00:00Z\nkiln-launcher: wine\nrun2 a\nrun2 b\n").unwrap();
        let (all, cut, end) = tail_lines(&p, TAIL_BYTES).unwrap();
        assert!(!cut);
        assert_eq!(end, std::fs::metadata(&p).unwrap().len());
        assert_eq!(all[0], "old 1");
        let (run, found) = last_run(all);
        assert!(found);
        assert_eq!(run, vec!["---- kiln-launcher start x at 2026-11-04T20:00:00Z", "kiln-launcher: wine", "run2 a", "run2 b"]);
        // a cut first line is dropped
        let (tail, cut, _) = tail_lines(&p, 14).unwrap();
        assert!(cut);
        assert_eq!(tail, vec!["run2 b"]);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn current_capture() {
        let dir = std::env::temp_dir().join(format!("kiln-console-cur-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let cap = dir.join("c.run.log");
        let glog = dir.join("enshrouded_server.log");
        assert!(!capture_is_current(&cap, Some(&glog)), "no capture");
        std::fs::write(&cap, "x\n").unwrap();
        assert!(capture_is_current(&cap, Some(&glog)), "no game log yet");
        std::fs::write(&glog, "y\n").unwrap();
        assert!(capture_is_current(&cap, Some(&glog)), "written together");
        let old = SystemTime::now() - Duration::from_secs(600);
        std::fs::File::options().write(true).open(&cap).unwrap().set_modified(old).unwrap();
        assert!(!capture_is_current(&cap, Some(&glog)), "the game log moved on: another start");
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
