//! Self-update of the Kiln Launcher (Tauri's updater plugin, minisign signatures).
//!
//! - Sources, in order: frostdev.io, then GitHub Releases ([`feed::sources`]). Each is tried with its own updater,
//!   so a failure anywhere on frostdev.io (no manifest, a download error, a bad signature or sha256) falls back to
//!   GitHub. The plugin checks every download against the public key in tauri.conf.json, and the version the file
//!   was signed for against the version the manifest announces (`requireSignedVersion`); a file that fails is never
//!   installed, whichever source served it.
//! - Channels: stable and dev ([`feed::Channel`]); the setting `updateChannel`, else the build's own channel.
//! - Checks: 8 s after the start and every 6 hours while the launcher runs (setting `updateCheck`, default on), and
//!   on request (`update_check`). A check is one plain GET of a small JSON file: no ids, no version, nothing else.
//! - Install: the AppImage is replaced in place (the new version runs after a restart); on Windows the verified NSIS
//!   installer runs on "Restart now" (it closes the launcher and starts the new one). Other installs (.deb, .rpm,
//!   development builds) get the download page ([`kind`]).
//! - `<data>/update.json` keeps the skipped version, the last check and the update just installed, so the next start
//!   can say what changed and offer the new Kiln runtime the launcher brought.
//! Every state change is sent as `kiln://update` with the whole [`UpdateStatus`].

pub mod feed;
pub mod kind;
#[cfg(test)]
mod tests;

use feed::{Channel, Source};
use kind::InstallInfo;
use kiln_manage::{Error, ErrorCode};
use serde::{Deserialize, Serialize};
use std::sync::{Arc, Mutex, OnceLock};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter, Runtime};
use tauri_plugin_updater::{Update, UpdaterExt};

pub const EVENT: &str = "kiln://update";
const FIRST_CHECK: Duration = Duration::from_secs(8);
const INTERVAL: Duration = Duration::from_secs(6 * 3600);
const NOTES_MAX: usize = 600;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum Phase {
    /// No check yet.
    Idle,
    Checking,
    UpToDate,
    Available,
    /// Available, but the user skipped this version.
    Skipped,
    /// No source answered.
    Failed,
    Downloading,
    /// Downloaded and verified: installed (AppImage) or waiting for "Restart now" (Windows).
    Ready,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Step {
    Manifest,
    Download,
    Signature,
    Hash,
}

/// What went wrong at one source.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Failure {
    pub source: String,
    pub step: Step,
    pub message: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Available {
    pub version: String,
    /// Short release notes.
    pub notes: Option<String>,
    pub date: Option<String>,
    /// The source whose manifest offered it.
    pub source: String,
    /// The download page for this release.
    pub page: String,
    /// The Kiln runtime the new launcher brings.
    pub runtime: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DownloadProgress {
    pub received: u64,
    pub total: Option<u64>,
    pub source: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Ready {
    pub version: String,
    pub source: String,
    /// True: already in place (AppImage), a restart runs it. False: "Restart now" runs the installer (Windows).
    pub installed: bool,
}

/// An update this launcher installed; kept until the user has seen it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Updated {
    pub from: String,
    pub to: String,
    pub at: String,
    pub source: String,
    #[serde(default)]
    pub notes: Option<String>,
}

/// `update_status`, and the payload of `kiln://update`.
#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UpdateStatus {
    /// This launcher's version.
    pub current: String,
    pub channel: Channel,
    /// Automatic checks are on (setting `updateCheck`).
    pub auto: bool,
    pub install: InstallInfo,
    /// Where to download a build by hand.
    pub page: String,
    pub phase: Phase,
    /// When the last check ended.
    pub checked: Option<String>,
    pub available: Option<Available>,
    /// Sources that failed in the last check or download (also when another source then worked).
    pub failures: Vec<Failure>,
    pub progress: Option<DownloadProgress>,
    pub ready: Option<Ready>,
    pub skipped: Option<String>,
    /// This start follows an update this launcher installed (until `update_ack`).
    pub just_updated: Option<Updated>,
    pub task_id: Option<String>,
    /// Why the last download or install failed.
    pub error: Option<Error>,
}

/// `<data>/update.json`
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Store {
    #[serde(default)]
    format: u32,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    skipped: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    checked: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    updated: Option<Updated>,
}

fn store_path() -> Option<std::path::PathBuf> {
    crate::state::dirs().ok().map(|d| d.root.join("update.json"))
}

fn load_store() -> Store {
    store_path().and_then(|p| kiln_manage::util::read_json::<Store>(&p).ok().flatten()).unwrap_or_default()
}

fn save_store(f: impl FnOnce(&mut Store)) {
    let Some(p) = store_path() else { return };
    let mut s = load_store();
    f(&mut s);
    s.format = 1;
    if let Err(e) = kiln_manage::util::write_json_atomic(&p, &s) {
        log(&format!("cannot write {}: {e}", p.display()));
    }
}

fn log(msg: &str) {
    crate::commands::app::launcher_log("update", msg);
}

// ---------------------------------------------------------------- state

struct Inner {
    status: UpdateStatus,
    /// Windows: the verified installer, run by "Restart now".
    pending: Option<(Update, Vec<u8>)>,
    /// A check or a download runs.
    busy: bool,
}

static STATE: OnceLock<Mutex<Inner>> = OnceLock::new();

fn state() -> std::sync::MutexGuard<'static, Inner> {
    STATE.get_or_init(|| Mutex::new(Inner { status: initial_status("0.0.0", kind::classify("", None, None)), pending: None, busy: false }))
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

fn initial_status(current: &str, install: InstallInfo) -> UpdateStatus {
    UpdateStatus {
        current: current.into(),
        channel: Channel::of_build(current),
        auto: true,
        install,
        page: feed::DOWNLOAD_PAGE.into(),
        phase: Phase::Idle,
        checked: None,
        available: None,
        failures: vec![],
        progress: None,
        ready: None,
        skipped: None,
        just_updated: None,
        task_id: None,
        error: None,
    }
}

/// The settings that steer updates: (automatic checks, effective channel).
fn settings(current: &str) -> (bool, Channel) {
    let s = crate::state::read_settings().unwrap_or_default();
    (s.update_check, Channel::effective(s.update_channel.as_deref(), current))
}

/// The status with the settings as they are now.
pub fn status() -> UpdateStatus {
    let mut g = state();
    let (auto, channel) = settings(&g.status.current);
    g.status.auto = auto;
    g.status.channel = channel;
    g.status.clone()
}

fn emit<R: Runtime>(app: &AppHandle<R>) {
    let s = status();
    let _ = app.emit(EVENT, s);
}

/// Sets up the state for this process and starts the automatic checks. Call once, from the app's setup.
pub fn start<R: Runtime>(app: AppHandle<R>) {
    let current = app.package_info().version.to_string();
    let install = kind::detect(&app);
    let store = load_store();
    let just = store.updated.clone().filter(|u| u.to == current);
    if store.updated.is_some() && just.is_none() {
        // an update that is not this version (never restarted into it, or replaced by hand since)
        save_store(|s| s.updated = None);
    }
    let (auto, channel) = settings(&current);
    {
        let mut g = state();
        g.status = initial_status(&current, install.clone());
        g.status.skipped = store.skipped.clone();
        g.status.checked = store.checked.clone();
        g.status.just_updated = just.clone();
        g.status.auto = auto;
        g.status.channel = channel;
    }
    log(&format!(
        "Kiln Launcher {current} (pid {}, {:?}, {}), channel {}, automatic checks {}",
        std::process::id(),
        install.kind,
        if install.in_place { "updates in place" } else { "updates by download" },
        channel.as_str(),
        if auto { "on" } else { "off" }
    ));
    if let Some(u) = &just {
        log(&format!("updated from {} to {} ({})", u.from, u.to, u.source));
    }
    let _ = std::thread::Builder::new().name("kiln-update".into()).spawn(move || {
        std::thread::sleep(FIRST_CHECK);
        // due now; while automatic checks are off nothing is due, so turning them on checks within 30 s
        let mut next = Instant::now();
        loop {
            let cur = state().status.current.clone();
            if Instant::now() >= next && settings(&cur).0 {
                next = Instant::now() + INTERVAL;
                tauri::async_runtime::block_on(check(&app, false));
            }
            std::thread::sleep(Duration::from_secs(30));
        }
    });
}

// ---------------------------------------------------------------- sources

/// Tries `attempt` on each source in order until one succeeds; the failures of those before it are returned too.
pub async fn try_sources<T, F, Fut>(sources: &[Source], mut attempt: F) -> (Option<(String, T)>, Vec<Failure>)
where
    F: FnMut(Source) -> Fut,
    Fut: std::future::Future<Output = Result<T, Failure>>,
{
    let mut failures = Vec::new();
    for src in sources {
        match attempt(src.clone()).await {
            Ok(v) => return (Some((src.name.clone(), v)), failures),
            Err(f) => failures.push(f),
        }
    }
    (None, failures)
}

/// A user-facing account of an updater error at `step` (`step` is the default for transport errors).
fn describe(e: &tauri_plugin_updater::Error, step: Step) -> (Step, String) {
    use tauri_plugin_updater::Error as U;
    match e {
        U::Minisign(_) | U::Base64(_) | U::SignatureUtf8(_) => (Step::Signature, "the signature does not match the file".into()),
        U::SignedVersionMismatch { signed, announced } => (Step::Signature, format!("the file is signed for {signed}, the update file says {announced}")),
        U::MissingSignedVersion => (Step::Signature, "the signature does not name the version it is for".into()),
        U::ReleaseNotFound => (Step::Manifest, "no update file there".into()),
        U::Serialization(_) => (Step::Manifest, "the update file is not valid".into()),
        U::TargetNotFound(_) | U::TargetsNotFound(_) => (Step::Manifest, "no build for this system in the update file".into()),
        U::InsecureTransportProtocol => (Step::Manifest, "the address is not https".into()),
        U::Reqwest(r) => {
            // the innermost cause says it best ("Connection refused (os error 111)", "dns error: ...")
            let mut m = r.to_string();
            let mut src = std::error::Error::source(r);
            while let Some(s) = src {
                m = s.to_string();
                src = s.source();
            }
            let m = if r.is_timeout() { "timed out".to_string() } else { m };
            (step, format!("not reachable ({m})"))
        }
        U::Network(m) => (step, m.trim_matches('`').to_string()),
        other => (step, other.to_string()),
    }
}

fn failure(src: &Source, (step, message): (Step, String)) -> Failure {
    Failure { source: src.name.clone(), step, message }
}

/// The updater for one source and channel. Requests: a connect timeout and a read timeout, no total limit (a slow
/// line still finishes the download).
fn updater_for<R: Runtime>(app: &AppHandle<R>, src: &Source, channel: Channel) -> Result<tauri_plugin_updater::Updater, Failure> {
    let url = tauri::Url::parse(src.url(channel)).map_err(|e| failure(src, (Step::Manifest, format!("bad address: {e}"))))?;
    app.updater_builder()
        .endpoints(vec![url])
        .and_then(|b| {
            b.configure_client(|c| c.connect_timeout(Duration::from_secs(15)).read_timeout(Duration::from_secs(60)))
                .version_comparator(move |cur, rel| feed::accepts(&cur.to_string(), &rel.version.to_string(), channel))
                .build()
        })
        .map_err(|e| failure(src, describe(&e, Step::Manifest)))
}

/// One source's answer: an update, or None when it has nothing newer.
pub async fn check_source<R: Runtime>(app: &AppHandle<R>, src: &Source, channel: Channel) -> Result<Option<Update>, Failure> {
    let updater = updater_for(app, src, channel)?;
    updater.check().await.map_err(|e| failure(src, describe(&e, Step::Manifest)))
}

/// The first source that answers.
pub async fn check_sources<R: Runtime>(app: &AppHandle<R>, sources: &[Source], channel: Channel) -> (Option<(String, Option<Update>)>, Vec<Failure>) {
    try_sources(sources, |src| {
        let app = app.clone();
        async move { check_source(&app, &src, channel).await }
    })
    .await
}

/// A downloaded, verified update.
pub struct Acquired {
    pub update: Update,
    pub bytes: Vec<u8>,
}

/// Downloads `update` from where its manifest points and checks it: the signature and signed version (the
/// plugin), then the sha256 the manifest lists, if any.
pub async fn fetch(update: &Update, src: &Source, progress: &Arc<Mutex<Option<DownloadProgress>>>) -> Result<Vec<u8>, Failure> {
    let name = src.name.clone();
    *progress.lock().unwrap() = Some(DownloadProgress { received: 0, total: None, source: name.clone() });
    let mut received = 0u64;
    let bytes = update
        .download(
            |n, total| {
                received += n as u64;
                *progress.lock().unwrap() = Some(DownloadProgress { received, total, source: name.clone() });
            },
            || {},
        )
        .await
        .map_err(|e| failure(src, describe(&e, Step::Download)))?;
    if let Some(want) = feed::sha256_for(&update.raw_json, update.download_url.as_str()) {
        let got = kiln_manage::hash::sha256_hex(&bytes);
        if got != want {
            return Err(failure(src, (Step::Hash, format!("the file's sha256 is {}, the update file says {}", &got[..12], &want[..want.len().min(12)]))));
        }
    }
    Ok(bytes)
}

/// The update from the first source whose manifest offers one and whose file downloads and verifies.
pub async fn acquire<R: Runtime>(
    app: &AppHandle<R>,
    sources: &[Source],
    channel: Channel,
    progress: Arc<Mutex<Option<DownloadProgress>>>,
) -> (Option<(String, Acquired)>, Vec<Failure>) {
    try_sources(sources, |src| {
        let app = app.clone();
        let progress = progress.clone();
        async move {
            let update = check_source(&app, &src, channel)
                .await?
                .ok_or_else(|| failure(&src, (Step::Manifest, "it offers no newer version".into())))?;
            let bytes = fetch(&update, &src, &progress).await?;
            Ok(Acquired { update, bytes })
        }
    })
    .await
}

fn summary(failures: &[Failure]) -> String {
    failures.iter().map(|f| format!("{}: {}", f.source, f.message)).collect::<Vec<_>>().join("; ")
}

// ---------------------------------------------------------------- check

/// Checks the sources now (unless a download runs or an update is ready) and returns the status.
pub async fn check<R: Runtime>(app: &AppHandle<R>, manual: bool) -> UpdateStatus {
    let current = {
        let mut g = state();
        if g.busy || matches!(g.status.phase, Phase::Downloading | Phase::Ready) {
            return g.status.clone();
        }
        g.busy = true;
        g.status.phase = Phase::Checking;
        g.status.current.clone()
    };
    emit(app);
    let (_, channel) = settings(&current);
    let sources = feed::sources();
    let (answer, failures) = check_sources(app, &sources, channel).await;
    let now = kiln_manage::util::now_rfc3339();
    let how = if manual { "check" } else { "automatic check" };
    {
        let mut g = state();
        g.busy = false;
        g.status.checked = Some(now.clone());
        g.status.failures = failures.clone();
        g.status.error = None;
        match answer {
            Some((source, Some(u))) => {
                let k = feed::kiln_fields(&u.raw_json);
                let skipped = g.status.skipped.as_deref() == Some(u.version.as_str());
                g.status.phase = if skipped { Phase::Skipped } else { Phase::Available };
                g.status.available = Some(Available {
                    version: u.version.clone(),
                    notes: u.body.as_deref().map(|n| feed::short_notes(n, NOTES_MAX)).filter(|n| !n.is_empty()),
                    date: u.raw_json.get("pub_date").and_then(|d| d.as_str()).map(String::from),
                    source: source.clone(),
                    page: k.page.unwrap_or_else(|| feed::DOWNLOAD_PAGE.into()),
                    runtime: k.runtime,
                });
                log(&format!("{how} ({}): {} offers {}{}", channel.as_str(), source, u.version, if skipped { " (skipped)" } else { "" }));
            }
            Some((source, None)) => {
                g.status.phase = Phase::UpToDate;
                g.status.available = None;
                log(&format!("{how} ({}): up to date ({source})", channel.as_str()));
            }
            None => {
                g.status.phase = Phase::Failed;
                log(&format!("{how} ({}) failed: {}", channel.as_str(), summary(&failures)));
            }
        }
        if !failures.is_empty() && g.status.phase != Phase::Failed {
            log(&format!("{how}: tried before: {}", summary(&failures)));
        }
    }
    save_store(|s| s.checked = Some(now));
    emit(app);
    status()
}

// ---------------------------------------------------------------- install, restart, skip

/// Starts the download and install as a task (`kiln://task`, "Update the launcher"); its result is the status.
pub fn install(app: &AppHandle) -> Result<String, Error> {
    {
        let mut g = state();
        if !g.status.install.in_place {
            return Err(Error::new(ErrorCode::Unsupported, g.status.install.reason.clone().unwrap_or_else(|| "This install cannot update itself.".into()))
                .with_detail(g.status.page.clone()));
        }
        if g.status.phase == Phase::Ready {
            return Err(Error::new(ErrorCode::Conflict, "The update is ready: restart the launcher to use it."));
        }
        if g.busy {
            return Err(Error::new(ErrorCode::Busy, "An update check or download is already running."));
        }
        g.busy = true;
        g.status.phase = Phase::Downloading;
        g.status.progress = None;
        g.status.error = None;
        g.status.failures.clear();
    }
    emit(app);
    log("install requested");
    let a = app.clone();
    let id = crate::commands::app::spawn_task(app, "update_install", "Update the launcher".into(), move |p| {
        let out = run_install(&a, p);
        state().busy = false;
        emit(&a);
        out.and_then(|_| crate::commands::app::to_value(status()))
    });
    state().status.task_id = Some(id.clone());
    emit(app);
    Ok(id)
}

fn run_install(app: &AppHandle, p: &mut dyn kiln_manage::task::Progress) -> Result<(), Error> {
    let current = state().status.current.clone();
    let (_, channel) = settings(&current);
    let sources = feed::sources();
    let progress: Arc<Mutex<Option<DownloadProgress>>> = Arc::new(Mutex::new(None));
    let (tx, rx) = std::sync::mpsc::channel();
    let (app2, progress2) = (app.clone(), progress.clone());
    let job = tauri::async_runtime::spawn(async move {
        let r = acquire(&app2, &sources, channel, progress2).await;
        let _ = tx.send(r);
    });
    p.report(None, "Looking for the update");
    let mut last_emit = Instant::now();
    let (got, failures) = loop {
        match rx.recv_timeout(Duration::from_millis(150)) {
            Ok(r) => break r,
            Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {
                if p.canceled() {
                    job.abort();
                    let mut g = state();
                    g.status.phase = if g.status.available.is_some() { Phase::Available } else { Phase::Idle };
                    g.status.progress = None;
                    log("install canceled");
                    return Err(Error::new(ErrorCode::Canceled, "Canceled."));
                }
                let now = progress.lock().unwrap().clone();
                if let Some(d) = now {
                    let frac = d.total.filter(|t| *t > 0).map(|t| d.received as f32 / t as f32);
                    let mb = |b: u64| format!("{:.1}", b as f64 / 1_048_576.0);
                    let msg = match d.total {
                        Some(t) => format!("Downloading from {}: {} of {} MB", d.source, mb(d.received), mb(t)),
                        None => format!("Downloading from {}: {} MB", d.source, mb(d.received)),
                    };
                    p.report(frac, &msg);
                    state().status.progress = Some(d);
                    if last_emit.elapsed() > Duration::from_millis(250) {
                        last_emit = Instant::now();
                        emit(app);
                    }
                }
            }
            Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {
                return Err(Error::new(ErrorCode::Internal, "the download stopped unexpectedly"));
            }
        }
    };
    let fail = |e: Error, failures: Vec<Failure>| {
        let mut g = state();
        g.status.phase = if g.status.available.is_some() { Phase::Available } else { Phase::Failed };
        g.status.progress = None;
        g.status.failures = failures;
        g.status.error = Some(e.clone());
        e
    };
    let Some((source, acq)) = got else {
        let code = if failures.iter().any(|f| f.step == Step::Signature) {
            ErrorCode::Refused
        } else if failures.iter().any(|f| f.step == Step::Hash) {
            ErrorCode::HashMismatch
        } else {
            ErrorCode::Network
        };
        log(&format!("install failed: {}", summary(&failures)));
        let e = Error::new(code, "The update could not be downloaded and verified from any source.").with_detail(summary(&failures));
        return Err(fail(e, failures));
    };
    let version = acq.update.version.clone();
    if !failures.is_empty() {
        log(&format!("{version}: fell back to {source} after: {}", summary(&failures)));
    }
    p.report(Some(1.0), &format!("Verified {version} from {source}"));
    let updated = Updated {
        from: current.clone(),
        to: version.clone(),
        at: kiln_manage::util::now_rfc3339(),
        source: source.clone(),
        notes: acq.update.body.as_deref().map(|n| feed::short_notes(n, NOTES_MAX)),
    };
    if cfg!(windows) {
        // the installer replaces the running program, so it runs on "Restart now" (and closes the launcher)
        let mut g = state();
        g.pending = Some((acq.update, acq.bytes));
        g.status.phase = Phase::Ready;
        g.status.progress = None;
        g.status.failures = failures;
        g.status.ready = Some(Ready { version: version.clone(), source: source.clone(), installed: false });
        drop(g);
        log(&format!("{version} downloaded and verified from {source}; the installer runs on restart"));
        return Ok(());
    }
    p.report(None, "Installing");
    if let Err(e) = acq.update.install(&acq.bytes) {
        let (_, m) = describe(&e, Step::Download);
        log(&format!("install of {version} failed: {m}"));
        return Err(fail(Error::new(ErrorCode::Io, format!("The update could not be put in place: {m}")), failures));
    }
    save_store(|s| s.updated = Some(updated));
    let mut g = state();
    g.status.phase = Phase::Ready;
    g.status.progress = None;
    g.status.failures = failures;
    g.status.ready = Some(Ready { version: version.clone(), source: source.clone(), installed: true });
    drop(g);
    log(&format!("{version} from {source} is in place; it runs after a restart"));
    Ok(())
}

/// "Restart now": the new version starts (AppImage: a restart; Windows: the installer, which closes the launcher
/// and starts the new one). Returns only on failure.
pub fn restart(app: &AppHandle) -> Result<(), Error> {
    let (phase, ready) = {
        let g = state();
        (g.status.phase, g.status.ready.clone())
    };
    let Some(ready) = ready.filter(|_| phase == Phase::Ready) else {
        return Err(Error::new(ErrorCode::Conflict, "No update is ready."));
    };
    if ready.installed {
        log(&format!("restarting into {}", ready.version));
        app.restart();
    }
    let pending = state().pending.take();
    let Some((update, bytes)) = pending else {
        return Err(Error::new(ErrorCode::Conflict, "The downloaded update is gone; check again."));
    };
    let current = state().status.current.clone();
    save_store(|s| {
        s.updated = Some(Updated {
            from: current,
            to: update.version.clone(),
            at: kiln_manage::util::now_rfc3339(),
            source: ready.source.clone(),
            notes: update.body.as_deref().map(|n| feed::short_notes(n, NOTES_MAX)),
        })
    });
    log(&format!("running the installer of {}", update.version));
    // returns only when the installer could not be started
    let r = update.install(&bytes);
    save_store(|s| s.updated = None);
    let e = r.err().map(|e| describe(&e, Step::Download).1).unwrap_or_else(|| "the installer did not start".into());
    log(&format!("installer of {} failed: {e}", update.version));
    state().pending = Some((update, bytes));
    Err(Error::new(ErrorCode::Io, format!("The installer could not start: {e}")))
}

/// Skips `version` (None: un-skips).
pub fn skip<R: Runtime>(app: &AppHandle<R>, version: Option<String>) -> UpdateStatus {
    save_store(|s| s.skipped = version.clone());
    {
        let mut g = state();
        g.status.skipped = version.clone();
        let offered = g.status.available.as_ref().map(|a| a.version.clone());
        if matches!(g.status.phase, Phase::Available | Phase::Skipped) {
            g.status.phase = if offered.is_some() && offered == version { Phase::Skipped } else { Phase::Available };
        }
    }
    log(&format!("skipped version: {}", version.as_deref().unwrap_or("none")));
    emit(app);
    status()
}

/// The user has seen the "updated" notice.
pub fn ack<R: Runtime>(app: &AppHandle<R>) -> UpdateStatus {
    save_store(|s| s.updated = None);
    state().status.just_updated = None;
    emit(app);
    status()
}
