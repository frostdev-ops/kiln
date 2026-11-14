// SPDX-License-Identifier: MIT
//! The boot game-data snapshot: the offline view of the game's items, templates, skill nodes and materials that
//! `Mod.OnBoot` reads before the game has loaded anything (contract c-08, docs/kiln/modding/boot-snapshot.md).
//!
//! kiln.boot never decompresses the game's archive itself: the snapshot is made before the start, from the game's own
//! files, by `tools/bootsnap/bootsnap.py` (one of the Python tools this crate carries, run like the cook tools:
//! [`crate::cooktools`]). It lands in `<game>/kiln/data/boot-gamedata/<key>/` (about 200 MB per game build), once per
//! game build: a later start finds it by its key in well under a second. The launcher makes it at Play right after the
//! cook, `kiln boot-snapshot --game <dir>` by hand, `tools/deploy-server.sh` for a lab.
//!
//! Nothing here stops a start: a missing Python, a tool this build does not carry, a refusal of the tool, a run past
//! its limit all come back as a [`BootSnapshot`] that says so (status and reason). The game then starts without a
//! snapshot and kiln.boot reports `mods.boot.game_data` unavailable with its reason.
use std::collections::BTreeMap;
use std::ffi::OsString;
use std::io::{BufRead, BufReader, Read};
use std::path::Path;
use std::process::Stdio;
use std::time::{Duration, Instant};

use serde::{Deserialize, Serialize};

use crate::cooktools::CookTools;

/// The tool, under the tools' root.
pub const SCRIPT: &str = "tools/bootsnap/bootsnap.py";

/// The launcher's limit: a first run takes about a minute; one that takes this long is stopped and the start goes on.
pub const LAUNCHER_LIMIT: Duration = Duration::from_secs(10 * 60);

/// How big a snapshot is on disk, for the lines a player reads (a real server archive of game build 1287369: 199 MB).
pub const SIZE_TEXT: &str = "about 200 MB";

/// What a run of the producer came to. `status`: `made` (written now), `reused` (the snapshot of this game build was
/// there), `refused` (the tool said why it did not make one), `failed` (it did not end well, or said nothing usable),
/// `unavailable` (it could not run: no Python, no zstd, or this build carries no boot snapshot tool).
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct BootSnapshot {
    pub status: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub key: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub path: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub counts: Option<BTreeMap<String, u64>>,
    /// Kinds the game's archive could not give: written empty, so every OnBoot read of them answers "not found".
    #[serde(default)]
    pub empty_kinds: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub skipped: Option<u64>,
    /// Which files were read: `backup` (the pair another loader kept aside), `backup header`, `plain`, ...
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub archive: Option<String>,
    /// Its size on disk (a made one).
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub bytes: Option<u64>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub seconds: Option<f64>,
    /// Why there is no snapshot (refused, failed, unavailable), as a sentence.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub reason: Option<String>,
}

impl BootSnapshot {
    fn not(status: &str, reason: impl Into<String>) -> BootSnapshot {
        BootSnapshot { status: status.into(), reason: Some(reason.into()), ..Default::default() }
    }

    /// A snapshot of this game build is in place (it may still lack a kind: [`BootSnapshot::empty_kinds`]).
    pub fn ready(&self) -> bool {
        self.status == "made" || self.status == "reused"
    }

    /// The one line a player or an operator should read, when something is not right: no snapshot (OnBoot reads of the
    /// game's data are unavailable on this start), or a snapshot without some kinds. None when all is well.
    pub fn warning(&self) -> Option<String> {
        if !self.ready() {
            let why = self.reason.as_deref().unwrap_or("no reason given");
            return Some(format!("The game data for OnBoot mods was not made ({why}). The game starts without it: a mod that reads the game's data in OnBoot is told it is unavailable."));
        }
        if !self.empty_kinds.is_empty() {
            return Some(format!(
                "The game data for OnBoot mods has no {} (the game's archive could not give them): an OnBoot read of them finds nothing.",
                self.empty_kinds.join(", ")
            ));
        }
        None
    }

    /// What a finished run says on its last line.
    pub fn summary(&self) -> String {
        match self.status.as_str() {
            "reused" => "The game data for OnBoot mods is current".into(),
            "made" => format!(
                "The game data for OnBoot mods is made ({}{})",
                self.bytes.map(|b| format!("{} MB", (b + 500_000) / 1_000_000)).unwrap_or_else(|| SIZE_TEXT.into()),
                self.seconds.map(|s| format!(", {s:.0} s")).unwrap_or_default()
            ),
            _ => self.warning().unwrap_or_default(),
        }
    }
}

/// How a run of the tool ended.
#[derive(Debug, Clone, Default, PartialEq)]
pub struct Run {
    /// Its exit code (None: stopped by Kiln, or by a signal).
    pub code: Option<i32>,
    /// Kiln stopped it: why.
    pub stopped: Option<String>,
    /// Everything it printed on stdout (the result is its last JSON line).
    pub stdout: String,
    /// Its last lines on stderr, for a failure's reason.
    pub last_err: Vec<String>,
}

/// Runs the tool. [`ToolRunner`] runs the real one; tests give a fake.
pub trait Runner {
    /// `bootsnap.py <args>`: each line it prints on stderr goes to `line` as it comes; stopped after `limit` or when
    /// `stop()` says so.
    fn run(&self, args: &[OsString], limit: Option<Duration>, line: &mut dyn FnMut(&str), stop: &dyn Fn() -> bool) -> std::io::Result<Run>;
}

/// The real tool: `python -I <root>/tools/bootsnap/bootsnap.py`, with the cook tools' Python and environment
/// ([`CookTools::script_command`]: the host's environment, no console window). The tool starts no process of its own,
/// so stopping it is stopping that one process.
pub struct ToolRunner<'a> {
    pub tools: &'a CookTools,
}

impl Runner for ToolRunner<'_> {
    fn run(&self, args: &[OsString], limit: Option<Duration>, line: &mut dyn FnMut(&str), stop: &dyn Fn() -> bool) -> std::io::Result<Run> {
        let mut cmd = self.tools.script_command(SCRIPT);
        cmd.args(args).stdin(Stdio::null()).stdout(Stdio::piped()).stderr(Stdio::piped());
        let mut child = cmd.spawn()?;
        let mut out = child.stdout.take().expect("piped stdout");
        let err = child.stderr.take().expect("piped stderr");
        let reader = std::thread::spawn(move || {
            let mut s = String::new();
            let _ = out.read_to_string(&mut s);
            s
        });
        let (tx, rx) = std::sync::mpsc::channel::<String>();
        let lines = std::thread::spawn(move || {
            for l in BufReader::new(err).lines() {
                let Ok(l) = l else { break };
                if tx.send(l).is_err() {
                    break;
                }
            }
        });
        let start = Instant::now();
        let mut last_err: Vec<String> = Vec::new();
        let mut stopped: Option<String> = None;
        let take = |l: String, last_err: &mut Vec<String>, line: &mut dyn FnMut(&str)| {
            line(&l);
            last_err.push(l);
            if last_err.len() > 20 {
                last_err.remove(0);
            }
        };
        let status = loop {
            match rx.recv_timeout(Duration::from_millis(100)) {
                Ok(l) => take(l, &mut last_err, line),
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) | Err(std::sync::mpsc::RecvTimeoutError::Disconnected) => {}
            }
            if let Some(s) = child.try_wait()? {
                break Some(s);
            }
            if stop() {
                stopped = Some("stopped: the start was canceled".into());
            } else if let Some(l) = limit.filter(|l| start.elapsed() >= *l) {
                stopped = Some(format!("stopped after {} minutes: making it may take that long at most", l.as_secs() / 60));
            }
            if stopped.is_some() {
                let _ = child.kill();
                let _ = child.wait();
                break None;
            }
        };
        let _ = lines.join();
        for l in rx.try_iter() {
            take(l, &mut last_err, line);
        }
        let stdout = reader.join().unwrap_or_default();
        Ok(Run { code: status.and_then(|s| s.code()), stopped, stdout, last_err })
    }
}

/// Makes the snapshot of the game in `game` (a dedicated server's folder or the client's), or finds that the one of
/// this game build is there. `force`: make it again all the same. `progress` gets the tool's progress lines (without
/// its "boot-snapshot: " prefix). Never an error: what went wrong is in the result.
pub fn make(runner: &dyn Runner, game: &Path, force: bool, limit: Option<Duration>, progress: &mut dyn FnMut(&str),
            stop: &dyn Fn() -> bool) -> BootSnapshot {
    let mut args: Vec<OsString> = vec!["make".into(), "--game".into(), game.as_os_str().to_owned(), "--json".into()];
    if force {
        args.push("--force".into());
    }
    let run = match runner.run(&args, limit, &mut |l| progress(l.strip_prefix("boot-snapshot: ").unwrap_or(l)), stop) {
        Ok(r) => r,
        Err(e) => return BootSnapshot::not("unavailable", format!("the boot snapshot tool could not be started: {e}")),
    };
    if let Some(why) = run.stopped {
        return BootSnapshot::not("failed", why);
    }
    let result = run.stdout.lines().rev().find(|l| l.trim_start().starts_with('{'))
        .and_then(|l| serde_json::from_str::<BootSnapshot>(l.trim()).ok());
    match result {
        Some(r) if run.code == Some(0) && r.ready() => r,
        Some(r) if r.status == "refused" => BootSnapshot { reason: r.reason.or_else(|| Some("no reason given".into())), ..r },
        _ => {
            let said = run.last_err.iter().rev().find(|l| !l.trim().is_empty()).cloned().unwrap_or_else(|| "it said nothing".into());
            BootSnapshot::not("failed", match run.code {
                Some(c) => format!("the boot snapshot tool ended with code {c}: {said}"),
                None => format!("the boot snapshot tool was ended: {said}"),
            })
        }
    }
}

/// [`make`] with the tools as the launcher and `kiln` locate them ([`crate::cooktools::locate`]): `tools` is that
/// result. No Python, no zstd, or a copy of the tools without the boot snapshot tool give status `unavailable`.
pub fn make_with(tools: std::result::Result<CookTools, String>, game: &Path, force: bool, limit: Option<Duration>,
                 progress: &mut dyn FnMut(&str), stop: &dyn Fn() -> bool) -> BootSnapshot {
    let tools = match tools {
        Ok(t) => t,
        Err(why) => return BootSnapshot::not("unavailable", why),
    };
    if !tools.root.join(SCRIPT).is_file() {
        return BootSnapshot::not("unavailable", "this build carries no boot snapshot tool (tools/bootsnap): set KILN_REPO to a Kiln checkout");
    }
    make(&ToolRunner { tools: &tools }, game, force, limit, progress, stop)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use std::path::PathBuf;

    struct Fake {
        out: Run,
        lines: Vec<&'static str>,
        args: RefCell<Vec<OsString>>,
        limit: RefCell<Option<Duration>>,
    }

    impl Fake {
        fn new(code: Option<i32>, stdout: &str, lines: Vec<&'static str>) -> Fake {
            Fake { out: Run { code, stopped: None, stdout: stdout.into(), last_err: lines.iter().map(|l| l.to_string()).collect() },
                   lines, args: RefCell::new(Vec::new()), limit: RefCell::new(None) }
        }
    }

    impl Runner for Fake {
        fn run(&self, args: &[OsString], limit: Option<Duration>, line: &mut dyn FnMut(&str), _stop: &dyn Fn() -> bool) -> std::io::Result<Run> {
            *self.args.borrow_mut() = args.to_vec();
            *self.limit.borrow_mut() = limit;
            for l in &self.lines {
                line(l);
            }
            Ok(self.out.clone())
        }
    }

    fn game() -> PathBuf {
        PathBuf::from("/games/EnshroudedServer")
    }

    #[test]
    fn a_made_snapshot_with_its_progress_and_arguments() {
        let f = Fake::new(Some(0), "{\"status\": \"made\", \"key\": \"a180f075537947c3874902dc3f6031c3\", \"path\": \"/g/kiln/data/boot-gamedata/a180\", \"counts\": {\"items\": 4268, \"templates\": 12229, \"skills\": 243, \"materials\": 201}, \"skipped\": 40, \"emptyKinds\": [], \"archive\": \"backup\", \"bytes\": 208420000, \"seconds\": 43.9}\n",
                          vec!["boot-snapshot: checking the game data snapshot", "boot-snapshot: items 500/4268"]);
        let mut seen = Vec::new();
        let r = make(&f, &game(), false, Some(LAUNCHER_LIMIT), &mut |l| seen.push(l.to_string()), &|| false);
        assert_eq!(r.status, "made");
        assert!(r.ready());
        assert_eq!(r.counts.as_ref().unwrap()["items"], 4268);
        assert_eq!(r.archive.as_deref(), Some("backup"));
        assert_eq!(r.warning(), None);
        assert_eq!(r.summary(), "The game data for OnBoot mods is made (208 MB, 44 s)");
        assert_eq!(seen, ["checking the game data snapshot", "items 500/4268"]);
        let args: Vec<String> = f.args.borrow().iter().map(|a| a.to_string_lossy().into_owned()).collect();
        assert_eq!(args, ["make", "--game", "/games/EnshroudedServer", "--json"]);
        assert_eq!(*f.limit.borrow(), Some(LAUNCHER_LIMIT));
        make(&f, &game(), true, None, &mut |_| {}, &|| false);
        assert_eq!(f.args.borrow().last().unwrap(), "--force");
    }

    #[test]
    fn a_reused_snapshot_with_an_empty_kind_warns_but_is_ready() {
        let f = Fake::new(Some(0), "{\"status\": \"reused\", \"key\": \"k\", \"path\": \"/p\", \"emptyKinds\": [\"skills\"], \"seconds\": 0.015}", vec![]);
        let r = make(&f, &game(), false, None, &mut |_| {}, &|| false);
        assert!(r.ready());
        assert_eq!(r.empty_kinds, ["skills"]);
        assert!(r.warning().unwrap().contains("has no skills"), "{:?}", r.warning());
        assert_eq!(r.summary(), "The game data for OnBoot mods is current");
    }

    #[test]
    fn a_refusal_keeps_the_tools_reason() {
        let f = Fake::new(Some(1), "{\"status\": \"refused\", \"reason\": \"no enshrouded.kfc beside enshrouded.exe\"}", vec!["boot-snapshot: no enshrouded.kfc beside enshrouded.exe"]);
        let r = make(&f, &game(), false, None, &mut |_| {}, &|| false);
        assert_eq!((r.status.as_str(), r.reason.as_deref()), ("refused", Some("no enshrouded.kfc beside enshrouded.exe")));
        assert!(!r.ready());
        assert!(r.warning().unwrap().starts_with("The game data for OnBoot mods was not made (no enshrouded.kfc"), "{:?}", r.warning());
    }

    #[test]
    fn a_crash_or_a_stop_is_a_failure_with_the_last_line() {
        let f = Fake::new(Some(1), "", vec!["Traceback (most recent call last):", "zstd: the data does not decompress", ""]);
        let r = make(&f, &game(), false, None, &mut |_| {}, &|| false);
        assert_eq!(r.status, "failed");
        assert_eq!(r.reason.as_deref(), Some("the boot snapshot tool ended with code 1: zstd: the data does not decompress"));
        let mut f = Fake::new(None, "", vec![]);
        f.out.stopped = Some("stopped after 10 minutes: making it may take that long at most".into());
        let r = make(&f, &game(), false, Some(LAUNCHER_LIMIT), &mut |_| {}, &|| false);
        assert_eq!((r.status.as_str(), r.reason.as_deref()), ("failed", Some("stopped after 10 minutes: making it may take that long at most")));
        // exit 0 but no result line: not a snapshot
        let r = make(&Fake::new(Some(0), "hello\n", vec![]), &game(), false, None, &mut |_| {}, &|| false);
        assert_eq!(r.status, "failed");
    }

    #[test]
    fn no_python_or_no_tool_is_unavailable() {
        let r = make_with(Err("the cook tools need Python 3.10 or newer and no python3 was found".into()), &game(), false, None, &mut |_| {}, &|| false);
        assert_eq!(r.status, "unavailable");
        assert!(r.warning().unwrap().contains("no python3 was found"));
        let root = std::env::temp_dir().join(format!("kiln-bootsnap-none-{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        let tools = CookTools { root: root.clone(), source: crate::cooktools::Source::Shipped,
                                python: crate::cooktools::Python { program: PathBuf::from("python3"), version: (3, 14), zstd: "stdlib".into(), given: false },
                                sound_helper: None };
        let r = make_with(Ok(tools), &game(), false, None, &mut |_| {}, &|| false);
        assert_eq!(r.status, "unavailable");
        assert!(r.reason.unwrap().starts_with("this build carries no boot snapshot tool"));
        let _ = std::fs::remove_dir_all(&root);
    }

    /// The real runner, on a stand-in script (no game: tools/bootsnap's own tests cover the tool): it streams the
    /// stderr lines, reads the result line, and stops a run past its limit.
    #[test]
    fn the_tool_runner_streams_reads_and_stops() {
        let Some(python) = crate::util::which("python3").or_else(|| crate::util::which("python")) else {
            eprintln!("no python on PATH: skipped");
            return;
        };
        let root = std::env::temp_dir().join(format!("kiln-bootsnap-runner-{}", std::process::id()));
        let script = root.join(SCRIPT);
        std::fs::create_dir_all(script.parent().unwrap()).unwrap();
        std::fs::write(&script, "import sys, time\nif '--slow' in sys.argv:\n    time.sleep(30)\nprint('boot-snapshot: items 1/2', file=sys.stderr, flush=True)\nprint('boot-snapshot: items 2/2', file=sys.stderr, flush=True)\nprint('{\"status\": \"made\", \"key\": \"k\", \"emptyKinds\": [], \"args\": ' + repr(len(sys.argv)) + '}')\n").unwrap();
        let tools = CookTools { root: root.clone(), source: crate::cooktools::Source::Checkout,
                                python: crate::cooktools::Python { program: python, version: (3, 14), zstd: "stdlib".into(), given: false },
                                sound_helper: None };
        let mut seen = Vec::new();
        let r = make_with(Ok(tools.clone()), &game(), false, Some(Duration::from_secs(60)), &mut |l| seen.push(l.to_string()), &|| false);
        assert_eq!((r.status.as_str(), r.key.as_deref()), ("made", Some("k")), "{r:?}");
        assert_eq!(seen, ["items 1/2", "items 2/2"]);
        let t = Instant::now();
        let run = ToolRunner { tools: &tools }.run(&["--slow".into()], Some(Duration::from_secs(1)), &mut |_| {}, &|| false).unwrap();
        assert!(run.stopped.is_some() && run.code.is_none(), "{run:?}");
        assert!(t.elapsed() < Duration::from_secs(20), "stopped at its limit");
        let run = ToolRunner { tools: &tools }.run(&["--slow".into()], None, &mut |_| {}, &|| true).unwrap();
        assert_eq!(run.stopped.as_deref(), Some("stopped: the start was canceled"));
        let _ = std::fs::remove_dir_all(&root);
    }
}
