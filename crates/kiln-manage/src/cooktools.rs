// SPDX-License-Identifier: MIT
//! The cook tools, where `kiln cook` and the launcher find them.
//!
//! A mod with a cook recipe (`*/cook.json`) is cooked on each machine from that machine's game files. The tools that
//! do it are Python (`tools/{animcook,impact,kfc,modelcook,soundcook}`). A player and a server operator have no Kiln
//! checkout, so this crate carries a copy (build.rs embeds the sources and data files, and soundcook's native helper
//! when the build names one) and [`locate`] unpacks it on first use, into `<cache>/<hash of the copy>/`. A checkout
//! still wins when the caller names one (developers: `$KILN_REPO`, or the tree `kiln` was built from).
//!
//! Python itself is the machine's (`python3`, [`MIN_PYTHON`] or newer), except where the caller brings one: the
//! launcher's Windows bundle ships the official embeddable Python and passes its path, and `$KILN_PYTHON` names one
//! by hand. A candidate is asked for its version and for a zstd decoder (the game's archives are zstd: the standard
//! library's on 3.14+, the `zstandard` package, or the system's libzstd through ctypes) before it is used; the error
//! says in one sentence what to install.
//!
//! The tools run isolated (`-I`: no `PYTHON*` variables, no user site, and neither the current folder nor the
//! script's folder on the module path, so a `compression.py` lying in a mod's folder is never imported) and through
//! [`crate::hostenv`]: started from the launcher's AppImage they must not see its `PYTHONHOME` or library path. The
//! tools that start one another pass the isolation on. The shipped copy's sound helper is named to every tool through
//! `KILN_SOUNDCOOK` (a modelcook recipe may hold sound steps), with `KILN_SOUNDCOOK_NO_BUILD=1`: nothing is ever
//! compiled on a player's machine.
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::OnceLock;

use sha2::{Digest, Sha256};

use crate::error::{Error, ErrorCode, Result};

include!(concat!(env!("OUT_DIR"), "/cooktools_files.rs"));

/// The oldest Python the tools run on (`zip(strict=True)`, `str.removeprefix`).
pub const MIN_PYTHON: (u32, u32) = (3, 10);

/// The tools a recipe's `"tool"` may name: its script under the tools' root, and the verb that cooks a recipe.
pub const TOOLS: [(&str, &str, &str); 4] = [
    ("modelcook", "tools/modelcook/modelcook.py", "recipe"),
    ("impact", "tools/impact/impact.py", "cook"),
    ("soundcook", "tools/soundcook/soundcook.py", "recipe"),
    ("animcook", "tools/animcook/animcook.py", "recipe"),
];

/// Where a [`CookTools`] root came from.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Source {
    /// A Kiln source checkout (the caller named it).
    Checkout,
    /// The copy this build carries, unpacked into the cache.
    Shipped,
}

/// A Python that passed the checks.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Python {
    pub program: PathBuf,
    pub version: (u32, u32),
    /// `stdlib`, `zstandard` or `libzstd`.
    pub zstd: String,
    /// True when the caller brought it ([`Locate::python`], the launcher's bundle) or `$KILN_PYTHON` named it.
    pub given: bool,
}

/// The cook tools, ready to run.
#[derive(Clone, Debug)]
pub struct CookTools {
    /// The folder that holds `tools/<tool>/...`.
    pub root: PathBuf,
    pub source: Source,
    pub python: Python,
    /// soundcook's native helper, when this build carries one (a checkout finds or builds its own).
    pub sound_helper: Option<PathBuf>,
}

/// What [`locate`] is told by its caller.
#[derive(Clone, Debug, Default)]
pub struct Locate {
    /// A Kiln checkout to take the tools from, if it has them (else the shipped copy is used).
    pub repo: Option<PathBuf>,
    /// Where the shipped copy is unpacked: a folder of Kiln's own (the data root's `cooktools`). Required unless
    /// `repo` has the tools.
    pub cache: Option<PathBuf>,
    /// A Python the caller ships (the launcher's Windows bundle). It is used before any other.
    pub python: Option<PathBuf>,
    /// `$KILN_PYTHON`, when the caller read it (tests pass None).
    pub env_python: Option<OsString>,
    /// `$PATH`, for `python3` / `python` (tests pass their own).
    pub path: Option<OsString>,
}

impl Locate {
    /// `cache` and `repo` as given, `$KILN_PYTHON` and `$PATH` from this process.
    pub fn from_process(repo: Option<PathBuf>, cache: Option<PathBuf>, python: Option<PathBuf>) -> Locate {
        Locate { repo, cache, python, env_python: std::env::var_os("KILN_PYTHON").filter(|v| !v.is_empty()), path: std::env::var_os("PATH") }
    }
}

/// Whether this build carries the tools (a development build without `tools/` beside the crates does not).
pub fn shipped() -> bool {
    !FILES.is_empty()
}

/// The names of the files this build carries, sorted (`tools/<tool>/...`).
pub fn shipped_files() -> impl Iterator<Item = &'static str> {
    FILES.iter().map(|(n, _)| *n)
}

fn has_tools(root: &Path) -> bool {
    TOOLS.iter().all(|(_, script, _)| root.join(script).is_file()) && root.join("tools/kfc/kfc/archive.py").is_file()
}

fn helper_name() -> &'static str {
    if cfg!(windows) { "kiln-soundcook.exe" } else { "kiln-soundcook" }
}

/// sha256 over every carried file (name, length, bytes) and the helper: the name of the folder the copy unpacks into,
/// so a new Kiln never runs an older Kiln's tools and two versions can sit side by side.
fn copy_id() -> &'static str {
    static ID: OnceLock<String> = OnceLock::new();
    ID.get_or_init(|| {
        let mut h = Sha256::new();
        for (name, data) in FILES.iter().copied().chain(HELPER.iter().map(|(n, d)| (*n, *d))) {
            h.update((name.len() as u64).to_le_bytes());
            h.update(name.as_bytes());
            h.update((data.len() as u64).to_le_bytes());
            h.update(data);
        }
        h.finalize().iter().take(8).map(|b| format!("{b:02x}")).collect()
    })
}

fn io(what: &str, p: &Path, e: std::io::Error) -> Error {
    Error::new(ErrorCode::Io, format!("{what} {}: {e}", p.display()))
}

/// Every carried file is there as a plain file (no link) with exactly the carried bytes. The copy is small (about
/// 100 files, under 3 MB): comparing it whole costs a few milliseconds and means a damaged or altered copy is never run.
fn whole(dir: &Path) -> bool {
    let same = |name: &str, data: &[u8]| {
        let p = dir.join(name);
        std::fs::symlink_metadata(&p).is_ok_and(|m| m.file_type().is_file() && m.len() == data.len() as u64)
            && std::fs::read(&p).is_ok_and(|have| have == data)
    };
    std::fs::symlink_metadata(dir).is_ok_and(|m| m.file_type().is_dir())
        && dir.join(".complete").is_file()
        && FILES.iter().all(|(n, d)| same(n, d))
        && HELPER.iter().all(|(n, d)| same(&format!("bin/{n}"), d))
}

static STAGING: AtomicU64 = AtomicU64::new(0);

/// Unpacks the carried copy under `cache` (once: a whole copy that is there is left alone) and returns its root.
/// A copy that is not whole (an interrupted first run, a file deleted, changed or replaced by a link) is written
/// again. Writing is one at a time across threads and processes (a lock file beside the copy): the copy is staged in
/// a folder of its own, checked again under the lock, and moved into place.
pub fn unpack(cache: &Path) -> Result<PathBuf> {
    if !shipped() {
        return Err(Error::new(
            ErrorCode::NotFound,
            "this build of Kiln carries no cook tools (a development build): set KILN_REPO to a Kiln checkout (the tools are its tools/ folder)",
        ));
    }
    let dir = cache.join(copy_id());
    if whole(&dir) {
        return Ok(dir);
    }
    std::fs::create_dir_all(cache).map_err(|e| io("creating", cache, e))?;
    let lock_path = cache.join(format!("{}.lock", copy_id()));
    let lock = std::fs::OpenOptions::new().create(true).truncate(false).write(true).open(&lock_path).map_err(|e| io("opening", &lock_path, e))?;
    lock.lock().map_err(|e| io("locking", &lock_path, e))?;  // released when `lock` is dropped, on every way out
    if whole(&dir) {
        return Ok(dir);  // another thread or process wrote it while this one waited
    }
    // what an interrupted run left behind: nobody else stages under the lock
    let stale = format!("{}.tmp-", copy_id());
    for e in std::fs::read_dir(cache).into_iter().flatten().flatten() {
        if e.file_name().to_string_lossy().starts_with(&stale) {
            let _ = std::fs::remove_dir_all(e.path());
        }
    }
    let tmp = cache.join(format!("{stale}{}-{}", std::process::id(), STAGING.fetch_add(1, Ordering::Relaxed)));
    let put = |rel: &str, data: &[u8], exec: bool| -> Result<()> {
        let p = tmp.join(rel);
        if let Some(parent) = p.parent() {
            std::fs::create_dir_all(parent).map_err(|e| io("creating", parent, e))?;
        }
        std::fs::write(&p, data).map_err(|e| io("writing", &p, e))?;
        #[cfg(unix)]
        if exec {
            use std::os::unix::fs::PermissionsExt;
            std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o755)).map_err(|e| io("marking", &p, e))?;
        }
        let _ = exec;
        Ok(())
    };
    let written = (|| -> Result<()> {
        for (name, data) in FILES {
            put(name, data, false)?;
        }
        if let Some((name, data)) = HELPER {
            put(&format!("bin/{name}"), data, true)?;
        }
        put(".complete", format!("{}\n", FILES.len()).as_bytes(), false)
    })();
    if let Err(e) = written {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(e);
    }
    // The copy that is there is not whole (checked under the lock): it goes, whatever it is (a folder, a link, a file).
    if std::fs::symlink_metadata(&dir).is_ok() {
        if std::fs::remove_dir_all(&dir).is_err() {
            let _ = std::fs::remove_file(&dir);
        }
    }
    if let Err(e) = std::fs::rename(&tmp, &dir) {
        let _ = std::fs::remove_dir_all(&tmp);
        return Err(io("moving the cook tools into", &dir, e));
    }
    drop(lock);
    Ok(dir)
}

/// What a candidate Python says about itself: its version and its zstd decoder ("" when it has none).
const PROBE: &str = "import sys\nsys.path.insert(0, sys.argv[1])\nfrom kfc import archive\nprint('kiln-python', sys.version_info[0], sys.version_info[1], archive.zstd_backend() or '-')";

fn quiet(cmd: &mut Command) -> &mut Command {
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        cmd.creation_flags(0x0800_0000); // CREATE_NO_WINDOW: no console flashing up from the launcher
    }
    cmd
}

/// Runs the probe; None when the program does not start or does not answer as a Python does.
fn probe(program: &Path, root: &Path) -> Option<((u32, u32), Option<String>)> {
    probe_within(program, root, crate::cook::Limits::LAUNCHER.step.unwrap_or(std::time::Duration::from_secs(120)))
}

/// [`probe`] within `limit`: run as a cook's short step (`cook::run_step`), so a candidate that hangs is stopped with
/// every process it started, and is no Python.
fn probe_within(program: &Path, root: &Path, limit: std::time::Duration) -> Option<((u32, u32), Option<String>)> {
    let mut cmd = crate::hostenv::command(program);
    // -I: the current folder is not on the module path (a mod's folder is where `kiln cook` is usually run)
    cmd.arg("-I").arg("-c").arg(PROBE).arg(root.join("tools/kfc"));
    let (_, out, _) = crate::cook::run_step(quiet(&mut cmd), Some(limit), &|| false).ok()?;
    parse_probe(&out)
}

fn parse_probe(stdout: &str) -> Option<((u32, u32), Option<String>)> {
    let line = stdout.lines().rev().find(|l| l.starts_with("kiln-python "))?;
    let mut it = line.split_whitespace().skip(1);
    let version = (it.next()?.parse().ok()?, it.next()?.parse().ok()?);
    let zstd = it.next().filter(|z| *z != "-").map(str::to_string);
    Some((version, zstd))
}

fn on_path(path: Option<&OsString>, names: &[&str]) -> Vec<PathBuf> {
    let Some(path) = path else { return Vec::new() };
    let mut found = Vec::new();
    for dir in std::env::split_paths(path) {
        for n in names {
            let p = dir.join(n);
            if crate::util::is_executable(&p) && !found.contains(&p) {
                found.push(p);
            }
        }
    }
    found
}

/// Why a Python that was found cannot be used, as one sentence.
fn unusable(program: &Path, version: (u32, u32), zstd: &Option<String>) -> Option<String> {
    if version < MIN_PYTHON {
        return Some(format!(
            "{} is Python {}.{}: the cook tools need Python {}.{} or newer",
            program.display(), version.0, version.1, MIN_PYTHON.0, MIN_PYTHON.1
        ));
    }
    if zstd.is_none() {
        return Some(format!(
            "Python {}.{} at {} has no zstd decoder: install the zstd library (libzstd) or the zstandard package, or use Python 3.14 or newer",
            version.0, version.1, program.display()
        ));
    }
    None
}

/// The Python the tools run with: the caller's, `$KILN_PYTHON`, a `python` folder beside this program (Windows), then
/// `python3` and `python` on `$PATH`; the first that is new enough and can read zstd.
fn find_python(l: &Locate, root: &Path) -> Result<Python> {
    let mut given: Vec<PathBuf> = Vec::new();
    given.extend(l.python.clone());
    given.extend(l.env_python.as_ref().map(PathBuf::from));
    if cfg!(windows) {
        if let Some(dir) = std::env::current_exe().ok().and_then(|e| e.parent().map(Path::to_path_buf)) {
            given.push(dir.join("python").join("python.exe"));
        }
    }
    let names: &[&str] = if cfg!(windows) { &["python3.exe", "python.exe"] } else { &["python3", "python"] };
    let found = on_path(l.path.as_ref(), names);
    let mut why: Option<String> = None;
    for (program, is_given) in given.iter().map(|p| (p, true)).chain(found.iter().map(|p| (p, false))) {
        if !program.is_file() {
            // one the caller named and that is not there is worth a word; a default place that is empty is not
            if is_given && (l.python.as_deref() == Some(program) || l.env_python.as_ref().map(PathBuf::from).as_deref() == Some(program)) {
                why.get_or_insert(format!("the Python at {} is not there", program.display()));
            }
            continue;
        }
        let Some((version, zstd)) = probe(program, root) else {
            why.get_or_insert(format!("{} did not answer as a Python does", program.display()));
            continue;
        };
        match unusable(program, version, &zstd) {
            Some(w) => {
                why.get_or_insert(w);
            }
            None => return Ok(Python { program: program.clone(), version, zstd: zstd.unwrap_or_default(), given: is_given }),
        }
    }
    let none = if cfg!(windows) {
        format!(
            "the cook tools need Python {}.{} or newer and none was found: Kiln's launcher brings its own, or set KILN_PYTHON to a python.exe",
            MIN_PYTHON.0, MIN_PYTHON.1
        )
    } else {
        format!(
            "the cook tools need Python {}.{} or newer and no python3 was found: install it with your system's package manager",
            MIN_PYTHON.0, MIN_PYTHON.1
        )
    };
    Err(Error::new(ErrorCode::NotFound, why.unwrap_or(none)))
}

/// The cook tools and a Python that can run them. A checkout in [`Locate::repo`] wins when it has the tools; else
/// the carried copy is unpacked under [`Locate::cache`].
pub fn locate(l: &Locate) -> Result<CookTools> {
    let (root, source) = match l.repo.as_ref().filter(|r| has_tools(r)) {
        Some(r) => (r.clone(), Source::Checkout),
        None => {
            let cache = l.cache.as_ref().ok_or_else(|| {
                Error::new(ErrorCode::InvalidArgument, "cooktools::locate needs a cache folder for the shipped cook tools")
            })?;
            (unpack(cache)?, Source::Shipped)
        }
    };
    let python = find_python(l, &root)?;
    let helper = root.join("bin").join(helper_name());
    let sound_helper = (source == Source::Shipped && helper.is_file()).then_some(helper);
    Ok(CookTools { root, source, python, sound_helper })
}

impl CookTools {
    /// The script and the recipe verb of a recipe's `"tool"`; None for a name that is no cook tool.
    pub fn script(&self, tool: &str) -> Option<(PathBuf, &'static str)> {
        let (_, rel, verb) = TOOLS.iter().find(|(n, _, _)| *n == tool)?;
        let p = self.root.join(rel);
        p.is_file().then_some((p, *verb))
    }

    /// `python -I <script>` for a tool, with the host's environment (not the AppImage's) and no console window. The
    /// caller adds the tool's own arguments (`--game`, the verb, the recipe).
    pub fn command(&self, tool: &str) -> Option<Command> {
        let (script, _) = self.script(tool)?;
        Some(self.run(&script))
    }

    /// `python -I <root>/<rel>`: a script of the tools that is no recipe tool's entry (modelcook's `looks`).
    pub fn script_command(&self, rel: &str) -> Command {
        self.run(&self.root.join(rel))
    }

    // Every tool gets the same: any of them may run sound steps (a modelcook recipe dispatches to soundcook). The
    // shipped copy names its helper, or none, and never builds one: a player's machine compiles nothing.
    fn run(&self, script: &Path) -> Command {
        let mut cmd = crate::hostenv::command(&self.python.program);
        cmd.arg("-I").arg(script);
        if self.source == Source::Shipped {
            cmd.env("KILN_SOUNDCOOK_NO_BUILD", "1");
            match &self.sound_helper {
                Some(h) => cmd.env("KILN_SOUNDCOOK", h),
                None => cmd.env_remove("KILN_SOUNDCOOK"),
            };
        }
        quiet(&mut cmd);
        cmd
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tmp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("kiln-cooktools-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn this_build_carries_every_tool_and_no_test_file() {
        assert!(shipped(), "the workspace has tools/: the feature must embed them");
        let names: Vec<&str> = shipped_files().collect();
        for (_, script, _) in TOOLS {
            assert!(names.contains(&script), "{script} is carried");
        }
        assert!(names.contains(&"tools/kfc/kfc/archive.py"));
        assert!(names.iter().all(|n| n.starts_with("tools/")));
        for n in &names {
            assert!(!n.contains("/tests/") && !n.contains("/lab/") && !n.contains("/native/") && !n.contains("/."), "{n} must not be carried");
            assert!(!n.ends_with(".pyc"), "{n}");
        }
        let mut sorted = names.clone();
        sorted.sort();
        assert_eq!(names, sorted);
    }

    #[test]
    fn the_copy_unpacks_once_and_heals_a_damaged_one() {
        let cache = tmp("unpack");
        let root = unpack(&cache).unwrap();
        assert!(has_tools(&root));
        assert_eq!(root, cache.join(copy_id()));
        let marker = std::fs::metadata(root.join(".complete")).unwrap().modified().unwrap();
        // a second call leaves a whole copy alone
        assert_eq!(unpack(&cache).unwrap(), root);
        assert_eq!(std::fs::metadata(root.join(".complete")).unwrap().modified().unwrap(), marker);
        // a file that is gone, or cut short, means the copy is written again
        std::fs::remove_file(root.join("tools/kfc/kfc/archive.py")).unwrap();
        std::fs::write(root.join("tools/modelcook/modelcook.py"), b"cut").unwrap();
        assert_eq!(unpack(&cache).unwrap(), root);
        let (_, data) = FILES.iter().find(|(n, _)| *n == "tools/modelcook/modelcook.py").unwrap();
        assert_eq!(std::fs::read(root.join("tools/modelcook/modelcook.py")).unwrap(), *data);
        assert!(root.join("tools/kfc/kfc/archive.py").is_file());
        // a file changed without changing its length is found too, and so is a link in a file's place
        let mut altered = data.to_vec();
        altered[0] ^= 1;
        std::fs::write(root.join("tools/modelcook/modelcook.py"), &altered).unwrap();
        assert_eq!(unpack(&cache).unwrap(), root);
        assert_eq!(std::fs::read(root.join("tools/modelcook/modelcook.py")).unwrap(), *data);
        #[cfg(unix)]
        {
            let elsewhere = cache.join("elsewhere.py");
            std::fs::write(&elsewhere, data).unwrap();
            std::fs::remove_file(root.join("tools/modelcook/modelcook.py")).unwrap();
            std::os::unix::fs::symlink(&elsewhere, root.join("tools/modelcook/modelcook.py")).unwrap();
            assert_eq!(unpack(&cache).unwrap(), root);
            assert!(std::fs::symlink_metadata(root.join("tools/modelcook/modelcook.py")).unwrap().file_type().is_file());
            std::fs::remove_file(&elsewhere).unwrap();
        }
        // a staging folder an interrupted run left is cleared by the next repair, and nothing else is left
        std::fs::create_dir_all(cache.join(format!("{}.tmp-1-0", copy_id())).join("tools")).unwrap();
        std::fs::remove_file(root.join(".complete")).unwrap();
        assert_eq!(unpack(&cache).unwrap(), root);
        let mut left: Vec<String> = std::fs::read_dir(&cache).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
        left.sort();
        assert_eq!(left, [copy_id().to_string(), format!("{}.lock", copy_id())]);
        let _ = std::fs::remove_dir_all(&cache);
    }

    #[test]
    fn many_at_once_unpack_one_whole_copy() {
        let cache = tmp("race");
        let start = std::sync::Arc::new(std::sync::Barrier::new(8));
        let threads: Vec<_> = (0..8)
            .map(|_| {
                let (cache, start) = (cache.clone(), start.clone());
                std::thread::spawn(move || {
                    start.wait();
                    unpack(&cache).unwrap()
                })
            })
            .collect();
        let roots: Vec<PathBuf> = threads.into_iter().map(|t| t.join().unwrap()).collect();
        assert!(roots.iter().all(|r| *r == roots[0]));
        assert!(whole(&roots[0]));
        let mut left: Vec<String> = std::fs::read_dir(&cache).unwrap().flatten().map(|e| e.file_name().to_string_lossy().into_owned()).collect();
        left.sort();
        assert_eq!(left, [copy_id().to_string(), format!("{}.lock", copy_id())]);
        let _ = std::fs::remove_dir_all(&cache);
    }

    #[test]
    fn the_tools_run_isolated_and_the_shipped_copy_names_its_helper() {
        let python = Python { program: PathBuf::from("/usr/bin/python3"), version: (3, 14), zstd: "stdlib".into(), given: false };
        let cache = tmp("env");
        let root = unpack(&cache).unwrap();
        let shipped = CookTools { root: root.clone(), source: Source::Shipped, python: python.clone(), sound_helper: Some(root.join("bin/kiln-soundcook")) };
        // modelcook too: its recipe may hold sound steps, which it hands to soundcook
        for cmd in [shipped.command("modelcook").unwrap(), shipped.command("soundcook").unwrap(), shipped.script_command("tools/modelcook/modelcook.py")] {
            let args: Vec<_> = cmd.get_args().map(|a| a.to_string_lossy().into_owned()).collect();
            assert_eq!(args[0], "-I", "{args:?}");
            assert!(!args.iter().any(|a| a == "--native" || a == "--no-native"));
            let env: std::collections::HashMap<_, _> = cmd.get_envs().map(|(k, v)| (k.to_string_lossy().into_owned(), v.map(|v| v.to_os_string()))).collect();
            assert_eq!(env.get("KILN_SOUNDCOOK"), Some(&Some(root.join("bin/kiln-soundcook").into_os_string())));
            assert_eq!(env.get("KILN_SOUNDCOOK_NO_BUILD"), Some(&Some("1".into())));
        }
        // no helper in the copy: none is named, an inherited one is dropped, and still nothing may be built
        let bare = CookTools { sound_helper: None, ..shipped.clone() };
        let cmd = bare.command("soundcook").unwrap();
        let env: std::collections::HashMap<_, _> = cmd.get_envs().map(|(k, v)| (k.to_string_lossy().into_owned(), v.map(|v| v.to_os_string()))).collect();
        assert_eq!(env.get("KILN_SOUNDCOOK"), Some(&None));
        assert_eq!(env.get("KILN_SOUNDCOOK_NO_BUILD"), Some(&Some("1".into())));
        // a checkout decides for itself (a developer's helper is found or built there)
        let checkout = CookTools { source: Source::Checkout, sound_helper: None, ..shipped };
        let cmd = checkout.command("soundcook").unwrap();
        assert!(cmd.get_envs().all(|(k, _)| k != "KILN_SOUNDCOOK" && k != "KILN_SOUNDCOOK_NO_BUILD"));
        let _ = std::fs::remove_dir_all(&cache);
    }

    #[test]
    fn a_checkout_wins_only_when_it_has_the_tools() {
        let cache = tmp("checkout");
        let empty = tmp("checkout-empty");
        let l = Locate { repo: Some(empty.clone()), cache: Some(cache.clone()), path: std::env::var_os("PATH"), ..Locate::default() };
        match locate(&l) {
            Ok(t) => assert_eq!(t.source, Source::Shipped),
            Err(e) => assert_eq!(e.code, ErrorCode::NotFound, "only a missing Python may stop this: {}", e.message),
        }
        let repo = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../..");
        let l = Locate { repo: Some(repo), cache: None, path: std::env::var_os("PATH"), ..Locate::default() };
        match locate(&l) {
            Ok(t) => {
                assert_eq!(t.source, Source::Checkout);
                assert!(t.sound_helper.is_none(), "a checkout finds or builds its own helper");
            }
            Err(e) => assert_eq!(e.code, ErrorCode::NotFound, "{}", e.message),
        }
        let l = Locate { repo: None, cache: None, ..Locate::default() };
        assert_eq!(locate(&l).unwrap_err().code, ErrorCode::InvalidArgument);
        let _ = std::fs::remove_dir_all(&cache);
        let _ = std::fs::remove_dir_all(&empty);
    }

    #[test]
    fn a_python_is_judged_by_its_version_and_its_zstd() {
        assert_eq!(parse_probe("kiln-python 3 14 stdlib\n"), Some(((3, 14), Some("stdlib".into()))));
        assert_eq!(parse_probe("noise\nkiln-python 3 11 -\n"), Some(((3, 11), None)));
        assert_eq!(parse_probe("Python was not found; run without arguments to install from the Microsoft Store"), None);
        assert_eq!(parse_probe(""), None);
        let p = Path::new("/usr/bin/python3");
        assert!(unusable(p, (3, 9), &Some("libzstd".into())).unwrap().contains("need Python 3.10"));
        assert!(unusable(p, (3, 11), &None).unwrap().contains("libzstd"));
        assert_eq!(unusable(p, (3, 10), &Some("libzstd".into())), None);
        assert_eq!(unusable(p, (3, 14), &Some("stdlib".into())), None);
    }

    #[cfg(unix)]
    #[test]
    fn the_first_python_that_is_good_enough_is_taken_and_the_error_says_why_none_was() {
        use std::os::unix::fs::PermissionsExt;
        let cache = tmp("python");
        let root = unpack(&cache).unwrap();
        let bin = tmp("python-bin");
        let fake = |name: &str, answer: &str| {
            let p = bin.join(name);
            std::fs::write(&p, format!("#!/bin/sh\necho '{answer}'\n")).unwrap();
            std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o755)).unwrap();
            p
        };
        // too old on the path: the error names it and the version that is needed
        fake("python3", "kiln-python 3 8 stdlib");
        let l = Locate { path: Some(bin.clone().into_os_string()), ..Locate::default() };
        let e = find_python(&l, &root).unwrap_err();
        assert!(e.message.contains("Python 3.8") && e.message.contains("3.10"), "{}", e.message);
        // one the caller brings is asked first, and taken
        let own = fake("bundled-python", "kiln-python 3 14 stdlib");
        let l = Locate { python: Some(own.clone()), path: Some(bin.clone().into_os_string()), ..Locate::default() };
        let py = find_python(&l, &root).unwrap();
        assert_eq!((py.program, py.version, py.given, py.zstd.as_str()), (own, (3, 14), true, "stdlib"));
        // KILN_PYTHON that is not there is said, when nothing else works either
        let l = Locate { env_python: Some("/nonexistent/python".into()), path: Some(bin.clone().into_os_string()), ..Locate::default() };
        assert!(find_python(&l, &root).unwrap_err().message.contains("/nonexistent/python"));
        // no zstd: said in one sentence with what to install
        fake("python3", "kiln-python 3 12 -");
        let l = Locate { path: Some(bin.clone().into_os_string()), ..Locate::default() };
        assert!(find_python(&l, &root).unwrap_err().message.contains("zstd"));
        // nothing at all
        let l = Locate { path: Some(tmp("python-none").into_os_string()), ..Locate::default() };
        assert!(find_python(&l, &root).unwrap_err().message.contains("no python3 was found"));
        let _ = std::fs::remove_dir_all(&cache);
        let _ = std::fs::remove_dir_all(&bin);
    }

    /// A candidate that hangs (and what it started) is stopped at the probe's limit, and is no Python: it never holds
    /// Play for ever.
    #[cfg(unix)]
    #[test]
    fn a_python_that_hangs_is_stopped_at_the_probes_limit() {
        use std::os::unix::fs::PermissionsExt;
        let bin = tmp("python-hang");
        let p = bin.join("python3");
        std::fs::write(&p, "#!/bin/sh\nsleep 600 &\nsleep 600\n").unwrap();
        std::fs::set_permissions(&p, std::fs::Permissions::from_mode(0o755)).unwrap();
        let t = std::time::Instant::now();
        assert_eq!(probe_within(&p, &bin, std::time::Duration::from_secs(1)), None);
        assert!(t.elapsed() < std::time::Duration::from_secs(30), "{:?}", t.elapsed());
        std::fs::write(&p, "#!/bin/sh\necho 'kiln-python 3 14 stdlib'\n").unwrap();
        assert_eq!(probe_within(&p, &bin, std::time::Duration::from_secs(30)), Some(((3, 14), Some("stdlib".into()))));
        let _ = std::fs::remove_dir_all(&bin);
    }

    #[test]
    fn the_real_python_of_this_machine_runs_a_shipped_tool() {
        // skipped where there is no usable python3 (the release tooling's own test runs a whole cook)
        let cache = tmp("run");
        let l = Locate { cache: Some(cache.clone()), path: std::env::var_os("PATH"), ..Locate::default() };
        let Ok(t) = locate(&l) else {
            let _ = std::fs::remove_dir_all(&cache);
            return;
        };
        assert_eq!(t.source, Source::Shipped);
        for (tool, _, _) in TOOLS {
            let out = t.command(tool).unwrap().arg("--help").output().unwrap();
            assert!(out.status.success(), "{tool} --help: {}", String::from_utf8_lossy(&out.stderr));
        }
        assert!(t.script("nonsense").is_none() && t.command("nonsense").is_none());
        let _ = std::fs::remove_dir_all(&cache);
    }
}
