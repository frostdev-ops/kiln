//! The data folder's commands (dev.25, package `data-folder`; contracts.md section 9): where the launcher keeps its
//! data, a check of a folder the player picked, the move (a task), "use this one", a fresh start after a broken
//! pointer, the notice after a move, and the restart. The crate does the work (kiln_manage::data_folder,
//! kiln_manage::data_folder_move); this file decides when, and holds the process's data folder while it runs
//! (crate::folder).
//!
//! At start ([`start`], before any watcher): a broken pointer or another launcher's move stops here (the window shows
//! only the dialog); an unfinished move's partial copy is removed (or left with its path when the proof is missing);
//! a committed move whose old copy is not removed yet gets a task that removes it ("Remove the old data folder"),
//! after the window is up. The result is `data_folder_get().notice` until `data_folder_notice_ack`.

use crate::commands::app::{begin_alone, drop_held_log, flush_held_log, launcher_log, spawn_task, to_value};
use crate::folder::{self, Folder, Hold};
use kiln_manage::data_folder::{self as df, RealFacts};
use kiln_manage::data_folder_move::{self as dfm, Cleanup, Journal, Pending};
use kiln_manage::paths::{LocateProblem, Located, RootSource};
use kiln_manage::{Error, ErrorCode};
use serde::Serialize;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{Duration, Instant};
use tauri::AppHandle;

// ---------------------------------------------------------------- what the front end gets (api/commands.ts)

/// An entry of the old folder, with its size and where it is.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct EntryOut {
    pub name: String,
    pub bytes: u64,
    pub path: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct KeptOut {
    pub path: String,
    pub bytes: u64,
    pub reason: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct ProblemOut {
    /// The folder the pointer names (the pointer file when it could not be read).
    pub path: String,
    pub reason: String,
    pub kind: kiln_manage::paths::ProblemKind,
}

/// The notice at the start after a move.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct NoticeOut {
    /// moved: a move finished; undone: a move that did not finish was undone at this start (its partial copy
    /// removed, or left where `kept` and `reason` say); unclear: the move's record matches neither folder (nothing
    /// was removed, `reason` says where things are).
    pub kind: &'static str,
    pub from: String,
    pub to: String,
    /// Every path still in the old folder (`stayed` and `kept`).
    pub left: Vec<String>,
    /// Entries the launcher does not know: copied, and left in the old folder on purpose.
    pub stayed: Vec<EntryOut>,
    /// What could not be removed (a file that changed since the move, a game's profile that names the old folder).
    pub kept: Vec<KeptOut>,
    /// The removal of the old copy runs now (a task).
    pub removing: bool,
    /// The removal stopped before the end (canceled, or the launcher closed): it goes on at the next start.
    pub unfinished: bool,
    /// The old folder itself is gone.
    pub folder_removed: bool,
    /// Why nothing was removed, and where the folder is.
    pub reason: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct DataFolderOut {
    /// The data folder in use (the default place when the pointer is broken).
    pub path: String,
    pub default_path: String,
    pub source: &'static str,
    pub problem: Option<ProblemOut>,
    pub notice: Option<NoticeOut>,
    /// Another launcher holds the folder's lock exclusively: it is moving the data.
    pub elsewhere: bool,
    /// An unfinished move's partial copy is being removed now (the folder is held): the window shows only its dialog.
    pub undoing: Option<UndoOut>,
    /// Why the folder cannot be moved now (an earlier move is not finished; the crate refuses `move_pending`): the
    /// Settings row says it instead of offering the folder dialog.
    pub pending: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct UndoOut {
    /// The task "Remove the unfinished copy" (None for the two seconds before it starts).
    pub task: Option<String>,
    /// The unfinished copy.
    pub folder: String,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SizeOut {
    pub bytes: u64,
    pub files: u64,
}

/// A check of a picked folder: a plan (`move`, `use`) or a refusal (`reason`, `code`).
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct PlanOut {
    pub picked: String,
    pub target: String,
    pub action: &'static str,
    pub reason: Option<String>,
    /// The refusal's stable code (`data_folder::RefusalCode`, snake_case).
    pub code: Option<String>,
    pub bytes: u64,
    pub files: u64,
    /// What the target needs free.
    pub needed: u64,
    pub free: Option<u64>,
    pub same_volume: Option<bool>,
    pub hard_links: bool,
    /// The games (by name) that would get their mods copied into their game folder from there and do not today.
    pub copy_mode: Vec<String>,
    /// Entries the launcher does not know: copied, and also left in the old folder.
    pub stays: Vec<EntryOut>,
    /// What will not work from there, one sentence each (the crate's).
    pub warnings: Vec<String>,
}

fn text(p: &Path) -> String {
    p.display().to_string()
}

fn source_word(s: RootSource) -> &'static str {
    match s {
        RootSource::Env => "env",
        RootSource::Pointer => "pointer",
        RootSource::Default => "default",
    }
}

fn busy(m: impl Into<String>) -> Error {
    Error::new(ErrorCode::Busy, m)
}

// ---------------------------------------------------------------- the start, the notice

/// The removal of an old copy at this start.
#[derive(Debug, Clone, PartialEq)]
enum Cleaning {
    None,
    /// Scheduled or running (the task id once it runs).
    Running(Option<String>),
    Ended,
}

/// What this start found and did about a move (the notice's other parts are read live from the journal).
struct StartState {
    /// The notice of an unfinished or unclear move, made at the start (the crate removes the journal of an undone move).
    notice: Option<NoticeOut>,
    /// `data_folder_notice_ack` was called: no notice for the rest of this process.
    acked: bool,
    cleaning: Cleaning,
    /// What this start's removal of the old copy gave. The journal keeps a result only when the removal ran; a refusal
    /// (a game's profile still names the old folder, no proof) is kept here, for this process.
    removal: Option<Cleanup>,
    undoing: Undoing,
    /// The unfinished copy being removed (the journal's `to`).
    undo_folder: Option<PathBuf>,
}

/// The removal of an unfinished move's partial copy at this start.
#[derive(Debug, Clone, PartialEq)]
enum Undoing {
    None,
    /// Scheduled or running (the task id once it runs): the folder is held.
    Running(Option<String>),
    Ended,
}

static START: Mutex<StartState> = Mutex::new(StartState {
    notice: None, acked: false, cleaning: Cleaning::None, removal: None, undoing: Undoing::None, undo_folder: None,
});

/// Tests: a new process (what this start found and did is forgotten).
#[cfg(test)]
pub(crate) fn forget_start() {
    *start_state() = StartState { notice: None, acked: false, cleaning: Cleaning::None, removal: None, undoing: Undoing::None, undo_folder: None };
    DEFERRED.lock().unwrap_or_else(|e| e.into_inner()).take();
}

fn start_state() -> std::sync::MutexGuard<'static, StartState> {
    START.lock().unwrap_or_else(|e| e.into_inner())
}

/// The journal's unknown entries (copied, left in the old folder).
fn stayed_of(j: &Journal) -> Vec<EntryOut> {
    j.entries.iter().filter(|e| !e.known).map(|e| EntryOut { name: e.name.clone(), bytes: e.bytes, path: text(&j.from.join(&e.name)) }).collect()
}

fn kept_of(c: Option<&Cleanup>) -> Vec<KeptOut> {
    c.map(|c| c.kept.iter().map(|k| KeptOut { path: text(&k.path), bytes: k.bytes, reason: k.reason.clone() }).collect()).unwrap_or_default()
}

fn notice_of(kind: &'static str, j: &Journal, c: Option<&Cleanup>, removing: bool, reason: Option<String>) -> NoticeOut {
    let stayed = if kind == "moved" { stayed_of(j) } else { Vec::new() };
    let kept = kept_of(c);
    let left = stayed.iter().map(|s| s.path.clone()).chain(kept.iter().map(|k| k.path.clone())).collect();
    let (from, to) = (text(&j.from), text(&j.to));
    NoticeOut {
        kind,
        from,
        to,
        left,
        stayed,
        kept,
        removing,
        // moved: the removal of the old copy has not finished; undone: the removal of the partial copy was canceled
        unfinished: (kind == "moved" && !removing && !c.is_some_and(|c| c.done || c.reason.is_some()))
            || (kind == "undone" && c.is_some_and(|c| c.interrupted)),
        folder_removed: c.is_some_and(|c| c.folder_removed),
        reason: reason.or_else(|| c.and_then(|c| c.reason.clone())),
    }
}

/// The notice now: none after the ack or with `KILN_DATA_DIR`; an undone or unclear move from the start; a committed
/// move from its journal (and whether its removal runs).
fn notice(f: &Folder) -> Option<NoticeOut> {
    let l = f.located().ok()?;
    if l.source == RootSource::Env {
        return None;
    }
    let st = start_state();
    if st.acked {
        return None;
    }
    if let Some(n) = &st.notice {
        return Some(n.clone());
    }
    let removing = matches!(st.cleaning, Cleaning::Running(_));
    let removal = st.removal.clone();
    drop(st);
    match dfm::pending(&f.env).ok()? {
        // this start's removal when it was refused (not in the journal), else the journal's result
        Pending::Committed { journal } => {
            let c = removal.filter(|c| c.reason.is_some()).or_else(|| journal.result.clone());
            Some(notice_of("moved", &journal, c.as_ref(), removing, None))
        }
        _ => None,
    }
}

/// The old copy of a committed move is still to be removed (no result yet, or one that stopped before the end).
fn needs_cleanup(j: &Journal) -> bool {
    !j.result.as_ref().is_some_and(|c| c.done || c.reason.is_some())
}

/// What start-up does next ([`start_with`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) enum Next {
    /// Nothing about a move: start-up goes on.
    Nothing,
    /// A committed move's old copy is left to remove: start-up goes on, the task "Remove the old data folder" follows.
    Cleanup,
    /// An unfinished move's partial copy is left: the folder is held and start-up waits for the task "Remove the
    /// unfinished copy" (its proof is a file identical to the one in the folder in use, so nothing may write it).
    Undo,
}

/// The start-up steps that use the data folder, put off while an unfinished copy is removed ([`end_undo`] runs them).
static DEFERRED: Mutex<Option<Box<dyn FnOnce() + Send>>> = Mutex::new(None);

fn defer(f: Box<dyn FnOnce() + Send>) {
    *DEFERRED.lock().unwrap_or_else(|e| e.into_inner()) = Some(f);
}

/// Before any watcher (lib.rs setup). `then`: the start-up steps that use the data folder (lib.rs `after_folder`):
/// run now, put off until an unfinished copy is removed, or not at all (a broken pointer, another launcher's move:
/// the window shows only the dialog).
pub fn start(app: &AppHandle, then: fn(&AppHandle)) {
    let f = folder::current();
    if let Some(p) = f.problem() {
        eprintln!("data folder: {}", p.reason);
        return;
    }
    if f.hold() == Hold::Elsewhere {
        eprintln!("data folder: {MOVING_ELSEWHERE_LOG}");
        return;
    }
    // on the main thread (setup, and the end of the recovery's task through the event loop)
    let a = app.clone();
    let then: Box<dyn FnOnce() + Send> = Box::new(move || {
        let b = a.clone();
        if let Err(e) = a.run_on_main_thread(move || then(&b)) {
            eprintln!("data folder: the start-up steps could not run: {e}");
        }
    });
    match begin(&f, then) {
        Next::Nothing => {}
        Next::Cleanup => after_the_window(app, spawn_cleanup),
        Next::Undo => after_the_window(app, spawn_undo),
    }
}

/// [`start_with`], then the start-up steps that use the folder: now, or when the recovery's hold ends.
pub(crate) fn begin(f: &Folder, then: Box<dyn FnOnce() + Send>) -> Next {
    let next = start_with(f);
    if next == Next::Undo { defer(then) } else { then() }
    next
}

/// A task after the window is up: it never holds up the start, and a cancel or a closed launcher go on next time.
fn after_the_window(app: &AppHandle, spawn: fn(&AppHandle)) {
    let app = app.clone();
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_secs(2));
        spawn(&app);
    });
}

const MOVING_ELSEWHERE_LOG: &str = "another launcher holds the data folder's lock exclusively (a move)";

/// What a start does about a move (no window needed; tests call it). It removes nothing: an unfinished move holds the
/// folder for its task, a committed one leaves its removal to a task, an unclear record becomes the notice.
pub(crate) fn start_with(f: &Folder) -> Next {
    let Ok(l) = f.located() else { return Next::Nothing };
    if l.source == RootSource::Env {
        return Next::Nothing;
    }
    match dfm::pending(&f.env) {
        // not acknowledged yet: the removal goes on (or, with nothing left, removes the lock files and the empty old
        // folder the moving process still held)
        Ok(Pending::Committed { .. }) => {
            start_state().cleaning = Cleaning::Running(None);
            return Next::Cleanup;
        }
        Ok(Pending::Uncommitted { journal }) => {
            // held before any watcher, `launcher_log` or import touches the folder
            f.set_hold(Hold::Undoing);
            let mut st = start_state();
            st.undoing = Undoing::Running(None);
            st.undo_folder = Some(journal.to.clone());
            return Next::Undo;
        }
        Ok(Pending::Unclear { journal, reason }) => {
            let n = match &journal {
                Some(j) => notice_of("unclear", j, j.result.as_ref(), false, Some(reason)),
                None => NoticeOut {
                    kind: "unclear", from: String::new(), to: String::new(), left: vec![], stayed: vec![], kept: vec![], removing: false,
                    unfinished: false, folder_removed: false, reason: Some(reason),
                },
            };
            start_state().notice = Some(n);
        }
        _ => {}
    }
    Next::Nothing
}

/// The task that removes an unfinished move's partial copy ("Remove the unfinished copy"), with the folder held.
fn spawn_undo(app: &AppHandle) {
    let id = spawn_task(app, "data_folder_undo", "Remove the unfinished copy".into(), move |p| {
        let f = folder::current();
        to_value(undo_work(&f, p)?)
    });
    let mut st = start_state();
    if st.undoing == Undoing::Running(None) {
        st.undoing = Undoing::Running(Some(id));
    }
}

/// Ends the recovery's hold however the task ends (done, canceled, failed, a panic): everything runs again.
struct EndUndo<'a>(&'a Folder);

impl Drop for EndUndo<'_> {
    fn drop(&mut self) {
        end_undo(self.0);
    }
}

/// The hold ends: the held log lines are written, then the start-up steps that were put off run.
fn end_undo(f: &Folder) {
    start_state().undoing = Undoing::Ended;
    if f.hold() == Hold::Undoing {
        f.set_hold(Hold::Free);
    }
    flush_held_log();
    let deferred = DEFERRED.lock().unwrap_or_else(|e| e.into_inner()).take();
    if let Some(d) = deferred {
        d();
    }
}

/// The recovery itself (the task's work; tests call it): the crate's removal of the partial copy, then the notice
/// "Move not finished" with what is left; the hold ends when it returns.
pub(crate) fn undo_work(f: &Folder, p: &mut dyn kiln_manage::task::Progress) -> Result<Cleanup, Error> {
    let _end = EndUndo(f);
    let journal = match dfm::pending(&f.env)? {
        Pending::Uncommitted { journal } => journal,
        _ => return Err(Error::not_found("No unfinished move of the data folder to undo.")),
    };
    // a file goes only when the folder in use holds the identical one; progress per file, a cancel between files
    // (`interrupted`, the journal kept: the next start goes on); what stays is in `kept`, with why
    let c = dfm::remove_partial_copy_with(&f.env, p);
    let n = match &c {
        Ok(c) => notice_of("undone", &journal, Some(c), false, None),
        Err(e) => notice_of("undone", &journal, None, false, Some(e.message.clone())),
    };
    start_state().notice = Some(n);
    launcher_log("data folder", &format!("the unfinished move to {} was undone: {:?}", journal.to.display(), c.as_ref().map(|c| (c.done, c.interrupted, c.kept.len()))));
    c
}

/// The task that removes a committed move's old copy ("Remove the old data folder"; cancel between two files).
fn spawn_cleanup(app: &AppHandle) {
    let id = spawn_task(app, "data_folder_cleanup", "Remove the old data folder".into(), move |p| {
        let f = folder::current();
        to_value(cleanup_work(&f, p)?)
    });
    let mut st = start_state();
    if st.cleaning == Cleaning::Running(None) {
        st.cleaning = Cleaning::Running(Some(id));
    }
}

/// The removal itself (the task's work; tests call it).
fn cleanup_work(f: &Folder, p: &mut dyn kiln_manage::task::Progress) -> Result<Cleanup, Error> {
    let r = dfm::remove_old_copy(&f.env, p);
    {
        let mut st = start_state();
        st.cleaning = Cleaning::Ended;
        st.removal = r.as_ref().ok().cloned();
    }
    let c = r?;
    launcher_log("data folder", &format!(
        "old copy {}: {} files removed, {} kept, done {}, interrupted {}",
        c.folder.display(), c.removed_files, c.kept.len(), c.done, c.interrupted
    ));
    Ok(c)
}

/// The notice was seen: it is gone for this process whatever comes next. A finished removal (or one the journal says
/// has nothing to remove) acknowledges the journal. Kept for the next start: a removal that stopped before the end or
/// was refused (a game's profile still names the old folder), and an unfinished move's partial copy that could not be
/// removed (the crate refuses the acknowledgement while it carries the journal's marker).
fn notice_ack_with(f: &Folder) -> Result<(), Error> {
    {
        let mut st = start_state();
        st.acked = true;
        st.notice = None;
    }
    let running = matches!(start_state().cleaning, Cleaning::Running(_));
    let finished = match dfm::pending(&f.env)? {
        Pending::Committed { journal } => !running && !needs_cleanup(&journal),
        Pending::Nothing | Pending::Uncommitted { .. } => false,
        Pending::Unclear { .. } => true,
    };
    if finished
        && let Err(e) = dfm::acknowledge(&f.env)
    {
        launcher_log("data folder", &format!("the move's record stays: {}", e.message));
    }
    Ok(())
}

fn folder_out(f: &Folder) -> DataFolderOut {
    let default_path = f.env.default_root().map(|p| text(&p)).unwrap_or_default();
    match f.located() {
        Ok(l) => DataFolderOut {
            path: text(&l.dirs.root),
            default_path,
            source: source_word(l.source),
            problem: None,
            notice: if matches!(f.hold(), Hold::Elsewhere | Hold::Undoing) { None } else { notice(f) },
            elsewhere: f.hold() == Hold::Elsewhere,
            undoing: undo_out(f),
            pending: if l.source == RootSource::Env { None } else { pending_sentence(f) },
        },
        Err(_) => {
            let p: &LocateProblem = f.problem().expect("a folder that is not found has a problem");
            DataFolderOut {
                path: default_path.clone(),
                default_path,
                source: "pointer",
                problem: Some(ProblemOut {
                    path: p.path.as_deref().or(p.pointer.as_deref()).map(text).unwrap_or_default(),
                    reason: p.reason.clone(),
                    kind: p.kind,
                }),
                notice: None,
                elsewhere: false,
                undoing: None,
                pending: None,
            }
        }
    }
}

fn undo_out(f: &Folder) -> Option<UndoOut> {
    if f.hold() != Hold::Undoing {
        return None;
    }
    let st = start_state();
    let Undoing::Running(task) = &st.undoing else { return None };
    Some(UndoOut { task: task.clone(), folder: st.undo_folder.as_deref().map(text).unwrap_or_default() })
}

/// Why a move would be refused with `move_pending` now, in words for the Settings row (None: no move is pending).
fn pending_sentence(f: &Folder) -> Option<String> {
    let st = start_state();
    let cleaning = matches!(st.cleaning, Cleaning::Running(_));
    let refused = st.removal.as_ref().and_then(|c| c.reason.clone());
    drop(st);
    Some(match dfm::pending(&f.env).ok()? {
        Pending::Nothing => return None,
        Pending::Committed { .. } if cleaning => "Kiln is removing the old copy of the last move: wait for it to finish.".into(),
        Pending::Committed { .. } => refused.unwrap_or_else(|| "The last move is not finished yet: Kiln finishes it at the next start.".into()),
        Pending::Uncommitted { journal } => {
            format!("Part of an unfinished copy from the last move is still in {}: Kiln removes it at the next start.", journal.to.display())
        }
        Pending::Unclear { reason, .. } => reason,
    })
}

fn located_out(l: &Located, f: &Folder) -> DataFolderOut {
    DataFolderOut {
        path: text(&l.dirs.root),
        default_path: f.env.default_root().map(|p| text(&p)).unwrap_or_default(),
        source: source_word(l.source),
        problem: None,
        notice: None,
        elsewhere: false,
        undoing: None,
        pending: None,
    }
}

// ---------------------------------------------------------------- the check

/// Another launcher holds the lock too (this process's own lock made exclusive for a moment, then shared again).
fn locked_by_another(f: &Folder) -> bool {
    let mut g = f.lock();
    match g.as_mut() {
        Some(l) => match l.upgrade() {
            Ok(true) => {
                let _ = l.downgrade();
                false
            }
            Ok(false) => true,
            Err(_) => false,
        },
        None => false,
    }
}

fn plan_out(f: &Folder, l: &Located, r: Result<df::Plan, df::Refusal>, picked: &Path) -> Result<PlanOut, Error> {
    match r {
        Err(rf) => Ok(PlanOut {
            picked: text(picked), target: text(picked), action: "refuse", reason: Some(rf.reason), code: Some(rf.code.as_str().into()),
            bytes: 0, files: 0, needed: 0, free: None, same_volume: None, hard_links: false, copy_mode: vec![], stays: vec![], warnings: vec![],
        }),
        Ok(p) => {
            let games = kiln_manage::game::known(&l.dirs).unwrap_or_default();
            let name = |id: &String| {
                games.iter().find(|g| &g.id == id).map(|g| g.path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| text(&g.path))).unwrap_or_else(|| id.clone())
            };
            let _ = f;
            Ok(PlanOut {
                picked: text(&p.picked),
                target: text(&p.target),
                action: match p.action { df::Action::Move => "move", df::Action::Use => "use" },
                reason: None,
                code: None,
                bytes: p.bytes,
                files: p.files,
                needed: p.needed,
                free: p.free,
                same_volume: p.same_volume,
                hard_links: p.hard_links,
                copy_mode: p.copy_mode_after.iter().filter(|id| !p.copy_mode_now.contains(id)).map(name).collect(),
                stays: p.unknown.iter().map(|e| EntryOut { name: e.name.clone(), bytes: e.bytes, path: text(&l.dirs.root.join(&e.name)) }).collect(),
                warnings: p.warnings.clone(),
            })
        }
    }
}

/// `data_folder_check` without the window: a running task is a refusal too (the move would refuse it).
fn check_with(f: &Folder, picked: &Path, running: &[String]) -> Result<PlanOut, Error> {
    f.dirs()?;
    let l = f.located()?.clone();
    if !running.is_empty() {
        let rf = df::Refusal::new(df::RefusalCode::Busy, still_working(running));
        return plan_out(f, &l, Err(rf), picked);
    }
    if crate::commands::nexus::other_instance(&l.dirs) {
        return plan_out(f, &l, Err(df::Refusal::new(df::RefusalCode::Locked, OTHER_LAUNCHER)), picked);
    }
    let r = df::check_here(&l, &f.env, picked, locked_by_another(f))?;
    plan_out(f, &l, r, picked)
}

fn still_working(running: &[String]) -> String {
    format!("Kiln is still working on: {}. Wait for it to finish.", running.join(", "))
}

// ---------------------------------------------------------------- holding the folder

/// Holds the data folder for a move or a switch: refused while a task runs ("Kiln is still working on: ..."), then
/// no command, task or background thread gets the folder (crate::folder), and the work that got it just before has
/// ended (`state::blocking`'s closures; at most `DRAIN` is waited).
pub fn hold_for_move(f: &Folder) -> Result<(), Error> {
    if let Ok(l) = f.located()
        && crate::commands::nexus::other_instance(&l.dirs)
    {
        return Err(df::Refusal::new(df::RefusalCode::Locked, OTHER_LAUNCHER).into_error());
    }
    begin_alone(|running| {
        if let Some(e) = f.held_error() {
            return Err(e);
        }
        if !running.is_empty() {
            return Err(busy(still_working(running)));
        }
        f.set_hold(Hold::Moving);
        Ok(())
    })?;
    let until = Instant::now() + DRAIN;
    while crate::state::in_flight() > 0 {
        if Instant::now() > until {
            release_after_failure(f);
            return Err(busy("Kiln is still working on something: try again in a moment."));
        }
        std::thread::sleep(Duration::from_millis(40));
    }
    Ok(())
}

const DRAIN: Duration = Duration::from_secs(15);

/// An older launcher (no data-folder lock) that holds `nexus/instance.lock` in a build with Nexus on.
const OTHER_LAUNCHER: &str = "Another Kiln Launcher is using the data folder: close it first.";

/// A move or a switch did not happen: everything runs again.
pub fn release_after_failure(f: &Folder) {
    if f.hold() == Hold::Moving {
        f.set_hold(Hold::Free);
    }
    flush_held_log();
}

/// The pointer names the new place: nothing of this process touches the folder again until the restart.
pub fn after_commit(f: &Folder) {
    f.set_hold(Hold::Moved);
    crate::watch::leaving();
    drop_held_log();
}

// ---------------------------------------------------------------- the move

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct CleanupOut {
    pub done: bool,
    pub interrupted: bool,
    pub folder_removed: bool,
    pub kept: Vec<KeptOut>,
    pub reason: Option<String>,
}

/// `data_folder_move`'s task result.
#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MovedOut {
    pub from: String,
    pub to: String,
    pub bytes: u64,
    pub files: u64,
    pub warnings: Vec<String>,
    /// Entries the launcher does not know: copied, and left in the old folder.
    pub stayed: Vec<EntryOut>,
    /// The removal of the old copy, the move's last phase (None: it did not run).
    pub cleanup: Option<CleanupOut>,
}

fn cleanup_out(c: &Cleanup) -> CleanupOut {
    CleanupOut { done: c.done, interrupted: c.interrupted, folder_removed: c.folder_removed, kept: kept_of(Some(c)), reason: c.reason.clone() }
}

/// The move itself, with the folder held (`hold_for_move`): the file-hash cache stops writing, `move_to` runs with
/// this process's lock; done (the pointer names the new place): nothing runs again until the restart; failed or
/// canceled (the old place is in use as it was): everything runs again.
pub(crate) fn move_with(f: &Folder, l: &Located, picked: &Path, opts: &dfm::MoveOptions, p: &mut dyn kiln_manage::task::Progress) -> Result<MovedOut, Error> {
    kiln_manage::filecache::unpersist();
    let r = {
        let mut g = f.lock();
        dfm::move_to(l, &f.env, picked, &RealFacts { dirs: &l.dirs }, g.as_mut(), opts, p)
    };
    match r {
        Ok(m) => {
            after_commit(f);
            eprintln!("data folder moved from {} to {}", m.from.display(), m.to.display());
            Ok(MovedOut {
                from: text(&m.from),
                to: text(&m.to),
                bytes: m.bytes,
                files: m.files,
                warnings: m.warnings.clone(),
                stayed: m.stayed.iter().map(|s| EntryOut { name: s.name.clone(), bytes: s.bytes, path: text(&s.path) }).collect(),
                cleanup: m.cleanup.as_ref().map(cleanup_out),
            })
        }
        Err(e) => {
            release_after_failure(f);
            kiln_manage::filecache::persist_at(&crate::state::file_hashes(&l.dirs));
            Err(e)
        }
    }
}

// ---------------------------------------------------------------- use, reset

fn use_with(f: &Folder, picked: &Path) -> Result<DataFolderOut, Error> {
    if f.problem().is_some() {
        // a broken pointer: there is no folder in use; the pointer is written (nothing else), then the restart
        let default = kiln_manage::paths::DataDirs::at(f.env.default_root()?);
        let now = dfm::repoint(&f.env, picked, &RealFacts { dirs: &default })?;
        f.set_hold(Hold::Moved);
        return Ok(located_out(&now, f));
    }
    let l = f.located()?.clone();
    hold_for_move(f)?;
    let r = {
        let mut g = f.lock();
        dfm::use_folder(&l, &f.env, picked, &RealFacts { dirs: &l.dirs }, g.as_mut())
    };
    match r {
        Ok(now) => {
            after_commit(f);
            launcher_log("data folder", &format!("switched to {}", now.dirs.root.display()));
            Ok(located_out(&now, f))
        }
        Err(e) => {
            release_after_failure(f);
            Err(e)
        }
    }
}

fn reset_with(f: &Folder) -> Result<DataFolderOut, Error> {
    if f.problem().is_none() {
        return Err(Error::new(ErrorCode::Refused, "The data folder's pointer is fine: start fresh only after a broken one."));
    }
    let now = dfm::reset(&f.env)?;
    f.set_hold(Hold::Moved);
    Ok(located_out(&now, f))
}

// ---------------------------------------------------------------- commands

async fn off_thread<T: Send + 'static>(work: impl FnOnce() -> Result<T, Error> + Send + 'static) -> Result<T, Error> {
    // not state::blocking: a move waits for those to end, and these are the move's own steps
    tauri::async_runtime::spawn_blocking(work).await.map_err(|e| Error::new(ErrorCode::Internal, format!("worker failed: {e}")))?
}

#[tauri::command]
pub async fn data_folder_get() -> Result<DataFolderOut, Error> {
    off_thread(|| Ok(folder_out(&folder::current()))).await
}

#[tauri::command]
pub async fn data_folder_size() -> Result<SizeOut, Error> {
    crate::state::blocking(|| {
        let f = folder::current();
        let d = f.dirs()?;
        let inv = df::inventory(&d.root, &f.env)?;
        Ok(SizeOut { bytes: inv.bytes, files: inv.files })
    })
    .await
}

#[tauri::command]
pub async fn data_folder_check(path: String) -> Result<PlanOut, Error> {
    crate::state::blocking(move || {
        let running = begin_alone(|r| r.to_vec());
        check_with(&folder::current(), Path::new(&path), &running)
    })
    .await
}

/// Task result: [`MovedOut`]. The folder is held first (refused while a task runs); the task copies, verifies,
/// switches and removes the old copy (`data_folder_move::move_to`); then the restart (the front end calls it).
#[tauri::command]
pub async fn data_folder_move(app: AppHandle, path: String) -> Result<super::TaskId, Error> {
    let f = folder::current();
    off_thread(move || {
        let l = f.located()?.clone();
        hold_for_move(&f)?;
        Ok(spawn_task(&app, "data_folder_move", "Move the data folder".into(), move |p| to_value(move_with(&f, &l, Path::new(&path), &dfm::MoveOptions::default(), p)?)))
    })
    .await
}

#[tauri::command]
pub async fn data_folder_use(path: String) -> Result<DataFolderOut, Error> {
    off_thread(move || use_with(&folder::current(), &PathBuf::from(path))).await
}

#[tauri::command]
pub async fn data_folder_reset() -> Result<DataFolderOut, Error> {
    off_thread(|| reset_with(&folder::current())).await
}

#[tauri::command]
pub async fn data_folder_notice_ack() -> Result<(), Error> {
    off_thread(|| notice_ack_with(&folder::current())).await
}

/// Restarts the launcher: the watcher stops, the lock goes, then Tauri's restart (in every build). Does not return.
#[tauri::command]
pub async fn data_folder_restart(app: AppHandle) -> Result<(), Error> {
    crate::watch::leaving();
    let f = folder::current();
    if f.hold() != Hold::Moved {
        f.set_hold(Hold::Leaving);
    }
    f.release_lock();
    launcher_log("data folder", "restarting");
    app.restart()
}

#[cfg(test)]
pub(crate) mod tests {
    use super::*;
    use kiln_manage::data_folder::DataLock;
    use kiln_manage::paths::DataEnv;
    use std::collections::BTreeSet;
    use std::sync::Arc;

    /// A scratch folder for one test (under TMPDIR), and an environment whose default root is `<it>/xdg/kiln` (nothing
    /// of the real launcher data: no HOME, no KILN_DATA_DIR).
    pub(crate) fn scratch(name: &str) -> (PathBuf, DataEnv) {
        let t = std::env::temp_dir().join(format!("kl-df-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&t);
        std::fs::create_dir_all(&t).unwrap();
        let env = DataEnv {
            home: Some(t.join("home").into()),
            xdg_data_home: Some(t.join("xdg").into()),
            xdg_state_home: Some(t.join("state").into()),
            xdg_config_home: Some(t.join("config").into()),
            ..Default::default()
        };
        (t, env)
    }

    /// A data folder the crate recognises by its layout (`library/<kind>/`, `settings.json`).
    pub(crate) fn data_folder(root: &Path) {
        std::fs::create_dir_all(root.join("library/kiln")).unwrap();
        std::fs::create_dir_all(root.join("profiles")).unwrap();
        std::fs::write(root.join("settings.json"), "{\"savesKeep\": 5}\n").unwrap();
    }

    /// Every path below `t` (files and folders).
    pub(crate) fn tree(t: &Path) -> BTreeSet<String> {
        fn go(d: &Path, base: &Path, out: &mut BTreeSet<String>) {
            for e in std::fs::read_dir(d).into_iter().flatten().flatten() {
                out.insert(e.path().strip_prefix(base).unwrap().display().to_string());
                if e.file_type().is_ok_and(|f| f.is_dir()) {
                    go(&e.path(), base, out);
                }
            }
        }
        let mut out = BTreeSet::new();
        go(t, t, &mut out);
        out
    }

    #[test]
    fn a_start_with_a_broken_pointer_creates_nothing_and_answers_only_its_commands() {
        let _g = folder::test_serial();
        let (t, env) = scratch("broken");
        let default = env.default_root().unwrap();
        std::fs::create_dir_all(&default).unwrap();
        df::write_pointer(&default, &t.join("usb/Kiln")).unwrap();
        let before = tree(&t);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        assert!(f.problem().is_some());
        assert!(f.lock().is_none(), "no lock taken");
        // start-up stops before any watcher, and the gate refuses every command but the five (and app_info)
        assert_eq!(start_with(&f), Next::Nothing);
        for name in crate::commands::NAMES {
            let refused = f.command_refusal(name);
            if folder::OPEN_WHEN_BROKEN.contains(name) {
                assert!(refused.is_none(), "{name} answers");
            } else {
                assert_eq!(refused.map(|e| e.code), Some(ErrorCode::DataFolder), "{name} is refused with the crate's error");
            }
        }
        assert_eq!(OPEN_COUNT_BROKEN, folder::OPEN_WHEN_BROKEN.len());
        // what data_folder_get says, and every way the process reaches the folder
        let out = folder_out(&f);
        assert_eq!(out.problem.as_ref().map(|p| p.path.clone()), Some(text(&t.join("usb/Kiln"))));
        assert_eq!(out.path, text(&default));
        assert_eq!(crate::state::dirs().unwrap_err().code, ErrorCode::DataFolder);
        crate::commands::app::launcher_log("test", "a line that has nowhere to go");
        assert!(crate::state::read_settings().is_err());
        let info = tauri::async_runtime::block_on(async { crate::folder::current().located().map(|l| l.dirs.root.clone()) });
        assert!(info.is_err());
        assert_eq!(tree(&t), before, "nothing was created anywhere");
        let _ = std::fs::remove_dir_all(&t);
    }

    const OPEN_COUNT_BROKEN: usize = 6;

    #[test]
    fn the_folder_is_refused_during_a_move_and_free_again_after_it() {
        let _g = folder::test_serial();
        let (t, env) = scratch("hold");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let root = f.located().unwrap().dirs.root.clone();
        assert!(root.is_dir(), "the default root is made, as before dev.25");
        assert!(f.lock().is_some(), "and its lock taken shared");
        let settings = root.join("settings.json");
        let set = |keep: u32| {
            let s = crate::commands::app::LauncherSettings { saves_keep: keep, ..Default::default() };
            tauri::async_runtime::block_on(crate::commands::app::app_settings_set(s))
        };
        set(3).unwrap();
        let written = std::fs::read(&settings).unwrap();

        hold_for_move(&f).unwrap();
        // every way in: the choke point, a writing command, the gate in front of the commands, a task's start
        let e = crate::state::dirs().unwrap_err();
        assert_eq!((e.code, e.message.as_str()), (ErrorCode::Busy, folder::MOVING_HERE));
        assert_eq!(set(9).unwrap_err().code, ErrorCode::Busy);
        assert_eq!(std::fs::read(&settings).unwrap(), written, "the settings file is as it was");
        assert!(f.command_refusal("app_settings_set").is_some());
        assert!(f.command_refusal("library_import").is_some());
        assert!(f.command_refusal("task_cancel").is_none(), "the move's own Cancel answers");
        crate::commands::app::launcher_log("test", "held back during the move");
        // (other tests in this process may write the log of the folder in use: look for the line, not the file)
        assert!(!std::fs::read_to_string(root.join("logs/launcher.log")).unwrap_or_default().contains("held back during the move"), "the log waits");
        assert_eq!(hold_for_move(&f).unwrap_err().code, ErrorCode::Busy, "one move at a time");

        // a failed or canceled move: everything runs again, the held line is written
        release_after_failure(&f);
        set(4).unwrap();
        assert!(std::fs::read_to_string(root.join("logs/launcher.log")).unwrap().contains("held back during the move"));
        assert!(f.command_refusal("app_settings_set").is_none());

        // a committed one: nothing runs again until the restart
        hold_for_move(&f).unwrap();
        after_commit(&f);
        assert_eq!(crate::state::dirs().unwrap_err().message, folder::MOVED);
        assert_eq!(set(5).unwrap_err().code, ErrorCode::Busy);
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn another_launchers_exclusive_lock_is_seen() {
        let _g = folder::test_serial();
        let (t, env) = scratch("elsewhere");
        let root = env.default_root().unwrap();
        data_folder(&root);
        // another launcher's move holds the lock exclusively
        let other = DataLock::exclusive(&root).unwrap().unwrap();
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        assert_eq!(f.hold(), Hold::Elsewhere);
        assert!(f.lock().is_none());
        assert_eq!(crate::state::dirs().unwrap_err().message, folder::MOVING_ELSEWHERE);
        for name in crate::commands::NAMES {
            assert_eq!(f.command_refusal(name).is_none(), folder::OPEN_WHEN_ELSEWHERE.contains(name), "{name}");
        }
        assert!(folder_out(&f).elsewhere);
        assert_eq!(start_with(&f), Next::Nothing);
        drop(other);
        // a second launcher of dev.25 holds it shared: a move here is refused by the crate, the check says so
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        assert_eq!(f.hold(), Hold::Free);
        let second = DataLock::shared(&root).unwrap().unwrap();
        assert!(locked_by_another(&f));
        drop(second);
        assert!(!locked_by_another(&f));
        assert!(f.lock().as_ref().is_some_and(|l| !l.is_exclusive()), "the check leaves this process's lock shared");
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn use_this_one_switches_and_then_nothing_runs_until_the_restart() {
        let _g = folder::test_serial();
        let (t, env) = scratch("use");
        let default = env.default_root().unwrap();
        data_folder(&default);
        let other = t.join("disk/Kiln");
        data_folder(&other);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let plan = check_with(&f, &other, &[]).unwrap();
        assert_eq!((plan.action, plan.target.clone()), ("use", text(&other)), "{plan:?}");
        // a running task refuses, with its title
        let busy = check_with(&f, &other, &["Install Kiln".into()]).unwrap();
        assert_eq!((busy.action, busy.code.as_deref()), ("refuse", Some("busy")));
        assert_eq!(busy.reason.as_deref(), Some("Kiln is still working on: Install Kiln. Wait for it to finish."));
        let out = use_with(&f, &other).unwrap();
        assert_eq!((out.path.as_str(), out.source), (text(&other).as_str(), "pointer"));
        assert_eq!(f.hold(), Hold::Moved);
        assert!(crate::state::dirs().is_err());
        // the next start (a new value over the same folders) finds the other folder
        let next = Folder::open(env.clone());
        assert_eq!(next.located().unwrap().dirs.root, other);
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn a_refusal_keeps_the_crates_sentence_and_code() {
        let _g = folder::test_serial();
        let (t, env) = scratch("refuse");
        let default = env.default_root().unwrap();
        data_folder(&default);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let inside = default.join("library");
        let p = check_with(&f, &inside, &[]).unwrap();
        assert_eq!((p.action, p.code.as_deref()), ("refuse", Some("inside_current")));
        assert!(p.reason.as_deref().unwrap().contains("is inside the current data folder"), "{p:?}");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// A Progress that tries a writing command while the move copies, and cancels the removal of the old copy after
    /// `cancel_after` of its files.
    struct Watch {
        tried: bool,
        removing: u32,
        cancel_after: Option<u32>,
        messages: Vec<String>,
    }

    impl kiln_manage::task::Progress for Watch {
        fn report(&mut self, _f: Option<f32>, m: &str) {
            if m.starts_with("copying") && !self.tried {
                self.tried = true;
                let s = crate::commands::app::LauncherSettings { saves_keep: 9, ..Default::default() };
                let e = tauri::async_runtime::block_on(crate::commands::app::app_settings_set(s)).unwrap_err();
                assert_eq!(e.code, ErrorCode::Busy, "a writing command during the move is refused");
                assert!(crate::state::dirs().is_err() && crate::commands::app::begin_alone(|_| folder::current().held_error()).is_some());
            }
            if m.starts_with("removing the old copy") {
                self.removing += 1;
            }
            self.messages.push(m.to_string());
        }
        fn canceled(&self) -> bool {
            self.cancel_after.is_some_and(|n| self.removing >= n)
        }
    }

    /// The default root as a data folder with `n` backup files, and a target on the same disk.
    fn moving(name: &str, n: usize) -> (PathBuf, DataEnv, PathBuf, PathBuf) {
        let (t, env) = scratch(name);
        let from = env.default_root().unwrap();
        data_folder(&from);
        for i in 0..n {
            let p = from.join(format!("backups/old/file-{i:03}.bin"));
            std::fs::create_dir_all(p.parent().unwrap()).unwrap();
            std::fs::write(&p, format!("backup {i}").repeat(50)).unwrap();
        }
        let to = t.join("disk/Kiln");
        (t, env, from, to)
    }

    #[cfg(unix)]
    #[test]
    fn a_failed_rollback_keeps_the_committed_move_held_for_restart() {
        use std::os::unix::fs::PermissionsExt;
        struct DenyRollback(PathBuf);
        impl kiln_manage::task::Progress for DenyRollback {
            fn report(&mut self, _: Option<f32>, m: &str) {
                if m == "checking the new place after the switch" {
                    std::fs::set_permissions(&self.0, std::fs::Permissions::from_mode(0o500)).unwrap();
                }
            }
            fn canceled(&self) -> bool { false }
        }
        let _g = folder::test_serial();
        let (t, env, from, to) = moving("rollback-denied", 3);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        let before: Vec<_> = (0..3).map(|i| std::fs::read(from.join(format!("backups/old/file-{i:03}.bin"))).unwrap()).collect();
        let opts = dfm::MoveOptions { force_copy: true, inject: Some(dfm::Inject::FailCheckAfterCommit) };
        let result = move_with(&f, &l, &to, &opts, &mut DenyRollback(from.clone()));
        std::fs::set_permissions(&from, std::fs::Permissions::from_mode(0o700)).unwrap();
        assert_eq!(env.locate().unwrap().dirs.root, to, "the rollback really failed: the pointer names the new folder");
        assert_eq!(f.hold(), Hold::Moved, "never resume the old root after a committed failure: {result:?}");
        let m = result.expect("a committed move carries its warning to the restart dialog, not Move failed");
        assert!(m.warnings.iter().any(|w| w.contains("Restart Kiln") && w.contains(&text(&to))), "{m:?}");
        assert!(m.cleanup.as_ref().is_some_and(|c| !c.done && c.reason.is_some()));
        for (i, bytes) in before.iter().enumerate() {
            assert_eq!(&std::fs::read(from.join(format!("backups/old/file-{i:03}.bin"))).unwrap(), bytes, "the old data stays complete");
        }
        assert!(to.join("backups/old/file-000.bin").is_file());
        assert!(f.command_refusal("app_settings_set").is_some());
        assert!(f.command_refusal("data_folder_restart").is_none());
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn a_move_through_the_command_layer_then_the_notice_after_the_restart_until_the_ack() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, to) = moving("move", 5);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        let plan = check_with(&f, &to, &[]).unwrap();
        assert_eq!(plan.action, "move", "{plan:?}");
        hold_for_move(&f).unwrap();
        let mut w = Watch { tried: false, removing: 0, cancel_after: None, messages: vec![] };
        let m = move_with(&f, &l, &to, &dfm::MoveOptions::default(), &mut w).unwrap();
        assert!(w.tried, "the move copied: {:?}", w.messages);
        assert_eq!(m.to, text(&to));
        assert!(m.cleanup.as_ref().is_some_and(|c| c.done), "{m:?}");
        // committed: nothing of this process runs again
        assert_eq!(f.hold(), Hold::Moved);
        assert_eq!(crate::state::dirs().unwrap_err().message, folder::MOVED);
        assert!(f.command_refusal("library_import").is_some() && f.command_refusal("data_folder_restart").is_none());

        // the restart: a new process over the same folders
        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        assert_eq!(f2.located().unwrap().dirs.root, to);
        assert_eq!(start_with(&f2), Next::Cleanup, "a committed move not acknowledged: the removal task");
        let n = notice(&f2).expect("the notice");
        assert_eq!((n.kind, n.from.as_str(), n.to.as_str(), n.removing), ("moved", text(&from).as_str(), text(&to).as_str(), true));
        cleanup_work(&f2, &mut kiln_manage::task::NoProgress).unwrap();
        let n = folder_out(&f2).notice.expect("the notice after the removal");
        assert!(!n.removing && !n.unfinished, "{n:?}");
        notice_ack_with(&f2).unwrap();
        assert_eq!(folder_out(&f2).notice, None);
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Nothing), "the journal is acknowledged");
        // and the next start has nothing to say
        forget_start();
        let f3 = Arc::new(Folder::open(env.clone()));
        assert_eq!(start_with(&f3), Next::Nothing);
        assert_eq!(folder_out(&f3).notice, None);
        assert!(!from.join("backups/old/file-000.bin").exists(), "the old copy is gone");
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn a_removal_canceled_halfway_goes_on_at_the_next_start() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, to) = moving("halfway", 200);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        // the crate reports the removal every 64 files: a cancel after its first report stops it near the start
        let mut w = Watch { tried: true, removing: 0, cancel_after: Some(1), messages: vec![] };
        let m = move_with(&f, &l, &to, &dfm::MoveOptions::default(), &mut w).unwrap();
        let c = m.cleanup.clone().expect("the removal ran");
        assert!(c.interrupted && !c.done, "a cancel there interrupts the removal only: {c:?}");
        assert_eq!(f.hold(), Hold::Moved, "the move itself is done");
        let left = |d: &Path| std::fs::read_dir(d.join("backups/old")).map(|r| r.count()).unwrap_or(0);
        let before = left(&from);
        assert!(before > 0 && before < 200, "halfway: {before} files left");

        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        let n = notice(&f2).expect("the notice");
        assert!(n.unfinished, "before the next removal it says the rest is still there: {n:?}");
        // the ack while unfinished keeps the journal: the next start goes on
        notice_ack_with(&f2).unwrap();
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Committed { .. }));
        forget_start();
        let f3 = Arc::new(Folder::open(env.clone()));
        folder::replace(f3.clone());
        assert_eq!(start_with(&f3), Next::Cleanup);
        let c = cleanup_work(&f3, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(c.done, "{c:?}");
        assert_eq!(left(&from), 0);
        assert_eq!(std::fs::read_dir(to.join("backups/old")).unwrap().count(), 200, "the new place has all of them");
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn a_failed_move_leaves_everything_running_and_the_cache_persisting() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, _to) = moving("fail", 2);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        // a folder the crate refuses: the move fails before anything is copied
        let e = move_with(&f, &l, &from.join("library"), &dfm::MoveOptions::default(), &mut kiln_manage::task::NoProgress).unwrap_err();
        assert_eq!(e.code, ErrorCode::Refused, "{e:?}");
        assert_eq!(f.hold(), Hold::Free);
        assert!(crate::state::dirs().is_ok());
        let _ = std::fs::remove_dir_all(&t);
    }

    #[test]
    fn choose_folder_after_a_broken_pointer_writes_only_the_pointer() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env) = scratch("repoint");
        let default = env.default_root().unwrap();
        std::fs::create_dir_all(&default).unwrap();
        df::write_pointer(&default, &t.join("gone/Kiln")).unwrap();
        let mine = t.join("disk/Kiln");
        data_folder(&mine);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let before: BTreeSet<String> = tree(&default);
        let out = use_with(&f, &mine).unwrap();
        assert_eq!(out.path, text(&mine));
        assert_eq!(tree(&default), before, "only the pointer changed (the same file names)");
        assert_eq!(Folder::open(env.clone()).located().unwrap().dirs.root, mine);
        let _ = std::fs::remove_dir_all(&t);
    }

    /// A launcher before dev.25 (or another data folder) activated its profile in the game: game_status says so and
    /// does not call it loaded; profile_active gives none.
    #[test]
    #[cfg(unix)]
    fn another_data_folders_profile_in_the_game_is_said_not_loaded() {
        use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
        use kiln_manage::{paths::DataDirs, profile};
        let _g = folder::test_serial();
        let (t, env) = scratch("other-profile");
        let here = env.default_root().unwrap();
        data_folder(&here);
        // the other data folder (the default root a dev.24 launcher used after a move) and its profile in the game
        let theirs = DataDirs::at(t.join("old-default/kiln"));
        data_folder(&theirs.root);
        let p = profile::create(&theirs, "Old launcher", None).unwrap();
        let path = t.join("game");
        std::fs::create_dir_all(path.join("kiln")).unwrap();
        std::fs::write(path.join("enshrouded.exe"), b"fixture").unwrap();
        let prefix = t.join("pfx");
        std::fs::create_dir_all(prefix.join("dosdevices")).unwrap();
        std::os::unix::fs::symlink("/", prefix.join("dosdevices/z:")).unwrap();
        let g = GameInstall { id: "other-profile".into(), kind: GameKind::Client, path, exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(prefix),
            build_id: None, exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into() };
        profile::activate(&theirs, &p.id, &g).unwrap();
        kiln_manage::game::write_record(&DataDirs::at(&here), &g, None).unwrap();
        folder::replace(Arc::new(Folder::open(env.clone())));
        let st = crate::state::game_status(&g).unwrap();
        assert_eq!(st.active_profile, None);
        let o = st.other_folder_profile.clone().expect("another data folder's profile");
        assert_eq!(o.id, p.id);
        assert_eq!(o.root.as_deref().map(df::canonical), Some(df::canonical(&theirs.root)));
        let json = serde_json::to_value(&st).unwrap();
        assert_eq!(json["otherFolderProfile"]["id"], p.id.as_str());
        let active = tauri::async_runtime::block_on(crate::commands::profile::profile_active(g.id.clone())).unwrap();
        assert!(active.is_none(), "never shown as loaded here: {active:?}");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// A move that fails and cannot remove a file it made (the crate's `HeldDuring`): the old place stays in use, the
    /// next start removes the rest; while that removal cannot finish either, the notice names what is left, the ack
    /// hides it for this process and keeps the journal, and the start after that finishes.
    #[test]
    #[cfg(unix)]
    fn a_failed_move_that_left_a_file_is_finished_by_a_later_start() {
        use std::os::unix::fs::PermissionsExt;
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, to) = moving("held", 6);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        let opts = dfm::MoveOptions { force_copy: true, inject: Some(dfm::Inject::HeldDuring(3)) };
        let e = move_with(&f, &l, &to, &opts, &mut kiln_manage::task::NoProgress).unwrap_err();
        assert!(e.message.contains("could not remove it again"), "the crate's sentence names what is left: {e:?}");
        assert_eq!(f.hold(), Hold::Free, "nothing changed: everything runs again");
        let c: Cleanup = serde_json::from_value(e.detail.clone().unwrap()).unwrap();
        let left = c.kept[0].path.clone();
        assert!(left.is_file());

        // the next start cannot remove it either (a read-only folder here)
        let dir = left.parent().unwrap().to_path_buf();
        std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o555)).unwrap();
        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        assert_eq!(f2.located().unwrap().dirs.root, from, "the old place is in use");
        assert_eq!(start_with(&f2), Next::Undo, "the start holds the folder for the task");
        assert_eq!(f2.hold(), Hold::Undoing);
        let _ = undo_work(&f2, &mut kiln_manage::task::NoProgress);
        assert_eq!(f2.hold(), Hold::Free, "the hold ends with the task");
        let n = notice(&f2).expect("the notice of the undone move");
        assert_eq!(n.kind, "undone");
        assert!(n.kept.iter().any(|k| left.starts_with(&k.path)), "it names what is left (the file or the entry that holds it): {n:?}");
        assert!(n.reason.as_deref().is_some_and(|r| r.contains("stays marked")), "{n:?}");
        notice_ack_with(&f2).unwrap();
        assert_eq!(notice(&f2), None, "the ack hides it for this process");
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Uncommitted { .. }), "and the journal stays");
        std::fs::set_permissions(&dir, std::fs::Permissions::from_mode(0o755)).unwrap();

        // the start after that removes the rest
        forget_start();
        let f3 = Arc::new(Folder::open(env.clone()));
        folder::replace(f3.clone());
        assert_eq!(start_with(&f3), Next::Undo);
        undo_work(&f3, &mut kiln_manage::task::NoProgress).unwrap();
        assert_eq!(notice(&f3).map(|n| n.kind), Some("undone"));
        assert!(!left.exists() && !to.exists(), "the partial copy is gone");
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Nothing));
        notice_ack_with(&f3).unwrap();
        let _ = std::fs::remove_dir_all(&t);
    }

    /// After a move, a launcher before dev.25 at the old default root activates its own profile in a game the moved
    /// folder knows: the removal at the next start is refused with the crate's reason (the journal keeps no result),
    /// the notice shows that reason instead of "the rest goes at the next start", and the ack keeps the journal.
    #[test]
    #[cfg(unix)]
    fn a_removal_refused_because_a_game_names_the_old_folder_says_why() {
        use kiln_manage::game::{GameInstall, GameKind, PathMode, Platform};
        use kiln_manage::{paths::DataDirs, profile};
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, to) = moving("named", 3);
        let path = t.join("game");
        std::fs::create_dir_all(path.join("kiln")).unwrap();
        std::fs::write(path.join("enshrouded.exe"), b"fixture").unwrap();
        let prefix = t.join("pfx");
        std::fs::create_dir_all(prefix.join("dosdevices")).unwrap();
        std::os::unix::fs::symlink("/", prefix.join("dosdevices/z:")).unwrap();
        let g = GameInstall { id: "named-game".into(), kind: GameKind::Client, path, exe: "enshrouded.exe".into(),
            app_id: kiln_manage::CLIENT_APP_ID, library: None, platform: Platform::Proton, prefix: Some(prefix),
            build_id: None, exe_sha256: None, steam_flavor: None, path_mode: PathMode::Z, source: "manual".into() };
        kiln_manage::game::write_record(&DataDirs::at(&from), &g, None).unwrap();
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        // the move's own removal is stopped, so the old copy is still there at the next start
        let mut w = Watch { tried: true, removing: 0, cancel_after: Some(1), messages: vec![] };
        move_with(&f, &l, &to, &dfm::MoveOptions::default(), &mut w).unwrap();
        // a launcher before dev.25 runs at the old default root as on a first run and activates its profile there
        let old = DataDirs::at(&from);
        let p = profile::create(&old, "Old launcher", None).unwrap();
        profile::activate(&old, &p.id, &g).unwrap();

        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        assert_eq!(start_with(&f2), Next::Cleanup);
        let c = cleanup_work(&f2, &mut kiln_manage::task::NoProgress).unwrap();
        let reason = c.reason.clone().expect("refused with the crate's reason");
        let n = notice(&f2).expect("the notice");
        assert_eq!(n.reason.as_deref(), Some(reason.as_str()), "{n:?}");
        assert!(!n.unfinished, "not \"the rest goes at the next start\": {n:?}");
        notice_ack_with(&f2).unwrap();
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Committed { .. }), "the journal stays for a later removal");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// The mechanism behind the writers outside `state::blocking` (state::writing): a move waits for one that got the
    /// folder before its hold, and one that asks after the hold is refused.
    #[test]
    fn a_move_waits_for_a_writer_that_got_the_folder_first() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env) = scratch("writer");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        // a writer that got the folder before the hold
        let w = crate::state::writing();
        let d = crate::state::dirs().unwrap();
        let (tx, rx) = std::sync::mpsc::channel();
        let f2 = f.clone();
        let mover = std::thread::spawn(move || {
            let r = hold_for_move(&f2);
            tx.send(()).unwrap();
            r
        });
        std::thread::sleep(Duration::from_millis(400));
        assert!(rx.try_recv().is_err(), "the move waits while the writer works");
        assert_eq!(f.hold(), Hold::Moving, "and nobody else gets the folder meanwhile");
        let late = crate::state::writing();
        assert!(crate::state::dirs().is_err(), "a writer that asks after the hold is refused");
        drop(late);
        std::fs::write(d.root.join("settings.json"), "{}").unwrap();
        drop(w);
        mover.join().unwrap().unwrap();
        assert!(rx.try_recv().is_ok());
        release_after_failure(&f);
        let _ = std::fs::remove_dir_all(&t);
    }

    /// A move that "died" after its copy left a marked partial copy; a file the move never made was added to it. The
    /// next start removes nothing synchronously: it holds the folder and leaves the recovery to a task (review F5, and
    /// the verifier's reproduction).
    #[test]
    fn recovery_start_does_not_remove_an_unrecorded_addition() {
        let _g = folder::test_serial();
        forget_start();
        let (t, env, from, to) = moving("unrecorded", 4);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        let opts = dfm::MoveOptions { force_copy: true, inject: Some(dfm::Inject::StopAfter(dfm::Step::Copy)) };
        let _ = move_with(&f, &l, &to, &opts, &mut kiln_manage::task::NoProgress);
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Uncommitted { .. }), "an unfinished move");
        let added = to.join("backups/added-after-crash.bin");
        std::fs::create_dir_all(added.parent().unwrap()).unwrap();
        std::fs::write(&added, b"not the move's").unwrap();

        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        assert_eq!(f2.located().unwrap().dirs.root, from);
        let scheduled = start_with(&f2);
        assert!(added.is_file(), "startup synchronously deleted an unrecorded addition (scheduled={scheduled:?})");
        assert_eq!(scheduled, Next::Undo, "the recovery is a task");
        assert_eq!(f2.hold(), Hold::Undoing, "and the folder is held for it");

        // the task itself keeps it too: named with the crate's reason, the journal stays, the notice lists it
        let c = undo_work(&f2, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(added.is_file(), "the recovery task deleted an unrecorded addition");
        assert!(!c.done, "{c:?}");
        let k = c.kept.iter().find(|k| k.path == added).expect("the addition is named");
        assert_eq!(k.reason, dfm::partial_reason::NO_SUCH_FILE, "{k:?}");
        assert_eq!(f2.hold(), Hold::Free);
        let n = notice(&f2).expect("the notice");
        assert!(n.kept.iter().any(|k| k.path == text(&added)), "{n:?}");
        notice_ack_with(&f2).unwrap();
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Uncommitted { .. }), "the next start tries again");

        // the player takes the named file out: the next start finishes and drops the journal
        std::fs::remove_file(&added).unwrap();
        forget_start();
        let f3 = Arc::new(Folder::open(env.clone()));
        folder::replace(f3.clone());
        assert_eq!(start_with(&f3), Next::Undo);
        let c = undo_work(&f3, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(c.done, "{c:?}");
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Nothing));
        assert!(!to.exists(), "the unfinished copy is gone");
        let _ = std::fs::remove_dir_all(&t);
    }

    /// Cancel: the task ends at once with the hold (the launcher mounts on the folder in use), the notice says the rest
    /// goes at the next start, and the next start's task goes on and finishes.
    #[test]
    fn a_canceled_recovery_goes_on_at_the_next_start() {
        struct Cancel;
        impl kiln_manage::task::Progress for Cancel {
            fn report(&mut self, _: Option<f32>, _: &str) {}
            fn canceled(&self) -> bool {
                true
            }
        }
        let _g = folder::test_serial();
        let (t, env, from, to) = unfinished("undo-cancel");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let ran = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let r = ran.clone();
        assert_eq!(begin(&f, Box::new(move || { r.fetch_add(1, std::sync::atomic::Ordering::SeqCst); })), Next::Undo);
        let c = undo_work(&f, &mut Cancel).unwrap();
        assert!(c.interrupted && !c.done, "{c:?}");
        assert_eq!(f.hold(), Hold::Free, "the launcher is usable on the folder in use");
        assert_eq!(ran.load(std::sync::atomic::Ordering::SeqCst), 1, "and its start-up goes on");
        assert_eq!(crate::state::dirs().unwrap().root, from);
        let n = folder_out(&f).notice.expect("the notice");
        assert!(n.kind == "undone" && n.unfinished, "{n:?}");
        notice_ack_with(&f).unwrap();
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Uncommitted { .. }), "the journal stays");
        assert!(to.exists(), "the rest is still there");

        forget_start();
        let f2 = Arc::new(Folder::open(env.clone()));
        folder::replace(f2.clone());
        assert_eq!(begin(&f2, Box::new(|| {})), Next::Undo);
        let c = undo_work(&f2, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(c.done, "the next start finishes: {c:?}");
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Nothing));
        let _ = std::fs::remove_dir_all(&t);
    }

    /// An unfinished move from a former start, its partial copy left: the folder to start on, and its environment.
    fn unfinished(name: &str) -> (PathBuf, DataEnv, PathBuf, PathBuf) {
        let (t, env, from, to) = moving(name, 4);
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let l = f.located().unwrap().clone();
        hold_for_move(&f).unwrap();
        let opts = dfm::MoveOptions { force_copy: true, inject: Some(dfm::Inject::StopAfter(dfm::Step::Copy)) };
        let _ = move_with(&f, &l, &to, &opts, &mut kiln_manage::task::NoProgress);
        assert!(matches!(dfm::pending(&env).unwrap(), Pending::Uncommitted { .. }));
        forget_start();
        (t, env, from, to)
    }

    /// During the recovery every writer is refused (a command, the log is held back); when it ends the held lines are
    /// written and the writer runs; the start-up steps that were put off run once, as on a start without a recovery.
    #[test]
    fn the_recovery_holds_the_folder_and_the_start_goes_on_after_it() {
        let _g = folder::test_serial();
        // a normal start: the steps run at once
        let (t0, env0) = scratch("normal-start");
        data_folder(&env0.default_root().unwrap());
        let f0 = Arc::new(Folder::open(env0));
        folder::replace(f0.clone());
        forget_start();
        let ran = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let r = ran.clone();
        assert_eq!(begin(&f0, Box::new(move || { r.fetch_add(1, std::sync::atomic::Ordering::SeqCst); })), Next::Nothing);
        assert_eq!(ran.load(std::sync::atomic::Ordering::SeqCst), 1);

        // a start with an unfinished move: held, the steps wait
        let (t, env, from, _to) = unfinished("held-start");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        let ran = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let r = ran.clone();
        assert_eq!(begin(&f, Box::new(move || { r.fetch_add(1, std::sync::atomic::Ordering::SeqCst); })), Next::Undo);
        assert_eq!(ran.load(std::sync::atomic::Ordering::SeqCst), 0, "nothing that uses the folder runs during the recovery");
        assert_eq!(crate::state::dirs().unwrap_err().message, folder::UNDOING);
        let set = |keep: u32| {
            let s = crate::commands::app::LauncherSettings { saves_keep: keep, ..Default::default() };
            tauri::async_runtime::block_on(crate::commands::app::app_settings_set(s))
        };
        assert_eq!(set(6).unwrap_err().code, ErrorCode::Busy, "a writer is refused");
        assert!(f.command_refusal("library_import").is_some() && f.command_refusal("task_cancel").is_none());
        crate::commands::app::launcher_log("test", "held back during the recovery");
        assert!(!std::fs::read_to_string(from.join("logs/launcher.log")).unwrap_or_default().contains("held back during the recovery"), "the log waits");
        let out = folder_out(&f);
        assert!(out.undoing.as_ref().is_some_and(|u| u.task.is_none() && !u.folder.is_empty()), "{out:?}");
        assert!(out.notice.is_none());

        // the task: done
        let c = undo_work(&f, &mut kiln_manage::task::NoProgress).unwrap();
        assert!(c.done, "{c:?}");
        assert_eq!(f.hold(), Hold::Free);
        assert_eq!(ran.load(std::sync::atomic::Ordering::SeqCst), 1, "the start-up steps ran once, after it");
        assert!(std::fs::read_to_string(from.join("logs/launcher.log")).unwrap().contains("held back during the recovery"));
        set(6).unwrap();
        let out = folder_out(&f);
        assert_eq!(out.undoing, None);
        assert_eq!(out.notice.map(|n| n.kind), Some("undone"));
        assert_eq!(out.pending, None, "nothing pending: the Settings row offers the folder dialog again");
        let _ = std::fs::remove_dir_all(&t);
        let _ = std::fs::remove_dir_all(&t0);
    }

    /// The hold ends however the task ends: here the crate fails (the journal went away under it).
    #[test]
    fn the_recovery_hold_ends_when_its_task_fails() {
        let _g = folder::test_serial();
        let (t, env, _from, _to) = unfinished("undo-fails");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        assert_eq!(begin(&f, Box::new(|| {})), Next::Undo);
        std::fs::remove_file(env.default_root().unwrap().join(df::JOURNAL)).unwrap();
        assert!(undo_work(&f, &mut kiln_manage::task::NoProgress).is_err());
        assert_eq!(f.hold(), Hold::Free, "everything runs again");
        assert!(crate::state::dirs().is_ok());
        let _ = std::fs::remove_dir_all(&t);
    }

    /// While a partial copy is left (the next start removes it), the Settings row says why a move cannot start.
    #[test]
    fn a_partial_copy_left_is_the_settings_rows_reason() {
        let _g = folder::test_serial();
        let (t, env, _from, to) = unfinished("pending-row");
        let f = Arc::new(Folder::open(env.clone()));
        folder::replace(f.clone());
        // no start-up here: the journal is there and the folder is free (as after an ack of a recovery that left files)
        let out = folder_out(&f);
        let why = out.pending.expect("the reason");
        assert!(why.contains(&text(&to)) && why.contains("next start"), "{why}");
        let plan = check_with(&f, &t.join("elsewhere"), &[]).unwrap();
        assert_eq!(plan.code.as_deref(), Some("move_pending"), "the crate refuses a move too: {plan:?}");
        let _ = std::fs::remove_dir_all(&t);
    }
}
