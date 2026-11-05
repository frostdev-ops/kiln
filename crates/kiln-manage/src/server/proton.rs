//! Starting a Steam-installed dedicated server without Steam (Linux), so no Proton console window opens and the
//! launcher captures the output (console.rs). What Steam would do, read-only from Steam's files:
//! - the Proton build: the app's compatibility-tool mapping in `config/config.vdf` (else the default mapping, app 0,
//!   else the build named in the prefix's `config_info`);
//! - the prefix: `<library>/steamapps/compatdata/2278520/pfx`, which Proton set up; refused when Proton would
//!   upgrade it first (the prefix's `version` differs from the build's `CURRENT_PREFIX_VERSION`): start it once
//!   from Steam then;
//! - the launch options (`localconfig.vdf`): `VAR=value` words before `%command%` become the environment
//!   (`WINEDLLOVERRIDES` is added to Kiln's default `mshtml=;dinput8=n,b`), words after it are arguments, wrappers
//!   (`gamemoderun`, `mangohud`) are left out and noted.
//! The server runs under the build's own `files/bin/wine` (as kilnctl runs GE-Proton), not through `proton run`:
//! Proton's `steam.exe` gives every console program a console window. Without `steam.exe` the server loads the
//! `steamclient64.dll` next to it, like a SteamCMD install. Never writes Steam's configuration or the prefix's
//! registry.

use super::instance::ServerInstance;
use crate::{Error, ErrorCode, Result};
use std::path::{Path, PathBuf};

/// Everything a direct start of a Steam install needs.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SteamLaunch {
    /// Compatibility tool name as Steam maps it (`GE-Proton11-11-x86_64`, `proton_9`).
    pub tool: String,
    pub tool_dir: PathBuf,
    pub wine: PathBuf,
    pub wineserver: PathBuf,
    pub prefix: PathBuf,
    /// Variables for the server (WINEPREFIX, WINEDEBUG, WINEDLLOVERRIDES, the launch options' own).
    pub env: Vec<(String, String)>,
    /// Arguments after `%command%`.
    pub args: Vec<String>,
    /// Lines for the console (what was used, what was left out).
    pub notes: Vec<String>,
}

/// `<library>/steamapps/common/<folder>` -> `<library>`.
pub fn library_of(dir: &Path) -> Option<PathBuf> {
    let common = dir.parent()?;
    let steamapps = common.parent()?;
    (common.file_name()? == "common" && steamapps.file_name()? == "steamapps").then(|| steamapps.parent().map(Path::to_path_buf))?
}

/// Splits a launch-options string into words like a POSIX shell does (quotes, backslash escapes; no expansions).
pub fn split_words(s: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut cur = String::new();
    let mut have = false;
    let mut it = s.chars().peekable();
    while let Some(c) = it.next() {
        match c {
            '\'' => {
                have = true;
                for d in it.by_ref() {
                    if d == '\'' {
                        break;
                    }
                    cur.push(d);
                }
            }
            '"' => {
                have = true;
                while let Some(d) = it.next() {
                    match d {
                        '"' => break,
                        '\\' if matches!(it.peek(), Some('"' | '\\' | '$' | '`')) => cur.push(it.next().unwrap()),
                        _ => cur.push(d),
                    }
                }
            }
            '\\' => {
                have = true;
                if let Some(d) = it.next() {
                    cur.push(d);
                }
            }
            c if c.is_whitespace() => {
                if have {
                    out.push(std::mem::take(&mut cur));
                    have = false;
                }
            }
            c => {
                have = true;
                cur.push(c);
            }
        }
    }
    if have {
        out.push(cur);
    }
    out
}

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct LaunchOpts {
    pub env: Vec<(String, String)>,
    pub args: Vec<String>,
    /// Words before `%command%` that are not assignments (wrappers such as gamemoderun).
    pub wrappers: Vec<String>,
}

fn assignment(w: &str) -> Option<(String, String)> {
    let (k, v) = w.split_once('=')?;
    let ok = k.bytes().next().is_some_and(|b| b.is_ascii_alphabetic() || b == b'_') && k.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_');
    ok.then(|| (k.to_string(), v.to_string()))
}

/// Steam's launch options: `VAR=v ... wrapper %command% args`; without `%command%` Steam appends every word.
pub fn parse_launch_options(s: &str) -> LaunchOpts {
    let words = split_words(s);
    let mut o = LaunchOpts::default();
    match words.iter().position(|w| w == "%command%") {
        Some(i) => {
            for w in &words[..i] {
                match assignment(w) {
                    Some(kv) => o.env.push(kv),
                    None => o.wrappers.push(w.clone()),
                }
            }
            o.args = words[i + 1..].to_vec();
        }
        None => o.args = words,
    }
    o
}

/// `CURRENT_PREFIX_VERSION="GE-Proton11-11"` in a Proton build's `proton` script.
pub fn tool_prefix_version(tool_dir: &Path) -> Option<String> {
    let text = std::fs::read_to_string(tool_dir.join("proton")).ok()?;
    text.lines().find_map(|l| {
        let v = l.trim().strip_prefix("CURRENT_PREFIX_VERSION=")?;
        Some(v.trim().trim_matches('"').trim_matches('\'').to_string())
    })
}

/// The first line of `<compatdata>/version` (what Proton last set the prefix up as).
pub fn prefix_version(compatdata: &Path) -> Option<String> {
    let t = std::fs::read_to_string(compatdata.join("version")).ok()?;
    t.lines().next().map(|l| l.trim().to_string()).filter(|l| !l.is_empty())
}

/// The build that last ran the prefix (`<compatdata>/config_info`: its version, then files of the build).
pub fn config_info_tool(compatdata: &Path) -> Option<PathBuf> {
    let t = std::fs::read_to_string(compatdata.join("config_info")).ok()?;
    t.lines().skip(1).find_map(|l| l.find("/files/").map(|i| PathBuf::from(&l[..i]))).filter(|d| d.join("files/bin/wine").exists())
}

/// Internal names a `compatibilitytool.vdf` registers, with their folders.
fn registered_tools(vdf_file: &Path) -> Vec<(String, PathBuf)> {
    let Ok(text) = std::fs::read_to_string(vdf_file) else { return vec![] };
    let Ok(v) = crate::vdf::parse(&text) else { return vec![] };
    let base = vdf_file.parent().unwrap_or(Path::new("/"));
    let Some(tools) = v.path(&["compatibilitytools", "compat_tools"]) else { return vec![] };
    tools
        .entries()
        .iter()
        .map(|(name, t)| {
            let rel = t.get("install_path").and_then(crate::vdf::Vdf::as_str).unwrap_or(".");
            (name.to_string(), crate::util::clean(&base.join(rel)))
        })
        .collect()
}

/// Folder names Valve's Proton builds use in `steamapps/common` for a compat tool name.
fn valve_folders(name: &str) -> Vec<String> {
    match name {
        "proton_experimental" => return vec!["Proton - Experimental".into()],
        "proton_hotfix" => return vec!["Proton Hotfix".into()],
        _ => {}
    }
    let Some(d) = name.strip_prefix("proton_").filter(|d| !d.is_empty() && d.bytes().all(|b| b.is_ascii_digit())) else { return vec![] };
    let mut v = vec![format!("Proton {d}.0")];
    for k in 1..d.len() {
        v.push(format!("Proton {}.{}", &d[..k], &d[k..]));
    }
    v
}

/// The folder of compat tool `name`: compatibilitytools.d (by registered name or folder name), then Valve's builds
/// in the libraries.
pub fn find_tool(name: &str, tool_roots: &[PathBuf], libraries: &[PathBuf]) -> Option<PathBuf> {
    let has_wine = |d: &Path| d.join("files/bin/wine").exists();
    for root in tool_roots {
        let Ok(rd) = std::fs::read_dir(root) else { continue };
        let mut dirs: Vec<PathBuf> = rd.flatten().map(|e| e.path()).collect();
        dirs.sort();
        for d in &dirs {
            if d.file_name().is_some_and(|f| f == name) && has_wine(d) {
                return Some(d.clone());
            }
            for (n, dir) in registered_tools(&d.join("compatibilitytool.vdf")) {
                if n == name && has_wine(&dir) {
                    return Some(dir);
                }
            }
        }
    }
    for lib in libraries {
        for f in valve_folders(name) {
            let d = lib.join("steamapps/common").join(f);
            if has_wine(&d) {
                return Some(d);
            }
        }
    }
    None
}

/// What a start of a Steam install runs (module docs). Fails with a message that says what to do.
pub fn plan(instance: &ServerInstance) -> Result<SteamLaunch> {
    let dir = instance.local_dir()?;
    let library = library_of(dir);
    let prefix = instance
        .prefix
        .clone()
        .or_else(|| library.as_ref().map(|l| crate::steam::compatdata(l, crate::SERVER_APP_ID).join("pfx")))
        .filter(|p| p.join("system.reg").exists())
        .ok_or_else(|| {
            Error::new(ErrorCode::NotFound, "Steam has not set up a Proton prefix for this server yet: start it once from Steam, then here.")
        })?;
    let compatdata = prefix.parent().map(Path::to_path_buf).unwrap_or_default();
    let roots = crate::steam::roots().unwrap_or_default();
    let root = library.as_ref().and_then(|l| crate::steam::root_of_library(&roots, l)).or_else(|| roots.first().cloned());
    let mut notes = Vec::new();
    let mapped = root.as_ref().and_then(|r| {
        crate::steam::compat_tool(r, crate::SERVER_APP_ID).ok().flatten().or_else(|| crate::steam::compat_tool(r, 0).ok().flatten())
    });
    let mut tool_roots: Vec<PathBuf> = roots.iter().map(|r| r.path.join("compatibilitytools.d")).collect();
    tool_roots.extend(crate::wine::ge_roots(&crate::util::home()));
    let mut libraries: Vec<PathBuf> = library.iter().cloned().collect();
    for r in &roots {
        libraries.extend(crate::steam::libraries(r).unwrap_or_default().into_iter().map(|l| l.path));
    }
    let (tool, tool_dir) = match &mapped {
        Some(name) => {
            let d = find_tool(name, &tool_roots, &libraries).ok_or_else(|| {
                Error::new(ErrorCode::NotFound, format!("Steam runs this server with {name}, which is not installed where the launcher looks (compatibilitytools.d, steamapps/common)."))
            })?;
            (name.clone(), d)
        }
        None => {
            let d = config_info_tool(&compatdata).ok_or_else(|| {
                Error::new(ErrorCode::NotFound, "Steam maps no Proton build to this server and its prefix names none: start it once from Steam, then here.")
            })?;
            let n = d.file_name().map(|f| f.to_string_lossy().to_string()).unwrap_or_default();
            notes.push(format!("no Proton mapping in Steam's config: using {n}, the build that last ran the prefix"));
            (n, d)
        }
    };
    if let (Some(have), Some(want)) = (prefix_version(&compatdata), tool_prefix_version(&tool_dir))
        && have != want
    {
        return Err(Error::new(
            ErrorCode::Conflict,
            format!("Steam set this server's prefix up with Proton {have}; {tool} ({want}) would upgrade it first. Start it once from Steam (Proton upgrades the prefix), then here."),
        ));
    }
    let runner = crate::wine::resolve(&crate::wine::WineSpec::Path(tool_dir.clone()), &crate::util::home())?;
    let mut env: Vec<(String, String)> = vec![
        ("WINEPREFIX".into(), prefix.display().to_string()),
        ("WINEDEBUG".into(), "-all".into()),
        ("WINEDLLOVERRIDES".into(), crate::wine::DLL_OVERRIDES.into()),
    ];
    let mut args = Vec::new();
    if let Some(r) = &root {
        let all = crate::steam::launch_options(r, crate::SERVER_APP_ID).unwrap_or_default();
        if let Some(first) = all.first() {
            if all.iter().any(|o| o.value != first.value) {
                notes.push(format!("several Steam users set launch options for the server; using user {}'s", first.steam_id));
            }
            notes.push(format!("Steam launch options: {}", first.value));
            let o = parse_launch_options(&first.value);
            for (k, v) in o.env {
                match k.as_str() {
                    "WINEDLLOVERRIDES" => {
                        let slot = &mut env.iter_mut().find(|(n, _)| n == "WINEDLLOVERRIDES").unwrap().1;
                        *slot = format!("{};{v}", crate::wine::DLL_OVERRIDES);
                    }
                    "WINEPREFIX" | "DISPLAY" | "WAYLAND_DISPLAY" | "LD_PRELOAD" => notes.push(format!("left out {k} from the launch options")),
                    _ => match env.iter_mut().find(|(n, _)| *n == k) {
                        Some(slot) => slot.1 = v,
                        None => env.push((k, v)),
                    },
                }
            }
            if !o.wrappers.is_empty() {
                notes.push(format!("left out the wrapper(s) {} from the launch options", o.wrappers.join(" ")));
            }
            args = o.args;
        }
    }
    let overrides = env.iter().find(|(k, _)| k == "WINEDLLOVERRIDES").map(|(_, v)| v.clone()).unwrap_or_default();
    crate::wine::check_mscoree(&overrides)?;
    if !crate::wine::dinput8_native_first(&overrides) {
        notes.push(format!("WINEDLLOVERRIDES {overrides:?} does not load dinput8 native first: Kiln will not load"));
    }
    Ok(SteamLaunch { tool, tool_dir, wine: runner.wine, wineserver: runner.wineserver, prefix, env, args, notes })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn words() {
        assert_eq!(split_words(r#"WINEDLLOVERRIDES="dinput8,dbghelp,winmm=n,b" %command%"#), vec!["WINEDLLOVERRIDES=dinput8,dbghelp,winmm=n,b", "%command%"]);
        assert_eq!(split_words(r#"  A='x y'  B=c\ d "e\"f" "#), vec!["A=x y", "B=c d", "e\"f"]);
        assert_eq!(split_words(""), Vec::<String>::new());
        assert_eq!(split_words(r#"A="" %command%"#), vec!["A=", "%command%"]);
    }

    #[test]
    fn launch_options() {
        let o = parse_launch_options(r#"WINEDLLOVERRIDES="dinput8,dbghelp,winmm=n,b" KILN_X=1 gamemoderun %command% -log"#);
        assert_eq!(o.env, vec![("WINEDLLOVERRIDES".into(), "dinput8,dbghelp,winmm=n,b".into()), ("KILN_X".into(), "1".into())]);
        assert_eq!(o.wrappers, vec!["gamemoderun"]);
        assert_eq!(o.args, vec!["-log"]);
        let o = parse_launch_options("-port 1234");
        assert!(o.env.is_empty());
        assert_eq!(o.args, vec!["-port", "1234"]);
        // the merged overrides keep Kiln's proxy native first and mscoree builtin
        let merged = format!("{};{}", crate::wine::DLL_OVERRIDES, "dinput8,dbghelp,winmm=n,b");
        assert!(crate::wine::dinput8_native_first(&merged));
        assert!(crate::wine::check_mscoree(&merged).is_ok());
    }

    #[test]
    fn libraries_and_valve_names() {
        assert_eq!(library_of(Path::new("/home/u/.local/share/Steam/steamapps/common/EnshroudedServer")), Some(PathBuf::from("/home/u/.local/share/Steam")));
        assert_eq!(library_of(Path::new("/srv/enshrouded/lab")), None);
        assert_eq!(valve_folders("proton_9"), vec!["Proton 9.0"]);
        assert!(valve_folders("proton_513").contains(&"Proton 5.13".to_string()));
        assert!(valve_folders("proton_63").contains(&"Proton 6.3".to_string()));
        assert_eq!(valve_folders("proton_experimental"), vec!["Proton - Experimental"]);
        assert!(valve_folders("GE-Proton11-11-x86_64").is_empty());
    }

    #[test]
    fn tools_and_versions() {
        let t = std::env::temp_dir().join(format!("kiln-proton-{}", std::process::id()));
        let ge = t.join("compatibilitytools.d/GE-Proton9-27");
        std::fs::create_dir_all(ge.join("files/bin")).unwrap();
        std::fs::write(ge.join("files/bin/wine"), "").unwrap();
        std::fs::write(ge.join("proton"), "#!/usr/bin/env python3\nCURRENT_PREFIX_VERSION=\"GE-Proton9-27\"\n").unwrap();
        std::fs::write(ge.join("compatibilitytool.vdf"), "\"compatibilitytools\" { \"compat_tools\" { \"GE-Proton9-27-x\" { \"install_path\" \".\" \"display_name\" \"GE\" } } }").unwrap();
        let valve = t.join("lib/steamapps/common/Proton 9.0");
        std::fs::create_dir_all(valve.join("files/bin")).unwrap();
        std::fs::write(valve.join("files/bin/wine"), "").unwrap();
        let roots = [t.join("compatibilitytools.d")];
        let libs = [t.join("lib")];
        assert_eq!(find_tool("GE-Proton9-27", &roots, &libs), Some(ge.clone()), "by folder name");
        assert_eq!(find_tool("GE-Proton9-27-x", &roots, &libs), Some(ge.clone()), "by registered name");
        assert_eq!(find_tool("proton_9", &roots, &libs), Some(valve.clone()));
        assert_eq!(find_tool("proton_8", &roots, &libs), None);
        assert_eq!(tool_prefix_version(&ge).as_deref(), Some("GE-Proton9-27"));
        let cd = t.join("compatdata");
        std::fs::create_dir_all(&cd).unwrap();
        std::fs::write(cd.join("version"), "GE-Proton9-27\n").unwrap();
        std::fs::write(cd.join("config_info"), format!("GE-Proton9-27\n{}/files/share/fonts/\n", ge.display())).unwrap();
        assert_eq!(prefix_version(&cd).as_deref(), Some("GE-Proton9-27"));
        assert_eq!(config_info_tool(&cd), Some(ge));
        std::fs::remove_dir_all(&t).unwrap();
    }
}
