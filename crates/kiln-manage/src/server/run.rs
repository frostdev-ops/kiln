//! Start / stop / restart / status per runner (instance.rs). Every local start is windowless: the server's stdout and
//! stderr go to its capture file (console.rs, the launcher's Console view), never to a console window, and the server
//! outlives the launcher.
//! - kilnctl: Wine started directly in its own session (`setsid`), output to kilnctl's run log (the capture file);
//!   stop through systemd when `kiln@<name>` runs, else SIGINT, the save, `wineserver -w`.
//! - steam (Linux): the build's own wine in Steam's Proton prefix, like kilnctl (proton.rs: the Proton build Steam
//!   would use, its launch options; never `steam://`, whose Proton `steam.exe` opens a console window); stop SIGINT
//!   to the server process, refused while players are online. On Windows a Steam install starts like `windows`.
//! - windows: the exe with `CREATE_NO_WINDOW` (its own console, without a window) and its output in the capture file;
//!   stop = Ctrl-C into that console (the game saves and exits), else the admin `save_now` op, then terminate.
//! - remote: `kilnctl start|stop <instance>` over SSH.
//! On Linux no Wine process the launcher or kilnctl starts can reach a display: `DISPLAY` is unset and
//! `WAYLAND_DISPLAY` names a socket that does not exist (console::NO_DISPLAY), so not even a crash dialog opens.
//! A start returns when the game log says the world runs (`LoadEcsScene to Run`) or fails.

use super::instance::{ServerInstance, ServerRunner};
use super::{ServerState, ServerStatus};
use crate::task::Progress;
use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant, SystemTime};

/// How long a start waits for the world.
pub const START_TIMEOUT: Duration = Duration::from_secs(600);

/// The game log of a local server (enshrouded_server.json logDirectory).
pub fn log_path(instance: &ServerInstance) -> Result<PathBuf> {
    let dir = instance.local_dir()?;
    let cfg = super::game::read_config(&dir.join(super::game::CONFIG)).ok();
    let rel = cfg.map(|c| c.log_dir).unwrap_or_else(|| "./logs".into());
    let pfx = instance.prefix.clone().unwrap_or_default();
    Ok(super::game::host_path(dir, &pfx, &rel).join(super::game::LOG))
}

/// The savegame folder of a local server (enshrouded_server.json saveDirectory).
pub fn save_dir(instance: &ServerInstance) -> Result<PathBuf> {
    let dir = instance.local_dir()?;
    let cfg = super::game::read_config(&dir.join(super::game::CONFIG)).ok();
    let rel = cfg.map(|c| c.save_dir).unwrap_or_else(|| "./savegame".into());
    let pfx = instance.prefix.clone().unwrap_or_default();
    Ok(super::game::host_path(dir, &pfx, &rel))
}

/// The server's process on this machine, if it runs.
pub fn pid(instance: &ServerInstance) -> Option<u32> {
    let dir = instance.dir.as_deref()?;
    match instance.runner {
        ServerRunner::Kilnctl => instance
            .prefix
            .as_deref()
            .and_then(super::procs::find_server)
            .or_else(|| super::procs::servers_in_dir(dir).into_iter().next()),
        ServerRunner::Steam if cfg!(unix) => super::procs::servers_in_dir(dir)
            .into_iter()
            .next()
            .or_else(|| instance.prefix.as_deref().and_then(super::procs::find_server)),
        ServerRunner::Steam | ServerRunner::Windows => crate::proc::find_exe(dir, super::game::EXE).ok().flatten().map(|p| p.pid),
        ServerRunner::Remote => None,
    }
}

fn uptime(pid: u32) -> Option<Duration> {
    #[cfg(unix)]
    {
        super::procs::uptime(pid)
    }
    #[cfg(not(unix))]
    {
        // Windows: GetProcessTimes' creation time
        SystemTime::now().duration_since(crate::proc::start_time(pid)?).ok()
    }
}

pub fn status(instance: &ServerInstance) -> Result<ServerStatus> {
    if instance.runner == ServerRunner::Remote {
        return remote_status(instance);
    }
    let dir = instance.local_dir()?;
    let pid = pid(instance);
    // boot.json stays after the server exits (and survives an uninstall), endpoint.json after a crash: they describe
    // this run only while the server runs, and on Windows only when their pid is the server's (both are Windows pids).
    // boot.json must also be newer than the process (under Wine its pid says nothing): a restart shows no old boot.
    let current = |file_pid: u32| pid.is_some_and(|p| !cfg!(windows) || p == file_pid);
    let started = pid.and_then(crate::proc::start_time);
    let boot_written = std::fs::metadata(dir.join(crate::paths::game::BOOT_JSON)).and_then(|m| m.modified()).ok();
    let boot = crate::state::read_boot(dir)
        .ok()
        .flatten()
        .filter(|b| current(b.pid) && crate::launch::written_after_start(boot_written, started));
    let endpoint = crate::admin::discover::endpoint(dir).ok().flatten().filter(|e| current(e.pid));
    let up = pid.and_then(uptime);
    let state = match pid {
        None => ServerState::Stopped,
        Some(_) => {
            // the world runs once the log says so after this process started
            let log = log_path(instance).ok().and_then(|p| super::game::read_log(&p));
            let ready = log.as_deref().and_then(super::game::last_state).is_some_and(|s| s.contains("Run"))
                || log.as_deref().is_some_and(|t| t.contains(super::game::READY));
            if ready { ServerState::Running } else { ServerState::Starting }
        }
    };
    let players = if state == ServerState::Running {
        super::query::query(&format!("127.0.0.1:{}", instance.query_port), Duration::from_millis(600)).ok().map(|q| q.players)
    } else {
        None
    };
    Ok(ServerStatus {
        id: instance.id.clone(),
        state,
        pid,
        uptime_s: up.map(|d| d.as_secs()),
        players,
        kiln: boot.as_ref().map(|b| b.kiln.clone()),
        installed: installed_kiln(dir),
        boot,
        endpoint,
    })
}

/// Kiln in a server folder: its install receipt's runtime version, else the version of a Kiln proxy put there without
/// the installer (a deploy script; `"unknown"` when the proxy names none). None: no Kiln.
pub fn installed_kiln(dir: &Path) -> Option<String> {
    if let Ok(Some(r)) = crate::install::read_receipt(dir) {
        return Some(r.runtime.version);
    }
    let c = crate::pe::classify_proxy(&dir.join(crate::install::PROXY)).ok()?;
    (c.kind == crate::pe::ProxyKind::Kiln).then(|| c.version.unwrap_or_else(|| "unknown".into()))
}

fn remote_status(instance: &ServerInstance) -> Result<ServerStatus> {
    let r = instance.remote.as_ref().ok_or_else(|| Error::invalid("remote server without ssh details"))?;
    let ep = crate::admin::discover::remote_endpoint(r).ok().flatten();
    Ok(ServerStatus {
        id: instance.id.clone(),
        state: if ep.is_some() { ServerState::Running } else { ServerState::Unknown },
        pid: None,
        uptime_s: None,
        players: None,
        kiln: ep.as_ref().and_then(|e| e.kiln.clone()),
        installed: None,
        boot: None,
        endpoint: ep,
    })
}

/// Waits for the world after a start (the log line after `since`), reporting log progress.
fn wait_ready(instance: &ServerInstance, since: SystemTime, progress: &mut dyn Progress, exited: &mut dyn FnMut() -> Option<String>) -> Result<ServerStatus> {
    let t0 = Instant::now();
    let log = log_path(instance)?;
    // Kiln's own line for the same moment: on Windows the game's log can reach the disk long after the world runs
    // (a start task then ended only when Stop made the server flush it)
    let kiln_log = instance.local_dir().ok().map(|d| d.join("kiln/logs/kiln.log"));
    loop {
        if progress.canceled() {
            return Err(Error::new(ErrorCode::Canceled, "stopped waiting (the server keeps starting)"));
        }
        if let Some(k) = &kiln_log
            && std::fs::metadata(k).and_then(|m| m.modified()).is_ok_and(|m| m >= since)
            && std::fs::read_to_string(k).is_ok_and(|t| t.lines().any(|l| l.contains("tick: server world") && l.contains(" started")))
        {
            progress.report(Some(1.0), &format!("{} runs ({:.0} s)", instance.id, t0.elapsed().as_secs_f64()));
            return status(instance);
        }
        let fresh = std::fs::metadata(&log).and_then(|m| m.modified()).is_ok_and(|m| m >= since);
        if fresh {
            if let Some(text) = super::game::read_log(&log) {
                if text.contains(super::game::READY) {
                    progress.report(Some(1.0), &format!("{} runs ({:.0} s)", instance.id, t0.elapsed().as_secs_f64()));
                    return status(instance);
                }
                if let Some(last) = text.lines().rev().find(|l| !l.trim().is_empty()) {
                    let secs = t0.elapsed().as_secs_f32();
                    progress.report(Some((secs / 60.0).min(0.95)), last.trim().chars().take(140).collect::<String>().as_str());
                }
            }
        }
        if let Some(why) = exited() {
            return Err(Error::new(ErrorCode::Io, format!("{} exited during the start: {why}", instance.id)));
        }
        if t0.elapsed() > START_TIMEOUT {
            return Err(Error::new(ErrorCode::Timeout, format!("no \"{}\" in {} after {} s; the server keeps running", super::game::READY, log.display(), START_TIMEOUT.as_secs())));
        }
        std::thread::sleep(Duration::from_millis(500));
    }
}

/// Returns when the world runs (or fails).
pub fn start(instance: &ServerInstance, progress: &mut dyn Progress) -> Result<ServerStatus> {
    if instance.runner != ServerRunner::Remote {
        if let Some(p) = pid(instance) {
            return Err(Error::new(ErrorCode::Conflict, format!("{} is running already (pid {p})", instance.id)));
        }
        let dir = instance.local_dir()?;
        if !dir.join(super::game::EXE).exists() {
            return Err(Error::not_found(format!("{} has no {} (install it first)", dir.display(), super::game::EXE)));
        }
    }
    let since = SystemTime::now() - Duration::from_secs(1);
    match instance.runner {
        ServerRunner::Kilnctl => start_kilnctl(instance, since, progress),
        ServerRunner::Steam if cfg!(windows) => start_windows(instance, since, progress),
        ServerRunner::Steam => start_steam(instance, since, progress),
        ServerRunner::Windows => start_windows(instance, since, progress),
        ServerRunner::Remote => remote_kilnctl(instance, "start").and_then(|_| status(instance)),
    }
}

#[cfg(all(unix, feature = "server"))]
fn start_kilnctl(instance: &ServerInstance, since: SystemTime, progress: &mut dyn Progress) -> Result<ServerStatus> {
    let inst = instance.kilnctl_instance()?;
    let runner = crate::wine::resolve(&inst.wine, &crate::util::home())?;
    if !inst.prefix.join("system.reg").exists() {
        progress.report(None, "creating the Wine prefix");
        make_prefix(&inst)?;
    }
    let note = format!("{} ./{} in {} (prefix {}, wine {})", runner.wine.display(), super::game::EXE, inst.dir.display(), inst.prefix.display(), runner.label);
    let (run_log, log) = super::console::open_capture(instance, &[note])?;
    std::fs::create_dir_all(inst.dir.join("logs")).ok();
    let mut cmd = runner.command(&inst);
    headless(&mut cmd);
    cmd.arg(format!("./{}", super::game::EXE)).current_dir(&inst.dir);
    let mut child = spawn_detached(&mut cmd, log).map_err(|e| Error::new(ErrorCode::Io, format!("starting {}: {e}", runner.wine.display())))?;
    progress.report(Some(0.02), &format!("started {} ./{} (wine pid {}), output in {}", runner.label, super::game::EXE, child.id(), run_log.display()));
    let t0 = Instant::now();
    let prefix = inst.prefix.clone();
    let r = wait_ready(instance, since, progress, &mut || {
        // wine's launcher exits once the server runs on its own; only "no server process at all" is a failure
        if let Ok(Some(st)) = child.try_wait() {
            if t0.elapsed() > Duration::from_secs(20) && super::procs::find_server(&prefix).is_none() {
                return Some(format!("wine exited ({st}), no server process; see the Console (or {})", run_log.display()));
            }
        }
        None
    });
    std::thread::spawn(move || {
        let _ = child.wait();  // reap the wine launcher when it exits
    });
    r
}

#[cfg(not(all(unix, feature = "server")))]
fn start_kilnctl(_instance: &ServerInstance, _since: SystemTime, _progress: &mut dyn Progress) -> Result<ServerStatus> {
    Err(Error::new(ErrorCode::Unsupported, "kilnctl instances run on Linux"))
}

/// No display for a Wine process (module docs): `DISPLAY` unset, `WAYLAND_DISPLAY` a socket that does not exist.
pub fn headless(cmd: &mut std::process::Command) -> &mut std::process::Command {
    cmd.env_remove("DISPLAY").env_remove("WAYLAND_SOCKET").env("WAYLAND_DISPLAY", super::console::NO_DISPLAY)
}

/// Spawns the server's launcher process in its own session (it outlives the launcher), stdin /dev/null, stdout and
/// stderr into `output` (not a terminal: Wine gives the server a console without a window).
#[cfg(unix)]
fn spawn_detached(cmd: &mut std::process::Command, output: std::fs::File) -> std::io::Result<std::process::Child> {
    use std::os::unix::process::CommandExt;
    cmd.stdin(std::process::Stdio::null()).stdout(output.try_clone()?).stderr(output);
    // SAFETY: setsid and signal are async-signal-safe; the server gets its own session and outlives the launcher.
    unsafe {
        cmd.pre_exec(|| {
            if libc::setsid() < 0 {
                return Err(std::io::Error::last_os_error());
            }
            libc::signal(libc::SIGTTOU, libc::SIG_IGN);
            Ok(())
        });
    }
    cmd.spawn()
}

/// Inherited variables that would mix Steam, another Wine or a display into a Steam install's start.
#[cfg(unix)]
const STEAM_SCRUB: &[&str] = &[
    "WINEPREFIX", "WINESERVER", "WINELOADER", "WINEDLLPATH", "WINEARCH", "WINEDLLOVERRIDES", "WINEDEBUG", "WINEESYNC", "WINEFSYNC",
    "STEAM_COMPAT_DATA_PATH", "STEAM_COMPAT_CLIENT_INSTALL_PATH", "STEAM_COMPAT_APP_ID", "SteamAppId", "SteamGameId", "LD_PRELOAD",
    "KILN_DISABLE",
];

/// A Steam install started without Steam (proton.rs): the build's wine, Steam's prefix, the launch options.
#[cfg(unix)]
fn start_steam(instance: &ServerInstance, since: SystemTime, progress: &mut dyn Progress) -> Result<ServerStatus> {
    let dir = instance.local_dir()?.to_path_buf();
    let plan = super::proton::plan(instance)?;
    let mut notes = vec![format!(
        "{} ./{} in {} (Steam install; {} at {}, prefix {})",
        plan.wine.display(),
        super::game::EXE,
        dir.display(),
        plan.tool,
        plan.tool_dir.display(),
        plan.prefix.display()
    )];
    notes.extend(plan.notes.iter().cloned());
    let (cap, out) = super::console::open_capture(instance, &notes)?;
    std::fs::create_dir_all(dir.join("logs")).ok();
    let mut cmd = crate::hostenv::command(&plan.wine);
    for k in STEAM_SCRUB {
        cmd.env_remove(k);
    }
    cmd.envs(plan.env.iter().map(|(k, v)| (k.as_str(), v.as_str())));
    headless(&mut cmd);
    cmd.arg(format!("./{}", super::game::EXE)).args(&plan.args).current_dir(&dir);
    let mut child = spawn_detached(&mut cmd, out).map_err(|e| Error::new(ErrorCode::Io, format!("starting {}: {e}", plan.wine.display())))?;
    progress.report(Some(0.02), &format!("started {} ./{} with {} (wine pid {}), output in {}", plan.wine.display(), super::game::EXE, plan.tool, child.id(), cap.display()));
    let t0 = Instant::now();
    let prefix = plan.prefix.clone();
    let r = wait_ready(instance, since, progress, &mut || {
        if let Ok(Some(st)) = child.try_wait() {
            if t0.elapsed() > Duration::from_secs(20) && super::procs::find_server(&prefix).is_none() && super::procs::servers_in_dir(&dir).is_empty() {
                return Some(format!("wine exited ({st}), no server process; see the Console (or {})", cap.display()));
            }
        }
        None
    });
    std::thread::spawn(move || {
        let _ = child.wait();
    });
    r
}

#[cfg(not(unix))]
fn start_steam(_instance: &ServerInstance, _since: SystemTime, _progress: &mut dyn Progress) -> Result<ServerStatus> {
    Err(Error::new(ErrorCode::Unsupported, "a Steam install starts like a Windows server here"))
}

/// `CREATE_NO_WINDOW`: the server gets a console of its own without a window (not `DETACHED_PROCESS`, which leaves it
/// none and cannot be combined), so the launcher's Ctrl-C never reaches it, while `stop` can still send it one
/// (ctrl_c). Never `CREATE_NEW_PROCESS_GROUP`: that turns the server's Ctrl-C handling off (windows worker, verified
/// under Wine with build 26430253: no shutdown in 60 s).
#[cfg(windows)]
pub const WINDOWS_START_FLAGS: u32 = 0x0800_0000;
/// Leaves the launcher's job object when there is one (the server must outlive the launcher); retried without when
/// the job does not allow it.
#[cfg(windows)]
const CREATE_BREAKAWAY_FROM_JOB: u32 = 0x0100_0000;

/// Starts `exe` windowless with stdout and stderr into `output` (Windows; the launcher and a Wine test use it).
#[cfg(windows)]
pub fn spawn_windowless(exe: &Path, dir: &Path, args: &[String], output: std::fs::File) -> std::io::Result<std::process::Child> {
    use std::os::windows::process::CommandExt;
    let mut cmd = crate::hostenv::command(exe);
    cmd.current_dir(dir).args(args).stdin(std::process::Stdio::null()).stdout(output.try_clone()?).stderr(output);
    match cmd.creation_flags(WINDOWS_START_FLAGS | CREATE_BREAKAWAY_FROM_JOB).spawn() {
        Ok(c) => Ok(c),
        Err(_) => cmd.creation_flags(WINDOWS_START_FLAGS).spawn(),
    }
}

#[cfg(windows)]
fn start_windows(instance: &ServerInstance, since: SystemTime, progress: &mut dyn Progress) -> Result<ServerStatus> {
    let dir = instance.local_dir()?;
    let exe = dir.join(super::game::EXE);
    let (cap, out) = super::console::open_capture(instance, &[format!("{} in {} (no window)", exe.display(), dir.display())])?;
    let mut child = spawn_windowless(&exe, dir, &[], out).map_err(|e| Error::new(ErrorCode::Io, format!("starting {}: {e}", super::game::EXE)))?;
    progress.report(Some(0.02), &format!("started {} (pid {}), output in {}", super::game::EXE, child.id(), cap.display()));
    wait_ready(instance, since, progress, &mut || child.try_wait().ok().flatten().map(|st| format!("exited ({st}); see the Console")))
}

#[cfg(not(windows))]
fn start_windows(_instance: &ServerInstance, _since: SystemTime, _progress: &mut dyn Progress) -> Result<ServerStatus> {
    Err(Error::new(ErrorCode::Unsupported, "a Windows server runs on Windows"))
}

fn remote_kilnctl(instance: &ServerInstance, verb: &str) -> Result<String> {
    let r = instance.remote.as_ref().ok_or_else(|| Error::invalid("remote server without ssh details"))?;
    let name = r.instance.as_deref().ok_or_else(|| Error::new(ErrorCode::Unsupported, "this remote server has no kilnctl instance name"))?;
    super::instance::validate_name(name)?;
    super::remote::run(r, &["kilnctl", verb, name])
}

/// Players online (A2S, else the admin players op): a steam server's stop refuses while there are any.
fn players_online(instance: &ServerInstance) -> u32 {
    if let Ok(q) = super::query::query(&format!("127.0.0.1:{}", instance.query_port), Duration::from_millis(800)) {
        return q.players;
    }
    instance
        .dir
        .as_deref()
        .and_then(|d| crate::admin::client::AdminClient::connect_dir(d, &instance.id, crate::admin::client::Transport::Auto).ok())
        .and_then(|c| c.call("players", Default::default()).ok())
        .and_then(|v| v.as_array().map(|a| a.len() as u32))
        .unwrap_or(0)
}

/// Graceful stop (the game saves), then waits for the process to end.
pub fn stop(instance: &ServerInstance, progress: &mut dyn Progress) -> Result<ServerStatus> {
    if instance.runner == ServerRunner::Remote {
        remote_kilnctl(instance, "stop")?;
        return status(instance);
    }
    let Some(p) = pid(instance) else {
        progress.report(Some(1.0), &format!("{} is not running", instance.id));
        return status(instance);
    };
    let timeout = Duration::from_secs(instance.stop_timeout_s.clamp(10, 900));
    let save_before = save_dir(instance).ok().and_then(|d| super::game::newest_file(&d)).map(|(_, t, _)| t);
    match instance.runner {
        ServerRunner::Steam => {
            let n = players_online(instance);
            if n > 0 {
                return Err(Error::new(ErrorCode::Conflict, format!("{n} player(s) are online: {} is not stopped", instance.id)));
            }
            if cfg!(windows) {
                stop_windows(instance, p, timeout, progress)?;
            } else {
                sigint_and_wait(p, timeout, progress)?;
            }
        }
        ServerRunner::Kilnctl => stop_kilnctl(instance, p, timeout, progress)?,
        ServerRunner::Windows => stop_windows(instance, p, timeout, progress)?,
        ServerRunner::Remote => unreachable!(),
    }
    let saved = save_dir(instance).ok().and_then(|d| super::game::newest_file(&d)).is_some_and(|(_, t, _)| save_before.is_none_or(|b| t > b));
    progress.report(Some(1.0), &format!("{} stopped{}", instance.id, if saved { ", world saved" } else { "" }));
    status(instance)
}

fn sigint_and_wait(p: u32, timeout: Duration, progress: &mut dyn Progress) -> Result<()> {
    #[cfg(unix)]
    {
        super::procs::signal(p, libc::SIGINT).map_err(|e| Error::new(ErrorCode::Permission, format!("SIGINT to pid {p}: {e}")))?;
        progress.report(Some(0.2), &format!("SIGINT -> pid {p}; waiting up to {} s for the save", timeout.as_secs()));
        if !super::procs::wait_gone(p, timeout) {
            return Err(Error::new(ErrorCode::Timeout, format!("pid {p} still runs after {} s", timeout.as_secs())));
        }
        Ok(())
    }
    #[cfg(not(unix))]
    {
        let _ = (p, timeout, progress);
        Err(Error::new(ErrorCode::Unsupported, "SIGINT is a Unix feature"))
    }
}

#[cfg(all(unix, feature = "server"))]
fn stop_kilnctl(instance: &ServerInstance, p: u32, timeout: Duration, progress: &mut dyn Progress) -> Result<()> {
    let inst = instance.kilnctl_instance()?;
    let unit = inst.unit();
    let active = crate::hostenv::command("systemctl")
        .args(["--user", "is-active", "--quiet", &unit])
        .status()
        .is_ok_and(|s| s.success());
    if active {
        // through systemd, so Restart=on-failure does not bring it back
        progress.report(Some(0.2), &format!("systemctl --user stop {unit}"));
        crate::util::run_status(crate::hostenv::command("systemctl").args(["--user", "stop", &unit]), "systemctl --user stop")?;
        return Ok(());
    }
    let r = sigint_and_wait(p, timeout, progress);
    let runner = crate::wine::resolve(&inst.wine, &crate::util::home())?;
    if r.is_err() {
        let _ = super::procs::signal(p, libc::SIGKILL);
        let _ = runner.wineserver(&inst).arg("-k").status();
        return Err(Error::new(ErrorCode::Timeout, format!("{} did not stop in {} s: killed; the save may be incomplete", inst.name, timeout.as_secs())));
    }
    progress.report(Some(0.9), "waiting for wineserver");
    wineserver_wait(&runner, &inst, Duration::from_secs(30))
}

#[cfg(not(all(unix, feature = "server")))]
fn stop_kilnctl(_instance: &ServerInstance, _p: u32, _timeout: Duration, _progress: &mut dyn Progress) -> Result<()> {
    Err(Error::new(ErrorCode::Unsupported, "kilnctl instances run on Linux"))
}

/// Windows: Ctrl-C into the server's console (the game saves and exits, as on SIGINT under Wine; works without
/// Kiln.Admin); when that cannot be sent or the server is still there after the timeout: the admin save_now op, then
/// terminate.
fn stop_windows(instance: &ServerInstance, p: u32, timeout: Duration, progress: &mut dyn Progress) -> Result<()> {
    match ctrl_c(p) {
        Ok(()) => {
            progress.report(Some(0.2), &format!("Ctrl-C -> pid {p}; waiting up to {} s for the save", timeout.as_secs()));
            let t0 = Instant::now();
            while crate::proc::alive(p) && t0.elapsed() < timeout {
                std::thread::sleep(Duration::from_millis(200));
            }
            if !crate::proc::alive(p) {
                return Ok(());
            }
            progress.report(Some(0.6), &format!("pid {p} still runs after Ctrl-C"));
        }
        Err(e) => progress.report(Some(0.1), &format!("no Ctrl-C ({}); saving through Kiln.Admin", e.message)),
    }
    progress.report(Some(0.7), "saving the world (admin save_now)");
    let dir = instance.local_dir()?;
    let saved = crate::admin::client::AdminClient::connect_dir(dir, &instance.id, crate::admin::client::Transport::Auto)
        .and_then(|c| c.call("save_now", Default::default()));
    if let Err(e) = &saved {
        return Err(Error::new(ErrorCode::AdminUnavailable, format!("could not save before stopping ({}); stop it from its console instead", e.message)));
    }
    terminate(p)?;
    let t0 = Instant::now();
    while crate::proc::alive(p) {
        if t0.elapsed() > timeout {
            return Err(Error::new(ErrorCode::Timeout, format!("pid {p} still runs")));
        }
        std::thread::sleep(Duration::from_millis(200));
    }
    Ok(())
}

#[cfg(windows)]
fn terminate(pid: u32) -> Result<()> {
    use windows_sys::Win32::Foundation::CloseHandle;
    use windows_sys::Win32::System::Threading::{OpenProcess, TerminateProcess, PROCESS_TERMINATE};
    // SAFETY: plain Win32 calls on a handle we own.
    unsafe {
        let h = OpenProcess(PROCESS_TERMINATE, 0, pid);
        if h.is_null() {
            return Err(Error::new(ErrorCode::Permission, format!("cannot open pid {pid}")));
        }
        let ok = TerminateProcess(h, 0);
        CloseHandle(h);
        if ok == 0 {
            return Err(Error::new(ErrorCode::Permission, format!("cannot terminate pid {pid}")));
        }
    }
    Ok(())
}

#[cfg(not(windows))]
fn terminate(pid: u32) -> Result<()> {
    let _ = pid;
    Err(Error::new(ErrorCode::Unsupported, "terminate is for Windows servers"))
}

// kernel32's console calls (the crate's windows-sys features have no Win32_System_Console).
#[cfg(windows)]
#[link(name = "kernel32")]
unsafe extern "system" {
    fn AttachConsole(pid: u32) -> i32;
    fn FreeConsole() -> i32;
    fn GenerateConsoleCtrlEvent(event: u32, group: u32) -> i32;
    fn SetConsoleCtrlHandler(handler: Option<unsafe extern "system" fn(u32) -> i32>, add: i32) -> i32;
    fn GetConsoleCP() -> u32;
    fn GetStdHandle(which: u32) -> *mut core::ffi::c_void;
    fn SetStdHandle(which: u32, handle: *mut core::ffi::c_void) -> i32;
}

/// Until when (ms since the epoch) `swallow_ctrl_c` eats a Ctrl-C: ctrl_c is attached to the server's console when it
/// sends one, and the event reaches this process too (the default handler would end it). A handler, not
/// `SetConsoleCtrlHandler(NULL, TRUE)`: that ignore flag is inherited by every process started meanwhile, and a
/// server started by another task in that moment would never react to Ctrl-C.
#[cfg(windows)]
static SWALLOW_UNTIL: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);

#[cfg(windows)]
fn now_ms() -> u64 {
    SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis() as u64).unwrap_or(0)
}

#[cfg(windows)]
unsafe extern "system" fn swallow_ctrl_c(event: u32) -> i32 {
    (event == 0 && now_ms() < SWALLOW_UNTIL.load(std::sync::atomic::Ordering::SeqCst)) as i32
}

/// The program that sends a Ctrl-C for this process: `<helper> --kiln-ctrl-c <pid>`. The launcher sets itself
/// (`set_ctrl_c_helper`), so it never attaches to a server's console: in-process, the Ctrl-C it sends also reached the
/// launcher, whose default handler ended it (exit 0xC000013A on every Stop of a Windows server, 0.2.0-dev.11).
pub const CTRL_C_HELPER_ARG: &str = "--kiln-ctrl-c";

#[cfg(windows)]
static CTRL_C_HELPER: std::sync::OnceLock<std::path::PathBuf> = std::sync::OnceLock::new();

/// Makes `ctrl_c` send through `exe --kiln-ctrl-c <pid>` (a GUI program calls this with its own path).
pub fn set_ctrl_c_helper(exe: std::path::PathBuf) {
    #[cfg(windows)]
    let _ = CTRL_C_HELPER.set(exe);
    #[cfg(not(windows))]
    let _ = exe;
}

/// The helper's whole job, in its own short-lived process: attach to `pid`'s console, ignore Ctrl-C here (this
/// process starts nothing, so the inherited ignore flag goes nowhere), signal the console, detach. Exit 0 = sent,
/// 2 = cannot attach, 3 = sending failed.
#[cfg(windows)]
pub fn ctrl_c_helper_main(pid: u32) -> i32 {
    const CTRL_C_EVENT: u32 = 0;
    // SAFETY: plain kernel32 calls in a process that does nothing else.
    unsafe {
        FreeConsole();
        if AttachConsole(pid) == 0 {
            return 2;
        }
        SetConsoleCtrlHandler(None, 1);
        let sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
        std::thread::sleep(Duration::from_millis(300));
        FreeConsole();
        if sent == 0 { 3 } else { 0 }
    }
}

#[cfg(not(windows))]
pub fn ctrl_c_helper_main(pid: u32) -> i32 {
    let _ = pid;
    1
}

#[cfg(windows)]
fn ctrl_c_via_helper(exe: &Path, pid: u32) -> Result<()> {
    use std::os::windows::process::CommandExt;
    const CREATE_NO_WINDOW: u32 = 0x0800_0000;
    let mut child = crate::hostenv::command(exe)
        .arg(CTRL_C_HELPER_ARG)
        .arg(pid.to_string())
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .map_err(|e| Error::new(ErrorCode::Io, format!("starting the Ctrl-C helper {}: {e}", exe.display())))?;
    let t0 = Instant::now();
    loop {
        if let Some(st) = child.try_wait().map_err(|e| Error::new(ErrorCode::Io, format!("Ctrl-C helper: {e}")))? {
            return match st.code() {
                Some(0) => Ok(()),
                Some(2) => Err(Error::new(ErrorCode::Unsupported, format!("cannot attach to the console of pid {pid}"))),
                c => Err(Error::new(ErrorCode::Io, format!("Ctrl-C to pid {pid} failed (helper exit {c:?})"))),
            };
        }
        if t0.elapsed() > Duration::from_secs(10) {
            let _ = child.kill();
            return Err(Error::new(ErrorCode::Timeout, "the Ctrl-C helper did not finish in 10 s"));
        }
        std::thread::sleep(Duration::from_millis(50));
    }
}

/// Sends Ctrl-C to the console of `pid` (a server started with CREATE_NO_WINDOW has its own). With a helper set
/// (`set_ctrl_c_helper`: the launcher), a helper process does it. Otherwise (kilnctl, a console program): attach to it, swallow
/// the Ctrl-C in this process (`swallow_ctrl_c`), signal the whole console (group 0), detach. One at a time: the console a process
/// is attached to is process-wide. The caller's standard handles are put back afterwards (AttachConsole rebinds them
/// to the attached console and FreeConsole closes those: a redirected stdout would go dead), and a caller that had a
/// console of its own re-attaches to its parent's (the launcher, a GUI program, has none).
#[cfg(windows)]
pub fn ctrl_c(pid: u32) -> Result<()> {
    if let Some(exe) = CTRL_C_HELPER.get() {
        return ctrl_c_via_helper(exe, pid);
    }
    static ONE: std::sync::Mutex<()> = std::sync::Mutex::new(());
    let _g = ONE.lock().unwrap_or_else(|e| e.into_inner());
    const CTRL_C_EVENT: u32 = 0;
    const STD: [u32; 3] = [-10i32 as u32, -11i32 as u32, -12i32 as u32]; // input, output, error
    const ATTACH_PARENT_PROCESS: u32 = u32::MAX;
    static HANDLER: std::sync::Once = std::sync::Once::new();
    use windows_sys::Win32::Foundation::{GENERIC_READ, GENERIC_WRITE, INVALID_HANDLE_VALUE};
    use windows_sys::Win32::Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, FILE_TYPE_CHAR, GetFileType, OPEN_EXISTING};
    // SAFETY: plain kernel32 calls; the standard handles are restored before returning; the handler is a static
    // function that only reads an atomic.
    unsafe {
        HANDLER.call_once(|| {
            SetConsoleCtrlHandler(Some(swallow_ctrl_c), 1);
        });
        let saved = STD.map(|w| GetStdHandle(w));
        // console handles die with FreeConsole: those are opened again on the console we go back to (CONIN$,
        // CONOUT$); redirected ones (files, pipes) are put back as they were
        let on_console = saved.map(|h| !h.is_null() && h != INVALID_HANDLE_VALUE && GetFileType(h) == FILE_TYPE_CHAR);
        let had_console = GetConsoleCP() != 0;
        let restore = |saved: &[*mut core::ffi::c_void; 3]| {
            let back = had_console && AttachConsole(ATTACH_PARENT_PROCESS) != 0;
            for ((w, h), console) in STD.iter().zip(saved).zip(on_console) {
                if console && back {
                    let name: Vec<u16> = if *w == STD[0] { "CONIN$" } else { "CONOUT$" }.encode_utf16().chain([0]).collect();
                    let fresh = CreateFileW(name.as_ptr(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        std::ptr::null(), OPEN_EXISTING, 0, std::ptr::null_mut());
                    if fresh != INVALID_HANDLE_VALUE {
                        SetStdHandle(*w, fresh);
                        continue;
                    }
                }
                SetStdHandle(*w, *h);
            }
        };
        FreeConsole();
        if AttachConsole(pid) == 0 {
            let err = std::io::Error::last_os_error();
            restore(&saved);
            return Err(Error::new(ErrorCode::Unsupported, format!("cannot attach to the console of pid {pid}: {err}")));
        }
        SWALLOW_UNTIL.store(now_ms() + 5_000, std::sync::atomic::Ordering::SeqCst);
        let sent = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
        let err = std::io::Error::last_os_error();
        // the event reaches every attached process on a new thread, this one too: stay attached a moment
        std::thread::sleep(Duration::from_millis(300));
        FreeConsole();
        restore(&saved);
        if sent == 0 {
            return Err(Error::new(ErrorCode::Io, format!("Ctrl-C to pid {pid} failed: {err}")));
        }
    }
    Ok(())
}

#[cfg(not(windows))]
pub fn ctrl_c(pid: u32) -> Result<()> {
    let _ = pid;
    Err(Error::new(ErrorCode::Unsupported, "Ctrl-C into a console is for Windows servers (SIGINT elsewhere)"))
}

pub fn restart(instance: &ServerInstance, progress: &mut dyn Progress) -> Result<ServerStatus> {
    stop(instance, &mut super::instance::Scaled { inner: progress, from: 0.0, to: 0.4 })?;
    start(instance, &mut super::instance::Scaled { inner: progress, from: 0.4, to: 1.0 })
}

/// `wineboot -i` + the null display driver, as `kilnctl prefix` does.
#[cfg(all(unix, feature = "server"))]
pub fn make_prefix(inst: &super::instance::Instance) -> Result<()> {
    let runner = crate::wine::resolve(&inst.wine, &crate::util::home())?;
    if let Some(parent) = inst.prefix.parent() {
        std::fs::create_dir_all(parent)?;
    }
    crate::util::run_status(headless(&mut runner.command(inst)).args(["wineboot", "-i"]).stdin(std::process::Stdio::null()), "wineboot -i")?;
    crate::util::run_status(
        headless(&mut runner.command(inst))
            .args(["reg", "add", "HKCU\\Software\\Wine\\Drivers", "/v", "Graphics", "/d", "null", "/f"])
            .stdin(std::process::Stdio::null()),
        "wine reg add (Graphics=null)",
    )?;
    wineserver_wait(&runner, inst, Duration::from_secs(60))
}

#[allow(dead_code)]
fn _unused(_: &Path) {}

// ---- the foreground runner (Unix only) -----------------------
// - `wine ./enshrouded_server.exe` in the server folder, own process group, stdin /dev/null.
// - SIGINT, SIGTERM and SIGHUP become one SIGINT to the server process (the game saves and exits on Ctrl-C).
// - After the launcher exits: wait for the server process, then `wineserver -w`; exit with the launcher's status.

#[cfg(unix)]
pub use unix_runner::*;

#[cfg(unix)]
mod unix_runner {
    use super::super::instance::Instance;
    use super::super::procs;
    use crate::util;
    use crate::wine::{self, Runner};
    use crate::{Error, ErrorCode, Result};
    use std::os::unix::process::{CommandExt, ExitStatusExt};
    use std::process::{Child, ExitStatus, Stdio};
    use std::sync::atomic::{AtomicI32, Ordering};
    use std::time::{Duration, Instant};

    static PENDING: AtomicI32 = AtomicI32::new(0);

    extern "C" fn on_signal(sig: libc::c_int) {
        PENDING.store(sig, Ordering::SeqCst);
    }

    fn install_handlers() {
        for sig in [libc::SIGINT, libc::SIGTERM, libc::SIGHUP] {
            // SAFETY: the handler only stores into an atomic, which is async-signal-safe.
            unsafe {
                let mut sa: libc::sigaction = std::mem::zeroed();
                sa.sa_sigaction = on_signal as extern "C" fn(libc::c_int) as libc::sighandler_t;
                libc::sigemptyset(&mut sa.sa_mask);
                sa.sa_flags = libc::SA_RESTART;
                libc::sigaction(sig, &sa, std::ptr::null_mut());
            }
        }
    }

    pub fn signame(sig: i32) -> String {
        match sig {
            libc::SIGINT => "SIGINT".into(),
            libc::SIGTERM => "SIGTERM".into(),
            libc::SIGHUP => "SIGHUP".into(),
            libc::SIGKILL => "SIGKILL".into(),
            n => format!("signal {n}"),
        }
    }

    /// Shell-style exit code: the code, or 128 + signal.
    pub fn exit_code(st: ExitStatus) -> i32 {
        st.code().or_else(|| st.signal().map(|s| 128 + s)).unwrap_or(1)
    }

    fn log(msg: impl AsRef<str>) {
        eprintln!("kilnctl: {}", msg.as_ref());
    }

    /// `wineserver -w` for the instance's prefix, bounded: on timeout `wineserver -k`.
    pub fn wineserver_wait(runner: &Runner, inst: &Instance, timeout: Duration) -> Result<()> {
        let mut child = runner
            .wineserver(inst)
            .arg("-w")
            .stdin(Stdio::null())
            .spawn()
            .map_err(|e| Error::new(ErrorCode::Io, format!("starting wineserver -w: {e}")))?;
        let t0 = Instant::now();
        loop {
            if child.try_wait()?.is_some() {
                return Ok(());
            }
            if t0.elapsed() >= timeout {
                let _ = child.kill();
                let _ = child.wait();
                let _ = runner.wineserver(inst).arg("-k").status();
                return Err(Error::new(
                    ErrorCode::Timeout,
                    format!("wineserver -w still waiting after {} s; ran wineserver -k", timeout.as_secs()),
                ));
            }
            std::thread::sleep(Duration::from_millis(200));
        }
    }

    fn spawn(runner: &Runner, inst: &Instance) -> Result<Child> {
        let mut cmd = runner.command(inst);
        super::headless(&mut cmd);
        cmd.arg(format!("./{}", super::super::game::EXE)).current_dir(&inst.dir).stdin(Stdio::null()).process_group(0);
        // SAFETY: only async-signal-safe calls between fork and exec. The launcher runs in its own process group so a
        // terminal's Ctrl-C reaches the server once (through us); SIGTTOU is ignored so Wine touching the terminal from
        // the background cannot stop it.
        unsafe {
            cmd.pre_exec(|| {
                libc::signal(libc::SIGTTOU, libc::SIG_IGN);
                Ok(())
            });
        }
        cmd.spawn().map_err(|e| Error::new(ErrorCode::Io, format!("starting {}: {e}", runner.wine.display())))
    }

    /// `kilnctl run`: the server in the foreground, the way systemd runs it (module docs). Returns the launcher's exit
    /// code.
    pub fn run_foreground(inst: &Instance) -> Result<i32> {
        let runner = wine::resolve(&inst.wine, &util::home())?;
        if let Some(p) = procs::find_server(&inst.prefix) {
            return Err(Error::new(ErrorCode::Conflict, format!("{} is already running (pid {p})", inst.name)));
        }
        if let Some(p) = procs::servers_in_dir(&inst.dir).first() {
            return Err(Error::new(ErrorCode::Conflict, format!("a server with another prefix runs in {} (pid {p})", inst.dir.display())));
        }
        if !inst.exe().exists() {
            return Err(Error::not_found(format!("{} missing (kilnctl install {})", inst.exe().display(), inst.name)));
        }
        std::fs::create_dir_all(inst.dir.join("logs")).ok();
        install_handlers();
        log(format!(
            "starting {}: {} ./{} in {} (prefix {}, UDP {}, wine {})",
            inst.name,
            runner.wine.display(),
            super::super::game::EXE,
            inst.dir.display(),
            inst.prefix.display(),
            inst.query_port,
            runner.label
        ));
        let mut child = spawn(&runner, inst)?;
        let t_start = Instant::now();
        let mut server: Option<u32> = None;
        let mut stop_at: Option<Instant> = None;
        let mut forwarded = false;
        let mut fallback = false;
        let status = loop {
            if let Some(st) = child.try_wait()? {
                break st;
            }
            if !server.is_some_and(procs::alive) {
                let found = procs::find_server(&inst.prefix);
                if let Some(p) = found
                    && server != Some(p)
                {
                    log(format!("server pid {p} ({:.1} s after launch)", t_start.elapsed().as_secs_f64()));
                }
                server = found.or(server);
            }
            let sig = PENDING.swap(0, Ordering::SeqCst);
            if sig != 0 {
                if stop_at.is_none() {
                    stop_at = Some(Instant::now());
                    log(format!("{} received: stopping {} (the server saves, then exits)", signame(sig), inst.name));
                } else {
                    log(format!("{} received again: already stopping, waiting for the save", signame(sig)));
                }
            }
            if let Some(t0) = stop_at
                && !forwarded
            {
                match server.filter(|&p| procs::alive(p)) {
                    Some(p) => {
                        match procs::signal(p, libc::SIGINT) {
                            Ok(()) => log(format!("SIGINT -> server pid {p}")),
                            Err(e) => log(format!("SIGINT -> server pid {p} failed: {e}")),
                        }
                        forwarded = true;
                    }
                    None if !fallback && t0.elapsed() > Duration::from_secs(10) => {
                        // No server process appeared (still booting Wine): interrupt the launcher instead.
                        log("no server process yet; SIGINT -> launcher process group");
                        // SAFETY: plain syscall; negative pid = process group.
                        unsafe { libc::kill(-(child.id() as libc::pid_t), libc::SIGINT) };
                        fallback = true;
                    }
                    None => {}
                }
            }
            std::thread::sleep(Duration::from_millis(200));
        };
        if let Some(p) = server
            && procs::alive(p)
        {
            log(format!("launcher exited; waiting up to {} s for server pid {p}", inst.stop_timeout.as_secs()));
            if !procs::wait_gone(p, inst.stop_timeout) {
                log(format!("server pid {p} still running; SIGKILL"));
                let _ = procs::signal(p, libc::SIGKILL);
            }
        }
        if let Err(e) = wineserver_wait(&runner, inst, Duration::from_secs(30)) {
            log(format!("{e:#}"));
        }
        let code = exit_code(status);
        log(format!(
            "{} exited after {} ({}{})",
            inst.name,
            util::human_duration(t_start.elapsed()),
            status,
            stop_at.map(|t| format!(", {:.1} s after the stop request", t.elapsed().as_secs_f64())).unwrap_or_default()
        ));
        Ok(code)
    }
}

#[cfg(test)]
mod installed_tests {
    use super::installed_kiln;

    #[test]
    fn kiln_in_a_server_folder_from_the_receipt_or_the_proxy() {
        let dir = std::env::temp_dir().join(format!("km-installed-kiln-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("kiln")).unwrap();
        assert_eq!(installed_kiln(&dir), None, "an empty folder");
        // another proxy is no Kiln
        let emf = crate::pe::build_test_dll(&["DirectInput8Create", "emf_loader_identity"], b"EMF loader");
        std::fs::write(dir.join("dinput8.dll"), emf).unwrap();
        assert_eq!(installed_kiln(&dir), None, "EMF's proxy");
        // a Kiln proxy put there by a deploy script, without a receipt
        let kiln = crate::pe::build_test_dll(&["DirectInput8Create", "KilnProxyInfo"], b"KilnProxy/2 0.2.0-dev+20261106\0");
        std::fs::write(dir.join("dinput8.dll"), kiln).unwrap();
        assert_eq!(installed_kiln(&dir).as_deref(), Some("0.2.0-dev+20261106"));
        // the installer's receipt wins
        let receipt = serde_json::json!({
            "format": 1, "installId": "4b8e2c1a-0000-4000-8000-000000000001", "installed": "2026-11-06T12:00:00Z",
            "runtime": { "version": "0.2.1+20261106.0d9662b", "components": ["core", "admin"] }, "side": "server", "files": [],
        });
        std::fs::write(dir.join("kiln/.install.json"), receipt.to_string()).unwrap();
        assert_eq!(installed_kiln(&dir).as_deref(), Some("0.2.1+20261106.0d9662b"));
        let _ = std::fs::remove_dir_all(&dir);
    }
}
