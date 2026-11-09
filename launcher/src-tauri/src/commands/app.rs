//! Tauri commands: app. Names and arguments: docs/kiln/launcher/contracts.md section 9.
//! Also the launcher's task runner ([`spawn_task`]): every `TaskId` command (install, import, server start, modpack
//! install...) runs its work on a blocking thread and reports through `kiln://task`.

use crate::events;
use kiln_manage::task::Progress;
use kiln_manage::{Error, ErrorCode};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, LazyLock, Mutex};
use std::time::{Duration, Instant};
use tauri::{AppHandle, Emitter};

/// `app_info` (launcher-only type).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppInfo {
    pub nexus_enabled: bool,
    pub version: String,
    /// Kiln (the runtime this launcher was built with).
    pub kiln: String,
    pub platform: String,
    pub arch: String,
    pub data_dir: String,
    pub commit: Option<String>,
    /// Always false here; the browser mock says true.
    pub mock: bool,
    /// `KILN_LAUNCHER_NO_WEBGL=1` was set for this start: the page draws the plain Play button, never the WebGL fire
    /// (the fallback a machine without WebGL gets; for tests and weak GPUs).
    #[serde(default)]
    pub no_webgl: bool,
    /// Steam Deck / SteamOS / gamescope detection and the `--deck` / `--desktop` flag: which UI to start in
    /// (crate::deck::detect; docs/kiln/launcher/deck.md).
    #[serde(default)]
    pub deck: crate::deck::DeckInfo,
    /// "direct" (frostdev.io, GitHub: updates itself) or "nexus" (Nexus Mods: no self-update, the UI links to the Kiln
    /// page instead): the build's `self-update` feature (crate::distribution).
    #[serde(default)]
    pub distribution: crate::distribution::Distribution,
}

/// `<data>/settings.json`
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LauncherSettings {
    #[serde(default)]
    pub steam_roots: Vec<String>,
    #[serde(default)]
    pub default_game_id: Option<String>,
    #[serde(default)]
    pub allow_http_modpacks: bool,
    #[serde(default = "ten")]
    pub saves_keep: u32,
    /// Check for launcher updates at the start and every 6 hours (Settings, Updates). Replaces the unused
    /// `checkUpdates`, which old files may still carry (ignored).
    #[serde(default = "yes")]
    pub update_check: bool,
    /// The update channel, "stable" or "dev"; absent: the channel of the running build (a dev build follows dev).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub update_channel: Option<String>,
    /// The fire on Play (WebGL). Off: the plain Play button (Settings, Appearance).
    #[serde(default = "yes")]
    pub animated_fire: bool,
    /// A dropped mod goes into the game's active profile (import, add, apply: `library_add`); off: it is only imported
    /// into the library (Settings, Launcher).
    #[serde(default = "yes")]
    pub add_to_active_profile: bool,
    /// The UI to start in: "deck" (the controller UI), "desktop", or "auto"/absent (Deck UI on a Steam Deck, SteamOS
    /// or gamescope). `--deck` / `--desktop` win for one start (docs/kiln/launcher/deck.md).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ui_mode: Option<String>,
}

impl Default for LauncherSettings {
    fn default() -> Self {
        LauncherSettings {
            steam_roots: vec![],
            default_game_id: None,
            allow_http_modpacks: false,
            saves_keep: 10,
            update_check: true,
            update_channel: None,
            animated_fire: true,
            add_to_active_profile: true,
            ui_mode: None,
        }
    }
}

fn ten() -> u32 {
    10
}

fn yes() -> bool {
    true
}

/// `KILN_LAUNCHER_NO_WEBGL` set to anything but empty or `0`.
pub fn no_webgl() -> bool {
    std::env::var("KILN_LAUNCHER_NO_WEBGL").is_ok_and(|v| !v.is_empty() && v != "0")
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum TaskState {
    Queued,
    Running,
    Done,
    Failed,
    Canceled,
}

/// A long operation; every change is emitted as `kiln://task` with the whole task.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Task {
    pub id: String,
    /// The command that started it (e.g. "runtime_install").
    pub kind: String,
    pub title: String,
    pub state: TaskState,
    pub progress: Option<f32>,
    pub message: Option<String>,
    pub started: String,
    pub finished: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub result: Option<serde_json::Value>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<Error>,
}

// ---------------------------------------------------------------- the task runner

struct Entry {
    task: Task,
    cancel: Arc<AtomicBool>,
}

static TASKS: LazyLock<Mutex<HashMap<String, Entry>>> = LazyLock::new(|| Mutex::new(HashMap::new()));
static SEQ: AtomicU64 = AtomicU64::new(0);
/// Finished tasks kept for `task_list` / `task_get`.
const KEEP_FINISHED: usize = 50;

fn finished(s: TaskState) -> bool {
    matches!(s, TaskState::Done | TaskState::Failed | TaskState::Canceled)
}

fn store(task: &Task) {
    let mut map = TASKS.lock().unwrap();
    if let Some(e) = map.get_mut(&task.id) {
        e.task = task.clone();
    }
    // forget the oldest finished tasks
    let mut done: Vec<(String, String)> =
        map.values().filter(|e| finished(e.task.state)).map(|e| (e.task.finished.clone().unwrap_or_default(), e.task.id.clone())).collect();
    if done.len() > KEEP_FINISHED {
        done.sort();
        for (_, id) in done.iter().take(done.len() - KEEP_FINISHED) {
            map.remove(id);
        }
    }
}

fn emit(app: &AppHandle, task: &Task) {
    store(task);
    let _ = app.emit(events::TASK, task);
}

/// The `Progress` a task's work receives: reports become `kiln://task` events (at most ~12 per second, plus every
/// message change), `canceled()` turns true after `task_cancel`.
struct TaskProgress {
    app: AppHandle,
    task: Task,
    cancel: Arc<AtomicBool>,
    last: Instant,
}

impl Progress for TaskProgress {
    fn report(&mut self, fraction: Option<f32>, message: &str) {
        let fraction = fraction.map(|f| f.clamp(0.0, 1.0));
        let msg_changed = self.task.message.as_deref() != Some(message);
        if !msg_changed && self.last.elapsed() < Duration::from_millis(80) {
            self.task.progress = fraction;
            return;
        }
        self.task.progress = fraction;
        self.task.message = Some(message.to_string());
        self.last = Instant::now();
        emit(&self.app, &self.task);
    }

    fn canceled(&self) -> bool {
        self.cancel.load(Ordering::Relaxed)
    }
}

/// Starts `work` on a blocking thread and returns its id at once. The task is emitted as `kiln://task` when it starts,
/// on each progress report and when it ends (`done` with `result`, `failed` with `error`, or `canceled` when the work
/// returns `ErrorCode::Canceled`). `task_cancel` makes `progress.canceled()` true.
pub fn spawn_task(
    app: &AppHandle,
    kind: &str,
    title: String,
    work: impl FnOnce(&mut dyn Progress) -> Result<serde_json::Value, Error> + Send + 'static,
) -> super::TaskId {
    let n = SEQ.fetch_add(1, Ordering::Relaxed) + 1;
    let id = format!("{kind}-{n}-{:x}", kiln_manage::util::now_unix());
    let cancel = Arc::new(AtomicBool::new(false));
    let task = Task {
        id: id.clone(),
        kind: kind.to_string(),
        title,
        state: TaskState::Running,
        progress: None,
        message: None,
        started: kiln_manage::util::now_rfc3339(),
        finished: None,
        result: None,
        error: None,
    };
    TASKS.lock().unwrap().insert(id.clone(), Entry { task: task.clone(), cancel: cancel.clone() });
    emit(app, &task);
    // every task's start and outcome go to launcher.log: the task list lives in memory and ends with the process
    launcher_log("task", &format!("{id} started: {}", task.title));
    let app = app.clone();
    tauri::async_runtime::spawn_blocking(move || {
        let mut p = TaskProgress { app: app.clone(), task, cancel, last: Instant::now() - Duration::from_secs(1) };
        let out = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| work(&mut p)))
            .unwrap_or_else(|_| Err(Error::new(ErrorCode::Internal, "the task panicked (a bug; see launcher.log)")));
        let mut t = p.task;
        t.finished = Some(kiln_manage::util::now_rfc3339());
        match out {
            Ok(v) => {
                t.state = TaskState::Done;
                t.progress = Some(1.0);
                t.result = Some(v);
                launcher_log("task", &format!("{} done: {}", t.id, t.message.as_deref().unwrap_or("-")));
            }
            Err(e) => {
                t.state = if e.code == ErrorCode::Canceled { TaskState::Canceled } else { TaskState::Failed };
                launcher_log("task", &format!(
                    "{} {}: [{}] {}{} (last step: {})",
                    t.id,
                    if t.state == TaskState::Canceled { "canceled" } else { "failed" },
                    e.code.as_str(),
                    e.message,
                    e.detail.as_ref().map(|d| format!(" {d}")).unwrap_or_default(),
                    t.message.as_deref().unwrap_or("-")
                ));
                t.message = Some(e.message.clone());
                t.error = Some(e);
            }
        }
        emit(&app, &t);
    });
    id
}

/// Serializes a task result (for `spawn_task` closures).
pub fn to_value<T: Serialize>(v: T) -> Result<serde_json::Value, Error> {
    serde_json::to_value(v).map_err(|e| Error::new(ErrorCode::Internal, e.to_string()))
}

// ---------------------------------------------------------------- commands

#[tauri::command]
pub async fn app_info(app: AppHandle) -> Result<AppInfo, Error> {
    let dirs = crate::state::dirs()?;
    Ok(AppInfo {
        nexus_enabled: kiln_manage::nexus::enabled(),
        // the release version (tools/release sets it at build time; a plain build is VERSION)
        version: app.package_info().version.to_string(),
        kiln: kiln_manage::VERSION.into(),
        platform: std::env::consts::OS.into(),
        arch: std::env::consts::ARCH.into(),
        data_dir: dirs.root.display().to_string(),
        commit: option_env!("KILN_GIT_COMMIT").filter(|c| !c.is_empty()).map(String::from),
        mock: false,
        no_webgl: no_webgl(),
        deck: crate::deck::detect::current(),
        distribution: crate::distribution::CURRENT,
    })
}

#[tauri::command]
pub async fn app_settings_get() -> Result<LauncherSettings, Error> {
    crate::state::blocking(crate::state::read_settings).await
}

#[tauri::command]
pub async fn app_settings_set(settings: LauncherSettings) -> Result<LauncherSettings, Error> {
    if settings.saves_keep == 0 || settings.saves_keep > 100 {
        return Err(Error::invalid("Keep between 1 and 100 save backups."));
    }
    crate::state::blocking(move || {
        let dirs = crate::state::dirs()?;
        kiln_manage::util::write_json_atomic(&dirs.settings(), &settings)?;
        Ok(settings)
    })
    .await
}

#[tauri::command]
pub async fn app_open_path(path: String) -> Result<(), Error> {
    let p = std::path::PathBuf::from(&path);
    if path.trim().is_empty() || !p.exists() {
        return Err(Error::not_found(format!("{path} does not exist")));
    }
    crate::state::open_external(p.as_os_str())
}

#[tauri::command]
pub async fn app_open_url(url: String) -> Result<(), Error> {
    if !url.starts_with("https://") {
        return Err(Error::new(ErrorCode::Refused, "Only https links open.").with_detail(url));
    }
    crate::state::open_external(std::ffi::OsStr::new(&url))
}

#[tauri::command]
pub async fn task_list() -> Result<Vec<Task>, Error> {
    let map = TASKS.lock().unwrap();
    let mut v: Vec<Task> = map.values().map(|e| e.task.clone()).collect();
    v.sort_by(|a, b| b.started.cmp(&a.started));
    Ok(v)
}

#[tauri::command]
pub async fn task_get(task_id: String) -> Result<Task, Error> {
    TASKS.lock().unwrap().get(&task_id).map(|e| e.task.clone()).ok_or_else(|| Error::not_found(format!("no task {task_id}")))
}

#[tauri::command]
pub async fn task_cancel(task_id: String) -> Result<(), Error> {
    let map = TASKS.lock().unwrap();
    let e = map.get(&task_id).ok_or_else(|| Error::not_found(format!("no task {task_id}")))?;
    e.cancel.store(true, Ordering::Relaxed);
    Ok(())
}

// ---------------------------------------------------------------- local game assets (game
// extraction: docs/kiln/launcher/gameart.md)

/// What `<data>/editor-assets/` and `<data>/admin-assets/` hold (the `kiln-asset` protocol's release roots).
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AssetsStatus {
    /// The launcher data dir.
    pub root: String,
    pub editor: EditorAssets,
    pub admin: AdminAssets,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct EditorAssets {
    /// fonts/, icons/ and ui/ found (in any root, debug roots included).
    pub present: bool,
    /// Bytes in `<data>/editor-assets/`.
    pub bytes: u64,
    /// Where `<data>/editor-assets/` came from: "game" (extracted) | "copy" (a folder) | null (nothing there, or
    /// no `.source.json`).
    #[serde(default)]
    pub source: Option<String>,
    /// The game data build the art was extracted from (`1287369`).
    #[serde(default)]
    pub build: Option<String>,
    /// The client "Extract from game" reads (the default game, else the first client known or found on Steam).
    #[serde(default)]
    pub game_id: Option<String>,
    /// That client's data build.
    #[serde(default)]
    pub game_build: Option<String>,
    /// true: extracted from the installed build; false: the game was updated since (offer to re-extract); null:
    /// unknown (a copy, or no client).
    #[serde(default)]
    pub current: Option<bool>,
    /// A client is there to extract from.
    #[serde(default)]
    pub can_extract: bool,
    /// The extraction task while one runs.
    #[serde(default)]
    pub task: Option<String>,
    /// Files per group of the last extraction (icons, ui, tinted, fonts) and when it ran.
    #[serde(default)]
    pub counts: std::collections::BTreeMap<String, u64>,
    #[serde(default)]
    pub extracted: Option<String>,
    /// Items added by Kiln mods (`<data>/editor-assets/modded/`): what the overlay holds and whether it matches the
    /// current mods (active profile, library, `kiln/mods`, the game's `kiln/state/content-definitions.json`).
    #[serde(default)]
    pub modded: Option<ModdedAssets>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModdedAssets {
    pub items: usize,
    pub icons: usize,
    /// false: the mods changed since; the overlay is rebuilt automatically.
    pub current: bool,
    /// Changes whenever the overlay is rebuilt (the editor uses it to refetch `modded/items.json`).
    pub version: Option<String>,
    pub generated: Option<String>,
    pub mods: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AdminAssets {
    /// map/tiles and map/gpu found.
    pub map: bool,
    /// data/items.json and data/creature-templates.json found.
    pub catalog: bool,
    /// Bytes in `<data>/admin-assets/`.
    pub bytes: u64,
    /// gameart: where `<data>/admin-assets/` came from ("game": relief, tiles, markers, marker icons, fonts extracted;
    /// markers, finder data and catalogs from `$KILN_ADMIN_EXTRAS` also copied when set | "copy" | null), its data build, and whether it
    /// matches the installed client (same fields as the editor's).
    #[serde(default)]
    pub source: Option<String>,
    #[serde(default)]
    pub build: Option<String>,
    #[serde(default)]
    pub game_build: Option<String>,
    #[serde(default)]
    pub current: Option<bool>,
    #[serde(default)]
    pub can_extract: bool,
    /// map/game/markers.json (from the game) or map/data/markers.json (a local copy) found.
    #[serde(default)]
    pub markers: bool,
    #[serde(default)]
    pub counts: std::collections::BTreeMap<String, u64>,
    #[serde(default)]
    pub extracted: Option<String>,
    /// The import task while one runs.
    #[serde(default)]
    pub task: Option<String>,
}

fn dir_bytes(p: &std::path::Path) -> u64 {
    let Ok(rd) = std::fs::read_dir(p) else { return 0 };
    rd.flatten()
        .map(|e| match e.file_type() {
            Ok(t) if t.is_dir() => dir_bytes(&e.path()),
            Ok(t) if t.is_file() => e.metadata().map(|m| m.len()).unwrap_or(0),
            _ => 0,
        })
        .sum()
}

/// The client to extract art from: the default game if it is a client, else the first known client, else the
/// first client found in the Steam libraries (not recorded).
pub fn art_game() -> Option<kiln_manage::game::GameInstall> {
    use kiln_manage::game::{self, GameKind};
    let d = crate::state::dirs().ok()?;
    let ok = |g: &game::GameInstall| g.kind == GameKind::Client && g.path.join("enshrouded.kfc").is_file();
    let known = game::known(&d).unwrap_or_default();
    let default = crate::state::read_settings().ok().and_then(|s| s.default_game_id);
    if let Some(g) = default.and_then(|id| known.iter().find(|g| g.id == id && ok(g))) {
        return Some(g.clone());
    }
    if let Some(g) = known.iter().find(|g| ok(g)) {
        return Some(g.clone());
    }
    game::detect_in(&crate::state::steam_roots(), &d.root).ok()?.into_iter().find(ok)
}

/// Running asset tasks by kind ("editor" | "admin"): a second start returns the running task.
static ASSET_TASKS: LazyLock<Mutex<HashMap<String, String>>> = LazyLock::new(|| Mutex::new(HashMap::new()));

fn running_asset_task(kind: &str) -> Option<String> {
    let id = ASSET_TASKS.lock().unwrap().get(kind).cloned()?;
    let live = TASKS.lock().unwrap().get(&id).is_some_and(|e| !finished(e.task.state));
    live.then_some(id)
}

pub fn assets_status() -> Result<AssetsStatus, Error> {
    let d = crate::state::dirs()?;
    let any = |prefix: &str, rels: &[&str]| crate::assets::roots(prefix).iter().any(|r| rels.iter().all(|x| r.join(x).exists()));
    let editor_dir = d.root.join("editor-assets");
    let admin_dir = d.root.join("admin-assets");
    let game = art_game();
    let st = kiln_manage::gameart::status(&editor_dir, game.as_ref().map(|g| g.path.as_path()));
    let sa = kiln_manage::gameart::status(&admin_dir, game.as_ref().map(|g| g.path.as_path()));
    let es = kiln_manage::gameart::read_source(&editor_dir);
    let asrc = kiln_manage::gameart::read_source(&admin_dir);
    // extracted art records its size; walk only copies
    let bytes_of = |src: &Option<kiln_manage::gameart::SourceInfo>, dir: &std::path::Path| match src {
        Some(s) if s.source == "game" && s.bytes > 0 => s.bytes,
        _ => dir_bytes(dir),
    };
    let modded = editor_dir.join("icons").is_dir().then(|| {
        let inp = kiln_manage::gameart::modded::collect_inputs(&d, game.as_ref());
        let info = kiln_manage::gameart::modded::read_info(&editor_dir);
        ModdedAssets {
            items: info.as_ref().map_or(0, |i| i.items),
            icons: info.as_ref().map_or(0, |i| i.icons),
            current: kiln_manage::gameart::modded::current(&inp, &editor_dir),
            version: info.as_ref().map(|i| i.fingerprint.chars().skip(5).take(12).collect::<String>() + &i.generated),
            generated: info.as_ref().map(|i| i.generated.clone()),
            mods: inp.mods.iter().map(|m| m.id.clone()).collect(),
        }
    });
    Ok(AssetsStatus {
        root: d.root.display().to_string(),
        editor: EditorAssets {
            present: any("editor", &["fonts", "icons", "ui"]),
            bytes: bytes_of(&es, &editor_dir),
            source: st.source,
            build: st.build,
            game_id: game.as_ref().map(|g| g.id.clone()),
            game_build: st.game_build,
            current: st.current,
            can_extract: game.is_some(),
            task: running_asset_task("editor"),
            counts: es.as_ref().map(|s| s.counts.clone()).unwrap_or_default(),
            extracted: st.extracted.clone(),
            modded,
        },
        admin: AdminAssets {
            map: any("admin", &["map/tiles", "map/gpu"]),
            catalog: any("admin", &["data/items.json", "data/creature-templates.json"]),
            bytes: bytes_of(&asrc, &admin_dir),
            source: sa.source,
            build: sa.build,
            game_build: sa.game_build,
            current: sa.current,
            can_extract: game.is_some(),
            markers: any("admin", &["map/game/markers.json"]) || any("admin", &["map/data/markers.json"]),
            counts: asrc.as_ref().map(|s| s.counts.clone()).unwrap_or_default(),
            extracted: sa.extracted.clone(),
            task: running_asset_task("admin"),
        },
    })
}

#[tauri::command]
pub async fn app_assets_status() -> Result<AssetsStatus, Error> {
    crate::state::blocking(assets_status).await
}

/// Fills `<data>/<kind>-assets/` (never downloads). Task result: AssetsStatus.
/// - `editor`, `from` null: **extracts the save editor's art from the installed client** (read-only;
///   `kiln_manage::gameart::extract_editor`; [`art_game`] picks the client; refused while Steam updates it).
/// - `editor`, `from` a folder (dev fallback): copies `<from>/build-assets/game/{fonts,icons,ui}` (a Save Editor
///   checkout) or `<from>/{fonts,icons,ui}`.
/// - `admin`, `from` null: **extracts the map's relief, tiles, marker icons and fonts from the installed client**
///   (`kiln_manage::gameart::extract_map`); what the game does not give yet (marker positions `map/data/`, item-named
///   icons `map/i/{i,j,p}-*`, the catalogs `data/*.json`) is copied from the folder `$KILN_ADMIN_EXTRAS` names, when set.
///   Without a client: the whole copy from that folder.
/// - `admin`, `from` a folder: copies `<from>/web/ext/map/{tiles,gpu,data,i,fonts}` -> `map/`,
///   `<from>/data/{creature-templates,items}.json` -> `data/`.
/// A second call while the same kind runs returns the running task.
#[tauri::command]
pub async fn app_assets_import(app: AppHandle, kind: String, from: Option<String>) -> Result<super::TaskId, Error> {
    if kind != "editor" && kind != "admin" {
        return Err(Error::invalid("kind is editor or admin"));
    }
    if let Some(id) = running_asset_task(&kind) {
        return Ok(id);
    }
    let home = kiln_manage::util::home();
    let from = from.filter(|f| !f.trim().is_empty()).map(|f| kiln_manage::util::expand(f.trim(), &home));
    let app2 = app.clone();
    let id = match (kind.as_str(), from) {
        ("editor", None) => crate::state::blocking(move || extract_editor_task(&app2)).await?,
        ("admin", None) => {
            let extras = std::env::var_os("KILN_ADMIN_EXTRAS").map(std::path::PathBuf::from);
            match crate::state::blocking(|| Ok(art_game())).await? {
                Some(game) => extract_map_task(&app, game, extras.filter(|p| p.is_dir()))?,
                None => match extras {
                    Some(from) => copy_assets_task(&app, "admin", from)?,
                    None => return Err(Error::not_found("no game client to extract the map from")),
                },
            }
        }
        (_, Some(from)) => copy_assets_task(&app, &kind, from)?,
        _ => unreachable!(),
    };
    ASSET_TASKS.lock().unwrap().insert(kind, id.clone());
    Ok(id)
}

/// Appends one line to `<data>/logs/launcher.log`: `<time> <area>: <msg>`. Best effort.
pub fn launcher_log(area: &str, msg: &str) {
    if let Ok(d) = crate::state::dirs() {
        let p = d.logs().join("launcher.log");
        let _ = std::fs::create_dir_all(d.logs());
        if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(p) {
            use std::io::Write;
            let _ = writeln!(f, "{} {area}: {msg}", kiln_manage::util::now_rfc3339());
        }
    }
}

/// Game-art problems (the UI only toasts them).
fn art_log(msg: &str) {
    launcher_log("gameart", msg);
}

/// The editor's art, as one task ("Prepare game art"): extracts it from the client when it is missing or the game
/// changed since, then rebuilds the mod overlay when the mods changed. Nothing to do: the task ends at once.
fn extract_editor_task(app: &AppHandle) -> Result<super::TaskId, Error> {
    let d = crate::state::dirs()?;
    let dest = d.root.join("editor-assets");
    let game = art_game();
    let have_art = dest.join("icons").is_dir();
    let need_art = match &game {
        Some(g) => !have_art || kiln_manage::gameart::status(&dest, Some(&g.path)).current == Some(false),
        None if have_art => false,
        None => return Err(Error::not_found("No Enshrouded client found. Add it in Settings → Games.")),
    };
    if need_art {
        refuse_steam_updating(game.as_ref().unwrap())?;
    }
    Ok(spawn_task(app, "app_assets_import", "Prepare game art".into(), move |p| {
        let r = (|| {
            if need_art {
                let g = game.as_ref().unwrap();
                kiln_manage::gameart::extract_editor(&g.path, &dest, &Default::default(), g.build_id.clone(), p)?;
            }
            let inp = kiln_manage::gameart::modded::collect_inputs(&d, game.as_ref());
            if !kiln_manage::gameart::modded::current(&inp, &dest) {
                let info = kiln_manage::gameart::modded::build(&inp, &dest, p)?;
                for pr in info.problems.iter().take(10) {
                    art_log(&format!("mod items: {pr}"));
                }
            }
            to_value(assets_status()?)
        })();
        if let Err(e) = &r
            && e.code != ErrorCode::Canceled
        {
            art_log(&format!("editor art failed: {} {}", e.message, e.detail.as_ref().map(|d| d.to_string()).unwrap_or_default()));
        }
        r
    }))
}

fn refuse_steam_updating(game: &kiln_manage::game::GameInstall) -> Result<(), Error> {
    let updating = game
        .library
        .as_deref()
        .and_then(|l| kiln_manage::steam::app_manifest(l, game.app_id).ok().flatten())
        .is_some_and(|m| m.updating);
    if updating {
        return Err(Error::new(ErrorCode::SteamUpdating, "Steam is updating the game. Try again when it is done."));
    }
    Ok(())
}

/// The admin map from the game, plus what the game does not give yet (markers, item-named icons, catalogs) from `extras`.
fn extract_map_task(app: &AppHandle, game: kiln_manage::game::GameInstall, extras: Option<std::path::PathBuf>) -> Result<super::TaskId, Error> {
    refuse_steam_updating(&game)?;
    let dest = crate::state::dirs()?.root.join("admin-assets");
    Ok(spawn_task(app, "app_assets_import", "Extract map art".into(), move |p| {
        if let Some(ew) = &extras {
            p.report(Some(0.0), "Copying marker data");
            let map = ew.join("web/ext/map");
            if map.join("data").is_dir() {
                let to = dest.join("map/data");
                let tmp = to.with_extension("tmp-import");
                let _ = std::fs::remove_dir_all(&tmp);
                kiln_manage::util::copy_tree(&map.join("data"), &tmp)?;
                let _ = std::fs::remove_dir_all(&to);
                std::fs::rename(&tmp, &to)?;
            }
            // item-named marker icons (i-*, j-*, p-*); the game's g-* icons come from the extraction
            if let Ok(rd) = std::fs::read_dir(map.join("i")) {
                std::fs::create_dir_all(dest.join("map/i"))?;
                for e in rd.flatten() {
                    let n = e.file_name();
                    if !n.to_string_lossy().starts_with("g-") && e.path().is_file() {
                        std::fs::copy(e.path(), dest.join("map/i").join(&n))?;
                    }
                }
            }
            for f in ["creature-templates.json", "items.json"] {
                if ew.join("data").join(f).is_file() {
                    std::fs::create_dir_all(dest.join("data"))?;
                    std::fs::copy(ew.join("data").join(f), dest.join("data").join(f))?;
                }
            }
        }
        let mut info = kiln_manage::gameart::extract_map(&game.path, &dest, &Default::default(), game.build_id.clone(), p)
            .inspect_err(|e| art_log(&format!("map art failed: {} {}", e.message, e.detail.as_ref().map(|d| d.to_string()).unwrap_or_default())))?;
        if let Some(ew) = &extras {
            info.from = Some(format!("{} (markers, item icons, catalogs)", ew.display()));
            kiln_manage::gameart::write_source(&dest, &info)?;
        }
        to_value(assets_status()?)
    }))
}

fn copy_assets_task(app: &AppHandle, kind: &str, from: std::path::PathBuf) -> Result<super::TaskId, Error> {
    if !from.is_dir() {
        return Err(Error::not_found(format!("{} is not a folder", from.display())));
    }
    let kind = kind.to_string();
    let title = if kind == "editor" { "Import editor art" } else { "Import map assets" };
    Ok(spawn_task(app, "app_assets_import", title.into(), move |p| {
        let d = crate::state::dirs()?;
        let (dest, parts): (std::path::PathBuf, Vec<(std::path::PathBuf, std::path::PathBuf)>) = if kind == "editor" {
            let dest = d.root.join("editor-assets");
            let src = if from.join("build-assets/game").is_dir() { from.join("build-assets/game") } else { from.clone() };
            (dest.clone(), kiln_manage::gameart::EDITOR_DIRS.iter().map(|x| (src.join(x), dest.join(x))).collect())
        } else {
            let dest = d.root.join("admin-assets");
            let map = from.join("web/ext/map");
            let mut v: Vec<_> = ["tiles", "gpu", "data", "i", "fonts"].iter().map(|x| (map.join(x), dest.join("map").join(x))).collect();
            v.extend(["creature-templates.json", "items.json"].iter().map(|x| (from.join("data").join(x), dest.join("data").join(x))));
            (dest, v)
        };
        let missing: Vec<String> = parts.iter().filter(|(s, _)| !s.exists()).map(|(s, _)| s.display().to_string()).collect();
        if missing.len() == parts.len() {
            return Err(Error::not_found(format!("nothing to import in {}", from.display())).with_detail(missing));
        }
        std::fs::create_dir_all(&dest)?;
        let n = parts.len() as f32;
        for (i, (src, to)) in parts.iter().enumerate() {
            if p.canceled() {
                return Err(Error::new(ErrorCode::Canceled, "Canceled."));
            }
            p.report(Some(i as f32 / n), &format!("copying {}", src.file_name().unwrap_or_default().to_string_lossy()));
            if src.is_dir() {
                let tmp = to.with_extension("tmp-import");
                let _ = std::fs::remove_dir_all(&tmp);
                kiln_manage::util::copy_tree(src, &tmp)?;
                let _ = std::fs::remove_dir_all(to);
                std::fs::rename(&tmp, to)?;
            } else if src.is_file() {
                std::fs::create_dir_all(to.parent().unwrap())?;
                std::fs::copy(src, to)?;
            }
        }
        {
            // mark it as a copy (status: source "copy", current unknown)
            let info = kiln_manage::gameart::SourceInfo {
                format: 1,
                kind: kind.clone(),
                source: "copy".into(),
                recipe: None,
                game: None,
                from: Some(from.display().to_string()),
                counts: Default::default(),
                skipped: Default::default(),
                bytes: dir_bytes(&dest),
                extracted: kiln_manage::util::now_rfc3339(),
                seconds: None,
                tool: Some(format!("kiln-launcher {}", env!("CARGO_PKG_VERSION"))),
                problems: vec![],
            };
            kiln_manage::gameart::write_source(&dest, &info)?;
        }
        to_value(assets_status()?)
    }))
}
