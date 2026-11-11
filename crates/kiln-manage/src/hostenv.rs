// SPDX-License-Identifier: MIT
//! The environment of programs the launcher starts on the host.
//!
//! The launcher's Linux AppImage runs under AppRun, which points the process at the files inside the mounted image:
//! `PYTHONHOME`, `PYTHONPATH`, `PERLLIB`, `LD_LIBRARY_PATH`, `PATH`, `XDG_DATA_DIRS`, `QT_PLUGIN_PATH`,
//! `GSETTINGS_SCHEMA_DIR`, the GStreamer plugin paths, all with entries under `$APPDIR`. A child process inherits them.
//! On 2026-11-11 a player pressed Play while Steam was closed: `xdg-open steam://...` started Steam with that
//! environment, Proton's launch script (Python) found no standard library under `PYTHONHOME=$APPDIR/usr/` ("Fatal
//! Python error: Failed to import encodings module"), and every game start of that Steam session ended within two
//! seconds, before the game's exe ran.
//!
//! So a program of the host is started through [`command`] (or a `Command` passed to [`clean`]): every entry under
//! `$APPDIR` is taken out of every variable, a variable left empty is removed, and the AppImage's own variables
//! (`APPDIR`, `APPIMAGE`, `ARGV0`, `OWD`) do not go along. Outside an AppImage (`APPDIR` unset) nothing changes.
use std::ffi::{OsStr, OsString};
use std::process::Command;

/// The AppImage runtime's own variables: of no use to a program of the host.
#[cfg(unix)]
const OWN: [&str; 4] = ["APPDIR", "APPIMAGE", "ARGV0", "OWD"];

/// What has to change in `vars` (a process environment) before a host program gets it: `(name, None)` removes the
/// variable, `(name, Some(value))` replaces it. Empty when `APPDIR` is unset or empty.
///
/// A value is read as a `:`-separated list (a single path is a list of one). An entry is the AppImage's when it is
/// `$APPDIR` or lies under it; AppRun writes `$APPDIR/usr/lib/:$OLD`, so with nothing in `$OLD` the list ends in an
/// empty entry, which for `PATH`-like variables means the current folder: empty entries at the ends go with them.
#[cfg(unix)]
pub fn changes<I>(vars: I) -> Vec<(OsString, Option<OsString>)>
where
    I: IntoIterator<Item = (OsString, OsString)>,
{
    use std::os::unix::ffi::{OsStrExt, OsStringExt};
    let vars: Vec<(OsString, OsString)> = vars.into_iter().collect();
    let Some(appdir) = vars.iter().find(|(k, _)| k == "APPDIR").map(|(_, v)| v.as_bytes().to_vec()) else { return Vec::new() };
    let appdir: &[u8] = match appdir.strip_suffix(b"/") { Some(s) => s, None => &appdir };
    if appdir.is_empty() { return Vec::new(); }
    let inside = |entry: &[u8]| entry.strip_prefix(appdir).is_some_and(|rest| rest.is_empty() || rest[0] == b'/');
    let mut out = Vec::new();
    for (name, value) in &vars {
        if OWN.iter().any(|own| name == own) {
            out.push((name.clone(), None));
            continue;
        }
        let entries: Vec<&[u8]> = value.as_bytes().split(|b| *b == b':').collect();
        if !entries.iter().any(|e| inside(e)) { continue; }
        let mut kept: Vec<&[u8]> = entries.into_iter().filter(|e| !inside(e)).collect();
        while kept.last().is_some_and(|e| e.is_empty()) { kept.pop(); }
        while kept.first().is_some_and(|e| e.is_empty()) { kept.remove(0); }
        out.push((name.clone(), (!kept.is_empty()).then(|| OsString::from_vec(kept.join(&b':')))));
    }
    out
}

/// Windows has no AppImage: nothing to change.
#[cfg(not(unix))]
pub fn changes<I>(_vars: I) -> Vec<(OsString, Option<OsString>)>
where
    I: IntoIterator<Item = (OsString, OsString)>,
{
    Vec::new()
}

/// Applies [`changes`] of `vars` to `cmd`. Call it before the caller's own `env` calls: those then win.
pub fn clean_with<I>(cmd: &mut Command, vars: I) -> &mut Command
where
    I: IntoIterator<Item = (OsString, OsString)>,
{
    for (name, value) in changes(vars) {
        match value {
            Some(v) => cmd.env(name, v),
            None => cmd.env_remove(name),
        };
    }
    cmd
}

/// Takes the AppImage's entries out of what `cmd` inherits from this process.
pub fn clean(cmd: &mut Command) -> &mut Command {
    clean_with(cmd, std::env::vars_os())
}

/// A `Command` for a program of the host (the desktop's opener, Steam, Wine, SteamCMD, tar, ssh, an archive tool):
/// it inherits this process's environment without the AppImage's entries.
pub fn command<S: AsRef<OsStr>>(program: S) -> Command {
    let mut cmd = Command::new(program);
    clean(&mut cmd);
    cmd
}

#[cfg(all(test, unix))]
mod tests {
    use super::*;

    fn vars(list: &[(&str, &str)]) -> Vec<(OsString, OsString)> {
        list.iter().map(|(k, v)| (OsString::from(k), OsString::from(v))).collect()
    }
    fn of<'a>(changes: &'a [(OsString, Option<OsString>)], name: &str) -> Option<&'a Option<OsString>> {
        changes.iter().find(|(k, _)| k == name).map(|(_, v)| v)
    }
    fn set(value: &str) -> Option<OsString> { Some(OsString::from(value)) }

    /// The environment Steam had on 2026-11-11 after the launcher's AppImage started it (shortened): the mount point
    /// has a space, AppRun's lists end in ":" where the variable was unset before, and Steam had already put its own
    /// folders in front.
    const A: &str = "/tmp/.mount_Kiln LMJEMBn";
    fn seen() -> Vec<(OsString, OsString)> {
        vars(&[
            ("APPDIR", A),
            ("APPIMAGE", "/games/Kiln Launcher_0.2.0-dev.23.2_amd64.AppImage"),
            ("ARGV0", "/games/Kiln Launcher_0.2.0-dev.23.2_amd64.AppImage"),
            ("OWD", "/games"),
            ("PYTHONHOME", "/tmp/.mount_Kiln LMJEMBn/usr/"),
            ("PYTHONPATH", "/tmp/.mount_Kiln LMJEMBn/usr/share/pyshared/:"),
            ("PERLLIB", "/tmp/.mount_Kiln LMJEMBn/usr/share/perl5/:/tmp/.mount_Kiln LMJEMBn/usr/lib/perl5/:"),
            ("GSETTINGS_SCHEMA_DIR", "/tmp/.mount_Kiln LMJEMBn/usr/share/glib-2.0/schemas/:/tmp/.mount_Kiln LMJEMBn//usr/share/glib-2.0/schemas"),
            ("LD_LIBRARY_PATH", "/steam/ubuntu12_32:/steam/pinned_libs_64:/tmp/.mount_Kiln LMJEMBn/usr/lib/:/tmp/.mount_Kiln LMJEMBn/lib/:"),
            ("XDG_DATA_DIRS", "/tmp/.mount_Kiln LMJEMBn/usr/share/:/tmp/.mount_Kiln LMJEMBn/usr/share:/usr/share:/usr/local/share/:/usr/share/"),
            ("PATH", "/steam/bin:/tmp/.mount_Kiln LMJEMBn/usr/bin/:/tmp/.mount_Kiln LMJEMBn/usr/sbin/:/usr/local/bin:/usr/bin"),
            ("HOME", "/home/player"),
            ("LANG", "en_US.UTF-8"),
        ])
    }

    #[test]
    fn the_appimages_entries_leave_and_the_hosts_stay() {
        let c = changes(seen());
        // what broke Proton: gone
        assert_eq!(of(&c, "PYTHONHOME"), Some(&None));
        assert_eq!(of(&c, "PYTHONPATH"), Some(&None), "only AppImage entries and AppRun's trailing ':'");
        assert_eq!(of(&c, "PERLLIB"), Some(&None));
        assert_eq!(of(&c, "GSETTINGS_SCHEMA_DIR"), Some(&None), "an entry with a doubled slash is under APPDIR too");
        // lists that also hold the host's entries keep exactly those, in order, with no empty entry at an end
        assert_eq!(of(&c, "LD_LIBRARY_PATH"), Some(&set("/steam/ubuntu12_32:/steam/pinned_libs_64")));
        assert_eq!(of(&c, "XDG_DATA_DIRS"), Some(&set("/usr/share:/usr/local/share/:/usr/share/")));
        assert_eq!(of(&c, "PATH"), Some(&set("/steam/bin:/usr/local/bin:/usr/bin")));
        // the AppImage's own variables do not go along
        for own in ["APPDIR", "APPIMAGE", "ARGV0", "OWD"] { assert_eq!(of(&c, own), Some(&None), "{own}"); }
        // what has nothing of the AppImage is not touched
        assert_eq!(of(&c, "HOME"), None);
        assert_eq!(of(&c, "LANG"), None);
    }

    #[test]
    fn outside_an_appimage_nothing_changes() {
        let mut v = seen();
        v.retain(|(k, _)| k != "APPDIR");
        assert!(changes(v).is_empty(), "no APPDIR: not an AppImage, even with paths that look like one");
        let mut v = seen();
        v.iter_mut().find(|(k, _)| k == "APPDIR").unwrap().1 = OsString::new();
        assert!(changes(v).is_empty(), "an empty APPDIR names no folder");
        assert!(changes(vars(&[("PATH", "/usr/bin"), ("HOME", "/home/p")])).is_empty());
    }

    #[test]
    fn only_entries_under_the_mount_are_the_appimages() {
        let c = changes(vars(&[
            ("APPDIR", "/tmp/.mount_Kiln/"),  // a trailing slash names the same folder
            ("A", "/tmp/.mount_Kiln"),  // the folder itself
            ("B", "/tmp/.mount_Kiln2/usr/lib:/tmp/.mount_Kiln/usr/lib"),  // a sibling with the same prefix is the host's
            ("C", "/data/tmp/.mount_Kiln/usr/lib"),  // the path inside another one is the host's
            ("D", "x:/tmp/.mount_Kiln/y::z"),  // an empty entry in the middle is the host's
            ("E", ":/tmp/.mount_Kiln/y:x"),  // one at the front goes (it would name the current folder)
        ]));
        assert_eq!(of(&c, "A"), Some(&None));
        assert_eq!(of(&c, "B"), Some(&set("/tmp/.mount_Kiln2/usr/lib")));
        assert_eq!(of(&c, "C"), None);
        assert_eq!(of(&c, "D"), Some(&set("x::z")));
        assert_eq!(of(&c, "E"), Some(&set("x")));
    }

    /// A command built for the host carries the removals, and the caller's own `env` after it wins.
    #[test]
    fn a_host_command_does_not_get_the_appimages_python() {
        let mut cmd = Command::new("sh");
        cmd.env("PYTHONHOME", "/tmp/.mount_Kiln LMJEMBn/usr/").env("LD_LIBRARY_PATH", "/tmp/.mount_Kiln LMJEMBn/usr/lib/:/opt/lib");
        clean_with(&mut cmd, seen());
        cmd.env("LANG", "C");
        let envs: Vec<(String, Option<String>)> = cmd.get_envs()
            .map(|(k, v)| (k.to_string_lossy().into_owned(), v.map(|v| v.to_string_lossy().into_owned()))).collect();
        let get = |name: &str| envs.iter().find(|(k, _)| k == name).map(|(_, v)| v.clone());
        assert_eq!(get("PYTHONHOME"), Some(None));
        assert_eq!(get("LD_LIBRARY_PATH"), Some(Some("/steam/ubuntu12_32:/steam/pinned_libs_64".into())));
        assert_eq!(get("LANG"), Some(Some("C".into())));
        // and it holds when the program really runs: the child sees no PYTHONHOME
        let out = cmd.arg("-c").arg("printf %s \"${PYTHONHOME-unset}\"").output().unwrap();
        assert_eq!(String::from_utf8_lossy(&out.stdout), "unset");
    }
}
