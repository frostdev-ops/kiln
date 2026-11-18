//! "Open folder" / "Open" (`app_open_path`): what the desktop is handed for a path, and how.
//!
//! Windows Explorer opens the player's Documents folder for any path it cannot resolve: a `\\?\` verbatim prefix, a
//! path with `/` in it (`C:\Games\Enshrouded/kiln/plugins/perfdiag.dll`), a relative path, a file it is asked to open
//! as a folder. So the path is made absolute (joined onto the game folder when the front end sends a game-relative
//! one), written with `\` and without the verbatim prefix, and checked: a file is shown selected in its folder
//! (`explorer /select,"<file>"`), a missing path opens its nearest existing parent folder, nothing at all is an error
//! (never Documents). On Linux (also with the game under Proton: the paths are the host's) a file is shown with the
//! file manager's `org.freedesktop.FileManager1.ShowItems` when one answers, else its folder opens with xdg-open.

use std::path::{Path, PathBuf};

/// What the desktop opens.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Target {
    /// A folder: opened.
    Folder(PathBuf),
    /// A file: its folder opened with it selected.
    File(PathBuf),
}

/// `\\?\C:\x` -> `C:\x`, `\\?\UNC\srv\x` -> `\\srv\x`; `/` -> `\`; runs of `\` -> one (a UNC path keeps its leading
/// `\\`); `.` parts dropped, `..` taken back; no trailing `\` (except a drive root, `C:\`).
pub fn windows_form(s: &str) -> String {
    let s = s.trim().replace('/', "\\");
    let (lead, rest) = if let Some(r) = s.strip_prefix(r"\\?\UNC\") {
        (r"\\", r)
    } else if let Some(r) = s.strip_prefix(r"\\?\") {
        ("", r)
    } else if let Some(r) = s.strip_prefix(r"\\.\") {
        ("", r)
    } else if let Some(r) = s.strip_prefix(r"\\") {
        (r"\\", r)
    } else if let Some(r) = s.strip_prefix('\\') {
        ("\\", r)
    } else {
        ("", s.as_str())
    };
    let mut parts: Vec<&str> = Vec::new();
    for p in rest.split('\\') {
        match p {
            "" | "." => {}
            ".." if parts.last().is_some_and(|l| is_drive(l)) => {} // nothing above a drive root
            ".." if parts.last().is_some_and(|l| *l != "..") => {
                parts.pop();
            }
            p => parts.push(p),
        }
    }
    let mut out = format!("{lead}{}", parts.join("\\"));
    if parts.len() == 1 && lead.is_empty() && is_drive(parts[0]) {
        out.push('\\'); // `C:` alone is the drive's current folder, `C:\` its root
    }
    out
}

fn is_drive(p: &str) -> bool {
    let b = p.as_bytes();
    b.len() == 2 && b[0].is_ascii_alphabetic() && b[1] == b':'
}

/// A Windows path is absolute: `C:\x` or `\\server\share`.
pub fn windows_absolute(s: &str) -> bool {
    let b = s.as_bytes();
    s.starts_with(r"\\") || (b.len() >= 3 && b[0].is_ascii_alphabetic() && b[1] == b':' && b[2] == b'\\')
}

/// The path the front end means: `path`, joined onto `base` (the game folder) when it is relative, in the platform's
/// form (Windows: [`windows_form`]; elsewhere `.`/`..`/`//` cleaned lexically). None: no absolute path comes of it.
pub fn absolute(base: Option<&str>, path: &str, windows: bool) -> Option<PathBuf> {
    let path = path.trim();
    let base = base.map(str::trim).filter(|b| !b.is_empty());
    if windows {
        let p = windows_form(path);
        let full = if windows_absolute(&p) {
            p
        } else {
            let b = windows_form(base?);
            if p.is_empty() { b } else { windows_form(&format!("{b}\\{p}")) }
        };
        return windows_absolute(&full).then(|| PathBuf::from(full));
    }
    let p = Path::new(path);
    let full = if p.is_absolute() {
        p.to_path_buf()
    } else {
        let b = Path::new(base?);
        if path.is_empty() { b.to_path_buf() } else { b.join(p) }
    };
    let clean = unix_clean(&full);
    clean.is_absolute().then_some(clean)
}

fn unix_clean(p: &Path) -> PathBuf {
    use std::path::Component;
    let mut out = PathBuf::new();
    for c in p.components() {
        match c {
            Component::CurDir => {}
            Component::ParentDir => {
                if !out.pop() {
                    out.push(c);
                }
            }
            c => out.push(c),
        }
    }
    out
}

/// What to open for an absolute path: the file or folder itself, else the nearest existing parent folder. None:
/// nothing on the way up exists (a drive that is gone).
pub fn target(p: &Path) -> Option<Target> {
    match std::fs::metadata(p) {
        Ok(m) if m.is_dir() => return Some(Target::Folder(p.to_path_buf())),
        Ok(_) => return Some(Target::File(p.to_path_buf())),
        Err(_) => {}
    }
    p.ancestors().skip(1).find(|a| !a.as_os_str().is_empty() && a.is_dir()).map(|a| Target::Folder(a.to_path_buf()))
}

/// Explorer's command line after `explorer` (written raw: Explorer parses its own, `/select,"C:\a b\c.dll"`).
pub fn explorer_line(t: &Target) -> String {
    match t {
        // a drive root (`C:\`) unquoted: `"C:\"` would end in an escaped quote
        Target::Folder(p) if p.to_string_lossy().ends_with('\\') => p.display().to_string(),
        Target::Folder(p) => format!("\"{}\"", p.display()),
        Target::File(p) => format!("/select,\"{}\"", p.display()),
    }
}

/// `file://` URI of an absolute Unix path (the FileManager1 interface takes URIs).
pub fn file_uri(p: &Path) -> String {
    let mut out = String::from("file://");
    for &b in p.as_os_str().as_encoded_bytes() {
        if b.is_ascii_alphanumeric() || b"/-._~".contains(&b) {
            out.push(b as char);
        } else {
            out.push_str(&format!("%{b:02X}"));
        }
    }
    out
}

/// Opens `path` (relative ones under `base`) with the desktop; see the module docs. Never waits for the program.
pub fn open(base: Option<&str>, path: &str) -> Result<(), kiln_manage::Error> {
    use kiln_manage::{Error, ErrorCode};
    let shown = if path.trim().is_empty() { base.unwrap_or_default() } else { path };
    let abs = absolute(base, path, cfg!(windows)).ok_or_else(|| Error::not_found(format!("{shown} is not a folder Kiln can open")))?;
    let t = target(&abs).ok_or_else(|| Error::not_found(format!("{} does not exist", abs.display())))?;
    let failed = |e: std::io::Error| Error::new(ErrorCode::Unsupported, format!("could not open it: {e}"));
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        std::process::Command::new("explorer")
            .raw_arg(explorer_line(&t))
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null())
            .spawn()
            .map(|_| ())
            .map_err(failed)
    }
    #[cfg(not(windows))]
    {
        use std::process::Stdio;
        fn xdg(dir: &Path) -> std::io::Result<()> {
            kiln_manage::hostenv::command("xdg-open").arg(dir).stdin(Stdio::null()).stdout(Stdio::null()).stderr(Stdio::null()).spawn().map(|_| ())
        }
        match t {
            Target::Folder(dir) => xdg(&dir).map_err(failed),
            Target::File(file) => {
                let dir = file.parent().map(Path::to_path_buf).unwrap_or_else(|| PathBuf::from("/"));
                // the file selected in the file manager when one answers on the session bus; its folder otherwise
                std::thread::spawn(move || {
                    let shown = kiln_manage::hostenv::command("gdbus")
                        .args(["call", "--session", "--timeout", "5", "--dest", "org.freedesktop.FileManager1",
                               "--object-path", "/org/freedesktop/FileManager1", "--method", "org.freedesktop.FileManager1.ShowItems"])
                        .arg(format!("['{}']", file_uri(&file)))
                        .arg("")
                        .stdin(Stdio::null())
                        .stdout(Stdio::null())
                        .stderr(Stdio::null())
                        .status()
                        .is_ok_and(|s| s.success());
                    if !shown {
                        let _ = xdg(&dir);
                    }
                });
                Ok(())
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn windows_form_strips_verbatim_and_fixes_slashes() {
        assert_eq!(windows_form(r"\\?\C:\Games\Enshrouded\kiln\plugins"), r"C:\Games\Enshrouded\kiln\plugins");
        assert_eq!(windows_form(r"\\?\UNC\nas\games\Enshrouded"), r"\\nas\games\Enshrouded");
        assert_eq!(windows_form(r"C:\Games\Enshrouded/kiln/plugins/perfdiag.dll"), r"C:\Games\Enshrouded\kiln\plugins\perfdiag.dll");
        assert_eq!(windows_form("c:/program files (x86)/steam/"), r"c:\program files (x86)\steam");
        assert_eq!(windows_form(r"C:\Games\\Enshrouded\.\kiln\..\mods\"), r"C:\Games\Enshrouded\mods");
        assert_eq!(windows_form(r"\\nas\share\x"), r"\\nas\share\x");
        assert_eq!(windows_form("C:"), r"C:\");
        assert_eq!(windows_form(r"C:\.."), r"C:\");
    }

    #[test]
    fn windows_absolute_paths() {
        // the bug: game folder + "/" + the entry's game-relative path, a DLL in kiln/plugins
        assert_eq!(absolute(Some(r"C:\Games\Enshrouded"), "kiln/plugins/perfdiag.dll", true),
                   Some(PathBuf::from(r"C:\Games\Enshrouded\kiln\plugins\perfdiag.dll")));
        assert_eq!(absolute(Some(r"\\?\D:\SteamLibrary\steamapps\common\Enshrouded"), "mods/MyMod", true),
                   Some(PathBuf::from(r"D:\SteamLibrary\steamapps\common\Enshrouded\mods\MyMod")));
        // ShroudForge's mods folder outside the game: absolute, the game folder is not prefixed
        assert_eq!(absolute(Some(r"C:\Games\Enshrouded"), "E:/SF/mods", true), Some(PathBuf::from(r"E:\SF\mods")));
        // a Library folder (absolute, verbatim)
        assert_eq!(absolute(None, r"\\?\C:\Users\p\AppData\Roaming\kiln\library\x\1.0.0", true),
                   Some(PathBuf::from(r"C:\Users\p\AppData\Roaming\kiln\library\x\1.0.0")));
        // no path: the game folder itself
        assert_eq!(absolute(Some(r"C:\Games\Enshrouded\"), "", true), Some(PathBuf::from(r"C:\Games\Enshrouded")));
        // relative with nothing to anchor it, or empty: refused (Explorer would open Documents)
        assert_eq!(absolute(None, "kiln/plugins", true), None);
        assert_eq!(absolute(None, "", true), None);
        assert_eq!(absolute(Some("Enshrouded"), "kiln", true), None);
    }

    #[test]
    fn unix_absolute_paths() {
        let game = "/home/p/.local/share/Steam/steamapps/common/Enshrouded";
        assert_eq!(absolute(Some(game), "kiln/plugins/perfdiag.dll", false), Some(PathBuf::from(format!("{game}/kiln/plugins/perfdiag.dll"))));
        assert_eq!(absolute(Some(game), "/mnt/sf/mods", false), Some(PathBuf::from("/mnt/sf/mods")));
        assert_eq!(absolute(Some(&format!("{game}/")), "./mods//x/../y", false), Some(PathBuf::from(format!("{game}/mods/y"))));
        assert_eq!(absolute(Some(game), "", false), Some(PathBuf::from(game)));
        assert_eq!(absolute(None, "kiln/plugins", false), None);
        assert_eq!(absolute(None, "  ", false), None);
    }

    #[test]
    fn target_per_kind() {
        let root = std::env::temp_dir().join(format!("kiln-reveal-{}", std::process::id()));
        let plugins = root.join("kiln").join("plugins");
        std::fs::create_dir_all(&plugins).unwrap();
        std::fs::create_dir_all(root.join("mods").join("SomeEml")).unwrap();
        std::fs::write(plugins.join("perfdiag.dll"), b"MZ").unwrap();
        let game = root.to_str().unwrap();
        let t = |rel: &str| target(&absolute(Some(game), rel, cfg!(windows)).unwrap());
        // a hand-placed DLL in kiln/plugins: its folder, the DLL selected
        assert_eq!(t("kiln/plugins/perfdiag.dll"), Some(Target::File(plugins.join("perfdiag.dll"))));
        // a mod folder (EML, SF, EMF, Shroudtopia): the folder itself
        assert_eq!(t("mods/SomeEml"), Some(Target::Folder(root.join("mods").join("SomeEml"))));
        // gone since the scan: the nearest folder that is there, never somewhere else
        assert_eq!(t("kiln/plugins/gone.dll"), Some(Target::Folder(plugins.clone())));
        assert_eq!(t("mods/Gone/sub/x.pak"), Some(Target::Folder(root.join("mods"))));
        assert_eq!(t(""), Some(Target::Folder(root.clone())));
        let _ = std::fs::remove_dir_all(&root);
    }

    #[test]
    fn explorer_command_lines() {
        assert_eq!(explorer_line(&Target::File(PathBuf::from(r"C:\Games\Enshrouded\kiln\plugins\perfdiag.dll"))),
                   r#"/select,"C:\Games\Enshrouded\kiln\plugins\perfdiag.dll""#);
        assert_eq!(explorer_line(&Target::Folder(PathBuf::from(r"C:\Program Files\x"))), r#""C:\Program Files\x""#);
        assert_eq!(explorer_line(&Target::Folder(PathBuf::from(r"D:\"))), r"D:\");
    }

    #[test]
    fn file_uris() {
        assert_eq!(file_uri(Path::new("/home/p/Steam Library/kiln/plugins/perf'diag.dll")),
                   "file:///home/p/Steam%20Library/kiln/plugins/perf%27diag.dll");
    }
}
