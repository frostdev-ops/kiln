//! This process's data folder (dev.25, package `data-folder`): found once, the first time anything asks, and kept
//! until the process ends. `state::dirs()` is the one way to it for every command and background thread, and refuses
//! while the folder is held:
//! - a broken pointer (the folder the pointer names cannot be used): nothing is created anywhere, no lock is taken,
//!   and only the data folder's own commands answer (the dialog over the whole window);
//! - another launcher holds the folder's lock exclusively (it is moving the data): the same dialog, Quit only;
//! - a move or a switch of this process runs, or has committed: nothing else reads or writes the folder until the
//!   move fails (then everything runs again) or the launcher restarts.
//!
//! Two gates keep that true: [`Folder::dirs`] for everything in-process, and [`Folder::command_refusal`] in front of
//! every Tauri command (lib.rs), so a command that reaches the data folder through the crate (which reads the pointer
//! itself) is refused too. The move itself works with its own handle ([`Folder::located`]), never through `dirs()`.

use kiln_manage::data_folder::DataLock;
use kiln_manage::paths::{DataDirs, DataEnv, LocateProblem, Located, RootSource};
use kiln_manage::{Error, ErrorCode};
use std::sync::{Arc, Mutex, MutexGuard, RwLock};

/// What holds the data folder (nothing: [`Hold::Free`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Hold {
    Free,
    /// Another launcher holds the lock exclusively: it is moving the data.
    Elsewhere,
    /// A move (or a switch to another folder) of this process runs.
    Moving,
    /// The pointer was written: this process restarts and touches the folder no more.
    Moved,
    /// The process is on its way out (restart, quit).
    Leaving,
}

/// The sentences the gates refuse with.
pub const MOVING_ELSEWHERE: &str = "Kiln is moving its data in another window.";
pub const MOVING_HERE: &str = "Kiln is moving its data folder: wait for the move to finish.";
pub const MOVED: &str = "The data folder has moved: Kiln restarts to use it.";
pub const LEAVING: &str = "Kiln is closing.";

/// Commands that answer while the pointer is broken.
pub const OPEN_WHEN_BROKEN: &[&str] = &["app_info", "data_folder_get", "data_folder_use", "data_folder_reset", "data_folder_restart", "deck_quit"];
/// Commands that answer while another launcher moves the folder.
pub const OPEN_WHEN_ELSEWHERE: &[&str] = &["app_info", "data_folder_get", "data_folder_restart", "deck_quit"];
/// Commands that answer while this process moves the folder or has moved it: the move's own progress and Cancel, the
/// restart, and what does not touch the data folder.
pub const OPEN_WHEN_MOVING: &[&str] = &[
    "app_info", "task_list", "task_get", "task_cancel", "data_folder_get", "data_folder_restart",
    "deck_quit", "deck_fullscreen", "deck_gamepad", "app_open_path", "app_open_url",
];

pub struct Folder {
    pub env: DataEnv,
    found: Result<Located, LocateProblem>,
    /// Making the root (the default or `KILN_DATA_DIR` one, as before dev.25): the error, if it failed.
    made: Result<(), Error>,
    lock: Mutex<Option<DataLock>>,
    hold: Mutex<Hold>,
}

impl Folder {
    /// Finds the data folder from `env`. A broken pointer: nothing more (nothing is created, no lock). Else the root is
    /// made when it is the default or the `KILN_DATA_DIR` one and missing (never a folder the pointer names: the crate
    /// checked it is there), and the lock is taken shared; held exclusively by another launcher: [`Hold::Elsewhere`].
    pub fn open(env: DataEnv) -> Folder {
        let found = env.locate_checked();
        let mut made = Ok(());
        let mut lock = None;
        let mut hold = Hold::Free;
        if let Ok(l) = &found {
            if l.source != RootSource::Pointer && !l.dirs.root.exists() {
                made = std::fs::create_dir_all(&l.dirs.root).map_err(|e| Error::from(e).with_detail(l.dirs.root.display().to_string()));
            }
            if made.is_ok() {
                match DataLock::shared(&l.dirs.root) {
                    Ok(Some(k)) => lock = Some(k),
                    Ok(None) => hold = Hold::Elsewhere,
                    // a folder Kiln cannot write a lock into (read-only): it works without one, as before dev.25
                    Err(e) => eprintln!("data folder lock: {e}"),
                }
            }
        }
        Folder { env, found, made, lock: Mutex::new(lock), hold: Mutex::new(hold) }
    }

    /// The data folder as found, held or not (the move's own handle). Err: the pointer is broken.
    pub fn located(&self) -> Result<&Located, Error> {
        self.found.as_ref().map_err(|p| p.clone().into_error())
    }

    pub fn problem(&self) -> Option<&LocateProblem> {
        self.found.as_ref().err()
    }

    pub fn hold(&self) -> Hold {
        *self.hold.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub fn set_hold(&self, h: Hold) {
        *self.hold.lock().unwrap_or_else(|e| e.into_inner()) = h;
    }

    /// Why the folder is not to be used now (None: it is free).
    pub fn held_error(&self) -> Option<Error> {
        if let Some(p) = self.problem() {
            return Some(p.clone().into_error());
        }
        let busy = |m: &str| Some(Error::new(ErrorCode::Busy, m));
        match self.hold() {
            Hold::Free => None,
            Hold::Elsewhere => busy(MOVING_ELSEWHERE),
            Hold::Moving => busy(MOVING_HERE),
            Hold::Moved => busy(MOVED),
            Hold::Leaving => busy(LEAVING),
        }
    }

    /// The data folder for everyone but the move: refused while held (see the module docs).
    pub fn dirs(&self) -> Result<DataDirs, Error> {
        if let Some(e) = self.held_error() {
            return Err(e);
        }
        self.made.clone()?;
        Ok(self.located()?.dirs.clone())
    }

    /// The gate in front of every Tauri command: why `cmd` may not run now (None: it may).
    pub fn command_refusal(&self, cmd: &str) -> Option<Error> {
        let open: &[&str] = if self.problem().is_some() {
            OPEN_WHEN_BROKEN
        } else {
            match self.hold() {
                Hold::Free => return None,
                Hold::Elsewhere => OPEN_WHEN_ELSEWHERE,
                Hold::Moving | Hold::Moved | Hold::Leaving => OPEN_WHEN_MOVING,
            }
        };
        if open.contains(&cmd) { None } else { self.held_error() }
    }

    /// The process's lock (None: none taken: a broken pointer, held elsewhere, or a folder it cannot be written into).
    pub fn lock(&self) -> MutexGuard<'_, Option<DataLock>> {
        self.lock.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Gives the lock up (before a restart).
    pub fn release_lock(&self) {
        self.lock().take();
    }
}

static CURRENT: RwLock<Option<Arc<Folder>>> = RwLock::new(None);

/// This process's data folder (found from the process environment the first time).
pub fn current() -> Arc<Folder> {
    if let Some(f) = CURRENT.read().unwrap_or_else(|e| e.into_inner()).as_ref() {
        return f.clone();
    }
    let mut w = CURRENT.write().unwrap_or_else(|e| e.into_inner());
    if let Some(f) = w.as_ref() {
        return f.clone();
    }
    let f = Arc::new(Folder::open(DataEnv::from_process()));
    *w = Some(f.clone());
    f
}

/// The data folder without opening it (no lock, nothing created): for `--selftest`'s report.
pub fn describe() -> String {
    match DataEnv::from_process().locate_checked() {
        Ok(l) => format!("{} ({:?})", l.dirs.root.display(), l.source),
        Err(p) => format!("(broken pointer: {})", p.reason),
    }
}

/// Tests: makes `f` the process's data folder (the next start of a launcher: a new value over the same folders).
#[cfg(test)]
pub fn replace(f: Arc<Folder>) {
    *CURRENT.write().unwrap_or_else(|e| e.into_inner()) = Some(f);
}

/// Tests that use the process's data folder ([`replace`]) run one at a time; when the guard goes, the process's folder
/// is found from the environment again (the test runs' scratch `KILN_DATA_DIR`).
#[cfg(test)]
pub fn test_serial() -> TestGuard {
    static M: Mutex<()> = Mutex::new(());
    TestGuard(Some(M.lock().unwrap_or_else(|e| e.into_inner())))
}

#[cfg(test)]
pub struct TestGuard(Option<MutexGuard<'static, ()>>);

#[cfg(test)]
impl Drop for TestGuard {
    fn drop(&mut self) {
        *CURRENT.write().unwrap_or_else(|e| e.into_inner()) = None;
        self.0.take();
    }
}
