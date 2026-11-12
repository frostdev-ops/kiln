// SPDX-License-Identifier: MIT
//! The commands besides `run` and `preflight`.
use crate::config::{self, InitSpec, Instance, Policy};
use crate::preflight::{self, Level};
use crate::run::wineserver_wait;
use crate::server;
#[cfg(unix)]
use crate::server::LogWatch;
use crate::util::{self, fmt_utc, human_duration, human_size};
use crate::wine::{self, WineSpec};
use crate::{gate, kiln, procs, sigport, steam};
use anyhow::{Context, Result, bail};
use std::io::{BufRead, BufReader, Write};
#[cfg(unix)]
use std::os::unix::process::CommandExt;
use std::path::{Path, PathBuf};
#[cfg(unix)]
use std::process::Child;
use std::process::{Command, Stdio};
use std::time::{Duration, Instant, SystemTime};

pub struct InitArgs {
    pub name: String,
    pub dir: PathBuf,
    pub port: u16,
    pub wine: String,
    pub prefix: Option<PathBuf>,
    pub backup_dir: Option<PathBuf>,
    pub policy: Policy,
    pub samples: bool,
    pub kiln_source: Option<PathBuf>,
    pub kiln_build: Option<PathBuf>,
    pub force: bool,
}

fn absolute(p: &Path) -> Result<PathBuf> {
    let p = util::expand(&p.to_string_lossy(), &util::home());
    let p = if p.is_absolute() { p } else { std::env::current_dir()?.join(p) };
    Ok(util::clean(&p))
}

pub fn init(a: InitArgs) -> Result<()> {
    config::validate_name(&a.name)?;
    let file = config::instance_file(&a.name);
    if file.exists() && !a.force {
        bail!("{} exists (--force overwrites it)", file.display());
    }
    let home = util::home();
    let dir = absolute(&a.dir)?;
    let prefix = match &a.prefix {
        Some(p) => absolute(p)?,
        None => util::with_suffix(&dir, ".pfx"),
    };
    let backup_dir = match &a.backup_dir {
        Some(p) => absolute(p)?,
        None => util::with_suffix(&dir, ".backups"),
    };
    let source = match &a.kiln_source {
        Some(p) => absolute(p)?,
        None => config::compiled_source(),
    };
    let build = match &a.kiln_build {
        Some(p) => absolute(p)?,
        None => source.join("native/build"),
    };
    // `--wine ge` pins the newest GE-Proton installed now, so a later GE-Proton install does not swap the Wine
    // under an existing prefix.
    let mut spec = WineSpec::parse(&a.wine, &home)?;
    if spec == WineSpec::GeLatest {
        let (name, _) = wine::ge_installs(&home)
            .into_iter()
            .next()
            .context("no GE-Proton installed (see docs/kiln/kilnctl.md), or pass --wine system / a wine path")?;
        spec = WineSpec::Ge(name);
    }
    let runner = wine::resolve(&spec, &home)?;
    let text = config::render(&InitSpec {
        name: &a.name,
        dir: &dir,
        prefix: &prefix,
        wine: &spec,
        port: a.port,
        backup_dir: &backup_dir,
        source: &source,
        build: &build,
        policy: a.policy,
        samples: a.samples,
    });
    let inst = config::parse(&a.name, &file, &text, &home)?; // same validation as every later load
    std::fs::create_dir_all(config::instances_dir())?;
    std::fs::write(&file, text).with_context(|| format!("writing {}", file.display()))?;
    println!("instance {} -> {}", inst.name, file.display());
    println!("  dir     {}", inst.dir.display());
    println!("  prefix  {}", inst.prefix.display());
    println!("  wine    {} ({})", spec.to_config(), runner.wine.display());
    println!("  port    UDP {}", inst.query_port);
    println!("  kiln    {} (build {}, policy {})", inst.kiln.source.display(), inst.kiln.build.display(), inst.kiln.policy.as_str());
    println!("next: kilnctl install {0} && kilnctl prefix {0} && kilnctl deploy {0} && kilnctl start {0}", inst.name);
    Ok(())
}

fn refuse_running(inst: &Instance, what: &str) -> Result<()> {
    if let Some(p) = procs::find_server(&inst.prefix) {
        bail!("{} is running (pid {p}); kilnctl stop {} before {what}", inst.name, inst.name);
    }
    if let Some(p) = procs::servers_in_dir(&inst.dir).first() {
        bail!("a server runs in {} with another prefix (pid {p}); stop it before {what}", inst.dir.display());
    }
    Ok(())
}

/// Writes enshrouded_server.json when missing (never edits an existing one).
fn ensure_server_json(inst: &Instance) -> Result<()> {
    let path = inst.server_json();
    if path.exists() {
        let c = server::read_config(&path)?;
        if c.query_port != inst.query_port {
            println!("warning: {} has queryPort {}, the instance says {}; preflight fails until they match", path.display(), c.query_port, inst.query_port);
        }
        return Ok(());
    }
    std::fs::write(&path, server::default_config(&format!("Kiln {}", inst.name), inst.query_port)?)?;
    println!("wrote {} (UDP {}, user groups with random passwords: edit the file to set your own)", path.display(), inst.query_port);
    Ok(())
}

pub fn install(inst: &Instance, reflink_from: Option<&Path>) -> Result<()> {
    refuse_running(inst, "installing")?;
    if let Some(src) = reflink_from {
        let src = src.canonicalize().with_context(|| format!("{}", src.display()))?;
        if !src.join(server::EXE).exists() {
            bail!("{} has no {}", src.display(), server::EXE);
        }
        if inst.dir.join(server::EXE).exists() {
            bail!("{} is already installed; run kilnctl install {} without --reflink-from to validate it", inst.dir.display(), inst.name);
        }
        let dir = inst.dir.canonicalize().unwrap_or_else(|_| inst.dir.clone());
        if dir.starts_with(&src) || src.starts_with(&dir) {
            bail!("source and target overlap");
        }
        std::fs::create_dir_all(&inst.dir)?;
        let mut entries: Vec<PathBuf> = std::fs::read_dir(&src)?
            .flatten()
            .filter(|e| !kiln::INSTANCE_FILES.contains(&e.file_name().to_string_lossy().as_ref()))
            .map(|e| e.path())
            .collect();
        entries.sort();
        println!("reflinking {} entries from {} (skipping {})", entries.len(), src.display(), kiln::INSTANCE_FILES.join(", "));
        let t0 = Instant::now();
        util::run_status(Command::new("cp").args(["-a", "--reflink=always"]).args(&entries).arg(&inst.dir), "cp --reflink=always")
            .context("reflink copy failed (source and target must be on the same btrfs/XFS filesystem)")?;
        println!("reflinked in {:.1} s", t0.elapsed().as_secs_f64());
    }
    std::fs::create_dir_all(&inst.dir)?;
    let t0 = Instant::now();
    steam::app_update(&inst.dir)?;
    if !inst.exe().exists() {
        bail!("steamcmd reported success but {} is missing", inst.exe().display());
    }
    ensure_server_json(inst)?;
    println!(
        "installed {} in {} (build {}, {})",
        inst.name,
        inst.dir.display(),
        steam::buildid(&inst.dir).unwrap_or_else(|| "?".into()),
        human_duration(t0.elapsed())
    );
    Ok(())
}

pub fn prefix(inst: &Instance) -> Result<()> {
    refuse_running(inst, "touching the prefix")?;
    let runner = wine::resolve(&inst.wine, &util::home())?;
    if let Some(parent) = inst.prefix.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let fresh = !inst.prefix.join("system.reg").exists();
    println!("{} prefix {} with {}", if fresh { "creating" } else { "updating" }, inst.prefix.display(), runner.label);
    let t0 = Instant::now();
    util::run_status(runner.command(inst).args(["wineboot", "-i"]).stdin(Stdio::null()), "wineboot -i")?;
    util::run_status(
        runner.command(inst).args(["reg", "add", "HKCU\\Software\\Wine\\Drivers", "/v", "Graphics", "/d", "null", "/f"]).stdin(Stdio::null()),
        "wine reg add (Graphics=null)",
    )?;
    wineserver_wait(&runner, inst, Duration::from_secs(60))?;
    match preflight::prefix_check(&inst.prefix) {
        Ok(m) => println!("prefix ok: {m} ({:.1} s)", t0.elapsed().as_secs_f64()),
        Err(e) => bail!("prefix check failed: {e}"),
    }
    Ok(())
}

pub fn deploy(inst: &Instance, from: Option<&Path>, samples: bool, build: bool) -> Result<()> {
    refuse_running(inst, "deploying")?;
    if !inst.exe().exists() {
        bail!("{} missing (kilnctl install {})", inst.exe().display(), inst.name);
    }
    kiln::deploy(inst, from, samples, build)
}

/// `kilnctl deploy --bundle`: the runtime bundle's server components through kiln_manage::install (a first deploy: the
/// defaults; an update: the installed ones plus every new or newly default component not turned off, see
/// install::resolve_components; `samples` adds samples), then the instance's policy in kiln/kiln.json (kept when the
/// file exists, like deploy).
pub fn deploy_bundle(inst: &Instance, zip: &Path, samples: bool) -> Result<()> {
    refuse_running(inst, "deploying")?;
    if !inst.exe().exists() {
        bail!("{} missing (kilnctl install {})", inst.exe().display(), inst.name);
    }
    let mut g = kiln_manage::game::inspect(&inst.dir).map_err(|e| anyhow::anyhow!(e.message))?;
    g.kind = kiln_manage::game::GameKind::Server;
    g.platform = kiln_manage::game::Platform::Wine;
    g.prefix = Some(inst.prefix.clone());
    g.source = "kilnctl".into();
    if samples || inst.kiln.samples {
        let dirs = crate::bundle::data_dirs()?;
        let options = kiln_manage::install::InstallOptions {
            installer: Some(format!("kilnctl {}", env!("CARGO_PKG_VERSION"))), ..Default::default()
        };
        let r = kiln_manage::install::install_adding(&dirs, &g, zip, &options, &["samples".into()], &mut crate::bundle::Printer::new())
            .map_err(|e| anyhow::anyhow!(e.message))?;
        crate::bundle::print_receipt(&r, &g.path);
    } else {
        crate::bundle::install_into(&g, zip, None, false)?;
    }
    // the bundle's kiln.json is installed only when missing; the instance's policy goes in either way (in place)
    let kiln_json = kiln::kiln_dir(inst).join("kiln.json");
    if let Some(new) = kiln::set_policy(&std::fs::read_to_string(&kiln_json)?, inst.kiln.policy.as_str())? {
        std::fs::write(&kiln_json, new)?;
        println!("kiln/kiln.json: policy set to {}", inst.kiln.policy.as_str());
    }
    Ok(())
}

pub fn preflight_cmd(inst: &Instance) -> Result<i32> {
    let checks = preflight::run(inst);
    preflight::print(&checks);
    let warns = checks.iter().filter(|c| c.level == Level::Warn).count();
    if preflight::failed(&checks) {
        let fails = checks.iter().filter(|c| c.level == Level::Fail).count();
        println!("preflight {}: FAILED ({fails} failure(s), {warns} warning(s))", inst.name);
        return Ok(1);
    }
    println!("preflight {}: ok ({warns} warning(s))", inst.name);
    Ok(0)
}

/// `systemctl --user is-active <unit>`; None when systemctl or the user manager is unavailable.
pub fn unit_state(unit: &str) -> Option<String> {
    let out = Command::new("systemctl").args(["--user", "is-active", unit]).stderr(Stdio::null()).output().ok()?;
    let s = String::from_utf8_lossy(&out.stdout).trim().to_string();
    (!s.is_empty()).then_some(s)
}

fn unit_running(unit: &str) -> bool {
    matches!(unit_state(unit).as_deref(), Some("active" | "activating" | "reloading" | "deactivating"))
}

fn log_path(inst: &Instance) -> PathBuf {
    let cfg = server::read_config(&inst.server_json()).ok();
    let dir = cfg.map(|c| c.log_dir).unwrap_or_else(|| "./logs".into());
    server::host_path(&inst.dir, &inst.prefix, &dir).join(server::LOG)
}

fn save_dir(inst: &Instance) -> PathBuf {
    let cfg = server::read_config(&inst.server_json()).ok();
    let dir = cfg.map(|c| c.save_dir).unwrap_or_else(|| "./savegame".into());
    server::host_path(&inst.dir, &inst.prefix, &dir)
}

#[cfg(unix)]
fn tail_file(path: &Path, lines: usize) -> String {
    let text = server::read_log(path).unwrap_or_default();
    let v: Vec<&str> = text.lines().collect();
    v[v.len().saturating_sub(lines)..].join("\n")
}

#[cfg(unix)]
enum Launch<'a> {
    Detached(&'a mut Child),
    Systemd(&'a str),
}

#[cfg(unix)]
fn wait_ready(inst: &Instance, mut watch: LogWatch, timeout: Duration, mut launch: Launch) -> Result<()> {
    let t0 = Instant::now();
    loop {
        if watch.poll().is_some_and(|t| t.contains(server::READY)) {
            let pid = procs::find_server(&inst.prefix).map(|p| p.to_string()).unwrap_or_else(|| "?".into());
            println!("{} up after {:.0} s (pid {pid}, UDP {}): \"{}\" in {}", inst.name, t0.elapsed().as_secs_f64(),
                inst.query_port, server::READY, watch.path().display());
            return Ok(());
        }
        match &mut launch {
            Launch::Detached(child) => {
                if let Some(st) = child.try_wait()? {
                    bail!("{} exited during start ({st}); last lines of {}:\n{}", inst.name, inst.run_log().display(), tail_file(&inst.run_log(), 15));
                }
            }
            Launch::Systemd(unit) => {
                if t0.elapsed() > Duration::from_secs(3) && !unit_running(unit) {
                    bail!("{unit} is {} (journalctl --user -u {unit})", unit_state(unit).unwrap_or_else(|| "unknown".into()));
                }
            }
        }
        if t0.elapsed() >= timeout {
            bail!("no \"{}\" in {} after {} s; the server keeps running (kilnctl logs {})", server::READY,
                watch.path().display(), timeout.as_secs(), inst.name);
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

// Windows: kilnctl's instances are Wine servers on Linux (start/stop); a native Windows server runs in the launcher.
#[cfg(not(unix))]
pub fn start(_inst: &Instance, _systemd: bool, _timeout: Duration) -> Result<()> {
    bail!("{}", util::UNIX_ONLY)
}

#[cfg(not(unix))]
pub fn stop(_inst: &Instance, _timeout: Option<Duration>) -> Result<()> {
    bail!("{}", util::UNIX_ONLY)
}

#[cfg(unix)]
pub fn start(inst: &Instance, systemd: bool, timeout: Duration) -> Result<()> {
    if let Some(p) = procs::find_server(&inst.prefix) {
        println!("{} is already running (pid {p})", inst.name);
        return Ok(());
    }
    let unit = inst.unit();
    if systemd {
        let watch = LogWatch::new(log_path(inst));
        println!("systemctl --user start {unit}");
        util::run_status(Command::new("systemctl").args(["--user", "start", &unit]), "systemctl --user start")?;
        return wait_ready(inst, watch, timeout, Launch::Systemd(&unit));
    }
    let checks = preflight::run(inst);
    if preflight::failed(&checks) {
        preflight::print(&checks);
        bail!("preflight failed; not starting {}", inst.name);
    }
    let watch = LogWatch::new(log_path(inst));
    let run_log = inst.run_log();
    std::fs::create_dir_all(run_log.parent().context("run log folder")?)?;
    if run_log.metadata().is_ok_and(|m| m.len() > 8 << 20) {
        std::fs::rename(&run_log, util::with_suffix(&run_log, ".old"))?; // one generation is enough
    }
    let mut log = std::fs::OpenOptions::new().create(true).append(true).open(&run_log)?;
    writeln!(log, "---- kilnctl start {} at {}", inst.name, fmt_utc(SystemTime::now()))?;
    let mut cmd = Command::new(std::env::current_exe()?);
    cmd.args(["run", &inst.name]).stdin(Stdio::null()).stdout(log.try_clone()?).stderr(log);
    // SAFETY: setsid is async-signal-safe; the runner gets its own session, away from this terminal.
    unsafe {
        cmd.pre_exec(|| {
            if libc::setsid() < 0 {
                return Err(std::io::Error::last_os_error());
            }
            Ok(())
        });
    }
    let mut child = cmd.spawn().context("starting kilnctl run")?;
    println!("started kilnctl run {} (pid {}, output {}); waiting for the world", inst.name, child.id(), run_log.display());
    wait_ready(inst, watch, timeout, Launch::Detached(&mut child))
}

#[cfg(unix)]
fn report_save(inst: &Instance, before: Option<SystemTime>) {
    let dir = save_dir(inst);
    match server::newest_file(&dir) {
        Some((f, t, len)) if before.is_none_or(|b| t > b) => {
            println!("save written: {} ({}, {})", f.display(), human_size(len), fmt_utc(t))
        }
        Some((f, t, _)) => println!("warning: no save written during the stop (newest {} from {})", f.display(), fmt_utc(t)),
        None => println!("warning: no save in {}", dir.display()),
    }
}

#[cfg(unix)]
pub fn stop(inst: &Instance, timeout: Option<Duration>) -> Result<()> {
    let timeout = timeout.unwrap_or(inst.stop_timeout);
    let unit = inst.unit();
    let before = server::newest_file(&save_dir(inst)).map(|(_, t, _)| t);
    let t0 = Instant::now();
    if unit_running(&unit) {
        // Through systemd, so the unit ends cleanly and Restart=on-failure does not bring the server back.
        println!("{} runs under systemd: systemctl --user stop {unit}", inst.name);
        util::run_status(Command::new("systemctl").args(["--user", "stop", &unit]), "systemctl --user stop")?;
        println!("{} stopped after {:.1} s ({unit}: {})", inst.name, t0.elapsed().as_secs_f64(),
            unit_state(&unit).unwrap_or_else(|| "?".into()));
        report_save(inst, before);
        return Ok(());
    }
    let Some(pid) = procs::find_server(&inst.prefix) else {
        println!("{} is not running", inst.name);
        return Ok(());
    };
    let runner = wine::resolve(&inst.wine, &util::home())?;
    procs::signal(pid, libc::SIGINT).with_context(|| format!("SIGINT to pid {pid}"))?;
    println!("SIGINT -> pid {pid}; waiting up to {} s for the save", timeout.as_secs());
    if !procs::wait_gone(pid, timeout) {
        let _ = procs::signal(pid, libc::SIGKILL);
        let _ = runner.wineserver(inst).arg("-k").status();
        bail!("{} still running after {} s: killed (SIGKILL, wineserver -k); the save may be incomplete", inst.name, timeout.as_secs());
    }
    let exited = t0.elapsed();
    wineserver_wait(&runner, inst, Duration::from_secs(30))?;
    println!("{} stopped: server exited after {:.1} s, wineserver gone after {:.1} s", inst.name, exited.as_secs_f64(), t0.elapsed().as_secs_f64());
    report_save(inst, before);
    Ok(())
}

pub fn status(inst: &Instance) -> Result<i32> {
    let pid = procs::find_server(&inst.prefix);
    println!("instance  {} ({})", inst.name, inst.file.display());
    let build = steam::buildid(&inst.dir).map(|b| format!(", build {b}")).unwrap_or_default();
    let installed = if inst.exe().exists() { "installed" } else { "NOT INSTALLED" };
    println!("dir       {} ({installed}{build})", inst.dir.display());
    let pfx = if preflight::prefix_check(&inst.prefix).is_ok() { "ok" } else { "missing or incomplete" };
    println!("prefix    {} ({pfx})", inst.prefix.display());
    match wine::resolve(&inst.wine, &util::home()) {
        Ok(r) => println!("wine      {} -> {} ({})", inst.wine.to_config(), r.wine.display(), r.version().unwrap_or_else(|| "?".into())),
        Err(e) => println!("wine      {}: {e:#}", inst.wine.to_config()),
    }
    let log = log_path(inst);
    let state = server::read_log(&log).and_then(|t| server::last_state(&t));
    match pid {
        Some(p) => {
            let up = procs::uptime(p).map(human_duration).unwrap_or_else(|| "?".into());
            let bound = if procs::udp_inodes(inst.query_port).is_empty() { "not bound yet" } else { "bound" };
            let ntsync = if procs::uses_ntsync(p) { "ntsync in use" } else { "no ntsync fd" };
            println!("server    running, pid {p}, up {up}, world {}, UDP {} {bound}, {ntsync}",
                state.unwrap_or_else(|| "?".into()), inst.query_port);
        }
        None => println!("server    stopped (UDP {}){}", inst.query_port, state.map(|s| format!(", last world state {s}")).unwrap_or_default()),
    }
    if let Some(s) = unit_state(&inst.unit()) {
        println!("systemd   {}: {s}", inst.unit());
    }
    let kl = kiln::kiln_dir(inst).join("logs/kiln.log");
    if kiln::is_deployed(inst) {
        let policy = std::fs::read_to_string(kiln::kiln_dir(inst).join("kiln.json")).ok()
            .and_then(|t| serde_json::from_str::<serde_json::Value>(&t).ok())
            .and_then(|v| v.get("policy")?.as_str().map(str::to_string))
            .unwrap_or_else(|| "verified-only".into());
        let text = server::read_log(&kl).unwrap_or_default();
        let build = text.lines().rev().find(|l| l.contains("[kiln] build ")).map(|l| l.trim().to_string());
        let booted = if text.contains("boot complete") { "boot complete" } else { "no boot in kiln.log yet" };
        println!("kiln      deployed, policy {policy}, {booted}{}", build.map(|l| format!("; {l}")).unwrap_or_default());
    } else {
        println!("kiln      not deployed");
    }
    match server::newest_file(&save_dir(inst)) {
        Some((f, t, len)) => println!("save      {} ({}, {})", f.display(), human_size(len), fmt_utc(t)),
        None => println!("save      none yet"),
    }
    let backups = backups(inst);
    match backups.last() {
        Some(b) => println!("backups   {} in {}, newest {}", backups.len(), inst.backup_dir.display(),
            b.file_name().unwrap_or_default().to_string_lossy()),
        None => println!("backups   none ({})", inst.backup_dir.display()),
    }
    println!("logs      {}", log.display());
    Ok(if pid.is_some() { 0 } else { 3 })
}

pub fn logs(inst: &Instance, lines: usize, follow: bool, kiln_log: bool, run_log: bool) -> Result<()> {
    let path = if kiln_log {
        kiln::kiln_dir(inst).join("logs/kiln.log")
    } else if run_log {
        inst.run_log()
    } else {
        log_path(inst)
    };
    if !path.exists() && !follow {
        bail!("{} does not exist yet", path.display());
    }
    let mut cmd = Command::new("tail");
    cmd.arg("-n").arg(lines.to_string());
    if follow {
        cmd.arg("-F");
    }
    let mut child = cmd.arg(&path).stdout(Stdio::piped()).spawn().context("starting tail")?;
    let out = child.stdout.take().context("tail stdout")?;
    let mut stdout = std::io::stdout().lock();
    for line in BufReader::new(out).split(b'\n') {
        let line = line?;
        let line = String::from_utf8_lossy(&line);
        if writeln!(stdout, "{}", line.trim_end_matches('\r')).is_err() {
            break; // reader went away
        }
        if follow {
            stdout.flush().ok();
        }
    }
    let _ = child.kill();
    let _ = child.wait();
    Ok(())
}

fn backups(inst: &Instance) -> Vec<PathBuf> {
    let prefix = format!("{}-savegame-", inst.name);
    let mut v: Vec<PathBuf> = std::fs::read_dir(&inst.backup_dir)
        .into_iter()
        .flatten()
        .flatten()
        .filter(|e| e.file_name().to_string_lossy().starts_with(&prefix))
        .map(|e| e.path())
        .collect();
    v.sort();
    v
}

/// `<backup_dir>/<name>-savegame-<UTC stamp>.tar.gz`: the save folder, then Kiln's persistent data (`kiln/data`:
/// the per-world sidecars of kiln.saves and kiln.spawn, and the mods' data folders).
///
/// Order matters for consistency with a running server: kiln.saves writes a world save's mod-data generation
/// (`kiln/data/worlds/<id>/manifests/<rev>*.json`) before the game writes the world file, and keeps the newest 30
/// generations, so the sidecars read after the world file always contain the generation of the world file in the
/// archive. Restore with the server stopped: `tar -xzf <backup> -C <dir>`.
pub fn backup(inst: &Instance) -> Result<PathBuf> {
    let save = save_dir(inst);
    if !save.is_dir() {
        bail!("no savegame folder yet ({})", save.display());
    }
    if let Some(p) = procs::find_server(&inst.prefix) {
        println!("note: {} is running (pid {p}); a save the game is writing right now is retried", inst.name);
    }
    std::fs::create_dir_all(&inst.backup_dir)?;
    let stamp = util::stamp(SystemTime::now());
    let mut file = inst.backup_dir.join(format!("{}-savegame-{stamp}.tar.gz", inst.name));
    let mut n = 1;
    while file.exists() {
        file = inst.backup_dir.join(format!("{}-savegame-{stamp}-{n}.tar.gz", inst.name));
        n += 1;
    }
    let with_data = kiln::kiln_dir(inst).join(KILN_DATA).is_dir();
    let args = backup_args(&file, &save, &inst.dir, with_data)?;
    for attempt in 1..=3 {
        let st = Command::new("tar").args(&args).status().context("running tar")?;
        match st.code() {
            Some(0) => {
                let len = file.metadata().map(|m| m.len()).unwrap_or(0);
                let what = if with_data { "savegame + kiln/data" } else { "savegame" };
                println!("backup {} ({}, {what})", file.display(), human_size(len));
                return Ok(file);
            }
            // GNU tar: 1 = a file changed while it was read (the game or Kiln saved meanwhile)
            Some(1) if attempt < 3 => {
                println!("the save changed during the backup; retrying");
                std::thread::sleep(Duration::from_secs(2));
            }
            _ => {
                let _ = std::fs::remove_file(&file);
                bail!("tar failed ({st})");
            }
        }
    }
    let _ = std::fs::remove_file(&file);
    bail!("the save kept changing during the backup")
}

/// Kiln's persistent data, relative to the kiln folder.
const KILN_DATA: &str = "data";

/// tar arguments of a backup: the save folder first, then `kiln/data` (relative to the instance folder) without
/// in-flight temp files of kiln.saves' atomic writes.
fn backup_args(file: &Path, save: &Path, inst_dir: &Path, with_data: bool) -> Result<Vec<std::ffi::OsString>> {
    let parent = save.parent().context("savegame folder has no parent")?;
    let base = save.file_name().context("savegame folder name")?;
    let mut a: Vec<std::ffi::OsString> = vec!["-czf".into(), file.into(), "--exclude=*.tmp-*".into(), "-C".into(), parent.into(), base.into()];
    if with_data {
        a.extend(["-C".into(), inst_dir.into(), Path::new("kiln").join(KILN_DATA).into()]);
    }
    Ok(a)
}

pub struct UpdateArgs {
    pub no_start: bool,
    pub timeout: Duration,
    /// No stop, backup, SteamCMD or start: port and gate as if `new_exe` (default: the instance's exe) had just been
    /// installed. Never writes a verified record.
    pub dry_run: bool,
    pub new_exe: Option<PathBuf>,
    pub old_exe: Option<PathBuf>,
    pub no_port: bool,
    pub flow: FlowArgs,
}

/// What `port` / `update` do after the symbols are ported.
#[derive(Clone)]
pub struct FlowArgs {
    pub gate: bool,
    pub mark: bool,
    pub defs: Option<PathBuf>,
    pub gate_opts: gate::GateOpts,
}

pub fn update(inst: &Instance, a: UpdateArgs) -> Result<()> {
    if a.dry_run {
        let new = match &a.new_exe {
            Some(p) => absolute(p)?,
            None => inst.exe(),
        };
        println!("== dry run: no stop, backup, steamcmd or start; treating {} as the updated exe", new.display());
        let sha = util::sha256(&new)?;
        let report = kiln::sigdb_check(inst, &new);
        print_sigdb(&report);
        if kiln::verified(inst, &sha) == Some(true) {
            println!("verified  {sha} already has a verified record; nothing to port");
            return Ok(());
        }
        if a.no_port {
            return Ok(());
        }
        let mut flow = a.flow.clone();
        flow.mark = false;
        port_flow(inst, a.old_exe.clone(), &new, &flow, true)?;
        return Ok(());
    }
    let was_running = procs::find_server(&inst.prefix).is_some();
    let via_systemd = unit_running(&inst.unit());
    if was_running || via_systemd {
        println!("== stop");
        stop(inst, None)?;
    }
    println!("== backup");
    // the one step of an update that may use the launcher's data root (a backup_dir inside it): the lock for that step
    let backup = if save_dir(inst).is_dir() { Some({ let _held = crate::lock::hold()?; backup(inst)? }) } else {
        println!("no savegame yet; nothing to back up");
        None
    };
    let old_build = steam::buildid(&inst.dir);
    let old_sha = util::sha256(&inst.exe()).ok();
    // the port after the update needs the old exe: keep it while it is still there
    let cached_old = sigport::remember_verified_exe(inst);
    if let Some(p) = &cached_old {
        println!("exe cache {}", p.display());
    }
    println!("== steamcmd");
    steam::app_update(&inst.dir)?;
    let new_build = steam::buildid(&inst.dir);
    let new_sha = util::sha256(&inst.exe())?;
    println!("== Kiln on the new build");
    let report = kiln::sigdb_check(inst, &inst.exe());
    let mut verified = kiln::verified(inst, &new_sha);
    let changed = old_sha.as_deref() != Some(new_sha.as_str());
    let deployed = kiln::is_deployed(inst);
    let mut outcome = None;
    if deployed && verified != Some(true) && !a.no_port {
        println!("== sigport: no verified record for {new_sha}");
        match port_flow(inst, a.old_exe.clone().or(cached_old), &inst.exe(), &a.flow, false) {
            Ok(o) => outcome = Some(o),
            Err(e) => println!("sigport   failed: {e:#}"),
        }
        verified = kiln::verified(inst, &new_sha);
    }
    println!("== summary for {}", inst.name);
    let b = |o: &Option<String>| o.clone().unwrap_or_else(|| "?".into());
    println!("build     {} -> {} ({}), exe sha256 {}", b(&old_build), b(&new_build), if changed { "CHANGED" } else { "unchanged" }, new_sha);
    print_sigdb(&report);
    if deployed {
        println!("verified  {}", kiln::policy_note(inst.kiln.policy, verified));
    } else {
        println!("kiln      not deployed in this instance (the check used {})", inst.kiln.source.join("sigdb/symbols/server.json").display());
    }
    if let Some(o) = &outcome {
        println!("sigport   {}", o.summary);
    }
    if let Some(b) = backup {
        println!("backup    {}", b.display());
    }
    if (was_running || via_systemd) && !a.no_start {
        println!("== start");
        start(inst, via_systemd, a.timeout)?;
    }
    Ok(())
}

fn print_sigdb(report: &Result<kiln::SigdbReport>) {
    match report {
        Ok(r) => {
            println!("sigdb     {}/{} symbols, {}/{} constants resolve ({})", r.symbols_ok, r.symbols, r.constants_ok, r.constants, r.json.display());
            for f in &r.failed {
                println!("          FAIL {f}");
            }
            let off = r.off();
            let on: Vec<&str> = r.features.iter().filter(|(_, v)| **v).map(|(n, _)| n.as_str()).collect();
            println!("features  on: {}", if on.is_empty() { "none".into() } else { on.join(", ") });
            println!("features  OFF: {}", if off.is_empty() { "none".into() } else { off.join(", ") });
        }
        Err(e) => println!("sigdb     check failed: {e:#}"),
    }
}

pub struct FlowOutcome {
    pub marked: Option<PathBuf>,
    pub summary: String,
}

/// sigport port (old -> new) -> lab gate -> mark verified. Marks only when the port is complete, the deployed
/// definitions need no change (the instance runs them as they are) and the gate is green; otherwise it writes what a
/// human has to do next to the port's output (NEXT.txt). `dry` only says what it would mark.
pub fn port_flow(inst: &Instance, old: Option<PathBuf>, new: &Path, f: &FlowArgs, dry: bool) -> Result<FlowOutcome> {
    let new_sha = util::sha256(new)?;
    let old = match old {
        Some(p) => absolute(&p)?,
        None => sigport::baseline_exe(inst, &new_sha).map(|(p, _)| p).with_context(|| {
            format!("no cached exe with a verified record in {} (kilnctl caches the exe of a verified build before \
                     an update, and on kilnctl port/gate); pass --old <exe>", sigport::cache_dir().display())
        })?,
    };
    let defs = match &f.defs {
        Some(p) => absolute(p)?,
        None => sigport::defs(inst),
    };
    let out = util::state_dir().join("sigport").join(&inst.name)
        .join(format!("{}-{}", &new_sha[..12], util::stamp(SystemTime::now())));
    let rep = sigport::port(inst, &old, new, &defs, &out)?;
    rep.print();
    let mut next = Vec::new();
    let mut gate_res = None;
    if f.gate {
        let mut o = f.gate_opts.clone();
        o.record = Some(rep.record());
        o.sigdb_tree = (!rep.defs_unchanged).then(|| rep.tree());
        o.exe = (util::clean(new) != util::clean(&inst.exe())).then(|| new.to_path_buf());
        match gate::run(inst, &o) {
            Ok(r) => gate_res = Some(r),
            Err(e) => next.push(format!("the gate failed to run: {e:#}")),
        }
    } else {
        next.push("no gate run (--no-gate): boot a lab with policy any and the self tests before marking".into());
    }
    let green = gate_res.as_ref().is_some_and(|r| r.green);
    if !rep.complete {
        next.push(format!("the port is incomplete: low confidence {:?}, lost {:?} (see {})", rep.low, rep.lost,
            rep.out.join("report.txt").display()));
    }
    if !rep.defs_unchanged {
        next.push(format!("the definitions need changes: review and apply {} to the Kiln tree (git apply), redeploy \
                           (kilnctl deploy {}), then run kilnctl port {} --mark", rep.patch().display(), inst.name, inst.name));
    }
    if let Some(r) = &gate_res {
        if !r.green {
            next.push(format!("gate RED: {}", r.reasons.join("; ")));
        }
    }
    let ok = green && rep.complete && rep.defs_unchanged;
    let mut marked = None;
    let summary;
    if ok && f.mark && !dry {
        let r = gate_res.as_ref().expect("green implies a gate result");
        let note = format!("verified by kilnctl {} ({}): sigport port from {} ({}/{} symbols, {}/{} constants), gate green: {}",
            if dry { "dry run" } else { "update/port" }, util::fmt_utc(SystemTime::now()),
            util::sha256(&old).map(|s| s[..12].to_string()).unwrap_or_default(),
            rep.symbols.0, rep.symbols.1, rep.constants.0, rep.constants.1,
            r.tests.iter().map(|(t, x)| format!("{t} {}/{}", x.pass, x.pass + x.fail)).collect::<Vec<_>>().join(", "));
        let p = sigport::mark_verified(inst, &rep, &note)?;
        // the new build's green run is the next update's baseline
        std::fs::write(gate::baseline_path(inst), serde_json::to_string_pretty(r)? + "\n")?;
        println!("verified  wrote {} (a copy for the repository: {})", p.display(),
            rep.out.join(format!("verified-{new_sha}.json")).display());
        marked = Some(p);
        summary = format!("marked verified ({})", rep.out.display());
    } else if ok {
        summary = format!("ready to mark verified ({}); {}", rep.out.display(),
            if dry { "dry run: not written" } else { "pass --mark to write the record" });
        println!("verified  {summary}");
    } else {
        summary = format!("left for a human: {}", rep.out.join("NEXT.txt").display());
        println!("verified  not marked; {summary}");
        for n in &next {
            println!("          - {n}");
        }
    }
    let text = format!("kilnctl port flow for {} ({})\nold {}\nnew {} ({new_sha})\ndefs {}\nresult: {summary}\n\n{}\n",
        inst.name, util::fmt_utc(SystemTime::now()), old.display(), new.display(), defs.display(),
        if next.is_empty() { "nothing left to do".to_string() } else { next.iter().map(|n| format!("- {n}")).collect::<Vec<_>>().join("\n") });
    std::fs::write(rep.out.join("NEXT.txt"), text)?;
    Ok(FlowOutcome { marked, summary })
}

pub fn port_cmd(inst: &Instance, old: Option<PathBuf>, new: Option<PathBuf>, f: &FlowArgs) -> Result<i32> {
    if let Some(p) = sigport::remember_verified_exe(inst) {
        println!("exe cache {}", p.display());
    }
    let new = match new {
        Some(p) => absolute(&p)?,
        None => inst.exe(),
    };
    let o = port_flow(inst, old, &new, f, false)?;
    Ok(if o.marked.is_some() || o.summary.starts_with("ready") { 0 } else { 1 })
}

/// Boots the gate copy on the instance as it is; `baseline` records the result as the instance's baseline (only on a
/// verified build).
pub fn gate_cmd(inst: &Instance, o: &gate::GateOpts, baseline: bool) -> Result<i32> {
    let sha = util::sha256(&inst.exe())?;
    if let Some(p) = sigport::remember_verified_exe(inst) {
        println!("exe cache {}", p.display());
    }
    if baseline && kiln::verified(inst, &sha) != Some(true) {
        bail!("{sha} has no verified record in {}: a baseline must come from a verified build", inst.name);
    }
    let mut r = gate::run(inst, o)?;
    if baseline {
        if !r.kiln_active || r.tests.values().any(|t| !t.summary) {
            bail!("not recorded as baseline: Kiln inactive or a test without summary");
        }
        r.green = true;
        r.reasons.clear();
        std::fs::write(gate::baseline_path(inst), serde_json::to_string_pretty(&r)? + "\n")?;
        println!("gate: baseline recorded: {}", gate::baseline_path(inst).display());
        return Ok(0);
    }
    Ok(if r.green { 0 } else { 1 })
}

pub fn list() -> Result<()> {
    let names = config::list();
    if names.is_empty() {
        println!("no instances in {} (kilnctl init <name> --dir <dir>)", config::instances_dir().display());
    }
    for n in names {
        match config::load(&n) {
            Ok(i) => {
                let st = match procs::find_server(&i.prefix) {
                    Some(p) => format!("running (pid {p})"),
                    None => "stopped".into(),
                };
                println!("{:<20} UDP {:<5}  {:<20} {}", n, i.query_port, st, i.dir.display());
            }
            Err(e) => println!("{n:<20} invalid: {e:#}"),
        }
    }
    Ok(())
}


// Unix: kilnctl's Wine instances (Unix paths, tar, /proc); on Windows kilnctl only bundles, installs and administers.
#[cfg(all(test, unix))]
mod tests {
    use super::*;

    #[test]
    fn sample_bundle_deploy_preserves_opt_outs_when_new_defaults_require_them() {
        // Isolate DataDirs::locate in a child test process instead of changing this process's environment.
        const ROOT: &str = "KILN_COMP19_DEPLOY_TEST_ROOT";
        let Some(root) = std::env::var_os(ROOT).map(PathBuf::from) else {
            let root = std::env::temp_dir().join(format!("kilnctl-sample-deploy-{}", std::process::id()));
            let _ = std::fs::remove_dir_all(&root);
            let result = Command::new(std::env::current_exe().unwrap())
                .args(["--exact", "ops::tests::sample_bundle_deploy_preserves_opt_outs_when_new_defaults_require_them", "--nocapture"])
                .env(ROOT, &root).env("KILN_DATA_DIR", root.join("data"))
                .output().unwrap();
            let _ = std::fs::remove_dir_all(&root);
            assert!(result.status.success(), "{}\n{}", String::from_utf8_lossy(&result.stdout), String::from_utf8_lossy(&result.stderr));
            return;
        };
        let put = |base: &Path, rel: &str, bytes: &[u8]| {
            let p = base.join(rel);
            std::fs::create_dir_all(p.parent().unwrap()).unwrap();
            std::fs::write(p, bytes).unwrap();
        };
        let game = root.join("server");
        put(&game, "enshrouded_server.exe", b"MZ synthetic server");
        let g = kiln_manage::game::inspect(&game).unwrap();
        let dirs = kiln_manage::paths::DataDirs::at(root.join("data"));
        for (tag, late_default, epoch) in [("aaaaaaa", false, 1_793_793_600), ("bbbbbbb", true, 1_793_880_000)] {
            let stage = root.join(tag);
            let manifest = serde_json::json!({
                "format": 1, "kiln": "0.2.0-dev", "abi": 2, "config": ["kiln/kiln.json"],
                "components": {
                    "core": {"sides": ["client", "server"], "default": true},
                    "emf": {"sides": ["client", "server"], "default": true, "requires": ["core"]},
                    "late": {"sides": ["client", "server"], "default": late_default, "requires": ["emf"]},
                    "sigdb-server": {"sides": ["server"], "default": true, "requires": ["core"]},
                    "samples": {"sides": ["server"], "default": false, "requires": ["core"]}
                }
            });
            put(&stage, "kiln-runtime.json", &serde_json::to_vec(&manifest).unwrap());
            put(&stage, "core/dinput8.dll", &kiln_manage::pe::build_test_dll(&["DirectInput8Create", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev\0"));
            put(&stage, "core/kiln/kiln.json", b"{\"policy\":\"verified-only\"}");
            for (part, path) in [("emf", "kiln/plugins/emf.dll"), ("late", "kiln/plugins/late.dll"),
                                 ("sigdb-server", "kiln/sigdb/server.json"), ("samples", "kiln/mods/sample.dll")] {
                put(&stage, &format!("{part}/{path}"), tag.as_bytes());
            }
            let zip = kiln_manage::bundle::create(&stage, &root.join("out"), &kiln_manage::bundle::CreateOptions {
                source_date_epoch: epoch, git: tag.into()
            }).unwrap();
            if !late_default {
                kiln_manage::install::install(&dirs, &g, &zip, &kiln_manage::install::InstallOptions {
                    components: Some(vec!["core".into(), "sigdb-server".into()]), ..Default::default()
                }, &mut kiln_manage::task::NoProgress).unwrap();
            } else {
                let inst = config::parse("synthetic-review", &root.join("instance.toml"),
                    &format!("dir = {:?}\nwine = \"system\"\n", game.to_str().unwrap()), &root).unwrap();
                deploy_bundle(&inst, &zip, true).unwrap();
            }
        }
        let r = kiln_manage::install::read_receipt(&game).unwrap().unwrap();
        assert_eq!(r.runtime.components, ["core", "emf", "late", "samples", "sigdb-server"]);
        assert_eq!(r.runtime.deselected.as_deref().unwrap(), ["emf"]);
        assert!(game.join("kiln/mods/sample.dll").is_file());
    }

    #[test]
    fn backup_args_save_then_kiln_data() {
        let a = backup_args(Path::new("/b/x.tar.gz"), Path::new("/srv/x/savegame"), Path::new("/srv/x"), true).unwrap();
        let a: Vec<String> = a.iter().map(|s| s.to_string_lossy().into_owned()).collect();
        assert_eq!(a, ["-czf", "/b/x.tar.gz", "--exclude=*.tmp-*", "-C", "/srv/x", "savegame", "-C", "/srv/x", "kiln/data"]);
        let b = backup_args(Path::new("/b/x.tar.gz"), Path::new("/elsewhere/save"), Path::new("/srv/x"), false).unwrap();
        assert_eq!(b.len(), 6);
        assert_eq!(b[5], "save");
    }

    /// Runs the real tar on a fake instance: the archive holds the save and the sidecars, without temp files, and
    /// extracts back to the same layout.
    #[test]
    fn backup_archive_contains_sidecars() {
        let root = std::env::temp_dir().join(format!("kilnctl-backup-test-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let inst = root.join("srv");
        let world = inst.join("kiln/data/worlds/11111111-2222-4333-8444-555555555555");
        std::fs::create_dir_all(inst.join("savegame")).unwrap();
        std::fs::create_dir_all(world.join("manifests")).unwrap();
        std::fs::create_dir_all(world.join("blobs/ab")).unwrap();
        std::fs::write(inst.join("savegame/world0.ksd"), b"SV2C").unwrap();
        std::fs::write(world.join("manifests/0000003007-1793770110.json"), b"{}").unwrap();
        std::fs::write(world.join("blobs/ab/ab12.json"), b"{}").unwrap();
        std::fs::write(world.join("blobs/ab/ab12.json.tmp-7-1"), b"partial").unwrap();
        let file = root.join("b.tar.gz");
        let st = Command::new("tar").args(backup_args(&file, &inst.join("savegame"), &inst, true).unwrap()).status().unwrap();
        assert!(st.success());
        let out = Command::new("tar").arg("-tzf").arg(&file).output().unwrap();
        let list = String::from_utf8_lossy(&out.stdout);
        assert!(list.lines().any(|l| l == "savegame/world0.ksd"), "{list}");
        assert!(list.lines().any(|l| l.ends_with("manifests/0000003007-1793770110.json")), "{list}");
        assert!(list.lines().any(|l| l.ends_with("blobs/ab/ab12.json")), "{list}");
        assert!(!list.contains(".tmp-"), "{list}");
        let restore = root.join("restore");
        std::fs::create_dir_all(&restore).unwrap();
        assert!(Command::new("tar").arg("-xzf").arg(&file).arg("-C").arg(&restore).status().unwrap().success());
        assert!(restore.join("savegame/world0.ksd").exists());
        assert!(restore.join("kiln/data/worlds/11111111-2222-4333-8444-555555555555/blobs/ab/ab12.json").exists());
        let _ = std::fs::remove_dir_all(&root);
    }
}
