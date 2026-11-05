//! /proc: finding an instance's server process, UDP ports, signals. Linux only; elsewhere the lookups find nothing
//! (Windows servers: crate::proc).
//!
//! `wine ./enshrouded_server.exe` runs `start.exe`, which spawns the server as a separate process that is reparented
//! to the nearest subreaper (systemd). The server is found by its name and the `WINEPREFIX` in its environment; every
//! instance has its own prefix, so this never matches another server (the live server runs in a Proton prefix).
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

/// The kernel truncates the process name to 15 bytes.
pub const COMM: &str = "enshrouded_serv";

pub fn pids() -> Vec<u32> {
    fs::read_dir("/proc")
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| e.file_name().to_str()?.parse().ok())
        .collect()
}

/// Value of `key` in a NUL-separated environment block.
pub fn parse_environ<'a>(data: &'a [u8], key: &str) -> Option<&'a [u8]> {
    data.split(|&b| b == 0).find_map(|kv| kv.strip_prefix(key.as_bytes())?.strip_prefix(b"="))
}

fn canon(p: &Path) -> PathBuf {
    p.canonicalize().unwrap_or_else(|_| crate::util::clean(p))
}

#[cfg(unix)]
fn os_path(v: &[u8]) -> PathBuf {
    use std::os::unix::ffi::OsStrExt;
    PathBuf::from(std::ffi::OsStr::from_bytes(v))
}

#[cfg(not(unix))]
fn os_path(v: &[u8]) -> PathBuf {
    PathBuf::from(String::from_utf8_lossy(v).into_owned())
}

pub use crate::proc::parse_stat_state;

/// Running (not a zombie).
pub fn alive(pid: u32) -> bool {
    crate::proc::alive(pid)
}

fn is_server(pid: u32) -> bool {
    let comm = fs::read_to_string(format!("/proc/{pid}/comm")).unwrap_or_default();
    if comm.trim_end() == COMM {
        return true;
    }
    let cmd = fs::read(format!("/proc/{pid}/cmdline")).unwrap_or_default();
    let argv0 = cmd.split(|&b| b == 0).next().unwrap_or_default();
    String::from_utf8_lossy(argv0).to_ascii_lowercase().ends_with("enshrouded_server.exe")
}

/// Server processes running in `prefix`.
pub fn find_servers(prefix: &Path) -> Vec<u32> {
    let want = canon(prefix);
    let mut v: Vec<u32> = pids()
        .into_iter()
        .filter(|&p| is_server(p))
        .filter(|&p| {
            let Ok(env) = fs::read(format!("/proc/{p}/environ")) else { return false };
            parse_environ(&env, "WINEPREFIX").is_some_and(|v| canon(&os_path(v)) == want)
        })
        .filter(|&p| alive(p))
        .collect();
    v.sort();
    v
}

pub fn find_server(prefix: &Path) -> Option<u32> {
    find_servers(prefix).into_iter().next()
}

/// Server processes whose working folder is `dir`, whatever their prefix (another instance or a manual start).
pub fn servers_in_dir(dir: &Path) -> Vec<u32> {
    let want = canon(dir);
    pids()
        .into_iter()
        .filter(|&p| is_server(p) && alive(p))
        .filter(|&p| fs::read_link(format!("/proc/{p}/cwd")).is_ok_and(|c| c == want))
        .collect()
}

#[cfg(unix)]
pub fn signal(pid: u32, sig: i32) -> std::io::Result<()> {
    // SAFETY: plain syscall.
    if unsafe { libc::kill(pid as libc::pid_t, sig) } == 0 { Ok(()) } else { Err(std::io::Error::last_os_error()) }
}

#[cfg(not(unix))]
pub fn signal(_pid: u32, _sig: i32) -> std::io::Result<()> {
    Err(std::io::Error::new(std::io::ErrorKind::Unsupported, "signals are a Unix feature"))
}

/// Polls until `pid` is gone; false on timeout.
pub fn wait_gone(pid: u32, timeout: Duration) -> bool {
    let t0 = Instant::now();
    while alive(pid) {
        if t0.elapsed() >= timeout {
            return false;
        }
        std::thread::sleep(Duration::from_millis(250));
    }
    true
}

/// Socket inodes bound to local UDP `port` in a /proc/net/udp{,6} table.
pub fn parse_udp_table(text: &str, port: u16) -> Vec<u64> {
    text.lines()
        .skip(1)
        .filter_map(|l| {
            let f: Vec<&str> = l.split_whitespace().collect();
            let (_, p) = f.get(1)?.rsplit_once(':')?;
            (u16::from_str_radix(p, 16).ok()? == port).then_some(())?;
            f.get(9)?.parse().ok()
        })
        .collect()
}

pub fn udp_inodes(port: u16) -> Vec<u64> {
    ["/proc/net/udp", "/proc/net/udp6"]
        .iter()
        .filter_map(|t| fs::read_to_string(t).ok())
        .flat_map(|t| parse_udp_table(&t, port))
        .collect()
}

/// Own process holding a socket inode: (pid, name).
pub fn socket_owner(inode: u64) -> Option<(u32, String)> {
    let want = format!("socket:[{inode}]");
    for pid in pids() {
        let Ok(rd) = fs::read_dir(format!("/proc/{pid}/fd")) else { continue };
        for fd in rd.flatten() {
            if fs::read_link(fd.path()).is_ok_and(|l| l.as_os_str() == want.as_str()) {
                let comm = fs::read_to_string(format!("/proc/{pid}/comm")).unwrap_or_default();
                return Some((pid, comm.trim_end().to_string()));
            }
        }
    }
    None
}

/// Whether the process has /dev/ntsync open (Wine uses it for sync objects).
pub fn uses_ntsync(pid: u32) -> bool {
    fs::read_dir(format!("/proc/{pid}/fd"))
        .into_iter()
        .flatten()
        .flatten()
        .any(|fd| fs::read_link(fd.path()).is_ok_and(|l| l == Path::new("/dev/ntsync")))
}

/// Seconds since the process started.
#[cfg(unix)]
pub fn uptime(pid: u32) -> Option<Duration> {
    let stat = fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
    let fields: Vec<&str> = stat.rsplit_once(')')?.1.split_whitespace().collect();
    let start: f64 = fields.get(19)?.parse().ok()?; // field 22 (starttime), counted from field 3 (state)
    let up: f64 = fs::read_to_string("/proc/uptime").ok()?.split_whitespace().next()?.parse().ok()?;
    // SAFETY: plain libc query.
    let hz = unsafe { libc::sysconf(libc::_SC_CLK_TCK) } as f64;
    (hz > 0.0).then(|| Duration::from_secs_f64((up - start / hz).max(0.0)))
}

#[cfg(not(unix))]
pub fn uptime(_pid: u32) -> Option<Duration> {
    None
}
