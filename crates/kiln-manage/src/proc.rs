//! Running processes: Linux /proc (Wine/Proton games show as `enshrouded.exe` in comm, their cwd is the game folder,
//! argv[0] is the Windows path of the exe), Windows Toolhelp + QueryFullProcessImageNameW.

use crate::Result;
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ProcessInfo {
    /// Host pid (Linux pid of the wine process; Windows pid).
    pub pid: u32,
    pub name: String,
    pub exe: Option<PathBuf>,
    pub cmdline: Vec<String>,
}

/// Linux: the extra facts `find_game` matches on (cwd), kept out of the serialized ProcessInfo.
#[derive(Debug, Clone, Default)]
struct Extra {
    cwd: Option<PathBuf>,
}

pub fn list() -> Result<Vec<ProcessInfo>> {
    Ok(list_with_extra()?.into_iter().map(|(p, _)| p).collect())
}

/// The running process of this install's exe, if any (matched by full path, not by name alone).
pub fn find_game(install: &crate::game::GameInstall) -> Result<Option<ProcessInfo>> {
    find_exe(&install.path, &install.exe)
}

/// The running process of `<dir>/<exe>` (`exe` is a file name such as `enshrouded.exe`).
pub fn find_exe(dir: &Path, exe: &str) -> Result<Option<ProcessInfo>> {
    Finder::default().find(dir, exe)
}

/// `pid` when it still is the running process of `<dir>/<exe>` (as [`find_exe`] matches it), without listing every
/// process: for a watcher that found the game once and checks every second that it still runs. None: it ended (or the
/// pid is another process now); look again with [`find_exe`].
pub fn still_running(pid: u32, dir: &Path, exe: &str) -> Option<ProcessInfo> {
    #[cfg(not(windows))]
    {
        let l = imp::light(pid)?;
        let probe = ProcessInfo { pid, name: l.name.clone(), exe: None, cmdline: l.cmdline.clone() };
        if !name_matches(&probe, exe) {
            return None;
        }
        let (p, extra) = imp::full(&l)?;
        let want_dir = canon(dir);
        matches_exe(&p, &extra, &want_dir, &want_dir.join(exe), exe).then_some(p)
    }
    #[cfg(windows)]
    {
        find_exe(dir, exe).ok().flatten().filter(|p| p.pid == pid)
    }
}

/// [`find_exe`] for several folders and exe names from one look at the process table: Linux lists /proc once, Windows
/// once per exe name. For one decision over many folders (the Library's removal guard); the listing is not refreshed.
#[derive(Default)]
pub struct Finder {
    all: Option<Vec<(ProcessInfo, Extra)>>,
    named: std::collections::HashMap<String, Vec<(ProcessInfo, Extra)>>,
    /// Linux: every process's name and command line, read once; the rest only for name matches (`find_light`).
    #[cfg(not(windows))]
    light: Option<Vec<imp::Light>>,
}

impl Finder {
    pub fn find(&mut self, dir: &Path, exe: &str) -> Result<Option<ProcessInfo>> {
        #[cfg(not(windows))]
        {
            return self.find_light(dir, exe);
        }
        #[cfg(windows)]
        self.find_listed(dir, exe)
    }

    /// Linux: what `find_listed` answers, from fewer reads: a process can only match by its name (comm) or argv[0], so
    /// its state, exe and cwd are read only for those (the watcher looks every second; /proc has hundreds of
    /// processes, and five reads each was most of a status poll).
    #[cfg(not(windows))]
    fn find_light(&mut self, dir: &Path, exe: &str) -> Result<Option<ProcessInfo>> {
        let light = match &mut self.light {
            Some(l) => l,
            None => self.light.insert(imp::list_light()),
        };
        let want_dir = canon(dir);
        let want_exe = want_dir.join(exe);
        for l in light.iter() {
            let probe = ProcessInfo { pid: l.pid, name: l.name.clone(), exe: None, cmdline: l.cmdline.clone() };
            if !name_matches(&probe, exe) {
                continue;
            }
            let Some((p, extra)) = imp::full(l) else { continue };
            if matches_exe(&p, &extra, &want_dir, &want_exe, exe) {
                return Ok(Some(p));
            }
        }
        Ok(None)
    }

    #[cfg_attr(not(windows), allow(dead_code))]
    fn find_listed(&mut self, dir: &Path, exe: &str) -> Result<Option<ProcessInfo>> {
        // Windows: only processes with the exe's image name get their full image path looked up (the launcher's
        // watcher calls find_exe every second; opening and canonicalizing every process each time is wasteful)
        let candidates: &[(ProcessInfo, Extra)] = if cfg!(windows) {
            match self.named.entry(exe.to_ascii_lowercase()) {
                std::collections::hash_map::Entry::Occupied(e) => e.into_mut(),
                std::collections::hash_map::Entry::Vacant(e) => e.insert(imp::list_named(exe)?),
            }
        } else {
            match &mut self.all {
                Some(all) => all,
                None => self.all.insert(list_with_extra()?),
            }
        };
        let want_dir = canon(dir);
        let want_exe = want_dir.join(exe);
        Ok(candidates.iter()
            .filter(|(p, extra)| matches_exe(p, extra, &want_dir, &want_exe, exe))
            .min_by_key(|(p, _)| p.pid)
            .map(|(p, _)| p.clone()))
    }
}

fn canon(p: &Path) -> PathBuf {
    std::fs::canonicalize(p).unwrap_or_else(|_| p.components().collect())
}

/// Linux/Wine: the kernel truncates comm to 15 bytes (`enshrouded_server.exe` -> `enshrouded_serv`).
fn comm_of(exe: &str) -> String {
    exe.bytes().take(15).map(char::from).collect()
}

fn eq_path(a: &Path, b: &Path) -> bool {
    crate::steam::same_path(a, b)
}

fn matches_exe(p: &ProcessInfo, extra: &Extra, want_dir: &Path, want_exe: &Path, exe: &str) -> bool {
    if cfg!(windows) {
        return p.exe.as_deref().is_some_and(|e| eq_path(&canon(e), want_exe));
    }
    // Wine/Proton: the game process is named after the exe; argv[0] is its Windows path (Z:\... or C:\...), cwd the
    // game folder. A name match alone is not enough (another install of the same game may run).
    if !name_matches(p, exe) {
        return false;
    }
    if let Some(a0) = p.cmdline.first()
        && let Some(host) = crate::winpath::z_to_unix(a0)
        && eq_path(&canon(&host), want_exe)
    {
        return true;
    }
    extra.cwd.as_deref().is_some_and(|c| eq_path(&canon(c), want_dir))
}

/// Linux/Wine: the process is named after the exe (comm), or its argv[0] is the exe's path.
fn name_matches(p: &ProcessInfo, exe: &str) -> bool {
    p.name.eq_ignore_ascii_case(&comm_of(exe)) || p.cmdline.first().is_some_and(|a| win_basename(a).eq_ignore_ascii_case(exe))
}

fn win_basename(s: &str) -> &str {
    s.rsplit(['\\', '/']).next().unwrap_or(s)
}

pub fn alive(pid: u32) -> bool {
    imp::alive(pid)
}

/// When the process started (wall clock), None when it is gone or unreadable. Linux: `/proc/<pid>/stat` starttime after
/// `/proc/stat` btime (a whole second's precision, the same value on every read); Windows: the creation time of
/// GetProcessTimes. Under Wine/Proton this is the host process: the game's real start, whoever started it.
pub fn start_time(pid: u32) -> Option<std::time::SystemTime> {
    imp::start_time(pid)
}

/// `start_time` as RFC 3339 UTC (`2026-11-06T13:05:09Z`).
pub fn start_rfc3339(pid: u32) -> Option<String> {
    start_time(pid).map(|t| crate::util::rfc3339(crate::util::unix(t)))
}

/// Linux: the start from `/proc/<pid>/stat` (field 22, starttime, in clock ticks after boot) and `/proc/stat`'s btime.
pub fn parse_start(stat: &str, proc_stat: &str, ticks_per_s: u64) -> Option<std::time::SystemTime> {
    let ticks: u64 = stat.rsplit_once(')')?.1.split_whitespace().nth(19)?.parse().ok()?;
    let btime: u64 = proc_stat.lines().find_map(|l| l.strip_prefix("btime ")?.trim().parse().ok())?;
    let hz = ticks_per_s.max(1);
    let since_boot = std::time::Duration::from_secs(ticks / hz) + std::time::Duration::from_nanos((ticks % hz) * 1_000_000_000 / hz);
    Some(std::time::UNIX_EPOCH + std::time::Duration::from_secs(btime) + since_boot)
}

/// Windows FILETIME (100 ns units since 1601-01-01) as a SystemTime; None before 1970.
pub fn filetime_to_system(ticks: u64) -> Option<std::time::SystemTime> {
    const UNIX_EPOCH_AS_FILETIME: u64 = 116_444_736_000_000_000;
    let since_unix = ticks.checked_sub(UNIX_EPOCH_AS_FILETIME)?;
    Some(std::time::UNIX_EPOCH + std::time::Duration::from_nanos(since_unix.checked_mul(100)?))
}

fn list_with_extra() -> Result<Vec<(ProcessInfo, Extra)>> {
    imp::list()
}

/// Process state letter from `/proc/<pid>/stat` (`R`, `S`, `Z`, ...).
pub fn parse_stat_state(stat: &str) -> Option<char> {
    stat.rsplit_once(')')?.1.trim_start().chars().next()
}

#[cfg(not(windows))]
mod imp {
    use super::{Extra, ProcessInfo, parse_stat_state};
    use crate::Result;
    use std::fs;

    pub fn pids() -> Vec<u32> {
        fs::read_dir("/proc")
            .into_iter()
            .flatten()
            .flatten()
            .filter_map(|e| e.file_name().to_str()?.parse().ok())
            .collect()
    }

    pub fn start_time(pid: u32) -> Option<std::time::SystemTime> {
        let stat = fs::read_to_string(format!("/proc/{pid}/stat")).ok()?;
        let proc_stat = fs::read_to_string("/proc/stat").ok()?;
        super::parse_start(&stat, &proc_stat, clock_ticks())
    }

    /// USER_HZ (100 on every common Linux).
    fn clock_ticks() -> u64 {
        #[cfg(unix)]
        {
            // SAFETY: plain libc query.
            let hz = unsafe { libc::sysconf(libc::_SC_CLK_TCK) };
            if hz > 0 {
                return hz as u64;
            }
        }
        100
    }

    pub fn alive(pid: u32) -> bool {
        match fs::read_to_string(format!("/proc/{pid}/stat")) {
            Ok(s) => !matches!(parse_stat_state(&s), Some('Z' | 'X') | None),
            Err(_) => false,
        }
    }

    /// Same as `list` (the name filter is a Windows optimisation; Wine processes are matched on argv[0] and cwd).
    #[allow(dead_code)]
    pub fn list_named(_exe: &str) -> Result<Vec<(ProcessInfo, Extra)>> {
        list()
    }

    fn cmdline(pid: u32) -> Vec<String> {
        let cmd = fs::read(format!("/proc/{pid}/cmdline")).unwrap_or_default();
        cmd.split(|&b| b == 0).filter(|a| !a.is_empty()).map(|a| String::from_utf8_lossy(a).into_owned()).collect()
    }

    pub fn list() -> Result<Vec<(ProcessInfo, Extra)>> {
        let mut out = Vec::new();
        for pid in pids() {
            let Ok(comm) = fs::read_to_string(format!("/proc/{pid}/comm")) else { continue };
            if !alive(pid) {
                continue;
            }
            let cmdline = cmdline(pid);
            let exe = fs::read_link(format!("/proc/{pid}/exe")).ok();
            let cwd = fs::read_link(format!("/proc/{pid}/cwd")).ok();
            out.push((ProcessInfo { pid, name: comm.trim_end().to_string(), exe, cmdline }, Extra { cwd }));
        }
        out.sort_by_key(|(p, _)| p.pid);
        Ok(out)
    }

    /// A process as far as name matching needs it (comm, argv).
    pub struct Light {
        pub pid: u32,
        pub name: String,
        pub cmdline: Vec<String>,
    }

    pub fn light(pid: u32) -> Option<Light> {
        let comm = fs::read_to_string(format!("/proc/{pid}/comm")).ok()?;
        Some(Light { pid, name: comm.trim_end().to_string(), cmdline: cmdline(pid) })
    }

    /// Every process's pid, comm and argv, by pid (two reads each; `list` does five).
    pub fn list_light() -> Vec<Light> {
        let mut out: Vec<Light> = pids().into_iter().filter_map(light).collect();
        out.sort_by_key(|l| l.pid);
        out
    }

    /// The rest of `list`'s entry for one process (None: it ended or is a zombie, which `list` leaves out).
    pub fn full(l: &Light) -> Option<(ProcessInfo, Extra)> {
        if !alive(l.pid) {
            return None;
        }
        let exe = fs::read_link(format!("/proc/{}/exe", l.pid)).ok();
        let cwd = fs::read_link(format!("/proc/{}/cwd", l.pid)).ok();
        Some((ProcessInfo { pid: l.pid, name: l.name.clone(), exe, cmdline: l.cmdline.clone() }, Extra { cwd }))
    }
}

#[cfg(windows)]
mod imp {
    use super::{Extra, ProcessInfo};
    use crate::Result;
    use std::path::PathBuf;
    use windows_sys::Win32::Foundation::{CloseHandle, FILETIME, INVALID_HANDLE_VALUE, STILL_ACTIVE, WAIT_TIMEOUT};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
    };
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, GetProcessTimes, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
        PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW, WaitForSingleObject,
    };

    fn wide_to_string(w: &[u16]) -> String {
        let n = w.iter().position(|&c| c == 0).unwrap_or(w.len());
        String::from_utf16_lossy(&w[..n])
    }

    fn image_path(pid: u32) -> Option<PathBuf> {
        // SAFETY: plain Win32 calls; the handle is closed on every path.
        unsafe {
            let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if h.is_null() {
                return None;
            }
            let mut buf = vec![0u16; 32768];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, buf.as_mut_ptr(), &mut len);
            CloseHandle(h);
            (ok != 0).then(|| PathBuf::from(String::from_utf16_lossy(&buf[..len as usize])))
        }
    }

    /// The creation time of GetProcessTimes (PROCESS_QUERY_LIMITED_INFORMATION is enough, also for elevated games).
    pub fn start_time(pid: u32) -> Option<std::time::SystemTime> {
        // SAFETY: plain Win32 calls on a handle we own and close; zeroed out-structs.
        let created = unsafe {
            let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if h.is_null() {
                return None;
            }
            let (mut c, mut x, mut k, mut u): (FILETIME, FILETIME, FILETIME, FILETIME) = std::mem::zeroed();
            let ok = GetProcessTimes(h, &mut c, &mut x, &mut k, &mut u) != 0;
            CloseHandle(h);
            if !ok {
                return None;
            }
            (u64::from(c.dwHighDateTime) << 32) | u64::from(c.dwLowDateTime)
        };
        super::filetime_to_system(created)
    }

    pub fn alive(pid: u32) -> bool {
        // SAFETY: plain Win32 calls; every handle is closed.
        unsafe {
            // a process object that is not signaled still runs (exit code 259 = STILL_ACTIVE would fool
            // GetExitCodeProcess alone); without SYNCHRONIZE access fall back to the exit code
            let h = OpenProcess(PROCESS_SYNCHRONIZE, 0, pid);
            if !h.is_null() {
                let r = WaitForSingleObject(h, 0);
                CloseHandle(h);
                return r == WAIT_TIMEOUT;
            }
            let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid);
            if h.is_null() {
                return false;
            }
            let mut code = 0u32;
            let ok = GetExitCodeProcess(h, &mut code);
            CloseHandle(h);
            ok != 0 && code == STILL_ACTIVE as u32
        }
    }

    pub fn list() -> Result<Vec<(ProcessInfo, Extra)>> {
        snapshot(None)
    }

    /// The processes whose image name (Toolhelp's szExeFile) is `exe`, case-insensitively.
    pub fn list_named(exe: &str) -> Result<Vec<(ProcessInfo, Extra)>> {
        snapshot(Some(exe))
    }

    fn snapshot(only: Option<&str>) -> Result<Vec<(ProcessInfo, Extra)>> {
        let mut out = Vec::new();
        // SAFETY: Toolhelp snapshot walk; dwSize set as required; the snapshot handle is closed.
        unsafe {
            let snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if snap == INVALID_HANDLE_VALUE {
                return Err(std::io::Error::last_os_error().into());
            }
            let mut e: PROCESSENTRY32W = std::mem::zeroed();
            e.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
            let mut more = Process32FirstW(snap, &mut e) != 0;
            while more {
                let pid = e.th32ProcessID;
                let name = wide_to_string(&e.szExeFile);
                if pid != 0 && only.is_none_or(|n| name.eq_ignore_ascii_case(n)) {
                    out.push((ProcessInfo { pid, name, exe: image_path(pid), cmdline: Vec::new() }, Extra::default()));
                }
                more = Process32NextW(snap, &mut e) != 0;
            }
            CloseHandle(snap);
        }
        out.sort_by_key(|(p, _)| p.pid);
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stat_state() {
        assert_eq!(parse_stat_state("123 (enshrouded_serv) S 1 2 3"), Some('S'));
        assert_eq!(parse_stat_state("9 (a) b) c) Z 1"), Some('Z'));
        assert_eq!(parse_stat_state("garbage"), None);
    }

    #[test]
    fn start_from_proc_stat_and_btime() {
        let stat = "4242 (enshrouded.exe) S 1 4242 4242 0 -1 4194560 0 0 0 0 0 0 0 0 20 0 1 0 12345 0 0";
        let proc_stat = "cpu  1 2 3\nbtime 1762400000\nprocesses 9\n";
        let t = parse_start(stat, proc_stat, 100).unwrap();
        assert_eq!(t, std::time::UNIX_EPOCH + std::time::Duration::from_millis(1_762_400_000_000 + 123_450));
        // a name with ") " in it does not shift the fields
        let odd = "7 (a) b) S 1 7 7 0 -1 0 0 0 0 0 0 0 0 0 20 0 1 0 250 0 0";
        assert_eq!(parse_start(odd, proc_stat, 250).unwrap(), std::time::UNIX_EPOCH + std::time::Duration::from_secs(1_762_400_001));
        assert!(parse_start("garbage", proc_stat, 100).is_none());
        assert!(parse_start(stat, "cpu 1 2 3\n", 100).is_none());
    }

    #[test]
    fn filetime_conversion() {
        // 2026-11-06T13:05:09.5Z = 1_793_970_309.5 s after 1970
        let ft = 116_444_736_000_000_000u64 + 1_793_970_309 * 10_000_000 + 5_000_000;
        assert_eq!(filetime_to_system(ft).unwrap(), std::time::UNIX_EPOCH + std::time::Duration::from_millis(1_793_970_309_500));
        assert!(filetime_to_system(1).is_none(), "before 1970");
        assert_eq!(crate::util::rfc3339(crate::util::unix(filetime_to_system(ft).unwrap())), "2026-11-06T13:05:09Z");
    }

    #[test]
    fn own_start_time_is_in_the_past_and_stable() {
        let a = start_time(std::process::id()).expect("own start time");
        let now = std::time::SystemTime::now();
        assert!(a <= now + std::time::Duration::from_secs(1), "{a:?} after {now:?}");
        assert!(now.duration_since(a).unwrap_or_default() < std::time::Duration::from_secs(3600), "started over an hour ago?");
        assert_eq!(start_time(std::process::id()), Some(a), "the same value on every read");
        assert!(start_time(u32::MAX - 3).is_none());
    }

    #[test]
    fn self_is_listed_and_alive() {
        assert!(alive(std::process::id()));
        let me = list().unwrap();
        assert!(me.iter().any(|p| p.pid == std::process::id()));
    }

    #[cfg(windows)]
    #[test]
    fn windows_finds_own_exe_by_image_path() {
        let me = std::env::current_exe().unwrap();
        let (dir, name) = (me.parent().unwrap(), me.file_name().unwrap().to_str().unwrap());
        let p = find_exe(dir, name).unwrap().expect("own process found by image path");
        assert_eq!(p.pid, std::process::id());
        // the same name in another folder is not this process
        assert!(find_exe(&std::env::temp_dir(), name).unwrap().is_none_or(|p| p.pid != std::process::id()));
        // case-insensitive file name, as Windows treats it
        assert_eq!(find_exe(dir, &name.to_ascii_uppercase()).unwrap().map(|p| p.pid), Some(std::process::id()));
        assert!(!alive(u32::MAX - 3));
    }

    #[cfg(not(windows))]
    #[test]
    fn wine_game_matches_by_argv0_or_cwd_not_by_name() {
        let dir = Path::new("/srv/games/Enshrouded");
        let exe = dir.join("enshrouded.exe");
        let p = |name: &str, a0: &str| ProcessInfo { pid: 1, name: name.into(), exe: None, cmdline: vec![a0.into()] };
        let none = Extra::default();
        assert!(matches_exe(&p("enshrouded.exe", "Z:\\srv\\games\\Enshrouded\\enshrouded.exe"), &none, dir, &exe, "enshrouded.exe"));
        // same name, other install
        assert!(!matches_exe(&p("enshrouded.exe", "Z:\\other\\enshrouded.exe"), &none, dir, &exe, "enshrouded.exe"));
        // C:\ path (Proton maps the library into the prefix): cwd decides
        let cwd = Extra { cwd: Some(dir.to_path_buf()) };
        assert!(matches_exe(&p("enshrouded.exe", "C:\\x\\enshrouded.exe"), &cwd, dir, &exe, "enshrouded.exe"));
        // a shell that merely cd'ed into the folder
        assert!(!matches_exe(&p("zsh", "zsh"), &cwd, dir, &exe, "enshrouded.exe"));
        // the server's comm is truncated
        let sdir = Path::new("/srv/s");
        assert!(matches_exe(&p("enshrouded_serv", "./enshrouded_server.exe"), &Extra { cwd: Some(sdir.into()) }, sdir,
            &sdir.join("enshrouded_server.exe"), "enshrouded_server.exe"));
    }
}
