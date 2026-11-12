//! Wine runner selection and the environment every Wine call of an instance gets.
//!
//! Decisions (docs/kiln/linux-server.md): GE-Proton's bundled wine called directly is the default, distro wine works
//! the same; no `proton` script, no Steam client. `WINEDLLOVERRIDES="mshtml=;dinput8=n,b"`. Never disable `mscoree`:
//! the .NET runtime opens assemblies through it under Wine and every managed load fails with 0x8007007E without it.
use crate::server::instance::Instance;
use crate::util::{self, is_executable};
use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};
use std::process::Command;

fn bad(message: String) -> Error {
    Error::invalid(message)
}

/// `mshtml=` stops the Gecko prompt; `dinput8=n,b` loads Kiln's proxy from the server folder.
pub const DLL_OVERRIDES: &str = "mshtml=;dinput8=n,b";

/// Inherited variables that would mix another runner, a display or Proton state into the instance.
const SCRUB: &[&str] = &[
    "DISPLAY",
    "WAYLAND_DISPLAY",
    "WAYLAND_SOCKET",
    "WINESERVER",
    "WINELOADER",
    "WINEDLLPATH",
    "WINEARCH",
    "WINEDLLOVERRIDES",
    "WINEDEBUG",
    "STEAM_COMPAT_DATA_PATH",
    "STEAM_COMPAT_CLIENT_INSTALL_PATH",
    "KILN_DISABLE",
];

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum WineSpec {
    /// Newest GE-Proton found in the compatibility-tool folders.
    GeLatest,
    /// A GE-Proton folder name (`GE-Proton11-11-x86_64`) or a GE-Proton folder path.
    Ge(String),
    /// `wine` and `wineserver` from `$PATH`.
    System,
    /// A wine binary (its `wineserver` must sit next to it), a GE-Proton folder or a wine `bin/` folder.
    Path(PathBuf),
}

impl WineSpec {
    pub fn parse(s: &str, home: &Path) -> Result<Self> {
        let s = s.trim();
        Ok(match s {
            "" => return Err(bad("wine is empty (ge, ge:<GE-Proton folder>, system or a path)".into())),
            "ge" => WineSpec::GeLatest,
            "system" => WineSpec::System,
            _ if s.starts_with("ge:") => {
                let v = &s[3..];
                if v.is_empty() {
                    return Err(bad("wine = \"ge:\" needs a GE-Proton folder name or path".into()));
                }
                WineSpec::Ge(v.to_string())
            }
            _ => {
                let p = util::expand(s, home);
                if !p.is_absolute() {
                    return Err(bad(format!("wine must be ge, ge:<GE-Proton folder>, system or an absolute path, not {s:?}")));
                }
                WineSpec::Path(p)
            }
        })
    }

    pub fn to_config(&self) -> String {
        match self {
            WineSpec::GeLatest => "ge".into(),
            WineSpec::Ge(v) => format!("ge:{v}"),
            WineSpec::System => "system".into(),
            WineSpec::Path(p) => p.display().to_string(),
        }
    }
}

#[derive(Debug, Clone)]
pub struct Runner {
    pub wine: PathBuf,
    pub wineserver: PathBuf,
    pub label: String,
}

impl Runner {
    /// `wine --version` (does not touch a prefix).
    pub fn version(&self) -> Option<String> {
        let out = crate::hostenv::command(&self.wine).arg("--version").env_remove("DISPLAY").output().ok()?;
        let v = String::from_utf8_lossy(&out.stdout).trim().to_string();
        (!v.is_empty()).then_some(v)
    }

    pub fn command(&self, inst: &Instance) -> Command {
        let mut c = crate::hostenv::command(&self.wine);
        apply_env(&mut c, inst);
        c
    }

    pub fn wineserver(&self, inst: &Instance) -> Command {
        let mut c = crate::hostenv::command(&self.wineserver);
        apply_env(&mut c, inst);
        c
    }
}

/// Folders searched for GE-Proton (Steam's compatibility-tool folders; GE-Proton itself needs no Steam).
pub fn ge_roots(home: &Path) -> Vec<PathBuf> {
    vec![
        home.join(".local/share/Steam/compatibilitytools.d"),
        home.join(".steam/root/compatibilitytools.d"),
        home.join(".steam/steam/compatibilitytools.d"),
        PathBuf::from("/usr/share/steam/compatibilitytools.d"),
    ]
}

/// Version key of a GE-Proton folder name: `GE-Proton11-11-x86_64` -> [11, 11].
pub fn ge_version(name: &str) -> Option<Vec<u32>> {
    let rest = name.strip_prefix("GE-Proton")?;
    let v: Vec<u32> = rest.split('-').map_while(|p| p.parse().ok()).collect();
    (!v.is_empty()).then_some(v)
}

/// Installed GE-Proton builds: (folder name, folder), newest first.
pub fn ge_installs(home: &Path) -> Vec<(String, PathBuf)> {
    let mut out: Vec<(String, PathBuf)> = Vec::new();
    for root in ge_roots(home) {
        let Ok(rd) = std::fs::read_dir(&root) else { continue };
        for e in rd.flatten() {
            let name = e.file_name().to_string_lossy().to_string();
            let dir = e.path();
            if ge_version(&name).is_none() || !is_executable(&dir.join("files/bin/wine")) {
                continue;
            }
            let canon = dir.canonicalize().unwrap_or(dir);
            if !out.iter().any(|(_, d)| *d == canon) {
                out.push((name, canon));
            }
        }
    }
    out.sort_by(|a, b| ge_version(&b.0).cmp(&ge_version(&a.0)).then(b.0.cmp(&a.0)));
    out
}

fn from_bin(bin: &Path, label: String) -> Result<Runner> {
    let wine = bin.join("wine");
    let wineserver = bin.join("wineserver");
    if !is_executable(&wine) {
        return Err(not_found(format!("{} is not an executable wine", wine.display())));
    }
    if !is_executable(&wineserver) {
        return Err(not_found(format!("no wineserver next to {}", wine.display())));
    }
    Ok(Runner { wine, wineserver, label })
}

fn from_ge_dir(dir: &Path, label: String) -> Result<Runner> {
    from_bin(&dir.join("files/bin"), label)
}

fn not_found(message: String) -> Error {
    Error::new(ErrorCode::NotFound, message)
}

pub fn resolve(spec: &WineSpec, home: &Path) -> Result<Runner> {
    match spec {
        WineSpec::GeLatest => {
            let (name, dir) = ge_installs(home).into_iter().next().ok_or_else(|| {
                not_found(format!(
                    "no GE-Proton found in {} (install one, or use wine = \"system\" or a path)",
                    ge_roots(home).iter().map(|p| p.display().to_string()).collect::<Vec<_>>().join(", ")
                ))
            })?;
            from_ge_dir(&dir, format!("ge ({name})"))
        }
        WineSpec::Ge(v) if v.contains('/') => {
            let dir = util::expand(v, home);
            from_ge_dir(&dir, format!("ge:{}", dir.display()))
        }
        WineSpec::Ge(v) => {
            let all = ge_installs(home);
            let Some((_, dir)) = all.iter().find(|(n, _)| n == v) else {
                let have: Vec<&str> = all.iter().map(|(n, _)| n.as_str()).collect();
                return Err(not_found(format!(
                    "GE-Proton {v} not found (installed: {})",
                    if have.is_empty() { "none".into() } else { have.join(", ") }
                )));
            };
            from_ge_dir(dir, format!("ge:{v}"))
        }
        WineSpec::System => {
            let wine = util::which("wine").ok_or_else(|| not_found("wine = \"system\" but there is no wine on PATH".into()))?;
            let wineserver = util::which("wineserver")
                .or_else(|| {
                    ["/usr/lib/wine/wineserver", "/usr/lib/wine/wineserver64", "/usr/lib64/wine/wineserver"]
                        .iter()
                        .map(PathBuf::from)
                        .find(|p| is_executable(p))
                })
                .ok_or_else(|| not_found("wine = \"system\" but there is no wineserver on PATH".into()))?;
            Ok(Runner { label: format!("system ({})", wine.display()), wine, wineserver })
        }
        WineSpec::Path(p) => {
            if p.is_dir() {
                if p.join("files/bin/wine").exists() {
                    return from_ge_dir(p, p.display().to_string());
                }
                return from_bin(p, p.display().to_string());
            }
            let bin = p.parent().ok_or_else(|| bad("wine path has no folder".into()))?;
            if !is_executable(p) {
                return Err(not_found(format!("{} is not executable", p.display())));
            }
            let wineserver = bin.join("wineserver");
            if !is_executable(&wineserver) {
                return Err(not_found(format!("no wineserver next to {}", p.display())));
            }
            Ok(Runner { wine: p.clone(), wineserver, label: p.display().to_string() })
        }
    }
}

/// Variables set for every Wine call of the instance (in order; `[env]` entries override the defaults).
pub fn env(inst: &Instance) -> Vec<(String, String)> {
    let mut v: Vec<(String, String)> = vec![
        ("WINEPREFIX".into(), inst.prefix.display().to_string()),
        ("WINEDEBUG".into(), "-all".into()),
        ("WINEDLLOVERRIDES".into(), DLL_OVERRIDES.into()),
        ("KILN_INSTANCE".into(), inst.name.clone()),
    ];
    for (k, val) in &inst.env {
        match v.iter_mut().find(|(n, _)| n == k) {
            Some(slot) => slot.1 = val.clone(),
            None => v.push((k.clone(), val.clone())),
        }
    }
    v
}

pub fn effective(inst: &Instance, key: &str) -> Option<String> {
    env(inst).into_iter().find(|(k, _)| k == key).map(|(_, v)| v)
}

pub fn apply_env(cmd: &mut Command, inst: &Instance) {
    for k in SCRUB {
        cmd.env_remove(k);
    }
    // no display at all (crate::server::run::headless): with WAYLAND_DISPLAY merely unset, libwayland connects to
    // $XDG_RUNTIME_DIR/wayland-0, the desktop's compositor, and a prefix without Graphics=null opens windows there
    cmd.env("WAYLAND_DISPLAY", crate::server::console::NO_DISPLAY);
    cmd.envs(env(inst));
}

/// `WINEDLLOVERRIDES` -> (dll lower-case without `.dll`, mode lower-case). Later entries win.
pub fn parse_overrides(s: &str) -> Vec<(String, String)> {
    let mut out = Vec::new();
    for entry in s.split(';') {
        let Some((dlls, mode)) = entry.split_once('=') else { continue };
        let mode = mode.trim().to_ascii_lowercase().replace(' ', "");
        for dll in dlls.split(',') {
            let d = dll.trim().to_ascii_lowercase();
            let d = d.strip_suffix(".dll").unwrap_or(&d);
            if !d.is_empty() {
                out.push((d.to_string(), mode.clone()));
            }
        }
    }
    out
}

fn mode_of<'a>(list: &'a [(String, String)], dll: &str) -> Option<&'a str> {
    list.iter().rev().find(|(d, _)| d == dll).map(|(_, m)| m.as_str())
}

/// Refuses overrides that take Wine's builtin `mscoree` away (disabled, or native-only).
pub fn check_mscoree(overrides: &str) -> Result<()> {
    let list = parse_overrides(overrides);
    let mode = mode_of(&list, "mscoree").or_else(|| mode_of(&list, "*"));
    if let Some(m) = mode
        && !m.split(',').any(|x| x == "b" || x == "builtin")
    {
        return Err(bad(format!(
            "WINEDLLOVERRIDES {overrides:?} takes Wine's builtin mscoree away (mode {m:?}); never disable mscoree: \
             under Wine the .NET runtime opens assemblies through it and every managed load fails with 0x8007007E"
        )));
    }
    Ok(())
}

/// Kiln's proxy only loads when dinput8 is native first.
pub fn dinput8_native_first(overrides: &str) -> bool {
    let list = parse_overrides(overrides);
    mode_of(&list, "dinput8").and_then(|m| m.split(',').next()).is_some_and(|f| f == "n" || f == "native")
}

#[cfg(all(test, unix))] // Unix paths and Wine runners: Linux tooling
mod tests {
    use super::*;

    #[test]
    fn spec_parsing() {
        let h = Path::new("/home/u");
        assert_eq!(WineSpec::parse("ge", h).unwrap(), WineSpec::GeLatest);
        assert_eq!(WineSpec::parse(" system ", h).unwrap(), WineSpec::System);
        assert_eq!(WineSpec::parse("ge:GE-Proton11-11-x86_64", h).unwrap(), WineSpec::Ge("GE-Proton11-11-x86_64".into()));
        assert_eq!(WineSpec::parse("~/wine/bin/wine", h).unwrap(), WineSpec::Path("/home/u/wine/bin/wine".into()));
        assert!(WineSpec::parse("wine", h).is_err());
        assert!(WineSpec::parse("ge:", h).is_err());
        assert!(WineSpec::parse("", h).is_err());
        for s in ["ge", "system", "ge:GE-Proton9-27", "/opt/wine/bin/wine"] {
            assert_eq!(WineSpec::parse(s, h).unwrap().to_config(), s);
        }
    }

    #[test]
    fn ge_versions() {
        assert_eq!(ge_version("GE-Proton11-11-x86_64"), Some(vec![11, 11]));
        assert_eq!(ge_version("GE-Proton9-27"), Some(vec![9, 27]));
        assert_eq!(ge_version("UMU-Proton-10.0-4"), None);
        assert_eq!(ge_version("GE-Proton"), None);
        assert!(ge_version("GE-Proton11-11-x86_64") > ge_version("GE-Proton11-7-x86_64"));
        assert!(ge_version("GE-Proton10-4") > ge_version("GE-Proton9-27"));
    }

    #[test]
    fn overrides() {
        let l = parse_overrides("mshtml=;dinput8=n,b");
        assert_eq!(l, vec![("mshtml".into(), "".into()), ("dinput8".into(), "n,b".into())]);
        assert_eq!(parse_overrides("a.dll,B=n;c")[1], ("b".into(), "n".into()));
        assert!(check_mscoree(DLL_OVERRIDES).is_ok());
        assert!(check_mscoree("mshtml=;mscoree=;dinput8=n,b").is_err());
        assert!(check_mscoree("mscoree=d").is_err());
        assert!(check_mscoree("mscoree.dll=n").is_err());
        assert!(check_mscoree("mscoree=n,b").is_ok());
        assert!(check_mscoree("*=n").is_err());
        assert!(check_mscoree("*=n;mscoree=b").is_ok());
        assert!(check_mscoree("mscoree=;mscoree=b").is_ok());
        assert!(dinput8_native_first(DLL_OVERRIDES));
        assert!(!dinput8_native_first("dinput8=b,n"));
        assert!(!dinput8_native_first("mshtml="));
        assert!(dinput8_native_first("DINPUT8.DLL=native,builtin"));
    }
}
