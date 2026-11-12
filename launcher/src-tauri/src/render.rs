//! How the window is drawn on Linux: with the GPU (WebKitGTK's default) or without it ("software").
//!
//! A player on an NVIDIA / X11 machine saw only a black window (0.2.0-dev.23): the webview never painted the page, and
//! nothing said so. So:
//! - the page says it started: `app_started` after React's first commit and two frames (launcher/src/shell/started.ts);
//! - software mode: before GTK starts, [`SOFTWARE_ENV`] (a value the user set stays); the webview's hardware
//!   acceleration policy Never and no WebGL (the page draws the CSS mouth instead of the fire: `app_info.noWebgl`);
//! - when the page has not said so [`TIMEOUT`] after the window was made, a start that drew with the GPU by default
//!   writes the marker (`<data>/state/software-render.json`, [`marker`]), logs why and starts itself again in software
//!   mode (`$APPIMAGE` when set, else this executable, with the same arguments). A start in software mode, or with a
//!   mode the user forced, only says so loudly: at most one restart, never a loop;
//! - `KILN_LAUNCHER_RENDER=software|gpu` and `--software-render` / `--gpu-render` win over the marker; Settings' "Draw the
//!   launcher without the GPU" sets or clears it (`app_render_set`), for the next start.
//! docs/kiln/launcher/app.md, "Drawing without the GPU".

use kiln_manage::paths::DataDirs;
use kiln_manage::Error;
use serde::{Deserialize, Serialize};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Condvar, Mutex, OnceLock};
use std::time::{Duration, Instant};

/// How long the page has, from the window's creation, to say it started. A cold start on a slow machine (spinning disk,
/// AppImage mount, WebKit's processes) takes a few seconds; the page's first screen is the splash.
pub const TIMEOUT: Duration = Duration::from_secs(15);
/// When the window is never made (GTK or WebKit hangs before the setup), the watchdog acts this long after the start.
pub const NO_WINDOW_TIMEOUT: Duration = Duration::from_secs(30);

pub const ENV: &str = "KILN_LAUNCHER_RENDER";
pub const ARG_SOFTWARE: &str = "--software-render";
pub const ARG_GPU: &str = "--gpu-render";
/// Set only for the process the fallback starts: it draws without the GPU even when the marker could not be written.
const RESTARTED: &str = "KILN_LAUNCHER_RENDER_RESTARTED";
/// The [`SOFTWARE_ENV`] names this process set itself (comma-separated), so a launcher started from it (the fallback, an
/// update's restart) tells them from the user's own.
#[cfg_attr(not(target_os = "linux"), allow(dead_code))]
const SET_BY_US: &str = "KILN_LAUNCHER_RENDER_SET";
/// Debug and QA builds (`dev-eval`) only: `gpu`, `software` or `all`: `app_started` is ignored in that mode, so the
/// fallback can be tested on a machine where the page renders.
#[cfg(any(debug_assertions, feature = "dev-eval"))]
const TEST_WITHHOLD: &str = "KILN_LAUNCHER_TEST_WITHHOLD_READY";

/// What software mode sets before GTK starts.
pub const SOFTWARE_ENV: [(&str, &str); 3] =
    [("WEBKIT_DISABLE_COMPOSITING_MODE", "1"), ("WEBKIT_SKIA_ENABLE_CPU_RENDERING", "1"), ("LIBGL_ALWAYS_SOFTWARE", "1")];

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Mode {
    Gpu,
    Software,
}

/// Why a start draws the way it does.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Why {
    /// Nothing asked: the GPU.
    Default,
    /// The marker of an earlier start (the fallback, or Settings).
    Marker,
    /// This process is the fallback's restart.
    Restart,
    /// `KILN_LAUNCHER_RENDER`.
    Environment,
    /// `--software-render` / `--gpu-render`.
    Argument,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Choice {
    pub mode: Mode,
    pub why: Why,
    /// A `KILN_LAUNCHER_RENDER` value that is neither mode (said in the log).
    pub ignored: Option<String>,
}

/// What a start does when the page has not said it started in time.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Late {
    /// Write the marker and start again without the GPU.
    Restart,
    /// Say so loudly and go on (software mode already, or a mode the user forced).
    Report,
}

/// The mode for this start. Precedence: the arguments (the last of `--software-render` / `--gpu-render`), then
/// `KILN_LAUNCHER_RENDER`, then the fallback's restart, then the marker, then the GPU.
pub fn decide(args: &[String], env: Option<&str>, restarted: bool, marker: bool) -> Choice {
    let arg = args.iter().skip(1).rev().find_map(|a| match a.as_str() {
        ARG_SOFTWARE => Some(Mode::Software),
        ARG_GPU => Some(Mode::Gpu),
        _ => None,
    });
    let mut ignored = None;
    let env = match env.map(|v| v.trim().to_ascii_lowercase()) {
        Some(v) if v == "software" => Some(Mode::Software),
        Some(v) if v == "gpu" => Some(Mode::Gpu),
        Some(v) if !v.is_empty() => {
            ignored = Some(format!("{ENV}={v} is neither software nor gpu: ignored"));
            None
        }
        _ => None,
    };
    let (mode, why) = match (arg, env) {
        (Some(m), _) => (m, Why::Argument),
        (None, Some(m)) => (m, Why::Environment),
        _ if restarted => (Mode::Software, Why::Restart),
        _ if marker => (Mode::Software, Why::Marker),
        _ => (Mode::Gpu, Why::Default),
    };
    Choice { mode, why, ignored }
}

/// Only a start that drew with the GPU because nothing asked otherwise falls back: software mode has nowhere to go
/// (no loop), and a mode the user forced is theirs.
pub fn when_late(c: &Choice) -> Late {
    if c.mode == Mode::Gpu && c.why == Why::Default { Late::Restart } else { Late::Report }
}

/// What the fallback starts: the AppImage file when `$APPIMAGE` is set (the running binary sits in the AppImage's
/// mount, which goes away with this process), else this executable. `true`: an AppImage.
pub fn restart_target(appimage: Option<OsString>, exe: Option<PathBuf>) -> Option<(PathBuf, bool)> {
    match appimage.filter(|a| !a.is_empty()) {
        Some(a) => Some((PathBuf::from(a), true)),
        None => exe.map(|e| (e, false)),
    }
}

/// The restart: `target` with this start's arguments (without argv[0]) and [`RESTARTED`]. An AppImage is a program of
/// the host: it gets `vars` (this process's environment) without the running image's entries, as a fresh start does.
pub fn restart_command<I>(target: &Path, appimage: bool, args: &[OsString], vars: I) -> Command
where
    I: IntoIterator<Item = (OsString, OsString)>,
{
    let mut cmd = Command::new(target);
    if appimage {
        kiln_manage::hostenv::clean_with(&mut cmd, vars);
    }
    cmd.args(args).env(RESTARTED, "1");
    cmd
}

/// Which of [`SOFTWARE_ENV`] software mode sets: those the environment (`get`) does not have.
pub fn software_env(get: impl Fn(&str) -> Option<OsString>) -> Vec<(&'static str, &'static str)> {
    SOFTWARE_ENV.iter().copied().filter(|(k, _)| get(k).is_none()).collect()
}

/// The names in a [`SET_BY_US`] value that are ours to take back (only [`SOFTWARE_ENV`] names, never anything else).
pub fn set_by_us(list: Option<&str>) -> Vec<&'static str> {
    let list = list.unwrap_or_default();
    SOFTWARE_ENV.iter().map(|(k, _)| *k).filter(|k| list.split(',').any(|x| x.trim() == *k)).collect()
}

// ---------------------------------------------------------------- the marker

/// `<data>/state/software-render.json`: there, a start without an override draws without the GPU.
pub fn marker(d: &DataDirs) -> PathBuf {
    d.root.join("state").join("software-render.json")
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Marker {
    format: u32,
    /// Who set it and why, in a sentence.
    why: String,
    at: String,
    version: String,
}

pub fn write_marker(d: &DataDirs, why: &str) -> Result<(), Error> {
    let m = Marker { format: 1, why: why.into(), at: kiln_manage::util::now_rfc3339(), version: env!("CARGO_PKG_VERSION").into() };
    kiln_manage::util::write_json_atomic(&marker(d), &m)?;
    Ok(())
}

pub fn clear_marker(d: &DataDirs) -> Result<(), Error> {
    match std::fs::remove_file(marker(d)) {
        Err(e) if e.kind() != std::io::ErrorKind::NotFound => Err(Error::from(e).with_detail(marker(d).display().to_string())),
        _ => Ok(()),
    }
}

/// The marker's sentence (None: no marker, or not readable as one).
fn marker_why(d: &DataDirs) -> Option<String> {
    kiln_manage::util::read_json::<Marker>(&marker(d)).ok().flatten().map(|m| format!("{} ({})", m.why, m.at))
}

// ---------------------------------------------------------------- the watchdog

/// The page's ready call and the window's creation, as the watchdog sees them.
pub struct Watch {
    state: Mutex<WatchState>,
    cv: Condvar,
}

#[derive(Default)]
struct WatchState {
    started: Option<Instant>,
    window: Option<Instant>,
}

/// What the watchdog saw.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Seen {
    /// The page said it started, this long after the window was made (or after the watchdog began, without one).
    Started(Duration),
    /// Time ran out; `window`: the window had been made.
    Late { window: bool },
}

impl Watch {
    pub const fn new() -> Watch {
        Watch { state: Mutex::new(WatchState { started: None, window: None }), cv: Condvar::new() }
    }
    pub fn window_created(&self) {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        s.window.get_or_insert_with(Instant::now);
        self.cv.notify_all();
    }
    /// The page's ready call; true the first time.
    pub fn page_started(&self) -> bool {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        let first = s.started.is_none();
        s.started.get_or_insert_with(Instant::now);
        self.cv.notify_all();
        first
    }
    /// Waits for the ready call: `timeout` from the window's creation, or `no_window` from `begun` while there is none.
    pub fn wait(&self, begun: Instant, timeout: Duration, no_window: Duration) -> Seen {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(t) = s.started {
                return Seen::Started(t.saturating_duration_since(s.window.unwrap_or(begun)));
            }
            let due = match s.window { Some(w) => w + timeout, None => begun + no_window };
            let now = Instant::now();
            if now >= due {
                return Seen::Late { window: s.window.is_some() };
            }
            s = self.cv.wait_timeout(s, due - now).unwrap_or_else(|e| e.into_inner()).0;
        }
    }
    /// After [`Seen::Late`]: waits (without end) for a late ready call and how long after the window it came.
    #[cfg_attr(not(target_os = "linux"), allow(dead_code))]
    fn wait_late(&self, begun: Instant) -> Duration {
        let mut s = self.state.lock().unwrap_or_else(|e| e.into_inner());
        loop {
            if let Some(t) = s.started {
                return t.saturating_duration_since(s.window.unwrap_or(begun));
            }
            s = self.cv.wait(s).unwrap_or_else(|e| e.into_inner());
        }
    }
}

impl Default for Watch {
    fn default() -> Self { Watch::new() }
}

static WATCH: Watch = Watch::new();
static CHOICE: OnceLock<Choice> = OnceLock::new();

/// This start's choice (Linux, after [`prepare`]).
pub fn choice() -> Option<&'static Choice> {
    CHOICE.get()
}

// ---------------------------------------------------------------- what the UI sees

/// `app_info.render` (Linux only).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RenderInfo {
    /// This start.
    pub mode: Mode,
    pub why: Why,
    /// The marker is there: a start without an override draws without the GPU.
    pub marker: bool,
    /// The marker's sentence: who set it, why, when.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub marker_why: Option<String>,
}

pub fn info() -> Option<RenderInfo> {
    let c = choice()?;
    let d = crate::state::dirs().ok();
    Some(RenderInfo {
        mode: c.mode,
        why: c.why,
        marker: d.as_ref().is_some_and(|d| marker(d).is_file()),
        marker_why: d.as_ref().and_then(marker_why),
    })
}

/// Settings, "Draw the launcher without the GPU": the marker for the next start.
pub fn set_next(software: bool) -> Result<RenderInfo, Error> {
    if choice().is_none() {
        return Err(Error::new(kiln_manage::ErrorCode::Unsupported, "Only the Linux launcher has this."));
    }
    let d = crate::state::dirs()?;
    if software {
        write_marker(&d, "Turned on in Settings")?;
    } else {
        clear_marker(&d)?;
    }
    log(&format!("Settings: the next start draws {}", if software { "without the GPU" } else { "with the GPU" }));
    info().ok_or_else(|| Error::new(kiln_manage::ErrorCode::Internal, "no render state"))
}

/// The page's ready call (`app_started`).
pub fn page_started() {
    #[cfg(any(debug_assertions, feature = "dev-eval"))]
    if let (Some(c), Ok(v)) = (choice(), std::env::var(TEST_WITHHOLD)) {
        let mode = match c.mode { Mode::Gpu => "gpu", Mode::Software => "software" };
        if v == "all" || v == mode {
            log(&format!("test: the page's ready call is withheld ({TEST_WITHHOLD}={v})"));
            return;
        }
    }
    WATCH.page_started();
}

fn log(msg: &str) {
    crate::commands::app::launcher_log("render", msg);
}

/// "drawing with the GPU: the default", for the logs and the support report.
pub fn says(c: &Choice) -> String {
    let how = match c.mode { Mode::Gpu => "with the GPU", Mode::Software => "without the GPU (software mode)" };
    let why = match c.why {
        Why::Default => "the default".to_string(),
        Why::Marker => "the marker of an earlier start".to_string(),
        Why::Restart => "restarted after the page did not start with the GPU".to_string(),
        Why::Environment => format!("{ENV} is set"),
        Why::Argument => format!("{} was given", if c.mode == Mode::Software { ARG_SOFTWARE } else { ARG_GPU }),
    };
    format!("drawing {how}: {why}")
}

// ---------------------------------------------------------------- Linux: before GTK, and at the setup

/// Linux, in `main` before any thread exists and before Tauri: decides the mode, sets software mode's variables,
/// prints the log's path and the mode to stderr, and starts the watchdog.
#[cfg(target_os = "linux")]
pub fn prepare(args: &[String]) {
    // a launcher started from another Kiln (the fallback, an update's restart): its variables are not the user's
    let ours = set_by_us(std::env::var(SET_BY_US).ok().as_deref());
    let restarted = std::env::var_os(RESTARTED).is_some();
    // SAFETY: main, before any thread exists (the callers' contract).
    unsafe {
        for k in &ours {
            std::env::remove_var(k);
        }
        std::env::remove_var(SET_BY_US);
        std::env::remove_var(RESTARTED);
    }
    let dirs = crate::state::dirs().ok();
    let has_marker = dirs.as_ref().is_some_and(|d| marker(d).is_file());
    let c = decide(args, std::env::var(ENV).ok().as_deref(), restarted, has_marker);
    let mut set = Vec::new();
    if c.mode == Mode::Software {
        for (k, v) in software_env(|k| std::env::var_os(k)) {
            // SAFETY: as above.
            unsafe { std::env::set_var(k, v) };
            set.push(k);
        }
        if !set.is_empty() {
            // SAFETY: as above.
            unsafe { std::env::set_var(SET_BY_US, set.join(",")) };
        }
    }
    let log_path = dirs.as_ref().map(|d| d.logs().join("launcher.log").display().to_string()).unwrap_or_else(|| "(no data folder)".into());
    let hint = match c.mode { Mode::Gpu => format!("; a black window: {ARG_SOFTWARE}"), Mode::Software => String::new() };
    eprintln!("Kiln Launcher: log {log_path}; {}{hint}", says(&c));
    let mut line = says(&c);
    if c.why == Why::Marker || c.why == Why::Restart {
        if let Some(w) = dirs.as_ref().and_then(marker_why) {
            line += &format!(" ({}: {w})", marker(dirs.as_ref().unwrap()).display());
        }
    }
    if let Some(i) = &c.ignored {
        line += &format!("; {i}");
    }
    if c.mode == Mode::Software {
        let vars: Vec<String> = SOFTWARE_ENV.iter().map(|(k, _)| {
            let v = std::env::var(k).unwrap_or_default();
            format!("{k}={v}{}", if set.contains(k) { "" } else { " (yours)" })
        }).collect();
        line += &format!("; {}", vars.join(", "));
    }
    log(&line);
    let _ = CHOICE.set(c.clone());
    let begun = Instant::now();
    std::thread::Builder::new().name("kiln-render-watch".into()).spawn(move || watchdog(c, begun)).ok();
}

#[cfg(not(target_os = "linux"))]
pub fn prepare(_args: &[String]) {}

/// The watchdog: the page's ready call in time, or the fallback.
#[cfg(target_os = "linux")]
fn watchdog(c: Choice, begun: Instant) {
    let window = match WATCH.wait(begun, TIMEOUT, NO_WINDOW_TIMEOUT) {
        Seen::Started(after) => {
            log(&format!("the page started {} ms after the window was made ({:?})", after.as_millis(), c.mode).to_lowercase());
            return;
        }
        Seen::Late { window } => window,
    };
    let what = if window { format!("the page did not start within {} s of the window", TIMEOUT.as_secs()) } else { format!("no window within {} s", NO_WINDOW_TIMEOUT.as_secs()) };
    match when_late(&c) {
        Late::Restart => {
            let why = format!("The launcher turned this on by itself: {what} with the GPU");
            let wrote = crate::state::dirs().and_then(|d| write_marker(&d, &why).map(|_| marker(&d)));
            match &wrote {
                Ok(p) => log(&format!("{what} ({}); wrote {}; starting again without the GPU", says(&c), p.display())),
                Err(e) => log(&format!("{what} ({}); could not write the marker ({}); starting again without the GPU", says(&c), e.message)),
            }
            let err = restart();
            log(&format!("PROBLEM: could not start again without the GPU: {err}"));
            eprintln!("Kiln Launcher: {what}; could not start again without the GPU: {err}. Start it with {ARG_SOFTWARE}.");
        }
        Late::Report => {
            let next = match c.mode {
                Mode::Software => "not starting again (once is enough). Send launcher.log and a support report".to_string(),
                Mode::Gpu => format!("not falling back to software mode: the GPU was asked for ({}). Try {ARG_SOFTWARE}", says(&c)),
            };
            log(&format!("PROBLEM: {what} ({}); {next}", says(&c)));
            eprintln!("Kiln Launcher: PROBLEM: {what} ({}); {next}.", says(&c));
        }
    }
    let after = WATCH.wait_late(begun);
    log(&format!("the page started late, {} ms after the window was made", after.as_millis()));
}

/// Replaces this process with a new start in software mode; returns only when that failed.
#[cfg(target_os = "linux")]
fn restart() -> std::io::Error {
    use std::os::unix::process::CommandExt;
    let Some((target, appimage)) = restart_target(std::env::var_os("APPIMAGE"), std::env::current_exe().ok()) else {
        return std::io::Error::other("neither $APPIMAGE nor this executable's path is known");
    };
    let args: Vec<OsString> = std::env::args_os().skip(1).collect();
    log(&format!("restart: {}{}", target.display(), if appimage { " (the AppImage)" } else { "" }));
    // exec: the same process (a terminal keeps waiting for it), and the instance lock goes with the old image
    restart_command(&target, appimage, &args, std::env::vars_os()).exec()
}

/// Linux, in Tauri's setup (the window exists): the watchdog's clock, the webview's acceleration policy in software
/// mode, the web process's end, and the facts a black window needs in launcher.log.
#[cfg(target_os = "linux")]
pub fn window_created(app: &tauri::AppHandle) {
    use tauri::Manager;
    WATCH.window_created();
    let Some(c) = choice().cloned() else { return };
    let webkit = {
        use webkit2gtk::ffi;
        // SAFETY: plain getters of the loaded library's version.
        unsafe { format!("{}.{}.{}", ffi::webkit_get_major_version(), ffi::webkit_get_minor_version(), ffi::webkit_get_micro_version()) }
    };
    let var = |k: &str| match std::env::var_os(k) { Some(v) if !v.is_empty() => "set", Some(_) => "empty", None => "not set" };
    let nvidia = std::fs::read_to_string("/proc/driver/nvidia/version").ok().as_deref().and_then(nvidia_line)
        .map_or("no /proc/driver/nvidia/version".to_string(), |l| format!("/proc/driver/nvidia/version: {l}"));
    log(&format!(
        "WebKitGTK {webkit}; XDG_SESSION_TYPE={}, DISPLAY {}, WAYLAND_DISPLAY {}; {nvidia}; WEBKIT_DISABLE_DMABUF_RENDERER={}",
        std::env::var("XDG_SESSION_TYPE").unwrap_or_else(|_| "(not set)".into()), var("DISPLAY"), var("WAYLAND_DISPLAY"),
        std::env::var("WEBKIT_DISABLE_DMABUF_RENDERER").unwrap_or_default()
    ));
    let Some(w) = app.get_webview_window("main") else { return };
    let software = c.mode == Mode::Software;
    let _ = w.with_webview(move |pw| {
        use webkit2gtk::{HardwareAccelerationPolicy, SettingsExt, WebViewExt};
        let wv = pw.inner();
        if software && let Some(s) = WebViewExt::settings(&wv) {
            s.set_hardware_acceleration_policy(HardwareAccelerationPolicy::Never);
            // WebKitGTK 2.54 still offers WebGL without compositing (through EGL: the GPU's driver); the page then
            // draws its CSS mouth (app_info.noWebgl), the admin map says WebGL2 is missing
            s.set_enable_webgl(false);
            log(&format!("the webview's hardware acceleration policy: {:?}, WebGL: {}", s.hardware_acceleration_policy(),
                if s.enables_webgl() { "on" } else { "off" }));
        }
        wv.connect_web_process_terminated(|_, reason| log(&format!("PROBLEM: the page's web process ended ({reason:?})")));
    });
}

#[cfg(not(target_os = "linux"))]
pub fn window_created(_app: &tauri::AppHandle) {}

/// The first line of `/proc/driver/nvidia/version`, without the driver build's `(user@host)` (a module built on the
/// player's machine names it; launcher.log goes into support reports).
pub fn nvidia_line(text: &str) -> Option<String> {
    let l = text.lines().next()?;
    Some(l.split_whitespace().filter(|w| !w.contains('@')).collect::<Vec<_>>().join(" "))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn args(tail: &[&str]) -> Vec<String> {
        std::iter::once("kiln-launcher").chain(tail.iter().copied()).map(str::to_string).collect()
    }
    fn c(mode: Mode, why: Why) -> Choice {
        Choice { mode, why, ignored: None }
    }

    #[test]
    fn nothing_asked_draws_with_the_gpu_and_may_fall_back() {
        let first = decide(&args(&[]), None, false, false);
        assert_eq!(first, c(Mode::Gpu, Why::Default));
        assert_eq!(when_late(&first), Late::Restart);
        // other arguments are not render arguments
        assert_eq!(decide(&args(&["--deck", "--software-renderer"]), None, false, false), c(Mode::Gpu, Why::Default));
    }

    #[test]
    fn a_marker_from_an_earlier_start_draws_without_the_gpu_from_the_start() {
        let m = decide(&args(&[]), None, false, true);
        assert_eq!(m, c(Mode::Software, Why::Marker));
        assert_eq!(when_late(&m), Late::Report, "software mode never restarts");
    }

    #[test]
    fn the_environment_and_the_arguments_win_over_the_marker() {
        // the environment over the marker
        let e = decide(&args(&[]), Some("gpu"), false, true);
        assert_eq!(e, c(Mode::Gpu, Why::Environment));
        assert_eq!(when_late(&e), Late::Report, "a GPU the user asked for is not switched off by the launcher");
        assert_eq!(decide(&args(&[]), Some(" Software "), false, false), c(Mode::Software, Why::Environment));
        // the arguments over the environment and the marker; the last one given wins
        assert_eq!(decide(&args(&[ARG_GPU]), Some("software"), false, true), c(Mode::Gpu, Why::Argument));
        assert_eq!(decide(&args(&[ARG_SOFTWARE]), Some("gpu"), false, false), c(Mode::Software, Why::Argument));
        assert_eq!(decide(&args(&[ARG_SOFTWARE, "--deck", ARG_GPU]), None, false, false), c(Mode::Gpu, Why::Argument));
        assert_eq!(when_late(&decide(&args(&[ARG_GPU]), None, false, false)), Late::Report);
        // the arguments and the environment also win over a restart's flag
        assert_eq!(decide(&args(&[ARG_GPU]), None, true, true), c(Mode::Gpu, Why::Argument));
        // an empty value is no value; an unknown one is said and ignored
        assert_eq!(decide(&args(&[]), Some(""), false, true), c(Mode::Software, Why::Marker));
        let odd = decide(&args(&[]), Some("fast"), false, false);
        assert_eq!((odd.mode, odd.why), (Mode::Gpu, Why::Default));
        assert!(odd.ignored.as_deref().is_some_and(|i| i.contains("KILN_LAUNCHER_RENDER=fast")), "{odd:?}");
    }

    #[test]
    fn the_fallback_restarts_once_and_never_loops() {
        let t = std::env::temp_dir().join(format!("kl-render-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&t);
        let d = DataDirs::at(&t);
        let start = |restarted: bool| decide(&args(&["--deck"]), None, restarted, marker(&d).is_file());
        // a first start draws with the GPU; the page is late: the marker, then the restart (same arguments, the flag)
        let first = start(false);
        assert_eq!(when_late(&first), Late::Restart);
        write_marker(&d, "test: the page did not start").unwrap();
        assert!(marker(&d).is_file());
        // the restart draws without the GPU and, late again, only reports
        let second = start(true);
        assert_eq!(second, c(Mode::Software, Why::Restart));
        assert_eq!(when_late(&second), Late::Report);
        // a marker that could not be written: the restart's flag alone still keeps it from looping
        let unwritten = decide(&args(&["--deck"]), None, true, false);
        assert_eq!(unwritten.mode, Mode::Software);
        assert_eq!(when_late(&unwritten), Late::Report);
        // the next start by hand: software mode from the start
        assert_eq!(start(false), c(Mode::Software, Why::Marker));
        assert!(marker_why(&d).is_some_and(|w| w.starts_with("test: the page did not start")));
        // Settings clears it: the GPU again (and a second clear is no error)
        clear_marker(&d).unwrap();
        clear_marker(&d).unwrap();
        assert_eq!(start(false), c(Mode::Gpu, Why::Default));
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn the_restart_runs_the_appimage_file_when_appimage_is_set() {
        let exe = PathBuf::from("/tmp/.mount_KilnAB12/usr/bin/kiln-launcher");
        let image = OsString::from("/games/Kiln Launcher_0.2.0-dev.24_amd64.AppImage");
        assert_eq!(restart_target(Some(image.clone()), Some(exe.clone())), Some((PathBuf::from(&image), true)));
        assert_eq!(restart_target(None, Some(exe.clone())), Some((exe.clone(), false)), "a .deb or a plain build: this executable");
        assert_eq!(restart_target(Some(OsString::new()), Some(exe.clone())), Some((exe.clone(), false)), "an empty APPIMAGE is none");
        assert_eq!(restart_target(Some(image.clone()), None), Some((PathBuf::from(&image), true)));
        assert_eq!(restart_target(None, None), None);
    }

    #[test]
    fn the_restart_keeps_the_arguments_and_starts_the_appimage_like_the_host_would() {
        let os = |s: &str| OsString::from(s);
        let mount = "/tmp/.mount_KilnAB12";
        let vars = vec![
            (os("APPDIR"), os(mount)),
            (os("APPIMAGE"), os("/games/Kiln.AppImage")),
            (os("LD_LIBRARY_PATH"), os(&format!("{mount}/usr/lib/:/opt/host/lib"))),
            (os("HOME"), os("/home/player")),
        ];
        let a = [os("--deck"), os("--nxm"), os("nxm://enshrouded/mods/1/files/2")];
        let envs = |cmd: &Command| cmd.get_envs().map(|(k, v)| (k.to_os_string(), v.map(|v| v.to_os_string()))).collect::<Vec<_>>();
        let cmd = restart_command(Path::new("/games/Kiln.AppImage"), true, &a, vars.clone());
        assert_eq!(cmd.get_program(), "/games/Kiln.AppImage");
        assert_eq!(cmd.get_args().collect::<Vec<_>>(), a.iter().map(|x| x.as_os_str()).collect::<Vec<_>>());
        let e = envs(&cmd);
        assert!(e.contains(&(os(RESTARTED), Some(os("1")))));
        // the running image's entries do not go along (its mount is not the new start's)
        assert!(e.contains(&(os("APPDIR"), None)) && e.contains(&(os("APPIMAGE"), None)), "{e:?}");
        assert!(e.contains(&(os("LD_LIBRARY_PATH"), Some(os("/opt/host/lib")))), "{e:?}");
        // a plain executable inherits the environment as it is
        let plain = restart_command(Path::new("/usr/bin/kiln-launcher"), false, &a, vars);
        assert_eq!(envs(&plain), vec![(os(RESTARTED), Some(os("1")))]);
    }

    #[test]
    fn software_mode_keeps_the_users_own_values() {
        let user = |k: &str| (k == "WEBKIT_DISABLE_COMPOSITING_MODE").then(|| OsString::from("0"));
        assert_eq!(software_env(user), vec![("WEBKIT_SKIA_ENABLE_CPU_RENDERING", "1"), ("LIBGL_ALWAYS_SOFTWARE", "1")]);
        assert_eq!(software_env(|_| None).len(), 3);
        // what an earlier Kiln set is taken back, nothing else ever is
        assert_eq!(set_by_us(Some("WEBKIT_DISABLE_COMPOSITING_MODE,PATH, LIBGL_ALWAYS_SOFTWARE")), vec!["WEBKIT_DISABLE_COMPOSITING_MODE", "LIBGL_ALWAYS_SOFTWARE"]);
        assert!(set_by_us(None).is_empty());
    }

    #[test]
    fn the_nvidia_line_keeps_the_version_and_not_the_build_host() {
        let open = "NVRM version: NVIDIA UNIX Open Kernel Module for x86_64  615.78.08  Release Build  (root@players-pc)  Sat Oct 4 2026\nGCC version: gcc 15\n";
        assert_eq!(nvidia_line(open).as_deref(), Some("NVRM version: NVIDIA UNIX Open Kernel Module for x86_64 615.78.08 Release Build Sat Oct 4 2026"));
        assert_eq!(nvidia_line("").as_deref(), None);
    }

    #[test]
    fn the_watchdog_waits_from_the_window_and_sees_a_ready_call() {
        let w = Watch::new();
        let begun = Instant::now();
        // no window yet: the longer wait runs from the start
        assert_eq!(w.wait(begun, Duration::from_millis(10), Duration::from_millis(30)), Seen::Late { window: false });
        assert!(begun.elapsed() >= Duration::from_millis(30));
        w.window_created();
        let t = Instant::now();
        assert_eq!(w.wait(begun, Duration::from_millis(40), Duration::from_millis(0)), Seen::Late { window: true });
        assert!(t.elapsed() >= Duration::from_millis(30), "the timeout counts from the window");
        // a ready call from another thread ends the wait at once
        let w = std::sync::Arc::new(Watch::new());
        w.window_created();
        let w2 = w.clone();
        let h = std::thread::spawn(move || { std::thread::sleep(Duration::from_millis(20)); assert!(w2.page_started()); assert!(!w2.page_started()); });
        let t = Instant::now();
        assert!(matches!(w.wait(Instant::now(), Duration::from_secs(20), Duration::from_secs(20)), Seen::Started(_)));
        assert!(t.elapsed() < Duration::from_secs(5));
        h.join().unwrap();
    }
}
