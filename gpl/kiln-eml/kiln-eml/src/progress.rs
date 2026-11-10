// SPDX-License-Identifier: GPL-3.0-or-later
//! Where a run's progress goes, so that a start never looks dead (EML 0.2.0 is silent for minutes on a big install):
//!
//! - the state file `<game>/kiln/state/eml.json` gets `phase: "running"` and a `progress` object (stage, bytes done and to
//!   do) a few times a second, replaced whole each time, so the launcher can show it;
//! - Kiln's progress window (the `kiln.progress` service of kiln-core: a small native window that appears when the work
//!   lasts more than a couple of seconds), when the core has one on this side and desktop.
//!
//! The run's counters are `mod_loader::lua::Progress` (mod-loader-lua/src/progress.rs); `Live` is its `Sink` and is called
//! from that thread only. Nothing here can fail a run: every error is swallowed, a missing window or an unwritable state
//! file only costs the display.

use std::ffi::c_void;
use std::sync::Mutex;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

use camino::Utf8PathBuf;
use mod_loader::lua::{Event, Sink, Stage};
use serde::Serialize;

use crate::runner::{Outcome, Phase};

/// The state file is rewritten at most this often (but at every change of stage).
const WRITE_EVERY: std::time::Duration = std::time::Duration::from_millis(250);

/// The `progress` object of a running state file (`schema/state-eml.schema.json`).
#[derive(Clone, Debug, Serialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct Live {
    /// `starting` before the run reports, then `checking`, `backup`, `mods`, `writing`, `saving`.
    pub stage: &'static str,
    /// For a person: "Checking the game data".
    pub label: &'static str,
    /// Bytes read so far in this stage and the bytes it has announced (0: not known).
    pub done_bytes: u64,
    pub total_bytes: u64,
    pub files_done: usize,
    pub files_total: usize,
    /// The file being read now (its name).
    #[serde(skip_serializing_if = "Option::is_none")]
    pub file: Option<String>,
    /// Milliseconds the stage has lasted.
    pub stage_ms: u64,
    /// While a file is being read: milliseconds since a byte came (0 otherwise). A big number is a stuck read.
    pub quiet_ms: u64,
    /// When the run began and when this was written, in ms since 1970.
    pub started_ms: u64,
    pub updated_ms: u64,
}

fn unix_ms() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map_or(0, |d| d.as_millis() as u64)
}

// ---- the window: the kiln.progress service (native/core/abi/services/kiln_progress.h) ----

#[repr(C)]
#[derive(Clone, Copy)]
struct Str {
    ptr: *const u8,
    len: usize,
}

impl Str {
    fn of(s: &str) -> Self {
        Self { ptr: s.as_ptr(), len: s.len() }
    }
}

/// kiln_progress.h KilnProgressApi v1.
#[repr(C)]
struct Table {
    size: u32,
    version: u32,
    begin: unsafe extern "C" fn(Str) -> u64,
    update: unsafe extern "C" fn(u64, Str, u64, u64),
    end: unsafe extern "C" fn(u64),
}

/// One open display task of the service. Ended when dropped.
pub struct Window {
    table: *const Table,
    task: u64,
}

// The table is Kiln's immutable function table and the task is a plain number; the service is callable from any thread.
unsafe impl Send for Window {}

impl Window {
    /// `table`: what `api->service("kiln.progress", 1)` returned (null: the core has none). The header is checked before
    /// any slot is read. None when the service says there is no display for this process (a server, `KILN_UI_DISABLE=1`).
    ///
    /// # Safety
    /// `table` is null or points to a Kiln service table that lives for the process.
    pub unsafe fn open(table: *const c_void, title: &str) -> Option<Window> {
        if table.is_null() {
            return None;
        }
        let (size, version) = unsafe { (std::ptr::read(table.cast::<u32>()), std::ptr::read(table.cast::<u32>().add(1))) };
        if version < 1 || (size as usize) < std::mem::size_of::<Table>() {
            return None;
        }
        let table = table.cast::<Table>();
        for offset in [std::mem::offset_of!(Table, begin), std::mem::offset_of!(Table, update), std::mem::offset_of!(Table, end)] {
            if unsafe { std::ptr::read(table.cast::<u8>().add(offset).cast::<usize>()) } == 0 {
                return None;
            }
        }
        let task = unsafe { ((*table).begin)(Str::of(title)) };
        (task != 0).then_some(Window { table, task })
    }

    pub fn update(&self, text: &str, done: u64, total: u64) {
        unsafe { ((*self.table).update)(self.task, Str::of(text), done, total) };
    }
}

impl Drop for Window {
    fn drop(&mut self) {
        unsafe { ((*self.table).end)(self.task) };
    }
}

/// The line the window shows: `Checking the game data: 12.3 of 48.6 GiB (25%)`, or the stage alone.
pub fn window_text(e: &Event) -> String {
    if e.total > 0 && e.stage == Stage::Checking {
        let percent = e.percent().unwrap_or(0);
        format!("{}: {} of {} ({percent}%)", e.stage.label(), size(e.done.min(e.total)), size(e.total))
    } else {
        e.stage.label().to_owned()
    }
}

/// `12.3 GiB`, `312 B`.
fn size(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KiB", "MiB", "GiB", "TiB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 { format!("{bytes} B") } else { format!("{value:.1} {}", UNITS[unit]) }
}

// ---- the sink ----

/// What a run reports into: the state file and the window. Built by the plugin (it knows the paths and the service);
/// the runner hands it the run's mods (`set_base`) and gives it to `Progress` as its sink.
pub struct Display {
    state_dir: Option<Utf8PathBuf>,
    window: Mutex<Option<Window>>,
    inner: Mutex<Inner>,
}

struct Inner {
    /// The state as it stands (the mods), published with `phase: running` and the progress.
    base: Option<Outcome>,
    started_ms: u64,
    last_write: Option<Instant>,
    last_stage: Option<(Stage, u64)>,
}

impl std::fmt::Debug for Display {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Display").field("state_dir", &self.state_dir).finish_non_exhaustive()
    }
}

impl Display {
    /// `table`: the `kiln.progress` service table, or null. The window opens now (the service shows it only when the
    /// work lasts, and `end`s with the run).
    ///
    /// # Safety
    /// See `Window::open`.
    pub unsafe fn new(state_dir: Option<Utf8PathBuf>, table: *const c_void) -> Self {
        let window = unsafe { Window::open(table, "Preparing the game data for EML mods") };
        Self::with_window(state_dir, window)
    }

    pub fn with_window(state_dir: Option<Utf8PathBuf>, window: Option<Window>) -> Self {
        Self {
            state_dir,
            window: Mutex::new(window),
            inner: Mutex::new(Inner { base: None, started_ms: unix_ms(), last_write: None, last_stage: None }),
        }
    }

    /// The state the live file carries besides the progress (the mods found so far). Writes the file at once with the
    /// run `starting`, so that the last run's `done` is not mistaken for this one's.
    pub fn set_base(&self, out: &Outcome) {
        let mut inner = self.inner.lock().unwrap_or_else(|e| e.into_inner());
        inner.base = Some(out.clone());
        let live = Live {
            stage: "starting", label: "Starting", done_bytes: 0, total_bytes: 0, files_done: 0, files_total: 0, file: None,
            stage_ms: 0, quiet_ms: 0, started_ms: inner.started_ms, updated_ms: unix_ms(),
        };
        let json = Self::document(&inner, live);
        inner.last_write = Some(Instant::now());
        drop(inner);
        self.write(&json);
    }

    fn document(inner: &Inner, live: Live) -> Option<String> {
        let mut out = inner.base.clone()?;
        out.phase = Phase::Running;
        out.progress = Some(live);
        serde_json::to_string_pretty(&out).ok()
    }

    fn write(&self, json: &Option<String>) {
        let (Some(dir), Some(json)) = (&self.state_dir, json) else { return };
        let _ = std::fs::create_dir_all(dir);
        let tmp = dir.join("eml.json.live.tmp");
        if std::fs::write(&tmp, json).is_ok() && std::fs::rename(&tmp, dir.join("eml.json")).is_err() {
            let _ = std::fs::remove_file(&tmp);
        }
    }
}

impl Sink for Display {
    fn update(&self, e: &Event) {
        if let Some(w) = self.window.lock().unwrap_or_else(|p| p.into_inner()).as_ref() {
            w.update(&window_text(e), if e.stage == Stage::Checking { e.done.min(e.total) } else { 0 }, if e.stage == Stage::Checking { e.total } else { 0 });
        }
        let mut inner = self.inner.lock().unwrap_or_else(|p| p.into_inner());
        let new_stage = inner.last_stage != Some((e.stage, e.serial));
        if !new_stage && inner.last_write.is_some_and(|t| t.elapsed() < WRITE_EVERY) {
            return;
        }
        inner.last_stage = Some((e.stage, e.serial));
        inner.last_write = Some(Instant::now());
        let live = Live {
            stage: e.stage.as_str(),
            label: e.stage.label(),
            done_bytes: e.done,
            total_bytes: e.total,
            files_done: e.files_done,
            files_total: e.files_total,
            file: e.file.clone(),
            stage_ms: e.elapsed.as_millis() as u64,
            quiet_ms: e.quiet.as_millis() as u64,
            started_ms: inner.started_ms,
            updated_ms: unix_ms(),
        };
        let json = Self::document(&inner, live);
        drop(inner);
        self.write(&json);
    }

    fn finished(&self) {
        // the window closes now, before the plugin goes on to the game's entry point; the state file is the plugin's to
        // publish (`publish` in plugin.rs), with the result
        *self.window.lock().unwrap_or_else(|p| p.into_inner()) = None;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU64, Ordering};
    use std::time::Duration;

    static CALLS: Mutex<Vec<String>> = Mutex::new(Vec::new());
    static NEXT: AtomicU64 = AtomicU64::new(7);

    unsafe extern "C" fn begin(title: Str) -> u64 {
        let t = unsafe { std::str::from_utf8(std::slice::from_raw_parts(title.ptr, title.len)).unwrap().to_owned() };
        CALLS.lock().unwrap().push(format!("begin {t}"));
        NEXT.load(Ordering::Relaxed)
    }
    unsafe extern "C" fn update(task: u64, text: Str, done: u64, total: u64) {
        let t = unsafe { std::str::from_utf8(std::slice::from_raw_parts(text.ptr, text.len)).unwrap().to_owned() };
        CALLS.lock().unwrap().push(format!("update {task} {t} {done}/{total}"));
    }
    unsafe extern "C" fn end(task: u64) {
        CALLS.lock().unwrap().push(format!("end {task}"));
    }
    unsafe extern "C" fn begin_none(_: Str) -> u64 {
        0
    }

    fn table(begin: unsafe extern "C" fn(Str) -> u64) -> Table {
        Table { size: std::mem::size_of::<Table>() as u32, version: 1, begin, update, end }
    }

    fn event(stage: Stage, done: u64, total: u64) -> Event {
        Event { stage, serial: 1, done, total, files_done: 1, files_total: 3, file: Some("enshrouded_001.dat".into()), elapsed: Duration::from_secs(4), quiet: Duration::from_secs(2) }
    }

    fn dir(name: &str) -> Utf8PathBuf {
        let d = Utf8PathBuf::from_path_buf(std::env::temp_dir()).unwrap().join(format!("kiln-eml-live-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        d
    }

    #[test]
    fn the_window_gets_the_stage_the_bytes_and_an_end() {
        // one test owns the recorder
        CALLS.lock().unwrap().clear();
        let t = table(begin);
        let display = unsafe { Display::new(None, (&t as *const Table).cast()) };
        display.update(&event(Stage::Checking, 3 << 30, 12 << 30));
        display.update(&event(Stage::Mods, 0, 0));
        display.finished();
        let calls = CALLS.lock().unwrap().clone();
        assert_eq!(calls, [
            "begin Preparing the game data for EML mods",
            "update 7 Checking the game data: 3.0 GiB of 12.0 GiB (25%) 3221225472/12884901888",
            "update 7 Running the mods 0/0",
            "end 7",
        ]);
        // a service that has no display here (a server, KILN_UI_DISABLE): no window, no error
        let none = table(begin_none);
        assert!(unsafe { Window::open((&none as *const Table).cast(), "t") }.is_none());
        assert!(unsafe { Window::open(std::ptr::null(), "t") }.is_none());
        // a table too short to hold the slots, or an unknown version, is not touched
        let short = [4u32, 1];
        assert!(unsafe { Window::open(short.as_ptr().cast(), "t") }.is_none());
        let future = Table { version: 0, ..table(begin) };
        assert!(unsafe { Window::open((&future as *const Table).cast(), "t") }.is_none());
    }

    #[test]
    fn the_state_file_says_running_with_the_progress_and_is_replaced_whole() {
        let d = dir("state");
        let display = Display::with_window(Some(d.clone()), None);
        let mut base = Outcome::new(Phase::Done, Vec::new());
        base.file = Some("enshrouded".into());
        display.set_base(&base);
        let read = || -> serde_json::Value { serde_json::from_str(&std::fs::read_to_string(d.join("eml.json")).unwrap()).unwrap() };
        let first = read();
        assert_eq!((first["phase"].as_str(), first["progress"]["stage"].as_str(), first["format"].as_u64()), (Some("running"), Some("starting"), Some(1)));
        assert!(first["mods"].is_array(), "a running state is still a format 1 document");

        display.update(&event(Stage::Checking, 1000, 4000));
        let v = read();
        assert_eq!(v["phase"], "running");
        assert_eq!((v["progress"]["doneBytes"].as_u64(), v["progress"]["totalBytes"].as_u64(), v["progress"]["filesTotal"].as_u64()), (Some(1000), Some(4000), Some(3)));
        assert_eq!(v["progress"]["file"], "enshrouded_001.dat");
        assert_eq!(v["progress"]["quietMs"].as_u64(), Some(2000));
        assert!(v["progress"]["startedMs"].as_u64().unwrap() > 0 && v["pid"].as_u64() == Some(std::process::id() as u64));

        // a second look within the rate limit changes nothing, a new stage is written at once
        display.update(&event(Stage::Checking, 2000, 4000));
        assert_eq!(read()["progress"]["doneBytes"].as_u64(), Some(1000));
        display.update(&event(Stage::Mods, 0, 0));
        assert_eq!(read()["progress"]["stage"], "mods");
        assert_eq!(std::fs::read_dir(&d).unwrap().count(), 1, "no temp file is left behind");
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn what_the_window_says() {
        assert_eq!(window_text(&event(Stage::Checking, 0, 0)), "Checking the game data");
        assert_eq!(window_text(&event(Stage::Checking, 5 << 20, 10 << 20)), "Checking the game data: 5.0 MiB of 10.0 MiB (50%)");
        assert_eq!(window_text(&event(Stage::Writing, 5, 10)), "Writing the patched game data");
    }
}
