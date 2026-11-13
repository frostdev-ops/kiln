//! Cooking a mod: one implementation for `kiln cook`, `kiln build` and the launcher.
//!
//! A mod with a cook recipe (`<mod>/*/cook.json`, or looks and sounds declared in `blocks/*.json`) has files that are
//! made on each machine from that machine's game: models, animations, sounds, Impact programs. The cook tools
//! ([`crate::cooktools`]) make them, into the mod's `assets/cooked` folder and nowhere else, so a cooked mod keeps its
//! identity ([`crate::hash::mod_identity`]). Each cook records what it wrote in the game it read
//! (`<game>/kiln/state/cook-receipts`, [`Receipt`]): kiln.assets in that game loads a cooked file only when a receipt
//! lists its bytes, so a cooked file a player was given (in an archive, a modpack, a copy) is never loaded
//! (docs/kiln/modding/cooking.md).
//!
//! - [`cook_folder`] cooks (or only checks) one mod folder: the sound blocks' steps, every recipe whose own check says
//!   a cook is needed, the looks of the blocks. It returns what each recipe did and what the tools said (its end).
//!   Every tool runs within [`CookOptions::limits`]; one that passes them is stopped with every process it started.
//! - [`needs_cook`] is the one rule for when the launcher cooks a mod before the game starts: the mod was never cooked
//!   for this game (it was installed or switched on), its files changed (it was updated), the game's build changed
//!   since its last cook (cooked files are made from game data), its last cook failed, or the files its last cook made
//!   are gone or changed.
//! - [`CookedState`] (`<game>/kiln/state/cooked.json`) is the record that rule reads: per mod, what was cooked for
//!   which game build, and how it ended.
//! - [`plan`] lists the mods of the game's profile that need a cook; [`cook_mods`] cooks them one after the other. One
//!   mod's failed cook stops neither the others nor the start of the game: it is reported as not cooked, with the
//!   tool's own text. [`cook_due`] is the launcher's whole cook: the active profile applied first, the game's lock held
//!   ([`crate::state::lock_game`]: no start and no other cook of this game meanwhile), the due mods cooked from scratch.

use crate::cooktools::CookTools;
use crate::game::GameInstall;
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, VecDeque};
use std::io::{BufRead, BufReader, Read};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::time::{Duration, Instant};

// ---------------------------------------------------------------------------------------------- one folder ----

/// How long a cook's tools may take ([`CookOptions::limits`]). A tool that passes a limit is stopped with every process
/// it started (its process group; on Windows its job), and its recipe fails with a sentence that names the limit.
/// None: no limit (`kiln cook`, `kiln build`: a developer's terminal, where Ctrl+C stops a tool).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct Limits {
    /// One tool's whole run (a recipe's cook, the looks).
    pub run: Option<Duration>,
    /// A tool that prints nothing for this long.
    pub quiet: Option<Duration>,
    /// A short step: a recipe's own check, the sound blocks' step, the looks' probe.
    pub step: Option<Duration>,
}

impl Limits {
    /// The launcher's: a cook never holds Play for ever. A real cook takes seconds to a few minutes.
    pub const LAUNCHER: Limits = Limits {
        run: Some(Duration::from_secs(30 * 60)),
        quiet: Some(Duration::from_secs(10 * 60)),
        step: Some(Duration::from_secs(120)),
    };
}

/// How [`cook_folder`] runs.
#[derive(Debug, Clone, Default)]
pub struct CookOptions {
    /// The game the cook reads (a client's folder, or a dedicated server's). None: only the tools that need no game
    /// run (sounds and animations of the mod's own files); a recipe that reads the game is `skipped` (or stops a
    /// strict run).
    pub game: Option<PathBuf>,
    /// Only say whether a cook is needed; nothing is written.
    pub check: bool,
    /// The mod's id for a project whose `kiln.mod.json` is made by its build (ids follow from it).
    pub mod_id: Option<String>,
    /// A problem that is not a recipe's own (no tool for it, no game to read, the sound blocks' steps) stops the run
    /// with a [`CookStop`] instead of being reported in the result: `kiln cook`. Off: `kiln build` and the launcher.
    pub strict: bool,
    /// Cook every recipe, whatever its own check says. A recipe's check reads the mod's files only (the recipe, its
    /// sources, its outputs, the last report): it cannot see that the game was updated, or that the files in
    /// `assets/cooked` were made somewhere else. The launcher's cook ([`cook_due`]) and `kiln cook --force`.
    /// Without effect on a `check`.
    pub force: bool,
    /// Start from nothing: the folder's `assets/cooked` and its receipts in the game are removed first, so what is
    /// there after the cook is what this cook made. The launcher's cook ([`cook_due`]).
    pub clean: bool,
    /// How long the tools may take ([`Limits::LAUNCHER`] for the launcher; none for `kiln cook` and `kiln build`).
    pub limits: Limits,
}

/// Why a strict [`cook_folder`] stopped: `code` is `kiln cook`'s error code.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CookStop {
    pub code: &'static str,
    pub message: String,
}

/// One recipe of a [`CookReport`] (`kiln cook --json` prints these keys).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RecipeResult {
    /// The recipe file, or the mod's `blocks` folder for the looks.
    pub recipe: PathBuf,
    pub tool: String,
    /// The cook's end (absent in a check).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub ok: Option<bool>,
    /// Whether the tool ran (false: it was up to date).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub cooked: Option<bool>,
    /// A check's answer.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub needed: Option<bool>,
    /// Why it did not run (no tool for it, no game to read).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub skipped: Option<String>,
}

/// What [`cook_folder`] did.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookReport {
    /// Every recipe ended well (a check: none needs a cook).
    pub ok: bool,
    pub recipes: Vec<RecipeResult>,
    /// What the tools said, in order: what a player reads when a cook fails. Its end only, when they said much
    /// ([`TEXT_KEPT`]).
    #[serde(skip)]
    pub text: String,
}

/// The cook recipes of a mod folder: every `<dir>/*/cook.json` (assets, impact, audio), sorted.
pub fn recipes(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir).into_iter().flatten().flatten().map(|e| e.path().join("cook.json")).filter(|p| p.is_file()).collect();
    v.sort();
    v
}

/// Whether a mod folder has anything a cook may have to make: a recipe, or blocks (their looks and sounds are cooked
/// without a recipe of the mod's own). The tools say the rest.
pub fn has_recipe(dir: &Path) -> bool {
    !recipes(dir).is_empty() || std::fs::read_dir(dir.join("blocks")).into_iter().flatten().flatten().any(|e| e.path().extension().is_some_and(|x| x == "json"))
}

fn read_jsonc(p: &Path) -> Option<serde_json::Value> {
    crate::jsonc::parse(std::fs::read_to_string(p).ok()?.trim_start_matches('\u{feff}')).ok()
}

// ------------------------------------------------------------------------------------------ running a tool ----

/// A line a tool prints is cut at this many bytes (the rest of it is dropped, "…" says so).
pub const LINE_MAX: usize = 4096;
/// What is kept of what the tools said ([`CookReport::text`]): the last this many bytes, whole lines.
pub const TEXT_KEPT: usize = 256 * 1024;
/// After a tool ended, how long the pipes it left to processes it started may stay open before those are stopped.
const AFTER_EXIT: Duration = Duration::from_secs(5);

/// The end of what the tools said, at most [`TEXT_KEPT`] bytes: the oldest lines go first.
#[derive(Default)]
struct Tail {
    lines: VecDeque<String>,
    bytes: usize,
    dropped: bool,
}

impl Tail {
    fn push(&mut self, line: &str) {
        self.bytes += line.len() + 1;
        self.lines.push_back(line.to_string());
        while self.bytes > TEXT_KEPT && self.lines.len() > 1 {
            let old = self.lines.pop_front().expect("more than one");
            self.bytes -= old.len() + 1;
            self.dropped = true;
        }
    }

    fn text(&self) -> String {
        let mut s = String::with_capacity(self.bytes + 4);
        if self.dropped {
            s.push_str("…\n");
        }
        for l in &self.lines {
            s.push_str(l);
            s.push('\n');
        }
        s
    }
}

/// Reads `pipe` line by line, each line at most [`LINE_MAX`] bytes (the rest of a longer one is read and dropped),
/// and sends them on: a tool that prints a huge line, or a lot, never makes Kiln hold it all.
fn read_lines(pipe: Box<dyn Read + Send>, err: bool, tx: std::sync::mpsc::SyncSender<(bool, String)>) {
    let mut r = BufReader::with_capacity(64 * 1024, pipe);
    let mut line: Vec<u8> = Vec::new();
    let mut cut = false;
    loop {
        let (done, used) = match r.fill_buf() {
            Ok([]) => (true, 0),
            Ok(buf) => {
                let (chunk, end) = match buf.iter().position(|&b| b == b'\n') {
                    Some(i) => (&buf[..i], Some(i + 1)),
                    None => (buf, None),
                };
                let room = LINE_MAX.saturating_sub(line.len());
                line.extend_from_slice(&chunk[..chunk.len().min(room)]);
                cut |= chunk.len() > room;
                if end.is_some() {
                    let mut text = String::from_utf8_lossy(&line).trim_end_matches('\r').to_string();
                    if cut {
                        text.push('…');
                    }
                    if tx.send((err, text)).is_err() {
                        return;
                    }
                    line.clear();
                    cut = false;
                }
                (false, end.unwrap_or(buf.len()))
            }
            Err(e) if e.kind() == std::io::ErrorKind::Interrupted => (false, 0),
            Err(_) => (true, 0),
        };
        r.consume(used);
        if done {
            if !line.is_empty() {
                let mut text = String::from_utf8_lossy(&line).trim_end_matches('\r').to_string();
                if cut {
                    text.push('…');
                }
                let _ = tx.send((err, text));
            }
            return;
        }
    }
}

/// The processes a tool started, as one: on Unix its own process group (only when a limit applies: a terminal's
/// Ctrl+C reaches a tool of `kiln cook` as before), on Windows a job object that ends them all. On Windows the tool is
/// started suspended, put into its job, and only then resumed: nothing it starts runs outside the job, and a tool that
/// cannot be put into one is ended before it ran and the cook fails.
struct Tree {
    #[cfg(windows)]
    job: Option<windows_job::Job>,
    own_group: bool,
}

impl Tree {
    fn prepare(cmd: &mut Command, limited: bool) -> Tree {
        #[cfg(unix)]
        if limited {
            use std::os::unix::process::CommandExt;
            cmd.process_group(0);
        }
        #[cfg(windows)]
        if limited {
            use std::os::windows::process::CommandExt;
            // CREATE_NO_WINDOW (as cooktools sets it) and CREATE_SUSPENDED: resumed once the job holds it (started)
            cmd.creation_flags(0x0800_0000 | 0x0000_0004);
        }
        let _ = cmd;
        Tree {
            #[cfg(windows)]
            job: None,
            own_group: limited,
        }
    }

    /// The tool runs: on Windows (with limits) it is put into its job and resumed; Err (the tool ended) when that fails.
    fn started(&mut self, child: &mut Child) -> std::io::Result<()> {
        #[cfg(windows)]
        if self.own_group {
            match windows_job::Job::for_child(child) {
                Some(job) => self.job = Some(job),
                None => {
                    let e = std::io::Error::last_os_error();
                    let _ = child.kill();
                    let _ = child.wait();
                    return Err(std::io::Error::new(e.kind(), format!("the cook tool could not be put into a job that ends it with what it starts: {e}")));
                }
            }
            if let Err(e) = windows_job::resume(child) {
                let _ = child.kill();
                let _ = child.wait();
                return Err(e);
            }
        }
        let _ = child;
        Ok(())
    }

    /// Stops the tool and every process it started, and reaps the tool.
    fn kill(&self, child: &mut Child) {
        #[cfg(unix)]
        if self.own_group {
            // SAFETY: a plain kill(2) of the process group the tool leads (process_group(0) above made it).
            unsafe { libc::kill(-(child.id() as libc::pid_t), libc::SIGKILL) };
        }
        #[cfg(windows)]
        if let Some(j) = &self.job {
            j.terminate();
        }
        let _ = child.kill();
        let _ = child.wait();
    }
}

#[cfg(windows)]
mod windows_job {
    use windows_sys::Win32::Foundation::{CloseHandle, HANDLE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next};
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JobObjectExtendedLimitInformation, SetInformationJobObject, TerminateJobObject,
    };
    use windows_sys::Win32::System::Threading::{OpenThread, ResumeThread, THREAD_SUSPEND_RESUME};

    /// A job the tool runs in: what it starts runs in it too, and closing the job (or [`Job::terminate`]) ends them all.
    pub struct Job(HANDLE);

    impl Job {
        /// None when the job cannot be made or the process cannot be put into it (`GetLastError` says why).
        pub fn for_child(child: &std::process::Child) -> Option<Job> {
            use std::os::windows::io::AsRawHandle;
            // SAFETY: plain Win32 calls on a handle this function owns and on the child's process handle.
            unsafe {
                let job = CreateJobObjectW(std::ptr::null(), std::ptr::null());
                if job.is_null() {
                    return None;
                }
                let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
                info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                let set = SetInformationJobObject(job, JobObjectExtendedLimitInformation, &info as *const _ as *const _,
                                                  std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32);
                if set == 0 || AssignProcessToJobObject(job, child.as_raw_handle() as HANDLE) == 0 {
                    CloseHandle(job);
                    return None;
                }
                Some(Job(job))
            }
        }

        pub fn terminate(&self) {
            // SAFETY: the job handle is open until drop.
            unsafe { TerminateJobObject(self.0, 1) };
        }
    }

    impl Drop for Job {
        fn drop(&mut self) {
            // SAFETY: closing the handle this Job owns (with KILL_ON_JOB_CLOSE: whatever still runs in it ends).
            unsafe { CloseHandle(self.0) };
        }
    }

    /// Resumes the threads of a process started suspended (its main thread: std keeps no handle to it).
    pub fn resume(child: &std::process::Child) -> std::io::Result<()> {
        let pid = child.id();
        // SAFETY: a thread snapshot, walked and closed here; each thread handle is closed after its resume.
        unsafe {
            let snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if snap == INVALID_HANDLE_VALUE {
                return Err(std::io::Error::last_os_error());
            }
            let mut entry: THREADENTRY32 = std::mem::zeroed();
            entry.dwSize = std::mem::size_of::<THREADENTRY32>() as u32;
            let mut resumed = 0;
            let mut more = Thread32First(snap, &mut entry) != 0;
            while more {
                if entry.th32OwnerProcessID == pid {
                    let t = OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID);
                    if !t.is_null() {
                        if ResumeThread(t) != u32::MAX {
                            resumed += 1;
                        }
                        CloseHandle(t);
                    }
                }
                more = Thread32Next(snap, &mut entry) != 0;
            }
            CloseHandle(snap);
            if resumed == 0 {
                return Err(std::io::Error::other("the cook tool was started suspended and could not be resumed"));
            }
        }
        Ok(())
    }
}

/// How a tool's run ended ([`run_tool`]).
#[derive(Debug, Clone, PartialEq, Eq)]
struct Ended {
    /// It exited with 0.
    ok: bool,
    /// Its exit code (None: stopped by a signal, or by Kiln).
    code: Option<i32>,
    /// Kiln stopped it: why, as a sentence.
    stopped: Option<String>,
}

fn minutes(d: Duration) -> String {
    let s = d.as_secs();
    if s >= 120 { format!("{} minutes", s / 60) } else { format!("{s} seconds") }
}

/// Runs a tool and hands each line it prints (stdout and stderr, as they come; `true` for stderr) to `on_line`, within
/// `limit` (the whole run) and `quiet` (no line for that long), until `stop()` says stop. A tool stopped for any of
/// them is ended with every process it started and reaped; the reason is in [`Ended::stopped`]. A tool that ended
/// while processes it started still hold its output open gets [`AFTER_EXIT`], then those are stopped too.
fn run_tool(cmd: &mut Command, limit: Option<Duration>, quiet: Option<Duration>, stop: &dyn Fn() -> bool,
            on_line: &mut dyn FnMut(bool, &str)) -> std::io::Result<Ended> {
    let limited = limit.is_some() || quiet.is_some();
    let mut tree = Tree::prepare(cmd, limited);
    let mut child = cmd.stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped()).spawn()?;
    tree.started(&mut child)?;
    let (tx, rx) = std::sync::mpsc::sync_channel::<(bool, String)>(256);
    let mut readers = Vec::new();
    for (pipe, err) in [(child.stdout.take().map(|p| Box::new(p) as Box<dyn Read + Send>), false), (child.stderr.take().map(|p| Box::new(p) as Box<dyn Read + Send>), true)] {
        if let Some(pipe) = pipe {
            let tx = tx.clone();
            readers.push(std::thread::spawn(move || read_lines(pipe, err, tx)));
        }
    }
    drop(tx);
    let start = Instant::now();
    let mut last = Instant::now();
    let mut exited: Option<(Instant, std::process::ExitStatus)> = None;
    let mut stopped: Option<String> = None;
    let mut open = true; // the tool's output can still bring lines
    // Every pass looks at the limits, the stop and the tool's end, whether a line came or not: a tool that keeps
    // printing is stopped like a silent one.
    loop {
        if open {
            match rx.recv_timeout(Duration::from_millis(100)) {
                Ok((err, line)) => {
                    last = Instant::now();
                    on_line(err, &line);
                }
                Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => open = false,
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => {}
            }
        } else {
            std::thread::sleep(Duration::from_millis(100));
        }
        if exited.is_none()
            && let Ok(Some(status)) = child.try_wait()
        {
            exited = Some((Instant::now(), status));
        }
        if !open && exited.is_some() {
            break;
        }
        if stop() {
            stopped = Some("stopped: the cook was canceled".into());
        } else if let Some(l) = limit.filter(|l| start.elapsed() >= *l) {
            stopped = Some(format!("stopped after {}: a cook tool may take that long at most", minutes(l)));
        } else if let Some(q) = quiet.filter(|q| last.elapsed() >= *q) {
            stopped = Some(format!("stopped: the cook tool said nothing for {}", minutes(q)));
        } else if exited.is_some_and(|(at, _)| at.elapsed() >= AFTER_EXIT) {
            break; // what it started still holds its output: stopped below
        }
        if stopped.is_some() {
            break;
        }
    }
    if stopped.is_some() || open {
        tree.kill(&mut child);
        // the readers end once every holder of the pipes is gone; a holder Kiln could not stop is not waited for
    } else {
        for r in readers {
            let _ = r.join();
        }
    }
    let status = match exited {
        Some((_, s)) => s,
        None => child.wait()?,
    };
    let ok = stopped.is_none() && status.success();
    Ok(Ended { ok, code: if stopped.is_some() { None } else { status.code() }, stopped })
}

/// [`run_tool`] for a short step whose output is read after it ended: (how it ended, stdout, stderr), each output at
/// most [`TEXT_KEPT`] bytes.
fn run_step(cmd: &mut Command, limit: Option<Duration>, stop: &dyn Fn() -> bool) -> std::io::Result<(Ended, String, String)> {
    let (mut out, mut err) = (Tail::default(), Tail::default());
    let end = run_tool(cmd, limit, None, stop, &mut |is_err, line| if is_err { err.push(line) } else { out.push(line) })?;
    Ok((end, out.text(), err.text()))
}

/// The cook tools' command, with the receipt asked for: a cook through Kiln records what it wrote in the game it read
/// (`KILN_COOK_RECEIPT=1`, tools/*/cookpaths.py `receipt`). The rest of the environment is the command's own.
fn with_receipt(mut cmd: Command) -> Command {
    cmd.env("KILN_COOK_RECEIPT", "1");
    cmd
}

/// Recipes with sound steps the blocks made, so that deleting the last sound block also removes its steps.
fn has_sound_blocks(dir: &Path) -> bool {
    let has_sounds = std::fs::read_dir(dir.join("blocks")).into_iter().flatten().flatten()
        .any(|e| e.path().extension().is_some_and(|x| x == "json") && read_jsonc(&e.path()).is_some_and(|d| d.get("sounds").is_some()));
    has_sounds || read_jsonc(&dir.join("assets/cook.json")).is_some_and(|d| d.get("blockSounds").is_some())
}

/// Whether a folder needs the cook tools at all: sound blocks, a recipe, or blocks that may hold looks.
pub fn wants_tools(dir: &Path) -> bool {
    has_sound_blocks(dir) || !recipes(dir).is_empty() || dir.join("blocks").is_dir()
}

/// The first step of a cook: the steps of the mod's sound blocks (`blocks/*.json` "sounds") written into its
/// `assets/cook.json` (soundcook `blocks`), or with `check` compared with it. -> whether the recipe is current. Nothing
/// runs for a mod without sound blocks. A failure stops a strict run and is a warning otherwise.
pub fn sound_blocks(tools: &CookTools, dir: &Path, opts: &CookOptions, say: &mut dyn FnMut(&str)) -> std::result::Result<bool, CookStop> {
    sound_blocks_watched(tools, dir, opts, say, &|| false)
}

fn sound_blocks_watched(tools: &CookTools, dir: &Path, opts: &CookOptions, say: &mut dyn FnMut(&str), stop: &dyn Fn() -> bool)
                        -> std::result::Result<bool, CookStop> {
    if !has_sound_blocks(dir) {
        return Ok(true);
    }
    let mut cmd = tools.script_command("tools/soundcook/soundcook.py");
    cmd.arg("blocks").arg(dir);
    if opts.check {
        cmd.arg("--check");
    }
    if let Some(id) = opts.mod_id.as_deref().filter(|id| !id.is_empty() && !dir.join("kiln.mod.json").is_file()) {
        cmd.arg("--mod").arg(id);
    }
    let error = match run_step(&mut cmd, opts.limits.step, stop) {
        Ok((end, said, _)) if end.ok || (opts.check && end.code == Some(1)) => {
            if opts.check || said.contains("written") || said.contains("removed") {
                say(&format!("kiln: {}", said.trim()));
            }
            return Ok(end.ok);
        }
        Ok((end, _, err)) => end.stopped.unwrap_or_else(|| err.trim().to_string()),
        Err(e) => e.to_string(),
    };
    if opts.strict {
        return Err(CookStop { code: "cook_failed", message: format!("sound blocks: {error}") });
    }
    say(&format!("kiln: warning: sound blocks: {error}"));
    Ok(false)
}


/// Cooks one mod folder (a mod project, or an installed mod's folder), or with `check` only says what needs a cook.
///
/// 1. the steps of the mod's sound blocks are written into `assets/cook.json` (soundcook `blocks`);
/// 2. every recipe (`*/cook.json`) is asked whether it needs a cook (the tool's own `--check`: the recipe, its
///    sources, its outputs and the report of the last cook; with [`CookOptions::force`] it is not asked) and cooked
///    when it does, with `--update-manifest`;
/// 3. the looks of `blocks/*.json` are cooked for a mod with no modelcook recipe of its own.
///
/// Every line a tool prints goes to `on_line` as it comes and into [`CookReport::text`]. A recipe that fails does not
/// stop the others. Everything a cook writes from the game goes into the mod's `assets/cooked` (the tools refuse
/// anything else), so the mod's identity is the same before and after; the tools record it in the game's receipts.
/// A cook (not a check) of a folder waits for another cook of the same folder to end first.
pub fn cook_folder(tools: &CookTools, dir: &Path, opts: &CookOptions, on_line: &mut dyn FnMut(&str)) -> std::result::Result<CookReport, CookStop> {
    cook_folder_watched(tools, dir, opts, &mut |_, line| on_line(line), &|| false)
}

/// [`cook_folder`] that `stop()` ends (the tool that runs is stopped with what it started, and nothing else starts),
/// with each line marked as a tool's error output (`true`: what it printed on stderr) or not (its stdout, and Kiln's
/// own lines): `kiln cook` prints them where the tool did.
pub fn cook_folder_watched(tools: &CookTools, dir: &Path, opts: &CookOptions, on_line: &mut dyn FnMut(bool, &str), stop: &dyn Fn() -> bool)
                           -> std::result::Result<CookReport, CookStop> {
    let fail = |code: &'static str, message: String| CookStop { code, message };
    // one cook of a folder at a time
    let _lock = if opts.check {
        None
    } else {
        let busy = format!("another cook of {} did not end", dir.display());
        let wait = opts.limits.run.map_or(Duration::from_secs(24 * 3600), |r| r + Duration::from_secs(60));
        let held = match crate::state::lock_cook_folder(dir, Duration::ZERO, stop, &busy) {
            Err(e) if e.code == ErrorCode::Busy => {
                on_line(false, &format!("kiln: waiting for another cook of {} to end", dir.display()));
                crate::state::lock_cook_folder(dir, wait, stop, &busy)
            }
            other => other,
        };
        Some(held.map_err(|e| fail(if e.code == ErrorCode::Canceled { "canceled" } else { "busy" }, e.message))?)
    };
    if !opts.clean || opts.check {
        return cook_steps(tools, dir, opts, on_line, stop);
    }
    // A clean cook: what the folder holds is set aside (its cooked files, their receipts, the mod's files a cook may
    // write, all of it on disk) and comes back when the cook does not end well (a missing source, a cancel, a tool that
    // fails): a mod that worked keeps working. When it ends well, what was set aside goes, and only then.
    // What an earlier cook left set aside (its own putting back failed, or it was stopped) is the last state that
    // worked: it comes back first, and a cook that cannot put it back does not start.
    if let Err(e) = recover_set_aside(dir, opts.game.as_deref()) {
        return Err(fail("io", format!("{}: the files of its last good cook are set aside in {} and could not be put back ({}): it is not cooked \
            again until they are. Close what holds them and try again, or move that folder back to assets/cooked yourself.",
            dir.display(), dir.join("assets").display(), e.message)));
    }
    let kept = set_aside(dir, opts.game.as_deref()).map_err(|e| fail("io", e.message))?;
    let r = cook_steps(tools, dir, opts, on_line, stop);
    if r.as_ref().is_ok_and(|rep| rep.ok) {
        kept.drop_old();
    } else if let Err(e) = kept.restore(dir, opts.game.as_deref()) {
        on_line(false, &format!("kiln: the files of {} from before this cook could not be put back ({}): they stay set aside in {} and are put \
            back before its next cook", dir.display(), e.message, kept.prev.display()));
    } else {
        on_line(false, &format!("kiln: {} keeps the files of its last cook", dir.display()));
    }
    r
}

/// The steps of [`cook_folder_watched`], the folder's lock held.
fn cook_steps(tools: &CookTools, dir: &Path, opts: &CookOptions, on_line: &mut dyn FnMut(bool, &str), stop: &dyn Fn() -> bool)
              -> std::result::Result<CookReport, CookStop> {
    let mut text = Tail::default();
    let mut tell = |err: bool, line: &str, text: &mut Tail| {
        on_line(err, line);
        text.push(line);
    };
    let fail = |code: &'static str, message: String| CookStop { code, message };
    let mut all_ok = true;
    let mut results = Vec::new();
    let quiet = !opts.strict; // kiln build and the launcher: nothing is said about what is up to date
    // a game with Kiln loads only cooked files its receipts list: a recipe with none there is cooked, whatever its own
    // check says (files cooked before receipts existed, or for another game)
    let receipts_needed = opts.game.as_deref().filter(|g| g.join(crate::paths::game::KILN).is_dir());
    let unreceipted = |recipe: &Path, tool: &str| receipts_needed.is_some_and(|g| {
        let rel = recipe.strip_prefix(dir).map(|r| r.to_string_lossy().replace('\\', "/")).unwrap_or_default();
        !receipts_of(g, dir).iter().any(|(_, x)| x.tool == tool && x.recipe == rel)
    });

    // 1. sound blocks
    match sound_blocks_watched(tools, dir, opts, &mut |l| tell(false, l, &mut text), stop) {
        Ok(current) => all_ok &= current,
        Err(e) => return Err(e),
    }

    // 2. the recipes
    let list = recipes(dir);
    let looks = dir.join("blocks").is_dir()
        && read_jsonc(&dir.join("assets/cook.json")).map(|d| d["tool"].as_str().unwrap_or("modelcook").to_string()).as_deref() != Some("modelcook");
    if list.is_empty() && !looks {
        if opts.strict {
            tell(false, "no cook recipes (*/cook.json) in this project", &mut text);
        }
        return Ok(CookReport { ok: all_ok, recipes: results, text: text.text() });
    }
    for r in &list {
        if stop() {
            tell(false, "kiln: the cook was stopped", &mut text);
            return Ok(CookReport { ok: false, recipes: results, text: text.text() });
        }
        let tool = read_jsonc(r).and_then(|d| d["tool"].as_str().map(str::to_owned)).unwrap_or_else(|| "modelcook".into());
        let entry = |ok, cooked, needed, skipped: Option<String>| RecipeResult { recipe: r.clone(), tool: tool.clone(), ok, cooked, needed, skipped };
        let Some((_, verb)) = tools.script(&tool) else {
            let msg = format!("{}: no cook tool '{tool}' in {}", r.display(), tools.root.display());
            if opts.strict {
                return Err(fail("tool_missing", msg));
            }
            tell(false, &format!("kiln: warning: {msg}"), &mut text);
            all_ok = false;
            results.push(entry(Some(false), None, None, Some(msg)));
            continue;
        };
        let no_receipt = !opts.force && unreceipted(r, &tool);
        let needed = (opts.force && !opts.check) || no_receipt || {
            let mut chk = tools.command(&tool).expect("the tool's script was found above");
            chk.arg(verb).arg(r).arg("--check");
            !run_step(&mut chk, opts.limits.step, stop).is_ok_and(|(end, _, _)| end.ok)
        };
        if no_receipt && !opts.check {
            tell(false, &format!("kiln: {}: no receipt of this game lists its files: cooked again, so the game loads them", r.display()), &mut text);
        }
        if opts.check {
            tell(false, &format!("{}: {}", r.display(), if needed { "cook needed" } else { "up to date" }), &mut text);
            all_ok &= !needed;
            results.push(entry(None, None, Some(needed), None));
            continue;
        }
        if !needed {
            if !quiet {
                tell(false, &format!("{}: up to date", r.display()), &mut text);
            }
            results.push(entry(Some(true), Some(false), None, None));
            continue;
        }
        let mut cmd = with_receipt(tools.command(&tool).expect("the tool's script was found above"));
        match &opts.game {
            Some(g) => {
                cmd.arg("--game").arg(g);
            }
            None if tool == "soundcook" || tool == "animcook" => {
                cmd.arg("--no-game");
            }
            None => {
                let msg = format!("{}: the cook reads your installed game; none found (--game or KILN_GAME)", r.display());
                if opts.strict {
                    return Err(fail("not_found", msg));
                }
                tell(false, &format!("kiln: warning: {msg}"), &mut text);
                all_ok = false;
                results.push(entry(Some(false), None, None, Some(msg)));
                continue;
            }
        }
        cmd.arg(verb).arg(r).arg("--update-manifest");
        tell(false, &format!("kiln: cooking {} ({tool})", r.display()), &mut text);
        let ok = match run_tool(&mut cmd, opts.limits.run, opts.limits.quiet, stop, &mut |e, l| tell(e, l, &mut text)) {
            Ok(end) => {
                if let Some(why) = &end.stopped {
                    tell(false, &format!("kiln: {}: {why}", r.display()), &mut text);
                }
                end.ok
            }
            Err(e) if opts.strict => return Err(fail("tool_missing", format!("cannot run {cmd:?}: {e}"))),
            Err(e) => {
                tell(false, &format!("kiln: cannot run the cook tool: {e}"), &mut text);
                false
            }
        };
        all_ok &= ok;
        results.push(entry(Some(ok), Some(true), None, None));
    }

    // 3. the looks of blocks/*.json, cooked with no recipe of the mod's own (`modelcook.py looks`)
    if looks && !stop() {
        let script = "tools/modelcook/modelcook.py";
        let where_ = dir.join("blocks");
        let entry = |ok, cooked, needed, skipped: Option<String>| RecipeResult { recipe: where_.clone(), tool: "modelcook".into(), ok, cooked, needed, skipped };
        let mut probe_cmd = tools.script_command(script);
        probe_cmd.arg("looks").arg(dir).arg("--check");
        if let Ok((probe, said, _)) = run_step(&mut probe_cmd, opts.limits.step, stop) {
            let needed = (opts.force && !opts.check) || !probe.ok || (!opts.force && unreceipted(&dir.join("assets/cook.json"), "modelcook.looks"));
            if said.contains("no looks") {
                if opts.strict && list.is_empty() {
                    tell(false, "no cook recipes (*/cook.json) in this project, and no looks with files in blocks/*.json", &mut text);
                }
            } else if opts.check {
                tell(false, &format!("{} (looks): {}", where_.display(), if needed { "cook needed" } else { "up to date" }), &mut text);
                all_ok &= !needed;
                results.push(entry(None, None, Some(needed), None));
            } else if !needed {
                if !quiet {
                    tell(false, &format!("{} (looks): up to date", where_.display()), &mut text);
                }
                results.push(entry(Some(true), Some(false), None, None));
            } else if let Some(game) = &opts.game {
                let mut cmd = with_receipt(tools.script_command(script));
                cmd.arg("--game").arg(game).arg("looks").arg(dir).arg("--update-manifest");
                // a model's GUID follows from the mod's id: a project whose kiln.mod.json is made by the build says it
                if let Some(id) = opts.mod_id.as_deref().filter(|id| !id.is_empty() && !dir.join("kiln.mod.json").is_file()) {
                    cmd.arg("--mod").arg(id);
                }
                tell(false, &format!("kiln: cooking the looks of {}", where_.display()), &mut text);
                let ok = match run_tool(&mut cmd, opts.limits.run, opts.limits.quiet, stop, &mut |e, l| tell(e, l, &mut text)) {
                    Ok(end) => {
                        if let Some(why) = &end.stopped {
                            tell(false, &format!("kiln: {} (looks): {why}", where_.display()), &mut text);
                        }
                        end.ok
                    }
                    Err(e) if opts.strict => return Err(fail("tool_missing", format!("cannot run {cmd:?}: {e}"))),
                    Err(e) => {
                        tell(false, &format!("kiln: cannot run the cook tool: {e}"), &mut text);
                        false
                    }
                };
                all_ok &= ok;
                results.push(entry(Some(ok), Some(true), None, None));
            } else {
                let msg = format!("{} (looks): the cook reads your installed game; none found (--game or KILN_GAME)", where_.display());
                if opts.strict {
                    return Err(fail("not_found", msg));
                }
                tell(false, &format!("kiln: warning: {msg}"), &mut text);
                all_ok = false;
                results.push(entry(Some(false), None, None, Some(msg)));
            }
        }
    }
    if stop() {
        tell(false, "kiln: the cook was stopped", &mut text);
        all_ok = false;
    }
    Ok(CookReport { ok: all_ok, recipes: results, text: text.text() })
}

/// What a clean cook set aside ([`set_aside`]), all of it on disk in `assets/.kiln-cooked-prev-<hex>` (a dot name no
/// identity counts): the folder's cooked files (`cooked/`, the folder renamed), copies of the receipts of this folder
/// in the game (`receipts/`) and of the mod's files a cook may write (`own/`: `assets/assets.json`, `assets/cook.json`,
/// `blocks/*.materials.json`), and `set-aside.json`, written before anything is moved, that says what was there. A
/// set-aside left by a cook that stopped, or whose putting back failed, is put back before the next cook
/// ([`recover_set_aside`]) and deleted only after a cook that ended well.
struct SetAside {
    prev: PathBuf,
}

const PREV_PREFIX: &str = ".kiln-cooked-prev-";
const SET_ASIDE_INDEX: &str = "set-aside.json";

/// `set-aside.json`: what a set-aside holds, so it can be put back by any later run.
#[derive(Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct SetAsideIndex {
    format: u32,
    /// The folder had an `assets/cooked` (renamed to `cooked/` here once the index is written).
    had_cooked: bool,
    /// Each receipt of the folder: where it lives in the game's store, and its copy here.
    receipts: Vec<(PathBuf, String)>,
    /// Each file of the mod a cook may write: where it lives, and its copy here (None: it was not there).
    own: Vec<(PathBuf, Option<String>)>,
}

#[cfg(test)]
thread_local! {
    /// The next [`SetAside::restore`] on this thread fails, as a sharing violation or a permission would.
    static FAIL_RESTORE: std::cell::Cell<bool> = const { std::cell::Cell::new(false) };
}

fn own_files(dir: &Path) -> Vec<PathBuf> {
    let mut v = vec![dir.join("assets/assets.json"), dir.join("assets/cook.json")];
    v.extend(std::fs::read_dir(dir.join("blocks")).into_iter().flatten().flatten().map(|e| e.path())
        .filter(|p| p.file_name().and_then(|n| n.to_str()).is_some_and(|n| n.ends_with(".materials.json"))));
    v.sort();
    v
}

fn left_over(dir: &Path) -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(dir.join("assets")).into_iter().flatten().flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with(PREV_PREFIX) && e.file_type().is_ok_and(|t| t.is_dir()))
        .map(|e| e.path()).collect();
    v.sort();
    v
}

/// Puts back every set-aside an earlier cook left in `dir` (the last state that worked), oldest first. Err when one
/// cannot be put back: it stays where it is, and the caller does not cook.
fn recover_set_aside(dir: &Path, game: Option<&Path>) -> Result<()> {
    for prev in left_over(dir) {
        SetAside { prev }.restore(dir, game)?;
    }
    Ok(())
}

/// A clean cook's start: copies of the folder's receipts in the game and of the mod's files a cook may write, an index
/// of what was there, then `assets/cooked` moved aside (a link in its place is refused, never followed), then the
/// receipts taken out of the store. A stop at any point leaves a set-aside [`recover_set_aside`] puts back.
fn set_aside(dir: &Path, game: Option<&Path>) -> Result<SetAside> {
    let assets = dir.join("assets");
    let cooked = assets.join("cooked");
    crate::engine_mods::refuse_symlink_path(dir, &cooked)?;
    let hex: String = crate::util::random_bytes::<6>()?.iter().map(|b| format!("{b:02x}")).collect();
    let prev = assets.join(format!("{PREV_PREFIX}{hex}"));
    let kept = SetAside { prev: prev.clone() };
    let saved = (|| -> Result<bool> {
        std::fs::create_dir_all(prev.join("receipts")).map_err(crate::util::io_at(&prev, "creating"))?;
        std::fs::create_dir_all(prev.join("own")).map_err(crate::util::io_at(&prev, "creating"))?;
        let mut receipts = Vec::new();
        if let Some(game) = game {
            for (i, (file, _)) in receipts_of(game, dir).into_iter().enumerate() {
                let copy = format!("receipts/{i}.json");
                std::fs::copy(&file, prev.join(&copy)).map_err(crate::util::io_at(&file, "keeping"))?;
                receipts.push((file, copy));
            }
        }
        let mut own = Vec::new();
        for (i, file) in own_files(dir).into_iter().enumerate() {
            if file.is_file() {
                let copy = format!("own/{i}");
                std::fs::copy(&file, prev.join(&copy)).map_err(crate::util::io_at(&file, "keeping"))?;
                own.push((file, Some(copy)));
            } else {
                own.push((file, None));
            }
        }
        let had_cooked = std::fs::symlink_metadata(&cooked).is_ok_and(|m| m.is_dir());
        crate::util::write_json_atomic(&prev.join(SET_ASIDE_INDEX), &SetAsideIndex { format: 1, had_cooked, receipts: receipts.clone(), own })?;
        Ok(had_cooked)
    })();
    let had_cooked = match saved {
        Ok(h) => h,
        Err(e) => {
            let _ = std::fs::remove_dir_all(&prev); // nothing was moved yet
            return Err(e);
        }
    };
    if had_cooked {
        std::fs::rename(&cooked, prev.join("cooked")).map_err(crate::util::io_at(&cooked, "setting aside"))?;
    } else if std::fs::symlink_metadata(&cooked).is_ok() {
        std::fs::remove_file(&cooked).map_err(crate::util::io_at(&cooked, "removing"))?;
    }
    if let Some(game) = game {
        for (file, _) in receipts_of(game, dir) {
            let _ = std::fs::remove_file(file);
        }
    }
    Ok(kept)
}

impl SetAside {
    /// The cook ended well: what was set aside goes.
    fn drop_old(self) {
        let _ = std::fs::remove_dir_all(&self.prev);
    }

    /// Puts back what was set aside (its cooked files, their receipts, the mod's own files as they were); what a cook
    /// made since goes. The set-aside is deleted only once all of it is back; Err leaves it where it is.
    fn restore(&self, dir: &Path, game: Option<&Path>) -> Result<()> {
        #[cfg(test)]
        if FAIL_RESTORE.with(|f| f.replace(false)) {
            return Err(Error::new(ErrorCode::Io, "injected: the set-aside could not be put back"));
        }
        let Some(index) = crate::util::read_json::<SetAsideIndex>(&self.prev.join(SET_ASIDE_INDEX)).ok().flatten() else {
            // no index: the set-aside stopped before it moved or took out anything
            return std::fs::remove_dir_all(&self.prev).map_err(crate::util::io_at(&self.prev, "removing"));
        };
        let cooked = dir.join("assets").join("cooked");
        crate::engine_mods::refuse_symlink_path(dir, &cooked)?;
        let saved = self.prev.join("cooked");
        let moved = index.had_cooked && saved.is_dir();
        // what the folder's assets/cooked is now was made since, unless the set-aside stopped before moving it
        if (moved || !index.had_cooked) && std::fs::symlink_metadata(&cooked).is_ok() {
            std::fs::remove_dir_all(&cooked).or_else(|_| std::fs::remove_file(&cooked)).map_err(crate::util::io_at(&cooked, "removing"))?;
        }
        if moved {
            std::fs::rename(&saved, &cooked).map_err(crate::util::io_at(&cooked, "putting back"))?;
        }
        if let Some(game) = game {
            for (file, _) in receipts_of(game, dir) {
                let _ = std::fs::remove_file(file);
            }
        }
        for (file, copy) in &index.receipts {
            let bytes = std::fs::read(self.prev.join(copy)).map_err(crate::util::io_at(&self.prev.join(copy), "reading"))?;
            crate::util::write_atomic(file, &bytes)?;
        }
        let before: Vec<&PathBuf> = index.own.iter().map(|(f, _)| f).collect();
        for f in own_files(dir) {
            if !before.contains(&&f) {
                std::fs::remove_file(&f).map_err(crate::util::io_at(&f, "removing"))?; // made since
            }
        }
        for (file, copy) in &index.own {
            match copy {
                Some(c) => {
                    let b = std::fs::read(self.prev.join(c)).map_err(crate::util::io_at(&self.prev.join(c), "reading"))?;
                    if std::fs::read(file).ok().as_deref() != Some(b.as_slice()) {
                        crate::util::write_atomic(file, &b)?;
                    }
                }
                None if file.is_file() => std::fs::remove_file(file).map_err(crate::util::io_at(file, "removing"))?,
                None => {}
            }
        }
        std::fs::remove_dir_all(&self.prev).map_err(crate::util::io_at(&self.prev, "removing"))
    }
}

// ---------------------------------------------------------------------------------------------- receipts ----

/// A cook's receipt: `<game>/kiln/state/cook-receipts/<key>.json`, written by the cook tools after a cook that read
/// that game (tools/modelcook/cook/cookpaths.py `receipt`; one per mod folder, recipe and tool). It lists every file
/// the cook wrote into the mod's `assets/cooked`, by its path below the mod's `assets` folder, with its SHA-256.
/// kiln.assets of that game loads a cooked file only when a receipt lists its bytes; [`plan`] checks a cooked folder
/// against the receipts that name it.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Receipt {
    pub format: u32,
    pub tool: String,
    /// The mod folder, as the tool was given it.
    #[serde(rename = "mod")]
    pub folder: String,
    /// The recipe, relative to the mod folder.
    pub recipe: String,
    #[serde(default)]
    pub at: String,
    /// `cooked/<file>` -> SHA-256 (hex).
    #[serde(default)]
    pub files: BTreeMap<String, String>,
}

/// The receipts folder of a game.
pub fn receipts_dir(game_dir: &Path) -> PathBuf {
    game_dir.join("kiln").join("state").join("cook-receipts")
}

fn same_folder(a: &Path, b: &Path) -> bool {
    a == b || std::fs::canonicalize(a).is_ok_and(|a| std::fs::canonicalize(b).is_ok_and(|b| a == b))
}

/// The receipts in `game_dir` of the cooks of `folder`, each with its file.
pub fn receipts_of(game_dir: &Path, folder: &Path) -> Vec<(PathBuf, Receipt)> {
    // a store that is a link (or under one, from the game's folder down) is no store: kiln.assets refuses it too
    if crate::engine_mods::refuse_symlink_path(game_dir, &receipts_dir(game_dir)).is_err() {
        return Vec::new();
    }
    let mut out: Vec<(PathBuf, Receipt)> = std::fs::read_dir(receipts_dir(game_dir)).into_iter().flatten().flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "json") && std::fs::symlink_metadata(p).is_ok_and(|m| m.is_file() && m.len() < 4 << 20))
        .filter_map(|p| crate::util::read_json::<Receipt>(&p).ok().flatten().filter(|r| r.format == 1).map(|r| (p, r)))
        .filter(|(_, r)| same_folder(Path::new(&r.folder), folder))
        .collect();
    out.sort_by(|a, b| a.0.cmp(&b.0));
    out
}

/// What the receipts of `folder` in `game_dir` say of its cooked files now: (how many files they list, whether every
/// one of them is there with the bytes the cook made). A listed path that leaves the folder counts as changed.
pub fn check_receipts(game_dir: &Path, folder: &Path) -> (usize, bool) {
    let assets = folder.join("assets");
    let mut listed = 0;
    let mut intact = true;
    for (_, r) in receipts_of(game_dir, folder) {
        for (rel, want) in &r.files {
            listed += 1;
            let safe = !rel.is_empty() && !rel.starts_with('/') && !rel.contains('\\')
                && rel.split('/').all(|p| !p.is_empty() && p != "." && p != "..") && rel.split('/').next() == Some("cooked");
            let file = assets.join(rel);
            intact &= safe
                && crate::engine_mods::refuse_symlink_path(folder, &file).is_ok()
                && crate::hash::sha256_file(&file).is_ok_and(|(have, _)| have.eq_ignore_ascii_case(want));
        }
    }
    (listed, intact)
}

// ------------------------------------------------------------------------------------- what needs a cook ----

/// `<game>/kiln/state/cooked.json`: what was cooked for this game, by mod id. Written by the launcher after each
/// mod's cook; [`needs_cook`] reads it. It is about one game folder: a server and a client each have their own.
#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookedState {
    pub format: u32,
    #[serde(default)]
    pub mods: BTreeMap<String, CookedMod>,
}

/// The last cook of one mod for one game.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookedMod {
    /// The mod's identity then (`hash::mod_identity`: its files without its cook outputs).
    pub identity: String,
    /// The game's build then ([`game_build`]).
    pub game_build: String,
    /// When (RFC 3339).
    pub at: String,
    pub ok: bool,
    /// The cook made files (its receipts in the game list some): a mod whose blocks need nothing cooked made none, and
    /// is not cooked again for having none.
    #[serde(default)]
    pub files: bool,
    /// A failed cook: the last lines the tools said.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

/// Why a mod is cooked before the game starts ([`needs_cook`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum CookReason {
    /// It was never cooked for this game: the mod was installed, or switched on for the first time.
    NotCooked,
    /// Its files changed since its last cook: the mod was updated.
    ModChanged,
    /// The game's build changed since its last cook: cooked files are made from the game's data.
    GameChanged,
    /// Its last cook failed.
    LastFailed,
    /// The files its last cook made are gone, or are not the bytes that cook made.
    FilesGone,
    /// The player asked ("Cook again").
    Asked,
}

impl CookReason {
    /// The reason in a sentence's place: "… because …".
    pub fn text(self) -> &'static str {
        match self {
            CookReason::NotCooked => "it was not cooked for this game yet",
            CookReason::ModChanged => "the mod changed since its last cook",
            CookReason::GameChanged => "the game was updated since its last cook",
            CookReason::LastFailed => "its last cook failed",
            CookReason::FilesGone => "its cooked files are gone or were changed",
            CookReason::Asked => "you asked",
        }
    }
}

/// The one rule for "this mod needs a cook before the game starts". `last`: its record in [`CookedState`] (None: never
/// cooked for this game); `identity`: the mod's identity now; `game_build`: the game's build now ([`game_build`]);
/// `files_intact`: every file its receipts in the game list is there with the bytes that cook made ([`check_receipts`],
/// whatever the record says); with no receipt, the last cook made nothing and no cooked file is in the folder ([`plan`]).
///
/// In this order: never cooked (installed, or switched on for the first time); the mod's files changed (updated); the
/// game's build changed; the last cook failed (tried again at every start: it stops nothing); the files its last cook
/// made are gone or changed. Else None: the tools are not even started.
pub fn needs_cook(last: Option<&CookedMod>, identity: &str, game_build: &str, files_intact: bool) -> Option<CookReason> {
    let Some(last) = last else { return Some(CookReason::NotCooked) };
    if last.identity != identity {
        Some(CookReason::ModChanged)
    } else if last.game_build != game_build {
        Some(CookReason::GameChanged)
    } else if !last.ok {
        Some(CookReason::LastFailed)
    } else if !files_intact {
        Some(CookReason::FilesGone)
    } else {
        None
    }
}

/// Whether a folder's `assets/cooked` holds any file (a link counts: it is not the cook's either).
fn has_cooked_files(folder: &Path) -> bool {
    fn walk(d: &Path, depth: u32) -> bool {
        depth < 16 && std::fs::read_dir(d).into_iter().flatten().flatten().any(|e| match e.file_type() {
            Ok(t) if t.is_dir() => walk(&e.path(), depth + 1),
            Ok(_) => true,
            Err(_) => false,
        })
    }
    walk(&folder.join("assets").join("cooked"), 0)
}

/// What tells one build of the game from the next, cheaply: Steam's build id when the install has one, else the size
/// and the write time of the game's data file (a game update replaces it). Never a hash of 20 GB.
pub fn game_build(install: &GameInstall) -> String {
    if let Some(id) = install.build_id.as_deref().filter(|id| !id.is_empty()) {
        return format!("steam:{id}");
    }
    let stem = install.exe.strip_suffix(".exe").unwrap_or(&install.exe);
    for file in [format!("{stem}.kfc"), install.exe.clone()] {
        if let Ok(m) = std::fs::metadata(install.path.join(&file)) {
            let at = m.modified().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map_or(0, |d| d.as_secs());
            return format!("file:{file}:{}:{at}", m.len());
        }
    }
    "unknown".into()
}

fn state_file(game_dir: &Path) -> PathBuf {
    game_dir.join("kiln/state/cooked.json")
}

/// The record of a game (empty when there is none yet, or it cannot be read: everything is cooked again then).
pub fn read_state(game_dir: &Path) -> CookedState {
    crate::util::read_json::<CookedState>(&state_file(game_dir)).ok().flatten().filter(|s| s.format == 1).unwrap_or(CookedState { format: 1, mods: BTreeMap::new() })
}

fn write_state(game_dir: &Path, state: &CookedState) -> Result<()> {
    let file = state_file(game_dir);
    crate::engine_mods::refuse_symlink_path(game_dir, &file)?;
    if let Some(p) = file.parent() {
        std::fs::create_dir_all(p).map_err(crate::util::io_at(p, "creating"))?;
    }
    crate::util::write_json_atomic(&file, state)
}

/// A mod of the game's profile that has something to cook.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookJob {
    pub id: String,
    /// The mod's name (its manifest's, else its id).
    pub name: String,
    /// The folder the game loads the mod from: where the cook writes `assets/cooked`.
    pub folder: PathBuf,
    pub identity: String,
    /// None: cooked for this build, nothing to do.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<CookReason>,
    /// Its last cook for this game, if any.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub last: Option<CookedMod>,
}

/// The enabled Kiln mods of the profile the game uses that have something to cook, each with whether it needs a cook
/// now ([`needs_cook`]) and its last cook. The folders are the ones the game's `kiln/profile.json` names (the Library
/// entry, or its copy in the game where the game cannot read the Library), so the game finds what the cook makes, and
/// each folder's cooked files are checked against their receipts. A mod whose folder is not there (not activated yet)
/// is left out. Nothing is written.
pub fn plan(install: &GameInstall) -> Result<Vec<CookJob>> {
    let Some(file) = crate::profile::read_game_profile(&install.path)? else { return Ok(Vec::new()) };
    let state = read_state(&install.path);
    let build = game_build(install);
    let mut jobs = Vec::new();
    for (m, folder) in crate::launch::kiln_folders(install, &file) {
        if !folder.join(crate::hash::MOD_MANIFEST).is_file() || !has_recipe(&folder) {
            continue;
        }
        let Ok(identity) = crate::hash::mod_identity(&folder) else { continue };
        let name = crate::manifest::read_kiln(&folder).map(|k| k.name).ok().filter(|n| !n.trim().is_empty()).unwrap_or_else(|| m.id.clone());
        let last = state.mods.get(&m.id).cloned();
        // only read when the rule gets that far (the files of a mod that is due anyway are not hashed): a last cook that
        // made files is intact while its receipts list files and every one of them is there as it was made
        let intact = match &last {
            Some(l) if l.ok && l.identity == identity && l.game_build == build => {
                // what the receipts list must be there as made, whatever the record says; with no receipt, the record
                // must say the cook made nothing, and no cooked file may be there (none would load where the rule is on)
                let (listed, as_made) = check_receipts(&install.path, &folder);
                if listed > 0 { as_made } else { !l.files && !has_cooked_files(&folder) }
            }
            _ => true,
        };
        let reason = needs_cook(last.as_ref(), &identity, &build, intact);
        jobs.push(CookJob { id: m.id.clone(), name, folder, identity, reason, last });
    }
    Ok(jobs)
}

/// How one mod's cook ended ([`cook_mods`]).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ModCook {
    pub id: String,
    pub name: String,
    pub reason: CookReason,
    pub ok: bool,
    /// Not cooked: why, in the tools' own words (their last lines), or the one sentence that says why no tool ran.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub error: Option<String>,
}

/// What [`cook_mods`] did. A failed mod never stops the others, and never the start of the game.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CookRun {
    pub mods: Vec<ModCook>,
    /// The active profile written again before the cook ([`cook_due`]: it changed since its activation), for the
    /// notices an activation brings. None: it was as it should be.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub activation: Option<crate::profile::Activation>,
}

impl CookRun {
    pub fn failed(&self) -> impl Iterator<Item = &ModCook> {
        self.mods.iter().filter(|m| !m.ok)
    }
}

/// The end of what the tools said, as a player reads it: the last lines, without the lines that only count clips.
pub fn error_text(text: &str, why: &str) -> String {
    let lines: Vec<&str> = text.lines().map(str::trim_end).filter(|l| !l.trim().is_empty()).collect();
    let tail: Vec<&str> = lines.iter().rev().take(12).rev().copied().collect();
    if tail.is_empty() { why.to_string() } else { tail.join("\n") }
}

/// Cooks the jobs that have a reason, one after the other, and records each in the game's [`CookedState`]. `cook` runs
/// one mod's cook (the launcher: [`cook_folder_watched`] with the tools it located; tests: anything) and returns
/// whether it ended well and what the tools said. `tools`: Err when no tool can run (no Python, no zstd): every mod is
/// then reported as not cooked with that one sentence, and nothing else happens. `on_event(index, total, job, line)`
/// gets the start of each mod (`line` None) and every line its tools print. When `stop()` says stop, the mod being
/// cooked is recorded as not cooked and no other starts: Err(`Canceled`).
pub fn cook_mods(install: &GameInstall, jobs: &[CookJob], tools: std::result::Result<(), String>,
                 cook: &mut dyn FnMut(&CookJob, &mut dyn FnMut(&str)) -> (bool, String),
                 on_event: &mut dyn FnMut(usize, usize, &CookJob, Option<&str>), stop: &dyn Fn() -> bool) -> Result<CookRun> {
    let due: Vec<&CookJob> = jobs.iter().filter(|j| j.reason.is_some()).collect();
    let mut run = CookRun::default();
    if due.is_empty() {
        return Ok(run);
    }
    let build = game_build(install);
    let mut state = read_state(&install.path);
    for (i, job) in due.iter().enumerate() {
        if stop() {
            return Err(Error::new(ErrorCode::Canceled, "the cook was stopped"));
        }
        let reason = job.reason.expect("filtered above");
        on_event(i, due.len(), job, None);
        let (ok, error) = match &tools {
            Err(why) => (false, Some(why.clone())),
            Ok(()) => {
                let (ok, text) = cook(job, &mut |line| on_event(i, due.len(), job, Some(line)));
                (ok, (!ok).then(|| error_text(&text, "the cook failed without a word")))
            }
        };
        // The identity after the cook is the one before it: a cook writes only into assets/cooked. It is read again all
        // the same. A cook that did change the mod's own files (the steps of its blocks, written into assets/cook.json
        // or blocks/*.materials.json of a mod whose author did not ship them) is recorded as what the folder is now,
        // so the mod is not cooked again at every start; the Library and a server still see that it is another mod.
        let identity = crate::hash::mod_identity(&job.folder).unwrap_or_else(|_| job.identity.clone());
        let files = check_receipts(&install.path, &job.folder).0 > 0;
        state.mods.insert(job.id.clone(), CookedMod { identity, game_build: build.clone(), at: crate::util::now_rfc3339(), ok, files, error: error.clone() });
        write_state(&install.path, &state)?;
        run.mods.push(ModCook { id: job.id.clone(), name: job.name.clone(), reason, ok, error });
        if !ok && stop() {
            return Err(Error::new(ErrorCode::Canceled, "the cook was stopped"));
        }
    }
    Ok(run)
}

/// The launcher's and the tests' way to say "cook this mod now" whatever the record says ("Cook again").
pub fn asked(mut job: CookJob) -> CookJob {
    job.reason = Some(CookReason::Asked);
    job
}

/// A cook is refused this long after a start of the game: Steam may still be starting it, and it reads the mods' files.
pub const AFTER_START: Duration = Duration::from_secs(120);

/// The launcher's cook, whole. Under the game's lock ([`crate::state::lock_game`]: no start of this game and no other
/// cook of it meanwhile; a start waits for the lock a moment, then says why it cannot start), refused while the game
/// runs, and "Cook again" also while the game was just started ([`AFTER_START`]: Play's own cook comes before its start):
///
/// 1. the active profile is written again when it changed since its activation ([`crate::profile::refresh_active`]),
///    so the mods cooked are the ones the start then loads;
/// 2. the mods of the game's profile that need a cook ([`plan`]), or with `only` that one mod whatever its record says
///    ("Cook again"), are cooked one after the other from this game by the tools `locate` finds
///    ([`crate::cooktools::locate`]: the copy this build carries, unpacked once into its cache). Each from nothing
///    ([`CookOptions::clean`], every recipe: what made the mod due may be the game, which a recipe's own check does not
///    read), within [`Limits::LAUNCHER`];
/// 3. `stop()` (the task was canceled), or the game being started meanwhile, stops the tool that runs with what it
///    started, and no further mod is cooked (`Canceled`, `GameRunning`).
///
/// Nothing is located or started when nothing is due. When no tool can run (no Python, no zstd), every due mod is
/// reported as not cooked with the one sentence that says what to install. `on_event` as in [`cook_mods`].
/// Err: the game's profile or the record cannot be read or written; `only` names no mod of the profile with something
/// to cook (`not_found`). A mod's failed cook is never an Err.
pub fn cook_due(dirs: &crate::paths::DataDirs, install: &GameInstall, only: Option<&str>, locate: &crate::cooktools::Locate,
                on_event: &mut dyn FnMut(usize, usize, &CookJob, Option<&str>), stop: &dyn Fn() -> bool) -> Result<CookRun> {
    refuse_running(crate::proc::find_game(install)?.is_some())?;
    let _game = crate::state::lock_game(&install.path, Duration::from_secs(10), stop)?;
    refuse_running(crate::proc::find_game(install)?.is_some())?;
    if only.is_some() && crate::launch::started_within(dirs, install, AFTER_START) {
        return Err(Error::new(ErrorCode::GameRunning, "The game was just started: a cook now would write the files it is reading. Cook again once it runs no more."));
    }
    let activation = crate::profile::refresh_active(dirs, install)?;
    let mut jobs = plan(install)?;
    if let Some(id) = only {
        jobs = jobs.into_iter().filter(|j| j.id == id).map(asked).collect();
        if jobs.is_empty() {
            return Err(Error::new(ErrorCode::NotFound, format!("{id} is not a mod of this game's profile with something to cook")));
        }
    }
    if jobs.iter().all(|j| j.reason.is_none()) {
        return Ok(CookRun { mods: Vec::new(), activation });
    }
    let tools = crate::cooktools::locate(locate);
    let ready = tools.as_ref().map(|_| ()).map_err(|e| e.message.clone());
    let opts = CookOptions { game: Some(install.path.clone()), force: true, clean: true, limits: Limits::LAUNCHER, ..CookOptions::default() };
    // the game started from Steam while the mods are cooked: looked at every two seconds, it stops the cook like a cancel
    let seen = std::cell::Cell::new(Instant::now() - Duration::from_secs(10));
    let started = std::cell::Cell::new(false);
    let halt = || {
        if stop() {
            return true;
        }
        if !started.get() && seen.get().elapsed() >= Duration::from_secs(2) {
            seen.set(Instant::now());
            started.set(crate::proc::find_game(install).ok().flatten().is_some());
        }
        started.get()
    };
    let mut run = cook_mods(install, &jobs, ready,
        &mut |job, line| match tools.as_ref() {
            Ok(t) => {
                // a Library entry another game loads too: never cooked while that game runs or starts
                if let Some(why) = used_elsewhere(dirs, install, &job.folder) {
                    line(&format!("kiln: {why}"));
                    return (false, why);
                }
                match cook_folder_watched(t, &job.folder, &opts, &mut |_, l| line(l), &halt) {
                    Ok(r) => (r.ok, r.text),
                    Err(e) => (false, e.message),
                }
            }
            Err(e) => (false, e.message.clone()),
        },
        on_event, &halt)
        .map_err(|e| if started.get() { Error::new(ErrorCode::GameRunning, "The game was started while its mods were cooked: the cook was stopped.") } else { e })?;
    run.activation = activation;
    Ok(run)
}

/// Another game this launcher knows that loads `folder` (its profile file names it: a Library entry two installs read
/// in Z mode) and runs, or was started through Kiln in the last [`AFTER_START`]: a cook of the folder would write the
/// files it reads. -> the sentence that says so.
fn used_elsewhere(dirs: &crate::paths::DataDirs, install: &GameInstall, folder: &Path) -> Option<String> {
    for other in crate::game::known(dirs).ok()? {
        if other.id == install.id || same_folder(&other.path, &install.path) {
            continue;
        }
        let Some(file) = crate::profile::read_game_profile(&other.path).ok().flatten() else { continue };
        if !crate::launch::kiln_folders(&other, &file).iter().any(|(_, f)| same_folder(f, folder)) {
            continue;
        }
        let running = crate::proc::find_game(&other).ok().flatten().is_some();
        if running || crate::launch::started_within(dirs, &other, AFTER_START) {
            return Some(format!("the game in {} loads this mod's folder too and {}: it is cooked once that game runs no more",
                other.path.display(), if running { "is running" } else { "was just started" }));
        }
    }
    None
}

/// A refusal for a game that is running: its files are in use, and it has read the mods already.
pub fn refuse_running(running: bool) -> Result<()> {
    if running {
        return Err(Error::new(ErrorCode::GameRunning, "Close the game first: a cook writes the files the game is using."));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::Digest;

    fn last(identity: &str, build: &str, ok: bool) -> CookedMod {
        CookedMod { identity: identity.into(), game_build: build.into(), at: "2026-11-12T10:00:00Z".into(), ok, files: true, error: (!ok).then(|| "boom".into()) }
    }

    #[test]
    fn needs_cook_is_the_one_rule() {
        // installed, or switched on for the first time: no record for this game
        assert_eq!(needs_cook(None, "kdh1:a", "steam:1", false), Some(CookReason::NotCooked));
        assert_eq!(needs_cook(None, "kdh1:a", "steam:1", true), Some(CookReason::NotCooked), "cooked files nobody recorded are not trusted");
        // cooked for this mod and this game: nothing to do
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:1", true), None);
        // updated: its files are others
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:b", "steam:1", true), Some(CookReason::ModChanged));
        // the game was updated
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:2", true), Some(CookReason::GameChanged));
        // both: the mod's change is named
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:b", "steam:2", true), Some(CookReason::ModChanged));
        // the last cook failed: again at the next start
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", false)), "kdh1:a", "steam:1", true), Some(CookReason::LastFailed));
        // a file its last cook made is gone or changed (a copy made again, a folder cleaned by hand, a file swapped)
        assert_eq!(needs_cook(Some(&last("kdh1:a", "steam:1", true)), "kdh1:a", "steam:1", false), Some(CookReason::FilesGone));
        // a mod whose cook had nothing to make (blocks without looks) made no file: plan calls it intact while no
        // cooked file is there, and it is not cooked again for that
        let nothing = CookedMod { files: false, ..last("kdh1:a", "steam:1", true) };
        assert_eq!(needs_cook(Some(&nothing), "kdh1:a", "steam:1", true), None);
        assert_eq!(needs_cook(Some(&nothing), "kdh1:a", "steam:1", false), Some(CookReason::FilesGone), "the record says nothing was made, the folder says otherwise");
    }

    #[test]
    fn the_tools_last_lines_are_the_error_a_player_reads() {
        let text = (1..=30).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n") + "\n\nmodelcook: input bronze: not in the game (game update or wrong side?)\n";
        let e = error_text(&text, "x");
        assert!(e.ends_with("modelcook: input bronze: not in the game (game update or wrong side?)"), "{e}");
        assert_eq!(e.lines().count(), 12);
        assert_eq!(error_text("  \n", "the cook failed without a word"), "the cook failed without a word");
    }

    #[test]
    fn recipes_and_blocks_make_a_mod_cookable() {
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-has").unwrap();
        let m = d.path().join("m");
        std::fs::create_dir_all(m.join("assets")).unwrap();
        assert!(!has_recipe(&m));
        std::fs::create_dir_all(m.join("blocks")).unwrap();
        assert!(!has_recipe(&m), "an empty blocks folder");
        std::fs::write(m.join("blocks/items.json"), b"{}").unwrap();
        assert!(has_recipe(&m));
        std::fs::remove_dir_all(m.join("blocks")).unwrap();
        std::fs::create_dir_all(m.join("impact")).unwrap();
        std::fs::write(m.join("impact/cook.json"), b"{}").unwrap();
        std::fs::write(m.join("assets/cook.json"), b"{}").unwrap();
        assert_eq!(recipes(&m), [m.join("assets/cook.json"), m.join("impact/cook.json")]);
        assert!(has_recipe(&m));
    }

    #[cfg(unix)]
    fn sh(script: &str) -> Command {
        let mut c = Command::new("sh");
        c.arg("-c").arg(script);
        c
    }

    /// A tool that hangs, that hangs without a word, or that leaves a process holding its output: each is stopped
    /// within its limit, with every process it started, and the run goes on.
    #[cfg(unix)]
    #[test]
    fn a_hung_tool_is_stopped_with_what_it_started() {
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-hang").unwrap();
        let pid_file = d.path().join("grandchild.pid");
        // the tool starts a process of its own that would run (and hold the output) for ten minutes
        let script = format!("sleep 600 & echo $! > '{}'; echo started; sleep 600", pid_file.display());
        let t = Instant::now();
        let mut lines = Vec::new();
        let end = run_tool(&mut sh(&script), Some(Duration::from_millis(1500)), None, &|| false, &mut |_, l| lines.push(l.to_string())).unwrap();
        assert!(t.elapsed() < Duration::from_secs(10), "{:?}", t.elapsed());
        assert!(!end.ok && end.stopped.as_deref().is_some_and(|s| s.contains("stopped after")), "{end:?}");
        assert_eq!(lines, ["started"]);
        let grandchild: i32 = std::fs::read_to_string(&pid_file).unwrap().trim().parse().unwrap();
        std::thread::sleep(Duration::from_millis(200));
        // SAFETY: kill(pid, 0) only asks whether the process is there.
        assert_ne!(unsafe { libc::kill(grandchild, 0) }, 0, "what the tool started is stopped too");
        // silent for too long
        let end = run_tool(&mut sh("echo one; sleep 600"), Some(Duration::from_secs(60)), Some(Duration::from_millis(800)), &|| false, &mut |_, _| {}).unwrap();
        assert!(end.stopped.as_deref().is_some_and(|s| s.contains("said nothing")), "{end:?}");
        // canceled
        let asked = std::cell::Cell::new(0);
        let end = run_tool(&mut sh("sleep 600"), Some(Duration::from_secs(60)), None, &|| { asked.set(asked.get() + 1); asked.get() > 3 }, &mut |_, _| {}).unwrap();
        assert!(end.stopped.as_deref().is_some_and(|s| s.contains("canceled")), "{end:?}");
        // a tool that prints a line every 10 ms for ever: the deadline and a cancel still stop it
        let chatty = "while true; do echo tick; sleep 0.01; done";
        let t = Instant::now();
        let end = run_tool(&mut sh(chatty), Some(Duration::from_millis(1500)), Some(Duration::from_secs(60)), &|| false, &mut |_, _| {}).unwrap();
        assert!(end.stopped.as_deref().is_some_and(|s| s.contains("stopped after")) && t.elapsed() < Duration::from_secs(6), "{end:?} {:?}", t.elapsed());
        let lines = std::cell::Cell::new(0);
        let t = Instant::now();
        let end = run_tool(&mut sh(chatty), Some(Duration::from_secs(60)), None, &|| lines.get() > 50, &mut |_, _| lines.set(lines.get() + 1)).unwrap();
        assert!(end.stopped.as_deref().is_some_and(|s| s.contains("canceled")) && t.elapsed() < Duration::from_secs(6), "{end:?} {:?}", t.elapsed());
        // a tool that ended while a process it started still holds its output: not waited for past AFTER_EXIT
        let t = Instant::now();
        let end = run_tool(&mut sh("sleep 600 & echo done"), Some(Duration::from_secs(60)), None, &|| false, &mut |_, _| {}).unwrap();
        assert!(end.ok && end.stopped.is_none(), "{end:?}");
        assert!(t.elapsed() < AFTER_EXIT + Duration::from_secs(5), "{:?}", t.elapsed());
        // a check that hangs reads as "a cook is needed", within the step limit
        let t = Instant::now();
        let (end, _, _) = run_step(&mut sh("sleep 600"), Some(Duration::from_millis(500)), &|| false).unwrap();
        assert!(!end.ok && t.elapsed() < Duration::from_secs(5));
        // without a limit (kiln cook in a terminal) a tool runs in the terminal's process group, as before
        let end = run_tool(&mut sh("echo $$ ; exit 3"), None, None, &|| false, &mut |_, _| {}).unwrap();
        assert_eq!((end.ok, end.code, end.stopped), (false, Some(3), None));
    }

    /// A huge line or a flood of output: each line is cut at LINE_MAX, and what is kept of the text is bounded.
    #[cfg(unix)]
    #[test]
    fn a_tools_output_is_bounded() {
        let mut longest = 0;
        let mut n = 0;
        let mut tail = Tail::default();
        // a line of 8 MB without a newline, then 40000 short lines (more than TEXT_KEPT together)
        let end = run_tool(&mut sh("head -c 8000000 /dev/zero | tr '\\0' x; echo; i=0; while [ $i -lt 40000 ]; do echo line $i; i=$((i+1)); done"),
            Some(Duration::from_secs(120)), None, &|| false, &mut |_, l| { longest = longest.max(l.len()); n += 1; tail.push(l) }).unwrap();
        assert!(end.ok, "{end:?}");
        assert_eq!(n, 40001);
        assert!(longest <= LINE_MAX + '…'.len_utf8(), "{longest}");
        let text = tail.text();
        assert!(text.len() <= TEXT_KEPT + 8, "{}", text.len());
        assert!(text.starts_with("…\n") && text.ends_with("line 39999\n"), "{}", &text[..40]);
        assert_eq!(error_text(&text, "x").lines().last(), Some("line 39999"));
    }

    #[test]
    fn receipts_say_whether_a_folder_holds_what_its_cook_made() {
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-receipts").unwrap();
        let (game, folder) = (d.path().join("game"), d.path().join("game/kiln/profiles/p/kiln/blades"));
        std::fs::create_dir_all(folder.join("assets/cooked")).unwrap();
        std::fs::write(folder.join("assets/cooked/a.bin"), b"made here").unwrap();
        assert_eq!(check_receipts(&game, &folder), (0, true), "no receipt: nothing listed");
        let digest = crate::hash::hex(&sha2::Sha256::digest(b"made here"));
        let receipt = |files: serde_json::Value| serde_json::json!({ "format": 1, "tool": "modelcook", "mod": folder, "recipe": "assets/cook.json", "files": files });
        std::fs::create_dir_all(receipts_dir(&game)).unwrap();
        crate::util::write_json_atomic(&receipts_dir(&game).join("r1.json"), &receipt(serde_json::json!({ "cooked/a.bin": digest }))).unwrap();
        // another folder's receipt is not this folder's
        crate::util::write_json_atomic(&receipts_dir(&game).join("r2.json"),
            &serde_json::json!({ "format": 1, "tool": "impact", "mod": d.path().join("elsewhere"), "recipe": "impact/cook.json", "files": { "cooked/z.bin": digest } })).unwrap();
        assert_eq!(check_receipts(&game, &folder), (1, true));
        assert_eq!(receipts_of(&game, &folder).len(), 1);
        std::fs::write(folder.join("assets/cooked/a.bin"), b"made elsewhere").unwrap();
        assert_eq!(check_receipts(&game, &folder), (1, false), "changed bytes");
        std::fs::remove_file(folder.join("assets/cooked/a.bin")).unwrap();
        assert_eq!(check_receipts(&game, &folder), (1, false), "gone");
        // a receipt that names a path out of assets/cooked is no proof of anything
        std::fs::write(folder.join("kiln.mod.json"), b"made here").unwrap();
        crate::util::write_json_atomic(&receipts_dir(&game).join("r1.json"), &receipt(serde_json::json!({ "../kiln.mod.json": digest }))).unwrap();
        assert_eq!(check_receipts(&game, &folder), (1, false));
        // a clean cook's start: the cooked folder and this folder's receipts are set aside, another folder's stay
        std::fs::write(folder.join("assets/cooked/b.bin"), b"x").unwrap();
        std::fs::write(folder.join("assets/assets.json"), b"{ \"shipped\": true }").unwrap();
        let kept = set_aside(&folder, Some(&game)).unwrap();
        assert!(!folder.join("assets/cooked").exists() && folder.join("kiln.mod.json").is_file());
        assert!(receipts_of(&game, &folder).is_empty() && receipts_dir(&game).join("r2.json").is_file());
        // the cook fails after it wrote things: all of it goes, and the folder has what it had
        std::fs::create_dir_all(folder.join("assets/cooked")).unwrap();
        std::fs::write(folder.join("assets/cooked/half.bin"), b"half").unwrap();
        std::fs::write(folder.join("assets/assets.json"), b"{ \"rewritten\": true }").unwrap();
        std::fs::create_dir_all(folder.join("blocks")).unwrap();
        std::fs::write(folder.join("blocks/new.materials.json"), b"{}").unwrap();
        crate::util::write_json_atomic(&receipts_dir(&game).join("r3.json"), &receipt(serde_json::json!({ "cooked/half.bin": digest }))).unwrap();
        kept.restore(&folder, Some(&game)).unwrap();
        assert_eq!(std::fs::read(folder.join("assets/cooked/b.bin")).unwrap(), b"x");
        assert!(!folder.join("assets/cooked/half.bin").exists() && !folder.join("blocks/new.materials.json").exists());
        assert_eq!(std::fs::read(folder.join("assets/assets.json")).unwrap(), b"{ \"shipped\": true }");
        assert_eq!(receipts_of(&game, &folder).iter().map(|(f, _)| f.file_name().unwrap().to_string_lossy().into_owned()).collect::<Vec<_>>(), ["r1.json"]);
        // it ends well: what was set aside goes, nothing of it is left
        let kept = set_aside(&folder, Some(&game)).unwrap();
        std::fs::create_dir_all(folder.join("assets/cooked")).unwrap();
        kept.drop_old();
        assert!(std::fs::read_dir(folder.join("assets")).unwrap().flatten().all(|e| !e.file_name().to_string_lossy().starts_with(PREV_PREFIX)));
        // a link in place of the cooked folder is refused, its target left alone
        #[cfg(unix)]
        {
            std::fs::remove_dir_all(folder.join("assets/cooked")).unwrap();
            std::fs::create_dir_all(d.path().join("theirs")).unwrap();
            std::fs::write(d.path().join("theirs/keep.bin"), b"keep").unwrap();
            std::os::unix::fs::symlink(d.path().join("theirs"), folder.join("assets/cooked")).unwrap();
            assert!(set_aside(&folder, Some(&game)).is_err());
            assert!(d.path().join("theirs/keep.bin").is_file());
            std::fs::remove_file(folder.join("assets/cooked")).unwrap();
            // and a receipt store that is a link is no store
            let store = receipts_dir(&game);
            std::fs::rename(&store, d.path().join("store-elsewhere")).unwrap();
            std::os::unix::fs::symlink(d.path().join("store-elsewhere"), &store).unwrap();
            assert!(receipts_of(&game, &folder).is_empty());
        }
    }

    /// A clean cook whose tool fails and whose putting back fails too (a sharing violation, a permission) leaves the
    /// set-aside on disk; the next cook puts it back before anything else, so when that cook fails as well the folder
    /// still has the files and receipts of its last good cook. A set-aside that cannot be put back stops the cook (the
    /// message names the folder); it goes only after a cook that ended well.
    #[cfg(unix)]
    #[test]
    fn a_set_aside_that_could_not_be_put_back_survives_the_next_failed_cook() {
        use crate::cooktools::{CookTools, Python, Source};
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-setaside").unwrap();
        let (game, folder) = (d.path().join("game"), d.path().join("game/kiln/profiles/p/kiln/blades"));
        std::fs::create_dir_all(game.join("kiln/state")).unwrap();
        std::fs::create_dir_all(folder.join("assets/cooked")).unwrap();
        std::fs::write(folder.join("kiln.mod.json"), br#"{ "id": "blades" }"#).unwrap();
        std::fs::write(folder.join("assets/cook.json"), br#"{ "format": 1, "tool": "modelcook", "out": "cooked", "steps": [] }"#).unwrap();
        std::fs::write(folder.join("assets/assets.json"), b"{ \"shipped\": true }").unwrap();
        std::fs::write(folder.join("assets/cooked/a.bin"), b"made by the last good cook").unwrap();
        let digest = crate::hash::hex(&sha2::Sha256::digest(b"made by the last good cook"));
        std::fs::create_dir_all(receipts_dir(&game)).unwrap();
        crate::util::write_json_atomic(&receipts_dir(&game).join("r1.json"),
            &serde_json::json!({ "format": 1, "tool": "modelcook", "mod": folder, "recipe": "assets/cook.json", "files": { "cooked/a.bin": digest } })).unwrap();
        // the cook tools, with a "Python" that fails every run (or, later, ends well)
        let root = d.path().join("tools");
        std::fs::create_dir_all(root.join("tools/modelcook")).unwrap();
        std::fs::write(root.join("tools/modelcook/modelcook.py"), b"").unwrap();
        let tools = |program: &str| CookTools { root: root.clone(), source: Source::Checkout, sound_helper: None,
            python: Python { program: PathBuf::from(program), version: (3, 14), zstd: "stdlib".into(), given: false } };
        let opts = CookOptions { game: Some(game.clone()), force: true, clean: true, ..CookOptions::default() };
        let good = |folder: &Path| std::fs::read(folder.join("assets/cooked/a.bin")).ok().as_deref() == Some(b"made by the last good cook".as_slice())
            && check_receipts(&game, folder) == (1, true) && std::fs::read(folder.join("assets/assets.json")).unwrap() == b"{ \"shipped\": true }";
        assert!(good(&folder));

        // 1. the tool fails, and so does putting back: the set-aside stays on disk, whole
        FAIL_RESTORE.with(|f| f.set(true));
        let mut said = Vec::new();
        let r = cook_folder_watched(&tools("/bin/false"), &folder, &opts, &mut |_, l| said.push(l.to_string()), &|| false).unwrap();
        assert!(!r.ok);
        assert!(said.iter().any(|l| l.contains("could not be put back") && l.contains("put back before its next cook")), "{said:?}");
        let kept = left_over(&folder);
        assert_eq!(kept.len(), 1);
        assert_eq!(std::fs::read(kept[0].join("cooked/a.bin")).unwrap(), b"made by the last good cook");
        assert!(!folder.join("assets/cooked/a.bin").exists() && receipts_of(&game, &folder).is_empty());

        // 2. the next cook, whose tool fails as well: what was set aside came back first, and is there after it
        let r = cook_folder_watched(&tools("/bin/false"), &folder, &opts, &mut |_, _| {}, &|| false).unwrap();
        assert!(!r.ok);
        assert!(good(&folder), "the last good cook's files and receipts");
        assert!(left_over(&folder).is_empty());

        // 3. a set-aside that cannot be put back: the cook does not start, and says where the files are
        FAIL_RESTORE.with(|f| f.set(true));
        cook_folder_watched(&tools("/bin/false"), &folder, &opts, &mut |_, _| {}, &|| false).unwrap();
        FAIL_RESTORE.with(|f| f.set(true));
        let e = cook_folder_watched(&tools("/bin/false"), &folder, &opts, &mut |_, _| {}, &|| false).unwrap_err();
        assert!(e.code == "io" && e.message.contains("set aside") && e.message.contains(&folder.display().to_string()), "{e:?}");
        assert_eq!(left_over(&folder).len(), 1, "still there");
        // 4. once it can be put back, it is; a cook that ends well then deletes its own set-aside, and only it
        let r = cook_folder_watched(&tools("/bin/true"), &folder, &opts, &mut |_, _| {}, &|| false).unwrap();
        assert!(r.ok);
        assert!(left_over(&folder).is_empty());
    }

    /// Two cooks of one folder: the second waits for the first, says so, and can be stopped while it waits.
    #[test]
    fn one_cook_of_a_folder_at_a_time() {
        let d = crate::library::TempDir::new(&std::env::temp_dir(), "cook-lock").unwrap();
        let held = crate::state::lock_file(&crate::state::cook_folder_lock_path(d.path()), Duration::ZERO, &|| false, "busy").unwrap();
        let e = crate::state::lock_file(&crate::state::cook_folder_lock_path(d.path()), Duration::from_millis(300), &|| false, "another cook").unwrap_err();
        assert_eq!((e.code, e.message.as_str()), (ErrorCode::Busy, "another cook"));
        assert_eq!(crate::state::lock_file(&crate::state::cook_folder_lock_path(d.path()), Duration::from_secs(60), &|| true, "x").unwrap_err().code, ErrorCode::Canceled);
        drop(held);
        crate::state::lock_file(&crate::state::cook_folder_lock_path(d.path()), Duration::ZERO, &|| false, "x").unwrap();
    }
}
