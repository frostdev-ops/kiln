// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: what a run is doing, for the people waiting for it. EML 0.2.0 says nothing between "Running lua" and the
// end of its game-data phase, which on a client with 48 GiB of data files took minutes (GitHub bisdat/kiln-mods#14, #17;
// a Steam Deck player saw nothing and gave up).
//
// The run reports into a `Progress`: the stage it is in and, while it reads game files, which file and how many bytes
// of the total are done. The counters are atomics, so the read loop pays an add per megabyte. A ticker thread (the
// only one; it lives as long as the `ProgressGuard`) does everything slow: it
//   - logs a line every few seconds while a stage lasts, with the file, the share done and the rate, so a reader of the
//     log can tell "reading, 61% done" from "hung";
//   - says so when a read has moved no byte for a while ("still waiting for the file system");
//   - hands each change to the host's `Sink` (kiln-eml: the state file and the progress window), a few times a second.
// A run without a guard (`Progress::none`) only counts.

use std::{
    sync::{Arc, Condvar, Mutex, MutexGuard, atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering}},
    thread::JoinHandle,
    time::{Duration, Instant},
};

use crate::log::{info, warn};

/// What the run is doing. Serialized by the host in the state file as `stage`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Stage {
    /// Fingerprinting the game's files: the change cache and the input checks.
    Checking,
    /// Preparing the vanilla data: the type registry from the exe (once per game version) and the backup of the header and
    /// the resource file (a copy, and its seal).
    Prepare,
    /// The mods' Lua.
    Mods,
    /// Writing and publishing the patched game data.
    Writing,
    /// Saving the change cache.
    Saving,
}

impl Stage {
    pub fn as_str(self) -> &'static str {
        match self {
            Stage::Checking => "checking",
            Stage::Prepare => "prepare",
            Stage::Mods => "mods",
            Stage::Writing => "writing",
            Stage::Saving => "saving",
        }
    }

    /// For a person: "Reading the game data".
    pub fn label(self) -> &'static str {
        match self {
            Stage::Checking => "Checking the game data",
            Stage::Prepare => "Preparing the vanilla game data",
            Stage::Mods => "Running the mods",
            Stage::Writing => "Writing the patched game data",
            Stage::Saving => "Saving the change cache",
        }
    }
}

/// A snapshot for a `Sink`.
#[derive(Clone, Debug)]
pub struct Event {
    pub stage: Stage,
    /// Counts the `stage()` calls: a stage that begins again (the same one twice in a row) is another step.
    pub serial: u64,
    /// Bytes read so far in this stage, and the bytes the files announced for it add up to (0: nothing announced).
    pub done: u64,
    pub total: u64,
    pub files_done: usize,
    pub files_total: usize,
    /// The file being read right now (its name, not its path).
    pub file: Option<String>,
    /// Since the stage began.
    pub elapsed: Duration,
    /// While a file is being read: how long no byte came. Zero otherwise.
    pub quiet: Duration,
}

impl Event {
    fn same_as(&self, other: &Event) -> bool {
        self.stage == other.stage && self.serial == other.serial && self.done == other.done && self.total == other.total
            && self.files_done == other.files_done && self.files_total == other.files_total && self.file == other.file
    }

    /// `61%`, or None when no total is known.
    pub fn percent(&self) -> Option<u64> {
        (self.total > 0).then(|| (self.done.min(self.total) * 100) / self.total)
    }
}

/// The host's receiver (kiln-eml: the state file and the window). Called from the ticker thread only.
pub trait Sink: Send + Sync {
    /// Something changed. Rate limiting is the sink's business (the ticker calls a few times a second at most).
    fn update(&self, event: &Event);
    /// The run is over (the guard was dropped): close what `update` opened.
    fn finished(&self) {}
}

struct State {
    stage: Stage,
    since: Instant,
    file: Option<String>,
    /// A stage was started: nothing is reported before.
    active: bool,
}

struct Shared {
    start: Instant,
    state: Mutex<State>,
    serial: AtomicU64,
    done: AtomicU64,
    total: AtomicU64,
    files_done: AtomicUsize,
    files_total: AtomicUsize,
    /// A read is in flight (between `begin_file` and `end_file`): only then can it stall.
    reading: AtomicBool,
    /// Milliseconds since `start` at which a byte was last read, a file began or a stage changed.
    moved: AtomicU64,
    sink: Option<Arc<dyn Sink>>,
    stop: Mutex<bool>,
    wake: Condvar,
    timing: Timing,
}

#[derive(Clone, Copy, Debug)]
pub struct Timing {
    /// Between two looks of the ticker.
    pub tick: Duration,
    /// Between two progress lines of a stage in the log.
    pub log_every: Duration,
    /// No byte for this long: a warning, then again every `stall_every`.
    pub stall: Duration,
    pub stall_every: Duration,
    /// An unchanged state is handed to the sink again after this long, so that what it writes keeps a fresh time.
    pub heartbeat: Duration,
}

impl Default for Timing {
    fn default() -> Self {
        Self {
            tick: Duration::from_millis(100),
            log_every: Duration::from_secs(5),
            stall: Duration::from_secs(15),
            stall_every: Duration::from_secs(30),
            heartbeat: Duration::from_secs(1),
        }
    }
}

/// The counters a run reports into. Cheap to clone; every clone is the same run.
#[derive(Clone)]
pub struct Progress(Arc<Shared>);

/// Owns the ticker thread: dropping it stops the thread, tells the sink the run is over and leaves the counters usable.
pub struct ProgressGuard {
    progress: Progress,
    ticker: Option<JoinHandle<()>>,
}

impl std::ops::Deref for ProgressGuard {
    type Target = Progress;
    fn deref(&self) -> &Progress {
        &self.progress
    }
}

impl Drop for ProgressGuard {
    fn drop(&mut self) {
        *lock(&self.progress.0.stop) = true;
        self.progress.0.wake.notify_all();
        if let Some(t) = self.ticker.take() {
            let _ = t.join();
        }
        if let Some(sink) = &self.progress.0.sink {
            sink.finished();
        }
    }
}

fn lock<T>(m: &Mutex<T>) -> MutexGuard<'_, T> {
    m.lock().unwrap_or_else(|e| e.into_inner())
}

impl Progress {
    fn build(sink: Option<Arc<dyn Sink>>, timing: Timing) -> Self {
        Progress(Arc::new(Shared {
            start: Instant::now(),
            state: Mutex::new(State { stage: Stage::Checking, since: Instant::now(), file: None, active: false }),
            serial: AtomicU64::new(0),
            done: AtomicU64::new(0),
            total: AtomicU64::new(0),
            files_done: AtomicUsize::new(0),
            files_total: AtomicUsize::new(0),
            reading: AtomicBool::new(false),
            moved: AtomicU64::new(0),
            sink,
            stop: Mutex::new(false),
            wake: Condvar::new(),
            timing,
        }))
    }

    /// Counts only: no thread, no log lines, no sink.
    pub fn none() -> Self {
        Self::build(None, Timing::default())
    }

    /// Counts, logs progress lines and reports to `sink` (when given) from a ticker thread.
    pub fn start(sink: Option<Arc<dyn Sink>>) -> ProgressGuard {
        Self::start_with(sink, Timing::default())
    }

    pub fn start_with(sink: Option<Arc<dyn Sink>>, timing: Timing) -> ProgressGuard {
        let progress = Self::build(sink, timing);
        let shared = progress.0.clone();
        let ticker = std::thread::Builder::new()
            .name("kiln-eml-progress".into())
            .spawn(move || tick_loop(&shared))
            .ok();
        ProgressGuard { progress, ticker }
    }

    fn now_ms(&self) -> u64 {
        self.0.start.elapsed().as_millis() as u64
    }

    /// A new stage: the counters start again.
    pub fn stage(&self, stage: Stage) {
        let s = &self.0;
        let mut st = lock(&s.state);
        st.stage = stage;
        st.since = Instant::now();
        st.file = None;
        st.active = true;
        s.serial.fetch_add(1, Ordering::Relaxed);
        s.done.store(0, Ordering::Relaxed);
        s.total.store(0, Ordering::Relaxed);
        s.files_done.store(0, Ordering::Relaxed);
        s.files_total.store(0, Ordering::Relaxed);
        s.reading.store(false, Ordering::Relaxed);
        s.moved.store(self.now_ms(), Ordering::Relaxed);
    }

    /// `files` files and `bytes` bytes are about to be read in the current stage (adds to what was announced).
    pub fn announce(&self, files: usize, bytes: u64) {
        self.0.files_total.fetch_add(files, Ordering::Relaxed);
        self.0.total.fetch_add(bytes, Ordering::Relaxed);
    }

    pub fn begin_file(&self, name: &str) {
        lock(&self.0.state).file = Some(name.to_owned());
        self.0.reading.store(true, Ordering::Relaxed);
        self.0.moved.store(self.now_ms(), Ordering::Relaxed);
    }

    /// `bytes` more bytes of the current file were read.
    pub fn add(&self, bytes: u64) {
        self.0.done.fetch_add(bytes, Ordering::Relaxed);
        self.0.moved.store(self.now_ms(), Ordering::Relaxed);
    }

    pub fn end_file(&self) {
        self.0.reading.store(false, Ordering::Relaxed);
        self.0.files_done.fetch_add(1, Ordering::Relaxed);
        self.0.moved.store(self.now_ms(), Ordering::Relaxed);
        lock(&self.0.state).file = None;
    }

    /// The current counters (None before the first stage).
    pub fn snapshot(&self) -> Option<Event> {
        let s = &self.0;
        let st = lock(&s.state);
        let quiet = if s.reading.load(Ordering::Relaxed) { Duration::from_millis(self.now_ms().saturating_sub(s.moved.load(Ordering::Relaxed))) } else { Duration::ZERO };
        st.active.then(|| Event {
            stage: st.stage,
            serial: s.serial.load(Ordering::Relaxed),
            done: s.done.load(Ordering::Relaxed),
            total: s.total.load(Ordering::Relaxed),
            files_done: s.files_done.load(Ordering::Relaxed),
            files_total: s.files_total.load(Ordering::Relaxed),
            file: st.file.clone(),
            elapsed: st.since.elapsed(),
            quiet,
        })
    }
}

fn tick_loop(shared: &Arc<Shared>) {
    let progress = Progress(shared.clone());
    let timing = shared.timing;
    let mut last_sent: Option<Event> = None;
    let mut sent_at = Instant::now();
    let mut log = LogState::default();
    loop {
        {
            let stop = lock(&shared.stop);
            if *stop {
                return;
            }
            let (stop, _) = shared.wake.wait_timeout(stop, timing.tick).unwrap_or_else(|e| e.into_inner());
            if *stop {
                return;
            }
        }
        let Some(event) = progress.snapshot() else { continue };
        if let Some(sink) = &shared.sink
            && (last_sent.as_ref().is_none_or(|last| !last.same_as(&event)) || sent_at.elapsed() >= timing.heartbeat)
        {
            sink.update(&event);
            sent_at = Instant::now();
            last_sent = Some(event.clone());
        }
        log.look(&event, shared.reading.load(Ordering::Relaxed), shared.moved.load(Ordering::Relaxed), progress.now_ms(), &timing);
    }
}

/// When the ticker last said something, so each stage's lines come at a sane rate.
#[derive(Default)]
struct LogState {
    stage: Option<(Stage, u64)>,
    last_line: Option<Instant>,
    done_at_line: u64,
    last_stall: Option<Instant>,
}

impl LogState {
    fn look(&mut self, event: &Event, reading: bool, moved_ms: u64, now_ms: u64, timing: &Timing) {
        let now = Instant::now();
        if self.stage != Some((event.stage, event.serial)) {
            *self = LogState { stage: Some((event.stage, event.serial)), last_line: Some(now), done_at_line: event.done, ..Default::default() };
        }
        let quiet = Duration::from_millis(now_ms.saturating_sub(moved_ms));
        if reading && quiet >= timing.stall && self.last_stall.is_none_or(|t| t.elapsed() >= timing.stall_every) {
            self.last_stall = Some(now);
            warn!(
                "No data read for {} s: still waiting on {} ({} of {} read in this step, {} file(s) done)",
                quiet.as_secs(), event.file.as_deref().unwrap_or("a file"), human(event.done), human(event.total), event.files_done
            );
        }
        let Some(last) = self.last_line else { return };
        if now.duration_since(last) < timing.log_every {
            return;
        }
        self.last_line = Some(now);
        let seconds = now.duration_since(last).as_secs_f64().max(0.001);
        let rate = event.done.saturating_sub(self.done_at_line) as f64 / seconds;
        self.done_at_line = event.done;
        if event.total > 0 && (event.done > 0 || reading) {
            info!(
                "{}: {}% ({} of {}), {}/s{}",
                event.stage.label(), event.percent().unwrap_or(0), human(event.done), human(event.total), human(rate as u64),
                event.file.as_deref().map(|f| format!(", now {f}")).unwrap_or_default()
            );
        } else if event.stage != Stage::Checking {
            info!("{}: still going, {} s so far", event.stage.label(), event.elapsed.as_secs());
        }
    }
}

/// `1.5 GiB`, `312 B`.
pub(crate) fn human(bytes: u64) -> String {
    const UNITS: [&str; 5] = ["B", "KiB", "MiB", "GiB", "TiB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 { format!("{bytes} B") } else { format!("{value:.1} {}", UNITS[unit]) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[derive(Default)]
    struct Collect(Mutex<Vec<Event>>, AtomicBool);
    impl Sink for Collect {
        fn update(&self, event: &Event) {
            lock(&self.0).push(event.clone());
        }
        fn finished(&self) {
            self.1.store(true, Ordering::Relaxed);
        }
    }

    fn fast() -> Timing {
        Timing { tick: Duration::from_millis(5), log_every: Duration::from_secs(3600), stall: Duration::from_secs(3600), stall_every: Duration::from_secs(3600), heartbeat: Duration::from_secs(3600) }
    }

    #[test]
    fn the_sink_sees_the_stage_and_the_bytes_and_is_told_when_the_run_ends() {
        let sink = Arc::new(Collect::default());
        {
            let guard = Progress::start_with(Some(sink.clone()), fast());
            assert!(guard.snapshot().is_none(), "nothing is reported before a stage starts");
            guard.stage(Stage::Checking);
            guard.announce(2, 3000);
            guard.begin_file("a.dat");
            guard.add(1000);
            std::thread::sleep(Duration::from_millis(60));
            guard.add(2000);
            std::thread::sleep(Duration::from_millis(60));
            guard.end_file();
            guard.stage(Stage::Mods);
            std::thread::sleep(Duration::from_millis(60));
        }
        let seen = lock(&sink.0).clone();
        assert!(sink.1.load(Ordering::Relaxed), "finished() ran");
        let checking: Vec<_> = seen.iter().filter(|e| e.stage == Stage::Checking).collect();
        assert!(checking.iter().any(|e| e.done == 1000 && e.total == 3000 && e.file.as_deref() == Some("a.dat")), "{seen:?}");
        assert_eq!(checking.last().unwrap().percent(), Some(100));
        let mods = seen.iter().rfind(|e| e.stage == Stage::Mods).expect("the next stage is reported");
        assert_eq!((mods.done, mods.total, mods.file.as_deref()), (0, 0, None), "the counters start again");
        // an unchanged state is reported once, not at every tick
        assert!(seen.windows(2).all(|w| !w[0].same_as(&w[1])), "{seen:?}");
    }

    #[test]
    fn an_unchanged_state_is_handed_over_again_as_a_heartbeat_and_a_silent_read_says_how_long() {
        let sink = Arc::new(Collect::default());
        {
            let timing = Timing { heartbeat: Duration::from_millis(20), ..fast() };
            let guard = Progress::start_with(Some(sink.clone()), timing);
            guard.stage(Stage::Checking);
            guard.announce(1, 100);
            guard.begin_file("a.dat");
            std::thread::sleep(Duration::from_millis(150));
        }
        let seen = lock(&sink.0).clone();
        assert!(seen.len() >= 3, "the state did not change, yet it was handed over again: {}", seen.len());
        assert!(seen.last().unwrap().quiet >= Duration::from_millis(60), "a read that moved no byte says for how long: {:?}", seen.last().unwrap().quiet);
        assert!(seen.first().unwrap().quiet < seen.last().unwrap().quiet);
    }

    #[test]
    fn a_run_without_a_guard_only_counts() {
        let p = Progress::none();
        p.stage(Stage::Checking);
        p.announce(1, 10);
        p.begin_file("x");
        p.add(4);
        let e = p.snapshot().unwrap();
        assert_eq!((e.done, e.total, e.files_total), (4, 10, 1));
        p.end_file();
        assert_eq!(p.snapshot().unwrap().files_done, 1);
    }

    #[test]
    fn a_read_that_moves_no_byte_is_said_to_stall_and_a_moving_one_is_not() {
        let timing = Timing { tick: Duration::from_millis(5), log_every: Duration::from_secs(3600), stall: Duration::from_millis(40), stall_every: Duration::from_secs(3600), heartbeat: Duration::from_secs(3600) };
        let mut state = LogState::default();
        let event = Event { stage: Stage::Checking, serial: 1, done: 10, total: 100, files_done: 0, files_total: 1, file: Some("a.dat".into()), elapsed: Duration::ZERO, quiet: Duration::ZERO };
        // not reading: nothing can stall; reading and quiet for less than the limit: neither
        state.look(&event, false, 0, 1000, &timing);
        assert!(state.last_stall.is_none());
        state.look(&event, true, 990, 1000, &timing);
        assert!(state.last_stall.is_none());
        state.look(&event, true, 900, 1000, &timing);
        assert!(state.last_stall.is_some(), "100 ms without a byte while reading");
    }

    #[test]
    fn a_step_that_begins_again_starts_its_rate_from_zero() {
        let timing = Timing { tick: Duration::from_millis(5), log_every: Duration::ZERO, stall: Duration::from_secs(3600), stall_every: Duration::from_secs(3600), heartbeat: Duration::from_secs(3600) };
        let mut state = LogState::default();
        let at = |serial, done| Event { stage: Stage::Checking, serial, done, total: 100, files_done: 0, files_total: 1, file: None, elapsed: Duration::ZERO, quiet: Duration::ZERO };
        state.look(&at(1, 80), true, 0, 0, &timing);
        state.look(&at(1, 90), true, 0, 0, &timing);
        assert_eq!(state.done_at_line, 90);
        // the same stage again (a later barrier): counters were reset, so the old figure must not be subtracted
        state.look(&at(2, 10), true, 0, 0, &timing);
        assert_eq!(state.done_at_line, 10, "a new step restarts: {:?}", state.stage);
        assert_eq!(state.stage, Some((Stage::Checking, 2)));
    }

    #[test]
    fn sizes_read_like_the_logs_say_them() {
        assert_eq!(human(312), "312 B");
        assert_eq!(human(1536), "1.5 KiB");
        assert_eq!(human(48 * (1 << 30)), "48.0 GiB");
    }
}
