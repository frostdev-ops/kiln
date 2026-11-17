//! Log files of a game or server (`kiln/logs/`: kiln.log, proxy.log, mods.txt, sigdb-report.json, tick-stats.jsonl,
//! tests/, `kiln/crash-reports/`, the game's own log), chunked reads and follow (the launcher emits `kiln://log`).
//! Kiln and the game write CRLF on Windows/Wine: every text returned here has its `\r` removed.
//!
//! kiln.log lines are `[<L> hh:mm:ss.mmm] [<source>] <message>` (L = I/W/E/D); a boot starts with
//! `[kiln] Kiln <version> starting (pid <n>)` and ends with `[kiln] boot complete; handing control to the game`.

use crate::{Error, Result};
use serde::{Deserialize, Serialize};
use std::io::{Read, Seek, SeekFrom};
use std::path::{Path, PathBuf};

pub const DEFAULT_MAX_BYTES: u64 = 256 * 1024;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum LogKind {
    Kiln,
    Proxy,
    Mod,
    Game,
    Sigdb,
    Crash,
    Other,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogFile {
    /// Relative to the game folder ('/' separated); what logs_read takes.
    pub name: String,
    pub path: String,
    pub size: u64,
    pub modified: String,
    pub kind: LogKind,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogChunk {
    pub file: String,
    /// Byte offset of `text`.
    pub offset: u64,
    /// Where the next read continues.
    pub next: u64,
    pub size: u64,
    /// UTF-8 (lossy), whole lines.
    pub text: String,
    /// More bytes were available than returned.
    pub truncated: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CrashReport {
    pub name: String,
    pub created: String,
    pub size: u64,
}

fn kind_of(name: &str) -> LogKind {
    let file = name.rsplit('/').next().unwrap_or(name);
    if name.starts_with("kiln/crash-reports/") {
        LogKind::Crash
    } else if name.starts_with("kiln/logs/tests/") || file == "mods.txt" {
        LogKind::Mod
    } else if file == "proxy.log" {
        LogKind::Proxy
    } else if file.starts_with("sigdb") {
        LogKind::Sigdb
    } else if name.starts_with("kiln/logs/") && file.starts_with("kiln") {
        LogKind::Kiln
    } else if !name.starts_with("kiln/") && file.starts_with("enshrouded") {
        LogKind::Game
    } else {
        LogKind::Other
    }
}

/// The names `list`/`read` accept: inside `kiln/logs/`, `kiln/crash-reports/`, `logs/` (the server's game log) or the
/// client's `enshrouded.log`; no `..`, no absolute paths.
fn allowed(name: &str) -> bool {
    if name.is_empty() || name.starts_with('/') || name.contains('\\') || name.contains(':') {
        return false;
    }
    if name.split('/').any(|c| c.is_empty() || c == "." || c == ".." || crate::bundle::windows_would_rename(c)) {
        return false;
    }
    name.starts_with("kiln/logs/")
        || name.starts_with("kiln/crash-reports/")
        || name.starts_with("logs/")
        || name == "enshrouded.log"
        || name == "enshrouded_server.log"
}

fn file_entry(game_dir: &Path, name: String) -> Option<LogFile> {
    let p = name.split('/').fold(game_dir.to_path_buf(), |p, c| p.join(c));
    let md = std::fs::metadata(&p).ok().filter(|m| m.is_file())?;
    Some(LogFile {
        kind: kind_of(&name),
        path: p.display().to_string(),
        size: md.len(),
        modified: crate::saves::rfc3339(md.modified().unwrap_or(std::time::UNIX_EPOCH)),
        name,
    })
}

fn files_in(game_dir: &Path, rel: &str, out: &mut Vec<LogFile>) {
    let dir = rel.split('/').fold(game_dir.to_path_buf(), |p, c| p.join(c));
    let mut names: Vec<String> = std::fs::read_dir(&dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_type().is_ok_and(|t| t.is_file()))
        .filter_map(|e| e.file_name().to_str().map(str::to_string))
        .filter(|n| !n.starts_with('.') && !n.ends_with(".tmp"))
        .collect();
    names.sort();
    out.extend(names.into_iter().filter_map(|n| file_entry(game_dir, format!("{rel}/{n}"))));
}

pub fn list(game_dir: &Path) -> Result<Vec<LogFile>> {
    let mut out = Vec::new();
    files_in(game_dir, "kiln/logs", &mut out);
    files_in(game_dir, "kiln/logs/tests", &mut out);
    files_in(game_dir, "kiln/crash-reports", &mut out);
    out.extend(file_entry(game_dir, "enshrouded.log".into()));
    out.extend(file_entry(game_dir, "logs/enshrouded_server.log".into()));
    Ok(out)
}

fn strip_cr(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).replace('\r', "")
}

/// `offset` None: the last `max_bytes` (default 256 KiB), starting at a line.
pub fn read(game_dir: &Path, name: &str, offset: Option<u64>, max_bytes: Option<u64>) -> Result<LogChunk> {
    if !allowed(name) {
        return Err(Error::invalid(format!("{name}: not a log file name")));
    }
    let path = name.split('/').fold(game_dir.to_path_buf(), |p, c| p.join(c));
    let mut chunk = read_path(&path, offset, max_bytes)?;
    chunk.file = name.to_string();
    Ok(chunk)
}

/// `read` on any path (servers' logs outside a game folder, tests).
pub fn read_path(path: &Path, offset: Option<u64>, max_bytes: Option<u64>) -> Result<LogChunk> {
    let max = max_bytes.unwrap_or(DEFAULT_MAX_BYTES).clamp(1, 64 << 20);
    let mut f = std::fs::File::open(path).map_err(|e| Error::from(e).with_detail(path.display().to_string()))?;
    let size = f.metadata()?.len();
    let (start, tail) = match offset {
        None => (size.saturating_sub(max), true),
        Some(o) if o > size => (0, false), // the file was truncated or replaced: start over
        Some(o) => (o, false),
    };
    let want = (size - start).min(max) as usize;
    f.seek(SeekFrom::Start(start))?;
    let mut buf = vec![0u8; want];
    f.read_exact(&mut buf)?;
    let mut from = 0usize;
    if tail && start > 0 {
        // begin at the first full line (unless the chunk has no line break at all)
        if let Some(i) = buf.iter().position(|&b| b == b'\n') {
            from = i + 1;
        }
    }
    let mut to = buf.len();
    let reached_end = start + buf.len() as u64 == size;
    match buf[from..].iter().rposition(|&b| b == b'\n') {
        Some(i) => to = from + i + 1,
        None if !reached_end || buf.len() == max as usize => {} // one huge line: return it to make progress
        None => to = from,                                      // a partial last line: wait for the rest
    }
    if to < from {
        to = from;
    }
    let offset_out = start + from as u64;
    let next = start + to as u64;
    Ok(LogChunk {
        file: path.display().to_string(),
        offset: offset_out,
        next,
        size,
        text: strip_cr(&buf[from..to]),
        truncated: if tail { offset_out > 0 } else { start + (want as u64) < size },
    })
}

pub fn crash_reports(game_dir: &Path) -> Result<Vec<CrashReport>> {
    let dir = game_dir.join("kiln").join("crash-reports");
    let mut out: Vec<(std::time::SystemTime, CrashReport)> = Vec::new();
    for e in std::fs::read_dir(&dir).into_iter().flatten().flatten() {
        let Ok(md) = e.metadata() else { continue };
        if !md.is_file() {
            continue;
        }
        let t = md.modified().unwrap_or(std::time::UNIX_EPOCH);
        out.push((
            t,
            CrashReport {
                name: format!("kiln/crash-reports/{}", e.file_name().to_string_lossy()),
                created: crate::saves::rfc3339(t),
                size: md.len(),
            },
        ));
    }
    out.sort_by(|a, b| b.0.cmp(&a.0).then(b.1.name.cmp(&a.1.name)));
    Ok(out.into_iter().map(|(_, c)| c).collect())
}

/// Identity of a file across polls: the inode on Unix, the file index (GetFileInformationByHandle) on Windows.
fn identity(path: &Path, md: &std::fs::Metadata) -> u64 {
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        let _ = path;
        md.ino()
    }
    #[cfg(windows)]
    {
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle};
        let _ = md;
        let Ok(f) = std::fs::File::open(path) else { return 0 };
        // SAFETY: a valid open handle and a zeroed out-struct of the right type.
        unsafe {
            let mut info: BY_HANDLE_FILE_INFORMATION = std::mem::zeroed();
            if GetFileInformationByHandle(f.as_raw_handle() as _, &mut info) == 0 {
                return 0;
            }
            (u64::from(info.nFileIndexHigh) << 32 | u64::from(info.nFileIndexLow)) ^ u64::from(info.dwVolumeSerialNumber) << 48
        }
    }
    #[cfg(not(any(unix, windows)))]
    {
        let _ = (path, md);
        0
    }
}

/// Follows a growing file (handles truncation and rotation).
pub struct Follower {
    path: PathBuf,
    pos: u64,
    ident: Option<u64>,
    partial: Vec<u8>,
}

impl Follower {
    /// Follows from the current end of the file (or from the start once it appears).
    pub fn open(path: &Path) -> Result<Follower> {
        let md = std::fs::metadata(path).ok();
        Ok(Follower {
            path: path.to_path_buf(),
            pos: md.as_ref().map_or(0, |m| m.len()),
            ident: md.as_ref().map(|m| identity(path, m)),
            partial: Vec::new(),
        })
    }

    /// Follows from `offset` (e.g. a LogChunk's `next`).
    pub fn open_at(path: &Path, offset: u64) -> Result<Follower> {
        let mut f = Follower::open(path)?;
        f.pos = offset.min(f.pos);
        Ok(f)
    }

    /// The byte offset the next poll reads from (complete lines only: a pending partial line is not counted).
    pub fn offset(&self) -> u64 {
        self.pos - self.partial.len() as u64
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    /// New complete lines since the last poll.
    pub fn poll(&mut self) -> Result<Vec<String>> {
        let md = match std::fs::metadata(&self.path) {
            Ok(m) => m,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
            Err(e) => return Err(e.into()),
        };
        let id = identity(&self.path, &md);
        if Some(id) != self.ident || md.len() < self.pos {
            // replaced or truncated: the new file from its start
            self.ident = Some(id);
            self.pos = 0;
            self.partial.clear();
        }
        if md.len() == self.pos {
            return Ok(Vec::new());
        }
        let mut f = std::fs::File::open(&self.path)?;
        f.seek(SeekFrom::Start(self.pos))?;
        let mut buf = Vec::new();
        f.take(64 << 20).read_to_end(&mut buf)?;
        self.pos += buf.len() as u64;
        self.partial.extend_from_slice(&buf);
        let mut lines = Vec::new();
        while let Some(i) = self.partial.iter().position(|&b| b == b'\n') {
            let line: Vec<u8> = self.partial.drain(..=i).collect();
            lines.push(strip_cr(&line[..line.len() - 1]));
        }
        Ok(lines)
    }
}

// ---- kiln.log contents ----

/// One kiln.log line.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogLine {
    /// I, W, E, D
    pub level: char,
    pub time: String,
    /// `kiln`, `host`, `mod:<id>`, `service:<dll>`, `plugin:<dll>`, ...
    pub source: String,
    pub message: String,
}

/// `[I 10:56:23.738] [kiln] text` -> LogLine; None for other lines (game log lines, continuation lines).
pub fn parse_line(line: &str) -> Option<LogLine> {
    let line = line.trim_end_matches('\r');
    let rest = line.strip_prefix('[')?;
    let (head, rest) = rest.split_once("] ")?;
    let (level, time) = head.split_once(' ')?;
    let level = level.chars().next().filter(|_| level.len() == 1)?;
    let rest = rest.strip_prefix('[')?;
    let (source, message) = rest.split_once(']')?;
    Some(LogLine { level, time: time.to_string(), source: source.to_string(), message: message.trim_start().to_string() })
}

/// What the last boot in a kiln.log says.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootState {
    /// A boot line was found.
    pub started: bool,
    pub kiln: Option<String>,
    /// The game-side pid (a Windows pid under Wine).
    pub pid: Option<u32>,
    pub start_time: Option<String>,
    pub side: Option<String>,
    /// `239/239 symbols, 347/347 constants resolved in 404.9 ms`
    pub sigdb: Option<String>,
    /// `c5c9fe50…: verified`
    pub build: Option<String>,
    pub verified: Option<bool>,
    /// SimStep hooked (hooks on).
    pub hooks: bool,
    /// The safe idle reason, when Kiln stays idle.
    pub safe_idle: Option<String>,
    /// `boot complete; handing control to the game` was logged.
    pub boot_complete: bool,
    /// A world started after the boot (`tick: server world 0x... started`, `world 0x... started`).
    pub world_started: bool,
    pub services: Vec<String>,
    pub warnings: u32,
    /// `[E ...]` lines of this boot (at most 50).
    pub errors: Vec<String>,
    /// kiln.ui saw the game make a swapchain (the game draws; the overlay could).
    #[serde(default)]
    pub ui_swapchain: bool,
    /// kiln.ui knows the game's Vulkan device (saw it being created, or took it over): without it the overlay
    /// cannot draw, and the in-game Mods page and every Kiln menu stay empty.
    #[serde(default)]
    pub ui_device: bool,
    /// The module another hook on the Vulkan functions leads into, when kiln.ui named one (`version.dll`).
    #[serde(default)]
    pub ui_patched_by: Option<String>,
}

/// The last boot of a kiln.log (`[kiln] Kiln <v> starting (pid <n>)` to the end).
pub fn boot_state(text: &str) -> BootState {
    let lines: Vec<&str> = text.lines().collect();
    let start = lines
        .iter()
        .rposition(|l| parse_line(l).is_some_and(|p| p.source == "kiln" && p.message.starts_with("Kiln ") && p.message.contains(" starting")))
        .unwrap_or(0);
    let mut s = BootState::default();
    for l in &lines[start..] {
        let Some(p) = parse_line(l) else { continue };
        match p.level {
            'W' => s.warnings += 1,
            'E' if s.errors.len() < 50 => s.errors.push(format!("[{}] {}", p.source, p.message)),
            _ => {}
        }
        if p.source == "service:kiln-ui.dll" {
            let m = p.message.as_str();
            if m.starts_with("ui: swapchain ") {
                s.ui_swapchain = true;
            } else if m.starts_with("ui: game Vulkan device ") && !m.contains(" destroyed") {
                s.ui_device = true;
            } else if let Some((_, who)) = m.split_once("it now leads into ") {
                s.ui_patched_by.get_or_insert_with(|| who.split(':').next().unwrap_or(who).trim().to_string());
            }
        }
        if p.source != "kiln" {
            continue;
        }
        let m = p.message.as_str();
        if let Some(rest) = m.strip_prefix("Kiln ")
            && let Some((ver, tail)) = rest.split_once(" starting")
        {
            s.started = true;
            s.kiln = Some(ver.to_string());
            s.start_time = Some(p.time.clone());
            s.pid = tail.trim().strip_prefix("(pid ").and_then(|x| x.strip_suffix(')')).and_then(|x| x.parse().ok());
        } else if let Some(side) = m.strip_prefix("side: ") {
            s.side = side.split_whitespace().next().map(str::to_string);
        } else if let Some(x) = m.strip_prefix("sigdb: ") {
            s.sigdb = Some(x.to_string());
        } else if let Some(x) = m.strip_prefix("build ") {
            s.build = Some(x.to_string());
            s.verified = Some(x.trim_end().ends_with(": verified"));
        } else if m.starts_with("tick: SimStep hooked") {
            s.hooks = true;
        } else if let Some(x) = m.strip_prefix("safe idle: ") {
            s.safe_idle = Some(x.to_string());
        } else if m.starts_with("boot complete") {
            s.boot_complete = true;
        } else if (m.starts_with("tick: ") && m.contains(" world 0x") && m.contains(" started")) || (m.starts_with("world 0x") && m.contains(" started")) {
            s.world_started = true;
        } else if let Some(x) = m.strip_prefix("service ")
            && let Some((name, rest)) = x.split_once(' ')
            && rest.trim_start().starts_with("v")
            && (rest.contains(": available") || rest.contains(" published by "))
        {
            let ver = rest.split_whitespace().next().unwrap_or("");
            s.services.push(format!("{name}@{}", ver.trim_start_matches('v').trim_end_matches(':')));
        }
    }
    s
}

/// p50 / p99 / max of a tick-stats timing.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Percentiles {
    #[serde(default)]
    pub p50: f64,
    #[serde(default)]
    pub p99: f64,
    #[serde(default)]
    pub max: f64,
}

/// One `kiln/logs/tick-stats.jsonl` line (written every 60 s per world).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TickStat {
    #[serde(default)]
    pub time: String,
    /// `server` | `client` | `prediction` on a client; absent on a dedicated server.
    #[serde(default)]
    pub world: Option<String>,
    #[serde(default)]
    pub steps: u64,
    #[serde(default)]
    pub rate: f64,
    #[serde(default, rename = "clock_rate")]
    pub clock_rate: f64,
    #[serde(default, rename = "step_ms")]
    pub step_ms: Percentiles,
    #[serde(default, rename = "pre_ms")]
    pub pre_ms: Percentiles,
    #[serde(default, rename = "post_ms")]
    pub post_ms: Percentiles,
    #[serde(default, rename = "interval_ms")]
    pub interval_ms: Percentiles,
    #[serde(default)]
    pub subs: serde_json::Value,
    /// Since 0.2.0-dev.29: the parts of Post (the game's job wait, the window services' end, each subscriber by owner).
    /// None on older lines, and when the field does not read (the rest of the line still does).
    #[serde(default, rename = "post_parts", deserialize_with = "lenient", skip_serializing_if = "Option::is_none")]
    pub post_parts: Option<PostParts>,
    /// Since 0.2.0-dev.29: the Pre subscribers with the longest call (8 at most).
    #[serde(default, rename = "pre_parts", deserialize_with = "lenient", skip_serializing_if = "Option::is_none")]
    pub pre_parts: Option<PreParts>,
    /// Since 0.2.0-dev.29: the window's worst step (Pre + step + Post) broken down.
    #[serde(default, deserialize_with = "lenient", skip_serializing_if = "Option::is_none")]
    pub worst: Option<WorstStep>,
    /// From the tasks plan (T4, issue #49; not written yet on `main`): the task pool's window. Absent when the pool
    /// does not exist, and None here when the section does not read; the rest of the line still does.
    #[serde(default, deserialize_with = "lenient", skip_serializing_if = "Option::is_none")]
    pub tasks: Option<TasksSection>,
}

/// `tasks.window` (T4): which diagnostics window a line carries, so a reader can avoid counting one window twice
/// when several worlds write a line for it.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TasksWindow {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub id: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub span_ms: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub age_ms: Option<f64>,
}

/// `tasks.pool` (T4).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TasksPool {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub workers: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub long_running: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub queue_max: Option<u64>,
}

/// A `current`/`max` gauge of a task owner (T4).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TaskGauge {
    #[serde(default)]
    pub current: u64,
    #[serde(default)]
    pub max: u64,
}

/// One owner's task numbers in a window (T4): `cpu_ms` is executing-thread time of its segments (awaiting IO is
/// excluded), `wall_ms` the sum of segment wall times, `longest_ms` its longest segment.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TaskOwner {
    #[serde(default)]
    pub owner: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub queued: Option<TaskGauge>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub running: Option<TaskGauge>,
    #[serde(default)]
    pub completed: u64,
    #[serde(default)]
    pub faulted: u64,
    #[serde(default)]
    pub cancelled: u64,
    #[serde(default)]
    pub cpu_ms: f64,
    #[serde(default)]
    pub wall_ms: f64,
    #[serde(default)]
    pub longest_ms: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub longest_name: Option<String>,
    #[serde(default)]
    pub queued_too_long: u64,
    #[serde(default)]
    pub running_too_long: u64,
    #[serde(default)]
    pub queue_full: u64,
    #[serde(default)]
    pub unload_blocked: u64,
}

/// `tasks.apply` (T4): the tick hops dispatched in the window; `deferred` waited for a later step, `ms` is the time
/// spent dispatching them.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TaskApply {
    #[serde(default)]
    pub applied: u64,
    #[serde(default)]
    pub deferred: u64,
    #[serde(default)]
    pub cancelled: u64,
    #[serde(default)]
    pub ms: f64,
    #[serde(default)]
    pub owner_budget_stops: u64,
    #[serde(default)]
    pub step_budget_stops: u64,
    #[serde(default)]
    pub owners_served: u64,
    #[serde(default)]
    pub backlog: u64,
}

/// `tasks.hops` (T4).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TaskHops {
    #[serde(default)]
    pub refused: u64,
}

/// The `tasks` section of a tick-stats line (T4, issue #49), as the before/after report (`tick_compare`) reads it.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct TasksSection {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub window: Option<TasksWindow>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pool: Option<TasksPool>,
    #[serde(default)]
    pub owners: Vec<TaskOwner>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub owners_truncated: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub apply: Option<TaskApply>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hops: Option<TaskHops>,
}

/// A timed part of a tick-stats window: its longest call, the sum, the calls over 50 ms (and p50/p99 for the job wait).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PartMs {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub p50: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub p99: Option<f64>,
    #[serde(default)]
    pub max: f64,
    #[serde(default)]
    pub total: f64,
    #[serde(default)]
    pub over50: u64,
}

/// One owner's subscribers in a tick-stats window (`service:kiln-saves.dll`, `kiln`, `plugin:kiln-emf.dll`, ...).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct SubPart {
    #[serde(default)]
    pub owner: String,
    #[serde(default)]
    pub max: f64,
    #[serde(default)]
    pub total: f64,
    #[serde(default)]
    pub over50: u64,
}

/// `post_parts`: the server world has `wait_ms` and `windows_ms`; a client-side world only `subs`.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PostParts {
    #[serde(default, rename = "wait_ms", skip_serializing_if = "Option::is_none")]
    pub wait_ms: Option<PartMs>,
    #[serde(default, rename = "windows_ms", skip_serializing_if = "Option::is_none")]
    pub windows_ms: Option<PartMs>,
    #[serde(default)]
    pub subs: Vec<SubPart>,
}

/// `pre_parts`.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct PreParts {
    #[serde(default)]
    pub subs: Vec<SubPart>,
}

/// One owner's time in the worst step.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct OwnerMs {
    #[serde(default)]
    pub owner: String,
    #[serde(default)]
    pub ms: f64,
}

/// `worst`: the window's worst step, `at` its local time (`hh:mm:ss.mmm`, as kiln.log's lines).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct WorstStep {
    #[serde(default)]
    pub at: String,
    #[serde(default)]
    pub pre: f64,
    #[serde(default)]
    pub step: f64,
    #[serde(default)]
    pub post: f64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub wait: Option<f64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub windows: Option<f64>,
    #[serde(default, rename = "post_subs")]
    pub post_subs: Vec<OwnerMs>,
    #[serde(default, rename = "pre_subs")]
    pub pre_subs: Vec<OwnerMs>,
}

/// A newer field that does not read is None; it never costs the line its other fields.
fn lenient<'de, D, T>(d: D) -> std::result::Result<Option<T>, D::Error>
where
    D: serde::Deserializer<'de>,
    T: serde::de::DeserializeOwned,
{
    let v = serde_json::Value::deserialize(d)?;
    Ok(serde_json::from_value(v).ok())
}

/// Parses tick-stats.jsonl text (bad lines skipped).
pub fn tick_stats(text: &str) -> Vec<TickStat> {
    text.lines().filter_map(|l| serde_json::from_str::<TickStat>(l.trim()).ok()).collect()
}

/// `kiln/logs/tick-stats.jsonl` of a game folder; the newest `last` lines (all when None).
pub fn read_tick_stats(game_dir: &Path, last: Option<usize>) -> Result<Vec<TickStat>> {
    let p = game_dir.join("kiln/logs/tick-stats.jsonl");
    let text = match std::fs::read(&p) {
        Ok(b) => strip_cr(&b),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(Vec::new()),
        Err(e) => return Err(e.into()),
    };
    let mut v = tick_stats(&text);
    if let Some(n) = last {
        let skip = v.len().saturating_sub(n);
        v.drain(..skip);
    }
    Ok(v)
}

/// Kiln.Host's per-mod cost line: `[host] stats: <mod>: pre X ms/step, post Y ms/step, max Z ms, W ms/s, faults N`; since
/// 0.2.0-dev.29 `max Z ms (pre A / post B)`, the longest Pre and the longest Post apart.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModStat {
    pub time: String,
    #[serde(rename = "mod")]
    pub mod_id: String,
    pub pre_ms: f64,
    pub post_ms: f64,
    pub max_ms: f64,
    /// Since 0.2.0-dev.29.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pre_max_ms: Option<f64>,
    /// Since 0.2.0-dev.29.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub post_max_ms: Option<f64>,
    pub ms_per_s: f64,
    pub faults: u64,
}

/// The `[host] stats: <mod>: ...` lines of a kiln.log (the gc summary line is skipped).
pub fn mod_stats(text: &str) -> Vec<ModStat> {
    let num = |s: &str| s.split_whitespace().next().and_then(|n| n.parse::<f64>().ok());
    text.lines()
        .filter_map(|l| {
            let p = parse_line(l)?;
            if p.source != "host" {
                return None;
            }
            let rest = p.message.strip_prefix("stats: ")?;
            let (id, fields) = rest.split_once(": ")?;
            if id.contains(' ') {
                return None;
            }
            let mut st = ModStat {
                time: p.time.clone(),
                mod_id: id.to_string(),
                pre_ms: 0.0,
                post_ms: 0.0,
                max_ms: 0.0,
                pre_max_ms: None,
                post_max_ms: None,
                ms_per_s: 0.0,
                faults: 0,
            };
            let mut any = false;
            for f in fields.split(", ") {
                if let Some(x) = f.strip_prefix("pre ") {
                    st.pre_ms = num(x)?;
                    any = true;
                } else if let Some(x) = f.strip_prefix("post ") {
                    st.post_ms = num(x)?;
                } else if let Some(x) = f.strip_prefix("max ") {
                    st.max_ms = num(x)?;
                    // "max Z ms (pre A / post B)"
                    if let Some((_, apart)) = x.split_once("(pre ")
                        && let Some((a, b)) = apart.trim_end_matches(')').split_once(" / post ")
                    {
                        st.pre_max_ms = num(a);
                        st.post_max_ms = num(b);
                    }
                } else if let Some(x) = f.strip_prefix("faults ") {
                    st.faults = num(x)? as u64;
                } else if f.ends_with(" ms/s") {
                    st.ms_per_s = num(f)?;
                }
            }
            any.then_some(st)
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// kiln.ui and the game's graphics device: seen created, taken over, or never known (the overlay cannot draw).
    #[test]
    fn the_overlay_needs_the_games_device() {
        let head = "[I 07:15:29.249] [kiln] Kiln 0.2.0-dev starting (pid 328)\n";
        let swap = "[I 07:15:44.462] [service:kiln-ui.dll] ui: swapchain 1280x800 format 44 colour space 0, 4+ images, window 0000000000020282\n";
        // as a Steam Deck with a second Vulkan hooker wrote it with 0.2.0-dev.22: no device line at all
        let lost = boot_state(&format!("{head}\
            [W 07:15:32.528] [service:kiln-ui.dll] ui: vkCreateDevice at 00006FFFFD8316C0 is unverified code/patch: not re-hooked (an existing hook may be underneath)\n{swap}"));
        assert!(lost.ui_swapchain && !lost.ui_device);
        assert_eq!(lost.ui_patched_by, None);
        // the same with a runtime that names the patch's module
        let named = boot_state(&format!("{head}\
            [W 07:15:32.528] [service:kiln-ui.dll] ui: vkCreateDevice at 00006FFFFD8316C0 is unverified code/patch, it now leads into version.dll: not re-hooked (an existing hook may be underneath)\n{swap}"));
        assert!(named.ui_swapchain && !named.ui_device);
        assert_eq!(named.ui_patched_by.as_deref(), Some("version.dll"));
        // seen created, and taken over at the swapchain
        for device in ["ui: game Vulkan device 000000000A0EDCB0 (3 queue families requested, functions ok; 1 live)",
                       "ui: game Vulkan device 000000000A0EDCB0 taken over at its swapchain: Kiln did not see it being created (vkCreateDevice is patched by version.dll). 2 queue(s) known; 1 live"] {
            let ok = boot_state(&format!("{head}[I 07:15:44.1] [service:kiln-ui.dll] {device}\n{swap}"));
            assert!(ok.ui_swapchain && ok.ui_device, "{device}");
        }
        // a destroyed device at exit is not a device
        let gone = boot_state(&format!("{head}{swap}[I 07:17:05.1] [service:kiln-ui.dll] ui: game Vulkan device 000000000A0EDCB0 destroyed (0 live)\n"));
        assert!(!gone.ui_device);
        // a server never draws
        assert!(!boot_state(head).ui_swapchain);
    }

    #[test]
    fn lines_and_boot() {
        let log = "[I 10:56:23.738] [kiln] Kiln 0.1.0 starting (pid 9)\r\n[E 10:56:23.8] [kiln] old error\r\n\
                   [I 10:56:23.738] [kiln] Kiln 0.2.0-dev starting (pid 252)\r\n\
                   [I 10:56:23.807] [kiln] side: server (pdb enshrouded_server.pdb)\r\n\
                   [I 10:56:24.220] [kiln] sigdb: 239/239 symbols, 347/347 constants resolved in 404.9 ms\r\n\
                   [I 10:56:24.222] [kiln] build c5c9: verified\r\n\
                   [I 10:56:24.222] [kiln] tick: SimStep hooked (world.storage=0xe179c0)\r\n\
                   [I 10:56:24.222] [kiln] service kiln.client v1: available\r\n\
                   [I 10:56:24.224] [kiln] service kiln.assets v1 published by service:kiln-assets.dll\r\n\
                   [W 10:56:24.262] [kiln] service kiln-ui.dll: kiln_plugin_main returned 3; revoked\r\n\
                   [I 10:56:24.334] [kiln] boot complete; handing control to the game\r\n\
                   [I 10:56:26.322] [kiln] tick: server world 0xb2410080 started (session 1, thread 388)\r\n\
                   [E 10:56:27.000] [mod:x] boom\r\n";
        let b = boot_state(log);
        assert_eq!(b.kiln.as_deref(), Some("0.2.0-dev"));
        assert_eq!(b.pid, Some(252));
        assert_eq!(b.side.as_deref(), Some("server"));
        assert_eq!(b.verified, Some(true));
        assert!(b.hooks && b.boot_complete && b.world_started);
        assert_eq!(b.services, vec!["kiln.client@1", "kiln.assets@1"]);
        assert_eq!(b.warnings, 1);
        assert_eq!(b.errors, vec!["[mod:x] boom"]);
        let idle = boot_state("[I 1] [kiln] Kiln 0.2.0 starting (pid 1)\n[W 2] [kiln] safe idle: build ab is not verified (x) and policy is verified-only; the game runs vanilla\n");
        assert!(idle.safe_idle.unwrap().starts_with("build ab"));
        assert!(!idle.hooks);
    }

    /// The server line native/core/tests/tick_stats_test.cpp formats (its "sample:" output), read back field for field, and
    /// written and read again unchanged; a client-side world's line without the wait; a part that does not read.
    #[test]
    fn tick_stats_reads_the_parts_of_post_and_pre() {
        let line = r#"{"time":"2026-11-15T13:25:09","steps":2621,"rate":43.68,"clock_rate":60.02,"step_ms":{"p50":9.812,"p99":11.004,"max":11.000},"pre_ms":{"p50":0.9375,"p99":9.6250,"max":127.250},"post_ms":{"p50":0.3125,"p99":0.8438,"max":10249.500},"interval_ms":{"p50":16.75,"p99":21.50,"max":10271.25},"subs":{"pre":30,"post":5},"post_parts":{"wait_ms":{"p50":0.2500,"p99":0.6875,"max":1.500,"total":1.750,"over50":0},"windows_ms":{"max":0.062,"total":0.062,"over50":0},"subs":[{"owner":"service:kiln-gameplay.dll","max":0.500,"total":0.500,"over50":0},{"owner":"kiln","max":60.000,"total":60.250,"over50":1},{"owner":"service:kiln-saves.dll","max":10240.000,"total":10240.000,"over50":1},{"owner":"plugin:kiln-emf.dll","max":4.000,"total":4.000,"over50":0}]},"pre_parts":{"subs":[{"owner":"native:mod-19","max":9.000,"total":9.000,"over50":0},{"owner":"native:mod-18","max":8.000,"total":8.000,"over50":0},{"owner":"native:mod-17","max":7.000,"total":7.000,"over50":0},{"owner":"native:mod-16","max":6.000,"total":6.000,"over50":0},{"owner":"native:mod-15","max":5.000,"total":5.000,"over50":0},{"owner":"native:mod-14","max":4.000,"total":4.000,"over50":0},{"owner":"native:mod-13","max":3.000,"total":3.000,"over50":0},{"owner":"native:mod-12","max":2.000,"total":2.000,"over50":0}]},"worst":{"at":"13:24:58.512","pre":6.000,"step":9.500,"post":10249.500,"wait":1.500,"windows":0.062,"post_subs":[{"owner":"service:kiln-gameplay.dll","ms":0.500},{"owner":"kiln","ms":1.250},{"owner":"service:kiln-saves.dll","ms":10240.000},{"owner":"plugin:kiln-emf.dll","ms":4.000}],"pre_subs":[{"owner":"native:mod-11","ms":5.000},{"owner":"native:mod-10","ms":1.000}]}}"#;
        let v = tick_stats(&format!("{line}\n"));
        assert_eq!(v.len(), 1);
        let t = &v[0];
        // the old fields, as before
        assert_eq!((t.steps, t.rate, t.post_ms.max, t.interval_ms.max), (2621, 43.68, 10249.5, 10271.25));
        assert_eq!(t.subs, serde_json::json!({"pre": 30, "post": 5}));
        assert_eq!(t.world, None);
        // the new ones
        let post = t.post_parts.as_ref().expect("post_parts");
        let wait = post.wait_ms.as_ref().expect("wait_ms");
        assert_eq!((wait.p50, wait.p99, wait.max, wait.total, wait.over50), (Some(0.25), Some(0.6875), 1.5, 1.75, 0));
        assert_eq!(post.windows_ms.as_ref().map(|w| (w.p50, w.max)), Some((None, 0.062)));
        let owners: Vec<_> = post.subs.iter().map(|s| (s.owner.as_str(), s.max, s.over50)).collect();
        assert_eq!(owners[2], ("service:kiln-saves.dll", 10240.0, 1));
        assert_eq!(owners.len(), 4);
        let pre = t.pre_parts.as_ref().expect("pre_parts");
        assert_eq!((pre.subs.len(), pre.subs[0].owner.as_str(), pre.subs[0].max), (8, "native:mod-19", 9.0));
        let worst = t.worst.as_ref().expect("worst");
        assert_eq!((worst.at.as_str(), worst.post, worst.wait, worst.windows), ("13:24:58.512", 10249.5, Some(1.5), Some(0.062)));
        assert_eq!((worst.post_subs[2].owner.as_str(), worst.post_subs[2].ms), ("service:kiln-saves.dll", 10240.0));
        assert_eq!(worst.pre_subs.len(), 2);
        // written and read again: the same
        let again: TickStat = serde_json::from_str(&serde_json::to_string(t).unwrap()).unwrap();
        assert_eq!(&again, t);

        // a client-side world: no wait, no windows
        let client = r#"{"time":"2026-11-15T13:25:09","world":"client","steps":1600,"rate":26.67,"clock_rate":26.67,"step_ms":{"p50":30.000,"p99":40.000,"max":41.000},"pre_ms":{"p50":0.0100,"p99":0.0200,"max":15.000},"post_ms":{"p50":0.0100,"p99":0.0200,"max":1.600},"interval_ms":{"p50":37.00,"p99":335.00,"max":340.00},"subs":{"pre":11,"post":1},"post_parts":{"subs":[{"owner":"plugin:kiln-emf.dll","max":1.600,"total":12.000,"over50":0}]},"pre_parts":{"subs":[]},"worst":{"at":"13:24:01.002","pre":0.020,"step":41.000,"post":0.010,"post_subs":[{"owner":"plugin:kiln-emf.dll","ms":0.010}],"pre_subs":[]}}"#;
        let c = &tick_stats(client)[0];
        assert_eq!(c.world.as_deref(), Some("client"));
        let cp = c.post_parts.as_ref().unwrap();
        assert!(cp.wait_ms.is_none() && cp.windows_ms.is_none() && cp.subs.len() == 1);
        assert_eq!(c.worst.as_ref().map(|w| (w.wait, w.step)), Some((None, 41.0)));

        // a line from before dev.29 has none of them; a part that does not read costs only itself
        let old = &tick_stats(r#"{"time":"t","steps":3,"subs":{"pre":1,"post":0}}"#)[0];
        assert!(old.post_parts.is_none() && old.pre_parts.is_none() && old.worst.is_none());
        let odd = &tick_stats(r#"{"time":"t","steps":3,"post_parts":"later","worst":{"post_subs":7},"pre_parts":{"subs":[{"owner":"x","max":2}]}}"#)[0];
        assert_eq!(odd.steps, 3);
        assert!(odd.post_parts.is_none() && odd.worst.is_none());
        assert_eq!(odd.pre_parts.as_ref().map(|p| p.subs[0].max), Some(2.0));
    }

    #[test]
    fn stats() {
        let t = "{\"time\":\"2026-11-04T10:57:26\",\"steps\":1416,\"rate\":23.59,\"clock_rate\":60.24,\"step_ms\":{\"p50\":1.0,\"p99\":7.474,\"max\":24.311},\"subs\":{\"pre\":18,\"post\":1}}\nnot json\n";
        let v = tick_stats(t);
        assert_eq!(v.len(), 1);
        assert_eq!(v[0].steps, 1416);
        assert_eq!(v[0].step_ms.p99, 7.474);
        let m = mod_stats("[I 1] [host] stats: chat-test: pre 0.0001 ms/step, post 0.0000 ms/step, max 0.001 ms, 0.002 ms/s, faults 3\n\
                           [I 2] [host] stats: gc +0/+0/+0 pause +0.00 ms, heap 4.2 MB, game lines dropped 0\n");
        assert_eq!(m.len(), 1);
        assert_eq!((m[0].mod_id.as_str(), m[0].faults, m[0].ms_per_s), ("chat-test", 3, 0.002));
        assert_eq!((m[0].pre_max_ms, m[0].post_max_ms), (None, None));
        // since dev.29 the longest Pre and Post apart, inside the max field
        let m = mod_stats("[I 1] [host] stats: auto-loot: pre 1.0312 ms/step, post 0.0000 ms/step, max 30.125 ms (pre 30.125 / post 0.000), 61.875 ms/s, faults 0\n");
        assert_eq!(m.len(), 1);
        assert_eq!((m[0].max_ms, m[0].pre_max_ms, m[0].post_max_ms, m[0].ms_per_s), (30.125, Some(30.125), Some(0.0), 61.875));
    }

    #[test]
    fn names() {
        assert!(allowed("kiln/logs/kiln.log"));
        assert!(allowed("logs/enshrouded_server.log"));
        assert!(allowed("enshrouded.log"));
        assert!(!allowed("kiln/logs/../kiln.json"));
        // Windows turns `".. "` into `..` and `"kiln.log."` into `kiln.log`
        assert!(!allowed("kiln/logs/.. /kiln.json"));
        assert!(!allowed("kiln/logs/kiln.log."));
        assert!(!allowed("/etc/passwd"));
        assert!(!allowed("kiln/kiln.json"));
        assert_eq!(kind_of("kiln/logs/kiln.log"), LogKind::Kiln);
        assert_eq!(kind_of("kiln/logs/proxy.log"), LogKind::Proxy);
        assert_eq!(kind_of("kiln/logs/sigdb-report.json"), LogKind::Sigdb);
        assert_eq!(kind_of("kiln/logs/tests/x.json"), LogKind::Mod);
        assert_eq!(kind_of("enshrouded.log"), LogKind::Game);
        assert_eq!(kind_of("kiln/crash-reports/c.txt"), LogKind::Crash);
    }
}
