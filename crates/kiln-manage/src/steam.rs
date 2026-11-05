//! Steam discovery, read-only (never write localconfig.vdf, config.vdf, launch options or compat tool mappings;
//! never start or stop Steam): roots (native, Flatpak, Snap, Windows registry), `steamapps/libraryfolders.vdf`,
//! `appmanifest_<id>.acf`, the compat tool mapped to an app (config.vdf CompatToolMapping), per-user launch options
//! (userdata/<id>/config/localconfig.vdf).
//!
//! `$KILN_STEAM_ROOTS` (a path list, `:` on Linux, `;` on Windows) replaces the platform search, for tests and odd
//! setups. Every reader has a pure `*_from_text` / `*_at` variant.

use crate::vdf::{self, Vdf};
use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SteamFlavor {
    Native,
    Flatpak,
    Snap,
    Windows,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SteamRoot {
    pub path: PathBuf,
    pub flavor: SteamFlavor,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Library {
    pub path: PathBuf,
    pub apps: Vec<u32>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppManifest {
    pub app_id: u32,
    pub name: String,
    /// `steamapps/common/<installdir>`
    pub install_dir: PathBuf,
    pub build_id: Option<String>,
    pub state_flags: u32,
    /// StateFlags says an update, validation or download is in progress: the installer refuses.
    pub updating: bool,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchOptions {
    pub steam_id: String,
    pub value: String,
}

/// Steam's `EAppState` bits that mean "Steam is (or must be) changing the game's files right now":
/// UpdateRequired 0x2, Locked 0x10, FilesMissing 0x20, FilesCorrupt 0x80, UpdateRunning 0x100, UpdatePaused 0x200,
/// UpdateStarted 0x400, Uninstalling 0x800, BackupRunning 0x1000, Reconfiguring 0x10000, Validating 0x20000,
/// AddingFiles 0x40000, Preallocating 0x80000, Downloading 0x100000, Staging 0x200000, Committing 0x400000,
/// UpdateStopping 0x800000. Not counted: FullyInstalled 0x4, Encrypted 0x8, AppRunning 0x40 (the game running is
/// checked through the process list).
pub const STATE_UPDATING_MASK: u32 = 0x2
    | 0x10
    | 0x20
    | 0x80
    | 0x100
    | 0x200
    | 0x400
    | 0x800
    | 0x1000
    | 0x10000
    | 0x20000
    | 0x40000
    | 0x80000
    | 0x100000
    | 0x200000
    | 0x400000
    | 0x800000;

pub const STATE_FULLY_INSTALLED: u32 = 0x4;

/// StateFlags means an update / validation / download is pending or running.
pub fn is_updating(state_flags: u32) -> bool {
    state_flags & STATE_UPDATING_MASK != 0
}

/// The flavor a root path suggests (Flatpak and Snap sandboxes by their well-known folders).
pub fn flavor_of(path: &Path) -> SteamFlavor {
    if cfg!(windows) {
        return SteamFlavor::Windows;
    }
    let s = path.to_string_lossy();
    if s.contains("/.var/app/com.valvesoftware.Steam/") {
        SteamFlavor::Flatpak
    } else if s.contains("/snap/steam/") {
        SteamFlavor::Snap
    } else {
        SteamFlavor::Native
    }
}

/// Canonical path without Windows' `\\?\` prefix (falls back to a lexical clean-up).
pub fn canon(p: &Path) -> PathBuf {
    let c = std::fs::canonicalize(p).unwrap_or_else(|_| p.components().collect());
    strip_verbatim(c)
}

/// `\\?\C:\x` -> `C:\x`, `\\?\UNC\s\x` -> `\\s\x` (the forms canonicalize returns on Windows).
pub fn strip_verbatim(p: PathBuf) -> PathBuf {
    let s = p.to_string_lossy();
    if let Some(rest) = s.strip_prefix(r"\\?\UNC\") {
        return PathBuf::from(format!(r"\\{rest}"));
    }
    if let Some(rest) = s.strip_prefix(r"\\?\") {
        return PathBuf::from(rest);
    }
    p
}

/// Two canonical paths name the same folder. Windows compares case-insensitively: the registry's SteamPath is
/// lower case (`c:/program files (x86)/steam`), and canonicalizing does not fix the case everywhere (Wine, some
/// network file systems).
pub fn same_path(a: &Path, b: &Path) -> bool {
    if cfg!(windows) {
        a == b || a.to_string_lossy().to_lowercase() == b.to_string_lossy().to_lowercase()
    } else {
        a == b
    }
}

fn is_root(p: &Path) -> bool {
    p.join("steamapps").is_dir()
}

fn push_root(out: &mut Vec<SteamRoot>, seen: &mut Vec<PathBuf>, path: PathBuf, flavor: SteamFlavor) {
    if !is_root(&path) {
        return;
    }
    let c = canon(&path);
    if seen.iter().any(|s| same_path(s, &c)) {
        return;
    }
    seen.push(c.clone());
    out.push(SteamRoot { path: c, flavor });
}

/// Linux candidates under `home`: native (`~/.local/share/Steam`, `~/.steam/steam`, `~/.steam/root`), Flatpak, Snap.
/// Only folders with a `steamapps/` count; symlinked duplicates are dropped (canonical paths).
pub fn roots_at(home: &Path) -> Vec<SteamRoot> {
    let mut out = Vec::new();
    let mut seen = Vec::new();
    let candidates: [(&str, SteamFlavor); 7] = [
        (".local/share/Steam", SteamFlavor::Native),
        (".steam/steam", SteamFlavor::Native),
        (".steam/root", SteamFlavor::Native),
        (".steam/debian-installation", SteamFlavor::Native),
        (".var/app/com.valvesoftware.Steam/.local/share/Steam", SteamFlavor::Flatpak),
        (".var/app/com.valvesoftware.Steam/data/Steam", SteamFlavor::Flatpak),
        ("snap/steam/common/.local/share/Steam", SteamFlavor::Snap),
    ];
    for (rel, flavor) in candidates {
        push_root(&mut out, &mut seen, home.join(rel), flavor);
    }
    out
}

/// Roots from a path list (`$KILN_STEAM_ROOTS`); the flavor comes from the path (`flavor_of`).
pub fn roots_from_list(list: &std::ffi::OsStr) -> Vec<SteamRoot> {
    let mut out = Vec::new();
    let mut seen = Vec::new();
    for p in std::env::split_paths(list).filter(|p| !p.as_os_str().is_empty()) {
        let flavor = flavor_of(&p);
        push_root(&mut out, &mut seen, p, flavor);
    }
    out
}

/// Every Steam installation on this machine.
pub fn roots() -> Result<Vec<SteamRoot>> {
    if let Some(list) = std::env::var_os("KILN_STEAM_ROOTS").filter(|v| !v.is_empty()) {
        return Ok(roots_from_list(&list));
    }
    #[cfg(windows)]
    {
        let mut out = Vec::new();
        let mut seen = Vec::new();
        for p in win::registry_roots() {
            push_root(&mut out, &mut seen, p, SteamFlavor::Windows);
        }
        for p in [r"C:\Program Files (x86)\Steam", r"C:\Program Files\Steam"] {
            push_root(&mut out, &mut seen, PathBuf::from(p), SteamFlavor::Windows);
        }
        Ok(out)
    }
    #[cfg(not(windows))]
    {
        let home = std::env::var_os("HOME").ok_or_else(|| Error::not_found("HOME is not set"))?;
        Ok(roots_at(Path::new(&home)))
    }
}

#[cfg(windows)]
pub(crate) mod win {
    use std::path::PathBuf;
    use windows_sys::Win32::System::Registry::{HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW};

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    /// A REG_SZ value (REG_EXPAND_SZ ones come back expanded: RegGetValueW expands them unless RRF_NOEXPAND is set).
    pub fn reg_string(root: HKEY, key: &str, value: &str) -> Option<String> {
        let (k, v) = (wide(key), wide(value));
        let mut buf = vec![0u16; 4096];
        let mut len = (buf.len() * 2) as u32;
        // SAFETY: buffer and length describe a writable u16 buffer; strings are NUL-terminated.
        let rc = unsafe {
            RegGetValueW(root, k.as_ptr(), v.as_ptr(), RRF_RT_REG_SZ, std::ptr::null_mut(), buf.as_mut_ptr().cast(), &mut len)
        };
        if rc != 0 {
            return None;
        }
        let n = (len as usize / 2).min(buf.len());
        let s = String::from_utf16_lossy(&buf[..n]);
        let s = s.trim_end_matches('\0').to_string();
        (!s.is_empty()).then_some(s)
    }

    /// HKCU\Software\Valve\Steam SteamPath (forward slashes), then HKLM (WOW6432Node) InstallPath.
    pub fn registry_roots() -> Vec<PathBuf> {
        let mut v = Vec::new();
        if let Some(p) = reg_string(HKEY_CURRENT_USER, r"Software\Valve\Steam", "SteamPath") {
            v.push(PathBuf::from(p.replace('/', "\\")));
        }
        for key in [r"SOFTWARE\WOW6432Node\Valve\Steam", r"SOFTWARE\Valve\Steam"] {
            if let Some(p) = reg_string(HKEY_LOCAL_MACHINE, key, "InstallPath") {
                v.push(PathBuf::from(p));
            }
        }
        v
    }

    /// The user's Saved Games folder (FOLDERID_SavedGames, which the user may have moved): `User Shell Folders`, then
    /// the legacy `Shell Folders` key, else `%USERPROFILE%\Saved Games`.
    pub fn saved_games() -> Option<PathBuf> {
        const ID: &str = "{4C5C32FF-BB9D-43b0-B5B4-2D72E54EAAA4}";
        for key in [r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"] {
            if let Some(p) = reg_string(HKEY_CURRENT_USER, key, ID).filter(|p| !p.contains('%')) {
                return Some(PathBuf::from(p));
            }
        }
        std::env::var_os("USERPROFILE").map(|h| PathBuf::from(h).join("Saved Games"))
    }
}

/// Library paths named in a libraryfolders.vdf (new format `"0" { "path" ... }` and the old `"1" "D:\\Lib"`).
pub fn parse_libraryfolders(text: &str) -> Result<Vec<(PathBuf, Vec<u32>)>> {
    let v = vdf::parse(text)?;
    let top = v.get("libraryfolders").ok_or_else(|| Error::new(ErrorCode::Parse, "libraryfolders.vdf has no \"libraryfolders\""))?;
    let mut out = Vec::new();
    for (k, node) in top.entries() {
        if !k.bytes().all(|b| b.is_ascii_digit()) {
            continue; // TimeNextStatsReport, ContentStatsID
        }
        match node {
            Vdf::Str(p) if !p.is_empty() => out.push((PathBuf::from(p), Vec::new())),
            Vdf::Map(_) => {
                let Some(p) = node.str_at(&["path"]).filter(|p| !p.is_empty()) else { continue };
                let apps = node.get("apps").map(|a| a.entries().iter().filter_map(|(id, _)| id.parse().ok()).collect()).unwrap_or_default();
                out.push((PathBuf::from(p), apps));
            }
            _ => {}
        }
    }
    Ok(out)
}

/// App ids with an `appmanifest_<id>.acf` in `<library>/steamapps/`.
fn manifest_ids(library: &Path) -> Vec<u32> {
    std::fs::read_dir(library.join("steamapps"))
        .into_iter()
        .flatten()
        .flatten()
        .filter_map(|e| {
            let n = e.file_name().to_string_lossy().into_owned();
            n.strip_prefix("appmanifest_")?.strip_suffix(".acf")?.parse().ok()
        })
        .collect()
}

/// The libraries listed in `<root>/steamapps/libraryfolders.vdf` (the root itself included).
pub fn libraries(root: &SteamRoot) -> Result<Vec<Library>> {
    let mut paths: Vec<PathBuf> = vec![root.path.clone()];
    for f in [root.path.join("steamapps/libraryfolders.vdf"), root.path.join("config/libraryfolders.vdf")] {
        let Ok(bytes) = std::fs::read(&f) else { continue };
        let listed = parse_libraryfolders(&String::from_utf8_lossy(&bytes))
            .map_err(|e| Error::new(e.code, format!("{}: {}", f.display(), e.message)))?;
        paths.extend(listed.into_iter().map(|(p, _)| p));
        break;
    }
    let mut out: Vec<Library> = Vec::new();
    let mut seen: Vec<PathBuf> = Vec::new();
    for (i, p) in paths.into_iter().enumerate() {
        if !p.join("steamapps").is_dir() {
            continue; // an unmounted drive or a removed library
        }
        let c = canon(&p);
        if let Some(k) = seen.iter().position(|s| same_path(s, &c)) {
            // the root listed again: libraryfolders.vdf's spelling wins (on Windows the registry's SteamPath is lower
            // case; Steam writes the folder's real case into the file, and canonicalizing does not fix it everywhere)
            if cfg!(windows) && i > 0 && k == 0 {
                out[0].path = c.clone();
                seen[0] = c;
            }
            continue;
        }
        seen.push(c.clone());
        let mut apps = manifest_ids(&c);
        apps.sort_unstable();
        apps.dedup();
        out.push(Library { path: c, apps });
    }
    Ok(out)
}

/// Parses an appmanifest's text; `library` is the library folder (for `install_dir`).
pub fn app_manifest_from_text(library: &Path, text: &str) -> Result<AppManifest> {
    let v = vdf::parse(text)?;
    let a = v.get("AppState").ok_or_else(|| Error::new(ErrorCode::Parse, "appmanifest has no \"AppState\""))?;
    let app_id: u32 = a
        .str_at(&["appid"])
        .and_then(|s| s.trim().parse().ok())
        .ok_or_else(|| Error::new(ErrorCode::Schema, "appmanifest has no numeric appid"))?;
    let dir = a.str_at(&["installdir"]).unwrap_or_default();
    let state_flags: u32 = a.str_at(&["StateFlags"]).and_then(|s| s.trim().parse().ok()).unwrap_or(0);
    let build_id = a.str_at(&["buildid"]).map(str::to_string).filter(|b| !b.is_empty() && b != "0");
    Ok(AppManifest {
        app_id,
        name: a.str_at(&["name"]).unwrap_or_default().to_string(),
        install_dir: library.join("steamapps").join("common").join(dir),
        build_id,
        state_flags,
        updating: is_updating(state_flags),
    })
}

/// `<library>/steamapps/appmanifest_<app_id>.acf`, None when the app is not in that library.
pub fn app_manifest(library: &Path, app_id: u32) -> Result<Option<AppManifest>> {
    let f = library.join("steamapps").join(format!("appmanifest_{app_id}.acf"));
    let bytes = match std::fs::read(&f) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(Error::from(e).with_detail(f.display().to_string())),
    };
    app_manifest_from_text(library, &String::from_utf8_lossy(&bytes))
        .map(Some)
        .map_err(|e| Error::new(e.code, format!("{}: {}", f.display(), e.message)))
}

/// The compat tool a config.vdf maps the app to (CompatToolMapping/<app>/name), None for none / empty.
pub fn compat_tool_from_text(text: &str, app_id: u32) -> Result<Option<String>> {
    let v = vdf::parse(text)?;
    let id = app_id.to_string();
    let name = v
        .path(&["InstallConfigStore", "Software", "Valve", "Steam", "CompatToolMapping", &id, "name"])
        .and_then(Vdf::as_str)
        .map(str::to_string)
        .filter(|s| !s.is_empty());
    Ok(name)
}

/// The compat tool Steam runs the app with (e.g. "proton_9", "GE-Proton10-3"), None for the default / native.
pub fn compat_tool(root: &SteamRoot, app_id: u32) -> Result<Option<String>> {
    let f = root.path.join("config/config.vdf");
    let bytes = match std::fs::read(&f) {
        Ok(b) => b,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.into()),
    };
    compat_tool_from_text(&String::from_utf8_lossy(&bytes), app_id)
}

/// LaunchOptions of the app in one user's localconfig.vdf (None when unset or empty).
pub fn launch_options_from_text(text: &str, app_id: u32) -> Result<Option<String>> {
    let v = vdf::parse(text)?;
    let id = app_id.to_string();
    let value = v
        .path(&["UserLocalConfigStore", "Software", "Valve", "Steam", "apps", &id, "LaunchOptions"])
        .and_then(Vdf::as_str)
        .map(str::to_string)
        .filter(|s| !s.trim().is_empty());
    Ok(value)
}

/// The app's launch options for every Steam user that set some.
pub fn launch_options(root: &SteamRoot, app_id: u32) -> Result<Vec<LaunchOptions>> {
    let mut out = Vec::new();
    let mut users: Vec<PathBuf> =
        std::fs::read_dir(root.path.join("userdata")).into_iter().flatten().flatten().map(|e| e.path()).collect();
    users.sort();
    for u in users {
        let Some(steam_id) = u.file_name().and_then(|n| n.to_str()).filter(|n| n.bytes().all(|b| b.is_ascii_digit()))
        else {
            continue;
        };
        let Ok(bytes) = std::fs::read(u.join("config/localconfig.vdf")) else { continue };
        // one broken user file must not hide the others
        if let Ok(Some(value)) = launch_options_from_text(&String::from_utf8_lossy(&bytes), app_id) {
            out.push(LaunchOptions { steam_id: steam_id.to_string(), value });
        }
    }
    Ok(out)
}

/// `<library>/steamapps/compatdata/<app_id>` (Proton prefix parent; the prefix is its `pfx/`).
pub fn compatdata(library: &Path, app_id: u32) -> PathBuf {
    library.join("steamapps").join("compatdata").join(app_id.to_string())
}

/// Every install of `app_id` in every library of `roots`: (root, library, manifest).
pub fn find_app(roots: &[SteamRoot], app_id: u32) -> Vec<(SteamRoot, Library, AppManifest)> {
    let mut out: Vec<(SteamRoot, Library, AppManifest)> = Vec::new();
    for r in roots {
        for lib in libraries(r).unwrap_or_default() {
            if out.iter().any(|(_, l, _)| same_path(&l.path, &lib.path)) {
                continue; // a library shared by two roots
            }
            if let Ok(Some(m)) = app_manifest(&lib.path, app_id) {
                out.push((r.clone(), lib, m));
            }
        }
    }
    out
}

/// The root whose libraries contain `library` (for launch options and compat tools of an install).
pub fn root_of_library(roots: &[SteamRoot], library: &Path) -> Option<SteamRoot> {
    let want = canon(library);
    roots.iter().find(|r| libraries(r).unwrap_or_default().iter().any(|l| same_path(&l.path, &want))).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn libraryfolders_both_formats() {
        let new = "\"libraryfolders\"\n{\n\"0\"\n{\n\"path\" \"/a\"\n\"apps\" { \"1203620\" \"1\" }\n}\n\"1\" { \"path\" \"/b\" }\n}\n";
        let v = parse_libraryfolders(new).unwrap();
        assert_eq!(v, vec![(PathBuf::from("/a"), vec![1203620]), (PathBuf::from("/b"), vec![])]);
        let old = "\"LibraryFolders\" { \"TimeNextStatsReport\" \"1\" \"ContentStatsID\" \"-1\" \"1\" \"D:\\\\Games\\\\Steam\" }";
        assert_eq!(parse_libraryfolders(old).unwrap(), vec![(PathBuf::from("D:\\Games\\Steam"), vec![])]);
    }

    #[test]
    fn state_flags() {
        assert!(!is_updating(4));
        assert!(is_updating(6));
        assert!(is_updating(4 | 0x100 | 0x400));
        assert!(is_updating(0x20000 | 4));
        assert!(!is_updating(4 | 0x40)); // running
    }
}
