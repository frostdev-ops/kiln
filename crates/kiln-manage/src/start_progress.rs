//! What a start is doing, from Play to the main menu, and how long it still takes (the contract of 0.2.0-dev.27,
//! `docs/kiln/launcher/app.md` "What a start is doing").
//!
//! One timeline for the whole start: the launcher's own steps (`cook`, `profile`, `steam`, timed here by the launcher:
//! [`LauncherStart`]), then the steps the game's runtime reports in `<game>/kiln/state/progress.json` ([`Progress`],
//! written by kiln-core; judged current exactly as boot.json is: [`crate::launch::state_file_is_current`]), ending at
//! state `ready`. The time left comes from this PC's own earlier starts ([`StartTimes`], `<data>/state/start-times.json`:
//! the last [`KEEP`] starts per game install that reached `ready`) by the rules of [`estimate`]: a number only when every
//! step still to come has a basis, otherwise the steps are counted and the window says the start is being timed.
//!
//! A runtime that writes no progress.json (0.2.0-dev.26 and older) gives no runtime steps: once its boot.json is there
//! the timeline says `legacy` and the window shows what it showed before, and nothing is kept.

use crate::game::GameInstall;
use crate::paths::DataDirs;
use crate::Result;
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// `kiln/state/progress.json`, relative to the game folder.
pub const PROGRESS_JSON: &str = "kiln/state/progress.json";
/// The history, below the data root (a file of `state/`: a move of the data folder carries it).
pub const HISTORY: [&str; 2] = ["state", "start-times.json"];
/// Finished starts kept per game install.
pub const KEEP: usize = 10;
/// Earlier starts a step's usual time is the median of.
pub const BASIS: usize = 5;
/// No write of progress.json for this long: the start is shown as silent, not as progress.
pub const QUIET_MS: u64 = 60_000;
/// The same inside `early-plugins`, where kiln-eml's eml.json is the finer source.
pub const QUIET_EARLY_MS: u64 = 120_000;
/// Steam was asked this long ago and no game process came: the start is over.
pub const STEAM_WAIT_MS: u64 = 5 * 60_000;
/// A running step that has taken this many times its usual time is "taking longer than usual" (as a fraction: 3/2).
pub const SLOW: (u64, u64) = (3, 2);

/// The launcher's own steps, in order.
pub const LAUNCHER_STEPS: [&str; 3] = ["cook", "profile", "steam"];
/// The runtime's step ids (contract section 1), in order.
pub const RUNTIME_STEPS: [&str; 11] =
    ["start", "native-early", "early-plugins", "signatures", "services", "plugins", "native", "dotnet", "mods", "content", "game"];

/// The label of a step that has not begun yet (a begun one carries its own).
pub fn default_label(id: &str) -> &'static str {
    match id {
        "cook" => "Cooking mods",
        "profile" => "Checking the profile",
        "steam" => "Starting the game through Steam",
        "start" => "Reading Kiln's settings and the profile",
        "native-early" => "Loading native mods before the hooks",
        "early-plugins" => "Starting the early plugins",
        "signatures" => "Finding the game's functions",
        "services" => "Starting Kiln's services",
        "plugins" => "Loading Kiln's plugins",
        "native" => "Loading native mods",
        "dotnet" => "Starting .NET",
        "mods" => "Loading the mods",
        "content" => "Applying the mods' content",
        "game" => "The game is loading",
        _ => "Starting",
    }
}

// ---------------------------------------------------------------- progress.json

/// One named piece of a step that took at least 500 ms (contract section 1).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressPart {
    pub label: String,
    #[serde(default)]
    pub took_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProgressStep {
    pub id: String,
    #[serde(default)]
    pub label: String,
    #[serde(default)]
    pub detail: Option<String>,
    #[serde(default)]
    pub done: Option<u64>,
    #[serde(default)]
    pub total: Option<u64>,
    /// When the step began: milliseconds since `started`.
    #[serde(default)]
    pub ms: u64,
    /// How long it took, once it ended.
    #[serde(default)]
    pub took_ms: Option<u64>,
    #[serde(default)]
    pub parts: Vec<ProgressPart>,
}

/// `<game>/kiln/state/progress.json` (format 1), as kiln-core writes it.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Progress {
    pub format: u32,
    #[serde(default)]
    pub pid: u32,
    #[serde(default)]
    pub kiln: Option<String>,
    #[serde(default)]
    pub side: Option<String>,
    /// When Kiln's boot began in this process (UTC, milliseconds).
    pub started: String,
    /// Milliseconds since `started` when the file was written.
    #[serde(default)]
    pub at_ms: u64,
    /// `booting`, `loading`, `ready`, `idle`, `failed`.
    pub state: String,
    #[serde(default)]
    pub plan: Vec<String>,
    #[serde(default)]
    pub steps: Vec<ProgressStep>,
    #[serde(default)]
    pub why: Option<String>,
}

impl Progress {
    /// `ready`, `idle` or `failed`: the file is not written again in this process.
    pub fn is_final(&self) -> bool {
        matches!(self.state.as_str(), "ready" | "idle" | "failed")
    }
}

/// The longest a start or any of its steps can take: a time beyond it is not a time a start took (a file outside the
/// bounds is not readable).
pub const MAX_MS: u64 = 24 * 3600 * 1000;
/// The most things a step can count (`done`, `total`).
pub const MAX_COUNT: u64 = 10_000_000;

fn in_bounds_ms(v: u64) -> bool {
    v <= MAX_MS
}

impl Progress {
    /// Every time within [`MAX_MS`], every count within [`MAX_COUNT`].
    pub fn in_bounds(&self) -> bool {
        in_bounds_ms(self.at_ms)
            && self.steps.iter().all(|s| {
                in_bounds_ms(s.ms)
                    && s.took_ms.is_none_or(in_bounds_ms)
                    && s.done.is_none_or(|d| d <= MAX_COUNT)
                    && s.total.is_none_or(|t| t <= MAX_COUNT)
                    && s.parts.iter().all(|x| in_bounds_ms(x.took_ms))
            })
    }
}

/// The text of a progress.json: format 1 with a start time it can read and every number within its bounds
/// ([`Progress::in_bounds`]), else None (a newer format is not guessed at).
pub fn parse_progress(text: &str) -> Option<Progress> {
    let p: Progress = serde_json::from_str(text.trim_start_matches('\u{feff}')).ok()?;
    (p.format == 1 && parse_ms(&p.started).is_some() && p.in_bounds()).then_some(p)
}

/// RFC 3339 UTC with milliseconds -> unix milliseconds.
pub fn parse_ms(text: &str) -> Option<i64> {
    let secs = crate::util::parse_rfc3339(text)?;
    let frac = text.get(19..).filter(|t| t.starts_with('.')).map(|t| {
        let digits: String = t[1..].chars().take_while(char::is_ascii_digit).collect();
        format!("{digits:0<3}")[..3].parse::<i64>().unwrap_or(0)
    });
    Some(secs * 1000 + frac.unwrap_or(0))
}

/// Unix milliseconds -> `2026-11-13T17:28:19.975Z`.
pub fn fmt_ms(ms: i64) -> String {
    let s = crate::util::rfc3339(ms.div_euclid(1000));
    format!("{}.{:03}Z", &s[..19], ms.rem_euclid(1000))
}

pub fn unix_ms(t: SystemTime) -> i64 {
    match t.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_millis() as i64,
        Err(e) => -(e.duration().as_millis() as i64),
    }
}

/// The progress.json of the game that runs now, with when it was written: None when no game runs, the file is missing
/// or not readable as format 1, or it is not of this process (written before the process started; on Windows another
/// pid): the rule of boot.json ([`crate::launch::state_file_is_current`]).
pub fn read_progress(install: &GameInstall) -> Option<(Progress, SystemTime)> {
    let path = install.path.join(PROGRESS_JSON);
    let written = std::fs::metadata(&path).and_then(|m| m.modified()).ok()?;
    let p = parse_progress(&std::fs::read_to_string(&path).ok()?)?;
    crate::launch::state_file_is_current(install, PROGRESS_JSON, p.pid).then_some((p, written))
}

// ---------------------------------------------------------------- the history

/// One piece of a kept step: a mod's cook (`key` = `<id>@<identity>`) or a runtime part (`key` = its label).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeptPart {
    pub key: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub label: Option<String>,
    pub ms: u64,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeptStep {
    pub id: String,
    /// How long it took.
    pub ms: u64,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub total: Option<u64>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub parts: Vec<KeptPart>,
}

/// A start that reached `ready`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct KeptStart {
    /// The Kiln runtime that ran: progress.json's `kiln` (with the build stamp). The one identity a start is stored
    /// and looked up by ([`version_identity`]).
    pub kiln: String,
    /// The install receipt's version when the start was made: how a start that has not written progress.json yet
    /// finds the `kiln` of earlier starts with the same runtime ([`version_identity`]).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub runtime: Option<String>,
    /// The kms1 of what the start loaded.
    pub fingerprint: String,
    /// When it reached `ready` (RFC 3339).
    #[serde(default)]
    pub finished: String,
    /// progress.json's `started`: one start is kept once, whoever keeps it.
    #[serde(default)]
    pub started: String,
    /// Every step, in order; the launcher's own when the launcher started the game.
    pub steps: Vec<KeptStep>,
}

impl KeptStart {
    pub fn step(&self, id: &str) -> Option<&KeptStep> {
        self.steps.iter().find(|s| s.id == id)
    }
    pub fn total_ms(&self) -> u64 {
        self.steps.iter().fold(0u64, |a, s| a.saturating_add(s.ms))
    }
    fn in_bounds(&self) -> bool {
        self.steps.iter().all(|s| in_bounds_ms(s.ms) && s.total.is_none_or(|t| t <= MAX_COUNT) && s.parts.iter().all(|p| in_bounds_ms(p.ms)))
    }
}

/// The version a start is looked up by: progress.json's `kiln` once the game wrote it; before that, the `kiln` of the
/// newest kept start made with the same installed runtime (the receipt's version); without one, the receipt's version.
pub fn version_identity(progress_kiln: Option<&str>, receipt: Option<&str>, history: &[KeptStart]) -> Option<String> {
    if let Some(k) = progress_kiln {
        return Some(k.to_string());
    }
    let r = receipt?;
    Some(history.iter().rev().find(|s| s.runtime.as_deref() == Some(r)).map_or_else(|| r.to_string(), |s| s.kiln.clone()))
}

/// `<data>/state/start-times.json` (format 1): per game install id, its last [`KEEP`] finished starts, oldest first.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StartTimes {
    pub format: u32,
    #[serde(default)]
    pub installs: BTreeMap<String, Vec<KeptStart>>,
}

impl Default for StartTimes {
    fn default() -> Self {
        StartTimes { format: 1, installs: BTreeMap::new() }
    }
}

pub fn history_path(dirs: &DataDirs) -> PathBuf {
    dirs.root.join(HISTORY[0]).join(HISTORY[1])
}

/// What is in the history's file.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HistoryFile {
    /// No file yet.
    Missing,
    /// A format 1 file within the bounds.
    Read(StartTimes),
    /// A file of a newer format (`format` > 1): left alone, no estimates.
    Newer(u64),
    /// A file that is not readable as any format (not JSON, no `format`, or format 1 that does not parse or is out of
    /// bounds): left alone, no estimates; why, in a sentence.
    Unreadable(String),
}

/// Reads the history's file: the `format` first, then the format 1 shape. A newer or unreadable file is never taken
/// for an empty one.
pub fn read_history_file(dirs: &DataDirs) -> HistoryFile {
    let path = history_path(dirs);
    let bytes = match std::fs::read(&path) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return HistoryFile::Missing,
        Err(e) => return HistoryFile::Unreadable(format!("{}: {e}", path.display())),
    };
    let bytes = bytes.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&bytes);
    let value: serde_json::Value = match serde_json::from_slice(bytes) {
        Ok(v) => v,
        Err(e) => return HistoryFile::Unreadable(format!("{}: {e}", path.display())),
    };
    match value.get("format").and_then(serde_json::Value::as_u64) {
        Some(1) => match serde_json::from_value::<StartTimes>(value) {
            Ok(h) if h.installs.values().flatten().all(KeptStart::in_bounds) => HistoryFile::Read(h),
            Ok(_) => HistoryFile::Unreadable(format!("{}: a time beyond a day", path.display())),
            Err(e) => HistoryFile::Unreadable(format!("{}: {e}", path.display())),
        },
        Some(f) if f > 1 => HistoryFile::Newer(f),
        _ => HistoryFile::Unreadable(format!("{}: no format 1", path.display())),
    }
}

/// The history; empty when there is none, or when it is of a newer format or not readable (then it is left as it is:
/// [`keep`] does not write over it).
pub fn read_history(dirs: &DataDirs) -> StartTimes {
    match read_history_file(dirs) {
        HistoryFile::Read(h) => h,
        _ => StartTimes::default(),
    }
}

/// The starts kept for one install, oldest first.
pub fn history_of(dirs: &DataDirs, install_id: &str) -> Vec<KeptStart> {
    read_history(dirs).installs.remove(install_id).unwrap_or_default()
}

/// What [`keep`] did.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Kept {
    Kept,
    /// This start was kept before (the watcher and the window both see `ready`).
    Already,
    /// The file is of a newer format or not readable: nothing was written over it (the sentence for the log).
    LeftAlone(String),
}

/// Keeps a finished start: under the history's lock (`state/start-times.lock`, two launchers may share the data
/// folder), once per `started` of the install, the last [`KEEP`] only, written atomically through a temporary file of
/// its own. A file of a newer format, or one that is not readable, is left as it is ([`Kept::LeftAlone`]).
pub fn keep(dirs: &DataDirs, install_id: &str, start: KeptStart) -> Result<Kept> {
    let path = history_path(dirs);
    let _lock = crate::state::lock_file(&path.with_file_name("start-times.lock"), Duration::from_secs(3), &|| false,
        "Another Kiln Launcher is writing the start times.")?;
    let mut h = match read_history_file(dirs) {
        HistoryFile::Missing => StartTimes::default(),
        HistoryFile::Read(h) => h,
        HistoryFile::Newer(f) => return Ok(Kept::LeftAlone(format!(
            "{} is of a newer format ({f}) than this launcher knows: it is left as it is, and this launcher gives no time estimates", path.display()))),
        HistoryFile::Unreadable(why) => return Ok(Kept::LeftAlone(format!(
            "the start times are not readable ({why}): the file is left as it is and no time estimates are given; delete it to start them again"))),
    };
    if !start.in_bounds() {
        return Ok(Kept::LeftAlone("a start with a time beyond a day is not kept".into()));
    }
    let list = h.installs.entry(install_id.to_string()).or_default();
    if !start.started.is_empty() && list.iter().any(|s| s.started == start.started) {
        return Ok(Kept::Already);
    }
    list.push(start);
    let extra = list.len().saturating_sub(KEEP);
    list.drain(..extra);
    let mut text = serde_json::to_string_pretty(&h)?;
    text.push('\n');
    crate::util::write_atomic_exclusive(&path, text.as_bytes())?;
    Ok(Kept::Kept)
}

// ---------------------------------------------------------------- the launcher's own steps

/// One of the launcher's steps as it runs.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherStep {
    pub id: String,
    pub label: String,
    pub detail: Option<String>,
    pub done: Option<u64>,
    pub total: Option<u64>,
    /// When it began, milliseconds since the start began.
    pub ms: u64,
    pub took_ms: Option<u64>,
    /// The pieces it is made of, in order, when known before (the cook: the mods due, as `<id>@<identity>`).
    pub keys: Vec<String>,
    /// The pieces that ended.
    pub parts: Vec<KeptPart>,
    /// When the running piece began (milliseconds since the start began).
    pub part_ms: u64,
}

/// A start this launcher makes (Play), timed by the launcher: begun at Play, the cook, the profile, Steam; then the
/// runtime's steps from progress.json. Times are unix milliseconds in, offsets from `began` inside.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherStart {
    pub began: i64,
    /// The launcher steps this start goes through (a start without mods cooks nothing: no `cook`).
    pub plan: Vec<String>,
    pub steps: Vec<LauncherStep>,
    /// The Kiln runtime in the game when Play was pressed.
    pub kiln: Option<String>,
    /// The kms1 of the start (the launch record's, once Steam was asked).
    pub fingerprint: Option<String>,
    /// Steam was asked (the `steam` step runs until the game's process exists).
    pub asked: bool,
    /// The game's process was seen running for this start.
    pub seen: bool,
    /// This start is kept in the history.
    pub kept: bool,
}

/// A cook job as the start knows it: `<id>@<identity>` and the mod's name.
pub fn cook_key(id: &str, identity: &str) -> String {
    format!("{id}@{identity}")
}

impl LauncherStart {
    /// Play was pressed at `now`. `cook`: the keys of the mods due for a cook (None: a start that does not cook).
    pub fn new(now: i64, cook: Option<Vec<String>>, kiln: Option<String>, fingerprint: Option<String>) -> Self {
        let mut s = LauncherStart { began: now, plan: Vec::new(), steps: Vec::new(), kiln, fingerprint, asked: false, seen: false, kept: false };
        if let Some(keys) = cook {
            s.plan.push("cook".into());
            s.begin("cook", now);
            let total = keys.len() as u64;
            if let Some(c) = s.steps.last_mut() {
                c.keys = keys;
                c.total = Some(total);
                c.done = Some(0);
            }
        }
        s.plan.extend(["profile".to_string(), "steam".to_string()]);
        s
    }

    fn rel(&self, now: i64) -> u64 {
        (now - self.began).max(0) as u64
    }

    /// When the last of its steps ended, if none runs and Steam was not asked yet (the launcher waits for the next
    /// step: launch_play after the cook); milliseconds since the start began.
    pub fn waiting_since(&self) -> Option<u64> {
        if self.asked || self.running().is_some() {
            return None;
        }
        self.steps.last().map(|s| s.ms.saturating_add(s.took_ms.unwrap_or(0)))
    }

    pub fn running(&self) -> Option<&LauncherStep> {
        self.steps.last().filter(|s| s.took_ms.is_none())
    }

    /// Ends the running step at `now` (its last piece too).
    pub fn end_running(&mut self, now: i64) {
        let at = self.rel(now);
        if let Some(s) = self.steps.last_mut().filter(|s| s.took_ms.is_none()) {
            if s.id == "cook" {
                close_part(s, at);
            }
            s.took_ms = Some(at.saturating_sub(s.ms));
        }
    }

    /// Step `id` begins at `now`; the running one ends. A step already begun is not begun again.
    pub fn begin(&mut self, id: &str, now: i64) {
        if self.steps.iter().any(|s| s.id == id) {
            return;
        }
        self.end_running(now);
        let at = self.rel(now);
        self.steps.push(LauncherStep { id: id.into(), label: default_label(id).into(), detail: None, done: None, total: None, ms: at,
            took_ms: None, keys: Vec::new(), parts: Vec::new(), part_ms: at });
    }

    /// The cook's progress (`cook::cook_mods`'s `on_event`): mod `index` of `total` (`key`, `name`) began (`line` None)
    /// or its tools said `line`.
    pub fn cook_event(&mut self, now: i64, index: usize, total: usize, key: &str, name: &str, line: Option<&str>) {
        let at = self.rel(now);
        let Some(s) = self.steps.last_mut().filter(|s| s.id == "cook" && s.took_ms.is_none()) else { return };
        if line.is_none() {
            // the mod before this one ended, this one begins
            close_part(s, at);
            s.part_ms = at;
            if s.keys.len() != total || s.keys.get(index).is_some_and(|k| k != key) {
                // the cook found other mods due than Play's plan: no piece-by-piece estimate for it
                s.keys.clear();
            }
            s.parts.push(KeptPart { key: key.into(), label: Some(name.into()), ms: 0 });
        }
        s.done = Some(index as u64);
        s.total = Some(total as u64);
        s.label = if total == 1 { format!("Cooking {name}") } else { format!("Cooking {name} ({} of {total})", index + 1) };
        s.detail = line.map(|l| l.trim().chars().take(160).collect());
    }

    /// The cook ended at `now` (`cook` the mods it cooked, whatever the outcome).
    pub fn cook_done(&mut self, now: i64) {
        let at = self.rel(now);
        if let Some(s) = self.steps.last_mut().filter(|s| s.id == "cook" && s.took_ms.is_none()) {
            close_part(s, at);
            if let Some(t) = s.total {
                s.done = Some(t);
            }
            s.label = default_label("cook").into();
            s.detail = None;
            s.took_ms = Some(at.saturating_sub(s.ms));
        }
    }
}

/// The running piece of the cook (the last part with ms 0) ends at `at`.
fn close_part(s: &mut LauncherStep, at: u64) {
    if let Some(p) = s.parts.last_mut().filter(|p| p.ms == 0) {
        p.ms = at.saturating_sub(s.part_ms).max(1);
    }
}

// ---------------------------------------------------------------- the estimate

/// A step as the estimate sees it.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct StepNow {
    pub id: String,
    /// When it began (milliseconds since the start began).
    pub ms: u64,
    pub took_ms: Option<u64>,
    pub done: Option<u64>,
    pub total: Option<u64>,
    /// The pieces it is made of, in order, when known before it began (the cook's mods).
    pub keys: Vec<String>,
    /// The pieces that ended (`key`, `ms`).
    pub parts: Vec<KeptPart>,
    /// When its running piece began.
    pub part_ms: u64,
}

/// What a step usually takes, and from how many earlier starts.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Usual {
    pub ms: u64,
    pub starts: u32,
    /// From starts with the same Kiln and the same mods (else: any start of this install, scaled by `total`).
    pub same: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Slow {
    pub id: String,
    pub usual_ms: u64,
}

/// The time left and how the bar is filled.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Estimate {
    /// None: a step still to come has no basis (the first start with these mods): no number is shown.
    pub left_ms: Option<u64>,
    /// How many earlier starts the number comes from (the most any step used).
    pub starts: u32,
    /// Every step's basis is starts with the same Kiln and the same mods.
    pub same: bool,
    /// The running step has taken 1.5 times its usual time.
    pub slow: Option<Slow>,
    /// 0..1: by time when there is a number, else by steps; below 1 until `ready`.
    pub fraction: f64,
    pub by_time: bool,
    pub steps_done: u32,
    pub steps_planned: u32,
    /// Each planned step's usual time (None: no basis), in the plan's order.
    pub usual: Vec<Option<u64>>,
}

fn median(mut v: Vec<u64>) -> Option<u64> {
    if v.is_empty() {
        return None;
    }
    v.sort_unstable();
    let n = v.len();
    Some(if n % 2 == 1 { v[n / 2] } else { ((v[n / 2 - 1] as u128 + v[n / 2] as u128) / 2) as u64 })
}

/// A step's usual time (contract section 2): the median over the last up to [`BASIS`] kept starts with the same Kiln
/// and profile fingerprint; if none, over the last up to [`BASIS`] of the install, each scaled by `total` when both
/// have one; if none either, None. A step made of known pieces (the cook's mods) is the sum of each piece's median over
/// the starts that have it, when every piece has one.
pub fn usual(history: &[KeptStart], id: &str, total: Option<u64>, keys: &[String], kiln: Option<&str>, fingerprint: Option<&str>) -> Option<Usual> {
    if !keys.is_empty() {
        let mut sum = 0;
        let mut starts = 0;
        let mut all = true;
        for k in keys {
            let v: Vec<u64> = history.iter().rev().filter_map(|r| r.step(id)?.parts.iter().find(|p| &p.key == k).map(|p| p.ms)).take(BASIS).collect();
            starts = starts.max(v.len() as u32);
            match median(v) {
                Some(m) => sum = u64::saturating_add(sum, m),
                None => all = false,
            }
        }
        if all {
            return Some(Usual { ms: sum, starts, same: true });
        }
    }
    let same: Vec<u64> = history.iter().rev()
        .filter(|r| kiln.is_some_and(|k| r.kiln == k) && fingerprint.is_some_and(|f| r.fingerprint == f))
        .filter_map(|r| r.step(id).map(|s| s.ms)).take(BASIS).collect();
    if !same.is_empty() {
        return Some(Usual { starts: same.len() as u32, ms: median(same)?, same: true });
    }
    let any: Vec<u64> = history.iter().rev().filter_map(|r| r.step(id)).take(BASIS)
        .map(|s| match (total, s.total) {
            (Some(t), Some(was)) if t > 0 && was > 0 => (s.ms as u128 * t as u128 / was as u128).min(MAX_MS as u128) as u64,
            _ => s.ms,
        }).collect();
    if any.is_empty() {
        return None;
    }
    Some(Usual { starts: any.len() as u32, ms: median(any)?, same: false })
}

/// The time left at `now` (milliseconds since the start began) for a start that goes through `plan`, with `steps` begun
/// so far (in order; the last without `took_ms` is the running one), against `history` (this install's kept starts,
/// oldest first). Contract section 2:
/// - a step that has not ended counts with its [`usual`] time; the running one less what it has taken already, never
///   below 0, or `elapsed * (total - done) / done` when it has `done` > 0 and has run 2 s, whichever is larger
///   ([`RateRule`]: with [`RATE_RULE`] the rate counts only when the usual time is not from the same Kiln and mods:
///   one slow item among quick ones, kiln-vfx among 31 services, makes the rate several times too high);
/// - a planned step that was passed over (a later one began) counts as done;
/// - when any step still to come has no basis there is no number: the bar counts steps;
/// - a running step at 1.5 times its usual time is `slow`: its share is spent, the time left is what the steps after
///   it usually take, and stays there until it ends;
/// - the bar reaches 1 only at `ready`.
pub fn estimate(plan: &[String], steps: &[StepNow], now: u64, history: &[KeptStart], kiln: Option<&str>, fingerprint: Option<&str>, ready: bool) -> Estimate {
    estimate_by(plan, steps, now, history, kiln, fingerprint, ready, RATE_RULE)
}

/// How the running step's rate (`elapsed * (total - done) / done`) and its usual time are put together.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RateRule {
    /// The larger of the two, always (the contract's wording).
    Larger,
    /// The usual time when it comes from starts with the same Kiln and mods (spent: 0, the step is "taking longer than
    /// usual" and the time left stays at what the steps after it take); the larger of the two otherwise (another set
    /// of mods or another Kiln, or no usual time).
    SameFirst,
}

/// The rule [`estimate`] uses.
pub const RATE_RULE: RateRule = RateRule::SameFirst;

#[allow(clippy::too_many_arguments)]
pub fn estimate_by(plan: &[String], steps: &[StepNow], now: u64, history: &[KeptStart], kiln: Option<&str>, fingerprint: Option<&str>, ready: bool,
                   rule: RateRule) -> Estimate {
    let pos = |id: &str| plan.iter().position(|p| p == id);
    let last_begun = steps.iter().filter_map(|s| pos(&s.id)).max();
    let mut left: Option<u64> = Some(0);
    let mut starts = 0u32;
    let mut same = true;
    let mut slow = None;
    let mut done = 0u32;
    let mut usuals = Vec::with_capacity(plan.len());
    for (i, id) in plan.iter().enumerate() {
        let step = steps.iter().find(|s| &s.id == id);
        let u = usual(history, id, step.and_then(|s| s.total), step.map(|s| s.keys.as_slice()).unwrap_or(&[]), kiln, fingerprint);
        if let Some(u) = u {
            starts = starts.max(u.starts);
            same &= u.same;
        }
        usuals.push(u.map(|u| u.ms));
        let rest: Option<u64> = match step {
            Some(s) if s.took_ms.is_some() || ready => {
                done += 1;
                Some(0)
            }
            Some(s) => {
                let elapsed = now.saturating_sub(s.ms);
                let by_usual = u.map(|u| running_left(s, u.ms, now, history, id));
                let by_rate = match (s.done, s.total) {
                    (Some(d), Some(t)) if d > 0 && elapsed >= 2000 => Some((elapsed as u128 * t.saturating_sub(d) as u128 / d as u128).min(MAX_MS as u128) as u64),
                    _ => None,
                };
                let is_slow = u.is_some_and(|u| u.ms > 0 && elapsed.saturating_mul(SLOW.1) >= u.ms.saturating_mul(SLOW.0));
                if is_slow && let Some(u) = u {
                    slow = Some(Slow { id: id.clone(), usual_ms: u.ms });
                }
                match (by_usual, by_rate) {
                    // the slow rule decides first: its share is spent, the time left never grows under "longer than usual"
                    (Some(a), _) if is_slow => Some(a),
                    (Some(a), Some(_)) if rule == RateRule::SameFirst && u.is_some_and(|u| u.same) => Some(a),
                    (Some(a), Some(b)) => Some(a.max(b)),
                    (a, b) => a.or(b),
                }
            }
            None if last_begun.is_some_and(|l| l > i) || ready => {
                done += 1;
                Some(0)
            }
            None => u.map(|u| u.ms),
        };
        left = match (left, rest) {
            (Some(a), Some(b)) => Some(a.saturating_add(b)),
            _ => None,
        };
    }
    let planned = plan.len() as u32;
    let (fraction, by_time) = match left {
        _ if ready => (1.0, left.is_some()),
        Some(l) if now.saturating_add(l) > 0 => ((now as f64 / (now as f64 + l as f64)).min(0.99), true),
        Some(_) => (0.0, true),
        None => ((done as f64 / planned.max(1) as f64).min(0.99), false),
    };
    Estimate { left_ms: if ready { Some(0) } else { left }, starts, same: same && starts > 0, slow, fraction, by_time, steps_done: done,
        steps_planned: planned, usual: usuals }
}

/// What the running step still takes by its usual time: piece by piece when it is made of known pieces that all have a
/// history (the mods still to cook, the running one less what it has taken), else its usual time less what it has taken.
fn running_left(s: &StepNow, usual_ms: u64, now: u64, history: &[KeptStart], id: &str) -> u64 {
    if !s.keys.is_empty() {
        let each: Option<Vec<u64>> = s.keys.iter().map(|k| {
            median(history.iter().rev().filter_map(|r| r.step(id)?.parts.iter().find(|p| &p.key == k).map(|p| p.ms)).take(BASIS).collect())
        }).collect();
        if let Some(each) = each {
            let ended = s.parts.iter().filter(|p| p.ms > 0).count();
            let running = each.get(ended).map_or(0, |u| u.saturating_sub(now.saturating_sub(s.part_ms)));
            return each.iter().skip(ended + 1).fold(running, |a, b| a.saturating_add(*b));
        }
    }
    usual_ms.saturating_sub(now.saturating_sub(s.ms))
}

// ---------------------------------------------------------------- the timeline

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "kebab-case")]
pub enum Phase {
    /// The launcher's own steps (the cook, the profile, Steam).
    Launcher,
    /// The game's process exists, the runtime has not said anything yet.
    Waiting,
    /// Inside Kiln's boot.
    Booting,
    /// Kiln's boot is complete, the game goes on loading.
    Loading,
    /// The main menu can be used.
    Ready,
    /// Safe idle: the game runs without mods (`why`).
    Idle,
    /// Kiln could not go on (`why`).
    Failed,
    /// progress.json was not written for [`QUIET_MS`] ([`QUIET_EARLY_MS`] in `early-plugins`).
    Quiet,
    /// The game ended before it was ready.
    Ended,
    /// The runtime writes no progress.json (0.2.0-dev.26 and older): boot.json is the only news.
    Legacy,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum StepState {
    Done,
    Running,
    Next,
    /// Planned, and passed over.
    Skipped,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct TimelineStep {
    pub id: String,
    pub label: String,
    pub detail: Option<String>,
    pub done: Option<u64>,
    pub total: Option<u64>,
    /// When it began (milliseconds since the start began); None: not yet.
    pub ms: Option<u64>,
    pub took_ms: Option<u64>,
    /// What it usually takes on this PC (None: no basis yet).
    pub usual_ms: Option<u64>,
    pub state: StepState,
    /// It belongs to the launcher (`cook`, `profile`, `steam`), else to the runtime.
    pub launcher: bool,
}

/// The whole start as the window shows it (`launch_progress`).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Timeline {
    pub phase: Phase,
    /// `idle` and `failed`: the runtime's sentence.
    pub why: Option<String>,
    /// The Kiln runtime's identity ([`version_identity`]).
    pub kiln: Option<String>,
    /// When the start began (Play, else Kiln's boot), RFC 3339 with milliseconds.
    pub started: String,
    /// Milliseconds since then.
    pub at_ms: u64,
    /// Milliseconds since progress.json was last written (None: no file of this start).
    pub quiet_ms: Option<u64>,
    /// Every planned step, in order: done, running, to come.
    pub steps: Vec<TimelineStep>,
    /// The running step's index in `steps`.
    pub running: Option<usize>,
    pub estimate: Estimate,
    /// The launcher's own steps are in it (the launcher started the game).
    pub launcher: bool,
}

/// What [`timeline`] is made from (times are unix milliseconds).
#[derive(Debug, Clone, Default)]
pub struct Inputs<'a> {
    pub launcher: Option<&'a LauncherStart>,
    pub progress: Option<&'a Progress>,
    /// When progress.json was last written.
    pub written: Option<i64>,
    /// When the game's process started (None: not running, or not known).
    pub process: Option<i64>,
    pub running: bool,
    /// boot.json of the running process is there.
    pub boot: bool,
    pub now: i64,
    /// This install's kept starts, oldest first.
    pub history: &'a [KeptStart],
    /// The install receipt's runtime version (progress.json's `kiln` wins once there: [`version_identity`]).
    pub kiln: Option<&'a str>,
    pub fingerprint: Option<&'a str>,
}

/// The runtime's plan before progress.json says it: the newest kept start's (with the same Kiln and mods first), else
/// None.
fn runtime_plan_from(history: &[KeptStart], kiln: Option<&str>, fingerprint: Option<&str>) -> Option<Vec<String>> {
    let runtime = |r: &KeptStart| -> Vec<String> { r.steps.iter().filter(|s| !LAUNCHER_STEPS.contains(&s.id.as_str())).map(|s| s.id.clone()).collect() };
    history.iter().rev().find(|r| kiln.is_some_and(|k| r.kiln == k) && fingerprint.is_some_and(|f| r.fingerprint == f))
        .or_else(|| history.last()).map(runtime).filter(|p| !p.is_empty())
}

/// The start as one timeline, None when there is nothing to show (no start of this launcher, no progress.json of the
/// running game).
pub fn timeline(i: &Inputs) -> Option<Timeline> {
    let l = i.launcher;
    let p = i.progress;
    if l.is_none() && p.is_none() {
        return None;
    }
    let p_started = p.and_then(|p| parse_ms(&p.started));
    let t0 = l.map(|l| l.began).or(p_started)?;
    let now = i.now.saturating_sub(t0).max(0) as u64;
    // one identity for storing and looking up: progress.json's `kiln` (before it, found through the receipt's version)
    let kiln = version_identity(p.and_then(|p| p.kiln.as_deref()), i.kiln, i.history);
    // the launcher's steps
    let mut steps: Vec<StepNow> = Vec::new();
    let mut shown: Vec<TimelineStep> = Vec::new();
    let mut plan: Vec<String> = l.map(|l| l.plan.clone()).unwrap_or_default();
    if let Some(l) = l {
        for s in &l.steps {
            let mut took = s.took_ms;
            if s.id == "steam" && took.is_none() {
                // Steam's step ends when the game's process exists (or Kiln's boot began, if that is earlier)
                let end = [i.process, p_started].into_iter().flatten().min();
                if let Some(e) = end.filter(|_| i.running || p.is_some()) {
                    took = Some((e.saturating_sub(t0).max(0) as u64).saturating_sub(s.ms));
                }
            }
            steps.push(StepNow { id: s.id.clone(), ms: s.ms, took_ms: took, done: s.done, total: s.total, keys: s.keys.clone(), parts: s.parts.clone(), part_ms: s.part_ms });
            shown.push(TimelineStep { id: s.id.clone(), label: s.label.clone(), detail: s.detail.clone(), done: s.done, total: s.total, ms: Some(s.ms),
                took_ms: took, usual_ms: None, state: StepState::Done, launcher: true });
        }
    }
    // the runtime's steps
    // before progress.json: the last start's runtime steps, else the contract's list (shown as to come, no number)
    let known = match p {
        Some(p) => Some(p.plan.clone()),
        None => runtime_plan_from(i.history, kiln.as_deref(), i.fingerprint),
    };
    let planned_runtime = known.is_some();
    let runtime_plan = known.unwrap_or_else(|| RUNTIME_STEPS.iter().map(|s| s.to_string()).collect());
    plan.extend(runtime_plan.iter().filter(|id| !LAUNCHER_STEPS.contains(&id.as_str())).cloned());
    if let (Some(p), Some(ps)) = (p, p_started) {
        let base = ps.saturating_sub(t0).max(0) as u64;
        // a launcher step still open when Kiln's boot began ends there
        let base = base.max(steps.iter().map(|s| s.ms).max().unwrap_or(0));
        let n = p.steps.len();
        for (k, s) in p.steps.iter().enumerate() {
            let took = s.took_ms.or_else(|| p.steps.get(k + 1).map(|next| next.ms.saturating_sub(s.ms)))
                .or_else(|| (k + 1 == n && p.is_final()).then(|| p.at_ms.saturating_sub(s.ms)));
            let parts = s.parts.iter().map(|x| KeptPart { key: x.label.clone(), label: None, ms: x.took_ms }).collect();
            let at = base.saturating_add(s.ms);
            steps.push(StepNow { id: s.id.clone(), ms: at, took_ms: took, done: s.done, total: s.total, keys: Vec::new(), parts, part_ms: at });
            shown.push(TimelineStep { id: s.id.clone(), label: if s.label.is_empty() { default_label(&s.id).into() } else { s.label.clone() },
                detail: s.detail.clone(), done: s.done, total: s.total, ms: Some(at), took_ms: took, usual_ms: None, state: StepState::Done, launcher: false });
            if !plan.contains(&s.id) {
                plan.push(s.id.clone());
            }
        }
    }
    let ready = p.is_some_and(|p| p.state == "ready");
    let mut est = estimate(&plan, &steps, now, i.history, kiln.as_deref(), i.fingerprint, ready);
    if !planned_runtime {
        // nothing tells the runtime's steps yet: the bar counts the contract's list, and there is no number
        est.left_ms = None;
        est.by_time = false;
        est.fraction = (est.steps_done as f64 / est.steps_planned.max(1) as f64).min(0.99);
    }
    // every planned step in order, the begun ones as they are
    let last_begun = steps.iter().filter_map(|s| plan.iter().position(|x| x == &s.id)).max();
    let mut out = Vec::with_capacity(plan.len());
    for (k, id) in plan.iter().enumerate() {
        let mut t = match shown.iter().position(|s| &s.id == id) {
            Some(j) => shown[j].clone(),
            None => TimelineStep { id: id.clone(), label: default_label(id).into(), detail: None, done: None, total: None, ms: None, took_ms: None,
                usual_ms: None, state: StepState::Next, launcher: LAUNCHER_STEPS.contains(&id.as_str()) },
        };
        t.usual_ms = est.usual.get(k).copied().flatten();
        t.state = match (t.ms, t.took_ms) {
            (Some(_), Some(_)) => StepState::Done,
            (Some(_), None) if ready => StepState::Done,
            (Some(_), None) => StepState::Running,
            (None, _) if ready || last_begun.is_some_and(|b| b > k) => StepState::Skipped,
            (None, _) => StepState::Next,
        };
        out.push(t);
    }
    let running = out.iter().position(|s| s.state == StepState::Running);
    let quiet_ms = p.and(i.written).map(|w| i.now.saturating_sub(w).max(0) as u64);
    let in_early = running.is_some_and(|r| out[r].id == "early-plugins");
    let phase = match p {
        Some(p) => match p.state.as_str() {
            "ready" => Phase::Ready,
            "idle" => Phase::Idle,
            "failed" => Phase::Failed,
            _ if !i.running => Phase::Ended,
            _ if quiet_ms.is_some_and(|q| q >= if in_early { QUIET_EARLY_MS } else { QUIET_MS }) => Phase::Quiet,
            "loading" => Phase::Loading,
            _ => Phase::Booting,
        },
        None if l.is_some_and(|l| l.seen) && !i.running => Phase::Ended,
        // Steam was asked and no game came: the start is over (the window goes back to Play)
        None if !i.running && l.is_some_and(|l| l.asked && l.steps.iter().find(|s| s.id == "steam").is_some_and(|s| now.saturating_sub(s.ms) >= STEAM_WAIT_MS)) => Phase::Ended,
        None if i.running && i.boot => Phase::Legacy,
        None if i.running => Phase::Waiting,
        None => Phase::Launcher,
    };
    Some(Timeline {
        phase,
        why: p.and_then(|p| p.why.clone()).filter(|_| matches!(phase, Phase::Idle | Phase::Failed)),
        kiln,
        started: fmt_ms(t0),
        at_ms: now,
        quiet_ms,
        steps: out,
        running,
        estimate: est,
        launcher: l.is_some(),
    })
}

/// The kept form of a start that reached `ready` (None: it did not). It is stored under the timeline's version identity
/// (progress.json's `kiln`), with the install receipt's version beside it (`receipt`, [`version_identity`]).
pub fn kept_from(t: &Timeline, progress: &Progress, receipt: Option<&str>, fingerprint: &str, finished: i64, launcher: Option<&LauncherStart>) -> Option<KeptStart> {
    if t.phase != Phase::Ready {
        return None;
    }
    let kiln = t.kiln.clone().or_else(|| receipt.map(str::to_string)).unwrap_or_default();
    let steps = t.steps.iter().filter(|s| s.ms.is_some()).map(|s| {
        let parts = if s.launcher {
            launcher.and_then(|l| l.steps.iter().find(|x| x.id == s.id)).map(|x| x.parts.iter().filter(|p| p.ms > 0).cloned().collect()).unwrap_or_default()
        } else {
            progress.steps.iter().find(|x| x.id == s.id).map(|x| x.parts.iter().take(16).map(|p| KeptPart { key: p.label.clone(), label: None, ms: p.took_ms }).collect())
                .unwrap_or_default()
        };
        KeptStep { id: s.id.clone(), ms: s.took_ms.unwrap_or(0), total: s.total, parts }
    }).collect();
    Some(KeptStart { kiln, runtime: receipt.map(str::to_string), fingerprint: fingerprint.into(), finished: fmt_ms(finished), started: progress.started.clone(), steps })
}

/// The bar's highest point per start (by `Timeline::started`), kept by the launcher: the bar never moves back between
/// two looks, whoever looks (the Play page, the pill, the Deck, a second window).
#[derive(Debug, Default)]
pub struct Steady {
    max: BTreeMap<String, f64>,
}

impl Steady {
    pub fn apply(&mut self, t: &mut Timeline) {
        if self.max.len() > 16 && !self.max.contains_key(&t.started) {
            self.max.clear();
        }
        let m = self.max.entry(t.started.clone()).or_insert(0.0);
        if t.estimate.fraction >= *m {
            *m = t.estimate.fraction;
        } else {
            t.estimate.fraction = *m;
        }
    }
}

/// One look at a game's start as the launcher makes it (`launch_progress`, and the watcher when progress.json changes):
/// the timeline from this launcher's own steps, the game's side and the history, with the bar kept from moving back,
/// and the start kept in the history once it reached `ready` (unless `launcher` says it was kept already).
pub struct Round {
    pub timeline: Option<Timeline>,
    /// What keeping the start did, when it reached `ready` now.
    pub kept: Option<Kept>,
}

#[allow(clippy::too_many_arguments)]
pub fn round(dirs: &DataDirs, install_id: &str, launcher: Option<&LauncherStart>, side: &GameSide, receipt: Option<&str>, fingerprint: Option<&str>,
             now: i64, steady: &mut Steady) -> Round {
    let history = history_of(dirs, install_id);
    let Some(mut tl) = timeline(&Inputs {
        launcher,
        progress: side.progress.as_ref(),
        written: side.written,
        process: side.process,
        running: side.running,
        boot: side.boot,
        now,
        history: &history,
        kiln: receipt,
        fingerprint,
    }) else {
        return Round { timeline: None, kept: None };
    };
    steady.apply(&mut tl);
    let mut kept = None;
    if tl.phase == Phase::Ready
        && let Some(p) = side.progress.as_ref()
        && !launcher.is_some_and(|l| l.kept)
        && let Some(k) = kept_from(&tl, p, receipt, fingerprint.unwrap_or_default(), now, launcher)
    {
        kept = Some(keep(dirs, install_id, k).unwrap_or_else(|e| Kept::LeftAlone(e.message)));
    }
    Round { timeline: Some(tl), kept }
}

/// Reads what the game's side says for `install` at `now`: progress.json (current or None), its write time, the
/// process start, whether boot.json is current. The launcher's [`timeline`] call takes these.
#[derive(Debug, Clone, Default)]
pub struct GameSide {
    pub progress: Option<Progress>,
    pub written: Option<i64>,
    pub process: Option<i64>,
    pub running: bool,
    pub boot: bool,
}

pub fn game_side(install: &GameInstall) -> GameSide {
    let proc = crate::proc::find_game(install).ok().flatten();
    let process = proc.as_ref().and_then(|p| crate::proc::start_time(p.pid)).map(unix_ms);
    let (progress, written) = match proc.as_ref().and_then(|_| read_progress(install)) {
        Some((p, w)) => (Some(p), Some(unix_ms(w))),
        None => (None, None),
    };
    let boot = proc.is_some() && crate::state::read_boot(&install.path).ok().flatten().is_some_and(|b| crate::launch::boot_is_current(install, &b));
    GameSide { progress, written, process, running: proc.is_some(), boot }
}

#[cfg(test)]
#[path = "start_progress_tests.rs"]
mod tests;
