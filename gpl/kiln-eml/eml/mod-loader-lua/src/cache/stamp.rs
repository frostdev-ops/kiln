// SPDX-License-Identifier: GPL-3.0-or-later
// kiln-eml: the operating system's own record of which file this is and when it last changed, apart from the
// last-write time that any program can set. `Fingerprints` (prints.rs) uses it to avoid re-reading a file whose
// bytes it already hashed; see there for what it is trusted for. Nothing here reads file contents.

use std::{fs::{self, File, OpenOptions}, io::Write, path::Path, time::{Duration, UNIX_EPOCH}};

use serde::{Deserialize, Serialize};

/// Identity and status-change time of an open file.
///
/// - Unix (Linux host tools, tests): device, inode, `ctime`.
/// - Windows and Wine/Proton: volume serial number and 128-bit file id (`FileIdInfo`), `ChangeTime`
///   (`FileBasicInfo`), both asked from the open handle.
///
/// The change time moves whenever the file's data or metadata is written, including a write whose last-write time
/// is then put back (`utimes`, `SetFileTime`). On Linux no program can set it back. On Windows a program can, on
/// purpose (`SetFileInformationByHandle` with a `ChangeTime`, or -1 to stop its updates): the stamp catches tools and
/// accidents, not a program written to hide an edit.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct Stamp {
    /// Volume the file is on (Unix `st_dev`, Windows volume serial number).
    pub volume: u64,
    /// File index on that volume (Unix inode, Windows file id).
    pub file: u128,
    /// Status change time, nanoseconds since 1970.
    pub changed: i128,
}

/// How a stamp is read. A function pointer so tests can stand in a file system that does not keep a change time.
pub type Reader = fn(&File) -> Option<Stamp>;

#[cfg(unix)]
pub fn read(file: &File) -> Option<Stamp> {
    use std::os::unix::fs::MetadataExt;
    let m = file.metadata().ok()?;
    if m.ino() == 0 || (m.ctime() == 0 && m.ctime_nsec() == 0) {
        return None;
    }
    Some(Stamp {
        volume: m.dev(),
        file: u128::from(m.ino()),
        changed: i128::from(m.ctime()) * 1_000_000_000 + i128::from(m.ctime_nsec()),
    })
}

#[cfg(windows)]
mod sys {
    use std::ffi::c_void;

    // FILE_INFO_BY_HANDLE_CLASS
    pub const FILE_BASIC_INFO: i32 = 0;
    pub const FILE_ID_INFO: i32 = 18;

    #[repr(C)]
    #[derive(Default)]
    pub struct BasicInfo { pub created: i64, pub accessed: i64, pub written: i64, pub changed: i64, pub attributes: u32 }

    #[repr(C)]
    #[derive(Default)]
    pub struct IdInfo { pub volume: u64, pub file: [u8; 16] }

    unsafe extern "system" {
        pub fn GetFileInformationByHandleEx(file: *mut c_void, class: i32, info: *mut c_void, size: u32) -> i32;
        pub fn GetVolumeInformationByHandleW(
            file: *mut c_void, volume_name: *mut u16, volume_name_len: u32, serial: *mut u32, max_component: *mut u32,
            flags: *mut u32, fs_name: *mut u16, fs_name_len: u32,
        ) -> i32;
    }
}

#[cfg(windows)]
pub fn read(file: &File) -> Option<Stamp> {
    use std::os::windows::io::AsRawHandle;
    let handle = file.as_raw_handle();
    let mut basic = sys::BasicInfo::default();
    let mut id = sys::IdInfo::default();
    // SAFETY: both structs match their FILE_*_INFO layouts and the sizes passed; the handle is open for the call.
    let ok = unsafe {
        sys::GetFileInformationByHandleEx(handle, sys::FILE_BASIC_INFO, (&raw mut basic).cast(), size_of::<sys::BasicInfo>() as u32) != 0
            && sys::GetFileInformationByHandleEx(handle, sys::FILE_ID_INFO, (&raw mut id).cast(), size_of::<sys::IdInfo>() as u32) != 0
    };
    let file_id = u128::from_le_bytes(id.file);
    // An all-zero id or time is what a file system that does not keep them reports: not a stamp.
    if !ok || basic.changed <= 0 || file_id == 0 {
        return None;
    }
    // FILETIME: 100 ns since 1601
    Some(Stamp { volume: id.volume, file: file_id, changed: (i128::from(basic.changed) - 116_444_736_000_000_000) * 100 })
}

#[cfg(not(any(unix, windows)))]
pub fn read(_file: &File) -> Option<Stamp> {
    None
}

/// File systems whose change time is a creation time or a coarse copy of the write time. Wine reports the host's
/// file systems as NTFS (the usual case), so this catches real Windows volumes and Wine's FAT-family reports.
#[cfg(windows)]
fn file_system_is_trustworthy(file: &File) -> Result<(), String> {
    use std::os::windows::io::AsRawHandle;
    let mut name = [0u16; 64];
    // SAFETY: only the file-system-name buffer is requested, with its real length.
    let ok = unsafe {
        sys::GetVolumeInformationByHandleW(
            file.as_raw_handle(), std::ptr::null_mut(), 0, std::ptr::null_mut(), std::ptr::null_mut(),
            std::ptr::null_mut(), name.as_mut_ptr(), name.len() as u32,
        ) != 0
    };
    if !ok {
        return Err("the file system cannot be identified".into());
    }
    let len = name.iter().position(|&c| c == 0).unwrap_or(name.len());
    let name = String::from_utf16_lossy(&name[..len]);
    match name.to_ascii_uppercase().as_str() {
        "" | "FAT" | "FAT32" | "EXFAT" | "CDFS" | "UDF" => Err(format!("{name:?} does not keep a reliable change time")),
        _ => Ok(()),
    }
}

#[cfg(not(windows))]
fn file_system_is_trustworthy(_file: &File) -> Result<(), String> {
    Ok(())
}

/// Does a same-size rewrite whose last-write time is put back move the change time on the volume of `dir`?
///
/// Runs on a scratch file in `dir` (the game folder; removed again, and earlier leftovers too) and nothing else.
/// `Ok(volume)` is the volume the answer holds for; a stamp is only ever trusted for files on that volume. `Err`
/// carries the reason: the file system keeps no usable change time, so every file is hashed in full.
pub fn probe(dir: &Path, read: Reader) -> Result<u64, String> {
    sweep(dir);
    let path = dir.join(format!("{PROBE_PREFIX}{}-{}", std::process::id(), uuid::Uuid::new_v4().simple()));
    let result = probe_file(&path, read);
    let _ = fs::remove_file(&path);
    result
}

const PROBE_PREFIX: &str = ".kiln-eml-stamp-probe-";

/// kiln-eml: a run that died inside the test (or whose scratch file a virus scanner held open) left its file behind:
/// remove those older than a minute. A test in progress is younger than that.
fn sweep(dir: &Path) {
    let Ok(entries) = fs::read_dir(dir) else { return };
    for entry in entries.flatten() {
        if !entry.file_name().to_string_lossy().starts_with(PROBE_PREFIX) {
            continue;
        }
        let stale = entry.metadata().ok()
            .filter(|m| m.is_file())
            .and_then(|m| m.modified().ok())
            .and_then(|t| t.elapsed().ok())
            .is_some_and(|age| age > Duration::from_secs(60));
        if stale {
            let _ = fs::remove_file(entry.path());
        }
    }
}

fn probe_file(path: &Path, read: Reader) -> Result<u64, String> {
    let io = |what: &str| { let what = what.to_owned(); move |e: std::io::Error| format!("{what}: {e}") };
    let mut file = OpenOptions::new().write(true).create_new(true).open(path).map_err(io("create the scratch file"))?;
    file.write_all(b"kiln-eml").map_err(io("write the scratch file"))?;
    file.sync_all().map_err(io("flush the scratch file"))?;
    // kiln-eml: a last-write time with 100 ns detail must come back unchanged. FAT keeps 2 s and exFAT 10 ms; Wine
    // reports both as NTFS when they are host mounts, and Linux keeps exFAT's change time only in memory (it is the
    // last-write time again after a remount), so the rewrite test below would pass there and say nothing.
    let now = file.metadata().and_then(|m| m.modified()).map_err(io("read the last-write time"))?;
    let seconds = now.duration_since(UNIX_EPOCH).map_err(|e| format!("the clock is before 1970: {e}"))?.as_secs();
    let written = UNIX_EPOCH + Duration::new(seconds, 123_456_700);
    file.set_modified(written).map_err(io("set the last-write time"))?;
    if file.metadata().and_then(|m| m.modified()).map_err(io("read the last-write time"))? != written {
        return Err("the file system keeps coarse times (FAT or exFAT): its change time does not survive a remount".into());
    }
    file_system_is_trustworthy(&file)?;
    let first = read(&file).ok_or("the file system reports no file identity or change time")?;
    drop(file);

    // let a coarse clock (up to one scheduler tick) move on, then rewrite the same number of bytes with other bytes
    // and put the last-write time back, as an in-place edit that tries not to be noticed would
    std::thread::sleep(Duration::from_millis(30));
    let mut file = OpenOptions::new().write(true).open(path).map_err(io("reopen the scratch file"))?;
    file.write_all(b"EML-kiln").map_err(io("rewrite the scratch file"))?;
    file.set_modified(written).map_err(io("restore the last-write time"))?;
    drop(file);

    let file = File::open(path).map_err(io("read the scratch file back"))?;
    let meta = file.metadata().map_err(io("read the scratch file's metadata"))?;
    let second = read(&file).ok_or("the file system lost the file identity")?;
    if meta.len() != 8 || meta.modified().map_err(io("read the last-write time"))? != written {
        return Err("the last-write time could not be restored, so the test says nothing".into());
    }
    if second.volume != first.volume || second.file != first.file {
        return Err("the file identity is not stable".into());
    }
    if second.changed <= first.changed {
        return Err("a same-size edit with the last-write time restored did not move the change time".into());
    }
    Ok(first.volume)
}
