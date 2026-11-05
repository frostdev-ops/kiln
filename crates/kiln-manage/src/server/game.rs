//! The game side of a dedicated server: enshrouded_server.json, the game log, the savegame folder.
use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::time::SystemTime;

fn bad(message: impl Into<String>) -> Error {
    Error::new(ErrorCode::Schema, message)
}

pub const EXE: &str = "enshrouded_server.exe";
pub const CONFIG: &str = "enshrouded_server.json";
pub const LOG: &str = "enshrouded_server.log";
/// The world is up: `[game_server] Switching state from LoadEcsScene to Run after ...`.
pub const READY: &str = "LoadEcsScene to Run";

#[derive(Debug, Clone, PartialEq)]
pub struct ServerConfig {
    pub name: String,
    pub query_port: u16,
    pub save_dir: String,
    pub log_dir: String,
    pub slot_count: Option<i64>,
}

pub fn parse_config(text: &str) -> Result<ServerConfig> {
    let v: serde_json::Value =
        serde_json::from_str(text).map_err(|e| Error::new(ErrorCode::Parse, format!("invalid JSON: {e}")))?;
    let o = v.as_object().ok_or_else(|| bad("not a JSON object"))?;
    let port = o.get("queryPort").ok_or_else(|| bad("queryPort missing"))?;
    let port = port.as_u64().and_then(|p| u16::try_from(p).ok()).filter(|&p| p > 0);
    let port = port.ok_or_else(|| bad("queryPort is not a port number"))?;
    let s = |k: &str, d: &str| -> Result<String> {
        match o.get(k) {
            None => Ok(d.to_string()),
            Some(serde_json::Value::String(s)) => Ok(s.clone()),
            Some(_) => Err(bad(format!("{k} is not a string"))),
        }
    };
    Ok(ServerConfig {
        name: s("name", "Enshrouded Server")?,
        query_port: port,
        save_dir: s("saveDirectory", "./savegame")?,
        log_dir: s("logDirectory", "./logs")?,
        slot_count: o.get("slotCount").and_then(|x| x.as_i64()),
    })
}

pub fn read_config(path: &Path) -> Result<ServerConfig> {
    let text = std::fs::read_to_string(path).map_err(crate::util::io_at(path, "reading"))?;
    parse_config(&text).map_err(|e| Error::new(e.code, format!("{}: {}", path.display(), e.message)))
}

/// A folder from enshrouded_server.json (Windows view, relative to the server folder) as a host path. On Windows the
/// game's view is the host's: absolute paths (`D:\Saves`, `Z:\...` on a mapped drive, `\\nas\share`) stay as they are.
pub fn host_path(dir: &Path, prefix: &Path, p: &str) -> PathBuf {
    if cfg!(windows) {
        let rel = p.trim_start_matches("./").trim_start_matches(".\\");
        return if crate::winpath::is_absolute_game_path(p) { PathBuf::from(p) } else { crate::util::clean(&dir.join(rel)) };
    }
    let s = p.replace('\\', "/");
    let lower = s.to_ascii_lowercase();
    if lower.starts_with("z:/") {
        return crate::util::clean(Path::new(&s[2..]));
    }
    if lower.starts_with("c:/") {
        return crate::util::clean(&prefix.join("drive_c").join(&s[3..]));
    }
    if s.starts_with('/') {
        return crate::util::clean(Path::new(&s));
    }
    crate::util::clean(&dir.join(s.trim_start_matches("./")))
}

/// 16 characters from [A-Za-z0-9], from the OS random source.
pub fn password() -> Result<String> {
    const SET: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
    let mut out = String::new();
    while out.len() < 16 {
        let buf = crate::util::random_bytes::<64>()?;
        for &b in &buf {
            if (b as usize) < 248 && out.len() < 16 {
                out.push(SET[b as usize % SET.len()] as char);
            }
        }
    }
    Ok(out)
}

/// A complete enshrouded_server.json like the game writes for a fresh server: the presets' four user groups with
/// random passwords, default difficulty. Written before the first start, so the server never comes up on the
/// default port 15637.
pub fn default_config(name: &str, port: u16) -> Result<String> {
    // canKickBan, canAccessInventories, canEditWorld, canEditBase, canExtendBase
    let groups: [(&str, [bool; 5]); 4] = [
        ("Admin", [true, true, true, true, true]),
        ("Friend", [false, true, true, true, false]),
        ("Guest", [false, false, true, false, false]),
        ("Visitor", [false, false, false, false, false]),
    ];
    let mut g = String::new();
    for (i, (n, f)) in groups.iter().enumerate() {
        g += &format!(
            "\t\t{{\n\t\t\t\"name\": \"{n}\",\n\t\t\t\"password\": \"{}\",\n\t\t\t\"canKickBan\": {},\n\
             \t\t\t\"canAccessInventories\": {},\n\t\t\t\"canEditWorld\": {},\n\t\t\t\"canEditBase\": {},\n\
             \t\t\t\"canExtendBase\": {},\n\t\t\t\"reservedSlots\": 0\n\t\t}}{}\n",
            password()?,
            f[0],
            f[1],
            f[2],
            f[3],
            f[4],
            if i + 1 < groups.len() { "," } else { "" }
        );
    }
    Ok(format!(
        "{{\n\t\"name\": {name},\n\t\"saveDirectory\": \"./savegame\",\n\t\"logDirectory\": \"./logs\",\n\
         \t\"ip\": \"0.0.0.0\",\n\t\"queryPort\": {port},\n\t\"slotCount\": 16,\n\t\"tags\": [],\n\
         \t\"voiceChatMode\": \"Proximity\",\n\t\"enableVoiceChat\": false,\n\t\"enableTextChat\": false,\n\
         \t\"gameSettingsPreset\": \"Default\",\n\t\"userGroups\": [\n{g}\t],\n\t\"bannedAccounts\": []\n}}\n",
        name = serde_json::to_string(name)?
    ))
}

/// Game log text (the game writes CRLF).
pub fn read_log(path: &Path) -> Option<String> {
    let b = std::fs::read(path).ok()?;
    Some(String::from_utf8_lossy(&b).replace('\r', ""))
}

/// Last world state switched to: `Switching state from LoadEcsScene to Run after ...` -> "Run".
pub fn last_state(log: &str) -> Option<String> {
    log.lines().rev().find_map(|l| {
        let rest = &l[l.find("Switching state from ")? + "Switching state from ".len()..];
        let (_, to) = rest.split_once(" to ")?;
        Some(to.split_whitespace().next()?.to_string())
    })
}

/// Follows a log file across a start: only text written after `new` counts, also when the game replaces or
/// truncates the file.
pub struct LogWatch {
    path: PathBuf,
    ino: Option<(u64, u64)>,
    base: u64,
    last_len: u64,
}

impl LogWatch {
    pub fn new(path: PathBuf) -> Self {
        let md = std::fs::metadata(&path).ok();
        let len = md.as_ref().map_or(0, |m| m.len());
        let ino = md.as_ref().and_then(|m| identity(&path, m));
        LogWatch { path, ino, base: len, last_len: len }
    }

    /// Text written since the mark.
    pub fn poll(&mut self) -> Option<String> {
        let md = std::fs::metadata(&self.path).ok()?;
        let id = identity(&self.path, &md);
        if id != self.ino || md.len() < self.last_len {
            self.base = 0;
            self.ino = id;
        }
        self.last_len = md.len();
        let b = std::fs::read(&self.path).ok()?;
        let from = (self.base as usize).min(b.len());
        Some(String::from_utf8_lossy(&b[from..]).replace('\r', ""))
    }

    pub fn path(&self) -> &Path {
        &self.path
    }
}

/// (device, inode) of a file: a replaced file has a new one. None where the platform has no inode (then only
/// truncation is detected).
#[cfg(unix)]
pub fn file_id(md: &std::fs::Metadata) -> Option<(u64, u64)> {
    use std::os::unix::fs::MetadataExt;
    Some((md.dev(), md.ino()))
}

#[cfg(not(unix))]
pub fn file_id(md: &std::fs::Metadata) -> Option<(u64, u64)> {
    // creation time stands in for the inode: a replaced file was created later
    md.created().ok().and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok()).map(|d| (0, d.as_nanos() as u64))
}

/// Which file `path` is now: `file_id` on Unix; on Windows the volume serial number and file index of an open handle
/// (creation times are no identity there: file-system tunneling gives a file re-created under the same name the old
/// one's creation time, and Wine reports a time that changes with every write).
fn identity(path: &Path, md: &std::fs::Metadata) -> Option<(u64, u64)> {
    #[cfg(windows)]
    {
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle};
        let _ = md;
        let f = std::fs::File::open(path).ok()?;
        // SAFETY: a valid open handle and a zeroed out-struct of the right type.
        unsafe {
            let mut info: BY_HANDLE_FILE_INFORMATION = std::mem::zeroed();
            if GetFileInformationByHandle(f.as_raw_handle() as _, &mut info) == 0 {
                return None;
            }
            Some((u64::from(info.dwVolumeSerialNumber), (u64::from(info.nFileIndexHigh) << 32) | u64::from(info.nFileIndexLow)))
        }
    }
    #[cfg(not(windows))]
    {
        let _ = path;
        file_id(md)
    }
}

/// Newest regular file directly in `dir` (the save; `backup/` subfolders are not looked into).
pub fn newest_file(dir: &Path) -> Option<(PathBuf, SystemTime, u64)> {
    std::fs::read_dir(dir)
        .ok()?
        .flatten()
        .filter_map(|e| {
            let md = e.metadata().ok()?;
            if !md.is_file() {
                return None;
            }
            Some((e.path(), md.modified().ok()?, md.len()))
        })
        .max_by_key(|(_, t, _)| *t)
}

#[cfg(test)]
mod tests {
    use super::host_path;
    use std::path::Path;

    #[cfg(windows)]
    #[test]
    fn host_path_windows_keeps_absolute_paths() {
        let dir = Path::new(r"C:\Games\Enshrouded Server Ü");
        let pfx = Path::new("");
        assert_eq!(host_path(dir, pfx, "./logs"), Path::new(r"C:\Games\Enshrouded Server Ü\logs"));
        assert_eq!(host_path(dir, pfx, r".\savegame"), Path::new(r"C:\Games\Enshrouded Server Ü\savegame"));
        assert_eq!(host_path(dir, pfx, r"D:\Saves"), Path::new(r"D:\Saves"));
        assert_eq!(host_path(dir, pfx, "C:/Logs"), Path::new("C:/Logs"));
        assert_eq!(host_path(dir, pfx, r"Z:\share\logs"), Path::new(r"Z:\share\logs"));
        assert_eq!(host_path(dir, pfx, r"\\nas\saves"), Path::new(r"\\nas\saves"));
    }

    #[cfg(unix)]
    #[test]
    fn host_path_wine_maps_drives() {
        let dir = Path::new("/srv/s");
        let pfx = Path::new("/srv/s.pfx");
        assert_eq!(host_path(dir, pfx, "./logs"), Path::new("/srv/s/logs"));
        assert_eq!(host_path(dir, pfx, r"Z:\srv\logs"), Path::new("/srv/logs"));
        assert_eq!(host_path(dir, pfx, r"C:\logs"), Path::new("/srv/s.pfx/drive_c/logs"));
    }
}
