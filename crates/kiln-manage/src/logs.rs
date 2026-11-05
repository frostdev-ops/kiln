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
    if name.split('/').any(|c| c.is_empty() || c == "." || c == "..") {
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

/// Kiln.Host's per-mod cost line: `[host] stats: <mod>: pre X ms/step, post Y ms/step, max Z ms, W ms/s, faults N`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModStat {
    pub time: String,
    #[serde(rename = "mod")]
    pub mod_id: String,
    pub pre_ms: f64,
    pub post_ms: f64,
    pub max_ms: f64,
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
            let mut st = ModStat { time: p.time.clone(), mod_id: id.to_string(), pre_ms: 0.0, post_ms: 0.0, max_ms: 0.0, ms_per_s: 0.0, faults: 0 };
            let mut any = false;
            for f in fields.split(", ") {
                if let Some(x) = f.strip_prefix("pre ") {
                    st.pre_ms = num(x)?;
                    any = true;
                } else if let Some(x) = f.strip_prefix("post ") {
                    st.post_ms = num(x)?;
                } else if let Some(x) = f.strip_prefix("max ") {
                    st.max_ms = num(x)?;
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
    }

    #[test]
    fn names() {
        assert!(allowed("kiln/logs/kiln.log"));
        assert!(allowed("logs/enshrouded_server.log"));
        assert!(allowed("enshrouded.log"));
        assert!(!allowed("kiln/logs/../kiln.json"));
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
