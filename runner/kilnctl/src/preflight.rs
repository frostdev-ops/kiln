// SPDX-License-Identifier: MIT
//! `kilnctl preflight`: everything that must hold before `run` (systemd runs it as ExecStartPre).
use crate::config::{self, Instance};
use crate::util::{self, human_size};
use crate::{kiln, procs, server, wine};
use std::fs::OpenOptions;
#[cfg(unix)]
use std::os::unix::fs::FileTypeExt;
use std::path::Path;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Level {
    Ok,
    Warn,
    Fail,
}

#[derive(Debug, Clone)]
pub struct Check {
    pub level: Level,
    pub what: &'static str,
    pub msg: String,
}

impl Check {
    pub fn ok(what: &'static str, msg: impl Into<String>) -> Self {
        Check { level: Level::Ok, what, msg: msg.into() }
    }
    pub fn warn(what: &'static str, msg: impl Into<String>) -> Self {
        Check { level: Level::Warn, what, msg: msg.into() }
    }
    pub fn fail(what: &'static str, msg: impl Into<String>) -> Self {
        Check { level: Level::Fail, what, msg: msg.into() }
    }
}

pub fn print(checks: &[Check]) {
    for c in checks {
        let tag = match c.level {
            Level::Ok => "  ok ",
            Level::Warn => " warn",
            Level::Fail => " FAIL",
        };
        println!("{tag}  {:<8} {}", c.what, c.msg);
    }
}

pub fn failed(checks: &[Check]) -> bool {
    checks.iter().any(|c| c.level == Level::Fail)
}

/// Opens /dev/ntsync read-write (what Wine does) and closes it again.
pub fn ntsync_check(path: &Path) -> Result<(), String> {
    let md = std::fs::metadata(path).map_err(|e| format!("{} missing ({e}): needs kernel 6.14+ with CONFIG_NTSYNC (modprobe ntsync)", path.display()))?;
    #[cfg(unix)]
    if !md.file_type().is_char_device() {
        return Err(format!("{} is not a character device", path.display()));
    }
    #[cfg(not(unix))]
    let _ = md;
    OpenOptions::new().read(true).write(true).open(path).map(drop).map_err(|e| {
        format!("{} not usable ({e}): Wine needs read-write access (udev rule KERNEL==\"ntsync\", MODE=\"0666\")", path.display())
    })
}

/// A prefix `kilnctl prefix` finished: registry, system32, the null graphics driver.
pub fn prefix_check(prefix: &Path) -> Result<String, String> {
    if !prefix.is_dir() {
        return Err(format!("{} does not exist", prefix.display()));
    }
    for f in ["system.reg", "user.reg", "drive_c/windows/system32"] {
        if !prefix.join(f).exists() {
            return Err(format!("{} has no {f} (wineboot did not finish)", prefix.display()));
        }
    }
    let user = std::fs::read(prefix.join("user.reg")).map_err(|e| format!("reading user.reg: {e}"))?;
    if !graphics_null(&String::from_utf8_lossy(&user)) {
        return Err(format!("{}: Graphics is not \"null\" in HKCU\\Software\\Wine\\Drivers", prefix.display()));
    }
    Ok(format!("{} (Graphics=null)", prefix.display()))
}

/// `[Software\\Wine\\Drivers]` has `"Graphics"="null"` in a user.reg.
pub fn graphics_null(user_reg: &str) -> bool {
    let mut in_drivers = false;
    for l in user_reg.lines() {
        let l = l.trim();
        if l.starts_with('[') {
            in_drivers = l.to_ascii_lowercase().starts_with("[software\\\\wine\\\\drivers]");
        } else if in_drivers && l.eq_ignore_ascii_case("\"Graphics\"=\"null\"") {
            return true;
        }
    }
    false
}

fn exe_check(inst: &Instance) -> Check {
    let exe = inst.exe();
    let mut head = [0u8; 2];
    let ok = std::fs::File::open(&exe).and_then(|mut f| std::io::Read::read_exact(&mut f, &mut head));
    match (ok, exe.metadata()) {
        (Ok(()), Ok(md)) if &head == b"MZ" => Check::ok("exe", format!("{} ({})", exe.display(), human_size(md.len()))),
        (Ok(()), _) => Check::fail("exe", format!("{} is not a Windows executable", exe.display())),
        _ => Check::fail("exe", format!("{} missing (kilnctl install {})", exe.display(), inst.name)),
    }
}

fn port_check(inst: &Instance, out: &mut Vec<Check>) {
    let port = inst.query_port;
    let inodes = procs::udp_inodes(port);
    if let Some(&ino) = inodes.first() {
        let who = procs::socket_owner(ino).map(|(p, c)| format!(" by pid {p} ({c})")).unwrap_or_default();
        out.push(Check::fail("port", format!("UDP {port} is in use{who}")));
        return;
    }
    match std::net::UdpSocket::bind(("0.0.0.0", port)) {
        Ok(_) => out.push(Check::ok("port", format!("UDP {port} free"))),
        Err(e) => {
            out.push(Check::fail("port", format!("UDP {port} cannot be bound: {e}")));
            return;
        }
    }
    let others: Vec<String> = config::list()
        .into_iter()
        .filter(|n| *n != inst.name)
        .filter(|n| config::load(n).is_ok_and(|o| o.query_port == port))
        .collect();
    if !others.is_empty() {
        out.push(Check::warn("port", format!("instance(s) {} use UDP {port} too", others.join(", "))));
    }
}

fn server_json_check(inst: &Instance, out: &mut Vec<Check>) {
    let path = inst.server_json();
    if !path.exists() {
        out.push(Check::fail(
            "config",
            format!("{} missing: without it the game starts on UDP 15637 (kilnctl install {} writes one)", path.display(), inst.name),
        ));
        return;
    }
    let c = match server::read_config(&path) {
        Ok(c) => c,
        Err(e) => {
            out.push(Check::fail("config", format!("{e:#}")));
            return;
        }
    };
    if c.query_port != inst.query_port {
        out.push(Check::fail(
            "config",
            format!("queryPort {} in {}, but the instance says {} ({})", c.query_port, server::CONFIG, inst.query_port, inst.file.display()),
        ));
        return;
    }
    let save = server::host_path(&inst.dir, &inst.prefix, &c.save_dir);
    if let Some(parent) = save.parent()
        && !parent.is_dir()
    {
        out.push(Check::fail("config", format!("saveDirectory {} has no parent folder", save.display())));
        return;
    }
    out.push(Check::ok("config", format!("{} valid: {:?}, queryPort {}, save {}", server::CONFIG, c.name, c.query_port, c.save_dir)));
    if let Some(n) = c.slot_count
        && !(1..=16).contains(&n)
    {
        out.push(Check::warn("config", format!("slotCount {n}: the game allows 1-16")));
    }
}

pub fn run(inst: &Instance) -> Vec<Check> {
    let mut out = vec![Check::ok("instance", format!("{} ({})", inst.name, inst.file.display()))];
    match wine::resolve(&inst.wine, &util::home()) {
        Ok(r) => out.push(Check::ok("wine", format!("{} -> {}", r.label, r.wine.display()))),
        Err(e) => out.push(Check::fail("wine", format!("{e:#}"))),
    }
    match ntsync_check(Path::new("/dev/ntsync")) {
        Ok(()) => out.push(Check::ok("ntsync", "/dev/ntsync present and opens read-write")),
        Err(e) if inst.require_ntsync => out.push(Check::fail("ntsync", e)),
        Err(e) => out.push(Check::warn("ntsync", format!("{e} (require_ntsync = false)"))),
    }
    let exe = exe_check(inst);
    let have_exe = exe.level == Level::Ok;
    out.push(exe);
    match prefix_check(&inst.prefix) {
        Ok(m) => out.push(Check::ok("prefix", m)),
        Err(e) => out.push(Check::fail("prefix", format!("{e} (kilnctl prefix {})", inst.name))),
    }
    let pids = |v: &[u32]| v.iter().map(u32::to_string).collect::<Vec<_>>().join(", ");
    match (procs::find_servers(&inst.prefix).as_slice(), procs::servers_in_dir(&inst.dir).as_slice()) {
        ([], []) => out.push(Check::ok("process", "no server running in this prefix or folder")),
        ([], other) => out.push(Check::fail("process", format!("a server with another prefix runs in this folder (pid {})", pids(other)))),
        (p, _) => out.push(Check::fail("process", format!("already running (pid {})", pids(p)))),
    }
    port_check(inst, &mut out);
    server_json_check(inst, &mut out);
    let overrides = wine::effective(inst, "WINEDLLOVERRIDES").unwrap_or_default();
    match wine::check_mscoree(&overrides) {
        Ok(()) => out.push(Check::ok("env", format!("WINEDLLOVERRIDES={overrides} (mscoree builtin)"))),
        Err(e) => out.push(Check::fail("env", format!("{e:#}"))),
    }
    let sha = if have_exe && kiln::is_deployed(inst) { util::sha256(&inst.exe()).ok() } else { None };
    kiln::checks(inst, sha.as_deref(), &mut out);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn graphics_driver() {
        let reg = "WINE REGISTRY Version 2\n\n[Software\\\\Wine\\\\Drivers] 1793756357\n#time=1dc4\n\"Graphics\"=\"null\"\n\n[Software\\\\Wine\\\\Fonts] 1\n";
        assert!(graphics_null(reg));
        assert!(!graphics_null(&reg.replace("\"null\"", "\"x11\"")));
        let elsewhere = "[Software\\\\Other] 1\n\"Graphics\"=\"null\"\n[Software\\\\Wine\\\\Drivers] 1\n\"Audio\"=\"\"\n";
        assert!(!graphics_null(elsewhere));
        assert!(!graphics_null(""));
    }

    #[test]
    fn ntsync_missing() {
        assert!(ntsync_check(Path::new("/nonexistent/ntsync")).is_err());
        assert!(ntsync_check(Path::new("/proc/self/status")).is_err()); // not a char device
    }
}
