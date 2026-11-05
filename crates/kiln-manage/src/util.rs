//! Small helpers shared by kilnctl and the launcher (moved from runner/kilnctl/src/util.rs): paths, UTC time
//! formatting without a time crate, human-readable sizes, external commands, atomic writes.

use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

pub fn home() -> PathBuf {
    let var = if cfg!(windows) { "USERPROFILE" } else { "HOME" };
    std::env::var_os(var).map(PathBuf::from).unwrap_or_else(|| PathBuf::from("/"))
}

/// Expands a leading `~` or `~/`.
pub fn expand(s: &str, home: &Path) -> PathBuf {
    if s == "~" {
        home.to_path_buf()
    } else if let Some(rest) = s.strip_prefix("~/") {
        home.join(rest)
    } else {
        PathBuf::from(s)
    }
}

/// Lexical clean-up: drops `.` components and trailing slashes (no symlink resolution).
pub fn clean(p: &Path) -> PathBuf {
    p.components().collect()
}

/// `<path><suffix>` (`/srv/x` + `.pfx` = `/srv/x.pfx`).
pub fn with_suffix(p: &Path, suffix: &str) -> PathBuf {
    let mut s = p.as_os_str().to_owned();
    s.push(suffix);
    PathBuf::from(s)
}

fn xdg(var: &str, fallback: &str) -> PathBuf {
    match std::env::var_os(var) {
        Some(v) if Path::new(&v).is_absolute() => PathBuf::from(v),
        _ => home().join(fallback),
    }
}

/// `$XDG_CONFIG_HOME/kiln` (default `~/.config/kiln`); Windows `%APPDATA%\Kiln`.
pub fn config_dir() -> PathBuf {
    if cfg!(windows)
        && let Some(a) = std::env::var_os("APPDATA")
    {
        return PathBuf::from(a).join("Kiln");
    }
    xdg("XDG_CONFIG_HOME", ".config").join("kiln")
}

/// `$XDG_STATE_HOME/kiln` (default `~/.local/state/kiln`); Windows `%LOCALAPPDATA%\Kiln\state`.
pub fn state_dir() -> PathBuf {
    if cfg!(windows)
        && let Some(a) = std::env::var_os("LOCALAPPDATA")
    {
        return PathBuf::from(a).join("Kiln").join("state");
    }
    xdg("XDG_STATE_HOME", ".local/state").join("kiln")
}

/// Days since 1970-01-01 -> (year, month, day). Howard Hinnant's `civil_from_days`.
pub fn civil_from_days(z: i64) -> (i64, u32, u32) {
    let z = z + 719_468;
    let era = (if z >= 0 { z } else { z - 146_096 }) / 146_097;
    let doe = z - era * 146_097;
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = (doy - (153 * mp + 2) / 5 + 1) as u32;
    let m = (if mp < 10 { mp + 3 } else { mp - 9 }) as u32;
    let y = yoe + era * 400 + i64::from(m <= 2);
    (y, m, d)
}

/// (year, month, day) -> days since 1970-01-01 (inverse of [`civil_from_days`]).
pub fn days_from_civil(y: i64, m: u32, d: u32) -> i64 {
    let y = if m <= 2 { y - 1 } else { y };
    let era = (if y >= 0 { y } else { y - 399 }) / 400;
    let yoe = y - era * 400;
    let m = m as i64;
    let doy = (153 * (if m > 2 { m - 3 } else { m + 9 }) + 2) / 5 + d as i64 - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    era * 146_097 + doe - 719_468
}

/// Seconds since the epoch (negative before it).
pub fn unix(t: SystemTime) -> i64 {
    match t.duration_since(UNIX_EPOCH) {
        Ok(d) => d.as_secs() as i64,
        Err(e) => -(e.duration().as_secs() as i64),
    }
}

pub fn now_unix() -> i64 {
    unix(SystemTime::now())
}

/// (year, month, day, hour, minute, second) in UTC.
pub fn utc(t: SystemTime) -> (i64, u32, u32, u32, u32, u32) {
    utc_secs(unix(t))
}

pub fn utc_secs(secs: i64) -> (i64, u32, u32, u32, u32, u32) {
    let (days, rem) = (secs.div_euclid(86_400), secs.rem_euclid(86_400));
    let (y, mo, d) = civil_from_days(days);
    (y, mo, d, (rem / 3600) as u32, (rem / 60 % 60) as u32, (rem % 60) as u32)
}

/// `2026-11-03 22:10:01Z`
pub fn fmt_utc(t: SystemTime) -> String {
    let (y, mo, d, h, mi, s) = utc(t);
    format!("{y:04}-{mo:02}-{d:02} {h:02}:{mi:02}:{s:02}Z")
}

/// `20261103-221001Z` (kilnctl file names).
pub fn stamp(t: SystemTime) -> String {
    let (y, mo, d, h, mi, s) = utc(t);
    format!("{y:04}{mo:02}{d:02}-{h:02}{mi:02}{s:02}Z")
}

/// RFC 3339 UTC: `2026-11-04T12:00:00Z` (every launcher format).
pub fn rfc3339(secs: i64) -> String {
    let (y, mo, d, h, mi, s) = utc_secs(secs);
    format!("{y:04}-{mo:02}-{d:02}T{h:02}:{mi:02}:{s:02}Z")
}

pub fn now_rfc3339() -> String {
    rfc3339(now_unix())
}

/// `20261104T120000Z` (contracts.md folder names: backups/install-<ts>, save-backups/<ts>).
pub fn ts_compact(secs: i64) -> String {
    let (y, mo, d, h, mi, s) = utc_secs(secs);
    format!("{y:04}{mo:02}{d:02}T{h:02}{mi:02}{s:02}Z")
}

/// RFC 3339 with a zone (`Z`, `z`, `+01:00`; fractional seconds allowed) -> unix seconds.
pub fn parse_rfc3339(text: &str) -> Option<i64> {
    let b = text.as_bytes();
    if b.len() < 20 {
        return None;
    }
    let num = |r: std::ops::Range<usize>| -> Option<i64> {
        let s = text.get(r)?;
        (!s.is_empty() && s.bytes().all(|c| c.is_ascii_digit())).then(|| s.parse().ok()).flatten()
    };
    if b[4] != b'-' || b[7] != b'-' || !matches!(b[10], b'T' | b't' | b' ') || b[13] != b':' || b[16] != b':' {
        return None;
    }
    let (y, mo, d, h, mi, s) = (num(0..4)?, num(5..7)?, num(8..10)?, num(11..13)?, num(14..16)?, num(17..19)?);
    if !(1..=12).contains(&mo) || !(1..=31).contains(&d) || h > 23 || mi > 59 || s > 60 {
        return None;
    }
    let mut i = 19;
    if b.get(i) == Some(&b'.') {
        i += 1;
        let start = i;
        while b.get(i).is_some_and(u8::is_ascii_digit) {
            i += 1;
        }
        if i == start {
            return None;
        }
    }
    let offset = match b.get(i)? {
        b'Z' | b'z' if i + 1 == b.len() => 0,
        c @ (b'+' | b'-') if i + 6 == b.len() && b[i + 3] == b':' => {
            let (oh, om) = (num(i + 1..i + 3)?, num(i + 4..i + 6)?);
            if oh > 23 || om > 59 {
                return None;
            }
            let o = oh * 3600 + om * 60;
            if *c == b'+' { o } else { -o }
        }
        _ => return None,
    };
    let days = days_from_civil(y, mo as u32, d as u32);
    if civil_from_days(days) != (y, mo as u32, d as u32) {
        return None; // 2026-02-30
    }
    Some(days * 86_400 + h * 3600 + mi * 60 + s - offset)
}

pub fn human_duration(d: Duration) -> String {
    let s = d.as_secs();
    match s {
        0..60 => format!("{s}s"),
        60..3600 => format!("{}m{:02}s", s / 60, s % 60),
        3600..86_400 => format!("{}h{:02}m", s / 3600, s / 60 % 60),
        _ => format!("{}d{:02}h", s / 86_400, s / 3600 % 24),
    }
}

pub fn human_size(n: u64) -> String {
    const U: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut v = n as f64;
    let mut i = 0;
    while v >= 1024.0 && i < U.len() - 1 {
        v /= 1024.0;
        i += 1;
    }
    if i == 0 { format!("{n} B") } else { format!("{v:.1} {}", U[i]) }
}

#[cfg(unix)]
pub fn is_executable(p: &Path) -> bool {
    use std::os::unix::fs::PermissionsExt;
    p.metadata().map(|m| m.is_file() && m.permissions().mode() & 0o111 != 0).unwrap_or(false)
}

#[cfg(not(unix))]
pub fn is_executable(p: &Path) -> bool {
    p.is_file()
}

/// Looks a command up on `$PATH`.
pub fn which(cmd: &str) -> Option<PathBuf> {
    let path = std::env::var_os("PATH")?;
    std::env::split_paths(&path).map(|d| d.join(cmd)).find(|p| is_executable(p))
}

/// sha256 of a file (hash::sha256_file without the size).
pub fn sha256(path: &Path) -> Result<String> {
    crate::hash::sha256_file(path)
        .map(|(h, _)| h)
        .map_err(|e| Error::new(e.code, format!("sha256 {}: {}", path.display(), e.message)))
}

/// Runs a command with inherited stdio; fails on a non-zero exit.
pub fn run_status(cmd: &mut Command, what: &str) -> Result<()> {
    let st = cmd.status().map_err(|e| Error::new(ErrorCode::Io, format!("starting {what}: {e}")))?;
    if !st.success() {
        return Err(Error::new(ErrorCode::Io, format!("{what} failed ({st})")));
    }
    Ok(())
}

/// An io error with the path in its message (the code follows the io error kind).
pub fn io_at(path: &Path, what: &str) -> impl FnOnce(std::io::Error) -> Error {
    let p = path.display().to_string();
    let what = what.to_string();
    move |e| {
        let base = Error::from(e);
        Error::new(base.code, format!("{what} {p}: {}", base.message))
    }
}

/// Replaces `path` atomically (contracts.md 1): `<name>.tmp` in the same folder, flushed, renamed over the target.
pub fn write_atomic(path: &Path, data: &[u8]) -> Result<()> {
    use std::io::Write;
    let dir = path.parent().ok_or_else(|| Error::invalid(format!("{}: no parent folder", path.display())))?;
    std::fs::create_dir_all(dir).map_err(io_at(dir, "creating"))?;
    let name = path.file_name().ok_or_else(|| Error::invalid(format!("{}: no file name", path.display())))?;
    let mut tmp_name = name.to_os_string();
    tmp_name.push(".tmp");
    let tmp = dir.join(tmp_name);
    {
        let mut f = std::fs::File::create(&tmp).map_err(io_at(&tmp, "writing"))?;
        f.write_all(data).map_err(io_at(&tmp, "writing"))?;
        f.sync_all().map_err(io_at(&tmp, "flushing"))?;
    }
    rename_replace(&tmp, path).inspect_err(|_| {
        let _ = std::fs::remove_file(&tmp);
    })
}

/// `rename(from, to)` replacing `to` (Windows: MoveFileEx semantics through std, which replaces).
pub fn rename_replace(from: &Path, to: &Path) -> Result<()> {
    std::fs::rename(from, to).map_err(io_at(to, "renaming to"))
}

/// JSON (pretty, two spaces, trailing newline) written atomically.
pub fn write_json_atomic<T: serde::Serialize>(path: &Path, value: &T) -> Result<()> {
    let mut text = serde_json::to_string_pretty(value)?;
    text.push('\n');
    write_atomic(path, text.as_bytes())
}

/// Reads and parses a JSON file; Ok(None) when it does not exist.
pub fn read_json<T: serde::de::DeserializeOwned>(path: &Path) -> Result<Option<T>> {
    let text = match std::fs::read(path) {
        Ok(t) => t,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(io_at(path, "reading")(e)),
    };
    let text = text.strip_prefix(b"\xef\xbb\xbf").unwrap_or(&text);
    serde_json::from_slice(text)
        .map(Some)
        .map_err(|e| Error::new(ErrorCode::Parse, format!("{}: {e}", path.display())))
}

/// N random bytes from the OS without the getrandom dependency: /dev/urandom, Windows BCryptGenRandom.
pub fn random_bytes<const N: usize>() -> Result<[u8; N]> {
    let mut b = [0u8; N];
    #[cfg(unix)]
    {
        use std::io::Read;
        std::fs::File::open("/dev/urandom")
            .and_then(|mut f| f.read_exact(&mut b))
            .map_err(|e| Error::new(ErrorCode::Io, format!("reading /dev/urandom: {e}")))?;
    }
    #[cfg(windows)]
    {
        // SAFETY: fills the buffer; BCRYPT_USE_SYSTEM_PREFERRED_RNG needs no algorithm handle.
        let st = unsafe {
            windows_sys::Win32::Security::Cryptography::BCryptGenRandom(
                std::ptr::null_mut(),
                b.as_mut_ptr(),
                N as u32,
                windows_sys::Win32::Security::Cryptography::BCRYPT_USE_SYSTEM_PREFERRED_RNG,
            )
        };
        if st != 0 {
            return Err(Error::new(ErrorCode::Io, format!("BCryptGenRandom failed ({st:#x})")));
        }
    }
    Ok(b)
}

/// A random version 4 UUID (`installId`).
pub fn uuid_v4() -> Result<String> {
    let mut b = random_bytes::<16>()?;
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    let h = crate::hash::hex(&b);
    Ok(format!("{}-{}-{}-{}-{}", &h[0..8], &h[8..12], &h[12..16], &h[16..20], &h[20..32]))
}

/// Copies a folder tree (regular files and folders; symlinks are refused). Returns the number of files.
pub fn copy_tree(from: &Path, to: &Path) -> Result<u64> {
    std::fs::create_dir_all(to).map_err(io_at(to, "creating"))?;
    let mut n = 0;
    for e in std::fs::read_dir(from).map_err(io_at(from, "reading"))? {
        let e = e?;
        let ft = e.file_type()?;
        let dst = to.join(e.file_name());
        if ft.is_symlink() {
            return Err(Error::new(ErrorCode::Refused, format!("{}: symlinks are not copied", e.path().display())));
        } else if ft.is_dir() {
            n += copy_tree(&e.path(), &dst)?;
        } else {
            std::fs::copy(e.path(), &dst).map_err(io_at(&dst, "copying to"))?;
            n += 1;
        }
    }
    Ok(n)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rfc3339_round_trip() {
        assert_eq!(rfc3339(1_762_257_600), "2025-11-04T12:00:00Z");
        assert_eq!(parse_rfc3339("2025-11-04T12:00:00Z"), Some(1_762_257_600));
        assert_eq!(parse_rfc3339("2026-11-04T13:00:00+01:00"), Some(1_793_793_600));
        assert_eq!(parse_rfc3339("2026-11-04T12:00:00.250z"), Some(1_793_793_600));
        assert_eq!(parse_rfc3339("2026-11-04T12:00:00"), None);
        assert_eq!(parse_rfc3339("2026-02-30T12:00:00Z"), None);
        assert_eq!(ts_compact(1_793_793_600), "20261104T120000Z");
        for d in [-1000, 0, 11_016, 20_760] {
            let (y, m, dd) = civil_from_days(d);
            assert_eq!(days_from_civil(y, m, dd), d);
        }
    }

    #[test]
    fn uuid_shape() {
        let u = uuid_v4().unwrap();
        assert_eq!(u.len(), 36);
        assert_eq!(&u[14..15], "4");
        assert_ne!(u, uuid_v4().unwrap());
    }

    #[test]
    fn atomic_writes_replace() {
        let d = std::env::temp_dir().join(format!("km-util-{}", std::process::id()));
        let f = d.join("a/b.json");
        write_json_atomic(&f, &serde_json::json!({"x": 1})).unwrap();
        write_atomic(&f, b"{\"x\": 2}\n").unwrap();
        let v: serde_json::Value = read_json(&f).unwrap().unwrap();
        assert_eq!(v["x"], 2);
        assert!(!d.join("a/b.json.tmp").exists());
        assert!(read_json::<serde_json::Value>(&d.join("none.json")).unwrap().is_none());
        let _ = std::fs::remove_dir_all(&d);
    }
}
