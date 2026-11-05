// SPDX-License-Identifier: MIT
//! Which server a command talks to, and `kiln run`: start/stop a dev lab (tools/lab/lab.sh, Linux) or a
//! launcher/kilnctl server instance (kiln_manage::server).

use crate::out::{self, CliError};
use crate::project::Project;
use crate::tools;
use anyhow::Result;
use kiln_manage::server::instance::ServerInstance;
use serde_json::json;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

pub struct Server {
    pub dir: PathBuf,
    /// how it was found: "path", "lab", "instance", "env", "project"
    pub via: &'static str,
    pub lab: Option<String>,
    pub instance: Option<ServerInstance>,
}

fn labs_dir() -> PathBuf {
    std::env::var_os("KILN_LABS").map(PathBuf::from).unwrap_or_else(|| kiln_manage::util::home().join("srv/enshrouded/labs"))
}

/// A lab is `<labs>/<name>/` with its Wine prefix `<labs>/<name>.pfx` (tools/lab/lab.sh).
fn lab_of(dir: &Path) -> Option<String> {
    let dir = dir.canonicalize().ok()?;
    let labs = labs_dir().canonicalize().ok()?;
    if dir.parent()? != labs {
        return None;
    }
    let name = dir.file_name()?.to_string_lossy().to_string();
    labs.join(format!("{name}.pfx")).is_dir().then_some(name)
}

fn instance_for_dir(dir: &Path) -> Option<ServerInstance> {
    let c = dir.canonicalize().ok()?;
    kiln_manage::server::instance::list()
        .ok()?
        .into_iter()
        .find(|s| s.dir.as_deref().and_then(|d| d.canonicalize().ok()).as_deref() == Some(c.as_path()))
}

fn from_dir(dir: PathBuf, via: &'static str) -> Server {
    let lab = lab_of(&dir);
    let instance = if lab.is_none() { instance_for_dir(&dir) } else { None };
    Server { dir, via, lab, instance }
}

/// --server (a folder, a lab name or an instance id), else $KILN_SERVER, else the project's KilnServer.
pub fn resolve(arg: Option<&str>, project: Option<&Project>) -> Result<Server> {
    if let Some(a) = arg.filter(|a| !a.is_empty()) {
        return resolve_one(a, "path");
    }
    if let Some(e) = std::env::var("KILN_SERVER").ok().filter(|v| !v.is_empty()) {
        return resolve_one(&e, "env");
    }
    if let Some(p) = project {
        if p.kind == crate::project::Kind::Dotnet {
            let s = p.property("KilnServer").unwrap_or_default();
            if !s.is_empty() {
                let d = if Path::new(&s).is_relative() { p.dir.join(&s) } else { PathBuf::from(&s) };
                return Ok(from_dir(d, "project"));
            }
        }
    }
    Err(CliError::usage("which server? --server <folder|lab|instance>, KILN_SERVER, or KilnServer in the project").into())
}

fn resolve_one(a: &str, via: &'static str) -> Result<Server> {
    let p = PathBuf::from(kiln_manage::util::expand(a, &kiln_manage::util::home()));
    if p.is_dir() {
        return Ok(from_dir(std::path::absolute(&p).unwrap_or(p), via));
    }
    let lab = labs_dir().join(a);
    if !a.contains('/') && lab.is_dir() && labs_dir().join(format!("{a}.pfx")).is_dir() {
        return Ok(Server { dir: lab, via: "lab", lab: Some(a.to_string()), instance: None });
    }
    if let Ok(i) = kiln_manage::server::instance::get(a) {
        if let Some(d) = i.dir.clone() {
            return Ok(Server { dir: d, via: "instance", lab: None, instance: Some(i) });
        }
        return Err(CliError::new("unsupported", format!("server '{a}' is remote; kiln talks to local servers")).into());
    }
    Err(CliError::new("not_found", format!("no server '{a}' (not a folder, a lab in {}, or a server instance)", labs_dir().display())).into())
}

#[derive(clap::Args)]
pub struct RunArgs {
    /// Server folder, lab name or server instance id (default: $KILN_SERVER, else the current project's KilnServer).
    #[arg(long)]
    pub server: Option<String>,
    /// Stop it instead (SIGINT: the game saves; Kiln unloads the mods first).
    #[arg(long, conflicts_with_all = ["restart", "status"])]
    pub stop: bool,
    #[arg(long, conflicts_with = "status")]
    pub restart: bool,
    /// Only report whether it runs.
    #[arg(long)]
    pub status: bool,
    /// After starting, wait up to this many seconds for Kiln's mods to load and print their load lines (0: don't wait).
    #[arg(long, default_value_t = 180)]
    pub wait: u64,
}

pub fn run(a: RunArgs, json: bool) -> Result<i32> {
    let project = Project::find(None).ok();
    let s = resolve(a.server.as_deref(), project.as_ref())?;
    let log = s.dir.join("kiln/logs/kiln.log");
    let before = std::fs::metadata(&log).map(|m| m.len()).unwrap_or(0);
    let state = |s: &Server| -> Result<bool> { running(s) };
    if a.status {
        let up = state(&s)?;
        if json {
            out::print_json(&json!({"ok": true, "server": s.dir, "via": s.via, "lab": s.lab, "running": up}));
        } else {
            println!("{}: {}", s.dir.display(), if up { "running" } else { "stopped" });
        }
        return Ok(0);
    }
    if a.stop || a.restart {
        stop(&s, json)?;
        if a.stop {
            if json {
                out::print_json(&json!({"ok": true, "server": s.dir, "running": false}));
            }
            return Ok(0);
        }
    }
    if state(&s)? {
        out::say(json, format!("{} is already running", s.dir.display()));
    } else {
        start(&s, json)?;
    }
    let mut lines = Vec::new();
    let mut loaded = None;
    if a.wait > 0 {
        let (l, ok) = wait_for_mods(&log, before, Duration::from_secs(a.wait), json)?;
        lines = l;
        loaded = Some(ok);
    }
    if json {
        out::print_json(&json!({"ok": loaded != Some(false), "server": s.dir, "via": s.via, "lab": s.lab, "running": true,
                                "modsLoaded": loaded, "lines": lines}));
    }
    Ok(if loaded == Some(false) { 1 } else { 0 })
}

pub fn running(s: &Server) -> Result<bool> {
    if let Some(l) = &s.lab {
        let repo = tools::need_repo("labs")?;
        let o = std::process::Command::new(repo.join("tools/lab/lab.sh")).arg("pid").arg(l).output()?;
        return Ok(!String::from_utf8_lossy(&o.stdout).trim().is_empty());
    }
    if let Some(i) = &s.instance {
        return Ok(kiln_manage::server::run::pid(i).is_some());
    }
    Err(CliError::new("unsupported", format!("{} is neither a lab nor a known server instance (kiln run starts those)", s.dir.display())).into())
}

fn start(s: &Server, json: bool) -> Result<()> {
    if let Some(l) = &s.lab {
        let repo = tools::need_repo("labs")?;
        let mut c = std::process::Command::new(repo.join("tools/lab/lab.sh"));
        c.arg("start").arg(l);
        if !tools::run_passthrough(&mut c, json)?.success() {
            return Err(CliError::new("start_failed", format!("lab.sh start {l} failed")).into());
        }
        return Ok(());
    }
    if let Some(i) = &s.instance {
        out::say(json, format!("starting server instance {} ({})", i.id, s.dir.display()));
        kiln_manage::server::run::start(i, &mut Say(json))?;
        return Ok(());
    }
    Err(CliError::new("unsupported", format!("{} is neither a lab nor a known server instance", s.dir.display())).into())
}

pub fn stop(s: &Server, json: bool) -> Result<()> {
    if let Some(l) = &s.lab {
        let repo = tools::need_repo("labs")?;
        let mut c = std::process::Command::new(repo.join("tools/lab/lab.sh"));
        c.arg("stop").arg(l);
        tools::run_passthrough(&mut c, json)?;
        return Ok(());
    }
    if let Some(i) = &s.instance {
        kiln_manage::server::run::stop(i, &mut Say(json))?;
        return Ok(());
    }
    Err(CliError::new("unsupported", format!("{} is neither a lab nor a known server instance", s.dir.display())).into())
}

struct Say(bool);
impl kiln_manage::task::Progress for Say {
    fn report(&mut self, _fraction: Option<f32>, message: &str) {
        if !message.is_empty() {
            out::say(self.0, message);
        }
    }
}

/// Follows kiln.log from `from` until the loader's summary (`mods: N loaded, ...`) and the world start, or timeout.
fn wait_for_mods(log: &Path, from: u64, timeout: Duration, json: bool) -> Result<(Vec<String>, bool)> {
    let mut f = kiln_manage::logs::Follower::open_at(log, from)?;
    if std::fs::metadata(log).map(|m| m.len() < from).unwrap_or(true) {
        f = kiln_manage::logs::Follower::open_at(log, 0)?;
    }
    let start = Instant::now();
    let mut keep = Vec::new();
    let (mut summary, mut started) = (false, false);
    while start.elapsed() < timeout {
        for line in f.poll()? {
            let Some(l) = kiln_manage::logs::parse_line(&line) else { continue };
            let interesting = (l.source == "host" && (l.message.starts_with("mod ") || l.message.starts_with("mods: ")))
                || l.message.contains("ServerStarted")
                || l.message.starts_with("lifecycle: ")
                || l.source.starts_with("native")
                || (l.level == 'E');
            if interesting {
                out::say(json, &line);
                keep.push(line.clone());
            }
            summary |= l.source == "host" && l.message.starts_with("mods: ");
            started |= l.message.contains("server started") || l.message.contains("ServerStarted");
        }
        if summary && started {
            return Ok((keep, true));
        }
        std::thread::sleep(Duration::from_millis(250));
    }
    out::say(json, format!("kiln: no mod load summary in {} within {} s", log.display(), timeout.as_secs()));
    Ok((keep, summary))
}
