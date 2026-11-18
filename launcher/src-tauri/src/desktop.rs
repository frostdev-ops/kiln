//! The launcher's window icon on Linux taskbars, docks and app menus.
//!
//! Wayland bars and docks (GNOME's dash, Hyprland bars such as Omarchy's and Waybar, KDE's taskbar) find a window's
//! icon through its app id: the `.desktop` file named after it (or one whose `StartupWMClass` is that id), then that
//! file's `Icon=` name in the icon theme. GTK 3 sends no icon of its own on Wayland.
//! - The app id: GTK uses GLib's program name, the executable's name unless set. [`set_app_id`] sets it to
//!   [`APP_ID`] (`kiln-launcher`) before GTK starts, so it never depends on how the launcher was started. Tauri's
//!   `enableGTKAppId` would make it `io.frostdev.kiln.launcher`, but it also makes GTK claim that name on the session
//!   bus, one instance only; the launcher hands nxm links between its own processes itself (commands/nexus.rs).
//! - The deb and rpm install `Kiln Launcher.desktop` (Tauri names it after the product), `StartupWMClass=kiln-launcher`,
//!   `Icon=kiln-launcher` and the hicolor icons 16 to 512 px plus scalable.
//! - An AppImage installs nothing. When the launcher runs as one, [`start`] writes the entry itself (on by default;
//!   Settings, Appearance, "Kiln Launcher in the app menu" removes it and remembers that): `kiln-launcher.desktop` in
//!   `$XDG_DATA_HOME/applications` (`Exec` and `TryExec` the AppImage's path, rewritten when the AppImage moved; menus
//!   hide the entry while that file is gone) and `kiln-launcher.png` / `.svg` in `$XDG_DATA_HOME/icons/hicolor`. It
//!   only ever writes those files, never a system folder, and never replaces a `kiln-launcher.desktop` it did not write
//!   unless the player turns the switch on (the [`MARK`] line tells them apart).
//! docs/kiln/launcher/app.md, "Taskbar icon and app menu".

use kiln_manage::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

/// The window's Wayland app id and X11 WM_CLASS name; the AppImage entry's file name and the icon's name.
pub const APP_ID: &str = "kiln-launcher";
pub const DESKTOP_ID: &str = "kiln-launcher.desktop";
/// The line that marks an entry Kiln wrote.
pub const MARK: &str = "X-Kiln-Desktop-Entry=1";

/// The hicolor sizes and their PNGs (the bundle's icons, tauri.conf.json `bundle.icon`).
pub const ICONS: &[(u32, &[u8])] = &[
    (16, include_bytes!("../icons/16x16.png")),
    (24, include_bytes!("../icons/24x24.png")),
    (32, include_bytes!("../icons/32x32.png")),
    (48, include_bytes!("../icons/48x48.png")),
    (64, include_bytes!("../icons/64x64.png")),
    (128, include_bytes!("../icons/128x128.png")),
    (256, include_bytes!("../icons/256x256.png")),
    (512, include_bytes!("../icons/icon.png")),
];
/// branding/logo/app-icon.svg
pub const SCALABLE: &[u8] = include_bytes!("../icons/icon.svg");

/// Windows: the AppUserModelID Tauri's installer gives the Start menu and desktop shortcuts (the bundle identifier).
pub const WINDOWS_APP_ID: &str = "io.frostdev.kiln.launcher";

/// Windows, before the window: the process takes the shortcuts' AppUserModelID, so a pinned shortcut and the
/// running window are one taskbar button, with the shortcut's (the exe's) icon. A start without the installer's
/// shortcuts (a copied exe) is unaffected.
#[cfg(windows)]
pub fn set_app_user_model_id() {
    #[link(name = "shell32")]
    unsafe extern "system" {
        fn SetCurrentProcessExplicitAppUserModelID(id: *const u16) -> i32;
    }
    let id: Vec<u16> = WINDOWS_APP_ID.encode_utf16().chain([0]).collect();
    // SAFETY: a NUL-terminated UTF-16 string that outlives the call; a failure only leaves the default id.
    unsafe {
        SetCurrentProcessExplicitAppUserModelID(id.as_ptr());
    }
}

/// GLib's program name, before GTK starts: the Wayland app id and X11 WM_CLASS are [`APP_ID`] whatever the
/// executable is called or however it was started (AppImage, deb, a copy, a symlink).
#[cfg(target_os = "linux")]
pub fn set_app_id() {
    webkit2gtk::glib::set_prgname(Some(APP_ID));
}

// ---------------------------------------------------------------- where

/// The user's applications and hicolor folders (tests: a temporary home).
#[derive(Debug, Clone)]
pub struct Places {
    pub home: PathBuf,
    pub data_home: PathBuf,
}

impl Places {
    /// `$XDG_DATA_HOME` (absolute only, per the spec), else `~/.local/share`.
    pub fn from_env() -> Places {
        let home = kiln_manage::util::home();
        let data_home = std::env::var_os("XDG_DATA_HOME")
            .map(PathBuf::from)
            .filter(|p| p.is_absolute())
            .unwrap_or_else(|| home.join(".local/share"));
        Places { home, data_home }
    }
    pub fn desktop_file(&self) -> PathBuf {
        self.data_home.join("applications").join(DESKTOP_ID)
    }
    fn hicolor(&self) -> PathBuf {
        self.data_home.join("icons/hicolor")
    }
    /// Every icon file and the bytes Kiln puts there.
    pub fn icons(&self) -> Vec<(PathBuf, &'static [u8])> {
        let h = self.hicolor();
        let mut v: Vec<_> = ICONS.iter().map(|(s, b)| (h.join(format!("{s}x{s}/apps/{APP_ID}.png")), *b)).collect();
        v.push((h.join(format!("scalable/apps/{APP_ID}.svg")), SCALABLE));
        v
    }
    /// Only a data folder in the user's home folder (never /usr/share or another system folder, also not with an odd
    /// XDG_DATA_HOME or HOME=/). A home under /var (/var/home/<user> on Fedora Atomic, Bazzite) is a home.
    fn check(&self) -> Result<()> {
        let home = kiln_manage::util::clean(&self.home);
        let d = kiln_manage::util::clean(&self.data_home);
        let dotted = d.components().any(|c| matches!(c, std::path::Component::ParentDir));
        if !home.is_absolute() || home.parent().is_none() || !d.is_absolute() || dotted || !d.starts_with(&home) {
            return Err(Error::new(ErrorCode::Refused, format!("{} is not in your home folder: Kiln writes no app menu entry there.", d.display())));
        }
        Ok(())
    }
}

// ---------------------------------------------------------------- the entry

/// A string value per the Desktop Entry spec (only `\` needs escaping in a path without control characters).
fn string_value(s: &str) -> String {
    s.replace('\\', "\\\\")
}

/// The entry for an AppImage at `appimage`.
pub fn entry(appimage: &Path) -> Result<String> {
    let path = appimage.to_str().ok_or_else(|| Error::invalid(format!("{}: the AppImage's path is not UTF-8", appimage.display())))?;
    if !appimage.is_absolute() || path.chars().any(char::is_control) {
        return Err(Error::invalid(format!("{path:?}: the AppImage's path must be absolute, without control characters")));
    }
    Ok(format!(
        "[Desktop Entry]\nType=Application\nVersion=1.5\nName=Kiln Launcher\nGenericName=Mod launcher\n\
         Comment=Mods for Enshrouded: install Kiln, manage mods, play, run servers\n\
         Exec={exec}\nTryExec={try_exec}\nIcon={APP_ID}\nStartupWMClass={APP_ID}\nTerminal=false\nCategories=Game;\n\
         Keywords=Enshrouded;Kiln;mods;modding;\n{MARK}\n",
        exec = kiln_manage::nexus::handler::exec_quote(path),
        try_exec = string_value(path),
    ))
}

/// The `kiln-launcher.desktop` there now.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Found {
    None,
    /// Kiln's (it has [`MARK`] in its group); its text.
    Ours(String),
    /// Someone else's: another AppImage tool's, the player's own.
    Foreign,
}

pub fn found(p: &Places) -> Found {
    let f = p.desktop_file();
    match std::fs::symlink_metadata(&f) {
        Err(_) => Found::None,
        Ok(m) if !m.is_file() => Found::Foreign,
        Ok(_) => match std::fs::read_to_string(&f) {
            Ok(t) if is_ours(&t) => Found::Ours(t),
            _ => Found::Foreign,
        },
    }
}

fn is_ours(text: &str) -> bool {
    let mut group = "";
    for l in text.lines().map(str::trim) {
        if l.starts_with('[') {
            group = l;
        } else if group == "[Desktop Entry]" && l == MARK {
            return true;
        }
    }
    false
}

/// What [`install`] did.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Done {
    /// Everything was there already, as it should be.
    Nothing,
    /// The entry is new (or replaced someone else's: `replace` was true).
    Written,
    /// Kiln's entry pointed somewhere else (the AppImage moved), or an icon was missing or old.
    Updated,
    /// Someone else's `kiln-launcher.desktop` is there and `replace` was false: nothing written.
    Foreign,
}

/// Writes the entry and the icons, or brings them up to date. Only what differs is written.
pub fn install(p: &Places, appimage: &Path, replace: bool) -> Result<Done> {
    p.check()?;
    let text = entry(appimage)?;
    let before = found(p);
    if before == Found::Foreign && !replace {
        return Ok(Done::Foreign);
    }
    let mut icons_changed = false;
    for (path, bytes) in p.icons() {
        if std::fs::read(&path).ok().as_deref() != Some(bytes) {
            replace_file(&path, bytes)?;
            icons_changed = true;
        }
    }
    if icons_changed {
        icon_cache(&p.hicolor());
    }
    Ok(match before {
        Found::Ours(t) if t == text => if icons_changed { Done::Updated } else { Done::Nothing },
        Found::Ours(_) => {
            replace_file(&p.desktop_file(), text.as_bytes())?;
            Done::Updated
        }
        Found::None | Found::Foreign => {
            replace_file(&p.desktop_file(), text.as_bytes())?;
            Done::Written
        }
    })
}

/// Removes Kiln's entry and its icons. Someone else's entry stays, and so do icons that are not Kiln's bytes (unless
/// Kiln's entry was there: then the icons at its paths are Kiln's, maybe an older version's). True: something went.
pub fn remove(p: &Places) -> Result<bool> {
    p.check()?;
    let ours = matches!(found(p), Found::Ours(_));
    let mut removed = false;
    if ours {
        let f = p.desktop_file();
        std::fs::remove_file(&f).map_err(kiln_manage::util::io_at(&f, "removing"))?;
        removed = true;
    }
    let known: Vec<&[u8]> = ICONS.iter().map(|(_, b)| *b).chain([SCALABLE]).collect();
    for (path, _) in p.icons() {
        let Ok(m) = std::fs::symlink_metadata(&path) else { continue };
        if !m.is_file() {
            continue;
        }
        if ours || std::fs::read(&path).is_ok_and(|b| known.contains(&b.as_slice())) {
            std::fs::remove_file(&path).map_err(kiln_manage::util::io_at(&path, "removing"))?;
            removed = true;
        }
    }
    if removed {
        icon_cache(&p.hicolor());
    }
    Ok(removed)
}

/// A file in a folder that is not Kiln's alone: a fresh temporary name, never through a link.
fn replace_file(path: &Path, bytes: &[u8]) -> Result<()> {
    if std::fs::symlink_metadata(path).is_ok_and(|m| m.is_dir()) {
        return Err(Error::new(ErrorCode::Refused, format!("{} is a folder", path.display())));
    }
    kiln_manage::util::write_atomic_exclusive(path, bytes)
}

/// GTK trusts `hicolor/icon-theme.cache` while it is newer than the folder: a new icon in a size folder would stay
/// unseen. The folder's time moves on (the cache is out of date, so it is not used), and an existing cache is rebuilt
/// when gtk-update-icon-cache is there. Kiln never creates a cache.
fn icon_cache(hicolor: &Path) {
    if let Ok(f) = std::fs::File::open(hicolor) {
        let _ = f.set_modified(std::time::SystemTime::now());
    }
    if hicolor.join("icon-theme.cache").is_file()
        && let Some(tool) = kiln_manage::util::which("gtk-update-icon-cache")
    {
        let _ = kiln_manage::hostenv::command(tool).args(["-q", "-t", "-f"]).arg(hicolor)
            .stdin(std::process::Stdio::null()).stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null()).status();
    }
}

// ---------------------------------------------------------------- the player's choice

/// `<data>/state/desktop-entry.json`: there when the player turned the entry off (Settings). Absent: on.
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Choice {
    format: u32,
    on: bool,
    at: String,
}

fn choice_path(d: &kiln_manage::paths::DataDirs) -> PathBuf {
    d.root.join("state").join("desktop-entry.json")
}

fn wanted(d: &kiln_manage::paths::DataDirs) -> bool {
    kiln_manage::util::read_json::<Choice>(&choice_path(d)).ok().flatten().is_none_or(|c| c.on)
}

/// What Settings shows: only for a launcher running as an AppImage on Linux.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct DesktopInfo {
    /// The player's choice (on unless turned off in Settings).
    pub on: bool,
    /// Kiln's entry is there and starts this AppImage.
    pub installed: bool,
    /// A `kiln-launcher.desktop` Kiln did not write is there; turning the switch on replaces it.
    pub foreign: bool,
    /// The entry's path.
    pub file: String,
}

pub fn info_at(p: &Places, appimage: &Path, on: bool) -> DesktopInfo {
    let f = found(p);
    let installed = matches!(&f, Found::Ours(t) if entry(appimage).is_ok_and(|e| &e == t));
    DesktopInfo { on, installed, foreign: f == Found::Foreign, file: p.desktop_file().display().to_string() }
}

/// The AppImage this launcher runs from (`$APPIMAGE`), on Linux.
fn appimage() -> Option<PathBuf> {
    if !cfg!(target_os = "linux") {
        return None;
    }
    std::env::var_os("APPIMAGE").filter(|a| !a.is_empty()).map(PathBuf::from)
}

/// Settings' state; none outside an AppImage.
pub fn info() -> Option<DesktopInfo> {
    let a = appimage()?;
    let on = crate::state::dirs().map(|d| wanted(&d)).unwrap_or(true);
    Some(info_at(&Places::from_env(), &a, on))
}

/// Every start (after the data folder is found): the entry is written or brought up to date, unless the player
/// turned it off. A start outside an AppImage does nothing.
pub fn start() {
    let Some(a) = appimage() else { return };
    let Ok(d) = crate::state::dirs() else { return };
    if !wanted(&d) {
        return;
    }
    let p = Places::from_env();
    let line = match install(&p, &a, false) {
        Ok(Done::Nothing) => return,
        Ok(Done::Written) => format!("app menu entry and icons written ({}, starts {})", p.desktop_file().display(), a.display()),
        Ok(Done::Updated) => format!("app menu entry brought up to date: it starts {}", a.display()),
        Ok(Done::Foreign) => format!("{} is not Kiln's: left as it is (Settings, Appearance replaces it)", p.desktop_file().display()),
        Err(e) => format!("app menu entry not written: {}", e.message),
    };
    crate::commands::app::launcher_log("desktop", &line);
}

/// Settings, "Kiln Launcher in the app menu": on writes the entry (replacing someone else's `kiln-launcher.desktop`),
/// off removes Kiln's entry and icons; the choice is kept for the next starts.
pub fn set(on: bool) -> Result<DesktopInfo> {
    let a = appimage().ok_or_else(|| Error::new(ErrorCode::Unsupported, "Only the Linux AppImage adds itself to the app menu: a package (deb, rpm) brings its own entry."))?;
    let d = crate::state::dirs()?;
    let p = Places::from_env();
    if on {
        install(&p, &a, true)?;
        let _ = std::fs::remove_file(choice_path(&d));
    } else {
        remove(&p)?;
        kiln_manage::util::write_json_atomic(&choice_path(&d), &Choice { format: 1, on: false, at: kiln_manage::util::now_rfc3339() })?;
    }
    crate::commands::app::launcher_log("desktop", &format!("Settings: app menu entry {}", if on { "on" } else { "off" }));
    Ok(info_at(&p, &a, on))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A temporary home with its ~/.local/share (never the real one).
    fn places(name: &str) -> (PathBuf, Places) {
        let root = std::env::temp_dir().join(format!("kiln-desktop-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&root);
        std::fs::create_dir_all(&root).unwrap();
        let p = Places { home: root.clone(), data_home: root.join(".local/share") };
        assert!(p.check().is_ok());
        (root, p)
    }

    #[test]
    fn the_entry_starts_the_appimage_with_kilns_icon_and_window_class() {
        let e = entry(Path::new("/home/player/Apps/Kiln Launcher_0.2.0_amd64.AppImage")).unwrap();
        assert!(e.starts_with("[Desktop Entry]\n"), "{e}");
        for line in [
            "Type=Application",
            "Name=Kiln Launcher",
            "Exec=\"/home/player/Apps/Kiln Launcher_0.2.0_amd64.AppImage\"",
            "TryExec=/home/player/Apps/Kiln Launcher_0.2.0_amd64.AppImage",
            "Icon=kiln-launcher",
            "StartupWMClass=kiln-launcher",
            "Categories=Game;",
            "Terminal=false",
            MARK,
        ] {
            assert!(e.lines().any(|l| l == line), "{line} missing in\n{e}");
        }
        // the Exec rules: quoted, $ ` " \ escaped, % doubled; TryExec only escapes the backslash
        let odd = entry(Path::new("/home/p/$x `y` \"z\" 100%\\a.AppImage")).unwrap();
        assert!(odd.contains("Exec=\"/home/p/\\\\$x \\\\`y\\\\` \\\\\"z\\\\\" 100%%\\\\\\\\a.AppImage\"\n"), "{odd}");
        assert!(odd.contains("TryExec=/home/p/$x `y` \"z\" 100%\\\\a.AppImage\n"), "{odd}");
        assert!(entry(Path::new("relative.AppImage")).is_err());
        assert!(entry(Path::new("/home/p/a\nExec=evil.AppImage")).is_err());
        // the window's app id is the entry's name and its StartupWMClass
        assert_eq!(DESKTOP_ID, format!("{APP_ID}.desktop"));
    }

    #[test]
    fn writes_the_entry_and_every_icon_then_nothing_until_the_appimage_moves() {
        let (root, p) = places("install");
        let a = Path::new("/home/player/Kiln.AppImage");
        assert_eq!(install(&p, a, false).unwrap(), Done::Written);
        let text = std::fs::read_to_string(p.desktop_file()).unwrap();
        assert_eq!(text, entry(a).unwrap());
        // the spec's checker, where it is installed (desktop-file-utils)
        if let Some(v) = kiln_manage::util::which("desktop-file-validate") {
            let out = std::process::Command::new(v).arg(p.desktop_file()).output().unwrap();
            assert!(out.status.success() && out.stdout.is_empty(), "{}{}", String::from_utf8_lossy(&out.stdout), String::from_utf8_lossy(&out.stderr));
        }
        assert_eq!(p.desktop_file(), p.data_home.join("applications/kiln-launcher.desktop"));
        for s in [16, 24, 32, 48, 64, 128, 256, 512] {
            let f = p.data_home.join(format!("icons/hicolor/{s}x{s}/apps/kiln-launcher.png"));
            assert_eq!(png_size(&std::fs::read(&f).unwrap()), Some((s, s)), "{}", f.display());
        }
        assert!(std::fs::read(p.data_home.join("icons/hicolor/scalable/apps/kiln-launcher.svg")).unwrap().starts_with(b"<svg"));
        assert_eq!(info_at(&p, a, true), DesktopInfo { on: true, installed: true, foreign: false, file: p.desktop_file().display().to_string() });
        // a second start: nothing to write
        assert_eq!(install(&p, a, false).unwrap(), Done::Nothing);
        // the AppImage moved: the entry follows it
        let moved = Path::new("/home/player/Games/Kiln.AppImage");
        assert!(!info_at(&p, moved, true).installed);
        assert_eq!(install(&p, moved, false).unwrap(), Done::Updated);
        assert_eq!(std::fs::read_to_string(p.desktop_file()).unwrap(), entry(moved).unwrap());
        // an icon deleted by hand comes back
        std::fs::remove_file(p.data_home.join("icons/hicolor/48x48/apps/kiln-launcher.png")).unwrap();
        assert_eq!(install(&p, moved, false).unwrap(), Done::Updated);
        // no temporary files left behind
        let left: Vec<_> = std::fs::read_dir(p.data_home.join("applications")).unwrap().flatten().map(|e| e.file_name()).collect();
        assert_eq!(left, vec![std::ffi::OsString::from(DESKTOP_ID)]);
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn someone_elses_entry_stays_unless_the_player_asks() {
        let (root, p) = places("foreign");
        let a = Path::new("/home/player/Kiln.AppImage");
        let theirs = "[Desktop Entry]\nType=Application\nName=Kiln Launcher\nExec=/home/player/kiln.AppImage --deck\n";
        std::fs::create_dir_all(p.desktop_file().parent().unwrap()).unwrap();
        std::fs::write(p.desktop_file(), theirs).unwrap();
        assert_eq!(found(&p), Found::Foreign);
        assert_eq!(install(&p, a, false).unwrap(), Done::Foreign);
        assert_eq!(std::fs::read_to_string(p.desktop_file()).unwrap(), theirs);
        assert!(info_at(&p, a, true).foreign);
        // removal leaves theirs too
        remove(&p).unwrap();
        assert_eq!(std::fs::read_to_string(p.desktop_file()).unwrap(), theirs);
        // the mark outside [Desktop Entry] does not count
        std::fs::write(p.desktop_file(), format!("[Desktop Action x]\n{MARK}\n[Desktop Entry]\nName=x\n")).unwrap();
        assert_eq!(found(&p), Found::Foreign);
        // the switch replaces it
        assert_eq!(install(&p, a, true).unwrap(), Done::Written);
        assert!(matches!(found(&p), Found::Ours(_)));
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn removal_takes_kilns_files_only() {
        let (root, p) = places("remove");
        let a = Path::new("/home/player/Kiln.AppImage");
        install(&p, a, false).unwrap();
        // another app's icon in the same folders stays
        let other = p.data_home.join("icons/hicolor/48x48/apps/other-app.png");
        std::fs::write(&other, b"x").unwrap();
        assert!(remove(&p).unwrap());
        assert_eq!(found(&p), Found::None);
        for (f, _) in p.icons() {
            assert!(!f.exists(), "{}", f.display());
        }
        assert!(other.is_file());
        assert!(!remove(&p).unwrap(), "nothing left to remove");
        // without Kiln's entry, only icons with Kiln's bytes go
        let mine = p.data_home.join("icons/hicolor/32x32/apps/kiln-launcher.png");
        std::fs::write(&mine, ICONS[2].1).unwrap();
        let changed = p.data_home.join("icons/hicolor/64x64/apps/kiln-launcher.png");
        std::fs::create_dir_all(changed.parent().unwrap()).unwrap();
        std::fs::write(&changed, b"someone else's").unwrap();
        assert!(remove(&p).unwrap());
        assert!(!mine.exists() && changed.is_file());
        std::fs::remove_dir_all(&root).unwrap();
    }

    #[test]
    fn never_a_system_folder() {
        for (home, d) in [("/home/p", "/usr/share"), ("/home/p", "/usr/local/share"), ("/home/p", "/etc"), ("/home/p", "/"),
            ("/home/p", "relative/share"), ("/home/p", "/home/p/../../usr/share"), ("/home/p", "/home/other/.local/share"),
            ("/", "/usr/share"), ("", "/usr/share"), ("relative", "relative/.local/share")] {
            let p = Places { home: PathBuf::from(home), data_home: PathBuf::from(d) };
            assert!(p.check().is_err(), "{home} {d}");
            assert!(install(&p, Path::new("/home/p/K.AppImage"), true).is_err(), "{d}");
            assert!(remove(&p).is_err(), "{d}");
        }
        for (home, d) in [("/home/p", "/home/p/.local/share"), ("/var/home/p", "/var/home/p/.local/share"), ("/home/deck", "/home/deck/xdg")] {
            assert!(Places { home: PathBuf::from(home), data_home: PathBuf::from(d) }.check().is_ok(), "{home} {d}");
        }
    }

    /// Width and height from a PNG's IHDR.
    fn png_size(b: &[u8]) -> Option<(u32, u32)> {
        (b.len() > 24 && b.starts_with(b"\x89PNG\r\n\x1a\n") && &b[12..16] == b"IHDR")
            .then(|| (u32::from_be_bytes(b[16..20].try_into().unwrap()), u32::from_be_bytes(b[20..24].try_into().unwrap())))
    }

    #[test]
    fn the_bundles_icons_are_the_sizes_their_names_say() {
        for (s, b) in ICONS {
            assert_eq!(png_size(b), Some((*s, *s)), "{s} px");
        }
        let conf: serde_json::Value = serde_json::from_str(include_str!("../tauri.conf.json")).unwrap();
        let list: Vec<&str> = conf["bundle"]["icon"].as_array().unwrap().iter().map(|v| v.as_str().unwrap()).collect();
        let dir = Path::new(env!("CARGO_MANIFEST_DIR"));
        let mut sizes = vec![];
        for f in list.iter().filter(|f| f.ends_with(".png")) {
            let (w, h) = png_size(&std::fs::read(dir.join(f)).unwrap()).unwrap();
            assert_eq!(w, h, "{f}");
            // Tauri puts an "@2x" file in a hicolor/<w>x<h>@2 folder no theme reads
            assert!(!f.contains("@2x"), "{f}");
            sizes.push(w);
        }
        // Tauri makes the first PNG the Linux window icon (X11 _NET_WM_ICON, GNOME without an entry)
        assert_eq!(sizes.first(), Some(&256), "{list:?}");
        sizes.sort();
        assert_eq!(sizes, vec![16, 24, 32, 48, 64, 128, 256, 512]);
        assert!(list.contains(&"icons/icon.ico"));
        assert_eq!(conf["bundle"]["linux"]["deb"]["files"]["/usr/share/icons/hicolor/scalable/apps/kiln-launcher.svg"], "icons/icon.svg");
        assert_eq!(conf["bundle"]["linux"]["rpm"]["files"]["/usr/share/icons/hicolor/scalable/apps/kiln-launcher.svg"], "icons/icon.svg");
        // Tauri's deb and rpm entry: Icon= and StartupWMClass= are the main binary's name, the window's app id
        assert_eq!(conf["mainBinaryName"], APP_ID);
        // the NSIS shortcuts' AppUserModelID is the identifier
        assert_eq!(conf["identifier"], WINDOWS_APP_ID);
    }

    /// icon.ico, the Windows exe's and installer's icon: every size Windows asks for.
    #[test]
    fn the_windows_icon_has_every_size() {
        let ico = include_bytes!("../icons/icon.ico");
        assert_eq!(&ico[..4], &[0, 0, 1, 0]);
        let n = u16::from_le_bytes([ico[4], ico[5]]) as usize;
        let mut sizes: Vec<u32> = (0..n).map(|i| match ico[6 + i * 16] { 0 => 256, w => w as u32 }).collect();
        sizes.sort();
        assert_eq!(sizes, vec![16, 24, 32, 48, 64, 256]);
    }
}
