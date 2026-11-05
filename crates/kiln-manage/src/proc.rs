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
    let want_dir = canon(dir);
    let want_exe = want_dir.join(exe);
    let mut found: Vec<ProcessInfo> = Vec::new();
    // Windows: only processes with the exe's image name get their full image path looked up (the launcher's watcher
    // calls this every second; opening and canonicalizing every process on the system each time is wasteful)
    let candidates = if cfg!(windows) { imp::list_named(exe)? } else { list_with_extra()? };
    for (p, extra) in candidates {
        if matches_exe(&p, &extra, &want_dir, &want_exe, exe) {
            found.push(p);
        }
    }
    found.sort_by_key(|p| p.pid);
    Ok(found.into_iter().next())
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
    let name_ok = p.name.eq_ignore_ascii_case(&comm_of(exe))
        || p.cmdline.first().is_some_and(|a| win_basename(a).eq_ignore_ascii_case(exe));
    if !name_ok {
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

fn win_basename(s: &str) -> &str {
    s.rsplit(['\\', '/']).next().unwrap_or(s)
}

pub fn alive(pid: u32) -> bool {
    imp::alive(pid)
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

    pub fn list() -> Result<Vec<(ProcessInfo, Extra)>> {
        let mut out = Vec::new();
        for pid in pids() {
            let Ok(comm) = fs::read_to_string(format!("/proc/{pid}/comm")) else { continue };
            if !alive(pid) {
                continue;
            }
            let cmd = fs::read(format!("/proc/{pid}/cmdline")).unwrap_or_default();
            let cmdline: Vec<String> = cmd
                .split(|&b| b == 0)
                .filter(|a| !a.is_empty())
                .map(|a| String::from_utf8_lossy(a).into_owned())
                .collect();
            let exe = fs::read_link(format!("/proc/{pid}/exe")).ok();
            let cwd = fs::read_link(format!("/proc/{pid}/cwd")).ok();
            out.push((ProcessInfo { pid, name: comm.trim_end().to_string(), exe, cmdline }, Extra { cwd }));
        }
        out.sort_by_key(|(p, _)| p.pid);
        Ok(out)
    }
}

#[cfg(windows)]
mod imp {
    use super::{Extra, ProcessInfo};
    use crate::Result;
    use std::path::PathBuf;
    use windows_sys::Win32::Foundation::{CloseHandle, INVALID_HANDLE_VALUE, STILL_ACTIVE, WAIT_TIMEOUT};
    use windows_sys::Win32::System::Diagnostics::ToolHelp::{
        CreateToolhelp32Snapshot, PROCESSENTRY32W, Process32FirstW, Process32NextW, TH32CS_SNAPPROCESS,
    };
    use windows_sys::Win32::System::Threading::{
        GetExitCodeProcess, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
        QueryFullProcessImageNameW, WaitForSingleObject,
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
