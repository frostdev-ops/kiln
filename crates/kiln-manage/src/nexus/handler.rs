//! The launcher as the `nxm://` handler ("Mod Manager Download" on Nexus Mods), for Enshrouded links only. Opt-in from
//! Settings, never at install or start:
//! - **Linux:** `$XDG_DATA_HOME/applications/kiln-launcher-nxm.desktop` (`MimeType=x-scheme-handler/nxm;`,
//!   `NoDisplay=true`, `Exec="<launcher>" --nxm %u`; the AppImage's own path when run as one), made the default with
//!   `xdg-mime default`.
//! - **Windows:** `HKCU\Software\Classes\nxm` (`URL Protocol`, `shell\open\command` = `"<launcher>" --nxm "%1"`).
//!
//! The handler before Kiln (Vortex, Mod Organizer 2, another manager) is remembered in `<data>/nexus/handler.json`:
//! links for other games go to it unchanged, and turning the option off gives it back (Linux: `xdg-mime default
//! <its .desktop>`; Windows: its command written back, or Kiln's per-user key removed so a machine-wide handler shows
//! again). Kiln never touches another program's files, only its own `.desktop` file, the user's default for
//! `x-scheme-handler/nxm`, and its own per-user registry key.
//!
//! A second launcher process started for a link hands it to the running one through `<data>/nexus/inbox/` and exits
//! ([`acquire_instance`], [`queue_link`], [`take_links`]).

use crate::{Error, ErrorCode, Result};
use serde::{Deserialize, Serialize};
use std::path::{Path, PathBuf};

pub const DESKTOP_ID: &str = "kiln-launcher-nxm.desktop";
pub const MIME: &str = "x-scheme-handler/nxm";

/// The handler Kiln replaced.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", tag = "type")]
pub enum Previous {
    /// Linux: a desktop file id ("com.nexusmods.app.desktop", "modorganizer2-nxm-handler.desktop").
    Desktop { id: String },
    /// Windows: the `shell\open\command` it had, its `DefaultIcon`, and where it lived ("hkcu" or "hklm").
    Command { command: String, icon: Option<String>, scope: String },
}

impl Previous {
    /// A name for Settings: "Vortex", "modorganizer2-nxm-handler".
    pub fn label(&self) -> String {
        let raw = match self {
            Previous::Desktop { id } => id.trim_end_matches(".desktop").rsplit('.').next().unwrap_or(id).to_string(),
            Previous::Command { command, .. } => {
                let prog = command.trim().trim_start_matches('"');
                let prog = prog.split('"').next().unwrap_or(prog);
                let base = prog.rsplit(['\\', '/']).next().unwrap_or(prog);
                base.split(' ').next().unwrap_or(base).trim_end_matches(".exe").trim_end_matches(".EXE").to_string()
            }
        };
        if raw.to_lowercase().contains("vortex") { "Vortex".into() } else if raw.to_lowercase().contains("modorganizer") || raw.eq_ignore_ascii_case("nxmhandler") { "Mod Organizer 2".into() } else { raw }
    }
}

#[derive(Debug, Clone, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HandlerState {
    pub format: u32,
    pub registered: bool,
    #[serde(default)]
    pub previous: Option<Previous>,
    #[serde(default)]
    pub exe: Option<String>,
    #[serde(default)]
    pub registered_at: Option<String>,
}

/// What Settings shows.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct HandlerStatus {
    /// This platform can register a handler.
    pub supported: bool,
    /// The option is on (Kiln registered itself).
    pub registered: bool,
    /// The system's nxm handler is Kiln right now (another manager may have taken it back since).
    pub active: bool,
    /// The handler links for other games go to.
    pub previous: Option<String>,
}

/// The system side (tests use a fake).
pub trait HandlerPlatform {
    fn supported(&self) -> bool;
    /// The current nxm handler, unless it is Kiln's own.
    fn current(&self) -> Result<Option<Previous>>;
    fn is_ours(&self) -> bool;
    fn install(&self, exe: &Path) -> Result<()>;
    /// Gives the handler back (None: there was none before Kiln).
    fn restore(&self, previous: Option<&Previous>) -> Result<()>;
    fn forward(&self, previous: &Previous, url: &str) -> Result<()>;
}

fn state_path(root: &Path) -> PathBuf {
    root.join("handler.json")
}

pub fn state(root: &Path) -> HandlerState {
    crate::util::read_json::<HandlerState>(&state_path(root)).ok().flatten().unwrap_or_default()
}

pub fn status(root: &Path, p: &dyn HandlerPlatform) -> HandlerStatus {
    let s = state(root);
    HandlerStatus { supported: p.supported(), registered: s.registered, active: s.registered && p.is_ours(), previous: s.previous.as_ref().map(Previous::label) }
}

/// Turns the option on: remembers the current handler (unless it is Kiln's), then makes Kiln the handler. Again while
/// on: refreshes the launcher's path (an AppImage that moved) and keeps the remembered handler.
pub fn register(root: &Path, p: &dyn HandlerPlatform, exe: &Path) -> Result<HandlerStatus> {
    if !p.supported() {
        return Err(Error::new(ErrorCode::Unsupported, "Kiln can't take Nexus Mods links on this system."));
    }
    let mut s = state(root);
    let previous = if s.registered { s.previous.clone().or(p.current()?) } else { p.current()? };
    p.install(exe)?;
    s = HandlerState { format: 1, registered: true, previous, exe: Some(exe.display().to_string()), registered_at: Some(crate::util::now_rfc3339()) };
    crate::util::write_json_atomic(&state_path(root), &s)?;
    Ok(status(root, p))
}

/// Turns the option off: the handler before Kiln comes back.
pub fn unregister(root: &Path, p: &dyn HandlerPlatform) -> Result<HandlerStatus> {
    let s = state(root);
    if s.registered || p.is_ours() {
        p.restore(s.previous.as_ref())?;
    }
    crate::util::write_json_atomic(&state_path(root), &HandlerState { format: 1, ..Default::default() })?;
    Ok(status(root, p))
}

/// A link for another game: to the handler before Kiln.
pub fn forward(root: &Path, p: &dyn HandlerPlatform, url: &str) -> Result<String> {
    let s = state(root);
    let prev = s.previous.ok_or_else(|| Error::not_found("No other mod manager was set up for Nexus Mods links before Kiln."))?;
    p.forward(&prev, url)?;
    Ok(prev.label())
}

/// The platform's handler side.
pub fn platform() -> Box<dyn HandlerPlatform> {
    #[cfg(windows)]
    return Box::new(win::WinRegistry::default());
    #[cfg(all(unix, not(target_os = "macos")))]
    return Box::new(LinuxMime::from_env());
    #[cfg(target_os = "macos")]
    return Box::new(Unsupported);
}

#[allow(dead_code)]
struct Unsupported;
impl HandlerPlatform for Unsupported {
    fn supported(&self) -> bool {
        false
    }
    fn current(&self) -> Result<Option<Previous>> {
        Ok(None)
    }
    fn is_ours(&self) -> bool {
        false
    }
    fn install(&self, _: &Path) -> Result<()> {
        Err(Error::new(ErrorCode::Unsupported, "not on this system"))
    }
    fn restore(&self, _: Option<&Previous>) -> Result<()> {
        Ok(())
    }
    fn forward(&self, _: &Previous, _: &str) -> Result<()> {
        Err(Error::new(ErrorCode::Unsupported, "not on this system"))
    }
}

// ---------------------------------------------------------------- Linux

/// `xdg-mime` and the user's applications folder. `env` goes to every xdg-mime call (tests: temporary XDG dirs).
#[derive(Debug, Clone)]
pub struct LinuxMime {
    pub data_home: PathBuf,
    pub config_home: PathBuf,
    pub data_dirs: Vec<PathBuf>,
    pub env: Vec<(String, String)>,
}

impl LinuxMime {
    pub fn from_env() -> LinuxMime {
        let home = crate::util::home();
        let var = |k: &str| std::env::var_os(k).filter(|v| !v.is_empty()).map(PathBuf::from);
        let data_home = var("XDG_DATA_HOME").unwrap_or_else(|| home.join(".local/share"));
        let config_home = var("XDG_CONFIG_HOME").unwrap_or_else(|| home.join(".config"));
        let mut data_dirs: Vec<PathBuf> = std::env::var("XDG_DATA_DIRS").ok().filter(|v| !v.is_empty()).unwrap_or_else(|| "/usr/local/share:/usr/share".into())
            .split(':').filter(|s| !s.is_empty()).map(PathBuf::from).collect();
        data_dirs.push(data_home.join("flatpak/exports/share"));
        data_dirs.push(PathBuf::from("/var/lib/flatpak/exports/share"));
        LinuxMime { data_home, config_home, data_dirs, env: vec![] }
    }

    pub fn desktop_file(&self) -> PathBuf {
        self.data_home.join("applications").join(DESKTOP_ID)
    }

    fn xdg_mime(&self, args: &[&str]) -> Result<String> {
        let exe = crate::util::which("xdg-mime").ok_or_else(|| Error::new(ErrorCode::Unsupported, "xdg-mime is not installed (package xdg-utils)."))?;
        let mut c = std::process::Command::new(exe);
        c.args(args).envs(self.env.iter().map(|(k, v)| (k.as_str(), v.as_str()))).stdin(std::process::Stdio::null()).stderr(std::process::Stdio::null());
        let out = c.output().map_err(|e| Error::new(ErrorCode::Unsupported, format!("xdg-mime: {e}")))?;
        if !out.status.success() {
            return Err(Error::new(ErrorCode::Io, format!("xdg-mime {} failed", args.first().unwrap_or(&""))));
        }
        Ok(String::from_utf8_lossy(&out.stdout).trim().to_string())
    }

    fn refresh_db(&self) {
        if let Some(u) = crate::util::which("update-desktop-database") {
            let _ = std::process::Command::new(u).arg(self.data_home.join("applications")).envs(self.env.iter().map(|(k, v)| (k.as_str(), v.as_str())))
                .stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null()).status();
        }
    }

    /// Where a desktop id's file is (the user's folder first).
    pub fn find_desktop(&self, id: &str) -> Option<PathBuf> {
        if id.contains('/') || id.contains("..") {
            return None;
        }
        std::iter::once(&self.data_home).chain(self.data_dirs.iter()).map(|d| d.join("applications").join(id)).find(|p| p.is_file())
    }

    /// Drops Kiln's id from `x-scheme-handler/nxm` in the user's mimeapps.list (when there was no handler before).
    fn drop_default(&self) -> Result<()> {
        let f = self.config_home.join("mimeapps.list");
        let Ok(text) = std::fs::read_to_string(&f) else { return Ok(()) };
        let mut out = String::new();
        for line in text.lines() {
            if let Some(v) = line.strip_prefix("x-scheme-handler/nxm=") {
                let rest: Vec<&str> = v.split(';').filter(|x| !x.is_empty() && *x != DESKTOP_ID).collect();
                if !rest.is_empty() {
                    out.push_str(&format!("x-scheme-handler/nxm={};\n", rest.join(";")));
                }
                continue;
            }
            out.push_str(line);
            out.push('\n');
        }
        if out != text {
            crate::util::write_atomic(&f, out.as_bytes())?;
        }
        Ok(())
    }
}

/// A desktop entry's `Exec` value for a path (quoted per the Desktop Entry spec).
pub fn exec_quote(path: &str) -> String {
    let mut s = String::from("\"");
    for c in path.chars() {
        if matches!(c, '"' | '`' | '$' | '\\') {
            s.push('\\');
        }
        s.push(c);
    }
    s.push('"');
    s
}

/// Splits a desktop entry's `Exec` and fills its field codes with `url` (`%u %U %f %F`; `%i %c %k` dropped).
pub fn exec_argv(exec: &str, url: &str) -> Vec<String> {
    let mut args: Vec<String> = Vec::new();
    let mut cur = String::new();
    let mut quoted = false;
    let mut any = false;
    let mut chars = exec.chars().peekable();
    while let Some(c) = chars.next() {
        match c {
            '"' => {
                quoted = !quoted;
                any = true;
            }
            '\\' if quoted => {
                if let Some(n) = chars.next() {
                    cur.push(n);
                }
            }
            ' ' | '\t' if !quoted => {
                if any || !cur.is_empty() {
                    args.push(std::mem::take(&mut cur));
                    any = false;
                }
            }
            _ => cur.push(c),
        }
    }
    if any || !cur.is_empty() {
        args.push(cur);
    }
    let mut out = Vec::new();
    let mut used = false;
    for a in args {
        match a.as_str() {
            "%u" | "%U" | "%f" | "%F" => {
                out.push(url.to_string());
                used = true;
            }
            "%i" | "%c" | "%k" => {}
            _ => out.push(a.replace("%%", "%")),
        }
    }
    if !used {
        out.push(url.to_string());
    }
    out
}

fn desktop_exec(file: &Path) -> Option<String> {
    let text = std::fs::read_to_string(file).ok()?;
    let mut in_entry = false;
    for line in text.lines() {
        let l = line.trim();
        if l.starts_with('[') {
            in_entry = l == "[Desktop Entry]";
            continue;
        }
        if in_entry && let Some(v) = l.strip_prefix("Exec=") {
            return Some(v.to_string());
        }
    }
    None
}

impl HandlerPlatform for LinuxMime {
    fn supported(&self) -> bool {
        crate::util::which("xdg-mime").is_some()
    }

    fn current(&self) -> Result<Option<Previous>> {
        let id = self.xdg_mime(&["query", "default", MIME]).unwrap_or_default();
        let id = id.lines().next().unwrap_or("").trim().to_string();
        Ok((!id.is_empty() && id != DESKTOP_ID).then_some(Previous::Desktop { id }))
    }

    fn is_ours(&self) -> bool {
        self.xdg_mime(&["query", "default", MIME]).is_ok_and(|id| id.trim() == DESKTOP_ID) && self.desktop_file().is_file()
    }

    fn install(&self, exe: &Path) -> Result<()> {
        let entry = format!(
            "[Desktop Entry]\nType=Application\nName=Kiln Launcher (Nexus Mods links)\nComment=Mod Manager Download links for Enshrouded\n\
             Exec={} --nxm %u\nIcon=kiln-launcher\nTerminal=false\nNoDisplay=true\nMimeType={MIME};\n",
            exec_quote(&exe.display().to_string())
        );
        crate::util::write_atomic(&self.desktop_file(), entry.as_bytes())?;
        self.refresh_db();
        self.xdg_mime(&["default", DESKTOP_ID, MIME])?;
        Ok(())
    }

    fn restore(&self, previous: Option<&Previous>) -> Result<()> {
        match previous {
            Some(Previous::Desktop { id }) => {
                self.xdg_mime(&["default", id, MIME])?;
            }
            _ => self.drop_default()?,
        }
        let _ = std::fs::remove_file(self.desktop_file());
        self.refresh_db();
        Ok(())
    }

    fn forward(&self, previous: &Previous, url: &str) -> Result<()> {
        let Previous::Desktop { id } = previous else { return Err(Error::new(ErrorCode::Unsupported, "not a desktop entry")) };
        let file = self.find_desktop(id).ok_or_else(|| Error::not_found(format!("{id} is gone")))?;
        let exec = desktop_exec(&file).ok_or_else(|| Error::new(ErrorCode::Parse, format!("{id} has no Exec line")))?;
        let argv = exec_argv(&exec, url);
        let (prog, args) = argv.split_first().ok_or_else(|| Error::new(ErrorCode::Parse, format!("{id}: empty Exec")))?;
        std::process::Command::new(prog).args(args).stdin(std::process::Stdio::null()).stdout(std::process::Stdio::null()).stderr(std::process::Stdio::null())
            .spawn().map(|_| ()).map_err(|e| Error::new(ErrorCode::Unsupported, format!("starting {id}: {e}")))
    }
}

// ---------------------------------------------------------------- Windows

/// Fills a Windows command line's `%1` with the link (appended when there is none).
pub fn fill_command(command: &str, url: &str) -> String {
    if command.contains("%1") { command.replace("%1", url) } else { format!("{command} \"{url}\"") }
}

/// The program of a Windows command line and the rest (raw).
pub fn split_command(command: &str) -> (String, String) {
    let c = command.trim();
    if let Some(r) = c.strip_prefix('"') {
        let (p, rest) = r.split_once('"').unwrap_or((r, ""));
        (p.to_string(), rest.trim_start().to_string())
    } else {
        let (p, rest) = c.split_once(' ').unwrap_or((c, ""));
        (p.to_string(), rest.trim_start().to_string())
    }
}

#[cfg(windows)]
pub mod win {
    use super::*;
    use windows_sys::Win32::System::Registry::{
        HKEY, HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_WRITE, REG_OPTION_NON_VOLATILE, REG_SZ, RegCloseKey, RegCreateKeyExW, RegDeleteTreeW, RegSetValueExW,
    };

    const KEY: &str = r"Software\Classes\nxm";

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }

    fn set(root: HKEY, key: &str, name: Option<&str>, value: &str) -> Result<()> {
        let k = wide(key);
        let mut h: HKEY = std::ptr::null_mut();
        // SAFETY: NUL-terminated strings; h is closed below.
        unsafe {
            let rc = RegCreateKeyExW(root, k.as_ptr(), 0, std::ptr::null(), REG_OPTION_NON_VOLATILE, KEY_WRITE, std::ptr::null(), &mut h, std::ptr::null_mut());
            if rc != 0 {
                return Err(Error::new(ErrorCode::Permission, format!("registry {key}: error {rc}")));
            }
            let n = name.map(wide);
            let v = wide(value);
            let rc = RegSetValueExW(h, n.as_ref().map_or(std::ptr::null(), |n| n.as_ptr()), 0, REG_SZ, v.as_ptr().cast(), (v.len() * 2) as u32);
            RegCloseKey(h);
            if rc != 0 {
                return Err(Error::new(ErrorCode::Permission, format!("registry {key}: error {rc}")));
            }
        }
        Ok(())
    }

    #[derive(Debug, Default)]
    pub struct WinRegistry;

    impl WinRegistry {
        fn command_of(root: HKEY, base: &str) -> Option<String> {
            crate::steam::win::reg_string(root, &format!(r"{base}\shell\open\command"), "")
        }
        fn exe_lower() -> String {
            std::env::current_exe().map(|p| p.display().to_string().to_lowercase()).unwrap_or_default()
        }
    }

    impl HandlerPlatform for WinRegistry {
        fn supported(&self) -> bool {
            true
        }
        fn current(&self) -> Result<Option<Previous>> {
            let me = Self::exe_lower();
            let ours = |c: &str| c.to_lowercase().contains("--nxm") && (me.is_empty() || c.to_lowercase().contains(&me) || c.to_lowercase().contains("kiln-launcher"));
            if let Some(c) = Self::command_of(HKEY_CURRENT_USER, KEY) {
                if ours(&c) {
                    return Ok(None);
                }
                let icon = crate::steam::win::reg_string(HKEY_CURRENT_USER, &format!(r"{KEY}\DefaultIcon"), "");
                return Ok(Some(Previous::Command { command: c, icon, scope: "hkcu".into() }));
            }
            Ok(Self::command_of(HKEY_LOCAL_MACHINE, r"SOFTWARE\Classes\nxm").filter(|c| !ours(c)).map(|c| Previous::Command { command: c, icon: None, scope: "hklm".into() }))
        }
        fn is_ours(&self) -> bool {
            Self::command_of(HKEY_CURRENT_USER, KEY).is_some_and(|c| c.contains("--nxm") && c.to_lowercase().contains(&Self::exe_lower()))
        }
        fn install(&self, exe: &Path) -> Result<()> {
            let e = exe.display().to_string();
            set(HKEY_CURRENT_USER, KEY, None, "URL:NXM Protocol")?;
            set(HKEY_CURRENT_USER, KEY, Some("URL Protocol"), "")?;
            set(HKEY_CURRENT_USER, &format!(r"{KEY}\DefaultIcon"), None, &format!("\"{e}\",0"))?;
            set(HKEY_CURRENT_USER, &format!(r"{KEY}\shell\open\command"), None, &format!("\"{e}\" --nxm \"%1\""))
        }
        fn restore(&self, previous: Option<&Previous>) -> Result<()> {
            match previous {
                Some(Previous::Command { command, icon, scope }) if scope == "hkcu" => {
                    set(HKEY_CURRENT_USER, &format!(r"{KEY}\shell\open\command"), None, command)?;
                    if let Some(i) = icon {
                        set(HKEY_CURRENT_USER, &format!(r"{KEY}\DefaultIcon"), None, i)?;
                    }
                    Ok(())
                }
                _ => {
                    let k = wide(KEY);
                    // SAFETY: NUL-terminated key below HKCU (Kiln's own per-user key).
                    let rc = unsafe { RegDeleteTreeW(HKEY_CURRENT_USER, k.as_ptr()) };
                    if rc != 0 && rc != 2 {
                        return Err(Error::new(ErrorCode::Permission, format!("registry {KEY}: error {rc}")));
                    }
                    Ok(())
                }
            }
        }
        fn forward(&self, previous: &Previous, url: &str) -> Result<()> {
            use std::os::windows::process::CommandExt;
            let Previous::Command { command, .. } = previous else { return Err(Error::new(ErrorCode::Unsupported, "not a command")) };
            let (prog, rest) = split_command(&fill_command(command, url));
            let mut c = std::process::Command::new(prog);
            if !rest.is_empty() {
                c.raw_arg(rest);
            }
            c.spawn().map(|_| ()).map_err(|e| Error::new(ErrorCode::Unsupported, format!("starting the previous handler: {e}")))
        }
    }
}

// ---------------------------------------------------------------- one launcher, the link inbox

/// Held while this launcher runs: a second process for a link sees it is taken and hands the link over.
pub struct Instance {
    _file: std::fs::File,
}

/// Takes `<root>/instance.lock` (an OS file lock, released when the process ends, also on a crash). None: another
/// launcher holds it.
pub fn acquire_instance(root: &Path) -> Result<Option<Instance>> {
    std::fs::create_dir_all(root).map_err(crate::util::io_at(root, "creating"))?;
    let p = root.join("instance.lock");
    let f = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(&p).map_err(crate::util::io_at(&p, "opening"))?;
    match f.try_lock() {
        Ok(()) => Ok(Some(Instance { _file: f })),
        Err(std::fs::TryLockError::WouldBlock) => Ok(None),
        Err(std::fs::TryLockError::Error(e)) => Err(crate::util::io_at(&p, "locking")(e)),
    }
}

fn inbox(root: &Path) -> PathBuf {
    root.join("inbox")
}

/// Puts a link into the inbox (readable by the user only: it carries a download key).
pub fn queue_link(root: &Path, url: &str) -> Result<PathBuf> {
    #[derive(Serialize)]
    struct L<'a> {
        url: &'a str,
    }
    let name = format!("{:013}-{}.json", std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_millis()).unwrap_or(0),
        crate::hash::hex(&crate::util::random_bytes::<4>()?));
    let p = inbox(root).join(name);
    super::keystore::write_private(&p, &L { url })?;
    Ok(p)
}

/// Takes every queued link, oldest first (links older than a day are dropped unread).
pub fn take_links(root: &Path) -> Vec<String> {
    #[derive(Deserialize)]
    struct L {
        url: String,
    }
    let mut files: Vec<PathBuf> = std::fs::read_dir(inbox(root)).into_iter().flatten().flatten().map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|x| x == "json")).collect();
    files.sort();
    let mut out = Vec::new();
    for f in files {
        let fresh = std::fs::metadata(&f).and_then(|m| m.modified()).ok().and_then(|t| t.elapsed().ok()).is_some_and(|age| age.as_secs() < 86_400);
        if fresh && let Ok(Some(l)) = crate::util::read_json::<L>(&f) {
            out.push(l.url);
        }
        let _ = std::fs::remove_file(&f);
    }
    out
}
