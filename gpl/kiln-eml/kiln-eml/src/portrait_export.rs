// SPDX-License-Identifier: GPL-3.0-or-later
//! A bounded C ABI and offline entry point for the CPU portrait renderer. This reads game archives, never writes them.
use crate::portraits::{self, Request};
use std::fs::{File, OpenOptions};
use std::path::{Component, Path};

pub const MAX_REQUEST: usize = 256 * 1024;
pub const MAX_ITEMS: usize = 32;
pub const MAX_PIXELS: u64 = 32 * 1024 * 1024;

fn is_link(meta: &std::fs::Metadata) -> bool {
    if meta.file_type().is_symlink() {
        return true;
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    false
}

// Wine exposes Unix volume boundaries (including Z:\) as mount-point reparse entries without reparse data.
// These do not redirect a cache path. Never extend this exception to data-backed junctions or symbolic links.
#[cfg(windows)]
fn wine_volume_boundary(path: &Path) -> bool {
    use std::os::windows::{ffi::{OsStrExt, OsStringExt}, fs::OpenOptionsExt, io::AsRawHandle};
    use windows::{core::{s, w, PCWSTR}, Win32::{
        Foundation::{HANDLE, ERROR_NOT_A_REPARSE_POINT},
        Storage::FileSystem::{FileAttributeTagInfo, FILE_ATTRIBUTE_TAG_INFO,
            GetFileInformationByHandleEx, GetVolumePathNameW},
        System::{IO::DeviceIoControl, LibraryLoader::{GetModuleHandleW, GetProcAddress}},
    }};
    unsafe {
        let Ok(ntdll) = GetModuleHandleW(w!("ntdll.dll")) else { return false };
        if GetProcAddress(ntdll, s!("wine_get_version")).is_none() { return false; }
        // Open the entry itself, not its target, and inspect its actual tag and reparse data.
        let Ok(file) = OpenOptions::new().access_mode(0).share_mode(7)
            .custom_flags(0x02200000).open(path) else { return false };
        let handle = HANDLE(file.as_raw_handle());
        let mut tag = FILE_ATTRIBUTE_TAG_INFO::default();
        if GetFileInformationByHandleEx(handle, FileAttributeTagInfo, (&mut tag as *mut FILE_ATTRIBUTE_TAG_INFO).cast(),
            std::mem::size_of_val(&tag) as u32).is_err() || tag.ReparseTag != 0xa0000003 {
            return false;
        }
        let mut data = [0u8; 16384];
        let mut returned = 0;
        let result = DeviceIoControl(handle, 0x900a8, None, 0, Some(data.as_mut_ptr().cast()),
            data.len() as u32, Some(&mut returned), None);
        if result.err().is_none_or(|e| e.code() != ERROR_NOT_A_REPARSE_POINT.to_hresult()) {
            return false;
        }
        // Confirm that the OS reports this exact entry as a volume root, not merely a path on that volume.
        let name: Vec<u16> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        let mut volume = vec![0u16; 32768];
        if GetVolumePathNameW(PCWSTR(name.as_ptr()), &mut volume).is_err() { return false; }
        let Some(end) = volume.iter().position(|v| *v == 0) else { return false };
        let volume = std::path::PathBuf::from(std::ffi::OsString::from_wide(&volume[..end]));
        match (std::fs::canonicalize(path), std::fs::canonicalize(volume)) {
            (Ok(path), Ok(volume)) => path == volume && std::fs::metadata(path).is_ok_and(|m| m.is_dir()),
            _ => false,
        }
    }
}
#[cfg(not(windows))]
fn wine_volume_boundary(_: &Path) -> bool { false }

pub(crate) fn safe_directory(path: &Path) -> Result<(), String> {
    let meta = std::fs::symlink_metadata(path).map_err(|e| format!("{}: {e}", path.display()))?;
    if is_link(&meta) || !meta.is_dir() {
        return Err(format!(
            "{} is not a folder (a regular directory is required)",
            path.display()
        ));
    }
    Ok(())
}
pub(crate) fn safe_item(path: &Path) -> Result<(), String> {
    match std::fs::symlink_metadata(path) {
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e.to_string()),
        _ => {}
    }
    safe_directory(path)?;
    for (i, e) in std::fs::read_dir(path)
        .map_err(|e| e.to_string())?
        .enumerate()
    {
        if i >= 128 {
            return Err("portrait cache folder has more than 128 files".into());
        }
        let e = e.map_err(|e| e.to_string())?;
        let m = std::fs::symlink_metadata(e.path()).map_err(|e| e.to_string())?;
        if is_link(&m) || !m.is_file() || m.len() > 2 * 1024 * 1024 {
            return Err(format!(
                "{} is not a bounded regular cache file",
                e.path().display()
            ));
        }
    }
    Ok(())
}
/// Opens and takes the cache lock. Errors carry their status: CACHE_BUSY when another generator holds the lock,
/// BAD_REQUEST otherwise.
pub(crate) fn cache_lock(path: &Path) -> Result<File, (i32, String)> {
    let file = open_cache_lock(path).map_err(|e| (crate::BAD_REQUEST, e))?;
    file.try_lock()
        .map_err(|e| (crate::CACHE_BUSY, format!("another portrait generator owns this cache: {e}")))?;
    Ok(file)
}
fn open_cache_lock(path: &Path) -> Result<File, String> {
    if !path.is_absolute() || path.components().any(|c| matches!(c, Component::ParentDir)) {
        return Err("cache_dir must be absolute without parent traversal".into());
    }
    // Check existing parents before create_dir_all can follow a link and create files outside the cache tree.
    for ancestor in path.ancestors() {
        match std::fs::symlink_metadata(ancestor) {
            Ok(m) if !is_link(&m) && m.is_dir() => {}
            Ok(_) if ancestor != path && wine_volume_boundary(ancestor) => {}
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            _ => return Err("cache_dir and its parents must be regular directories".into()),
        }
    }
    std::fs::create_dir_all(path).map_err(|e| e.to_string())?;
    safe_directory(path)?;
    let lock = path.join(".kiln-portraits.lock");
    if let Ok(m) = std::fs::symlink_metadata(&lock) {
        if is_link(&m) || !m.is_file() {
            return Err("portrait lock is not a regular file".into());
        }
    }
    let mut options = OpenOptions::new();
    options.read(true).write(true).create(true).truncate(false);
    #[cfg(target_os = "linux")]
    {
        use std::os::unix::fs::OpenOptionsExt;
        options.custom_flags(0o400000);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.custom_flags(0x00200000);
    }
    let file = options.open(lock).map_err(|e| e.to_string())?;
    if is_link(&file.metadata().map_err(|e| e.to_string())?) {
        return Err("portrait lock is a link".into());
    }
    Ok(file)
}
pub(crate) fn check_budget(request: &Request) -> Result<(), String> {
    if request.items.len() > MAX_ITEMS {
        return Err("at most 32 portraits per request".into());
    }
    let pixels = request.items.iter().try_fold(0u64, |total, v| {
        let frames = v.get("frames").and_then(|n| n.as_u64()).unwrap_or(24);
        let size = v.get("size").and_then(|n| n.as_u64()).unwrap_or(256);
        total.checked_add(frames.checked_mul(size.checked_mul(size)?)?)
    });
    if pixels.is_none_or(|n| n > MAX_PIXELS) {
        return Err("portrait request exceeds 32 million output pixels".into());
    }
    Ok(())
}
/// Errors carry their status: CACHE_FULL when the cache is over its budget, BAD_REQUEST for an entry it refuses.
pub(crate) fn cache_budget(path: &Path, request: &Request) -> Result<(), (i32, String)> {
    let bad = |e: std::io::Error| (crate::BAD_REQUEST, e.to_string());
    let mut folders = 0usize;
    let mut bytes = 0u64;
    for (i, e) in std::fs::read_dir(path)
        .map_err(bad)?
        .enumerate()
    {
        if i >= 1024 {
            return Err((crate::CACHE_FULL, "portrait cache has too many entries".into()));
        }
        let e = e.map_err(bad)?;
        let meta = std::fs::symlink_metadata(e.path()).map_err(bad)?;
        if is_link(&meta) {
            return Err((crate::BAD_REQUEST, "portrait cache contains a link".into()));
        }
        if meta.is_dir() {
            folders += 1;
            safe_item(&e.path()).map_err(|e| (crate::BAD_REQUEST, e))?;
            for f in std::fs::read_dir(e.path()).map_err(bad)? {
                bytes = bytes.saturating_add(
                    f.map_err(bad)?
                        .metadata()
                        .map_err(bad)?
                        .len(),
                );
            }
        } else if meta.is_file() && meta.len() <= 2 * 1024 * 1024 {
            bytes = bytes.saturating_add(meta.len());
        } else {
            return Err((crate::BAD_REQUEST, "portrait cache contains an invalid entry".into()));
        }
    }
    let mut new = std::collections::BTreeSet::new();
    let mut pixels = 0u64;
    for item in &request.items {
        if let Some(key) = item.get("key").and_then(|v| v.as_str()) {
            if portraits::check_key(key).is_ok() && !path.join(key).exists() {
                new.insert(key.to_ascii_lowercase());
            }
        }
        let frames = item.get("frames").and_then(|n| n.as_u64()).unwrap_or(24);
        let size = item.get("size").and_then(|n| n.as_u64()).unwrap_or(256);
        pixels = pixels.saturating_add(frames.saturating_mul(size.saturating_mul(size)));
    }
    // Reserve twice raw RGBA size, covering PNG overhead and whole-folder staging without deleting old pictures.
    if folders + new.len() > 128
        || bytes.saturating_add(pixels.saturating_mul(8)) > 512 * 1024 * 1024
    {
        return Err((crate::CACHE_FULL, "portrait cache exceeds 128 folders or its 512 MiB generation budget".into()));
    }
    Ok(())
}
pub fn render_json(bytes: &[u8]) -> String {
    let result = std::panic::catch_unwind(|| {
        if bytes.len() > MAX_REQUEST {
            return portraits::Reply::failed(
                crate::BAD_REQUEST,
                "portrait request exceeds 256 KiB",
            );
        }
        let request: Request = match serde_json::from_slice(bytes) {
            Ok(r) => r,
            Err(e) => {
                return portraits::Reply::failed(
                    crate::BAD_REQUEST,
                    format!("invalid portrait request: {e}"),
                );
            }
        };
        let name = request
            .file_name
            .clone()
            .unwrap_or_else(|| "enshrouded".into());
        if name != "enshrouded" && name != "enshrouded_server" {
            return portraits::Reply::failed(
                crate::BAD_REQUEST,
                "file_name must name the game or dedicated server",
            );
        }
        if !Path::new(&request.game_dir).is_absolute() {
            return portraits::Reply::failed(crate::BAD_REQUEST, "game_dir must be absolute");
        }
        portraits::run(request, &name, portraits::game::Archive::open)
    });
    result
        .unwrap_or_else(|_| {
            portraits::Reply::failed(crate::RUN_FAILED, "portrait renderer panicked")
        })
        .to_json()
}

/// Bounded JSON in/out. `written` receives bytes including NUL. -1: invalid pointers/length, -2: reply buffer small.
/// The caller owns the buffers, keeps them valid for the call and uses the same DLL's process architecture.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kiln_eml_portraits(
    request: *const u8,
    length: usize,
    reply: *mut u8,
    capacity: usize,
    written: *mut usize,
) -> i32 {
    if request.is_null() || written.is_null() || length > MAX_REQUEST {
        return -1;
    }
    let text = render_json(unsafe { std::slice::from_raw_parts(request, length) });
    unsafe {
        *written = text.len() + 1;
    }
    if reply.is_null() || capacity < text.len() + 1 {
        return -2;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(text.as_ptr(), reply, text.len());
        *reply.add(text.len()) = 0;
    }
    0
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn cache_regular_paths_create_and_serialize() {
        let root = std::env::temp_dir().join(format!("kiln-portrait-regular-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let cache = root.join("new/cache");
        let first = cache_lock(&cache).unwrap();
        assert!(cache.join(".kiln-portraits.lock").is_file());
        assert_eq!(cache_lock(&cache).unwrap_err().0, crate::CACHE_BUSY, "contention is transient");
        drop(first);
        drop(cache_lock(&cache).unwrap());
        std::fs::remove_dir_all(root).unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn cache_windows_linked_parents_are_refused() {
        use std::os::windows::{ffi::OsStrExt, fs::OpenOptionsExt, io::AsRawHandle};
        use windows::Win32::{Foundation::HANDLE, System::IO::DeviceIoControl};
        let root = std::env::temp_dir().join(format!("kiln-portrait-win-links-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        let outside = root.join("outside");
        std::fs::create_dir_all(outside.join("existing")).unwrap();
        let symbolic = root.join("symbolic");
        std::os::windows::fs::symlink_dir(&outside, &symbolic).unwrap();
        let junction = root.join("junction");
        std::fs::create_dir(&junction).unwrap();
        let entry = OpenOptions::new().access_mode(0x40000000).share_mode(7)
            .custom_flags(0x02200000).open(&junction).unwrap();
        let target = std::fs::canonicalize(&outside).unwrap();
        let mut target: Vec<u16> = target.as_os_str().encode_wide().collect();
        assert_eq!(&target[..4], &[92, 92, 63, 92]); // \\?\ -> \??\ for the junction's substitute name
        target[1] = 63;
        let length = (target.len() * 2) as u16;
        let mut data = Vec::new();
        data.extend_from_slice(&0xa0000003u32.to_le_bytes());
        for value in [length + 12, 0, 0, length, length + 2, 0] {
            data.extend_from_slice(&value.to_le_bytes());
        }
        for value in target.into_iter().chain([0, 0]) { data.extend_from_slice(&value.to_le_bytes()); }
        let mut returned = 0;
        unsafe { DeviceIoControl(HANDLE(entry.as_raw_handle()), 0x900a4,
            Some(data.as_ptr().cast()), data.len() as u32, None, 0, Some(&mut returned), None).unwrap(); }
        drop(entry);
        for linked in [&symbolic, &junction] {
            // Ensure the fixture really is a Windows reparse link, not a transparent host-side alias.
            assert!(std::fs::symlink_metadata(linked).unwrap().file_type().is_symlink());
            assert!(!wine_volume_boundary(linked));
            assert!(cache_lock(&linked.join("existing")).is_err());
            assert!(!outside.join("existing/.kiln-portraits.lock").exists());
            assert!(cache_lock(&linked.join("new/cache")).is_err());
            assert!(!outside.join("new").exists());
            std::fs::remove_dir(linked).unwrap();
        }
        std::fs::remove_dir_all(root).unwrap();
    }

    #[test]
    fn bounded_input_and_work() {
        assert!(render_json(&vec![b'x'; MAX_REQUEST + 1]).contains("256 KiB"));
        let r:Request=serde_json::from_value(serde_json::json!({"game_dir":"/game","cache_dir":"/cache","items":vec![serde_json::json!({});33]})).unwrap();
        assert!(check_budget(&r).is_err());
        let r:Request=serde_json::from_value(serde_json::json!({"game_dir":"/game","cache_dir":"/cache","items":[{"frames":64,"size":512},{"frames":64,"size":512},{"frames":64,"size":512}]})).unwrap();
        assert!(check_budget(&r).is_err());
    }
    #[test]
    fn export_bounds_and_reply_buffer() {
        let request = b"{";
        let mut written = 0usize;
        unsafe {
            assert_eq!(
                kiln_eml_portraits(std::ptr::null(), 0, std::ptr::null_mut(), 0, &mut written),
                -1
            );
            assert_eq!(
                kiln_eml_portraits(
                    request.as_ptr(),
                    MAX_REQUEST + 1,
                    std::ptr::null_mut(),
                    0,
                    &mut written
                ),
                -1
            );
            assert_eq!(
                kiln_eml_portraits(
                    request.as_ptr(),
                    request.len(),
                    std::ptr::null_mut(),
                    0,
                    &mut written
                ),
                -2
            );
            assert!(written > 1);
            let mut bytes = vec![0u8; written];
            assert_eq!(
                kiln_eml_portraits(
                    request.as_ptr(),
                    request.len(),
                    bytes.as_mut_ptr(),
                    bytes.len(),
                    &mut written
                ),
                0
            );
            assert_eq!(bytes[written - 1], 0);
            let value: serde_json::Value = serde_json::from_slice(&bytes[..written - 1]).unwrap();
            assert_eq!(value["code"], crate::BAD_REQUEST);
        }
    }
    #[test]
    fn cache_growth_is_bounded() {
        let root = std::env::temp_dir().join(format!("kiln-portrait-quota-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        for i in 0..128 {
            std::fs::create_dir(root.join(format!("key{i}"))).unwrap();
        }
        let request: Request =
            serde_json::from_value(serde_json::json!({"items":[{"key":"new"}]})).unwrap();
        assert_eq!(cache_budget(&root, &request).unwrap_err().0, crate::CACHE_FULL, "over budget = cache full");
        std::fs::remove_dir(root.join("key127")).unwrap();
        assert!(cache_budget(&root, &request).is_ok());
        std::fs::remove_dir_all(root).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn cache_ancestors_are_not_followed_or_created_through_links() {
        use std::os::unix::fs::symlink;
        let dir = std::env::temp_dir().join(format!("kiln-portrait-parent-links-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("outside/existing")).unwrap();
        symlink(dir.join("outside"), dir.join("linked")).unwrap();
        assert!(cache_lock(&dir.join("linked/existing")).is_err());
        assert!(!dir.join("outside/existing/.kiln-portraits.lock").exists());
        assert!(cache_lock(&dir.join("linked/new/cache")).is_err());
        assert!(!dir.join("outside/new").exists());
        std::fs::remove_dir_all(dir).unwrap();
    }
    #[cfg(unix)]
    #[test]
    fn cache_symlinks_are_refused() {
        use std::os::unix::fs::symlink;
        let dir = std::env::temp_dir().join(format!("kiln-portrait-links-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(dir.join("cache")).unwrap();
        std::fs::create_dir(dir.join("outside")).unwrap();
        symlink(dir.join("outside"), dir.join("linked")).unwrap();
        assert_eq!(cache_lock(&dir.join("linked")).unwrap_err().0, crate::BAD_REQUEST, "a link is a lasting refusal");
        symlink(dir.join("outside"), dir.join("cache/wolf")).unwrap();
        assert!(safe_item(&dir.join("cache/wolf")).is_err());
        std::fs::create_dir(dir.join("cache/boar")).unwrap();
        symlink(dir.join("outside"), dir.join("cache/boar/index.json")).unwrap();
        assert!(safe_item(&dir.join("cache/boar")).is_err());
        let first = cache_lock(&dir.join("cache")).unwrap();
        assert!(cache_lock(&dir.join("cache")).is_err());
        drop(first);
        assert!(cache_lock(&dir.join("cache")).is_ok());
        std::fs::remove_dir_all(dir).unwrap();
    }
}
